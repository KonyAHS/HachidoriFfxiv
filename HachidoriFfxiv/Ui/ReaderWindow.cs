using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using HachidoriFfxiv.Capture;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Ui;

/// <summary>
/// The in-game texthooker: the current dialogue line (and optionally history) drawn with a
/// Japanese font, where hovering a character opens the Hachidori popup.
/// </summary>
public sealed class ReaderWindow : Window
{
    private readonly Configuration config;
    private readonly TextCapture capture;
    private readonly PopupManager popups;
    private readonly Fonts fonts;
    private GameLine? scrolledTo;
    private Vector2 windowMin;
    private Vector2 windowMax;
    private bool revealed;
    private bool hiddenStyle;

    // Backlog browsing: while the user is scrolled back, new lines don't move the view. It
    // returns to the latest line when they scroll back down, or after the mouse has been away
    // from the reader (and its popups) for FollowResumeSeconds.
    private const double FollowResumeSeconds = 5;
    private bool following = true;
    private GameLine? browsing;
    private DateTime lastAttended = DateTime.UtcNow;

    public ReaderWindow(Configuration config, TextCapture capture, PopupManager popups, Fonts fonts)
        : base("Hachidori Reader##hachidori-ffxiv-reader", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse)
    {
        this.config = config;
        this.capture = capture;
        this.popups = popups;
        this.fonts = fonts;
        Size = new Vector2(720, 140);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = false;
        IsOpen = config.ReaderEnabled;
    }

    /// <summary>Shown manually with /hachidori reader, independent of dialogue.</summary>
    public bool Pinned { get; set; }

    public override void OnClose()
    {
        config.ReaderEnabled = false;
        config.Save();
    }

    public override bool DrawConditions()
    {
        if (!config.ReaderEnabled)
            return false;
        if (Pinned || !config.ReaderAutoShowWithDialogue)
            return true;
        if (capture.TalkVisible || capture.BattleTalkVisible || popups.AnyOpen)
            return true;
        // Keep the backlog up while the mouse is on it.
        if (ImGui.IsMousePosValid() && ImGui.IsMouseHoveringRect(windowMin, windowMax, false))
            return true;
        // Linger briefly after the dialogue closes so the last line can still be read.
        return capture.Current is { } line && (DateTime.UtcNow - line.Time).TotalSeconds < 6 && ImGui.IsMousePosValid();
    }

