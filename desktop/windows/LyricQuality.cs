using System.Text.RegularExpressions;

namespace LyricBridge;

// A small common gate for all lyric sources and caches. Sparse documents are
// provisional, not proof that every recording must contain vocals near its end.
internal static class LyricQuality
{
    private static readonly Regex Time = new(@"\[(\d{1,3}):(\d{2})(?:[\.:](\d{1,3}))?\]", RegexOptions.CultureInvariant);

    public static bool TryValidate(string content, MediaSnapshot snapshot, out List<LyricLine> lines, out string reason)
    {
        lines = new();
        if (string.IsNullOrWhiteSpace(content)) { reason = "empty"; return false; }
        if (content.Length > 524288) { reason = "content-too-large"; return false; }
        if (Time.Matches(content).Any(match => int.Parse(match.Groups[2].Value) >= 60))
        { reason = "invalid-timestamp-seconds"; return false; }
        var parsed = Lrc.Parse(content);
        var meaningful = parsed.Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => line.TimeMs).Distinct().OrderBy(time => time).ToList();
        if (meaningful.Count < 2) { reason = "insufficient-timestamps"; return false; }
        if (snapshot.DurationMs > 0 && parsed.Any(line => line.TimeMs > snapshot.DurationMs))
        { reason = "timeline-outside-recording"; return false; }

        // Two opening lines are often a preview/truncated download. An explicit
        // empty cue close to the end supplies stronger evidence of an intentional
        // long instrumental tail. Empty cues elsewhere are not completion evidence.
        bool endCue = snapshot.DurationMs > 0 && parsed.Any(line => string.IsNullOrWhiteSpace(line.Text)
            && line.TimeMs >= snapshot.DurationMs - 5000 && line.TimeMs <= snapshot.DurationMs);
        if (snapshot.DurationMs >= 60000 && meaningful.Count == 2
            && meaningful[1] - meaningful[0] <= 15000
            && meaningful[1] < snapshot.DurationMs / 3 && !endCue)
        { reason = "sparse-opening-timeline"; return false; }
        lines = parsed; reason = ""; return true;
    }
}
