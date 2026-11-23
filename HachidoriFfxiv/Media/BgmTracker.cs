using System;
using System.Collections.Generic;
using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Sound;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;

namespace HachidoriFfxiv.Media;

/// <summary>
/// Tracks FFXIV's currently playing BGM track and its playback timing on the main thread
/// using <see cref="BGMSystem"/> and <see cref="SoundManager"/>, and extracts clean OST audio
/// slices directly from the game's .scd files via Lumina.
/// </summary>
public sealed unsafe class BgmTracker
{
    private const long PollIntervalTicks = 200; // milliseconds
    private const double ResumeRememberSeconds = 600.0; // remember paused track position for up to 10 minutes

    private readonly object gate = new();
    private readonly OstTrackCache cache = new(maxEntries: 3);
    private readonly Dictionary<ushort, string?> bgmPaths = new();
    private readonly Dictionary<string, (double ElapsedSeconds, long SavedAtTicks)> pausedPositions = new(StringComparer.OrdinalIgnoreCase);

    private long lastPollTicks;
    private string? activePath;
    private long trackStartedAtTicks;
    private double lastElapsedSeconds;
    private bool loggedFirstDetection;
    private BgmSnapshot? lastSnapshot;
    private bool bgmMutedInGame;

    /// <summary>The most recent BGM snapshot observed on the main thread, or null when silent / no OST is playing.</summary>
    public BgmSnapshot? LastSnapshot
    {
        get
        {
            lock (gate)
                return lastSnapshot;
        }
    }

    /// <summary>Whether the game's BGM bus is currently muted or turned down to zero.</summary>
    public bool BgmMutedInGame
    {
        get
        {
            lock (gate)
                return bgmMutedInGame;
        }
    }

    /// <summary>Returns the current BGM snapshot projected forward to <paramref name="nowTicks"/>.</summary>
    public BgmSnapshot? CurrentAt(long nowTicks)
    {
        lock (gate)
        {
            if (lastSnapshot is null)
                return null;
            var delta = Math.Max(0.0, (nowTicks - lastSnapshot.CapturedAtTicks) / (double)Stopwatch.Frequency);
            return lastSnapshot with { ElapsedSeconds = lastSnapshot.ElapsedSeconds + delta, CapturedAtTicks = nowTicks };
        }
    }

    /// <summary>Call once per frame on the main thread; internally throttled to ~5 Hz.</summary>
    public void Update(long nowTicks)
    {
        var minStep = Stopwatch.Frequency * PollIntervalTicks / 1000;
        if (nowTicks - lastPollTicks < minStep)
            return;
        CaptureNow(nowTicks);
    }

