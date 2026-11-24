using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Media;
using HachidoriFfxiv.Rendering;
using HachidoriFfxiv.Stats;
using Microsoft.Web.WebView2.Core;

namespace HachidoriFfxiv.WebView;

/// <summary>
/// Coordinates the Dalamud-side lifecycle of Hachidori Web inside FFXIV:
/// owns <see cref="HachidoriBundleManager"/> (Phase 3 automatic version-matched bundle sync),
/// <see cref="HachidoriWebBridge"/> (live + offline RPC and FFXIV media hooks), and
/// <see cref="WebViewPopupHost"/> (permanently click-through DirectComposition visual overlay),
/// plus invisible ImGui hitboxes that capture mouse interaction over visible popups without
/// ever taking OS focus away from FFXIV.
/// </summary>
public sealed class WebViewPopupController : IDisposable
{
    private const uint IdcIBeam = 32513;
    private const uint IdcSizeNwSe = 32642;
    private const uint IdcSizeNeSw = 32643;
    private const uint IdcSizeWe = 32644;
    private const uint IdcSizeNs = 32645;
    private const uint IdcSizeAll = 32646;
    private const uint IdcNo = 32648;
    private const uint IdcHand = 32649;

    private readonly Configuration config;
    private readonly HachidoriService service;
    private readonly Func<MediaMiner?> getMedia;
    private readonly string userDataRoot;
    private readonly HachidoriWebAdapter adapter;
    private readonly HachidoriWebBridge bridge;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object hostLock = new();

    private WebViewPopupHost? host;
    private string? activeSessionDataFolder;
    private volatile string? controllerError;
    private bool controllerMode;
    private bool startAttempted;
    private string? lastSyncedHostVersion;
    private string? lastConfigFingerprint;
    private bool snapshotDirty = true;
    private DateTime lastSyncKick = DateTime.MinValue;
    private float lastScaleX = 1f;
    private float lastScaleY = 1f;
    private bool pointerInsideWebView;
    private int lastMouseX = int.MinValue;
    private int lastMouseY = int.MinValue;
    private CoreWebView2MouseEventVirtualKeys lastMouseKeys;
    private bool leftCaptured;
    private bool rightCaptured;
    private bool middleCaptured;
    private bool disposed;

    public WebViewPopupController(
        Configuration config,
        HachidoriService service,
        string configDirectory,
        Func<MediaMiner?> getMedia,
        Func<StatsTracker?> getStats,
        Func<bool> npcAvailable,
        Action<string, string> onNpcCapture,
        Action<string> onOpenSettings)
    {
        this.config = config;
        this.service = service;
        this.getMedia = getMedia;
        userDataRoot = Path.Combine(configDirectory, "webview2-data");
        Bundles = new HachidoriBundleManager(Path.Combine(configDirectory, "hachidori-web"));
        _ = Task.Run(() => PruneStaleSessionFolders(userDataRoot));
        adapter = new HachidoriWebAdapter(config, service, getMedia, getStats, npcAvailable);
        bridge = new HachidoriWebBridge(() => HachidoriWebAdapter.OptionsFrom(config), adapter)
        {
            OnNpcCaptureRequested = onNpcCapture,
            OnOpenExternalUrl = OpenExternalUrlSafely,
            OnOpenSettings = onOpenSettings,
        };

        service.Link.ConnectionChanged += OnConnectionChanged;
        service.Link.SnapshotChanged += OnSnapshotChanged;
        service.Link.WordStatusChanged += OnWordStatusChanged;
        Bundles.BundleChanged += OnBundleChanged;

        if (config.UseWebViewPopup && PlatformSupported)
            StartInitialSync();
        else if (config.UseWebViewPopup && !PlatformSupported)
            DiagnosticLog.Write("webview", $"Skipping WebView2 on {PlatformInfo.Current.DisplayName}; using built-in ImGui popup renderer.");
    }

    public HachidoriBundleManager Bundles { get; }

