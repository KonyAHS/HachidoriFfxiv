using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.Offline;

/// <summary>
/// The offline copy: the native engine over the downloaded dictionaries, with
/// the settings snapshot saved from the last connection.
/// </summary>
public sealed class LocalBackend(OfflineMode offline, HttpClient http, Func<JsonObject> options) : IOfflineDictionaryBackend
{
    private const int PendingStatsLimit = 1000;
    private readonly ConcurrentQueue<(string Term, string Reading)> pendingStats = new();

    public string Name => "Offline engine";
    public bool IsLive => false;
    public bool Ready => offline.Ready;
    public JsonObject? SavedSnapshot => offline.Store.Snapshot;

    public event Action? Changed
    {
        add => offline.Changed += value;
        remove => offline.Changed -= value;
    }

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
