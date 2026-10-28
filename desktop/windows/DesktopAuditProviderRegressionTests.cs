using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
namespace LyricBridge;
internal static class DesktopAuditProviderRegressionTests
{
    public static async Task<Dictionary<string,bool>> RunAsync(string root)
    {
        var checks=new Dictionary<string,bool>();
        var song=new MediaSnapshot{Version=1,Type="media",PackageName="audit.player",Title="测试歌",Artist="测试歌手",DurationMs=180000,PositionKnown=true};
        string Full(string first)=>"[00:00]"+first+"\n[01:00]第二句\n[02:00]第三句";
        string Area(string name){string path=Path.Combine(root,name+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(path,"lyrics"));return path;}
        string Cache(string area,MediaSnapshot s)=>Path.Combine(area,"lyrics",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.TrackKey)))+".lrc");
        string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        string None(Uri uri)=>uri.Host=="lrclib.net"?"[]":uri.Host=="music.163.com"?"{\"code\":200,\"result\":{\"songs\":[]}}":"{}";
        string Library(string content="",bool instrumental=false)=>JsonSerializer.Serialize(new[]{new{trackName=song.Title,artistName=song.Artist,duration=180,syncedLyrics=content,instrumental}});
        LyricsProvider Offline(string area)=>new(area,new Fixture((_,_)=>throw new InvalidOperationException("Offline path issued HTTP")));
        foreach(bool lockEvidence in new[]{true,false})
        {
            string area=Area(lockEvidence?"locked-json":"locked-mirror"),current=Full("原来的歌词");
            var provider=new LyricsProvider(area,new Fixture((uri,_)=>Task.FromResult(uri.Host=="lrclib.net"?Library(current):None(uri))));
            await provider.FetchAsync(song,CancellationToken.None);
            string path=Cache(area,song),oldMirror=await File.ReadAllTextAsync(path),oldJson=await File.ReadAllTextAsync(path+".json");current=Full("新下载歌词");
            LyricsResult downloaded;
            using(var locked=new FileStream(path+(lockEvidence?".json":""),FileMode.Open,FileAccess.Read,FileShare.Read))
                downloaded=await provider.FetchAsync(song,CancellationToken.None,true,true);
            var offline=await Offline(area).FetchAsync(song,CancellationToken.None,false,false);
            checks[(lockEvidence?"Locked authoritative document":"Locked compatibility mirror")+" does not discard downloaded lyrics"]=downloaded.Lines.FirstOrDefault()?.Text=="新下载歌词";
            checks[(lockEvidence?"Failed authoritative commit preserves entire previous pair":"Committed authoritative content survives a locked mirror")]=lockEvidence
                ?await File.ReadAllTextAsync(path)==oldMirror&&await File.ReadAllTextAsync(path+".json")==oldJson&&offline.Lines.FirstOrDefault()?.Text=="原来的歌词"
                :await File.ReadAllTextAsync(path)==oldMirror&&offline.Lines.FirstOrDefault()?.Text=="新下载歌词";
            checks["Cache temporary files are cleaned after "+(lockEvidence?"JSON":"mirror")+" failure"]=Directory.GetFiles(Path.Combine(area,"lyrics"),"*.tmp*").Length==0;
        }
        string schema2Area=Area("schema2-no-mirror"),schema2Path=Cache(schema2Area,song),text=Full("自包含缓存");
        async Task WriteSchema2(string content,string? hash=null,string kind="network")=>await File.WriteAllTextAsync(schema2Path+".json",JsonSerializer.Serialize(new{Schema=2,Kind=kind,Hash=hash??Hash(content),Content=content}));
        await WriteSchema2(text);
        var result=await Offline(schema2Area).FetchAsync(song,CancellationToken.None,false,false);
        checks["Schema2 is self contained without a compatibility mirror"]=result.Lines.FirstOrDefault()?.Text=="自包含缓存";
        await File.WriteAllTextAsync(schema2Path,Full("不该回退的镜像"));
        await WriteSchema2(text,"BAD");result=await Offline(schema2Area).FetchAsync(song,CancellationToken.None,false,false);
        checks["Corrupt schema2 hash cannot fall back to an unchecked mirror"]=result.Lines.Count==0;
        await WriteSchema2(text+"被改写",Hash(text));result=await Offline(schema2Area).FetchAsync(song,CancellationToken.None,false,false);
        checks["Tampered schema2 content is rejected"]=result.Lines.Count==0;
        await File.WriteAllTextAsync(schema2Path+".json","{");result=await Offline(schema2Area).FetchAsync(song,CancellationToken.None,false,false);
        checks["Invalid authoritative JSON cannot fall back to legacy mirror"]=result.Lines.Count==0;
        await WriteSchema2(text,kind:"unknown");result=await Offline(schema2Area).FetchAsync(song,CancellationToken.None,false,false);
        checks["Unknown cache provenance cannot reuse a mirror"]=result.Lines.Count==0;
        string legacyArea=Area("legacy"),legacyPath=Cache(legacyArea,song);await File.WriteAllTextAsync(legacyPath,text);
        result=await Offline(legacyArea).FetchAsync(song,CancellationToken.None,false,false);
        checks["Legacy raw cache remains readable"]=result.Lines.FirstOrDefault()?.Text=="自包含缓存";
        await File.WriteAllTextAsync(legacyPath+".json",JsonSerializer.Serialize(new{Schema=1,Kind="network",Hash=Hash(text)}));
        result=await Offline(legacyArea).FetchAsync(song,CancellationToken.None,false,false);
        checks["Schema1 cache remains readable with a valid matching hash"]=result.Lines.FirstOrDefault()?.Text=="自包含缓存";
        string boundArea=Area("provided-provenance");var supplied=song with{Source="integration",SessionId="session",TrackId="track",ProvidedLyricsTrackId="track",ProvidedLyrics=text};
        await Offline(boundArea).RememberProvidedAsync(supplied,CancellationToken.None);
        result=await Offline(boundArea).FetchAsync(supplied with{ProvidedLyrics=""},CancellationToken.None,false,false);
        checks["Schema2 retains the supplied lyric provenance"]=result.Lines.Count>0&&result.FromProvidedLyrics;
        result=await Offline(boundArea).FetchAsync(supplied with{ProvidedLyrics="",ProvidedLyricsTrackId=""},CancellationToken.None,false,false);
        checks["Schema2 cannot revive explicitly withdrawn supplied lyrics"]=result.Lines.Count==0;
        string escapedArea=Area("escaped-document"),escapedPath=Cache(escapedArea,song);
        string escapedText=Full("中文歌词")+"\n"+new string('中',262200);
        await File.WriteAllTextAsync(escapedPath+".json",JsonSerializer.Serialize(new{Schema=2,Kind="network",Hash=Hash(escapedText),Content=escapedText}));
        result=await Offline(escapedArea).FetchAsync(song,CancellationToken.None,false,false);
        checks["Bounded escaped CJK JSON is not rejected by a small metadata limit"]=result.Lines.Count>0;
        foreach(bool native in new[]{false,true,true,true})
        {
            string area=Area("priority-plain");
            var provider=new LyricsProvider(area,new Fixture(async(uri,token)=>{
                if(uri.Host=="lrclib.net"){await Task.Delay(10,token);return Library(instrumental:true);}
                if(uri.Host=="c.y.qq.com"){await Task.Delay(350,token);return "{\"code\":0,\"data\":[]}";}
                if(uri.Host=="songsearch.kugou.com")return "{\"data\":{\"lists\":[]}}";
                if(uri.Host=="music.163.com")return None(uri);
                if(uri.AbsolutePath.EndsWith("search"))return JsonSerializer.Serialize(new{candidates=new[]{new{song=song.Title,singer=song.Artist,duration=180000,id="k",accesskey="x"}}});
                return JsonSerializer.Serialize(new{content=Convert.ToBase64String(Encoding.UTF8.GetBytes("演唱歌词第一句\n演唱歌词第二句"))});
            }));
            result=await OnDispatcher(()=>provider.FetchAsync(song with{PackageName="com.tencent.qqmusic",MediaId=native?"4242":""},CancellationToken.None));
            checks["STA dispatcher preserves vocal evidence across native priority gate "+(native?checks.Count.ToString():"no-id-control")]=!result.IsInstrumental&&result.Outcome==LyricsOutcome.Unsynced&&result.PlainText.Contains("演唱歌词");
        }
        var syncedProvider=new LyricsProvider(Area("priority-synced"),new Fixture(async(uri,token)=>{
            if(uri.Host=="lrclib.net"){await Task.Delay(10,token);return Library(Full("有效同步歌词"));}
            if(uri.Host=="c.y.qq.com"){await Task.Delay(350,token);return "{\"code\":0,\"data\":[]}";}
            if(uri.Host=="music.163.com"||uri.Host=="songsearch.kugou.com")return None(uri);
            if(uri.AbsolutePath.EndsWith("search"))return JsonSerializer.Serialize(new{candidates=new[]{new{song=song.Title,singer=song.Artist,duration=180000,id="k",accesskey="x"}}});
            return JsonSerializer.Serialize(new{content=Convert.ToBase64String(Encoding.UTF8.GetBytes("演唱歌词无时间轴"))});
        }));
        result=await OnDispatcher(()=>syncedProvider.FetchAsync(song with{PackageName="com.tencent.qqmusic",MediaId="4242"},CancellationToken.None));
        checks["Retained plain evidence never prevents a valid synced alternative"]=result.Outcome==LyricsOutcome.Success&&result.Lines.FirstOrDefault()?.Text=="有效同步歌词";
        return checks;
    }
    private static Task<T> OnDispatcher<T>(Func<Task<T>> work)
    {
        var completion=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{
            var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async()=>{try{completion.TrySetResult(await work());}catch(Exception error){completion.TrySetException(error);}finally{dispatcher.InvokeShutdown();}}));Dispatcher.Run();
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
    private sealed class Fixture(Func<Uri,CancellationToken,Task<string>> response):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>new(HttpStatusCode.OK){Content=new StringContent(await response(request.RequestUri!,token))};
    }
}
