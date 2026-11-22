using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Stats;

/// <summary>
/// Filters out Japanese particles, auxiliaries, pronouns and filler so stats highlight
/// real, meaningful vocabulary parsed from dialogue and lookups.
/// </summary>
public static class NonTrivialWords
{
    private static readonly char[] TrimChars = " \t\r\n　「」『』（）()【】［］[]《》〈〉、。・，．！？!?…―ー～~:;：；\"'’‘`´^*+=/\\|<>＜＞♪★☆◆◇■□●○△▲▽▼→←↑↓".ToCharArray();

    // Common grammar words, particles, auxiliaries, pronouns, filler and basic function words.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // Particles, conjunctions, copulas, auxiliaries
        "の", "は", "が", "を", "に", "へ", "で", "と", "も", "や", "か", "な", "ね", "よ", "わ", "さ", "ぞ", "ぜ", "な",
        "から", "まで", "より", "ほど", "だけ", "ばかり", "ながら", "ので", "のに", "けど", "けれど", "けれども", "だが",
        "しかし", "そして", "また", "でも", "だから", "それで", "すると", "つまり", "なお", "ただ", "ただし", "もし",
        "では", "じゃ", "じゃあ", "なら", "ならば", "って", "とは", "には", "では", "へと", "とも", "かも", "など",
        "なんか", "なんて", "くらい", "ぐらい", "こそ", "さえ", "すら", "しか", "ずつ", "きり", "っきり",
        "です", "ます", "だ", "だった", "でした", "ない", "なかった", "なく", "ぬ", "ん", "ず", "たい", "たく",
        "せる", "させる", "れる", "られる", "て", "ている", "てる", "ていた", "てた", "てある", "ておく", "とく",
        "てみる", "てしまう", "ちゃう", "じゃう", "てくる", "ていく", "てく", "てくれる", "てもらう", "てあげる",
        "よう", "ように", "ような", "そう", "そうに", "そうな", "らしい", "みたい", "べき", "はず", "つもり",
        "という", "といった", "として", "とした", "について", "にとって", "によって", "により", "において", "における",
        "ため", "ために", "ところ", "ところに", "とき", "ときに", "こと", "ことが", "ことを", "ことに", "もの", "ものが",
        "わけ", "はず", "まま", "とおり", "うち", "あと", "まえ", "うえ", "した", "なか", "ほう", "ほか", "ほど",
        "した", "して", "する", "しない", "します", "した", "しよう", "しろ", "せよ", "でき", "できる", "できた",
        "いる", "いた", "いて", "いない", "います", "ある", "あった", "あって", "あります", "なる", "なった", "なって",
        "おる", "おり", "ござる", "ございます", "いたす", "いたします", "くださる", "ください", "しまう", "しまった",
        // Demonstratives, pronouns, question words
        "これ", "それ", "あれ", "どれ", "この", "その", "あの", "どの", "ここ", "そこ", "あそこ", "どこ",
        "こちら", "そちら", "あちら", "どちら", "こっち", "そっち", "あっち", "どっち", "こう", "そう", "ああ", "どう",
        "こんな", "そんな", "あんな", "どんな", "なに", "なん", "だれ", "いつ", "なぜ", "どうして", "いかが", "いくら",
        "わたし", "わたくし", "あたし", "ぼく", "おれ", "わし", "われ", "われわれ", "あなた", "あんた", "きみ", "おまえ",
        "てめえ", "きさま", "かれ", "かのじょ", "みんな", "みな", "みなさん", "じぶん", "ひと", "だれか", "なにか",
        "私", "僕", "俺", "君", "貴方", "彼", "彼女", "自分", "私達", "私たち", "我々", "皆", "皆様", "皆さん",
        "お前", "貴様", "奴", "何", "誰", "今", "事", "物", "所", "時", "為", "様", "方", "中", "上", "下", "前", "後",
        "人", "者", "的", "達", "等", "気", "手", "目", "口", "心", "訳", "分", "度", "回", "番", "名", "日", "年",
        "一", "二", "三", "四", "五", "六", "七", "八", "九", "十", "百", "千", "万", "大", "小", "新", "古", "多", "少",
        // Interjections, filler, common adverbs, ultra-basic verbs/adjectives
        "はい", "いいえ", "ええ", "うん", "ううん", "ああ", "おお", "うう", "ええ", "ふむ", "ほう", "むむ", "うむ",
        "くっ", "はっ", "あっ", "えっ", "おっ", "ちっ", "ぬっ", "ぐっ", "ふん", "へえ", "ほお", "なあ", "ねえ", "おい",
        "こら", "よし", "ほら", "さあ", "まあ", "さて", "では", "やれやれ", "おや", "あら", "おっと", "なんと",
        "もう", "まだ", "ずっと", "すぐ", "いま", "ただいま", "ちょっと", "とても", "すごく", "かなり", "ずいぶん",
        "やはり", "やっぱり", "たぶん", "きっと", "もちろん", "ぜひ", "どうか", "せめて", "まったく", "ほんとう",
        "本当", "本当に", "大丈夫", "ありがとう", "ありがとうございます", "すみません", "ごめん", "ごめんなさい", "よろしく",
        "言う", "いう", "思う", "おもう", "見る", "みる", "行く", "いく", "来る", "くる", "知る", "しる",
        "分かる", "わかる", "持つ", "もつ", "使う", "つかう", "作る", "つくる", "取る", "とる", "出る", "でる",
        "入る", "はいる", "聞く", "きく", "話す", "はなす", "やる", "くれる", "もらう", "あげる", "おく", "みる",
        "良い", "よい", "いい", "悪い", "わるい", "多い", "おおい", "少ない", "すくない", "大きい", "おおきい",
        "小さい", "ちいさい", "長い", "ながい", "短い", "みじかい", "高い", "たかい", "低い", "ひくい",
        "新しい", "あたらしい", "古い", "ふるい", "同じ", "おなじ", "違う", "ちがう", "すごい", "ない", "ある",
        "ひとつ", "ふたつ", "みっつ", "よっつ", "いつつ", "すべて", "全て", "全部", "少し", "すこし", "たくさん",
        "いま", "きょう", "あした", "きのう", "今日", "明日", "昨日", "毎日", "いつも", "ときどき", "また",
        // Conjugated common endings sometimes surfaced as standalone spans
        "ている", "ていた", "ていて", "ていない", "ません", "ました", "ましょう", "だろう", "でしょう", "かもしれない",
        "じゃない", "ではない", "だった", "でした", "なので", "だから", "けれど", "しかし", "それで", "それから",
        "ありがとう", "おはよう", "こんにちは", "こんばんは", "さようなら", "お願い", "おねがい",
    };

    /// <summary>Trims surrounding whitespace and Japanese/ASCII punctuation from a token.</summary>
    public static string NormalizeToken(string surface)
    {
        if (string.IsNullOrWhiteSpace(surface))
            return string.Empty;
        return surface.Trim(TrimChars);
    }

    /// <summary>
    /// Returns true if the token is a meaningful Japanese word worth highlighting in stats
    /// (excludes single kana, punctuation, pure ASCII/numbers, and common grammar/filler).
    /// </summary>
    public static bool IsNonTrivial(string raw)
    {
        var token = NormalizeToken(raw);
        if (token.Length == 0)
            return false;
        if (StopWords.Contains(token))
            return false;

        var runes = 0;
        var kanji = 0;
        var hiragana = 0;
        var katakana = 0;
        var other = 0;
        var firstRune = 0;
        var allSameRune = true;

        foreach (var rune in token.EnumerateRunes())
        {
            var cp = rune.Value;
            if (runes == 0)
                firstRune = cp;
            else if (cp != firstRune && cp != 'ー' && cp != 'っ' && cp != 'ッ')
                allSameRune = false;
            runes++;

            if (Japanese.IsKanji(cp))
                kanji++;
            else if (cp is >= 0x3041 and <= 0x309F)
                hiragana++;
            else if (cp is >= 0x30A0 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F)
                katakana++;
            else
                other++;
        }

        // Must contain Japanese script.
        if (kanji + hiragana + katakana == 0)
            return false;

        // Ignore noise like "あああ" or "フフフ" or "――".
        if (runes >= 2 && allSameRune && kanji == 0)
            return false;

        // Single-rune tokens: only keep substantive kanji (stop-listed basic kanji already rejected above).
        if (runes == 1)
            return kanji == 1;

        // Pure hiragana needs at least 3 chars (filters out 2-char particles/auxiliaries like けど, ので, とは, etc.)
        if (kanji == 0 && katakana == 0)
            return hiragana >= 3 && other == 0;

        // Katakana loanwords / proper nouns: at least 2 katakana chars (ignore trailing ー only).
        if (kanji == 0 && katakana > 0)
            return katakana >= 2 && token.Replace("ー", string.Empty).Length >= 2;

        // Anything containing kanji (length >= 2) is almost always substantive unless stop-listed above.
        return kanji >= 1;
    }

    /// <summary>
    /// Fallback word extractor when neither live Hachidori nor offline engine segmentation is ready.
    /// Extracts kanji compounds (with optional short okurigana) and katakana terms.
    /// </summary>
    public static IReadOnlyList<string> ExtractFallbackWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var results = new List<string>();
        var current = new StringBuilder();
        var mode = 0; // 0=none, 1=kanji, 2=katakana

        void Flush()
        {
            if (current.Length == 0)
                return;
            var word = current.ToString();
            current.Clear();
            if (IsNonTrivial(word))
                results.Add(word);
        }

        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (Japanese.IsKanji(cp))
            {
                if (mode != 1)
                    Flush();
                mode = 1;
                current.Append(rune);
            }
            else if (cp is >= 0x30A1 and <= 0x30FA or 0x30FC or >= 0x31F0 and <= 0x31FF)
            {
                if (mode != 2)
                    Flush();
                mode = 2;
                current.Append(rune);
            }
            else
            {
                Flush();
                mode = 0;
            }
        }

        Flush();
        return results;
    }

    /// <summary>Counts Japanese characters (kana + kanji), kanji specifically, and distinct kanji codepoints.</summary>
    public static (int JapaneseChars, int KanjiChars, HashSet<int> UniqueKanji) CountJapaneseChars(string text)
    {
        var jp = 0;
        var kanji = 0;
        var unique = new HashSet<int>();
        if (string.IsNullOrEmpty(text))
            return (0, 0, unique);

        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (Japanese.IsKanji(cp))
            {
                jp++;
                kanji++;
                unique.Add(cp);
            }
            else if (Japanese.IsKana(cp))
            {
                jp++;
            }
        }

        return (jp, kanji, unique);
    }

    /// <summary>FFXIV-flavored immersion ranks based on cumulative Japanese characters read.</summary>
    public static readonly (int MinChars, string Title, string Subtitle)[] Ranks =
    [
        (0, "Sprout Reader", "Just stepped off the airship"),
        (1_000, "Guild Apprentice", "Getting comfortable with Eorzean prose"),
        (5_000, "Scion Scribe", "Following the MSQ in Japanese"),
        (15_000, "Gridanian Chronicler", "Steady immersion habit"),
        (40_000, "Ul'dahn Lexicographer", "Turning dialogue into vocabulary"),
        (100_000, "Sharlayan Archivist", "One full light novel's worth of Japanese"),
        (250_000, "Keeper of the Lake", "Deep into Eorzean lore"),
        (500_000, "Ascian Philologist", "Half a million Japanese characters"),
        (1_000_000, "Warrior of Kanji", "One million characters mastered"),
    ];

    public static (int Level, string Title, string Subtitle, int CurrentFloor, int? NextTarget) GetRank(long totalJapaneseChars)
    {
        var index = 0;
        for (var i = Ranks.Length - 1; i >= 0; i--)
        {
            if (totalJapaneseChars >= Ranks[i].MinChars)
            {
                index = i;
                break;
            }
        }

        var current = Ranks[index];
        int? next = index + 1 < Ranks.Length ? Ranks[index + 1].MinChars : null;
        return (index + 1, current.Title, current.Subtitle, current.MinChars, next);
    }

    /// <summary>Fun real-world reading equivalent for a character count.</summary>
    public static string ReadingEquivalent(long japaneseChars) => japaneseChars switch
    {
        < 100 => "warming up",
        < 2_000 => $"≈ {japaneseChars / 400.0:0.0} manga pages",
        < 15_000 => $"≈ {japaneseChars / 1_500.0:0.0} manga chapters",
        < 100_000 => $"≈ {japaneseChars / 15_000.0:0.0} manga volumes",
        _ => $"≈ {japaneseChars / 100_000.0:0.00} light novels",
    };

    /// <summary>Fun time-of-day persona based on 24-hour character distribution.</summary>
    public static (string Badge, string Description) ReadingPersona(int[] hourlyChars)
    {
        if (hourlyChars.Length != 24 || hourlyChars.All(v => v == 0))
            return ("New Adventurer", "Read some dialogue to discover your peak immersion hours");

        var morning = hourlyChars[5..12].Sum();
        var afternoon = hourlyChars[12..18].Sum();
        var evening = hourlyChars[18..23].Sum();
        var night = hourlyChars[23] + hourlyChars[0..5].Sum();

        var peakHour = 0;
        for (var h = 1; h < 24; h++)
        {
            if (hourlyChars[h] > hourlyChars[peakHour])
                peakHour = h;
        }

        var peakText = $"Peak hour: {peakHour:00}:00–{(peakHour + 1) % 24:00}:00";
        if (night >= morning && night >= afternoon && night >= evening)
            return ("Night Owl", $"Late-night immersion specialist · {peakText}");
        if (morning >= afternoon && morning >= evening)
            return ("Early Bird", $"Morning Eorzea reader · {peakText}");
        if (afternoon >= evening)
            return ("Daylight Adventurer", $"Afternoon reading rhythm · {peakText}");
        return ("Prime-Time Scion", $"Evening immersion regular · {peakText}");
    }
}
