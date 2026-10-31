using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HachidoriFfxiv.Rendering;

/// <summary>
/// Rewrites a Latin-script character name (the player's, which the Japanese client inserts
/// as-is) into katakana, so segmentation, lookups and mined sentences see a word instead of
/// single letters.
/// </summary>
public static class NameKana
{
    private static readonly Dictionary<string, string> Syllables = new()
    {
        ["a"] = "ア", ["i"] = "イ", ["u"] = "ウ", ["e"] = "エ", ["o"] = "オ",
        ["ka"] = "カ", ["ki"] = "キ", ["ku"] = "ク", ["ke"] = "ケ", ["ko"] = "コ",
        ["ga"] = "ガ", ["gi"] = "ギ", ["gu"] = "グ", ["ge"] = "ゲ", ["go"] = "ゴ",
        ["sa"] = "サ", ["si"] = "シ", ["su"] = "ス", ["se"] = "セ", ["so"] = "ソ",
        ["za"] = "ザ", ["zi"] = "ジ", ["zu"] = "ズ", ["ze"] = "ゼ", ["zo"] = "ゾ",
        ["ta"] = "タ", ["ti"] = "ティ", ["tu"] = "トゥ", ["te"] = "テ", ["to"] = "ト",
        ["da"] = "ダ", ["di"] = "ディ", ["du"] = "ドゥ", ["de"] = "デ", ["do"] = "ド",
        ["na"] = "ナ", ["ni"] = "ニ", ["nu"] = "ヌ", ["ne"] = "ネ", ["no"] = "ノ",
        ["ha"] = "ハ", ["hi"] = "ヒ", ["hu"] = "フ", ["he"] = "ヘ", ["ho"] = "ホ",
        ["ba"] = "バ", ["bi"] = "ビ", ["bu"] = "ブ", ["be"] = "ベ", ["bo"] = "ボ",
        ["pa"] = "パ", ["pi"] = "ピ", ["pu"] = "プ", ["pe"] = "ペ", ["po"] = "ポ",
        ["ma"] = "マ", ["mi"] = "ミ", ["mu"] = "ム", ["me"] = "メ", ["mo"] = "モ",
        ["ya"] = "ヤ", ["yi"] = "イ", ["yu"] = "ユ", ["ye"] = "イェ", ["yo"] = "ヨ",
        ["ra"] = "ラ", ["ri"] = "リ", ["ru"] = "ル", ["re"] = "レ", ["ro"] = "ロ",
        ["la"] = "ラ", ["li"] = "リ", ["lu"] = "ル", ["le"] = "レ", ["lo"] = "ロ",
        ["wa"] = "ワ", ["wi"] = "ウィ", ["wu"] = "ウ", ["we"] = "ウェ", ["wo"] = "ウォ",
        ["fa"] = "ファ", ["fi"] = "フィ", ["fu"] = "フ", ["fe"] = "フェ", ["fo"] = "フォ",
        ["va"] = "ヴァ", ["vi"] = "ヴィ", ["vu"] = "ヴ", ["ve"] = "ヴェ", ["vo"] = "ヴォ",
        ["ja"] = "ジャ", ["ji"] = "ジ", ["ju"] = "ジュ", ["je"] = "ジェ", ["jo"] = "ジョ",
        ["ca"] = "カ", ["ci"] = "シ", ["cu"] = "ク", ["ce"] = "セ", ["co"] = "コ",
        ["qa"] = "カ", ["qi"] = "キ", ["qu"] = "ク", ["qe"] = "ケ", ["qo"] = "コ",
        ["xa"] = "ザ", ["xi"] = "ジ", ["xu"] = "ズ", ["xe"] = "ゼ", ["xo"] = "ゾ",
        ["sha"] = "シャ", ["shi"] = "シ", ["shu"] = "シュ", ["she"] = "シェ", ["sho"] = "ショ",
        ["cha"] = "チャ", ["chi"] = "チ", ["chu"] = "チュ", ["che"] = "チェ", ["cho"] = "チョ",
        ["tsu"] = "ツ", ["tha"] = "サ", ["thi"] = "シ", ["thu"] = "ス", ["the"] = "セ", ["tho"] = "ソ",
        ["kya"] = "キャ", ["kyu"] = "キュ", ["kyo"] = "キョ", ["nya"] = "ニャ", ["nyu"] = "ニュ", ["nyo"] = "ニョ",
        ["hya"] = "ヒャ", ["hyu"] = "ヒュ", ["hyo"] = "ヒョ", ["mya"] = "ミャ", ["myu"] = "ミュ", ["myo"] = "ミョ",
        ["rya"] = "リャ", ["ryu"] = "リュ", ["ryo"] = "リョ", ["gya"] = "ギャ", ["gyu"] = "ギュ", ["gyo"] = "ギョ",
        ["bya"] = "ビャ", ["byu"] = "ビュ", ["byo"] = "ビョ", ["pya"] = "ピャ", ["pyu"] = "ピュ", ["pyo"] = "ピョ",
    };

