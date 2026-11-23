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

public sealed class ConfigWindow : Window
{
    private static readonly Configuration Defaults = new();

    private static readonly Vector4 AccentPurple = new(0.68f, 0.62f, 0.94f, 1f);
    private static readonly Vector4 AccentCyan = new(0.42f, 0.80f, 0.98f, 1f);
    private static readonly Vector4 AccentGreen = new(0.42f, 0.86f, 0.56f, 1f);
    private static readonly Vector4 AccentAmber = new(0.96f, 0.76f, 0.36f, 1f);
    private static readonly Vector4 AccentRed = new(0.96f, 0.48f, 0.48f, 1f);
    private static readonly Vector4 MutedText = new(0.68f, 0.70f, 0.76f, 1f);

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

    private static readonly string[] ProviderNames =
        ["Gemini", "Groq", "OpenAI-compatible", "Ollama", "LM Studio", "Z.ai", "DeepL", "Codex (local CLI)", "Claude Code (local CLI)"];

    private static readonly GamepadButtons[] HoldChoices =
    [
        GamepadButtons.None,
        GamepadButtons.Select,
        GamepadButtons.Start,
        GamepadButtons.L3,
        GamepadButtons.R3,
        GamepadButtons.L3 | GamepadButtons.R3,
        GamepadButtons.L1 | GamepadButtons.R1,
        GamepadButtons.Select | GamepadButtons.DpadDown,
    ];

    private static readonly GamepadButtons[] BindChoices =
    [
        GamepadButtons.None,
        GamepadButtons.South,
        GamepadButtons.East,
        GamepadButtons.West,
        GamepadButtons.North,
        GamepadButtons.L1,
        GamepadButtons.R1,
        GamepadButtons.L2,
        GamepadButtons.R2,
        GamepadButtons.L3,
        GamepadButtons.R3,
    ];

    private sealed record SettingEntry(
        string Tab,
        string Section,
        string Label,
        string Keywords,
        bool IsAdvanced,
        Action DrawControl);

    private readonly Configuration config;
    private readonly HachidoriService service;
    private readonly Fonts fonts;
    private readonly NativeTextHover nativeHover;
    private readonly TextPublisher publisher;
    private readonly PopupThemeProvider themes;
    private readonly MediaMiner media;
    private readonly Func<string> exportLogs;
    private readonly Action? openStats;
    private readonly Action<string?>? openSearchSample;
    private readonly Action? openReader;
    private readonly Action? openPictures;
    private readonly WebViewPopupController? webView;
    private readonly Action? triggerFreezeFrame;
    private readonly List<SettingEntry> catalog = new();

    private string searchQuery = string.Empty;
    private string? pendingSelectTab;
    private string? confirmResetTab;
    private string? resetNotice;
    private DateTime resetNoticeUntil = DateTime.MinValue;
    private string? mediaTest;
    private string? exportedLog;
    private string? dictionaryNotice;
    private bool dictionaryNoticeIsError;
    private DateTime dictionaryNoticeUntil = DateTime.MinValue;
    private bool confirmRemoveOffline;
    private bool ankiConnectedHint;
    private bool firstDraw = true;

    public ConfigWindow(
        Configuration config,
        HachidoriService service,
        Fonts fonts,
        NativeTextHover nativeHover,
        TextPublisher publisher,
        PopupThemeProvider themes,
        MediaMiner media,
        Func<string> exportLogs,
        Action? openStats = null,
        Action<string?>? openSearchSample = null,
        Action? openReader = null,
        Action? openPictures = null,
        WebViewPopupController? webView = null,
        Action? triggerFreezeFrame = null)
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
        this.openStats = openStats;
        this.openSearchSample = openSearchSample;
        this.openReader = openReader;
        this.openPictures = openPictures;
        this.webView = webView;
        this.triggerFreezeFrame = triggerFreezeFrame;

