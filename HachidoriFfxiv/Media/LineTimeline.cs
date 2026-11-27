using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HachidoriFfxiv.Capture;

namespace HachidoriFfxiv.Media;

/// <summary>When a dialogue line was on screen, on the <see cref="Stopwatch"/> clock.</summary>
public sealed class LineSpan
{
    public required GameLine Line { get; init; }
    public required long Start { get; init; }
    public long? End { get; set; }
    /// <summary>The playing OST and its playback timing when this line appeared, for clean TTS + OST mixing.</summary>
    public BgmSnapshot? Bgm { get; init; }
    /// <summary>The native game voice .scd playback associated with this line, if FFXIV voiced it.</summary>
    public VoicePlaybackEvent? Voice { get; set; }
    /// <summary>Cached JPEG of the game viewport captured while this line was on screen, for mining past lines.</summary>
    public byte[]? ScreenshotJpeg { get; set; }
    /// <summary>Cached encoded audio bytes for instant repeated playback of this line.</summary>
    public byte[]? ReplayAudio { get; set; }
    /// <summary>True if <see cref="ReplayAudio"/> was synthesized via TTS rather than extracted/recorded from game voice.</summary>
    public bool ReplayAudioIsTts { get; set; }
}

/// <summary>
/// The recent dialogue and battle-talk lines with their on-screen times and native voice events.
/// A line ends when the next one appears or its window closes.
/// </summary>
public sealed class LineTimeline
{
    public const int MaxCachedScreenshots = 40;
    public const int MaxCachedReplayAudio = 40;

    private readonly Func<int> screenshotLimit;
    private readonly Func<int> replayAudioLimit;
    private readonly List<LineSpan> spans = new();
    private readonly List<VoicePlaybackEvent> recentVoices = new();
    private readonly HashSet<VoicePlaybackEvent> claimedVoices = new();
    private readonly object gate = new();

    public LineTimeline(Func<int>? maxScreenshots = null, Func<int>? maxReplayAudio = null)
    {
        screenshotLimit = maxScreenshots ?? (() => MaxCachedScreenshots);
        replayAudioLimit = maxReplayAudio ?? (() => MaxCachedReplayAudio);
    }

    public (int Screenshots, long ScreenshotBytes, int ReplayAudios, long ReplayAudioBytes) CacheStats()
    {
        lock (gate)
        {
            int shots = 0, audios = 0;
            long shotBytes = 0, audioBytes = 0;
            foreach (var s in spans)
            {
                if (s.ScreenshotJpeg is { Length: > 0 } jpg)
                {
                    shots++;
                    shotBytes += jpg.Length;
                }
                if (s.ReplayAudio is { Length: > 0 } aud)
                {
                    audios++;
                    audioBytes += aud.Length;
                }
            }
            return (shots, shotBytes, audios, audioBytes);
        }
    }

    public void ClearMediaCaches()
    {
        lock (gate)
        {
            foreach (var s in spans)
            {
                s.ScreenshotJpeg = null;
                s.ReplayAudio = null;
                s.ReplayAudioIsTts = false;
            }
        }
    }

    public void TrimMediaCaches()
    {
        lock (gate)
        {
            var maxShots = Math.Max(0, screenshotLimit());
            var maxAudios = Math.Max(0, replayAudioLimit());
            int keptShots = 0, keptAudios = 0;
            for (var i = spans.Count - 1; i >= 0; i--)
            {
                if (spans[i].ScreenshotJpeg is not null)
                {
                    keptShots++;
                    if (keptShots > maxShots)
                        spans[i].ScreenshotJpeg = null;
                }
                if (spans[i].ReplayAudio is not null)
                {
                    keptAudios++;
                    if (keptAudios > maxAudios)
                    {
                        spans[i].ReplayAudio = null;
                        spans[i].ReplayAudioIsTts = false;
                    }
                }
            }
        }
    }

    public void OnLine(GameLine line, long now, BgmSnapshot? bgm = null)
    {
        if (line.Source is not (LineSource.Talk or LineSource.BattleTalk))
            return;
        var isBattle = line.Source == LineSource.BattleTalk;
        lock (gate)
        {
            LineSpan? previousOpen = null;
            foreach (var open in spans.Where(s => s.End is null))
            {
                if (open.Line.Source == line.Source)
                    previousOpen = open;
                open.End = now;
            }

            // 1. Claim an unclaimed voice that fired right before this line appeared.
            var voice = VoiceLineAssociation.FindUnclaimedPreLineVoice(
                recentVoices, claimedVoices, now, isBattle, Stopwatch.Frequency);
            if (voice is not null)
            {
                claimedVoices.Add(voice);
            }
            // 2. If the previous open line had just tentatively received a voice moments before
            //    this new line appeared (unvoiced -> voiced transition, or a transient partial line),
            //    transfer it to the new line.
            else if (previousOpen?.Voice is { } prevVoice
                     && VoiceLineAssociation.ShouldTransferToNewLine(
                         previousOpen.Start,
                         prevVoice,
                         now,
                         Stopwatch.Frequency,
                         previousOpen.Line.Speaker,
                         previousOpen.Line.Text,
                         line.Speaker,
                         line.Text))
            {
                previousOpen.Voice = null;
                voice = prevVoice;
            }

            spans.Add(new LineSpan { Line = line, Start = now, Bgm = bgm, Voice = voice });
            if (spans.Count > 200)
                spans.RemoveRange(0, spans.Count - 200);
        }
    }

