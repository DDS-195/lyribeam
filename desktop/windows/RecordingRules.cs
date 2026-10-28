using System.Text.RegularExpressions;
namespace LyricBridge;
internal static class RecordingRules
{
    private static readonly Regex Brackets = new(@"[\(（\[【]([^\)）\]】]+)[\)）\]】]");
    private static readonly Regex Original = new(@"[\(（\[【]\s*(?:original(?:\s+version)?|原版|正式版)\s*[\)）\]】]", RegexOptions.IgnoreCase);
    private static readonly Regex Featured = new(@"^(?:feat(?:uring)?|ft)\.?\s+(.+)$", RegexOptions.IgnoreCase);
    private static readonly Regex TrailingFeatured = new(@"\s+(?:feat(?:uring)?|ft)\.?\s+(.+)$", RegexOptions.IgnoreCase);
    private static readonly Regex Version = new(@"\blive\b|\bdj\b|\bremix\b|\bcover\b|\bdemo\b|\bacoustic\b|\bremaster|\bver(?:sion)?\b|\bextended\b|\bmix\b|\bedit\b|\bradio\b|\brework\b|\bbootleg\b|\bmashup\b|\bnightcore\b|\bsped\b|\bslowed\b|\bedition\b|\bdeluxe\b|\banniversary\b|\bbonus\b|\bexpanded\b|\breissue\b", RegexOptions.IgnoreCase);
    private const string ChineseVersionPattern = @"现场|混音|翻唱|伴奏|铃声|加速|慢速|纯音乐|重制|重录|豪华|纪念|周年|珍藏|典藏|特别版|扩展版|剪辑|精简版|加长版";
    private static int Script(string value) => (Regex.IsMatch(value,@"[A-Za-z]")?1:0) | (Regex.IsMatch(value,@"\p{IsCJKUnifiedIdeographs}")?2:0)
        | (Regex.IsMatch(value,@"[\u3040-\u30ff]")?4:0) | (Regex.IsMatch(value,@"[\uac00-\ud7af]")?8:0) | (Regex.IsMatch(value,@"[\u0400-\u04ff]")?16:0);
    private static string[] ArtistParts(string value) => Regex.Split(value,@"\s*(?:/|、|&|＆|;|；|,|，|\bfeat\.?\s|\bft\.?\s|\bfeaturing\s)\s*",RegexOptions.IgnoreCase)
        .Where(s=>!string.IsNullOrWhiteSpace(s)).ToArray();
    internal static bool ArtistsMatch(string requested,string candidate)
    {
        if (LyricsProvider.Normalize(requested)==LyricsProvider.Normalize(candidate)) return !string.IsNullOrWhiteSpace(requested);
        HashSet<string> Aliases(string value)
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { LyricsProvider.Normalize(value), LyricsProvider.Normalize(Brackets.Replace(value,"")) };
            foreach(Match match in Brackets.Matches(value)) { string alias=match.Groups[1].Value; string main=Brackets.Replace(value, ""); if(Script(main)>0 && Script(alias)>0 && (Script(main)&Script(alias))==0) aliases.Add(LyricsProvider.Normalize(alias)); }
            foreach(Match match in Regex.Matches(Brackets.Replace(value,""),@"\p{IsCJKUnifiedIdeographs}{2,}")) aliases.Add(LyricsProvider.Normalize(match.Value));
            aliases.Remove(""); return aliases;
        }
        var left=ArtistParts(requested).Select(Aliases).ToArray();var right=ArtistParts(candidate).Select(Aliases).ToArray();
        return left.Length>0 && right.Length>0 && left.All(a=>right.Any(b=>a.Overlaps(b))) && right.All(b=>left.Any(a=>a.Overlaps(b)));
    }

    private sealed class NameEvidence
    {
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Qualifiers { get; } = new(StringComparer.Ordinal);
        public string Language { get; set; } = "";
        public string Content { get; set; } = "";
        public bool Invalid { get; set; }
    }
    private static string Language(string tag)
    {
        string key = LyricsProvider.Normalize(tag);
        key = Regex.Replace(key, @"(?:version|ver|版)$", "");
        return key switch {
            "japanese" or "japan" or "jp" or "jpn" or "日语" or "日文" => "ja",
            "korean" or "korea" or "kr" or "kor" or "韩语" or "韩文" => "ko",
            "english" or "en" or "eng" or "英语" or "英文" => "en",
            "mandarin" or "国语" or "普通话" => "zh-mandarin",
            "cantonese" or "粤语" or "广东话" => "zh-cantonese",
            "chinese" or "中文" or "汉语" => "zh",
            _ => ""
        };
    }
    private static string Content(string tag) => LyricsProvider.Normalize(tag) switch {
        "explicit" or "explicitversion" or "uncensored" or "uncensoredversion" or "dirty" or "dirtyversion" => "explicit",
        "clean" or "cleanversion" or "censored" or "censoredversion" or "消音版" or "洁净版" => "clean",
        _ => ""
    };
    private static void AddSemantic(NameEvidence evidence, string value, bool language)
    {
        string current = language ? evidence.Language : evidence.Content;
        if (current.Length > 0 && current != value) evidence.Invalid = true;
        if (language) evidence.Language = value; else evidence.Content = value;
    }
    private static bool FeaturedArtistsKnown(string text, string artist)
    {
        var featured = ArtistParts(text); var known = ArtistParts(artist);
        return featured.Length > 0 && known.Length > 0
            && featured.All(name => known.Any(credit => ArtistsMatch(name, credit)));
    }
    private static NameEvidence Parse(string value, string artist)
    {
        var evidence = new NameEvidence();
        string main = Brackets.Replace(value, "").Trim();
        var suffix = TrailingFeatured.Match(main);
        if (suffix.Success && FeaturedArtistsKnown(suffix.Groups[1].Value, artist)) main = main[..suffix.Index].Trim();
        evidence.Names.Add(LyricsProvider.Normalize(main));
        foreach (Match match in Brackets.Matches(value))
        {
            string tag = match.Groups[1].Value.Trim(), key = LyricsProvider.Normalize(tag);
            if (Regex.IsMatch(tag, @"^(?:original(?:\s+version)?|原版|正式版)$", RegexOptions.IgnoreCase)) continue;
            string language = Language(tag), content = Content(tag);
            if (language.Length > 0) { AddSemantic(evidence, language, true); continue; }
            if (content.Length > 0) { AddSemantic(evidence, content, false); continue; }
            var feature = Featured.Match(tag);
            if (feature.Success)
            {
                if (!FeaturedArtistsKnown(feature.Groups[1].Value, artist)) evidence.Qualifiers.Add(key);
                continue;
            }
            bool version = Version.IsMatch(tag) || Regex.IsMatch(key, ChineseVersionPattern);
            bool translation = !version && Script(main)>0 && Script(tag)>0 && (Script(main)&Script(tag))==0;
            if (translation) evidence.Names.Add(key); else evidence.Qualifiers.Add(key);
        }
        evidence.Names.Remove(""); evidence.Qualifiers.Remove("");
        return evidence;
    }
    private static bool Conflicts(string a, string b) => a.Length > 0 && b.Length > 0 && a != b;
    private static bool Corroborated(MediaSnapshot snapshot, bool artistMatches, double duration) => artistMatches
        && snapshot.DurationMs > 0 && duration > 0 && Math.Abs(snapshot.DurationMs-duration)<=3000;

    internal static bool TitlesMatch(MediaSnapshot snapshot, string candidateTitle, bool artistMatches, double candidateDuration)
    {
        if (LyricsProvider.Normalize(snapshot.Title).Length == 0 || LyricsProvider.Normalize(candidateTitle).Length == 0) return false;
        var left = Parse(snapshot.Title, snapshot.Artist); var right = Parse(candidateTitle, snapshot.Artist);
        var album = Parse(snapshot.Album, snapshot.Artist);
        if (left.Invalid || right.Invalid || album.Invalid || Conflicts(left.Language, right.Language)
            || Conflicts(left.Language, album.Language) || Conflicts(right.Language, album.Language)
            || Conflicts(left.Content, right.Content) || Conflicts(left.Content, album.Content) || Conflicts(right.Content, album.Content)) return false;
        if (!left.Qualifiers.SetEquals(right.Qualifiers) || !left.Names.Overlaps(right.Names)) return false;
        // An omitted Explicit/Clean title label is accepted only when the same
        // edition is independently present in the active player's album metadata.
        if (left.Content != right.Content && album.Content != (left.Content.Length > 0 ? left.Content : right.Content)) return false;
        string a = LyricsProvider.Normalize(Original.Replace(snapshot.Title,""));
        string b = LyricsProvider.Normalize(Original.Replace(candidateTitle,""));
        if (a == b) return true;
        // Aliases, omitted language labels, and credited feature annotations must
        // have both the singer and duration of the active recording as evidence.
        return Corroborated(snapshot, artistMatches, candidateDuration);
    }
    internal static bool AlbumsCompatible(string requested, string candidate, string artist = "")
    {
        if (LyricsProvider.Normalize(requested).Length == 0 || LyricsProvider.Normalize(candidate).Length == 0) return true;
        var left = Parse(requested, artist); var right = Parse(candidate, artist);
        return !left.Invalid && !right.Invalid && left.Names.Overlaps(right.Names)
            && left.Qualifiers.SetEquals(right.Qualifiers) && left.Language == right.Language && left.Content == right.Content;
    }
    // Only a separately verified native recording may use release packaging as descriptive metadata.
    internal static bool AlbumLabelsCompatible(string requested, string candidate, string artist = "")
    {
        if (LyricsProvider.Normalize(requested).Length == 0 || LyricsProvider.Normalize(candidate).Length == 0) return false;
        var left = Parse(requested, artist); var right = Parse(candidate, artist);
        var leftWords = UnbracketedAlbumLabels(requested); var rightWords = UnbracketedAlbumLabels(candidate);
        string Edition(NameEvidence evidence, HashSet<string> words, out bool invalid)
        {
            var editions = words.Select(Content).Append(evidence.Content).Where(value => value.Length > 0).Distinct().ToArray();
            invalid = editions.Length > 1; return editions.FirstOrDefault() ?? "";
        }
        string leftEdition = Edition(left, leftWords, out bool leftInvalid), rightEdition = Edition(right, rightWords, out bool rightInvalid);
        leftWords.RemoveWhere(value => Content(value).Length > 0);
        rightWords.RemoveWhere(value => Content(value).Length > 0);
        return !left.Invalid && !right.Invalid && !leftInvalid && !rightInvalid
            && left.Qualifiers.Where(value => !ReleasePackaging(value)).ToHashSet().SetEquals(right.Qualifiers.Where(value => !ReleasePackaging(value)))
            && left.Language == right.Language && !Conflicts(leftEdition, rightEdition)
            && leftWords.SetEquals(rightWords);
    }
    private static bool ReleasePackaging(string value) => Regex.IsMatch(value,
        @"\A(?:doublealbum|single|singlerelease|digital(?:single|album)|compilation(?:album)?|album(?:release)?|ep|\d+(?:cd|disc)(?:set)?)\z", RegexOptions.CultureInvariant);

    internal static bool NativeLyricTitleMatches(MediaSnapshot snapshot, string candidateTitle, string lyricAlbum, double duration)
    {
        if (TitlesMatch(snapshot, candidateTitle, true, duration)) return true;
        const string soundtrack = @"(?:o\.?\s*s\.?\s*t\.?|(?:original\s+)?(?:motion\s+picture\s+)?soundtrack)";
        string albumMain = Brackets.Replace(lyricAlbum, "");
        // Strip only a complete trailing release label; OST inside a movie name is not a marker.
        var albumRelease = Regex.Match(albumMain.Trim(), @"\A(?<name>.+?)\s+" + soundtrack + @"\z", RegexOptions.IgnoreCase);
        if (!albumRelease.Success) return false;
        string albumName = LyricsProvider.Normalize(albumRelease.Groups["name"].Value);
        string cleaned = Brackets.Replace(candidateTitle, match =>
        {
            var release = Regex.Match(match.Groups[1].Value.Trim(), @"\A(?<name>.+?)\s+" + soundtrack + @"\s+(?:version|ver\.?)\z", RegexOptions.IgnoreCase);
            if (!release.Success) return match.Value;
            string name = release.Groups["name"].Value;
            // A soundtrack association is release metadata only when independently named in the LRC album.
            if (Version.IsMatch(name) || Regex.IsMatch(name, ChineseVersionPattern) || albumName.Length < 3 || LyricsProvider.Normalize(name) != albumName) return match.Value;
            return "";
        });
        return TitlesMatch(snapshot, cleaned, true, duration);
    }
    private static HashSet<string> UnbracketedAlbumLabels(string value)
    {
        string main = Brackets.Replace(ChineseText.Simplify(value), "");
        const string content = @"\b(?:explicit|clean|uncensored|censored|dirty)(?:\s*version)?\b";
        var contentLabels = Regex.Matches(main, content, RegexOptions.IgnoreCase).Cast<Match>()
            .Select(match => LyricsProvider.Normalize(match.Value)).ToArray();
        main = Regex.Replace(main, content, "", RegexOptions.IgnoreCase);
        const string extra = @"\binstrumental\b|\bkaraoke\b|\bre[-\s]?record(?:ed|ing)?\b|现场|混音|翻唱|伴奏|纯音乐|加速|慢速|重制|重录|豪华|纪念|周年|剪辑版|加长版|消音|洁净";
        return Version.Matches(main).Cast<Match>().Concat(Regex.Matches(main, extra, RegexOptions.IgnoreCase).Cast<Match>())
            .Select(match => LyricsProvider.Normalize(match.Value)).Concat(contentLabels).ToHashSet(StringComparer.Ordinal);
    }
    internal static bool TitleAliasesMatch(string requested,string candidate,bool singerMatches,double duration,double candidateDuration) =>
        TitlesMatch(new MediaSnapshot {Title=requested,DurationMs=(long)Math.Max(0,duration)},candidate,singerMatches,candidateDuration);
}
