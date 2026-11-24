using System;

namespace HachidoriFfxiv.Capture;

/// <summary>
/// Geometry and flags of an <c>AtkTextNode</c> inside <c>Talk</c>, used to identify
/// the dialogue body and speaker nodes without depending on offset-named ClientStructs fields.
/// </summary>
internal readonly record struct TalkTextNodeCandidate(
    int Index,
    uint NodeId,
    float X,
    float Y,
    ushort Width,
    ushort Height,
    bool Visible,
    bool MultiLine,
    bool WordWrap,
    bool HasText);

/// <summary>
/// Pure, ClientStructs-free UI classification and node-selection heuristics shared
/// between <see cref="GameUiInterop"/> and offline regression checks.
/// </summary>
internal static class GameUiHeuristics
{
    /// <summary>FFXIV's UI depth layer for tooltips and floating hover details.</summary>
    internal const uint TooltipDepthLayer = 9;

    /// <summary>
    /// Selects the dialogue body and speaker text node indices from <c>Talk</c>'s text nodes
    /// using box geometry and multi-line flags rather than struct offsets or string length.
    /// Keeps short lines (e.g. "はい") and empty transition frames anchored to the body box.
    /// </summary>
    internal static (int SpeakerIndex, int BodyIndex) SelectTalkNodeIndices(ReadOnlySpan<TalkTextNodeCandidate> candidates)
    {
        var bodyIdx = -1;
        var bodyScore = -1L;

        // Pass 1: visible multi-line / word-wrapped dialogue box.
        for (var i = 0; i < candidates.Length; i++)
        {
            var c = candidates[i];
            if (!c.Visible || (!c.MultiLine && !c.WordWrap) || c.Width < 100 || c.Height < 30)
                continue;
            var area = (long)c.Width * c.Height;
            var score = area * 2 + (c.HasText ? 1 : 0);
            if (score > bodyScore)
            {
                bodyScore = score;
                bodyIdx = i;
            }
        }

        // Pass 2: largest visible box of dialogue proportions if flags changed.
        if (bodyIdx < 0)
        {
            for (var i = 0; i < candidates.Length; i++)
            {
                var c = candidates[i];
                if (!c.Visible || c.Width < 100 || c.Height < 30)
                    continue;
                var area = (long)c.Width * c.Height;
                var score = area * 2 + (c.HasText ? 1 : 0);
                if (score > bodyScore)
                {
                    bodyScore = score;
                    bodyIdx = i;
                }
            }
        }

        // Pass 3: even if momentarily hidden, keep the largest multi-line box anchored.
        if (bodyIdx < 0)
        {
            for (var i = 0; i < candidates.Length; i++)
            {
                var c = candidates[i];
                if ((!c.MultiLine && !c.WordWrap) || c.Width < 100 || c.Height < 30)
                    continue;
                var area = (long)c.Width * c.Height;
                if (area > bodyScore)
                {
                    bodyScore = area;
                    bodyIdx = i;
                }
            }
        }

        if (bodyIdx < 0)
            return (-1, -1);

        var body = candidates[bodyIdx];
        var speakerIdx = -1;
        var speakerScore = long.MinValue;

        for (var i = 0; i < candidates.Length; i++)
        {
            if (i == bodyIdx)
                continue;
            var c = candidates[i];
            if (!c.Visible || c.Width < 40 || c.Height == 0)
                continue;
            // The speaker nameplate sits above the dialogue body and is shorter than it.
            if (c.Y >= body.Y || c.Height >= body.Height)
                continue;

            var score = (c.HasText ? 1_000_000_000L : 0L)
                + (c.MultiLine ? 0L : 100_000_000L)
                + c.Width * 100L
                - (long)Math.Abs(body.Y - c.Y);
            if (score > speakerScore)
            {
                speakerScore = score;
                speakerIdx = i;
            }
        }

        return (speakerIdx, bodyIdx);
    }

    /// <summary>Parses a chat panel addon name such as <c>ChatLogPanel_0</c> into its tab index.</summary>
    internal static bool TryParseChatPanelIndex(string? name, out int index)
    {
        const string Prefix = "ChatLogPanel_";
        index = -1;
        if (string.IsNullOrEmpty(name) || !name.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        return int.TryParse(name.AsSpan(Prefix.Length), out index) && index >= 0;
    }

    /// <summary>Classifies whether an addon is a tooltip by name or UI depth layer.</summary>
    internal static bool IsTooltipAddon(string name, uint? depthLayer = null)
    {
        if (depthLayer is >= TooltipDepthLayer)
            return true;
        return name is "ItemDetail" or "ActionDetail" or "Tooltip"
               || name.EndsWith("Detail", StringComparison.Ordinal)
               || name.EndsWith("Tooltip", StringComparison.Ordinal);
    }
}
