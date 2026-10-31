using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.Rendering;

/// <summary>Turns a Yomitan glossary (strings and structured content) into <see cref="RichBlock"/>s.</summary>
public sealed class GlossaryBuilder
{
    private readonly List<RichBlock> blocks = new();
    private readonly bool showAttribution;
    private readonly string dictionary;
    private RichBlock? current;
    private float indent;
    private string? pendingMarker;

    private GlossaryBuilder(float baseIndent, bool showAttribution, string dictionary)
    {
        indent = baseIndent;
        this.showAttribution = showAttribution;
        this.dictionary = dictionary;
    }

    private readonly record struct Style(uint Color, float Scale, bool Bold, bool Underline, bool Strike, bool Link);

    public static List<RichBlock> Build(JsonNode? glossary, float baseIndent, bool showAttribution, string dictionary = "")
    {
        var builder = new GlossaryBuilder(baseIndent, showAttribution, dictionary);
        var style = new Style(0, 1f, false, false, false, false);
        if (glossary is JsonArray items)
        {
            var many = items.Count > 1;
            var number = 1;
            foreach (var item in items)
            {
                // Each top-level glossary item is its own definition, numbered as Yomitan does.
                builder.Break();
                if (many)
                    builder.pendingMarker = $"{number++}.";
                builder.indent += many ? 1.4f : 0f;
                builder.Item(item, style);
                builder.Break();
                builder.indent -= many ? 1.4f : 0f;
            }
        }
        else
        {
            builder.Item(glossary, style);
        }

        builder.Break();
        return builder.blocks.Where(b => !b.IsEmpty).ToList();
    }

    private void Item(JsonNode? item, Style style)
    {
        switch (item)
        {
            case null:
                return;
            case JsonValue value:
                Text(LookupResult.Str(value), style);
                return;
            case JsonArray array:
                foreach (var child in array)
                    Node(child, style);
                return;
            case JsonObject obj:
                switch (LookupResult.Str(obj["type"]))
                {
                    case "text":
                        Text(LookupResult.Str(obj["text"]), style);
                        return;
                    case "structured-content":
                        Node(obj["content"], style);
                        return;
                    case "image":
                        Image(obj, style);
                        return;
                    default:
                        Node(obj, style);
                        return;
                }
        }
    }

    private void Node(JsonNode? node, Style style)
    {
        switch (node)
        {
            case null:
                return;
            case JsonValue value:
                Text(LookupResult.Str(value), style);
                return;
            case JsonArray array:
                foreach (var child in array)
                    Node(child, style);
                return;
            case JsonObject element:
                Element(element, style);
                return;
        }
    }

    private void Element(JsonObject element, Style style)
    {
        var tag = LookupResult.Str(element["tag"]).ToLowerInvariant();
        var data = element["data"] as JsonObject;
        var dataContent = LookupResult.Str(data?["content"]);
        if (!showAttribution && dataContent is "attribution" or "attribution-footnote")
            return;

        var css = element["style"] as JsonObject;
        var next = ApplyStyle(style, css, tag);
        var marginLeft = Em(css?["marginLeft"]) + Em(css?["paddingLeft"]);
        var content = element["content"];

        switch (tag)
        {
            case "br":
                Text("\n", style);
                return;
            case "img":
            case "image":
                Image(element, style);
                return;
            case "ruby":
                Ruby(element, next);
                return;
            case "rt":
            case "rp":
                return;
            case "ul":
            case "ol":
            {
                Break();
                var listType = ListStyle(css) ?? (tag == "ol" ? null : "•");
                var number = 1;
                indent += 1.2f + marginLeft;
                foreach (var child in Children(content))
                {
                    if (child is JsonObject li && LookupResult.Str(li["tag"]) == "li")
                    {
                        var itemStyle = ListStyle(li["style"] as JsonObject);
                        pendingMarker = itemStyle ?? listType ?? $"{number}.";
                        number++;
                        Element(li, next);
                    }
                    else
                    {
                        Node(child, next);
                    }
                }

                Break();
                indent -= 1.2f + marginLeft;
                return;
            }

            case "li":
            case "div":
            case "p":
            case "details":
            case "summary":
            case "table":
            case "thead":
            case "tbody":
            case "tfoot":
            case "tr":
            case "section":
            case "blockquote":
            {
                var marker = pendingMarker;
                pendingMarker = null;
                Break();
                pendingMarker = marker;
                indent += marginLeft;
                if (tag == "summary")
                    next = next with { Bold = true };
                if (dataContent == "example-sentence")
                    next = next with { Color = next.Color == 0 ? Colors.Example : next.Color };
                Node(content, next);
                Break();
                indent -= marginLeft;
                return;
            }

            case "td":
            case "th":
                if (current is { IsEmpty: false })
                    Text("  ", style);
                Node(content, tag == "th" ? next with { Bold = true } : next);
                return;
            default:
            {
                var background = ParseColor(css?["backgroundColor"]?.ToString() ?? css?["background"]?.ToString());
                if (background != 0)
                {
                    // Tag-like spans (Jitendex part-of-speech labels and friends) become chips.
                    var text = PlainText(content);
                    if (text.Length > 0)
                    {
                        var block = Current();
                        block.Add(new RichRun
                        {
                            Text = text,
                            Chip = true,
                            Background = background,
                            Color = next.Color != 0 ? next.Color : 0xFFFFFFFF,
                            Scale = next.Scale,
                            Bold = next.Bold,
                        });
                        block.Add(" ");
                    }

                    return;
                }

                Node(content, next);
                return;
            }
        }
    }

