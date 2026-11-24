using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;
using HachidoriFfxiv.Stats;

namespace HachidoriFfxiv.Ui;

/// <summary>
/// Immersion statistics dashboard: reading volume, Rubi adaptive speed, streaks, FFXIV rank,
/// customizable daily & long-term goals, FFXIV expansion character estimates, ecosystem sync/export
/// (exSTATic, GameSentenceMiner, Rubi, Tidepool, Kechimochi, SubMiner), chattiest NPCs,
/// live dialogue feed, most parsed non-trivial words, kanji explorer, and Anki cards.
/// </summary>
public sealed class StatsWindow : Window
{
    // Palette: blue for reading data, lavender for level and people, gold for streaks and
    // achievements, green for goals met, red for errors. Everything else uses the theme.
    private static readonly Vector4 Accent = new(0.47f, 0.66f, 0.92f, 1f);
    private static readonly Vector4 Lavender = new(0.70f, 0.62f, 0.94f, 1f);
    private static readonly Vector4 Gold = new(0.93f, 0.74f, 0.38f, 1f);
    private static readonly Vector4 Good = new(0.45f, 0.78f, 0.55f, 1f);
    private static readonly Vector4 Bad = new(0.92f, 0.50f, 0.50f, 1f);
    private static readonly Vector4 MutedText = new(0.68f, 0.70f, 0.76f, 1f);

    private static readonly (StatsRange Range, string Label)[] Ranges =
    [
        (StatsRange.Session, "This session"),
        (StatsRange.Today, "Today"),
        (StatsRange.Last7Days, "Last 7 days"),
        (StatsRange.Last30Days, "Last 30 days"),
        (StatsRange.Last12Months, "Last 12 months"),
        (StatsRange.AllTime, "All time"),
    ];

    private readonly Configuration config;
    private readonly StatsTracker tracker;
    private readonly AnkiCardFeed ankiFeed;
    private readonly PopupManager popups;
    private readonly Fonts fonts;
    private readonly FfxivMsqResolver msqResolver;

    private StatsRange selectedRange = StatsRange.Last7Days;
    private StatsSnapshot? cachedSnapshot;
    private StatsRange cachedRange = (StatsRange)(-1);
    private long cachedVersion = -1;
    private GoalSettings? cachedGoalSettings;

    // Snapshots are built on a worker so a cache miss (up to ~20 ms for a year of history)
    // never stalls the draw thread. The previous snapshot stays on screen until the new one lands.
    private Task<StatsSnapshot>? snapshotTask;
    private (StatsRange Range, GoalSettings Goals) snapshotTaskKey;
    private (StatsRange Range, long Version, GoalSettings Goals)? failedSnapshotKey;

    // Data-tab file actions (import, export, clear) run one at a time on a worker. Their results
    // come back through this queue and are applied on the draw thread (status, clipboard).
    private readonly ConcurrentQueue<Action> uiActions = new();
    private bool dataActionBusy;
    private string dataActionLabel = string.Empty;

    private readonly List<RichBlock> dialogueBlocks = new();
    private readonly List<RichBlock> minedCardBlocks = new();

    private string dialogueFilter = string.Empty;
    private string wordFilter = string.Empty;
    private int wordMode; // 0 = Parsed in Dialogue, 1 = Most Looked Up ("Nemesis")
    private int ankiViewMode; // 0 = Mined in FFXIV, 1 = Latest from AnkiConnect
    private bool confirmClear;
    private string? statusBanner;
    private bool statusIsError;
    private DateTime statusBannerUntil = DateTime.MinValue;

    // MSQ quest offset lookup & live detection state
    private string msqSearchQuery = string.Empty;
    private MsqQuestEntry? detectedMsq;
    private bool detectedMsqAccepted;
    private DateTime lastMsqDetectUtc = DateTime.MinValue;

    // Ecosystem import / GSM sync UI state
    private string importPath = string.Empty;
    private string importTitleFilter = string.Empty;
    private bool gsmSyncBusy;

