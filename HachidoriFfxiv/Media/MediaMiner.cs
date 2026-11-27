using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HachidoriFfxiv.Capture;
using HachidoriFfxiv.Hachidori;

namespace HachidoriFfxiv.Media;

/// <summary>Summary of cached media available for a captured dialogue line.</summary>
public readonly record struct LineMediaStatus(bool HasScreenshot, bool HasVoice, bool HasClip);

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

    public MediaMiner(Configuration config, TextCapture capture, Func<string?>? ankiApiKey = null)
    {
        this.config = config;
        this.capture = capture;
        Frames = new GameFrames(config)
        {
            IsLineStillOpen = id => Timeline.FindById(id) is { } s && Timeline.EndOf(s) is null,
        };
        Voices = new VoiceLineTracker(config.VoiceLoadSoundFileSigOverride, config.VoicePlaySpecificSoundSigOverride);
        Tts = new TtsClient(config);
        Anki = new AnkiConnect(() => config.AnkiConnectUrl, ankiApiKey);
        Translator = new Translator(config);
        capture.LineCaptured += OnLine;
        Voices.VoicePlayed += OnVoicePlayed;
        Frames.ClipReady += OnClip;
        Frames.LineScreenshotReady += OnLineScreenshot;
    }

    public GameFrames Frames { get; }
    public LineTimeline Timeline { get; } = new();
    public VoiceLineTracker Voices { get; }
    public BgmTracker Bgm { get; } = new();
    public PluginAudioTracker PluginAudio { get; } = new();
    public TtsClient Tts { get; }
    public AnkiConnect Anki { get; }
    public Translator Translator { get; }
    public GameAudioRecorder? Recorder => recorder;

    /// <summary>Whether anything beyond Hachidori's own note needs AnkiConnect.</summary>
    public bool AttachesMedia => config.RecordVoiceLines || Tts.Enabled || config.CaptureGif || config.TranslationEnabled;

    private void OnVoicePlayed(VoicePlaybackEvent voice) => Timeline.OnVoicePlayed(voice);

    private void OnLineScreenshot(long lineId, byte[] jpeg) => Timeline.SetScreenshot(lineId, jpeg);

    private void OnLine(GameLine line)
    {
        var now = Stopwatch.GetTimestamp();
        BgmSnapshot? bgm = null;
        if (line.Source is LineSource.Talk or LineSource.BattleTalk)
        {
            if (Tts.Enabled && config.TtsInjectOst)
                bgm = Bgm.CaptureNow(now);
            Frames.OnLineStarted(now, line.Id);
        }
        Timeline.OnLine(line, now, bgm);
    }

    /// <summary>Call once per frame from UiBuilder.Draw.</summary>
    public void Draw()
    {
        var now = Stopwatch.GetTimestamp();
        Frames.Draw();
        Timeline.OnWindows(capture.TalkVisible, capture.BattleTalkVisible, now);
        if (config.RecordVoiceLines)
            Voices.Update(now);
        if (Tts.Enabled && config.TtsInjectOst)
            Bgm.Update(now);
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

    /// <summary>Cached media badges for one captured line, for the backlog drawer.</summary>
    public LineMediaStatus GetLineMediaStatus(long lineId)
    {
        var span = Timeline.FindById(lineId);
        if (span is null)
            return default;
        var hasShot = Timeline.ScreenshotOf(span) is { Length: > 0 };
        var hasVoice = Timeline.VoiceOf(span) is not null;
        bool hasClip;
        lock (clips)
            hasClip = clips.Any(c => c.Start == span.Start);
        return new LineMediaStatus(hasShot, hasVoice, hasClip);
    }

    /// <summary>The cached JPEG captured while <paramref name="lineId"/> was on screen, if still retained.</summary>
    public byte[]? GetLineScreenshot(long lineId) =>
        Timeline.FindById(lineId) is { } span ? Timeline.ScreenshotOf(span) : null;

    /// <summary>
    /// Decodes playable audio for replaying a captured line in the backlog (prefers clean native
    /// SCD voice extraction, else loopback recording, else TTS when enabled).
    /// </summary>
    public async Task<byte[]?> GetLineVoiceReplayAsync(long lineId)
    {
        var span = Timeline.FindById(lineId);
        if (span is null)
            return null;

        var voice = Timeline.VoiceOf(span);
        if (voice is not null)
        {
            var clean = await Task.Run(() => TryExtractCleanVoiceFromScd(voice, "Backlog voice replay")).ConfigureAwait(false);
            if (clean is not null)
                return clean;
        }

        if (config.RecordVoiceLines && recorder is { Running: true } rec)
        {
            var clip = await Task.Run(() => VoiceClipAsync(rec, span)).ConfigureAwait(false);
            if (clip is not null)
                return clip;
        }

        if (Tts.Enabled)
        {
            var bgmSnapshot = ResolveBgmForTts(span);
            var (data, _, _, _) = await SynthesizeTtsAsync(span.Line.Text, bgmSnapshot).ConfigureAwait(false);
            return data;
        }

        return null;
    }

    /// <summary>
    /// A JPEG of the game for {screenshot}, or null when it is turned off.
    /// When mining a past dialogue line whose window has already moved on, returns that line's
    /// cached JPEG instead of capturing the unrelated current screen.
    /// </summary>
    public async Task<ScreenshotMedia?> ScreenshotAsync(long? lineId = null, string? sentence = null)
    {
        if (!config.CaptureScreenshot)
            return null;

        var span = Timeline.Resolve(lineId, sentence);
        if (span is not null && Timeline.EndOf(span) is not null)
        {
            var cached = Timeline.ScreenshotOf(span);
            if (cached is { Length: > 0 })
                return MakeScreenshotMedia(cached);
            // The line already ended and its cached still was evicted or never captured:
            // avoid attaching an unrelated current-screen frame.
            return null;
        }

        try
        {
            var jpeg = await Frames.ScreenshotAsync().ConfigureAwait(false);
            return MakeScreenshotMedia(jpeg);
        }
        catch when (span is not null && Timeline.ScreenshotOf(span) is { Length: > 0 } fallback)
        {
            return MakeScreenshotMedia(fallback);
        }
    }

    private static ScreenshotMedia MakeScreenshotMedia(byte[] jpeg) =>
        new(Guid.NewGuid().ToString("N"), $"hachidori-screenshot-{Guid.NewGuid():D}.jpg", Convert.ToBase64String(jpeg));

    /// <summary>Adds the sentence audio and GIF to a note Hachidori just added. Returns a short summary.</summary>
    public async Task<string> AttachAsync(long noteId, string sentence, string? screenshotFile, long? lineId = null)
    {
        var span = Timeline.Resolve(lineId, sentence);
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

    /// <summary>The game's voice for the line, else TTS for the sentence (optionally mixed with clean OST), as (bytes, extension, description).</summary>
    private async Task<(byte[] Data, string Ext, string Kind)?> SentenceAudioAsync(LineSpan? span, string sentence)
    {
        if (config.RecordVoiceLines && recorder is { Running: true } rec && span is not null)
        {
            var clip = await Task.Run(() => VoiceClipAsync(rec, span)).ConfigureAwait(false);
            if (clip is not null)
                return (clip, "ogg", "voice line");
        }

        if (!Tts.Enabled)
            return null;
        var text = span?.Line.Text ?? sentence;
        var bgmSnapshot = ResolveBgmForTts(span);
        var (data, ext, mixedOst, _) = await SynthesizeTtsAsync(text, bgmSnapshot).ConfigureAwait(false);
        return (data, ext, mixedOst ? "TTS + OST audio" : "TTS audio");
    }

    /// <summary>Tests TTS synthesis (and clean OST injection when enabled) for Settings → Mining.</summary>
    public async Task<(byte[] Data, string Ext, bool MixedOst, BgmSnapshot? Bgm)> TestTtsAsync(string text)
    {
        var bgmSnapshot = config.TtsInjectOst ? Bgm.CurrentAt(Stopwatch.GetTimestamp()) : null;
        return await SynthesizeTtsAsync(text, bgmSnapshot).ConfigureAwait(false);
    }

    private BgmSnapshot? ResolveBgmForTts(LineSpan? span)
    {
        if (!config.TtsInjectOst)
            return null;
        return span?.Bgm ?? Bgm.CurrentAt(Stopwatch.GetTimestamp());
    }

    private async Task<(byte[] Data, string Ext, bool MixedOst, BgmSnapshot? Bgm)> SynthesizeTtsAsync(string text, BgmSnapshot? bgmSnapshot)
    {
        var speech = await Tts.SpeakAsync(text).ConfigureAwait(false);
        var decoded = AudioClips.DecodeAudio(speech);
        if (decoded is null)
            return (speech, AudioClips.Extension(speech) ?? "mp3", false, null);

        var (samples, rate) = decoded.Value;
        if (config.TtsInjectOst)
        {
            if (bgmSnapshot is null)
            {
                DiagnosticLog.Write("ost", "Skipped OST mix into TTS: no active BGM track detected.");
            }
            else if (config.TtsOstRespectMute && bgmSnapshot.Muted)
            {
                DiagnosticLog.Write("ost", $"Skipped OST mix into TTS: game BGM is muted ({bgmSnapshot.DisplayName}).");
            }
            else
            {
                const int outputRate = 48000;
                var (_, _, _, totalSamples) = AudioClips.PrepareTtsForOst(samples, rate, outputRate);
                var ostStartSeconds = Math.Max(0.0, bgmSnapshot.ElapsedSeconds - AudioClips.OstPreRollSeconds);
                var ostSlice = await Task.Run(() => Bgm.ExtractSlice(bgmSnapshot, ostStartSeconds, totalSamples, outputRate)).ConfigureAwait(false);
                if (ostSlice is null)
                {
                    DiagnosticLog.Write("ost", $"Skipped OST mix into TTS: failed to extract slice from {bgmSnapshot.FilePath} @ {ostStartSeconds:0.0}s.");
                }
                else
                {
                    var (mixed, mixedOst) = AudioClips.MixTtsWithOst(samples, rate, ostSlice, config.TtsOstVolume, outputRate);
                    if (mixedOst)
                    {
                        DiagnosticLog.Write("ost", $"Mixed {bgmSnapshot.DisplayName} @ {ostStartSeconds:0.0}s ({(bgmSnapshot.FromEngineClock ? "engine" : "wall")} clock) into TTS ({mixed.Length / (double)outputRate:0.0}s).");
                        return (AudioClips.EncodeOpus(mixed, outputRate, bitrate: 64000), "ogg", true, bgmSnapshot);
                    }
                    DiagnosticLog.Write("ost", $"Skipped OST mix into TTS: extracted OST slice was silent ({bgmSnapshot.DisplayName} @ {ostStartSeconds:0.0}s).");
                }
            }
        }

        if (AudioClips.Extension(speech) == "wav")
            return (AudioClips.EncodeOpus(AudioClips.Normalize(AudioClips.Trim(samples, rate)), rate), "ogg", false, null);

        return (speech, AudioClips.Extension(speech) ?? "mp3", false, null);
    }

    /// <summary>The line's audio from the game recording, or null when the line was not voiced.</summary>
    private async Task<byte[]?> VoiceClipAsync(GameAudioRecorder rec, LineSpan span)
    {
        var ring = rec.Ring;
        if (ring.Written == 0)
            return null;

        // Primary path: authoritative native voice playback events (LoadSoundFile + PlaySpecificSound hooks
        // and SoundManager active-pool companion, matching TextToTalk / Echokraut).
        var detectedVoice = Timeline.VoiceOf(span);
        if (Voices.HooksActive)
        {
            // If mined right after the line appeared, wait briefly for a delayed voice onset while the line stays open.
            var waitUntil = span.Start + (long)(1.8 * Stopwatch.Frequency);
            while (detectedVoice is null && rec.Running && Timeline.EndOf(span) is null && Stopwatch.GetTimestamp() < waitUntil)
            {
                await Task.Delay(80).ConfigureAwait(false);
                detectedVoice = Timeline.VoiceOf(span);
            }

            if (detectedVoice is null)
            {
                DiagnosticLog.Write("voice", $"No native voice matched {span.Line.Source} span (chars={span.Line.Text.Length}); falling back to TTS.");
                return null;
            }

            return await RecordConfirmedVoiceAsync(rec, span, detectedVoice).ConfigureAwait(false);
        }

        if (detectedVoice is not null)
            return await RecordConfirmedVoiceAsync(rec, span, detectedVoice).ConfigureAwait(false);

        // Fallback when native sound hooks are unavailable: RMS voice-over-background detector.
        var rate = ring.SampleRate;
        var frameSize = rate / AudioClips.FramesPerSecond;
        var start = ring.IndexAt(span.Start) - rate * 3 / 10;
        var startTicks = span.Start - (long)(0.3 * Stopwatch.Frequency);
        var maxEnd = start + rate * (long)Math.Clamp(config.MaxLineSeconds, 3, 60);
        var beforeSamples = ring.Read(start - rate * 2, start);
        PluginAudio.MaskOverlappingSamples(beforeSamples, startTicks - 2 * Stopwatch.Frequency, rate);
        var before = AudioClips.FrameLevels(beforeSamples, rate);
        var ratio = Math.Clamp(config.VoiceDetectionRatio, 1.1f, 10f);
        var levels = new List<float>();
        while (rec.Running && Math.Max(0, ring.Written - ring.Capacity) <= Math.Max(0, start))
        {
            var lineEnd = Timeline.EndOf(span);
            var boundary = lineEnd is { } ticks ? ring.IndexAt(ticks) : long.MaxValue;
            var limit = Math.Min(boundary, maxEnd);
            // Allow packets to arrive before treating missing samples as quiet.
            var recordedThrough = ring.IndexAt(Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10);
            var scanEnd = Math.Min(limit, recordedThrough);
            var count = (int)(Math.Max(0, scanEnd - start) / frameSize);
            if (count > levels.Count)
            {
                var chunkOffset = levels.Count * frameSize;
                var samples = ring.Read(start + chunkOffset, start + count * frameSize);
                var chunkStartTicks = startTicks + (long)(chunkOffset * (double)Stopwatch.Frequency / rate);
                PluginAudio.MaskOverlappingSamples(samples, chunkStartTicks, rate);
                levels.AddRange(AudioClips.FrameLevels(samples, rate));
            }

            var voice = AudioClips.FindVoice(before, CollectionsMarshal.AsSpan(levels)[..count], ratio);
            if (voice is { } range && (scanEnd >= limit || range.Finished))
            {
                var from = start + range.Start * frameSize - rate;
                var to = start + range.End * frameSize + rate;
                var availableEnd = Math.Min(to, boundary);
                if (recordedThrough >= availableEnd)
                {
                    var clip = ring.Read(from, availableEnd);
                    // Fill any padding after the dialogue ends with silence.
                    Array.Resize(ref clip, (int)(to - from));
                    return AudioClips.EncodeOpus(AudioClips.Normalize(clip), rate);
                }
            }
            else if (voice is null && (scanEnd >= limit || count >= AudioClips.VoiceOnsetFrames))
            {
                return null;
            }

            // If mined during playback, wait for the rest of the line and its padding.
            await Task.Delay(150).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Cuts a game-audio clip for a line confirmed voiced by FFXIV's sound engine, using exact
    /// voice start time and SCD duration / SoundManager stop time so consecutive voiced lines
    /// or loud BGM never trigger a false fallback to TTS.
    /// </summary>
    private async Task<byte[]?> RecordConfirmedVoiceAsync(GameAudioRecorder rec, LineSpan span, VoicePlaybackEvent voice)
    {
        var ring = rec.Ring;
        var rate = ring.SampleRate;
        var voiceStartIdx = ring.IndexAt(voice.StartTicks);
        var from = Math.Max(0, voiceStartIdx - rate * 3 / 10);
        var maxEnd = voiceStartIdx + rate * (long)Math.Clamp(config.MaxLineSeconds, 3, 60);

        // Give the async SCD header lookup a short window to supply exact clip duration.
        if (voice.DurationSeconds is null)
        {
            var durDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
            while (voice.DurationSeconds is null && Stopwatch.GetTimestamp() < durDeadline)
                await Task.Delay(25).ConfigureAwait(false);
        }

        long finalEnd = from;
        while (rec.Running && Math.Max(0, ring.Written - ring.Capacity) <= from)
        {
            var lineEnd = Timeline.EndOf(span);
            var boundary = lineEnd is { } ticks ? ring.IndexAt(ticks) : long.MaxValue;
            var limit = Math.Min(boundary, maxEnd);
            var recordedThrough = ring.IndexAt(Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10);

            if (voice.DurationSeconds is { } dur)
            {
                var desiredEnd = voiceStartIdx + (long)Math.Round((dur + 0.6) * rate);
                var targetEnd = Math.Min(limit, desiredEnd);
                if (recordedThrough >= targetEnd)
                {
                    finalEnd = targetEnd;
                    break;
                }
            }
            else if (voice.EndTicks is { } endTicks)
            {
                var desiredEnd = ring.IndexAt(endTicks) + rate * 5 / 10;
                var targetEnd = Math.Min(limit, desiredEnd);
                if (recordedThrough >= targetEnd)
                {
                    finalEnd = targetEnd;
                    break;
                }
            }
            else
            {
                if (lineEnd is not null && recordedThrough >= limit)
                {
                    finalEnd = limit;
                    break;
                }
                if (recordedThrough >= maxEnd)
                {
                    finalEnd = maxEnd;
                    break;
                }
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        if (finalEnd <= from)
        {
            var cleanScd = TryExtractCleanVoiceFromScd(voice, "Loopback buffer aged out");
            if (cleanScd is not null)
                return cleanScd;
            DiagnosticLog.Write("voice", $"No loopback audio window available for {voice.DisplayName} (evicted or ended before start); falling back to TTS.");
            return null;
        }

        // If the plugin played pronunciation audio while this voice line was being captured,
        // process/system loopback will contain that pronunciation mixed over the game voice.
        // Extract the clean voice directly from FFXIV's .scd file instead.
        var clipFromTicks = voice.StartTicks + (long)((from - voiceStartIdx) * (double)Stopwatch.Frequency / rate);
        var clipEndTicks = voice.StartTicks + (long)((finalEnd - voiceStartIdx) * (double)Stopwatch.Frequency / rate);
        if (PluginAudio.Overlaps(clipFromTicks, clipEndTicks))
        {
            var cleanScd = TryExtractCleanVoiceFromScd(voice, "Plugin pronunciation overlapped loopback");
            if (cleanScd is not null)
                return cleanScd;
            DiagnosticLog.Write("voice", $"Plugin pronunciation overlapped loopback for {voice.DisplayName}, and direct SCD decode was unavailable; using loopback clip.");
        }

        var clip = ring.Read(from, finalEnd);
        var peak = 0f;
        foreach (var s in clip)
            peak = Math.Max(peak, Math.Abs(s));
        if (peak <= 1e-4f)
        {
            DiagnosticLog.Write("voice", $"Loopback clip for {voice.DisplayName} was digital silence (game muted?); falling back to TTS.");
            return null;
        }

        DiagnosticLog.Write("voice", $"Recorded game voice {voice.DisplayName} ({clip.Length / (double)rate:0.0}s, scdDur={(voice.DurationSeconds is { } d ? $"{d:0.0}s" : "n/a")}).");
        return AudioClips.EncodeOpus(AudioClips.Normalize(clip), rate);
    }

    private static byte[]? TryExtractCleanVoiceFromScd(VoicePlaybackEvent voice, string reason)
    {
        try
        {
            var scd = DalamudServices.DataManager?.GetFile<Lumina.Data.Files.ScdFile>(voice.FilePath);
            if (scd is null)
                return null;
            var decoded = ScdVoiceExtractor.TryDecode(scd);
            if (decoded is not { } audio || audio.Samples.Length == 0)
                return null;
            var samples48k = AudioClips.Resample(audio.Samples, audio.SampleRate, 48000);
            var peak = 0f;
            foreach (var s in samples48k)
                peak = Math.Max(peak, Math.Abs(s));
            if (peak <= 1e-4f)
                return null;
            DiagnosticLog.Write("voice", $"{reason} for {voice.DisplayName}; extracted clean voice directly from SCD ({samples48k.Length / 48000.0:0.0}s).");
            return AudioClips.EncodeOpus(AudioClips.Normalize(samples48k), 48000);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("voice", $"Clean SCD voice extraction failed for {voice.DisplayName}: {ex.Message}");
            return null;
        }
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
    private const int MaxCachedClips = 20;
    private readonly LinkedList<(long Start, Task<(byte[] Data, string Ext, string? Note)> Encoded, CancellationTokenSource Cancel)> clips = new();

    // At most this many background clip encodes (each may be an FFmpeg process) run at once.
    // Clips wait their turn holding only their raw frames, and the cache limit above bounds how many wait.
    private readonly SemaphoreSlim clipEncodeSlots = new(2, 2);

    /// <summary>Encodes each line's clip as soon as it is complete, so only small files are kept and mining is instant.</summary>
    private void OnClip(long start, List<GifFrame> frames)
    {
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => EncodeClipAsync(frames, cts.Token));
        lock (clips)
        {
            clips.AddLast((start, task, cts));
            while (clips.Count > MaxCachedClips)
            {
                // An evicted clip can no longer be mined: stop its encode (and FFmpeg) and drop its frames.
                var evicted = clips.First!.Value;
                clips.RemoveFirst();
                CancelClip(evicted.Encoded, evicted.Cancel);
            }
        }
    }

    private static void CancelClip(Task encoded, CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        _ = encoded.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
    }

    private async Task<(byte[] Data, string Ext, string? Note)> EncodeClipAsync(List<GifFrame> frames, CancellationToken ct)
    {
        await clipEncodeSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await EncodeAsync(frames, ct).ConfigureAwait(false);
        }
        finally
        {
            clipEncodeSlots.Release();
        }
    }

    private async Task<(byte[] Data, string Ext, string? Note)> EncodeAsync(IReadOnlyList<GifFrame> frames, CancellationToken ct = default)
    {
        var ffmpeg = config.AnimationFormat != AnimationFormat.Gif ? FfmpegPath : null;
        return await AnimationEncoder.EncodeAsync(frames, Math.Clamp(config.GifFps, 2, 24), config.AnimationFormat, ffmpeg,
            Math.Clamp(config.AvifCrf, 10, 55), Math.Clamp(config.WebpQuality, 30, 100), ct).ConfigureAwait(false);
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
            {
                try
                {
                    return await encoded.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Evicted while this mine was looking it up: encode from the buffer below.
                }
            }
        }

        // Mined within the line's first seconds: use frames from that line's start time.
        // If a historical line already ended and its clip was evicted, skip animation so its
        // cached still screenshot is kept rather than attaching frames from the live scene.
        var historicalEnded = span is not null && Timeline.EndOf(span) is not null;
        var from = span?.Start ?? Stopwatch.GetTimestamp() - Stopwatch.Frequency * (long)Math.Clamp(config.GifSeconds, 1, 10);
        var frames = Frames.FramesFrom(from, allowLatestFallback: !historicalEnded);
        if (frames.Count < 2)
        {
            if (historicalEnded)
                return null;
            throw new InvalidOperationException(Frames.Error ?? "no frames recorded yet");
        }
        return await Task.Run(() => EncodeAsync(frames)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        capture.LineCaptured -= OnLine;
        Voices.VoicePlayed -= OnVoicePlayed;
        Frames.ClipReady -= OnClip;
        Frames.LineScreenshotReady -= OnLineScreenshot;
        lock (clips)
        {
            foreach (var clip in clips)
                CancelClip(clip.Encoded, clip.Cancel);
            clips.Clear();
        }
        recorder?.Dispose();
        Voices.Dispose();
        Frames.Dispose();
        Tts.Dispose();
        Translator.Dispose();
        Anki.Dispose();
    }
}
