using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Lumina.Data.Files;

namespace HachidoriFfxiv.Media;

/// <summary>
/// Detects real FFXIV voice-acted dialogue playback directly from the game's sound engine,
/// using the same <c>LoadSoundFile</c> + <c>PlaySpecificSound</c> hooks proven by TextToTalk /
/// SoundFilter / Echokraut, backed by a <see cref="SoundManager"/> active-pool companion for
/// stop-time tracking and signature-drift resilience.
/// </summary>
public sealed unsafe class VoiceLineTracker : IDisposable
{
    // Signatures from TextToTalk / Anna Clemens's SoundFilter (updated for FFXIV 7.x).
    private const string LoadSoundFileSigPrimary = "E8 ?? ?? ?? ?? 48 85 C0 75 12 B0 F6";
    private const string LoadSoundFileSigFallback = "E8 ?? ?? ?? ?? 48 85 C0 75 05 40 B7 F6";
    private const string PlaySpecificSoundSig = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 33 F6 8B DA 48 8B F9 0F BA E2 0F";

    // Poll SoundManager.SoundDataPool at ~25 Hz so ClientStructs-backed voice detection remains
    // responsive and accurate even if raw byte signatures drift on patch day.
    private const long PollIntervalMs = 40;
    private const double DuplicateFireWindowSeconds = 0.12;

    private delegate nint LoadSoundFileDelegate(nint resourceHandlePtr, uint arg2);
    private delegate nint PlaySpecificSoundDelegate(nint soundPtr, int arg2);

    private static readonly int ResourceDataOffset = Marshal.SizeOf<ResourceHandle>();
    private static readonly int SoundDataOffset = Marshal.SizeOf<nint>();

    private readonly Hook<LoadSoundFileDelegate>? loadSoundFileHook;
    private readonly Hook<PlaySpecificSoundDelegate>? playSpecificSoundHook;

    // Thread-safe map of loaded voice resource data pointers -> (path, kind).
    // Kept across replays (game audio cache can replay without reloading) and pruned on address reuse.
    private readonly ConcurrentDictionary<nint, (string FilePath, VoiceLineKind Kind)> knownVoiceLines = new();
    private readonly object fireGate = new();
    private readonly Dictionary<string, VoicePlaybackEvent> recentHookVoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VoicePlaybackEvent> poolTrackedVoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> poolPreloadedVoices = new(StringComparer.OrdinalIgnoreCase);

    private nint lastPlayedPtr;
    private long lastPlayedTicks;
    private long lastPollTicks;
    private volatile string? lastVoicePath;
    private volatile bool poolAvailable;
    private volatile bool disposed;

