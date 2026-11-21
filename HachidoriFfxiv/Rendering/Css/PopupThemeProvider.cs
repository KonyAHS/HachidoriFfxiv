using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.Rendering.Css;

/// <summary>
/// Keeps <see cref="PopupTheme.Current"/> in step with the theme, colour variant, opacity and
/// custom popup CSS selected in Hachidori (or the overrides in the plugin's settings).
/// </summary>
public sealed class PopupThemeProvider
{
    public static readonly string[] Renderers = ["default", "nazeka", "plain", "jl", "bee", "crystal"];
    public static readonly string[] Variants = ["auto", "grey", "beige", "blue"];

    private readonly Configuration config;
    private readonly HachidoriService service;
    private readonly Dictionary<string, string> sheets = new();
    private string? key;

    public PopupThemeProvider(Configuration config, HachidoriService service)
    {
        this.config = config;
        this.service = service;
    }

    public string? Error { get; private set; }

    /// <summary>The Default renderer's palettes (popupTheme values), read from its stylesheet.</summary>
    public IReadOnlyList<string> Palettes => palettes ??= System.Text.RegularExpressions.Regex
        .Matches(Sheet("default"), @":host\(\[data-hoshidicts-theme=""([a-z0-9-]+)""\]\)")
        .Select(m => m.Groups[1].Value).Distinct().ToList();

    private IReadOnlyList<string>? palettes;

    /// <summary>Recomputes the theme when its inputs changed. Cheap otherwise; call once per frame.</summary>
    public void Update()
    {
        if (!config.FollowHachidoriTheme)
        {
            if (key != "plugin")
            {
                key = "plugin";
                PopupTheme.Current = PopupTheme.PluginDefault();
                Error = null;
            }

            return;
        }

        var theme = config.HachidoriThemeOverride.Length > 0 ? config.HachidoriThemeOverride : service.OptionString("popupTheme", "default");
        var variant = config.HachidoriVariantOverride.Length > 0 ? config.HachidoriVariantOverride : service.OptionString("popupThemeVariant", "auto");
        var renderer = Renderers.Contains(theme) ? theme : "default";
        // Automatic follows the system's light or dark mode; a game is dark.
        if (renderer == "crystal" && variant == "auto")
            variant = "grey";
        var opacity = (float)Math.Clamp(config.FollowHachidoriOpacity ? service.OptionNumber("popupOpacityPercent", 85) : config.PopupBackgroundAlpha * 100, 0, 100);
        var custom = config.ApplyHachidoriCustomCss ? CustomCss() : string.Empty;
        custom += "\n" + config.ExtraPopupCss;

        var next = $"{renderer}\u0001{theme}\u0001{variant}\u0001{opacity}\u0001{custom}";
        if (next == key)
            return;
        key = next;
        try
        {
            PopupTheme.Current = PopupThemeEngine.Compute(new PopupThemeEngine.Input(renderer, theme, variant, Sheet(renderer), custom, opacity));
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Plugin.Log.Warning(ex, "Could not apply the Hachidori popup theme");
            PopupTheme.Current = PopupTheme.PluginDefault();
        }
    }

    /// <summary>Hachidori's own order: each enabled user script's CSS, then the custom popup CSS.</summary>
    private string CustomCss()
    {
        var sb = new StringBuilder();
        if (service.Options["userScripts"] is JsonArray scripts)
        {
            foreach (var script in scripts.OfType<JsonObject>())
            {
                if (script["enabled"] is JsonValue e && e.TryGetValue<bool>(out var enabled) && enabled
                    && script["css"] is JsonValue css && css.TryGetValue<string>(out var text))
                    sb.AppendLine(text);
            }
        }

        sb.AppendLine(service.OptionString("customPopupCss", string.Empty));
        return sb.ToString();
    }

    private string Sheet(string renderer)
    {
        if (sheets.TryGetValue(renderer, out var css))
            return css;
        using var stream = typeof(PopupThemeProvider).Assembly.GetManifestResourceStream($"HachidoriFfxiv.Themes.{renderer}.css");
        css = stream is null ? string.Empty : new StreamReader(stream, Encoding.UTF8).ReadToEnd();
        sheets[renderer] = css;
        return css;
    }
}
