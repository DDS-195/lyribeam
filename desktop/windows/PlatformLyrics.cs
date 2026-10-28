using System.Text.Json;
using System.Text.RegularExpressions;

namespace LyricBridge;

// These optional public web endpoints require no account or relay. Their responses are
// still checked against the active recording before the usual LRC validation runs.
internal static class PlatformLyrics
{
    private sealed record Identifier(string Platform, string Value, bool Numeric);
    private static readonly Regex Number = new(@"^[1-9][0-9]{0,19}$", RegexOptions.CultureInvariant);
    private static readonly Regex Mid = new(@"^[A-Za-z0-9]{14}$", RegexOptions.CultureInvariant);

    public static bool Supports(MediaSnapshot snapshot) => Identify(snapshot) != null;
    public static bool SupportsSearch(MediaSnapshot snapshot) => snapshot.PackageName is "com.tencent.qqmusic" or "com.netease.cloudmusic";
    // Tuple wrappers remain for older callers; production lookup uses the typed documents.
    public static async Task<(string, string)> SearchAsync(MediaSnapshot snapshot,
        Func<string, string?, CancellationToken, Task<JsonDocument>> get, CancellationToken token, Action<string>? diagnostic = null) =>
        Legacy(await SearchDocumentAsync(snapshot, get, token, diagnostic));
    public static Task<LyricDocument> SearchDocumentAsync(MediaSnapshot snapshot,
        Func<string, string?, CancellationToken, Task<JsonDocument>> get, CancellationToken token, Action<string>? diagnostic = null) =>
        snapshot.PackageName switch {
            "com.tencent.qqmusic" => SearchQqDocumentAsync(snapshot, get, token, diagnostic),
            "com.netease.cloudmusic" => SearchNeteaseDocumentAsync(snapshot, get, token, diagnostic),
            _ => Task.FromResult(new LyricDocument("", "", ""))
        };
    private static (string, string) Legacy(LyricDocument document) => (document.Content, document.Source);
    internal static string NativeCatalogId(MediaSnapshot snapshot)
    {
        var id = Identify(snapshot);
        return id == null ? "" : (id.Numeric ? "id:" : "mid:") + id.Value;
    }

