using System.Net;
using System.Text.Json;
namespace LyricBridge;
internal static class GeneralMatchingRegressionTests
{
    public static async Task<Dictionary<string,bool>> RunAsync(string root)
    {
        var checks=new Dictionary<string,bool>();
        var song=new MediaSnapshot {Version=1,Type="media",Title="测试",Artist="测试歌手",DurationMs=180000,PositionKnown=true};
        foreach(var tag in new[]{"Extended Mix","Radio Edit","Rework","Bootleg","Mashup","Nightcore","Slowed"})
            checks["Edited recording qualifier remains significant: "+tag]=LyricsProvider.Score(song,"测试 ("+tag+")",song.Artist,180000)<65;
        checks["Same-script artist brackets are not proof of identity"]=!RecordingRules.ArtistsMatch("Adele","Cover Band (Adele)") && !RecordingRules.ArtistsMatch("周杰伦","某某 (周杰伦)");
        checks["Cross-script singer aliases remain usable"]=RecordingRules.ArtistsMatch("IU","아이유 (IU)") && RecordingRules.ArtistsMatch("邓紫棋","G.E.M.邓紫棋");
        checks["Missing known artist is not accepted by title alone"]=LyricsProvider.Score(song,song.Title,"",180000)<65;
        checks["Duration rounding tolerance is bounded"]=LyricsProvider.Score(song,song.Title,song.Artist,183000)>=65 && LyricsProvider.Score(song,song.Title,song.Artist,183001)<65;
        var provider=new LyricsProvider(Path.Combine(root,"instrumental-"+Guid.NewGuid().ToString("N")),new Fixture(uri=>
            uri.Host=="lrclib.net"?JsonSerializer.Serialize(new[]{new{trackName=song.Title,artistName=song.Artist,duration=180,instrumental=true}}):"{}"));
        var result=await provider.FetchAsync(song,CancellationToken.None);
        checks["Explicit instrumental metadata resolves without fabricated lines"]=result.IsInstrumental && result.Lines.Count==0;
        int requests=0;double now=0;
        using(var lookup=new LyricLookup((_,_)=>{
            if(++requests==1)return Task.FromResult(new LyricsResult(new(),"纯音乐",IsInstrumental:true));
            throw new IOException("Injected refresh failure");
        },()=>now))
        {
            lookup.Observe(song);await lookup.PollAsync(true);now=1000;await lookup.PollAsync(true);
            checks["Confirmed instrumental stops automatic lyric retries"]=requests==1 && !lookup.WillRetry && lookup.Result.IsInstrumental;
            await lookup.RefreshAsync();
            checks["Failed refresh preserves confirmed instrumental state"]=lookup.Result.IsInstrumental && !lookup.WillRetry;
        }
        return checks;
    }
    private sealed class Fixture(Func<Uri,string> reply):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(reply(request.RequestUri!))});
    }
}
