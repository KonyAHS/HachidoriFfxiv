using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Stats;

/// <summary>
/// Tracks Japanese reading immersion stats: dialogue lines, NPC speech, character/kanji counts,
/// parsed non-trivial vocabulary, dictionary lookups, reading speed, streaks, and mined Anki cards.
/// Persistence is debounced and atomic so gameplay frames never block on disk I/O.
/// </summary>
public sealed class StatsTracker : IDisposable
{
    private const int MaxMinedCards = 500;
    private const int MaxRecentDialogue = 200;
    private const int MaxWordsPerDay = 2500;
    private const double MaxReadingGapSeconds = 90.0;
    private const double MinLineReadingSeconds = 2.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object sync = new();
    private readonly string? filePath;
    private readonly Func<string, Task<IReadOnlyList<WordSpan>>>? segmenter;
    private readonly Func<DateTime> clock;
    private readonly Channel<(string Text, DateTime LocalTime, bool TrackSession)> parseQueue;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task workerTask;
    private readonly Task saveLoopTask;

    private StatsData data;
    private DailyBucket session = new();
    private long version;
    private bool dirty;
    private DateTime lastLineUtc = DateTime.MinValue;
    private string lastDedupKey = string.Empty;
    private DateTime lastDedupUtc = DateTime.MinValue;
    private string lastLookupKey = string.Empty;
    private DateTime lastLookupUtc = DateTime.MinValue;
    private bool disposed;

    public StatsTracker(
        string? storageDirectory,
        Func<string, Task<IReadOnlyList<WordSpan>>>? segmenter = null,
        Func<DateTime>? utcNow = null)
    {
        this.segmenter = segmenter;
        clock = utcNow ?? (() => DateTime.UtcNow);
        filePath = string.IsNullOrWhiteSpace(storageDirectory) ? null : Path.Combine(storageDirectory, "stats.json");
        data = Load(filePath);
        EnsureArrayShapes(data);

        parseQueue = Channel.CreateBounded<(string, DateTime, bool)>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        workerTask = Task.Run(() => ParseWorkerAsync(lifetime.Token));
        saveLoopTask = Task.Run(() => SaveLoopAsync(lifetime.Token));
    }

    public long Version => Interlocked.Read(ref version);

    /// <summary>Records a captured game line (Talk, BattleTalk, Chat).</summary>
    public void RecordLine(string source, string speaker, string text, DateTime timeUtc)
    {
        if (source is not ("Talk" or "BattleTalk" or "Chat"))
            return;
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return;

        var (jpChars, kanjiChars, uniqueKanji) = NonTrivialWords.CountJapaneseChars(text);
        if (jpChars == 0)
            return;

        var nowUtc = timeUtc == default ? clock() : timeUtc.ToUniversalTime();
        var local = nowUtc.ToLocalTime();
        speaker = (speaker ?? string.Empty).Trim();

        lock (sync)
        {
            // Avoid counting rapid duplicate captures of the exact same line.
            var dedupKey = $"{source}\u0001{speaker}\u0001{text}";
            if (dedupKey == lastDedupKey && (nowUtc - lastDedupUtc).TotalSeconds < 3.0)
                return;
            lastDedupKey = dedupKey;
            lastDedupUtc = nowUtc;

            var activeDelta = MinLineReadingSeconds;
            if (lastLineUtc != DateTime.MinValue)
            {
                var gap = (nowUtc - lastLineUtc).TotalSeconds;
                if (gap > 0 && gap <= MaxReadingGapSeconds)
                    activeDelta = Math.Max(1.0, gap);
            }
            lastLineUtc = nowUtc;

            var bucket = GetOrCreateDayLocked(local);
            ApplyLineToBucket(bucket, source, speaker, jpChars, kanjiChars, uniqueKanji, activeDelta, local.Hour);
            ApplyLineToBucket(session, source, speaker, jpChars, kanjiChars, uniqueKanji, activeDelta, local.Hour);

            var isDialogue = source is "Talk" or "BattleTalk";
            if (speaker.Length > 0 && isDialogue)
            {
                if (!data.NpcProfiles.TryGetValue(speaker, out var profile))
                {
                    profile = new NpcProfile { FirstSeenUtc = nowUtc };
                    data.NpcProfiles[speaker] = profile;
                }
                profile.LastSeenUtc = nowUtc;
                profile.LatestQuote = text;
                if (jpChars > profile.LongestQuoteChars)
                {
                    profile.LongestQuote = text;
                    profile.LongestQuoteChars = jpChars;
                }
            }

            if (isDialogue)
            {
                data.RecentDialogue.Insert(0, new DialogueLogEntry
                {
                    Source = source,
                    Speaker = speaker,
                    Text = text,
                    JapaneseChars = jpChars,
                    TimeUtc = nowUtc,
                });
                if (data.RecentDialogue.Count > MaxRecentDialogue)
                    data.RecentDialogue.RemoveRange(MaxRecentDialogue, data.RecentDialogue.Count - MaxRecentDialogue);

                if (data.LongestLine is null || jpChars > data.LongestLine.JapaneseChars)
                {
                    data.LongestLine = new LongestMonologueRecord
                    {
                        Speaker = speaker,
                        Text = text,
                        JapaneseChars = jpChars,
                        RecordedAtUtc = nowUtc,
                    };
                }
            }

            UpdateStreaksLocked(local.Date);
            dirty = true;
            Interlocked.Increment(ref version);
        }

        // Tokenize in background for non-trivial word frequency stats.
        parseQueue.Writer.TryWrite((text, local, true));
    }

