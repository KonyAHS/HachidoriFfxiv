using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Media;
using HachidoriFfxiv.Offline;

namespace HachidoriFfxiv.Ui;

public sealed partial class ConfigWindow
{
    // -------------------------------------------------------------- Popup tab

    private void DrawPopupTab()
    {
        var scale = ImGuiHelpers.GlobalScale;

        using (var card = ConfigStyle.Card("popup-scan", "Hover & Scanning", ConfigStyle.Primary,
            badge: config.NativeTextHover ? "ON" : "OFF",
            badgeColor: config.NativeTextHover ? ConfigStyle.Primary : ConfigStyle.Muted))
        {
            if (card.IsOpen)
            {
                Checkbox("Hover game text", () => config.NativeTextHover, v => config.NativeTextHover = v);
                ImGui.SameLine(0, 18 * scale);
                Checkbox("Include chat", () => config.NativeChatHover, v => config.NativeChatHover = v);
                DrawScanModifierControl();
                DrawFreezeHotkeyControl();

                if (Advanced)
                {
                    AdvancedDivider();
                    Checkbox("Whole-word hover (tokenizer)", () => config.TokenizedHover, v => config.TokenizedHover = v);
                    ImGui.SameLine(0, 14 * scale);
                    Checkbox("Other native windows (exp.)", () => config.NativeOtherTextHover, v => config.NativeOtherTextHover = v);
                    Checkbox("Japanese characters only", () => config.ScanOnlyJapanese, v => config.ScanOnlyJapanese = v);
                    ImGui.SameLine(0, 14 * scale);
                    Checkbox("Personal dictionary", () => config.UsePersonalDictionary, v => config.UsePersonalDictionary = v);
                    DrawScanLengthControl();
                    DrawMaxResultsControl();
                    DrawMaxPopupDepthControl();
                    Checkbox("Show hover target boxes (debug)", () => config.NativeHoverDebug, v => config.NativeHoverDebug = v);
                    if (config.NativeHoverDebug)
                    {
                        ImGui.TextDisabled($"Targets under mouse: {nativeHover.RegionCount}");
                        if (nativeHover.LastError is { } error) ImGui.TextWrapped(error);
                    }
                }
            }
        }

        var rendererBadge = webView is { Active: true } ? "WEBVIEW2" : "BUILT-IN";
        using (var card = ConfigStyle.Card("popup-look", "Look, Renderer & Size", ConfigStyle.Secondary,
            badge: rendererBadge, badgeColor: ConfigStyle.Secondary))
        {
            if (card.IsOpen)
            {
                DrawPopupRendererControl();
                if (config.FollowHachidoriTheme)
                {
                    DrawThemeOverrideControl();
                    DrawVariantOverrideControl();
                }
                DrawRendererStatus(scale);

                ImGui.Spacing();
                DrawPopupWidthControl();
                DrawPopupMaxHeightControl();
                DrawPopupFontSizeControl();
                if (!config.FollowHachidoriTheme || !config.FollowHachidoriOpacity)
                    DrawPopupOpacityControl();

                if (Advanced)
                {
                    AdvancedDivider();
                    DrawHideDelayControl();
                    Checkbox("Hide popup when mouse leaves", () => config.AutoHideRootPopup, v => config.AutoHideRootPopup = v);
                    if (config.FollowHachidoriTheme)
                    {
                        DrawFollowOpacityControl();
                        DrawApplyCustomCssControl();
                        DrawAutoSyncWebViewControl();
                        DrawWebViewBundlePathControl();
                        DrawExtraCssControl();
                    }
                }
            }
        }

        using (var card = ConfigStyle.Card("popup-contents", "Contents & Audio", ConfigStyle.Primary))
        {
            if (card.IsOpen)
            {
                using (var tbl = ImRaii.Table("##popup-contents-grid", 2, ImGuiTableFlags.SizingStretchSame))
                {
                    if (tbl.Success)
                    {
                        ImGui.TableNextColumn();
                        Checkbox("Frequencies", () => config.ShowFrequencies, v => config.ShowFrequencies = v);
                        Checkbox("Dictionary names", () => config.ShowDictionaryNames, v => config.ShowDictionaryNames = v);
                        ImGui.TableNextColumn();
                        Checkbox("Pitch accent", () => config.ShowPitchAccent, v => config.ShowPitchAccent = v);
                        Checkbox("Auto-play audio", () => config.AutoPlayAudio, v => config.AutoPlayAudio = v);
                    }
                }
                DrawAudioVolumeControl();

                if (Advanced)
                {
                    AdvancedDivider();
                    using var tbl = ImRaii.Table("##popup-adv-grid", 2, ImGuiTableFlags.SizingStretchSame);
                    if (tbl.Success)
                    {
                        ImGui.TableNextColumn();
                        Checkbox("Deinflection trace", () => config.ShowInflections, v => config.ShowInflections = v);
                        Checkbox("Definition tags", () => config.ShowDefinitionTags, v => config.ShowDefinitionTags = v);
                        Checkbox("Attribution", () => config.ShowAttribution, v => config.ShowAttribution = v);
                        ImGui.TableNextColumn();
                        Checkbox("Lookup counts", () => config.ShowLookupCounts, v => config.ShowLookupCounts = v);
                        Checkbox("Honour lookup blur", () => config.HonourDefinitionBlur, v => config.HonourDefinitionBlur = v);
                    }
                }
            }
        }
    }

