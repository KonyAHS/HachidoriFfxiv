using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.Offline;

/// <summary>One downloaded dictionary package as the local engine loads it.</summary>
public sealed record LocalDictionary(string Id, string Title, string Path, bool Enabled,
    long TermCount, long FrequencyCount, long PitchCount, long KanjiCount)
{
    /// <summary>engine-service.js kindsForPackage: the kinds with entries, or term alone.</summary>
    public IReadOnlyList<NativeEngine.Kind> Kinds
    {
        get
        {
            var kinds = new List<NativeEngine.Kind>();
            if (TermCount > 0) kinds.Add(NativeEngine.Kind.Term);
            if (FrequencyCount > 0) kinds.Add(NativeEngine.Kind.Frequency);
            if (PitchCount > 0) kinds.Add(NativeEngine.Kind.Pitch);
            if (KanjiCount > 0) kinds.Add(NativeEngine.Kind.Kanji);
            return kinds.Count == 0 ? [NativeEngine.Kind.Term] : kinds;
        }
    }
}

/// <summary>
/// Hachidori's engine service (engine-service.js) over the native engine: the
/// same request messages in, the same replies out, including the JavaScript
/// post-processing a live <c>hd_lookup</c> gets.
/// </summary>
public sealed class LocalEngine : IDisposable
{
    public const string PersonalDictionaryTitle = "Hachidori Custom Dictionary";
    private const string KanjiWordFrequencyDictionary = "Bee's Ultimate Kanji Dictionary";
    private static readonly string[] FrequencyOrders = ["auto", "ascending", "descending", "disabled"];
    private const int MaxLookupTextBytes = 4 * 1024;

    private readonly NativeEngine native;
    private readonly KanjiFurigana? furigana;
    private Dictionary<string, Dictionary<string, JsonObject>>? tagBanks;

    public LocalEngine(NativeEngine native, KanjiFurigana? furigana = null)
    {
        this.native = native;
        this.furigana = furigana;
    }

    public NativeVersion Version => native.Version;

    /// <summary>Bumped on every load, like the host's dictionary generation.</summary>
    public long Generation { get; private set; }

    public int DictionaryCount { get; private set; }

    public IReadOnlyList<LocalDictionary> Loaded { get; private set; } = [];

    /// <summary>Packages that failed to load in the last <see cref="Load"/>, with the engine's reason.</summary>
    public IReadOnlyList<(LocalDictionary Dictionary, string Error)> Failures { get; private set; } = [];

    /// <summary>
    /// Replaces the loaded set with the enabled packages, in order. A package
    /// that fails is left out and reported rather than stopping every other
    /// dictionary (engine-service.js loadDictionaries).
    /// </summary>
    public void Load(IEnumerable<LocalDictionary> dictionaries)
    {
        lock (native.Gate)
        {
            var candidates = dictionaries.Where(d => d.Enabled).ToList();
            var failures = new List<(LocalDictionary, string)>();
            while (true)
            {
                native.Reset();
                var count = 0;
                (LocalDictionary, string)? failure = null;
                foreach (var dictionary in candidates)
                {
                    foreach (var kind in dictionary.Kinds)
                    {
                        if (native.AddDictionary(dictionary.Path, kind) is { } error)
                        {
                            failure = (dictionary, $"could not load as {kind}: {error}");
                            break;
                        }
                        count++;
                    }
                    if (failure is not null) break;
                }
                if (failure is { } failed)
                {
                    // A package can fail after some of its kinds were added: reload without it.
                    failures.Add(failed);
                    candidates.Remove(failed.Item1);
                    continue;
                }
                DictionaryCount = count;
                Loaded = candidates;
                Failures = failures;
                Generation++;
                tagBanks = null;
                return;
            }
        }
    }

    public void Unload() => Load([]);

    // --------------------------------------------------------------- hd_lookup

    /// <summary>engine-service.js hd_lookup for a request without a profile: <c>{results, dictionaryCount, generation}</c>.</summary>
    public JsonObject Lookup(JsonObject message)
    {
        var text = BoundedText(message["text"], "lookup text", MaxLookupTextBytes);
        var maxResults = ClampInt(message["maxResults"], 1, 256, 32);
        var scanLength = ClampInt(message["scanLength"], 1, 64, 16);
        var options = message["options"] as JsonObject;
        string json;
        long generation;
        lock (native.Gate)
        {
            json = native.Lookup(text, maxResults, scanLength, LookupOptionsJson(options, null));
            generation = Generation;
        }
        var reply = JsonNode.Parse(json) as JsonObject;
        if (reply?["results"] is not JsonArray results || reply["dictionaryCount"] is not JsonValue)
            throw new OfflineEngineException("hdw_lookup returned a malformed lookup response");
        WithoutKanjiWordFrequencies(results);
        WithoutPersonalDictionary(results, options);
        WithDefinitionTags(results);
        WithFurigana(results);
        reply["generation"] = generation;
        return reply;
    }

