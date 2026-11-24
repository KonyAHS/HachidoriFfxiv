using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Scd;
using NVorbis;

namespace HachidoriFfxiv.Media;

/// <summary>Snapshot of the game's playing BGM at a specific <see cref="System.Diagnostics.Stopwatch"/> timestamp.</summary>
public sealed record BgmSnapshot(
    string FilePath,
    uint SoundNumber,
    double ElapsedSeconds,
    long CapturedAtTicks,
    bool FromEngineClock,
    bool Muted)
{
    /// <summary>Short human-readable track name (filename without directory or .scd extension).</summary>
    public string DisplayName => Path.GetFileNameWithoutExtension(FilePath);
}

/// <summary>Minimal projection of a single <c>BGMSystem.Scene</c> slot for testable priority resolution.</summary>
public readonly record struct BgmSceneEntry(ushort BgmId, ushort PlayingBgmId);

/// <summary>
/// Resolves the highest-priority active BGM path from FFXIV's <c>BGMSystem.Scenes</c> array.
/// Matches OrchestrionPlugin's proven rules:
/// - <c>BgmId == 0</c> means the scene slot is inactive.
/// - <c>PlayingBgmId</c> is the actual resolved BGM sheet row (ignoring 0 and sentinel 9999).
/// - <c>BGMSystem.Scene.PlayState</c> is NOT used to skip scenes, because FFXIV leaves it at 0 on normal playing scenes.
/// - Row 1 or <c>BGM_Null.scd</c> in the highest-priority active scene means explicit silence.
/// </summary>
public static class BgmSceneSelector
{
    private const ushort NoChangeSentinelId = 9999;
    private const ushort SilenceBgmId = 1;
    private const string NullBgmSuffix = "BGM_Null.scd";

    public static bool IsNullBgm(string path) =>
        path.EndsWith(NullBgmSuffix, StringComparison.OrdinalIgnoreCase);

    public static (string? ActivePath, bool ExplicitSilence, HashSet<string>? OverriddenPaths) Select(
        ReadOnlySpan<BgmSceneEntry> scenes,
        Func<ushort, string?> resolvePath)
    {
        string? activePath = null;
        var explicitSilence = false;
        HashSet<string>? overriddenPaths = null;

        for (var i = 0; i < scenes.Length; i++)
        {
            var scene = scenes[i];
            // BgmId (offset 0x0C, Orchestrion's BgmReference) is 0 when the scene slot is unused.
            if (scene.BgmId == 0)
                continue;

            // PlayingBgmId (offset 0x0E) holds the resolved BGM row ID; 9999 is FFXIV's no-change sentinel.
            var id = scene.PlayingBgmId;
            if (id == 0 || id == NoChangeSentinelId)
                continue;

            var path = resolvePath(id);
            if (activePath is null && !explicitSilence)
            {
                if (id == SilenceBgmId || string.IsNullOrEmpty(path) || IsNullBgm(path))
                    explicitSilence = true;
                else
                    activePath = path;
            }
            else if (!string.IsNullOrEmpty(path) && !IsNullBgm(path)
                     && !string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase))
            {
                (overriddenPaths ??= new(StringComparer.OrdinalIgnoreCase)).Add(path);
            }
        }

        return (activePath, explicitSilence, overriddenPaths);
    }
}

