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

/// <summary>Optional runtime callbacks for StatsTracker (game title, reading-time rule, ecosystem logging).</summary>
public sealed class StatsTrackerOptions
{
    public Func<string>? GameTitle { get; init; }
    public Func<int>? DayStartHour { get; init; }
    public Func<bool>? UseRubiAdaptiveTime { get; init; }
    public Func<bool>? WriteHistoryCsv { get; init; }
    public Func<bool>? WriteTidepoolSqlite { get; init; }
}

/// <summary>
/// Tracks Japanese reading immersion stats: dialogue lines, NPC speech, character/kanji counts,
/// parsed non-trivial vocabulary, dictionary lookups, Rubi adaptive reading speed, streaks,
/// customizable daily/expansion goals, mined Anki cards, and bidirectional ecosystem logging
/// (ExStatic / Rubi / GSM / Tidepool / Kechimochi / SubMiner).
/// Persistence is debounced and atomic so gameplay frames never block on disk I/O.
/// </summary>
public sealed class StatsTracker : IDisposable
{
    private const int MaxMinedCards = 500;
    private const int MaxRecentDialogue = 200;
    private const int MaxWordsPerDay = 2500;
    private const int MaxImportedKeys = 250_000;
    private const double LegacyMaxReadingGapSeconds = 90.0;
    private const double MinLineReadingSeconds = 2.0;
    private const int MaxPendingStoreRows = 50_000;

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

    // Serializes everything that touches the files on disk (flushes, clear, live-store exports).
    // Lock order: ioSync before sync; never take ioSync while holding sync.
    private readonly object ioSync = new();
    private readonly string? storageDir;
    private readonly string? filePath;
    private readonly string? historyCsvPath;
    private readonly string? tidepoolSqlitePath;
    private readonly Func<string, Task<IReadOnlyList<WordSpan>>>? segmenter;
    private readonly Func<DateTime> clock;
    private readonly StatsTrackerOptions options;
    private readonly Channel<(string Text, DateTime LogicalDay, bool TrackSession)> parseQueue;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task workerTask;
    private readonly Task saveLoopTask;

    private readonly List<EcosystemLine> pendingCsvLines = new();
    private readonly List<EcosystemLine> pendingSqliteLines = new();
    private readonly List<(long NoteId, string Title, long MinedAtMs)> pendingSqliteCards = new();
    private double pendingSqliteActiveSeconds;

    private StatsData data;
    private DailyBucket session = new();
    private long version;
    private long lineSequence;
    private bool dirty;
    private DateTime lastLineUtc = DateTime.MinValue;
    private int lastLineChars;
    private int consecutiveSkimLines;
    private bool inSkipBurst;
    private string lastDedupKey = string.Empty;
    private DateTime lastDedupUtc = DateTime.MinValue;
    private string lastLookupKey = string.Empty;
    private DateTime lastLookupUtc = DateTime.MinValue;
    private bool disposed;

