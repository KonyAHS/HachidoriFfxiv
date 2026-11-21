using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Offline;

namespace HachidoriFfxiv.Anki;

/// <summary>A note field's template: its text with {markers}, and how it updates an existing note.</summary>
public sealed record FieldTemplate(string Value, string OverwriteMode = "coalesce");

/// <summary>
/// Hachidori's Anki Template markers (anki-templates.js) and their values
/// (anki-values.js buildAnkiFields), in Hachidori 0.5.2, for a note mined
/// without a profile language.
/// </summary>
public static partial class AnkiFields
{
    public static readonly string[] CoreMarkers =
    [
        "expression", "reading", "character", "onyomi", "kunyomi", "stroke-count", "furigana", "furigana-plain", "dictionary", "dictionary-alias",
        "definition", "glossary", "glossary-brief", "glossary-no-dictionary", "glossary-plain", "glossary-plain-no-dictionary",
        "glossary-first", "glossary-first-brief", "glossary-first-no-dictionary", "main-definition", "jpmn-primary-definition",
        "conjugation", "part-of-speech", "phonetic-transcriptions", "tags", "popup-selection-text", "search-query", "document-title",
        "url", "url-plain", "miscInfo",
        "sentence", "sentence-furigana", "sentence-furigana-plain", "cloze-prefix", "cloze-body", "cloze-suffix",
        "frequency", "frequencies", "frequency-harmonic-rank", "frequency-harmonic-occurrence", "frequency-average-rank",
        "frequency-average-occurrence", "pitch", "pitch-position", "pitch-accent-positions", "pitch-categories",
        "pitch-accent-categories", "pitch-accent-graphs", "pitch-accent-graphs-jj",
        "audio", "screenshot", "sentence-audio", "gif",
    ];

    /// <summary>Markers whose values offline mining cannot produce yet; a template using one stays live-only.</summary>
    public static readonly HashSet<string> UnsupportedOffline = ["pitch-accent-graphs", "pitch-accent-graphs-jj"];

    private static readonly Dictionary<string, string> MarkerAliases = new() { ["pitch-accent"] = "pitch", ["pitch-accents"] = "pitch" };
    private static readonly HashSet<string> Markers = CoreMarkers.Select(m => m.ToLowerInvariant()).Concat(MarkerAliases.Keys).ToHashSet();
    private static readonly string[] DynamicPrefixes = ["single-glossary-", "single-frequency-"];
    private static readonly string[] AnkiFieldSlots = ["expression", "reading", "definition", "sentence", "frequency", "pitch", "audio", "screenshot"];

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakPattern();

    [GeneratedRegex(@"(<style\b[^>]*>[\s\S]*?</style\s*>)", RegexOptions.IgnoreCase)]
    private static partial Regex StyleElement();

    // ---------------------------------------------------------------- templates

    public static List<string> MarkerNames(string template) => MarkerPattern().Matches(template).Select(match =>
    {
        var name = match.Groups[1].Value.ToLowerInvariant();
        return MarkerAliases.GetValueOrDefault(name, name);
    }).ToList();

