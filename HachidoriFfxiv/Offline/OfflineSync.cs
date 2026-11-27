using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Offline;

/// <summary>One dictionary the relay's <c>GET /dictionaries</c> lists.</summary>
public sealed record RemoteDictionary(string Id, string Title, string Revision);

/// <summary>What a sync changed; <see cref="Retired"/> are folders to delete once the engine lets go of them.</summary>
public sealed record SyncResult(int Kept, int Downloaded, int Removed, IReadOnlyList<string> Retired, IReadOnlyList<OfflineSkip> Skipped)
{
    public bool Changed => Downloaded > 0 || Removed > 0;

    public override string ToString() =>
        $"{Downloaded} downloaded, {Removed} removed, {Kept} unchanged" + (Skipped.Count > 0 ? $", {Skipped.Count} skipped" : string.Empty);
}

/// <summary>
/// Keeps offline/dictionaries in step with the live host. The relay's HTTP API
/// lists every dictionary with its revision and serves each as a Hachidori
/// backup archive whose <c>dictionaries/0</c> folder holds the package's
/// compiled hoshidicts files, which the engine opens as they are. Only a new or
/// changed revision is downloaded; it lands in a new folder, so a package the
/// engine has mapped is never written to, and the manifest is replaced in one
/// rename once every download has been verified.
/// </summary>
public sealed class OfflineSync
{
    private const string PackagePrefix = "dictionaries/0/";
    private const string MarkerPrefix = ".hoshidicts_";
    private const string BackupIndex = "hachidori-backup.json";

    private readonly OfflineStore store;
    private readonly HttpClient http;
    private readonly Func<Uri> api;
    private readonly int newestFormat;
    private readonly SemaphoreSlim running = new(1, 1);

    public OfflineSync(OfflineStore store, HttpClient http, Func<Uri> api, int newestFormat)
    {
        this.store = store;
        this.http = http;
        this.api = api;
        this.newestFormat = newestFormat;
    }

    public bool Running => running.CurrentCount == 0;

    /// <summary>What the running sync is doing, for the settings window.</summary>
    public string? Progress { get; private set; }

