using System.Text.Json;
namespace LyricBridge;

internal static class PlatformLyricsRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var netease = new MediaSnapshot { PackageName = "com.netease.cloudmusic", MediaId = "186016",
            Title = "晴天", Artist = "周杰伦", DurationMs = 269000 };
        var qq = netease with { PackageName = "com.tencent.qqmusic", MediaId = "003Ue6Ia32q4gl" };
        string Serialize(object value) => JsonSerializer.Serialize(value);
        Task<JsonDocument> Json(string value) => Task.FromResult(JsonDocument.Parse(value));
        string NetDetail(string id = "186016", string title = "晴天", string singer = "周杰伦", long duration = 269000) =>
            Serialize(new { code = 200, songs = new[] { new { id, name = title, duration, artists = new[] { new { name = singer } } } } });
        string QqDetail(string id = "201773975", string mid = "003Ue6Ia32q4gl", string title = "晴天", string name = "晴天", string singer = "周杰伦", long interval = 269) =>
            Serialize(new { code = 0, data = new[] { new { id, mid, title, name, interval, singer = new[] { new { name = singer } } } } });
        const string lyric = "[ti:晴天]\n[ar:周杰伦]\n[00:00]平台编号歌词\n[01:30]平台中段歌词\n[03:50]平台末段歌词";
        checks["Only exact known player packages accept numeric or QQ MID identifiers"] =
            PlatformLyrics.Supports(netease) && PlatformLyrics.Supports(qq)
            && PlatformLyrics.Supports(qq with { MediaId = "201773975" })
            && !PlatformLyrics.Supports(netease with { PackageName = "com.unknown.player" })
            && !PlatformLyrics.Supports(netease with { PackageName = "com.netease.cloudmusic.fake" })
            && !PlatformLyrics.Supports(netease with { MediaId = qq.MediaId });
        checks["Opaque local identifiers and out-of-range syntax are rejected"] =
            new[] { "", "0", "-186016", "song_186016", "content://media/186016", "186016.mp3", "1" + new string('0', 20) }
            .All(id => !PlatformLyrics.Supports(netease with { MediaId = id }));
        checks["Complete official song URLs are parsed without cross-platform inference"] =
            PlatformLyrics.Supports(netease with { MediaId = "https://music.163.com/song?id=186016" })
            && PlatformLyrics.Supports(netease with { MediaId = "https://music.163.com/#/song?id=186016" })
            && PlatformLyrics.Supports(qq with { MediaId = "https://y.qq.com/n/ryqq/songDetail/003Ue6Ia32q4gl" })
            && !PlatformLyrics.Supports(netease with { MediaId = "https://music.163.com/playlist?id=186016" })
            && !PlatformLyrics.Supports(netease with { MediaId = "https://music.163.com.evil.test/song?id=186016" })
            && !PlatformLyrics.Supports(netease with { MediaId = "https://music.163.com/song?id=186016&id=123" })
            && !PlatformLyrics.Supports(netease with { MediaId = "https://user@music.163.com/song?id=186016" })
            && !PlatformLyrics.Supports(netease with { MediaId = "https://y.qq.com/n/ryqq/songDetail/003Ue6Ia32q4gl" });

        int requests = 0, lyricRequests = 0; bool referers = true;
        Task<JsonDocument> NetGet(string url, string? referer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); requests++; referers &= referer == "https://music.163.com/";
            if (new Uri(url).AbsolutePath.Contains("/detail")) return Json(NetDetail());
            lyricRequests++;
            return Json(Serialize(new { code = 200, lrc = new { lyric } }));
        }
        var result = await PlatformLyrics.FetchAsync(netease, NetGet, CancellationToken.None);
        checks["NetEase verifies recording details before requesting original timed lyrics"] =
            result.Item1 == lyric && result.Item2.Contains("网易云") && requests == 2 && lyricRequests == 1 && referers;
        requests = 0;
        await PlatformLyrics.FetchAsync(netease with { PackageName = "unknown" }, NetGet, CancellationToken.None);
        checks["Unsupported input never issues a network request"] = requests == 0;

        foreach (string problem in new[] { "id", "title", "artist", "duration", "version" })
        {
            lyricRequests = 0;
            result = await PlatformLyrics.FetchAsync(netease, (url, _, _) => {
                if (new Uri(url).AbsolutePath.Contains("/detail"))
                    return Json(NetDetail(id: problem == "id" ? "999" : netease.MediaId,
                        title: problem == "title" ? "江南" : problem == "version" ? "晴天 (Live)" : netease.Title,
                        singer: problem == "artist" ? "林俊杰" : netease.Artist,
                        duration: problem == "duration" ? 300000 : netease.DurationMs));
                lyricRequests++; return Json(Serialize(new { code = 200, lrc = new { lyric } }));
            }, CancellationToken.None);
            checks["NetEase mismatched " + problem + " cannot fetch or reuse wrong lyrics"] =
                result.Item1.Length == 0 && lyricRequests == 0;
        }
        requests = 0; referers = true; bool requestedMid = false;
        result = await PlatformLyrics.FetchAsync(qq, (url, referer, _) => {
            requests++; referers &= referer == "https://y.qq.com/";
            if (new Uri(url).AbsolutePath.Contains("single_song")) return Json(QqDetail());
            requestedMid = url.Contains("songmid=" + qq.MediaId) && !url.Contains("musicid=");
            return Json(Serialize(new { code = 0, retcode = 0, lyric }));
        }, CancellationToken.None);
        checks["QQ MID verifies metadata then uses matching songmid endpoint with referer"] =
            result.Item1 == lyric && result.Item2.Contains("QQ音乐") && requests == 2 && referers && requestedMid;

        bool requestedNumeric = false;
        result = await PlatformLyrics.FetchAsync(qq with { MediaId = "201773975" }, (url, _, _) => {
            if (new Uri(url).AbsolutePath.Contains("single_song"))
                return Json(QqDetail());
            requestedNumeric = url.Contains("musicid=201773975") && !url.Contains("songmid=");
            return Json(Serialize(new { code = 0, retcode = 0, lyric }));
        }, CancellationToken.None);
        checks["QQ numeric identifier uses musicid rather than mislabeling it as MID"] =
            result.Item1 == lyric && requestedNumeric;
        foreach (string problem in new[] { "mid", "artist", "duration", "version" })
        {
            lyricRequests = 0;
            result = await PlatformLyrics.FetchAsync(qq, (url, _, _) => {
                if (new Uri(url).AbsolutePath.Contains("single_song"))
                    return Json(QqDetail(mid: problem == "mid" ? "001RaE0n4RrGX9" : qq.MediaId,
                        title: problem == "version" ? "晴天 (Live)" : qq.Title,
                        name: qq.Title, singer: problem == "artist" ? "林俊杰" : qq.Artist,
                        interval: problem == "duration" ? 300 : 269));
                lyricRequests++; return Json(Serialize(new { code = 0, lyric }));
            }, CancellationToken.None);
            checks["QQ mismatched " + problem + " cannot hide behind a matching short song name"] =
                result.Item1.Length == 0 && lyricRequests == 0;
        }
        foreach (string response in new[] { "{}", "[]", "{\"code\":200,\"songs\":[]}", "{\"code\":200,\"songs\":[null]}" })
        {
            result = await PlatformLyrics.FetchAsync(netease, (_, _, _) => Json(response), CancellationToken.None);
            checks["Malformed or empty detail is rejected: " + response] = result.Item1.Length == 0;
        }
        bool nativeRejected = false;
        try {
            await PlatformLyrics.FetchAsync(qq, (url, _, _) => Json(new Uri(url).AbsolutePath.Contains("single_song")
                ? QqDetail() : Serialize(new { code = -1310, retcode = -1310, lyric })), CancellationToken.None);
        } catch (HttpRequestException) { nativeRejected = true; }
        checks["QQ upstream failure code is classified as a source error"] = nativeRejected;
        result = await PlatformLyrics.FetchAsync(netease, (url, _, _) => Json(new Uri(url).AbsolutePath.Contains("/detail")
            ? NetDetail() : "{\"code\":200,\"nolyric\":true}"), CancellationToken.None);
        checks["Missing platform LRC returns empty for cross-source fallback"] = result.Item1.Length == 0;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool cancellationObserved = false;
        try { await PlatformLyrics.FetchAsync(netease, NetGet, cancelled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        checks["Platform lookup preserves caller cancellation"] = cancellationObserved;
        object SearchSong(string id = "123", string title = "晴天", string singer = "周杰伦", long duration = 269000) =>
            new { id, name = title, duration, artists = new[] { new { name = singer } } };
        string SearchResult(params object[] songs) => Serialize(new { code = 200, result = new { songs } });
        var unknown = netease with { PackageName = "other.player", MediaId = "999999999" };
        requests = 0; lyricRequests = 0; bool searchParameters = false; bool wrongIdentifierUsed = false;
        result = await PlatformLyrics.SearchNeteaseAsync(unknown, (url, referer, _) => {
            requests++; referers = referer == "https://music.163.com/";
            if (new Uri(url).AbsolutePath.Contains("/search/"))
            {
                searchParameters = url.Contains("limit=12") && url.Contains("offset=0") && url.Contains("type=1");
                return Json(SearchResult(SearchSong()));
            }
            lyricRequests++; wrongIdentifierUsed |= url.Contains(unknown.MediaId) || url.Contains("/detail");
            return Json(Serialize(new { code = 200, lrc = new { lyric } }));
        }, CancellationToken.None);
        checks["Generic NetEase search uses only returned platform ID without a detail request"] =
            result.Item1 == lyric && requests == 2 && lyricRequests == 1 && searchParameters && referers && !wrongIdentifierUsed;
        foreach (string problem in new[] { "id", "title", "artist", "duration", "version" })
        {
            lyricRequests = 0;
            result = await PlatformLyrics.SearchNeteaseAsync(unknown, (url, _, _) => {
                if (new Uri(url).AbsolutePath.Contains("/search/"))
                    return Json(SearchResult(SearchSong(id: problem == "id" ? "123/fake" : "123",
                        title: problem == "title" ? "江南" : problem == "version" ? "晴天 (Live)" : unknown.Title,
                        singer: problem == "artist" ? "林俊杰" : unknown.Artist,
                        duration: problem == "duration" ? 300000 : unknown.DurationMs)));
                lyricRequests++; return Json(Serialize(new { code = 200, lrc = new { lyric } }));
            }, CancellationToken.None);
            checks["Generic NetEase search rejects mismatched " + problem + " before download"] =
                result.Item1.Length == 0 && lyricRequests == 0;
        }
        lyricRequests = 0;
        result = await PlatformLyrics.SearchNeteaseAsync(unknown, (url, _, _) => {
            if (new Uri(url).AbsolutePath.Contains("/search/"))
                return Json(SearchResult(SearchSong("1"), SearchSong("1"), SearchSong("2"), SearchSong("3"), SearchSong("4")));
            lyricRequests++;
            return Json(Serialize(new { code = 200, lrc = new { lyric = "无同步时间轴" } }));
        }, CancellationToken.None);
        checks["Generic search deduplicates IDs and attempts at most three candidates"] =
            LyricsProvider.ParseLyrics(result.Item1, unknown).Count == 0 && result.Item1 == "无同步时间轴" && lyricRequests == 3;
        foreach (string content in new[] { "", "只有文本没有时间轴", "[ti:晴天 (Live)]\n[ar:周杰伦]\n[00:00]错误现场歌词", "[00:00]\n[00:01]" })
        {
            bool qualityRejected = false;
            try {
                result = await PlatformLyrics.SearchNeteaseAsync(unknown, (url, _, _) =>
                    Json(new Uri(url).AbsolutePath.Contains("/search/") ? SearchResult(SearchSong())
                        : Serialize(new { code = 200, lrc = new { lyric = content } })), CancellationToken.None);
            } catch (UnreliableLyricsException) { qualityRejected = true; }
            checks["Generic search cannot accept unusable lyric content: " + content] =
                qualityRejected || LyricsProvider.ParseLyrics(result.Item1, unknown).Count == 0;
        }
        lyricRequests = 0;
        result = await PlatformLyrics.SearchNeteaseAsync(unknown, (url, _, _) => {
            if (new Uri(url).AbsolutePath.Contains("/search/")) return Json(SearchResult(SearchSong("1"), SearchSong("2")));
            lyricRequests++;
            return Json(Serialize(new { code = 200, lrc = new { lyric = lyricRequests == 1
                ? "[ti:晴天 (Live)]\n[ar:周杰伦]\n[00:00]错版本" : lyric } }));
        }, CancellationToken.None);
        checks["Generic search skips mismatched lyric body for the next verified candidate"] =
            result.Item1 == lyric && lyricRequests == 2;
        lyricRequests = 0;
        result = await PlatformLyrics.SearchNeteaseAsync(unknown, (url, _, _) => {
            if (new Uri(url).AbsolutePath.Contains("/search/")) return Json(SearchResult(SearchSong("1"), SearchSong("2")));
            lyricRequests++;
            if (lyricRequests == 1) throw new HttpRequestException("Fixture download failure");
            return Json(Serialize(new { code = 200, lrc = new { lyric } }));
        }, CancellationToken.None);
        checks["Generic search can use a second recording candidate after one download error"] =
            result.Item1 == lyric && lyricRequests == 2;
        bool searchCancellation = false; requests = 0;
        try { await PlatformLyrics.SearchNeteaseAsync(unknown, (_, _, _) => { requests++; return Json("{}"); }, cancelled.Token); }
        catch (OperationCanceledException) { searchCancellation = true; }
        checks["Generic search honors cancellation before any request"] = searchCancellation && requests == 0;
        requests = 0;
        result = await PlatformLyrics.SearchNeteaseAsync(unknown with { Title = "" },
            (_, _, _) => { requests++; return Json("{}"); }, CancellationToken.None);
        checks["Generic search avoids empty title requests"] = result.Item1.Length == 0 && requests == 0;
        var searchDiagnostics = new List<string>();
        result = await PlatformLyrics.SearchNeteaseAsync(unknown, (url, _, _) =>
            Json(new Uri(url).AbsolutePath.Contains("/search/")
                ? SearchResult(SearchSong("123"), SearchSong("4", singer: "林俊杰"), SearchSong("5", duration: 300000),
                    SearchSong("6", title: "晴天 (Live)"), SearchSong("not-numeric"))
                : Serialize(new { code = 200, lrc = new { lyric } })), CancellationToken.None, message => searchDiagnostics.Add(message));
        string searchDiagnostic = searchDiagnostics.LastOrDefault(message => message.StartsWith("netease-search:", StringComparison.Ordinal)) ?? "";
        checks["Generic search diagnostic counts artist duration version and ID exclusions"] =
            result.Item1 == lyric && searchDiagnostic.Contains("candidates=5; matched=1")
            && searchDiagnostic.Contains("reject-artist=1; reject-duration=1; reject-title-version=1; reject-id=1");
        foreach (string rejected in new[] { "{\"code\":400}", "{\"code\":503}" })
        {
            bool serviceFailure = false;
            try { await PlatformLyrics.SearchNeteaseAsync(unknown, (_, _, _) => Json(rejected), CancellationToken.None); }
            catch (HttpRequestException) { serviceFailure = true; }
            checks["Explicit search service rejection is an error: " + rejected] = serviceFailure;
        }
        foreach (string empty in new[] { "{\"code\":200}", "{\"code\":200,\"result\":{}}" })
        {
            result = await PlatformLyrics.SearchNeteaseAsync(unknown, (_, _, _) => Json(empty), CancellationToken.None);
            checks["Successful empty search remains a normal miss: " + empty] = result.Item1.Length == 0;
        }
        bool downloadRejection = false;
        try {
            await PlatformLyrics.SearchNeteaseAsync(unknown, (url, _, _) =>
                Json(new Uri(url).AbsolutePath.Contains("/search/") ? SearchResult(SearchSong()) : "{\"code\":503}"),
                CancellationToken.None);
        }
        catch (HttpRequestException) { downloadRejection = true; }
        checks["Explicit lyric endpoint rejection cannot be reported as missing lyrics"] = downloadRejection;
        return checks;
    }
}
