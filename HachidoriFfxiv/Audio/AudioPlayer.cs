using System;
using System.IO;
using System.Threading.Tasks;
using HachidoriFfxiv.Hachidori;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HachidoriFfxiv.Audio;

/// <summary>Plays pronunciation audio (mp3/aac/wav through Media Foundation).</summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly Configuration config;
    private readonly object gate = new();
    private IWavePlayer? output;
    private WaveStream? reader;
    private int sequence;
    private bool disposed;

    public AudioPlayer(Configuration config) => this.config = config;

    public string? LastError { get; private set; }

    public void PlayTerm(HachidoriService service, string expression, string reading)
    {
        LastError = null;
        var mine = System.Threading.Interlocked.Increment(ref sequence);
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
                Plugin.Log.Debug($"Audio failed: {ex.Message}");
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
            WasapiOut? wasapi = null;
            try
            {
                wasapi = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
                var volume = new VolumeSampleProvider(stream.ToSampleProvider()) { Volume = Math.Clamp(config.AudioVolume, 0f, 1f) };
                wasapi.Init(volume);
                wasapi.PlaybackStopped += (_, e) =>
                {
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
            }
            catch
            {
                wasapi?.Dispose();
                stream.Dispose();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            output?.Stop();
            output?.Dispose();
            reader?.Dispose();
            output = null;
            reader = null;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            System.Threading.Interlocked.Increment(ref sequence);
            Stop();
        }
    }
}
