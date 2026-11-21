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
        Translator = new Translator(config);
        capture.LineCaptured += OnLine;
        Frames.ClipReady += OnClip;
    }

    public GameFrames Frames { get; }
    public LineTimeline Timeline { get; } = new();
    public TtsClient Tts { get; }
    public AnkiConnect Anki { get; }
    public Translator Translator { get; }
    public GameAudioRecorder? Recorder => recorder;

    /// <summary>Whether anything beyond Hachidori's own note needs AnkiConnect.</summary>
    public bool AttachesMedia => config.RecordVoiceLines || Tts.Enabled || config.CaptureGif || config.TranslationEnabled;

    private void OnLine(GameLine line)
    {
        var now = Stopwatch.GetTimestamp();
        Timeline.OnLine(line, now);
        if (line.Source is LineSource.Talk or LineSource.BattleTalk)
            Frames.OnLineStarted(now);
    }

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
        var gifTask = config.CaptureGif ? AnimationAsync(span) : Task.FromResult<(byte[] Data, string Ext, string? Note)?>(null);
        var translationTask = config.TranslationEnabled ? TranslateAsync(span, sentence) : Task.FromResult<string?>(null);
        (byte[] Data, string Ext, string Kind)? audio = null;
        (byte[] Data, string Ext, string? Note)? gif = null;
        string? translation = null;
        try { audio = await audioTask.ConfigureAwait(false); }
        catch (Exception ex) { warnings.Add($"audio: {ex.Message}"); }
        try { gif = await gifTask.ConfigureAwait(false); }
        catch (Exception ex) { warnings.Add($"animation: {ex.Message}"); }
        try { translation = await translationTask.ConfigureAwait(false); }
        catch (Exception ex) { warnings.Add($"translation: {ex.Message}"); }
        if (audio is null && gif is null && translation is null)
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

        if (gif is { } anim)
        {
            if (anim.Note is not null)
                warnings.Add(anim.Note);
            var field = MediaFields.GifField(fields, config.GifField, screenshotFile, config.GifReplacesScreenshot);
            if (field is null)
            {
                warnings.Add("no GIF field (set it in Settings → Mining)");
            }
            else
            {
                var file = await Anki.StoreMediaAsync($"hachidori-ffxiv-{Guid.NewGuid():N}.{anim.Ext}", anim.Data).ConfigureAwait(false);
                var current = updates.TryGetValue(field, out var pending) ? pending : fields.First(f => f.Name == field).Value;
                updates[field] = MediaFields.WithGif(current, file, screenshotFile);
                notes.Add($"{anim.Ext.ToUpperInvariant()} ({anim.Data.Length / 1024} KB)");
            }
        }

        if (translation is not null)
        {
            var field = fields.FirstOrDefault(f => string.Equals(f.Name, config.TranslationField.Trim(), StringComparison.OrdinalIgnoreCase)).Name;
            if (field is null)
            {
                warnings.Add($"no “{config.TranslationField}” field for the translation");
            }
            else if (fields.First(f => f.Name == field).Value.Trim().Length == 0)
            {
                updates[field] = System.Net.WebUtility.HtmlEncode(translation).Replace("\n", "<br>");
                notes.Add("translation");
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

    /// <summary>The line with GSM-style dialogue context: up to N captured lines before and after it.</summary>
    private Task<string?> TranslateAsync(LineSpan? span, string sentence)
    {
        var history = capture.History.Where(l => l.Source is LineSource.Talk or LineSource.BattleTalk).ToList();
        var index = span is null ? -1 : history.IndexOf(span.Line);
        var n = config.DialogueContextLength;
        List<string> context;
        if (n == 0 || history.Count == 0)
            context = [];
        else if (n < 0)
            context = history.Select(l => l.Text).ToList();
        else if (index < 0)
            context = history.Skip(Math.Max(0, history.Count - n)).Select(l => l.Text).ToList();
        else
            context = history.Skip(Math.Max(0, index - n)).Take(Math.Min(history.Count, index + 1 + n) - Math.Max(0, index - n)).Select(l => l.Text).ToList();
        var text = span?.Line.Text ?? sentence;
        return Translator.TranslateAsync(text, context, span?.Line.Speaker ?? string.Empty, "FINAL FANTASY XIV")!;
    }

    private string? ffmpegPath;
    private string? ffmpegConfigured;
    private readonly LinkedList<(long Start, Task<(byte[] Data, string Ext, string? Note)> Encoded)> clips = new();

    /// <summary>Encodes each line's clip as soon as it is complete, so only small files are kept and mining is instant.</summary>
    private void OnClip(long start, List<GifFrame> frames)
    {
        var task = Task.Run(() => EncodeAsync(frames));
        lock (clips)
        {
            clips.AddLast((start, task));
            while (clips.Count > 6)
                clips.RemoveFirst();
        }
    }

    private async Task<(byte[] Data, string Ext, string? Note)> EncodeAsync(IReadOnlyList<GifFrame> frames)
    {
        var ffmpeg = config.AnimationFormat == AnimationFormat.Avif ? FfmpegPath : null;
        return await AnimationEncoder.EncodeAsync(frames, Math.Clamp(config.GifFps, 2, 24), config.AnimationFormat, ffmpeg,
            Math.Clamp(config.AvifCrf, 10, 55), Math.Clamp(config.WebpQuality, 30, 100)).ConfigureAwait(false);
    }

    /// <summary>FFmpeg for AVIF, looked up once per configured path.</summary>
    public string? FfmpegPath
    {
        get
        {
            if (ffmpegConfigured != config.FfmpegPath)
            {
                ffmpegConfigured = config.FfmpegPath;
                ffmpegPath = Ffmpeg.Find(config.FfmpegPath);
            }

            return ffmpegPath;
        }
    }

    private async Task<(byte[] Data, string Ext, string? Note)?> AnimationAsync(LineSpan? span)
    {
        if (span is not null)
        {
            Task<(byte[] Data, string Ext, string? Note)>? encoded = null;
            lock (clips)
                encoded = clips.FirstOrDefault(c => c.Start == span.Start).Encoded;
            if (encoded is not null)
                return await encoded.ConfigureAwait(false);
        }

        // Mined within the line's first seconds (or an old line): use what is buffered now.
        var from = span?.Start ?? Stopwatch.GetTimestamp() - Stopwatch.Frequency * (long)Math.Clamp(config.GifSeconds, 1, 10);
        var frames = Frames.FramesFrom(from);
        if (frames.Count < 2)
            throw new InvalidOperationException(Frames.Error ?? "no frames recorded yet");
        return await Task.Run(() => EncodeAsync(frames)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        capture.LineCaptured -= OnLine;
        Frames.ClipReady -= OnClip;
        recorder?.Dispose();
        Frames.Dispose();
        Tts.Dispose();
        Translator.Dispose();
        Anki.Dispose();
    }
}
