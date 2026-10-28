using System.Text.Json;
namespace LyricBridge;
internal static class NativePlatformRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var song = new MediaSnapshot { Type = "media", Title = "测试作品", Artist = "测试歌手", Album = "版本专辑", DurationMs = 180000 };
        string Full(MediaSnapshot s) => "[ti:" + s.Title + "]\n[ar:" + s.Artist + "]\n[al:" + s.Album + "]\n[00:00]原版第一句\n[00:45]第二句\n[01:30]第三句\n[02:40]末句";
        foreach (string package in new[] { "com.netease.cloudmusic", "com.tencent.qqmusic" })
        {
            bool qq = package == "com.tencent.qqmusic";
            var current = song with { PackageName = package };
            checks["Native metadata search does not require a media ID: " + package] = PlatformLyrics.SupportsSearch(current) && !PlatformLyrics.Supports(current);
            string Search(MediaSnapshot candidate, string id = "900100") => qq
                ? JsonSerializer.Serialize(new { code = 0, data = new { song = new { list = new[] {
                    new { songid = id, songmid = "0039MnYb0qxYhV", songname = candidate.Title, interval = candidate.DurationMs / 1000,
                        albumname = candidate.Album, singer = new[] { new { name = candidate.Artist } } }
                } } } })
                : JsonSerializer.Serialize(new { code = 200, result = new { songs = new[] {
                    new { id, name = candidate.Title, duration = candidate.DurationMs, album = new { name = candidate.Album },
                        artists = new[] { new { name = candidate.Artist } } }
                } } });
            string Lyric(string content) => qq ? JsonSerializer.Serialize(new { code = 0, retcode = 0, lyric = content })
                : JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = content } });
            int downloads = 0; string actualSearch = "", actualDownload = "";
            Func<string, string?, CancellationToken, Task<JsonDocument>> Fixture(MediaSnapshot candidate, string? content = null, string id = "900100") =>
                (url, referer, token) => {
                    token.ThrowIfCancellationRequested();
                    bool search = new Uri(url).AbsolutePath.Contains("/search/") || new Uri(url).AbsolutePath.Contains("/soso/");
                    if (search) actualSearch = url; else { downloads++; actualDownload = url; }
                    return Task.FromResult(JsonDocument.Parse(search ? Search(candidate, id) : Lyric(content ?? Full(current))));
                };
            var found = await PlatformLyrics.SearchAsync(current, Fixture(current), CancellationToken.None);
            checks["Native candidate is checked and its own lyric document is used: " + package] = downloads == 1
                && LyricsProvider.ParseLyrics(found.Item1, current).Count == 4 && found.Item2.Contains(qq ? "QQ音乐" : "网易云");
            checks["Native search uses the public JSON address without the known empty-result variant: " + package] = qq
                ? actualSearch.Contains("/soso/") && !actualSearch.Contains("new_json=1")
                : actualSearch.Contains("/api/search/get?") && !actualSearch.Contains("/get/web?");
            foreach (var mismatch in new[] {
                ("artist", current with { Artist = "另一位歌手" }),
                ("duration", current with { DurationMs = 184001 }),
                ("Live recording", current with { Title = current.Title + " (Live)" }),
                ("album", current with { Album = "另一张专辑" })
            })
            {
                downloads = 0;
                found = await PlatformLyrics.SearchAsync(current, Fixture(mismatch.Item2), CancellationToken.None);
                checks["A native " + mismatch.Item1 + " conflict is rejected before lyric download: " + package] = downloads == 0 && found.Item1.Length == 0;
            }
            downloads = 0;
            found = await PlatformLyrics.SearchAsync(current, Fixture(current with { Album = "" }), CancellationToken.None);
            checks["Missing candidate album does not invent a conflicting album: " + package] = downloads == 1 && LyricsProvider.ParseLyrics(found.Item1, current).Count == 4;
            bool unreliable = false;
            try { await PlatformLyrics.SearchAsync(current, Fixture(current, "[00:00]只有开头\n[00:10]不完整"), CancellationToken.None); }
            catch (UnreliableLyricsException) { unreliable = true; }
            checks["A native truncated timeline remains retryable instead of becoming a successful match: " + package] = unreliable;
            unreliable = false;
            try { await PlatformLyrics.SearchAsync(current, Fixture(current, Full(current with { Artist = "另一位歌手" })), CancellationToken.None); }
            catch (UnreliableLyricsException) { unreliable = true; }
            checks["Native lyric headers cannot contradict the verified candidate: " + package] = unreliable;
            bool denied = false;
            try { await PlatformLyrics.SearchAsync(current, (_, _, _) => Task.FromResult(JsonDocument.Parse("{\"code\":403}")), CancellationToken.None); }
            catch (HttpRequestException) { denied = true; }
            checks["A native business denial is a service error rather than an empty catalogue: " + package] = denied;
            var identified = current with { MediaId = "900100" };
            downloads = 0;
            string detail = qq
                ? JsonSerializer.Serialize(new { code = 0, data = new[] { new { id = 900100, title = current.Title, interval = 180,
                    singer = new[] { new { name = current.Artist } }, album = new { name = "另一张专辑" } } } })
                : JsonSerializer.Serialize(new { code = 200, songs = new[] { new { id = 900100, name = current.Title, duration = 180000,
                    artists = new[] { new { name = current.Artist } }, album = new { name = "另一张专辑" } } } });
            found = await PlatformLyrics.FetchAsync(identified, (url, _, _) => {
                if (url.Contains("lyric")) downloads++;
                return Task.FromResult(JsonDocument.Parse(url.Contains("lyric") ? Lyric(Full(current)) : detail));
            }, CancellationToken.None);
            checks["A native ID detail with a conflicting album cannot be silently accepted: " + package] = downloads == 0 && found.Item1.Length == 0;
            if (qq)
            {
                found = await PlatformLyrics.SearchAsync(current, Fixture(current, id: ""), CancellationToken.None);
                checks["A native QQ MID can identify a candidate when its numeric ID is absent"] = actualDownload.Contains("songmid=0039MnYb0qxYhV") && LyricsProvider.ParseLyrics(found.Item1, current).Count == 4;
            }
        }
        bool encryptedRejected = false;
        try { await PlatformLyrics.SearchNeteaseAsync(song, (_, _, _) => Task.FromResult(JsonDocument.Parse("{\"code\":200,\"result\":\"abcdef012345\"}")), CancellationToken.None); }
        catch (HttpRequestException) { encryptedRejected = true; }
        checks["Unreadable NetEase result payload is a source failure, not missing lyrics"] = encryptedRejected;
        checks["Custom and lookalike app IDs cannot opt into another platform's native library"] =
            !PlatformLyrics.SupportsSearch(song with { PackageName = "custom.player" })
            && !PlatformLyrics.SupportsSearch(song with { PackageName = "com.tencent.qqmusic.fake" });
        return checks;
    }
}
