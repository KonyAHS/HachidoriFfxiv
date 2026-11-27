using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Offline;

namespace HachidoriFfxiv.Ui;

public sealed partial class ConfigWindow
{
    private const string AnkiConnectPageUrl = "https://ankiweb.net/shared/info/2055492159";

    private void DrawSetupTab()
    {
        var scale = ImGuiHelpers.GlobalScale;
        doctor.EnsureFresh();
        var source = ActiveSource;

        DrawPlatformCompatibilityGuide();

        // 1. Dictionary Source Picker (hides irrelevant settings everywhere else)
        using (var card = ConfigStyle.Card("setup-source", "1. Dictionary Source", ConfigStyle.Primary,
            badge: source == DictionarySource.BuiltIn ? "BUILT-IN" : "EXTENSION",
            subtitle: "Choose where dictionary lookups come from. Only settings for your choice are shown."))
        {
            if (card.IsOpen)
            {
                var avail = Math.Max(280f * scale, ImGui.GetContentRegionAvail().X - 12f * scale);
                var gap = 8f * scale;
                var tileW = (avail - gap) / 2f;
                const string builtInSub = "Import Yomitan .zip dictionaries directly into the plugin and map Anki fields here.";
                const string extSub = "Link with the Hachidori browser extension through Anki to share its dictionaries and templates.";
                var rowH = ConfigStyle.MeasurePresetRowHeight(tileW, builtInSub, extSub);

                if (ConfigStyle.PresetTile("##src-builtin", "Built-in Dictionaries", builtInSub,
                    source == DictionarySource.BuiltIn, tileW, rowH))
                {
                    SelectDictionarySource(DictionarySource.BuiltIn);
                }
                ImGui.SameLine(0, gap);
                if (ConfigStyle.PresetTile("##src-ext", "Hachidori Browser Extension", extSub,
                    source == DictionarySource.HachidoriBrowser, tileW, rowH))
                {
                    SelectDictionarySource(DictionarySource.HachidoriBrowser);
                }
            }
        }

        // 2. Setup Style Presets (generous dynamic tile height so zero text clips)
        using (var card = ConfigStyle.Card("setup-style", "2. Mining Style", ConfigStyle.Primary,
            badge: config.SetupStyle == SetupStyle.Unset ? "PICK ONE" : config.SetupStyle.ToString().ToUpperInvariant(),
            badgeColor: config.SetupStyle == SetupStyle.Unset ? ConfigStyle.Emphasis : ConfigStyle.Primary,
            subtitle: "Choose a starting preset. You can adjust individual options anytime."))
        {
            if (card.IsOpen)
            {
                var avail = Math.Max(300f * scale, ImGui.GetContentRegionAvail().X - 12f * scale);
                var gap = 8f * scale;
                var tileW = (avail - gap * 2f) / 3f;
                const string gsmSub = "Shift hover + GSM feed.\nNo duplicate capture.";
                const string lightSub = "Shift hover · Still shots.\nVoice clips · Low CPU.";
                const string fullSub = "Instant hover · Clips.\nVoice · WebView2 UI.";
                var rowH = ConfigStyle.MeasurePresetRowHeight(tileW, gsmSub, lightSub, fullSub);

                if (ConfigStyle.PresetTile("##style-gsm", "Keep GSM", gsmSub,
                    config.SetupStyle == SetupStyle.KeepGsm, tileW, rowH))
                {
                    PerformancePresets.ApplySetupStyle(config, SetupStyle.KeepGsm);
                    Changed(true);
                    resetNotice = "Applied Keep GSM preset (Shift hover + GSM WebSocket on).";
                    resetNoticeUntil = DateTime.UtcNow.AddSeconds(4);
                    doctor.RunNow();
                }
                ImGui.SameLine(0, gap);
                if (ConfigStyle.PresetTile("##style-light", "Lightweight", lightSub,
                    config.SetupStyle == SetupStyle.Lightweight, tileW, rowH))
                {
                    PerformancePresets.ApplySetupStyle(config, SetupStyle.Lightweight);
                    Changed(true);
                    resetNotice = "Applied Lightweight preset (still screenshots + voice lines).";
                    resetNoticeUntil = DateTime.UtcNow.AddSeconds(4);
                    doctor.RunNow();
                }
                ImGui.SameLine(0, gap);
                if (ConfigStyle.PresetTile("##style-full", "Full Media", fullSub,
                    config.SetupStyle == SetupStyle.FullMedia, tileW, rowH))
                {
                    PerformancePresets.ApplySetupStyle(config, SetupStyle.FullMedia);
                    Changed(true);
                    resetNotice = "Applied Full Media preset (on-demand clips + voice + WebView2).";
                    resetNoticeUntil = DateTime.UtcNow.AddSeconds(4);
                    doctor.RunNow();
                }
            }
        }

        // 3. Dictionaries & FFXIV Pack (contextual to chosen source; FFXIV Dictionary always visible!)
        var dictReady = source == DictionarySource.BuiltIn
            ? offline is { Ready: true }
            : service.Link.Connected || service.UsingOffline;
        var installedFfxiv = FindInstalledFfxivDictionary();

        using (var card = ConfigStyle.Card("setup-dicts", "3. Dictionaries & FFXIV Pack", ConfigStyle.Secondary,
            badge: dictReady ? "READY" : "ACTION NEEDED",
            badgeColor: dictReady ? ConfigStyle.Good : ConfigStyle.Emphasis))
        {
            if (card.IsOpen)
            {
                if (source == DictionarySource.BuiltIn)
                {
                    if (offline is { } off)
                    {
                        using (ImRaii.Disabled(off.Importing))
                        {
                            if (ConfigStyle.AccentButton("Import Yomitan .zip…", ConfigStyle.Primary))
                                StartBrowseZipImport();
                            ImGui.SameLine(0, 6 * scale);
                            if (ImGui.Button("Install JMdict (EN)"))
                                _ = off.ImportFromUrlAsync(StandaloneDictionaries.StarterJmdictUrl, "JMdict (English)")
                                    .ContinueWith(_ => { service.ClearCache(); doctor.RunNow(); });
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("Downloads official Yomitan JMdict_english.zip into the built-in dictionary engine.");
                        }

                        if (off.Importing && off.ImportProgress is { } prog)
                            ImGui.TextColored(ConfigStyle.Primary, prog);
                        else if (off.LastImportError is { } impErr)
                            ImGui.TextColored(ConfigStyle.Bad, impErr);
                        else if (off.LastImportSummary is { } impOk)
                            ImGui.TextColored(ConfigStyle.Good, impOk);
                    }
                }
                else
                {
                    if (service.Link.Connected)
                    {
                        ImGui.TextColored(ConfigStyle.Good, $"Linked to Hachidori v{service.Link.HostVersion} ({service.Link.DictionaryCount} dictionaries).");
                    }
                    else
                    {
                        ImGui.BulletText("Open Anki with the Hachidori Relay add-on.");
                        ImGui.BulletText("Open Hachidori in your browser and enable Settings → Sharing.");
                    }
                    if (ImGui.SmallButton("Reconnect"))
                    {
                        service.Link.Reconnect();
                        doctor.RunNow();
                    }
                    if (service.Link.Connected && !config.OfflineMode && offline is { } offSync)
                    {
                        ImGui.SameLine(0, 6 * scale);
                        using (ImRaii.Disabled(offSync.Sync.Running))
                        {
                            if (ImGui.SmallButton("Enable Offline Sync"))
                                _ = offSync.SetUpAsync();
                        }
                    }
                }

                // Always show the FFXIV Dictionary row in Setup so it is easy to discover & update
                ConfigStyle.SubHeading("FFXIV Custom Dictionary (37,729 terms & 190 portraits)", ConfigStyle.Secondary);
                if (installedFfxiv is { Enabled: true })
                {
                    var rev = string.IsNullOrWhiteSpace(installedFfxiv.Revision) ? string.Empty : $" (rev {installedFfxiv.Revision})";
                    ImGui.TextColored(ConfigStyle.Good, $"Installed: {installedFfxiv.Title}{rev}");
                }
                else if (installedFfxiv is not null)
                {
                    ImGui.TextColored(ConfigStyle.Emphasis, $"Installed but disabled: {installedFfxiv.Title}");
                }
                else
                {
                    ImGui.TextColored(ConfigStyle.Muted, "Adds FFXIV characters, locations, actions, and JP Party Finder lingo (contains expansion spoilers).");
                }

                if (source == DictionarySource.BuiltIn && offline is { } offFfxiv)
                {
                    using (ImRaii.Disabled(offFfxiv.Importing))
                    {
                        var label = installedFfxiv is not null ? "Reinstall / Update FFXIV Dict" : "Install FFXIV Dictionary";
                        if (ConfigStyle.AccentButton(label, ConfigStyle.Secondary))
                            _ = offFfxiv.ImportFromUrlAsync(FfxivDictionary.DownloadUrl, FfxivDictionary.Title)
                                .ContinueWith(_ => { service.ClearCache(); doctor.RunNow(); });
                    }
                }
                else
                {
                    var label = installedFfxiv is not null ? "Download Latest FFXIV Dict (.zip)" : "Install FFXIV Dictionary (.zip)";
                    if (ConfigStyle.AccentButton(label, ConfigStyle.Secondary))
                        OpenDictionaryLink(FfxivDictionary.DownloadUrl, "Opened download — import the .zip in Hachidori → Settings → Dictionaries.");
                }

                if (openSearchSample is not null && dictReady)
                {
                    ImGui.SameLine(0, 8 * scale);
                    if (ImGui.Button("Try Sample Sentence"))
                        openSearchSample(SearchWindow.SampleLine);
                }

                if (dictionaryNotice is not null && DateTime.UtcNow < dictionaryNoticeUntil)
                    ImGui.TextColored(dictionaryNoticeIsError ? ConfigStyle.Emphasis : ConfigStyle.Muted, dictionaryNotice);
            }
        }

        // 4. Connection Doctor (collapsible!)
        var anyError = doctor.Checks.Any(c => c.Severity == DoctorSeverity.Error);
        var anyWarn = doctor.Checks.Any(c => c.Severity == DoctorSeverity.Warn);
        var docBadge = doctor.Running ? "CHECKING…" : anyError ? "ACTION NEEDED" : anyWarn ? "PARTIAL" : "ALL GOOD";
        var docBadgeCol = doctor.Running ? ConfigStyle.Primary : anyError ? ConfigStyle.Bad : anyWarn ? ConfigStyle.Emphasis : ConfigStyle.Good;

        using (var card = ConfigStyle.Card("setup-doctor", "4. Connection Doctor", ConfigStyle.Primary,
            badge: docBadge, badgeColor: docBadgeCol, defaultOpen: anyError || anyWarn))
        {
            if (card.IsOpen)
            {
                using (ImRaii.Disabled(doctor.Running))
                {
                    if (ImGui.SmallButton("Re-check"))
                        doctor.RunNow();
                }
                if (ffmpegDownloadStatus is not null)
                {
                    ImGui.SameLine(0, 10 * scale);
                    ImGui.TextColored(
                        ffmpegDownloading ? ConfigStyle.Primary
                            : ffmpegDownloadStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ? ConfigStyle.Bad
                            : ConfigStyle.Good,
                        ffmpegDownloadStatus);
                }

                ImGui.Spacing();
                using var tbl = ImRaii.Table("##doctor-tbl", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
                if (tbl.Success)
                {
                    ImGui.TableSetupColumn("Component", ImGuiTableColumnFlags.WidthFixed, 130 * scale);
                    ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 126 * scale);

                    foreach (var check in doctor.Checks)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        var (dot, col) = check.Severity switch
                        {
                            DoctorSeverity.Ok => ("●", ConfigStyle.Good),
                            DoctorSeverity.Warn => ("●", ConfigStyle.Emphasis),
                            DoctorSeverity.Error => ("●", ConfigStyle.Bad),
                            _ => ("○", ConfigStyle.Muted),
                        };
                        ImGui.TextColored(col, dot);
                        ImGui.SameLine(0, 6 * scale);
                        ImGui.TextUnformatted(check.Label);

                        ImGui.TableNextColumn();
                        ImGui.TextColored(check.Severity is DoctorSeverity.Error or DoctorSeverity.Warn ? col : ConfigStyle.Muted, check.Detail);

                        ImGui.TableNextColumn();
                        if (!string.IsNullOrEmpty(check.ActionLabel) && !string.IsNullOrEmpty(check.ActionId))
                        {
                            using var id = ImRaii.PushId(check.Id);
                            var disabled = check.ActionId == "download-ffmpeg" && ffmpegDownloading;
                            using (ImRaii.Disabled(disabled))
                            {
                                if (ImGui.SmallButton(check.ActionLabel))
                                    ExecuteDoctorAction(check.ActionId);
                            }
                        }
                    }
                }
            }
        }