    public StatsWindow(
        Configuration config,
        StatsTracker tracker,
        AnkiCardFeed ankiFeed,
        PopupManager popups,
        Fonts fonts,
        FfxivMsqResolver msqResolver)
        : base("Hachidori Immersion Stats##hachidori-ffxiv-stats")
    {
        this.config = config;
        this.tracker = tracker;
        this.ankiFeed = ankiFeed;
        this.popups = popups;
        this.fonts = fonts;
        this.msqResolver = msqResolver;
        msqSearchQuery = config.GoalMsqQuestName ?? string.Empty;
        Size = new Vector2(920, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 460),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void OnOpen()
    {
        ankiFeed.RefreshIfStale(TimeSpan.FromMinutes(2));
    }

    public override void Draw()
    {
        while (uiActions.TryDequeue(out var uiAction))
            uiAction();

        var snap = GetCurrentSnapshot();
        var scale = ImGuiHelpers.GlobalScale;

        if (statusBanner is not null && DateTime.UtcNow < statusBannerUntil)
        {
            ImGui.TextColored(statusIsError ? Bad : Good, statusBanner);
        }
        else if (statusBanner is not null)
        {
            statusBanner = null;
            statusIsError = false;
        }

        // One row: tabs on the left, the period picker on the right.
        var rowStart = ImGui.GetCursorScreenPos();
        var rowRight = rowStart.X + ImGui.GetContentRegionAvail().X;
        using var tabs = ImRaii.TabBar("##stats-tabs");
        if (!tabs.Success)
            return;
        var contentStart = ImGui.GetCursorScreenPos();
        DrawRangePicker(rowStart.Y, rowRight, scale);
        ImGui.SetCursorScreenPos(contentStart);

        // "###id" keeps a tab selected while its count changes.
        DrawStatsTab("Overview###overview", () => DrawOverviewTab(snap, scale));
        DrawStatsTab("Goals###goals", () => DrawGoalsAndExpansionsTab(snap, scale));
        DrawStatsTab($"NPCs ({snap.TopNpcs.Count})###npcs", () => DrawNpcTab(snap, scale));
        DrawStatsTab("Words###words", () => DrawWordsTab(snap, scale));
        DrawStatsTab($"Anki ({snap.RecentMinedCards.Count})###anki", () => DrawAnkiTab(snap, scale));
        DrawStatsTab("Achievements###achievements", () => DrawAchievementsTab(snap, scale));
        DrawStatsTab("Data###data", () => DrawEcosystemTab(scale));
    }

    private static void DrawStatsTab(string label, Action drawBody)
    {
        using var tab = ImRaii.TabItem(label);
        if (!tab.Success)
            return;
        using var scroll = ImRaii.Child($"##scroll-{label[(label.IndexOf("###", StringComparison.Ordinal) + 3)..]}", Vector2.Zero, false);
        if (scroll.Success)
        {
            ImGui.Spacing();
            drawBody();
        }
    }

    private void DrawRangePicker(float rowY, float rowRight, float scale)
    {
        var comboW = 130 * scale;
        var x = rowRight - comboW;
        if (selectedRange == StatsRange.Session)
        {
            var resetW = ImGui.CalcTextSize("Reset").X + ImGui.GetStyle().FramePadding.X * 2;
            x -= resetW + 6 * scale;
            ImGui.SetCursorScreenPos(new Vector2(x, rowY));
            if (ImGui.Button("Reset##session"))
            {
                tracker.ResetSession();
                SetStatus("Session counters reset.");
            }
            Tooltip("Reset this session's counters.");
            x += resetW + 6 * scale;
        }

        ImGui.SetCursorScreenPos(new Vector2(x, rowY));
        ImGui.SetNextItemWidth(comboW);
        var current = Array.Find(Ranges, r => r.Range == selectedRange).Label;
        using var combo = ImRaii.Combo("##stats-range", current);
        if (!combo.Success)
            return;
        foreach (var (range, label) in Ranges)
        {
            if (ImGui.Selectable(label, range == selectedRange))
                selectedRange = range;
        }
    }

    private GoalSettings CurrentGoalSettings() => new(
        DailyCharGoal: Math.Max(0, config.DailyCharGoal),
        DailyMinutesGoal: Math.Max(0, config.DailyMinutesGoal),
        DailyCardsGoal: Math.Max(0, config.DailyCardsGoal),
        CustomCharGoal: Math.Max(0, config.CustomCharGoal),
        CustomGoalLabel: config.CustomGoalLabel ?? string.Empty,
        GoalExpansionId: string.IsNullOrWhiteSpace(config.GoalExpansionId) ? ExpansionEstimates.AllMsqId : config.GoalExpansionId,
        ExpansionStartingChars: Math.Max(0, config.ExpansionStartingChars),
        DayStartHour: Math.Clamp(config.StatsDayStartHour, 0, 12));

    private StatsSnapshot GetCurrentSnapshot()
    {
        var ver = tracker.Version;
        var goals = CurrentGoalSettings();
        if (snapshotTask is { IsCompleted: true } done)
        {
            snapshotTask = null;
            if (done.IsCompletedSuccessfully)
            {
                AdoptSnapshot(done.Result, snapshotTaskKey.Range, snapshotTaskKey.Goals);
            }
            else
            {
                failedSnapshotKey = (snapshotTaskKey.Range, ver, snapshotTaskKey.Goals);
                DiagnosticLog.Write("stats", $"Could not build stats snapshot: {done.Exception?.GetBaseException().Message}");
            }
        }

        if (cachedSnapshot is not null && cachedRange == selectedRange && cachedVersion == ver && goals == cachedGoalSettings)
            return cachedSnapshot;

        // First open: build once synchronously so there is something to draw.
        if (cachedSnapshot is null)
        {
            AdoptSnapshot(tracker.GetSnapshot(selectedRange, goals), selectedRange, goals);
            return cachedSnapshot!;
        }

        if (snapshotTask is null && failedSnapshotKey != (selectedRange, ver, goals))
        {
            var range = selectedRange;
            snapshotTaskKey = (range, goals);
            snapshotTask = Task.Run(() => tracker.GetSnapshot(range, goals));
        }

        return cachedSnapshot;
    }

    private void AdoptSnapshot(StatsSnapshot snapshot, StatsRange range, GoalSettings goals)
    {
        cachedSnapshot = snapshot;
        cachedRange = range;
        cachedVersion = snapshot.Version;
        cachedGoalSettings = goals;

        dialogueBlocks.Clear();
        foreach (var entry in cachedSnapshot.RecentDialogue)
            dialogueBlocks.Add(new RichBlock().Add(entry.Text));

        minedCardBlocks.Clear();
        foreach (var card in cachedSnapshot.RecentMinedCards)
            minedCardBlocks.Add(new RichBlock().Add(card.Sentence));
    }

    // -------------------------------------------------------------- overview

    private void DrawOverviewTab(StatsSnapshot snap, float scale)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var isMonthly = snap.Range is StatsRange.Last12Months or StatsRange.AllTime;
        var dailyGoalLine = !isMonthly && config.DailyCharGoal > 0 ? config.DailyCharGoal : 0;

        DrawSummaryRow(snap);
        ImGui.Spacing();
        DrawLevel(snap, scale);
        if (snap.Goals is { Settings.HasAnyDailyGoal: true } g)
            DrawTodayGoals(g);

        var chartTitle = snap.Range switch
        {
            StatsRange.Last7Days => "Characters per day, last 7 days",
            StatsRange.Last12Months => "Characters per month, last 12 months",
            StatsRange.AllTime => "Characters per month",
            _ => "Characters per day, last 30 days",
        };
        SectionTitle(chartTitle);
        DrawDailyBarChart(snap.DailySeries, avail, 150 * scale, dailyGoalLine, scale);

        // The chart already shows the last 30 days for these ranges; the heatmap would repeat it.
        if (snap.Range is StatsRange.Last7Days or StatsRange.Last12Months or StatsRange.AllTime)
        {
            SectionTitle("Last 30 days");
            DrawHeatmap30(snap.Heatmap30Days, avail, config.DailyCharGoal, scale);
        }

        ImGui.Spacing();
        var colW = Math.Max(260 * scale, (avail - 12 * scale) * 0.5f);
        using var table = ImRaii.Table("##overview-split", 2, ImGuiTableFlags.SizingStretchSame);
        if (!table.Success)
            return;
        ImGui.TableNextColumn();
        DrawImmersionClock(snap, colW - 8 * scale, scale);
        ImGui.TableNextColumn();
        DrawHighlights(snap);
    }

    private static void DrawSummaryRow(StatsSnapshot snap)
    {
        var charsSub = snap.Comparison is { CharsDeltaText.Length: > 0 } c1
            ? $"{c1.CharsDeltaText} · {snap.KanjiPercent:0}% kanji"
            : $"{snap.KanjiPercent:0}% kanji";
        var pace = snap.CharsPerHour > 0 ? $"{snap.CharsPerHour:N0}/h · {FormatDuration(snap.ActiveSeconds)}" : FormatDuration(snap.ActiveSeconds);
        var linesSub = snap.Comparison is { TimeDeltaText.Length: > 0 } c2 ? $"{pace} ({c2.TimeDeltaText})" : pace;
        var cardsSub = snap.CardsMined > 0
            ? $"1 per {snap.CharsPerCard:N0} chars" + (snap.Comparison is { CardsDeltaText.Length: > 0 } c3 ? $" · {c3.CardsDeltaText}" : string.Empty)
            : $"{snap.Lookups:N0} lookups";
        var joyoPct = Math.Min(100.0, snap.UniqueKanjiCount / 2136.0 * 100.0);

        using var table = ImRaii.Table("##summary", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame);
        if (!table.Success)
            return;
        SummaryCell(FontAwesomeIcon.BookOpen, "Characters", $"{snap.JapaneseChars:N0}", charsSub, Accent);
        SummaryCell(FontAwesomeIcon.Comments, "Lines", $"{snap.Lines:N0}", linesSub, Accent);
        SummaryCell(FontAwesomeIcon.Clone, "Cards mined", $"{snap.CardsMined:N0}", cardsSub, Gold);
        SummaryCell(FontAwesomeIcon.Language, "Unique kanji", $"{snap.UniqueKanjiCount:N0}", $"{joyoPct:0}% of Joyo", Lavender);
    }

    private static void SummaryCell(FontAwesomeIcon icon, string label, string value, string sub, Vector4 color)
    {
        ImGui.TableNextColumn();
        Icon(icon, color);
        ImGui.SameLine(0, 6 * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled(label);
        ImGui.SetWindowFontScale(1.4f);
        ImGui.TextUnformatted(value);
        ImGui.SetWindowFontScale(1f);
        ImGui.TextDisabled(TruncateToWidth(sub, ImGui.GetContentRegionAvail().X));
    }

    private static void DrawLevel(StatsSnapshot snap, float scale)
    {
        var (level, title, _, floor, next) = NonTrivialWords.GetRank(snap.AllTimeJapaneseChars);
        var total = snap.AllTimeJapaneseChars;

        ImGui.TextColored(Lavender, $"Lv.{level}");
        ImGui.SameLine();
        ImGui.TextUnformatted(title);
        ImGui.SameLine(0, 10 * scale);
        ImGui.TextDisabled(next is { } target
            ? $"{total:N0} / {target:N0} characters to the next rank"
            : $"{total:N0} characters · top rank");

        var streak = snap.CurrentStreakDays > 0
            ? $"{snap.CurrentStreakDays}-day streak · best {snap.BestStreakDays}"
            : $"Best streak {snap.BestStreakDays} days";
        var streakColor = snap.CurrentStreakDays > 0 ? Gold : MutedText;
        var streakW = ImGui.CalcTextSize(streak).X + 22 * scale;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - streakW));
        Icon(FontAwesomeIcon.Fire, streakColor);
        ImGui.SameLine(0, 6 * scale);
        ImGui.TextColored(streakColor, streak);

        var progress = next is { } t ? Math.Clamp((float)(total - floor) / Math.Max(1, t - floor), 0f, 1f) : 1f;
        Bar(progress, new Vector2(-1, 6 * scale), string.Empty, Lavender);
    }

    private static void DrawTodayGoals(GoalProgressSummary g)
    {
        var items = new List<(string Label, float Ratio, string Value, bool Met)>();
        if (g.Settings.DailyCharGoal > 0)
            items.Add(("Characters today", (float)g.TodayChars / g.Settings.DailyCharGoal, $"{g.TodayChars:N0} / {g.Settings.DailyCharGoal:N0}", g.TodayCharsMet));
        if (g.Settings.DailyMinutesGoal > 0)
        {
            var mins = g.TodayActiveSeconds / 60.0;
            items.Add(("Reading time today", (float)(mins / g.Settings.DailyMinutesGoal), $"{mins:0} / {g.Settings.DailyMinutesGoal} min", g.TodayTimeMet));
        }
        if (g.Settings.DailyCardsGoal > 0)
            items.Add(("Cards today", (float)g.TodayCardsMined / g.Settings.DailyCardsGoal, $"{g.TodayCardsMined} / {g.Settings.DailyCardsGoal}", g.TodayCardsMet));
        if (items.Count == 0)
            return;

        ImGui.Spacing();
        using var table = ImRaii.Table("##today-goals", items.Count, ImGuiTableFlags.SizingStretchSame);
        if (!table.Success)
            return;
        foreach (var (label, ratio, value, met) in items)
        {
            ImGui.TableNextColumn();
            ImGui.TextDisabled(label);
            Bar(ratio, new Vector2(-1, 0), value, met ? Good : Accent);
        }
    }

    private static void DrawExpansionJourneyStrip(ExpansionJourneySummary journey, float width, float scale)
    {
        var lastExpName = journey.Expansions.Count > 0 ? journey.Expansions[^1].Expansion.Name : "Dawntrail";
        ImGui.TextUnformatted(journey.FocusExpansion is { } f ? $"{f.Name} MSQ" : $"Full MSQ (ARR to {lastExpName})");

        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var barH = 10 * scale;
        draw.AddRectFilled(pos, pos + new Vector2(width, barH), ImGui.GetColorU32(ImGuiCol.FrameBg));

        // One segment per expansion, sized by its share of the MSQ.
        var fillCol = ImGui.ColorConvertFloat4ToU32(Accent);
        var sepCol = ImGui.GetColorU32(ImGuiCol.WindowBg);
        var x = pos.X;
        for (var i = 0; i < journey.Expansions.Count; i++)
        {
            var item = journey.Expansions[i];
            var segW = width * item.Expansion.EstimatedMsqChars / Math.Max(1, journey.TotalMsqChars);
            var fillW = segW * (float)(item.PercentComplete / 100.0);
            if (fillW > 1f)
                draw.AddRectFilled(new Vector2(x, pos.Y), new Vector2(x + fillW, pos.Y + barH), fillCol);
            if (i > 0)
                draw.AddLine(new Vector2(x, pos.Y), new Vector2(x, pos.Y + barH), sepCol, 2f);
            x += segW;
        }
        ImGui.Dummy(new Vector2(width, barH + 2 * scale));

        var etaParts = new List<string>
        {
            $"{journey.FocusCurrentChars:N0} of about {journey.FocusTargetChars:N0} chars ({journey.FocusPercent:0.0}%)",
        };
        if (journey.EstimatedHoursRemaining is { } hrs)
            etaParts.Add($"about {hrs:0.0} h left at your pace");
        if (journey.EstimatedDaysAtGoal is { } days)
            etaParts.Add($"{Math.Ceiling(days):0} days at your daily goal");
        ImGui.TextDisabled(string.Join("  ·  ", etaParts));
    }

