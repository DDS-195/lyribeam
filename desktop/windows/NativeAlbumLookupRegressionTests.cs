using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace LyricBridge;

internal static class NativeAlbumLookupRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        string Area(string name) => Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
        var song = new MediaSnapshot { Version=1, Type="media", Title="验证作品", Artist="验证歌手", Album="单曲发行 (Explicit)",
            PackageName="com.kugou.android.lite", MediaId="4242", DurationMs=180000, PositionKnown=true, Source="media-session" };
        const string bodyAlbum="完整专辑 (Explicit)";
        string body=Lyrics(song.Title+" (Explicit)",song.Artist,bodyAlbum);
        string area=Area("native-album-proof");
        var native=new NativeFixture(song,body);
        var provider=new LyricsProvider(area,native);
        var result=await provider.FetchAsync(song,CancellationToken.None);
        checks["Verified native recording accepts a different release album without weakening singer and timeline checks"] =
            result.Lines.Count==3 && result.Source.Contains("编号") && native.CatalogRequests>0;
        var offline=new NativeFixture(song,body);
        var cached=await new LyricsProvider(area,offline).FetchAsync(song,CancellationToken.None,false,false);
        checks["Native album evidence survives a new provider and validates the private cache offline"] = cached.Lines.Count==3 && offline.Requests==0;
        string shared=Path.Combine(area,"recording-cache");
        checks["A relaxed native album does not become an unqualified cross-player shared cache"] =
            !Directory.Exists(shared) || Directory.GetFiles(shared,"*.json").Length==0;
        var other=await new LyricsProvider(area,offline).FetchAsync(song with{PackageName="another.player",MediaId="different"},CancellationToken.None,false,false);
        checks["Native album proof cannot authorize another player offline"] = other.Lines.Count==0 && offline.Requests==0;
        var otherRecording=await new LyricsProvider(area,offline).FetchAsync(song with{MediaId="9999"},CancellationToken.None,false,false);
        checks["Native album proof cannot authorize another recording identifier"] = otherRecording.Lines.Count==0;
        string? document=Directory.GetFiles(Path.Combine(area,"lyrics"),"*.lrc.json").SingleOrDefault();
        bool originalStored=false,noFlagShortcut=false,wrongProofRejected=false;
        if(document!=null)
        {
            string original=await File.ReadAllTextAsync(document);
            var json=JsonNode.Parse(original)!;
            originalStored=json["Content"]?.GetValue<string>()==body && json["RecordingEvidence"]!=null;
            json["NativeVerified"]=true;json.AsObject().Remove("RecordingEvidence");
            await File.WriteAllTextAsync(document,json.ToJsonString());
            var missing=await new LyricsProvider(area,offline).FetchAsync(song,CancellationToken.None,false,false);
            noFlagShortcut=missing.Lines.Count==0;
            json=JsonNode.Parse(original)!;
            if(json["RecordingEvidence"] is JsonObject proof)
            {
                proof["MediaId"]="9999";await File.WriteAllTextAsync(document,json.ToJsonString());
                var wrong=await new LyricsProvider(area,offline).FetchAsync(song,CancellationToken.None,false,false);
                wrongProofRejected=wrong.Lines.Count==0;
            }
            await File.WriteAllTextAsync(document,original);
        }
        checks["Private cache preserves original lyric headers together with explicit native evidence"] = originalStored;
        checks["A source-name NativeVerified flag alone cannot bypass album validation"] = noFlagShortcut;
        checks["Stored native evidence is checked against the requested identifier on every read"] = wrongProofRejected;

        foreach(var pair in new Dictionary<string,MediaSnapshot>{
            ["missing native identifier"]=song with{MediaId=""},
            ["unresolved native identifier"]=song with{MediaId="9999"},
            ["lookalike player package"]=song with{PackageName="com.kugou.android.fake"},
            ["unknown recording duration"]=song with{DurationMs=0}
        })
        {
            var fixture=new NativeFixture(song,body);
            var rejected=await new LyricsProvider(Area("unverified"),fixture).FetchAsync(pair.Value,CancellationToken.None);
            checks["Album mismatch is not waived for "+pair.Key] = rejected.Lines.Count==0;
        }
        foreach(long candidateDuration in new long[]{0,181500})
        {
            var rejected=await new LyricsProvider(Area("candidate-duration-evidence"),new NativeFixture(song,body,candidateDuration)).FetchAsync(song,CancellationToken.None);
            checks["Native album proof needs an independently known close candidate duration: "+candidateDuration] = rejected.Lines.Count==0;
        }
        foreach(var pair in new Dictionary<string,string>{
            ["an explicit versus clean album"]=Lyrics(song.Title,song.Artist,"完整专辑 (Clean)"),
            ["a live album"]=Lyrics(song.Title,song.Artist,"完整专辑 (Explicit) (Live)"),
            ["a remix title"]=Lyrics(song.Title+" (Remix)",song.Artist,bodyAlbum),
            ["a different singer"]=Lyrics(song.Title,"另一位歌手",bodyAlbum),
            ["a contradictory language album"]=Lyrics(song.Title,song.Artist,"完整专辑 (Explicit) (Japanese Ver.)"),
            ["a conflicting real title header"]="[ti:另一首作品]\n"+body,
            ["a timeline outside the recording"]=body.Replace("[02:30]","[03:01]")
        })
        {
            var rejected=await new LyricsProvider(Area("hard-conflict"),new NativeFixture(song,pair.Value)).FetchAsync(song,CancellationToken.None);
            checks["Exact native metadata still rejects "+pair.Key] = rejected.Lines.Count==0;
        }

        var generic=song with{PackageName="fixture.candidate.player",MediaId="",Album="候选专辑",Title="候选作品"};
        var seen=new ConcurrentDictionary<string,int>();int active=0,maxActive=0;
        var candidates=Enumerable.Range(0,20).Select(i=>new{song=generic.Title,singer=generic.Artist,duration=i<12?180000:i==12?180100:180500,id="candidate-"+i,accesskey="fixture"}).ToArray();
        var fixtureCandidates=new Fixture(async(uri,token)=>{
            if(uri.Host is not ("lyrics.kugou.com" or "krcs.kugou.com"))return Empty(uri);
            if(uri.AbsolutePath.EndsWith("search"))return JsonSerializer.Serialize(new{candidates});
            string id=Query(uri,"id");seen.AddOrUpdate(id,1,(_,count)=>count+1);
            int current=Interlocked.Increment(ref active);int previous;
            do{previous=maxActive;if(previous>=current)break;}while(Interlocked.CompareExchange(ref maxActive,current,previous)!=previous);
            try{await Task.Delay(15,token);return Download(Lyrics(id=="candidate-12"?generic.Title:"错误作品",generic.Artist,generic.Album));}
            finally{Interlocked.Decrement(ref active);}
        });
        var later=await new LyricsProvider(Area("later-candidate"),fixtureCandidates).FetchAsync(generic,CancellationToken.None);
        checks["A valid thirteenth candidate is still reached after the first twelve lyric bodies are rejected"] = later.Lines.Count==3 && seen.ContainsKey("candidate-12");
        checks["Candidate diversification keeps the existing bounded download concurrency"] = maxActive<=3;

        var boundedSeen=new ConcurrentDictionary<string,int>();
        var many=Enumerable.Range(0,30).Select(i=>new{song=generic.Title,singer=generic.Artist,duration=180000,id="bounded-"+i,accesskey="fixture"}).ToArray();
        var bounded=new Fixture((uri,_)=>{
            if(uri.Host is not ("lyrics.kugou.com" or "krcs.kugou.com"))return Task.FromResult(Empty(uri));
            if(uri.AbsolutePath.EndsWith("search"))return Task.FromResult(JsonSerializer.Serialize(new{candidates=many}));
            string id=Query(uri,"id");boundedSeen.AddOrUpdate(id,1,(_,count)=>count+1);
            return Task.FromResult(Download(Lyrics(id=="bounded-24"?generic.Title:"错误作品",generic.Artist,generic.Album)));
        });
        var boundedResult=await new LyricsProvider(Area("bounded-candidates"),bounded).FetchAsync(generic,CancellationToken.None);
        checks["Candidate continuation remains bounded to twenty unique identifiers per search"] = boundedResult.Lines.Count==0 && boundedSeen.Count<=20 && !boundedSeen.ContainsKey("bounded-24");

        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int cancelledDownloads=0;
        var cancellationSignals=new ConcurrentBag<Task>();
        var cancelling=new Fixture(async(uri,token)=>{
            if(uri.Host is not ("lyrics.kugou.com" or "krcs.kugou.com"))return Empty(uri);
            if(uri.AbsolutePath.EndsWith("search"))return JsonSerializer.Serialize(new{candidates=many});
            var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationSignals.Add(cancelled.Task);entered.TrySetResult();
            try{await Task.Delay(Timeout.Infinite,token);return "{}";}
            catch(OperationCanceledException){Interlocked.Increment(ref cancelledDownloads);cancelled.TrySetResult();throw;}
        });
        using(var cancel=new CancellationTokenSource())
        {
            var cancelledFetch=new LyricsProvider(Area("cancel-candidates"),cancelling).FetchAsync(generic,cancel.Token);
            bool started=await Task.WhenAny(entered.Task,Task.Delay(1200))==entered.Task;
            cancel.Cancel();bool observed=false;
            try{await cancelledFetch;}catch(OperationCanceledException){observed=true;}
            var allCancelled=Task.WhenAll(cancellationSignals.ToArray());
            bool callbacksArrived=await Task.WhenAny(allCancelled,Task.Delay(1200))==allCancelled;
            checks["Caller cancellation still interrupts all pending candidate downloads"] = started && observed && callbacksArrived
                && cancellationSignals.Count>0 && cancelledDownloads==cancellationSignals.Count;
        }
        return checks;
    }

    private static string Lyrics(string title,string artist,string album)=>"[ti:"+title+"]\n[ar:"+artist+"]\n[al:"+album+"]\n[00:00]验证首句\n[01:00]验证中句\n[02:30]验证末句";
    private static string Download(string content)=>JsonSerializer.Serialize(new{content=Convert.ToBase64String(Encoding.UTF8.GetBytes(content))});
    private static string Empty(Uri uri)=>uri.Host=="lrclib.net"?"[]":uri.Host=="music.163.com"?"{\"code\":200,\"result\":{\"songs\":[]}}":"{}";
    private static string Query(Uri uri,string key)=>Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&').FirstOrDefault(part=>part.StartsWith(key+"=",StringComparison.Ordinal))?[(key.Length+1)..]??"");
    private sealed class NativeFixture(MediaSnapshot catalogue,string content,long? candidateDurationMs=null):HttpMessageHandler
    {
        public int Requests,CatalogRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Interlocked.Increment(ref Requests);var uri=request.RequestUri!;string response;
            if(uri.Host=="songsearch.kugou.com")
            {
                Interlocked.Increment(ref CatalogRequests);
                response=JsonSerializer.Serialize(new{data=new{lists=new[]{new{ID="4242",MixSongID="4242",SongName=catalogue.Title+" (Explicit)",SingerName=catalogue.Artist,AlbumName=catalogue.Album,Duration=catalogue.DurationMs/1000.0,FileHash="0123456789ABCDEF0123456789ABCDEF"}}}});
            }
            else if(uri.Host is "lyrics.kugou.com" or "krcs.kugou.com")
                response=uri.AbsolutePath.EndsWith("search")?JsonSerializer.Serialize(new{candidates=new[]{new{song=catalogue.Title,singer=catalogue.Artist,duration=candidateDurationMs??catalogue.DurationMs,id="native-fixture",accesskey="fixture"}}}):Download(content);
            else response=Empty(uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(response)});
        }
    }
    private sealed class Fixture(Func<Uri,CancellationToken,Task<string>> response):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>
            new(HttpStatusCode.OK){Content=new StringContent(await response(request.RequestUri!,token))};
    }
}
