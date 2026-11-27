using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HachidoriFfxiv.Anki;

/// <summary>
/// Built-in field mapping presets for popular Japanese mining note types (Lapis, JPMN, Senren, Kaishi)
/// plus smart auto-mapping for arbitrary Anki note types and a sample FFXIV preview request.
/// </summary>
public static class StandaloneAnkiTemplates
{
    public sealed record PresetDefinition(
        string Id,
        string Name,
        string Description,
        IReadOnlyDictionary<string, string> CoreFields,
        string SentenceAudioField,
        string GifField,
        string TranslationField);

    public static readonly IReadOnlyList<PresetDefinition> Presets =
    [
        new(
            "lapis",
            "Lapis",
            "Popular modern JP mining note type (Expression, Sentence, MainDefinition, Picture, SentenceAudio)",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Expression"] = "{expression}",
                ["ExpressionFurigana"] = "{furigana-plain}",
                ["ExpressionReading"] = "{reading}",
                ["ExpressionAudio"] = "{audio}",
                ["SelectionText"] = "{popup-selection-text}",
                ["MainDefinition"] = "{glossary-first}",
                ["Sentence"] = "{sentence}",
                ["SentenceFurigana"] = "{sentence-furigana-plain}",
                ["Picture"] = "{screenshot}",
                ["Glossary"] = "{glossary}",
                ["PitchPosition"] = "{pitch-position}",
                ["PitchCategories"] = "{pitch-accent-categories}",
                ["Frequency"] = "{frequencies}",
                ["FreqSort"] = "{frequency-harmonic-rank}",
                ["MiscInfo"] = "{document-title}",
            },
            SentenceAudioField: "SentenceAudio",
            GifField: "Picture",
            TranslationField: "SentenceTranslation"),

