using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Enfolderer.App;

public class BinderThemeService
{
    private readonly List<Brush> _customBinderBrushes = new();
    private readonly List<Brush> _generatedRandomBinderBrushes = new();
    private int _seedHash = 0;

    public record DirectiveResult(int? PagesPerBinder, string? LayoutMode, bool EnableHttpDebug);

    public void Reset(string? variabilitySeed = null)
    {
        _customBinderBrushes.Clear();
        _generatedRandomBinderBrushes.Clear();
        _seedHash = variabilitySeed == null ? 0 : variabilitySeed.GetHashCode();
    }

    public DirectiveResult ApplyDirectiveLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new(null,null,false);
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("**")) return new(null,null,false);
        trimmed = trimmed.Substring(2).Trim();
        if (trimmed.Length == 0) return new(null,null,false);
        int? pagesPerBinder = null; string? layoutMode = null; bool httpDebug = false;
        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var partRaw in parts)
        {
            var part = partRaw.Trim();
            if (part.StartsWith("pages=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(part.Substring(6), out int pages) && pages>0) pagesPerBinder = pages;
                continue;
            }
            if (string.Equals(part, "4x3", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "3x3", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "2x2", StringComparison.OrdinalIgnoreCase))
            { layoutMode = part.ToLowerInvariant(); continue; }
            if (part.Equals("httplog", StringComparison.OrdinalIgnoreCase) || part.Equals("debughttp", StringComparison.OrdinalIgnoreCase))
            { httpDebug = true; continue; }
            if (TryParseColorToken(part, out var b)) _customBinderBrushes.Add(b);
        }
        return new(pagesPerBinder, layoutMode, httpDebug);
    }

    public Brush GetBrushForBinder(int zeroBasedBinderIndex)
    {
        if (zeroBasedBinderIndex < 0) zeroBasedBinderIndex = 0;
        Brush baseBrush;
        if (zeroBasedBinderIndex < _customBinderBrushes.Count)
        {
            baseBrush = _customBinderBrushes[zeroBasedBinderIndex];
        }
        else
        {
            int needed = zeroBasedBinderIndex - _customBinderBrushes.Count;
            while (_generatedRandomBinderBrushes.Count <= needed)
            {
                int n = _generatedRandomBinderBrushes.Count + 1;
                // Deterministic-ish color from binder index + seed hash
                int hash = HashCombine(_seedHash, n);
                double hue = (hash & 0xFFFF) / 65535.0 * 360.0;
                double sat = 0.55 + ((hash >> 16) & 0xFF) / 255.0 * 0.25; // 0.55 - 0.80
                double light = 0.35 + ((hash >> 24) & 0xFF) / 255.0 * 0.25; // 0.35 - 0.60
                var col = FromHsl(hue, sat, light);
                var brush = new SolidColorBrush(col);
                if (brush.CanFreeze) brush.Freeze();
                _generatedRandomBinderBrushes.Add(brush);
            }
            baseBrush = _generatedRandomBinderBrushes[needed];
        }
        return baseBrush;
    }

    private static int HashCombine(int h1, int h2)
    {
        unchecked { return ((h1 << 5) + h1) ^ h2; }
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        hue = hue % 360.0; if (hue < 0) hue += 360.0;
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        double m = lightness - c / 2;
        double r=0,g=0,b=0;
        if (hue < 60) { r=c; g=x; b=0; }
        else if (hue < 120) { r=x; g=c; b=0; }
        else if (hue < 180) { r=0; g=c; b=x; }
        else if (hue < 240) { r=0; g=x; b=c; }
        else if (hue < 300) { r=x; g=0; b=c; }
        else { r=c; g=0; b=x; }
        byte R = (byte)Math.Round((r + m) * 255);
        byte G = (byte)Math.Round((g + m) * 255);
        byte B = (byte)Math.Round((b + m) * 255);
        return Color.FromRgb(R,G,B);
    }

    private bool TryParseColorToken(string token, out Brush brush)
    {
        brush = Brushes.Transparent;
        if (string.IsNullOrWhiteSpace(token)) return false;
        token = token.Trim();
        try
        {
            var obj = ColorConverter.ConvertFromString(token);
            if (obj is Color col)
            {
                var solid = new SolidColorBrush(col); if (solid.CanFreeze) solid.Freeze(); brush = solid; return true;
            }
        } catch { }
        if (Regex.IsMatch(token, "^[0-9A-Fa-f]{6}$"))
        {
            try
            {
                byte r = Convert.ToByte(token.Substring(0,2),16);
                byte g = Convert.ToByte(token.Substring(2,2),16);
                byte b = Convert.ToByte(token.Substring(4,2),16);
                var solid = new SolidColorBrush(Color.FromRgb(r,g,b)); if (solid.CanFreeze) solid.Freeze(); brush = solid; return true;
            } catch { }
        }
        return false;
    }
}
