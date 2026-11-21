using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HachidoriFfxiv.Anki;

/// <summary>
/// The small part of a browser DOM that Hachidori's Anki glossary renderer uses,
/// with Chrome's HTML serialization (outerHTML), so offline note fields come out
/// as the offscreen document writes them: attributes in the order they were
/// first set, the inline style serialized from its declarations, void elements
/// without end tags, and the HTML fragment serialization escapes.
/// </summary>
public abstract class HtmlNode
{
    public HtmlElement? Parent { get; internal set; }

    public void Remove() => Parent?.RemoveChild(this);
}

public sealed class HtmlText(string value) : HtmlNode
{
    public string Value { get; set; } = value;
}

/// <summary>Markup kept as it is: what Hachidori assigns through innerHTML from already escaped text.</summary>
public sealed class HtmlRaw(string html) : HtmlNode
{
    public string Html { get; } = html;
}

public sealed class HtmlElement : HtmlNode
{
    private static readonly HashSet<string> VoidElements = ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"];
    private const string StyleAttribute = "style";

    // Attribute order matters for serialization. The style attribute's value is
    // its declaration block, serialized when the element is.
    private readonly List<(string Name, string Value)> attributes = [];
    private readonly List<HtmlNode> children = [];
    // A style attribute set as text keeps that text until the next CSSOM change reserializes it.
    private string? literalStyle;
    // Chrome writes a CSSOM change back to the style attribute lazily: an element
    // without one gets it, at the end of its attributes, only when it is read.
    private bool styleDirty;

    public HtmlElement(string tag) => Tag = tag.ToLowerInvariant();

    public string Tag { get; }
    public IReadOnlyList<HtmlNode> Children => children;
    public CssDeclarations Style { get; } = new();

    // ------------------------------------------------------------- attributes

    public string? GetAttribute(string name)
    {
        if (name == StyleAttribute)
        {
            SynchronizeStyle();
            return HasAttribute(StyleAttribute) ? literalStyle ?? Style.CssText : null;
        }
        foreach (var (key, value) in attributes)
            if (key == name) return value;
        return null;
    }

    public bool HasAttribute(string name) => attributes.Any(a => a.Name == name);

    public void SetAttribute(string name, string value)
    {
        name = name.ToLowerInvariant();
        if (name == StyleAttribute)
        {
            styleDirty = false;
            Style.SetCssText(value);
            literalStyle = value;
            EnsureAttribute(StyleAttribute);
            return;
        }
        for (var i = 0; i < attributes.Count; i++)
        {
            if (attributes[i].Name != name) continue;
            attributes[i] = (name, value);
            return;
        }
        attributes.Add((name, value));
    }

    public void RemoveAttribute(string name) => attributes.RemoveAll(a => a.Name == name);

    private void EnsureAttribute(string name)
    {
        if (!HasAttribute(name)) attributes.Add((name, string.Empty));
    }

    /// <summary>element.style.setProperty / element.style[name] = value: creates the style attribute on first use.</summary>
    public void SetStyle(string property, string value)
    {
        if (!Style.Set(property, value)) return;
        literalStyle = null;
        styleDirty = true;
    }

    /// <summary>element.style.cssText = text: parsed and reserialized.</summary>
    public void SetStyleText(string text)
    {
        Style.SetCssText(text);
        literalStyle = null;
        styleDirty = true;
    }

    private void SynchronizeStyle()
    {
        if (!styleDirty) return;
        styleDirty = false;
        EnsureAttribute(StyleAttribute);
    }

    public string ClassName
    {
        get => GetAttribute("class") ?? string.Empty;
        set => SetAttribute("class", value);
    }

    public IEnumerable<string> ClassList => ClassName.Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>classList.add: tokens appended once each, the attribute created where it first appears.</summary>
    public void AddClass(params string[] tokens)
    {
        var list = ClassList.ToList();
        foreach (var token in tokens)
            if (!list.Contains(token)) list.Add(token);
        SetAttribute("class", string.Join(' ', list));
    }

