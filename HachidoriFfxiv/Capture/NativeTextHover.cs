using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;
using HachidoriFfxiv.Ui;
using Vector2 = System.Numerics.Vector2;

namespace HachidoriFfxiv.Capture;

/// <summary>
/// Invisible, click-through hover targets over the original game text. Native reads and
/// font measurements happen on Framework.Update. Draw only consumes managed snapshots;
/// no game pointer survives the update and no game node/text/visibility is modified.
/// </summary>
public sealed unsafe class NativeTextHover : IDisposable
{
    private sealed record Part(RichBlock Block, NativeTextLayout Layout);
    private readonly record struct TextStyle(ushort Width, ushort Height, byte FontSize, byte AlignmentFontType,
        byte LineSpacing, byte CharSpacing, TextFlags Flags, bool ChatBottomAligned, int ChatClipTop, int ChatClipHeight);
    private sealed record Cached(TextStyle Style, byte[] Raw, Part[] Parts);
    private sealed record Region(string Addon, Part Text, Vector2 Min, Vector2 Max, Vector2 ClipMin, Vector2 ClipMax, Vector2 Scale, bool Body = false);
    private sealed class ChatScrollState
    {
        public bool WasBottom = true;
        public uint LastTotalLines;
        public uint LastFirstVisible;
        public DateTime NewMessageGraceUntilUtc;
    }

    private readonly Configuration config;
    private readonly Dictionary<nint, Cached> cache = new();
    private readonly Dictionary<nint, ChatScrollState> chatScroll = new();
    private Region[] regions = [];
    // Reused every frame; Update runs each game tick.
    private readonly List<Region> frameRegions = new();
    private readonly HashSet<nint> frameVisited = new();
    private readonly HashSet<nint> frameSeen = new();
    private readonly List<nint> staleKeys = new();
    private static readonly string[] DialogueAddons = ["Talk", "_BattleTalk"];
    private Vector2 lastMouse = new(-10000);
    private DateTime lastError;
    private DateTime lastChatProbe;
    private Action<FrozenRegion[]>? pendingFreezeCallback;

    /// <summary>
    /// A scannable native text block with its glyph cells on screen, for controller navigation.
    /// <paramref name="Body"/> marks a window's main text (Talk's dialogue rather than the speaker's name).
    /// </summary>
    public sealed record TextTarget(string Addon, RichBlock Block, IReadOnlyList<(int Index, Vector2 Min, Vector2 Max)> Cells, Func<RichBlock, int, ScanContext> Context, bool Body = false);

    // The node being read as the current addon's main text; set only while ReadAddon walks that addon.
    private AtkTextNode* bodyNode;

    /// <summary>An immutable snapshot of a visible native text block captured for Freeze Frame inspection.</summary>
    public sealed record FrozenRegion(
        string Addon,
        bool IsTooltip,
        int DrawOrder,
        RichBlock Block,
        NativeTextLayout Layout,
        Vector2 Min,
        Vector2 Max,
        Vector2 ClipMin,
        Vector2 ClipMax,
        Vector2 Scale);

    /// <summary>Raised when visible Japanese chat lines change, so the plugin can pre-warm word segmentation.</summary>
    public event Action<IReadOnlyList<string>>? ChatLinesChanged;

    /// <summary>Set while controller mode is on: read all visible dialogue text, not only the text under the mouse.</summary>
    public bool ControllerScan { get; set; }

    public string? LastError { get; private set; }
    public int RegionCount => regions.Length;
    public bool SubtitleVisible { get; private set; }
    public string LastHoveredAddon { get; private set; } = string.Empty;
    public string FocusedAddonName { get; private set; } = string.Empty;

    public NativeTextHover(Configuration config)
    {
        this.config = config;
        DalamudServices.Framework.Update += Update;
    }

    /// <summary>Queues a full-screen snapshot of all visible native Japanese text (including tooltips) on the next Framework.Update.</summary>
    public void RequestFreezeSnapshot(Action<FrozenRegion[]> onCaptured) =>
        pendingFreezeCallback = onCaptured;

