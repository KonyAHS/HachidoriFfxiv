using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using HachidoriFfxiv.Capture;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Media;
using HachidoriFfxiv.Publishing;
using HachidoriFfxiv.Rendering.Css;
using HachidoriFfxiv.WebView;

namespace HachidoriFfxiv.Ui;

public sealed partial class ConfigWindow
{
    // -------------------------------------------------------------- individual controls (shared by tabs & search!)

    private void DrawScanModifierControl()
    {
        var modifier = (int)config.ScanModifier;
        if (ImGui.Combo("Scan key", ref modifier, Enum.GetNames<ScanModifier>(), Enum.GetValues<ScanModifier>().Length))
        {
            config.ScanModifier = (ScanModifier)modifier;
            Changed(true);
        }
        HelpMarker("None scans on hover, like JL's default. Alt over a headword kanji (or clicking it) opens kanji info.");
    }

    private static readonly FreezeHotkey[] FreezeHotkeys = Enum.GetValues<FreezeHotkey>();
    private static readonly string[] FreezeHotkeyLabels = FreezeHotkeys.Select(FreezeFrameOverlay.HotkeyLabel).ToArray();

    private void DrawFreezeHotkeyControl()
    {
        var idx = Math.Max(0, Array.IndexOf(FreezeHotkeys, config.FreezeFrameHotkey));
        if (ImGui.Combo("Freeze frame (tooltips)", ref idx, FreezeHotkeyLabels, FreezeHotkeyLabels.Length))
        {
            config.FreezeFrameHotkey = FreezeHotkeys[idx];
            config.Save();
        }
        HelpMarker("Freezes the screen so you can move onto item and action tooltips to look words up. Also /hachidori freeze.");
        if (triggerFreezeFrame is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Freeze now"))
                triggerFreezeFrame();
        }
    }

    private void DrawScanLengthControl() =>
        NumberInt("Scan length", () => config.ScanLength, v => { config.ScanLength = v; Changed(true); }, 4, 32, Defaults.ScanLength, "Maximum characters sent per lookup scan.");

    private void DrawMaxResultsControl() =>
        NumberInt("Max results", () => config.MaxResults, v => { config.MaxResults = v; Changed(true); }, 1, 32, Defaults.MaxResults, "Maximum dictionary entries shown in a popup.");

    private void DrawMaxPopupDepthControl() =>
        NumberInt("Nested popups", () => config.MaxPopupDepth, v => { config.MaxPopupDepth = v; Changed(true); }, 1, 8, Defaults.MaxPopupDepth, "How many nested popups deep you can look up definitions inside definitions.");

    private void DrawPopupWidthControl() =>
        NumberFloat("Popup width", () => config.PopupWidth, v => { config.PopupWidth = v; Changed(true); }, 280, 900, Defaults.PopupWidth, "%.0f", "px");

    private void DrawPopupMaxHeightControl() =>
        NumberFloat("Popup max height", () => config.PopupMaxHeight, v => { config.PopupMaxHeight = v; Changed(true); }, 160, 1000, Defaults.PopupMaxHeight, "%.0f", "px");

    private void DrawPopupFontSizeControl() =>
        NumberFloat("Popup font size", () => config.PopupFontSize, v => config.PopupFontSize = v, 12, 36, Defaults.PopupFontSize, "%.0f", "px",
            deferSave: () => Changed(true));

    private void DrawPopupOpacityControl() =>
        NumberFloat("Popup opacity", () => config.PopupBackgroundAlpha, v => { config.PopupBackgroundAlpha = v; Changed(true); }, 0.3f, 1f, Defaults.PopupBackgroundAlpha, "%.2f");

    private void DrawHideDelayControl() =>
        NumberFloat("Hide delay (s)", () => config.HideDelaySeconds, v => { config.HideDelaySeconds = v; Changed(true); }, 0f, 2f, Defaults.HideDelaySeconds, "%.2f", "s");

    private void DrawAudioVolumeControl() =>
        NumberFloat("Volume", () => config.AudioVolume, v => { config.AudioVolume = v; Changed(true); }, 0f, 1f, Defaults.AudioVolume, "%.2f");

    private void DrawUseWebViewControl()
    {
        var enabled = config.UseWebViewPopup;
        if (ImGui.Checkbox("Render popups with Hachidori Web (WebView2)", ref enabled))
        {
            config.UseWebViewPopup = enabled;
            config.Save();
            webView?.NotifySettingsChanged();
        }
        HelpMarker("Shows Hachidori's own popup in game, so themes and CSS look exactly like the browser. Falls back to the built-in renderer when WebView2 is missing.");
    }

