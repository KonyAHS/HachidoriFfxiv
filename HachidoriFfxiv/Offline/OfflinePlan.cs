using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace HachidoriFfxiv.Offline;

/// <summary>Which downloaded packages the local engine loads, in what order.</summary>
public static class OfflinePlan
{
    /// <summary>
    /// The packages to load, in the host's order: Japanese's profile list once
    /// the dictionary state has profiles (reader-options.js projectDictionaryState),
    /// else the state's own list. Downloaded packages the state does not list follow, enabled.
    /// </summary>
    public static List<LocalDictionary> For(OfflineStore store, JsonObject? snapshot)
    {
        var state = snapshot?["dictionaryState"] as JsonObject;
        var rows = OfflineSync.SnapshotRows(snapshot);
        var order = new List<(string Id, bool Enabled)>();
        if (state?["profiles"] is JsonObject profiles)
        {
            if (profiles["ja"]?["dictionaries"] is JsonArray entries)
                order.AddRange(entries.OfType<JsonObject>().Select(e => (Str(e["id"]), IsEnabled(e["enabled"]))));
        }
        else if (state?["dictionaries"] is JsonArray list)
        {
            order.AddRange(list.OfType<JsonObject>().Select(r => (Str(r["id"]), IsEnabled(r["enabled"]))));
        }

        var downloaded = store.Manifest.Dictionaries.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var plan = new List<LocalDictionary>();
        foreach (var (id, enabled) in order)
        {
            if (!downloaded.Remove(id, out var dictionary)) continue;
            plan.Add(ToLocal(store, dictionary, rows.GetValueOrDefault(id), enabled));
        }
        // Without profiles, packages missing from the state are not in Hachidori's
        // engine either; with them, a package outside Japanese's list is not used.
        // Standalone locally-imported Yomitan dictionaries ("local") always load in their manifest order.
        if (state is null)
        {
            foreach (var d in store.Manifest.Dictionaries)
            {
                if (downloaded.Remove(d.Id, out var dict))
                    plan.Add(ToLocal(store, dict, null, dict.Enabled));
            }
        }
        else
        {
            foreach (var d in store.Manifest.Dictionaries)
            {
                if (string.Equals(d.Source, "local", StringComparison.OrdinalIgnoreCase) && downloaded.Remove(d.Id, out var dict))
                    plan.Add(ToLocal(store, dict, null, dict.Enabled));
            }
        }
        return plan;
    }

    private static LocalDictionary ToLocal(OfflineStore store, OfflineDictionary d, JsonObject? row, bool enabled) => new(
        d.Id, d.Title, store.PathOf(d), enabled,
        d.TermCount > 0 ? d.TermCount : Count(row?["termCount"]),
        d.FrequencyCount > 0 ? d.FrequencyCount : Count(row?["frequencyCount"]),
        d.PitchCount > 0 ? d.PitchCount : Count(row?["pitchCount"]),
        d.KanjiCount > 0 ? d.KanjiCount : Count(row?["kanjiCount"]));

    /// <summary>Whether a Hachidori version ("0.6.0") is newer than another; false when either does not parse.</summary>
    public static bool IsNewer(string candidate, string baseline) =>
        TryVersion(candidate, out var a) && TryVersion(baseline, out var b) && a > b;

    private static bool TryVersion(string text, out Version version)
    {
        var core = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(core.Contains('.') ? core : core + ".0", out version!);
    }

    private static bool IsEnabled(JsonNode? node) => node is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;

    private static long Count(JsonNode? node) => JsonNumber.Count(node);

    private static string Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
}
