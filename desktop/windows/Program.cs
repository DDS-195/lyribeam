using System.Text;
using System.Text.Json;

namespace LyricBridge;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--regression-test") return RegressionTests.RunAsync(args[1]).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args[1]);
            if (args.Length > 1 && args[0] == "--tray-lifecycle-test") return TrayMenuLifecycleVerification.Run(args[1]);
            if (args.Length > 1 && args[0] == "--tray-focus-target") return TrayMenuLifecycleVerification.FocusTarget(args[1]);
            if (args.Length > 0 && args[0] == "--probe-bluetooth") return Probe(args[1]);
            if (args.Length > 0 && args[0] == "--test-lyrics") return TestLyrics(args[1], args.Length > 2 ? args[2] : null, args.Contains("--warm")).GetAwaiter().GetResult();
            if (args.Length > 1 && args[0] == "--activation-test-signal") return SingleInstanceActivation.SignalIsolatedTest(args[1]);
            bool preview = args.Contains("--ui-test") || args.Contains("--demo") || args.Contains("--color-ui-test");
            using var activation = preview ? null : new SingleInstanceActivation();
            if (activation?.IsPrimary == false) { activation.RequestActivation(); return 0; }
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
            var form = new MainWindow(preview, preview);
            activation?.Listen(() =>
            {
                if (app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return;
                try { app.Dispatcher.BeginInvoke(() => form.ActivateFromLauncher()); }
                catch (InvalidOperationException) when (app.Dispatcher.HasShutdownStarted) { }
            });
            if (args.Length > 1 && args[0] == "--ui-test")
            {
                form.Loaded += async (_, _) => await form.CapturePreviews(args[1]);
            }
            if (args.Length > 1 && args[0] == "--color-ui-test")
                form.Loaded += async (_, _) => await form.CaptureColorPreviews(args[1]);
            if (args.Length > 1 && args[0] == "--capture-live")
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                int attempts = 0;
                timer.Tick += (_, _) => { form.SaveLiveVerification(args[1]); if (form.HasSyncedLyrics || ++attempts >= 10) timer.Stop(); };
                form.Loaded += (_, _) => timer.Start();
            }
            app.Run(form);
            return 0;
        }
        catch (Exception error)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "startup-error.txt");
            File.WriteAllText(path, error.ToString());
            if (args.Length == 0) System.Windows.MessageBox.Show("程序启动失败，详情已写入 startup-error.txt\n" + error.Message);
            return 1;
        }
    }
    private static int Probe(string output)
    {
        using var done = new ManualResetEventSlim(); using var server = new BluetoothServer();
        bool success = false; string message = "蓝牙服务没有响应";
        server.Status += text => { message = text; if (text.Contains("已就绪") || text.Contains("失败")) { success = text.Contains("已就绪"); done.Set(); } };
        server.Start(); done.Wait(TimeSpan.FromSeconds(15)); server.Stop();
        File.WriteAllText(output, JsonSerializer.Serialize(new { success, message, serviceUuid = BluetoothServer.ServiceId, nativeAddressBytes = BluetoothServer.AddressSize }, new JsonSerializerOptions { WriteIndented = true }));
        return success ? 0 : 1;
    }
    private static async Task<int> TestLyrics(string output, string? input, bool warm)
    {
        var songs = input == null ? new List<MediaSnapshot> { new() { Title = "晴天", Artist = "周杰伦", DurationMs = 269000 } }
            : JsonSerializer.Deserialize<List<MediaSnapshot>>(File.ReadAllText(input), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var checks = new List<object>(); bool success = songs.Count > 0;
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var provider = new LyricsProvider(Path.Combine(Path.GetDirectoryName(output)!, "provider-live-" + Guid.NewGuid().ToString("N")));
        provider.Diagnostic += diagnostics.Enqueue;
        var start = System.Diagnostics.Stopwatch.StartNew();
        if (warm) await provider.WarmUpAsync();
        long warmupMs = start.ElapsedMilliseconds;
        foreach (var song in songs)
        {
            diagnostics.Clear();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await provider.FetchAsync(song, CancellationToken.None);
            long elapsedMs = watch.ElapsedMilliseconds;
            var cacheWatch = System.Diagnostics.Stopwatch.StartNew();
            var cached = result.Lines.Count > 0 ? await provider.FetchAsync(song, CancellationToken.None) : result;
            long cacheMs = cacheWatch.ElapsedMilliseconds;
            success &= result.Lines.Count > 0 || result.IsInstrumental;
            checks.Add(new { song.Title, song.Artist, song.DurationMs, source = result.Source, lineCount = result.Lines.Count,
                elapsedMs, cacheMs, result.IsInstrumental, firstTimeMs = result.Lines.FirstOrDefault()?.TimeMs,
                lastTimeMs = result.Lines.LastOrDefault()?.TimeMs, simplifiedChinese = result.Lines.All(line => ChineseText.Simplify(line.Text) == line.Text), diagnostics = diagnostics.ToArray() });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { success, warm, warmupMs, checks }, new JsonSerializerOptions { WriteIndented = true }));
        return success ? 0 : 1;
    }
}

