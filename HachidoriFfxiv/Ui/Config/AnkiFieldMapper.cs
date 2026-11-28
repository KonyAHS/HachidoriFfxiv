using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using HachidoriFfxiv.Anki;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Media;

namespace HachidoriFfxiv.Ui;

/// <summary>
/// Live AnkiConnect schema inspector, unified media & standalone template field mapper,
/// note-type preset applier (Lapis, JPMN, Senren, Kaishi), and Dry-Run Card Previewer.
/// </summary>
internal sealed class AnkiFieldMapper
{
    private static readonly string[] CommonMarkers =
    [
        "",
        "{expression}",
        "{reading}",
        "{furigana-plain}",
        "{furigana}",
        "{audio}",
        "{glossary-first}",
        "{glossary}",
        "{glossary-brief}",
        "{sentence}",
        "{sentence-furigana-plain}",
        "{screenshot}",
        "{pitch-position}",
        "{pitch-accent-categories}",
        "{pitch-accent-graphs}",
        "{pitch-accent-graphs-jj}",
        "{frequencies}",
        "{frequency-harmonic-rank}",
        "{document-title}",
        "{popup-selection-text}",
    ];

    private readonly Configuration config;
    private readonly HachidoriService service;
    private readonly MediaMiner media;
    private readonly Action onChanged;

    private bool loading;
    private bool loadedOnce;
    private string? error;
    private List<string> decks = [];
    private List<string> models = [];
    private Dictionary<string, List<string>> modelFields = new(StringComparer.Ordinal);
    private string? statusMessage;
    private bool showPreview;
    private List<(string Field, string Value, bool Warning)> previewRows = [];

    public AnkiFieldMapper(Configuration config, HachidoriService service, MediaMiner media, Action onChanged)
    {
        this.config = config;
        this.service = service;
        this.media = media;
        this.onChanged = onChanged;
    }

    public bool Loading => loading;
    public bool Connected => loadedOnce && error is null;
    public string? Error => error;

    public void EnsureLoaded()
    {
        if (!loadedOnce && !loading)
            Refresh();
    }

