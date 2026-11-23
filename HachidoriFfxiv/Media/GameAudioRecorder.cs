using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace HachidoriFfxiv.Media;

/// <summary>How <see cref="GameAudioRecorder"/> is currently capturing audio.</summary>
public enum GameAudioCaptureMode
{
    None,
    /// <summary>Windows 10 2004+ per-process loopback (records only FFXIV).</summary>
    ProcessLoopback,
    /// <summary>Standard WASAPI system output loopback fallback (Linux/Steam Deck via Wine/Proton, or older Windows).</summary>
    SystemLoopback,
}

/// <summary>
/// Records the game's audio so voice lines can be cut from it.
/// Prefers Windows 10 2004+ per-process loopback (FFXIV only), and automatically falls back
/// to standard WASAPI system-output loopback on Linux / Steam Deck (Wine / Proton / PipeWire),
/// older Windows 10, or whenever process loopback is unavailable.
/// </summary>
public sealed class GameAudioRecorder : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private Thread? thread;
    private volatile bool stopping;
    private volatile bool running;
    private volatile GameAudioCaptureMode mode;

    public GameAudioRecorder(double bufferSeconds)
    {
        Ring = new AudioRing(SampleRate, bufferSeconds);
    }

    public AudioRing Ring { get; }
    public bool Running => running && !stopping;
    public GameAudioCaptureMode Mode => mode;
    public string? Error { get; private set; }

    public void Start()
    {
        if (thread is not null)
            return;
        stopping = false;
        thread = new Thread(Run) { IsBackground = true, Name = "Hachidori game audio" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public void Dispose()
    {
        stopping = true;
        thread?.Join(2000);
        thread = null;
    }

    private void Run()
    {
        string? processError = null;
        if (PlatformInfo.Current.SupportsProcessAudioLoopback)
        {
            if (RunProcessLoopback(out processError) || stopping)
                return;
            DiagnosticLog.Write("audio", $"Process loopback unavailable ({processError}); trying system loopback fallback.");
        }

        if (RunSystemLoopback(out var systemError) || stopping)
            return;

        mode = GameAudioCaptureMode.None;
        Error = PlatformInfo.Current.HostOs == HostOsKind.MacOs
            ? "Game audio recording is unavailable on macOS (CoreAudio has no built-in loopback). Enable TTS below for spoken cards, or route audio through a virtual loopback device."
            : $"Game audio recording is unavailable: {systemError ?? processError ?? "no loopback audio device found"}. You can still use TTS below for sentence audio.";
        DiagnosticLog.Write("audio", Error);
    }

    /// <summary>Tier 1: Windows 10 2004+ per-process loopback (captures only FFXIV).</summary>
    private bool RunProcessLoopback(out string? error)
    {
        error = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        using var ready = new AutoResetEvent(false);
        try
        {
            client = Activate(Environment.ProcessId);
            var format = new WaveFormatEx
            {
                FormatTag = 1, Channels = (ushort)Channels, SamplesPerSec = SampleRate, BitsPerSample = 16,
                BlockAlign = (ushort)(Channels * 2), AvgBytesPerSec = SampleRate * Channels * 2, Size = 0,
            };
            const uint loopback = 0x00020000, eventCallback = 0x00040000, autoConvert = 0x80000000, srcDefault = 0x08000000;
            Check(client.Initialize(0, loopback | eventCallback | autoConvert | srcDefault, 200_0000, 0, ref format, IntPtr.Zero), "Initialize");
            Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            var captureId = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref captureId, out var service), "GetService");
            capture = (IAudioCaptureClient)service;
            Check(client.Start(), "Start");
            Error = null;
            mode = GameAudioCaptureMode.ProcessLoopback;
            running = true;
            DiagnosticLog.Write("audio", "Started Windows per-process loopback recording (FFXIV only).");
            var mono = new float[SampleRate];
            while (!stopping)
            {
                ready.WaitOne(100);
                while (capture.GetNextPacketSize(out var packet) == 0 && packet > 0)
                {
                    if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc) != 0)
                        break;
                    var count = (int)Math.Min(frames, (uint)mono.Length);
                    var silent = (flags & 0x2) != 0;
                    unsafe
                    {
                        var pcm = (short*)data;
                        for (var i = 0; i < count; i++)
                            mono[i] = silent ? 0 : (pcm[i * 2] + pcm[i * 2 + 1]) / 65536f;
                    }

                    capture.ReleaseBuffer(frames);
                    // qpc: the performance counter (100 ns units) at the packet's first frame.
                    var ticks = qpc != 0
                        ? (long)(qpc * (Stopwatch.Frequency / 1e7))
                        : Stopwatch.GetTimestamp() - (long)(count * (double)Stopwatch.Frequency / SampleRate);
                    Ring.Write(mono.AsSpan(0, count), ticks);
                }
            }

            client.Stop();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            running = false;
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
        }
    }

    /// <summary>Tier 2: Standard WASAPI default-endpoint loopback (works on Linux/Steam Deck via Wine/PipeWire and older Windows).</summary>
    private bool RunSystemLoopback(out string? error)
    {
        error = null;
        WasapiLoopbackCapture? capture = null;
        using var stopped = new ManualResetEventSlim(false);
        Exception? asyncFailure = null;
        try
        {
            capture = new WasapiLoopbackCapture();
            var format = capture.WaveFormat;
            float[] scratch = [];
            capture.DataAvailable += (_, e) =>
            {
                if (stopping || e.BytesRecorded <= 0)
                    return;
                var converted = ConvertToMono48k(e.Buffer.AsSpan(0, e.BytesRecorded), format, ref scratch);
                if (converted <= 0)
                    return;
                var ticks = Stopwatch.GetTimestamp() - (long)(converted * (double)Stopwatch.Frequency / SampleRate);
                Ring.Write(scratch.AsSpan(0, converted), ticks);
            };
            capture.RecordingStopped += (_, e) =>
            {
                asyncFailure = e.Exception;
                stopped.Set();
            };

            capture.StartRecording();
            Error = null;
            mode = GameAudioCaptureMode.SystemLoopback;
            running = true;
            DiagnosticLog.Write("audio", $"Started system WASAPI loopback fallback ({format.SampleRate} Hz, {format.Channels} ch, {format.Encoding}) on {PlatformInfo.Current.DisplayName}.");

            while (!stopping && !stopped.IsSet)
                stopped.Wait(100);

            try { capture.StopRecording(); } catch { /* already stopped */ }
            stopped.Wait(500);
            if (asyncFailure is not null && !stopping)
            {
                error = asyncFailure.Message;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            running = false;
            try { capture?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Converts interleaved WASAPI loopback bytes (IEEE float 32-bit or PCM 16/24/32-bit, any channel count and sample rate)
    /// into 48 kHz mono float samples inside <paramref name="output"/>, returning the sample count written.
    /// </summary>
    internal static int ConvertToMono48k(ReadOnlySpan<byte> bytes, WaveFormat format, ref float[] output)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        var frameBytes = channels * bytesPerSample;
        if (frameBytes <= 0 || bytes.Length < frameBytes || format.SampleRate <= 0)
            return 0;

        var srcFrames = bytes.Length / frameBytes;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32 && format is WaveFormatExtensible ext && ext.SubFormat == KsdDataFormatSubtypeIeeeFloat);

        // Downmix to mono at source sample rate.
        var monoSrc = srcFrames <= 4096 ? stackalloc float[srcFrames] : new float[srcFrames];
        var invCh = 1f / channels;
        for (var i = 0; i < srcFrames; i++)
        {
            float sum = 0;
            var frameOffset = i * frameBytes;
            for (var c = 0; c < channels; c++)
            {
                var off = frameOffset + c * bytesPerSample;
                sum += ReadSample(bytes.Slice(off, bytesPerSample), isFloat);
            }
            monoSrc[i] = sum * invCh;
        }

        if (format.SampleRate == SampleRate)
        {
            if (output.Length < srcFrames)
                output = new float[srcFrames];
            monoSrc.CopyTo(output);
            return srcFrames;
        }

        // Linear resample to 48 kHz.
        var dstFrames = Math.Max(1, (int)Math.Round(srcFrames * (double)SampleRate / format.SampleRate));
        if (output.Length < dstFrames)
            output = new float[dstFrames];
        var step = (double)format.SampleRate / SampleRate;
        for (var i = 0; i < dstFrames; i++)
        {
            var pos = i * step;
            var idx = (int)pos;
            if (idx >= srcFrames - 1)
            {
                output[i] = monoSrc[srcFrames - 1];
            }
            else
            {
                var frac = (float)(pos - idx);
                output[i] = monoSrc[idx] + (monoSrc[idx + 1] - monoSrc[idx]) * frac;
            }
        }
        return dstFrames;
    }

    private static float ReadSample(ReadOnlySpan<byte> sample, bool isFloat)
    {
        if (isFloat && sample.Length == 4)
            return MemoryMarshal.Read<float>(sample);
        return sample.Length switch
        {
            2 => MemoryMarshal.Read<short>(sample) / 32768f,
            3 => ((sample[0] | (sample[1] << 8) | ((sbyte)sample[2] << 16)) / 8388608f),
            4 => MemoryMarshal.Read<int>(sample) / 2147483648f,
            1 => (sample[0] - 128) / 128f,
            _ => 0f,
        };
    }

    private static readonly Guid KsdDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private static void Check(int hr, string step)
    {
        if (hr < 0)
            throw new COMException($"{step} failed (0x{hr:X8}). Process audio recording needs Windows 10 version 2004 or later.", hr);
    }

    private static IAudioClient Activate(int processId)
    {
        var parameters = new ActivationParams { ActivationType = 1, TargetProcessId = (uint)processId, ProcessLoopbackMode = 0 };
        var size = Marshal.SizeOf<ActivationParams>();
        var paramsPtr = Marshal.AllocHGlobal(size);
        var variant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(parameters, paramsPtr, false);
            Marshal.StructureToPtr(new PropVariantBlob { Vt = 65 /* VT_BLOB */, Size = (uint)size, Data = paramsPtr }, variant, false);
            var handler = new Completion();
            var clientId = typeof(IAudioClient).GUID;
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref clientId, variant, handler, out _);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Windows did not activate process audio recording.");
            Check(handler.Result, "Activate");
            return (IAudioClient)handler.Interface!;
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    // ------------------------------------------------------------------ interop

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1, Reserved2, Reserved3;
        public uint Size;
        public IntPtr Data;
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

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject
    {
    }

    /// <summary>Agile, so Windows may call it on any thread without marshalling.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public int Result;
        public object? Interface;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out Result, out var activated);
                Interface = activated;
            }
            catch (Exception ex)
            {
                Result = ex.HResult;
            }
            finally
            {
                Done.Set();
            }
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
