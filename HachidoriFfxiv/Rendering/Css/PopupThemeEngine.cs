using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace HachidoriFfxiv.Rendering.Css;

/// <summary>
/// Computes a <see cref="PopupTheme"/> from Hachidori's real theme stylesheets and the user's
/// custom popup CSS. It runs a small cascade (selector specificity, source order, !important and
/// custom properties with var()) and maps Hachidori's class names onto the parts of the ImGui popup:
/// the popup box and its ::before frame, entries, dictionary sections, the headword and reading,
/// tag/dictionary/frequency chips, buttons, links and the scrollbar.
/// Layout properties (flex, grid, positioning) have no ImGui equivalent and are ignored.
/// </summary>
public static partial class PopupThemeEngine
{
    public enum Role
    {
        Host,
        Popup,
        PopupFrame,
        PopupOrnament,
        Scroll,
        Entry,
        EntrySibling,
        Section,
        Headword,
        Reading,
        Definitions,
        DictionaryChip,
        TagChip,
        FrequencyChip,
        InflectionChip,
        PitchMark,
        Button,
        Link,
    }

    private static readonly Dictionary<string, Role> ClassRoles = new()
    {
        ["gsm-hoshidicts-popup"] = Role.Popup,
        ["jl-scroll"] = Role.Scroll, ["nazeka-scroll"] = Role.Scroll, ["plain-scroll"] = Role.Scroll,
        ["crystal-scroll"] = Role.Scroll, ["gsm-hoshidicts-scroll"] = Role.Scroll, ["gsm-hoshidicts-content"] = Role.Scroll,
        ["gsm-hoshidicts-entry"] = Role.Entry, ["jl-entry"] = Role.Entry, ["crystal-result"] = Role.Entry,
        ["gsm-hoshidicts-glossary-card"] = Role.Section, ["crystal-block"] = Role.Section,
        ["gsm-hoshidicts-expression"] = Role.Headword, ["jl-spelling"] = Role.Headword,
        ["nazeka-word"] = Role.Headword, ["crystal-spelling"] = Role.Headword,
        ["gsm-hoshidicts-reading"] = Role.Reading, ["jl-reading"] = Role.Reading,
        ["nazeka-reading"] = Role.Reading, ["crystal-reading"] = Role.Reading,
        ["gsm-hoshidicts-definitions"] = Role.Definitions, ["nazeka-sense"] = Role.Definitions,
        ["gsm-hoshidicts-tag-dictionary"] = Role.DictionaryChip, ["gsm-hoshidicts-dictionary"] = Role.DictionaryChip,
        ["jl-dictionary"] = Role.DictionaryChip, ["nazeka-dictionary"] = Role.DictionaryChip,
        ["crystal-plaque"] = Role.DictionaryChip, ["crystal-dictionary"] = Role.DictionaryChip,
        ["gsm-hoshidicts-tag"] = Role.TagChip, ["crystal-tag"] = Role.TagChip,
        ["gsm-hoshidicts-tag-frequency"] = Role.FrequencyChip, ["jl-frequency"] = Role.FrequencyChip,
        ["nazeka-frequency"] = Role.FrequencyChip, ["crystal-frequency"] = Role.FrequencyChip,
        ["jl-deconj"] = Role.InflectionChip, ["nazeka-deconj"] = Role.InflectionChip,
        ["gsm-hoshidicts-inflection"] = Role.InflectionChip, ["crystal-deconj"] = Role.InflectionChip,
        ["jl-mora"] = Role.PitchMark, ["gsm-hoshidicts-pitch-mora"] = Role.PitchMark,
        ["gsm-hoshidicts-audio-button"] = Role.Button, ["gsm-hoshidicts-mine-button"] = Role.Button,
        ["gsm-hoshidicts-link"] = Role.Link,
    };

    private static readonly Dictionary<string, Role> ElementRoles = new()
    {
        ["button"] = Role.Button, ["rt"] = Role.Reading, ["a"] = Role.Link,
    };

