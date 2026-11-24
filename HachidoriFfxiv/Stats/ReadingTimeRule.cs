using System;
using System.Collections.Generic;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Stats;

/// <summary>
/// Implements bee-san's Rubi / LearnFromGames adaptive reading-time rule, skip-mode filter,
/// ecosystem character counting (matching Rubi, LearnFromGames, GSM, and Tidepool),
/// 04:00 logical day turnover, and goal-stepping helpers.
/// </summary>
public static class ReadingTimeRule
{
    private const int PaceUnitShift = 6; // 64 µs per unit
    public const int PaceStart = 18_750; // 1.2 s/char (~3,000 chars/hr)
    public const int PaceMin = 32;
    public const int PaceMax = 0xFFFF;

    private const long CapFloorUs = 2_000_000L;       // 2 seconds
    private const long CapCeilingUs = 300_000_000L;   // 5 minutes
    private const long CapPerUnitUs = 160L;           // 2.5 × 64 µs
    private const int PaceMinChars = 5;
    private const long PaceMinGapUs = 1_000_000L;

    // Skip mode: 3+ consecutive lines of >= 12 chars each advancing in < 1.0 s.
    public const long SkimGapUs = 1_000_000L;
    public const int SkimChars = 12;
    public const int SkimLines = 3;

    /// <summary>Goal step presets matching bee-san/LearnFromGames ReadingGoals.</summary>
    public static readonly int[] MinuteGoalSteps = [0, 15, 30, 45, 60, 90, 120, 150, 180, 240, 300, 360, 480];
    public static readonly int[] CardGoalSteps = [0, 1, 2, 3, 5, 10, 15, 20, 25, 30, 40, 50];
    public static readonly int[] CharGoalSteps = [0, 500, 1_000, 2_000, 3_000, 4_000, 5_000, 7_500, 10_000, 15_000, 20_000, 25_000, 30_000, 40_000, 50_000, 75_000, 100_000];

    public static int StepValue(int[] steps, int current, bool up)
    {
        if (up)
        {
            for (var i = 0; i < steps.Length; i++)
            {
                if (steps[i] > current)
                    return steps[i];
            }
            return steps[^1];
        }

        for (var i = steps.Length - 1; i >= 0; i--)
        {
            if (steps[i] < current)
                return steps[i];
        }
        return steps[0];
    }

