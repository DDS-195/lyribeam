using System.Text.Json;
namespace LyricBridge;

internal sealed record LyricDocument(string Content, string Source, string Provider, RecordingEvidence? Evidence = null)
{
    internal LyricsResult Validate(MediaSnapshot snapshot, Action<string>? diagnostic = null)
    {
        if (Source == "纯音乐") return new(new(), Source, IsInstrumental: true) { Provider = Provider };
        bool providerMatches = Evidence == null || Evidence.Provider == Provider;
        string reason = "provider-evidence-mismatch";
        var parsed = providerMatches ? LyricsProvider.ParseLyricsChecked(Content, snapshot, Evidence, out reason) : new List<LyricLine>();
        bool timed = Lrc.Parse(Content).Count > 0;
        if (Content.Length > 0) CandidateDiagnostic.Report(snapshot, this, parsed.Count > 0 ? "verified" : reason, parsed.Count > 0, diagnostic);
        return new(parsed, parsed.Count > 0 ? Source : Source + (timed ? "（歌词录音或时间轴未通过校验）" : "（暂无可用同步歌词）"),
            parsed.Count > 0 || !timed ? Content : "", Outcome: parsed.Count > 0 ? LyricsOutcome.Success
                : timed ? LyricsOutcome.Unreliable : Content.Length > 0 ? LyricsOutcome.Unsynced : LyricsOutcome.NoMatch) {
            Provider = Provider, RecordingEvidence = Evidence
        };
    }
}

internal static class CandidateDiagnostic
{
    internal static void Report(MediaSnapshot snapshot, LyricDocument document, string reason, bool accepted, Action<string>? diagnostic)
    {
        if (diagnostic == null) return;
        string title = ProvidedLyrics.ReadHeader(document.Content, "ti", out bool titleClear),
            artist = ProvidedLyrics.ReadHeader(document.Content, "ar", out bool artistClear),
            album = ProvidedLyrics.ReadHeader(document.Content, "al", out bool albumClear);
        string binding = "none";
        bool valid = document.Evidence?.Matches(snapshot, out binding) ?? false;
        diagnostic("candidate-decision: " + JsonSerializer.Serialize(new {
            provider = Short(document.Provider), basis = document.Evidence?.Basis ?? "headers", accepted, reason,
            requested = new { player = Short(snapshot.PackageName), title = Short(snapshot.Title), artist = Short(snapshot.Artist),
                album = Short(snapshot.Album), mediaId = Short(snapshot.MediaId), durationMs = snapshot.DurationMs },
            candidate = document.Evidence == null ? null : new { id = Short(document.Evidence.CatalogId),
                title = Short(document.Evidence.Title), artist = Short(document.Evidence.Artist), album = Short(document.Evidence.Album),
                durationMs = document.Evidence.DurationMs },
            headers = new { title = Short(title), artist = Short(artist), album = Short(album), titleClear, artistClear, albumClear },
            evidence = new { valid, state = binding }
        }));
    }
    private static string Short(string? value) => value == null ? "" : value.Length <= 160 ? value : value[..110] + "…" + value[^50..];
}
