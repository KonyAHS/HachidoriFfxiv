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
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.Offline;

/// <summary>
/// Standalone dictionary management: imports standard Yomitan .zip archives directly
/// through <see cref="NativeEngine.Import"/> (no Chrome or Hachidori Relay required)
/// and synthesizes a compatible snapshot for lookups, audio, and direct AnkiConnect mining.
/// </summary>
public static class StandaloneDictionaries
{
    public const string StandaloneTemplateId = "standalone-ffxiv";
    public const string StarterJmdictUrl = "https://github.com/yomidevs/jmdict-yomitan/releases/latest/download/JMdict_english.zip";

    private const string MarkerPrefix = ".hoshidicts_";

    /// <summary>
    /// Compiles a Yomitan dictionary .zip into <paramref name="store"/> using <paramref name="native"/>.
    /// Replaces any existing local package with the same title.
    /// </summary>
    public static OfflineDictionary ImportZip(
        NativeEngine native,
        OfflineStore store,
        string zipPath,
        Action<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            throw new FileNotFoundException("Dictionary archive not found.", zipPath);

        var (zipTitle, zipRevision) = ReadZipIndex(zipPath);
        progress?.Invoke($"Importing {(zipTitle.Length > 0 ? zipTitle : Path.GetFileName(zipPath))}…");

        Directory.CreateDirectory(store.StagingDirectory);
        Directory.CreateDirectory(store.DictionariesDirectory);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var stagedOut = Path.Combine(store.StagingDirectory, "imp-" + token);
        Directory.CreateDirectory(stagedOut);
        try
        {
            var result = native.Import(zipPath, stagedOut);
            var ok = result["success"] is JsonValue s && s.TryGetValue<bool>(out var b) && b;
            if (!ok)
            {
                var err = result["error"]?.ToString();
                throw new OfflineEngineException(string.IsNullOrWhiteSpace(err)
                    ? $"Could not import {Path.GetFileName(zipPath)} (not a valid Yomitan dictionary archive)."
                    : $"Import failed: {err}")
                { Permanent = true };
            }

            var title = NonEmpty(Str(result["title"]), zipTitle, Path.GetFileNameWithoutExtension(zipPath));
            var revision = NonEmpty(Str(result["revision"]), zipRevision, DateTime.UtcNow.ToString("yyyy.MM.dd"));

            // hdw_import writes into stagedOut/<title>, or directly into stagedOut if single child folder.
            var packageDir = Path.Combine(stagedOut, title);
            if (!Directory.Exists(packageDir))
                packageDir = Directory.EnumerateDirectories(stagedOut).FirstOrDefault() ?? stagedOut;

            var format = DetectFormatMarker(packageDir);
            if (format == 0)
                throw new OfflineEngineException($"{title}: compiled dictionary contained no hoshidicts format marker.") { Permanent = true };

            var id = "local-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title.Trim().ToLowerInvariant())))[..12];
            var folder = $"{id}-{token}";
            var destination = Path.Combine(store.DictionariesDirectory, folder);
            Directory.Move(packageDir, destination);

            var entry = new OfflineDictionary
            {
                Id = id,
                Title = title,
                Revision = revision,
                Folder = folder,
                Bytes = OfflineStore.DirectoryBytes(destination),
                Format = format,
                TermCount = JsonNumber.Count(result["termCount"]),
                FrequencyCount = JsonNumber.Count(result["frequencyCount"]),
                PitchCount = JsonNumber.Count(result["pitchCount"]),
                KanjiCount = JsonNumber.Count(result["kanjiCount"]),
                Source = "local",
                Enabled = true,
            };

            // If counts weren't returned by hdw_import, fall back to probing compiled bank files.
            if (entry.TermCount == 0 && entry.FrequencyCount == 0 && entry.PitchCount == 0 && entry.KanjiCount == 0)
                entry = InferKindsFromFiles(entry, destination);

            var existing = store.Manifest.Dictionaries.ToList();
            var retired = new List<string>();
            var replacedIndex = existing.FindIndex(d =>
                string.Equals(d.Source, "local", StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Title, title, StringComparison.OrdinalIgnoreCase));
            if (replacedIndex >= 0)
            {
                retired.Add(store.PathOf(existing[replacedIndex]));
                existing[replacedIndex] = entry with { Enabled = existing[replacedIndex].Enabled };
            }
            else
            {
                existing.Add(entry);
            }

            store.SaveManifest(store.Manifest with
            {
                EngineVersion = native.Version.Hachidori,
                SyncedAt = DateTimeOffset.UtcNow,
                Dictionaries = existing,
            });
            foreach (var old in retired)
                OfflineStore.TryDelete(old);
            return entry;
        }
        finally
        {
            OfflineStore.TryDelete(stagedOut);
        }
    }

