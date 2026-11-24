using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HachidoriFfxiv.Media;

/// <summary>
/// Tracks time intervals on the <see cref="Stopwatch"/> clock when the plugin itself is playing
/// pronunciation audio (either via <see cref="Audio.AudioPlayer"/> inside FFXIV's process or via
/// WebView2's child process). Used to detect when WASAPI loopback capture was overlapped by the
/// plugin's own audio so voice-line mining can substitute a clean SCD decode and RMS fallback
/// voice detection ignores plugin audio.
/// </summary>
public sealed class PluginAudioTracker
{
    private const double LeadMarginSeconds = 0.10;
    private const double TailMarginSeconds = 0.25;
    private const double MaxOpenIntervalSeconds = 30.0;
    private const double RetainWindowSeconds = 300.0;

    private readonly object gate = new();
    private readonly long ticksPerSecond;
    private readonly List<Interval> intervals = new();
    private long nextHandle;
    private long externalHandle;

    public PluginAudioTracker(long? ticksPerSecond = null)
    {
        this.ticksPerSecond = ticksPerSecond is > 0 ? ticksPerSecond.Value : Stopwatch.Frequency;
    }

    private sealed class Interval(long handle, long startTicks)
    {
        public long Handle { get; } = handle;
        public long StartTicks { get; } = startTicks;
        public long? EndTicks { get; set; }
    }

    /// <summary>Marks the start of a plugin audio playback and returns a handle to close it.</summary>
    public long BeginPlayback(long? nowTicks = null)
    {
        var now = nowTicks ?? Stopwatch.GetTimestamp();
        lock (gate)
        {
            var handle = ++nextHandle;
            intervals.Add(new Interval(handle, now));
            PruneLocked(now);
            return handle;
        }
    }

    /// <summary>Marks a playback started with <see cref="BeginPlayback"/> as finished.</summary>
    public void EndPlayback(long handle, long? nowTicks = null)
    {
        if (handle <= 0)
            return;
        var now = nowTicks ?? Stopwatch.GetTimestamp();
        var tail = (long)(TailMarginSeconds * ticksPerSecond);
        lock (gate)
        {
            for (var i = intervals.Count - 1; i >= 0; i--)
            {
                if (intervals[i].Handle == handle)
                {
                    intervals[i].EndTicks ??= Math.Max(intervals[i].StartTicks, now) + tail;
                    break;
                }
            }
            PruneLocked(now);
        }
    }

    /// <summary>Updates boolean playback state reported by an external host such as WebView2.</summary>
    public void SetExternalPlaying(bool playing, long? nowTicks = null)
    {
        var now = nowTicks ?? Stopwatch.GetTimestamp();
        lock (gate)
        {
            if (playing)
            {
                if (externalHandle != 0)
                    return;
                var handle = ++nextHandle;
                externalHandle = handle;
                intervals.Add(new Interval(handle, now));
                PruneLocked(now);
            }
            else if (externalHandle != 0)
            {
                var handle = externalHandle;
                externalHandle = 0;
                var tail = (long)(TailMarginSeconds * ticksPerSecond);
                for (var i = intervals.Count - 1; i >= 0; i--)
                {
                    if (intervals[i].Handle == handle)
                    {
                        intervals[i].EndTicks ??= Math.Max(intervals[i].StartTicks, now) + tail;
                        break;
                    }
                }
                PruneLocked(now);
            }
        }
    }

    /// <summary>Returns true if any plugin pronunciation playback overlapped [<paramref name="fromTicks"/>, <paramref name="toTicks"/>].</summary>
    public bool Overlaps(long fromTicks, long toTicks, long? nowTicks = null)
    {
        if (toTicks <= fromTicks)
            return false;
        var now = nowTicks ?? Stopwatch.GetTimestamp();
        var lead = (long)(LeadMarginSeconds * ticksPerSecond);
        var maxOpen = (long)(MaxOpenIntervalSeconds * ticksPerSecond);
        lock (gate)
        {
            for (var i = intervals.Count - 1; i >= 0; i--)
            {
                var iv = intervals[i];
                var start = iv.StartTicks - lead;
                var end = iv.EndTicks ?? Math.Min(now + (long)(TailMarginSeconds * ticksPerSecond), iv.StartTicks + maxOpen);
                if (end >= fromTicks && start <= toTicks)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Zeros out samples in <paramref name="samples"/> that fall inside any plugin pronunciation
    /// playback interval, so RMS-based fallback voice detection does not mistake plugin audio for
    /// a game voice line.
    /// </summary>
    public void MaskOverlappingSamples(float[] samples, long bufferStartTicks, int sampleRate, long? nowTicks = null)
    {
        if (samples.Length == 0 || sampleRate <= 0)
            return;
        var now = nowTicks ?? Stopwatch.GetTimestamp();
        var bufferDurationTicks = (long)(samples.Length * (double)ticksPerSecond / sampleRate);
        var bufferEndTicks = bufferStartTicks + bufferDurationTicks;
        var lead = (long)(LeadMarginSeconds * ticksPerSecond);
        var maxOpen = (long)(MaxOpenIntervalSeconds * ticksPerSecond);
        var openTail = (long)(TailMarginSeconds * ticksPerSecond);

        lock (gate)
        {
            for (var i = intervals.Count - 1; i >= 0; i--)
            {
                var iv = intervals[i];
                var start = iv.StartTicks - lead;
                var end = iv.EndTicks ?? Math.Min(now + openTail, iv.StartTicks + maxOpen);
                if (end <= bufferStartTicks || start >= bufferEndTicks)
                    continue;

                var s0 = (int)Math.Clamp(Math.Floor((start - bufferStartTicks) * (double)sampleRate / ticksPerSecond), 0, samples.Length);
                var s1 = (int)Math.Clamp(Math.Ceiling((end - bufferStartTicks) * (double)sampleRate / ticksPerSecond), s0, samples.Length);
                if (s1 > s0)
                    Array.Clear(samples, s0, s1 - s0);
            }
        }
    }

    private void PruneLocked(long nowTicks)
    {
        var cutoff = nowTicks - (long)(RetainWindowSeconds * ticksPerSecond);
        var maxOpen = (long)(MaxOpenIntervalSeconds * ticksPerSecond);
        for (var i = intervals.Count - 1; i >= 0; i--)
        {
            var iv = intervals[i];
            var effectiveEnd = iv.EndTicks ?? (iv.StartTicks + maxOpen);
            if (effectiveEnd < cutoff)
                intervals.RemoveAt(i);
        }
    }
}
