using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Media;

public enum AnimationFormat
{
    /// <summary>Animated AVIF (AV1) through FFmpeg: the smallest files at the best quality.</summary>
    Avif,
    /// <summary>Animated lossy WebP through FFmpeg.</summary>
    Webp,
    /// <summary>GIF, built in: plays everywhere, largest files, 256 colours.</summary>
    Gif,
}

/// <summary>Encodes a line's frames as AVIF or WebP (FFmpeg) or GIF (built in), falling back down that list.</summary>
public static class AnimationEncoder
{
    /// <summary>Returns the bytes, the file extension and a note when a fallback format was used.</summary>
    public static async Task<(byte[] Data, string Extension, string? Note)> EncodeAsync(
        IReadOnlyList<GifFrame> frames, int fps, AnimationFormat format, string? ffmpeg, int avifCrf, int webpQuality, CancellationToken ct = default)
    {
        if (frames.Count == 0)
            throw new ArgumentException("No frames to encode.", nameof(frames));
        var caps = ffmpeg is null ? default : Ffmpeg.Capabilities(ffmpeg);
        string? note = null;
        if (format == AnimationFormat.Avif)
        {
            if (caps.Av1 is { } av1)
            {
                try
                {
                    return (await RunAsync(frames, fps, ffmpeg!, AvifArguments(av1, avifCrf), "avif", ct).ConfigureAwait(false), "avif", null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    note = $"AVIF failed ({ex.Message})";
                }
            }
            else
            {
                note = ffmpeg is null ? "FFmpeg not found" : "this FFmpeg has no AV1 encoder";
            }

            format = AnimationFormat.Webp;
        }

        if (format == AnimationFormat.Webp)
        {
            if (caps.Webp)
            {
                try
                {
                    var webp = await RunAsync(frames, fps, ffmpeg!,
                        ["-c:v", "libwebp_anim", "-quality", Math.Clamp(webpQuality, 30, 100).ToString(), "-lossless", "0", "-compression_level", "4"],
                        "webp", ct).ConfigureAwait(false);
                    return (webp, "webp", note is null ? null : $"{note}, saved WebP");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    note = $"{(note is null ? string.Empty : note + "; ")}WebP failed ({ex.Message})";
                }
            }
            else
            {
                note ??= ffmpeg is null ? "FFmpeg not found" : "this FFmpeg has no WebP encoder";
            }
        }

        var gif = GifEncoder.Encode(frames, 1000 / Math.Max(1, fps));
        return (gif, "gif", note is null ? null : $"{note}, saved GIF");
    }

    private static string[] AvifArguments(string encoder, int crf) => encoder switch
    {
        "libsvtav1" => ["-c:v", "libsvtav1", "-preset", "8", "-crf", crf.ToString()],
        "libaom-av1" => ["-c:v", "libaom-av1", "-cpu-used", "6", "-row-mt", "1", "-crf", crf.ToString(), "-b:v", "0"],
        _ => ["-c:v", encoder, "-qp", Math.Clamp(crf * 4, 0, 255).ToString()],
    };

    /// <summary>Raw RGBA frames piped into FFmpeg, written to a temporary file of the given container.</summary>
    private static async Task<byte[]> RunAsync(IReadOnlyList<GifFrame> frames, int fps, string ffmpeg, string[] codec, string container, CancellationToken ct)
    {
        var width = frames[0].Width & ~1;
        var height = frames[0].Height & ~1;
        var output = Path.Combine(Path.GetTempPath(), $"hachidori-ffxiv-{Guid.NewGuid():N}.{container}");
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{width}x{height}", "-framerate", fps.ToString(), "-i", "-",
            .. codec,
            "-pix_fmt", "yuv420p", "-loop", "0", "-f", container, output,
        ];
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg did not start");
        var errors = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            using (var input = process.StandardInput.BaseStream)
            {
                var row = width * 4;
                foreach (var frame in frames)
                {
                    for (var y = 0; y < height; y++)
                        await input.WriteAsync(frame.Rgba.AsMemory(y * frame.Width * 4, row), ct).ConfigureAwait(false);
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                var message = (await errors.ConfigureAwait(false)).Split('\n')
                    .LastOrDefault(l => l.Trim().Length > 0 && !l.StartsWith("Svt[", StringComparison.Ordinal)) ?? "unknown error";
                throw new InvalidOperationException($"FFmpeg exited with {process.ExitCode}: {message.Trim()}");
            }

            return await File.ReadAllBytesAsync(output, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch { /* already gone */ }
            }

            try { File.Delete(output); } catch { /* best effort */ }
        }
    }
}

/// <summary>Finds FFmpeg (the configured path, PATH, GSM's copy or WinGet's) and what it can encode.</summary>
public static class Ffmpeg
{
    public readonly record struct Caps(string? Av1, bool Webp)
    {
        public bool Any => Av1 is not null || Webp;
    }

    private static readonly Dictionary<string, Caps> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first FFmpeg that can write AVIF, else the first that can write WebP, else null.</summary>
    public static string? Find(string configured)
    {
        string? webpOnly = null;
        foreach (var candidate in Candidates(configured))
        {
            if (!File.Exists(candidate))
                continue;
            var caps = Capabilities(candidate);
            if (caps.Av1 is not null)
                return candidate;
            if (caps.Webp)
                webpOnly ??= candidate;
        }

        return webpOnly;
    }

    private static IEnumerable<string> Candidates(string configured)
    {
        if (configured.Trim().Length > 0)
        {
            yield return configured.Trim().Trim('"');
            yield break;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir.Trim('"'), "ffmpeg.exe");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(appData, "GameSentenceMiner", "ffmpeg", "ffmpeg.exe");
        var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            foreach (var package in Directory.EnumerateDirectories(winget, "*FFmpeg*"))
            {
                foreach (var exe in Directory.EnumerateFiles(package, "ffmpeg.exe", SearchOption.AllDirectories))
                    yield return exe;
            }
        }
    }

    /// <summary>The software AV1 encoder (with the AVIF muxer) and animated WebP support of an FFmpeg. Cached per path.</summary>
    public static Caps Capabilities(string ffmpeg)
    {
        lock (Known)
        {
            if (Known.TryGetValue(ffmpeg, out var cached))
                return cached;
        }

        Caps caps = default;
        try
        {
            var encoders = Run(ffmpeg, "-hide_banner -encoders");
            var muxers = Run(ffmpeg, "-hide_banner -muxers");
            var av1 = muxers.Contains(" avif ", StringComparison.Ordinal)
                ? new[] { "libsvtav1", "libaom-av1", "librav1e" }.FirstOrDefault(e => encoders.Contains($" {e} ", StringComparison.Ordinal))
                : null;
            var webp = encoders.Contains(" libwebp_anim ", StringComparison.Ordinal) && muxers.Contains(" webp ", StringComparison.Ordinal);
            caps = new Caps(av1, webp);
        }
        catch
        {
            caps = default;
        }

        lock (Known)
            Known[ffmpeg] = caps;
        return caps;
    }

    private static string Run(string ffmpeg, string arguments)
    {
        var start = new ProcessStartInfo(ffmpeg, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg did not start");
        _ = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        return output;
    }
}
