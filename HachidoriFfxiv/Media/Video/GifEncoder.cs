using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HachidoriFfxiv.Media;

/// <summary>One RGBA frame: rows of Width × 4 bytes from the start of the array, which may be longer.</summary>
public sealed record GifFrame(byte[] Rgba, int Width, int Height);

/// <summary>
/// An animated GIF89a encoder: a 256-colour palette per frame by median cut, a light ordered
/// dither against banding, and LZW compression. Loops forever.
/// </summary>
public static class GifEncoder
{
    private static readonly int[] Bayer4 = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    public static byte[] Encode(IReadOnlyList<GifFrame> frames, int delayMs)
    {
        if (frames.Count == 0)
            throw new ArgumentException("No frames to encode.", nameof(frames));
        var width = frames[0].Width;
        var height = frames[0].Height;
        using var output = new MemoryStream();
        var w = new BinaryWriter(output);
        w.Write("GIF89a"u8);
        w.Write((ushort)width);
        w.Write((ushort)height);
        w.Write((byte)0x00); // no global colour table
        w.Write((byte)0);
        w.Write((byte)0);
        // NETSCAPE2.0: loop forever.
        w.Write([0x21, 0xFF, 0x0B]);
        w.Write("NETSCAPE2.0"u8);
        w.Write([0x03, 0x01, 0x00, 0x00, 0x00]);

        var delay = (ushort)Math.Clamp((delayMs + 5) / 10, 2, 65535);
        foreach (var frame in frames)
        {
            if (frame.Width != width || frame.Height != height)
                throw new ArgumentException("All frames must be the same size.", nameof(frames));
            var (palette, lookup) = BuildPalette(frame.Rgba, width * height * 4);
            var indices = Index(frame, lookup);

            w.Write([0x21, 0xF9, 0x04, 0x04]); // graphic control: dispose "do not dispose", no transparency
            w.Write(delay);
            w.Write((byte)0);
            w.Write((byte)0);

            w.Write((byte)0x2C);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)width);
            w.Write((ushort)height);
            w.Write((byte)0x87); // local colour table, 256 entries
            for (var i = 0; i < 256; i++)
            {
                var c = i < palette.Count ? palette[i] : 0;
                w.Write((byte)(c >> 16));
                w.Write((byte)(c >> 8));
                w.Write((byte)c);
            }

