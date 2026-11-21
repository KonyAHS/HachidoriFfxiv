using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Media;
using HachidoriFfxiv.Offline;
using PlannedImage = HachidoriFfxiv.Anki.AnkiNoteRenderer.PlannedImage;

namespace HachidoriFfxiv.Anki;

/// <summary>
/// Anki mining without the Hachidori host: the plugin renders the note from
/// the user's Anki Template in Hachidori's saved settings, as Hachidori's
/// anki-mining.js and anki-worker.js do, and writes it through AnkiConnect
/// itself. The mining requests are the ones the plugin sends to the host.
/// Duplicates are Anki's own first-field check for the note type (Hachidori's
/// fallback when its word index is unavailable); its word index, overwriting
/// existing notes, Smaller Anki cards and the pitch-graph markers stay live-only.
/// </summary>
public sealed class OfflineAnkiMiner : IDisposable
{
    private const string ConfigChanged = "Anki configuration changed. Refresh this result before adding a note.";
    private static readonly Dictionary<string, string> AudioExtensions = new()
    {
        ["audio/aac"] = "aac", ["audio/flac"] = "flac", ["audio/mp4"] = "m4a", ["audio/mpeg"] = "mp3", ["audio/ogg"] = "ogg",
        ["audio/wav"] = "wav", ["audio/webm"] = "webm", ["audio/x-wav"] = "wav", ["application/ogg"] = "ogg",
    };
    private readonly Func<JsonObject> snapshot;
    private readonly Func<LocalEngine> engine;
    private readonly HttpClient http;
    private readonly SemaphoreSlim writes = new(1, 1);
    private AnkiConnect? anki;
    private string? ankiEndpoint;

    public OfflineAnkiMiner(Func<JsonObject> snapshot, Func<LocalEngine> engine, HttpClient http)
    {
        this.snapshot = snapshot;
        this.engine = engine;
        this.http = http;
    }

    private JsonObject Options => snapshot()["options"] as JsonObject ?? new JsonObject();

    // ---------------------------------------------------------- configuration

