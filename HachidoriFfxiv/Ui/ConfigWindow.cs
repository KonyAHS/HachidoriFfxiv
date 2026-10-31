using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Capture;
using HachidoriFfxiv.Publishing;

namespace HachidoriFfxiv.Ui;

public sealed class ConfigWindow : Window
{
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

    private readonly Configuration config;
    private readonly HachidoriService service;
    private readonly Fonts fonts;
    private readonly NativeTextHover nativeHover;
    private readonly TextPublisher publisher;
    private readonly Func<string> exportLogs;
    private string? exportedLog;

    public ConfigWindow(Configuration config, HachidoriService service, Fonts fonts, NativeTextHover nativeHover, TextPublisher publisher, Func<string> exportLogs)
        : base("Hachidori Settings##hachidori-ffxiv-config")
    {
        this.config = config;
        this.service = service;
        this.fonts = fonts;
        this.nativeHover = nativeHover;
        this.publisher = publisher;
        this.exportLogs = exportLogs;
        Size = new Vector2(560, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        using var tabs = ImRaii.TabBar("##tabs");
        if (!tabs.Success)
            return;
        using (var tab = ImRaii.TabItem("Popup"))
        {
            if (tab.Success)
                DrawPopupTab();
        }

        using (var tab = ImRaii.TabItem("Reader"))
        {
            if (tab.Success)
                DrawReaderTab();
        }

        using (var tab = ImRaii.TabItem("Capture"))
        {
            if (tab.Success)
                DrawCaptureTab();
        }

        using (var tab = ImRaii.TabItem("GSM"))
        {
            if (tab.Success)
                DrawPublishTab();
        }

        using (var tab = ImRaii.TabItem("Connection"))
        {
            if (tab.Success)
                DrawConnectionTab();
        }
    }

    private void DrawStatus()
    {
        if (ImGui.Button("Export debug log"))
        {
            try { exportedLog = exportLogs(); }
            catch (Exception ex) { exportedLog = $"Export failed: {ex.Message}"; }
        }
        if (exportedLog is not null)
        {
            ImGui.TextWrapped(exportedLog);
            if (ImGui.Button("Copy log path")) ImGui.SetClipboardText(exportedLog);
        }
        var link = service.Link;
        if (link.Connected)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), "●");
            ImGui.SameLine();
            ImGui.TextUnformatted($"Linked to Hachidori in {link.HostName} (v{link.HostVersion}), {link.DictionaryCount} dictionaries.");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "●");
            ImGui.SameLine();
            ImGui.TextWrapped(link.Error ?? "Connecting to the Hachidori Relay…");
            ImGui.TextWrapped("Needs: Anki open with the Hachidori Relay add-on, and Chrome with Hachidori (Settings → Sharing on).");
        }
    }

    private void Changed(bool changed)
    {
        if (!changed)
            return;
        config.Save();
        fonts.Refresh();
        service.ClearCache();
    }

    private void DrawPopupTab()
    {
        Checkbox("Hover whole words (Hachidori tokenizer)", () => config.TokenizedHover, v => config.TokenizedHover = v);
        Checkbox("Hover native game text", () => config.NativeTextHover, v => config.NativeTextHover = v);
        Checkbox("Include native chat text", () => config.NativeChatHover, v => config.NativeChatHover = v);
        Checkbox("Other native windows (experimental)", () => config.NativeOtherTextHover, v => config.NativeOtherTextHover = v);
        Checkbox("Show native hover targets (debug)", () => config.NativeHoverDebug, v => config.NativeHoverDebug = v);
        if (config.NativeHoverDebug)
        {
            ImGui.TextDisabled($"Native text regions under mouse: {nativeHover.RegionCount}");
            if (nativeHover.LastError is { } error) ImGui.TextWrapped(error);
        }
        ImGui.Separator();
        var modifier = (int)config.ScanModifier;
        if (ImGui.Combo("Scan key", ref modifier, Enum.GetNames<ScanModifier>(), Enum.GetValues<ScanModifier>().Length))
        {
            config.ScanModifier = (ScanModifier)modifier;
            Changed(true);
        }

        HelpMarker("None scans on hover, like JL's default. Alt over a headword kanji (or clicking it) opens kanji info.");

        var scanLength = config.ScanLength;
        if (ImGui.SliderInt("Scan length", ref scanLength, 4, 32))
        {
            config.ScanLength = scanLength;
            Changed(true);
        }

        var maxResults = config.MaxResults;
        if (ImGui.SliderInt("Max results", ref maxResults, 1, 32))
        {
            config.MaxResults = maxResults;
            Changed(true);
        }

        var depth = config.MaxPopupDepth;
        if (ImGui.SliderInt("Nested popups", ref depth, 1, 8))
        {
            config.MaxPopupDepth = depth;
            Changed(true);
        }

        var width = config.PopupWidth;
        if (ImGui.SliderFloat("Popup width", ref width, 280, 900, "%.0f"))
        {
            config.PopupWidth = width;
            Changed(true);
        }

        var height = config.PopupMaxHeight;
        if (ImGui.SliderFloat("Popup max height", ref height, 160, 1000, "%.0f"))
        {
            config.PopupMaxHeight = height;
            Changed(true);
        }

        var fontSize = config.PopupFontSize;
        if (ImGui.SliderFloat("Popup font size", ref fontSize, 12, 36, "%.0f"))
            config.PopupFontSize = fontSize;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Changed(true);

        var alpha = config.PopupBackgroundAlpha;
        if (ImGui.SliderFloat("Popup opacity", ref alpha, 0.3f, 1f))
        {
            config.PopupBackgroundAlpha = alpha;
            Changed(true);
        }

        var hide = config.HideDelaySeconds;
        if (ImGui.SliderFloat("Hide delay (s)", ref hide, 0f, 2f, "%.2f"))
        {
            config.HideDelaySeconds = hide;
            Changed(true);
        }

        Checkbox("Hide the popup when the mouse leaves", () => config.AutoHideRootPopup, v => config.AutoHideRootPopup = v);
        Checkbox("Only start scans on Japanese characters", () => config.ScanOnlyJapanese, v => config.ScanOnlyJapanese = v);
        Checkbox("Use the personal dictionary", () => config.UsePersonalDictionary, v => config.UsePersonalDictionary = v);
        ImGui.Separator();
        Checkbox("Show frequencies", () => config.ShowFrequencies, v => config.ShowFrequencies = v);
        Checkbox("Show pitch accent", () => config.ShowPitchAccent, v => config.ShowPitchAccent = v);
        Checkbox("Show deinflection", () => config.ShowInflections, v => config.ShowInflections = v);
        Checkbox("Show definition tags", () => config.ShowDefinitionTags, v => config.ShowDefinitionTags = v);
        Checkbox("Show dictionary names", () => config.ShowDictionaryNames, v => config.ShowDictionaryNames = v);
        Checkbox("Show attribution", () => config.ShowAttribution, v => config.ShowAttribution = v);
        Checkbox("Show lookup counts (Hachidori lookup statistics)", () => config.ShowLookupCounts, v => config.ShowLookupCounts = v);
        Checkbox("Honour Hachidori's lookup blur", () => config.HonourDefinitionBlur, v => config.HonourDefinitionBlur = v);
        Checkbox("Auto-play pronunciation", () => config.AutoPlayAudio, v => config.AutoPlayAudio = v);
        var volume = config.AudioVolume;
        if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f))
        {
            config.AudioVolume = volume;
            Changed(true);
        }
    }

    private void DrawReaderTab()
    {
        Checkbox("Enable reader overlay", () => config.ReaderEnabled, v => config.ReaderEnabled = v);
        Checkbox("Show only while dialogue is open", () => config.ReaderAutoShowWithDialogue, v => config.ReaderAutoShowWithDialogue = v);
        Checkbox("Show history", () => config.ReaderShowHistory, v => config.ReaderShowHistory = v);
        Checkbox("Lock (no title bar, click-to-move off)", () => config.ReaderLocked, v => config.ReaderLocked = v);
        Checkbox("Hide text until hovered", () => config.ReaderHideUntilHovered, v => config.ReaderHideUntilHovered = v);
        var anchor = (int)config.ReaderAnchor;
        if (ImGui.Combo("Position", ref anchor, ["Free", "Above the dialogue box", "Below the dialogue box"], 3))
        {
            config.ReaderAnchor = (ReaderAnchor)anchor;
            Changed(true);
        }

        var fontSize = config.ReaderFontSize;
        if (ImGui.SliderFloat("Reader font size", ref fontSize, 14, 48, "%.0f"))
            config.ReaderFontSize = fontSize;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Changed(true);
        var alpha = config.ReaderBackgroundAlpha;
        if (ImGui.SliderFloat("Reader opacity", ref alpha, 0f, 1f))
        {
            config.ReaderBackgroundAlpha = alpha;
            Changed(true);
        }

        var historySize = config.HistorySize;
        if (ImGui.SliderInt("History size", ref historySize, 10, 500))
        {
            config.HistorySize = historySize;
            Changed(true);
        }
    }

    private void DrawCaptureTab()
    {
        Checkbox("Dialogue (Talk, cutscene subtitles)", () => config.CaptureTalk, v => config.CaptureTalk = v);
        Checkbox("Battle talk", () => config.CaptureBattleTalk, v => config.CaptureBattleTalk = v);
        Checkbox("Chat", () => config.CaptureChat, v => config.CaptureChat = v);
        Checkbox("Read my character's name as katakana", () => config.KatakanaPlayerName, v => config.KatakanaPlayerName = v);
        using (ImRaii.Disabled(!config.KatakanaPlayerName))
        using (ImRaii.PushIndent())
        {
            var kana = config.PlayerNameKatakana;
            if (ImGui.InputTextWithHint("Katakana name", PlayerName.Suggested(), ref kana, 64))
            {
                config.PlayerNameKatakana = kana.Trim();
                Changed(true);
            }

            ImGui.TextDisabled("Blank uses the automatic reading shown. Separate first and last name with ・ or a space.");
        }

        ImGui.TextWrapped("Channel selection applies to reader history. Native chat hover scans the visible chat text.");
        using var disabled = ImRaii.Disabled(!config.CaptureChat);
        using var indent = ImRaii.PushIndent();
        foreach (var channel in SelectableChannels)
        {
            var on = config.ChatChannels.Contains(channel);
            if (ImGui.Checkbox(channel.ToString(), ref on))
            {
                if (on)
                    config.ChatChannels.Add(channel);
                else
                    config.ChatChannels.Remove(channel);
                Changed(true);
            }
        }
    }

    private void DrawPublishTab()
    {
        ImGui.TextWrapped("Sends game text over a WebSocket on this computer so GameSentenceMiner can read it. Off by default; nothing leaves this PC.");
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
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "Sends other players' messages to GSM.");
        }
        Checkbox("Put the speaker's name before each line", () => config.PublishSpeaker, v => config.PublishSpeaker = v);

        if ((config.PublishTalk && !config.CaptureTalk) || (config.PublishBattleTalk && !config.CaptureBattleTalk) || (config.PublishChat && !config.CaptureChat))
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "A selected source is turned off in the Capture tab and won't be sent.");

        ImGui.Separator();
        var portIndex = Array.FindIndex(PublishPorts, p => p.Port == config.PublishPort);
        var portNames = PublishPorts.Select(p => $"{p.Name} ({p.Port})").Append("Custom").ToArray();
        var portSelected = portIndex < 0 ? PublishPorts.Length : portIndex;
        if (ImGui.Combo("GSM source slot", ref portSelected, portNames, portNames.Length) && portSelected < PublishPorts.Length)
        {
            config.PublishPort = PublishPorts[portSelected].Port;
            Changed(true);
        }
        HelpMarker("GSM listens to all three slots by default, and the name is only its label. Choose one that no other program you run uses.");

        var port = config.PublishPort;
        if (ImGui.InputInt("Port", ref port))
        {
            config.PublishPort = Math.Clamp(port, 1, 65535);
            Changed(true);
        }
        ImGui.TextDisabled($"In GSM's WebSocket sources, this is localhost:{config.PublishPort}");

        if (publisher.Error is { } error)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), error);
        else if (publisher.Listening)
            ImGui.TextUnformatted(publisher.ClientCount == 0 ? "Listening. Waiting for GSM to connect…" : $"Connected clients: {publisher.ClientCount}");
        using (ImRaii.Disabled(publisher.ClientCount == 0))
        {
            if (ImGui.Button("Send test line"))
                publisher.SendTest();
        }
    }

    private void DrawConnectionTab()
    {
        var host = config.RelayHost;
        if (ImGui.InputText("Relay host", ref host, 128))
        {
            config.RelayHost = host.Trim();
            Changed(true);
        }

        var port = config.RelayPort;
        if (ImGui.InputInt("Relay port", ref port))
        {
            config.RelayPort = Math.Clamp(port, 1, 65535);
            Changed(true);
        }

        var apiPort = config.ApiPort;
        if (ImGui.InputInt("Yomitan API port (audio)", ref apiPort))
        {
            config.ApiPort = Math.Clamp(apiPort, 1, 65535);
            Changed(true);
        }

        var template = config.AnkiTemplateId;
        if (ImGui.InputText("Anki Template ID (blank: first)", ref template, 256))
        {
            config.AnkiTemplateId = template.Trim();
            Changed(true);
        }

        var title = config.DocumentTitle;
        if (ImGui.InputText("{document-title}", ref title, 256))
        {
            config.DocumentTitle = title;
            Changed(true);
        }

        if (ImGui.Button("Reconnect"))
            service.Link.Reconnect();

        if (service.Link.Connected)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Dictionaries on the host:");
            foreach (var d in service.Dictionaries)
            {
                var enabled = d["enabled"] is not System.Text.Json.Nodes.JsonValue e || !e.TryGetValue<bool>(out var en) || en;
                ImGui.BulletText($"{LookupResult.Str(d["title"])}{(enabled ? string.Empty : " (disabled)")}");
            }
        }
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

    private static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
