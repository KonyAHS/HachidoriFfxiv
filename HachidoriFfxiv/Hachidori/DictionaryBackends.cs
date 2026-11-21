using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HachidoriFfxiv.Offline;

namespace HachidoriFfxiv.Hachidori;

/// <summary>
/// Where lookups are answered: the linked Hachidori (<see cref="LiveBackend"/>)
/// or the offline copy (<see cref="LocalBackend"/>). Both take the same request
/// messages (<see cref="HachidoriPayloads"/>) and return the same models.
/// </summary>
public interface IDictionaryBackend
{
    /// <summary>Shown in the settings window.</summary>
    string Name { get; }

    bool IsLive { get; }

    Task<LookupReply> LookupAsync(JsonObject message, CancellationToken ct = default);

    Task<LookupReply> KanjiAsync(string character, CancellationToken ct = default);

    Task<IReadOnlyList<WordSpan>> SegmentAsync(JsonObject message, string text, CancellationToken ct = default);

    Task<byte[]?> MediaAsync(string dictionary, string path, long generation, CancellationToken ct = default);

    /// <summary>Hachidori's lookup count for a headword; null when unavailable.</summary>
    Task<int?> LookupStatsAsync(string term, string reading, bool record, CancellationToken ct = default);

    Task<byte[]?> AudioAsync(string expression, string reading, CancellationToken ct = default);
}

/// <summary>The linked Hachidori, over the relay.</summary>
public sealed class LiveBackend(HachidoriLink link, HttpClient http, Func<string> apiBase) : IDictionaryBackend
{
    public const string EngineTarget = "hoshidicts-offscreen";
    public const string WorkerTarget = "hoshidicts-worker";

    public string Name => "Hachidori (live)";
    public bool IsLive => true;

    public async Task<LookupReply> LookupAsync(JsonObject message, CancellationToken ct = default) =>
        HachidoriPayloads.LookupReply(await link.RequestAsync(EngineTarget, "hd_lookup", message, ct).ConfigureAwait(false),
            LookupResult.Str(message["text"]));

    public async Task<LookupReply> KanjiAsync(string character, CancellationToken ct = default) =>
        HachidoriPayloads.KanjiReply(await link.RequestAsync(EngineTarget, "hd_kanji", HachidoriPayloads.Kanji(character), ct)
            .ConfigureAwait(false), character);

    public async Task<IReadOnlyList<WordSpan>> SegmentAsync(JsonObject message, string text, CancellationToken ct = default) =>
        WordSpan.Parse(await link.RequestAsync(EngineTarget, "hd_segment", message, ct).ConfigureAwait(false), text);

    public async Task<byte[]?> MediaAsync(string dictionary, string path, long generation, CancellationToken ct = default) =>
        HachidoriPayloads.Media(await link.RequestAsync(EngineTarget, "hd_media", new JsonObject
        {
            ["dictionary"] = dictionary, ["path"] = path, ["generation"] = generation,
        }, ct).ConfigureAwait(false));

    public async Task<int?> LookupStatsAsync(string term, string reading, bool record, CancellationToken ct = default)
    {
        var reply = await link.RequestAsync(WorkerTarget, record ? "hd_lookup_stats_record" : "hd_lookup_stats_read",
            new JsonObject { ["term"] = term, ["reading"] = reading }, ct).ConfigureAwait(false);
        return reply?["statistics"]?["lookupCount"] is JsonValue v && v.TryGetValue<int>(out var count) ? count : null;
    }

    /// <summary>
    /// Pronunciation audio from the user's Hachidori audio sources, through the relay's
    /// Yomitan-compatible API (<c>POST /ankiFields</c> with the <c>audio</c> marker).
    /// </summary>
    public async Task<byte[]?> AudioAsync(string expression, string reading, CancellationToken ct = default)
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
        using var response = await http.PostAsync($"{apiBase()}ankiFields", content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HachidoriException($"Audio unavailable ({(int)response.StatusCode}).");
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var data = HachidoriPayloads.Audio(json, expression, reading);
        DiagnosticLog.Write("audio", $"Host audio sources resolved; returnedBytes={data?.Length ?? 0}");
        return data;
    }
}

/// <summary>
/// The offline copy: the native engine over the downloaded dictionaries, with
/// the settings snapshot saved from the last connection.
/// </summary>
public sealed class LocalBackend(OfflineMode offline, HttpClient http, Func<JsonObject> options) : IDictionaryBackend
{
    private const int PendingStatsLimit = 1000;
    private readonly ConcurrentQueue<(string Term, string Reading)> pendingStats = new();

    public string Name => "Offline engine";
    public bool IsLive => false;

    public Task<LookupReply> LookupAsync(JsonObject message, CancellationToken ct = default) =>
        Run(engine => HachidoriPayloads.LookupReply(engine.Lookup(message), LookupResult.Str(message["text"])), ct);

    public Task<LookupReply> KanjiAsync(string character, CancellationToken ct = default) =>
        Run(engine => HachidoriPayloads.KanjiReply(engine.Kanji(HachidoriPayloads.Kanji(character)), character), ct);

    public Task<IReadOnlyList<WordSpan>> SegmentAsync(JsonObject message, string text, CancellationToken ct = default) =>
        Run(engine => WordSpan.Parse(engine.Segment(message), text), ct);

    // The offline engine has one generation at a time; an image asked for by an
    // older reply is still the same file unless a sync replaced its package.
    public Task<byte[]?> MediaAsync(string dictionary, string path, long generation, CancellationToken ct = default) =>
        Run(engine => engine.Media(dictionary, path), ct);

    /// <summary>
    /// Counts live in Hachidori's storage, which the snapshot does not carry, so
    /// none is shown offline. Recorded lookups wait for the next connection.
    /// </summary>
    public Task<int?> LookupStatsAsync(string term, string reading, bool record, CancellationToken ct = default)
    {
        if (record && pendingStats.Count < PendingStatsLimit) pendingStats.Enqueue((term, reading));
        return Task.FromResult<int?>(null);
    }

    /// <summary>Hands the lookups recorded offline to Hachidori once it is linked again.</summary>
    public async Task FlushLookupStatsAsync(IDictionaryBackend live, CancellationToken ct)
    {
        while (pendingStats.TryDequeue(out var lookup))
        {
            try { await live.LookupStatsAsync(lookup.Term, lookup.Reading, record: true, ct).ConfigureAwait(false); }
            catch (HachidoriException) { return; }
        }
    }

    public Task<byte[]?> AudioAsync(string expression, string reading, CancellationToken ct = default) =>
        OfflineAudio.FetchAsync(http, options()["audioSources"], expression, reading, ct);

    // Engine calls are short (well under a millisecond for a lookup) but loading
    // the dictionaries is not, so they run off the caller's thread.
    private Task<T> Run<T>(Func<LocalEngine, T> call, CancellationToken ct) =>
        Task.Run(() => call(offline.Loaded()), ct);
}
