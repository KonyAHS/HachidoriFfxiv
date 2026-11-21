using System;
using System.Collections.Generic;
using System.Numerics;

namespace HachidoriFfxiv.Ui;

/// <summary>A place the controller cursor can stop: a character of a text block, and its rectangle on screen.</summary>
public readonly record struct CursorStop(int Target, int Index, Vector2 Min, Vector2 Max)
{
    public Vector2 Center => (Min + Max) * 0.5f;
    public float Height => Max.Y - Min.Y;
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
}
