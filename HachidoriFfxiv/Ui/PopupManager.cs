using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;
using HachidoriFfxiv.Rendering.Css;

namespace HachidoriFfxiv.Ui;

/// <summary>
/// Yomitan-style popups: hovering scannable text (in the reader, the search window, or inside a
/// popup) looks the text up from that character and opens a popup next to it. Popups nest: text
/// inside popup N opens popup N+1, and moving away closes the deeper ones.
/// </summary>
public sealed class PopupManager : IDisposable
{
    private readonly Configuration config;
    private readonly HachidoriService service;
    private readonly Audio.AudioPlayer audio;
    private readonly Fonts fonts;
    private readonly List<PopupLayer> layers = new();
    private PendingHit? frameHit;
    private DateTime ankiStatusAt = DateTime.MinValue;
    private Task<HachidoriService.AnkiStatus>? ankiStatus;

    public PopupManager(Configuration config, HachidoriService service, Audio.AudioPlayer audio, Fonts fonts)
    {
        this.config = config;
        this.service = service;
        this.audio = audio;
        this.fonts = fonts;
        Images = new DictionaryImages(service);
        NpcImages = new NpcImageCapture(service);
        Themes = new PopupThemeProvider(config, service);
        service.Link.WordStatusChanged += () => ankiStatusAt = DateTime.MinValue;
        // Live and offline mining check different things: ask again when the backend changes.
        service.Link.ConnectionChanged += () => ankiStatusAt = DateTime.MinValue;
    }

    public bool AnyOpen => layers.Any(l => l.Visible);
    public DictionaryImages Images { get; }
    public NpcImageCapture NpcImages { get; }
    public PopupThemeProvider Themes { get; }

    /// <summary>Game screenshot, voice line or TTS, and GIF for mined notes; set by the plugin.</summary>
    public Media.MediaMiner? Media { get; set; }

    /// <summary>Immersion statistics tracker; set by the plugin.</summary>
    public Stats.StatsTracker? Stats { get; set; }
    public bool ContainsMouse(Vector2 mouse) => layers.Any(l => l.Visible && l.Contains(mouse));

    public void CloseAll() => layers.Clear();
    public void Dispose()
    {
        CloseAll();
        Images.Dispose();
        NpcImages.Dispose();
    }

    internal sealed record PendingHit(TextHit Hit, int Depth, ScanContext Context, bool Kanji, int? TokenEnd = null,
        bool Forced = false, bool KeepPrevious = true);

    // ------------------------------------------------------------ controller

    /// <summary>Controller mode: mouse hovers only count after the mouse moves, and the cursor's parents stay open.</summary>
    public bool ControllerActive { get; set; }
    private Vector2 lastMouse;
    private DateTime mouseMovedAt = DateTime.MinValue;
    private int sustainDepth = -1;

    private bool MouseRecentlyMoved => (DateTime.UtcNow - mouseMovedAt).TotalSeconds < 0.75;

    /// <summary>Keeps popups 0..depth open this frame.</summary>
    public void Sustain(int depth) => sustainDepth = Math.Max(sustainDepth, depth);

    /// <summary>The number of visible popups.</summary>
    public int VisibleCount => layers.TakeWhile(l => l.Visible).Count();

    /// <summary>The popup's screen rectangle.</summary>
    public (Vector2 Min, Vector2 Max)? LayerRect(int depth) =>
        depth < layers.Count && layers[depth].Visible ? (layers[depth].WindowPos, layers[depth].WindowPos + layers[depth].WindowSize) : null;

    /// <summary>The scannable definition text of popup <paramref name="depth"/>, in reading order.</summary>
    public List<RichBlock> ScanBlocks(int depth)
    {
        var blocks = new List<RichBlock>();
        if (depth >= layers.Count || layers[depth].Showing is not { } shown)
            return blocks;
        foreach (var entry in shown.Entries)
            foreach (var (_, group) in entry.Definitions)
                blocks.AddRange(group.Where(b => b.Image is null && !b.IsEmpty));
        return blocks;
    }

