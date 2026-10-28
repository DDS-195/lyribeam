using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace LyricBridge;
internal static class QueryFixRegressionTests
{
 public static async Task<Dictionary<string,bool>> RunAsync(string root)
 {
  var checks=new Dictionary<string,bool>();
  var song=new MediaSnapshot {Type="media",PackageName="com.kugou.android.lite",Title="测试作品",Artist="测试歌手",Album="日文专辑",DurationMs=180000,PositionKnown=true};
  const string full="[00:00]第一句\n[00:45]第二句\n[01:30]第三句\n[02:40]第四句";
  string Area(string key)=>Path.Combine(root,key+Guid.NewGuid().ToString("N"));
  string Library(string album)=>JsonSerializer.Serialize(new[]{new{trackName=song.Title,artistName=song.Artist,albumName=album,duration=180,syncedLyrics=full,instrumental=false}});
  string Empty(Uri uri)=>uri.Host=="lrclib.net"?"[]":uri.Host=="music.163.com"?"{\"code\":200,\"result\":{\"songs\":[]}}":"{}";
  foreach(string package in new[]{"custom.player","com.netease.cloudmusic"}) {
   var provider=new LyricsProvider(Area("conflicting-album"),new Fixture((uri,_)=>Task.FromResult(uri.Host=="lrclib.net"?Library("韩文专辑"):Empty(uri))));
   var result=await provider.FetchAsync(song with{PackageName=package},CancellationToken.None);
   checks["Headerless LRCLIB cannot hide a conflicting candidate album: "+package]=result.Lines.Count==0;
  }
  int nativeDownloads=0;
  string nativeArea=Area("invalid-id-priority");
  var native=new LyricsProvider(nativeArea,new Fixture(async(uri,token)=>{
   if(uri.Host=="lrclib.net"){await Task.Delay(30,token);return Library(song.Album);}
   if(uri.Host=="songsearch.kugou.com")return "{\"data\":{\"lists\":[]}}";
   if(uri.Host=="lyrics.kugou.com"&&uri.AbsolutePath.EndsWith("search")){await Task.Delay(200,token);return JsonSerializer.Serialize(new{candidates=new[]{new{song=song.Title,singer=song.Artist,duration=180000,id="a",accesskey="x"}}});}
   if(uri.Host=="lyrics.kugou.com"){nativeDownloads++;return JsonSerializer.Serialize(new{content=Convert.ToBase64String(Encoding.UTF8.GetBytes(full))});}
   return Empty(uri);
  }));
  var found=await native.FetchAsync(song with{MediaId="4242"},CancellationToken.None);
  checks["An invalid Kugou ID retains the native metadata preference window"]=found.Source.Contains("酷狗")&&nativeDownloads>0;
  int extraNativeRequests=0;
  var strongCache=new LyricsProvider(nativeArea,new Fixture((uri,_)=>{extraNativeRequests++;return Task.FromResult(Empty(uri));}));
  var strongCached=await strongCache.FetchAsync(song with{MediaId="4242"},CancellationToken.None);
  checks["Current native disk evidence stays immediate without an unnecessary source review"]=strongCached.Lines.Count==4&&!strongCached.NeedsSourceReview&&extraNativeRequests==0;
  string legacy=Area("legacy-cache");Directory.CreateDirectory(Path.Combine(legacy,"lyrics"));
  string cache=Path.Combine(legacy,"lyrics",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(song.TrackKey)))+".lrc");
  await File.WriteAllTextAsync(cache,full);
  int requests=0;
  var cached=new LyricsProvider(legacy,new Fixture((uri,_)=>{requests++;return Task.FromResult(Empty(uri));}));
  found=await cached.FetchAsync(song,CancellationToken.None);
  checks["A legacy cache displays immediately and asks for bounded native review"]=found.Lines.Count==4&&found.NeedsSourceReview&&requests==0;
  string fallback=Area("fallback-cache");
  cached=new LyricsProvider(fallback,new Fixture((uri,_)=>Task.FromResult(uri.Host=="lrclib.net"?Library(song.Album):Empty(uri))));
  found=await cached.FetchAsync(song,CancellationToken.None);
  checks["A new cross-platform fallback requests a one-time source review"]=found.Source=="LRCLIB"&&found.NeedsSourceReview;
  requests=0;cached=new LyricsProvider(fallback,new Fixture((uri,_)=>{requests++;return Task.FromResult(Empty(uri));}));
  found=await cached.FetchAsync(song,CancellationToken.None);
  checks["Recently checked fallback disk evidence respects its review cooldown"]=found.Lines.Count==4&&!found.NeedsSourceReview&&requests==0;
  string evidence=Directory.GetFiles(Path.Combine(fallback,"lyrics"),"*.json").Single();
  var json=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(await File.ReadAllTextAsync(evidence))!;
  var rewritten=json.ToDictionary(pair=>pair.Key,pair=>(object)pair.Value);rewritten["CheckedAtUtc"]=DateTimeOffset.UtcNow.AddHours(-7);
  await File.WriteAllTextAsync(evidence,JsonSerializer.Serialize(rewritten));
  found=await cached.FetchAsync(song,CancellationToken.None);
  checks["An aged fallback cache can request another source review without blocking display"]=found.Lines.Count==4&&found.NeedsSourceReview&&requests==0;
  string integrated=Area("cache-to-native");var nativeSong=song with{PackageName="com.netease.cloudmusic"};
  Directory.CreateDirectory(Path.Combine(integrated,"lyrics"));
  string legacyNative=Path.Combine(integrated,"lyrics",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nativeSong.TrackKey)))+".lrc");
  await File.WriteAllTextAsync(legacyNative,full);
  int integratedCalls=0;
  var actualProvider=new LyricsProvider(integrated,new Fixture((uri,_)=>{
   integratedCalls++;
   if(uri.Host=="music.163.com"&&uri.AbsolutePath.Contains("/search/"))return Task.FromResult(JsonSerializer.Serialize(new{code=200,result=new{songs=new[]{new{id=900100,name=song.Title,duration=180000,artists=new[]{new{name=song.Artist}},album=new{name=song.Album}}}}}));
   if(uri.Host=="music.163.com"&&uri.AbsolutePath.Contains("lyric"))return Task.FromResult(JsonSerializer.Serialize(new{code=200,lrc=new{lyric=full.Replace("第一句","原平台确认第一句")}}));
   return Task.FromResult(Empty(uri));
  }));
  double now=0;
  using(var lookup=new LyricLookup((s,t,b,n)=>actualProvider.FetchAsync(s,t,b,n),()=>now)) {
   lookup.Observe(nativeSong);await lookup.PollAsync(true);
   bool immediate=lookup.Result.Source=="本地缓存"&&integratedCalls==0&&lookup.WillRetry;
   now=5;await lookup.PollAsync(true);
   checks["Legacy display upgrades through the real provider and persists native evidence"] = immediate
    &&lookup.Result.Source.Contains("网易云")&&lookup.Result.Lines[0].Text=="原平台确认第一句"&&!lookup.WillRetry;
   var confirmed=await actualProvider.FetchAsync(nativeSong,CancellationToken.None);
   checks["A successfully upgraded native cache does not request another review"] = confirmed.Source=="本地缓存"&&!confirmed.NeedsSourceReview;
  }
  return checks;
 }
 private sealed class Fixture(Func<Uri,CancellationToken,Task<string>> response):HttpMessageHandler
 {
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>
   new(HttpStatusCode.OK){Content=new StringContent(await response(request.RequestUri!,token))};
 }
}
