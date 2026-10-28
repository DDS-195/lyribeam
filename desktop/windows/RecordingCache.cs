using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LyricBridge;

// This cache is deliberately conservative: its index never uses unqualified native
// IDs, and every hit rechecks original document evidence against both observations.
internal sealed class RecordingCache
{
    private const int MaximumContentChars = 262144;
    private const long MaximumEntryBytes = 2097152;
    private readonly string directory;
    private readonly Action<string>? diagnostic;
    private sealed record Entry
    {
        public int Schema { get; init; } = 2;
        public string Identity { get; init; } = "";
        public string TitleHeader { get; init; } = "";
        public string ArtistHeader { get; init; } = "";
        public string AlbumHeader { get; init; } = "";
        public string ObservedTitle { get; init; } = "";
        public string ObservedArtist { get; init; } = "";
        public string ObservedAlbum { get; init; } = "";
        public long ObservedDurationMs { get; init; }
        public string OriginPackage { get; init; } = "";
        public string OriginMediaId { get; init; } = "";
        public string OriginSource { get; init; } = "";
        public string OriginTrackId { get; init; } = "";
        public string LyricsSource { get; init; } = "";
        public string Provider { get; init; } = "";
        public RecordingEvidence? RecordingEvidence { get; init; }
        public string Content { get; init; } = "";
        public string ContentSha256 { get; init; } = "";
        public DateTimeOffset StoredAt { get; init; }
        public DateTimeOffset CheckedAtUtc { get; init; }
        public int Strategy { get; init; }
        public bool NativeVerified { get; init; }
    }

    public RecordingCache(string dataPath, Action<string>? diagnostic = null)
    {
        directory = Path.Combine(dataPath, "recording-cache");
        this.diagnostic = diagnostic;
    }