    public StatsTracker(
        string? storageDirectory,
        Func<string, Task<IReadOnlyList<WordSpan>>>? segmenter = null,
        Func<DateTime>? utcNow = null,
        StatsTrackerOptions? options = null)
    {
        this.segmenter = segmenter;
        this.options = options ?? new StatsTrackerOptions();
        clock = utcNow ?? (() => DateTime.UtcNow);
        storageDir = string.IsNullOrWhiteSpace(storageDirectory) ? null : storageDirectory;
        filePath = storageDir is null ? null : Path.Combine(storageDir, "stats.json");
        historyCsvPath = storageDir is null ? null : Path.Combine(storageDir, "history.csv");
        tidepoolSqlitePath = storageDir is null ? null : Path.Combine(storageDir, "hachidori-ffxiv-log.sqlite");
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
    public string? StorageDirectory => storageDir;
    public string? HistoryCsvPath => historyCsvPath;
    public string? TidepoolSqlitePath => tidepoolSqlitePath;

    private string CurrentGameTitle()
    {
        var t = options.GameTitle?.Invoke()?.Trim();
        return string.IsNullOrEmpty(t) ? EcosystemStatsCodec.DefaultGameName : t;
    }

    private int CurrentDayStartHour() => Math.Clamp(options.DayStartHour?.Invoke() ?? 0, 0, 12);
    private bool UseAdaptiveTime() => options.UseRubiAdaptiveTime?.Invoke() ?? true;
    private bool ShouldWriteHistoryCsv() => historyCsvPath is not null && (options.WriteHistoryCsv?.Invoke() ?? true);
    private bool ShouldWriteTidepoolSqlite() => tidepoolSqlitePath is not null && (options.WriteTidepoolSqlite?.Invoke() ?? true);

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
        var logicalDay = ReadingTimeRule.LogicalDate(nowUtc, CurrentDayStartHour());
        speaker = (speaker ?? string.Empty).Trim();
        var ecoChars = ReadingTimeRule.CountEcosystemChars(text);

        lock (sync)
        {
            // Avoid counting rapid duplicate captures of the exact same line.
            var dedupKey = $"{source}\u0001{speaker}\u0001{text}";
            if (dedupKey == lastDedupKey && (nowUtc - lastDedupUtc).TotalSeconds < 3.0)
                return;
            lastDedupKey = dedupKey;
            lastDedupUtc = nowUtc;

            var activeDelta = MinLineReadingSeconds;
            var skippedBySkim = false;
            if (lastLineUtc != DateTime.MinValue)
            {
                var gapSec = (nowUtc - lastLineUtc).TotalSeconds;
                if (gapSec > 0)
                {
                    if (UseAdaptiveTime())
                    {
                        var gapUs = (long)Math.Round(gapSec * 1_000_000.0);
                        var (creditUs, nextPace) = ReadingTimeRule.AdaptiveCredit(gapUs, Math.Max(lastLineChars, jpChars), data.AdaptivePace);
                        data.AdaptivePace = nextPace;
                        activeDelta = creditUs / 1_000_000.0;

                        // Live Rubi skip-mode filter: 3+ rapid long lines (<1s) in a row.
                        if (creditUs > 0 && creditUs < ReadingTimeRule.SkimGapUs)
                        {
                            if (inSkipBurst)
                            {
                                skippedBySkim = true;
                            }
                            else if (jpChars >= ReadingTimeRule.SkimChars)
                            {
                                consecutiveSkimLines++;
                                if (consecutiveSkimLines >= ReadingTimeRule.SkimLines)
                                {
                                    inSkipBurst = true;
                                    skippedBySkim = true;
                                }
                            }
                        }
                        else
                        {
                            consecutiveSkimLines = 0;
                            inSkipBurst = false;
                        }
                    }
                    else if (gapSec <= LegacyMaxReadingGapSeconds)
                    {
                        activeDelta = Math.Max(1.0, gapSec);
                    }
                }
            }
            lastLineUtc = nowUtc;
            lastLineChars = jpChars;

            if (skippedBySkim)
                activeDelta = 0.0;

            var bucket = GetOrCreateDayLocked(logicalDay);
            if (!skippedBySkim)
            {
                ApplyLineToBucket(bucket, source, speaker, jpChars, kanjiChars, uniqueKanji, activeDelta, local.Hour);
                ApplyLineToBucket(session, source, speaker, jpChars, kanjiChars, uniqueKanji, activeDelta, local.Hour);
            }

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

            // Queue ecosystem line log (ExStatic / Rubi / GSM / Tidepool compatible).
            var unixSec = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds() / 1000.0;
            var seq = Interlocked.Increment(ref lineSequence);
            var lineId = $"hachidori-{(long)(unixSec * 1000)}-{seq}";
            var ecoLine = new EcosystemLine(
                EcosystemStatsCodec.DefaultGameUuid,
                lineId,
                CurrentGameTitle(),
                text,
                unixSec,
                ecoChars,
                "hook",
                speaker);

            if (ShouldWriteHistoryCsv())
                pendingCsvLines.Add(ecoLine);
            if (ShouldWriteTidepoolSqlite())
            {
                pendingSqliteLines.Add(ecoLine);
                pendingSqliteActiveSeconds += activeDelta;
            }

            UpdateStreaksLocked(logicalDay);
            dirty = true;
            Interlocked.Increment(ref version);
        }

        // Tokenize in background for non-trivial word frequency stats.
        parseQueue.Writer.TryWrite((text, logicalDay, true));
    }