    /// <summary>Containers a matched element may sit inside; anything else (menus, editors, tabs) is a different context.</summary>
    private static readonly HashSet<string> AncestorClasses =
    [
        "gsm-hoshidicts-popup", "gsm-hoshidicts-entry", "jl-entry", "crystal-result", "jl-scroll", "nazeka-scroll",
        "plain-scroll", "crystal-scroll", "gsm-hoshidicts-scroll", "gsm-hoshidicts-content", "gsm-hoshidicts-entry-actions",
        "jl-top", "crystal-head", "crystal-word", "gsm-hoshidicts-headword", "gsm-hoshidicts-entry-header",
        "crystal-banner", "jl-frequency", "gsm-hoshidicts-definitions", "crystal-block", "gsm-hoshidicts-expression",
        "gsm-hoshidicts-glossary-card", "gsm-hoshidicts-tags", "gsm-hoshidicts-frequency-metadata",
        "gsm-hoshidicts-audio-control", "jl-header", "crystal-senses", "crystal-sense", "gsm-hoshidicts-entry-body",
    ];

    private static readonly HashSet<string> BlockedPseudo =
    [
        "hover", "focus", "focus-visible", "focus-within", "active", "disabled", "checked", "empty", "has",
        "first-child", "last-child", "only-child", "nth-child", "nth-of-type", "first-of-type", "last-of-type",
        "placeholder-shown", "visited", "target", "lang", "dir", "where", "defined", "popover-open",
    ];

    private sealed record Match(Role Role, int Specificity);

    private readonly record struct Winner(string Value, bool Important, int Specificity, int Order);

    public sealed record Input(string Renderer, string Palette, string Variant, string ThemeCss, string CustomCss, float OpacityPercent);

    public static PopupTheme Compute(Input input)
    {
        // A first pass finds the colour scheme; @media (prefers-color-scheme) and light-dark() depend on it.
        var dark = true;
        var styles = Cascade(input, dark);
        var scheme = (styles.Value(Role.Popup, "color-scheme") ?? styles.Value(Role.Host, "color-scheme") ?? "dark").Trim();
        if (scheme.StartsWith("light", StringComparison.OrdinalIgnoreCase) && !scheme.Contains("dark"))
        {
            dark = false;
            styles = Cascade(input, dark);
        }

        return Build(input, styles, dark);
    }

    // ------------------------------------------------------------------ cascade

    private sealed class Styles
    {
        public readonly Dictionary<Role, Dictionary<string, Winner>> Props = new();

        public void Set(Role role, CssDeclaration d, int specificity, int order)
        {
            if (!Props.TryGetValue(role, out var map))
                Props[role] = map = new Dictionary<string, Winner>();
            var next = new Winner(d.Value, d.Important, specificity, order);
            if (!map.TryGetValue(d.Property, out var old) || Beats(next, old))
                map[d.Property] = next;
        }

        private static bool Beats(Winner a, Winner b) =>
            a.Important != b.Important ? a.Important
            : a.Specificity != b.Specificity ? a.Specificity > b.Specificity
            : a.Order >= b.Order;

        public string? Raw(Role role, string property) =>
            Props.TryGetValue(role, out var map) && map.TryGetValue(property, out var w) ? w.Value : null;

        /// <summary>A property with var() resolved against the role's own, the popup's and the host's custom properties.</summary>
        public string? Value(Role role, string property)
        {
            var raw = Raw(role, property);
            return raw is null ? null : Substitute(raw, role, 0);
        }

        public string? Variable(Role role, string name)
        {
            foreach (var scope in new[] { role, Role.Popup, Role.Host }.Distinct())
            {
                if (Raw(scope, name) is { } v)
                    return Substitute(v, scope, 0);
            }

            return null;
        }

