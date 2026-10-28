using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace LyricBridge;

internal static class NativeRoutingRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var song = new MediaSnapshot {
            Version = 1, Type = "media", Title = "测试作品", Artist = "测试歌手", Album = "版本专辑",
            DurationMs = 180000, PositionKnown = true
        };
        string Area(string name) => Path.Combine(root, "native-routing-" + name + "-" + Guid.NewGuid().ToString("N"));
        foreach (string package in new[] { "com.netease.cloudmusic", "com.tencent.qqmusic" })
        {
            string platform = package == "com.netease.cloudmusic" ? "网易云" : "QQ音乐";
            var current = song with { PackageName = package, MediaId = "" };
            string nativeLyrics = Lyrics(current, "原平台");
            string alternateLyrics = Lyrics(current, "备用来源");
            int nativeRequests = 0, nativeSearches = 0, alternateRequests = 0;
            var provider = new LyricsProvider(Area(platform + "-metadata-priority"), new Fixture(async (uri, token) => {
                if (uri.Host == "lrclib.net") {
                    Interlocked.Increment(ref alternateRequests);
                    await Task.Delay(25, token);
                    return Library(current, alternateLyrics);
                }
                if (!IsNative(uri, package)) return Empty(uri);
                Interlocked.Increment(ref nativeRequests);
                if (IsSearch(uri, package)) {
                    Interlocked.Increment(ref nativeSearches);
                    await Task.Delay(300, token);
                    return Search(current, package);
                }
                return NativeLyric(nativeLyrics, package);
            }));
            var watch = Stopwatch.StartNew();
            var result = await provider.FetchAsync(current, CancellationToken.None);
            checks["Native metadata search wins over an earlier cross-platform result without ID: " + package] =
                nativeRequests >= 2 && result.Source.Contains(platform) && result.Lines.Count == 4
                && result.Lines[0].Text == "原平台第一句" && watch.ElapsedMilliseconds < 3500;
            checks["Fallback is prepared concurrently during native priority: " + package] = alternateRequests > 0;
            checks["Native metadata search is not duplicated by an additional fallback: " + package] = nativeSearches == 1;

            nativeRequests = 0;
            provider = new LyricsProvider(Area(platform + "-empty-release"), new Fixture(async (uri, token) => {
                if (uri.Host == "lrclib.net") { await Task.Delay(30, token); return Library(current, alternateLyrics); }
                if (IsNative(uri, package)) Interlocked.Increment(ref nativeRequests);
                return Empty(uri);
            }));
            watch.Restart(); result = await provider.FetchAsync(current, CancellationToken.None);
            checks["An empty native catalogue releases an already available fallback promptly: " + package] =
                nativeRequests > 0 && result.Lines.Count == 4 && result.Lines[0].Text == "备用来源第一句"
                && watch.ElapsedMilliseconds < 1200;

            nativeRequests = 0;
            provider = new LyricsProvider(Area(platform + "-error-release"), new Fixture(async (uri, token) => {
                if (uri.Host == "lrclib.net") { await Task.Delay(30, token); return Library(current, alternateLyrics); }
                if (IsNative(uri, package)) {
                    Interlocked.Increment(ref nativeRequests);
                    throw new HttpRequestException("Native fixture denied the request", null, HttpStatusCode.Forbidden);
                }
                return Empty(uri);
            }));
            watch.Restart(); result = await provider.FetchAsync(current, CancellationToken.None);
            checks["A native service failure promptly releases a valid cross-platform fallback: " + package] =
                nativeRequests > 0 && result.Lines.Count == 4 && result.Lines[0].Text == "备用来源第一句"
                && watch.ElapsedMilliseconds < 1200;

            nativeRequests = 0;
            provider = new LyricsProvider(Area(platform + "-bounded-priority"), new Fixture(async (uri, token) => {
                if (uri.Host == "lrclib.net") { await Task.Delay(25, token); return Library(current, alternateLyrics); }
                if (IsNative(uri, package)) {
                    Interlocked.Increment(ref nativeRequests);
                    await Task.Delay(Timeout.Infinite, token);
                }
                return Empty(uri);
            }));
            watch.Restart(); result = await provider.FetchAsync(current, CancellationToken.None);
            checks["A hung native source has a bounded preference window rather than blocking ready lyrics: " + package] =
                nativeRequests > 0 && result.Lines.Count == 4 && result.Lines[0].Text == "备用来源第一句"
                && watch.ElapsedMilliseconds >= 1400 && watch.ElapsedMilliseconds < 3500;

            int nativeLyricRequests = 0;
            provider = new LyricsProvider(Area(platform + "-wrong-live"), new Fixture(async (uri, token) => {
                if (uri.Host == "lrclib.net") { await Task.Delay(25, token); return Library(current, alternateLyrics); }
                if (!IsNative(uri, package)) return Empty(uri);
                if (IsSearch(uri, package)) return Search(current with { Title = current.Title + " (Live)" }, package);
                Interlocked.Increment(ref nativeLyricRequests);
                return NativeLyric(nativeLyrics, package);
            }));
            result = await provider.FetchAsync(current, CancellationToken.None);
            checks["A same-platform Live recording cannot displace a verified studio fallback: " + package] =
                nativeLyricRequests == 0 && result.Lines.Count == 4 && result.Lines[0].Text == "备用来源第一句";
        }

        foreach (string package in new[] { "com.netease.cloudmusic", "com.tencent.qqmusic" })
        {
            bool qq = package == "com.tencent.qqmusic";
            var current = song with { PackageName = package, MediaId = "4242" };
            string Detail(bool present) => qq
                ? JsonSerializer.Serialize(new { code = 0, data = present ? new[] { new { id = 4242, title = song.Title, interval = 180,
                    album = new { name = song.Album }, singer = new[] { new { name = song.Artist } } } } : new object[0] })
                : JsonSerializer.Serialize(new { code = 200, songs = present ? new[] { new { id = 4242, name = song.Title, duration = 180000,
                    album = new { name = song.Album }, artists = new[] { new { name = song.Artist } } } } : new object[0] });
            bool IsDetail(Uri uri) => uri.AbsolutePath.Contains("/detail") || uri.AbsolutePath.Contains("single_song");
            int searches = 0;
            var provider = new LyricsProvider(Area("exact-before-metadata-" + package), new Fixture(async (uri, token) => {
                if (uri.Host == "lrclib.net") { await Task.Delay(25, token); return Library(current, Lyrics(current, "跨平台")); }
                if (!IsNative(uri, package)) return Empty(uri);
                if (IsDetail(uri)) { await Task.Delay(150, token); return Detail(true); }
                if (IsSearch(uri, package)) { Interlocked.Increment(ref searches); return Search(current, package); }
                bool exact = uri.Query.Contains("=4242", StringComparison.Ordinal);
                if (exact) await Task.Delay(150, token);
                return NativeLyric(Lyrics(current, exact ? "编号" : "元数据"), package);
            }));
            var result = await provider.FetchAsync(current, CancellationToken.None);
            checks["A validated native ID outranks a metadata candidate that completes first: " + package] =
                searches == 1 && result.Source.Contains("编号") && result.Lines[0].Text == "编号第一句";
            provider = new LyricsProvider(Area("id-miss-metadata-" + package), new Fixture(async (uri, token) => {
                if (uri.Host == "lrclib.net") { await Task.Delay(25, token); return Library(current, Lyrics(current, "跨平台")); }
                if (!IsNative(uri, package)) return Empty(uri);
                if (IsDetail(uri)) return Detail(false);
                if (IsSearch(uri, package)) { await Task.Delay(80, token); return Search(current, package); }
                return NativeLyric(Lyrics(current, "原平台补查"), package);
            }));
            var watch = Stopwatch.StartNew(); result = await provider.FetchAsync(current, CancellationToken.None);
            checks["A missing native ID immediately starts metadata search and keeps its platform preference: " + package] =
                result.Lines[0].Text == "原平台补查第一句" && watch.ElapsedMilliseconds < 1200;
        }
        var kugou = song with { PackageName = "com.kugou.android.lite" };
        var kugouProvider = new LyricsProvider(Area("kugou-no-id"), new Fixture(async (uri, token) => {
            if (uri.Host == "lrclib.net") { await Task.Delay(25, token); return Library(kugou, Lyrics(kugou, "跨平台")); }
            if (uri.Host == "lyrics.kugou.com" && uri.AbsolutePath.EndsWith("search")) {
                await Task.Delay(300, token);
                return JsonSerializer.Serialize(new { candidates = new[] { new { song = song.Title, singer = song.Artist,
                    duration = 180000, id = "fixture", accesskey = "fixture" } } });
            }
            if (uri.Host == "lyrics.kugou.com") return JsonSerializer.Serialize(new { content = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(Lyrics(kugou, "酷狗原平台"))) });
            return Empty(uri);
        }));
        var kugouResult = await kugouProvider.FetchAsync(kugou, CancellationToken.None);
        checks["Kugou without an ID also prefers its valid native lyrics over an earlier generic result"] =
            kugouResult.Source.Contains("酷狗") && kugouResult.Lines[0].Text == "酷狗原平台第一句";
        int lookalikeNativeRequests = 0;
        var unknown = song with { PackageName = "com.tencent.qqmusic.fake", MediaId = "4242" };
        var unknownProvider = new LyricsProvider(Area("unknown-package"), new Fixture(async (uri, token) => {
            if (uri.Host == "lrclib.net") { await Task.Delay(25, token); return Library(unknown, Lyrics(unknown, "通用")); }
            if (uri.Host is "c.y.qq.com" or "u.y.qq.com") Interlocked.Increment(ref lookalikeNativeRequests);
            return Empty(uri);
        }));
        var unknownWatch = Stopwatch.StartNew();
        var unknownResult = await unknownProvider.FetchAsync(unknown, CancellationToken.None);
        checks["An unknown or lookalike app keeps the generic fast path without using its numeric ID as QQ identity"] =
            unknownResult.Lines.Count == 4 && unknownResult.Lines[0].Text == "通用第一句"
            && lookalikeNativeRequests == 0 && unknownWatch.ElapsedMilliseconds < 1200;

        var oldSong = song with { PackageName = "com.netease.cloudmusic" };
        var nextSong = oldSong with { Title = "另一首作品" };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int oldNativeCancelled = 0;
        var switchingProvider = new LyricsProvider(Area("switch-cancellation"), new Fixture(async (uri, token) => {
            string query = Uri.UnescapeDataString(uri.Query);
            bool next = query.Contains(nextSong.Title, StringComparison.Ordinal);
            if (uri.Host == "music.163.com" && IsSearch(uri, oldSong.PackageName)) {
                if (next) return Search(nextSong, oldSong.PackageName);
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { Interlocked.Increment(ref oldNativeCancelled); throw; }
            }
            if (uri.Host == "music.163.com" && uri.AbsolutePath.Contains("lyric", StringComparison.Ordinal))
                return NativeLyric(Lyrics(nextSong, "新曲"), oldSong.PackageName);
            if (uri.Host == "lrclib.net") {
                if (next) return Library(nextSong, Lyrics(nextSong, "新曲备用"));
                await Task.Delay(Timeout.Infinite, token);
            }
            return Empty(uri);
        }));
        using (var lookup = new LyricLookup((s, token) => switchingProvider.FetchAsync(s, token)))
        {
            lookup.Observe(oldSong); var oldPoll = lookup.PollAsync(true);
            bool oldStarted = await Task.WhenAny(entered.Task, Task.Delay(1200)) == entered.Task;
            var switchWatch = Stopwatch.StartNew();
            lookup.Observe(nextSong); await lookup.PollAsync(true); await oldPoll;
            checks["Changing tracks cancels an in-flight native preference wait and only installs the new recording"] =
                oldStarted && oldNativeCancelled > 0 && switchWatch.ElapsedMilliseconds < 1200
                && lookup.Active?.Title == nextSong.Title && lookup.Result.Lines.Count == 4
                && lookup.Result.Lines[0].Text.StartsWith("新曲", StringComparison.Ordinal);
        }
        return checks;
    }

    private static string Lyrics(MediaSnapshot song, string marker) =>
        "[ti:" + song.Title + "]\n[ar:" + song.Artist + "]\n[al:" + song.Album + "]\n"
        + "[00:00]" + marker + "第一句\n[00:45]" + marker + "第二句\n[01:30]" + marker + "第三句\n[02:40]" + marker + "第四句";
    private static string Library(MediaSnapshot song, string lyric) => JsonSerializer.Serialize(new[] {
        new { trackName = song.Title, artistName = song.Artist, albumName = song.Album,
            duration = song.DurationMs / 1000.0, syncedLyrics = lyric, instrumental = false }
    });
    private static bool IsNative(Uri uri, string package) => package == "com.netease.cloudmusic"
        ? uri.Host == "music.163.com" : uri.Host is "c.y.qq.com" or "u.y.qq.com";
    private static bool IsSearch(Uri uri, string package) => package == "com.netease.cloudmusic"
        ? uri.AbsolutePath.Contains("/search/", StringComparison.Ordinal) : uri.AbsolutePath.Contains("/soso/", StringComparison.Ordinal);
    private static string Search(MediaSnapshot song, string package) => package == "com.netease.cloudmusic"
        ? JsonSerializer.Serialize(new { code = 200, result = new { songs = new[] {
            new { id = "900100", name = song.Title, duration = song.DurationMs,
                artists = new[] { new { name = song.Artist } }, album = new { name = song.Album } }
        } } })
        : JsonSerializer.Serialize(new { code = 0, data = new { song = new { list = new[] {
            new { songid = 900100, songmid = "0039MnYb0qxYhV", songname = song.Title,
                interval = song.DurationMs / 1000, albumname = song.Album, singer = new[] { new { name = song.Artist } } }
        } } } });
    private static string NativeLyric(string content, string package) => package == "com.netease.cloudmusic"
        ? JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = content } })
        : JsonSerializer.Serialize(new { code = 0, retcode = 0, lyric = content });
    private static string Empty(Uri uri) => uri.Host == "lrclib.net" ? "[]"
        : uri.Host == "music.163.com" ? "{\"code\":200,\"result\":{\"songs\":[]}}"
        : uri.Host is "c.y.qq.com" or "u.y.qq.com" ? "{\"code\":0,\"data\":{\"song\":{\"list\":[]}}}"
        : "{}";
    private sealed class Fixture(Func<Uri, CancellationToken, Task<string>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await response(request.RequestUri!, token)) };
    }
}