    /// <summary>Records a dictionary lookup performed by the user.</summary>
    public void RecordLookup(LookupResult result)
    {
        var expression = NonTrivialWords.NormalizeToken(result.Term.Expression);
        if (expression.Length == 0)
            return;

        var nowUtc = clock();
        var logicalDay = ReadingTimeRule.LogicalDate(nowUtc, CurrentDayStartHour());
        var reading = result.Term.Reading ?? string.Empty;
        var shortMeaning = ExtractShortMeaning(result.Term);

        lock (sync)
        {
            var key = $"{expression}\u0001{reading}";
            if (key == lastLookupKey && (nowUtc - lastLookupUtc).TotalSeconds < 2.0)
                return;
            lastLookupKey = key;
            lastLookupUtc = nowUtc;

            var bucket = GetOrCreateDayLocked(logicalDay);
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
        var logicalDay = ReadingTimeRule.LogicalDate(nowUtc, CurrentDayStartHour());
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
            var bucket = GetOrCreateDayLocked(logicalDay);
            bucket.CardsMined++;
            session.CardsMined++;

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

            if (ShouldWriteTidepoolSqlite())
                pendingSqliteCards.Add((noteId, CurrentGameTitle(), new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds()));

            UpsertWordMetaLocked(expression, reading, shortMeaning, nowUtc);
            dirty = true;
            Interlocked.Increment(ref version);
        }
    }

