using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using HachidoriFfxiv.Capture;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Ui;

/// <summary>
/// Freezes a snapshot of the FFXIV screen and every visible native Japanese text block
/// (including transient hover tooltips such as <c>ItemDetail</c>, <c>ActionDetail</c>, and <c>Tooltip</c>)
/// so the player can freely move the mouse onto tooltip text to scan, look up, and mine words
/// without the tooltip vanishing.
/// <para>
/// Registered as a Dalamud <see cref="Window"/> so <c>RespectCloseHotkey</c> and keyboard capture
/// keep <c>Esc</c> and other keys from leaking through to the game while frozen. Hotkey detection
/// runs on <c>Framework.Update</c> via physical key state so it works reliably during gameplay even
/// when no ImGui window has keyboard focus.
/// </para>
/// </summary>
public sealed class FreezeFrameOverlay : Window, IDisposable
{
    private const ImGuiWindowFlags BackdropFlags =
        ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoDocking
        | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoBringToFrontOnFocus
        | ImGuiWindowFlags.NoBackground;

    private const int VkMButton = 0x04;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkPause = 0x13;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const int VkF = 0x46;
    private const int VkF9 = 0x78;

    private readonly Configuration config;
    private readonly NativeTextHover nativeHover;
    private readonly HachidoriService service;
    private readonly PopupManager popups;
    private readonly Action<string> openSearch;

    private bool active;
    private bool capturing;
    private bool regionsCaptured;
    private bool needViewportCapture;
    private NativeTextHover.FrozenRegion[] frozenRegions = [];
    private Task<IDalamudTextureWrap>? frameTextureTask;
    private IDalamudTextureWrap? frameTexture;
    private string summaryLabel = string.Empty;
    private bool showRegionOutlines = true;
    private DateTime enteredAt;

    // Reliable hotkey + Esc + gamepad-B edge tracking (works even when ImGui has no focus).
    private bool primaryWasDown;
    private bool escWasDown;
    private bool gamepadBackWasDown;
    private bool escPressedSinceLastDraw;
    private bool lastImGuiWantTextInput;
    private DateTime suppressKeysUntilUtc;
    private VirtualKey suppressedVirtualKey = VirtualKey.NO_KEY;
    private int suppressedOsVk;