/// <summary>A decoded-ready Ogg Vorbis OST entry extracted from an FFXIV .scd file, with sample-accurate loop bounds.</summary>
public sealed class CachedOstTrack
{
    public required string FilePath { get; init; }
    public required uint SoundNumber { get; init; }
    public required byte[] OggData { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required long TotalSamples { get; init; }
    public required long LoopStartSample { get; init; }
    public required long LoopEndSample { get; init; }

    public bool Loops => LoopEndSample > LoopStartSample && LoopEndSample <= TotalSamples;
}

/// <summary>
/// Extracts clean, loop-aware BGM slices directly from FFXIV .scd game files (Ogg Vorbis),
/// without capturing voices, dragon speak, or sound effects from the live audio mix.
/// </summary>
public static class ScdOstExtractor
{
    /// <summary>Parses an FFXIV <see cref="ScdFile"/> into a seekable Ogg Vorbis track with loop metadata.</summary>
    public static CachedOstTrack? LoadFromScd(ScdFile scd, string filePath, uint soundNumber = 0)
    {
        if (scd.AudioDataCount <= 0)
            return null;

        var audioIndex = ResolveAudioIndex(scd, soundNumber);
        if (audioIndex < 0 || audioIndex >= scd.AudioDataCount)
            audioIndex = 0;

        var audio = scd.GetAudio(audioIndex);
        if (audio.AudioBasicDesc.Format != AudioFormat.OggVorbis)
            return null;

        var data = audio.AudioData;
        if (data is null || data.Length < 28 || !data.AsSpan(0, 4).SequenceEqual("OggS"u8))
            return null;

        int sampleRate;
        int channels;
        long totalSamples;
        using (var ms = new MemoryStream(data, writable: false))
        using (var reader = new VorbisReader(ms, closeOnDispose: false))
        {
            sampleRate = reader.SampleRate;
            channels = reader.Channels;
            totalSamples = reader.TotalSamples;
        }

        if (sampleRate <= 0 || channels <= 0 || totalSamples <= 0)
            return null;

        var (loopStart, loopEnd) = ResolveLoopPoints(audio, totalSamples, sampleRate);
        return new CachedOstTrack
        {
            FilePath = filePath,
            SoundNumber = soundNumber,
            OggData = data,
            SampleRate = sampleRate,
            Channels = channels,
            TotalSamples = totalSamples,
            LoopStartSample = loopStart,
            LoopEndSample = loopEnd,
        };
    }

    /// <summary>Wraps a raw Ogg Vorbis stream (primarily for tests and offline verification).</summary>
    public static CachedOstTrack? FromOggData(string filePath, byte[] oggData, long loopStartSample = 0, long loopEndSample = 0)
    {
        if (oggData.Length < 28 || !oggData.AsSpan(0, 4).SequenceEqual("OggS"u8))
            return null;

        int sampleRate;
        int channels;
        long totalSamples;
        using (var ms = new MemoryStream(oggData, writable: false))
        using (var reader = new VorbisReader(ms, closeOnDispose: false))
        {
            sampleRate = reader.SampleRate;
            channels = reader.Channels;
            totalSamples = reader.TotalSamples;
        }

        if (sampleRate <= 0 || channels <= 0 || totalSamples <= 0)
            return null;

        var clampedEnd = loopEndSample > 0 ? Math.Min(loopEndSample, totalSamples) : 0;
        var clampedStart = Math.Clamp(loopStartSample, 0, clampedEnd);
        return new CachedOstTrack
        {
            FilePath = filePath,
            SoundNumber = 0,
            OggData = oggData,
            SampleRate = sampleRate,
            Channels = channels,
            TotalSamples = totalSamples,
            LoopStartSample = clampedStart,
            LoopEndSample = clampedEnd,
        };
    }

    /// <summary>
    /// Maps an arbitrary elapsed sample position into the valid playback range of the track,
    /// wrapping across [<paramref name="loopStartSample"/>, <paramref name="loopEndSample"/>) when the track loops.
    /// Returns -1 when a non-looping track has already finished.
    /// </summary>
    public static long ResolveStartSample(long requestedSample, long totalSamples, long loopStartSample, long loopEndSample)
    {
        if (totalSamples <= 0)
            return -1;
        if (requestedSample < 0)
            requestedSample = 0;

        if (loopEndSample > loopStartSample && loopEndSample <= totalSamples)
        {
            if (requestedSample >= loopEndSample)
            {
                var loopLength = loopEndSample - loopStartSample;
                requestedSample = loopStartSample + ((requestedSample - loopStartSample) % loopLength);
            }
            return requestedSample;
        }

        return requestedSample < totalSamples ? requestedSample : -1;
    }