    // engine-service.js lookupOptionsJson.
    private static string LookupOptionsJson(JsonObject? options, string? excludedDictionary)
    {
        var order = Text(options?["frequencyOrder"]);
        var json = new JsonObject
        {
            ["frequencyDictionary"] = BoundedText(options?["frequencyDictionary"], "frequency dictionary", MaxLookupTextBytes),
            ["frequencyOrder"] = FrequencyOrders.Contains(order) ? order : "auto",
            ["primaryReading"] = BoundedText(options?["primaryReading"], "primary reading", MaxLookupTextBytes),
            ["metadataOnlyTerms"] = options?["metadataOnlyTerms"] is JsonValue m && m.TryGetValue<bool>(out var b) && b,
        };
        if (excludedDictionary is not null) json["excludedDictionary"] = excludedDictionary;
        return json.ToJsonString();
    }

    // Bee's rich kanji cards' character ranks belong to the clicked-kanji route,
    // not word ranks (#724).
    private static void WithoutKanjiWordFrequencies(JsonArray results)
    {
        foreach (var term in results.Select(r => r?["term"]).OfType<JsonObject>())
        {
            if (term["frequencies"] is not JsonArray frequencies) continue;
            var index = frequencies.ToList().FindIndex(g => Text(g?["dictionary"]) == KanjiWordFrequencyDictionary);
            if (index != -1) frequencies.RemoveAt(index);
        }
    }

    // A reader with the personal dictionary off leaves out its glossaries.
    private static void WithoutPersonalDictionary(JsonArray results, JsonObject? options)
    {
        if (options?["personalDictionary"] is not JsonValue p || !p.TryGetValue<bool>(out var personal) || personal) return;
        for (var i = results.Count - 1; i >= 0; i--)
        {
            if (results[i]?["term"]?["glossaries"] is not JsonArray glossaries) continue;
            var kept = glossaries.Where(g => Text(g?["dictionary"]) != PersonalDictionaryTitle).ToList();
            if (kept.Count == glossaries.Count) continue;
            if (kept.Count == 0)
            {
                results.RemoveAt(i);
                continue;
            }
            for (var g = glossaries.Count - 1; g >= 0; g--)
                if (Text(glossaries[g]?["dictionary"]) == PersonalDictionaryTitle) glossaries.RemoveAt(g);
        }
    }

    // Each glossary gets `tags`: its definitionTags expanded from the dictionary's
    // tag bank as Yomitan's Translator does (engine-service.js expandDefinitionTags).
    private void WithDefinitionTags(JsonArray results)
    {
        var banks = TagBanks();
        foreach (var glossary in results.SelectMany(r => r?["term"]?["glossaries"] as JsonArray ?? []).OfType<JsonObject>())
        {
            banks.TryGetValue(Text(glossary["dictionary"]), out var bank);
            glossary["tags"] = ExpandDefinitionTags(Text(glossary["definitionTags"]), bank);
        }
    }