    public FreezeFrameOverlay(
        Configuration config,
        NativeTextHover nativeHover,
        HachidoriService service,
        PopupManager popups,
        Action<string> openSearch)
        : base("##hachidori-freeze-frame-backdrop", BackdropFlags, forceMainWindow: true)
    {
        this.config = config;
        this.nativeHover = nativeHover;
        this.service = service;
        this.popups = popups;
        this.openSearch = openSearch;

        RespectCloseHotkey = true;
        ForceMainWindow = true;
        InhibitAtkCollision = true;
        DisableWindowSounds = true;
        DisableFadeInFadeOut = true;
        AllowPinning = false;
        AllowClickthrough = false;
        ShowCloseButton = false;
        BgAlpha = 0f;

        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    public bool Active => active || capturing || IsOpen;

    public static string HotkeyLabel(FreezeHotkey key) => key switch
    {
        FreezeHotkey.CtrlShiftF => "Ctrl + Shift + F",
        FreezeHotkey.AltF => "Alt + F",
        FreezeHotkey.CtrlSpace => "Ctrl + Space",
        FreezeHotkey.Pause => "Pause / Break",
        FreezeHotkey.F9 => "F9",
        FreezeHotkey.ShiftMiddleClick => "Shift + Middle Click",
        FreezeHotkey.None => "Disabled (/hachidori freeze)",
        _ => key.ToString(),
    };

    public new void Toggle()
    {
        if (Active) Exit();
        else Enter();
    }

    public void Enter()
    {
        if (Active) return;
        BeginCapture(synchronousSnapshot: false);
    }

    public void Exit() => Exit(suppressKey: VirtualKey.NO_KEY, suppressOsVk: 0);

    private void Exit(VirtualKey suppressKey, int suppressOsVk)
    {
        if (!active && !capturing && !IsOpen) return;
        IsOpen = false;
        FinishExit(suppressKey, suppressOsVk);
    }

    public override void OnClose()
    {
        // Called by Dalamud's WindowSystem when Esc closes the window via RespectCloseHotkey.
        if (!active && !capturing) return;
        FinishExit(VirtualKey.ESCAPE, VkEscape);
    }

    private void FinishExit(VirtualKey suppressKey, int suppressOsVk)
    {
        capturing = false;
        active = false;
        regionsCaptured = false;
        needViewportCapture = false;
        frozenRegions = [];
        summaryLabel = string.Empty;
        escPressedSinceLastDraw = false;
        popups.CloseAll();
        ReleaseTexture();

        if (suppressOsVk != 0)
        {
            suppressedVirtualKey = suppressKey;
            suppressedOsVk = suppressOsVk;
            suppressKeysUntilUtc = DateTime.UtcNow.AddMilliseconds(300);
            ConsumeGameKey(suppressKey);
        }
    }

    private void BeginCapture(bool synchronousSnapshot)
    {
        popups.CloseAll();
        ReleaseTexture();
        capturing = true;
        active = false;
        regionsCaptured = false;
        needViewportCapture = true;
        frozenRegions = [];
        summaryLabel = string.Empty;
        escPressedSinceLastDraw = false;
        enteredAt = DateTime.UtcNow;

        if (synchronousSnapshot)
        {
            frozenRegions = nativeHover.CaptureFreezeSnapshotNow();
            regionsCaptured = true;
            BuildSummary();
            PrewarmTooltipSegmentation();
            DiagnosticLog.Write("freeze-frame", $"Captured {frozenRegions.Length} blocks synchronously via hotkey ({config.FreezeFrameHotkey}).");
        }
        else
        {
            nativeHover.RequestFreezeSnapshot(captured =>
            {
                if (!capturing) return;
                frozenRegions = captured;
                regionsCaptured = true;
                BuildSummary();
                PrewarmTooltipSegmentation();
                DiagnosticLog.Write("freeze-frame", $"Captured {frozenRegions.Length} blocks via command/UI.");
            });
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;

        // Post-exit key drain: keep clearing the exit key (Esc or toggle hotkey) from FFXIV's
        // KeyState until the user releases the physical key so it never leaks to the game menu.
        if (suppressedOsVk != 0)
        {
            if (now < suppressKeysUntilUtc && IsOsKeyDown(suppressedOsVk))
            {
                ConsumeGameKey(suppressedVirtualKey);
            }
            else
            {
                suppressedOsVk = 0;
                suppressedVirtualKey = VirtualKey.NO_KEY;
                suppressKeysUntilUtc = DateTime.MinValue;
            }
        }

        if (!IsGameForeground())
        {
            primaryWasDown = false;
            escWasDown = false;
            return;
        }

        // Track physical Esc edge while frozen as a backup if Dalamud's "Close windows with Esc" is off.
        var escDown = IsOsKeyDown(VkEscape);
        if (escDown && !escWasDown && (active || IsOpen))
            escPressedSinceLastDraw = true;
        escWasDown = escDown;

        // Allow Steam Deck / controller users to exit Freeze Frame with B / Circle.
        var padBackDown = false;
        try { padBackDown = DalamudServices.Gamepad.Raw(GamepadButtons.East) > 0; }
        catch { /* gamepad service unavailable */ }
        if (padBackDown && !gamepadBackWasDown && (active || IsOpen) && (now - enteredAt).TotalMilliseconds > 250)
            escPressedSinceLastDraw = true;
        gamepadBackWasDown = padBackDown;

        var hotkey = config.FreezeFrameHotkey;
        if (hotkey == FreezeHotkey.None)
        {
            primaryWasDown = false;
            return;
        }

        var primaryDown = IsPrimaryKeyDown(hotkey);
        var primaryPressed = primaryDown && !primaryWasDown;
        primaryWasDown = primaryDown;

        if (!primaryPressed || !ModifiersMatch(hotkey))
            return;

        if (!Active)
        {
            if (ShouldIgnoreHotkeyForTextInput(hotkey))
                return;

            var triggerVk = PrimaryVirtualKey(hotkey);
            ConsumeGameKey(triggerVk);
            BeginCapture(synchronousSnapshot: true);
        }
        else if ((now - enteredAt).TotalMilliseconds > 180)
        {
            var triggerVk = PrimaryVirtualKey(hotkey);
            var triggerOsVk = PrimaryOsVk(hotkey);
            ConsumeGameKey(triggerVk);
            Exit(triggerVk, triggerOsVk);
        }
    }

    /// <summary>Called once per frame in UiBuilder.Draw BEFORE WindowSystem.Draw.</summary>
    public void BeforeWindowsDraw()
    {
        lastImGuiWantTextInput = ImGui.GetIO().WantTextInput;

        // Start viewport snapshot on Draw (TakeBeforeImGuiRender captures the game frame cleanly).
        if (capturing && needViewportCapture)
        {
            needViewportCapture = false;
            try
            {
                frameTextureTask = DalamudServices.TextureProvider.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
                {
                    ViewportId = ImGui.GetMainViewport().ID,
                    TakeBeforeImGuiRender = true,
                    AutoUpdate = false,
                    KeepTransparency = false,
                }, "Hachidori freeze frame");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("freeze-frame", $"Viewport snapshot unavailable: {ex.Message}");
                frameTextureTask = null;
            }
        }

        // Poll one-shot viewport texture capture.
        if (frameTextureTask is not null && frameTextureTask.IsCompleted)
        {
            if (frameTextureTask.IsCompletedSuccessfully)
                frameTexture = frameTextureTask.Result;
            frameTextureTask = null;
        }

        // Wait for the 1-frame GPU texture capture before opening the ImGui window so
        // InhibitAtkCollision does not dismiss the tooltip before the frame is captured.
        if (capturing && regionsCaptured)
        {
            var textureDone = frameTextureTask is null || (DateTime.UtcNow - enteredAt).TotalMilliseconds > 220;
            if (textureDone)
            {
                capturing = false;
                active = true;
                IsOpen = true;
                RequestFocus = true;
                BringToFront();
            }
        }

        // Capture keyboard while frozen (and briefly while Esc/hotkey is still held after exit)
        // so Esc, WASD, hotbars, etc. never leak through Dalamud's WndProc to FFXIV.
        var draining = suppressedOsVk != 0 && DateTime.UtcNow < suppressKeysUntilUtc && IsOsKeyDown(suppressedOsVk);
        if (active || IsOpen || draining)
            ImGui.SetNextFrameWantCaptureKeyboard(true);
    }

    /// <summary>Called once per frame in UiBuilder.Draw AFTER WindowSystem.Draw and BEFORE PopupManager.Draw.</summary>
    public void AfterWindowsDraw()
    {
        if (!active && !IsOpen)
        {
            escPressedSinceLastDraw = false;
            return;
        }

        // Fallback Esc handling in case Dalamud's "Close windows with Esc" is disabled
        // or focus briefly shifted to a WebView2 hitbox window.
        var canExit = (DateTime.UtcNow - enteredAt).TotalMilliseconds > 180;
        var escTriggered = escPressedSinceLastDraw || ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        escPressedSinceLastDraw = false;

        if (canExit && escTriggered)
            Exit(VirtualKey.ESCAPE, VkEscape);
    }

    public override bool DrawConditions() => active;

    public override void PreDraw()
    {
        var vp = ImGui.GetMainViewport();
        Position = vp.Pos;
        PositionCondition = ImGuiCond.Always;
        Size = vp.Size;
        SizeCondition = ImGuiCond.Always;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(2);
    }

    public override void Draw()
    {
        // Keep focus on the Freeze Frame window (when no widget/scrollbar drag is active) so
        // Dalamud's WindowSystem always prioritizes it for RespectCloseHotkey (Esc).
        if (!IsFocused && !ImGui.IsAnyItemActive() && !ImGui.GetIO().WantTextInput)
            RequestFocus = true;

        var vp = ImGui.GetMainViewport();
        var mouse = ImGui.GetMousePos();
        var scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var accent = Rendering.Css.CssColor.ToAbgr(Rendering.Css.PopupTheme.Current.Accent with { W = 1f });

        if (frameTexture is not null)
        {
            draw.AddImage(frameTexture.Handle, vp.Pos, vp.Pos + vp.Size);
        }
        else
        {
            // Fallback when GPU viewport capture is unavailable: dim backdrop + readable cards for captured text.
            draw.AddRectFilled(vp.Pos, vp.Pos + vp.Size, 0xB8101218);
            DrawFallbackCards(draw, vp.Pos, accent);
        }

        // Subtle viewport frame so Freeze Frame mode is unmistakable.
        draw.AddRect(vp.Pos + Vector2.One, vp.Pos + vp.Size - Vector2.One, (accent & 0x00FFFFFF) | 0xA0000000, 0f, ImDrawFlags.None, 2.5f);

        // Top floating control pill.
        var topBarHovered = DrawControlPill(vp, scale);
        if (!active)
            return;

        // Exit on right-click outside any open dictionary popup and outside the control pill.
        var canExit = (DateTime.UtcNow - enteredAt).TotalMilliseconds > 180;
        if (canExit && !topBarHovered && !popups.ContainsMouse(mouse) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            Exit();
            return;
        }

        // Hover hit-testing and word highlighting over frozen regions (tooltips prioritized first).
        var blocked = topBarHovered || popups.ContainsMouse(mouse);
        NativeTextHover.FrozenRegion? hoveredRegion = null;
        NativeTextLayout.Cell? hoveredCell = null;

        foreach (var region in frozenRegions)
        {
            var origin = region.Min + vp.Pos;
            var clipMin = region.ClipMin + vp.Pos;
            var clipMax = region.ClipMax + vp.Pos;

            if (showRegionOutlines && region.IsTooltip)
            {
                var outlineCol = (accent & 0x00FFFFFF) | 0x48000000;
                draw.AddRect(clipMin - new Vector2(2f, 2f), clipMax + new Vector2(2f, 2f), outlineCol, 3f, ImDrawFlags.None, 1f);
            }

            if (popups.HighlightFor(region.Block, 0) is { } highlight)
            {
                var theme = Rendering.Css.PopupTheme.Current;
                var highlightColor = theme.IsPlugin
                    ? ImGui.GetColorU32(ImGuiCol.TextSelectedBg)
                    : Rendering.Css.CssColor.ToAbgr(theme.Highlight);
                foreach (var glyph in region.Layout.Cells)
                {
                    if (glyph.Index < highlight.Start || glyph.Index >= highlight.End) continue;
                    var glyphMin = Vector2.Max(origin + glyph.Min * region.Scale, clipMin);
                    var glyphMax = Vector2.Min(origin + glyph.Max * region.Scale, clipMax);
                    if (glyphMax.X > glyphMin.X && glyphMax.Y > glyphMin.Y)
                        draw.AddRectFilled(glyphMin, glyphMax, highlightColor);
                }
            }

            if (hoveredRegion is null && region.Layout.HitScreen(mouse, origin, region.Scale, clipMin, clipMax) is { } cell)
            {
                hoveredRegion = region;
                hoveredCell = cell;
            }
        }

        if (hoveredRegion is not null && hoveredCell is { } hitCell && !blocked)
        {
            var addon = hoveredRegion.Addon;
            popups.Report(
                new TextHit(hoveredRegion.Block, hitCell.Index, hitCell.Min, hitCell.Max),
                0,
                (block, index) =>
                {
                    var sentence = NameKana.Apply(block.ScanText, PlayerName.Replacements(config), ref index);
                    return new ScanContext(sentence, index, $"{config.DocumentTitle} — {addon}");
                });
        }
    }

    private bool DrawControlPill(ImGuiViewportPtr vp, float scale)
    {
        var pad = new Vector2(12f * scale, 6f * scale);
        var pillY = vp.Pos.Y + 14f * scale;
        var label = $"FREEZE FRAME  ·  {summaryLabel}  ·  Hover any text to look up";
        var textSize = ImGui.CalcTextSize(label);
        var btnH = ImGui.GetFrameHeight();
        var outlineW = ImGui.CalcTextSize(showRegionOutlines ? "Hide Boxes" : "Show Boxes").X + 16f * scale;
        var searchW = ImGui.CalcTextSize("Send to Search").X + 16f * scale;
        var exitW = ImGui.CalcTextSize("Exit (Esc)").X + 16f * scale;
        var gap = 8f * scale;
        var totalW = pad.X * 2 + textSize.X + gap * 3 + outlineW + searchW + exitW;
        var totalH = MathF.Max(textSize.Y, btnH) + pad.Y * 2;
        var pillPos = new Vector2(vp.Pos.X + (vp.Size.X - totalW) * 0.5f, pillY);

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pillPos, pillPos + new Vector2(totalW, totalH), 0xE6141720, 8f * scale);
        draw.AddRect(pillPos, pillPos + new Vector2(totalW, totalH), 0xAA68B8FF, 8f * scale, ImDrawFlags.None, 1.2f);

        ImGui.SetCursorScreenPos(pillPos + new Vector2(pad.X, (totalH - textSize.Y) * 0.5f));
        ImGui.TextColored(new Vector4(0.86f, 0.94f, 1f, 1f), label);

        var btnX = pillPos.X + pad.X + textSize.X + gap;
        var btnY = pillPos.Y + (totalH - btnH) * 0.5f;

        ImGui.SetCursorScreenPos(new Vector2(btnX, btnY));
        if (ImGui.Button(showRegionOutlines ? "Hide Boxes##ff-box" : "Show Boxes##ff-box", new Vector2(outlineW, btnH)))
            showRegionOutlines = !showRegionOutlines;

        btnX += outlineW + gap;
        ImGui.SetCursorScreenPos(new Vector2(btnX, btnY));
        if (ImGui.Button("Send to Search##ff-search", new Vector2(searchW, btnH)))
        {
            var dump = CollectPreferredText();
            Exit();
            if (!string.IsNullOrWhiteSpace(dump))
                openSearch(dump);
            return true;
        }

        btnX += searchW + gap;
        ImGui.SetCursorScreenPos(new Vector2(btnX, btnY));
        if (ImGui.Button("Exit (Esc)##ff-exit", new Vector2(exitW, btnH)))
        {
            Exit();
            return true;
        }

        var mouse = ImGui.GetMousePos();
        return mouse.X >= pillPos.X && mouse.X <= pillPos.X + totalW
               && mouse.Y >= pillPos.Y && mouse.Y <= pillPos.Y + totalH;
    }

