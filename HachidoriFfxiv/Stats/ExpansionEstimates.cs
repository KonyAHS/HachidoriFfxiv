using System;
using System.Collections.Generic;
using System.Linq;

namespace HachidoriFfxiv.Stats;

/// <summary>
/// Estimated Japanese dialogue character volume for a single FFXIV expansion.
/// Counts are measured from FFXIV's Japanese MSQ quest dialogue scripts (kanji + kana)
/// and are intended as a fun ballpark guide — actual immersion varies with side quests,
/// raids, job quests, battle talk, and player chat.
/// </summary>
public sealed record FfxivExpansion(
    string Id,
    string ShortCode,
    string Name,
    string JapaneseName,
    long EstimatedMsqChars,
    long EstimatedAllQuestsChars,
    int EstimatedMsqLines,
    uint AccentRgba);

public sealed record ExpansionProgressItem(
    FfxivExpansion Expansion,
    long CumulativeStart,
    long CumulativeEnd,
    long EarnedChars,
    double PercentComplete,
    bool Completed,
    bool Active);

public sealed record ExpansionJourneySummary(
    long EffectiveChars,
    long TotalMsqChars,
    double TotalMsqPercent,
    FfxivExpansion? FocusExpansion,
    long FocusTargetChars,
    long FocusCurrentChars,
    long FocusRemainingChars,
    double FocusPercent,
    double? EstimatedHoursRemaining,
    double? EstimatedDaysAtGoal,
    string FunComparison,
    IReadOnlyList<ExpansionProgressItem> Expansions);

public static class ExpansionEstimates
{
    public const string AllMsqId = "all";

    /// <summary>
    /// Baseline FFXIV expansion Japanese MSQ dialogue estimates derived from Japanese quest script data
    /// (A Realm Reborn 2.0–2.55 through Dawntrail 7.0–7.x, excluding removed 2.0 quests).
    /// Additional expansions (8.0+) are merged dynamically from live Lumina <c>ExVersion</c> sheets.
    /// </summary>
    private static readonly IReadOnlyList<FfxivExpansion> Baseline =
    [
        new("arr", "2.x", "A Realm Reborn", "新生エオルゼア",     356_000, 2_298_000, 10_380, 0xFF56B8F5),
        new("hw",  "3.x", "Heavensward",    "蒼天のイシュガルド", 174_000, 1_055_000, 4_690,  0xFFFAAD5A),
        new("sb",  "4.x", "Stormblood",     "紅蓮のリベレーター", 247_000, 1_205_000, 6_720,  0xFF6B6AE8),
        new("shb", "5.x", "Shadowbringers", "漆黒のヴィランズ",   310_000, 1_131_000, 8_620,  0xFFEB89A6),
        new("ew",  "6.x", "Endwalker",      "暁月のフィナーレ",   380_000, 1_389_000, 10_800, 0xFFF2C85B),
        new("dt",  "7.x", "Dawntrail",      "黄金のレガシー",     345_000, 1_122_000, 10_320, 0xFF68D698),
    ];

    private static volatile IReadOnlyList<FfxivExpansion> current = Baseline;

    public static IReadOnlyList<FfxivExpansion> All => current;

    public static long TotalMsqChars => current.Sum(e => e.EstimatedMsqChars);

    public static long TotalAllQuestsChars => current.Sum(e => e.EstimatedAllQuestsChars);

    /// <summary>
    /// Appends future expansions (8.0+) discovered from the player's installed FFXIV Excel sheets
    /// while preserving the baseline ARR–Dawntrail entries.
    /// </summary>
    public static void RegisterExtraExpansions(IReadOnlyList<FfxivExpansion> extraExpansions)
    {
        if (extraExpansions.Count == 0)
        {
            current = Baseline;
            return;
        }

        var merged = new List<FfxivExpansion>(Baseline.Count + extraExpansions.Count);
        merged.AddRange(Baseline);
        var existingIds = new HashSet<string>(Baseline.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var exp in extraExpansions)
        {
            if (existingIds.Add(exp.Id))
                merged.Add(exp);
        }
        current = merged;
    }

    internal static void ResetToBaseline() => current = Baseline;

