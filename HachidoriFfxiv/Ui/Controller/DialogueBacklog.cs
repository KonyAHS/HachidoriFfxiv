using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using HachidoriFfxiv.Audio;
using HachidoriFfxiv.Capture;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Media;
using HachidoriFfxiv.Rendering;
using HachidoriFfxiv.Rendering.Css;

namespace HachidoriFfxiv.Ui;

/// <summary>
/// On-demand, controller-first dialogue backlog drawer for scanning and mining previous lines
/// (with cached screenshot preview, media badges, and voice replay). Also works cleanly with mouse.
/// </summary>
public sealed class DialogueBacklog : IDisposable
{
    public sealed record RenderedLine(
        GameLine Line,
        Vector2 CardMin,
        Vector2 CardMax,
        IReadOnlyList<(int Index, Vector2 Min, Vector2 Max)> ClippedCells);

    private readonly Configuration config;
    private readonly TextCapture capture;
    private readonly MediaMiner media;
    private readonly PopupManager popups;
    private readonly HachidoriService service;
    private readonly AudioPlayer audio;
    private readonly Fonts fonts;

    private readonly List<GameLine> filteredLines = new();
    private readonly List<long> filteredIds = new();
    private readonly List<RenderedLine> renderedLines = new();

    private bool pendingScrollToSelected;
    private float pendingListScrollDelta;
    private Vector2 windowMin;
    private Vector2 windowMax;

    // Lazy preview thumbnail of the currently selected line's cached screenshot.
    private long previewLineId;
    private byte[]? previewBytes;
    private Task<IDalamudTextureWrap>? previewTask;
    private IDalamudTextureWrap? previewTexture;

    // Voice replay feedback toast.
    private int replaySequence;
    private string? replayNotice;
    private DateTime replayNoticeUntil = DateTime.MinValue;

    public DialogueBacklog(
        Configuration config,
        TextCapture capture,
        MediaMiner media,
        PopupManager popups,
        HachidoriService service,
        AudioPlayer audio,
        Fonts fonts)
    {
        this.config = config;
        this.capture = capture;
        this.media = media;
        this.popups = popups;
        this.service = service;
        this.audio = audio;
        this.fonts = fonts;
    }

    public ControllerBacklogState State { get; } = new();
    public bool Open => State.Open;
    public int LineCount => RefreshFilter();
    public GameLine? SelectedLine => FindSelectedLine();
    public long? LatestDialogueLineId => GetLatestDialogueLineId();

    /// <summary>Whether the drawer was opened standalone (e.g. /hachidori backlog without controller mode).</summary>
    public bool Standalone { get; private set; }

    public bool ContainsPoint(Vector2 pos) =>
        Open && pos.X >= windowMin.X && pos.X <= windowMax.X && pos.Y >= windowMin.Y && pos.Y <= windowMax.Y;

    public bool OpenAtOffset(int offsetFromLatest, bool autoOpened = false, bool standalone = false,
        BacklogSnapEdge snap = BacklogSnapEdge.FirstStop, float snapX = 0f)
    {
        RefreshFilter();
        if (!State.OpenAt(filteredIds, offsetFromLatest, explicitLineId: null, autoOpened: autoOpened,
                pinnedFromAdvance: false, snap: snap, snapX: snapX))
            return false;
        Standalone = standalone;
        pendingScrollToSelected = true;
        PrewarmAroundSelection();
        return true;
    }

    public bool OpenPinned(long lineId)
    {
        RefreshFilter();
        if (!State.OpenAt(filteredIds, offsetFromLatest: 0, explicitLineId: lineId, autoOpened: false,
                pinnedFromAdvance: true, snap: BacklogSnapEdge.None))
            return false;
        Standalone = false;
        pendingScrollToSelected = true;
        PrewarmAroundSelection();
        return true;
    }