        new(
            "jpmn",
            "JP Mining Note (JPMN)",
            "Classic JPMN layout (Word, WordReading, PrimaryDefinition, Sentence, Picture, SentenceAudio)",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Word"] = "{expression}",
                ["WordReading"] = "{reading}",
                ["WordReadingHiragana"] = "{reading}",
                ["WordFurigana"] = "{furigana-plain}",
                ["WordAudio"] = "{audio}",
                ["Sentence"] = "{sentence}",
                ["SentenceFurigana"] = "{sentence-furigana-plain}",
                ["PrimaryDefinition"] = "{jpmn-primary-definition}",
                ["ExtraDefinitions"] = "{glossary}",
                ["Picture"] = "{screenshot}",
                ["PAOverride"] = "{pitch-position}",
                ["PADict"] = "{pitch-accent-graphs}",
                ["FrequencySort"] = "{frequency-harmonic-rank}",
                ["Frequencies"] = "{frequencies}",
                ["AdditionalNotes"] = "{document-title}",
            },
            SentenceAudioField: "SentenceAudio",
            GifField: "Picture",
            TranslationField: "SentenceTranslation"),

        new(
            "senren",
            "Senren",
            "Senren note type (word, wordReading, sentence, definition, picture, sentenceAudio)",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["word"] = "{expression}",
                ["wordReading"] = "{reading}",
                ["wordFurigana"] = "{furigana-plain}",
                ["wordAudio"] = "{audio}",
                ["sentence"] = "{sentence}",
                ["sentenceFurigana"] = "{sentence-furigana-plain}",
                ["definition"] = "{glossary-first}",
                ["glossary"] = "{glossary}",
                ["picture"] = "{screenshot}",
                ["pitchPosition"] = "{pitch-position}",
                ["pitch"] = "{pitch-accent-graphs}",
                ["freqSort"] = "{frequency-harmonic-rank}",
                ["frequencies"] = "{frequencies}",
                ["miscInfo"] = "{document-title}",
            },
            SentenceAudioField: "sentenceAudio",
            GifField: "picture",
            TranslationField: "sentenceTranslation"),

        new(
            "kaishi",
            "Kaishi 1.5k",
            "Kaishi deck note type (Word, Word Reading, Word Meaning, Sentence, Word Audio, Sentence Audio, Picture)",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Word"] = "{expression}",
                ["Word Reading"] = "{reading}",
                ["Word Furigana"] = "{furigana-plain}",
                ["Word Meaning"] = "{glossary-first}",
                ["Word Audio"] = "{audio}",
                ["Sentence"] = "{sentence}",
                ["Sentence Furigana"] = "{sentence-furigana-plain}",
                ["Picture"] = "{screenshot}",
                ["Pitch Accent"] = "{pitch-position}",
                ["Notes"] = "{document-title}",
            },
            SentenceAudioField: "Sentence Audio",
            GifField: "Picture",
            TranslationField: "Sentence Meaning"),
    ];

    public readonly record struct MappingResult(int MappedCount, string? SentenceAudioField, string? GifField, string? TranslationField);

    /// <summary>Applies a named preset onto <paramref name="target"/>, filtering to actual model fields when provided.</summary>
    public static MappingResult ApplyPreset(StandaloneAnkiConfig target, PresetDefinition preset, IReadOnlyList<string>? modelFields = null)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? audioField, gifField, tlField;
        if (modelFields is { Count: > 0 })
        {
            foreach (var field in modelFields)
            {
                if (preset.CoreFields.TryGetValue(field, out var marker))
                    map[field] = marker;
            }
            audioField = MatchField(modelFields, preset.SentenceAudioField);
            gifField = MatchField(modelFields, preset.GifField);
            var tl = MatchField(modelFields, preset.TranslationField);
            tlField = tl.Length > 0 ? tl : null;
        }
        else
        {
            foreach (var (k, v) in preset.CoreFields)
                map[k] = v;
            audioField = preset.SentenceAudioField;
            gifField = preset.GifField;
            tlField = preset.TranslationField;
        }

        target.Fields = map;
        return new MappingResult(map.Count, audioField, gifField, tlField);
    }

    /// <summary>Infers reasonable markers for any arbitrary Anki note type from its field names.</summary>
    public static MappingResult AutoMapFields(StandaloneAnkiConfig target, IReadOnlyList<string> modelFields)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedFirst = false;
        string? audioField = null, gifField = null, tlField = null;

        foreach (var field in modelFields)
        {
            var trimmed = field.Trim();
            var lower = trimmed.ToLowerInvariant();
            string? marker = null;

            // Skip card-type toggle flags (e.g. IsAudioCard, IsSentenceCard, IsClickCard, IsWordAndSentenceCard)
            // and auxiliary picture slots (DefinitionPicture) that should stay user-controlled.
            if (Regex.IsMatch(lower, @"^is[\s_-]?(audio|sentence|word|click|vocab|targeted|card)"))
                continue;

            if (Regex.IsMatch(lower, @"^(expression|word|vocab(ulary)?|front|kanji|target[\s_-]*word)$"))
            {
                marker = "{expression}";
                usedFirst = true;
            }
            else if (Regex.IsMatch(lower, @"sentence[\s_-]*furigana"))
                marker = "{sentence-furigana-plain}";
            else if (Regex.IsMatch(lower, @"furigana"))
                marker = "{furigana-plain}";
            else if (Regex.IsMatch(lower, @"reading|kana"))
                marker = "{reading}";
            else if (Regex.IsMatch(lower, @"sent(ence)?[\s_-]*audio|voice"))
            {
                audioField = field;
                continue;
            }
            else if (Regex.IsMatch(lower, @"^audio$|^(expression|word|vocab|term)[\s_-]*audio$|pronunciation"))
                marker = "{audio}";
            else if (Regex.IsMatch(lower, @"sent(ence)?[\s_-]*(translation|meaning|eng)"))
            {
                tlField = field;
                continue;
            }
            else if (Regex.IsMatch(lower, @"^(sentence|example|context)$"))
                marker = "{sentence}";
            else if (Regex.IsMatch(lower, @"\bgif\b|anim"))
            {
                gifField = field;
                continue;
            }
            else if (Regex.IsMatch(lower, @"def(inition)?[\s_-]*(pic(ture)?|img|image)"))
                continue;
            else if (Regex.IsMatch(lower, @"picture|image|screenshot|^pic$|^img$"))
                marker = "{screenshot}";
            else if (Regex.IsMatch(lower, @"primary[\s_-]*def|main[\s_-]*def|meaning|back"))
                marker = "{glossary-first}";
            else if (Regex.IsMatch(lower, @"glossary|def(inition)?"))
                marker = "{glossary}";
            else if (Regex.IsMatch(lower, @"pitch[\s_-]*(graph|svg|diagram)"))
                marker = "{pitch-accent-graphs}";
            else if (Regex.IsMatch(lower, @"pitch[\s_-]*cat"))
                marker = "{pitch-accent-categories}";
            else if (Regex.IsMatch(lower, @"pitch"))
                marker = "{pitch-position}";
            else if (Regex.IsMatch(lower, @"freq[\s_-]*sort|harmonic"))
                marker = "{frequency-harmonic-rank}";
            else if (Regex.IsMatch(lower, @"freq"))
                marker = "{frequencies}";
            else if (Regex.IsMatch(lower, @"misc|notes|source|title"))
                marker = "{document-title}";

            if (marker is not null)
                map[field] = marker;
        }

        // Anki requires the first field to be non-empty.
        if (!usedFirst && modelFields.Count > 0 && !map.ContainsKey(modelFields[0]))
            map[modelFields[0]] = "{expression}";

        target.Fields = map;
        return new MappingResult(map.Count, audioField, gifField, tlField);
    }

    /// <summary>
    /// Copies deck, note type, duplicate behavior, tags, and field mappings from an existing
    /// synced Hachidori template JSON object into <paramref name="target"/>.
    /// </summary>
    public static bool SeedFromTemplateJson(StandaloneAnkiConfig target, JsonObject? templateJson)
    {
        if (templateJson is null)
            return false;
        var deck = AnkiFields.Str(templateJson["deck"]).Trim();
        var model = AnkiFields.Str(templateJson["model"]).Trim();
        if (deck.Length == 0 || model.Length == 0)
            return false;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (templateJson["fieldTemplates"] is JsonObject ft)
        {
            foreach (var (name, node) in ft)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var val = AnkiFields.Str(node?["value"]);
                fields[name] = val;
            }
        }
        if (fields.Count == 0)
            return false;

        target.Deck = deck;
        target.Model = model;
        var behavior = AnkiFields.Str(templateJson["duplicateBehavior"]).Trim();
        if (behavior.Length > 0)
            target.DuplicateBehavior = behavior;
        var scope = AnkiFields.Str(templateJson["duplicateScope"]).Trim();
        if (scope.Length > 0)
            target.DuplicateScope = scope;
        if (templateJson["tags"] is JsonArray tagsArr)
            target.Tags = string.Join(" ", tagsArr.Select(AnkiFields.Str).Where(s => s.Length > 0));
        target.Fields = fields;
        return true;
    }

    /// <summary>Builds a realistic FFXIV sample mining request for Dry-Run Card Preview.</summary>
    public static JsonObject SamplePreviewRequest(string documentTitle) => JsonNode.Parse($$"""
    {
      "matched": "冒険者",
      "deinflected": "冒険者",
      "trace": [],
      "term": {
        "expression": "冒険者",
        "reading": "ぼうけんしゃ",
        "rules": "n",
        "score": 0,
        "furigana": [
          {"text": "冒", "reading": "ぼう"},
          {"text": "険", "reading": "けん"},
          {"text": "者", "reading": "しゃ"}
        ],
        "glossaries": [
          {
            "dictionary": "JMdict",
            "glossary": "[\"adventurer\"]",
            "definitionTags": "n",
            "termTags": "",
            "tags": [{"name": "n", "category": "partOfSpeech", "order": 0, "score": 0, "notes": "noun"}]
          }
        ],
        "frequencies": [
          {"dictionary": "JPDB", "frequencyMode": "rank-based", "frequencies": [{"value": 2400, "displayValue": "2400"}]}
        ],
        "pitches": [
          {"dictionary": "Kanjium", "pitches": [{"position": 3, "pattern": "", "nasal": [], "devoice": []}], "transcriptions": []}
        ]
      },
      "generation": 1,
      "sentence": "冒険者さん、お待たせしました！",
      "matchOffset": 0,
      "searchQuery": "冒険者",
      "popupSelectionText": "",
      "documentTitle": {{JsonValue.Create(string.IsNullOrWhiteSpace(documentTitle) ? "FINAL FANTASY XIV" : documentTitle).ToJsonString()}},
      "pageUrl": "",
      "dictionaryAliases": {},
      "dictionaryIds": {},
      "frequencyDictionaries": ["JPDB"],
      "captureUnavailable": ["sentence-audio", "gif"],
      "screenshot": {"token": "preview", "filename": "hachidori-preview.jpg"}
    }
    """)!.AsObject();

    private static string MatchField(IReadOnlyList<string> fields, string wanted)
    {
        var hit = fields.FirstOrDefault(f => string.Equals(f, wanted, StringComparison.OrdinalIgnoreCase));
        return hit ?? string.Empty;
    }
}