    public async Task<LyricsResult?> TryReadAsync(MediaSnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string identity = Identity(snapshot);
        if (identity.Length == 0) return null;
        string path = Path.Combine(directory, identity + ".json");
        try
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaximumEntryBytes)
            { diagnostic?.Invoke("shared-cache-rejected: entry-too-large"); return null; }
            var entry = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path, token));
            if (entry == null || entry.Schema is not (1 or 2) || entry.Identity != identity
                || string.IsNullOrEmpty(entry.Content) || entry.Content.Length > MaximumContentChars
                || entry.ContentSha256 != Hash(entry.Content))
            { diagnostic?.Invoke("shared-cache-rejected: invalid-document-integrity"); return null; }
            bool fromProvided = IsProvidedSource(entry.LyricsSource);
            if (fromProvided && !ProvidedLyrics.CanReuseCached(snapshot))
            { diagnostic?.Invoke("shared-cache-rejected: supplied-lyrics-withdrawn"); return null; }
            var observation = new MediaSnapshot {
                Title = entry.ObservedTitle ?? "", Artist = entry.ObservedArtist ?? "", Album = entry.ObservedAlbum ?? "",
                Type = "media", DurationMs = entry.ObservedDurationMs,
                PackageName = entry.OriginPackage ?? "", MediaId = entry.OriginMediaId ?? "",
                Source = entry.OriginSource ?? "", TrackId = entry.OriginTrackId ?? ""
            };
            if (Identity(observation) != identity || IdentityConflicts(entry, snapshot)
                || Math.Abs(snapshot.DurationMs - entry.ObservedDurationMs) > 1000
                || !Evidence(observation, entry.Content, out var original, out _)
                || original.TitleHeader != entry.TitleHeader || original.ArtistHeader != entry.ArtistHeader
                || original.AlbumHeader != entry.AlbumHeader
                || !Evidence(snapshot, entry.Content, out _, out var lines))
            { diagnostic?.Invoke("shared-cache-rejected: recording-evidence-mismatch"); return null; }
            string provider = entry.Schema >= 2 ? KnownProvider(entry.Provider) : "";
            var proof = entry.Schema >= 2 && !fromProvided && provider.Length > 0
                && entry.RecordingEvidence?.Provider == provider
                && ProofMatches(entry.RecordingEvidence, observation) && ProofMatches(entry.RecordingEvidence, snapshot)
                ? entry.RecordingEvidence : null;
            // Source provenance never substitutes for the independent header checks above.
            // A proof bound to another player is discarded even when the strict document is reusable.
            bool nativeVerified = NativeProvenance(provider, observation.PackageName, snapshot.PackageName, entry.CheckedAtUtc);
            diagnostic?.Invoke("shared-cache-hit: verified-headers-album-duration");
            return new(lines, "已验证的共享缓存") { FromProvidedLyrics = fromProvided, Provider = provider,
                RecordingEvidence = proof,
                NeedsSourceReview = !fromProvided && LyricsProvider.CacheNeedsSourceReview(snapshot,
                    entry.Strategy, nativeVerified, entry.CheckedAtUtc, entry.OriginPackage) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { diagnostic?.Invoke("shared-cache-read-failed: " + error.GetType().Name); return null; }
    }

    public async Task StoreAsync(MediaSnapshot snapshot, string content, string source, CancellationToken token,
        bool nativeVerified = false, int strategy = 0, DateTimeOffset? checkedAtUtc = null,
        string provider = "", RecordingEvidence? recordingEvidence = null)
    {
        token.ThrowIfCancellationRequested();
        if (!Evidence(snapshot, content, out var evidence, out _)) return;
        provider = KnownProvider(provider);
        var proof = provider.Length > 0 && recordingEvidence?.Provider == provider
            && ProofMatches(recordingEvidence, snapshot) ? recordingEvidence : null;
        var checkedAt = checkedAtUtc ?? DateTimeOffset.MinValue;
        var entry = evidence with {
            OriginPackage = snapshot.PackageName, OriginMediaId = snapshot.MediaId,
            OriginSource = snapshot.Source, OriginTrackId = snapshot.TrackId,
            LyricsSource = source, StoredAt = DateTimeOffset.UtcNow,
            Provider = provider, RecordingEvidence = proof,
            NativeVerified = NativeProvenance(provider, snapshot.PackageName, snapshot.PackageName, checkedAt),
            Strategy = strategy, CheckedAtUtc = checkedAt
        };
        string path = Path.Combine(directory, entry.Identity + ".json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entry), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { diagnostic?.Invoke("shared-cache-write-failed: " + error.GetType().Name); }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool Evidence(MediaSnapshot snapshot, string content, out Entry evidence, out List<LyricLine> lines)
    {
        evidence = new(); lines = new();
        string identity = Identity(snapshot);
        if (identity.Length == 0 || string.IsNullOrEmpty(content) || content.Length > MaximumContentChars) return false;
        string title = ProvidedLyrics.ReadHeader(content, "ti", out bool titleClear);
        string artist = ProvidedLyrics.ReadHeader(content, "ar", out bool artistClear);
        string album = ProvidedLyrics.ReadHeader(content, "al", out bool albumClear);
        if (!titleClear || !artistClear || !albumClear
            || LyricsProvider.Normalize(title).Length == 0 || LyricsProvider.Normalize(artist).Length == 0
            || LyricsProvider.Normalize(album).Length == 0
            || !RecordingRules.AlbumsCompatible(snapshot.Album, album, snapshot.Artist)
            || LyricsProvider.Score(snapshot, title, artist, snapshot.DurationMs) < 65) return false;
        if (!LyricQuality.TryValidate(content, snapshot, out lines, out _)) return false;
        evidence = new() {
            Identity = identity, TitleHeader = title, ArtistHeader = artist, AlbumHeader = album,
            ObservedTitle = snapshot.Title, ObservedArtist = snapshot.Artist, ObservedAlbum = snapshot.Album,
            ObservedDurationMs = snapshot.DurationMs, Content = content, ContentSha256 = Hash(content)
        };
        return true;
    }

    private static string KnownProvider(string? value) => value is "kugou" or "qq" or "netease" or "lrclib" ? value : "";

    private static bool NativeProvenance(string provider, string? originalPackage, string? currentPackage, DateTimeOffset checkedAt)
    {
        if (provider.Length == 0 || checkedAt <= DateTimeOffset.UtcNow.AddHours(-6)
            || checkedAt > DateTimeOffset.UtcNow.AddMinutes(5)) return false;
        return LyricsProvider.NativeFamily(originalPackage ?? "") == provider
            && LyricsProvider.NativeFamily(currentPackage ?? "") == provider;
    }

    private static bool ProofMatches(RecordingEvidence? proof, MediaSnapshot snapshot)
    {
        // Nullable annotations do not constrain a deserialized cache document.
        if (proof == null || proof.PackageName == null || proof.MediaId == null || proof.FileHash == null
            || proof.Title == null || proof.Artist == null || proof.Album == null || proof.Provider == null
            || proof.Basis == null || proof.CatalogId == null || proof.OriginSource == null || proof.OriginTrackId == null)
            return false;
        return proof.Matches(snapshot);
    }
    private static bool IsProvidedSource(string? source) => source == "播放器提供的歌词" || string.Equals(source, "provided", StringComparison.OrdinalIgnoreCase);

    private static bool IdentityConflicts(Entry entry, MediaSnapshot snapshot)
    {
        if (!string.Equals(entry.OriginPackage, snapshot.PackageName, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(entry.OriginMediaId) && !string.IsNullOrEmpty(snapshot.MediaId)
            && !string.Equals(entry.OriginMediaId, snapshot.MediaId, StringComparison.Ordinal)) return true;
        return entry.OriginSource == "integration" && snapshot.Source == "integration"
            && !string.IsNullOrEmpty(entry.OriginTrackId) && !string.IsNullOrEmpty(snapshot.TrackId)
            && !string.Equals(entry.OriginTrackId, snapshot.TrackId, StringComparison.Ordinal);
    }

    private static string Identity(MediaSnapshot snapshot)
    {
        if (snapshot.DurationMs <= 0 || snapshot.DurationMs > 604800000
            || snapshot.Title == null || snapshot.Artist == null || snapshot.Album == null
            || snapshot.Title.Length > 512 || snapshot.Artist.Length > 512 || snapshot.Album.Length > 512) return "";
        string title = LyricsProvider.Normalize(snapshot.Title), artist = LyricsProvider.Normalize(snapshot.Artist),
            album = LyricsProvider.Normalize(snapshot.Album);
        if (title.Length == 0 || artist.Length == 0 || album.Length == 0) return "";
        return Hash(title + "\n" + artist + "\n" + album);
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
