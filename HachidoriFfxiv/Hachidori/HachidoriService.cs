using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HachidoriFfxiv.Offline;

namespace HachidoriFfxiv.Hachidori;

/// <summary>Everything the plugin asks Hachidori for, on top of <see cref="HachidoriLink"/>.</summary>
public sealed class HachidoriService : IDisposable, INpcPictureHost
{
    private const string EngineTarget = LiveBackend.EngineTarget;
    private const string AnkiTarget = "hachidori-anki";
    private const int CacheLimit = 512;

    private readonly Configuration config;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ConcurrentDictionary<string, Task<LookupReply>> cache = new();
    private readonly ConcurrentQueue<string> cacheOrder = new();
    private readonly ConcurrentDictionary<string, Task<byte[]?>> audioCache = new();
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<WordSpan>>> segmentCache = new();
    private readonly LiveBackend live;
    private readonly LocalBackend? local;
    private readonly Anki.OfflineAnkiMiner? offlineAnki;

    /// <param name="offlineRoot">Where offline mode keeps its copy; null leaves offline mode out.</param>
    /// <param name="libraryDirectory">The folder holding hdw_native.dll.</param>
    public HachidoriService(Configuration config, string? offlineRoot = null, string? libraryDirectory = null)
    {
        this.config = config;
        Link = new HachidoriLink(() => new UriBuilder("ws", config.RelayHost, config.RelayPort, "/link").Uri,
            ex => Plugin.Log.Warning(ex, "Dropped a sharing frame"));
        Link.SnapshotChanged += ClearCache;
        Link.ConnectionChanged += OnConnectionChanged;
        live = new LiveBackend(Link, http, () => $"http://{config.RelayHost}:{config.ApiPort}/");
        if (offlineRoot is not null && libraryDirectory is not null)
        {
            Offline = new OfflineMode(config, Link, offlineRoot, libraryDirectory);
            Offline.Changed += ClearCache;
            local = new LocalBackend(Offline, http, () => Options);
            offlineAnki = new Anki.OfflineAnkiMiner(() => Snapshot, Offline.Loaded, http);
        }
    }

    public HachidoriLink Link { get; }

    public OfflineMode? Offline { get; }

    /// <summary>
    /// Live Hachidori whenever it is linked; the offline copy only while it is
    /// not and offline mode is set up; otherwise live, which reports why it is unreachable.
    /// </summary>
    public IDictionaryBackend Backend => !Link.Connected && local is not null && Offline!.Ready ? local : live;

    public bool UsingOffline => !Backend.IsLive;

    /// <summary>Hachidori's settings: the host's, or the copy offline mode saved when it is not linked.</summary>
    public JsonObject Snapshot => Link.Connected || Link.Snapshot.Count > 0 || Offline?.Store.Snapshot is not { } saved
        ? Link.Snapshot
        : saved;

    public JsonObject Options => Snapshot["options"] as JsonObject ?? new JsonObject();

    public IEnumerable<JsonObject> Dictionaries =>
        (Snapshot["dictionaryState"]?["dictionaries"] as JsonArray)?.OfType<JsonObject>() ?? [];

    private void OnConnectionChanged()
    {
        ClearCache();
        if (Link.Connected && local is not null)
            _ = local.FlushLookupStatsAsync(live, CancellationToken.None);
    }

