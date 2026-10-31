using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HachidoriFfxiv.Publishing;

/// <summary>
/// Turns per-frame text reads into finished lines: a line is released once it stops changing,
/// a growing line replaces its earlier prefix, and a redraw of the same text is not repeated.
/// </summary>
public sealed class PublishGate
{
    private readonly TimeSpan settle;
    private readonly TimeSpan repeatWindow;
    private readonly Dictionary<string, (string Text, DateTime Changed)> pending = new();
    private readonly Dictionary<string, (string Text, DateTime Time)> sent = new();
    private readonly List<string> ready = new();

    public PublishGate(TimeSpan? settle = null, TimeSpan? repeatWindow = null)
    {
        this.settle = settle ?? TimeSpan.FromMilliseconds(150);
        this.repeatWindow = repeatWindow ?? TimeSpan.FromSeconds(2);
    }

    public void Offer(string source, string text, DateTime now)
    {
        if (pending.TryGetValue(source, out var current))
        {
            if (current.Text == text)
                return;
            if (!text.StartsWith(current.Text, StringComparison.Ordinal))
                Release(source, current.Text, now);
        }
        pending[source] = (text, now);
    }

    public IReadOnlyList<string> Drain(DateTime now)
    {
        foreach (var (source, line) in new List<KeyValuePair<string, (string Text, DateTime Changed)>>(pending))
        {
            if (now - line.Changed < settle)
                continue;
            Release(source, line.Text, now);
            pending.Remove(source);
        }
        if (ready.Count == 0)
            return [];
        var lines = ready.ToArray();
        ready.Clear();
        return lines;
    }

    private void Release(string source, string text, DateTime now)
    {
        if (sent.TryGetValue(source, out var last) && last.Text == text && now - last.Time < repeatWindow)
            return;
        sent[source] = (text, now);
        ready.Add(text);
    }
}

public static class PublishMessage
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Line(string speaker, string text, bool includeSpeaker) =>
        includeSpeaker && speaker.Length > 0 ? $"{speaker}：{text}" : text;

    /// <summary>
    /// GSM reads <c>sentence</c>. Always send an object: GSM also parses plain text as JSON,
    /// so a bare line such as 123 would make its listener drop the connection.
    /// </summary>
    public static string Format(string line) =>
        new JsonObject { ["sentence"] = line }.ToJsonString(Options);
}