    // A consonant with no following vowel takes the closest katakana sound.
    private static readonly Dictionary<char, string> Bare = new()
    {
        ['b'] = "ブ", ['c'] = "ク", ['d'] = "ド", ['f'] = "フ", ['g'] = "グ", ['h'] = "", ['j'] = "ジ",
        ['k'] = "ク", ['l'] = "ル", ['m'] = "ム", ['n'] = "ン", ['p'] = "プ", ['q'] = "ク", ['r'] = "ル",
        ['s'] = "ス", ['t'] = "ト", ['v'] = "ヴ", ['w'] = "ウ", ['x'] = "クス", ['z'] = "ズ",
    };

    private static bool IsVowel(char c) => c is 'a' or 'i' or 'u' or 'e' or 'o';

    /// <summary>Converts one romanised word, e.g. "Rony" → ロニー, "Tamino" → タミノ.</summary>
    public static string ToKatakana(string word)
    {
        var s = word.ToLowerInvariant();
        var output = new StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (!char.IsAsciiLetter(c))
            {
                i++;
                continue;
            }

            // Doubled consonant → small tsu (except nn, which is ン).
            if (i + 1 < s.Length && c == s[i + 1] && !IsVowel(c) && c != 'n' && c != 'y')
            {
                output.Append('ッ');
                i++;
                continue;
            }

            if (c == 'n' && (i + 1 >= s.Length || !IsVowel(s[i + 1]) && s[i + 1] != 'y'))
            {
                output.Append('ン');
                i++;
                continue;
            }

            var matched = false;
            for (var length = Math.Min(3, s.Length - i); length >= 1; length--)
            {
                var part = s.Substring(i, length);
                // Consonant + y at the end of the word or before another consonant: "ny" → ニー.
                if (length >= 2 && part[^1] == 'y' && !IsVowel(part[0])
                    && (i + length >= s.Length || !IsVowel(s[i + length])))
                {
                    var stem = part[..^1] + "i";
                    if (Syllables.TryGetValue(stem, out var kana))
                    {
                        output.Append(kana);
                        if (i + length >= s.Length)
                            output.Append('ー');
                        i += length;
                        matched = true;
                        break;
                    }
                }

                if (Syllables.TryGetValue(part, out var syllable))
                {
                    // Repeated vowel lengthens the previous sound: "Aa" → アー.
                    if (length == 1 && i > 0 && s[i - 1] == c && output.Length > 0)
                        output.Append('ー');
                    else
                        output.Append(syllable);
                    i += length;
                    matched = true;
                    break;
                }
            }

            if (matched)
                continue;
            if (c == 'y')
                output.Append('イ');
            else if (Bare.TryGetValue(c, out var bare))
                output.Append(bare);
            i++;
        }

        return output.ToString();
    }

    /// <summary>
    /// Builds replacements for a full name: the full form (joined with ・) and each part alone,
    /// longest first. A katakana override (parts split by space or ・) replaces the guess.
    /// </summary>
    public static IReadOnlyList<(string Latin, string Kana)> Replacements(string name, string? overrideKana)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !parts.Any(p => p.Any(char.IsAsciiLetter)))
            return [];
        var kanaParts = parts.Select(ToKatakana).ToArray();
        if (!string.IsNullOrWhiteSpace(overrideKana))
        {
            var given = overrideKana.Split([' ', '　', '・'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (given.Length == parts.Length)
                kanaParts = given;
            else
                return [(name, overrideKana.Trim())];
        }

        var list = new List<(string, string)>();
        if (parts.Length > 1)
            list.Add((string.Join(' ', parts), string.Join('・', kanaParts)));
        for (var i = 0; i < parts.Length; i++)
            list.Add((parts[i], kanaParts[i]));
        return list.OrderByDescending(r => r.Item1.Length).ToList();
    }

    /// <summary>
    /// Replaces whole-word occurrences of the name, mapping <paramref name="offset"/> (a UTF-16
    /// index into <paramref name="text"/>) to the same place in the result.
    /// </summary>
    public static string Apply(string text, IReadOnlyList<(string Latin, string Kana)> replacements, ref int offset)
    {
        if (replacements.Count == 0 || text.Length == 0)
            return text;
        var pattern = string.Join('|', replacements.Select(r => Regex.Escape(r.Latin)));
        var regex = new Regex($@"(?<![A-Za-z'\-])(?:{pattern})(?![A-Za-z'\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var map = replacements.GroupBy(r => r.Latin, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Kana, StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder();
        var last = 0;
        var mapped = offset;
        foreach (Match match in regex.Matches(text))
        {
            output.Append(text, last, match.Index - last);
            var kana = map[match.Value];
            if (offset >= match.Index + match.Length)
                mapped += kana.Length - match.Length;
            else if (offset >= match.Index)
                mapped = output.Length; // inside the name: point at its start
            output.Append(kana);
            last = match.Index + match.Length;
        }

        if (last == 0)
            return text;
        output.Append(text, last, text.Length - last);
        offset = mapped;
        return output.ToString();
    }

    public static string Apply(string text, IReadOnlyList<(string Latin, string Kana)> replacements)
    {
        var offset = 0;
        return Apply(text, replacements, ref offset);
    }
}
