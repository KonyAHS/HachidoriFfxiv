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
    private readonly Dictionary<string, VoicePlaybackEvent> poolTrackedVoices = new(StringComparer.OrdinalIgnoreCase);

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
            if (Plugin.SigScanner is not null && Plugin.GameInterop is not null)
            {
                if (TryScanFirst(out var loadPtr, loadSigOverride, LoadSoundFileSigPrimary, LoadSoundFileSigFallback))
                {
                    loadSoundFileHook = Plugin.GameInterop.HookFromAddress<LoadSoundFileDelegate>(loadPtr, LoadSoundFileDetour);
                    loadSoundFileHook.Enable();
                }

                if (TryScanFirst(out var playPtr, playSigOverride, PlaySpecificSoundSig))
                {
                    playSpecificSoundHook = Plugin.GameInterop.HookFromAddress<PlaySpecificSoundDelegate>(playPtr, PlaySpecificSoundDetour);
                    playSpecificSoundHook.Enable();
                }

                if (HooksActive)
                {
                    DiagnosticLog.Write("voice", "Hooked LoadSoundFile and PlaySpecificSound for native game voice detection.");
                }
                else
                {
                    var message = $"Native voice hooks partial/unavailable (load={LoadHookActive}, play={PlayHookActive}); using SoundManager.SoundDataPool + loudness fallback.";
                    DiagnosticLog.Write("voice", message);
                    Plugin.Log?.Warning("HachidoriFfxiv: {Message}", message);
                }
            }
        }
        catch (Exception ex)
        {
            var message = $"Could not initialize native voice hooks: {ex.Message}";
            DiagnosticLog.Write("voice", message);
            Plugin.Log?.Warning(ex, "HachidoriFfxiv: {Message}", message);
            loadSoundFileHook?.Dispose();
            playSpecificSoundHook?.Dispose();
            loadSoundFileHook = null;
            playSpecificSoundHook = null;
        }
    }

    private static bool TryScanFirst(out nint address, params string?[] signatures)
    {
        address = nint.Zero;
        foreach (var raw in signatures)
        {
            var sig = raw?.Trim();
            if (string.IsNullOrEmpty(sig))
                continue;
            if (Plugin.SigScanner.TryScanText(sig, out address))
                return true;
        }
        return false;
    }

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

            for (var i = 0; i < entries.Length; i++)
            {
                ref var data = ref entries[i];
                if (!data.IsActive || data.IsLoadingSoundResource || data.SoundResourceHandle == null)
                    continue;
                if (!data.IsPlaying())
                    continue;

                var path = ReadSoundPath(ref data);
                if (!VoiceLinePath.IsVoiceLine(path, out var kind))
                    continue;

                var normalized = VoiceLinePath.Normalize(path)!;
                var fileSize = data.SoundResourceHandle->FileSize;
                if (!VoiceLinePath.IsValidVoiceSize(fileSize))
                    continue;

                (activeNow ??= new(StringComparer.OrdinalIgnoreCase)).Add(normalized);

                if (!poolTrackedVoices.ContainsKey(normalized))
                {
                    var elapsedRaw = data.GetElapsedTime();
                    var elapsedOffset = float.IsFinite(elapsedRaw) && elapsedRaw >= 0f && elapsedRaw < 60f
                        ? (long)(elapsedRaw * Stopwatch.Frequency)
                        : 0L;
                    var startTicks = nowTicks - elapsedOffset;

                    // If hooks already emitted this voice recently, just track it for EndTicks without duplicating.
                    var alreadyFromHook = false;
                    lock (fireGate)
                    {
                        if (string.Equals(lastVoicePath, normalized, StringComparison.OrdinalIgnoreCase)
                            && Math.Abs(nowTicks - lastPlayedTicks) < Stopwatch.Frequency * 15)
                        {
                            alreadyFromHook = true;
                        }
                    }

                    if (!alreadyFromHook)
                    {
                        var ev = EmitVoice(normalized, kind, startTicks, "pool");
                        poolTrackedVoices[normalized] = ev;
                    }
                    else
                    {
                        poolTrackedVoices[normalized] = new VoicePlaybackEvent(normalized, kind, startTicks);
                    }
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
        DiagnosticLog.Write("voice", $"Detected voiced line ({source}, {kind}): {filePath}");

        // Resolve exact duration off-thread from Lumina SCD headers so clipping never cuts early or pads dead air.
        _ = Task.Run(() =>
        {
            try
            {
                var scd = Plugin.DataManager?.GetFile<ScdFile>(filePath);
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
        poolTrackedVoices.Clear();
    }
}
