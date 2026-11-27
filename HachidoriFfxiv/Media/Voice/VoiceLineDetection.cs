using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Scd;
using NVorbis;

namespace HachidoriFfxiv.Media;

/// <summary>Whether a game voice .scd belongs to cutscene dialogue or battle/field dialogue.</summary>
public enum VoiceLineKind
{
    Cutscene,
    Battle,
}

/// <summary>A real voice-acted .scd sound played by FFXIV's sound engine.</summary>
public sealed class VoicePlaybackEvent
{
    private double? durationSeconds;
    private long? endTicks;

    public VoicePlaybackEvent(string filePath, VoiceLineKind kind, long startTicks)
    {
        FilePath = filePath;
        Kind = kind;
        StartTicks = startTicks;
    }

    public string FilePath { get; }
    public VoiceLineKind Kind { get; }
    public long StartTicks { get; }

    /// <summary>Exact duration of the voice .scd in seconds, resolved asynchronously from Lumina.</summary>
    public double? DurationSeconds
    {
        get => durationSeconds;
        set
        {
            if (value is { } d && double.IsFinite(d) && d > 0.05 && d < 120.0)
                durationSeconds = d;
        }
    }

    /// <summary>Observed Stopwatch timestamp when SoundManager stopped playing this voice, if tracked.</summary>
    public long? EndTicks
    {
        get => endTicks;
        set
        {
            if (value is { } t && t >= StartTicks)
                endTicks = t;
        }
    }

    public string DisplayName => Path.GetFileNameWithoutExtension(FilePath);
}

/// <summary>
/// Classifies FFXIV .scd resource paths as voiced dialogue (matching TextToTalk / Echokraut's
/// proven rules) and estimates voice clip durations from Lumina <see cref="ScdFile"/> headers.
/// </summary>
public static class VoiceLinePath
{
    /// <summary>
    /// Square Enix ships empty or header-only stub .scd files (~1 B or ~416 B) for cut lines.
    /// Real voice clips are always well above 1 KB.
    /// </summary>
    public const int MinVoicedScdBytes = 1024;

    private static readonly Regex IgnoredSoundRegex = new(
        @"^(bgcommon|music|sound/(battle|foot|instruments|strm|vfx|voice/Vo_Emote|zingle))/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CutsceneVoiceRegex = new(
        @"^cut/.*/(vo_|voice)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BattleVoiceRegex = new(
        @"^sound/.*/Vo_Line/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Normalizes slashes and trims a game sound path.</summary>
    public static string? Normalize(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;
        return rawPath.Trim().Replace('\\', '/');
    }

    /// <summary>Returns true when <paramref name="rawPath"/> is a voiced dialogue .scd file.</summary>
    public static bool IsVoiceLine(string? rawPath, out VoiceLineKind kind)
    {
        kind = VoiceLineKind.Cutscene;
        var path = Normalize(rawPath);
        if (path is null || !path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IgnoredSoundRegex.IsMatch(path))
            return false;
        if (CutsceneVoiceRegex.IsMatch(path))
        {
            kind = VoiceLineKind.Cutscene;
            return true;
        }
        if (BattleVoiceRegex.IsMatch(path))
        {
            kind = VoiceLineKind.Battle;
            return true;
        }
        return false;
    }

    /// <summary>Returns true if a ResourceHandle FileSize indicates a real voice clip rather than a stub.</summary>
    public static bool IsValidVoiceSize(long fileSize) => fileSize <= 0 || fileSize >= MinVoicedScdBytes;

    /// <summary>
    /// Reads the duration (in seconds) of a voice <see cref="ScdFile"/> without decoding the audio body.
    /// Supports both MS-ADPCM (used for most FFXIV cutscene voices) and Ogg Vorbis.
    /// </summary>
    public static double? EstimateScdDurationSeconds(ScdFile scd)
    {
        if (scd.AudioDataCount <= 0)
            return null;

        try
        {
            var audio = scd.GetAudio(0);
            var desc = audio.AudioBasicDesc;
            switch (desc.Format)
            {
                case AudioFormat.OggVorbis:
                    if (audio.AudioData is { Length: >= 28 } ogg && ogg.AsSpan(0, 4).SequenceEqual("OggS"u8))
                    {
                        using var ms = new MemoryStream(ogg, writable: false);
                        using var reader = new VorbisReader(ms, closeOnDispose: false);
                        if (reader.SampleRate > 0 && reader.TotalSamples > 0)
                        {
                            var sec = reader.TotalSamples / (double)reader.SampleRate;
                            if (double.IsFinite(sec) && sec > 0.05 && sec < 120.0)
                                return sec;
                        }
                    }
                    break;

                case AudioFormat.MsAdpcm:
                    if (audio.AudioDataHeader is AdpcmWaveFormat adpcm && desc.Size > 0)
                    {
                        double? sec = null;
                        if (adpcm.BlockAlign > 0 && adpcm.SamplesPerBlock > 0 && desc.Rate > 0)
                            sec = (desc.Size / (double)adpcm.BlockAlign) * adpcm.SamplesPerBlock / desc.Rate;
                        else if (adpcm.AvgBytesPerSec > 0)
                            sec = desc.Size / (double)adpcm.AvgBytesPerSec;

                        if (sec is { } d && double.IsFinite(d) && d > 0.05 && d < 120.0)
                            return d;
                    }
                    break;
            }
        }
        catch
        {
            // Fall through to null when an SCD uses an unusual codec or layout.
        }

        return null;
    }
}

/// <summary>
/// Pure timestamp-based matcher linking <see cref="VoicePlaybackEvent"/>s to dialogue spans.
/// Handles both orderings observed in FFXIV:
/// - Voice starts just before the dialogue addon updates (common in cutscenes, ~0–600 ms early)
/// - Voice starts after the dialogue box appears (including dramatic pauses while the line stays open)
/// </summary>
public static class VoiceLineAssociation
{
    /// <summary>
    /// Maximum time a voice event may precede a newly captured dialogue line and still belong to it.
    /// In FFXIV, PlaySpecificSound often fires on the same frame or a few frames before TextCapture polls Talk.
    /// </summary>
    public const double PreLineWindowSeconds = 0.65;

