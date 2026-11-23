using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Game.Text;

namespace HachidoriFfxiv;

public enum ScanModifier
{
    None,
    Shift,
    Ctrl,
    Alt,
}

public enum ReaderAnchor
{
    Free,
    AboveTalk,
    BelowTalk,
}

public enum FreezeHotkey
{
    CtrlShiftF,
    AltF,
    CtrlSpace,
    Pause,
    F9,
    ShiftMiddleClick,
    None,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Connection to Hachidori (through the Hachidori Relay add-on running inside Anki).
    public string RelayHost { get; set; } = "127.0.0.1";
    public int RelayPort { get; set; } = 8771;
    public int ApiPort { get; set; } = 19633;

    // Offline mode: a local copy of Hachidori's dictionaries and settings, used
    // only while Hachidori is not linked. Off until set up in the settings.
    public bool OfflineMode { get; set; } = false;

    // Scanning
    public ScanModifier ScanModifier { get; set; } = ScanModifier.None;
    public int ScanLength { get; set; } = 16;
    public int MaxResults { get; set; } = 16;
    public bool ScanOnlyJapanese { get; set; } = true;
    public int MaxPopupDepth { get; set; } = 4;
    public float HideDelaySeconds { get; set; } = 0.25f;
    public bool AutoHideRootPopup { get; set; } = true;
    public bool UsePersonalDictionary { get; set; } = true;
    public bool NativeTextHover { get; set; } = true;
    public bool NativeChatHover { get; set; } = true;
    public bool NativeOtherTextHover { get; set; } = false;
    public bool NativeHoverDebug { get; set; } = false;
    public bool TokenizedHover { get; set; } = true;
    public FreezeHotkey FreezeFrameHotkey { get; set; } = FreezeHotkey.CtrlShiftF;

    // Popup appearance
    public float PopupWidth { get; set; } = 480f;
    public float PopupMaxHeight { get; set; } = 460f;
    public float PopupFontSize { get; set; } = 18f;
    public float PopupBackgroundAlpha { get; set; } = 0.97f;
    public bool ShowFrequencies { get; set; } = true;
    public bool ShowPitchAccent { get; set; } = true;
    public bool ShowInflections { get; set; } = true;
    public bool ShowDefinitionTags { get; set; } = true;
    public bool ShowDictionaryNames { get; set; } = true;
    public bool ShowAttribution { get; set; } = false;
    public bool ShowLookupCounts { get; set; } = true;
    public bool HonourDefinitionBlur { get; set; } = true;
    // Hachidori popup themes: the popup follows the theme, colours and custom CSS chosen in Hachidori.
    public bool FollowHachidoriTheme { get; set; } = true;
    public bool UseWebViewPopup { get; set; } = true;
    public bool AutoSyncWebViewBundle { get; set; } = true;
    public string WebViewBundlePath { get; set; } = string.Empty;
    public bool FollowHachidoriOpacity { get; set; } = true;
    public bool ApplyHachidoriCustomCss { get; set; } = true;
    public string HachidoriThemeOverride { get; set; } = string.Empty;
    public string HachidoriVariantOverride { get; set; } = string.Empty;
    public string ExtraPopupCss { get; set; } = string.Empty;

    public bool AutoPlayAudio { get; set; } = false;
    public float AudioVolume { get; set; } = 0.8f;

    // Reader overlay
    public bool ReaderEnabled { get; set; } = false;
    public bool ReaderAutoShowWithDialogue { get; set; } = true;
    public ReaderAnchor ReaderAnchor { get; set; } = ReaderAnchor.AboveTalk;
    public float ReaderFontSize { get; set; } = 26f;
    public float ReaderBackgroundAlpha { get; set; } = 0.75f;
    public bool ReaderShowHistory { get; set; } = false;
    public int HistorySize { get; set; } = 100;
    public bool ReaderLocked { get; set; } = false;
    public bool ReaderHideUntilHovered { get; set; } = true;

    // Text capture
    public bool KatakanaPlayerName { get; set; } = true;
    public string PlayerNameKatakana { get; set; } = string.Empty;
    public bool CaptureTalk { get; set; } = true;
    public bool CaptureBattleTalk { get; set; } = true;
    public bool CaptureChat { get; set; } = true;
    public HashSet<XivChatType> ChatChannels { get; set; } = new()
    {
        XivChatType.NPCDialogue,
        XivChatType.NPCDialogueAnnouncements,
        XivChatType.Say,
        XivChatType.Shout,
        XivChatType.Yell,
        XivChatType.Party,
        XivChatType.FreeCompany,
        XivChatType.TellIncoming,
    };