    public void Refresh()
    {
        if (loading)
            return;
        loading = true;
        error = null;
        _ = Task.Run(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var replies = await media.Anki.MultiAsync(
            [
                ("deckNames", null),
                ("modelNames", null),
            ], readOnly: true).ConfigureAwait(false);

            if (replies[0].Error is { } dErr) throw new InvalidOperationException(dErr);
            if (replies[1].Error is { } mErr) throw new InvalidOperationException(mErr);

            var nextDecks = (replies[0].Result as JsonArray ?? []).Select(n => n?.ToString() ?? string.Empty).Where(s => s.Length > 0).ToList();
            var nextModels = (replies[1].Result as JsonArray ?? []).Select(n => n?.ToString() ?? string.Empty).Where(s => s.Length > 0).ToList();

            var nextFields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (nextModels.Count > 0)
            {
                var fieldCalls = nextModels
                    .Select(m => ("modelFieldNames", (JsonObject?)new JsonObject { ["modelName"] = m }))
                    .ToList();
                var fieldReplies = await media.Anki.MultiAsync(fieldCalls, readOnly: true).ConfigureAwait(false);
                for (var i = 0; i < nextModels.Count && i < fieldReplies.Count; i++)
                {
                    if (fieldReplies[i].Error is null && fieldReplies[i].Result is JsonArray arr)
                        nextFields[nextModels[i]] = arr.Select(n => n?.ToString() ?? string.Empty).Where(s => s.Length > 0).ToList();
                }
            }

            decks = nextDecks;
            models = nextModels;
            modelFields = nextFields;
            loadedOnce = true;
            error = null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            loadedOnce = true;
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>Draws the unified Anki Card Mapping card inside the Mining tab.</summary>
    public void Draw(bool advanced, DictionarySource source)
    {
        EnsureLoaded();
        var scale = ImGuiHelpers.GlobalScale;

        // Top toolbar: Refresh + Dry-Run Preview
        using (ImRaii.Disabled(loading))
        {
            if (ImGui.SmallButton(loading ? "Refreshing Anki…" : "Refresh Anki Fields"))
                Refresh();
        }
        ImGui.SameLine(0, 8 * scale);
        if (ConfigStyle.AccentButton("Dry-Run Preview", ConfigStyle.Primary))
        {
            BuildPreview(source);
            showPreview = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Preview exactly what a mined FFXIV card will write into each Anki field without adding a real note.");

        if (error is not null)
        {
            ImGui.TextColored(ConfigStyle.Emphasis, $"AnkiConnect unavailable: {error}");
            ImGui.TextColored(ConfigStyle.Muted, "Open Anki with AnkiConnect (2055492159) to load live field dropdowns.");
        }
        else if (loadedOnce)
        {
            ImGui.SameLine(0, 10 * scale);
            ImGui.TextColored(ConfigStyle.Good, $"Anki ready ({decks.Count} decks, {models.Count} note types)");
        }

        // Source-aware template selector: hide Hachidori template options in Built-in mode and vice versa.
        var hostTemplates = GetHostTemplates();
        bool useStandalone;
        if (source == DictionarySource.BuiltIn)
        {
            useStandalone = true;
            if (IsStandaloneEmpty())
                SeedStandaloneFromHostTemplate(overwrite: false);
            var changed = false;
            if (!config.StandaloneAnki.PreferStandalone)
            {
                config.StandaloneAnki.PreferStandalone = true;
                changed = true;
            }
            if (!IsStandaloneEmpty() && config.AnkiTemplateId != Offline.StandaloneDictionaries.StandaloneTemplateId)
            {
                config.AnkiTemplateId = Offline.StandaloneDictionaries.StandaloneTemplateId;
                changed = true;
            }
            if (changed)
                onChanged();
        }
        else if (hostTemplates.Count > 0)
        {
            var labels = new List<string> { "Hachidori default template" };
            var ids = new List<string> { string.Empty };
            foreach (var (id, name, model) in hostTemplates)
            {
                if (id == Offline.StandaloneDictionaries.StandaloneTemplateId) continue;
                labels.Add(string.IsNullOrWhiteSpace(model) ? name : $"{name} ({model})");
                ids.Add(id);
            }
            if (advanced)
            {
                labels.Add("Built-in template override");
                ids.Add(Offline.StandaloneDictionaries.StandaloneTemplateId);
            }

            var currentIdx = config.StandaloneAnki.PreferStandalone && advanced
                ? ids.Count - 1
                : Math.Max(0, ids.IndexOf(config.AnkiTemplateId));
            ImGui.SetNextItemWidth(280 * scale);
            if (ImGui.Combo("Active Template", ref currentIdx, labels.ToArray(), labels.Count))
            {
                if (ids[currentIdx] == Offline.StandaloneDictionaries.StandaloneTemplateId)
                {
                    config.StandaloneAnki.PreferStandalone = true;
                    config.AnkiTemplateId = Offline.StandaloneDictionaries.StandaloneTemplateId;
                }
                else
                {
                    config.StandaloneAnki.PreferStandalone = false;
                    config.AnkiTemplateId = ids[currentIdx];
                }
                onChanged();
            }
            useStandalone = config.StandaloneAnki.PreferStandalone && advanced;
        }
        else
        {
            useStandalone = true;
        }

        var activeModel = useStandalone ? config.StandaloneAnki.Model : ResolveActiveHostModel(hostTemplates);
        var activeFields = !string.IsNullOrWhiteSpace(activeModel) && modelFields.TryGetValue(activeModel, out var fList)
            ? fList
            : [];

        if (useStandalone)
            DrawStandaloneTemplateEditor(scale, activeFields);

        // Media & Translation target field dropdowns
        ConfigStyle.SubHeading("Media & Translation Target Fields", ConfigStyle.Secondary);
        var activeTuples = activeFields.Select(f => (f, string.Empty)).ToList();

        var autoAudio = activeTuples.Count > 0 ? MediaFields.SentenceAudioField(activeTuples, string.Empty) : null;
        DrawFieldDropdown("Sentence Audio", config.SentenceAudioField, v => { config.SentenceAudioField = v; onChanged(); },
            activeFields, autoAudio is not null ? $"Auto ({autoAudio})" : "Auto-detect", scale);

        var autoGif = activeTuples.Count > 0 ? MediaFields.GifField(activeTuples, string.Empty, null, config.GifReplacesScreenshot) : null;
        DrawFieldDropdown("Animation / GIF", config.GifField, v => { config.GifField = v; onChanged(); },
            activeFields, autoGif is not null ? $"Auto ({autoGif})" : "Auto (replaces Picture)", scale);

        DrawFieldDropdown("Translation", config.TranslationField, v => { config.TranslationField = v; onChanged(); },
            activeFields, null, scale);

        if (statusMessage is not null)
            ImGui.TextColored(ConfigStyle.Good, statusMessage);

        if (showPreview)
            DrawPreviewTable(scale);
    }

    public bool IsStandaloneEmpty() =>
        string.IsNullOrWhiteSpace(config.StandaloneAnki.Deck)
        || string.IsNullOrWhiteSpace(config.StandaloneAnki.Model)
        || config.StandaloneAnki.Fields.Count == 0;

    /// <summary>
    /// Seeds <see cref="Configuration.StandaloneAnki"/> from the first synced Hachidori template
    /// when available so switching to Built-in mode keeps the user's existing deck/note type/fields.
    /// </summary>
    public bool SeedStandaloneFromHostTemplate(bool overwrite)
    {
        if (!overwrite && !IsStandaloneEmpty())
            return false;
        var tpl = FindFirstHostTemplateJson();
        if (tpl is null)
            return false;
        return StandaloneAnkiTemplates.SeedFromTemplateJson(config.StandaloneAnki, tpl);
    }

    private JsonObject? FindFirstHostTemplateJson()
    {
        var templates = service.Options["anki"]?["templates"] as JsonArray;
        if (templates is null)
            return null;
        return templates.OfType<JsonObject>()
            .FirstOrDefault(t => AnkiFields.Str(t["id"]) != Offline.StandaloneDictionaries.StandaloneTemplateId);
    }

    private void DrawStandaloneTemplateEditor(float scale, List<string> activeFields)
    {
        var sa = config.StandaloneAnki;
        ConfigStyle.SubHeading("Note Type & Presets", ConfigStyle.Primary);

        // Deck dropdown
        if (decks.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(sa.Deck) && !SeedStandaloneFromHostTemplate(overwrite: false))
            {
                sa.Deck = decks[0];
                config.Save();
            }
            var deckIdx = Math.Max(0, decks.IndexOf(sa.Deck));
            var deckChoices = decks.ToArray();
            ImGui.SetNextItemWidth(260 * scale);
            if (ImGui.Combo("Deck", ref deckIdx, deckChoices, deckChoices.Length))
            {
                sa.Deck = deckChoices[deckIdx];
                if (!IsStandaloneEmpty())
                    config.AnkiTemplateId = Offline.StandaloneDictionaries.StandaloneTemplateId;
                onChanged();
            }
        }
        else
        {
            var deck = sa.Deck;
            ImGui.SetNextItemWidth(260 * scale);
            if (ImGui.InputTextWithHint("Deck", "e.g. Japanese Mining", ref deck, 128))
                sa.Deck = deck.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit())
                onChanged();
        }

        // Model dropdown
        if (models.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(sa.Model))
            {
                sa.Model = models.FirstOrDefault(m =>
                    m.Contains("Lapis", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("Mining", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("Senren", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("Kaishi", StringComparison.OrdinalIgnoreCase)) ?? models[0];
                if (modelFields.TryGetValue(sa.Model, out var initialFields) && sa.Fields.Count == 0)
                    ApplyMappingResult(StandaloneAnkiTemplates.AutoMapFields(sa, initialFields));
                config.Save();
            }

            var modelIdx = Math.Max(0, models.IndexOf(sa.Model));
            var modelChoices = models.ToArray();
            ImGui.SetNextItemWidth(260 * scale);
            if (ImGui.Combo("Note Type", ref modelIdx, modelChoices, modelChoices.Length))
            {
                sa.Model = modelChoices[modelIdx];
                if (modelFields.TryGetValue(sa.Model, out var f))
                    ApplyMappingResult(StandaloneAnkiTemplates.AutoMapFields(sa, f));
                onChanged();
            }
        }
        else
        {
            var model = sa.Model;
            ImGui.SetNextItemWidth(260 * scale);
            if (ImGui.InputTextWithHint("Note Type", "e.g. Lapis", ref model, 128))
                sa.Model = model.Trim();
            if (ImGui.IsItemDeactivatedAfterEdit())
                onChanged();
        }

        // Duplicate behavior & tags
        var dupModes = new[] { "prevent", "overwrite", "new" };
        var dupLabels = new[] { "Skip duplicates", "Update existing note", "Allow duplicates" };
        var dupIdx = Math.Max(0, Array.IndexOf(dupModes, sa.DuplicateBehavior));
        ImGui.SetNextItemWidth(180 * scale);
        if (ImGui.Combo("Duplicates", ref dupIdx, dupLabels, dupLabels.Length))
        {
            sa.DuplicateBehavior = dupModes[dupIdx];
            onChanged();
        }
        ImGui.SameLine(0, 12 * scale);
        var tags = sa.Tags;
        ImGui.SetNextItemWidth(160 * scale);
        if (ImGui.InputTextWithHint("Tags", "hachidori ffxiv", ref tags, 128))
            sa.Tags = tags;
        if (ImGui.IsItemDeactivatedAfterEdit())
            onChanged();

        // One-click note-type presets
        ImGui.Spacing();
        ImGui.TextColored(ConfigStyle.Muted, "Quick presets:");
        ImGui.SameLine(0, 6 * scale);
        if (FindFirstHostTemplateJson() is { } hostTpl)
        {
            if (ConfigStyle.AccentButton("Copy from Hachidori", ConfigStyle.Secondary))
            {
                if (StandaloneAnkiTemplates.SeedFromTemplateJson(sa, hostTpl))
                {
                    config.AnkiTemplateId = Offline.StandaloneDictionaries.StandaloneTemplateId;
                    statusMessage = $"Copied deck “{sa.Deck}” / note type “{sa.Model}” ({sa.Fields.Count} fields) from Hachidori.";
                    onChanged();
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Copies your existing Hachidori deck, note type, and field mapping into Built-in mode.");
            ImGui.SameLine(0, 4 * scale);
        }
        if (activeFields.Count > 0)
        {
            if (ConfigStyle.AccentButton("Smart Auto-Map", ConfigStyle.Primary))
            {
                var res = StandaloneAnkiTemplates.AutoMapFields(sa, activeFields);
                ApplyMappingResult(res);
                statusMessage = $"Auto-mapped {res.MappedCount} fields for “{sa.Model}”.";
                onChanged();
            }
            ImGui.SameLine(0, 4 * scale);
        }
        foreach (var preset in StandaloneAnkiTemplates.Presets)
        {
            if (ImGui.SmallButton(preset.Name))
            {
                var res = StandaloneAnkiTemplates.ApplyPreset(sa, preset, activeFields.Count > 0 ? activeFields : null);
                ApplyMappingResult(res);
                statusMessage = $"Applied {preset.Name} preset ({res.MappedCount} fields mapped).";
                onChanged();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(preset.Description);
            ImGui.SameLine(0, 4 * scale);
        }
        ImGui.NewLine();

        // Compact field mapping table when note type fields are known
        var fieldList = activeFields.Count > 0 ? activeFields : sa.Fields.Keys.ToList();
        if (fieldList.Count > 0 && ImGui.TreeNode($"Field Mapping ({sa.Fields.Count(kv => !string.IsNullOrWhiteSpace(kv.Value))}/{fieldList.Count} mapped)##sa-fields"))
        {
            // Explicit block ensures ImGui.EndTable() runs before ImGui.TreePop() (avoids PushID/PopID mismatch).
            using (var tbl = ImRaii.Table("##sa-fields-tbl", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                if (tbl.Success)
                {
                    ImGui.TableSetupColumn("Anki Field", ImGuiTableColumnFlags.WidthFixed, 170 * scale);
                    ImGui.TableSetupColumn("Marker / Value", ImGuiTableColumnFlags.WidthStretch);
                    foreach (var field in fieldList)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(field);
                        ImGui.TableNextColumn();

                        sa.Fields.TryGetValue(field, out var current);
                        current ??= string.Empty;

                        using var id = ImRaii.PushId(field);
                        ImGui.SetNextItemWidth(-46 * scale);
                        if (ImGui.InputText("##val", ref current, 256))
                            sa.Fields[field] = current;
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            onChanged();
                        ImGui.SameLine(0, 4 * scale);
                        if (ImGui.SmallButton("Pick"))
                            ImGui.OpenPopup("marker-pick");
                        if (ImGui.BeginPopup("marker-pick"))
                        {
                            foreach (var m in CommonMarkers)
                            {
                                var label = m.Length == 0 ? "(empty)" : m;
                                if (ImGui.Selectable(label, current == m))
                                {
                                    sa.Fields[field] = m;
                                    onChanged();
                                }
                            }
                            ImGui.EndPopup();
                        }
                    }
                }
            }
            ImGui.TreePop();
        }
    }

    private void ApplyMappingResult(StandaloneAnkiTemplates.MappingResult res)
    {
        if (res.SentenceAudioField is not null)
            config.SentenceAudioField = res.SentenceAudioField;
        if (res.GifField is not null)
            config.GifField = res.GifField;
        if (!string.IsNullOrWhiteSpace(res.TranslationField))
            config.TranslationField = res.TranslationField;
    }

    private void DrawFieldDropdown(string label, string current, Action<string> set, List<string> fields, string? autoLabel, float scale)
    {
        if (fields.Count == 0)
        {
            var val = current;
            ImGui.SetNextItemWidth(220 * scale);
            if (ImGui.InputTextWithHint(label, autoLabel ?? "Field name", ref val, 128))
                set(val.Trim());
            return;
        }

        var choices = new List<string>();
        var labels = new List<string>();
        if (autoLabel is not null)
        {
            choices.Add(string.Empty);
            labels.Add(autoLabel);
        }
        foreach (var f in fields)
        {
            choices.Add(f);
            labels.Add(f);
        }
        if (!string.IsNullOrWhiteSpace(current) && !choices.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            choices.Add(current);
            labels.Add($"{current} (not in note type!)");
        }

        var idx = choices.FindIndex(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) idx = 0;

        ImGui.SetNextItemWidth(240 * scale);
        if (ImGui.Combo(label, ref idx, labels.ToArray(), labels.Count))
            set(choices[idx]);
    }

    private void BuildPreview(DictionarySource source)
    {
        previewRows.Clear();
        var sample = StandaloneAnkiTemplates.SamplePreviewRequest(config.DocumentTitle);
        var hostTemplates = GetHostTemplates();
        var useStandalone = source == DictionarySource.BuiltIn || config.StandaloneAnki.PreferStandalone || (!service.Link.Connected && hostTemplates.Count == 0);

        Dictionary<string, FieldTemplate> templates;
        List<string> fields;
        if (useStandalone)
        {
            fields = modelFields.TryGetValue(config.StandaloneAnki.Model, out var f) && f.Count > 0
                ? f
                : config.StandaloneAnki.Fields.Keys.ToList();
            templates = fields.ToDictionary(
                name => name,
                name => new FieldTemplate(config.StandaloneAnki.Fields.TryGetValue(name, out var v) ? v : string.Empty),
                StringComparer.Ordinal);
        }
        else
        {
            var snap = service.Snapshot;
            var all = (snap["options"]?["anki"]?["templates"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var tpl = string.IsNullOrWhiteSpace(config.AnkiTemplateId)
                ? all.FirstOrDefault()
                : all.FirstOrDefault(t => AnkiFields.Str(t["id"]) == config.AnkiTemplateId);
            if (tpl is null)
            {
                previewRows.Add(("Template", "No active Anki template configured yet.", true));
                return;
            }
            var model = AnkiFields.Str(tpl["model"]);
            fields = modelFields.TryGetValue(model, out var f) && f.Count > 0
                ? f
                : (tpl["fieldTemplates"] as JsonObject)?.Select(p => p.Key).ToList() ?? [];
            var (resolved, errors) = AnkiFields.Resolve(tpl, fields);
            foreach (var err in errors)
                previewRows.Add(("Config Warning", err, true));
            templates = resolved;
        }

        if (fields.Count == 0)
        {
            previewRows.Add(("Note Type", "Select a Note Type above or connect AnkiConnect to preview fields.", true));
            return;
        }

        var rendered = AnkiFields.Build(sample, templates, opts => new AnkiGlossary(sample, null, []).Render(opts), "[sound:word_audio.mp3]");
        var fieldTuples = fields.Select(f => (f, rendered.GetValueOrDefault(f, string.Empty))).ToList();

        var audioTarget = config.RecordVoiceLines || config.TtsMode != TtsMode.Off
            ? MediaFields.SentenceAudioField(fieldTuples, config.SentenceAudioField)
            : null;
        var gifTarget = config.CaptureGif
            ? MediaFields.GifField(fieldTuples, config.GifField, "hachidori-preview.jpg", config.GifReplacesScreenshot)
            : null;
        var tlTarget = config.TranslationEnabled
            ? fields.FirstOrDefault(f => string.Equals(f, config.TranslationField.Trim(), StringComparison.OrdinalIgnoreCase))
            : null;

        foreach (var field in fields)
        {
            var val = rendered.GetValueOrDefault(field, string.Empty);
            var extras = new List<string>();
            if (field == audioTarget)
                extras.Add(config.RecordVoiceLines ? "[+ Game Voice / TTS]" : "[+ TTS Audio]");
            if (field == gifTarget)
                extras.Add($"[+ Animated {config.AnimationFormat.ToString().ToUpperInvariant()}]");
            if (field == tlTarget)
                extras.Add($"[+ {config.TranslationProvider} Translation]");

            var display = StripHtmlPreview(val);
            if (extras.Count > 0)
                display = string.IsNullOrEmpty(display) ? string.Join(" ", extras) : $"{display}  {string.Join(" ", extras)}";

            if (!string.IsNullOrWhiteSpace(display))
                previewRows.Add((field, display, false));
        }

        if ((config.RecordVoiceLines || config.TtsMode != TtsMode.Off) && audioTarget is null)
            previewRows.Add(("Sentence Audio", "No matching field found — pick one in the dropdown above.", true));
        if (config.CaptureGif && gifTarget is null)
            previewRows.Add(("Animation", "No matching GIF/Picture field found — pick one above.", true));
        if (config.TranslationEnabled && tlTarget is null)
            previewRows.Add(("Translation", $"Field “{config.TranslationField}” is not in this note type.", true));
    }

    private void DrawPreviewTable(float scale)
    {
        ImGui.Spacing();
        ImGui.TextColored(ConfigStyle.Primary, "Dry-Run Preview (sample word: 冒険者)");
        ImGui.SameLine();
        if (ImGui.SmallButton("Close preview"))
            showPreview = false;

        using var tbl = ImRaii.Table("##anki-preview-tbl", 2, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!tbl.Success)
            return;
        ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 150 * scale);
        ImGui.TableSetupColumn("Will Receive", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        foreach (var (field, value, warn) in previewRows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(warn ? ConfigStyle.Emphasis : ConfigStyle.Muted, field);
            ImGui.TableNextColumn();
            if (warn)
                ImGui.TextColored(ConfigStyle.Emphasis, value);
            else
                ImGui.TextWrapped(value);
        }
    }

    private List<(string Id, string Name, string Model)> GetHostTemplates()
    {
        var list = new List<(string, string, string)>();
        var templates = service.Options["anki"]?["templates"] as JsonArray;
        if (templates is null)
            return list;
        foreach (var t in templates.OfType<JsonObject>())
        {
            var id = AnkiFields.Str(t["id"]);
            if (id.Length == 0) continue;
            var name = AnkiFields.Str(t["name"]);
            var model = AnkiFields.Str(t["model"]);
            list.Add((id, name.Length > 0 ? name : id, model));
        }
        return list;
    }

    private string? ResolveActiveHostModel(List<(string Id, string Name, string Model)> hostTemplates)
    {
        if (hostTemplates.Count == 0)
            return null;
        if (!string.IsNullOrWhiteSpace(config.AnkiTemplateId))
        {
            var found = hostTemplates.FirstOrDefault(t => t.Id == config.AnkiTemplateId);
            if (!string.IsNullOrEmpty(found.Model))
                return found.Model;
        }
        return hostTemplates[0].Model;
    }

    private static string StripHtmlPreview(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;
        if (html.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            return "[SVG Pitch Accent Graph]";
        var clean = Regex.Replace(html, "<style[\\s\\S]*?</style>", string.Empty, RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, "<img[^>]*src=\"([^\"]+)\"[^>]*>", "[Image: $1]", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, "<[^>]+>", " ");
        clean = Regex.Replace(clean, "\\s+", " ").Trim();
        return clean.Length > 140 ? clean[..140] + "…" : clean;
    }
}
