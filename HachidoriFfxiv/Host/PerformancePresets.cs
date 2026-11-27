namespace HachidoriFfxiv;

/// <summary>Applies <see cref="PerformanceModel"/> presets and setup styles onto <see cref="Configuration"/>.</summary>
public static class PerformancePresets
{
    public static void Apply(Configuration config, PerformancePreset preset)
    {
        config.PerformancePreset = preset;
        if (preset == PerformancePreset.Custom)
            return;

        var v = PerformanceModel.ValuesFor(preset, PlatformInfo.Current.SupportsWebView2Overlay);
        config.UseWebViewPopup = v.UseWebViewPopup;
        if (preset == PerformancePreset.Low)
            config.CaptureGif = v.CaptureGif;
        config.EagerEncodeClips = v.EagerEncodeClips;
        config.VoiceBufferSeconds = v.VoiceBufferSeconds;
        config.LookupCacheSize = v.LookupCacheSize;
        config.SegmentCacheSize = v.SegmentCacheSize;
        config.AudioCacheSize = v.AudioCacheSize;
        config.CacheLineScreenshots = v.CacheLineScreenshots;
        config.MaxCachedScreenshots = v.MaxCachedScreenshots;
        config.CacheReplayAudio = v.CacheReplayAudio;
        config.MaxCachedReplayAudio = v.MaxCachedReplayAudio;
        config.MaxCachedClips = v.MaxCachedClips;
        config.MaxConcurrentEncodes = v.MaxConcurrentEncodes;
        config.HistorySize = v.HistorySize;
        config.GifWidth = v.GifWidth;
        config.GifFps = v.GifFps;
        config.GifSeconds = v.GifSeconds;
    }

    public static PerformancePreset Detect(Configuration config) => PerformanceModel.Detect(
        config.UseWebViewPopup,
        config.EagerEncodeClips,
        config.VoiceBufferSeconds,
        config.LookupCacheSize,
        config.SegmentCacheSize,
        config.AudioCacheSize,
        config.MaxCachedScreenshots,
        config.MaxCachedReplayAudio,
        config.MaxCachedClips);

    public static void ApplySetupStyle(Configuration config, SetupStyle style)
    {
        config.SetupStyle = style;
        switch (style)
        {
            case SetupStyle.KeepGsm:
                config.ScanModifier = ScanModifier.Shift;
                config.PublishEnabled = true;
                config.CaptureScreenshot = false;
                config.RecordVoiceLines = false;
                config.CaptureGif = false;
                config.EagerEncodeClips = false;
                break;

            case SetupStyle.Lightweight:
                config.ScanModifier = ScanModifier.Shift;
                config.PublishEnabled = false;
                config.CaptureScreenshot = true;
                config.RecordVoiceLines = true;
                config.CaptureGif = false;
                config.EagerEncodeClips = false;
                break;

            case SetupStyle.FullMedia:
                config.ScanModifier = ScanModifier.None;
                config.PublishEnabled = false;
                config.CaptureScreenshot = true;
                config.RecordVoiceLines = true;
                config.CaptureGif = true;
                config.EagerEncodeClips = false;
                config.UseWebViewPopup = PlatformInfo.Current.SupportsWebView2Overlay;
                break;
        }
    }

    public static MemoryEstimate Estimate(Configuration config, bool platformSupportsWebView2) => PerformanceModel.Estimate(
        platformSupportsWebView2,
        config.UseWebViewPopup,
        config.FollowHachidoriTheme,
        config.RecordVoiceLines,
        config.VoiceBufferSeconds,
        config.CaptureGif,
        config.EagerEncodeClips,
        config.GifWidth,
        config.GifFps,
        config.GifSeconds,
        config.MaxCachedClips,
        config.CaptureScreenshot,
        config.CacheLineScreenshots,
        config.MaxCachedScreenshots,
        config.CacheReplayAudio,
        config.MaxCachedReplayAudio,
        config.LookupCacheSize,
        config.SegmentCacheSize,
        config.AudioCacheSize);
}