    private void DrawAutoSyncWebViewControl()
    {
        var autoSync = config.AutoSyncWebViewBundle;
        if (ImGui.Checkbox("Auto-sync Hachidori Web when Hachidori updates", ref autoSync))
        {
            config.AutoSyncWebViewBundle = autoSync;
            config.Save();
            webView?.NotifySettingsChanged();
        }
        HelpMarker("Matches your Hachidori version from the browser extension or GitHub Releases, and rolls back if an update fails to load.");
    }

    private void DrawWebViewBundlePathControl()
    {
        var customPath = config.WebViewBundlePath;
        if (ImGui.InputTextWithHint("Custom Hachidori extension folder", "blank: auto-detect Chrome/Edge or download", ref customPath, 512))
            config.WebViewBundlePath = customPath.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save();
            if (webView is not null)
                _ = webView.SyncBundleNowAsync(force: true);
        }
        HelpMarker("An unpacked Hachidori extension folder (with manifest.json), for testing local builds.");
    }

    private void DrawFollowThemeControl()
    {
        var follow = config.FollowHachidoriTheme;
        if (ImGui.Checkbox("Use Hachidori's popup theme", ref follow))
        {
            config.FollowHachidoriTheme = follow;
            config.Save();
            webView?.NotifySettingsChanged();
        }
        HelpMarker("Uses the theme chosen in Hachidori → Settings → Design.");
    }

    private void DrawThemeOverrideControl()
    {
        var choices = new List<string> { string.Empty };
        var labels = new List<string> { "Same as Hachidori" };

        var bundleThemes = webView?.Bundles.Themes;
        var bundlePalettes = webView?.Bundles.DefaultPalettes;
        if (bundleThemes is { Count: > 0 })
        {
            foreach (var t in bundleThemes)
            {
                if (string.Equals(t.Slug, "default", StringComparison.OrdinalIgnoreCase)) continue;
                choices.Add(t.Slug);
                labels.Add(string.IsNullOrWhiteSpace(t.Name) ? $"{t.Slug} (theme)" : $"{t.Name} ({t.Slug})");
            }
            var palettes = bundlePalettes is { Count: > 0 } ? bundlePalettes : themes.Palettes;
            foreach (var p in palettes)
            {
                if (choices.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                choices.Add(p);
                labels.Add($"{p} (Default palette)");
            }
        }
        else
        {
            foreach (var r in PopupThemeProvider.Renderers.Where(r => r != "default"))
            {
                choices.Add(r);
                labels.Add($"{r} (theme)");
            }
            foreach (var p in themes.Palettes)
            {
                choices.Add(p);
                labels.Add($"{p} (Default palette)");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.HachidoriThemeOverride)
            && !choices.Contains(config.HachidoriThemeOverride, StringComparer.Ordinal))
        {
            choices.Add(config.HachidoriThemeOverride);
            labels.Add($"{config.HachidoriThemeOverride} (custom)");
        }

        var index = Math.Max(0, choices.IndexOf(config.HachidoriThemeOverride));
        var labelArray = labels.ToArray();
        if (ImGui.Combo("Theme", ref index, labelArray, labelArray.Length))
        {
            config.HachidoriThemeOverride = choices[index];
            config.Save();
            webView?.NotifySettingsChanged();
        }
    }

    private void DrawVariantOverrideControl()
    {
        var effectiveSlug = string.IsNullOrWhiteSpace(config.HachidoriThemeOverride)
            ? service.OptionString("popupTheme", "default")
            : config.HachidoriThemeOverride;
        var matchedTheme = webView?.Bundles.Themes.FirstOrDefault(t =>
            string.Equals(t.Slug, effectiveSlug, StringComparison.OrdinalIgnoreCase));

        var variants = new List<string> { string.Empty };
        var variantLabels = new List<string> { "Same as Hachidori" };

        if (matchedTheme is { Variants.Count: > 0 })
        {
            variants.Add("auto");
            variantLabels.Add("Automatic");
            foreach (var (id, name) in matchedTheme.Variants)
            {
                if (string.Equals(id, "auto", StringComparison.OrdinalIgnoreCase)) continue;
                variants.Add(id);
                variantLabels.Add(string.IsNullOrWhiteSpace(name) ? id : name);
            }
        }
        else
        {
            foreach (var v in PopupThemeProvider.Variants)
            {
                variants.Add(v);
                variantLabels.Add(v == "auto" ? "Automatic (Grey in game)" : v);
            }
        }

        if (!string.IsNullOrWhiteSpace(config.HachidoriVariantOverride)
            && !variants.Contains(config.HachidoriVariantOverride, StringComparer.Ordinal))
        {
            variants.Add(config.HachidoriVariantOverride);
            variantLabels.Add(config.HachidoriVariantOverride);
        }

        var variantIndex = Math.Max(0, variants.IndexOf(config.HachidoriVariantOverride));
        var labelArray = variantLabels.ToArray();
        if (ImGui.Combo("Colours", ref variantIndex, labelArray, labelArray.Length))
        {
            config.HachidoriVariantOverride = variants[variantIndex];
            config.Save();
            webView?.NotifySettingsChanged();
        }
        HelpMarker("Colour schemes offered by the theme, such as Crystal's Grey, Beige and Retro blue.");
    }

    private void DrawFollowOpacityControl()
    {
        var opacity = config.FollowHachidoriOpacity;
        if (ImGui.Checkbox("Use Hachidori's popup opacity", ref opacity))
        {
            config.FollowHachidoriOpacity = opacity;
            config.Save();
            webView?.NotifySettingsChanged();
        }
    }

    private void DrawApplyCustomCssControl()
    {
        var custom = config.ApplyHachidoriCustomCss;
        if (ImGui.Checkbox("Apply Hachidori's custom popup CSS", ref custom))
        {
            config.ApplyHachidoriCustomCss = custom;
            config.Save();
            webView?.NotifySettingsChanged();
        }
        HelpMarker("Hachidori's Custom popup CSS and the CSS of enabled user scripts.");
    }

    private void DrawExtraCssControl()
    {
        ImGui.TextUnformatted("Extra CSS for the game popup");
        HelpMarker("Applied after Hachidori's CSS. Full CSS is supported when rendering with Hachidori Web (WebView2).");
        var extra = config.ExtraPopupCss;
        if (ImGui.InputTextMultiline("##extra-css", ref extra, 16384, new Vector2(-1, 120 * ImGuiHelpers.GlobalScale)))
            config.ExtraPopupCss = extra;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.Save();
            webView?.NotifySettingsChanged();
        }
    }

    private void DrawAnkiNotifyToggleControl()
    {
        Toggle("Show notification when a card is created", config.NotifyOnAnkiMine, v => config.NotifyOnAnkiMine = v);
        HelpMarker("Shows a small Dalamud toast notification whenever a card is added to Anki.");
    }

    private void DrawScreenshotToggleControl()
    {
        Toggle("Screenshot", config.CaptureScreenshot, v => config.CaptureScreenshot = v);
        HelpMarker("Fills {screenshot} in your Hachidori Anki Template. The picture has no plugin windows in it.");
    }

    private void DrawScreenshotMaxWidthControl() =>
        NumberInt("Screenshot max width", () => config.ScreenshotMaxWidth, v => config.ScreenshotMaxWidth = v, 640, 3840, Defaults.ScreenshotMaxWidth, deferSave: config.Save);

    private void DrawScreenshotQualityControl() =>
        NumberFloat("Screenshot JPEG quality", () => config.ScreenshotQuality, v => config.ScreenshotQuality = v, 0.5f, 1f, Defaults.ScreenshotQuality, "%.2f", deferSave: config.Save);

    private void DrawGifToggleControl()
    {
        Toggle("Animation of the line", config.CaptureGif, v => config.CaptureGif = v);
        HelpMarker("A few seconds of the scene from when the line appeared. Without a GIF field it replaces the screenshot in your picture field.");
    }

    private void DrawAnimationFormatControl()
    {
        var formats = new[] { "AVIF (smallest, best quality)", "WebP", "GIF (plays everywhere, large)" };
        var format = (int)config.AnimationFormat;
        if (ImGui.Combo("Format", ref format, formats, formats.Length))
        {
            config.AnimationFormat = (AnimationFormat)format;
            config.Save();
        }
    }

    private void DrawGifWidthControl() =>
        NumberInt("Animation width", () => config.GifWidth, v => config.GifWidth = v, 320, 1280, Defaults.GifWidth, deferSave: config.Save);

    private void DrawGifFpsControl() =>
        NumberInt("Animation FPS", () => config.GifFps, v => config.GifFps = v, 4, 24, Defaults.GifFps, deferSave: config.Save);

    private void DrawGifSecondsControl() =>
        NumberInt("Animation length (s)", () => config.GifSeconds, v => config.GifSeconds = v, 1, 8, Defaults.GifSeconds, deferSave: config.Save);

    private void DrawAvifCrfControl() =>
        NumberInt("AVIF quality (CRF, lower is better)", () => config.AvifCrf, v => config.AvifCrf = v, 18, 50, Defaults.AvifCrf, deferSave: config.Save);

    private void DrawWebpQualityControl() =>
        NumberInt("WebP quality", () => config.WebpQuality, v => config.WebpQuality = v, 40, 95, Defaults.WebpQuality, deferSave: config.Save);

    private void DrawFfmpegPathControl()
    {
        var ffmpegPath = config.FfmpegPath;
        if (ImGui.InputTextWithHint("FFmpeg path", "blank: find automatically", ref ffmpegPath, 512))
            config.FfmpegPath = ffmpegPath.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
    }

    private void DrawVoiceRecordToggleControl()
    {
        Toggle("Voice line from the game", config.RecordVoiceLines, v => config.RecordVoiceLines = v);
        HelpMarker("Records only FFXIV's own sound and cuts out the line you mined. Lower the BGM volume in game for cleaner clips.");
    }

    private void DrawVoiceRatioControl() =>
        NumberFloat("Voice detection", () => config.VoiceDetectionRatio, v => config.VoiceDetectionRatio = v, 1.2f, 6f, Defaults.VoiceDetectionRatio, "%.1f", "×",
            help: "How much louder than the music before it a line must be to count as voiced. Raise it if unvoiced lines get music clips instead of TTS; lower it if quiet voices are missed.",
            deferSave: config.Save);

    private void DrawMaxLineSecondsControl() =>
        NumberInt("Longest voice line (s)", () => config.MaxLineSeconds, v => config.MaxLineSeconds = v, 5, 60, Defaults.MaxLineSeconds, deferSave: config.Save);

    private void DrawVoiceBufferSecondsControl() =>
        NumberInt("Voice ring buffer (s)", () => config.VoiceBufferSeconds, v => config.VoiceBufferSeconds = v, 30, 300, Defaults.VoiceBufferSeconds, "How many seconds of game audio are kept in memory.", deferSave: config.Save);

    private void DrawTtsModeControl()
    {
        var modes = new[] { "Off", "VOICEVOX-compatible (VOICEVOX, AivisSpeech…)", "Custom URL" };
        var mode = (int)config.TtsMode;
        if (ImGui.Combo("TTS for unvoiced lines", ref mode, modes, modes.Length))
        {
            config.TtsMode = (TtsMode)mode;
            if (config.TtsMode == TtsMode.Voicevox && !config.TtsUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                config.TtsUrl = "http://127.0.0.1:50021";
            config.Save();
        }
    }

    private void DrawTtsUrlControl()
    {
        var url = config.TtsUrl;
        if (ImGui.InputText(config.TtsMode == TtsMode.Voicevox ? "Engine address" : "TTS URL", ref url, 512))
            config.TtsUrl = url.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
        if (config.TtsMode == TtsMode.Voicevox)
            HelpMarker("VOICEVOX: http://127.0.0.1:50021 · AivisSpeech: http://127.0.0.1:10101");
        else
            HelpMarker("{text} becomes the sentence and {speaker} the speaker below. The URL must return audio (WAV, MP3 or Ogg).");
    }

    private void DrawTtsSpeakerControl()
    {
        var speaker = config.TtsSpeaker;
        if (ImGui.InputText("Speaker / voice", ref speaker, 128))
            config.TtsSpeaker = speaker;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
    }

    private void DrawTtsInjectOstControl()
    {
        Toggle("Mix current playing OST into TTS", config.TtsInjectOst, v => config.TtsInjectOst = v);
        HelpMarker("Mixes the track that is playing, taken clean from the game files, under the TTS voice.");
    }

    private void DrawTtsOstVolumeControl() =>
        NumberFloat("OST background level", () => config.TtsOstVolume, v => config.TtsOstVolume = v, 0.05f, 1.0f, Defaults.TtsOstVolume, "%.2f",
            help: "Target loudness of the OST relative to the TTS voice (0.28 = subtle background music, 0.50 = louder).", deferSave: config.Save);

    private void DrawTtsOstRespectMuteControl()
    {
        Toggle("Skip OST when game BGM is muted", config.TtsOstRespectMute, v => config.TtsOstRespectMute = v);
        HelpMarker("When on, leaves TTS dry if FFXIV's BGM slider is muted or at zero.");
    }

    private void DrawCurrentOstStatus()
    {
        var snap = media.Bgm.CurrentAt(System.Diagnostics.Stopwatch.GetTimestamp());
        if (snap is null)
        {
            ImGui.TextDisabled("Current OST: none / silent");
            return;
        }

        var mins = (int)(snap.ElapsedSeconds / 60);
        var secs = (int)(snap.ElapsedSeconds % 60);
        var clock = snap.FromEngineClock ? "engine timing" : "tracked timing";
        if (snap.Muted && config.TtsOstRespectMute)
            ImGui.TextColored(AccentAmber, $"Current OST: {snap.DisplayName} ({mins}:{secs:00}, BGM muted in game — skipped)");
        else
            ImGui.TextDisabled($"Current OST: {snap.DisplayName} ({mins}:{secs:00} · {clock})");
    }

    private void DrawTranslationProviderControl()
    {
        var provider = (int)config.TranslationProvider;
        if (ImGui.Combo("Provider", ref provider, ProviderNames, ProviderNames.Length))
        {
            config.TranslationProvider = (TranslationProvider)provider;
            config.Save();
        }
    }

    private void DrawTranslationFieldControl()
    {
        var field = config.TranslationField;
        if (ImGui.InputText("Translation Anki field", ref field, 128))
            config.TranslationField = field.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
    }

    private void DrawTranslationLanguageControl()
    {
        var language = config.TranslationLanguage;
        if (ImGui.InputText("Translate into", ref language, 64))
            config.TranslationLanguage = language.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
    }

    private void DrawDialogueContextControl() =>
        NumberInt("Dialogue context lines", () => config.DialogueContextLength, v => config.DialogueContextLength = v, -1, 20, Defaults.DialogueContextLength,
            "Lines before and after the mined one. 0: none, -1: all captured lines.", deferSave: config.Save);

    private void DrawTemperatureControl() =>
        NumberFloat("Temperature", () => (float)config.TranslationTemperature, v => config.TranslationTemperature = Math.Round(v, 2), 0f, 2f, (float)Defaults.TranslationTemperature, "%.2f", deferSave: config.Save);

    private void DrawTopPControl() =>
        NumberFloat("Top P", () => (float)config.TranslationTopP, v => config.TranslationTopP = Math.Round(v, 2), 0f, 1f, (float)Defaults.TranslationTopP, "%.2f", deferSave: config.Save);

    private void DrawMaxTokensControl() =>
        NumberInt("Max output tokens", () => config.TranslationMaxTokens, v => config.TranslationMaxTokens = v, 64, 65536, Defaults.TranslationMaxTokens, deferSave: config.Save);

    private void DrawSentenceAudioFieldControl()
    {
        var audioField = config.SentenceAudioField;
        if (ImGui.InputTextWithHint("Sentence audio field", "e.g. SentenceAudio", ref audioField, 128))
            config.SentenceAudioField = audioField.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
    }

    private void DrawGifFieldControl()
    {
        var gifField = config.GifField;
        if (ImGui.InputTextWithHint("GIF field", "e.g. Picture", ref gifField, 128))
            config.GifField = gifField.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
    }

    private void DrawAnkiConnectUrlControl()
    {
        var anki = config.AnkiConnectUrl;
        if (ImGui.InputText("AnkiConnect address", ref anki, 256))
            config.AnkiConnectUrl = anki.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
    }

    private void DrawAnkiConnectApiKeyControl()
    {
        var key = config.AnkiConnectApiKey;
        if (ImGui.InputTextWithHint("AnkiConnect API key", "blank: use Hachidori's API key", ref key, 256, ImGuiInputTextFlags.Password))
            config.AnkiConnectApiKey = key.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
    }

    private void DrawControllerHoldButtonControl()
    {
        var ps = config.ControllerPlayStationLabels;
        string Name(GamepadButtons b) => b switch
        {
            GamepadButtons.None => "Off (macro only)",
            _ => string.Join(" + ", Enum.GetValues<GamepadButtons>()
                .Where(v => v != GamepadButtons.None && b.HasFlag(v))
                .Select(v => v switch
                {
                    GamepadButtons.Select => ps ? "Select / Touchpad" : "Back / View",
                    GamepadButtons.Start => ps ? "Options" : "Start / Menu",
                    GamepadButtons.L1 => ps ? "L1" : "LB",
                    GamepadButtons.R1 => ps ? "R1" : "RB",
                    GamepadButtons.L3 => ps ? "L3" : "LS click",
                    GamepadButtons.R3 => ps ? "R3" : "RS click",
                    GamepadButtons.DpadDown => "D-pad down",
                    _ => v.ToString(),
                })),
        };
        var index = Math.Max(0, Array.IndexOf(HoldChoices, config.ControllerHoldButton));
        var labels = HoldChoices.Select(Name).ToArray();
        if (ImGui.Combo("Controller trigger", ref index, labels, labels.Length))
        {
            config.ControllerHoldButton = HoldChoices[index];
            config.Save();
        }
        HelpMarker("Button or combo that enters and exits controller mode. FFXIV still sees that press when entering, so pick a button whose game action you don't mind, or use the macro or a keybind.");
    }

    private void DrawControllerTriggerModeControl()
    {
        var modes = new[] { "Hold to toggle", "Press to toggle" };
        var mode = (int)config.ControllerTriggerMode;
        if (ImGui.Combo("Trigger style", ref mode, modes, modes.Length))
        {
            config.ControllerTriggerMode = (ControllerTriggerMode)mode;
            config.Save();
        }
        HelpMarker("Hold avoids conflicting with normal game actions; Press toggles immediately (useful for unused or extra controller buttons). Using the same trigger again exits controller mode.");
    }

    private void DrawControllerHoldSecondsControl() =>
        NumberFloat("Hold for (s)", () => config.ControllerHoldSeconds, v => config.ControllerHoldSeconds = v, 0.2f, 1.5f, Defaults.ControllerHoldSeconds, "%.1f", "s", deferSave: config.Save);

    private void DrawControllerToggleKeyControl()
    {
        if (recordingControllerKey)
            PollControllerKeyRecorder();

        var scale = ImGuiHelpers.GlobalScale;
        var label = recordingControllerKey
            ? "Press any key… (Esc cancels)"
            : config.ControllerToggleKey > 0
                ? KeybindFormat.Format(config.ControllerToggleKey, config.ControllerToggleKeyCtrl, config.ControllerToggleKeyShift, config.ControllerToggleKeyAlt)
                : "None (click to bind)";

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Toggle keybind");
        ImGui.SameLine(0, 8 * scale);
        using (var col = recordingControllerKey ? ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.68f, 0.45f, 0.14f, 0.95f)) : null)
        {
            if (ImGui.Button($"{label}##ctrl-toggle-key", new Vector2(210 * scale, 0)))
            {
                recordingControllerKey = !recordingControllerKey;
                if (gamepad is not null)
                    gamepad.SuppressToggleKey = recordingControllerKey;
                if (recordingControllerKey)
                    SnapshotRecorderKeys();
            }
        }
        if (config.ControllerToggleKey > 0 && !recordingControllerKey)
        {
            ImGui.SameLine(0, 6 * scale);
            if (ImGui.SmallButton("Clear##ctrl-toggle-key-clear"))
            {
                config.ControllerToggleKey = 0;
                config.ControllerToggleKeyCtrl = false;
                config.ControllerToggleKeyShift = false;
                config.ControllerToggleKeyAlt = false;
                config.Save();
            }
        }
        HelpMarker("Optional single-press keyboard or extra-button keybind to toggle controller mode on/off. Great for controller back paddles or Steam Deck grip buttons mapped to a key like F13–F24 or Mouse 4/5.");
    }

    private void SnapshotRecorderKeys()
    {
        for (var vk = 0x04; vk < 0xFF; vk++)
            recorderPrevKeys[vk] = GamepadNavigator.IsOsKeyDown(vk);
    }

    private void PollControllerKeyRecorder()
    {
        for (var vk = 0x04; vk < 0xFF; vk++)
        {
            var down = GamepadNavigator.IsOsKeyDown(vk);
            var newlyPressed = down && !recorderPrevKeys[vk];
            recorderPrevKeys[vk] = down;
            if (!newlyPressed)
                continue;
            // Skip modifier-only keys; they are captured as chord flags with the primary key.
            if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5)
                continue;

            recordingControllerKey = false;
            if (gamepad is not null)
                gamepad.SuppressToggleKey = false;

            if (vk == 0x1B) // Escape cancels
                return;

            config.ControllerToggleKey = vk;
            config.ControllerToggleKeyCtrl = GamepadNavigator.IsOsKeyDown(0x11);
            config.ControllerToggleKeyShift = GamepadNavigator.IsOsKeyDown(0x10);
            config.ControllerToggleKeyAlt = GamepadNavigator.IsOsKeyDown(0x12);
            config.Save();
            gamepad?.NotifyToggleKeyBound();
            return;
        }
    }

    private void DrawControllerCursorSpeedControl() =>
        NumberFloat("Left stick cursor speed", () => config.ControllerCursorSpeed, v => config.ControllerCursorSpeed = v, 300f, 2000f, Defaults.ControllerCursorSpeed, "%.0f", "px/s", deferSave: config.Save);

    private void DrawControllerScanButtonControl()
    {
        var ps = config.ControllerPlayStationLabels;
        var labels = BindChoices.Select(b => GamepadNavigator.ButtonLabel(b, ps)).ToArray();
        var idx = Math.Max(0, Array.IndexOf(BindChoices, config.ControllerScanButton));
        if (ImGui.Combo("Look up button", ref idx, labels, labels.Length))
        {
            config.ControllerScanButton = BindChoices[idx];
            config.Save();
        }
        HelpMarker("Button to open the popup for the word under the cursor when auto-scan is off. Can share a button with Mine (opens the popup first, then mines once open).");
        if (!config.ControllerAutoScan && config.ControllerScanButton == GamepadButtons.None)
            ImGui.TextColored(AccentAmber, "Pick a button so you can open popups.");
    }

    private void DrawReaderAnchorControl()
    {
        var anchor = (int)config.ReaderAnchor;
        if (ImGui.Combo("Position", ref anchor, ["Free", "Above the dialogue box", "Below the dialogue box"], 3))
        {
            config.ReaderAnchor = (ReaderAnchor)anchor;
            Changed(true);
        }
    }

    private void DrawReaderFontSizeControl() =>
        NumberFloat("Reader font size", () => config.ReaderFontSize, v => config.ReaderFontSize = v, 14, 48, Defaults.ReaderFontSize, "%.0f", "px",
            deferSave: () => Changed(true));

    private void DrawReaderOpacityControl() =>
        NumberFloat("Reader opacity", () => config.ReaderBackgroundAlpha, v => { config.ReaderBackgroundAlpha = v; Changed(true); }, 0f, 1f, Defaults.ReaderBackgroundAlpha, "%.2f");

    private void DrawHistorySizeControl() =>
        NumberInt("History size", () => config.HistorySize, v => { config.HistorySize = v; Changed(true); }, 10, 500, Defaults.HistorySize);

    private void DrawPlayerNameKatakanaControl()
    {
        var kana = config.PlayerNameKatakana;
        if (ImGui.InputTextWithHint("Katakana name", PlayerName.Suggested(), ref kana, 64))
        {
            config.PlayerNameKatakana = kana.Trim();
            Changed(true);
        }
    }

    private void DrawPublishPortControl() =>
        PortInput("GSM WebSocket port", () => config.PublishPort, v => { config.PublishPort = v; Changed(true); }, Defaults.PublishPort);

    private void DrawRelayHostControl()
    {
        var host = config.RelayHost;
        if (ImGui.InputText("Relay host", ref host, 128))
        {
            config.RelayHost = host.Trim();
            Changed(true);
        }
    }

    private void DrawRelayPortControl() =>
        PortInput("Relay port", () => config.RelayPort, v => { config.RelayPort = v; Changed(true); }, Defaults.RelayPort);

    private void DrawApiPortControl() =>
        PortInput("Yomitan API port (audio)", () => config.ApiPort, v => { config.ApiPort = v; Changed(true); }, Defaults.ApiPort);

    private void DrawAnkiTemplateIdControl()
    {
        var template = config.AnkiTemplateId;
        if (ImGui.InputText("Anki Template ID (blank: first)", ref template, 256))
        {
            config.AnkiTemplateId = template.Trim();
            Changed(true);
        }
    }

    private void DrawDocumentTitleControl()
    {
        var title = config.DocumentTitle;
        if (ImGui.InputText("{document-title}", ref title, 256))
        {
            config.DocumentTitle = title;
            Changed(true);
        }
    }
}
