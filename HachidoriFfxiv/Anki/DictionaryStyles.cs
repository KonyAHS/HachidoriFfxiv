using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HachidoriFfxiv.Anki;

/// <summary>
/// A dictionary's own stylesheet inside a note field, as Hachidori's
/// applyDictionaryStyles writes it for Anki (render/glossary.js): rules that
/// could load resources or reach outside the glossary are dropped, the
/// dictionary's custom properties are renamed under a random prefix, and every
/// selector is prefixed with the dictionary's item of this glossary. The browser
/// reserializes the sheet through CSSOM; this follows its formatting closely
/// but not byte for byte (the prefix is random there too).
/// </summary>
public static partial class DictionaryStyles
{
    private const int MaxDictionaryStyleBytes = 256 * 1024;
    private const int MaxDictionaryStylesBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> StyleVariables = ["--text-color", "--background-color", "--fg", "--canvas", "--font-size-no-units"];
    private static readonly HashSet<string> GroupRules = ["media", "supports", "container"];
    private static readonly HashSet<string> FontFamilies =
    [
        "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui", "ui-serif", "ui-sans-serif", "ui-monospace",
        "ui-rounded", "math", "fangsong", "inherit", "initial", "unset", "revert", "revert-layer",
    ];

    [GeneratedRegex(@"\b(?:url|src|image-set|paint|attr)\s*\(|(?<![A-Za-z0-9_\u0080-￿-])--[A-Za-z0-9_\u0080-￿-]+\(", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeFunction();

    [GeneratedRegex(@"""[^""]*""|'[^']*'|/\*[\s\S]*?\*/|\bvar\(\s*(--[A-Za-z0-9_\u0080-￿-]+)|(?<![A-Za-z0-9_\u0080-￿-])--[A-Za-z0-9_\u0080-￿-]+|[()]", RegexOptions.IgnoreCase)]
    private static partial Regex VariableTokens();

    /// <summary>Appends one &lt;style&gt; per dictionary with styles to <paramref name="host"/>.</summary>
    public static void Apply(HtmlElement host, long generation, IEnumerable<(string Dictionary, string Styles)> entries, Func<string, string> scope)
    {
        var prefix = "--hd" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + "-";
        var seen = new HashSet<string>();
        var total = 0;
        foreach (var (dictionary, styles) in entries)
        {
            var bytes = Encoding.UTF8.GetByteCount(styles);
            if (dictionary.Length == 0 || styles.Length == 0 || !seen.Add(dictionary) || bytes > MaxDictionaryStyleBytes
                || total + bytes > MaxDictionaryStylesBytes) continue;
            total += bytes;
            var rules = Filter(Parse(StripComments(styles)), prefix);
            Prefix(rules, NormalizeSelector(scope(dictionary)));
            var style = new HtmlElement("style");
            style.SetData("hoshidictsDictionaryStyle", dictionary);
            style.SetData("hoshidictsGeneration", generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var text = string.Join("\n", rules.Select(Serialize));
            // A serialized closing style tag must not end the element early.
            style.Append(Regex.Replace(text, "</style", m => @"<\/" + m.Value[2..], RegexOptions.IgnoreCase));
            host.Append(style);
        }
    }

    /// <summary>The attribute scope Hachidori's Anki renderer gives a dictionary's rules.</summary>
    public static string GlossaryScope(string dictionary) => $".yomitan-glossary [data-dictionary={CssEscape(dictionary)}]";

    // ------------------------------------------------------------------ model

    private abstract record CssRule;
    private sealed record StyleRule(string Selector, List<(string Property, string Value)> Declarations, List<CssRule> Children) : CssRule
    {
        public string Selector { get; set; } = Selector;
    }
    private sealed record GroupRule(string Name, string Condition, List<CssRule> Rules) : CssRule;

    private static string StripComments(string css) => Regex.Replace(css, @"/\*[\s\S]*?\*/", string.Empty);

    private static List<CssRule> Parse(string css)
    {
        var rules = new List<CssRule>();
        var i = 0;
        while (i < css.Length)
        {
            while (i < css.Length && char.IsWhiteSpace(css[i])) i++;
            if (i >= css.Length) break;
            var preludeStart = i;
            var (stop, at) = ScanTo(css, i, '{', ';');
            var prelude = css[preludeStart..Math.Min(stop, css.Length)].Trim();
            if (stop >= css.Length) break;
            if (at == ';')
            {
                // Skip statement at-rules (@import, @charset...) and stray text.
                i = stop + 1;
                continue;
            }
            var end = MatchingBrace(css, stop);
            var body = css[(stop + 1)..Math.Min(end, css.Length)];
            i = end + 1;
            if (prelude.StartsWith('@'))
            {
                var nameEnd = 1;
                while (nameEnd < prelude.Length && (char.IsAsciiLetterOrDigit(prelude[nameEnd]) || prelude[nameEnd] == '-')) nameEnd++;
                var name = prelude[1..nameEnd].ToLowerInvariant();
                if (GroupRules.Contains(name))
                    rules.Add(new GroupRule(name, Collapse(prelude[nameEnd..].Trim()), Parse(body)));
            }
            else if (prelude.Length > 0)
            {
                var (declarations, children) = StyleBody(body);
                rules.Add(new StyleRule(prelude, declarations, children));
            }
        }
        return rules;
    }

    private static (int Index, char Found) ScanTo(string css, int i, params char[] stops)
    {
        char quote = '\0';
        var depth = 0;
        for (; i < css.Length; i++)
        {
            var c = css[i];
            if (c == '\\') { i++; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (depth == 0 && stops.Contains(c)) return (i, c);
            if (c is '"' or '\'') quote = c;
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
        }
        return (css.Length, '\0');
    }

    // A style rule's block: its declarations and the rules nested in it (CSS nesting).
    private static (List<(string, string)> Declarations, List<CssRule> Children) StyleBody(string body)
    {
        var declarations = new StringBuilder();
        var children = new List<CssRule>();
        var i = 0;
        while (i < body.Length)
        {
            var (stop, found) = ScanTo(body, i, '{', ';');
            if (found == '{')
            {
                // The text since the last declaration is the nested rule's prelude.
                var end = MatchingBrace(body, stop);
                children.AddRange(Parse(body[i..Math.Min(end + 1, body.Length)]));
                i = end + 1;
                continue;
            }
            declarations.Append(body[i..Math.Min(stop + 1, body.Length)]);
            i = stop + 1;
        }
        return (Declarations(declarations.ToString()), children);
    }

    private static int MatchingBrace(string css, int open)
    {
        var depth = 0;
        char quote = '\0';
        for (var i = open; i < css.Length; i++)
        {
            var c = css[i];
            if (c == '\\') { i++; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return css.Length;
    }

    private static List<(string, string)> Declarations(string body)
    {
        var list = new List<(string, string)>();
        foreach (var part in CssValues.SplitDeclarations(body))
        {
            if (part.Contains('{')) continue; // nested rules are not kept
            var colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var property = part[..colon].Trim();
            var value = part[(colon + 1)..].Trim();
            if (value.Length == 0) continue;
            if (!property.StartsWith("--", StringComparison.Ordinal))
            {
                property = property.ToLowerInvariant();
                value = CssValues.Normalize(property, value) ?? value;
            }
            list.RemoveAll(d => d.Item1 == property);
            list.Add((property, value));
        }
        return list;
    }

    // ------------------------------------------------------------- filtering

    private static List<CssRule> Filter(List<CssRule> rules, string prefix)
    {
        var kept = new List<CssRule>();
        foreach (var rule in rules)
        {
            switch (rule)
            {
                case StyleRule style:
                    if (!IsSafe(style)) continue;
                    RenameVariables(style, prefix);
                    var children = Filter(style.Children, prefix);
                    style.Children.Clear();
                    style.Children.AddRange(children);
                    kept.Add(style);
                    break;
                case GroupRule group:
                    kept.Add(group with { Rules = Filter(group.Rules, prefix) });
                    break;
            }
        }
        return kept;
    }

    private static bool IsSafe(StyleRule rule)
    {
        var text = Block(rule.Declarations);
        if (text.Contains('\\') || UnsafeFunction().IsMatch(text)) return false;
        return rule.Declarations.Where(d => d.Property is "font-family" or "font").All(d => d.Property != "font-family"
            || d.Value.Split(',').All(f => FontFamilies.Contains(f.Trim().ToLowerInvariant())));
    }

    private static void RenameVariables(StyleRule rule, string prefix)
    {
        var text = Block(rule.Declarations);
        if (!text.Contains("--", StringComparison.Ordinal)) return;
        var suffixes = new Stack<string>();
        var renamed = VariableTokens().Replace(text, match =>
        {
            var token = match.Value;
            var variable = match.Groups[1].Success ? match.Groups[1].Value : null;
            var name = variable ?? (token.StartsWith("--", StringComparison.Ordinal) ? token : null);
            if (name is not null && !StyleVariables.Contains(name))
            {
                if (variable is not null) suffixes.Push(string.Empty);
                var at = token.IndexOf(name, StringComparison.Ordinal);
                return token[..at] + prefix + name[2..] + token[(at + name.Length)..];
            }
            if (variable is not null)
            {
                var numeric = variable == "--font-size-no-units";
                suffixes.Push(numeric ? " * 1)" : " 100%, transparent)");
                return (numeric ? "calc(" : "color-mix(in srgb, ") + token;
            }
            if (token == "(") suffixes.Push(string.Empty);
            return token == ")" ? token + (suffixes.Count > 0 ? suffixes.Pop() : string.Empty) : token;
        });
        rule.Declarations.Clear();
        rule.Declarations.AddRange(Declarations(renamed));
    }

    private static void Prefix(List<CssRule> rules, string scope)
    {
        for (var i = rules.Count - 1; i >= 0; i--)
        {
            switch (rules[i])
            {
                case StyleRule style:
                    style.Selector = string.Join(", ", SplitSelectorList(style.Selector).Select(s => $"{scope} {NormalizeSelector(s)}"));
                    // Nested rules stay relative to their prefixed parent.
                    NormalizeNested(style.Children);
                    break;
                case GroupRule group:
                    Prefix(group.Rules, scope);
                    break;
            }
        }
    }

    // --------------------------------------------------------- serialization

    private static string Block(List<(string Property, string Value)> declarations) =>
        string.Join(' ', declarations.Select(d => $"{d.Property}: {d.Value};"));

    private static void NormalizeNested(List<CssRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule is StyleRule style)
            {
                style.Selector = string.Join(", ", SplitSelectorList(style.Selector).Select(NormalizeSelector));
                NormalizeNested(style.Children);
            }
            else if (rule is GroupRule group)
            {
                NormalizeNested(group.Rules);
            }
        }
    }

    private static string Serialize(CssRule rule) => rule switch
    {
        StyleRule { Children.Count: > 0 } nested => $"{nested.Selector} {{\n"
            + (nested.Declarations.Count > 0 ? $"  {Block(nested.Declarations)}\n" : string.Empty)
            + string.Concat(nested.Children.Select(c => "  " + Serialize(c).Replace("\n", "\n  ") + "\n")) + "}",
        StyleRule style => style.Declarations.Count == 0 ? $"{style.Selector} {{ }}" : $"{style.Selector} {{ {Block(style.Declarations)} }}",
        // Chrome indents a group's rules once and leaves the lines inside them as they are.
        GroupRule group => $"@{group.Name} {group.Condition} {{\n" + string.Concat(group.Rules.Select(r => "  " + Serialize(r) + "\n")) + "}",
        _ => string.Empty,
    };

    private static List<string> SplitSelectorList(string text)
    {
        var selectors = new List<string>();
        int depth = 0, start = 0;
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\') { i++; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                selectors.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        selectors.Add(text[start..].Trim());
        return selectors;
    }

    /// <summary>A selector as the browser reports it: combinators spaced, attribute values quoted.</summary>
    public static string NormalizeSelector(string selector)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < selector.Length; i++)
        {
            var c = selector[i];
            if (c == '[')
            {
                var end = ScanTo(selector, i + 1, ']').Index;
                builder.Append(NormalizeAttribute(selector[(i + 1)..Math.Min(end, selector.Length)]));
                i = end;
                continue;
            }
            if (c is '>' or '+' or '~')
            {
                while (builder.Length > 0 && builder[^1] == ' ') builder.Length--;
                builder.Append(' ').Append(c).Append(' ');
                while (i + 1 < selector.Length && char.IsWhiteSpace(selector[i + 1])) i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
                continue;
            }
            builder.Append(c);
        }
        return builder.ToString().Trim();
    }

    private static string NormalizeAttribute(string test)
    {
        foreach (var op in new[] { "^=", "$=", "*=", "~=", "|=", "=" })
        {
            var at = test.IndexOf(op, StringComparison.Ordinal);
            if (at < 0) continue;
            var name = test[..at].Trim();
            var raw = test[(at + op.Length)..].Trim();
            var flag = string.Empty;
            if (raw.Length > 2 && raw[^2] == ' ' && raw[^1] is 'i' or 's') { flag = " " + raw[^1]; raw = raw[..^2].Trim(); }
            var value = raw.Length >= 2 && raw[0] is '"' or '\'' && raw[^1] == raw[0] ? raw[1..^1] : CssUnescape(raw);
            return $"[{name}{op}\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"{flag}]";
        }
        return $"[{test.Trim()}]";
    }

    private static string CssUnescape(string value)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length) { builder.Append(value[i]); continue; }
            var hex = new StringBuilder();
            var j = i + 1;
            while (j < value.Length && hex.Length < 6 && char.IsAsciiHexDigit(value[j])) hex.Append(value[j++]);
            if (hex.Length > 0)
            {
                builder.Append(char.ConvertFromUtf32(Convert.ToInt32(hex.ToString(), 16)));
                if (j < value.Length && value[j] == ' ') j++;
                i = j - 1;
            }
            else
            {
                builder.Append(value[++i]);
            }
        }
        return builder.ToString();
    }

    /// <summary>CSS.escape.</summary>
    public static string CssEscape(string value)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\0') builder.Append('�');
            else if (c is >= '\u0001' and <= '\u001f' or '\u007f'
                     || (i == 0 && char.IsAsciiDigit(c))
                     || (i == 1 && char.IsAsciiDigit(c) && value[0] == '-'))
                builder.Append('\\').Append(((int)c).ToString("x")).Append(' ');
            else if (i == 0 && c == '-' && value.Length == 1) builder.Append("\\-");
            else if (c >= 0x80 || c is '-' or '_' || char.IsAsciiLetterOrDigit(c)) builder.Append(c);
            else builder.Append('\\').Append(c);
        }
        return builder.ToString();
    }

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Replace("( ", "(").Replace(" )", ")")
        .Replace(":", ": ").Replace(":  ", ": ");
}