    /// <summary>element.dataset[key] = value.</summary>
    public void SetData(string camelKey, string value)
    {
        var name = new StringBuilder("data-");
        foreach (var c in camelKey)
        {
            if (char.IsAsciiLetterUpper(c)) name.Append('-').Append(char.ToLowerInvariant(c));
            else name.Append(c);
        }
        SetAttribute(name.ToString(), value);
    }

    public string? GetData(string camelKey)
    {
        var name = new StringBuilder("data-");
        foreach (var c in camelKey)
        {
            if (char.IsAsciiLetterUpper(c)) name.Append('-').Append(char.ToLowerInvariant(c));
            else name.Append(c);
        }
        return GetAttribute(name.ToString());
    }

    // --------------------------------------------------------------- children

    public T Append<T>(T node) where T : HtmlNode
    {
        node.Remove();
        node.Parent = this;
        children.Add(node);
        return node;
    }

    public HtmlText Append(string text) => Append(new HtmlText(text));

    public void Prepend(HtmlNode node)
    {
        node.Remove();
        node.Parent = this;
        children.Insert(0, node);
    }

    internal void RemoveChild(HtmlNode node)
    {
        if (children.Remove(node)) node.Parent = null;
    }

    /// <summary>ChildNode.replaceWith for one text node and the nodes that replace it.</summary>
    public void ReplaceChild(HtmlNode old, IEnumerable<HtmlNode> replacement)
    {
        var index = children.IndexOf(old);
        if (index < 0) return;
        children.RemoveAt(index);
        old.Parent = null;
        foreach (var node in replacement.Reverse())
        {
            node.Remove();
            node.Parent = this;
            children.Insert(index, node);
        }
    }

    public IEnumerable<HtmlElement> Descendants()
    {
        foreach (var child in children.OfType<HtmlElement>())
        {
            yield return child;
            foreach (var descendant in child.Descendants()) yield return descendant;
        }
    }

    public IEnumerable<HtmlText> DescendantTexts()
    {
        foreach (var child in children)
        {
            if (child is HtmlText text) yield return text;
            else if (child is HtmlElement element)
                foreach (var nested in element.DescendantTexts()) yield return nested;
        }
    }

    /// <summary>Element.closest for an attribute's presence.</summary>
    public HtmlElement? ClosestWithAttribute(string name)
    {
        for (var element = this; element is not null; element = element.Parent)
            if (element.HasAttribute(name)) return element;
        return null;
    }

    // ------------------------------------------------------------ serializing

    public string OuterHtml
    {
        get
        {
            var builder = new StringBuilder();
            Serialize(builder);
            return builder.ToString();
        }
    }

    public string InnerHtml
    {
        get
        {
            var builder = new StringBuilder();
            foreach (var child in children) SerializeNode(child, builder, Tag);
            return builder.ToString();
        }
    }

    private void Serialize(StringBuilder builder)
    {
        SynchronizeStyle();
        builder.Append('<').Append(Tag);
        foreach (var (name, value) in attributes)
        {
            var text = name == StyleAttribute ? literalStyle ?? Style.CssText : value;
            builder.Append(' ').Append(name).Append("=\"").Append(EscapeAttribute(text)).Append('"');
        }
        builder.Append('>');
        if (VoidElements.Contains(Tag)) return;
        foreach (var child in children) SerializeNode(child, builder, Tag);
        builder.Append("</").Append(Tag).Append('>');
    }

    private static void SerializeNode(HtmlNode node, StringBuilder builder, string parentTag)
    {
        switch (node)
        {
            case HtmlElement element:
                element.Serialize(builder);
                break;
            case HtmlText text:
                // Raw text elements keep their text as it is.
                builder.Append(parentTag is "style" or "script" ? text.Value : EscapeText(text.Value));
                break;
            case HtmlRaw raw:
                builder.Append(raw.Html);
                break;
        }
    }

