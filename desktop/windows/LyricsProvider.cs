using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LyricBridge;

public enum LyricsOutcome { Unknown, Success, NoMatch, Unsynced, Instrumental, ServiceFailure, PartialFailure, Unreliable, ReadFailure }
public sealed record LyricsResult(List<LyricLine> Lines, string Source, string PlainText = "", bool IsInstrumental = false,
    LyricsOutcome Outcome = LyricsOutcome.Unknown)
{
    public bool FromProvidedLyrics { get; init; }
    public bool NeedsSourceReview { get; init; }
    internal RecordingEvidence? RecordingEvidence { get; init; }
    internal string Provider { get; init; } = "";
    public string EmptyMessage => Outcome switch {
        LyricsOutcome.ServiceFailure => "歌词服务暂时连接失败",
        LyricsOutcome.PartialFailure => "部分歌词来源暂时连接失败",
        LyricsOutcome.Unreliable => "暂未取得可靠的同步歌词",
        LyricsOutcome.ReadFailure => "歌词读取暂时失败",
        LyricsOutcome.Unsynced => "暂时没有找到同步歌词",
        _ => PlainText.Length > 0 ? "暂时没有找到同步歌词" : "暂时没有找到匹配歌词"
    };
}
internal sealed class UnreliableLyricsException : Exception
{
    public UnreliableLyricsException() : base("No trustworthy lyric recording or timeline remained") { }
}
public sealed class LyricsProvider
{
    private readonly HttpClient http;
    private readonly HttpClient? domestic;
    private readonly string cachePath;
    private readonly RecordingCache recordingCache;
    public event Action<string>? Diagnostic;
    private sealed record SourceAttempt(LyricsResult Result, bool Failed = false);
    private sealed record SongMatch(string Hash, string Title, string Artist, double Duration, double Score);
    private sealed record KugouDownload(string Content, bool Failed, bool UseEvidence, double Duration, string Id, string Title, string Artist, string EvidenceState);