    /// <summary>Records parsed tokens directly (useful for testing or synchronous callers).</summary>
    public void RecordParsedWords(IEnumerable<string> words, DateTime? localTime = null, bool includeSession = true)
    {
        var when = localTime ?? clock().ToLocalTime();
        var logicalDay = when.Date;
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
            var bucket = GetOrCreateDayLocked(logicalDay);
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

    /// <summary>Builds a pre-aggregated snapshot for the requested time window and goal settings.</summary>
    public StatsSnapshot GetSnapshot(StatsRange range, GoalSettings? goalSettings = null)
    {
        var dayStartHour = goalSettings?.DayStartHour ?? CurrentDayStartHour();
        var today = ReadingTimeRule.LogicalDate(clock(), dayStartHour);

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

            // All-time totals (for rank, achievements, streak, expansion journey)
            long allTimeChars = 0;
            var allTimeLines = 0;
            var allTimeCards = 0;
            var allTimeActiveSec = 0.0;
            var allTimeKanji = new HashSet<int>();
            DayPoint? mostActiveDay = null;
            foreach (var (dateKey, b) in data.Days)
            {
                allTimeChars += b.JapaneseChars;
                allTimeLines += b.Lines;
                allTimeCards += b.CardsMined;
                allTimeActiveSec += b.ActiveSeconds;
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

            var dailySeries = range switch
            {
                StatsRange.Last7Days => BuildDailySeriesLocked(today, 7),
                StatsRange.Last12Months => BuildMonthlySeriesLocked(today, 12),
                StatsRange.AllTime => BuildMonthlySeriesLocked(today, months: 0),
                _ => BuildDailySeriesLocked(today, 30),
            };
            var heatmap30 = BuildDailySeriesLocked(today, 30);

            var comparison = BuildComparisonLocked(range, today, jpChars, activeSec, cards);
            var goals = goalSettings is not null ? BuildGoalProgressLocked(goalSettings, range, today) : null;

            // Compute chars/hour for expansion ETA using active window if substantial, else all-time.
            var etaCph = activeSec >= 120 && jpChars > 0
                ? jpChars / (activeSec / 3600.0)
                : (allTimeActiveSec >= 60 && allTimeChars > 0 ? allTimeChars / (allTimeActiveSec / 3600.0) : 0);

            var expansionJourney = ExpansionEstimates.Compute(
                allTimeChars,
                goalSettings?.GoalExpansionId ?? ExpansionEstimates.AllMsqId,
                goalSettings?.ExpansionStartingChars ?? 0,
                etaCph,
                goalSettings?.DailyCharGoal ?? 0);

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
                Comparison = comparison,
                Goals = goals,
                ExpansionJourney = expansionJourney,
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

    /// <summary>
    /// Deletes every recorded stat: the aggregates in stats.json, the live history.csv and the
    /// rows in the live SQLite log. Runs under the I/O lock so a background flush cannot write
    /// an old batch back afterwards. Throws <see cref="IOException"/> if a store could not be cleared;
    /// the aggregates are cleared regardless.
    /// </summary>
    public void ClearAll()
    {
        lock (ioSync)
        {
            ClearAggregates();
            var failures = new List<string>();

            if (historyCsvPath is not null && File.Exists(historyCsvPath))
            {
                try
                {
                    File.Delete(historyCsvPath);
                }
                catch (Exception ex)
                {
                    failures.Add($"history.csv: {ex.Message}");
                }
            }

            if (tidepoolSqlitePath is not null)
            {
                try
                {
                    EcosystemStatsCodec.ClearTidepoolSqlite(tidepoolSqlitePath);
                }
                catch (Exception ex)
                {
                    failures.Add($"SQLite log: {ex.Message}");
                }
            }

            FlushLocked();
            if (failures.Count > 0)
                throw new IOException("Stats were cleared, but these files could not be emptied: " + string.Join("; ", failures));
        }
    }

    private void ClearAggregates()
    {
        lock (sync)
        {
            data = new StatsData();
            session = new DailyBucket();
            pendingCsvLines.Clear();
            pendingSqliteLines.Clear();
            pendingSqliteCards.Clear();
            pendingSqliteActiveSeconds = 0;
            lastLineUtc = DateTime.MinValue;
            lastLineChars = 0;
            consecutiveSkimLines = 0;
            inSkipBurst = false;
            lastDedupKey = string.Empty;
            lastLookupKey = string.Empty;
            dirty = true;
            Interlocked.Increment(ref version);
        }
    }

    // -------------------------------------------------------------- Exports & Ecosystem Sync

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

    /// <summary>Exports ExStatic / Rubi / GameSentenceMiner compatible lines CSV.</summary>
    public string ExportExStaticLinesCsv(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"hachidori-ffxiv-exstatic-lines-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        lock (ioSync)
        {
            FlushLocked();
            if (historyCsvPath is not null && File.Exists(historyCsvPath) && new FileInfo(historyCsvPath).Length > 0)
            {
                File.Copy(historyCsvPath, path, overwrite: true);
                return path;
            }
        }

        // Fallback to recent dialogue if live history.csv hasn't been written yet.
        List<EcosystemLine> fallback;
        lock (sync)
        {
            var gameName = CurrentGameTitle();
            fallback = data.RecentDialogue
                .OrderBy(d => d.TimeUtc)
                .Select((d, idx) =>
                {
                    var unixSec = new DateTimeOffset(d.TimeUtc).ToUnixTimeMilliseconds() / 1000.0;
                    return new EcosystemLine(
                        EcosystemStatsCodec.DefaultGameUuid,
                        $"hachidori-recent-{(long)(unixSec * 1000)}-{idx}",
                        gameName,
                        d.Text,
                        unixSec,
                        ReadingTimeRule.CountEcosystemChars(d.Text),
                        "hook",
                        d.Speaker);
                })
                .ToList();
        }
        EcosystemStatsCodec.WriteExStaticLinesCsv(path, fallback);
        return path;
    }

    /// <summary>Exports exSTATic daily statistics CSV.</summary>
    public string ExportExStaticStatsCsv(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"hachidori-ffxiv-exstatic-stats-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        Dictionary<string, DailyBucket> copy;
        string title;
        lock (sync)
        {
            copy = new Dictionary<string, DailyBucket>(data.Days, StringComparer.Ordinal);
            title = CurrentGameTitle();
        }
        EcosystemStatsCodec.WriteExStaticStatsCsv(path, copy, title);
        return path;
    }

    /// <summary>Exports Kechimochi ("kimochi") Activity Logs CSV.</summary>
    public string ExportKechimochiCsv(string directory, string activityType = "Playing")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"hachidori-ffxiv-kechimochi-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        Dictionary<string, DailyBucket> copy;
        string title;
        lock (sync)
        {
            copy = new Dictionary<string, DailyBucket>(data.Days, StringComparer.Ordinal);
            title = CurrentGameTitle();
        }
        EcosystemStatsCodec.WriteKechimochiCsv(path, copy, title, activityType);
        return path;
    }

    /// <summary>Exports a Tidepool / Telly / GSM-compatible SQLite database snapshot.</summary>
    public string ExportTidepoolSqlite(string directory)
    {
        if (!NativeSqlite.IsAvailable)
            throw new PlatformNotSupportedException("Native SQLite (e_sqlite3) is unavailable on this platform.");

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"hachidori-ffxiv-tidepool-{DateTime.Now:yyyyMMdd-HHmmss}.sqlite");
        lock (ioSync)
        {
            FlushLocked();
            if (tidepoolSqlitePath is not null && File.Exists(tidepoolSqlitePath))
            {
                // VACUUM INTO reads through the WAL, so committed rows not yet checkpointed are included.
                EcosystemStatsCodec.BackupSqlite(tidepoolSqlitePath, path);
                return path;
            }
        }

        // Build snapshot from recent dialogue if live SQLite was disabled.
        List<EcosystemLine> lines;
        double totalActive;
        lock (sync)
        {
            var title = CurrentGameTitle();
            totalActive = data.Days.Values.Sum(d => d.ActiveSeconds);
            lines = data.RecentDialogue
                .OrderBy(d => d.TimeUtc)
                .Select((d, idx) =>
                {
                    var unixSec = new DateTimeOffset(d.TimeUtc).ToUnixTimeMilliseconds() / 1000.0;
                    return new EcosystemLine(
                        EcosystemStatsCodec.DefaultGameUuid,
                        $"hachidori-export-{(long)(unixSec * 1000)}-{idx}",
                        title,
                        d.Text,
                        unixSec,
                        ReadingTimeRule.CountEcosystemChars(d.Text),
                        "hook",
                        d.Speaker);
                })
                .ToList();
        }
        EcosystemStatsCodec.AppendTidepoolSqlite(path, lines, totalActive);
        return path;
    }

    /// <summary>Pushes recorded lines directly into a running GameSentenceMiner instance via HTTP.</summary>
    public async Task<(bool Ok, string Message)> PushToGsmAsync(string gsmBaseUrl, CancellationToken ct = default)
    {
        // Upload a snapshot copy so a background append cannot change the file mid-upload.
        var tempDir = storageDir ?? Path.GetTempPath();
        var csvPath = ExportExStaticLinesCsv(Path.Combine(tempDir, "exports"));

        try
        {
            return await EcosystemStatsCodec.PushToGsmAsync(gsmBaseUrl, csvPath, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(csvPath); } catch { }
        }
    }

    /// <summary>
    /// Imports historical immersion data from ExStatic / Rubi / LearnFromGames / GSM / Kechimochi CSV
    /// or GSM / SubMiner / Tidepool / Telly SQLite databases, deduplicated so re-importing is safe.
    /// </summary>
    public EcosystemImportResult ImportFromFile(string filePath, string? titleFilter = null)
    {
        var (format, lines, daily) = EcosystemStatsCodec.ReadImportFile(filePath, titleFilter);
        var dayStartHour = CurrentDayStartHour();
        var importedLines = 0;
        var importedDays = 0;
        var skippedDupes = 0;
        long totalJpChars = 0;

        if (lines.Count > 0)
        {
            // Sort chronologically and apply Rubi adaptive reading time rule.
            lines.Sort((a, b) => a.UnixSeconds.CompareTo(b.UnixSeconds));
            var seqInput = lines.Select(l => (l.UnixSeconds, l.Characters > 0 ? l.Characters : ReadingTimeRule.CountEcosystemChars(l.Text), l.GameUuid)).ToList();
            var (creditsSec, skippedSkim) = ReadingTimeRule.CreditSequence(seqInput);

            lock (sync)
            {
                var touchedDays = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < lines.Count; i++)
                {
                    if (skippedSkim[i])
                        continue;

                    var l = lines[i];
                    var dedupKey = $"L:{l.GameUuid}:{l.LineId}";
                    if (!data.ImportedKeys.Add(dedupKey))
                    {
                        skippedDupes++;
                        continue;
                    }

                    var (jpChars, kanjiChars, uniqueKanji) = NonTrivialWords.CountJapaneseChars(l.Text);
                    if (jpChars == 0 && l.Characters > 0)
                        jpChars = l.Characters;
                    if (jpChars == 0)
                        continue;

                    var utc = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(l.UnixSeconds * 1000.0)).UtcDateTime;
                    var local = utc.ToLocalTime();
                    var logicalDay = ReadingTimeRule.LogicalDate(utc, dayStartHour);
                    var dayKey = logicalDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var bucket = GetOrCreateDayLocked(logicalDay);

                    ApplyLineToBucket(bucket, "Talk", l.Speaker, jpChars, kanjiChars, uniqueKanji, creditsSec[i], local.Hour);
                    touchedDays.Add(dayKey);
                    importedLines++;
                    totalJpChars += jpChars;
                }

                importedDays = touchedDays.Count;
                TrimImportedKeysLocked();
                RecalculateBestStreakLocked();
                dirty = true;
                Interlocked.Increment(ref version);
            }
        }
        else if (daily.Count > 0)
        {
            lock (sync)
            {
                var touchedDays = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in daily)
                {
                    var dedupKey = $"D:{entry.SourceKind}:{entry.DateKey}:{entry.Title}:{entry.Characters}:{(int)Math.Round(entry.ActiveSeconds)}";
                    if (!data.ImportedKeys.Add(dedupKey))
                    {
                        skippedDupes++;
                        continue;
                    }

                    if (!DateTime.TryParseExact(entry.DateKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        continue;

                    var bucket = GetOrCreateDayLocked(date);
                    bucket.JapaneseChars += Math.Max(0, entry.Characters);
                    bucket.Lines += Math.Max(0, entry.Lines);
                    bucket.TalkLines += Math.Max(0, entry.Lines);
                    bucket.ActiveSeconds += Math.Max(0.0, entry.ActiveSeconds);
                    bucket.CardsMined += Math.Max(0, entry.CardsMined);
                    touchedDays.Add(entry.DateKey);
                    totalJpChars += Math.Max(0, entry.Characters);
                }

                importedDays = touchedDays.Count;
                TrimImportedKeysLocked();
                RecalculateBestStreakLocked();
                dirty = true;
                Interlocked.Increment(ref version);
            }
        }

        Flush();
        return new EcosystemImportResult(format, importedLines, importedDays, skippedDupes, totalJpChars);
    }