    // Text feed for GameSentenceMiner (off unless the user enables it)
    public bool PublishEnabled { get; set; } = false;
    public int PublishPort { get; set; } = 6677;
    public bool PublishTalk { get; set; } = true;
    public bool PublishBattleTalk { get; set; } = true;
    public bool PublishPopups { get; set; } = true;
    public bool PublishChat { get; set; } = false;
    public bool PublishSpeaker { get; set; } = false;

    // Controller reading mode (/hachidori pad, or hold a button).
    public bool ControllerEnabled { get; set; } = true;
    public Dalamud.Game.ClientState.GamePad.GamepadButtons ControllerHoldButton { get; set; } = Dalamud.Game.ClientState.GamePad.GamepadButtons.Select;
    public float ControllerHoldSeconds { get; set; } = 0.6f;
    public bool ControllerStartInWordMode { get; set; } = true;
    public bool ControllerExitWhenDialogueCloses { get; set; } = true;
    public bool ControllerShowHints { get; set; } = true;
    public bool ControllerPlayStationLabels { get; set; } = false;
    public bool ControllerUseTouchpad { get; set; } = false;
    public float ControllerCursorSpeed { get; set; } = 900f;
    public Dalamud.Game.ClientState.GamePad.GamepadButtons ControllerMineButton { get; set; } = Dalamud.Game.ClientState.GamePad.GamepadButtons.South;
    public Dalamud.Game.ClientState.GamePad.GamepadButtons ControllerAudioButton { get; set; } = Dalamud.Game.ClientState.GamePad.GamepadButtons.West;
    public Dalamud.Game.ClientState.GamePad.GamepadButtons ControllerWordModeButton { get; set; } = Dalamud.Game.ClientState.GamePad.GamepadButtons.North;
    public Dalamud.Game.ClientState.GamePad.GamepadButtons ControllerBackExitButton { get; set; } = Dalamud.Game.ClientState.GamePad.GamepadButtons.East;
    public Dalamud.Game.ClientState.GamePad.GamepadButtons ControllerPrevEntryButton { get; set; } = Dalamud.Game.ClientState.GamePad.GamepadButtons.L2;
    public Dalamud.Game.ClientState.GamePad.GamepadButtons ControllerNextEntryButton { get; set; } = Dalamud.Game.ClientState.GamePad.GamepadButtons.R2;
    public Dalamud.Game.ClientState.GamePad.GamepadButtons ControllerDiveButton { get; set; } = Dalamud.Game.ClientState.GamePad.GamepadButtons.R1;
    public Dalamud.Game.ClientState.GamePad.GamepadButtons ControllerWindowButton { get; set; } = Dalamud.Game.ClientState.GamePad.GamepadButtons.L1;

    // Settings window: everyday options only, unless the user asks for all of them.
    public bool AdvancedSettings { get; set; } = false;
    public bool QuickGuideDismissed { get; set; } = false;

    // Card media from the game (replaces GSM): screenshot, voice line or TTS, GIF.
    public bool CaptureScreenshot { get; set; } = true;
    public int ScreenshotMaxWidth { get; set; } = 1600;
    public float ScreenshotQuality { get; set; } = 0.85f;
    public bool RecordVoiceLines { get; set; } = true;
    public string SentenceAudioField { get; set; } = string.Empty;
    public float VoiceDetectionRatio { get; set; } = 2f;
    public int MaxLineSeconds { get; set; } = 20;
    public int VoiceBufferSeconds { get; set; } = 120;
    public Media.TtsMode TtsMode { get; set; } = Media.TtsMode.Off;
    public string TtsUrl { get; set; } = "http://127.0.0.1:50021";
    public string TtsSpeaker { get; set; } = "1";
    public bool TtsPost { get; set; } = false;
    public bool CaptureGif { get; set; } = true;
    public string GifField { get; set; } = string.Empty;
    public bool GifReplacesScreenshot { get; set; } = true;
    public Media.AnimationFormat AnimationFormat { get; set; } = Media.AnimationFormat.Avif;
    public string FfmpegPath { get; set; } = string.Empty;
    public int AvifCrf { get; set; } = 32;
    public int WebpQuality { get; set; } = 75;
    public int GifWidth { get; set; } = 720;
    public int GifFps { get; set; } = 12;
    public int GifSeconds { get; set; } = 3;

    /// <summary>1 once the low early animation defaults (400 px, 8 fps) were raised.</summary>
    public int AnimationDefaults { get; set; } = 0;

