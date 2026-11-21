using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using HachidoriFfxiv.Rendering;
using HachidoriFfxiv.Rendering.Css;

namespace HachidoriFfxiv.Ui;

/// <summary>One cleanup per push/Begin; also restores ImGui when entry drawing throws.</summary>
internal sealed class PopupWindowScope : IDisposable
{
    private readonly Stack<Action> cleanup = new();

    public PopupWindowScope(string name, ImGuiWindowFlags flags, bool firstFrame)
    {
        try
        {
            var theme = PopupTheme.Current;
            ThemePainter.Push(theme, cleanup);
            Style(ImGuiStyleVar.Alpha, firstFrame ? 0f : 1f);
            Visible = ImGui.Begin(name, flags);
            cleanup.Push(ImGui.End);
            if (Visible)
            {
                ThemePainter.DecorateWindow(theme, ImGui.GetWindowPos(), ImGui.GetWindowPos() + ImGui.GetWindowSize());
                ThemePainter.PushInner(theme, cleanup);
                var previousLink = RichText.LinkColor;
                RichText.LinkColor = theme.IsPlugin ? RichText.DefaultLinkColor : CssColor.ToAbgr(theme.Link);
                cleanup.Push(() => RichText.LinkColor = previousLink);
                // Dictionary colours must stay readable on this theme's background.
                var previousContrast = RichText.Contrast;
                var background = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg] with { W = 1 };
                RichText.SetContrast(CssColor.ToAbgr(background), ImGui.GetColorU32(ImGuiCol.Text) | 0xFF000000);
                cleanup.Push(() => RichText.SetContrast(previousContrast.Background, previousContrast.Text));
            }
            // Draw order alone does not change ImGui's persistent window order.
            // Keep definitions over the reader without stealing keyboard focus.
            unsafe { ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow()); }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool Visible { get; }

    private void Style(ImGuiStyleVar variable, float value)
    {
        ImGui.PushStyleVar(variable, value);
        cleanup.Push(() => ImGui.PopStyleVar());
    }

    public void Dispose()
    {
        while (cleanup.TryPop(out var restore))
            restore();
    }
}
