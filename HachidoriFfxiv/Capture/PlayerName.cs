using System.Collections.Generic;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Capture;

/// <summary>The logged-in character's name in katakana, for rewriting captured text.</summary>
public static class PlayerName
{
    private static string cachedKey = string.Empty;
    private static IReadOnlyList<(string Latin, string Kana)> cached = [];

    /// <summary>Replacements for the current character, empty when disabled or logged out.</summary>
    public static IReadOnlyList<(string Latin, string Kana)> Replacements(Configuration config)
    {
        if (!config.KatakanaPlayerName || !Plugin.PlayerState.IsLoaded)
            return [];
        var name = Plugin.PlayerState.CharacterName;
        var key = name + "\u0001" + config.PlayerNameKatakana;
        if (key != cachedKey)
        {
            cached = NameKana.Replacements(name, config.PlayerNameKatakana);
            cachedKey = key;
        }

        return cached;
    }

    /// <summary>The automatic katakana for the current character, shown in settings.</summary>
    public static string Suggested()
    {
        if (!Plugin.PlayerState.IsLoaded)
            return string.Empty;
        var parts = Plugin.PlayerState.CharacterName.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        return string.Join('・', System.Array.ConvertAll(parts, NameKana.ToKatakana));
    }
}
