using System;
using System.Collections.Generic;
using System.Linq;

namespace HachidoriFfxiv.Stats;

/// <summary>One live FFXIV Main Scenario Quest with its Japanese dialogue character count and cumulative MSQ offset.</summary>
public sealed record MsqQuestEntry(
    uint RowId,
    string ExpansionId,
    string ExpansionShortCode,
    string JapaneseName,
    string EnglishName,
    string GenreName,
    int QuestChars,
    long CumulativeBeforeChars,
    long ExpansionBeforeChars);

/// <summary>
/// Searchable chronological index of FFXIV MSQ quests used to resolve a quest name (in Japanese or English)
/// into its cumulative Japanese character offset across the MSQ.
/// </summary>
public sealed class MsqQuestIndex
{
    public IReadOnlyList<MsqQuestEntry> Quests { get; }
    public IReadOnlyDictionary<string, long> ExpansionTotals { get; }
    public long TotalMsqChars { get; }

    public MsqQuestIndex(IReadOnlyList<MsqQuestEntry> orderedQuests)
    {
        Quests = orderedQuests;
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long grand = 0;
        foreach (var q in orderedQuests)
        {
            totals.TryGetValue(q.ExpansionId, out var cur);
            totals[q.ExpansionId] = cur + q.QuestChars;
            grand += q.QuestChars;
        }
        ExpansionTotals = totals;
        TotalMsqChars = grand;
    }

    /// <summary>
    /// Searches MSQ quests by Japanese or English name (exact matches first, then starts-with, then substring).
    /// </summary>
    public IReadOnlyList<MsqQuestEntry> Search(string? query, int maxResults = 8)
    {
        var needle = Normalize(query);
        if (needle.Length == 0 || Quests.Count == 0)
            return [];

        var ranked = new List<(int Score, int Index, MsqQuestEntry Entry)>();
        for (var i = 0; i < Quests.Count; i++)
        {
            var q = Quests[i];
            var score = ScoreMatch(q, needle);
            if (score > 0)
                ranked.Add((score, i, q));
        }

        ranked.Sort((a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
        });

        var count = Math.Min(maxResults, ranked.Count);
        var result = new List<MsqQuestEntry>(count);
        for (var i = 0; i < count; i++)
            result.Add(ranked[i].Entry);
        return result;
    }

    public MsqQuestEntry? FindBest(string? query)
    {
        var matches = Search(query, maxResults: 1);
        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>
    /// Computes the prior-MSQ starting offset so that <c>offset + alreadyTrackedChars</c> lands right on
    /// <paramref name="quest"/> without double-counting characters already recorded by Hachidori.
    /// </summary>
    public static long ComputeStartingOffset(MsqQuestEntry quest, long alreadyTrackedChars) =>
        Math.Max(0L, quest.CumulativeBeforeChars - Math.Max(0L, alreadyTrackedChars));

    public static string ExpansionIdFromIndex(uint expansionIndex) => expansionIndex switch
    {
        0 => "arr",
        1 => "hw",
        2 => "sb",
        3 => "shb",
        4 => "ew",
        5 => "dt",
        _ => $"ex{expansionIndex}",
    };

    public static string ExpansionShortCodeFromIndex(uint expansionIndex) => expansionIndex switch
    {
        0 => "2.x",
        1 => "3.x",
        2 => "4.x",
        3 => "5.x",
        4 => "6.x",
        5 => "7.x",
        _ => $"{expansionIndex + 2}.x",
    };

    /// <summary>
    /// Filters FFXIV <c>JournalGenre</c>/<c>JournalCategory</c> rows for Main Scenario Quests.
    /// Excludes only removed 2.0 quests (0) and the "Tales of..." side-story genres (79–81)
    /// so future expansion MSQ genres allocated above 81 remain included.
    /// </summary>
    public static bool IsMsqGenre(uint genreId, string? japaneseCategoryName, string? englishCategoryName = null)
    {
        if (genreId is 0 or 79 or 80 or 81)
            return false;
        if (!string.IsNullOrEmpty(japaneseCategoryName)
            && japaneseCategoryName.Contains("メインクエスト", StringComparison.Ordinal))
            return true;
        if (!string.IsNullOrEmpty(englishCategoryName)
            && englishCategoryName.Contains("Main Scenario", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static int ScoreMatch(MsqQuestEntry q, string needle)
    {
        var ja = Normalize(q.JapaneseName);
        var en = Normalize(q.EnglishName);
        var best = 0;

        if (ja.Length > 0)
        {
            if (ja.Equals(needle, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 100);
            else if (ja.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 75);
            else if (ja.Contains(needle, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 50);
        }

        if (en.Length > 0)
        {
            if (en.Equals(needle, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 100);
            else if (en.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 70);
            else if (en.Contains(needle, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, 45);
        }

        return best;
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        return text.Trim().Replace('　', ' ');
    }
}
