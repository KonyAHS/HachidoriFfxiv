using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Capture;

public enum LineSource
{
    Talk,
    BattleTalk,
    Chat,
    Manual,
}

/// <summary>A captured line of game text, with its prebuilt scannable block.</summary>
public sealed class GameLine
{
    public required LineSource Source { get; init; }
    public required string Speaker { get; init; }
    public required string Text { get; init; }
    public DateTime Time { get; init; } = DateTime.UtcNow;
    public RichBlock Block { get; } = new();
}

/// <summary>
/// Captures Japanese game text: the dialogue window (Talk), battle talk, and chat channels.
/// The addons are read every frame (two text nodes), which follows the typewriter effect,
/// cutscene subtitles and auto-advance without depending on addon event ordering.
/// </summary>
public sealed unsafe class TextCapture : IDisposable
{
    private readonly Configuration config;
    private readonly List<GameLine> history = new();
    private string lastTalk = string.Empty;
    private string lastBattleTalk = string.Empty;
    private string lastSubtitle = string.Empty;

    public TextCapture(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
        Plugin.ChatGui.ChatMessageUnhandled += OnChat;
    }

    public event Action<GameLine>? LineCaptured;

    public IReadOnlyList<GameLine> History => history;
    public GameLine? Current { get; private set; }
    public bool TalkVisible { get; private set; }
    public bool BattleTalkVisible { get; private set; }

    public void AddManual(string text) => Add(LineSource.Manual, string.Empty, text);

    private void OnUpdate(IFramework framework)
    {
        TalkVisible = false;
        BattleTalkVisible = false;
        if (config.CaptureTalk)
        {
            Poll("Talk", LineSource.Talk, ref lastTalk, v => TalkVisible = v);
            Poll("TalkSubtitle", LineSource.Talk, ref lastSubtitle, v => TalkVisible |= v);
        }
        else
        {
            lastTalk = lastSubtitle = string.Empty;
        }
        if (config.CaptureBattleTalk)
            Poll("_BattleTalk", LineSource.BattleTalk, ref lastBattleTalk, v => BattleTalkVisible = v);
        else
            lastBattleTalk = string.Empty;
    }

    private void Poll(string addonName, LineSource source, ref string last, Action<bool> setVisible)
    {
        var addon = Plugin.GameGui.GetAddonByName(addonName);
        if (addon.IsNull || !addon.IsVisible)
        {
            last = string.Empty;
            setVisible(false);
            return;
        }

        setVisible(true);
        var (speaker, text) = addonName switch
        {
            "TalkSubtitle" => (string.Empty, ReadSeString(((AddonTalkSubtitle*)addon.Address)->SubtitleText.AsSpan())),
            "Talk" => ReadTalk((AddonTalk*)addon.Address),
            _ => ReadTexts((AtkUnitBase*)addon.Address),
        };
        if (text.Length == 0)
            return;
        var key = speaker + "\u0001" + text;
        if (key == last)
            return;
        last = key;
        if (addonName == "TalkSubtitle")
            DiagnosticLog.Write("capture", $"TalkSubtitle text read: characters={text.Length}");
        Add(source, speaker, text);
    }

    /// <summary>
    /// Talk has dedicated speaker and text nodes. Reading them directly keeps a short line
    /// (はい) from being mistaken for the speaker, and a momentarily empty text node between
    /// lines from recording the speaker's name as a line of its own.
    /// </summary>
    private static (string Speaker, string Text) ReadTalk(AddonTalk* talk)
    {
        var speakerNode = talk->AtkTextNode220;
        var textNode = talk->AtkTextNode228;
        if (textNode == null)
            return ReadTexts(&talk->AtkUnitBase);
        var text = ReadSeString(textNode->NodeText.AsSpan()).Trim();
        var speaker = speakerNode != null && speakerNode->IsVisible()
            ? ReadSeString(speakerNode->NodeText.AsSpan()).Trim()
            : string.Empty;
        return (speaker, text);
    }

    /// <summary>The longest visible text node is the line, the other short one the speaker.</summary>
    private static (string Speaker, string Text) ReadTexts(AtkUnitBase* unit)
    {
        var texts = new List<string>();
        var manager = unit->UldManager;
        for (var i = 0; i < manager.NodeListCount; i++)
        {
            var node = manager.NodeList[i];
            if (node == null || node->Type != NodeType.Text || !node->IsVisible())
                continue;
            var textNode = (AtkTextNode*)node;
            var value = ReadSeString(textNode->NodeText.AsSpan());
            if (!string.IsNullOrWhiteSpace(value))
                texts.Add(value.Trim());
        }

        // A lone node is the speaker of a line whose text hasn't been set yet, not a line.
        if (texts.Count < 2)
            return (string.Empty, string.Empty);
        var text = texts.OrderByDescending(t => t.Length).First();
        var speaker = texts.Where(t => t != text && t.Length <= 32).OrderBy(t => t.Length).LastOrDefault() ?? string.Empty;
        return (speaker, text);
    }

    private static string ReadSeString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;
        try
        {
            return SeString.Parse(bytes).TextValue;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void OnChat(IChatMessage message)
    {
        if (!config.CaptureChat || !config.ChatChannels.Contains(message.LogKind))
            return;
        var text = message.Message.TextValue;
        if (!text.Any(c => Japanese.IsJapanese(c)))
            return;
        Add(LineSource.Chat, message.Sender.TextValue, text);
    }

    private void Add(LineSource source, string speaker, string text)
    {
        text = Japanese.NormaliseGameText(text, joinLines: true);
        if (text.Length == 0)
            return;
        var names = PlayerName.Replacements(config);
        text = NameKana.Apply(text, names);
        speaker = NameKana.Apply(speaker, names);
        var line = new GameLine { Source = source, Speaker = speaker, Text = text };
        line.Block.Add(text);
        history.Add(line);
        var limit = Math.Max(1, config.HistorySize);
        if (history.Count > limit)
            history.RemoveRange(0, history.Count - limit);
        Current = line;
        LineCaptured?.Invoke(line);
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.ChatGui.ChatMessageUnhandled -= OnChat;
    }
}
