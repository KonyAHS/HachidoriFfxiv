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
    private const float FrameSeconds = 0.02f;

    /// <summary>RMS of consecutive 20 ms frames.</summary>
    public static float[] FrameLevels(ReadOnlySpan<float> samples, int sampleRate)
    {
        var frame = Math.Max(1, (int)(sampleRate * FrameSeconds));
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
    /// Whether a line was voiced: its loud parts stand out from the sound just before it
    /// (music and ambience) by <paramref name="ratio"/>, and are not near-silent themselves.
    /// </summary>
    public static bool IsVoiced(ReadOnlySpan<float> before, ReadOnlySpan<float> line, int sampleRate, float ratio)
    {
        var lineLevels = FrameLevels(line, sampleRate);
        if (lineLevels.Length == 0)
            return false;
        var loud = Percentile(lineLevels, 0.85f);
        if (loud < 0.01f)
            return false;
        var baseline = Percentile(FrameLevels(before, sampleRate), 0.5f);
        return baseline < 0.004f || loud >= baseline * ratio;
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
        var frame = (int)(sampleRate * FrameSeconds);
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