    /// <summary>Records a dictionary lookup performed by the user.</summary>
    public void RecordLookup(LookupResult result)
    {
        var expression = NonTrivialWords.NormalizeToken(result.Term.Expression);
        if (expression.Length == 0)
            return;

        var nowUtc = clock();
        var local = nowUtc.ToLocalTime();
        var reading = result.Term.Reading ?? string.Empty;
        var shortMeaning = ExtractShortMeaning(result.Term);

        lock (sync)
        {
            var key = $"{expression}\u0001{reading}";
            if (key == lastLookupKey && (nowUtc - lastLookupUtc).TotalSeconds < 2.0)
                return;
            lastLookupKey = key;
            lastLookupUtc = nowUtc;

            var bucket = GetOrCreateDayLocked(local);
            bucket.Lookups++;
            session.Lookups++;

            if (NonTrivialWords.IsNonTrivial(expression))
            {
                IncrementWord(bucket.LookupWords, expression);
                IncrementWord(session.LookupWords, expression);
            }

            UpsertWordMetaLocked(expression, reading, shortMeaning, nowUtc);
            dirty = true;
            Interlocked.Increment(ref version);
        }
    }

    /// <summary>Records an Anki card mined through Hachidori.</summary>
    public void RecordMinedCard(
        long noteId,
        LookupResult result,
        string sentence,
        string speaker,
        bool hasScreenshot,
        bool hasAudio,
        bool hasAnimation,
        bool hasTranslation)
    {
        var nowUtc = clock();
        var local = nowUtc.ToLocalTime();
        var term = result.Term;
        var expression = term.Expression;
        var reading = term.Reading;
        var shortMeaning = ExtractShortMeaning(term);
        var freq = term.Frequencies.FirstOrDefault()?.Values.FirstOrDefault().Display ?? string.Empty;
        var pitch = term.Pitches.FirstOrDefault()?.Pitches.FirstOrDefault() is { } p && (p.Position >= 0 || p.Pattern.Length > 0)
            ? $"[{Japanese.DownstepLabel(p)}]"
            : string.Empty;

        lock (sync)
        {
            var bucket = GetOrCreateDayLocked(local);
            bucket.CardsMined++;
            session.CardsMined++;

            // Replace existing entry if same noteId was updated.
            if (noteId > 0)
                data.MinedCards.RemoveAll(c => c.NoteId == noteId);

            data.MinedCards.Insert(0, new MinedCardEntry
            {
                NoteId = noteId,
                Expression = expression,
                Reading = reading,
                Sentence = sentence ?? string.Empty,
                Speaker = speaker,
                ShortMeaning = shortMeaning,
                Frequency = freq,
                Pitch = pitch,
                HasScreenshot = hasScreenshot,
                HasAudio = hasAudio,
                HasAnimation = hasAnimation,
                HasTranslation = hasTranslation,
                MinedAtUtc = nowUtc,
            });
            if (data.MinedCards.Count > MaxMinedCards)
                data.MinedCards.RemoveRange(MaxMinedCards, data.MinedCards.Count - MaxMinedCards);

            UpsertWordMetaLocked(expression, reading, shortMeaning, nowUtc);
            dirty = true;
            Interlocked.Increment(ref version);
        }
    }

