using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.Ui;

/// <summary>Captures NPC pictures and manages the single "FFXIV NPC pictures" host dictionary.</summary>
public sealed class NpcImageCapture : Window, IDisposable
{
    // WIC's standard PNG container GUID; no extra image encoding dependency.
    private static readonly Guid PngFormat = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");

    private readonly HachidoriService service;
    private readonly NpcPictureLibrary library;
    private readonly CancellationTokenSource lifetime = new();
    private Task<IDalamudTextureWrap>? capture;
    private IDalamudTextureWrap? frame;
    private Task<NpcSaveResult>? save;
    private string saveVerb = "";
    private Func<bool, Task<NpcSaveResult>>? conflictRetry;
    private string? conflict;
    private IReadOnlyList<(string Expression, string Reading)> saved = [];
    private bool savedStale = true;
    private string expression = "";
    private string reading = "";
    private string? message;
    private Vector2? dragStart;
    private bool disposed;

    public NpcImageCapture(HachidoriService service) : base("NPC pictures##hachidori-ffxiv-npc-picture")
    {
        this.service = service;
        library = new NpcPictureLibrary(Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "npc-pictures"),
            service, ShrinkAsync);
        Size = new Vector2(800, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public bool Busy => capture is not null || save is { IsCompleted: false };
    public bool Available => service.Link.Connected && service.Link.HostCapabilities.Contains(NpcImageDictionary.Capability);

    /// <summary>Opens the picture list without capturing.</summary>
    public void Open()
    {
        IsOpen = true;
        savedStale = true;
        if (Busy) return;
        expression = reading = "";
        message = null;
    }

    public void Start(TermInfo term)
    {
        if (Busy || disposed) return;
        IsOpen = true;
        savedStale = true;
        expression = term.Expression;
        reading = term.Reading;
        ClearConflict();
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
            savedStale = true;
            var error = finished.Exception?.GetBaseException();
            if (finished.IsCompletedSuccessfully)
            {
                var done = finished.Result;
                service.ClearCache();
                message = $"{saveVerb} Hachidori now has {done.Pictures} NPC picture(s) in one dictionary.";
                if (done.Merged > 0) message += $" Merged {done.Merged} old one-picture dictionar{(done.Merged == 1 ? "y" : "ies")}.";
                if (done.LegacyRemaining > 0) message += $" {done.LegacyRemaining} old dictionar{(done.LegacyRemaining == 1 ? "y" : "ies")} could not be merged; remove them in Hachidori if their pictures are already here.";
                DiagnosticLog.Write("image", $"NPC picture dictionary saved: pictures={done.Pictures}, merged={done.Merged}, legacyLeft={done.LegacyRemaining}.");
                frame?.Dispose();
                frame = null;
                ClearConflict();
            }
            else if (error is NpcPictureConflictException)
            {
                conflict = error.Message;
                message = null;
            }
            else message = "Could not confirm the save: " + (error?.Message ?? "Cancelled.") + " Check Hachidori before trying again.";
        }
        if (!IsOpen && save is null)
        {
            frame?.Dispose();
            frame = null;
            dragStart = null;
            ClearConflict();
        }
    }

    private void ClearConflict()
    {
        conflict = null;
        conflictRetry = null;
    }

    private void Run(string verb, Func<bool, Task<NpcSaveResult>> operation, bool replaceForeign = false)
    {
        if (save is not null) return;
        saveVerb = verb;
        message = null;
        conflictRetry = operation;
        conflict = null;
        save = operation(replaceForeign);
    }

    public override void Draw()
    {
        if (capture is not null) { ImGui.TextDisabled("Capturing game frame…"); return; }
        if (expression.Length > 0) ImGui.TextWrapped($"Picture for {expression}");
        if (message is not null) ImGui.TextWrapped(message);
        if (save is not null) { ImGui.TextDisabled("Saving pictures in Hachidori…"); }
        else if (conflict is not null && conflictRetry is { } retry)
        {
            ImGui.TextWrapped(conflict);
            if (ImGui.Button("Replace it")) Run(saveVerb, retry, replaceForeign: true);
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ClearConflict();
            return;
        }
        if (frame is null) { DrawLibrary(); return; }
        if (save is not null) return;

        ImGui.TextWrapped("Drag around the NPC. Release to save. Close this window to cancel.");
        var available = ImGui.GetContentRegionAvail();
        var scale = Math.Min(available.X / frame.Width, available.Y / frame.Height);
        if (scale <= 0) return;
        var size = new Vector2(frame.Width, frame.Height) * scale;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Image(frame.Handle, size);
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
        var source = frame;
        var (term, termReading) = (expression, reading);
        var cropped = CropAsync(source, uv0, uv1, term, termReading);
        Run("Saved.", async replace => await library.AddAsync(await cropped.ConfigureAwait(false), replace, lifetime.Token).ConfigureAwait(false));
    }

