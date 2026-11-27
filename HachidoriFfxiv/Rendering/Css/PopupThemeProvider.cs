using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
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

    // Cheap inputs, compared every frame without allocating. The custom CSS is only rebuilt
    // when one of them changes: the sharing snapshot is replaced whenever Hachidori's options change.
    private object? inputSnapshot;
    private bool inputFollow, inputApplyCustom, inputFollowOpacity;
    private string? inputThemeOverride, inputVariantOverride, inputExtraCss;
    private float inputAlpha;
    private bool inputsKnown;

    private string? key;
    private string? computedKey;
    private Task<PopupTheme>? computing;
    private string? computingKey;

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
        ApplyFinished();
        if (!config.FollowHachidoriTheme)
        {
            inputsKnown = false;
            if (key != "plugin")
            {
                key = computedKey = "plugin";
                PopupTheme.Current = PopupTheme.PluginDefault();
                Error = null;
            }

            return;
        }

        if (!InputsChanged())
            return;

        var theme = config.HachidoriThemeOverride.Length > 0 ? config.HachidoriThemeOverride : service.OptionString("popupTheme", "default");
        var variant = config.HachidoriVariantOverride.Length > 0 ? config.HachidoriVariantOverride : service.OptionString("popupThemeVariant", "auto");
        var renderer = Renderers.Contains(theme) ? theme : "default";
        // Automatic follows the system's light or dark mode; a game is dark.
        if (renderer == "crystal" && variant == "auto")
            variant = "grey";
        var opacity = (float)Math.Clamp(config.FollowHachidoriOpacity ? service.OptionNumber("popupOpacityPercent", 85) : config.PopupBackgroundAlpha * 100, 0, 100);
        var custom = config.ApplyHachidoriCustomCss ? CustomCss() : string.Empty;
        custom += "\n" + config.ExtraPopupCss;

        key = $"{renderer}\u0001{theme}\u0001{variant}\u0001{opacity}\u0001{custom}";
        StartCompute(new PopupThemeEngine.Input(renderer, theme, variant, Sheet(renderer), custom, opacity));
    }

    private bool InputsChanged()
    {
        var snapshot = service.Link.Snapshot;
        if (inputsKnown
            && ReferenceEquals(snapshot, inputSnapshot)
            && inputFollow == config.FollowHachidoriTheme
            && inputApplyCustom == config.ApplyHachidoriCustomCss
            && inputFollowOpacity == config.FollowHachidoriOpacity
            && inputAlpha == config.PopupBackgroundAlpha
            && string.Equals(inputThemeOverride, config.HachidoriThemeOverride, StringComparison.Ordinal)
            && string.Equals(inputVariantOverride, config.HachidoriVariantOverride, StringComparison.Ordinal)
            && string.Equals(inputExtraCss, config.ExtraPopupCss, StringComparison.Ordinal))
            return false;
        inputSnapshot = snapshot;
        inputFollow = config.FollowHachidoriTheme;
        inputApplyCustom = config.ApplyHachidoriCustomCss;
        inputFollowOpacity = config.FollowHachidoriOpacity;
        inputAlpha = config.PopupBackgroundAlpha;
        inputThemeOverride = config.HachidoriThemeOverride;
        inputVariantOverride = config.HachidoriVariantOverride;
        inputExtraCss = config.ExtraPopupCss;
        inputsKnown = true;
        return true;
    }

    /// <summary>
    /// The cascade over a large sheet takes tens of milliseconds the first time, so it runs off the
    /// render thread; the previous theme stays on screen until the new one is ready.
    /// </summary>
    private void StartCompute(PopupThemeEngine.Input input)
    {
        if (key == computedKey || key == computingKey)
            return;
        if (computing is { IsCompleted: false })
            return; // ApplyFinished starts the latest inputs when this one is done.
        computingKey = key;
        computing = Task.Run(() => PopupThemeEngine.Compute(input));
    }

    private void ApplyFinished()
    {
        if (computing is not { IsCompleted: true } done)
            return;

        computing = null;
        var finishedKey = computingKey;
        computingKey = null;
        if (!config.FollowHachidoriTheme)
            return;
        if (done.IsCompletedSuccessfully)
        {
            PopupTheme.Current = done.Result;
            Error = null;
        }
        else
        {
            var ex = done.Exception?.GetBaseException();
            Error = ex?.Message;
            if (ex is not null) DiagnosticLog.Fault("theme", ex, "Could not apply the Hachidori popup theme");
            PopupTheme.Current = PopupTheme.PluginDefault();
        }

        computedKey = finishedKey;
        // Inputs changed while this theme was being computed: compute the latest ones next frame.
        if (key != computedKey)
            inputsKnown = false;
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
        lock (sheets)
        {
            if (sheets.TryGetValue(renderer, out var css))
                return css;
            // Embedded minified and deflated by the PackThemeSheets build step.
            using var stream = typeof(PopupThemeProvider).Assembly.GetManifestResourceStream($"HachidoriFfxiv.Themes.{renderer}.css.deflate");
            if (stream is null)
            {
                css = string.Empty;
            }
            else
            {
                using var inflate = new DeflateStream(stream, CompressionMode.Decompress);
                using var reader = new StreamReader(inflate, Encoding.UTF8);
                css = reader.ReadToEnd();
            }

            sheets[renderer] = css;
            return css;
        }
    }
}
