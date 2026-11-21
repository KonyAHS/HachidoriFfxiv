using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using HachidoriFfxiv.Capture;

namespace HachidoriFfxiv.Media;

/// <summary>
/// Card media from the game itself, replacing GSM: a screenshot (sent with the note through
/// Hachidori), the line's voice recorded from the game or spoken by a TTS engine, and a GIF of
/// the line. Audio and GIF are attached through AnkiConnect once Hachidori has added the note.
/// </summary>
public sealed class MediaMiner : IDisposable
{
    private readonly Configuration config;
    private readonly TextCapture capture;
    private GameAudioRecorder? recorder;

    public MediaMiner(Configuration config, TextCapture capture)
    {
        this.config = config;
        this.capture = capture;
        Frames = new GameFrames(config);
        Tts = new TtsClient(config);
        Anki = new AnkiConnect(() => config.AnkiConnectUrl);
        capture.LineCaptured += OnLine;
    }

    public GameFrames Frames { get; }
    public LineTimeline Timeline { get; } = new();
    public TtsClient Tts { get; }
    public AnkiConnect Anki { get; }
    public GameAudioRecorder? Recorder => recorder;

    /// <summary>Whether anything beyond Hachidori's own note needs AnkiConnect.</summary>
    public bool AttachesMedia => config.RecordVoiceLines || Tts.Enabled || config.CaptureGif;

    private void OnLine(GameLine line) => Timeline.OnLine(line, Stopwatch.GetTimestamp());

    /// <summary>Call once per frame from UiBuilder.Draw.</summary>
    public void Draw()
    {
        Frames.Draw();
        Timeline.OnWindows(capture.TalkVisible, capture.BattleTalkVisible, Stopwatch.GetTimestamp());
        if (config.RecordVoiceLines && recorder is null)
        {
            recorder = new GameAudioRecorder(Math.Clamp(config.VoiceBufferSeconds, 30, 300));
            recorder.Start();
        }
        else if (!config.RecordVoiceLines && recorder is not null)
        {
            recorder.Dispose();
            recorder = null;
        }
    }

    /// <summary>A JPEG of the game for {screenshot}, or null when it is turned off.</summary>
    public async Task<ScreenshotMedia?> ScreenshotAsync()
    {
        if (!config.CaptureScreenshot)
            return null;
        var jpeg = await Frames.ScreenshotAsync().ConfigureAwait(false);
        return new ScreenshotMedia(Guid.NewGuid().ToString("N"), $"hachidori-screenshot-{Guid.NewGuid():D}.jpg", Convert.ToBase64String(jpeg));
    }

    /// <summary>Adds the sentence audio and GIF to a note Hachidori just added. Returns a short summary.</summary>
    public async Task<string> AttachAsync(long noteId, string sentence, string? screenshotFile)
    {
        var span = Timeline.Find(sentence);
        var notes = new List<string>();
        var warnings = new List<string>();

        var audioTask = SentenceAudioAsync(span, sentence);
        var gifTask = config.CaptureGif ? GifAsync(span) : Task.FromResult<byte[]?>(null);
        (byte[] Data, string Ext, string Kind)? audio = null;
        byte[]? gif = null;
        try { audio = await audioTask.ConfigureAwait(false); }
        catch (Exception ex) { warnings.Add($"audio: {ex.Message}"); }
        try { gif = await gifTask.ConfigureAwait(false); }
        catch (Exception ex) { warnings.Add($"GIF: {ex.Message}"); }
        if (audio is null && gif is null)
            return warnings.Count > 0 ? string.Join("; ", warnings) : string.Empty;

        var fields = await Anki.FieldsAsync(noteId).ConfigureAwait(false);
        var updates = new Dictionary<string, string>();
        if (audio is { } a)
        {
            var field = MediaFields.SentenceAudioField(fields, config.SentenceAudioField);
            if (field is null)
            {
                warnings.Add("no sentence audio field (set it in Settings → Mining)");
            }
            else
            {
                var file = await Anki.StoreMediaAsync($"hachidori-ffxiv-{Guid.NewGuid():N}.{a.Ext}", a.Data).ConfigureAwait(false);
                var current = fields.First(f => f.Name == field).Value;
                updates[field] = current.Trim().Length == 0 ? $"[sound:{file}]" : $"{current}[sound:{file}]";
                notes.Add(a.Kind);
            }
        }

        if (gif is not null)
        {
            var field = MediaFields.GifField(fields, config.GifField, screenshotFile, config.GifReplacesScreenshot);
            if (field is null)
            {
                warnings.Add("no GIF field (set it in Settings → Mining)");
            }
            else
            {
                var file = await Anki.StoreMediaAsync($"hachidori-ffxiv-{Guid.NewGuid():N}.gif", gif).ConfigureAwait(false);
                var current = updates.TryGetValue(field, out var pending) ? pending : fields.First(f => f.Name == field).Value;
                updates[field] = MediaFields.WithGif(current, file, screenshotFile);
                notes.Add("GIF");
            }
        }

        if (updates.Count > 0)
            await Anki.UpdateFieldsAsync(noteId, updates).ConfigureAwait(false);
        var summary = notes.Count > 0 ? $"Added {string.Join(" and ", notes)}." : string.Empty;
        return warnings.Count > 0 ? $"{summary} Skipped {string.Join("; ", warnings)}.".Trim() : summary;
    }

