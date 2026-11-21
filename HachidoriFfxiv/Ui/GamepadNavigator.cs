using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using HachidoriFfxiv.Capture;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Ui;

/// <summary>
/// Controller reading, after GSM's overlay: a mode in which the controller moves a cursor
/// over the dialogue word by word, opens the dictionary at it, and mines.
/// <para>
/// FFXIV binds every button, so the mode takes the controller from the game (Dalamud stops
/// passing its input through) and gives it back on exit. Entering is the only press the game
/// also sees: a /hachidori pad macro on a cross hotbar avoids even that, or hold a chosen button.
/// </para>
/// </summary>
public sealed class GamepadNavigator : IDisposable
{
    private const double RepeatDelay = 0.40;
    private const double RepeatRate = 0.15;

    private readonly Configuration config;
    private readonly PopupManager popups;
    private readonly NativeTextHover nativeHover;
    private readonly HachidoriService service;

    private GamepadButtons previous;
    private DateTime? holdStarted;
    private bool holdFired;
    private bool waitForHoldRelease;
    private readonly Dictionary<string, (DateTime Next, bool Repeating)> repeats = new();
    private DateTime? noTextSince;
    private DateTime lastScroll = DateTime.MinValue;
    private bool popupClosedByBack;

    // Cursor per level: 0 is the game's text, n is the definitions of popup n-1.
    private readonly List<(RichBlock? Block, int Index)> cursor = new() { (null, -1) };
    private int level;
    private bool wordMode = true;

    public GamepadNavigator(Configuration config, PopupManager popups, NativeTextHover nativeHover, HachidoriService service)
    {
        this.config = config;
        this.popups = popups;
        this.nativeHover = nativeHover;
        this.service = service;
        wordMode = config.ControllerStartInWordMode;
    }

    public bool Active { get; private set; }

    /// <summary>Dalamud ignores input blocking when "plugin gamepad input" is off in its settings.</summary>
    public bool BlockingRefused { get; private set; }

    public string? Notice { get; private set; }

    public void Toggle()
    {
        if (Active) Exit();
        else Enter();
    }

    public void Enter()
    {
        if (!config.ControllerEnabled || Active)
            return;
        Active = true;
        level = 0;
        cursor.Clear();
        cursor.Add((null, -1));
        noTextSince = null;
        popupClosedByBack = false;
        wordMode = config.ControllerStartInWordMode;
        waitForHoldRelease = true;
        // Buttons held while entering must not act until released.
        previous = ReadButtons();
        Notice = null;
        // Dalamud also hands the controller to ImGui navigation: no plugin window may hold
        // focus, or A would press its buttons.
        unfocus = true;
    }

    private bool unfocus;

    public void Exit()
    {
        if (!Active)
            return;
        Active = false;
        Plugin.Gamepad.EnableGamepadNav = false;
        nativeHover.ControllerScan = false;
        popups.ControllerActive = false;
        popups.CloseAll();
        repeats.Clear();
    }

    private static readonly GamepadButtons[] AllButtons = Enum.GetValues<GamepadButtons>().Where(b => b != GamepadButtons.None).ToArray();

    private static GamepadButtons ReadButtons()
    {
        var mask = GamepadButtons.None;
        foreach (var button in AllButtons)
        {
            if (Plugin.Gamepad.Raw(button) > 0)
                mask |= button;
        }

        return mask;
    }

