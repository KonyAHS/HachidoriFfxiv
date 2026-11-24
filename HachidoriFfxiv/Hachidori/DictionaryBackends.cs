using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Hachidori;

/// <summary>
/// Where lookups are answered: the linked Hachidori (<see cref="LiveBackend"/>)
/// or an offline backend (<see cref="IOfflineDictionaryBackend"/>). Both take the
/// same request messages (<see cref="HachidoriPayloads"/>) and return the same models.
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

/// <summary>Offline dictionary backend supplied by the Offline subsystem.</summary>
public interface IOfflineDictionaryBackend : IDictionaryBackend
{
    bool Ready { get; }

    JsonObject? SavedSnapshot { get; }

    event Action? Changed;

    Task FlushLookupStatsAsync(IDictionaryBackend live, CancellationToken ct);
}

/// <summary>Offline Anki miner supplied by the Anki subsystem.</summary>
public interface IOfflineAnkiMiner : IDisposable
{
    void InvalidateCache();

    string DuplicateBehaviorFor(string? templateId);

    Task<AnkiStatus> StatusAsync(string? templateId, CancellationToken ct = default);

    Task<AnkiDecision[]> PreflightManyAsync(IReadOnlyList<JsonObject> requests, CancellationToken ct = default);

    Task<AnkiDecision[]?> TryLivePreflightManyAsync(IReadOnlyList<JsonObject> requests, CancellationToken ct = default);

    Task<AnkiDecision> SubmitAsync(JsonObject request, ScreenshotMedia? screenshot, CancellationToken ct = default);

    Task BrowseAsync(long[] noteIds, string expression, string? templateId, CancellationToken ct = default);
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
