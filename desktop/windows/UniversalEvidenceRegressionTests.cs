using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LyricBridge;

// Synthetic public-endpoint fixtures, with no real listening history or network traffic.
internal static class UniversalEvidenceRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        string Area(string name) => Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
        foreach (string package in new[] { "com.tencent.qqmusic", "com.netease.cloudmusic" })
        {
            string platform = package == "com.tencent.qqmusic" ? "qq" : "netease";
            var song = new MediaSnapshot {
                Version = 1, Type = "media", PackageName = package, MediaId = "91001",
                Title = "Evidence Song", Artist = "Evidence Artist、Guest Artist", Album = "Single Release (Explicit)",
                DurationMs = 180000, Source = "media-session", PositionKnown = true
            };
            string body = Lyrics(song.Title, song.Artist, "Full Album");
            string area = Area(platform + "-native");
            var fixture = new NativeFixture(platform, song, body);
            var provider = new LyricsProvider(area, fixture);
            var diagnostics = new ConcurrentQueue<string>();
            provider.Diagnostic += diagnostics.Enqueue;
            var found = await provider.FetchAsync(song, CancellationToken.None);
            checks[platform + ": native recording accepts an independently compatible release album label"] =
                found.Lines.Count == 3 && found.Source.Contains("编号", StringComparison.Ordinal)
                && fixture.DetailRequests > 0 && fixture.LyricRequests > 0;
            checks[platform + ": successful native validation has the shared provider and basis diagnostic"] =
                HasDecision(diagnostics, platform, "native-id");
            checks[platform + ": diagnostics do not include lyric-body text"] =
                !string.Join("\n", diagnostics).Contains("VERIFICATION-BODY-FIRST", StringComparison.Ordinal);

            var offline = new NativeFixture(platform, song, body);
            var cached = await new LyricsProvider(area, offline).FetchAsync(song, CancellationToken.None, false, false);
            checks[platform + ": recording proof survives a fresh provider and an offline cache read"] =
                cached.Lines.Count == 3 && offline.Requests == 0;
            foreach (var mismatch in new Dictionary<string, MediaSnapshot> {
                ["another native identifier"] = song with { MediaId = "91002" },
                ["another platform namespace"] = song with { PackageName = platform == "qq" ? "com.netease.cloudmusic" : "com.tencent.qqmusic" },
                ["an unknown player with the same numeric ID"] = song with { PackageName = "fixture.small.player" },
                ["notification-only playback"] = song with { Source = "notification" }
            })
            {
                var rejected = await new LyricsProvider(area, offline).FetchAsync(mismatch.Value, CancellationToken.None, false, false);
                checks[platform + ": cached native release proof cannot authorize " + mismatch.Key] =
                    rejected.Lines.Count == 0 && offline.Requests == 0;
            }
            string shared = Path.Combine(area, "recording-cache");
            checks[platform + ": native album relaxation is not published as an unqualified shared cache"] =
                !Directory.Exists(shared) || Directory.GetFiles(shared, "*.json").Length == 0;

            string? document = Directory.Exists(Path.Combine(area, "lyrics"))
                ? Directory.GetFiles(Path.Combine(area, "lyrics"), "*.lrc.json").SingleOrDefault() : null;
            bool namespaced = false;
            var tampering = new Dictionary<string, bool> { ["missing proof"] = false, ["wrong provider"] = false,
                ["wrong catalog ID"] = false, ["metadata-only basis"] = false };
            if (document != null)
            {
                string original = await File.ReadAllTextAsync(document);
                var parsed = JsonNode.Parse(original)!;
                namespaced = parsed["Content"]?.GetValue<string>() == body
                    && parsed["RecordingEvidence"]?["Provider"]?.GetValue<string>() == platform
                    && parsed["RecordingEvidence"]?["Basis"]?.GetValue<string>() == "native-id"
                    && !string.IsNullOrEmpty(parsed["RecordingEvidence"]?["CatalogId"]?.GetValue<string>());
                foreach (string problem in tampering.Keys.ToList())
                {
                    var mutated = JsonNode.Parse(original)!;
                    mutated["NativeVerified"] = true;
                    if (problem == "missing proof") mutated.AsObject().Remove("RecordingEvidence");
                    else if (mutated["RecordingEvidence"] is JsonObject proof)
                    {
                        if (problem == "wrong provider") proof["Provider"] = platform == "qq" ? "netease" : "qq";
                        if (problem == "wrong catalog ID") proof["CatalogId"] = "id:91002";
                        if (problem == "metadata-only basis") proof["Basis"] = "catalog";
                    }
                    await File.WriteAllTextAsync(document, mutated.ToJsonString());
                    var rejected = await new LyricsProvider(area, offline).FetchAsync(song, CancellationToken.None, false, false);
                    tampering[problem] = rejected.Lines.Count == 0;
                }
                await File.WriteAllTextAsync(document, original);
            }
            checks[platform + ": private document retains original headers and namespaced native evidence"] = namespaced;
            foreach (var problem in tampering)
                checks[platform + ": offline album relaxation fails closed with " + problem.Key] = problem.Value;

            foreach (var mismatch in new Dictionary<string, string> {
                ["a different full artist set"] = Lyrics(song.Title, "Evidence Artist", "Full Album"),
                ["a clean edition"] = Lyrics(song.Title, song.Artist, "Full Album (Clean)"),
                ["a live album"] = Lyrics(song.Title, song.Artist, "Full Album (Live)"),
                ["a remix title"] = Lyrics(song.Title + " (Remix)", song.Artist, "Full Album"),
                ["a different language edition"] = Lyrics(song.Title, song.Artist, "Full Album (Japanese Version)"),
                ["conflicting title headers"] = "[ti:Unrelated Recording]\n" + body,
                ["a truncated time line"] = "[ti:" + song.Title + "]\n[ar:" + song.Artist + "]\n[al:Full Album]\n[00:00]开头\n[00:10]截断",
                ["an out-of-range time line"] = body.Replace("[02:30]", "[03:10]", StringComparison.Ordinal)
            })
            {
                var rejected = await new LyricsProvider(Area(platform + "-lyric-conflict"),
                    new NativeFixture(platform, song, mismatch.Value)).FetchAsync(song, CancellationToken.None);
                checks[platform + ": native evidence still rejects " + mismatch.Key] = rejected.Lines.Count == 0;
            }
            foreach (var mismatch in new Dictionary<string, MediaSnapshot> {
                ["unknown catalog duration"] = song with { DurationMs = 0 },
                ["catalog duration outside the native proof window"] = song with { DurationMs = 181500 },
                ["missing catalog album"] = song with { Album = "" },
                ["a different catalog album"] = song with { Album = "Other Release (Explicit)" },
                ["a different catalog singer"] = song with { Artist = "Another Artist" },
                ["a live catalog recording"] = song with { Title = song.Title + " (Live)" }
            })
            {
                var rejected = await new LyricsProvider(Area(platform + "-catalog-conflict"),
                    new NativeFixture(platform, mismatch.Value, body)).FetchAsync(song, CancellationToken.None);
                checks[platform + ": release relaxation requires complete compatible metadata: " + mismatch.Key] = rejected.Lines.Count == 0;
            }
            foreach (var mismatch in new Dictionary<string, MediaSnapshot> {
                ["a notification source"] = song with { Source = "notification" },
                ["unknown active duration"] = song with { DurationMs = 0 },
                ["no native ID"] = song with { MediaId = "" }
            })
            {
                var rejected = await new LyricsProvider(Area(platform + "-active-context"),
                    new NativeFixture(platform, song, body)).FetchAsync(mismatch.Value, CancellationToken.None);
                checks[platform + ": native release evidence is unavailable with " + mismatch.Key] = rejected.Lines.Count == 0;
            }
            if (platform == "qq")
            {
                foreach (string mediaId in new[] { "0039MnYb0qxYhV", "https://y.qq.com/n/ryqq/songDetail/0039MnYb0qxYhV" })
                {
                    var midSong = song with { MediaId = mediaId };
                    var midResult = await new LyricsProvider(Area("qq-mid"), new NativeFixture(platform, song, body))
                        .FetchAsync(midSong, CancellationToken.None);
                    checks["qq: canonical MID proof is preserved for " + mediaId] = midResult.Lines.Count == 3;
                }
            }
            else
            {
                var urlSong = song with { MediaId = "https://music.163.com/#/song?id=91001" };
                var urlResult = await new LyricsProvider(Area("netease-url"), new NativeFixture(platform, song, body))
                    .FetchAsync(urlSong, CancellationToken.None);
                checks["netease: canonical official URL proof is preserved"] = urlResult.Lines.Count == 3;
            }
        }

        var generic = new MediaSnapshot { Version = 1, Type = "media", PackageName = "fixture.small.player",
            MediaId = "91001", Source = "media-session", Title = "Evidence Song", Artist = "Evidence Artist、Guest Artist",
            Album = "Single Release (Explicit)", DurationMs = 180000, PositionKnown = true };
        foreach (string id in new[] { "91001", "content://private-library/song/91001", "locally-generated-song-key" })
        {
            var current = generic with { MediaId = id };
            var fixture = new NativeFixture("netease", generic, Lyrics(generic.Title, generic.Artist, generic.Album), search: true);
            var provider = new LyricsProvider(Area("small-player"), fixture);
            var diagnostics = new ConcurrentQueue<string>(); provider.Diagnostic += diagnostics.Enqueue;
            var found = await provider.FetchAsync(current, CancellationToken.None);
            checks["Unknown player uses metadata search without interpreting its ID: " + id] = found.Lines.Count == 3
                && fixture.DetailRequests == 0 && fixture.SearchRequests > 0 && fixture.LyricRequests > 0;
            checks["Unknown player receives common catalog diagnostics: " + id] = HasDecision(diagnostics, "netease", "catalog");
        }
        var genericRelaxation = await new LyricsProvider(Area("small-player-no-native-authority"),
            new NativeFixture("netease", generic, Lyrics(generic.Title, generic.Artist, "Full Album"), search: true))
            .FetchAsync(generic, CancellationToken.None);
        checks["A returned metadata candidate never inherits another platform's native-ID album privilege"] = genericRelaxation.Lines.Count == 0;
        return checks;
    }

    private static bool HasDecision(IEnumerable<string> entries, string provider, string basis)
    {
        foreach (string entry in entries.Where(value => value.StartsWith("candidate-decision:", StringComparison.Ordinal)))
        {
            try
            {
                using var document = JsonDocument.Parse(entry[(entry.IndexOf(':') + 1)..]);
                var root = document.RootElement;
                if (root.TryGetProperty("provider", out var p) && p.GetString() == provider
                    && root.TryGetProperty("basis", out var b) && b.GetString() == basis
                    && root.TryGetProperty("reason", out var r) && !string.IsNullOrEmpty(r.GetString())) return true;
            }
            catch (JsonException) { }
        }
        return false;
    }
    private static string Lyrics(string title, string artist, string album) => "[ti:" + title + "]\n[ar:" + artist
        + "]\n[al:" + album + "]\n[00:00]VERIFICATION-BODY-FIRST\n[01:00]验证中句\n[02:30]验证末句";

    private sealed class NativeFixture(string platform, MediaSnapshot catalogue, string content, bool search = false) : HttpMessageHandler
    {
        public int Requests, DetailRequests, LyricRequests, SearchRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Requests);
            var uri = request.RequestUri!; string response;
            var artists = catalogue.Artist.Split('、').Select(name => new { name }).ToArray();
            bool qq = platform == "qq";
            bool onPlatform = qq ? uri.Host is "c.y.qq.com" or "u.y.qq.com" : uri.Host == "music.163.com";
            if (onPlatform && (uri.AbsolutePath.Contains("/detail", StringComparison.Ordinal)
                || uri.AbsolutePath.Contains("single_song", StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref DetailRequests);
                response = qq
                    ? JsonSerializer.Serialize(new { code = 0, data = new[] { new { id = "91001", mid = "0039MnYb0qxYhV", title = catalogue.Title,
                        name = catalogue.Title, singer = artists, interval = catalogue.DurationMs / 1000.0, album = new { name = catalogue.Album } } } })
                    : JsonSerializer.Serialize(new { code = 200, songs = new[] { new { id = "91001", name = catalogue.Title,
                        artists, duration = catalogue.DurationMs, album = new { name = catalogue.Album } } } });
            }
            else if (onPlatform && uri.AbsolutePath.Contains("lyric", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref LyricRequests);
                response = qq ? JsonSerializer.Serialize(new { code = 0, retcode = 0, lyric = content })
                    : JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = content } });
            }
            else if (onPlatform && (uri.AbsolutePath.Contains("/search/", StringComparison.Ordinal)
                || uri.AbsolutePath.Contains("/soso/", StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref SearchRequests);
                response = qq
                    ? JsonSerializer.Serialize(new { code = 0, data = new { song = new { list = search ? new[] { new { songid = "91001",
                        songmid = "0039MnYb0qxYhV", songname = catalogue.Title, singer = artists,
                        interval = catalogue.DurationMs / 1000.0, albumname = catalogue.Album } } : System.Array.Empty<object>() } } })
                    : JsonSerializer.Serialize(new { code = 200, result = new { songs = search ? new[] { new { id = "91001", name = catalogue.Title,
                        artists, duration = catalogue.DurationMs, album = new { name = catalogue.Album } } } : System.Array.Empty<object>() } });
            }
            else response = uri.Host == "lrclib.net" ? "[]"
                : uri.Host == "music.163.com" ? "{\"code\":200,\"result\":{\"songs\":[]}}"
                : uri.Host == "c.y.qq.com" ? "{\"code\":0,\"data\":{\"song\":{\"list\":[]}}}" : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
        }
    }
}
