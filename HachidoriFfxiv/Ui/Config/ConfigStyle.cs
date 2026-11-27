using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace HachidoriFfxiv.Ui;

/// <summary>
/// Unified design tokens and collapsible card primitives matching <see cref="StatsWindow"/>'s
/// restrained 3-color palette: Primary (soft blue), Secondary (lavender), Emphasis (gold),
/// over native Dalamud <see cref="ImGuiCol.FrameBg"/> card surfaces.
/// </summary>
internal static class ConfigStyle
{
    // Exact 3-color palette from StatsWindow: 2 main accents + 1 emphasis color.
    public static readonly Vector4 Primary = new(0.47f, 0.66f, 0.92f, 1f);    // soft steel blue
    public static readonly Vector4 Secondary = new(0.70f, 0.62f, 0.94f, 1f);  // lavender
    public static readonly Vector4 Emphasis = new(0.93f, 0.74f, 0.38f, 1f);   // gold

    // Subtle semantic colors used strictly for tiny status dots and error messages.
    public static readonly Vector4 Good = new(0.45f, 0.78f, 0.55f, 1f);
    public static readonly Vector4 Bad = new(0.92f, 0.50f, 0.50f, 1f);
    public static readonly Vector4 Muted = new(0.68f, 0.70f, 0.76f, 1f);

    private static readonly Dictionary<string, bool> CardOpenState = new(StringComparer.Ordinal);

