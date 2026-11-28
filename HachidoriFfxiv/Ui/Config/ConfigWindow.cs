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
using HachidoriFfxiv.Offline;
using HachidoriFfxiv.Publishing;
using HachidoriFfxiv.Rendering.Css;
using HachidoriFfxiv.WebView;

namespace HachidoriFfxiv.Ui;

public sealed partial class ConfigWindow : Window, IDisposable
{
    private static readonly Configuration Defaults = new();

    // Unified StatsWindow palette aliases
    private static readonly Vector4 AccentGreen = ConfigStyle.Good;
    private static readonly Vector4 AccentAmber = ConfigStyle.Emphasis;
    private static readonly Vector4 AccentRed = ConfigStyle.Bad;
    private static readonly Vector4 MutedText = ConfigStyle.Muted;

    private static readonly XivChatType[] SelectableChannels =
    [
        XivChatType.NPCDialogue, XivChatType.NPCDialogueAnnouncements, XivChatType.Say, XivChatType.Shout,
        XivChatType.Yell, XivChatType.Party, XivChatType.Alliance, XivChatType.FreeCompany, XivChatType.TellIncoming,
        XivChatType.Ls1, XivChatType.CrossLinkShell1, XivChatType.Echo, XivChatType.SystemMessage,
    ];

