using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using HachidoriFfxiv.Hachidori;
using HachidoriFfxiv.Rendering;

namespace HachidoriFfxiv.Ui;

/// <summary>Type or paste Japanese and hover it, like Yomitan's search page.</summary>
public sealed class SearchWindow : Window
{
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
        Size = new Vector2(520, 220);
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
        using var font = fonts.Reader.Push();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##query", "検索…", ref input, 2048))
            block = new RichBlock().Add(input);
        ImGui.Spacing();
        var hit = RichText.Draw(block, ImGui.GetContentRegionAvail().X, popups.HighlightFor(block, 0));
        popups.Report(hit, 0, (b, i) => new ScanContext(input, i, config.DocumentTitle));
    }
}