    /// <summary>
    /// Seeks to <paramref name="startSeconds"/> (loop-aware) and decodes <paramref name="outputSamples"/>
    /// mono float samples at <paramref name="outputRate"/> Hz, seamlessly wrapping across loop boundaries.
    /// </summary>
    public static float[]? ExtractSlice(CachedOstTrack track, double startSeconds, int outputSamples, int outputRate = 48000)
    {
        if (outputSamples <= 0 || outputRate <= 0 || track.SampleRate <= 0 || track.Channels <= 0 || track.TotalSamples <= 0)
            return null;

        var requestedSample = (long)Math.Round(Math.Max(0.0, startSeconds) * track.SampleRate);
        var startSample = ResolveStartSample(requestedSample, track.TotalSamples, track.LoopStartSample, track.LoopEndSample);
        if (startSample < 0)
            return null;

        var srcNeeded = Math.Max(1, (int)Math.Ceiling(outputSamples * (double)track.SampleRate / outputRate) + 2);
        var monoSrc = new float[srcNeeded];
        var written = 0;

        using var ms = new MemoryStream(track.OggData, writable: false);
        using var reader = new VorbisReader(ms, closeOnDispose: false);
        var channels = reader.Channels;
        var invChannels = 1f / channels;
        var chunk = new float[4096 * channels];
        var loopLimit = track.Loops ? track.LoopEndSample : track.TotalSamples;

        reader.SamplePosition = Math.Clamp(startSample, 0, Math.Max(0, track.TotalSamples - 1));
        var wrapCount = 0;

        while (written < srcNeeded)
        {
            var currentPos = reader.SamplePosition;
            var remainingSrc = srcNeeded - written;
            var framesUntilLimit = (int)Math.Min(remainingSrc, Math.Max(0, loopLimit - currentPos));
            if (framesUntilLimit <= 0)
            {
                if (!track.Loops || ++wrapCount > 8)
                    break;
                reader.SamplePosition = track.LoopStartSample;
                continue;
            }

            var toReadSamples = Math.Min(chunk.Length, framesUntilLimit * channels);
            var readSamples = reader.ReadSamples(chunk, 0, toReadSamples);
            if (readSamples <= 0)
            {
                if (!track.Loops || ++wrapCount > 8)
                    break;
                reader.SamplePosition = track.LoopStartSample;
                continue;
            }

            var framesRead = readSamples / channels;
            for (var f = 0; f < framesRead && written < srcNeeded; f++)
            {
                float sum = 0;
                var baseIdx = f * channels;
                for (var c = 0; c < channels; c++)
                    sum += chunk[baseIdx + c];
                monoSrc[written++] = sum * invChannels;
            }
        }

        if (written == 0)
            return null;

        // If a non-looping track ended early, remaining samples in monoSrc are already 0 (silence).
        if (track.SampleRate == outputRate)
        {
            var exact = new float[outputSamples];
            Array.Copy(monoSrc, 0, exact, 0, Math.Min(outputSamples, monoSrc.Length));
            return exact;
        }

        return ResampleExact(monoSrc, track.SampleRate, outputSamples, outputRate);
    }

    /// <summary>Linearly resamples <paramref name="src"/> from <paramref name="srcRate"/> into exactly <paramref name="dstLength"/> samples at <paramref name="dstRate"/>.</summary>
    internal static float[] ResampleExact(ReadOnlySpan<float> src, int srcRate, int dstLength, int dstRate)
    {
        var dst = new float[dstLength];
        if (src.Length == 0 || dstLength == 0)
            return dst;

        var step = (double)srcRate / dstRate;
        var last = src.Length - 1;
        for (var i = 0; i < dstLength; i++)
        {
            var pos = i * step;
            var idx = (int)pos;
            if (idx >= last)
            {
                dst[i] = src[last];
            }
            else
            {
                var frac = (float)(pos - idx);
                dst[i] = src[idx] + (src[idx + 1] - src[idx]) * frac;
            }
        }
        return dst;
    }

    /// <summary>Reads the 64-bit granule position of an OggS page at <paramref name="offset"/>, or -1 if invalid.</summary>
    internal static long OggGranuleAtOffset(ReadOnlySpan<byte> oggData, int offset)
    {
        if (offset < 0 || offset + 14 > oggData.Length)
            return -1;
        if (!oggData.Slice(offset, 4).SequenceEqual("OggS"u8))
            return -1;
        return BinaryPrimitives.ReadInt64LittleEndian(oggData.Slice(offset + 6, 8));
    }

