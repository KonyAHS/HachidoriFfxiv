using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace HachidoriFfxiv.Hachidori;

public static class HachidoriPayloads
{
    public static JsonObject Submit(JsonObject request)
    {
        if (request["clientSpeech"] is JsonObject)
            throw new HachidoriException("This audio source requires browser speech recording. Select a downloadable pronunciation source in Hachidori before mining from FFXIV.");
        return new JsonObject { ["request"] = request.DeepClone(), ["clientMedia"] = new JsonObject() };
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
