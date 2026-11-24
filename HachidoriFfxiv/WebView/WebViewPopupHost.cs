using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.WebView;

/// <summary>A screen-space rectangle (in physical pixels relative to the overlay viewport) occupied by a visible popup.</summary>
public readonly record struct OverlayRect(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(float x, float y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>A scannable definition text block extracted from an active WebView2 popup, with physical-pixel character rects.</summary>
public sealed record WebPopupScanBlock(
    RichBlock Block,
    IReadOnlyList<(int Index, float Left, float Top, float Right, float Bottom)> Cells);

/// <summary>Snapshot of the active popup DOM inside WebView2 (used for diagnostics and automated parity tests).</summary>
public sealed record WebViewInspectResult(
    string Renderer,
    string Theme,
    string Variant,
    int VisibleCount,
    string Text,
    string Background,
    string BorderColor,
    string Color,
    bool HasNpcButton,
    int NpcButtonCount,
    int MaxNpcButtonsPerBar,
    bool HasMineButton,
    string MineButtonState,
    bool HasAudioButton,
    bool AudioButtonVisible,
    OverlayRect[] Rects,
    OverlayRect? NpcButtonRect = null);

/// <summary>
/// Hosts Hachidori Web's unmodified popup frontend inside a single persistent, transparent,
/// visual-only DirectComposition WebView2 overlay running on a dedicated STA thread.
/// <list type="bullet">
///   <item>Never blocks Dalamud's D3D11/ImGui draw thread.</item>
///   <item>Uses <see cref="CoreWebView2CompositionController"/> hosted on a permanently click-through
///         unowned overlay HWND (<c>WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP</c>):
///         WebView2 creates no input HWND, never steals focus from FFXIV, never fights FFXIV's cursor,
///         and never blocks keyboard chat or controller input.</item>
///   <item>Hidden (<c>SW_HIDE</c>) whenever no popups are visible; shown directly above FFXIV in Z-order
///         without cross-thread window ownership (<c>AttachThreadInput</c>).</item>
///   <item>Mouse interactions over popup rects are captured by invisible Dalamud ImGui hitboxes and
///         forwarded synthetically via <see cref="SendMouseInput"/>.</item>
///   <item>Excluded from <see cref="Media.GameFrames"/> screenshots automatically because <c>GameFrames</c>
///         reads FFXIV's D3D11 backbuffer directly.</item>
/// </list>
/// </summary>
public sealed class WebViewPopupHost : IDisposable
{
    private const string AssetHost = "hachidori.ffxiv.local";
    private const string OverlayHost = "ffxiv-host.local";
    private const string HostPageUrl = $"https://{OverlayHost}/host.html";
    private const uint WmAppInvoke = 0x8000 + 42;

    private static readonly Guid IidIDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
    private static readonly string HostHtmlAsset = ReadEmbeddedAsset("host.html");
    private static readonly string BridgeJsAsset = ReadEmbeddedAsset("ffxiv-bridge.js");
    private static readonly string OverlayModeAsset = ReadEmbeddedAsset("overlay-mode.js");

    private readonly string userDataFolder;
    private readonly HachidoriWebBridge bridge;
    private readonly HachidoriBundleManager bundles;
    private readonly bool headless;
    private readonly ConcurrentQueue<Action> staQueue = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<WebViewInspectResult>> inspectWaiters = new();
    private readonly ManualResetEventSlim hwndReady = new(false);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Thread staThread;

    private nint hwnd;
    private int staThreadId;
    private nint dcompDevice;
    private nint dcompTarget;
    private nint dcompVisual;
    private object? dcompVisualRcw;
    private CoreWebView2Environment? environment;
    private CoreWebView2CompositionController? compositionController;
    private CoreWebView2Controller? controller;
    private CoreWebView2? webView;
    private string? mappedBundlePath;
    private string? mappedBundleVersion;
    private int bundleNavigationSeq;
    private int nextInspectId;
    private bool disposed;

    private volatile bool initialized;
    private volatile bool ready;
    private volatile bool failed;
    private volatile bool smokeTestPassed;
    private volatile bool usedFallbackSelector;
    private volatile string hostTagName = string.Empty;
    private volatile string? lastError;
    private volatile string statusText = "Starting WebView2…";
    private volatile int visibleCount;
    private volatile int matchedLength;
    private volatile uint systemCursorId;
    private volatile string matchedExpression = string.Empty;
    private volatile string matchedReading = string.Empty;
    private OverlayRect[] popupRects = [];
    private OverlayRect[] popupLayerRects = [];
    private readonly object rectsLock = new();
    private readonly Dictionary<int, List<WebPopupScanBlock>> popupScanBlocks = new();
    private readonly object scanBlocksLock = new();
    private bool controllerMode;
    private string? lastCursorJson;
    private bool overlayShown;
    private long lookupPendingUntilUtcTicks;
    private nint lastGameHwnd;
    private int lastX, lastY, lastW = 1280, lastH = 720;
    private TaskCompletionSource<bool> readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Keep delegate alive for the lifetime of the Win32 window class.
    private readonly WndProcDelegate wndProcDelegate;
    private readonly string windowClassName;

    public WebViewPopupHost(
        string userDataFolder,
        HachidoriWebBridge bridge,
        HachidoriBundleManager bundles,
        nint ownerHwnd = 0,
        bool headless = false,
        int initialWidth = 1280,
        int initialHeight = 720)
    {
        _ = ownerHwnd; // Kept in signature for source compatibility; overlay stays unowned to avoid cross-thread AttachThreadInput.
        this.userDataFolder = userDataFolder;
        this.bridge = bridge;
        this.bundles = bundles;
        this.headless = headless;
        lastW = Math.Max(320, initialWidth);
        lastH = Math.Max(240, initialHeight);
        windowClassName = $"HachidoriFfxivWebView_{Guid.NewGuid():N}";
        wndProcDelegate = WndProc;

        bundles.BundleChanged += OnBundleChanged;

        staThread = new Thread(RunStaLoop)
        {
            Name = "HachidoriFfxiv.WebView2",
            IsBackground = true,
        };
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
    }

    public bool IsReady => ready && !failed && !disposed;
    public bool IsFailed => failed;
    public string? LastError => lastError;
    public string StatusText => statusText;
    public int VisibleCount => visibleCount;
    public bool AnyOpen => visibleCount > 0;
    public int MatchedLength => matchedLength;
    public string MatchedExpression => matchedExpression;
    public string MatchedReading => matchedReading;
    public uint SystemCursorId => systemCursorId;
    public string? LoadedVersion => mappedBundleVersion;
    public bool SmokeTestPassed => smokeTestPassed;
    public bool UsedFallbackSelector => usedFallbackSelector;
    public string HostTagName => hostTagName;
    internal nint OverlayHwnd => hwnd;

    /// <summary>Invoked whenever WebView2 starts or stops playing pronunciation audio.</summary>
    public Action<bool>? OnAudioPlaybackChanged { get; set; }

    /// <summary>Optional warning callback (wired to <c>DalamudServices.Log.Warning</c> in Dalamud).</summary>
    public Action<string>? OnWarning { get; set; }

    public Task<bool> WaitUntilReadyAsync(TimeSpan timeout) =>
        readyTcs.Task.WaitAsync(timeout);

    /// <summary>Returns true if WebView2 Runtime and DirectComposition overlays are available on this OS.</summary>
    public static bool IsRuntimeAvailable(out string? version)
    {
        if (!PlatformInfo.Current.SupportsWebView2Overlay)
        {
            version = null;
            return false;
        }
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrWhiteSpace(version);
        }
        catch
        {
            version = null;
            return false;
        }
    }

    /// <summary>True if the screen-space point (relative to the game client viewport) lies inside any visible popup.</summary>
    public bool ContainsViewportPoint(float x, float y)
    {
        OverlayRect[] current;
        lock (rectsLock) current = popupRects;
        for (var i = 0; i < current.Length; i++)
        {
            if (current[i].Contains(x, y))
                return true;
        }
        return false;
    }

    public OverlayRect[] GetPopupRects()
    {
        lock (rectsLock) return [.. popupRects];
    }

    public OverlayRect? GetPopupLayerRect(int depth)
    {
        lock (rectsLock)
            return depth >= 0 && depth < popupLayerRects.Length ? popupLayerRects[depth] : null;
    }

    public IReadOnlyList<WebPopupScanBlock> GetScanBlocks(int depth)
    {
        lock (scanBlocksLock)
            return popupScanBlocks.TryGetValue(depth, out var list) ? [.. list] : [];
    }

    /// <summary>
    /// Called each frame from Dalamud's draw loop to keep the visual-only DirectComposition overlay
    /// aligned with FFXIV's client viewport, hidden when no popups are open, and layered directly above
    /// FFXIV in Z-order when popups are visible. Cheap when nothing changed.
    /// </summary>
    public void SyncOverlayFrame(
        int clientScreenX,
        int clientScreenY,
        int clientWidth,
        int clientHeight,
        bool gameVisible,
        nint targetGameHwnd = 0)
    {
        if (disposed || headless || hwnd == 0) return;

        lastGameHwnd = targetGameHwnd;
        var lookupPending = Interlocked.Read(ref lookupPendingUntilUtcTicks) > DateTime.UtcNow.Ticks;
        var shouldShow = gameVisible && IsReady && (visibleCount > 0 || lookupPending);
        var w = Math.Max(1, clientWidth);
        var h = Math.Max(1, clientHeight);
        var geomChanged = clientScreenX != lastX || clientScreenY != lastY || w != lastW || h != lastH;
        var showChanged = shouldShow != overlayShown;
        var zOrderStale = shouldShow && targetGameHwnd != 0 && !IsDirectlyAbove(hwnd, targetGameHwnd);

        if (!geomChanged && !showChanged && !zOrderStale)
            return;

        lastX = clientScreenX;
        lastY = clientScreenY;
        lastW = w;
        lastH = h;
        overlayShown = shouldShow;

        PostToSta(() =>
        {
            if (hwnd == 0) return;

            // Keep controller bounds synced even while hidden so the first popup lays out against the real game viewport.
            if (controller is not null)
            {
                var bounds = new Rectangle(0, 0, lastW, lastH);
                if (controller.Bounds != bounds)
                {
                    controller.Bounds = bounds;
                    CommitDirectComposition();
                }
            }

            if (!shouldShow)
            {
                if (geomChanged)
                {
                    SetWindowPos(hwnd, 0, lastX, lastY, lastW, lastH,
                        SwpNoActivate | SwpNoZOrder | SwpHideWindow);
                }
                else
                {
                    ShowWindow(hwnd, SwHide);
                }
                return;
            }

            var insertAfter = ResolveInsertAfter(hwnd, targetGameHwnd);
            var flags = SwpNoActivate | SwpShowWindow;
            if (insertAfter == HwndNoChange)
                flags |= SwpNoZOrder;

            SetWindowPos(hwnd, insertAfter == HwndNoChange ? 0 : insertAfter, lastX, lastY, lastW, lastH, flags);
            CommitDirectComposition();
        });
    }

    /// <summary>Forwards synthetic mouse input captured by Dalamud's ImGui hitbox into WebView2's composition controller.</summary>
    public void SendMouseInput(
        CoreWebView2MouseEventKind kind,
        CoreWebView2MouseEventVirtualKeys virtualKeys,
        uint mouseData,
        int x,
        int y)
    {
        if (disposed || !IsReady) return;
        PostToSta(() =>
        {
            if (disposed || compositionController is null) return;
            try
            {
                compositionController.SendMouseInput(kind, virtualKeys, mouseData, new Point(x, y));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("webview", $"SendMouseInput({kind}) failed: {ex.Message}");
            }
        });
    }

    /// <summary>Pushes the latest projected Hachidori storage snapshot + audio volume + NPC capture flag to the WebView.</summary>
    public void PushStorageUpdate(JsonObject snapshot, float audioVolume, bool npcPicturesAvailable)
    {
        if (!IsReady) return;
        PostJson(new JsonObject
        {
            ["kind"] = "storageUpdate",
            ["snapshot"] = snapshot.DeepClone(),
            ["audioVolume"] = Math.Clamp(audioVolume, 0f, 1f),
            ["npcPicturesAvailable"] = npcPicturesAvailable,
        });
    }

    public void NotifyWordStatusChanged()
    {
        if (!IsReady) return;
        PostJson(new JsonObject { ["kind"] = "wordStatusChanged" });
    }

    /// <summary>Triggers Hachidori's real popup lookup anchored at the FFXIV text rectangle.</summary>
    public void ShowLookup(string sentence, int charIndex, Vector2 anchorMin, Vector2 anchorMax, string documentTitle, int tokenLength = 1)
    {
        if (!IsReady) return;
        bridge.CancelPendingPreflights();
        var (normSentence, normIndex, normTokenLength) = HachidoriWebBridge.NormalizeLookupContext(sentence, charIndex, tokenLength);
        if (normSentence.Length == 0 || normIndex >= normSentence.Length) return;
        bridge.SetActiveLookupContext(normSentence, normIndex);
        // Wake the overlay HWND immediately so Chromium's document.visibilityState is visible while laying out the popup.
        Interlocked.Exchange(ref lookupPendingUntilUtcTicks, DateTime.UtcNow.AddMilliseconds(750).Ticks);
        if (!headless)
        {
            overlayShown = true;
            PostToSta(() =>
            {
                if (hwnd == 0 || IsWindowVisible(hwnd)) return;
                var insertAfter = ResolveInsertAfter(hwnd, lastGameHwnd);
                var flags = SwpNoActivate | SwpShowWindow;
                if (insertAfter == HwndNoChange)
                    flags |= SwpNoZOrder;
                SetWindowPos(hwnd, insertAfter == HwndNoChange ? 0 : insertAfter, lastX, lastY, lastW, lastH, flags);
                CommitDirectComposition();
            });
        }
        PostJson(new JsonObject
        {
            ["kind"] = "showLookup",
            ["sentence"] = normSentence,
            ["charIndex"] = normIndex,
            ["tokenLength"] = normTokenLength,
            ["documentTitle"] = documentTitle,
            ["anchorRect"] = new JsonObject
            {
                ["left"] = anchorMin.X,
                ["top"] = anchorMin.Y,
                ["width"] = MathF.Max(8f, anchorMax.X - anchorMin.X),
                ["height"] = MathF.Max(12f, anchorMax.Y - anchorMin.Y),
            },
        });
    }

    public void CloseAll()
    {
        bridge.CancelPendingPreflights();
        bridge.ClearActiveLookupContext();
        Interlocked.Exchange(ref lookupPendingUntilUtcTicks, 0);
        if (!IsReady) return;
        PostJson(new JsonObject { ["kind"] = "closeAll" });
    }

    public void CloseTop()
    {
        if (!IsReady) return;
        PostJson(new JsonObject { ["kind"] = "closeTop" });
    }

    public void CloseFrom(int depth)
    {
        if (depth <= 0)
        {
            CloseAll();
            return;
        }
        if (!IsReady) return;
        PostJson(new JsonObject { ["kind"] = "closeFrom", ["depth"] = depth });
    }

    public void SetControllerMode(bool active)
    {
        controllerMode = active;
        if (!active)
        {
            lastCursorJson = null;
            lock (scanBlocksLock) popupScanBlocks.Clear();
        }
        if (!IsReady) return;
        PostJson(new JsonObject { ["kind"] = "controllerMode", ["active"] = active });
    }

    public void ScanPopupAt(int parentDepth, float viewportX, float viewportY)
    {
        if (!IsReady) return;
        PostJson(new JsonObject
        {
            ["kind"] = "scanPopup",
            ["depth"] = parentDepth,
            ["x"] = viewportX,
            ["y"] = viewportY,
        });
    }

    public void SetControllerCursor(IReadOnlyList<(float Left, float Top, float Right, float Bottom)>? rects, string? colorCss = null)
    {
        if (!IsReady) return;
        var arr = new JsonArray();
        if (rects is not null)
        {
            foreach (var (l, t, r, b) in rects)
            {
                arr.Add(new JsonObject
                {
                    ["left"] = MathF.Round(l),
                    ["top"] = MathF.Round(t),
                    ["right"] = MathF.Round(r),
                    ["bottom"] = MathF.Round(b),
                });
            }
        }
        var payload = new JsonObject
        {
            ["kind"] = "controllerCursor",
            ["rects"] = arr,
            ["color"] = colorCss ?? string.Empty,
        };
        var json = payload.ToJsonString();
        if (string.Equals(json, lastCursorJson, StringComparison.Ordinal))
            return;
        lastCursorJson = json;
        PostToSta(() =>
        {
            try { webView?.PostWebMessageAsJson(json); }
            catch (Exception ex) { DiagnosticLog.Write("webview", $"PostWebMessage failed: {ex.Message}"); }
        });
    }

    public void SendCommand(string action)
    {
        if (!IsReady) return;
        PostJson(new JsonObject { ["kind"] = "command", ["action"] = action });
    }

    public void ScrollPopup(int depth, float deltaY)
    {
        if (!IsReady) return;
        PostJson(new JsonObject { ["kind"] = "scroll", ["depth"] = depth, ["deltaY"] = deltaY });
    }

    /// <summary>Reloads the WebView with the active bundle from <see cref="HachidoriBundleManager"/>.</summary>
    public void ReloadBundle()
    {
        PostToSta(() => _ = NavigateActiveBundleAsync());
    }

    /// <summary>Queries the live popup DOM inside WebView2 (theme, computed colors, buttons, text, rects).</summary>
    public async Task<WebViewInspectResult> InspectAsync(TimeSpan? timeout = null)
    {
        if (!IsReady)
            throw new InvalidOperationException(lastError ?? "WebView2 host is not ready.");
        var id = Interlocked.Increment(ref nextInspectId);
        var tcs = new TaskCompletionSource<WebViewInspectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        inspectWaiters[id] = tcs;
        PostJson(new JsonObject { ["kind"] = "inspect", ["id"] = id });
        try
        {
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            inspectWaiters.TryRemove(id, out _);
        }
    }

    /// <summary>Captures a PNG screenshot of the WebView2 viewport (used for visual verification).</summary>
    public Task<byte[]> CapturePngAsync()
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostToSta(async () =>
        {
            try
            {
                if (webView is null) throw new InvalidOperationException("WebView2 is not initialized.");
                using var ms = new MemoryStream();
                await webView.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                tcs.TrySetResult(ms.ToArray());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    // ------------------------------------------------------------------ STA thread + DirectComposition + WebView2 lifecycle

    private void OnBundleChanged()
    {
        if (disposed || !initialized) return;
        ReloadBundle();
    }

    private void PostToSta(Action action)
    {
        if (disposed) return;
        staQueue.Enqueue(action);
        if (hwnd != 0)
            PostMessage(hwnd, WmAppInvoke, 0, 0);
    }

    private void PostJson(JsonObject payload)
    {
        var json = payload.ToJsonString();
        PostToSta(() =>
        {
            try
            {
                webView?.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("webview", $"PostWebMessage failed: {ex.Message}");
            }
        });
    }

    private void RunStaLoop()
    {
        staThreadId = GetCurrentThreadId();
        try
        {
            CreateOverlayWindow();
            SynchronizationContext.SetSynchronizationContext(new Win32SyncContext(this));
            hwndReady.Set();
            _ = InitializeWebViewAsync();

            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Fail($"WebView2 STA loop failed: {ex.Message}");
            hwndReady.Set();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
            CleanupOnSta();
        }
    }

    private sealed class Win32SyncContext(WebViewPopupHost owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            owner.PostToSta(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (GetCurrentThreadId() == owner.staThreadId)
            {
                d(state);
                return;
            }
            using var done = new ManualResetEventSlim(false);
            Exception? captured = null;
            owner.PostToSta(() =>
            {
                try { d(state); }
                catch (Exception ex) { captured = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (captured is not null) throw captured;
        }
    }

    private void CreateOverlayWindow()
    {
        var hInstance = GetModuleHandle(null);
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = windowClassName,
        };
        RegisterClassEx(ref wc);

        // Visual-only, permanently click-through, never-activating, unowned DirectComposition overlay HWND.
        // WS_EX_LAYERED | WS_EX_TRANSPARENT makes Windows skip this window completely for cross-thread hit-testing
        // and cursor updates; WS_EX_NOREDIRECTIONBITMAP lets DirectComposition composite per-pixel alpha directly.
        var exStyle = WsExToolWindow | WsExNoActivate | WsExTransparent | WsExLayered | WsExNoRedirectionBitmap;
        var style = WsPopup;
        var x = headless ? -32000 : lastX;
        var y = headless ? -32000 : lastY;

        hwnd = CreateWindowEx(
            exStyle,
            windowClassName,
            "HachidoriFfxivOverlay",
            style,
            x, y, lastW, lastH,
            0,
            0,
            hInstance,
            0);

        if (hwnd == 0)
            throw new InvalidOperationException($"CreateWindowEx failed (Win32 error {Marshal.GetLastWin32Error()}).");

        // Ensure the layered window has full composition alpha (per-pixel transparency comes from WebView2).
        SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);

        if (headless)
        {
            // Keep off-screen window visible so Chromium renders layout/paint frames for automated verification.
            ShowWindow(hwnd, SwShowNoActivate);
        }
    }

    private unsafe void InitializeDirectComposition()
    {
        if (!PlatformInfo.Current.IsDirectCompositionAvailable)
            throw new InvalidOperationException($"DirectComposition is not available on {PlatformInfo.Current.DisplayName}.");

        var iid = IidIDCompositionDevice;
        var hr = DCompositionCreateDevice(0, ref iid, out dcompDevice);
        if (hr != 0 || dcompDevice == 0)
            throw new InvalidOperationException($"DCompositionCreateDevice failed (HRESULT 0x{hr:X8}).");

        var devVtbl = *(nint**)dcompDevice;
        var createTarget = (delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)devVtbl[6];
        var createVisual = (delegate* unmanaged[Stdcall]<nint, nint*, int>)devVtbl[7];

        nint target = 0, visual = 0;
        hr = createTarget(dcompDevice, hwnd, 1, &target);
        if (hr != 0 || target == 0)
            throw new InvalidOperationException($"IDCompositionDevice::CreateTargetForHwnd failed (HRESULT 0x{hr:X8}).");
        dcompTarget = target;

        hr = createVisual(dcompDevice, &visual);
        if (hr != 0 || visual == 0)
            throw new InvalidOperationException($"IDCompositionDevice::CreateVisual failed (HRESULT 0x{hr:X8}).");
        dcompVisual = visual;

        var targetVtbl = *(nint**)dcompTarget;
        var setRoot = (delegate* unmanaged[Stdcall]<nint, nint, int>)targetVtbl[3];
        hr = setRoot(dcompTarget, dcompVisual);
        if (hr != 0)
            throw new InvalidOperationException($"IDCompositionTarget::SetRoot failed (HRESULT 0x{hr:X8}).");

        CommitDirectComposition();
    }

    private unsafe void CommitDirectComposition()
    {
        if (dcompDevice == 0) return;
        var devVtbl = *(nint**)dcompDevice;
        var commit = (delegate* unmanaged[Stdcall]<nint, int>)devVtbl[3];
        commit(dcompDevice);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            statusText = "Checking WebView2 runtime…";
            if (!IsRuntimeAvailable(out var runtimeVersion))
                throw new InvalidOperationException("Microsoft Edge WebView2 Runtime is not installed.");

            statusText = "Initializing DirectComposition…";
            InitializeDirectComposition();

            statusText = "Creating WebView2 environment…";
            Directory.CreateDirectory(userDataFolder);
            var envOptions = new CoreWebView2EnvironmentOptions(
                "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows --autoplay-policy=no-user-gesture-required");
            environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, envOptions)
                .WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);

            statusText = "Creating WebView2 composition controller…";
            compositionController = await environment.CreateCoreWebView2CompositionControllerAsync(hwnd)
                .WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
            controller = compositionController;
            controller.DefaultBackgroundColor = Color.Transparent;
            dcompVisualRcw = Marshal.GetObjectForIUnknown(dcompVisual);
            compositionController.RootVisualTarget = dcompVisualRcw;
            CommitDirectComposition();
            controller.Bounds = new Rectangle(0, 0, lastW, lastH);
            controller.IsVisible = true;
            compositionController.CursorChanged += (_, _) =>
            {
                try { systemCursorId = compositionController.SystemCursorId; }
                catch { systemCursorId = 0; }
            };

            webView = controller.CoreWebView2;
            webView.Settings.IsScriptEnabled = true;
            webView.Settings.IsWebMessageEnabled = true;
            webView.Settings.AreDefaultScriptDialogsEnabled = false;
            webView.Settings.IsStatusBarEnabled = false;
            webView.Settings.AreDevToolsEnabled = true;
            webView.Settings.AreDefaultContextMenusEnabled = false;

            webView.WebMessageReceived += OnWebMessageReceived;
            webView.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                    Fail($"WebView2 navigation failed: {args.WebErrorStatus}");
            };
            webView.ProcessFailed += (_, args) =>
            {
                DiagnosticLog.Write("webview", $"WebView2 process failed: {args.ProcessFailedKind}");
                if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
                    or CoreWebView2ProcessFailedKind.RenderProcessExited)
                {
                    Fail($"WebView2 process exited ({args.ProcessFailedKind}).");
                }
            };

            var hostAssetsDir = Path.Combine(userDataFolder, "ffxiv-host");
            Directory.CreateDirectory(hostAssetsDir);
            File.WriteAllText(Path.Combine(hostAssetsDir, "host.html"), HostHtmlAsset, Encoding.UTF8);
            File.WriteAllText(Path.Combine(hostAssetsDir, "ffxiv-bridge.js"), BridgeJsAsset, Encoding.UTF8);
            File.WriteAllText(Path.Combine(hostAssetsDir, "overlay-mode.js"), OverlayModeAsset, Encoding.UTF8);
            webView.SetVirtualHostNameToFolderMapping(OverlayHost, hostAssetsDir, CoreWebView2HostResourceAccessKind.Allow);

            initialized = true;
            statusText = "Loading Hachidori Web…";
            DiagnosticLog.Write("webview", $"Initialized DirectComposition WebView2 runtime {runtimeVersion}.");
            await NavigateActiveBundleAsync();
        }
        catch (Exception ex)
        {
            Fail($"WebView2 initialization failed: {ex.Message}");
        }
    }

    private async Task NavigateActiveBundleAsync()
    {
        if (disposed || webView is null) return;
        if (!bundles.Ready || bundles.ActivePath is not { } bundlePath)
        {
            lastError = bundles.LastError ?? "Waiting for Hachidori Web bundle sync…";
            return;
        }
        if (string.Equals(mappedBundlePath, bundlePath, StringComparison.OrdinalIgnoreCase))
            return;

        ready = false;
        smokeTestPassed = false;
        usedFallbackSelector = false;
        hostTagName = string.Empty;
        mappedBundlePath = bundlePath;
        mappedBundleVersion = bundles.ActiveVersion;
        var seq = Interlocked.Increment(ref bundleNavigationSeq);
        webView.SetVirtualHostNameToFolderMapping(AssetHost, bundlePath, CoreWebView2HostResourceAccessKind.Allow);
        DiagnosticLog.Write("webview", $"Loading Hachidori Web v{mappedBundleVersion} from {bundlePath}");
        if (seq == 1 || !string.Equals(webView.Source, HostPageUrl, StringComparison.OrdinalIgnoreCase))
            webView.Navigate(HostPageUrl);
        else
            webView.Reload();
        await Task.CompletedTask;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject? msg;
        try
        {
            msg = JsonNode.Parse(e.WebMessageAsJson) as JsonObject;
        }
        catch
        {
            return;
        }
        if (msg is null) return;

        switch (msg["kind"]?.ToString())
        {
            case "loaded":
                // Send initial projected snapshot so ffxiv-bridge.js boots Hachidori's manifest scripts.
                PostJson(new JsonObject
                {
                    ["kind"] = "init",
                    ["snapshot"] = bridge.BuildProjectedSnapshot(),
                    ["audioVolume"] = bridge.AudioVolume,
                    ["npcPicturesAvailable"] = bridge.NpcPicturesAvailable?.Invoke() ?? false,
                    ["bundleKey"] = $"{mappedBundleVersion ?? "0"}-{bundleNavigationSeq}",
                });
                return;

            case "ready":
                ready = true;
                failed = false;
                lastError = null;
                statusText = "Ready";
                smokeTestPassed = true;
                usedFallbackSelector = BoolVal(msg["usedFallbackSelector"]) || BoolVal(msg["usedFallbackHostTag"]);
                hostTagName = msg["hostTag"]?.ToString() ?? string.Empty;
                bundles.MarkBootSuccess(mappedBundleVersion);
                DiagnosticLog.Write("webview", $"Hachidori Web v{msg["version"]} popup host ready (hostTag={hostTagName}, fallbackSelector={usedFallbackSelector}).");
                if (usedFallbackSelector)
                    OnWarning?.Invoke($"Hachidori Web v{msg["version"]} required structural DOM fallback (hostTag={hostTagName}); upstream CSS/tag names may have changed.");
                if (controllerMode)
                    PostJson(new JsonObject { ["kind"] = "controllerMode", ["active"] = true });
                readyTcs.TrySetResult(true);
                return;

            case "bootError":
                var err = msg["error"]?.ToString() ?? "Unknown boot error.";
                DiagnosticLog.Write("webview", $"Hachidori Web v{mappedBundleVersion} boot error: {err}");
                OnWarning?.Invoke($"Hachidori Web v{mappedBundleVersion} failed boot/smoke test: {err}");
                var previousPath = mappedBundlePath;
                if (bundles.MarkBootFailure(mappedBundleVersion, err) && bundles.ActivePath is not null
                    && !string.Equals(bundles.ActivePath, previousPath, StringComparison.OrdinalIgnoreCase))
                {
                    DiagnosticLog.Write("webview", $"Rolled back to Hachidori Web v{bundles.ActiveVersion}; reloading.");
                    if (!string.Equals(mappedBundlePath, bundles.ActivePath, StringComparison.OrdinalIgnoreCase))
                        _ = NavigateActiveBundleAsync();
                }
                else
                {
                    Fail($"Hachidori Web boot failed: {err}");
                }
                return;

            case "rpc":
                HandleRpcMessage(msg);
                return;

            case "rects":
                HandleRectsMessage(msg);
                return;

            case "popupTexts":
                HandlePopupTextsMessage(msg);
                return;

            case "audioPlayback":
                OnAudioPlaybackChanged?.Invoke(BoolVal(msg["playing"]));
                return;

            case "lookupMiss":
                if (visibleCount == 0)
                    Interlocked.Exchange(ref lookupPendingUntilUtcTicks, 0);
                return;

            case "match":
                matchedLength = msg["length"] is JsonValue lv && lv.TryGetValue<int>(out var len) ? len : 0;
                matchedExpression = msg["expression"]?.ToString() ?? string.Empty;
                matchedReading = msg["reading"]?.ToString() ?? string.Empty;
                return;

            case "npcCapture":
                bridge.OnNpcCaptureRequested?.Invoke(
                    msg["expression"]?.ToString() ?? string.Empty,
                    msg["reading"]?.ToString() ?? string.Empty);
                return;

            case "openExternal":
                if (msg["url"]?.ToString() is { Length: > 0 } url)
                    bridge.OnOpenExternalUrl?.Invoke(url);
                return;

            case "openSettings":
                bridge.OnOpenSettings?.Invoke(msg["section"]?.ToString() ?? string.Empty);
                return;

            case "inspectReply":
                HandleInspectReply(msg);
                return;
        }
    }

    private void HandleRpcMessage(JsonObject msg)
    {
        var id = msg["id"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
        var inner = msg["message"] as JsonObject ?? new JsonObject();
        _ = Task.Run(async () =>
        {
            try
            {
                var reply = await bridge.HandleRpcAsync(inner, lifetime.Token).ConfigureAwait(false);
                PostJson(new JsonObject
                {
                    ["kind"] = "rpcReply",
                    ["id"] = id,
                    ["reply"] = reply,
                });
            }
            catch (Exception ex)
            {
                PostJson(new JsonObject
                {
                    ["kind"] = "rpcReply",
                    ["id"] = id,
                    ["error"] = ex.Message,
                });
            }
        });
    }

    private void HandleRectsMessage(JsonObject msg)
    {
        var count = msg["visibleCount"] is JsonValue vc && vc.TryGetValue<int>(out var c) ? c : 0;
        var list = new List<OverlayRect>();
        if (msg["rects"] is JsonArray arr)
        {
            foreach (var r in arr.OfType<JsonObject>())
            {
                list.Add(new OverlayRect(
                    IntVal(r["left"]),
                    IntVal(r["top"]),
                    IntVal(r["right"]),
                    IntVal(r["bottom"])));
            }
        }
        var layers = new List<OverlayRect>();
        if (msg["layerRects"] is JsonArray layerArr)
        {
            foreach (var r in layerArr.OfType<JsonObject>())
            {
                layers.Add(new OverlayRect(
                    IntVal(r["left"]),
                    IntVal(r["top"]),
                    IntVal(r["right"]),
                    IntVal(r["bottom"])));
            }
        }
        lock (rectsLock)
        {
            popupRects = list.ToArray();
            popupLayerRects = layers.ToArray();
        }
        visibleCount = count;
        if (count == 0)
        {
            lock (scanBlocksLock) popupScanBlocks.Clear();
        }
    }

    private void HandlePopupTextsMessage(JsonObject msg)
    {
        var layersArr = msg["layers"] as JsonArray;
        lock (scanBlocksLock)
        {
            var prev = new Dictionary<int, List<WebPopupScanBlock>>(popupScanBlocks);
            popupScanBlocks.Clear();
            if (layersArr is null)
                return;

            foreach (var layerNode in layersArr.OfType<JsonObject>())
            {
                var depth = IntVal(layerNode["depth"]);
                prev.TryGetValue(depth, out var prevBlocks);
                var nextBlocks = new List<WebPopupScanBlock>();
                var blockIdx = 0;
                foreach (var blockNode in (layerNode["blocks"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var text = blockNode["text"]?.ToString() ?? string.Empty;
                    if (text.Length == 0)
                    {
                        blockIdx++;
                        continue;
                    }
                    var cellsArr = blockNode["cells"] as JsonArray;
                    if (cellsArr is null || cellsArr.Count == 0)
                    {
                        blockIdx++;
                        continue;
                    }
                    var cells = new List<(int Index, float Left, float Top, float Right, float Bottom)>(cellsArr.Count);
                    foreach (var cellNode in cellsArr.OfType<JsonArray>())
                    {
                        if (cellNode.Count < 5) continue;
                        cells.Add((
                            IntVal(cellNode[0]),
                            FloatVal(cellNode[1]),
                            FloatVal(cellNode[2]),
                            FloatVal(cellNode[3]),
                            FloatVal(cellNode[4])));
                    }
                    if (cells.Count == 0)
                    {
                        blockIdx++;
                        continue;
                    }

                    // Reuse the previous RichBlock instance when the definition text at this depth/index
                    // did not change (e.g. while scrolling) so controller cursor identity stays stable.
                    RichBlock block;
                    if (prevBlocks is not null && blockIdx < prevBlocks.Count
                        && string.Equals(prevBlocks[blockIdx].Block.ScanText, text, StringComparison.Ordinal))
                    {
                        block = prevBlocks[blockIdx].Block;
                    }
                    else
                    {
                        block = prevBlocks?.FirstOrDefault(b => string.Equals(b.Block.ScanText, text, StringComparison.Ordinal))?.Block
                            ?? new RichBlock().Add(text);
                    }
                    nextBlocks.Add(new WebPopupScanBlock(block, cells));
                    blockIdx++;
                }
                if (nextBlocks.Count > 0)
                    popupScanBlocks[depth] = nextBlocks;
            }
        }
    }

    private static float FloatVal(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? (float)d : 0f;

    private void HandleInspectReply(JsonObject msg)
    {
        var id = IntVal(msg["id"]);
        if (!inspectWaiters.TryRemove(id, out var tcs)) return;
        var rects = (msg["rects"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(r => new OverlayRect(IntVal(r["left"]), IntVal(r["top"]), IntVal(r["right"]), IntVal(r["bottom"])))
            .ToArray();
        OverlayRect? npcRect = msg["npcButtonRect"] is JsonObject nr
            ? new OverlayRect(IntVal(nr["left"]), IntVal(nr["top"]), IntVal(nr["right"]), IntVal(nr["bottom"]))
            : null;
        tcs.TrySetResult(new WebViewInspectResult(
            msg["renderer"]?.ToString() ?? string.Empty,
            msg["theme"]?.ToString() ?? string.Empty,
            msg["variant"]?.ToString() ?? string.Empty,
            IntVal(msg["visibleCount"]),
            msg["text"]?.ToString() ?? string.Empty,
            msg["background"]?.ToString() ?? string.Empty,
            msg["borderColor"]?.ToString() ?? string.Empty,
            msg["color"]?.ToString() ?? string.Empty,
            BoolVal(msg["hasNpcButton"]),
            IntVal(msg["npcButtonCount"]),
            IntVal(msg["maxNpcButtonsPerBar"]),
            BoolVal(msg["hasMineButton"]),
            msg["mineButtonState"]?.ToString() ?? string.Empty,
            BoolVal(msg["hasAudioButton"]),
            BoolVal(msg["audioButtonVisible"]),
            rects,
            npcRect));
    }

    private static int IntVal(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? (int)Math.Round(d) : 0;

    private static bool BoolVal(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private void Fail(string error)
    {
        lastError = error;
        failed = true;
        ready = false;
        OnAudioPlaybackChanged?.Invoke(false);
        DiagnosticLog.Write("webview", error);
        readyTcs.TrySetResult(false);
    }

    private nint WndProc(nint h, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WmAppInvoke:
                while (staQueue.TryDequeue(out var action))
                {
                    try { action(); }
                    catch (Exception ex) { DiagnosticLog.Write("webview", $"STA invoke error: {ex.Message}"); }
                }
                return 0;

            case WmMouseActivate:
                return MaNoActivate;

            case WmNcHitTest:
                return HtTransparent;

            case WmEraseBkgnd:
                return 1;

            case WmClose:
                // Close WebView2 controller BEFORE destroying the HWND so msedgewebview2.exe shuts down cleanly.
                CleanupOnSta();
                PostQuitMessage(0);
                return 0;

            case WmDestroy:
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProc(h, msg, wParam, lParam);
    }

    private static bool IsDirectlyAbove(nint overlay, nint game)
    {
        var cur = GetWindow(game, GwHwndPrev);
        for (var i = 0; i < 8 && cur != 0; i++)
        {
            if (cur == overlay) return true;
            if (IsWindowVisible(cur)) return false;
            cur = GetWindow(cur, GwHwndPrev);
        }
        return false;
    }

    private static nint ResolveInsertAfter(nint overlay, nint game)
    {
        if (game == 0) return HwndTop;
        var gameTopMost = (GetWindowLongPtr(game, GwlExStyle).ToInt64() & WsExTopMost) != 0;
        var prev = GetWindow(game, GwHwndPrev);
        if (prev == overlay) return HwndNoChange;
        if (prev == 0) return gameTopMost ? HwndTopMost : HwndTop;
        var prevTopMost = (GetWindowLongPtr(prev, GwlExStyle).ToInt64() & WsExTopMost) != 0;
        if (prevTopMost && !gameTopMost)
            return HwndTop;
        return prev;
    }

    private void CleanupOnSta()
    {
        try
        {
            if (compositionController is not null)
                compositionController.RootVisualTarget = null;
        }
        catch { }
        try { controller?.Close(); } catch { }
        compositionController = null;
        controller = null;
        webView = null;
        environment = null;
        if (dcompVisualRcw is not null)
        {
            try { Marshal.ReleaseComObject(dcompVisualRcw); } catch { }
            dcompVisualRcw = null;
        }
        if (dcompVisual != 0) { try { Marshal.Release(dcompVisual); } catch { } dcompVisual = 0; }
        if (dcompTarget != 0) { try { Marshal.Release(dcompTarget); } catch { } dcompTarget = 0; }
        if (dcompDevice != 0) { try { Marshal.Release(dcompDevice); } catch { } dcompDevice = 0; }
        if (hwnd != 0)
        {
            try { DestroyWindow(hwnd); } catch { }
            hwnd = 0;
        }
        try { UnregisterClass(windowClassName, GetModuleHandle(null)); } catch { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        OnAudioPlaybackChanged?.Invoke(false);
        bundles.BundleChanged -= OnBundleChanged;
        lifetime.Cancel();
        readyTcs.TrySetResult(false);
        foreach (var (_, waiter) in inspectWaiters) waiter.TrySetCanceled();
        inspectWaiters.Clear();

        if (hwndReady.Wait(TimeSpan.FromSeconds(2)) && hwnd != 0)
        {
            PostMessage(hwnd, WmClose, 0, 0);
            if (!staThread.Join(TimeSpan.FromSeconds(3)) && staThreadId != 0)
                PostThreadMessage(staThreadId, WmQuit, 0, 0);
        }
        hwndReady.Dispose();
        lifetime.Dispose();
    }

    private static string ReadEmbeddedAsset(string filename)
    {
        var name = $"HachidoriFfxiv.WebView.Assets.{filename}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Missing embedded WebView asset: {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ------------------------------------------------------------------ Win32 + DirectComposition interop

    private const int GwlExStyle = -20;
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopMost = 0x00000008;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoRedirectionBitmap = 0x00200000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint LwaAlpha = 0x00000002;
    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmQuit = 0x0012;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmNcHitTest = 0x0084;
    private const nint MaNoActivate = 3;
    private const nint HtTransparent = -1;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const uint GwHwndPrev = 3;
    private static readonly nint HwndTop = 0;
    private static readonly nint HwndTopMost = new(-1);
    private static readonly nint HwndNoChange = new(-99);

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(int threadId, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("dcomp.dll")]
    private static extern int DCompositionCreateDevice(nint dxgiDevice, ref Guid iid, out nint dcompositionDevice);
}