        private string Substitute(string value, Role role, int depth)
        {
            if (depth > 16)
                return value;
            var index = value.IndexOf("var(", StringComparison.Ordinal);
            while (index >= 0)
            {
                var close = MatchingParen(value, index + 3);
                if (close < 0)
                    break;
                var inner = value[(index + 4)..close];
                var comma = TopLevelComma(inner);
                var name = (comma < 0 ? inner : inner[..comma]).Trim();
                var fallback = comma < 0 ? null : inner[(comma + 1)..].Trim();
                string? replacement = null;
                foreach (var scope in new[] { role, Role.Popup, Role.Host }.Distinct())
                {
                    if (Raw(scope, name) is { } v)
                    {
                        replacement = Substitute(v, scope, depth + 1);
                        break;
                    }
                }

                replacement ??= fallback is null ? string.Empty : Substitute(fallback, role, depth + 1);
                value = value[..index] + replacement + value[(close + 1)..];
                index = value.IndexOf("var(", index + replacement.Length, StringComparison.Ordinal);
            }

            return value.Trim();
        }
    }

    private static int MatchingParen(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }

        return -1;
    }

    private static int TopLevelComma(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (s[i] == ',' && depth == 0) return i;
        }

        return -1;
    }

    private static Styles Cascade(Input input, bool dark)
    {
        var rules = new List<CssRule>();
        CssParser.Parse(input.ThemeCss, dark, rules);
        CssParser.Parse(input.CustomCss, dark, rules, 1_000_000);
        var attributes = new Dictionary<string, string>
        {
            ["data-hoshidicts-theme"] = input.Palette,
            ["data-hoshidicts-theme-variant"] = input.Variant,
        };

        var styles = new Styles();
        foreach (var rule in rules)
        {
            foreach (var selector in rule.Selectors)
            {
                foreach (var match in Analyze(selector, attributes))
                {
                    foreach (var declaration in rule.Declarations)
                        styles.Set(match.Role, declaration, match.Specificity, rule.Order);
                }
            }
        }

        // The host sets these inline on its element, above any stylesheet.
        var inline = new[]
        {
            new CssDeclaration("--gsm-hoshidicts-popup-opacity", $"{input.OpacityPercent.ToString(CultureInfo.InvariantCulture)}%", false),
            new CssDeclaration("--gsm-hoshidicts-popup-scale", "100%", false),
            new CssDeclaration("--gsm-hoshidicts-page-zoom", "1", false),
        };
        foreach (var d in inline)
            styles.Set(Role.Host, d, 1_000_000, int.MaxValue);
        return styles;
    }

    [GeneratedRegex(@"^\s*(?::host(?:\((?<cond>[^)]*)\))?|:root|html(?<cond>(?:\[[^\]]*\])*))(?<rest>.*)$", RegexOptions.Singleline)]
    private static partial Regex HostPrefix();

    [GeneratedRegex(@"\[\s*(?<name>[\w-]+)\s*=\s*[""']?(?<value>[^""'\]]*)[""']?\s*\]")]
    private static partial Regex AttributeEquals();

    [GeneratedRegex(@"\s*(>|\+|~)\s*|\s+")]
    private static partial Regex Combinator();

    /// <summary>The popup parts a selector styles, if it styles one in its resting state.</summary>
    private static IEnumerable<Match> Analyze(string selector, Dictionary<string, string> attributes)
    {
        var specificity = 0;
        var rest = selector.Trim();
        var host = HostPrefix().Match(rest);
        if (host.Success)
        {
            var condition = host.Groups["cond"].Value;
            foreach (System.Text.RegularExpressions.Match attr in AttributeEquals().Matches(condition))
            {
                if (!attributes.TryGetValue(attr.Groups["name"].Value, out var actual) || actual != attr.Groups["value"].Value)
                    yield break;
                specificity += 10;
            }

            if (condition.Contains('[') && !AttributeEquals().IsMatch(condition))
                yield break;
            rest = host.Groups["rest"].Value.Trim();
            specificity += 1;
            if (rest.Length == 0)
            {
                yield return new Match(Role.Host, specificity);
                yield break;
            }
        }

        // Split into compounds, remembering whether a sibling combinator was used.
        var compounds = new List<string>();
        var sibling = false;
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= rest.Length; i++)
        {
            var c = i < rest.Length ? rest[i] : ' ';
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (depth == 0 && (char.IsWhiteSpace(c) || c is '>' or '+' or '~' || i == rest.Length))
            {
                if (i > start)
                    compounds.Add(rest[start..i]);
                if (c is '+' or '~')
                    sibling = true;
                start = i + 1;
            }
        }

        if (compounds.Count == 0)
            yield break;

        for (var i = 0; i < compounds.Count - 1; i++)
        {
            if (!AncestorOk(compounds[i]))
                yield break;
            specificity += CompoundSpecificity(compounds[i]);
        }

        var last = compounds[^1];
        specificity += CompoundSpecificity(last);
        foreach (var role in CompoundRoles(last, sibling))
            yield return new Match(role, specificity);
    }

    private static int CompoundSpecificity(string compound)
    {
        var score = 0;
        foreach (var c in compound)
        {
            if (c is '.' or '[') score += 10;
            else if (c == '#') score += 100;
        }

        score += Regex.Matches(compound, @"(?<!:):(?!:)(?!not\()(?!is\()").Count * 10;
        if (compound.Length > 0 && char.IsLetter(compound[0]))
            score += 1;
        return score;
    }

    private static bool HasBlockedState(string compound)
    {
        // Attribute filters outside :not() describe states (selected tab, duplicate note, blurred…).
        var withoutNot = Regex.Replace(compound, @":not\((?:[^()]|\([^()]*\))*\)", string.Empty);
        if (withoutNot.Contains('['))
            return true;
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(withoutNot, @"(?<!:):([a-z-]+)"))
        {
            if (BlockedPseudo.Contains(m.Groups[1].Value))
                return true;
        }

        return false;
    }

    private static bool AncestorOk(string compound)
    {
        if (HasBlockedState(compound))
            return false;
        var classes = Classes(compound);
        if (classes.Count == 0)
            return Regex.IsMatch(compound, @"^(?:ruby|li|ol|ul|div|span|\*)$");
        return classes.Any(AncestorClasses.Contains);
    }

    private static List<string> Classes(string compound)
    {
        var inner = compound;
        // :is(.a, .b) offers alternatives; collect all of them.
        inner = Regex.Replace(inner, @":(?:is|matches)\(", " ");
        inner = Regex.Replace(inner, @":not\((?:[^()]|\([^()]*\))*\)", string.Empty);
        return Regex.Matches(inner, @"\.([A-Za-z0-9_-]+)").Select(m => m.Groups[1].Value).ToList();
    }

    private static IEnumerable<Role> CompoundRoles(string compound, bool sibling)
    {
        if (HasBlockedState(compound))
            yield break;
        string? pseudoElement = null;
        var pe = Regex.Match(compound, @"::([a-z-]+)");
        if (pe.Success)
        {
            pseudoElement = pe.Groups[1].Value;
            compound = compound[..pe.Index];
        }

        var roles = new HashSet<Role>();
        foreach (var cls in Classes(compound))
        {
            if (ClassRoles.TryGetValue(cls, out var role))
                roles.Add(role);
        }

        var element = Regex.Match(compound, @"^[a-z]+").Value;
        if (element.Length > 0 && ElementRoles.TryGetValue(element, out var elementRole) && roles.Count == 0)
            roles.Add(elementRole);

        foreach (var role in roles)
        {
            if (pseudoElement is not null)
            {
                if (role == Role.Popup && pseudoElement == "before") yield return Role.PopupFrame;
                else if (role == Role.Popup && pseudoElement == "after") yield return Role.PopupOrnament;
                continue;
            }

            yield return role == Role.Entry && sibling ? Role.EntrySibling : role;
        }
    }

    // -------------------------------------------------------------------- build

    private static PopupTheme Build(Input input, Styles s, bool dark)
    {
        Vector4? Color(Role role, string property) => CssColor.Parse(s.Value(role, property), dark);
        Vector4? Var(string name) => CssColor.Parse(s.Variable(Role.Popup, name), dark);

        var basePx = FontSize(s.Value(Role.Popup, "font-size") ?? s.Value(Role.Popup, "font")) ?? 16f;
        var popup = Box(s, Role.Popup, dark, basePx);
        var text = popup.Color ?? Var("--hoshidicts-text") ?? (dark ? new Vector4(0.9f, 0.9f, 0.9f, 1) : new Vector4(0.07f, 0.07f, 0.07f, 1));
        popup.Color = text;
        var opacity = Math.Clamp(input.OpacityPercent / 100f, 0, 1);
        popup.Background ??= dark ? new Vector4(0.12f, 0.12f, 0.14f, opacity) : new Vector4(1, 1, 1, opacity);
        var background = popup.Background.Value with { W = 1 };
        Vector4 MixBg(Vector4 c, float amount) => CssColor.Mix(c, amount, background, 1 - amount);

        var frame = s.Props.ContainsKey(Role.PopupFrame) ? Box(s, Role.PopupFrame, dark, basePx) : null;
        // Themes without Hachidori's accent variable (Crystal) are keyed to their frame.
        var accent = Var("--hoshidicts-accent") ?? frame?.BorderColor ?? Color(Role.Link, "color") ?? new Vector4(0.6f, 0.55f, 0.9f, 1);
        var ornament = s.Value(Role.PopupOrnament, "background") ?? s.Value(Role.PopupOrnament, "background-image");
        var crest = ornament is not null && ornament.Contains("url(", StringComparison.Ordinal)
                    && (CssText.Length(s.Value(Role.PopupOrnament, "width")) ?? 0) >= 60;

        var entry = Box(s, Role.Entry, dark, basePx);
        var sibling = Box(s, Role.EntrySibling, dark, basePx);
        Vector4? separator = sibling.BorderTopColor ?? (entry.BorderTopWidth > 0 ? entry.BorderTopColor : null);
        var separatorWidth = sibling.BorderTopColor is not null ? sibling.BorderTopWidth : entry.BorderTopWidth;

        var section = s.Props.ContainsKey(Role.Section) ? Box(s, Role.Section, dark, basePx) : null;
        if (section is { HasPaint: false })
            section = null;

        var headwordBox = Box(s, Role.Headword, dark, basePx);
        var readingBox = Box(s, Role.Reading, dark, basePx);
        var headwordPx = headwordBox.FontSize ?? FontSize(s.Variable(Role.Popup, "--hd-font-headword"));

        CssBox ChipBox(Role role, Vector4 fallbackBg)
        {
            var box = Box(s, role, dark, basePx);
            var hasFill = box.Background is { W: > 0.05f };
            var hasBorder = box.BorderWidth > 0 && box.BorderColor is { W: > 0 };
            if (!hasFill && !hasBorder)
            {
                // Themes like JL and Nazeka colour the text only; keep a faint plate so chips stay legible.
                box.Background = box.Color is { } c ? MixBg(c, 0.14f) : fallbackBg;
            }

            box.Color ??= text;
            if (box.Radius <= 0 && !hasBorder)
                box.Radius = 3;
            return box;
        }

        var tag = ChipBox(Role.TagChip, MixBg(text, 0.16f));
        var dictionary = ChipBox(Role.DictionaryChip, MixBg(accent, 0.3f));
        var frequency = ChipBox(Role.FrequencyChip, MixBg(Var("--hoshidicts-frequency") ?? accent, 0.22f));
        var inflection = ChipBox(Role.InflectionChip, MixBg(text, 0.12f));
        var count = new CssBox
        {
            Background = MixBg(accent, 0.22f), Color = text, Radius = tag.Radius,
            BorderColor = accent, BorderWidth = 1,
        };

        var buttonBox = Box(s, Role.Button, dark, basePx);
        var buttonBg = buttonBox.Background ?? CssColor.Transparent;
        var buttonText = buttonBox.Color is { } bc && bc != Vector4.Zero ? bc : text;

        var pitch = CssColor.Parse(BorderColorOf(s.Value(Role.PitchMark, "border") ?? s.Value(Role.PitchMark, "border-color")), dark)
                    ?? accent;
        var scrollbar = s.Value(Role.Scroll, "scrollbar-color") is { } sc && CssText.SplitTopLevel(sc, ' ') is { Count: >= 1 } parts
            ? CssColor.Parse(parts[0], dark)
            : null;

        var gold = Var("--crystal-gold") ?? frame?.BorderColor ?? accent;
        return new PopupTheme
        {
            Renderer = input.Renderer,
            Palette = input.Palette,
            Variant = input.Variant,
            Dark = dark,
            BaseFontPx = basePx,
            Popup = popup,
            Frame = frame is { HasPaint: true } ? frame : null,
            Crest = crest,
            CrestGold = frame?.BorderColor ?? gold,
            Text = text,
            TextMuted = Var("--hoshidicts-text-muted") ?? Var("--crystal-muted") ?? MixBg(text, 0.62f),
            Accent = accent,
            Link = Color(Role.Link, "color") ?? Var("--crystal-link") ?? accent,
            Highlight = accent with { W = 0.35f },
            PitchLine = pitch,
            Headword = headwordBox.Color,
            Reading = readingBox.Color,
            HeadwordScale = Math.Clamp(headwordPx is { } hp ? hp / basePx : 1.7f, 1f, 3f),
            Entry = entry,
            Separator = separator is { W: > 0 } ? separator : null,
            SeparatorWidth = Math.Max(1, separatorWidth),
            Section = section,
            DictionaryChip = dictionary,
            TagChip = tag,
            FrequencyChip = frequency,
            InflectionChip = inflection,
            CountChip = count,
            Button = buttonBg,
            ButtonHovered = buttonBg.W > 0.05f ? CssColor.Mix(buttonBg, 0.8f, text, 0.2f) : accent with { W = 0.22f },
            ButtonText = buttonText,
            ButtonBorder = buttonBox.BorderWidth > 0 ? buttonBox.BorderColor : null,
            ButtonRadius = buttonBox.Radius > 0 ? buttonBox.Radius : 4,
            Scrollbar = scrollbar,
        };
    }

    private static float? FontSize(string? value)
    {
        if (value is null)
            return null;
        foreach (var token in CssText.SplitTopLevel(value, ' '))
        {
            var t = token.Split('/')[0];
            if (CssText.Length(t) is { } px && px > 0 && t.EndsWith("px"))
                return px;
        }

        return null;
    }

    private static string? BorderColorOf(string? border)
    {
        if (border is null)
            return null;
        foreach (var token in CssText.SplitTopLevel(border, ' '))
        {
            if (CssText.Length(token) is null && token is not ("solid" or "dotted" or "dashed" or "double" or "none"))
                return token;
        }

        return null;
    }

    private static CssBox Box(Styles s, Role role, bool dark, float basePx)
    {
        var box = new CssBox();
        string? V(string p) => s.Value(role, p);
        Vector4? C(string? v) => CssColor.Parse(v, dark);

        box.Color = C(V("color"));
        box.FontSize = FontSize(V("font-size") ?? V("font"));

        // Background layers: the last plain colour is the fill; a linear gradient becomes a vertical overlay.
        foreach (var source in new[] { V("background"), V("background-image") })
        {
            if (source is null)
                continue;
            foreach (var layer in CssText.SplitTopLevel(source, ','))
            {
                var l = layer.Trim();
                if (l.StartsWith("linear-gradient(", StringComparison.OrdinalIgnoreCase))
                {
                    var stops = CssText.SplitTopLevel(l[16..^1], ',')
                        .Select(stop => CssText.SplitTopLevel(stop, ' ').FirstOrDefault())
                        .Select(token => C(token))
                        .Where(c => c is not null)
                        .ToList();
                    if (stops.Count >= 2 && box.GradientTop is null)
                    {
                        box.GradientTop = stops[0];
                        box.GradientBottom = stops[^1];
                    }
                }
                else if (!l.Contains("gradient(") && !l.Contains("url("))
                {
                    foreach (var token in CssText.SplitTopLevel(l, ' '))
                    {
                        if (C(token) is { } c)
                            box.Background = c;
                    }

                    if (l == "none")
                        box.Background = CssColor.Transparent;
                }
            }
        }

        if (C(V("background-color")) is { } bgc)
            box.Background = bgc;

        ParseBorder(V("border"), out box.BorderWidth, out box.BorderColor, dark);
        if (V("border-width") is { } bw && CssText.Length(CssText.SplitTopLevel(bw, ' ').FirstOrDefault(), basePx) is { } w)
            box.BorderWidth = w;
        if (C(V("border-color")) is { } bcol)
            box.BorderColor = bcol;
        if (V("border-image") is not null || V("border-image-source") is not null)
        {
            // An SVG border image (Crystal's plaque): a hairline between the fill and the ink stands in for it.
            box.BorderWidth = 1;
            box.BorderColor = box.Background is { } fill && box.Color is { } ink ? CssColor.Mix(fill, 0.5f, ink, 0.5f) : box.Color;
        }

        if (box.BorderColor is null && box.BorderWidth > 0)
            box.BorderColor = box.Color;
        ParseBorder(V("border-top"), out var topWidth, out var topColor, dark);
        if (topColor is not null)
        {
            box.BorderTopWidth = topWidth;
            box.BorderTopColor = topColor;
        }

        if (V("border-radius") is { } radius && CssText.Length(CssText.SplitTopLevel(radius, ' ').FirstOrDefault(), basePx) is { } r)
            box.Radius = r;
        if (V("inset") is { } inset && CssText.Length(CssText.SplitTopLevel(inset, ' ').FirstOrDefault(), basePx) is { } i)
            box.Inset = i;

        if (V("padding") is { } padding)
        {
            var p = CssText.SplitTopLevel(padding, ' ').Select(t => CssText.Length(t, basePx) ?? 0).ToList();
            box.Padding = p.Count switch
            {
                1 => new Vector4(p[0], p[0], p[0], p[0]),
                2 => new Vector4(p[0], p[1], p[0], p[1]),
                3 => new Vector4(p[0], p[1], p[2], p[1]),
                >= 4 => new Vector4(p[0], p[1], p[2], p[3]),
                _ => default,
            };
        }

        if (V("box-shadow") is { } shadow && shadow != "none")
        {
            foreach (var layer in CssText.SplitTopLevel(shadow, ','))
            {
                var tokens = CssText.SplitTopLevel(layer, ' ');
                var insetShadow = tokens.Remove("inset");
                var lengths = new List<float>();
                Vector4? color = null;
                foreach (var t in tokens)
                {
                    if (CssText.Length(t, basePx) is { } len)
                        lengths.Add(len);
                    else
                        color ??= C(t);
                }

                if (color is null || lengths.Count < 2)
                    continue;
                box.Shadows.Add(new CssShadow(color.Value, lengths[0], lengths[1],
                    lengths.Count > 2 ? lengths[2] : 0, lengths.Count > 3 ? lengths[3] : 0, insetShadow));
            }
        }

        return box;
    }

    private static void ParseBorder(string? value, out float width, out Vector4? color, bool dark)
    {
        width = 0;
        color = null;
        if (value is null)
            return;
        if (value.Trim() is "none" or "0")
        {
            color = CssColor.Transparent;
            return;
        }

        var style = "solid";
        foreach (var token in CssText.SplitTopLevel(value, ' '))
        {
            if (CssText.Length(token) is { } w)
                width = w;
            else if (token is "solid" or "dotted" or "dashed" or "double" or "groove" or "ridge" or "inset" or "outset" or "none" or "hidden")
                style = token;
            else if (token is "thin")
                width = 1;
            else if (token is "medium")
                width = 3;
            else
                color = CssColor.Parse(token, dark) ?? color;
        }

        if (style is "none" or "hidden")
            width = 0;
        else if (width == 0 && color is not null)
            width = 3; // CSS's initial border-width is medium.
    }
}
