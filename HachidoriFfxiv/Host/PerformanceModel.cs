using System;
using System.Collections.Generic;

namespace HachidoriFfxiv;

public enum PerformancePreset
{
    Low,
    Balanced,
    High,
    Custom,
}

public enum SetupStyle
{
    Unset,
    KeepGsm,
    Lightweight,
    FullMedia,
}

public enum DictionarySource
{
    Auto,
    BuiltIn,
    HachidoriBrowser,
}

/// <summary>
/// Built-in Anki template used when mining standalone offline (without Chrome / Hachidori linked),
/// or when the user chooses to override Hachidori's template with a direct AnkiConnect mapping.
/// Pure POCO with zero Dalamud coupling so Offline/Anki layers and tests share it directly.
/// </summary>
[Serializable]
public sealed class StandaloneAnkiConfig
{
    public bool PreferStandalone { get; set; } = false;
    public string Deck { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string DuplicateBehavior { get; set; } = "prevent";
    public string DuplicateScope { get; set; } = "collection";
    public string Tags { get; set; } = "hachidori ffxiv";
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Pure parameter values for a premade performance preset.</summary>
public readonly record struct PerformancePresetValues(
    bool UseWebViewPopup,
    bool CaptureGif,
    bool EagerEncodeClips,
    int VoiceBufferSeconds,
    int LookupCacheSize,
    int SegmentCacheSize,
    int AudioCacheSize,
    bool CacheLineScreenshots,
    int MaxCachedScreenshots,
    bool CacheReplayAudio,
    int MaxCachedReplayAudio,
    int MaxCachedClips,
    int MaxConcurrentEncodes,
    int HistorySize,
    int GifWidth,
    int GifFps,
    int GifSeconds);

/// <summary>Estimated RAM/VRAM cost of the current configuration, in megabytes.</summary>
public readonly record struct MemoryEstimate(
    int WebViewMb,
    int VoiceBufferMb,
    int AnimationBufferMb,
    int LineCachesMb,
    int LookupCachesMb)
{
    public int PluginProcessMb => VoiceBufferMb + AnimationBufferMb + LineCachesMb + LookupCachesMb;
    public int TotalMb => WebViewMb + PluginProcessMb;
}

/// <summary>
/// Pure, Dalamud-free definitions for premade performance presets and live memory estimation.
/// </summary>
public static class PerformanceModel
{
    public static PerformancePresetValues ValuesFor(PerformancePreset preset, bool supportsWebView2) => preset switch
    {
        PerformancePreset.Low => new(
            UseWebViewPopup: false,
            CaptureGif: false,
            EagerEncodeClips: false,
            VoiceBufferSeconds: 45,
            LookupCacheSize: 128,
            SegmentCacheSize: 64,
            AudioCacheSize: 16,
            CacheLineScreenshots: true,
            MaxCachedScreenshots: 8,
            CacheReplayAudio: true,
            MaxCachedReplayAudio: 8,
            MaxCachedClips: 2,
            MaxConcurrentEncodes: 1,
            HistorySize: 50,
            GifWidth: 480,
            GifFps: 8,
            GifSeconds: 2),

        PerformancePreset.High => new(
            UseWebViewPopup: supportsWebView2,
            CaptureGif: true,
            EagerEncodeClips: true,
            VoiceBufferSeconds: 240,
            LookupCacheSize: 1024,
            SegmentCacheSize: 256,
            AudioCacheSize: 128,
            CacheLineScreenshots: true,
            MaxCachedScreenshots: 60,
            CacheReplayAudio: true,
            MaxCachedReplayAudio: 60,
            MaxCachedClips: 20,
            MaxConcurrentEncodes: 2,
            HistorySize: 250,
            GifWidth: 960,
            GifFps: 15,
            GifSeconds: 4),

        _ => new(
            UseWebViewPopup: supportsWebView2,
            CaptureGif: true,
            EagerEncodeClips: false,
            VoiceBufferSeconds: 120,
            LookupCacheSize: 512,
            SegmentCacheSize: 128,
            AudioCacheSize: 64,
            CacheLineScreenshots: true,
            MaxCachedScreenshots: 24,
            CacheReplayAudio: true,
            MaxCachedReplayAudio: 24,
            MaxCachedClips: 4,
            MaxConcurrentEncodes: 1,
            HistorySize: 100,
            GifWidth: 720,
            GifFps: 12,
            GifSeconds: 3),
    };

    public static PerformancePreset Detect(
        bool useWebViewPopup,
        bool eagerEncodeClips,
        int voiceBufferSeconds,
        int lookupCacheSize,
        int segmentCacheSize,
        int audioCacheSize,
        int maxCachedScreenshots,
        int maxCachedReplayAudio,
        int maxCachedClips)
    {
        if (!useWebViewPopup
            && !eagerEncodeClips
            && voiceBufferSeconds <= 60
            && lookupCacheSize <= 128
            && maxCachedScreenshots <= 10)
            return PerformancePreset.Low;

        if (eagerEncodeClips
            && voiceBufferSeconds >= 200
            && lookupCacheSize >= 1024
            && maxCachedClips >= 16)
            return PerformancePreset.High;

        if (!eagerEncodeClips
            && voiceBufferSeconds == 120
            && lookupCacheSize == 512
            && segmentCacheSize == 128
            && audioCacheSize == 64
            && maxCachedScreenshots == 24
            && maxCachedReplayAudio == 24
            && maxCachedClips == 4)
            return PerformancePreset.Balanced;

        return PerformancePreset.Custom;
    }

    public static MemoryEstimate Estimate(
        bool platformSupportsWebView2,
        bool useWebViewPopup,
        bool followTheme,
        bool recordVoiceLines,
        int voiceBufferSeconds,
        bool captureGif,
        bool eagerEncodeClips,
        int gifWidth,
        int gifFps,
        int gifSeconds,
        int maxCachedClips,
        bool captureScreenshot,
        bool cacheLineScreenshots,
        int maxCachedScreenshots,
        bool cacheReplayAudio,
        int maxCachedReplayAudio,
        int lookupCacheSize,
        int segmentCacheSize,
        int audioCacheSize)
    {
        var webViewMb = platformSupportsWebView2 && useWebViewPopup && followTheme ? 300 : 0;
        var voiceMb = recordVoiceLines
            ? (int)Math.Ceiling(Math.Clamp(voiceBufferSeconds, 30, 300) * 48000.0 * 4.0 / (1024.0 * 1024.0))
            : 0;

        var animMb = 0;
        if (captureGif)
        {
            var width = Math.Clamp(gifWidth, 160, 1280);
            var height = Math.Max(2, (int)Math.Round(width * 9.0 / 16.0));
            var frameBytes = width * (double)height * 4.0;
            var fps = Math.Clamp(gifFps, 2, 24);
            var secs = Math.Clamp(gifSeconds, 1, 10);
            var ringFrames = (secs + 1) * fps;
            var cachedRawClips = eagerEncodeClips ? 0 : Math.Clamp(maxCachedClips, 0, 40);
            var rawClipFrames = cachedRawClips * secs * fps;
            var encodedClipMb = eagerEncodeClips ? Math.Clamp(maxCachedClips, 0, 40) * 0.4 : 0;
            animMb = (int)Math.Ceiling(((ringFrames + rawClipFrames) * frameBytes) / (1024.0 * 1024.0) + encodedClipMb);
        }

        var shotsMb = captureScreenshot && cacheLineScreenshots
            ? Math.Clamp(maxCachedScreenshots, 0, 100) * 0.25
            : 0;
        var replayMb = cacheReplayAudio
            ? Math.Clamp(maxCachedReplayAudio, 0, 100) * 0.08
            : 0;
        var lineCachesMb = (int)Math.Ceiling(shotsMb + replayMb);

        var lookupMb = (int)Math.Ceiling(
            Math.Clamp(lookupCacheSize, 0, 2048) * 0.015
            + Math.Clamp(segmentCacheSize, 0, 512) * 0.005
            + Math.Clamp(audioCacheSize, 0, 256) * 0.06);

        return new MemoryEstimate(webViewMb, voiceMb, animMb, lineCachesMb, lookupMb);
    }
}
