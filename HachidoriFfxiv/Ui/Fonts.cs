using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Ui;

/// <summary>
/// Japanese fonts (Dalamud's Noto Sans CJK, Japanese face) rasterised at each size the popup and reader draw.
/// The default font is usually AXIS, whose fixed game textures are resampled and blur at most sizes.
/// </summary>
public sealed class Fonts : IDisposable
{
    // Body, furigana and chips; the popup adds its headword size.
    private static readonly float[] PopupScales = [1f, 0.5f, 0.75f, 1.7f];
    private static readonly float[] ReaderScales = [1f, 0.5f, 0.75f];

    private readonly Configuration config;
    private float popupSize;
    private float readerSize;
    private (float Scale, IFontHandle Handle)[] popupFonts;
    private (float Scale, IFontHandle Handle)[] readerFonts;

    public Fonts(Configuration config)
    {
        this.config = config;
        popupFonts = CreateAll(popupSize = config.PopupFontSize, PopupScales);
        readerFonts = CreateAll(readerSize = config.ReaderFontSize, ReaderScales);
    }

    public IFontHandle Popup => popupFonts[0].Handle;
    public IFontHandle Reader => readerFonts[0].Handle;

    /// <summary>Rebuilds the handles when the configured sizes change.</summary>
    public void Refresh()
    {
        if (Math.Abs(popupSize - config.PopupFontSize) > 0.01f)
        {
            DisposeAll(popupFonts);
            popupFonts = CreateAll(popupSize = config.PopupFontSize, PopupScales);
        }

        if (Math.Abs(readerSize - config.ReaderFontSize) > 0.01f)
        {
            DisposeAll(readerFonts);
            readerFonts = CreateAll(readerSize = config.ReaderFontSize, ReaderScales);
        }
    }

    /// <summary>Publishes this frame's sized fonts to <see cref="RichText"/> and keeps them alive until disposed.</summary>
    public IDisposable LockFrame()
    {
        var locks = new List<ILockedImFont>();
        var ladders = new Dictionary<nint, SizedFontRung[]>();
        AddLadder(popupFonts, ladders, locks);
        AddLadder(readerFonts, ladders, locks);
        RichText.SetFontLadders(ladders);
        return new FrameLocks(locks);
    }

    // A size that is still building leaves its whole ladder out, so text scales the current font until it is ready.
    private static void AddLadder((float Scale, IFontHandle Handle)[] fonts, Dictionary<nint, SizedFontRung[]> ladders, List<ILockedImFont> locks)
    {
        var rungs = new SizedFontRung[fonts.Length];
        var start = locks.Count;
        for (var i = 0; i < fonts.Length; i++)
        {
            if (fonts[i].Handle.TryLock(out _) is not { } locked)
            {
                for (var j = locks.Count - 1; j >= start; j--)
                {
                    locks[j].Dispose();
                    locks.RemoveAt(j);
                }

                return;
            }

            locks.Add(locked);
            rungs[i] = new SizedFontRung(locked.ImFont, fonts[i].Scale);
        }

        unsafe { ladders[(nint)rungs[0].Font.Handle] = rungs; }
    }

    private static (float Scale, IFontHandle Handle)[] CreateAll(float sizePx, float[] scales) =>
        scales.Select(scale => (scale, Create(sizePx * scale))).ToArray();

    private static IFontHandle Create(float sizePx) =>
        Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var font = tk.AddDalamudAssetFont(DalamudAsset.NotoSansCjkMedium, new SafeFontConfig
            {
                SizePx = sizePx,
                FontNo = 0, // The collection's Japanese face.
                GlyphRanges = GlyphRangesJapanese.GlyphRanges,
            });
            tk.AddGameSymbol(new SafeFontConfig { SizePx = sizePx, MergeFont = font });
            tk.AttachExtraGlyphsForDalamudLanguage(new SafeFontConfig { SizePx = sizePx, MergeFont = font });
            tk.Font = font;
        }));

    private static void DisposeAll((float Scale, IFontHandle Handle)[] fonts)
    {
        foreach (var (_, handle) in fonts)
            handle.Dispose();
    }

    public void Dispose()
    {
        DisposeAll(popupFonts);
        DisposeAll(readerFonts);
    }

    private sealed class FrameLocks(List<ILockedImFont> locks) : IDisposable
    {
        public void Dispose()
        {
            foreach (var locked in locks)
                locked.Dispose();
        }
    }
}
