using System;
using System.Threading;
using System.Threading.Tasks;
using HachidoriFfxiv.Hachidori;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HachidoriFfxiv.Audio;

/// <summary>
/// Plays pronunciation audio via WASAPI, falling back automatically to Windows Multimedia
/// (<c>winmm.dll</c> <c>waveOut</c>) if WASAPI fails to initialize (e.g. certain Wine/Proton/macOS audio backends).
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly Configuration config;
    private readonly object gate = new();
    private IWavePlayer? output;
    private WinMmWaveOut? fallbackOutput;
    private WaveStream? reader;
    private int sequence;
    private long activePlaybackHandle;
    private bool disposed;

    public AudioPlayer(Configuration config)
    {
        this.config = config;
        PronunciationDecoder.ConfiguredFfmpegPath = () => this.config.FfmpegPath;
    }

    public string? LastError { get; private set; }

    /// <summary>Optional tracker notified whenever this player renders pronunciation audio.</summary>
    public IAudioPlaybackObserver? PlaybackTracker { get; set; }

    public void PlayTerm(HachidoriService service, string expression, string reading)
    {
        LastError = null;
        var mine = Interlocked.Increment(ref sequence);
        _ = Task.Run(async () =>
        {
            try
            {
                var data = await service.GetAudioAsync(expression, reading).ConfigureAwait(false);
                if (mine != sequence)
                    return;
                if (data is null)
                {
                    LastError = "No pronunciation audio from your Hachidori audio sources.";
                    DiagnosticLog.Write("audio", LastError);
                    return;
                }

                Play(data);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                DiagnosticLog.Fault("audio", ex, "Pronunciation playback failed");
            }
        });
    }

    public void Play(byte[] data)
    {
        IWavePlayer? oldOutput = null;
        WinMmWaveOut? oldFallback = null;
        WaveStream? oldReader = null;
        try
        {
            lock (gate)
            {
                if (disposed)
                    return;
                DetachOutputsLocked(out oldOutput, out oldFallback, out oldReader);
            }
            DisposeOutputs(oldOutput, oldFallback, oldReader);
            oldOutput = null;
            oldFallback = null;
            oldReader = null;

            lock (gate)
            {
                if (disposed)
                    return;
                var stream = PronunciationDecoder.Open(data);
                var vol = Math.Clamp(config.AudioVolume, 0f, 1f);
                WasapiOut? wasapi = null;
                try
                {
                    wasapi = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
                    var volume = new VolumeSampleProvider(stream.ToSampleProvider()) { Volume = vol };
                    wasapi.Init(volume);
                    var handle = PlaybackTracker?.BeginPlayback() ?? 0;
                    Interlocked.Exchange(ref activePlaybackHandle, handle);
                    wasapi.PlaybackStopped += (_, e) =>
                    {
                        EndTrackedPlayback(handle);
                        if (e.Exception is not null)
                        {
                            LastError = e.Exception.Message;
                            DiagnosticLog.Write("audio", e.Exception.ToString());
                        }
                    };
                    wasapi.Play();
                    reader = stream;
                    output = wasapi;
                    DiagnosticLog.Write("audio", $"Playing {stream.WaveFormat}; bytes={data.Length}");
                    LastError = null;
                    return;
                }
                catch (Exception wasapiEx)
                {
                    EndActivePlayback();
                    wasapi?.Dispose();
                    DiagnosticLog.Write("audio", $"WASAPI playback failed ({wasapiEx.Message}); trying WinMM waveOut fallback.");
                    try
                    {
                        if (stream.CanSeek)
                            stream.Position = 0;
                        var volume = new VolumeSampleProvider(stream.ToSampleProvider()) { Volume = vol };
                        var handle = PlaybackTracker?.BeginPlayback() ?? 0;
                        Interlocked.Exchange(ref activePlaybackHandle, handle);
                        var winmm = WinMmWaveOut.Start(volume, err =>
                        {
                            LastError = err;
                            DiagnosticLog.Write("audio", $"WinMM playback error: {err}");
                        }, onFinished: () => EndTrackedPlayback(handle));
                        fallbackOutput = winmm;
                        stream.Dispose();
                        DiagnosticLog.Write("audio", $"Playing via WinMM fallback ({volume.WaveFormat}); bytes={data.Length}");
                        LastError = null;
                        return;
                    }
                    catch (Exception winmmEx)
                    {
                        EndActivePlayback();
                        stream.Dispose();
                        DiagnosticLog.Write("audio", $"WinMM fallback failed: {winmmEx.Message}");
                        throw new InvalidOperationException($"Audio playback failed (WASAPI: {wasapiEx.Message}; WinMM: {winmmEx.Message})", wasapiEx);
                    }
                }
            }
        }
        finally
        {
            DisposeOutputs(oldOutput, oldFallback, oldReader);
        }
    }

    private void EndTrackedPlayback(long handle)
    {
        if (handle <= 0)
            return;
        // Lock-free so PlaybackStopped on NAudio's background thread can never deadlock against Stop/Dispose.
        Interlocked.CompareExchange(ref activePlaybackHandle, 0, handle);
        PlaybackTracker?.EndPlayback(handle);
    }

    private void EndActivePlayback()
    {
        var handle = Interlocked.Exchange(ref activePlaybackHandle, 0);
        if (handle > 0)
            PlaybackTracker?.EndPlayback(handle);
    }

    private void DetachOutputsLocked(out IWavePlayer? oldOutput, out WinMmWaveOut? oldFallback, out WaveStream? oldReader)
    {
        oldOutput = output;
        oldFallback = fallbackOutput;
        oldReader = reader;
        output = null;
        fallbackOutput = null;
        reader = null;
        EndActivePlayback();
    }

    private static void DisposeOutputs(IWavePlayer? oldOutput, WinMmWaveOut? oldFallback, WaveStream? oldReader)
    {
        try { oldOutput?.Stop(); } catch { }
        try { oldOutput?.Dispose(); } catch { }
        try { oldFallback?.Dispose(); } catch { }
        try { oldReader?.Dispose(); } catch { }
    }

    public void Stop()
    {
        IWavePlayer? oldOutput;
        WinMmWaveOut? oldFallback;
        WaveStream? oldReader;
        lock (gate)
            DetachOutputsLocked(out oldOutput, out oldFallback, out oldReader);
        // Dispose outside gate so WasapiOut.Dispose() joining its playback thread cannot deadlock.
        DisposeOutputs(oldOutput, oldFallback, oldReader);
    }

    public void Dispose()
    {
        IWavePlayer? oldOutput;
        WinMmWaveOut? oldFallback;
        WaveStream? oldReader;
        lock (gate)
        {
            disposed = true;
            Interlocked.Increment(ref sequence);
            DetachOutputsLocked(out oldOutput, out oldFallback, out oldReader);
        }
        DisposeOutputs(oldOutput, oldFallback, oldReader);
    }
}
