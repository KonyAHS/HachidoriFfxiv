using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.WebView;

/// <summary>Plugin settings projected into Hachidori Web's <c>chrome.storage.local</c> snapshot.</summary>
public sealed record WebBridgeOptions
{
    public float PopupWidth { get; init; } = 480f;
    public float PopupMaxHeight { get; init; } = 460f;
    public float PopupBackgroundAlpha { get; init; } = 0.97f;
    public float HideDelaySeconds { get; init; } = 0.25f;
    public int MaxPopupDepth { get; init; } = 4;
    public bool ScanOnlyJapanese { get; init; } = true;
    public bool UsePersonalDictionary { get; init; } = true;
    public bool ShowLookupCounts { get; init; } = true;
    public bool HonourDefinitionBlur { get; init; } = true;
    public bool AutoPlayAudio { get; init; } = false;
    public float AudioVolume { get; init; } = 0.8f;
    public bool FollowHachidoriOpacity { get; init; } = true;
    public bool ApplyHachidoriCustomCss { get; init; } = true;
    public string HachidoriThemeOverride { get; init; } = string.Empty;
    public string HachidoriVariantOverride { get; init; } = string.Empty;
    public string ExtraPopupCss { get; init; } = string.Empty;
    public string AnkiTemplateId { get; init; } = string.Empty;
    public bool CaptureScreenshot { get; init; } = true;
}

/// <summary>Backend boundary between <see cref="HachidoriWebBridge"/> and the live/offline Hachidori + FFXIV media services.</summary>
public interface IHachidoriWebBackend
{
    JsonObject Snapshot { get; }
    bool CanCaptureScreenshot { get; }
    bool CanAttachMedia { get; }
    bool NpcPicturesAvailable { get; }

    Task<JsonObject> SendRuntimeMessageAsync(string target, string type, JsonObject message, CancellationToken ct);
    Task<JsonObject[]> PreflightAnkiBatchAsync(IReadOnlyList<JsonObject> requests, CancellationToken ct)
    {
        var tasks = requests.Select(async reqObj =>
        {
            var single = new JsonObject
            {
                ["target"] = "hachidori-anki",
                ["type"] = "hd_anki_preflight",
                ["request"] = reqObj.DeepClone(),
            };
            try
            {
                return await SendRuntimeMessageAsync("hachidori-anki", "hd_anki_preflight", single, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new JsonObject { ["state"] = "error", ["canAdd"] = false, ["error"] = ex.Message };
            }
        }).ToArray();
        return Task.WhenAll(tasks);
    }
    Task<ScreenshotMedia?> CaptureScreenshotAsync(long? lineId, string? sentence, CancellationToken ct);
    Task<JsonObject> SubmitAnkiAsync(JsonObject request, ScreenshotMedia? screenshot, CancellationToken ct);
    Task<string?> AttachPostMineMediaAsync(long noteId, string sentence, string? screenshotFilename, long? lineId, CancellationToken ct);
    void RecordLookup(JsonObject lookupReply);
    void RecordMinedCard(long noteId, JsonObject miningRequest, string sentence, bool hasScreenshot);
}

/// <summary>
/// Routes Hachidori Web's <c>chrome.runtime.sendMessage</c> RPCs between the embedded
/// WebView2 frontend (<c>ffxiv-bridge.js</c>) and <see cref="IHachidoriWebBackend"/>.
/// Intercepts <c>hd_anki_screenshot</c> and <c>hd_anki_submit</c> so FFXIV's clean game
/// screenshots, voice clips, TTS, animations and AI translations attach seamlessly.
/// </summary>
public sealed class HachidoriWebBridge
{
    private readonly Func<WebBridgeOptions> getOptions;
    private readonly IHachidoriWebBackend backend;
    private readonly ConcurrentDictionary<string, ScreenshotMedia> pendingScreenshots = new(StringComparer.Ordinal);
    private readonly object projectionLock = new();
    private readonly object preflightGate = new();
    private readonly object miningContextLock = new();
    private readonly List<JsonArray> recentLookupResults = [];
    private const int MaxCachedLookups = 8;
    private (string Sentence, int CharIndex, long? LineId)? activeLookupContext;
    private CancellationTokenSource? activePreflightCts;
    private string? lastProjectedOptionsJson;
    private int lastProjectedRevision;