    public static FfxivExpansion? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i].Id, id, StringComparison.OrdinalIgnoreCase))
                return All[i];
        }
        return null;
    }

    /// <summary>Cumulative MSQ characters completed before the given expansion starts.</summary>
    public static long CumulativeBefore(string expansionId)
    {
        long sum = 0;
        foreach (var exp in All)
        {
            if (string.Equals(exp.Id, expansionId, StringComparison.OrdinalIgnoreCase))
                return sum;
            sum += exp.EstimatedMsqChars;
        }
        return 0;
    }

    public static ExpansionJourneySummary Compute(
        long lifetimeJapaneseChars,
        string? focusExpansionId,
        long startingOffsetChars,
        double charsPerHour,
        int dailyCharGoal)
    {
        var safeChars = Math.Max(0L, lifetimeJapaneseChars);
        var safeOffset = Math.Max(0L, startingOffsetChars);
        var effective = safeChars + safeOffset;

        var items = new List<ExpansionProgressItem>(All.Count);
        long cursor = 0;
        var foundActive = false;
        for (var i = 0; i < All.Count; i++)
        {
            var exp = All[i];
            var start = cursor;
            var end = cursor + exp.EstimatedMsqChars;
            var earned = Math.Clamp(effective - start, 0L, exp.EstimatedMsqChars);
            var pct = exp.EstimatedMsqChars > 0 ? 100.0 * earned / exp.EstimatedMsqChars : 0.0;
            var completed = effective >= end;
            var active = !completed && !foundActive;
            if (active)
                foundActive = true;
            items.Add(new ExpansionProgressItem(exp, start, end, earned, pct, completed, active));
            cursor = end;
        }

        var focus = FindById(focusExpansionId);
        long focusTarget;
        long focusCurrent;
        if (focus is not null)
        {
            // When focusing a specific expansion, measure the user's progress within that expansion
            // (using their cumulative journey position if they have an offset/prior progress, or direct chars if starting from 0).
            var item = items.First(x => x.Expansion.Id == focus.Id);
            focusTarget = focus.EstimatedMsqChars;
            focusCurrent = safeOffset > 0 || safeChars >= item.CumulativeStart
                ? item.EarnedChars
                : Math.Min(safeChars, focus.EstimatedMsqChars);
        }
        else
        {
            focusTarget = TotalMsqChars;
            focusCurrent = Math.Min(effective, TotalMsqChars);
        }

        var remaining = Math.Max(0L, focusTarget - focusCurrent);
        var focusPct = focusTarget > 0 ? Math.Clamp(100.0 * focusCurrent / focusTarget, 0.0, 100.0) : 0.0;
        double? hoursLeft = remaining > 0 && charsPerHour >= 100 ? remaining / charsPerHour : null;
        double? daysLeft = remaining > 0 && dailyCharGoal > 0 ? (double)remaining / dailyCharGoal : null;

        var totalPct = TotalMsqChars > 0 ? Math.Clamp(100.0 * effective / TotalMsqChars, 0.0, 100.0) : 0.0;
        var fun = FormatFunComparison(safeChars);

        return new ExpansionJourneySummary(
            EffectiveChars: effective,
            TotalMsqChars: TotalMsqChars,
            TotalMsqPercent: totalPct,
            FocusExpansion: focus,
            FocusTargetChars: focusTarget,
            FocusCurrentChars: focusCurrent,
            FocusRemainingChars: remaining,
            FocusPercent: focusPct,
            EstimatedHoursRemaining: hoursLeft,
            EstimatedDaysAtGoal: daysLeft,
            FunComparison: fun,
            Expansions: items);
    }

    public static string FormatFunComparison(long japaneseChars)
    {
        if (japaneseChars <= 0)
            return "Ready for Eorzea";
        if (japaneseChars < 20_000)
            return $"≈ {100.0 * japaneseChars / All[1].EstimatedMsqChars:0.0}% of Heavensward MSQ";
        if (japaneseChars < All[1].EstimatedMsqChars)
            return $"≈ {100.0 * japaneseChars / All[1].EstimatedMsqChars:0}% of Heavensward MSQ";
        if (japaneseChars < All[0].EstimatedMsqChars)
            return $"≈ {japaneseChars / (double)All[1].EstimatedMsqChars:0.0}× Heavensward MSQ";
        if (japaneseChars < TotalMsqChars)
            return $"≈ {100.0 * japaneseChars / TotalMsqChars:0.0}% of full FFXIV MSQ";
        return $"≈ {japaneseChars / (double)TotalMsqChars:0.00}× full FFXIV MSQ!";
    }
}
