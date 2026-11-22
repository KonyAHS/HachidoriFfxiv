using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
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
/// Fun immersion statistics dashboard: reading volume, speed, streaks, FFXIV rank,
/// chattiest NPCs, live dialogue feed, most parsed non-trivial words, kanji explorer,
/// and latest Anki cards (both mined in-game and fetched from AnkiConnect).
/// </summary>
public sealed class StatsWindow : Window
{
    private static readonly Vector4 AccentPurple = new(0.66f, 0.61f, 0.92f, 1f);
    private static readonly Vector4 AccentCyan = new(0.38f, 0.78f, 0.98f, 1f);
    private static readonly Vector4 AccentGreen = new(0.38f, 0.84f, 0.58f, 1f);
    private static readonly Vector4 AccentAmber = new(0.96f, 0.74f, 0.34f, 1f);
    private static readonly Vector4 AccentCoral = new(0.95f, 0.50f, 0.52f, 1f);
    private static readonly Vector4 MutedText = new(0.68f, 0.70f, 0.76f, 1f);

    private static readonly (StatsRange Range, string Label)[] Ranges =
    [
        (StatsRange.Session, "Session"),
        (StatsRange.Today, "Today"),
        (StatsRange.Last7Days, "7 Days"),
        (StatsRange.Last30Days, "30 Days"),
        (StatsRange.AllTime, "All Time"),
    ];

    private readonly Configuration config;
    private readonly StatsTracker tracker;
    private readonly AnkiCardFeed ankiFeed;
    private readonly PopupManager popups;
    private readonly Fonts fonts;

    private StatsRange selectedRange = StatsRange.Last7Days;
    private StatsSnapshot? cachedSnapshot;
    private StatsRange cachedRange = (StatsRange)(-1);
    private long cachedVersion = -1;

    private readonly List<RichBlock> dialogueBlocks = new();
    private readonly List<RichBlock> minedCardBlocks = new();

    private string dialogueFilter = string.Empty;
    private string wordFilter = string.Empty;
    private int wordMode; // 0 = Parsed in Dialogue, 1 = Most Looked Up ("Nemesis")
    private int ankiViewMode; // 0 = Mined in FFXIV, 1 = Latest from AnkiConnect
    private bool confirmClear;
    private string? statusBanner;
    private DateTime statusBannerUntil = DateTime.MinValue;