    /// <summary>True on native Windows with DirectComposition; false on Wine/Proton/Steam Deck/macOS.</summary>
    public bool PlatformSupported => PlatformInfo.Current.SupportsWebView2Overlay;

    /// <summary>True when WebView2 is enabled, booted, and ready to render Hachidori's real frontend.</summary>
    public bool Active => !disposed && PlatformSupported && config.UseWebViewPopup && config.FollowHachidoriTheme && host is { IsReady: true };

    public bool RuntimeAvailable => PlatformSupported && WebViewPopupHost.IsRuntimeAvailable(out _);

    public string? LastError => !PlatformSupported ? null : host?.LastError ?? controllerError ?? Bundles.LastError;

    public string StatusText => !PlatformSupported
        ? $"Hachidori Web (WebView2) is Windows-only — using built-in ImGui renderer on {PlatformInfo.Current.DisplayName}."
        : host?.StatusText ?? (Bundles.Ready ? "Starting WebView2 host…" : "Waiting for Hachidori Web bundle…");

    public bool AnyOpen => Active && host!.AnyOpen;

    public bool IsPointerCaptured => leftCaptured || rightCaptured || middleCaptured;

    public int VisibleCount => Active ? host!.VisibleCount : 0;

    public int MatchedLength => Active ? host!.MatchedLength : 0;

    public string? LoadedVersion => host?.LoadedVersion ?? Bundles.ActiveVersion;
    public bool SmokeTestPassed => host?.SmokeTestPassed ?? false;
    public bool UsedFallbackSelector => host?.UsedFallbackSelector ?? false;

    /// <summary>Marks plugin settings dirty so the projected snapshot pushes to WebView2 on the next frame.</summary>
    public void NotifySettingsChanged()
    {
        snapshotDirty = true;
        if (PlatformSupported && config.UseWebViewPopup && host is null && !startAttempted)
            StartInitialSync();
    }

    /// <summary>Triggers a manual sync of Hachidori's frontend bundle (local browser install or GitHub Releases).</summary>
    public Task<bool> SyncBundleNowAsync(bool force = true)
    {
        if (!PlatformSupported)
            return Task.FromResult(false);
        var target = service.Link.Connected ? service.Link.HostVersion : null;
        return Task.Run(async () =>
        {
            var ok = await Bundles.EnsureBundleAsync(target, config.WebViewBundlePath, allowNetwork: true, force: force, ct: lifetime.Token).ConfigureAwait(false);
            if (ok) EnsureHostStarted(restartIfFailed: true);
            return ok;
        });
    }

    /// <summary>True if the ImGui screen-space mouse point lies inside any visible WebView2 popup.</summary>
    public bool ContainsScreenPoint(Vector2 screenPos)
    {
        if (!Active) return false;
        var vp = ImGui.GetMainViewport();
        return host!.ContainsViewportPoint(
            (screenPos.X - vp.Pos.X) * lastScaleX,
            (screenPos.Y - vp.Pos.Y) * lastScaleY);
    }

    public void ShowLookup(string sentence, int charIndex, Vector2 anchorMinScreen, Vector2 anchorMaxScreen, string documentTitle, int tokenLength = 1)
    {
        if (!Active) return;
        var vp = ImGui.GetMainViewport();
        var min = new Vector2((anchorMinScreen.X - vp.Pos.X) * lastScaleX, (anchorMinScreen.Y - vp.Pos.Y) * lastScaleY);
        var max = new Vector2((anchorMaxScreen.X - vp.Pos.X) * lastScaleX, (anchorMaxScreen.Y - vp.Pos.Y) * lastScaleY);
        host!.ShowLookup(sentence, charIndex, min, max, documentTitle, tokenLength);
    }

    public void CloseAll() => host?.CloseAll();
    public void CloseTop() => host?.CloseTop();
    public void CloseFrom(int depth) => host?.CloseFrom(depth);
    public void SendCommand(string action) => host?.SendCommand(action);
    public void ScrollPopup(int depth, float deltaY) => host?.ScrollPopup(depth, deltaY);

