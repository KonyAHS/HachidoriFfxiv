using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace HachidoriFfxiv.Hachidori;

public readonly record struct WordSpan(int Start, int Length)
{
    public int End => Start + Length;
    public static IReadOnlyList<WordSpan> Parse(JsonNode? reply, string text)
    {
        var spans = reply?["segments"]?[0]?["spans"] as JsonArray;
        if (spans is null) throw new HachidoriException("The host did not return word segments.");
        var result = new List<WordSpan>();
        var previousEnd = 0;
        foreach (var span in spans.OfType<JsonObject>())
        {
            if (span["start"] is not JsonValue startNode || !startNode.TryGetValue<int>(out var start)
                || span["length"] is not JsonValue lengthNode || !lengthNode.TryGetValue<int>(out var length)
                || start < previousEnd || length < 1 || start > text.Length || length > text.Length - start) continue;
            var end = start + length;
            if ((start > 0 && start < text.Length && char.IsLowSurrogate(text[start]))
                || (end < text.Length && char.IsLowSurrogate(text[end]))) continue;
            result.Add(new(start, length));
            previousEnd = end;
        }
        return result;
    }

    public static WordSpan? At(IReadOnlyList<WordSpan> spans, int index)
    {
        foreach (var span in spans)
            if (index >= span.Start && index < span.End) return span;
        return null;
    }
}
