using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Capture;
using HachidoriFfxiv.Publishing;
using HachidoriFfxiv.Media;
using HachidoriFfxiv.Rendering.Css;

namespace HachidoriFfxiv.Ui;

public sealed class ConfigWindow : Window
{
    private static readonly XivChatType[] SelectableChannels =
    [
        XivChatType.NPCDialogue, XivChatType.NPCDialogueAnnouncements, XivChatType.Say, XivChatType.Shout,
        XivChatType.Yell, XivChatType.Party, XivChatType.Alliance, XivChatType.FreeCompany, XivChatType.TellIncoming,
        XivChatType.Ls1, XivChatType.CrossLinkShell1, XivChatType.Echo, XivChatType.SystemMessage,
    ];

    private static readonly (string Name, bool Talk, bool Battle, bool Popups, bool Chat)[] PublishPresets =
    [
        ("Dialogue only", true, true, false, false),
        ("Dialogue and popups (recommended)", true, true, true, false),
        ("Everything, including chat", true, true, true, true),
    ];

    private static readonly (string Name, int Port)[] PublishPorts =
    [
        ("Textractor", 6677),
        ("LunaTranslator", 2333),
        ("Agent", 9001),
    ];

    private readonly Configuration config;
    private readonly HachidoriService service;
    private readonly Fonts fonts;
    private readonly NativeTextHover nativeHover;
    private readonly TextPublisher publisher;
    private readonly PopupThemeProvider themes;
    private readonly MediaMiner media;
    private string? mediaTest;
    private readonly Func<string> exportLogs;
    private string? exportedLog;

