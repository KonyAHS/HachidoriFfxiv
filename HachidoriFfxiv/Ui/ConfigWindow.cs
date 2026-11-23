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
    private readonly GamepadNavigator? gamepad;
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
    private bool customPublishSources;
    private bool customPublishPort;
    private bool ankiConnectedHint;
    private bool firstDraw = true;
    private bool recordingControllerKey;
    private readonly bool[] recorderPrevKeys = new bool[256];

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
        Action? triggerFreezeFrame = null,
        GamepadNavigator? gamepad = null)
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
        this.gamepad = gamepad;

        Size = new Vector2(660, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 440),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        BuildSearchCatalog();
    }

    private const string ReaderTab = "Reader & Text";

    private bool Advanced => config.AdvancedSettings;

    public override void OnClose()
    {
        recordingControllerKey = false;
        if (gamepad is not null)
            gamepad.SuppressToggleKey = false;
    }

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
        DrawTab("Popup", () => { config.ResetPopupDefaults(); config.ResetThemeDefaults(); Changed(true); }, DrawPopupTab, scale);
        DrawTab("Mining", () => { config.ResetMiningDefaults(); config.Save(); }, DrawMiningTab, scale);
        DrawTab(ReaderTab, () => { config.ResetReaderDefaults(); config.ResetCaptureDefaults(); Changed(true); }, DrawReaderTab, scale);
        DrawTab("Controller", () => { config.ResetControllerDefaults(); config.Save(); }, DrawControllerTab, scale);
        DrawTab("Connection", () => { config.ResetConnectionDefaults(); Changed(true); }, DrawConnectionTab, scale);
        if (Advanced || config.PublishEnabled || pendingSelectTab == "GSM")
            DrawTab("GSM", () => { config.ResetPublishDefaults(); Changed(true); }, DrawPublishTab, scale);

        pendingSelectTab = null;
    }

    // -------------------------------------------------------------- header & search

    private void DrawHeaderBar(float scale)
    {
        var link = service.Link;
        var pos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var bannerH = ImGui.GetFrameHeight();
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

        ImGui.SetCursorScreenPos(pos + new Vector2(0, ImGui.GetStyle().FramePadding.Y));
        ImGui.TextColored(dotCol, "●");
        ImGui.SameLine(0, 6 * scale);

        var buttonsW = 248 * scale;
        var textMaxW = Math.Max(100 * scale, avail - buttonsW - 32 * scale);
        ImGui.TextUnformatted(TruncateToWidth(statusText, textMaxW));

        // Quick window launchers on the right side of the status strip
        ImGui.SetCursorScreenPos(new Vector2(pos.X + avail - buttonsW, pos.Y + ImGui.GetStyle().FramePadding.Y));
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
        var advWidth = ImGui.CalcTextSize("Show advanced").X + 40 * scale;
        var clearW = searchQuery.Length > 0 ? 28 * scale : 0f;
        var searchW = Math.Max(140 * scale, avail - advWidth - clearW - (clearW > 0 ? 12 * scale : 8 * scale));

        ImGui.SetNextItemWidth(searchW);
        ImGui.InputTextWithHint("##settings-search", "Search settings…", ref searchQuery, 128);
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
        if (ImGui.Checkbox("Show advanced", ref advanced))
        {
            config.AdvancedSettings = advanced;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show fine-tuning options in every tab. Search always finds every setting.");
    }

    private void DrawSearchResults(float scale)
    {
        var tokens = searchQuery.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = catalog.Where(item => MatchesAll(item, tokens)).ToList();

        ImGui.TextDisabled($"{matches.Count} matching setting{(matches.Count == 1 ? string.Empty : "s")}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Back to tabs"))
            searchQuery = string.Empty;
        ImGui.Spacing();

        using var scroll = ImRaii.Child("##search-results-scroll", Vector2.Zero, false);
        if (!scroll.Success)
            return;

        if (matches.Count == 0)
        {
            ImGui.TextDisabled($"No settings match “{searchQuery}”.");
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
                ImGui.TextUnformatted(group);
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
                entry.DrawControl();
                if (entry.IsAdvanced && !Advanced)
                {
                    ImGui.SameLine(0, 8 * scale);
                    ImGui.TextDisabled("advanced");
                }
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

        var footerH = resetAction is not null ? ImGui.GetFrameHeightWithSpacing() + 6 * scale : 0f;
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
            var confirming = confirmResetTab == name;
            using var col = ImRaii.PushColor(ImGuiCol.Button, confirming ? new Vector4(0.75f, 0.25f, 0.25f, 0.95f) : new Vector4(0.22f, 0.22f, 0.27f, 0.9f));
            var btnLabel = confirming ? "Click again to confirm" : $"Reset {name} to defaults";
            if (ImGui.SmallButton($"{btnLabel}##reset-{name}"))
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
            if (ImGui.IsItemHovered() && !confirming)
                ImGui.SetTooltip("Right-click any slider to reset just that value.");
        }
    }

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
                    else if (Advanced)
                        ImGui.TextDisabled(media.Voices.HooksActive
                            ? "Recording FFXIV audio only · native voice detection active."
                            : "Recording FFXIV audio only · using volume-based voice detection.");
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

        if (service.Offline is { } offline && Section("conn-offline", config.OfflineMode ? "Offline Mode  [Enabled]" : "Offline Mode", defaultOpen: true, accent: config.OfflineMode ? AccentGreen : null))
        {
            DrawOfflineModeBody(offline);
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

    // -------------------------------------------------------------- search catalog

    private void BuildSearchCatalog()
    {
        void Add(string tab, string section, string label, string keywords, bool advanced, Action draw) =>
            catalog.Add(new SettingEntry(tab, section, label, keywords, advanced, draw));

        // Popup
        Add("Popup", "Hover & Scanning", "Hover game text", "native hover dialogue subtitles scan", false,
            () => Checkbox("Hover game text", () => config.NativeTextHover, v => config.NativeTextHover = v));
        Add("Popup", "Hover & Scanning", "Include chat", "native chat hover logbox", false,
            () => Checkbox("Include chat", () => config.NativeChatHover, v => config.NativeChatHover = v));
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

        Add("Popup", "Popup Contents", "Frequencies", "show freq rank jpdb bccwj", false,
            () => Checkbox("Frequencies", () => config.ShowFrequencies, v => config.ShowFrequencies = v));
        Add("Popup", "Popup Contents", "Pitch accent", "show accent downstep heiban odaka", false,
            () => Checkbox("Pitch accent", () => config.ShowPitchAccent, v => config.ShowPitchAccent = v));
        Add("Popup", "Popup Contents", "Dictionary names", "show dict badge label", false,
            () => Checkbox("Dictionary names", () => config.ShowDictionaryNames, v => config.ShowDictionaryNames = v));
        Add("Popup", "Popup Contents", "Deinflection", "show conjugation inflection trace grammar", true,
            () => Checkbox("Deinflection", () => config.ShowInflections, v => config.ShowInflections = v));
        Add("Popup", "Popup Contents", "Definition tags", "show pos tags noun verb", true,
            () => Checkbox("Definition tags", () => config.ShowDefinitionTags, v => config.ShowDefinitionTags = v));
        Add("Popup", "Popup Contents", "Attribution", "show credits copyright source", true,
            () => Checkbox("Attribution", () => config.ShowAttribution, v => config.ShowAttribution = v));
        Add("Popup", "Popup Contents", "Lookup counts", "show statistics times looked up", true,
            () => Checkbox("Lookup counts (Hachidori statistics)", () => config.ShowLookupCounts, v => config.ShowLookupCounts = v));
        Add("Popup", "Popup Contents", "Honour Hachidori's lookup blur", "recall blur hide definition", true,
            () => Checkbox("Honour Hachidori's lookup blur", () => config.HonourDefinitionBlur, v => config.HonourDefinitionBlur = v));

        Add("Popup", "Pronunciation Audio", "Auto-play pronunciation", "audio sound speak automatic", false,
            () => Checkbox("Auto-play pronunciation", () => config.AutoPlayAudio, v => config.AutoPlayAudio = v));
        Add("Popup", "Pronunciation Audio", "Volume", "audio loudness sound", false, DrawAudioVolumeControl);

        // Theme
        Add("Popup", "Appearance", "Render popups with Hachidori Web (WebView2)", "webview2 browser parity upstream themes renderer", false, DrawUseWebViewControl);
        Add("Popup", "Appearance", "Use Hachidori's popup theme", "crystal nazeka jl bee default appearance style", false, DrawFollowThemeControl);
        Add("Popup", "Appearance", "Theme override", "palette renderer dark light crystal", false, DrawThemeOverrideControl);
        Add("Popup", "Appearance", "Colours / variant", "grey beige retro blue crystal scheme", false, DrawVariantOverrideControl);
        Add("Popup", "Appearance", "Use Hachidori's popup opacity", "alpha transparency theme", true, DrawFollowOpacityControl);
        Add("Popup", "Appearance", "Apply Hachidori's custom popup CSS", "custom stylesheet userscript", true, DrawApplyCustomCssControl);
        Add("Popup", "Appearance", "Extra CSS for the game popup", "custom css colors border style", true, DrawExtraCssControl);
        Add("Popup", "Appearance", "Auto-sync Hachidori Web when Hachidori updates", "webview bundle update github chrome sync", true, DrawAutoSyncWebViewControl);
        Add("Popup", "Appearance", "Custom Hachidori extension folder", "webview bundle unpacked extension path", true, DrawWebViewBundlePathControl);

        // Mining
        Add("Mining", "Notifications", "Show notification when a card is created", "anki notification toast popup card created mined", false, DrawAnkiNotifyToggleControl);
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
        Add("Mining", "Voice Recording & TTS", "Mix current playing OST into TTS", "ost bgm music tts inject background dragon", false, DrawTtsInjectOstControl);
        Add("Mining", "Voice Recording & TTS", "OST background level", "ost bgm music volume mix tts", false, DrawTtsOstVolumeControl);
        Add("Mining", "Voice Recording & TTS", "Skip OST when game BGM is muted", "ost bgm mute silent tts", true, DrawTtsOstRespectMuteControl);

        Add("Mining", "Sentence Translation", "Sentence translation", "ai llm translate gemini groq openai deepl claude codex", false,
            () => Toggle("Sentence translation", config.TranslationEnabled, v => config.TranslationEnabled = v));
        Add("Mining", "Sentence Translation", "Translation provider", "gemini groq openai ollama lmstudio deepl codex claude", false, DrawTranslationProviderControl);
        Add("Mining", "Sentence Translation", "Translation Anki field", "anki field SentenceTranslation", false, DrawTranslationFieldControl);
        Add("Mining", "Sentence Translation", "Translate into language", "target language english", true, DrawTranslationLanguageControl);
        Add("Mining", "Sentence Translation", "Dialogue context lines", "context window history lines", true, DrawDialogueContextControl);
        Add("Mining", "Sentence Translation", "Temperature", "llm sampling creativity", true, DrawTemperatureControl);
        Add("Mining", "Sentence Translation", "Top P", "llm nucleus sampling", true, DrawTopPControl);
        Add("Mining", "Sentence Translation", "Max output tokens", "llm token limit", true, DrawMaxTokensControl);

        Add("Mining", "AnkiConnect & Fields", "Sentence audio field", "anki field name sound", true, DrawSentenceAudioFieldControl);
        Add("Mining", "AnkiConnect & Fields", "GIF field", "anki field name picture image", true, DrawGifFieldControl);
        Add("Mining", "AnkiConnect & Fields", "Animation may replace screenshot", "gif overwrite picture", true,
            () => Toggle("The animation may replace the screenshot", config.GifReplacesScreenshot, v => config.GifReplacesScreenshot = v));
        Add("Mining", "AnkiConnect & Fields", "AnkiConnect address", "url port 8765 anki", true, DrawAnkiConnectUrlControl);

        // Controller
        Add("Controller", "Activation", "Controller reading mode", "gamepad pad xbox playstation dualsense", false,
            () => Toggle("Controller reading mode", config.ControllerEnabled, v => config.ControllerEnabled = v));
        Add("Controller", "Activation", "Controller trigger", "select start l3 r3 trigger hold press toggle", false, DrawControllerHoldButtonControl);
        Add("Controller", "Activation", "Trigger style (hold / press)", "hold press instant toggle extra button paddle", false, DrawControllerTriggerModeControl);
        Add("Controller", "Activation", "Hold for (s)", "duration seconds hold", false, DrawControllerHoldSecondsControl);
        Add("Controller", "Activation", "Toggle keybind", "keyboard hotkey keybind press f13 paddle extra button controller", false, DrawControllerToggleKeyControl);
        Add("Controller", "Behavior & Display", "Scan words automatically under the cursor", "auto scan manual button press popup lookup immediately", false,
            () => Checkbox("Scan words automatically under the cursor", () => config.ControllerAutoScan, v => config.ControllerAutoScan = v));
        Add("Controller", "Behavior & Display", "Look up button", "manual scan button press lookup popup controller", false, DrawControllerScanButtonControl);
        Add("Controller", "Behavior & Display", "Start in word mode", "word character snap", false,
            () => Checkbox("Start in word mode", () => config.ControllerStartInWordMode, v => config.ControllerStartInWordMode = v));
        Add("Controller", "Behavior & Display", "Leave mode when dialogue closes", "auto exit talk", false,
            () => Checkbox("Leave the mode when the dialogue closes", () => config.ControllerExitWhenDialogueCloses, v => config.ControllerExitWhenDialogueCloses = v));
        Add("Controller", "Behavior & Display", "Show button hints", "hud overlay prompts", false,
            () => Checkbox("Show button hints", () => config.ControllerShowHints, v => config.ControllerShowHints = v));
        Add("Controller", "Behavior & Display", "PlayStation button names", "cross circle square triangle xbox", false,
            () => Checkbox("PlayStation button names", () => config.ControllerPlayStationLabels, v => config.ControllerPlayStationLabels = v));
        Add("Controller", "Behavior & Display", "DualSense/DS4 touchpad cursor", "touchpad virtual cursor ps5 ps4", false,
            () => Checkbox("Move the cursor with the DualSense/DS4 touchpad", () => config.ControllerUseTouchpad, v => config.ControllerUseTouchpad = v));
        Add("Controller", "Behavior & Display", "Left stick cursor speed", "sensitivity speed px", true, DrawControllerCursorSpeedControl);

        // Reader
        Add(ReaderTab, "Reader Overlay", "Enable reader overlay", "texthooker window dialogue", false,
            () => Checkbox("Enable reader overlay", () => config.ReaderEnabled, v => config.ReaderEnabled = v));
        Add(ReaderTab, "Reader Overlay", "Show only while dialogue is open", "auto show hide talk", false,
            () => Checkbox("Show only while dialogue is open", () => config.ReaderAutoShowWithDialogue, v => config.ReaderAutoShowWithDialogue = v));
        Add(ReaderTab, "Reader Overlay", "Show history", "backlog scroll lines", false,
            () => Checkbox("Show history", () => config.ReaderShowHistory, v => config.ReaderShowHistory = v));
        Add(ReaderTab, "Reader Overlay", "Lock reader window", "no title bar click move", true,
            () => Checkbox("Lock (no title bar, click-to-move off)", () => config.ReaderLocked, v => config.ReaderLocked = v));
        Add(ReaderTab, "Reader Overlay", "Hide text until hovered", "reveal hover invisible", true,
            () => Checkbox("Hide text until hovered", () => config.ReaderHideUntilHovered, v => config.ReaderHideUntilHovered = v));
        Add(ReaderTab, "Reader Overlay", "Reader position", "anchor above below free", false, DrawReaderAnchorControl);
        Add(ReaderTab, "Reader Overlay", "Reader font size", "text scale px", false, DrawReaderFontSizeControl);
        Add(ReaderTab, "Reader Overlay", "Reader opacity", "alpha transparency background", true, DrawReaderOpacityControl);
        Add(ReaderTab, "Reader Overlay", "History size", "backlog line limit count", true, DrawHistorySizeControl);

        // Capture
        Add(ReaderTab, "Captured Text", "Capture dialogue & cutscenes", "talk cutscene subtitles msq", false,
            () => Checkbox("Dialogue & cutscenes", () => config.CaptureTalk, v => config.CaptureTalk = v));
        Add(ReaderTab, "Captured Text", "Capture battle talk", "boss combat dialogue", false,
            () => Checkbox("Battle talk", () => config.CaptureBattleTalk, v => config.CaptureBattleTalk = v));
        Add(ReaderTab, "Captured Text", "Capture chat", "chat channels log", false,
            () => Checkbox("Chat", () => config.CaptureChat, v => config.CaptureChat = v));
        Add(ReaderTab, "Captured Text", "Read character's name as katakana", "player name furigana katakana", false,
            () => Checkbox("Read my character's name as katakana", () => config.KatakanaPlayerName, v => config.KatakanaPlayerName = v));
        Add(ReaderTab, "Captured Text", "Custom katakana name", "override player name reading", true, DrawPlayerNameKatakanaControl);

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

    /// <summary>Collapsible section; the optional accent colours the title to show a status (ready, needs action).</summary>
    private static bool Section(string id, string title, bool defaultOpen = true, Vector4? accent = null)
    {
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        using var textCol = accent is { } a ? ImRaii.PushColor(ImGuiCol.Text, a) : null;

        ImGui.Spacing();
        var open = ImGui.CollapsingHeader($"{title}###sec-{id}", flags);
        if (open)
            ImGui.Spacing();
        return open;
    }

    /// <summary>Small amber "Advanced" label that separates fine-tuning options inside a section.</summary>
    private static void AdvancedDivider()
    {
        ImGui.Spacing();
        ImGui.TextColored(AccentAmber, "Advanced");
    }

    /// <summary>Slider for ints (Ctrl+click to type), with right-click reset to default.</summary>
    private void NumberInt(string label, Func<int> get, Action<int> set, int min, int max, int defaultValue, string? help = null, Action? deferSave = null)
    {
        var val = get();
        ImGui.SetNextItemWidth(Math.Max(180 * ImGuiHelpers.GlobalScale, ImGui.CalcItemWidth()));
        var changed = ImGui.SliderInt(label, ref val, min, max);
        var deactivated = ImGui.IsItemDeactivatedAfterEdit();
        var reset = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Ctrl+click to type ({min}–{max}). Right-click resets to {defaultValue}.");

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

    /// <summary>Slider for floats (Ctrl+click to type), with right-click reset to default.</summary>
    private void NumberFloat(string label, Func<float> get, Action<float> set, float min, float max, float defaultValue, string format = "%.2f", string? unit = null, string? help = null, Action? deferSave = null)
    {
        var val = get();
        var sliderFormat = unit is not null ? $"{format} {unit}" : format;
        var defaultText = defaultValue.ToString(format.Contains(".0") ? "0" : format.Contains(".1") ? "0.0" : "0.00", CultureInfo.InvariantCulture);

        ImGui.SetNextItemWidth(Math.Max(180 * ImGuiHelpers.GlobalScale, ImGui.CalcItemWidth()));
        var changed = ImGui.SliderFloat(label, ref val, min, max, sliderFormat);
        var deactivated = ImGui.IsItemDeactivatedAfterEdit();
        var reset = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Ctrl+click to type ({min:0.##}–{max:0.##}). Right-click resets to {defaultText}{(unit is not null ? $" {unit}" : string.Empty)}.");

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

    /// <summary>Plain number box for ports and other values a slider makes no sense for.</summary>
    private void PortInput(string label, Func<int> get, Action<int> set, int defaultValue)
    {
        var val = get();
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt(label, ref val, 0, 0) && val is >= 1 and <= 65535)
            set(val);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            set(defaultValue);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Right-click resets to {defaultValue}.");
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
            var (audio, ext, mixedOst, bgm) = await media.TestTtsAsync("冒険者さん、こんにちは。").ConfigureAwait(false);
            if (mixedOst && bgm is not null)
            {
                var mins = (int)(bgm.ElapsedSeconds / 60);
                var secs = (int)(bgm.ElapsedSeconds % 60);
                mediaTest = $"TTS + OST works: {audio.Length / 1024} KB of {ext} audio (mixed with {bgm.DisplayName} @ {mins}:{secs:00}).";
            }
            else
            {
                mediaTest = $"TTS works: {audio.Length / 1024} KB of {ext} audio.";
            }
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