    public void SetControllerMode(bool active)
    {
        controllerMode = active;
        host?.SetControllerMode(active);
    }

    public (Vector2 Min, Vector2 Max)? PopupRectScreen(int depth)
    {
        if (!Active || host!.GetPopupLayerRect(depth) is not { } r)
            return null;
        var vp = ImGui.GetMainViewport();
        var sx = MathF.Max(0.01f, lastScaleX);
        var sy = MathF.Max(0.01f, lastScaleY);
        return (
            new Vector2(vp.Pos.X + r.Left / sx, vp.Pos.Y + r.Top / sy),
            new Vector2(vp.Pos.X + r.Right / sx, vp.Pos.Y + r.Bottom / sy));
    }

    public IReadOnlyList<(RichBlock Block, IReadOnlyList<(int Index, Vector2 Min, Vector2 Max)> Cells)> GetScanTargets(int depth)
    {
        if (!Active)
            return [];
        var raw = host!.GetScanBlocks(depth);
        if (raw.Count == 0)
            return [];
        var vp = ImGui.GetMainViewport();
        var sx = MathF.Max(0.01f, lastScaleX);
        var sy = MathF.Max(0.01f, lastScaleY);
        var result = new List<(RichBlock, IReadOnlyList<(int, Vector2, Vector2)>)>(raw.Count);
        foreach (var item in raw)
        {
            var cells = new List<(int, Vector2, Vector2)>(item.Cells.Count);
            foreach (var (index, l, t, r, b) in item.Cells)
            {
                cells.Add((
                    index,
                    new Vector2(vp.Pos.X + l / sx, vp.Pos.Y + t / sy),
                    new Vector2(vp.Pos.X + r / sx, vp.Pos.Y + b / sy)));
            }
            if (cells.Count > 0)
                result.Add((item.Block, cells));
        }
        return result;
    }

    public void ScanPopupAt(int parentDepth, Vector2 screenPoint)
    {
        if (!Active) return;
        var vp = ImGui.GetMainViewport();
        host!.ScanPopupAt(
            parentDepth,
            (screenPoint.X - vp.Pos.X) * lastScaleX,
            (screenPoint.Y - vp.Pos.Y) * lastScaleY);
    }

    public void SetControllerCursor(IReadOnlyList<(Vector2 Min, Vector2 Max)>? screenRects, string? colorCss = null)
    {
        if (!Active) return;
        if (screenRects is null || screenRects.Count == 0)
        {
            host!.SetControllerCursor(null, colorCss);
            return;
        }
        var vp = ImGui.GetMainViewport();
        var mapped = new List<(float, float, float, float)>(screenRects.Count);
        foreach (var (min, max) in screenRects)
        {
            mapped.Add((
                (min.X - vp.Pos.X) * lastScaleX,
                (min.Y - vp.Pos.Y) * lastScaleY,
                (max.X - vp.Pos.X) * lastScaleX,
                (max.Y - vp.Pos.Y) * lastScaleY));
        }
        host!.SetControllerCursor(mapped, colorCss);
    }

