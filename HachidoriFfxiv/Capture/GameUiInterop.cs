using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HachidoriFfxiv.Capture;

/// <summary>Snapshot of patch-sensitive FFXIV UI addon hooks for diagnostics.</summary>
internal readonly record struct UiInteropHealth(
    bool TalkVisible,
    bool TalkBodyResolved,
    bool TalkSpeakerResolved,
    bool ChatLogVisible,
    int ChatPanelCount,
    int SelectedChatTab);

/// <summary>
/// Centralizes all addon-specific <c>FFXIVClientStructs</c> reads and structural fallbacks
/// so patch-day UI layout changes are isolated to a single file.
/// </summary>
internal static unsafe class GameUiInterop
{
    /// <summary>
    /// Resolves <c>Talk</c>'s speaker and dialogue <see cref="AtkTextNode"/> pointers from its
    /// <see cref="AtkUldManager"/> without touching offset-named <c>AddonTalk</c> fields.
    /// </summary>
    internal static bool TryGetTalkNodes(AtkUnitBase* unit, out AtkTextNode* speaker, out AtkTextNode* body)
    {
        speaker = null;
        body = null;
        if (unit == null)
            return false;

        var manager = unit->UldManager;
        if (manager.NodeList == null || manager.NodeListCount == 0 || manager.NodeListCount > 512)
            return false;

        Span<TalkTextNodeCandidate> candidates = stackalloc TalkTextNodeCandidate[32];
        var nodes = stackalloc AtkTextNode*[32];
        var count = 0;

        for (var i = 0; i < manager.NodeListCount && count < 32; i++)
        {
            var node = manager.NodeList[i];
            if (node == null || node->Type != NodeType.Text)
                continue;
            var textNode = (AtkTextNode*)node;
            var flags = textNode->TextFlags;
            var hasText = !textNode->NodeText.AsSpan().IsEmpty;
            candidates[count] = new TalkTextNodeCandidate(
                Index: count,
                NodeId: node->NodeId,
                X: node->X,
                Y: node->Y,
                Width: node->Width,
                Height: node->Height,
                Visible: node->IsVisible(),
                MultiLine: (flags & TextFlags.MultiLine) != 0,
                WordWrap: (flags & TextFlags.WordWrap) != 0,
                HasText: hasText);
            nodes[count] = textNode;
            count++;
        }

        if (count == 0)
            return false;

        var (speakerIdx, bodyIdx) = GameUiHeuristics.SelectTalkNodeIndices(candidates[..count]);
        if (bodyIdx < 0)
            return false;

        body = nodes[bodyIdx];
        speaker = speakerIdx >= 0 ? nodes[speakerIdx] : null;
        return true;
    }

    /// <summary>
    /// Reads <c>TalkSubtitle</c>'s raw SeString bytes, preferring <c>AddonTalkSubtitle.SubtitleText</c>
    /// and falling back to visible <see cref="AtkTextNode"/>s if the struct field is empty.
    /// </summary>
    internal static ReadOnlySpan<byte> ReadTalkSubtitleRaw(AtkUnitBase* unit)
    {
        if (unit == null)
            return ReadOnlySpan<byte>.Empty;

        var subtitleSpan = ((AddonTalkSubtitle*)unit)->SubtitleText.AsSpan();
        if (!subtitleSpan.IsEmpty)
            return subtitleSpan;

        var manager = unit->UldManager;
        if (manager.NodeList == null || manager.NodeListCount > 512)
            return ReadOnlySpan<byte>.Empty;

        for (var i = 0; i < manager.NodeListCount; i++)
        {
            var node = manager.NodeList[i];
            if (node == null || node->Type != NodeType.Text || !node->IsVisible())
                continue;
            var span = ((AtkTextNode*)node)->NodeText.AsSpan();
            if (!span.IsEmpty)
                return span;
        }

        return ReadOnlySpan<byte>.Empty;
    }

    /// <summary>
    /// Discovers all loaded, visible <c>ChatLogPanel_*</c> addons dynamically from
    /// <see cref="RaptureAtkUnitManager.AllLoadedUnitsList"/> instead of assuming exactly 0..3.
    /// </summary>
    internal static List<(int PanelIndex, string Name, nint Address)> GetVisibleChatPanels()
    {
        var panels = new List<(int PanelIndex, string Name, nint Address)>();
        var stage = AtkStage.Instance();
        if (stage != null && stage->RaptureAtkUnitManager != null)
        {
            ref var all = ref stage->RaptureAtkUnitManager->AllLoadedUnitsList;
            for (var i = 0; i < all.Count; i++)
            {
                var unit = all.Entries[i].Value;
                if (unit == null || !unit->IsVisible)
                    continue;
                var name = unit->NameString;
                if (GameUiHeuristics.TryParseChatPanelIndex(name, out var idx))
                    panels.Add((idx, name, (nint)unit));
            }

            if (panels.Count > 0)
            {
                panels.Sort((a, b) => a.PanelIndex.CompareTo(b.PanelIndex));
                return panels;
            }
        }

        // Fallback if RaptureAtkUnitManager is unavailable.
        for (var i = 0; i < 4; i++)
        {
            var name = $"ChatLogPanel_{i}";
            var addon = DalamudServices.GameGui.GetAddonByName(name);
            if (!addon.IsNull && addon.IsVisible)
                panels.Add((i, name, addon.Address));
        }

        return panels;
    }

    internal static bool IsTooltipAddon(AtkUnitBase* unit, string name) =>
        GameUiHeuristics.IsTooltipAddon(name, unit != null ? unit->DepthLayer : null);

    /// <summary>Captures a one-shot health summary of live game UI hooks for <c>/hachidori diag</c>.</summary>
    internal static UiInteropHealth InspectHealth()
    {
        var talkVisible = false;
        var talkBodyResolved = false;
        var talkSpeakerResolved = false;
        var talk = DalamudServices.GameGui.GetAddonByName("Talk");
        if (!talk.IsNull && talk.IsVisible)
        {
            talkVisible = true;
            if (TryGetTalkNodes((AtkUnitBase*)talk.Address, out var speaker, out var body))
            {
                talkBodyResolved = body != null;
                talkSpeakerResolved = speaker != null;
            }
        }

        var chatLog = DalamudServices.GameGui.GetAddonByName("ChatLog");
        var chatLogVisible = !chatLog.IsNull && chatLog.IsVisible;
        var selectedTab = chatLogVisible ? ((AddonChatLog*)chatLog.Address)->TabIndex : -1;
        var panelCount = GetVisibleChatPanels().Count;

        return new UiInteropHealth(
            TalkVisible: talkVisible,
            TalkBodyResolved: talkBodyResolved,
            TalkSpeakerResolved: talkSpeakerResolved,
            ChatLogVisible: chatLogVisible,
            ChatPanelCount: panelCount,
            SelectedChatTab: selectedTab);
    }
}
