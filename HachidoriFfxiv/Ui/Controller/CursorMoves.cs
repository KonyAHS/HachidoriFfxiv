using System;
using System.Collections.Generic;
using System.Numerics;

namespace HachidoriFfxiv.Ui;

/// <summary>A place the controller cursor can stop: a character or word of a text block, and its rectangle on screen.</summary>
public readonly record struct CursorStop(int Target, int Index, Vector2 Min, Vector2 Max, int EndIndex = -1)
{
    public Vector2 Center => (Min + Max) * 0.5f;
    public float Height => Max.Y - Min.Y;
    public int EffectiveEnd => EndIndex > Index ? EndIndex : Index + 1;
}

/// <summary>Controller cursor movement over stops given in reading order.</summary>
public static class CursorMoves
{
    /// <summary>The next (+1) or previous (-1) stop in reading order, staying put at either end.</summary>
    public static int Horizontal(IReadOnlyList<CursorStop> stops, int current, int direction)
    {
        if (stops.Count == 0)
            return -1;
        if (current < 0 || current >= stops.Count)
            return 0;
        return Math.Clamp(current + Math.Sign(direction), 0, stops.Count - 1);
    }

    /// <summary>
    /// The stop on the visual line above (-1) or below (+1) that is horizontally nearest, across
    /// blocks; stays put when there is no such line.
    /// </summary>
    public static int Vertical(IReadOnlyList<CursorStop> stops, int current, int direction)
    {
        if (stops.Count == 0)
            return -1;
        if (current < 0 || current >= stops.Count)
            return 0;
        var here = stops[current];
        var tolerance = Math.Max(2f, here.Height * 0.5f);

        // The nearest line in that direction: the closest centre Y beyond this line.
        float? lineY = null;
        foreach (var stop in stops)
        {
            var dy = stop.Center.Y - here.Center.Y;
            if (direction < 0 ? dy >= -tolerance : dy <= tolerance)
                continue;
            if (lineY is null || (direction < 0 ? stop.Center.Y > lineY : stop.Center.Y < lineY))
                lineY = stop.Center.Y;
        }

        if (lineY is not { } y)
            return current;

        var best = current;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            if (Math.Abs(stop.Center.Y - y) > Math.Max(2f, stop.Height * 0.5f))
                continue;
            var distance = Math.Abs(stop.Center.X - here.Center.X);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Merges glyph cells in <c>[startIndex, endIndex)</c> into one bounding rectangle per visual line,
    /// so multi-character (and line-wrapped) words can be underlined cleanly.
    /// </summary>
    public static List<(Vector2 Min, Vector2 Max)> SpanLineRects(
        IReadOnlyList<(int Index, Vector2 Min, Vector2 Max)> cells,
        int startIndex,
        int endIndex)
    {
        var result = new List<(Vector2 Min, Vector2 Max)>();
        if (cells.Count == 0 || endIndex <= startIndex)
            return result;

        var hasLine = false;
        var lineMin = Vector2.Zero;
        var lineMax = Vector2.Zero;
        foreach (var (index, min, max) in cells)
        {
            if (index < startIndex || index >= endIndex)
                continue;
            if (!hasLine)
            {
                hasLine = true;
                lineMin = min;
                lineMax = max;
                continue;
            }

            var lineCenterY = (lineMin.Y + lineMax.Y) * 0.5f;
            var cellCenterY = (min.Y + max.Y) * 0.5f;
            var tol = MathF.Max(4f, MathF.Max(lineMax.Y - lineMin.Y, max.Y - min.Y) * 0.5f);
            if (MathF.Abs(cellCenterY - lineCenterY) <= tol)
            {
                lineMin = Vector2.Min(lineMin, min);
                lineMax = Vector2.Max(lineMax, max);
            }
            else
            {
                result.Add((lineMin, lineMax));
                lineMin = min;
                lineMax = max;
            }
        }

        if (hasLine)
            result.Add((lineMin, lineMax));
        return result;
    }
}

public enum ControllerTriggerMode
{
    Hold,
    Press,
}

/// <summary>
/// Tracks controller-mode enter/exit trigger timing (hold or instant press) so the same
/// button or combo reliably toggles the mode without bouncing while still held.
/// </summary>
public sealed class ControllerTriggerState
{
    private DateTime? holdStarted;
    private bool waitingForRelease;
    private bool wasDown;

