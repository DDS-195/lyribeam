using System.Text;
using System.Text.Json;

namespace LyricBridge;

// Writes metadata diagnostics only. Callers serialize writes for a given path.
internal static class LookupDiagnosticLog
{
    internal const long MaximumFileBytes = 1024 * 1024;
    internal const int ArchiveCount = 2;
    internal const int MaximumRecordBytes = 16 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    internal static void Append(string path, DateTimeOffset time, string message,
        long maximumFileBytes = MaximumFileBytes, int archiveCount = ArchiveCount)
    {
        if (maximumFileBytes < 256) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        if (archiveCount is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(archiveCount));
        string line = EncodeRecord(time, message ?? "", (int)Math.Min(maximumFileBytes, MaximumRecordBytes));
        int bytes = Utf8.GetByteCount(line);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(path) && new FileInfo(path).Length + bytes > maximumFileBytes)
        {
            for (int index = archiveCount; index >= 2; index--)
            {
                string previous = ArchivePath(path, index - 1);
                if (!File.Exists(previous)) continue;
                TrimOversizedTail(previous, maximumFileBytes);
                File.Move(previous, ArchivePath(path, index), true);
            }
            TrimOversizedTail(path, maximumFileBytes);
            File.Move(path, ArchivePath(path, 1), true);
        }
        File.AppendAllText(path, line, Utf8);
    }

    internal static string ArchivePath(string path, int index)
        => Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + "." + index + Path.GetExtension(path));

    private static string EncodeRecord(DateTimeOffset time, string message, int maximumBytes)
    {
        string Serialize(string text, bool truncated) => (truncated
            ? JsonSerializer.Serialize(new { time, message = text, truncated = true })
            : JsonSerializer.Serialize(new { time, message = text })) + "\n";
        int upper = Math.Min(message.Length, maximumBytes);
        if (upper == message.Length)
        {
            string full = Serialize(message, false);
            if (Utf8.GetByteCount(full) <= maximumBytes) return full;
        }
        string Prefix(int length)
        {
            if (length > 0 && char.IsHighSurrogate(message[length - 1])) length--;
            return message[..length] + " [truncated]";
        }
        int lower = 0;
        while (lower < upper)
        {
            int middle = lower + (upper - lower + 1) / 2;
            if (Utf8.GetByteCount(Serialize(Prefix(middle), true)) <= maximumBytes) lower = middle;
            else upper = middle - 1;
        }
        return Serialize(Prefix(lower), true);
    }

    private static void TrimOversizedTail(string path, long maximumBytes)
    {
        if (new FileInfo(path).Length <= maximumBytes) return;
        string temporary = path + ".rotation-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string tail;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                input.Seek(-maximumBytes, SeekOrigin.End);
                using var reader = new StreamReader(input, Utf8, false);
                reader.ReadLine(); // Drop a partial UTF-8/JSON line at the seek boundary.
                tail = reader.ReadToEnd();
            }
            File.WriteAllText(temporary, tail, Utf8);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