    /// <summary>
    /// Synchronously snapshots every visible native Japanese text block (including tooltips).
    /// Must only be called on the main game thread (e.g. inside Framework.Update).
    /// </summary>
    public FrozenRegion[] CaptureFreezeSnapshotNow()
    {
        try
        {
            return CaptureAllVisibleSnapshot();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("freeze-frame", $"Snapshot capture failed: {ex.Message}");
            return [];
        }
    }

    internal static bool IsTooltipAddon(string name) =>
        GameUiInterop.IsTooltipAddon(name);

    private void Update(IFramework framework)
    {
        if (pendingFreezeCallback is { } freezeCb)
        {
            pendingFreezeCallback = null;
            try
            {
                freezeCb(CaptureAllVisibleSnapshot());
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("freeze-frame", $"Snapshot capture failed: {ex.Message}");
                freezeCb([]);
            }
        }

        if (!config.NativeTextHover)
        {
            SubtitleVisible = false;
            regions = [];
            cache.Clear();
            return;
        }
        var next = frameRegions;
        var visited = frameVisited;
        var seen = frameSeen;
        next.Clear();
        visited.Clear();
        seen.Clear();
        try
        {
            // TalkSubtitle has no interactive backdrop. The native collision hit may
            // belong to a different addon underneath it, so inspect it independently.
            var subtitle = DalamudServices.GameGui.GetAddonByName("TalkSubtitle");
            var subtitleVisible = config.CaptureTalk && !subtitle.IsNull && subtitle.IsVisible;
            if (subtitleVisible != SubtitleVisible)
                DiagnosticLog.Write("capture", $"Floating subtitle visible={subtitleVisible}");
            SubtitleVisible = subtitleVisible;
            if (subtitleVisible) ReadAddon((AtkUnitBase*)subtitle.Address, "TalkSubtitle", next, visited, seen, ignoreMouse: ControllerScan);
            var stage = AtkStage.Instance();
            var hovered = stage == null || stage->AtkCollisionManager == null ? null : stage->AtkCollisionManager->IntersectingAddon;
            // Collision selection limits us to the topmost native addon when available.
            if (ControllerScan)
            {
                FocusedAddonName = string.Empty;
                if (stage != null && stage->RaptureAtkUnitManager != null)
                {
                    var focusedAddon = stage->RaptureAtkUnitManager->FocusedAddon;
                    if (focusedAddon != null && focusedAddon->IsVisible)
                        FocusedAddonName = focusedAddon->NameString;
                }
                foreach (var name in DialogueAddons)
                {
                    if (!Allowed(name)) continue;
                    var addon = DalamudServices.GameGui.GetAddonByName(name);
                    if (!addon.IsNull && addon.IsVisible) ReadAddon((AtkUnitBase*)addon.Address, name, next, visited, seen, ignoreMouse: true);
                }
                if (next.Count == 0 && stage != null && stage->RaptureAtkUnitManager != null)
                {
                    // Scan all visible allowed UI addons plus the active chat panel so the user
                    // can navigate any open window (Quest Accept, Journal, tooltips, ToDoList, Chat).
                    ref var all = ref stage->RaptureAtkUnitManager->AllLoadedUnitsList;
                    for (var i = (int)all.Count - 1; i >= 0; i--)
                    {
                        var unit = all.Entries[i].Value;
                        if (unit == null || !unit->IsVisible) continue;
                        var name = unit->NameString;
                        if (string.IsNullOrEmpty(name) || IsChat(name) || name == "TalkSubtitle" || !Allowed(name)) continue;
                        ReadAddon(unit, name, next, visited, seen, ignoreMouse: true);
                    }
                    ReadChat(null, next, visited, seen, ignoreMouse: true);
                }
            }
            else if (hovered != null && hovered->IsVisible)
            {
                var name = hovered->NameString;
                LastHoveredAddon = name;
                // The ChatLog window and its text panels are separate addons, and the
                // collision hit can be the window, which has no chat text of its own.
                if (IsChat(name)) ReadChat(hovered, next, visited, seen, ignoreMouse: false);
                else if (Allowed(name) && name != "TalkSubtitle") ReadAddon(hovered, name, next, visited, seen, ignoreMouse: false);
            }
            else
            {
                LastHoveredAddon = string.Empty;
                foreach (var name in DialogueAddons)
                {
                    if (!Allowed(name)) continue;
                    var addon = DalamudServices.GameGui.GetAddonByName(name);
                    if (!addon.IsNull && addon.IsVisible) ReadAddon((AtkUnitBase*)addon.Address, name, next, visited, seen, ignoreMouse: false);
                }
                ReadChat(null, next, visited, seen, ignoreMouse: false);
            }
            regions = next.Count == 0 ? [] : next.ToArray();
            if (cache.Count > 0)
            {
                staleKeys.Clear();
                foreach (var key in cache.Keys)
                    if (!seen.Contains(key)) staleKeys.Add(key);
                foreach (var key in staleKeys) cache.Remove(key);
            }
            LastError = null;
        }
        catch (Exception ex)
        {
            regions = [];
            cache.Clear();
            LastError = ex.Message;
            if ((DateTime.UtcNow - lastError).TotalSeconds > 10)
            {
                lastError = DateTime.UtcNow;
                DiagnosticLog.Write("native-hover", ex.Message);
                DalamudServices.Log.Warning(ex, "Native text hover skipped a frame");
            }
        }
    }

