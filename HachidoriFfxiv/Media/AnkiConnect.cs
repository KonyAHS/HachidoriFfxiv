using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HachidoriFfxiv.Media;

/// <summary>The AnkiConnect calls used to attach recorded media to a note Hachidori created.</summary>
public sealed class AnkiConnect : IDisposable
{
    private static readonly HashSet<string> ReadOnlyActions = new(StringComparer.Ordinal)
    {
        "version", "deckNames", "modelNames", "modelNamesAndIds", "modelFieldNames",
        "canAddNotesWithErrorDetail", "findNotes", "notesInfo", "cardsInfo", "guiSelectedNotes",
    };

    private readonly Func<string> url;
    private readonly Func<string?>? key;
    private readonly HttpClient http;

    /// <param name="key">AnkiConnect's API key, when it requires one.</param>
    public AnkiConnect(Func<string> url, Func<string?>? key = null)
    {
        this.url = url;
        this.key = key;
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // AnkiConnect's Python HTTP server drops idle sockets; avoiding keep-alive reuse prevents
        // transient "response ended prematurely" / connection-reset failures on the next request.
        http.DefaultRequestHeaders.ConnectionClose = true;
    }

    public Task<JsonNode?> InvokeAsync(string action, JsonObject? parameters = null, CancellationToken ct = default) =>
        InvokeAsync(action, parameters, retryOnTransportError: ReadOnlyActions.Contains(action), ct);

    public async Task<JsonNode?> InvokeAsync(string action, JsonObject? parameters, bool retryOnTransportError, CancellationToken ct = default)
    {
        var body = new JsonObject { ["action"] = action, ["version"] = 6 };
        if (parameters is not null)
            body["params"] = parameters;
        if (key?.Invoke() is { Length: > 0 } apiKey)
            body["key"] = apiKey;
        var json = body.ToJsonString();
        var attempts = retryOnTransportError ? 2 : 1;
        HttpResponseMessage? response = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            try
            {
                response = await http.PostAsync(url(), content, ct).ConfigureAwait(false);
                break;
            }
            catch (HttpRequestException) when (attempt < attempts && !ct.IsCancellationRequested)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"AnkiConnect is not reachable at {url()}. Install the AnkiConnect add-on in Anki. ({ex.Message})", ex);
            }
        }

        using (response!)
        {
            var reply = JsonNode.Parse(await response!.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (reply?["error"] is JsonValue error && error.ToString() is { Length: > 0 } message)
                throw new InvalidOperationException($"AnkiConnect {action}: {message}");
            return reply?["result"];
        }
    }

    /// <summary>
    /// Runs several AnkiConnect actions in a single HTTP round trip (paying AnkiConnect's poll interval once).
    /// Each sub-action uses API v6 and returns its own <c>(Result, Error)</c> pair in request order.
    /// </summary>
    public async Task<List<(JsonNode? Result, string? Error)>> MultiAsync(
        IReadOnlyList<(string Action, JsonObject? Params)> actions,
        bool readOnly = false,
        CancellationToken ct = default)
    {
        if (actions.Count == 0)
            return [];

        var apiKey = key?.Invoke();
        var items = new JsonArray();
        foreach (var (action, parameters) in actions)
        {
            var entry = new JsonObject { ["action"] = action, ["version"] = 6 };
            if (parameters is not null)
                entry["params"] = parameters.DeepClone();
            if (!string.IsNullOrEmpty(apiKey))
                entry["key"] = apiKey;
            items.Add(entry);
        }

        var raw = await InvokeAsync("multi", new JsonObject { ["actions"] = items }, retryOnTransportError: readOnly, ct).ConfigureAwait(false);
        if (raw is not JsonArray array || array.Count != actions.Count)
            throw new InvalidOperationException("AnkiConnect returned an invalid multi response.");

        var results = new List<(JsonNode? Result, string? Error)>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonObject obj && obj.ContainsKey("result") && obj.ContainsKey("error"))
            {
                var err = obj["error"] is JsonValue ev && ev.ToString() is { Length: > 0 } msg ? msg : null;
                results.Add((obj["result"]?.DeepClone(), err));
            }
            else
            {
                results.Add((item?.DeepClone(), null));
            }
        }
        return results;
    }

    public async Task<string> StoreMediaAsync(string filename, byte[] data, CancellationToken ct = default)
    {
        var stored = await InvokeAsync("storeMediaFile", new JsonObject
        {
            ["filename"] = filename,
            ["data"] = Convert.ToBase64String(data),
        }, ct).ConfigureAwait(false);
        return stored?.ToString() is { Length: > 0 } name ? name : filename;
    }

    /// <summary>The note's fields in order, with their current values.</summary>
    public async Task<List<(string Name, string Value)>> FieldsAsync(long noteId, CancellationToken ct = default)
    {
        var info = await InvokeAsync("notesInfo", new JsonObject { ["notes"] = new JsonArray(noteId) }, ct).ConfigureAwait(false);
        var fields = (info as JsonArray)?.FirstOrDefault()?["fields"] as JsonObject
            ?? throw new InvalidOperationException($"Anki has no note {noteId}.");
        return fields
            .Select(f => (f.Key, Order: f.Value?["order"]?.GetValue<int>() ?? 0, Value: f.Value?["value"]?.ToString() ?? string.Empty))
            .OrderBy(f => f.Order)
            .Select(f => (f.Key, f.Value))
            .ToList();
    }

    public async Task UpdateFieldsAsync(long noteId, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
    {
        // AnkiConnect's updateNoteFields writes to SQLite without reloading an open Browser editor;
        // if this note is currently selected in Anki's Browser, the stale editor can overwrite our
        // update seconds later. Deselecting it before the write and reselecting it afterwards avoids
        // the clobber and refreshes the Browser view.
        var reselect = false;
        try
        {
            if (await InvokeAsync("guiSelectedNotes", null, ct).ConfigureAwait(false) is JsonArray selected
                && selected.Any(n => n is JsonValue v && v.TryGetValue<long>(out var id) && id == noteId))
            {
                await InvokeAsync("guiBrowse", new JsonObject { ["query"] = "nid:1" }, ct).ConfigureAwait(false);
                reselect = true;
            }
        }
        catch { /* best-effort: never block field updates on GUI calls */ }

        await InvokeAsync("updateNoteFields", new JsonObject
        {
            ["note"] = new JsonObject
            {
                ["id"] = noteId,
                ["fields"] = new JsonObject(fields.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))),
            },
        }, ct).ConfigureAwait(false);

        if (reselect)
        {
            try { await InvokeAsync("guiBrowse", new JsonObject { ["query"] = $"nid:{noteId}" }, ct).ConfigureAwait(false); }
            catch { }
        }
    }

    public void Dispose() => http.Dispose();
}

