using System.Diagnostics;
using System.Text.Json;

namespace LyricBridge;

public sealed class MainForm : Form
{
    private readonly BluetoothServer bluetooth = new();
    private readonly PlaybackClock clock = new();
    private readonly LyricsProvider provider;
    private readonly string dataPath;
    private readonly Label connection = NewLabel("准备连接手机", 11);
    private readonly Label song = NewLabel("蓝牙歌词", 16);
    private readonly Label previous = NewLabel("", 15);
    private readonly Label current = NewLabel("先打开手机助手，完成授权并连接这台电脑", 25);
    private readonly Label next = NewLabel("", 16);
    private readonly Label timing = NewLabel("等待手机播放状态", 10);
    private readonly Label source = NewLabel("歌词会根据歌名、歌手和时长匹配", 10);
    private readonly Label offsetLabel = NewLabel("偏移 0.0 秒", 10);
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(31, 42, 55), ForeColor = Color.FromArgb(190, 206, 219), Font = new Font("Microsoft YaHei UI", 9) };
    private readonly CheckBox online = new() { Text = "自动联网匹配歌词", Checked = true, AutoSize = true, ForeColor = Color.White, Margin = new Padding(10, 10, 8, 0) };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 6 };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly Dictionary<string, double> offsets = new();
    private CancellationTokenSource? lookup;
    private List<LyricLine> lines = new();
    private string trackKey = "";
    private string plainText = "";
    private double offsetMs;
    private string connectionText = "蓝牙尚未启动";
    private bool testMode;
    public MainForm(bool demo = false)
    {
        testMode = demo;
        dataPath = Path.Combine(AppContext.BaseDirectory, "data"); Directory.CreateDirectory(dataPath);
        provider = new LyricsProvider(dataPath);
        try { var stored = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path.Combine(dataPath, "offsets.json"))); if (stored != null) foreach (var p in stored) offsets[p.Key] = p.Value; } catch { }
        Text = demo ? "蓝牙歌词 · 演示数据" : "蓝牙歌词";
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(19, 28, 39); ForeColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen; Size = new Size(940, 710); MinimumSize = new Size(760, 580);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, Padding = new Padding(26, 20, 26, 18) };
        foreach (var style in new RowStyle[] { new(SizeType.Absolute, 43), new(SizeType.Absolute, 110), new(SizeType.Absolute, 35), new(SizeType.Percent, 24), new(SizeType.Percent, 38), new(SizeType.Percent, 28), new(SizeType.Absolute, 35), new(SizeType.Absolute, 26), new(SizeType.Absolute, 9), new(SizeType.Absolute, 120) }) root.RowStyles.Add(style);
        var heading = NewLabel("蓝牙歌词  /  手机播放 · 电脑显示", 18); heading.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(heading, 0, 0);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        bar.Controls.Add(Button("连接手机", () => { testMode = false; bluetooth.Start(); }));
        bar.Controls.Add(Button("断开", () => { bluetooth.Stop(); connectionText = "已停止蓝牙接收"; }));
        bar.Controls.Add(Button("导入 LRC", ImportLyrics));
        bar.Controls.Add(Button("重新匹配", () => { if (clock.Snapshot is { Type: "media" } s) _ = FetchLyrics(s); }));
        bar.Controls.Add(Button("歌词提前", () => ChangeOffset(200)));
        bar.Controls.Add(Button("歌词延后", () => ChangeOffset(-200)));
        bar.Controls.Add(online);
        var topmost = new CheckBox { Text = "置顶", AutoSize = true, ForeColor = Color.White, Margin = new Padding(8, 10, 0, 0) };
        topmost.CheckedChanged += (_, _) => TopMost = topmost.Checked; bar.Controls.Add(topmost);
        root.Controls.Add(bar, 0, 1);
        connection.TextAlign = ContentAlignment.MiddleLeft; connection.ForeColor = Color.FromArgb(115, 225, 199); root.Controls.Add(connection, 0, 2);
        var titleBox = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        titleBox.RowStyles.Add(new RowStyle(SizeType.Percent, 65)); titleBox.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        titleBox.Controls.Add(song, 0, 0); previous.ForeColor = Color.FromArgb(139, 157, 175); titleBox.Controls.Add(previous, 0, 1);
        root.Controls.Add(titleBox, 0, 3);
        current.ForeColor = Color.FromArgb(113, 239, 205); root.Controls.Add(current, 0, 4);
        next.ForeColor = Color.FromArgb(172, 190, 205); root.Controls.Add(next, 0, 5);
        root.Controls.Add(source, 0, 6);
        var timeBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        timeBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70)); timeBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        timing.TextAlign = ContentAlignment.MiddleLeft; offsetLabel.TextAlign = ContentAlignment.MiddleRight;
        timeBar.Controls.Add(timing, 0, 0); timeBar.Controls.Add(offsetLabel, 1, 0); root.Controls.Add(timeBar, 0, 7);
        root.Controls.Add(progress, 0, 8); root.Controls.Add(details, 0, 9);
        Controls.Add(root);
        bluetooth.Status += value => OnUi(() => { connectionText = value; Log("bluetooth", value); });
        bluetooth.SnapshotReceived += value => OnUi(() => Receive(value));
        timer.Tick += (_, _) => Render(); timer.Start();
        Shown += (_, _) => { if (demo) StartDemo(); else bluetooth.Start(); };
        FormClosed += (_, _) => { lookup?.Cancel(); timer.Stop(); bluetooth.Stop(); };
    }
    private static Label NewLabel(string text, float fontSize) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Font = new Font("Microsoft YaHei UI", fontSize) };
    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 33, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(36, 53, 69), ForeColor = Color.White, Margin = new Padding(0, 3, 8, 4), Padding = new Padding(5, 0, 5, 0) };
        button.Click += (_, _) => { try { action(); } catch (Exception error) { MessageBox.Show(error.Message, "蓝牙歌词"); } };
        return button;
    }
    private void OnUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed) action(); }); } catch (InvalidOperationException) { }
    }
    private void Receive(MediaSnapshot snapshot)
    {
        clock.Update(snapshot);
        if (snapshot.Type == "idle")
        {
            lookup?.Cancel(); trackKey = ""; lines.Clear(); plainText = "";
            song.Text = "等待手机音乐"; current.Text = snapshot.Diagnostic; previous.Text = next.Text = "";
            source.Text = "在手机上播放酷狗，再查看诊断是否收到歌曲和进度";
            return;
        }
        if (trackKey != snapshot.TrackKey)
        {
            trackKey = snapshot.TrackKey; lines.Clear(); plainText = "";
            offsetMs = offsets.GetValueOrDefault(trackKey, 0);
            song.Text = snapshot.Title + (snapshot.Artist.Length > 0 ? "  ·  " + snapshot.Artist : "");
            source.Text = "尚未加载歌词";
            if (online.Checked) _ = FetchLyrics(snapshot);
        }
        Log("last-snapshot", JsonSerializer.Serialize(snapshot));
    }
    private async Task FetchLyrics(MediaSnapshot snapshot)
    {
        lookup?.Cancel(); lookup = new CancellationTokenSource();
        var token = lookup.Token; string key = snapshot.TrackKey;
        source.Text = "正在匹配歌词…";
        try
        {
            var result = await provider.FetchAsync(snapshot, token);
            if (token.IsCancellationRequested || key != trackKey) return;
            lines = result.Lines; plainText = result.PlainText; source.Text = result.Source;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (key == trackKey) source.Text = "歌词读取失败：" + error.Message; }
    }
    private void ImportLyrics()
    {
        using var dialog = new OpenFileDialog { Title = "选择当前歌曲的 LRC 歌词", Filter = "LRC 歌词|*.lrc|文本文件|*.txt|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(dialog.FileName).Length > 2_000_000) throw new IOException("歌词文件过大");
        var imported = Lrc.Parse(File.ReadAllText(dialog.FileName));
        if (imported.Count == 0) throw new FormatException("文件里没有找到 [分:秒] 时间轴，请选择 LRC 格式");
        lookup?.Cancel(); lines = imported; plainText = ""; source.Text = "本地歌词：" + Path.GetFileName(dialog.FileName);
    }
    private void ChangeOffset(double change)
    {
        offsetMs = Math.Clamp(offsetMs + change, -60000, 60000);
        if (trackKey.Length > 0) { offsets[trackKey] = offsetMs; File.WriteAllText(Path.Combine(dataPath, "offsets.json"), JsonSerializer.Serialize(offsets)); }
    }
    private void Render()
    {
        if (testMode && clock.Snapshot is { } demo) clock.Update(demo with { PositionMs = (long)(Stopwatch.GetElapsedTime(demoStart).TotalMilliseconds % 30000) });
        var s = clock.Snapshot;
        connection.Text = connectionText + (clock.Stale ? "  ·  手机状态已过期，请检查连接" : "");
        offsetLabel.Text = $"偏移 {offsetMs / 1000:+0.0;-0.0;0.0} 秒";
        if (s is null) { details.Text = "操作顺序：电脑蓝牙配对手机 → 手机助手授权通知和蓝牙 → 选择电脑 → 开始同步。\r\n歌词窗口可自由移动，不会接管手机声音。"; return; }
        if (s.Type != "media") { details.Text = s.Diagnostic; timing.Text = "没有活动歌曲"; progress.Value = 0; return; }
        double position = clock.PositionMs;
        timing.Text = s.PositionKnown ? $"{Time(position)} / {Time(s.DurationMs)}  ·  {(s.Playing ? "播放中" : "已暂停")}" : "播放器进度未知，无法可靠同步";
        progress.Value = s.PositionKnown && s.DurationMs > 0 ? (int)Math.Clamp(position / s.DurationMs * 1000, 0, 1000) : 0;
        details.Text = $"来源：{s.PackageName}\r\n歌名：{s.Title}    歌手：{s.Artist}\r\n原始进度：{s.RawPositionMs} ms    可读取：{s.PositionKnown}    数据序号：{s.Sequence}\r\n诊断：{s.Diagnostic}";
        if (clock.Stale) { previous.Text = next.Text = ""; current.Text = "手机状态已过期，等待重新连接"; return; }
        if (!s.PositionKnown) { previous.Text = next.Text = ""; current.Text = "已收到歌曲信息，但播放器未提供可靠进度"; return; }
        if (lines.Count == 0)
        {
            previous.Text = next.Text = "";
            current.Text = plainText.Length > 0 ? plainText : "等待匹配歌词，也可以导入本地 LRC"; return;
        }
        int index = Lrc.FindIndex(lines, position + offsetMs);
        previous.Text = index > 0 ? lines[index - 1].Text : "";
        current.Text = index >= 0 ? (lines[index].Text.Length == 0 ? "♪" : lines[index].Text) : "♪";
        next.Text = index + 1 < lines.Count ? lines[index + 1].Text : "";
    }
    private long demoStart;
    private void StartDemo()
    {
        demoStart = Stopwatch.GetTimestamp(); connectionText = "演示模式 · 非手机实际数据"; online.Checked = false;
        Receive(new MediaSnapshot { Version = 1, Type = "media", Title = "连接测试", Artist = "蓝牙歌词", PackageName = "demo", DurationMs = 30000, PositionMs = 0, RawPositionMs = 0, PositionKnown = true, Playing = true, Diagnostic = "仅验证界面与歌词时间轴" });
        lines = Lrc.Parse("[00:00]手机正常听歌\n[00:05]电脑同步显示歌词\n[00:10]蓝牙直接连接\n[00:15]不需要中转服务器\n[00:20]暂停和拖动后跟随系统进度\n[00:25]等待真实手机联调");
        source.Text = "内置原创演示歌词";
    }
    private static string Time(double ms) { if (ms < 0) return "--:--"; var t = TimeSpan.FromMilliseconds(ms); return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}"; }
    private void Log(string name, string value)
    {
        try { File.WriteAllText(Path.Combine(dataPath, name + ".txt"), DateTimeOffset.Now.ToString("O") + "\n" + value); } catch { }
    }
    internal void SaveScreenshot(string path)
    {
        using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(path);
    }
}