    private FrozenRegion[] CaptureAllVisibleSnapshot()
    {
        var snapshot = new List<FrozenRegion>();
        var visited = new HashSet<nint>();
        var seen = new HashSet<nint>();
        var tempRegions = new List<Region>();

        void CollectUnit(AtkUnitBase* unit, string name, int drawOrder)
        {
            var start = tempRegions.Count;
            ReadAddon(unit, name, tempRegions, visited, seen, ignoreMouse: true);
            var isTooltip = GameUiInterop.IsTooltipAddon(unit, name);
            for (var i = start; i < tempRegions.Count; i++)
            {
                var r = tempRegions[i];
                snapshot.Add(new FrozenRegion(r.Addon, isTooltip, drawOrder, r.Text.Block, r.Text.Layout,
                    r.Min, r.Max, r.ClipMin, r.ClipMax, r.Scale));
            }
        }

        var subtitle = DalamudServices.GameGui.GetAddonByName("TalkSubtitle");
        if (!subtitle.IsNull && subtitle.IsVisible)
            CollectUnit((AtkUnitBase*)subtitle.Address, "TalkSubtitle", 1000);

        var stage = AtkStage.Instance();
        if (stage != null && stage->RaptureAtkUnitManager != null)
        {
            ref var all = ref stage->RaptureAtkUnitManager->AllLoadedUnitsList;
            for (var i = (int)all.Count - 1; i >= 0; i--)
            {
                var unit = all.Entries[i].Value;
                if (unit == null || !unit->IsVisible) continue;
                var name = unit->NameString;
                if (string.IsNullOrEmpty(name) || IsChat(name) || name == "TalkSubtitle") continue;
                CollectUnit(unit, name, unit->DrawOrderIndex);
            }
        }

        var chatStart = tempRegions.Count;
        ReadChat(null, tempRegions, visited, seen, ignoreMouse: true, forceAllow: true);
        for (var i = chatStart; i < tempRegions.Count; i++)
        {
            var r = tempRegions[i];
            snapshot.Add(new FrozenRegion(r.Addon, false, 0, r.Text.Block, r.Text.Layout,
                r.Min, r.Max, r.ClipMin, r.ClipMax, r.Scale));
        }

        // Prioritize tooltips on top, then higher draw order, then smallest area so overlapping text resolves cleanly.
        snapshot.Sort((a, b) =>
        {
            var tip = b.IsTooltip.CompareTo(a.IsTooltip);
            if (tip != 0) return tip;
            var order = b.DrawOrder.CompareTo(a.DrawOrder);
            if (order != 0) return order;
            var areaA = (a.Max.X - a.Min.X) * (a.Max.Y - a.Min.Y);
            var areaB = (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y);
            return areaA.CompareTo(areaB);
        });
        return snapshot.ToArray();
    }