/// <summary>Which note fields receive the sentence audio and the GIF.</summary>
public static class MediaFields
{
    private static readonly Regex SentenceAudio = new(@"sent(ence)?[\s_\-]*audio|audio[\s_\-]*sent(ence)?|^sentaudio$|^voice(line)?$", RegexOptions.IgnoreCase);
    private static readonly Regex Gif = new(@"\bgif\b|anim(ation|ated)?", RegexOptions.IgnoreCase);
    private static readonly Regex Picture = new(@"picture|image|screenshot|^pic$|^img$", RegexOptions.IgnoreCase);

    /// <summary>The configured field if the note has it, else the first field whose name looks like sentence audio.</summary>
    public static string? SentenceAudioField(IReadOnlyList<(string Name, string Value)> fields, string configured) =>
        Pick(fields, configured, f => SentenceAudio.IsMatch(f.Name));

    /// <summary>
    /// The configured field; else a field named for GIFs; else, when the GIF may replace the
    /// screenshot, the picture field holding the screenshot Hachidori just added.
    /// </summary>
    public static string? GifField(IReadOnlyList<(string Name, string Value)> fields, string configured, string? screenshotFile, bool replaceScreenshot)
    {
        if (Pick(fields, configured, f => Gif.IsMatch(f.Name)) is { } named)
            return named;
        if (!replaceScreenshot)
            return null;
        var holding = fields.Where(f => screenshotFile is not null && f.Value.Contains(screenshotFile, StringComparison.Ordinal)).ToList();
        return holding.FirstOrDefault(f => Picture.IsMatch(f.Name)).Name ?? holding.FirstOrDefault().Name
            ?? fields.FirstOrDefault(f => Picture.IsMatch(f.Name) && f.Value.Trim().Length == 0).Name;
    }

    private static string? Pick(IReadOnlyList<(string Name, string Value)> fields, string configured, Func<(string Name, string Value), bool> match)
    {
        configured = configured.Trim();
        if (configured.Length > 0)
            return fields.Any(f => string.Equals(f.Name, configured, StringComparison.OrdinalIgnoreCase))
                ? fields.First(f => string.Equals(f.Name, configured, StringComparison.OrdinalIgnoreCase)).Name
                : null;
        return fields.Where(match).Select(f => f.Name).FirstOrDefault();
    }

    /// <summary>The field's new HTML: the GIF in place of the screenshot image, else added after what is there.</summary>
    public static string WithGif(string value, string gifFile, string? screenshotFile)
    {
        var image = $"<img src=\"{gifFile}\">";
        if (screenshotFile is not null && value.Contains(screenshotFile, StringComparison.Ordinal))
            return Regex.Replace(value, $"<img[^>]*{Regex.Escape(screenshotFile)}[^>]*>", image, RegexOptions.IgnoreCase);
        return value.Trim().Length == 0 ? image : $"{value}{image}";
    }
}
