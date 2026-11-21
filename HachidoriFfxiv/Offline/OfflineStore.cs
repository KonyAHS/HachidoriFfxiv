using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HachidoriFfxiv.Offline;

/// <summary>A downloaded dictionary package, as offline/manifest.json lists it.</summary>
public sealed record OfflineDictionary
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Revision { get; init; }
    /// <summary>The package's folder under offline/dictionaries; a new one per download.</summary>
    public required string Folder { get; init; }
    public long Bytes { get; init; }
    /// <summary>The <c>.hoshidicts_N</c> layout the package was written in.</summary>
    public int Format { get; init; }
    public long TermCount { get; init; }
    public long FrequencyCount { get; init; }
    public long PitchCount { get; init; }
    public long KanjiCount { get; init; }
}

public sealed record OfflineManifest
{
    public int Format { get; init; } = 1;
    public string HostVersion { get; init; } = string.Empty;
    public string EngineVersion { get; init; } = string.Empty;
    public DateTimeOffset? SyncedAt { get; init; }
    public IReadOnlyList<OfflineDictionary> Dictionaries { get; init; } = [];
    /// <summary>Packages the last sync could not install, with why.</summary>
    public IReadOnlyList<OfflineSkip> Skipped { get; init; } = [];
}

/// <summary>
/// A package revision the last sync left out. A permanent reason (a newer
/// format, a damaged archive) is not downloaded again until its revision changes.
/// </summary>
public sealed record OfflineSkip(string Id, string Title, string Revision, string Reason, bool Permanent);

/// <summary>
/// The offline copy under the plugin's config folder:
/// <c>offline/manifest.json</c> (what was downloaded, at which revision),
/// <c>offline/snapshot.json</c> (Hachidori's settings), one folder per package
/// under <c>offline/dictionaries</c>, and <c>offline/staging</c> for downloads
/// in progress. Every file is replaced by writing a sibling and renaming it.
/// </summary>
public sealed class OfflineStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object gate = new();

    public OfflineStore(string root)
    {
        Root = root;
        Manifest = ReadManifest();
        Snapshot = ReadSnapshot();
    }

    public string Root { get; }
    public string DictionariesDirectory => Path.Combine(Root, "dictionaries");
    public string StagingDirectory => Path.Combine(Root, "staging");
    private string ManifestPath => Path.Combine(Root, "manifest.json");
    private string SnapshotPath => Path.Combine(Root, "snapshot.json");

    public OfflineManifest Manifest { get; private set; }

    /// <summary>The last Hachidori settings snapshot seen from the live host.</summary>
    public JsonObject? Snapshot { get; private set; }

    public bool HasDictionaries => Manifest.Dictionaries.Count > 0;

    public string PathOf(OfflineDictionary dictionary) => Path.Combine(DictionariesDirectory, dictionary.Folder);

    public long DiskBytes => Manifest.Dictionaries.Sum(d => d.Bytes);

    public void SaveManifest(OfflineManifest manifest)
    {
        lock (gate)
        {
            WriteAtomically(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
            Manifest = manifest;
        }
    }

    public void SaveSnapshot(JsonObject snapshot)
    {
        var copy = (JsonObject)snapshot.DeepClone();
        lock (gate)
        {
            WriteAtomically(SnapshotPath, JsonSerializer.SerializeToUtf8Bytes(copy));
            Snapshot = copy;
        }
    }

    /// <summary>
    /// Removes package folders and staging debris that the manifest does not
    /// reference: an interrupted download, or a replaced revision whose files
    /// were still mapped when it was swapped out.
    /// </summary>
    public void CleanUnreferenced()
    {
        lock (gate)
        {
            TryDelete(StagingDirectory);
            if (!Directory.Exists(DictionariesDirectory)) return;
            var referenced = Manifest.Dictionaries.Select(d => d.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Directory.EnumerateDirectories(DictionariesDirectory))
            {
                if (!referenced.Contains(Path.GetFileName(folder)))
                    TryDelete(folder);
            }
        }
    }

    /// <summary>Deletes everything offline mode stored. The engine must not have any of it loaded.</summary>
    public void Clear()
    {
        lock (gate)
        {
            TryDelete(Root);
            Manifest = new OfflineManifest();
            Snapshot = null;
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still mapped by the engine or held by a scanner; the next start cleans it up.
            return false;
        }
    }

    public static long DirectoryBytes(string path) =>
        Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    private OfflineManifest ReadManifest()
    {
        try
        {
            return File.Exists(ManifestPath)
                ? JsonSerializer.Deserialize<OfflineManifest>(File.ReadAllBytes(ManifestPath), Json) ?? new OfflineManifest()
                : new OfflineManifest();
        }
        catch (JsonException)
        {
            // A damaged manifest means nothing is trusted: the next sync downloads again.
            return new OfflineManifest();
        }
    }

    private JsonObject? ReadSnapshot()
    {
        try
        {
            return File.Exists(SnapshotPath) ? JsonNode.Parse(File.ReadAllBytes(SnapshotPath)) as JsonObject : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Root);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
