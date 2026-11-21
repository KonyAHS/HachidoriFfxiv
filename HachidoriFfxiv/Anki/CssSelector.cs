using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HachidoriFfxiv.Anki;

/// <summary>
/// Element.matches for the selectors in Yomitan's structured-content-style.json,
/// on a detached fragment: compound selectors of type, class and attribute
/// tests, :not() and the descendant, child and sibling combinators. :root,
/// :hover, :focus and the structural pseudo-classes never match a note field's
/// detached nodes here, which is how the browser answers for this fragment.
/// A selector list with a pseudo-element throws in the browser, so it matches nothing.
/// </summary>
public static class CssSelector
{
    public static bool Matches(HtmlElement element, string selectorList)
    {
        var selectors = SplitList(selectorList);
        if (selectors.Any(s => s.Contains("::", StringComparison.Ordinal))) return false;
        return selectors.Any(selector => MatchesComplex(element, Parse(selector)));
    }

    private static List<string> SplitList(string text)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '(' or '[') depth++;
            else if (text[i] is ')' or ']') depth--;
            else if (text[i] == ',' && depth == 0)
            {
                list.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        list.Add(text[start..].Trim());
        return list;
    }

    private sealed record Step(string Compound, char Combinator); // combinator to the step on the left

    private static List<Step> Parse(string selector)
    {
        var steps = new List<Step>();
        var combinator = '\0';
        int i = 0, depth = 0;
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            if (current.Length == 0) return;
            steps.Add(new Step(current.ToString(), combinator));
            current.Clear();
            combinator = ' ';
        }
        for (; i < selector.Length; i++)
        {
            var c = selector[i];
            if (c is '(' or '[') depth++;
            if (c is ')' or ']') depth--;
            if (depth == 0 && c is ' ' or '>' or '~' or '+')
            {
                Flush();
                if (c != ' ') combinator = c;
                continue;
            }
            current.Append(c);
        }
        Flush();
        if (steps.Count > 0) steps[0] = steps[0] with { Combinator = '\0' };
        return steps;
    }

    private static bool MatchesComplex(HtmlElement element, List<Step> steps) => MatchFrom(element, steps, steps.Count - 1);

    private static bool MatchFrom(HtmlElement element, List<Step> steps, int index)
    {
        if (!MatchesCompound(element, steps[index].Compound)) return false;
        if (index == 0) return true;
        switch (steps[index].Combinator)
        {
            case '>':
                return element.Parent is { } parent && MatchFrom(parent, steps, index - 1);
            case '~':
                foreach (var sibling in PreviousSiblings(element))
                    if (MatchFrom(sibling, steps, index - 1)) return true;
                return false;
            case '+':
                return PreviousSiblings(element).FirstOrDefault() is { } previous && MatchFrom(previous, steps, index - 1);
            default:
                for (var ancestor = element.Parent; ancestor is not null; ancestor = ancestor.Parent)
                    if (MatchFrom(ancestor, steps, index - 1)) return true;
                return false;
        }
    }

    private static IEnumerable<HtmlElement> PreviousSiblings(HtmlElement element)
    {
        if (element.Parent is null) yield break;
        var siblings = element.Parent.Children.OfType<HtmlElement>().ToList();
        for (var i = siblings.IndexOf(element) - 1; i >= 0; i--) yield return siblings[i];
    }

    private static bool MatchesCompound(HtmlElement element, string compound)
    {
        var i = 0;
        // Type selector.
        var type = new System.Text.StringBuilder();
        while (i < compound.Length && (char.IsAsciiLetterOrDigit(compound[i]) || compound[i] is '-' or '_' or '*')) type.Append(compound[i++]);
        if (type.Length > 0 && type.ToString() != "*" && !string.Equals(type.ToString(), element.Tag, StringComparison.OrdinalIgnoreCase)) return false;
        while (i < compound.Length)
        {
            var c = compound[i];
            if (c == '.')
            {
                var name = ReadIdent(compound, ref i);
                if (!element.ClassList.Contains(name)) return false;
            }
            else if (c == '#')
            {
                var name = ReadIdent(compound, ref i);
                if (element.GetAttribute("id") != name) return false;
            }
            else if (c == '[')
            {
                var end = compound.IndexOf(']', i);
                if (end < 0 || !MatchesAttribute(element, compound[(i + 1)..end])) return false;
                i = end + 1;
            }
            else if (c == ':')
            {
                var start = ++i;
                while (i < compound.Length && (char.IsAsciiLetterOrDigit(compound[i]) || compound[i] == '-')) i++;
                var name = compound[start..i];
                string? argument = null;
                if (i < compound.Length && compound[i] == '(')
                {
                    var depth = 0;
                    var open = i;
                    for (; i < compound.Length; i++)
                    {
                        if (compound[i] == '(') depth++;
                        else if (compound[i] == ')' && --depth == 0) break;
                    }
                    argument = compound[(open + 1)..i];
                    i++;
                }
                if (name == "not")
                {
                    if (argument is null || MatchesCompound(element, argument.Trim())) return false;
                }
                else
                {
                    // :root, :hover, :focus and structural tests do not hold for a detached note fragment.
                    return false;
                }
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    private static string ReadIdent(string text, ref int i)
    {
        var start = ++i;
        while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '-' or '_' || text[i] > 0x7f)) i++;
        return text[start..i];
    }

    private static bool MatchesAttribute(HtmlElement element, string test)
    {
        var operators = new[] { "^=", "$=", "*=", "~=", "|=", "=" };
        foreach (var op in operators)
        {
            var at = test.IndexOf(op, StringComparison.Ordinal);
            if (at < 0) continue;
            var name = test[..at].Trim();
            var expected = test[(at + op.Length)..].Trim().Trim('"', '\'');
            var actual = element.GetAttribute(name);
            if (actual is null) return false;
            return op switch
            {
                "^=" => expected.Length > 0 && actual.StartsWith(expected, StringComparison.Ordinal),
                "$=" => expected.Length > 0 && actual.EndsWith(expected, StringComparison.Ordinal),
                "*=" => expected.Length > 0 && actual.Contains(expected, StringComparison.Ordinal),
                "~=" => actual.Split(' ').Contains(expected),
                "|=" => actual == expected || actual.StartsWith(expected + "-", StringComparison.Ordinal),
                _ => actual == expected,
            };
        }
        return element.HasAttribute(test.Trim());
    }
}