        Size = new Vector2(660, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 440),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        BuildSearchCatalog();
    }

    private bool Advanced => config.AdvancedSettings;

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;

        if (firstDraw)
        {
            firstDraw = false;
            // Open Quick Guide automatically on first launch if not connected and not dismissed.
            if (!config.QuickGuideDismissed && !service.Link.Connected && !service.UsingOffline)
                pendingSelectTab = "Quick Guide";
            else
                pendingSelectTab = "Popup";
        }

        DrawHeaderBar(scale);
        DrawSearchAndToolbar(scale);

        if (resetNotice is not null && DateTime.UtcNow < resetNoticeUntil)
            ImGui.TextColored(AccentGreen, resetNotice);
        else if (resetNotice is not null)
            resetNotice = null;

        ImGui.Separator();

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            DrawSearchResults(scale);
            return;
        }

        using var tabs = ImRaii.TabBar("##settings-tabs", ImGuiTabBarFlags.FittingPolicyScroll);
        if (!tabs.Success)
            return;

        DrawTab("Quick Guide", null, DrawQuickGuideTab, scale);
        DrawTab("Popup", () => { config.ResetPopupDefaults(); Changed(true); }, DrawPopupTab, scale);
        DrawTab("Theme", () => { config.ResetThemeDefaults(); config.Save(); webView?.NotifySettingsChanged(); }, DrawThemeTab, scale);
        DrawTab("Mining", () => { config.ResetMiningDefaults(); config.Save(); }, DrawMiningTab, scale);
        DrawTab("Controller", () => { config.ResetControllerDefaults(); config.Save(); }, DrawControllerTab, scale);
        DrawTab("Reader", () => { config.ResetReaderDefaults(); Changed(true); }, DrawReaderTab, scale);
        DrawTab("Capture", () => { config.ResetCaptureDefaults(); Changed(true); }, DrawCaptureTab, scale);
        if (Advanced || config.PublishEnabled || pendingSelectTab == "GSM")
            DrawTab("GSM", () => { config.ResetPublishDefaults(); Changed(true); }, DrawPublishTab, scale);
        DrawTab("Connection", () => { config.ResetConnectionDefaults(); Changed(true); }, DrawConnectionTab, scale);

        pendingSelectTab = null;
    }

    // -------------------------------------------------------------- header & search

    private void DrawHeaderBar(float scale)
    {
        var link = service.Link;
        var pos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();

        // Compact status pill background
        var bannerH = 30 * scale;
        Vector4 dotCol;
        string statusText;
        if (link.Connected)
        {
            dotCol = AccentGreen;
            statusText = $"Linked to Hachidori ({link.HostName} v{link.HostVersion}) · {link.DictionaryCount} dicts";
        }
        else if (service.UsingOffline && service.Offline is { } offline)
        {
            dotCol = AccentAmber;
            statusText = $"Offline mode · {offline.Store.Manifest.Dictionaries.Count} local dicts (synced {Ago(offline.Store.Manifest.SyncedAt)})";
        }
        else
        {
            dotCol = AccentRed;
            statusText = link.Error ?? "Not linked — open Quick Guide to connect";
        }

        draw.AddRectFilled(pos, pos + new Vector2(avail, bannerH), ImGui.ColorConvertFloat4ToU32(new Vector4(0.13f, 0.14f, 0.18f, 0.92f)), 6 * scale);
        draw.AddRect(pos, pos + new Vector2(avail, bannerH), ImGui.ColorConvertFloat4ToU32(new Vector4(dotCol.X, dotCol.Y, dotCol.Z, 0.35f)), 6 * scale);

        ImGui.SetCursorScreenPos(pos + new Vector2(10 * scale, 6 * scale));
        ImGui.TextColored(dotCol, "●");
        ImGui.SameLine(0, 6 * scale);

        var buttonsW = 248 * scale;
        var textMaxW = Math.Max(100 * scale, avail - buttonsW - 32 * scale);
        ImGui.TextUnformatted(TruncateToWidth(statusText, textMaxW));

        // Quick window launchers on the right side of the status strip
        ImGui.SetCursorScreenPos(new Vector2(pos.X + avail - buttonsW, pos.Y + 4 * scale));
        if (openStats is not null && ImGui.SmallButton("Stats"))
            openStats();
        if (openStats is not null) ImGui.SameLine(0, 4 * scale);
        if (openSearchSample is not null && ImGui.SmallButton("Search"))
            openSearchSample(null);
        if (openSearchSample is not null) ImGui.SameLine(0, 4 * scale);
        if (openReader is not null && ImGui.SmallButton("Reader"))
            openReader();
        if (openReader is not null) ImGui.SameLine(0, 4 * scale);
        if (openPictures is not null && ImGui.SmallButton("Pictures"))
            openPictures();

        ImGui.SetCursorScreenPos(pos + new Vector2(0, bannerH + 5 * scale));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawSearchAndToolbar(float scale)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var advWidth = 175 * scale;
        var clearW = searchQuery.Length > 0 ? 28 * scale : 0f;
        var searchW = Math.Max(140 * scale, avail - advWidth - clearW - (clearW > 0 ? 12 * scale : 8 * scale));

        ImGui.SetNextItemWidth(searchW);
        ImGui.InputTextWithHint("##settings-search", "Search all settings (e.g. font, avif, voice, pitch, blur, port)…", ref searchQuery, 128);
        if (clearW > 0)
        {
            ImGui.SameLine(0, 4 * scale);
            if (ImGui.Button("×##clear-search", new Vector2(clearW, 0)))
                searchQuery = string.Empty;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Clear search");
        }

        ImGui.SameLine(0, 8 * scale);
        var advanced = config.AdvancedSettings;
        if (ImGui.Checkbox("Advanced settings", ref advanced))
        {
            config.AdvancedSettings = advanced;
            config.Save();
        }
        HelpMarker("Shows advanced collapsible sections inside tabs. Search always finds all settings regardless of this toggle.");
    }

    private void DrawSearchResults(float scale)
    {
        var tokens = searchQuery.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = catalog.Where(item => MatchesAll(item, tokens)).ToList();

        ImGui.TextColored(AccentCyan, $"{matches.Count} matching setting{(matches.Count == 1 ? string.Empty : "s")}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Back to tabs"))
            searchQuery = string.Empty;
        ImGui.Spacing();

        using var scroll = ImRaii.Child("##search-results-scroll", Vector2.Zero, false);
        if (!scroll.Success)
            return;

        if (matches.Count == 0)
        {
            ImGui.TextDisabled($"No settings match “{searchQuery}”. Try keywords like popup, theme, audio, voice, gif, translation, controller, reader, chat, or offline.");
            return;
        }

        string? lastGroup = null;
        for (var i = 0; i < matches.Count; i++)
        {
            var entry = matches[i];
            var group = $"{entry.Tab} › {entry.Section}";
            if (group != lastGroup)
            {
                if (lastGroup is not null)
                {
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();
                }

                lastGroup = group;
                ImGui.TextColored(AccentPurple, group);
                ImGui.SameLine(0, 10 * scale);
                using (ImRaii.PushId($"jump-{i}"))
                {
                    if (ImGui.SmallButton($"Open {entry.Tab} tab"))
                    {
                        pendingSelectTab = entry.Tab;
                        searchQuery = string.Empty;
                    }
                }
            }

            using var id = ImRaii.PushId($"sr-{i}");
            using (ImRaii.PushIndent(10 * scale))
            {
                if (entry.IsAdvanced)
                {
                    ImGui.TextColored(AccentAmber, "[Advanced]");
                    ImGui.SameLine(0, 6 * scale);
                }
                entry.DrawControl();
            }
        }
    }

    private static bool MatchesAll(SettingEntry item, string[] tokens)
    {
        foreach (var token in tokens)
        {
            var hit = item.Label.Contains(token, StringComparison.OrdinalIgnoreCase)
                      || item.Section.Contains(token, StringComparison.OrdinalIgnoreCase)
                      || item.Tab.Contains(token, StringComparison.OrdinalIgnoreCase)
                      || item.Keywords.Contains(token, StringComparison.OrdinalIgnoreCase);
            if (!hit)
                return false;
        }
        return true;
    }

    // -------------------------------------------------------------- tab shell

    private void DrawTab(string name, Action? resetAction, Action drawBody, float scale)
    {
        var flags = pendingSelectTab == name ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        using var tab = ImRaii.TabItem(name, flags);
        if (!tab.Success)
            return;

        var footerH = resetAction is not null ? 36 * scale : 0f;
        var scrollH = Math.Max(120 * scale, ImGui.GetContentRegionAvail().Y - footerH);
        using (var scroll = ImRaii.Child($"##tab-scroll-{name}", new Vector2(0, scrollH), false))
        {
            if (scroll.Success)
            {
                ImGui.Spacing();
                drawBody();
                ImGui.Spacing();
            }
        }

        if (resetAction is not null)
        {
            ImGui.Separator();
            ImGui.Spacing();
            var confirming = confirmResetTab == name;
            using var col = ImRaii.PushColor(ImGuiCol.Button, confirming ? new Vector4(0.75f, 0.25f, 0.25f, 0.95f) : new Vector4(0.22f, 0.22f, 0.27f, 0.9f));
            var btnLabel = confirming ? $"Click again to reset {name} to defaults" : $"Reset {name} tab to defaults";
            if (ImGui.Button($"{btnLabel}##reset-{name}"))
            {
                if (confirming)
                {
                    confirmResetTab = null;
                    resetAction();
                    resetNotice = $"Reset {name} settings to defaults.";
                    resetNoticeUntil = DateTime.UtcNow.AddSeconds(4);
                }
                else
                {
                    confirmResetTab = name;
                }
            }
            else if (confirming && !ImGui.IsItemHovered())
            {
                confirmResetTab = null;
            }

            ImGui.SameLine(0, 10 * scale);
            ImGui.TextDisabled("Tip: right-click any number control to reset just that value.");
        }
    }

    // -------------------------------------------------------------- Quick Guide tab

    private void DrawQuickGuideTab()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var connected = service.Link.Connected;
        var offlineReady = service.UsingOffline;
        var dictReady = connected || offlineReady;

        ImGui.TextColored(AccentPurple, "Welcome to Hachidori for FFXIV");
        ImGui.TextWrapped("Hover native Japanese dialogue, battle talk, and chat for Yomitan-style dictionary popups, pitch accent, audio, and one-click Anki card mining with game media.");
        ImGui.Spacing();

        DrawPlatformCompatibilityGuide(scale);

        // Step 1: Connect dictionary backend
        if (Section("guide-step1", dictReady ? "Step 1: Connect Dictionaries  [Ready]" : "Step 1: Connect Dictionaries  [Action Needed]", defaultOpen: true, accent: dictReady ? AccentGreen : AccentAmber))
        {
            if (connected)
            {
                ImGui.TextColored(AccentGreen, $"Connected to Hachidori ({service.Link.HostName} v{service.Link.HostVersion}) with {service.Link.DictionaryCount} dictionaries.");
            }
            else if (offlineReady)
            {
                ImGui.TextColored(AccentAmber, "Running in Offline Mode using your local synced dictionaries.");
            }
            else
            {
                ImGui.BulletText("Open Anki with the Hachidori Relay add-on installed.");
                ImGui.BulletText("Open Chrome with Hachidori and enable Settings → Sharing.");
                ImGui.BulletText("Once connected once, you can enable Offline Mode so Chrome is no longer needed.");
            }

            ImGui.Spacing();
            if (ImGui.Button("Reconnect now"))
                service.Link.Reconnect();
            ImGui.SameLine(0, 6 * scale);
            if (ImGui.Button("Open Connection tab"))
                pendingSelectTab = "Connection";
            if (!config.OfflineMode && connected && service.Offline is { } offline)
            {
                ImGui.SameLine(0, 6 * scale);
                using (ImRaii.Disabled(offline.Sync.Running))
                {
                    if (ImGui.Button("Set up Offline Mode"))
                        _ = offline.SetUpAsync();
                }
            }
        }

        // Optional: Custom FFXIV dictionary
        var installedFfxivDict = FindInstalledFfxivDictionary();
        var dictHeader = installedFfxivDict is { Enabled: true }
            ? "Optional: Custom FFXIV Dictionary  [Installed]"
            : installedFfxivDict is not null
                ? "Optional: Custom FFXIV Dictionary  [Disabled]"
                : "Optional: Custom FFXIV Dictionary";
        var dictAccent = installedFfxivDict is { Enabled: true }
            ? AccentGreen
            : installedFfxivDict is not null
                ? AccentAmber
                : AccentCyan;
        if (Section("guide-ffxiv-dict", dictHeader, defaultOpen: installedFfxivDict is null, accent: dictAccent))
        {
            DrawFfxivDictionaryBody(scale, installedFfxivDict, "guide");
        }

        // Step 2: Scanning & testing
        if (Section("guide-step2", "Step 2: Try Hovering Japanese Text", defaultOpen: true, accent: AccentCyan))
        {
            ImGui.TextWrapped("Hover directly over FFXIV dialogue boxes, cutscene subtitles, or chat. Adjust your preferred scan trigger below:");
            Checkbox("Hover native game text", () => config.NativeTextHover, v => config.NativeTextHover = v);
            ImGui.SameLine(0, 16 * scale);
            Checkbox("Include native chat text", () => config.NativeChatHover, v => config.NativeChatHover = v);
            DrawScanModifierControl();
            ImGui.Spacing();
            if (openSearchSample is not null && ImGui.Button("Open Search with sample FFXIV sentence"))
                openSearchSample(SearchWindow.SampleLine);
            ImGui.SameLine(0, 6 * scale);
            if (ImGui.Button("Customize Popup & Theme"))
                pendingSelectTab = "Popup";
        }

        // Step 3: Anki mining & media
        if (Section("guide-step3", ankiConnectedHint ? "Step 3: Anki Mining & Game Media  [Ready]" : "Step 3: Anki Mining & Game Media (Optional)", defaultOpen: !dictReady, accent: ankiConnectedHint ? AccentGreen : AccentPurple))
        {
            ImGui.TextWrapped("Click + on any popup entry to create an Anki card with automatic game screenshots, voice clips, animations, and AI translations.");
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
            if (ImGui.Button("Open Mining settings"))
                pendingSelectTab = "Mining";
            if (mediaTest is not null)
                ImGui.TextWrapped(mediaTest);
        }

        // Step 4: Immersion tools & commands
        if (Section("guide-step4", "Step 4: Explore Stats, Controller Mode & Commands", defaultOpen: false, accent: AccentAmber))
        {
            if (openStats is not null && ImGui.Button("Open Immersion Stats"))
                openStats();
            ImGui.SameLine(0, 6 * scale);
            if (openReader is not null && ImGui.Button("Open Reader Overlay"))
                openReader();
            ImGui.SameLine(0, 6 * scale);
            if (ImGui.Button("Copy /hachidori pad macro"))
                ImGui.SetClipboardText("/hachidori pad");

            ImGui.Spacing();
            using var tbl = ImRaii.Table("##guide-commands", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
            if (tbl.Success)
            {
                ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, 175 * scale);
                ImGui.TableSetupColumn("What it does", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                foreach (var (cmd, desc) in new[]
                {
                    ("/hachidori", "Open or close this Settings window"),
                    ("/hachidori stats", "Open the Immersion Stats dashboard (NPCs, words, Anki cards)"),
                    ("/hachidori search", "Open the manual Japanese search & hover window"),
                    ("/hachidori <text>", "Look up Japanese text directly"),
                    ("/hachidori reader", "Pin or unpin the dialogue reader overlay"),
                    ("/hachidori pad", "Toggle controller reading mode (great on a cross hotbar macro)"),
                    ("/hachidori pictures", "Manage your custom FFXIV NPC picture dictionary"),
                    ("/hachidori logs", "Export a redacted debug log file"),
                })
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(AccentCyan, cmd);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(desc);
                }
            }
        }

        ImGui.Spacing();
        var dismissed = config.QuickGuideDismissed;
        if (ImGui.Checkbox("Open directly to Popup settings next time instead of Quick Guide", ref dismissed))
        {
            config.QuickGuideDismissed = dismissed;
            config.Save();
        }
    }

    private void DrawPlatformCompatibilityGuide(float scale)
    {
        var platform = PlatformInfo.Current;
        var missingWebViewOnWindows = platform.SupportsWebView2Overlay && webView is { RuntimeAvailable: false };
        var showOpen = platform.HasCompatibilityFallbacks || missingWebViewOnWindows;
        var badge = platform.HasCompatibilityFallbacks
            ? $"{platform.DisplayName} · Compatibility Mode"
            : missingWebViewOnWindows
                ? $"{platform.DisplayName} · ImGui Fallback"
                : $"{platform.DisplayName} · Full Support";
        var accent = platform.HasCompatibilityFallbacks || missingWebViewOnWindows ? AccentAmber : AccentCyan;

        if (!Section("guide-platform", $"OS & Platform Compatibility  [{badge}]", defaultOpen: showOpen, accent: accent))
            return;

        if (platform.IsWine)
        {
            ImGui.TextColored(AccentAmber, $"Detected {platform.DisplayName}. Automatic fallbacks are enabled where Windows-only APIs are unavailable:");
            ImGui.BulletText("Popups: using the built-in ImGui renderer with bundled Hachidori themes (Hachidori Web / WebView2 is Windows-only).");
            if (platform.HostOs == HostOsKind.MacOs)
                ImGui.BulletText("Voice recording: macOS CoreAudio has no built-in loopback; enable TTS in Mining for spoken cards or route audio through a virtual loopback device.");
            else
                ImGui.BulletText("Voice recording: using standard system-output loopback fallback (may include other desktop apps; lower BGM/other audio for cleaner clips).");
            ImGui.BulletText("Pronunciation & animations: Ogg/Opus/WAV and GIF work built-in; install FFmpeg on the host for MP3/AAC fallback and AVIF/WebP animations.");
            ImGui.BulletText("Anki & Chrome: run native Linux/macOS Anki (with Hachidori Relay) and Chrome normally — 127.0.0.1 is shared with the game.");

            if (platform.IsSteamDeck)
            {
                ImGui.Spacing();
                ImGui.TextColored(AccentCyan, "Steam Deck tips:");
                ImGui.BulletText("Best workflow: open Chrome + Anki once in Desktop Mode, link Hachidori, and enable Offline Mode so lookups work in Gaming Mode with Chrome closed.");
                ImGui.BulletText("Put /hachidori pad and /hachidori freeze on cross-hotbar macros (press B / Circle to exit Freeze Frame).");
                ImGui.BulletText("Enable \"plugin gamepad input\" in Dalamud settings; the left stick and Steam Deck trackpads (as mouse) both move the virtual cursor.");
            }

            ImGui.Spacing();
            ImGui.TextDisabled("Disclaimer: Linux, Steam Deck, and macOS support runs via Wine/Proton/XIV on Mac compatibility fallbacks and is best-effort compared to native Windows.");
        }
        else if (!platform.SupportsProcessAudioLoopback)
        {
            ImGui.TextColored(AccentAmber, $"Detected {platform.DisplayName} (pre-2004). Voice recording automatically falls back to system-output audio loopback instead of FFXIV-only loopback.");
            if (missingWebViewOnWindows)
                ImGui.BulletText("Microsoft Edge WebView2 Runtime is not installed — popups are using the built-in ImGui fallback.");
        }
        else if (missingWebViewOnWindows)
        {
            ImGui.TextColored(AccentAmber, "Microsoft Edge WebView2 Runtime was not detected — popups are using the built-in ImGui renderer.");
            ImGui.TextWrapped("Install Microsoft Edge WebView2 Runtime for exact upstream Hachidori Web themes, or keep using the built-in ImGui themes.");
        }
        else
        {
            ImGui.TextWrapped($"Detected {platform.DisplayName} with full native support (WebView2 popups, per-process FFXIV audio recording, GPU screenshots/Freeze Frame, and Offline Mode).");
            ImGui.TextDisabled("Playing on Linux, Steam Deck (XIVLauncher.Core), or macOS (XIV on Mac)? The plugin automatically detects Wine/Proton and enables built-in fallbacks (ImGui themes, system audio loopback / TTS, FFmpeg/GIF). Non-Windows platforms are supported on a best-effort basis.");
        }
    }

    // -------------------------------------------------------------- Popup tab

    private void DrawPopupTab()
    {
        if (Section("popup-scan", "Hover & Scanning", defaultOpen: true))
        {
            Checkbox("Hover native game text", () => config.NativeTextHover, v => config.NativeTextHover = v);
            Checkbox("Include native chat text", () => config.NativeChatHover, v => config.NativeChatHover = v);
            DrawScanModifierControl();
            DrawFreezeHotkeyControl();

            if (Advanced)
            {
                ImGui.Spacing();
                ImGui.TextColored(AccentAmber, "Advanced scanning");
                Checkbox("Hover whole words (Hachidori tokenizer)", () => config.TokenizedHover, v => config.TokenizedHover = v);
                Checkbox("Other native windows (experimental)", () => config.NativeOtherTextHover, v => config.NativeOtherTextHover = v);
                Checkbox("Show native hover targets (debug)", () => config.NativeHoverDebug, v => config.NativeHoverDebug = v);
                if (config.NativeHoverDebug)
                {
                    ImGui.TextDisabled($"Native text regions under mouse: {nativeHover.RegionCount}");
                    if (nativeHover.LastError is { } error) ImGui.TextWrapped(error);
                }

                DrawScanLengthControl();
                DrawMaxResultsControl();
                DrawMaxPopupDepthControl();
                Checkbox("Only start scans on Japanese characters", () => config.ScanOnlyJapanese, v => config.ScanOnlyJapanese = v);
                Checkbox("Use the personal dictionary", () => config.UsePersonalDictionary, v => config.UsePersonalDictionary = v);
            }
        }

        if (Section("popup-size", "Size & Behavior", defaultOpen: true))
        {
            DrawPopupWidthControl();
            DrawPopupMaxHeightControl();
            DrawPopupFontSizeControl();

            if (!config.FollowHachidoriTheme || !config.FollowHachidoriOpacity)
            {
                DrawPopupOpacityControl();
            }
            else
            {
                ImGui.TextDisabled("Popup opacity is currently controlled by Hachidori's theme (see Theme tab).");
            }

            if (Advanced)
            {
                DrawHideDelayControl();
                Checkbox("Hide the popup when the mouse leaves", () => config.AutoHideRootPopup, v => config.AutoHideRootPopup = v);
            }
        }

        if (Section("popup-display", "Display Elements", defaultOpen: true))
        {
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
        }

        if (Section("popup-audio", "Pronunciation Audio", defaultOpen: false))
        {
            Checkbox("Auto-play pronunciation", () => config.AutoPlayAudio, v => config.AutoPlayAudio = v);
            DrawAudioVolumeControl();
        }
    }

    // -------------------------------------------------------------- Theme tab

    private void DrawThemeTab()
    {
        var scale = ImGuiHelpers.GlobalScale;

        if (Section("theme-renderer", "Popup Renderer (Hachidori Web)", defaultOpen: true))
        {
            DrawUseWebViewControl();

            if (webView is { PlatformSupported: false })
            {
                ImGui.TextColored(AccentCyan, $"Running on {PlatformInfo.Current.DisplayName} — Hachidori Web (WebView2) is Windows-only; automatically using the built-in ImGui renderer with bundled Hachidori themes.");
            }
            else if (!config.UseWebViewPopup)
            {
                ImGui.TextDisabled("Using the legacy ImGui popup renderer.");
            }
            else if (!config.FollowHachidoriTheme)
            {
                ImGui.TextDisabled("Enable \"Use Hachidori's popup theme\" below to render with Hachidori Web.");
            }
            else if (webView is { Active: true })
            {
                var src = string.IsNullOrWhiteSpace(webView.Bundles.ActiveSource) ? "cache" : webView.Bundles.ActiveSource;
                ImGui.TextColored(AccentGreen, $"Active: Hachidori Web v{webView.LoadedVersion} ({src}) — exact upstream themes & CSS.");
            }
            else if (webView is { RuntimeAvailable: false })
            {
                ImGui.TextColored(AccentAmber, "Microsoft Edge WebView2 Runtime is not installed — using built-in ImGui fallback.");
            }
            else if (webView?.Bundles.Syncing == true)
            {
                ImGui.TextColored(AccentCyan, "Syncing Hachidori Web bundle…");
            }
            else if (webView?.Bundles.Ready == true)
            {
                ImGui.TextColored(AccentAmber, $"{webView.StatusText} — using built-in ImGui fallback until ready.");
            }
            else
            {
                ImGui.TextColored(AccentAmber, "Waiting for Hachidori Web bundle — using built-in ImGui fallback until ready.");
            }

            if (webView?.LastError is { Length: > 0 } wvError && config.UseWebViewPopup && webView.PlatformSupported)
                ImGui.TextColored(AccentRed, $"WebView status: {wvError}");

            if (webView is { PlatformSupported: true })
            {
                using (ImRaii.Disabled(webView.Bundles.Syncing))
                {
                    if (ImGui.Button("Sync Hachidori Web now"))
                        _ = webView.SyncBundleNowAsync(force: true);
                }
                if (webView.Bundles.SyncedAt is { } syncedAt)
                {
                    ImGui.SameLine(0, 10 * scale);
                    ImGui.TextDisabled($"Last synced: {Ago(new DateTimeOffset(DateTime.SpecifyKind(syncedAt, DateTimeKind.Utc)))}");
                }
            }
        }

        if (Section("theme-sync", "Hachidori Theme Sync", defaultOpen: true))
        {
            DrawFollowThemeControl();
            if (!config.FollowHachidoriTheme)
            {
                ImGui.TextDisabled("Popups use the plugin's own look.");
                return;
            }

            var hostTheme = service.OptionString("popupTheme", "default");
            var hostVariant = service.OptionString("popupThemeVariant", "auto");
            ImGui.TextDisabled(service.Link.Connected
                ? $"Hachidori uses \"{hostTheme}\"{(hostVariant != "auto" ? $" ({hostVariant})" : string.Empty)}."
                : "Not linked: showing the theme defaults until Hachidori connects.");

            DrawThemeOverrideControl();
            DrawVariantOverrideControl();
        }

        if (Advanced && config.FollowHachidoriTheme)
        {
            if (Section("theme-css", "Opacity & Custom CSS [Advanced]", defaultOpen: true, accent: AccentAmber))
            {
                DrawFollowOpacityControl();
                if (!config.FollowHachidoriOpacity)
                    ImGui.TextDisabled("Uses \"Popup opacity\" from the Popup tab.");
                DrawApplyCustomCssControl();
                DrawExtraCssControl();
            }

            if (Section("theme-bundle", "Hachidori Web Bundle [Advanced]", defaultOpen: false, accent: AccentAmber))
            {
                DrawAutoSyncWebViewControl();
                DrawWebViewBundlePathControl();
                if (webView?.Bundles.ActivePath is { Length: > 0 } activePath)
                    ImGui.TextDisabled($"Active bundle: {activePath}");
                if (webView?.Bundles.LastGoodVersion is { Length: > 0 } lastGood)
                    ImGui.TextDisabled($"Last verified version: v{lastGood}");
            }
        }

        if (webView is { Active: true })
        {
            var effectiveTheme = string.IsNullOrWhiteSpace(config.HachidoriThemeOverride)
                ? service.OptionString("popupTheme", "default")
                : config.HachidoriThemeOverride;
            var effectiveVariant = string.IsNullOrWhiteSpace(config.HachidoriVariantOverride)
                ? service.OptionString("popupThemeVariant", "auto")
                : config.HachidoriVariantOverride;
            var variantNote = !string.IsNullOrWhiteSpace(effectiveVariant) && effectiveVariant != "auto"
                ? $" / {effectiveVariant}"
                : string.Empty;
            ImGui.TextDisabled($"Active look (WebView2): {effectiveTheme}{variantNote}. Changes apply live.");
        }
        else
        {
            var current = PopupTheme.Current;
            if (themes.Error is { } error)
                ImGui.TextColored(AccentRed, $"Theme error: {error}");
            else
                ImGui.TextDisabled($"Active look (ImGui fallback): {current.Renderer}{(current.Renderer == "default" ? $" / {current.Palette}" : string.Empty)}{(current.Variant.Length > 0 && current.Renderer == "crystal" ? $" / {current.Variant}" : string.Empty)}, {(current.Dark ? "dark" : "light")}. Changes apply to the next popup.");
        }
    }

    // -------------------------------------------------------------- Mining tab

    private void DrawMiningTab()
    {
        ImGui.TextWrapped("Media for cards you add with + in a popup, captured from the game itself. No GSM needed.");
        ImGui.Spacing();

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
                            ? "FFmpeg not found: saving GIF instead. Install FFmpeg (GSM includes one) or set its path under Advanced."
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
                        ImGui.TextColored(AccentAmber, "Recording system output audio (compatibility fallback — other apps' audio may be included; lower BGM/other apps for cleaner clips).");
                    else
                        ImGui.TextDisabled("Recording FFXIV game audio only (Windows process loopback).");
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

        if (Section("mining-anki", "AnkiConnect & Field Mapping", defaultOpen: false))
        {
            if (ImGui.Button("Test AnkiConnect"))
            {
                mediaTest = "Checking AnkiConnect…";
                _ = TestAnkiConnectAsync();
            }
            HelpMarker("Voice lines, TTS audio and GIFs are added to the note through the AnkiConnect add-on, after Hachidori creates it.");

            if (Advanced)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("Fields (blank: detected automatically by name)");
                DrawSentenceAudioFieldControl();
                DrawGifFieldControl();
                Toggle("The animation may replace the screenshot", config.GifReplacesScreenshot, v => config.GifReplacesScreenshot = v);
                DrawAnkiConnectUrlControl();
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
        HelpMarker("Translates the mined line with GSM's prompt and settings, using nearby dialogue as context, into the field below.");
        ImGui.SameLine(0, 10 * ImGuiHelpers.GlobalScale);
        if (ImGui.SmallButton("Import from GSM"))
        {
            try { mediaTest = media.Translator.ImportFromGsm(); config.Save(); }
            catch (Exception ex) { mediaTest = $"Import failed: {ex.Message}"; }
        }
        HelpMarker("Copies the AI provider, models, API keys, prompts and Anki field from GameSentenceMiner's active profile.");

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

        ImGui.Spacing();
        ImGui.TextColored(AccentAmber, "Advanced translation parameters");

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
        ImGui.TextWrapped("Read FFXIV dialogue with a controller: move a cursor word by word, see the dictionary, mine with one button. While the mode is on, the controller drives Hachidori and the game ignores it.");
        ImGui.Spacing();

        if (Section("ctrl-activation", "Activation", defaultOpen: true))
        {
            Toggle("Controller reading mode", config.ControllerEnabled, v => config.ControllerEnabled = v);
            if (!config.ControllerEnabled)
                return;

            ImGui.TextWrapped("Best: put a macro with this line on a cross hotbar slot. FFXIV handles the press, so nothing else triggers.");
            ImGui.TextColored(AccentCyan, "/hachidori pad");
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy"))
                ImGui.SetClipboardText("/hachidori pad");

            DrawControllerHoldButtonControl();
            if (config.ControllerHoldButton != GamepadButtons.None)
                DrawControllerHoldSecondsControl();
        }

        if (Section("ctrl-behavior", "Behavior & Display", defaultOpen: true))
        {
            Checkbox("Start in word mode", () => config.ControllerStartInWordMode, v => config.ControllerStartInWordMode = v);
            Checkbox("Leave the mode when the dialogue closes", () => config.ControllerExitWhenDialogueCloses, v => config.ControllerExitWhenDialogueCloses = v);
            Checkbox("Show button hints", () => config.ControllerShowHints, v => config.ControllerShowHints = v);
            Checkbox("PlayStation button names", () => config.ControllerPlayStationLabels, v => config.ControllerPlayStationLabels = v);
            Checkbox("Use DualSense/DS4 touchpad for virtual cursor", () => config.ControllerUseTouchpad, v => config.ControllerUseTouchpad = v);
            HelpMarker("Reads Sony DualSense / DualShock 4 touchpads via Windows HID (primarily Windows). On Steam Deck, the built-in trackpads already glide the virtual cursor automatically when mapped to mouse in Steam Input.");
            if (Advanced)
                DrawControllerCursorSpeedControl();
        }

        if (Advanced && Section("ctrl-binds", "Button Bindings [Advanced]", defaultOpen: false, accent: AccentAmber))
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
            foreach (var (keys, action) in new[]
            {
                ("Left stick", "move virtual cursor over any UI or dialogue text"),
                ("D-pad", "previous / next word and line"),
                ("Right stick", "scroll the popup"),
                (L(config.ControllerMineButton), "add the selected entry to Anki"),
                (L(config.ControllerAudioButton), "play its pronunciation"),
                ($"{L(config.ControllerPrevEntryButton)} / {L(config.ControllerNextEntryButton)}", "previous / next dictionary entry"),
                (L(config.ControllerDiveButton), "move the cursor into the popup to look up a word inside it"),
                (L(config.ControllerWindowButton), "cycle between open UI windows (or back out of a nested popup)"),
                (L(config.ControllerWordModeButton), "switch between words and characters"),
                (L(config.ControllerBackExitButton), "back out of a nested popup, or leave the mode"),
                (ps ? "Select / Options" : "Back / Start", "leave the mode"),
            })
                ImGui.BulletText($"{keys}: {action}");
        }
    }

    // -------------------------------------------------------------- Reader tab

    private void DrawReaderTab()
    {
        if (Section("reader-behavior", "Visibility & Behavior", defaultOpen: true))
        {
            Checkbox("Enable reader overlay", () => config.ReaderEnabled, v => config.ReaderEnabled = v);
            Checkbox("Show only while dialogue is open", () => config.ReaderAutoShowWithDialogue, v => config.ReaderAutoShowWithDialogue = v);
            Checkbox("Show history", () => config.ReaderShowHistory, v => config.ReaderShowHistory = v);
            if (Advanced)
            {
                Checkbox("Lock (no title bar, click-to-move off)", () => config.ReaderLocked, v => config.ReaderLocked = v);
                Checkbox("Hide text until hovered", () => config.ReaderHideUntilHovered, v => config.ReaderHideUntilHovered = v);
            }
        }

        if (Section("reader-layout", "Layout & Appearance", defaultOpen: true))
        {
            DrawReaderAnchorControl();
            DrawReaderFontSizeControl();
            if (Advanced)
            {
                DrawReaderOpacityControl();
                DrawHistorySizeControl();
            }
        }
    }

    // -------------------------------------------------------------- Capture tab

    private void DrawCaptureTab()
    {
        if (Section("capture-sources", "Text Sources", defaultOpen: true))
        {
            Checkbox("Dialogue (Talk, cutscene subtitles)", () => config.CaptureTalk, v => config.CaptureTalk = v);
            Checkbox("Battle talk", () => config.CaptureBattleTalk, v => config.CaptureBattleTalk = v);
            Checkbox("Chat", () => config.CaptureChat, v => config.CaptureChat = v);
        }

        if (Section("capture-playername", "Player Name Pronunciation", defaultOpen: true))
        {
            Checkbox("Read my character's name as katakana", () => config.KatakanaPlayerName, v => config.KatakanaPlayerName = v);
            if (Advanced)
            {
                using (ImRaii.Disabled(!config.KatakanaPlayerName))
                using (ImRaii.PushIndent())
                {
                    DrawPlayerNameKatakanaControl();
                    ImGui.TextDisabled("Blank uses the automatic reading shown. Separate first and last name with ・ or a space.");
                }
            }
        }

        if (Advanced && Section("capture-channels", "Chat Channels [Advanced]", defaultOpen: false, accent: AccentAmber))
        {
            ImGui.TextWrapped("Channel selection applies to reader history and stats. Native chat hover scans the visible chat text.");
            using var disabled = ImRaii.Disabled(!config.CaptureChat);
            using var table = ImRaii.Table("##chat-channels-grid", 2, ImGuiTableFlags.SizingStretchSame);
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
        ImGui.TextWrapped("Sends game text over a WebSocket on this computer so GameSentenceMiner can read it. Off by default; nothing leaves this PC.");
        ImGui.Spacing();

        if (Section("gsm-sources", "Feed Sources", defaultOpen: true))
        {
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
                    ImGui.TextColored(AccentAmber, "Sends other players' messages to GSM.");
            }

            Checkbox("Put the speaker's name before each line", () => config.PublishSpeaker, v => config.PublishSpeaker = v);

            if ((config.PublishTalk && !config.CaptureTalk) || (config.PublishBattleTalk && !config.CaptureBattleTalk) || (config.PublishChat && !config.CaptureChat))
                ImGui.TextColored(AccentAmber, "A selected source is turned off in the Capture tab and won't be sent.");
        }

        if (Section("gsm-server", "WebSocket Server", defaultOpen: true))
        {
            using var disabled = ImRaii.Disabled(!config.PublishEnabled);
            var portIndex = Array.FindIndex(PublishPorts, p => p.Port == config.PublishPort);
            var portNames = PublishPorts.Select(p => $"{p.Name} ({p.Port})").Append("Custom").ToArray();
            var portSelected = portIndex < 0 ? PublishPorts.Length : portIndex;
            if (ImGui.Combo("GSM source slot", ref portSelected, portNames, portNames.Length) && portSelected < PublishPorts.Length)
            {
                config.PublishPort = PublishPorts[portSelected].Port;
                Changed(true);
            }
            HelpMarker("GSM listens to all three slots by default, and the name is only its label. Choose one that no other program you run uses.");

            DrawPublishPortControl();
            ImGui.TextDisabled($"In GSM's WebSocket sources, this is localhost:{config.PublishPort}");

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
                : AccentCyan;
        if (Section("conn-ffxiv-dict", dictConnTitle, defaultOpen: installedFfxivDict is null, accent: dictConnAccent))
        {
            DrawFfxivDictionaryBody(ImGuiHelpers.GlobalScale, installedFfxivDict, "conn");
        }

        if (service.Offline is { } offline && Section("conn-offline", config.OfflineMode ? "Offline Mode  [Enabled]" : "Offline Mode", defaultOpen: true, accent: config.OfflineMode ? AccentGreen : null))
        {
            DrawOfflineModeBody(offline);
        }

        if (Advanced && Section("conn-diag", "Diagnostics [Advanced]", defaultOpen: false, accent: AccentAmber))
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
        if (service.Offline is { } offline)
            return FfxivDictionary.FindInstalledOffline(offline.Store.Manifest.Dictionaries);
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
            ImGui.TextColored(dictionaryNoticeIsError ? AccentAmber : AccentCyan, dictionaryNotice);
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
        HelpMarker("Freezes the screen and captures visible tooltips (ItemDetail, ActionDetail, Tooltip, etc.) so you can move your mouse onto them to look up and mine words. Also available via /hachidori freeze.");
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
        HelpMarker("Runs Hachidori's real unmodified popup frontend in a transparent overlay so all upstream themes, fonts, layouts, custom CSS, and future Hachidori updates appear identically in FFXIV. Automatically falls back to the built-in ImGui popup if WebView2 is unavailable.");
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
        HelpMarker("Automatically matches your connected Hachidori version from your local browser extension or official bee-san/hachidori GitHub Releases, with automatic rollback if an update fails to load.");
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
        HelpMarker("Optional path to an unpacked Hachidori extension folder (containing manifest.json) for testing local themes or unreleased Hachidori builds.");
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
        HelpMarker("Draws popups with the theme chosen in Hachidori (Settings → Design): Default and its colour palettes, Nazeka, Plain, JL, Bee's Theme, Crystal, and any new upstream themes, including your custom popup CSS.");
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
        HelpMarker("Theme colour schemes (e.g. Crystal's Grey, Beige, and Retro blue). Automatically populated from the active Hachidori Web theme.");
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
        if (ImGui.Combo("Hold button to enter", ref index, labels, labels.Length))
        {
            config.ControllerHoldButton = HoldChoices[index];
            config.Save();
        }
        HelpMarker("Holding the button enters the mode. FFXIV still sees that press, so pick a button whose game action you don't mind, or use the macro.");
    }

    private void DrawControllerHoldSecondsControl() =>
        NumberFloat("Hold for (s)", () => config.ControllerHoldSeconds, v => config.ControllerHoldSeconds = v, 0.2f, 1.5f, Defaults.ControllerHoldSeconds, "%.1f", "s", deferSave: config.Save);

    private void DrawControllerCursorSpeedControl() =>
        NumberFloat("Left stick cursor speed", () => config.ControllerCursorSpeed, v => config.ControllerCursorSpeed = v, 300f, 2000f, Defaults.ControllerCursorSpeed, "%.0f", "px/s", deferSave: config.Save);

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
        NumberInt("GSM WebSocket port", () => config.PublishPort, v => { config.PublishPort = v; Changed(true); }, 1, 65535, Defaults.PublishPort);

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
        NumberInt("Relay port", () => config.RelayPort, v => { config.RelayPort = v; Changed(true); }, 1, 65535, Defaults.RelayPort);

    private void DrawApiPortControl() =>
        NumberInt("Yomitan API port (audio)", () => config.ApiPort, v => { config.ApiPort = v; Changed(true); }, 1, 65535, Defaults.ApiPort);

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

    // -------------------------------------------------------------- search catalog

    private void BuildSearchCatalog()
    {
        void Add(string tab, string section, string label, string keywords, bool advanced, Action draw) =>
            catalog.Add(new SettingEntry(tab, section, label, keywords, advanced, draw));

        // Popup
        Add("Popup", "Hover & Scanning", "Hover native game text", "native hover dialogue subtitles scan", false,
            () => Checkbox("Hover native game text", () => config.NativeTextHover, v => config.NativeTextHover = v));
        Add("Popup", "Hover & Scanning", "Include native chat text", "native chat hover logbox", false,
            () => Checkbox("Include native chat text", () => config.NativeChatHover, v => config.NativeChatHover = v));
        Add("Popup", "Hover & Scanning", "Scan key", "modifier shift ctrl alt trigger hotkey", false, DrawScanModifierControl);
        Add("Popup", "Hover & Scanning", "Freeze frame (tooltips)", "freeze frame tooltip item action skill snapshot hotkey", false, DrawFreezeHotkeyControl);
        Add("Popup", "Hover & Scanning", "Hover whole words (tokenizer)", "tokenized hover word segment", true,
            () => Checkbox("Hover whole words (Hachidori tokenizer)", () => config.TokenizedHover, v => config.TokenizedHover = v));
        Add("Popup", "Hover & Scanning", "Other native windows (experimental)", "experimental addons ui windows", true,
            () => Checkbox("Other native windows (experimental)", () => config.NativeOtherTextHover, v => config.NativeOtherTextHover = v));
        Add("Popup", "Hover & Scanning", "Show native hover targets (debug)", "debug boxes overlay hitbox", true,
            () => Checkbox("Show native hover targets (debug)", () => config.NativeHoverDebug, v => config.NativeHoverDebug = v));
        Add("Popup", "Hover & Scanning", "Scan length", "characters max length lookup", true, DrawScanLengthControl);
        Add("Popup", "Hover & Scanning", "Max results", "entries limit count", true, DrawMaxResultsControl);
        Add("Popup", "Hover & Scanning", "Nested popups", "depth recursive inside popup", true, DrawMaxPopupDepthControl);
        Add("Popup", "Hover & Scanning", "Only start scans on Japanese characters", "japanese kana kanji filter", true,
            () => Checkbox("Only start scans on Japanese characters", () => config.ScanOnlyJapanese, v => config.ScanOnlyJapanese = v));
        Add("Popup", "Hover & Scanning", "Use the personal dictionary", "custom personal dict", true,
            () => Checkbox("Use the personal dictionary", () => config.UsePersonalDictionary, v => config.UsePersonalDictionary = v));

        Add("Popup", "Size & Behavior", "Popup width", "size px wide window", false, DrawPopupWidthControl);
        Add("Popup", "Size & Behavior", "Popup max height", "size px tall scroll window", false, DrawPopupMaxHeightControl);
        Add("Popup", "Size & Behavior", "Popup font size", "text scale bigger smaller px", false, DrawPopupFontSizeControl);
        Add("Popup", "Size & Behavior", "Popup opacity", "alpha transparency background", false, DrawPopupOpacityControl);
        Add("Popup", "Size & Behavior", "Hide delay (s)", "close timeout linger seconds", true, DrawHideDelayControl);
        Add("Popup", "Size & Behavior", "Hide the popup when the mouse leaves", "auto hide root close", true,
            () => Checkbox("Hide the popup when the mouse leaves", () => config.AutoHideRootPopup, v => config.AutoHideRootPopup = v));

        Add("Popup", "Display Elements", "Show frequencies", "freq rank jpdb bccwj", false,
            () => Checkbox("Show frequencies", () => config.ShowFrequencies, v => config.ShowFrequencies = v));
        Add("Popup", "Display Elements", "Show pitch accent", "accent downstep heiban odaka", false,
            () => Checkbox("Show pitch accent", () => config.ShowPitchAccent, v => config.ShowPitchAccent = v));
        Add("Popup", "Display Elements", "Show dictionary names", "dict badge label", false,
            () => Checkbox("Show dictionary names", () => config.ShowDictionaryNames, v => config.ShowDictionaryNames = v));
        Add("Popup", "Display Elements", "Show deinflection", "conjugation inflection trace grammar", true,
            () => Checkbox("Show deinflection", () => config.ShowInflections, v => config.ShowInflections = v));
        Add("Popup", "Display Elements", "Show definition tags", "pos tags noun verb", true,
            () => Checkbox("Show definition tags", () => config.ShowDefinitionTags, v => config.ShowDefinitionTags = v));
        Add("Popup", "Display Elements", "Show attribution", "credits copyright source", true,
            () => Checkbox("Show attribution", () => config.ShowAttribution, v => config.ShowAttribution = v));
        Add("Popup", "Display Elements", "Show lookup counts", "statistics times looked up", true,
            () => Checkbox("Show lookup counts (Hachidori lookup statistics)", () => config.ShowLookupCounts, v => config.ShowLookupCounts = v));
        Add("Popup", "Display Elements", "Honour Hachidori's lookup blur", "recall blur hide definition", true,
            () => Checkbox("Honour Hachidori's lookup blur", () => config.HonourDefinitionBlur, v => config.HonourDefinitionBlur = v));

        Add("Popup", "Pronunciation Audio", "Auto-play pronunciation", "audio sound speak automatic", false,
            () => Checkbox("Auto-play pronunciation", () => config.AutoPlayAudio, v => config.AutoPlayAudio = v));
        Add("Popup", "Pronunciation Audio", "Volume", "audio loudness sound", false, DrawAudioVolumeControl);

        // Theme
        Add("Theme", "Popup Renderer (Hachidori Web)", "Render popups with Hachidori Web (WebView2)", "webview2 browser parity upstream themes renderer", false, DrawUseWebViewControl);
        Add("Theme", "Hachidori Theme Sync", "Use Hachidori's popup theme", "crystal nazeka jl bee default appearance style", false, DrawFollowThemeControl);
        Add("Theme", "Hachidori Theme Sync", "Theme override", "palette renderer dark light crystal", false, DrawThemeOverrideControl);
        Add("Theme", "Hachidori Theme Sync", "Colours / variant", "grey beige retro blue crystal scheme", false, DrawVariantOverrideControl);
        Add("Theme", "Opacity & Custom CSS", "Use Hachidori's popup opacity", "alpha transparency theme", true, DrawFollowOpacityControl);
        Add("Theme", "Opacity & Custom CSS", "Apply Hachidori's custom popup CSS", "custom stylesheet userscript", true, DrawApplyCustomCssControl);
        Add("Theme", "Opacity & Custom CSS", "Extra CSS for the game popup", "custom css colors border style", true, DrawExtraCssControl);
        Add("Theme", "Hachidori Web Bundle", "Auto-sync Hachidori Web when Hachidori updates", "webview bundle update github chrome sync", true, DrawAutoSyncWebViewControl);
        Add("Theme", "Hachidori Web Bundle", "Custom Hachidori extension folder", "webview bundle unpacked extension path", true, DrawWebViewBundlePathControl);

        // Mining
        Add("Mining", "Screenshots & Animation", "Screenshot", "picture image capture anki", false, DrawScreenshotToggleControl);
        Add("Mining", "Screenshots & Animation", "Screenshot max width", "resolution size px", true, DrawScreenshotMaxWidthControl);
        Add("Mining", "Screenshots & Animation", "Screenshot JPEG quality", "compression image", true, DrawScreenshotQualityControl);
        Add("Mining", "Screenshots & Animation", "Animation of the line", "gif avif webp video clip", false, DrawGifToggleControl);
        Add("Mining", "Screenshots & Animation", "Animation format", "avif webp gif encoder", false, DrawAnimationFormatControl);
        Add("Mining", "Screenshots & Animation", "Animation width", "gif avif resolution px", true, DrawGifWidthControl);
        Add("Mining", "Screenshots & Animation", "Animation FPS", "frames per second gif avif", true, DrawGifFpsControl);
        Add("Mining", "Screenshots & Animation", "Animation length (s)", "duration seconds clip", true, DrawGifSecondsControl);
        Add("Mining", "Screenshots & Animation", "AVIF quality (CRF)", "avif crf compression", true, DrawAvifCrfControl);
        Add("Mining", "Screenshots & Animation", "WebP quality", "webp compression", true, DrawWebpQualityControl);
        Add("Mining", "Screenshots & Animation", "FFmpeg path", "ffmpeg binary exe encoder", true, DrawFfmpegPathControl);

        Add("Mining", "Voice Recording & TTS", "Voice line from the game", "record audio wasapi game sound", false, DrawVoiceRecordToggleControl);
        Add("Mining", "Voice Recording & TTS", "Voice detection", "ratio bgm music threshold", true, DrawVoiceRatioControl);
        Add("Mining", "Voice Recording & TTS", "Longest voice line (s)", "max clip duration seconds", true, DrawMaxLineSecondsControl);
        Add("Mining", "Voice Recording & TTS", "Voice ring buffer (s)", "audio memory buffer history seconds", true, DrawVoiceBufferSecondsControl);
        Add("Mining", "Voice Recording & TTS", "TTS for unvoiced lines", "voicevox aivisspeech speech synthesis", false, DrawTtsModeControl);
        Add("Mining", "Voice Recording & TTS", "TTS engine address / URL", "voicevox url port endpoint", false, DrawTtsUrlControl);
        Add("Mining", "Voice Recording & TTS", "TTS speaker / voice", "voicevox speaker id", false, DrawTtsSpeakerControl);

        Add("Mining", "Sentence Translation", "Sentence translation", "ai llm translate gemini groq openai deepl claude codex", false,
            () => Toggle("Sentence translation", config.TranslationEnabled, v => config.TranslationEnabled = v));
        Add("Mining", "Sentence Translation", "Translation provider", "gemini groq openai ollama lmstudio deepl codex claude", false, DrawTranslationProviderControl);
        Add("Mining", "Sentence Translation", "Translation Anki field", "anki field SentenceTranslation", false, DrawTranslationFieldControl);
        Add("Mining", "Sentence Translation", "Translate into language", "target language english", true, DrawTranslationLanguageControl);
        Add("Mining", "Sentence Translation", "Dialogue context lines", "context window history lines", true, DrawDialogueContextControl);
        Add("Mining", "Sentence Translation", "Temperature", "llm sampling creativity", true, DrawTemperatureControl);
        Add("Mining", "Sentence Translation", "Top P", "llm nucleus sampling", true, DrawTopPControl);
        Add("Mining", "Sentence Translation", "Max output tokens", "llm token limit", true, DrawMaxTokensControl);

        Add("Mining", "AnkiConnect & Field Mapping", "Sentence audio field", "anki field name sound", true, DrawSentenceAudioFieldControl);
        Add("Mining", "AnkiConnect & Field Mapping", "GIF field", "anki field name picture image", true, DrawGifFieldControl);
        Add("Mining", "AnkiConnect & Field Mapping", "Animation may replace screenshot", "gif overwrite picture", true,
            () => Toggle("The animation may replace the screenshot", config.GifReplacesScreenshot, v => config.GifReplacesScreenshot = v));
        Add("Mining", "AnkiConnect & Field Mapping", "AnkiConnect address", "url port 8765 anki", true, DrawAnkiConnectUrlControl);

        // Controller
        Add("Controller", "Activation", "Controller reading mode", "gamepad pad xbox playstation dualsense", false,
            () => Toggle("Controller reading mode", config.ControllerEnabled, v => config.ControllerEnabled = v));
        Add("Controller", "Activation", "Hold button to enter", "select start l3 r3 trigger", false, DrawControllerHoldButtonControl);
        Add("Controller", "Activation", "Hold for (s)", "duration seconds hold", false, DrawControllerHoldSecondsControl);
        Add("Controller", "Behavior & Display", "Start in word mode", "word character snap", false,
            () => Checkbox("Start in word mode", () => config.ControllerStartInWordMode, v => config.ControllerStartInWordMode = v));
        Add("Controller", "Behavior & Display", "Leave mode when dialogue closes", "auto exit talk", false,
            () => Checkbox("Leave the mode when the dialogue closes", () => config.ControllerExitWhenDialogueCloses, v => config.ControllerExitWhenDialogueCloses = v));
        Add("Controller", "Behavior & Display", "Show button hints", "hud overlay prompts", false,
            () => Checkbox("Show button hints", () => config.ControllerShowHints, v => config.ControllerShowHints = v));
        Add("Controller", "Behavior & Display", "PlayStation button names", "cross circle square triangle xbox", false,
            () => Checkbox("PlayStation button names", () => config.ControllerPlayStationLabels, v => config.ControllerPlayStationLabels = v));
        Add("Controller", "Behavior & Display", "DualSense/DS4 touchpad cursor", "touchpad virtual cursor ps5 ps4", false,
            () => Checkbox("Use DualSense/DS4 touchpad for virtual cursor", () => config.ControllerUseTouchpad, v => config.ControllerUseTouchpad = v));
        Add("Controller", "Behavior & Display", "Left stick cursor speed", "sensitivity speed px", true, DrawControllerCursorSpeedControl);

        // Reader
        Add("Reader", "Visibility & Behavior", "Enable reader overlay", "texthooker window dialogue", false,
            () => Checkbox("Enable reader overlay", () => config.ReaderEnabled, v => config.ReaderEnabled = v));
        Add("Reader", "Visibility & Behavior", "Show only while dialogue is open", "auto show hide talk", false,
            () => Checkbox("Show only while dialogue is open", () => config.ReaderAutoShowWithDialogue, v => config.ReaderAutoShowWithDialogue = v));
        Add("Reader", "Visibility & Behavior", "Show history", "backlog scroll lines", false,
            () => Checkbox("Show history", () => config.ReaderShowHistory, v => config.ReaderShowHistory = v));
        Add("Reader", "Visibility & Behavior", "Lock reader window", "no title bar click move", true,
            () => Checkbox("Lock (no title bar, click-to-move off)", () => config.ReaderLocked, v => config.ReaderLocked = v));
        Add("Reader", "Visibility & Behavior", "Hide text until hovered", "reveal hover invisible", true,
            () => Checkbox("Hide text until hovered", () => config.ReaderHideUntilHovered, v => config.ReaderHideUntilHovered = v));
        Add("Reader", "Layout & Appearance", "Reader position", "anchor above below free", false, DrawReaderAnchorControl);
        Add("Reader", "Layout & Appearance", "Reader font size", "text scale px", false, DrawReaderFontSizeControl);
        Add("Reader", "Layout & Appearance", "Reader opacity", "alpha transparency background", true, DrawReaderOpacityControl);
        Add("Reader", "Layout & Appearance", "History size", "backlog line limit count", true, DrawHistorySizeControl);

        // Capture
        Add("Capture", "Text Sources", "Capture dialogue & subtitles", "talk cutscene msq", false,
            () => Checkbox("Dialogue (Talk, cutscene subtitles)", () => config.CaptureTalk, v => config.CaptureTalk = v));
        Add("Capture", "Text Sources", "Capture battle talk", "boss combat dialogue", false,
            () => Checkbox("Battle talk", () => config.CaptureBattleTalk, v => config.CaptureBattleTalk = v));
        Add("Capture", "Text Sources", "Capture chat", "chat channels log", false,
            () => Checkbox("Chat", () => config.CaptureChat, v => config.CaptureChat = v));
        Add("Capture", "Player Name Pronunciation", "Read character's name as katakana", "player name furigana katakana", false,
            () => Checkbox("Read my character's name as katakana", () => config.KatakanaPlayerName, v => config.KatakanaPlayerName = v));
        Add("Capture", "Player Name Pronunciation", "Custom katakana name", "override player name reading", true, DrawPlayerNameKatakanaControl);

        // GSM
        Add("GSM", "Feed Sources", "Publish text for GSM", "gamesentenceminer websocket texthooker feed", true,
            () => Checkbox("Publish text for GSM", () => config.PublishEnabled, v => config.PublishEnabled = v));
        Add("GSM", "WebSocket Server", "GSM WebSocket port", "textractor lunatranslator agent 6677", true, DrawPublishPortControl);

        // Connection
        Add("Connection", "Hachidori Relay & Host", "Relay host", "ip address localhost 127.0.0.1", true, DrawRelayHostControl);
        Add("Connection", "Hachidori Relay & Host", "Relay port", "8771 websocket relay", true, DrawRelayPortControl);
        Add("Connection", "Hachidori Relay & Host", "Yomitan API port (audio)", "19633 audio pronunciation port", true, DrawApiPortControl);
        Add("Connection", "Hachidori Relay & Host", "Anki Template ID", "template deck model", true, DrawAnkiTemplateIdControl);
        Add("Connection", "Hachidori Relay & Host", "{document-title}", "anki document title ffxiv", true, DrawDocumentTitleControl);
        Add("Connection", "Custom FFXIV Dictionary", "Download custom FFXIV dictionary", "ffxiv dictionary download zip portraits lingo yomitan hachidori custom glossary", false, () =>
            DrawFfxivDictionaryBody(ImGuiHelpers.GlobalScale, FindInstalledFfxivDictionary(), "search"));
        Add("Connection", "Offline Mode", "Offline mode", "local dictionaries sync hdw_native", false, () =>
        {
            if (service.Offline is { } offline)
                DrawOfflineModeBody(offline);
        });
    }

    // -------------------------------------------------------------- reusable UI primitives

    /// <summary>Styled collapsible dropdown section with clear hierarchy and subtle accenting.</summary>
    private static bool Section(string id, string title, bool defaultOpen = true, Vector4? accent = null)
    {
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        using var headerBg = ImRaii.PushColor(ImGuiCol.Header, new Vector4(0.18f, 0.19f, 0.25f, 0.85f));
        using var headerHov = ImRaii.PushColor(ImGuiCol.HeaderHovered, new Vector4(0.24f, 0.25f, 0.33f, 0.95f));
        using var headerAct = ImRaii.PushColor(ImGuiCol.HeaderActive, new Vector4(0.28f, 0.29f, 0.39f, 1.0f));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 5f * ImGuiHelpers.GlobalScale);
        using var textCol = accent is { } a ? ImRaii.PushColor(ImGuiCol.Text, a) : null;

        ImGui.Spacing();
        var open = ImGui.CollapsingHeader($"{title}###sec-{id}", flags);
        if (open)
            ImGui.Spacing();
        return open;
    }

    /// <summary>Dual Slider + Direct Numeric Input for ints, with right-click reset to default.</summary>
    private void NumberInt(string label, Func<int> get, Action<int> set, int min, int max, int defaultValue, string? help = null, Action? deferSave = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var totalW = Math.Max(180 * scale, ImGui.CalcItemWidth());
        var spacing = 6 * scale;
        var inputW = Math.Clamp(totalW * 0.30f, 64 * scale, 100 * scale);
        var sliderW = Math.Max(80 * scale, totalW - inputW - spacing);

        var val = get();
        var changed = false;
        var deactivated = false;
        var reset = false;

        using (ImRaii.PushId(label))
        {
            ImGui.SetNextItemWidth(sliderW);
            if (ImGui.SliderInt("##s", ref val, min, max))
                changed = true;
            deactivated |= ImGui.IsItemDeactivatedAfterEdit();
            reset |= ImGui.IsItemClicked(ImGuiMouseButton.Right);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Drag or type a value ({min}–{max}). Default: {defaultValue} (right-click to reset).");

            ImGui.SameLine(0, spacing);
            ImGui.SetNextItemWidth(inputW);
            if (ImGui.InputInt("##i", ref val, 0, 0))
                changed = true;
            deactivated |= ImGui.IsItemDeactivatedAfterEdit();
            reset |= ImGui.IsItemClicked(ImGuiMouseButton.Right);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Type exact number ({min}–{max}). Default: {defaultValue} (right-click to reset).");

            ImGui.SameLine(0, 8 * scale);
            ImGui.TextUnformatted(label);
        }

        if (help is not null)
            HelpMarker(help);

        if (reset)
        {
            set(defaultValue);
            deferSave?.Invoke();
        }
        else if (changed)
        {
            set(Math.Clamp(val, min, max));
        }

        if (deactivated && deferSave is not null)
            deferSave();
    }

    /// <summary>Dual Slider + Direct Numeric Input for floats, with right-click reset to default.</summary>
    private void NumberFloat(string label, Func<float> get, Action<float> set, float min, float max, float defaultValue, string format = "%.2f", string? unit = null, string? help = null, Action? deferSave = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var totalW = Math.Max(180 * scale, ImGui.CalcItemWidth());
        var spacing = 6 * scale;
        var inputW = Math.Clamp(totalW * 0.30f, 64 * scale, 100 * scale);
        var sliderW = Math.Max(80 * scale, totalW - inputW - spacing);

        var val = get();
        var changed = false;
        var deactivated = false;
        var reset = false;
        var sliderFormat = unit is not null ? $"{format} {unit}" : format;
        var defaultText = defaultValue.ToString(format.Contains(".0") ? "0" : format.Contains(".1") ? "0.0" : "0.00", CultureInfo.InvariantCulture);

        using (ImRaii.PushId(label))
        {
            ImGui.SetNextItemWidth(sliderW);
            if (ImGui.SliderFloat("##s", ref val, min, max, sliderFormat))
                changed = true;
            deactivated |= ImGui.IsItemDeactivatedAfterEdit();
            reset |= ImGui.IsItemClicked(ImGuiMouseButton.Right);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Drag or type a value ({min:0.##}–{max:0.##}). Default: {defaultText}{(unit is not null ? $" {unit}" : string.Empty)} (right-click to reset).");

            ImGui.SameLine(0, spacing);
            ImGui.SetNextItemWidth(inputW);
            if (ImGui.InputFloat("##i", ref val, 0, 0, format))
                changed = true;
            deactivated |= ImGui.IsItemDeactivatedAfterEdit();
            reset |= ImGui.IsItemClicked(ImGuiMouseButton.Right);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Type exact number ({min:0.##}–{max:0.##}). Default: {defaultText} (right-click to reset).");

            ImGui.SameLine(0, 8 * scale);
            ImGui.TextUnformatted(label);
        }

        if (help is not null)
            HelpMarker(help);

        if (reset)
        {
            set(defaultValue);
            deferSave?.Invoke();
        }
        else if (changed)
        {
            set(Math.Clamp(val, min, max));
        }

        if (deactivated && deferSave is not null)
            deferSave();
    }

    private void Changed(bool changed)
    {
        if (!changed)
            return;
        config.Save();
        fonts.Refresh();
        service.ClearCache();
        webView?.NotifySettingsChanged();
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

    private bool Toggle(string label, bool value, Action<bool> set)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            config.Save();
        }
        return value;
    }

    private static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(360 * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private static string TruncateToWidth(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth || text.Length <= 4)
            return text;
        var len = text.Length - 1;
        while (len > 3 && ImGui.CalcTextSize(text[..len] + "…").X > maxWidth)
            len--;
        return text[..len] + "…";
    }

    // -------------------------------------------------------------- async tests

    private async Task TestTranslationAsync()
    {
        try
        {
            var result = await media.Translator.TranslateAsync("冒険者さん、ちょっと手を貸してもらえないかな？",
                ["この辺りで魔物が増えているらしい。"], "村人", "FINAL FANTASY XIV").ConfigureAwait(false);
            mediaTest = $"Translation works: {result}";
        }
        catch (Exception ex)
        {
            mediaTest = $"Translation failed: {ex.Message}";
        }
    }

    private async Task TestTtsAsync()
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

    private async Task TestAnkiConnectAsync()
    {
        try
        {
            var version = await media.Anki.InvokeAsync("version").ConfigureAwait(false);
            ankiConnectedHint = true;
            mediaTest = $"AnkiConnect works (version {version}).";
        }
        catch (Exception ex)
        {
            ankiConnectedHint = false;
            mediaTest = ex.Message;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private static string Ago(DateTimeOffset? time)
    {
        if (time is not { } t) return "never";
        var age = DateTimeOffset.UtcNow - t;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : t.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}
