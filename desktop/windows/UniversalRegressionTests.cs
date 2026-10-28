using System.Text;
using System.Text.Json;
namespace LyricBridge;
internal static class UniversalRegressionTests
{
    public static async Task<Dictionary<string,bool>> RunAsync(string root)
    {
        var checks=new Dictionary<string,bool>();
        var song=new MediaSnapshot{Version=1,Type="media",Title="通用歌曲",Artist="作者",Album="专辑",PackageName="com.local.customplayer",
            Source="integration",Capability="sync",SessionId="session-1",TrackId="track-1",DurationMs=60000,PositionKnown=true,PositionMs=1000,Playing=true};
        const string lyrics="[ti:通用歌曲]\n[ar:作者]\n[al:专辑]\n[00:00]第一句\n[00:45]第二句";
        var supplied=song with{ProvidedLyrics=lyrics,ProvidedLyricsTrackId=song.TrackId};
        var parsed=MediaSnapshot.Parse(JsonSerializer.Serialize(supplied));
        checks["Unified sender fields and bound lyrics survive protocol parsing"]=parsed.Source=="integration"&&parsed.SessionId=="session-1"&&parsed.TrackId=="track-1"&&parsed.ProvidedLyrics==lyrics;
        checks["Capability follows actual clock rather than unverified advertised value"]=(song with{PositionKnown=false,Capability="sync"}).EffectiveCapability=="metadata-only";
        var old=MediaSnapshot.Parse("""{"version":1,"type":"media","title":"旧手机","positionKnown":true,"positionMs":0}""");
        checks["Previous phone protocol remains compatible"]=old.Source==""&&old.ProvidedLyrics==""&&old.EffectiveCapability=="sync";
        bool Reject(MediaSnapshot s){try{MediaSnapshot.Parse(JsonSerializer.Serialize(s));return false;}catch(FormatException){return true;}}
        checks["Notification text cannot claim a playback clock"]=Reject(song with{Source="notification"});
        checks["Unknown source codes are rejected"]=Reject(song with{Source="untrusted"});
        checks["Unknown capability codes are rejected"]=Reject(song with{Capability="everything"});
        checks["Integration needs session and track identities"]=Reject(song with{SessionId=""})&&Reject(song with{TrackId=""});
        checks["Unified identity and lyric payload limits are enforced"]=Reject(song with{TrackId=new string('x',257)})&&Reject(supplied with{ProvidedLyrics=new string('x',65537)});
        checks["Integration track identities isolate private recording cache keys"]=song.TrackKey!=(song with{TrackId="track-2"}).TrackKey;
        checks["Legacy private cache keys retain exact compatibility"]=old.TrackKey==old.LegacyTrackKey;
        string large="[00:00]"+new string('词',10000)+"\n[00:45]第二句";
        string frame=JsonSerializer.Serialize(supplied with{ProvidedLyrics=large})+"\n";
        var bytes=Encoding.UTF8.GetBytes(frame);var framed=new LineFramer().Append(bytes,bytes.Length);
        checks["Complete bounded lyric payload supports frames larger than old 16K limit"]=frame.Length>16384&&framed.Count==1&&MediaSnapshot.Parse(framed[0]).ProvidedLyrics==large;
        bool tooLarge=false;try{var overflow=Encoding.UTF8.GetBytes(new string('x',LineFramer.MaximumCharacters+1));new LineFramer().Append(overflow,overflow.Length);}catch(FormatException){tooLarge=true;}
        checks["Expanded playback frames remain bounded"]=tooLarge;
        int requests=0;
        using(var lookup=new LyricLookup((_,_)=>{requests++;return Task.FromResult(new LyricsResult(new(),"fallback"));}))
        {
            lookup.Observe(supplied);await lookup.PollAsync(false);
            checks["Provided lyrics display synchronously without online lookup"]=lookup.Result.Lines.Count==2&&requests==0&&lookup.Result.Source.Contains("播放器提供");
            lookup.Observe(song with{ProvidedLyricsTrackId=song.TrackId});await lookup.PollAsync(true);
            checks["Same playback heartbeat safely retains omitted complete lyrics"]=lookup.Active?.ProvidedLyrics==lyrics&&lookup.Result.Lines.Count==2&&requests==0;
            lookup.Observe(song with{TrackId="track-2",ProvidedLyricsTrackId="track-2"});
            checks["Next track cannot inherit omitted old lyric payload"]=lookup.Active?.ProvidedLyrics==""&&lookup.Result.Lines.Count==0;
        }
        using(var lookup=new LyricLookup((_,_)=>Task.FromResult(new LyricsResult(new(),"fallback"))))
        {
            lookup.Observe(supplied);lookup.Observe(song with{SessionId="session-2",ProvidedLyricsTrackId=song.TrackId});
            checks["New session cannot inherit an old session's lyric payload"]=lookup.Active?.ProvidedLyrics==""&&lookup.Result.Lines.Count==0;
        }
        using(var lookup=new LyricLookup((_,_)=>Task.FromResult(new LyricsResult(new(),"fallback"))))
        {
            lookup.Observe(supplied);lookup.Observe(song with{PackageName="com.another.player",ProvidedLyricsTrackId=song.TrackId});
            checks["Different player cannot inherit another player's omitted payload"]=lookup.Active?.ProvidedLyrics==""&&lookup.Result.Lines.Count==0;
        }
        using(var lookup=new LyricLookup((_,_)=>Task.FromResult(new LyricsResult(new(),"fallback"))))
        {
            lookup.Observe(supplied);lookup.Observe(song);
            checks["Explicitly withdrawn provided lyrics require fresh validation"]=lookup.Active?.ProvidedLyrics==""&&lookup.Result.Lines.Count==0&&lookup.WillRetry;
        }
        using(var lookup=new LyricLookup((_,_)=>Task.FromResult(new LyricsResult(new(),"fallback"))))
        {
            lookup.Observe(supplied);lookup.Observe(supplied with{ProvidedLyrics="[ti:另一首]\n[00:00]错歌\n[00:45]错歌"});
            checks["Invalid replacement cannot remain labelled as current provided lyrics"]=lookup.Result.Lines.Count==0&&lookup.WillRetry;
        }
        var pending=new TaskCompletionSource<LyricsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var lookup=new LyricLookup((_,_)=>pending.Task))
        {
            lookup.Observe(song);var oldRequest=lookup.PollAsync(true);lookup.Observe(supplied);
            pending.SetResult(new(Lrc.Parse("[00:00]迟到网络结果"),"network"));await oldRequest;
            checks["Late network response cannot overwrite newly provided lyrics"]=lookup.Result.Source.Contains("播放器提供")&&lookup.Result.Lines.Count==2;
            lookup.EndProvidedPlayback();
            checks["Cleared or expired integrated playback drops active payload"]=lookup.Active==null&&lookup.Result.Lines.Count==0;
        }
        using(var lookup=new LyricLookup((_,_)=>Task.FromResult(new LyricsResult(new(),"fallback"))))
        {
            lookup.Observe(supplied);lookup.Observe(supplied with{ProvidedLyrics="[ti:通用歌曲]\n[ar:作者]\n[00:00]新的第一句\n[00:45]新的第二句"});
            checks["Updated lyrics for current playback replace lines immediately"]=lookup.Result.Lines[0].Text=="新的第一句";
        }
        return checks;
    }
}