    private static Identifier? Identify(MediaSnapshot snapshot)
    {
        string platform = snapshot.PackageName switch {
            "com.netease.cloudmusic" => "netease",
            "com.tencent.qqmusic" => "qq",
            _ => ""
        };
        if (platform.Length == 0) return null;
        string value = snapshot.MediaId.Trim();
        if (value.Length == 0 || value.Length > 256) return null;
        if (Number.IsMatch(value)) return new(platform, value, true);
        if (platform == "qq" && Mid.IsMatch(value)) return new(platform, value, false);

        // Only a complete official song URL is recognized. Do not extract arbitrary
        // digits from content URIs, filenames, opaque media IDs, or another platform.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
            return null;
        if (platform == "netease" && uri.Host.Equals("music.163.com", StringComparison.OrdinalIgnoreCase))
        {
            string path = uri.AbsolutePath, query = uri.Query;
            if (uri.Fragment.StartsWith("#/song?", StringComparison.Ordinal))
            { path = "/song"; query = uri.Fragment["#/song".Length..]; }
            if (path is not ("/song" or "/m/song")) return null;
            string? id = Parameter(query, "id");
            return id != null && Number.IsMatch(id) ? new(platform, id, true) : null;
        }
        if (platform == "qq" && uri.Host.Equals("y.qq.com", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(uri.AbsolutePath, @"^/n/ryqq/songDetail/([A-Za-z0-9]{14})/?$",
                RegexOptions.CultureInvariant);
            if (match.Success) return new(platform, match.Groups[1].Value, false);
        }
        return null;
    }

    private static string? Parameter(string query, string key)
    {
        string? found = null;
        foreach (string field in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = field.IndexOf('=');
            if (split < 0 || Uri.UnescapeDataString(field[..split]) != key) continue;
            if (found != null) return null; // Ambiguous repeated IDs are not trusted.
            found = Uri.UnescapeDataString(field[(split + 1)..]);
        }
        return found;
    }

    public static async Task<(string, string)> FetchAsync(MediaSnapshot snapshot,
        Func<string, string?, CancellationToken, Task<JsonDocument>> get, CancellationToken token) =>
        Legacy(await FetchDocumentAsync(snapshot, get, token));
    public static async Task<LyricDocument> FetchDocumentAsync(MediaSnapshot snapshot,
        Func<string, string?, CancellationToken, Task<JsonDocument>> get, CancellationToken token, Action<string>? diagnostic = null)
    {
        var identifier = Identify(snapshot);
        if (identifier == null) return new("", "", "");
        return identifier.Platform == "netease"
            ? await NeteaseAsync(snapshot, identifier, get, token, diagnostic)
            : await QqAsync(snapshot, identifier, get, token, diagnostic);
    }

    public static async Task<(string, string)> SearchNeteaseAsync(MediaSnapshot snapshot,
        Func<string, string?, CancellationToken, Task<JsonDocument>> get, CancellationToken token, Action<string>? diagnostic = null) =>
        Legacy(await SearchNeteaseDocumentAsync(snapshot, get, token, diagnostic));
    public static async Task<LyricDocument> SearchNeteaseDocumentAsync(MediaSnapshot snapshot,
        Func<string, string?, CancellationToken, Task<JsonDocument>> get, CancellationToken token, Action<string>? diagnostic = null)
    {
        const string source = "网易云音乐 · 歌曲信息匹配";
        const string referer = "https://music.163.com/";
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(snapshot.Title)) return new("", source, "netease");
        string title = Regex.Replace(ChineseText.Simplify(snapshot.Title),
            @"[\(\[（【]\s*(?:original(?:\s+version)?|原版|正式版)\s*[\)\]）】]", "", RegexOptions.IgnoreCase).Trim();
        string keyword = (ChineseText.Simplify(snapshot.Artist).Trim() + " " + title).Trim();
        using var search = await get("https://music.163.com/api/search/get?s=" + Uri.EscapeDataString(keyword)
            + "&type=1&limit=12&offset=0", referer, token);
        var root = search.RootElement;
        string code = Text(root, "code");
        if (code.Length > 0 && code != "200") throw new HttpRequestException("NetEase search rejected request: code=" + code);
        if (code == "200" && root.TryGetProperty("result", out var searchResult)
            && searchResult.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new HttpRequestException("NetEase search returned an unreadable result document");
        if (code != "200" || !Object(root, "result", out var result) || !Array(result, "songs", out var songs))
        {
            diagnostic?.Invoke("netease-search: candidates=0; matched=0");
            return new("", source, "netease");
        }
        int rejectedArtist = 0, rejectedDuration = 0, rejectedTitle = 0, rejectedId = 0, rejectedAlbum = 0;
        var considered = songs.EnumerateArray().Take(12).ToList();
        var matches = new List<(string Id, double Score, double Difference, RecordingEvidence Proof)>();
        foreach (var song in considered)
        {
            string artist = Artists(song, "artists");
            if (artist.Length == 0) artist = Artists(song, "ar");
            double duration = Milliseconds(song, "duration");
            if (duration <= 0) duration = Milliseconds(song, "dt");
            double score = LyricsProvider.Score(snapshot, Text(song, "name"), artist, duration);
            var proof = RecordingEvidence.Catalog("netease", snapshot, "id:" + Text(song, "id"),
                Text(song, "name"), artist, Album(song), SafeDuration(duration));
            string rejection = CandidateRejection(snapshot, Text(song, "name"), artist, Album(song), duration);
            if (rejection.Length == 0 && !Number.IsMatch(Text(song, "id"))) rejection = "identifier-invalid";
            if (rejection.Length > 0)
            {
                if (rejection == "artist-conflict") rejectedArtist++;
                else if (rejection == "candidate-duration-conflict") rejectedDuration++;
                else if (rejection == "title-version-conflict") rejectedTitle++;
                else if (rejection == "catalog-album-conflict") rejectedAlbum++;
                else rejectedId++;
                CandidateDiagnostic.Report(snapshot, new("", source, "netease", proof), rejection, false, diagnostic);
                continue;
            }
            matches.Add((Text(song, "id"), score, Math.Abs(duration - snapshot.DurationMs), proof));
        }
        var matched = matches.OrderByDescending(song => song.Score).ThenBy(song => song.Difference)
            .DistinctBy(song => song.Id).Take(3).ToList();
        diagnostic?.Invoke($"netease-search: candidates={considered.Count}; matched={matches.Count}; reject-artist={rejectedArtist}; reject-duration={rejectedDuration}; reject-title-version={rejectedTitle}; reject-id={rejectedId}; reject-album={rejectedAlbum}");
        Exception? failure = null; bool lyricReachable = false, unreliable = false; string plain = "";
        foreach (var candidate in matched)
        {
            try
            {
                using var response = await get("https://music.163.com/api/song/lyric?id=" + candidate.Id
                    + "&lv=-1&kv=-1&tv=-1", referer, token);
                root = response.RootElement;
                code = Text(root, "code");
                if (code.Length > 0 && code != "200") throw new HttpRequestException("NetEase lyric request rejected: code=" + code);
                lyricReachable = true;
                if (code != "200" || !Object(root, "lrc", out var lrc)) continue;
                string content = BoundedLyrics(Text(lrc, "lyric"));
                var document = Document(content, source, "netease", candidate.Proof, snapshot);
                if (document.Validate(snapshot, diagnostic).Lines.Count > 0) return document;
                if (Lrc.Parse(content).Count > 0) unreliable = true;
                else if (plain.Length == 0) plain = content;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or JsonException) { failure = error; }
        }
        if (failure != null && (!lyricReachable || unreliable) && plain.Length == 0)
            throw new HttpRequestException("NetEase matched lyric downloads failed", failure);
        if (plain.Length == 0 && unreliable) throw new UnreliableLyricsException();
        return new(plain, source, "netease");
    }

    public static async Task<(string, string)> SearchQqAsync(MediaSnapshot snapshot,
        Func<string, string?, CancellationToken, Task<JsonDocument>> get, CancellationToken token, Action<string>? diagnostic = null) =>
        Legacy(await SearchQqDocumentAsync(snapshot, get, token, diagnostic));
    public static async Task<LyricDocument> SearchQqDocumentAsync(MediaSnapshot snapshot,
        Func<string, string?, CancellationToken, Task<JsonDocument>> get, CancellationToken token, Action<string>? diagnostic = null)
    {
        const string source = "QQ音乐 · 歌曲信息匹配";
        const string referer = "https://y.qq.com/";
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(snapshot.Title)) return new("", source, "qq");
        string keyword = (ChineseText.Simplify(snapshot.Artist).Trim() + " " + ChineseText.Simplify(snapshot.Title).Trim()).Trim();
        // new_json=1 silently empties this public endpoint's list. Use its original
        // JSON schema and request no account cookies or playback credentials.
        using var search = await get("https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w=" + Uri.EscapeDataString(keyword)
            + "&n=12&p=1&t=0&format=json&ct=24&qqmusic_ver=1298&remoteplace=txt.yqq.song"
            + "&aggr=1&cr=1&catZhida=1&platform=yqq.json&g_tk=5381&loginUin=0&hostUin=0"
            + "&inCharset=utf8&outCharset=utf-8&notice=0&needNewCode=0", referer, token);
        var root = search.RootElement;
        CheckCode(root, "code", "0", "QQ search");
        if (Text(root, "code") != "0" || !Object(root, "data", out var data)
            || !Object(data, "song", out var songs) || !Array(songs, "list", out var rows))
            return new("", source, "qq");
        var matches = new List<(string Id, string Mid, double Score, double Difference, RecordingEvidence Proof)>();
        int considered = 0, rejected = 0;
        foreach (var song in rows.EnumerateArray().Take(12))
        {
            considered++;
            string title = Text(song, "songname");
            if (title.Length == 0) title = Text(song, "title");
            string artist = Artists(song, "singer");
            double duration = Milliseconds(song, "interval") * 1000;
            double score = LyricsProvider.Score(snapshot, title, artist, duration);
            string id = Text(song, "songid"), mid = Text(song, "songmid");
            var proof = RecordingEvidence.Catalog("qq", snapshot, Number.IsMatch(id) ? "id:" + id : "mid:" + mid,
                title, artist, Album(song), SafeDuration(duration));
            string rejection = CandidateRejection(snapshot, title, artist, Album(song), duration);
            if (rejection.Length == 0 && !Number.IsMatch(id) && !Mid.IsMatch(mid)) rejection = "identifier-invalid";
            if (rejection.Length > 0)
            {
                rejected++;
                CandidateDiagnostic.Report(snapshot, new("", source, "qq", proof), rejection, false, diagnostic);
                continue;
            }
            matches.Add((id, mid, score, Math.Abs(duration - snapshot.DurationMs), proof));
        }
        diagnostic?.Invoke($"qq-search: candidates={considered}; matched={matches.Count}; rejected={rejected}");
        bool reachable = false, unreliable = false; string plain = ""; Exception? failure = null;
        foreach (var candidate in matches.OrderByDescending(item => item.Score).ThenBy(item => item.Difference)
            .DistinctBy(item => Number.IsMatch(item.Id) ? "id:" + item.Id : "mid:" + item.Mid).Take(3))
        {
            try
            {
                string identifier = Number.IsMatch(candidate.Id) ? "musicid=" + candidate.Id : "songmid=" + candidate.Mid;
                using var response = await get("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?" + identifier
                    + "&format=json&nobase64=1&platform=yqq.json&g_tk=5381", referer, token);
                root = response.RootElement;
                CheckCode(root, "code", "0", "QQ lyrics"); CheckCode(root, "retcode", "0", "QQ lyrics");
                reachable = true;
                if (Text(root, "code") != "0") continue;
                string content = BoundedLyrics(System.Net.WebUtility.HtmlDecode(Text(root, "lyric")));
                var document = Document(content, source, "qq", candidate.Proof, snapshot);
                if (document.Validate(snapshot, diagnostic).Lines.Count > 0) return document;
                if (Lrc.Parse(content).Count > 0) unreliable = true;
                else if (plain.Length == 0) plain = content;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or JsonException) { failure = error; }
        }
        if (failure != null && (!reachable || unreliable) && plain.Length == 0)
            throw new HttpRequestException("QQ matched lyric downloads failed", failure);
        if (plain.Length == 0 && unreliable) throw new UnreliableLyricsException();
        return new(plain, source, "qq");
    }

