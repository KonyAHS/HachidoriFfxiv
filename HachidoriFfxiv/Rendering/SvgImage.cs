using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace HachidoriFfxiv.Rendering;

/// <summary>A shape of an SVG laid out in screen space: an outline and/or a fill, or a line of text.</summary>
public abstract record SvgPrimitive;

/// <summary>Points of a shape; Stroke and Fill are ImGui colours (ABGR), null for none, 0 for the current text colour.</summary>
public sealed record SvgShape(IReadOnlyList<Vector2> Points, bool Closed, uint? Stroke, float StrokeWidth, uint? Fill, IReadOnlyList<int> Triangles) : SvgPrimitive;

/// <summary>Text whose baseline starts at Baseline after Anchor ("start", "middle", "end") is applied.</summary>
public sealed record SvgText(Vector2 Baseline, float Size, uint? Fill, string Text, string Anchor) : SvgPrimitive;

/// <summary>
/// Dictionary images in SVG (daijirin's 漢/呉 badges, KanjiVG stroke diagrams), which
/// Windows' image decoders cannot read. The common subset is interpreted and drawn
/// with ImGui's draw list: shapes, paths (curves and arcs flattened), polylines,
/// text, groups with transforms, presentation attributes and inline style, and the
/// viewBox. Gradients, patterns, masks, filters and clip paths are left out.
/// </summary>
public sealed class SvgImage
{
    /// <summary>The colour that stands for currentColor: the popup's text colour.</summary>
    public const uint CurrentColor = 0;

    private const int CurveSegments = 12;
    private readonly XElement root;
    private readonly Vector4 viewBox;

    private SvgImage(XElement root, float width, float height, Vector4 viewBox)
    {
        this.root = root;
        Width = width;
        Height = height;
        this.viewBox = viewBox;
    }

    /// <summary>The intrinsic size in CSS pixels.</summary>
    public float Width { get; }
    public float Height { get; }

