using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
        // An SVG image, drawn as shapes: Windows' decoders cannot read SVG.
        public SvgImage? Vector;
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
            else if (slot.Vector is { } vector)
            {
                var size = image.Size(width, ImGui.GetFontSize(), (int)MathF.Ceiling(vector.Width), (int)MathF.Ceiling(vector.Height));
                var origin = ImGui.GetCursorScreenPos();
                ImGui.Dummy(size);
                DrawVector(vector, origin, size);
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
            if (SvgImage.LooksLikeSvg(bytes))
            {
                var vector = SvgImage.Parse(bytes);
                lock (slot.Gate)
                {
                    if (!slot.Disposed) slot.Vector = vector;
                }
                return;
            }
            var texture = await DalamudServices.TextureProvider.CreateFromImageAsync(bytes, "Hachidori for FFXIV dictionary image", slot.Cancel.Token).ConfigureAwait(false);
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

    // The shapes of an SVG image, laid out for this frame's size.
    private static void DrawVector(SvgImage vector, Vector2 origin, Vector2 size)
    {
        var draw = ImGui.GetWindowDrawList();
        var text = ImGui.GetColorU32(ImGuiCol.Text);
        uint Resolve(uint color) => color == SvgImage.CurrentColor ? text : color;
        foreach (var primitive in vector.Layout(origin, size))
        {
            switch (primitive)
            {
                case SvgShape shape:
                    if (shape.Fill is { } fill)
                    {
                        var c = Resolve(fill);
                        var t = shape.Triangles;
                        for (var i = 0; i + 2 < t.Count; i += 3)
                            draw.AddTriangleFilled(shape.Points[t[i]], shape.Points[t[i + 1]], shape.Points[t[i + 2]], c);
                    }
                    if (shape.Stroke is { } stroke)
                    {
                        var c = Resolve(stroke);
                        var points = shape.Points;
                        var count = shape.Closed ? points.Count : points.Count - 1;
                        for (var i = 0; i < count; i++)
                            draw.AddLine(points[i], points[(i + 1) % points.Count], c, shape.StrokeWidth);
                        // Round the joins of thick strokes (KanjiVG's stroke-linejoin: round).
                        if (shape.StrokeWidth >= 2.5f)
                            foreach (var point in points) draw.AddCircleFilled(point, shape.StrokeWidth / 2, c);
                    }
                    break;
                case SvgText label when label.Fill is { } textFill:
                {
                    var font = ImGui.GetFont();
                    var measured = ImGui.CalcTextSize(label.Text) * (label.Size / Math.Max(1, ImGui.GetFontSize()));
                    var x = label.Anchor switch { "middle" => label.Baseline.X - measured.X / 2, "end" => label.Baseline.X - measured.X, _ => label.Baseline.X };
                    // ImGui places text by its top; an SVG text position is its baseline.
                    var top = label.Baseline.Y - label.Size * 0.8f;
                    draw.AddText(font, label.Size, new Vector2(x, top), Resolve(textFill), label.Text);
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var slot in cache.Values) slot.Dispose();
        cache.Clear();
        order.Clear();
    }
}
