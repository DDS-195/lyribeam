using System.Net;
using System.Text.Json;
namespace LyricBridge;
internal static class CompatibilityRegressionTests
{
    public static async Task<Dictionary<string,bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string,bool>();
        var song = MediaSnapshot.Parse("""{"version":1,"type":"media","title":"晴天","artist":"周杰伦","packageName":"com.netease.cloudmusic","mediaId":"186016","album":"叶惠美","durationMs":269000,"positionKnown":true}""");
        string Album(MediaSnapshot s) { var j=JsonSerializer.SerializeToElement(s); return j.TryGetProperty("Album",out var a)?a.GetString()??"":""; }
        checks["Album survives Bluetooth metadata parsing"] = Album(song)=="叶惠美";
        checks["Legacy sender without album remains compatible"] = Album(MediaSnapshot.Parse("""{"version":1,"type":"media","title":"晴天","artist":"周杰伦"}"""))=="";
        checks["Explicit null album is normalized"] = JsonSerializer.Serialize(MediaSnapshot.Parse("""{"version":1,"type":"media","title":"晴天","artist":"周杰伦","album":null}""")).Contains("\"Album\":\"\"");
        bool rejected=false;try {MediaSnapshot.Parse(JsonSerializer.Serialize(new{version=1,type="media",title="晴天",artist="周杰伦",album=new string('x',513)}));}catch(FormatException){rejected=true;}
        checks["Oversized album is rejected without invalidating bounded metadata"] = rejected;
        var messages = new List<string>();
        using(var lookup = new LyricLookup((_,_)=>Task.FromResult(new LyricsResult(new(),"没有候选"))))
        {
            lookup.Diagnostic += messages.Add; lookup.Observe(song); await lookup.PollAsync(true);
        }
        string context=messages.FirstOrDefault(m=>m.StartsWith("lookup-context: "))??"";
        checks["Lookup context records player artist album duration and opaque ID"] = context.Contains("com.netease.cloudmusic")&&context.Contains("186016")&&context.Contains("269000")&&context.Contains("artist")&&context.Contains("album");
        string Area(string name)=>Path.Combine(root,name+"-"+Guid.NewGuid().ToString("N"));
        string None(Uri u)=>u.Host=="lrclib.net"?"[]":"{}";
        var fallbackSong=song with {PackageName="com.luna.music",MediaId="",Artist="G.E.M.邓紫棋"};
        int titleOnly=0;
        var provider=new LyricsProvider(Area("title-only"),new Fixture(uri=>{
            if(uri.Host!="lrclib.net")return None(uri);
            if(uri.Query.Contains("artist_name="))return "[]";
            titleOnly++;return JsonSerializer.Serialize(new[]{new{trackName="晴天",artistName="邓紫棋",duration=269,syncedLyrics=CompleteTimeline("[00:00]同歌手别名的有效歌词")}});
        }));
        var result=await provider.FetchAsync(fallbackSong,CancellationToken.None);
        checks["Title-only retry recovers artist query spelling while validating singer"] = result.Lines.Count == 2&&titleOnly>0;
        provider=new LyricsProvider(Area("wrong-artist"),new Fixture(uri=>uri.Host=="lrclib.net"&&!uri.Query.Contains("artist_name=")
            ?JsonSerializer.Serialize(new[]{new{trackName="晴天",artistName="其他歌手",duration=269,syncedLyrics=CompleteTimeline("[00:00]错误歌手")}}):None(uri)));
        result=await provider.FetchAsync(fallbackSong,CancellationToken.None);
        checks["Broader title search never accepts unrelated singer"] = result.Lines.Count==0;
        provider=new LyricsProvider(Area("original-label"),new Fixture(uri=>{
            if(uri.Host!="lrclib.net")return None(uri);
            return Uri.UnescapeDataString(uri.Query).Contains("track_name=HEAVEN&")
                ?JsonSerializer.Serialize(new[]{new{trackName="HEAVEN",artistName="周杰伦",duration=269,syncedLyrics=CompleteTimeline("[00:00]原版标签查询")}}):"[]";
        }));
        result=await provider.FetchAsync(song with{PackageName="compat",MediaId="",Title="HEAVEN (Original Version)"},CancellationToken.None);
        checks["Equivalent original-version label is removed for search as well as validation"] = result.Lines.Count == 2;
        provider=new LyricsProvider(Area("album-ranking"),new Fixture(uri=>uri.Host=="lrclib.net"
            ?JsonSerializer.Serialize(new[]{
                new{trackName="晴天",artistName="周杰伦",albumName="其他专辑",duration=269,syncedLyrics=CompleteTimeline("[00:00]较弱候选")},
                new{trackName="晴天",artistName="周杰伦",albumName="叶惠美",duration=269,syncedLyrics=CompleteTimeline("[00:00]专辑相符候选")}}):None(uri)));
        result=await provider.FetchAsync(song with{PackageName="compat",MediaId=""},CancellationToken.None);
        checks["Matching album ranks otherwise equal lyric candidates first"] = result.Lines.FirstOrDefault()?.Text=="专辑相符候选";
        messages.Clear();
        provider=new LyricsProvider(Area("rejections"),new Fixture(uri=>uri.Host=="lrclib.net"
            ?JsonSerializer.Serialize(new[]{
                new{trackName="晴天",artistName="其他歌手",duration=269,syncedLyrics=CompleteTimeline("[00:00]错误歌手")},
                new{trackName="晴天",artistName="周杰伦",duration=300,syncedLyrics=CompleteTimeline("[00:00]错误时长")},
                new{trackName="晴天 (Live)",artistName="周杰伦",duration=269,syncedLyrics=CompleteTimeline("[00:00]错误版本")}}):None(uri)));
        provider.Diagnostic += messages.Add; result=await provider.FetchAsync(song with{PackageName="compat",MediaId=""},CancellationToken.None);
        checks["Rejected candidates report artist duration and title-version reasons"] = result.Lines.Count==0&&messages.Any(m=>m.Contains("artist=1")&&m.Contains("duration=1")&&m.Contains("title-version=1"));
        checks["Diagnostics do not contain lyric bodies"] = messages.All(m=>!m.Contains("错误歌手")&&!m.Contains("错误时长")&&!m.Contains("错误版本"));
        string NetDetail(string name="晴天") => JsonSerializer.Serialize(new { code=200, songs=new[]{new{id=186016,name,duration=269000,artists=new[]{new{name="周杰伦"}}}}});
        var native = song;
        bool referer=false;
        provider=new LyricsProvider(Area("native-priority"),new AsyncFixture(async(request,token)=>{
            var uri=request.RequestUri!;
            if(uri.Host=="music.163.com") {
                referer|=request.Headers.Referrer?.Host=="music.163.com";
                await Task.Delay(80,token);
                return uri.AbsolutePath.Contains("/detail")?NetDetail():JsonSerializer.Serialize(new{code=200,lrc=new{lyric=CompleteTimeline("[ti:晴天]\n[ar:周杰伦]\n[00:00]平台正确录音")}});
            }
            return uri.Host=="lrclib.net"?JsonSerializer.Serialize(new[]{new{trackName="晴天",artistName="周杰伦",duration=269,syncedLyrics=CompleteTimeline("[00:00]普通检索录音")}}):"{}";
        }));
        result=await provider.FetchAsync(native,CancellationToken.None);
        checks["Verified platform ID retains priority over quick generic result"] = result.Source.Contains("网易云")&&result.Lines.FirstOrDefault()?.Text=="平台正确录音"&&referer;
        int nativeLyrics=0;
        provider=new LyricsProvider(Area("bad-native-id"),new Fixture(uri=>{
            if(uri.Host=="music.163.com") { if(uri.AbsolutePath.Contains("/detail"))return NetDetail("晴天 (Live)"); if(uri.AbsolutePath.Contains("lyric"))nativeLyrics++;return "{}"; }
            return uri.Host=="lrclib.net"?JsonSerializer.Serialize(new[]{new{trackName="晴天",artistName="周杰伦",duration=269,syncedLyrics=CompleteTimeline("[00:00]编号不可信仍有正确兜底")}}):"{}";
        }));
        result=await provider.FetchAsync(native,CancellationToken.None);
        checks["Unverified platform ID falls back without fetching wrong recording"] = nativeLyrics==0&&result.Source=="LRCLIB"&&result.Lines.Count == 2;
        provider=new LyricsProvider(Area("bad-native-body"),new Fixture(uri=>{
            if(uri.Host=="music.163.com")return uri.AbsolutePath.Contains("/detail")?NetDetail():JsonSerializer.Serialize(new{code=200,lrc=new{lyric=CompleteTimeline("[ti:晴天 (Live)]\n[ar:周杰伦]\n[00:00]平台错版正文")}});
            return uri.Host=="lrclib.net"?JsonSerializer.Serialize(new[]{new{trackName="晴天",artistName="周杰伦",duration=269,syncedLyrics=CompleteTimeline("[00:00]歌词正文拒绝后兜底")}}):"{}";
        }));
        result=await provider.FetchAsync(native,CancellationToken.None);
        checks["Platform lyrics still undergo downloaded recording header validation"] = result.Source=="LRCLIB"&&result.Lines.FirstOrDefault()?.Text=="歌词正文拒绝后兜底";
        bool nativeCancelled=false;
        provider=new LyricsProvider(Area("native-timeout"),new AsyncFixture(async(request,token)=>{
            if(request.RequestUri!.Host=="music.163.com") {
                try {await Task.Delay(Timeout.Infinite,token);} catch(OperationCanceledException){nativeCancelled=true;throw;}
            }
            return request.RequestUri.Host=="lrclib.net"?JsonSerializer.Serialize(new[]{new{trackName="晴天",artistName="周杰伦",duration=269,syncedLyrics=CompleteTimeline("[00:00]编号超时后及时显示")}}):"{}";
        }));
        var watch=System.Diagnostics.Stopwatch.StartNew();
        result=await provider.FetchAsync(native,CancellationToken.None);
        await Task.Delay(30);
        checks["Stalled platform request cannot hold a validated generic result indefinitely"] = result.Lines.Count == 2&&watch.ElapsedMilliseconds<2800&&nativeCancelled;