    public bool WaitingForRelease => waitingForRelease;

    public void Reset()
    {
        holdStarted = null;
        waitingForRelease = false;
        wasDown = false;
    }

    /// <summary>
    /// Marks that controller mode was just entered or exited via another path (e.g. command,
    /// Back/Select, or keybind) while the trigger may currently be held.
    /// </summary>
    public void RequireReleaseIfDown(bool triggerDown)
    {
        holdStarted = null;
        if (triggerDown)
            waitingForRelease = true;
    }

    /// <summary>
    /// Advances trigger state for this frame. Returns true on the exact frame controller mode
    /// should toggle (enter when inactive, exit when active).
    /// </summary>
    public bool Update(bool triggerDown, ControllerTriggerMode mode, double holdSeconds, DateTime now)
    {
        var justPressed = triggerDown && !wasDown;
        wasDown = triggerDown;

        if (!triggerDown)
        {
            holdStarted = null;
            waitingForRelease = false;
            return false;
        }

        if (waitingForRelease)
            return false;

        if (mode == ControllerTriggerMode.Press)
        {
            if (!justPressed)
                return false;
            waitingForRelease = true;
            holdStarted = null;
            return true;
        }

        holdStarted ??= now;
        if ((now - holdStarted.Value).TotalSeconds >= Math.Max(0.05, holdSeconds))
        {
            waitingForRelease = true;
            holdStarted = null;
            return true;
        }

        return false;
    }
}

/// <summary>Human-readable labels for Windows virtual-key chords used as optional toggle keybinds.</summary>
public static class KeybindFormat
{
    public static string Format(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk <= 0)
            return "None";
        var parts = new List<string>(4);
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        parts.Add(KeyName(vk));
        return string.Join(" + ", parts);
    }

    public static string KeyName(int vk) => vk switch
    {
        0 => "None",
        0x04 => "Middle Click",
        0x05 => "Mouse 4",
        0x06 => "Mouse 5",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "Caps Lock",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2C => "Print Screen",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
        0x6A => "Num *",
        0x6B => "Num +",
        0x6C => "Num Sep",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x90 => "Num Lock",
        0x91 => "Scroll Lock",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => $"Key 0x{vk:X2}",
    };

    /// <summary>Keys that are safe to trigger even while a chat or text input box is focused.</summary>
    public static bool IsNonTypingKey(int vk, bool ctrl, bool alt) =>
        ctrl || alt
        || vk is 0x04 or 0x05 or 0x06 or 0x13 or 0x2C or 0x2D or 0x91
        || (vk is >= 0x70 and <= 0x87);
}

public enum ControllerScanPress
{
    None,
    Open,
    Close,
    PassThrough,
}

/// <summary>
/// Tracks manual-scan state for controller mode when auto-scan is disabled.
/// </summary>
public sealed class ControllerScanState
{
    private bool popupObserved;

    public bool Active { get; private set; }
    public object? ScannedBlock { get; private set; }
    public int ScannedIndex { get; private set; } = -1;

    public void Reset()
    {
        Active = false;
        popupObserved = false;
        ScannedBlock = null;
        ScannedIndex = -1;
    }

    /// <summary>Updates visibility tracking; clears an active scan if its popup was closed externally.</summary>
    public void SyncVisibility(bool popupVisibleAtLevel)
    {
        if (!Active)
            return;
        if (popupVisibleAtLevel)
            popupObserved = true;
        else if (popupObserved)
            Reset();
    }

    /// <summary>Marks a cursor move or text change. Returns true if an active scan was cleared and its popup should close.</summary>
    public bool OnStopChanged(object? block, int index)
    {
        if (!Active)
            return false;
        if (ReferenceEquals(ScannedBlock, block) && ScannedIndex == index)
            return false;
        Reset();
        return true;
    }