    private void Ruby(JsonObject element, Style style)
    {
        var baseText = string.Empty;
        var ruby = string.Empty;
        foreach (var child in Children(element["content"]))
        {
            if (child is JsonObject obj && LookupResult.Str(obj["tag"]) is "rt")
                ruby += PlainText(obj["content"]);
            else if (child is JsonObject rp && LookupResult.Str(rp["tag"]) is "rp")
                continue;
            else
                baseText += PlainText(child);
        }

        if (baseText.Length == 0)
            return;
        Current().Add(new RichRun
        {
            Text = baseText,
            Ruby = ruby.Length > 0 ? ruby : null,
            Color = style.Color,
            Scale = style.Scale,
            Bold = style.Bold,
        });
    }

    private void Text(string text, Style style)
    {
        if (text.Length == 0)
            return;
        Current().Add(new RichRun
        {
            Text = text.Replace("\r\n", "\n"),
            Color = style.Color,
            Scale = style.Scale,
            Bold = style.Bold,
            Underline = style.Underline,
            Strike = style.Strike,
            Link = style.Link,
        });
    }

    private RichBlock Current()
    {
        if (current is null)
        {
            current = new RichBlock { IndentEm = indent, Marker = pendingMarker };
            pendingMarker = null;
            blocks.Add(current);
        }

        return current;
    }

    private void Break() => current = null;

    private void Image(JsonObject value, Style style)
    {
        if (DictionaryImage.Parse(value, dictionary) is not { } image)
        {
            Text(ImageLabel(value), style with { Color = Colors.Dim });
            return;
        }
        Break();
        blocks.Add(new RichBlock { Image = image, IndentEm = indent, Marker = pendingMarker });
        pendingMarker = null;
        Break();
    }

    private static IEnumerable<JsonNode?> Children(JsonNode? content) => content switch
    {
        JsonArray array => array,
        null => Array.Empty<JsonNode?>(),
        _ => new[] { content },
    };

    public static string PlainText(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue v => LookupResult.Str(v),
        JsonArray a => string.Concat(a.Select(PlainText)),
        JsonObject o when LookupResult.Str(o["tag"]) is "rt" or "rp" => string.Empty,
        JsonObject o when LookupResult.Str(o["tag"]) is "br" => "\n",
        JsonObject o => PlainText(o["content"]),
        _ => string.Empty,
    };

    private static string ImageLabel(JsonObject image)
    {
        var label = LookupResult.Str(image["alt"]);
        if (label.Length == 0)
            label = LookupResult.Str(image["title"]);
        if (label.Length == 0)
            label = LookupResult.Str(image["description"]);
        return label.Length > 0 ? label : "[image]";
    }

    private static string? ListStyle(JsonObject? css)
    {
        var value = css?["listStyleType"]?.ToString();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            return value[1..^1].Trim();
        return value switch
        {
            "none" => string.Empty,
            "disc" => "•",
            "circle" => "◦",
            "square" => "▪",
            _ => null,
        };
    }

    private static Style ApplyStyle(Style style, JsonObject? css, string tag)
    {
        switch (tag)
        {
            case "b":
            case "strong":
            case "th":
                style = style with { Bold = true };
                break;
            case "u":
                style = style with { Underline = true };
                break;
            case "s":
            case "del":
                style = style with { Strike = true };
                break;
            case "sub":
            case "sup":
            case "small":
                style = style with { Scale = style.Scale * 0.8f };
                break;
            case "a":
                style = style with { Link = true };
                break;
        }

        if (css is null)
            return style;
        var fontSize = css["fontSize"]?.ToString();
        if (!string.IsNullOrWhiteSpace(fontSize))
            style = style with { Scale = style.Scale * FontScale(fontSize) };
        var weight = css["fontWeight"]?.ToString();
        if (weight is "bold" or "bolder" or "600" or "700" or "800" or "900")
            style = style with { Bold = true };
        else if (weight is "normal" or "400")
            style = style with { Bold = false };
        var decoration = css["textDecorationLine"]?.ToString() ?? css["textDecoration"]?.ToString();
        if (decoration?.Contains("underline") == true)
            style = style with { Underline = true };
        if (decoration?.Contains("line-through") == true)
            style = style with { Strike = true };
        var color = ParseColor(css["color"]?.ToString());
        if (color != 0)
            style = style with { Color = color };
        return style;
    }

