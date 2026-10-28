using System.Text.RegularExpressions;

namespace LyricBridge;

internal static class ProvidedLyrics
{
    // Complete lyrics are accepted only from the separately authorized integration
    // channel, and are bound to this session's current opaque track identifier.
    public static bool TryValidate(MediaSnapshot snapshot, out LyricsResult result, out string reason)
    {
        result = new(new(), "");
        string content = snapshot.ProvidedLyrics ?? "";
        if (content.Length == 0) { reason = "absent"; return false; }
        if (content.Length > 65536) { reason = "content-too-large"; return false; }
        if (snapshot.Type != "media" || snapshot.Source != "integration")
        { reason = "untrusted-or-inactive-source"; return false; }
        if (string.IsNullOrWhiteSpace(snapshot.SessionId) || string.IsNullOrWhiteSpace(snapshot.TrackId)
            || string.IsNullOrWhiteSpace(snapshot.ProvidedLyricsTrackId)
            || !string.Equals(snapshot.TrackId, snapshot.ProvidedLyricsTrackId, StringComparison.Ordinal))
        { reason = "track-binding-mismatch"; return false; }
        foreach (string tag in new[] { "ti", "ar", "al" })
        {
            string header = ReadHeader(content, tag, out bool unambiguous);
            if (!unambiguous) { reason = "conflicting-" + tag + "-headers"; return false; }
            if (tag == "al" && header.Length > 0 && !string.IsNullOrWhiteSpace(snapshot.Album)
                && !RecordingRules.AlbumsCompatible(snapshot.Album, header, snapshot.Artist))
            { reason = "album-mismatch"; return false; }
        }
        string title = ReadHeader(content, "ti", out _), artist = ReadHeader(content, "ar", out _);
        if ((title.Length > 0 || artist.Length > 0)
            && LyricsProvider.Score(snapshot, title.Length > 0 ? title : snapshot.Title,
                artist.Length > 0 ? artist : snapshot.Artist, snapshot.DurationMs) < 65)
        { reason = "recording-headers-mismatch"; return false; }
        if (!LyricQuality.TryValidate(content, snapshot, out var lines, out reason)) return false;
        result = new(lines, "播放器提供的歌词", content) { FromProvidedLyrics = true };
        reason = "";
        return true;
    }

    // Omitted payloads keep their binding; a missing or different binding is an
    // explicit withdrawal and must not revive a previously supplied cache entry.
    internal static bool CanReuseCached(MediaSnapshot snapshot) => snapshot.Source != "integration"
        || !string.IsNullOrWhiteSpace(snapshot.TrackId)
            && string.Equals(snapshot.TrackId, snapshot.ProvidedLyricsTrackId, StringComparison.Ordinal);

    // Read evidence from the original document; never manufacture tags from the request.
    internal static string ReadHeader(string content, string tag, out bool unambiguous)
    {
        var headers = Regex.Matches(content, @"\[" + tag + @":([^\]\r\n]*)\]", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value.Trim()).ToList();
        unambiguous = headers.All(value => value.Length <= 512)
            && headers.Select(LyricsProvider.Normalize).Distinct(StringComparer.Ordinal).Take(2).Count() <= 1;
        return headers.FirstOrDefault() ?? "";
    }
}
