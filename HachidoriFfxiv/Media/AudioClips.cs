using System;
using System.IO;
using System.Linq;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HachidoriFfxiv.Media;

/// <summary>Voice-line clips: detection, trimming, levelling and Ogg Opus encoding.</summary>
public static class AudioClips
{
    internal const int FramesPerSecond = 50;
    internal const int VoiceOnsetFrames = 75; // 1.2 s after the text appears, plus 300 ms of pre-roll
    private const int VoicePauseFrames = 60; // allow 1.2 s between phrases

    /// <summary>RMS of consecutive 20 ms frames.</summary>
    public static float[] FrameLevels(ReadOnlySpan<float> samples, int sampleRate)
    {
        var frame = Math.Max(1, sampleRate / FramesPerSecond);
        var levels = new float[samples.Length / frame];
        for (var f = 0; f < levels.Length; f++)
        {
            double sum = 0;
            for (var i = f * frame; i < (f + 1) * frame; i++)
                sum += samples[i] * samples[i];
            levels[f] = (float)Math.Sqrt(sum / frame);
        }

        return levels;
    }

    private static float Percentile(float[] values, float p)
    {
        if (values.Length == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[Math.Clamp((int)(p * (sorted.Length - 1)), 0, sorted.Length - 1)];
    }

    /// <summary>
    /// Finds the first voice near the text onset, relative to the preceding music and ambience.
    /// Returns frame indices with an exclusive end. More frames can extend the end until a pause confirms it.
    /// </summary>
    internal static (int Start, int End, bool Finished)? FindVoice(float[] before, ReadOnlySpan<float> levels, float ratio)
    {
        var baseline = Percentile(before, 0.5f);
        var loudBackground = Percentile(before, 0.9f);
        var onsetLevel = Math.Max(0.01f, Math.Max(baseline * ratio, loudBackground * 1.15f));
        var speechLevel = Math.Max(0.006f, Math.Max(baseline * 1.2f, loudBackground * 1.05f));
        const int sustainedFrames = 3;
        var run = 0;
        var start = -1;
        for (var i = 0; i < Math.Min(levels.Length, VoiceOnsetFrames); i++)
        {
            run = levels[i] >= onsetLevel ? run + 1 : 0;
            if (run == sustainedFrames)
            {
                start = i + 1 - run;
                break;
            }
        }

        if (start < 0)
            return null;

        var end = start + sustainedFrames;
        // Include soft sounds just before and after louder speech.
        var leadIn = Math.Max(0, start - FramesPerSecond / 5);
        while (start > leadIn && levels[start - 1] >= speechLevel)
            start--;

        for (var i = end; i < levels.Length; i++)
        {
            run = levels[i] >= speechLevel ? run + 1 : 0;
            if (run >= sustainedFrames)
                end = i + 1;
            if (run == 0 && i + 1 - end >= VoicePauseFrames)
                return (start, end, true);
        }

        return (start, end, false);
    }

    /// <summary>Cuts leading and trailing quiet (relative to the clip's loudest part), keeping a little padding.</summary>
    public static float[] Trim(float[] samples, int sampleRate, float padSeconds = 0.15f)
    {
        var levels = FrameLevels(samples, sampleRate);
        if (levels.Length == 0)
            return samples;
        var threshold = Math.Max(0.006f, Percentile(levels, 0.95f) * 0.12f);
        var first = Array.FindIndex(levels, l => l >= threshold);
        var last = Array.FindLastIndex(levels, l => l >= threshold);
        if (first < 0)
            return samples;
        var frame = Math.Max(1, sampleRate / FramesPerSecond);
        var pad = (int)(sampleRate * padSeconds);
        var start = Math.Max(0, first * frame - pad);
        var end = Math.Min(samples.Length, (last + 1) * frame + pad);
        return samples[start..end];
    }

    /// <summary>Scales the clip so its peak sits at -1 dBFS, never amplifying more than 4×.</summary>
    public static float[] Normalize(float[] samples)
    {
        var peak = 0f;
        foreach (var s in samples)
            peak = Math.Max(peak, Math.Abs(s));
        if (peak <= 0)
            return samples;
        var gain = Math.Min(4f, 0.891f / peak);
        return samples.Select(s => s * gain).ToArray();
    }

    /// <summary>Mono samples as an Ogg Opus file, which Anki plays everywhere.</summary>
    public static byte[] EncodeOpus(float[] samples, int sampleRate, int bitrate = 48000)
    {
        using var output = new MemoryStream();
        var encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = bitrate;
        var ogg = new OpusOggWriteStream(encoder, output, null, sampleRate, 5, true);
        ogg.WriteSamples(samples, 0, samples.Length);
        ogg.Finish();
        return output.ToArray();
    }

    /// <summary>A WAV file's audio as mono float samples.</summary>
    public static (float[] Samples, int SampleRate) DecodeWav(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.Channels == 2)
            provider = new StereoToMonoSampleProvider(provider);
        else if (provider.WaveFormat.Channels > 2)
            throw new InvalidDataException("Only mono and stereo TTS audio is supported.");
        var rate = provider.WaveFormat.SampleRate;
        var buffer = new float[rate];
        var list = new System.Collections.Generic.List<float>();
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            list.AddRange(buffer.AsSpan(0, read).ToArray());
        return (list.ToArray(), rate);
    }

    /// <summary>The file extension for audio bytes, by their signature.</summary>
    public static string? Extension(byte[] data) => data switch
    {
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', ..] => "wav",
        [(byte)'O', (byte)'g', (byte)'g', (byte)'S', ..] => "ogg",
        [(byte)'I', (byte)'D', (byte)'3', ..] => "mp3",
        [0xFF, var b, ..] when (b & 0xE0) == 0xE0 => "mp3",
        [_, _, _, _, (byte)'f', (byte)'t', (byte)'y', (byte)'p', ..] => "m4a",
        _ => null,
    };
}
