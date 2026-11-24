using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.Text;
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

public sealed partial class ConfigWindow : Window
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
    private readonly Offline.OfflineMode? offline;
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
        Offline.OfflineMode? offline,
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
        this.offline = offline;
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
        else if (service.UsingOffline && offline is { } off)
        {
            dotCol = AccentAmber;
            statusText = $"Offline mode · {off.Store.Manifest.Dictionaries.Count} local dicts (synced {Ago(off.Store.Manifest.SyncedAt)})";
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
        Add("Mining", "AnkiConnect & Fields", "AnkiConnect API key", "anki key auth token", true, DrawAnkiConnectApiKeyControl);

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
            if (offline is { } off)
                DrawOfflineModeBody(off);
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

    private void Toggle(string label, bool value, Action<bool> set)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            config.Save();
        }
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
