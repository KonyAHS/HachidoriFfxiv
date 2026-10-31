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

    // Anki (mining is done by the Hachidori host with its own Anki Template)
    public string AnkiTemplateId { get; set; } = string.Empty;
    public string DocumentTitle { get; set; } = "FINAL FANTASY XIV";

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