    private static bool KnownMarker(string value) =>
        Markers.Contains(value) || DynamicPrefixes.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal) && value.Length > prefix.Length);

    public static List<string> TemplateErrors(string template) => MarkerPattern().Matches(template)
        .Where(match => !KnownMarker(match.Groups[1].Value.ToLowerInvariant()))
        .Select(match => $"Unknown marker: {match.Value}").Distinct().ToList();

    public static string EscapeHtml(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&#x27;");

    // Anki's cloze tokenizer reads {{c1::…}} in raw field HTML: braces a value carries are encoded, except inside <style>.
    public static string EncodeClozeBraces(string value) => string.Concat(StyleElement().Split(value)
        .Select((part, index) => index % 2 == 1 ? part : part.Replace("{", "&#123;").Replace("}", "&#125;")));

    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var errors = TemplateErrors(template);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        return string.Join("<br>", BreakPattern().Split(template).SelectMany(segment =>
        {
            var hasMarkers = MarkerPattern().IsMatch(segment);
            var rendered = MarkerPattern().Replace(segment, match =>
            {
                var key = match.Groups[1].Value.ToLowerInvariant();
                return EncodeClozeBraces(values.GetValueOrDefault(key) ?? values.GetValueOrDefault(MarkerAliases.GetValueOrDefault(key, key)) ?? string.Empty);
            });
            return hasMarkers && rendered.Trim().Length == 0 ? [] : new[] { rendered };
        }));
    }

    /// <summary>resolveAnkiTemplates: one template per note-type field, matched case-insensitively.</summary>
    public static (Dictionary<string, FieldTemplate> Templates, List<string> Errors) Resolve(JsonObject config, IReadOnlyList<string> fields)
    {
        var model = Str(config["model"]);
        var errors = new List<string>();
        string Available() => fields.Count == 0 ? string.Empty : $" Its fields are {string.Join(", ", fields.Select(f => $"“{f}”"))}.";
        if (config["fieldTemplates"] is not JsonObject saved)
        {
            // basicTemplates: the semantic field mapping of an older Template.
            var rows = fields.ToDictionary(f => f, _ => new StringBuilder(), StringComparer.Ordinal);
            var canonical = fields.GroupBy(f => f.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            foreach (var semantic in AnkiFieldSlots)
            {
                var name = Str(config["fields"]?[semantic]);
                if (name.Length == 0) continue;
                if (!canonical.TryGetValue(name.ToLowerInvariant(), out var field))
                {
                    errors.Add($"The {semantic} mapping points at field “{name}”, which is unavailable in note type “{model}”.{Available()}");
                    continue;
                }
                var marker = semantic == "pitch" && field.Equals("pitchposition", StringComparison.OrdinalIgnoreCase) ? "pitch-position" : semantic;
                if (rows[field].Length > 0) rows[field].Append("<br>");
                rows[field].Append('{').Append(marker).Append('}');
            }
            return (rows.ToDictionary(r => r.Key, r => new FieldTemplate(r.Value.ToString())), errors);
        }
        var folded = new Dictionary<string, string>();
        foreach (var name in saved.Select(p => p.Key)) folded.TryAdd(name.ToLowerInvariant(), name);
        var used = new HashSet<string>();
        var templates = new Dictionary<string, FieldTemplate>();
        foreach (var field in fields)
        {
            var name = saved.ContainsKey(field) ? field : folded.GetValueOrDefault(field.ToLowerInvariant());
            if (name is null)
            {
                templates[field] = new FieldTemplate(string.Empty);
                continue;
            }
            used.Add(name);
            templates[field] = new FieldTemplate(Str(saved[name]?["value"]), Str(saved[name]?["overwriteMode"]) is { Length: > 0 } mode ? mode : "coalesce");
        }
        errors.AddRange(saved.Select(p => p.Key).Where(f => !used.Contains(f))
            .Select(f => $"Template field “{f}” is unavailable in note type “{model}”.{Available()}"));
        foreach (var (field, template) in templates)
            errors.AddRange(TemplateErrors(template.Value).Select(e => $"Field “{field}”: {e}"));
        return (templates, errors);
    }

    // ------------------------------------------------------------------ values

    private const string GlossaryMarkerPrefix = "single-glossary-";
    private static readonly Dictionary<string, string> ValueAliases = new()
    {
        ["definition"] = "glossary", ["main-definition"] = "glossary-first", ["jpmn-primary-definition"] = "glossary-first",
        ["frequency"] = "frequencies", ["pitch-accent-positions"] = "pitch-position", ["pitch-categories"] = "pitch-accent-categories",
    };
    private static readonly Dictionary<string, string> PartsOfSpeech = new()
    {
        ["v1"] = "Ichidan verb", ["v5"] = "Godan verb", ["vk"] = "Kuru verb", ["vs"] = "Suru verb",
        ["vz"] = "Zuru verb", ["adj-i"] = "I-adjective", ["n"] = "Noun",
    };

    /// <summary>
    /// buildAnkiFields: every field's text from its template and the mining request.
    /// <paramref name="definition"/> renders the glossary markers; <paramref name="audio"/> is {audio}'s sound tag.
    /// </summary>
    public static Dictionary<string, string> Build(JsonObject request, IReadOnlyDictionary<string, FieldTemplate> templates,
        Func<DefinitionOptions, string> definition, string audio = "", IReadOnlyList<IReadOnlyList<JsonObject>>? sentenceSegments = null)
    {
        var term = request["term"] as JsonObject ?? new JsonObject();
        var expression = Str(term["expression"]);
        var reading = Str(term["reading"]);
        var glossaries = (term["glossaries"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var sentence = Str(request["sentence"]);
        var matched = Str(request["matched"]);
        var matchOffset = (int)Math.Clamp(JsonNumber.Or(request["matchOffset"], 0), 0, sentence.Length);
        string[]? sentenceParts = null;
        string[] Parts() => sentenceParts ??= new[]
        {
            Slice(sentence, 0, matchOffset), Slice(sentence, matchOffset, matchOffset + matched.Length), Slice(sentence, matchOffset + matched.Length, null),
        }.Select(EscapeHtml).ToArray();
        string Sentence() { var p = Parts(); return $"{p[0]}<b>{p[1]}</b>{p[2]}"; }
        string SentenceFurigana(bool plain)
        {
            if (sentenceSegments is null) return Sentence();
            var end = matchOffset + matched.Length;
            var prefix = FuriganaHtml(SpanFurigana(Slice(sentence, 0, matchOffset), sentenceSegments[0]), plain);
            var body = FuriganaHtml(MatchedFurigana(expression, reading, term["furigana"], Slice(sentence, matchOffset, end)), plain);
            var suffix = FuriganaHtml(SpanFurigana(Slice(sentence, end, null), sentenceSegments[1]), plain);
            return $"{prefix}<b>{body}</b>{suffix}";
        }
        string FirstDictionary() => glossaries.Count > 0 ? Str(glossaries[0]["dictionary"]) : string.Empty;
        string Alias(string dictionary) => request["dictionaryAliases"] is JsonObject aliases && aliases.ContainsKey(dictionary)
            ? Str(aliases[dictionary]) : dictionary;
        string PageUrl() => request["pageUrl"] is JsonValue u && u.GetValueKind() == JsonValueKind.String ? EscapeHtml(u.GetValue<string>()) : string.Empty;
        var frequencies = (term["frequencies"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var pitches = (term["pitches"] as JsonArray ?? []).OfType<JsonObject>().ToList();

        var table = new Dictionary<string, Func<string>>
        {
            ["expression"] = () => EscapeHtml(expression),
            ["reading"] = () => EscapeHtml(reading),
            // The plugin's requests carry no native kanji entries, so the kanji markers are empty, as live.
            ["character"] = () => string.Empty, ["onyomi"] = () => string.Empty, ["kunyomi"] = () => string.Empty,
            ["stroke-count"] = () => string.Empty,
            ["furigana"] = () => FuriganaHtml(TermFurigana(expression, reading, term["furigana"]), false),
            ["furigana-plain"] = () => FuriganaHtml(TermFurigana(expression, reading, term["furigana"]), true),
            ["dictionary"] = () => EscapeHtml(FirstDictionary()),
            ["dictionary-alias"] = () => EscapeHtml(Alias(FirstDictionary())),
            ["glossary"] = () => definition(new DefinitionOptions()),
            ["glossary-brief"] = () => definition(new DefinitionOptions(Brief: true)),
            ["glossary-no-dictionary"] = () => definition(new DefinitionOptions(NoDictionary: true)),
            ["glossary-plain"] = () => definition(new DefinitionOptions(Plain: true)),
            ["glossary-plain-no-dictionary"] = () => definition(new DefinitionOptions(Plain: true, NoDictionary: true)),
            ["glossary-first"] = () => definition(new DefinitionOptions(FirstOnly: true)),
            ["glossary-first-brief"] = () => definition(new DefinitionOptions(FirstOnly: true, Brief: true)),
            ["glossary-first-no-dictionary"] = () => definition(new DefinitionOptions(FirstOnly: true, NoDictionary: true)),
            ["conjugation"] = () => (request["trace"] as JsonArray ?? []).Select(step => EscapeHtml(Str(step?["name"]))).ToList() is { Count: > 0 } steps
                ? string.Join(" « ", steps) : EscapeHtml(Str(term["rules"])),
            ["part-of-speech"] = () => UniqueTokens(new[] { Str(term["rules"]) }.Concat(glossaries.Select(g => Str(g["termTags"]))))
                .Select(tag => EscapeHtml(PartsOfSpeech.GetValueOrDefault(tag, tag))).ToList() is { Count: > 0 } labels
                ? string.Join(", ", labels) : "Unknown",
            ["tags"] = () => string.Join(", ", UniqueTokens(glossaries.SelectMany(g =>
                    DefinitionTagNames(g).Append(Str(g["termTags"]))))
                .Select(tag => $"<span class=\"tag\" data-details=\"{EscapeHtml(tag)}\">{EscapeHtml(tag)}</span>")),
            ["phonetic-transcriptions"] = () => pitches.SelectMany(g => (g["transcriptions"] as JsonArray ?? []).Select(Str)).Where(t => t.Length > 0)
                .Select(t => $"<li class=\"pronunciation\" data-pronunciation-type=\"phonetic-transcription\">{EscapeHtml(t)}</li>").ToList() is { Count: > 0 } items
                ? $"<ul>{string.Concat(items)}</ul>" : string.Empty,
            ["popup-selection-text"] = () => EscapeHtml(Str(request["popupSelectionText"])).Replace("\n", "<br>\n"),
            ["search-query"] = () => EscapeHtml(Str(request["searchQuery"])),
            ["document-title"] = () => EscapeHtml(Str(request["documentTitle"])),
            ["miscinfo"] = () => EscapeHtml(Str(request["miscInfo"]) is { Length: > 0 } misc ? misc : Str(request["documentTitle"])),
            ["url"] = () => PageUrl() is { Length: > 0 } address ? $"<a href=\"{address}\">{address}</a>" : string.Empty,
            ["url-plain"] = PageUrl,
            ["sentence"] = Sentence,
            ["sentence-furigana"] = () => SentenceFurigana(false),
            ["sentence-furigana-plain"] = () => SentenceFurigana(true),
            ["cloze-prefix"] = () => Parts()[0], ["cloze-body"] = () => Parts()[1], ["cloze-suffix"] = () => Parts()[2],
            ["frequencies"] = () => FrequencyHtml(frequencies, Alias),
            ["frequency-harmonic-rank"] = () => FrequencyAggregate(frequencies, "rank-based", true),
            ["frequency-harmonic-occurrence"] = () => FrequencyAggregate(frequencies, "occurrence-based", true),
            ["frequency-average-rank"] = () => FrequencyAggregate(frequencies, "rank-based", false),
            ["frequency-average-occurrence"] = () => FrequencyAggregate(frequencies, "occurrence-based", false),
            ["pitch"] = () => PitchHtml(pitches),
            ["pitch-position"] = () => string.Join(", ", pitches.SelectMany(g => (g["pitches"] as JsonArray ?? []).OfType<JsonObject>())
                .Select(PitchDownstep).Distinct()),
            ["pitch-accent-categories"] = () => PitchCategories(expression, reading, Str(term["rules"]), pitches),
            ["audio"] = () => audio,
            ["screenshot"] = () => Screenshot(request),
            ["sentence-audio"] = () => request["sentenceAudio"]?["filename"] is JsonValue f && Str(f).Length > 0 && !Unavailable(request, "sentence-audio")
                ? $"[sound:{EscapeHtml(Str(f))}]" : string.Empty,
            ["gif"] = () => request["gif"]?["filename"] is JsonValue g && Str(g).Length > 0
                ? Unavailable(request, "gif") ? string.Empty : $"<img src=\"{EscapeHtml(Str(g))}\">"
                : Screenshot(request),
        };

        var values = new Dictionary<string, string>();
        var glossaryMarkers = templates.Values.SelectMany(t => MarkerNames(t.Value)).Where(n => n.StartsWith(GlossaryMarkerPrefix, StringComparison.Ordinal)).ToHashSet();
        Dictionary<string, DefinitionOptions>? dynamicGlossaries = null;
        Dictionary<string, (string Dictionary, bool Numeric)>? dynamicFrequencies = null;
        string ValueFor(string name)
        {
            if (ValueAliases.TryGetValue(name, out var alias)) return ValueFor(alias);
            if (values.TryGetValue(name, out var cached)) return cached;
            var value = string.Empty;
            if (table.TryGetValue(name, out var compute)) value = compute();
            else if (name.StartsWith(GlossaryMarkerPrefix, StringComparison.Ordinal))
            {
                dynamicGlossaries ??= DynamicGlossaries(request, glossaries, glossaryMarkers);
                if (dynamicGlossaries.TryGetValue(name, out var options)) value = definition(options);
            }
            else if (name.StartsWith("single-frequency-", StringComparison.Ordinal))
            {
                dynamicFrequencies ??= DynamicFrequencies(request);
                if (dynamicFrequencies.TryGetValue(name, out var variant)) value = SingleFrequency(frequencies, variant.Dictionary, variant.Numeric, Alias);
            }
            values[name] = value;
            return value;
        }

        var fields = new Dictionary<string, string>();
        foreach (var (field, template) in templates)
        {
            var markers = new Dictionary<string, string>();
            foreach (var name in MarkerNames(template.Value)) markers[name] = ValueFor(name);
            fields[field] = Render(template.Value, markers);
        }
        return fields;
    }

    private static bool Unavailable(JsonObject request, string kind) =>
        (request["captureUnavailable"] as JsonArray ?? []).Any(n => Str(n) == kind);

    private static string Screenshot(JsonObject request) =>
        request["screenshot"]?["filename"] is JsonValue f && Str(f).Length > 0 && !Unavailable(request, "screenshot")
            ? $"<img src=\"{EscapeHtml(Str(f))}\">" : string.Empty;

    private static IEnumerable<string> DefinitionTagNames(JsonObject glossary) => glossary["tags"] is JsonArray tags
        ? tags.Select(t => Str(t?["name"])) : AnkiGlossary.ParseTagList(Str(glossary["definitionTags"]));

    // Tags split on spaces and commas, each once.
    private static List<string> UniqueTokens(IEnumerable<string> values) =>
        values.SelectMany(v => Regex.Split(v, "[ ,]+").Where(t => t.Length > 0)).Distinct().ToList();

    // ------------------------------------------------------------- frequency

    private static string FrequencyHtml(List<JsonObject> groups, Func<string, string> alias)
    {
        var items = groups.SelectMany(group =>
        {
            var name = alias(Str(group["dictionary"]));
            return (group["frequencies"] as JsonArray ?? []).OfType<JsonObject>().Select(value =>
            {
                var display = value["displayValue"] is JsonValue d && d.GetValueKind() == JsonValueKind.String ? d.GetValue<string>() : JsNumber.Format(JsonNumber.Or(value["value"], 0));
                return $"<li>{EscapeHtml(name)}: {EscapeHtml(display)}</li>";
            });
        }).ToList();
        return items.Count == 0 ? string.Empty : $"<ul style=\"text-align: left;\">{string.Concat(items)}</ul>";
    }

    /// <summary>HDGlossary.frequencyNumber: the display value's leading digits, else the value.</summary>
    public static double FrequencyNumber(JsonObject frequency)
    {
        var display = frequency["displayValue"] is JsonValue d && d.GetValueKind() == JsonValueKind.String ? Regex.Match(d.GetValue<string>(), @"^\d+") : null;
        var number = display is { Success: true } && double.TryParse(display.Value, out var parsed) ? parsed : 0;
        return number > 0 ? number : JsonNumber.Or(frequency["value"], double.NaN);
    }

    private static bool ModeMatches(string? mode, string requested) => mode is not ("rank-based" or "occurrence-based") || mode == requested;

    private static string FrequencyAggregate(List<JsonObject> groups, string mode, bool harmonic)
    {
        var numbers = new List<double>();
        var seen = new HashSet<string>();
        foreach (var group in groups.Where(g => ModeMatches(g["frequencyMode"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : null, mode)))
        {
            if (!seen.Add(Str(group["dictionary"]))) continue;
            var first = (group["frequencies"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault();
            var value = first is null ? double.NaN : FrequencyNumber(first);
            if (double.IsFinite(value) && value > 0) numbers.Add(value);
        }
        if (numbers.Count == 0) return mode == "rank-based" ? "9999999" : "0";
        var total = numbers.Sum(v => harmonic ? 1 / v : v);
        return JsNumber.Format(Math.Floor(harmonic ? numbers.Count / total : total / numbers.Count));
    }

    private static Dictionary<string, (string, bool)> DynamicFrequencies(JsonObject request)
    {
        var variants = new Dictionary<string, (string, bool)>();
        foreach (var dictionary in (request["frequencyDictionaries"] as JsonArray ?? []).Select(Str))
        {
            var key = DictionaryMarker(dictionary);
            if (key.Length == 0) continue;
            variants[$"single-frequency-number-{key}"] = (dictionary, true);
            variants[$"single-frequency-{key}"] = (dictionary, false);
        }
        return variants;
    }

    private static string SingleFrequency(List<JsonObject> frequencies, string dictionary, bool numeric, Func<string, string> alias)
    {
        var groups = frequencies.Where(g => Str(g["dictionary"]) == dictionary && (g["frequencies"] as JsonArray)?.Count > 0).ToList();
        if (!numeric) return FrequencyHtml(groups, alias);
        var value = groups.Count > 0 ? FrequencyNumber((JsonObject)groups[0]["frequencies"]![0]!) : 0;
        return value > 0 ? JsNumber.Format(value) : string.Empty;
    }

    // ------------------------------------------------------------------- pitch

    private static string PitchHtml(List<JsonObject> groups) => string.Join("<br>", groups.Select(group =>
    {
        var descriptions = (group["pitches"] as JsonArray ?? []).OfType<JsonObject>().Select(pitch =>
        {
            var details = new List<string>();
            var nasal = (pitch["nasal"] as JsonArray ?? []).Select(n => JsNumber.Format(JsonNumber.Or(n, 0))).ToList();
            var devoice = (pitch["devoice"] as JsonArray ?? []).Select(n => JsNumber.Format(JsonNumber.Or(n, 0))).ToList();
            if (nasal.Count > 0) details.Add($"nasal {string.Join(",", nasal)}");
            if (devoice.Count > 0) details.Add($"devoice {string.Join(",", devoice)}");
            var pattern = Str(pitch["pattern"]);
            return (pattern.Length > 0 ? pattern : $"position {JsNumber.Format(JsonNumber.Or(pitch["position"], 0))}")
                   + (details.Count > 0 ? $" ({string.Join("; ", details)})" : string.Empty);
        }).ToList();
        descriptions.AddRange((group["transcriptions"] as JsonArray ?? []).Select(Str));
        return descriptions.Count > 0 ? $"<b>{EscapeHtml(Str(group["dictionary"]))}</b>: {string.Join(", ", descriptions.Select(EscapeHtml))}" : string.Empty;
    }).Where(s => s.Length > 0));

    private static object PitchPositions(JsonObject pitch) =>
        Str(pitch["pattern"]) is { Length: > 0 } pattern ? pattern : JsonNumber.Or(pitch["position"], double.NaN);

    /// <summary>HDGlossary.pitchAccentDownstep: the downstep mora, or each one of a pattern.</summary>
    public static string PitchDownstep(JsonObject pitch)
    {
        var positions = PitchPositions(pitch);
        if (positions is string pattern)
        {
            var downsteps = new List<int>();
            for (var i = 1; i < pattern.Length; i++)
                if (pattern[i - 1] == 'H' && pattern[i] == 'L') downsteps.Add(i);
            if (downsteps.Count == 0) downsteps.Add(pattern.StartsWith('L') ? 0 : -1);
            return string.Join(",", downsteps);
        }
        return JsNumber.Format((double)positions);
    }

    private static readonly Regex VerbEnding = new("[うくぐすずつづぬふぶむゆるウクグスズツヅヌフブムユル]$");
    private static readonly Regex SuruEnding = new(@"(?:する|為る|ずる|(?:[㐀-䶿一-鿿豈-﫿]|[\uD840-\uD87E][\uDC00-\uDFFF])[すず])$");
    private static readonly (string Rule, Regex Ending)[] DictionaryFormEndings =
    [
        ("v1", VerbEnding), ("v5", VerbEnding), ("vk", VerbEnding), ("vs", SuruEnding), ("vz", SuruEnding), ("adj-i", new Regex("[いイ]$")),
    ];
    private static readonly HashSet<string> SmallKana = "ゃゅょぁぃぅぇぉゎャュョァィゥェォヮ".Select(c => c.ToString()).ToHashSet();

    private static int MoraCount(string reading)
    {
        var count = 0;
        foreach (var rune in reading.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            var text = rune.ToString();
            var mark = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(rune.Value) is
                System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.EnclosingMark;
            if (count > 0 && (SmallKana.Contains(text) || mark)) continue;
            count++;
        }
        return count;
    }

    /// <summary>HDGlossary.pitchAccentCategory.</summary>
    public static string? PitchCategory(string reading, JsonObject pitch, IReadOnlyCollection<string> wordClasses, string expression)
    {
        var first = PitchDownstep(pitch).Split(',')[0];
        var position = double.TryParse(first, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : double.NaN;
        if (position == 0) return "heiban";
        if (double.IsNaN(position) || position < 0) return null;
        if (PitchPositions(pitch) is string pattern && pattern.IndexOf('H', (int)position) >= 0) return null;
        var classes = wordClasses.ToHashSet();
        if (!(classes.Contains("vs") && classes.Contains("n")) && DictionaryFormEndings.Any(e => classes.Contains(e.Rule) && e.Ending.IsMatch(expression)))
            return "kifuku";
        if (position == 1) return "atamadaka";
        return position >= MoraCount(reading) ? "odaka" : "nakadaka";
    }

    private static string PitchCategories(string expression, string reading, string rules, List<JsonObject> groups)
    {
        var word = reading.Length > 0 ? reading : expression;
        var classes = UniqueTokens([rules]);
        return string.Join(",", groups.SelectMany(g => (g["pitches"] as JsonArray ?? []).OfType<JsonObject>())
            .Select(pitch => PitchCategory(word, pitch, classes, expression)).Where(c => c is not null).Distinct());
    }

    // ---------------------------------------------------------------- furigana

    private static string FuriganaHtml(IEnumerable<FuriganaSegment> segments, bool plain) =>
        string.Concat(segments.Select((segment, index) =>
        {
            if (segment.Reading.Length == 0) return EscapeHtml(segment.Text);
            return plain ? $"{(index > 0 ? " " : string.Empty)}{EscapeHtml(segment.Text)}[{EscapeHtml(segment.Reading)}]"
                : $"<ruby>{EscapeHtml(segment.Text)}<rt>{EscapeHtml(segment.Reading)}</rt></ruby>";
        }));

    /// <summary>HDGlossary.termFurigana: the engine's split when it spells the expression, else the local one.</summary>
    public static IReadOnlyList<FuriganaSegment> TermFurigana(string expression, string reading, JsonNode? furigana)
    {
        if (furigana is JsonArray { Count: > 0 } array && array.All(s => s?["text"] is JsonValue t && t.GetValueKind() == JsonValueKind.String
                && s["reading"] is JsonValue r && r.GetValueKind() == JsonValueKind.String))
        {
            var segments = array.Select(s => new FuriganaSegment(Str(s!["text"]), Str(s["reading"]))).ToList();
            if (string.Concat(segments.Select(s => s.Text)) == expression) return segments;
        }
        return SegmentFurigana(expression, reading);
    }

    public static IReadOnlyList<FuriganaSegment> SegmentFurigana(string expression, string reading) =>
        KanjiFurigana.Distribute(expression, reading) ?? [new FuriganaSegment(expression, reading)];

    /// <summary>HDGlossary.matchedFurigana: the furigana of the text a lookup matched for the term.</summary>
    public static IReadOnlyList<FuriganaSegment> MatchedFurigana(string expression, string reading, JsonNode? furigana, string matched)
    {
        if (matched == expression) return TermFurigana(expression, reading, furigana);
        int StemLength(string text)
        {
            var length = 0;
            while (length < text.Length && length < matched.Length
                   && KanjiFurigana.ToHiragana(text[length].ToString()) == KanjiFurigana.ToHiragana(matched[length].ToString())) length++;
            return length;
        }
        var stem = StemLength(expression);
        var readingStem = StemLength(reading);
        if (readingStem > 0 && readingStem >= stem) return [new FuriganaSegment(matched, string.Empty)];
        var ending = expression[stem..];
        if (stem == 0 || !reading.EndsWith(ending, StringComparison.Ordinal)) return [new FuriganaSegment(matched, reading)];
        return [.. SegmentFurigana(expression[..stem], reading[..(reading.Length - ending.Length)]), new FuriganaSegment(matched[stem..], string.Empty)];
    }

    // The furigana of text from its hd_segment spans: each word read as its first headword.
    private static List<FuriganaSegment> SpanFurigana(string text, IReadOnlyList<JsonObject> spans)
    {
        var segments = new List<FuriganaSegment>();
        var position = 0;
        foreach (var span in spans)
        {
            var start = (int)JsonNumber.Or(span["start"], 0);
            var length = (int)JsonNumber.Or(span["length"], 0);
            if (start > position) segments.Add(new FuriganaSegment(Slice(text, position, start), string.Empty));
            var candidate = (span["candidates"] as JsonArray)?.FirstOrDefault();
            segments.AddRange(MatchedFurigana(Str(candidate?["expression"]), Str(candidate?["reading"]), null, Slice(text, start, start + length)));
            position = start + length;
        }
        if (position < text.Length) segments.Add(new FuriganaSegment(Slice(text, position, null), string.Empty));
        return segments;
    }

    // ------------------------------------------------- single-glossary markers

    /// <summary>anki-values.js dictionaryMarker: a title as a marker name.</summary>
    public static string DictionaryMarker(string name)
    {
        var value = Regex.Replace(name, @"[_\s]", "-");
        value = Regex.Replace(value, @"[^\p{L}\p{N}-]", string.Empty);
        value = Regex.Replace(value, "-+", "-");
        return value.Trim('-').ToLowerInvariant();
    }

    private static readonly (string Ending, DefinitionOptions Options)[] GlossaryVariants =
    [
        ("-brief", new DefinitionOptions(Brief: true)), ("-no-dictionary", new DefinitionOptions(NoDictionary: true)),
        ("-plain", new DefinitionOptions(Plain: true)), ("-plain-no-dictionary", new DefinitionOptions(Plain: true, NoDictionary: true)),
    ];

    private static Dictionary<string, DefinitionOptions> DynamicGlossaries(JsonObject request, List<JsonObject> glossaries, HashSet<string> requested)
    {
        var exact = new Dictionary<string, string>();
        var variants = new Dictionary<string, List<(string Name, DefinitionOptions Options)>>();
        var bases = new HashSet<string>();
        foreach (var name in requested)
        {
            var key = name[GlossaryMarkerPrefix.Length..];
            exact[key] = name;
            bases.Add(key);
            foreach (var (ending, options) in GlossaryVariants)
            {
                if (!key.EndsWith(ending, StringComparison.Ordinal)) continue;
                var baseKey = key[..^ending.Length];
                bases.Add(baseKey);
                if (!variants.TryGetValue(baseKey, out var list)) variants[baseKey] = list = [];
                list.Add((name, options));
            }
        }
        var aliases = request["dictionaryAliases"] as JsonObject;
        var ids = request["dictionaryIds"] as JsonObject;
        var wantsId = bases.Any(k => Regex.IsMatch(k, "^id--[0-9a-f]{32}$"));
        var descriptors = glossaries.Select(g => Str(g["dictionary"])).Distinct().Select(dictionary => (
            Dictionary: dictionary,
            Legacy: DictionaryMarker(dictionary),
            Alias: aliases is not null && aliases.ContainsKey(dictionary) ? DictionaryMarker(Str(aliases[dictionary]).Normalize(NormalizationForm.FormKC)) : string.Empty,
            Id: wantsId && Regex.IsMatch(Str(ids?[dictionary]), "^[0-9a-f]{32}$") ? $"id--{Str(ids![dictionary])}" : string.Empty)).ToList();
        var owners = new Dictionary<string, string?>();
        void Claim(string key, string dictionary)
        {
            if (key.Length == 0 || !bases.Contains(key)) return;
            if (!owners.ContainsKey(key)) owners[key] = dictionary;
            else if (owners[key] != dictionary) owners[key] = null;
        }
        foreach (var d in descriptors)
        {
            Claim(d.Legacy, d.Dictionary);
            if (d.Alias != d.Legacy) Claim(d.Alias, d.Dictionary);
        }
        var resolved = new Dictionary<string, DefinitionOptions>();
        foreach (var phase in new[] { "legacy", "alias", "id" })
        {
            string Identity((string Dictionary, string Legacy, string Alias, string Id) d)
            {
                var key = phase switch { "legacy" => d.Legacy, "alias" => d.Alias, _ => d.Id };
                return phase != "alias" || (owners.TryGetValue(key, out var owner) && owner == d.Dictionary) ? key : string.Empty;
            }
            foreach (var d in descriptors)
            {
                var key = Identity(d);
                if (key.Length > 0 && exact.TryGetValue(key, out var name) && !resolved.ContainsKey(name))
                    resolved[name] = new DefinitionOptions(Dictionary: d.Dictionary);
            }
            foreach (var d in descriptors)
            {
                var key = Identity(d);
                if (key.Length == 0 || !variants.TryGetValue(key, out var matches)) continue;
                foreach (var (name, options) in matches)
                    if (!resolved.ContainsKey(name)) resolved[name] = options with { Dictionary = d.Dictionary };
            }
        }
        return resolved;
    }

    // ------------------------------------------------------------------ helpers

    private static string Slice(string text, int start, int? end)
    {
        var a = Math.Clamp(start, 0, text.Length);
        var b = Math.Clamp(end ?? text.Length, 0, text.Length);
        return a >= b ? string.Empty : text[a..b];
    }

    internal static string Str(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : string.Empty;
}