    /// <summary>Records parsed tokens directly (useful for testing or synchronous callers).</summary>
    public void RecordParsedWords(IEnumerable<string> words, DateTime? localTime = null, bool includeSession = true)
    {
        var when = localTime ?? clock().ToLocalTime();
        var filtered = new List<string>();
        foreach (var raw in words)
        {
            var norm = NonTrivialWords.NormalizeToken(raw);
            if (NonTrivialWords.IsNonTrivial(norm))
                filtered.Add(norm);
        }

        if (filtered.Count == 0)
            return;

        lock (sync)
        {
            var bucket = GetOrCreateDayLocked(when);
            foreach (var word in filtered)
            {
                IncrementWord(bucket.ParsedWords, word);
                if (includeSession)
                    IncrementWord(session.ParsedWords, word);
            }

            TrimWordMap(bucket.ParsedWords, MaxWordsPerDay);
            dirty = true;
            Interlocked.Increment(ref version);
        }
    }

    /// <summary>Builds a pre-aggregated snapshot for the requested time window.</summary>
    public StatsSnapshot GetSnapshot(StatsRange range)
    {
        var today = clock().ToLocalTime().Date;

        lock (sync)
        {
            var buckets = SelectBucketsLocked(range, today);

            var lines = 0;
            var talk = 0;
            var battle = 0;
            var chat = 0;
            long jpChars = 0;
            long kanjiChars = 0;
            var lookups = 0;
            var cards = 0;
            var activeSec = 0.0;
            var hourly = new int[24];
            var uniqueKanji = new HashSet<int>();
            var npcMap = new Dictionary<string, (int Chars, int Lines)>(StringComparer.Ordinal);
            var parsedWords = new Dictionary<string, int>(StringComparer.Ordinal);
            var lookupWords = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var b in buckets)
            {
                lines += b.Lines;
                talk += b.TalkLines;
                battle += b.BattleTalkLines;
                chat += b.ChatLines;
                jpChars += b.JapaneseChars;
                kanjiChars += b.KanjiChars;
                lookups += b.Lookups;
                cards += b.CardsMined;
                activeSec += b.ActiveSeconds;

                if (b.HourlyChars is { Length: 24 })
                {
                    for (var h = 0; h < 24; h++)
                        hourly[h] += b.HourlyChars[h];
                }

                if (!string.IsNullOrEmpty(b.UniqueKanji))
                {
                    foreach (var r in b.UniqueKanji.EnumerateRunes())
                        uniqueKanji.Add(r.Value);
                }

                foreach (var (speaker, npc) in b.Npcs)
                {
                    npcMap.TryGetValue(speaker, out var prev);
                    npcMap[speaker] = (prev.Chars + npc.JapaneseChars, prev.Lines + npc.Lines);
                }

                foreach (var (word, count) in b.ParsedWords)
                {
                    if (!NonTrivialWords.IsNonTrivial(word))
                        continue;
                    parsedWords.TryGetValue(word, out var prev);
                    parsedWords[word] = prev + count;
                }

                foreach (var (word, count) in b.LookupWords)
                {
                    if (!NonTrivialWords.IsNonTrivial(word))
                        continue;
                    lookupWords.TryGetValue(word, out var prev);
                    lookupWords[word] = prev + count;
                }
            }

            // All-time totals (for rank, achievements, streak)
            long allTimeChars = 0;
            var allTimeLines = 0;
            var allTimeCards = 0;
            var allTimeKanji = new HashSet<int>();
            DayPoint? mostActiveDay = null;
            foreach (var (dateKey, b) in data.Days)
            {
                allTimeChars += b.JapaneseChars;
                allTimeLines += b.Lines;
                allTimeCards += b.CardsMined;
                if (!string.IsNullOrEmpty(b.UniqueKanji))
                {
                    foreach (var r in b.UniqueKanji.EnumerateRunes())
                        allTimeKanji.Add(r.Value);
                }

                if (b.JapaneseChars > 0 && (mostActiveDay is null || b.JapaneseChars > mostActiveDay.JapaneseChars))
                {
                    var shortLbl = DateTime.TryParseExact(dateKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                        ? d.ToString("MMM d", CultureInfo.InvariantCulture)
                        : dateKey;
                    mostActiveDay = new DayPoint(dateKey, shortLbl, b.JapaneseChars, b.Lines, b.Lookups, b.CardsMined, b.ActiveSeconds);
                }
            }

            var currentStreak = ComputeCurrentStreakLocked(today);
            var bestStreak = Math.Max(data.BestStreakDays, currentStreak);

            var chartDays = range == StatsRange.Last7Days ? 7 : 30;
            var dailySeries = BuildDailySeriesLocked(today, chartDays);
            var heatmap30 = chartDays == 30 ? dailySeries : BuildDailySeriesLocked(today, 30);

            var totalNpcChars = Math.Max(1, npcMap.Values.Sum(v => (long)v.Chars));
            var topNpcs = npcMap
                .OrderByDescending(kv => kv.Value.Chars)
                .ThenByDescending(kv => kv.Value.Lines)
                .Take(30)
                .Select(kv =>
                {
                    data.NpcProfiles.TryGetValue(kv.Key, out var prof);
                    return new NpcRankItem(
                        kv.Key,
                        kv.Value.Chars,
                        kv.Value.Lines,
                        100.0 * kv.Value.Chars / totalNpcChars,
                        prof?.LatestQuote ?? string.Empty,
                        prof?.LastSeenUtc ?? DateTime.MinValue);
                })
                .ToList();

            var topParsed = parsedWords
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(40)
                .Select(kv =>
                {
                    data.WordDetails.TryGetValue(kv.Key, out var meta);
                    return new WordRankItem(kv.Key, kv.Value, meta?.Reading ?? string.Empty, meta?.ShortMeaning ?? string.Empty);
                })
                .ToList();

            var topLookedUp = lookupWords
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(30)
                .Select(kv =>
                {
                    data.WordDetails.TryGetValue(kv.Key, out var meta);
                    return new WordRankItem(kv.Key, kv.Value, meta?.Reading ?? string.Empty, meta?.ShortMeaning ?? string.Empty);
                })
                .ToList();

            var sampleKanji = uniqueKanji.Take(120).Select(cp => char.ConvertFromUtf32(cp)).ToList();

            var allTimeNpcs = data.NpcProfiles.Count;
            var achievements = BuildAchievements(allTimeChars, allTimeLines, allTimeKanji.Count, allTimeCards, bestStreak, allTimeNpcs);

            return new StatsSnapshot
            {
                Range = range,
                Version = version,
                Lines = lines,
                TalkLines = talk,
                BattleTalkLines = battle,
                ChatLines = chat,
                JapaneseChars = jpChars,
                KanjiChars = kanjiChars,
                UniqueKanjiCount = uniqueKanji.Count,
                SampleKanji = sampleKanji,
                Lookups = lookups,
                CardsMined = cards,
                ActiveSeconds = activeSec,
                AllTimeJapaneseChars = allTimeChars,
                AllTimeUniqueKanji = allTimeKanji.Count,
                AllTimeLines = allTimeLines,
                AllTimeCardsMined = allTimeCards,
                AllTimeNpcCount = allTimeNpcs,
                CurrentStreakDays = currentStreak,
                BestStreakDays = bestStreak,
                HourlyChars = hourly,
                DailySeries = dailySeries,
                Heatmap30Days = heatmap30,
                TopNpcs = topNpcs,
                TopParsedWords = topParsed,
                TopLookedUpWords = topLookedUp,
                RecentMinedCards = data.MinedCards.Take(50).ToList(),
                RecentDialogue = data.RecentDialogue.Take(100).ToList(),
                LongestLine = data.LongestLine,
                MostActiveDay = mostActiveDay,
                Achievements = achievements,
            };
        }
    }

