using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Hachidori;

/// <summary>Everything the plugin asks Hachidori for, on top of <see cref="HachidoriLink"/>.</summary>
public sealed class HachidoriService : IDisposable
{
    private const string EngineTarget = "hoshidicts-offscreen";
    private const string WorkerTarget = "hoshidicts-worker";
    private const string AnkiTarget = "hachidori-anki";
    private const int CacheLimit = 512;

    private readonly Configuration config;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ConcurrentDictionary<string, Task<LookupReply>> cache = new();
    private readonly ConcurrentQueue<string> cacheOrder = new();
    private readonly ConcurrentDictionary<string, Task<byte[]?>> audioCache = new();
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<WordSpan>>> segmentCache = new();

    public HachidoriService(Configuration config)
    {
        this.config = config;
        Link = new HachidoriLink(() => new UriBuilder("ws", config.RelayHost, config.RelayPort, "/link").Uri,
            ex => Plugin.Log.Warning(ex, "Dropped a sharing frame"));
        Link.SnapshotChanged += ClearCache;
    }

    public HachidoriLink Link { get; }

    public JsonObject Options => Link.Snapshot["options"] as JsonObject ?? new JsonObject();

    public IEnumerable<JsonObject> Dictionaries =>
        (Link.Snapshot["dictionaryState"]?["dictionaries"] as JsonArray)?.OfType<JsonObject>() ?? [];

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
        var response = await Link.RequestAsync(EngineTarget, "hd_segment",
            HachidoriPayloads.Segment(text, config.ScanLength, config.UsePersonalDictionary && OptionBool("personalDictionaryEnabled", true))).ConfigureAwait(false);
        var spans = WordSpan.Parse(response, text);
        DiagnosticLog.Write("segment", $"characters={text.Length}, words={spans.Count}");
        return spans;
    }

    public async Task<byte[]?> GetImageAsync(string dictionary, string path, long generation, CancellationToken ct)
    {
        var reply = await Link.RequestAsync(EngineTarget, "hd_media", new JsonObject
        {
            ["dictionary"] = dictionary, ["path"] = path, ["generation"] = generation,
        }, ct).ConfigureAwait(false);
        var bytes = HachidoriPayloads.Media(reply);
        DiagnosticLog.Write("image", $"generation={generation}, bytes={bytes?.Length ?? 0}");
        return bytes;
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

    private async Task<LookupReply> LookupUncachedAsync(string text)
    {
        var options = new JsonObject
        {
            ["personalDictionary"] = config.UsePersonalDictionary && OptionBool("personalDictionaryEnabled", true),
        };
        if (Options["frequencyDictionary"] is JsonValue fd)
            options["frequencyDictionary"] = fd.DeepClone();
        if (Options["frequencyOrder"] is JsonValue fo)
            options["frequencyOrder"] = fo.DeepClone();

        var reply = await Link.RequestAsync(EngineTarget, "hd_lookup", new JsonObject
        {
            ["text"] = text,
            ["maxResults"] = config.MaxResults,
            ["scanLength"] = config.ScanLength,
            ["options"] = options,
        }).ConfigureAwait(false);

        var results = (reply?["results"] as JsonArray)?.OfType<JsonObject>()
            .Select(r => LookupResult.Parse((JsonObject)r.DeepClone()))
            .ToList() ?? [];
        return new LookupReply
        {
            Query = text,
            Results = results,
            Generation = reply?["generation"] is JsonValue g && g.TryGetValue<long>(out var gen) ? gen : 0,
        };
    }

    private async Task<LookupReply> KanjiUncachedAsync(string character)
    {
        var reply = await Link.RequestAsync(EngineTarget, "hd_kanji", new JsonObject { ["character"] = character })
            .ConfigureAwait(false);
        var results = new List<LookupResult>();
        if (reply?["kanji"]?["entries"] is JsonArray entries)
        {
            foreach (var entry in entries.OfType<JsonObject>())
                results.Add(LookupResult.Parse(KanjiEntryResult(character, entry)));
        }

        return new LookupReply { Query = character, Results = results, IsKanji = true };
    }

    /// <summary>A native kanji entry as a LookupResult, as Hachidori's popup does (kanjiEntryResult).</summary>
    private static JsonObject KanjiEntryResult(string character, JsonObject entry)
    {
        static IEnumerable<string> Tokens(JsonNode? node) => node switch
        {
            JsonArray array => array.Select(LookupResult.Str).Where(s => s.Length > 0),
            JsonValue value => LookupResult.Str(value).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            _ => [],
        };

        var content = new JsonArray();
        var tags = Tokens(entry["tags"]).ToList();
        if (tags.Count > 0)
            content.Add(new JsonObject { ["tag"] = "div", ["data"] = new JsonObject { ["content"] = "tags" }, ["content"] = string.Join(" ", tags) });
        foreach (var (label, key) in new[] { ("On", "onyomi"), ("Kun", "kunyomi") })
        {
            var values = Tokens(entry[key]).ToList();
            if (values.Count > 0)
            {
                content.Add(new JsonObject
                {
                    ["tag"] = "div",
                    ["content"] = new JsonArray(
                        new JsonObject { ["tag"] = "strong", ["content"] = label },
                        $" {string.Join(" · ", values)}"),
                });
            }
        }

        if (entry["definitions"] is JsonArray definitions && definitions.Count > 0)
        {
            content.Add(new JsonObject
            {
                ["tag"] = "ol",
                ["content"] = new JsonArray(definitions.Select(d => (JsonNode)new JsonObject { ["tag"] = "li", ["content"] = d?.DeepClone() }).ToArray()),
            });
        }

        if (entry["stats"] is JsonArray stats && stats.Count > 0)
        {
            content.Add(new JsonObject
            {
                ["tag"] = "table",
                ["content"] = new JsonArray(stats.OfType<JsonObject>().Select(s => (JsonNode)new JsonObject
                {
                    ["tag"] = "tr",
                    ["content"] = new JsonArray(
                        new JsonObject { ["tag"] = "th", ["content"] = LookupResult.Str(s["name"]) },
                        new JsonObject { ["tag"] = "td", ["content"] = LookupResult.Str(s["value"]) }),
                }).ToArray()),
            });
        }

        var glossary = new JsonArray(new JsonObject { ["type"] = "structured-content", ["content"] = content });
        return new JsonObject
        {
            ["matched"] = character,
            ["deinflected"] = character,
            ["trace"] = new JsonArray(),
            ["preprocessorSteps"] = 0,
            ["term"] = new JsonObject
            {
                ["expression"] = character,
                ["reading"] = string.Empty,
                ["rules"] = string.Empty,
                ["score"] = 0,
                ["frequencies"] = entry["frequencies"]?.DeepClone() ?? new JsonArray(),
                ["pitches"] = new JsonArray(),
                ["glossaries"] = new JsonArray(new JsonObject
                {
                    ["dictionary"] = LookupResult.Str(entry["dictionary"]),
                    ["glossary"] = glossary.ToJsonString(),
                    ["definitionTags"] = string.Empty,
                    ["termTags"] = string.Empty,
                }),
            },
        };
    }

    // ------------------------------------------------------- lookup statistics

    /// <summary>Reads (or records) Hachidori's lookup count for a headword. Null when counts are off or unavailable.</summary>
    public async Task<int?> LookupStatsAsync(string term, string reading, bool record)
    {
        var reply = await Link.RequestAsync(WorkerTarget, record ? "hd_lookup_stats_record" : "hd_lookup_stats_read",
            new JsonObject { ["term"] = term, ["reading"] = reading }).ConfigureAwait(false);
        return reply?["statistics"]?["lookupCount"] is JsonValue v && v.TryGetValue<int>(out var count) ? count : null;
    }

    // -------------------------------------------------------------------- Anki

    public sealed record AnkiStatus(bool Available, string? ConfigKey, string? Error);

    public async Task<AnkiStatus> AnkiStatusAsync()
    {
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
        // The game has no viewport screenshot Hachidori can take (yet), so the host leaves {screenshot} empty.
        request["captureUnavailable"] = new JsonArray("screenshot", "sentence-audio", "gif");
        if (configKey is not null)
            request["configKey"] = configKey;
        if (!string.IsNullOrWhiteSpace(config.AnkiTemplateId))
            request["templateId"] = config.AnkiTemplateId;
        return request;
    }

    public async Task<AnkiDecision> AnkiPreflightAsync(JsonObject request)
    {
        var reply = await Link.RequestAsync(AnkiTarget, "hd_anki_preflight", new JsonObject { ["request"] = request.DeepClone() })
            .ConfigureAwait(false);
        return ParseDecision(reply);
    }

    public async Task<AnkiDecision> AnkiSubmitAsync(JsonObject request)
    {
        DiagnosticLog.Write("anki", "Submitting note with explicit client-media envelope.");
        var reply = await Link.RequestAsync(AnkiTarget, "hd_anki_submit", HachidoriPayloads.Submit(request))
            .ConfigureAwait(false);
        return ParseDecision(reply);
    }

    public async Task AnkiBrowseAsync(long[] noteIds, string expression, string? configKey)
    {
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
        };
    }

    // ------------------------------------------------------------------- audio

    /// <summary>
    /// Pronunciation audio from the user's Hachidori audio sources, through the relay's
    /// Yomitan-compatible API (<c>POST /ankiFields</c> with the <c>audio</c> marker).
    /// </summary>
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
        var body = new JsonObject
        {
            ["text"] = expression,
            ["type"] = "term",
            ["markers"] = new JsonArray("audio"),
            ["maxEntries"] = 8,
            ["includeMedia"] = true,
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"http://{config.RelayHost}:{config.ApiPort}/ankiFields", content).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HachidoriException($"Audio unavailable ({(int)response.StatusCode}).");
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var data = HachidoriPayloads.Audio(json, expression, reading);
        DiagnosticLog.Write("audio", $"Host audio sources resolved; returnedBytes={data?.Length ?? 0}");
        return data;
    }

    public void Dispose()
    {
        Link.SnapshotChanged -= ClearCache;
        Link.Dispose();
        http.Dispose();
    }
}

/// <summary>Where a scan came from: the game line it belongs to, for Anki's {sentence} and friends.</summary>
public sealed record ScanContext(string Sentence, int MatchOffset, string DocumentTitle);
