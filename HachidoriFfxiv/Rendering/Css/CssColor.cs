using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace HachidoriFfxiv.Rendering.Css;

/// <summary>
/// CSS colour values as straight-alpha RGBA (0..1): hex, named colours, rgb(), hsl(), oklch(), oklab(),
/// light-dark() and color-mix(). Mixing happens in sRGB, which is what Hachidori's themes ask for
/// almost everywhere (and close enough where they do not).
/// </summary>
public static class CssColor
{
    public static readonly Vector4 Transparent = Vector4.Zero;

    /// <summary>Parses a colour; <paramref name="dark"/> picks the second half of light-dark().</summary>
    public static Vector4? Parse(string? value, bool dark)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim().ToLowerInvariant();
        if (v.StartsWith('#'))
            return Hex(v[1..]);
        if (Named.TryGetValue(v, out var named))
            return named;

        var open = v.IndexOf('(');
        if (open <= 0 || !v.EndsWith(')'))
            return null;
        var name = v[..open].Trim();
        var inner = v[(open + 1)..^1];
        switch (name)
        {
            case "light-dark":
            {
                var parts = CssText.SplitTopLevel(inner, ',');
                return parts.Count == 2 ? Parse(parts[dark ? 1 : 0], dark) : null;
            }
            case "color-mix":
                return Mix(inner, dark);
            case "rgb" or "rgba":
                return Rgb(inner);
            case "hsl" or "hsla":
                return Hsl(inner);
            case "oklch":
                return OkLch(inner);
            case "oklab":
                return OkLab(inner);
            default:
                return null;
        }
    }

    /// <summary>The colour as ImGui's packed ABGR.</summary>
    public static uint ToAbgr(Vector4 c)
    {
        static uint B(float f) => (uint)Math.Clamp(MathF.Round(f * 255f), 0, 255);
        return (B(c.W) << 24) | (B(c.Z) << 16) | (B(c.Y) << 8) | B(c.X);
    }

    public static Vector4 WithAlpha(Vector4 c, float alpha) => c with { W = c.W * alpha };

    /// <summary>color-mix(in srgb, a p, b) with premultiplied alpha, as CSS defines it.</summary>
    public static Vector4 Mix(Vector4 a, float pa, Vector4 b, float pb)
    {
        var total = pa + pb;
        if (total <= 0)
            return Transparent;
        var wa = pa / total;
        var wb = pb / total;
        var alpha = a.W * wa + b.W * wb;
        if (alpha <= 0)
            return Transparent;
        var rgb = (new Vector3(a.X, a.Y, a.Z) * a.W * wa + new Vector3(b.X, b.Y, b.Z) * b.W * wb) / alpha;
        return new Vector4(rgb, alpha);
    }

    private static Vector4? Mix(string inner, bool dark)
    {
        var parts = CssText.SplitTopLevel(inner, ',');
        if (parts.Count != 3 || !parts[0].TrimStart().StartsWith("in "))
            return null;
        if (!MixStop(parts[1], dark, out var a, out var pa) || !MixStop(parts[2], dark, out var b, out var pb))
            return null;
        if (pa is null && pb is null)
            (pa, pb) = (0.5f, 0.5f);
        else if (pa is null)
            pa = 1 - pb!.Value;
        else if (pb is null)
            pb = 1 - pa.Value;
        var sum = pa.Value + pb!.Value;
        var mixed = Mix(a, pa.Value, b, pb.Value);
        // A sum under 100% scales the alpha down (CSS Color 5).
        return sum < 1 ? mixed with { W = mixed.W * sum } : mixed;
    }

    private static bool MixStop(string text, bool dark, out Vector4 color, out float? percent)
    {
        color = default;
        percent = null;
        var tokens = CssText.SplitTopLevel(text.Trim(), ' ');
        string? colorText = null;
        foreach (var token in tokens)
        {
            if (token.EndsWith('%') && float.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                percent = Math.Clamp(p / 100f, 0, 1);
            else
                colorText = colorText is null ? token : colorText + " " + token;
        }

        if (colorText is null || Parse(colorText, dark) is not { } c)
            return false;
        color = c;
        return true;
    }

    private static Vector4? Hex(string hex)
    {
        if (hex.Length is 3 or 4)
            hex = string.Concat(Array.ConvertAll(hex.ToCharArray(), ch => $"{ch}{ch}"));
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n))
            return null;
        if (hex.Length == 6)
            n = (n << 8) | 0xFF;
        return new Vector4(((n >> 24) & 255) / 255f, ((n >> 16) & 255) / 255f, ((n >> 8) & 255) / 255f, (n & 255) / 255f);
    }

    /// <summary>Channel list of a modern or legacy colour function: "r g b / a" or "r, g, b, a".</summary>
    private static List<string> Channels(string inner)
    {
        var slash = inner.Split('/');
        var list = new List<string>(inner.Contains(',') && slash.Length == 1
            ? CssText.SplitTopLevel(inner, ',')
            : CssText.SplitTopLevel(slash[0], ' '));
        if (slash.Length == 2)
            list.Add(slash[1]);
        for (var i = 0; i < list.Count; i++)
            list[i] = list[i].Trim();
        list.RemoveAll(s => s.Length == 0);
        return list;
    }

    private static float Number(string s, float percentOf = 1f)
    {
        s = s.Trim();
        if (s == "none")
            return 0;
        if (s.EndsWith('%'))
            return float.Parse(s[..^1], CultureInfo.InvariantCulture) / 100f * percentOf;
        if (s.EndsWith("deg"))
            return float.Parse(s[..^3], CultureInfo.InvariantCulture);
        return float.Parse(s, CultureInfo.InvariantCulture);
    }

    private static float Alpha(List<string> c, int index) => c.Count > index ? Math.Clamp(Number(c[index]), 0, 1) : 1;

    private static Vector4? Rgb(string inner)
    {
        try
        {
            var c = Channels(inner);
            if (c.Count < 3)
                return null;
            return new Vector4(Number(c[0], 255) / 255f, Number(c[1], 255) / 255f, Number(c[2], 255) / 255f, Alpha(c, 3));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Vector4? Hsl(string inner)
    {
        try
        {
            var c = Channels(inner);
            if (c.Count < 3)
                return null;
            var h = Number(c[0]) % 360 / 360f;
            if (h < 0) h += 1;
            var s = Number(c[1].EndsWith('%') ? c[1] : c[1] + "%");
            var l = Number(c[2].EndsWith('%') ? c[2] : c[2] + "%");
            float Hue(float t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                var q = l < 0.5f ? l * (1 + s) : l + s - l * s;
                var p = 2 * l - q;
                if (t < 1 / 6f) return p + (q - p) * 6 * t;
                if (t < 0.5f) return q;
                if (t < 2 / 3f) return p + (q - p) * (2 / 3f - t) * 6;
                return p;
            }

            return new Vector4(Hue(h + 1 / 3f), Hue(h), Hue(h - 1 / 3f), Alpha(c, 3));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Vector4? OkLch(string inner)
    {
        try
        {
            var c = Channels(inner);
            if (c.Count < 3)
                return null;
            var l = Number(c[0]);
            var chroma = Number(c[1], 0.4f);
            var hue = Number(c[2]) * MathF.PI / 180f;
            return FromOkLab(l, chroma * MathF.Cos(hue), chroma * MathF.Sin(hue), Alpha(c, 3));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Vector4? OkLab(string inner)
    {
        try
        {
            var c = Channels(inner);
            if (c.Count < 3)
                return null;
            return FromOkLab(Number(c[0]), Number(c[1], 0.4f), Number(c[2], 0.4f), Alpha(c, 3));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Vector4 FromOkLab(float l, float a, float b, float alpha)
    {
        var l_ = l + 0.3963377774f * a + 0.2158037573f * b;
        var m_ = l - 0.1055613458f * a - 0.0638541728f * b;
        var s_ = l - 0.0894841775f * a - 1.2914855480f * b;
        var lc = l_ * l_ * l_;
        var mc = m_ * m_ * m_;
        var sc = s_ * s_ * s_;
        var r = 4.0767416621f * lc - 3.3077115913f * mc + 0.2309699292f * sc;
        var g = -1.2684380046f * lc + 2.6097574011f * mc - 0.3413193965f * sc;
        var bl = -0.0041960863f * lc - 0.7034186147f * mc + 1.7076147010f * sc;
        static float Gamma(float x)
        {
            x = Math.Clamp(x, 0, 1);
            return x <= 0.0031308f ? 12.92f * x : 1.055f * MathF.Pow(x, 1 / 2.4f) - 0.055f;
        }

        return new Vector4(Gamma(r), Gamma(g), Gamma(bl), alpha);
    }

    private static Vector4 Rgb8(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1);

    private static readonly Dictionary<string, Vector4> Named = new()
    {
        ["transparent"] = Transparent,
        ["black"] = Rgb8(0, 0, 0), ["white"] = Rgb8(255, 255, 255), ["red"] = Rgb8(255, 0, 0),
        ["green"] = Rgb8(0, 128, 0), ["blue"] = Rgb8(0, 0, 255), ["yellow"] = Rgb8(255, 255, 0),
        ["gray"] = Rgb8(128, 128, 128), ["grey"] = Rgb8(128, 128, 128), ["silver"] = Rgb8(192, 192, 192),
        ["darkgray"] = Rgb8(169, 169, 169), ["darkgrey"] = Rgb8(169, 169, 169), ["lightgray"] = Rgb8(211, 211, 211),
        ["lightgrey"] = Rgb8(211, 211, 211), ["dimgray"] = Rgb8(105, 105, 105), ["gainsboro"] = Rgb8(220, 220, 220),
        ["whitesmoke"] = Rgb8(245, 245, 245), ["slategray"] = Rgb8(112, 128, 144), ["orange"] = Rgb8(255, 165, 0),
        ["orangered"] = Rgb8(255, 69, 0), ["gold"] = Rgb8(255, 215, 0), ["goldenrod"] = Rgb8(218, 165, 32),
        ["chocolate"] = Rgb8(210, 105, 30), ["brown"] = Rgb8(165, 42, 42), ["maroon"] = Rgb8(128, 0, 0),
        ["darkred"] = Rgb8(139, 0, 0), ["crimson"] = Rgb8(220, 20, 60), ["tomato"] = Rgb8(255, 99, 71),
        ["coral"] = Rgb8(255, 127, 80), ["salmon"] = Rgb8(250, 128, 114), ["pink"] = Rgb8(255, 192, 203),
        ["hotpink"] = Rgb8(255, 105, 180), ["deeppink"] = Rgb8(255, 20, 147), ["magenta"] = Rgb8(255, 0, 255),
        ["fuchsia"] = Rgb8(255, 0, 255), ["violet"] = Rgb8(238, 130, 238), ["plum"] = Rgb8(221, 160, 221),
        ["orchid"] = Rgb8(218, 112, 214), ["purple"] = Rgb8(128, 0, 128), ["rebeccapurple"] = Rgb8(102, 51, 153),
        ["indigo"] = Rgb8(75, 0, 130), ["lavender"] = Rgb8(230, 230, 250), ["navy"] = Rgb8(0, 0, 128),
        ["darkblue"] = Rgb8(0, 0, 139), ["midnightblue"] = Rgb8(25, 25, 112), ["royalblue"] = Rgb8(65, 105, 225),
        ["dodgerblue"] = Rgb8(30, 144, 255), ["steelblue"] = Rgb8(70, 130, 180), ["skyblue"] = Rgb8(135, 206, 235),
        ["deepskyblue"] = Rgb8(0, 191, 255), ["lightblue"] = Rgb8(173, 216, 230), ["lightsteelblue"] = Rgb8(176, 196, 222),
        ["cyan"] = Rgb8(0, 255, 255), ["aqua"] = Rgb8(0, 255, 255), ["teal"] = Rgb8(0, 128, 128),
        ["turquoise"] = Rgb8(64, 224, 208), ["lime"] = Rgb8(0, 255, 0), ["limegreen"] = Rgb8(50, 205, 50),
        ["seagreen"] = Rgb8(46, 139, 87), ["forestgreen"] = Rgb8(34, 139, 34), ["darkgreen"] = Rgb8(0, 100, 0),
        ["olive"] = Rgb8(128, 128, 0), ["khaki"] = Rgb8(240, 230, 140), ["beige"] = Rgb8(245, 245, 220),
        ["ivory"] = Rgb8(255, 255, 240), ["wheat"] = Rgb8(245, 222, 179), ["tan"] = Rgb8(210, 180, 140),
        ["canvas"] = Rgb8(30, 30, 30), ["canvastext"] = Rgb8(255, 255, 255),
    };
}
