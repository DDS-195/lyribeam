namespace LyricBridge;

internal static class ChainQualityRegressionTests
{
    public static async Task<Dictionary<string,bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string,bool>();
        var song = new MediaSnapshot {
            Version = 1, Type = "media", PackageName = "player-a", Title = "审核测试歌",
            Artist = "审核测试歌手", Album = "审核测试专辑", DurationMs = 180000
        };
        const string headers = "[ti:审核测试歌]\n[ar:审核测试歌手]\n[al:审核测试专辑]\n";
        const string full = "[00:00]第一句\n[01:00]第二句\n[02:00]第三句";
        bool Valid(string content, MediaSnapshot? value = null) =>
            LyricQuality.TryValidate(content, value ?? song, out _, out _);
        foreach (var pair in new Dictionary<string,string> {
            ["empty"] = "",
            ["plain text"] = "没有时间轴",
            ["only one cue"] = "[00:10]只有一句",
            ["same cue repeated"] = "[00:10]一句\n[00:10]重复一句",
            ["invalid seconds"] = "[00:00]第一句\n[00:99]非法时间\n[02:00]第三句",
            ["outside known recording"] = "[00:00]第一句\n[03:30]结束之后",
            ["truncated opening preview"] = "[00:00]第一句\n[00:10]第二句",
            ["intermediate blank cue is not completion"] = "[00:00]第一句\n[00:10]第二句\n[00:40]"
        }) checks["Common gate rejects "+pair.Key] = !Valid(pair.Value);
        checks["Complete ordinary timeline is accepted"] = Valid(full);
        checks["Unknown duration retains two-cue evidence for later revalidation"] =
            Valid("[00:00]第一句\n[00:10]第二句", song with { DurationMs = 0 });
        checks["A genuine short recording can contain only two nearby lines"] =
            Valid("[00:00]第一句\n[00:10]第二句", song with { DurationMs = 20000 });
        checks["Long instrumental introduction is not rejected"] = Valid("[02:10]第一句\n[02:40]第二句");
        checks["Long instrumental tail with several vocal cues is not rejected"] =
            Valid("[00:00]第一句\n[00:10]第二句\n[00:20]第三句");
        checks["Explicit final empty cue can confirm intentional long tail"] =
            Valid("[00:00]第一句\n[00:10]第二句\n[02:58]");
        checks["An empty cue beyond duration cannot manufacture completion"] =
            !Valid("[00:00]第一句\n[00:10]第二句\n[03:01]");
        checks["A lyric cue exactly at known duration remains valid"] =
            Valid("[00:00]第一句\n[03:00]最后一句");
        checks["Quality parsing does not manufacture document headers"] =
            LyricQuality.TryValidate(full, song, out var lines, out _) && lines.Count == 3;

        var supplied = song with { Source = "integration", SessionId = "session", TrackId = "track",
            ProvidedLyricsTrackId = "track", ProvidedLyrics = headers + full };
        checks["Provided source uses same reliable timeline gate"] =
            ProvidedLyrics.TryValidate(supplied, out var provided, out _) && provided.FromProvidedLyrics;
        checks["Provided source rejects truncated opening timeline"] =
            !ProvidedLyrics.TryValidate(supplied with { ProvidedLyrics = headers+"[00:00]第一句\n[00:10]第二句" }, out _, out _);
        checks["Payload omission keeps valid cache binding"] =
            ProvidedLyrics.CanReuseCached(supplied with { ProvidedLyrics = "" });
        checks["Explicit withdrawal invalidates supplied cache binding"] =
            !ProvidedLyrics.CanReuseCached(supplied with { ProvidedLyrics = "", ProvidedLyricsTrackId = "" });
        checks["Another binding cannot revive supplied cache"] =
            !ProvidedLyrics.CanReuseCached(supplied with { ProvidedLyricsTrackId = "old-track" });
        checks["Standard source is not treated as an integration withdrawal"] =
            ProvidedLyrics.CanReuseCached(song);

        Directory.CreateDirectory(root);
        foreach (string origin in new[] { "播放器提供的歌词", "provided" })
        {
            var cache = new RecordingCache(Path.Combine(root, Guid.NewGuid().ToString("N")));
            await cache.StoreAsync(supplied, headers+full, origin, CancellationToken.None);
            var withdrawn = supplied with { ProvidedLyrics = "", ProvidedLyricsTrackId = "" };
            checks["Shared cache respects explicit withdrawal for "+origin] =
                await cache.TryReadAsync(withdrawn, CancellationToken.None) == null;
            var omitted = await cache.TryReadAsync(supplied with { ProvidedLyrics = "" }, CancellationToken.None);
            checks["Shared cache preserves omitted payload and provenance for "+origin] =
                omitted?.Lines.Count == 3 && omitted.FromProvidedLyrics;
            var ordinary = await cache.TryReadAsync(song with { PackageName = "other-player" }, CancellationToken.None);
            checks["Verified supplied cache remains usable across ordinary players for "+origin] =
                ordinary?.Lines.Count == 3 && ordinary.FromProvidedLyrics;
        }
        {
            var cache = new RecordingCache(Path.Combine(root, "network"));
            await cache.StoreAsync(supplied, headers+full, "LRCLIB", CancellationToken.None);
            var found = await cache.TryReadAsync(supplied with { ProvidedLyrics = "", ProvidedLyricsTrackId = "" }, CancellationToken.None);
            checks["Integration withdrawal does not discard independently retrieved network cache"] =
                found?.Lines.Count == 3 && !found.FromProvidedLyrics;
        }
        {
            string area = Path.Combine(root, "truncated");
            var cache = new RecordingCache(area);
            await cache.StoreAsync(song, headers+"[00:00]第一句\n[00:10]第二句", "LRCLIB", CancellationToken.None);
            checks["Truncated timeline is never published as verified shared cache"] =
                !Directory.Exists(Path.Combine(area,"recording-cache"));
        }
        return checks;
    }
}