    public VoiceLineTracker(string? loadSigOverride = null, string? playSigOverride = null)
    {
        try
        {
            if (DalamudServices.SigScanner is not null && DalamudServices.GameInterop is not null)
            {
                var loadMatch = ScanFirst(out var loadPtr, ("override", loadSigOverride), ("primary", LoadSoundFileSigPrimary), ("fallback", LoadSoundFileSigFallback));
                if (loadMatch is not null)
                {
                    loadSoundFileHook = DalamudServices.GameInterop.HookFromAddress<LoadSoundFileDelegate>(loadPtr, LoadSoundFileDetour);
                    loadSoundFileHook.Enable();
                }

                var playMatch = ScanFirst(out var playPtr, ("override", playSigOverride), ("primary", PlaySpecificSoundSig));
                if (playMatch is not null)
                {
                    playSpecificSoundHook = DalamudServices.GameInterop.HookFromAddress<PlaySpecificSoundDelegate>(playPtr, PlaySpecificSoundDetour);
                    playSpecificSoundHook.Enable();
                }

                if (HooksActive)
                {
                    DiagnosticLog.Milestone("voice", $"Hooked LoadSoundFile ({loadMatch} sig, +0x{LoadOffset(loadPtr):x}) and PlaySpecificSound ({playMatch} sig, +0x{LoadOffset(playPtr):x}) for native game voice detection.");
                    SeedKnownVoicesFromPool(logCount: true);
                }
                else
                {
                    DiagnosticLog.Milestone("voice", $"Native voice hooks partial/unavailable (load={loadMatch ?? "no match"}, play={playMatch ?? "no match"}); using SoundManager.SoundDataPool + loudness fallback.", DiagLevel.Warn);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Fault("voice", ex, "Could not initialize native voice hooks");
            loadSoundFileHook?.Dispose();
            playSpecificSoundHook?.Dispose();
            loadSoundFileHook = null;
            playSpecificSoundHook = null;
        }
    }

    /// <summary>The name of the first signature that matches, so a patch-day log says which one broke.</summary>
    private static string? ScanFirst(out nint address, params (string Name, string? Signature)[] signatures)
    {
        address = nint.Zero;
        foreach (var (name, raw) in signatures)
        {
            var sig = raw?.Trim();
            if (string.IsNullOrEmpty(sig))
                continue;
            if (DalamudServices.SigScanner.TryScanText(sig, out address))
                return name;
        }
        return null;
    }

    private static long LoadOffset(nint address) =>
        address == nint.Zero ? 0 : (long)address - (long)DalamudServices.SigScanner.Module.BaseAddress;

    /// <summary>Raised whenever FFXIV starts playing a voice-acted dialogue .scd file.</summary>
    public event Action<VoicePlaybackEvent>? VoicePlayed;

    /// <summary>Whether both native game sound hooks are installed and active.</summary>
    public bool HooksActive => loadSoundFileHook is not null && playSpecificSoundHook is not null;

    public bool LoadHookActive => loadSoundFileHook is not null;
    public bool PlayHookActive => playSpecificSoundHook is not null;
    public bool PoolAvailable => poolAvailable;

    /// <summary>Human-readable summary of which voice detection tier is active.</summary>
    public string DetectionModeSummary =>
        HooksActive ? "Native hooks + SoundManager pool"
        : poolAvailable ? "SoundManager pool (native hooks unavailable)"
        : "Loudness fallback (native hooks & SoundManager pool unavailable)";

    /// <summary>The most recently played voice .scd path (for diagnostics).</summary>
    public string? LastVoicePath => lastVoicePath;

    /// <summary>
    /// Polls <see cref="SoundManager.SoundDataPool"/> on the main thread (~10 Hz) to track when
    /// active voice lines stop playing and as a secondary detector if sound hooks are unavailable.
    /// </summary>
    public void Update(long nowTicks)
    {
        if (disposed)
            return;

        var minStep = Stopwatch.Frequency * PollIntervalMs / 1000;
        if (nowTicks - lastPollTicks < minStep)
            return;
        lastPollTicks = nowTicks;

        try
        {
            var soundManager = SoundManager.Instance();
            if (soundManager == null || soundManager->SoundDataPool == null)
            {
                poolAvailable = false;
                return;
            }
            poolAvailable = true;

            var entries = soundManager->SoundDataPool->Entries;
            HashSet<string>? activeNow = null;
            List<(string Path, VoiceLineKind Kind, float Elapsed, long StartTicks)>? newPoolCandidates = null;
            var hooksActive = HooksActive;

            for (var i = 0; i < entries.Length; i++)
            {
                ref var data = ref entries[i];
                if (!data.IsActive || data.IsLoadingSoundResource || data.SoundResourceHandle == null)
                    continue;

                var path = ReadSoundPath(ref data);
                if (!VoiceLinePath.IsVoiceLine(path, out var kind))
                    continue;

                var normalized = VoiceLinePath.Normalize(path)!;
                var fileSize = data.SoundResourceHandle->FileSize;
                if (!VoiceLinePath.IsValidVoiceSize(fileSize))
                    continue;

                // Keep knownVoiceLines populated even for voices loaded before the plugin started
                // or hot-reloaded mid-cutscene (when LoadSoundFile already ran earlier).
                RegisterResourceHandle((nint)data.SoundResourceHandle, normalized, kind);

                if (!data.IsPlaying())
                    continue;

                (activeNow ??= new(StringComparer.OrdinalIgnoreCase)).Add(normalized);

                var elapsedRaw = data.GetElapsedTime();
                var elapsedValid = float.IsFinite(elapsedRaw) && elapsedRaw >= 0f && elapsedRaw < 60f ? elapsedRaw : 0f;

                if (hooksActive)
                {
                    // Native hooks are authoritative for playback start; use the pool only to track
                    // EndTicks on the exact VoicePlaybackEvent emitted by PlaySpecificSound.
                    VoicePlaybackEvent? hookEvent = null;
                    lock (fireGate)
                    {
                        if (recentHookVoices.TryGetValue(normalized, out var candidate)
                            && Math.Abs(nowTicks - candidate.StartTicks) < Stopwatch.Frequency * 30)
                        {
                            hookEvent = candidate;
                        }
                    }

                    if (hookEvent is not null && !poolTrackedVoices.ContainsKey(normalized))
                        poolTrackedVoices[normalized] = hookEvent;
                    continue;
                }

                if (poolTrackedVoices.ContainsKey(normalized))
                    continue;

                // Pool-only fallback (when native hooks are unavailable): if a cutscene preloaded
                // this voice in a batch earlier, only emit once its playback clock actually advances.
                if (poolPreloadedVoices.TryGetValue(normalized, out var preloadElapsed))
                {
                    if (elapsedValid > preloadElapsed + 0.01f)
                    {
                        poolPreloadedVoices.Remove(normalized);
                        var startTicks = nowTicks - (long)(elapsedValid * Stopwatch.Frequency);
                        poolTrackedVoices[normalized] = EmitVoice(normalized, kind, startTicks, "pool");
                    }
                    continue;
                }

                var candStart = nowTicks - (long)(elapsedValid * Stopwatch.Frequency);
                (newPoolCandidates ??= new()).Add((normalized, kind, elapsedValid, candStart));
            }

            if (newPoolCandidates is { Count: > 0 })
            {
                // Cutscenes preload all dialogue .scd slots into SoundDataPool simultaneously at scene start.
                if (newPoolCandidates.Count >= 3 && newPoolCandidates.TrueForAll(c => c.Kind == VoiceLineKind.Cutscene))
                {
                    foreach (var cand in newPoolCandidates)
                        poolPreloadedVoices[cand.Path] = cand.Elapsed;
                }
                else
                {
                    foreach (var cand in newPoolCandidates)
                        poolTrackedVoices[cand.Path] = EmitVoice(cand.Path, cand.Kind, cand.StartTicks, "pool");
                }
            }

            if (poolPreloadedVoices.Count > 0)
            {
                List<string>? evictedPreloads = null;
                foreach (var key in poolPreloadedVoices.Keys)
                {
                    if (activeNow is null || !activeNow.Contains(key))
                        (evictedPreloads ??= new()).Add(key);
                }

                if (evictedPreloads is not null)
                {
                    foreach (var key in evictedPreloads)
                        poolPreloadedVoices.Remove(key);
                }
            }

            if (poolTrackedVoices.Count > 0)
            {
                List<string>? stopped = null;
                foreach (var kv in poolTrackedVoices)
                {
                    if (activeNow is null || !activeNow.Contains(kv.Key))
                    {
                        kv.Value.EndTicks = nowTicks;
                        (stopped ??= new()).Add(kv.Key);
                    }
                }

                if (stopped is not null)
                {
                    foreach (var key in stopped)
                        poolTrackedVoices.Remove(key);
                }
            }
        }
        catch
        {
            // Never disrupt the game draw loop if SoundManager is mid-transition.
        }
    }

    private nint LoadSoundFileDetour(nint resourceHandlePtr, uint arg2)
    {
        var result = loadSoundFileHook!.Original(resourceHandlePtr, arg2);
        if (disposed || resourceHandlePtr == nint.Zero)
            return result;

        try
        {
            var handle = (ResourceHandle*)resourceHandlePtr;
            var fileName = handle->FileName.ToString();
            if (fileName.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
            {
                var resourceDataPtr = Marshal.ReadIntPtr(resourceHandlePtr + ResourceDataOffset);
                if (resourceDataPtr != nint.Zero)
                {
                    var normalized = VoiceLinePath.Normalize(fileName);
                    if (normalized is not null
                        && VoiceLinePath.IsVoiceLine(normalized, out var kind)
                        && VoiceLinePath.IsValidVoiceSize(handle->FileSize))
                    {
                        knownVoiceLines[resourceDataPtr] = (normalized, kind);
                    }
                    else
                    {
                        // Memory address may be reused by a non-voice sound (BGM/SFX/emote).
                        knownVoiceLines.TryRemove(resourceDataPtr, out _);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("voice", $"LoadSoundFile detour error: {ex.Message}");
        }

        return result;
    }

    private nint PlaySpecificSoundDetour(nint soundPtr, int arg2)
    {
        var result = playSpecificSoundHook!.Original(soundPtr, arg2);
        if (disposed || soundPtr == nint.Zero)
            return result;

        try
        {
            var soundDataPtr = Marshal.ReadIntPtr(soundPtr + SoundDataOffset);
            if (soundDataPtr != nint.Zero && knownVoiceLines.TryGetValue(soundDataPtr, out var info))
            {
                var now = Stopwatch.GetTimestamp();
                var duplicate = false;
                lock (fireGate)
                {
                    if (soundDataPtr == lastPlayedPtr
                        && (now - lastPlayedTicks) < (long)(DuplicateFireWindowSeconds * Stopwatch.Frequency))
                    {
                        duplicate = true;
                    }
                    else
                    {
                        lastPlayedPtr = soundDataPtr;
                        lastPlayedTicks = now;
                    }
                }

                if (!duplicate)
                    EmitVoice(info.FilePath, info.Kind, now, "hook");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("voice", $"PlaySpecificSound detour error: {ex.Message}");
        }

        return result;
    }

    private VoicePlaybackEvent EmitVoice(string filePath, VoiceLineKind kind, long startTicks, string source)
    {
        lastVoicePath = filePath;
        var ev = new VoicePlaybackEvent(filePath, kind, startTicks);
        if (source == "hook")
        {
            lock (fireGate)
            {
                recentHookVoices[filePath] = ev;
                if (recentHookVoices.Count > 32)
                {
                    var cutoff = startTicks - Stopwatch.Frequency * 60;
                    List<string>? stale = null;
                    foreach (var kv in recentHookVoices)
                    {
                        if (kv.Value.StartTicks < cutoff)
                            (stale ??= new()).Add(kv.Key);
                    }
                    if (stale is not null)
                    {
                        foreach (var key in stale)
                            recentHookVoices.Remove(key);
                    }
                }
            }
        }
        DiagnosticLog.Write("voice", $"Detected voiced line ({source}, {kind}): {filePath}");

        // Resolve exact duration off-thread from Lumina SCD headers so clipping never cuts early or pads dead air.
        _ = Task.Run(() =>
        {
            try
            {
                var scd = DalamudServices.DataManager?.GetFile<ScdFile>(filePath);
                if (scd is not null)
                    ev.DurationSeconds = VoiceLinePath.EstimateScdDurationSeconds(scd);
            }
            catch
            {
                // Non-fatal: VoiceClipAsync still records using span/audio boundaries.
            }
        });

        try
        {
            VoicePlayed?.Invoke(ev);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("voice", $"VoicePlayed handler error: {ex.Message}");
        }

        return ev;
    }

    private void SeedKnownVoicesFromPool(bool logCount)
    {
        try
        {
            var soundManager = SoundManager.Instance();
            if (soundManager == null || soundManager->SoundDataPool == null)
                return;

            var entries = soundManager->SoundDataPool->Entries;
            var seeded = 0;
            for (var i = 0; i < entries.Length; i++)
            {
                ref var data = ref entries[i];
                if (!data.IsActive || data.IsLoadingSoundResource || data.SoundResourceHandle == null)
                    continue;

                var path = ReadSoundPath(ref data);
                if (!VoiceLinePath.IsVoiceLine(path, out var kind))
                    continue;

                var normalized = VoiceLinePath.Normalize(path)!;
                if (!VoiceLinePath.IsValidVoiceSize(data.SoundResourceHandle->FileSize))
                    continue;

                if (RegisterResourceHandle((nint)data.SoundResourceHandle, normalized, kind))
                    seeded++;
            }

            if (logCount && seeded > 0)
                DiagnosticLog.Write("voice", $"Seeded {seeded} already-loaded voice line(s) from SoundManager pool.");
        }
        catch
        {
            // Non-fatal: hooks still capture newly loaded voice files.
        }
    }

    private bool RegisterResourceHandle(nint resourceHandlePtr, string normalizedPath, VoiceLineKind kind)
    {
        if (resourceHandlePtr == nint.Zero)
            return false;
        var resourceDataPtr = Marshal.ReadIntPtr(resourceHandlePtr + ResourceDataOffset);
        if (resourceDataPtr == nint.Zero)
            return false;
        var added = !knownVoiceLines.ContainsKey(resourceDataPtr);
        knownVoiceLines[resourceDataPtr] = (normalizedPath, kind);
        return added;
    }

    private static string? ReadSoundPath(ref SoundData data)
    {
        var name = data.GetFileName().ToString();
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        if (data.SoundResourceHandle != null)
            return data.SoundResourceHandle->FileName.ToString();
        return null;
    }

    public void Dispose()
    {
        disposed = true;
        loadSoundFileHook?.Dispose();
        playSpecificSoundHook?.Dispose();
        knownVoiceLines.Clear();
        lock (fireGate)
            recentHookVoices.Clear();
        poolTrackedVoices.Clear();
        poolPreloadedVoices.Clear();
    }
}
