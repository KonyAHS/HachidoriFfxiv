using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Media;

public enum TtsMode
{
    Off,
    /// <summary>VOICEVOX, AivisSpeech, COEIROINK v1 and other engines with VOICEVOX's API.</summary>
    Voicevox,
    /// <summary>Any URL that returns audio, with {text} (and {speaker}) filled in.</summary>
    Custom,
}

/// <summary>Speech for lines the game did not voice, from a local TTS engine.</summary>
public sealed class TtsClient : IDisposable
{
    private readonly Configuration config;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public TtsClient(Configuration config) => this.config = config;

    public bool Enabled => config.TtsMode != TtsMode.Off;

    /// <summary>Synthesises <paramref name="text"/>; returns the engine's audio bytes (usually WAV).</summary>
    public async Task<byte[]> SpeakAsync(string text, CancellationToken ct = default)
    {
        return config.TtsMode switch
        {
            TtsMode.Voicevox => await VoicevoxAsync(text, ct).ConfigureAwait(false),
            TtsMode.Custom => await CustomAsync(text, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Text-to-speech is turned off."),
        };
    }

    private async Task<byte[]> VoicevoxAsync(string text, CancellationToken ct)
    {
        var root = config.TtsUrl.TrimEnd('/');
        var speaker = Uri.EscapeDataString(config.TtsSpeaker.Trim().Length > 0 ? config.TtsSpeaker.Trim() : "1");
        using var queryResponse = await http.PostAsync($"{root}/audio_query?text={Uri.EscapeDataString(text)}&speaker={speaker}", null, ct).ConfigureAwait(false);
        await EnsureAsync(queryResponse, "audio_query").ConfigureAwait(false);
        var query = await queryResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var body = new StringContent(query, Encoding.UTF8, "application/json");
        using var synthesis = await http.PostAsync($"{root}/synthesis?speaker={speaker}", body, ct).ConfigureAwait(false);
        await EnsureAsync(synthesis, "synthesis").ConfigureAwait(false);
        return await synthesis.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<byte[]> CustomAsync(string text, CancellationToken ct)
    {
        var url = config.TtsUrl
            .Replace("{text}", Uri.EscapeDataString(text), StringComparison.Ordinal)
            .Replace("{speaker}", Uri.EscapeDataString(config.TtsSpeaker.Trim()), StringComparison.Ordinal);
        using var response = config.TtsPost
            ? await http.PostAsync(url, new StringContent(new JsonObject { ["text"] = text, ["speaker"] = config.TtsSpeaker.Trim() }.ToJsonString(), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false)
            : await http.GetAsync(url, ct).ConfigureAwait(false);
        await EnsureAsync(response, "request").ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string step)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (detail.Length > 200) detail = detail[..200];
        throw new HttpRequestException($"TTS {step} answered {(int)response.StatusCode}: {detail}");
    }

    public void Dispose() => http.Dispose();
}