    /// <summary>
    /// Writes pending stats to disk. Only one flush, clear or live-store export runs at a time
    /// (<see cref="ioSync"/>), and a batch that fails to write is put back in the queue so the
    /// next flush retries it. The CSV append rolls back partial writes and the SQLite append
    /// is transactional, so a retry does not duplicate rows.
    /// </summary>
    public void Flush()
    {
        lock (ioSync)
            FlushLocked();
    }

    private void FlushLocked()
    {
        string? json = null;
        List<EcosystemLine>? csvBatch = null;
        List<EcosystemLine>? sqliteBatch = null;
        List<(long NoteId, string Title, long MinedAtMs)>? sqliteCards = null;
        double sqliteActiveSec = 0;

        lock (sync)
        {
            if (filePath is not null && dirty)
            {
                json = JsonSerializer.Serialize(data, JsonOptions);
                dirty = false;
            }
            if (historyCsvPath is not null && pendingCsvLines.Count > 0)
            {
                csvBatch = [.. pendingCsvLines];
                pendingCsvLines.Clear();
            }
            if (tidepoolSqlitePath is not null && (pendingSqliteLines.Count > 0 || pendingSqliteCards.Count > 0))
            {
                sqliteBatch = [.. pendingSqliteLines];
                pendingSqliteLines.Clear();
                sqliteCards = [.. pendingSqliteCards];
                pendingSqliteCards.Clear();
                sqliteActiveSec = pendingSqliteActiveSeconds;
                pendingSqliteActiveSeconds = 0;
            }
        }

        if (json is not null && filePath is not null)
        {
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

        if (csvBatch is { Count: > 0 } && historyCsvPath is not null)
        {
            try
            {
                EcosystemStatsCodec.AppendExStaticLines(historyCsvPath, csvBatch);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("stats", $"Could not append history.csv (will retry {csvBatch.Count} lines): {ex.Message}");
                lock (sync)
                {
                    pendingCsvLines.InsertRange(0, csvBatch);
                    CapPendingLocked(pendingCsvLines, "history.csv");
                }
            }
        }

        if (tidepoolSqlitePath is not null && ((sqliteBatch is { Count: > 0 }) || (sqliteCards is { Count: > 0 })))
        {
            try
            {
                EcosystemStatsCodec.AppendTidepoolSqlite(tidepoolSqlitePath, sqliteBatch ?? [], sqliteActiveSec, sqliteCards);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("stats", $"Could not update Tidepool SQLite log (will retry): {ex.Message}");
                lock (sync)
                {
                    if (sqliteBatch is { Count: > 0 })
                        pendingSqliteLines.InsertRange(0, sqliteBatch);
                    if (sqliteCards is { Count: > 0 })
                        pendingSqliteCards.InsertRange(0, sqliteCards);
                    pendingSqliteActiveSeconds += sqliteActiveSec;
                    CapPendingLocked(pendingSqliteLines, "SQLite log");
                    CapPendingLocked(pendingSqliteCards, "SQLite card log");
                }
            }
        }
    }

    /// <summary>Bounds retry queues so a store that keeps failing cannot grow memory without limit.</summary>
    private static void CapPendingLocked<T>(List<T> pending, string store)
    {
        var excess = pending.Count - MaxPendingStoreRows;
        if (excess <= 0)
            return;
        pending.RemoveRange(0, excess);
        DiagnosticLog.Write("stats", $"Dropped the {excess} oldest unsaved {store} rows after repeated write failures.");
    }

    // -------------------------------------------------------------- internals

    private async Task ParseWorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (text, logicalDay, includeSession) in parseQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
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

                RecordParsedWords(words, logicalDay, includeSession);
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

        if (range == StatsRange.Last12Months)
        {
            var fromMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-11);
            var list = new List<DailyBucket>();
            foreach (var (key, bucket) in data.Days)
            {
                if (DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    && d >= fromMonth && d <= today)
                    list.Add(bucket);
            }
            return list;
        }

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

