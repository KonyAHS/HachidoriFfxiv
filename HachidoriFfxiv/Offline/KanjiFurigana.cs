using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.Offline;

/// <summary>
/// The furigana split Hachidori's engine service adds to a lookup's headwords
/// (engine-service.js withFurigana), ported from HDGlossary.kanjiFurigana in
/// Hachidori 0.5.2's render/glossary.js (GPL-3.0-or-later) so offline replies
/// carry the same <c>term.furigana</c> as live ones. JavaScript string semantics
/// are kept: offsets count UTF-16 code units and loops over "characters" walk
/// code points.
/// </summary>
public sealed class KanjiFurigana
{
    private const string DakutenKana = "かきくけこさしすせそたちつてとはひふへほ";
    private const string HandakutenKana = "はひふへほ";
    private const string URowKana = "うくぐすつぬぶむる";
    private const string IRowKana = "いきぎしちにびみり";
    private const string SokuonKana = "つちくきり";
    private static readonly IReadOnlySet<string> NoKanjiReadings = new HashSet<string>();

    private readonly Func<string, IReadOnlySet<string>> kanjiReadings;
    private readonly Kanwa kanwa;

    public KanjiFurigana(IReadOnlyDictionary<string, string> kanjidic, IReadOnlyDictionary<string, string> kanwaReadings,
        IReadOnlyDictionary<string, string> kanwaCompounds)
    {
        kanjiReadings = CreateKanjiReadings(kanjidic, null);
        kanwa = new Kanwa(kanwaReadings, kanwaCompounds, kanjiReadings);
    }

    private static readonly Lazy<KanjiFurigana> Bundled = new(LoadBundled);

    /// <summary>The KANJIDIC and kanwa tables Hachidori bundles (Offline/Data).</summary>
    public static KanjiFurigana Shared => Bundled.Value;

    private static KanjiFurigana LoadBundled()
    {
        var assembly = typeof(KanjiFurigana).Assembly;
        using var kanjidic = JsonDocument.Parse(Resource("kanji-readings"));
        using var kanwaDoc = JsonDocument.Parse(Resource("kanwa-readings"));
        return new KanjiFurigana(Table(kanjidic.RootElement.GetProperty("readings")),
            Table(kanwaDoc.RootElement.GetProperty("readings")), Table(kanwaDoc.RootElement.GetProperty("compounds")));

        Stream Resource(string name) => assembly.GetManifestResourceStream($"HachidoriFfxiv.Offline.{name}.json")
            ?? throw new InvalidOperationException($"The bundled {name}.json is missing.");
    }

