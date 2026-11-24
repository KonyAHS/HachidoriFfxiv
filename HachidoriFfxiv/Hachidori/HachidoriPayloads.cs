using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace HachidoriFfxiv.Hachidori;

public static class HachidoriPayloads
{
    /// <summary>
    /// The submit envelope. With a screenshot, the request names it ({token, filename}) and the
    /// client media carries the JPEG; without one, {screenshot} is declared unavailable.
    /// </summary>
    public static JsonObject Submit(JsonObject request, ScreenshotMedia? screenshot = null)
    {
        if (request["clientSpeech"] is JsonObject)
            throw new HachidoriException("This audio source requires browser speech recording. Select a downloadable pronunciation source in Hachidori before mining from FFXIV.");
        var copy = request.DeepClone().AsObject();
        var media = new JsonObject();
        if (screenshot is not null)
        {
            copy["screenshot"] = new JsonObject { ["token"] = screenshot.Token, ["filename"] = screenshot.Filename };
            media["screenshot"] = new JsonObject { ["token"] = screenshot.Token, ["filename"] = screenshot.Filename, ["data"] = screenshot.Base64 };
            if (copy["captureUnavailable"] is JsonArray unavailable)
                copy["captureUnavailable"] = new JsonArray(unavailable.Where(n => n?.ToString() != "screenshot").Select(n => n?.DeepClone()).ToArray());
        }
        else
        {
            copy.Remove("screenshot");
            var unavailable = copy["captureUnavailable"] as JsonArray ?? new JsonArray();
            if (!unavailable.Any(n => n?.ToString() == "screenshot"))
                unavailable.Add("screenshot");
            copy["captureUnavailable"] = unavailable.DeepClone();
        }

        return new JsonObject { ["request"] = copy, ["clientMedia"] = media };
    }

    /// <summary>The <c>hd_lookup</c> message: the same for the live host and the local engine.</summary>
    public static JsonObject Lookup(string text, int maxResults, int scanLength, JsonObject options) => new()
    {
        ["text"] = text,
        ["maxResults"] = maxResults,
        ["scanLength"] = scanLength,
        ["options"] = options.DeepClone(),
    };

    /// <summary>The lookup options a hover sends: the personal dictionary switch and Hachidori's ranking settings.</summary>
    public static JsonObject LookupOptions(bool personal, JsonObject hostOptions)
    {
        var options = new JsonObject { ["personalDictionary"] = personal };
        if (hostOptions["frequencyDictionary"] is JsonValue fd)
            options["frequencyDictionary"] = fd.DeepClone();
        if (hostOptions["frequencyOrder"] is JsonValue fo)
            options["frequencyOrder"] = fo.DeepClone();
        return options;
    }

    public static JsonObject Kanji(string character) => new() { ["character"] = character };

    /// <summary>An <c>hd_lookup</c> reply as the plugin's model.</summary>
    public static LookupReply LookupReply(JsonNode? reply, string text) => new()
    {
        Query = text,
        Results = (reply?["results"] as JsonArray)?.OfType<JsonObject>()
            .Select(r => LookupResult.Parse((JsonObject)r.DeepClone()))
            .ToList() ?? [],
        Generation = reply?["generation"] is JsonValue g && g.TryGetValue<long>(out var gen) ? gen : 0,
    };

    /// <summary>An <c>hd_kanji</c> reply as the plugin's model: one result per kanji dictionary entry.</summary>
    public static LookupReply KanjiReply(JsonNode? reply, string character)
    {
        var results = new List<LookupResult>();
        if (reply?["kanji"]?["entries"] is JsonArray entries)
        {
            foreach (var entry in entries.OfType<JsonObject>())
                results.Add(LookupResult.Parse(KanjiEntryResult(character, entry)));
        }
        return new LookupReply { Query = character, Results = results, IsKanji = true };
    }