    /// <summary>Moves the controller's entry selection in popup <paramref name="depth"/> and scrolls to it.</summary>
    public void SelectEntry(int depth, int delta)
    {
        if (depth >= layers.Count || layers[depth].Showing is not { Entries.Count: > 0 } shown)
            return;
        var layer = layers[depth];
        layer.SelectedEntry = Math.Clamp(layer.SelectedEntry + delta, 0, shown.Entries.Count - 1);
        layer.ScrollToSelected = true;
    }

    public EntryView? SelectedEntry(int depth) =>
        depth < layers.Count && layers[depth].Showing is { Entries.Count: > 0 } shown
            ? shown.Entries[Math.Clamp(layers[depth].SelectedEntry, 0, shown.Entries.Count - 1)]
            : null;

    public void MineSelected(int depth) => SelectedEntry(depth)?.RequestAnki();

    public void PlaySelected(int depth)
    {
        if (SelectedEntry(depth) is { } entry)
            audio.PlayTerm(service, entry.Result.Term.Expression, entry.Result.Term.Reading);
    }

    /// <summary>Scrolls popup <paramref name="depth"/> by <paramref name="pixels"/> on its next draw.</summary>
    public void Scroll(int depth, float pixels)
    {
        if (depth < layers.Count)
            layers[depth].PendingScroll += pixels;
    }

    /// <summary>Closes the deepest popup (controller "back").</summary>
    public void CloseFrom(int depth)
    {
        if (depth < layers.Count)
            layers.RemoveRange(depth, layers.Count - depth);
    }

    /// <summary>A text area reports the hovered character. Depth is 0 outside popups, N+1 inside popup N.</summary>
    public void Report(TextHit? hit, int depth, Func<RichBlock, int, ScanContext>? context = null,
        bool forced = false, bool keepPrevious = true, bool bypassTokenize = false)
    {
        if (hit is not { } h)
            return;
        // While the controller drives, a resting mouse must not fight its cursor.
        if (ControllerActive && !forced && !MouseRecentlyMoved)
            return;
        int? tokenEnd = null;
        if (config.TokenizedHover && !bypassTokenize)
        {
            var task = service.SegmentAsync(h.Block.ScanText);
            if (!task.IsCompleted) return;
            if (task.IsCompletedSuccessfully && WordSpan.At(task.Result, h.CharIndex) is { } span)
            {
                h = h with { CharIndex = span.Start };
                tokenEnd = span.End;
            }
            else if (task.IsFaulted)
            {
                _ = task.Exception; // Older hosts fall back to character scanning.
            }
        }
        var ctx = context?.Invoke(h.Block, h.CharIndex) ?? new ScanContext(h.Block.ScanText, h.CharIndex, config.DocumentTitle);
        frameHit = new PendingHit(h, depth, ctx, false, tokenEnd, forced, keepPrevious);
    }

    /// <summary>Opens a popup for a query from a command or a button, anchored at the mouse.</summary>
    public void OpenQuery(string text, bool kanji = false)
    {
        var block = new RichBlock().Add(text);
        var mouse = ImGui.GetMousePos();
        layers.Clear();
        StartLayer(new PendingHit(new TextHit(block, 0, mouse, mouse + new Vector2(1, 16)), 0, new ScanContext(text, 0, config.DocumentTitle), kanji), text);
    }

    /// <summary>The highlighted range for a block, when it is the source of an open popup.</summary>
    public (int Start, int End)? HighlightFor(RichBlock block, int depth)
    {
        if (depth >= layers.Count)
            return null;
        var layer = layers[depth];
        if (layer.Source != block || !layer.Visible)
            return null;
        return (layer.SourceIndex, layer.SourceIndex + Math.Max(1, layer.MatchLength));
    }

    private bool ModifierHeld()
    {
        var io = ImGui.GetIO();
        return config.ScanModifier switch
        {
            ScanModifier.Shift => io.KeyShift,
            ScanModifier.Ctrl => io.KeyCtrl,
            ScanModifier.Alt => io.KeyAlt,
            _ => true,
        };
    }