    private static (long Start, long End) ResolveLoopPoints(ScdFile.Audio audio, long totalSamples, int sampleRate)
    {
        // 1. Explicit sample-based marker chunk, when present.
        if (audio.MarkerChunkHeader is { } marker
            && marker.SampleLoopEndPos > marker.SampleLoopStartPos
            && marker.SampleLoopStartPos >= 0
            && marker.SampleLoopEndPos <= totalSamples)
        {
            return (marker.SampleLoopStartPos, marker.SampleLoopEndPos);
        }

        // 2. SCD Ogg Vorbis byte loop offsets (relative to the end of OggHeaderSize).
        var desc = audio.AudioBasicDesc;
        if (desc.LoopEnd <= desc.LoopStart)
            return (0, 0);

        var oggHeaderSize = audio.AudioDataHeader is OggVorbisSeekTableHeader seekHdr ? seekHdr.OggHeaderSize : 0u;
        var step = audio.AudioDataHeader is OggVorbisSeekTableHeader h && h.Step > 0f ? h.Step : 0.1f;

        long loopStart = desc.LoopStart == 0
            ? 0
            : ResolveByteOffsetToSample(audio.AudioData, audio.SeekTable, desc.LoopStart, oggHeaderSize, step, sampleRate, totalSamples);
        long loopEnd = desc.LoopEnd >= desc.Size
            ? totalSamples
            : ResolveByteOffsetToSample(audio.AudioData, audio.SeekTable, desc.LoopEnd, oggHeaderSize, step, sampleRate, totalSamples);

        if (loopStart < 0) loopStart = 0;
        if (loopEnd <= loopStart || loopEnd > totalSamples) loopEnd = totalSamples;
        return loopEnd > loopStart ? (loopStart, loopEnd) : (0, 0);
    }

    private static long ResolveByteOffsetToSample(
        byte[] oggData,
        uint[]? seekTable,
        uint byteOffset,
        uint oggHeaderSize,
        float stepSeconds,
        int sampleRate,
        long totalSamples)
    {
        // Prefer the OggS page granule position right at byteOffset + oggHeaderSize.
        var pageOffset = (long)byteOffset + oggHeaderSize;
        if (pageOffset >= 0 && pageOffset + 14 <= oggData.Length)
        {
            var granule = OggGranuleAtOffset(oggData, (int)pageOffset);
            if (granule >= 0 && granule <= totalSamples)
                return granule;
        }

        // Fall back to SCD's fixed-interval seek table (typically 0.1s per entry).
        if (seekTable is { Length: > 0 })
        {
            var idx = Array.BinarySearch(seekTable, byteOffset);
            if (idx < 0) idx = ~idx;
            var seconds = idx * (double)stepSeconds;
            return Math.Clamp((long)Math.Round(seconds * sampleRate), 0, totalSamples);
        }

        return 0;
    }

    private static int ResolveAudioIndex(ScdFile scd, uint soundNumber)
    {
        if (scd.AudioDataCount <= 1 || scd.SoundDataCount <= 0)
            return 0;

        try
        {
            var targetIndex = (int)Math.Min(soundNumber, (uint)(scd.SoundDataCount - 1));
            var sound = scd.GetSound(targetIndex);
            if (sound.TrackInfos is TrackInfo[] tracks && tracks.Length > 0)
                return tracks[0].AudioDataIndex;
            if (sound.TrackInfos is RandomTrackInfo[] randTracks && randTracks.Length > 0)
                return randTracks[0].BaseInfo.AudioDataIndex;
        }
        catch
        {
            // Fall back to entry 0 if SCD sound metadata is unusual.
        }

        return 0;
    }
}

/// <summary>Small thread-safe LRU cache of the most recently decoded BGM SCD tracks (so repeated mines in the same scene are instant).</summary>
public sealed class OstTrackCache
{
    private readonly object gate = new();
    private readonly int maxEntries;
    private readonly LinkedList<(string KeyPath, uint SoundNumber, CachedOstTrack Track)> entries = new();

    public OstTrackCache(int maxEntries = 3) => this.maxEntries = Math.Max(1, maxEntries);

    public CachedOstTrack? GetOrAdd(string filePath, uint soundNumber, Func<CachedOstTrack?> loader)
    {
        lock (gate)
        {
            for (var node = entries.First; node is not null; node = node.Next)
            {
                if (node.Value.SoundNumber == soundNumber && string.Equals(node.Value.KeyPath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    entries.Remove(node);
                    entries.AddFirst(node);
                    return node.Value.Track;
                }
            }
        }

        var loaded = loader();
        if (loaded is null)
            return null;

        lock (gate)
        {
            // Avoid duplicating if another thread loaded it concurrently.
            for (var node = entries.First; node is not null; node = node.Next)
            {
                if (node.Value.SoundNumber == soundNumber && string.Equals(node.Value.KeyPath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    entries.Remove(node);
                    entries.AddFirst(node);
                    return node.Value.Track;
                }
            }

            entries.AddFirst((filePath, soundNumber, loaded));
            while (entries.Count > maxEntries)
                entries.RemoveLast();
            return loaded;
        }
    }
}
