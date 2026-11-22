using System;
using System.Collections.Generic;

namespace HachidoriFfxiv.Stats;

public enum StatsRange
{
    Session,
    Today,
    Last7Days,
    Last30Days,
    AllTime,
}

/// <summary>Persisted immersion statistics document (saved atomically as JSON).</summary>
public sealed class StatsData
{
    public int Version { get; set; } = 1;
    public Dictionary<string, DailyBucket> Days { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, NpcProfile> NpcProfiles { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, WordMeta> WordDetails { get; set; } = new(StringComparer.Ordinal);
    public List<MinedCardEntry> MinedCards { get; set; } = new();
    public List<DialogueLogEntry> RecentDialogue { get; set; } = new();
    public LongestMonologueRecord? LongestLine { get; set; }
    public int BestStreakDays { get; set; }
}

public sealed class DailyBucket
{
    public int Lines { get; set; }
    public int TalkLines { get; set; }
    public int BattleTalkLines { get; set; }
    public int ChatLines { get; set; }
    public int JapaneseChars { get; set; }
    public int KanjiChars { get; set; }
    public int Lookups { get; set; }
    public int CardsMined { get; set; }
    public double ActiveSeconds { get; set; }
    public int[] HourlyChars { get; set; } = new int[24];
    public Dictionary<string, NpcDailyStats> Npcs { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ParsedWords { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> LookupWords { get; set; } = new(StringComparer.Ordinal);
    public string UniqueKanji { get; set; } = string.Empty;
}

public sealed class NpcDailyStats
{
    public int Lines { get; set; }
    public int JapaneseChars { get; set; }
}

public sealed class NpcProfile
{
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public string LatestQuote { get; set; } = string.Empty;
    public string LongestQuote { get; set; } = string.Empty;
    public int LongestQuoteChars { get; set; }
}

public sealed class WordMeta
{
    public string Reading { get; set; } = string.Empty;
    public string ShortMeaning { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class MinedCardEntry
{
    public long NoteId { get; set; }
    public string Expression { get; set; } = string.Empty;
    public string Reading { get; set; } = string.Empty;
    public string Sentence { get; set; } = string.Empty;
    public string Speaker { get; set; } = string.Empty;
    public string ShortMeaning { get; set; } = string.Empty;
    public string Frequency { get; set; } = string.Empty;
    public string Pitch { get; set; } = string.Empty;
    public bool HasScreenshot { get; set; }
    public bool HasAudio { get; set; }
    public bool HasAnimation { get; set; }
    public bool HasTranslation { get; set; }
    public DateTime MinedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DialogueLogEntry
{
    public string Source { get; set; } = "Talk";
    public string Speaker { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int JapaneseChars { get; set; }
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LongestMonologueRecord
{
    public string Speaker { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int JapaneseChars { get; set; }
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Precomputed immutable view model for the Stats window (rebuilt only on change).</summary>
public sealed class StatsSnapshot
{
    public required StatsRange Range { get; init; }
    public required long Version { get; init; }
    public required int Lines { get; init; }
    public required int TalkLines { get; init; }
    public required int BattleTalkLines { get; init; }
    public required int ChatLines { get; init; }
    public required long JapaneseChars { get; init; }
    public required long KanjiChars { get; init; }
    public required int UniqueKanjiCount { get; init; }
    public required IReadOnlyList<string> SampleKanji { get; init; }
    public required int Lookups { get; init; }
    public required int CardsMined { get; init; }
    public required double ActiveSeconds { get; init; }
    public required long AllTimeJapaneseChars { get; init; }
    public required int AllTimeUniqueKanji { get; init; }
    public required int AllTimeLines { get; init; }
    public required int AllTimeCardsMined { get; init; }
    public required int AllTimeNpcCount { get; init; }
    public required int CurrentStreakDays { get; init; }
    public required int BestStreakDays { get; init; }
    public required int[] HourlyChars { get; init; }
    public required IReadOnlyList<DayPoint> DailySeries { get; init; }
    public required IReadOnlyList<DayPoint> Heatmap30Days { get; init; }
    public required IReadOnlyList<NpcRankItem> TopNpcs { get; init; }
    public required IReadOnlyList<WordRankItem> TopParsedWords { get; init; }
    public required IReadOnlyList<WordRankItem> TopLookedUpWords { get; init; }
    public required IReadOnlyList<MinedCardEntry> RecentMinedCards { get; init; }
    public required IReadOnlyList<DialogueLogEntry> RecentDialogue { get; init; }
    public LongestMonologueRecord? LongestLine { get; init; }
    public DayPoint? MostActiveDay { get; init; }
    public required IReadOnlyList<AchievementItem> Achievements { get; init; }

    public double CharsPerHour => ActiveSeconds >= 30 ? JapaneseChars / (ActiveSeconds / 3600.0) : 0;
    public double KanjiPercent => JapaneseChars > 0 ? 100.0 * KanjiChars / JapaneseChars : 0;
    public double CharsPerCard => CardsMined > 0 ? (double)JapaneseChars / CardsMined : 0;
}

public sealed record DayPoint(
    string DateKey,
    string ShortLabel,
    int JapaneseChars,
    int Lines,
    int Lookups,
    int CardsMined,
    double ActiveSeconds);

public sealed record NpcRankItem(
    string Speaker,
    int JapaneseChars,
    int Lines,
    double SharePercent,
    string LatestQuote,
    DateTime LastSeenUtc);

public sealed record WordRankItem(
    string Word,
    int Count,
    string Reading,
    string ShortMeaning);

public sealed record AchievementItem(
    string Title,
    string Description,
    long Current,
    long Target,
    bool Unlocked);
