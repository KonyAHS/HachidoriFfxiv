using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Hachidori;

/// <summary>An installed host dictionary, from the shared dictionary state.</summary>
public sealed record HostDictionary(string Id, string Title, string? Revision);

/// <summary>What <see cref="NpcPictureLibrary"/> needs from the Hachidori host.</summary>
public interface INpcPictureHost
{
    IReadOnlyList<HostDictionary> Dictionaries { get; }
    Task<long> GenerationAsync(CancellationToken ct);
    Task<byte[]?> MediaAsync(string dictionary, string path, long generation, CancellationToken ct);
    Task<JsonNode?> ImportAsync(string type, JsonObject payload, CancellationToken ct);
    Task RemoveAsync(HostDictionary dictionary, CancellationToken ct);
}

/// <summary>The host has a picture dictionary this computer did not build; saving would replace it.</summary>
public sealed class NpcPictureConflictException(int localCount) : Exception(
    $"Hachidori already has an \"{NpcImageDictionary.Title}\" dictionary that this computer did not make "
    + $"(another PC, or this plugin's picture folder was reset). Saving replaces it with the {localCount} picture(s) stored here.");

public sealed record NpcSaveResult(int Pictures, int Merged, int LegacyRemaining);

/// <summary>
/// The pictures on this computer are the master copy: Hachidori can only replace
/// a whole dictionary, and cannot hand an installed one back as its source ZIP.
/// Every change writes the local store first, then rebuilds and replaces the
/// single host dictionary, merging any older one-picture dictionaries into it.
/// </summary>
public sealed class NpcPictureLibrary(string directory, INpcPictureHost host,
    Func<NpcPicture, CancellationToken, Task<NpcPicture>>? normaliseLegacy = null)
{
    private const int KnownRevisionLimit = 32;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string IndexPath => Path.Combine(directory, "index.json");

    public IReadOnlyList<HostDictionary> LegacyDictionaries =>
        host.Dictionaries.Where(d => NpcImageDictionary.TryParseLegacyTitle(d.Title, out _, out _)).ToList();

    /// <summary>The saved headwords and readings, without loading picture bytes.</summary>
    public IReadOnlyList<(string Expression, string Reading)> List()
    {
        var index = ReadIndex();
        return (index?["pictures"] as JsonArray ?? [])
            .Select(n => (n?["expression"]?.GetValue<string>() ?? "", n?["reading"]?.GetValue<string>() ?? ""))
            .OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal).ToList();
    }

    public bool Contains(string expression, string reading) =>
        List().Any(p => p.Expression == expression && p.Reading == reading);

    public Task<NpcSaveResult> AddAsync(NpcPicture picture, bool replaceForeign, CancellationToken ct) =>
        ChangeAsync(pictures =>
        {
            pictures.RemoveAll(p => p.Matches(picture.Expression, picture.Reading));
            pictures.Add(picture);
        }, replaceForeign, ct);

    public Task<NpcSaveResult> RemoveAsync(string expression, string reading, bool replaceForeign, CancellationToken ct) =>
        ChangeAsync(pictures => pictures.RemoveAll(p => p.Matches(expression, reading)), replaceForeign, ct);

    /// <summary>Folds the old one-picture dictionaries into the single one, changing nothing else.</summary>
    public Task<NpcSaveResult> MergeLegacyAsync(bool replaceForeign, CancellationToken ct) =>
        ChangeAsync(_ => { }, replaceForeign, ct);

    private async Task<NpcSaveResult> ChangeAsync(Action<List<NpcPicture>> change, bool replaceForeign, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (pictures, known) = Load();
            var snapshot = host.Dictionaries;
            var combined = snapshot.Where(d => d.Title == NpcImageDictionary.Title).ToList();
            if (!replaceForeign && combined.Any(d => d.Revision is not null && !known.Contains(d.Revision)))
                throw new NpcPictureConflictException(pictures.Count);

            // Pictures captured since the merge are newer than any legacy copy.
            var merged = new List<HostDictionary>();
            var legacy = snapshot.Where(d => NpcImageDictionary.TryParseLegacyTitle(d.Title, out _, out _)).ToList();
            if (legacy.Count > 0)
            {
                var generation = await host.GenerationAsync(ct).ConfigureAwait(false);
                foreach (var dictionary in legacy)
                {
                    NpcImageDictionary.TryParseLegacyTitle(dictionary.Title, out var expression, out var reading);
                    var png = await host.MediaAsync(dictionary.Title, NpcImageDictionary.LegacyImagePath, generation, ct).ConfigureAwait(false);
                    if (png is null || !NpcImageDictionary.IsPng(png)) continue; // Leave it installed rather than lose it.
                    if (!pictures.Any(p => p.Matches(expression, reading)))
                    {
                        var (width, height) = NpcImageDictionary.PngSize(png);
                        var picture = new NpcPicture(expression, reading, png, width, height);
                        if (normaliseLegacy is not null) picture = await normaliseLegacy(picture, ct).ConfigureAwait(false);
                        pictures.Add(picture);
                    }
                    merged.Add(dictionary);
                }
            }

            change(pictures);
            // Record the revision before uploading: if the commit's reply is lost,
            // the host may hold this revision and it must still be recognised.
            if (pictures.Count > 0) known.Insert(0, NpcImageDictionary.Revision(pictures));
            Save(pictures, known);

            if (pictures.Count > 0)
                await NpcImageDictionary.UploadAsync(NpcImageDictionary.Build(pictures), host.ImportAsync, ct).ConfigureAwait(false);
            else
                foreach (var dictionary in combined) await host.RemoveAsync(dictionary, ct).ConfigureAwait(false);

            // Only after the combined dictionary holds their pictures.
            var removed = 0;
            foreach (var dictionary in merged)
            {
                try { await host.RemoveAsync(dictionary, ct).ConfigureAwait(false); removed++; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DiagnosticLog.Write("image", $"Could not remove a merged picture dictionary: {ex.Message}");
                }
            }
            return new NpcSaveResult(pictures.Count, removed, legacy.Count - removed);
        }
        finally { gate.Release(); }
    }

    private JsonObject? ReadIndex()
    {
        if (!File.Exists(IndexPath)) return null;
        try { return JsonNode.Parse(File.ReadAllText(IndexPath)) as JsonObject ?? throw new FormatException(); }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            // Never treat an unreadable store as empty: the next save would wipe the host's pictures.
            throw new HachidoriException($"The NPC picture list is unreadable ({IndexPath}). Fix or delete it before saving pictures.");
        }
    }

    private (List<NpcPicture> Pictures, List<string> Known) Load()
    {
        var index = ReadIndex();
        var pictures = new List<NpcPicture>();
        foreach (var node in index?["pictures"] as JsonArray ?? [])
        {
            var file = Path.GetFileName(node?["file"]?.GetValue<string>() ?? "");
            var path = Path.Combine(directory, file);
            if (file.Length == 0 || !File.Exists(path))
                throw new HachidoriException($"An NPC picture file is missing ({path}).");
            pictures.Add(new NpcPicture(node!["expression"]!.GetValue<string>(), node["reading"]!.GetValue<string>(),
                File.ReadAllBytes(path), node["width"]!.GetValue<int>(), node["height"]!.GetValue<int>()));
        }
        var known = (index?["knownRevisions"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().ToList();
        return (pictures, known);
    }

    private void Save(List<NpcPicture> pictures, List<string> known)
    {
        Directory.CreateDirectory(directory);
        foreach (var picture in pictures)
        {
            var path = Path.Combine(directory, picture.Hash + ".png");
            if (!File.Exists(path)) WriteAtomically(path, picture.Png);
        }
        var index = new JsonObject
        {
            ["version"] = 1,
            ["knownRevisions"] = new JsonArray(known.Distinct().Take(KnownRevisionLimit).Select(r => (JsonNode)r!).ToArray()),
            ["pictures"] = new JsonArray(pictures.Select(p => (JsonNode)new JsonObject
            {
                ["expression"] = p.Expression, ["reading"] = p.Reading, ["file"] = p.Hash + ".png",
                ["width"] = p.Width, ["height"] = p.Height,
            }).ToArray()),
        };
        WriteAtomically(IndexPath, System.Text.Encoding.UTF8.GetBytes(index.ToJsonString(new() { WriteIndented = true })));
        var referenced = pictures.Select(p => p.Hash + ".png").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory, "*.png"))
            if (!referenced.Contains(Path.GetFileName(file)))
                try { File.Delete(file); } catch (IOException) { }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }
}