    /// <summary>Records the full FFXIV text box for the active root WebView lookup so Anki mining preserves the entire line.</summary>
    public void SetActiveLookupContext(string sentence, int charIndex, long? lineId = null)
    {
        lock (miningContextLock)
        {
            activeLookupContext = string.IsNullOrEmpty(sentence)
                ? null
                : (sentence, Math.Clamp(charIndex, 0, sentence.Length), lineId);
        }
    }

    /// <summary>Clears the active root WebView lookup context (e.g. when popups close).</summary>
    public void ClearActiveLookupContext()
    {
        lock (miningContextLock)
        {
            activeLookupContext = null;
        }
    }

    /// <summary>
    /// Joins single FFXIV line wraps inside a text box (matching <see cref="Rendering.Japanese.NormaliseGameText"/>),
    /// turns paragraph breaks into a single space, trims surrounding whitespace, and remaps
    /// <paramref name="charIndex"/> and <paramref name="tokenLength"/> to the normalized string.
    /// </summary>
    public static (string Sentence, int CharIndex, int TokenLength) NormalizeLookupContext(string sentence, int charIndex, int tokenLength = 1)
    {
        if (string.IsNullOrEmpty(sentence))
            return (string.Empty, 0, 1);

        var rawStart = Math.Clamp(charIndex, 0, sentence.Length);
        var rawEnd = Math.Clamp(rawStart + Math.Max(1, tokenLength), rawStart, sentence.Length);

        var sb = new System.Text.StringBuilder(sentence.Length);
        var map = new int[sentence.Length + 1];

        for (var i = 0; i < sentence.Length; i++)
        {
            map[i] = sb.Length;
            var c = sentence[i];
            if (c == '\u200B')
                continue;
            if (c is '\r' or '\n')
            {
                var j = i;
                var breaks = 0;
                while (j < sentence.Length && sentence[j] is '\r' or '\n')
                {
                    if (sentence[j] == '\r' && j + 1 < sentence.Length && sentence[j + 1] == '\n')
                        j += 2;
                    else
                        j++;
                    breaks++;
                }
                for (var k = i; k < j; k++)
                    map[k] = sb.Length;
                // Keep paragraph breaks (blank lines) as a space; drop single line wraps inside Japanese prose.
                if (breaks >= 2 && sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
                i = j - 1;
                continue;
            }
            sb.Append(c);
        }
        map[sentence.Length] = sb.Length;

        var joined = sb.ToString();
        var trimStart = 0;
        while (trimStart < joined.Length && char.IsWhiteSpace(joined[trimStart]))
            trimStart++;
        var trimEnd = joined.Length;
        while (trimEnd > trimStart && char.IsWhiteSpace(joined[trimEnd - 1]))
            trimEnd--;

        var normalized = joined[trimStart..trimEnd];
        var mappedStart = Math.Clamp(map[rawStart] - trimStart, 0, normalized.Length);
        var mappedEnd = Math.Clamp(map[rawEnd] - trimStart, mappedStart, normalized.Length);
        return (normalized, mappedStart, Math.Max(1, mappedEnd - mappedStart));
    }

    /// <summary>Cancels any in-flight popup preflight batch (e.g. when a new lookup opens or popups close).</summary>
    public void CancelPendingPreflights()
    {
        CancellationTokenSource? old;
        lock (preflightGate)
        {
            old = activePreflightCts;
            activePreflightCts = null;
        }
        if (old is not null)
        {
            try { old.Cancel(); } catch { }
            old.Dispose();
        }
    }

    public HachidoriWebBridge(Func<WebBridgeOptions> getOptions, IHachidoriWebBackend backend)
    {
        this.getOptions = getOptions;
        this.backend = backend;
    }

    public Func<bool>? NpcPicturesAvailable => () => backend.NpcPicturesAvailable;
    public float AudioVolume => getOptions().AudioVolume;
    public Action<string, string>? OnNpcCaptureRequested { get; set; }
    public Action<string>? OnOpenExternalUrl { get; set; }
    public Action<string>? OnOpenSettings { get; set; }

    /// <summary>
    /// Builds a cloned storage snapshot for <c>chrome.storage.local</c> with FFXIV overrides
    /// applied (theme/variant overrides, opacity, extra CSS, popup dimensions, selected Anki
    /// template). Bumps <c>options.revision</c> monotonically so Hachidori's <c>adoptOptions</c>
    /// applies every local setting change immediately without mutating the host's snapshot.
    /// </summary>
    public JsonObject BuildProjectedSnapshot(JsonObject? sourceSnapshot = null)
    {
        var opts = getOptions();
        var raw = sourceSnapshot ?? backend.Snapshot;
        var snapshot = (JsonObject)raw.DeepClone();
        var options = snapshot["options"] as JsonObject ?? new JsonObject();
        snapshot["options"] = options;

        var baseRev = options["revision"] is JsonValue rv && rv.TryGetValue<int>(out var r) ? r : 0;

        options["hoverEnabled"] = true;
        options["lookupMode"] = "hover";
        options["scanDelayMs"] = 0;
        options["popupHideDelayMs"] = (int)Math.Clamp(opts.HideDelaySeconds * 1000f, 0, 10_000);
        options["popupWidthPx"] = (int)Math.Clamp(opts.PopupWidth, 260, 1400);
        options["popupHeightPx"] = (int)Math.Clamp(opts.PopupMaxHeight, 200, 1200);
        options["popupNestingMaxDepth"] = Math.Clamp(opts.MaxPopupDepth, 1, 8);
        options["onlyScanJapaneseText"] = opts.ScanOnlyJapanese;
        options["audioAutoplay"] = opts.AutoPlayAudio;

        if (!opts.UsePersonalDictionary)
            options["personalDictionaryEnabled"] = false;
        if (!opts.ShowLookupCounts)
            options["showLookupCounts"] = false;
        if (!opts.HonourDefinitionBlur)
        {
            options["definitionBlurCountEnabled"] = false;
            options["definitionBlurAnkiMature"] = false;
            options["definitionBlurFrequencyEnabled"] = false;
        }

        if (!string.IsNullOrWhiteSpace(opts.HachidoriThemeOverride))
            options["popupTheme"] = opts.HachidoriThemeOverride.Trim();
        if (!string.IsNullOrWhiteSpace(opts.HachidoriVariantOverride))
            options["popupThemeVariant"] = opts.HachidoriVariantOverride.Trim();
        if (!opts.FollowHachidoriOpacity)
            options["popupOpacityPercent"] = (int)Math.Clamp(MathF.Round(opts.PopupBackgroundAlpha * 100f), 10, 100);

        var customCss = opts.ApplyHachidoriCustomCss
            ? (options["customPopupCss"]?.ToString() ?? string.Empty)
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(opts.ExtraPopupCss))
            customCss = string.IsNullOrEmpty(customCss) ? opts.ExtraPopupCss : $"{customCss}\n{opts.ExtraPopupCss}";
        options["customPopupCss"] = customCss;

        if (!string.IsNullOrWhiteSpace(opts.AnkiTemplateId)
            && options["anki"] is JsonObject anki
            && anki["templates"] is JsonArray templates)
        {
            var matchIndex = -1;
            for (var i = 0; i < templates.Count; i++)
            {
                if (templates[i]?["id"]?.ToString() == opts.AnkiTemplateId)
                {
                    matchIndex = i;
                    break;
                }
            }
            if (matchIndex > 0)
            {
                var chosen = templates[matchIndex]!.DeepClone();
                templates.RemoveAt(matchIndex);
                templates.Insert(0, chosen);
            }
        }

        // Only bump options.revision when the projected options payload actually changed.
        // Unconditional bumps on lookupStats updates caused Hachidori to re-adopt options and re-query the active popup.
        options["revision"] = 0;
        var canonicalOptions = options.ToJsonString();
        lock (projectionLock)
        {
            if (!string.Equals(canonicalOptions, lastProjectedOptionsJson, StringComparison.Ordinal))
            {
                lastProjectedOptionsJson = canonicalOptions;
                lastProjectedRevision = Math.Max(baseRev, lastProjectedRevision) + 1;
            }
            options["revision"] = lastProjectedRevision;
        }

        return snapshot;
    }

