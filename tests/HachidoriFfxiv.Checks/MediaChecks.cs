using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Concentus.Oggfile;
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
        await LiveLoopbackAsync(check);
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