            w.Write((byte)8); // LZW minimum code size
            WriteSubBlocks(w, Lzw(indices));
        }

        w.Write((byte)0x3B);
        w.Flush();
        return output.ToArray();
    }

    /// <summary>A median-cut palette over a 5-bit-per-channel histogram, and its 32768-entry lookup.</summary>
    private static (List<int> Palette, byte[] Lookup) BuildPalette(byte[] rgba, int length)
    {
        // Captured frames may be longer than the image (rows packed in place from a padded readback).
        var histogram = new int[32768];
        for (var i = 0; i + 3 < length; i += 4)
            histogram[(rgba[i] >> 3 << 10) | (rgba[i + 1] >> 3 << 5) | (rgba[i + 2] >> 3)]++;
        var colours = Enumerable.Range(0, 32768).Where(c => histogram[c] > 0).ToList();

        var boxes = new List<List<int>> { colours };
        while (boxes.Count < 256)
        {
            // Split the box with the widest channel range (weighted by population).
            var best = -1;
            var bestScore = 0L;
            var bestChannel = 0;
            for (var b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2) continue;
                for (var ch = 0; ch < 3; ch++)
                {
                    var shift = 10 - ch * 5;
                    int min = 31, max = 0;
                    long count = 0;
                    foreach (var c in boxes[b])
                    {
                        var v = (c >> shift) & 31;
                        min = Math.Min(min, v);
                        max = Math.Max(max, v);
                        count += histogram[c];
                    }

                    var score = (max - min) * (long)Math.Sqrt(count);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = b;
                        bestChannel = shift;
                    }
                }
            }

            if (best < 0)
                break;
            var box = boxes[best];
            var channel = bestChannel;
            box.Sort((a, c) => ((a >> channel) & 31).CompareTo((c >> channel) & 31));
            long total = box.Sum(c => (long)histogram[c]);
            long running = 0;
            var split = 1;
            for (var i = 0; i < box.Count - 1; i++)
            {
                running += histogram[box[i]];
                if (running * 2 >= total) { split = i + 1; break; }
            }

            boxes[best] = box.GetRange(0, split);
            boxes.Add(box.GetRange(split, box.Count - split));
        }

        var palette = new List<int>();
        foreach (var box in boxes.Where(b => b.Count > 0))
        {
            long r = 0, g = 0, bl = 0, n = 0;
            foreach (var c in box)
            {
                var h = histogram[c];
                r += ((c >> 10) & 31) * h;
                g += ((c >> 5) & 31) * h;
                bl += (c & 31) * h;
                n += h;
            }

            n = Math.Max(1, n);
            palette.Add((int)(r * 255 / (31 * n)) << 16 | (int)(g * 255 / (31 * n)) << 8 | (int)(bl * 255 / (31 * n)));
        }

        if (palette.Count == 0)
            palette.Add(0);

        var lookup = new byte[32768];
        for (var c = 0; c < 32768; c++)
        {
            var r = ((c >> 10) & 31) * 255 / 31;
            var g = ((c >> 5) & 31) * 255 / 31;
            var b = (c & 31) * 255 / 31;
            var bestIndex = 0;
            var bestDistance = int.MaxValue;
            for (var i = 0; i < palette.Count; i++)
            {
                var p = palette[i];
                int dr = ((p >> 16) & 255) - r, dg = ((p >> 8) & 255) - g, db = (p & 255) - b;
                var d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                if (d < bestDistance) { bestDistance = d; bestIndex = i; }
            }

            lookup[c] = (byte)bestIndex;
        }

        return (palette, lookup);
    }

    private static byte[] Index(GifFrame frame, byte[] lookup)
    {
        var indices = new byte[frame.Width * frame.Height];
        var rgba = frame.Rgba;
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var i = (y * frame.Width + x) * 4;
                var dither = Bayer4[(y & 3) * 4 + (x & 3)] / 2 - 4; // about ±4 levels
                int r = Math.Clamp(rgba[i] + dither, 0, 255), g = Math.Clamp(rgba[i + 1] + dither, 0, 255), b = Math.Clamp(rgba[i + 2] + dither, 0, 255);
                indices[y * frame.Width + x] = lookup[(r >> 3 << 10) | (g >> 3 << 5) | (b >> 3)];
            }
        }

        return indices;
    }

    /// <summary>GIF's variable-width LZW with 8-bit symbols.</summary>
    private static byte[] Lzw(byte[] indices)
    {
        const int clear = 256, end = 257;
        var output = new List<byte>(indices.Length / 2);
        var codeSize = 9;
        var next = 258;
        var bitBuffer = 0;
        var bitCount = 0;
        void Emit(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                output.Add((byte)bitBuffer);
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        var table = new Dictionary<int, int>(4096);
        Emit(clear);
        if (indices.Length == 0)
        {
            Emit(end);
            if (bitCount > 0) output.Add((byte)bitBuffer);
            return output.ToArray();
        }

        var prefix = (int)indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            var k = indices[i];
            var key = (prefix << 8) | k;
            if (table.TryGetValue(key, out var code))
            {
                prefix = code;
                continue;
            }

            Emit(prefix);
            if (next < 4096)
            {
                table[key] = next++;
                if (next > (1 << codeSize) && codeSize < 12)
                    codeSize++;
            }
            else
            {
                Emit(clear);
                table.Clear();
                next = 258;
                codeSize = 9;
            }

            prefix = k;
        }

        Emit(prefix);
        Emit(end);
        if (bitCount > 0)
            output.Add((byte)bitBuffer);
        return output.ToArray();
    }

    private static void WriteSubBlocks(BinaryWriter w, byte[] data)
    {
        for (var i = 0; i < data.Length; i += 255)
        {
            var n = Math.Min(255, data.Length - i);
            w.Write((byte)n);
            w.Write(data, i, n);
        }

        w.Write((byte)0);
    }
}
