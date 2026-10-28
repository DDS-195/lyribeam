using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LyricBridge;

// These synthetic documents exercise release metadata, never service availability.
internal static class NativeAlbumCompatibilityRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        string Area(string name) => Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
        var song = new MediaSnapshot {
            Version = 1, Type = "media", Title = "验证作品", Artist = "验证歌手、合作歌手",
            Album = "单曲发行 (Explicit)", PackageName = "com.kugou.android.lite", MediaId = "6200",
            DurationMs = 180000, Source = "media-session", PositionKnown = true
        };
        string omittedLabelBody = Lyrics(song.Title, song.Artist, "完整专辑");
        string acceptedArea = Area("omitted-content-label");
        var acceptedFixture = new NativeFixture(song, omittedLabelBody);
        var accepted = await new LyricsProvider(acceptedArea, acceptedFixture).FetchAsync(song, CancellationToken.None);
        checks["A confirmed native recording accepts an omitted Explicit album label in the lyric document"] =
            accepted.Lines.Count == 3 && accepted.Source.Contains("编号", StringComparison.Ordinal) && acceptedFixture.CatalogRequests > 0;

        var doubleAlbumSong = song with { Album = "Compilation Release (Double Album) [Explicit]" };
        var packaging = await new LyricsProvider(Area("double-album-packaging"),
            new NativeFixture(doubleAlbumSong, Lyrics(song.Title, song.Artist, "完整专辑 (Explicit)")))
            .FetchAsync(doubleAlbumSong, CancellationToken.None);
        checks["A confirmed native recording accepts a Double Album packaging difference with matching edition labels"] =
            packaging.Lines.Count == 3 && packaging.Source.Contains("编号", StringComparison.Ordinal);
        var combined = await new LyricsProvider(Area("double-album-and-omitted-label"),
            new NativeFixture(doubleAlbumSong, omittedLabelBody)).FetchAsync(doubleAlbumSong, CancellationToken.None);
        checks["A confirmed native recording accepts Double Album packaging and an omitted Explicit label together"] =
            combined.Lines.Count == 3 && combined.Source.Contains("编号", StringComparison.Ordinal);

        var offline = new NativeFixture(song, omittedLabelBody);
        var cached = await new LyricsProvider(acceptedArea, offline).FetchAsync(song, CancellationToken.None, false, false);
        checks["The accepted release-label difference survives a fresh offline provider with native evidence"] =
            cached.Lines.Count == 3 && offline.Requests == 0;
        var otherPlayer = await new LyricsProvider(acceptedArea, offline).FetchAsync(
            song with { PackageName = "fixture.other.player", MediaId = "another-id" }, CancellationToken.None, false, false);
        checks["Release-label native proof does not leak through a shared cache to another player"] =
            otherPlayer.Lines.Count == 0 && offline.Requests == 0;
        var otherId = await new LyricsProvider(acceptedArea, offline).FetchAsync(song with { MediaId = "6201" }, CancellationToken.None, false, false);
        checks["Release-label native proof cannot authorize another native song identifier offline"] =
            otherId.Lines.Count == 0 && offline.Requests == 0;
        string sharedPath = Path.Combine(acceptedArea, "recording-cache");
        checks["An omitted release label is not promoted to an unqualified cross-player cache"] =
            !Directory.Exists(sharedPath) || Directory.GetFiles(sharedPath, "*.json").Length == 0;

        string? cacheDocument = Directory.GetFiles(Path.Combine(acceptedArea, "lyrics"), "*.lrc.json").SingleOrDefault();
        bool originalHeadersAndProof = false, proofMustRemain = false, proofIdMustMatch = false;
        if (cacheDocument != null)
        {
            string original = await File.ReadAllTextAsync(cacheDocument);
            var document = JsonNode.Parse(original)!;
            originalHeadersAndProof = document["Content"]?.GetValue<string>() == omittedLabelBody
                && document["RecordingEvidence"] is JsonObject;
            document.AsObject().Remove("RecordingEvidence");
            document["NativeVerified"] = true;
            await File.WriteAllTextAsync(cacheDocument, document.ToJsonString());
            var withoutProof = await new LyricsProvider(acceptedArea, offline).FetchAsync(song, CancellationToken.None, false, false);
            proofMustRemain = withoutProof.Lines.Count == 0;
            document = JsonNode.Parse(original)!;
            if (document["RecordingEvidence"] is JsonObject proof)
            {
                proof["MediaId"] = "6201";
                await File.WriteAllTextAsync(cacheDocument, document.ToJsonString());
                var wrongProof = await new LyricsProvider(acceptedArea, offline).FetchAsync(song, CancellationToken.None, false, false);
                proofIdMustMatch = wrongProof.Lines.Count == 0;
            }
            await File.WriteAllTextAsync(cacheDocument, original);
        }
        checks["Release compatibility preserves original lyric headers and explicit recording proof in private cache"] = originalHeadersAndProof;
        checks["A NativeVerified flag without the proof cannot authorize an omitted release label"] = proofMustRemain;
        checks["Cached release compatibility revalidates the proof song identifier"] = proofIdMustMatch;

        foreach (var pair in new Dictionary<string, MediaSnapshot> {
            ["no identifier"] = song with { MediaId = "" },
            ["unresolved identifier"] = song with { MediaId = "9999" },
            ["a lookalike player package"] = song with { PackageName = "com.kugou.android.fake" },
            ["an untrusted notification source"] = song with { Source = "notification" },
            ["unknown player duration"] = song with { DurationMs = 0 },
            ["conflicting player duration"] = song with { DurationMs = 184000 }
        })
        {
            var rejected = await new LyricsProvider(Area("unverified-native-context"), new NativeFixture(song, omittedLabelBody))
                .FetchAsync(pair.Value, CancellationToken.None);
            checks["Omitted release labels remain rejected with " + pair.Key] = rejected.Lines.Count == 0;
        }
        foreach (long candidateDuration in new long[] { 0, 181100 })
        {
            var rejected = await new LyricsProvider(Area("candidate-duration-proof"),
                new NativeFixture(song, omittedLabelBody, candidateDurationMs: candidateDuration)).FetchAsync(song, CancellationToken.None);
            checks["Release compatibility requires independently close lyric-candidate duration: " + candidateDuration] = rejected.Lines.Count == 0;
        }
        foreach (long catalogueDuration in new long[] { 0, 181100 })
        {
            var rejected = await new LyricsProvider(Area("catalogue-duration-proof"),
                new NativeFixture(song, omittedLabelBody, catalogueDurationMs: catalogueDuration)).FetchAsync(song, CancellationToken.None);
            checks["Release compatibility requires independently close native-catalogue duration: " + catalogueDuration] = rejected.Lines.Count == 0;
        }
        foreach (var pair in new Dictionary<string, string> {
            ["a positively conflicting Clean album"] = Lyrics(song.Title, song.Artist, "完整专辑 (Clean)"),
            ["a positively conflicting Clean title"] = Lyrics(song.Title + " (Clean)", song.Artist, "完整专辑"),
            ["a live album edition"] = Lyrics(song.Title, song.Artist, "完整专辑 (Live)"),
            ["a remix album edition"] = Lyrics(song.Title, song.Artist, "完整专辑 (Remix)"),
            ["a remix title"] = Lyrics(song.Title + " (Remix)", song.Artist, "完整专辑"),
            ["a different singer"] = Lyrics(song.Title, "另一位歌手", "完整专辑"),
            ["an omitted credited collaborator"] = Lyrics(song.Title, "验证歌手", "完整专辑"),
            ["a contradictory language label"] = Lyrics(song.Title, song.Artist, "完整专辑 (Japanese Ver.)"),
            ["an unknown edition qualifier"] = Lyrics(song.Title, song.Artist, "完整专辑 (Unknown Edition)"),
            ["a missing title header"] = Lyrics("", song.Artist, "完整专辑"),
            ["a missing artist header"] = Lyrics(song.Title, "", "完整专辑"),
            ["conflicting title headers"] = "[ti:另一首作品]\n" + omittedLabelBody,
            ["conflicting album headers"] = "[al:另一张专辑]\n" + omittedLabelBody,
            ["an out-of-recording timestamp"] = omittedLabelBody.Replace("[02:30]", "[03:01]", StringComparison.Ordinal)
        })
        {
            var rejected = await new LyricsProvider(Area("hard-recording-conflict"), new NativeFixture(song, pair.Value))
                .FetchAsync(song, CancellationToken.None);
            checks["Confirmed native release compatibility still rejects " + pair.Key] = rejected.Lines.Count == 0;
        }
        // A soundtrack release annotation needs both native recording proof and
        // the same release name in the document's independent album header.
        const string soundtrackAlbum = "Example Film Original Motion Picture Soundtrack";
        string soundtrackTitle = song.Title + " (feat. 合作歌手) (Example Film O.S.T. Version)";
        string soundtrackBody = Lyrics(soundtrackTitle, song.Artist, soundtrackAlbum);
        string soundtrackArea = Area("corroborated-soundtrack-release");
        var soundtrackFixture = new NativeFixture(doubleAlbumSong, soundtrackBody);
        var soundtrack = await new LyricsProvider(soundtrackArea, soundtrackFixture).FetchAsync(doubleAlbumSong, CancellationToken.None);
        checks["Native recording proof accepts a soundtrack release annotation corroborated by its album header"] =
            soundtrack.Lines.Count == 3 && soundtrack.Source.Contains("编号", StringComparison.Ordinal) && soundtrackFixture.CatalogRequests > 0;
        var offlineSoundtrackFixture = new NativeFixture(doubleAlbumSong, soundtrackBody);
        var offlineSoundtrack = await new LyricsProvider(soundtrackArea, offlineSoundtrackFixture)
            .FetchAsync(doubleAlbumSong, CancellationToken.None, false, false);
        checks["Corroborated soundtrack release proof survives a fresh offline provider"] =
            offlineSoundtrack.Lines.Count == 3 && offlineSoundtrackFixture.Requests == 0;
        var crossPlayerSoundtrack = await new LyricsProvider(soundtrackArea, offlineSoundtrackFixture).FetchAsync(
            doubleAlbumSong with { PackageName = "fixture.other.player", MediaId = "another-id" }, CancellationToken.None, false, false);
        checks["Soundtrack release proof is not reused as an unqualified cross-player lyric"] =
            crossPlayerSoundtrack.Lines.Count == 0 && offlineSoundtrackFixture.Requests == 0;
        foreach (var pair in new Dictionary<string, string> {
            ["a different film prefix"] = Lyrics(song.Title + " (Another Film O.S.T. Version)", song.Artist, soundtrackAlbum),
            ["an added Live title qualifier"] = Lyrics(soundtrackTitle + " (Live)", song.Artist, soundtrackAlbum),
            ["a Live qualifier hidden inside the film prefix"] = Lyrics(song.Title + " (Example Film Live O.S.T. Version)", song.Artist,
                "Example Film Live Original Motion Picture Soundtrack"),
            ["a Remix qualifier hidden inside the film prefix"] = Lyrics(song.Title + " (Example Film Remix O.S.T. Version)", song.Artist,
                "Example Film Remix Original Motion Picture Soundtrack"),
            ["a different singer at the same recording duration"] = Lyrics(soundtrackTitle, "另一位歌手", soundtrackAlbum),
            ["an explicit versus Clean album conflict"] = Lyrics(soundtrackTitle, song.Artist, soundtrackAlbum + " (Clean)"),
            ["an explicit versus Clean title conflict"] = Lyrics(soundtrackTitle + " (Clean)", song.Artist, soundtrackAlbum)
        })
        {
            var rejected = await new LyricsProvider(Area("soundtrack-hard-conflict"), new NativeFixture(doubleAlbumSong, pair.Value))
                .FetchAsync(doubleAlbumSong, CancellationToken.None);
            checks["Soundtrack release compatibility still rejects " + pair.Key] = rejected.Lines.Count == 0;
        }
        var soundtrackWithoutId = await new LyricsProvider(Area("soundtrack-without-native-id"), new NativeFixture(doubleAlbumSong, soundtrackBody))
            .FetchAsync(doubleAlbumSong with { MediaId = "" }, CancellationToken.None);
        checks["A soundtrack release annotation cannot waive title and album checks without a native identifier"] = soundtrackWithoutId.Lines.Count == 0;
        var soundtrackWithoutCandidateDuration = await new LyricsProvider(Area("soundtrack-without-candidate-duration"),
            new NativeFixture(doubleAlbumSong, soundtrackBody, candidateDurationMs: 0)).FetchAsync(doubleAlbumSong, CancellationToken.None);
        checks["A soundtrack release annotation requires independently known lyric-candidate duration"] = soundtrackWithoutCandidateDuration.Lines.Count == 0;
        const string frostedAlbum = "Frosted Original Motion Picture Soundtrack";
        var exactFrosted = await new LyricsProvider(Area("soundtrack-name-containing-ost"),
            new NativeFixture(doubleAlbumSong, Lyrics(song.Title + " (Frosted O.S.T. Version)", song.Artist, frostedAlbum)))
            .FetchAsync(doubleAlbumSong, CancellationToken.None);
        checks["Soundtrack suffix removal preserves OST letters inside the actual film name"] =
            exactFrosted.Lines.Count == 3 && exactFrosted.Source.Contains("编号", StringComparison.Ordinal);
        var wrongFred = await new LyricsProvider(Area("soundtrack-name-substring-conflict"),
            new NativeFixture(doubleAlbumSong, Lyrics(song.Title + " (Fred O.S.T. Version)", song.Artist, frostedAlbum)))
            .FetchAsync(doubleAlbumSong, CancellationToken.None);
        checks["Removing a soundtrack suffix cannot turn Frosted into the different film Fred"] = wrongFred.Lines.Count == 0;
        foreach (string qualifier in new[] { "铃声", "特别版", "精简版" })
        {
            string filmName = "Example Film " + qualifier;
            var rejected = await new LyricsProvider(Area("soundtrack-chinese-version-conflict"),
                new NativeFixture(doubleAlbumSong, Lyrics(song.Title + " (" + filmName + " O.S.T. Version)", song.Artist,
                    filmName + " Original Motion Picture Soundtrack"))).FetchAsync(doubleAlbumSong, CancellationToken.None);
            checks["A soundtrack film prefix cannot erase a Chinese recording qualifier: " + qualifier] = rejected.Lines.Count == 0;
        }

        var allDiagnostics = new List<string>();
        async Task<(LyricsResult Result, string[] Messages)> WithDiagnostics(string name, NativeFixture fixture)
        {
            var messages = new ConcurrentQueue<string>();
            var provider = new LyricsProvider(Area(name), fixture);
            provider.Diagnostic += messages.Enqueue;
            var fetched = await provider.FetchAsync(song, CancellationToken.None);
            string[] captured = messages.ToArray();
            allDiagnostics.AddRange(captured);
            return (fetched, captured);
        }
        var compatibleDecision = await WithDiagnostics("compatible-release-diagnostic", new NativeFixture(song, omittedLabelBody));
        var compatibleDetails = DiagnosticJson(compatibleDecision.Messages, "native-release-compatible: ");
        checks["Native release decisions log player and header albums, candidate duration and enabled proof"] = compatibleDecision.Result.Lines.Count == 3
            && compatibleDetails.Any(detail => Text(Child(detail, "requested"), "title") == song.Title
                && Text(Child(detail, "requested"), "album") == song.Album
                && Text(Child(detail, "headers"), "album") == "完整专辑"
                && Number(Child(detail, "candidate"), "durationMs") == song.DurationMs
                && Flag(Child(detail, "evidence"), "enabled", true)
                && Text(Child(detail, "evidence"), "state") == "verified");
        string cleanBody = Lyrics(song.Title, song.Artist, "完整专辑 (Clean)");
        var cleanDecision = await WithDiagnostics("conflicting-edition-diagnostic", new NativeFixture(song, cleanBody));
        var cleanDetails = DiagnosticJson(cleanDecision.Messages, "lyric-body-rejected: source=kugou;");
        checks["Rejected Clean editions log the actual album conflict and recording proof state"] = cleanDecision.Result.Lines.Count == 0
            && cleanDetails.Any(detail => Text(detail, "reason") == "album-conflict"
                && Text(Child(detail, "requested"), "album") == song.Album
                && Text(Child(detail, "headers"), "album") == "完整专辑 (Clean)"
                && Number(Child(detail, "candidate"), "durationMs") == song.DurationMs
                && Flag(Child(detail, "evidence"), "enabled", true));
        var missingDurationDecision = await WithDiagnostics("missing-candidate-duration-diagnostic",
            new NativeFixture(song, omittedLabelBody, candidateDurationMs: 0));
        checks["Missing candidate duration is diagnosed as disabled native proof"] = missingDurationDecision.Result.Lines.Count == 0
            && DiagnosticJson(missingDurationDecision.Messages, "lyric-body-rejected: source=kugou;").Any(detail =>
                Text(detail, "route") == "native-id" && Number(Child(detail, "candidate"), "durationMs") == 0
                && Flag(Child(detail, "evidence"), "enabled", false)
                && Text(Child(detail, "evidence"), "state") == "candidate-duration-missing");
        var catalogueConflictDecision = await WithDiagnostics("catalogue-duration-diagnostic",
            new NativeFixture(song, omittedLabelBody, catalogueDurationMs: 181100));
        checks["Native catalogue duration conflicts log an invalid proof and the precise duration delta"] = catalogueConflictDecision.Result.Lines.Count == 0
            && DiagnosticJson(catalogueConflictDecision.Messages, "native-recording-proof: ").Any(detail =>
                Flag(detail, "valid", false) && Text(detail, "reason") == "catalog-duration-conflict"
                && Number(detail, "durationDeltaMs") == 1100
                && Number(detail, "catalogDurationMs") == 181100 && Number(detail, "playerDurationMs") == song.DurationMs);
        checks["An invalid native catalogue proof is not misleadingly logged as a resolved native recording"] =
            !catalogueConflictDecision.Messages.Any(message => message.StartsWith("recording-id-resolved: ", StringComparison.Ordinal));
        var repeatedDecision = await WithDiagnostics("duplicate-rejection-diagnostic", new NativeFixture(song, cleanBody, candidateCopies: 3));
        checks["Repeated rejected lyric headers are summarized without repeating every identical detail"] = repeatedDecision.Result.Lines.Count == 0
            && DiagnosticJson(repeatedDecision.Messages, "lyric-rejections-summary: ").Any(detail =>
                Text(detail, "route") == "native-id" && Number(detail, "rejectedCount") == 3
                && Number(detail, "distinctExamples") == 1 && Number(detail, "repeated") == 2);
        foreach (string invalidDuration in new[] { "NaN", "Infinity", "-Infinity" })
        {
            var invalidDecision = await WithDiagnostics("nonfinite-candidate-duration",
                new NativeFixture(song, omittedLabelBody, candidateDurationText: invalidDuration));
            var invalidDetails = DiagnosticJson(invalidDecision.Messages, "lyric-body-rejected: source=kugou;");
            bool nativeProofChecked = DiagnosticJson(invalidDecision.Messages, "native-recording-proof: ").Any(detail => Flag(detail, "valid", true));
            bool waiverReported = DiagnosticJson(invalidDecision.Messages, "native-release-compatible: ").Any(detail => Flag(Child(detail, "evidence"), "enabled", true));
            bool disabledWhenDownloaded = invalidDuration != "NaN" || invalidDetails.Any(detail =>
                Text(detail, "route") == "native-id" && Flag(Child(detail, "evidence"), "enabled", false));
            checks["A nonfinite candidate duration cannot enable release compatibility or break diagnostic serialization: " + invalidDuration] =
                invalidDecision.Result.Lines.Count == 0 && nativeProofChecked && !waiverReported && disabledWhenDownloaded
                && invalidDecision.Result.Outcome is not (LyricsOutcome.ServiceFailure or LyricsOutcome.PartialFailure);
        }
        string encodedOmittedBody = Convert.ToBase64String(Encoding.UTF8.GetBytes(omittedLabelBody));
        string encodedCleanBody = Convert.ToBase64String(Encoding.UTF8.GetBytes(cleanBody));
        checks["Structured lookup diagnostics omit lyric text, encoded documents and access keys"] = allDiagnostics.Count > 0
            && !allDiagnostics.Any(message => message.Contains("[00:00]", StringComparison.Ordinal)
                || message.Contains(encodedOmittedBody, StringComparison.Ordinal) || message.Contains(encodedCleanBody, StringComparison.Ordinal)
                || message.Contains(FixtureAccessKey, StringComparison.Ordinal) || message.Contains("accesskey", StringComparison.OrdinalIgnoreCase)
                || message.Contains("\"content\"", StringComparison.OrdinalIgnoreCase));
        checks["A positive unbracketed Dirty edition conflicts with a Clean native album"] = !RecordingRules.AlbumLabelsCompatible("Release (Clean)","Other Release Dirty");
        checks["A positive unbracketed UncensoredVersion conflicts with a Clean native album"] = !RecordingRules.AlbumLabelsCompatible("Release (Clean)","Other Release UncensoredVersion");
        checks["Equivalent unbracketed content-version labels retain the same native edition"] = RecordingRules.AlbumLabelsCompatible("Release (Explicit)","Other Release Uncensored Version");
        return checks;
    }

    private static string Lyrics(string title, string artist, string album) =>
        "[ti:" + title + "]\n[ar:" + artist + "]\n[al:" + album + "]\n[00:00]验证首句\n[01:00]验证中句\n[02:30]验证末句";
    private const string FixtureAccessKey = "fixture-access-secret-4f12";
    private static JsonElement Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) ? child : default;
    private static string Text(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }
    private static double Number(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) ? number : double.NaN;
    }
    private static bool Flag(JsonElement parent, string name, bool expected) =>
        Child(parent, name).ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);
    private static List<JsonElement> DiagnosticJson(IEnumerable<string> messages, string prefix)
    {
        var result = new List<JsonElement>();
        foreach (string message in messages.Where(message => message.StartsWith(prefix, StringComparison.Ordinal)))
        {
            const string detailMarker = "; detail=";
            int detailStart = message.IndexOf(detailMarker, StringComparison.Ordinal);
            string json = detailStart >= 0 ? message[(detailStart + detailMarker.Length)..] : message[prefix.Length..];
            try { using var document = JsonDocument.Parse(json); result.Add(document.RootElement.Clone()); }
            catch (JsonException) { }
        }
        return result;
    }    private static string Download(string content) => JsonSerializer.Serialize(new { content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) });
    private sealed class NativeFixture(MediaSnapshot catalogue, string content, long? candidateDurationMs = null,
        long? catalogueDurationMs = null, int candidateCopies = 1, string? candidateDurationText = null) : HttpMessageHandler
    {
        public int Requests, CatalogRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Requests);
            var uri = request.RequestUri!;
            string response;
            if (uri.Host == "songsearch.kugou.com")
            {
                Interlocked.Increment(ref CatalogRequests);
                response = JsonSerializer.Serialize(new { data = new { lists = new[] { new {
                    ID = catalogue.MediaId, MixSongID = catalogue.MediaId, SongName = catalogue.Title, SingerName = catalogue.Artist,
                    AlbumName = catalogue.Album, Duration = (catalogueDurationMs ?? catalogue.DurationMs) / 1000.0,
                    FileHash = "ABCDEF0123456789ABCDEF0123456789"
                } } } });
            }
            else if (uri.Host is "lyrics.kugou.com" or "krcs.kugou.com")
                response = uri.AbsolutePath.EndsWith("search", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new { candidates = Enumerable.Range(0, candidateCopies).Select(index => new {
                        song = catalogue.Title, singer = catalogue.Artist,
                        duration = candidateDurationText == null ? (object)(candidateDurationMs ?? catalogue.DurationMs) : candidateDurationText,
                        id = "native-release-fixture-" + index, accesskey = FixtureAccessKey
                    }).ToArray() })
                    : Download(content);
            else response = uri.Host == "lrclib.net" ? "[]"
                : uri.Host == "music.163.com" ? "{\"code\":200,\"result\":{\"songs\":[]}}" : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
        }
    }
}
