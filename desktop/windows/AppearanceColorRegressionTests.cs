using System.Windows.Media;

namespace LyricBridge;

internal static class AppearanceColorRegressionTests
{
    public static Dictionary<string, bool> Run()
    {
        var checks = new Dictionary<string, bool>();
        checks["Opaque full RGB trims whitespace and normalizes uppercase"] =
            AppearanceColors.Normalize("  #a1b2c3  ", AppearanceColors.DefaultLyric) == "#A1B2C3";
        checks["Short RGB expands each channel rather than parsing alpha"] =
            AppearanceColors.Normalize("#a3f", AppearanceColors.DefaultBackground) == "#AA33FF";
        checks["Missing saved colors retain independent lyric and background defaults"] =
            AppearanceColors.Normalize(null, AppearanceColors.DefaultLyric) == "#F5F3F8"
            && AppearanceColors.Normalize(null, AppearanceColors.DefaultBackground) == "#211F29";
        foreach (string invalid in new[] { "", "red", "transparent", "AABBCC", "#12", "#12345", "#1234567",
            "#80112233", "#00FFFFFF", "#GG00FF", "#１２３４５６" })
            checks["Invalid or alpha input safely falls back: " + invalid] =
                AppearanceColors.Normalize(invalid, "#abc") == "#AABBCC";
        checks["Invalid input and fallback still produce a safe opaque default"] =
            AppearanceColors.Normalize("#bad-color", "transparent") == AppearanceColors.DefaultLyric;
        checks["Validator can reject invalid text without committing a replacement"] =
            !AppearanceColors.TryNormalize("#12345678", out var rejected) && rejected == ""
            && AppearanceColors.TryNormalize("#4a9", out var accepted) && accepted == "#44AA99";
        var parsed = AppearanceColors.Parse("#1a2b3c");
        checks["Parsed RGB is opaque with exact channel values"] =
            parsed.A == 255 && parsed.R == 0x1a && parsed.G == 0x2b && parsed.B == 0x3c;
        checks["Color serialization never leaks an unsupported alpha prefix"] =
            AppearanceColors.ToHex(Color.FromArgb(7, 0x1a, 0x2b, 0x3c)) == "#1A2B3C";
        checks["Blending is bounded and yields exact endpoint colors"] =
            AppearanceColors.Blend(Colors.Black, Colors.White, -1) == Colors.Black
            && AppearanceColors.Blend(Colors.Black, Colors.White, 2) == Colors.White
            && AppearanceColors.Blend(Colors.Black, Colors.White, .5) == Color.FromRgb(128, 128, 128);
        checks["Nonfinite blend input cannot produce invisible or corrupt channels"] =
            AppearanceColors.Blend(Color.FromRgb(20, 40, 60), Colors.White, double.NaN) == Color.FromRgb(20, 40, 60);
        checks["Black-white contrast has the expected luminance extrema"] =
            Math.Abs(AppearanceColors.Contrast(Colors.Black, Colors.White) - 21) < .00001;
        checks["Surface classification uses readable ink for bright yellow and dark blue"] =
            AppearanceColors.IsLight("#FFFF00") && !AppearanceColors.IsLight("#0000FF")
            && AppearanceColors.IsLight("#FFFFFF") && !AppearanceColors.IsLight("#000000");

        var combinations = (from background in new[] { "#211F29", "#F6F3F9", "#777777", "#999999", "#FFFF00",
                "#0000FF", "#FF0000", "#113333", "#D7BBA7" }
            from lyric in new[] { "#F5F3F8", "#000000", "#FFFFFF", "#C8BAF7", "#FFD36B" }
            select new { Background = AppearanceColors.Parse(background), Lyric = AppearanceColors.Parse(lyric),
                Palette = AppearanceColors.Resolve(lyric, background) }).ToList();
        checks["All 45 color pairs preserve the chosen lyric rather than silently correcting it"] =
            combinations.All(item => item.Palette.Current == item.Lyric);
        checks["Helper text remains readable across 45 dark light saturated and midtone pairs"] =
            combinations.All(item => new[] { item.Palette.Next, item.Palette.Primary, item.Palette.Muted, item.Palette.Accent }
                .All(color => AppearanceColors.Contrast(color, item.Background) >= 4.5));
        checks["Derived palette stays opaque across the selected color matrix"] =
            combinations.All(item => new[] { item.Palette.Current, item.Palette.Next, item.Palette.Primary, item.Palette.Muted,
                item.Palette.Border, item.Palette.ProgressTrack, item.Palette.Progress, item.Palette.Accent, item.Palette.CardTop,
                item.Palette.CardBottom, item.Palette.Shadow }.All(color => color.A == 255));
        var dark = AppearanceColors.Resolve("#C8BAF7", "#211F29");
        var light = AppearanceColors.Resolve("#75539F", "#F6F3F9");
        checks["Light and dark cards select different helper ink while retaining gentle surfaces"] =
            !dark.LightSurface && light.LightSurface
            && AppearanceColors.Contrast(dark.Primary, AppearanceColors.Parse("#211F29")) > 10
            && AppearanceColors.Contrast(light.Primary, AppearanceColors.Parse("#F6F3F9")) > 10;
        return checks;
    }
}
