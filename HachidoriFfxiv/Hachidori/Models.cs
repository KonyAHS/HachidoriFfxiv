using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HachidoriFfxiv.Hachidori;

/// <summary>One <c>hd_lookup</c> reply: the engine's LookupResult list plus the dictionary generation it was answered from.</summary>
public sealed class LookupReply
{
    public required string Query { get; init; }
    public required IReadOnlyList<LookupResult> Results { get; init; }
    public long Generation { get; init; }
    public bool IsKanji { get; init; }

    public static readonly LookupReply Empty = new() { Query = string.Empty, Results = [] };
}

/// <summary>hoshidicts' LookupResult, as Hachidori's engine service returns it.</summary>
public sealed class LookupResult
{
    public required JsonObject Raw { get; init; }
    public string Matched { get; init; } = string.Empty;
    public string Deinflected { get; init; } = string.Empty;
    public IReadOnlyList<string> Trace { get; init; } = [];
    public required TermInfo Term { get; init; }

    public static LookupResult Parse(JsonObject raw)
    {
        var term = raw["term"] as JsonObject ?? new JsonObject();
        return new LookupResult
        {
            Raw = raw,
            Matched = Str(raw["matched"]),
            Deinflected = Str(raw["deinflected"]),
            Trace = raw["trace"] is JsonArray trace
                ? trace.OfType<JsonObject>().Select(step => Str(step["name"])).Where(name => name.Length > 0).ToList()
                : [],
            Term = TermInfo.Parse(term),
        };
    }

    internal static string Str(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        _ => node.ToString(),
    };
}

public sealed class TermInfo
{
    public string Expression { get; init; } = string.Empty;
    public string Reading { get; init; } = string.Empty;
    public string Rules { get; init; } = string.Empty;
    public IReadOnlyList<FuriganaSegment>? Furigana { get; init; }
    public IReadOnlyList<GlossaryInfo> Glossaries { get; init; } = [];
    public IReadOnlyList<FrequencyGroup> Frequencies { get; init; } = [];
    public IReadOnlyList<PitchGroup> Pitches { get; init; } = [];

    public static TermInfo Parse(JsonObject term) => new()
    {
        Expression = LookupResult.Str(term["expression"]),
        Reading = LookupResult.Str(term["reading"]),
        Rules = LookupResult.Str(term["rules"]),
        Furigana = term["furigana"] is JsonArray furigana
            ? furigana.OfType<JsonObject>().Select(s => new FuriganaSegment(LookupResult.Str(s["text"]), LookupResult.Str(s["reading"]))).ToList()
            : null,
        Glossaries = term["glossaries"] is JsonArray glossaries
            ? glossaries.OfType<JsonObject>().Select(GlossaryInfo.Parse).ToList()
            : [],
        Frequencies = term["frequencies"] is JsonArray frequencies
            ? frequencies.OfType<JsonObject>().Select(FrequencyGroup.Parse).ToList()
            : [],
        Pitches = term["pitches"] is JsonArray pitches
            ? pitches.OfType<JsonObject>().Select(PitchGroup.Parse).ToList()
            : [],
    };
}

public readonly record struct FuriganaSegment(string Text, string Reading);

public sealed class GlossaryInfo
{
    public string Dictionary { get; init; } = string.Empty;
    public string DefinitionTags { get; init; } = string.Empty;
    public string TermTags { get; init; } = string.Empty;
    public IReadOnlyList<TagInfo> Tags { get; init; } = [];

    /// <summary>The Yomitan glossary: an array of strings / {type:"text"|"structured-content"|"image"} items.</summary>
    public JsonNode? Content { get; init; }

    public static GlossaryInfo Parse(JsonObject glossary)
    {
        JsonNode? content = null;
        var raw = glossary["glossary"];
        if (raw is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try
            {
                content = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                content = JsonValue.Create(text);
            }
        }
        else
        {
            content = raw?.DeepClone();
        }

        return new GlossaryInfo
        {
            Dictionary = LookupResult.Str(glossary["dictionary"]),
            DefinitionTags = LookupResult.Str(glossary["definitionTags"]),
            TermTags = LookupResult.Str(glossary["termTags"]),
            Tags = glossary["tags"] is JsonArray tags
                ? tags.OfType<JsonObject>().Select(t => new TagInfo(
                    LookupResult.Str(t["name"]),
                    LookupResult.Str(t["category"]),
                    LookupResult.Str(t["notes"]))).ToList()
                : [],
            Content = content,
        };
    }
}

public readonly record struct TagInfo(string Name, string Category, string Notes);

public sealed class FrequencyGroup
{
    public string Dictionary { get; init; } = string.Empty;
    public IReadOnlyList<(double Value, string Display)> Values { get; init; } = [];

    public static FrequencyGroup Parse(JsonObject group) => new()
    {
        Dictionary = LookupResult.Str(group["dictionary"]),
        Values = group["frequencies"] is JsonArray values
            ? values.OfType<JsonObject>().Select(f =>
            {
                var number = f["value"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
                var display = LookupResult.Str(f["displayValue"]).Trim();
                return (number, display.Length > 0 ? display : number.ToString("0.##"));
            }).ToList()
            : [],
    };
}

public sealed class PitchGroup
{
    public string Dictionary { get; init; } = string.Empty;
    public IReadOnlyList<PitchInfo> Pitches { get; init; } = [];

    public static PitchGroup Parse(JsonObject group) => new()
    {
        Dictionary = LookupResult.Str(group["dictionary"]),
        Pitches = group["pitches"] is JsonArray pitches
            ? pitches.OfType<JsonObject>().Select(p => new PitchInfo(
                p["position"] is JsonValue v && v.TryGetValue<int>(out var pos) ? pos : -1,
                LookupResult.Str(p["pattern"]),
                Ints(p["nasal"]),
                Ints(p["devoice"]))).ToList()
            : [],
    };

    private static int[] Ints(JsonNode? node) => node is JsonArray array
        ? array.Select(n => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1).Where(i => i >= 0).ToArray()
        : [];
}

public readonly record struct PitchInfo(int Position, string Pattern, int[] Nasal, int[] Devoice);

/// <summary>Anki readiness for one result, from <c>hd_anki_preflight</c>.</summary>
public sealed class AnkiDecision
{
    public string State { get; init; } = string.Empty;
    public bool CanAdd { get; init; }
    public string? Error { get; init; }
    public long[] NoteIds { get; init; } = [];
    public JsonNode? ClientSpeech { get; init; }
}
