using System.Diagnostics;
using System.Text.Json;
namespace LyricBridge;

public sealed class LyricLookup : IDisposable
{
    private readonly Func<MediaSnapshot, CancellationToken, bool, bool, Task<LyricsResult>> fetch;
    private readonly Func<double> now;
    private readonly Dictionary<string, (long Duration, LyricsResult Result)> memory = new();
    private readonly HashSet<string> reviewedSources = new(StringComparer.Ordinal);
    private bool sourceReviewPending, networkInFlight, automaticMatchingEnabled = true;
    private double sourceReviewAt = double.PositiveInfinity;
    private CancellationTokenSource? cancellation;
    private int generation, failures;
    private bool disposed, cacheChecked, needsValidation;
    private double nextAttempt;
    private long recordingDuration;
    private string identity = "";
    private string lastProvidedText = "";
    public MediaSnapshot? Active { get; private set; }
    public LyricsResult Result { get; private set; } = new(new(), "尚未加载歌词");
    public bool Busy { get; private set; }
    public int Attempts { get; private set; }
    private bool Resolved => Result.Lines.Count > 0 || Result.IsInstrumental;
    public bool WillRetry => Active != null && (sourceReviewPending
        || (!Resolved || needsValidation) && !double.IsPositiveInfinity(nextAttempt));
    public string Status => Result.IsInstrumental ? "纯音乐" : Busy ? Result.Lines.Count > 0 ? "正在重新确认歌词…" : "正在匹配歌词…"
        : needsValidation && Result.Lines.Count > 0 ? Result.Source + " · 将重试确认版本"
        : Result.Lines.Count > 0 ? Result.Source : Attempts > 0 ? Result.Source + " · 将自动重试" : Result.Source;
    public event Action? Changed;
    public event Action<string>? Diagnostic;

