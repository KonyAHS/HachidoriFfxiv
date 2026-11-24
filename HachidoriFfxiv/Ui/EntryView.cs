using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;
using HachidoriFfxiv.Rendering.Css;

namespace HachidoriFfxiv.Ui;

public enum AnkiState
{
    Unknown,
    Checking,
    Ready,
    Duplicate,
    Busy,
    Added,
    Error,
}

/// <summary>One dictionary entry in a popup, with its prebuilt text blocks and per-entry state.</summary>
public sealed class EntryView
{
    private readonly Configuration config;
    private readonly PopupTheme theme = PopupTheme.Current;
    private readonly Dictionary<int, float> sectionHeights = new();

    public EntryView(LookupResult result, Configuration config, bool isKanji)
    {
        this.config = config;
        Result = result;
        IsKanji = isKanji;
        Headword = BuildHeadword(result.Term);
        Meta = BuildMeta();
        foreach (var group in result.Term.Glossaries)
        {
            var header = new RichBlock { MarginTopEm = 0.25f };
            if (config.ShowDictionaryNames && group.Dictionary.Length > 0)
                header.Add(ThemePainter.Chip(HachidoriFfxiv.Hachidori.NpcImageDictionary.IsPictureDictionary(group.Dictionary) ? "Your NPC picture" : group.Dictionary, theme.DictionaryChip, theme)).Add(" ");
            if (config.ShowDefinitionTags)
            {
                foreach (var tag in group.Tags.Count > 0
                             ? group.Tags.Select(t => t.Name)
                             : group.DefinitionTags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    header.Add(ThemePainter.Chip(tag, theme.TagChip, theme)).Add(" ");
            }

            Definitions.Add((header.IsEmpty ? null : header, GlossaryBuilder.Build(group.Content, 0.4f, config.ShowAttribution, group.Dictionary)));
        }
    }

    public LookupResult Result { get; }
    public bool IsKanji { get; }
    public RichBlock Headword { get; }
    public RichBlock Meta { get; private set; }
    public List<(RichBlock? Header, List<RichBlock> Blocks)> Definitions { get; } = new();

    /// <summary>The entry card's height last frame, so the card can be painted behind its content.</summary>
    public float CardHeight { get; set; }

    public int? LookupCount { get; set; }
    public DateTime? BlurUntil { get; set; }
    public bool Revealed { get; set; }

    public AnkiState Anki { get; set; } = AnkiState.Unknown;
    public string? AnkiMessage { get; set; }
    public AnkiDecision? Decision { get; set; }
    public JsonObject? MiningRequest { get; set; }

    public void SetLookupCount(int? count)
    {
        LookupCount = count;
        Meta = BuildMeta();
    }

    private RichBlock BuildHeadword(TermInfo term)
    {
        var block = new RichBlock();
        var scale = theme.HeadwordScale;
        var color = theme.Headword is { } h ? CssColor.ToAbgr(h) : 0;
        var rubyColor = theme.Reading is { } r ? CssColor.ToAbgr(r) : 0;
        var segments = term.Furigana ?? Japanese.DistributeFurigana(term.Expression, term.Reading);
        foreach (var segment in segments)
        {
            block.Add(new RichRun
            {
                Text = segment.Text,
                Ruby = segment.Reading.Length > 0 ? segment.Reading : null,
                Scale = scale,
                Color = color,
                RubyColor = rubyColor,
            });
        }

        if (block.IsEmpty)
            block.Add(term.Expression, color, scale);
        return block;
    }

    private RichBlock BuildMeta()
    {
        var block = new RichBlock();
        if (config.ShowInflections && Result.Trace.Count > 0)
        {
            block.Add(ThemePainter.Chip("« " + string.Join(" « ", Result.Trace), theme.InflectionChip, theme)).Add(" ");
        }

        if (config.ShowFrequencies)
        {
            foreach (var group in Result.Term.Frequencies)
            {
                if (group.Values.Count == 0)
                    continue;
                var values = string.Join(", ", group.Values.Select(v => v.Display).Distinct());
                block.Add(ThemePainter.Chip($"{group.Dictionary}  {values}", theme.FrequencyChip, theme)).Add(" ");
            }
        }

        if (config.ShowLookupCounts && LookupCount is > 0)
        {
            block.Add(ThemePainter.Chip($"looked up {LookupCount}×", theme.CountChip, theme));
        }

        return block;
    }

    /// <summary>Draws the entry; returns the hovered text (for nested lookups).</summary>
    public TextHit? Draw(PopupManager manager, int depth, float width, HachidoriService service, Audio.AudioPlayer audio, LookupReply reply, ScanContext context)
    {
        TextHit? hit = null;
        var style = ImGui.GetStyle();
        var buttonSize = ImGui.GetFrameHeight();
        var buttonsWidth = (buttonSize + style.ItemSpacing.X) * (IsKanji ? 3 : 4);
        var start = ImGui.GetCursorPos();

        // Buttons on the right of the headword.
        ImGui.SetCursorPos(new Vector2(start.X + width - buttonsWidth, start.Y + ImGui.GetFontSize() * 0.6f));
        DrawButtons(manager, service, audio);
        ImGui.SetCursorPos(start);

        var hl = manager.HighlightFor(Headword, depth);
        hit = RichText.Draw(Headword, width - buttonsWidth, hl) ?? hit;
        if (!Meta.IsEmpty)
            hit = RichText.Draw(Meta, width, null, false) ?? hit;

        if (config.ShowPitchAccent && !IsKanji)
            DrawPitch(width);

        if (AnkiMessage is { Length: > 0 })
            ImGui.TextColored(Anki == AnkiState.Error ? new Vector4(1f, 0.5f, 0.5f, 1f) : new Vector4(0.6f, 0.9f, 0.6f, 1f), AnkiMessage);
        if (audio.LastError is { Length: > 0 } audioError)
            ImGui.TextWrapped($"Audio: {audioError}");

        var blurred = !Revealed && BlurUntil is { } until && DateTime.UtcNow < until;
        var defsStart = ImGui.GetCursorScreenPos();
        var section = theme.IsPlugin ? null : theme.Section;
        for (var g = 0; g < Definitions.Count; g++)
        {
            var (header, blocks) = Definitions[g];
            // A themed section (Crystal's framed block, Default's glossary card) is painted
            // behind its content from last frame's height; the first frame is invisible anyway.
            var top = ImGui.GetCursorScreenPos();
            var pad = Vector4.Zero;
            var innerWidth = width;
            if (section is not null)
            {
                pad = section.Padding * theme.Scale;
                ImGui.Dummy(new Vector2(1, 3 * theme.Scale));
                top = ImGui.GetCursorScreenPos();
                if (sectionHeights.TryGetValue(g, out var height))
                    ThemePainter.Paint(ImGui.GetWindowDrawList(), top, top + new Vector2(width, height), section, theme.Scale);
                ImGui.Dummy(new Vector2(1, MathF.Max(pad.X, 2)));
                pad.W = MathF.Max(pad.W, 4 * theme.Scale);
                pad.Y = MathF.Max(pad.Y, 4 * theme.Scale);
                ImGui.Indent(pad.W);
                innerWidth = width - pad.W - pad.Y;
            }

            if (header is not null)
                RichText.Draw(header, innerWidth, null, false);
            foreach (var block in blocks)
            {
                if (block.Image is { } image)
                {
                    manager.Images.Draw(image, reply.Generation, innerWidth);
                    continue;
                }
                var h = RichText.Draw(block, innerWidth, manager.HighlightFor(block, depth), !blurred);
                hit = h ?? hit;
            }

            if (section is not null)
            {
                ImGui.Unindent(pad.W);
                ImGui.Dummy(new Vector2(1, MathF.Max(pad.Z, 2)));
                sectionHeights[g] = ImGui.GetCursorScreenPos().Y - top.Y;
            }
        }

        if (blurred)
        {
            var end = ImGui.GetCursorScreenPos();
            var drawList = ImGui.GetWindowDrawList();
            var min = defsStart;
            var max = new Vector2(defsStart.X + width, end.Y);
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.PopupBg) | 0xFF000000, 6f);
            var label = $"Recall it first: looked up {LookupCount}×. Click to reveal.";
            var size = ImGui.CalcTextSize(label);
            drawList.AddText((min + max - size) * 0.5f, ImGui.GetColorU32(ImGuiCol.TextDisabled), label);
            if (ImGui.IsMouseHoveringRect(min, max) && ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                Revealed = true;
        }

        return hit;
    }

