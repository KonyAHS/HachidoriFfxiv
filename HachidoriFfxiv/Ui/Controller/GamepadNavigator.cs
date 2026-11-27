using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.UI;
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
    private readonly TextCapture capture;
    private readonly DialogueBacklog backlog;

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private GamepadButtons previous;
    private readonly ControllerTriggerState triggerState = new();
    private readonly ControllerTriggerState quickReplayTriggerState = new();
    private bool toggleKeyWasDown;
    private bool replayKeyWasDown;
    private readonly Dictionary<string, DateTime> repeats = new();
    private DateTime? noTextSince;
    private bool seenText;
    private DateTime lastScroll = DateTime.MinValue;
    private readonly ControllerScanState scanState = new();
    private Vector2 lastPointer = new(-10000);
    private Vector2 virtualPointer = new(-10000);
    private DateTime lastPointerMoved = DateTime.MinValue;
    private readonly TouchpadReader touchpad = new();

    private bool pendingAutoBacklogCheck;
    private long trackedLiveLineId;
    private int trackedLiveCharIndex;

    // Cursor per level: 0 is the game's text (or backlog), n is the definitions of popup n-1.
    private readonly List<(RichBlock? Block, int Index)> cursor = new() { (null, -1) };
    private int level;
    private bool wordMode = true;

    public GamepadNavigator(
        Configuration config,
        PopupManager popups,
        NativeTextHover nativeHover,
        HachidoriService service,
        TextCapture capture,
        DialogueBacklog backlog)
    {
        this.config = config;
        this.popups = popups;
        this.nativeHover = nativeHover;
        this.service = service;
        this.capture = capture;
        this.backlog = backlog;
        wordMode = config.ControllerStartInWordMode;
    }

    public bool Active { get; private set; }

    /// <summary>Set by ConfigWindow while recording a keybind so the bound press does not also trigger it.</summary>
    public bool SuppressToggleKey { get; set; }

    /// <summary>Prevents the just-recorded toggle key press from triggering controller mode while still held.</summary>
    public void NotifyToggleKeyBound() => toggleKeyWasDown = true;

    /// <summary>Prevents the just-recorded replay key press from triggering replay while still held.</summary>
    public void NotifyReplayKeyBound() => replayKeyWasDown = true;

    /// <summary>Repeats the cached voice line for the selected backlog entry, active live dialogue, or latest dialogue line.</summary>
    public void ReplayCurrentOrLatestLine()
    {
        var targetId = ControllerBacklogState.ResolveReplayTargetLineId(
            backlog.Open,
            backlog.SelectedLine?.Id,
            Active ? trackedLiveLineId : 0,
            backlog.LatestDialogueLineId);
        backlog.ReplayVoice(targetId);
    }

    /// <summary>Dalamud ignores input blocking when "plugin gamepad input" is off in its settings.</summary>
    public bool BlockingRefused { get; private set; }

    public string? Notice { get; private set; }

    public void Toggle()
    {
        if (Active) Exit();
        else Enter();
    }

    /// <summary>Opens (or toggles) the dialogue backlog, entering controller mode when enabled.</summary>
    public void ToggleBacklog()
    {
        if (Active)
        {
            if (backlog.Open)
            {
                CloseBacklogToLive();
            }
            else if (backlog.OpenAtOffset(IsLiveDialogueVisible() && backlog.LineCount >= 2 ? 1 : 0))
            {
                ResetForBacklogSwitch();
            }
            return;
        }

        if (config.ControllerEnabled)
        {
            Enter(openBacklog: true);
            return;
        }

        if (backlog.Open)
            backlog.Close();
        else
            backlog.OpenAtOffset(0, standalone: true);
    }

    public void Enter(bool openBacklog = false)
    {
        if (!config.ControllerEnabled || Active)
            return;
        Active = true;
        level = 0;
        cursor.Clear();
        cursor.Add((null, -1));
        noTextSince = null;
        seenText = false;
        trackedLiveLineId = 0;
        trackedLiveCharIndex = 0;
        pendingAutoBacklogCheck = !openBacklog && config.ControllerAutoOpenBacklog;
        scanState.Reset();
        nativeHover.ControllerScan = true;
        popups.ControllerActive = true;
        if (!config.ControllerAutoScan)
            popups.CloseAll();
        lastPointer = ImGui.IsMousePosValid() ? ImGui.GetMousePos() : new Vector2(-10000);
        virtualPointer = new Vector2(-10000);
        lastPointerMoved = DateTime.MinValue;
        if (config.ControllerUseTouchpad)
            touchpad.Start();
        wordMode = config.ControllerStartInWordMode;
        if (openBacklog)
        {
            var offset = IsLiveDialogueVisible() && backlog.LineCount >= 2 ? 1 : 0;
            backlog.OpenAtOffset(offset);
        }
        // Buttons held while entering must not act until released.
        previous = ReadButtons();
        triggerState.RequireReleaseIfDown(IsTriggerDown(previous));
        quickReplayTriggerState.RequireReleaseIfDown(IsQuickReplayDown(previous));
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
        backlog.Close();
        pendingAutoBacklogCheck = false;
        trackedLiveLineId = 0;
        trackedLiveCharIndex = 0;
        touchpad.Stop();
        DalamudServices.Gamepad.EnableGamepadNav = false;
        nativeHover.ControllerScan = false;
        popups.SetWebControllerCursor(null);
        popups.ControllerActive = false;
        popups.CloseAll();
        scanState.Reset();
        repeats.Clear();
        var currentButtons = ReadButtons();
        triggerState.RequireReleaseIfDown(IsTriggerDown(currentButtons));
        quickReplayTriggerState.RequireReleaseIfDown(IsQuickReplayDown(currentButtons));
    }

    private bool IsTriggerDown(GamepadButtons raw)
    {
        var trigger = config.ControllerHoldButton;
        return trigger != GamepadButtons.None && (raw & trigger) == trigger;
    }

    private bool IsQuickReplayDown(GamepadButtons raw)
    {
        var button = config.ControllerQuickReplayButton;
        return button != GamepadButtons.None && (raw & button) == button;
    }

    private static readonly GamepadButtons[] AllButtons = Enum.GetValues<GamepadButtons>().Where(b => b != GamepadButtons.None).ToArray();

    private static GamepadButtons ReadButtons()
    {
        var mask = GamepadButtons.None;
        foreach (var button in AllButtons)
        {
            if (DalamudServices.Gamepad.Raw(button) > 0)
                mask |= button;
        }

        return mask;
    }

    /// <summary>Call every frame after native hover and before the popups draw.</summary>
    public void Draw()
    {
        var now = DateTime.UtcNow;
        var raw = ReadButtons();
        var pressed = raw & ~previous;
        previous = raw;

        if (PollReplayKey())
            ReplayCurrentOrLatestLine();

        if (!config.ControllerEnabled)
        {
            Exit();
            PollOutsideQuickReplay(raw, now);
            if (backlog.Open)
                backlog.Draw(controllerActive: false);
            else
                backlog.DrawReplayNoticeOverlay();
            return;
        }

        var trigger = config.ControllerHoldButton;
        var triggerDown = trigger != GamepadButtons.None && (raw & trigger) == trigger;

        if (PollToggleKey())
        {
            Toggle();
            return;
        }

        var triggerFired = triggerState.Update(triggerDown, config.ControllerTriggerMode, config.ControllerHoldSeconds, now);

        if (!Active)
        {
            if (triggerFired)
            {
                Enter();
                return;
            }
            PollOutsideQuickReplay(raw, now);
            if (backlog.Open)
                backlog.Draw(controllerActive: false);
            else
                backlog.DrawReplayNoticeOverlay();
            return;
        }

        quickReplayTriggerState.RequireReleaseIfDown(IsQuickReplayDown(raw));

        if (triggerState.WaitingForRelease && triggerDown)
            pressed &= ~trigger;

        if (unfocus)
        {
            ImGui.SetWindowFocus((string?)null);
            unfocus = false;
        }

        // Take the controller from the game while the mode is on.
        DalamudServices.Gamepad.EnableGamepadNav = true;
        BlockingRefused = !DalamudServices.Gamepad.EnableGamepadNav;
        nativeHover.ControllerScan = true;
        popups.ControllerActive = true;

        // Keep touchpad reader synced with the setting if toggled live.
        if (config.ControllerUseTouchpad && !touchpad.Active) touchpad.Start();
        else if (!config.ControllerUseTouchpad && touchpad.Active) touchpad.Stop();

        if (triggerFired || Has(pressed, GamepadButtons.Select) || Has(pressed, GamepadButtons.Start))
        {
            Exit();
            return;
        }

        // Auto-open the backlog when entering controller mode with no live dialogue or non-HUD UI window
        // (e.g. a cutscene just finished or BattleTalk faded away moments ago).
        if (pendingAutoBacklogCheck && !backlog.Open && nativeHover.HasControllerScanSnapshot)
        {
            pendingAutoBacklogCheck = false;
            var targets = nativeHover.Targets();
            var hasLiveDialogue = IsLiveDialogueVisible() || targets.Any(t => IsDialogueAddon(t.Addon));
            var hasNonHudWindow = targets.Any(t => !string.IsNullOrEmpty(t.Addon) && !HudAddons.Contains(t.Addon) && !IsDialogueAddon(t.Addon));
            if (ControllerBacklogState.ShouldAutoOpenOnEnter(config.ControllerAutoOpenBacklog, hasLiveDialogue, hasNonHudWindow, backlog.LineCount))
            {
                if (backlog.OpenAtOffset(0, autoOpened: true))
                    ResetForBacklogSwitch();
            }
        }

        // Auto-advance pinning: if FFXIV advances or closes the dialogue line while the user is actively
        // scanning it in controller mode, seamlessly pin that exact line in the backlog instead of losing focus.
        if (!backlog.Open && ControllerBacklogState.ShouldPinOnLiveAdvance(
                config.ControllerPinOnAdvance,
                trackedLiveLineId,
                backlog.LatestDialogueLineId,
                IsLiveDialogueVisible()))
        {
            var pinLineId = trackedLiveLineId;
            var pinIndex = trackedLiveCharIndex;
            trackedLiveLineId = 0;
            if (backlog.OpenPinned(pinLineId))
            {
                level = 0;
                cursor.Clear();
                cursor.Add((backlog.SelectedLine?.Block, pinIndex));
            }
        }

        // Toggle backlog drawer (default L3).
        if (HasBound(pressed, config.ControllerHistoryButton))
        {
            pendingAutoBacklogCheck = false;
            if (backlog.Open)
            {
                CloseBacklogToLive();
            }
            else if (backlog.OpenAtOffset(IsLiveDialogueVisible() && backlog.LineCount >= 2 ? 1 : 0))
            {
                ResetForBacklogSwitch();
            }
            else
            {
                Notice = "No dialogue history yet";
            }
        }

        // Replay voice line for the selected backlog entry, active live dialogue, or latest dialogue line (default R3).
        if (HasBound(pressed, config.ControllerReplayVoiceButton))
            ReplayCurrentOrLatestLine();

        // Render the backlog drawer before reading stops so glyph rects are fresh on this exact frame.
        if (backlog.Open)
            backlog.Draw(controllerActive: true);
        else
            backlog.DrawReplayNoticeOverlay();

        // A popup closed by the mouse or by time: fall back to a level that still exists.
        var prevLevel = level;
        level = Math.Min(level, popups.VisibleCount);
        while (cursor.Count <= level)
            cursor.Add((null, -1));
        if (level < prevLevel)
        {
            var (parentBlock, parentIndex) = cursor[level];
            scanState.RestoreParent(parentBlock, parentIndex, popups.VisibleCount > level);
        }
        if (!config.ControllerAutoScan)
            scanState.SyncVisibility(popups.VisibleCount > level);

        var stops = Stops(level, out var contexts);
        if (stops.Count == 0)
        {
            if (level > 0)
            {
                level--;
                var (parentBlock, parentIndex) = cursor[level];
                scanState.RestoreParent(parentBlock, parentIndex, popups.VisibleCount > level);
                popups.Sustain(level);
                return;
            }

            if (!config.ControllerAutoScan && scanState.Active)
            {
                scanState.Reset();
                popups.CloseAll();
            }

            if (HasBound(pressed, config.ControllerBackExitButton))
            {
                Exit();
                return;
            }

            // While the backlog drawer is scrolling a newly selected line into view (1 frame),
            // wait without timing out or showing a "Waiting for text" notice.
            if (backlog.Open)
            {
                seenText = true;
                noTextSince = null;
                DrawHints();
                return;
            }

            noTextSince ??= now;
            Notice = "Waiting for text...";
            popups.SetWebControllerCursor(null);
            if (seenText && config.ControllerExitWhenDialogueCloses && (now - noTextSince.Value).TotalSeconds > 1.5)
                Exit();
            DrawHints();
            return;
        }

        seenText = true;
        noTextSince = null;
        Notice = null;
        var current = FindCurrent(stops);
        if (level == 0 && backlog.Open && backlog.State.ConsumePendingSnap(stops) is { } snapped)
            current = snapped;

        // ------------------------------------------------------- navigation
        var moved = false;
        if (Repeat("left", Has(raw, GamepadButtons.DpadLeft), now)) { current = CursorMoves.Horizontal(stops, current, -1); moved = true; }
        if (Repeat("right", Has(raw, GamepadButtons.DpadRight), now)) { current = CursorMoves.Horizontal(stops, current, 1); moved = true; }
        if (Repeat("up", Has(raw, GamepadButtons.DpadUp), now))
        {
            var nextUp = CursorMoves.Vertical(stops, current, -1);
            if (nextUp != current)
            {
                current = nextUp;
                moved = true;
            }
            else if (level == 0 && backlog.Open)
            {
                if (backlog.StepLine(-1, stops[current].Center.X))
                {
                    OnBacklogLineChanged();
                    DrawHints();
                    return;
                }
            }
            else if (level == 0 && !backlog.Open && IsDialogueAddon(StopAddon(stops[current])) && backlog.LineCount >= 2)
            {
                // Pushing Up past the top row of live dialogue rewinds straight into the previous line (-1).
                if (backlog.OpenAtOffset(1, snap: BacklogSnapEdge.BottomNearestX, snapX: stops[current].Center.X))
                {
                    ResetForBacklogSwitch();
                    DrawHints();
                    return;
                }
            }
        }
        if (Repeat("down", Has(raw, GamepadButtons.DpadDown), now))
        {
            var nextDown = CursorMoves.Vertical(stops, current, 1);
            if (nextDown != current)
            {
                current = nextDown;
                moved = true;
            }
            else if (level == 0 && backlog.Open)
            {
                if (backlog.StepLine(1, stops[current].Center.X))
                {
                    OnBacklogLineChanged();
                    DrawHints();
                    return;
                }
            }
        }

        // Virtual cursor: Left analog stick (default), optional DualSense/DS4 touchpad, or mouse gliding.
        if (!moved)
        {
            var vp = ImGui.GetMainViewport();
            var stick = Normalize(DalamudServices.Gamepad.LeftStick);
            var stickMag = stick.Length();
            var padDelta = config.ControllerUseTouchpad ? touchpad.ConsumeDelta() : Vector2.Zero;
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
                if (level == 0 && backlog.Open && backlog.TrySelectLineAt(virtualPointer))
                {
                    stops = Stops(0, out contexts);
                    if (stops.Count > 0)
                        current = FindStopAtPointer(stops, virtualPointer) ?? 0;
                    moved = true;
                }
                else if (FindStopAtPointer(stops, virtualPointer) is { } stickHit)
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
                if (level == 0 && backlog.Open && backlog.TrySelectLineAt(virtualPointer))
                {
                    stops = Stops(0, out contexts);
                    if (stops.Count > 0)
                        current = FindStopAtPointer(stops, virtualPointer) ?? 0;
                    moved = true;
                }
                else if (FindStopAtPointer(stops, virtualPointer) is { } padHit)
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
                    if (!insidePopup && level == 0 && backlog.Open && backlog.TrySelectLineAt(mouse))
                    {
                        stops = Stops(0, out contexts);
                        if (stops.Count > 0)
                            current = FindStopAtPointer(stops, mouse) ?? 0;
                        moved = true;
                    }
                    else if (!insidePopup && FindStopAtPointer(stops, mouse) is { } hitStop)
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
        var stop = stops[Math.Clamp(current, 0, stops.Count - 1)];
        var block = StopBlock(stop);
        if (!config.ControllerAutoScan && scanState.OnStopChanged(block, stop.Index))
            popups.CloseFrom(level);

        if (level == 0 && !backlog.Open)
        {
            if (IsDialogueAddon(StopAddon(stop)) && backlog.LatestDialogueLineId is { } liveId)
            {
                trackedLiveLineId = liveId;
                trackedLiveCharIndex = stop.Index;
            }
            else
            {
                trackedLiveLineId = 0;
            }
        }

        if (!config.ControllerAutoScan && HasBound(pressed, config.ControllerScanButton))
        {
            var action = scanState.HandleScanButton(block, stop.Index, popups.VisibleCount > level, ScanButtonSharedWithPopupAction());
            if (action == ControllerScanPress.Open)
            {
                pressed &= ~config.ControllerScanButton;
            }
            else if (action == ControllerScanPress.Close)
            {
                popups.CloseFrom(level);
                pressed &= ~config.ControllerScanButton;
            }
        }

        if (HasBound(pressed, config.ControllerWordModeButton))
        {
            wordMode = !wordMode;
            stops = Stops(level, out contexts);
            if (stops.Count == 0)
            {
                if (level > 0) popups.Sustain(level - 1);
                DrawHints();
                return;
            }
            current = FindCurrent(stops);
            stop = stops[Math.Clamp(current, 0, stops.Count - 1)];
            block = StopBlock(stop);
            if (!config.ControllerAutoScan)
                scanState.AlignAfterModeToggle(block, stop.Index);
        }

        cursor[level] = (block, stop.Index);

        // ------------------------------------------------------ popup actions
        if (HasBound(pressed, config.ControllerDiveButton) && popups.VisibleCount > level && popups.HasScanTargets(level))
        {
            level++;
            while (cursor.Count <= level)
                cursor.Add((null, -1));
            cursor[level] = (null, -1);
            scanState.Reset();
            popups.Sustain(level - 1);
            DrawHints();
            return;
        }

        if (HasBound(pressed, config.ControllerBackExitButton))
        {
            if (!config.ControllerAutoScan && popups.VisibleCount > level)
            {
                scanState.Reset();
                popups.CloseFrom(level);
            }
            else if (level > 0)
            {
                popups.CloseFrom(level);
                level--;
                var (parentBlock, parentIndex) = cursor[level];
                scanState.RestoreParent(parentBlock, parentIndex, popups.VisibleCount > level);
                popups.Sustain(level);
                DrawHints();
                return;
            }
            else if (backlog.Open && !backlog.State.AutoOpened && HasLiveNonHudTarget())
            {
                CloseBacklogToLive();
                DrawHints();
                return;
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
                var (parentBlock, parentIndex) = cursor[level];
                scanState.RestoreParent(parentBlock, parentIndex, popups.VisibleCount > level);
                popups.Sustain(level);
                DrawHints();
                return;
            }
            else if (backlog.Open)
            {
                if (nativeHover.Targets().Count > 0)
                {
                    CloseBacklogToLive();
                    DrawHints();
                    return;
                }
            }
            else
            {
                var nextWindow = NextAddonStop(stops, current);
                if (nextWindow != current)
                {
                    current = nextWindow;
                    stop = stops[current];
                    block = StopBlock(stop);
                    cursor[level] = (block, stop.Index);
                    virtualPointer = stop.Center;
                    if (!config.ControllerAutoScan && scanState.OnStopChanged(block, stop.Index))
                        popups.CloseFrom(level);
                }
            }
        }

        var deepest = popups.VisibleCount - 1;
        var right = Normalize(DalamudServices.Gamepad.RightStick);
        if (deepest >= 0)
        {
            if (HasBound(pressed, config.ControllerMineButton)) popups.MineSelected(deepest);
            if (HasBound(pressed, config.ControllerAudioButton)) popups.PlaySelected(deepest);
            if (HasBound(pressed, config.ControllerNextEntryButton)) popups.SelectEntry(deepest, 1);
            if (HasBound(pressed, config.ControllerPrevEntryButton)) popups.SelectEntry(deepest, -1);
            if (Math.Abs(right.Y) > 0.45f && (now - lastScroll).TotalSeconds > RepeatRate)
            {
                popups.Scroll(deepest, -Math.Sign(right.Y) * 110 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
                lastScroll = now;
            }
        }
        else if (backlog.Open && Math.Abs(right.Y) > 0.45f && (now - lastScroll).TotalSeconds > RepeatRate)
        {
            backlog.ScrollList(-Math.Sign(right.Y) * 90 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
            lastScroll = now;
        }

        // ------------------------------------------------- look up at the cursor
        if (level > 0)
            popups.Sustain(level - 1);
        if (block is not null && (config.ControllerAutoScan || scanState.Active))
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
    private readonly List<IReadOnlyList<(int Index, Vector2 Min, Vector2 Max)>> stopCells = new();

    private RichBlock? StopBlock(CursorStop stop) => stop.Target < stopBlocks.Count ? stopBlocks[stop.Target] : null;
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

    private bool IsLiveDialogueVisible() =>
        capture.TalkVisible || capture.BattleTalkVisible || nativeHover.SubtitleVisible;

    private bool IsCutsceneOrDialogueActive()
    {
        if (IsLiveDialogueVisible())
            return true;
        try
        {
            var cond = DalamudServices.Condition;
            return cond is not null && (
                cond[ConditionFlag.OccupiedInCutSceneEvent]
                || cond[ConditionFlag.WatchingCutscene]
                || cond[ConditionFlag.WatchingCutscene78]
                || cond[ConditionFlag.OccupiedInQuestEvent]);
        }
        catch
        {
            return false;
        }
    }

    private void PollOutsideQuickReplay(GamepadButtons raw, DateTime now)
    {
        var allowed = ControllerBacklogState.ShouldFireGamepadQuickReplay(
            config.ControllerQuickReplayButton != GamepadButtons.None,
            config.ControllerQuickReplayCutsceneOnly,
            IsCutsceneOrDialogueActive(),
            backlog.Open);
        var fired = quickReplayTriggerState.Update(
            allowed && IsQuickReplayDown(raw),
            config.ControllerQuickReplayTriggerMode,
            config.ControllerHoldSeconds,
            now);
        if (fired)
            ReplayCurrentOrLatestLine();
    }

    private static bool IsDialogueAddon(string addon) =>
        addon is "Talk" or "TalkSubtitle" or "_BattleTalk";

    private bool HasLiveNonHudTarget() =>
        IsLiveDialogueVisible()
        || nativeHover.Targets().Any(t => !string.IsNullOrEmpty(t.Addon) && !HudAddons.Contains(t.Addon));

    private void ResetForBacklogSwitch()
    {
        level = 0;
        cursor.Clear();
        cursor.Add((null, -1));
        trackedLiveLineId = 0;
        scanState.Reset();
        popups.CloseAll();
    }

    private void OnBacklogLineChanged()
    {
        level = 0;
        cursor.Clear();
        cursor.Add((null, -1));
        if (!config.ControllerAutoScan)
        {
            scanState.Reset();
            popups.CloseAll();
        }
    }

    private void CloseBacklogToLive()
    {
        backlog.Close();
        ResetForBacklogSwitch();
    }

    /// <summary>Where the cursor may stop at a level: word starts (or characters) of its text, in reading order.</summary>
    private List<CursorStop> Stops(int atLevel, out List<Func<RichBlock, int, ScanContext>?> contexts)
    {
        stopBlocks.Clear();
        stopAddons.Clear();
        stopBodies.Clear();
        stopCells.Clear();
        contexts = new List<Func<RichBlock, int, ScanContext>?>();
        var blocks = new List<(string Addon, RichBlock Block, IReadOnlyList<(int Index, Vector2 Min, Vector2 Max)> Cells, Func<RichBlock, int, ScanContext>? Context, bool Body)>();
        if (atLevel == 0)
        {
            if (backlog.Open)
            {
                if (backlog.SelectedTarget() is { } selected)
                {
                    var line = selected.Line;
                    blocks.Add(("Backlog", line.Block, selected.ClippedCells, (_, idx) => backlog.ContextFor(line, idx), true));
                }
            }
            else
            {
                foreach (var target in nativeHover.Targets().OrderBy(t => t.Cells.Min(c => c.Min.Y)).ThenBy(t => t.Cells.Min(c => c.Min.X)))
                    blocks.Add((target.Addon, target.Block, target.Cells, target.Context, target.Body));
            }
        }
        else
        {
            foreach (var (block, cells) in popups.ScanTargets(atLevel - 1))
            {
                if (cells.Count > 0)
                    blocks.Add((string.Empty, block, cells, null, false));
            }
        }

        var stops = new List<CursorStop>();
        foreach (var (addon, block, cells, context, body) in blocks)
        {
            var orderedCells = cells.OrderBy(c => c.Index).ToList();
            var target = stopBlocks.Count;
            stopBlocks.Add(block);
            stopAddons.Add(addon);
            stopBodies.Add(body);
            stopCells.Add(orderedCells);
            contexts.Add(context);
            var text = block.ScanText;
            IReadOnlyList<WordSpan>? spans = null;
            HashSet<int>? starts = null;
            if (wordMode && config.TokenizedHover)
            {
                var task = service.SegmentAsync(text);
                if (task.IsCompletedSuccessfully)
                {
                    spans = task.Result;
                    starts = spans.Select(s => s.Start).ToHashSet();
                }
            }

            foreach (var (index, min, max) in orderedCells)
            {
                if (index >= text.Length || char.IsLowSurrogate(text[index]))
                    continue;
                var cp = Japanese.CodePointAt(text, index);
                if (starts is not null ? !starts.Contains(index) : config.ScanOnlyJapanese && !Japanese.IsJapanese(cp))
                    continue;
                var charEnd = index + (char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1);
                var endIndex = spans is not null && WordSpan.At(spans, index) is { } span ? span.End : charEnd;
                var stopMin = min;
                var stopMax = max;
                if (endIndex > charEnd)
                {
                    var lineRects = CursorMoves.SpanLineRects(orderedCells, index, endIndex);
                    if (lineRects.Count > 0)
                        (stopMin, stopMax) = lineRects[0];
                }
                stops.Add(new CursorStop(target, index, stopMin, stopMax, endIndex));
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

    private bool ScanButtonSharedWithPopupAction()
    {
        var scan = config.ControllerScanButton;
        if (scan == GamepadButtons.None)
            return false;
        return scan == config.ControllerMineButton
               || scan == config.ControllerAudioButton
               || scan == config.ControllerPrevEntryButton
               || scan == config.ControllerNextEntryButton
               || scan == config.ControllerDiveButton
               || scan == config.ControllerWordModeButton
               || scan == config.ControllerWindowButton
               || scan == config.ControllerHistoryButton
               || scan == config.ControllerReplayVoiceButton
               || scan == config.ControllerBackExitButton;
    }

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
        GamepadButtons.DpadDown => "D-pad down",
        _ => button.ToString(),
    };

    internal static string ButtonComboLabel(GamepadButtons mask, bool ps)
    {
        if (mask == GamepadButtons.None)
            return "None";
        return string.Join(" + ", AllButtons.Where(b => (mask & b) == b).Select(b => ButtonLabel(b, ps)));
    }

    private bool PollToggleKey() =>
        PollKeybind(config.ControllerToggleKey, config.ControllerToggleKeyCtrl, config.ControllerToggleKeyShift, config.ControllerToggleKeyAlt, ref toggleKeyWasDown);

    private bool PollReplayKey() =>
        PollKeybind(config.ReplayVoiceKey, config.ReplayVoiceKeyCtrl, config.ReplayVoiceKeyShift, config.ReplayVoiceKeyAlt, ref replayKeyWasDown);

    private bool PollKeybind(int vk, bool reqCtrl, bool reqShift, bool reqAlt, ref bool wasDown)
    {
        if (vk <= 0)
        {
            wasDown = false;
            return false;
        }
        if (!IsGameForeground())
        {
            wasDown = false;
            return false;
        }

        var down = IsOsKeyDown(vk);
        var pressed = down && !wasDown;
        wasDown = down;

        if (!pressed || SuppressToggleKey)
            return false;

        var ctrl = IsOsKeyDown(VkControl);
        var shift = IsOsKeyDown(VkShift);
        var alt = IsOsKeyDown(VkMenu);
        if (ctrl != reqCtrl || shift != reqShift || alt != reqAlt)
            return false;

        if (!Active
            && !KeybindFormat.IsNonTypingKey(vk, ctrl, alt)
            && (ImGui.GetIO().WantTextInput || IsGameTextInputActive()))
            return false;

        ConsumeGameKey((VirtualKey)vk);
        return true;
    }

    internal static bool IsOsKeyDown(int vk)
    {
        if (vk <= 0)
            return false;
        if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            return true;
        if (!PlatformInfo.Current.IsWine)
            return false;
        try
        {
            var key = (VirtualKey)vk;
            return DalamudServices.KeyState.IsVirtualKeyValid(key) && DalamudServices.KeyState[key];
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGameForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == 0)
            return PlatformInfo.Current.IsWine;
        _ = GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static unsafe bool IsGameTextInputActive()
    {
        try
        {
            var rapture = RaptureAtkModule.Instance();
            return rapture != null && rapture->IsTextInputActive();
        }
        catch
        {
            return false;
        }
    }

    private static void ConsumeGameKey(VirtualKey vk)
    {
        if (vk == VirtualKey.NO_KEY)
            return;
        try
        {
            if (DalamudServices.KeyState.IsVirtualKeyValid(vk))
            {
                DalamudServices.KeyState[vk] = false;
                DalamudServices.KeyState.SetRawValue(vk, 0);
            }
        }
        catch
        {
            // Ignore keys outside FFXIV's SeVirtualKey map (e.g. F13-F24, mouse buttons).
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    /// <summary>True on press, then again after a delay at a steady rate while held.</summary>
    private bool Repeat(string key, bool down, DateTime now)
    {
        if (!down)
        {
            repeats.Remove(key);
            return false;
        }

        if (!repeats.TryGetValue(key, out var next))
        {
            repeats[key] = now.AddSeconds(RepeatDelay);
            return true;
        }

        if (now < next)
            return false;
        repeats[key] = now.AddSeconds(RepeatRate);
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
        var accent = Rendering.Css.PopupTheme.Current.Accent with { W = 1 };
        IReadOnlyList<(Vector2 Min, Vector2 Max)> segments = stop.Target < stopCells.Count
            ? CursorMoves.SpanLineRects(stopCells[stop.Target], stop.Index, stop.EffectiveEnd)
            : [(stop.Min, stop.Max)];
        if (segments.Count == 0)
            segments = [(stop.Min, stop.Max)];

        if (level > 0 && popups.UsingWebView)
        {
            var r = (byte)Math.Clamp(MathF.Round(accent.X * 255f), 0, 255);
            var g = (byte)Math.Clamp(MathF.Round(accent.Y * 255f), 0, 255);
            var b = (byte)Math.Clamp(MathF.Round(accent.Z * 255f), 0, 255);
            popups.SetWebControllerCursor(segments, $"#{r:X2}{g:X2}{b:X2}");
            return;
        }

        popups.SetWebControllerCursor(null);
        var draw = ImGui.GetForegroundDrawList();
        var color = Rendering.Css.CssColor.ToAbgr(accent);
        foreach (var (min, max) in segments)
        {
            var y = max.Y + 2;
            draw.AddLine(new Vector2(min.X, y), new Vector2(max.X, y), color, Math.Max(2f, (max.Y - min.Y) / 12f));
        }
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
            backlog.Open && level == 0
                ? "D-Pad " + (wordMode ? "word / ↑↓ line" : "char / ↑↓ line")
                : "D-Pad " + (wordMode ? "word" : "char"),
        };
        var popupAtLevel = popups.VisibleCount > level;
        var waitingForScan = !config.ControllerAutoScan && !popupAtLevel;
        if (waitingForScan && config.ControllerScanButton != GamepadButtons.None)
            parts.Add($"{L(config.ControllerScanButton)} look up");

        if (popups.VisibleCount > 0)
        {
            var mineHiddenByScan = waitingForScan && config.ControllerMineButton == config.ControllerScanButton;
            if (!mineHiddenByScan && config.ControllerMineButton != GamepadButtons.None) parts.Add($"{L(config.ControllerMineButton)} mine");
            if (config.ControllerAudioButton != GamepadButtons.None) parts.Add($"{L(config.ControllerAudioButton)} audio");
            if (config.ControllerPrevEntryButton != GamepadButtons.None || config.ControllerNextEntryButton != GamepadButtons.None)
                parts.Add($"{L(config.ControllerPrevEntryButton)}/{L(config.ControllerNextEntryButton)} entry");
            if (popupAtLevel && popups.HasScanTargets(level) && config.ControllerDiveButton != GamepadButtons.None)
                parts.Add($"{L(config.ControllerDiveButton)} inside");
        }

        if (level == 0 && config.ControllerHistoryButton != GamepadButtons.None && (backlog.Open || backlog.LineCount > 0))
            parts.Add($"{L(config.ControllerHistoryButton)} {(backlog.Open ? "live" : "backlog")}");
        if (level == 0 && config.ControllerReplayVoiceButton != GamepadButtons.None && (backlog.Open || trackedLiveLineId > 0 || backlog.LatestDialogueLineId is not null))
            parts.Add($"{L(config.ControllerReplayVoiceButton)} repeat");
        if (level == 0 && !backlog.Open && config.ControllerWindowButton != GamepadButtons.None && stopAddons.Distinct(StringComparer.Ordinal).Skip(1).Any())
            parts.Add($"{L(config.ControllerWindowButton)} window");
        else if (level == 0 && backlog.Open && config.ControllerWindowButton != GamepadButtons.None && nativeHover.Targets().Count > 0)
            parts.Add($"{L(config.ControllerWindowButton)} game UI");
        if (config.ControllerWordModeButton != GamepadButtons.None)
            parts.Add($"{L(config.ControllerWordModeButton)} {(wordMode ? "chars" : "words")}");
        if (config.ControllerBackExitButton != GamepadButtons.None)
        {
            var backLabel = !config.ControllerAutoScan && popupAtLevel
                ? "close"
                : level > 0
                    ? "back"
                    : backlog.Open && !backlog.State.AutoOpened && HasLiveNonHudTarget() ? "live" : "exit";
            parts.Add($"{L(config.ControllerBackExitButton)} {backLabel}");
        }
        if (config.ControllerHoldButton != GamepadButtons.None
            && config.ControllerHoldButton != GamepadButtons.Select
            && config.ControllerHoldButton != GamepadButtons.Start)
        {
            var prefix = config.ControllerTriggerMode == ControllerTriggerMode.Hold ? "Hold " : string.Empty;
            parts.Add($"{prefix}{ButtonComboLabel(config.ControllerHoldButton, ps)} exit");
        }
        else
        {
            parts.Add($"{(ps ? "Select" : "Back")} exit");
        }
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