    /// <summary>Call every frame after native hover and before the popups draw.</summary>
    public void Draw()
    {
        if (!config.ControllerEnabled)
        {
            Exit();
            return;
        }

        var now = DateTime.UtcNow;
        var raw = ReadButtons();
        var pressed = raw & ~previous;
        previous = raw;
        var hold = config.ControllerHoldButton;

        if (!Active)
        {
            // Long-press to enter: a short press still does whatever FFXIV binds to the button.
            if (hold != GamepadButtons.None && (raw & hold) == hold)
            {
                holdStarted ??= now;
                if (!holdFired && (now - holdStarted.Value).TotalSeconds >= config.ControllerHoldSeconds)
                {
                    holdFired = true;
                    Enter();
                }
            }
            else
            {
                holdStarted = null;
                holdFired = false;
            }

            return;
        }

        if (waitForHoldRelease)
        {
            if ((raw & hold) == hold && hold != GamepadButtons.None)
                pressed &= ~hold;
            else
                waitForHoldRelease = false;
        }

        if (unfocus)
        {
            ImGui.SetWindowFocus((string?)null);
            unfocus = false;
        }

        // Take the controller from the game while the mode is on.
        Plugin.Gamepad.EnableGamepadNav = true;
        BlockingRefused = !Plugin.Gamepad.EnableGamepadNav;
        nativeHover.ControllerScan = true;
        popups.ControllerActive = true;

        if (Has(pressed, GamepadButtons.Select) || Has(pressed, GamepadButtons.Start) || (hold != GamepadButtons.None && !waitForHoldRelease && (pressed & hold) == hold))
        {
            Exit();
            return;
        }

        // A popup closed by the mouse or by time: fall back to a level that still exists.
        level = Math.Min(level, popups.VisibleCount);
        while (cursor.Count <= level)
            cursor.Add((null, -1));

        var stops = Stops(level, out var contexts);
        if (stops.Count == 0)
        {
            if (level > 0)
            {
                level--;
                return;
            }

            noTextSince ??= now;
            Notice = "Waiting for dialogue…";
            if (config.ControllerExitWhenDialogueCloses && (now - noTextSince.Value).TotalSeconds > 1.5)
                Exit();
            DrawHints();
            return;
        }

        noTextSince = null;
        Notice = null;
        var current = FindCurrent(stops);

        // ------------------------------------------------------- navigation
        var stick = Normalize(Plugin.Gamepad.LeftStick);
        var moved = false;
        if (Repeat("left", Has(raw, GamepadButtons.DpadLeft) || stick.X < -0.6f, now)) { current = CursorMoves.Horizontal(stops, current, -1); moved = true; }
        if (Repeat("right", Has(raw, GamepadButtons.DpadRight) || stick.X > 0.6f, now)) { current = CursorMoves.Horizontal(stops, current, 1); moved = true; }
        if (Repeat("up", Has(raw, GamepadButtons.DpadUp) || stick.Y > 0.6f, now)) { current = CursorMoves.Vertical(stops, current, -1); moved = true; }
        if (Repeat("down", Has(raw, GamepadButtons.DpadDown) || stick.Y < -0.6f, now)) { current = CursorMoves.Vertical(stops, current, 1); moved = true; }
        if (moved)
            popupClosedByBack = false;
        if (Has(pressed, GamepadButtons.North))
        {
            wordMode = !wordMode;
            stops = Stops(level, out contexts);
            current = FindCurrent(stops);
        }

        var stop = stops[Math.Clamp(current, 0, stops.Count - 1)];
        cursor[level] = (StopBlock(stop, level), stop.Index);

        // ------------------------------------------------------ popup actions
        var deepest = popups.VisibleCount - 1;
        if (Has(pressed, GamepadButtons.R1) && popups.VisibleCount > level && popups.ScanBlocks(level).Count > 0)
        {
            level++;
            while (cursor.Count <= level)
                cursor.Add((null, -1));
            cursor[level] = (null, -1);
            return;
        }

        if (Has(pressed, GamepadButtons.East) || Has(pressed, GamepadButtons.L1))
        {
            if (level > 0)
            {
                popups.CloseFrom(level);
                level--;
            }
            else if (popups.VisibleCount > 0)
            {
                popups.CloseAll();
                popupClosedByBack = true;
            }
            else if (Has(pressed, GamepadButtons.East))
            {
                Exit();
                return;
            }
        }

        if (deepest >= 0)
        {
            if (Has(pressed, GamepadButtons.South)) popups.MineSelected(deepest);
            if (Has(pressed, GamepadButtons.West)) popups.PlaySelected(deepest);
            if (Has(pressed, GamepadButtons.R2)) popups.SelectEntry(deepest, 1);
            if (Has(pressed, GamepadButtons.L2)) popups.SelectEntry(deepest, -1);
            var right = Normalize(Plugin.Gamepad.RightStick);
            if (Math.Abs(right.Y) > 0.45f && (now - lastScroll).TotalSeconds > RepeatRate)
            {
                popups.Scroll(deepest, -Math.Sign(right.Y) * 110 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
                lastScroll = now;
            }
        }

        // ------------------------------------------------- look up at the cursor
        var block = StopBlock(stop, level);
        if (block is not null && !popupClosedByBack)
        {
            popups.Report(new TextHit(block, stop.Index, stop.Min, stop.Max), level, contexts[stop.Target], forced: true);
            popups.Sustain(level);
        }

        if (level > 0 && popups.LayerRect(level - 1) is { } rect)
        {
            // Keep the cursor visible inside its scrolling popup.
            var margin = ImGui.GetFontSize();
            if (stop.Max.Y > rect.Max.Y - margin) popups.Scroll(level - 1, stop.Max.Y - (rect.Max.Y - margin));
            else if (stop.Min.Y < rect.Min.Y + margin) popups.Scroll(level - 1, stop.Min.Y - (rect.Min.Y + margin));
        }

        DrawCursor(stop);
        DrawHints();
    }

    // ------------------------------------------------------------------ stops

    private readonly List<RichBlock> stopBlocks = new();

    private RichBlock? StopBlock(CursorStop stop, int forLevel) => stop.Target < stopBlocks.Count ? stopBlocks[stop.Target] : null;

    /// <summary>Where the cursor may stop at a level: word starts (or characters) of its text, in reading order.</summary>
    private List<CursorStop> Stops(int atLevel, out List<Func<RichBlock, int, ScanContext>?> contexts)
    {
        stopBlocks.Clear();
        contexts = new List<Func<RichBlock, int, ScanContext>?>();
        var blocks = new List<(RichBlock Block, IReadOnlyList<(int Index, Vector2 Min, Vector2 Max)> Cells, Func<RichBlock, int, ScanContext>? Context)>();
        if (atLevel == 0)
        {
            foreach (var target in nativeHover.Targets().OrderBy(t => t.Cells.Min(c => c.Min.Y)).ThenBy(t => t.Cells.Min(c => c.Min.X)))
                blocks.Add((target.Block, target.Cells, target.Context));
        }
        else
        {
            foreach (var block in popups.ScanBlocks(atLevel - 1))
            {
                var cells = RichText.GlyphRects(block);
                if (cells.Count > 0)
                    blocks.Add((block, cells, null));
            }
        }

        var stops = new List<CursorStop>();
        foreach (var (block, cells, context) in blocks)
        {
            var target = stopBlocks.Count;
            stopBlocks.Add(block);
            contexts.Add(context);
            var text = block.ScanText;
            HashSet<int>? starts = null;
            if (wordMode && config.TokenizedHover)
            {
                var task = service.SegmentAsync(text);
                if (task.IsCompletedSuccessfully)
                    starts = task.Result.Select(s => s.Start).ToHashSet();
            }

            foreach (var (index, min, max) in cells.OrderBy(c => c.Index))
            {
                if (index >= text.Length || char.IsLowSurrogate(text[index]))
                    continue;
                var cp = Japanese.CodePointAt(text, index);
                if (starts is not null ? !starts.Contains(index) : config.ScanOnlyJapanese && !Japanese.IsJapanese(cp))
                    continue;
                stops.Add(new CursorStop(target, index, min, max));
            }
        }

        return stops;
    }

    /// <summary>The cursor's stop, or the nearest one in its block, or the first stop.</summary>
    private int FindCurrent(List<CursorStop> stops)
    {
        var (block, index) = cursor[level];
        if (block is not null)
        {
            var best = -1;
            for (var i = 0; i < stops.Count; i++)
            {
                if (stopBlocks[stops[i].Target] != block)
                    continue;
                if (stops[i].Index <= index)
                    best = i;
                else if (best < 0)
                    best = i;
            }

            if (best >= 0)
                return best;
        }

        // New text: start at the first word of the newest block (the dialogue line).
        return 0;
    }

    // ------------------------------------------------------------------ input

    private static bool Has(GamepadButtons mask, GamepadButtons button) => (mask & button) == button;

    /// <summary>True on press, then again after a delay at a steady rate while held.</summary>
    private bool Repeat(string key, bool down, DateTime now)
    {
        if (!down)
        {
            repeats.Remove(key);
            return false;
        }

        if (!repeats.TryGetValue(key, out var state))
        {
            repeats[key] = (now.AddSeconds(RepeatDelay), true);
            return true;
        }

        if (now < state.Next)
            return false;
        repeats[key] = (now.AddSeconds(RepeatRate), true);
        return true;
    }

    /// <summary>Stick tilt as -1..1 (Dalamud reports -99..99).</summary>
    private static Vector2 Normalize(Vector2 stick)
    {
        var scale = Math.Max(Math.Abs(stick.X), Math.Abs(stick.Y)) > 1.5f ? 99f : 1f;
        return stick / scale;
    }

    // ---------------------------------------------------------------- drawing

    private void DrawCursor(CursorStop stop)
    {
        var draw = ImGui.GetForegroundDrawList();
        var color = Rendering.Css.CssColor.ToAbgr(Rendering.Css.PopupTheme.Current.Accent with { W = 1 });
        var y = stop.Max.Y + 2;
        draw.AddLine(new Vector2(stop.Min.X, y), new Vector2(stop.Max.X, y), color, Math.Max(2f, (stop.Max.Y - stop.Min.Y) / 12f));
    }

    private void DrawHints()
    {
        if (!config.ControllerShowHints)
            return;
        var ps = config.ControllerPlayStationLabels;
        string B(string xbox, string playstation) => ps ? playstation : xbox;
        var parts = new List<string>
        {
            "◀▶ " + (wordMode ? "word" : "character"),
            "▲▼ line",
        };
        if (popups.VisibleCount > 0)
        {
            parts.Add($"{B("A", "✕")} mine");
            parts.Add($"{B("X", "□")} audio");
            parts.Add($"{B("LT/RT", "L2/R2")} entry");
            if (popups.VisibleCount > level)
                parts.Add($"{B("RB", "R1")} look up inside");
        }

        parts.Add($"{B("Y", "△")} {(wordMode ? "characters" : "words")}");
        parts.Add($"{B("B", "○")} back");
        parts.Add($"{B("Back", "Select")} exit");
        var text = (Notice is null ? string.Empty : Notice + "   ") + string.Join("   ", parts)
                   + (BlockingRefused ? "   ⚠ Dalamud is not blocking game input: enable plugin gamepad input in Dalamud's settings" : string.Empty);

        var viewport = ImGui.GetMainViewport();
        var draw = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize();
        var width = ImGui.CalcTextSize(text).X;
        var pos = new Vector2(viewport.Pos.X + (viewport.Size.X - width) * 0.5f, viewport.Pos.Y + viewport.Size.Y - size * 2.4f);
        var pad = new Vector2(size * 0.6f, size * 0.35f);
        draw.AddRectFilled(pos - pad, pos + new Vector2(width, size) + pad, 0xD0101010, size * 0.5f);
        draw.AddText(font, size, pos, 0xFFFFFFFF, text);
    }

    public void Dispose() => Exit();
}
