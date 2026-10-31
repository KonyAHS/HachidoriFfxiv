using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.Rendering;

public static class Japanese
{
    public static bool IsKana(int cp) => cp is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F;

    public static bool IsKanji(int cp) => cp is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2FA1F
        or 0x3005 or 0x3006 or 0x3007 or 0x303B;

    /// <summary>Text worth scanning: kana, kanji, full-width letters/digits and the iteration mark.</summary>
    public static bool IsJapanese(int cp) => IsKana(cp) || IsKanji(cp) || cp is >= 0xFF10 and <= 0xFF5A;

    public static int CodePointAt(string text, int index) =>
        char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? char.ConvertToUtf32(text[index], text[index + 1]) : text[index];

    private static string ToHiragana(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c);
        return sb.ToString();
    }

    /// <summary>Yomitan's distributeFurigana: ruby only over the kanji runs of a headword.</summary>
    public static IReadOnlyList<FuriganaSegment> DistributeFurigana(string expression, string reading)
    {
        if (string.IsNullOrEmpty(reading) || reading == expression)
            return [new FuriganaSegment(expression, string.Empty)];

        var groups = new List<(bool Kana, string Text)>();
        for (var i = 0; i < expression.Length;)
        {
            var cp = CodePointAt(expression, i);
            var len = cp > 0xFFFF ? 2 : 1;
            var kana = IsKana(cp);
            if (groups.Count > 0 && groups[^1].Kana == kana)
                groups[^1] = (kana, groups[^1].Text + expression.Substring(i, len));
            else
                groups.Add((kana, expression.Substring(i, len)));
            i += len;
        }

        if (groups.All(g => g.Kana))
            return [new FuriganaSegment(expression, string.Empty)];

        var pattern = "^" + string.Concat(groups.Select(g => g.Kana ? Regex.Escape(ToHiragana(g.Text)) : "(.+?)")) + "$";
        var match = Regex.Match(ToHiragana(reading), pattern);
        if (!match.Success)
            return [new FuriganaSegment(expression, reading)];

        var result = new List<FuriganaSegment>();
        var groupIndex = 1;
        var readingOffset = 0;
        foreach (var group in groups)
        {
            if (group.Kana)
            {
                result.Add(new FuriganaSegment(group.Text, string.Empty));
                readingOffset += group.Text.Length;
            }
            else
            {
                var captured = match.Groups[groupIndex++];
                result.Add(new FuriganaSegment(group.Text, reading.Substring(captured.Index, captured.Length)));
                readingOffset = captured.Index + captured.Length;
            }
        }

        return result;
    }

    private const string SmallKana = "ぁぃぅぇぉゃゅょゎゕゖァィゥェォャュョヮヵヶ";

    /// <summary>Splits a reading into morae (small kana join the previous mora).</summary>
    public static List<string> Morae(string reading)
    {
        var morae = new List<string>();
        foreach (var c in reading)
        {
            if (morae.Count > 0 && SmallKana.Contains(c))
                morae[^1] += c;
            else
                morae.Add(c.ToString());
        }

        return morae;
    }

    /// <summary>Yomitan's isMoraPitchHigh for a downstep number or an H/L pattern.</summary>
    public static bool IsMoraHigh(int moraIndex, PitchInfo pitch)
    {
        if (!string.IsNullOrEmpty(pitch.Pattern))
            return moraIndex < pitch.Pattern.Length && pitch.Pattern[moraIndex] == 'H';
        return pitch.Position switch
        {
            0 => moraIndex > 0,
            1 => moraIndex < 1,
            _ => moraIndex > 0 && moraIndex < pitch.Position,
        };
    }

    public static string DownstepLabel(PitchInfo pitch)
    {
        if (string.IsNullOrEmpty(pitch.Pattern))
            return pitch.Position.ToString();
        var downsteps = new List<int>();
        for (var i = 1; i < pitch.Pattern.Length; i++)
        {
            if (pitch.Pattern[i - 1] == 'H' && pitch.Pattern[i] == 'L')
                downsteps.Add(i);
        }

        if (downsteps.Count == 0)
            downsteps.Add(pitch.Pattern.StartsWith('L') ? 0 : -1);
        return string.Join(",", downsteps);
    }

    /// <summary>Strips characters that never start a word and joins wrapped lines.</summary>
    public static string NormaliseGameText(string text, bool joinLines)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("­", string.Empty);
        if (joinLines)
        {
            // Japanese wraps without spaces; keep breaks only where the game left an empty line.
            var parts = text.Split("\n\n");
            text = string.Join("\n", parts.Select(p => p.Replace("\n", string.Empty)));
        }

        return text.Trim();
    }
}