    /// <summary>Raises settings saved by earlier builds to the current defaults; true if anything changed.</summary>
    public bool Migrate()
    {
        if (AnimationDefaults >= 1)
            return false;
        if (GifWidth <= 400) GifWidth = 720;
        if (GifFps <= 8) GifFps = 12;
        AnimationDefaults = 1;
        return true;
    }
    public string AnkiConnectUrl { get; set; } = "http://127.0.0.1:8765";

    // Sentence translation, with GameSentenceMiner's parameters (Settings → Mining).
    public bool TranslationEnabled { get; set; } = false;
    public Media.TranslationProvider TranslationProvider { get; set; } = Media.TranslationProvider.Gemini;
    public string TranslationField { get; set; } = "SentenceTranslation";
    public string TranslationLanguage { get; set; } = "English";
    public string DeepLTargetLanguage { get; set; } = "EN-US";
    public bool UseCannedTranslationPrompt { get; set; } = true;
    public bool UseCannedContextPrompt { get; set; } = false;
    public string CustomTranslationPrompt { get; set; } = string.Empty;
    public string CustomFullPrompt { get; set; } = string.Empty;
    public int DialogueContextLength { get; set; } = 5;
    public double TranslationTemperature { get; set; } = 0.3;
    public double TranslationTopP { get; set; } = 0.9;
    public int TranslationMaxTokens { get; set; } = 4096;
    public Dictionary<Media.TranslationProvider, Media.AiProviderSettings> AiProviders { get; set; } = new();
    public string CodexPath { get; set; } = string.Empty;
    public string ClaudeCodePath { get; set; } = string.Empty;

    // Anki (mining is done by the Hachidori host with its own Anki Template)
    public string AnkiTemplateId { get; set; } = string.Empty;
    public string DocumentTitle { get; set; } = "FINAL FANTASY XIV";

    public void ResetPopupDefaults()
    {
        var d = new Configuration();
        ScanModifier = d.ScanModifier;
        ScanLength = d.ScanLength;
        MaxResults = d.MaxResults;
        ScanOnlyJapanese = d.ScanOnlyJapanese;
        MaxPopupDepth = d.MaxPopupDepth;
        HideDelaySeconds = d.HideDelaySeconds;
        AutoHideRootPopup = d.AutoHideRootPopup;
        UsePersonalDictionary = d.UsePersonalDictionary;
        NativeTextHover = d.NativeTextHover;
        NativeChatHover = d.NativeChatHover;
        NativeOtherTextHover = d.NativeOtherTextHover;
        NativeHoverDebug = d.NativeHoverDebug;
        TokenizedHover = d.TokenizedHover;
        FreezeFrameHotkey = d.FreezeFrameHotkey;
        PopupWidth = d.PopupWidth;
        PopupMaxHeight = d.PopupMaxHeight;
        PopupFontSize = d.PopupFontSize;
        PopupBackgroundAlpha = d.PopupBackgroundAlpha;
        ShowFrequencies = d.ShowFrequencies;
        ShowPitchAccent = d.ShowPitchAccent;
        ShowInflections = d.ShowInflections;
        ShowDefinitionTags = d.ShowDefinitionTags;
        ShowDictionaryNames = d.ShowDictionaryNames;
        ShowAttribution = d.ShowAttribution;
        ShowLookupCounts = d.ShowLookupCounts;
        HonourDefinitionBlur = d.HonourDefinitionBlur;
        AutoPlayAudio = d.AutoPlayAudio;
        AudioVolume = d.AudioVolume;
    }

    public void ResetThemeDefaults()
    {
        var d = new Configuration();
        FollowHachidoriTheme = d.FollowHachidoriTheme;
        UseWebViewPopup = d.UseWebViewPopup;
        AutoSyncWebViewBundle = d.AutoSyncWebViewBundle;
        WebViewBundlePath = d.WebViewBundlePath;
        FollowHachidoriOpacity = d.FollowHachidoriOpacity;
        ApplyHachidoriCustomCss = d.ApplyHachidoriCustomCss;
        HachidoriThemeOverride = d.HachidoriThemeOverride;
        HachidoriVariantOverride = d.HachidoriVariantOverride;
        ExtraPopupCss = d.ExtraPopupCss;
    }