    /// <summary>Draws a crisp status pill inline (e.g. READY, BALANCED).</summary>
    public static void StatusPill(string text, Vector4 accent)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(text);
        var padX = 7f * scale;
        var padY = 2f * scale;
        var size = new Vector2(textSize.X + padX * 2, textSize.Y + padY * 2);
        var bg = accent with { W = 0.16f };
        var border = accent with { W = 0.50f };
        draw.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(bg), 4f * scale);
        draw.AddRect(pos, pos + size, ImGui.ColorConvertFloat4ToU32(border), 4f * scale);
        draw.AddText(pos + new Vector2(padX, padY), ImGui.ColorConvertFloat4ToU32(accent), text);
        ImGui.Dummy(size);
    }

    /// <summary>
    /// Draws a collapsible card panel matching StatsWindow's subtle rounded FrameBg aesthetic.
    /// Click anywhere on the header bar to collapse/expand. Always dispose the returned <see cref="CardScope"/>.
    /// </summary>
    public static CardScope Card(
        string id,
        string title,
        Vector4? accent = null,
        string? badge = null,
        Vector4? badgeColor = null,
        string? subtitle = null,
        bool defaultOpen = true)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var headerAccent = accent ?? Primary;
        if (!CardOpenState.TryGetValue(id, out var isOpen))
            CardOpenState[id] = isOpen = defaultOpen;

        ImGui.Spacing();
        var startPos = ImGui.GetCursorScreenPos();
        var availW = Math.Max(140f * scale, ImGui.GetContentRegionAvail().X);
        var headerH = Math.Max(28f * scale, ImGui.GetTextLineHeight() + 12f * scale);

        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);

        ImGui.BeginGroup();
        ImGui.PushID(id);

        // Clickable full-width header strip for collapsing/expanding
        ImGui.SetCursorScreenPos(startPos);
        if (ImGui.InvisibleButton("##card-hdr", new Vector2(availW, headerH)))
        {
            isOpen = !isOpen;
            CardOpenState[id] = isOpen;
        }
        var headerHovered = ImGui.IsItemHovered();

        // Chevron triangle
        var chevCenter = startPos + new Vector2(14f * scale, headerH * 0.5f);
        var chevR = 4f * scale;
        var chevCol = ImGui.ColorConvertFloat4ToU32(headerHovered ? Emphasis : headerAccent);
        if (isOpen)
        {
            draw.AddTriangleFilled(
                chevCenter + new Vector2(-chevR, -chevR * 0.6f),
                chevCenter + new Vector2(chevR, -chevR * 0.6f),
                chevCenter + new Vector2(0, chevR * 0.85f),
                chevCol);
        }
        else
        {
            draw.AddTriangleFilled(
                chevCenter + new Vector2(-chevR * 0.6f, -chevR),
                chevCenter + new Vector2(chevR * 0.85f, 0),
                chevCenter + new Vector2(-chevR * 0.6f, chevR),
                chevCol);
        }

        // Title text
        var titlePos = startPos + new Vector2(26f * scale, (headerH - ImGui.GetTextLineHeight()) * 0.5f);
        draw.AddText(titlePos, ImGui.ColorConvertFloat4ToU32(headerHovered ? Emphasis : headerAccent), title);

        // Optional right-aligned badge pill
        if (!string.IsNullOrEmpty(badge))
        {
            var badgeAccent = badgeColor ?? headerAccent;
            var textSize = ImGui.CalcTextSize(badge);
            var padX = 7f * scale;
            var padY = 2f * scale;
            var pillSize = new Vector2(textSize.X + padX * 2, textSize.Y + padY * 2);
            var pillPos = new Vector2(startPos.X + availW - 10f * scale - pillSize.X, startPos.Y + (headerH - pillSize.Y) * 0.5f);
            draw.AddRectFilled(pillPos, pillPos + pillSize, ImGui.ColorConvertFloat4ToU32(badgeAccent with { W = 0.16f }), 4f * scale);
            draw.AddRect(pillPos, pillPos + pillSize, ImGui.ColorConvertFloat4ToU32(badgeAccent with { W = 0.45f }), 4f * scale);
            draw.AddText(pillPos + new Vector2(padX, padY), ImGui.ColorConvertFloat4ToU32(badgeAccent), badge);
        }

        if (isOpen)
        {
            // Subtle rule below header
            var ruleY = startPos.Y + headerH;
            draw.AddLine(
                new Vector2(startPos.X + 10f * scale, ruleY),
                new Vector2(startPos.X + availW - 10f * scale, ruleY),
                ImGui.GetColorU32(ImGuiCol.Separator));

            ImGui.SetCursorScreenPos(new Vector2(startPos.X + 12f * scale, ruleY + 8f * scale));
            ImGui.PushTextWrapPos(startPos.X + availW - 12f * scale);

            if (!string.IsNullOrEmpty(subtitle))
            {
                ImGui.TextColored(Muted, subtitle);
                ImGui.Dummy(new Vector2(0, 3f * scale));
                ImGui.SetCursorScreenPos(new Vector2(startPos.X + 12f * scale, ImGui.GetCursorScreenPos().Y));
            }

            ImGui.BeginGroup();
        }

        return new CardScope(draw, startPos, availW, headerH, headerAccent, headerHovered, isOpen, scale);
    }

    public readonly struct CardScope(
        ImDrawListPtr draw,
        Vector2 startPos,
        float width,
        float headerH,
        Vector4 accent,
        bool headerHovered,
        bool isOpen,
        float scale) : IDisposable
    {
        public bool IsOpen => isOpen;

        public void Dispose()
        {
            if (isOpen)
            {
                ImGui.EndGroup();
                ImGui.PopTextWrapPos();
                ImGui.Dummy(new Vector2(0, 8f * scale));
            }

            ImGui.PopID();
            ImGui.EndGroup();

            var endY = isOpen
                ? Math.Max(startPos.Y + headerH + 12f * scale, ImGui.GetItemRectMax().Y + 2f * scale)
                : startPos.Y + headerH;
            var maxPos = new Vector2(startPos.X + width, endY);
            var rounding = 5f * scale;

            draw.ChannelsSetCurrent(0);
            var bgCol = ImGui.GetColorU32(ImGuiCol.FrameBg);
            draw.AddRectFilled(startPos, maxPos, bgCol, rounding);

            if (headerHovered)
            {
                draw.AddRectFilled(
                    startPos,
                    new Vector2(maxPos.X, startPos.Y + headerH),
                    ImGui.ColorConvertFloat4ToU32(accent with { W = 0.08f }),
                    rounding,
                    isOpen ? ImDrawFlags.RoundCornersTop : ImDrawFlags.RoundCornersAll);
            }

            var borderCol = isOpen
                ? ImGui.GetColorU32(ImGuiCol.Separator)
                : ImGui.ColorConvertFloat4ToU32(accent with { W = 0.25f });
            draw.AddRect(startPos, maxPos, borderCol, rounding);
            draw.ChannelsMerge();

            ImGui.SetCursorScreenPos(new Vector2(startPos.X, endY + 2f * scale));
            ImGui.Dummy(Vector2.Zero);
        }
    }

    /// <summary>
    /// Selectable preset tile with automatic height measurement so multi-line text never clips
    /// at any DPI or font size. Selected state uses Gold (<see cref="Emphasis"/>) like StatsWindow.
    /// </summary>
    public static bool PresetTile(string id, string title, string subtitle, bool selected, float width, float minHeight = 0f)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var padX = 10f * scale;
        var padY = 9f * scale;
        var innerW = Math.Max(40f * scale, width - padX * 2f);
        var titleH = ImGui.GetTextLineHeight();
        var subSize = ImGui.CalcTextSize(subtitle, false, innerW);
        var neededH = padY + titleH + 5f * scale + subSize.Y + padY;
        var height = Math.Max(minHeight > 0 ? minHeight : 72f * scale, neededH);

        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();

        var draw = ImGui.GetWindowDrawList();
        var rounding = 5f * scale;
        var max = pos + new Vector2(width, height);

        var bg = selected
            ? Emphasis with { W = 0.16f }
            : hovered
                ? Primary with { W = 0.14f }
                : new Vector4(0.08f, 0.09f, 0.12f, 0.45f);
        var border = selected
            ? Emphasis with { W = 0.80f }
            : hovered
                ? Primary with { W = 0.60f }
                : new Vector4(0.35f, 0.37f, 0.44f, 0.45f);

        draw.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(bg), rounding);
        draw.AddRect(pos, max, ImGui.ColorConvertFloat4ToU32(border), rounding, ImDrawFlags.None, selected ? 1.6f : 1f);

        var titleCol = selected ? Emphasis : hovered ? Primary : new Vector4(0.95f, 0.96f, 0.98f, 1f);
        draw.AddText(pos + new Vector2(padX, padY), ImGui.ColorConvertFloat4ToU32(titleCol), title);

        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        draw.AddText(
            font,
            fontSize,
            pos + new Vector2(padX, padY + titleH + 5f * scale),
            ImGui.ColorConvertFloat4ToU32(Muted),
            subtitle,
            innerW);

        return clicked;
    }

    /// <summary>Computes a uniform row height for a row of <see cref="PresetTile"/>s so all tiles align cleanly.</summary>
    public static float MeasurePresetRowHeight(float tileWidth, params string[] subtitles)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var padX = 10f * scale;
        var padY = 9f * scale;
        var innerW = Math.Max(40f * scale, tileWidth - padX * 2f);
        var titleH = ImGui.GetTextLineHeight();
        var maxH = 68f * scale;
        foreach (var sub in subtitles)
        {
            var subH = ImGui.CalcTextSize(sub, false, innerW).Y;
            maxH = Math.Max(maxH, padY + titleH + 5f * scale + subH + padY);
        }
        return maxH;
    }

    /// <summary>StatsWindow-style subtle section heading with horizontal rule.</summary>
    public static void SubHeading(string text, Vector4? color = null)
    {
        ImGui.Dummy(new Vector2(0, 4f * ImGuiHelpers.GlobalScale));
        ImGui.TextColored(color ?? Secondary, text);
        ImGui.SameLine();
        var pos = ImGui.GetCursorScreenPos();
        var y = pos.Y + ImGui.GetTextLineHeight() * 0.5f;
        var right = pos.X + ImGui.GetContentRegionAvail().X - 4f * ImGuiHelpers.GlobalScale;
        if (right > pos.X)
            ImGui.GetWindowDrawList().AddLine(new Vector2(pos.X, y), new Vector2(right, y), ImGui.GetColorU32(ImGuiCol.Separator));
        ImGui.NewLine();
    }

    /// <summary>Subtle accent button using the unified palette.</summary>
    public static bool AccentButton(string label, Vector4? accent = null, Vector2 size = default)
    {
        var c = accent ?? Primary;
        using var col1 = ImRaii.PushColor(ImGuiCol.Button, c with { W = 0.24f });
        using var col2 = ImRaii.PushColor(ImGuiCol.ButtonHovered, c with { W = 0.38f });
        using var col3 = ImRaii.PushColor(ImGuiCol.ButtonActive, c with { W = 0.50f });
        using var border = ImRaii.PushColor(ImGuiCol.Border, c with { W = 0.55f });
        using var bw = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1f);
        return ImGui.Button(label, size);
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
