using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace HachidoriFfxiv.Audio;

/// <summary>
/// Minimal <c>winmm.dll</c> <c>waveOut</c> player for short pronunciation clips when WASAPI fails
/// (works reliably across Windows, Wine, Proton, Steam Deck, and macOS).
/// </summary>
internal sealed class WinMmWaveOut : IDisposable
{
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackEvent = 0x00050000;
    private const uint WhdrDone = 0x00000001;

    private readonly nint handle;
    private readonly GCHandle pcmPin;
    private readonly GCHandle headerPin;
    private readonly AutoResetEvent doneEvent;
    private readonly Thread watcher;
    private readonly Action? onFinished;
    private volatile bool disposed;

    private WinMmWaveOut(nint handle, GCHandle pcmPin, GCHandle headerPin, AutoResetEvent doneEvent, Action<string>? onError, Action? onFinished)
    {
        this.handle = handle;
        this.pcmPin = pcmPin;
        this.headerPin = headerPin;
        this.doneEvent = doneEvent;
        this.onFinished = onFinished;
        watcher = new Thread(() =>
        {
            try
            {
                while (!disposed)
                {
                    doneEvent.WaitOne(100);
                    if (disposed) break;
                    var hdr = Marshal.PtrToStructure<WaveHdr>(headerPin.AddrOfPinnedObject());
                    if ((hdr.DwFlags & WhdrDone) != 0)
                        break;
                }
            }
            catch (Exception ex)
            {
                if (!disposed) onError?.Invoke(ex.Message);
            }
            finally
            {
                Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "Hachidori WinMM Audio",
        };
        watcher.Start();
    }

    public static WinMmWaveOut Start(ISampleProvider source, Action<string>? onError = null, Action? onFinished = null)
    {
        var channels = Math.Clamp(source.WaveFormat.Channels, 1, 2);
        var sampleRate = Math.Max(8000, source.WaveFormat.SampleRate);
        var pcm = ReadPcm16(source, channels);
        if (pcm.Length == 0)
            throw new InvalidDataException("Audio clip contained no playable samples.");

        var fmt = new WaveFormatEx
        {
            FormatTag = 1,
            Channels = (ushort)channels,
            SamplesPerSec = sampleRate,
            BitsPerSample = 16,
            BlockAlign = (ushort)(channels * 2),
            AvgBytesPerSec = sampleRate * channels * 2,
            Size = 0,
        };

        var doneEvent = new AutoResetEvent(false);
        var mm = waveOutOpen(out var handle, WaveMapper, ref fmt, doneEvent.SafeWaitHandle.DangerousGetHandle(), 0, CallbackEvent);
        if (mm != 0 || handle == 0)
        {
            doneEvent.Dispose();
            throw new InvalidOperationException($"waveOutOpen failed (MMRESULT {mm}).");
        }

        var pcmPin = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        var hdr = new WaveHdr
        {
            LpData = pcmPin.AddrOfPinnedObject(),
            DwBufferLength = (uint)pcm.Length,
        };
        var headerPin = GCHandle.Alloc(hdr, GCHandleType.Pinned);
        try
        {
            var hdrSize = (uint)Marshal.SizeOf<WaveHdr>();
            mm = waveOutPrepareHeader(handle, headerPin.AddrOfPinnedObject(), hdrSize);
            if (mm != 0) throw new InvalidOperationException($"waveOutPrepareHeader failed (MMRESULT {mm}).");
            mm = waveOutWrite(handle, headerPin.AddrOfPinnedObject(), hdrSize);
            if (mm != 0) throw new InvalidOperationException($"waveOutWrite failed (MMRESULT {mm}).");
            return new WinMmWaveOut(handle, pcmPin, headerPin, doneEvent, onError, onFinished);
        }
        catch
        {
            try { waveOutReset(handle); } catch { }
            try { waveOutUnprepareHeader(handle, headerPin.AddrOfPinnedObject(), (uint)Marshal.SizeOf<WaveHdr>()); } catch { }
            try { waveOutClose(handle); } catch { }
            if (headerPin.IsAllocated) headerPin.Free();
            if (pcmPin.IsAllocated) pcmPin.Free();
            doneEvent.Dispose();
            throw;
        }
    }

    internal static byte[] ReadPcm16(ISampleProvider source, int maxChannels)
    {
        var srcChannels = Math.Max(1, source.WaveFormat.Channels);
        var buf = new float[4096 * srcChannels];
        using var ms = new MemoryStream();
        const long maxBytes = 48000L * 2 * 2 * 120; // cap at 2 minutes of 48kHz stereo
        int read;
        while ((read = source.Read(buf, 0, buf.Length)) > 0 && ms.Length < maxBytes)
        {
            var frames = read / srcChannels;
            for (var f = 0; f < frames; f++)
            {
                if (maxChannels == 1)
                {
                    float sum = 0;
                    for (var c = 0; c < srcChannels; c++) sum += buf[f * srcChannels + c];
                    var s = (short)Math.Clamp(MathF.Round((sum / srcChannels) * 32767f), short.MinValue, short.MaxValue);
                    ms.WriteByte((byte)(s & 0xFF));
                    ms.WriteByte((byte)((s >> 8) & 0xFF));
                }
                else
                {
                    for (var c = 0; c < 2; c++)
                    {
                        var sample = buf[f * srcChannels + Math.Min(c, srcChannels - 1)];
                        var s = (short)Math.Clamp(MathF.Round(sample * 32767f), short.MinValue, short.MaxValue);
                        ms.WriteByte((byte)(s & 0xFF));
                        ms.WriteByte((byte)((s >> 8) & 0xFF));
                    }
                }
            }
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { doneEvent.Set(); } catch { }
        try { waveOutReset(handle); } catch { }
        try { waveOutUnprepareHeader(handle, headerPin.AddrOfPinnedObject(), (uint)Marshal.SizeOf<WaveHdr>()); } catch { }
        try { waveOutClose(handle); } catch { }
        if (headerPin.IsAllocated) headerPin.Free();
        if (pcmPin.IsAllocated) pcmPin.Free();
        try { doneEvent.Dispose(); } catch { }
        try { onFinished?.Invoke(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public nint LpData;
        public uint DwBufferLength;
        public uint DwBytesRecorded;
        public nint DwUser;
        public uint DwFlags;
        public uint DwLoops;
        public nint LpNext;
        public nint Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(out nint hWaveOut, uint uDeviceID, ref WaveFormatEx lpFormat, nint dwCallback, nint dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(nint hWaveOut, nint lpWaveOutHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(nint hWaveOut, nint lpWaveOutHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(nint hWaveOut, nint lpWaveOutHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(nint hWaveOut);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(nint hWaveOut);
}
