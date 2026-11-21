using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace HachidoriFfxiv.Media;

/// <summary>
/// Records only the game's own audio with Windows' process loopback (Windows 10 2004 or later),
/// so voice lines can be cut from it. Other programs (music players, Discord) are not recorded.
/// </summary>
public sealed class GameAudioRecorder : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private Thread? thread;
    private volatile bool stopping;
    private volatile bool running;

    public GameAudioRecorder(double bufferSeconds)
    {
        Ring = new AudioRing(SampleRate, bufferSeconds);
    }

    public AudioRing Ring { get; }
    public bool Running => running && !stopping;
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
            running = true;
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
        }
        catch (Exception ex)
        {
            Error = $"Game audio recording is unavailable: {ex.Message}";
            DiagnosticLog.Write("audio", Error);
        }
        finally
        {
            running = false;
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
        }
    }

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
