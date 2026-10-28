using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LyricBridge;

public sealed record MediaSnapshot
{
    public int Version { get; init; }
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string PackageName { get; init; } = "";
    public string MediaId { get; init; } = "";
    public long DurationMs { get; init; }
    public long PositionMs { get; init; }
    public long RawPositionMs { get; init; }
    public long PositionUpdatedAtMs { get; init; }
    public bool PositionKnown { get; init; }
    public bool Playing { get; init; }
    public double Speed { get; init; } = 1;
    public long Sequence { get; init; }
    public string Source { get; init; } = "";
    public string Capability { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string TrackId { get; init; } = "";
    public string ProvidedLyrics { get; init; } = "";
    public string ProvidedLyricsTrackId { get; init; } = "";
    public string EffectiveCapability => Type != "media" ? "none" : PositionKnown ? "sync" : "metadata-only";
    public string Diagnostic { get; init; } = "";
    public string LegacyTrackKey => $"{PackageName}\n{Title}\n{Artist}\n{DurationMs}";
    public string TrackKey => LegacyTrackKey + (MediaId.Length > 0 ? "\nid:" + MediaId : "")
        + (!string.IsNullOrWhiteSpace(Album) ? "\nalbum:" + Album : "")
        + (Source == "integration" ? "\nintegration:" + SessionId + "\ntrack:" + TrackId : "");

    public static MediaSnapshot Parse(string json)
    {
        var value = JsonSerializer.Deserialize<MediaSnapshot>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new FormatException("Empty snapshot");
        if (value.Version != 1 || (value.Type != "media" && value.Type != "idle"))
            throw new FormatException("Unsupported protocol");
        if (value.Title is null || value.Artist is null || value.Title.Length > 512 || value.Artist.Length > 512
            || value.Type == "media" && string.IsNullOrWhiteSpace(value.Title)
            || !double.IsFinite(value.Speed) || Math.Abs(value.Speed) > 16 || value.DurationMs < 0 || value.DurationMs > 604800000)
            throw new FormatException("Invalid snapshot fields");
        if (value.PositionKnown && (value.PositionMs < 0 || value.PositionMs > 604800000))
            throw new FormatException("Invalid playback position");
        string package = value.PackageName ?? "", diagnostic = value.Diagnostic ?? "", mediaId = value.MediaId ?? "";
        string album = value.Album ?? "";
        if (mediaId.Length > 256 || package.Length > 256 || diagnostic.Length > 8192 || album.Length > 512) throw new FormatException("Snapshot metadata exceeds limit");
        string source = value.Source ?? "", capability = value.Capability ?? "", session = value.SessionId ?? "", track = value.TrackId ?? "";
        string provided = value.ProvidedLyrics ?? "", binding = value.ProvidedLyricsTrackId ?? "";
        if (source is not ("" or "media-session" or "notification" or "integration")
            || capability is not ("" or "sync" or "metadata-only" or "none")
            || session.Length > 256 || track.Length > 256 || binding.Length > 256 || provided.Length > 65536)
            throw new FormatException("Unsupported or oversized playback capabilities");
        if (source == "notification" && value.PositionKnown) throw new FormatException("Notification text cannot provide a playback clock");
        if (source == "integration" && value.Type == "media" && (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(track)))
            throw new FormatException("Integrated playback needs session and track identities");
        return value with { PackageName = package, Diagnostic = diagnostic, MediaId = mediaId, Album = album,
            Source = source, Capability = capability, SessionId = session, TrackId = track,
            ProvidedLyrics = provided, ProvidedLyricsTrackId = binding };
    }
}

public sealed class LineFramer
{
    public const int MaximumCharacters = 524288;
    private readonly Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly StringBuilder pending = new();
    public List<string> Append(byte[] bytes, int count)
    {
        var chars = new char[count + 4];
        int n = decoder.GetChars(bytes, 0, count, chars, 0);
        var lines = new List<string>();
        for (int i = 0; i < n; i++)
        {
            if (chars[i] == '\n')
            {
                string line = pending.ToString().TrimEnd('\r');
                pending.Clear();
                if (line.Length > 0) lines.Add(line);
            }
            else pending.Append(chars[i]);
            if (pending.Length > MaximumCharacters) throw new FormatException("Message exceeds the bounded playback frame");
        }
        return lines;
    }
}

public sealed class PlaybackClock
{
    public MediaSnapshot? Snapshot { get; private set; }
    private long received;
    public bool Stale => Snapshot is not null && Stopwatch.GetElapsedTime(received).TotalSeconds > 5;
    public void Update(MediaSnapshot value) => Update(value, Stopwatch.GetTimestamp());
    public void Update(MediaSnapshot value, long receivedAt)
    {
        long now = Stopwatch.GetTimestamp();
        Snapshot = value;
        received = receivedAt > 0 && receivedAt <= now ? receivedAt : now;
    }
    public double PositionMs
    {
        get
        {
            var s = Snapshot;
            if (s is null || !s.PositionKnown) return -1;
            double elapsed = Math.Max(0, Stopwatch.GetElapsedTime(received).TotalMilliseconds);
            double position = s.PositionMs + (s.Playing ? Math.Min(elapsed, 5000) * s.Speed : 0);
            return Math.Clamp(position, 0, s.DurationMs > 0 ? s.DurationMs : 604800000);
        }
    }
}

public sealed record LyricLine(long TimeMs, string Text);
public static partial class Lrc
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[\.:](\d{1,3}))?\]")]
    private static partial Regex TimestampRegex();
    [GeneratedRegex(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetRegex();
    public static List<LyricLine> Parse(string content)
    {
        long offset = 0;
        var offsetMatch = OffsetRegex().Match(content);
        if (offsetMatch.Success) long.TryParse(offsetMatch.Groups[1].Value, out offset);
        var result = new List<LyricLine>();
        foreach (string raw in content.Split('\n'))
        {
            var times = TimestampRegex().Matches(raw);
            if (times.Count == 0) continue;
            string text = ChineseText.Simplify(TimestampRegex().Replace(raw, "").Trim());
            foreach (Match match in times)
            {
                long ms = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60000
                    + long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 1000;
                string fraction = match.Groups[3].Value;
                if (fraction.Length > 0) ms += long.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture);
                result.Add(new LyricLine(Math.Max(0, ms + offset), text));
            }
        }
        return result.OrderBy(l => l.TimeMs).ToList();
    }
    public static int FindIndex(IReadOnlyList<LyricLine> lines, double positionMs)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].TimeMs <= positionMs) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }
}
