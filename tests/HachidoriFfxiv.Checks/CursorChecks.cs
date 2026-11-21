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
    }
}