    public ConfigWindow(Configuration config, HachidoriService service, Fonts fonts, NativeTextHover nativeHover, TextPublisher publisher, PopupThemeProvider themes, MediaMiner media, Func<string> exportLogs)
        : base("Hachidori Settings##hachidori-ffxiv-config")
    {
        this.config = config;
        this.service = service;
        this.fonts = fonts;
        this.nativeHover = nativeHover;
        this.publisher = publisher;
        this.themes = themes;
        this.media = media;
        this.exportLogs = exportLogs;
        Size = new Vector2(560, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        DrawStatus();
        var advanced = config.AdvancedSettings;
        if (ImGui.Checkbox("Advanced settings", ref advanced))
        {
            config.AdvancedSettings = advanced;
            config.Save();
        }

        HelpMarker("Shows every option. Without it, only the settings most people change are listed; hidden settings keep their values.");
        ImGui.Separator();
        using var tabs = ImRaii.TabBar("##tabs");
        if (!tabs.Success)
            return;
        using (var tab = ImRaii.TabItem("Popup"))
        {
            if (tab.Success)
                DrawPopupTab();
        }

        using (var tab = ImRaii.TabItem("Theme"))
        {
            if (tab.Success)
                DrawThemeTab();
        }

        using (var tab = ImRaii.TabItem("Mining"))
        {
            if (tab.Success)
                DrawMiningTab();
        }

        using (var tab = ImRaii.TabItem("Reader"))
        {
            if (tab.Success)
                DrawReaderTab();
        }

        using (var tab = ImRaii.TabItem("Capture"))
        {
            if (tab.Success)
                DrawCaptureTab();
        }

        // The game's own capture replaces GSM; its text feed stays for those who still use it.
        if (Advanced || config.PublishEnabled)
        {
            using var tab = ImRaii.TabItem("GSM");
            if (tab.Success)
                DrawPublishTab();
        }

        using (var tab = ImRaii.TabItem("Connection"))
        {
            if (tab.Success)
                DrawConnectionTab();
        }
    }

    private void DrawStatus()
    {
        if (Advanced && ImGui.Button("Export debug log"))
        {
            try { exportedLog = exportLogs(); }
            catch (Exception ex) { exportedLog = $"Export failed: {ex.Message}"; }
        }
        if (Advanced && exportedLog is not null)
        {
            ImGui.TextWrapped(exportedLog);
            if (ImGui.Button("Copy log path")) ImGui.SetClipboardText(exportedLog);
        }
        var link = service.Link;
        if (link.Connected)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), "●");
            ImGui.SameLine();
            ImGui.TextUnformatted($"Linked to Hachidori in {link.HostName} (v{link.HostVersion}), {link.DictionaryCount} dictionaries.");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "●");
            ImGui.SameLine();
            ImGui.TextWrapped(link.Error ?? "Connecting to the Hachidori Relay…");
            ImGui.TextWrapped("Needs: Anki open with the Hachidori Relay add-on, and Chrome with Hachidori (Settings → Sharing on).");
        }
    }

    private bool Advanced => config.AdvancedSettings;

    private void Changed(bool changed)
    {
        if (!changed)
            return;
        config.Save();
        fonts.Refresh();
        service.ClearCache();
    }

    private void DrawPopupTab()
    {
        Checkbox("Hover native game text", () => config.NativeTextHover, v => config.NativeTextHover = v);
        Checkbox("Include native chat text", () => config.NativeChatHover, v => config.NativeChatHover = v);
        if (Advanced)
        {
            Checkbox("Hover whole words (Hachidori tokenizer)", () => config.TokenizedHover, v => config.TokenizedHover = v);
            Checkbox("Other native windows (experimental)", () => config.NativeOtherTextHover, v => config.NativeOtherTextHover = v);
            Checkbox("Show native hover targets (debug)", () => config.NativeHoverDebug, v => config.NativeHoverDebug = v);
            if (config.NativeHoverDebug)
            {
                ImGui.TextDisabled($"Native text regions under mouse: {nativeHover.RegionCount}");
                if (nativeHover.LastError is { } error) ImGui.TextWrapped(error);
            }
        }

        ImGui.Separator();
        var modifier = (int)config.ScanModifier;
        if (ImGui.Combo("Scan key", ref modifier, Enum.GetNames<ScanModifier>(), Enum.GetValues<ScanModifier>().Length))
        {
            config.ScanModifier = (ScanModifier)modifier;
            Changed(true);
        }

        HelpMarker("None scans on hover, like JL's default. Alt over a headword kanji (or clicking it) opens kanji info.");

        if (Advanced)
        {
            var scanLength = config.ScanLength;
            if (ImGui.SliderInt("Scan length", ref scanLength, 4, 32))
            {
                config.ScanLength = scanLength;
                Changed(true);
            }

            var maxResults = config.MaxResults;
            if (ImGui.SliderInt("Max results", ref maxResults, 1, 32))
            {
                config.MaxResults = maxResults;
                Changed(true);
            }

            var depth = config.MaxPopupDepth;
            if (ImGui.SliderInt("Nested popups", ref depth, 1, 8))
            {
                config.MaxPopupDepth = depth;
                Changed(true);
            }
        }

        var width = config.PopupWidth;
        if (ImGui.SliderFloat("Popup width", ref width, 280, 900, "%.0f"))
        {
            config.PopupWidth = width;
            Changed(true);
        }

        var height = config.PopupMaxHeight;
        if (ImGui.SliderFloat("Popup max height", ref height, 160, 1000, "%.0f"))
        {
            config.PopupMaxHeight = height;
            Changed(true);
        }

        var fontSize = config.PopupFontSize;
        if (ImGui.SliderFloat("Popup font size", ref fontSize, 12, 36, "%.0f"))
            config.PopupFontSize = fontSize;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Changed(true);

        // With Hachidori's theme and opacity, this slider would do nothing.
        if (!config.FollowHachidoriTheme || !config.FollowHachidoriOpacity)
        {
            var alpha = config.PopupBackgroundAlpha;
            if (ImGui.SliderFloat("Popup opacity", ref alpha, 0.3f, 1f))
            {
                config.PopupBackgroundAlpha = alpha;
                Changed(true);
            }
        }

        if (Advanced)
        {
            var hide = config.HideDelaySeconds;
            if (ImGui.SliderFloat("Hide delay (s)", ref hide, 0f, 2f, "%.2f"))
            {
                config.HideDelaySeconds = hide;
                Changed(true);
            }

            Checkbox("Hide the popup when the mouse leaves", () => config.AutoHideRootPopup, v => config.AutoHideRootPopup = v);
            Checkbox("Only start scans on Japanese characters", () => config.ScanOnlyJapanese, v => config.ScanOnlyJapanese = v);
            Checkbox("Use the personal dictionary", () => config.UsePersonalDictionary, v => config.UsePersonalDictionary = v);
        }

        ImGui.Separator();
        Checkbox("Show frequencies", () => config.ShowFrequencies, v => config.ShowFrequencies = v);
        Checkbox("Show pitch accent", () => config.ShowPitchAccent, v => config.ShowPitchAccent = v);
        Checkbox("Show dictionary names", () => config.ShowDictionaryNames, v => config.ShowDictionaryNames = v);
        if (Advanced)
        {
            Checkbox("Show deinflection", () => config.ShowInflections, v => config.ShowInflections = v);
            Checkbox("Show definition tags", () => config.ShowDefinitionTags, v => config.ShowDefinitionTags = v);
            Checkbox("Show attribution", () => config.ShowAttribution, v => config.ShowAttribution = v);
            Checkbox("Show lookup counts (Hachidori lookup statistics)", () => config.ShowLookupCounts, v => config.ShowLookupCounts = v);
            Checkbox("Honour Hachidori's lookup blur", () => config.HonourDefinitionBlur, v => config.HonourDefinitionBlur = v);
        }

        Checkbox("Auto-play pronunciation", () => config.AutoPlayAudio, v => config.AutoPlayAudio = v);
        var volume = config.AudioVolume;
        if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f))
        {
            config.AudioVolume = volume;
            Changed(true);
        }
    }

    private void DrawThemeTab()
    {
        void Save() => config.Save();
        var follow = config.FollowHachidoriTheme;
        if (ImGui.Checkbox("Use Hachidori's popup theme", ref follow))
        {
            config.FollowHachidoriTheme = follow;
            Save();
        }

        HelpMarker("Draws popups with the theme chosen in Hachidori (Settings → Design): Default and its colour palettes, Nazeka, Plain, JL, Bee's Theme or Crystal, including your custom popup CSS.");
        if (!config.FollowHachidoriTheme)
        {
            ImGui.TextDisabled("Popups use the plugin's own look.");
            return;
        }

        using var indent = ImRaii.PushIndent();
        var hostTheme = service.OptionString("popupTheme", "default");
        var hostVariant = service.OptionString("popupThemeVariant", "auto");
        var current = PopupTheme.Current;
        ImGui.TextDisabled(service.Link.Connected
            ? $"Hachidori uses \"{hostTheme}\"{(hostVariant != "auto" ? $" ({hostVariant})" : string.Empty)}."
            : "Not linked: showing the theme defaults until Hachidori connects.");

        // Theme: Hachidori's choice, a renderer, or one of Default's palettes.
        var choices = new[] { string.Empty }.Concat(PopupThemeProvider.Renderers.Where(r => r != "default")).Concat(themes.Palettes).ToArray();
        var labels = choices.Select(c => c.Length == 0 ? "Same as Hachidori" : PopupThemeProvider.Renderers.Contains(c) ? $"{c} (theme)" : $"{c} (Default palette)").ToArray();
        var index = Math.Max(0, Array.IndexOf(choices, config.HachidoriThemeOverride));
        if (ImGui.Combo("Theme", ref index, labels, labels.Length))
        {
            config.HachidoriThemeOverride = choices[index];
            Save();
        }

        var variants = new[] { string.Empty }.Concat(PopupThemeProvider.Variants).ToArray();
        var variantLabels = variants.Select(v => v.Length == 0 ? "Same as Hachidori" : v == "auto" ? "Automatic (Grey in game)" : v).ToArray();
        var variantIndex = Math.Max(0, Array.IndexOf(variants, config.HachidoriVariantOverride));
        if (ImGui.Combo("Colours", ref variantIndex, variantLabels, variantLabels.Length))
        {
            config.HachidoriVariantOverride = variants[variantIndex];
            Save();
        }

        HelpMarker("Crystal's colour schemes: Grey, Beige and Retro blue.");

        if (!Advanced)
        {
            ImGui.TextDisabled("Changes apply to the next popup.");
            return;
        }

        var opacity = config.FollowHachidoriOpacity;
        if (ImGui.Checkbox("Use Hachidori's popup opacity", ref opacity))
        {
            config.FollowHachidoriOpacity = opacity;
            Save();
        }

        if (!config.FollowHachidoriOpacity)
            ImGui.TextDisabled("Uses \"Popup opacity\" from the Popup tab.");

        var custom = config.ApplyHachidoriCustomCss;
        if (ImGui.Checkbox("Apply Hachidori's custom popup CSS", ref custom))
        {
            config.ApplyHachidoriCustomCss = custom;
            Save();
        }

        HelpMarker("Hachidori's Custom popup CSS and the CSS of enabled user scripts.");

        ImGui.TextUnformatted("Extra CSS for the game popup");
        HelpMarker("Applied after Hachidori's CSS. Supported: colours, backgrounds and linear gradients, borders and border-radius, padding, box-shadow, font sizes of the headword and reading, and custom properties (var()). For example:\n:host { --hoshidicts-accent: #f0a; }\n.gsm-hoshidicts-popup { border-radius: 20px; border: 2px solid gold; }\n.jl-spelling { color: orange; font-size: 26px; }");
        var extra = config.ExtraPopupCss;
        if (ImGui.InputTextMultiline("##extra-css", ref extra, 16384, new Vector2(-1, 140 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale)))
            config.ExtraPopupCss = extra;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();

        if (themes.Error is { } error)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), $"Theme error: {error}");
        else
            ImGui.TextDisabled($"Showing: {current.Renderer}{(current.Renderer == "default" ? $" / {current.Palette}" : string.Empty)}{(current.Variant.Length > 0 && current.Renderer == "crystal" ? $" / {current.Variant}" : string.Empty)}, {(current.Dark ? "dark" : "light")}.");
        ImGui.TextDisabled("Changes apply to the next popup. Try /hachidori search to check a theme.");
    }

    private void DrawMiningTab()
    {
        ImGui.TextWrapped("Media for cards you add with + in a popup, captured from the game itself. No GSM needed.");
        void Save() => config.Save();
        bool Toggle(string label, bool value, Action<bool> set)
        {
            if (ImGui.Checkbox(label, ref value))
            {
                set(value);
                Save();
            }

            return value;
        }

        Toggle("Screenshot", config.CaptureScreenshot, v => config.CaptureScreenshot = v);
        HelpMarker("Fills {screenshot} in your Hachidori Anki Template. The picture has no plugin windows in it.");
        if (media.Frames.Error is { } frameError)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), frameError);

        Toggle("Voice line from the game", config.RecordVoiceLines, v => config.RecordVoiceLines = v);
        HelpMarker("Records only FFXIV's own sound and cuts out the line you mined. Lower the BGM volume in game for cleaner clips.");
        if (config.RecordVoiceLines && media.Recorder is { } recorder)
        {
            if (recorder.Error is { } audioError)
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), audioError);
            else if (recorder.Running)
                ImGui.TextDisabled("Recording game audio.");
        }

        Toggle("Animation of the line", config.CaptureGif, v => config.CaptureGif = v);
        HelpMarker("A few seconds of the scene from when the line appeared. Without a GIF field it replaces the screenshot in your picture field.");
        if (config.CaptureGif)
        {
            using var indent = ImRaii.PushIndent();
            var formats = new[] { "AVIF (smallest, best quality)", "WebP", "GIF (plays everywhere, large)" };
            var format = (int)config.AnimationFormat;
            if (ImGui.Combo("Format", ref format, formats, formats.Length))
            {
                config.AnimationFormat = (AnimationFormat)format;
                Save();
            }

            if (config.AnimationFormat != AnimationFormat.Gif)
            {
                var ffmpeg = media.FfmpegPath;
                var caps = ffmpeg is null ? default : Ffmpeg.Capabilities(ffmpeg);
                var ready = config.AnimationFormat == AnimationFormat.Avif ? caps.Av1 is not null : caps.Webp;
                if (ready)
                    ImGui.TextDisabled($"FFmpeg: {ffmpeg}");
                else
                    ImGui.TextColored(new Vector4(1f, 0.75f, 0.4f, 1f), ffmpeg is null
                        ? "FFmpeg not found: saving GIF instead. Install FFmpeg (GSM includes one) or set its path under Advanced."
                        : $"This FFmpeg cannot write {(config.AnimationFormat == AnimationFormat.Avif ? "AVIF" : "WebP")}: saving {(caps.Webp ? "WebP" : "GIF")} instead.");
            }
        }

        ImGui.Separator();
        var modes = new[] { "Off", "VOICEVOX-compatible (VOICEVOX, AivisSpeech…)", "Custom URL" };
        var mode = (int)config.TtsMode;
        if (ImGui.Combo("TTS for unvoiced lines", ref mode, modes, modes.Length))
        {
            config.TtsMode = (TtsMode)mode;
            if (config.TtsMode == TtsMode.Voicevox && !config.TtsUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                config.TtsUrl = "http://127.0.0.1:50021";
            Save();
        }

        if (config.TtsMode != TtsMode.Off)
        {
            using var indent = ImRaii.PushIndent();
            var url = config.TtsUrl;
            if (ImGui.InputText(config.TtsMode == TtsMode.Voicevox ? "Engine address" : "URL", ref url, 512))
                config.TtsUrl = url.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit())
                Save();
            if (config.TtsMode == TtsMode.Voicevox)
                HelpMarker("VOICEVOX: http://127.0.0.1:50021 · AivisSpeech: http://127.0.0.1:10101");
            else
                HelpMarker("{text} becomes the sentence and {speaker} the speaker below. The URL must return audio (WAV, MP3 or Ogg).");

            var speaker = config.TtsSpeaker;
            if (ImGui.InputText("Speaker / voice", ref speaker, 128))
                config.TtsSpeaker = speaker;
            if (ImGui.IsItemDeactivatedAfterEdit())
                Save();
            if (config.TtsMode == TtsMode.Custom)
                Toggle("Send as POST with JSON {text, speaker}", config.TtsPost, v => config.TtsPost = v);

            if (ImGui.Button("Test TTS"))
            {
                mediaTest = "Speaking…";
                _ = TestTtsAsync();
            }
        }

        ImGui.Separator();
        if (ImGui.Button("Test AnkiConnect"))
        {
            mediaTest = "Checking AnkiConnect…";
            _ = TestAnkiConnectAsync();
        }

        HelpMarker("Voice lines, TTS audio and GIFs are added to the note through the AnkiConnect add-on, after Hachidori creates it.");
        if (mediaTest is not null)
            ImGui.TextWrapped(mediaTest);

        if (!Advanced)
            return;

        ImGui.Separator();
        ImGui.TextDisabled("Fields (blank: detected by name)");
        var audioField = config.SentenceAudioField;
        if (ImGui.InputTextWithHint("Sentence audio field", "e.g. SentenceAudio", ref audioField, 128))
            config.SentenceAudioField = audioField.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();
        var gifField = config.GifField;
        if (ImGui.InputTextWithHint("GIF field", "e.g. Picture", ref gifField, 128))
            config.GifField = gifField.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();
        Toggle("The animation may replace the screenshot", config.GifReplacesScreenshot, v => config.GifReplacesScreenshot = v);

        ImGui.Separator();
        var maxWidth = config.ScreenshotMaxWidth;
        if (ImGui.SliderInt("Screenshot max width", ref maxWidth, 640, 3840))
            config.ScreenshotMaxWidth = maxWidth;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();
        var quality = config.ScreenshotQuality;
        if (ImGui.SliderFloat("Screenshot JPEG quality", ref quality, 0.5f, 1f, "%.2f"))
            config.ScreenshotQuality = quality;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();

        var gifWidth = config.GifWidth;
        if (ImGui.SliderInt("Animation width", ref gifWidth, 320, 1280))
            config.GifWidth = gifWidth;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();
        var fps = config.GifFps;
        if (ImGui.SliderInt("Animation frames per second", ref fps, 4, 24))
            config.GifFps = fps;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();
        var seconds = config.GifSeconds;
        if (ImGui.SliderInt("Animation length (s)", ref seconds, 1, 8))
            config.GifSeconds = seconds;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();
        var crf = config.AvifCrf;
        if (ImGui.SliderInt("AVIF quality (CRF, lower is better)", ref crf, 18, 50))
            config.AvifCrf = crf;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();
        var webp = config.WebpQuality;
        if (ImGui.SliderInt("WebP quality", ref webp, 40, 95))
            config.WebpQuality = webp;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();
        var ffmpegPath = config.FfmpegPath;
        if (ImGui.InputTextWithHint("FFmpeg path", "blank: find automatically", ref ffmpegPath, 512))
            config.FfmpegPath = ffmpegPath.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();

        var ratio = config.VoiceDetectionRatio;
        if (ImGui.SliderFloat("Voice detection", ref ratio, 1.2f, 6f, "%.1f×"))
            config.VoiceDetectionRatio = ratio;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();
        HelpMarker("How much louder than the music before it a line must be to count as voiced. Raise it if unvoiced lines get music clips instead of TTS; lower it if quiet voices are missed.");
        var maxLine = config.MaxLineSeconds;
        if (ImGui.SliderInt("Longest voice line (s)", ref maxLine, 5, 60))
            config.MaxLineSeconds = maxLine;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();

        var anki = config.AnkiConnectUrl;
        if (ImGui.InputText("AnkiConnect address", ref anki, 256))
            config.AnkiConnectUrl = anki.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();
    }

    private async System.Threading.Tasks.Task TestTtsAsync()
    {
        try
        {
            var audio = await media.Tts.SpeakAsync("冒険者さん、こんにちは。").ConfigureAwait(false);
            mediaTest = $"TTS works: {audio.Length / 1024} KB of {AudioClips.Extension(audio) ?? "unknown"} audio.";
        }
        catch (Exception ex)
        {
            mediaTest = $"TTS failed: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task TestAnkiConnectAsync()
    {
        try
        {
            var version = await media.Anki.InvokeAsync("version").ConfigureAwait(false);
            mediaTest = $"AnkiConnect works (version {version}).";
        }
        catch (Exception ex)
        {
            mediaTest = ex.Message;
        }
    }

    private void DrawReaderTab()
    {
        Checkbox("Enable reader overlay", () => config.ReaderEnabled, v => config.ReaderEnabled = v);
        Checkbox("Show only while dialogue is open", () => config.ReaderAutoShowWithDialogue, v => config.ReaderAutoShowWithDialogue = v);
        Checkbox("Show history", () => config.ReaderShowHistory, v => config.ReaderShowHistory = v);
        if (Advanced)
        {
            Checkbox("Lock (no title bar, click-to-move off)", () => config.ReaderLocked, v => config.ReaderLocked = v);
            Checkbox("Hide text until hovered", () => config.ReaderHideUntilHovered, v => config.ReaderHideUntilHovered = v);
        }

        var anchor = (int)config.ReaderAnchor;
        if (ImGui.Combo("Position", ref anchor, ["Free", "Above the dialogue box", "Below the dialogue box"], 3))
        {
            config.ReaderAnchor = (ReaderAnchor)anchor;
            Changed(true);
        }

        var fontSize = config.ReaderFontSize;
        if (ImGui.SliderFloat("Reader font size", ref fontSize, 14, 48, "%.0f"))
            config.ReaderFontSize = fontSize;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Changed(true);
        if (!Advanced)
            return;
        var alpha = config.ReaderBackgroundAlpha;
        if (ImGui.SliderFloat("Reader opacity", ref alpha, 0f, 1f))
        {
            config.ReaderBackgroundAlpha = alpha;
            Changed(true);
        }

        var historySize = config.HistorySize;
        if (ImGui.SliderInt("History size", ref historySize, 10, 500))
        {
            config.HistorySize = historySize;
            Changed(true);
        }
    }

    private void DrawCaptureTab()
    {
        Checkbox("Dialogue (Talk, cutscene subtitles)", () => config.CaptureTalk, v => config.CaptureTalk = v);
        Checkbox("Battle talk", () => config.CaptureBattleTalk, v => config.CaptureBattleTalk = v);
        Checkbox("Chat", () => config.CaptureChat, v => config.CaptureChat = v);
        Checkbox("Read my character's name as katakana", () => config.KatakanaPlayerName, v => config.KatakanaPlayerName = v);
        if (!Advanced)
            return;
        using (ImRaii.Disabled(!config.KatakanaPlayerName))
        using (ImRaii.PushIndent())
        {
            var kana = config.PlayerNameKatakana;
            if (ImGui.InputTextWithHint("Katakana name", PlayerName.Suggested(), ref kana, 64))
            {
                config.PlayerNameKatakana = kana.Trim();
                Changed(true);
            }

            ImGui.TextDisabled("Blank uses the automatic reading shown. Separate first and last name with ・ or a space.");
        }

        ImGui.TextWrapped("Channel selection applies to reader history. Native chat hover scans the visible chat text.");
        using var disabled = ImRaii.Disabled(!config.CaptureChat);
        using var indent = ImRaii.PushIndent();
        foreach (var channel in SelectableChannels)
        {
            var on = config.ChatChannels.Contains(channel);
            if (ImGui.Checkbox(channel.ToString(), ref on))
            {
                if (on)
                    config.ChatChannels.Add(channel);
                else
                    config.ChatChannels.Remove(channel);
                Changed(true);
            }
        }
    }

    private void DrawPublishTab()
    {
        ImGui.TextWrapped("Sends game text over a WebSocket on this computer so GameSentenceMiner can read it. Off by default; nothing leaves this PC.");
        Checkbox("Publish text for GSM", () => config.PublishEnabled, v => config.PublishEnabled = v);
        using var disabled = ImRaii.Disabled(!config.PublishEnabled);

        var preset = Array.FindIndex(PublishPresets, p => p.Talk == config.PublishTalk && p.Battle == config.PublishBattleTalk && p.Popups == config.PublishPopups && p.Chat == config.PublishChat);
        var presetNames = PublishPresets.Select(p => p.Name).Append("Custom").ToArray();
        var selected = preset < 0 ? PublishPresets.Length : preset;
        if (ImGui.Combo("What to send", ref selected, presetNames, presetNames.Length) && selected < PublishPresets.Length)
        {
            var p = PublishPresets[selected];
            config.PublishTalk = p.Talk;
            config.PublishBattleTalk = p.Battle;
            config.PublishPopups = p.Popups;
            config.PublishChat = p.Chat;
            Changed(true);
        }

        using (ImRaii.PushIndent())
        {
            Checkbox("Dialogue and cutscene subtitles", () => config.PublishTalk, v => config.PublishTalk = v);
            Checkbox("Battle talk", () => config.PublishBattleTalk, v => config.PublishBattleTalk = v);
            Checkbox("Interaction popups (yes/no prompts, choice menus)", () => config.PublishPopups, v => config.PublishPopups = v);
            Checkbox("Chat (channels from the Capture tab)", () => config.PublishChat, v => config.PublishChat = v);
            if (config.PublishChat)
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "Sends other players' messages to GSM.");
        }
        Checkbox("Put the speaker's name before each line", () => config.PublishSpeaker, v => config.PublishSpeaker = v);

        if ((config.PublishTalk && !config.CaptureTalk) || (config.PublishBattleTalk && !config.CaptureBattleTalk) || (config.PublishChat && !config.CaptureChat))
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "A selected source is turned off in the Capture tab and won't be sent.");

        ImGui.Separator();
        var portIndex = Array.FindIndex(PublishPorts, p => p.Port == config.PublishPort);
        var portNames = PublishPorts.Select(p => $"{p.Name} ({p.Port})").Append("Custom").ToArray();
        var portSelected = portIndex < 0 ? PublishPorts.Length : portIndex;
        if (ImGui.Combo("GSM source slot", ref portSelected, portNames, portNames.Length) && portSelected < PublishPorts.Length)
        {
            config.PublishPort = PublishPorts[portSelected].Port;
            Changed(true);
        }
        HelpMarker("GSM listens to all three slots by default, and the name is only its label. Choose one that no other program you run uses.");

        var port = config.PublishPort;
        if (ImGui.InputInt("Port", ref port))
        {
            config.PublishPort = Math.Clamp(port, 1, 65535);
            Changed(true);
        }
        ImGui.TextDisabled($"In GSM's WebSocket sources, this is localhost:{config.PublishPort}");

        if (publisher.Error is { } error)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), error);
        else if (publisher.Listening)
            ImGui.TextUnformatted(publisher.ClientCount == 0 ? "Listening. Waiting for GSM to connect…" : $"Connected clients: {publisher.ClientCount}");
        using (ImRaii.Disabled(publisher.ClientCount == 0))
        {
            if (ImGui.Button("Send test line"))
                publisher.SendTest();
        }
    }

    private void DrawConnectionTab()
    {
        if (ImGui.Button("Reconnect"))
            service.Link.Reconnect();
        if (!Advanced)
        {
            DrawDictionaries();
            return;
        }

        var host = config.RelayHost;
        if (ImGui.InputText("Relay host", ref host, 128))
        {
            config.RelayHost = host.Trim();
            Changed(true);
        }

        var port = config.RelayPort;
        if (ImGui.InputInt("Relay port", ref port))
        {
            config.RelayPort = Math.Clamp(port, 1, 65535);
            Changed(true);
        }

        var apiPort = config.ApiPort;
        if (ImGui.InputInt("Yomitan API port (audio)", ref apiPort))
        {
            config.ApiPort = Math.Clamp(apiPort, 1, 65535);
            Changed(true);
        }

        var template = config.AnkiTemplateId;
        if (ImGui.InputText("Anki Template ID (blank: first)", ref template, 256))
        {
            config.AnkiTemplateId = template.Trim();
            Changed(true);
        }

        var title = config.DocumentTitle;
        if (ImGui.InputText("{document-title}", ref title, 256))
        {
            config.DocumentTitle = title;
            Changed(true);
        }

        DrawDictionaries();
    }

    private void DrawDictionaries()
    {
        if (service.Link.Connected)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Dictionaries on the host:");
            foreach (var d in service.Dictionaries)
            {
                var enabled = d["enabled"] is not System.Text.Json.Nodes.JsonValue e || !e.TryGetValue<bool>(out var en) || en;
                ImGui.BulletText($"{LookupResult.Str(d["title"])}{(enabled ? string.Empty : " (disabled)")}");
            }
        }
    }

    private void Checkbox(string label, Func<bool> get, Action<bool> set)
    {
        var value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            Changed(true);
        }
    }

    private static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
