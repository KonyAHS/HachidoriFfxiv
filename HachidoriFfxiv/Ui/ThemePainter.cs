using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using HachidoriFfxiv.Rendering;
using HachidoriFfxiv.Rendering.Css;

namespace HachidoriFfxiv.Ui;

/// <summary>Paints <see cref="CssBox"/>es with an ImGui draw list and builds themed chips.</summary>
internal static class ThemePainter
{
    private static uint U(Vector4 c) => CssColor.ToAbgr(c);

    /// <summary>Paints a box. <paramref name="fill"/>/<paramref name="border"/> are skipped when ImGui already draws them.</summary>
    /// <param name="shadowDraw">Where blurred shadows go; behind the window so they never paint over it.</param>
    public static void Paint(ImDrawListPtr draw, Vector2 min, Vector2 max, CssBox box, float scale, bool fill = true, bool border = true, ImDrawListPtr? shadowDraw = null)
    {
        var radius = MathF.Min(box.Radius * scale, MathF.Min(max.X - min.X, max.Y - min.Y) * 0.5f);

        // Outer shadows and rings, outside the box.
        foreach (var shadow in box.Shadows.Where(s => !s.Inset))
        {
            var offset = new Vector2(shadow.OffsetX, shadow.OffsetY) * scale;
            var spread = shadow.Spread * scale;
            var blur = shadow.Blur * scale;
            if (blur <= 0.5f)
            {
                if (spread > 0)
                    draw.AddRect(min + offset - new Vector2(spread * 0.5f), max + offset + new Vector2(spread * 0.5f), U(shadow.Color), radius + spread * 0.5f, ImDrawFlags.None, spread);
                continue;
            }

            // A soft shadow as nested translucent fills: they add up towards the box and fade
            // out over the blur radius, with no visible edges between layers.
            var target = shadowDraw ?? draw;
            const int steps = 16;
            var layerAlpha = shadow.Color.W / steps;
            for (var i = 0; i < steps; i++)
            {
                var grow = spread - blur * 0.5f + blur * (i + 1) / steps;
                target.AddRectFilled(min + offset - new Vector2(grow), max + offset + new Vector2(grow),
                    U(shadow.Color with { W = layerAlpha }), MathF.Max(0, radius + grow));
            }
        }

        if (fill && box.Background is { W: > 0 } bg)
            draw.AddRectFilled(min, max, U(bg), radius);

        if (box.GradientTop is { } top && box.GradientBottom is { } bottom)
            VerticalGradient(draw, min, max, radius, top, bottom);

        // Inset rings, widest first so the inner lines sit on top.
        foreach (var ring in box.Shadows.Where(s => s.Inset && s.Blur <= 0.5f && s.Spread > 0).OrderByDescending(s => s.Spread))
        {
            var width = ring.Spread * scale;
            draw.AddRect(min + new Vector2(width * 0.5f), max - new Vector2(width * 0.5f), U(ring.Color), MathF.Max(0, radius - width * 0.5f), ImDrawFlags.None, width);
        }

        if (border && box.BorderWidth > 0 && box.BorderColor is { W: > 0 } bc)
        {
            var w = MathF.Max(1f, box.BorderWidth * scale);
            draw.AddRect(min + new Vector2(w * 0.5f), max - new Vector2(w * 0.5f), U(bc), MathF.Max(0, radius - w * 0.5f), ImDrawFlags.None, w);
        }
    }

    /// <summary>A vertical gradient clipped to a rounded rectangle, drawn as thin strips.</summary>
    private static void VerticalGradient(ImDrawListPtr draw, Vector2 min, Vector2 max, float radius, Vector4 top, Vector4 bottom)
    {
        var height = max.Y - min.Y;
        var strips = Math.Clamp((int)(height / 3), 8, 96);
        for (var i = 0; i < strips; i++)
        {
            var y0 = min.Y + height * i / strips;
            var y1 = min.Y + height * (i + 1) / strips;
            var mid = (y0 + y1) * 0.5f;
            var inset = 0f;
            if (radius > 0)
            {
                var dy = mid < min.Y + radius ? min.Y + radius - mid : mid > max.Y - radius ? mid - (max.Y - radius) : 0;
                inset = radius - MathF.Sqrt(MathF.Max(0, radius * radius - dy * dy));
            }

            var t = (mid - min.Y) / height;
            var color = Vector4.Lerp(top, bottom, t);
            if (color.W > 0.003f)
                draw.AddRectFilled(new Vector2(min.X + inset, y0), new Vector2(max.X - inset, y1), U(color));
        }
    }

