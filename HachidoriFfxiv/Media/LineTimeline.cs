using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HachidoriFfxiv.Capture;

namespace HachidoriFfxiv.Media;

/// <summary>When a dialogue line was on screen, on the <see cref="Stopwatch"/> clock.</summary>
public sealed class LineSpan
{
    public required GameLine Line { get; init; }
    public required long Start { get; init; }
    public long? End { get; set; }
    /// <summary>The playing OST and its playback timing when this line appeared, for clean TTS + OST mixing.</summary>
    public BgmSnapshot? Bgm { get; init; }
}

/// <summary>
/// The recent dialogue and battle-talk lines with their on-screen times. A line ends when the
/// next one appears or its window closes.
/// </summary>
public sealed class LineTimeline
{
    private readonly List<LineSpan> spans = new();
    private readonly object gate = new();

    public void OnLine(GameLine line, long now, BgmSnapshot? bgm = null)
    {
        if (line.Source is not (LineSource.Talk or LineSource.BattleTalk))
            return;
        lock (gate)
        {
            foreach (var open in spans.Where(s => s.End is null))
                open.End = now;
            spans.Add(new LineSpan { Line = line, Start = now, Bgm = bgm });
            if (spans.Count > 200)
                spans.RemoveRange(0, spans.Count - 200);
        }
    }

    /// <summary>Called each frame: a closed dialogue window ends its line.</summary>
    public void OnWindows(bool talkVisible, bool battleTalkVisible, long now)
    {
        lock (gate)
        {
            foreach (var open in spans.Where(s => s.End is null))
            {
                var visible = open.Line.Source == LineSource.Talk ? talkVisible : battleTalkVisible;
                if (!visible)
                    open.End = now;
            }
        }
    }

    public long? EndOf(LineSpan span)
    {
        lock (gate)
            return span.End;
    }

    /// <summary>The newest line whose text is the mined sentence (or contains it, or is contained by it).</summary>
    public LineSpan? Find(string sentence)
    {
        var wanted = Normalize(sentence);
        if (wanted.Length == 0)
            return null;
        lock (gate)
        {
            for (var i = spans.Count - 1; i >= 0; i--)
            {
                var text = Normalize(spans[i].Line.Text);
                if (text == wanted)
                    return spans[i];
            }

            for (var i = spans.Count - 1; i >= 0; i--)
            {
                var text = Normalize(spans[i].Line.Text);
                if (text.Length > 0 && (text.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(text, StringComparison.Ordinal)))
                    return spans[i];
            }
        }

        return null;
    }

    private static string Normalize(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c) && c != '　').ToArray());
}
