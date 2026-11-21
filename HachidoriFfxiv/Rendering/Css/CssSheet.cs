using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HachidoriFfxiv.Rendering.Css;

/// <summary>String helpers that respect quotes and nested parentheses.</summary>
public static class CssText
{
    public static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        char quote = '\0';
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth = Math.Max(0, depth - 1);
                    break;
                default:
                    var isSeparator = separator == ' ' ? char.IsWhiteSpace(c) : c == separator;
                    if (isSeparator && depth == 0)
                    {
                        if (i > start)
                            parts.Add(text[start..i].Trim());
                        start = i + 1;
                    }

                    break;
            }
        }

        if (start < text.Length)
            parts.Add(text[start..].Trim());
        parts.RemoveAll(p => p.Length == 0);
        return parts;
    }

    /// <summary>A CSS length in px (px, em/rem at <paramref name="fontPx"/>, unitless 0), or null.</summary>
    public static float? Length(string? value, float fontPx = 16f)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim().ToLowerInvariant();
        static float? Num(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null;
        if (v.EndsWith("px")) return Num(v[..^2]);
        if (v.EndsWith("rem")) return Num(v[..^3]) * 16f;
        if (v.EndsWith("em")) return Num(v[..^2]) * fontPx;
        if (v == "0") return 0;
        return null;
    }
}

/// <summary>One declaration block with the selectors it applies to.</summary>
public sealed record CssRule(IReadOnlyList<string> Selectors, IReadOnlyList<CssDeclaration> Declarations, int Order);

public readonly record struct CssDeclaration(string Property, string Value, bool Important);

/// <summary>
/// A tolerant stylesheet reader: comments removed, @media blocks kept only when their
/// condition holds for an ImGui popup in a game, other at-rules (keyframes, font-face) skipped.
/// </summary>
public static class CssParser
{
    public static List<CssRule> Parse(string css, bool dark, List<CssRule>? into = null, int orderBase = 0)
    {
        var rules = into ?? new List<CssRule>();
        var text = StripComments(css);
        var order = orderBase;
        ParseBlock(text, 0, text.Length, dark, rules, ref order);
        return rules;
    }

    private static string StripComments(string css)
    {
        var sb = new StringBuilder(css.Length);
        char quote = '\0';
        for (var i = 0; i < css.Length; i++)
        {
            var c = css[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < css.Length) sb.Append(css[++i]);
                else if (c == quote) quote = '\0';
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                sb.Append(c);
                continue;
            }

            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 1;
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Index of the brace that closes the block opened just before <paramref name="start"/>.</summary>
    private static int CloseOf(string text, int start, int end)
    {
        var depth = 1;
        char quote = '\0';
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }

            if (c is '"' or '\'') quote = c;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }

        return end;
    }

    private static void ParseBlock(string text, int start, int end, bool dark, List<CssRule> rules, ref int order)
    {
        var i = start;
        while (i < end)
        {
            var open = -1;
            var semicolon = -1;
            char quote = '\0';
            var parens = 0;
            for (var j = i; j < end; j++)
            {
                var c = text[j];
                if (quote != '\0')
                {
                    if (c == '\\') j++;
                    else if (c == quote) quote = '\0';
                    continue;
                }

                if (c is '"' or '\'') quote = c;
                else if (c == '(') parens++;
                else if (c == ')') parens = Math.Max(0, parens - 1);
                else if (c == '{' && parens == 0) { open = j; break; }
                else if (c == ';' && parens == 0) { semicolon = j; break; }
            }

            if (open < 0)
                return;
            var prelude = text[i..open].Trim();
            if (semicolon >= 0 && semicolon < open)
            {
                // A statement at-rule (@import, @charset) or stray text.
                i = semicolon + 1;
                continue;
            }

            var close = CloseOf(text, open + 1, end);
            if (prelude.StartsWith('@'))
            {
                if (AtRuleApplies(prelude, dark))
                    ParseBlock(text, open + 1, close, dark, rules, ref order);
            }
            else if (prelude.Length > 0)
            {
                var selectors = CssText.SplitTopLevel(prelude, ',');
                var declarations = ParseDeclarations(text[(open + 1)..close]);
                if (selectors.Count > 0 && declarations.Count > 0)
                    rules.Add(new CssRule(selectors, declarations, order++));
            }

            i = close + 1;
        }
    }

    private static bool AtRuleApplies(string prelude, bool dark)
    {
        var p = prelude.ToLowerInvariant();
        if (p.StartsWith("@layer") || p.StartsWith("@supports") || p.StartsWith("@scope"))
            return true;
        if (!p.StartsWith("@media"))
            return false;
        // The popup has no Windows contrast theme and no reduced-motion preference.
        if (p.Contains("forced-colors: active") || p.Contains("forced-colors:active")) return false;
        if (p.Contains("prefers-color-scheme: light") || p.Contains("prefers-color-scheme:light")) return !dark;
        if (p.Contains("prefers-color-scheme: dark") || p.Contains("prefers-color-scheme:dark")) return dark;
        if (p.Contains("forced-colors: none") || p.Contains("forced-colors:none")) return true;
        if (p.Contains("prefers-reduced") || p.Contains("print") || p.Contains("hover: none") || p.Contains("pointer: coarse")) return false;
        // Width queries refer to the page, which a game popup does not have.
        return p.Trim() is "@media screen" or "@media all";
    }

    public static List<CssDeclaration> ParseDeclarations(string block)
    {
        var list = new List<CssDeclaration>();
        foreach (var part in CssText.SplitTopLevel(block, ';'))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0)
                continue;
            var property = part[..colon].Trim();
            var value = part[(colon + 1)..].Trim();
            if (property.Contains('{') || value.Contains('{'))
                continue;
            var important = false;
            var bang = value.LastIndexOf("!important", StringComparison.OrdinalIgnoreCase);
            if (bang >= 0)
            {
                important = true;
                value = value[..bang].Trim();
            }

            if (!property.StartsWith("--"))
                property = property.ToLowerInvariant();
            list.Add(new CssDeclaration(property, value, important));
        }

        return list;
    }
}