    /// <summary>The window decorations a theme adds around ImGui's own background and border.</summary>
    public static void DecorateWindow(PopupTheme theme, Vector2 min, Vector2 max)
    {
        if (theme.IsPlugin)
            return;
        var draw = ImGui.GetWindowDrawList();
        var scale = theme.Scale;
        var margin = new Vector2(64 * scale);
        draw.PushClipRect(min - margin, max + margin, false);
        Paint(draw, min, max, theme.Popup, scale, fill: false, border: false, shadowDraw: ImGui.GetBackgroundDrawList());
        if (theme.Frame is { } frame)
        {
            var inset = new Vector2(frame.Inset * scale);
            Paint(draw, min + inset, max - inset, frame, scale);
        }

        if (theme.Crest)
            Crest(draw, new Vector2((min.X + max.X) * 0.5f, min.Y), scale, theme);
        draw.PopClipRect();
    }

    /// <summary>A faceted crystal in a mount with swept wings, centred on the top edge (Crystal's ::after).</summary>
    private static void Crest(ImDrawListPtr draw, Vector2 top, float scale, PopupTheme theme)
    {
        var s = scale * 0.8f;
        var gold = U(theme.CrestGold);
        var goldDark = U(CssColor.Mix(theme.CrestGold, 0.55f, new Vector4(0, 0, 0, 1), 0.45f));
        var light = U(CssColor.Mix(theme.CrestGold, 0.4f, Vector4.One, 0.6f));
        var center = top + new Vector2(0, 6 * s);

        // Wings: tapered sweeps either side of the gem.
        for (var side = -1; side <= 1; side += 2)
        {
            var points = new[]
            {
                center + new Vector2(side * 9, -6) * s,
                center + new Vector2(side * 30, -11) * s,
                center + new Vector2(side * 58, -9) * s,
                center + new Vector2(side * 34, -4) * s,
                center + new Vector2(side * 22, 2) * s,
                center + new Vector2(side * 10, 3) * s,
            };
            for (var i = 1; i < points.Length - 1; i++)
                draw.AddTriangleFilled(points[0], points[i], points[i + 1], gold);
            for (var i = 0; i < points.Length; i++)
                draw.AddLine(points[i], points[(i + 1) % points.Length], goldDark, 1f);
            draw.AddLine(center + new Vector2(side * 12, -7) * s, center + new Vector2(side * 50, -9.5f) * s, light, 1f);
        }

        // Mount and gem.
        var mount = new[] { center + new Vector2(0, -15) * s, center + new Vector2(11, -1) * s, center + new Vector2(0, 21) * s, center + new Vector2(-11, -1) * s };
        draw.AddQuadFilled(mount[0], mount[1], mount[2], mount[3], gold);
        draw.AddQuad(mount[0], mount[1], mount[2], mount[3], goldDark, 1f);
        var gem = theme.CrestGem;
        var gemDark = CssColor.Mix(gem, 0.5f, new Vector4(0.04f, 0.16f, 0.4f, 1), 0.5f);
        var g = new[] { center + new Vector2(0, -12) * s, center + new Vector2(8.5f, -1) * s, center + new Vector2(0, 17) * s, center + new Vector2(-8.5f, -1) * s };
        draw.AddQuadFilled(g[0], g[1], g[2], g[3], U(gem));
        draw.AddTriangleFilled(g[0], g[1], center + new Vector2(0, -1) * s, U(Vector4.Lerp(gem, Vector4.One, 0.55f)));
        draw.AddTriangleFilled(g[2], g[1], center + new Vector2(0, -1) * s, U(gemDark));
        draw.AddTriangleFilled(g[0], g[3], g[2], U(gemDark with { W = 0.35f }));
        draw.AddLine(g[3], g[1], U(new Vector4(0.84f, 0.95f, 1, 0.8f)), 0.8f);
        draw.AddTriangleFilled(center + new Vector2(-1.5f, -9) * s, center + new Vector2(-6, -1.5f) * s, center + new Vector2(-2.5f, -1.5f) * s, 0xEEFFFFFF);
    }

    // ------------------------------------------------------------------ chips

    /// <summary>A chip run in a theme's chip style.</summary>
    public static RichRun Chip(string text, CssBox box, PopupTheme theme) => new()
    {
        Text = text,
        Chip = true,
        Background = box.Background is { W: > 0 } bg ? CssColor.ToAbgr(bg) : 0,
        ChipBorder = box.BorderWidth > 0 && box.BorderColor is { W: > 0 } border ? CssColor.ToAbgr(border) : 0,
        ChipRadiusEm = theme.IsPlugin ? 0.3f : box.Radius / MathF.Max(8f, box.FontSize ?? 12f),
        Color = box.Color is { } c ? CssColor.ToAbgr(c) : 0xFFFFFFFF,
        Scale = 0.75f,
    };

