using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace LyricBridge;
internal static class ChainProviderRegressionTests
{
    public static async Task<Dictionary<string,bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string,bool>();
        var song = new MediaSnapshot {Version=1,Type="media",PackageName="custom.player",Title="测试作品",Artist="测试歌手",Album="专辑",DurationMs=180000,PositionKnown=true};
        const string full = "[ti:测试作品]\n[ar:测试歌手]\n[al:专辑]\n[00:00]完整第一句\n[00:45]完整第二句\n[01:30]完整第三句\n[02:40]完整第四句";
        string Area(string name) => Path.Combine(root,name+"-"+Guid.NewGuid().ToString("N"));
        string None(Uri uri) => uri.Host=="lrclib.net" ? "[]" : uri.Host=="music.163.com" ? "{\"code\":200,\"result\":{\"songs\":[]}}" : "{}";
        string Library(string content="",bool instrumental=false) => JsonSerializer.Serialize(new[]{new{trackName=song.Title,artistName=song.Artist,duration=180,syncedLyrics=content,instrumental}});
        string Search() => JsonSerializer.Serialize(new{candidates=new[]{new{song=song.Title,singer=song.Artist,duration=180000,id="good",accesskey="x"}}});
        string Download(string text) => JsonSerializer.Serialize(new{content=Convert.ToBase64String(Encoding.UTF8.GetBytes(text))});
        string Cache(string area,MediaSnapshot s) => Path.Combine(area,"lyrics",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.TrackKey)))+".lrc");
        var provider=new LyricsProvider(Area("instrumental-race"),new Fixture(async(uri,token)=>{
            if(uri.Host=="lrclib.net") return Library(instrumental:true);
            if(uri.Host=="music.163.com"||uri.Host=="songsearch.kugou.com") return None(uri);
            if(uri.AbsolutePath.EndsWith("search")) return Search();
            await Task.Delay(300,token);return Download(full);
        }));
        var result=await provider.FetchAsync(song,CancellationToken.None);
        checks["A single instrumental flag cannot cancel valid pending vocal lyrics"]=!result.IsInstrumental&&result.Lines.Count==4;
        provider=new LyricsProvider(Area("unconfirmed-instrumental"),new Fixture(async(uri,token)=>{
            if(uri.Host=="lrclib.net")return Library(instrumental:true);
            if(uri.Host=="music.163.com")return None(uri);
            await Task.Delay(Timeout.Infinite,token);return "{}";
        }));
        result=await provider.FetchAsync(song,CancellationToken.None);
        checks["Unconfirmed instrumental with unavailable peers stays retryable"]=!result.IsInstrumental&&result.Lines.Count==0&&result.Outcome==LyricsOutcome.Unreliable;
        foreach(string package in new[]{"com.tencent.qqmusic","com.kugou.android"})
        {
            var identified=song with{PackageName=package,MediaId="4242"};
            provider=new LyricsProvider(Area(package),new Fixture(async(uri,token)=>{
                if(uri.Host=="lrclib.net"){await Task.Delay(30,token);return Library(full);}
                if(uri.Host=="c.y.qq.com")return "{\"code\":0,\"data\":[]}";
                if(uri.Host=="songsearch.kugou.com")return "{\"data\":{\"lists\":[]}}";
                if(uri.Host=="music.163.com")return None(uri);
                await Task.Delay(Timeout.Infinite,token);return "{}";
            }));
            var watch=Stopwatch.StartNew();result=await provider.FetchAsync(identified,CancellationToken.None);
            checks["Unavailable native stage releases alternate within its preference window: "+package]
                =watch.ElapsedMilliseconds<(package=="com.kugou.android"?3000:1000)&&result.Lines.Count==4;
        }
        bool lowStarted=false;
        provider=new LyricsProvider(Area("quality-order"),new Fixture(async(uri,token)=>{
            if(uri.Host=="lrclib.net"||uri.Host=="songsearch.kugou.com"||uri.Host=="music.163.com")return None(uri);
            if(uri.AbsolutePath.EndsWith("search"))return JsonSerializer.Serialize(new{candidates=new[]{
                new{song=song.Title,singer=song.Artist,duration=180000,id="known",accesskey="x"},
                new{song=song.Title,singer=song.Artist,duration=0,id="unknown",accesskey="x"}}});
            if(uri.Query.Contains("id=unknown")){lowStarted=true;return Download(full.Replace("完整第一句","较弱候选"));}
            await Task.Delay(150,token);return Download(full);
        }));
        result=await provider.FetchAsync(song,CancellationToken.None);
        checks["Incomplete candidate evidence cannot race ahead of a stronger recording"]=!lowStarted&&result.Lines[0].Text=="完整第一句";
        string partialArea=Area("partial");
        provider=new LyricsProvider(partialArea,new Fixture((uri,_)=>Task.FromResult(uri.Host=="lrclib.net"?Library("[00:00]只有开头\n[00:10]停在这里"):None(uri))));
        using(var lookup=new LyricLookup((s,t,b,n)=>provider.FetchAsync(s,t,b,n)))
        {
            lookup.Observe(song);await lookup.PollAsync(true);
            checks["Truncated network timeline is neither resolved nor cached"]=lookup.Result.Lines.Count==0&&lookup.WillRetry&&!File.Exists(Cache(partialArea,song))
                &&lookup.Result.Outcome==LyricsOutcome.Unreliable&&lookup.Result.EmptyMessage.Contains("可靠");
        }
        provider=new LyricsProvider(Area("kugou-unreliable"),new Fixture((uri,_)=>Task.FromResult(
            uri.Host=="lrclib.net"||uri.Host=="music.163.com"||uri.Host=="songsearch.kugou.com" ? None(uri)
            : uri.AbsolutePath.EndsWith("search") ? Search() : Download("[00:00]只有开头\n[00:10]不完整"))));
        result=await provider.FetchAsync(song,CancellationToken.None);
        checks["Kugou nested candidates preserve an unreliable timeline outcome"]=result.Outcome==LyricsOutcome.Unreliable&&result.Lines.Count==0;
        provider=new LyricsProvider(Area("netease-unreliable"),new Fixture((uri,_)=>Task.FromResult(
            uri.Host!="music.163.com" ? None(uri) : uri.AbsolutePath.Contains("/search/")
                ? JsonSerializer.Serialize(new{code=200,result=new{songs=new[]{new{id=123,name=song.Title,duration=180000,artists=new[]{new{name=song.Artist}}}}}})
                : JsonSerializer.Serialize(new{code=200,lrc=new{lyric="[00:00]只有开头\n[00:10]不完整"}}))));
        result=await provider.FetchAsync(song,CancellationToken.None);
        checks["Generic NetEase candidate rejection retains unreliable status"]=result.Outcome==LyricsOutcome.Unreliable&&result.Lines.Count==0;
        provider=new LyricsProvider(Area("plain-not-instrumental"),new Fixture((uri,_)=>Task.FromResult(
            uri.Host=="lrclib.net" ? Library(instrumental:true) : uri.Host=="music.163.com"||uri.Host=="songsearch.kugou.com"
                ? None(uri) : uri.AbsolutePath.EndsWith("search") ? Search() : Download("这是演唱歌词，当前没有时间轴"))));
        result=await provider.FetchAsync(song,CancellationToken.None);
        checks["Unsynced vocal text contradicts a competing instrumental flag"]=!result.IsInstrumental&&result.Outcome==LyricsOutcome.Unsynced&&result.PlainText.Contains("演唱歌词");
        provider=new LyricsProvider(Area("unreliable-and-service"),new Fixture((uri,_)=>{
            if(uri.Host=="music.163.com")throw new HttpRequestException("forbidden",null,HttpStatusCode.Forbidden);
            return Task.FromResult(uri.Host=="lrclib.net"?Library("[00:00]只有开头\n[00:10]不完整"):None(uri));
        }));
        result=await provider.FetchAsync(song,CancellationToken.None);
        checks["Known source failure keeps priority over rejected timeline status"]=result.Outcome==LyricsOutcome.PartialFailure;
        string outsideArea=Area("outside-private");int outsideRequests=0;
        provider=new LyricsProvider(outsideArea,new Fixture((uri,_)=>{outsideRequests++;return Task.FromResult(uri.Host=="lrclib.net"?Library(full):None(uri));}));
        await File.WriteAllTextAsync(Cache(outsideArea,song),"[00:00]开头\n[03:30]越界");
        result=await provider.FetchAsync(song,CancellationToken.None,false,false);
        checks["Offline invalid private timeline is distinguished from an ordinary cache miss"]=result.Outcome==LyricsOutcome.Unreliable&&outsideRequests==0;
        result=await provider.FetchAsync(song,CancellationToken.None);
        checks["Online invalid private timeline continues to a valid alternative"]=result.Outcome==LyricsOutcome.Success&&result.Lines.Count==4&&outsideRequests>0;
        provider=new LyricsProvider(Area("candidate-after-rejection"),new Fixture((uri,_)=>Task.FromResult(
            uri.Host=="lrclib.net"||uri.Host=="music.163.com"||uri.Host=="songsearch.kugou.com" ? None(uri)
            : uri.AbsolutePath.EndsWith("search") ? JsonSerializer.Serialize(new{candidates=new[]{
                new{song=song.Title,singer=song.Artist,duration=180000,id="bad",accesskey="x"},
                new{song=song.Title,singer=song.Artist,duration=180000,id="good",accesskey="x"}}})
            : Download(uri.Query.Contains("id=bad")?"[00:00]只有开头\n[00:10]不完整":full))));
        result=await provider.FetchAsync(song,CancellationToken.None);
        checks["A rejected timeline does not terminate another valid candidate"]=result.Outcome==LyricsOutcome.Success&&result.Lines.Count==4;
        string suppliedArea=Area("provided-lifecycle");
        var supplied=song with{Source="integration",SessionId="session",TrackId="track",ProvidedLyricsTrackId="track",ProvidedLyrics=full};
        var withdrawn=supplied with{ProvidedLyrics="",ProvidedLyricsTrackId=""};
        int calls=0;
        var handler=new Fixture((uri,_)=>{calls++;return Task.FromResult(uri.Host=="lrclib.net"?Library(full.Replace("完整第一句","网络新词")):None(uri));});
        provider=new LyricsProvider(suppliedArea,handler);await provider.RememberProvidedAsync(supplied,CancellationToken.None);
        var restarted=new LyricsProvider(suppliedArea,handler);
        result=await restarted.FetchAsync(withdrawn,CancellationToken.None,false,false);
        checks["Restarted offline lookup cannot revive explicitly withdrawn provided lyrics"]=result.Lines.Count==0&&calls==0;
        result=await restarted.FetchAsync(withdrawn,CancellationToken.None);
        checks["Restarted online withdrawal revalidates rather than reviving old private or shared lyrics"]=result.Lines[0].Text=="网络新词"&&calls>0&&!result.FromProvidedLyrics;
        calls=0;result=await restarted.FetchAsync(withdrawn,CancellationToken.None,false,false);
        checks["Ordinary network cache remains usable for integrated playback without a supplied binding"]=result.Lines[0].Text=="网络新词"&&calls==0;
        await restarted.RememberProvidedAsync(supplied,CancellationToken.None);
        result=await restarted.FetchAsync(supplied with{ProvidedLyrics=""},CancellationToken.None,false,false);
        checks["Omitted payload with retained binding can read its provided cache provenance"]=result.Lines.Count==4&&result.FromProvidedLyrics;
        await File.WriteAllTextAsync(Cache(suppliedArea,supplied),full.Replace("完整第一句","被改写的词"));
        result=await restarted.FetchAsync(supplied with{ProvidedLyrics=""},CancellationToken.None,false,false);
        checks["Private cache evidence hash prevents silently reusing modified content"]=result.Lines.All(l=>l.Text!="被改写的词");
        provider=new LyricsProvider(Area("partial-service"),new Fixture((uri,_)=>{
            if(uri.Host=="music.163.com")throw new HttpRequestException("forbidden",null,HttpStatusCode.Forbidden);
            return Task.FromResult(None(uri));
        }));
        result=await provider.FetchAsync(song,CancellationToken.None);
        checks["Partial source failure has a structured status and an accurate UI message"]=result.Outcome==LyricsOutcome.PartialFailure&&result.EmptyMessage.Contains("连接失败");
        provider=new LyricsProvider(Area("all-service"),new Fixture((_,_)=>throw new HttpRequestException("forbidden",null,HttpStatusCode.Forbidden)));
        result=await provider.FetchAsync(song,CancellationToken.None);
        checks["Complete source failure is distinguished from an empty catalogue"]=result.Outcome==LyricsOutcome.ServiceFailure;
        provider=new LyricsProvider(Area("empty-catalogue"),new Fixture((uri,_)=>Task.FromResult(None(uri))));
        result=await provider.FetchAsync(song,CancellationToken.None);
        checks["Reachable empty catalogues remain normal no-match results"]=result.Outcome==LyricsOutcome.NoMatch;
        foreach(var native in new[]{song with{PackageName="com.tencent.qqmusic",MediaId="4242"},song with{PackageName="com.netease.cloudmusic",MediaId="4242"}})
        {
            bool rejected=false;
            try {await PlatformLyrics.FetchAsync(native,(_,_,_)=>Task.FromResult(JsonDocument.Parse("{\"code\":403}")),CancellationToken.None);}
            catch(HttpRequestException){rejected=true;}
            checks["Native business failure is not silently treated as missing lyrics: "+native.PackageName]=rejected;
        }
        using var cancellation=new CancellationTokenSource();
        provider=new LyricsProvider(Area("cancel"),new Fixture(async(uri,token)=>{
            if(uri.Host=="lrclib.net")return Library(instrumental:true);
            await Task.Delay(Timeout.Infinite,token);return "{}";
        }));
        var fetching=provider.FetchAsync(song,cancellation.Token);cancellation.Cancel();bool cancelled=false;
        try{await fetching;}catch(OperationCanceledException){cancelled=true;}
        checks["Caller cancellation remains observable during instrumental confirmation"]=cancelled;
        return checks;
    }
    private sealed class Fixture(Func<Uri,CancellationToken,Task<string>> response):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>
            new(HttpStatusCode.OK){Content=new StringContent(await response(request.RequestUri!,token))};
    }
}
