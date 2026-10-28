using System.Security.Cryptography;
using System.Text;
using System.Net;
namespace LyricBridge;
internal static class AlbumRegressionTests
{
    public static async Task<Dictionary<string,bool>> RunAsync(string root)
    {
        var checks=new Dictionary<string,bool>();
        var song=new MediaSnapshot {Version=1,Type="media",PackageName="album.test",Title="Same Song",Artist="Singer",DurationMs=200000,MediaId="id-one"};
        string legacy=$"{song.PackageName}\n{song.Title}\n{song.Artist}\n{song.DurationMs}\nid:{song.MediaId}";
        checks["Album-less snapshot retains its existing disk cache key"]=song.TrackKey==legacy;
        checks["Known albums separate disk cache identities"]=(song with {Album="Album A"}).TrackKey!=(song with {Album="Album B"}).TrackKey;
        var calls=new List<(MediaSnapshot Snapshot,bool Bypass)>();
        using(var lookup=new LyricLookup((s,_,bypass,_)=>{
            calls.Add((s,bypass));return Task.FromResult(new LyricsResult(new(){new(0,"lyrics "+s.Album)},"fixture"));
        }))
        {
            lookup.Observe(song);await lookup.PollAsync(true);
            bool changed=lookup.Observe(song with {Album="Album A"});
            checks["Completing an unknown album triggers recording revalidation"]=changed&&lookup.WillRetry;
            checks["Album completion keeps usable lyrics while verification runs"]=lookup.Result.Lines.Count==1;
            await lookup.PollAsync(true);
            checks["Album completion bypasses the unscoped cache and confirms the album"]=calls.Count==2&&calls.Last().Bypass&&calls.Last().Snapshot.Album=="Album A"&&!lookup.WillRetry;
            int count=calls.Count;
            changed=lookup.Observe(song with {Album=""});await lookup.PollAsync(true);
            checks["Temporary missing album with same ID retains its confirmed identity"]=!changed&&lookup.Active?.Album=="Album A"&&calls.Count==count;
            changed=lookup.Observe(song with {Album="",MediaId=""});await lookup.PollAsync(true);
            checks["Temporary missing album and ID do not weaken the active recording"]=!changed&&lookup.Active?.Album=="Album A"&&lookup.Active.MediaId=="id-one"&&calls.Count==count;
            changed=lookup.Observe(song with {Album="",MediaId="id-two"});
            checks["A new recording ID cannot inherit the previous album"]=changed&&lookup.Active?.Album==""&&lookup.Active.MediaId=="id-two"&&lookup.Result.Lines.Count==0;
        }
        calls.Clear();
        using(var lookup=new LyricLookup((s,_,bypass,_)=>{
            calls.Add((s,bypass));return Task.FromResult(new LyricsResult(new(){new(0,"lyrics "+s.Album)},"fixture"));
        }))
        {
            lookup.Observe(song with {Album="Album A"});await lookup.PollAsync(true);
            bool changed=lookup.Observe(song with {Album="Album B",MediaId=""});
            checks["Changing known albums never inherits the old recording ID"]=changed&&lookup.Active?.MediaId==""&&lookup.Active.Album=="Album B";
            checks["Changing known albums never displays the previous album cache"]=lookup.Result.Lines.Count==0;
            await lookup.PollAsync(true);
            checks["Changing albums performs a lookup for the new recording"]=calls.Count==2&&calls.Last().Snapshot.Album=="Album B"&&lookup.Result.Lines.Single().Text=="lyrics Album B";
        }
        using(var lookup=new LyricLookup((s,_)=>Task.FromResult(new LyricsResult(new(){new(0,"confirmed "+s.Album)},"fixture"))))
        {
            lookup.Observe(song with {Album="Album A"});await lookup.PollAsync(true);
            bool changed=lookup.Observe(song with {Album="Album B"});
            checks["A changed album revalidates even when the player repeats its ID"]=changed&&lookup.Result.Lines.Count==0&&lookup.WillRetry;
        }
        using(var lookup=new LyricLookup((s,_)=>Task.FromResult(new LyricsResult(new(){new(0,"confirmed "+s.Album)},"fixture"))))
        {
            lookup.Observe(song with {Album="Album A",MediaId=""});await lookup.PollAsync(true);
            bool changed=lookup.Observe(song with {Album="",MediaId="first-new-id"});
            checks["A newly supplied ID cannot borrow an album from an unidentified recording"]=changed&&lookup.Active?.Album=="";
        }
        int attempts=0;
        using(var lookup=new LyricLookup((s,_)=>Task.FromResult(++attempts==1?new LyricsResult(new(){new(0,"old usable lyrics")},"fixture"):new LyricsResult(new(),"temporary failure"))))
        {
            lookup.Observe(song);await lookup.PollAsync(true);
            lookup.Observe(song with {Album="Album A"});await lookup.PollAsync(true);
            checks["Failed album verification preserves lyrics but schedules another attempt"]=attempts==2&&lookup.Result.Lines.Count==1&&lookup.WillRetry;
        }
        string area=Path.Combine(root,"album-disk-"+Guid.NewGuid().ToString("N"));
        string currentAlbum="Album A";int requests=0;
        var provider=new LyricsProvider(area,new Fixture(uri=>{
            requests++;
            return uri.Host=="lrclib.net" ? System.Text.Json.JsonSerializer.Serialize(new[] { new { trackName="Same Song",artistName="Singer",duration=200,syncedLyrics=CompleteTimeline("[00:00]"+currentAlbum) } }) : "{}";
        }));
        var first=await provider.FetchAsync(song with {Album=currentAlbum},CancellationToken.None);
        int firstRequests=requests;currentAlbum="Album B";
        var second=await provider.FetchAsync(song with {Album=currentAlbum},CancellationToken.None);
        checks["The disk cache never substitutes a different known album"]=first.Lines.FirstOrDefault()?.Text=="Album A"&&second.Lines.FirstOrDefault()?.Text=="Album B"&&requests>firstRequests;
        var lyricDir=Path.Combine(area,"lyrics");
        var files=Directory.GetFiles(lyricDir,"*.lrc");
        checks["Both verified album cache files are preserved"]=files.Length==2;
        string legacyFile=Path.Combine(lyricDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacy)))+".lrc");
        await File.WriteAllTextAsync(legacyFile,CompleteTimeline("[00:00]legacy"));
        var old=await provider.FetchAsync(song,CancellationToken.None,false,false);
        checks["Existing album-less cache still works for older phone senders"]=old.Lines.FirstOrDefault()?.Text=="legacy"&&File.Exists(legacyFile);
        return checks;
    }
    private static string CompleteTimeline(string content) => content + "\n[02:40]测试后续歌词";

    private sealed class Fixture(Func<Uri,string> reply):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(reply(request.RequestUri!))});
    }
}