    /// <summary>Called once per frame on Dalamud's UiBuilder.Draw thread (non-blocking, allocation-free when idle).</summary>
    public void Draw(Vector2 mouseScreenPos)
    {
        if (disposed) return;

        if (!PlatformSupported || !config.UseWebViewPopup || !config.FollowHachidoriTheme)
        {
            ReleasePointerState(0, 0);
            if (host is { AnyOpen: true }) host.CloseAll();
            host?.SyncOverlayFrame(0, 0, 1, 1, gameVisible: false);
            return;
        }

        if (host is null && Bundles.Ready && !startAttempted)
            EnsureHostStarted();

        if (host is null || !host.IsReady)
            return;

        // Detect any configuration change (including Theme tab edits) without requiring explicit callbacks everywhere.
        var fingerprint = ConfigFingerprint();
        if (snapshotDirty || !string.Equals(fingerprint, lastConfigFingerprint, StringComparison.Ordinal))
        {
            snapshotDirty = false;
            lastConfigFingerprint = fingerprint;
            host.PushStorageUpdate(bridge.BuildProjectedSnapshot(), config.AudioVolume, adapter.NpcPicturesAvailable);
        }

        var gameHwnd = FindGameWindowHandle();
        var vp = ImGui.GetMainViewport();
        int clientX = (int)MathF.Round(vp.Pos.X);
        int clientY = (int)MathF.Round(vp.Pos.Y);
        int clientW = Math.Max(1, (int)MathF.Round(vp.Size.X));
        int clientH = Math.Max(1, (int)MathF.Round(vp.Size.Y));
        var gameVisible = true;

        if (gameHwnd != 0)
        {
            gameVisible = IsWindowVisible(gameHwnd) && !IsIconic(gameHwnd);
            if (gameVisible && GetClientRect(gameHwnd, out var rect))
            {
                var pt = new Point { X = 0, Y = 0 };
                if (ClientToScreen(gameHwnd, ref pt) && rect.Right > rect.Left && rect.Bottom > rect.Top)
                {
                    clientX = pt.X;
                    clientY = pt.Y;
                    clientW = rect.Right - rect.Left;
                    clientH = rect.Bottom - rect.Top;
                }
            }
        }

        lastScaleX = clientW / MathF.Max(1f, vp.Size.X);
        lastScaleY = clientH / MathF.Max(1f, vp.Size.Y);

        host.SyncOverlayFrame(clientX, clientY, clientW, clientH, gameVisible, gameHwnd);

        // Draw invisible ImGui hitboxes over visible popup rects and forward captured mouse input to WebView2.
        DrawHitboxesAndForwardInput(vp, mouseScreenPos, clientW, clientH, gameVisible);
    }

    private void DrawHitboxesAndForwardInput(ImGuiViewportPtr vp, Vector2 mouseScreenPos, int clientW, int clientH, bool gameVisible)
    {
        if (host is null || !gameVisible || host.VisibleCount <= 0)
        {
            ReleasePointerState(0, 0);
            return;
        }

        var rects = host.GetPopupRects();
        if (rects.Length == 0)
        {
            ReleasePointerState(0, 0);
            return;
        }

        const ImGuiWindowFlags HitboxFlags =
            ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoDocking
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoBringToFrontOnFocus;

        const ImGuiButtonFlags AllMouseButtons =
            ImGuiButtonFlags.MouseButtonLeft
            | ImGuiButtonFlags.MouseButtonRight
            | ImGuiButtonFlags.MouseButtonMiddle;

        var anyHovered = false;
        var anyActive = false;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        try
        {
            for (var i = 0; i < rects.Length; i++)
            {
                var r = rects[i];
                var minX = vp.Pos.X + r.Left / lastScaleX;
                var minY = vp.Pos.Y + r.Top / lastScaleY;
                var width = MathF.Max(1f, (r.Right - r.Left) / lastScaleX);
                var height = MathF.Max(1f, (r.Bottom - r.Top) / lastScaleY);

                ImGui.SetNextWindowPos(new Vector2(minX, minY), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Always);
                if (ImGui.Begin($"##hachidori-webview-hit-{i}", HitboxFlags))
                {
                    // Keep popup hitboxes above Reader/Search windows without stealing keyboard focus.
                    unsafe { ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow()); }
                    ImGui.InvisibleButton($"##wv-btn-{i}", new Vector2(width, height), AllMouseButtons);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
                        anyHovered = true;
                    if (ImGui.IsItemActive())
                        anyActive = true;
                }
                ImGui.End();
            }
        }
        finally
        {
            ImGui.PopStyleVar(3);
        }

