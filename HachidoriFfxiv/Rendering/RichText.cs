using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;

namespace HachidoriFfxiv.Rendering;

/// <summary>A font rasterised at <see cref="Scale"/> times its ladder's base size.</summary>
public readonly record struct SizedFontRung(ImFontPtr Font, float Scale);

/// <summary>A styled piece of inline text.</summary>
public sealed class RichRun
{
    public string Text = string.Empty;
    public uint Color;        // 0: the theme's text colour
    public float Scale = 1f;
    public bool Bold;
    public bool Underline;
    public bool Strike;
    public uint Background;   // with Chip: the chip's fill
    public bool Chip;         // an unbreakable rounded label
    public string? Ruby;      // furigana drawn above this run (unbreakable)
    public bool Link;
}

/// <summary>
/// A paragraph: inline runs laid out together and scanned as one string, so a lookup
/// can start anywhere and run across style changes.
/// </summary>
public sealed class RichBlock
{
    public DictionaryImage? Image { get; init; }
    public readonly List<RichRun> Runs = new();
    public float IndentEm;
    public string? Marker;
    public float MarginTopEm;

    private string? scanText;
    internal Layout? CachedLayout;

    public string ScanText
    {
        get
        {
            if (scanText is null)
            {
                var sb = new StringBuilder();
                foreach (var run in Runs)
                    sb.Append(run.Text);
                scanText = sb.ToString();
            }

            return scanText;
        }
    }

    public bool IsEmpty => Image is null && Runs.TrueForAll(r => r.Text.Length == 0);

    public RichBlock Add(string text, uint color = 0, float scale = 1f, bool bold = false)
    {
        if (text.Length > 0)
        {
            Runs.Add(new RichRun { Text = text, Color = color, Scale = scale, Bold = bold });
            scanText = null;
            CachedLayout = null;
        }

        return this;
    }

    public RichBlock Add(RichRun run)
    {
        Runs.Add(run);
        scanText = null;
        CachedLayout = null;
        return this;
    }

    internal sealed class Layout
    {
        public float Width;
        public float BaseSize;
        public nint Font;
        public int LadderVersion;
        public float Height;
        public readonly List<Glyph> Glyphs = new();
        public readonly List<Line> Lines = new();
        public readonly List<Decoration> Decorations = new();
        public readonly List<Segment> Segments = new();
    }

    internal struct Glyph
    {
        public int CharIndex;
        public int Length;
        public int Line;
        public float X;
        public float W;
        public float Size;
    }

    internal struct Line
    {
        public float Top;
        public float Height;
        public float RubyHeight;
    }

    /// <summary>A chip background or ruby, drawn per unit.</summary>
    internal struct Decoration
    {
        public int Run;
        public int Line;
        public float X;
        public float W;
        public float Size;
    }

    /// <summary>Consecutive glyphs of one run on one line, drawn with a single AddText.</summary>
    internal struct Segment
    {
        public int Run;
        public int Line;
        public float X;
        public float Size;
        public int CharStart;
        public int CharEnd;
    }
}

/// <summary>Result of hovering a block: the scanned character and its rectangle on screen.</summary>
public readonly record struct TextHit(RichBlock Block, int CharIndex, Vector2 Min, Vector2 Max);

public static unsafe class RichText
{
    private const float LineSpacing = 1.3f;
    private const float RubyScale = 0.5f;
    private const float ChipPadEm = 0.35f;
    private static readonly Dictionary<(nint Font, int Codepoint), float> Advances = new();
    private static Dictionary<nint, SizedFontRung[]> ladders = new();
    private static int ladderVersion;

    public static uint DefaultLinkColor => 0xFFF5B06A; // ABGR: soft blue

    /// <summary>Sets the sized fonts for each base font; a change invalidates cached layouts and advances.</summary>
    public static void SetFontLadders(Dictionary<nint, SizedFontRung[]> next)
    {
        var same = next.Count == ladders.Count;
        foreach (var (font, rungs) in next)
        {
            same = same && ladders.TryGetValue(font, out var old) && SameRungs(rungs, old);
            if (!same)
                break;
        }

        ladders = next;
        if (same)
            return;
        ladderVersion++;
        Advances.Clear();
    }

