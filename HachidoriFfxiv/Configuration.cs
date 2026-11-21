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

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Connection to Hachidori (through the Hachidori Relay add-on running inside Anki).
    public string RelayHost { get; set; } = "127.0.0.1";
    public int RelayPort { get; set; } = 8771;
    public int ApiPort { get; set; } = 19633;

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

    // Settings window: everyday options only, unless the user asks for all of them.
    public bool AdvancedSettings { get; set; } = false;

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
    public int GifWidth { get; set; } = 400;
    public int GifFps { get; set; } = 8;
    public int GifSeconds { get; set; } = 3;
    public int GifBufferSeconds { get; set; } = 12;
    public string AnkiConnectUrl { get; set; } = "http://127.0.0.1:8765";

    // Anki (mining is done by the Hachidori host with its own Anki Template)
    public string AnkiTemplateId { get; set; } = string.Empty;
    public string DocumentTitle { get; set; } = "FINAL FANTASY XIV";

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