        var px = Math.Clamp((int)MathF.Round((mouseScreenPos.X - vp.Pos.X) * lastScaleX), 0, Math.Max(0, clientW - 1));
        var py = Math.Clamp((int)MathF.Round((mouseScreenPos.Y - vp.Pos.Y) * lastScaleY), 0, Math.Max(0, clientH - 1));

        var io = ImGui.GetIO();
        var keys = CoreWebView2MouseEventVirtualKeys.None;
        if (io.KeyShift) keys |= CoreWebView2MouseEventVirtualKeys.Shift;
        if (io.KeyCtrl) keys |= CoreWebView2MouseEventVirtualKeys.Control;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) keys |= CoreWebView2MouseEventVirtualKeys.LeftButton;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Right)) keys |= CoreWebView2MouseEventVirtualKeys.RightButton;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Middle)) keys |= CoreWebView2MouseEventVirtualKeys.MiddleButton;

        var engaged = anyHovered || anyActive || IsPointerCaptured;
        if (engaged)
        {
            if (anyHovered)
                ApplyWebViewCursor(host.SystemCursorId);

            if (!pointerInsideWebView || px != lastMouseX || py != lastMouseY || keys != lastMouseKeys)
            {
                host.SendMouseInput(CoreWebView2MouseEventKind.Move, keys, 0, px, py);
                pointerInsideWebView = true;
                lastMouseX = px;
                lastMouseY = py;
                lastMouseKeys = keys;
            }
        }
        else if (pointerInsideWebView)
        {
            host.SendMouseInput(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, 0, 0);
            pointerInsideWebView = false;
            lastMouseX = int.MinValue;
            lastMouseY = int.MinValue;
        }

        ForwardMouseButton(
            anyHovered,
            ImGuiMouseButton.Left,
            CoreWebView2MouseEventVirtualKeys.LeftButton,
            CoreWebView2MouseEventKind.LeftButtonDown,
            CoreWebView2MouseEventKind.LeftButtonDoubleClick,
            CoreWebView2MouseEventKind.LeftButtonUp,
            ref leftCaptured,
            keys,
            px,
            py);

        ForwardMouseButton(
            anyHovered,
            ImGuiMouseButton.Right,
            CoreWebView2MouseEventVirtualKeys.RightButton,
            CoreWebView2MouseEventKind.RightButtonDown,
            CoreWebView2MouseEventKind.RightButtonDoubleClick,
            CoreWebView2MouseEventKind.RightButtonUp,
            ref rightCaptured,
            keys,
            px,
            py);

        ForwardMouseButton(
            anyHovered,
            ImGuiMouseButton.Middle,
            CoreWebView2MouseEventVirtualKeys.MiddleButton,
            CoreWebView2MouseEventKind.MiddleButtonDown,
            CoreWebView2MouseEventKind.MiddleButtonDoubleClick,
            CoreWebView2MouseEventKind.MiddleButtonUp,
            ref middleCaptured,
            keys,
            px,
            py);

        if (anyHovered)
        {
            if (io.MouseWheel != 0f)
            {
                var wheelDelta = unchecked((uint)(int)MathF.Round(io.MouseWheel * 120f));
                host.SendMouseInput(CoreWebView2MouseEventKind.Wheel, keys, wheelDelta, px, py);
            }
            if (io.MouseWheelH != 0f)
            {
                var wheelDeltaH = unchecked((uint)(int)MathF.Round(io.MouseWheelH * 120f));
                host.SendMouseInput(CoreWebView2MouseEventKind.HorizontalWheel, keys, wheelDeltaH, px, py);
            }
        }
    }

    private void ForwardMouseButton(
        bool hovered,
        ImGuiMouseButton button,
        CoreWebView2MouseEventVirtualKeys buttonKey,
        CoreWebView2MouseEventKind downKind,
        CoreWebView2MouseEventKind dblClickKind,
        CoreWebView2MouseEventKind upKind,
        ref bool captured,
        CoreWebView2MouseEventVirtualKeys keys,
        int px,
        int py)
    {
        if (host is null) return;

        if (hovered && ImGui.IsMouseDoubleClicked(button))
        {
            captured = true;
            host.SendMouseInput(dblClickKind, keys | buttonKey, 0, px, py);
        }
        else if (hovered && ImGui.IsMouseClicked(button))
        {
            captured = true;
            host.SendMouseInput(downKind, keys | buttonKey, 0, px, py);
        }

        if (captured && !ImGui.IsMouseDown(button))
        {
            captured = false;
            host.SendMouseInput(upKind, keys & ~buttonKey, 0, px, py);
        }
    }

    private void ReleasePointerState(int px, int py)
    {
        if (host is not null && host.IsReady)
        {
            if (leftCaptured)
                host.SendMouseInput(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, px, py);
            if (rightCaptured)
                host.SendMouseInput(CoreWebView2MouseEventKind.RightButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, px, py);
            if (middleCaptured)
                host.SendMouseInput(CoreWebView2MouseEventKind.MiddleButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, px, py);
            if (pointerInsideWebView)
                host.SendMouseInput(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, 0, 0);
        }
        leftCaptured = false;
        rightCaptured = false;
        middleCaptured = false;
        pointerInsideWebView = false;
        lastMouseX = int.MinValue;
        lastMouseY = int.MinValue;
    }

    private static void ApplyWebViewCursor(uint systemCursorId)
    {
        var cursor = systemCursorId switch
        {
            IdcHand => ImGuiMouseCursor.Hand,
            IdcIBeam => ImGuiMouseCursor.TextInput,
            IdcSizeWe => ImGuiMouseCursor.ResizeEw,
            IdcSizeNs => ImGuiMouseCursor.ResizeNs,
            IdcSizeNwSe => ImGuiMouseCursor.ResizeNwse,
            IdcSizeNeSw => ImGuiMouseCursor.ResizeNesw,
            IdcSizeAll => ImGuiMouseCursor.ResizeAll,
            IdcNo => ImGuiMouseCursor.NotAllowed,
            _ => ImGuiMouseCursor.None,
        };
        if (cursor != ImGuiMouseCursor.None)
            ImGui.SetMouseCursor(cursor);
    }

    private void StartInitialSync()
    {
        if (Bundles.Ready)
            EnsureHostStarted();

        if (!config.AutoSyncWebViewBundle && Bundles.Ready)
            return;

        _ = Task.Run(async () =>
        {
            var target = service.Link.Connected ? service.Link.HostVersion : null;
            var ok = await Bundles.EnsureBundleAsync(target, config.WebViewBundlePath, allowNetwork: true, force: false, ct: lifetime.Token).ConfigureAwait(false);
            if (ok && !disposed)
                EnsureHostStarted();
        });
    }

    private void EnsureHostStarted(bool restartIfFailed = false)
    {
        if (!PlatformSupported) return;
        lock (hostLock)
        {
            if (disposed || !Bundles.Ready) return;
            if (host is not null)
            {
                if (!restartIfFailed || !host.IsFailed) return;
                var failedHost = host;
                var failedFolder = activeSessionDataFolder;
                host = null;
                activeSessionDataFolder = null;
                _ = Task.Run(() =>
                {
                    try { failedHost.Dispose(); } catch { }
                    TryDeleteDirectory(failedFolder);
                });
            }
            startAttempted = true;
            controllerError = null;
            if (!WebViewPopupHost.IsRuntimeAvailable(out _))
            {
                controllerError = "Microsoft Edge WebView2 Runtime is not installed.";
                DiagnosticLog.Write("webview", "Microsoft Edge WebView2 Runtime not detected; falling back to ImGui popup renderer.");
                return;
            }
            try
            {
                // Use an isolated per-session profile directory under webview2-data so reloads or upgrades
                // never collide with an orphaned msedgewebview2.exe holding a stale user-data lock/args.
                var sessionFolder = Path.Combine(userDataRoot, $"run-{Guid.NewGuid():N}");
                activeSessionDataFolder = sessionFolder;
                host = new WebViewPopupHost(sessionFolder, bridge, Bundles, headless: false)
                {
                    OnAudioPlaybackChanged = playing => getMedia()?.PluginAudio.SetExternalPlaying(playing),
                    OnWarning = msg => Plugin.Log.Warning("HachidoriFfxiv: {Message}", msg),
                };
                if (controllerMode)
                    host.SetControllerMode(true);
            }
            catch (Exception ex)
            {
                controllerError = $"Failed to create WebViewPopupHost: {ex.Message}";
                DiagnosticLog.Write("webview", controllerError);
            }
        }
    }

    private static void PruneStaleSessionFolders(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root, "run-*"))
                TryDeleteDirectory(dir);
        }
        catch { }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { /* locked by an active WebView2 process; pruned on a future run */ }
    }

    private void OnConnectionChanged()
    {
        snapshotDirty = true;
        if (!PlatformSupported || !service.Link.Connected || !config.UseWebViewPopup || !config.AutoSyncWebViewBundle)
            return;

        var hostVer = service.Link.HostVersion;
        if (string.IsNullOrWhiteSpace(hostVer)) return;
        if (string.Equals(hostVer, lastSyncedHostVersion, StringComparison.Ordinal)
            && string.Equals(hostVer, Bundles.ActiveVersion, StringComparison.Ordinal))
            return;
        if ((DateTime.UtcNow - lastSyncKick).TotalSeconds < 15) return;

        lastSyncKick = DateTime.UtcNow;
        lastSyncedHostVersion = hostVer;
        _ = Task.Run(async () =>
        {
            var ok = await Bundles.EnsureBundleAsync(hostVer, config.WebViewBundlePath, allowNetwork: true, force: false, ct: lifetime.Token).ConfigureAwait(false);
            if (ok && !disposed)
                EnsureHostStarted();
        });
    }

    private void OnSnapshotChanged() => snapshotDirty = true;

    private void OnWordStatusChanged() => host?.NotifyWordStatusChanged();

    private void OnBundleChanged()
    {
        snapshotDirty = true;
        if (!disposed && config.UseWebViewPopup && host is null && Bundles.Ready)
            EnsureHostStarted();
    }

    private string ConfigFingerprint() =>
        string.Join('\u0001',
            config.HachidoriThemeOverride,
            config.HachidoriVariantOverride,
            config.FollowHachidoriOpacity,
            config.ApplyHachidoriCustomCss,
            config.ExtraPopupCss,
            config.PopupWidth,
            config.PopupMaxHeight,
            config.PopupBackgroundAlpha,
            config.HideDelaySeconds,
            config.MaxPopupDepth,
            config.ScanOnlyJapanese,
            config.UsePersonalDictionary,
            config.ShowLookupCounts,
            config.HonourDefinitionBlur,
            config.AutoPlayAudio,
            config.AudioVolume,
            config.AnkiTemplateId,
            config.CaptureScreenshot,
            adapter.NpcPicturesAvailable);

    private static void OpenExternalUrlSafely(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("webview", $"Could not open external URL: {ex.Message}");
        }
    }

    private static nint FindGameWindowHandle()
    {
        try
        {
            var h = Process.GetCurrentProcess().MainWindowHandle;
            if (h != 0) return h;
        }
        catch { }
        return 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        service.Link.ConnectionChanged -= OnConnectionChanged;
        service.Link.SnapshotChanged -= OnSnapshotChanged;
        service.Link.WordStatusChanged -= OnWordStatusChanged;
        Bundles.BundleChanged -= OnBundleChanged;
        lifetime.Cancel();
        WebViewPopupHost? toDispose;
        string? sessionToDelete;
        lock (hostLock)
        {
            toDispose = host;
            sessionToDelete = activeSessionDataFolder;
            host = null;
            activeSessionDataFolder = null;
        }
        toDispose?.Dispose();
        TryDeleteDirectory(sessionToDelete);
        Bundles.Dispose();
        lifetime.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);
}