    private PeriodComparison? BuildComparisonLocked(StatsRange range, DateTime today, long currentChars, double currentSec, int currentCards)
    {
        if (range is not (StatsRange.Last7Days or StatsRange.Last30Days))
            return null;

        var n = range == StatsRange.Last7Days ? 7 : 30;
        var priorLabel = n == 7 ? "prior 7d" : "prior 30d";
        long priorChars = 0;
        var priorLines = 0;
        var priorCards = 0;
        var priorSec = 0.0;

        for (var i = n; i < n * 2; i++)
        {
            var key = today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (data.Days.TryGetValue(key, out var b))
            {
                priorChars += b.JapaneseChars;
                priorLines += b.Lines;
                priorCards += b.CardsMined;
                priorSec += b.ActiveSeconds;
            }
        }

        return new PeriodComparison(
            priorLabel,
            priorChars,
            priorLines,
            priorCards,
            priorSec,
            ReadingTimeRule.FormatChange(currentChars, priorChars, priorLabel),
            ReadingTimeRule.FormatChange(currentSec, priorSec, priorLabel),
            ReadingTimeRule.FormatChange(currentCards, priorCards, priorLabel));
    }

    private GoalProgressSummary BuildGoalProgressLocked(GoalSettings settings, StatsRange range, DateTime today)
    {
        var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        data.Days.TryGetValue(todayKey, out var tb);
        var todayChars = tb?.JapaneseChars ?? 0;
        var todaySec = tb?.ActiveSeconds ?? 0;
        var todayCards = tb?.CardsMined ?? 0;

        static bool MetChars(GoalSettings g, int c) => g.DailyCharGoal > 0 && c >= g.DailyCharGoal;
        static bool MetTime(GoalSettings g, double s) => g.DailyMinutesGoal > 0 && s >= g.DailyMinutesGoal * 60.0;
        static bool MetCards(GoalSettings g, int m) => g.DailyCardsGoal > 0 && m >= g.DailyCardsGoal;
        static bool MetAll(GoalSettings g, DailyBucket b) =>
            g.HasAnyDailyGoal
            && (g.DailyCharGoal <= 0 || b.JapaneseChars >= g.DailyCharGoal)
            && (g.DailyMinutesGoal <= 0 || b.ActiveSeconds >= g.DailyMinutesGoal * 60.0)
            && (g.DailyCardsGoal <= 0 || b.CardsMined >= g.DailyCardsGoal);

        var periodDays = range switch
        {
            StatsRange.Today => 1,
            StatsRange.Last7Days => 7,
            StatsRange.Last30Days => 30,
            StatsRange.Last12Months => 365,
            _ => Math.Max(1, data.Days.Count),
        };

        var charDays = 0;
        var timeDays = 0;
        var cardDays = 0;
        var allDays = 0;

        if (range is StatsRange.Today or StatsRange.Last7Days or StatsRange.Last30Days or StatsRange.Last12Months)
        {
            for (var i = 0; i < periodDays; i++)
            {
                var key = today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!data.Days.TryGetValue(key, out var b))
                    continue;
                if (MetChars(settings, b.JapaneseChars)) charDays++;
                if (MetTime(settings, b.ActiveSeconds)) timeDays++;
                if (MetCards(settings, b.CardsMined)) cardDays++;
                if (MetAll(settings, b)) allDays++;
            }
        }
        else
        {
            foreach (var b in data.Days.Values)
            {
                if (MetChars(settings, b.JapaneseChars)) charDays++;
                if (MetTime(settings, b.ActiveSeconds)) timeDays++;
                if (MetCards(settings, b.CardsMined)) cardDays++;
                if (MetAll(settings, b)) allDays++;
            }
        }