internal static class SelfTests
{
    public static int Run(string output)
    {
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool condition) { checks[name] = condition; if (!condition) throw new InvalidOperationException(name); }
        try
        {
            var framer = new LineFramer(); var received = new List<string>();
            byte[] data = Encoding.UTF8.GetBytes("{\"title\":\"中文歌词\"}\nsecond\r\n");
            foreach (byte b in data) received.AddRange(framer.Append(new[] { b }, 1));
            Check("UTF8 fragmented Bluetooth frames", received.SequenceEqual(new[] { "{\"title\":\"中文歌词\"}", "second" }));
            bool oversized = false; try { var bytes = Encoding.UTF8.GetBytes(new string('x', LineFramer.MaximumCharacters + 1)); new LineFramer().Append(bytes, bytes.Length); } catch (FormatException) { oversized = true; }
            Check("Reject unbounded input", oversized);
            var snapshot = MediaSnapshot.Parse("{\"version\":1,\"type\":\"media\",\"title\":\"测试\",\"positionKnown\":true,\"positionMs\":25000,\"durationMs\":30000,\"playing\":false}");
            var clock = new PlaybackClock(); clock.Update(snapshot);
            Check("Paused clock preserves mid-song position", clock.PositionMs == 25000);
            clock.Update(snapshot with { PositionKnown = false, PositionMs = -1 });
            Check("Unknown position is not guessed", clock.PositionMs == -1);
            clock.Update(snapshot with { PositionMs = 29999, Playing = true }); Thread.Sleep(8);
            Check("Clock clamps to duration", clock.PositionMs <= 30000);
            bool invalid = false; try { MediaSnapshot.Parse("{\"version\":2,\"type\":\"media\"}"); } catch (FormatException) { invalid = true; }
            Check("Reject incompatible protocol version", invalid);
            var lyrics = Lrc.Parse("[offset:-100]\n[00:01.2][00:03.25]第一句\n[00:05.123]第二句");
            Check("LRC multi-tags fractions and offset", lyrics.Count == 3 && lyrics[0].TimeMs == 1100 && lyrics[1].TimeMs == 3150 && lyrics[2].TimeMs == 5023);
            Check("LRC seek backward and before first line", Lrc.FindIndex(lyrics, 0) == -1 && Lrc.FindIndex(lyrics, 4000) == 1 && Lrc.FindIndex(lyrics, 1200) == 0);
            Check("Reject mismatched recording duration", LyricsProvider.Score(new MediaSnapshot { Title = "晴天", Artist = "周杰伦", DurationMs = 269000 }, "晴天", "周杰伦", 180000) < 65);
            Check("Native Bluetooth address matches Windows SDK", BluetoothServer.AddressSize == 30);
            File.WriteAllText(output, JsonSerializer.Serialize(new { success = true, checks }, new JsonSerializerOptions { WriteIndented = true })); return 0;
        }
        catch (Exception error) { File.WriteAllText(output, JsonSerializer.Serialize(new { success = false, checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true })); return 1; }
    }
}