    private bool ResolveChatBottomAligned(AddonChatLogPanel* panel)
    {
        var key = (nint)panel;
        if (!chatScroll.TryGetValue(key, out var state))
        {
            state = new ChatScrollState();
            chatScroll[key] = state;
        }

        var lv = panel->LogViewer;
        var now = DateTime.UtcNow;

        if (lv.IsScrolledBottom)
        {
            state.WasBottom = true;
            state.LastTotalLines = lv.TotalLineCount;
            state.LastFirstVisible = lv.FirstLineVisible;
            state.NewMessageGraceUntilUtc = DateTime.MinValue;
            return true;
        }

        if (lv.DisplayableLineCount > 0 && lv.TotalLineCount <= lv.DisplayableLineCount)
        {
            state.WasBottom = true;
            state.LastTotalLines = lv.TotalLineCount;
            state.LastFirstVisible = lv.FirstLineVisible;
            return true;
        }

        // Right after a new message arrives while at the live bottom of chat, LogViewer can briefly
        // report IsScrolledBottom=false before its scroll tick catches up. Hold bottom alignment
        // across that short transition unless the user actually scrolled upward into history.
        if (state.WasBottom && lv.FirstLineVisible >= state.LastFirstVisible)
        {
            if (lv.TotalLineCount > state.LastTotalLines)
            {
                state.LastTotalLines = lv.TotalLineCount;
                state.LastFirstVisible = lv.FirstLineVisible;
                state.NewMessageGraceUntilUtc = now.AddMilliseconds(500);
                return true;
            }
            if (now < state.NewMessageGraceUntilUtc)
            {
                state.LastFirstVisible = lv.FirstLineVisible;
                return true;
            }
        }

        state.WasBottom = false;
        state.LastTotalLines = lv.TotalLineCount;
        state.LastFirstVisible = lv.FirstLineVisible;
        state.NewMessageGraceUntilUtc = DateTime.MinValue;
        return false;
    }

    private void ReadAddon(AtkUnitBase* addon, string name, List<Region> next, HashSet<nint> visited, HashSet<nint> seen, bool ignoreMouse)
    {
        // Talk has a dedicated dialogue node; the speaker's name node sits above it.
        bodyNode = name == "Talk" && GameUiInterop.TryGetTalkNodes(addon, out _, out var talkBody) ? talkBody : null;
        try
        {
            ReadNodes(&addon->UldManager, name, 0, next, visited, seen, ignoreMouse);
        }
        finally
        {
            bodyNode = null;
        }
    }

