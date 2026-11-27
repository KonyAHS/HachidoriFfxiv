using System;
using System.Collections.Concurrent;
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
/// Anki mining without the Hachidori host, plus a fast batched local-AnkiConnect
/// preflight path shared by offline and local-live modes: discovers decks/models/fields
/// in one cached <c>multi</c> call and checks an entire popup's entries in 1-2 batched
/// AnkiConnect requests instead of per-entry round trips or linked audio probes.
/// </summary>
public sealed class OfflineAnkiMiner : IOfflineAnkiMiner
{
    private const string ConfigChanged = "Anki configuration changed. Refresh this result before adding a note.";
    private static readonly TimeSpan ConfigCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly Regex ClozePattern = new(@"\{\{c", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> GlossaryMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "definition", "glossary", "glossary-brief", "glossary-no-dictionary", "glossary-plain",
        "glossary-plain-no-dictionary", "glossary-first", "glossary-first-brief", "glossary-first-no-dictionary",
        "main-definition", "jpmn-primary-definition",
    };
    private static readonly Dictionary<string, string> AudioExtensions = new()
    {
        ["audio/aac"] = "aac", ["audio/flac"] = "flac", ["audio/mp4"] = "m4a", ["audio/mpeg"] = "mp3", ["audio/ogg"] = "ogg",
        ["audio/wav"] = "wav", ["audio/webm"] = "webm", ["audio/x-wav"] = "wav", ["application/ogg"] = "ogg",
    };
    private readonly Func<JsonObject> snapshot;
    private readonly Func<LocalEngine?> engine;
    private readonly HttpClient http;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly object ankiGate = new();
    private readonly ConcurrentDictionary<string, CachedConfig> configCache = new(StringComparer.Ordinal);
    private AnkiConnect? anki;
    private string? ankiEndpoint;

    public OfflineAnkiMiner(Func<JsonObject> snapshot, Func<LocalEngine?> engine, HttpClient http)
    {
        this.snapshot = snapshot;
        this.engine = engine;
        this.http = http;
    }

    private JsonObject Options => snapshot()["options"] as JsonObject ?? new JsonObject();

    /// <summary>Drops cached AnkiConnect deck/model/field discovery (e.g. when Hachidori settings change).</summary>
    public void InvalidateCache() => configCache.Clear();

    /// <summary>Returns the configured <c>duplicateBehavior</c> for a template (or empty string if unavailable).</summary>
    public string DuplicateBehaviorFor(string? templateId) =>
        TemplateConfig(templateId) is { } cfg ? AnkiFields.Str(cfg["duplicateBehavior"]) : string.Empty;

    // ---------------------------------------------------------- configuration

