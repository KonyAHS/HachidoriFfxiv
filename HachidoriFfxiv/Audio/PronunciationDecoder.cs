using System;
using System.IO;
using System.Text;
using Concentus;
using Concentus.Oggfile;
using NAudio.Vorbis;
using NAudio.Wave;

namespace HachidoriFfxiv.Audio;

/// <summary>Browser audio sources commonly return Ogg/Opus, which Media Foundation cannot reliably open.</summary>
public static class PronunciationDecoder
{
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
        return new StreamMediaFoundationReader(new MemoryStream(data, writable: false));
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
