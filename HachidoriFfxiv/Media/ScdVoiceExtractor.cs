using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Scd;
using NVorbis;

namespace HachidoriFfxiv.Media;

/// <summary>
/// Decodes FFXIV voice <see cref="ScdFile"/> entries directly to mono float PCM (MS-ADPCM, Ogg Vorbis,
/// or MP3), used when the live WASAPI loopback buffer was overlapped by the plugin's own pronunciation audio.
/// </summary>
public static class ScdVoiceExtractor
{
    private static readonly int[] AdaptationTable =
    [
        230, 230, 230, 230, 307, 409, 512, 614,
        768, 614, 512, 409, 307, 230, 230, 230,
    ];

    private static readonly short[] DefaultCoefs =
    [
        256, 0,
        512, -256,
        0, 0,
        192, 64,
        240, 0,
        460, -208,
        392, -232,
    ];

    /// <summary>Decodes the first playable audio track of a voice <see cref="ScdFile"/> into mono float samples.</summary>
    public static unsafe (float[] Samples, int SampleRate)? TryDecode(ScdFile scd)
    {
        if (scd.AudioDataCount <= 0)
            return null;

        try
        {
            var audio = scd.GetAudio(0);
            var desc = audio.AudioBasicDesc;
            var data = audio.AudioData;
            if (data is null || data.Length == 0)
                return null;

            switch (desc.Format)
            {
                case AudioFormat.OggVorbis:
                    return DecodeOggVorbis(data);

                case AudioFormat.MsAdpcm:
                    if (audio.AudioDataHeader is AdpcmWaveFormat adpcm)
                    {
                        var channels = adpcm.Channels > 0 ? adpcm.Channels : (int)desc.Channel;
                        var rate = adpcm.SamplesPerSec > 0 ? (int)adpcm.SamplesPerSec : (int)desc.Rate;
                        var coefCount = Math.Clamp((int)adpcm.NumCoef, 0, 7);
                        Span<short> coefs = stackalloc short[14];
                        if (coefCount > 0)
                        {
                            for (var i = 0; i < coefCount * 2; i++)
                                coefs[i] = adpcm.Coef[i];
                        }
                        else
                        {
                            DefaultCoefs.CopyTo(coefs);
                            coefCount = 7;
                        }
                        var samples = DecodeMsAdpcm(data, channels, adpcm.BlockAlign, adpcm.SamplesPerBlock, coefs[..(coefCount * 2)]);
                        return samples is { Length: > 0 } && rate > 0 ? (samples, rate) : null;
                    }
                    return null;

                case AudioFormat.Mp3:
                    return AudioClips.DecodeAudio(data);

                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decodes an Ogg Vorbis stream into mono float samples.</summary>
    public static (float[] Samples, int SampleRate)? DecodeOggVorbis(byte[] oggData)
    {
        if (oggData.Length < 28 || !oggData.AsSpan(0, 4).SequenceEqual("OggS"u8))
            return null;

        using var ms = new MemoryStream(oggData, writable: false);
        using var reader = new VorbisReader(ms, closeOnDispose: false);
        var sampleRate = reader.SampleRate;
        var channels = reader.Channels;
        if (sampleRate <= 0 || channels <= 0)
            return null;

        var invChannels = 1f / channels;
        var chunk = new float[4096 * channels];
        var mono = new List<float>(reader.TotalSamples > 0 && reader.TotalSamples < 48000 * 120 ? (int)reader.TotalSamples : 48000);
        int read;
        while ((read = reader.ReadSamples(chunk, 0, chunk.Length)) > 0)
        {
            var frames = read / channels;
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                var baseIdx = f * channels;
                for (var c = 0; c < channels; c++)
                    sum += chunk[baseIdx + c];
                mono.Add(sum * invChannels);
            }
        }
        return mono.Count > 0 ? (mono.ToArray(), sampleRate) : null;
    }

    /// <summary>
    /// Decodes Microsoft ADPCM (4-bit) blocks into mono float PCM in [-1, 1].
    /// </summary>
    public static float[]? DecodeMsAdpcm(
        ReadOnlySpan<byte> data,
        int channels,
        int blockAlign,
        int samplesPerBlock,
        ReadOnlySpan<short> coefs)
    {
        if (data.Length == 0 || channels is < 1 or > 2 || blockAlign <= 0)
            return null;

        var headerBytes = 7 * channels;
        if (blockAlign < headerBytes)
            return null;

        var coefTable = coefs.Length >= 2 ? coefs : DefaultCoefs;
        var numCoefs = coefTable.Length / 2;
        if (numCoefs <= 0)
            return null;

        var maxSamplesInBlock = 2 + ((blockAlign - headerBytes) * 2) / channels;
        var targetPerBlock = samplesPerBlock > 0 ? Math.Min(samplesPerBlock, maxSamplesInBlock) : maxSamplesInBlock;
        var blockCount = (data.Length + blockAlign - 1) / blockAlign;
        var output = new List<float>(blockCount * Math.Max(2, targetPerBlock));

        Span<int> coef1 = stackalloc int[2];
        Span<int> coef2 = stackalloc int[2];
        Span<int> delta = stackalloc int[2];
        Span<int> samp1 = stackalloc int[2];
        Span<int> samp2 = stackalloc int[2];
        var invChannels = 1f / (channels * 32768f);

        for (var offset = 0; offset + headerBytes <= data.Length; offset += blockAlign)
        {
            var blockLen = Math.Min(blockAlign, data.Length - offset);
            var block = data.Slice(offset, blockLen);
            var pos = 0;

            for (var c = 0; c < channels; c++)
            {
                var pred = Math.Clamp((int)block[pos++], 0, numCoefs - 1);
                coef1[c] = coefTable[pred * 2];
                coef2[c] = coefTable[pred * 2 + 1];
            }
            for (var c = 0; c < channels; c++)
            {
                delta[c] = BinaryPrimitives.ReadInt16LittleEndian(block.Slice(pos, 2));
                pos += 2;
            }
            for (var c = 0; c < channels; c++)
            {
                samp1[c] = BinaryPrimitives.ReadInt16LittleEndian(block.Slice(pos, 2));
                pos += 2;
            }
            for (var c = 0; c < channels; c++)
            {
                samp2[c] = BinaryPrimitives.ReadInt16LittleEndian(block.Slice(pos, 2));
                pos += 2;
            }

            // Emit the two initial samples per channel (samp2 is older, samp1 is newer).
            float sum2 = 0, sum1 = 0;
            for (var c = 0; c < channels; c++)
            {
                sum2 += samp2[c];
                sum1 += samp1[c];
            }
            output.Add(sum2 * invChannels);
            output.Add(sum1 * invChannels);

            var emittedFrames = 2;
            var ch = 0;
            float frameAcc = 0;

            while (pos < block.Length && emittedFrames < targetPerBlock)
            {
                var b = block[pos++];
                for (var shift = 4; shift >= 0 && emittedFrames < targetPerBlock; shift -= 4)
                {
                    var rawNibble = (b >> shift) & 0x0F;
                    var signedNibble = rawNibble >= 8 ? rawNibble - 16 : rawNibble;

                    var predictor = ((samp1[ch] * coef1[ch]) + (samp2[ch] * coef2[ch])) / 256;
                    predictor += signedNibble * delta[ch];
                    predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);

                    samp2[ch] = samp1[ch];
                    samp1[ch] = predictor;

                    var nextDelta = (AdaptationTable[rawNibble] * delta[ch]) / 256;
                    delta[ch] = Math.Max(16, nextDelta);

                    frameAcc += predictor;
                    ch++;
                    if (ch == channels)
                    {
                        output.Add(frameAcc * invChannels);
                        frameAcc = 0;
                        ch = 0;
                        emittedFrames++;
                    }
                }
            }
        }

        return output.Count > 0 ? output.ToArray() : null;
    }
}