    /// <summary>Handles one <c>chrome.runtime.sendMessage</c> request from Hachidori's frontend.</summary>
    public async Task<JsonObject> HandleRpcAsync(JsonObject message, CancellationToken ct = default)
    {
        var type = message["type"]?.ToString() ?? string.Empty;
        var target = message["target"]?.ToString() ?? string.Empty;
        var requestId = message["requestId"]?.DeepClone();

        try
        {
            JsonObject reply;
            if (target == "hachidori-anki" && type == "hd_anki_screenshot")
            {
                reply = await HandleScreenshotAsync(ct).ConfigureAwait(false);
            }
            else if (target == "hachidori-anki" && type == "hd_anki_screenshot_discard")
            {
                var token = message["token"]?.ToString();
                if (!string.IsNullOrEmpty(token)) pendingScreenshots.TryRemove(token, out _);
                reply = new JsonObject { ["ok"] = true };
            }
            else if (target == "hachidori-anki" && type == "hd_anki_submit")
            {
                reply = await HandleAnkiSubmitAsync(message, ct).ConfigureAwait(false);
            }
            else if (target == "hachidori-anki" && type == "hd_anki_preflight_batch")
            {
                reply = await HandleAnkiPreflightBatchAsync(message, ct).ConfigureAwait(false);
            }
            else
            {
                var forward = (JsonObject)message.DeepClone();
                if (target == "hachidori-anki" && type == "hd_anki_preflight" && forward["request"] is JsonObject preReq)
                {
                    EnrichMiningRequest(preReq);
                    ApplyMediaCapabilities(preReq, getOptions());
                }

                reply = await backend.SendRuntimeMessageAsync(target, type, forward, ct).ConfigureAwait(false);
                if (type == "hd_lookup")
                {
                    CacheLookupResults(reply);
                    backend.RecordLookup(reply);
                }
            }

            return NormalizeReply(reply, type, requestId);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("webview-rpc", $"{type} failed: {ex.Message}");
            return new JsonObject
            {
                ["ok"] = false,
                ["type"] = $"{type}_result",
                ["requestId"] = requestId,
                ["error"] = ex.Message,
            };
        }
    }

