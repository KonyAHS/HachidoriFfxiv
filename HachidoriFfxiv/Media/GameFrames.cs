using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace HachidoriFfxiv.Media;

/// <summary>
/// The game picture without plugin windows: a live copy of the main viewport taken before ImGui
/// renders. Keeps a few seconds of small frames for GIFs and encodes JPEG screenshots on demand.
/// </summary>
public sealed class GameFrames : IDisposable
{
    private static readonly Guid JpegContainer = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");

    private readonly Configuration config;
    private readonly LinkedList<(long Ticks, GifFrame Frame)> frames = new();
    private readonly object gate = new();
    private Task<IDalamudTextureWrap>? viewport;
    private long lastFrameTicks;
    private long retryAfter;
    private bool readingFrame;

    public GameFrames(Configuration config) => this.config = config;

    public string? Error { get; private set; }

    /// <summary>Call from UiBuilder.Draw: starts the viewport copy and samples GIF frames.</summary>
    public void Draw()
    {
        var wanted = config.CaptureScreenshot || config.CaptureGif;
        if (!wanted)
        {
            Release();
            return;
        }

        if (viewport is null && Stopwatch.GetTimestamp() < retryAfter)
            return;
        viewport ??= Plugin.TextureProvider.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
        {
            ViewportId = ImGui.GetMainViewport().ID,
            TakeBeforeImGuiRender = true,
            AutoUpdate = true,
            KeepTransparency = false,
        }, "Hachidori game frame");

        if (viewport.IsFaulted)
        {
            Error = $"Screen capture is unavailable: {viewport.Exception?.GetBaseException().Message}";
            viewport = null;
            retryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
            return;
        }

        if (viewport.IsCompletedSuccessfully)
            Error = null;
        if (!config.CaptureGif || !viewport.IsCompletedSuccessfully || readingFrame)
            return;
        var now = Stopwatch.GetTimestamp();
        var interval = Stopwatch.Frequency / Math.Clamp(config.GifFps, 2, 20);
        if (now - lastFrameTicks < interval)
            return;
        lastFrameTicks = now;
        readingFrame = true;
        _ = SampleAsync(viewport.Result, now);
    }

    private async Task SampleAsync(IDalamudTextureWrap source, long ticks)
    {
        try
        {
            var width = Math.Clamp(config.GifWidth, 160, 960);
            var height = Math.Max(2, (int)Math.Round(width * source.Height / (double)Math.Max(1, source.Width)) & ~1);
            var (spec, data) = await Plugin.TextureReadback.GetRawImageAsync(source, new TextureModificationArgs
            {
                NewWidth = width,
                NewHeight = height,
                MakeOpaque = true,
            }, true).ConfigureAwait(false);
            var frame = await Task.Run(() => ToRgba(spec.Width, spec.Height, spec.Pitch, spec.DxgiFormat, data)).ConfigureAwait(false);
            lock (gate)
            {
                frames.AddLast((ticks, frame));
                var keep = Stopwatch.Frequency * (long)Math.Clamp(config.GifBufferSeconds, 4, 30);
                while (frames.First is { } first && ticks - first.Value.Ticks > keep)
                    frames.RemoveFirst();
            }
        }
        catch (Exception ex)
        {
            Error = $"GIF frames: {ex.GetBaseException().Message}";
        }
        finally
        {
            readingFrame = false;
        }
    }

    /// <summary>Packs a readback into tight RGBA, whatever the swap chain's channel order.</summary>
    private static GifFrame ToRgba(int width, int height, int pitch, int dxgiFormat, byte[] data)
    {
        // 87/91: B8G8R8A8 (UNORM/SRGB); 28/29: R8G8B8A8.
        var bgra = dxgiFormat is 87 or 88 or 91 or 93;
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var src = y * pitch;
            var dst = y * width * 4;
            for (var x = 0; x < width; x++, src += 4, dst += 4)
            {
                rgba[dst] = data[src + (bgra ? 2 : 0)];
                rgba[dst + 1] = data[src + 1];
                rgba[dst + 2] = data[src + (bgra ? 0 : 2)];
                rgba[dst + 3] = 255;
            }
        }

        return new GifFrame(rgba, width, height);
    }

    /// <summary>The buffered frames from <paramref name="fromTicks"/> for at most the GIF length; the latest ones if those are gone.</summary>
    public List<GifFrame> FramesFrom(long fromTicks)
    {
        var length = (long)(Stopwatch.Frequency * Math.Clamp(config.GifSeconds, 1, 10));
        lock (gate)
        {
            var list = frames.Where(f => f.Ticks >= fromTicks && f.Ticks <= fromTicks + length).Select(f => f.Frame).ToList();
            if (list.Count >= 2)
                return list;
            var latest = frames.LastOrDefault().Ticks;
            return frames.Where(f => f.Ticks >= latest - length).Select(f => f.Frame).ToList();
        }
    }

    /// <summary>A JPEG of the current game picture, at most the configured width.</summary>
    public async Task<byte[]> ScreenshotAsync()
    {
        if (viewport is null || !viewport.IsCompletedSuccessfully)
            throw new InvalidOperationException(Error ?? "The game picture is not ready yet.");
        var source = viewport.Result;
        var maxWidth = Math.Clamp(config.ScreenshotMaxWidth, 480, 3840);
        IDalamudTextureWrap? scaled = null;
        try
        {
            if (source.Width > maxWidth)
            {
                scaled = await Plugin.TextureProvider.CreateFromExistingTextureAsync(source, new TextureModificationArgs
                {
                    NewWidth = maxWidth,
                    NewHeight = (int)Math.Round(maxWidth * source.Height / (double)source.Width),
                    MakeOpaque = true,
                }, true, "Hachidori screenshot").ConfigureAwait(false);
            }

            using var stream = new MemoryStream();
            await Plugin.TextureReadback.SaveToStreamAsync(scaled ?? source, JpegContainer, stream,
                new Dictionary<string, object> { ["ImageQuality"] = Math.Clamp(config.ScreenshotQuality, 0.4f, 1f) },
                true, true).ConfigureAwait(false);
            return stream.ToArray();
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    private void Release()
    {
        if (viewport is { IsCompletedSuccessfully: true })
            viewport.Result.Dispose();
        viewport = null;
        lock (gate)
            frames.Clear();
    }

    public void Dispose() => Release();
}