    private void ReadChat(AtkUnitBase* hovered, List<Region> next, HashSet<nint> visited, HashSet<nint> seen, bool ignoreMouse, bool forceAllow = false)
    {
        if (!forceAllow && !Allowed("ChatLog")) return;
        var addon = DalamudServices.GameGui.GetAddonByName("ChatLog");
        var chatLog = (AddonChatLog*)addon.Address;
        var selectedTab = addon.IsNull || !addon.IsVisible ? -1 : chatLog->TabIndex;
        var hoverInMain = chatLog != null && (hovered == &chatLog->AtkUnitBase
            || (hovered != null && hovered->HostId == chatLog->Id));
        AddonChatLogPanel* target = null;
        var targetName = string.Empty;
        var panels = GameUiInterop.GetVisibleChatPanels();
        for (var i = 0; i < panels.Count; i++)
        {
            var (panelIndex, panelName, panelAddr) = panels[i];
            var panel = (AddonChatLogPanel*)panelAddr;
            if (panel == null) continue;
            var attached = chatLog != null && panel->HostId == chatLog->Id;
            // Inactive tabs retain their text and can still report visible.
            if (attached)
            {
                if (panelIndex != selectedTab || (hovered != null && !hoverInMain)) continue;
            }
            else if (hovered != null && hovered != &panel->AtkUnitBase) continue;

            // When there is no collision hit, use the frontmost panel under the
            // mouse. Choose the panel before looking for readable text in it.
            if (hovered == null)
            {
                var textNode = panel->ChatText;
                if (textNode == null || !Visible((AtkResNode*)textNode)) continue;
                Bounds bounds;
                textNode->GetBounds(&bounds);
                if (!ignoreMouse && !NativeTextLayout.Contains(new(bounds.Pos1.X, bounds.Pos1.Y), new(bounds.Pos2.X, bounds.Pos2.Y), lastMouse)) continue;
                if (target != null && target->DrawOrderIndex >= panel->DrawOrderIndex) continue;
            }
            target = panel;
            targetName = panelName;
        }

        var before = next.Count;
        var text = target == null ? null : target->ChatText;
        if (text != null && visited.Add((nint)text) && Visible((AtkResNode*)text))
            ReadText(text, targetName, next, seen, ignoreMouse, ResolveChatBottomAligned(target));
        if (!ignoreMouse && hovered != null && next.Count == before && (DateTime.UtcNow - lastChatProbe).TotalSeconds > 5)
        {
            lastChatProbe = DateTime.UtcNow;
            DiagnosticLog.Write("chat-hover", $"No chat text under mouse: hovered='{LastHoveredAddon}', tab={selectedTab}, panel='{targetName}'");
        }
    }

