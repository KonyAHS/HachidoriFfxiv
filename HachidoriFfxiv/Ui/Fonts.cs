using System;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace HachidoriFfxiv.Ui;

/// <summary>Japanese-capable fonts (Dalamud's Noto Sans JP) at the popup and reader sizes.</summary>
public sealed class Fonts : IDisposable
{
    private readonly Configuration config;
    private float popupSize;
    private float readerSize;

    public Fonts(Configuration config)
    {
        this.config = config;
        Popup = Create(popupSize = config.PopupFontSize);
        Reader = Create(readerSize = config.ReaderFontSize);
    }

    public IFontHandle Popup { get; private set; }
    public IFontHandle Reader { get; private set; }

    /// <summary>Rebuilds the handles when the configured sizes change.</summary>
    public void Refresh()
    {
        if (Math.Abs(popupSize - config.PopupFontSize) > 0.01f)
        {
            Popup.Dispose();
            Popup = Create(popupSize = config.PopupFontSize);
        }

        if (Math.Abs(readerSize - config.ReaderFontSize) > 0.01f)
        {
            Reader.Dispose();
            Reader = Create(readerSize = config.ReaderFontSize);
        }
    }

    private static IFontHandle Create(float sizePx) =>
        Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var font = tk.AddDalamudDefaultFont(sizePx, GlyphRangesJapanese.GlyphRanges);
            tk.Font = font;
        }));

    public void Dispose()
    {
        Popup.Dispose();
        Reader.Dispose();
    }
}
