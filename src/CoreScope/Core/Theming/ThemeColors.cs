using System;
using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace CoreScope.Core.Theming;

/// <summary>Small colour helpers for the theme engine (pure functions, unit tested).</summary>
public static class ThemeColors
{
    /// <summary>Parses "#RGB", "#RRGGBB" or "#AARRGGBB" (the # is optional).</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length == 6) hex = "FF" + hex;
        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Linear blend: t = 0 gives <paramref name="a"/>, 1 gives <paramref name="b"/>. Alpha comes from <paramref name="a"/>.</summary>
    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(a.A, Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    /// <summary>Relative luminance (WCAG), 0 = black, 1 = white.</summary>
    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG contrast ratio between two colours (1 to 21).</summary>
    public static double Contrast(Color a, Color b)
    {
        var (hi, lo) = (Luminance(a), Luminance(b));
        if (hi < lo) (hi, lo) = (lo, hi);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Black or white, whichever reads better on <paramref name="background"/>.</summary>
    public static Color OnColor(Color background) =>
        Contrast(background, Colors.White) >= Contrast(background, Colors.Black) ? Colors.White : Colors.Black;

    /// <summary>Nudges an accent towards white (dark UI) or black (light UI) until it reads as text on the surface (at least 4.5:1).</summary>
    public static Color ReadableOn(Color accent, Color surface)
    {
        var toward = Luminance(surface) < 0.5 ? Colors.White : Colors.Black;
        var c = accent;
        for (var i = 0; i < 12 && Contrast(c, surface) < 4.5; i++) c = Lerp(c, toward, 0.12);
        return c;
    }
}