    private void ReadNodes(AtkUldManager* manager, string name, int depth, List<Region> next, HashSet<nint> visited, HashSet<nint> seen, bool ignoreMouse)
    {
        if (depth > 8 || manager->NodeList == null || manager->NodeListCount > 4096) return;
        for (var i = 0; i < manager->NodeListCount && visited.Count < 4096; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || !visited.Add((nint)node) || !Visible(node)) continue;
            if (node->Type == NodeType.Text) ReadText((AtkTextNode*)node, name, next, seen, ignoreMouse);
            else if ((int)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null) ReadNodes(&component->UldManager, name, depth + 1, next, visited, seen, ignoreMouse);
            }
        }
    }

    private void ReadText(AtkTextNode* node, string name, List<Region> next, HashSet<nint> seen, bool ignoreMouse, bool chatBottomAligned = false)
    {
        if (node->Width == 0 || node->Height == 0 || node->FontSize == 0) return;
        Bounds bounds;
        node->GetBounds(&bounds);
        var min = new Vector2(bounds.Pos1.X, bounds.Pos1.Y);
        var max = new Vector2(bounds.Pos2.X, bounds.Pos2.Y);
        if (max.X <= min.X || max.Y <= min.Y || (!ignoreMouse && !NativeTextLayout.Contains(min, max, lastMouse))) return;
        var clipMin = min;
        var clipMax = max;
        for (var parent = node->ParentNode; parent != null; parent = parent->ParentNode)
        {
            if ((parent->NodeFlags & NodeFlags.Clip) == 0) continue;
            Bounds clip;
            parent->GetBounds(&clip);
            clipMin = Vector2.Max(clipMin, new(clip.Pos1.X, clip.Pos1.Y));
            clipMax = Vector2.Min(clipMax, new(clip.Pos2.X, clip.Pos2.Y));
        }
        if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y || (!ignoreMouse && !NativeTextLayout.Contains(clipMin, clipMax, lastMouse))) return;
        var raw = node->NodeText.AsSpan();
        var chat = IsChat(name);
        if (raw.Length == 0 || raw.Length > (chat ? 65536 : 16384)) return;
        var scale = new Vector2((max.X - min.X) / node->Width, (max.Y - min.Y) / node->Height);
        var visTop = chat && scale.Y > 0.001f ? Math.Max(0f, (clipMin.Y - min.Y) / scale.Y) : 0f;
        var visHeight = chat && scale.Y > 0.001f ? Math.Max(0f, (clipMax.Y - clipMin.Y) / scale.Y) : node->Height;
        var style = new TextStyle(node->Width, node->Height, node->FontSize, node->AlignmentFontType,
            node->LineSpacing, node->CharSpacing, node->TextFlags, chatBottomAligned,
            chat ? (int)MathF.Round(visTop) : 0,
            chat ? (int)MathF.Round(visHeight) : 0);
        var key = (nint)node;
        seen.Add(key);
        if (!cache.TryGetValue(key, out var cached) || cached.Style != style || !raw.SequenceEqual(cached.Raw))
        {
            // Use the game's own font measurement, never an approximate ImGui font.
            var glyphHeight = Measure(node, "国"u8).Y;
            if (glyphHeight <= 0) return;
            var advance = Measure(node, chat ? "国\r国"u8 : "国\n国"u8).Y - glyphHeight;
            if (advance <= 0) advance = Math.Max(node->FontSize, node->LineSpacing);
            Part[] parts = [];
            if (chat)
            {
                parts = NativeChatLayout.Build(raw, style.Width, style.Height, (int)node->AlignmentType,
                    glyphHeight, advance, style.ChatBottomAligned, prefix => Measure(node, prefix).X,
                    visTop, visHeight)
                    .Select(p => new Part(new RichBlock().Add(p.Text), p.Layout)).ToArray();
            }
            else
            {
                var text = SeString.Parse(raw).TextValue;
                if (text.Length is > 0 and <= 2048 && text.EnumerateRunes().Any(r => Japanese.IsJapanese(r.Value)))
                {
                    var layout = NativeTextLayout.Build(text, style.Width, style.Height, (int)node->AlignmentType,
                        glyphHeight, advance, (style.Flags & TextFlags.WordWrap) != 0,
                        prefix => Measure(node, Encoding.UTF8.GetBytes(prefix)));
                    parts = [new Part(new RichBlock().Add(text), layout)];
                }
            }
            cached = new Cached(style, raw.ToArray(), parts);
            cache[key] = cached;

            if (chat && !ignoreMouse && parts.Length > 0 && ChatLinesChanged is not null)
            {
                var take = Math.Min(3, parts.Length);
                var recent = new string[take];
                for (var i = 0; i < take; i++)
                    recent[i] = parts[parts.Length - take + i].Block.ScanText;
                ChatLinesChanged.Invoke(recent);
            }
        }
        var body = bodyNode != null && node == bodyNode;
        foreach (var part in cached.Parts)
            next.Add(new Region(name, part, min, max, clipMin, clipMax, scale, body));
    }

    private static bool IsChat(string name) => name.StartsWith("ChatLog", StringComparison.Ordinal);

    private bool Allowed(string name) => name switch
    {
        "Talk" or "TalkSubtitle" => config.CaptureTalk,
        "_BattleTalk" => config.CaptureBattleTalk,
        _ when IsChat(name) => config.NativeChatHover && config.CaptureChat,
        _ => config.NativeOtherTextHover,
    };

    private static bool Visible(AtkResNode* node)
    {
        var depth = 0;
        for (var current = node; current != null; current = current->ParentNode)
        {
            // Axis-aligned targets cannot faithfully cover rotated native text.
            if (++depth > 64 || !current->IsVisible() || current->IsDrawDisabled || Math.Abs(current->Rotation) > 0.001f) return false;
        }
        return true;
    }

    private static Vector2 Measure(AtkTextNode* node, ReadOnlySpan<byte> text)
    {
        Span<byte> bytes = text.Length < 1024 ? stackalloc byte[text.Length + 1] : new byte[text.Length + 1];
        text.CopyTo(bytes);
        bytes[^1] = 0;
        ushort width = 0, height = 0;
        fixed (byte* pointer = bytes)
            node->GetTextDrawSize(&width, &height, pointer);
        return new Vector2(width, height);
    }

    /// <summary>The visible dialogue text blocks with on-screen glyph cells (controller mode).</summary>
    public List<TextTarget> Targets()
    {
        var viewport = ImGui.GetMainViewport();
        var list = new List<TextTarget>();
        foreach (var region in regions)
        {
            var origin = region.Min + viewport.Pos;
            var clipMin = region.ClipMin + viewport.Pos;
            var clipMax = region.ClipMax + viewport.Pos;
            var cells = new List<(int, Vector2, Vector2)>();
            foreach (var cell in region.Text.Layout.Cells)
            {
                var min = Vector2.Max(origin + cell.Min * region.Scale, clipMin);
                var max = Vector2.Min(origin + cell.Max * region.Scale, clipMax);
                if (max.X > min.X && max.Y > min.Y)
                    cells.Add((cell.Index, min, max));
            }

            if (cells.Count == 0)
                continue;
            var addon = region.Addon;
            list.Add(new TextTarget(addon, region.Text.Block, cells, (block, index) =>
            {
                var sentence = NameKana.Apply(block.ScanText, PlayerName.Replacements(config), ref index);
                return new ScanContext(sentence, index, $"{config.DocumentTitle} — {addon}");
            }, region.Body));
        }

        return list;
    }

    public void Draw(PopupManager popups)
    {
        var viewport = ImGui.GetMainViewport();
        var mouse = ImGui.GetMousePos();
        lastMouse = mouse - viewport.Pos;
        if (!config.NativeTextHover || !ImGui.IsMousePosValid() || ImGui.GetIO().AppFocusLost) return;
        // The detector creates no window/item and never captures clicks. Plugin windows
        // and nested dictionary popups retain priority over native text behind them.
        var blocked = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) || popups.ContainsMouse(mouse);
        foreach (var region in regions.OrderBy(r => (r.Max.X - r.Min.X) * (r.Max.Y - r.Min.Y)))
        {
            var origin = region.Min + viewport.Pos;
            var draw = ImGui.GetBackgroundDrawList();
            if (popups.HighlightFor(region.Text.Block, 0) is { } highlight)
            {
                var theme = Rendering.Css.PopupTheme.Current;
                var highlightColor = theme.IsPlugin ? ImGui.GetColorU32(ImGuiCol.TextSelectedBg) : Rendering.Css.CssColor.ToAbgr(theme.Highlight);
                foreach (var glyph in region.Text.Layout.Cells)
                {
                    if (glyph.Index < highlight.Start || glyph.Index >= highlight.End) continue;
                    var glyphMin = Vector2.Max(origin + glyph.Min * region.Scale, region.ClipMin + viewport.Pos);
                    var glyphMax = Vector2.Min(origin + glyph.Max * region.Scale, region.ClipMax + viewport.Pos);
                    if (glyphMax.X > glyphMin.X && glyphMax.Y > glyphMin.Y)
                        draw.AddRectFilled(glyphMin, glyphMax, highlightColor);
                }
            }
            if (config.NativeHoverDebug)
                draw.AddRect(origin, region.Max + viewport.Pos, 0xFF00FFFF);
            if (region.Text.Layout.HitScreen(mouse, origin, region.Scale, region.ClipMin + viewport.Pos, region.ClipMax + viewport.Pos) is not { } cell) continue;
            if (config.NativeHoverDebug)
                draw.AddRectFilled(cell.Min, cell.Max, 0x6000FFFF);
            if (blocked) continue;
            popups.Report(new TextHit(region.Text.Block, cell.Index, cell.Min, cell.Max), 0,
                (block, index) =>
                {
                    // The game draws the name in Latin letters; the sentence Hachidori gets uses katakana.
                    var sentence = NameKana.Apply(block.ScanText, PlayerName.Replacements(config), ref index);
                    return new ScanContext(sentence, index, $"{config.DocumentTitle} — {region.Addon}");
                }, keepPrevious: !IsChat(region.Addon));
            return;
        }
    }

    public void Dispose()
    {
        DalamudServices.Framework.Update -= Update;
        regions = [];
        cache.Clear();
        chatScroll.Clear();
    }
}