    public async Task<IReadOnlyList<RemoteDictionary>> ListAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(api(), "dictionaries"), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OfflineEngineException($"The relay could not list Hachidori's dictionaries (HTTP {(int)response.StatusCode}). Is Hachidori open in Chrome?");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (body?["dictionaries"] is not JsonArray list)
            throw new OfflineEngineException("The relay sent a malformed dictionary list.");
        return list.OfType<JsonObject>()
            .Select(d => new RemoteDictionary(Str(d["id"]), Str(d["title"]), Str(d["revision"])))
            .Where(d => d.Id.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Downloads what changed since the manifest and drops what the host no
    /// longer has. <paramref name="snapshot"/> supplies each package's counts
    /// when its archive does not.
    /// </summary>
    public async Task<SyncResult> SyncAsync(JsonObject? snapshot, string hostVersion, string engineVersion, CancellationToken ct)
    {
        if (!await running.WaitAsync(0, ct).ConfigureAwait(false))
            throw new OfflineEngineException("A dictionary sync is already running.");
        try
        {
            Progress = "Listing Hachidori's dictionaries…";
            var remote = await ListAsync(ct).ConfigureAwait(false);
            var before = store.Manifest;
            var current = before.Dictionaries.ToDictionary(d => d.Id, StringComparer.Ordinal);
            var rows = SnapshotRows(snapshot);
            var next = new List<OfflineDictionary>();
            var skipped = new List<OfflineSkip>();
            var knownSkips = before.Skipped.Where(k => k.Permanent).ToDictionary(k => (k.Id, k.Revision));
            var retired = new List<string>();
            int kept = 0, downloaded = 0;

            Directory.CreateDirectory(store.DictionariesDirectory);
            foreach (var (dictionary, index) in remote.Select((d, i) => (d, i)))
            {
                ct.ThrowIfCancellationRequested();
                if (current.TryGetValue(dictionary.Id, out var existing) && existing.Revision == dictionary.Revision
                    && File.Exists(Path.Combine(store.PathOf(existing), MarkerPrefix + existing.Format)))
                {
                    next.Add(existing with { Title = dictionary.Title });
                    kept++;
                    continue;
                }

                if (knownSkips.TryGetValue((dictionary.Id, dictionary.Revision), out var known))
                {
                    skipped.Add(known with { Title = dictionary.Title });
                    if (existing is not null) next.Add(existing);
                    continue;
                }

                Progress = $"Downloading {dictionary.Title} ({index + 1}/{remote.Count})…";
                try
                {
                    var installed = await DownloadAsync(dictionary, rows.GetValueOrDefault(dictionary.Id), ct).ConfigureAwait(false);
                    next.Add(installed);
                    downloaded++;
                    if (existing is not null) retired.Add(store.PathOf(existing));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The package itself is unusable (not a network failure): do not fetch this revision again.
                    var permanent = ex is OfflineEngineException { Permanent: true } or InvalidDataException;
                    skipped.Add(new OfflineSkip(dictionary.Id, dictionary.Title, dictionary.Revision, ex.Message, permanent));
                    DiagnosticLog.Write("offline", $"Skipped a dictionary: {ex.Message}");
                    // A revision that cannot be installed keeps the one already here.
                    if (existing is not null) next.Add(existing);
                }
            }

            var remoteIds = remote.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            var preservedLocal = before.Dictionaries
                .Where(d => string.Equals(d.Source, "local", StringComparison.OrdinalIgnoreCase) && !remoteIds.Contains(d.Id))
                .ToList();
            next.AddRange(preservedLocal);
            var keptIds = next.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            var removed = before.Dictionaries.Where(d => !keptIds.Contains(d.Id)).ToList();
            retired.AddRange(removed.Select(store.PathOf));

            store.SaveManifest(new OfflineManifest
            {
                HostVersion = hostVersion,
                EngineVersion = engineVersion,
                SyncedAt = DateTimeOffset.UtcNow,
                Dictionaries = next,
                Skipped = skipped,
            });
            if (snapshot is not null) store.SaveSnapshot(snapshot);
            return new SyncResult(kept, downloaded, removed.Count, retired, skipped);
        }
        finally
        {
            Progress = null;
            OfflineStore.TryDelete(store.StagingDirectory);
            running.Release();
        }
    }

    private async Task<OfflineDictionary> DownloadAsync(RemoteDictionary dictionary, JsonObject? row, CancellationToken ct)
    {
        Directory.CreateDirectory(store.StagingDirectory);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var archive = Path.Combine(store.StagingDirectory, $"{token}.zip");
        var staged = Path.Combine(store.StagingDirectory, token);
        try
        {
            using (var response = await http.GetAsync(new Uri(api(), $"dictionaries/{Uri.EscapeDataString(dictionary.Id)}"),
                       HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new OfflineEngineException($"{dictionary.Title}: download failed (HTTP {(int)response.StatusCode}).");
                var expected = response.Content.Headers.ContentLength;
                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                await body.CopyToAsync(file, 1 << 20, ct).ConfigureAwait(false);
                // The relay ends a failed transfer early rather than send a wrong body.
                if (expected is { } length && file.Length != length)
                    throw new OfflineEngineException($"{dictionary.Title}: the download stopped at {file.Length} of {length} bytes.");
            }

            var (format, backupRow) = Extract(archive, staged, dictionary.Title);
            if (format > newestFormat)
                throw new OfflineEngineException(
                    $"{dictionary.Title} uses dictionary format {format}, newer than this plugin's engine reads ({newestFormat}). Update the plugin; live lookups still work.")
                { Permanent = true };
            var source = backupRow ?? row;
            var folder = $"{dictionary.Id}-{token}";
            var destination = Path.Combine(store.DictionariesDirectory, folder);
            Directory.Move(staged, destination);
            return new OfflineDictionary
            {
                Id = dictionary.Id,
                Title = dictionary.Title,
                Revision = dictionary.Revision,
                Folder = folder,
                Bytes = OfflineStore.DirectoryBytes(destination),
                Format = format,
                TermCount = Count(source?["termCount"]),
                FrequencyCount = Count(source?["frequencyCount"]),
                PitchCount = Count(source?["pitchCount"]),
                KanjiCount = Count(source?["kanjiCount"]),
            };
        }
        finally
        {
            OfflineStore.TryDelete(archive);
            OfflineStore.TryDelete(staged);
        }
    }

    /// <summary>
    /// Unpacks <c>dictionaries/0</c> of a backup archive into <paramref name="destination"/>.
    /// Returns the package's layout version and its dictionary-state row from
    /// <c>hachidori-backup.json</c>, when the archive has one.
    /// </summary>
    public static (int Format, JsonObject? Row) Extract(string archive, string destination, string title)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        JsonObject? row = null;
        var format = 0;
        using (var zip = ZipFile.OpenRead(archive))
        {
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name == BackupIndex)
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    row = (JsonNode.Parse(reader.ReadToEnd())?["snapshot"]?["state"]?["dictionaries"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                    continue;
                }
                if (!name.StartsWith(PackagePrefix, StringComparison.Ordinal) || name.EndsWith('/')) continue;
                var relative = name[PackagePrefix.Length..];
                var target = Path.GetFullPath(Path.Combine(destination, relative));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new OfflineEngineException($"{title}: the archive names a file outside its package.") { Permanent = true };
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                var file = Path.GetFileName(relative);
                if (file.StartsWith(MarkerPrefix, StringComparison.Ordinal) && int.TryParse(file[MarkerPrefix.Length..], out var marker))
                    format = Math.Max(format, marker);
            }
        }
        if (format == 0)
            throw new OfflineEngineException($"{title}: the archive holds no compiled hoshidicts package.") { Permanent = true };
        return (format, row);
    }

    /// <summary>The host's dictionary-state rows by id (schema 1 and 2 share the package list).</summary>
    public static Dictionary<string, JsonObject> SnapshotRows(JsonObject? snapshot) =>
        ((snapshot?["dictionaryState"]?["dictionaries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        .Where(r => Str(r["id"]).Length > 0)
        .GroupBy(r => Str(r["id"]))
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static long Count(JsonNode? node) => JsonNumber.Count(node);

    private static string Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
}