    public static async Task<OfflineDictionary> ImportFromUrlAsync(
        NativeEngine native,
        OfflineStore store,
        HttpClient http,
        string url,
        string label,
        Action<string>? progress,
        CancellationToken ct)
    {
        if (!FfxivDictionary.IsAllowedExternalUrl(url))
            throw new OfflineEngineException("Only http/https dictionary URLs are supported.") { Permanent = true };

        Directory.CreateDirectory(store.StagingDirectory);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var archive = Path.Combine(store.StagingDirectory, $"dl-{token}.zip");
        try
        {
            progress?.Invoke($"Downloading {label}…");
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.UserAgent.ParseAdd("HachidoriFfxiv/2.0");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new OfflineEngineException($"Download failed for {label} (HTTP {(int)response.StatusCode}).");
                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await body.CopyToAsync(file, 1 << 16, ct).ConfigureAwait(false);
            }

            return await Task.Run(() => ImportZip(native, store, archive, progress), ct).ConfigureAwait(false);
        }
        finally
        {
            OfflineStore.TryDelete(archive);
        }
    }

    public static bool RemoveLocal(OfflineStore store, string id, out string? retiredFolder)
    {
        retiredFolder = null;
        var list = store.Manifest.Dictionaries.ToList();
        var idx = list.FindIndex(d => d.Id == id && string.Equals(d.Source, "local", StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return false;
        retiredFolder = store.PathOf(list[idx]);
        list.RemoveAt(idx);
        store.SaveManifest(store.Manifest with { Dictionaries = list });
        return true;
    }

    public static bool SetLocalEnabled(OfflineStore store, string id, bool enabled)
    {
        var list = store.Manifest.Dictionaries.ToList();
        var idx = list.FindIndex(d => d.Id == id && string.Equals(d.Source, "local", StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || list[idx].Enabled == enabled)
            return false;
        list[idx] = list[idx] with { Enabled = enabled };
        store.SaveManifest(store.Manifest with { Dictionaries = list });
        return true;
    }

    public static bool MoveLocal(OfflineStore store, string id, int delta)
    {
        var list = store.Manifest.Dictionaries.ToList();
        var idx = list.FindIndex(d => d.Id == id && string.Equals(d.Source, "local", StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return false;
        var target = Math.Clamp(idx + delta, 0, list.Count - 1);
        if (target == idx)
            return false;
        var item = list[idx];
        list.RemoveAt(idx);
        list.Insert(target, item);
        store.SaveManifest(store.Manifest with { Dictionaries = list });
        return true;
    }

    /// <summary>
    /// Builds the effective offline snapshot: starts from Hachidori's synced snapshot if present,
    /// injects any standalone local dictionaries so their images and metadata work, and injects
    /// the plugin's built-in Standalone Anki Template when configured or when no synced template exists.
    /// </summary>
    public static JsonObject BuildEffectiveSnapshot(
        OfflineStore store,
        StandaloneAnkiConfig? standaloneAnki,
        bool captureScreenshot = true,
        string ankiConnectUrl = "http://127.0.0.1:8765",
        string ankiConnectApiKey = "")
    {
        var snap = store.Snapshot is not null
            ? (JsonObject)store.Snapshot.DeepClone()
            : new JsonObject();

        // Ensure dictionaryState contains every local dictionary (needed for Anki image path hashing and UI listing).
        var dictState = snap["dictionaryState"] as JsonObject ?? new JsonObject { ["schemaVersion"] = 1 };
        var dicts = dictState["dictionaries"] as JsonArray ?? new JsonArray();
        var existingTitles = dicts.OfType<JsonObject>().Select(d => Str(d["title"])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in store.Manifest.Dictionaries)
        {
            if (existingTitles.Contains(d.Title))
                continue;
            dicts.Add(new JsonObject
            {
                ["id"] = d.Id,
                ["title"] = d.Title,
                ["revision"] = d.Revision,
                ["enabled"] = d.Enabled,
                ["path"] = store.PathOf(d),
                ["termCount"] = d.TermCount,
                ["frequencyCount"] = d.FrequencyCount,
                ["pitchCount"] = d.PitchCount,
                ["kanjiCount"] = d.KanjiCount,
            });
        }
        dictState["dictionaries"] = dicts;
        snap["dictionaryState"] = dictState;

        var options = snap["options"] as JsonObject ?? new JsonObject();

        // Provide a working default pronunciation source when running pure standalone (no Chrome audioSources synced).
        if (options["audioSources"] is not JsonArray { Count: > 0 })
        {
            options["audioSources"] = new JsonArray(new JsonObject
            {
                ["type"] = "url",
                ["enabled"] = true,
                ["name"] = "JapanesePod101",
                ["url"] = "https://assets.languagepod101.com/dictionary/japanese/audiomp3.php?kanji={term}&kana={reading}",
            });
        }

        // Inject built-in standalone Anki template if configured and either preferred or no synced template exists.
        var anki = options["anki"] as JsonObject ?? new JsonObject();
        var templates = anki["templates"] as JsonArray ?? new JsonArray();
        var standalone = BuildStandaloneTemplateJson(standaloneAnki, captureScreenshot);
        if (standalone is not null)
        {
            // Remove any prior copy of the standalone template before inserting.
            for (var i = templates.Count - 1; i >= 0; i--)
            {
                if (Str(templates[i]?["id"]) == StandaloneTemplateId)
                    templates.RemoveAt(i);
            }

            if (standaloneAnki!.PreferStandalone || templates.Count == 0)
                templates.Insert(0, standalone);
            else
                templates.Add(standalone);

            if (string.IsNullOrWhiteSpace(Str(anki["url"])))
                anki["url"] = string.IsNullOrWhiteSpace(ankiConnectUrl) ? "http://127.0.0.1:8765" : ankiConnectUrl.Trim();
            if (string.IsNullOrWhiteSpace(Str(anki["apiKey"])) && !string.IsNullOrWhiteSpace(ankiConnectApiKey))
                anki["apiKey"] = ankiConnectApiKey.Trim();
        }
        anki["templates"] = templates;
        options["anki"] = anki;
        snap["options"] = options;
        return snap;
    }

    private static JsonObject? BuildStandaloneTemplateJson(StandaloneAnkiConfig? sa, bool captureScreenshot)
    {
        if (sa is null || string.IsNullOrWhiteSpace(sa.Deck) || string.IsNullOrWhiteSpace(sa.Model) || sa.Fields.Count == 0)
            return null;

        var fieldTemplates = new JsonObject();
        foreach (var (name, value) in sa.Fields)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            fieldTemplates[name] = new JsonObject
            {
                ["value"] = value ?? string.Empty,
                ["overwriteMode"] = "coalesce",
            };
        }

        var tags = (sa.Tags ?? string.Empty)
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => (JsonNode?)t)
            .ToArray();

        return new JsonObject
        {
            ["id"] = StandaloneTemplateId,
            ["name"] = $"Standalone ({sa.Model})",
            ["deck"] = sa.Deck.Trim(),
            ["model"] = sa.Model.Trim(),
            ["duplicateBehavior"] = string.IsNullOrWhiteSpace(sa.DuplicateBehavior) ? "prevent" : sa.DuplicateBehavior,
            ["duplicateScope"] = string.IsNullOrWhiteSpace(sa.DuplicateScope) ? "collection" : sa.DuplicateScope,
            ["captureScreenshot"] = captureScreenshot,
            ["tags"] = new JsonArray(tags),
            ["fieldTemplates"] = fieldTemplates,
        };
    }

    private static (string Title, string Revision) ReadZipIndex(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName.Replace('\\', '/'), "index.json", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return (string.Empty, string.Empty);
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var node = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
            return (Str(node?["title"]), Str(node?["revision"]));
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    private static int DetectFormatMarker(string directory)
    {
        var format = 0;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(MarkerPrefix, StringComparison.Ordinal)
                && int.TryParse(name[MarkerPrefix.Length..], out var marker))
            {
                format = Math.Max(format, marker);
            }
        }
        return format;
    }

    private static OfflineDictionary InferKindsFromFiles(OfflineDictionary entry, string directory)
    {
        var files = Directory.EnumerateFiles(directory).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasTerm = files.Any(f => f is not null && (f.Contains("term", StringComparison.OrdinalIgnoreCase) || f.StartsWith("blobs", StringComparison.OrdinalIgnoreCase)));
        var hasFreq = files.Any(f => f is not null && f.Contains("freq", StringComparison.OrdinalIgnoreCase));
        var hasPitch = files.Any(f => f is not null && f.Contains("pitch", StringComparison.OrdinalIgnoreCase));
        var hasKanji = files.Any(f => f is not null && f.Contains("kanji", StringComparison.OrdinalIgnoreCase));
        return entry with
        {
            TermCount = hasTerm ? 1 : (hasFreq || hasPitch || hasKanji ? 0 : 1),
            FrequencyCount = hasFreq ? 1 : 0,
            PitchCount = hasPitch ? 1 : 0,
            KanjiCount = hasKanji ? 1 : 0,
        };
    }

    private static string NonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    private static string Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
}
