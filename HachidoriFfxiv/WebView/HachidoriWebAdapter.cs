using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Media;
using HachidoriFfxiv.Stats;

namespace HachidoriFfxiv.WebView;

/// <summary>
/// Connects <see cref="HachidoriWebBridge"/> to the live/offline <see cref="HachidoriService"/>,
/// <see cref="MediaMiner"/>, <see cref="StatsTracker"/> and <see cref="Configuration"/>.
/// </summary>
public sealed class HachidoriWebAdapter : IHachidoriWebBackend
{
    private readonly Configuration config;
    private readonly HachidoriService service;
    private readonly Func<MediaMiner?> getMedia;
    private readonly Func<StatsTracker?> getStats;
    private readonly Func<bool> npcAvailable;

    public HachidoriWebAdapter(
        Configuration config,
        HachidoriService service,
        Func<MediaMiner?> getMedia,
        Func<StatsTracker?> getStats,
        Func<bool> npcAvailable)
    {
        this.config = config;
        this.service = service;
        this.getMedia = getMedia;
        this.getStats = getStats;
        this.npcAvailable = npcAvailable;
    }

    public static WebBridgeOptions OptionsFrom(Configuration c) => new()
    {
        PopupWidth = c.PopupWidth,
        PopupMaxHeight = c.PopupMaxHeight,
        PopupBackgroundAlpha = c.PopupBackgroundAlpha,
        HideDelaySeconds = c.HideDelaySeconds,
        MaxPopupDepth = c.MaxPopupDepth,
        ScanOnlyJapanese = c.ScanOnlyJapanese,
        UsePersonalDictionary = c.UsePersonalDictionary,
        ShowLookupCounts = c.ShowLookupCounts,
        HonourDefinitionBlur = c.HonourDefinitionBlur,
        AutoPlayAudio = c.AutoPlayAudio,
        AudioVolume = c.AudioVolume,
        FollowHachidoriOpacity = c.FollowHachidoriOpacity,
        ApplyHachidoriCustomCss = c.ApplyHachidoriCustomCss,
        HachidoriThemeOverride = c.HachidoriThemeOverride,
        HachidoriVariantOverride = c.HachidoriVariantOverride,
        ExtraPopupCss = c.ExtraPopupCss,
        AnkiTemplateId = c.AnkiTemplateId,
        CaptureScreenshot = c.CaptureScreenshot,
    };

    public JsonObject Snapshot => service.Snapshot;
    public bool CanCaptureScreenshot => getMedia() is not null;
    public bool CanAttachMedia => getMedia() is { AttachesMedia: true };
    public bool NpcPicturesAvailable => npcAvailable();

    public Task<ScreenshotMedia?> CaptureScreenshotAsync(CancellationToken ct) =>
        getMedia() is { } media ? media.ScreenshotAsync() : Task.FromResult<ScreenshotMedia?>(null);

    public async Task<string?> AttachPostMineMediaAsync(long noteId, string sentence, string? screenshotFilename, CancellationToken ct) =>
        getMedia() is { AttachesMedia: true } media
            ? await media.AttachAsync(noteId, sentence, screenshotFilename).ConfigureAwait(false)
            : null;

    public void RecordLookup(JsonObject lookupReply)
    {
        if (getStats() is not { } stats) return;
        try
        {
            if (lookupReply["results"] is JsonArray { Count: > 0 } results && results[0] is JsonObject first)
                stats.RecordLookup(LookupResult.Parse((JsonObject)first.DeepClone()));
        }
        catch { /* stats must never break lookups */ }
    }

    public void RecordMinedCard(long noteId, JsonObject miningRequest, string sentence, bool hasScreenshot)
    {
        var parsed = LookupResult.Parse((JsonObject)miningRequest.DeepClone());
        var expr = parsed.Term.Expression.Length > 0 ? parsed.Term.Expression : parsed.Matched;
        Plugin.NotifyAnkiCardCreated(config, expr, parsed.Term.Reading);
        if (getStats() is not { } stats) return;
        var media = getMedia();
        var hasAudio = media is not null && (config.RecordVoiceLines || media.Tts.Enabled);
        var hasAnimation = media is not null && config.CaptureGif;
        var hasTranslation = media is not null && config.TranslationEnabled;
        stats.RecordMinedCard(noteId, parsed, sentence, string.Empty,
            hasScreenshot, hasAudio, hasAnimation, hasTranslation);
    }

