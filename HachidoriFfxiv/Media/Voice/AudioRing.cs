using System;
using System.Diagnostics;

namespace HachidoriFfxiv.Media;

/// <summary>
/// The last minutes of the game's sound as mono float samples, addressed by an absolute sample
/// index on a clock shared with <see cref="Stopwatch"/>. Windows delivers no packets while the
/// game is silent, so gaps are filled with silence from each packet's timestamp.
/// </summary>
public sealed class AudioRing
{
    private readonly object gate = new();
    private readonly float[] samples;
    private long written;      // absolute index one past the newest sample
    private long origin = -1;  // Stopwatch ticks of absolute sample 0

    public AudioRing(int sampleRate, double seconds)
    {
        SampleRate = sampleRate;
        samples = new float[Math.Max(sampleRate, (int)(sampleRate * seconds))];
    }

    public int SampleRate { get; }
    public int Capacity => samples.Length;

    /// <summary>The absolute sample index of a <see cref="Stopwatch"/> timestamp.</summary>
    public long IndexAt(long stopwatchTicks)
    {
        lock (gate)
        {
            if (origin < 0)
                return 0;
            return (long)Math.Round((stopwatchTicks - origin) * (double)SampleRate / Stopwatch.Frequency);
        }
    }

    /// <summary>Stores a packet that started playing at <paramref name="stopwatchTicks"/>.</summary>
    public void Write(ReadOnlySpan<float> packet, long stopwatchTicks)
    {
        lock (gate)
        {
            if (origin < 0)
                origin = stopwatchTicks;
            var start = (long)Math.Round((stopwatchTicks - origin) * (double)SampleRate / Stopwatch.Frequency);
            // Timestamps jitter by a few ms; only a real gap (over 20 ms) is filled with silence.
            if (start < written || start - written < SampleRate / 50)
                start = written;
            for (var i = written; i < start; i++)
                samples[i % samples.Length] = 0;
            for (var i = 0; i < packet.Length; i++)
                samples[(start + i) % samples.Length] = packet[i];
            written = start + packet.Length;
        }
    }

    /// <summary>Copies [from, to) in absolute samples; anything not recorded (too old, or not yet) is silence.</summary>
    public float[] Read(long from, long to)
    {
        if (to <= from)
            return [];
        var result = new float[to - from];
        lock (gate)
        {
            var oldest = Math.Max(0, written - samples.Length);
            for (var i = Math.Max(from, oldest); i < Math.Min(to, written); i++)
                result[i - from] = samples[i % samples.Length];
        }

        return result;
    }

    public long Written
    {
        get
        {
            lock (gate)
                return written;
        }
    }
}