    private static Dictionary<string, string> Table(JsonElement element) =>
        element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);

    /// <summary>
    /// The split a headword's kanji readings give it, or null where that is the
    /// split the renderer makes alone (kanjiFurigana).
    /// </summary>
    public IReadOnlyList<FuriganaSegment>? For(string expression, string reading)
    {
        var local = DistributeFurigana(expression, reading, null);
        var segments = local ?? DistributeFurigana(expression, reading, kanjiReadings);
        if (segments is null) return null;
        var furigana = SplitKanjiRuns(segments, kanjiReadings, kanwa);
        return ReferenceEquals(furigana, local) ? null : furigana;
    }

    // ------------------------------------------------------------ distribute

    /// <summary>distributeFurigana without kanji readings: null where no split, or more than one, spells the reading.</summary>
    public static IReadOnlyList<FuriganaSegment>? Distribute(string expression, string reading) => DistributeFurigana(expression, reading, null);

    private sealed class Group(bool isKana, string text)
    {
        public bool IsKana { get; } = isKana;
        public string Text { get; set; } = text;
        public string? NormalizedText { get; set; }
    }

    private static List<FuriganaSegment>? DistributeFurigana(string expression, string reading,
        Func<string, IReadOnlySet<string>>? readings)
    {
        if (reading.Length == 0 || reading == expression)
            return [new FuriganaSegment(expression, string.Empty)];
        var groups = new List<Group>();
        foreach (var character in CodePoints(expression))
        {
            var isKana = IsKana(character);
            if (groups.Count > 0 && groups[^1].IsKana == isKana) groups[^1].Text += character;
            else groups.Add(new Group(isKana, character));
        }
        foreach (var group in groups)
            if (group.IsKana) group.NormalizedText = ToHiragana(group.Text);
        return SegmentizeFurigana(reading, ToHiragana(reading), groups, 0, readings);
    }

    private static List<FuriganaSegment>? SegmentizeFurigana(string reading, string normalizedReading, List<Group> groups,
        int groupStart, Func<string, IReadOnlySet<string>>? readings)
    {
        var groupCount = groups.Count - groupStart;
        if (groupCount <= 0) return reading.Length == 0 ? [] : null;

        var group = groups[groupStart];
        if (group.IsKana)
        {
            if (group.NormalizedText is not null && normalizedReading.StartsWith(group.NormalizedText, StringComparison.Ordinal))
            {
                var segments = SegmentizeFurigana(Substring(reading, group.Text.Length), Substring(normalizedReading, group.Text.Length),
                    groups, groupStart + 1, readings);
                if (segments is not null)
                {
                    if (reading.StartsWith(group.Text, StringComparison.Ordinal))
                        segments.Insert(0, new FuriganaSegment(group.Text, string.Empty));
                    else
                        segments.InsertRange(0, KanaSegments(group.Text, reading));
                    return segments;
                }
            }
            return null;
        }

        List<FuriganaSegment>? result = null;
        for (var index = reading.Length; index >= group.Text.Length; index--)
        {
            var segments = readings is null || ReadKanjiRun(CodePoints(group.Text).ToList(), Substring(normalizedReading, 0, index), readings) is not null
                ? SegmentizeFurigana(Substring(reading, index), Substring(normalizedReading, index), groups, groupStart + 1, readings)
                : null;
            if (segments is not null)
            {
                if (result is not null) return null;
                segments.Insert(0, new FuriganaSegment(group.Text, Substring(reading, 0, index)));
                result = segments;
            }
            if (groupCount == 1) break;
        }
        return result;
    }

    private static List<FuriganaSegment> KanaSegments(string text, string reading)
    {
        var segments = new List<FuriganaSegment>();
        var start = 0;
        var state = At(reading, 0) == At(text, 0);
        for (var index = 1; index < text.Length; index++)
        {
            var nextState = At(reading, index) == At(text, index);
            if (state == nextState) continue;
            segments.Add(new FuriganaSegment(Substring(text, start, index), state ? string.Empty : Substring(reading, start, index)));
            state = nextState;
            start = index;
        }
        segments.Add(new FuriganaSegment(Substring(text, start), state ? string.Empty : Substring(reading, start, text.Length)));
        return segments;
    }

    // ------------------------------------------------------------ kanji runs

    private static IReadOnlyList<FuriganaSegment> SplitKanjiRuns(List<FuriganaSegment> segments,
        Func<string, IReadOnlySet<string>> readings, Kanwa? kanwa)
    {
        var split = new List<FuriganaSegment>();
        foreach (var segment in segments)
        {
            var characters = CodePoints(segment.Text).ToList();
            if (characters.Count < 2 || segment.Reading.Length == 0)
            {
                split.Add(segment);
                continue;
            }
            var reading = ToHiragana(segment.Reading);
            var bounds = ReadKanjiRun(characters, reading, readings)?.Select(b => (int?)b).ToList()
                ?? (kanwa is null ? null : ReadKanwaRun(characters, reading, kanwa));
            if (bounds is not { Count: > 0 })
            {
                split.Add(segment);
                continue;
            }
            for (int start = 0, end = 1; end <= characters.Count; end++)
            {
                // Past the end of the list is JavaScript's undefined: not null, and a slice to the end.
                if (end < bounds.Count && bounds[end] is null) continue;
                split.Add(new FuriganaSegment(
                    end - start == 1 ? characters[start] : string.Concat(characters.Skip(start).Take(end - start)),
                    Slice(segment.Reading, start < bounds.Count ? bounds[start] ?? 0 : 0, end < bounds.Count ? bounds[end] : null)));
                start = end;
            }
        }
        return split.Count == segments.Count ? segments : split;
    }

    /// <summary>readKanjiRun: where each character's share starts, then the length; empty when ambiguous; null when none reads.</summary>
    private static List<int>? ReadKanjiRun(IReadOnlyList<string> text, string reading, Func<string, IReadOnlySet<string>> readings)
    {
        var ends = new Dictionary<int, int> { [0] = 0 };
        var steps = new List<Dictionary<int, int>>();
        var forms = NoKanjiReadings;
        foreach (var character in text)
        {
            if (character != "々") forms = readings(character);
            var next = new Dictionary<int, int>();
            foreach (var (end, start) in ends)
            {
                foreach (var form in forms)
                {
                    if (!StartsWithAt(reading, form, end)) continue;
                    var shareEnd = end + form.Length;
                    next[shareEnd] = next.ContainsKey(shareEnd) || start < 0 ? -1 : end;
                }
            }
            if (next.Count == 0) return null;
            steps.Add(next);
            ends = next;
        }
        if (!ends.TryGetValue(reading.Length, out var last)) return null;
        if (last < 0) return [];
        var bounds = new int[steps.Count + 1];
        bounds[steps.Count] = reading.Length;
        for (var index = steps.Count - 1; index >= 0; index--) bounds[index] = steps[index][bounds[index + 1]];
        return bounds.ToList();
    }

    private sealed record Split(int Count, List<int?>? Shares); // null Shares: more than one split reads

    private static List<int?>? ReadKanwaRun(IReadOnlyList<string> characters, string reading, Kanwa kanwa)
    {
        if (!characters.Any(kanwa.Adds)) return null;
        var starting = characters.Select((_, index) => kanwa.Compounds(characters, index)).ToList();
        var forms = NoKanjiReadings;
        var characterForms = characters.Select(character =>
        {
            if (character != "々") forms = kanwa.KanjiReadings(character);
            return forms;
        }).ToList();
        var best = new Dictionary<int, Split?>();

        Split? Read(int index, int end)
        {
            if (index == characters.Count) return end == reading.Length ? new Split(0, []) : null;
            var key = index * (reading.Length + 1) + end;
            if (best.TryGetValue(key, out var cached)) return cached;
            Split? found = null;
            foreach (var compound in starting[index])
            {
                foreach (var (whole, shares) in compound.Readings)
                {
                    if (!StartsWithAt(reading, whole, end)) continue;
                    found = BetterSplit(found, compound.Characters.Count, shares.Select(s => s is null ? null : end + s).ToList(),
                        Read(index + compound.Characters.Count, end + whole.Length));
                }
            }
            foreach (var form in characterForms[index])
            {
                if (StartsWithAt(reading, form, end))
                    found = BetterSplit(found, 0, [end], Read(index + 1, end + form.Length));
            }
            best[key] = found;
            return found;
        }

        var split = Read(0, 0);
        if (split is null) return null;
        return split.Shares is null ? [] : [.. split.Shares, reading.Length];
    }

    private static Split? BetterSplit(Split? found, int count, List<int?> shares, Split? rest)
    {
        if (rest is null) return found;
        var split = new Split(count + rest.Count, rest.Shares is null ? null : [.. shares, .. rest.Shares]);
        if (found is null || split.Count > found.Count) return split;
        if (split.Count < found.Count || SameSplit(split.Shares, found.Shares)) return found;
        return found with { Shares = null };
    }

    private static bool SameSplit(List<int?>? left, List<int?>? right) =>
        left is not null && right is not null && left.SequenceEqual(right);

    // -------------------------------------------------------------- readings

    private static Func<string, IReadOnlySet<string>> CreateKanjiReadings(IReadOnlyDictionary<string, string> table,
        Func<string, IReadOnlySet<string>>? baseReadings)
    {
        var cache = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var gate = new object();
        return character =>
        {
            lock (gate)
            {
                if (cache.TryGetValue(character, out var forms)) return forms;
                if (!table.TryGetValue(character, out var readings)) forms = baseReadings is null ? NoKanjiReadings : baseReadings(character);
                else if (baseReadings is null) forms = KanjiReadingForms(readings);
                else forms = new HashSet<string>(baseReadings(character).Concat(KanjiReadingForms(readings)), StringComparer.Ordinal);
                cache[character] = forms;
                return forms;
            }
        };
    }

    private static HashSet<string> KanjiReadingForms(string readings)
    {
        var forms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reading in readings.Split(' '))
        {
            var parts = reading.Split('.');
            var stem = parts[0];
            var okurigana = parts.Length > 1 ? parts[1] : string.Empty;
            forms.Add(stem);
            forms.Add(stem + okurigana);
            for (var length = 1; length < okurigana.Length; length++) forms.Add(stem + okurigana[..length]);
            var row = okurigana.Length == 0 ? -1 : URowKana.IndexOf(okurigana[^1]);
            if (row >= 0) forms.Add(stem + okurigana[..^1] + IRowKana[row]);
        }
        foreach (var form in forms.ToList())
        {
            if (form.Length == 0) continue;
            var first = form[0];
            if (DakutenKana.Contains(first)) forms.Add((char)(first + 1) + form[1..]);
            if (HandakutenKana.Contains(first)) forms.Add((char)(first + 2) + form[1..]);
            if (first == 'ち') forms.Add("じ" + form[1..]);
            if (first == 'つ') forms.Add("ず" + form[1..]);
        }
        foreach (var form in forms.ToList())
        {
            if (form.Length > 0 && SokuonKana.Contains(form[^1])) forms.Add(form[..^1] + "っ");
        }
        return forms;
    }

    private sealed record Compound(IReadOnlyList<string> Characters, IReadOnlyList<(string Reading, IReadOnlyList<int?> Shares)> Readings);

    /// <summary>createKanwaReadings: what Jitendex's kanwa data adds to the KANJIDIC readings.</summary>
    private sealed class Kanwa
    {
        private readonly IReadOnlyDictionary<string, string> readings;
        private readonly IReadOnlyDictionary<string, string> table;
        private readonly Dictionary<string, List<string>> byFirst = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Compound>> parsed = new(StringComparer.Ordinal);

        public Kanwa(IReadOnlyDictionary<string, string> readings, IReadOnlyDictionary<string, string> compounds,
            Func<string, IReadOnlySet<string>> kanjiReadings)
        {
            this.readings = readings;
            table = compounds;
            foreach (var compound in compounds.Keys)
            {
                var first = CodePoints(compound).First();
                if (!byFirst.TryGetValue(first, out var group)) byFirst[first] = group = [];
                group.Add(compound);
            }
            KanjiReadings = CreateKanjiReadings(readings, kanjiReadings);
        }

        public Func<string, IReadOnlySet<string>> KanjiReadings { get; }

        public bool Adds(string character) => readings.ContainsKey(character) || byFirst.ContainsKey(character);

        public List<Compound> Compounds(IReadOnlyList<string> characters, int start)
        {
            List<Compound> compounds;
            lock (parsed)
            {
                if (!parsed.TryGetValue(characters[start], out compounds!))
                {
                    compounds = (byFirst.TryGetValue(characters[start], out var names) ? names : [])
                        .Select(name => Parse(name, table[name])).ToList();
                    parsed[characters[start]] = compounds;
                }
            }
            return compounds.Where(compound => compound.Characters
                .Select((character, index) => start + index < characters.Count && characters[start + index] == character)
                .All(x => x)).ToList();
        }

        private static Compound Parse(string compound, string readings)
        {
            var characters = CodePoints(compound).ToList();
            return new Compound(characters, readings.Split(' ').Select(reading =>
            {
                var parts = reading.Split('|');
                var start = 0;
                IReadOnlyList<int?> shares = parts.Length == 1
                    ? characters.Select((_, index) => index == 0 ? (int?)0 : null).ToList()
                    : parts.Select(part => (int?)((start += part.Length) - part.Length)).ToList();
                return (string.Concat(parts), shares);
            }).ToList());
        }
    }

    // ------------------------------------------------------ string semantics

    internal static string ToHiragana(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text) builder.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c);
        return builder.ToString();
    }

    private static bool IsKana(string character) => character.Length == 1 && character[0] is >= '぀' and <= 'ヿ';

    private static IEnumerable<string> CodePoints(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return text.Substring(i, 2);
                i++;
            }
            else
            {
                yield return text[i].ToString();
            }
        }
    }

    private static char? At(string text, int index) => index < text.Length ? text[index] : null;

    // String.prototype.startsWith(search, position): the position is clamped to the length.
    private static bool StartsWithAt(string text, string search, int position)
    {
        position = Math.Clamp(position, 0, text.Length);
        return text.Length - position >= search.Length && string.CompareOrdinal(text, position, search, 0, search.Length) == 0;
    }

    // String.prototype.substring: clamped, and swapped when start > end.
    private static string Substring(string text, int start, int? end = null)
    {
        var a = Math.Clamp(start, 0, text.Length);
        var b = Math.Clamp(end ?? text.Length, 0, text.Length);
        if (a > b) (a, b) = (b, a);
        return text[a..b];
    }

    // String.prototype.slice: clamped, empty when start >= end.
    private static string Slice(string text, int start, int? end)
    {
        var a = Math.Clamp(start, 0, text.Length);
        var b = Math.Clamp(end ?? text.Length, 0, text.Length);
        return a >= b ? string.Empty : text[a..b];
    }
}