    /// <summary>Keeps an active scan aligned with a snapped index after toggling word/char mode on the same block.</summary>
    public void AlignAfterModeToggle(object? block, int index)
    {
        if (!Active)
            return;
        if (ReferenceEquals(ScannedBlock, block))
            ScannedIndex = index;
        else
            Reset();
    }

    /// <summary>Restores active scan state when backing out of a nested popup to a parent whose popup is still open.</summary>
    public void RestoreParent(object? block, int index, bool parentPopupVisible)
    {
        if (parentPopupVisible && block is not null)
        {
            Active = true;
            popupObserved = true;
            ScannedBlock = block;
            ScannedIndex = index;
        }
        else
        {
            Reset();
        }
    }

    /// <summary>
    /// Handles a press of the scan button when auto-scan is off.
    /// Returns the action to take and whether the button press should be consumed.
    /// </summary>
    public ControllerScanPress HandleScanButton(
        object? block,
        int index,
        bool popupVisibleAtLevel,
        bool sharedWithPopupAction)
    {
        if (!Active || !popupVisibleAtLevel)
        {
            if (block is null)
                return ControllerScanPress.None;
            Active = true;
            popupObserved = false;
            ScannedBlock = block;
            ScannedIndex = index;
            return ControllerScanPress.Open;
        }

        // A popup is already open at this level. If the button is shared with a popup action
        // (such as Mine on A / Cross), let that action handle the press instead of closing.
        if (sharedWithPopupAction)
            return ControllerScanPress.PassThrough;

        Reset();
        return ControllerScanPress.Close;
    }
}

public enum BacklogSnapEdge
{
    None,
    FirstStop,
    TopNearestX,
    BottomNearestX,
}

/// <summary>
/// Pure selection, auto-fallback, and auto-advance pinning state for the controller dialogue backlog.
/// Kept free of ImGui/Dalamud types so every transition is covered by unit tests.
/// </summary>
public sealed class ControllerBacklogState
{
    private long openedLatestId;

    public bool Open { get; private set; }
    public long SelectedLineId { get; private set; }
    public bool AutoOpened { get; private set; }
    public long? PinnedFromLiveLineId { get; private set; }
    public bool HasNewerSinceOpen { get; private set; }
    public BacklogSnapEdge PendingSnap { get; private set; }
    public float PendingSnapX { get; private set; }

    /// <summary>True when entering controller mode should land directly in the backlog instead of HUD widgets.</summary>
    public static bool ShouldAutoOpenOnEnter(bool autoOpenEnabled, bool hasLiveDialogue, bool hasNonHudWindow, int backlogCount) =>
        autoOpenEnabled && !hasLiveDialogue && !hasNonHudWindow && backlogCount > 0;

    /// <summary>True when FFXIV advanced or closed a live dialogue line while the user was actively scanning it.</summary>
    public static bool ShouldPinOnLiveAdvance(bool pinEnabled, long trackedLiveLineId, long? currentLiveDialogueId, bool liveDialogueVisible) =>
        pinEnabled && trackedLiveLineId > 0 && (!liveDialogueVisible || (currentLiveDialogueId is { } cur && cur != trackedLiveLineId));

    /// <summary>Opens the backlog at an offset back from the newest line (0 = latest, 1 = previous), or at an explicit line id.</summary>
    public bool OpenAt(
        IReadOnlyList<long> lineIds,
        int offsetFromLatest = 0,
        long? explicitLineId = null,
        bool autoOpened = false,
        bool pinnedFromAdvance = false,
        BacklogSnapEdge snap = BacklogSnapEdge.FirstStop,
        float snapX = 0f)
    {
        if (lineIds.Count == 0)
            return false;

        var chosenIndex = -1;
        if (explicitLineId is { } targetId)
        {
            for (var i = lineIds.Count - 1; i >= 0; i--)
            {
                if (lineIds[i] == targetId)
                {
                    chosenIndex = i;
                    break;
                }
            }
        }
        if (chosenIndex < 0)
            chosenIndex = Math.Clamp(lineIds.Count - 1 - Math.Max(0, offsetFromLatest), 0, lineIds.Count - 1);

        Open = true;
        SelectedLineId = lineIds[chosenIndex];
        openedLatestId = lineIds[^1];
        AutoOpened = autoOpened;
        PinnedFromLiveLineId = pinnedFromAdvance ? SelectedLineId : null;
        HasNewerSinceOpen = pinnedFromAdvance && SelectedLineId != openedLatestId;
        PendingSnap = snap;
        PendingSnapX = snapX;
        return true;
    }