    private static string CandidateRejection(MediaSnapshot snapshot, string title, string artist, string album, double duration)
    {
        if (LyricsProvider.Normalize(snapshot.Artist).Length > 0 && !RecordingRules.ArtistsMatch(snapshot.Artist, artist))
            return "artist-conflict";
        if (snapshot.DurationMs > 0 && duration > 0 && Math.Abs(duration - snapshot.DurationMs) > 3000)
            return "candidate-duration-conflict";
        if (LyricsProvider.Score(snapshot, title, artist, duration) < 65) return "title-version-conflict";
        return AlbumConflict(snapshot, album) ? "catalog-album-conflict" : "";
    }

    private static bool AlbumConflict(MediaSnapshot snapshot, string album) =>
        !RecordingRules.AlbumsCompatible(snapshot.Album, album, snapshot.Artist);
    private static string Album(JsonElement song)
    {
        string album = Text(song, "albumname");
        if (album.Length == 0 && Object(song, "album", out var full)) album = Text(full, "name");
        if (album.Length == 0 && Object(song, "al", out var shortAlbum)) album = Text(shortAlbum, "name");
        return album;
    }
    private static LyricDocument Document(string content, string source, string provider, RecordingEvidence proof, MediaSnapshot snapshot) =>
        new(content, source, provider, proof.Matches(snapshot) ? proof : null);
    private static long SafeDuration(double duration) => duration > 0 && duration <= 604800000 && double.IsFinite(duration) ? (long)duration : 0;