    public LyricsProvider(string dataPath, HttpMessageHandler? handler = null, HttpMessageHandler? directHandler = null)
    {
        http = CreateClient(handler ?? Transport(true));
        // Test fixtures keep their single injected transport unless both routes are supplied explicitly.
        domestic = handler == null || directHandler != null ? CreateClient(directHandler ?? Transport(false)) : null;
        cachePath = Path.Combine(dataPath, "lyrics"); Directory.CreateDirectory(cachePath);
        recordingCache = new RecordingCache(dataPath, message => Diagnostic?.Invoke(message));
    }
    public Task<LyricsResult> FetchAsync(MediaSnapshot snapshot, CancellationToken token) => FetchAsync(snapshot, token, false, true);
    public async Task<LyricsResult> FetchAsync(MediaSnapshot snapshot, CancellationToken token, bool bypassCache, bool allowNetwork)
    {
        token.ThrowIfCancellationRequested();
        if (snapshot.Type == "idle") return new(new(), "等待播放");
        if (ProvidedLyrics.TryValidate(snapshot, out var provided, out string providedReason))
        {
            await RememberProvidedAsync(snapshot, token);
            return provided with { PlainText = "", Outcome = LyricsOutcome.Success };
        }
        if (!string.IsNullOrEmpty(snapshot.ProvidedLyrics)) Diagnostic?.Invoke("provided-lyrics-rejected: " + providedReason);
        string cache = PrivateCachePath(snapshot);
        LyricsResult? cacheRejection = null;
        if (!bypassCache || !allowNetwork)
        {
            var cached = await ReadPrivateCacheAsync(cache, snapshot, token);
            if (cached?.Lines.Count > 0) return cached;
            cacheRejection = cached;
            var shared = await recordingCache.TryReadAsync(snapshot, token);
            if (shared != null) return shared with { Outcome = LyricsOutcome.Success };
        }
        if (!allowNetwork) return cacheRejection ?? new(new(), "本地暂无缓存歌词（已关闭联网匹配）", Outcome: LyricsOutcome.NoMatch);
        using var query = CancellationTokenSource.CreateLinkedTokenSource(token);
        query.CancelAfter(TimeSpan.FromSeconds(18));
        bool platformSearch = PlatformLyrics.SupportsSearch(snapshot);
        bool nativePlatform = platformSearch || IsKugou(snapshot);
        Task? identityDeadline = nativePlatform ? Task.Delay(2200, query.Token) : null;
        Diagnostic?.Invoke("platform-route: " + snapshot.PackageName + "; native=" + nativePlatform
            + "; id=" + (PlatformLyrics.Supports(snapshot) || HasKugouId(snapshot)));
        var preferred = platformSearch ? QuerySourceAsync(PreferredPlatformAsync, snapshot, query.Token)
            : PreferredKugouAsync(snapshot, query.Token);
        var alternate = QuerySourceAsync(LrclibAsync, snapshot, query.Token);
        var pending = new List<Task<SourceAttempt>> { preferred, alternate };
        // Other libraries prepare results concurrently, but a valid native recording
        // gets a bounded preference window. Never send duplicate NetEase searches.
        if (platformSearch) pending.Add(QuerySourceAsync(KugouAsync, snapshot, query.Token));
        if (snapshot.PackageName != "com.netease.cloudmusic")
            pending.Add(QuerySourceAsync(DelayedNeteaseSearchAsync, snapshot, query.Token));
        LyricsResult? plain = null, instrumental = null;
        Task? confirmationDeadline = null;
        bool failed = false, reachable = false, unreliable = cacheRejection != null;
        while (pending.Count > 0)
        {
            if (confirmationDeadline != null)
            {
                var wake = await Task.WhenAny(pending.Cast<Task>().Append(confirmationDeadline));
                if (wake == confirmationDeadline)
                {
                    token.ThrowIfCancellationRequested();
                    query.Cancel();
                    if (failed) return new(new(), "部分歌词来源无法连接", Outcome: LyricsOutcome.PartialFailure);
                    if (plain != null) return plain with { Outcome = LyricsOutcome.Unsynced };
                    return new(new(), "纯音乐标记尚未得到其他来源确认", Outcome: LyricsOutcome.Unreliable);
                }
            }
            var finished = await Task.WhenAny(pending); pending.Remove(finished);
            var attempt = await finished; token.ThrowIfCancellationRequested();
            var result = attempt.Result;
            if (identityDeadline != null && finished != preferred && pending.Contains(preferred)
                && (result.Lines.Count > 0 || result.IsInstrumental))
            {
                await Task.WhenAny(preferred, identityDeadline);
                token.ThrowIfCancellationRequested();
                if (preferred.IsCompleted)
                {
                    pending.Remove(preferred);
                    var exact = await preferred;
                    failed |= exact.Failed; reachable |= !exact.Failed;
                    unreliable |= exact.Result.Outcome == LyricsOutcome.Unreliable;
                    // This task is removed from pending here, so retain its evidence
                    // even when the ready alternative remains the chosen result.
                    if (exact.Result.PlainText.Length > 0 && exact.Result.Lines.Count == 0) plain ??= exact.Result;
                    if (exact.Result.IsInstrumental)
                    {
                        instrumental ??= exact.Result;
                        confirmationDeadline ??= Task.Delay(1500, query.Token);
                    }
                    if (exact.Result.Lines.Count > 0) result = exact.Result;
                }
            }
            failed |= attempt.Failed; reachable |= !attempt.Failed;
            unreliable |= result.Outcome == LyricsOutcome.Unreliable;
            if (result.IsInstrumental)
            {
                instrumental ??= result;
                confirmationDeadline ??= Task.Delay(1500, query.Token);
                continue;
            }
            if (result.Lines.Count > 0)
            {
                Diagnostic?.Invoke("lyrics-selected: " + result.Source);
                query.Cancel();
                bool nativeVerified = NativeFamily(snapshot.PackageName).Length > 0 && NativeFamily(snapshot.PackageName) == result.Provider;
                await StoreCacheAsync(cache, result.PlainText, token, nativeVerified: nativeVerified, recordingEvidence: result.RecordingEvidence, provider: result.Provider);
                await recordingCache.StoreAsync(snapshot, result.PlainText, result.Source, token,
                    nativeVerified: nativeVerified, strategy: MatchingStrategy, checkedAtUtc: DateTimeOffset.UtcNow, provider: result.Provider, recordingEvidence: result.RecordingEvidence);
                return result with { PlainText = "", Outcome = LyricsOutcome.Success,
                    NeedsSourceReview = !nativeVerified && NativeFamily(snapshot.PackageName).Length > 0 };
            }
            if (result.PlainText.Length > 0) plain ??= result;
        }
        if (failed) return new(new(), reachable ? "暂未取得同步歌词（部分来源无法连接，将自动重试）" : "歌词服务暂时无法连接",
            plain?.PlainText ?? "", Outcome: reachable ? LyricsOutcome.PartialFailure : LyricsOutcome.ServiceFailure);
        if (plain != null) return plain with { Outcome = LyricsOutcome.Unsynced };
        if (unreliable) return new(new(), "歌词录音或时间轴需要重新确认", Outcome: LyricsOutcome.Unreliable);
        return instrumental != null ? instrumental with { Outcome = LyricsOutcome.Instrumental }
            : new(new(), "暂未收录可用的同步歌词", Outcome: LyricsOutcome.NoMatch);
    }
    public async Task RememberProvidedAsync(MediaSnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ProvidedLyrics.TryValidate(snapshot, out var provided, out _)) return;
        await StoreCacheAsync(PrivateCachePath(snapshot), provided.PlainText, token, "provided");
        await recordingCache.StoreAsync(snapshot, provided.PlainText, provided.Source, token);
    }

    private const int MaximumPrivateContentChars = 524288;
    private const long MaximumPrivateDocumentBytes = 4194304;
    internal const int MatchingStrategy = 2;
    private sealed record PrivateCacheEvidence(int Schema, string Kind, string Hash, string? Content = null,
        int Strategy = 0, bool NativeVerified = false, DateTimeOffset CheckedAtUtc = default, RecordingEvidence? RecordingEvidence = null, string Provider = "");
    internal static string NativeFamily(string package) => package switch {
        "com.kugou.android" or "com.kugou.android.lite" => "kugou",
        "com.tencent.qqmusic" => "qq", "com.netease.cloudmusic" => "netease", _ => ""
    };
    internal static bool CacheNeedsSourceReview(MediaSnapshot snapshot, int strategy, bool nativeVerified,
        DateTimeOffset checkedAtUtc, string? originPackage = null)
    {
        string family = NativeFamily(snapshot.PackageName);
        if (family.Length == 0) return false;
        if (strategy != MatchingStrategy || checkedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5)) return true;
        if (originPackage != null && family != NativeFamily(originPackage)) return true;
        return !nativeVerified && checkedAtUtc <= DateTimeOffset.UtcNow.AddHours(-6);
    }
    private static bool ExplicitProof(JsonElement proof) => new[] { "Provider", "Basis", "CatalogId" }.All(key =>
        proof.TryGetProperty(key,out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()));
    private static string ContentHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private async Task<LyricsResult?> ReadPrivateCacheAsync(string path, MediaSnapshot snapshot, CancellationToken token)
    {
        try
        {
            string evidencePath = path + ".json", content;
            PrivateCacheEvidence? evidence = null;
            if (File.Exists(evidencePath))
            {
                if (new FileInfo(evidencePath).Length > MaximumPrivateDocumentBytes) return null;
                evidence = JsonSerializer.Deserialize<PrivateCacheEvidence>(await File.ReadAllTextAsync(evidencePath, token));
                if (evidence == null || evidence.Schema is not (1 or 2 or 3 or 4 or 5) || evidence.Kind is not ("network" or "provided")) return null;
                if (evidence.Schema >= 5 && evidence.RecordingEvidence != null)
                {
                    using var envelope = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath,token));
                    if (!envelope.RootElement.TryGetProperty("RecordingEvidence",out var proof)
                        || proof.ValueKind != JsonValueKind.Object || !ExplicitProof(proof))
                    { Diagnostic?.Invoke("cache-rejected: missing-provider-or-proof-namespace"); return null; }
                }
                if (evidence.Schema >= 2)
                {
                    // Content and provenance commit together; the compatibility mirror
                    // may be absent or stale after its optional replacement fails.
                    if (evidence.Content == null || evidence.Content.Length > MaximumPrivateContentChars) return null;
                    content = evidence.Content;
                }
                else
                {
                    if (!File.Exists(path) || new FileInfo(path).Length > 2097152) return null;
                    content = await File.ReadAllTextAsync(path, token);
                }
                if (content.Length > MaximumPrivateContentChars || evidence.Hash != ContentHash(content)) return null;
            }
            else
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 2097152) return null;
                content = await File.ReadAllTextAsync(path, token);
                if (content.Length > MaximumPrivateContentChars) return null;
            }
            bool fromProvided = evidence?.Kind == "provided" || evidence == null && snapshot.Source == "integration";
            if (!ProvidedLyrics.CanReuseCached(snapshot) && (fromProvided || evidence == null))
            {
                Diagnostic?.Invoke("cache-rejected: withdrawn-or-unverified-provided-document");
                return null;
            }
            var recordingEvidence = !fromProvided && evidence?.Schema >= 4 ? evidence.RecordingEvidence : null;
            if (recordingEvidence != null && (!recordingEvidence.Matches(snapshot)
                || evidence?.Schema >= 5 && recordingEvidence.Provider != evidence.Provider))
            {
                Diagnostic?.Invoke("cache-evidence-dropped: origin-or-recording-mismatch");
                recordingEvidence = null; // Independently strict headers may still be reusable.
            }
            var lines = ParseLyricsWithEvidence(content, snapshot, recordingEvidence);
            if (lines.Count == 0) { Diagnostic?.Invoke("cache-rejected: untrusted-recording-or-timeline");
                return new(new(), "缓存歌词录音或时间轴需要重新确认", Outcome: LyricsOutcome.Unreliable); }
            await recordingCache.StoreAsync(snapshot, content, fromProvided ? "播放器提供的歌词" : "本地缓存", token,
                nativeVerified: evidence?.NativeVerified ?? false, strategy: evidence?.Strategy ?? 0,
                checkedAtUtc: evidence?.CheckedAtUtc ?? DateTimeOffset.MinValue, provider: evidence?.Provider ?? "", recordingEvidence: recordingEvidence);
            return new(lines, "本地缓存", Outcome: LyricsOutcome.Success) {
                FromProvidedLyrics = fromProvided, RecordingEvidence = recordingEvidence, Provider = evidence?.Provider ?? "",
                NeedsSourceReview = !fromProvided && CacheNeedsSourceReview(snapshot, evidence?.Strategy ?? 0,
                    evidence?.NativeVerified == true && NativeFamily(snapshot.PackageName) == evidence.Provider, evidence?.CheckedAtUtc ?? DateTimeOffset.MinValue)
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Diagnostic?.Invoke("cache-read-failed: " + error.GetType().Name); return null; }
    }

    private string PrivateCachePath(MediaSnapshot snapshot) =>
        Path.Combine(cachePath, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.TrackKey))) + ".lrc");

    private async Task StoreCacheAsync(string cache, string content, CancellationToken token, string kind = "network", bool nativeVerified = false, RecordingEvidence? recordingEvidence = null, string provider = "")
    {
        if (content.Length > MaximumPrivateContentChars) return;
        string temporary = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string evidenceTemporary = temporary + ".json";
        try
        {
            await File.WriteAllTextAsync(temporary, content, token);
            await File.WriteAllTextAsync(evidenceTemporary, JsonSerializer.Serialize(new PrivateCacheEvidence(5, kind, ContentHash(content), content, MatchingStrategy, nativeVerified, DateTimeOffset.UtcNow, recordingEvidence, provider)), token);
            token.ThrowIfCancellationRequested();
            // The authoritative document is one atomic replacement. If it fails,
            // the previous pair stays unchanged. The LRC is a compatibility mirror,
            // never a fallback for corrupt or explicitly withdrawn evidence.
            File.Move(evidenceTemporary, cache + ".json", true);
            File.Move(temporary, cache, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Diagnostic?.Invoke("cache-write-failed: " + error.Message); }
        finally
        {
            try { File.Delete(temporary); File.Delete(evidenceTemporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Diagnostic?.Invoke("cache-cleanup-failed: " + error.Message); }
        }
    }
    private async Task<SourceAttempt> QuerySourceAsync(Func<MediaSnapshot, CancellationToken, Task<LyricDocument>> source, MediaSnapshot snapshot, CancellationToken token)
    {
        try { return new((await source(snapshot, token)).Validate(snapshot, Diagnostic)); }
        catch (UnreliableLyricsException) { return new(new(new(), "歌词录音或时间轴需要重新确认", Outcome: LyricsOutcome.Unreliable)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(new(new(), "查询已取消"), true); }
        catch (Exception error)
        {
            Diagnostic?.Invoke(source.Method.Name + ": " + error.GetType().Name + ": " + error.Message);
            return new(new(new(), "歌词服务暂时无法连接"), true);
        }
    }
    private static LyricDocument DocumentOf(LyricsResult result) =>
        new(result.PlainText, result.Source, result.Provider, result.RecordingEvidence);
    public async Task WarmUpAsync(CancellationToken token = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        await Task.WhenAll(new[] { "lyrics.kugou.com", "krcs.kugou.com", "lrclib.net" }.Select(async host =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, "https://" + host + "/");
                using var response = await ((DomesticHost(host) ? domestic : null) ?? http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            }
            catch (Exception) { /* Warm-up is optional; normal lookups still handle failures. */ }
        }));
    }
    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 LocalLyricBridge/0.7.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
    private static SocketsHttpHandler Transport(bool useProxy) => new() {
        UseProxy = useProxy, ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = 6,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    };
    private static bool DomesticHost(string host) => host is "lyrics.kugou.com" or "krcs.kugou.com" or "songsearch.kugou.com" or "music.163.com" or "c.y.qq.com";
    private async Task<JsonDocument> GetSingleJsonAsync(string url, CancellationToken token, string? referer = null)
    {
        if (domestic == null || !DomesticHost(new Uri(url).Host)) return await GetRouteJsonAsync(http, url, token, referer);
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task<JsonDocument> Backup()
        {
            await Task.Delay(650, race.Token);
            Diagnostic?.Invoke("route-fallback: " + new Uri(url).Host);
            return await GetRouteJsonAsync(http, url, race.Token, referer);
        }
        var pending = new List<Task<JsonDocument>> { GetRouteJsonAsync(domestic, url, race.Token, referer), Backup() };
        Exception? failure = null;
        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending); pending.Remove(completed);
                try { var document = await completed; token.ThrowIfCancellationRequested(); return document; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { failure = error; }
            }
            throw new HttpRequestException("Lyric network routes failed", failure);
        }
        finally { race.Cancel(); _ = DisposePendingJsonAsync(pending); }
    }
    private static async Task DisposePendingJsonAsync(IEnumerable<Task<JsonDocument>> tasks)
    {
        foreach (var task in tasks) { try { using var document = await task; } catch (Exception) { } }
    }

    private async Task<JsonDocument> GetRouteJsonAsync(HttpClient client, string url, CancellationToken token, string? referer = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (referer != null) request.Headers.Referrer = new Uri(referer);
                using var response = await client.SendAsync(request, token);
                response.EnsureSuccessStatusCode();
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            }
            catch (HttpRequestException error) when (attempt == 0 && !token.IsCancellationRequested &&
                (error.StatusCode == null || error.StatusCode == System.Net.HttpStatusCode.RequestTimeout || (int)error.StatusCode >= 500))
            { Diagnostic?.Invoke("transport-retry: " + new Uri(url).Host); }
            catch (OperationCanceledException) when (attempt == 0 && !token.IsCancellationRequested)
            { Diagnostic?.Invoke("timeout-retry: " + new Uri(url).Host); }
            await Task.Delay(180, token);
        }
    }
    private static IEnumerable<(string Title, string Artist)> QueryNames(MediaSnapshot s)
    {
        static string OriginalLabel(string value) => Regex.Replace(value, @"[\(\[（【]\s*(?:original(?:\s+version)?|原版|正式版)\s*[\)\]）】]", "", RegexOptions.IgnoreCase).Trim();
        return new[] { (s.Title.Trim(), s.Artist.Trim()), (ChineseText.Simplify(s.Title).Trim(), ChineseText.Simplify(s.Artist).Trim()),
            (OriginalLabel(s.Title), s.Artist.Trim()), (OriginalLabel(ChineseText.Simplify(s.Title)), ChineseText.Simplify(s.Artist).Trim()) }
            .Where(pair => pair.Item1.Length > 0).Distinct();
    }

    private async Task<LyricDocument> DelayedNeteaseSearchAsync(MediaSnapshot s, CancellationToken token)
    {
        await Task.Delay(500, token);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(2500);
        return await PlatformLyrics.SearchNeteaseDocumentAsync(s, (url, referer, cancellation) => GetSingleJsonAsync(url, cancellation, referer), budget.Token, Diagnostic);
    }

    private async Task<LyricDocument> PreferredPlatformAsync(MediaSnapshot s, CancellationToken token)
    {
        using var nativeBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
        nativeBudget.CancelAfter(2200);
        using var idBudget = CancellationTokenSource.CreateLinkedTokenSource(nativeBudget.Token);
        idBudget.CancelAfter(1800);
        Task<SourceAttempt>? identity = PlatformLyrics.Supports(s)
            ? QuerySourceAsync((snapshot, cancellation) => PlatformLyrics.FetchDocumentAsync(snapshot,
                (url, referer, ct) => GetSingleJsonAsync(url, ct, referer), cancellation, Diagnostic), s, idBudget.Token)
            : null;
        async Task<LyricDocument> Metadata(MediaSnapshot snapshot, CancellationToken cancellation)
        {
            if (identity != null) await Task.WhenAny(identity, Task.Delay(250, cancellation));
            cancellation.ThrowIfCancellationRequested();
            // A synchronous identity response need not trigger an unnecessary search.
            if (identity?.IsCompletedSuccessfully == true && identity.Result.Result.Lines.Count > 0)
                return new("", "", NativeFamily(snapshot.PackageName));
            return await PlatformLyrics.SearchDocumentAsync(snapshot,
                (url, referer, ct) => GetSingleJsonAsync(url, ct, referer), cancellation, Diagnostic);
        }
        var metadata = QuerySourceAsync(Metadata, s, nativeBudget.Token);
        SourceAttempt? exact = null;
        try
        {
            if (identity != null)
            {
                exact = await identity;
                token.ThrowIfCancellationRequested();
                if (exact.Result.Lines.Count > 0)
                {
                    Diagnostic?.Invoke("platform-id-resolved: " + s.PackageName);
                    return DocumentOf(exact.Result);
                }
                Diagnostic?.Invoke("platform-id-fallback: " + exact.Result.Outcome);
            }
            var searched = await metadata;
            token.ThrowIfCancellationRequested();
            if (searched.Result.Lines.Count > 0)
            {
                Diagnostic?.Invoke("platform-metadata-resolved: " + s.PackageName);
                return DocumentOf(searched.Result);
            }
            var plain = searched.Result.PlainText.Length > 0 ? searched.Result : exact?.Result;
            if (plain?.PlainText.Length > 0) return DocumentOf(plain);
            if (searched.Failed || exact?.Failed == true)
                throw new HttpRequestException("Native lyric lookup failed or exceeded its time budget");
            if (searched.Result.Outcome == LyricsOutcome.Unreliable || exact?.Result.Outcome == LyricsOutcome.Unreliable)
                throw new UnreliableLyricsException();
            return new("", searched.Result.Source, NativeFamily(s.PackageName));
        }
        finally
        {
            nativeBudget.Cancel();
            // QuerySourceAsync observes request failures and cancellation, including
            // a speculative metadata request cancelled by a successful native ID.
        }
    }

    private static bool IsKugou(MediaSnapshot s) => s.PackageName is "com.kugou.android" or "com.kugou.android.lite";
    private static bool HasKugouId(MediaSnapshot s) => IsKugou(s)
        && Regex.IsMatch(s.MediaId, @"^[1-9][0-9]{0,19}$");
    private async Task<SourceAttempt> PreferredKugouAsync(MediaSnapshot s, CancellationToken token)
    {
        bool unreliable = false;
        if (HasKugouId(s))
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromMilliseconds(2200));
            try
            {
                foreach (var (title, artist) in QueryNames(s))
                {
                    string query = Uri.EscapeDataString((artist + " " + title).Trim());
                    using var response = await GetSingleJsonAsync($"https://songsearch.kugou.com/song_search_v2?keyword={query}&page=1&pagesize=30&platform=WebFilter",budget.Token);
                    if (!response.RootElement.TryGetProperty("data",out var data) || !data.TryGetProperty("lists",out var rows) || rows.ValueKind!=JsonValueKind.Array) continue;
                    foreach (var row in rows.EnumerateArray().Where(row=>Text(row,"ID")==s.MediaId || Text(row,"MixSongID")==s.MediaId))
                    {
                        string hash=Text(row,"FileHash"); double duration=Number(row,"Duration")*1000;
                        string catalogIssue = !Regex.IsMatch(hash,"^[A-Fa-f0-9]{32}$") ? "file-hash-missing"
                            : Score(s,Name(row,"SongName"),Name(row,"SingerName"),duration)<65 ? "title-artist-or-duration-conflict"
                            : !RecordingRules.AlbumsCompatible(s.Album,Name(row,"AlbumName"),s.Artist) ? "catalog-album-conflict" : "";
                        RecordingEvidence? recordingEvidence = RecordingEvidence.Native("kugou",s,"id:"+s.MediaId,
                            Name(row,"SongName"), Name(row,"SingerName"), Name(row,"AlbumName"), SafeDuration(duration),hash);
                        bool proofValid = recordingEvidence.Matches(s, out string proofReason);
                        Diagnostic?.Invoke("native-recording-proof: " + JsonSerializer.Serialize(new {
                            title = DiagnosticText(s.Title), mediaId = DiagnosticText(s.MediaId), valid = proofValid, reason = catalogIssue.Length > 0 ? catalogIssue : proofReason,
                            playerAlbum = DiagnosticText(s.Album), catalogAlbum = DiagnosticText(recordingEvidence.Album),
                            catalogTitle = DiagnosticText(recordingEvidence.Title), catalogArtist = DiagnosticText(recordingEvidence.Artist),
                            playerDurationMs = s.DurationMs, catalogDurationMs = double.IsFinite(duration) ? (double?)duration : null, durationDeltaMs = double.IsFinite(duration) ? (double?)Math.Abs(duration - s.DurationMs) : null
                        }));
                        if (catalogIssue.Length > 0) continue;
                        if (!proofValid) recordingEvidence = null;
                        else Diagnostic?.Invoke("recording-id-resolved: " + s.MediaId);
                        var document=await KugouLyricsAsync(s,Name(row,"SongName"),Name(row,"SingerName"),hash,duration,budget.Token,recordingEvidence,"native-id");
                        var validated = (document with { Source = "酷狗歌词 · 歌曲编号匹配" }).Validate(s, Diagnostic);
                        if(validated.Lines.Count>0)return new(validated);
                    }
                }
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(UnreliableLyricsException){unreliable = true;}
            catch(Exception error){Diagnostic?.Invoke("recording-id-fallback: "+error.GetType().Name);}
        }
        var fallback = await QuerySourceAsync(KugouAsync,s,token);
        if (unreliable && !fallback.Failed && fallback.Result.Lines.Count == 0)
            return new(new(new(),"歌词录音或时间轴需要重新确认",Outcome:LyricsOutcome.Unreliable));
        return fallback;
    }

    private async Task<LyricDocument> KugouAsync(MediaSnapshot s, CancellationToken token)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        var keyword = QuerySourceAsync(KugouKeywordAsync, s, race.Token);
        async Task<LyricDocument> Identify(MediaSnapshot snapshot, CancellationToken cancellation)
        {
            // Avoid a second search for tracks already resolved by the fast lyric query.
            var first = await Task.WhenAny(keyword, Task.Delay(500, cancellation));
            cancellation.ThrowIfCancellationRequested();
            if (first == keyword && (await keyword).Result.Lines.Count > 0) return new("", "酷狗歌词", "kugou");
            return await KugouHashAsync(snapshot, cancellation);
        }
        var pending = new List<Task<SourceAttempt>> { keyword, QuerySourceAsync(Identify, s, race.Token) };
        string plain = ""; bool failed = false, unreliable = false;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending); pending.Remove(done);
            var attempt = await done; token.ThrowIfCancellationRequested();
            if (attempt.Result.Lines.Count > 0) { race.Cancel(); return DocumentOf(attempt.Result); }
            if (plain.Length == 0) plain = attempt.Result.PlainText;
            failed |= attempt.Failed;
            unreliable |= attempt.Result.Outcome == LyricsOutcome.Unreliable;
        }
        if (failed && plain.Length == 0) throw new HttpRequestException("Kugou lookup connection failed");
        if (plain.Length == 0 && unreliable) throw new UnreliableLyricsException();
        return new(plain, "酷狗歌词", "kugou");
    }
    private async Task<LyricDocument> KugouKeywordAsync(MediaSnapshot s, CancellationToken token)
    {
        string plain = ""; bool unreliable = false;
        foreach (var (title, artist) in QueryNames(s))
        {
            try {
                var document = await KugouLyricsAsync(s, title, artist, null, 0, token);
                if (document.Validate(s).Lines.Count > 0) return document;
                if (plain.Length == 0) plain = document.Content;
            } catch (UnreliableLyricsException) { unreliable = true; }
        }
        if (plain.Length == 0 && unreliable) throw new UnreliableLyricsException();
        return new(plain, "酷狗歌词", "kugou");
    }
    private async Task<LyricDocument> KugouHashAsync(MediaSnapshot s, CancellationToken token)
    {
        string plain = ""; bool unreliable = false;
        // Keyword search can return only clips. Resolve the full recording before asking for lyrics by hash.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (title, artist) in QueryNames(s))
        {
            string query = Uri.EscapeDataString((artist + " " + title).Trim());
            using var response = await GetSingleJsonAsync($"https://songsearch.kugou.com/song_search_v2?keyword={query}&page=1&pagesize=10&platform=WebFilter", token);
            if (!response.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("lists", out var songs) || songs.ValueKind != JsonValueKind.Array) continue;
            LogRejections("kugou-song",s,songs.EnumerateArray().Select(row=>(Name(row,"SongName"),Name(row,"SingerName"),Number(row,"Duration")*1000)));
            var matched = songs.EnumerateArray().Where(row => RecordingRules.AlbumsCompatible(s.Album, Name(row,"AlbumName"), s.Artist))
                .Select(row => new SongMatch(Text(row, "FileHash"), Name(row, "SongName"), Name(row, "SingerName"), Number(row, "Duration") * 1000,
                Score(s, Name(row, "SongName"), Name(row, "SingerName"), Number(row, "Duration") * 1000)))
                .Where(row => Regex.IsMatch(row.Hash, "^[A-Fa-f0-9]{32}$") && row.Score >= 65)
                .OrderByDescending(row => row.Score).ThenBy(row => Math.Abs(row.Duration - s.DurationMs)).ToList();
            Diagnostic?.Invoke($"kugou-song: candidates={songs.GetArrayLength()}; matched={matched.Count}");
            foreach (var song in matched)
            {
                if (seen.Count >= 2) break;
                if (!seen.Add(song.Hash)) continue;
                try {
                    var document = await KugouLyricsAsync(s, song.Title, song.Artist, song.Hash, song.Duration, token);
                    if (document.Validate(s).Lines.Count > 0) return document;
                    if (plain.Length == 0) plain = document.Content;
                } catch (UnreliableLyricsException) { unreliable = true; }
            }
            if (seen.Count >= 2) break;
        }
        if (plain.Length == 0 && unreliable) throw new UnreliableLyricsException();
        return new(plain, "酷狗歌词", "kugou");
    }
    private async Task<LyricDocument> KugouLyricsAsync(MediaSnapshot s, string title, string artist, string? hash, double referenceDuration, CancellationToken token, RecordingEvidence? recordingEvidence = null, string route = "")
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task<LyricDocument> Backup()
        {
            await Task.Delay(200, race.Token);
            return await KugouEndpointAsync(s, title, artist, hash, referenceDuration, "krcs.kugou.com", race.Token, recordingEvidence, route);
        }
        // Compete on a fully validated lyric, not merely a nonempty search/download response.
        var pending = new List<Task<LyricDocument>> {
            KugouEndpointAsync(s, title, artist, hash, referenceDuration, "lyrics.kugou.com", race.Token, recordingEvidence, route), Backup()
        };
        string plain = ""; Exception? failure = null; bool unreliable = false;
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending); pending.Remove(done);
                try
                {
                    var document = await done; token.ThrowIfCancellationRequested();
                    if (document.Validate(s).Lines.Count > 0) { race.Cancel(); return document; }
                    if (plain.Length == 0) plain = document.Content;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (UnreliableLyricsException) { unreliable = true; }
                catch (Exception error) { failure = error; }
            }
            token.ThrowIfCancellationRequested();
            if (plain.Length == 0 && failure != null) throw new HttpRequestException("Kugou lyric endpoints failed", failure);
            if (plain.Length == 0 && unreliable) throw new UnreliableLyricsException();
            return new(plain, "酷狗歌词", "kugou");
        }
        finally { race.Cancel(); _ = ObservePendingAsync(pending); }
    }
    private static async Task ObservePendingAsync(IEnumerable<Task<LyricDocument>> pending)
    {
        foreach (var task in pending) { try { await task; } catch (Exception) { } }
    }
    private async Task<LyricDocument> KugouEndpointAsync(MediaSnapshot s, string title, string artist, string? hash, double referenceDuration, string host, CancellationToken token, RecordingEvidence? recordingEvidence = null, string route = "")
    {
        string label = artist.Length > 0 ? artist + " - " + title : title;
        string url = $"https://{host}/search?ver=1&man=yes&client=pc&keyword={Uri.EscapeDataString(label)}&duration={s.DurationMs}&lrctxt=1";
        if (hash != null) url += "&hash=" + Uri.EscapeDataString(hash);
        if (HasKugouId(s)) url += "&album_audio_id=" + Uri.EscapeDataString(s.MediaId);
        using var response = await GetSingleJsonAsync(url, token);
        if (response.RootElement.ValueKind != JsonValueKind.Object || !response.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array) return new("", "酷狗歌词", "kugou");
        LogRejections("kugou-"+(hash==null?"keyword":"hash"), s, candidates.EnumerateArray().Select(c => (NameOr(c,"song",hash==null?"":title),NameOr(c,"singer",hash==null?"":artist),Number(c,"duration")>0?Number(c,"duration"):referenceDuration)));
        var scored = candidates.EnumerateArray().Select(c => new {
            Data = c, Id = Text(c, "id"),
            Score = Score(s, NameOr(c, "song", hash == null ? "" : title), NameOr(c, "singer", hash == null ? "" : artist),
                Number(c, "duration") > 0 ? Number(c, "duration") : referenceDuration),
            Difference = Math.Abs(Number(c, "duration") - s.DurationMs),
            Group = Normalize(NameOr(c,"song",title)) + "\n" + Number(c,"duration").ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Where(c => c.Score >= 65 && c.Id.Length > 0 && Text(c.Data, "accesskey").Length > 0);
        var matched = KugouCandidateOrder.Interleave(scored, c => c.Id, c => c.Score, c => c.Difference, c => c.Group).ToList();
        Diagnostic?.Invoke($"kugou-{(hash == null ? "keyword" : "hash")}: host={host}; candidates={candidates.GetArrayLength()}; matched={matched.Count}");
        string plain = ""; int transportFailures = 0; bool unreliable = false;
        using var downloads = CancellationTokenSource.CreateLinkedTokenSource(token);
        route = route.Length > 0 ? route : hash == null ? "keyword" : "catalog-hash";
        int rejectedCount = 0, rejectionExamples = 0;
        var rejectionSignatures = new HashSet<string>(StringComparer.Ordinal);
        async Task<KugouDownload> Download(JsonElement data)
        {
            string id = Text(data,"id"), access = Uri.EscapeDataString(Text(data,"accesskey"));
            string candidateTitle = NameOr(data,"song",title), candidateArtist = NameOr(data,"singer",artist);
            double rawDuration = Number(data,"duration");
            double candidateDuration = double.IsFinite(rawDuration) ? rawDuration : 0;
            string proofState;
            if (recordingEvidence == null) proofState = route == "native-id" ? "native-catalog-not-verified" : "not-native-id-route";
            else if (!recordingEvidence.Matches(s, out string bindingReason)) proofState = bindingReason;
            else if (!double.IsFinite(rawDuration)) proofState = "candidate-duration-invalid";
            else if (candidateDuration <= 0) proofState = "candidate-duration-missing";
            else if (Math.Abs(candidateDuration-s.DurationMs)>1000) proofState = "candidate-player-duration-conflict";
            else if (Math.Abs(candidateDuration-recordingEvidence.DurationMs)>1000) proofState = "candidate-catalog-duration-conflict";
            else proofState = "verified";
            bool useEvidence = proofState == "verified";
            try
            {
                using var lyrics=await GetSingleJsonAsync($"https://{host}/download?ver=1&client=pc&id={Uri.EscapeDataString(id)}&accesskey={access}&fmt=lrc&charset=utf8",downloads.Token);
                string encoded=Text(lyrics.RootElement,"content");
                return new(encoded.Length>0?Encoding.UTF8.GetString(Convert.FromBase64String(encoded)):"",false,useEvidence,
                    candidateDuration,id,candidateTitle,candidateArtist,proofState);
            }
            catch(OperationCanceledException) when(downloads.IsCancellationRequested){throw;}
            catch(Exception error)
            {
                Diagnostic?.Invoke("kugou-candidate-failed: "+error.GetType().Name);
                return new("",error is HttpRequestException or OperationCanceledException,false,
                    candidateDuration,id,candidateTitle,candidateArtist,proofState);
            }
        }
        var queue=new Queue<(JsonElement Data,double Score)>(matched.Select(candidate=>(candidate.Data,candidate.Score)));
        var pending=new List<Task<KugouDownload>>();
        double activeScore = double.NaN;
        void Fill()
        {
            if (pending.Count == 0 && queue.Count > 0) activeScore = queue.Peek().Score;
            while (pending.Count < 3 && queue.Count > 0 && queue.Peek().Score == activeScore)
                pending.Add(Download(queue.Dequeue().Data));
        }
        Fill();
        try
        {
            while(pending.Count>0)
            {
                var finished=await Task.WhenAny(pending);pending.Remove(finished);
                var result=await finished;token.ThrowIfCancellationRequested();
                if(result.Failed)transportFailures++;
                var proof = result.UseEvidence ? recordingEvidence : RecordingEvidence.Catalog("kugou",s,"lyric:"+result.Id,
                    result.Title,result.Artist,"",SafeDuration(result.Duration),hash ?? "");
                if (proof?.Matches(s) != true) proof = null;
                var document = new LyricDocument(result.Content,"酷狗歌词","kugou",proof);
                if(ParseLyricsCore(result.Content,s,proof,out string rejection).Count>0)
                {
                    string headerTitle = ProvidedLyrics.ReadHeader(result.Content,"ti",out _), headerArtist = ProvidedLyrics.ReadHeader(result.Content,"ar",out _), headerAlbum = ProvidedLyrics.ReadHeader(result.Content,"al",out _);
                    bool strictAlbum = RecordingRules.AlbumsCompatible(s.Album,headerAlbum,s.Artist);
                    bool strictTitle = Score(s,headerTitle.Length>0?headerTitle:s.Title,headerArtist.Length>0?headerArtist:s.Artist,s.DurationMs)>=65;
                    if (result.UseEvidence && (!strictAlbum || !strictTitle))
                        ReportKugouDecision(s,result,recordingEvidence,host,route,"verified-native-release-metadata",true,null);
                    return document;
                }
                if(result.Content.Length>0 && Lrc.Parse(result.Content).Count>0) {
                    unreliable = true; rejectedCount++;
                    if (ReportKugouDecision(s,result,recordingEvidence,host,route,rejection,false,rejectionSignatures)) rejectionExamples++;
                }
                else if(plain.Length==0)plain=result.Content;
                Fill();
            }
        }
        finally
        {
            downloads.Cancel();
            foreach(var remaining in pending)_=ObserveCandidateAsync(remaining);
            if (rejectedCount > rejectionExamples) Diagnostic?.Invoke("lyric-rejections-summary: " + JsonSerializer.Serialize(new {
                title = s.Title, host, route, rejectedCount, distinctExamples = rejectionExamples, repeated = rejectedCount - rejectionExamples
            }));
        }
        if(matched.Count>0 && transportFailures==matched.Count)throw new HttpRequestException("All matched lyric downloads failed");
        if (plain.Length == 0 && unreliable) throw new UnreliableLyricsException();
        return new(plain, "酷狗歌词", "kugou");
    }
    private static async Task ObserveCandidateAsync(Task<KugouDownload> task)
    {
        try { await task; } catch(Exception) { }
    }

    private static string DiagnosticText(string value) => value.Length <= 160 ? value : value[..110] + "…" + value[^50..];

    private bool ReportKugouDecision(MediaSnapshot snapshot, KugouDownload candidate, RecordingEvidence? proof,
        string host, string route, string reason, bool accepted, HashSet<string>? seen)
    {
        string title = ProvidedLyrics.ReadHeader(candidate.Content,"ti",out bool titleClear),
            artist = ProvidedLyrics.ReadHeader(candidate.Content,"ar",out bool artistClear),
            album = ProvidedLyrics.ReadHeader(candidate.Content,"al",out bool albumClear);
        string signature = JsonSerializer.Serialize(new { reason, title = DiagnosticText(title), artist = DiagnosticText(artist), album = DiagnosticText(album), titleLength = title.Length, artistLength = artist.Length, albumLength = album.Length, titleClear, artistClear, albumClear, candidate.UseEvidence, candidate.EvidenceState });
        if (seen != null && !seen.Add(signature)) return false;
        var commonProof = candidate.UseEvidence ? proof : RecordingEvidence.Catalog("kugou",snapshot,"lyric:"+candidate.Id,
            candidate.Title,candidate.Artist,"",SafeDuration(candidate.Duration));
        CandidateDiagnostic.Report(snapshot,new(candidate.Content,"酷狗歌词","kugou",commonProof),reason,accepted,Diagnostic);
        string detail = JsonSerializer.Serialize(new {
            reason, host, route,
            requested = new { title = DiagnosticText(snapshot.Title), artist = DiagnosticText(snapshot.Artist), album = DiagnosticText(snapshot.Album), durationMs = snapshot.DurationMs, mediaId = DiagnosticText(snapshot.MediaId) },
            candidate = new { id = DiagnosticText(candidate.Id), title = DiagnosticText(candidate.Title), artist = DiagnosticText(candidate.Artist), durationMs = candidate.Duration,
                playerDeltaMs = candidate.Duration > 0 ? (double?)Math.Abs(candidate.Duration-snapshot.DurationMs) : null,
                catalogDeltaMs = candidate.Duration > 0 && proof != null ? (double?)Math.Abs(candidate.Duration-proof.DurationMs) : null },
            headers = new { title = DiagnosticText(title), artist = DiagnosticText(artist), album = DiagnosticText(album), titleClear, artistClear, albumClear },
            evidence = new { enabled = candidate.UseEvidence, state = candidate.EvidenceState, catalogDurationMs = proof?.DurationMs }
        });
        Diagnostic?.Invoke(accepted ? "native-release-compatible: " + detail : "lyric-body-rejected: source=kugou; reason=" + reason + "; detail=" + detail);
        return true;
    }
    private async Task<LyricDocument> LrclibAsync(MediaSnapshot s, CancellationToken token)
    {
        string plain = ""; bool unreliable = false;
        var queries = QueryNames(s).ToList();
        if (s.DurationMs > 0 && !string.IsNullOrWhiteSpace(s.Artist))
        {
            var broad = queries.Select(pair => (pair.Title, Artist: "")).Distinct().Take(2).ToArray();
            queries.AddRange(broad);
        }
        foreach (var (title, artist) in queries.Distinct())
        {
            string url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}";
            if (artist.Length > 0) url += "&artist_name=" + Uri.EscapeDataString(artist);
            using var response = await GetSingleJsonAsync(url, token);
            if (response.RootElement.ValueKind != JsonValueKind.Array) continue;
            LogRejections("lrclib", s, response.RootElement.EnumerateArray().Select(c => (Name(c,"trackName"),Name(c,"artistName"),Number(c,"duration")*1000)));
            var results = response.RootElement.EnumerateArray().Select(c => new { Data = c,
                Score = Score(s, Name(c, "trackName"), Name(c, "artistName"), Number(c, "duration") * 1000),
                AlbumMatch = s.Album.Length > 0 && Normalize(s.Album) == Normalize(Name(c,"albumName")) })
                .Where(c => c.Score >= 65 && RecordingRules.AlbumsCompatible(s.Album, Name(c.Data,"albumName"), s.Artist))
                .OrderByDescending(c => c.Score).ThenByDescending(c => c.AlbumMatch).ToList();
            Diagnostic?.Invoke($"lrclib: candidates={response.RootElement.GetArrayLength()}; matched={results.Count}; query={(artist.Length > 0 ? "title-artist" : "title-only")}");
            foreach (var item in results)
            {
                string synced = Text(item.Data, "syncedLyrics");
                var proof = RecordingEvidence.Catalog("lrclib",s,"id:"+Text(item.Data,"id"),Name(item.Data,"trackName"),
                    Name(item.Data,"artistName"),Name(item.Data,"albumName"),SafeDuration(Number(item.Data,"duration")*1000));
                var document = new LyricDocument(synced,"LRCLIB","lrclib",proof.Matches(s) ? proof : null);
                if (document.Validate(s,Diagnostic).Lines.Count > 0) return document;
                if (Lrc.Parse(synced).Count > 0) {
                    unreliable = true; Diagnostic?.Invoke("lyric-body-rejected: source=lrclib; recording headers or timeline conflict");
                }
            }
            if (plain.Length == 0 && results.Count > 0) plain = Text(results[0].Data, "plainLyrics");
            if (!unreliable && plain.Length == 0 && results.Any(item => item.Score >= 100 && item.Data.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True))
                return new("", "纯音乐", "lrclib");
        }
        if (plain.Length == 0 && unreliable) throw new UnreliableLyricsException();
        return new(plain, "LRCLIB", "lrclib");
    }

    private void LogRejections(string source, MediaSnapshot s, IEnumerable<(string Title,string Artist,double Duration)> candidates)
    {
        var reasons = candidates.Select(c => RejectionReason(s,c.Title,c.Artist,c.Duration)).ToArray();
        Diagnostic?.Invoke($"candidate-rejections: source={source}; artist={reasons.Count(r=>r=="artist")}; duration={reasons.Count(r=>r=="duration")}; title-version={reasons.Count(r=>r=="title-version")}");
    }

    private static string RejectionReason(MediaSnapshot s,string title,string artist,double durationMs)
    {
        bool artistMatches = Normalize(s.Artist).Length > 0 && Normalize(artist).Length > 0 && RecordingRules.ArtistsMatch(s.Artist,artist);
        if (Normalize(s.Artist).Length > 0 && !artistMatches) return "artist";
        if (s.DurationMs > 0 && durationMs > 0 && Math.Abs(s.DurationMs-durationMs)>3000) return "duration";
        return RecordingRules.TitlesMatch(s,title,artistMatches,durationMs) ? "" : "title-version";
    }
    internal static string Normalize(string value) => Regex.Replace(ChineseText.Simplify(value).ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    internal static double Score(MediaSnapshot s, string title, string artist, double durationMs)
    {
        string x = Normalize(s.Artist), y = Normalize(artist);
        bool artistMatches = x.Length > 0 && y.Length > 0 && RecordingRules.ArtistsMatch(s.Artist, artist);
        if (x.Length > 0 && !artistMatches) return 0;
        if (s.DurationMs > 0 && durationMs > 0 && Math.Abs(s.DurationMs - durationMs) > 3000) return 0;
        if (!RecordingRules.TitlesMatch(s, title, artistMatches, durationMs)) return 0;
        double score = 65;
        if (artistMatches) score += 25;
        if (s.DurationMs > 0 && durationMs > 0) { double delta = Math.Abs(s.DurationMs - durationMs); score += delta <= 3000 ? 15 : delta <= 10000 ? 3 : -45; }
        return score;
    }
    internal static List<LyricLine> ParseLyrics(string content, MediaSnapshot snapshot) => ParseLyricsWithEvidence(content,snapshot,null);
    internal static List<LyricLine> ParseLyricsWithEvidence(string content, MediaSnapshot snapshot, RecordingEvidence? evidence) =>
        ParseLyricsCore(content,snapshot,evidence,out _);
    internal static List<LyricLine> ParseLyricsChecked(string content, MediaSnapshot snapshot, RecordingEvidence? evidence, out string reason) =>
        ParseLyricsCore(content, snapshot, evidence, out reason);
    private static List<LyricLine> ParseLyricsCore(string content, MediaSnapshot snapshot, RecordingEvidence? evidence, out string reason)
    {
        if (evidence != null && !evidence.Matches(snapshot, out string bindingReason))
        { reason = "recording-evidence-" + bindingReason; return new(); }
        string title = ProvidedLyrics.ReadHeader(content, "ti", out bool titleClear),
            artist = ProvidedLyrics.ReadHeader(content, "ar", out bool artistClear),
            album = ProvidedLyrics.ReadHeader(content, "al", out bool albumClear);
        if (!titleClear || !artistClear || !albumClear) { reason="conflicting-headers"; return new(); }
        bool nativeHeaders = evidence?.AllowsLyricAlbum(snapshot,title,artist,album) ?? false;
        if (album.Length > 0 && snapshot.Album.Length > 0 && !RecordingRules.AlbumsCompatible(snapshot.Album,album,snapshot.Artist)
            && !nativeHeaders)
        { reason="album-conflict"; return new(); }
        if ((title.Length > 0 || artist.Length > 0) && Score(snapshot,title.Length > 0 ? title : snapshot.Title,
            artist.Length > 0 ? artist : snapshot.Artist,snapshot.DurationMs) < 65 && !nativeHeaders)
        { reason="title-or-artist-conflict"; return new(); }
        return LyricQuality.TryValidate(content,snapshot,out var lines,out reason) ? lines : new();
    }
    private static long SafeDuration(double value) => double.IsFinite(value) && value > 0 && value <= 604800000 ? (long)value : 0;
    private static string Name(JsonElement value, string key) => System.Net.WebUtility.HtmlDecode(Regex.Replace(Text(value, key), "<[^>]+>", "")).Trim();
    private static string NameOr(JsonElement value, string key, string fallback) { string name = Name(value, key); return name.Length > 0 ? name : fallback; }
    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : "";
    private static double Number(JsonElement value, string key) => double.TryParse(Text(value, key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) ? n : 0;
}
