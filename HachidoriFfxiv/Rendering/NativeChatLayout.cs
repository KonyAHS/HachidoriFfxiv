using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace HachidoriFfxiv.Rendering;

internal static class NativeChatLayout
{
    // The log viewer has already wrapped the text and chosen the scroll position.
    // The node buffer can still include older rows above the visible window.
    public static IReadOnlyList<(string Text, NativeTextLayout Layout)> Build(ReadOnlySpan<byte> text,
        float width, float height, int alignment, float glyphHeight, float lineAdvance, bool bottomAligned,
        Func<ReadOnlySpan<byte>, float> measure, float visibleTop = 0f, float visibleHeight = -1f)
    {
        var lines = new List<(int Start, int End, string Text, (int Index, int Start, int End)[] Glyphs)>();
        var glyphs = new List<(int Index, int Start, int End)>();
        var plain = new StringBuilder();
        Span<char> utf16 = stackalloc char[2];
        var lineStart = 0;

        foreach (var entry in new ReadOnlySeStringSpan(text).GetOffsetEnumerator())
        {
            var offset = entry.Offset;
            var payload = entry.Payload;
            if (payload.Type == ReadOnlySePayloadType.Macro && payload.MacroCode == MacroCode.NewLine)
            {
                EndLine(offset, offset + payload.EnvelopeByteLength);
                continue;
            }
            if (payload.Type != ReadOnlySePayloadType.Text) continue;

            var bytes = payload.Body;
            for (var i = 0; i < bytes.Length;)
            {
                var start = offset + i;
                if (bytes[i] is (byte)'\r' or (byte)'\n')
                {
                    i += bytes[i] == '\r' && i + 1 < bytes.Length && bytes[i + 1] == '\n' ? 2 : 1;
                    EndLine(start, offset + i);
                    continue;
                }
                Rune.DecodeFromUtf8(bytes[i..], out var rune, out var consumed);
                i += consumed;
                glyphs.Add((plain.Length, start, offset + i));
                plain.Append(utf16[..rune.EncodeToUtf16(utf16)]);
            }
        }
        if (lineStart < text.Length)
            EndLine(text.Length, text.Length);

        var parts = new List<(string, NativeTextLayout)>();
        if (lines.Count == 0 || lineAdvance <= 0f)
            return parts;

        var viewTop = Math.Max(0f, visibleTop);
        var viewHeight = visibleHeight > 0f ? Math.Min(height - viewTop, visibleHeight) : height;
        if (viewHeight <= 0f)
            return parts;

        // While scrolling history, start with the first buffered row. At the live
        // end, place the last row against the visible bottom and let the node clip the top.
        var visibleCount = Math.Min(lines.Count, (int)Math.Ceiling(viewHeight / lineAdvance));
        var contentHeight = glyphHeight + (visibleCount - 1) * lineAdvance;
        var firstRow = bottomAligned ? lines.Count - visibleCount : 0;
        var y = bottomAligned
            ? viewTop + viewHeight - contentHeight
            : viewTop + Math.Max(0, viewHeight - contentHeight) * Math.Clamp(alignment / 3, 0, 2) / 2f;
        var viewBottom = viewTop + viewHeight;
        var horizontal = Math.Clamp(alignment % 3, 0, 2);
        for (var i = firstRow; i < firstRow + visibleCount; i++)
        {
            if (y >= viewBottom) break;
            var line = lines[i];
            if (line.Text.EnumerateRunes().Any(r => Japanese.IsJapanese(r.Value)))
            {
                var x = horizontal == 0 ? 0 : Math.Max(0, width - measure(text[line.Start..line.End])) * horizontal / 2f;
                var cells = new List<NativeTextLayout.Cell>(line.Glyphs.Length);
                var previousEnd = line.Start;
                var right = 0f;
                foreach (var glyph in line.Glyphs)
                {
                    // An inline icon advances the pen but has no character to look up.
                    var left = glyph.Start == previousEnd ? right : measure(text[line.Start..glyph.Start]);
                    right = measure(text[line.Start..glyph.End]);
                    previousEnd = glyph.End;
                    if (right > left)
                        cells.Add(new(glyph.Index, new(x + left, y), new(x + right, y + glyphHeight)));
                }
                if (cells.Count > 0) parts.Add((line.Text, new NativeTextLayout(cells)));
            }
            y += lineAdvance;
        }
        return parts;

        void EndLine(int end, int next)
        {
            lines.Add((lineStart, end, plain.ToString(), glyphs.ToArray()));
            plain.Clear();
            glyphs.Clear();
            lineStart = next;
        }
    }
}
