using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Offline;

namespace HachidoriFfxiv.Anki;

/// <summary>
/// Generates inline SVG pitch accent step graphs for <c>{pitch-accent-graphs}</c> and
/// <c>{pitch-accent-graphs-jj}</c> so offline and standalone Anki mining matches popular
/// templates (Lapis, JPMN, Senren) without requiring Chrome.
/// </summary>
public static class PitchAccentGraph
{
    private static readonly HashSet<string> SmallKana =
        "ゃゅょぁぃぅぇぉゎャュョァィゥェォヮ".Select(c => c.ToString()).ToHashSet();

    /// <summary>Splits a kana reading into morae (combining digraph small kana with the preceding character).</summary>
    public static List<string> SplitMorae(string reading)
    {
        var morae = new List<string>();
        foreach (var rune in reading.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            var text = rune.ToString();
            var mark = CharUnicodeInfo.GetUnicodeCategory(rune.Value) is
                UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            if (morae.Count > 0 && (SmallKana.Contains(text) || mark))
                morae[^1] += text;
            else
                morae.Add(text);
        }
        return morae;
    }

    public static string Render(string reading, IReadOnlyList<JsonObject> groups, bool showMoraText)
    {
        if (string.IsNullOrWhiteSpace(reading) || groups.Count == 0)
            return string.Empty;

        var morae = SplitMorae(reading);
        if (morae.Count == 0)
            return string.Empty;

        var items = new List<string>();
        foreach (var group in groups)
        {
            var pitches = (group["pitches"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (pitches.Count == 0)
                continue;
            var svgs = new List<string>();
            foreach (var pitch in pitches)
            {
                var svg = RenderSingleSvg(morae, pitch, showMoraText);
                if (svg.Length > 0)
                    svgs.Add(svg);
            }
            if (svgs.Count == 0)
                continue;
            if (svgs.Count == 1)
                items.Add(svgs[0]);
            else
                items.Add($"<ol>{string.Concat(svgs.Select(s => $"<li>{s}</li>"))}</ol>");
        }

        if (items.Count == 0)
            return string.Empty;
        if (items.Count == 1)
            return items[0];
        return $"<ol>{string.Concat(items.Select(i => $"<li>{i}</li>"))}</ol>";
    }

    private static string RenderSingleSvg(List<string> morae, JsonObject pitch, bool showMoraText)
    {
        var high = ComputeHighPattern(morae.Count, pitch);
        if (high is null || high.Length < morae.Count + 1)
            return string.Empty;

        var nasal = (pitch["nasal"] as JsonArray ?? []).Select(n => (int)JsonNumber.Or(n, 0)).ToHashSet();
        var devoice = (pitch["devoice"] as JsonArray ?? []).Select(n => (int)JsonNumber.Or(n, 0)).ToHashSet();

        const int step = 35;
        const int padX = 18;
        const int highY = 18;
        const int lowY = 48;
        var count = morae.Count;
        var width = padX * 2 + count * step;
        var height = showMoraText ? 84 : 66;

        var sb = new StringBuilder(512);
        sb.Append($"<svg class=\"pronunciation-graph\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\">");

        // Solid polyline across word morae.
        sb.Append("<path class=\"pronunciation-graph-line\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"3\" d=\"");
        for (var i = 0; i < count; i++)
        {
            var x = padX + i * step;
            var y = high[i] ? highY : lowY;
            sb.Append(i == 0 ? $"M{x} {y}" : $" L{x} {y}");
        }
        sb.Append("\"/>");

        // Dashed/thin connector to trailing particle mora.
        var lastX = padX + (count - 1) * step;
        var lastY = high[count - 1] ? highY : lowY;
        var nextX = padX + count * step;
        var nextY = high[count] ? highY : lowY;
        sb.Append($"<path class=\"pronunciation-graph-line-tail\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-dasharray=\"4 3\" d=\"M{lastX} {lastY} L{nextX} {nextY}\"/>");

        // Mora dots (filled for normal, ring for downstep/nasal/devoice).
        for (var i = 0; i < count; i++)
        {
            var x = padX + i * step;
            var y = high[i] ? highY : lowY;
            var isDrop = high[i] && !high[i + 1];
            var isDevoiced = devoice.Contains(i + 1);
            var isNasal = nasal.Contains(i + 1);

            if (isDevoiced)
                sb.Append($"<circle class=\"pronunciation-graph-dot-devoiced\" cx=\"{x}\" cy=\"{y}\" r=\"5\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-dasharray=\"2 2\"/>");
            else if (isDrop)
                sb.Append($"<circle class=\"pronunciation-graph-dot-downstep\" cx=\"{x}\" cy=\"{y}\" r=\"6\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\"/><circle cx=\"{x}\" cy=\"{y}\" r=\"3\" fill=\"currentColor\"/>");
            else
                sb.Append($"<circle class=\"pronunciation-graph-dot\" cx=\"{x}\" cy=\"{y}\" r=\"5\" fill=\"currentColor\"/>");

            if (isNasal)
                sb.Append($"<circle class=\"pronunciation-graph-dot-nasal\" cx=\"{x + 7}\" cy=\"{y - 7}\" r=\"2.5\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.5\"/>");

            if (showMoraText)
                sb.Append($"<text x=\"{x}\" y=\"76\" text-anchor=\"middle\" font-size=\"14\" fill=\"currentColor\">{AnkiFields.EscapeHtml(morae[i])}</text>");
        }

        // Trailing particle marker (hollow triangle/circle).
        sb.Append($"<circle class=\"pronunciation-graph-dot-tail\" cx=\"{nextX}\" cy=\"{nextY}\" r=\"4.5\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"/>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Returns a boolean high/low array of length <c>moraCount + 1</c> (including the following particle).</summary>
    internal static bool[]? ComputeHighPattern(int moraCount, JsonObject pitch)
    {
        if (moraCount <= 0)
            return null;

        var pattern = AnkiFields.Str(pitch["pattern"]).Trim();
        if (pattern.Length > 0)
        {
            var result = new bool[moraCount + 1];
            for (var i = 0; i < result.Length; i++)
            {
                var ch = i < pattern.Length ? pattern[i] : pattern[^1];
                result[i] = ch is 'H' or 'h';
            }
            return result;
        }

        var pos = (int)Math.Round(JsonNumber.Or(pitch["position"], -1));
        if (pos < 0)
            return null;

        var high = new bool[moraCount + 1];
        if (pos == 0)
        {
            // Heiban: L H H ... H (particle stays H)
            for (var i = 1; i < high.Length; i++)
                high[i] = true;
        }
        else if (pos == 1)
        {
            // Atamadaka: H L L ... L
            high[0] = true;
        }
        else
        {
            // Nakadaka / Odaka: L H ... H (up to pos-1), then L
            for (var i = 1; i < Math.Min(pos, high.Length); i++)
                high[i] = true;
        }
        return high;
    }
}