    private static async Task<LyricDocument> NeteaseAsync(MediaSnapshot snapshot, Identifier identifier,
        Func<string, string?, CancellationToken, Task<JsonDocument>> get, CancellationToken token, Action<string>? diagnostic)
    {
        const string source = "网易云音乐 · 歌曲编号匹配";
        const string referer = "https://music.163.com/";
        string id = identifier.Value;
        using var detail = await get("https://music.163.com/api/song/detail/?id=" + id
            + "&ids=" + Uri.EscapeDataString("[" + id + "]"), referer, token);
        var root = detail.RootElement;
        CheckCode(root, "code", "200", "NetEase detail");
        if (Text(root, "code") != "200" || !Array(root, "songs", out var songs)) return new("", source, "netease");
        JsonElement song = default;
        foreach (var item in songs.EnumerateArray())
            if (Text(item, "id") == id) { song = item; break; }
        if (song.ValueKind != JsonValueKind.Object) return new("", source, "netease");
        string artist = Artists(song, "artists");
        if (artist.Length == 0) artist = Artists(song, "ar");
        double duration = Milliseconds(song, "duration");
        if (duration <= 0) duration = Milliseconds(song, "dt");
        var proof = RecordingEvidence.Native("netease", snapshot, "id:" + identifier.Value,
            Text(song, "name"), artist, Album(song), SafeDuration(duration));
        string rejection = CandidateRejection(snapshot, Text(song, "name"), artist, Album(song), duration);
        if (rejection.Length > 0)
        {
            CandidateDiagnostic.Report(snapshot, new("", source, "netease", proof), rejection, false, diagnostic);
            return new("", source, "netease");
        }

        using var lyrics = await get("https://music.163.com/api/song/lyric?id=" + id
            + "&lv=-1&kv=-1&tv=-1", referer, token);
        root = lyrics.RootElement;
        CheckCode(root, "code", "200", "NetEase lyrics");
        if (Text(root, "code") != "200" || !Object(root, "lrc", out var lrc)) return new("", source, "netease");
        var document = Document(BoundedLyrics(Text(lrc, "lyric")), source, "netease", proof, snapshot);
        document.Validate(snapshot, diagnostic);
        return document;
    }

