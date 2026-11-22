using System;
using System.Buffers;
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
    Popup,
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
    private static readonly string[] PopupAddons = ["SelectYesno", "SelectOk", "SelectString", "SelectIconString", "CutSceneSelectString"];

    private readonly Configuration config;
    private readonly List<GameLine> history = new();
    private string lastTalk = string.Empty;
    private string lastBattleTalk = string.Empty;
    private string lastSubtitle = string.Empty;
    private readonly Dictionary<string, string> lastPopups = new();
    // The raw text bytes each addon showed last frame. Text that hasn't changed is not parsed again.
    private readonly Dictionary<string, byte[]> lastRaw = new();
    private readonly ArrayBufferWriter<byte> rawScratch = new();

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
            TalkVisible = Poll("Talk", LineSource.Talk, ref lastTalk);
            TalkVisible |= Poll("TalkSubtitle", LineSource.Talk, ref lastSubtitle);
        }
        else
        {
            lastTalk = lastSubtitle = string.Empty;
            lastRaw.Remove("Talk");
            lastRaw.Remove("TalkSubtitle");
        }
        if (config.CaptureBattleTalk)
        {
            BattleTalkVisible = Poll("_BattleTalk", LineSource.BattleTalk, ref lastBattleTalk);
        }
        else
        {
            lastBattleTalk = string.Empty;
            lastRaw.Remove("_BattleTalk");
        }
        PollPopups();
    }

    /// <summary>Interaction prompts and choice menus, read only for the opt-in text feed.</summary>
    private void PollPopups()
    {
        if (!config.PublishEnabled || !config.PublishPopups)
        {
            foreach (var name in PopupAddons)
                lastRaw.Remove(name);
            lastPopups.Clear();
            return;
        }
        foreach (var name in PopupAddons)
        {
            var addon = Plugin.GameGui.GetAddonByName(name);
            if (addon.IsNull || !addon.IsVisible)
            {
                lastPopups.Remove(name);
                lastRaw.Remove(name);
                continue;
            }
            if (RawUnchanged(name, (AtkUnitBase*)addon.Address))
                continue;
            var prompt = name switch
            {
                "SelectYesno" => ((AddonSelectYesno*)addon.Address)->PromptText,
                "SelectOk" => ((AddonSelectOk*)addon.Address)->PromptText,
                _ => null,
            };
            var text = prompt != null
                ? ReadSeString(prompt->NodeText.AsSpan()).Trim()
                : ReadAllTexts(&((AtkUnitBase*)addon.Address)->UldManager);
            if (text.Length == 0 || (lastPopups.TryGetValue(name, out var last) && last == text))
                continue;
            lastPopups[name] = text;
            AddPopup(text);
        }
    }

    /// <summary>Every visible text node including list rows, in node order, one per paragraph.</summary>
    private static string ReadAllTexts(AtkUldManager* root)
    {
        var parts = new List<string>();
        Collect(root, 0);
        return string.Join("\n\n", parts);

        void Collect(AtkUldManager* manager, int depth)
        {
            if (depth > 8 || manager->NodeList == null)
                return;
            for (var i = 0; i < manager->NodeListCount; i++)
            {
                var node = manager->NodeList[i];
                if (node == null || !node->IsVisible())
                    continue;
                if (node->Type == NodeType.Text)
                {
                    var value = ReadSeString(((AtkTextNode*)node)->NodeText.AsSpan()).Trim();
                    if (value.Length > 0)
                        parts.Add(value);
                }
                else if ((int)node->Type >= 1000 && ((AtkComponentNode*)node)->Component != null)
                {
                    Collect(&((AtkComponentNode*)node)->Component->UldManager, depth + 1);
                }
            }
        }
    }

    /// <summary>Reads one dialogue addon. Returns whether it is visible.</summary>
    private bool Poll(string addonName, LineSource source, ref string last)
    {
        var addon = Plugin.GameGui.GetAddonByName(addonName);
        if (addon.IsNull || !addon.IsVisible)
        {
            last = string.Empty;
            lastRaw.Remove(addonName);
            return false;
        }

        // Reading is a pure function of these bytes, so the same bytes give the same result as last frame.
        if (RawUnchanged(addonName, (AtkUnitBase*)addon.Address))
            return true;
        var (speaker, text) = addonName switch
        {
            "TalkSubtitle" => (string.Empty, ReadSeString(((AddonTalkSubtitle*)addon.Address)->SubtitleText.AsSpan())),
            "Talk" => ReadTalk((AddonTalk*)addon.Address),
            _ => ReadTexts((AtkUnitBase*)addon.Address),
        };
        if (text.Length == 0)
            return true;
        var key = speaker + "\u0001" + text;
        if (key == last)
            return true;
        last = key;
        if (addonName == "TalkSubtitle")
            DiagnosticLog.Write("capture", $"TalkSubtitle text read: characters={text.Length}");
        Add(source, speaker, text);
        return true;
    }

    /// <summary>
    /// Collects the raw bytes of every text node the readers below look at and compares them with
    /// last frame's, so unchanged text is not parsed into strings again every frame.
    /// </summary>
    private bool RawUnchanged(string addonName, AtkUnitBase* unit)
    {
        rawScratch.ResetWrittenCount();
        switch (addonName)
        {
            case "TalkSubtitle":
                AppendRaw(((AddonTalkSubtitle*)unit)->SubtitleText.AsSpan());
                break;
            case "Talk" when ((AddonTalk*)unit)->AtkTextNode228 != null:
            {
                // ReadTalk: the text node, and the speaker node while it is visible.
                var talk = (AddonTalk*)unit;
                AppendRaw(talk->AtkTextNode228->NodeText.AsSpan());
                var speaker = talk->AtkTextNode220;
                var speakerVisible = speaker != null && speaker->IsVisible();
                rawScratch.Write(speakerVisible ? (ReadOnlySpan<byte>)[1] : [0]);
                if (speakerVisible)
                    AppendRaw(speaker->NodeText.AsSpan());
                break;
            }
            case "SelectYesno":
            case "SelectOk":
            {
                var prompt = addonName == "SelectYesno" ? ((AddonSelectYesno*)unit)->PromptText : ((AddonSelectOk*)unit)->PromptText;
                if (prompt != null)
                    AppendRaw(prompt->NodeText.AsSpan());
                else
                    AppendTree(&unit->UldManager, 0);
                break;
            }
            default:
                if (Array.IndexOf(PopupAddons, addonName) >= 0)
                {
                    AppendTree(&unit->UldManager, 0);
                }
                else
                {
                    // ReadTexts: the addon's own visible text nodes.
                    var manager = unit->UldManager;
                    for (var i = 0; i < manager.NodeListCount; i++)
                    {
                        var node = manager.NodeList[i];
                        if (node != null && node->Type == NodeType.Text && node->IsVisible())
                            AppendRaw(((AtkTextNode*)node)->NodeText.AsSpan());
                    }
                }
                break;
        }

        var raw = rawScratch.WrittenSpan;
        if (lastRaw.TryGetValue(addonName, out var previous) && raw.SequenceEqual(previous))
            return true;
        lastRaw[addonName] = raw.ToArray();
        return false;

        // Mirrors ReadAllTexts.
        void AppendTree(AtkUldManager* manager, int depth)
        {
            if (depth > 8 || manager->NodeList == null)
                return;
            for (var i = 0; i < manager->NodeListCount; i++)
            {
                var node = manager->NodeList[i];
                if (node == null || !node->IsVisible())
                    continue;
                if (node->Type == NodeType.Text)
                    AppendRaw(((AtkTextNode*)node)->NodeText.AsSpan());
                else if ((int)node->Type >= 1000 && ((AtkComponentNode*)node)->Component != null)
                    AppendTree(&((AtkComponentNode*)node)->Component->UldManager, depth + 1);
            }
        }
    }

    /// <summary>Length-prefixed, so node boundaries are part of the comparison.</summary>
    private void AppendRaw(ReadOnlySpan<byte> bytes)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(rawScratch.GetSpan(4), bytes.Length);
        rawScratch.Advance(4);
        rawScratch.Write(bytes);
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

    /// <summary>Popups go to the feed only: they would displace the current line in the reader.</summary>
    private void AddPopup(string text)
    {
        text = Japanese.NormaliseGameText(text, joinLines: true);
        if (text.Length > 0)
            LineCaptured?.Invoke(new GameLine { Source = LineSource.Popup, Speaker = string.Empty, Text = NameKana.Apply(text, PlayerName.Replacements(config)) });
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
