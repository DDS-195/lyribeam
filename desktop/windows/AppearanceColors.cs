using System.Globalization;
using System.Windows.Media;

namespace LyricBridge;

internal sealed record AppearancePalette(
    Color Current, Color Next, Color Primary, Color Muted, Color Border,
    Color ProgressTrack, Color Progress, Color Accent, Color CardTop,
    Color CardBottom, Color Shadow, bool LightSurface);

internal static class AppearanceColors
{
    public const string DefaultLyric = "#F5F3F8";
    public const string DefaultBackground = "#211F29";

    // Persistence accepts opaque RGB only. Alpha belongs to the surface style,
    // never to user input where it could silently make lyrics disappear.
    public static string Normalize(string? value, string fallback)
    {
        return TryNormalize(value, out var normalized) ? normalized
            : TryNormalize(fallback, out normalized) ? normalized : DefaultLyric;
    }

    public static bool TryNormalize(string? value, out string normalized)
    {
        string text = value?.Trim() ?? "";
        normalized = "";
        if ((text.Length != 4 && text.Length != 7) || text[0] != '#'
            || !text.Skip(1).All(Uri.IsHexDigit)) return false;
        normalized = text.Length == 4
            ? "#" + string.Concat(text.Skip(1).Select(c => new string(c, 2)))
            : text;
        normalized = normalized.ToUpperInvariant();
        return true;
    }

    public static Color Parse(string value)
    {
        string text = Normalize(value, DefaultLyric);
        return Color.FromRgb(
            byte.Parse(text.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(text.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(text.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Color Blend(Color a, Color b, double amount)
    {
        double mix = double.IsFinite(amount) ? Math.Clamp(amount, 0, 1) : 0;
        byte Channel(byte start, byte end) =>
            (byte)Math.Round(start + (end - start) * mix, MidpointRounding.AwayFromZero);
        return Color.FromRgb(Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B));
    }

    public static bool IsLight(string background) => IsLight(Parse(Normalize(background, DefaultBackground)));

    private static bool IsLight(Color background) => Contrast(Colors.Black, background) >= Contrast(Colors.White, background);

    public static double Contrast(Color foreground, Color background)
    {
        double a = Luminance(foreground), b = Luminance(background);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double c = channel / 255d;
            return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static Color Readable(Color preferred, Color background, double minimum)
    {
        if (Contrast(preferred, background) >= minimum) return preferred;
        var ink = IsLight(background) ? Colors.Black : Colors.White;
        // Keep as much of the requested tint as possible while making helper
        // text readable. The main lyric color deliberately remains user-owned.
        for (int step = 1; step <= 100; step++)
        {
            var candidate = Blend(preferred, ink, step / 100d);
            if (Contrast(candidate, background) >= minimum) return candidate;
        }
        return ink;
    }

    public static AppearancePalette Resolve(string? lyric, string? background)
    {
        var current = Parse(Normalize(lyric, DefaultLyric));
        var surface = Parse(Normalize(background, DefaultBackground));
        bool light = IsLight(surface);
        var primary = Readable(Parse(light ? "#24212B" : "#F5F3F8"), surface, 4.5);
        var muted = Readable(Blend(primary, surface, .34), surface, 4.5);
        var next = Readable(Blend(current, surface, .30), surface, 4.5);
        var accent = Readable(Parse(light ? "#75539F" : "#C8BAF7"), surface, 4.5);
        return new(
            current, next, primary, muted,
            Blend(primary, surface, .84),
            Blend(primary, surface, .89),
            Readable(Blend(current, primary, .25), surface, 3),
            accent,
            Blend(surface, Colors.White, .025),
            Blend(surface, Colors.Black, .035),
            Colors.Black, light);
    }
}
