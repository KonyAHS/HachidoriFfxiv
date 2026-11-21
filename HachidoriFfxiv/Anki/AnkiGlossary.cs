using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HachidoriFfxiv.Anki;

/// <summary>Which definitions a glossary marker renders, and how (anki-values.js definition options).</summary>
public sealed record DefinitionOptions(string? Dictionary = null, bool FirstOnly = false, bool Brief = false, bool NoDictionary = false, bool Plain = false);

/// <summary>
/// Hachidori's Anki definition renderer (anki-glossary.js createAnkiDefinitionRenderer
/// over render/glossary.js appendTextOnlyGlossary in its "anki" layout), in
/// Hachidori 0.5.2, for a note mined without a profile language: one list item
/// per term-bank row with its tags and dictionary name, structured content as
/// HTML with Yomitan's class styles inlined, the dictionaries' own stylesheets
/// scoped to their items, and the rules and deinflection footer.
/// </summary>
public sealed partial class AnkiGlossary
{
    private const int MaxStructuredDataAttributes = 64;
    private const int MaxStructuredDataKeyLength = 64;
    private const int MaxStructuredDataValueLength = 4096;
    private const int MaxLookupTextBytes = 4 * 1024;

    private static readonly HashSet<string> AllowedTags =
    [
        "a", "br", "code", "details", "div", "em", "img", "li", "ol", "p", "rp", "rt", "ruby", "small", "span", "strong",
        "sub", "summary", "sup", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul",
    ];
    private static readonly HashSet<string> IgnoredTags = ["audio", "button", "canvas", "iframe", "input", "script", "source", "style", "svg", "video"];
    private static readonly HashSet<string> TagsWithoutContent = ["br", "img"];
    private static readonly HashSet<string> Blocks = ["br", "div", "li", "ol", "p", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul"];
    private static readonly (string Property, string Css)[] StyleProperties =
    [
        ("fontStyle", "font-style"), ("fontWeight", "font-weight"), ("fontSize", "font-size"), ("color", "color"),
        ("background", "background"), ("backgroundColor", "background-color"), ("verticalAlign", "vertical-align"),
        ("textAlign", "text-align"), ("textEmphasis", "text-emphasis"), ("textShadow", "text-shadow"),
        ("textDecorationLine", "text-decoration"), ("textDecorationStyle", "text-decoration-style"),
        ("textDecorationColor", "text-decoration-color"), ("borderColor", "border-color"), ("borderStyle", "border-style"),
        ("borderRadius", "border-radius"), ("borderWidth", "border-width"), ("clipPath", "clip-path"), ("margin", "margin"),
        ("marginTop", "margin-top"), ("marginLeft", "margin-left"), ("marginRight", "margin-right"),
        ("marginBottom", "margin-bottom"), ("padding", "padding"), ("paddingTop", "padding-top"),
        ("paddingLeft", "padding-left"), ("paddingRight", "padding-right"), ("paddingBottom", "padding-bottom"),
        ("wordBreak", "word-break"), ("whiteSpace", "white-space"), ("cursor", "cursor"), ("listStyleType", "list-style-type"),
    ];

    [GeneratedRegex(@"\b(?:url|src|image-set|paint|attr)\s*\(|(?<![A-Za-z0-9_\u0080-￿-])--[A-Za-z0-9_\u0080-￿-]+\(", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeStyleFunction();

    [GeneratedRegex(@"\bvar\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex VarFunction();

    private readonly JsonObject request;
    private readonly Func<string, string, string?>? filenameFor;
    private readonly IReadOnlyList<(string Dictionary, string Styles)> dictionaryStyles;
    private readonly bool compactGlossaries;
    private readonly List<(string Name, List<JsonObject> Glossaries)> groups = [];

    /// <param name="filenameFor">The Anki filename of a dictionary image (dictionary, path); null leaves images out.</param>
    public AnkiGlossary(JsonObject request, Func<string, string, string?>? filenameFor,
        IReadOnlyList<(string Dictionary, string Styles)> dictionaryStyles, bool compactGlossaries = false)
    {
        this.request = request;
        this.filenameFor = filenameFor;
        this.dictionaryStyles = dictionaryStyles;
        this.compactGlossaries = compactGlossaries;
        foreach (var glossary in (request["term"]?["glossaries"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = Str(glossary["dictionary"]);
            var group = groups.FirstOrDefault(g => g.Name == name);
            if (group.Glossaries is null) groups.Add((name, [glossary]));
            else group.Glossaries.Add(glossary);
        }
    }

    public string Render(DefinitionOptions options)
    {
        var selected = groups.Where(g => options.Dictionary is null || g.Name == options.Dictionary).ToList();
        if (options.FirstOnly) selected = selected.Take(1).ToList();
        if (selected.Count == 0) return string.Empty;
        if (options.Plain) return PlainDefinition(selected, options.NoDictionary);

        var root = new HtmlElement("div");
        root.ClassName = "yomitan-glossary";
        root.SetStyleText("text-align: left; contain: layout paint style; isolation: isolate;");
        var list = new HtmlElement("ol");
        var images = new List<(HtmlElement Image, string Dictionary, string Path)>();
        foreach (var (name, glossaries) in selected)
        {
            foreach (var glossary in glossaries)
            {
                var page = new HtmlElement("li");
                page.SetData("dictionary", name);
                page.Append(Entry(glossary, options.Brief, options.NoDictionary, images));
                list.Append(page);
            }
        }
        root.Append(list);
        var names = selected.Select(s => s.Name).ToHashSet();
        DictionaryStyles.Apply(root, (long)HachidoriFfxiv.Offline.JsonNumber.Or(request["generation"], 0),
            dictionaryStyles.Where(s => names.Contains(s.Dictionary)), DictionaryStyles.GlossaryScope);
        if (!options.Brief) AppendDetails(root);
        // The images' sources resolve after the rest of the render, so src is their last attribute.
        foreach (var (image, dictionary, path) in images)
            if (filenameFor?.Invoke(dictionary, path) is { } filename) image.SetAttribute("src", filename);
        StructuredContentStyle.Apply(list);
        return root.OuterHtml;
    }

    // ------------------------------------------------------------------ entry

    private HtmlElement Entry(JsonObject glossary, bool brief, bool noDictionary, List<(HtmlElement, string, string)>? images)
    {
        var wrapper = new HtmlElement("div");
        var tags = brief ? [] : DefinitionTagNames(glossary).Concat(ParseTagList(Str(glossary["termTags"]))).Where(t => t.Length > 0).ToList();
        var name = brief || noDictionary ? string.Empty : Alias(Str(glossary["dictionary"]));
        if (tags.Count > 0 || name.Length > 0)
        {
            var meta = new HtmlElement("i");
            meta.ClassName = "yomitan-glossary-meta";
            var parts = name.Length > 0 ? tags.Append(string.Empty) : tags;
            meta.Append($"({string.Join(", ", parts)}{name})");
            wrapper.Append(meta);
            wrapper.Append(" ");
        }
        wrapper.Append(ReplaceNewlines(Content(glossary, images)));
        return wrapper;
    }

    private HtmlElement Content(JsonObject glossary, List<(HtmlElement, string, string)>? images)
    {
        var body = new HtmlElement("div");
        body.ClassName = "gsm-hoshidicts-glossary-content";
        var dictionary = Str(glossary["dictionary"]);
        body.SetData("hoshidictsDictionary", dictionary);
        AppendTextOnlyGlossary(body, Str(glossary["glossary"]), dictionary, images);
        return body;
    }

    private static HtmlElement ReplaceNewlines(HtmlElement root)
    {
        foreach (var text in root.DescendantTexts().ToList())
        {
            var lines = Regex.Split(text.Value, "\r?\n|\r");
            if (lines.Length < 2) continue;
            var replacement = new List<HtmlNode>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) replacement.Add(new HtmlElement("br"));
                replacement.Add(new HtmlText(lines[i]));
            }
            text.Parent!.ReplaceChild(text, replacement);
        }
        return root;
    }

    private void AppendDetails(HtmlElement root)
    {
        var details = new List<string>();
        var term = request["term"] as JsonObject;
        var rules = Str(term?["rules"]);
        if (rules.Length > 0) details.Add($"Rules: {HtmlElement.EscapeText(rules)}");
        var trace = (request["trace"] as JsonArray ?? []).Select(step => HtmlElement.EscapeText(Str(step?["name"]))).ToList();
        if (trace.Count > 0) details.Add($"Deinflection: {string.Join(" &gt; ", trace)}");
        if (details.Count == 0) return;
        var small = new HtmlElement("small");
        small.ClassName = "yomitan-glossary-details";
        // innerHTML of already escaped text and <br>: it serializes back unchanged.
        small.Append(new HtmlRaw(string.Join("<br>", details)));
        root.Append(small);
    }

    private string PlainDefinition(List<(string Name, List<JsonObject> Glossaries)> selected, bool noDictionary)
    {
        var lines = new List<string>();
        foreach (var (dictionary, glossaries) in selected)
        {
            if (!noDictionary) lines.Add($"({HtmlElement.EscapeText(Alias(dictionary))})");
            foreach (var glossary in glossaries)
            {
                lines.AddRange(Regex.Split(PlainText(Content(glossary, null)), "\r?\n|\r")
                    .Select(line => line.Trim()).Where(line => line.Length > 0).Select(HtmlElement.EscapeText));
            }
        }
        return string.Join("<br>", lines);
    }

    private static string PlainText(HtmlNode node)
    {
        var output = new StringBuilder();
        var stack = new Stack<object>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is string newline) output.Append(newline);
            else if (current is HtmlText text) output.Append(text.Value);
            else if (current is HtmlElement element && element.GetAttribute("aria-hidden") != "true")
            {
                if (Blocks.Contains(element.Tag)) stack.Push("\n");
                for (var i = element.Children.Count - 1; i >= 0; i--) stack.Push(element.Children[i]);
            }
        }
        return output.ToString();
    }

    // -------------------------------------------------- structured content

    private void AppendTextOnlyGlossary(HtmlElement parent, string value, string dictionary, List<(HtmlElement, string, string)>? images)
    {
        if (value.Length == 0) return;
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(value); }
        catch (JsonException) { parsed = JsonValue.Create(value); }
        var items = parsed is JsonArray array ? array.ToList() : [parsed];
        if (items.Count == 0) return;
        if (items.Any(item => item is JsonObject o && Str(o["type"]) == "structured-content")) parent.AddClass("structured-content");
        if (items.Count == 1 || compactGlossaries)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) parent.Append(" | ");
                AppendStructuredValue(parent, items[i], null, dictionary, images);
            }
            return;
        }
        var list = new HtmlElement("ul");
        list.ClassName = "gloss-list";
        foreach (var item in items)
        {
            var listItem = new HtmlElement("li");
            listItem.ClassName = "gloss-item";
            AppendStructuredValue(listItem, item, null, dictionary, images);
            list.Append(listItem);
        }
        parent.Append(list);
    }

    // appendStructuredValue: `language` is the nearest dictionary-set lang, below which text is not detected again.
    private void AppendStructuredValue(HtmlElement parent, JsonNode? value, string? language, string dictionary,
        List<(HtmlElement, string, string)>? images)
    {
        switch (value)
        {
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
            {
                var text = v.GetValue<string>();
                parent.Append(text);
                if (language is null && LanguageFromText(text) is { } detected) parent.SetAttribute("lang", detected);
                return;
            }
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                parent.Append(JsNumber.Format(HachidoriFfxiv.Offline.JsonNumber.Or(v, 0)));
                return;
            case JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                parent.Append(v.GetValueKind() == JsonValueKind.True ? "true" : "false");
                return;
            case JsonArray items:
                foreach (var item in items) AppendStructuredValue(parent, item, language, dictionary, images);
                return;
            case not JsonObject:
                return;
        }

        var record = (JsonObject)value;
        var type = Str(record["type"]);
        if (type == "structured-content")
        {
            AppendStructuredValue(parent, record["content"], language, dictionary, images);
            return;
        }
        if (type == "text")
        {
            AppendStructuredValue(parent, record.ContainsKey("text") ? record["text"] : record["content"], language, dictionary, images);
            return;
        }
        var tag = type == "image" ? "img" : Str(record["tag"]).ToLowerInvariant();
        if (IgnoredTags.Contains(tag)) return;
        if (!AllowedTags.Contains(tag))
        {
            if (record.ContainsKey("content")) AppendStructuredValue(parent, record["content"], language, dictionary, images);
            return;
        }
        if (tag == "img")
        {
            if (NormalizeMediaPath(record["path"]) is { } path && images is not null) AppendImage(parent, record, dictionary, path, images);
            return;
        }

        var element = new HtmlElement(tag);
        element.AddClass($"gloss-sc-{tag}");
        ApplyStructuredStyle(element, record["style"] as JsonObject);
        ApplyStructuredData(element, record["data"] as JsonObject);
        var childLanguage = language;
        if (record["lang"] is JsonValue langValue && langValue.GetValueKind() == JsonValueKind.String
            && Regex.IsMatch(langValue.GetValue<string>(), "^[A-Za-z0-9-]{1,35}$"))
        {
            element.SetAttribute("lang", langValue.GetValue<string>());
            childLanguage = langValue.GetValue<string>();
        }
        if (tag is "td" or "th")
        {
            foreach (var (property, attribute) in new[] { ("colSpan", "colspan"), ("rowSpan", "rowspan") })
            {
                var span = JsNumberOf(record[property]);
                if (span is >= 1 and <= 32 && Math.Floor(span) == span) element.SetAttribute(attribute, JsNumber.Format(span));
            }
        }
        if (record["title"] is JsonValue titleValue && titleValue.GetValueKind() == JsonValueKind.String && titleValue.GetValue<string>().Length <= 4096)
            element.SetAttribute("title", titleValue.GetValue<string>());
        if (tag == "details" && record["open"] is JsonValue open && open.GetValueKind() == JsonValueKind.True)
            element.SetAttribute("open", string.Empty);
        var external = false;
        if (tag == "a")
        {
            element.AddClass("gloss-link", "gsm-hoshidicts-structured-link");
            var link = ParseStructuredLink(record["href"]);
            if (link is { Internal: true })
            {
                element.SetAttribute("href", "#");
                element.SetData("external", "false");
                element.SetData("hoshidictsQuery", link.Query);
                if (link.PrimaryReading.Length > 0) element.SetData("hoshidictsReading", link.PrimaryReading);
            }
            else if (link is not null)
            {
                element.SetAttribute("href", link.Href);
                element.SetAttribute("target", "_blank");
                element.SetAttribute("rel", "noopener noreferrer");
                element.SetData("external", "true");
                external = true;
            }
        }
        var contentParent = element;
        if (tag == "a")
        {
            contentParent = new HtmlElement("span");
            contentParent.ClassName = "gloss-link-text";
            element.Append(contentParent);
        }
        if (tag == "table")
        {
            var container = new HtmlElement("div");
            container.ClassName = "gloss-sc-table-container";
            container.Append(element);
            parent.Append(container);
        }
        else
        {
            parent.Append(element);
        }
        if (!TagsWithoutContent.Contains(tag) && record.ContainsKey("content"))
            AppendStructuredValue(contentParent, record["content"], childLanguage, dictionary, images);
        if (external)
        {
            var icon = new HtmlElement("span");
            icon.ClassName = "gloss-link-external-icon icon";
            icon.SetData("icon", "external-link");
            icon.SetAttribute("aria-hidden", "true");
            element.Append(icon);
        }
    }

    private static void AppendImage(HtmlElement parent, JsonObject value, string dictionary, string path, List<(HtmlElement, string, string)> images)
    {
        var image = new HtmlElement("img");
        image.ClassName = "gloss-sc-img";
        images.Add((image, dictionary, path));
        image.SetAttribute("alt", value["title"] is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : "Dictionary image");
        image.SetStyle("max-width", "100%");
        image.SetStyle("object-fit", "contain");
        ImageSize(image, value);
        parent.Append(image);
    }

    private static void ImageSize(HtmlElement image, JsonObject value)
    {
        var units = Str(value["sizeUnits"]) == "em" ? "em" : "px";
        static bool Positive(double? size) => size is { } s && double.IsFinite(s) && s > 0;
        double? Read(string key) => HachidoriFfxiv.Offline.JsonNumber.TryGet(value[key], out var d) ? d : null;
        var preferred = (Width: Read("preferredWidth"), Height: Read("preferredHeight"));
        var styled = Positive(preferred.Width) || Positive(preferred.Height) ? preferred
            : units == "em" ? (Read("width"), Read("height")) : ((double?)null, (double?)null);
        foreach (var (dimension, own, style) in new[] { ("width", Read("width"), styled.Item1), ("height", Read("height"), styled.Item2) })
        {
            if (units == "px" && Positive(own)) image.SetAttribute(dimension, JsNumber.Format(own!.Value));
            if (Positive(style)) image.SetStyle(dimension, JsNumber.Format(style!.Value) + units);
        }
        var width = image.Style.Get("width");
        var height = image.Style.Get("height");
        if (width is not null && height is null) image.SetStyle("height", "auto");
        else if (height is not null && width is null) image.SetStyle("width", "auto");
    }

    private static string? NormalizeMediaPath(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String) return null;
        var value = v.GetValue<string>();
        if (value.Length is < 1 or > 4096 || value.Any(c => c == '\\' || c < 0x20 || c == 0x7f) || value.StartsWith('/')
            || Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9+.-]*:")) return null;
        return value.Split('/').Any(c => c.Length == 0 || c is "." or "..") ? null : value;
    }

    private static void ApplyStructuredStyle(HtmlElement element, JsonObject? style)
    {
        if (style is null) return;
        foreach (var (property, css) in StyleProperties)
        {
            var raw = style[property];
            string? value = null;
            if (raw is JsonValue n && n.GetValueKind() == JsonValueKind.Number && property.StartsWith("margin", StringComparison.Ordinal) && property.Length > 6)
                value = JsNumber.Format(HachidoriFfxiv.Offline.JsonNumber.Or(n, 0)) + "em";
            else if (raw is JsonArray array && property == "textDecorationLine")
                value = string.Join(' ', array.Select(Str));
            else if (raw is JsonValue s && s.GetValueKind() == JsonValueKind.String)
                value = s.GetValue<string>();
            if (value is null || value.Contains('\\') || UnsafeStyleFunction().IsMatch(value) || VarFunction().IsMatch(value)) continue;
            element.SetStyle(css, value);
        }
    }

    private static void ApplyStructuredData(HtmlElement element, JsonObject? data)
    {
        if (data is null) return;
        var count = 0;
        foreach (var (key, raw) in data)
        {
            if (count >= MaxStructuredDataAttributes) break;
            string value;
            if (raw is JsonValue v && v.GetValueKind() == JsonValueKind.String) value = v.GetValue<string>();
            else if (raw is JsonValue n && n.GetValueKind() == JsonValueKind.Number) value = JsNumber.Format(HachidoriFfxiv.Offline.JsonNumber.Or(n, 0));
            else if (raw is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False) value = b.GetValueKind() == JsonValueKind.True ? "true" : "false";
            else continue;
            var attribute = DataAttributeName(key);
            if (attribute is null || value.Length > MaxStructuredDataValueLength || value.Any(c => c < 0x20 || c == 0x7f)) continue;
            if (attribute.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '>' or '/' or '=' or '<')) continue;
            element.SetAttribute(attribute, value);
            count++;
        }
    }

    private static string? DataAttributeName(string key)
    {
        if (key.Length is 0 or > MaxStructuredDataKeyLength) return null;
        var property = "sc" + key[..1].ToUpperInvariant() + key[1..];
        if (Regex.IsMatch(property, "-[a-z]")) return null;
        return "data-" + Regex.Replace(property, "[A-Z]", m => "-" + m.Value.ToLowerInvariant());
    }

    private sealed record StructuredLink(bool Internal, string Href, string Query, string PrimaryReading);

    private static StructuredLink? ParseStructuredLink(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String) return null;
        var href = v.GetValue<string>();
        if (href.Length is 0 or > 4096) return null;
        if (href.StartsWith('?'))
        {
            var parameters = new Dictionary<string, string>();
            try
            {
                foreach (var part in href[1..].Split('&'))
                {
                    var separator = part.IndexOf('=');
                    var key = Uri.UnescapeDataString((separator < 0 ? part : part[..separator]).Replace('+', ' '));
                    var value = Uri.UnescapeDataString((separator < 0 ? string.Empty : part[(separator + 1)..]).Replace('+', ' '));
                    parameters.TryAdd(key, value);
                }
            }
            catch (UriFormatException)
            {
                return null;
            }
            static string Bounded(string? s) => s is null ? string.Empty : s.Length > MaxLookupTextBytes ? s[..MaxLookupTextBytes] : s;
            var query = Bounded(parameters.GetValueOrDefault("query")).Trim();
            return query.Length == 0 ? null : new StructuredLink(true, "#", query, Bounded(parameters.GetValueOrDefault("primary_reading")).Trim());
        }
        // HDExternalLinks.normaliseExternalUrl: an absolute http(s) URL without credentials.
        if (href.Any(c => c < 0x20 || c == 0x7f)) return null;
        var source = href.Trim();
        if (!Regex.IsMatch(source, "^https?://", RegexOptions.IgnoreCase)) return null;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0) return null;
        return new StructuredLink(false, uri.AbsoluteUri, string.Empty, string.Empty);
    }

    // ------------------------------------------------------------- helpers

    private static readonly (int Low, int High)[] JapaneseRanges =
    [
        (0x3000, 0x30ff), (0x3400, 0x4dbf), (0x4e00, 0x9fff), (0xf900, 0xfaff), (0xff01, 0xff1f), (0xff21, 0xff3f),
        (0xff41, 0xff9f), (0xffe0, 0xffee), (0x20000, 0x2a6df), (0x2a700, 0x2ee5f), (0x2f800, 0x2fa1f), (0x30000, 0x323af),
    ];
    private static readonly (int Low, int High)[] ChineseRanges = [(0x3100, 0x312f), (0x31a0, 0x31bf), (0xfe10, 0xfe1f), (0xfe50, 0xfe6f), (0x16fe0, 0x16fff)];

    /// <summary>render/glossary.js languageFromText: "ja" for Japanese text, "zh" for Chinese-only scripts.</summary>
    public static string? LanguageFromText(string text)
    {
        static bool Any(string t, (int Low, int High)[] ranges) => t.EnumerateRunes().Any(r => ranges.Any(range => r.Value >= range.Low && r.Value <= range.High));
        return Any(text, JapaneseRanges) ? "ja" : Any(text, ChineseRanges) ? "zh" : null;
    }

    private IEnumerable<string> DefinitionTagNames(JsonObject glossary) => glossary["tags"] is JsonArray tags
        ? tags.Select(t => Str(t?["name"]))
        : ParseTagList(Str(glossary["definitionTags"]));

    public static IEnumerable<string> ParseTagList(string value) => value.Split(' ').Where(s => s.Length > 0);

    private string Alias(string dictionary) => request["dictionaryAliases"] is JsonObject aliases && aliases.ContainsKey(dictionary)
        ? Str(aliases[dictionary]) : dictionary;

    private static string Str(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : string.Empty;

    // JavaScript's Number(value) for the values a span attribute can hold.
    private static double JsNumberOf(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => HachidoriFfxiv.Offline.JsonNumber.Or(v, double.NaN),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => 1,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => 0,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Trim() is var s && s.Length == 0 ? 0
            : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
        null => double.NaN,
        _ => double.NaN,
    };
}