    // Retain the simple fetch contract for callers that have no disk/network distinction.
    public LyricLookup(Func<MediaSnapshot, CancellationToken, Task<LyricsResult>> fetch, Func<double>? now = null)
        : this((snapshot, token, _, online) => online ? fetch(snapshot, token)
            : Task.FromResult(new LyricsResult(new(), "本地暂无缓存歌词（已关闭联网匹配）")), now) { }
    public LyricLookup(Func<MediaSnapshot, CancellationToken, bool, bool, Task<LyricsResult>> fetch, Func<double>? now = null)
    { this.fetch = fetch; this.now = now ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency); }
    private static string SongIdentity(MediaSnapshot s) => s.PackageName + "\n" + LyricsProvider.Normalize(s.Title) + "\n" + LyricsProvider.Normalize(s.Artist);
    private static string AlbumIdentity(MediaSnapshot s) => LyricsProvider.Normalize(s.Album);
    private static string BaseIdentity(MediaSnapshot s) => SongIdentity(s) + "\nalbum:" + AlbumIdentity(s);
    private static string Identity(MediaSnapshot s) => BaseIdentity(s) + "\n" + s.MediaId
        + (s.Source.Length > 0 ? "\nsource:" + s.Source + "\nsession:" + s.SessionId : "")
        + (s.Source == "integration" ? "\ntrack:" + s.TrackId : "");
    private static bool Compatible(long a, long b) => a == 0 || b == 0 || Math.Abs(a - b) <= 3000;
    private static bool SameObservationScope(MediaSnapshot left, MediaSnapshot right) =>
        left.PackageName == right.PackageName && left.Source == right.Source && left.SessionId == right.SessionId
        && (left.Source != "integration" || left.TrackId == right.TrackId);
    private void CancelPending()
    {
        generation++; cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; Busy = false;
        sourceReviewPending = networkInFlight = false; sourceReviewAt = double.PositiveInfinity;
    }
    private void ScheduleSourceReview(MediaSnapshot snapshot)
    {
        sourceReviewPending = !needsValidation && Result.Lines.Count > 0 && Result.NeedsSourceReview
            && !Result.FromProvidedLyrics && Result.Source != "播放器提供的歌词"
            && !reviewedSources.Contains(snapshot.TrackKey);
        sourceReviewAt = sourceReviewPending ? now() + 5 : double.PositiveInfinity;
    }
    private void MarkSourceReviewed(MediaSnapshot snapshot)
    {
        // TrackKey excludes the Bluetooth session, so reconnecting or switching back
        // cannot repeatedly turn the same provisional cache into background traffic.
        if (reviewedSources.Count >= 64 && !reviewedSources.Contains(snapshot.TrackKey))
            reviewedSources.Remove(reviewedSources.First());
        reviewedSources.Add(snapshot.TrackKey);
        sourceReviewPending = false; sourceReviewAt = double.PositiveInfinity;
        if (Result.NeedsSourceReview) Result = Result with { NeedsSourceReview = false };
    }

    public bool Observe(MediaSnapshot snapshot)
    {
        if (snapshot.Type != "media" || string.IsNullOrWhiteSpace(snapshot.Title)) return false;
        var previous = Active;
        bool sameSong = previous != null && SameObservationScope(previous, snapshot) && SongIdentity(previous) == SongIdentity(snapshot)
            && Compatible(previous.DurationMs, snapshot.DurationMs);
        // Only incomplete frames inside the same observation scope can retain fields.
        // A changed source/session/integration track must not inherit an old recording.
        if (sameSong && previous != null && AlbumIdentity(snapshot).Length == 0 && AlbumIdentity(previous).Length > 0
            && (snapshot.MediaId.Length == 0 || snapshot.MediaId == previous.MediaId))
            snapshot = snapshot with { Album = previous.Album };
        if (sameSong && previous != null && snapshot.MediaId.Length == 0 && previous.MediaId.Length > 0
            && AlbumIdentity(previous) == AlbumIdentity(snapshot))
            snapshot = snapshot with { MediaId = previous.MediaId };
        bool albumCompleted = sameSong && previous != null && AlbumIdentity(previous).Length == 0
            && AlbumIdentity(snapshot).Length > 0 && previous.MediaId == snapshot.MediaId;
        if (previous != null && snapshot.Source == "integration" && previous.Source == snapshot.Source
            && snapshot.PackageName == previous.PackageName && snapshot.SessionId.Length > 0 && snapshot.SessionId == previous.SessionId
            && snapshot.TrackId.Length > 0 && snapshot.TrackId == previous.TrackId
            && snapshot.ProvidedLyrics.Length == 0 && snapshot.ProvidedLyricsTrackId == snapshot.TrackId
            && SongIdentity(previous) == SongIdentity(snapshot) && AlbumIdentity(previous) == AlbumIdentity(snapshot)
            && Compatible(previous.DurationMs,snapshot.DurationMs))
            snapshot = snapshot with { ProvidedLyrics = previous.ProvidedLyrics };
        bool suppliedUpdated = previous != null && snapshot.Source == "integration" && SameObservationScope(previous, snapshot)
            && (snapshot.ProvidedLyrics != previous.ProvidedLyrics || snapshot.ProvidedLyricsTrackId != previous.ProvidedLyricsTrackId);
        var previousResult = Result;
        string key = Identity(snapshot);
        bool changed = identity != key || !Compatible(recordingDuration, snapshot.DurationMs);
        if (changed)
        {
            CancelPending();
            identity = key; recordingDuration = snapshot.DurationMs; lastProvidedText = "";
            Attempts = failures = 0; cacheChecked = false; nextAttempt = now(); needsValidation = false;
            if (memory.TryGetValue(key, out var cached) && Compatible(cached.Duration, snapshot.DurationMs)
                && (!(cached.Result.FromProvidedLyrics || cached.Result.Source == "播放器提供的歌词")
                    || ProvidedLyrics.CanReuseCached(snapshot)))
            {
                Result = cached.Result;
                needsValidation = cached.Duration == 0 && snapshot.DurationMs > 0;
                if (needsValidation) memory.Remove(key);
                else nextAttempt = double.PositiveInfinity;
            }
            else
            {
                memory.Remove(key);
                Result = new(new(), "尚未加载歌词");
            }
            if (albumCompleted)
            {
                if (Result.Lines.Count == 0 && !Result.IsInstrumental
                    && (previousResult.Lines.Count > 0 || previousResult.IsInstrumental)) Result = previousResult;
                // Unscoped lyrics can stay visible, but the completed album must be
                // verified online rather than silently becoming a permanent cache hit.
                needsValidation = true; memory.Remove(key); nextAttempt = now();
                Diagnostic?.Invoke("album-completed: " + snapshot.Title + "; revalidating=true");
            }
            Diagnostic?.Invoke("track-changed: " + snapshot.Title + "; cached=" + Result.Lines.Count);
        }
        else if (recordingDuration == 0 && snapshot.DurationMs > 0)
        {
            // A first unknown duration cannot validate which recording was selected.
            // Keep the displayed lines while replacing/cancelling an unverified request.
            CancelPending(); memory.Remove(key); needsValidation = true;
            cacheChecked = false; failures = 0; nextAttempt = now();
            Diagnostic?.Invoke("duration-completed: " + snapshot.Title + "; revalidating=true");
        }
        if (snapshot.DurationMs > 0) recordingDuration = snapshot.DurationMs;
        // A transient missing duration must not weaken a later manual lookup.
        Active = snapshot.DurationMs == 0 && recordingDuration > 0
            ? snapshot with { DurationMs = recordingDuration } : snapshot;
        if (Active.DurationMs > 0 && Result.Lines.Any(line => line.TimeMs > Active.DurationMs))
        {
            CancelPending(); memory.Remove(identity); lastProvidedText = "";
            Result = new(new(), "歌词时间轴超出当前歌曲长度", Outcome: LyricsOutcome.Unreliable);
            needsValidation = true; cacheChecked = false; failures = 0; nextAttempt = now();
            Diagnostic?.Invoke("active-timeline-rejected: completed recording duration");
            Changed?.Invoke();
        }
        if (ProvidedLyrics.TryValidate(Active, out var supplied, out var rejection))
        {
            if (changed || lastProvidedText != Active.ProvidedLyrics || Result.Source != supplied.Source)
            {
                CancelPending(); Result = supplied with { PlainText = "" }; lastProvidedText = Active.ProvidedLyrics;
                needsValidation = false; failures = 0; cacheChecked = true; nextAttempt = double.PositiveInfinity;
                if (memory.Count >= 64 && !memory.ContainsKey(identity)) memory.Remove(memory.Keys.First());
                memory[identity] = (Active.DurationMs,Result);
                Diagnostic?.Invoke("provided-lyrics-ready: " + Active.Title);
                Changed?.Invoke();
            }
        }
        else if (suppliedUpdated || Result.FromProvidedLyrics && Active.Source == "integration" && Active.ProvidedLyrics.Length > 0)
        {
            CancelPending(); memory.Remove(identity); lastProvidedText = "";
            Result = new(new(), "播放器自带歌词需要重新确认", Outcome: LyricsOutcome.Unreliable); needsValidation = true; nextAttempt = now();
            Diagnostic?.Invoke("provided-lyrics-rejected: " + rejection); Changed?.Invoke();
        }
        else if (Active.ProvidedLyrics.Length > 0 && changed)
            Diagnostic?.Invoke("provided-lyrics-rejected: " + rejection);
        if (changed && !needsValidation) ScheduleSourceReview(Active);
        return changed;
    }

    public void EndPlayback()
    {
        // Render may report the same disconnection every tick. Notify only once.
        if (Active == null && !Busy) return;
        bool supplied = Active?.Source == "integration";
        CancelPending();
        if (supplied) memory.Remove(identity);
        Active = null; identity = ""; recordingDuration = 0; Attempts = failures = 0;
        lastProvidedText = ""; Result = new(new(), "等待播放器提供歌曲信息");
        needsValidation = false; cacheChecked = false; nextAttempt = now(); Changed?.Invoke();
    }
    public void EndProvidedPlayback() => EndPlayback();

    public void SetOnline(bool enabled)
    {
        if (disposed || automaticMatchingEnabled == enabled) return;
        automaticMatchingEnabled = enabled;
        bool cancelNetwork = !enabled && Busy && networkInFlight;
        if (cancelNetwork)
        {
            CancelPending();
            if (!Resolved || needsValidation) { cacheChecked = false; nextAttempt = now(); }
        }
        if (enabled && (!Resolved || needsValidation)) nextAttempt = now();
        if (cancelNetwork) Changed?.Invoke();
    }

    public Task PollAsync(bool enabled)
    {
        if (disposed) return Task.CompletedTask;
        // Only a preference transition cancels a network request. An explicit
        // rematch started while automatic matching is off can still complete.
        SetOnline(enabled);
        if (Active == null) return Task.CompletedTask;
        if (Busy) return Task.CompletedTask;
        if (!enabled)
            return !cacheChecked && (!Resolved || needsValidation)
                ? RefreshCoreAsync(false, false) : Task.CompletedTask;
        if ((!Resolved || needsValidation) && now() >= nextAttempt)
            return RefreshCoreAsync(needsValidation, true);
        return sourceReviewPending && now() >= sourceReviewAt
            ? RefreshCoreAsync(true, true, true) : Task.CompletedTask;
    }
    // The explicit user action retries the source instead of re-reading the same cached file.
    public Task RefreshAsync() => RefreshCoreAsync(true, true);
    private async Task RefreshCoreAsync(bool bypassCache, bool allowNetwork, bool sourceReview = false)
    {
        if (disposed || Active == null) return;
        CancelPending(); cancellation = new();
        var token = cancellation.Token; int request = generation; var snapshot = Active;
        Busy = true; cacheChecked = true; networkInFlight = allowNetwork;
        // Any explicit online rematch already performs the native check. Count it as
        // this recording's one review, without limiting normal failure/metadata retries.
        if (bypassCache && allowNetwork) MarkSourceReviewed(snapshot);
        if (sourceReview) Diagnostic?.Invoke("source-review-start: " + snapshot.Title);
        if (allowNetwork) Attempts++;
        Diagnostic?.Invoke($"{(allowNetwork ? "lookup-start" : "lookup-cache-only")}: {snapshot.Title}; attempt={Attempts}");
        Diagnostic?.Invoke("lookup-context: " + JsonSerializer.Serialize(new { player = snapshot.PackageName, title = snapshot.Title, artist = snapshot.Artist, album = snapshot.Album, mediaId = snapshot.MediaId, source = snapshot.Source, capability = snapshot.EffectiveCapability, sessionId = snapshot.SessionId, trackId = snapshot.TrackId, durationMs = snapshot.DurationMs, attempt = Attempts, online = allowNetwork }));
        Changed?.Invoke();
        try
        {
            var result = await fetch(snapshot, token, bypassCache, allowNetwork);
            if (token.IsCancellationRequested || request != generation || disposed) return;
            if (result.Lines.Count > 0 || result.IsInstrumental)
            {
                if (reviewedSources.Contains(snapshot.TrackKey)) result = result with { NeedsSourceReview = false };
                Result = result; failures = 0; needsValidation = false; nextAttempt = double.PositiveInfinity;
                ScheduleSourceReview(snapshot);
                if (memory.Count >= 64 && !memory.ContainsKey(identity)) memory.Remove(memory.Keys.First());
                memory[identity] = (snapshot.DurationMs, result);
                Diagnostic?.Invoke($"lookup-success: {snapshot.Title}; lines={result.Lines.Count}; source={result.Source}");
            }
            else if (allowNetwork) Failure(result.Source, result);
            else
            {
                if (Result.Lines.Count == 0) Result = result;
                // Going online later may try immediately; offline rendering does not repeat disk reads.
                nextAttempt = now();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (request == generation && !disposed)
            {
                if (allowNetwork) Failure(error.Message);
                else { if (Result.Lines.Count == 0) Result = new(new(), "本地缓存暂时无法读取", Outcome: LyricsOutcome.ReadFailure); nextAttempt = now(); }
            }
        }
        finally { if (request == generation && !disposed) { Busy = false; networkInFlight = false; Changed?.Invoke(); } }
    }
    private void Failure(string reason, LyricsResult? empty = null)
    {
        if (!Resolved) Result = empty ?? new(new(), "歌词读取暂时失败", Outcome: LyricsOutcome.ReadFailure);
        if (!Resolved || needsValidation)
        {
            double[] delays = { 3, 8, 20, 45, 60 };
            nextAttempt = now() + delays[Math.Min(failures++, delays.Length - 1)];
        }
        Diagnostic?.Invoke($"lookup-failed: {Active?.Title}; preserved={Result.Lines.Count}; reason={reason}");
    }
    public void Dispose() { disposed = true; CancelPending(); }
}
