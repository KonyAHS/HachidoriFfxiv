using System;
using System.Numerics;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.Rendering;

public sealed record DictionaryImage(string Dictionary, string Path, string Label, float Width, float Height, bool Em)
{
    public static DictionaryImage? Parse(JsonObject image, string dictionary)
    {
        var path = LookupResult.Str(image["path"]);
        if (path.Length == 0 || path.Length > 4096 || path.Contains('\\') || path.Contains(':') || path.StartsWith('/')
            || Array.Exists(path.Split('/'), p => p is "" or "." or "..")) return null;
        static float Dimension(JsonNode? value) => float.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && float.IsFinite(n) && n > 0 ? n : 0;
        var label = LookupResult.Str(image["alt"]);
        if (label.Length == 0) label = LookupResult.Str(image["title"]);
        if (label.Length == 0) label = LookupResult.Str(image["description"]);
        return new(dictionary, path, label, Dimension(image["preferredWidth"] ?? image["width"]),
            Dimension(image["preferredHeight"] ?? image["height"]), image["sizeUnits"]?.ToString() == "em");
    }

    public Vector2 Size(float availableWidth, float fontSize, int naturalWidth, int naturalHeight)
    {
        var aspect = naturalWidth / (float)Math.Max(1, naturalHeight);
        var unit = Em ? fontSize : 1f;
        var w = Width > 0 ? Width * unit : Height > 0 ? Height * unit * aspect : naturalWidth;
        var h = Height > 0 ? Height * unit : Width > 0 ? Width * unit / aspect : naturalHeight;
        var scale = Math.Min(1, Math.Min(Math.Max(1, availableWidth) / Math.Max(1, w), 320 * fontSize / 18 / Math.Max(1, h)));
        return new Vector2(Math.Max(1, w * scale), Math.Max(1, h * scale));
    }
}