    private static float FontScale(string value)
    {
        value = value.Trim().ToLowerInvariant();
        return value switch
        {
            "xx-small" => 0.6f,
            "x-small" => 0.7f,
            "small" or "smaller" => 0.85f,
            "medium" => 1f,
            "large" or "larger" => 1.15f,
            "x-large" => 1.35f,
            "xx-large" => 1.6f,
            _ when value.EndsWith("em") && float.TryParse(value.TrimEnd('m', 'e', 'r'), NumberStyles.Float, CultureInfo.InvariantCulture, out var em) => Math.Clamp(em, 0.5f, 3f),
            _ when value.EndsWith('%') && float.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) => Math.Clamp(pct / 100f, 0.5f, 3f),
            _ => 1f,
        };
    }

    private static float Em(JsonNode? node)
    {
        var value = node?.ToString()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
            return 0;
        if (value.EndsWith("em") && float.TryParse(value.TrimEnd('m', 'e', 'r'), NumberStyles.Float, CultureInfo.InvariantCulture, out var em))
            return Math.Clamp(em, 0, 6);
        if (value.EndsWith("px") && float.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
            return Math.Clamp(px / 16f, 0, 6);
        return 0;
    }

    /// <summary>A CSS colour as ImGui ABGR, or 0.</summary>
    public static uint ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        value = value.Trim().ToLowerInvariant();
        static uint Abgr(int r, int g, int b, int a = 255) => (uint)((a << 24) | (b << 16) | (g << 8) | r);
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length is 3 or 4)
                hex = string.Concat(hex.Select(c => $"{c}{c}"));
            if (hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                return hex.Length == 6
                    ? Abgr((int)(rgb >> 16) & 255, (int)(rgb >> 8) & 255, (int)rgb & 255)
                    : Abgr((int)(rgb >> 24) & 255, (int)(rgb >> 16) & 255, (int)(rgb >> 8) & 255, (int)rgb & 255);
            }

            return 0;
        }

        if (value.StartsWith("rgb"))
        {
            var parts = value[(value.IndexOf('(') + 1)..].TrimEnd(')').Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3
                && int.TryParse(parts[0], out var r) && int.TryParse(parts[1], out var g) && int.TryParse(parts[2], out var b))
            {
                var a = parts.Length > 3 && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var af) ? (int)(af <= 1 ? af * 255 : af) : 255;
                return Abgr(r, g, b, a);
            }

            return 0;
        }

        return value switch
        {
            "white" => Abgr(255, 255, 255),
            "black" => Abgr(30, 30, 30),
            "red" => Abgr(230, 80, 80),
            "darkred" => Abgr(170, 40, 40),
            "green" => Abgr(70, 170, 90),
            "darkgreen" => Abgr(40, 120, 60),
            "blue" => Abgr(90, 130, 230),
            "darkblue" => Abgr(50, 70, 170),
            "brown" => Abgr(150, 80, 40),
            "purple" => Abgr(150, 80, 190),
            "orange" => Abgr(230, 140, 40),
            "gray" or "grey" => Abgr(128, 128, 128),
            "darkgray" or "darkgrey" => Abgr(90, 90, 90),
            "lightgray" or "lightgrey" => Abgr(200, 200, 200),
            "pink" => Abgr(230, 140, 170),
            "teal" => Abgr(40, 150, 150),
            "navy" => Abgr(40, 50, 130),
            "maroon" => Abgr(128, 30, 30),
            "olive" => Abgr(128, 128, 30),
            "goldenrod" => Abgr(218, 165, 32),
            "crimson" => Abgr(220, 20, 60),
            "transparent" or "inherit" or "initial" or "currentcolor" => 0,
            _ => 0,
        };
    }
}

public static class Colors
{
    public const uint Dim = 0xFF9A9A9A;
    public const uint Example = 0xFFC8D8E8;
    public const uint DictionaryChip = 0xFF6B4A3A;   // muted blue
    public const uint TagChip = 0xFF505050;
    public const uint FrequencyChip = 0xFF3F6B3A;    // muted green
    public const uint InflectionChip = 0xFF5A3F6B;   // muted purple
    public const uint CountChip = 0xFF2F5F8F;        // amber-ish
    public const uint PitchLine = 0xFF6AB0F5;
    public const uint Accent = 0xFFB98CF0;           // lilac (Hachidori)
}