/// <summary>
/// Yomitan's CssStyleApplier.applyClassStyles with structured-content-style.json
/// (Yomitan 67db60d, as Hachidori bundles it): matching class rules become the
/// element's inline style, ahead of the style it already has.
/// </summary>
public static class StructuredContentStyle
{
    private sealed record Rule(string Selectors, string CssText);

    private static readonly Lazy<List<Rule>> Rules = new(() =>
    {
        using var stream = typeof(StructuredContentStyle).Assembly.GetManifestResourceStream("HachidoriFfxiv.Anki.structured-content-style.json")
            ?? throw new InvalidOperationException("The bundled structured-content-style.json is missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().Select(rule => new Rule(
            string.Join(',', rule.GetProperty("selectors").EnumerateArray().Select(s => s.GetString())),
            string.Concat(rule.GetProperty("styles").EnumerateArray().Select(s => $"{s[0].GetString()}:{s[1].GetString()};")))).ToList();
    });

    private static readonly Dictionary<string, List<Rule>> Candidates = new();

    // _selectorMightMatch: a rule can only match if it names one of the classes.
    private static List<Rule> RulesForClass(string className)
    {
        lock (Candidates)
        {
            if (Candidates.TryGetValue(className, out var rules)) return rules;
            var tokens = className.Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries);
            rules = Rules.Value.Where(rule => tokens.Any(token =>
            {
                for (var start = rule.Selectors.IndexOf('.' + token, StringComparison.Ordinal); start >= 0;
                     start = rule.Selectors.IndexOf('.' + token, start + 1, StringComparison.Ordinal))
                {
                    var next = start + token.Length + 1;
                    if (next >= rule.Selectors.Length || !(char.IsAsciiLetterOrDigit(rule.Selectors[next]) || rule.Selectors[next] is '_' or '-'))
                        return true;
                }
                return false;
            })).ToList();
            Candidates[className] = rules;
            return rules;
        }
    }

    public static void Apply(HtmlElement root)
    {
        var styled = new List<(HtmlElement, string)>();
        foreach (var element in root.Descendants().Where(e => e.HasAttribute("class")))
        {
            var cssText = string.Concat(RulesForClass(element.ClassName).Where(rule => CssSelector.Matches(element, rule.Selectors)).Select(r => r.CssText));
            if (cssText.Length > 0) styled.Add((element, cssText + element.Style.CssText));
        }
        foreach (var (element, cssText) in styled) element.SetAttribute("style", cssText);
    }
}