    /// <summary>Records a voice-acted .scd playback from FFXIV's sound engine and links it to an open line if appropriate.</summary>
    public void OnVoicePlayed(VoicePlaybackEvent voice)
    {
        lock (gate)
        {
            recentVoices.Add(voice);
            if (recentVoices.Count > 64)
            {
                var dropped = recentVoices[0];
                recentVoices.RemoveAt(0);
                claimedVoices.Remove(dropped);
            }

            // Prefer an open span whose source matches the voice kind (Cutscene -> Talk, Battle -> BattleTalk)
            // and that does not already have a voice assigned.
            var preferredSource = voice.Kind == VoiceLineKind.Battle ? LineSource.BattleTalk : LineSource.Talk;
            LineSpan? candidate = null;
            for (var i = spans.Count - 1; i >= 0; i--)
            {
                var s = spans[i];
                if (s.End is not null)
                    continue;
                if (s.Voice is not null)
                    continue;
                if (s.Line.Source == preferredSource)
                {
                    candidate = s;
                    break;
                }
                candidate ??= s;
            }

            if (candidate is not null)
            {
                candidate.Voice = voice;
                claimedVoices.Add(voice);
            }
        }
    }

    /// <summary>Called each frame: a closed dialogue window ends its line.</summary>
    public void OnWindows(bool talkVisible, bool battleTalkVisible, long now)
    {
        lock (gate)
        {
            foreach (var open in spans.Where(s => s.End is null))
            {
                var visible = open.Line.Source == LineSource.Talk ? talkVisible : battleTalkVisible;
                if (!visible)
                    open.End = now;
            }
        }
    }

    public long? EndOf(LineSpan span)
    {
        lock (gate)
            return span.End;
    }

    public VoicePlaybackEvent? VoiceOf(LineSpan span)
    {
        lock (gate)
            return span.Voice;
    }

    public byte[]? ScreenshotOf(LineSpan span)
    {
        lock (gate)
            return span.ScreenshotJpeg;
    }

    public (byte[]? Audio, bool IsTts) ReplayAudioOf(LineSpan span)
    {
        lock (gate)
            return (span.ReplayAudio, span.ReplayAudioIsTts);
    }

    public bool SetReplayAudio(long lineId, byte[] audio, bool isTts)
    {
        if (audio.Length == 0)
            return false;
        lock (gate)
        {
            LineSpan? target = null;
            for (var i = spans.Count - 1; i >= 0; i--)
            {
                if (spans[i].Line.Id == lineId)
                {
                    target = spans[i];
                    break;
                }
            }
            if (target is null)
                return false;

            // Never overwrite a real game-voice clip with a TTS clip.
            if (isTts && target.ReplayAudio is { Length: > 0 } && !target.ReplayAudioIsTts)
                return false;

            var maxAudios = Math.Max(0, replayAudioLimit());
            if (maxAudios == 0)
                return false;

            target.ReplayAudio = audio;
            target.ReplayAudioIsTts = isTts;

            // Keep cached replay audio memory bounded to the newest configured lines.
            var kept = 0;
            for (var i = spans.Count - 1; i >= 0; i--)
            {
                if (spans[i].ReplayAudio is null)
                    continue;
                kept++;
                if (kept > maxAudios)
                {
                    spans[i].ReplayAudio = null;
                    spans[i].ReplayAudioIsTts = false;
                }
            }
            return true;
        }
    }

    public bool SetScreenshot(long lineId, byte[] jpeg)
    {
        if (jpeg.Length == 0)
            return false;
        lock (gate)
        {
            LineSpan? target = null;
            for (var i = spans.Count - 1; i >= 0; i--)
            {
                if (spans[i].Line.Id == lineId)
                {
                    target = spans[i];
                    break;
                }
            }
            if (target is null)
                return false;

            var maxShots = Math.Max(0, screenshotLimit());
            if (maxShots == 0)
                return false;

            target.ScreenshotJpeg = jpeg;

            // Keep screenshot memory bounded to the newest configured lines.
            var kept = 0;
            for (var i = spans.Count - 1; i >= 0; i--)
            {
                if (spans[i].ScreenshotJpeg is null)
                    continue;
                kept++;
                if (kept > maxShots)
                    spans[i].ScreenshotJpeg = null;
            }
            return true;
        }
    }

    public LineSpan? FindById(long lineId)
    {
        if (lineId <= 0)
            return null;
        lock (gate)
        {
            for (var i = spans.Count - 1; i >= 0; i--)
            {
                if (spans[i].Line.Id == lineId)
                    return spans[i];
            }
        }
        return null;
    }

    /// <summary>Exact match by captured line id when known, else falls back to sentence text lookup.</summary>
    public LineSpan? Resolve(long? lineId, string? sentence)
    {
        if (lineId is { } id && FindById(id) is { } exact)
            return exact;
        return string.IsNullOrEmpty(sentence) ? null : Find(sentence);
    }

    /// <summary>The newest line whose text is the mined sentence (or contains it, or is contained by it).</summary>
    public LineSpan? Find(string sentence)
    {
        var wanted = Normalize(sentence);
        if (wanted.Length == 0)
            return null;
        lock (gate)
        {
            for (var i = spans.Count - 1; i >= 0; i--)
            {
                var text = Normalize(spans[i].Line.Text);
                if (text == wanted)
                    return spans[i];
            }

            for (var i = spans.Count - 1; i >= 0; i--)
            {
                var text = Normalize(spans[i].Line.Text);
                if (text.Length > 0 && (text.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(text, StringComparison.Ordinal)))
                    return spans[i];
            }
        }

        return null;
    }

    private static string Normalize(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c) && c != '　').ToArray());
}
