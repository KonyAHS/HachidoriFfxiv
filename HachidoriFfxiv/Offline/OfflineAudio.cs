using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Offline;

/// <summary>
/// Pronunciation audio without the host: the Audio URL (<c>custom</c>) and
/// Yomitan JSON (<c>custom-json</c>) sources from Hachidori's saved settings,
/// fetched directly, in their order (audio-repository.js). Browser speech
/// sources need Chrome and are skipped.
/// </summary>
public static partial class OfflineAudio
{
    private const int MaxAudioBytes = 16 * 1024 * 1024;

    public static async Task<byte[]?> FetchAsync(HttpClient http, JsonNode? sources, string expression, string reading, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var source in (sources as JsonArray ?? []).OfType<JsonObject>())
        {
            if (source["enabled"] is JsonValue e && e.TryGetValue<bool>(out var enabled) && !enabled) continue;
            var type = Str(source["type"]);
            var template = Str(source["url"]);
            if (template.Length == 0 || (type != "custom" && type != "custom-json")) continue;
            try
            {
                var url = SourceUrl(template, expression, reading);
                var candidates = type == "custom" ? [url] : await ListAsync(http, url, ct).ConfigureAwait(false);
                foreach (var candidate in candidates)
                {
                    if (await DownloadAsync(http, candidate, ct).ConfigureAwait(false) is { Length: > 0 } data)
                        return data;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OfflineEngineException or UriFormatException or TaskCanceledException
                                           && !ct.IsCancellationRequested)
            {
                errors.Add(ex.Message);
            }
        }
        if (errors.Count > 0) DiagnosticLog.Write("audio", $"Offline audio sources failed: {string.Join("; ", errors)}");
        return null;
    }

    /// <summary>audio-sources.js audioSourceUrl: {term}, {expression}, {reading} and {language} filled in, URL-encoded.</summary>
    public static string SourceUrl(string template, string expression, string reading, string language = "ja")
    {
        HttpUrl(template);
        var term = Encode(expression);
        var values = new Dictionary<string, string>
        {
            ["term"] = term, ["expression"] = term, ["reading"] = Encode(reading), ["language"] = Encode(language),
        };
        return HttpUrl(Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value));
    }

    private static async Task<IReadOnlyList<string>> ListAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new OfflineEngineException($"The pronunciation list returned HTTP {(int)response.StatusCode}.");
        var list = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (Str(list?["type"]) != "audioSourceList" || list?["audioSources"] is not JsonArray candidates)
            throw new OfflineEngineException("The audio provider returned an invalid Yomitan audioSourceList.");
        return candidates.OfType<JsonObject>().Select(c => HttpUrl(Str(c["url"]))).ToList();
    }

    private static async Task<byte[]?> DownloadAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        if (response.Content.Headers.ContentLength > MaxAudioBytes) return null;
        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return data.Length <= MaxAudioBytes ? data : null;
    }

    private static string HttpUrl(string value)
    {
        if (value.Any(c => c < 0x20))
            throw new OfflineEngineException("Audio URLs must be absolute HTTP(S) URLs.");
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new OfflineEngineException("Audio URLs must use HTTP(S) without a username or password.");
        return uri.AbsoluteUri;
    }

    // encodeURIComponent, plus !'()* as Hachidori's encodeValue escapes them.
    private static string Encode(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~') builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2"));
        }
        return builder.ToString();
    }

    private static string Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Placeholder();
}
