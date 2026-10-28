using System.Text.Json.Nodes;
namespace LyricBridge;

internal static class UniversalCacheProvenanceRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var song = new MediaSnapshot { Type = "media", Source = "media-session", PackageName = "com.kugou.android.lite",
            MediaId = "2468", Title = "缓存验证作品", Artist = "缓存验证歌手", Album = "缓存验证专辑", DurationMs = 180000 };
        const string body = "[ti:缓存验证作品]\n[ar:缓存验证歌手]\n[al:缓存验证专辑]\n[00:00]第一句\n[01:20]第二句\n[02:40]末句";
        var proof = RecordingEvidence.Native("kugou", song, "id:2468", song.Title, song.Artist, song.Album,
            song.DurationMs, "0123456789abcdef0123456789abcdef");
        string area = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var cache = new RecordingCache(area);
        await cache.StoreAsync(song, body, "酷狗歌词 · 歌曲编号匹配", CancellationToken.None,
            nativeVerified: true, strategy: LyricsProvider.MatchingStrategy, checkedAtUtc: DateTimeOffset.UtcNow,
            provider: "kugou", recordingEvidence: proof);
        string path = Directory.GetFiles(Path.Combine(area, "recording-cache"), "*.json").Single();
        string original = await File.ReadAllTextAsync(path);
        var stored = JsonNode.Parse(original)!;
        checks["Strict shared documents preserve typed provider and namespaced native proof in schema 2"] =
            stored["Schema"]!.GetValue<int>() == 2 && stored["Provider"]!.GetValue<string>() == "kugou"
            && stored["RecordingEvidence"]?["CatalogId"]?.GetValue<string>() == "id:2468";
        var same = await cache.TryReadAsync(song, CancellationToken.None);
        checks["A proof is retained only after restoring its full original playback context"] =
            same?.Lines.Count == 3 && same.Provider == "kugou" && same.RecordingEvidence?.Matches(song) == true
            && !same.NeedsSourceReview;
        var other = await cache.TryReadAsync(song with { PackageName = "fixture.small.player", MediaId = "opaque-local:7" }, CancellationToken.None);
        checks["Strict headers remain reusable across small players while native proof is discarded"] =
            other?.Lines.Count == 3 && other.Provider == "kugou" && other.RecordingEvidence == null;
        var qq = await cache.TryReadAsync(song with { PackageName = "com.tencent.qqmusic", MediaId = "2468" }, CancellationToken.None);
        checks["A cross-platform cache is reviewable and cannot borrow an equal numeric native ID"] =
            qq?.Lines.Count == 3 && qq.NeedsSourceReview && qq.RecordingEvidence == null;
        checks["Strict documents still reject conflicting IDs within the same player"] =
            await cache.TryReadAsync(song with { MediaId = "2469" }, CancellationToken.None) == null;

        foreach (string mutation in new[] { "null-source", "wrong-proof-provider", "unknown-document-provider", "null-document-provider" })
        {
            var document = JsonNode.Parse(original)!;
            if (mutation == "null-source") document["RecordingEvidence"]!["OriginSource"] = null;
            if (mutation == "wrong-proof-provider") document["RecordingEvidence"]!["Provider"] = "qq";
            if (mutation == "unknown-document-provider") document["Provider"] = "fake-provider";
            if (mutation == "null-document-provider") document["Provider"] = null;
            await File.WriteAllTextAsync(path, document.ToJsonString());
            var read = await cache.TryReadAsync(song, CancellationToken.None);
            checks["Malformed or mismatching provenance safely loses privileges: " + mutation] =
                read?.Lines.Count == 3 && read.RecordingEvidence == null
                && (mutation is not ("unknown-document-provider" or "null-document-provider") || read.Provider.Length == 0);
        }
        var legacy = JsonNode.Parse(original)!;
        legacy["Schema"] = 1; legacy["Strategy"] = 1;
        legacy.AsObject().Remove("Provider"); legacy.AsObject().Remove("RecordingEvidence");
        legacy["NativeVerified"] = true;
        await File.WriteAllTextAsync(path, legacy.ToJsonString());
        var old = await cache.TryReadAsync(song, CancellationToken.None);
        checks["Legacy schema 1 stays available offline without inheriting native proof or skipping strategy review"] =
            old?.Lines.Count == 3 && old.Provider.Length == 0 && old.RecordingEvidence == null && old.NeedsSourceReview;
        var flag = JsonNode.Parse(original)!;
        flag["Provider"] = ""; flag["RecordingEvidence"] = null; flag["NativeVerified"] = true;
        flag["CheckedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        await File.WriteAllTextAsync(path, flag.ToJsonString());
        var forged = await cache.TryReadAsync(song, CancellationToken.None);
        checks["A NativeVerified boolean cannot suppress review when typed provenance is missing"] =
            forged?.Lines.Count == 3 && forged.NeedsSourceReview && forged.RecordingEvidence == null;
        var changedOrigin = JsonNode.Parse(original)!;
        changedOrigin["OriginSource"] = "notification";
        await File.WriteAllTextAsync(path, changedOrigin.ToJsonString());
        var rebound = await cache.TryReadAsync(song, CancellationToken.None);
        checks["Stored proof is rechecked against original source rather than only current metadata"] =
            rebound?.Lines.Count == 3 && rebound.RecordingEvidence == null;
        await File.WriteAllTextAsync(path, original);

        string relaxedArea = Path.Combine(root, "relaxed-" + Guid.NewGuid().ToString("N"));
        await new RecordingCache(relaxedArea).StoreAsync(song, body.Replace("[al:缓存验证专辑]", "[al:另一发行专辑]"),
            "酷狗歌词 · 歌曲编号匹配", CancellationToken.None, true, LyricsProvider.MatchingStrategy, DateTimeOffset.UtcNow,
            "kugou", proof);
        checks["Native release compatibility never promotes a relaxed album into the strict shared cache"] =
            !Directory.Exists(Path.Combine(relaxedArea, "recording-cache"))
            || Directory.GetFiles(Path.Combine(relaxedArea, "recording-cache"), "*.json").Length == 0;
        return checks;
    }
}