    private async Task<JsonObject> HandleAnkiPreflightBatchAsync(JsonObject message, CancellationToken ct)
    {
        var requests = message["requests"] as JsonArray ?? [];
        var opts = getOptions();

        // Cancel any superseded preflight batch so an earlier hover never stalls the active popup.
        var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? previous;
        lock (preflightGate)
        {
            previous = activePreflightCts;
            activePreflightCts = batchCts;
        }
        if (previous is not null)
        {
            try { previous.Cancel(); } catch { }
            previous.Dispose();
        }

        try
        {
            var replies = new JsonObject[requests.Count];
            var validIndices = new List<int>(requests.Count);
            var validRequests = new List<JsonObject>(requests.Count);
            for (var i = 0; i < requests.Count; i++)
            {
                if (requests[i] is not JsonObject reqObj)
                {
                    replies[i] = new JsonObject { ["state"] = "error", ["canAdd"] = false, ["error"] = "Invalid preflight request." };
                    continue;
                }
                var reqCopy = (JsonObject)reqObj.DeepClone();
                EnrichMiningRequest(reqCopy);
                ApplyMediaCapabilities(reqCopy, opts);
                validIndices.Add(i);
                validRequests.Add(reqCopy);
            }

            if (validRequests.Count > 0)
            {
                var batchReplies = await backend.PreflightAnkiBatchAsync(validRequests, batchCts.Token).ConfigureAwait(false);
                for (var i = 0; i < validIndices.Count && i < batchReplies.Length; i++)
                    replies[validIndices[i]] = batchReplies[i];
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["replies"] = new JsonArray(replies.Select(r => (JsonNode?)(r?.DeepClone() ?? new JsonObject { ["state"] = "error", ["canAdd"] = false })).ToArray()),
            };
        }
        finally
        {
            lock (preflightGate)
            {
                if (activePreflightCts == batchCts)
                    activePreflightCts = null;
            }
            batchCts.Dispose();
        }
    }

    private (string Sentence, int CharIndex, long? LineId)? SnapshotLookupContext()
    {
        lock (miningContextLock)
            return activeLookupContext;
    }

    private async Task<JsonObject> HandleScreenshotAsync(CancellationToken ct)
    {
        var opts = getOptions();
        if (!opts.CaptureScreenshot || !backend.CanCaptureScreenshot)
            return new JsonObject { ["ok"] = false, ["error"] = "FFXIV screenshot capture is disabled." };

        var ctx = SnapshotLookupContext();
        var shot = await backend.CaptureScreenshotAsync(ctx?.LineId, ctx?.Sentence, ct).ConfigureAwait(false);
        if (shot is null)
            return new JsonObject { ["ok"] = false, ["error"] = "No game frame is available yet." };

        pendingScreenshots[shot.Token] = shot;
        return new JsonObject
        {
            ["ok"] = true,
            ["token"] = shot.Token,
            ["filename"] = shot.Filename,
        };
    }

