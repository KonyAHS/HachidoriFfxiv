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
    // -------------------------------------------------------------- Quick Guide tab

    private void DrawQuickGuideTab()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var connected = service.Link.Connected;
        var offlineReady = service.UsingOffline;
        var dictReady = connected || offlineReady;

        ImGui.TextWrapped("Hover Japanese in dialogue, battle talk and chat to look it up, then mine Anki cards with game media in one click.");

        DrawPlatformCompatibilityGuide();

        if (Section("guide-step1", dictReady ? "1. Connect dictionaries  ·  Ready" : "1. Connect dictionaries", defaultOpen: !dictReady, accent: dictReady ? AccentGreen : AccentAmber))
        {
            if (connected)
            {
                ImGui.TextColored(AccentGreen, $"Linked to Hachidori v{service.Link.HostVersion} with {service.Link.DictionaryCount} dictionaries.");
            }
            else if (offlineReady)
            {
                ImGui.TextColored(AccentAmber, "Offline mode: using your synced dictionaries.");
            }
            else
            {
                ImGui.BulletText("Open Anki with the Hachidori Relay add-on.");
                ImGui.BulletText("Open Chrome with Hachidori and turn on Settings → Sharing.");
                ImGui.BulletText("Once linked, Offline Mode lets you play without Chrome.");
            }

            ImGui.Spacing();
            if (ImGui.Button("Reconnect"))
                service.Link.Reconnect();
            ImGui.SameLine(0, 6 * scale);
            if (ImGui.Button("Connection settings"))
                pendingSelectTab = "Connection";
            if (!config.OfflineMode && connected && offline is { } off)
            {
                ImGui.SameLine(0, 6 * scale);
                using (ImRaii.Disabled(off.Sync.Running))
                {
                    if (ImGui.Button("Set up Offline Mode"))
                        _ = off.SetUpAsync();
                }
            }
        }

        // The FFXIV dictionary step disappears once it is installed and enabled (the Connection tab keeps it).
        var installedFfxivDict = FindInstalledFfxivDictionary();
        if (installedFfxivDict is not { Enabled: true }
            && Section("guide-ffxiv-dict",
                installedFfxivDict is null ? "Optional: FFXIV dictionary" : "Optional: FFXIV dictionary  ·  Disabled",
                defaultOpen: installedFfxivDict is null,
                accent: installedFfxivDict is null ? null : AccentAmber))
        {
            DrawFfxivDictionaryBody(scale, installedFfxivDict, "guide");
        }

        if (Section("guide-step2", "2. Hover Japanese text", defaultOpen: true))
        {
            Checkbox("Hover game text", () => config.NativeTextHover, v => config.NativeTextHover = v);
            ImGui.SameLine(0, 16 * scale);
            Checkbox("Include chat", () => config.NativeChatHover, v => config.NativeChatHover = v);
            DrawScanModifierControl();
            ImGui.Spacing();
            if (openSearchSample is not null)
            {
                if (ImGui.Button("Try a sample sentence"))
                    openSearchSample(SearchWindow.SampleLine);
                ImGui.SameLine(0, 6 * scale);
            }
            if (ImGui.Button("Popup settings"))
                pendingSelectTab = "Popup";
        }

        if (Section("guide-step3", ankiConnectedHint ? "3. Mine to Anki  ·  Ready" : "3. Mine to Anki (optional)", defaultOpen: !dictReady, accent: ankiConnectedHint ? AccentGreen : null))
        {
            ImGui.TextWrapped("Click + on a popup entry to make a card. Choose what to attach:");
            Checkbox("Screenshot", () => config.CaptureScreenshot, v => config.CaptureScreenshot = v);
            ImGui.SameLine(0, 12 * scale);
            Checkbox("Voice line", () => config.RecordVoiceLines, v => config.RecordVoiceLines = v);
            ImGui.SameLine(0, 12 * scale);
            Checkbox("Animation", () => config.CaptureGif, v => config.CaptureGif = v);
            ImGui.SameLine(0, 12 * scale);
            Checkbox("Translation", () => config.TranslationEnabled, v => config.TranslationEnabled = v);

            if (ImGui.Button("Test AnkiConnect##guide"))
            {
                mediaTest = "Checking AnkiConnect…";
                _ = TestAnkiConnectAsync();
            }
            ImGui.SameLine(0, 6 * scale);
            if (ImGui.Button("Mining settings"))
                pendingSelectTab = "Mining";
            if (mediaTest is not null)
                ImGui.TextWrapped(mediaTest);
        }

        if (Section("guide-commands", "Chat commands", defaultOpen: false))
        {
            using var tbl = ImRaii.Table("##guide-commands", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
            if (tbl.Success)
            {
                ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, 160 * scale);
                ImGui.TableSetupColumn("What it does", ImGuiTableColumnFlags.WidthStretch);
                foreach (var (cmd, desc) in new[]
                {
                    ("/hachidori", "Open or close Settings"),
                    ("/hachidori stats", "Immersion stats"),
                    ("/hachidori search", "Search window"),
                    ("/hachidori <text>", "Look up text"),
                    ("/hachidori reader", "Pin or unpin the reader overlay"),
                    ("/hachidori pad", "Controller reading mode (put it on a hotbar macro)"),
                    ("/hachidori freeze", "Freeze the screen to look up tooltips"),
                    ("/hachidori pictures", "NPC picture dictionary"),
                    ("/hachidori logs", "Export a debug log"),
                })
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(cmd);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(desc);
                }
            }
        }

        ImGui.Spacing();
        var dismissed = config.QuickGuideDismissed;
        if (ImGui.Checkbox("Open the Popup tab first next time", ref dismissed))
        {
            config.QuickGuideDismissed = dismissed;
            config.Save();
        }
    }

    private void DrawPlatformCompatibilityGuide()
    {
        var platform = PlatformInfo.Current;
        var missingWebView = platform.SupportsWebView2Overlay && webView is { RuntimeAvailable: false };
        // Full native support needs no explanation; only surface the section when something falls back.
        if (!platform.IsWine && platform.SupportsProcessAudioLoopback && !missingWebView)
            return;

        if (!Section("guide-platform", $"Compatibility: {platform.DisplayName}", defaultOpen: true, accent: AccentAmber))
            return;

        if (platform.IsWine)
        {
            ImGui.BulletText("Popups use the built-in renderer (Hachidori Web needs Windows).");
            ImGui.BulletText(platform.HostOs == HostOsKind.MacOs
                ? "Voice recording: macOS has no audio loopback. Use TTS (Mining tab) or a virtual loopback device."
                : "Voice recording uses system audio, so other apps can be heard. Lower BGM for cleaner clips.");
            ImGui.BulletText("Install FFmpeg on the host for MP3/AAC audio and AVIF/WebP animations.");
            ImGui.BulletText("Run Anki (with Hachidori Relay) and Chrome natively; they share 127.0.0.1 with the game.");

            if (platform.IsSteamDeck)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted("Steam Deck");
                ImGui.BulletText("Link Hachidori once in Desktop Mode, then turn on Offline Mode to play in Gaming Mode without Chrome.");
                ImGui.BulletText("Put /hachidori pad and /hachidori freeze on cross-hotbar macros (B / Circle exits Freeze Frame).");
                ImGui.BulletText("Turn on \"plugin gamepad input\" in Dalamud settings; the stick and trackpads move the cursor.");
            }

            ImGui.TextDisabled("Support outside Windows is best-effort.");
            return;
        }

        if (!platform.SupportsProcessAudioLoopback)
            ImGui.BulletText("This Windows version can't record FFXIV alone, so voice recording uses system audio.");
        if (missingWebView)
            ImGui.BulletText("The Microsoft Edge WebView2 Runtime isn't installed, so popups use the built-in renderer.");
    }

    // -------------------------------------------------------------- Popup tab

    private void DrawPopupTab()
    {
        var scale = ImGuiHelpers.GlobalScale;

        if (Section("popup-scan", "Hover & Scanning", defaultOpen: true))
        {
            Checkbox("Hover game text", () => config.NativeTextHover, v => config.NativeTextHover = v);
            ImGui.SameLine(0, 16 * scale);
            Checkbox("Include chat", () => config.NativeChatHover, v => config.NativeChatHover = v);
            DrawScanModifierControl();
            DrawFreezeHotkeyControl();

            if (Advanced)
            {
                AdvancedDivider();
                Checkbox("Hover whole words (Hachidori tokenizer)", () => config.TokenizedHover, v => config.TokenizedHover = v);
                Checkbox("Other native windows (experimental)", () => config.NativeOtherTextHover, v => config.NativeOtherTextHover = v);
                Checkbox("Only start scans on Japanese characters", () => config.ScanOnlyJapanese, v => config.ScanOnlyJapanese = v);
                Checkbox("Use the personal dictionary", () => config.UsePersonalDictionary, v => config.UsePersonalDictionary = v);
                DrawScanLengthControl();
                DrawMaxResultsControl();
                DrawMaxPopupDepthControl();
                Checkbox("Show native hover targets (debug)", () => config.NativeHoverDebug, v => config.NativeHoverDebug = v);
                if (config.NativeHoverDebug)
                {
                    ImGui.TextDisabled($"Native text regions under mouse: {nativeHover.RegionCount}");
                    if (nativeHover.LastError is { } error) ImGui.TextWrapped(error);
                }
            }
        }

        if (Section("popup-appearance", "Appearance", defaultOpen: true))
            DrawAppearanceSection();

        if (Section("popup-size", "Size & Behavior", defaultOpen: true))
        {
            DrawPopupWidthControl();
            DrawPopupMaxHeightControl();
            DrawPopupFontSizeControl();

            if (!config.FollowHachidoriTheme || !config.FollowHachidoriOpacity)
                DrawPopupOpacityControl();
            else
                ImGui.TextDisabled("Opacity follows Hachidori's theme.");

            if (Advanced)
            {
                AdvancedDivider();
                DrawHideDelayControl();
                Checkbox("Hide the popup when the mouse leaves", () => config.AutoHideRootPopup, v => config.AutoHideRootPopup = v);
            }
        }

        if (Section("popup-display", "Popup Contents", defaultOpen: true))
        {
            Checkbox("Frequencies", () => config.ShowFrequencies, v => config.ShowFrequencies = v);
            ImGui.SameLine(0, 16 * scale);
            Checkbox("Pitch accent", () => config.ShowPitchAccent, v => config.ShowPitchAccent = v);
            ImGui.SameLine(0, 16 * scale);
            Checkbox("Dictionary names", () => config.ShowDictionaryNames, v => config.ShowDictionaryNames = v);
            if (Advanced)
            {
                AdvancedDivider();
                Checkbox("Deinflection", () => config.ShowInflections, v => config.ShowInflections = v);
                Checkbox("Definition tags", () => config.ShowDefinitionTags, v => config.ShowDefinitionTags = v);
                Checkbox("Attribution", () => config.ShowAttribution, v => config.ShowAttribution = v);
                Checkbox("Lookup counts (Hachidori statistics)", () => config.ShowLookupCounts, v => config.ShowLookupCounts = v);
                Checkbox("Honour Hachidori's lookup blur", () => config.HonourDefinitionBlur, v => config.HonourDefinitionBlur = v);
            }
        }

        if (Section("popup-audio", "Pronunciation Audio", defaultOpen: false))
        {
            Checkbox("Auto-play pronunciation", () => config.AutoPlayAudio, v => config.AutoPlayAudio = v);
            DrawAudioVolumeControl();
        }
    }

    private void DrawAppearanceSection()
    {
        var scale = ImGuiHelpers.GlobalScale;

        DrawFollowThemeControl();
        if (config.FollowHachidoriTheme)
        {
            DrawThemeOverrideControl();
            DrawVariantOverrideControl();
            if (service.Link.Connected)
            {
                var hostTheme = service.OptionString("popupTheme", "default");
                var hostVariant = service.OptionString("popupThemeVariant", "auto");
                ImGui.TextDisabled($"Hachidori is set to \"{hostTheme}\"{(hostVariant != "auto" ? $" ({hostVariant})" : string.Empty)}.");
            }
            else
            {
                ImGui.TextDisabled("Not linked: using theme defaults until Hachidori connects.");
            }
        }

        DrawRendererStatus(scale);

        if (!Advanced)
            return;

        AdvancedDivider();
        DrawUseWebViewControl();
        if (config.FollowHachidoriTheme)
        {
            DrawFollowOpacityControl();
            DrawApplyCustomCssControl();
            DrawAutoSyncWebViewControl();
            DrawWebViewBundlePathControl();
            if (webView?.Bundles.ActivePath is { Length: > 0 } activePath)
                ImGui.TextDisabled($"Active bundle: {activePath}");
            if (webView?.Bundles.LastGoodVersion is { Length: > 0 } lastGood)
                ImGui.TextDisabled($"Last verified version: v{lastGood}");
            DrawExtraCssControl();
        }

        if (webView is not { Active: true })
        {
            var current = PopupTheme.Current;
            ImGui.TextDisabled($"Built-in look: {current.Renderer}{(current.Renderer == "default" ? $" / {current.Palette}" : string.Empty)}{(current.Variant.Length > 0 && current.Renderer == "crystal" ? $" / {current.Variant}" : string.Empty)}, {(current.Dark ? "dark" : "light")}.");
        }
    }

    private void DrawRendererStatus(float scale)
    {
        var supported = webView is { PlatformSupported: true };
        if (!supported)
            ImGui.TextDisabled("Renderer: built-in (Hachidori Web is Windows-only).");
        else if (!config.UseWebViewPopup)
            ImGui.TextDisabled("Renderer: built-in.");
        else if (!config.FollowHachidoriTheme)
            ImGui.TextDisabled("Renderer: built-in. Use Hachidori's theme to render with Hachidori Web.");
        else if (webView!.Active)
            ImGui.TextColored(AccentGreen, $"Renderer: Hachidori Web v{webView.LoadedVersion}");
        else if (!webView.RuntimeAvailable)
            ImGui.TextColored(AccentAmber, "Renderer: built-in. Install the Microsoft Edge WebView2 Runtime to use Hachidori Web.");
        else if (webView.Bundles.Syncing)
            ImGui.TextDisabled("Renderer: syncing Hachidori Web…");
        else if (webView.Bundles.Ready)
            ImGui.TextColored(AccentAmber, $"Renderer: built-in until Hachidori Web is ready ({webView.StatusText}).");
        else
            ImGui.TextColored(AccentAmber, "Renderer: built-in until Hachidori Web is downloaded.");

        if (supported && config.UseWebViewPopup && webView!.LastError is { Length: > 0 } wvError)
            ImGui.TextColored(AccentRed, wvError);
        if (webView is not { Active: true } && themes.Error is { } themeError)
            ImGui.TextColored(AccentRed, $"Theme error: {themeError}");

        // Manual sync is only needed when the bundle isn't running, or for advanced users.
        if (!supported || !config.UseWebViewPopup || (!Advanced && webView!.Active))
            return;

        using (ImRaii.Disabled(webView!.Bundles.Syncing))
        {
            if (ImGui.SmallButton("Sync Hachidori Web now"))
                _ = webView.SyncBundleNowAsync(force: true);
        }
        if (webView.Bundles.SyncedAt is { } syncedAt)
        {
            ImGui.SameLine(0, 10 * scale);
            ImGui.TextDisabled($"Last synced {Ago(new DateTimeOffset(DateTime.SpecifyKind(syncedAt, DateTimeKind.Utc)))}");
        }
    }

    // -------------------------------------------------------------- Mining tab

    private void DrawMiningTab()
    {
        ImGui.TextDisabled("What gets added to cards you mine with + in a popup.");
        DrawAnkiNotifyToggleControl();

        if (Section("mining-visuals", "Screenshots & Animation", defaultOpen: true))
        {
            DrawScreenshotToggleControl();
            if (media.Frames.Error is { } frameError)
                ImGui.TextColored(AccentAmber, frameError);
            if (Advanced && config.CaptureScreenshot)
            {
                using (ImRaii.PushIndent())
                {
                    DrawScreenshotMaxWidthControl();
                    DrawScreenshotQualityControl();
                }
            }

            ImGui.Spacing();
            DrawGifToggleControl();
            if (config.CaptureGif)
            {
                using var indent = ImRaii.PushIndent();
                DrawAnimationFormatControl();
                if (config.AnimationFormat != AnimationFormat.Gif)
                {
                    var ffmpeg = media.FfmpegPath;
                    var caps = ffmpeg is null ? default : Ffmpeg.Capabilities(ffmpeg);
                    var ready = config.AnimationFormat == AnimationFormat.Avif ? caps.Av1 is not null : caps.Webp;
                    if (ready)
                        ImGui.TextDisabled($"FFmpeg: {ffmpeg}");
                    else
                        ImGui.TextColored(AccentAmber, ffmpeg is null
                            ? "FFmpeg not found, so GIF is saved instead. Install FFmpeg (GSM includes one) or set its path under Show advanced."
                            : $"This FFmpeg cannot write {(config.AnimationFormat == AnimationFormat.Avif ? "AVIF" : "WebP")}: saving {(caps.Webp ? "WebP" : "GIF")} instead.");
                }

                if (Advanced)
                {
                    DrawGifWidthControl();
                    DrawGifFpsControl();
                    DrawGifSecondsControl();
                    DrawAvifCrfControl();
                    DrawWebpQualityControl();
                    DrawFfmpegPathControl();
                }
            }
        }

        if (Section("mining-audio", "Voice Recording & TTS", defaultOpen: true))
        {
            DrawVoiceRecordToggleControl();
            if (config.RecordVoiceLines && media.Recorder is { } recorder)
            {
                if (recorder.Error is { } audioError)
                    ImGui.TextColored(AccentAmber, audioError);
                else if (recorder.Running)
                {
                    if (recorder.Mode == GameAudioCaptureMode.SystemLoopback)
                        ImGui.TextColored(AccentAmber, "Recording all system audio, so other apps can be heard.");
                    if (!media.Voices.HooksActive)
                        ImGui.TextColored(AccentAmber, $"Voice detection: {media.Voices.DetectionModeSummary}.");
                    else if (Advanced)
                        ImGui.TextDisabled($"Recording FFXIV audio only · {media.Voices.DetectionModeSummary}.");
                }
            }

            if (Advanced && config.RecordVoiceLines)
            {
                using (ImRaii.PushIndent())
                {
                    DrawVoiceRatioControl();
                    DrawMaxLineSecondsControl();
                    DrawVoiceBufferSecondsControl();
                }
            }

            ImGui.Spacing();
            DrawTtsModeControl();
            if (config.TtsMode != TtsMode.Off)
            {
                using var indent = ImRaii.PushIndent();
                DrawTtsUrlControl();
                DrawTtsSpeakerControl();
                if (config.TtsMode == TtsMode.Custom)
                    Toggle("Send as POST with JSON {text, speaker}", config.TtsPost, v => config.TtsPost = v);

                ImGui.Spacing();
                DrawTtsInjectOstControl();
                if (config.TtsInjectOst)
                {
                    using var ostIndent = ImRaii.PushIndent();
                    DrawTtsOstVolumeControl();
                    if (Advanced)
                    {
                        DrawTtsOstRespectMuteControl();
                        DrawCurrentOstStatus();
                    }
                }

                if (ImGui.Button("Test TTS"))
                {
                    mediaTest = "Speaking…";
                    _ = TestTtsAsync();
                }
            }
        }

        if (Section("mining-translation", "Sentence Translation", defaultOpen: config.TranslationEnabled))
        {
            DrawTranslationSection();
        }

        if (Section("mining-anki", "AnkiConnect & Fields", defaultOpen: false))
        {
            if (ImGui.Button("Test AnkiConnect"))
            {
                mediaTest = "Checking AnkiConnect…";
                _ = TestAnkiConnectAsync();
            }
            HelpMarker("Voice lines, TTS and animations are added through the AnkiConnect add-on after Hachidori creates the note.");

            if (Advanced)
            {
                AdvancedDivider();
                ImGui.TextDisabled("Leave a field blank to detect it by name.");
                DrawSentenceAudioFieldControl();
                DrawGifFieldControl();
                Toggle("The animation may replace the screenshot", config.GifReplacesScreenshot, v => config.GifReplacesScreenshot = v);
                DrawAnkiConnectUrlControl();
                DrawAnkiConnectApiKeyControl();
            }
        }

        if (mediaTest is not null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(mediaTest);
        }
    }

    private void DrawTranslationSection()
    {
        Toggle("Sentence translation", config.TranslationEnabled, v => config.TranslationEnabled = v);
        HelpMarker("Translates the mined line with an AI provider, using nearby dialogue as context.");
        ImGui.SameLine(0, 10 * ImGuiHelpers.GlobalScale);
        if (ImGui.SmallButton("Import from GSM"))
        {
            try { mediaTest = media.Translator.ImportFromGsm(); config.Save(); }
            catch (Exception ex) { mediaTest = $"Import failed: {ex.Message}"; }
        }
        HelpMarker("Copies the provider, models, keys, prompts and Anki field from GameSentenceMiner.");

        if (!config.TranslationEnabled)
            return;

        using var indent = ImRaii.PushIndent();
        DrawTranslationProviderControl();

        var settings = media.Translator.Settings(config.TranslationProvider);
        var cli = config.TranslationProvider is TranslationProvider.Codex or TranslationProvider.ClaudeCode;
        if (cli)
        {
            ImGui.TextWrapped(config.TranslationProvider == TranslationProvider.Codex
                ? "Runs `codex exec` read-only in an empty folder with your Codex login. Nothing else is installed."
                : "Runs `claude -p` with no tools in an empty folder with your Claude Code login. Nothing else is installed.");
        }
        else if (config.TranslationProvider is not (TranslationProvider.Ollama or TranslationProvider.LmStudio))
        {
            var key = settings.ApiKey;
            if (ImGui.InputText("API key", ref key, 512, ImGuiInputTextFlags.Password))
                settings.ApiKey = key.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
        }

        if (config.TranslationProvider != TranslationProvider.DeepL)
        {
            var model = settings.Model;
            if (ImGui.InputTextWithHint("Model", cli ? "blank: the CLI's default" : "", ref model, 256))
                settings.Model = model.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
        }

        DrawTranslationFieldControl();

        if (ImGui.Button("Test translation"))
        {
            mediaTest = "Translating…";
            _ = TestTranslationAsync();
        }

        if (!Advanced)
            return;

        AdvancedDivider();

        if (!cli && config.TranslationProvider != TranslationProvider.DeepL)
        {
            var backup = settings.BackupModel;
            if (ImGui.InputText("Backup model", ref backup, 256))
                settings.BackupModel = backup.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
        }

        if (!cli)
        {
            var url = settings.Url;
            if (ImGui.InputTextWithHint("API address", Translator.Defaults[config.TranslationProvider].Url, ref url, 512))
                settings.Url = url.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
        }

        if (config.TranslationProvider == TranslationProvider.OpenAI)
        {
            var effort = settings.ReasoningEffort;
            if (ImGui.InputTextWithHint("Reasoning effort", "blank, low, medium, high", ref effort, 32))
                settings.ReasoningEffort = effort.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
        }

        if (config.TranslationProvider == TranslationProvider.DeepL)
        {
            var target = config.DeepLTargetLanguage;
            if (ImGui.InputText("DeepL target language", ref target, 16))
                config.DeepLTargetLanguage = target.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
            return;
        }

        if (cli)
        {
            var path = config.TranslationProvider == TranslationProvider.Codex ? config.CodexPath : config.ClaudeCodePath;
            if (ImGui.InputTextWithHint("CLI path", "blank: find automatically", ref path, 512))
            {
                if (config.TranslationProvider == TranslationProvider.Codex) config.CodexPath = path.Trim();
                else config.ClaudeCodePath = path.Trim();
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
        }

        DrawTranslationLanguageControl();

        var promptKinds = new[] { "Canned translation prompt", "Canned context prompt", "Custom prompt" };
        var kind = config.UseCannedTranslationPrompt ? 0 : config.UseCannedContextPrompt ? 1 : 2;
        if (ImGui.Combo("Prompt", ref kind, promptKinds, promptKinds.Length))
        {
            config.UseCannedTranslationPrompt = kind == 0;
            config.UseCannedContextPrompt = kind == 1;
            config.Save();
        }

        if (kind == 2)
        {
            var custom = config.CustomTranslationPrompt;
            if (ImGui.InputTextMultiline("##custom-prompt", ref custom, 8192, new Vector2(-1, 80 * ImGuiHelpers.GlobalScale)))
                config.CustomTranslationPrompt = custom;
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
        }

        var full = config.CustomFullPrompt;
        ImGui.TextUnformatted("Full prompt template (blank: GSM's)");
        HelpMarker("Placeholders: {game_title} {character_context} {dialogue_context} {prompt_to_use} {sentence} {native_language}. Must include {sentence}.");
        if (ImGui.InputTextMultiline("##full-prompt", ref full, 16384, new Vector2(-1, 80 * ImGuiHelpers.GlobalScale)))
            config.CustomFullPrompt = full;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        DrawDialogueContextControl();
        if (!cli)
        {
            DrawTemperatureControl();
            DrawTopPControl();
            DrawMaxTokensControl();
        }
    }

    // -------------------------------------------------------------- Controller tab

    private void DrawControllerTab()
    {
        var ps = config.ControllerPlayStationLabels;
        ImGui.TextDisabled("Read dialogue word by word with a controller. While the mode is on, the game ignores the controller.");

        if (Section("ctrl-activation", "Activation", defaultOpen: true))
        {
            Toggle("Controller reading mode", config.ControllerEnabled, v => config.ControllerEnabled = v);
            if (!config.ControllerEnabled)
                return;

            ImGui.TextUnformatted("Easiest: put this on a cross hotbar macro.");
            ImGui.SameLine();
            ImGui.TextUnformatted("/hachidori pad");
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy"))
                ImGui.SetClipboardText("/hachidori pad");

            DrawControllerHoldButtonControl();
            if (config.ControllerHoldButton != GamepadButtons.None)
            {
                DrawControllerTriggerModeControl();
                if (config.ControllerTriggerMode == ControllerTriggerMode.Hold)
                    DrawControllerHoldSecondsControl();
            }
            DrawControllerToggleKeyControl();
        }

        if (Section("ctrl-behavior", "Behavior & Display", defaultOpen: true))
        {
            Checkbox("Scan words automatically under the cursor", () => config.ControllerAutoScan, v => config.ControllerAutoScan = v);
            HelpMarker("When off, move the cursor freely without popping up definitions, then press the Look up button to open a popup.");
            if (!config.ControllerAutoScan)
            {
                using var indent = ImRaii.PushIndent();
                DrawControllerScanButtonControl();
            }
            Checkbox("Start in word mode", () => config.ControllerStartInWordMode, v => config.ControllerStartInWordMode = v);
            Checkbox("Leave the mode when the dialogue closes", () => config.ControllerExitWhenDialogueCloses, v => config.ControllerExitWhenDialogueCloses = v);
            Checkbox("Show button hints", () => config.ControllerShowHints, v => config.ControllerShowHints = v);
            Checkbox("PlayStation button names", () => config.ControllerPlayStationLabels, v => config.ControllerPlayStationLabels = v);
            Checkbox("Move the cursor with the DualSense/DS4 touchpad", () => config.ControllerUseTouchpad, v => config.ControllerUseTouchpad = v);
            HelpMarker("Windows only. Steam Deck trackpads already move the cursor when mapped to mouse in Steam Input.");
            if (Advanced)
                DrawControllerCursorSpeedControl();
        }

        if (Advanced && Section("ctrl-binds", "Button Bindings", defaultOpen: false))
        {
            using var bindsId = ImRaii.PushId("ctrl-binds");
            var bindLabels = BindChoices.Select(b => GamepadNavigator.ButtonLabel(b, ps)).ToArray();
            void BindCombo(string label, Func<GamepadButtons> get, Action<GamepadButtons> set)
            {
                var idx = Math.Max(0, Array.IndexOf(BindChoices, get()));
                if (ImGui.Combo(label, ref idx, bindLabels, bindLabels.Length))
                {
                    set(BindChoices[idx]);
                    config.Save();
                }
            }

            BindCombo("Look up word (when auto-scan is off)", () => config.ControllerScanButton, v => config.ControllerScanButton = v);
            BindCombo("Mine to Anki", () => config.ControllerMineButton, v => config.ControllerMineButton = v);
            BindCombo("Play audio", () => config.ControllerAudioButton, v => config.ControllerAudioButton = v);
            BindCombo("Word / char mode", () => config.ControllerWordModeButton, v => config.ControllerWordModeButton = v);
            BindCombo("Back / exit", () => config.ControllerBackExitButton, v => config.ControllerBackExitButton = v);
            BindCombo("Previous entry", () => config.ControllerPrevEntryButton, v => config.ControllerPrevEntryButton = v);
            BindCombo("Next entry", () => config.ControllerNextEntryButton, v => config.ControllerNextEntryButton = v);
            BindCombo("Look up inside popup", () => config.ControllerDiveButton, v => config.ControllerDiveButton = v);
            BindCombo("Cycle UI window", () => config.ControllerWindowButton, v => config.ControllerWindowButton = v);

            if (ImGui.SmallButton("Reset keybinds only"))
            {
                config.ControllerCursorSpeed = Defaults.ControllerCursorSpeed;
                config.ControllerScanButton = Defaults.ControllerScanButton;
                config.ControllerMineButton = Defaults.ControllerMineButton;
                config.ControllerAudioButton = Defaults.ControllerAudioButton;
                config.ControllerWordModeButton = Defaults.ControllerWordModeButton;
                config.ControllerBackExitButton = Defaults.ControllerBackExitButton;
                config.ControllerPrevEntryButton = Defaults.ControllerPrevEntryButton;
                config.ControllerNextEntryButton = Defaults.ControllerNextEntryButton;
                config.ControllerDiveButton = Defaults.ControllerDiveButton;
                config.ControllerWindowButton = Defaults.ControllerWindowButton;
                config.Save();
            }
        }

        if (Section("ctrl-ref", "Controls Reference", defaultOpen: false))
        {
            string L(GamepadButtons b) => GamepadNavigator.ButtonLabel(b, ps);
            var rows = new List<(string Keys, string Action)>
            {
                ("Left stick", "move virtual cursor over any UI or dialogue text"),
                ("D-pad", "previous / next word and line"),
                ("Right stick", "scroll the popup"),
            };
            if (!config.ControllerAutoScan)
                rows.Add((L(config.ControllerScanButton), "look up the word under the cursor"));
            rows.Add((L(config.ControllerMineButton), "add the selected entry to Anki"));
            rows.Add((L(config.ControllerAudioButton), "play its pronunciation"));
            rows.Add(($"{L(config.ControllerPrevEntryButton)} / {L(config.ControllerNextEntryButton)}", "previous / next dictionary entry"));
            rows.Add((L(config.ControllerDiveButton), "move the cursor into the popup to look up a word inside it"));
            rows.Add((L(config.ControllerWindowButton), "cycle between open UI windows (or back out of a nested popup)"));
            rows.Add((L(config.ControllerWordModeButton), "switch between words and characters"));
            rows.Add((L(config.ControllerBackExitButton), config.ControllerAutoScan
                ? "back out of a nested popup, or leave the mode"
                : "close the popup, back out of a nested popup, or leave the mode"));
            if (config.ControllerHoldButton != GamepadButtons.None)
            {
                var trigLabel = GamepadNavigator.ButtonComboLabel(config.ControllerHoldButton, ps);
                rows.Add((config.ControllerTriggerMode == ControllerTriggerMode.Hold ? $"Hold {trigLabel}" : trigLabel, "enter or leave the mode"));
            }
            if (config.ControllerToggleKey > 0)
                rows.Add((KeybindFormat.Format(config.ControllerToggleKey, config.ControllerToggleKeyCtrl, config.ControllerToggleKeyShift, config.ControllerToggleKeyAlt), "enter or leave the mode"));
            rows.Add((ps ? "Select / Options" : "Back / Start", "leave the mode"));
            foreach (var (keys, action) in rows)
                ImGui.BulletText($"{keys}: {action}");
        }
    }

    // -------------------------------------------------------------- Reader & Text tab

    private void DrawReaderTab()
    {
        var scale = ImGuiHelpers.GlobalScale;

        if (Section("reader-overlay", "Reader Overlay", defaultOpen: true))
        {
            Checkbox("Enable reader overlay", () => config.ReaderEnabled, v => config.ReaderEnabled = v);
            Checkbox("Show only while dialogue is open", () => config.ReaderAutoShowWithDialogue, v => config.ReaderAutoShowWithDialogue = v);
            Checkbox("Show history", () => config.ReaderShowHistory, v => config.ReaderShowHistory = v);
            DrawReaderAnchorControl();
            DrawReaderFontSizeControl();
            if (Advanced)
            {
                AdvancedDivider();
                Checkbox("Lock (no title bar, click-to-move off)", () => config.ReaderLocked, v => config.ReaderLocked = v);
                Checkbox("Hide text until hovered", () => config.ReaderHideUntilHovered, v => config.ReaderHideUntilHovered = v);
                DrawReaderOpacityControl();
                DrawHistorySizeControl();
            }
        }

        if (Section("capture-sources", "Captured Text", defaultOpen: true))
        {
            ImGui.TextDisabled("Text the reader, stats and GSM receive.");
            Checkbox("Dialogue & cutscenes", () => config.CaptureTalk, v => config.CaptureTalk = v);
            ImGui.SameLine(0, 16 * scale);
            Checkbox("Battle talk", () => config.CaptureBattleTalk, v => config.CaptureBattleTalk = v);
            ImGui.SameLine(0, 16 * scale);
            Checkbox("Chat", () => config.CaptureChat, v => config.CaptureChat = v);

            ImGui.Spacing();
            Checkbox("Read my character's name as katakana", () => config.KatakanaPlayerName, v => config.KatakanaPlayerName = v);
            if (Advanced)
            {
                using (ImRaii.Disabled(!config.KatakanaPlayerName))
                using (ImRaii.PushIndent())
                {
                    DrawPlayerNameKatakanaControl();
                    HelpMarker("Blank uses the automatic reading shown. Separate first and last name with ・ or a space.");
                }
            }
        }

        if (Advanced && Section("capture-channels", "Chat Channels", defaultOpen: false))
        {
            ImGui.TextDisabled("Which channels reach the reader history and stats. Chat hover works on any visible chat.");
            using var disabled = ImRaii.Disabled(!config.CaptureChat);
            using var table = ImRaii.Table("##chat-channels-grid", 3, ImGuiTableFlags.SizingStretchSame);
            if (table.Success)
            {
                foreach (var channel in SelectableChannels)
                {
                    ImGui.TableNextColumn();
                    var on = config.ChatChannels.Contains(channel);
                    if (ImGui.Checkbox(channel.ToString(), ref on))
                    {
                        if (on) config.ChatChannels.Add(channel);
                        else config.ChatChannels.Remove(channel);
                        Changed(true);
                    }
                }
            }
        }
    }

    // -------------------------------------------------------------- GSM tab

    private void DrawPublishTab()
    {
        ImGui.TextDisabled("Sends game text to GameSentenceMiner over a local WebSocket. Nothing leaves this PC.");

        if (Section("gsm-sources", "Feed Sources", defaultOpen: true))
        {
            Checkbox("Publish text for GSM", () => config.PublishEnabled, v => config.PublishEnabled = v);
            using var disabled = ImRaii.Disabled(!config.PublishEnabled);

            var preset = Array.FindIndex(PublishPresets, p => p.Talk == config.PublishTalk && p.Battle == config.PublishBattleTalk && p.Popups == config.PublishPopups && p.Chat == config.PublishChat);
            var custom = preset < 0 || customPublishSources;
            var presetNames = PublishPresets.Select(p => p.Name).Append("Custom").ToArray();
            var selected = custom ? PublishPresets.Length : preset;
            if (ImGui.Combo("What to send", ref selected, presetNames, presetNames.Length))
            {
                customPublishSources = selected >= PublishPresets.Length;
                if (!customPublishSources)
                {
                    var p = PublishPresets[selected];
                    config.PublishTalk = p.Talk;
                    config.PublishBattleTalk = p.Battle;
                    config.PublishPopups = p.Popups;
                    config.PublishChat = p.Chat;
                    Changed(true);
                }
            }

            // Individual sources only matter for a custom mix.
            if (Advanced || custom)
            using (ImRaii.PushIndent())
            {
                Checkbox("Dialogue and cutscene subtitles", () => config.PublishTalk, v => config.PublishTalk = v);
                Checkbox("Battle talk", () => config.PublishBattleTalk, v => config.PublishBattleTalk = v);
                Checkbox("Interaction popups (yes/no prompts, choice menus)", () => config.PublishPopups, v => config.PublishPopups = v);
                Checkbox("Chat (channels from the Reader & Text tab)", () => config.PublishChat, v => config.PublishChat = v);
                if (config.PublishChat)
                    ImGui.TextColored(AccentAmber, "Sends other players' messages to GSM.");
            }

            Checkbox("Put the speaker's name before each line", () => config.PublishSpeaker, v => config.PublishSpeaker = v);

            if ((config.PublishTalk && !config.CaptureTalk) || (config.PublishBattleTalk && !config.CaptureBattleTalk) || (config.PublishChat && !config.CaptureChat))
                ImGui.TextColored(AccentAmber, "A selected source is turned off in the Reader & Text tab, so it won't be sent.");
        }

        if (Section("gsm-server", "WebSocket Server", defaultOpen: true))
        {
            using var disabled = ImRaii.Disabled(!config.PublishEnabled);
            var portIndex = Array.FindIndex(PublishPorts, p => p.Port == config.PublishPort);
            var portNames = PublishPorts.Select(p => $"{p.Name} ({p.Port})").Append("Custom").ToArray();
            var customPort = portIndex < 0 || customPublishPort;
            var portSelected = customPort ? PublishPorts.Length : portIndex;
            if (ImGui.Combo("GSM source slot", ref portSelected, portNames, portNames.Length))
            {
                customPublishPort = portSelected >= PublishPorts.Length;
                if (!customPublishPort)
                {
                    config.PublishPort = PublishPorts[portSelected].Port;
                    Changed(true);
                }
            }
            HelpMarker("GSM listens on all three slots by default; the name is only a label. Pick one no other program uses.");

            if (Advanced || customPort)
                DrawPublishPortControl();
            ImGui.TextDisabled($"GSM address: localhost:{config.PublishPort}");

            if (publisher.Error is { } error)
                ImGui.TextColored(AccentRed, error);
            else if (publisher.Listening)
                ImGui.TextUnformatted(publisher.ClientCount == 0 ? "Listening. Waiting for GSM to connect…" : $"Connected clients: {publisher.ClientCount}");

            using (ImRaii.Disabled(publisher.ClientCount == 0))
            {
                if (ImGui.Button("Send test line"))
                    publisher.SendTest();
            }
        }
    }

    // -------------------------------------------------------------- Connection tab

    private void DrawConnectionTab()
    {
        if (Section("conn-relay", "Hachidori Relay & Host", defaultOpen: true))
        {
            if (ImGui.Button("Reconnect"))
                service.Link.Reconnect();

            if (Advanced)
            {
                ImGui.Spacing();
                DrawRelayHostControl();
                DrawRelayPortControl();
                DrawApiPortControl();
                DrawAnkiTemplateIdControl();
                DrawDocumentTitleControl();
            }
        }

        if (service.Link.Connected)
        {
            var dicts = service.Dictionaries.ToList();
            if (Section("conn-dicts", $"Host Dictionaries ({dicts.Count})", defaultOpen: false))
            {
                foreach (var d in dicts)
                {
                    var enabled = d["enabled"] is not System.Text.Json.Nodes.JsonValue e || !e.TryGetValue<bool>(out var en) || en;
                    ImGui.BulletText($"{LookupResult.Str(d["title"])}{(enabled ? string.Empty : " (disabled)")}");
                }
            }
        }

        var installedFfxivDict = FindInstalledFfxivDictionary();
        var dictConnTitle = installedFfxivDict is { Enabled: true }
            ? "Custom FFXIV Dictionary  [Installed]"
            : installedFfxivDict is not null
                ? "Custom FFXIV Dictionary  [Disabled]"
                : "Custom FFXIV Dictionary";
        var dictConnAccent = installedFfxivDict is { Enabled: true }
            ? AccentGreen
            : installedFfxivDict is not null
                ? AccentAmber
                : (Vector4?)null;
        if (Section("conn-ffxiv-dict", dictConnTitle, defaultOpen: installedFfxivDict is null, accent: dictConnAccent))
        {
            DrawFfxivDictionaryBody(ImGuiHelpers.GlobalScale, installedFfxivDict, "conn");
        }

        if (offline is { } off && Section("conn-offline", config.OfflineMode ? "Offline Mode  [Enabled]" : "Offline Mode", defaultOpen: true, accent: config.OfflineMode ? AccentGreen : null))
        {
            DrawOfflineModeBody(off);
        }

        if (Advanced && Section("conn-diag", "Diagnostics", defaultOpen: false))
        {
            if (ImGui.Button("Export debug log"))
            {
                try { exportedLog = exportLogs(); }
                catch (Exception ex) { exportedLog = $"Export failed: {ex.Message}"; }
            }
            if (exportedLog is not null)
            {
                ImGui.TextWrapped(exportedLog);
                if (ImGui.Button("Copy log path"))
                    ImGui.SetClipboardText(exportedLog);
            }
        }
    }

    private InstalledFfxivDictionary? FindInstalledFfxivDictionary()
    {
        var live = FfxivDictionary.FindInstalled(service.Dictionaries);
        if (live is not null)
            return live;
        if (offline is { } off)
            return FfxivDictionary.FindInstalledOffline(off.Store.Manifest.Dictionaries.Select(d => (d.Title, d.Revision)));
        return null;
    }

    private void DrawFfxivDictionaryBody(float scale, InstalledFfxivDictionary? installed, string scopeId)
    {
        if (installed is { Enabled: true })
        {
            var rev = string.IsNullOrWhiteSpace(installed.Revision) ? string.Empty : $" (rev {installed.Revision})";
            ImGui.TextColored(AccentGreen, $"Installed: {installed.Title}{rev}");
            ImGui.TextDisabled("To update, remove the older entry in Hachidori → Settings → Dictionaries, then import the latest ZIP.");
        }
        else if (installed is not null)
        {
            var rev = string.IsNullOrWhiteSpace(installed.Revision) ? string.Empty : $" (rev {installed.Revision})";
            ImGui.TextColored(AccentAmber, $"Installed but disabled: {installed.Title}{rev}");
            ImGui.TextDisabled("Enable it in Hachidori → Settings → Dictionaries to see FFXIV definitions.");
        }
        else
        {
            ImGui.TextWrapped("37,729 FFXIV Japanese→English entries: NPCs & 190 character/location portraits, enemies, locations, actions, status effects, and JP Party Finder lingo. Import the downloaded ZIP in Hachidori → Settings → Dictionaries.");
            ImGui.TextDisabled("Note: contains character names and portraits from all expansions (spoilers).");
        }

        ImGui.Spacing();
        var downloadLabel = installed is not null
            ? $"Download latest ({FfxivDictionary.ArchiveFileName})##ffxiv-dict-dl-{scopeId}"
            : $"Download FFXIV Dictionary (.zip)##ffxiv-dict-dl-{scopeId}";
        if (ImGui.Button(downloadLabel))
            OpenDictionaryLink(FfxivDictionary.DownloadUrl, "Opened download in your browser — import the ZIP in Hachidori → Settings → Dictionaries.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(FfxivDictionary.DownloadUrl);

        ImGui.SameLine(0, 6 * scale);
        if (ImGui.Button($"Guide & source##ffxiv-dict-guide-{scopeId}"))
            OpenDictionaryLink(FfxivDictionary.GuideUrl, "Opened FFXIV dictionary guide and source code in your browser.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(FfxivDictionary.GuideUrl);

        ImGui.SameLine(0, 6 * scale);
        if (ImGui.Button($"Copy link##ffxiv-dict-copy-{scopeId}"))
        {
            ImGui.SetClipboardText(FfxivDictionary.DownloadUrl);
            SetDictionaryNotice("Copied download link to clipboard.", isError: false);
        }

        if (dictionaryNotice is not null && DateTime.UtcNow < dictionaryNoticeUntil)
            ImGui.TextColored(dictionaryNoticeIsError ? AccentAmber : MutedText, dictionaryNotice);
        else if (dictionaryNotice is not null)
            dictionaryNotice = null;
    }

    private void OpenDictionaryLink(string url, string successMessage)
    {
        if (FfxivDictionary.TryOpenUrl(url, out var error))
        {
            SetDictionaryNotice(successMessage, isError: false);
        }
        else
        {
            ImGui.SetClipboardText(url);
            SetDictionaryNotice($"Could not open browser ({error ?? "unknown error"}). Copied link to clipboard instead.", isError: true);
        }
    }

    private void SetDictionaryNotice(string message, bool isError)
    {
        dictionaryNotice = message;
        dictionaryNoticeIsError = isError;
        dictionaryNoticeUntil = DateTime.UtcNow.AddSeconds(6);
    }

    private void DrawOfflineModeBody(Offline.OfflineMode offline)
    {
        ImGui.TextWrapped("Keeps a copy of your Hachidori dictionaries and settings so lookups work with only Anki (and the Hachidori Relay) running. Live Hachidori is always used while linked; the copy updates on every connection.");

        var link = service.Link;
        var running = offline.Sync.Running;
        if (!config.OfflineMode)
        {
            using (ImRaii.Disabled(!link.Connected || running))
            {
                if (ImGui.Button("Set up offline mode"))
                    _ = offline.SetUpAsync();
            }
            if (!link.Connected)
                ImGui.TextDisabled("Link Hachidori (Chrome with Sharing on) to set up offline mode.");
        }
        else
        {
            ImGui.TextUnformatted($"Answering lookups: {service.Backend.Name}");
            var manifest = offline.Store.Manifest;
            ImGui.TextUnformatted($"{manifest.Dictionaries.Count} dictionaries, {FormatBytes(offline.Store.DiskBytes)} on disk, synced {Ago(manifest.SyncedAt)}.");
            if (offline.EngineVersion is { } engine)
                ImGui.TextDisabled(engine.Short);

            using (ImRaii.Disabled(!link.Connected || running))
            {
                if (ImGui.Button("Sync now"))
                    _ = offline.SyncNowAsync();
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(running))
            {
                if (ImGui.Button(confirmRemoveOffline ? "Click again to delete the offline copy" : "Turn off and delete offline data"))
                {
                    if (confirmRemoveOffline)
                    {
                        confirmRemoveOffline = false;
                        _ = offline.RemoveAsync().ContinueWith(_ => service.ClearCache(), TaskScheduler.Default);
                    }
                    else
                    {
                        confirmRemoveOffline = true;
                    }
                }
                else if (confirmRemoveOffline && !ImGui.IsItemHovered())
                {
                    confirmRemoveOffline = false;
                }
            }

            if (manifest.Dictionaries.Count > 0 && ImGui.TreeNode("Offline dictionaries"))
            {
                foreach (var d in manifest.Dictionaries)
                    ImGui.BulletText($"{d.Title} ({d.Revision}, {FormatBytes(d.Bytes)})");
                ImGui.TreePop();
            }
        }

        if (running)
            ImGui.TextWrapped(offline.Sync.Progress ?? "Syncing…");
        else if (offline.LastSync is { } last)
            ImGui.TextDisabled($"Last sync: {last}.");
        if (offline.LastSyncError is { } error)
            ImGui.TextColored(AccentRed, error);
        foreach (var warning in offline.Warnings())
        {
            ImGui.TextColored(AccentAmber, "!");
            ImGui.SameLine();
            ImGui.TextWrapped(warning);
        }
    }
}
