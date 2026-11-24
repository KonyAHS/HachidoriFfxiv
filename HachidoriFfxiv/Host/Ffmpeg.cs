using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HachidoriFfxiv;

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

    /// <summary>The first existing FFmpeg binary (even without AV1/WebP encoders), e.g. for audio fallback.</summary>
    public static string? FindAny(string configured = "")
    {
        foreach (var candidate in Candidates(configured))
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { /* ignore invalid paths */ }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string configured)
    {
        if (configured.Trim().Length > 0)
        {
            yield return configured.Trim().Trim('"');
            yield break;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = dir.Trim('"');
            if (clean.Length == 0) continue;
            yield return Path.Combine(clean, "ffmpeg.exe");
            if (PlatformInfo.Current.IsWine)
                yield return Path.Combine(clean, "ffmpeg");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (appData.Length > 0)
            yield return Path.Combine(appData, "GameSentenceMiner", "ffmpeg", "ffmpeg.exe");
        if (local.Length > 0)
        {
            var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(winget))
            {
                IEnumerable<string> packages;
                try { packages = Directory.EnumerateDirectories(winget, "*FFmpeg*"); }
                catch { packages = []; }
                foreach (var package in packages)
                {
                    IEnumerable<string> exes;
                    try { exes = Directory.EnumerateFiles(package, "ffmpeg.exe", SearchOption.AllDirectories); }
                    catch { exes = []; }
                    foreach (var exe in exes)
                        yield return exe;
                }
            }
        }

        if (PlatformInfo.Current.IsWine)
        {
            // Host Linux / SteamOS / Flatpak / macOS FFmpeg via Wine's Z:\ root mount.
            yield return @"Z:\usr\bin\ffmpeg";
            yield return @"Z:\usr\local\bin\ffmpeg";
            yield return @"Z:\opt\homebrew\bin\ffmpeg";
            yield return @"Z:\app\bin\ffmpeg";
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