    private async Task<JsonObject> HandleAnkiSubmitAsync(JsonObject message, CancellationToken ct)
    {
        var opts = getOptions();
        var request = message["request"] as JsonObject
            ?? throw new InvalidOperationException("Anki submission is missing its request payload.");
        var copy = (JsonObject)request.DeepClone();
        var ctx = SnapshotLookupContext();
        EnrichMiningRequest(copy);
        ApplyMediaCapabilities(copy, opts);

        var submitSentence = copy["sentence"]?.ToString() ?? string.Empty;
        var lineId = ctx is { } c && (string.IsNullOrEmpty(submitSentence) || string.Equals(c.Sentence, submitSentence, StringComparison.Ordinal))
            ? c.LineId
            : null;

        ScreenshotMedia? screenshot = null;
        if (copy["screenshot"]?["token"]?.ToString() is { Length: > 0 } token)
            pendingScreenshots.TryRemove(token, out screenshot);

        if (screenshot is null && opts.CaptureScreenshot && backend.CanCaptureScreenshot && copy["screenshot"] is JsonObject)
            screenshot = await backend.CaptureScreenshotAsync(lineId, submitSentence, ct).ConfigureAwait(false);

        var reply = await backend.SubmitAnkiAsync(copy, screenshot, ct).ConfigureAwait(false);

        var state = reply["state"]?.ToString() ?? string.Empty;
        long noteId = 0;
        if (reply["noteId"] is JsonValue nv && nv.TryGetValue<long>(out var nid))
            noteId = nid;
        else if (reply["noteIds"] is JsonArray arr && arr.Count > 0 && arr[0] is JsonValue av && av.TryGetValue<long>(out var aid))
            noteId = aid;

        if ((state is "added" or "updated") && noteId > 0)
        {
            var sentence = submitSentence;
            if (backend.CanAttachMedia)
            {
                try
                {
                    var mediaSummary = await backend.AttachPostMineMediaAsync(noteId, sentence, screenshot?.Filename, lineId, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(mediaSummary))
                    {
                        var warnings = reply["warnings"] as JsonArray ?? new JsonArray();
                        warnings.Add(mediaSummary);
                        reply["warnings"] = warnings;
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("webview-anki", $"Post-mine media attachment failed: {ex.Message}");
                    var warnings = reply["warnings"] as JsonArray ?? new JsonArray();
                    warnings.Add($"FFXIV media: {ex.Message}");
                    reply["warnings"] = warnings;
                }
            }

            try
            {
                backend.RecordMinedCard(noteId, copy, sentence, screenshot is not null);
            }
            catch { /* stats must never fail a mine */ }
        }

        return reply;
    }

    private void CacheLookupResults(JsonObject reply)
    {
        if (reply["results"] is not JsonArray { Count: > 0 } results)
            return;
        var clone = (JsonArray)results.DeepClone();
        lock (miningContextLock)
        {
            recentLookupResults.Add(clone);
            if (recentLookupResults.Count > MaxCachedLookups)
                recentLookupResults.RemoveAt(0);
        }
    }

    /// <summary>
    /// Restores full FFXIV text-box sentence context for root lookups (undoing Yomitan/Hachidori's
    /// terminator splitting) and restores full lookup glossaries when per-dictionary themes such as
    /// Crystal/JL project <c>term.glossaries</c> down to a single section, while keeping the clicked
    /// section's dictionary first for <c>{glossary-first}</c>.
    /// </summary>
    private void EnrichMiningRequest(JsonObject request)
    {
        RestoreFullSentence(request);
        RestoreFullGlossaries(request);
    }

    private void RestoreFullSentence(JsonObject request)
    {
        var ctx = SnapshotLookupContext();
        if (ctx is not { } active || active.Sentence.Length == 0)
            return;

        var full = active.Sentence;
        var charIndex = active.CharIndex;
        var reqSentence = request["sentence"]?.ToString() ?? string.Empty;
        if (full.Length <= reqSentence.Length)
            return;

        var matched = request["matched"]?.ToString() ?? string.Empty;
        if (matched.Length > 0 && (charIndex + matched.Length > full.Length
            || !full.AsSpan(charIndex, matched.Length).SequenceEqual(matched)))
        {
            return;
        }

        var reqOffset = request["matchOffset"] is JsonValue mv && mv.TryGetValue<int>(out var off) ? off : 0;
        if (reqSentence.Length > 0)
        {
            var expectedStart = charIndex - reqOffset;
            if (expectedStart < 0 || expectedStart + reqSentence.Length > full.Length)
                return;
            if (!full.AsSpan(expectedStart, reqSentence.Length).SequenceEqual(reqSentence))
                return;
        }

        request["sentence"] = full;
        request["matchOffset"] = charIndex;
    }

    private void RestoreFullGlossaries(JsonObject request)
    {
        if (request["term"] is not JsonObject term
            || term["glossaries"] is not JsonArray currentGlossaries
            || currentGlossaries.Count == 0)
        {
            return;
        }

        // Leave native kanji-entry mining untouched.
        foreach (var g in currentGlossaries)
        {
            if (g is JsonObject go && go["kanji"] is JsonObject)
                return;
        }

        var expr = term["expression"]?.ToString() ?? string.Empty;
        var reading = term["reading"]?.ToString() ?? string.Empty;
        if (expr.Length == 0)
            return;

        var currentDicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in currentGlossaries)
        {
            var dict = g?["dictionary"]?.ToString();
            if (!string.IsNullOrEmpty(dict))
                currentDicts.Add(dict);
        }
        if (currentDicts.Count == 0)
            return;

        JsonArray? matchedFullGlossaries = null;
        lock (miningContextLock)
        {
            for (var i = recentLookupResults.Count - 1; i >= 0 && matchedFullGlossaries is null; i--)
            {
                foreach (var item in recentLookupResults[i])
                {
                    if (item?["term"] is not JsonObject candTerm) continue;
                    if (!string.Equals(candTerm["expression"]?.ToString(), expr, StringComparison.Ordinal)) continue;
                    if (!string.Equals(candTerm["reading"]?.ToString() ?? string.Empty, reading, StringComparison.Ordinal)) continue;
                    if (candTerm["glossaries"] is not JsonArray candGlossaries || candGlossaries.Count <= currentGlossaries.Count)
                        continue;

                    var candDicts = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var cg in candGlossaries)
                    {
                        var dict = cg?["dictionary"]?.ToString();
                        if (!string.IsNullOrEmpty(dict))
                            candDicts.Add(dict);
                    }
                    if (candDicts.Count > currentDicts.Count && currentDicts.IsSubsetOf(candDicts))
                    {
                        matchedFullGlossaries = candGlossaries;
                        break;
                    }
                }
            }
        }