    // ------------------------------------------------------------------ style

    /// <summary>Pushes a theme's colours and shapes for one popup window; returns the restore actions.</summary>
    public static void Push(PopupTheme theme, Stack<Action> cleanup)
    {
        void Color(ImGuiCol col, Vector4 value)
        {
            ImGui.PushStyleColor(col, value);
            cleanup.Push(() => ImGui.PopStyleColor());
        }

        void Var(ImGuiStyleVar v, float value)
        {
            ImGui.PushStyleVar(v, value);
            cleanup.Push(() => ImGui.PopStyleVar());
        }

        void Var2(ImGuiStyleVar v, Vector2 value)
        {
            ImGui.PushStyleVar(v, value);
            cleanup.Push(() => ImGui.PopStyleVar());
        }

        if (theme.IsPlugin)
        {
            Var(ImGuiStyleVar.WindowRounding, 8f);
            Var(ImGuiStyleVar.WindowBorderSize, 1f);
            Color(ImGuiCol.Border, theme.Accent);
            return;
        }

        var scale = theme.Scale;
        var popup = theme.Popup;
        Var(ImGuiStyleVar.WindowRounding, popup.Radius * scale);
        Var(ImGuiStyleVar.WindowBorderSize, popup.BorderColor is { W: > 0 } ? popup.BorderWidth * scale : 0);
        Var2(ImGuiStyleVar.WindowPadding, WindowPadding(theme));
        Var(ImGuiStyleVar.FrameRounding, theme.ButtonRadius * scale);
        Var(ImGuiStyleVar.ScrollbarRounding, 6 * scale);

        var bg = popup.Background ?? new Vector4(0, 0, 0, 0.85f);
        Color(ImGuiCol.WindowBg, bg);
        Color(ImGuiCol.PopupBg, bg with { W = 1 });
        Color(ImGuiCol.Border, popup.BorderColor ?? CssColor.Transparent);
        Color(ImGuiCol.BorderShadow, CssColor.Transparent);
        Color(ImGuiCol.Text, theme.Text);
        Color(ImGuiCol.TextDisabled, theme.TextMuted);
        Color(ImGuiCol.TextSelectedBg, theme.Highlight);
        Color(ImGuiCol.Separator, theme.Separator ?? theme.TextMuted with { W = 0.4f });
        Color(ImGuiCol.Button, theme.Button);
        Color(ImGuiCol.ButtonHovered, theme.ButtonHovered);
        Color(ImGuiCol.ButtonActive, theme.ButtonHovered with { W = MathF.Min(1, theme.ButtonHovered.W + 0.2f) });
        Color(ImGuiCol.ScrollbarBg, CssColor.Transparent);
        var grab = theme.Scrollbar ?? theme.TextMuted with { W = 0.45f };
        Color(ImGuiCol.ScrollbarGrab, grab);
        Color(ImGuiCol.ScrollbarGrabHovered, grab with { W = MathF.Min(1, grab.W + 0.2f) });
        Color(ImGuiCol.ScrollbarGrabActive, theme.Accent);
    }

    /// <summary>Styles for the window's contents, pushed after Begin (which has drawn the window border by then).</summary>
    public static void PushInner(PopupTheme theme, Stack<Action> cleanup)
    {
        if (theme.IsPlugin || theme.ButtonBorder is not { } border)
            return;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, MathF.Max(1, theme.Scale));
        cleanup.Push(() => ImGui.PopStyleVar());
        ImGui.PushStyleColor(ImGuiCol.Border, border);
        cleanup.Push(() => ImGui.PopStyleColor());
    }

    /// <summary>The theme's padding, at least clear of its frame and never cramped.</summary>
    public static Vector2 WindowPadding(PopupTheme theme)
    {
        var p = theme.Popup.Padding;
        var x = MathF.Max(p.W, p.Y);
        var y = MathF.Max(p.X, p.Z);
        var minimum = 8f;
        if (theme.Frame is { } frame)
            minimum = frame.Inset + frame.BorderWidth + 6;
        if (theme.Crest)
            y = MathF.Max(y, 14);
        return new Vector2(MathF.Max(x, minimum), MathF.Max(y, minimum)) * theme.Scale;
    }
}
