using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace LyricBridge;

internal static class RecordingDocumentRegressionTests
{
    public static async Task<Dictionary<string,bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string,bool>();
        var song = new MediaSnapshot { Type="media",PackageName="com.kugou.android.lite",MediaId="4242",
            Title="共用验证作品",Artist="验证歌手",Album="Single Release (Explicit)",DurationMs=180000,Source="media-session" };
        string Body(string album) => "[ti:共用验证作品]\n[ar:验证歌手]\n[al:"+album+"]\n[00:00]共用首句\n[01:00]中间句\n[02:30]末句";
        string strict=Body(song.Album), relaxed=Body("Full Album");
        var native=RecordingEvidence.Native("kugou",song,"id:4242",song.Title,song.Artist,song.Album,180000,new string('a',32));
        checks["A rich document rejects provider and proof from different libraries"] =
            new LyricDocument(relaxed,"QQ音乐","qq",native).Validate(song).Lines.Count==0;
        foreach(string provider in new[]{"kugou","qq","netease","lrclib"})
        {
            var current = song with{ PackageName="fixture.small.player", MediaId="opaque-local-id" };
            var proof=RecordingEvidence.Catalog(provider,current,"id:catalog",song.Title,song.Artist,song.Album,180000);
            var accepted=new LyricDocument(strict,"候选来源",provider,proof).Validate(current);
            checks[provider+": common metadata evidence validates a strict document from a small player"] = accepted.Lines.Count==3&&accepted.Provider==provider;
            checks[provider+": metadata evidence cannot grant native release-album privileges"] =
                new LyricDocument(relaxed,"候选来源",provider,proof).Validate(current).Lines.Count==0;
            checks[provider+": catalogue proof cannot follow a different opaque player ID"] =
                new LyricDocument(strict,"候选来源",provider,proof).Validate(current with{MediaId="another-id"}).Lines.Count==0;
        }
        foreach(var proof in new[] {native with{OriginSource=null!}, native with{Provider=null!},native with{Basis=null!},
            native with{CatalogId=null!}, native with{Album=null!}, native with{FileHash=null!}})
            checks["Malformed proof fails safely: "+checks.Count]=!proof.Matches(song);

        // Literal schema-4 JSON contains no new provider fields: this is a true migration check.
        string data=Path.Combine(root,"legacy-"+Guid.NewGuid().ToString("N"));
        var lyrics=new LyricsProvider(data);
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(song.TrackKey)));
        string path=Path.Combine(data,"lyrics",key+".lrc.json");
        var old=new { Schema=4,Kind="network",Hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relaxed))),
            Content=relaxed,Strategy=1,NativeVerified=true,CheckedAtUtc=DateTimeOffset.UtcNow,
            RecordingEvidence=new { native.PackageName,native.MediaId,native.FileHash,native.Title,native.Artist,native.Album,native.DurationMs } };
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(old));
        var migrated=await lyrics.FetchAsync(song,CancellationToken.None,false,false);
        checks["An actual schema-4 Kugou proof remains usable offline after migration"] = migrated.Lines.Count==3;
        var document=JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["Schema"]=5;document["Provider"]="kugou";document["Strategy"]=2;
        await File.WriteAllTextAsync(path,document.ToJsonString());
        var ambiguous=await lyrics.FetchAsync(song,CancellationToken.None,false,false);
        checks["Schema-5 cannot silently infer missing evidence namespace fields from legacy defaults"] = ambiguous.Lines.Count==0;
        return checks;
    }
}
