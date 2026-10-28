namespace LyricBridge;

internal static class LookupLifecycleRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var song = new MediaSnapshot { Version=1, Type="media", Title="同名测试录音", Artist="测试歌手", Album="原专辑", MediaId="original-id",
            PackageName="audit.lifecycle.player", DurationMs=180000, PositionKnown=true, Source="media-session", SessionId="same-session" };
        static LyricsResult Good(string marker) => new(new(){new(1000,marker+"首句"),new(120000,marker+"末句")},marker);
        static void End(LyricLookup lookup) => lookup.EndProvidedPlayback();
        static async Task SetOnline(LyricLookup lookup, bool enabled)
        {
            var method = typeof(LyricLookup).GetMethod("SetOnline", new[]{typeof(bool)});
            if (method != null) method.Invoke(lookup,new object[]{enabled});
            else await lookup.PollAsync(enabled);
        }

        var pending = new TaskCompletionSource<LyricsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pendingToken=default;
        using(var lookup=new LyricLookup((_,token,_,_)=>{pendingToken=token;return pending.Task;}))
        {
            lookup.Observe(song);var old=lookup.PollAsync(true);End(lookup);
            checks["Idle ends an ordinary pending playback and cancels its request"] = lookup.Active==null && !lookup.Busy && pendingToken.IsCancellationRequested;
            pending.SetResult(Good("空闲后晚到"));await old;
            checks["A late ordinary result cannot complete after playback ends"] = lookup.Active==null && lookup.Result.Lines.Count==0;
            lookup.Observe(song with{MediaId="",Album=""});
            checks["A same-session same-title recording cannot inherit old identifier or album across idle"] =
                lookup.Active?.MediaId=="" && lookup.Active?.Album=="";
        }

        int calls=0,changed=0;
        using(var lookup=new LyricLookup((_,_,_,_)=>{calls++;return Task.FromResult(Good("可信缓存"));}))
        {
            lookup.Observe(song);await lookup.PollAsync(true);lookup.Changed+=()=>changed++;
            End(lookup);int afterEnd=changed;End(lookup);End(lookup);
            checks["Repeated end notifications are idempotent without a render notification loop"] = lookup.Active==null && afterEnd==1 && changed==afterEnd;
            lookup.Observe(song);await lookup.PollAsync(true);
            checks["Verified ordinary memory cache survives playback end for a fast return"] = calls==1 && lookup.Result.Source=="可信缓存";
        }

        var supplied=song with {Source="integration",SessionId="integrated-session",TrackId="track-one",ProvidedLyricsTrackId="track-one",
            ProvidedLyrics="[00:01]播放器首句\n[02:00]播放器末句"};
        using(var lookup=new LyricLookup((_,_,_,_)=>Task.FromResult(new LyricsResult(new(),"无缓存"))))
        {
            lookup.Observe(supplied);bool initiallySupplied=lookup.Result.FromProvidedLyrics;
            End(lookup);lookup.Observe(supplied with{ProvidedLyrics="",ProvidedLyricsTrackId=""});await lookup.PollAsync(false);
            checks["Ending an integration cannot revive withdrawn supplied lyrics from memory"] = initiallySupplied && lookup.Result.Lines.Count==0;
        }

        pending=new(TaskCreationOptions.RunContinuationsAsynchronously);pendingToken=default;calls=0;
        using(var lookup=new LyricLookup((_,token,_,online)=>{
            calls++;if(!online)return Task.FromResult(new LyricsResult(new(),"本地无缓存"));pendingToken=token;return pending.Task;
        }))
        {
            lookup.Observe(song);var old=lookup.PollAsync(true);await SetOnline(lookup,false);
            checks["Turning automatic matching off cancels an already running ordinary network query"] = pendingToken.IsCancellationRequested && !lookup.Busy;
            pending.SetResult(Good("关闭后晚到"));await old;
            checks["The ordinary network result arriving after disable is ignored"] = lookup.Result.Lines.Count==0;
            for(int i=0;i<5;i++)await lookup.PollAsync(false);
            checks["After cancellation the offline cache is checked once without repeating network calls"] = calls==2 && lookup.Result.Lines.Count==0;
        }

        calls=0;pending=new(TaskCreationOptions.RunContinuationsAsynchronously);pendingToken=default;
        using(var lookup=new LyricLookup((_,token,_,online)=>{
            calls++;if(!online)return Task.FromResult(new LyricsResult(new(),"本地无缓存"));pendingToken=token;return pending.Task;
        }))
        {
            lookup.Observe(song);await lookup.PollAsync(false);var manual=lookup.RefreshAsync();
            for(int i=0;i<5;i++)await lookup.PollAsync(false);
            bool pendingManual=!pendingToken.IsCancellationRequested && lookup.Busy && calls==2;
            pending.SetResult(Good("用户主动查询"));await manual;
            checks["Offline automatic polling does not cancel an explicitly requested manual lookup"] = pendingManual && lookup.Result.Source=="用户主动查询";
        }

        calls=0;double now=0;
        using(var lookup=new LyricLookup((_,_,_,_)=>Task.FromResult(++calls==1?new LyricsResult(new(),"失败"):Good("立即恢复")),()=>now))
        {
            lookup.Observe(song);await lookup.PollAsync(true);await SetOnline(lookup,false);await SetOnline(lookup,true);await lookup.PollAsync(true);
            checks["Enabling automatic matching retries an unresolved song immediately instead of waiting for old backoff"] = calls==2 && lookup.Result.Source=="立即恢复";
        }

        calls=0;
        using(var lookup=new LyricLookup((_,_,_,_)=>{calls++;return Task.FromResult(Good("保留可用歌词"));}))
        {
            lookup.Observe(song);await lookup.PollAsync(true);await SetOnline(lookup,false);await lookup.PollAsync(false);
            await SetOnline(lookup,true);await lookup.PollAsync(true);
            checks["Changing network preference preserves resolved lyrics without an extra query"] = calls==1 && lookup.Result.Source=="保留可用歌词";
        }

        calls=0;pending=new(TaskCreationOptions.RunContinuationsAsynchronously);pendingToken=default;
        using(var lookup=new LyricLookup((_,token,_,_)=>{calls++;pendingToken=token;return pending.Task;}))
        {
            lookup.Observe(song);var local=lookup.PollAsync(false);await SetOnline(lookup,true);await SetOnline(lookup,false);
            bool localNotCancelled=!pendingToken.IsCancellationRequested && lookup.Busy;
            pending.SetResult(Good("本地读取"));await local;
            checks["Disabling automatic network does not discard a pending local cache read"] = localNotCancelled && calls==1 && lookup.Result.Source=="本地读取";
        }
        return checks;
    }
}