    private void DrawLibrary()
    {
        if (savedStale)
        {
            savedStale = false;
            try { saved = library.List(); }
            catch (Exception ex) { saved = []; message = ex.Message; }
        }
        var busy = save is not null || !Available;
        ImGui.Separator();
        ImGui.TextWrapped($"{saved.Count} picture(s) in the \"{NpcImageDictionary.Title}\" dictionary. "
            + "Use the camera button in a popup to add or replace one.");
        if (!Available) ImGui.TextDisabled("Connect to a Hachidori host with dictionary upload support to change pictures.");

        var legacy = Available ? library.LegacyDictionaries.Count : 0;
        if (legacy > 0)
        {
            ImGui.TextWrapped($"{legacy} old one-picture dictionar{(legacy == 1 ? "y is" : "ies are")} still installed. "
                + "They are merged automatically on your next save, or now:");
            using (ImRaii.Disabled(busy))
                if (ImGui.Button("Merge into one dictionary"))
                    Run("Merged.", replace => library.MergeLegacyAsync(replace, lifetime.Token));
        }

        using var list = ImRaii.Child("##npc-pictures", ImGui.GetContentRegionAvail(), true);
        if (!list) return;
        for (var i = 0; i < saved.Count; i++)
        {
            var (exp, read) = saved[i];
            using var id = ImRaii.PushId(i);
            using (ImRaii.Disabled(busy))
                if (ImGui.SmallButton("Remove"))
                    Run($"Removed {exp}.", replace => library.RemoveAsync(exp, read, replace, lifetime.Token));
            ImGui.SameLine();
            ImGui.TextUnformatted(read.Length > 0 && read != exp ? $"{exp}  【{read}】" : exp);
        }
    }

    private async Task<NpcPicture> CropAsync(IDalamudTextureWrap source, Vector2 uv0, Vector2 uv1, string term, string termReading)
    {
        var (width, height) = NpcImageDictionary.Fit(
            Math.Max(1, (int)Math.Round((uv1.X - uv0.X) * source.Width)), Math.Max(1, (int)Math.Round((uv1.Y - uv0.Y) * source.Height)));
        using var crop = await Plugin.TextureProvider.CreateFromExistingTextureAsync(source,
            new TextureModificationArgs { Uv0 = uv0, Uv1 = uv1, NewWidth = width, NewHeight = height }, leaveWrapOpen: true,
            debugName: "Hachidori for FFXIV NPC crop", cancellationToken: lifetime.Token).ConfigureAwait(false);
        return new NpcPicture(term, termReading, await EncodeAsync(crop, lifetime.Token).ConfigureAwait(false), crop.Width, crop.Height);
    }

    /// <summary>Older pictures were stored at full crop size; shrink them while merging.</summary>
    private static async Task<NpcPicture> ShrinkAsync(NpcPicture picture, CancellationToken ct)
    {
        var (width, height) = NpcImageDictionary.Fit(picture.Width, picture.Height);
        if (width == picture.Width && height == picture.Height) return picture;
        using var original = await Plugin.TextureProvider.CreateFromImageAsync(picture.Png, "Hachidori for FFXIV NPC merge", ct).ConfigureAwait(false);
        using var resized = await Plugin.TextureProvider.CreateFromExistingTextureAsync(original,
            new TextureModificationArgs { NewWidth = width, NewHeight = height }, leaveWrapOpen: true,
            debugName: "Hachidori for FFXIV NPC merge resize", cancellationToken: ct).ConfigureAwait(false);
        return picture with { Png = await EncodeAsync(resized, ct).ConfigureAwait(false), Width = resized.Width, Height = resized.Height };
    }

    private static async Task<byte[]> EncodeAsync(IDalamudTextureWrap texture, CancellationToken ct)
    {
        using var png = new MemoryStream();
        await Plugin.TextureReadback.SaveToStreamAsync(texture, PngFormat, png,
            leaveWrapOpen: true, leaveStreamOpen: true, cancellationToken: ct).ConfigureAwait(false);
        return png.ToArray();
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