    /// <summary>Called once per frame after all scannable windows were drawn.</summary>
    public void Draw()
    {
        Themes.Update();
        var theme = PopupTheme.Current;
        theme.Scale = ImGuiHelpers.GlobalScale * config.PopupFontSize / MathF.Max(8f, theme.BaseFontPx);

        var now = DateTime.UtcNow;
        var mouse = ImGui.GetMousePos();
        if (Vector2.DistanceSquared(mouse, lastMouse) > 4)
        {
            lastMouse = mouse;
            mouseMovedAt = now;
        }

        var hit = frameHit;
        frameHit = null;

        // 1. A hover may start a new lookup at its depth.
        if (hit is not null && hit.Depth <= layers.Count && hit.Depth < config.MaxPopupDepth)
        {
            var text = hit.Hit.Block.ScanText;
            var index = hit.Hit.CharIndex;
            var existing = hit.Depth < layers.Count ? layers[hit.Depth] : null;
            var sameSpot = existing is not null && existing.Source == hit.Hit.Block && existing.SourceIndex == index && existing.IsKanji == hit.Kanji;
            var insideMatch = existing is not null && existing.Source == hit.Hit.Block
                              && index >= existing.SourceIndex && index < existing.SourceIndex + Math.Max(1, existing.MatchLength);
            var scanning = hit.Forced || ModifierHeld();
            if (sameSpot || (insideMatch && !scanning))
            {
                existing!.SustainedAt = now;
                if (sameSpot) existing.MoveAnchor(hit.Hit);
            }
            else if (scanning && index < text.Length)
            {
                var cp = Japanese.CodePointAt(text, index);
                if (!config.ScanOnlyJapanese || Japanese.IsJapanese(cp))
                {
                    var query = Slice(text, index, hit.Kanji ? 1 : config.ScanLength);
                    if (query.Length > 0)
                        StartLayer(hit, query);
                }
                else if (insideMatch)
                {
                    existing!.SustainedAt = now;
                }
            }
            else if (insideMatch)
            {
                existing!.SustainedAt = now;
            }
        }

        // 2. Resolve finished lookups.
        foreach (var layer in layers.ToList())
            layer.Poll(this);
        RemoveDead();

        // 3. Keep layers alive while the mouse is inside them (or a deeper one).
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (layer.Visible && layer.Contains(mouse))
            {
                for (var j = 0; j <= i; j++)
                    layers[j].SustainedAt = now;
                break;
            }
        }

        if (sustainDepth >= 0)
        {
            for (var j = 0; j <= Math.Min(sustainDepth, layers.Count - 1); j++)
                layers[j].SustainedAt = now;
            sustainDepth = -1;
        }

