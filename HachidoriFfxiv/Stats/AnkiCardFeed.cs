using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HachidoriFfxiv.Media;

namespace HachidoriFfxiv.Stats;

public sealed record AnkiRecentNote(
    long NoteId,
    string Expression,
    string Reading,
    string Sentence,
    string ModelName,
    string[] Tags,
    bool HasAudio,
    bool HasImage,
    DateTime CreatedAtUtc);

/// <summary>
/// Fetches recently added notes directly from Anki via AnkiConnect so the Stats page can
/// display the user's latest Anki cards even across restarts or older sessions.
/// </summary>
public sealed class AnkiCardFeed
{
    private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex SoundTag = new(@"\[sound:[^\]]+\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ImgTag = new(@"<img\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WordFieldPattern = new(@"^(word|expression|vocab|term|front|kanji|target)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReadingFieldPattern = new(@"^(reading|kana|furigana|hiragana)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SentenceFieldPattern = new(@"sentence|context|example", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AnkiConnect anki;
    private readonly object sync = new();
    private Task? activeRefresh;

    public AnkiCardFeed(AnkiConnect anki) => this.anki = anki;

    public IReadOnlyList<AnkiRecentNote> Notes { get; private set; } = [];
    public int AddedLast7Days { get; private set; }
    public int AddedLast30Days { get; private set; }
    public DateTime? LastRefreshedUtc { get; private set; }
    public string? Error { get; private set; }
    public bool Loading => activeRefresh is { IsCompleted: false };

    public void RefreshIfStale(TimeSpan maxAge)
    {
        if (Loading)
            return;
        if (LastRefreshedUtc is { } last && DateTime.UtcNow - last < maxAge)
            return;
        Refresh();
    }

    public void Refresh()
    {
        lock (sync)
        {
            if (activeRefresh is { IsCompleted: false })
                return;
            activeRefresh = RefreshAsync();
        }
    }

    public void BrowseNote(long noteId)
    {
        if (noteId <= 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await anki.InvokeAsync("guiBrowse", new JsonObject { ["query"] = $"nid:{noteId}" }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
        });
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ids30Node = await anki.InvokeAsync("findNotes", new JsonObject { ["query"] = "added:30" }, cts.Token).ConfigureAwait(false);
            var ids30 = ParseIds(ids30Node);
            var ids7Cutoff = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();
            AddedLast30Days = ids30.Count;
            AddedLast7Days = ids30.Count(id => id >= ids7Cutoff);

            var latestIds = ids30.OrderByDescending(id => id).Take(25).ToArray();
            if (latestIds.Length == 0)
            {
                Notes = [];
                Error = null;
                LastRefreshedUtc = DateTime.UtcNow;
                return;
            }

            var infoNode = await anki.InvokeAsync("notesInfo", new JsonObject
            {
                ["notes"] = new JsonArray(latestIds.Select(id => (JsonNode?)id).ToArray()),
            }, cts.Token).ConfigureAwait(false);

            var parsed = new List<AnkiRecentNote>();
            if (infoNode is JsonArray arr)
            {
                foreach (var item in arr.OfType<JsonObject>())
                {
                    if (ParseNote(item) is { } note)
                        parsed.Add(note);
                }
            }

            Notes = parsed.OrderByDescending(n => n.NoteId).ToList();
            Error = null;
            LastRefreshedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private static List<long> ParseIds(JsonNode? node)
    {
        var ids = new List<long>();
        if (node is not JsonArray arr)
            return ids;
        foreach (var item in arr)
        {
            if (item is JsonValue v && v.TryGetValue<long>(out var id) && id > 0)
                ids.Add(id);
        }
        return ids;
    }

    internal static AnkiRecentNote? ParseNote(JsonObject item)
    {
        if (item["noteId"] is not JsonValue idVal || !idVal.TryGetValue<long>(out var noteId) || noteId <= 0)
            return null;

        var model = item["modelName"]?.ToString() ?? string.Empty;
        var tags = (item["tags"] as JsonArray)?.Select(t => t?.ToString() ?? string.Empty).Where(t => t.Length > 0).ToArray() ?? [];
        var fieldsObj = item["fields"] as JsonObject;
        if (fieldsObj is null || fieldsObj.Count == 0)
            return null;

        var ordered = fieldsObj
            .Select(kv => (
                Name: kv.Key,
                Order: kv.Value?["order"] is JsonValue ov && ov.TryGetValue<int>(out var o) ? o : 0,
                Raw: kv.Value?["value"]?.ToString() ?? string.Empty))
            .OrderBy(f => f.Order)
            .ToList();

        var hasAudio = ordered.Any(f => SoundTag.IsMatch(f.Raw));
        var hasImage = ordered.Any(f => ImgTag.IsMatch(f.Raw));

        var exprField = ordered.FirstOrDefault(f => WordFieldPattern.IsMatch(f.Name) && Clean(f.Raw).Length > 0);
        if (string.IsNullOrEmpty(exprField.Name))
            exprField = ordered.FirstOrDefault(f => Clean(f.Raw).Length > 0);

        var readField = ordered.FirstOrDefault(f => f.Name != exprField.Name && ReadingFieldPattern.IsMatch(f.Name) && Clean(f.Raw).Length > 0);
        var sentField = ordered.FirstOrDefault(f => f.Name != exprField.Name && SentenceFieldPattern.IsMatch(f.Name) && !f.Name.Contains("Audio", StringComparison.OrdinalIgnoreCase) && !f.Name.Contains("Translation", StringComparison.OrdinalIgnoreCase) && Clean(f.Raw).Length > 0);

        var expression = Clean(exprField.Raw);
        if (expression.Length == 0)
            return null;

        DateTime created;
        try { created = DateTimeOffset.FromUnixTimeMilliseconds(noteId).UtcDateTime; }
        catch { created = DateTime.UtcNow; }

        return new AnkiRecentNote(
            noteId,
            Truncate(expression, 40),
            Truncate(Clean(readField.Raw), 40),
            Truncate(Clean(sentField.Raw), 140),
            model,
            tags,
            hasAudio,
            hasImage,
            created);
    }

    private static string Clean(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;
        var text = SoundTag.Replace(html, string.Empty);
        text = HtmlTag.Replace(text, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
