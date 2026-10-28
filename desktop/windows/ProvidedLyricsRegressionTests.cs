using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LyricBridge;

internal static class ProvidedLyricsRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        const string content = "[ti:测试歌]\n[ar:测试歌手]\n[al:测试专辑]\n[00:00]当前播放器第一句\n[01:10]当前播放器第二句";
        var snapshot = new MediaSnapshot {
            Version = 1, Type = "media", Title = "测试歌", Artist = "测试歌手", Album = "测试专辑", DurationMs = 120000,
            PackageName = "authorized.player", Source = "integration", Capability = "sync",
            SessionId = "session-A", TrackId = "track-A", ProvidedLyricsTrackId = "track-A", ProvidedLyrics = content
        };
        checks["Current authorized complete lyrics are accepted with track binding"] =
            ProvidedLyrics.TryValidate(snapshot, out var valid, out _) && valid.Lines.Count == 2 && valid.PlainText == content;
        checks["Metadata-only capability does not invalidate a complete provided document"] =
            ProvidedLyrics.TryValidate(snapshot with { Capability = "metadata-only" }, out _, out _);
        checks["Complete trusted lyrics do not require fabricated body headers"] =
            ProvidedLyrics.TryValidate(snapshot with { ProvidedLyrics = "[00:00]第一句\n[01:10]第二句" }, out _, out _);
        foreach (var pair in new Dictionary<string, MediaSnapshot> {
            ["wrong binding"] = snapshot with { ProvidedLyricsTrackId = "track-B" },
            ["missing binding"] = snapshot with { ProvidedLyricsTrackId = "" },
            ["missing current track"] = snapshot with { TrackId = "" },
            ["missing session"] = snapshot with { SessionId = "" },
            ["inactive source"] = snapshot with { Type = "idle" },
            ["ordinary media source"] = snapshot with { Source = "media_session" },
            ["changed song"] = snapshot with { TrackId = "track-B" },
            ["changed title"] = snapshot with { Title = "另一首歌" },
            ["changed artist"] = snapshot with { Artist = "另一位歌手" },
            ["changed recording version"] = snapshot with { Title = "测试歌 (Live)" },
            ["wrong album"] = snapshot with { Album = "另一专辑" },
            ["plain text"] = snapshot with { ProvidedLyrics = "没有时间轴的完整文本" },
            ["only current line"] = snapshot with { ProvidedLyrics = "[01:10]当前一句" },
            ["same timestamp only"] = snapshot with { ProvidedLyrics = "[01:10]第一句\n[01:10]第二句" },
            ["blank lines"] = snapshot with { ProvidedLyrics = "[00:00]\n[01:10]" },
            ["out of duration"] = snapshot with { ProvidedLyrics = "[00:00]第一句\n[02:01]第二句" },
            ["conflicting body headers"] = snapshot with { ProvidedLyrics = "[ti:测试歌]\n[ti:另一首歌]\n" + content },
            ["oversized payload"] = snapshot with { ProvidedLyrics = new string('x', 65537) }
        })
            checks["Provided lyrics reject " + pair.Key] = !ProvidedLyrics.TryValidate(pair.Value, out _, out _);
        string area = Path.Combine(root, "provided-" + Guid.NewGuid().ToString("N"));
        int requests = 0;
        var provider = new LyricsProvider(area, new Fixture(uri => { requests++; return uri.Host == "lrclib.net" ? "[]" : "{}"; }));
        string cache = Path.Combine(area, "lyrics", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.TrackKey))) + ".lrc");
        const string older = "[ti:测试歌]\n[ar:测试歌手]\n[al:测试专辑]\n[00:00]缓存第一句\n[01:10]缓存第二句";
        await File.WriteAllTextAsync(cache, older);
        var result = await provider.FetchAsync(snapshot, CancellationToken.None);
        checks["Provided lyrics win over private cache and network without any HTTP"] =
            result.Source.Contains("播放器") && result.Lines[0].Text == "当前播放器第一句" && requests == 0;
        checks["Validated original provided document is persisted without synthesized tags"] =
            await File.ReadAllTextAsync(cache) == content;
        await File.WriteAllTextAsync(cache, older);
        result = await provider.FetchAsync(snapshot with { ProvidedLyricsTrackId = "wrong" }, CancellationToken.None, false, false);
        checks["Rejected provided payload cannot revive a withdrawn supplied private cache"] =
            result.Lines.Count == 0;
        await provider.RememberProvidedAsync(snapshot with { ProvidedLyricsTrackId = "wrong" }, CancellationToken.None);
        checks["Invalid supplied payload cannot overwrite previously working private cache"] =
            await File.ReadAllTextAsync(cache) == older;
        result = await provider.FetchAsync(snapshot with { Type = "idle" }, CancellationToken.None);
        checks["Idle disconnect snapshot cannot revive a previous cached song"] = result.Lines.Count == 0 && requests == 0;
        result = await provider.FetchAsync(snapshot with { TrackId = "track-B" }, CancellationToken.None, false, false);
        checks["New track with stale provided binding cannot reuse old private lyrics"] =
            result.Lines.Count == 0 && requests == 0;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool cancelled = false;
        try { await provider.RememberProvidedAsync(snapshot, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        checks["Provided document persistence honors caller cancellation"] = cancelled;
        return checks;
    }

    private sealed class Fixture(Func<Uri, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request.RequestUri!)) });
    }
}