    public void ResetMiningDefaults()
    {
        var d = new Configuration();
        CaptureScreenshot = d.CaptureScreenshot;
        ScreenshotMaxWidth = d.ScreenshotMaxWidth;
        ScreenshotQuality = d.ScreenshotQuality;
        RecordVoiceLines = d.RecordVoiceLines;
        SentenceAudioField = d.SentenceAudioField;
        VoiceDetectionRatio = d.VoiceDetectionRatio;
        MaxLineSeconds = d.MaxLineSeconds;
        VoiceBufferSeconds = d.VoiceBufferSeconds;
        TtsMode = d.TtsMode;
        TtsUrl = d.TtsUrl;
        TtsSpeaker = d.TtsSpeaker;
        TtsPost = d.TtsPost;
        CaptureGif = d.CaptureGif;
        GifField = d.GifField;
        GifReplacesScreenshot = d.GifReplacesScreenshot;
        AnimationFormat = d.AnimationFormat;
        FfmpegPath = d.FfmpegPath;
        AvifCrf = d.AvifCrf;
        WebpQuality = d.WebpQuality;
        GifWidth = d.GifWidth;
        GifFps = d.GifFps;
        GifSeconds = d.GifSeconds;
        AnkiConnectUrl = d.AnkiConnectUrl;
        TranslationEnabled = d.TranslationEnabled;
        TranslationProvider = d.TranslationProvider;
        TranslationField = d.TranslationField;
        TranslationLanguage = d.TranslationLanguage;
        DeepLTargetLanguage = d.DeepLTargetLanguage;
        UseCannedTranslationPrompt = d.UseCannedTranslationPrompt;
        UseCannedContextPrompt = d.UseCannedContextPrompt;
        CustomTranslationPrompt = d.CustomTranslationPrompt;
        CustomFullPrompt = d.CustomFullPrompt;
        DialogueContextLength = d.DialogueContextLength;
        TranslationTemperature = d.TranslationTemperature;
        TranslationTopP = d.TranslationTopP;
        TranslationMaxTokens = d.TranslationMaxTokens;
        CodexPath = d.CodexPath;
        ClaudeCodePath = d.ClaudeCodePath;
    }

    public void ResetControllerDefaults()
    {
        var d = new Configuration();
        ControllerEnabled = d.ControllerEnabled;
        ControllerHoldButton = d.ControllerHoldButton;
        ControllerHoldSeconds = d.ControllerHoldSeconds;
        ControllerStartInWordMode = d.ControllerStartInWordMode;
        ControllerExitWhenDialogueCloses = d.ControllerExitWhenDialogueCloses;
        ControllerShowHints = d.ControllerShowHints;
        ControllerPlayStationLabels = d.ControllerPlayStationLabels;
        ControllerUseTouchpad = d.ControllerUseTouchpad;
        ControllerCursorSpeed = d.ControllerCursorSpeed;
        ControllerMineButton = d.ControllerMineButton;
        ControllerAudioButton = d.ControllerAudioButton;
        ControllerWordModeButton = d.ControllerWordModeButton;
        ControllerBackExitButton = d.ControllerBackExitButton;
        ControllerPrevEntryButton = d.ControllerPrevEntryButton;
        ControllerNextEntryButton = d.ControllerNextEntryButton;
        ControllerDiveButton = d.ControllerDiveButton;
        ControllerWindowButton = d.ControllerWindowButton;
    }

    public void ResetReaderDefaults()
    {
        var d = new Configuration();
        ReaderEnabled = d.ReaderEnabled;
        ReaderAutoShowWithDialogue = d.ReaderAutoShowWithDialogue;
        ReaderAnchor = d.ReaderAnchor;
        ReaderFontSize = d.ReaderFontSize;
        ReaderBackgroundAlpha = d.ReaderBackgroundAlpha;
        ReaderShowHistory = d.ReaderShowHistory;
        HistorySize = d.HistorySize;
        ReaderLocked = d.ReaderLocked;
        ReaderHideUntilHovered = d.ReaderHideUntilHovered;
    }

    public void ResetCaptureDefaults()
    {
        var d = new Configuration();
        KatakanaPlayerName = d.KatakanaPlayerName;
        PlayerNameKatakana = d.PlayerNameKatakana;
        CaptureTalk = d.CaptureTalk;
        CaptureBattleTalk = d.CaptureBattleTalk;
        CaptureChat = d.CaptureChat;
        ChatChannels = new HashSet<XivChatType>(d.ChatChannels);
    }

    public void ResetPublishDefaults()
    {
        var d = new Configuration();
        PublishEnabled = d.PublishEnabled;
        PublishPort = d.PublishPort;
        PublishTalk = d.PublishTalk;
        PublishBattleTalk = d.PublishBattleTalk;
        PublishPopups = d.PublishPopups;
        PublishChat = d.PublishChat;
        PublishSpeaker = d.PublishSpeaker;
    }

    public void ResetConnectionDefaults()
    {
        var d = new Configuration();
        RelayHost = d.RelayHost;
        RelayPort = d.RelayPort;
        ApiPort = d.ApiPort;
        AnkiTemplateId = d.AnkiTemplateId;
        DocumentTitle = d.DocumentTitle;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