    private void DrawFallbackCards(ImDrawListPtr draw, Vector2 vpPos, uint accent)
    {
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        foreach (var r in frozenRegions)
        {
            var min = r.ClipMin + vpPos;
            var max = r.ClipMax + vpPos;
            draw.AddRectFilled(min - new Vector2(4, 3), max + new Vector2(4, 3), r.IsTooltip ? 0xE8181D2B : 0xB814161E, 4f);
            draw.AddRect(min - new Vector2(4, 3), max + new Vector2(4, 3), r.IsTooltip ? accent : 0x558899AA, 4f);
            draw.AddText(font, fontSize, min, 0xFFF2F5FA, r.Block.ScanText);
        }
    }

    private void BuildSummary()
    {
        var tips = frozenRegions.Where(r => r.IsTooltip).Select(r => r.Addon).Distinct(StringComparer.Ordinal).ToArray();
        if (tips.Length > 0)
            summaryLabel = $"{string.Join(", ", tips)} ({frozenRegions.Length} blocks)";
        else if (frozenRegions.Length > 0)
            summaryLabel = $"{frozenRegions.Length} Japanese text blocks";
        else
            summaryLabel = "No Japanese text detected";
    }

    private void PrewarmTooltipSegmentation()
    {
        if (!config.TokenizedHover) return;
        foreach (var r in frozenRegions.Where(r => r.IsTooltip).Take(8))
            _ = service.SegmentAsync(r.Block.ScanText);
    }

