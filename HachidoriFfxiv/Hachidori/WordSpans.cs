using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HachidoriFfxiv.Hachidori;

public readonly record struct WordSpan(int Start, int Length)
{
    public int End => Start + Length;

    /// <summary>
    /// The words of an <c>hd_segment</c> reply. A span whose alternative split
    /// starts with a function word (黙っては|おるまい split as 羽織る) also adds the
    /// words after that particle, so hovering inside them looks them up while the
    /// span's first character still looks up the whole span.
    /// </summary>
    public static IReadOnlyList<WordSpan> Parse(JsonNode? reply, string text)
    {
        var spans = reply?["segments"]?[0]?["spans"] as JsonArray;
        if (spans is null) throw new HachidoriException("The host did not return word segments.");
        var result = new List<WordSpan>();
        var inner = new List<WordSpan>();
        var previousEnd = 0;
        foreach (var span in spans.OfType<JsonObject>())
        {
            if (!TryRange(span, text, out var start, out var length) || start < previousEnd) continue;
            result.Add(new(start, length));
            previousEnd = start + length;
            if (span["alternative"] is JsonArray { Count: > 1 } alternative)
            {
                // If the alternative split contains a particle/function word (either at the start like
                // は|おるまい or inside a phrase like 不語仙|の|座卓), expose the words after that
                // particle as inner spans so hovering them looks them up while the start still opens whole.
                var sawFunctionWord = false;
                foreach (var word in alternative.OfType<JsonObject>())
                {
                    var isFunction = word["functionWord"] is JsonValue fw && fw.GetValueKind() == JsonValueKind.True;
                    if (isFunction)
                    {
                        sawFunctionWord = true;
                        continue;
                    }
                    if (sawFunctionWord && TryRange(word, text, out var wordStart, out var wordLength) && wordStart > start && wordStart + wordLength <= start + length)
                        inner.Add(new(wordStart, wordLength));
                }
            }
        }
        result.AddRange(inner);
        return result;
    }

    private static bool TryRange(JsonObject span, string text, out int start, out int length)
    {
        start = length = 0;
        if (span["start"] is not JsonValue startNode || !startNode.TryGetValue(out start)
            || span["length"] is not JsonValue lengthNode || !lengthNode.TryGetValue(out length)
            || start < 0 || length < 1 || start > text.Length || length > text.Length - start) return false;
        var end = start + length;
        return !((start > 0 && start < text.Length && char.IsLowSurrogate(text[start]))
                 || (end < text.Length && char.IsLowSurrogate(text[end])));
    }

    /// <summary>The word a hover at <paramref name="index"/> looks up: the innermost span that contains it.</summary>
    public static WordSpan? At(IReadOnlyList<WordSpan> spans, int index)
    {
        WordSpan? best = null;
        foreach (var span in spans)
        {
            if (index >= span.Start && index < span.End && (best is null || span.Start > best.Value.Start))
                best = span;
        }
        return best;
    }
}