    private void DrawButtons(PopupManager manager, HachidoriService service, Audio.AudioPlayer audio)
    {
        var term = Result.Term;
        using (DalamudServices.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            if (Button(FontAwesomeIcon.VolumeUp, "Play pronunciation (Hachidori audio sources)"))
                audio.PlayTerm(service, term.Expression, term.Reading);
            ImGui.SameLine();

            var (icon, tip, enabled) = Anki switch
            {
                AnkiState.Checking or AnkiState.Unknown => (FontAwesomeIcon.Spinner, "Checking Anki…", false),
                AnkiState.Ready => (FontAwesomeIcon.Plus, "Add to Anki with your Hachidori Template", true),
                AnkiState.Duplicate => (FontAwesomeIcon.Search, "Already in Anki: view the note (Shift-click to add anyway)", true),
                AnkiState.Busy => (FontAwesomeIcon.Spinner, "Saving to Anki…", false),
                AnkiState.Added => (FontAwesomeIcon.Check, "Added: view the note in Anki", true),
                _ => (FontAwesomeIcon.ExclamationTriangle, AnkiMessage ?? "Anki is unavailable", true),
            };
            using (ImRaii.Disabled(!enabled))
            {
                if (Button(icon, tip))
                    OnAnkiClicked?.Invoke(this, ImGui.GetIO().KeyShift);
            }
            ImGui.SameLine();

            if (Button(FontAwesomeIcon.Copy, "Copy headword"))
                ImGui.SetClipboardText(term.Expression);
            if (!IsKanji)
            {
                ImGui.SameLine();
                using (ImRaii.Disabled(manager.NpcImages.Busy || !manager.NpcImages.Available))
                {
                    if (Button(FontAwesomeIcon.Camera, manager.NpcImages.Available
                            ? "Attach an NPC picture in Hachidori: capture, drag around the NPC, release to save. Replaces your previous picture for this name and reading. All pictures share one dictionary; /hachidori pictures lists them."
                            : "NPC pictures require a connected Hachidori host with dictionary upload support."))
                        manager.NpcImages.Start(term);
                }
            }
        }
    }