    public static JsonArray ExpandDefinitionTags(string definitionTags, IReadOnlyDictionary<string, JsonObject>? bank)
    {
        var tags = new List<(string Name, string Category, double Order, double Score, string Notes)>();
        foreach (var name in ParseTagList(definitionTags).Distinct())
        {
            var colon = name.IndexOf(':');
            JsonObject? entry = null;
            bank?.TryGetValue(colon < 0 ? name : name[..colon], out entry);
            var category = Text(entry?["category"]);
            tags.Add((name, category.Length > 0 ? category : "default", Number(entry?["order"]), Number(entry?["score"]), Text(entry?["notes"])));
        }
        var collator = CultureInfo.GetCultureInfo("en-US").CompareInfo;
        tags.Sort((left, right) => left.Order != right.Order
            ? left.Order.CompareTo(right.Order)
            : collator.Compare(left.Name, right.Name, CompareOptions.None));
        return new JsonArray(tags.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name, ["category"] = t.Category, ["order"] = t.Order, ["score"] = t.Score, ["notes"] = t.Notes,
        }).ToArray());
    }

    // Yomitan's _splitField: tag lists split on U+0020 only.
    public static IEnumerable<string> ParseTagList(string value) => value.Split(' ').Where(s => s.Length > 0);

    private Dictionary<string, Dictionary<string, JsonObject>> TagBanks()
    {
        lock (native.Gate)
        {
            if (tagBanks is not null) return tagBanks;
            var banks = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.Ordinal);
            if (JsonNode.Parse(native.Tags()) is JsonArray dictionaries)
            {
                foreach (var dictionary in dictionaries.OfType<JsonObject>())
                {
                    var bank = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                    // Yomitan's findTagMetaBulk answers a name with its first row.
                    foreach (var tag in (dictionary["tags"] as JsonArray ?? []).OfType<JsonObject>())
                        bank.TryAdd(Text(tag["name"]), tag);
                    banks[Text(dictionary["dictionary"])] = bank;
                }
            }
            return tagBanks = banks;
        }
    }

    private void WithFurigana(JsonArray results)
    {
        if (furigana is null) return;
        foreach (var term in results.Select(r => r?["term"]).OfType<JsonObject>())
        {
            if (furigana.For(Text(term["expression"]), Text(term["reading"])) is not { } split) continue;
            term["furigana"] = new JsonArray(split.Select(s => (JsonNode)new JsonObject { ["text"] = s.Text, ["reading"] = s.Reading }).ToArray());
        }
    }

    // -------------------------------------------------------------- hd_segment

    /// <summary>engine-service.js hd_segment: <c>{segments: [{id, spans}]}</c>.</summary>
    public JsonObject Segment(JsonObject message)
    {
        var scanLength = ClampInt(message["scanLength"], 1, 64, 16);
        var options = message["options"] as JsonObject;
        var personalOff = options?["personalDictionary"] is JsonValue p && p.TryGetValue<bool>(out var personal) && !personal;
        var optionsJson = LookupOptionsJson(options, personalOff ? PersonalDictionaryTitle : null);
        var segments = new JsonArray();
        foreach (var chunk in (message["chunks"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var text = ToWellFormed(BoundedText(chunk["text"], "segment text", MaxLookupTextBytes));
            JsonNode spans = new JsonArray();
            if (text.Length > 0)
            {
                var parsed = JsonNode.Parse(native.Segment(text, scanLength, optionsJson));
                spans = parsed?["spans"] as JsonArray ?? throw new OfflineEngineException("hdw_segment returned a malformed response");
                parsed!.AsObject().Remove("spans"); // detach, so it can join the reply
            }
            segments.Add(new JsonObject { ["id"] = chunk["id"]?.DeepClone(), ["spans"] = spans });
        }
        return new JsonObject { ["segments"] = segments };
    }

    // ---------------------------------------------------------------- hd_kanji

    /// <summary>engine-service.js hd_kanji for a request without a profile: <c>{kanji}</c>.</summary>
    public JsonObject Kanji(JsonObject message)
    {
        var character = BoundedText(message["character"], "kanji text", MaxLookupTextBytes);
        if (character.Length == 0) return new JsonObject { ["kanji"] = null };
        var kanji = JsonNode.Parse(native.Kanji(character)) as JsonObject;
        if (kanji?["character"] is not JsonValue c || !c.TryGetValue<string>(out var found) || kanji["entries"] is not JsonArray)
            throw new OfflineEngineException("hdw_kanji returned a malformed lookup response");
        return new JsonObject { ["kanji"] = found.Length == 0 ? null : kanji };
    }

    // ---------------------------------------------------------------- hd_media

    /// <summary>engine-service.js hd_media: the first spelling of the path the dictionary has.</summary>
    public byte[]? Media(string dictionary, string path)
    {
        if (dictionary.Length == 0 || path.Length == 0) return null;
        foreach (var candidate in MediaPathCandidates(path))
        {
            if (native.Media(dictionary, candidate) is { Length: > 0 } bytes) return bytes;
        }
        return null;
    }

    // NFC/NFD and percent-decoded spellings of a media path (mediaPathCandidates).
    public static IReadOnlyList<string> MediaPathCandidates(string path)
    {
        var candidates = new List<string> { path };
        var decoded = path;
        if (path.Contains('%'))
        {
            try
            {
                decoded = Uri.UnescapeDataString(path);
                candidates.Add(decoded);
            }
            catch (UriFormatException)
            {
                // Not percent-encoded; keep the raw path only.
            }
        }
        foreach (var source in new[] { path, decoded }.Distinct())
        {
            candidates.Add(source.Normalize(NormalizationForm.FormC));
            candidates.Add(source.Normalize(NormalizationForm.FormD));
        }
        return candidates.Distinct().ToList();
    }

    // ---------------------------------------------------------------- helpers

    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    private static double Number(JsonNode? node) => JsonNumber.Or(node, 0);

    private static int ClampInt(JsonNode? node, int min, int max, int fallback)
    {
        if (!JsonNumber.TryGet(node, out var number) || !double.IsFinite(number)) return fallback;
        return (int)Math.Clamp(Math.Truncate(number), min, max);
    }

    private static string BoundedText(JsonNode? value, string label, int maxBytes)
    {
        var text = Text(value);
        if (text.Contains('\0')) throw new OfflineEngineException($"{label} contains NUL");
        if (text.Length * 3 > maxBytes && Encoding.UTF8.GetByteCount(text) > maxBytes)
            throw new OfflineEngineException($"{label} exceeds the {maxBytes}-byte limit");
        return text;
    }

    // String.prototype.toWellFormed: a lone surrogate becomes U+FFFD.
    private static string ToWellFormed(string text)
    {
        char[]? copy = null;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (!char.IsSurrogate(text[i])) continue;
            copy ??= text.ToCharArray();
            copy[i] = '�';
        }
        return copy is null ? text : new string(copy);
    }

    public void Dispose() => native.Dispose();
}
