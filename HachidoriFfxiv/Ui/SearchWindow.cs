using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Ui;

/// <summary>Type or paste Japanese and hover it, like Yomitan's search page.</summary>
public sealed class SearchWindow : Window
{
    internal const string SampleLine = "冒険者よ、エオルゼアの危機を救うため力を貸してほしい！";

    private readonly Configuration config;
    private readonly PopupManager popups;
    private readonly Fonts fonts;
    private string input = string.Empty;
    private RichBlock block = new();

    public SearchWindow(Configuration config, PopupManager popups, Fonts fonts)
        : base("Hachidori Search##hachidori-ffxiv-search")
    {
        this.config = config;
        this.popups = popups;
        this.fonts = fonts;
        Size = new Vector2(540, 240);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void SetText(string text)
    {
        input = text;
        block = new RichBlock().Add(text);
        IsOpen = true;
    }

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var clearW = input.Length > 0 ? 58 * scale : 0f;
        using (fonts.Reader.Push())
        {
            ImGui.SetNextItemWidth(clearW > 0 ? MathF.Max(120 * scale, ImGui.GetContentRegionAvail().X - clearW - 6 * scale) : -1);
            if (ImGui.InputTextWithHint("##query", "検索… (type or paste Japanese)", ref input, 2048))
                block = new RichBlock().Add(input);
        }

        if (clearW > 0)
        {
            ImGui.SameLine(0, 6 * scale);
            if (ImGui.Button("Clear", new Vector2(clearW, 0)))
            {
                input = string.Empty;
                block = new RichBlock();
            }
        }

        ImGui.Spacing();
        if (input.Length == 0)
        {
            ImGui.TextDisabled("Type or paste Japanese above, then hover any word to open the popup dictionary.");
            if (ImGui.SmallButton("Load sample FFXIV sentence"))
                SetText(SampleLine);
            return;
        }

        using (fonts.Reader.Push())
        {
            var hit = RichText.Draw(block, ImGui.GetContentRegionAvail().X, popups.HighlightFor(block, 0));
            popups.Report(hit, 0, (b, i) => new ScanContext(input, i, config.DocumentTitle));
        }
    }
}