        // Collapsible Chat Commands reference
        using (var card = ConfigStyle.Card("setup-commands", "Chat Commands", ConfigStyle.Secondary, defaultOpen: false))
        {
            if (card.IsOpen)
            {
                using var tbl = ImRaii.Table("##guide-commands", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
                if (tbl.Success)
                {
                    ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, 160 * scale);
                    ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);
                    foreach (var (cmd, desc) in new[]
                    {
                        ("/hachidori", "Open or close Settings"),
                        ("/hachidori freeze", "Freeze screen & tooltips (Ctrl+Shift+F)"),
                        ("/hachidori stats", "Immersion stats & Anki card feed"),
                        ("/hachidori pad", "Controller reading mode (great on cross-hotbar)"),
                        ("/hachidori backlog", "Dialogue backlog with cached media"),
                        ("/hachidori replay", "Repeat latest cached voice line"),
                        ("/hachidori reader", "Toggle optional line-history Reader"),
                        ("/hachidori search", "Manual search window"),
                        ("/hachidori pictures", "Custom NPC picture dictionary"),
                        ("/hachidori logs", "Export redacted diagnostic log"),
                    })
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextColored(ConfigStyle.Primary, cmd);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(desc);
                    }
                }
            }
        }

        // Footer row: startup tab preference + Reset Setup (keeps stats) + separate Delete Stats
        ImGui.Spacing();
        var dismissed = config.QuickGuideDismissed;
        if (ImGui.Checkbox("Open the Popup tab first next time", ref dismissed))
        {
            config.QuickGuideDismissed = dismissed;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushColor(ImGuiCol.Button, confirmResetSetup ? ConfigStyle.Emphasis with { W = 0.45f } : new Vector4(0.22f, 0.22f, 0.27f, 0.9f)))
        {
            if (ImGui.SmallButton(confirmResetSetup ? "Click again to reset setup" : "Reset Setup (keeps stats)"))
            {
                if (confirmResetSetup)
                {
                    confirmResetSetup = false;
                    config.ResetSetup();
                    media.ApplyPerformanceSettings();
                    Changed(true);
                    doctor.RunNow();
                    resetNotice = "Setup and settings reset to defaults. Your immersion stats were kept.";
                    resetNoticeUntil = DateTime.UtcNow.AddSeconds(5);
                }
                else
                {
                    confirmResetSetup = true;
                }
            }
            else if (confirmResetSetup && !ImGui.IsItemHovered())
            {
                confirmResetSetup = false;
            }
        }
        if (ImGui.IsItemHovered() && !confirmResetSetup)
            ImGui.SetTooltip("Resets setup choices and plugin settings to defaults. Your immersion stats and downloaded dictionaries are not deleted.");

        if (clearStats is not null)
        {
            ImGui.SameLine(0, 12 * scale);
            using (ImRaii.PushColor(ImGuiCol.Button, confirmClearStats ? ConfigStyle.Bad with { W = 0.65f } : new Vector4(0.28f, 0.16f, 0.16f, 0.75f)))
            {
                if (ImGui.SmallButton(confirmClearStats ? "Click again to delete all stats" : "Delete Immersion Stats"))
                {
                    if (confirmClearStats)
                    {
                        confirmClearStats = false;
                        clearStats();
                        resetNotice = "All immersion stats have been cleared.";
                        resetNoticeUntil = DateTime.UtcNow.AddSeconds(5);
                    }
                    else
                    {
                        confirmClearStats = true;
                    }
                }
                else if (confirmClearStats && !ImGui.IsItemHovered())
                {
                    confirmClearStats = false;
                }
            }
            if (ImGui.IsItemHovered() && !confirmClearStats)
                ImGui.SetTooltip("Permanently clears all recorded reading stats, streaks, and history (separate from Reset Setup).");
        }
    }

    private void SelectDictionarySource(DictionarySource next)
    {
        config.DictionarySource = next;
        if (next == DictionarySource.BuiltIn)
        {
            ankiMapper.SeedStandaloneFromHostTemplate(overwrite: false);
            config.StandaloneAnki.PreferStandalone = true;
            config.AnkiTemplateId = ankiMapper.IsStandaloneEmpty()
                ? string.Empty
                : StandaloneDictionaries.StandaloneTemplateId;
        }
        else
        {
            config.StandaloneAnki.PreferStandalone = false;
            if (config.AnkiTemplateId == StandaloneDictionaries.StandaloneTemplateId)
                config.AnkiTemplateId = string.Empty;
        }
        Changed(true);
        doctor.RunNow();
    }

    private void ExecuteDoctorAction(string actionId)
    {
        switch (actionId)
        {
            case "import-zip":
                StartBrowseZipImport();
                break;
            case "open-ankiconnect":
                OpenDictionaryLink(AnkiConnectPageUrl, "Opened AnkiConnect add-on page (install code: 2055492159).");
                break;
            case "reconnect":
                service.Link.Reconnect();
                doctor.RunNow();
                break;
            case "open-mining":
                pendingSelectTab = "Mining";
                break;
            case "download-ffmpeg":
                StartManagedFfmpegDownload();
                break;
            case "sync-webview":
                if (webView is not null)
                    _ = webView.SyncBundleNowAsync(force: true);
                break;
        }
    }

    private void DrawPlatformCompatibilityGuide()
    {
        var platform = PlatformInfo.Current;
        var missingWebView = platform.SupportsWebView2Overlay && webView is { RuntimeAvailable: false };
        if (!platform.IsWine && platform.SupportsProcessAudioLoopback && !missingWebView)
            return;

        using var card = ConfigStyle.Card("guide-platform", $"Environment: {platform.DisplayName}", ConfigStyle.Emphasis, badge: "FALLBACK");
        if (!card.IsOpen)
            return;

        if (platform.IsWine)
        {
            ImGui.BulletText("Popups use the built-in ImGui renderer (WebView2 is Windows-only).");
            ImGui.BulletText(platform.HostOs == HostOsKind.MacOs
                ? "macOS has no audio loopback — use TTS in Mining or a virtual loopback device."
                : "Voice recording captures system audio; lower BGM/other apps for clean clips.");
            if (platform.IsSteamDeck)
                ImGui.BulletText("Steam Deck: bind /hachidori pad & /hachidori freeze to hotbars, and enable plugin gamepad input in Dalamud.");
        }
        else
        {
            if (!platform.SupportsProcessAudioLoopback)
                ImGui.BulletText("This Windows build uses system audio loopback instead of FFXIV-only capture.");
            if (missingWebView)
                ImGui.BulletText("Microsoft Edge WebView2 Runtime is not installed — using built-in renderer.");
        }
    }
}