    public async Task<JsonObject> SubmitAnkiAsync(JsonObject request, ScreenshotMedia? screenshot, CancellationToken ct)
    {
        if (service.UsingOffline)
        {
            var decision = await service.AnkiSubmitAsync(request, screenshot).ConfigureAwait(false);
            return DecisionToJson(decision);
        }

        var envelope = HachidoriPayloads.Submit(request, screenshot);
        var raw = await service.Link.RequestAsync("hachidori-anki", "hd_anki_submit", envelope, ct).ConfigureAwait(false);
        return raw as JsonObject ?? new JsonObject { ["ok"] = true };
    }

    public async Task<JsonObject[]> PreflightAnkiBatchAsync(IReadOnlyList<JsonObject> requests, CancellationToken ct)
    {
        if (requests.Count == 0)
            return [];

        var prepared = new List<JsonObject>(requests.Count);
        foreach (var req in requests)
        {
            var copy = (JsonObject)req.DeepClone();
            if (!string.IsNullOrWhiteSpace(config.AnkiTemplateId) && string.IsNullOrEmpty(copy["templateId"]?.ToString()))
                copy["templateId"] = config.AnkiTemplateId;
            prepared.Add(copy);
        }

        var decisions = await service.AnkiPreflightManyAsync(prepared, ct).ConfigureAwait(false);
        return decisions.Select(DecisionToJson).ToArray();
    }

    public Task<JsonObject> SendRuntimeMessageAsync(string target, string type, JsonObject message, CancellationToken ct) =>
        service.UsingOffline
            ? HandleOfflineAsync(target, type, message, ct)
            : HandleLiveAsync(target, type, message, ct);

    private async Task<JsonObject> HandleLiveAsync(string target, string type, JsonObject message, CancellationToken ct)
    {
        var payload = (JsonObject)message.DeepClone();
        payload.Remove("target");
        payload.Remove("type");
        payload.Remove("requestId");

        if (target == "hachidori-anki")
        {
            if (!string.IsNullOrWhiteSpace(config.AnkiTemplateId))
            {
                if (type == "hd_anki_status" && string.IsNullOrEmpty(payload["templateId"]?.ToString()))
                    payload["templateId"] = config.AnkiTemplateId;
                if (payload["request"] is JsonObject req && string.IsNullOrEmpty(req["templateId"]?.ToString()))
                    req["templateId"] = config.AnkiTemplateId;
            }

            // Hachidori's hd_anki_view only returns a cached duplicate when duplicateBehavior == "prevent".
            // Short-circuiting other modes avoids N sequential WebSocket round trips on every popup open.
            if (type == "hd_anki_view")
            {
                var tid = payload["request"]?["templateId"]?.ToString();
                var behavior = service.TemplateDuplicateBehavior(tid);
                if (behavior.Length > 0 && !string.Equals(behavior, "prevent", StringComparison.Ordinal))
                {
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["state"] = "unknown",
                        ["canAdd"] = false,
                        ["noteIds"] = new JsonArray(),
                        ["cached"] = false,
                    };
                }
            }

            if (type == "hd_anki_preflight" && payload["request"] is JsonObject singleReq)
            {
                var decision = await service.AnkiPreflightAsync(singleReq, ct).ConfigureAwait(false);
                return DecisionToJson(decision);
            }
        }

