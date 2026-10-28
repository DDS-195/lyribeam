using System.Text;
using System.Text.Json;

namespace LyricBridge;

internal static class LookupDiagnosticLogRegressionTests
{
    internal static Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var time = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.FromHours(8));
        string Area(string name)
        {
            string directory = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); return Path.Combine(directory, "lookup-log.jsonl");
        }
        List<JsonElement> Read(string path)
        {
            var rows = new List<JsonElement>();
            for (int index = LookupDiagnosticLog.ArchiveCount; index >= 0; index--)
            {
                string file = index == 0 ? path : LookupDiagnosticLog.ArchivePath(path, index);
                if (!File.Exists(file)) continue;
                foreach (string line in File.ReadLines(file))
                {
                    using var json = JsonDocument.Parse(line);
                    rows.Add(json.RootElement.Clone());
                }
            }
            return rows;
        }
        string history = Area("history");
        for (int index = 0; index < 1000; index++)
            LookupDiagnosticLog.Append(history, time.AddMilliseconds(index), "fixture-" + index + ": " + new string('x', 128));
        var historyRows = Read(history);
        checks["Diagnostic history beyond the old 128 KiB boundary retains every complete entry"] = historyRows.Count == 1000
            && historyRows[0].GetProperty("message").GetString()!.StartsWith("fixture-0:")
            && historyRows[^1].GetProperty("message").GetString()!.StartsWith("fixture-999:");
        LookupDiagnosticLog.Append(history, time.AddSeconds(2), "after-reopen");
        historyRows = Read(history);
        checks["A later append preserves existing diagnostic history"] = historyRows.Count == 1001
            && historyRows[^1].GetProperty("message").GetString() == "after-reopen";

        string rotating = Area("rotation");
        for (int index = 0; index < 40; index++)
            LookupDiagnosticLog.Append(rotating, time.AddSeconds(index), "entry-" + index + ": " + new string('x', 70), 512);
        var rotatingRows = Read(rotating);
        var files = Directory.GetFiles(Path.GetDirectoryName(rotating)!, "*.jsonl");
        checks["Rotation bounds the current file and two archives without malformed JSON"] = files.Length == 3
            && files.All(file => new FileInfo(file).Length <= 512) && rotatingRows.Count > 1;
        var indexes = rotatingRows.Select(row => int.Parse(row.GetProperty("message").GetString()!.Split(':')[0][6..])).ToArray();
        checks["Archives retain recent entries in chronological order and include the latest event"] = indexes.SequenceEqual(indexes.OrderBy(index => index))
            && indexes.Distinct().Count() == indexes.Length && indexes[0] > 0 && indexes[^1] == 39;

        string oversized = Area("oversized-record");
        string message = string.Concat(Enumerable.Repeat("诊断\t\"\\\n🎵", 3000));
        LookupDiagnosticLog.Append(oversized, time, message, 512);
        var oversizedRows = Read(oversized);
        checks["Oversized Unicode and escaped diagnostics become a bounded valid JSON record"] = new FileInfo(oversized).Length <= 512
            && oversizedRows.Count == 1 && oversizedRows[0].TryGetProperty("truncated", out var flag) && flag.GetBoolean()
            && oversizedRows[0].GetProperty("message").GetString()!.EndsWith(" [truncated]");
        checks["JSONL remains UTF-8 without a byte order mark"] = !File.ReadAllBytes(oversized).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF });

        string previousLarge = Area("oversized-old-file");
        File.WriteAllLines(previousLarge, Enumerable.Range(0, 100).Select(index => JsonSerializer.Serialize(new { time, message = "previous-" + index + new string('x', 100) })), new UTF8Encoding(false));
        LookupDiagnosticLog.Append(previousLarge, time.AddMinutes(1), "latest-after-large-file", 512);
        var repaired = Read(previousLarge);
        checks["An oversized pre-existing file is retained as bounded complete-line history"] = repaired.Count >= 2
            && repaired[^1].GetProperty("message").GetString() == "latest-after-large-file"
            && Directory.GetFiles(Path.GetDirectoryName(previousLarge)!, "*.jsonl").All(file => new FileInfo(file).Length <= 512)
            && !Directory.GetFiles(Path.GetDirectoryName(previousLarge)!, "*.tmp").Any();
        return Task.FromResult(checks);
    }
}