    /// <summary>reader-options.js ankiTemplateConfig: the Template's settings with the shared Anki address.</summary>
    private JsonObject? TemplateConfig(string? templateId)
    {
        var settings = Options["anki"] as JsonObject;
        var templates = (settings?["templates"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var template = string.IsNullOrWhiteSpace(templateId) ? templates.FirstOrDefault()
            : templates.FirstOrDefault(t => AnkiFields.Str(t["id"]) == templateId);
        if (template is null) return null;
        var config = (JsonObject)template.DeepClone();
        config["url"] = AnkiFields.Str(settings?["url"]) is { Length: > 0 } url ? url : "http://127.0.0.1:8765";
        config["apiKey"] = AnkiFields.Str(settings?["apiKey"]);
        return config;
    }

    private AnkiConnect Anki(JsonObject config)
    {
        var url = AnkiFields.Str(config["url"]);
        var key = AnkiFields.Str(config["apiKey"]);
        var endpoint = url + "\n" + key;
        if (anki is null || ankiEndpoint != endpoint)
        {
            anki?.Dispose();
            anki = new AnkiConnect(() => url, () => key);
            ankiEndpoint = endpoint;
        }
        return anki;
    }

    private static string ConfigKey(string? templateId, JsonObject config) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["templateId"] = templateId, ["config"] = config.DeepClone(),
        }.ToJsonString())));

    private sealed record Current(JsonObject Config, string ConfigKey, List<string> Fields, Dictionary<string, FieldTemplate> Templates, List<string> Errors);

    private async Task<Current> ConfigurationAsync(string? templateId, CancellationToken ct)
    {
        var config = TemplateConfig(templateId) ?? throw new InvalidOperationException("The selected Anki Template is no longer available.");
        var key = ConfigKey(templateId, config);
        var model = AnkiFields.Str(config["model"]);
        var deck = AnkiFields.Str(config["deck"]);
        if (model.Length == 0) return new Current(config, key, [], [], ["Choose an Anki note type in Hachidori's Anki settings."]);
        var client = Anki(config);
        var errors = new List<string>();
        var decks = Names(await client.InvokeAsync("deckNames", null, ct).ConfigureAwait(false));
        var models = Names(await client.InvokeAsync("modelNames", null, ct).ConfigureAwait(false));
        var fields = models.Contains(model)
            ? Names(await client.InvokeAsync("modelFieldNames", new JsonObject { ["modelName"] = model }, ct).ConfigureAwait(false))
            : [];
        // anki.js ankiAvailability.
        if (!decks.Contains(deck)) errors.Add(deck.Length > 0 ? $"Anki has no deck named “{deck}”. Choose an available deck." : "Choose an available deck.");
        if (!models.Contains(model)) errors.Add($"Anki has no note type named “{model}”. Choose an available note type.");
        var (templates, templateErrors) = AnkiFields.Resolve(config, fields);
        errors.AddRange(templateErrors);
        if (fields.Count > 0)
        {
            var first = templates[fields[0]].Value;
            if (first.Trim().Length == 0)
                errors.Add($"Map the first field, “{fields[0]}”, of note type “{model}” before adding notes. Anki requires it.");
            if (AnkiFields.MarkerNames(first).Any(m => m is "screenshot" or "sentence-audio" or "gif"))
                errors.Add($"Captured media cannot be mapped to the first field, “{fields[0]}”.");
        }
        errors.AddRange(OfflineLimits(config, templates));
        return new Current(config, key, fields, templates, errors);
    }

    /// <summary>What this Template needs that offline mining does not do yet.</summary>
    private IEnumerable<string> OfflineLimits(JsonObject config, Dictionary<string, FieldTemplate> templates)
    {
        const string Suffix = " Open Chrome with Hachidori to mine with this Template.";
        var unsupported = templates.Values.SelectMany(t => AnkiFields.MarkerNames(t.Value)).Where(AnkiFields.UnsupportedOffline.Contains).Distinct().ToList();
        if (unsupported.Count > 0)
            yield return $"Offline mining cannot fill {string.Join(", ", unsupported.Select(m => $"{{{m}}}"))} yet.{Suffix}";
        if (AnkiFields.Str(config["duplicateBehavior"]) == "overwrite")
            yield return $"Offline mining does not overwrite existing notes yet.{Suffix}";
        if (Options["smallerAnkiCards"] is JsonValue small && small.GetValueKind() == JsonValueKind.True)
            yield return $"Offline mining does not make Smaller Anki cards yet.{Suffix}";
        if (Options["profiles"] is JsonArray { Count: > 0 })
            yield return $"Offline mining does not support language profiles yet.{Suffix}";
    }

    private static List<string> Names(JsonNode? reply) =>
        reply is JsonArray array ? array.Select(AnkiFields.Str).ToList() : throw new InvalidOperationException("AnkiConnect returned an invalid list.");

    // ----------------------------------------------------------------- status

    public async Task<HachidoriService.AnkiStatus> StatusAsync(string? templateId, CancellationToken ct = default)
    {
        try
        {
            var current = await ConfigurationAsync(templateId, ct).ConfigureAwait(false);
            return new HachidoriService.AnkiStatus(current.Errors.Count == 0, current.ConfigKey,
                current.Errors.Count == 0 ? null : string.Join("\n", current.Errors));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return new HachidoriService.AnkiStatus(false, null, ex.Message);
        }
    }

    // ---------------------------------------------------------------- prepare

    private sealed record Prepared(Current Current, JsonObject Note, string FirstField, List<PlannedImage> Images, AnkiConnect Anki);

    private async Task<Prepared> PrepareAsync(JsonObject request, string audio, CancellationToken ct)
    {
        var templateId = AnkiFields.Str(request["templateId"]) is { Length: > 0 } id ? id : null;
        var current = await ConfigurationAsync(templateId, ct).ConfigureAwait(false);
        if (AnkiFields.Str(request["configKey"]) != current.ConfigKey) throw new InvalidOperationException(ConfigChanged);
        if (current.Errors.Count > 0) throw new InvalidOperationException(string.Join("\n", current.Errors));
        var local = engine();
        if (!JsonNumber.TryGet(request["generation"], out var generation) || (long)generation != local.Generation)
            throw new InvalidOperationException("The dictionaries changed since this lookup. Look up this result again before adding it.");
        if (AnkiFields.Str(request["term"]?["expression"]).Length == 0)
            throw new InvalidOperationException("Mining requires a current dictionary result.");

        var images = new List<PlannedImage>();
        var fields = Render(request, current.Templates, audio, local, images);
        var firstField = current.Fields[0];
        if (fields[firstField].Trim().Length == 0)
        {
            var template = current.Templates[firstField].Value;
            var expression = AnkiFields.Str(request["term"]?["expression"]);
            throw new InvalidOperationException($"The first field of note type “{AnkiFields.Str(current.Config["model"])}”, “{firstField}”, is empty for this result"
                + (template.Length > 0 ? $": its template {template} produced nothing for “{expression}”" : string.Empty) + ". Anki requires it.");
        }
        var note = new JsonObject
        {
            ["deckName"] = AnkiFields.Str(current.Config["deck"]),
            ["modelName"] = AnkiFields.Str(current.Config["model"]),
            ["fields"] = new JsonObject(fields.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))),
            ["options"] = NoteOptions(current.Config, request["allowDuplicate"] is JsonValue a && a.GetValueKind() == JsonValueKind.True),
            ["tags"] = new JsonArray((current.Config["tags"] as JsonArray ?? []).Select(AnkiFields.Str).Distinct().Select(t => (JsonNode?)t).ToArray()),
        };
        return new Prepared(current, note, firstField, images, Anki(current.Config));
    }

    private Dictionary<string, string> Render(JsonObject request, Dictionary<string, FieldTemplate> templates, string audio,
        LocalEngine local, List<PlannedImage> images) => AnkiNoteRenderer.RenderFields(snapshot(), request, templates, audio, local, images);

    /// <summary>anki-duplicates.js ankiNoteOptions.</summary>
    private static JsonObject NoteOptions(JsonObject config, bool allowDuplicate)
    {
        var deck = AnkiFields.Str(config["duplicateScope"]) == "deck";
        return new JsonObject
        {
            ["allowDuplicate"] = allowDuplicate || AnkiFields.Str(config["duplicateBehavior"]) == "new",
            ["duplicateScope"] = deck ? "deck" : "collection",
            ["duplicateScopeOptions"] = new JsonObject
            {
                ["deckName"] = deck ? AnkiFields.Str(config["deck"]) : null,
                ["checkChildren"] = deck,
                ["checkAllModels"] = false,
            },
        };
    }

    // --------------------------------------------------------------- decision

    private sealed record Decision(string State, bool CanAdd, string? Error, long[] NoteIds);

    private async Task<Decision> DecideAsync(Prepared prepared, JsonObject request, CancellationToken ct)
    {
        if (request["allowDuplicate"] is JsonValue a && a.GetValueKind() == JsonValueKind.True) return new Decision("addable", true, null, []);
        var config = prepared.Current.Config;
        var behavior = AnkiFields.Str(config["duplicateBehavior"]);
        var check = await CheckAsync(prepared, allowDuplicate: false, ct).ConfigureAwait(false);
        if (!check.Duplicate) return new Decision(check.Addable ? "addable" : "invalid", check.Addable, check.Error, []);
        var noteIds = await DuplicateNotesAsync(prepared, ct).ConfigureAwait(false);
        if (behavior == "new")
        {
            var addable = await CheckAsync(prepared, allowDuplicate: true, ct).ConfigureAwait(false);
            if (!addable.Addable) return new Decision("invalid", false, addable.Error, noteIds);
        }
        return new Decision("duplicate", behavior == "new", null, noteIds);
    }

    private sealed record Check(bool Duplicate, bool Addable, string? Error);

    private static async Task<Check> CheckAsync(Prepared prepared, bool allowDuplicate, CancellationToken ct)
    {
        var note = (JsonObject)prepared.Note.DeepClone();
        ((JsonObject)note["options"]!)["allowDuplicate"] = allowDuplicate;
        var reply = await prepared.Anki.InvokeAsync("canAddNotesWithErrorDetail", new JsonObject { ["notes"] = new JsonArray(note) }, ct).ConfigureAwait(false);
        var item = (reply as JsonArray)?.FirstOrDefault() as JsonObject ?? throw new InvalidOperationException("AnkiConnect returned invalid duplicate check results.");
        var error = AnkiFields.Str(item["error"]) is { Length: > 0 } e ? e : null;
        var canAdd = item["canAdd"] is JsonValue c && c.GetValueKind() == JsonValueKind.True;
        return new Check(IsDuplicateError(error), canAdd && error is null, error);
    }

    private static bool IsDuplicateError(string? error) =>
        error is not null && Regex.IsMatch(error, "cannot create note because it is a duplicate", RegexOptions.IgnoreCase);

    /// <summary>anki-duplicates.js findAnkiDuplicateNotes: Anki's exact first-field duplicates of the note type in scope.</summary>
    private static async Task<long[]> DuplicateNotesAsync(Prepared prepared, CancellationToken ct)
    {
        var config = prepared.Current.Config;
        var model = AnkiFields.Str(config["model"]);
        var models = await prepared.Anki.InvokeAsync("modelNamesAndIds", null, ct).ConfigureAwait(false);
        if (models?[model] is not JsonValue idValue || !JsonNumber.TryGet(idValue, out var modelId)) return [];
        var text = Regex.Replace(AnkiFields.Str(prepared.Note["fields"]?[prepared.FirstField]), @"[\\""]", m => "\\" + m.Value);
        var found = await prepared.Anki.InvokeAsync("findNotes", new JsonObject { ["query"] = $"\"dupe:{(long)modelId},{text}\"" }, ct).ConfigureAwait(false);
        var ids = (found as JsonArray ?? []).Select(n => (long)JsonNumber.Or(n, 0)).Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0 || AnkiFields.Str(config["duplicateScope"]) != "deck") return ids;
        var infos = await prepared.Anki.InvokeAsync("notesInfo", new JsonObject { ["notes"] = new JsonArray(ids.Select(i => (JsonNode?)i).ToArray()) }, ct).ConfigureAwait(false);
        var cards = (infos as JsonArray ?? []).SelectMany(i => (i?["cards"] as JsonArray ?? []).Select(c => (JsonNode?)c?.DeepClone())).ToArray();
        if (cards.Length == 0) return [];
        var details = await prepared.Anki.InvokeAsync("cardsInfo", new JsonObject { ["cards"] = new JsonArray(cards) }, ct).ConfigureAwait(false);
        var deck = AnkiFields.Str(config["deck"]).ToLowerInvariant();
        var inScope = (details as JsonArray ?? []).OfType<JsonObject>()
            .Where(card => AnkiFields.Str(card["deckName"]).ToLowerInvariant() is var name && (name == deck || name.StartsWith(deck + "::", StringComparison.Ordinal)))
            .Select(card => (long)JsonNumber.Or(card["note"], 0)).ToHashSet();
        return ids.Where(inScope.Contains).ToArray();
    }

    // -------------------------------------------------------------- preflight

    public async Task<AnkiDecision> PreflightAsync(JsonObject request, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(request, string.Empty, ct).ConfigureAwait(false);
        var decision = await DecideAsync(prepared, request, ct).ConfigureAwait(false);
        return ToReply(decision, prepared);
    }

    private static AnkiDecision ToReply(Decision decision, Prepared prepared, string[]? warnings = null) => new()
    {
        State = decision.State,
        CanAdd = decision.CanAdd,
        Error = decision.Error,
        NoteIds = decision.NoteIds,
        Screenshot = prepared.Current.Config["captureScreenshot"] is JsonValue s && s.GetValueKind() == JsonValueKind.True
                     && prepared.Current.Templates.Values.Any(t => AnkiFields.MarkerNames(t.Value).Any(m => m is "screenshot" or "gif")),
        Warnings = warnings ?? [],
    };

    // ----------------------------------------------------------------- submit

    public async Task<AnkiDecision> SubmitAsync(JsonObject request, ScreenshotMedia? screenshot, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await WriteAsync((JsonObject)request.DeepClone(), screenshot, ct).ConfigureAwait(false);
        }
        finally
        {
            writes.Release();
        }
    }

    private async Task<AnkiDecision> WriteAsync(JsonObject request, ScreenshotMedia? screenshot, CancellationToken ct)
    {
        var warnings = new List<string>();
        var files = new List<MediaFile>();
        // The game screenshot fills {screenshot} under its own name, as the linked client's media does.
        if (screenshot is not null)
        {
            request["screenshot"] = new JsonObject { ["token"] = screenshot.Token, ["filename"] = screenshot.Filename };
            request["captureUnavailable"] = new JsonArray((request["captureUnavailable"] as JsonArray ?? [])
                .Where(n => AnkiFields.Str(n) != "screenshot").Select(n => n?.DeepClone()).ToArray());
            files.Add(new MediaFile(screenshot.Filename, screenshot.Base64, "picture", "Screenshot", Required: false));
        }
        else
        {
            request.Remove("screenshot");
        }

        // Pronunciation first, so {audio} is part of the note (anki-worker.js prepareAdd).
        var audio = string.Empty;
        var config = TemplateConfig(AnkiFields.Str(request["templateId"]) is { Length: > 0 } id ? id : null);
        var wantsAudio = config is not null && (config["fieldTemplates"] as JsonObject ?? new JsonObject())
            .Any(f => AnkiFields.MarkerNames(AnkiFields.Str(f.Value?["value"])).Contains("audio"));
        if (wantsAudio)
        {
            try
            {
                var term = request["term"];
                var file = await OfflineAudio.FetchFileAsync(http, Options["audioSources"], AnkiFields.Str(term?["expression"]),
                    AnkiFields.Str(term?["reading"]), ct).ConfigureAwait(false);
                if (file is null)
                {
                    warnings.Add("Pronunciation: no downloadable pronunciation is available for this result.");
                }
                else
                {
                    var filename = AnkiNoteRenderer.MediaFilename(file.Data, AudioExtension(file));
                    audio = $"[sound:{filename}]";
                    files.Add(new MediaFile(filename, Convert.ToBase64String(file.Data), "audio", "Pronunciation", Required: false));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                warnings.Add($"Pronunciation: {ex.Message}");
            }
        }

        var prepared = await PrepareAsync(request, audio, ct).ConfigureAwait(false);
        var decision = await DecideAsync(prepared, request, ct).ConfigureAwait(false);
        if (!decision.CanAdd) return ToReply(decision, prepared);

        var note = prepared.Note;
        var noteFields = (JsonObject)note["fields"]!;
        var local = engine();
        foreach (var image in prepared.Images.Where(i => noteFields.Any(f => AnkiFields.Str(f.Value).Contains(i.Filename, StringComparison.Ordinal))))
        {
            var bytes = local.Media(image.Dictionary, image.Path)
                ?? throw new InvalidOperationException("The dictionary image is no longer available or is malformed.");
            files.Add(new MediaFile(image.Filename, Convert.ToBase64String(bytes), "picture", "dictionary image", Required: true));
        }
        warnings.AddRange(await AttachMediaAsync(note, files, prepared.Anki, ct).ConfigureAwait(false));

        long noteId;
        try
        {
            var reply = await prepared.Anki.InvokeAsync("addNote", new JsonObject { ["note"] = note }, ct).ConfigureAwait(false);
            noteId = (long)JsonNumber.Or(reply, 0);
        }
        catch (InvalidOperationException ex) when (IsDuplicateError(ex.Message))
        {
            var firstValue = AnkiFields.Str(noteFields[prepared.FirstField]).Trim();
            return new AnkiDecision
            {
                State = "duplicate",
                NoteIds = await DuplicateNotesAsync(prepared, ct).ConfigureAwait(false),
                Error = $"Anki already has a note in deck “{AnkiFields.Str(note["deckName"])}” (note type “{AnkiFields.Str(note["modelName"])}”) whose first field “{prepared.FirstField}” is "
                        + (firstValue.Length > 0 ? $"“{firstValue}”." : "empty."),
            };
        }
        if (noteId <= 0)
            throw new InvalidOperationException($"Anki did not return a valid note ID for the added note in deck “{AnkiFields.Str(note["deckName"])}”. Check the note in Anki.");
        foreach (var warning in warnings) DiagnosticLog.Write("anki", $"Offline note warning: {warning}");
        return new AnkiDecision { State = "added", CanAdd = true, NoteIds = [noteId], Warnings = warnings.ToArray() };
    }

    private sealed record MediaFile(string Filename, string Data, string Type, string Kind, bool Required);

    /// <summary>
    /// anki-media.js attachAnkiAddMedia: a file that is a field's whole value goes
    /// with addNote, which stores it and fills the field; one inside rich HTML is
    /// stored first so it keeps its place.
    /// </summary>
    private static async Task<List<string>> AttachMediaAsync(JsonObject note, List<MediaFile> files, AnkiConnect anki, CancellationToken ct)
    {
        var warnings = new List<string>();
        var fields = (JsonObject)note["fields"]!;
        var uploads = new List<MediaFile>();
        string Reference(MediaFile file) => file.Type == "audio" ? $"[sound:{file.Filename}]" : $"<img src=\"{file.Filename}\">";
        void Failed(MediaFile file, string message)
        {
            if (file.Required) throw new InvalidOperationException(message);
            foreach (var (name, value) in fields.ToList()) fields[name] = AnkiFields.Str(value).Replace(Reference(file), string.Empty);
            warnings.Add($"{file.Kind}: {message}");
        }
        foreach (var file in files)
        {
            var holding = fields.Where(f => AnkiFields.Str(f.Value).Contains(file.Filename, StringComparison.Ordinal)).Select(f => f.Key).ToList();
            if (holding.Count == 0) continue;
            if (!file.Required && holding.All(f => AnkiFields.Str(fields[f]) == Reference(file)))
            {
                foreach (var f in holding) fields[f] = string.Empty;
                if (note[file.Type] is not JsonArray list) note[file.Type] = list = new JsonArray();
                list.Add(new JsonObject
                {
                    ["filename"] = file.Filename, ["data"] = file.Data,
                    ["fields"] = new JsonArray(holding.Select(h => (JsonNode?)h).ToArray()), ["deleteExisting"] = false,
                });
            }
            else
            {
                uploads.Add(file);
            }
        }
        if (uploads.Count == 0) return warnings;
        var results = await anki.InvokeAsync("multi", new JsonObject
        {
            ["actions"] = new JsonArray(uploads.Select(u => (JsonNode?)new JsonObject
            {
                ["action"] = "storeMediaFile",
                ["params"] = new JsonObject { ["filename"] = u.Filename, ["data"] = u.Data, ["deleteExisting"] = false },
            }).ToArray()),
        }, ct).ConfigureAwait(false);
        var replies = results as JsonArray ?? [];
        for (var i = 0; i < uploads.Count; i++)
        {
            var reply = i < replies.Count ? replies[i] : null;
            var error = reply is JsonObject o ? AnkiFields.Str(o["error"]) : null;
            var stored = reply is JsonObject r ? AnkiFields.Str(r["result"]) : AnkiFields.Str(reply);
            if (!string.IsNullOrEmpty(error)) Failed(uploads[i], error);
            else if (stored != uploads[i].Filename) Failed(uploads[i], "Anki stored media under a different filename.");
        }
        return warnings;
    }

    // ----------------------------------------------------------------- browse

    public async Task BrowseAsync(long[] noteIds, string expression, string? templateId, CancellationToken ct = default)
    {
        var config = TemplateConfig(templateId) ?? throw new InvalidOperationException("The selected Anki Template is no longer available.");
        var query = noteIds.Length > 0 ? $"nid:{string.Join(",", noteIds.Distinct())}" : BrowseQuery(expression);
        await Anki(config).InvokeAsync("guiBrowse", new JsonObject { ["query"] = query }, ct).ConfigureAwait(false);
    }

    private static string BrowseQuery(string expression) =>
        "\"" + Regex.Replace(expression.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"), @"[\\""*_:]", m => "\\" + m.Value) + "\"";

    // ---------------------------------------------------------------- helpers

    private static string AudioExtension(OfflineAudio.AudioFile file)
    {
        var mime = (file.MediaType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (AudioExtensions.TryGetValue(mime, out var extension)) return extension;
        var suffix = Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) ? uri.AbsolutePath.Split('.').Last().ToLowerInvariant() : string.Empty;
        return Regex.IsMatch(suffix, "^[a-z0-9]+$") ? suffix : "bin";
    }

    public void Dispose()
    {
        anki?.Dispose();
        writes.Dispose();
    }
}