        if (matchedFullGlossaries is null)
            return;

        // Keep the clicked dictionary section first so {glossary-first} / MainDefinition honors the user's choice,
        // and append the remaining dictionaries so {glossary} / {glossary-brief} / {single-glossary-...} have all dictionaries.
        var merged = new JsonArray();
        foreach (var g in currentGlossaries)
            merged.Add(g?.DeepClone());
        foreach (var g in matchedFullGlossaries)
        {
            var dict = g?["dictionary"]?.ToString();
            if (string.IsNullOrEmpty(dict) || !currentDicts.Contains(dict))
                merged.Add(g?.DeepClone());
        }
        term["glossaries"] = merged;
    }

    private void ApplyMediaCapabilities(JsonObject request, WebBridgeOptions opts)
    {
        var unavailable = new HashSet<string>(
            (request["captureUnavailable"] as JsonArray ?? []).Select(n => n?.ToString() ?? string.Empty).Where(s => s.Length > 0),
            StringComparer.Ordinal);
        unavailable.Add("sentence-audio");
        unavailable.Add("gif");
        if (!opts.CaptureScreenshot || !backend.CanCaptureScreenshot)
            unavailable.Add("screenshot");
        else
            unavailable.Remove("screenshot");
        request["captureUnavailable"] = new JsonArray(unavailable.Select(u => (JsonNode?)u).ToArray());
    }

    private static JsonObject NormalizeReply(JsonObject reply, string type, JsonNode? requestId)
    {
        var normalized = (JsonObject)reply.DeepClone();
        normalized["type"] = $"{type}_result";
        if (requestId is not null)
            normalized["requestId"] = requestId.DeepClone();
        if (normalized["ok"] is null)
            normalized["ok"] = string.IsNullOrEmpty(normalized["error"]?.ToString());
        return normalized;
    }
}