    private static bool SameRungs(SizedFontRung[] a, SizedFontRung[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
        {
            if ((nint)a[i].Font.Handle != (nint)b[i].Font.Handle || a[i].Scale != b[i].Scale)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The font and exact size to draw <paramref name="size"/> with: the nearest font rasterised for the
    /// current base font, snapped to its native size so glyphs are not resampled. Without one, the current font.
    /// </summary>
    public static (ImFontPtr Font, float Size) Sized(float size)
    {
        var font = ImGui.GetFont();
        if (!ladders.TryGetValue((nint)font.Handle, out var rungs))
            return (font, size);
        var baseSize = ImGui.GetFontSize();
        var wanted = MathF.Log(size / baseSize);
        var best = rungs[0];
        foreach (var rung in rungs)
        {
            if (MathF.Abs(MathF.Log(rung.Scale) - wanted) < MathF.Abs(MathF.Log(best.Scale) - wanted))
                best = rung;
        }

        return (best.Font, baseSize * best.Scale);
    }

    /// <summary>
    /// Draws a block at the cursor, advances the cursor, and reports the character under the mouse.
    /// <paramref name="highlight"/> is a [start, end) range in <see cref="RichBlock.ScanText"/>.
    /// </summary>
    public static TextHit? Draw(RichBlock block, float width, (int Start, int End)? highlight = null, bool hitTest = true)
    {
        var font = ImGui.GetFont();
        var baseSize = ImGui.GetFontSize();
        width = MathF.Max(width, baseSize * 2);
        var layout = block.CachedLayout;
        var fontId = (nint)font.Handle;
        if (layout is null || layout.Font != fontId || MathF.Abs(layout.Width - width) > 0.5f || MathF.Abs(layout.BaseSize - baseSize) > 0.01f || layout.LadderVersion != ladderVersion)
        {
            layout = BuildLayout(block, font, fontId, baseSize, width);
            block.CachedLayout = layout;
        }

        var origin = ImGui.GetCursorScreenPos() + new Vector2(0, block.MarginTopEm * baseSize);
        var drawList = ImGui.GetWindowDrawList();
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);

        // Marker (list bullet) in the indent gutter, on the first line.
        if (!string.IsNullOrEmpty(block.Marker) && layout.Lines.Count > 0)
        {
            var markerWidth = Measure(font, fontId, baseSize, block.Marker);
            var first = layout.Lines[0];
            var x = origin.X + block.IndentEm * baseSize - markerWidth - baseSize * 0.25f;
            var y = origin.Y + first.Top + first.RubyHeight + (first.Height - first.RubyHeight - baseSize) * 0.5f;
            drawList.AddText(font, baseSize, new Vector2(x, y), ImGui.GetColorU32(ImGuiCol.TextDisabled), block.Marker);
        }

        // Chip backgrounds.
        foreach (var deco in layout.Decorations)
        {
            var run = block.Runs[deco.Run];
            if (!run.Chip)
                continue;
            var line = layout.Lines[deco.Line];
            var top = origin.Y + line.Top + line.RubyHeight + (line.Height - line.RubyHeight - deco.Size * LineSpacing) * 0.5f;
            var min = new Vector2(origin.X + deco.X, top + deco.Size * 0.08f);
            var max = new Vector2(origin.X + deco.X + deco.W, top + deco.Size * LineSpacing - deco.Size * 0.08f);
            drawList.AddRectFilled(min, max, run.Background != 0 ? run.Background : 0xFF505050, deco.Size * 0.3f);
        }

        // Highlight of the scanned word.
        if (highlight is { } hl && hl.End > hl.Start)
        {
            var color = ImGui.GetColorU32(ImGuiCol.TextSelectedBg);
            foreach (var glyph in layout.Glyphs)
            {
                if (glyph.CharIndex < hl.Start || glyph.CharIndex >= hl.End)
                    continue;
                var line = layout.Lines[glyph.Line];
                var min = new Vector2(origin.X + glyph.X, origin.Y + line.Top + line.RubyHeight);
                var max = new Vector2(origin.X + glyph.X + glyph.W, origin.Y + line.Top + line.Height);
                drawList.AddRectFilled(min, max, color, 2f);
            }
        }

        // Text.
        var scan = block.ScanText;
        foreach (var seg in layout.Segments)
        {
            var run = block.Runs[seg.Run];
            var line = layout.Lines[seg.Line];
            var y = origin.Y + line.Top + line.RubyHeight + (line.Height - line.RubyHeight - seg.Size) * 0.6f;
            var pos = new Vector2(origin.X + seg.X + (run.Chip ? seg.Size * ChipPadEm : 0), y);
            var color = run.Color != 0 ? run.Color : run.Link ? DefaultLinkColor : textColor;
            var text = scan.Substring(seg.CharStart, seg.CharEnd - seg.CharStart);
            var segFont = Sized(seg.Size).Font;
            drawList.AddText(segFont, seg.Size, pos, color, text);
            if (run.Bold)
                drawList.AddText(segFont, seg.Size, pos + new Vector2(MathF.Max(1f, MathF.Round(seg.Size / 28f)), 0), color, text);
            if (run.Underline || run.Link || run.Strike)
            {
                var w = 0f;
                foreach (var g in layout.Glyphs)
                {
                    if (g.CharIndex >= seg.CharStart && g.CharIndex < seg.CharEnd)
                        w += g.W;
                }

                var ly = run.Strike ? y + seg.Size * 0.55f : y + seg.Size * 1.02f;
                if (run.Underline || run.Strike)
                    drawList.AddLine(new Vector2(pos.X, ly), new Vector2(pos.X + w, ly), color, MathF.Max(1f, seg.Size / 18f));
            }
        }

        // Ruby (furigana).
        foreach (var deco in layout.Decorations)
        {
            var run = block.Runs[deco.Run];
            if (string.IsNullOrEmpty(run.Ruby))
                continue;
            var line = layout.Lines[deco.Line];
            var (rubyFont, rubySize) = Sized(deco.Size * RubyScale);
            var rubyWidth = Measure(rubyFont, rubySize, run.Ruby);
            var x = origin.X + deco.X + (deco.W - rubyWidth) * 0.5f;
            var baseTop = origin.Y + line.Top + line.RubyHeight + (line.Height - line.RubyHeight - deco.Size) * 0.6f;
            drawList.AddText(rubyFont, rubySize, new Vector2(x, baseTop - rubySize * 1.05f), run.Color != 0 ? run.Color : textColor, run.Ruby);
        }

        var size = new Vector2(width, layout.Height + block.MarginTopEm * baseSize);
        ImGui.Dummy(size);

        if (!hitTest || !ImGui.IsWindowHovered(ImGuiHoveredFlags.None))
            return null;
        var mouse = ImGui.GetMousePos();
        if (mouse.X < origin.X || mouse.Y < origin.Y || mouse.X > origin.X + width || mouse.Y > origin.Y + layout.Height)
            return null;
        foreach (var glyph in layout.Glyphs)
        {
            var line = layout.Lines[glyph.Line];
            var min = new Vector2(origin.X + glyph.X, origin.Y + line.Top);
            var max = new Vector2(origin.X + glyph.X + glyph.W, origin.Y + line.Top + line.Height);
            if (mouse.X >= min.X && mouse.X < max.X && mouse.Y >= min.Y && mouse.Y < max.Y)
                return new TextHit(block, glyph.CharIndex, min with { Y = min.Y + line.RubyHeight }, max);
        }

        return null;
    }

    public static float Measure(ImFontPtr font, float size, string text) => Measure(font, (nint)font.Handle, size, text);

    private static float Measure(ImFontPtr font, nint fontId, float size, string text)
    {
        var w = 0f;
        for (var i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }

            w += Advance(font, fontId, cp) * size;
        }

        return w;
    }

    /// <summary>Glyph advance for a 1px font (multiply by the size).</summary>
    private static float Advance(ImFontPtr font, nint fontId, int codepoint)
    {
        if (Advances.TryGetValue((fontId, codepoint), out var advance))
            return advance;
        if (Advances.Count > 50_000)
            Advances.Clear();
        if (codepoint <= 0xFFFF)
        {
            advance = font.GetCharAdvance((ushort)codepoint) / font.FontSize;
        }
        else
        {
            var size = ImGui.CalcTextSizeA(font, font.FontSize, float.MaxValue, 0f, char.ConvertFromUtf32(codepoint), out _);
            advance = size.X / font.FontSize;
        }

        Advances[(fontId, codepoint)] = advance;
        return advance;
    }

    private static bool IsWordChar(int cp) =>
        cp < 0x2E80 && !char.IsWhiteSpace((char)Math.Min(cp, 0xFFFF)) && cp != '\n';

    private struct Unit
    {
        public int Run;
        public int Start; // in run text
        public int End;
        public bool Atomic;
        public bool Space;
        public bool Newline;
        public float W;
    }

    private static RichBlock.Layout BuildLayout(RichBlock block, ImFontPtr font, nint fontId, float baseSize, float width)
    {
        var layout = new RichBlock.Layout { Width = width, BaseSize = baseSize, Font = fontId, LadderVersion = ladderVersion };
        var units = new List<Unit>();
        var runOffsets = new int[block.Runs.Count];
        var offset = 0;
        for (var r = 0; r < block.Runs.Count; r++)
        {
            runOffsets[r] = offset;
            var run = block.Runs[r];
            var text = run.Text;
            offset += text.Length;
            var (runFont, size) = Sized(baseSize * run.Scale);
            var runFontId = (nint)runFont.Handle;
            if (text.Length == 0)
                continue;
            if (run.Chip || run.Ruby is not null)
            {
                var w = Measure(runFont, runFontId, size, text);
                if (run.Ruby is not null)
                {
                    var (rubyFont, rubySize) = Sized(size * RubyScale);
                    w = MathF.Max(w, Measure(rubyFont, rubySize, run.Ruby));
                }

                if (run.Chip)
                    w += size * ChipPadEm * 2;
                units.Add(new Unit { Run = r, Start = 0, End = text.Length, Atomic = true, W = w });
                continue;
            }

            var i = 0;
            while (i < text.Length)
            {
                int cp = text[i];
                var len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
                if (len == 2)
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                if (cp == '\n')
                {
                    units.Add(new Unit { Run = r, Start = i, End = i + 1, Newline = true });
                    i++;
                    continue;
                }

                if (char.IsWhiteSpace((char)Math.Min(cp, 0xFFFF)))
                {
                    units.Add(new Unit { Run = r, Start = i, End = i + len, Space = true, W = Advance(runFont, runFontId, cp) * size });
                    i += len;
                    continue;
                }

                if (!IsWordChar(cp))
                {
                    units.Add(new Unit { Run = r, Start = i, End = i + len, W = Advance(runFont, runFontId, cp) * size });
                    i += len;
                    continue;
                }

                var start = i;
                var w2 = 0f;
                while (i < text.Length)
                {
                    int c = text[i];
                    var l = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
                    if (l == 2)
                        c = char.ConvertToUtf32(text[i], text[i + 1]);
                    if (!IsWordChar(c))
                        break;
                    w2 += Advance(runFont, runFontId, c) * size;
                    i += l;
                }

                units.Add(new Unit { Run = r, Start = start, End = i, W = w2 });
            }
        }

        var indent = block.IndentEm * baseSize;
        var lineIndex = 0;
        var x = indent;
        var lineHasContent = false;
        var lineMaxSize = baseSize;
        var lineHasRuby = false;
        var lines = layout.Lines;
        float top = 0;

        void FinishLine()
        {
            var height = lineMaxSize * LineSpacing;
            var ruby = lineHasRuby ? lineMaxSize * RubyScale * 1.1f : 0f;
            lines.Add(new RichBlock.Line { Top = top, Height = height + ruby, RubyHeight = ruby });
            top += height + ruby;
            lineIndex++;
            x = indent;
            lineHasContent = false;
            lineMaxSize = baseSize;
            lineHasRuby = false;
        }

        void PlaceGlyphs(Unit unit, bool breakAnywhere)
        {
            var run = block.Runs[unit.Run];
            var (runFont, size) = Sized(baseSize * run.Scale);
            var runFontId = (nint)runFont.Handle;
            var text = run.Text;
            if (unit.Atomic)
            {
                layout.Decorations.Add(new RichBlock.Decoration { Run = unit.Run, Line = lineIndex, X = x, W = unit.W, Size = size });
                // Ruby/chip glyphs share the unit's width evenly for hit testing.
                var count = 0;
                for (var i = unit.Start; i < unit.End; i += char.IsHighSurrogate(text[i]) && i + 1 < unit.End ? 2 : 1)
                    count++;
                var each = unit.W / Math.Max(1, count);
                var gx = x;
                for (var i = unit.Start; i < unit.End;)
                {
                    var l = char.IsHighSurrogate(text[i]) && i + 1 < unit.End ? 2 : 1;
                    layout.Glyphs.Add(new RichBlock.Glyph { CharIndex = runOffsets[unit.Run] + i, Length = l, Line = lineIndex, X = gx, W = each, Size = size });
                    gx += each;
                    i += l;
                }

                x += unit.W;
                lineMaxSize = MathF.Max(lineMaxSize, size);
                lineHasRuby |= run.Ruby is not null;
                lineHasContent = true;
                return;
            }

            for (var i = unit.Start; i < unit.End;)
            {
                int cp = text[i];
                var l = char.IsHighSurrogate(text[i]) && i + 1 < unit.End ? 2 : 1;
                if (l == 2)
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                var w = Advance(runFont, runFontId, cp) * size;
                if (breakAnywhere && lineHasContent && x + w > width)
                    FinishLine();
                layout.Glyphs.Add(new RichBlock.Glyph { CharIndex = runOffsets[unit.Run] + i, Length = l, Line = lineIndex, X = x, W = w, Size = size });
                x += w;
                lineMaxSize = MathF.Max(lineMaxSize, size);
                lineHasContent = true;
                i += l;
            }
        }

        foreach (var unit in units)
        {
            if (unit.Newline)
            {
                FinishLine();
                continue;
            }

            if (unit.Space && !lineHasContent)
            {
                continue;
            }

            if (lineHasContent && x + unit.W > width && !unit.Space)
                FinishLine();
            PlaceGlyphs(unit, !unit.Atomic && unit.W > width - indent);
        }

        if (lineHasContent || lines.Count == 0)
            FinishLine();
        layout.Height = top;

        // Segments: consecutive glyphs of the same run on the same line.
        RichBlock.Segment? current = null;
        var runOf = new int[offset + 1];
        for (var r = 0; r < block.Runs.Count; r++)
        {
            for (var i = 0; i < block.Runs[r].Text.Length; i++)
                runOf[runOffsets[r] + i] = r;
        }

        foreach (var glyph in layout.Glyphs)
        {
            var run = runOf[glyph.CharIndex];
            if (current is { } c && c.Run == run && c.Line == glyph.Line && c.CharEnd == glyph.CharIndex)
            {
                current = c with { CharEnd = glyph.CharIndex + glyph.Length };
                continue;
            }

            if (current is { } done)
                layout.Segments.Add(done);
            current = new RichBlock.Segment
            {
                Run = run,
                Line = glyph.Line,
                X = block.Runs[run].Chip
                    ? FindDecorationX(layout, run, glyph.Line, glyph.X)
                    : block.Runs[run].Ruby is not null
                        ? CenteredRubyBaseX(layout, block.Runs[run], run, glyph.Line, glyph.X, glyph.Size)
                        : glyph.X,
                Size = glyph.Size,
                CharStart = glyph.CharIndex,
                CharEnd = glyph.CharIndex + glyph.Length,
            };
        }

        if (current is { } last)
            layout.Segments.Add(last);
        return layout;
    }

    private static float CenteredRubyBaseX(RichBlock.Layout layout, RichRun run, int runIndex, int line, float fallback, float size)
    {
        foreach (var d in layout.Decorations)
        {
            if (d.Run == runIndex && d.Line == line)
                return d.X + (d.W - Measure(Sized(size).Font, size, run.Text)) * 0.5f;
        }

        return fallback;
    }

    private static float FindDecorationX(RichBlock.Layout layout, int run, int line, float fallback)
    {
        foreach (var d in layout.Decorations)
        {
            if (d.Run == run && d.Line == line)
                return d.X;
        }

        return fallback;
    }
}
