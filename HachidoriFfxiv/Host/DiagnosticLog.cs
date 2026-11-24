using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HachidoriFfxiv;

internal static partial class DiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Events = new();
    [GeneratedRegex(@"(?:https?|wss?)://[^\s""<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex RedactUrlRegex();

    public static void Write(string area, string message)
    {
        // Never store request bodies, media, dialogue, source URLs, or credentials.
        var safe = RedactUrlRegex().Replace(message, "<url>");
        if (safe.Length > 2000) safe = safe[..2000];
        lock (Gate)
        {
            Events.Enqueue($"{DateTime.UtcNow:O} [{area}] {safe}");
            while (Events.Count > 500) Events.Dequeue();
        }
    }

    public static string Export(string directory, JsonObject context)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"hachidori-ffxiv-debug-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
        lock (Gate)
        {
            var document = new JsonObject
            {
                ["createdUtc"] = DateTime.UtcNow.ToString("O"),
                ["pluginVersion"] = typeof(DiagnosticLog).Assembly.GetName().Version?.ToString(),
                ["context"] = context.DeepClone(),
                ["events"] = new JsonArray(Array.ConvertAll(Events.ToArray(), e => (JsonNode?)JsonValue.Create(e))),
            };
            File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        return path;
    }
}
