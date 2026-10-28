using System.Text.Json;
namespace LyricBridge;
internal static class RegressionTests
{
    public static async Task<int> RunAsync(string path)
    {
        var checks = new Dictionary<string, bool>();
        var song = new MediaSnapshot { Version = 1, Type = "media", Title = "测试", Artist = "本地", PackageName = "test", DurationMs = 180000, PositionKnown = true };
        var good = new LyricsResult(Lrc.Parse("[00:00]可用歌词"), "故障注入");
        double now = 0; int requests = 0;
        using (var lookup = new LyricLookup((_, _) => Task.FromResult(++requests == 1 ? new LyricsResult(new(), "网络暂时失败") : good), () => now))
        {
            lookup.Observe(song); await lookup.PollAsync(true); now = 4; await lookup.PollAsync(true);
            checks["Automatic recovery after first lookup fails"] = lookup.Result.Lines.Count > 0 && requests == 2;
            int before = requests; now = 1000; await lookup.PollAsync(true);
            checks["Successful lyrics do not trigger repeated network requests"] = requests == before;
        }
        requests = 0;
        using (var lookup = new LyricLookup((_, _) => Task.FromResult(++requests == 1 ? good : new LyricsResult(new(), "暂时没找到"))))
        {
            lookup.Observe(song); await lookup.PollAsync(true); await lookup.RefreshAsync();
            checks["Failed refresh preserves working lyrics"] = lookup.Result.Lines.Count == good.Lines.Count;
        }
        requests = 0;
        using (var lookup = new LyricLookup((_, _) => { requests++; return Task.FromResult(good); }))
        {
            lookup.Observe(song); await lookup.PollAsync(true);
            lookup.Observe(new MediaSnapshot { Type = "idle" }); lookup.Observe(song with { DurationMs = 0 });
            lookup.Observe(song with { DurationMs = 180900 }); await lookup.PollAsync(true);
            checks["Transient media gaps and duration updates preserve lyrics"] = lookup.Result.Lines.Count > 0 && requests == 1;
        }
        var pending = new TaskCompletionSource<LyricsResult>();
        using (var lookup = new LyricLookup((s, _) => s.Title == "测试" ? pending.Task : Task.FromResult(new LyricsResult(Lrc.Parse("[00:00]新歌"), "新歌"))))
        {
            lookup.Observe(song); var oldRequest = lookup.PollAsync(true);
            lookup.Observe(song with { Title = "另一首" }); await lookup.PollAsync(true);
            pending.SetResult(good); await oldRequest;
            checks["Late response from old track cannot overwrite new track"] = lookup.Result.Source == "新歌";
        }
        var parsed = Lrc.Parse("[offset:-100]\n[00:01.200]這份愛與音樂，Hello 2026!\n[00:02.400]明日は晴れる");
        checks["Traditional Chinese lyrics display as simplified"] = parsed[0].Text == "这份爱与音乐，Hello 2026!";
        checks["Chinese conversion preserves timestamps and Japanese"] = parsed[0].TimeMs == 1100 && parsed[1].Text == "明日は晴れる";
        checks["Traditional titles match simplified source metadata"] = LyricsProvider.Score(song with { Title = "記憶", Artist = "聲音" }, "记忆", "声音", 180000) >= 65;
        string fixtures = Path.Combine(Path.GetDirectoryName(path)!, "provider-fixture-" + Guid.NewGuid().ToString("N"));
        string cacheDir = Path.Combine(fixtures, "lyrics"); Directory.CreateDirectory(cacheDir);
        string cacheFile = Path.Combine(cacheDir, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(song.TrackKey))) + ".lrc");
        Directory.CreateDirectory(cacheFile);
        var provider = new LyricsProvider(fixtures, new FixtureHandler(uri =>
            uri.AbsolutePath.EndsWith("search") ? JsonSerializer.Serialize(new { candidates = new[] { new { song = "测试", singer = "本地", duration = 180000, id = "1", accesskey = "fixture" } } })
            : JsonSerializer.Serialize(new { content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(CompleteTimeline("[00:00]模擬歌詞"))) })));
        var result = await provider.FetchAsync(song, CancellationToken.None);
        checks["Cache write failure does not discard downloaded lyrics"] = result.Lines.Count == 2 && result.Lines[0].Text == "模拟歌词";
        provider = new LyricsProvider(fixtures + "-plain", new FixtureHandler(uri =>
            uri.Host == "lrclib.net" ? JsonSerializer.Serialize(new[] { new { trackName = "测试", artistName = "本地", duration = 180, syncedLyrics=CompleteTimeline("[00:00]後續歌詞") } })
            : uri.AbsolutePath.EndsWith("search") ? JsonSerializer.Serialize(new { candidates = new[] { new { song = "测试", singer = "本地", duration = 180000, id = "1", accesskey = "fixture" } } })
            : JsonSerializer.Serialize(new { content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("只有纯文本")) })));
        result = await provider.FetchAsync(song, CancellationToken.None);
        checks["Plain text from first source does not hide synced fallback"] = result.Lines.Count == 2 && result.Source == "LRCLIB";
        int variantQueries = 0;
        provider = new LyricsProvider(fixtures + "-variants", new FixtureHandler(uri =>
        {
            if (uri.Host != "lrclib.net") return "{\"candidates\":[]}";
            variantQueries++;
            return Uri.UnescapeDataString(uri.Query).Contains("記憶") ? "[]" : JsonSerializer.Serialize(new[] { new { trackName = "记忆", artistName = "声音", duration = 180, syncedLyrics=CompleteTimeline("[00:00]簡體搜尋") } });
        }));
        result = await provider.FetchAsync(song with { Title = "記憶", Artist = "聲音" }, CancellationToken.None);
        checks["Traditional metadata retries lookup using simplified names"] = result.Lines.Count == 2 && variantQueries == 2;
        requests = 0; now = 0;
        using (var lookup = new LyricLookup((_, _) => { requests++; return Task.FromResult(new LyricsResult(new(), "暂时失败")); }, () => now))
        {
            lookup.Observe(song); await lookup.PollAsync(false);
            bool offline = requests == 0;
            await lookup.PollAsync(true); now = 1; await lookup.PollAsync(true);
            checks["Retries wait for backoff and honor offline setting"] = offline && requests == 1;
        }
        provider = new LyricsProvider(fixtures + "-keyword", new FixtureHandler(uri =>
        {
            if (uri.Host == "lrclib.net") return "[]";
            if (uri.AbsolutePath.EndsWith("search"))
                return Uri.UnescapeDataString(uri.Query).Contains("keyword=本地 - 测试")
                    ? JsonSerializer.Serialize(new { candidates = new[] { new { song = "测试", singer = "本地", duration = 180000, id = "keyword", accesskey = "fixture" } } })
                    : "{\"candidates\":[]}";
            return JsonSerializer.Serialize(new { content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(CompleteTimeline("[00:00]关键词匹配"))) });
        }));
        result = await provider.FetchAsync(song, CancellationToken.None);
        checks["Kugou search uses singer dash title keyword"] = result.Lines.Count == 2;

        const string correctHash = "0123456789ABCDEF0123456789ABCDEF";
        bool resolvedByHash = false, wrongDownload = false;
        provider = new LyricsProvider(fixtures + "-hash", new FixtureHandler(uri =>
        {
            if (uri.Host == "lrclib.net") return "[]";
            if (uri.Host == "songsearch.kugou.com") return JsonSerializer.Serialize(new { data = new { lists = new[] {
                new { SongName = "测试 (DJ版)", SingerName = "本地", Duration = 120, FileHash = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" },
                new { SongName = "<em>测试</em>", SingerName = "本地", Duration = 180, FileHash = correctHash }
            } } });
            if (uri.AbsolutePath.EndsWith("search"))
            {
                bool exact = uri.Query.Contains("hash=" + correctHash); resolvedByHash |= exact;
                return JsonSerializer.Serialize(new { candidates = new[] { new { song = "测试", singer = "本地", duration = exact ? 180000 : 120000, id = exact ? "exact" : "wrong", accesskey = "fixture" } } });
            }
            wrongDownload |= uri.Query.Contains("id=wrong");
            return JsonSerializer.Serialize(new { content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(CompleteTimeline("[00:00]原版歌詞"))) });
        }));
        result = await provider.FetchAsync(song, CancellationToken.None);
        checks["Metadata hash fallback finds original recording and rejects wrong duration"] = result.Lines.Count == 2 && resolvedByHash && !wrongDownload;

        provider = new LyricsProvider(fixtures + "-candidates", new FixtureHandler(uri =>
        {
            if (uri.Host == "lrclib.net") return "[]";
            if (uri.AbsolutePath.EndsWith("search")) return JsonSerializer.Serialize(new { candidates = new[] {
                new { song = "测试", singer = "本地", duration = 180000, id = "plain", accesskey = "fixture" },
                new { song = "测试", singer = "本地", duration = 179950, id = "synced", accesskey = "fixture" }
            } });
            return JsonSerializer.Serialize(new { content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(uri.Query.Contains("id=plain") ? "只有文字" : CompleteTimeline("[00:00]第二个候选有时间轴"))) });
        }));
        result = await provider.FetchAsync(song, CancellationToken.None);
        checks["Try another matched candidate when first lyric has no timeline"] = result.Lines.Count == 2;

        var blocked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider = new LyricsProvider(fixtures + "-parallel", new AsyncFixtureHandler(async (uri, token) =>
        {
            if (uri.Host == "lyrics.kugou.com") return await blocked.Task.WaitAsync(token);
            if (uri.Host == "lrclib.net") return JsonSerializer.Serialize(new[] { new { trackName = "测试", artistName = "本地", duration = 180, syncedLyrics=CompleteTimeline("[00:00]备用来源及时返回") } });
            return "{}";
        }));
        var fetching = provider.FetchAsync(song, CancellationToken.None);
        bool fastFallback = await Task.WhenAny(fetching, Task.Delay(600)) == fetching;
        blocked.TrySetResult("{\"candidates\":[]}");
        result = await fetching;
        checks["Slow first source cannot delay successful second source"] = fastFallback && result.Lines.Count == 2;

        int transportAttempts = 0;
        provider = new LyricsProvider(fixtures + "-transport", new FixtureHandler(uri =>
        {
            if (uri.Host == "lrclib.net") return "[]";
            if (uri.AbsolutePath.EndsWith("search"))
            {
                if (++transportAttempts == 1) throw new HttpRequestException("Injected transient TLS connection failure");
                return JsonSerializer.Serialize(new { candidates = new[] { new { song = "测试", singer = "本地", duration = 180000, id = "retry", accesskey = "fixture" } } });
            }
            return JsonSerializer.Serialize(new { content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(CompleteTimeline("[00:00]瞬时失败后恢复"))) });
        }));
        result = await provider.FetchAsync(song, CancellationToken.None);
        checks["Transient transport failure retries before abandoning source"] = result.Lines.Count == 2 && transportAttempts == 2;
        checks["Same title and duration cannot match a different known singer"] = LyricsProvider.Score(song, "测试", "另一位歌手", 180000) < 65;
        var stalledPrimary = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider = new LyricsProvider(fixtures + "-mirror-latency", new AsyncFixtureHandler(async (uri, token) =>
        {
            if (uri.Host == "lrclib.net") return "[]";
            if (uri.Host == "songsearch.kugou.com") return "{}";
            if (uri.Host == "lyrics.kugou.com") await stalledPrimary.Task.WaitAsync(token);
            return uri.AbsolutePath.EndsWith("search")
                ? JsonSerializer.Serialize(new { candidates = new[] { new { song = "测试", singer = "本地", duration = 180000, id = "fast", accesskey = "fixture" } } })
                : JsonSerializer.Serialize(new { content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(CompleteTimeline("[00:00]备用入口及时返回"))) });
        }));
        fetching = provider.FetchAsync(song, CancellationToken.None);
        bool fastMirror = await Task.WhenAny(fetching, Task.Delay(900)) == fetching;
        stalledPrimary.TrySetResult(true); result = await fetching;
        checks["Stalled Kugou connection uses alternate endpoint before request timeout"] = fastMirror && result.Lines.Count == 2;

        var stalledKeyword = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider = new LyricsProvider(fixtures + "-hash-latency", new AsyncFixtureHandler(async (uri, token) =>
        {
            if (uri.Host == "lrclib.net") return "[]";
            if (uri.Host == "songsearch.kugou.com") return JsonSerializer.Serialize(new { data = new { lists = new[] {
                new { SongName = "测试", SingerName = "本地", Duration = 180, FileHash = correctHash }
            } } });
            if (uri.AbsolutePath.EndsWith("search"))
            {
                if (!uri.Query.Contains("hash=")) { await stalledKeyword.Task.WaitAsync(token); return "{\"candidates\":[]}"; }
                return JsonSerializer.Serialize(new { candidates = new[] { new { song = "测试", singer = "本地", duration = 180000, id = "fast-hash", accesskey = "fixture" } } });
            }
            return JsonSerializer.Serialize(new { content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(CompleteTimeline("[00:00]歌曲定位及时完成"))) });
        }));
        fetching = provider.FetchAsync(song, CancellationToken.None);
        bool earlyHash = await Task.WhenAny(fetching, Task.Delay(600)) == fetching;
        stalledKeyword.TrySetResult(true); result = await fetching;
        checks["Song identification starts without waiting for slow keyword lookup"] = earlyHash && result.Lines.Count == 2;
        int unnecessarySongSearch = 0;
        provider = new LyricsProvider(fixtures + "-avoid-extra-search", new FixtureHandler(uri =>
        {
            if (uri.Host == "songsearch.kugou.com") { unnecessarySongSearch++; return "{}"; }
            if (uri.Host == "lrclib.net") return "[]";
            return uri.AbsolutePath.EndsWith("search")
                ? JsonSerializer.Serialize(new { candidates = new[] { new { song = "测试", singer = "本地", duration = 180000, id = "direct", accesskey = "fixture" } } })
                : JsonSerializer.Serialize(new { content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(CompleteTimeline("[00:00]及时匹配无需歌曲搜索"))) });
        }));
        result = await provider.FetchAsync(song, CancellationToken.None);
        checks["Fast keyword match avoids unnecessary song searches"] = result.Lines.Count == 2 && unnecessarySongSearch == 0;
        foreach (var pair in await GeneralMatchingRegressionTests.RunAsync(fixtures + "-general")) checks["General: " + pair.Key] = pair.Value;
        foreach (var pair in await ProviderRegressionTests.RunAsync(fixtures + "-provider")) checks["Provider: " + pair.Key] = pair.Value;
        foreach (var pair in await LookupRegressionTests.RunAsync(fixtures + "-lookup")) checks["Lookup: " + pair.Key] = pair.Value;
        foreach (var pair in await BluetoothRegressionTests.RunAsync(fixtures + "-bluetooth")) checks["Bluetooth: " + pair.Key] = pair.Value;
        foreach (var pair in await ActivationRegressionTests.RunAsync(fixtures + "-activation")) checks["Activation: " + pair.Key] = pair.Value;
        foreach (var pair in await CompatibilityRegressionTests.RunAsync(fixtures + "-compat")) checks["Compatibility: " + pair.Key] = pair.Value;
        foreach (var pair in await PlatformLyricsRegressionTests.RunAsync(fixtures + "-platform")) checks["Platform: " + pair.Key] = pair.Value;
        foreach (var pair in await AlbumRegressionTests.RunAsync(fixtures + "-album")) checks["Album: " + pair.Key] = pair.Value;
        foreach (var pair in await UniversalRegressionTests.RunAsync(fixtures + "-universal")) checks["Universal: " + pair.Key] = pair.Value;
        foreach (var pair in await ProvidedLyricsRegressionTests.RunAsync(fixtures + "-provided")) checks["Provided: " + pair.Key] = pair.Value;
        foreach (var pair in await RecordingCacheRegressionTests.RunAsync(fixtures + "-recording-cache")) checks["RecordingCache: " + pair.Key] = pair.Value;
        foreach (var pair in await ChainLookupRegressionTests.RunAsync(fixtures + "-chain-lookup")) checks["Chain lookup: " + pair.Key] = pair.Value;
        foreach (var pair in await ChainQualityRegressionTests.RunAsync(fixtures + "-chain-quality")) checks["Chain quality: " + pair.Key] = pair.Value;
        foreach (var pair in await ChainProviderRegressionTests.RunAsync(fixtures + "-chain-provider")) checks["Chain provider: " + pair.Key] = pair.Value;
        foreach (var pair in await DesktopAuditProviderRegressionTests.RunAsync(fixtures + "-desktop-audit")) checks["Desktop audit: " + pair.Key] = pair.Value;
        foreach (var pair in AppearanceColorRegressionTests.Run()) checks["Appearance colors: " + pair.Key] = pair.Value;
        var legacyAppearance = JsonSerializer.Deserialize<Appearance>("{\"Mode\":\"text\",\"FontSize\":37,\"ShowNext\":false,\"Topmost\":false,\"Online\":false,\"Left\":12,\"Top\":34}")!;
        legacyAppearance.Normalize();
        checks["Existing appearance profile gains colors without resetting previous preferences"] =
            legacyAppearance.Mode=="text"&&legacyAppearance.FontSize==37&&!legacyAppearance.ShowNext&&!legacyAppearance.Topmost&&!legacyAppearance.Online
            &&legacyAppearance.Left==12&&legacyAppearance.Top==34&&legacyAppearance.LyricColor==AppearanceColors.DefaultLyric
            &&legacyAppearance.BackgroundColor==AppearanceColors.DefaultBackground;
        legacyAppearance.LyricColor="#abc";legacyAppearance.BackgroundColor="#f2efea";legacyAppearance.Normalize();
        var reloadedAppearance=JsonSerializer.Deserialize<Appearance>(JsonSerializer.Serialize(legacyAppearance))!;
        reloadedAppearance.Normalize();
        checks["Independent custom colors survive profile serialization and preserve other choices"] =
            reloadedAppearance.LyricColor=="#AABBCC"&&reloadedAppearance.BackgroundColor=="#F2EFEA"&&reloadedAppearance.FontSize==37&&!reloadedAppearance.Online;
        reloadedAppearance.LyricColor=null!;reloadedAppearance.BackgroundColor="transparent";reloadedAppearance.Normalize();
        checks["Invalid saved colors safely default without clearing the remaining profile"] =
            reloadedAppearance.LyricColor==AppearanceColors.DefaultLyric&&reloadedAppearance.BackgroundColor==AppearanceColors.DefaultBackground
            &&reloadedAppearance.Mode=="text"&&reloadedAppearance.FontSize==37&&reloadedAppearance.Left==12;
        foreach (var pair in await NativeRoutingRegressionTests.RunAsync(fixtures + "-native-routing")) checks["Native routing: " + pair.Key] = pair.Value;
        foreach (var pair in await NativePlatformRegressionTests.RunAsync(fixtures + "-native-platform")) checks["Native adapter: " + pair.Key] = pair.Value;
        foreach (var pair in await QueryFixRegressionTests.RunAsync(fixtures + "-query-fixes")) checks["Query fixes: " + pair.Key] = pair.Value;
        foreach (var pair in await RecordingEvidenceRegressionTests.RunAsync(fixtures + "-recording-evidence")) checks["Recording evidence: " + pair.Key] = pair.Value;
        foreach (var pair in await CacheReviewRegressionTests.RunAsync(fixtures + "-cache-review")) checks["Cache review: " + pair.Key] = pair.Value;
        foreach (var pair in await LookupLifecycleRegressionTests.RunAsync(fixtures + "-lifecycle")) checks["Playback lifecycle: " + pair.Key] = pair.Value;
        foreach (var pair in await ReceiveClockRegressionTests.RunAsync(fixtures + "-receiver-clock")) checks["Receipt clock: " + pair.Key] = pair.Value;
        foreach (var pair in await KugouCandidateOrderRegressionTests.RunAsync(fixtures + "-candidate-order")) checks["Kugou candidate order: " + pair.Key] = pair.Value;
        foreach (var pair in await NativeAlbumLookupRegressionTests.RunAsync(fixtures + "-native-album")) checks["Native album lookup: " + pair.Key] = pair.Value;
        foreach (var pair in await NativeAlbumCompatibilityRegressionTests.RunAsync(fixtures + "-native-release-compatibility")) checks["Native release compatibility: " + pair.Key] = pair.Value;
        foreach (var pair in await LookupDiagnosticLogRegressionTests.RunAsync(fixtures + "-diagnostic-retention")) checks["Diagnostic retention: " + pair.Key] = pair.Value;
        foreach (var pair in await UniversalEvidenceRegressionTests.RunAsync(fixtures + "-universal-evidence")) checks["Universal evidence: " + pair.Key] = pair.Value;
        foreach (var pair in await UniversalCacheProvenanceRegressionTests.RunAsync(fixtures + "-universal-cache-provenance")) checks["Universal cache provenance: " + pair.Key] = pair.Value;
        foreach (var pair in await RecordingDocumentRegressionTests.RunAsync(fixtures + "-recording-document")) checks["Recording document: " + pair.Key] = pair.Value;
        bool success = checks.Values.All(x => x);
        File.WriteAllText(path, JsonSerializer.Serialize(new { success, checks }, new JsonSerializerOptions { WriteIndented = true }));
        return success ? 0 : 1;
    }
    private static string CompleteTimeline(string content) => content + "\n[02:30]测试后续歌词";

    private sealed class AsyncFixtureHandler(Func<Uri, CancellationToken, Task<string>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(await response(request.RequestUri!, token)) };
    }
    private sealed class FixtureHandler(Func<Uri, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(response(request.RequestUri!)) });
    }
}
