using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.Ui;

public sealed class NpcImageCapture : Window, IDisposable
{
    private readonly HachidoriService service;
    private readonly CancellationTokenSource lifetime = new();
    private Task<IDalamudTextureWrap>? capture;
    private IDalamudTextureWrap? frame;
    private Task? save;
    private string expression = "";
    private string reading = "";
    private string? message;
    private Vector2? dragStart;
    private bool disposed;

    public NpcImageCapture(HachidoriService service) : base("NPC picture##hachidori-ffxiv-npc-picture")
    {
        this.service = service;
        Size = new Vector2(800, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public bool Busy => capture is not null || save is { IsCompleted: false };
    public bool Available => service.Link.Connected && service.Link.HostCapabilities.Contains(NpcImageDictionary.Capability);

    public void Start(TermInfo term)
    {
        if (Busy || disposed) return;
        IsOpen = true;
        if (!Available)
        {
            message = "Connect to a Hachidori host with dictionary upload support (linked-import-v1).";
            return;
        }
        frame?.Dispose();
        frame = null;
        save = null;
        message = null;
        dragStart = null;
        expression = term.Expression;
        reading = term.Reading;
        try
        {
            capture = Plugin.TextureProvider.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
            {
                ViewportId = ImGui.GetMainViewport().ID, AutoUpdate = false, TakeBeforeImGuiRender = true,
            }, "Hachidori for FFXIV NPC capture", lifetime.Token);
        }
        catch (Exception ex) { message = "Could not capture the game frame: " + ex.Message; }
    }

    // Poll even when closed: closing during GPU capture must not leave the camera
    // permanently busy, and completed operations must still release their textures.
    public void PollOperations()
    {
        if (capture is { IsCompleted: true } result)
        {
            capture = null;
            if (result.IsCompletedSuccessfully)
            {
                if (IsOpen) frame = result.Result;
                else result.Result.Dispose();
            }
            else message = result.Exception?.GetBaseException().Message ?? "Capture cancelled.";
        }
        if (save is { IsCompleted: true } finished)
        {
            save = null;
            if (finished.IsCompletedSuccessfully)
            {
                message = "Saved in Hachidori. Look up the name again to see the picture.";
                frame?.Dispose();
                frame = null;
            }
            else message = "Could not confirm the save: " + (finished.Exception?.GetBaseException().Message ?? "Cancelled.")
                + " Check Hachidori before trying again.";
        }
        if (!IsOpen && save is null)
        {
            frame?.Dispose();
            frame = null;
            dragStart = null;
        }
    }

    public override void Draw()
    {
        ImGui.TextWrapped(expression.Length > 0 ? $"Picture for {expression}" : "NPC picture");
        if (capture is not null) { ImGui.TextDisabled("Capturing game frame…"); return; }
        if (message is not null) ImGui.TextWrapped(message);
        if (frame is null) return;
        ImGui.TextWrapped(save is not null ? "Saving picture in Hachidori…" : "Drag around the NPC. Release to save. Close this window to cancel.");
        var available = ImGui.GetContentRegionAvail();
        var scale = Math.Min(available.X / frame.Width, available.Y / frame.Height);
        if (scale <= 0) return;
        var size = new Vector2(frame.Width, frame.Height) * scale;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Image(frame.Handle, size);
        if (save is not null) return;
        Vector2 MouseUv() => Vector2.Clamp((ImGui.GetMousePos() - origin) / size, Vector2.Zero, Vector2.One);
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) dragStart = MouseUv();
        if (dragStart is not { } start) return;
        var end = MouseUv();
        var uv0 = Vector2.Min(start, end);
        var uv1 = Vector2.Max(start, end);
        ImGui.GetWindowDrawList().AddRect(origin + uv0 * size, origin + uv1 * size, 0xFF00FFFF, 0, ImDrawFlags.None, 2);
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;
        dragStart = null;
        if ((uv1.X - uv0.X) * frame.Width < 16 || (uv1.Y - uv0.Y) * frame.Height < 16) return;
        message = null;
        save = SaveAsync(frame, uv0, uv1, expression, reading);
    }

    private async Task SaveAsync(IDalamudTextureWrap source, Vector2 uv0, Vector2 uv1, string term, string termReading)
    {
        using var crop = await Plugin.TextureProvider.CreateFromExistingTextureAsync(source,
            new TextureModificationArgs { Uv0 = uv0, Uv1 = uv1 }, leaveWrapOpen: true,
            debugName: "Hachidori for FFXIV NPC crop", cancellationToken: lifetime.Token).ConfigureAwait(false);
        using var png = new MemoryStream();
        // WIC's standard PNG container GUID; no extra image encoding dependency.
        await Plugin.TextureReadback.SaveToStreamAsync(crop, new Guid("1b7cfaf4-713f-473c-bbcd-6137425faeaf"),
            png, leaveWrapOpen: true, leaveStreamOpen: true, cancellationToken: lifetime.Token).ConfigureAwait(false);
        var archive = NpcImageDictionary.Build(term, termReading, png.ToArray(), crop.Width, crop.Height);
        await NpcImageDictionary.UploadAsync(archive,
            (type, payload, ct) => service.Link.RequestAsync(NpcImageDictionary.Target, type, payload, ct), lifetime.Token).ConfigureAwait(false);
        service.ClearCache();
        DiagnosticLog.Write("image", "Personal NPC image imported on the Hachidori host.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        var pendingCapture = capture;
        var pendingSave = save;
        var ownedFrame = frame;
        frame = null;
        _ = ReleaseAsync();
        async Task ReleaseAsync()
        {
            // A GPU operation must finish before its input texture is released.
            try { if (pendingSave is not null) await pendingSave.ConfigureAwait(false); } catch { }
            ownedFrame?.Dispose();
            try { if (pendingCapture is not null) (await pendingCapture.ConfigureAwait(false)).Dispose(); } catch { }
            lifetime.Dispose();
        }
    }
}
