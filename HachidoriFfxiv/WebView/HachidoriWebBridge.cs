using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Media;
using HachidoriFfxiv.Stats;

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
    Task<ScreenshotMedia?> CaptureScreenshotAsync(CancellationToken ct);
    Task<JsonObject> SubmitAnkiAsync(JsonObject request, ScreenshotMedia? screenshot, CancellationToken ct);
    Task<string?> AttachPostMineMediaAsync(long noteId, string sentence, string? screenshotFilename, CancellationToken ct);
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
    private string? lastProjectedOptionsJson;
    private int lastProjectedRevision;

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
                    ApplyMediaCapabilities(preReq, getOptions());

                reply = await backend.SendRuntimeMessageAsync(target, type, forward, ct).ConfigureAwait(false);
                if (type == "hd_lookup")
                    backend.RecordLookup(reply);
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
        // Hachidori's sharing-protocol.js forwards single hd_anki_preflight requests (not batch);
        // fan out batch preflights into individual hd_anki_preflight calls for both live and offline modes.
        var requests = message["requests"] as JsonArray ?? [];
        var opts = getOptions();
        var tasks = requests.Select(async item =>
        {
            if (item is not JsonObject reqObj)
                return new JsonObject { ["state"] = "error", ["canAdd"] = false, ["error"] = "Invalid preflight request." };
            var reqCopy = (JsonObject)reqObj.DeepClone();
            ApplyMediaCapabilities(reqCopy, opts);
            var single = new JsonObject
            {
                ["target"] = "hachidori-anki",
                ["type"] = "hd_anki_preflight",
                ["request"] = reqCopy,
            };
            try
            {
                return await backend.SendRuntimeMessageAsync("hachidori-anki", "hd_anki_preflight", single, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new JsonObject { ["state"] = "error", ["canAdd"] = false, ["error"] = ex.Message };
            }
        }).ToArray();

        var replies = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new JsonObject
        {
            ["ok"] = true,
            ["replies"] = new JsonArray(replies.Select(r => (JsonNode?)r.DeepClone()).ToArray()),
        };
    }

    private async Task<JsonObject> HandleScreenshotAsync(CancellationToken ct)
    {
        var opts = getOptions();
        if (!opts.CaptureScreenshot || !backend.CanCaptureScreenshot)
            return new JsonObject { ["ok"] = false, ["error"] = "FFXIV screenshot capture is disabled." };

        var shot = await backend.CaptureScreenshotAsync(ct).ConfigureAwait(false);
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
        ApplyMediaCapabilities(copy, opts);

        ScreenshotMedia? screenshot = null;
        if (copy["screenshot"]?["token"]?.ToString() is { Length: > 0 } token)
            pendingScreenshots.TryRemove(token, out screenshot);

        if (screenshot is null && opts.CaptureScreenshot && backend.CanCaptureScreenshot && copy["screenshot"] is JsonObject)
            screenshot = await backend.CaptureScreenshotAsync(ct).ConfigureAwait(false);

        var reply = await backend.SubmitAnkiAsync(copy, screenshot, ct).ConfigureAwait(false);

        var state = reply["state"]?.ToString() ?? string.Empty;
        long noteId = 0;
        if (reply["noteId"] is JsonValue nv && nv.TryGetValue<long>(out var nid))
            noteId = nid;
        else if (reply["noteIds"] is JsonArray arr && arr.Count > 0 && arr[0] is JsonValue av && av.TryGetValue<long>(out var aid))
            noteId = aid;

        if ((state is "added" or "updated") && noteId > 0)
        {
            var sentence = copy["sentence"]?.ToString() ?? string.Empty;
            if (backend.CanAttachMedia)
            {
                try
                {
                    var mediaSummary = await backend.AttachPostMineMediaAsync(noteId, sentence, screenshot?.Filename, ct).ConfigureAwait(false);
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