    public bool OptionBool(string name, bool fallback) =>
        Options[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public double OptionNumber(string name, double fallback) =>
        Options[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;

    public string OptionString(string name, string fallback) =>
        Options[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    public void ClearCache()
    {
        cache.Clear();
        segmentCache.Clear();
        audioCache.Clear();
        while (cacheOrder.TryDequeue(out _))
        {
        }
    }

    // ---------------------------------------------------------------- lookups

    public Task<IReadOnlyList<WordSpan>> SegmentAsync(string text)
    {
        var key = $"{config.ScanLength}|{config.UsePersonalDictionary}|{text}";
        if (segmentCache.TryGetValue(key, out var task)) return task;
        if (segmentCache.Count >= 128) segmentCache.Clear();
        return segmentCache[key] = SegmentUncachedAsync(text);
    }

    private async Task<IReadOnlyList<WordSpan>> SegmentUncachedAsync(string text)
    {
        var backend = Backend;
        var personal = config.UsePersonalDictionary && OptionBool("personalDictionaryEnabled", true);
        if (text.IndexOfAny(['\r', '\n']) < 0)
        {
            var direct = await backend.SegmentAsync(HachidoriPayloads.Segment(text, config.ScanLength, personal), text).ConfigureAwait(false);
            DiagnosticLog.Write("segment", $"characters={text.Length}, words={direct.Count}, live={backend.IsLive}");
            return direct;
        }

        // Strip single line wraps (e.g. "思\n案") so words split across a UI line break tokenize
        // whole, while keeping paragraph breaks (\n\n) as boundaries, then map spans back to source.
        var sb = new System.Text.StringBuilder(text.Length);
        var toSource = new int[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n')
            {
                var next = i + 1;
                if (c == '\r' && next < text.Length && text[next] == '\n') next++;
                if (next < text.Length && text[next] is '\r' or '\n')
                {
                    toSource[sb.Length] = i;
                    sb.Append('\n');
                }
                i = next - 1;
                continue;
            }
            toSource[sb.Length] = i;
            sb.Append(c);
        }

        var collapsed = sb.ToString();
        var raw = await backend.SegmentAsync(HachidoriPayloads.Segment(collapsed, config.ScanLength, personal), collapsed).ConfigureAwait(false);
        var mapped = new List<WordSpan>(raw.Count);
        foreach (var span in raw)
        {
            if (span.Start < 0 || span.End > collapsed.Length || span.Length < 1) continue;
            var srcStart = toSource[span.Start];
            var srcEnd = toSource[span.End - 1] + 1;
            mapped.Add(new WordSpan(srcStart, srcEnd - srcStart));
        }
        DiagnosticLog.Write("segment", $"characters={text.Length}, words={mapped.Count}, live={backend.IsLive}");
        return mapped;
    }

    public async Task<byte[]?> GetImageAsync(string dictionary, string path, long generation, CancellationToken ct)
    {
        var backend = Backend;
        var bytes = await backend.MediaAsync(dictionary, path, generation, ct).ConfigureAwait(false);
        DiagnosticLog.Write("image", $"generation={generation}, bytes={bytes?.Length ?? 0}, live={backend.IsLive}");
        return bytes;
    }

    // ---------------------------------------------------------------- NPC pictures

    IReadOnlyList<HostDictionary> INpcPictureHost.Dictionaries => Dictionaries
        .Select(d => new HostDictionary(Text(d["id"]) ?? "", Text(d["title"]) ?? "", Text(d["revision"])))
        .Where(d => d.Title.Length > 0).ToList();

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    async Task<long> INpcPictureHost.GenerationAsync(CancellationToken ct)
    {
        var reply = await Link.RequestAsync(EngineTarget, "hd_status", null, ct).ConfigureAwait(false);
        return reply?["generation"] is JsonValue g && g.TryGetValue<long>(out var generation)
            ? generation : throw new HachidoriException("The host did not report its dictionary generation.");
    }

    Task<byte[]?> INpcPictureHost.MediaAsync(string dictionary, string path, long generation, CancellationToken ct) =>
        GetImageAsync(dictionary, path, generation, ct);

    Task<JsonNode?> INpcPictureHost.ImportAsync(string type, JsonObject payload, CancellationToken ct) =>
        Link.RequestAsync(NpcImageDictionary.Target, type, payload, ct);

    async Task INpcPictureHost.RemoveAsync(HostDictionary dictionary, CancellationToken ct)
    {
        var payload = new JsonObject { ["title"] = dictionary.Title };
        if (dictionary.Id.Length > 0) payload["id"] = dictionary.Id;
        await Link.RequestAsync(EngineTarget, "hd_remove", payload, ct).ConfigureAwait(false);
    }

    public Task<LookupReply> LookupAsync(string text)
    {
        var key = $"t\u0001{config.ScanLength}\u0001{config.MaxResults}\u0001{config.UsePersonalDictionary}\u0001{text}";
        return Cached(key, () => LookupUncachedAsync(text));
    }

    public Task<LookupReply> KanjiAsync(string character) =>
        Cached($"k\u0001{character}", () => KanjiUncachedAsync(character));

    private Task<LookupReply> Cached(string key, Func<Task<LookupReply>> factory)
    {
        if (cache.TryGetValue(key, out var existing) && !existing.IsFaulted && !existing.IsCanceled)
            return existing;
        var task = factory();
        cache[key] = task;
        cacheOrder.Enqueue(key);
        while (cacheOrder.Count > CacheLimit && cacheOrder.TryDequeue(out var old))
            cache.TryRemove(old, out _);
        return task;
    }

    private Task<LookupReply> LookupUncachedAsync(string text) =>
        Backend.LookupAsync(HachidoriPayloads.Lookup(text, config.MaxResults, config.ScanLength,
            HachidoriPayloads.LookupOptions(config.UsePersonalDictionary && OptionBool("personalDictionaryEnabled", true), Options)));

    private Task<LookupReply> KanjiUncachedAsync(string character) => Backend.KanjiAsync(character);

    // ------------------------------------------------------- lookup statistics

    /// <summary>Reads (or records) Hachidori's lookup count for a headword. Null when counts are off or unavailable.</summary>
    public Task<int?> LookupStatsAsync(string term, string reading, bool record) =>
        Backend.LookupStatsAsync(term, reading, record);

    // -------------------------------------------------------------------- Anki

    public sealed record AnkiStatus(bool Available, string? ConfigKey, string? Error);

    public async Task<AnkiStatus> AnkiStatusAsync()
    {
        // Offline, the plugin renders the note from the saved Template and writes it through AnkiConnect.
        if (UsingOffline && offlineAnki is not null)
            return await offlineAnki.StatusAsync(NullIfBlank(config.AnkiTemplateId)).ConfigureAwait(false);
        var payload = new JsonObject();
        if (!string.IsNullOrWhiteSpace(config.AnkiTemplateId))
            payload["templateId"] = config.AnkiTemplateId;
        var reply = await Link.RequestAsync(AnkiTarget, "hd_anki_status", payload).ConfigureAwait(false);
        var available = reply?["available"] is JsonValue a && a.TryGetValue<bool>(out var b) && b;
        return new AnkiStatus(available, reply?["configKey"]?.ToString(), reply?["error"]?.ToString());
    }

    /// <summary>The mining request Hachidori's popup sends for a result (content.js getRequest).</summary>
    public JsonObject BuildMiningRequest(LookupReply reply, LookupResult result, ScanContext context, string? configKey)
    {
        var dictionaries = Dictionaries.ToList();
        var frequencyModes = dictionaries.ToDictionary(d => LookupResult.Str(d["title"]), d => d["frequencyMode"]?.DeepClone());
        var request = (JsonObject)result.Raw.DeepClone();
        if (request["term"] is JsonObject term && term["frequencies"] is JsonArray frequencies)
        {
            foreach (var group in frequencies.OfType<JsonObject>())
            {
                if (frequencyModes.TryGetValue(LookupResult.Str(group["dictionary"]), out var mode) && mode is not null)
                    group["frequencyMode"] = mode.DeepClone();
            }
        }

        var matched = result.Matched.Length > 0 ? result.Matched : result.Term.Expression;
        request["generation"] = reply.Generation;
        request["sentence"] = context.Sentence;
        request["matchOffset"] = context.MatchOffset;
        request["matched"] = matched;
        request["searchQuery"] = reply.Query;
        request["popupSelectionText"] = string.Empty;
        request["documentTitle"] = context.DocumentTitle;
        request["pageUrl"] = string.Empty;
        request["dictionaryAliases"] = new JsonObject(dictionaries
            .Where(d => LookupResult.Str(d["displayName"]).Length > 0)
            .GroupBy(d => LookupResult.Str(d["title"]))
            .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)LookupResult.Str(g.First()["displayName"]))));
        request["dictionaryIds"] = new JsonObject(dictionaries
            .GroupBy(d => LookupResult.Str(d["title"]))
            .Select(g => KeyValuePair.Create(g.Key, g.First()["id"]?.DeepClone())));
        request["frequencyDictionaries"] = new JsonArray(dictionaries
            .Where(d => d["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var en) || en)
            .Where(d => d["frequencyCount"] is JsonValue c && c.TryGetValue<double>(out var n) && n > 0)
            .Select(d => (JsonNode?)LookupResult.Str(d["title"])).ToArray());
        // The plugin sends its own game screenshot when it can (see MediaMiner); sentence audio and the
        // GIF are attached through AnkiConnect afterwards, so Hachidori leaves those markers empty
        // ({gif} falls back to the screenshot until the GIF replaces it).
        request["captureUnavailable"] = config.CaptureScreenshot
            ? new JsonArray("sentence-audio", "gif")
            : new JsonArray("screenshot", "sentence-audio", "gif");
        if (configKey is not null)
            request["configKey"] = configKey;
        if (!string.IsNullOrWhiteSpace(config.AnkiTemplateId))
            request["templateId"] = config.AnkiTemplateId;
        return request;
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public async Task<AnkiDecision> AnkiPreflightAsync(JsonObject request)
    {
        if (UsingOffline && offlineAnki is not null)
            return await offlineAnki.PreflightAsync(request).ConfigureAwait(false);
        var reply = await Link.RequestAsync(AnkiTarget, "hd_anki_preflight", new JsonObject { ["request"] = request.DeepClone() })
            .ConfigureAwait(false);
        return ParseDecision(reply);
    }

    public async Task<AnkiDecision> AnkiSubmitAsync(JsonObject request, Media.ScreenshotMedia? screenshot = null)
    {
        if (UsingOffline && offlineAnki is not null)
            return await offlineAnki.SubmitAsync(request, screenshot).ConfigureAwait(false);
        DiagnosticLog.Write("anki", $"Submitting note with explicit client-media envelope (screenshot={screenshot is not null}).");
        var reply = await Link.RequestAsync(AnkiTarget, "hd_anki_submit", HachidoriPayloads.Submit(request, screenshot))
            .ConfigureAwait(false);
        return ParseDecision(reply);
    }

    public async Task AnkiBrowseAsync(long[] noteIds, string expression, string? configKey)
    {
        if (UsingOffline && offlineAnki is not null)
        {
            await offlineAnki.BrowseAsync(noteIds, expression, NullIfBlank(config.AnkiTemplateId)).ConfigureAwait(false);
            return;
        }
        var request = new JsonObject
        {
            ["noteIds"] = new JsonArray(noteIds.Select(id => (JsonNode?)id).ToArray()),
            ["expression"] = expression,
            ["configKey"] = configKey,
        };
        if (!string.IsNullOrWhiteSpace(config.AnkiTemplateId))
            request["templateId"] = config.AnkiTemplateId;
        await Link.RequestAsync(AnkiTarget, "hd_anki_browse", new JsonObject { ["request"] = request }).ConfigureAwait(false);
    }

    private static AnkiDecision ParseDecision(JsonNode? reply)
    {
        var ids = new List<long>();
        if (reply?["noteIds"] is JsonArray noteIds)
            ids.AddRange(noteIds.Select(n => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0).Where(l => l != 0));
        if (reply?["noteId"] is JsonValue single && single.TryGetValue<long>(out var noteId))
            ids.Add(noteId);
        DiagnosticLog.Write("anki", $"state={reply?["state"]}, canAdd={reply?["canAdd"]}, error={reply?["error"]}");
        return new AnkiDecision
        {
            State = reply?["state"]?.ToString() ?? string.Empty,
            CanAdd = reply?["canAdd"] is JsonValue c && c.TryGetValue<bool>(out var can) && can,
            Error = reply?["error"]?.ToString(),
            NoteIds = ids.ToArray(),
            ClientSpeech = reply?["clientSpeech"]?.DeepClone(),
            Screenshot = reply?["screenshot"] is JsonValue shot && shot.TryGetValue<bool>(out var wants) && wants,
        };
    }

    // ------------------------------------------------------------------- audio

    /// <summary>Pronunciation audio: Hachidori's sources through the relay, or fetched directly offline.</summary>
    public Task<byte[]?> GetAudioAsync(string expression, string reading)
    {
        var key = $"{expression}\u0001{reading}";
        if (audioCache.TryGetValue(key, out var existing) && !existing.IsFaulted && !existing.IsCanceled
            && !(existing.IsCompletedSuccessfully && existing.Result is null))
            return existing;
        if (audioCache.Count >= 64) audioCache.Clear();
        var task = FetchAudioAsync(expression, reading);
        audioCache[key] = task;
        return task;
    }

    private async Task<byte[]?> FetchAudioAsync(string expression, string reading)
    {
        var backend = Backend;
        var data = await backend.AudioAsync(expression, reading).ConfigureAwait(false);
        DiagnosticLog.Write("audio", $"Audio sources resolved; live={backend.IsLive}, returnedBytes={data?.Length ?? 0}");
        return data;
    }

    public void Dispose()
    {
        Link.SnapshotChanged -= ClearCache;
        Link.ConnectionChanged -= OnConnectionChanged;
        offlineAnki?.Dispose();
        Offline?.Dispose();
        Link.Dispose();
        http.Dispose();
    }
}

/// <summary>Where a scan came from: the game line it belongs to, for Anki's {sentence} and friends.</summary>
public sealed record ScanContext(string Sentence, int MatchOffset, string DocumentTitle);