    private static void DrawDailyBarChart(IReadOnlyList<DayPoint> series, float width, float height, int goalChars, float scale)
    {
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var max = pos + new Vector2(width, height);

        ImGui.InvisibleButton("##daily-chart", new Vector2(width, height));
        if (series.Count == 0)
            return;

        var padT = 18 * scale;
        var padB = 20 * scale;
        var plotMin = new Vector2(pos.X, pos.Y + padT);
        var plotSize = new Vector2(Math.Max(40, width), Math.Max(40, height - padT - padB));
        var dataPeak = Math.Max(1, series.Max(p => p.JapaneseChars));
        var maxVal = Math.Max(dataPeak, goalChars > 0 ? (int)(goalChars * 1.15) : 1);

        var gridCol = ImGui.GetColorU32(ImGuiCol.Separator);
        var baseY = plotMin.Y + plotSize.Y;
        draw.AddLine(new Vector2(plotMin.X, baseY), new Vector2(plotMin.X + plotSize.X, baseY), gridCol, 1f);

        var step = plotSize.X / series.Count;
        var barGap = Math.Clamp(step * 0.25f, 2f, 10f);
        var barW = Math.Max(3f, step - barGap);
        var mouse = ImGui.GetMousePos();
        var hovered = ImGui.IsItemHovered();
        DayPoint? hoveredPoint = null;
        var muted = ImGui.ColorConvertFloat4ToU32(MutedText);

        var labelStep = series.Count <= 12 ? 1 : 5;
        for (var i = 0; i < series.Count; i++)
        {
            var p = series[i];
            var x0 = plotMin.X + i * step + barGap * 0.5f;
            var x1 = x0 + barW;
            var barH = p.JapaneseChars > 0 ? Math.Max(2f * scale, (float)p.JapaneseChars / maxVal * plotSize.Y) : 0f;

            var isHot = hovered && mouse.X >= x0 - barGap * 0.5f && mouse.X <= x1 + barGap * 0.5f && mouse.Y >= pos.Y && mouse.Y <= max.Y;
            if (isHot)
                hoveredPoint = p;

            var col = goalChars > 0 && p.JapaneseChars >= goalChars ? Good : Accent;
            if (!isHot)
                col.W = 0.8f;
            if (barH > 0)
                draw.AddRectFilled(new Vector2(x0, baseY - barH), new Vector2(x1, baseY), ImGui.ColorConvertFloat4ToU32(col));

            if (i % labelStep == 0 || i == series.Count - 1)
            {
                var lblSize = ImGui.CalcTextSize(p.ShortLabel);
                draw.AddText(new Vector2(x0 + (barW - lblSize.X) * 0.5f, baseY + 3 * scale), muted, p.ShortLabel);
            }
        }

        if (goalChars > 0)
        {
            var goalY = plotMin.Y + plotSize.Y * (1f - (float)goalChars / maxVal);
            var dash = 5 * scale;
            var gap = 4 * scale;
            for (var dx = plotMin.X; dx < plotMin.X + plotSize.X; dx += dash + gap)
                draw.AddLine(new Vector2(dx, goalY), new Vector2(Math.Min(plotMin.X + plotSize.X, dx + dash), goalY), muted, 1f);
            draw.AddText(new Vector2(plotMin.X, pos.Y), muted, $"Goal {goalChars:N0}");
        }

        var maxLbl = $"Peak {dataPeak:N0}";
        var maxLblSize = ImGui.CalcTextSize(maxLbl);
        draw.AddText(new Vector2(pos.X + width - maxLblSize.X, pos.Y), muted, maxLbl);

        if (hoveredPoint is { } hp)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(hp.DateKey);
            ImGui.TextUnformatted($"{hp.JapaneseChars:N0} characters" + (goalChars > 0 && hp.JapaneseChars >= goalChars ? " (goal met)" : string.Empty));
            ImGui.TextDisabled($"{hp.Lines:N0} lines · {hp.Lookups:N0} lookups · {hp.CardsMined:N0} cards");
            if (hp.ActiveSeconds > 0)
                ImGui.TextDisabled($"Active {FormatDuration(hp.ActiveSeconds)}");
            ImGui.EndTooltip();
        }
    }

    private static void DrawHeatmap30(IReadOnlyList<DayPoint> days, float width, int dailyGoalChars, float scale)
    {
        if (days.Count == 0)
            return;

        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var gap = 3 * scale;
        var cell = Math.Clamp((width - gap * (days.Count - 1)) / days.Count, 10 * scale, 24 * scale);
        var totalW = days.Count * cell + (days.Count - 1) * gap;

        ImGui.InvisibleButton("##heatmap-30", new Vector2(Math.Max(width, totalW), cell));
        var maxChars = Math.Max(1, days.Max(d => d.JapaneseChars));
        var mouse = ImGui.GetMousePos();
        var hovered = ImGui.IsItemHovered();
        var empty = ImGui.GetColorU32(ImGuiCol.FrameBg);
        DayPoint? hot = null;

        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var min = new Vector2(pos.X + i * (cell + gap), pos.Y);
            var rectMax = min + new Vector2(cell, cell);
            var intensity = d.JapaneseChars == 0 ? 0f : Math.Clamp((float)Math.Sqrt((double)d.JapaneseChars / maxChars), 0.25f, 1f);
            draw.AddRectFilled(min, rectMax, d.JapaneseChars == 0 ? empty : ImGui.ColorConvertFloat4ToU32(Accent with { W = intensity }));

            if (hovered && mouse.X >= min.X && mouse.X <= rectMax.X && mouse.Y >= min.Y && mouse.Y <= rectMax.Y)
            {
                hot = d;
                draw.AddRect(min, rectMax, ImGui.GetColorU32(ImGuiCol.Text));
            }
        }

        if (hot is { } hd)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(hd.DateKey);
            ImGui.TextDisabled($"{hd.JapaneseChars:N0} characters · {hd.Lines:N0} lines · {hd.CardsMined:N0} cards");
            if (dailyGoalChars > 0)
                ImGui.TextDisabled(hd.JapaneseChars >= dailyGoalChars ? "Daily goal met" : $"{dailyGoalChars - hd.JapaneseChars:N0} short of the daily goal");
            ImGui.EndTooltip();
        }
    }

    private static void DrawImmersionClock(StatsSnapshot snap, float width, float scale)
    {
        SectionTitle("Time of day");
        var peak = Math.Max(1, snap.HourlyChars.Max());
        var peakHour = Array.IndexOf(snap.HourlyChars, snap.HourlyChars.Max());
        if (snap.HourlyChars.Max() > 0)
            ImGui.TextDisabled($"Most reading at {peakHour:00}:00–{(peakHour + 1) % 24:00}:00");

        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var height = 80 * scale;
        var max = pos + new Vector2(width, height);

        ImGui.InvisibleButton("##clock-24", new Vector2(width, height));
        var padB = 18 * scale;
        var plotH = Math.Max(24, height - padB);
        var step = width / 24f;
        var barW = Math.Max(2f, step - 2f);
        var mouse = ImGui.GetMousePos();
        var hovered = ImGui.IsItemHovered();
        var muted = ImGui.ColorConvertFloat4ToU32(MutedText);
        var hotHour = -1;

        for (var h = 0; h < 24; h++)
        {
            var val = snap.HourlyChars[h];
            var x0 = pos.X + h * step;
            var y1 = pos.Y + plotH;
            if (hovered && mouse.X >= x0 && mouse.X <= x0 + step && mouse.Y >= pos.Y && mouse.Y <= max.Y)
                hotHour = h;

            if (val > 0)
            {
                var bh = Math.Max(2f, (float)val / peak * plotH);
                draw.AddRectFilled(new Vector2(x0, y1 - bh), new Vector2(x0 + barW, y1), ImGui.ColorConvertFloat4ToU32(Accent with { W = hotHour == h ? 1f : 0.8f }));
            }

            if (h % 6 == 0)
                draw.AddText(new Vector2(x0, y1 + 3 * scale), muted, $"{h:00}h");
        }
        draw.AddLine(new Vector2(pos.X, pos.Y + plotH), new Vector2(pos.X + width, pos.Y + plotH), ImGui.GetColorU32(ImGuiCol.Separator));

        if (hotHour >= 0)
            ImGui.SetTooltip($"{hotHour:00}:00–{(hotHour + 1) % 24:00}:00 · {snap.HourlyChars[hotHour]:N0} characters");
    }

    private void DrawHighlights(StatsSnapshot snap)
    {
        var scale = ImGuiHelpers.GlobalScale;
        SectionTitle("Highlights");

        using (var table = ImRaii.Table("##highlights", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, 105 * scale);
                ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);

                HighlightRow("Lines", $"{snap.TalkLines:N0} dialogue", $"{snap.BattleTalkLines:N0} battle talk · {snap.ChatLines:N0} chat", null);
                if (snap.MostActiveDay is { } best)
                    HighlightRow("Biggest day", best.DateKey, $"{best.JapaneseChars:N0} chars", null);
                if (snap.TopNpcs.Count > 0)
                    HighlightRow("Chattiest NPC", snap.TopNpcs[0].Speaker, $"{snap.TopNpcs[0].JapaneseChars:N0} chars", Lavender, lookup: true);
                if (snap.TopParsedWords.Count > 0)
                    HighlightRow("Most read word", snap.TopParsedWords[0].Word, $"{snap.TopParsedWords[0].Count}×", Accent, lookup: true);
                if (snap.LongestLine is { } longest)
                    HighlightRow("Longest line", string.IsNullOrEmpty(longest.Speaker) ? "Narrator" : longest.Speaker, $"{longest.JapaneseChars} chars", Lavender);
            }
        }

        if (snap.LongestLine is not { Text.Length: > 0 } line)
            return;

        // The longest line as a quote: a lavender rule on the left, text wrapped inside the column.
        ImGui.Spacing();
        var top = ImGui.GetCursorScreenPos();
        var indent = 12 * scale;
        using (ImRaii.PushIndent(indent))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextWrapped($"“{line.Text}”");
            ImGui.PopTextWrapPos();
        }
        var bottom = ImGui.GetItemRectMax().Y;
        ImGui.GetWindowDrawList().AddLine(new Vector2(top.X + 3 * scale, top.Y), new Vector2(top.X + 3 * scale, bottom),
            ImGui.ColorConvertFloat4ToU32(Lavender), 2 * scale);
    }

    private void HighlightRow(string label, string value, string detail, Vector4? color, bool lookup = false)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        using (ImRaii.PushColor(ImGuiCol.Text, color ?? Vector4.Zero, color is not null))
        {
            if (lookup)
            {
                if (ImGui.Selectable($"{value}##hl-{label}", false, ImGuiSelectableFlags.None, ImGui.CalcTextSize(value)))
                    popups.OpenQuery(value);
            }
            else
            {
                ImGui.TextUnformatted(value);
            }
        }
        if (lookup)
            Tooltip("Look up");
        ImGui.SameLine();
        ImGui.TextDisabled(detail);
    }

    // -------------------------------------------------------------- Goals

    private void DrawGoalsAndExpansionsTab(StatsSnapshot snap, float scale)
    {
        var changed = false;

        SectionTitle("Daily goals");
        ImGui.TextDisabled("Set a goal to 0 to turn it off.");

        using (var table = ImRaii.Table("##daily-goals", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("Goal", ImGuiTableColumnFlags.WidthFixed, 110 * scale);
                ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 200 * scale);
                ImGui.TableSetupColumn("Today", ImGuiTableColumnFlags.WidthStretch);

                changed |= DrawGoalRow("Characters", config.DailyCharGoal, ReadingTimeRule.CharGoalSteps,
                    val => config.DailyCharGoal = Math.Clamp(val, 0, 500_000),
                    snap.Goals?.TodayChars ?? 0, snap.Goals?.CharGoalDaysMet ?? 0, snap.Goals?.PeriodDayCount ?? 1, "chars", scale);
                changed |= DrawGoalRow("Reading time", config.DailyMinutesGoal, ReadingTimeRule.MinuteGoalSteps,
                    val => config.DailyMinutesGoal = Math.Clamp(val, 0, 1440),
                    (int)Math.Round((snap.Goals?.TodayActiveSeconds ?? 0) / 60.0), snap.Goals?.TimeGoalDaysMet ?? 0, snap.Goals?.PeriodDayCount ?? 1, "min", scale);
                changed |= DrawGoalRow("Anki cards", config.DailyCardsGoal, ReadingTimeRule.CardGoalSteps,
                    val => config.DailyCardsGoal = Math.Clamp(val, 0, 500),
                    snap.Goals?.TodayCardsMined ?? 0, snap.Goals?.CardGoalDaysMet ?? 0, snap.Goals?.PeriodDayCount ?? 1, "cards", scale);
            }
        }

        if (snap.Goals is { Settings.HasAnyDailyGoal: true } gp)
        {
            ImGui.TextDisabled($"Goal streak {gp.CurrentGoalStreak} days (best {gp.BestGoalStreak}) · all goals met on {gp.AllGoalsDaysMet} of {gp.PeriodDayCount} days");
        }

        if (ImGui.TreeNode("Time tracking options##goal-options"))
        {
            var useAdaptive = config.StatsUseRubiAdaptiveTime;
            if (ImGui.Checkbox("Adaptive reading time (Rubi rule)", ref useAdaptive))
            {
                config.StatsUseRubiAdaptiveTime = useAdaptive;
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Times each line from your own pace (75th percentile, 2.5× cap, 2 s–5 min), so idle time and skipped cutscenes don't count. Same rule as Rubi and LearnFromGames.");

            var nightOwlDay = config.StatsDayStartHour == 4;
            if (ImGui.Checkbox("Day starts at 04:00", ref nightOwlDay))
            {
                config.StatsDayStartHour = nightOwlDay ? 4 : 0;
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Reading after midnight counts toward the previous day's goals.");
            ImGui.TreePop();
        }

        SectionTitle("Milestone");

        var label = config.CustomGoalLabel ?? string.Empty;
        ImGui.SetNextItemWidth(220 * scale);
        if (ImGui.InputTextWithHint("##custom-goal-label", "Name (e.g. Clear Heavensward)", ref label, 64))
        {
            config.CustomGoalLabel = label;
            changed = true;
        }
        ImGui.SameLine(0, 8 * scale);
        var customTarget = (int)Math.Clamp(config.CustomCharGoal, 0, int.MaxValue);
        ImGui.SetNextItemWidth(150 * scale);
        if (ImGui.InputInt("chars##custom-goal-target", ref customTarget, 10_000, 50_000))
        {
            config.CustomCharGoal = Math.Max(0, customTarget);
            changed = true;
        }
        ImGui.SameLine(0, 8 * scale);
        foreach (var (presetLabel, presetVal) in new[] { ("Off", 0L), ("100k", 100_000L), ("250k", 250_000L), ("500k", 500_000L), ("1M", 1_000_000L) })
        {
            if (ImGui.SmallButton($"{presetLabel}##cg-{presetVal}"))
            {
                config.CustomCharGoal = presetVal;
                changed = true;
            }
            ImGui.SameLine(0, 4 * scale);
        }
        ImGui.NewLine();

        if (config.CustomCharGoal > 0)
        {
            var cur = snap.AllTimeJapaneseChars;
            var pct = Math.Clamp((float)cur / config.CustomCharGoal, 0f, 1f);
            var rem = Math.Max(0L, config.CustomCharGoal - cur);
            var title = string.IsNullOrWhiteSpace(config.CustomGoalLabel) ? "Milestone" : config.CustomGoalLabel.Trim();
            var eta = new List<string>();
            if (rem == 0)
            {
                eta.Add("complete");
            }
            else
            {
                eta.Add($"{rem:N0} left");
                if (snap.CharsPerHour >= 100)
                    eta.Add($"about {rem / snap.CharsPerHour:0.0} h at your pace");
                if (config.DailyCharGoal > 0)
                    eta.Add($"{Math.Ceiling((double)rem / config.DailyCharGoal):0} days at your daily goal");
            }
            ImGui.TextUnformatted($"{title}: {cur:N0} / {config.CustomCharGoal:N0}");
            ImGui.SameLine();
            ImGui.TextDisabled(string.Join(" · ", eta));
            Bar(pct, new Vector2(-1, 0), $"{pct * 100f:0.0}%", rem == 0 ? Good : Gold);
        }

        SectionTitle("MSQ progress");
        ImGui.TextDisabled("Estimated from the Japanese MSQ scripts. Side quests, raids and chat come on top.");
        ImGui.Spacing();

        // MSQ quest name -> automatic offset (from installed game data + QuestManager)
        msqResolver.EnsureLoaded();
        var msqIndex = msqResolver.Index;
        if (msqIndex is not null && (DateTime.UtcNow - lastMsqDetectUtc).TotalSeconds > 5.0)
        {
            (detectedMsq, detectedMsqAccepted) = msqResolver.DetectCurrentMsq();
            lastMsqDetectUtc = DateTime.UtcNow;
        }

        if (msqResolver.IsLoading)
            ImGui.TextDisabled("Indexing MSQ quests…");

        // A small form: detected quest, quest search, and which expansion to track. One font throughout.
        using (var form = ImRaii.Table("##msq-form", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            if (form.Success)
            {
                ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, 100 * scale);
                ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);

                if (!msqResolver.IsLoading && msqIndex is { Quests.Count: > 0 })
                {
                    if (detectedMsq is { } liveQ)
                    {
                        FormLabel(detectedMsqAccepted ? "Current MSQ" : "Next MSQ");
                        ImGui.TextColored(Gold, $"[{liveQ.ExpansionShortCode}]");
                        ImGui.SameLine();
                        ImGui.TextUnformatted(liveQ.JapaneseName);
                        if (!string.IsNullOrEmpty(liveQ.EnglishName) && liveQ.EnglishName != liveQ.JapaneseName)
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled(liveQ.EnglishName);
                        }
                        ImGui.SameLine(0, 10 * scale);
                        if (ImGui.SmallButton("Use this quest"))
                        {
                            ApplyMsqQuestOffset(liveQ, snap.AllTimeJapaneseChars);
                            changed = true;
                        }
                        Tooltip($"Counts the roughly {liveQ.CumulativeBeforeChars:N0} MSQ characters before this quest as already read.");
                    }

                    FormLabel("Find quest");
                    var setW = ImGui.CalcTextSize("Set").X + ImGui.GetStyle().FramePadding.X * 2;
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - setW - 6 * scale);
                    var submitted = ImGui.InputTextWithHint("##msq-quest-name", "Japanese or English quest name", ref msqSearchQuery, 128, ImGuiInputTextFlags.EnterReturnsTrue);
                    ImGui.SameLine(0, 6 * scale);
                    if (ImGui.Button("Set") || submitted)
                    {
                        var best = msqIndex.FindBest(msqSearchQuery);
                        if (best is not null)
                        {
                            ApplyMsqQuestOffset(best, snap.AllTimeJapaneseChars);
                            changed = true;
                        }
                        else
                        {
                            SetStatus($"No MSQ quest matched \"{msqSearchQuery.Trim()}\".", isError: true);
                        }
                    }

                    // Live suggestions while typing (once the query differs from the applied quest).
                    var trimmedQuery = msqSearchQuery.Trim();
                    if (trimmedQuery.Length >= 2 && !string.Equals(trimmedQuery, config.GoalMsqQuestName, StringComparison.Ordinal))
                    {
                        var matches = msqIndex.Search(trimmedQuery, maxResults: 6);
                        for (var i = 0; i < matches.Count; i++)
                        {
                            var q = matches[i];
                            using var id = ImRaii.PushId(i);
                            var rowLabel = string.IsNullOrEmpty(q.EnglishName) || q.EnglishName == q.JapaneseName
                                ? $"[{q.ExpansionShortCode}] {q.JapaneseName}"
                                : $"[{q.ExpansionShortCode}] {q.JapaneseName} / {q.EnglishName}";
                            if (ImGui.Selectable(rowLabel))
                            {
                                ApplyMsqQuestOffset(q, snap.AllTimeJapaneseChars);
                                changed = true;
                            }
                        }
                    }
                }

                var focusId = string.IsNullOrWhiteSpace(config.GoalExpansionId) ? ExpansionEstimates.AllMsqId : config.GoalExpansionId;
                var focusDisplay = focusId == ExpansionEstimates.AllMsqId
                    ? "Full MSQ"
                    : ExpansionEstimates.FindById(focusId) is { } fe
                        ? fe.Name
                        : "Full MSQ";
                FormLabel("Track");
                ImGui.SetNextItemWidth(Math.Min(260 * scale, ImGui.GetContentRegionAvail().X));
                using var combo = ImRaii.Combo("##exp-focus", focusDisplay);
                if (combo.Success)
                {
                    if (ImGui.Selectable($"Full MSQ ({ExpansionEstimates.TotalMsqChars:N0} chars)", focusId == ExpansionEstimates.AllMsqId))
                    {
                        config.GoalExpansionId = ExpansionEstimates.AllMsqId;
                        changed = true;
                    }
                    foreach (var exp in ExpansionEstimates.All)
                    {
                        if (ImGui.Selectable($"{exp.Name} ({exp.EstimatedMsqChars:N0} chars)", string.Equals(focusId, exp.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            config.GoalExpansionId = exp.Id;
                            changed = true;
                        }
                    }
                }
            }
        }

        if (ImGui.TreeNode("Set starting point manually##msq-manual"))
        {
            var offsetInt = (int)Math.Clamp(config.ExpansionStartingChars, 0, int.MaxValue);
            ImGui.SetNextItemWidth(160 * scale);
            if (ImGui.InputInt("MSQ characters already read##exp-offset", ref offsetInt, 25_000, 100_000))
            {
                config.ExpansionStartingChars = Math.Max(0, offsetInt);
                config.GoalMsqQuestName = string.Empty;
                changed = true;
            }

            ImGui.TextDisabled("Start of:");
            foreach (var exp in ExpansionEstimates.All)
            {
                ImGui.SameLine(0, 4 * scale);
                if (ImGui.SmallButton($"{exp.ShortCode}##offset-{exp.Id}"))
                {
                    config.ExpansionStartingChars = ExpansionEstimates.CumulativeBefore(exp.Id);
                    config.GoalExpansionId = exp.Id;
                    config.GoalMsqQuestName = string.Empty;
                    msqSearchQuery = string.Empty;
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(exp.Name);
            }
            ImGui.TreePop();
        }

        ImGui.Spacing();

        if (snap.ExpansionJourney is { } journey)
        {
            DrawExpansionJourneyStrip(journey, ImGui.GetContentRegionAvail().X, scale);
            ImGui.Spacing();

            var barMaxW = Math.Max(90 * scale, ImGui.GetContentRegionAvail().X * 0.20f);
            using var table = ImRaii.Table("##expansion-estimates-table", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
            if (table.Success)
            {
                ImGui.TableSetupColumn("Expansion", ImGuiTableColumnFlags.WidthStretch, 0.28f);
                ImGui.TableSetupColumn("MSQ chars", ImGuiTableColumnFlags.WidthFixed, 100 * scale);
                ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthFixed, barMaxW + 58 * scale);
                ImGui.TableSetupColumn("Remaining", ImGuiTableColumnFlags.WidthStretch, 0.22f);
                ImGui.TableSetupColumn("With side quests", ImGuiTableColumnFlags.WidthFixed, 108 * scale);
                ImGui.TableHeadersRow();

                for (var i = 0; i < journey.Expansions.Count; i++)
                {
                    var item = journey.Expansions[i];
                    var exp = item.Expansion;
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(exp.Name);
                    using (fonts.Popup.Push())
                        ImGui.TextDisabled(exp.JapaneseName);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{exp.EstimatedMsqChars:N0}");
                    ImGui.TextDisabled($"{exp.EstimatedMsqLines:N0} lines");

                    ImGui.TableNextColumn();
                    var pctLbl = item.Completed ? "100%" : $"{item.PercentComplete:0.0}%";
                    DrawInlineBar((float)(item.PercentComplete / 100.0), barMaxW + 50 * scale, pctLbl, item.Completed ? Good : null);

                    ImGui.TableNextColumn();
                    var rem = Math.Max(0L, exp.EstimatedMsqChars - item.EarnedChars);
                    if (rem == 0)
                    {
                        ImGui.TextColored(Good, "Cleared");
                    }
                    else
                    {
                        var etaText = $"{rem:N0} chars";
                        if (config.DailyCharGoal > 0)
                            etaText += $" ({Math.Ceiling((double)rem / config.DailyCharGoal):0} days)";
                        else if (snap.CharsPerHour >= 100)
                            etaText += $" ({rem / snap.CharsPerHour:0.0} h)";
                        ImGui.TextDisabled(etaText);
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled($"{exp.EstimatedAllQuestsChars / 1_000_000.0:0.00}M");
                }
            }
        }

        if (changed)
            config.Save();
    }

    private static bool DrawGoalRow(string title, int currentGoal, int[] steps, Action<int> setGoal,
        int todayValue, int daysMet, int periodDays, string unit, float scale)
    {
        var changed = false;
        using var id = ImRaii.PushId(title);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(title);

        ImGui.TableNextColumn();
        if (ImGui.Button("-", new Vector2(ImGui.GetFrameHeight(), 0)))
        {
            setGoal(ReadingTimeRule.StepValue(steps, currentGoal, up: false));
            changed = true;
        }
        ImGui.SameLine(0, 4 * scale);
        var val = currentGoal;
        ImGui.SetNextItemWidth(90 * scale);
        if (ImGui.InputInt("##goal", ref val, 0, 0))
        {
            setGoal(Math.Max(0, val));
            changed = true;
        }
        ImGui.SameLine(0, 4 * scale);
        if (ImGui.Button("+", new Vector2(ImGui.GetFrameHeight(), 0)))
        {
            setGoal(ReadingTimeRule.StepValue(steps, currentGoal, up: true));
            changed = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled(unit);

        ImGui.TableNextColumn();
        if (currentGoal <= 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled($"Off · {todayValue:N0} {unit} today");
        }
        else
        {
            var met = todayValue >= currentGoal;
            Bar((float)todayValue / currentGoal, new Vector2(-1, 0), $"{todayValue:N0} / {currentGoal:N0}", met ? Good : Accent);
            Tooltip($"Met on {daysMet} of {periodDays} days in this period.");
        }

        return changed;
    }

    // -------------------------------------------------------------- Data (export, import, sync)

    private void DrawEcosystemTab(float scale)
    {
        var changed = false;
        var sqliteOk = NativeSqlite.IsAvailable;

        if (dataActionBusy)
            ImGui.TextDisabled(dataActionLabel);

        if (DataSection("data-export", "Export", defaultOpen: true))
        {
            ImGui.TextDisabled("Files are saved to the exports folder and the path is copied.");
            using (ImRaii.Disabled(dataActionBusy))
            {
                if (ImGui.Button("Lines CSV"))
                    RunExport("lines CSV", dir => tracker.ExportExStaticLinesCsv(dir));
                Tooltip("exSTATic, Rubi and GSM lines format.");
                ImGui.SameLine(0, 6 * scale);
                if (ImGui.Button("Daily stats CSV"))
                    RunExport("daily stats CSV", dir => tracker.ExportExStaticStatsCsv(dir));
                Tooltip("exSTATic daily stats format.");
                ImGui.SameLine(0, 6 * scale);
                if (ImGui.Button("Kechimochi CSV"))
                {
                    var activityType = config.KechimochiActivityType;
                    RunExport("Kechimochi CSV", dir => tracker.ExportKechimochiCsv(dir, activityType));
                }
                Tooltip($"Kechimochi activity log (logged as \"{config.KechimochiActivityType}\"; change it under Options).");
                ImGui.SameLine(0, 6 * scale);
                using (ImRaii.Disabled(!sqliteOk))
                {
                    if (ImGui.Button("SQLite"))
                        RunExport("SQLite database", dir => tracker.ExportTidepoolSqlite(dir));
                }
                Tooltip(sqliteOk ? "Tidepool, Telly and GSM schema." : "SQLite isn't available on this system.");
                ImGui.SameLine(0, 6 * scale);
                if (ImGui.Button("Full JSON"))
                    RunExport("JSON", dir => tracker.ExportToFile(dir));
                Tooltip("Everything Hachidori records.");
            }
            ImGui.SameLine(0, 6 * scale);
            if (ImGui.SmallButton("Open folder"))
                OpenFolderInShell(GetExportDirectory());
        }

        if (DataSection("data-import", "Import", defaultOpen: false))
        {
            ImGui.TextDisabled("CSV or SQLite from exSTATic, Rubi, LearnFromGames, Kechimochi, GSM, SubMiner or Tidepool. Duplicates are skipped.");

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var detectedPaths = new (string Label, string Path)[]
            {
                ("GSM", Path.Combine(appData, "GameSentenceMiner", "gsm.db")),
                ("SubMiner", Path.Combine(appData, "SubMiner", "immersion.sqlite")),
                ("Telly", Path.Combine(appData, "Telly", "telly-log.sqlite")),
            };
            var anyFound = false;
            foreach (var (lbl, p) in detectedPaths)
            {
                if (!File.Exists(p))
                    continue;
                if (!anyFound)
                {
                    ImGui.TextDisabled("Found on this PC:");
                    anyFound = true;
                }
                ImGui.SameLine(0, 6 * scale);
                if (ImGui.SmallButton($"{lbl}##detected-{lbl}"))
                    importPath = p;
                Tooltip(p);
            }

            ImGui.SetNextItemWidth(Math.Max(260 * scale, ImGui.GetContentRegionAvail().X - 90 * scale));
            ImGui.InputTextWithHint("##eco-import-path", "Path to a .csv, .sqlite or .db file", ref importPath, 512);
            ImGui.SameLine(0, 6 * scale);
            if (ImGui.Button("Paste"))
            {
                var clip = ImGui.GetClipboardText()?.Trim().Trim('"') ?? string.Empty;
                if (clip.Length > 0)
                    importPath = clip;
            }

            ImGui.SetNextItemWidth(260 * scale);
            ImGui.InputTextWithHint("##eco-import-filter", "Only titles containing… (optional)", ref importTitleFilter, 128);
            ImGui.SameLine(0, 8 * scale);
            using (ImRaii.Disabled(dataActionBusy))
            {
                if (ImGui.Button("Import"))
                {
                    var cleanPath = importPath.Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(cleanPath))
                    {
                        SetStatus("Enter or paste a file path first.", isError: true);
                    }
                    else
                    {
                        var titleFilter = importTitleFilter;
                        RunDataAction("Importing…", "Import failed", () =>
                        {
                            var res = tracker.ImportFromFile(cleanPath, titleFilter);
                            return () => SetStatus($"Imported {res.DetectedFormat}: +{res.TotalJapaneseChars:N0} chars ({res.ImportedLines:N0} lines across {res.ImportedDays} days, {res.SkippedDuplicates} duplicates skipped).");
                        });
                    }
                }
            }
        }

        if (DataSection("data-gsm", "Send to GameSentenceMiner", defaultOpen: false))
        {
            ImGui.TextDisabled("Uploads your lines to GSM. GSM skips lines it already has.");
            var gsmUrl = config.GsmApiUrl ?? "http://127.0.0.1:7275";
            ImGui.SetNextItemWidth(260 * scale);
            if (ImGui.InputText("GSM address##gsm-url", ref gsmUrl, 128))
            {
                config.GsmApiUrl = gsmUrl;
                changed = true;
            }
            ImGui.SameLine(0, 8 * scale);
            using (ImRaii.Disabled(gsmSyncBusy))
            {
                if (ImGui.Button(gsmSyncBusy ? "Sending…" : "Send now"))
                {
                    gsmSyncBusy = true;
                    var targetUrl = config.GsmApiUrl ?? "http://127.0.0.1:7275";
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var (ok, msg) = await tracker.PushToGsmAsync(targetUrl).ConfigureAwait(false);
                            uiActions.Enqueue(() => SetStatus(msg, isError: !ok));
                        }
                        catch (Exception ex)
                        {
                            uiActions.Enqueue(() => SetStatus($"GSM sync failed: {ex.Message}", isError: true));
                        }
                        finally
                        {
                            uiActions.Enqueue(() => gsmSyncBusy = false);
                        }
                    });
                }
            }
        }

        if (DataSection("data-options", "Options", defaultOpen: false))
        {
            var writeCsv = config.StatsWriteHistoryCsv;
            if (ImGui.Checkbox("Keep a live history.csv (Rubi / exSTATic / GSM)", ref writeCsv))
            {
                config.StatsWriteHistoryCsv = writeCsv;
                changed = true;
            }
            if (tracker.HistoryCsvPath is { } csvPath)
            {
                ImGui.SameLine(0, 8 * scale);
                if (ImGui.SmallButton("Copy path##copy-csv"))
                {
                    ImGui.SetClipboardText(csvPath);
                    SetStatus($"Copied: {csvPath}");
                }
                Tooltip(csvPath);
            }

            using (ImRaii.Disabled(!sqliteOk))
            {
                var writeSql = config.StatsWriteTidepoolSqlite;
                if (ImGui.Checkbox("Keep a live SQLite log (Tidepool / Telly / GSM)", ref writeSql))
                {
                    config.StatsWriteTidepoolSqlite = writeSql;
                    changed = true;
                }
            }
            if (!sqliteOk)
            {
                Tooltip("SQLite isn't available on this system. CSV still works.");
            }
            else if (tracker.TidepoolSqlitePath is { } sqlPath)
            {
                ImGui.SameLine(0, 8 * scale);
                if (ImGui.SmallButton("Copy path##copy-sql"))
                {
                    ImGui.SetClipboardText(sqlPath);
                    SetStatus($"Copied: {sqlPath}");
                }
                Tooltip(sqlPath);
            }

            var actType = config.KechimochiActivityType is "Reading" ? "Reading" : "Playing";
            ImGui.TextUnformatted("Kechimochi activity type:");
            ImGui.SameLine(0, 8 * scale);
            if (ImGui.RadioButton("Playing", actType == "Playing"))
            {
                config.KechimochiActivityType = "Playing";
                changed = true;
            }
            ImGui.SameLine(0, 8 * scale);
            if (ImGui.RadioButton("Reading", actType == "Reading"))
            {
                config.KechimochiActivityType = "Reading";
                changed = true;
            }

            if (tracker.StorageDirectory is { } statsDir && ImGui.SmallButton("Open stats folder"))
                OpenFolderInShell(statsDir);

            ImGui.Spacing();
            using (ImRaii.Disabled(dataActionBusy))
            using (ImRaii.PushColor(ImGuiCol.Button, confirmClear ? new Vector4(0.75f, 0.22f, 0.22f, 0.95f) : new Vector4(0.24f, 0.18f, 0.20f, 0.9f)))
            {
                if (ImGui.SmallButton(confirmClear ? "Click again to delete all stats" : "Clear all stats"))
                {
                    if (confirmClear)
                    {
                        confirmClear = false;
                        RunDataAction("Clearing stats…", "Clear incomplete", () =>
                        {
                            tracker.ClearAll();
                            return () => SetStatus("All saved stats cleared, including history.csv and the SQLite log.");
                        });
                    }
                    else
                    {
                        confirmClear = true;
                    }
                }
                else if (confirmClear && !ImGui.IsItemHovered())
                {
                    confirmClear = false;
                }
            }
            if (!confirmClear)
                Tooltip("Deletes every recorded stat, the live history.csv and the live SQLite log. Export first if you want a backup.");
        }

        if (changed)
            config.Save();
    }

    private static bool DataSection(string id, string title, bool defaultOpen)
    {
        ImGui.Spacing();
        return ImGui.CollapsingHeader($"{title}###{id}", defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }

    private void ApplyMsqQuestOffset(MsqQuestEntry quest, long alreadyTrackedChars)
    {
        var offset = MsqQuestIndex.ComputeStartingOffset(quest, alreadyTrackedChars);
        var displayName = string.IsNullOrEmpty(quest.EnglishName) || quest.EnglishName == quest.JapaneseName
            ? quest.JapaneseName
            : $"{quest.JapaneseName} / {quest.EnglishName}";
        config.ExpansionStartingChars = offset;
        config.GoalExpansionId = quest.ExpansionId;
        config.GoalMsqQuestName = displayName;
        msqSearchQuery = displayName;
        SetStatus($"MSQ offset set from [{quest.ExpansionShortCode}] {displayName}: about {quest.CumulativeBeforeChars:N0} MSQ chars before it (prior offset {offset:N0}).");
    }

    private void RunExport(string label, Func<string, string> export)
    {
        string dir;
        try
        {
            dir = GetExportDirectory();
        }
        catch (Exception ex)
        {
            SetStatus($"{label} export failed: {ex.Message}", isError: true);
            return;
        }

        RunDataAction($"Exporting {label}…", $"{label} export failed", () =>
        {
            var path = export(dir);
            return () =>
            {
                ImGui.SetClipboardText(path);
                SetStatus($"Exported {label} (path copied): {path}");
            };
        });
    }

    /// <summary>
    /// Runs a blocking Data-tab action on a worker, one at a time. <paramref name="work"/> returns
    /// the UI update to apply on the draw thread once it finishes.
    /// </summary>
    private void RunDataAction(string busyLabel, string failurePrefix, Func<Action> work)
    {
        if (dataActionBusy)
            return;
        dataActionBusy = true;
        dataActionLabel = busyLabel;
        _ = Task.Run(() =>
        {
            Action onDone;
            try
            {
                onDone = work();
            }
            catch (Exception ex)
            {
                onDone = () => SetStatus($"{failurePrefix}: {ex.Message}", isError: true);
            }

            uiActions.Enqueue(() =>
            {
                dataActionBusy = false;
                onDone();
            });
        });
    }

    private static string GetExportDirectory()
    {
        var dir = Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "exports");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void OpenFolderInShell(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open folder: {ex.Message}", isError: true);
        }
    }

    // -------------------------------------------------------------- NPCs

    private void DrawNpcTab(StatsSnapshot snap, float scale)
    {
        SectionTitle("Chattiest NPCs");
        if (snap.TopNpcs.Count == 0)
        {
            ImGui.TextDisabled("No NPC dialogue in this period yet.");
        }
        else
        {
            var maxChars = Math.Max(1, snap.TopNpcs[0].JapaneseChars);
            var barMaxW = Math.Max(80 * scale, ImGui.GetContentRegionAvail().X * 0.28f);

            using var table = ImRaii.Table("##npc-leaderboard", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
            if (table.Success)
            {
                ImGui.TableSetupColumn("NPC", ImGuiTableColumnFlags.WidthStretch, 0.28f);
                ImGui.TableSetupColumn("Characters", ImGuiTableColumnFlags.WidthFixed, barMaxW + 85 * scale);
                ImGui.TableSetupColumn("Lines", ImGuiTableColumnFlags.WidthFixed, 56 * scale);
                ImGui.TableSetupColumn("Latest line", ImGuiTableColumnFlags.WidthStretch, 0.42f);
                ImGui.TableHeadersRow();

                for (var i = 0; i < snap.TopNpcs.Count; i++)
                {
                    var npc = snap.TopNpcs[i];
                    using var rowId = ImRaii.PushId(i);
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    using (fonts.Popup.Push())
                    {
                        if (ImGui.Selectable($"{i + 1}. {npc.Speaker}", false, ImGuiSelectableFlags.None))
                            popups.OpenQuery(npc.Speaker);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Look up {npc.Speaker} · {npc.SharePercent:0.1}% of NPC dialogue");

                    ImGui.TableNextColumn();
                    DrawInlineBar((float)npc.JapaneseChars / maxChars, barMaxW + 70 * scale, $"{npc.JapaneseChars:N0}", Lavender);

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled($"{npc.Lines:N0}");

                    ImGui.TableNextColumn();
                    using (fonts.Popup.Push())
                    {
                        var quote = npc.LatestQuote.Length > 48 ? npc.LatestQuote[..47] + "…" : npc.LatestQuote;
                        ImGui.TextDisabled(quote);
                    }
                }
            }
        }

        SectionTitle("Recent dialogue");
        if (snap.RecentDialogue.Count == 0)
        {
            ImGui.TextDisabled("Captured lines will appear here.");
            return;
        }

        ImGui.SetNextItemWidth(260 * scale);
        ImGui.InputTextWithHint("##npc-feed-filter", "Filter by NPC or text…", ref dialogueFilter, 128);

        var filter = dialogueFilter.Trim();
        var shown = 0;
        var contentW = ImGui.GetContentRegionAvail().X - 16 * scale;
        for (var i = 0; i < snap.RecentDialogue.Count && shown < 40; i++)
        {
            var entry = snap.RecentDialogue[i];
            if (filter.Length > 0
                && !entry.Speaker.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !entry.Text.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            shown++;

            using var id = ImRaii.PushId(i);
            var speakerName = string.IsNullOrEmpty(entry.Speaker) ? entry.Source : entry.Speaker;
            using (fonts.Popup.Push())
            {
                ImGui.TextColored(Lavender, speakerName);
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"· {entry.JapaneseChars} chars · {RelativeTime(entry.TimeUtc)}");

            if (i < dialogueBlocks.Count)
            {
                using (fonts.Popup.Push())
                {
                    var block = dialogueBlocks[i];
                    var hit = RichText.Draw(block, contentW, popups.HighlightFor(block, 0));
                    popups.Report(hit, 0, (_, idx) => new ScanContext(entry.Text, idx,
                        string.IsNullOrEmpty(entry.Speaker) ? config.DocumentTitle : $"{config.DocumentTitle} — {entry.Speaker}"));
                }
            }

            ImGui.Separator();
        }
    }

    // -------------------------------------------------------------- words & kanji

    private void DrawWordsTab(StatsSnapshot snap, float scale)
    {
        if (ToggleButton($"Most read ({snap.TopParsedWords.Count})", wordMode == 0,
                "Particles, pronouns, auxiliaries and single kana are left out."))
            wordMode = 0;
        ImGui.SameLine(0, 4 * scale);
        if (ToggleButton($"Most looked up ({snap.TopLookedUpWords.Count})", wordMode == 1,
                "The words you hover most often: good candidates to mine."))
            wordMode = 1;

        ImGui.SameLine(0, 12 * scale);
        ImGui.SetNextItemWidth(200 * scale);
        ImGui.InputTextWithHint("##word-filter", "Filter…", ref wordFilter, 64);

        var list = wordMode == 0 ? snap.TopParsedWords : snap.TopLookedUpWords;

        if (list.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(wordMode == 0
                ? "No words read in this period yet."
                : "No lookups in this period yet.");
        }
        else
        {
            var filter = wordFilter.Trim();
            var filtered = filter.Length == 0
                ? list
                : list.Where(w => w.Word.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                  || w.Reading.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                  || w.ShortMeaning.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            var maxCount = Math.Max(1, filtered.FirstOrDefault()?.Count ?? 1);
            var barW = Math.Max(70 * scale, ImGui.GetContentRegionAvail().X * 0.22f);

            using var table = ImRaii.Table("##words-table", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
            if (table.Success)
            {
                ImGui.TableSetupColumn("Word", ImGuiTableColumnFlags.WidthStretch, 0.24f);
                ImGui.TableSetupColumn("Reading", ImGuiTableColumnFlags.WidthStretch, 0.20f);
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, barW + 58 * scale);
                ImGui.TableSetupColumn("Meaning", ImGuiTableColumnFlags.WidthStretch, 0.36f);
                ImGui.TableHeadersRow();

                for (var i = 0; i < filtered.Count; i++)
                {
                    var item = filtered[i];
                    using var id = ImRaii.PushId(i);
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    using (fonts.Popup.Push())
                    {
                        if (ImGui.Selectable($"{i + 1}. {item.Word}"))
                            popups.OpenQuery(item.Word);
                    }

                    ImGui.TableNextColumn();
                    using (fonts.Popup.Push())
                        ImGui.TextDisabled(item.Reading);

                    ImGui.TableNextColumn();
                    DrawInlineBar((float)item.Count / maxCount, barW + 50 * scale, $"{item.Count}×");

                    ImGui.TableNextColumn();
                    using (fonts.Popup.Push())
                        ImGui.TextDisabled(item.ShortMeaning);
                }
            }
        }

        SectionTitle($"Kanji · {snap.UniqueKanjiCount:N0} unique, {snap.KanjiChars:N0} total");
        if (snap.SampleKanji.Count == 0)
        {
            ImGui.TextDisabled("Kanji you read will appear here.");
        }
        else
        {
            var tileSize = 34 * scale;
            var availX = ImGui.GetContentRegionAvail().X;
            var perRow = Math.Max(4, (int)(availX / (tileSize + 4 * scale)));
            using (fonts.Popup.Push())
            {
                for (var i = 0; i < snap.SampleKanji.Count; i++)
                {
                    if (i % perRow != 0)
                        ImGui.SameLine(0, 4 * scale);
                    using var kId = ImRaii.PushId(i);
                    if (ImGui.Button(snap.SampleKanji[i], new Vector2(tileSize, tileSize)))
                        popups.OpenQuery(snap.SampleKanji[i], kanji: true);
                }
            }
        }
    }

    // -------------------------------------------------------------- Anki cards

    private void DrawAnkiTab(StatsSnapshot snap, float scale)
    {
        if (ToggleButton($"Mined in game ({snap.RecentMinedCards.Count})", ankiViewMode == 0, "Cards you mined with Hachidori."))
            ankiViewMode = 0;
        ImGui.SameLine(0, 4 * scale);
        if (ToggleButton($"All recent in Anki ({ankiFeed.Notes.Count})", ankiViewMode == 1, "Notes added to Anki in the last 30 days, from AnkiConnect."))
        {
            ankiViewMode = 1;
            ankiFeed.RefreshIfStale(TimeSpan.FromSeconds(30));
        }

        if (ankiViewMode == 1)
        {
            ImGui.SameLine(0, 8 * scale);
            using (ImRaii.Disabled(ankiFeed.Loading))
            {
                if (ImGui.SmallButton("Refresh"))
                    ankiFeed.Refresh();
            }
        }

        if (ankiFeed.LastRefreshedUtc is not null)
        {
            ImGui.SameLine(0, 12 * scale);
            ImGui.TextDisabled($"Added in Anki: {ankiFeed.AddedLast7Days} this week · {ankiFeed.AddedLast30Days} this month");
        }

        ImGui.Spacing();
        if (ankiViewMode == 0)
            DrawMinedInGameCards(snap, scale);
        else
            DrawAnkiConnectCards(scale);
    }

    private void DrawMinedInGameCards(StatsSnapshot snap, float scale)
    {
        if (snap.RecentMinedCards.Count == 0)
        {
            ImGui.TextDisabled("No cards mined yet. Click + on a popup entry to mine one.");
            ImGui.Spacing();
            if (ImGui.Button("Show recent cards from Anki"))
            {
                ankiViewMode = 1;
                ankiFeed.Refresh();
            }
            return;
        }

        var contentW = ImGui.GetContentRegionAvail().X - 16 * scale;
        for (var i = 0; i < snap.RecentMinedCards.Count; i++)
        {
            var card = snap.RecentMinedCards[i];
            using var id = ImRaii.PushId(i);

            using (fonts.Popup.Push())
            {
                using (ImRaii.PushColor(ImGuiCol.Text, Accent))
                {
                    if (ImGui.Selectable($"{card.Expression}##expr-{i}", false, ImGuiSelectableFlags.None, ImGui.CalcTextSize(card.Expression)))
                        popups.OpenQuery(card.Expression);
                }
                Tooltip("Look up");
                if (card.Reading.Length > 0 && card.Reading != card.Expression)
                {
                    ImGui.SameLine(0, 6 * scale);
                    ImGui.TextDisabled($"【{card.Reading}】");
                }
            }

            if (card.Pitch.Length > 0)
            {
                ImGui.SameLine(0, 6 * scale);
                ImGui.TextDisabled(card.Pitch);
            }
            if (card.Frequency.Length > 0)
            {
                ImGui.SameLine(0, 6 * scale);
                ImGui.TextDisabled($"#{card.Frequency}");
            }

            var details = new List<string>();
            if (card.HasScreenshot) details.Add("image");
            if (card.HasAudio) details.Add("audio");
            if (card.HasAnimation) details.Add("animation");
            if (card.HasTranslation) details.Add("translation");
            details.Add(RelativeTime(card.MinedAtUtc));
            ImGui.SameLine(0, 10 * scale);
            ImGui.TextDisabled(string.Join(" · ", details));

            if (card.NoteId > 0)
            {
                ImGui.SameLine(0, 8 * scale);
                if (ImGui.SmallButton("Open in Anki"))
                    ankiFeed.BrowseNote(card.NoteId);
            }

            if (card.ShortMeaning.Length > 0)
            {
                using (fonts.Popup.Push())
                    ImGui.TextColored(MutedText, card.ShortMeaning);
            }

            if (card.Sentence.Length > 0 && i < minedCardBlocks.Count)
            {
                if (card.Speaker.Length > 0)
                {
                    using (fonts.Popup.Push())
                        ImGui.TextDisabled($"{card.Speaker}:");
                    ImGui.SameLine(0, 6 * scale);
                }

                using (fonts.Popup.Push())
                {
                    var block = minedCardBlocks[i];
                    var hit = RichText.Draw(block, contentW - 24 * scale, popups.HighlightFor(block, 0));
                    popups.Report(hit, 0, (_, idx) => new ScanContext(card.Sentence, idx, config.DocumentTitle));
                }
            }

            ImGui.Separator();
        }
    }

    private void DrawAnkiConnectCards(float scale)
    {
        if (ankiFeed.Loading)
        {
            ImGui.TextDisabled("Loading latest cards from AnkiConnect…");
            return;
        }

        if (ankiFeed.Error is { } err)
        {
            ImGui.TextColored(Bad, err);
            if (ImGui.Button("Retry AnkiConnect"))
                ankiFeed.Refresh();
            return;
        }

        if (ankiFeed.Notes.Count == 0)
        {
            ImGui.TextDisabled("No notes added in the last 30 days.");
            return;
        }

        using var table = ImRaii.Table("##anki-live-table", 4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Expression", ImGuiTableColumnFlags.WidthStretch, 0.24f);
        ImGui.TableSetupColumn("Sentence / Context", ImGuiTableColumnFlags.WidthStretch, 0.44f);
        ImGui.TableSetupColumn("Added", ImGuiTableColumnFlags.WidthFixed, 105 * scale);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 84 * scale);
        ImGui.TableHeadersRow();

        for (var i = 0; i < ankiFeed.Notes.Count; i++)
        {
            var note = ankiFeed.Notes[i];
            using var id = ImRaii.PushId(i);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            using (fonts.Popup.Push())
            {
                if (ImGui.Selectable(note.Expression))
                    popups.OpenQuery(note.Expression);
                if (note.Reading.Length > 0 && note.Reading != note.Expression)
                {
                    ImGui.SameLine(0, 4 * scale);
                    ImGui.TextDisabled($"({note.Reading})");
                }
            }

            ImGui.TableNextColumn();
            using (fonts.Popup.Push())
                ImGui.TextDisabled(note.Sentence);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(RelativeTime(note.CreatedAtUtc));
            if (note.HasAudio || note.HasImage)
                ImGui.TextDisabled(note.HasAudio && note.HasImage ? "image, audio" : note.HasImage ? "image" : "audio");

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Browse"))
                ankiFeed.BrowseNote(note.NoteId);
        }
    }

    // -------------------------------------------------------------- achievements

    private static void DrawAchievementsTab(StatsSnapshot snap, float scale)
    {
        var unlocked = snap.Achievements.Count(a => a.Unlocked);
        SectionTitle($"Achievements · {unlocked} of {snap.Achievements.Count}");

        var cols = ImGui.GetContentRegionAvail().X >= 660 * scale ? 3 : 2;
        using var table = ImRaii.Table("##achievements-grid", cols, ImGuiTableFlags.SizingStretchSame);
        if (!table.Success)
            return;

        var draw = ImGui.GetWindowDrawList();
        var cardBg = ImGui.GetColorU32(ImGuiCol.FrameBg);
        var trackBg = ImGui.GetColorU32(ImGuiCol.WindowBg);
        var pad = 10 * scale;
        var rounding = 4 * scale;

        foreach (var ach in snap.Achievements)
        {
            ImGui.TableNextColumn();
            var cardW = ImGui.GetContentRegionAvail().X;
            var cardH = 66 * scale;
            var pos = ImGui.GetCursorScreenPos();
            var color = ach.Unlocked ? Gold : Accent;

            draw.AddRectFilled(pos, pos + new Vector2(cardW, cardH), cardBg, rounding);
            if (ach.Unlocked)
                draw.AddRect(pos, pos + new Vector2(cardW, cardH), ImGui.ColorConvertFloat4ToU32(Gold with { W = 0.6f }), rounding);

            ImGui.SetCursorScreenPos(pos + new Vector2(pad, 8 * scale));
            Icon(ach.Unlocked ? FontAwesomeIcon.Trophy : FontAwesomeIcon.Lock, ach.Unlocked ? Gold : MutedText);
            ImGui.SameLine(0, 6 * scale);
            if (ach.Unlocked)
                ImGui.TextColored(Gold, ach.Title);
            else
                ImGui.TextUnformatted(ach.Title);

            var count = ach.Unlocked ? "Done" : $"{ach.Current:N0} / {ach.Target:N0}";
            var countW = ImGui.CalcTextSize(count).X;
            ImGui.SetCursorScreenPos(new Vector2(pos.X + cardW - pad - countW, pos.Y + 8 * scale));
            ImGui.TextColored(ach.Unlocked ? Gold : MutedText, count);

            ImGui.SetCursorScreenPos(pos + new Vector2(pad, 28 * scale));
            ImGui.TextDisabled(TruncateToWidth(ach.Description, cardW - pad * 2));

            var barPos = pos + new Vector2(pad, cardH - 13 * scale);
            var barW = cardW - pad * 2;
            var barH = 4 * scale;
            var pct = Math.Clamp((float)ach.Current / Math.Max(1, ach.Target), 0f, 1f);
            draw.AddRectFilled(barPos, barPos + new Vector2(barW, barH), trackBg, barH);
            if (pct > 0.005f)
                draw.AddRectFilled(barPos, barPos + new Vector2(Math.Max(barH, barW * pct), barH), ImGui.ColorConvertFloat4ToU32(color), barH);

            ImGui.SetCursorScreenPos(pos);
            ImGui.Dummy(new Vector2(cardW, cardH + 6 * scale));
        }
    }

    // -------------------------------------------------------------- helpers

    private void SetStatus(string message, bool isError = false)
    {
        statusBanner = message;
        statusIsError = isError;
        statusBannerUntil = DateTime.UtcNow.AddSeconds(8);
    }

    /// <summary>Section heading: the title and a rule, with room above so sections read as separate blocks.</summary>
    private static void SectionTitle(string title)
    {
        ImGui.Dummy(new Vector2(0, 8 * ImGuiHelpers.GlobalScale));
        ImGui.TextUnformatted(title);
        ImGui.SameLine();
        var pos = ImGui.GetCursorScreenPos();
        var y = pos.Y + ImGui.GetTextLineHeight() * 0.5f;
        var right = pos.X + ImGui.GetContentRegionAvail().X;
        if (right > pos.X)
            ImGui.GetWindowDrawList().AddLine(new Vector2(pos.X, y), new Vector2(right, y), ImGui.GetColorU32(ImGuiCol.Separator));
        ImGui.NewLine();
    }

    /// <summary>Starts a label/value row in a two-column form table.</summary>
    private static void FormLabel(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
    }

    private static void Icon(FontAwesomeIcon icon, Vector4 color)
    {
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
            ImGui.TextColored(color, icon.ToIconString());
    }

    /// <summary>Native progress bar in the given colour. An empty overlay shows no text.</summary>
    private static void Bar(float ratio, Vector2 size, string overlay, Vector4 color)
    {
        using var col = ImRaii.PushColor(ImGuiCol.PlotHistogram, color);
        ImGui.ProgressBar(Math.Clamp(ratio, 0f, 1f), size, overlay.Length == 0 ? " " : overlay);
    }

    private static void DrawInlineBar(float ratio, float maxWidth, string valueLabel, Vector4? color = null) =>
        Bar(ratio, new Vector2(maxWidth, 0), valueLabel, color ?? Accent);

    /// <summary>One half of a two-way view switch; the active side is highlighted.</summary>
    private static bool ToggleButton(string label, bool active, string tooltip)
    {
        using var bg = ImRaii.PushColor(ImGuiCol.Button, Accent with { W = 0.45f }, active);
        // The count in the label changes; keep the ID stable on the text before it.
        var stableId = label.Split(" (")[0];
        var clicked = ImGui.Button($"{label}###toggle-{stableId}");
        Tooltip(tooltip);
        return clicked;
    }

    private static string TruncateToWidth(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth || text.Length <= 4)
            return text;
        var len = text.Length - 1;
        while (len > 3 && ImGui.CalcTextSize(text[..len] + "…").X > maxWidth)
            len--;
        return text[..len] + "…";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60)
            return $"{(int)seconds}s";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m" : $"{ts.Minutes}m";
    }

    private static string RelativeTime(DateTime utc)
    {
        if (utc == DateTime.MinValue)
            return string.Empty;
        var age = DateTime.UtcNow - utc;
        if (age.TotalSeconds < 60) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        if (age.TotalDays < 7) return $"{(int)age.TotalDays}d ago";
        return utc.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
    }
}
