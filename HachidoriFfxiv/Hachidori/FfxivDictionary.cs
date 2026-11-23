using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using HachidoriFfxiv.Offline;

namespace HachidoriFfxiv.Hachidori;

/// <summary>Installed state for the optional custom FFXIV Hachidori dictionary.</summary>
public sealed record InstalledFfxivDictionary(string Title, string Revision, bool Enabled);

/// <summary>
/// Public download links, host detection, and browser redirection for the
/// repository's custom FFXIV Japanese-to-English Yomitan/Hachidori dictionary.
/// </summary>
public static class FfxivDictionary
{
    public const string Title = "FFXIV Hachidori JP–EN";
    public const string ArchiveFileName = "FFXIV-Hachidori-JP-EN.zip";
    public const string DownloadUrl = "https://github.com/KonyAHS/HachidoriFfxiv/releases/latest/download/FFXIV-Hachidori-JP-EN.zip";
    public const string GuideUrl = "https://github.com/KonyAHS/HachidoriFfxiv/tree/main/dictionaries/ffxiv";

    public static bool IsCustomDictionaryTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;
        var trimmed = title.Trim();
        return string.Equals(trimmed, Title, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "FFXIV Hachidori JP-EN", StringComparison.OrdinalIgnoreCase);
    }

    public static InstalledFfxivDictionary? FindInstalled(IEnumerable<JsonObject> dictionaries)
    {
        foreach (var d in dictionaries)
        {
            var title = LookupResult.Str(d["title"]);
            if (!IsCustomDictionaryTitle(title))
                continue;
            var revision = LookupResult.Str(d["revision"]);
            var enabled = d["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var en) || en;
            return new InstalledFfxivDictionary(title, revision, enabled);
        }
        return null;
    }

    public static InstalledFfxivDictionary? FindInstalledOffline(IEnumerable<OfflineDictionary> dictionaries)
    {
        var match = dictionaries.FirstOrDefault(d => IsCustomDictionaryTitle(d.Title));
        return match is null ? null : new InstalledFfxivDictionary(match.Title, match.Revision, Enabled: true);
    }

    public static bool IsAllowedExternalUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public static bool TryOpenUrl(string url, out string? error)
    {
        if (!IsAllowedExternalUrl(url))
        {
            error = "Refused to open an unsupported URL scheme.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
