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
    private bool seenText;
    private DateTime lastScroll = DateTime.MinValue;
    private bool popupClosedByBack;
    private Vector2 lastPointer = new(-10000);
    private Vector2 virtualPointer = new(-10000);
    private DateTime lastPointerMoved = DateTime.MinValue;
    private readonly TouchpadReader touchpad = new();

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
        seenText = false;
        popupClosedByBack = false;
        lastPointer = ImGui.IsMousePosValid() ? ImGui.GetMousePos() : new Vector2(-10000);
        virtualPointer = new Vector2(-10000);
        lastPointerMoved = DateTime.MinValue;
        if (config.ControllerUseTouchpad)
            touchpad.Start();
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
        touchpad.Stop();
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

        // Keep touchpad reader synced with the setting if toggled live.
        if (config.ControllerUseTouchpad && !touchpad.Active) touchpad.Start();
        else if (!config.ControllerUseTouchpad && touchpad.Active) touchpad.Stop();

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

            if (HasBound(pressed, config.ControllerBackExitButton))
            {
                Exit();
                return;
            }

            noTextSince ??= now;
            Notice = "Waiting for text...";
            if (seenText && config.ControllerExitWhenDialogueCloses && (now - noTextSince.Value).TotalSeconds > 1.5)
                Exit();
            DrawHints();
            return;
        }

        seenText = true;
        noTextSince = null;
        Notice = null;
        var current = FindCurrent(stops);

        // ------------------------------------------------------- navigation
        var moved = false;
        if (Repeat("left", Has(raw, GamepadButtons.DpadLeft), now)) { current = CursorMoves.Horizontal(stops, current, -1); moved = true; }
        if (Repeat("right", Has(raw, GamepadButtons.DpadRight), now)) { current = CursorMoves.Horizontal(stops, current, 1); moved = true; }
        if (Repeat("up", Has(raw, GamepadButtons.DpadUp), now)) { current = CursorMoves.Vertical(stops, current, -1); moved = true; }
        if (Repeat("down", Has(raw, GamepadButtons.DpadDown), now)) { current = CursorMoves.Vertical(stops, current, 1); moved = true; }

        // Virtual cursor: Left analog stick (default), optional DualSense/DS4 touchpad, or mouse gliding.
        if (!moved)
        {
            var vp = ImGui.GetMainViewport();
            var stick = Normalize(Plugin.Gamepad.LeftStick);
            var stickMag = stick.Length();
            var padDelta = config.ControllerUseTouchpad ? touchpad.ConsumeDelta(out _) : Vector2.Zero;
            if (stickMag > 0.18f)
            {
                if (virtualPointer.X < vp.Pos.X - 100)
                    virtualPointer = stops[Math.Clamp(current, 0, stops.Count - 1)].Center;
                var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0.001f, 0.05f);
                var norm = (stickMag - 0.18f) / 0.82f;
                var speed = Math.Clamp(config.ControllerCursorSpeed, 200f, 2500f) * (norm * norm);
                var dir = stick / stickMag;
                virtualPointer = new Vector2(
                    Math.Clamp(virtualPointer.X + dir.X * speed * dt, vp.Pos.X, vp.Pos.X + vp.Size.X),
                    Math.Clamp(virtualPointer.Y - dir.Y * speed * dt, vp.Pos.Y, vp.Pos.Y + vp.Size.Y));
                lastPointerMoved = now;
                if (FindStopAtPointer(stops, virtualPointer) is { } stickHit)
                {
                    current = stickHit;
                    moved = true;
                }
            }
            else if (padDelta.LengthSquared() > 0.25f)
            {
                if (virtualPointer.X < vp.Pos.X - 100)
                    virtualPointer = stops[Math.Clamp(current, 0, stops.Count - 1)].Center;
                var scale = vp.Size.X / 1200f;
                virtualPointer = new Vector2(
                    Math.Clamp(virtualPointer.X + padDelta.X * scale, vp.Pos.X, vp.Pos.X + vp.Size.X),
                    Math.Clamp(virtualPointer.Y + padDelta.Y * scale, vp.Pos.Y, vp.Pos.Y + vp.Size.Y));
                lastPointerMoved = now;
                if (FindStopAtPointer(stops, virtualPointer) is { } padHit)
                {
                    current = padHit;
                    moved = true;
                }
            }
            else if (ImGui.IsMousePosValid())
            {
                var mouse = ImGui.GetMousePos();
                if (Vector2.DistanceSquared(mouse, lastPointer) > 2f)
                {
                    lastPointer = mouse;
                    virtualPointer = mouse;
                    lastPointerMoved = now;
                    var insidePopup = level == 0 && popups.ContainsMouse(mouse);
                    if (!insidePopup && FindStopAtPointer(stops, mouse) is { } hitStop)
                    {
                        current = hitStop;
                        moved = true;
                    }
                }
            }
        }
        else if (virtualPointer.X > -5000)
        {
            // Keep the virtual pointer synced to the word after D-pad moves so the next glide starts here.
            virtualPointer = stops[Math.Clamp(current, 0, stops.Count - 1)].Center;
        }
        if (moved)
            popupClosedByBack = false;
        if (HasBound(pressed, config.ControllerWordModeButton))
        {
            wordMode = !wordMode;
            stops = Stops(level, out contexts);
            current = FindCurrent(stops);
        }

        var stop = stops[Math.Clamp(current, 0, stops.Count - 1)];
        cursor[level] = (StopBlock(stop, level), stop.Index);

        // ------------------------------------------------------ popup actions
        var deepest = popups.VisibleCount - 1;
        if (HasBound(pressed, config.ControllerDiveButton) && popups.VisibleCount > level && popups.ScanBlocks(level).Count > 0)
        {
            level++;
            while (cursor.Count <= level)
                cursor.Add((null, -1));
            cursor[level] = (null, -1);
            return;
        }

        if (HasBound(pressed, config.ControllerBackExitButton))
        {
            if (level > 0)
            {
                popups.CloseFrom(level);
                level--;
            }
            else
            {
                Exit();
                return;
            }
        }
        else if (HasBound(pressed, config.ControllerWindowButton))
        {
            if (level > 0)
            {
                popups.CloseFrom(level);
                level--;
            }
            else
            {
                var nextWindow = NextAddonStop(stops, current);
                if (nextWindow != current)
                {
                    current = nextWindow;
                    stop = stops[current];
                    cursor[level] = (StopBlock(stop, level), stop.Index);
                    virtualPointer = stop.Center;
                    popupClosedByBack = false;
                }
            }
        }

        if (deepest >= 0)
        {
            if (HasBound(pressed, config.ControllerMineButton)) popups.MineSelected(deepest);
            if (HasBound(pressed, config.ControllerAudioButton)) popups.PlaySelected(deepest);
            if (HasBound(pressed, config.ControllerNextEntryButton)) popups.SelectEntry(deepest, 1);
            if (HasBound(pressed, config.ControllerPrevEntryButton)) popups.SelectEntry(deepest, -1);
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
            popups.Report(new TextHit(block, stop.Index, stop.Min, stop.Max), level, contexts[stop.Target], forced: true, bypassTokenize: !wordMode);
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
        if ((now - lastPointerMoved).TotalSeconds < 2.0 && virtualPointer.X > -5000)
            DrawPointer(virtualPointer);
        DrawHints();
    }

    // ------------------------------------------------------------------ stops

    private static readonly HashSet<string> HudAddons = new(StringComparer.Ordinal)
    {
        "ScenarioTree", "_ToDoList", "_NaviMap", "_Exp", "_ParameterWidget", "_MainCommand",
        "_Money", "_BagWidget", "_Status", "_StatusCustom0", "_StatusCustom1", "_StatusCustom2",
        "_StatusCustom3", "ServerInfo", "MiniMap", "_LocationTitle", "_ActionBar", "_ActionCross",
        "_ActionDoubleCrossL", "_ActionDoubleCrossR", "ChatLog", "ChatLogPanel_0", "ChatLogPanel_1",
        "ChatLogPanel_2", "ChatLogPanel_3",
    };

    private readonly List<RichBlock> stopBlocks = new();
    private readonly List<string> stopAddons = new();
    private readonly List<bool> stopBodies = new();

    private RichBlock? StopBlock(CursorStop stop, int forLevel) => stop.Target < stopBlocks.Count ? stopBlocks[stop.Target] : null;
    private string StopAddon(CursorStop stop) => stop.Target < stopAddons.Count ? stopAddons[stop.Target] : string.Empty;

    private int NextAddonStop(List<CursorStop> stops, int current)
    {
        if (stops.Count == 0) return current;
        var curAddon = StopAddon(stops[Math.Clamp(current, 0, stops.Count - 1)]);
        var order = new List<(string Addon, int FirstIndex)>();
        var seenAddons = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < stops.Count; i++)
        {
            var addon = StopAddon(stops[i]);
            if (seenAddons.Add(addon))
                order.Add((addon, i));
        }
        if (order.Count <= 1) return current;
        var idx = order.FindIndex(o => o.Addon == curAddon);
        var next = order[(idx + 1) % order.Count];
        return FirstStopIn(stops, next.Addon) ?? next.FirstIndex;
    }

    /// <summary>Where the cursor may stop at a level: word starts (or characters) of its text, in reading order.</summary>
    private List<CursorStop> Stops(int atLevel, out List<Func<RichBlock, int, ScanContext>?> contexts)
    {
        stopBlocks.Clear();
        stopAddons.Clear();
        stopBodies.Clear();
        contexts = new List<Func<RichBlock, int, ScanContext>?>();
        var blocks = new List<(string Addon, RichBlock Block, IReadOnlyList<(int Index, Vector2 Min, Vector2 Max)> Cells, Func<RichBlock, int, ScanContext>? Context, bool Body)>();
        if (atLevel == 0)
        {
            foreach (var target in nativeHover.Targets().OrderBy(t => t.Cells.Min(c => c.Min.Y)).ThenBy(t => t.Cells.Min(c => c.Min.X)))
                blocks.Add((target.Addon, target.Block, target.Cells, target.Context, target.Body));
        }
        else
        {
            foreach (var block in popups.ScanBlocks(atLevel - 1))
            {
                var cells = RichText.GlyphRects(block);
                if (cells.Count > 0)
                    blocks.Add((string.Empty, block, cells, null, false));
            }
        }

        var stops = new List<CursorStop>();
        foreach (var (addon, block, cells, context, body) in blocks)
        {
            var target = stopBlocks.Count;
            stopBlocks.Add(block);
            stopAddons.Add(addon);
            stopBodies.Add(body);
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

        // Fresh entry at level 0: prefer the focused non-HUD window (e.g. JournalAccept, SelectString),
        // else any non-HUD window, rather than jumping straight to top-left HUD (ScenarioTree / _ToDoList).
        if (level == 0)
        {
            var focused = nativeHover.FocusedAddonName;
            if (!string.IsNullOrEmpty(focused) && !HudAddons.Contains(focused) && FirstStopIn(stops, focused) is { } inFocused)
                return inFocused;
            for (var i = 0; i < stops.Count; i++)
            {
                var addon = StopAddon(stops[i]);
                if (!string.IsNullOrEmpty(addon) && !HudAddons.Contains(addon))
                    return FirstStopIn(stops, addon) ?? i;
            }
        }

        return 0;
    }

    /// <summary>
    /// The first stop of a window, starting at its main text when it has one: in a Talk
    /// window that is the dialogue, not the speaker's name drawn above it.
    /// </summary>
    private int? FirstStopIn(List<CursorStop> stops, string addon)
    {
        int? first = null;
        for (var i = 0; i < stops.Count; i++)
        {
            if (StopAddon(stops[i]) != addon)
                continue;
            if (stops[i].Target < stopBodies.Count && stopBodies[stops[i].Target])
                return i;
            first ??= i;
        }

        return first;
    }

    /// <summary>The stop under (or snapped to) the touchpad/mouse pointer, or null if far from any text.</summary>
    private static int? FindStopAtPointer(List<CursorStop> stops, Vector2 pointer)
    {
        // 1. Exact cell hit.
        for (var i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            if (pointer.X >= s.Min.X && pointer.X <= s.Max.X && pointer.Y >= s.Min.Y && pointer.Y <= s.Max.Y)
                return i;
        }

        // 2. Same visual line: pick the word start at or just to the left of the pointer (for multi-char words).
        var bestLine = -1;
        var bestDx = float.MaxValue;
        for (var i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            var padY = MathF.Max(6f, s.Height * 0.4f);
            if (pointer.Y < s.Min.Y - padY || pointer.Y > s.Max.Y + padY)
                continue;
            var dx = pointer.X >= s.Min.X ? pointer.X - s.Min.X : (s.Min.X - pointer.X) + 64f;
            if (dx < 240f && dx < bestDx)
            {
                bestDx = dx;
                bestLine = i;
            }
        }
        if (bestLine >= 0)
            return bestLine;

        // 3. Nearby fallback within 40 px.
        var nearest = -1;
        var nearestDistSq = 40f * 40f;
        for (var i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            var cx = Math.Clamp(pointer.X, s.Min.X, s.Max.X);
            var cy = Math.Clamp(pointer.Y, s.Min.Y, s.Max.Y);
            var dSq = (pointer.X - cx) * (pointer.X - cx) + (pointer.Y - cy) * (pointer.Y - cy);
            if (dSq < nearestDistSq)
            {
                nearestDistSq = dSq;
                nearest = i;
            }
        }
        return nearest >= 0 ? nearest : null;
    }

    // ------------------------------------------------------------------ input

    private static bool Has(GamepadButtons mask, GamepadButtons button) => (mask & button) == button;
    private static bool HasBound(GamepadButtons mask, GamepadButtons button) => button != GamepadButtons.None && (mask & button) == button;

    internal static string ButtonLabel(GamepadButtons button, bool ps) => button switch
    {
        GamepadButtons.None => "None",
        GamepadButtons.South => ps ? "X" : "A",
        GamepadButtons.East => ps ? "Circle" : "B",
        GamepadButtons.West => ps ? "Square" : "X",
        GamepadButtons.North => ps ? "Triangle" : "Y",
        GamepadButtons.L1 => ps ? "L1" : "LB",
        GamepadButtons.R1 => ps ? "R1" : "RB",
        GamepadButtons.L2 => ps ? "L2" : "LT",
        GamepadButtons.R2 => ps ? "R2" : "RT",
        GamepadButtons.L3 => ps ? "L3" : "LS",
        GamepadButtons.R3 => ps ? "R3" : "RS",
        GamepadButtons.Select => ps ? "Select" : "Back",
        GamepadButtons.Start => ps ? "Options" : "Start",
        _ => button.ToString(),
    };

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

    private static void DrawPointer(Vector2 pos)
    {
        var draw = ImGui.GetForegroundDrawList();
        var accent = Rendering.Css.CssColor.ToAbgr(Rendering.Css.PopupTheme.Current.Accent with { W = 1 });
        draw.AddCircleFilled(pos, 5f, 0xCC101010, 16);
        draw.AddCircle(pos, 5f, accent, 16, 2f);
    }

    private void DrawHints()
    {
        if (!config.ControllerShowHints)
            return;
        var ps = config.ControllerPlayStationLabels;
        string L(GamepadButtons b) => ButtonLabel(b, ps);
        var parts = new List<string>
        {
            "LS cursor",
            "D-Pad " + (wordMode ? "word" : "char"),
        };
        if (popups.VisibleCount > 0)
        {
            if (config.ControllerMineButton != GamepadButtons.None) parts.Add($"{L(config.ControllerMineButton)} mine");
            if (config.ControllerAudioButton != GamepadButtons.None) parts.Add($"{L(config.ControllerAudioButton)} audio");
            if (config.ControllerPrevEntryButton != GamepadButtons.None || config.ControllerNextEntryButton != GamepadButtons.None)
                parts.Add($"{L(config.ControllerPrevEntryButton)}/{L(config.ControllerNextEntryButton)} entry");
            if (popups.VisibleCount > level && config.ControllerDiveButton != GamepadButtons.None)
                parts.Add($"{L(config.ControllerDiveButton)} inside");
        }

        if (level == 0 && config.ControllerWindowButton != GamepadButtons.None && stopAddons.Distinct(StringComparer.Ordinal).Skip(1).Any())
            parts.Add($"{L(config.ControllerWindowButton)} window");
        if (config.ControllerWordModeButton != GamepadButtons.None)
            parts.Add($"{L(config.ControllerWordModeButton)} {(wordMode ? "chars" : "words")}");
        if (config.ControllerBackExitButton != GamepadButtons.None)
            parts.Add($"{L(config.ControllerBackExitButton)} {(level > 0 ? "back" : "exit")}");
        parts.Add($"{(ps ? "Select" : "Back")} exit");
        var text = (Notice is null ? string.Empty : Notice + "   ") + string.Join("   ", parts)
                   + (BlockingRefused ? "   WARNING: Dalamud is not blocking game input: enable plugin gamepad input in Dalamud's settings" : string.Empty);

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
