namespace LyricBridge;

internal static class LookupRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var song = new MediaSnapshot {Version=1,Type="media",Title="测试",Artist="本地",PackageName="lookup-regression",DurationMs=269000,PositionKnown=true};
        static LyricsResult Good(string text) => new(Lrc.Parse("[00:00]" + text), text);
        var calls = new List<(long Duration, bool Bypass, bool Online)>();
        using (var lookup = new LyricLookup((snapshot, _, bypass, online) => {
            calls.Add((snapshot.DurationMs,bypass,online));
            return Task.FromResult(Good(bypass ? "新歌词" : "已缓存歌词"));
        }))
        {
            lookup.Observe(song); await lookup.PollAsync(true); await lookup.RefreshAsync();
            checks["Manual refresh explicitly bypasses the lyric cache"] = calls.Count==2 && !calls[0].Bypass && calls[1].Bypass && calls[1].Online && lookup.Result.Source=="新歌词";
        }
        calls.Clear();
        using (var lookup = new LyricLookup((snapshot, _, bypass, online) => {
            calls.Add((snapshot.DurationMs,bypass,online)); return Task.FromResult(Good("离线缓存"));
        }))
        {
            lookup.Observe(song); await lookup.PollAsync(false); await lookup.PollAsync(false);
            checks["Disabled automatic network matching still loads local lyrics once"] = calls.Count==1 && !calls[0].Online && lookup.Result.Lines.Count==1;
        }
        calls.Clear();
        using (var lookup = new LyricLookup((snapshot, _, bypass, online) => {
            calls.Add((snapshot.DurationMs,bypass,online));
            return Task.FromResult(online ? Good("联网恢复") : new LyricsResult(new(),"无缓存"));
        }))
        {
            lookup.Observe(song); for(int i=0;i<10;i++) await lookup.PollAsync(false);
            bool once=calls.Count==1 && !calls[0].Online;
            await lookup.PollAsync(true);
            checks["Offline cache miss does not poll disk repeatedly and can go online immediately"] = once && calls.Count==2 && calls[1].Online && lookup.Result.Source=="联网恢复";
        }
        calls.Clear();
        using (var lookup = new LyricLookup((snapshot, _, bypass, online) => {
            calls.Add((snapshot.DurationMs,bypass,online));
            return Task.FromResult(snapshot.DurationMs==0 ? Good("时长未知时的歌词") : Good("已验证完整版"));
        }))
        {
            lookup.Observe(song with {DurationMs=0}); await lookup.PollAsync(true);
            lookup.Observe(song); await lookup.PollAsync(true);
            checks["First duration completion revalidates lyrics using the known duration"] = calls.Count==2 && calls[1].Duration==269000 && calls[1].Bypass && lookup.Result.Source=="已验证完整版";
        }
        var pending=new TaskCompletionSource<LyricsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int pendingRequests=0;
        using (var lookup = new LyricLookup((snapshot, _, _, _) => {
            pendingRequests++; return snapshot.DurationMs==0 ? pending.Task : Task.FromResult(Good("已知时长的新结果"));
        }))
        {
            lookup.Observe(song with {DurationMs=0}); var old=lookup.PollAsync(true);
            lookup.Observe(song); await lookup.PollAsync(true);
            pending.SetResult(Good("过期短版")); await old;
            checks["A pending unknown-duration result cannot overwrite its revalidated recording"] = pendingRequests==2 && lookup.Result.Source=="已知时长的新结果";
        }
        calls.Clear();
        using (var lookup = new LyricLookup((snapshot, _, bypass, online) => {
            calls.Add((snapshot.DurationMs,bypass,online)); return Task.FromResult(Good("完整歌词"));
        }))
        {
            lookup.Observe(song); await lookup.PollAsync(true);
            bool changed=lookup.Observe(song with {DurationMs=0}); await lookup.PollAsync(true);
            bool preserved=!changed && calls.Count==1 && lookup.Active?.DurationMs==269000;
            await lookup.RefreshAsync();
            checks["Temporary missing duration preserves lyrics and does not weaken manual validation"] = preserved && calls.Count==2 && calls[1].Duration==269000;
        }
        calls.Clear();
        using (var lookup = new LyricLookup((snapshot, _, bypass, online) => {
            calls.Add((snapshot.DurationMs,bypass,online)); return Task.FromResult(Good(snapshot.DurationMs==0?"未知版本":"确定版本"));
        }))
        {
            lookup.Observe(song with {DurationMs=0}); await lookup.PollAsync(true);
            lookup.Observe(song with {Title="另一首"}); await lookup.PollAsync(true);
            lookup.Observe(song); await lookup.PollAsync(true);
            checks["Unknown-duration memory entry is not trusted for a later known recording"] = calls.Count==3 && calls[2].Bypass && lookup.Result.Source=="确定版本";
        }
        double now=0; int retryRequests=0;
        using (var lookup = new LyricLookup((snapshot, _, _, _) => {
            retryRequests++;
            return Task.FromResult(retryRequests==1?Good("原显示"):retryRequests==2?new LyricsResult(new(),"临时故障"):Good("校验恢复"));
        },()=>now))
        {
            lookup.Observe(song with {DurationMs=0}); await lookup.PollAsync(true);
            lookup.Observe(song); await lookup.PollAsync(true);
            bool preserved=lookup.Result.Source=="原显示" && lookup.WillRetry;
            await lookup.PollAsync(true); bool backoff=retryRequests==2;
            now=4; await lookup.PollAsync(true);
            checks["Duration revalidation retries after failure while preserving visible lyrics"] = preserved && backoff && retryRequests==3 && lookup.Result.Source=="校验恢复";
        }
        int manualRequests=0;
        using (var lookup = new LyricLookup((_, _, bypass, _) => {
            manualRequests++;return Task.FromResult(bypass?new LyricsResult(new(),"重匹配失败"):Good("仍可用"));
        }))
        {
            lookup.Observe(song); await lookup.PollAsync(true); await lookup.RefreshAsync(); await lookup.PollAsync(true);
            checks["Failed explicit rematch preserves available lyrics without an endless retry loop"] = manualRequests==2 && lookup.Result.Source=="仍可用";
        }
        calls.Clear();
        using (var lookup = new LyricLookup((snapshot, _, bypass, online) => {
            calls.Add((snapshot.DurationMs,bypass,online));
            return Task.FromResult(online?Good(snapshot.DurationMs==0?"未知时长歌词":"联网验证歌词"):new LyricsResult(new(),"本地无缓存"));
        }))
        {
            lookup.Observe(song with {DurationMs=0}); await lookup.PollAsync(true);
            lookup.Observe(song); await lookup.PollAsync(false); await lookup.PollAsync(false);
            bool offline=calls.Count==2 && !calls[1].Online && lookup.Result.Lines.Count==1;
            await lookup.PollAsync(true);
            checks["Known-duration validation honors disabled network and resumes after enabling it"] = offline && calls.Count==3 && calls[2].Online && calls[2].Bypass && lookup.Result.Source=="联网验证歌词";
        }

        int identityCalls=0;
        using(var lookup=new LyricLookup((snapshot,_)=>Task.FromResult(Good("recording-"+snapshot.MediaId+"-"+ ++identityCalls))))
        {
            var first=song with {MediaId="id-a"};
            lookup.Observe(first);await lookup.PollAsync(true);
            lookup.Observe(song);await lookup.PollAsync(true);
            checks["Temporary missing identifier retains current recording"] = identityCalls==1 && lookup.Active?.MediaId=="id-a";
            lookup.Observe(song with {MediaId="id-b"});await lookup.PollAsync(true);
            checks["Identifier change revalidates identical title artist and duration"] = identityCalls==2 && lookup.Result.Source.Contains("id-b");
        }

        return checks;
    }
}
