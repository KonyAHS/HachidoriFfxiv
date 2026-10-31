using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Ui;

public sealed class DictionaryImages : IDisposable
{
    private sealed class Slot : IDisposable
    {
        public readonly CancellationTokenSource Cancel = new();
        public readonly object Gate = new();
        public IDalamudTextureWrap? Texture;
        public string? Error;
        public bool Disposed;
        public void Dispose()
        {
            lock (Gate)
            {
                if (Disposed) return;
                Disposed = true;
                Cancel.Cancel();
                Texture?.Dispose();
                Texture = null;
            }
        }
    }

    private readonly HachidoriService service;
    private readonly Dictionary<string, Slot> cache = new();
    private readonly Queue<string> order = new();
    public DictionaryImages(HachidoriService service) => this.service = service;

    public void Draw(DictionaryImage image, long generation, float width)
    {
        var key = $"{generation}\u0001{image.Dictionary}\u0001{image.Path}";
        if (!cache.TryGetValue(key, out var slot))
        {
            slot = new Slot();
            cache.Add(key, slot);
            order.Enqueue(key);
            _ = LoadAsync(slot, image, generation);
            while (cache.Count > 64 && order.TryDequeue(out var old))
                if (cache.Remove(old, out var removed)) removed.Dispose();
        }
        lock (slot.Gate)
        {
            if (slot.Texture is { } texture)
            {
                ImGui.Image(texture.Handle, image.Size(width, ImGui.GetFontSize(), texture.Width, texture.Height));
                if (image.Label.Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(image.Label);
            }
            else
            {
                ImGui.TextDisabled(slot.Error is null ? "Loading image…" : $"Image unavailable: {image.Label}");
                if (slot.Error is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(slot.Error);
            }
        }
    }

    private async Task LoadAsync(Slot slot, DictionaryImage image, long generation)
    {
        try
        {
            var bytes = await service.GetImageAsync(image.Dictionary, image.Path, generation, slot.Cancel.Token).ConfigureAwait(false);
            if (bytes is null) throw new HachidoriException("Image was not found in the dictionary.");
            var texture = await Plugin.TextureProvider.CreateFromImageAsync(bytes, "Hachidori for FFXIV dictionary image", slot.Cancel.Token).ConfigureAwait(false);
            lock (slot.Gate)
            {
                if (slot.Disposed) texture.Dispose();
                else slot.Texture = texture;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (slot.Gate) slot.Error = ex.Message;
            DiagnosticLog.Write("image", ex.ToString());
        }
    }

    public void Dispose()
    {
        foreach (var slot in cache.Values) slot.Dispose();
        cache.Clear();
        order.Clear();
    }
}
