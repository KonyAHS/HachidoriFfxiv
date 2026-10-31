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
        service.Link.WordStatusChanged += () => ankiStatusAt = DateTime.MinValue;
    }

    public bool AnyOpen => layers.Any(l => l.Visible);
    public DictionaryImages Images { get; }
    public NpcImageCapture NpcImages { get; }
    public bool ContainsMouse(Vector2 mouse) => layers.Any(l => l.Visible && l.Contains(mouse));

    public void CloseAll() => layers.Clear();
    public void Dispose()
    {
        CloseAll();
        Images.Dispose();
        NpcImages.Dispose();
    }

    internal sealed record PendingHit(TextHit Hit, int Depth, ScanContext Context, bool Kanji, int? TokenEnd = null);

    /// <summary>A text area reports the hovered character. Depth is 0 outside popups, N+1 inside popup N.</summary>
    public void Report(TextHit? hit, int depth, Func<RichBlock, int, ScanContext>? context = null)
    {
        if (hit is not { } h)
            return;
        int? tokenEnd = null;
        if (config.TokenizedHover)
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
        frameHit = new PendingHit(h, depth, ctx, false, tokenEnd);
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
        var now = DateTime.UtcNow;
        var mouse = ImGui.GetMousePos();
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
            if (sameSpot || (insideMatch && !ModifierHeld()))
            {
                existing!.SustainedAt = now;
                if (sameSpot) existing.MoveAnchor(hit.Hit);
            }
            else if (ModifierHeld() && index < text.Length)
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
        var end = index;
        for (var n = 0; n < codepoints && end < text.Length; n++)
        {
            if (text[end] == '\n')
                break;
            end += char.IsHighSurrogate(text[end]) && end + 1 < text.Length ? 2 : 1;
        }

        return text[index..end];
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
                Previous = previous.Visible ? previous : null,
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
                    {
                        ImGui.Spacing();
                        ImGui.Separator();
                        ImGui.Spacing();
                    }

                    var entry = shown.Entries[e];
                    using var entryId = ImRaii.PushId(e);
                    var entryHit = entry.Draw(this, depth + 1, contentWidth, service, audio, reply, shown.Context);
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

    // ------------------------------------------------------------- lookups

    internal void OnLoaded(PopupLayer layer, LookupState state)
    {
        if (state.Reply is not { } reply || state.Entries.Count == 0)
            return;
        var first = state.Entries[0];

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
            var result = await service.AnkiSubmitAsync(request).ConfigureAwait(false);
            if (result.State is "added" or "updated")
            {
                entry.Anki = AnkiState.Added;
                entry.Decision = result;
                entry.AnkiMessage = $"{(result.State == "added" ? "Added" : "Updated")} note {result.NoteIds.FirstOrDefault()}.";
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
        public bool ResetScroll { get; set; }
        public float ContentHeight { get; set; }
        public Vector2 WindowPos { get; set; }
        public Vector2 WindowSize { get; set; }

        public int MatchLength => TokenLength ?? (polled
            ? state.Reply?.Results.FirstOrDefault()?.Matched.Length ?? 1
            : Previous?.MatchLength ?? 1);

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
