using System.Collections.Generic;
using System.Numerics;

namespace HachidoriFfxiv.Rendering.Css;

/// <summary>A drop shadow or ring from box-shadow, in CSS px.</summary>
public readonly record struct CssShadow(Vector4 Color, float OffsetX, float OffsetY, float Blur, float Spread, bool Inset);

/// <summary>The paint of one CSS box, in CSS px. Null colours mean "not declared".</summary>
public sealed class CssBox
{
    public Vector4? Background;
    public Vector4? GradientTop;
    public Vector4? GradientBottom;
    public Vector4? Color;
    public Vector4? BorderColor;
    public float BorderWidth;
    public Vector4? BorderTopColor;
    public float BorderTopWidth;
    public float Radius;
    public Vector4 Padding;      // top, right, bottom, left
    public float Inset;
    public float? FontSize;
    public List<CssShadow> Shadows = new();

    public bool HasPaint => Background is { W: > 0 } || GradientTop is not null || (BorderWidth > 0 && BorderColor is { W: > 0 }) || Shadows.Count > 0;
}

/// <summary>
/// The popup's look, computed from a Hachidori theme stylesheet plus the user's custom popup CSS.
/// Lengths are CSS px; <see cref="Scale"/> converts them to screen pixels.
/// </summary>
public sealed class PopupTheme
{
    public string Renderer { get; init; } = "plugin";
    public string Palette { get; init; } = string.Empty;
    public string Variant { get; init; } = string.Empty;
    public bool Dark { get; init; } = true;

    /// <summary>CSS px → screen px (Dalamud scale × the user's font size against the theme's base size).</summary>
    public float Scale { get; set; } = 1f;
    public float BaseFontPx { get; init; } = 16f;

    public CssBox Popup { get; init; } = new();
    public CssBox? Frame { get; init; }
    public bool Crest { get; init; }
    public Vector4 CrestGold { get; init; } = new(0.82f, 0.68f, 0.39f, 1);
    public Vector4 CrestGem { get; init; } = new(0.32f, 0.71f, 1f, 1);

    public Vector4 Text { get; init; }
    public Vector4 TextMuted { get; init; }
    public Vector4 Accent { get; init; }
    public Vector4 Link { get; init; }
    public Vector4 Highlight { get; init; }
    public Vector4 PitchLine { get; init; }
    public Vector4? Headword { get; init; }
    public Vector4? Reading { get; init; }
    public float HeadwordScale { get; init; } = 1.7f;

    public CssBox Entry { get; init; } = new();
    public Vector4? Separator { get; init; }
    public float SeparatorWidth { get; init; } = 1f;
    public CssBox? Section { get; init; }

    public CssBox DictionaryChip { get; init; } = new();
    public CssBox TagChip { get; init; } = new();
    public CssBox FrequencyChip { get; init; } = new();
    public CssBox InflectionChip { get; init; } = new();
    public CssBox CountChip { get; init; } = new();

    public Vector4 Button { get; init; }
    public Vector4 ButtonHovered { get; init; }
    public Vector4 ButtonText { get; init; }
    public Vector4? ButtonBorder { get; init; }
    public float ButtonRadius { get; init; } = 4f;
    public Vector4? Scrollbar { get; init; }

    /// <summary>True for the plugin's own look (no Hachidori theme applied).</summary>
    public bool IsPlugin => Renderer == "plugin";

    /// <summary>The look the active popups use. Swapped atomically by <see cref="PopupThemeProvider"/>.</summary>
    public static PopupTheme Current { get; set; } = PluginDefault();

    public static Vector4 Rgba(uint abgr) => new((abgr & 255) / 255f, ((abgr >> 8) & 255) / 255f, ((abgr >> 16) & 255) / 255f, ((abgr >> 24) & 255) / 255f);

    private static CssBox Chip(uint abgr) => new() { Background = Rgba(abgr), Color = Vector4.One, Radius = 5f };

    /// <summary>The plugin's original popup: lilac border, coloured chips, ImGui's own palette.</summary>
    public static PopupTheme PluginDefault() => new()
    {
        Renderer = "plugin",
        Popup = new CssBox { BorderWidth = 1, BorderColor = Rgba(0xFFB98CF0), Radius = 8 },
        Text = new Vector4(1, 1, 1, 1),
        TextMuted = Rgba(0xFF9A9A9A),
        Accent = Rgba(0xFFB98CF0),
        Link = Rgba(0xFFF5B06A),
        Highlight = new Vector4(0.26f, 0.59f, 0.98f, 0.35f),
        PitchLine = Rgba(0xFF6AB0F5),
        DictionaryChip = Chip(0xFF6B4A3A),
        TagChip = Chip(0xFF505050),
        FrequencyChip = Chip(0xFF3F6B3A),
        InflectionChip = Chip(0xFF5A3F6B),
        CountChip = Chip(0xFF2F5F8F),
    };
}
