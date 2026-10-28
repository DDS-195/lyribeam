using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LyricBridge;

internal static class ProviderRegressionTests
{
    // Every scenario has its own directory and synthetic HTTP transport; no live cache or network is used.
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var song = new MediaSnapshot { Version = 1, Type = "media", Title = "晴天", Artist = "周杰伦", PackageName = "provider-regression", DurationMs = 269000, PositionKnown = true };
        string Area(string name) => Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
        string Candidate(string title, long duration = 269000, string id = "fixture") => JsonSerializer.Serialize(new { candidates = new[] { new { song = title, singer = song.Artist, duration, id, accesskey = "fixture" } } });
        // Transport tests use a representative complete document, not a one-line preview.
        string Timeline(string value) => Lrc.Parse(value).Count == 1 ? value + "\n[01:30]测试中段\n[03:50]测试末段" : value;
        string Download(string value) => JsonSerializer.Serialize(new { content = Convert.ToBase64String(Encoding.UTF8.GetBytes(Timeline(value))) });
        string None(Uri uri) => uri.Host == "lrclib.net" ? "[]" : "{}";
        string Cache(string path) => Path.Combine(path, "lyrics", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(song.TrackKey))) + ".lrc");
        static Task<LyricsResult> Fetch(LyricsProvider provider, MediaSnapshot snapshot, bool bypass, bool network, CancellationToken token = default)
        {
            // The fallback lets the same tests demonstrate the old behavior before the options API exists.
            var method = typeof(LyricsProvider).GetMethod(nameof(LyricsProvider.FetchAsync), new[] { typeof(MediaSnapshot), typeof(CancellationToken), typeof(bool), typeof(bool) });
            return method == null ? provider.FetchAsync(snapshot, token) : (Task<LyricsResult>)method.Invoke(provider, new object[] { snapshot, token, bypass, network })!;
        }
        checks["Provider exposes explicit cache/network policy"] = typeof(LyricsProvider).GetMethod(nameof(LyricsProvider.FetchAsync), new[] { typeof(MediaSnapshot), typeof(CancellationToken), typeof(bool), typeof(bool) }) != null;

        string cacheRoot = Area("cache"); int downloads = 0, requests = 0; bool failDownload = false;
        string content = "[00:00]旧的歌词";
        var provider = new LyricsProvider(cacheRoot, new Fixture((uri, _) => {
            requests++;
            if (uri.Host == "lrclib.net" || uri.Host == "songsearch.kugou.com") return Task.FromResult(None(uri));
            if (uri.AbsolutePath.EndsWith("search")) return Task.FromResult(Candidate(song.Title));
            downloads++;
            if (failDownload) throw new HttpRequestException("Injected download failure", null, HttpStatusCode.ServiceUnavailable);
            return Task.FromResult(Download(content));
        }));
        await provider.FetchAsync(song, CancellationToken.None);
        int before = downloads; content = "[00:00]修正后的歌词";
        var result = await Fetch(provider, song, true, true);
        checks["Forced refresh downloads and replaces matching cache"] = downloads > before && result.Lines.FirstOrDefault()?.Text == "修正后的歌词" && File.ReadAllText(Cache(cacheRoot)).Contains("修正后的歌词");
        string priorCache = File.ReadAllText(Cache(cacheRoot)); before = requests; failDownload = true;
        result = await Fetch(provider, song, true, true);
        checks["Failed forced refresh preserves previous cache"] = result.Lines.Count == 0 && requests > before && File.ReadAllText(Cache(cacheRoot)) == priorCache;
        failDownload = false; content = "[00:00]可用的新下载歌词";
        using (var lockedCache = new FileStream(Cache(cacheRoot), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await Fetch(provider, song, true, true);
            checks["Cache replacement failure preserves old file and usable download"] = result.Lines.FirstOrDefault()?.Text == "可用的新下载歌词" && File.ReadAllText(Cache(cacheRoot)) == priorCache;
        }
        before = requests; result = await Fetch(provider, song, true, false);
        checks["Offline lookup loads disk cache without HTTP"] = result.Lines.Count > 0 && requests == before;
        before = requests; result = await Fetch(provider, song with { Title = "没有缓存的新歌" }, true, false);
        checks["Offline cache miss never sends HTTP and has clear status"] = requests == before && result.Lines.Count == 0 && result.Source.Contains("缓存");

        foreach (string mode in new[] { "unmatched", "plain", "invalid-base64", "empty-lines" })
        {
            int mirror = 0;
            provider = new LyricsProvider(Area(mode), new Fixture((uri, _) => {
                if (uri.Host == "lrclib.net" || uri.Host == "songsearch.kugou.com") return Task.FromResult(None(uri));
                bool alternate = uri.Host == "krcs.kugou.com";
                if (alternate) mirror++;
                if (uri.AbsolutePath.EndsWith("search")) return Task.FromResult(Candidate(song.Title, !alternate && mode == "unmatched" ? 30000 : song.DurationMs));
                return Task.FromResult(alternate ? Download("[00:00]备用入口的有效歌词") : mode switch {
                    "plain" => Download("只有文字，没有时间轴"),
                    "invalid-base64" => "{\"content\":\"not-base64!\"}",
                    "empty-lines" => Download("[00:00]\n[00:01]"),
                    _ => Download("[00:00]短片段")
                });
            }));
            result = await provider.FetchAsync(song, CancellationToken.None);
            checks["Mirror survives primary " + mode + " response"] = mirror > 0 && result.Lines.Any(line => line.Text == "备用入口的有效歌词");
        }
        int alternateRequests = 0;
        provider = new LyricsProvider(Area("fast-primary"), new Fixture((uri, _) => {
            if (uri.Host == "krcs.kugou.com") alternateRequests++;
            if (uri.Host == "lrclib.net" || uri.Host == "songsearch.kugou.com") return Task.FromResult(None(uri));
            return Task.FromResult(uri.AbsolutePath.EndsWith("search") ? Candidate(song.Title) : Download("[00:00]主入口及时完成"));
        }));
        result = await provider.FetchAsync(song, CancellationToken.None); await Task.Delay(250);
        checks["Fast validated primary avoids mirror requests"] = result.Lines.Count > 0 && alternateRequests == 0;
        var stall = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider = new LyricsProvider(Area("slow-primary"), new Fixture(async (uri, token) => {
            if (uri.Host == "lrclib.net" || uri.Host == "songsearch.kugou.com") return None(uri);
            if (uri.Host == "lyrics.kugou.com") return await stall.Task.WaitAsync(token);
            return uri.AbsolutePath.EndsWith("search") ? Candidate(song.Title) : Download("[00:00]备用入口及时完成");
        }));
        var fetching = provider.FetchAsync(song, CancellationToken.None);
        bool completedQuickly = await Task.WhenAny(fetching, Task.Delay(900)) == fetching;
        stall.TrySetResult("{}"); result = await fetching;
        checks["Stalled primary still resolves through delayed mirror promptly"] = completedQuickly && result.Lines.Count > 0;

        checks["Version suffixes and arbitrary title substrings are rejected"] = new[] { "晴天 (Live)", "晴天 (DJ版)", "晴天 (翻唱)", "晴天 (Cover)", "晴天 (Remix)", "晴天 (伴奏)", "晴天以后" }.All(title => LyricsProvider.Score(song, title, song.Artist, song.DurationMs) < 65);
        checks["Matching version and punctuation variations remain accepted"] = LyricsProvider.Score(song with { Title = "晴天 (Live)" }, "晴天 [Live]", song.Artist, song.DurationMs) >= 65;
        checks["Existing qualified artist aliases remain accepted"] = LyricsProvider.Score(song with { Artist = "G.E.M.邓紫棋" }, song.Title, "邓紫棋", song.DurationMs) >= 65;
        checks["Explicit original-version labels remain equivalent"] = LyricsProvider.Score(song with { Title = "HEAVEN (Original Version)" }, "HEAVEN", song.Artist, song.DurationMs) >= 65;
        checks["Omitted language label requires matching duration and singer"] = LyricsProvider.Score(song with { Title = "K歌之王 (粤语版)" }, "K歌之王", song.Artist, song.DurationMs) >= 65 && LyricsProvider.Score(song with { Title = "K歌之王 (粤语版)" }, "K歌之王", song.Artist, 0) < 65 && LyricsProvider.Score(song with { Title = "K歌之王 (粤语版)" }, "K歌之王 (国语版)", song.Artist, song.DurationMs) < 65;
        provider = new LyricsProvider(Area("lrclib-version"), new Fixture((uri, _) => Task.FromResult(uri.Host == "lrclib.net"
            ? JsonSerializer.Serialize(new[] { new { trackName = "晴天 (Live)", artistName = song.Artist, duration = 269, syncedLyrics = "[00:00]错误的现场版本" } }) : "{}")));
        result = await provider.FetchAsync(song, CancellationToken.None);
        checks["LRCLIB cannot persist a different recording version"] = result.Lines.Count == 0;
        provider = new LyricsProvider(Area("download-failure"), new Fixture((uri, _) => {
            if (uri.Host == "lrclib.net" || uri.Host == "songsearch.kugou.com") return Task.FromResult(None(uri));
            if (uri.AbsolutePath.EndsWith("search")) return Task.FromResult(Candidate(song.Title));
            throw new HttpRequestException("Injected download failure", null, HttpStatusCode.ServiceUnavailable);
        }));
        result = await provider.FetchAsync(song, CancellationToken.None);
        checks["All candidate download transport failures report a service error"] = result.Lines.Count == 0 && result.Source.Contains("无法连接");

        // Search metadata can look right while the downloaded LRC belongs to another recording.
        string goodHeader = "[ti:晴天]\n[ar:周杰伦]\n[00:00]正确原版\n[02:00]继续同步";
        string badHeader = "[ti:晴天 (Live)]\n[ar:其他歌手/周杰伦]\n[00:00]错误现场版";
        string bodyRoot = Area("body-headers");
        provider = new LyricsProvider(bodyRoot, new Fixture((uri, _) => {
            if (uri.Host == "lrclib.net" || uri.Host == "songsearch.kugou.com") return Task.FromResult(None(uri));
            if (uri.AbsolutePath.EndsWith("search")) return Task.FromResult(JsonSerializer.Serialize(new { candidates = new[] {
                new { song = song.Title, singer = song.Artist, duration = song.DurationMs, id = "bad", accesskey = "fixture" },
                new { song = song.Title, singer = song.Artist, duration = song.DurationMs, id = "good", accesskey = "fixture" }
            }}));
            return Task.FromResult(Download(uri.Query.Contains("id=bad") ? badHeader : goodHeader));
        }));
        result = await provider.FetchAsync(song, CancellationToken.None);
        checks["Downloaded recording headers override misleading search metadata"] = result.Lines.Any(l => l.Text == "正确原版") && result.Lines.All(l => l.Text != "错误现场版");

        string badCacheRoot = Area("mislabeled-cache");
        Directory.CreateDirectory(Path.Combine(badCacheRoot,"lyrics")); File.WriteAllText(Cache(badCacheRoot),badHeader);
        int badCacheRequests = 0;
        provider = new LyricsProvider(badCacheRoot,new Fixture((uri, _) => {
            badCacheRequests++;
            return Task.FromResult(uri.Host == "lrclib.net" || uri.Host == "songsearch.kugou.com" ? None(uri)
                : uri.AbsolutePath.EndsWith("search") ? Candidate(song.Title) : Download(goodHeader));
        }));
        result = await Fetch(provider,song,false,false);
        checks["Offline cache cannot display explicitly wrong recording"] = result.Lines.Count == 0 && badCacheRequests == 0 && File.ReadAllText(Cache(badCacheRoot)) == badHeader;
        result = await provider.FetchAsync(song,CancellationToken.None);
        checks["Incorrect cached recording is automatically replaced online"] = result.Lines.Any(l => l.Text == "正确原版") && File.ReadAllText(Cache(badCacheRoot)) == goodHeader;

        provider = new LyricsProvider(Area("wrong-body-artist"),new Fixture((uri, _) => Task.FromResult(uri.Host == "lrclib.net"
            ? JsonSerializer.Serialize(new[] {new {trackName=song.Title,artistName=song.Artist,duration=269,syncedLyrics="[ti:晴天]\n[ar:另一位歌手]\n[00:00]错误歌手"}}) : "{}")));
        result = await provider.FetchAsync(song,CancellationToken.None);
        checks["Lyrics body cannot silently substitute another singer"] = result.Lines.Count == 0;


        int directCalls = 0, proxyKugouCalls = 0, lrclibProxyCalls = 0;
        Task<string> RouteReply(Uri uri) => Task.FromResult(uri.Host == "lrclib.net" || uri.Host == "songsearch.kugou.com"
            ? None(uri) : uri.AbsolutePath.EndsWith("search") ? Candidate(song.Title) : Download("[00:00]直连歌词"));
        provider = new LyricsProvider(Area("direct-route"),
            new Fixture((uri, _) => { if (uri.Host == "lrclib.net") lrclibProxyCalls++; else proxyKugouCalls++; return RouteReply(uri); }),
            new Fixture((uri, _) => { directCalls++; if (uri.Host == "lrclib.net") throw new InvalidOperationException("Foreign service must retain system route"); return RouteReply(uri); }));
        result = await provider.FetchAsync(song,CancellationToken.None); await Task.Delay(750);
        checks["Fast domestic route avoids unnecessary proxy requests"] = result.Lines.Any(l => l.Text == "直连歌词") && directCalls > 0 && proxyKugouCalls == 0;
        checks["Foreign lyric service retains the system network route"] = lrclibProxyCalls > 0;

        int proxyFallback = 0, cancelledDirect = 0;
        provider = new LyricsProvider(Area("direct-stalled"),
            new Fixture((uri, _) => { if (uri.Host != "lrclib.net") proxyFallback++; return RouteReply(uri); }),
            new Fixture(async (uri, token) => {
                try { await Task.Delay(Timeout.Infinite,token); return "{}"; }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelledDirect); throw; }
            }));
        fetching = provider.FetchAsync(song,CancellationToken.None);
        bool routeFallbackQuickly = await Task.WhenAny(fetching,Task.Delay(2500)) == fetching;
        result = await fetching; await Task.Delay(50);
        checks["Blocked direct route falls back and cancels abandoned requests"] = routeFallbackQuickly && result.Lines.Count > 0 && proxyFallback > 0 && cancelledDirect > 0;


        checks["Singer prefixes are not aliases"] = LyricsProvider.Score(song with {Artist="Sam"},song.Title,"Samantha",song.DurationMs)<65;
        checks["Additional credited singers do not silently substitute a recording"] = LyricsProvider.Score(song,song.Title,"周杰伦/另一歌手",song.DurationMs)<65;
        checks["Known durations must agree within rounding tolerance"] = LyricsProvider.Score(song,song.Title,song.Artist,song.DurationMs+8000)<65;
        var korean=song with {Title="밤편지",Artist="IU",DurationMs=253000};
        checks["Translated title and singer aliases are accepted with corroborating duration"] = LyricsProvider.Score(korean,"밤편지 (夜信)","아이유 (IU)",253000)>=65;
        checks["Translated aliases cannot erase a Live qualifier"] = LyricsProvider.Score(korean,"밤편지 (夜信) (Live)","아이유 (IU)",253000)<65;
        checks["Same-language bracket text is not treated as an alternate title"] = LyricsProvider.Score(song,"晴天 (特别版)",song.Artist,song.DurationMs)<65;
        checks["Recording identifiers isolate cache keys"] = (song with {MediaId="1"}).TrackKey != (song with {MediaId="2"}).TrackKey && song.TrackKey==song.LegacyTrackKey;
        var parsedId=MediaSnapshot.Parse("{\"version\":1,\"type\":\"idle\",\"mediaId\":null}");
        checks["Missing optional media identifier remains compatible"] = parsedId.MediaId=="";
        bool oversizedId=false;try{MediaSnapshot.Parse("{\"version\":1,\"type\":\"idle\",\"mediaId\":\""+new string('x',257)+"\"}");}catch(FormatException){oversizedId=true;}
        checks["Unbounded media identifiers are rejected"] = oversizedId;

        bool keywordWithoutId=false,wrongHashUsed=false;
        string rightHash=new string('A',32),wrongHash=new string('B',32);
        var identifiedSong=song with {PackageName="com.kugou.android.lite",MediaId="4242"};
        provider=new LyricsProvider(Area("recording-identifier"),new Fixture((uri,_)=>{
            if(uri.Host=="lrclib.net")return Task.FromResult("[]");
            if(uri.Host=="songsearch.kugou.com")return Task.FromResult(JsonSerializer.Serialize(new{data=new{lists=new[]{
                new{ID="1111",MixSongID="1111",FileHash=wrongHash,SongName=song.Title,SingerName=song.Artist,Duration=269},
                new{ID="4242",MixSongID="4242",FileHash=rightHash,SongName=song.Title,SingerName=song.Artist,Duration=269}
            }}}));
            if(uri.AbsolutePath.EndsWith("search")){
                keywordWithoutId|=!uri.Query.Contains("hash="+rightHash);
                wrongHashUsed|=uri.Query.Contains("hash="+wrongHash);
                return Task.FromResult(Candidate(song.Title));
            }
            return Task.FromResult(Download(goodHeader));
        }));
        result=await provider.FetchAsync(identifiedSong,CancellationToken.None);
        checks["Exact media identifier takes precedence over identical title recordings"] = result.Lines.Count>0 && result.Source.Contains("歌曲编号") && !keywordWithoutId && !wrongHashUsed;

        var stalledCandidate=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider=new LyricsProvider(Area("slow-first-candidate"),new Fixture(async(uri,token)=>{
            if(uri.Host=="lrclib.net"||uri.Host=="songsearch.kugou.com")return None(uri);
            if(uri.AbsolutePath.EndsWith("search"))return JsonSerializer.Serialize(new{candidates=new[]{
                new{song=song.Title,singer=song.Artist,duration=song.DurationMs,id="slow",accesskey="x"},
                new{song=song.Title,singer=song.Artist,duration=song.DurationMs,id="fast",accesskey="x"}
            }});
            return uri.Query.Contains("id=slow") ? await stalledCandidate.Task.WaitAsync(token) : Download(goodHeader);
        }));
        fetching=provider.FetchAsync(song,CancellationToken.None);
        bool fastCandidateWins=await Task.WhenAny(fetching,Task.Delay(900))==fetching;
        stalledCandidate.TrySetResult("{}");result=await fetching;
        checks["One slow candidate cannot block another validated recording"] = fastCandidateWins && result.Lines.Any(l=>l.Text=="正确原版");


        // The foreign lookup is deliberately faster than the authoritative recording-ID lookup.
        // Holding its result proves priority applies to completed responses, not just launch order.
        foreach (bool foreignInstrumental in new[] { false, true })
        {
            provider = new LyricsProvider(Area("identifier-priority-" + foreignInstrumental), new Fixture(async (uri, token) => {
                if (uri.Host == "lrclib.net")
                    return JsonSerializer.Serialize(new[] { new { trackName = song.Title, artistName = song.Artist, duration = 269,
                        syncedLyrics = foreignInstrumental ? null : Timeline("[00:00]同名搜索结果"), instrumental = foreignInstrumental } });
                if (uri.Host == "songsearch.kugou.com")
                {
                    await Task.Delay(1100, token);
                    return JsonSerializer.Serialize(new { data = new { lists = new[] {
                        new { ID = identifiedSong.MediaId, MixSongID = identifiedSong.MediaId, FileHash = rightHash,
                            SongName = song.Title, SingerName = song.Artist, Duration = 269 }
                    } } });
                }
                return uri.AbsolutePath.EndsWith("search") ? Candidate(song.Title) : Download("[00:00]编号确认的歌词");
            }));
            result = await provider.FetchAsync(identifiedSong, CancellationToken.None);
            checks["Fast " + (foreignInstrumental ? "instrumental" : "name-only") + " result cannot beat a resolving exact identifier"] =
                result.Source.Contains("歌曲编号") && result.Lines.Any(line => line.Text == "编号确认的歌词") && !result.IsInstrumental;
        }

        provider = new LyricsProvider(Area("identifier-priority-bounded"), new Fixture(async (uri, token) => {
            if (uri.Host == "lrclib.net")
                return JsonSerializer.Serialize(new[] { new { trackName = song.Title, artistName = song.Artist, duration = 269, syncedLyrics = Timeline("[00:00]有界备用结果") } });
            if (uri.Host == "songsearch.kugou.com")
            {
                await Task.Delay(Timeout.Infinite, token);
                return "{}";
            }
            return "{}";
        }));
        var priorityWatch = System.Diagnostics.Stopwatch.StartNew();
        result = await provider.FetchAsync(identifiedSong, CancellationToken.None);
        checks["Unresponsive identifier lookup does not indefinitely delay an available fallback"] =
            priorityWatch.ElapsedMilliseconds < 3300 && result.Source == "LRCLIB" && result.Lines.Any(line => line.Text == "有界备用结果");

        provider = new LyricsProvider(Area("instrumental-result"), new Fixture((uri, _) => Task.FromResult(uri.Host == "lrclib.net"
            ? JsonSerializer.Serialize(new[] { new { trackName = song.Title, artistName = song.Artist, duration = 269, instrumental = true, syncedLyrics = (string?)null } })
            : "{}")));
        result = await provider.FetchAsync(song, CancellationToken.None);
        checks["Confirmed instrumental result survives the outer source race"] = result.IsInstrumental && result.Lines.Count == 0 && result.Source == "纯音乐";

        return checks;
    }
    private sealed class Fixture(Func<Uri, CancellationToken, Task<string>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => new(HttpStatusCode.OK) { Content = new StringContent(await response(request.RequestUri!, token)) };
    }
}