    public void Close()
    {
        if (!Open)
            return;
        State.Close();
        Standalone = false;
        renderedLines.Clear();
        pendingScrollToSelected = false;
        pendingListScrollDelta = 0f;
        ReleasePreview();
    }

    public bool StepLine(int delta, float cursorX)
    {
        RefreshFilter();
        if (!State.Step(filteredIds, delta, cursorX))
            return false;
        pendingScrollToSelected = true;
        PrewarmAroundSelection();
        return true;
    }

    public void ScrollList(float deltaY) => pendingListScrollDelta += deltaY;

    /// <summary>Switches selection if <paramref name="pointer"/> glides onto another visible line card.</summary>
    public bool TrySelectLineAt(Vector2 pointer)
    {
        if (!Open)
            return false;
        foreach (var item in renderedLines)
        {
            if (pointer.X >= item.CardMin.X && pointer.X <= item.CardMax.X
                && pointer.Y >= item.CardMin.Y && pointer.Y <= item.CardMax.Y)
            {
                if (item.Line.Id == State.SelectedLineId)
                    return false;
                RefreshFilter();
                if (State.SelectLine(filteredIds, item.Line.Id))
                {
                    PrewarmAroundSelection();
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Returns the rendered scannable target for the currently selected backlog line.</summary>
    public RenderedLine? SelectedTarget()
    {
        if (!Open)
            return null;
        foreach (var item in renderedLines)
        {
            if (item.Line.Id == State.SelectedLineId && item.ClippedCells.Count > 0)
                return item;
        }
        return null;
    }

    /// <summary>Builds a <see cref="ScanContext"/> bound to the exact historical <see cref="GameLine.Id"/>.</summary>
    public ScanContext ContextFor(GameLine line, int index)
    {
        var title = line.Speaker.Length > 0 ? $"{config.DocumentTitle} — {line.Speaker}" : config.DocumentTitle;
        return new ScanContext(line.Text, index, title, line.Id);
    }

    /// <summary>Replays the captured game voice (or TTS) for <paramref name="lineId"/>.</summary>
    public void ReplayVoice(long lineId)
    {
        if (lineId <= 0)
        {
            SetReplayNotice("No dialogue line captured yet", 2.0);
            return;
        }
        var seq = Interlocked.Increment(ref replaySequence);
        SetReplayNotice("Loading voice…", 2.5);
        _ = Task.Run(async () =>
        {
            try
            {
                var data = await media.GetLineVoiceReplayAsync(lineId).ConfigureAwait(false);
                if (seq != replaySequence)
                    return;
                if (data is null || data.Length == 0)
                {
                    SetReplayNotice("No cached voice for this line", 2.5);
                    return;
                }
                audio.Play(data);
                SetReplayNotice("Repeating voice", 1.8);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("audio", $"Voice replay failed: {ex.Message}");
                SetReplayNotice("Voice replay failed", 2.5);
            }
        });
    }

    /// <summary>Replays the most recently captured dialogue line (useful during cutscenes outside controller mode).</summary>
    public void ReplayLatestDialogue()
    {
        var latest = GetLatestDialogueLineId();
        if (latest is { } lineId)
            ReplayVoice(lineId);
        else
            SetReplayNotice("No dialogue line captured yet", 2.0);
    }

    /// <summary>Draws a subtle foreground feedback pill when replaying a line while the backlog drawer is closed.</summary>
    public void DrawReplayNoticeOverlay()
    {
        if (Open || replayNotice is null)
            return;
        if (DateTime.UtcNow >= replayNoticeUntil)
        {
            replayNotice = null;
            return;
        }

        var viewport = ImGui.GetMainViewport();
        var draw = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize();
        var text = replayNotice;
        var width = ImGui.CalcTextSize(text).X;
        var pos = new Vector2(viewport.Pos.X + (viewport.Size.X - width) * 0.5f, viewport.Pos.Y + viewport.Size.Y - size * 3.8f);
        var pad = new Vector2(size * 0.65f, size * 0.32f);
        var accent = CssColor.ToAbgr(PopupTheme.Current.Accent with { W = 0.85f });
        draw.AddRectFilled(pos - pad, pos + new Vector2(width, size) + pad, 0xCC10131C, size * 0.5f);
        draw.AddRect(pos - pad, pos + new Vector2(width, size) + pad, accent, size * 0.5f);
        draw.AddText(font, size, pos, 0xFFFFFFFF, text);
    }

    /// <summary>Draws the backlog drawer and records clipped screen-space glyph cells for controller scanning.</summary>
    public void Draw(bool controllerActive)
    {
        renderedLines.Clear();
        if (!Open)
            return;

        // Standalone mode (opened via /hachidori backlog without controller mode) closes on Esc.
        if (Standalone && !controllerActive && ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            Close();
            return;
        }

        RefreshFilter();
        var selectedIdx = State.Sync(filteredIds);
        if (selectedIdx < 0 || !Open)
        {
            ReleasePreview();
            return;
        }

        var selectedLine = filteredLines[selectedIdx];
        UpdatePreviewTexture(selectedLine.Id);

        var vp = ImGui.GetMainViewport();
        var scale = ImGuiHelpers.GlobalScale;
        var drawerW = MathF.Min(vp.Size.X * 0.74f, 880f * scale);
        var drawerH = MathF.Min(vp.Size.Y * 0.48f, 430f * scale);
        var hintsReserve = config.ControllerShowHints && controllerActive ? 54f * scale : 28f * scale;
        var pos = new Vector2(
            vp.Pos.X + (vp.Size.X - drawerW) * 0.5f,
            vp.Pos.Y + vp.Size.Y - drawerH - hintsReserve);
        pos.Y = MathF.Max(vp.Pos.Y + 24f * scale, pos.Y);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(drawerW, drawerH), ImGuiCond.Always);

        var accentVec = PopupTheme.Current.Accent with { W = 1f };
        var accentCol = CssColor.ToAbgr(accentVec);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking
                    | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        using var bg = ImRaii.PushColor(ImGuiCol.WindowBg, new Vector4(0.07f, 0.08f, 0.11f, 0.92f));
        using var border = ImRaii.PushColor(ImGuiCol.Border, accentVec with { W = 0.65f });
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 10f * scale);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f * scale, 10f * scale));

        if (ImGui.Begin("##hachidori-dialogue-backlog", flags))
        {
            windowMin = ImGui.GetWindowPos();
            windowMax = windowMin + ImGui.GetWindowSize();

            var showSidebar = config.CaptureScreenshot || previewTexture is not null;
            DrawHeader(selectedIdx, selectedLine, controllerActive, showSidebar, scale, accentVec);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(3f);

            var avail = ImGui.GetContentRegionAvail();
            var sidebarW = showSidebar ? MathF.Min(210f * scale, avail.X * 0.28f) : 0f;
            var listW = showSidebar ? MathF.Max(200f * scale, avail.X - sidebarW - 10f * scale) : avail.X;

            DrawLineList(selectedIdx, listW, avail.Y, controllerActive, scale, accentCol, accentVec);

            if (showSidebar)
            {
                ImGui.SameLine(0f, 10f * scale);
                DrawPreviewSidebar(selectedLine, sidebarW, avail.Y, controllerActive, scale, accentVec);
            }
        }
        ImGui.End();
    }