    public static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        var head = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 512)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
               || (head.StartsWith("<?xml", StringComparison.Ordinal) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
               || (head.StartsWith("<!--", StringComparison.Ordinal) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
               || (head.StartsWith("<!DOCTYPE svg", StringComparison.OrdinalIgnoreCase));
    }

    public static SvgImage Parse(byte[] bytes)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
        using var stream = new System.IO.MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader);
        var svg = document.Root;
        if (svg is null || svg.Name.LocalName != "svg") throw new FormatException("The image is not an SVG document.");
        var box = Numbers(Attr(svg, "viewBox"));
        var width = Length(Attr(svg, "width"), 0);
        var height = Length(Attr(svg, "height"), 0);
        var viewBox = box.Count == 4 && box[2] > 0 && box[3] > 0
            ? new Vector4(box[0], box[1], box[2], box[3])
            : new Vector4(0, 0, width > 0 ? width : 300, height > 0 ? height : 150);
        if (width <= 0 && height <= 0) (width, height) = (viewBox.Z, viewBox.W);
        else if (width <= 0) width = height * viewBox.Z / viewBox.W;
        else if (height <= 0) height = width * viewBox.W / viewBox.Z;
        return new SvgImage(svg, width, height, viewBox);
    }

    // --------------------------------------------------------------- layout

    private sealed record Paint(uint? Fill, uint? Stroke, float StrokeWidth, float FontSize, string Anchor, float Opacity);

    /// <summary>The image's shapes in screen space, drawn into the rectangle at origin with size (preserveAspectRatio xMidYMid meet).</summary>
    public List<SvgPrimitive> Layout(Vector2 origin, Vector2 size)
    {
        var scale = Math.Min(size.X / viewBox.Z, size.Y / viewBox.W);
        var offset = origin + (size - new Vector2(viewBox.Z, viewBox.W) * scale) / 2 - new Vector2(viewBox.X, viewBox.Y) * scale;
        var transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(offset);
        var output = new List<SvgPrimitive>();
        var paint = new Paint(0xFF000000, null, 1, 16, "start", 1);
        foreach (var child in root.Elements()) Walk(child, Inherit(root, paint), transform, scale, output);
        return output;
    }

    private void Walk(XElement element, Paint inherited, Matrix3x2 transform, float scale, List<SvgPrimitive> output)
    {
        var name = element.Name.LocalName;
        if (name is "defs" or "style" or "title" or "desc" or "metadata" or "clipPath" or "mask" or "linearGradient" or "radialGradient" or "pattern" or "symbol") return;
        if (Attr(element, "display") == "none" || StyleOf(element).GetValueOrDefault("display") == "none") return;
        var paint = Inherit(element, inherited);
        var local = Transform(Attr(element, "transform")) * transform;
        var stroke = paint.StrokeWidth * scale * MathF.Sqrt(MathF.Abs(local.GetDeterminant()) / Math.Max(1e-6f, scale * scale));
        switch (name)
        {
            case "g":
            case "a":
            case "svg":
                foreach (var child in element.Elements()) Walk(child, paint, local, scale, output);
                break;
            case "rect":
            {
                float x = X(element, "x"), y = Y(element, "y"), w = X(element, "width"), h = Y(element, "height");
                if (w <= 0 || h <= 0) break;
                var rx = Attr(element, "rx") is null ? Attr(element, "ry") is null ? 0 : Y(element, "ry") : X(element, "rx");
                var ry = Attr(element, "ry") is null ? rx : Y(element, "ry");
                rx = Math.Min(rx, w / 2);
                ry = Math.Min(ry, h / 2);
                var points = new List<Vector2>();
                if (rx <= 0 || ry <= 0)
                {
                    points.AddRange([new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)]);
                }
                else
                {
                    Arc(points, new(x + w - rx, y + ry), rx, ry, -MathF.PI / 2, 0);
                    Arc(points, new(x + w - rx, y + h - ry), rx, ry, 0, MathF.PI / 2);
                    Arc(points, new(x + rx, y + h - ry), rx, ry, MathF.PI / 2, MathF.PI);
                    Arc(points, new(x + rx, y + ry), rx, ry, MathF.PI, MathF.PI * 1.5f);
                }
                Emit(output, points, true, paint, stroke, local);
                break;
            }
            case "circle":
            case "ellipse":
            {
                var cx = X(element, "cx");
                var cy = Y(element, "cy");
                var rx = name == "circle" ? X(element, "r") : X(element, "rx");
                var ry = name == "circle" ? rx : Y(element, "ry");
                if (rx <= 0 || ry <= 0) break;
                var points = new List<Vector2>();
                Arc(points, new(cx, cy), rx, ry, 0, MathF.PI * 2, 32);
                points.RemoveAt(points.Count - 1);
                Emit(output, points, true, paint, stroke, local);
                break;
            }
            case "line":
                Emit(output, [new(X(element, "x1"), Y(element, "y1")), new(X(element, "x2"), Y(element, "y2"))], false, paint with { Fill = null }, stroke, local);
                break;
            case "polyline":
            case "polygon":
            {
                var n = Numbers(Attr(element, "points"));
                var points = Enumerable.Range(0, n.Count / 2).Select(i => new Vector2(n[2 * i], n[2 * i + 1])).ToList();
                Emit(output, points, name == "polygon", paint, stroke, local);
                break;
            }
            case "path":
                foreach (var (points, closed) in PathData(Attr(element, "d") ?? string.Empty))
                    Emit(output, points, closed, paint, stroke, local);
                break;
            case "text":
                Text(element, paint, local, scale, output);
                break;
        }
    }

    private void Text(XElement element, Paint paint, Matrix3x2 transform, float scale, List<SvgPrimitive> output)
    {
        var text = string.Concat(element.DescendantNodes().OfType<XText>().Select(t => t.Value)).Trim();
        if (text.Length == 0 || paint.Fill is null) return;
        var position = new Vector2(X(element, "x") + Em(Attr(element, "dx"), paint.FontSize, viewBox.Z), Y(element, "y") + Em(Attr(element, "dy"), paint.FontSize, viewBox.W));
        var size = paint.FontSize * scale * MathF.Sqrt(MathF.Abs(transform.GetDeterminant()) / Math.Max(1e-6f, scale * scale));
        output.Add(new SvgText(Vector2.Transform(position, transform), size, Faded(paint.Fill, paint.Opacity), text, paint.Anchor));
    }

    private static void Emit(List<SvgPrimitive> output, List<Vector2> points, bool closed, Paint paint, float strokeWidth, Matrix3x2 transform)
    {
        if (points.Count < 2) return;
        var screen = points.Select(p => Vector2.Transform(p, transform)).ToList();
        var fill = closed || points.Count > 2 ? Faded(paint.Fill, paint.Opacity) : null;
        var stroke = Faded(paint.Stroke, paint.Opacity);
        if (fill is null && stroke is null) return;
        output.Add(new SvgShape(screen, closed, stroke, Math.Max(0.5f, strokeWidth), fill, fill is null ? [] : Triangulate(screen)));
    }

    private static uint? Faded(uint? color, float opacity)
    {
        if (color is not { } c || c == CurrentColor || opacity >= 1) return color;
        var alpha = (uint)Math.Clamp((c >> 24) * opacity, 0, 255);
        return (c & 0x00FFFFFF) | (alpha << 24);
    }

    // --------------------------------------------------------------- paint

    private static Paint Inherit(XElement element, Paint parent)
    {
        var style = StyleOf(element);
        string? Get(string name) => style.TryGetValue(name, out var value) ? value : Attr(element, name);
        var paint = parent;
        if (Get("fill") is { } fill) paint = paint with { Fill = Color(fill, parent.Fill) };
        if (Get("stroke") is { } stroke) paint = paint with { Stroke = Color(stroke, parent.Stroke) };
        if (Get("stroke-width") is { } width) paint = paint with { StrokeWidth = Length(width, parent.StrokeWidth) };
        if (Get("font-size") is { } size) paint = paint with { FontSize = Em(size, parent.FontSize, parent.FontSize) is var s && s > 0 ? s : parent.FontSize };
        if (Get("text-anchor") is { } anchor) paint = paint with { Anchor = anchor.Trim() };
        var opacity = 1f;
        foreach (var key in new[] { "opacity", "fill-opacity" })
            if (Get(key) is { } o && float.TryParse(o, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) opacity *= Math.Clamp(v, 0, 1);
        if (opacity < 1) paint = paint with { Opacity = paint.Opacity * opacity };
        return paint;
    }

    private static Dictionary<string, string> StyleOf(XElement element)
    {
        var style = new Dictionary<string, string>();
        foreach (var part in (Attr(element, "style") ?? string.Empty).Split(';'))
        {
            var colon = part.IndexOf(':');
            if (colon > 0) style[part[..colon].Trim().ToLowerInvariant()] = part[(colon + 1)..].Trim();
        }
        return style;
    }

    private static readonly Dictionary<string, uint> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = 0x000000, ["white"] = 0xFFFFFF, ["red"] = 0xFF0000, ["green"] = 0x008000, ["blue"] = 0x0000FF,
        ["gray"] = 0x808080, ["grey"] = 0x808080, ["silver"] = 0xC0C0C0, ["maroon"] = 0x800000, ["purple"] = 0x800080,
        ["navy"] = 0x000080, ["teal"] = 0x008080, ["olive"] = 0x808000, ["orange"] = 0xFFA500, ["yellow"] = 0xFFFF00,
        ["brown"] = 0xA52A2A, ["darkgray"] = 0xA9A9A9, ["darkgrey"] = 0xA9A9A9, ["lightgray"] = 0xD3D3D3, ["lightgrey"] = 0xD3D3D3,
        ["dimgray"] = 0x696969, ["dimgrey"] = 0x696969, ["crimson"] = 0xDC143C, ["darkred"] = 0x8B0000, ["lime"] = 0x00FF00,
    };

    /// <summary>An SVG paint as an ImGui colour: null for none, <see cref="CurrentColor"/> for currentColor.</summary>
    public static uint? Color(string value, uint? inherited)
    {
        var v = value.Trim();
        if (v.Length == 0 || v.Equals("inherit", StringComparison.OrdinalIgnoreCase)) return inherited;
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase) || v.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return null;
        if (v.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return CurrentColor;
        uint rgb;
        float alpha = 1;
        if (v.StartsWith('#'))
        {
            var hex = v[1..];
            if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(c => $"{c}{c}"));
            if (hex.Length is not (6 or 8) || !uint.TryParse(hex[..6], NumberStyles.HexNumber, null, out rgb)) return inherited;
            if (hex.Length == 8) alpha = Convert.ToInt32(hex[6..8], 16) / 255f;
        }
        else if (v.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var n = Numbers(v[(v.IndexOf('(') + 1)..].TrimEnd(')').Replace('/', ' '), percent: true);
            if (n.Count < 3) return inherited;
            rgb = ((uint)Math.Clamp(n[0], 0, 255) << 16) | ((uint)Math.Clamp(n[1], 0, 255) << 8) | (uint)Math.Clamp(n[2], 0, 255);
            if (n.Count > 3) alpha = Math.Clamp(n[3], 0, 1);
        }
        else if (!NamedColors.TryGetValue(v, out rgb))
        {
            return 0xFF000000; // An unknown named colour still paints: black is the least surprising.
        }
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        var a = (uint)Math.Round(alpha * 255);
        var abgr = (a << 24) | (b << 16) | (g << 8) | r;
        return abgr == CurrentColor ? 0x00000001 : abgr; // never collide with the currentColor marker
    }

    // ------------------------------------------------------------ geometry

    private float X(XElement element, string name) => Coordinate(Attr(element, name), viewBox.Z);
    private float Y(XElement element, string name) => Coordinate(Attr(element, name), viewBox.W);

    private static float Coordinate(string? value, float reference)
    {
        if (value is null) return 0;
        var v = value.Trim();
        return v.EndsWith('%') && float.TryParse(v[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p / 100 * reference : Length(v, 0);
    }

    private static float Em(string? value, float fontSize, float reference)
    {
        if (value is null) return 0;
        var v = value.Trim();
        if (v.EndsWith("em", StringComparison.OrdinalIgnoreCase) && float.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var em)) return em * fontSize;
        return Coordinate(v, reference);
    }

    private static float Length(string? value, float fallback)
    {
        if (value is null) return fallback;
        var v = value.Trim();
        foreach (var unit in new[] { "px", "pt", "em" })
        {
            if (!v.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) continue;
            var factor = unit == "pt" ? 4f / 3 : unit == "em" ? 16 : 1;
            return float.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n * factor : fallback;
        }
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : fallback;
    }

    private static string? Attr(XElement element, string name) => element.Attribute(name)?.Value;

    private static List<float> Numbers(string? text, bool percent = false)
    {
        var numbers = new List<float>();
        if (text is null) return numbers;
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
            var start = i;
            if (i < text.Length && text[i] is '+' or '-') i++;
            var dot = false;
            var exponent = false;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsAsciiDigit(c)) i++;
                else if (c == '.' && !dot && !exponent) { dot = true; i++; }
                else if (c is 'e' or 'E' && !exponent && i > start) { exponent = true; i++; if (i < text.Length && text[i] is '+' or '-') i++; }
                else break;
            }
            if (i == start) { i++; continue; }
            if (float.TryParse(text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                if (percent && i < text.Length && text[i] == '%') { n = n / 100 * 255; i++; }
                numbers.Add(n);
            }
        }
        return numbers;
    }

    private static Matrix3x2 Transform(string? text)
    {
        var result = Matrix3x2.Identity;
        if (string.IsNullOrWhiteSpace(text)) return result;
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('(', i);
            if (open < 0) break;
            var close = text.IndexOf(')', open);
            if (close < 0) break;
            var name = text[i..open].Trim(' ', ',', '\t', '\n', '\r');
            var n = Numbers(text[(open + 1)..close]);
            var m = name switch
            {
                "translate" when n.Count >= 1 => Matrix3x2.CreateTranslation(n[0], n.Count > 1 ? n[1] : 0),
                "scale" when n.Count >= 1 => Matrix3x2.CreateScale(n[0], n.Count > 1 ? n[1] : n[0]),
                "rotate" when n.Count >= 1 => Matrix3x2.CreateRotation(n[0] * MathF.PI / 180, n.Count >= 3 ? new Vector2(n[1], n[2]) : Vector2.Zero),
                "matrix" when n.Count >= 6 => new Matrix3x2(n[0], n[1], n[2], n[3], n[4], n[5]),
                "skewX" when n.Count >= 1 => Matrix3x2.CreateSkew(n[0] * MathF.PI / 180, 0),
                "skewY" when n.Count >= 1 => Matrix3x2.CreateSkew(0, n[0] * MathF.PI / 180),
                _ => Matrix3x2.Identity,
            };
            // The rightmost transform applies first.
            result = m * result;
            i = close + 1;
        }
        return result;
    }

    private static void Arc(List<Vector2> points, Vector2 center, float rx, float ry, float from, float to, int segments = 6)
    {
        for (var s = 0; s <= segments; s++)
        {
            var t = from + (to - from) * s / segments;
            points.Add(center + new Vector2(rx * MathF.Cos(t), ry * MathF.Sin(t)));
        }
    }

    /// <summary>A path's subpaths as polylines, curves flattened.</summary>
    public static List<(List<Vector2> Points, bool Closed)> PathData(string d)
    {
        var subpaths = new List<(List<Vector2>, bool)>();
        List<Vector2>? current = null;
        Vector2 position = default, start = default, control = default;
        var previous = ' ';
        var i = 0;
        var command = ' ';
        float Next()
        {
            var n = Numbers(Token(d, ref i));
            return n.Count > 0 ? n[0] : 0;
        }
        void Begin()
        {
            if (current is { Count: > 1 }) subpaths.Add((current, false));
            current = [position];
            start = position;
        }
        while (true)
        {
            while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
            if (i >= d.Length) break;
            if (char.IsAsciiLetter(d[i])) command = d[i++];
            else if (command == ' ') break;
            var relative = char.IsLower(command);
            var origin = relative ? position : Vector2.Zero;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    position = origin + new Vector2(Next(), Next());
                    Begin();
                    command = relative ? 'l' : 'L'; // further pairs are line-tos
                    break;
                case 'L':
                    position = origin + new Vector2(Next(), Next());
                    (current ??= [start]).Add(position);
                    break;
                case 'H':
                    position = new Vector2((relative ? position.X : 0) + Next(), position.Y);
                    (current ??= [start]).Add(position);
                    break;
                case 'V':
                    position = new Vector2(position.X, (relative ? position.Y : 0) + Next());
                    (current ??= [start]).Add(position);
                    break;
                case 'C':
                case 'S':
                {
                    var upper = char.ToUpperInvariant(command);
                    var c1 = upper == 'C' ? origin + new Vector2(Next(), Next())
                        : previous is 'C' or 'S' ? 2 * position - control : position;
                    var c2 = origin + new Vector2(Next(), Next());
                    var end = origin + new Vector2(Next(), Next());
                    current ??= [position];
                    for (var s = 1; s <= CurveSegments; s++)
                    {
                        var t = s / (float)CurveSegments;
                        var u = 1 - t;
                        current.Add(u * u * u * position + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * end);
                    }
                    control = c2;
                    position = end;
                    break;
                }
                case 'Q':
                case 'T':
                {
                    var upper = char.ToUpperInvariant(command);
                    var c = upper == 'Q' ? origin + new Vector2(Next(), Next())
                        : previous is 'Q' or 'T' ? 2 * position - control : position;
                    var end = origin + new Vector2(Next(), Next());
                    current ??= [position];
                    for (var s = 1; s <= CurveSegments; s++)
                    {
                        var t = s / (float)CurveSegments;
                        var u = 1 - t;
                        current.Add(u * u * position + 2 * u * t * c + t * t * end);
                    }
                    control = c;
                    position = end;
                    break;
                }
                case 'A':
                {
                    var rx = MathF.Abs(Next());
                    var ry = MathF.Abs(Next());
                    var rotation = Next() * MathF.PI / 180;
                    var large = Next() != 0;
                    var sweep = Next() != 0;
                    var end = origin + new Vector2(Next(), Next());
                    current ??= [position];
                    current.AddRange(ArcPoints(position, end, rx, ry, rotation, large, sweep));
                    position = end;
                    break;
                }
                case 'Z':
                    if (current is { Count: > 1 }) subpaths.Add((current, true));
                    current = null;
                    position = start;
                    break;
                default:
                    i++;
                    break;
            }
            previous = char.ToUpperInvariant(command);
        }
        if (current is { Count: > 1 }) subpaths.Add((current, false));
        return subpaths;
    }

    private static string Token(string d, ref int i)
    {
        while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
        var start = i;
        if (i < d.Length && d[i] is '+' or '-') i++;
        var dot = false;
        while (i < d.Length)
        {
            var c = d[i];
            if (char.IsAsciiDigit(c)) i++;
            else if (c == '.' && !dot) { dot = true; i++; }
            else if (c is 'e' or 'E' && i + 1 < d.Length && (char.IsAsciiDigit(d[i + 1]) || d[i + 1] is '-' or '+')) { i += 2; }
            else break;
        }
        return d[start..i];
    }

    // SVG's endpoint arc as points (SVG 1.1 appendix F.6).
    private static IEnumerable<Vector2> ArcPoints(Vector2 from, Vector2 to, float rx, float ry, float phi, bool large, bool sweep)
    {
        if (rx == 0 || ry == 0 || from == to)
        {
            yield return to;
            yield break;
        }
        var cos = MathF.Cos(phi);
        var sin = MathF.Sin(phi);
        var half = (from - to) / 2;
        var x1 = cos * half.X + sin * half.Y;
        var y1 = -sin * half.X + cos * half.Y;
        var lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1) { rx *= MathF.Sqrt(lambda); ry *= MathF.Sqrt(lambda); }
        var numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        var factor = MathF.Sqrt(Math.Max(0, numerator / (rx * rx * y1 * y1 + ry * ry * x1 * x1))) * (large == sweep ? -1 : 1);
        var cx1 = factor * rx * y1 / ry;
        var cy1 = -factor * ry * x1 / rx;
        var center = new Vector2(cos * cx1 - sin * cy1, sin * cx1 + cos * cy1) + (from + to) / 2;
        static float Angle(Vector2 u, Vector2 v) => MathF.Atan2(u.X * v.Y - u.Y * v.X, Vector2.Dot(u, v));
        var theta = Angle(Vector2.UnitX, new Vector2((x1 - cx1) / rx, (y1 - cy1) / ry));
        var delta = Angle(new Vector2((x1 - cx1) / rx, (y1 - cy1) / ry), new Vector2((-x1 - cx1) / rx, (-y1 - cy1) / ry));
        if (!sweep && delta > 0) delta -= 2 * MathF.PI;
        else if (sweep && delta < 0) delta += 2 * MathF.PI;
        const int segments = 16;
        for (var s = 1; s <= segments; s++)
        {
            var t = theta + delta * s / segments;
            var p = new Vector2(rx * MathF.Cos(t), ry * MathF.Sin(t));
            yield return new Vector2(cos * p.X - sin * p.Y, sin * p.X + cos * p.Y) + center;
        }
    }

    /// <summary>Ear clipping: triangle vertex indices for a simple polygon.</summary>
    public static List<int> Triangulate(IReadOnlyList<Vector2> polygon)
    {
        var triangles = new List<int>();
        var n = polygon.Count;
        if (n < 3) return triangles;
        var area = 0f;
        for (int p = n - 1, q = 0; q < n; p = q++) area += polygon[p].X * polygon[q].Y - polygon[q].X * polygon[p].Y;
        var indices = area > 0 ? Enumerable.Range(0, n).ToList() : Enumerable.Range(0, n).Reverse().ToList();
        var guard = 0;
        while (indices.Count > 2 && guard++ < n * n)
        {
            var clipped = false;
            for (var i = 0; i < indices.Count; i++)
            {
                var a = polygon[indices[(i + indices.Count - 1) % indices.Count]];
                var b = polygon[indices[i]];
                var c = polygon[indices[(i + 1) % indices.Count]];
                if (Cross(a, b, c) <= 0) continue;
                var inside = false;
                foreach (var j in indices)
                {
                    var p = polygon[j];
                    if (p == a || p == b || p == c) continue;
                    if (Cross(a, b, p) >= 0 && Cross(b, c, p) >= 0 && Cross(c, a, p) >= 0) { inside = true; break; }
                }
                if (inside) continue;
                triangles.AddRange([indices[(i + indices.Count - 1) % indices.Count], indices[i], indices[(i + 1) % indices.Count]]);
                indices.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) break; // self-intersecting: draw what was clipped
        }
        return triangles;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}