    /// <summary>The HTML fragment serialization algorithm's text escape (span.textContent then innerHTML).</summary>
    public static string EscapeText(string value)
    {
        if (value.IndexOfAny(['&', '<', '>', ' ']) < 0) return value;
        return value.Replace("&", "&amp;").Replace(" ", "&nbsp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    // Chrome escapes < and > in attribute values as well (the 2025 HTML change).
    public static string EscapeAttribute(string value)
    {
        if (value.IndexOfAny(['&', '"', '<', '>', ' ']) < 0) return value;
        return value.Replace("&", "&amp;").Replace(" ", "&nbsp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    /// <summary>Node.textContent.</summary>
    public string TextContent => string.Concat(DescendantTexts().Select(t => t.Value));
}

/// <summary>
/// A CSSStyleDeclaration as Chrome serializes it: "property: value;" pairs
/// joined by spaces, in the order properties were first set, with values
/// normalized the way the browser reports them for the properties dictionary
/// structured content uses.
/// </summary>
public sealed class CssDeclarations
{
    private readonly List<(string Property, string Value)> declarations = [];

    public int Count => declarations.Count;

    public string? Get(string property) => declarations.FirstOrDefault(d => d.Property == property).Value;

    public string CssText => string.Join(' ', declarations.Select(d => $"{d.Property}: {d.Value};"));

    /// <summary>Sets a property; false (and nothing changes) for a value the browser would reject.</summary>
    public bool Set(string property, string value)
    {
        property = property.Trim().ToLowerInvariant();
        var normalized = CssValues.Normalize(property, value);
        if (normalized is null) return false;
        for (var i = 0; i < declarations.Count; i++)
        {
            if (declarations[i].Property != property) continue;
            declarations[i] = (property, normalized);
            return true;
        }
        declarations.Add((property, normalized));
        return true;
    }

    /// <summary>style.cssText = text: replaces every declaration; a repeated property keeps its last value, in its last position.</summary>
    public void SetCssText(string text)
    {
        declarations.Clear();
        var parsed = new List<(string, string)>();
        foreach (var part in CssValues.SplitDeclarations(text))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var property = part[..colon].Trim().ToLowerInvariant();
            var normalized = CssValues.Normalize(property, part[(colon + 1)..]);
            if (normalized is null) continue;
            parsed.RemoveAll(p => p.Item1 == property);
            parsed.Add((property, normalized));
        }
        declarations.AddRange(parsed);
    }
}

/// <summary>Chrome's serialization of the CSS values dictionaries put in structured content.</summary>
public static class CssValues
{
    private static readonly HashSet<string> LengthProperties =
    [
        "font-size", "margin", "margin-top", "margin-right", "margin-bottom", "margin-left", "padding", "padding-top",
        "padding-right", "padding-bottom", "padding-left", "border-width", "border-radius", "width", "height",
        "max-width", "max-height", "min-width", "min-height", "padding-inline", "top", "left", "right", "bottom",
    ];

    // A shorthand Chrome reports in its shortest form: "1px 1px" is "1px".
    private static readonly HashSet<string> BoxShorthands = ["margin", "padding", "border-width", "border-color", "border-style", "padding-inline"];

    private static readonly string[] ContainOrder = ["size", "inline-size", "layout", "style", "paint"];

    public static string? Normalize(string property, string value)
    {
        var text = CollapseWhitespace(value.Trim());
        var important = false;
        if (text.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
        {
            important = true;
            text = text[..^"!important".Length].TrimEnd();
        }
        if (text.Length == 0) return null;
        var tokens = Tokens(text).Select(token => NormalizeToken(property, token)).ToList();
        if (BoxShorthands.Contains(property) && tokens.Count is > 1 and <= 4) tokens = ShortestBox(tokens);
        // Chrome reports contain's keywords in the property's canonical order.
        if (property == "contain")
            tokens = tokens.OrderBy(t => Array.IndexOf(ContainOrder, t) is var i && i < 0 ? int.MaxValue : i).ToList();
        var result = string.Join(' ', tokens);
        return important ? result + " !important" : result;
    }

    private static string NormalizeToken(string property, string token)
    {
        if (token.StartsWith('#') && HexColor(token) is { } color) return color;
        // Chrome serializes CSS strings in double quotes.
        if (token.Length >= 2 && token[0] == '\'' && token[^1] == '\'')
            return "\"" + token[1..^1].Replace("\\'", "'").Replace("\"", "\\\"") + "\"";
        var lower = token.ToLowerInvariant();
        if (lower.StartsWith("rgb(") || lower.StartsWith("rgba(")) return FunctionSpacing(lower);
        if (LengthProperties.Contains(property) && token == "0") return "0px";
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return token;
        // Numbers with units: "0.80em" is reported as "0.8em".
        var unitStart = 0;
        while (unitStart < token.Length && (char.IsAsciiDigit(token[unitStart]) || token[unitStart] is '.' or '-' or '+')) unitStart++;
        if (unitStart > 0 && unitStart < token.Length && char.IsAsciiLetter(token[unitStart])
            && double.TryParse(token[..unitStart], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsNumber.Format(number) + token[unitStart..].ToLowerInvariant();
        return IsKeyword(token) ? lower : token;
    }

    private static bool IsKeyword(string token) => token.All(c => char.IsAsciiLetter(c) || c == '-');

    private static List<string> ShortestBox(List<string> v)
    {
        if (v.Count == 4 && v[3] == v[1]) v.RemoveAt(3);
        if (v.Count == 3 && v[2] == v[0]) v.RemoveAt(2);
        if (v.Count == 2 && v[1] == v[0]) v.RemoveAt(1);
        return v;
    }

    private static string? HexColor(string token)
    {
        var hex = token[1..];
        if (!hex.All(char.IsAsciiHexDigit) || hex.Length is not (3 or 4 or 6 or 8)) return null;
        if (hex.Length <= 4) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        var r = Convert.ToInt32(hex[..2], 16);
        var g = Convert.ToInt32(hex[2..4], 16);
        var b = Convert.ToInt32(hex[4..6], 16);
        if (hex.Length == 6) return $"rgb({r}, {g}, {b})";
        var a = Convert.ToInt32(hex[6..8], 16) / 255.0;
        return $"rgba({r}, {g}, {b}, {JsNumber.Format(Math.Round(a, 3))})";
    }

    private static string FunctionSpacing(string token)
    {
        var open = token.IndexOf('(');
        var args = token[(open + 1)..].TrimEnd(')').Split(',').Select(a => a.Trim());
        return token[..open] + "(" + string.Join(", ", args) + ")";
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space && builder.Length > 0) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>Space-separated tokens, keeping parentheses and quotes together.</summary>
    public static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var depth = 0;
        char quote = '\0';
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ' ' && depth == 0)
            {
                if (i > start) tokens.Add(text[start..i]);
                start = i + 1;
            }
        }
        if (start < text.Length) tokens.Add(text[start..]);
        return tokens;
    }

    /// <summary>A declaration block's declarations, split on semicolons outside strings and parentheses.</summary>
    public static IEnumerable<string> SplitDeclarations(string text)
    {
        var depth = 0;
        char quote = '\0';
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ';' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        if (start < text.Length) yield return text[start..];
    }
}

/// <summary>JavaScript's Number to String: shortest round trip, no trailing ".0".</summary>
public static class JsNumber
{
    public static string Format(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return "0";
        if (Math.Abs(value) is >= 1e-6 and < 1e21)
        {
            var text = value.ToString("R", CultureInfo.InvariantCulture);
            if (!text.Contains('E')) return text;
            return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d.ToString(CultureInfo.InvariantCulture) : text;
        }
        var exponent = value.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "e+").Replace("E-", "e-");
        return exponent.Contains('e') ? exponent : value.ToString(CultureInfo.InvariantCulture);
    }
}
