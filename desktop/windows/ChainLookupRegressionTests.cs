namespace LyricBridge;

internal static class ChainLookupRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var basic = new MediaSnapshot {
            Version=1, Type="media", PackageName="dev.custom.player", Title="同名作品", Artist="同一歌手",
            DurationMs=180000, PositionKnown=true, Playing=true, Source="media-session", SessionId="session-A"
        };
        static LyricsResult Good(string value) => new(Lrc.Parse("[00:00]"+value+"\n[02:50]结束"), value);
        foreach (var boundary in new[] {"session","source","track"})
        {
            var seen = new List<MediaSnapshot>();
            using var lookup = new LyricLookup((snapshot,_) => {
                seen.Add(snapshot);
                return Task.FromResult(Good(snapshot.MediaId.Length > 0 ? "旧录音" : "新录音"));
            });
            var first = basic with { MediaId="recording-A", Album="专辑A" };
            if (boundary=="track") first=first with { Source="integration",TrackId="track-A" };
            lookup.Observe(first); await lookup.PollAsync(true);
            var next=first with {MediaId="",Album=""};
            if(boundary=="session") next=next with {SessionId="session-B"};
            if(boundary=="source") next=next with {Source="notification"};
            if(boundary=="track") next=next with {TrackId="track-B"};
            bool changed=lookup.Observe(next); await lookup.PollAsync(true);
            checks["Changed "+boundary+" cannot inherit a previous recording identifier or album"] =
                changed && lookup.Active?.MediaId=="" && lookup.Active?.Album=="" &&
                seen.Count==2 && seen[^1].MediaId=="" && lookup.Result.Source=="新录音";
        }
        {
            int calls=0;
            using var lookup=new LyricLookup((_,_)=>{calls++;return Task.FromResult(Good("保留"));});
            lookup.Observe(basic with {MediaId="recording-A",Album="专辑A"});await lookup.PollAsync(true);
            bool changed=lookup.Observe(basic with {DurationMs=0});await lookup.PollAsync(true);
            checks["A temporary incomplete frame in the same session keeps identified fields and duration"] =
                !changed && calls==1 && lookup.Active?.MediaId=="recording-A" &&
                lookup.Active?.Album=="专辑A" && lookup.Active?.DurationMs==180000;
        }
        {
            int calls=0;bool bypassed=false;
            using var lookup=new LyricLookup((snapshot,_,bypass,_)=>{calls++;bypassed=bypass;return Task.FromResult(Good(snapshot.Album));});
            lookup.Observe(basic with {MediaId="recording-A"});await lookup.PollAsync(true);
            lookup.Observe(basic with {MediaId="recording-A",Album="专辑A"});await lookup.PollAsync(true);
            checks["Album completion within the same session still forces revalidation"] =
                calls==2 && bypassed && lookup.Result.Source=="专辑A";
        }
        {
            using var lookup=new LyricLookup((_,_)=>Task.FromResult(Good("旧会话")));
            lookup.Observe(basic);await lookup.PollAsync(true);
            lookup.Observe(basic with {SessionId="session-B",Album="专辑B",DurationMs=0});
            checks["A different session does not retain previous lyrics or a known duration for album completion"] =
                lookup.Result.Lines.Count==0 && lookup.Active?.DurationMs==0;
        }
        const string provided="[ti:同名作品]\n[ar:同一歌手]\n[00:00]播放器旧歌词\n[02:50]结束";
        var supplied=basic with {Source="integration",SessionId="shared-session",TrackId="shared-track",
            ProvidedLyrics=provided,ProvidedLyricsTrackId="shared-track"};
        {
            var requests=new List<bool>();
            using var lookup=new LyricLookup((snapshot,_,bypass,_)=>{requests.Add(bypass);return Task.FromResult(Good(snapshot.PackageName));});
            var a=supplied with {PackageName="dev.player.a"};
            var b=supplied with {PackageName="dev.player.b",ProvidedLyrics="",ProvidedLyricsTrackId=""};
            lookup.Observe(b);await lookup.PollAsync(true);
            lookup.Observe(a);
            lookup.Observe(b);
            bool held=lookup.Result.Lines.Count>0;
            await lookup.PollAsync(true);
            checks["Different packages with coincident integration identities do not clear their cached result"] =
                held && requests.Count==1 && lookup.Result.Source=="dev.player.b";
        }
        {
            var requests=new List<bool>();
            using var lookup=new LyricLookup((_,_,bypass,_)=>{requests.Add(bypass);return Task.FromResult(Good("网络新歌词"));});
            lookup.Observe(supplied);
            lookup.Observe(supplied with {ProvidedLyrics="",ProvidedLyricsTrackId=""});
            bool cleared=lookup.Result.Lines.Count==0;await lookup.PollAsync(true);
            checks["An explicit withdrawal in the same integration scope clears and bypasses old lyrics"] =
                cleared && requests.Count==1 && requests[0] && lookup.Result.Source=="网络新歌词";
        }
        {
            int requests=0;
            using var lookup=new LyricLookup((_,_)=>{requests++;return Task.FromResult(Good("网络新歌词"));});
            lookup.Observe(supplied);
            lookup.Observe(supplied with {ProvidedLyrics=""});
            await lookup.PollAsync(true);
            checks["Omitted payload with an intact track binding retains the current provided lyrics"] =
                requests==0 && lookup.Result.Lines.Any(line=>line.Text=="播放器旧歌词");
        }
        {
            int requests=0;
            using var lookup=new LyricLookup((_,_)=>{requests++;return Task.FromResult(Good("网络新歌词"));});
            lookup.Observe(supplied);
            lookup.Observe(basic with {Title="另一首"});
            await lookup.PollAsync(true);
            lookup.Observe(supplied with {ProvidedLyrics="",ProvidedLyricsTrackId=""});
            bool restored=lookup.Result.Lines.Any(line=>line.Text=="播放器旧歌词");
            await lookup.PollAsync(true);
            checks["Returning to a withdrawn integration track cannot resurrect provided lyrics from memory"] =
                !restored && requests==2 && lookup.Result.Source=="网络新歌词";
        }
        {
            int requests=0;
            var incoming=supplied with {ProvidedLyrics="",ProvidedLyricsTrackId=""};
            using var lookup=new LyricLookup((snapshot,_)=>{
                requests++;
                if(requests==1) return Task.FromResult(Good("本地缓存") with {FromProvidedLyrics=true});
                return Task.FromResult(Good("网络新歌词"));
            });
            lookup.Observe(incoming);await lookup.PollAsync(true);
            lookup.Observe(basic with {Title="另一首"});await lookup.PollAsync(true);
            lookup.Observe(incoming);
            bool heldOld=lookup.Result.Source=="本地缓存";await lookup.PollAsync(true);
            checks["Provided provenance survives a private-cache source label when invalidating memory"] =
                !heldOld && requests==3 && lookup.Result.Source=="网络新歌词";
        }
        {
            int requests=0;
            using var lookup=new LyricLookup((_,_)=>{requests++;return Task.FromResult(Good("另一首"));});
            lookup.Observe(supplied);
            lookup.Observe(basic with {Title="另一首"});await lookup.PollAsync(true);
            lookup.Observe(supplied with {ProvidedLyrics=""});await lookup.PollAsync(true);
            checks["Returning with an intact provided binding can reuse the same-track memory entry"] =
                requests==1 && lookup.Result.Lines.Any(line=>line.Text=="播放器旧歌词");
        }
        {
            using var lookup=new LyricLookup((_,_)=>throw new IOException("Injected unexpected read error"));
            lookup.Observe(basic);await lookup.PollAsync(true);
            checks["Unexpected lyric read failure stays retryable and does not masquerade as a catalogue miss"] =
                lookup.Result.Outcome==LyricsOutcome.ReadFailure && lookup.Result.EmptyMessage=="歌词读取暂时失败" && lookup.WillRetry;
        }
        {
            var initial=basic with {DurationMs=0,Source="integration",SessionId="late-duration",TrackId="track",
                ProvidedLyricsTrackId="track",ProvidedLyrics="[00:00]第一句\n[03:30]越界旧词"};
            using var lookup=new LyricLookup((_,_)=>Task.FromResult(new LyricsResult(new(),"没有可用替代",Outcome:LyricsOutcome.NoMatch)));
            lookup.Observe(initial);
            bool initiallyValid=lookup.Result.Lines.Count==2;
            lookup.Observe(initial with {DurationMs=180000});
            bool cleared=lookup.Result.Lines.Count==0;
            await lookup.PollAsync(true);
            checks["Completed duration immediately removes invalid provided lyrics even if fallback fails"] =
                initiallyValid && cleared && lookup.Result.Lines.Count==0 && lookup.WillRetry;
        }
        {
            int calls=0;
            using var lookup=new LyricLookup((_,_)=>Task.FromResult(++calls==1
                ? new LyricsResult(Lrc.Parse("[00:00]第一句\n[03:30]越界网络旧词"),"network")
                : new LyricsResult(new(),"没有替代",Outcome:LyricsOutcome.NoMatch)));
            lookup.Observe(basic with {DurationMs=0});await lookup.PollAsync(true);
            lookup.Observe(basic);
            bool cleared=lookup.Result.Lines.Count==0;await lookup.PollAsync(true);
            checks["Completed duration cannot preserve an out-of-range network result across failed revalidation"] =
                cleared && lookup.Result.Lines.Count==0 && lookup.WillRetry && calls==2;
        }
        {
            var initial=basic with {Album="",Source="integration",SessionId="late-album",TrackId="track",
                ProvidedLyricsTrackId="track",ProvidedLyrics="[al:旧专辑]\n[00:00]第一句\n[02:00]旧专辑第二句"};
            using var lookup=new LyricLookup((_,_)=>Task.FromResult(new LyricsResult(new(),"没有替代",Outcome:LyricsOutcome.NoMatch)));
            lookup.Observe(initial);bool initiallyValid=lookup.Result.Lines.Count==2;
            lookup.Observe(initial with {Album="新专辑"});
            bool cleared=lookup.Result.Lines.Count==0;await lookup.PollAsync(true);
            checks["Completed album invalidates conflicting provided body instead of preserving it after failure"] =
                initiallyValid && cleared && lookup.Result.Lines.Count==0 && lookup.WillRetry;
        }
        return checks;
    }
}