    private void DrawHeader(int selectedIdx, GameLine selectedLine, bool controllerActive, bool showSidebar, float scale, Vector4 accentVec)
    {
        var backOffset = filteredLines.Count - 1 - selectedIdx;
        var offsetText = backOffset == 0 ? "Latest" : $"-{backOffset}";

        ImGui.TextColored(accentVec, "Dialogue Backlog");
        ImGui.SameLine(0f, 10f * scale);
        ImGui.TextDisabled($"{offsetText}  ({selectedIdx + 1}/{filteredLines.Count})");

        if (State.PinnedFromLiveLineId is not null)
        {
            ImGui.SameLine(0f, 12f * scale);
            ImGui.TextColored(new Vector4(0.98f, 0.78f, 0.36f, 1f), "Dialogue advanced — ↓ for newer");
        }
        else if (State.HasNewerSinceOpen)
        {
            ImGui.SameLine(0f, 12f * scale);
            ImGui.TextColored(new Vector4(0.55f, 0.88f, 0.65f, 1f), "New line arrived — ↓ for latest");
        }

        if (replayNotice is not null && DateTime.UtcNow < replayNoticeUntil)
        {
            ImGui.SameLine(0f, 12f * scale);
            ImGui.TextColored(new Vector4(0.68f, 0.85f, 1f, 1f), $"· {replayNotice}");
        }
        else if (replayNotice is not null)
        {
            replayNotice = null;
        }

        var closeW = 24f * scale;
        var closeX = ImGui.GetWindowContentRegionMax().X - closeW;
        if (!showSidebar)
        {
            var ps = config.ControllerPlayStationLabels;
            var replayBind = config.ControllerReplayVoiceButton != Dalamud.Game.ClientState.GamePad.GamepadButtons.None
                ? $" ({GamepadNavigator.ButtonLabel(config.ControllerReplayVoiceButton, ps)})"
                : string.Empty;
            var repeatLabel = $"Repeat line{replayBind}";
            var repeatW = ImGui.CalcTextSize(repeatLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
            var repeatX = closeX - repeatW - 8f * scale;
            if (repeatX > ImGui.GetCursorPosX() + 12f)
            {
                var canReplay = media.CanReplayLineVoice(selectedLine.Id);
                ImGui.SameLine(repeatX);
                using (ImRaii.Disabled(!canReplay))
                {
                    if (ImGui.SmallButton($"{repeatLabel}##backlog-header-replay"))
                        ReplayVoice(selectedLine.Id);
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(canReplay ? "Repeat the cached voice for the selected line" : "No cached voice for this line");
            }
        }
        if (closeX > ImGui.GetCursorPosX() + 12f)
        {
            ImGui.SameLine(closeX);
            if (ImGui.SmallButton("×##backlog-close"))
                Close();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(controllerActive ? "Close backlog" : "Close backlog (Esc)");
        }
    }

    private void DrawLineList(int selectedIdx, float width, float height, bool controllerActive, float scale, uint accentCol, Vector4 accentVec)
    {
        using var child = ImRaii.Child("##hachidori-backlog-lines", new Vector2(width, height), false,
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.HorizontalScrollbar);
        if (!child.Success)
            return;

        if (MathF.Abs(pendingListScrollDelta) > 0.5f)
        {
            ImGui.SetScrollY(Math.Clamp(ImGui.GetScrollY() + pendingListScrollDelta, 0f, ImGui.GetScrollMaxY()));
            pendingListScrollDelta = 0f;
        }

        var clipMin = ImGui.GetWindowPos();
        var clipMax = clipMin + ImGui.GetWindowSize();
        var draw = ImGui.GetWindowDrawList();
        var innerW = MathF.Max(80f, ImGui.GetContentRegionAvail().X - 4f * scale);
        var cardPadX = 10f * scale;
        var cardPadY = 6f * scale;
        var now = DateTime.UtcNow;

        for (var i = 0; i < filteredLines.Count; i++)
        {
            var line = filteredLines[i];
            var isSelected = i == selectedIdx;
            var offset = filteredLines.Count - 1 - i;

            using var id = ImRaii.PushId((int)(line.Id & 0x7FFFFFFF));
            var cardTop = ImGui.GetCursorScreenPos();
            var startY = ImGui.GetCursorPosY();

            // Reserve card padding and draw the line content.
            ImGui.SetCursorPos(new Vector2(ImGui.GetCursorPosX() + cardPadX, startY + cardPadY));

            // Compact metadata row: offset, speaker, age, media pills.
            var status = media.GetLineMediaStatus(line.Id);
            ImGui.TextDisabled(offset == 0 ? "NOW" : $"-{offset}");
            if (line.Speaker.Length > 0)
            {
                ImGui.SameLine(0f, 8f * scale);
                ImGui.TextColored(new Vector4(0.76f, 0.62f, 0.96f, isSelected ? 1f : 0.72f), line.Speaker);
            }
            if (line.Source == LineSource.BattleTalk)
            {
                ImGui.SameLine(0f, 8f * scale);
                ImGui.TextColored(new Vector4(0.95f, 0.72f, 0.42f, isSelected ? 0.95f : 0.6f), "[Battle]");
            }
            else if (line.Source == LineSource.Chat)
            {
                ImGui.SameLine(0f, 8f * scale);
                ImGui.TextDisabled("[Chat]");
            }

            var badges = BuildMediaBadges(status);
            var age = FormatAge(now - line.Time);
            ImGui.SameLine(0f, 8f * scale);
            ImGui.TextDisabled(badges.Length > 0 ? $"{age} · {badges}" : age);

            // Per-line Repeat button right on each card that has cached audio (or TTS/recording).
            var cardReplayClicked = false;
            var canReplayLine = media.CanReplayLineVoice(line.Id);
            if (canReplayLine)
            {
                const string btnText = "Repeat";
                var btnW = ImGui.CalcTextSize(btnText).X + ImGui.GetStyle().FramePadding.X * 2f;
                var btnX = innerW - cardPadX - btnW;
                if (btnX > ImGui.GetCursorPosX() + 8f * scale)
                    ImGui.SameLine(btnX);
                else
                    ImGui.SameLine(0f, 8f * scale);
                if (ImGui.SmallButton($"{btnText}##line-replay"))
                {
                    cardReplayClicked = true;
                    State.SelectLine(filteredIds, line.Id);
                    PrewarmAroundSelection();
                    ReplayVoice(line.Id);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Repeat the cached voice for this line");
            }

            // Dialogue text body.
            ImGui.SetCursorPosX(cardPadX);
            var textWidth = MathF.Max(60f, innerW - cardPadX * 2f);
            TextHit? mouseHit;
            using (fonts.Reader.Push())
            using (ImRaii.PushColor(ImGuiCol.Text, isSelected ? new Vector4(0.97f, 0.98f, 1f, 1f) : new Vector4(0.75f, 0.78f, 0.84f, 0.82f)))
            {
                mouseHit = RichText.Draw(line.Block, textWidth, popups.HighlightFor(line.Block, 0), hitTest: true);
            }

            if (mouseHit is { } hit)
            {
                if (!controllerActive)
                    State.SelectLine(filteredIds, line.Id);
                popups.Report(hit, 0, (b, idx) => ContextFor(line, idx));
            }

            var cardBottomY = ImGui.GetCursorScreenPos().Y + cardPadY;
            var cardMin = new Vector2(cardTop.X, cardTop.Y);
            var cardMax = new Vector2(cardTop.X + innerW, cardBottomY);

            // Draw subtle card highlight behind/around the line.
            if (isSelected)
            {
                draw.AddRectFilled(cardMin, cardMax, (accentCol & 0x00FFFFFF) | 0x26000000, 6f * scale);
                draw.AddRect(cardMin, cardMax, (accentCol & 0x00FFFFFF) | 0x99000000, 6f * scale, ImDrawFlags.None, 1.4f);
                draw.AddRectFilled(cardMin + new Vector2(1f, 3f), new Vector2(cardMin.X + 4f * scale, cardMax.Y - 3f), accentCol, 2f);
            }
            else
            {
                draw.AddRectFilled(cardMin, cardMax, 0x1EFFFFFF, 6f * scale);
            }

            // Click anywhere on a card with mouse to jump selection to it.
            if (!cardReplayClicked
                && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                && ImGui.IsMouseHoveringRect(cardMin, cardMax, true)
                && !popups.ContainsMouse(ImGui.GetMousePos()))
            {
                State.SelectLine(filteredIds, line.Id);
                PrewarmAroundSelection();
            }

            // Collect glyph cells clipped to the visible scroll viewport.
            var visibleCard = cardMax.Y >= clipMin.Y && cardMin.Y <= clipMax.Y;
            if (visibleCard)
            {
                var rawCells = RichText.GlyphRects(line.Block);
                var clipped = new List<(int Index, Vector2 Min, Vector2 Max)>(rawCells.Count);
                foreach (var cell in rawCells)
                {
                    var cy = (cell.Min.Y + cell.Max.Y) * 0.5f;
                    if (cy >= clipMin.Y && cy <= clipMax.Y)
                        clipped.Add(cell);
                }
                renderedLines.Add(new RenderedLine(line, Vector2.Max(cardMin, clipMin), Vector2.Min(cardMax, clipMax), clipped));
            }

            if (isSelected && pendingScrollToSelected)
            {
                ImGui.SetScrollHereY(0.55f);
                pendingScrollToSelected = false;
            }
            else if (isSelected && controllerActive && (!visibleCard || (renderedLines.Count > 0 && renderedLines[^1].Line.Id == line.Id && renderedLines[^1].ClippedCells.Count == 0)))
            {
                pendingScrollToSelected = true;
            }

            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + cardPadY + 4f * scale);
        }
    }

    private void DrawPreviewSidebar(GameLine line, float width, float height, bool controllerActive, float scale, Vector4 accentVec)
    {
        using var side = ImRaii.Child("##hachidori-backlog-preview", new Vector2(width, height), false,
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!side.Success)
            return;

        var innerW = ImGui.GetContentRegionAvail().X;
        var thumbH = MathF.Round(innerW * 9f / 16f);
        var thumbMin = ImGui.GetCursorScreenPos();
        var thumbMax = thumbMin + new Vector2(innerW, thumbH);
        var draw = ImGui.GetWindowDrawList();

        if (previewTexture is not null)
        {
            draw.AddImageRounded(previewTexture.Handle, thumbMin, thumbMax, Vector2.Zero, Vector2.One, 0xFFFFFFFF, 5f * scale);
            draw.AddRect(thumbMin, thumbMax, 0x55FFFFFF, 5f * scale);
        }
        else
        {
            draw.AddRectFilled(thumbMin, thumbMax, 0x44161A24, 5f * scale);
            draw.AddRect(thumbMin, thumbMax, 0x33FFFFFF, 5f * scale);
            var msg = previewTask is not null ? "Loading…" : "No cached shot";
            var msgSize = ImGui.CalcTextSize(msg);
            draw.AddText(thumbMin + (new Vector2(innerW, thumbH) - msgSize) * 0.5f, 0x99CBD4E1, msg);
        }
        ImGui.Dummy(new Vector2(innerW, thumbH));
        ImGuiHelpers.ScaledDummy(6f);

        var status = media.GetLineMediaStatus(line.Id);
        ImGui.TextDisabled("Cached for Anki:");
        DrawStatusBullet("Screenshot", status.HasScreenshot);
        DrawStatusBullet("Game voice", status.HasVoice);
        DrawStatusBullet("Animation clip", status.HasClip);

        ImGuiHelpers.ScaledDummy(6f);
        var ps = config.ControllerPlayStationLabels;
        var replayBind = config.ControllerReplayVoiceButton != Dalamud.Game.ClientState.GamePad.GamepadButtons.None
            ? $" ({GamepadNavigator.ButtonLabel(config.ControllerReplayVoiceButton, ps)})"
            : string.Empty;
        var canReplay = media.CanReplayLineVoice(line.Id);
        using (ImRaii.Disabled(!canReplay))
        {
            if (ImGui.Button($"Repeat voice{replayBind}##backlog-replay", new Vector2(innerW, 0f)))
                ReplayVoice(line.Id);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(canReplay ? "Repeat the cached voice for this line" : "No cached voice for this line");

        if (!controllerActive)
        {
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.TextDisabled("Hover any word to look up & mine.");
        }
        else if (accentVec.W > 0f)
        {
            ImGuiHelpers.ScaledDummy(4f);
            ImGui.TextDisabled("D-Pad ↑/↓ crosses lines.");
        }
    }

    private static void DrawStatusBullet(string label, bool available)
    {
        var color = available ? new Vector4(0.46f, 0.88f, 0.58f, 1f) : new Vector4(0.55f, 0.58f, 0.65f, 0.7f);
        ImGui.TextColored(color, available ? $"● {label}" : $"○ {label}");
    }

    private static string BuildMediaBadges(LineMediaStatus status)
    {
        var parts = new List<string>(3);
        if (status.HasVoice) parts.Add("Voice");
        if (status.HasClip) parts.Add("Clip");
        if (status.HasScreenshot) parts.Add("Shot");
        return string.Join(" · ", parts);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 5) return "just now";
        if (age.TotalSeconds < 60) return $"{(int)age.TotalSeconds}s ago";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        return $"{(int)age.TotalHours}h ago";
    }

    private int RefreshFilter()
    {
        filteredLines.Clear();
        filteredIds.Clear();
        var includeChat = config.ControllerBacklogIncludeChat;
        var history = capture.History;
        for (var i = 0; i < history.Count; i++)
        {
            var line = history[i];
            if (line.Source is LineSource.Talk or LineSource.BattleTalk
                || (includeChat && line.Source == LineSource.Chat))
            {
                filteredLines.Add(line);
                filteredIds.Add(line.Id);
            }
        }
        return filteredLines.Count;
    }

    private GameLine? FindSelectedLine()
    {
        if (!Open)
            return null;
        RefreshFilter();
        for (var i = filteredLines.Count - 1; i >= 0; i--)
        {
            if (filteredLines[i].Id == State.SelectedLineId)
                return filteredLines[i];
        }
        return null;
    }

    private long? GetLatestDialogueLineId()
    {
        var history = capture.History;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Source is LineSource.Talk or LineSource.BattleTalk)
                return history[i].Id;
        }
        return null;
    }

