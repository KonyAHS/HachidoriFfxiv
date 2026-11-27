using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace HachidoriFfxiv.Stats;

/// <summary>
/// Builds a live <see cref="MsqQuestIndex"/> from the player's installed FFXIV Excel sheets
/// (so future patches and expansions work automatically) and detects the character's current MSQ
/// via <see cref="QuestManager"/>.
/// </summary>
public sealed class FfxivMsqResolver
{
    private readonly IDataManager dataManager;
    private readonly object sync = new();

    private MsqQuestIndex? index;
    private bool loading;
    private string? loadError;

    public FfxivMsqResolver(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public MsqQuestIndex? Index => index;
    public bool IsLoading => loading;
    public string? LoadError => loadError;

    /// <summary>Starts loading the MSQ quest index on a background thread once (safe to call every frame).</summary>
    public void EnsureLoaded()
    {
        if (index is not null || loading)
            return;

        lock (sync)
        {
            if (index is not null || loading)
                return;
            loading = true;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var built = BuildIndex();
                index = built;
                loadError = null;
                DiagnosticLog.Milestone("stats", $"Indexed {built.Quests.Count} live FFXIV MSQ quests ({built.TotalMsqChars:N0} Japanese dialogue chars).");
            }
            catch (Exception ex)
            {
                loadError = ex.Message;
                DiagnosticLog.Write("stats", $"Failed to index FFXIV MSQ quests: {ex.Message}");
            }
            finally
            {
                loading = false;
            }
        });
    }

    /// <summary>
    /// Detects the logged-in character's current MSQ quest from <see cref="QuestManager"/>.
    /// Must be called on the main game/UI thread.
    /// </summary>
    public unsafe (MsqQuestEntry? Quest, bool IsAccepted) DetectCurrentMsq()
    {
        var idx = index;
        if (idx is null || idx.Quests.Count == 0)
            return (null, false);

        var qm = QuestManager.Instance();
        if (qm == null)
            return (null, false);

        // 1. Prefer an actively accepted MSQ quest (pick the latest one in chronological order if multiple).
        MsqQuestEntry? accepted = null;
        for (var i = 0; i < idx.Quests.Count; i++)
        {
            var q = idx.Quests[i];
            if (qm->IsQuestAccepted(q.RowId))
                accepted = q;
        }
        if (accepted is not null)
            return (accepted, true);

        // 2. Otherwise find the highest completed MSQ quest and return the next uncompleted MSQ quest after it.
        var lastCompletedIdx = -1;
        for (var i = 0; i < idx.Quests.Count; i++)
        {
            if (QuestManager.IsQuestComplete(idx.Quests[i].RowId))
                lastCompletedIdx = i;
        }

        if (lastCompletedIdx < 0)
            return (null, false);

        if (lastCompletedIdx + 1 < idx.Quests.Count)
            return (idx.Quests[lastCompletedIdx + 1], false);

        // Player has completed every MSQ quest currently in the game!
        return (idx.Quests[lastCompletedIdx], false);
    }

    private MsqQuestIndex BuildIndex()
    {
        var qJa = dataManager.GetExcelSheet<Quest>(ClientLanguage.Japanese);
        var qEn = dataManager.GetExcelSheet<Quest>(ClientLanguage.English);
        var exJa = dataManager.GetExcelSheet<ExVersion>(ClientLanguage.Japanese);
        var exEn = dataManager.GetExcelSheet<ExVersion>(ClientLanguage.English);
        var catEn = dataManager.GetExcelSheet<JournalCategory>(ClientLanguage.English);
        if (qJa is null)
            return new MsqQuestIndex([]);

        var raw = new List<(uint RowId, uint Exp, uint GenreId, ushort SortKey, string GenreName, string Ja, string En, int Chars)>();

        foreach (var q in qJa)
        {
            var id = q.Id.ExtractText();
            if (string.IsNullOrEmpty(id))
                continue;

            var gid = q.JournalGenre.RowId;
            var genre = q.JournalGenre.ValueNullable;
            var catRef = genre?.JournalCategory;
            var catNameJa = catRef?.ValueNullable?.Name.ExtractText() ?? string.Empty;
            var catNameEn = catRef is { RowId: > 0 } cat ? catEn?.GetRowOrDefault(cat.RowId)?.Name.ExtractText() : null;
            if (!MsqQuestIndex.IsMsqGenre(gid, catNameJa, catNameEn))
                continue;

            var folder = (q.RowId / 100 % 1000).ToString("D3");
            var us = id.LastIndexOf('_');
            if (us >= 0 && int.TryParse(id[(us + 1)..], out var num))
                folder = (num / 100).ToString("D3");

            var jpChars = 0;
            try
            {
                var sheet = dataManager.GetExcelSheet<RawRow>(ClientLanguage.Japanese, $"quest/{folder}/{id}");
                if (sheet is not null)
                {
                    foreach (var row in sheet)
                    {
                        var key = row.ReadStringColumn(0).ExtractText();
                        var val = row.ReadStringColumn(1).ExtractText();
                        if (string.IsNullOrWhiteSpace(val))
                            continue;
                        if (key.Contains("_SEQ_", StringComparison.Ordinal)
                            || key.Contains("_TODO_", StringComparison.Ordinal)
                            || key.Contains("_SYSTEM_", StringComparison.Ordinal))
                            continue;
                        var (c, _, _) = NonTrivialWords.CountJapaneseChars(val);
                        jpChars += c;
                    }
                }
            }
            catch
            {
                // Ignore missing/unreadable quest text sheets cleanly.
            }

            var jaName = q.Name.ExtractText();
            var enName = qEn?.GetRowOrDefault(q.RowId)?.Name.ExtractText() ?? string.Empty;
            var genreName = genre?.Name.ExtractText() ?? string.Empty;

            raw.Add((q.RowId, q.Expansion.RowId, gid, q.SortKey, genreName, jaName, enName, jpChars));
        }

        // Order chronologically across all expansions and patch arcs.
        raw.Sort((a, b) =>
        {
            var c = a.Exp.CompareTo(b.Exp);
            if (c != 0) return c;
            c = a.GenreId.CompareTo(b.GenreId);
            if (c != 0) return c;
            c = a.SortKey.CompareTo(b.SortKey);
            if (c != 0) return c;
            return a.RowId.CompareTo(b.RowId);
        });

        var entries = new List<MsqQuestEntry>(raw.Count);
        long cumulative = 0;
        var expCumulative = new Dictionary<uint, long>();

        foreach (var item in raw)
        {
            expCumulative.TryGetValue(item.Exp, out var expBefore);
            var expId = MsqQuestIndex.ExpansionIdFromIndex(item.Exp);
            var shortCode = MsqQuestIndex.ExpansionShortCodeFromIndex(item.Exp);

            entries.Add(new MsqQuestEntry(
                RowId: item.RowId,
                ExpansionId: expId,
                ExpansionShortCode: shortCode,
                JapaneseName: item.Ja,
                EnglishName: item.En,
                GenreName: item.GenreName,
                QuestChars: item.Chars,
                CumulativeBeforeChars: cumulative,
                ExpansionBeforeChars: expBefore));

            cumulative += item.Chars;
            expCumulative[item.Exp] = expBefore + item.Chars;
        }

        var built = new MsqQuestIndex(entries);

        // Dynamically register any future expansions (8.0+ / ExVersion >= 6) from live Lumina sheets.
        var extraExpansions = new List<FfxivExpansion>();
        foreach (var expIndex in expCumulative.Keys.Where(k => k > 5).OrderBy(k => k))
        {
            var expId = MsqQuestIndex.ExpansionIdFromIndex(expIndex);
            var shortCode = MsqQuestIndex.ExpansionShortCodeFromIndex(expIndex);
            var enName = exEn?.GetRowOrDefault(expIndex)?.Name.ExtractText();
            var jaName = exJa?.GetRowOrDefault(expIndex)?.Name.ExtractText();
            var msqChars = Math.Max(1L, expCumulative[expIndex]);
            var questCount = entries.Count(e => string.Equals(e.ExpansionId, expId, StringComparison.OrdinalIgnoreCase));
            extraExpansions.Add(new FfxivExpansion(
                Id: expId,
                ShortCode: shortCode,
                Name: !string.IsNullOrWhiteSpace(enName) ? enName : $"Expansion {shortCode}",
                JapaneseName: !string.IsNullOrWhiteSpace(jaName) ? jaName : shortCode,
                EstimatedMsqChars: msqChars,
                EstimatedAllQuestsChars: msqChars * 3,
                EstimatedMsqLines: Math.Max(1, questCount * 80),
                AccentRgba: 0xFF7FD4E6));
        }
        if (extraExpansions.Count > 0)
        {
            ExpansionEstimates.RegisterExtraExpansions(extraExpansions);
            DiagnosticLog.Write("stats", $"Registered {extraExpansions.Count} dynamic post-7.x expansion(s): {string.Join(", ", extraExpansions.Select(e => $"{e.ShortCode} {e.Name}"))}");
        }

        return built;
    }
}
