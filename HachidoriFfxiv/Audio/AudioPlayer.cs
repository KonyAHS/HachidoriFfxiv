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
                DiagnosticLog.Write("audio", ex.ToString());
                DalamudServices.Log.Debug($"Audio failed: {ex.Message}");
            }
        });
    }

    public void Play(byte[] data)
    {
        lock (gate)
        {
            if (disposed)
                return;
            Stop();
            var stream = PronunciationDecoder.Open(data);
            var vol = Math.Clamp(config.AudioVolume, 0f, 1f);
            WasapiOut? wasapi = null;
            try
            {
                wasapi = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
                var volume = new VolumeSampleProvider(stream.ToSampleProvider()) { Volume = vol };
                wasapi.Init(volume);
                var handle = PlaybackTracker?.BeginPlayback() ?? 0;
                activePlaybackHandle = handle;
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
                EndActivePlaybackLocked();
                wasapi?.Dispose();
                DiagnosticLog.Write("audio", $"WASAPI playback failed ({wasapiEx.Message}); trying WinMM waveOut fallback.");
                try
                {
                    if (stream.CanSeek)
                        stream.Position = 0;
                    var volume = new VolumeSampleProvider(stream.ToSampleProvider()) { Volume = vol };
                    var handle = PlaybackTracker?.BeginPlayback() ?? 0;
                    activePlaybackHandle = handle;
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
                    EndActivePlaybackLocked();
                    stream.Dispose();
                    DiagnosticLog.Write("audio", $"WinMM fallback failed: {winmmEx.Message}");
                    throw new InvalidOperationException($"Audio playback failed (WASAPI: {wasapiEx.Message}; WinMM: {winmmEx.Message})", wasapiEx);
                }
            }
        }
    }

    private void EndTrackedPlayback(long handle)
    {
        if (handle <= 0)
            return;
        lock (gate)
        {
            if (activePlaybackHandle == handle)
                activePlaybackHandle = 0;
        }
        PlaybackTracker?.EndPlayback(handle);
    }

    private void EndActivePlaybackLocked()
    {
        if (activePlaybackHandle <= 0)
            return;
        var handle = activePlaybackHandle;
        activePlaybackHandle = 0;
        PlaybackTracker?.EndPlayback(handle);
    }

    public void Stop()
    {
        lock (gate)
        {
            output?.Stop();
            output?.Dispose();
            fallbackOutput?.Dispose();
            reader?.Dispose();
            output = null;
            fallbackOutput = null;
            reader = null;
            EndActivePlaybackLocked();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Interlocked.Increment(ref sequence);
            Stop();
        }
    }
}