    /// <summary>
    /// When a voice event arrives while <paramref name="openSpanStart"/> is open, returns whether
    /// a newly arriving line at <paramref name="newLineStart"/> should claim that voice from the previous line.
    /// </summary>
    public static bool ShouldTransferToNewLine(
        long prevSpanStart,
        VoicePlaybackEvent prevSpanVoice,
        long newLineStart,
        long ticksPerSecond,
        string? prevSpeaker = null,
        string? prevText = null,
        string? newSpeaker = null,
        string? newText = null)
    {
        var preWindow = (long)(PreLineWindowSeconds * ticksPerSecond);
        var beforeNewLine = newLineStart - prevSpanVoice.StartTicks;
        if (beforeNewLine < 0 || beforeNewLine > preWindow)
            return false;

        var afterPrevStart = prevSpanVoice.StartTicks - prevSpanStart;
        // If the voice fired right before the new line appeared and noticeably after the previous
        // line started, it belonged to the incoming line (e.g. unvoiced line -> voiced line).
        if (afterPrevStart > beforeNewLine)
            return true;

        // If the previous span was a transient speaker-only frame or partial prefix of the incoming
        // line within the pre-line window, transfer the voice to the complete line.
        if (!string.IsNullOrEmpty(prevText))
        {
            if (string.IsNullOrEmpty(prevSpeaker)
                && !string.IsNullOrEmpty(newSpeaker)
                && string.Equals(prevText, newSpeaker, StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(newText)
                && newText.Length > prevText.Length
                && newText.StartsWith(prevText, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the most recent unclaimed voice event in <paramref name="recentVoices"/> that fired
    /// shortly before <paramref name="newLineStart"/> (within <see cref="PreLineWindowSeconds"/>).
    /// Prefers events whose <see cref="VoiceLineKind"/> matches <paramref name="isBattleTalk"/>.
    /// </summary>
    public static VoicePlaybackEvent? FindUnclaimedPreLineVoice(
        IReadOnlyList<VoicePlaybackEvent> recentVoices,
        HashSet<VoicePlaybackEvent> claimedVoices,
        long newLineStart,
        bool isBattleTalk,
        long ticksPerSecond)
    {
        var preWindow = (long)(PreLineWindowSeconds * ticksPerSecond);
        var preferredKind = isBattleTalk ? VoiceLineKind.Battle : VoiceLineKind.Cutscene;
        VoicePlaybackEvent? fallback = null;

        for (var i = recentVoices.Count - 1; i >= 0; i--)
        {
            var v = recentVoices[i];
            if (claimedVoices.Contains(v))
                continue;
            var delta = newLineStart - v.StartTicks;
            if (delta < -ticksPerSecond / 20) // ignore future events beyond 50 ms jitter
                continue;
            if (delta > preWindow)
                break;

            if (v.Kind == preferredKind)
                return v;
            fallback ??= v;
        }

        return fallback;
    }
}
