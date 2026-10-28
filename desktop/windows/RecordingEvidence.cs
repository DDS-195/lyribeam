using System.Text.RegularExpressions;
namespace LyricBridge;

// Built on the desktop from a checked catalogue response, never trusted from a phone packet.
// The original seven fields retain compatibility with the schema-4 Kugou cache.
internal sealed record RecordingEvidence(string PackageName, string MediaId, string FileHash,
    string Title, string Artist, string Album, long DurationMs)
{
    public string Provider { get; init; } = "kugou";
    public string Basis { get; init; } = "native-id";
    public string CatalogId { get; init; } = "";
    public string OriginSource { get; init; } = "";
    public string OriginTrackId { get; init; } = "";

    internal static RecordingEvidence Native(string provider, MediaSnapshot snapshot, string catalogId,
        string title, string artist, string album, long duration, string fileHash = "") =>
        Create(provider, "native-id", snapshot, catalogId, title, artist, album, duration, fileHash);
    internal static RecordingEvidence Catalog(string provider, MediaSnapshot snapshot, string catalogId,
        string title, string artist, string album, long duration, string fileHash = "") =>
        Create(provider, "catalog", snapshot, catalogId, title, artist, album, duration, fileHash);
    private static RecordingEvidence Create(string provider, string basis, MediaSnapshot snapshot, string catalogId,
        string title, string artist, string album, long duration, string fileHash) =>
        new(snapshot.PackageName, snapshot.MediaId, fileHash, title, artist, album, duration) {
            Provider = provider, Basis = basis, CatalogId = catalogId,
            OriginSource = snapshot.Source, OriginTrackId = snapshot.TrackId
        };

    internal bool Matches(MediaSnapshot snapshot) => Matches(snapshot, out _);
    internal bool Matches(MediaSnapshot snapshot, out string reason)
    {
        if (PackageName == null || MediaId == null || FileHash == null || Title == null || Artist == null || Album == null
            || Provider == null || Basis == null || CatalogId == null || OriginSource == null || OriginTrackId == null
            || CatalogId.Length > 256 || FileHash.Length > 128 || PackageName.Length > 256 || MediaId.Length > 512
            || OriginSource.Length > 64 || OriginTrackId.Length > 512)
        { reason = "malformed-evidence"; return false; }
        if (snapshot.Type != "media" || Basis is not ("native-id" or "catalog")
            || Provider is not ("kugou" or "qq" or "netease" or "lrclib"))
        { reason = "untrusted-source"; return false; }
        if (PackageName != snapshot.PackageName || MediaId != snapshot.MediaId
            || OriginSource.Length > 0 && OriginSource != snapshot.Source
            || OriginTrackId.Length > 0 && OriginTrackId != snapshot.TrackId)
        { reason = "identifier-mismatch"; return false; }
        if (Basis == "native-id")
        {
            if (Provider != "kugou" && OriginSource != "media-session") { reason = "untrusted-source"; return false; }
            if (snapshot.Source is not ("" or "media-session")) { reason = "untrusted-source"; return false; }
            if (LyricsProvider.NativeFamily(PackageName) != Provider)
            { reason = "platform-mismatch"; return false; }
            string expected = Provider == "kugou"
                ? (Regex.IsMatch(MediaId, @"\A[1-9][0-9]{0,19}\z") ? "id:" + MediaId : "")
                : PlatformLyrics.NativeCatalogId(snapshot);
            // Only old Kugou documents may omit the namespaced catalogue identifier.
            string actual = CatalogId.Length == 0 && Provider == "kugou" ? "id:" + MediaId : CatalogId;
            if (expected.Length == 0 || expected != actual)
            { reason = "identifier-mismatch"; return false; }
            if (Provider == "kugou" && !Regex.IsMatch(FileHash, @"\A[A-Fa-f0-9]{32}\z"))
            { reason = "file-hash-missing"; return false; }
            if (!KnownText(Album) || !KnownText(snapshot.Album))
            { reason = "metadata-incomplete"; return false; }
            if (DurationMs <= 0 || DurationMs > 604800000 || snapshot.DurationMs <= 0 || snapshot.DurationMs > 604800000)
            { reason = "duration-unknown"; return false; }
            if (Math.Abs(DurationMs - snapshot.DurationMs) > 1000)
            { reason = "catalog-duration-conflict"; return false; }
        }
        else if (DurationMs < 0 || DurationMs > 604800000 || snapshot.DurationMs > 604800000
            || DurationMs > 0 && snapshot.DurationMs > 0 && Math.Abs(DurationMs - snapshot.DurationMs) > 3000)
        { reason = "catalog-duration-conflict"; return false; }
        if (!KnownText(Title) || !KnownText(Artist) || !KnownText(snapshot.Title) || !KnownText(snapshot.Artist)
            || Album.Length > 512 || snapshot.Album.Length > 512)
        { reason = "metadata-incomplete"; return false; }
        if (!RecordingRules.ArtistsMatch(snapshot.Artist, Artist)) { reason = "artist-conflict"; return false; }
        if (LyricsProvider.Score(snapshot, Title, Artist, DurationMs) < 65) { reason = "title-version-conflict"; return false; }
        if (!RecordingRules.AlbumsCompatible(snapshot.Album, Album, snapshot.Artist))
        { reason = "catalog-album-conflict"; return false; }
        reason = "verified"; return true;
    }

    internal bool AllowsLyricAlbum(MediaSnapshot snapshot, string lyricTitle, string lyricArtist, string lyricAlbum)
    {
        // Search metadata is useful evidence, but cannot grant native recording privileges.
        if (Basis != "native-id" || !Matches(snapshot) || !KnownText(lyricTitle) || !KnownText(lyricArtist) || !KnownText(lyricAlbum)) return false;
        return RecordingRules.ArtistsMatch(snapshot.Artist, lyricArtist)
            && RecordingRules.NativeLyricTitleMatches(snapshot, lyricTitle, lyricAlbum, DurationMs)
            && RecordingRules.AlbumLabelsCompatible(snapshot.Album, lyricAlbum, snapshot.Artist);
    }
    private static bool KnownText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512
        && LyricsProvider.Normalize(value).Length > 0;
}
