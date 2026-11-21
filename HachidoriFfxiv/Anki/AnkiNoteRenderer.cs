using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Offline;

namespace HachidoriFfxiv.Anki;

/// <summary>Note fields from a mining request and Hachidori's saved settings, with no Anki involved.</summary>
public static class AnkiNoteRenderer
{
    public sealed record PlannedImage(string Dictionary, string Path, string Filename);

    private static readonly HashSet<string> ImageExtensions = ["png", "jpg", "jpeg", "gif", "webp", "svg", "avif", "bmp", "ico", "tiff"];
    private const string CompactGlossStyles = """
        .yomitan-glossary ul[data-sc-content="glossary"] > li:not(:first-child)::before {
          white-space: pre-wrap;
          content: ' | ';
          display: inline;
          color: #777777;
        }
        .yomitan-glossary ul[data-sc-content="glossary"] > li {
          display: inline;
        }
        .yomitan-glossary ul[data-sc-content="glossary"] {
          display: inline;
          list-style: none;
          padding-left: 0;
        }
        """;

    /// <summary>
    /// A note's fields for a mining request, as Hachidori's offscreen renderer
    /// (anki-resources.js buildAnkiResourceFields) makes them. <paramref name="images"/>
    /// collects the dictionary images the fields name; without
    /// <paramref name="splitSentence"/> the sentence furigana markers stay plain,
    /// as in the relay API's /ankiFields.
    /// </summary>
    public static Dictionary<string, string> RenderFields(JsonObject snapshot, JsonObject request, IReadOnlyDictionary<string, FieldTemplate> templates,
        string audio, LocalEngine local, List<PlannedImage> images, bool splitSentence = true)
    {
        var options = snapshot["options"] as JsonObject ?? new JsonObject();
        var compactGlossaries = AnkiFields.Str(options["glossaryLayoutMode"]) == "compact-popup-anki";
        // The paths Hachidori loaded each enabled dictionary from name its images' Anki files.
        var paths = ((snapshot["dictionaryState"]?["dictionaries"] as JsonArray) ?? []).OfType<JsonObject>()
            .Where(d => d["enabled"] is not JsonValue e || e.GetValueKind() != JsonValueKind.False)
            .GroupBy(d => AnkiFields.Str(d["title"])).ToDictionary(g => g.Key, g => AnkiFields.Str(g.First()["path"]));
        string? FilenameFor(string dictionary, string path)
        {
            if (!paths.TryGetValue(dictionary, out var generationPath) || generationPath.Length == 0)
                throw new InvalidOperationException($"The dictionary generation is no longer available: {dictionary}");
            var existing = images.FirstOrDefault(i => i.Dictionary == dictionary && i.Path == path);
            if (existing is not null) return existing.Filename;
            var suffix = path.Split('.').Last().ToLowerInvariant();
            var filename = MediaFilename(Encoding.UTF8.GetBytes($"[{JsString(generationPath)},{JsString(path)}]"),
                ImageExtensions.Contains(suffix) ? suffix : "bin");
            images.Add(new PlannedImage(dictionary, path, filename));
            return filename;
        }
        AnkiGlossary? rich = null, plain = null;
        string Definition(DefinitionOptions definition)
        {
            if (definition.Plain)
                return (plain ??= new AnkiGlossary(request, null, [], compactGlossaries)).Render(definition);
            rich ??= new AnkiGlossary(request, FilenameFor, local.Styles(), compactGlossaries);
            var html = rich.Render(definition);
            return compactGlossaries && html.EndsWith("</div>", StringComparison.Ordinal) ? CompactStyle(html) : html;
        }
        return AnkiFields.Build(request, templates, Definition, audio, splitSentence ? SentenceSegments(options, request, templates, local) : null);
    }

    // Under Compact popup and Anki the glossary carries Yomitan's compact gloss style.
    private static string CompactStyle(string html)
    {
        var details = html.LastIndexOf("<small class=\"yomitan-glossary-details\">", StringComparison.Ordinal);
        var at = details >= 0 ? details : html.Length - "</div>".Length;
        return html[..at] + $"<style>{CompactGlossStyles}</style>" + html[at..];
    }

    // The words of the sentence around the match, split as a hover splits them, for the sentence furigana markers.
    private static IReadOnlyList<IReadOnlyList<JsonObject>>? SentenceSegments(JsonObject options, JsonObject request,
        IReadOnlyDictionary<string, FieldTemplate> templates, LocalEngine local)
    {
        if (!templates.Values.Any(t => AnkiFields.MarkerNames(t.Value).Any(m => m is "sentence-furigana" or "sentence-furigana-plain"))) return null;
        var sentence = AnkiFields.Str(request["sentence"]);
        var offset = (int)Math.Clamp(JsonNumber.Or(request["matchOffset"], 0), 0, sentence.Length);
        var end = Math.Min(sentence.Length, offset + AnkiFields.Str(request["matched"]).Length);
        var reply = local.Segment(new JsonObject
        {
            ["chunks"] = new JsonArray(new JsonObject { ["id"] = 0, ["text"] = sentence[..offset] }, new JsonObject { ["id"] = 1, ["text"] = sentence[end..] }),
            ["scanLength"] = JsonNumber.Or(options["scanLength"], 16),
            ["options"] = new JsonObject
            {
                ["frequencyDictionary"] = options["frequencyDictionary"]?.DeepClone(),
                ["frequencyOrder"] = options["frequencyOrder"]?.DeepClone(),
                ["personalDictionary"] = options["personalDictionaryEnabled"]?.DeepClone(),
            },
        });
        return (reply["segments"] as JsonArray ?? []).Select(s => (IReadOnlyList<JsonObject>)(s?["spans"] as JsonArray ?? []).OfType<JsonObject>().ToList()).ToList();
    }

    // Under Compact popup and Anki the glossary carries Yomitan's compact gloss style.
    /// <summary>anki-resources.js ankiMediaFilename: hachidori_&lt;SHA-256&gt;.&lt;extension&gt;.</summary>
    public static string MediaFilename(byte[] bytes, string extension) => $"hachidori_{Convert.ToHexStringLower(SHA256.HashData(bytes))}.{extension}";

    /// <summary>JSON.stringify of a string: only quotes, backslashes and control characters are escaped.</summary>
    public static string JsString(string value)
    {
        var builder = new StringBuilder("\"");
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    var lone = char.IsSurrogate(c) && !(char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                               && !(char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(value[i - 1]));
                    if (c < 0x20 || lone) builder.Append($"\\u{(int)c:x4}");
                    else builder.Append(c);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }
}