    private void PrewarmAroundSelection()
    {
        if (!config.TokenizedHover)
            return;
        var idx = State.Sync(filteredIds);
        if (idx < 0)
            return;
        for (var i = Math.Max(0, idx - 2); i <= Math.Min(filteredLines.Count - 1, idx + 2); i++)
            _ = service.SegmentAsync(filteredLines[i].Text);
    }

    private void UpdatePreviewTexture(long lineId)
    {
        var bytes = media.GetLineScreenshot(lineId);
        if (lineId != previewLineId || !ReferenceEquals(bytes, previewBytes))
        {
            ReleasePreview();
            previewLineId = lineId;
            previewBytes = bytes;
            if (bytes is { Length: > 0 })
                previewTask = DalamudServices.TextureProvider.CreateFromImageAsync(bytes);
        }

        if (previewTask is not null && previewTask.IsCompleted)
        {
            if (previewTask.IsCompletedSuccessfully)
                previewTexture = previewTask.Result;
            previewTask = null;
        }
    }

    private void ReleasePreview()
    {
        previewTexture?.Dispose();
        previewTexture = null;
        if (previewTask is { IsCompletedSuccessfully: true })
            previewTask.Result.Dispose();
        else if (previewTask is not null)
            _ = previewTask.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
        previewTask = null;
        previewLineId = 0;
        previewBytes = null;
    }

    private void SetReplayNotice(string message, double seconds)
    {
        replayNotice = message;
        replayNoticeUntil = DateTime.UtcNow.AddSeconds(seconds);
    }

    public void Dispose() => ReleasePreview();
}
