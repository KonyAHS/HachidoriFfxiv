using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Ui;

/// <summary>One cleanup per push/Begin; also restores ImGui when entry drawing throws.</summary>
internal sealed class PopupWindowScope : IDisposable
{
    private readonly Stack<Action> cleanup = new();

    public PopupWindowScope(string name, ImGuiWindowFlags flags, bool firstFrame)
    {
        try
        {
            Style(ImGuiStyleVar.WindowRounding, 8f);
            Style(ImGuiStyleVar.WindowBorderSize, 1f);
            ImGui.PushStyleColor(ImGuiCol.Border, Colors.Accent);
            cleanup.Push(() => ImGui.PopStyleColor());
            Style(ImGuiStyleVar.Alpha, firstFrame ? 0f : 1f);
            Visible = ImGui.Begin(name, flags);
            cleanup.Push(ImGui.End);
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