    /// <summary>
    /// Counts characters as Rubi, LearnFromGames, GameSentenceMiner, and Tidepool count them:
    /// Japanese letters (kanji, hiragana, katakana, iteration marks) and ASCII letters/digits,
    /// excluding punctuation, brackets, spaces, and symbols.
    /// </summary>
    public static int CountEcosystemChars(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp < 0x80)
            {
                if (cp is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
                    count++;
            }
            else if (Japanese.IsKanji(cp)
                     || Japanese.IsKana(cp)
                     || cp is 0x3005 or 0x3007 or (>= 0x3031 and <= 0x3035) or (>= 0x3038 and <= 0x303C)
                     || cp is 0x3099 or 0x309A or (>= 0x309D and <= 0x309F)
                     || cp is (>= 0x30FC and <= 0x30FF)
                     || cp is (>= 0xFF10 and <= 0xFF19) or (>= 0xFF21 and <= 0xFF3A) or (>= 0xFF41 and <= 0xFF5A))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Computes the local logical date for a timestamp, shifting back by <paramref name="dayStartHour"/>
    /// (e.g. 4 means 04:00 AM turnover so past-midnight sessions stay on the same evening).
    /// </summary>
    public static DateTime LogicalDate(DateTime utcTime, int dayStartHour)
    {
        var local = utcTime.Kind == DateTimeKind.Local ? utcTime : utcTime.ToLocalTime();
        var shift = Math.Clamp(dayStartHour, 0, 12);
        return shift > 0 ? local.AddHours(-shift).Date : local.Date;
    }

    /// <summary>
    /// Rubi's adaptive reading-time rule: credits <paramref name="gapUs"/> capped at
    /// 2.5× the learner's running 75th-percentile pace for <paramref name="chars"/> (clamped 2 s – 5 min).
    /// </summary>
    public static (long CreditUs, int NextPace) AdaptiveCredit(long gapUs, int chars, int pace)
    {
        if (gapUs <= 0)
            return (0, pace);

        var gap = Math.Min(gapUs, 0xFFFF_FFFFL);
        var p = pace == 0 ? PaceStart : pace;
        var expect = (long)p * Math.Max(0, chars);
        var cap = Math.Max(
            expect >= CapCeilingUs / CapPerUnitUs ? CapCeilingUs : expect * CapPerUnitUs,
            CapFloorUs);

        var after = pace;
        if (chars >= PaceMinChars && gap >= PaceMinGapUs && gap <= CapCeilingUs)
        {
            p = (gap >> PaceUnitShift) > expect ? p + 3 * (p >> 5) : p - (p >> 5);
            after = Math.Clamp(p, PaceMin, PaceMax);
        }

        return (Math.Min(gap, cap), after);
    }

    /// <summary>
    /// Identifies indices of lines dropped by Rubi's skip-mode filter:
    /// 3 consecutive lines of >= 12 characters each credited under 1 second,
    /// plus every subsequent fast line until a normal read or break occurs.
    /// </summary>
    public static HashSet<int> DetectSkippedIndices(IReadOnlyList<(long CreditUs, int Chars)> lines)
    {
        var skipped = new HashSet<int>();
        var run = new List<int>(SkimLines);
        var skipping = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var (credit, chars) = lines[i];
            if (credit == 0 || credit >= SkimGapUs)
            {
                run.Clear();
                skipping = false;
            }
            else if (skipping)
            {
                skipped.Add(i);
            }
            else if (chars >= SkimChars)
            {
                run.Add(i);
                if (run.Count == SkimLines)
                {
                    foreach (var idx in run)
                        skipped.Add(idx);
                    run.Clear();
                    skipping = true;
                }
            }
        }

        return skipped;
    }

    /// <summary>
    /// Batch-credits a time-ordered sequence of line timestamps and character counts using Rubi's adaptive rule.
    /// </summary>
    public static (double[] Seconds, bool[] Skipped) CreditSequence(IReadOnlyList<(double UnixSeconds, int Chars, string GameId)> lines)
    {
        var n = lines.Count;
        var credits = new (long CreditUs, int Chars)[n];
        var paces = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < n; i++)
        {
            var cur = lines[i];
            long us = 0;
            if (i + 1 < n)
            {
                var next = lines[i + 1];
                if (string.Equals(next.GameId, cur.GameId, StringComparison.Ordinal) && next.UnixSeconds > cur.UnixSeconds)
                {
                    var gapUs = (long)Math.Round((next.UnixSeconds - cur.UnixSeconds) * 1_000_000.0);
                    paces.TryGetValue(cur.GameId, out var pace);
                    var (creditUs, nextPace) = AdaptiveCredit(gapUs, cur.Chars, pace);
                    us = creditUs;
                    paces[cur.GameId] = nextPace;
                }
            }
            credits[i] = (us, cur.Chars);
        }

        var skippedSet = DetectSkippedIndices(credits);
        var seconds = new double[n];
        var skipped = new bool[n];
        for (var i = 0; i < n; i++)
        {
            if (skippedSet.Contains(i))
            {
                skipped[i] = true;
                seconds[i] = 0;
            }
            else
            {
                seconds[i] = credits[i].CreditUs / 1_000_000.0;
            }
        }

        return (seconds, skipped);
    }

    /// <summary>
    /// Formats a period-over-period delta ("Up 18% vs prior 7d", "Down 5% vs prior 30d"),
    /// inspired by bee-san's LearnFromGames and Telly.
    /// </summary>
    public static string FormatChange(double current, double previous, string priorLabel)
    {
        if (previous <= 0)
            return current <= 0 ? string.Empty : $"New vs {priorLabel}";
        var pct = (int)Math.Round(100.0 * (current - previous) / previous);
        if (pct == 0)
            return $"Same as {priorLabel}";
        return pct > 0 ? $"+{pct}% vs {priorLabel}" : $"{pct}% vs {priorLabel}";
    }
}
