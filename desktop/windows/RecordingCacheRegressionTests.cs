using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LyricBridge;

internal static class RecordingCacheRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        string Area(string name) => Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
        const string content = "[ti:测试歌]\n[ar:测试歌手]\n[al:测试专辑]\n[00:00]验证第一句\n[01:10]验证第二句";
        var song = new MediaSnapshot { Version = 1, Type = "media", PackageName = "player-a", MediaId = "123",
            Title = "测试歌", Artist = "测试歌手", Album = "测试专辑", DurationMs = 120000, Source = "media_session" };
        var other = song with { PackageName = "player-b", MediaId = "456" };
        string sharedArea = Area("shared");
        var cache = new RecordingCache(sharedArea);
        await cache.StoreAsync(song, content, "fixture-verified-source", CancellationToken.None);
        var result = await cache.TryReadAsync(other, CancellationToken.None);
        checks["Verified original headers album and duration allow cross-player reuse"] =
            result?.Lines.Count == 2 && result.Source.Contains("共享");
        checks["Small known duration rounding remains reusable"] =
            await cache.TryReadAsync(other with { DurationMs = song.DurationMs + 1000 }, CancellationToken.None) != null;
        foreach (var pair in new Dictionary<string, MediaSnapshot> {
            ["duration beyond one second"] = other with { DurationMs = song.DurationMs + 1001 },
            ["unknown duration"] = other with { DurationMs = 0 },
            ["unknown album"] = other with { Album = "" },
            ["different album edition"] = other with { Album = "测试专辑 (Deluxe)" },
            ["different singer"] = other with { Artist = "另一位歌手" },
            ["different recording version"] = other with { Title = "测试歌 (Live)" },
            ["same foreign numeric ID but a different song"] = other with { MediaId = song.MediaId, Title = "另一首歌" },
            ["conflicting native ID in the same player"] = song with { MediaId = "999" }
        })
            checks["Shared cache rejects " + pair.Key] = await cache.TryReadAsync(pair.Value, CancellationToken.None) == null;
        string originalPath = Directory.GetFiles(Path.Combine(sharedArea, "recording-cache"), "*.json").Single();
        string originalJson = await File.ReadAllTextAsync(originalPath);
        using (var document = JsonDocument.Parse(originalJson))
        {
            checks["Original LRC is stored unchanged rather than inventing evidence headers"] =
                document.RootElement.GetProperty("Content").GetString() == content
                && document.RootElement.GetProperty("OriginPackage").GetString() == song.PackageName
                && document.RootElement.GetProperty("ObservedDurationMs").GetInt64() == song.DurationMs;
        }
        foreach (var pair in new Dictionary<string, (MediaSnapshot Snapshot, string Content)> {
            ["missing title header"] = (song, content.Replace("[ti:测试歌]\n", "")),
            ["missing artist header"] = (song, content.Replace("[ar:测试歌手]\n", "")),
            ["missing album header"] = (song, content.Replace("[al:测试专辑]\n", "")),
            ["missing observed album"] = (song with { Album = "" }, content),
            ["missing known duration"] = (song with { DurationMs = 0 }, content),
            ["mismatching album evidence"] = (song with { Album = "另一专辑" }, content),
            ["mismatching singer evidence"] = (song with { Artist = "另一位歌手" }, content),
            ["mismatching version evidence"] = (song with { Title = "测试歌 (Live)" }, content),
            ["conflicting real headers"] = (song, "[ar:另一位歌手]\n" + content),
            ["plain text without a timeline"] = (song, "[ti:测试歌]\n[ar:测试歌手]\n[al:测试专辑]\n完整歌词文本"),
            ["only one timed line"] = (song, "[ti:测试歌]\n[ar:测试歌手]\n[al:测试专辑]\n[01:10]当前一句"),
            ["timeline beyond recording duration"] = (song, content.Replace("[01:10]", "[02:02]"))
        })
        {
            string area = Area("unverified");
            var unverified = new RecordingCache(area);
            await unverified.StoreAsync(pair.Value.Snapshot, pair.Value.Content, "fixture", CancellationToken.None);
            checks["Unverified cache is not published: " + pair.Key] =
                !Directory.Exists(Path.Combine(area, "recording-cache"))
                || Directory.GetFiles(Path.Combine(area, "recording-cache"), "*.json").Length == 0;
        }
        string integrationArea = Area("integration");
        var integrated = song with { Source = "integration", SessionId = "session", TrackId = "track-1", MediaId = "" };
        var integratedCache = new RecordingCache(integrationArea);
        await integratedCache.StoreAsync(integrated, content, "provided", CancellationToken.None);
        checks["Distinct integration track identifiers cannot reuse the previous document"] =
            await integratedCache.TryReadAsync(integrated with { TrackId = "track-2" }, CancellationToken.None) == null;

        foreach (string corruption in new[] { "hash", "header", "forged-headers" })
        {
            var document = JsonNode.Parse(originalJson)!;
            if (corruption == "hash") document["ContentSha256"] = "invalid";
            if (corruption == "header") document["TitleHeader"] = "伪造歌名";
            if (corruption == "forged-headers")
            {
                string withoutHeaders = "[00:00]第一句\n[01:10]第二句";
                document["Content"] = withoutHeaders;
                document["ContentSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(withoutHeaders)));
            }
            await File.WriteAllTextAsync(originalPath, document.ToJsonString());
            checks["Read revalidates evidence after " + corruption + " corruption"] =
                await cache.TryReadAsync(other, CancellationToken.None) == null;
        }
        await File.WriteAllTextAsync(originalPath, "broken-json");
        checks["Malformed shared cache safely misses"] = await cache.TryReadAsync(other, CancellationToken.None) == null;
        await File.WriteAllTextAsync(originalPath, originalJson);
        using (var locked = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.None))
            checks["Locked shared cache safely misses"] = await cache.TryReadAsync(other, CancellationToken.None) == null;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool observedCancellation = false;
        try { await cache.StoreAsync(song, content, "cancelled", cancelled.Token); }
        catch (OperationCanceledException) { observedCancellation = true; }
        checks["Cancelled publication preserves existing verified cache without temporary debris"] =
            observedCancellation && await File.ReadAllTextAsync(originalPath) == originalJson
            && Directory.GetFiles(Path.Combine(sharedArea, "recording-cache"), "*.tmp").Length == 0;

        int networkRequests = 0;
        var provider = new LyricsProvider(sharedArea, new Fixture(uri => { networkRequests++; return uri.Host == "lrclib.net" ? "[]" : "{}"; }));
        result = await provider.FetchAsync(other, CancellationToken.None, false, false);
        checks["Verified shared hit works offline and does not contact lyric services"] =
            result.Lines.Count == 2 && result.Source.Contains("共享") && networkRequests == 0;
        string privatePath = Path.Combine(sharedArea, "lyrics", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(other.TrackKey))) + ".lrc");
        string privateContent = content.Replace("验证第一句", "私有缓存第一句");
        await File.WriteAllTextAsync(privatePath, privateContent);
        result = await provider.FetchAsync(other, CancellationToken.None);
        checks["Existing private cache remains ahead of verified shared cache"] =
            result.Source == "本地缓存" && result.Lines[0].Text == "私有缓存第一句" && networkRequests == 0;

        string oldArea = Area("old-private");
        provider = new LyricsProvider(oldArea, new Fixture(uri => { networkRequests++; return uri.Host == "lrclib.net" ? "[]" : "{}"; }));
        string oldPath = Path.Combine(oldArea, "lyrics", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(song.TrackKey))) + ".lrc");
        const string unverifiedPrivate = "[00:00]老缓存第一句\n[01:10]老缓存第二句";
        await File.WriteAllTextAsync(oldPath, unverifiedPrivate);
        result = await provider.FetchAsync(song, CancellationToken.None, false, false);
        checks["Legacy private cache remains usable without promotion based on fabricated tags"] =
            result.Lines.Count == 2 && await File.ReadAllTextAsync(oldPath) == unverifiedPrivate
            && !Directory.Exists(Path.Combine(oldArea, "recording-cache"));
        result = await provider.FetchAsync(other, CancellationToken.None, false, false);
        checks["Another player cannot consume unverified legacy private cache"] = result.Lines.Count == 0 && networkRequests == 0;

        string networkArea = Area("network-publish"); int fetchedRequests = 0;
        provider = new LyricsProvider(networkArea, new Fixture(uri => {
            fetchedRequests++;
            return uri.Host == "lrclib.net"
                ? JsonSerializer.Serialize(new[] { new { trackName = song.Title, artistName = song.Artist, albumName = song.Album,
                    duration = song.DurationMs / 1000, syncedLyrics = content } })
                : "{}";
        }));
        result = await provider.FetchAsync(song, CancellationToken.None);
        int before = fetchedRequests;
        var next = await provider.FetchAsync(other, CancellationToken.None, false, false);
        checks["Validated network LRC publishes a reusable recording without another lookup"] =
            result.Lines.Count == 2 && before > 0 && next.Source.Contains("共享") && fetchedRequests == before;
        return checks;
    }

    private sealed class Fixture(Func<Uri, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request.RequestUri!)) });
    }
}
