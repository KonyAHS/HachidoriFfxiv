using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Concentus.Oggfile;
using HachidoriFfxiv;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Media;
using NAudio.Wave;

/// <summary>Card media: audio ring, voice detection, Opus, GIF, Anki fields and the screenshot envelope.</summary>
static class MediaChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const int rate = 48000;
        float[] Tone(double seconds, float amplitude, double hz) =>
            Enumerable.Range(0, (int)(seconds * rate)).Select(i => amplitude * (float)Math.Sin(2 * Math.PI * hz * i / rate)).ToArray();
        float[] Mix(float[] a, float[] b) => a.Select((v, i) => v + (i < b.Length ? b[i] : 0)).ToArray();

        // ------------------------------------------------------------ ring buffer
        var ring = new AudioRing(rate, 2);
        var t0 = Stopwatch.GetTimestamp();
        ring.Write(Enumerable.Repeat(0.5f, rate / 10).ToArray(), t0);
        ring.Write(Enumerable.Repeat(0.25f, rate / 10).ToArray(), t0 + Stopwatch.Frequency / 2); // 0.4 s of silence between
        var gap = ring.Read(0, rate * 6 / 10);
        check(gap[rate / 20] == 0.5f && gap[rate / 4] == 0 && gap[rate / 2 + 10] == 0.25f && ring.Written == rate * 6 / 10,
            "audio ring fills silent gaps from packet timestamps");
        ring.Write(Enumerable.Repeat(0.1f, rate * 3).ToArray(), t0 + Stopwatch.Frequency);
        var old = ring.Read(0, rate / 10);
        check(old.All(v => v == 0) && ring.Read(ring.Written - 10, ring.Written).All(v => v == 0.1f),
            "audio ring forgets samples older than its capacity");

        // ------------------------------------------------------- voice detection
        var music = Tone(2, 0.05f, 220);
        var voiced = Mix(Tone(2, 0.05f, 220), Tone(2, 0.3f, 180));
        (int Start, int End, bool Finished)? FindVoice(float[] before, float[] samples) =>
            AudioClips.FindVoice(AudioClips.FrameLevels(before, rate), AudioClips.FrameLevels(samples, rate), 2f);
        check(FindVoice(music, voiced) is { Start: 0, End: 100, Finished: false }, "a voice over music is detected while it is still playing");
        check(FindVoice(music, music) is null, "music alone is not a voice line");
        check(FindVoice(new float[rate], new float[rate]) is null, "silence is not a voice line");
        check(FindVoice(new float[rate], Tone(1, 0.2f, 200)) is { Start: 0, End: 50 }, "a voice after silence counts as voiced");

        // A line starts just after its text, pauses mid-sentence, and ends quietly. The
        // music continues through a late card click and a loud sound unrelated to the line.
        var speech = new float[rate * 4 / 10]
            .Concat(Tone(0.6, 0.3f, 180))
            .Concat(new float[rate * 8 / 10])
            .Concat(Tone(0.4, 0.3f, 180))
            .Concat(Tone(0.4, 0.06f, 180))
            .ToArray();
        var recording = Mix(Tone(8, 0.05f, 220), speech);
        var effect = Tone(0.5, 0.5f, 200);
        for (var i = 0; i < effect.Length; i++)
            recording[rate * 6 + i] += effect[i];
        var early = FindVoice(music, recording[..(rate * 3 / 2)]);
        var afterSpeech = FindVoice(music, recording[..(rate * 4)]);
        var late = FindVoice(music, recording);
        check(early is { Finished: false }, "mining during a pause keeps waiting for the voice line to finish");
        check(afterSpeech is { Start: 20, End: 130, Finished: true } && late == afterSpeech,
            "a late card click keeps the same speech boundaries over music and ignores later sound effects");
        check(FindVoice(music, Mix(Tone(3, 0.05f, 220), Tone(0.02, 0.6f, 200))) is null,
            "a brief sound effect near the start does not count as a voice line");

        var padded = new float[rate].Concat(Tone(1, 0.3f, 200)).Concat(new float[rate]).ToArray();
        var trimmed = AudioClips.Trim(padded, rate);
        check(trimmed.Length < rate * 1.5 && trimmed.Length > rate, "TTS clips are trimmed to the speech with a little padding");
        check(AudioClips.Normalize(Tone(1, 0.1f, 200)).Max() is > 0.38f and < 0.41f, "quiet clips are levelled up, at most 4×");

        // ------------------------------------------------------------------ Opus
        var clip = Tone(1.5, 0.3f, 300);
        var opus = AudioClips.EncodeOpus(clip, rate);
        var decoded = 0;
        var reader = new OpusOggReadStream(Concentus.OpusCodecFactory.CreateDecoder(48000, 1), new MemoryStream(opus));
        while (reader.HasNextPacket)
            decoded += reader.DecodeNextPacket()?.Length ?? 0;
        check(opus.AsSpan(0, 4).SequenceEqual("OggS"u8) && Math.Abs(decoded - clip.Length) < rate / 10,
            "voice lines encode to Ogg Opus of the right length");

        var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(wav, new WaveFormat(24000, 16, 2)))
        {
            var stereo = Tone(0.5, 0.2f, 300).Select(v => (short)(v * 32767)).SelectMany(v => new[] { v, v }).ToArray();
            writer.WriteSamples(stereo, 0, stereo.Length);
        }

        var (wavSamples, wavRate) = AudioClips.DecodeWav(wav.ToArray());
        // 24000 stereo frames (Tone counts samples at 48 kHz) become 24000 mono samples.
        check(wavRate == 24000 && wavSamples.Length == 24000 && AudioClips.Extension(wav.ToArray()) == "wav",
            "TTS WAV decodes to mono samples");

        // ----------------------------------------------------- OST extraction & TTS mixing
        CheckOstExtractionAndMixing(check);
        CheckBgmSceneSelectionAndVoiceDetection(check);
        CheckPluginAudioAndScdVoiceExtraction(check);

        // ------------------------------------------------------------------- GIF
        var frames = Enumerable.Range(0, 3).Select(f =>
        {
            var rgba = new byte[64 * 32 * 4];
            for (var y = 0; y < 32; y++)
                for (var x = 0; x < 64; x++)
                {
                    var i = (y * 64 + x) * 4;
                    rgba[i] = (byte)(x * 4);
                    rgba[i + 1] = (byte)(y * 8);
                    rgba[i + 2] = (byte)(f * 100);
                    rgba[i + 3] = 255;
                }

            return new GifFrame(rgba, 64, 32);
        }).ToList();
        var gif = GifEncoder.Encode(frames, 125);
        var decodedFrames = DecodeGif(gif);
        check(gif.AsSpan(0, 6).SequenceEqual("GIF89a"u8) && gif[^1] == 0x3B && decodedFrames.Count == 3,
            "GIFs have a header, three frames and a trailer");
        var worst = 0;
        for (var f = 0; f < 3; f++)
            for (var p = 0; p < 64 * 32; p++)
                for (var c = 0; c < 3; c++)
                    worst = Math.Max(worst, Math.Abs(decodedFrames[f][p * 3 + c] - frames[f].Rgba[p * 4 + c]));
        check(worst <= 24, $"GIF frames decode back to their colours (worst channel error {worst})");
        // Captured frames are packed in place, so their arrays can run past the image.
        var longFrames = frames.Select(f => new GifFrame([.. f.Rgba, .. Enumerable.Repeat((byte)0xAB, 4096)], f.Width, f.Height)).ToList();
        check(GifEncoder.Encode(longFrames, 125).AsSpan().SequenceEqual(gif), "GIF frames with bytes past the image encode the same");

        // ------------------------------------------------------ AVIF / WebP / GIF
        var noFfmpeg = await AnimationEncoder.EncodeAsync(frames, 8, AnimationFormat.Avif, null, 32, 75);
        check(noFfmpeg.Extension == "gif" && noFfmpeg.Note?.Contains("FFmpeg not found") == true,
            "without FFmpeg, AVIF falls back to the built-in GIF and says so");
        var ffmpeg = Ffmpeg.Find(string.Empty);
        if (ffmpeg is null || Ffmpeg.Capabilities(ffmpeg).Av1 is null)
        {
            Console.WriteLine("SKIP: AVIF and WebP encoding (no FFmpeg with AV1 on this machine)");
        }
        else
        {
            var big = Enumerable.Range(0, 24).Select(f =>
            {
                var rgba = new byte[640 * 360 * 4];
                for (var p = 0; p < 640 * 360; p++)
                {
                    rgba[p * 4] = (byte)((p % 640 + f * 8) % 256);
                    rgba[p * 4 + 1] = (byte)(p / 640 * 255 / 360);
                    rgba[p * 4 + 2] = (byte)(f * 10);
                    rgba[p * 4 + 3] = 255;
                }

                return new GifFrame(rgba, 640, 360);
            }).ToList();
            var avif = await AnimationEncoder.EncodeAsync(big, 12, AnimationFormat.Avif, ffmpeg, 32, 75);
            var webp = await AnimationEncoder.EncodeAsync(big, 12, AnimationFormat.Webp, ffmpeg, 32, 75);
            var gif2 = await AnimationEncoder.EncodeAsync(big, 12, AnimationFormat.Gif, ffmpeg, 32, 75);
            Console.WriteLine($"  640x360x24: AVIF {avif.Data.Length / 1024} KB, WebP {webp.Data.Length / 1024} KB, GIF {gif2.Data.Length / 1024} KB");
            check(avif.Extension == "avif" && avif.Note is null && avif.Data.AsSpan(4, 8).SequenceEqual("ftypavis"u8),
                "AVIF animations are AV1 image sequences (avis)");
            check(webp.Extension == "webp" && webp.Data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && webp.Data.AsSpan(8, 4).SequenceEqual("WEBP"u8)
                  && System.Text.Encoding.ASCII.GetString(webp.Data).Contains("ANIM"),
                "WebP animations are animated WebP");
            check(avif.Data.Length < webp.Data.Length && webp.Data.Length < gif2.Data.Length,
                "AVIF is smaller than WebP, which is smaller than GIF");
        }

        // ---------------------------------------------------- translation prompt
        var prompt = TranslationPrompt.Build("English", true, false, "", "", 5, "{sentence}を守れ{x}",
            ["前の台詞", "次の台詞"], "アルフィノ", "FINAL FANTASY XIV");
        check(prompt.Contains("\"FINAL FANTASY XIV\"") && prompt.Contains("Professional Game Localization Task")
              && prompt.Contains("natural-sounding, context-aware English") && prompt.Contains("Dialogue Context:\n\n前の台詞\n次の台詞")
              && prompt.Contains("spoken by アルフィノ") && prompt.TrimEnd().EndsWith("{sentence}を守れ{x}"),
            "translation prompts follow GSM's template, with context, speaker and the line last");
        check(TranslationPrompt.Build("English", true, false, "", "", 0, "a", ["b"], "", "").Contains("No dialogue context available.")
              && TranslationPrompt.Build("French", false, false, "Say it in {native_language}:", "", 5, "a", [], "", "").Contains("Say it in French:"),
            "dialogue context can be turned off, and custom prompts get GSM's placeholders");
        try
        {
            TranslationPrompt.Build("English", true, false, "", "no sentence here", 5, "a", [], "", "");
            check(false, "a full prompt without {sentence} is rejected");
        }
        catch (InvalidOperationException) { check(true, "a full prompt without {sentence} is rejected"); }

        // --------------------------------------------------------------- fields
        var fields = new List<(string Name, string Value)>
        {
            ("Expression", "冒険"), ("Sentence", "冒険の話"), ("Sentence Audio", string.Empty),
            ("Picture", "<img src=\"hachidori-screenshot-1.jpg\">"), ("Notes", string.Empty),
        };
        check(MediaFields.SentenceAudioField(fields, string.Empty) == "Sentence Audio"
              && MediaFields.SentenceAudioField(fields, "notes") == "Notes"
              && MediaFields.SentenceAudioField(fields, "Missing") is null,
            "sentence audio goes to the configured field, else one named for it");
        check(MediaFields.GifField(fields, string.Empty, "hachidori-screenshot-1.jpg", true) == "Picture"
              && MediaFields.GifField(fields, string.Empty, "hachidori-screenshot-1.jpg", false) is null
              && MediaFields.GifField([.. fields, ("GIF", string.Empty)], string.Empty, "hachidori-screenshot-1.jpg", true) == "GIF",
            "the GIF goes to a GIF field, else replaces the screenshot");
        check(MediaFields.WithGif("<div><img src=\"hachidori-screenshot-1.jpg\"></div>", "a.gif", "hachidori-screenshot-1.jpg") == "<div><img src=\"a.gif\"></div>"
              && MediaFields.WithGif(string.Empty, "a.gif", null) == "<img src=\"a.gif\">",
            "the GIF replaces only the screenshot image");

        // ------------------------------------------------- Hachidori screenshot
        var shot = new ScreenshotMedia("token", $"hachidori-screenshot-{Guid.NewGuid():D}.jpg", Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0]));
        var request = new JsonObject { ["captureUnavailable"] = new JsonArray("sentence-audio", "gif") };
        var envelope = HachidoriPayloads.Submit(request, shot);
        var hachidoriName = new Regex(@"^hachidori-screenshot-[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\.jpg$");
        check(envelope["request"]?["screenshot"]?["token"]?.ToString() == "token"
              && envelope["clientMedia"]?["screenshot"]?["data"]?.ToString()?.StartsWith("/9j/") == true
              && hachidoriName.IsMatch(shot.Filename)
              && envelope["request"]?["captureUnavailable"] is JsonArray { Count: 2 },
            "screenshots travel in Hachidori's linked client-media envelope");
        var without = HachidoriPayloads.Submit(new JsonObject { ["captureUnavailable"] = new JsonArray("sentence-audio", "gif") });
        check((without["request"]?["captureUnavailable"] as JsonArray)?.Any(n => n?.ToString() == "screenshot") == true
              && without["clientMedia"] is JsonObject { Count: 0 },
            "without a screenshot, {screenshot} is declared unavailable");

        // ----------------------------------------------- live game-audio capture
        CheckPlatformAndFallbacks(check);
        await LiveLoopbackAsync(check);
    }

    private static void CheckOstExtractionAndMixing(Action<bool, string> check)
    {
        const int rate = 48000;

        // Loop position resolution: before loop, inside loop, multiple loop wraps, and non-looping end.
        check(ScdOstExtractor.ResolveStartSample(500, 10000, 2000, 8000) == 500
              && ScdOstExtractor.ResolveStartSample(8500, 10000, 2000, 8000) == 2500
              && ScdOstExtractor.ResolveStartSample(20500, 10000, 2000, 8000) == 2500
              && ScdOstExtractor.ResolveStartSample(4000, 10000, 0, 0) == 4000
              && ScdOstExtractor.ResolveStartSample(12000, 10000, 0, 0) == -1,
            "OST loop position wraps across loop bounds and stops past non-looping tracks");

        // Extract a slice across a loop boundary from a real Ogg Vorbis fixture.
        var oggPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone.ogg");
        var oggBytes = File.ReadAllBytes(oggPath);
        var track = ScdOstExtractor.FromOggData("music/test/BGM_Test_Loop.scd", oggBytes, loopStartSample: 2000, loopEndSample: 8000);
        check(track is { Loops: true } && track.SampleRate > 0 && track.TotalSamples >= 8000,
            "OST extractor loads Ogg Vorbis metadata and loop points");
        var loopedSlice = ScdOstExtractor.ExtractSlice(track!, startSeconds: 5.0, outputSamples: rate, outputRate: rate);
        check(loopedSlice is { Length: rate } && loopedSlice.Max(Math.Abs) > 0.05f,
            "OST extractor wraps long elapsed timestamps and seamlessly loops across boundaries");

        // LRU cache returns the same track without re-invoking the loader.
        var cache = new OstTrackCache(maxEntries: 2);
        var loads = 0;
        var t1 = cache.GetOrAdd(track!.FilePath, 0, () => { loads++; return track; });
        var t2 = cache.GetOrAdd(track!.FilePath, 0, () => { loads++; return track; });
        check(ReferenceEquals(t1, t2) && loads == 1, "OST track cache reuses decoded SCD tracks for repeated mines");

        // Mix 24 kHz TTS speech with a loud 48 kHz OST bed.
        const int ttsRate = 24000;
        var ttsSpeech = Enumerable.Range(0, ttsRate)
            .Select(i => 0.35f * MathF.Sin(2f * MathF.PI * 300f * i / ttsRate))
            .ToArray();
        var (_, preSamples, postSamples, totalSamples) = AudioClips.PrepareTtsForOst(ttsSpeech, ttsRate, rate);
        var loudOst = Enumerable.Range(0, totalSamples)
            .Select(i => 0.90f * MathF.Sin(2f * MathF.PI * 110f * i / rate))
            .ToArray();

        var (mixed, mixedOst) = AudioClips.MixTtsWithOst(ttsSpeech, ttsRate, loudOst, ostVolume: 0.25f, outputRate: rate);
        var startEdge = Math.Abs(mixed[0]);
        var endEdge = Math.Abs(mixed[^1]);
        var preRollRms = MathF.Sqrt(mixed.AsSpan(preSamples / 2, preSamples / 2).ToArray().Select(v => v * v).Average());
        var midSpeechRms = MathF.Sqrt(mixed.AsSpan(preSamples + rate / 4, rate / 2).ToArray().Select(v => v * v).Average());
        var peak = mixed.Max(Math.Abs);
        check(mixedOst && mixed.Length == totalSamples
              && startEdge < 0.01f && endEdge < 0.02f
              && preRollRms > 0.01f && preRollRms < midSpeechRms * 0.6f
              && peak <= 0.895f,
            $"TTS + OST mix balances loud OST below speech with smooth fade-in/out (preRms={preRollRms:0.000}, speechRms={midSpeechRms:0.000}, peak={peak:0.00})");

        // Silent OST slice falls back cleanly without padding dead silence.
        var (silentMixed, silentFlag) = AudioClips.MixTtsWithOst(ttsSpeech, ttsRate, new float[totalSamples], ostVolume: 0.28f, outputRate: rate);
        check(!silentFlag && silentMixed.Length > 0 && silentMixed.Length < totalSamples,
            "silent OST falls back to clean trimmed TTS without dead-air padding");
    }

    private static void CheckBgmSceneSelectionAndVoiceDetection(Action<bool, string> check)
    {
        // ----------------------------------------------- BGMSystem scene priority resolution
        var bgmSheet = new Dictionary<ushort, string>
        {
            [1] = "music/ffxiv/BGM_Null.scd",
            [10] = "music/ex1/BGM_EX1_Town_Ishgard_Day.scd",
            [20] = "music/ex1/BGM_EX1_Event_Cutscene.scd",
            [30] = "music/ex1/BGM_EX1_Field_Coerthas.scd",
        };
        string? Resolve(ushort id) => bgmSheet.TryGetValue(id, out var p) ? p : null;

        // Normal territory BGM on scene 11 (BgmId != 0, PlayingBgmId == 10); inactive scenes have BgmId == 0.
        var territoryOnly = new[]
        {
            new BgmSceneEntry(0, 20),   // inactive slot with stale PlayingBgmId -> must be skipped
            new BgmSceneEntry(5001, 0), // unresolved BGMSituation -> must be skipped
            new BgmSceneEntry(100, 9999), // sentinel 9999 -> must be skipped
            new BgmSceneEntry(10, 10),  // active territory BGM
        };
        var (path1, silent1, overridden1) = BgmSceneSelector.Select(territoryOnly, Resolve);
        check(path1 == bgmSheet[10] && !silent1 && overridden1 is null,
            "BGMSceneSelector resolves active territory BGM while ignoring inactive slots and 9999 sentinels");

        // Higher-priority cutscene/event BGM (scene 0) overrides territory BGM (scene 11), recording overridden paths.
        var eventOverTerritory = new[]
        {
            new BgmSceneEntry(20, 20),
            new BgmSceneEntry(30, 30),
        };
        var (path2, silent2, overridden2) = BgmSceneSelector.Select(eventOverTerritory, Resolve);
        check(path2 == bgmSheet[20] && !silent2 && overridden2 is not null && overridden2.Contains(bgmSheet[30]),
            "BGMSceneSelector prefers higher-priority event BGM and marks lower-priority tracks as overridden");

        // Explicit silence (BGM row 1 / BGM_Null.scd) on higher-priority scene silences lower-priority territory BGM.
        var explicitSilence = new[]
        {
            new BgmSceneEntry(1, 1),
            new BgmSceneEntry(30, 30),
        };
        var (path3, silent3, overridden3) = BgmSceneSelector.Select(explicitSilence, Resolve);
        check(path3 is null && silent3 && overridden3 is not null && overridden3.Contains(bgmSheet[30]),
            "BGMSceneSelector treats BGM row 1 / BGM_Null.scd as explicit silence over lower scenes");

        // ----------------------------------------------- native voice .scd path classification
        check(VoiceLinePath.IsVoiceLine("cut/ex1/sound/vo_man/vo_man_00100_00010_ja.scd", out var k1) && k1 == VoiceLineKind.Cutscene,
            "VoiceLinePath classifies cutscene vo_ .scd paths as Cutscene voice");
        check(VoiceLinePath.IsVoiceLine(@"cut\ffxiv\sound\voice\voice_001_ja.scd", out var k2) && k2 == VoiceLineKind.Cutscene,
            "VoiceLinePath normalizes backslashes and matches cut/*/voice .scd paths");
        check(VoiceLinePath.IsVoiceLine("sound/voice/Vo_Line/8200101_ja.scd", out var k3) && k3 == VoiceLineKind.Battle,
            "VoiceLinePath classifies sound/voice/Vo_Line/ .scd paths as Battle voice");
        check(!VoiceLinePath.IsVoiceLine("sound/voice/Vo_Emote/vo_emote_01.scd", out _)
              && !VoiceLinePath.IsVoiceLine("music/ex1/BGM_EX1_Town.scd", out _)
              && !VoiceLinePath.IsVoiceLine("sound/battle/SE_Bt_01.scd", out _)
              && !VoiceLinePath.IsVoiceLine("sound/vfx/SE_Vfx_01.scd", out _)
              && !VoiceLinePath.IsVoiceLine("cut/ex1/sound/vo_man/vo_man_00100.avfx", out _),
            "VoiceLinePath rejects emotes, BGM, SFX and non-.scd files");
        check(!VoiceLinePath.IsValidVoiceSize(1) && !VoiceLinePath.IsValidVoiceSize(416)
              && VoiceLinePath.IsValidVoiceSize(2048) && VoiceLinePath.IsValidVoiceSize(0),
            "VoiceLinePath rejects 1-byte and 416-byte cut stub SCDs while accepting real clips");

        // ----------------------------------------------- voice-to-line timestamp association
        const long tps = 10_000_000; // 10 MHz ticks
        var v1 = new VoicePlaybackEvent("cut/ex1/sound/vo_man/vo_1_ja.scd", VoiceLineKind.Cutscene, 10 * tps);
        var v2 = new VoicePlaybackEvent("cut/ex1/sound/vo_man/vo_2_ja.scd", VoiceLineKind.Cutscene, 14 * tps - tps / 50); // 20 ms before line 2
        var recent = new List<VoicePlaybackEvent> { v1, v2 };
        var claimed = new HashSet<VoicePlaybackEvent> { v1 };
        var matched = VoiceLineAssociation.FindUnclaimedPreLineVoice(recent, claimed, 14 * tps, isBattleTalk: false, tps);
        check(ReferenceEquals(matched, v2),
            "VoiceLineAssociation links an unclaimed voice fired 20 ms before the next cutscene line");

        // Unvoiced line 1 at t=10s, voice2 fires at t=13.98s (tentatively on line 1), line 2 appears at t=14.00s -> transfers to line 2.
        check(VoiceLineAssociation.ShouldTransferToNewLine(10 * tps, v2, 14 * tps, tps),
            "VoiceLineAssociation transfers a voice fired right before line 2 away from an earlier unvoiced line");

        // Voiced line 1 at t=10s with 2s dramatic pause (voice at t=12s), line 2 appears at t=16s -> stays on line 1.
        var delayedVoice = new VoicePlaybackEvent("cut/ex1/sound/vo_man/vo_3_ja.scd", VoiceLineKind.Cutscene, 12 * tps);
        check(!VoiceLineAssociation.ShouldTransferToNewLine(10 * tps, delayedVoice, 16 * tps, tps),
            "VoiceLineAssociation keeps a delayed voice on its own line when the next line appears seconds later");
    }

    private static void CheckPluginAudioAndScdVoiceExtraction(Action<bool, string> check)
    {
        const long tps = 10_000_000;
        var tracker = new PluginAudioTracker(tps);

        // Active and finished playback overlap detection (including 250 ms tail margin).
        var h1 = tracker.BeginPlayback(10 * tps);
        check(tracker.Overlaps(10 * tps, 12 * tps, nowTicks: 11 * tps),
            "PluginAudioTracker detects an active pronunciation playback overlapping a voice window");
        tracker.EndPlayback(h1, 11 * tps);
        check(tracker.Overlaps(11 * tps + tps / 10, 13 * tps, nowTicks: 13 * tps),
            "PluginAudioTracker includes WASAPI tail margin after playback ends");
        check(!tracker.Overlaps(12 * tps, 14 * tps, nowTicks: 14 * tps),
            "PluginAudioTracker reports no overlap for a clean voice window after pronunciation finishes");

        // External (WebView2) boolean playback tracking.
        tracker.SetExternalPlaying(true, 20 * tps);
        check(tracker.Overlaps(20 * tps, 21 * tps, nowTicks: 21 * tps),
            "PluginAudioTracker tracks WebView2 external audio playback start");
        tracker.SetExternalPlaying(false, 21 * tps);
        check(!tracker.Overlaps(22 * tps, 24 * tps, nowTicks: 24 * tps),
            "PluginAudioTracker closes WebView2 external audio playback interval on stop");

        // MaskOverlappingSamples suppresses pronunciation audio on RMS fallback so unvoiced lines stay unvoiced.
        const int rate = 48000;
        var quietBefore = new float[rate];
        var unvoicedWithPronunciation = Enumerable.Range(0, rate * 2)
            .Select(i => i >= rate / 2 && i < rate ? 0.4f * MathF.Sin(2f * MathF.PI * 300f * i / rate) : 0f)
            .ToArray();
        var maskTracker = new PluginAudioTracker(tps);
        var h2 = maskTracker.BeginPlayback(100 * tps + tps / 2);
        maskTracker.EndPlayback(h2, 101 * tps);
        maskTracker.MaskOverlappingSamples(unvoicedWithPronunciation, 100 * tps, rate, nowTicks: 102 * tps);
        var maskedVoice = AudioClips.FindVoice(AudioClips.FrameLevels(quietBefore, rate), AudioClips.FrameLevels(unvoicedWithPronunciation, rate), 2f);
        check(maskedVoice is null && unvoicedWithPronunciation.Max(Math.Abs) == 0f,
            "PluginAudioTracker masks pronunciation audio out of RMS fallback voice detection");

        // MS-ADPCM pure C# decoder check (mono block: predictor=0 => coef1=256, coef2=0, delta=100, samp1=1000, samp2=500, nibbles +1, -1).
        var block = new byte[8];
        block[0] = 0; // predictor 0 -> (256, 0)
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(1, 2), 100);  // delta
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(3, 2), 1000); // samp1
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(5, 2), 500);  // samp2
        block[7] = 0x1F; // high nibble = +1, low nibble = -1 (0xF -> -1)
        var adpcm = ScdVoiceExtractor.DecodeMsAdpcm(block, channels: 1, blockAlign: 8, samplesPerBlock: 4, coefs: []);
        // Expected: [500, 1000, 1100, 1100 - 89 = 1011] / 32768 (delta after +1 is 230*100/256 = 89).
        check(adpcm is { Length: 4 }
              && MathF.Abs(adpcm[0] - 500f / 32768f) < 1e-4f
              && MathF.Abs(adpcm[1] - 1000f / 32768f) < 1e-4f
              && MathF.Abs(adpcm[2] - 1100f / 32768f) < 1e-4f
              && MathF.Abs(adpcm[3] - 1011f / 32768f) < 1e-4f,
            "ScdVoiceExtractor decodes MS-ADPCM voice blocks accurately");

        // Ogg Vorbis voice decode check using real fixture.
        var oggPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone.ogg");
        var decodedOgg = ScdVoiceExtractor.DecodeOggVorbis(File.ReadAllBytes(oggPath));
        check(decodedOgg is { SampleRate: > 0, Samples.Length: > 1000 } && decodedOgg.Value.Samples.Max(Math.Abs) > 0.05f,
            "ScdVoiceExtractor decodes Ogg Vorbis voice streams to mono PCM");
    }

    private static void CheckPlatformAndFallbacks(Action<bool, string> check)
    {
        static Func<string, string?> Env(params (string K, string V)[] pairs) =>
            k => pairs.FirstOrDefault(p => string.Equals(p.K, k, StringComparison.OrdinalIgnoreCase)).V;

        var win11 = HachidoriFfxiv.PlatformInfo.Classify(null, null, Env(), false, null, true, new Version(10, 0, 22631));
        check(win11.IsNativeWindows && !win11.IsWine && win11.SupportsWebView2Overlay && win11.SupportsProcessAudioLoopback && !win11.HasCompatibilityFallbacks,
            "PlatformInfo identifies native Windows 11 with full support");

        var oldWin10 = HachidoriFfxiv.PlatformInfo.Classify(null, null, Env(), false, null, true, new Version(10, 0, 18363));
        check(oldWin10.IsNativeWindows && oldWin10.SupportsWebView2Overlay && !oldWin10.SupportsProcessAudioLoopback && oldWin10.HasCompatibilityFallbacks,
            "PlatformInfo flags pre-2004 Windows 10 for system-loopback audio fallback");

        var linux = HachidoriFfxiv.PlatformInfo.Classify("9.0", "Linux", Env(("XL_WINEONLINUX", "1")), false, "ID=arch\n", true, new Version(10, 0, 19041));
        check(linux.IsWine && linux.HostOs == HachidoriFfxiv.HostOsKind.Linux && !linux.IsSteamDeck
              && !linux.SupportsWebView2Overlay && !linux.SupportsProcessAudioLoopback,
            "PlatformInfo identifies Linux (Wine/Proton) and disables Windows-only overlays/process-loopback");

        var deck = HachidoriFfxiv.PlatformInfo.Classify("9.0", "Linux", Env(("SteamDeck", "1"), ("GAMESCOPE_WAYLAND_DISPLAY", "gamescope-0")), false, "ID=steamos\nVARIANT_ID=steamdeck\n", false, new Version(10, 0, 19041));
        check(deck.IsWine && deck.IsSteamDeck && deck.IsGamescope && deck.HostOs == HachidoriFfxiv.HostOsKind.Linux
              && deck.DisplayName.Contains("Steam Deck", StringComparison.Ordinal) && deck.DisplayName.Contains("Gaming Mode", StringComparison.Ordinal),
            "PlatformInfo identifies Steam Deck Gaming Mode (SteamOS/Gamescope)");

        var mac = HachidoriFfxiv.PlatformInfo.Classify("8.0.1", "Darwin", Env(), true, null, false, new Version(10, 0, 19041));
        check(mac.IsWine && mac.HostOs == HachidoriFfxiv.HostOsKind.MacOs && !mac.IsSteamDeck
              && !mac.SupportsWebView2Overlay && mac.DisplayName.Contains("macOS", StringComparison.Ordinal),
            "PlatformInfo identifies macOS (XIV on Mac / Wine)");

        // System loopback format conversion: 44.1 kHz stereo IEEE float -> 48 kHz mono float.
        var srcFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var srcFrames = 441; // 10 ms
        var rawFloat = new byte[srcFrames * 2 * 4];
        for (var i = 0; i < srcFrames; i++)
        {
            var s = MathF.Sin(i * 2f * MathF.PI * 440f / 44100f) * 0.5f;
            BitConverter.TryWriteBytes(rawFloat.AsSpan(i * 8, 4), s);
            BitConverter.TryWriteBytes(rawFloat.AsSpan(i * 8 + 4, 4), s);
        }
        float[] converted = [];
        var count48k = GameAudioRecorder.ConvertToMono48k(rawFloat, srcFormat, ref converted);
        var peak48k = converted.AsSpan(0, count48k).ToArray().Max(Math.Abs);
        check(Math.Abs(count48k - 480) <= 2 && peak48k > 0.45f && peak48k < 0.55f,
            $"system loopback converter resamples 44.1 kHz stereo float to 48 kHz mono ({count48k} frames, peak {peak48k:0.00})");

        // 48 kHz 16-bit PCM 6-channel surround downmix.
        var pcmFormat = new WaveFormat(48000, 16, 6);
        var rawPcm = new byte[100 * 6 * 2];
        for (var i = 0; i < 100; i++)
            for (var c = 0; c < 6; c++)
                BitConverter.TryWriteBytes(rawPcm.AsSpan((i * 6 + c) * 2, 2), (short)16384);
        var countPcm = GameAudioRecorder.ConvertToMono48k(rawPcm, pcmFormat, ref converted);
        check(countPcm == 100 && Math.Abs(converted[50] - 0.5f) < 0.01f,
            "system loopback converter downmixes multi-channel PCM cleanly");

        // WinMM PCM16 conversion fallback helper.
        var sig = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 1) { Gain = 0.5, Frequency = 440 };
        var take = new NAudio.Wave.SampleProviders.OffsetSampleProvider(sig) { Take = TimeSpan.FromMilliseconds(50) };
        var pcm16 = HachidoriFfxiv.Audio.WinMmWaveOut.ReadPcm16(take, 1);
        check(pcm16.Length == 2400 * 2 && pcm16.Any(b => b != 0),
            "WinMM fallback converts sample provider to 16-bit PCM");

        // FFmpeg piped MP3 decode fallback (used when Wine Media Foundation lacks MP3/AAC codecs).
        var anyFfmpeg = Ffmpeg.FindAny();
        var mp3Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone.mp3");
        if (anyFfmpeg is not null && File.Exists(mp3Fixture))
        {
            var wavBytes = HachidoriFfxiv.Audio.PronunciationDecoder.DecodeWithFfmpeg(File.ReadAllBytes(mp3Fixture), anyFfmpeg);
            check(wavBytes.Length > 44 && wavBytes.AsSpan(0, 4).SequenceEqual("RIFF"u8),
                $"FFmpeg piped audio fallback decodes MP3 to WAV ({wavBytes.Length} bytes)");
        }
        else
        {
            Console.WriteLine("SKIP: FFmpeg piped audio fallback check (no FFmpeg found)");
        }
    }

    /// <summary>Plays a tone in this process and checks process loopback hears it. Skipped without a sound device.</summary>
    private static async Task LiveLoopbackAsync(Action<bool, string> check)
    {
        WasapiOut? output;
        try
        {
            output = new WasapiOut();
            output.Init(new NAudio.Wave.SampleProviders.SignalGenerator(48000, 1) { Gain = 0.2, Frequency = 440 });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SKIP: process audio capture (no sound device: {ex.Message})");
            return;
        }

        using (output)
        using (var recorder = new GameAudioRecorder(10))
        {
            recorder.Start();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!recorder.Running && recorder.Error is null && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            check(recorder.Running, $"process loopback recording starts ({recorder.Error ?? "ok"})");
            output.Play();
            await Task.Delay(1500);
            output.Stop();
            var heard = recorder.Ring.Read(Math.Max(0, recorder.Ring.Written - 48000), recorder.Ring.Written);
            var level = Math.Sqrt(heard.Select(v => (double)v * v).DefaultIfEmpty(0).Average());
            check(level > 0.02, $"process loopback records this process's own sound (RMS {level:0.000})");
        }
    }

    /// <summary>A minimal GIF decoder for the checks: each frame as RGB bytes.</summary>
    private static List<byte[]> DecodeGif(byte[] gif)
    {
        var frames = new List<byte[]>();
        var width = gif[6] | gif[7] << 8;
        var height = gif[8] | gif[9] << 8;
        var pos = 13;
        while (pos < gif.Length && gif[pos] != 0x3B)
        {
            if (gif[pos] == 0x21)
            {
                pos += 2;
                while (gif[pos] != 0) pos += gif[pos] + 1;
                pos++;
                continue;
            }

            pos += 9; // image descriptor
            var packed = gif[pos++];
            var size = 2 << (packed & 7);
            var palette = gif.AsSpan(pos, size * 3).ToArray();
            pos += size * 3;
            var minCode = gif[pos++];
            var data = new List<byte>();
            while (gif[pos] != 0)
            {
                data.AddRange(gif.AsSpan(pos + 1, gif[pos]).ToArray());
                pos += gif[pos] + 1;
            }

            pos++;
            var indices = Lzw(data.ToArray(), minCode, width * height);
            frames.Add(indices.SelectMany(i => new[] { palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2] }).ToArray());
        }

        return frames;
    }

    private static List<int> Lzw(byte[] data, int minCode, int count)
    {
        var clear = 1 << minCode;
        var end = clear + 1;
        var size = minCode + 1;
        var table = new List<List<int>>();
        void Reset()
        {
            table = Enumerable.Range(0, clear).Select(i => new List<int> { i }).ToList();
            table.Add([]);
            table.Add([]);
            size = minCode + 1;
        }

        Reset();
        var output = new List<int>(count);
        List<int>? previous = null;
        var bit = 0;
        while (bit + size <= data.Length * 8)
        {
            var code = 0;
            for (var b = 0; b < size; b++, bit++)
                code |= ((data[bit >> 3] >> (bit & 7)) & 1) << b;
            if (code == clear) { Reset(); previous = null; continue; }
            if (code == end) break;
            List<int> entry;
            if (code < table.Count) entry = table[code];
            else if (previous is not null) entry = [.. previous, previous[0]];
            else throw new InvalidDataException("bad LZW code");
            output.AddRange(entry);
            if (previous is not null && table.Count < 4096)
                table.Add([.. previous, entry[0]]);
            previous = entry;
            if (table.Count == 1 << size && size < 12) size++;
        }

        return output;
    }
}