    private string CollectPreferredText()
    {
        var source = frozenRegions.Any(r => r.IsTooltip)
            ? frozenRegions.Where(r => r.IsTooltip)
            : frozenRegions;
        return string.Join("\n", source.Select(r => r.Block.ScanText.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal));
    }

    private bool ShouldIgnoreHotkeyForTextInput(FreezeHotkey hotkey)
    {
        if (hotkey is FreezeHotkey.Pause or FreezeHotkey.ShiftMiddleClick)
            return false;
        if (lastImGuiWantTextInput)
            return true;
        if (hotkey == FreezeHotkey.F9)
            return false;
        return IsGameTextInputActive();
    }

    private static unsafe bool IsGameTextInputActive()
    {
        try
        {
            var rapture = RaptureAtkModule.Instance();
            return rapture != null && rapture->IsTextInputActive();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGameForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == 0)
            return PlatformInfo.Current.IsWine; // Gamescope/Wayland under Wine can report a null foreground HWND while active.
        _ = GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static bool IsOsKeyDown(int vk)
    {
        if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            return true;
        if (!PlatformInfo.Current.IsWine)
            return false;
        return vk switch
        {
            VkControl => IsGameKeyDown(VirtualKey.CONTROL) || IsGameKeyDown(VirtualKey.LCONTROL) || IsGameKeyDown(VirtualKey.RCONTROL),
            VkShift => IsGameKeyDown(VirtualKey.SHIFT) || IsGameKeyDown(VirtualKey.LSHIFT) || IsGameKeyDown(VirtualKey.RSHIFT),
            VkMenu => IsGameKeyDown(VirtualKey.MENU) || IsGameKeyDown(VirtualKey.LMENU) || IsGameKeyDown(VirtualKey.RMENU),
            VkEscape => IsGameKeyDown(VirtualKey.ESCAPE),
            VkF => IsGameKeyDown(VirtualKey.F),
            VkSpace => IsGameKeyDown(VirtualKey.SPACE),
            VkPause => IsGameKeyDown(VirtualKey.PAUSE),
            VkF9 => IsGameKeyDown(VirtualKey.F9),
            VkMButton => IsGameKeyDown(VirtualKey.MBUTTON),
            _ => false,
        };
    }

    private static bool IsGameKeyDown(VirtualKey vk)
    {
        try { return DalamudServices.KeyState.IsVirtualKeyValid(vk) && DalamudServices.KeyState[vk]; }
        catch { return false; }
    }

    private static int PrimaryOsVk(FreezeHotkey key) => key switch
    {
        FreezeHotkey.CtrlShiftF or FreezeHotkey.AltF => VkF,
        FreezeHotkey.CtrlSpace => VkSpace,
        FreezeHotkey.Pause => VkPause,
        FreezeHotkey.F9 => VkF9,
        FreezeHotkey.ShiftMiddleClick => VkMButton,
        _ => 0,
    };

    private static VirtualKey PrimaryVirtualKey(FreezeHotkey key) => key switch
    {
        FreezeHotkey.CtrlShiftF or FreezeHotkey.AltF => VirtualKey.F,
        FreezeHotkey.CtrlSpace => VirtualKey.SPACE,
        FreezeHotkey.Pause => VirtualKey.PAUSE,
        FreezeHotkey.F9 => VirtualKey.F9,
        FreezeHotkey.ShiftMiddleClick => VirtualKey.MBUTTON,
        _ => VirtualKey.NO_KEY,
    };

    private static bool IsPrimaryKeyDown(FreezeHotkey key)
    {
        var vk = PrimaryOsVk(key);
        return vk != 0 && IsOsKeyDown(vk);
    }

    private static bool ModifiersMatch(FreezeHotkey key)
    {
        var ctrl = IsOsKeyDown(VkControl);
        var shift = IsOsKeyDown(VkShift);
        var alt = IsOsKeyDown(VkMenu);
        return key switch
        {
            FreezeHotkey.CtrlShiftF => ctrl && shift && !alt,
            FreezeHotkey.AltF => alt && !ctrl && !shift,
            FreezeHotkey.CtrlSpace => ctrl && !shift && !alt,
            FreezeHotkey.Pause => true,
            FreezeHotkey.F9 => !ctrl && !shift && !alt,
            FreezeHotkey.ShiftMiddleClick => shift && !ctrl && !alt,
            _ => false,
        };
    }

    private static void ConsumeGameKey(VirtualKey vk)
    {
        if (vk == VirtualKey.NO_KEY) return;
        try
        {
            if (DalamudServices.KeyState.IsVirtualKeyValid(vk))
            {
                DalamudServices.KeyState[vk] = false;
                DalamudServices.KeyState.SetRawValue(vk, 0);
            }
        }
        catch
        {
            // Some keys (e.g. Pause/mouse buttons) are outside FFXIV's SeVirtualKey map.
        }
    }

    private void ReleaseTexture()
    {
        frameTexture?.Dispose();
        frameTexture = null;
        if (frameTextureTask is { IsCompletedSuccessfully: true })
            frameTextureTask.Result.Dispose();
        else if (frameTextureTask is not null)
            _ = frameTextureTask.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
        frameTextureTask = null;
    }

    public void Dispose()
    {
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
        ReleaseTexture();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}
