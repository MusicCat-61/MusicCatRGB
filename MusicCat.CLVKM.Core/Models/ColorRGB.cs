using System;
using System.Runtime.InteropServices;

namespace MusicCat.CLVKM.Core.Models;

[StructLayout(LayoutKind.Sequential)]
public record struct ColorRGB(byte R, byte G, byte B)
{
    public static readonly ColorRGB Black = new(0, 0, 0);
    public static readonly ColorRGB White = new(255, 255, 255);
    public static readonly ColorRGB Red = new(255, 0, 0);
    public static readonly ColorRGB Green = new(0, 255, 0);
    public static readonly ColorRGB Blue = new(0, 0, 255);

    // Fast brightness adjustment
    public ColorRGB ApplyBrightness(float brightness)
    {
        if (brightness >= 1.0f) return this;
        if (brightness <= 0.0f) return Black;

        return new ColorRGB(
            (byte)(R * brightness),
            (byte)(G * brightness),
            (byte)(B * brightness)
        );
    }

    /// <summary>Linear interpolation between two colours (t = 0 → a, t = 1 → b).</summary>
    public static ColorRGB Lerp(ColorRGB a, ColorRGB b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return new ColorRGB(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>Hue in degrees [0..360), saturation/value in [0..1].</summary>
    public (double Hue, double Saturation, double Value) ToHsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * (((b - r) / delta) + 2);
            else hue = 60 * (((r - g) / delta) + 4);
        }

        if (hue < 0) hue += 360;
        double saturation = max <= 0 ? 0 : delta / max;

        return (hue, saturation, max);
    }

    public static ColorRGB FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0.0, 1.0);
        value = Math.Clamp(value, 0.0, 1.0);

        double c = value * saturation;
        double x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        double m = value - c;

        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return new ColorRGB(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