    /// <summary>A native kanji entry as a LookupResult, as Hachidori's popup does (kanjiEntryResult).</summary>
    public static JsonObject KanjiEntryResult(string character, JsonObject entry)
    {
        static IEnumerable<string> Tokens(JsonNode? node) => node switch
        {
            JsonArray array => array.Select(LookupResult.Str).Where(s => s.Length > 0),
            JsonValue value => LookupResult.Str(value).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            _ => [],
        };

        var content = new JsonArray();
        var tags = Tokens(entry["tags"]).ToList();
        if (tags.Count > 0)
            content.Add(new JsonObject { ["tag"] = "div", ["data"] = new JsonObject { ["content"] = "tags" }, ["content"] = string.Join(" ", tags) });
        foreach (var (label, key) in new[] { ("On", "onyomi"), ("Kun", "kunyomi") })
        {
            var values = Tokens(entry[key]).ToList();
            if (values.Count > 0)
            {
                content.Add(new JsonObject
                {
                    ["tag"] = "div",
                    ["content"] = new JsonArray(
                        new JsonObject { ["tag"] = "strong", ["content"] = label },
                        $" {string.Join(" · ", values)}"),
                });
            }
        }

        if (entry["definitions"] is JsonArray definitions && definitions.Count > 0)
        {
            content.Add(new JsonObject
            {
                ["tag"] = "ol",
                ["content"] = new JsonArray(definitions.Select(d => (JsonNode)new JsonObject { ["tag"] = "li", ["content"] = d?.DeepClone() }).ToArray()),
            });
        }

        if (entry["stats"] is JsonArray stats && stats.Count > 0)
        {
            content.Add(new JsonObject
            {
                ["tag"] = "table",
                ["content"] = new JsonArray(stats.OfType<JsonObject>().Select(s => (JsonNode)new JsonObject
                {
                    ["tag"] = "tr",
                    ["content"] = new JsonArray(
                        new JsonObject { ["tag"] = "th", ["content"] = LookupResult.Str(s["name"]) },
                        new JsonObject { ["tag"] = "td", ["content"] = LookupResult.Str(s["value"]) }),
                }).ToArray()),
            });
        }

        var glossary = new JsonArray(new JsonObject { ["type"] = "structured-content", ["content"] = content });
        return new JsonObject
        {
            ["matched"] = character,
            ["deinflected"] = character,
            ["trace"] = new JsonArray(),
            ["preprocessorSteps"] = 0,
            ["term"] = new JsonObject
            {
                ["expression"] = character,
                ["reading"] = string.Empty,
                ["rules"] = string.Empty,
                ["score"] = 0,
                ["frequencies"] = entry["frequencies"]?.DeepClone() ?? new JsonArray(),
                ["pitches"] = new JsonArray(),
                ["glossaries"] = new JsonArray(new JsonObject
                {
                    ["dictionary"] = LookupResult.Str(entry["dictionary"]),
                    ["glossary"] = glossary.ToJsonString(),
                    ["definitionTags"] = string.Empty,
                    ["termTags"] = string.Empty,
                }),
            },
        };
    }

    public static JsonObject Segment(string text, int scanLength, bool personal) => new()
    {
        ["chunks"] = new JsonArray(new JsonObject { ["id"] = 0, ["text"] = text }),
        ["scanLength"] = scanLength,
        ["options"] = new JsonObject { ["personalDictionary"] = personal },
    };

    public static byte[]? Media(JsonNode? response)
    {
        var url = response?["dataUrl"]?.ToString();
        if (string.IsNullOrEmpty(url)) return null;
        var comma = url.IndexOf(',');
        if (!url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || comma < 0
            || !url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) || url.Length > 24 * 1024 * 1024)
            throw new HachidoriException("The host returned an unsupported dictionary image payload.");
        return Convert.FromBase64String(url[(comma + 1)..]);
    }

    public static byte[]? Audio(JsonNode? json, string expression, string reading)
    {
        if (json?["error"] is { } error) throw new HachidoriException(error.ToString());
        var media = (json?["audioMedia"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var best = media.FirstOrDefault(m => LookupResult.Str(m["term"]) == expression && LookupResult.Str(m["reading"]) == reading)
            ?? media.FirstOrDefault(m => LookupResult.Str(m["term"]) == expression);
        // Never play audio for a different expression just because it appears first.
        var data = best?["content"]?.ToString();
        return string.IsNullOrEmpty(data) ? null : Convert.FromBase64String(data);
    }
}