        int wrongPlatformId=0, generalSearch=0;
        provider=new LyricsProvider(Area("third-source"),new Fixture(uri=>{
            if(uri.Host!="music.163.com")return None(uri);
            if(uri.AbsolutePath.Contains("/search/")) {generalSearch++;return JsonSerializer.Serialize(new{code=200,result=new{songs=new[]{new{id=186016,name="晴天",duration=269000,artists=new[]{new{name="周杰伦"}}}}}});}
            if(uri.Query.Contains("id=123&"))wrongPlatformId++;
            return JsonSerializer.Serialize(new{code=200,lrc=new{lyric=CompleteTimeline("[ti:晴天]\n[ar:周杰伦]\n[00:00]第三曲库的可信歌词")}});
        }));
        result=await provider.FetchAsync(song with{PackageName="com.miui.player",MediaId="123"},CancellationToken.None);
        checks["Additional catalogue recovers other players without interpreting their opaque ID"] = generalSearch>0&&wrongPlatformId==0&&result.Source.Contains("网易云")&&result.Lines.FirstOrDefault()?.Text=="第三曲库的可信歌词";
        int extraTraffic=0;
        provider=new LyricsProvider(Area("no-extra-traffic"),new Fixture(uri=>{
            if(uri.Host=="music.163.com") {extraTraffic++;return "{}";}
            return uri.Host=="lrclib.net"?JsonSerializer.Serialize(new[]{new{trackName="晴天",artistName="周杰伦",duration=269,syncedLyrics=CompleteTimeline("[00:00]快速原来源")}}):"{}";
        }));
        result=await provider.FetchAsync(song with{PackageName="com.luna.music",MediaId=""},CancellationToken.None);
        await Task.Delay(550);
        checks["Fast existing source cancels delayed additional catalogue before HTTP"] = result.Source=="LRCLIB"&&extraTraffic==0;
        provider=new LyricsProvider(Area("platform-connectivity"),new Fixture(uri=>{
            if(uri.Host=="music.163.com")throw new HttpRequestException("Injected platform offline",null,HttpStatusCode.Forbidden);
            return None(uri);
        }));
        result=await provider.FetchAsync(native,CancellationToken.None);
        checks["Platform connection failure remains distinguishable from all catalogues empty"] = result.Lines.Count==0&&result.Source.Contains("部分来源");

        return checks;
    }
    private static string CompleteTimeline(string content) => content + "\n[03:40]测试后续歌词";

    private sealed class AsyncFixture(Func<HttpRequestMessage,CancellationToken,Task<string>> reply):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>new(HttpStatusCode.OK){Content=new StringContent(await reply(request,token))};
    }

    private sealed class Fixture(Func<Uri,string> reply):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(reply(request.RequestUri!))});
    }
}
