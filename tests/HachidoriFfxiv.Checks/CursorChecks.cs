using System.Numerics;
using HachidoriFfxiv.Ui;

/// <summary>Controller cursor movement over on-screen characters.</summary>
static class CursorChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Dialogue block 0: two lines of 5 characters (20 px wide, 24 px tall), then battle talk
        // block 1 lower down, offset to the right.
        var stops = new List<CursorStop>();
        for (var line = 0; line < 2; line++)
            for (var c = 0; c < 5; c++)
                stops.Add(new CursorStop(0, line * 5 + c, new Vector2(100 + c * 20, 100 + line * 30), new Vector2(120 + c * 20, 124 + line * 30)));
        for (var c = 0; c < 3; c++)
            stops.Add(new CursorStop(1, c, new Vector2(170 + c * 20, 300), new Vector2(190 + c * 20, 324)));

        check(CursorMoves.Horizontal(stops, 4, 1) == 5 && CursorMoves.Horizontal(stops, 9, 1) == 10,
            "right moves in reading order, across lines and into the next block");
        check(CursorMoves.Horizontal(stops, 0, -1) == 0 && CursorMoves.Horizontal(stops, 12, 1) == 12,
            "the cursor stays put at either end");
        check(CursorMoves.Vertical(stops, 2, 1) == 7 && CursorMoves.Vertical(stops, 7, -1) == 2,
            "down and up keep the column on the adjacent line");
        check(CursorMoves.Vertical(stops, 6, 1) == 10 && CursorMoves.Vertical(stops, 12, 1) == 12,
            "down reaches the nearest character of a lower block, and stops at the last line");
        check(CursorMoves.Vertical(stops, 10, -1) == 8,
            "up from a lower block lands on the horizontally nearest character");
        check(CursorMoves.Horizontal([], 0, 1) == -1 && CursorMoves.Vertical(stops, -1, 1) == 0,
            "no stops gives no cursor, and a lost cursor restarts at the first stop");

        // Manual-scan state machine (when ControllerAutoScan is false).
        var blockA = new object();
        var blockB = new object();
        var scan = new ControllerScanState();
        check(!scan.Active, "manual scan starts inactive so entering controller mode does not pop up immediately");
        check(!scan.OnStopChanged(blockA, 0) && !scan.Active, "moving cursor before scanning keeps scan inactive");

        // Press shared Scan/Mine button (e.g. A/Cross) when no popup is open -> opens popup and consumes press.
        check(scan.HandleScanButton(blockA, 2, popupVisibleAtLevel: false, sharedWithPopupAction: true) == ControllerScanPress.Open
              && scan.Active && !scan.PopupObserved,
            "pressing Scan when no popup is open activates manual scan");
        // While lookup is still in flight (popup not yet visible), SyncVisibility does not cancel it.
        scan.SyncVisibility(false);
        check(scan.Active, "in-flight manual scan stays active before popup becomes visible");
        // Popup appears.
        scan.SyncVisibility(true);
        check(scan.Active && scan.PopupObserved, "observing popup records PopupObserved");
        // Pressing shared Scan/Mine button while popup is open passes through so Mine can run.
        check(scan.HandleScanButton(blockA, 2, popupVisibleAtLevel: true, sharedWithPopupAction: true) == ControllerScanPress.PassThrough
              && scan.Active,
            "shared Scan/Mine button passes through when popup is already open");
        // Staying on the same stop (e.g. stick jitter or boundary D-pad press) keeps popup open.
        check(!scan.OnStopChanged(blockA, 2) && scan.Active, "no-op cursor movement keeps manual popup open");
        // Toggling word/char mode on the same block aligns index without closing.
        scan.AlignAfterModeToggle(blockA, 3);
        check(scan.Active && !scan.OnStopChanged(blockA, 3), "word/char mode toggle on same block preserves active manual scan");
        // Moving to a different stop clears scan and signals popup close.
        check(scan.OnStopChanged(blockA, 4) && !scan.Active, "moving to a new stop closes the previous manual popup");

        // Dedicated (non-shared) Scan button toggles open and closed.
        check(scan.HandleScanButton(blockA, 4, popupVisibleAtLevel: false, sharedWithPopupAction: false) == ControllerScanPress.Open
              && scan.Active,
            "dedicated Scan button opens manual popup");
        scan.SyncVisibility(true);
        check(scan.HandleScanButton(blockA, 4, popupVisibleAtLevel: true, sharedWithPopupAction: false) == ControllerScanPress.Close
              && !scan.Active,
            "dedicated Scan button closes an already-open manual popup");

        // External dismissal (e.g. clicking outside with mouse) clears active scan instead of reopening.
        scan.HandleScanButton(blockA, 1, popupVisibleAtLevel: false, sharedWithPopupAction: true);
        scan.SyncVisibility(true);
        scan.SyncVisibility(false);
        check(!scan.Active, "externally closed popup clears manual scan state");

        // Dialogue text changing to a new block closes old popup without auto-scanning the new line.
        scan.HandleScanButton(blockA, 0, popupVisibleAtLevel: false, sharedWithPopupAction: true);
        check(scan.OnStopChanged(blockB, 0) && !scan.Active, "dialogue block change closes old manual popup");

        // Backing out of a nested popup restores parent popup scan state.
        scan.RestoreParent(blockA, 2, parentPopupVisible: true);
        check(scan.Active && scan.PopupObserved && !scan.OnStopChanged(blockA, 2),
            "backing out of nested popup keeps parent popup active");

        // Parsed-word underline rectangles (single-line and line-wrapped).
        var wordCells = new List<(int Index, Vector2 Min, Vector2 Max)>
        {
            (0, new Vector2(100, 100), new Vector2(120, 124)),
            (1, new Vector2(120, 100), new Vector2(140, 124)),
            (2, new Vector2(140, 100), new Vector2(160, 124)),
            (4, new Vector2(100, 132), new Vector2(120, 156)),
            (5, new Vector2(120, 132), new Vector2(140, 156)),
        };
        var singleLine = CursorMoves.SpanLineRects(wordCells, 0, 3);
        check(singleLine.Count == 1 && singleLine[0].Min == new Vector2(100, 100) && singleLine[0].Max == new Vector2(160, 124),
            "parsed word underline spans every character on the same visual line");
        var wrappedWord = CursorMoves.SpanLineRects(wordCells, 2, 6);
        check(wrappedWord.Count == 2
              && wrappedWord[0].Min == new Vector2(140, 100) && wrappedWord[0].Max == new Vector2(160, 124)
              && wrappedWord[1].Min == new Vector2(100, 132) && wrappedWord[1].Max == new Vector2(140, 156),
            "line-wrapped parsed word splits into one underline segment per visual line");

        // Hold trigger: hold to enter, release guard, hold same trigger to exit, no bounce while releasing.
        var trigger = new ControllerTriggerState();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        check(!trigger.Update(true, ControllerTriggerMode.Hold, 0.6, t0), "hold trigger does not fire immediately");
        check(!trigger.Update(true, ControllerTriggerMode.Hold, 0.6, t0.AddSeconds(0.5)), "hold trigger waits for full hold duration");
        check(trigger.Update(true, ControllerTriggerMode.Hold, 0.6, t0.AddSeconds(0.6)), "hold trigger fires on reaching hold duration");
        check(!trigger.Update(true, ControllerTriggerMode.Hold, 0.6, t0.AddSeconds(1.5)) && trigger.WaitingForRelease,
            "continuing to hold after entering does not immediately exit");
        check(!trigger.Update(false, ControllerTriggerMode.Hold, 0.6, t0.AddSeconds(1.6)) && !trigger.WaitingForRelease,
            "releasing clears WaitingForRelease");
        check(!trigger.Update(true, ControllerTriggerMode.Hold, 0.6, t0.AddSeconds(2.0)), "holding to exit waits for hold duration");
        check(trigger.Update(true, ControllerTriggerMode.Hold, 0.6, t0.AddSeconds(2.6)), "holding the same trigger exits gamepad mode");
        check(!trigger.Update(true, ControllerTriggerMode.Hold, 0.6, t0.AddSeconds(3.5)),
            "continuing to hold after exiting does not bounce back into gamepad mode");

        // Press trigger: instant enter on press, instant exit on next press, no bounce while held.
        trigger.Reset();
        check(trigger.Update(true, ControllerTriggerMode.Press, 0.6, t0), "press trigger enters immediately on press");
        check(!trigger.Update(true, ControllerTriggerMode.Press, 0.6, t0.AddSeconds(0.1)), "holding press trigger does not double-toggle");
        check(!trigger.Update(false, ControllerTriggerMode.Press, 0.6, t0.AddSeconds(0.2)), "releasing press trigger arms next toggle");
        check(trigger.Update(true, ControllerTriggerMode.Press, 0.6, t0.AddSeconds(0.3)), "pressing trigger again exits immediately");

        // Keybind formatting for extra controller paddle keys & chords.
        check(KeybindFormat.Format(0, false, false, false) == "None"
              && KeybindFormat.Format(0x7C, false, false, false) == "F13"
              && KeybindFormat.Format(0x05, false, false, false) == "Mouse 4"
              && KeybindFormat.Format(0x47, true, true, false) == "Ctrl + Shift + G",
            "keybind formatter labels F13, Mouse 4, and modifier chords cleanly");
    }
}
