using System.Reflection;
namespace LyricBridge;
internal static class RecordingEvidenceRegressionTests
{
    public static Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var assembly = typeof(RecordingEvidenceRegressionTests).Assembly;
        var rules = assembly.GetType("LyricBridge.RecordingRules") ?? typeof(MediaSnapshot).Assembly.GetType("LyricBridge.RecordingRules")!;
        var titles = rules.GetMethod("TitlesMatch", BindingFlags.Static | BindingFlags.NonPublic);
        var albums = rules.GetMethod("AlbumsCompatible", BindingFlags.Static | BindingFlags.NonPublic);
        var oldProvider = typeof(MediaSnapshot).Assembly.GetType("LyricBridge.LyricsProvider")!;
        var oldScore = oldProvider.GetMethod("Score", BindingFlags.Static | BindingFlags.NonPublic)!;
        var normalize = oldProvider.GetMethod("Normalize", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Title(MediaSnapshot s, string candidate, bool artistMatches = true, double? duration = null) => titles != null
            ? (bool)titles.Invoke(null, new object[] { s, candidate, artistMatches, duration ?? s.DurationMs })!
            : (double)oldScore.Invoke(null, new object[] { s, candidate, artistMatches ? s.Artist : "unrelated artist", duration ?? s.DurationMs })! >= 65;
        string Norm(string value) => (string)normalize.Invoke(null, new object[] { value })!;
        bool Album(string a, string b, string artist = "") => albums != null ? (bool)albums.Invoke(null,
            albums.GetParameters().Length == 3 ? new object[] { a, b, artist } : new object[] { a, b })!
            : Norm(a).Length == 0 || Norm(b).Length == 0 || Norm(a) == Norm(b);
        void Check(string name, bool value) => checks[name] = value;
        var song = new MediaSnapshot { Type="media", Title="测试作品", Artist="测试歌手", DurationMs=180000 };
        Check("Equivalent original label stays compatible", Title(song with {Title="HEAVEN (Original Version)"}, "HEAVEN"));
        Check("Literal matching title needs no invented duration", Title(song with {DurationMs=0}, song.Title, duration:0));
        Check("Arbitrary title suffix is not an alias", !Title(song, "测试作品以后"));
        foreach (string tag in new[] { "Live", "Remix", "Cover", "DJ版", "Acoustic", "Demo", "伴奏", "加速版", "Remastered" })
            Check("An omitted recording qualifier stays rejected: " + tag, !Title(song, song.Title + " (" + tag + ")"));
        Check("English language abbreviation conflict is rejected", !Title(song with {Title="测试作品 (Japanese Ver.)"}, "测试作品 (Korean Ver.)"));
        Check("Bare language abbreviation conflict is rejected", !Title(song with {Title="测试作品 (JP Ver.)"}, "测试作品 (KR Ver.)"));
        Check("Full and abbreviated same language agree", Title(song with {Title="测试作品 (Japanese Version)"}, "测试作品 (Japanese Ver.)"));
        Check("Chinese and English labels for same language agree", Title(song with {Title="测试作品 (日语版)"}, "测试作品 (Japanese Ver.)"));
        Check("Omitted Chinese language label has corroboration", Title(song with {Title="K歌之王 (粤语版)"}, "K歌之王"));
        Check("Omitted language needs known duration", !Title(song with {Title="K歌之王 (粤语版)"}, "K歌之王", duration:0));
        Check("Language from known album cannot contradict candidate title", !Title(song with {Album="测试专辑 (Japanese Ver.)"}, "测试作品 (Korean Ver.)"));
        Check("Different full language versions are rejected", !Title(song with {Title="测试作品 (Japanese Version)"}, "测试作品 (Korean Version)"));
        var translated = song with {Title="夜信",Artist="IU",DurationMs=253000};
        Check("A bilingual title retains its translated alias", Title(translated, "밤편지 (夜信)"));
        Check("A translated alias needs matching singer", !Title(translated, "밤편지 (夜信)", false));
        Check("A translated alias needs matching duration", !Title(translated, "밤편지 (夜信)", duration:0));
        Check("Translated aliases cannot erase a live recording", !Title(translated, "밤편지 (夜信) (Live)"));
        var collaboration = song with {Title="If The Sun Burns Out Tonight", Artist="无畏契约、Grabbitz、Oli Sykes、Courtney Laplante", DurationMs=226413,
            Album="If The Sun Burns Out Tonight (feat. Oli Sykes & Courtney LaPlante) (炽日将烬)"};
        Check("Known featured singers and translated title are descriptive metadata", Title(collaboration, "If The Sun Burns Out Tonight (feat. Oli Sykes & Courtney LaPlante) (炽日将烬)"));
        Check("An unlisted featured singer is not ignored", !Title(collaboration, "If The Sun Burns Out Tonight (feat. Another Singer)"));
        Check("A credited feature still requires recording duration", !Title(collaboration, "If The Sun Burns Out Tonight (feat. Oli Sykes)", duration:0));
        Check("Trailing credited feature is compatible", Title(song with {Title="Same Song",Artist="Lead、Guest"}, "Same Song feat. Guest"));
        var explicitSong = song with {Title="Soft Lips",Artist="BlankCh3ck、Nathaniel Davis",Album="XOXO (Explicit)",DurationMs=166922};
        Check("Explicit album confirms an omitted title label", Title(explicitSong, "Soft Lips (Explicit)"));
        Check("An unknown album cannot invent explicit equivalence", !Title(explicitSong with {Album=""}, "Soft Lips (Explicit)"));
        Check("An explicit album rejects a clean title", !Title(explicitSong, "Soft Lips (Clean)"));
        Check("Explicit and clean title versions never agree", !Title(explicitSong with {Title="Soft Lips (Clean)",Album="XOXO (Clean)"}, "Soft Lips (Explicit)"));
        Check("Equal explicit titles stay usable without album", Title(explicitSong with {Title="Soft Lips (Explicit)",Album=""}, "Soft Lips (Explicit)"));
        Check("Clean album can confirm an omitted clean title label", Title(explicitSong with {Title="Soft Lips (Clean)",Album="XOXO (Clean)"}, "Soft Lips"));
        Check("Translated album annotation is compatible", Album("Dish of the Day (今日菜单)", "Dish of the Day"));
        Check("A translated album title can use its explicit alias", Album("今日菜单", "Dish of the Day (今日菜单)"));
        Check("Unknown album does not invent a conflict", Album("", "Different Album") && Album("Known Album", ""));
        Check("Arbitrary known albums cannot substitute", !Album("Album A", "Album B"));
        Check("A deluxe album edition cannot silently disappear", !Album("测试专辑 (Deluxe)", "测试专辑"));
        Check("Different album language versions stay rejected", !Album("Same Album (Japanese Ver.)", "Same Album (Korean Ver.)"));
        Check("Same album language aliases agree", Album("Same Album (Japanese Version)", "Same Album (日语版)"));
        Check("Explicit and clean albums stay distinct", !Album("XOXO (Explicit)", "XOXO (Clean)"));
        Check("An unmarked known album cannot confirm explicit edition", !Album("XOXO (Explicit)", "XOXO"));
        Check("An album translation cannot erase a live qualifier", !Album("Same Album (现场版)", "Same Album"));
        Check("Known featured album credit is descriptive metadata", Album("Share That Love (Explicit)", "Share That Love (feat. G-Eazy) (Explicit)", "Lukas Graham、G-Eazy"));
        Check("Featured album credit can be omitted on either side", Album("Share That Love (feat. G-Eazy) (Explicit)", "Share That Love (Explicit)", "Lukas Graham、G-Eazy"));
        Check("An unlisted album feature cannot be ignored", !Album("Share That Love (Explicit)", "Share That Love (feat. Another Singer) (Explicit)", "Lukas Graham、G-Eazy"));
        Check("Album feature omission needs actual singer context", !Album("Share That Love (Explicit)", "Share That Love (feat. G-Eazy) (Explicit)"));
        Check("Credited album features cannot hide clean versus explicit conflict", !Album("Share That Love (Explicit)", "Share That Love (feat. G-Eazy) (Clean)", "Lukas Graham、G-Eazy"));
        Check("Credited album features cannot hide a live edition", !Album("Share That Love (Explicit)", "Share That Love (feat. G-Eazy) (Explicit) (Live)", "Lukas Graham、G-Eazy"));
        return Task.FromResult(checks);
    }
}
