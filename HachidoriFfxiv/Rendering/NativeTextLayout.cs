using System;
using System.Collections.Generic;
using System.Numerics;

namespace HachidoriFfxiv.Rendering;

/// <summary>Managed hit targets in node-local coordinates. Measurements come from the game font.</summary>
public sealed class NativeTextLayout
{
    public readonly record struct Cell(int Index, Vector2 Min, Vector2 Max);
    public IReadOnlyList<Cell> Cells { get; private init; } = [];

    public static NativeTextLayout Build(string text, float width, float height, int alignment,
        float glyphHeight, float lineAdvance, bool wrap, Func<string, Vector2> measure)
    {
        var lines = new List<List<Cell>> { new() };
        var lineStart = 0;
        var previousWidth = 0f;
        for (var i = 0; i < text.Length;)
        {
            if (text[i] is '\r' or '\n')
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                lineStart = ++i;
                previousWidth = 0;
                lines.Add(new());
                continue;
            }
            var length = char.IsSurrogatePair(text, i) ? 2 : 1;
            var nextWidth = measure(text[lineStart..(i + length)]).X;
            if (wrap && nextWidth > width && lines[^1].Count > 0)
            {
                lineStart = i;
                previousWidth = 0;
                lines.Add(new());
                nextWidth = measure(text.Substring(i, length)).X;
            }
            // A zero-width mark has no independent hover rectangle.
            if (nextWidth > previousWidth)
                lines[^1].Add(new Cell(i, new(previousWidth, 0), new(nextWidth, glyphHeight)));
            previousWidth = nextWidth;
            i += length;
        }

        var cells = new List<Cell>();
        var contentHeight = glyphHeight + (lines.Count - 1) * lineAdvance;
        var vertical = Math.Clamp(alignment / 3, 0, 2);
        var y = Math.Max(0, height - contentHeight) * vertical / 2f;
        foreach (var line in lines)
        {
            var lineWidth = line.Count > 0 ? line[^1].Max.X : 0;
            var x = Math.Max(0, width - lineWidth) * Math.Clamp(alignment % 3, 0, 2) / 2f;
            foreach (var cell in line)
                cells.Add(cell with { Min = cell.Min + new Vector2(x, y), Max = cell.Max + new Vector2(x, y) });
            y += lineAdvance;
        }
        return new NativeTextLayout { Cells = cells };
    }

    /// <summary>
    /// One independent layout per source line, with offsets local to that line. Chat holds many
    /// unrelated messages in one text node; each is its own sentence for lookups and mining.
    /// </summary>
    public IReadOnlyList<(string Text, NativeTextLayout Layout)> SplitLines(string text)
    {
        var parts = new List<(string, NativeTextLayout)>();
        for (var start = 0; start < text.Length;)
        {
            var end = text.IndexOfAny(['\r', '\n'], start);
            if (end < 0) end = text.Length;
            var lineCells = new List<Cell>();
            foreach (var cell in Cells)
                if (cell.Index >= start && cell.Index < end) lineCells.Add(cell with { Index = cell.Index - start });
            if (lineCells.Count > 0) parts.Add((text[start..end], new NativeTextLayout { Cells = lineCells }));
            start = end + (end + 1 < text.Length && text[end] == '\r' && text[end + 1] == '\n' ? 2 : 1);
        }
        return parts;
    }

    public Cell? Hit(Vector2 point)
    {
        foreach (var cell in Cells)
            if (Contains(cell.Min, cell.Max, point)) return cell;
        return null;
    }

    public Cell? HitScreen(Vector2 point, Vector2 origin, Vector2 scale, Vector2 clipMin, Vector2 clipMax)
    {
        if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || scale.X <= 0 || scale.Y <= 0
            || !Contains(clipMin, clipMax, point)) return null;
        if (Hit((point - origin) / scale) is not { } cell) return null;
        var min = Vector2.Max(origin + cell.Min * scale, clipMin);
        var max = Vector2.Min(origin + cell.Max * scale, clipMax);
        return Contains(min, max, point) ? cell with { Min = min, Max = max } : null;
    }

    public static bool Contains(Vector2 min, Vector2 max, Vector2 point) =>
        point.X >= min.X && point.X < max.X && point.Y >= min.Y && point.Y < max.Y;
}