        var (curGoalStreak, bestGoalStreak) = ComputeGoalStreaksLocked(settings, today, MetAll);

        return new GoalProgressSummary(
            Settings: settings,
            TodayChars: todayChars,
            TodayActiveSeconds: todaySec,
            TodayCardsMined: todayCards,
            TodayCharsMet: MetChars(settings, todayChars),
            TodayTimeMet: MetTime(settings, todaySec),
            TodayCardsMet: MetCards(settings, todayCards),
            TodayAllMet: tb is not null && MetAll(settings, tb),
            CharGoalDaysMet: charDays,
            TimeGoalDaysMet: timeDays,
            CardGoalDaysMet: cardDays,
            AllGoalsDaysMet: allDays,
            PeriodDayCount: periodDays,
            CurrentGoalStreak: curGoalStreak,
            BestGoalStreak: bestGoalStreak);
    }

    private (int Current, int Best) ComputeGoalStreaksLocked(
        GoalSettings settings,
        DateTime today,
        Func<GoalSettings, DailyBucket, bool> isMet)
    {
        if (!settings.HasAnyDailyGoal)
            return (0, 0);

        var metDates = new SortedSet<DateTime>();
        foreach (var (key, b) in data.Days)
        {
            if (isMet(settings, b)
                && DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                metDates.Add(d);
        }

        if (metDates.Count == 0)
            return (0, 0);

        var best = 0;
        var run = 0;
        DateTime? prev = null;
        foreach (var d in metDates)
        {
            run = prev.HasValue && prev.Value.AddDays(1) == d ? run + 1 : 1;
            if (run > best) best = run;
            prev = d;
        }

        var current = 0;
        var cursor = metDates.Contains(today) ? today : today.AddDays(-1);
        while (metDates.Contains(cursor))
        {
            current++;
            cursor = cursor.AddDays(-1);
        }

        return (current, best);
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

    private List<DayPoint> BuildMonthlySeriesLocked(DateTime today, int months)
    {
        var endMonth = new DateTime(today.Year, today.Month, 1);
        DateTime startMonth;
        if (months > 0)
        {
            startMonth = endMonth.AddMonths(-(months - 1));
        }
        else
        {
            var earliest = endMonth;
            foreach (var key in data.Days.Keys)
            {
                if (DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                {
                    var m = new DateTime(d.Year, d.Month, 1);
                    if (m < earliest) earliest = m;
                }
            }
            startMonth = earliest;
        }

        var totalMonths = (endMonth.Year - startMonth.Year) * 12 + (endMonth.Month - startMonth.Month) + 1;
        totalMonths = Math.Clamp(totalMonths, 1, 60);

        var agg = new Dictionary<string, (int Chars, int Lines, int Lookups, int Cards, double Sec)>(StringComparer.Ordinal);
        foreach (var (key, b) in data.Days)
        {
            if (key.Length < 7) continue;
            var ym = key[..7];
            agg.TryGetValue(ym, out var cur);
            agg[ym] = (cur.Chars + b.JapaneseChars, cur.Lines + b.Lines, cur.Lookups + b.Lookups, cur.Cards + b.CardsMined, cur.Sec + b.ActiveSeconds);
        }

        var list = new List<DayPoint>(totalMonths);
        for (var i = 0; i < totalMonths; i++)
        {
            var m = startMonth.AddMonths(i);
            var ym = m.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var lbl = (i == 0 || m.Month == 1)
                ? m.ToString("MMM yy", CultureInfo.InvariantCulture)
                : m.ToString("MMM", CultureInfo.InvariantCulture);
            if (agg.TryGetValue(ym, out var v))
                list.Add(new DayPoint(ym, lbl, v.Chars, v.Lines, v.Lookups, v.Cards, v.Sec));
            else
                list.Add(new DayPoint(ym, lbl, 0, 0, 0, 0, 0));
        }

        return list;
    }

    private DailyBucket GetOrCreateDayLocked(DateTime logicalDate)
    {
        var key = logicalDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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

    private void TrimImportedKeysLocked()
    {
        if (data.ImportedKeys.Count <= MaxImportedKeys)
            return;
        var keep = data.ImportedKeys.Skip(data.ImportedKeys.Count - (MaxImportedKeys - 20_000)).ToHashSet(StringComparer.Ordinal);
        data.ImportedKeys = keep;
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

    private void RecalculateBestStreakLocked()
    {
        var activeDates = new SortedSet<DateTime>();
        foreach (var (key, b) in data.Days)
        {
            if (b.JapaneseChars > 0
                && DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                activeDates.Add(d);
        }

        var best = data.BestStreakDays;
        var run = 0;
        DateTime? prev = null;
        foreach (var d in activeDates)
        {
            run = prev.HasValue && prev.Value.AddDays(1) == d ? run + 1 : 1;
            if (run > best) best = run;
            prev = d;
        }
        data.BestStreakDays = best;
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
        Make("Heavensward Scale", "Read 186,000 Japanese chars (Heavensward MSQ length)", chars, 186_000),
        Make("Realm Reborn Scale", "Read 447,000 Japanese chars (A Realm Reborn MSQ length)", chars, 447_000),
        Make("Joyo Explorer", "Encounter 1,500 unique kanji", uniqueKanji, 1_500),
        Make("Warrior of Kanji", "Read 1,000,000 Japanese characters", chars, 1_000_000),
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
        d.ImportedKeys ??= new(StringComparer.Ordinal);
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