    /// <summary>
    /// Reads FFXIV's live BGM state immediately on the main thread (call when a dialogue line starts)
    /// and returns a <see cref="BgmSnapshot"/> with accurate track identity and elapsed playback time.
    /// </summary>
    public BgmSnapshot? CaptureNow(long nowTicks)
    {
        lastPollTicks = nowTicks;
        var (filePath, soundNumber, engineElapsed, trackVolume, muted) = ReadLiveBgm();

        lock (gate)
        {
            bgmMutedInGame = muted;

            if (string.IsNullOrEmpty(filePath))
            {
                RememberActiveTrackLocked(nowTicks);
                activePath = null;
                lastSnapshot = null;
                return null;
            }

            var hasEngineClock = engineElapsed is { } e && double.IsFinite(e) && e > 0.001 && e < 86400.0;
            double elapsed;

            if (!string.Equals(activePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                RememberActiveTrackLocked(nowTicks);
                activePath = filePath;

                if (hasEngineClock)
                {
                    elapsed = engineElapsed!.Value;
                    trackStartedAtTicks = nowTicks - (long)(elapsed * Stopwatch.Frequency);
                }
                else if (pausedPositions.TryGetValue(filePath, out var saved)
                         && (nowTicks - saved.SavedAtTicks) / (double)Stopwatch.Frequency <= ResumeRememberSeconds)
                {
                    elapsed = saved.ElapsedSeconds;
                    trackStartedAtTicks = nowTicks - (long)(elapsed * Stopwatch.Frequency);
                }
                else
                {
                    elapsed = 0.0;
                    trackStartedAtTicks = nowTicks;
                }

                DiagnosticLog.Write("ost", $"BGM switched to {filePath} (sound={soundNumber}, elapsed={elapsed:0.0}s, engineClock={hasEngineClock}, vol={trackVolume:0.00}, muted={muted}).");
                loggedFirstDetection = true;
            }
            else
            {
                if (hasEngineClock)
                {
                    elapsed = engineElapsed!.Value;
                    trackStartedAtTicks = nowTicks - (long)(elapsed * Stopwatch.Frequency);
                }
                else
                {
                    elapsed = Math.Max(0.0, (nowTicks - trackStartedAtTicks) / (double)Stopwatch.Frequency);
                }

                if (!loggedFirstDetection)
                {
                    DiagnosticLog.Write("ost", $"BGM active: {filePath} (sound={soundNumber}, elapsed={elapsed:0.0}s, engineClock={hasEngineClock}, vol={trackVolume:0.00}, muted={muted}).");
                    loggedFirstDetection = true;
                }
            }

            lastElapsedSeconds = elapsed;
            lastSnapshot = new BgmSnapshot(filePath, soundNumber, elapsed, nowTicks, trackVolume, hasEngineClock, muted);
            return lastSnapshot;
        }
    }

    /// <summary>
    /// Loads (or retrieves from cache) the .scd file for <paramref name="snapshot"/> and decodes
    /// <paramref name="outputSamples"/> mono float samples at <paramref name="outputRate"/> Hz
    /// starting at <paramref name="startSeconds"/>. Safe to call from a background worker thread.
    /// </summary>
    public float[]? ExtractSlice(BgmSnapshot snapshot, double startSeconds, int outputSamples, int outputRate = 48000)
    {
        if (string.IsNullOrWhiteSpace(snapshot.FilePath) || outputSamples <= 0)
            return null;

        try
        {
            var track = cache.GetOrAdd(snapshot.FilePath, snapshot.SoundNumber, () =>
            {
                var scd = Plugin.DataManager.GetFile<ScdFile>(snapshot.FilePath);
                if (scd is null)
                {
                    DiagnosticLog.Write("ost", $"Could not load SCD file: {snapshot.FilePath}");
                    return null;
                }

                var loaded = ScdOstExtractor.LoadFromScd(scd, snapshot.FilePath, snapshot.SoundNumber);
                if (loaded is null)
                    DiagnosticLog.Write("ost", $"Unsupported or non-OggVorbis SCD file: {snapshot.FilePath}");
                return loaded;
            });

            if (track is null)
                return null;

            return ScdOstExtractor.ExtractSlice(track, startSeconds, outputSamples, outputRate);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("ost", $"Failed to extract OST slice for {snapshot.FilePath}: {ex.Message}");
            return null;
        }
    }

    private void RememberActiveTrackLocked(long nowTicks)
    {
        if (string.IsNullOrEmpty(activePath))
            return;
        pausedPositions[activePath] = (lastElapsedSeconds, nowTicks);
        if (pausedPositions.Count > 16)
        {
            string? oldestKey = null;
            var oldestTicks = long.MaxValue;
            foreach (var kv in pausedPositions)
            {
                if (kv.Value.SavedAtTicks < oldestTicks)
                {
                    oldestTicks = kv.Value.SavedAtTicks;
                    oldestKey = kv.Key;
                }
            }
            if (oldestKey is not null)
                pausedPositions.Remove(oldestKey);
        }
    }

    /// <summary>
    /// Reads FFXIV's active BGM path, sound number, engine elapsed time, volume, and mute state.
    /// Must only be called on the main/framework thread.
    /// </summary>
    private (string? FilePath, uint SoundNumber, double? EngineElapsed, float TrackVolume, bool Muted) ReadLiveBgm()
    {
        try
        {
            var soundManager = SoundManager.Instance();
            var muted = false;
            if (soundManager != null)
            {
                if (soundManager->Disabled || soundManager->MasterVolume <= 0.001f)
                    muted = true;
                else if (soundManager->BusMuted[(int)SoundBus.Music] || soundManager->Volume[(int)SoundBus.Music] <= 0.001f)
                    muted = true;
            }

            string? bgmSystemPath = null;
            var explicitSilence = false;
            HashSet<string>? overriddenScenePaths = null;

            var bgmSystem = BGMSystem.Instance();
            if (bgmSystem != null)
            {
                var count = (int)Math.Min(bgmSystem->Scenes.Count, 32);
                if (count > 0)
                {
                    Span<BgmSceneEntry> scenes = stackalloc BgmSceneEntry[count];
                    for (var i = 0; i < count; i++)
                    {
                        var scene = bgmSystem->Scenes[i];
                        scenes[i] = new BgmSceneEntry(scene.BgmId, scene.PlayingBgmId);
                    }
                    (bgmSystemPath, explicitSilence, overriddenScenePaths) = BgmSceneSelector.Select(scenes, ResolveBgmPath);
                }
            }

            // Cross-reference with SoundManager's active SoundData pool to get exact engine elapsed time
            // and also catch Orchestrion / direct PlayBGMSound tracks not tracked by BGMSystem.
            if (soundManager != null && soundManager->SoundDataPool != null)
            {
                var entries = soundManager->SoundDataPool->Entries;
                var orchestrionPath = NormalizePath(soundManager->OrchestrionPathString);

                uint matchedSoundNumber = 0;
                double? matchedElapsed = null;
                float matchedVolume = -1f;

                uint orchSoundNumber = 0;
                double? orchElapsed = null;
                float orchVolume = -1f;

                string? bestFallbackPath = null;
                uint bestFallbackSoundNumber = 0;
                double? bestFallbackElapsed = null;
                float bestFallbackVolume = 0f;

                for (var i = 0; i < entries.Length; i++)
                {
                    ref var data = ref entries[i];
                    if (!data.IsActive || data.IsLoadingSoundResource || data.SoundResourceHandle == null)
                        continue;
                    if (!data.IsPlaying() || data.GetIsFadingOut())
                        continue;

                    var path = ReadSoundPath(ref data);
                    if (string.IsNullOrEmpty(path) || BgmSceneSelector.IsNullBgm(path))
                        continue;

                    var vol = data.GetVolume();
                    var elapsedRaw = data.GetElapsedTime();
                    double? elapsed = float.IsFinite(elapsedRaw) && elapsedRaw >= 0f && elapsedRaw < 86400f
                        ? elapsedRaw
                        : null;

                    if (orchestrionPath is not null
                        && string.Equals(path, orchestrionPath, StringComparison.OrdinalIgnoreCase)
                        && vol > 0.01f && vol > orchVolume)
                    {
                        orchSoundNumber = data.GetSoundNumber();
                        orchElapsed = elapsed;
                        orchVolume = vol;
                    }

                    if (bgmSystemPath is not null
                        && string.Equals(path, bgmSystemPath, StringComparison.OrdinalIgnoreCase)
                        && vol > matchedVolume)
                    {
                        matchedSoundNumber = data.GetSoundNumber();
                        matchedElapsed = elapsed;
                        matchedVolume = vol;
                    }

                    if (path.StartsWith("music/", StringComparison.OrdinalIgnoreCase) && vol > 0.01f)
                    {
                        // Skip tracks belonging to lower-priority overridden BGMSystem scenes.
                        if (overriddenScenePaths is not null && overriddenScenePaths.Contains(path)
                            && !string.Equals(path, orchestrionPath, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // If BGMSystem explicitly requested silence, only allow Orchestrion or an actively loud direct BGM.
                        if (explicitSilence && !string.Equals(path, orchestrionPath, StringComparison.OrdinalIgnoreCase) && vol < 0.05f)
                            continue;

                        if (vol > bestFallbackVolume)
                        {
                            bestFallbackPath = path;
                            bestFallbackSoundNumber = data.GetSoundNumber();
                            bestFallbackElapsed = elapsed;
                            bestFallbackVolume = vol;
                        }
                    }
                }

                // Estate / inn Orchestrion plays over BGMSystem when active.
                if (orchestrionPath is not null && orchVolume > 0.01f)
                    return (orchestrionPath, orchSoundNumber, orchElapsed, orchVolume, muted);

                if (bgmSystemPath is not null)
                {
                    if (matchedVolume > 0.005f)
                        return (bgmSystemPath, matchedSoundNumber, matchedElapsed, matchedVolume, muted);
                    // Track is active in BGMSystem (e.g. starting/fading in or streaming); keep wall-clock tracking.
                    return (bgmSystemPath, 0, null, 1f, muted);
                }

                if (bestFallbackPath is not null)
                    return (bestFallbackPath, bestFallbackSoundNumber, bestFallbackElapsed, bestFallbackVolume, muted);
            }

            if (bgmSystemPath is not null)
                return (bgmSystemPath, 0, null, 1f, muted);

            return (null, 0, null, 0f, muted);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("ost", $"Error reading live BGM state: {ex.Message}");
            return (null, 0, null, 0f, false);
        }
    }

    private string? ResolveBgmPath(ushort bgmId)
    {
        if (bgmId == 0)
            return null;
        if (bgmPaths.TryGetValue(bgmId, out var cached))
            return cached;

        string? resolved = null;
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<BGM>();
            if (sheet is not null && sheet.TryGetRow(bgmId, out var row))
                resolved = NormalizePath(row.File.ExtractText());
        }
        catch
        {
            resolved = null;
        }

        bgmPaths[bgmId] = resolved;
        return resolved;
    }

    private static string? ReadSoundPath(ref SoundData data)
    {
        var name = data.GetFileName().ToString();
        if (!string.IsNullOrWhiteSpace(name))
            return NormalizePath(name);
        if (data.SoundResourceHandle != null)
            return NormalizePath(data.SoundResourceHandle->FileName.ToString());
        return null;
    }

    private static string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return raw.Trim().Replace('\\', '/');
    }
}