    private void DrawRendererStatus(float scale)
    {
        if (webView is { PlatformSupported: true } && config.UseWebViewPopup && config.FollowHachidoriTheme)
        {
            if (webView.Active)
                ImGui.TextColored(ConfigStyle.Muted, $"Hachidori Web v{webView.LoadedVersion} active.");
            else if (!webView.RuntimeAvailable)
                ImGui.TextColored(ConfigStyle.Emphasis, "WebView2 Runtime missing — using built-in renderer.");
            else if (webView.Bundles.Syncing)
                ImGui.TextColored(ConfigStyle.Primary, "Syncing Hachidori Web bundle…");
            else
                ImGui.TextColored(ConfigStyle.Emphasis, $"Built-in fallback ({webView.StatusText}).");

            if (webView.LastError is { Length: > 0 } wvError)
                ImGui.TextColored(ConfigStyle.Bad, wvError);

            if (Advanced || !webView.Active)
            {
                using (ImRaii.Disabled(webView.Bundles.Syncing))
                {
                    if (ImGui.SmallButton("Sync Web UI"))
                        _ = webView.SyncBundleNowAsync(force: true);
                }
            }
        }
        else if (webView is not { Active: true } && themes.Error is { } themeError)
        {
            ImGui.TextColored(ConfigStyle.Bad, $"Theme error: {themeError}");
        }
    }

    // -------------------------------------------------------------- Mining tab

    private void DrawMiningTab()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var source = ActiveSource;
        var keepGsm = config.SetupStyle == SetupStyle.KeepGsm;

        // If user picked "Keep GSM", surface GSM Bridge first and collapse duplicate built-in capture by default.
        if (keepGsm)
            DrawGsmCard(scale, defaultOpen: true);

        using (var card = ConfigStyle.Card("mining-media", "Card Media", ConfigStyle.Primary,
            subtitle: "What gets attached when you mine a word from a popup.",
            defaultOpen: !keepGsm))
        {
            if (card.IsOpen)
            {
                DrawVisualCaptureControl();
                if (media.Frames.Error is { } frameError)
                    ImGui.TextColored(ConfigStyle.Emphasis, frameError);

                if (config.CaptureGif)
                {
                    using var indent = ImRaii.PushIndent();
                    var keepStill = !config.GifReplacesScreenshot;
                    if (ImGui.Checkbox("Also keep still screenshot (uses separate GIF field)", ref keepStill))
                    {
                        config.GifReplacesScreenshot = !keepStill;
                        config.Save();
                    }

                    if (config.AnimationFormat != AnimationFormat.Gif)
                    {
                        var ffmpeg = media.FfmpegPath;
                        var caps = ffmpeg is null ? default : Ffmpeg.Capabilities(ffmpeg);
                        var ready = config.AnimationFormat == AnimationFormat.Avif ? caps.Av1 is not null : caps.Webp;
                        if (!ready)
                        {
                            ImGui.TextColored(ConfigStyle.Emphasis, ffmpeg is null
                                ? "FFmpeg not found — saving GIF until installed."
                                : "Installed FFmpeg lacks AV1/WebP support — falling back.");
                            if (ffmpeg is null && !PlatformInfo.Current.IsWine)
                            {
                                ImGui.SameLine(0, 8 * scale);
                                using (ImRaii.Disabled(ffmpegDownloading))
                                {
                                    if (ConfigStyle.AccentButton(ffmpegDownloading ? "Downloading…" : "Download FFmpeg", ConfigStyle.Emphasis))
                                        StartManagedFfmpegDownload();
                                }
                            }
                        }
                        else if (Advanced)
                        {
                            ImGui.TextDisabled($"FFmpeg: {ffmpeg}");
                        }
                    }
                }

                ImGui.Spacing();
                DrawSentenceAudioControl();
                if (config.RecordVoiceLines && media.Recorder is { } recorder)
                {
                    if (recorder.Error is { } audioError)
                        ImGui.TextColored(ConfigStyle.Emphasis, audioError);
                    else if (recorder.Running && recorder.Mode == GameAudioCaptureMode.SystemLoopback)
                        ImGui.TextColored(ConfigStyle.Emphasis, "Capturing system audio (lower other apps for clean clips).");
                    else if (recorder.Running && !media.Voices.HooksActive)
                        ImGui.TextColored(ConfigStyle.Emphasis, $"Voice detection fallback: {media.Voices.DetectionModeSummary}.");
                }

                if (config.TtsMode != TtsMode.Off)
                {
                    using var indent = ImRaii.PushIndent();
                    DrawTtsUrlControl();
                    DrawTtsSpeakerControl();
                    if (config.TtsMode == TtsMode.Custom)
                        Toggle("POST JSON {text, speaker}", config.TtsPost, v => config.TtsPost = v);
                    DrawTtsInjectOstControl();
                    if (config.TtsInjectOst)
                    {
                        DrawTtsOstVolumeControl();
                        if (Advanced)
                        {
                            DrawTtsOstRespectMuteControl();
                            DrawCurrentOstStatus();
                        }
                    }
                    if (ImGui.SmallButton("Test TTS"))
                    {
                        mediaTest = "Speaking…";
                        _ = TestTtsAsync();
                    }
                }

                ImGui.Spacing();
                DrawAnkiNotifyToggleControl();

                if (Advanced)
                {
                    AdvancedDivider();
                    if (config.CaptureScreenshot)
                    {
                        DrawScreenshotMaxWidthControl();
                        DrawScreenshotQualityControl();
                    }
                    if (config.CaptureGif)
                    {
                        DrawAvifCrfControl();
                        DrawWebpQualityControl();
                        DrawFfmpegPathControl();
                    }
                    if (config.RecordVoiceLines)
                    {
                        DrawVoiceRatioControl();
                        DrawMaxLineSecondsControl();
                    }
                }
            }
        }