    public void Close()
    {
        Open = false;
        SelectedLineId = 0;
        openedLatestId = 0;
        AutoOpened = false;
        PinnedFromLiveLineId = null;
        HasNewerSinceOpen = false;
        PendingSnap = BacklogSnapEdge.None;
        PendingSnapX = 0f;
    }

    /// <summary>Keeps selection valid as new lines arrive or old lines trim out. Returns the selected index or -1 if closed.</summary>
    public int Sync(IReadOnlyList<long> lineIds)
    {
        if (!Open)
            return -1;
        if (lineIds.Count == 0)
        {
            Close();
            return -1;
        }

        if (lineIds[^1] > openedLatestId)
            HasNewerSinceOpen = true;

        for (var i = lineIds.Count - 1; i >= 0; i--)
        {
            if (lineIds[i] == SelectedLineId)
                return i;
        }

        // The previously selected line was trimmed from history; fall back to the oldest surviving line.
        SelectedLineId = lineIds[0];
        PendingSnap = BacklogSnapEdge.FirstStop;
        return 0;
    }

    /// <summary>Steps to an older (-1) or newer (+1) dialogue entry, scheduling an edge snap at <paramref name="cursorX"/>.</summary>
    public bool Step(IReadOnlyList<long> lineIds, int delta, float cursorX)
    {
        if (!Open || lineIds.Count == 0 || delta == 0)
            return false;

        var idx = Sync(lineIds);
        if (idx < 0)
            return false;

        var next = Math.Clamp(idx + Math.Sign(delta), 0, lineIds.Count - 1);
        if (next == idx)
            return false;

        SelectedLineId = lineIds[next];
        PendingSnap = delta < 0 ? BacklogSnapEdge.BottomNearestX : BacklogSnapEdge.TopNearestX;
        PendingSnapX = cursorX;
        if (next == lineIds.Count - 1)
        {
            openedLatestId = lineIds[^1];
            HasNewerSinceOpen = false;
            PinnedFromLiveLineId = null;
        }
        return true;
    }

    /// <summary>Selects a specific visible line (e.g. from pointer glide or click) without forcing an edge snap.</summary>
    public bool SelectLine(IReadOnlyList<long> lineIds, long lineId)
    {
        if (!Open || lineIds.Count == 0 || lineId == SelectedLineId)
            return false;
        for (var i = lineIds.Count - 1; i >= 0; i--)
        {
            if (lineIds[i] == lineId)
            {
                SelectedLineId = lineId;
                PendingSnap = BacklogSnapEdge.None;
                if (i == lineIds.Count - 1)
                {
                    openedLatestId = lineIds[^1];
                    HasNewerSinceOpen = false;
                    PinnedFromLiveLineId = null;
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>Consumes any pending snap and picks the matching stop index in the selected line.</summary>
    public int? ConsumePendingSnap(IReadOnlyList<CursorStop> stops)
    {
        var edge = PendingSnap;
        if (edge == BacklogSnapEdge.None || stops.Count == 0)
            return null;

        PendingSnap = BacklogSnapEdge.None;
        if (edge == BacklogSnapEdge.FirstStop)
            return 0;

        var targetY = edge == BacklogSnapEdge.TopNearestX
            ? float.MaxValue
            : float.MinValue;
        for (var i = 0; i < stops.Count; i++)
        {
            var y = stops[i].Center.Y;
            if (edge == BacklogSnapEdge.TopNearestX ? y < targetY : y > targetY)
                targetY = y;
        }

        var best = 0;
        var bestDx = float.MaxValue;
        for (var i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            if (MathF.Abs(s.Center.Y - targetY) > MathF.Max(2f, s.Height * 0.5f))
                continue;
            var dx = MathF.Abs(s.Center.X - PendingSnapX);
            if (dx < bestDx)
            {
                bestDx = dx;
                best = i;
            }
        }
        return best;
    }
}