    private static readonly (string Name, bool Talk, bool Battle, bool Popups, bool Chat)[] PublishPresets =
    [
        ("Dialogue only", true, true, false, false),
        ("Dialogue & popups (recommended)", true, true, true, false),
        ("Everything including chat", true, true, true, true),
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

    private static readonly GamepadButtons[] QuickReplayChoices =
    [
        GamepadButtons.None,
        GamepadButtons.R3,
        GamepadButtons.L3,
        GamepadButtons.L1,
        GamepadButtons.R1,
        GamepadButtons.L2,
        GamepadButtons.R2,
        GamepadButtons.Select,
        GamepadButtons.L1 | GamepadButtons.R1,
        GamepadButtons.L2 | GamepadButtons.R2,
        GamepadButtons.L3 | GamepadButtons.R3,
        GamepadButtons.L1 | GamepadButtons.R3,
        GamepadButtons.R1 | GamepadButtons.R3,
        GamepadButtons.Select | GamepadButtons.R3,
        GamepadButtons.Select | GamepadButtons.DpadDown,
    ];

    private enum KeybindRecordTarget
    {
        None,
        ControllerToggle,
        ReplayVoice,
    }

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
    private readonly OfflineMode? offline;
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
    private readonly Action? clearStats;
    private readonly ConnectionDoctor doctor;
    private readonly AnkiFieldMapper ankiMapper;
    private readonly List<SettingEntry> catalog = new();

    private string searchQuery = string.Empty;
    private string? pendingSelectTab;
    private string? confirmResetTab;
    private bool confirmResetSetup;
    private bool confirmClearStats;
    private string? resetNotice;
    private DateTime resetNoticeUntil = DateTime.MinValue;
    private string? mediaTest;
    private string? exportedLog;
    private string? dictionaryNotice;
    private bool dictionaryNoticeIsError;
    private DateTime dictionaryNoticeUntil = DateTime.MinValue;
    private string zipPathInput = string.Empty;
    private bool ffmpegDownloading;
    private string? ffmpegDownloadStatus;
    private bool confirmRemoveOffline;
    private bool customPublishSources;
    private bool customPublishPort;
    private bool firstDraw = true;
    private KeybindRecordTarget recordingKeyTarget;
    private readonly bool[] recorderPrevKeys = new bool[256];

    private const string SetupTab = "Setup";
    private const string ReaderTab = "Reader";

    public ConfigWindow(
        Configuration config,
        HachidoriService service,
        OfflineMode? offline,
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
        GamepadNavigator? gamepad = null,
        Action? clearStats = null)
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
        this.clearStats = clearStats;
        doctor = new ConnectionDoctor(config, service, offline, media, webView);
        ankiMapper = new AnkiFieldMapper(config, service, media, () => Changed(true));

        Size = new Vector2(720, 660);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 460),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        BuildSearchCatalog();
    }

    private bool Advanced => config.AdvancedSettings;
    private DictionarySource ActiveSource => doctor.EffectiveSource();

    public override void OnClose()
    {
        recordingKeyTarget = KeybindRecordTarget.None;
        if (gamepad is not null)
            gamepad.SuppressToggleKey = false;
    }

    public void Dispose() => doctor.Dispose();

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;

        if (firstDraw)
        {
            firstDraw = false;
            if (!config.QuickGuideDismissed && !service.Link.Connected && !service.UsingOffline)
                pendingSelectTab = SetupTab;
            else
                pendingSelectTab = "Popup";
        }

        DrawHeaderBar(scale);
        DrawSearchAndToolbar(scale);

        if (resetNotice is not null && DateTime.UtcNow < resetNoticeUntil)
            ImGui.TextColored(ConfigStyle.Good, resetNotice);
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

        DrawTab(SetupTab, null, DrawSetupTab, scale);
        DrawTab("Popup", () => { config.ResetPopupDefaults(); config.ResetThemeDefaults(); Changed(true); }, DrawPopupTab, scale);
        DrawTab("Mining", () => { config.ResetMiningDefaults(); config.Save(); }, DrawMiningTab, scale);
        DrawTab("Performance", () => { config.ResetPerformanceDefaults(); media.ApplyPerformanceSettings(); Changed(true); }, DrawPerformanceTab, scale);
        DrawTab("Controller", () => { config.ResetControllerDefaults(); config.Save(); }, DrawControllerTab, scale);
        DrawTab(ReaderTab, () => { config.ResetReaderDefaults(); config.ResetCaptureDefaults(); Changed(true); }, DrawReaderTab, scale);
        DrawTab("Connection", () => { config.ResetConnectionDefaults(); Changed(true); }, DrawConnectionTab, scale);

        pendingSelectTab = null;
    }

    // -------------------------------------------------------------- header & search

    private void DrawHeaderBar(float scale)
    {
        var link = service.Link;
        var pos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var bannerH = ImGui.GetFrameHeight();

        Vector4 pillCol;
        string pillText;
        string statusText;
        if (!service.UsingOffline && link.Connected)
        {
            pillCol = ConfigStyle.Primary;
            pillText = "CONNECTED";
            statusText = $"Hachidori v{link.HostVersion} · {link.DictionaryCount} dicts";
        }
        else if (service.UsingOffline && offline is { } off)
        {
            pillCol = ConfigStyle.Secondary;
            pillText = "BUILT-IN";
            statusText = $"{off.Store.Manifest.Dictionaries.Count} local dict{(off.Store.Manifest.Dictionaries.Count == 1 ? "" : "s")} ready";
        }
        else
        {
            pillCol = ConfigStyle.Emphasis;
            pillText = "SETUP";
            statusText = "Choose a dictionary source in Setup";
        }

        ImGui.SetCursorScreenPos(pos + new Vector2(0, 1f * scale));
        ConfigStyle.StatusPill(pillText, pillCol);
        ImGui.SameLine(0, 8 * scale);

        var buttonsW = 248 * scale;
        var textMaxW = Math.Max(80 * scale, avail - buttonsW - 110 * scale);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(TruncateToWidth(statusText, textMaxW));

        ImGui.SetCursorScreenPos(new Vector2(pos.X + avail - buttonsW, pos.Y + ImGui.GetStyle().FramePadding.Y));
        if (openStats is not null && ImGui.SmallButton("Stats")) openStats();
        if (openStats is not null) ImGui.SameLine(0, 4 * scale);
        if (openSearchSample is not null && ImGui.SmallButton("Search")) openSearchSample(null);
        if (openSearchSample is not null) ImGui.SameLine(0, 4 * scale);
        if (openReader is not null && ImGui.SmallButton("Reader")) openReader();
        if (openReader is not null) ImGui.SameLine(0, 4 * scale);
        if (openPictures is not null && ImGui.SmallButton("Pictures")) openPictures();

        ImGui.SetCursorScreenPos(pos + new Vector2(0, bannerH + 6 * scale));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawSearchAndToolbar(float scale)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var advWidth = ImGui.CalcTextSize("Show advanced").X + 40 * scale;
        var clearW = searchQuery.Length > 0 ? 28 * scale : 0f;
        var searchW = Math.Max(140 * scale, avail - advWidth - clearW - (clearW > 0 ? 12 * scale : 8 * scale));

        ImGui.SetNextItemWidth(searchW);
        ImGui.InputTextWithHint("##settings-search", "Search all settings…", ref searchQuery, 128);
        if (clearW > 0)
        {
            ImGui.SameLine(0, 4 * scale);
            if (ImGui.Button("×##clear-search", new Vector2(clearW, 0)))
                searchQuery = string.Empty;
        }

        ImGui.SameLine(0, 8 * scale);
        var advanced = config.AdvancedSettings;
        if (ImGui.Checkbox("Show advanced", ref advanced))
        {
            config.AdvancedSettings = advanced;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show fine-tuning controls in every tab. Search always finds every setting.");
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
                ImGui.TextColored(ConfigStyle.Primary, group);
                ImGui.SameLine(0, 10 * scale);
                using (ImRaii.PushId($"jump-{i}"))
                {
                    if (ImGui.SmallButton($"Open {entry.Tab}"))
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
                    ImGui.TextColored(ConfigStyle.Emphasis, "advanced");
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
            var btnLabel = confirming ? "Click again to confirm" : $"Reset {name} defaults";
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
                ImGui.SetTooltip("Right-click any slider to reset just that single value.");
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
        Add("Popup", "Hover & Scanning", "Freeze tooltips hotkey", "freeze frame tooltip item action skill snapshot", false, DrawFreezeHotkeyControl);
        Add("Popup", "Hover & Scanning", "Whole-word hover (tokenizer)", "tokenized hover word segment", true,
            () => Checkbox("Whole-word hover (tokenizer)", () => config.TokenizedHover, v => config.TokenizedHover = v));
        Add("Popup", "Hover & Scanning", "Other native windows", "experimental addons ui windows", true,
            () => Checkbox("Other native windows (experimental)", () => config.NativeOtherTextHover, v => config.NativeOtherTextHover = v));
        Add("Popup", "Hover & Scanning", "Show hover target boxes (debug)", "debug boxes overlay hitbox", true,
            () => Checkbox("Show hover target boxes (debug)", () => config.NativeHoverDebug, v => config.NativeHoverDebug = v));
        Add("Popup", "Hover & Scanning", "Scan length", "characters max length lookup", true, DrawScanLengthControl);
        Add("Popup", "Hover & Scanning", "Max results", "entries limit count", true, DrawMaxResultsControl);
        Add("Popup", "Hover & Scanning", "Nested popups", "depth recursive inside popup", true, DrawMaxPopupDepthControl);
        Add("Popup", "Hover & Scanning", "Japanese characters only", "japanese kana kanji filter", true,
            () => Checkbox("Only start scans on Japanese characters", () => config.ScanOnlyJapanese, v => config.ScanOnlyJapanese = v));
        Add("Popup", "Hover & Scanning", "Personal dictionary", "custom personal dict", true,
            () => Checkbox("Use personal dictionary", () => config.UsePersonalDictionary, v => config.UsePersonalDictionary = v));

        Add("Popup", "Look & Renderer", "Popup renderer", "webview2 browser imgui theme renderer", false, DrawPopupRendererControl);
        Add("Popup", "Look & Renderer", "Theme override", "palette crystal nazeka jl bee default", false, DrawThemeOverrideControl);
        Add("Popup", "Look & Renderer", "Color variant", "grey beige retro blue crystal scheme", false, DrawVariantOverrideControl);
        Add("Popup", "Look & Renderer", "Popup width", "size px wide window", false, DrawPopupWidthControl);
        Add("Popup", "Look & Renderer", "Popup max height", "size px tall scroll window", false, DrawPopupMaxHeightControl);
        Add("Popup", "Look & Renderer", "Popup font size", "text scale bigger smaller px", false, DrawPopupFontSizeControl);
        Add("Popup", "Look & Renderer", "Popup opacity", "alpha transparency background", true, DrawPopupOpacityControl);
        Add("Popup", "Look & Renderer", "Hide delay", "close timeout linger seconds", true, DrawHideDelayControl);
        Add("Popup", "Look & Renderer", "Auto-hide on mouse leave", "auto hide root close", true,
            () => Checkbox("Hide popup when mouse leaves", () => config.AutoHideRootPopup, v => config.AutoHideRootPopup = v));
        Add("Popup", "Look & Renderer", "Extra popup CSS", "custom css colors border style", true, DrawExtraCssControl);

        Add("Popup", "Contents & Audio", "Frequencies", "show freq rank jpdb bccwj", false,
            () => Checkbox("Frequencies", () => config.ShowFrequencies, v => config.ShowFrequencies = v));
        Add("Popup", "Contents & Audio", "Pitch accent", "show accent downstep heiban odaka", false,
            () => Checkbox("Pitch accent", () => config.ShowPitchAccent, v => config.ShowPitchAccent = v));
        Add("Popup", "Contents & Audio", "Dictionary names", "show dict badge label", false,
            () => Checkbox("Dictionary names", () => config.ShowDictionaryNames, v => config.ShowDictionaryNames = v));
        Add("Popup", "Contents & Audio", "Auto-play audio", "audio sound speak pronunciation automatic", false,
            () => Checkbox("Auto-play audio", () => config.AutoPlayAudio, v => config.AutoPlayAudio = v));
        Add("Popup", "Contents & Audio", "Audio volume", "audio loudness sound", false, DrawAudioVolumeControl);
        Add("Popup", "Contents & Audio", "Deinflection", "conjugation trace grammar", true,
            () => Checkbox("Deinflection trace", () => config.ShowInflections, v => config.ShowInflections = v));
        Add("Popup", "Contents & Audio", "Definition tags", "pos tags noun verb", true,
            () => Checkbox("Definition tags", () => config.ShowDefinitionTags, v => config.ShowDefinitionTags = v));
        Add("Popup", "Contents & Audio", "Lookup counts", "statistics times looked up", true,
            () => Checkbox("Lookup counts", () => config.ShowLookupCounts, v => config.ShowLookupCounts = v));
        Add("Popup", "Contents & Audio", "Lookup blur", "recall blur hide definition", true,
            () => Checkbox("Honour lookup blur", () => config.HonourDefinitionBlur, v => config.HonourDefinitionBlur = v));

        // Mining
        Add("Mining", "Card Media", "Visual capture mode", "screenshot picture animation gif avif webp video", false, DrawVisualCaptureControl);
        Add("Mining", "Card Media", "Sentence audio mode", "record voice wasapi tts voicevox aivisspeech audio", false, DrawSentenceAudioControl);
        Add("Mining", "Card Media", "Toast on mine", "anki notification toast card created", false, DrawAnkiNotifyToggleControl);
        Add("Mining", "Card Media", "Screenshot max width", "resolution size px", true, DrawScreenshotMaxWidthControl);
        Add("Mining", "Card Media", "Screenshot JPEG quality", "compression image", true, DrawScreenshotQualityControl);
        Add("Mining", "Card Media", "FFmpeg path", "ffmpeg binary exe encoder", true, DrawFfmpegPathControl);
        Add("Mining", "Card Media", "Voice detection ratio", "bgm music threshold", true, DrawVoiceRatioControl);
        Add("Mining", "Card Media", "Longest voice line", "max clip duration seconds", true, DrawMaxLineSecondsControl);
        Add("Mining", "Card Media", "TTS address", "voicevox url port endpoint", false, DrawTtsUrlControl);
        Add("Mining", "Card Media", "TTS speaker", "voicevox speaker id", false, DrawTtsSpeakerControl);
        Add("Mining", "Card Media", "Mix OST into TTS", "ost bgm music tts inject", false, DrawTtsInjectOstControl);
        Add("Mining", "Card Media", "OST background level", "ost bgm volume mix tts", false, DrawTtsOstVolumeControl);
        Add("Mining", "Sentence Translation", "Enable AI translation", "ai llm translate gemini groq openai deepl claude codex", false,
            () => Toggle("Enable AI sentence translation", config.TranslationEnabled, v => config.TranslationEnabled = v));
        Add("Mining", "Sentence Translation", "Translation provider", "gemini groq openai ollama lmstudio deepl codex claude", false, DrawTranslationProviderControl);
        Add("Mining", "GSM Bridge", "Publish text for GSM", "gamesentenceminer websocket texthooker feed 6677", false,
            () => Checkbox("Publish text to GSM", () => config.PublishEnabled, v => config.PublishEnabled = v));
        Add("Mining", "AnkiConnect", "AnkiConnect address", "url port 8765 anki", true, DrawAnkiConnectUrlControl);
        Add("Mining", "AnkiConnect", "AnkiConnect API key", "anki key auth token", true, DrawAnkiConnectApiKeyControl);

        // Performance
        Add("Performance", "Quick Tuning", "Clip encoding mode", "lazy eager on-demand ffmpeg cpu animation", false, DrawClipEncodingModeControl);
        Add("Performance", "Quick Tuning", "Cache line screenshots", "memory cache backlog jpeg screenshots", false,
            () => Toggle("Cache line screenshots", config.CacheLineScreenshots, v => { config.CacheLineScreenshots = v; media.ApplyPerformanceSettings(); }));
        Add("Performance", "Quick Tuning", "Cache voice replays", "memory cache backlog replay audio", false,
            () => Toggle("Cache voice replays", config.CacheReplayAudio, v => { config.CacheReplayAudio = v; media.ApplyPerformanceSettings(); }));
        Add("Performance", "Cache Sizes & Buffers", "Lookup cache size", "memory cache lookup results", true, DrawLookupCacheSizeControl);
        Add("Performance", "Cache Sizes & Buffers", "Word segment cache size", "memory cache tokenizer segment", true, DrawSegmentCacheSizeControl);
        Add("Performance", "Cache Sizes & Buffers", "Pronunciation audio cache", "memory cache audio pronunciation", true, DrawAudioCacheSizeControl);
        Add("Performance", "Cache Sizes & Buffers", "Max cached screenshots", "memory cache screenshots count", true, DrawMaxCachedScreenshotsOnlyControl);
        Add("Performance", "Cache Sizes & Buffers", "Max cached voice replays", "memory cache replay audio count", true, DrawMaxCachedReplayAudioOnlyControl);
        Add("Performance", "Cache Sizes & Buffers", "Cached clips count", "memory cache animation clips", true, DrawMaxCachedClipsControl);
        Add("Performance", "Cache Sizes & Buffers", "Parallel FFmpeg encodes", "cpu threads concurrent ffmpeg", true, DrawMaxConcurrentEncodesControl);
        Add("Performance", "Cache Sizes & Buffers", "Voice ring buffer (s)", "audio memory buffer history seconds", true, DrawVoiceBufferSecondsControl);
        Add("Performance", "Cache Sizes & Buffers", "Animation width", "gif avif resolution px", true, DrawGifWidthControl);
        Add("Performance", "Cache Sizes & Buffers", "Animation FPS", "frames per second gif avif", true, DrawGifFpsControl);
        Add("Performance", "Cache Sizes & Buffers", "Animation length (s)", "duration seconds clip", true, DrawGifSecondsControl);
        Add("Performance", "Cache Sizes & Buffers", "Line history size", "backlog line limit count", true, DrawHistorySizeControl);

        // Controller
        Add("Controller", "Activation", "Controller reading mode", "gamepad pad xbox playstation dualsense", false,
            () => Toggle("Enable controller reading mode", config.ControllerEnabled, v => config.ControllerEnabled = v));
        Add("Controller", "Activation", "Controller trigger", "select start l3 r3 hold press toggle", false, DrawControllerHoldButtonControl);
        Add("Controller", "Activation", "Toggle keybind", "keyboard hotkey f13 paddle", true, DrawControllerToggleKeyControl);
        Add("Controller", "Voice Repeat", "Cutscene repeat button", "replay repeat voice cutscene gamepad r3 l3", false, DrawControllerQuickReplayButtonControl);
        Add("Controller", "Voice Repeat", "Repeat trigger style", "hold press repeat voice", false, DrawControllerQuickReplayTriggerModeControl);
        Add("Controller", "Voice Repeat", "Repeat line keybind", "keyboard hotkey repeat replay voice", true, DrawReplayVoiceKeyControl);
        Add("Controller", "Behavior", "Auto-scan under cursor", "auto scan manual button lookup", false,
            () => Checkbox("Auto-scan under cursor", () => config.ControllerAutoScan, v => config.ControllerAutoScan = v));
        Add("Controller", "Behavior", "PlayStation labels", "cross circle square triangle xbox", false,
            () => Checkbox("PlayStation labels", () => config.ControllerPlayStationLabels, v => config.ControllerPlayStationLabels = v));
        Add("Controller", "Behavior", "DualSense touchpad", "touchpad virtual cursor ps5 ps4", false,
            () => Checkbox("DualSense/DS4 touchpad", () => config.ControllerUseTouchpad, v => config.ControllerUseTouchpad = v));

        // Reader
        Add(ReaderTab, "Reader Overlay", "Enable reader overlay", "texthooker window dialogue", false,
            () => Checkbox("Enable reader overlay", () => config.ReaderEnabled, v => config.ReaderEnabled = v));
        Add(ReaderTab, "Reader Overlay", "Reader position", "anchor above below free", false, DrawReaderAnchorControl);
        Add(ReaderTab, "Reader Overlay", "Reader font size", "text scale px", false, DrawReaderFontSizeControl);
        Add(ReaderTab, "Captured Text", "Katakana character name", "player name furigana katakana", false,
            () => Checkbox("Read character name as katakana", () => config.KatakanaPlayerName, v => config.KatakanaPlayerName = v));

        // Connection
        Add("Connection", "Hachidori Extension", "Relay host", "ip address localhost 127.0.0.1", true, DrawRelayHostControl);
        Add("Connection", "Hachidori Extension", "Relay port", "8771 websocket relay", true, DrawRelayPortControl);
        Add("Connection", "Hachidori Extension", "Audio API port", "19633 audio pronunciation port", true, DrawApiPortControl);
        Add("Connection", "Hachidori Extension", "{document-title}", "anki document title ffxiv", true, DrawDocumentTitleControl);
    }

    private void DrawMaxCachedScreenshotsOnlyControl() =>
        NumberInt("Max cached screenshots", () => config.MaxCachedScreenshots, v => config.MaxCachedScreenshots = v, 0, 100, Defaults.MaxCachedScreenshots,
            deferSave: () => { config.PerformancePreset = PerformancePresets.Detect(config); config.Save(); media.ApplyPerformanceSettings(); });

    private void DrawMaxCachedReplayAudioOnlyControl() =>
        NumberInt("Max cached voice replays", () => config.MaxCachedReplayAudio, v => config.MaxCachedReplayAudio = v, 0, 100, Defaults.MaxCachedReplayAudio,
            deferSave: () => { config.PerformancePreset = PerformancePresets.Detect(config); config.Save(); media.ApplyPerformanceSettings(); });

    // -------------------------------------------------------------- reusable UI primitives

    private static bool Section(string id, string title, bool defaultOpen = true, Vector4? accent = null)
    {
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        using var textCol = accent is { } a ? ImRaii.PushColor(ImGuiCol.Text, a) : null;
        ImGui.Spacing();
        var open = ImGui.CollapsingHeader($"{title}###sec-{id}", flags);
        if (open) ImGui.Spacing();
        return open;
    }

    private static void AdvancedDivider()
    {
        ConfigStyle.SubHeading("Advanced", ConfigStyle.Emphasis);
    }

    private void NumberInt(string label, Func<int> get, Action<int> set, int min, int max, int defaultValue, string? help = null, Action? deferSave = null)
    {
        var val = get();
        ImGui.SetNextItemWidth(Math.Max(180 * ImGuiHelpers.GlobalScale, Math.Min(260 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.55f)));
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

    private void NumberFloat(string label, Func<float> get, Action<float> set, float min, float max, float defaultValue, string format = "%.2f", string? unit = null, string? help = null, Action? deferSave = null)
    {
        var val = get();
        var sliderFormat = unit is not null ? $"{format} {unit}" : format;
        var defaultText = defaultValue.ToString(format.Contains(".0") ? "0" : format.Contains(".1") ? "0.0" : "0.00", CultureInfo.InvariantCulture);

        ImGui.SetNextItemWidth(Math.Max(180 * ImGuiHelpers.GlobalScale, Math.Min(260 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.55f)));
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
        config.PerformancePreset = PerformancePresets.Detect(config);
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
            config.PerformancePreset = PerformancePresets.Detect(config);
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

    // -------------------------------------------------------------- async helpers

    private void StartBrowseZipImport()
    {
        if (offline is null || offline.Importing)
            return;
        _ = Task.Run(async () =>
        {
            var picked = FilePicker.PickZipFiles();
            if (picked.Count > 0)
            {
                await offline.ImportZipsAsync(picked).ConfigureAwait(false);
                service.ClearCache();
                doctor.RunNow();
            }
        });
    }

    private void StartManagedFfmpegDownload()
    {
        if (ffmpegDownloading)
            return;
        ffmpegDownloading = true;
        ffmpegDownloadStatus = "Starting FFmpeg download…";
        var dir = DalamudServices.PluginInterface.GetPluginConfigDirectory();
        _ = Task.Run(async () =>
        {
            try
            {
                var exe = await Ffmpeg.DownloadManagedAsync(dir, msg => ffmpegDownloadStatus = msg).ConfigureAwait(false);
                config.FfmpegPath = exe;
                config.Save();
                media.RefreshFfmpegPath();
                ffmpegDownloadStatus = "FFmpeg installed and ready.";
                doctor.RunNow();
            }
            catch (Exception ex)
            {
                ffmpegDownloadStatus = $"FFmpeg download failed: {ex.Message}";
            }
            finally
            {
                ffmpegDownloading = false;
            }
        });
    }

    private async Task TestTranslationAsync()
    {
        try
        {
            var result = await media.Translator.TranslateAsync("冒険者さん、ちょっと手を貸してもらえないかな？",
                ["この辺りで魔物が増えているらしい。"], "村人", "FINAL FANTASY XIV").ConfigureAwait(false);
            mediaTest = $"Translation OK: {result}";
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
                mediaTest = $"TTS + OST OK: {audio.Length / 1024} KB {ext} ({bgm.DisplayName} @ {mins}:{secs:00}).";
            }
            else
            {
                mediaTest = $"TTS OK: {audio.Length / 1024} KB {ext}.";
            }
        }
        catch (Exception ex)
        {
            mediaTest = $"TTS failed: {ex.Message}";
        }
    }

    private static string FormatBytes(long bytes) => ConfigStyle.FormatBytes(bytes);

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