        using (var card = ConfigStyle.Card("mining-anki-map", "Anki Card Mapping", ConfigStyle.Secondary,
            badge: ankiMapper.Connected ? "READY" : "OFFLINE",
            badgeColor: ankiMapper.Connected ? ConfigStyle.Good : ConfigStyle.Emphasis,
            defaultOpen: !keepGsm))
        {
            if (card.IsOpen)
            {
                ankiMapper.Draw(Advanced, source);
                if (Advanced)
                {
                    AdvancedDivider();
                    DrawAnkiConnectUrlControl();
                    DrawAnkiConnectApiKeyControl();
                }
            }
        }

        using (var card = ConfigStyle.Card("mining-translation", "AI Sentence Translation", ConfigStyle.Secondary,
            badge: config.TranslationEnabled ? "ON" : null,
            badgeColor: ConfigStyle.Emphasis,
            defaultOpen: config.TranslationEnabled))
        {
            if (card.IsOpen)
                DrawTranslationSection();
        }

        if (!keepGsm)
            DrawGsmCard(scale, defaultOpen: config.PublishEnabled);

        if (mediaTest is not null)
        {
            ImGui.Spacing();
            ImGui.TextColored(mediaTest.Contains("failed", StringComparison.OrdinalIgnoreCase) ? ConfigStyle.Bad : ConfigStyle.Good, mediaTest);
        }
    }

    private void DrawGsmCard(float scale, bool defaultOpen)
    {
        using var card = ConfigStyle.Card("mining-gsm", "GameSentenceMiner (GSM)", ConfigStyle.Secondary,
            badge: config.PublishEnabled ? "FEED ON" : null,
            badgeColor: ConfigStyle.Primary,
            defaultOpen: defaultOpen);
        if (!card.IsOpen)
            return;

        if (ConfigStyle.AccentButton("Import Settings from GSM", ConfigStyle.Primary))
        {
            try
            {
                mediaTest = media.Translator.ImportFromGsm();
                media.RefreshFfmpegPath();
                config.Save();
            }
            catch (Exception ex) { mediaTest = $"Import failed: {ex.Message}"; }
        }
        HelpMarker("Copies AI provider, keys, prompts, Anki media fields, AnkiConnect URL, and FFmpeg path from GameSentenceMiner.");

        ImGui.Spacing();
        DrawGsmBridgeSection(scale);
    }

    private void DrawTranslationSection()
    {
        Toggle("Enable AI sentence translation", config.TranslationEnabled, v => config.TranslationEnabled = v);
        if (!config.TranslationEnabled)
            return;

        using var indent = ImRaii.PushIndent();
        DrawTranslationProviderControl();
        var settings = media.Translator.Settings(config.TranslationProvider);
        var cli = config.TranslationProvider is TranslationProvider.Codex or TranslationProvider.ClaudeCode;

        if (cli)
        {
            ImGui.TextColored(ConfigStyle.Muted, config.TranslationProvider == TranslationProvider.Codex
                ? "Uses your local `codex exec` login (read-only)."
                : "Uses your local `claude -p` login (no tools).");
        }
        else if (config.TranslationProvider is not (TranslationProvider.Ollama or TranslationProvider.LmStudio))
        {
            var key = settings.ApiKey;
            if (ImGui.InputText("API key", ref key, 512, ImGuiInputTextFlags.Password))
                settings.ApiKey = key.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit()) config.Save();
        }

        if (config.TranslationProvider != TranslationProvider.DeepL)
        {
            var model = settings.Model;
            if (ImGui.InputTextWithHint("Model", cli ? "blank: CLI default" : "", ref model, 256))
                settings.Model = model.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit()) config.Save();
        }

        if (ImGui.SmallButton("Test translation"))
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
            if (ImGui.InputText("Backup model", ref backup, 256)) settings.BackupModel = backup.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit()) config.Save();
        }
        if (!cli)
        {
            var url = settings.Url;
            if (ImGui.InputTextWithHint("API address", Translator.Defaults[config.TranslationProvider].Url, ref url, 512)) settings.Url = url.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit()) config.Save();
        }
        if (config.TranslationProvider == TranslationProvider.OpenAI)
        {
            var effort = settings.ReasoningEffort;
            if (ImGui.InputTextWithHint("Reasoning effort", "blank, low, medium, high", ref effort, 32)) settings.ReasoningEffort = effort.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit()) config.Save();
        }
        if (config.TranslationProvider == TranslationProvider.DeepL)
        {
            var target = config.DeepLTargetLanguage;
            if (ImGui.InputText("DeepL target language", ref target, 16)) config.DeepLTargetLanguage = target.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit()) config.Save();
            return;
        }
        if (cli)
        {
            var path = config.TranslationProvider == TranslationProvider.Codex ? config.CodexPath : config.ClaudeCodePath;
            if (ImGui.InputTextWithHint("CLI path", "blank: auto-detect", ref path, 512))
            {
                if (config.TranslationProvider == TranslationProvider.Codex) config.CodexPath = path.Trim();
                else config.ClaudeCodePath = path.Trim();
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) config.Save();
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
            if (ImGui.InputTextMultiline("##custom-prompt", ref custom, 8192, new Vector2(-1, 70 * ImGuiHelpers.GlobalScale)))
                config.CustomTranslationPrompt = custom;
            if (ImGui.IsItemDeactivatedAfterEdit()) config.Save();
        }
        DrawDialogueContextControl();
        if (!cli)
        {
            DrawTemperatureControl();
            DrawTopPControl();
            DrawMaxTokensControl();
        }
    }

    private void DrawGsmBridgeSection(float scale)
    {
        Checkbox("Publish game text to GSM (WebSocket feed)", () => config.PublishEnabled, v => config.PublishEnabled = v);
        HelpMarker("Sends live FFXIV lines over localhost WebSocket so you can keep using GameSentenceMiner alongside native hover.");
        if (!config.PublishEnabled)
            return;

        using var indent = ImRaii.PushIndent();
        var preset = Array.FindIndex(PublishPresets, p => p.Talk == config.PublishTalk && p.Battle == config.PublishBattleTalk && p.Popups == config.PublishPopups && p.Chat == config.PublishChat);
        var custom = preset < 0 || customPublishSources;
        var presetNames = PublishPresets.Select(p => p.Name).Append("Custom").ToArray();
        var selected = custom ? PublishPresets.Length : preset;
        ImGui.SetNextItemWidth(240 * scale);
        if (ImGui.Combo("Feed scope", ref selected, presetNames, presetNames.Length))
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
        if (Advanced || custom)
        {
            Checkbox("Dialogue & subtitles", () => config.PublishTalk, v => config.PublishTalk = v);
            ImGui.SameLine(0, 12 * scale);
            Checkbox("Battle talk", () => config.PublishBattleTalk, v => config.PublishBattleTalk = v);
            Checkbox("Popups & choices", () => config.PublishPopups, v => config.PublishPopups = v);
            ImGui.SameLine(0, 12 * scale);
            Checkbox("Chat", () => config.PublishChat, v => config.PublishChat = v);
        }
        Checkbox("Prepend speaker name", () => config.PublishSpeaker, v => config.PublishSpeaker = v);

        var portIndex = Array.FindIndex(PublishPorts, p => p.Port == config.PublishPort);
        var portNames = PublishPorts.Select(p => $"{p.Name} ({p.Port})").Append("Custom").ToArray();
        var customPort = portIndex < 0 || customPublishPort;
        var portSelected = customPort ? PublishPorts.Length : portIndex;
        ImGui.SetNextItemWidth(200 * scale);
        if (ImGui.Combo("WebSocket slot", ref portSelected, portNames, portNames.Length))
        {
            customPublishPort = portSelected >= PublishPorts.Length;
            if (!customPort)
            {
                config.PublishPort = PublishPorts[portSelected].Port;
                Changed(true);
            }
        }
        if (Advanced || customPort)
            DrawPublishPortControl();

        if (publisher.Error is { } error)
            ImGui.TextColored(ConfigStyle.Bad, error);
        else if (publisher.Listening)
            ImGui.TextColored(publisher.ClientCount > 0 ? ConfigStyle.Good : ConfigStyle.Muted,
                publisher.ClientCount == 0 ? $"Listening on localhost:{config.PublishPort} — waiting for GSM…" : $"GSM connected ({publisher.ClientCount} client)");

        using (ImRaii.Disabled(publisher.ClientCount == 0))
        {
            ImGui.SameLine(0, 10 * scale);
            if (ImGui.SmallButton("Send test line"))
                publisher.SendTest();
        }
    }

    // -------------------------------------------------------------- Performance tab

    private void DrawPerformanceTab()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var detected = PerformancePresets.Detect(config);
        var est = PerformancePresets.Estimate(config, webView?.PlatformSupported ?? false);

        // 1. Premade Presets (auto-sized height so zero lines clip)
        using (var card = ConfigStyle.Card("perf-presets", "Performance Presets", ConfigStyle.Primary,
            badge: detected.ToString().ToUpperInvariant(),
            badgeColor: detected == PerformancePreset.Custom ? ConfigStyle.Emphasis : ConfigStyle.Primary,
            subtitle: "Choose a baseline for your hardware. Turn on Show advanced to fine-tune individual cache sizes."))
        {
            if (card.IsOpen)
            {
                var avail = Math.Max(300f * scale, ImGui.GetContentRegionAvail().X - 12f * scale);
                var gap = 8f * scale;
                var tileW = (avail - gap * 2f) / 3f;
                const string lowSub = "Built-in UI · No anim.\nSmall caches (~25 MB).";
                const string balSub = "On-demand clips.\nBalanced caches.";
                const string highSub = "Pre-encoded clips.\nLarge caches · Fast mine.";
                var rowH = ConfigStyle.MeasurePresetRowHeight(tileW, lowSub, balSub, highSub);

                if (ConfigStyle.PresetTile("##perf-low", "Low / Steam Deck", lowSub,
                    detected == PerformancePreset.Low, tileW, rowH))
                {
                    PerformancePresets.Apply(config, PerformancePreset.Low);
                    media.ApplyPerformanceSettings();
                    Changed(true);
                    resetNotice = "Applied Low / Steam Deck performance preset.";
                    resetNoticeUntil = DateTime.UtcNow.AddSeconds(4);
                }
                ImGui.SameLine(0, gap);
                if (ConfigStyle.PresetTile("##perf-bal", "Balanced", balSub,
                    detected == PerformancePreset.Balanced, tileW, rowH))
                {
                    PerformancePresets.Apply(config, PerformancePreset.Balanced);
                    media.ApplyPerformanceSettings();
                    Changed(true);
                    resetNotice = "Applied Balanced performance preset.";
                    resetNoticeUntil = DateTime.UtcNow.AddSeconds(4);
                }
                ImGui.SameLine(0, gap);
                if (ConfigStyle.PresetTile("##perf-high", "High / Instant", highSub,
                    detected == PerformancePreset.High, tileW, rowH))
                {
                    PerformancePresets.Apply(config, PerformancePreset.High);
                    media.ApplyPerformanceSettings();
                    Changed(true);
                    resetNotice = "Applied High / Instant performance preset.";
                    resetNoticeUntil = DateTime.UtcNow.AddSeconds(4);
                }
            }
        }

        // 2. Live Memory Footprint & Clear Caches
        var (shots, shotBytes, replays, replayBytes) = media.Timeline.CacheStats();
        var (clips, clipBytes) = media.ClipCacheStats();
        using (var card = ConfigStyle.Card("perf-live", "Footprint & Live Caches", ConfigStyle.Secondary,
            badge: $"~{est.TotalMb} MB EST."))
        {
            if (card.IsOpen)
            {
                using (var tbl = ImRaii.Table("##perf-summary", 3, ImGuiTableFlags.SizingStretchSame))
                {
                    if (tbl.Success)
                    {
                        ImGui.TableNextColumn();
                        ImGui.TextColored(ConfigStyle.Muted, "In-Game Buffers");
                        ImGui.TextColored(ConfigStyle.Primary, $"~{est.PluginProcessMb} MB");
                        ImGui.TextDisabled($"Voice {est.VoiceBufferMb} · Anim {est.AnimationBufferMb} · Caches {est.LineCachesMb + est.LookupCachesMb} MB");

                        ImGui.TableNextColumn();
                        ImGui.TextColored(ConfigStyle.Muted, "WebView2 Process");
                        ImGui.TextColored(est.WebViewMb > 0 ? ConfigStyle.Secondary : ConfigStyle.Good, est.WebViewMb > 0 ? $"~{est.WebViewMb} MB" : "Off (0 MB)");
                        ImGui.TextDisabled(est.WebViewMb > 0 ? "Separate OS process" : "Using built-in ImGui");

                        ImGui.TableNextColumn();
                        ImGui.TextColored(ConfigStyle.Muted, "Live Cached Items");
                        ImGui.TextUnformatted($"{service.LookupCacheCount} lookups · {service.AudioCacheCount} audio");
                        ImGui.TextDisabled($"{shots} shots ({FormatBytes(shotBytes)}) · {clips} clips ({FormatBytes(clipBytes)}) · {replays} voices ({FormatBytes(replayBytes)})");
                    }
                }

                ImGui.Spacing();
                if (ConfigStyle.AccentButton("Clear All Caches Now", ConfigStyle.Emphasis))
                {
                    service.ClearCache();
                    media.ClearCaches();
                    resetNotice = "Cleared lookup, audio, screenshot, voice replay, and clip caches.";
                    resetNoticeUntil = DateTime.UtcNow.AddSeconds(4);
                }
            }
        }

        // 3. Concise everyday caching & rendering choices (Normal mode)
        using (var card = ConfigStyle.Card("perf-quick", "Quick Performance Controls", ConfigStyle.Primary))
        {
            if (card.IsOpen)
            {
                DrawPopupRendererControl();
                DrawClipEncodingModeControl();
                ImGui.Spacing();
                Toggle("Cache line screenshots", config.CacheLineScreenshots, v => { config.CacheLineScreenshots = v; media.ApplyPerformanceSettings(); });
                ImGui.SameLine(0, 18 * scale);
                Toggle("Cache voice replays", config.CacheReplayAudio, v => { config.CacheReplayAudio = v; media.ApplyPerformanceSettings(); });
            }
        }

        // 4. Granular cache limits & buffer tuning (Advanced only — keeps normal view super clean!)
        if (Advanced)
        {
            using var card = ConfigStyle.Card("perf-adv-caches", "Cache Sizes & Buffer Limits (Advanced)", ConfigStyle.Emphasis,
                subtitle: "Set any cache size to 0 to disable that cache.");
            if (card.IsOpen)
            {
                ConfigStyle.SubHeading("Dictionary & Pronunciation Caches", ConfigStyle.Primary);
                DrawLookupCacheSizeControl();
                DrawSegmentCacheSizeControl();
                DrawAudioCacheSizeControl();

                ConfigStyle.SubHeading("Dialogue, Voice & Clip Buffers", ConfigStyle.Secondary);
                if (config.CacheLineScreenshots)
                    DrawMaxCachedScreenshotsOnlyControl();
                if (config.CacheReplayAudio)
                    DrawMaxCachedReplayAudioOnlyControl();
                DrawMaxCachedClipsControl();
                DrawMaxConcurrentEncodesControl();
                DrawVoiceBufferSecondsControl();
                DrawHistorySizeControl();

                if (config.CaptureGif)
                {
                    ConfigStyle.SubHeading("Animation Dimensions", ConfigStyle.Secondary);
                    DrawGifWidthControl();
                    DrawGifFpsControl();
                    DrawGifSecondsControl();
                }
            }
        }
    }

    // -------------------------------------------------------------- Controller tab

    private void DrawControllerTab()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var ps = config.ControllerPlayStationLabels;

        using (var card = ConfigStyle.Card("ctrl-activation", "Controller Reading Mode", ConfigStyle.Primary,
            badge: config.ControllerEnabled ? "ON" : "OFF",
            badgeColor: config.ControllerEnabled ? ConfigStyle.Primary : ConfigStyle.Muted))
        {
            if (card.IsOpen)
            {
                Toggle("Enable controller reading mode", config.ControllerEnabled, v => config.ControllerEnabled = v);
                if (config.ControllerEnabled)
                {
                    ImGui.SameLine(0, 14 * scale);
                    ImGui.TextColored(ConfigStyle.Muted, "Macro:");
                    ImGui.SameLine(0, 4 * scale);
                    ImGui.TextColored(ConfigStyle.Primary, "/hachidori pad");
                    ImGui.SameLine(0, 6 * scale);
                    if (ImGui.SmallButton("Copy")) ImGui.SetClipboardText("/hachidori pad");

                    DrawControllerHoldButtonControl();
                    if (Advanced)
                    {
                        if (config.ControllerHoldButton != GamepadButtons.None)
                        {
                            DrawControllerTriggerModeControl();
                            if (config.ControllerTriggerMode == ControllerTriggerMode.Hold)
                                DrawControllerHoldSecondsControl();
                        }
                        DrawControllerToggleKeyControl();
                    }
                }
            }
        }

        using (var card = ConfigStyle.Card("ctrl-replay", "Cutscene Voice Repeat", ConfigStyle.Secondary,
            subtitle: "Replay the latest voice line without opening controller mode."))
        {
            if (card.IsOpen)
            {
                DrawControllerQuickReplayButtonControl();
                if (config.ControllerQuickReplayButton != GamepadButtons.None)
                {
                    using var indent = ImRaii.PushIndent();
                    DrawControllerQuickReplayTriggerModeControl();
                    if (config.ControllerQuickReplayTriggerMode == ControllerTriggerMode.Hold)
                        DrawControllerHoldSecondsControl();
                    if (Advanced)
                        Checkbox("Only during cutscenes / dialogue", () => config.ControllerQuickReplayCutsceneOnly, v => config.ControllerQuickReplayCutsceneOnly = v);
                }
                if (Advanced)
                {
                    DrawReplayVoiceKeyControl();
                    if (config.ControllerEnabled)
                        DrawControllerReplayVoiceButtonControl();
                }
            }
        }

        if (!config.ControllerEnabled)
            return;

        using (var card = ConfigStyle.Card("ctrl-behavior", "Display & Behavior", ConfigStyle.Primary))
        {
            if (card.IsOpen)
            {
                using (var tbl = ImRaii.Table("##ctrl-flags", 2, ImGuiTableFlags.SizingStretchSame))
                {
                    if (tbl.Success)
                    {
                        ImGui.TableNextColumn();
                        Checkbox("Auto-scan under cursor", () => config.ControllerAutoScan, v => config.ControllerAutoScan = v);
                        Checkbox("Show button hints", () => config.ControllerShowHints, v => config.ControllerShowHints = v);
                        ImGui.TableNextColumn();
                        Checkbox("PlayStation labels", () => config.ControllerPlayStationLabels, v => config.ControllerPlayStationLabels = v);
                        Checkbox("DualSense/DS4 touchpad", () => config.ControllerUseTouchpad, v => config.ControllerUseTouchpad = v);
                    }
                }
                if (!config.ControllerAutoScan)
                    DrawControllerScanButtonControl();

                if (Advanced)
                {
                    AdvancedDivider();
                    using (var tbl = ImRaii.Table("##ctrl-adv-flags", 2, ImGuiTableFlags.SizingStretchSame))
                    {
                        if (tbl.Success)
                        {
                            ImGui.TableNextColumn();
                            Checkbox("Start in word mode", () => config.ControllerStartInWordMode, v => config.ControllerStartInWordMode = v);
                            Checkbox("Auto-open backlog", () => config.ControllerAutoOpenBacklog, v => config.ControllerAutoOpenBacklog = v);
                            Checkbox("Include chat in backlog", () => config.ControllerBacklogIncludeChat, v => config.ControllerBacklogIncludeChat = v);
                            ImGui.TableNextColumn();
                            Checkbox("Pin line on auto-advance", () => config.ControllerPinOnAdvance, v => config.ControllerPinOnAdvance = v);
                            Checkbox("Exit when dialogue closes", () => config.ControllerExitWhenDialogueCloses, v => config.ControllerExitWhenDialogueCloses = v);
                        }
                    }
                    DrawControllerCursorSpeedControl();
                }
            }
        }

        if (Advanced)
        {
            using var card = ConfigStyle.Card("ctrl-binds", "Remap Controller Buttons (Advanced)", ConfigStyle.Emphasis, defaultOpen: false);
            if (card.IsOpen)
            {
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
                BindCombo("Look up word", () => config.ControllerScanButton, v => config.ControllerScanButton = v);
                BindCombo("Mine to Anki", () => config.ControllerMineButton, v => config.ControllerMineButton = v);
                BindCombo("Play audio", () => config.ControllerAudioButton, v => config.ControllerAudioButton = v);
                BindCombo("Word / char mode", () => config.ControllerWordModeButton, v => config.ControllerWordModeButton = v);
                BindCombo("Back / exit", () => config.ControllerBackExitButton, v => config.ControllerBackExitButton = v);
                BindCombo("Previous entry", () => config.ControllerPrevEntryButton, v => config.ControllerPrevEntryButton = v);
                BindCombo("Next entry", () => config.ControllerNextEntryButton, v => config.ControllerNextEntryButton = v);
                BindCombo("Dive into popup", () => config.ControllerDiveButton, v => config.ControllerDiveButton = v);
                BindCombo("Cycle UI window", () => config.ControllerWindowButton, v => config.ControllerWindowButton = v);
                BindCombo("Dialogue backlog", () => config.ControllerHistoryButton, v => config.ControllerHistoryButton = v);
                BindCombo("Replay line voice", () => config.ControllerReplayVoiceButton, v => config.ControllerReplayVoiceButton = v);
            }
        }
    }

    // -------------------------------------------------------------- Reader tab

    private void DrawReaderTab()
    {
        var scale = ImGuiHelpers.GlobalScale;

        using (var card = ConfigStyle.Card("reader-overlay", "Reader Overlay", ConfigStyle.Primary,
            badge: config.ReaderEnabled ? "ON" : "OFF",
            badgeColor: config.ReaderEnabled ? ConfigStyle.Primary : ConfigStyle.Muted))
        {
            if (card.IsOpen)
            {
                Checkbox("Enable reader overlay", () => config.ReaderEnabled, v => config.ReaderEnabled = v);
                ImGui.SameLine(0, 14 * scale);
                Checkbox("Only during dialogue", () => config.ReaderAutoShowWithDialogue, v => config.ReaderAutoShowWithDialogue = v);
                ImGui.SameLine(0, 14 * scale);
                Checkbox("Show history", () => config.ReaderShowHistory, v => config.ReaderShowHistory = v);
                DrawReaderAnchorControl();
                DrawReaderFontSizeControl();
                if (Advanced)
                {
                    AdvancedDivider();
                    Checkbox("Lock window", () => config.ReaderLocked, v => config.ReaderLocked = v);
                    ImGui.SameLine(0, 14 * scale);
                    Checkbox("Hide text until hovered", () => config.ReaderHideUntilHovered, v => config.ReaderHideUntilHovered = v);
                    DrawReaderOpacityControl();
                }
            }
        }

        using (var card = ConfigStyle.Card("reader-capture", "Captured Text & Player Name", ConfigStyle.Secondary))
        {
            if (card.IsOpen)
            {
                Checkbox("Dialogue & cutscenes", () => config.CaptureTalk, v => config.CaptureTalk = v);
                ImGui.SameLine(0, 14 * scale);
                Checkbox("Battle talk", () => config.CaptureBattleTalk, v => config.CaptureBattleTalk = v);
                ImGui.SameLine(0, 14 * scale);
                Checkbox("Chat", () => config.CaptureChat, v => config.CaptureChat = v);

                ImGui.Spacing();
                Checkbox("Read character name as katakana", () => config.KatakanaPlayerName, v => config.KatakanaPlayerName = v);
                if (Advanced && config.KatakanaPlayerName)
                {
                    using var indent = ImRaii.PushIndent();
                    DrawPlayerNameKatakanaControl();
                }

                if (Advanced && Section("capture-channels", "Captured Chat Channels", defaultOpen: false))
                {
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
        }
    }

    // -------------------------------------------------------------- Connection tab

    private void DrawConnectionTab()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var source = ActiveSource;

        // Compact source switcher at top so users only see settings for their active dictionary source
        var sourceChoices = new[] { "Built-in Dictionaries (local .zip)", "Hachidori Browser Extension" };
        var sourceIdx = source == DictionarySource.BuiltIn ? 0 : 1;
        ImGui.SetNextItemWidth(280 * scale);
        if (ImGui.Combo("Dictionary source", ref sourceIdx, sourceChoices, sourceChoices.Length))
        {
            SelectDictionarySource(sourceIdx == 0 ? DictionarySource.BuiltIn : DictionarySource.HachidoriBrowser);
            source = ActiveSource;
        }

        // Only show Built-in Dictionaries card when Built-in mode is active (or Advanced)
        if (source == DictionarySource.BuiltIn && offline is { } off)
        {
            var localDicts = off.Store.Manifest.Dictionaries.Where(d => string.Equals(d.Source, "local", StringComparison.OrdinalIgnoreCase)).ToList();
            using var card = ConfigStyle.Card("conn-local", "Built-in Dictionaries", ConfigStyle.Primary,
                badge: localDicts.Count > 0 ? $"{localDicts.Count} LOADED" : "EMPTY",
                badgeColor: localDicts.Count > 0 ? ConfigStyle.Good : ConfigStyle.Emphasis);
            if (card.IsOpen)
            {
                using (ImRaii.Disabled(off.Importing))
                {
                    if (ConfigStyle.AccentButton("Import .zip…", ConfigStyle.Primary))
                        StartBrowseZipImport();
                    ImGui.SameLine(0, 6 * scale);
                    if (ImGui.Button("Install JMdict (EN)"))
                        _ = off.ImportFromUrlAsync(StandaloneDictionaries.StarterJmdictUrl, "JMdict (English)")
                            .ContinueWith(_ => { service.ClearCache(); doctor.RunNow(); });
                    ImGui.SameLine(0, 6 * scale);
                    if (ImGui.Button("Install FFXIV Dict"))
                        _ = off.ImportFromUrlAsync(FfxivDictionary.DownloadUrl, FfxivDictionary.Title)
                            .ContinueWith(_ => { service.ClearCache(); doctor.RunNow(); });
                }

                if (Advanced)
                {
                    ImGui.SetNextItemWidth(Math.Max(200 * scale, ImGui.GetContentRegionAvail().X - 96 * scale));
                    ImGui.InputTextWithHint("##zip-path", "Or paste path to a .zip dictionary…", ref zipPathInput, 512);
                    ImGui.SameLine(0, 6 * scale);
                    using (ImRaii.Disabled(off.Importing || string.IsNullOrWhiteSpace(zipPathInput)))
                    {
                        if (ImGui.Button("Import##zip-path-go"))
                        {
                            var path = zipPathInput.Trim().Trim('"');
                            zipPathInput = string.Empty;
                            _ = off.ImportZipsAsync([path]).ContinueWith(_ => { service.ClearCache(); doctor.RunNow(); });
                        }
                    }
                }

                if (off.Importing && off.ImportProgress is { } prog)
                    ImGui.TextColored(ConfigStyle.Primary, prog);
                if (off.LastImportError is { } impErr)
                    ImGui.TextColored(ConfigStyle.Bad, impErr);
                else if (off.LastImportSummary is { } impOk)
                    ImGui.TextColored(ConfigStyle.Good, impOk);

                if (localDicts.Count > 0)
                {
                    ImGui.Spacing();
                    using var tbl = ImRaii.Table("##local-dicts-tbl", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
                    if (tbl.Success)
                    {
                        ImGui.TableSetupColumn("Dictionary", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 70 * scale);
                        ImGui.TableSetupColumn("Order / Remove", ImGuiTableColumnFlags.WidthFixed, 108 * scale);

                        foreach (var d in localDicts)
                        {
                            using var id = ImRaii.PushId(d.Id);
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            var en = d.Enabled;
                            if (ImGui.Checkbox($"{d.Title}##en", ref en))
                            {
                                off.SetLocalDictionaryEnabled(d.Id, en);
                                service.ClearCache();
                            }

                            ImGui.TableNextColumn();
                            ImGui.TextDisabled(FormatBytes(d.Bytes));

                            ImGui.TableNextColumn();
                            if (ImGui.SmallButton("↑")) { off.MoveLocalDictionary(d.Id, -1); service.ClearCache(); }
                            ImGui.SameLine(0, 2 * scale);
                            if (ImGui.SmallButton("↓")) { off.MoveLocalDictionary(d.Id, 1); service.ClearCache(); }
                            ImGui.SameLine(0, 4 * scale);
                            if (ImGui.SmallButton("×"))
                            {
                                off.RemoveLocalDictionary(d.Id);
                                service.ClearCache();
                                doctor.RunNow();
                            }
                        }
                    }
                }
            }
        }

        // Only show Hachidori Extension card when Extension mode is active
        if (source == DictionarySource.HachidoriBrowser)
        {
            using var card = ConfigStyle.Card("conn-relay", "Hachidori Browser Extension", ConfigStyle.Primary,
                badge: service.Link.Connected ? "LINKED" : "OFFLINE",
                badgeColor: service.Link.Connected ? ConfigStyle.Good : ConfigStyle.Emphasis);
            if (card.IsOpen)
            {
                if (service.Link.Connected)
                    ImGui.TextColored(ConfigStyle.Good, $"Connected to Hachidori v{service.Link.HostVersion} ({service.Link.DictionaryCount} dictionaries).");
                else
                    ImGui.TextColored(ConfigStyle.Muted, service.Link.Error ?? "Waiting for Hachidori Relay in Anki.");

                if (ImGui.Button("Reconnect"))
                {
                    service.Link.Reconnect();
                    doctor.RunNow();
                }

                if (offline is { } offMode)
                {
                    ImGui.SameLine(0, 8 * scale);
                    if (!config.OfflineMode)
                    {
                        using (ImRaii.Disabled(!service.Link.Connected || offMode.Sync.Running))
                        {
                            if (ImGui.Button("Enable Offline Sync"))
                                _ = offMode.SetUpAsync();
                        }
                    }
                    else
                    {
                        using (ImRaii.Disabled(!service.Link.Connected || offMode.Sync.Running))
                        {
                            if (ImGui.Button("Sync Now"))
                                _ = offMode.SyncNowAsync();
                        }
                        ImGui.SameLine(0, 8 * scale);
                        using (ImRaii.Disabled(offMode.Sync.Running))
                        {
                            if (ImGui.Button(confirmRemoveOffline ? "Confirm delete offline copy" : "Delete Offline Copy"))
                            {
                                if (confirmRemoveOffline)
                                {
                                    confirmRemoveOffline = false;
                                    _ = offMode.RemoveAsync().ContinueWith(_ => { service.ClearCache(); doctor.RunNow(); });
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
                    }

                    if (offMode.Sync.Running)
                        ImGui.TextColored(ConfigStyle.Primary, offMode.Sync.Progress ?? "Syncing…");
                    else if (offMode.LastSyncError is { } err)
                        ImGui.TextColored(ConfigStyle.Bad, err);
                    else if (offMode.Store.HasDictionaries)
                        ImGui.TextDisabled($"Offline copy: {offMode.Store.Manifest.Dictionaries.Count} dicts, {FormatBytes(offMode.Store.DiskBytes)} (synced {Ago(offMode.Store.Manifest.SyncedAt)}).");

                    foreach (var w in offMode.Warnings())
                        ImGui.TextColored(ConfigStyle.Emphasis, $"! {w}");
                }

                if (service.Link.Connected)
                {
                    var dicts = service.Dictionaries.ToList();
                    if (dicts.Count > 0 && ImGui.TreeNode($"Host dictionaries ({dicts.Count})"))
                    {
                        foreach (var d in dicts)
                        {
                            var enabled = d["enabled"] is not System.Text.Json.Nodes.JsonValue e || !e.TryGetValue<bool>(out var en) || en;
                            ImGui.BulletText($"{LookupResult.Str(d["title"])}{(enabled ? string.Empty : " (disabled)")}");
                        }
                        ImGui.TreePop();
                    }
                }

                if (Advanced)
                {
                    AdvancedDivider();
                    DrawRelayHostControl();
                    DrawRelayPortControl();
                    DrawApiPortControl();
                }
            }
        }

        // Diagnostics & Document Title card (Advanced)
        if (Advanced)
        {
            using var card = ConfigStyle.Card("conn-diag", "General & Diagnostics (Advanced)", ConfigStyle.Secondary, defaultOpen: false);
            if (card.IsOpen)
            {
                DrawDocumentTitleControl();
                ImGui.Spacing();
                if (ImGui.SmallButton("Export Debug Log"))
                {
                    try { exportedLog = exportLogs(); }
                    catch (Exception ex) { exportedLog = $"Export failed: {ex.Message}"; }
                }
                if (exportedLog is not null)
                {
                    ImGui.SameLine(0, 8 * scale);
                    if (ImGui.SmallButton("Copy Log Path"))
                        ImGui.SetClipboardText(exportedLog);
                    ImGui.TextWrapped(exportedLog);
                }
            }
        }

        if (dictionaryNotice is not null && DateTime.UtcNow < dictionaryNoticeUntil)
            ImGui.TextColored(dictionaryNoticeIsError ? ConfigStyle.Emphasis : ConfigStyle.Muted, dictionaryNotice);
        else if (dictionaryNotice is not null)
            dictionaryNotice = null;
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

    private void OpenDictionaryLink(string url, string successMessage)
    {
        if (FfxivDictionary.TryOpenUrl(url, out var error))
        {
            SetDictionaryNotice(successMessage, isError: false);
        }
        else
        {
            ImGui.SetClipboardText(url);
            SetDictionaryNotice($"Could not open browser ({error ?? "unknown error"}). Copied link to clipboard.", isError: true);
        }
    }

    private void SetDictionaryNotice(string message, bool isError)
    {
        dictionaryNotice = message;
        dictionaryNoticeIsError = isError;
        dictionaryNoticeUntil = DateTime.UtcNow.AddSeconds(6);
    }
}
