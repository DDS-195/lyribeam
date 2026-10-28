namespace LyricBridge;
internal static class CacheReviewRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var song = new MediaSnapshot { Type="media", PackageName="com.tencent.qqmusic", Title="缓存审核歌曲", Artist="审核歌手", Album="审核专辑", DurationMs=180000, MediaId="4242", Source="media-session", SessionId="session-one" };
        static LyricsResult Good(string marker, bool review=false) => new(new(){new(0,marker+"第一句"),new(120000,marker+"第二句")},marker) { NeedsSourceReview=review };
        double time=0; int calls=0; bool bypassed=false, onlineReview=false;
        var pending=new TaskCompletionSource<LyricsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var lookup=new LyricLookup((_,_,bypass,online)=>{
            calls++; bypassed=bypass; onlineReview=online;
            return calls==1?Task.FromResult(Good("备用缓存",true)):pending.Task;
        },()=>time))
        {
            lookup.Observe(song);await lookup.PollAsync(true);
            checks["A reviewable cache is displayed immediately while its review is scheduled"] = calls==1 && lookup.Result.Source=="备用缓存" && lookup.WillRetry && !lookup.Busy;
            time=4.9;await lookup.PollAsync(true);
            checks["A cached result does not delay initial display or trigger the review before five seconds"] = calls==1;
            time=5;var reviewing=lookup.PollAsync(true);
            checks["A background source review bypasses disk and preserves the displayed lyrics while pending"] = calls==2 && bypassed && onlineReview && lookup.Busy && lookup.Result.Source=="备用缓存";
            pending.TrySetResult(Good("原平台确认"));await reviewing;
            checks["A stronger reviewed source replaces the provisional cache and stops automatic review"] = lookup.Result.Source=="原平台确认" && !lookup.WillRetry && !lookup.Busy;
        }
        time=0;calls=0;
        using(var lookup=new LyricLookup((_,_,_,_)=>Task.FromResult(++calls==1?Good("可信原平台"):Good("不该调用")),()=>time))
        {
            lookup.Observe(song);await lookup.PollAsync(true);time=10000;for(int i=0;i<5;i++)await lookup.PollAsync(true);
            checks["Strong source results do not receive unnecessary background requests"] = calls==1 && !lookup.WillRetry;
        }
        foreach(bool throws in new[]{false,true})
        {
            time=0;calls=0;
            using(var lookup=new LyricLookup((_,_,_,_)=>{
                calls++;if(calls==1)return Task.FromResult(Good("仍然可用",true));
                if(throws)throw new HttpRequestException("temporary failure");
                return Task.FromResult(new LyricsResult(new(),"服务暂时不可用",Outcome:LyricsOutcome.PartialFailure));
            },()=>time))
            {
                lookup.Observe(song);await lookup.PollAsync(true);time=5;await lookup.PollAsync(true);
                time=10000;for(int i=0;i<5;i++)await lookup.PollAsync(true);
                checks["A "+(throws?"thrown":"structured")+" source-review failure preserves lyrics and does not loop"] = calls==2 && lookup.Result.Source=="仍然可用" && !lookup.WillRetry;
            }
        }
        time=0;calls=0;
        using(var lookup=new LyricLookup((_,_,_,_)=>Task.FromResult(++calls==1?Good("备用旧词",true):Good("备用新词",true)),()=>time))
        {
            lookup.Observe(song);await lookup.PollAsync(true);time=5;await lookup.PollAsync(true);time=10000;await lookup.PollAsync(true);
            checks["A reviewed fallback can remain usable without scheduling an infinite review sequence"] = calls==2 && lookup.Result.Source=="备用新词" && !lookup.WillRetry;
            lookup.Observe(song with{Title="别的歌曲",MediaId="9000"});await lookup.PollAsync(true);
            lookup.Observe(song);await lookup.PollAsync(true);time+=20;await lookup.PollAsync(true);
            checks["Returning to an already reviewed recording does not restart its automatic review"] = calls==3 && lookup.Result.Source=="备用新词" && !lookup.WillRetry;
            await lookup.RefreshAsync();await lookup.RefreshAsync();
            checks["Explicit rematch remains available after the one automatic source review"] = calls==5;
        }
        time=0;calls=0;var modes=new List<(bool Bypass,bool Online)>();
        using(var lookup=new LyricLookup((_,_,bypass,online)=>{
            calls++;modes.Add((bypass,online));return Task.FromResult(Good(online?"联网确认":"离线缓存",!online));
        },()=>time))
        {
            lookup.Observe(song);await lookup.PollAsync(false);time=100;for(int i=0;i<5;i++)await lookup.PollAsync(false);
            bool offline=calls==1 && !modes[0].Online && lookup.Result.Source=="离线缓存";
            await lookup.PollAsync(true);
            checks["Disabled network still displays the cached result and resumes its deferred review when enabled"] = offline && calls==2 && modes[1].Bypass && modes[1].Online && lookup.Result.Source=="联网确认";
        }
        time=0;calls=0;bool reviewCancelled=false;
        var offlineReview=new TaskCompletionSource<LyricsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var lookup=new LyricLookup((_,token,_,_)=>{
            calls++;if(calls==1)return Task.FromResult(Good("断网时保留",true));
            token.Register(()=>reviewCancelled=true);return offlineReview.Task;
        },()=>time))
        {
            lookup.Observe(song);await lookup.PollAsync(true);time=5;var old=lookup.PollAsync(true);
            await lookup.PollAsync(false);bool cancelled=reviewCancelled;
            offlineReview.TrySetResult(Good("离线后不该替换"));await old;await lookup.PollAsync(true);
            checks["Disabling network cancels an in-flight source review while preserving available lyrics"] =
                calls==2 && cancelled && lookup.Result.Source=="断网时保留" && !lookup.Busy && !lookup.WillRetry;
        }
        time=0;calls=0;
        var oldReview=new TaskCompletionSource<LyricsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var lookup=new LyricLookup((snapshot,_,_,_)=>{
            calls++;return snapshot.Title!=song.Title?Task.FromResult(Good("新歌")):calls==1?Task.FromResult(Good("旧歌缓存",true)):oldReview.Task;
        },()=>time))
        {
            lookup.Observe(song);await lookup.PollAsync(true);time=5;var old=lookup.PollAsync(true);
            lookup.Observe(song with{Title="新歌曲",MediaId="9000"});await lookup.PollAsync(true);
            oldReview.TrySetResult(Good("过时旧歌确认"));await old;
            checks["A completed old source review cannot overwrite a newer track"] = calls==3 && lookup.Result.Source=="新歌" && lookup.Active?.Title=="新歌曲";
        }
        time=0;calls=0;
        using(var lookup=new LyricLookup((_,_,_,_)=>{
            calls++;return Task.FromResult(calls==1?Good("旧缓存",true):calls==2?new LyricsResult(new(),"确认失败"):Good("时长校验恢复"));
        },()=>time))
        {
            lookup.Observe(song with{DurationMs=0});await lookup.PollAsync(true);
            lookup.Observe(song);await lookup.PollAsync(true);
            bool retained=lookup.Result.Source=="旧缓存" && lookup.WillRetry;
            time=4;await lookup.PollAsync(true);
            checks["A source-review flag does not suppress mandatory metadata revalidation retries"] = retained && calls==3 && lookup.Result.Source=="时长校验恢复" && !lookup.WillRetry;
        }
        time=0;calls=0;
        using(var lookup=new LyricLookup((_,_,_,_)=>Task.FromResult(Good("会话缓存",++calls!=2)),()=>time))
        {
            lookup.Observe(song);await lookup.PollAsync(true);time=5;await lookup.PollAsync(true);
            lookup.Observe(song with{SessionId="session-two"});await lookup.PollAsync(true);time+=20;await lookup.PollAsync(true);
            checks["A reconnect cannot turn the same already reviewed recording into repeated background traffic"] = calls==3 && !lookup.WillRetry;
        }
        return checks;
    }
}