    public void ResetSession()
    {
        lock (sync)
        {
            session = new DailyBucket();
            Interlocked.Increment(ref version);
        }
    }

    public void ClearAll()
    {
        lock (sync)
        {
            data = new StatsData();
            session = new DailyBucket();
            lastLineUtc = DateTime.MinValue;
            lastDedupKey = string.Empty;
            lastLookupKey = string.Empty;
            dirty = true;
            Interlocked.Increment(ref version);
        }

        Flush();
    }

    public string ExportToFile(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"hachidori-stats-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        string json;
        lock (sync)
            json = JsonSerializer.Serialize(data, PrettyJsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    public void Flush()
    {
        if (filePath is null)
            return;

        string? json = null;
        lock (sync)
        {
            if (!dirty)
                return;
            json = JsonSerializer.Serialize(data, JsonOptions);
            dirty = false;
        }

        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = filePath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("stats", $"Could not save stats: {ex.Message}");
            lock (sync) dirty = true;
        }
    }

    // -------------------------------------------------------------- internals

    private async Task ParseWorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (text, localTime, includeSession) in parseQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                IReadOnlyList<string> words;
                try
                {
                    if (segmenter is not null)
                    {
                        var spans = await segmenter(text).ConfigureAwait(false);
                        if (spans.Count > 0)
                        {
                            var list = new List<string>(spans.Count);
                            foreach (var span in spans)
                            {
                                if (span.Start >= 0 && span.Length > 0 && span.Start + span.Length <= text.Length)
                                    list.Add(text.Substring(span.Start, span.Length));
                            }
                            words = list;
                        }
                        else
                        {
                            words = NonTrivialWords.ExtractFallbackWords(text);
                        }
                    }
                    else
                    {
                        words = NonTrivialWords.ExtractFallbackWords(text);
                    }
                }
                catch
                {
                    words = NonTrivialWords.ExtractFallbackWords(text);
                }

                RecordParsedWords(words, localTime, includeSession);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SaveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                Flush();
            }
        }
        catch (OperationCanceledException) { }
    }

    private List<DailyBucket> SelectBucketsLocked(StatsRange range, DateTime today)
    {
        if (range == StatsRange.Session)
            return [session];

        if (range == StatsRange.AllTime)
            return data.Days.Values.ToList();

        var days = range switch
        {
            StatsRange.Today => 1,
            StatsRange.Last7Days => 7,
            StatsRange.Last30Days => 30,
            _ => 30,
        };

        var result = new List<DailyBucket>(days);
        for (var i = 0; i < days; i++)
        {
            var key = today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (data.Days.TryGetValue(key, out var bucket))
                result.Add(bucket);
        }

        return result;
    }

    private List<DayPoint> BuildDailySeriesLocked(DateTime today, int days)
    {
        var list = new List<DayPoint>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var label = days <= 7
                ? date.ToString("ddd", CultureInfo.InvariantCulture)
                : date.ToString("M/d", CultureInfo.InvariantCulture);
            if (data.Days.TryGetValue(key, out var b))
                list.Add(new DayPoint(key, label, b.JapaneseChars, b.Lines, b.Lookups, b.CardsMined, b.ActiveSeconds));
            else
                list.Add(new DayPoint(key, label, 0, 0, 0, 0, 0));
        }

        return list;
    }

    private DailyBucket GetOrCreateDayLocked(DateTime localTime)
    {
        var key = localTime.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!data.Days.TryGetValue(key, out var bucket))
        {
            bucket = new DailyBucket();
            data.Days[key] = bucket;
        }
        else if (bucket.HourlyChars is not { Length: 24 })
        {
            bucket.HourlyChars = new int[24];
        }

        return bucket;
    }

    private static void ApplyLineToBucket(
        DailyBucket bucket,
        string source,
        string speaker,
        int jpChars,
        int kanjiChars,
        HashSet<int> uniqueKanji,
        double activeDelta,
        int hour)
    {
        bucket.Lines++;
        switch (source)
        {
            case "Talk": bucket.TalkLines++; break;
            case "BattleTalk": bucket.BattleTalkLines++; break;
            case "Chat": bucket.ChatLines++; break;
        }

        bucket.JapaneseChars += jpChars;
        bucket.KanjiChars += kanjiChars;
        bucket.ActiveSeconds += activeDelta;
        if (bucket.HourlyChars is { Length: 24 } && hour is >= 0 and < 24)
            bucket.HourlyChars[hour] += jpChars;

        if (uniqueKanji.Count > 0)
            bucket.UniqueKanji = MergeUniqueKanji(bucket.UniqueKanji, uniqueKanji);

        if (speaker.Length > 0 && source is "Talk" or "BattleTalk")
        {
            if (!bucket.Npcs.TryGetValue(speaker, out var npc))
            {
                npc = new NpcDailyStats();
                bucket.Npcs[speaker] = npc;
            }
            npc.Lines++;
            npc.JapaneseChars += jpChars;
        }
    }

    private static string MergeUniqueKanji(string existing, HashSet<int> incoming)
    {
        var set = new HashSet<int>();
        if (!string.IsNullOrEmpty(existing))
        {
            foreach (var r in existing.EnumerateRunes())
                set.Add(r.Value);
        }

        var before = set.Count;
        foreach (var cp in incoming)
            set.Add(cp);
        if (set.Count == before)
            return existing;

        var sb = new StringBuilder(set.Count * 2);
        foreach (var cp in set)
            sb.Append(char.ConvertFromUtf32(cp));
        return sb.ToString();
    }

    private static void IncrementWord(Dictionary<string, int> map, string word)
    {
        map.TryGetValue(word, out var count);
        map[word] = count + 1;
    }

    private static void TrimWordMap(Dictionary<string, int> map, int limit)
    {
        if (map.Count <= limit)
            return;
        var drop = map.OrderBy(kv => kv.Value).Take(map.Count - limit).Select(kv => kv.Key).ToList();
        foreach (var k in drop)
            map.Remove(k);
    }

    private void UpsertWordMetaLocked(string word, string reading, string shortMeaning, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(word))
            return;
        if (!data.WordDetails.TryGetValue(word, out var meta))
        {
            meta = new WordMeta();
            data.WordDetails[word] = meta;
        }
        if (!string.IsNullOrWhiteSpace(reading))
            meta.Reading = reading;
        if (!string.IsNullOrWhiteSpace(shortMeaning))
            meta.ShortMeaning = shortMeaning;
        meta.LastSeenUtc = nowUtc;

        if (data.WordDetails.Count > 4000)
        {
            var oldest = data.WordDetails.OrderBy(kv => kv.Value.LastSeenUtc).Take(data.WordDetails.Count - 3500).Select(kv => kv.Key).ToList();
            foreach (var key in oldest)
                data.WordDetails.Remove(key);
        }
    }

    private int ComputeCurrentStreakLocked(DateTime today)
    {
        var streak = 0;
        var cursor = today;

        // If today has no activity yet, check if yesterday keeps the streak alive.
        var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!data.Days.TryGetValue(todayKey, out var tb) || tb.JapaneseChars <= 0)
            cursor = today.AddDays(-1);

        while (true)
        {
            var key = cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!data.Days.TryGetValue(key, out var b) || b.JapaneseChars <= 0)
                break;
            streak++;
            cursor = cursor.AddDays(-1);
        }

        return streak;
    }

    private void UpdateStreaksLocked(DateTime today)
    {
        var current = ComputeCurrentStreakLocked(today);
        if (current > data.BestStreakDays)
            data.BestStreakDays = current;
    }

    private static List<AchievementItem> BuildAchievements(long chars, int lines, int uniqueKanji, int cards, int bestStreak, int npcCount) =>
    [
        Make("First Steps in Eorzea", "Read 1,000 Japanese characters", chars, 1_000),
        Make("Cutscene Enthusiast", "Read 500 dialogue lines", lines, 500),
        Make("Social Butterfly", "Read dialogue from 25 different NPCs", npcCount, 25),
        Make("Card Smith", "Mine 50 Anki cards in FFXIV", cards, 50),
        Make("Dedicated Scion", "Maintain a 7-day reading streak", bestStreak, 7),
        Make("Kanji Collector", "Encounter 500 unique kanji", uniqueKanji, 500),
        Make("Novella Reader", "Read 25,000 Japanese characters", chars, 25_000),
        Make("Light Novel Clear", "Read 100,000 Japanese characters", chars, 100_000),
        Make("Joyo Explorer", "Encounter 1,500 unique kanji", uniqueKanji, 1_500),
    ];

    private static AchievementItem Make(string title, string desc, long current, long target) =>
        new(title, desc, current, target, current >= target);

    private static string ExtractShortMeaning(TermInfo term)
    {
        foreach (var group in term.Glossaries)
        {
            var blocks = GlossaryBuilder.Build(group.Content, 0, false, group.Dictionary);
            foreach (var block in blocks)
            {
                if (block.Image is not null)
                    continue;
                var text = block.ScanText.Trim();
                if (text.Length == 0)
                    continue;
                text = text.Replace('\n', ' ').Replace('\r', ' ');
                return text.Length > 72 ? text[..71] + "…" : text;
            }
        }

        return string.Empty;
    }

    private static StatsData Load(string? path)
    {
        if (path is null || !File.Exists(path))
            return new StatsData();
        try
        {
            var loaded = JsonSerializer.Deserialize<StatsData>(File.ReadAllText(path), JsonOptions);
            return loaded ?? new StatsData();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("stats", $"Could not read stats file, starting fresh: {ex.Message}");
            return new StatsData();
        }
    }

    private static void EnsureArrayShapes(StatsData d)
    {
        d.Days ??= new(StringComparer.Ordinal);
        d.NpcProfiles ??= new(StringComparer.Ordinal);
        d.WordDetails ??= new(StringComparer.Ordinal);
        d.MinedCards ??= new();
        d.RecentDialogue ??= new();
        foreach (var bucket in d.Days.Values)
        {
            if (bucket.HourlyChars is not { Length: 24 })
                bucket.HourlyChars = new int[24];
            bucket.Npcs ??= new(StringComparer.Ordinal);
            bucket.ParsedWords ??= new(StringComparer.Ordinal);
            bucket.LookupWords ??= new(StringComparer.Ordinal);
            bucket.UniqueKanji ??= string.Empty;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        parseQueue.Writer.TryComplete();
        lifetime.Cancel();
        try { workerTask.Wait(TimeSpan.FromMilliseconds(250)); } catch { }
        try { saveLoopTask.Wait(TimeSpan.FromMilliseconds(250)); } catch { }
        Flush();
        lifetime.Dispose();
    }
}
