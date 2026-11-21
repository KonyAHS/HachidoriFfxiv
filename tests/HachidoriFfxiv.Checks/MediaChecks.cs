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
        check(AudioClips.IsVoiced(music, voiced, rate, 2f), "a voice over music counts as voiced");
        check(!AudioClips.IsVoiced(music, Tone(2, 0.05f, 220), rate, 2f), "music alone is not a voice line");
        check(!AudioClips.IsVoiced(new float[rate], new float[rate], rate, 2f), "silence is not a voice line");
        check(AudioClips.IsVoiced(new float[rate], Tone(1, 0.2f, 200), rate, 2f), "a voice after silence counts as voiced");

        var padded = new float[rate].Concat(Tone(1, 0.3f, 200)).Concat(new float[rate]).ToArray();
        var trimmed = AudioClips.Trim(padded, rate);
        check(trimmed.Length < rate * 1.5 && trimmed.Length > rate, "voice clips are trimmed to the speech with a little padding");
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
