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