    public event Action<EntryView, bool>? OnAnkiClicked;

    /// <summary>The + button's action, for the controller.</summary>
    public void RequestAnki(bool force = false) => OnAnkiClicked?.Invoke(this, force);

    /// <summary>The entry's top in its popup's content (window-local Y), from the last draw.</summary>
    public float TopY { get; set; }

    private static bool Button(FontAwesomeIcon icon, string tooltip)
    {
        var clicked = ImGui.Button(icon.ToIconString(), new Vector2(ImGui.GetFrameHeight()));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            using (DalamudServices.PluginInterface.UiBuilder.DefaultFontHandle.Push())
                ImGui.SetTooltip(tooltip);
        }

        return clicked;
    }

    private void DrawPitch(float width)
    {
        var reading = Result.Term.Reading.Length > 0 ? Result.Term.Reading : Result.Term.Expression;
        var morae = Japanese.Morae(reading);
        if (morae.Count == 0)
            return;
        var shown = new HashSet<string>();
        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize();
        var (labelFont, labelSize) = RichText.Sized(size * 0.8f);
        var drawList = ImGui.GetWindowDrawList();
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var pitchColor = CssColor.ToAbgr(theme.PitchLine);
        var x0 = ImGui.GetCursorScreenPos().X;
        var lineStart = true;
        foreach (var group in Result.Term.Pitches)
        {
            foreach (var pitch in group.Pitches)
            {
                var key = $"{pitch.Position}|{pitch.Pattern}";
                if (!shown.Add(key))
                    continue;
                var label = $"[{Japanese.DownstepLabel(pitch)}]";
                var widths = morae.Select(m => RichText.Measure(font, size, m)).ToList();
                var total = widths.Sum() + RichText.Measure(labelFont, labelSize, label) + size * 0.8f;
                if (!lineStart && ImGui.GetCursorScreenPos().X + total > x0 + width)
                    ImGui.NewLine();
                var pos = ImGui.GetCursorScreenPos();
                var top = pos.Y + 3f;
                var x = pos.X;
                for (var i = 0; i < morae.Count; i++)
                {
                    var high = Japanese.IsMoraHigh(i, pitch);
                    drawList.AddText(font, size, new Vector2(x, top + 2), textColor, morae[i]);
                    if (high)
                    {
                        drawList.AddLine(new Vector2(x, top), new Vector2(x + widths[i], top), pitchColor, 2f);
                        if (!Japanese.IsMoraHigh(i + 1, pitch))
                            drawList.AddLine(new Vector2(x + widths[i], top), new Vector2(x + widths[i], top + size * 0.45f), pitchColor, 2f);
                    }

                    if (pitch.Nasal.Contains(i + 1))
                        drawList.AddCircle(new Vector2(x + widths[i] - 2, top + 4), 2.5f, pitchColor);
                    x += widths[i];
                }

                drawList.AddText(labelFont, labelSize, new Vector2(x + size * 0.2f, top + 2 + size * 0.15f), ImGui.GetColorU32(ImGuiCol.TextDisabled), label);
                ImGui.Dummy(new Vector2(total, size * 1.35f));
                if (ImGui.IsItemHovered() && group.Dictionary.Length > 0)
                {
                    using (DalamudServices.PluginInterface.UiBuilder.DefaultFontHandle.Push())
                        ImGui.SetTooltip(group.Dictionary);
                }

                ImGui.SameLine();
                lineStart = false;
            }
        }

        if (!lineStart)
            ImGui.NewLine();
    }
}