        // Clicking anywhere outside closes everything.
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && layers.Count > 0 && !layers.Any(l => l.Visible && l.Contains(mouse)))
        {
            var onSource = hit is not null && hit.Depth == 0;
            if (!onSource)
                layers.Clear();
        }

        // 4. Hide what was left behind.
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            var autoHide = i > 0 || config.AutoHideRootPopup;
            var idle = (now - layer.SustainedAt).TotalSeconds;
            if (layer.Visible && autoHide && idle > config.HideDelaySeconds)
                layers.RemoveRange(i, layers.Count - i);
            else if (!layer.Visible && idle > 2)
                layers.RemoveRange(i, layers.Count - i);
        }

        // 5. Draw.
        using (fonts.Popup.Push())
        {
            for (var i = 0; i < layers.Count; i++)
            {
                if (layers[i].Visible)
                    DrawLayer(layers[i], i);
            }
        }
    }

    private static string Slice(string text, int index, int codepoints)
    {
        var sb = new System.Text.StringBuilder(Math.Min(codepoints * 2, Math.Max(0, text.Length - index)));
        var end = index;
        var count = 0;
        while (count < codepoints && end < text.Length)
        {
            var c = text[end];
            if (c is '\r' or '\n')
            {
                // Skip single soft/hard line wraps inside a sentence (e.g. "思\n案"),
                // but stop at paragraph breaks (blank lines).
                var next = end + 1;
                if (c == '\r' && next < text.Length && text[next] == '\n') next++;
                if (next < text.Length && text[next] is '\r' or '\n')
                    break;
                end = next;
                continue;
            }
            var step = char.IsHighSurrogate(c) && end + 1 < text.Length ? 2 : 1;
            sb.Append(text, end, step);
            end += step;
            count++;
        }

        return sb.ToString();
    }

    internal static int SourceLengthForMatch(string text, int index, int matchedChars)
    {
        if (matchedChars <= 0 || index >= text.Length)
            return 0;
        var end = index;
        var remaining = matchedChars;
        while (remaining > 0 && end < text.Length)
        {
            var c = text[end];
            if (c is '\r' or '\n')
            {
                var next = end + 1;
                if (c == '\r' && next < text.Length && text[next] == '\n') next++;
                if (next < text.Length && text[next] is '\r' or '\n')
                    break;
                end = next;
                continue;
            }
            end++;
            remaining--;
        }
        return end - index;
    }

    private void StartLayer(PendingHit hit, string query)
    {
        var depth = hit.Depth;
        if (depth < 0 || depth >= config.MaxPopupDepth || depth > layers.Count || string.IsNullOrEmpty(query))
            return;
        if (layers.Count > depth)
        {
            var previous = layers[depth];
            layers.RemoveRange(depth, layers.Count - depth);
            // Keep showing the previous result at this spot until the new one arrives (no flicker).
            var replacement = new PopupLayer(hit, query, hit.Kanji ? service.KanjiAsync(query[..Math.Min(query.Length, char.IsHighSurrogate(query[0]) ? 2 : 1)]) : service.LookupAsync(query))
            {
                Previous = hit.KeepPrevious && previous.Visible ? previous : null,
            };
            layers.Add(replacement);
            return;
        }

        layers.Add(new PopupLayer(hit, query, hit.Kanji ? service.KanjiAsync(query[..Math.Min(query.Length, char.IsHighSurrogate(query[0]) ? 2 : 1)]) : service.LookupAsync(query)));
    }

    private void RemoveDead()
    {
        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i].Dead)
            {
                layers.RemoveRange(i, layers.Count - i);
                return;
            }
        }
    }

    // --------------------------------------------------------------- drawing

    private void DrawLayer(PopupLayer layer, int depth)
    {
        var shown = layer.Showing;
        var viewport = ImGui.GetMainViewport();
        var width = config.PopupWidth * ImGuiHelpers.GlobalScale;
        var maxHeight = config.PopupMaxHeight * ImGuiHelpers.GlobalScale;
        var height = Math.Clamp(layer.ContentHeight > 0 ? layer.ContentHeight : maxHeight, 40f, maxHeight);

        // Below the scanned word, or above it when there is no room; inside the screen.
        var anchorMin = layer.AnchorMin;
        var anchorMax = layer.AnchorMax;
        var pos = new Vector2(anchorMin.X, anchorMax.Y + 4);
        if (pos.Y + height > viewport.Pos.Y + viewport.Size.Y && anchorMin.Y - 4 - height >= viewport.Pos.Y)
            pos.Y = anchorMin.Y - 4 - height;
        pos.X = Math.Clamp(pos.X, viewport.Pos.X, Math.Max(viewport.Pos.X, viewport.Pos.X + viewport.Size.X - width));
        pos.Y = Math.Clamp(pos.Y, viewport.Pos.Y, Math.Max(viewport.Pos.Y, viewport.Pos.Y + viewport.Size.Y - height));

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Always);
        var theme = PopupTheme.Current;
        if (theme.IsPlugin)
            ImGui.SetNextWindowBgAlpha(config.PopupBackgroundAlpha);

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking
                    | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;
        using var window = new PopupWindowScope($"##hachidori-ffxiv-popup-{depth}", flags, layer.FirstFrame);
        if (window.Visible)
        {
            layer.WindowPos = ImGui.GetWindowPos();
            layer.WindowSize = ImGui.GetWindowSize();
            var contentWidth = ImGui.GetContentRegionAvail().X;
            if (layer.ResetScroll)
            {
                ImGui.SetScrollY(0);
                layer.ResetScroll = false;
                layer.SelectedEntry = 0;
            }

            if (layer.PendingScroll != 0)
            {
                ImGui.SetScrollY(Math.Clamp(ImGui.GetScrollY() + layer.PendingScroll, 0, ImGui.GetScrollMaxY()));
                layer.PendingScroll = 0;
            }

            if (shown?.Error is { } error)
            {
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.55f, 1f), "Hachidori");
                ImGui.TextWrapped(error);
            }
            else if (shown?.Reply is { } reply)
            {
                for (var e = 0; e < shown.Entries.Count; e++)
                {
                    if (e > 0)
                        DrawEntrySeparator(theme, contentWidth);

                    var entry = shown.Entries[e];
                    using var entryId = ImRaii.PushId(e);
                    entry.TopY = ImGui.GetCursorPosY();
                    if (ControllerActive && e == layer.SelectedEntry && shown.Entries.Count > 1)
                    {
                        // The controller's selected entry: a bar in the margin.
                        var top = ImGui.GetCursorScreenPos();
                        ImGui.GetWindowDrawList().AddRectFilled(top - new Vector2(ImGui.GetStyle().WindowPadding.X * 0.6f, 0),
                            top - new Vector2(ImGui.GetStyle().WindowPadding.X * 0.6f - 3, -ImGui.GetFontSize() * 2.2f),
                            ImGui.GetColorU32(ImGuiCol.ScrollbarGrabActive), 2f);
                    }

                    if (layer.ScrollToSelected && e == layer.SelectedEntry)
                    {
                        ImGui.SetScrollY(Math.Max(0, entry.TopY - ImGui.GetStyle().WindowPadding.Y));
                        layer.ScrollToSelected = false;
                    }

                    var entryHit = DrawEntry(theme, entry, depth, contentWidth, reply, shown.Context);
                    if (entryHit is { } eh)
                    {
                        // Kanji lookup: hovering the headword while holding Alt (or clicking a kanji) opens kanji info.
                        var kanji = eh.Block == entry.Headword
                                    && Japanese.IsKanji(Japanese.CodePointAt(eh.Block.ScanText, eh.CharIndex))
                                    && (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.GetIO().KeyAlt);
                        if (kanji)
                        {
                            // Resolve on the next frame, just like scans in definitions. Do not
                            // mutate the layer stack while it is being drawn, or restart each frame.
                            frameHit = new PendingHit(eh, depth + 1, new ScanContext(shown.Context.Sentence, shown.Context.MatchOffset, config.DocumentTitle), true);
                        }
                        else if (eh.Block != entry.Headword)
                        {
                            Report(eh, depth + 1, (b, i) => new ScanContext(b.ScanText, i, config.DocumentTitle));
                        }
                        else if (depth + 1 < layers.Count)
                        {
                            layers[depth + 1].SustainedAt = DateTime.UtcNow;
                        }
                    }
                }
            }

            layer.ContentHeight = ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y;
        }

        layer.FirstFrame = false;
    }

    private static void DrawEntrySeparator(PopupTheme theme, float width)
    {
        if (theme.IsPlugin)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            return;
        }

        ImGui.Dummy(new Vector2(1, 4 * theme.Scale));
        if (theme.Separator is { } color)
        {
            var thickness = MathF.Max(1f, theme.SeparatorWidth * theme.Scale);
            var start = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddLine(start, start + new Vector2(width, 0), CssColor.ToAbgr(color), thickness);
            ImGui.Dummy(new Vector2(1, thickness));
        }

        ImGui.Dummy(new Vector2(1, 4 * theme.Scale));
    }

    /// <summary>An entry, inside the theme's entry card when it has one (painted from last frame's height).</summary>
    private TextHit? DrawEntry(PopupTheme theme, EntryView entry, int depth, float width, LookupReply reply, ScanContext context)
    {
        var card = theme.IsPlugin || !theme.Entry.HasPaint ? null : theme.Entry;
        if (card is null)
            return entry.Draw(this, depth + 1, width, service, audio, reply, context);

        var scale = theme.Scale;
        var pad = card.Padding * scale;
        var top = ImGui.GetCursorScreenPos();
        if (entry.CardHeight > 0)
            ThemePainter.Paint(ImGui.GetWindowDrawList(), top, top + new Vector2(width, entry.CardHeight), card, scale);
        ImGui.Dummy(new Vector2(1, MathF.Max(pad.X, 2 * scale)));
        if (pad.W > 0) ImGui.Indent(pad.W);
        var hit = entry.Draw(this, depth + 1, width - pad.W - pad.Y, service, audio, reply, context);
        if (pad.W > 0) ImGui.Unindent(pad.W);
        ImGui.Dummy(new Vector2(1, MathF.Max(pad.Z, 2 * scale)));
        entry.CardHeight = ImGui.GetCursorScreenPos().Y - top.Y;
        return hit;
    }

    // ------------------------------------------------------------- lookups

    internal void OnLoaded(PopupLayer layer, LookupState state)
    {
        if (state.Reply is not { } reply || state.Entries.Count == 0)
            return;
        var first = state.Entries[0];

        if (!reply.IsKanji)
            Stats?.RecordLookup(first.Result);

        // Lookup statistics: Hachidori records the primary result of each executed lookup.
        if (config.ShowLookupCounts && service.OptionBool("showLookupCounts", true) && !reply.IsKanji)
        {
            var term = first.Result.Term;
            _ = service.LookupStatsAsync(term.Expression, term.Reading, record: true).ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully)
                    return;
                first.SetLookupCount(t.Result);
                if (config.HonourDefinitionBlur && service.OptionBool("definitionBlurCountEnabled", false)
                    && t.Result is { } count && count >= service.OptionNumber("definitionBlurThreshold", 5))
                {
                    first.BlurUntil = DateTime.UtcNow.AddMilliseconds(service.OptionNumber("definitionBlurDelayMs", 3000));
                }
            }, TaskScheduler.Default);
        }

        if (config.AutoPlayAudio && !reply.IsKanji)
            audio.PlayTerm(service, first.Result.Term.Expression, first.Result.Term.Reading);

        // Every visible result needs a working mining button, including later results.
        foreach (var entry in state.Entries)
        {
            entry.OnAnkiClicked += (e, force) => _ = MineAsync(state, e, force);
            _ = CheckAnkiAsync(state, entry);
        }
    }

    private Task<HachidoriService.AnkiStatus> AnkiStatus()
    {
        if (ankiStatus is null || (DateTime.UtcNow - ankiStatusAt).TotalSeconds > 30 || ankiStatus.IsFaulted)
        {
            ankiStatus = service.AnkiStatusAsync();
            ankiStatusAt = DateTime.UtcNow;
        }

        return ankiStatus;
    }

    private async Task CheckAnkiAsync(LookupState state, EntryView entry)
    {
        try
        {
            entry.Anki = AnkiState.Checking;
            var status = await AnkiStatus().ConfigureAwait(false);
            if (!status.Available)
            {
                entry.Anki = AnkiState.Error;
                entry.AnkiMessage = status.Error ?? "Anki mining is unavailable in Hachidori.";
                return;
            }

            entry.MiningRequest = service.BuildMiningRequest(state.Reply!, entry.Result, state.Context, status.ConfigKey);
            var decision = await service.AnkiPreflightAsync(entry.MiningRequest).ConfigureAwait(false);
            entry.Decision = decision;
            entry.Anki = decision.State switch
            {
                "duplicate" when !decision.CanAdd => AnkiState.Duplicate,
                "duplicate" => AnkiState.Duplicate,
                "invalid" or "error" => AnkiState.Error,
                _ => decision.CanAdd ? AnkiState.Ready : AnkiState.Error,
            };
            if (entry.Anki == AnkiState.Error)
                entry.AnkiMessage = decision.Error ?? "Anki rejected this note.";
        }
        catch (Exception ex)
        {
            entry.Anki = AnkiState.Error;
            entry.AnkiMessage = ex.Message;
        }
    }

    private async Task MineAsync(LookupState state, EntryView entry, bool force)
    {
        try
        {
            if (entry.Anki is AnkiState.Added || (entry.Anki is AnkiState.Duplicate && !force))
            {
                var ids = entry.Decision?.NoteIds ?? [];
                var status = await AnkiStatus().ConfigureAwait(false);
                await service.AnkiBrowseAsync(ids, entry.Result.Term.Expression, status.ConfigKey).ConfigureAwait(false);
                return;
            }

            if (entry.Anki is AnkiState.Error)
            {
                ankiStatusAt = DateTime.MinValue;
                await CheckAnkiAsync(state, entry).ConfigureAwait(false);
                return;
            }

            entry.Anki = AnkiState.Busy;
            entry.AnkiMessage = "Saving to Anki…";
            var request = entry.MiningRequest ?? service.BuildMiningRequest(state.Reply!, entry.Result, state.Context, (await AnkiStatus().ConfigureAwait(false)).ConfigKey);
            request = (System.Text.Json.Nodes.JsonObject)request.DeepClone();
            if (force)
                request["allowDuplicate"] = true;
            if (entry.Decision?.ClientSpeech is { } speech)
                request["clientSpeech"] = speech.DeepClone();
            // The Template maps {screenshot}: take the game picture now, without plugin windows.
            Media.ScreenshotMedia? screenshot = null;
            string? screenshotWarning = null;
            if (Media is not null && config.CaptureScreenshot && entry.Decision?.Screenshot == true)
            {
                try { screenshot = await Media.ScreenshotAsync().ConfigureAwait(false); }
                catch (Exception ex) { screenshotWarning = $" Screenshot skipped: {ex.Message}"; }
            }

            var result = await service.AnkiSubmitAsync(request, screenshot).ConfigureAwait(false);
            if (result.State is "added" or "updated")
            {
                entry.Decision = result;
                var noteId = result.NoteIds.FirstOrDefault();
                var warnings = result.Warnings.Length > 0 ? " " + string.Join(" ", result.Warnings) : string.Empty;
                var added = $"{(result.State == "added" ? "Added" : "Updated")} note {noteId}.{screenshotWarning}{warnings}";
                var attachedText = string.Empty;
                if (Media is { AttachesMedia: true } media && noteId != 0)
                {
                    entry.AnkiMessage = $"{added} Adding media…";
                    try
                    {
                        var attached = await media.AttachAsync(noteId, state.Context.Sentence, screenshot?.Filename).ConfigureAwait(false);
                        attachedText = attached;
                        entry.AnkiMessage = attached.Length > 0 ? $"{added} {attached}" : added;
                        DiagnosticLog.Write("anki", $"Media attach for note {noteId}: {(attached.Length > 0 ? attached : "(nothing added)")}");
                    }
                    catch (Exception ex)
                    {
                        entry.AnkiMessage = $"{added} Media not attached: {ex.Message}";
                        DiagnosticLog.Write("anki", $"Media attach failed for note {noteId}: {ex}");
                    }
                }
                else
                {
                    entry.AnkiMessage = added;
                }
                entry.Anki = AnkiState.Added;
                RecordMinedStats(noteId, entry.Result, state.Context, screenshot is not null, attachedText);
            }
            else if (result.State == "duplicate")
            {
                entry.Anki = AnkiState.Duplicate;
                entry.Decision = result;
                entry.AnkiMessage = "Already in Anki.";
            }
            else
            {
                entry.Anki = AnkiState.Error;
                entry.AnkiMessage = result.Error ?? $"Anki answered {result.State}.";
            }
        }
        catch (Exception ex)
        {
            entry.Anki = AnkiState.Error;
            entry.AnkiMessage = ex.Message;
        }
    }

    private void RecordMinedStats(long noteId, LookupResult result, ScanContext context, bool hadScreenshot, string attachedSummary)
    {
        if (Stats is null)
            return;
        var speaker = string.Empty;
        var sep = context.DocumentTitle.IndexOf(" — ", StringComparison.Ordinal);
        if (sep >= 0 && sep + 3 < context.DocumentTitle.Length)
            speaker = context.DocumentTitle[(sep + 3)..].Trim();
        else if (Media?.Timeline.Find(context.Sentence) is { Line.Speaker.Length: > 0 } span)
            speaker = span.Line.Speaker;

        var hasAudio = attachedSummary.Contains("voice line", StringComparison.OrdinalIgnoreCase)
                       || attachedSummary.Contains("TTS audio", StringComparison.OrdinalIgnoreCase);
        var hasAnim = attachedSummary.Contains("AVIF", StringComparison.OrdinalIgnoreCase)
                      || attachedSummary.Contains("WEBP", StringComparison.OrdinalIgnoreCase)
                      || attachedSummary.Contains("GIF", StringComparison.OrdinalIgnoreCase);
        var hasTl = attachedSummary.Contains("translation", StringComparison.OrdinalIgnoreCase);
        Stats.RecordMinedCard(noteId, result, context.Sentence, speaker, hadScreenshot, hasAudio, hasAnim, hasTl);
    }

    // ---------------------------------------------------------------- layer

    internal sealed class LookupState
    {
        public LookupReply? Reply;
        public string? Error;
        public List<EntryView> Entries = new();
        public required ScanContext Context;
    }

    internal sealed class PopupLayer
    {
        private readonly Task<LookupReply> task;
        private readonly LookupState state;
        private bool polled;

        public PopupLayer(PendingHit hit, string query, Task<LookupReply> task)
        {
            this.task = task;
            Source = hit.Hit.Block;
            SourceIndex = hit.Hit.CharIndex;
            TokenLength = hit.TokenEnd - hit.Hit.CharIndex;
            AnchorMin = hit.Hit.Min;
            AnchorMax = hit.Hit.Max;
            Query = query;
            IsKanji = hit.Kanji;
            SustainedAt = DateTime.UtcNow;
            state = new LookupState { Context = hit.Context with { MatchOffset = hit.Context.MatchOffset } };
        }

        public RichBlock Source { get; }
        public int SourceIndex { get; }
        private int? TokenLength { get; }
        public string Query { get; }
        public bool IsKanji { get; }
        public Vector2 AnchorMin { get; private set; }
        public Vector2 AnchorMax { get; private set; }
        public void MoveAnchor(TextHit hit)
        {
            AnchorMin = hit.Min;
            AnchorMax = hit.Max;
        }
        public DateTime SustainedAt { get; set; }
        public PopupLayer? Previous { get; set; }
        public bool Dead { get; private set; }
        public bool FirstFrame { get; set; } = true;
        // Every new lookup starts at its first result: the popup's ImGui window, and so its
        // scroll position, is shared by every lookup shown at this depth.
        public bool ResetScroll { get; set; } = true;
        public float ContentHeight { get; set; }
        public Vector2 WindowPos { get; set; }
        public Vector2 WindowSize { get; set; }
        public int SelectedEntry { get; set; }
        public bool ScrollToSelected { get; set; }
        public float PendingScroll { get; set; }

        /// <summary>
        /// The highlighted length in the source text. Hachidori measures the match as
        /// <c>result.matched || result.term.expression</c>; the engine may leave <c>matched</c>
        /// empty, which used to collapse the highlight to the first character. A word span from
        /// tokenized hover only ever extends the match, never shortens it.
        /// </summary>
        public int MatchLength
        {
            get
            {
                if (!polled)
                    return Math.Max(TokenLength ?? 0, Previous?.MatchLength ?? 1);
                var first = state.Reply?.Results.FirstOrDefault();
                var matched = first is null ? 0
                    : first.Matched.Length > 0 ? first.Matched.Length
                    : first.Term.Expression.Length;
                var sourceMatched = SourceLengthForMatch(Source.ScanText, SourceIndex, matched);
                var available = Math.Max(1, Source.ScanText.Length - SourceIndex);
                return Math.Clamp(Math.Max(TokenLength ?? 0, sourceMatched), 1, available);
            }
        }

        public bool Visible => polled ? !Dead : Previous is not null;

        public LookupState? Showing => polled ? state : Previous?.Showing;

        public bool Contains(Vector2 point) =>
            point.X >= WindowPos.X && point.Y >= WindowPos.Y
                                   && point.X <= WindowPos.X + WindowSize.X && point.Y <= WindowPos.Y + WindowSize.Y;

        public void Poll(PopupManager manager)
        {
            if (polled || !task.IsCompleted)
                return;
            polled = true;
            if (Previous is not null)
            {
                // Inherit the window geometry so the swap does not flash.
                WindowPos = Previous.WindowPos;
                WindowSize = Previous.WindowSize;
                FirstFrame = false;
                ResetScroll = true;
                Previous = null;
            }

            if (task.IsFaulted)
            {
                state.Error = task.Exception?.GetBaseException().Message ?? "Lookup failed.";
                Plugin.Log.Debug($"Lookup failed: {state.Error}");
                return;
            }

            if (task.IsCanceled)
            {
                Dead = true;
                return;
            }

            var reply = task.Result;
            if (reply.Results.Count == 0)
            {
                Dead = true;
                return;
            }

            state.Reply = reply;
            state.Entries = reply.Results.Select(r => new EntryView(r, manager.config, reply.IsKanji)).ToList();
            manager.OnLoaded(this, state);
        }
    }
}