    private static async Task<LyricDocument> QqAsync(MediaSnapshot snapshot, Identifier identifier,
        Func<string, string?, CancellationToken, Task<JsonDocument>> get, CancellationToken token, Action<string>? diagnostic)
    {
        const string source = "QQ音乐 · 歌曲编号匹配";
        const string referer = "https://y.qq.com/";
        string parameter = identifier.Numeric ? "songid" : "songmid";
        using var detail = await get("https://c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg?"
            + parameter + "=" + identifier.Value + "&tpl=yqq_song_detail&format=json&g_tk=5381&platform=yqq",
            referer, token);
        var root = detail.RootElement;
        CheckCode(root, "code", "0", "QQ detail");
        if (Text(root, "code") != "0" || root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)) return new("", source, "qq");
        var candidates = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray()
            : data.ValueKind == JsonValueKind.Object ? new[] { data } : System.Array.Empty<JsonElement>();
        JsonElement song = default;
        foreach (var candidate in candidates)
            if (Text(candidate, identifier.Numeric ? "id" : "mid") == identifier.Value) { song = candidate; break; }
        if (song.ValueKind != JsonValueKind.Object) return new("", source, "qq");
        string title = Text(song, "title");
        if (title.Length == 0) title = Text(song, "name");
        // The title can include Live/remix qualifiers missing from the short name.
        var proof = RecordingEvidence.Native("qq", snapshot, (identifier.Numeric ? "id:" : "mid:") + identifier.Value,
            title, Artists(song, "singer"), Album(song), SafeDuration(Milliseconds(song, "interval") * 1000));
        string rejection = CandidateRejection(snapshot, title, Artists(song, "singer"), Album(song), Milliseconds(song, "interval") * 1000);
        if (rejection.Length > 0)
        {
            CandidateDiagnostic.Report(snapshot, new("", source, "qq", proof), rejection, false, diagnostic);
            return new("", source, "qq");
        }

        using var lyrics = await get("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?"
            + (identifier.Numeric ? "musicid" : "songmid") + "=" + identifier.Value
            + "&format=json&nobase64=1&platform=yqq.json&g_tk=5381", referer, token);
        root = lyrics.RootElement;
        CheckCode(root, "code", "0", "QQ lyrics");
        CheckCode(root, "retcode", "0", "QQ lyrics");
        if (Text(root, "code") != "0" || Text(root, "retcode") is not ("" or "0")) return new("", source, "qq");
        var document = Document(BoundedLyrics(System.Net.WebUtility.HtmlDecode(Text(root, "lyric"))), source, "qq", proof, snapshot);
        document.Validate(snapshot, diagnostic);
        return document;
    }

    private static void CheckCode(JsonElement value, string field, string expected, string endpoint)
    {
        string code = Text(value, field);
        if (code.Length > 0 && code != expected)
            throw new HttpRequestException(endpoint + " rejected request: " + field + "=" + code);
    }
    private static string BoundedLyrics(string content) => content.Length <= 524288 ? content : "";
    private static string Artists(JsonElement value, string key) => Array(value, key, out var artists)
        ? string.Join("/", artists.EnumerateArray().Select(artist => Text(artist, "name")).Where(name => name.Length > 0)) : "";
    private static double Milliseconds(JsonElement value, string key) =>
        double.TryParse(Text(value, key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) && number > 0 ? number : 0;
    private static string Text(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var item)) return "";
        return item.ValueKind is JsonValueKind.String or JsonValueKind.Number ? item.ToString().Trim() : "";
    }
    private static bool Array(JsonElement value, string key, out JsonElement array)
    {
        array = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out array) && array.ValueKind == JsonValueKind.Array;
    }
    private static bool Object(JsonElement value, string key, out JsonElement item)
    {
        item = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out item) && item.ValueKind == JsonValueKind.Object;
    }
}