    public StatsWindow(
        Configuration config,
        StatsTracker tracker,
        AnkiCardFeed ankiFeed,
        PopupManager popups,
        Fonts fonts)
        : base("Hachidori Immersion Stats##hachidori-ffxiv-stats")
    {
        this.config = config;
        this.tracker = tracker;
        this.ankiFeed = ankiFeed;
        this.popups = popups;
        this.fonts = fonts;
        Size = new Vector2(860, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 440),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void OnOpen()
    {
        ankiFeed.RefreshIfStale(TimeSpan.FromMinutes(2));
    }

    public override void Draw()
    {
        var snap = GetCurrentSnapshot();
        var scale = ImGuiHelpers.GlobalScale;

        DrawHeaderBar(scale);
        if (statusBanner is not null && DateTime.UtcNow < statusBannerUntil)
        {
            ImGui.TextColored(AccentGreen, statusBanner);
        }
        else if (statusBanner is not null)
        {
            statusBanner = null;
        }

        DrawRankBanner(snap, scale);
        ImGui.Spacing();
        DrawHeroCards(snap, scale);
        ImGui.Spacing();

        using var tabs = ImRaii.TabBar("##stats-tabs");
        if (!tabs.Success)
            return;

        using (var tab = ImRaii.TabItem("Overview"))
        {
            if (tab.Success)
            {
                using var scroll = ImRaii.Child("##stats-overview-scroll", Vector2.Zero, false);
                if (scroll.Success)
                    DrawOverviewTab(snap, scale);
            }
        }

        using (var tab = ImRaii.TabItem($"NPC Dialogue ({snap.TopNpcs.Count})"))
        {
            if (tab.Success)
            {
                using var scroll = ImRaii.Child("##stats-npc-scroll", Vector2.Zero, false);
                if (scroll.Success)
                    DrawNpcTab(snap, scale);
            }
        }

        using (var tab = ImRaii.TabItem("Top Words & Kanji"))
        {
            if (tab.Success)
            {
                using var scroll = ImRaii.Child("##stats-words-scroll", Vector2.Zero, false);
                if (scroll.Success)
                    DrawWordsTab(snap, scale);
            }
        }

        using (var tab = ImRaii.TabItem($"Anki Cards ({snap.RecentMinedCards.Count})"))
        {
            if (tab.Success)
            {
                using var scroll = ImRaii.Child("##stats-anki-scroll", Vector2.Zero, false);
                if (scroll.Success)
                    DrawAnkiTab(snap, scale);
            }
        }

        using (var tab = ImRaii.TabItem("Achievements"))
        {
            if (tab.Success)
            {
                using var scroll = ImRaii.Child("##stats-ach-scroll", Vector2.Zero, false);
                if (scroll.Success)
                    DrawAchievementsTab(snap, scale);
            }
        }
    }

    private StatsSnapshot GetCurrentSnapshot()
    {
        var ver = tracker.Version;
        if (cachedSnapshot is not null && cachedRange == selectedRange && cachedVersion == ver)
            return cachedSnapshot;

        cachedSnapshot = tracker.GetSnapshot(selectedRange);
        cachedRange = selectedRange;
        cachedVersion = ver;

        dialogueBlocks.Clear();
        foreach (var entry in cachedSnapshot.RecentDialogue)
            dialogueBlocks.Add(new RichBlock().Add(entry.Text));

        minedCardBlocks.Clear();
        foreach (var card in cachedSnapshot.RecentMinedCards)
            minedCardBlocks.Add(new RichBlock().Add(card.Sentence));

        return cachedSnapshot;
    }

    // -------------------------------------------------------------- header

    private void DrawHeaderBar(float scale)
    {
        for (var i = 0; i < Ranges.Length; i++)
        {
            var (range, label) = Ranges[i];
            if (i > 0)
                ImGui.SameLine(0, 4 * scale);

            var active = selectedRange == range;
            using var color = ImRaii.PushColor(ImGuiCol.Button, active ? new Vector4(0.45f, 0.40f, 0.78f, 0.95f) : new Vector4(0.18f, 0.19f, 0.24f, 0.9f));
            using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, active ? new Vector4(0.52f, 0.47f, 0.86f, 1f) : new Vector4(0.26f, 0.28f, 0.35f, 1f));
            if (ImGui.Button(label))
                selectedRange = range;
        }

        var rightWidth = 230 * scale;
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > rightWidth)
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - rightWidth);
        else
            ImGui.SameLine();

        if (IconButton(FontAwesomeIcon.Sync, "Refresh Anki", "Refresh recent cards from AnkiConnect", ankiFeed.Loading))
            ankiFeed.Refresh();

        ImGui.SameLine(0, 4 * scale);
        if (IconButton(FontAwesomeIcon.Download, "Export", "Export full immersion stats as JSON"))
        {
            try
            {
                var dir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "exports");
                var path = tracker.ExportToFile(dir);
                ImGui.SetClipboardText(path);
                SetStatus($"Exported stats JSON (path copied): {path}");
            }
            catch (Exception ex)
            {
                SetStatus($"Export failed: {ex.Message}");
            }
        }

        ImGui.SameLine(0, 4 * scale);
        if (selectedRange == StatsRange.Session)
        {
            if (IconButton(FontAwesomeIcon.Redo, "Reset", "Reset current session counters"))
            {
                tracker.ResetSession();
                SetStatus("Session counters reset.");
            }
        }
        else
        {
            using var danger = ImRaii.PushColor(ImGuiCol.Button, confirmClear ? new Vector4(0.75f, 0.22f, 0.22f, 0.95f) : new Vector4(0.24f, 0.18f, 0.20f, 0.9f));
            if (ImGui.Button(confirmClear ? "Confirm?" : "Clear"))
            {
                if (confirmClear)
                {
                    confirmClear = false;
                    tracker.ClearAll();
                    SetStatus("All saved stats cleared.");
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
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Click twice to wipe all recorded immersion stats.");
        }

        ImGui.Spacing();
    }

    private void DrawRankBanner(StatsSnapshot snap, float scale)
    {
        var (level, title, subtitle, floor, next) = NonTrivialWords.GetRank(snap.AllTimeJapaneseChars);
        var width = ImGui.GetContentRegionAvail().X;
        var height = 56 * scale;
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        draw.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.ColorConvertFloat4ToU32(new Vector4(0.14f, 0.13f, 0.22f, 0.92f)), 8 * scale);
        draw.AddRect(pos, pos + new Vector2(width, height), ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.50f, 0.85f, 0.35f)), 8 * scale);

        var pad = 12 * scale;
        ImGui.SetCursorScreenPos(pos + new Vector2(pad, 7 * scale));
        ImGui.TextColored(AccentPurple, $"Lv.{level}  {title}");
        ImGui.SameLine();
        ImGui.TextColored(MutedText, $"— {subtitle}");

        // Right-aligned streak pill
        var streakLabel = snap.CurrentStreakDays > 0
            ? $"Streak: {snap.CurrentStreakDays}d (Best {snap.BestStreakDays}d)"
            : $"Best streak: {snap.BestStreakDays}d";
        var streakWidth = ImGui.CalcTextSize(streakLabel).X + 24 * scale;
        ImGui.SetCursorScreenPos(new Vector2(pos.X + width - streakWidth - pad, pos.Y + 7 * scale));
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            ImGui.TextColored(snap.CurrentStreakDays > 0 ? AccentAmber : MutedText, FontAwesomeIcon.Fire.ToIconString());
        ImGui.SameLine(0, 5 * scale);
        ImGui.TextColored(snap.CurrentStreakDays > 0 ? AccentAmber : MutedText, streakLabel);

        // XP bar
        var barPos = pos + new Vector2(pad, 33 * scale);
        var barW = width - pad * 2;
        var barH = 12 * scale;
        var progress = next is { } target
            ? Math.Clamp((float)(snap.AllTimeJapaneseChars - floor) / Math.Max(1, target - floor), 0f, 1f)
            : 1f;
        draw.AddRectFilled(barPos, barPos + new Vector2(barW, barH), ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.08f, 0.12f, 1f)), 6 * scale);
        if (progress > 0.005f)
            draw.AddRectFilled(barPos, barPos + new Vector2(Math.Max(barH, barW * progress), barH), ImGui.ColorConvertFloat4ToU32(AccentPurple), 6 * scale);

        var xpText = next is { } targetChars
            ? $"{snap.AllTimeJapaneseChars:N0} / {targetChars:N0} lifetime Japanese chars  ({progress * 100f:0}%)"
            : $"{snap.AllTimeJapaneseChars:N0} lifetime Japanese chars — MAX RANK!";
        var xpSize = ImGui.CalcTextSize(xpText);
        draw.AddText(new Vector2(barPos.X + (barW - xpSize.X) * 0.5f, barPos.Y - 1 * scale),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.96f, 0.96f, 1f, 0.95f)), xpText);

        ImGui.SetCursorScreenPos(pos + new Vector2(0, height + 2 * scale));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawHeroCards(StatsSnapshot snap, float scale)
    {
        var gap = 8 * scale;
        var avail = ImGui.GetContentRegionAvail().X;
        var cardW = Math.Max(130 * scale, (avail - gap * 3) / 4f);
        var cardH = 74 * scale;

        var equiv = NonTrivialWords.ReadingEquivalent(snap.JapaneseChars);
        var speedText = snap.CharsPerHour > 0 ? $"{snap.CharsPerHour:N0} chars/hr · {FormatDuration(snap.ActiveSeconds)}" : FormatDuration(snap.ActiveSeconds);
        var miningRate = snap.CardsMined > 0 ? $"1 card / {snap.CharsPerCard:N0} chars" : $"{snap.Lookups:N0} lookups";
        var joyoPct = Math.Min(100.0, snap.UniqueKanjiCount / 2136.0 * 100.0);

        DrawKpiCard(cardW, cardH, AccentCyan, FontAwesomeIcon.BookOpen,
            "JAPANESE CHARS", $"{snap.JapaneseChars:N0}", $"{snap.KanjiPercent:0.0}% kanji · {equiv}", scale);
        ImGui.SameLine(0, gap);
        DrawKpiCard(cardW, cardH, AccentGreen, FontAwesomeIcon.Comments,
            "LINES & PACE", $"{snap.Lines:N0}", speedText, scale);
        ImGui.SameLine(0, gap);
        DrawKpiCard(cardW, cardH, AccentAmber, FontAwesomeIcon.Clone,
            "ANKI CARDS MINED", $"{snap.CardsMined:N0}", miningRate, scale);
        ImGui.SameLine(0, gap);
        DrawKpiCard(cardW, cardH, AccentCoral, FontAwesomeIcon.Star,
            "UNIQUE KANJI", $"{snap.UniqueKanjiCount:N0}", $"{joyoPct:0.0}% of Joyo · {snap.TopNpcs.Count} NPCs", scale);
    }

    private static void DrawKpiCard(float width, float height, Vector4 accent, FontAwesomeIcon icon, string label, string value, string sub, float scale)
    {
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var max = pos + new Vector2(width, height);

        draw.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0.13f, 0.14f, 0.18f, 0.92f)), 8 * scale);
        draw.AddRect(pos, max, ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.30f)), 8 * scale);
        draw.AddRectFilled(pos, new Vector2(pos.X + 4 * scale, max.Y), ImGui.ColorConvertFloat4ToU32(accent), 8 * scale, ImDrawFlags.RoundCornersLeft);

        using (ImRaii.Group())
        {
            ImGui.Dummy(new Vector2(width, height));
            var padX = 12 * scale;
            ImGui.SetCursorScreenPos(pos + new Vector2(padX, 7 * scale));
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                ImGui.TextColored(accent, icon.ToIconString());
            ImGui.SameLine(0, 6 * scale);
            ImGui.TextColored(MutedText, label);

            ImGui.SetCursorScreenPos(pos + new Vector2(padX, 27 * scale));
            ImGui.SetWindowFontScale(1.35f);
            ImGui.TextUnformatted(value);
            ImGui.SetWindowFontScale(1.0f);

            ImGui.SetCursorScreenPos(pos + new Vector2(padX, 53 * scale));
            ImGui.TextColored(MutedText, TruncateToWidth(sub, width - padX - 8 * scale));
            ImGui.SetCursorScreenPos(new Vector2(pos.X, max.Y));
        }
    }

    // -------------------------------------------------------------- overview

    private void DrawOverviewTab(StatsSnapshot snap, float scale)
    {
        var avail = ImGui.GetContentRegionAvail().X;

        // 1. Daily Reading Activity Bar Chart
        SectionTitle(FontAwesomeIcon.ChartBar, snap.DailySeries.Count <= 7 ? "Daily Characters Read (Last 7 Days)" : "Daily Characters Read (Last 30 Days)", AccentCyan, scale);
        DrawDailyBarChart(snap.DailySeries, avail, 155 * scale, scale);
        ImGui.Spacing();

        // 2. 30-Day Activity Heatmap
        SectionTitle(FontAwesomeIcon.CalendarAlt, "30-Day Activity Heatmap", AccentGreen, scale);
        DrawHeatmap30(snap.Heatmap30Days, avail, scale);
        ImGui.Spacing();

        // 3. Two-column bottom: Immersion Clock + Fun Highlights
        var colW = Math.Max(260 * scale, (avail - 12 * scale) * 0.5f);
        using (var table = ImRaii.Table("##overview-split", 2, ImGuiTableFlags.SizingStretchSame))
        {
            if (table.Success)
            {
                ImGui.TableNextColumn();
                DrawImmersionClock(snap, colW - 8 * scale, scale);

                ImGui.TableNextColumn();
                DrawHighlightsCard(snap, scale);
            }
        }
    }

    private static void DrawDailyBarChart(IReadOnlyList<DayPoint> series, float width, float height, float scale)
    {
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var max = pos + new Vector2(width, height);

        draw.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0.11f, 0.12f, 0.16f, 0.92f)), 8 * scale);
        draw.AddRect(pos, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.08f)), 8 * scale);

        ImGui.InvisibleButton("##daily-chart", new Vector2(width, height));
        if (series.Count == 0)
            return;

        var padL = 14 * scale;
        var padR = 14 * scale;
        var padT = 16 * scale;
        var padB = 24 * scale;
        var plotMin = new Vector2(pos.X + padL, pos.Y + padT);
        var plotSize = new Vector2(Math.Max(40, width - padL - padR), Math.Max(40, height - padT - padB));
        var maxVal = Math.Max(1, series.Max(p => p.JapaneseChars));

        // Subtle horizontal gridlines
        var gridCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.06f));
        for (var g = 0; g <= 2; g++)
        {
            var y = plotMin.Y + plotSize.Y * g / 2f;
            draw.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMin.X + plotSize.X, y), gridCol, 1f);
        }

        var step = plotSize.X / series.Count;
        var barGap = Math.Clamp(step * 0.22f, 2f, 10f);
        var barW = Math.Max(3f, step - barGap);
        var mouse = ImGui.GetMousePos();
        var hovered = ImGui.IsItemHovered();
        DayPoint? hoveredPoint = null;

        var labelStep = series.Count <= 7 ? 1 : 5;
        for (var i = 0; i < series.Count; i++)
        {
            var p = series[i];
            var x0 = plotMin.X + i * step + barGap * 0.5f;
            var x1 = x0 + barW;
            var ratio = (float)p.JapaneseChars / maxVal;
            var barH = p.JapaneseChars > 0 ? Math.Max(3f * scale, ratio * plotSize.Y) : 2f;
            var y1 = plotMin.Y + plotSize.Y;
            var y0 = y1 - barH;

            var isHot = hovered && mouse.X >= x0 - barGap * 0.5f && mouse.X <= x1 + barGap * 0.5f && mouse.Y >= pos.Y && mouse.Y <= max.Y;
            if (isHot)
                hoveredPoint = p;

            var col = p.JapaneseChars == 0
                ? new Vector4(0.25f, 0.27f, 0.34f, 0.45f)
                : isHot
                    ? new Vector4(0.55f, 0.88f, 1f, 1f)
                    : new Vector4(0.35f, 0.72f, 0.96f, 0.88f);
            draw.AddRectFilled(new Vector2(x0, y0), new Vector2(x1, y1), ImGui.ColorConvertFloat4ToU32(col), 3 * scale, ImDrawFlags.RoundCornersTop);

            // Show small gold cap if cards were mined that day
            if (p.CardsMined > 0 && p.JapaneseChars > 0)
                draw.AddRectFilled(new Vector2(x0, y0), new Vector2(x1, Math.Min(y1, y0 + 3 * scale)), ImGui.ColorConvertFloat4ToU32(AccentAmber));

            if (i % labelStep == 0 || i == series.Count - 1)
            {
                var lblSize = ImGui.CalcTextSize(p.ShortLabel);
                draw.AddText(new Vector2(x0 + (barW - lblSize.X) * 0.5f, y1 + 4 * scale),
                    ImGui.ColorConvertFloat4ToU32(MutedText), p.ShortLabel);
            }
        }

        // Max value callout top-right
        var maxLbl = $"Peak: {maxVal:N0} chars";
        var maxLblSize = ImGui.CalcTextSize(maxLbl);
        draw.AddText(new Vector2(pos.X + width - padR - maxLblSize.X, pos.Y + 3 * scale),
            ImGui.ColorConvertFloat4ToU32(MutedText), maxLbl);

        if (hoveredPoint is { } hp)
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(AccentCyan, hp.DateKey);
            ImGui.TextUnformatted($"Japanese chars: {hp.JapaneseChars:N0}");
            ImGui.TextUnformatted($"Lines read: {hp.Lines:N0}");
            ImGui.TextUnformatted($"Lookups: {hp.Lookups:N0}   ·   Anki cards: {hp.CardsMined:N0}");
            if (hp.ActiveSeconds > 0)
                ImGui.TextDisabled($"Active time: {FormatDuration(hp.ActiveSeconds)}");
            ImGui.EndTooltip();
        }
    }

    private static void DrawHeatmap30(IReadOnlyList<DayPoint> days, float width, float scale)
    {
        if (days.Count == 0)
            return;

        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var gap = 4 * scale;
        var cell = Math.Clamp((width - gap * (days.Count - 1)) / days.Count, 10 * scale, 26 * scale);
        var totalW = days.Count * cell + (days.Count - 1) * gap;
        var height = cell + 4 * scale;

        ImGui.InvisibleButton("##heatmap-30", new Vector2(Math.Max(width, totalW), height));
        var maxChars = Math.Max(1, days.Max(d => d.JapaneseChars));
        var mouse = ImGui.GetMousePos();
        var hovered = ImGui.IsItemHovered();
        DayPoint? hot = null;

        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var x0 = pos.X + i * (cell + gap);
            var y0 = pos.Y + 2 * scale;
            var rectMax = new Vector2(x0 + cell, y0 + cell);
            var intensity = d.JapaneseChars == 0 ? 0f : Math.Clamp((float)Math.Sqrt((double)d.JapaneseChars / maxChars), 0.18f, 1f);
            var col = d.JapaneseChars == 0
                ? new Vector4(0.16f, 0.17f, 0.22f, 0.9f)
                : Vector4.Lerp(new Vector4(0.14f, 0.36f, 0.28f, 1f), AccentGreen, intensity);
            draw.AddRectFilled(new Vector2(x0, y0), rectMax, ImGui.ColorConvertFloat4ToU32(col), 3 * scale);

            if (hovered && mouse.X >= x0 && mouse.X <= rectMax.X && mouse.Y >= y0 && mouse.Y <= rectMax.Y)
            {
                hot = d;
                draw.AddRect(new Vector2(x0, y0), rectMax, ImGui.ColorConvertFloat4ToU32(Vector4.One), 3 * scale, ImDrawFlags.None, 1.5f);
            }
        }

        if (hot is { } hd)
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(AccentGreen, hd.DateKey);
            ImGui.TextUnformatted($"{hd.JapaneseChars:N0} chars · {hd.Lines:N0} lines · {hd.CardsMined:N0} Anki cards");
            ImGui.EndTooltip();
        }
    }

    private static void DrawImmersionClock(StatsSnapshot snap, float width, float scale)
    {
        var (badge, desc) = NonTrivialWords.ReadingPersona(snap.HourlyChars);
        SectionTitle(FontAwesomeIcon.Clock, $"Immersion Clock — {badge}", AccentPurple, scale);
        ImGui.TextColored(MutedText, desc);

        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var height = 96 * scale;
        var max = pos + new Vector2(width, height);
        draw.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0.11f, 0.12f, 0.16f, 0.92f)), 8 * scale);

        ImGui.InvisibleButton("##clock-24", new Vector2(width, height));
        var padX = 10 * scale;
        var padT = 10 * scale;
        var padB = 20 * scale;
        var plotW = Math.Max(48, width - padX * 2);
        var plotH = Math.Max(24, height - padT - padB);
        var peak = Math.Max(1, snap.HourlyChars.Max());
        var step = plotW / 24f;
        var barW = Math.Max(2f, step - 2f);
        var mouse = ImGui.GetMousePos();
        var hovered = ImGui.IsItemHovered();
        var hotHour = -1;

        for (var h = 0; h < 24; h++)
        {
            var val = snap.HourlyChars[h];
            var x0 = pos.X + padX + h * step;
            var y1 = pos.Y + padT + plotH;
            var bh = val > 0 ? Math.Max(3f, (float)val / peak * plotH) : 2f;
            var y0 = y1 - bh;

            if (hovered && mouse.X >= x0 && mouse.X <= x0 + step && mouse.Y >= pos.Y && mouse.Y <= max.Y)
                hotHour = h;

            var col = val == 0 ? new Vector4(0.24f, 0.25f, 0.30f, 0.5f) : hotHour == h ? AccentAmber : AccentPurple;
            draw.AddRectFilled(new Vector2(x0, y0), new Vector2(x0 + barW, y1), ImGui.ColorConvertFloat4ToU32(col), 2 * scale);

            if (h % 6 == 0)
                draw.AddText(new Vector2(x0, y1 + 3 * scale), ImGui.ColorConvertFloat4ToU32(MutedText), $"{h:00}h");
        }

        if (hotHour >= 0)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"{hotHour:00}:00–{(hotHour + 1) % 24:00}:00  ·  {snap.HourlyChars[hotHour]:N0} Japanese chars");
            ImGui.EndTooltip();
        }
    }

    private void DrawHighlightsCard(StatsSnapshot snap, float scale)
    {
        SectionTitle(FontAwesomeIcon.Trophy, "Highlights & Breakdown", AccentAmber, scale);

        // Source breakdown bar (Dialogue vs Battle Talk vs Chat)
        var totalLines = Math.Max(1, snap.TalkLines + snap.BattleTalkLines + snap.ChatLines);
        ImGui.TextColored(MutedText, $"Dialogue {snap.TalkLines:N0}  ·  Battle Talk {snap.BattleTalkLines:N0}  ·  Chat {snap.ChatLines:N0}");
        var barPos = ImGui.GetCursorScreenPos();
        var barW = ImGui.GetContentRegionAvail().X;
        var barH = 10 * scale;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(barPos, barPos + new Vector2(barW, barH), ImGui.ColorConvertFloat4ToU32(new Vector4(0.16f, 0.17f, 0.22f, 1f)), 5 * scale);
        var x = barPos.X;
        void Seg(int count, Vector4 col)
        {
            if (count <= 0) return;
            var w = barW * count / totalLines;
            draw.AddRectFilled(new Vector2(x, barPos.Y), new Vector2(x + w, barPos.Y + barH), ImGui.ColorConvertFloat4ToU32(col));
            x += w;
        }
        Seg(snap.TalkLines, AccentCyan);
        Seg(snap.BattleTalkLines, AccentCoral);
        Seg(snap.ChatLines, AccentGreen);
        ImGui.Dummy(new Vector2(barW, barH + 4 * scale));

        if (snap.MostActiveDay is { } best)
            ImGui.BulletText($"Biggest reading day: {best.DateKey} ({best.JapaneseChars:N0} chars, {best.Lines:N0} lines)");

        if (snap.TopNpcs.Count > 0)
        {
            var mvp = snap.TopNpcs[0];
            ImGui.Bullet();
            ImGui.TextUnformatted("Chattiest NPC: ");
            ImGui.SameLine(0, 0);
            using (fonts.Popup.Push())
                ImGui.TextColored(AccentPurple, mvp.Speaker);
            ImGui.SameLine(0, 4 * scale);
            ImGui.TextDisabled($"({mvp.JapaneseChars:N0} chars)");
        }

        if (snap.TopParsedWords.Count > 0)
        {
            var topW = snap.TopParsedWords[0];
            ImGui.Bullet();
            ImGui.TextUnformatted("Most parsed non-trivial word: ");
            ImGui.SameLine(0, 0);
            using (fonts.Popup.Push())
                ImGui.TextColored(AccentCyan, topW.Word);
            ImGui.SameLine(0, 4 * scale);
            ImGui.TextDisabled($"({topW.Count}×)");
        }

        if (snap.LongestLine is { } longest)
        {
            ImGui.Bullet();
            ImGui.TextUnformatted("Longest single monologue: ");
            ImGui.SameLine(0, 0);
            using (fonts.Popup.Push())
                ImGui.TextColored(AccentAmber, string.IsNullOrEmpty(longest.Speaker) ? "Narrator" : longest.Speaker);
            ImGui.SameLine(0, 4 * scale);
            ImGui.TextDisabled($"({longest.JapaneseChars} chars)");
            using (fonts.Popup.Push())
            {
                var snippet = longest.Text.Length > 70 ? longest.Text[..69] + "…" : longest.Text;
                ImGui.TextDisabled($"  “{snippet}”");
            }
        }
    }

    // -------------------------------------------------------------- NPCs

    private void DrawNpcTab(StatsSnapshot snap, float scale)
    {
        SectionTitle(FontAwesomeIcon.Users, "Chattiest NPCs", AccentPurple, scale);
        if (snap.TopNpcs.Count == 0)
        {
            ImGui.TextDisabled("No NPC dialogue recorded in this window yet. Talk to NPCs in FFXIV to fill the leaderboard!");
        }
        else
        {
            ImGui.TextDisabled("Click any NPC name to open their Hachidori dictionary entry (and NPC picture if installed).");
            var maxChars = Math.Max(1, snap.TopNpcs[0].JapaneseChars);
            var barMaxW = Math.Max(80 * scale, ImGui.GetContentRegionAvail().X * 0.28f);

            using var table = ImRaii.Table("##npc-leaderboard", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
            if (table.Success)
            {
                ImGui.TableSetupColumn("NPC", ImGuiTableColumnFlags.WidthStretch, 0.28f);
                ImGui.TableSetupColumn("Volume", ImGuiTableColumnFlags.WidthFixed, barMaxW + 85 * scale);
                ImGui.TableSetupColumn("Lines", ImGuiTableColumnFlags.WidthFixed, 56 * scale);
                ImGui.TableSetupColumn("Latest Quote", ImGuiTableColumnFlags.WidthStretch, 0.42f);
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
                    DrawInlineBar((float)npc.JapaneseChars / maxChars, barMaxW, 12 * scale, AccentPurple, $"{npc.JapaneseChars:N0}", scale);

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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        SectionTitle(FontAwesomeIcon.Comments, "Recent NPC Dialogue Feed", AccentCyan, scale);
        ImGui.SetNextItemWidth(260 * scale);
        ImGui.InputTextWithHint("##npc-feed-filter", "Filter by NPC or dialogue text…", ref dialogueFilter, 128);
        ImGui.SameLine();
        ImGui.TextDisabled("Hover any Japanese word below to look it up or mine it.");

        if (snap.RecentDialogue.Count == 0)
        {
            ImGui.TextDisabled("Captured NPC lines will appear here.");
            return;
        }

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
                ImGui.TextColored(AccentPurple, speakerName);
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
        // Mode switcher: Most Parsed Non-Trivial Words vs Most Looked-Up ("Nemesis") Words
        using (var c0 = ImRaii.PushColor(ImGuiCol.Button, wordMode == 0 ? new Vector4(0.32f, 0.56f, 0.78f, 0.95f) : new Vector4(0.18f, 0.19f, 0.24f, 0.9f)))
        {
            if (ImGui.Button($"Most Parsed Non-Trivial Words ({snap.TopParsedWords.Count})"))
                wordMode = 0;
        }
        ImGui.SameLine(0, 6 * scale);
        using (var c1 = ImRaii.PushColor(ImGuiCol.Button, wordMode == 1 ? new Vector4(0.76f, 0.42f, 0.44f, 0.95f) : new Vector4(0.18f, 0.19f, 0.24f, 0.9f)))
        {
            if (ImGui.Button($"Most Looked Up / Nemesis Words ({snap.TopLookedUpWords.Count})"))
                wordMode = 1;
        }

        ImGui.SameLine(0, 12 * scale);
        ImGui.SetNextItemWidth(200 * scale);
        ImGui.InputTextWithHint("##word-filter", "Filter words…", ref wordFilter, 64);

        var list = wordMode == 0 ? snap.TopParsedWords : snap.TopLookedUpWords;
        var barColor = wordMode == 0 ? AccentCyan : AccentCoral;
        ImGui.TextDisabled(wordMode == 0
            ? "Particles, pronouns, auxiliaries and single kana are automatically filtered out. Click any word to open its dictionary popup."
            : "Words you've hovered and looked up most often — great candidates to mine into Anki! Click any word to inspect or mine.");

        if (list.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(wordMode == 0
                ? "No non-trivial words parsed in this time window yet. Read some FFXIV dialogue to populate this list!"
                : "No dictionary lookups recorded in this time window yet.");
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
                    DrawInlineBar((float)item.Count / maxCount, barW, 11 * scale, barColor, $"{item.Count}×", scale);

                    ImGui.TableNextColumn();
                    using (fonts.Popup.Push())
                        ImGui.TextDisabled(item.ShortMeaning);
                }
            }
        }

        // Kanji Spotlight
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        SectionTitle(FontAwesomeIcon.Star, $"Kanji Encountered ({snap.UniqueKanjiCount:N0} unique · {snap.KanjiChars:N0} total)", AccentAmber, scale);
        if (snap.SampleKanji.Count == 0)
        {
            ImGui.TextDisabled("Kanji you encounter in game text will appear here. Click any tile for kanji details.");
        }
        else
        {
            ImGui.TextDisabled("Click any kanji tile to open its kanji dictionary entry.");
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
        using (var c0 = ImRaii.PushColor(ImGuiCol.Button, ankiViewMode == 0 ? new Vector4(0.68f, 0.50f, 0.22f, 0.95f) : new Vector4(0.18f, 0.19f, 0.24f, 0.9f)))
        {
            if (ImGui.Button($"Mined in FFXIV ({snap.RecentMinedCards.Count})"))
                ankiViewMode = 0;
        }
        ImGui.SameLine(0, 6 * scale);
        using (var c1 = ImRaii.PushColor(ImGuiCol.Button, ankiViewMode == 1 ? new Vector4(0.32f, 0.56f, 0.78f, 0.95f) : new Vector4(0.18f, 0.19f, 0.24f, 0.9f)))
        {
            if (ImGui.Button($"Latest from AnkiConnect ({ankiFeed.Notes.Count})"))
            {
                ankiViewMode = 1;
                ankiFeed.RefreshIfStale(TimeSpan.FromSeconds(30));
            }
        }

        if (ankiFeed.LastRefreshedUtc is not null)
        {
            ImGui.SameLine(0, 12 * scale);
            ImGui.TextDisabled($"Anki total added: {ankiFeed.AddedLast7Days} (7d) · {ankiFeed.AddedLast30Days} (30d)");
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
            ImGui.TextWrapped("No Anki cards mined through Hachidori yet. Click + on any popup entry to mine a word — it will show up right here with its NPC speaker, sentence, and attached media!");
            ImGui.Spacing();
            if (ImGui.Button("View recent cards directly from AnkiConnect instead"))
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
                using var btnCol = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.22f, 0.19f, 0.13f, 0.85f));
                using var btnHov = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.32f, 0.27f, 0.17f, 0.95f));
                using var txtCol = ImRaii.PushColor(ImGuiCol.Text, AccentAmber);
                if (ImGui.SmallButton($"{card.Expression}##expr-{i}"))
                    popups.OpenQuery(card.Expression);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Click to open dictionary popup");
                if (card.Reading.Length > 0 && card.Reading != card.Expression)
                {
                    ImGui.SameLine(0, 6 * scale);
                    ImGui.TextDisabled($"【{card.Reading}】");
                }
            }

            if (card.Pitch.Length > 0)
            {
                ImGui.SameLine(0, 6 * scale);
                ImGui.TextColored(AccentCyan, card.Pitch);
            }
            if (card.Frequency.Length > 0)
            {
                ImGui.SameLine(0, 6 * scale);
                ImGui.TextDisabled($"#{card.Frequency}");
            }

            // Media badges
            if (card.HasScreenshot) DrawMiniBadge("IMG", AccentCyan, scale);
            if (card.HasAudio) DrawMiniBadge("AUDIO", AccentGreen, scale);
            if (card.HasAnimation) DrawMiniBadge("ANIM", AccentPurple, scale);
            if (card.HasTranslation) DrawMiniBadge("TL", AccentAmber, scale);

            ImGui.SameLine(0, 8 * scale);
            ImGui.TextDisabled(RelativeTime(card.MinedAtUtc));

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
                        ImGui.TextColored(AccentPurple, $"{card.Speaker}:");
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
            ImGui.TextColored(AccentCoral, err);
            if (ImGui.Button("Retry AnkiConnect"))
                ankiFeed.Refresh();
            return;
        }

        if (ankiFeed.Notes.Count == 0)
        {
            ImGui.TextDisabled("No Anki notes added in the last 30 days (or click Refresh Anki above).");
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
            if (note.HasAudio) DrawMiniBadge("AUD", AccentGreen, scale);
            if (note.HasImage) DrawMiniBadge("IMG", AccentCyan, scale);

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Browse"))
                ankiFeed.BrowseNote(note.NoteId);
        }
    }

    // -------------------------------------------------------------- achievements

    private static void DrawAchievementsTab(StatsSnapshot snap, float scale)
    {
        var unlocked = snap.Achievements.Count(a => a.Unlocked);
        SectionTitle(FontAwesomeIcon.Trophy, $"Immersion Milestones ({unlocked} / {snap.Achievements.Count} unlocked)", AccentAmber, scale);
        ImGui.Spacing();

        var avail = ImGui.GetContentRegionAvail().X;
        var cols = avail >= 660 * scale ? 3 : 2;
        using var table = ImRaii.Table("##achievements-grid", cols, ImGuiTableFlags.SizingStretchSame);
        if (!table.Success)
            return;

        foreach (var ach in snap.Achievements)
        {
            ImGui.TableNextColumn();
            var cardW = ImGui.GetContentRegionAvail().X;
            var cardH = 74 * scale;
            var pos = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            var accent = ach.Unlocked ? AccentAmber : MutedText;

            draw.AddRectFilled(pos, pos + new Vector2(cardW, cardH),
                ImGui.ColorConvertFloat4ToU32(ach.Unlocked ? new Vector4(0.18f, 0.16f, 0.12f, 0.92f) : new Vector4(0.12f, 0.13f, 0.17f, 0.85f)), 8 * scale);
            draw.AddRect(pos, pos + new Vector2(cardW, cardH),
                ImGui.ColorConvertFloat4ToU32(ach.Unlocked ? new Vector4(0.96f, 0.74f, 0.34f, 0.45f) : new Vector4(1f, 1f, 1f, 0.08f)), 8 * scale);

            ImGui.Dummy(new Vector2(cardW, cardH));
            var pad = 10 * scale;
            ImGui.SetCursorScreenPos(pos + new Vector2(pad, 8 * scale));
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                ImGui.TextColored(accent, (ach.Unlocked ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.Trophy).ToIconString());
            ImGui.SameLine(0, 6 * scale);
            ImGui.TextColored(ach.Unlocked ? AccentAmber : Vector4.One, ach.Title);

            ImGui.SetCursorScreenPos(pos + new Vector2(pad, 28 * scale));
            ImGui.TextColored(MutedText, TruncateToWidth(ach.Description, cardW - pad * 2));

            var pct = Math.Clamp((float)ach.Current / Math.Max(1, ach.Target), 0f, 1f);
            var barPos = pos + new Vector2(pad, 52 * scale);
            var barW = cardW - pad * 2;
            var barH = 10 * scale;
            draw.AddRectFilled(barPos, barPos + new Vector2(barW, barH), ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.08f, 0.11f, 1f)), 4 * scale);
            if (pct > 0.01f)
                draw.AddRectFilled(barPos, barPos + new Vector2(barW * pct, barH), ImGui.ColorConvertFloat4ToU32(ach.Unlocked ? AccentAmber : AccentCyan), 4 * scale);
            var label = $"{Math.Min(ach.Current, ach.Target):N0} / {ach.Target:N0}";
            var lSize = ImGui.CalcTextSize(label);
            draw.AddText(new Vector2(barPos.X + barW - lSize.X, pos.Y + 8 * scale), ImGui.ColorConvertFloat4ToU32(MutedText), label);

            ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + cardH + 6 * scale));
            ImGui.Dummy(Vector2.Zero);
        }
    }

    // -------------------------------------------------------------- helpers

    private void SetStatus(string message)
    {
        statusBanner = message;
        statusBannerUntil = DateTime.UtcNow.AddSeconds(6);
    }

    private static void SectionTitle(FontAwesomeIcon icon, string title, Vector4 accent, float scale)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            ImGui.TextColored(accent, icon.ToIconString());
        ImGui.SameLine(0, 7 * scale);
        ImGui.TextUnformatted(title);
    }

    private static void DrawInlineBar(float ratio, float maxWidth, float height, Vector4 color, string valueLabel, float scale)
    {
        var pos = ImGui.GetCursorScreenPos();
        var y = pos.Y + (ImGui.GetTextLineHeight() - height) * 0.5f;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(new Vector2(pos.X, y), new Vector2(pos.X + maxWidth, y + height),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.18f, 0.19f, 0.24f, 0.9f)), 3 * scale);
        var fill = Math.Clamp(ratio, 0f, 1f) * maxWidth;
        if (fill > 1f)
            draw.AddRectFilled(new Vector2(pos.X, y), new Vector2(pos.X + fill, y + height), ImGui.ColorConvertFloat4ToU32(color), 3 * scale);
        ImGui.Dummy(new Vector2(maxWidth + 6 * scale, ImGui.GetTextLineHeight()));
        ImGui.SameLine(0, 0);
        ImGui.TextUnformatted(valueLabel);
    }

    private static void DrawMiniBadge(string label, Vector4 color, float scale)
    {
        ImGui.SameLine(0, 4 * scale);
        using var bg = ImRaii.PushColor(ImGuiCol.Button, new Vector4(color.X, color.Y, color.Z, 0.22f));
        using var hov = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(color.X, color.Y, color.Z, 0.28f));
        using var txt = ImRaii.PushColor(ImGuiCol.Text, color);
        using var pad = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(4 * scale, 1 * scale));
        ImGui.SmallButton(label);
    }

    private static bool IconButton(FontAwesomeIcon icon, string label, string tooltip, bool disabled = false)
    {
        using var dis = ImRaii.Disabled(disabled);
        var clicked = ImGui.Button($"{label}##btn-{icon}");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
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