    public override void PreDraw()
    {
        BgAlpha = config.ReaderBackgroundAlpha;
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (config.ReaderLocked)
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar;
        if (!config.ReaderShowHistory)
            Flags |= ImGuiWindowFlags.NoScrollWithMouse; // the wheel steps through lines instead

        // Hide everything but the hover area until the mouse enters last frame's window rect.
        // Once revealed, stay revealed while one of the reader's popups is open so the mouse can
        // move into it; popups opened from native text alone don't reveal the reader.
        var mouseInside = ImGui.IsMousePosValid() && ImGui.IsMouseHoveringRect(windowMin, windowMax, false);
        revealed = !config.ReaderHideUntilHovered || mouseInside || (revealed && popups.AnyOpen);
        if (mouseInside || (revealed && popups.AnyOpen))
            lastAttended = DateTime.UtcNow;
        else if (!following && (DateTime.UtcNow - lastAttended).TotalSeconds >= FollowResumeSeconds)
            Follow();
        hiddenStyle = !revealed;
        if (hiddenStyle)
        {
            BgAlpha = 0f;
            Flags |= ImGuiWindowFlags.NoTitleBar;
            ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        }

        Position = null;
        PositionCondition = ImGuiCond.None;

        if (config.ReaderAnchor != ReaderAnchor.Free && capture.TalkVisible)
        {
            var talk = Plugin.GameGui.GetAddonByName("Talk");
            if (!talk.IsNull && talk.IsVisible)
            {
                var size = talk.ScaledSize;
                var pos = talk.Position;
                Size = Size is { } s ? new Vector2(Math.Max(size.X, 320), s.Y) : new Vector2(size.X, 140);
                SizeCondition = ImGuiCond.Always;
                var windowHeight = Size.Value.Y;
                Position = config.ReaderAnchor == ReaderAnchor.AboveTalk
                    ? new Vector2(pos.X, pos.Y - windowHeight - 8)
                    : new Vector2(pos.X, pos.Y + size.Y + 8);
                PositionCondition = ImGuiCond.Always;
                return;
            }
        }

        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void PostDraw()
    {
        if (hiddenStyle)
            ImGui.PopStyleColor();
    }

    public override void Draw()
    {
        windowMin = ImGui.GetWindowPos();
        windowMax = windowMin + ImGui.GetWindowSize();
        if (!revealed)
            return;

        using var font = fonts.Reader.Push();
        var width = ImGui.GetContentRegionAvail().X;
        var current = capture.Current;
        if (current is null)
        {
            ImGui.TextDisabled("Waiting for Japanese dialogue…  (/hachidori <text> to look something up)");
            return;
        }

        if (config.ReaderShowHistory)
        {
            using var child = Dalamud.Interface.Utility.Raii.ImRaii.Child("##history", new Vector2(width, ImGui.GetContentRegionAvail().Y), false);
            if (child.Success)
            {
                var inner = ImGui.GetContentRegionAvail().X;
                foreach (var line in capture.History)
                    DrawLine(line, inner);

                // A wheel scroll decides whether we're browsing: away from the bottom stops
                // following, back at the bottom resumes it.
                if (ImGui.IsWindowHovered() && ImGui.GetIO().MouseWheel != 0)
                    following = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f && ImGui.GetIO().MouseWheel < 0;
                if (following && scrolledTo != current)
                {
                    ImGui.SetScrollHereY(1f);
                    scrolledTo = current;
                }
            }

            return;
        }

        var shown = SingleLine(current);
        DrawLine(shown, width);
        if (!following)
        {
            using (fonts.Popup.Push())
                ImGui.TextDisabled($"{capture.History.Count - 1 - IndexOf(shown)} back — scroll down for latest");
        }
    }

    /// <summary>Steps through the backlog with the mouse wheel, one line per notch.</summary>
    private GameLine SingleLine(GameLine current)
    {
        var history = capture.History;
        var index = following || browsing is null ? history.Count - 1 : IndexOf(browsing);
        if (index < 0)
        {
            Follow(); // the browsed line was trimmed from history
            index = history.Count - 1;
        }
        var wheel = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) ? ImGui.GetIO().MouseWheel : 0;
        if (wheel > 0)
            index = Math.Max(0, index - 1);
        else if (wheel < 0)
            index = Math.Min(history.Count - 1, index + 1);
        if (wheel != 0)
        {
            following = index >= history.Count - 1;
            browsing = following ? null : history[index];
        }

        return following || browsing is null ? current : browsing;
    }

    private int IndexOf(GameLine line)
    {
        var history = capture.History;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(history[i], line))
                return i;
        }

        return -1;
    }

    private void Follow()
    {
        following = true;
        browsing = null;
        scrolledTo = null;
    }

    private void DrawLine(GameLine line, float width)
    {
        if (line.Speaker.Length > 0)
        {
            using (fonts.Popup.Push())
                ImGui.TextColored(new Vector4(0.73f, 0.55f, 0.94f, 1f), line.Speaker);
        }

        var hit = RichText.Draw(line.Block, width, popups.HighlightFor(line.Block, 0));
        popups.Report(hit, 0, (block, index) => new ScanContext(line.Text, index, Title(line)));
        ImGuiHelpers.ScaledDummy(2);
    }

    private string Title(GameLine line) =>
        line.Speaker.Length > 0 ? $"{config.DocumentTitle} — {line.Speaker}" : config.DocumentTitle;
}