    /// <summary>reader-options.js ankiTemplateConfig: the Template's settings with the shared Anki address.</summary>
    private JsonObject? TemplateConfig(string? templateId)
    {
        var settings = EffectiveAnkiOptions();
        var templates = (settings?["templates"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var template = string.IsNullOrWhiteSpace(templateId)
            ? templates.FirstOrDefault()
            : templates.FirstOrDefault(t => AnkiFields.Str(t["id"]) == templateId);
        // If standalone template was selected before its deck/model were configured, fall back
        // to any synced template rather than failing mining with "no longer available".
        if (template is null && templateId == StandaloneDictionaries.StandaloneTemplateId)
            template = templates.FirstOrDefault();
        if (template is null) return null;
        var config = (JsonObject)template.DeepClone();
        config["url"] = AnkiFields.Str(settings?["url"]) is { Length: > 0 } url ? url : "http://127.0.0.1:8765";
        config["apiKey"] = AnkiFields.Str(settings?["apiKey"]);
        return config;
    }

    private JsonObject? EffectiveAnkiOptions()
    {
        if (Options["profiles"] is JsonArray profiles && profiles.Count > 0)
        {
            var jaProfile = profiles.OfType<JsonObject>().FirstOrDefault(p =>
                string.Equals(AnkiFields.Str(p["language"]), "ja", StringComparison.OrdinalIgnoreCase)
                || string.Equals(AnkiFields.Str(p["id"]), "ja", StringComparison.OrdinalIgnoreCase));
            if (jaProfile?["options"]?["anki"] is JsonObject profAnki)
                return profAnki;
        }
        return Options["anki"] as JsonObject;
    }

    private AnkiConnect Anki(JsonObject config)
    {
        var url = AnkiFields.Str(config["url"]);
        var key = AnkiFields.Str(config["apiKey"]);
        var endpoint = url + "\n" + key;
        lock (ankiGate)
        {
            if (anki is null || ankiEndpoint != endpoint)
            {
                anki?.Dispose();
                anki = new AnkiConnect(() => url, () => key);
                ankiEndpoint = endpoint;
            }
            return anki;
        }
    }

    private static string ConfigKey(string? templateId, JsonObject config) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["templateId"] = templateId, ["config"] = config.DeepClone(),
        }.ToJsonString())));

    private sealed record Current(
        JsonObject Config,
        string ConfigKey,
        long? ModelId,
        List<string> Fields,
        Dictionary<string, FieldTemplate> Templates,
        List<string> Errors,
        bool WantsScreenshot);

    private sealed record CachedConfig(string ConfigKey, DateTime CachedAtUtc, Task<Current> Task);

    private Task<Current> ConfigurationAsync(string? templateId, CancellationToken ct, bool requireOfflineLimits = true)
    {
        var config = TemplateConfig(templateId) ?? throw new InvalidOperationException("The selected Anki Template is no longer available.");
        var resolvedTemplateId = AnkiFields.Str(config["id"]) is { Length: > 0 } id ? id : templateId;
        var key = ConfigKey(resolvedTemplateId, config);
        var cacheKey = $"{(requireOfflineLimits ? "off" : "live")}\u0001{resolvedTemplateId ?? string.Empty}";
        var now = DateTime.UtcNow;

        if (configCache.TryGetValue(cacheKey, out var cached)
            && cached.ConfigKey == key
            && (now - cached.CachedAtUtc) < ConfigCacheTtl
            && !cached.Task.IsFaulted
            && !cached.Task.IsCanceled)
        {
            return cached.Task.WaitAsync(ct);
        }

        // Coalesce concurrent cold discovery onto a single AnkiConnect multi call.
        var task = DiscoverConfigurationAsync(config, key, requireOfflineLimits);
        var entry = new CachedConfig(key, now, task);
        configCache[cacheKey] = entry;
        _ = task.ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || t.Result.Errors.Count > 0)
                configCache.TryRemove(new KeyValuePair<string, CachedConfig>(cacheKey, entry));
        }, TaskScheduler.Default);
        return task.WaitAsync(ct);
    }

    private async Task<Current> DiscoverConfigurationAsync(JsonObject config, string key, bool requireOfflineLimits)
    {
        var model = AnkiFields.Str(config["model"]);
        var deck = AnkiFields.Str(config["deck"]);
        if (model.Length == 0)
            return new Current(config, key, null, [], [], ["Choose an Anki note type in Hachidori's Anki settings."], false);

        var client = Anki(config);
        var errors = new List<string>();
        // One multi round trip: deckNames, modelNamesAndIds (also supplies modelId for dupe:),
        // and speculatively modelFieldNames for the configured model.
        var replies = await client.MultiAsync(
        [
            ("deckNames", null),
            ("modelNamesAndIds", null),
            ("modelFieldNames", new JsonObject { ["modelName"] = model }),
        ], readOnly: true, CancellationToken.None).ConfigureAwait(false);

        if (replies[0].Error is { } deckErr) throw new InvalidOperationException($"AnkiConnect deckNames: {deckErr}");
        if (replies[1].Error is { } modelErr) throw new InvalidOperationException($"AnkiConnect modelNamesAndIds: {modelErr}");

        var decks = Names(replies[0].Result);
        var modelMap = replies[1].Result as JsonObject
            ?? throw new InvalidOperationException("AnkiConnect returned an invalid model map.");
        var hasModel = modelMap.ContainsKey(model);
        long? modelId = hasModel && modelMap[model] is JsonValue idVal && JsonNumber.TryGet(idVal, out var idNum) && (long)idNum > 0
            ? (long)idNum
            : null;

        List<string> fields;
        if (hasModel)
        {
            if (replies[2].Error is { } fieldErr) throw new InvalidOperationException($"AnkiConnect modelFieldNames: {fieldErr}");
            fields = Names(replies[2].Result);
        }
        else
        {
            fields = [];
        }

        // anki.js ankiAvailability.
        if (!decks.Contains(deck)) errors.Add(deck.Length > 0 ? $"Anki has no deck named “{deck}”. Choose an available deck." : "Choose an available deck.");
        if (!hasModel) errors.Add($"Anki has no note type named “{model}”. Choose an available note type.");
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
        if (requireOfflineLimits)
            errors.AddRange(OfflineLimits(config, templates));

        var wantsScreenshot = config["captureScreenshot"] is JsonValue s && s.GetValueKind() == JsonValueKind.True
                              && templates.Values.Any(t => AnkiFields.MarkerNames(t.Value).Any(m => m is "screenshot" or "gif"));
        return new Current(config, key, modelId, fields, templates, errors, wantsScreenshot);
    }

    /// <summary>What this Template needs that offline mining does not do yet.</summary>
    private IEnumerable<string> OfflineLimits(JsonObject config, Dictionary<string, FieldTemplate> templates)
    {
        const string Suffix = " Open Chrome with Hachidori to mine with this Template.";
        var unsupported = templates.Values.SelectMany(t => AnkiFields.MarkerNames(t.Value)).Where(AnkiFields.UnsupportedOffline.Contains).Distinct().ToList();
        if (unsupported.Count > 0)
            yield return $"Offline mining cannot fill {string.Join(", ", unsupported.Select(m => $"{{{m}}}"))} yet.{Suffix}";
        if (Options["smallerAnkiCards"] is JsonValue small && small.GetValueKind() == JsonValueKind.True)
            yield return $"Offline mining does not make Smaller Anki cards yet.{Suffix}";
    }

    private static List<string> Names(JsonNode? reply) =>
        reply is JsonArray array ? array.Select(AnkiFields.Str).ToList() : throw new InvalidOperationException("AnkiConnect returned an invalid list.");

    // ----------------------------------------------------------------- status

    public async Task<AnkiStatus> StatusAsync(string? templateId, CancellationToken ct = default)
    {
        try
        {
            var current = await ConfigurationAsync(templateId, ct, requireOfflineLimits: true).ConfigureAwait(false);
            return new AnkiStatus(current.Errors.Count == 0, current.ConfigKey,
                current.Errors.Count == 0 ? null : string.Join("\n", current.Errors));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return new AnkiStatus(false, null, ex.Message);
        }
    }

    // ---------------------------------------------------------------- prepare

    private sealed record Prepared(Current Current, JsonObject Note, string FirstField, List<PlannedImage> Images, AnkiConnect Anki);

    private LocalEngine RequireEngine() =>
        engine() ?? throw new InvalidOperationException("The offline dictionary engine is unavailable.");

    private async Task<Prepared> PrepareAsync(JsonObject request, string audio, CancellationToken ct)
    {
        var templateId = AnkiFields.Str(request["templateId"]) is { Length: > 0 } id ? id : null;
        var current = await ConfigurationAsync(templateId, ct, requireOfflineLimits: true).ConfigureAwait(false);
        if (AnkiFields.Str(request["configKey"]) != current.ConfigKey) throw new InvalidOperationException(ConfigChanged);
        if (current.Errors.Count > 0) throw new InvalidOperationException(string.Join("\n", current.Errors));
        var local = RequireEngine();
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
        var note = BuildNote(current.Config, fields, request["allowDuplicate"] is JsonValue a && a.GetValueKind() == JsonValueKind.True);
        return new Prepared(current, note, firstField, images, Anki(current.Config));
    }

    private static JsonObject BuildNote(JsonObject config, IReadOnlyDictionary<string, string> fields, bool allowDuplicate) =>
        new()
        {
            ["deckName"] = AnkiFields.Str(config["deck"]),
            ["modelName"] = AnkiFields.Str(config["model"]),
            ["fields"] = new JsonObject(fields.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))),
            ["options"] = NoteOptions(config, allowDuplicate),
            ["tags"] = new JsonArray((config["tags"] as JsonArray ?? []).Select(AnkiFields.Str).Distinct().Select(t => (JsonNode?)t).ToArray()),
        };

    /// <summary>
    /// Renders only the fields Anki's duplicate/validity check inspects: the first field plus any
    /// field with literal cloze markers (<c>{{c</c>). Skips expensive glossary HTML rendering and
    /// sentence segmentation unless those markers appear in one of those fields.
    /// </summary>
    private Dictionary<string, string> RenderPreflightFields(JsonObject request, Current current, LocalEngine? local)
    {
        var firstField = current.Fields[0];
        var needed = new Dictionary<string, FieldTemplate>(StringComparer.Ordinal);
        foreach (var field in current.Fields)
        {
            var template = current.Templates[field];
            if (field == firstField || ClozePattern.IsMatch(template.Value))
                needed[field] = template;
        }

        Dictionary<string, string> rendered;
        if (NeedsLocalEngine(needed))
        {
            if (local is null)
                throw new InvalidOperationException("The offline dictionary engine is unavailable.");
            rendered = Render(request, needed, string.Empty, local, []);
        }
        else
        {
            rendered = AnkiFields.Build(request, needed, _ => string.Empty, string.Empty, null);
        }

        var fields = new Dictionary<string, string>(current.Fields.Count, StringComparer.Ordinal);
        foreach (var field in current.Fields)
            fields[field] = rendered.TryGetValue(field, out var value) ? value : string.Empty;
        return fields;
    }

    private static bool NeedsLocalEngine(IReadOnlyDictionary<string, FieldTemplate> templates)
    {
        foreach (var template in templates.Values)
        {
            foreach (var marker in AnkiFields.MarkerNames(template.Value))
            {
                if (GlossaryMarkers.Contains(marker)
                    || marker.StartsWith("single-glossary-", StringComparison.OrdinalIgnoreCase)
                    || marker is "sentence-furigana" or "sentence-furigana-plain")
                {
                    return true;
                }
            }
        }
        return false;
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
        if (behavior == "overwrite")
            return new Decision("duplicate", noteIds.Length > 0, noteIds.Length == 0 ? "Could not locate existing duplicate note to overwrite." : null, noteIds);
        if (behavior == "new")
        {
            var addable = await CheckAsync(prepared, allowDuplicate: true, ct).ConfigureAwait(false);
            if (!addable.Addable) return new Decision("invalid", false, addable.Error, noteIds);
        }
        return new Decision("duplicate", behavior == "new", null, noteIds);
    }

    private sealed record Check(bool Duplicate, bool Addable, string? Error);

    private static async Task<Check> CheckAsync(Prepared prepared, bool allowDuplicate, CancellationToken ct) =>
        (await CheckManyAsync(prepared.Anki, [prepared.Note], allowDuplicate, ct).ConfigureAwait(false))[0];

    private static async Task<Check[]> CheckManyAsync(AnkiConnect anki, IReadOnlyList<JsonObject> notes, bool allowDuplicate, CancellationToken ct)
    {
        if (notes.Count == 0) return [];
        var payloadNotes = new JsonArray();
        foreach (var source in notes)
        {
            var note = (JsonObject)source.DeepClone();
            ((JsonObject)note["options"]!)["allowDuplicate"] = allowDuplicate;
            payloadNotes.Add(note);
        }
        var reply = await anki.InvokeAsync("canAddNotesWithErrorDetail", new JsonObject { ["notes"] = payloadNotes }, ct).ConfigureAwait(false);
        return ParseChecks(reply, notes.Count);
    }

    private static Check[] ParseChecks(JsonNode? reply, int expectedCount)
    {
        if (reply is not JsonArray arr || arr.Count != expectedCount)
            throw new InvalidOperationException("AnkiConnect returned invalid duplicate check results.");
        var results = new Check[expectedCount];
        for (var i = 0; i < expectedCount; i++)
        {
            if (arr[i] is not JsonObject item)
                throw new InvalidOperationException("AnkiConnect returned invalid duplicate check results.");
            var error = AnkiFields.Str(item["error"]) is { Length: > 0 } e ? e : null;
            var canAdd = item["canAdd"] is JsonValue c && c.GetValueKind() == JsonValueKind.True;
            results[i] = new Check(IsDuplicateError(error), canAdd && error is null, error);
        }
        return results;
    }

    private static bool IsDuplicateError(string? error) =>
        error is not null && Regex.IsMatch(error, "cannot create note because it is a duplicate", RegexOptions.IgnoreCase);

    private static string BuildDuplicateQuery(long modelId, string firstFieldValue, JsonObject config)
    {
        var text = Regex.Replace(firstFieldValue, @"[\\""]", m => "\\" + m.Value);
        var dupe = $"\"dupe:{modelId},{text}\"";
        if (AnkiFields.Str(config["duplicateScope"]) != "deck")
            return dupe;
        var deck = EscapeAnkiSearchValue(AnkiFields.Str(config["deck"]));
        return $"{dupe} \"deck:{deck}\"";
    }

    private static string EscapeAnkiSearchValue(string value) =>
        Regex.Replace(value, @"[\\""*_:]", m => "\\" + m.Value);

    private static long[] ParseNoteIds(JsonNode? reply) =>
        (reply as JsonArray ?? []).Select(n => (long)JsonNumber.Or(n, 0)).Where(id => id > 0).Distinct().ToArray();

    /// <summary>anki-duplicates.js findAnkiDuplicateNotes: Anki's exact first-field duplicates of the note type in scope.</summary>
    private static async Task<long[]> DuplicateNotesAsync(Prepared prepared, CancellationToken ct)
    {
        var config = prepared.Current.Config;
        long? modelId = prepared.Current.ModelId;
        if (modelId is null)
        {
            var model = AnkiFields.Str(config["model"]);
            var models = await prepared.Anki.InvokeAsync("modelNamesAndIds", null, ct).ConfigureAwait(false);
            if (models?[model] is not JsonValue idValue || !JsonNumber.TryGet(idValue, out var parsed) || (long)parsed <= 0)
                return [];
            modelId = (long)parsed;
        }
        var query = BuildDuplicateQuery(modelId.Value, AnkiFields.Str(prepared.Note["fields"]?[prepared.FirstField]), config);
        var found = await prepared.Anki.InvokeAsync("findNotes", new JsonObject { ["query"] = query }, ct).ConfigureAwait(false);
        return ParseNoteIds(found);
    }

    // -------------------------------------------------------------- preflight

    /// <summary>
    /// Batched offline preflight: resolves template configuration once (cached) and checks all
    /// entries in 1-2 AnkiConnect round trips instead of N×4+ sequential calls.
    /// </summary>
    public Task<AnkiDecision[]> PreflightManyAsync(IReadOnlyList<JsonObject> requests, CancellationToken ct = default) =>
        PreflightManyCoreAsync(requests, liveMode: false, ct);

    /// <summary>
    /// Fast direct-AnkiConnect preflight for local live mode: avoids fanning out N individual
    /// <c>hd_anki_preflight</c> RPCs through Chrome (which probes every audio source over HTTP for
    /// every popup entry). Returns <c>null</c> when any template requires live host semantics
    /// (overwrite, cross-model "all" scope, multi-language profiles, first-field audio, or
    /// AnkiConnect unreachable) so the caller falls back cleanly to the live host.
    /// </summary>
    public async Task<AnkiDecision[]?> TryLivePreflightManyAsync(IReadOnlyList<JsonObject> requests, CancellationToken ct = default)
    {
        if (requests.Count == 0)
            return [];
        if (Options["profiles"] is JsonArray { Count: > 0 })
            return null;

        try
        {
            var results = await PreflightManyCoreAsync(requests, liveMode: true, ct).ConfigureAwait(false);
            return results;
        }
        catch (LivePreflightFallbackException)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && !ct.IsCancellationRequested)
        {
            DiagnosticLog.Write("anki", $"Direct local preflight fallback to live host: {ex.Message}");
            return null;
        }
    }

    private sealed class LivePreflightFallbackException : Exception;

    private sealed record BatchEntry(int Index, JsonObject Request, JsonObject Note, string FirstFieldValue, bool AllowDuplicate);

    private async Task<AnkiDecision[]> PreflightManyCoreAsync(IReadOnlyList<JsonObject> requests, bool liveMode, CancellationToken ct)
    {
        if (requests.Count == 0)
            return [];

        var decisions = new AnkiDecision[requests.Count];
        var groups = new Dictionary<string, List<(int Index, JsonObject Request)>>(StringComparer.Ordinal);
        for (var i = 0; i < requests.Count; i++)
        {
            var tid = AnkiFields.Str(requests[i]["templateId"]);
            if (!groups.TryGetValue(tid, out var list))
                groups[tid] = list = [];
            list.Add((i, requests[i]));
        }

        foreach (var (tid, items) in groups)
        {
            var templateId = tid.Length > 0 ? tid : null;
            Current current;
            try
            {
                current = await ConfigurationAsync(templateId, ct, requireOfflineLimits: !liveMode).ConfigureAwait(false);
            }
            catch (Exception ex) when (!liveMode && (ex is InvalidOperationException or HttpRequestException))
            {
                foreach (var (index, _) in items)
                    decisions[index] = new AnkiDecision { State = "error", CanAdd = false, Error = ex.Message };
                continue;
            }

            if (liveMode)
            {
                if (current.Errors.Count > 0 || current.Fields.Count == 0)
                    throw new LivePreflightFallbackException();
                var behavior = AnkiFields.Str(current.Config["duplicateBehavior"]);
                var scope = AnkiFields.Str(current.Config["duplicateScope"]);
                if (behavior == "overwrite" || scope == "all")
                    throw new LivePreflightFallbackException();
                var firstTemplate = current.Templates[current.Fields[0]].Value;
                if (AnkiFields.MarkerNames(firstTemplate).Contains("audio", StringComparer.OrdinalIgnoreCase))
                    throw new LivePreflightFallbackException();
            }

            var local = engine();
            var preparedEntries = new List<BatchEntry>(items.Count);
            foreach (var (index, req) in items)
            {
                try
                {
                    if (!liveMode)
                    {
                        if (AnkiFields.Str(req["configKey"]) != current.ConfigKey)
                            throw new InvalidOperationException(ConfigChanged);
                        if (current.Errors.Count > 0)
                            throw new InvalidOperationException(string.Join("\n", current.Errors));
                        if (local is null)
                            throw new InvalidOperationException("The offline dictionary engine is unavailable.");
                        if (!JsonNumber.TryGet(req["generation"], out var generation) || (long)generation != local.Generation)
                            throw new InvalidOperationException("The dictionaries changed since this lookup. Look up this result again before adding it.");
                    }
                    if (AnkiFields.Str(req["term"]?["expression"]).Length == 0)
                        throw new InvalidOperationException("Mining requires a current dictionary result.");

                    var firstField = current.Fields[0];
                    if (liveMode && NeedsLocalEngine(new Dictionary<string, FieldTemplate> { [firstField] = current.Templates[firstField] }) && local is null)
                        throw new LivePreflightFallbackException();

                    var fields = RenderPreflightFields(req, current, local);
                    var firstValue = fields[firstField];
                    if (firstValue.Trim().Length == 0)
                    {
                        var template = current.Templates[firstField].Value;
                        var expression = AnkiFields.Str(req["term"]?["expression"]);
                        throw new InvalidOperationException($"The first field of note type “{AnkiFields.Str(current.Config["model"])}”, “{firstField}”, is empty for this result"
                            + (template.Length > 0 ? $": its template {template} produced nothing for “{expression}”" : string.Empty) + ". Anki requires it.");
                    }

                    var allowDuplicate = req["allowDuplicate"] is JsonValue a && a.GetValueKind() == JsonValueKind.True;
                    var note = BuildNote(current.Config, fields, allowDuplicate);
                    preparedEntries.Add(new BatchEntry(index, req, note, firstValue, allowDuplicate));
                }
                catch (LivePreflightFallbackException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    decisions[index] = new AnkiDecision { State = "error", CanAdd = false, Error = ex.Message };
                }
            }

            if (preparedEntries.Count == 0)
                continue;

            var client = Anki(current.Config);
            var normalEntries = preparedEntries.Where(e => !e.AllowDuplicate).ToList();
            var forcedEntries = preparedEntries.Where(e => e.AllowDuplicate).ToList();

            foreach (var entry in forcedEntries)
            {
                decisions[entry.Index] = new AnkiDecision
                {
                    State = "addable",
                    CanAdd = true,
                    Screenshot = current.WantsScreenshot,
                };
            }

            if (normalEntries.Count == 0)
                continue;

            var checks = await CheckManyAsync(client, normalEntries.Select(e => e.Note).ToList(), allowDuplicate: false, ct).ConfigureAwait(false);
            var duplicateEntries = new List<BatchEntry>();
            for (var i = 0; i < normalEntries.Count; i++)
            {
                var entry = normalEntries[i];
                var check = checks[i];
                if (check.Duplicate)
                {
                    duplicateEntries.Add(entry);
                }
                else
                {
                    if (!check.Addable)
                        InvalidateCache();
                    decisions[entry.Index] = new AnkiDecision
                    {
                        State = check.Addable ? "addable" : "invalid",
                        CanAdd = check.Addable,
                        Error = check.Error,
                        Screenshot = current.WantsScreenshot,
                    };
                }
            }

            if (duplicateEntries.Count == 0)
                continue;

            // Single multi call for all duplicate entries: deduplicated findNotes + optional allowDuplicate=true check.
            var behaviorMode = AnkiFields.Str(current.Config["duplicateBehavior"]);
            var uniqueFirstValues = duplicateEntries.Select(e => e.FirstFieldValue).Distinct(StringComparer.Ordinal).ToList();
            var multiActions = new List<(string Action, JsonObject? Params)>();
            var hasModelId = current.ModelId is > 0;
            if (hasModelId)
            {
                foreach (var value in uniqueFirstValues)
                {
                    var query = BuildDuplicateQuery(current.ModelId!.Value, value, current.Config);
                    multiActions.Add(("findNotes", new JsonObject { ["query"] = query }));
                }
            }
            var checkAllowedIndex = -1;
            if (behaviorMode == "new")
            {
                checkAllowedIndex = multiActions.Count;
                var allowedNotes = new JsonArray();
                foreach (var entry in duplicateEntries)
                {
                    var clone = (JsonObject)entry.Note.DeepClone();
                    ((JsonObject)clone["options"]!)["allowDuplicate"] = true;
                    allowedNotes.Add(clone);
                }
                multiActions.Add(("canAddNotesWithErrorDetail", new JsonObject { ["notes"] = allowedNotes }));
            }

            var noteIdsByValue = new Dictionary<string, long[]>(StringComparer.Ordinal);
            Check[]? allowedChecks = null;
            if (multiActions.Count > 0)
            {
                var multiReplies = await client.MultiAsync(multiActions, readOnly: true, ct).ConfigureAwait(false);
                if (hasModelId)
                {
                    for (var i = 0; i < uniqueFirstValues.Count; i++)
                    {
                        noteIdsByValue[uniqueFirstValues[i]] = multiReplies[i].Error is null
                            ? ParseNoteIds(multiReplies[i].Result)
                            : [];
                    }
                }
                if (checkAllowedIndex >= 0)
                {
                    if (multiReplies[checkAllowedIndex].Error is { } allowErr)
                        throw new InvalidOperationException($"AnkiConnect canAddNotesWithErrorDetail: {allowErr}");
                    allowedChecks = ParseChecks(multiReplies[checkAllowedIndex].Result, duplicateEntries.Count);
                }
            }

            for (var i = 0; i < duplicateEntries.Count; i++)
            {
                var entry = duplicateEntries[i];
                var noteIds = noteIdsByValue.TryGetValue(entry.FirstFieldValue, out var ids) ? ids : [];
                if (allowedChecks is not null && !allowedChecks[i].Addable)
                {
                    InvalidateCache();
                    decisions[entry.Index] = new AnkiDecision
                    {
                        State = "invalid",
                        CanAdd = false,
                        Error = allowedChecks[i].Error,
                        NoteIds = noteIds,
                        Screenshot = current.WantsScreenshot,
                    };
                }
                else
                {
                    var canAdd = behaviorMode == "new" || (behaviorMode == "overwrite" && noteIds.Length > 0);
                    decisions[entry.Index] = new AnkiDecision
                    {
                        State = "duplicate",
                        CanAdd = canAdd,
                        NoteIds = noteIds,
                        Screenshot = current.WantsScreenshot,
                    };
                }
            }
        }

        return decisions;
    }

    private static AnkiDecision ToReply(Decision decision, Prepared prepared, string[]? warnings = null) => new()
    {
        State = decision.State,
        CanAdd = decision.CanAdd,
        Error = decision.Error,
        NoteIds = decision.NoteIds,
        Screenshot = prepared.Current.WantsScreenshot,
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
        var local = RequireEngine();
        foreach (var image in prepared.Images.Where(i => noteFields.Any(f => AnkiFields.Str(f.Value).Contains(i.Filename, StringComparison.Ordinal))))
        {
            var bytes = local.Media(image.Dictionary, image.Path)
                ?? throw new InvalidOperationException("The dictionary image is no longer available or is malformed.");
            files.Add(new MediaFile(image.Filename, Convert.ToBase64String(bytes), "picture", "dictionary image", Required: true));
        }

        // Overwrite existing duplicate note via updateNoteFields instead of addNote.
        var behavior = AnkiFields.Str(prepared.Current.Config["duplicateBehavior"]);
        if (decision.State == "duplicate" && behavior == "overwrite" && decision.NoteIds.Length > 0)
        {
            var targetId = decision.NoteIds[0];
            var existingFields = await prepared.Anki.FieldsAsync(targetId, ct).ConfigureAwait(false);
            var existingMap = existingFields.ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);
            var merged = MergeOverwriteFields(existingMap, noteFields, prepared.Current.Templates);
            var neededFiles = files.Where(f => merged.Values.Any(v => v.Contains(f.Filename, StringComparison.Ordinal))).ToList();
            if (neededFiles.Count > 0)
            {
                var replies = await prepared.Anki.MultiAsync(
                    neededFiles.Select(u => ("storeMediaFile", (JsonObject?)new JsonObject
                    {
                        ["filename"] = u.Filename,
                        ["data"] = u.Data,
                        ["deleteExisting"] = false,
                    })).ToList(),
                    readOnly: false,
                    ct).ConfigureAwait(false);
                for (var i = 0; i < neededFiles.Count; i++)
                {
                    if (!string.IsNullOrEmpty(replies[i].Error))
                        warnings.Add($"{neededFiles[i].Kind}: {replies[i].Error}");
                }
            }
            if (merged.Count > 0)
                await prepared.Anki.UpdateFieldsAsync(targetId, merged, ct).ConfigureAwait(false);
            foreach (var warning in warnings) DiagnosticLog.Write("anki", $"Offline overwrite warning: {warning}");
            return new AnkiDecision { State = "updated", CanAdd = true, NoteIds = [targetId], Warnings = warnings.ToArray() };
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
        var replies = await anki.MultiAsync(
            uploads.Select(u => ("storeMediaFile", (JsonObject?)new JsonObject
            {
                ["filename"] = u.Filename,
                ["data"] = u.Data,
                ["deleteExisting"] = false,
            })).ToList(),
            readOnly: false,
            ct).ConfigureAwait(false);
        for (var i = 0; i < uploads.Count; i++)
        {
            var (result, error) = replies[i];
            var stored = AnkiFields.Str(result);
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

    /// <summary>Merges rendered fields into an existing note according to each field template's <c>overwriteMode</c>.</summary>
    internal static Dictionary<string, string> MergeOverwriteFields(
        IReadOnlyDictionary<string, string> existing,
        JsonObject rendered,
        IReadOnlyDictionary<string, FieldTemplate> templates)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, node) in rendered)
        {
            var incoming = AnkiFields.Str(node);
            if (incoming.Trim().Length == 0)
                continue;
            var current = existing.TryGetValue(name, out var val) ? val : string.Empty;
            var mode = templates.TryGetValue(name, out var tpl) ? tpl.OverwriteMode : "coalesce";
            var next = mode.ToLowerInvariant() switch
            {
                "overwrite" => incoming,
                "append" => current.Trim().Length == 0 ? incoming : $"{current}<br>{incoming}",
                "prepend" => current.Trim().Length == 0 ? incoming : $"{incoming}<br>{current}",
                _ => current.Trim().Length == 0 ? incoming : current,
            };
            if (!string.Equals(next, current, StringComparison.Ordinal))
                updates[name] = next;
        }
        return updates;
    }

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