    /// <summary>The game's voice for the line, else TTS for the sentence, as (bytes, extension, description).</summary>
    private async Task<(byte[] Data, string Ext, string Kind)?> SentenceAudioAsync(LineSpan? span, string sentence)
    {
        if (config.RecordVoiceLines && recorder is { Running: true } rec && span is not null)
        {
            var clip = await Task.Run(() => VoiceClip(rec.Ring, span)).ConfigureAwait(false);
            if (clip is not null)
                return (clip, "ogg", "voice line");
        }

        if (!Tts.Enabled)
            return null;
        var text = span?.Line.Text ?? sentence;
        var speech = await Tts.SpeakAsync(text).ConfigureAwait(false);
        if (AudioClips.Extension(speech) == "wav")
        {
            var (samples, rate) = AudioClips.DecodeWav(speech);
            return (AudioClips.EncodeOpus(AudioClips.Normalize(AudioClips.Trim(samples, rate)), rate), "ogg", "TTS audio");
        }

        return (speech, AudioClips.Extension(speech) ?? "mp3", "TTS audio");
    }

    /// <summary>The line's audio from the game recording, or null when the line was not voiced.</summary>
    private byte[]? VoiceClip(AudioRing ring, LineSpan span)
    {
        var rate = ring.SampleRate;
        var start = ring.IndexAt(span.Start) - rate * 3 / 10;
        var endTicks = span.End ?? Stopwatch.GetTimestamp();
        var end = Math.Min(ring.IndexAt(endTicks) + rate / 2, start + rate * (long)Math.Clamp(config.MaxLineSeconds, 3, 60));
        var before = ring.Read(start - rate * 2, start);
        var clip = ring.Read(start, end);
        if (!AudioClips.IsVoiced(before, clip, rate, Math.Clamp(config.VoiceDetectionRatio, 1.1f, 10f)))
            return null;
        return AudioClips.EncodeOpus(AudioClips.Normalize(AudioClips.Trim(clip, rate)), rate);
    }

    private async Task<byte[]?> GifAsync(LineSpan? span)
    {
        var from = span?.Start ?? Stopwatch.GetTimestamp() - Stopwatch.Frequency * (long)Math.Clamp(config.GifSeconds, 1, 10);
        var frames = Frames.FramesFrom(from);
        if (frames.Count < 2)
            throw new InvalidOperationException(Frames.Error ?? "no frames recorded yet");
        var delay = 1000 / Math.Clamp(config.GifFps, 2, 20);
        return await Task.Run(() => GifEncoder.Encode(frames, delay)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        capture.LineCaptured -= OnLine;
        recorder?.Dispose();
        Frames.Dispose();
        Tts.Dispose();
        Anki.Dispose();
    }
}
