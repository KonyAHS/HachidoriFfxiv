using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Concentus;
using Concentus.Oggfile;
using NAudio.Vorbis;
using NAudio.Wave;

namespace HachidoriFfxiv.Audio;

/// <summary>
/// Decodes pronunciation audio: Ogg/Opus (Concentus), Ogg/Vorbis (NVorbis), and WAV natively in C#,
/// plus MP3/AAC/M4A via Windows Media Foundation with an automatic FFmpeg stdin/stdout fallback when
/// Media Foundation codecs are missing or stubbed (common on Linux / Steam Deck / macOS under Wine/Proton).
/// </summary>
public static class PronunciationDecoder
{
    /// <summary>Optional configured FFmpeg path provider used if Media Foundation fails.</summary>
    internal static Func<string>? ConfiguredFfmpegPath { get; set; }

    public static WaveStream Open(byte[] data)
    {
        if (data.Length < 4 || data.Length > 16 * 1024 * 1024) throw new InvalidDataException("Pronunciation data is empty or too large.");
        if (data.AsSpan(0, 4).SequenceEqual("OggS"u8))
        {
            var packet = data.Length > 27 ? 27 + data[26] : data.Length;
            if (packet + 19 <= data.Length && data.AsSpan(packet, 8).SequenceEqual("OpusHead"u8))
                return DecodeOpus(data, packet);
            return new VorbisWaveReader(new MemoryStream(data, writable: false), true);
        }
        if (data.AsSpan(0, 4).SequenceEqual("RIFF"u8)) return new WaveFileReader(new MemoryStream(data, writable: false));

        try
        {
            return new StreamMediaFoundationReader(new MemoryStream(data, writable: false));
        }
        catch (Exception mfEx)
        {
            var ffmpeg = Ffmpeg.FindAny(ConfiguredFfmpegPath?.Invoke() ?? string.Empty);
            if (ffmpeg is not null)
            {
                try
                {
                    var wav = DecodeWithFfmpeg(data, ffmpeg);
                    DiagnosticLog.Write("audio", $"Decoded pronunciation via FFmpeg fallback ({wav.Length} WAV bytes) after Media Foundation error: {mfEx.Message}");
                    return new WaveFileReader(new MemoryStream(wav, writable: false));
                }
                catch (Exception ffEx)
                {
                    DiagnosticLog.Write("audio", $"FFmpeg pronunciation fallback failed: {ffEx.Message}");
                }
            }

            if (PlatformInfo.Current.IsWine)
                throw new InvalidDataException($"Could not decode pronunciation audio (Wine Media Foundation failed: {mfEx.Message}). Install FFmpeg (or configure its path in Settings → Mining) to decode MP3/AAC under {PlatformInfo.Current.DisplayName}.", mfEx);
            throw;
        }
    }

    internal static byte[] DecodeWithFfmpeg(byte[] data, string ffmpeg)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", "pipe:0", "-ac", "2", "-ar", "48000", "-f", "wav", "pipe:1" })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg did not start.");
        using var stdout = new MemoryStream();
        var readOut = Task.Run(() => process.StandardOutput.BaseStream.CopyTo(stdout));
        var readErr = process.StandardError.ReadToEndAsync();
        try
        {
            using (var input = process.StandardInput.BaseStream)
                input.Write(data, 0, data.Length);
        }
        catch (IOException) { /* FFmpeg may exit early on invalid input */ }

        if (!process.WaitForExit(10_000))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("FFmpeg timed out decoding pronunciation audio.");
        }
        readOut.Wait(2_000);
        var wav = stdout.ToArray();
        if (process.ExitCode != 0 || wav.Length < 44 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8))
        {
            var err = readErr.Wait(1_000) ? readErr.Result.Trim() : string.Empty;
            throw new InvalidDataException($"FFmpeg failed (exit {process.ExitCode}): {err}");
        }
        return wav;
    }

    private static WaveStream DecodeOpus(byte[] data, int header)
    {
        var channels = data[header + 9];
        if (channels is < 1 or > 2 || data[header + 18] != 0)
            throw new InvalidDataException("This Opus channel mapping is not supported for pronunciation playback.");
        var preSkip = data[header + 10] | data[header + 11] << 8;
        using var input = new MemoryStream(data, writable: false);
        using var decoder = OpusCodecFactory.CreateDecoder(48000, channels);
        var ogg = new OpusOggReadStream(decoder, input);
        using var pcm = new MemoryStream();
        while (ogg.HasNextPacket)
        {
            var samples = ogg.DecodeNextPacket();
            if (samples is null) break;
            if (pcm.Length + samples.Length * 2L > 48000L * channels * 2 * 120)
                throw new InvalidDataException("Pronunciation audio exceeds two minutes.");
            var bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            pcm.Write(bytes);
        }
        if (!string.IsNullOrEmpty(ogg.LastError)) throw new InvalidDataException(ogg.LastError);
        var offset = (int)Math.Min(pcm.Length, preSkip * channels * 2L);
        var length = (int)pcm.Length - offset;
        if (ogg.GranuleCount > preSkip)
            length = (int)Math.Min(length, (ogg.GranuleCount - preSkip) * channels * 2);
        if (length <= 0) throw new InvalidDataException("Opus source decoded no audio samples.");
        return new RawSourceWaveStream(new MemoryStream(pcm.ToArray(), offset, length, writable: false), new WaveFormat(48000, 16, channels));
    }
}