        var raw = await service.Link.RequestAsync(target, type, payload, ct).ConfigureAwait(false);
        return raw as JsonObject ?? new JsonObject { ["ok"] = true };
    }

    private async Task<JsonObject> HandleOfflineAsync(string target, string type, JsonObject message, CancellationToken ct)
    {
        if (target == "hoshidicts-offscreen")
        {
            return type switch
            {
                "hd_lookup" => await Task.Run(() => service.Offline!.Loaded().Lookup(message), ct).ConfigureAwait(false),
                "hd_kanji" => await Task.Run(() => service.Offline!.Loaded().Kanji(message), ct).ConfigureAwait(false),
                "hd_segment" => await Task.Run(() => service.Offline!.Loaded().Segment(message), ct).ConfigureAwait(false),
                "hd_styles" => await Task.Run(() =>
                {
                    var loaded = service.Offline!.Loaded();
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["generation"] = loaded.Generation,
                        ["styles"] = new JsonArray(loaded.Styles().Select(s => (JsonNode)new JsonObject
                        {
                            ["dictionary"] = s.Dictionary,
                            ["styles"] = s.Styles,
                        }).ToArray()),
                    };
                }, ct).ConfigureAwait(false),
                "hd_media" => await Task.Run(() =>
                {
                    var dict = message["dictionary"]?.ToString() ?? string.Empty;
                    var path = message["path"]?.ToString() ?? string.Empty;
                    var bytes = service.Offline!.Loaded().Media(dict, path);
                    if (bytes is null || bytes.Length == 0)
                        return new JsonObject { ["ok"] = false, ["error"] = "Media not found." };
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["dataUrl"] = $"data:{GuessMimeType(path)};base64,{Convert.ToBase64String(bytes)}",
                    };
                }, ct).ConfigureAwait(false),
                "hd_status" => await Task.Run(() =>
                {
                    var loaded = service.Offline!.Loaded();
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["generation"] = loaded.Generation,
                        ["dictionaryCount"] = loaded.DictionaryCount,
                    };
                }, ct).ConfigureAwait(false),
                _ => new JsonObject { ["ok"] = false, ["error"] = $"{type} requires a live Hachidori connection." },
            };
        }

        if (target == "hoshidicts-worker")
        {
            if (type is "hd_lookup_stats_read" or "hd_lookup_stats_record")
            {
                var term = message["term"]?.ToString() ?? string.Empty;
                var reading = message["reading"]?.ToString() ?? string.Empty;
                var count = await service.LookupStatsAsync(term, reading, type == "hd_lookup_stats_record").ConfigureAwait(false);
                return new JsonObject
                {
                    ["ok"] = true,
                    ["descriptor"] = new JsonObject { ["generation"] = null, ["revision"] = 0 },
                    ["statistics"] = count is { } c
                        ? new JsonObject { ["term"] = term, ["reading"] = reading, ["lookupCount"] = c }
                        : null,
                };
            }
            return new JsonObject { ["ok"] = false, ["error"] = $"{type} requires a live Hachidori connection." };
        }

        if (target == "hachidori-anki")
        {
            switch (type)
            {
                case "hd_anki_status":
                {
                    var status = await service.AnkiStatusAsync().ConfigureAwait(false);
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["available"] = status.Available,
                        ["configKey"] = status.ConfigKey,
                        ["error"] = status.Error,
                    };
                }
                case "hd_anki_preflight":
                {
                    var req = message["request"] as JsonObject ?? new JsonObject();
                    return DecisionToJson(await service.AnkiPreflightAsync(req).ConfigureAwait(false));
                }
                case "hd_anki_view":
                    return new JsonObject { ["ok"] = true, ["state"] = "unknown", ["noteIds"] = new JsonArray() };
                case "hd_anki_maturity":
                    return new JsonObject { ["ok"] = true, ["mature"] = false };
                case "hd_anki_word_status":
                    return new JsonObject { ["ok"] = true, ["statuses"] = new JsonObject() };
                case "hd_anki_browse":
                {
                    var req = message["request"] as JsonObject;
                    var ids = (req?["noteIds"] as JsonArray ?? [])
                        .Select(n => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0)
                        .Where(l => l > 0)
                        .ToArray();
                    await service.AnkiBrowseAsync(ids, req?["expression"]?.ToString() ?? string.Empty, req?["configKey"]?.ToString()).ConfigureAwait(false);
                    return new JsonObject { ["ok"] = true, ["opened"] = true };
                }
                default:
                    return new JsonObject { ["ok"] = false, ["error"] = $"{type} is unavailable offline." };
            }
        }

        return new JsonObject { ["ok"] = false, ["error"] = $"Unsupported offline request: {target}/{type}" };
    }

    private static JsonObject DecisionToJson(AnkiDecision decision)
    {
        var obj = new JsonObject
        {
            ["ok"] = true,
            ["state"] = decision.State,
            ["canAdd"] = decision.CanAdd,
            ["screenshot"] = decision.Screenshot,
            ["noteIds"] = new JsonArray(decision.NoteIds.Select(id => (JsonNode?)id).ToArray()),
            ["warnings"] = new JsonArray(decision.Warnings.Select(w => (JsonNode?)w).ToArray()),
        };
        if (decision.NoteIds.Length > 0)
            obj["noteId"] = decision.NoteIds[0];
        if (!string.IsNullOrEmpty(decision.Error))
            obj["error"] = decision.Error;
        if (decision.ClientSpeech is not null)
            obj["clientSpeech"] = decision.ClientSpeech.DeepClone();
        return obj;
    }

    private static string GuessMimeType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".avif" => "image/avif",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };
}
