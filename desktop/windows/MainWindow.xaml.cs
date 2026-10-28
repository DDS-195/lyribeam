using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace LyricBridge;

public sealed class Appearance
{
    public string Mode { get; set; } = "card";
    public double FontSize { get; set; } = 30;
    public string LyricColor { get; set; } = AppearanceColors.DefaultLyric;
    public string BackgroundColor { get; set; } = AppearanceColors.DefaultBackground;
    public bool ShowNext { get; set; } = true;
    public bool Topmost { get; set; } = true;
    public bool Online { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public void Normalize()
    {
        Mode = Mode == "text" ? "text" : "card";
        LyricColor = AppearanceColors.Normalize(LyricColor, AppearanceColors.DefaultLyric);
        BackgroundColor = AppearanceColors.Normalize(BackgroundColor, AppearanceColors.DefaultBackground);
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 22, 48) : 30;
        if (Left is { } l && !double.IsFinite(l)) Left = null;
        if (Top is { } t && !double.IsFinite(t)) Top = null;
    }
}

public partial class MainWindow : Window
{
    private const double OverlayWidth = 660, OverlayHeight = 160;
    private readonly BluetoothServer bluetooth = new();
    private readonly PlaybackClock clock = new();
    private readonly LyricsProvider provider;
    private readonly object lookupLogGate = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<string, double> offsets = new();
    private readonly string dataPath;
    private readonly bool demo;
    private readonly bool preview;
    private readonly LyricLookup lyricLookup;
    private List<LyricLine> lines = new();
    private string trackKey = "", plainText = "", source = "尚未加载歌词";
    private string rememberedProvided = "";
    private CancellationTokenSource? providedSave;
    private async Task RememberProvidedAsync(MediaSnapshot snapshot)
    {
        if (!ProvidedLyrics.TryValidate(snapshot,out _,out _)) return;
        string signature = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshot.TrackKey + "\n" + snapshot.ProvidedLyrics)));
        if (signature == rememberedProvided) return;
        rememberedProvided = signature;
        providedSave?.Cancel(); providedSave?.Dispose(); providedSave = new CancellationTokenSource();
        var token = providedSave.Token;
        try { await provider.RememberProvidedAsync(snapshot,token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { LogLookup("provided-cache-failed: " + error.GetType().Name); }
    }
    private string connectionText = "蓝牙正在启动";
    private double offsetMs;
    private long demoStart, trackReceivedAt;
    private bool quitting, hovering, locked, positioned, disposed, readyLogged;
    private Forms.NotifyIcon? tray;
    private SettingsWindow? settings;
    private Brush? cardBrush;
    private AppearancePalette palette = AppearanceColors.Resolve(AppearanceColors.DefaultLyric, AppearanceColors.DefaultBackground);
    private Forms.ToolStripMenuItem? lockMenu;
    private Forms.ContextMenuStrip? trayMenu;
    internal Appearance Preferences { get; private set; } = new();
    internal string SourceText => source;
    internal double OffsetMs => offsetMs;
    internal string? AppearanceSaveError { get; private set; }
    internal bool HasUnsavedAppearance => AppearanceSaveError != null;
    public MainWindow(bool demo = false, bool preview = false)
    {
        this.demo = demo; this.preview = preview;
        dataPath = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataPath);
        provider = new LyricsProvider(dataPath);
        lyricLookup = new LyricLookup((snapshot, token, bypassCache, allowNetwork) => provider.FetchAsync(snapshot, token, bypassCache, allowNetwork));
        lyricLookup.Changed += () => OnUi(ApplyLookup);
        lyricLookup.Diagnostic += LogLookup; provider.Diagnostic += LogLookup;
        if (!preview)
        {
            try { Preferences = JsonSerializer.Deserialize<Appearance>(File.ReadAllText(Path.Combine(dataPath, "appearance.json"))) ?? new(); } catch { }
        }
        Preferences.Normalize();
        try { var saved = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path.Combine(dataPath, "offsets.json"))); if (saved != null) foreach (var p in saved) offsets[p.Key] = p.Value; } catch { }
        InitializeComponent(); cardBrush = Card.Background;
        ApplyAppearance(false);
        bluetooth.Status += value => OnUi(() => { connectionText = value; Log("bluetooth", value); });
        bluetooth.SnapshotReceived += value =>
        {
            long receivedAt = Stopwatch.GetTimestamp();
            OnUi(() => ReceiveAt(value, receivedAt));
        };
        Loaded += (_, _) =>
        {
            PlaceWindow(); positioned = true;
            if (!preview) CreateTray();
            if (demo) StartDemo(); else bluetooth.Start();
            if (!demo && Preferences.Online) _ = provider.WarmUpAsync();
            timer.Start();
        };
        SizeChanged += (_, _) => { if (positioned) KeepOnScreen(); };
        Closing += OnClosing;
        Closed += (_, _) => Cleanup();
        timer.Tick += (_, _) => Render();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { Hide(); WindowState = WindowState.Normal; } };
    }
    private void OnUi(Action action)
    {
        if (disposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => { if (!disposed) action(); });
    }
    private void Receive(MediaSnapshot snapshot) => ReceiveAt(snapshot, Stopwatch.GetTimestamp());
    private void ReceiveAt(MediaSnapshot snapshot, long receivedAt)
    {
        clock.Update(snapshot, receivedAt);
        if (snapshot.Type != "media" || !demo && clock.Stale)
        {
            providedSave?.Cancel(); rememberedProvided = ""; lyricLookup.EndPlayback(); return;
        }
        bool changed = lyricLookup.Observe(snapshot);
        // Use the same completed recording metadata for lookup and display, while
        // retaining the original receipt time across the UI dispatcher queue.
        var effective = lyricLookup.Active ?? snapshot;
        clock.Update(effective, receivedAt);
        if (changed) { trackReceivedAt = Stopwatch.GetTimestamp(); readyLogged = false; }
        if (changed || demo && trackKey != snapshot.TrackKey)
        {
            trackKey = effective.TrackKey;
            offsetMs = offsets.GetValueOrDefault(trackKey, offsets.GetValueOrDefault(effective.LegacyTrackKey, 0));
            TrackTitle.Text = ChineseText.Simplify(snapshot.Title);
            TrackArtist.Text = snapshot.Artist.Length > 0 ? "  /  " + ChineseText.Simplify(snapshot.Artist) : "";
            TrackTitle.ToolTip = TrackTitle.Text + " · " + ChineseText.Simplify(snapshot.Artist);
            if (!demo) ApplyLookup();
        }
        if (!demo)
        {
            Log("last-snapshot", JsonSerializer.Serialize(snapshot with { ProvidedLyrics = "" }));
            if (lyricLookup.Active is { } active) _ = RememberProvidedAsync(active);
            _ = lyricLookup.PollAsync(Preferences.Online);
        }
    }
    private void ApplyLookup()
    {
        bool first = !readyLogged && lyricLookup.Result.Lines.Count > 0;
        lines = lyricLookup.Result.Lines; plainText = ChineseText.Simplify(lyricLookup.Result.PlainText); source = lyricLookup.Status;
        if (!demo && first && trackReceivedAt > 0)
        {
            readyLogged = true;
            LogLookup($"lyrics-ready: {clock.Snapshot?.Title}; elapsedMs={Stopwatch.GetElapsedTime(trackReceivedAt).TotalMilliseconds:0}");
        }
        Render();
    }
    private void LogLookup(string message)
    {
        if (demo) return;
        lock (lookupLogGate)
        {
            try
            {
                LookupDiagnosticLog.Append(Path.Combine(dataPath, "lookup-log.jsonl"), DateTimeOffset.Now, message);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    internal void SetAutomaticMatching(bool enabled)
    {
        Preferences.Online = enabled;
        lyricLookup.SetOnline(enabled);
        ApplyAppearance();
        if (enabled) MatchAgain();
    }
    internal void MatchAgain()
    {
        if (!demo && clock.Snapshot is { Type: "media" }) _ = lyricLookup.RefreshAsync();
    }
    internal void ChangeOffset(double change, bool reset = false)
    {
        offsetMs = reset ? 0 : Math.Clamp(offsetMs + change, -60000, 60000);
        if (trackKey.Length > 0 && !demo)
        {
            offsets[trackKey] = offsetMs;
            TryWrite("offsets.json", JsonSerializer.Serialize(offsets));
        }
        Render();
    }
    internal void ApplyAppearance(bool persist = true)
    {
        Preferences.Normalize();
        Width = OverlayWidth; Height = OverlayHeight;
        Topmost = Preferences.Topmost;
        CurrentLine.FontSize = Preferences.FontSize;
        CurrentLine.LineHeight = Preferences.FontSize * 1.3;
        NextLine.FontSize = Math.Clamp(Math.Round(Preferences.FontSize * 0.55), 12, 16);
        NextLine.LineHeight = 20;
        NextLine.Visibility = Preferences.ShowNext ? Visibility.Visible : Visibility.Collapsed;
        TrackInfo.MaxWidth = Math.Max(140, Width - 240); TrackTitle.MaxWidth = Math.Min(220, TrackInfo.MaxWidth * 0.65); TrackArtist.MaxWidth = Math.Max(55, TrackInfo.MaxWidth * 0.35 - 15);
        palette = AppearanceColors.Resolve(Preferences.LyricColor, Preferences.BackgroundColor);
        CurrentLine.Foreground = new SolidColorBrush(palette.Current);
        TrackTitle.Foreground = new SolidColorBrush(palette.Primary);
        TrackArtist.Foreground = new SolidColorBrush(palette.Muted);
        StateCaption.Foreground = TimeCaption.Foreground = new SolidColorBrush(palette.Muted);
        ProgressTrack.Background = new SolidColorBrush(palette.ProgressTrack);
        ProgressFill.Background = new SolidColorBrush(palette.Progress);
        foreach (var button in HoverControls.Children.OfType<Button>()) button.Foreground = new SolidColorBrush(palette.Primary);
        LockButton.Foreground = new SolidColorBrush(locked ? palette.Accent : palette.Muted);
        Color Glass(Color color) => Color.FromArgb(240,color.R,color.G,color.B);
        cardBrush = new LinearGradientBrush(Glass(palette.CardTop),Glass(palette.CardBottom),45);
        CurrentLine.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = palette.Shadow,
            BlurRadius = Preferences.Mode == "text" ? 5 : 3, ShadowDepth = 0, Opacity = Preferences.Mode == "text" ? .55 : .12 };
        NextLine.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = palette.Shadow,
            BlurRadius = 4, ShadowDepth = 0, Opacity = Preferences.Mode == "text" ? .45 : 0 };

        UpdateChrome(false);
        if (positioned) KeepOnScreen();
        if (persist) SaveAppearance();
    }
    private void UpdateChrome(bool animate)
    {
        bool showSurface = Preferences.Mode == "card" || hovering;
        Card.Background = showSurface ? cardBrush : Brushes.Transparent;
        Card.BorderBrush = showSurface ? new SolidColorBrush(palette.Border) : Brushes.Transparent;
        NextLine.Foreground = new SolidColorBrush(showSurface ? palette.Next : palette.Current);
        NextLine.Opacity = showSurface ? 1 : .62;
        Card.Effect = showSurface ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = palette.LightSurface ? .14 : .25 } : null;
        SetFade(TrackInfo, showSurface ? 1 : 0, animate);
        SetFade(Footer, showSurface ? 1 : 0, animate);
        SetFade(HoverControls, hovering ? 1 : 0, animate);
        HoverControls.IsHitTestVisible = hovering;
    }
    private static void SetFade(UIElement element, double target, bool animate)
    {
        double from = element.Opacity;
        element.BeginAnimation(OpacityProperty, null);
        if (animate) element.BeginAnimation(OpacityProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.HoldEnd });
        else element.Opacity = target;
    }
    private void SetLyric(string current, string next, bool animate = true)
    {
        if (CurrentLine.Text != current || NextLine.Text != next)
        {
            CurrentLine.Text = current; NextLine.Text = next; CurrentLine.ToolTip = current; NextLine.ToolTip = next;
            if (animate)
            {
                LyricBlock.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(240)));
                ((TranslateTransform)LyricBlock.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(3, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
    }
    private void Render()
    {
        if (demo && clock.Snapshot is { } d) clock.Update(d with { PositionMs = preview ? 12000 : (long)(Stopwatch.GetElapsedTime(demoStart).TotalMilliseconds % 40000) });
        var s = clock.Snapshot;
        if (!demo && s is { Type: "media" } && !clock.Stale) { _ = lyricLookup.PollAsync(Preferences.Online); source = lyricLookup.Status; }
        var connected = s != null && !clock.Stale;
        StateDot.Background = new SolidColorBrush(connected ? palette.LightSurface ? Color.FromRgb(48,116,82) : Color.FromRgb(151,207,184) : palette.Accent);
        if (s is null)
        {
            StateCaption.Text = connectionText.Contains("失败") ? "蓝牙连接需要检查 · 打开设置" : "等待手机连接";
            TimeCaption.Text = "拖动移动 · 右键设置";
            SetLyric("让音乐，轻轻留在桌面", "在手机助手里，开始蓝牙同步", false);
        }
        else if (clock.Stale)
        {
            providedSave?.Cancel(); lyricLookup.EndPlayback();
            StateCaption.Text = "连接已断开"; TimeCaption.Text = "等待手机重新连接";
            SetLyric("等待手机重新连接", "保持手机助手的蓝牙同步开启");
        }
        else if (s.Type != "media")
        {
            StateCaption.Text = "手机已连接"; TimeCaption.Text = "等待播放";
            SetLyric("下一首，随时开始", "在手机上播放音乐");
        }
        else
        {
            double position = clock.PositionMs;
            StateCaption.Text = demo ? "界面演示 · 原创示例文字" : !s.PositionKnown ? "蓝牙已连接 · 已识别歌曲" : s.Playing ? "蓝牙已连接 · 播放中" : "蓝牙已连接 · 已暂停";
            TimeCaption.Text = s.PositionKnown ? $"{Time(position)}  /  {Time(s.DurationMs)}" : "进度未知";
            ProgressFill.Width = s.PositionKnown && s.DurationMs > 0 ? Math.Max(0, Footer.ActualWidth * Math.Clamp(position / s.DurationMs, 0, 1)) : 0;
            if (!s.PositionKnown) SetLyric("已识别歌曲，暂时无法跟随", s.Source == "notification" ? "播放器目前只提供了音乐通知" : "播放器尚未提供可靠的播放进度");
            else if (lyricLookup.Result.IsInstrumental) SetLyric("纯音乐，静静聆听", "这首作品没有演唱歌词");
            else if (lines.Count == 0)
            {
                string message = !Preferences.Online ? "已暂停自动匹配歌词" : lyricLookup.Busy ? "正在查找这首歌的歌词…"
                    : lyricLookup.Result.EmptyMessage;
                SetLyric(message, !Preferences.Online ? "在设置中开启自动联网匹配"
                    : lyricLookup.WillRetry ? "正在自动重试，无需重新同步手机" : "右键打开设置，可重新匹配歌词");
            }
            else
            {
                int index = Lrc.FindIndex(lines, position + offsetMs);
                SetLyric(index >= 0 && lines[index].Text.Length > 0 ? lines[index].Text : "♪  ♪  ♪",
                    index + 1 < lines.Count ? lines[index + 1].Text : "");
            }
        }
        if (s is null || s.Type != "media") ProgressFill.Width = 0;
        if (tray != null) tray.Text = s?.Type == "media" ? ("蓝牙歌词 · " + s.Title)[..Math.Min(63, ("蓝牙歌词 · " + s.Title).Length)] : "蓝牙歌词";
        settings?.RefreshStatus();
    }
    private void StartDemo()
    {
        demoStart = Stopwatch.GetTimestamp();
        Receive(new MediaSnapshot { Version = 1, Type = "media", Title = "此刻", Artist = "桌面音乐", DurationMs = 40000, PositionKnown = true, Playing = true, PackageName = "demo", Diagnostic = "仅用于界面预览" });
        lines = Lrc.Parse("[00:00]把此刻，留给音乐\n[00:10]让旋律，轻轻落在桌面\n[00:20]每一句，都刚刚好\n[00:30]陪你走过平凡的一天");
        source = "内置原创演示文字";
        Render();
    }
    private void PlaceWindow()
    {
        var work = SystemParameters.WorkArea;
        Left = Preferences.Left ?? work.Left + (work.Width - Width) / 2;
        Top = Preferences.Top ?? work.Bottom - ActualHeight - 60;
        KeepOnScreen();
    }
    private void KeepOnScreen()
    {
        // Clamp against the working area of the monitor containing this window.
        var hwnd = new WindowInteropHelper(this).Handle;
        var screen = hwnd == 0 ? Forms.Screen.PrimaryScreen! : Forms.Screen.FromHandle(hwnd);
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = screen.WorkingArea;
        Left = Math.Clamp(double.IsFinite(Left) ? Left : 0, work.Left / dpi.DpiScaleX, Math.Max(work.Left / dpi.DpiScaleX, work.Right / dpi.DpiScaleX - ActualWidth));
        Top = Math.Clamp(double.IsFinite(Top) ? Top : 0, work.Top / dpi.DpiScaleY, Math.Max(work.Top / dpi.DpiScaleY, work.Bottom / dpi.DpiScaleY - ActualHeight));
    }
    private void SaveAppearance()
    {
        if (preview) return;
        if (positioned) { Preferences.Left = Left; Preferences.Top = Top; }
        string target = Path.Combine(dataPath, "appearance.json");
        string temporary = Path.Combine(dataPath, ".appearance-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Preferences, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, target, true);
            AppearanceSaveError = null;
        }
        catch (IOException error) { AppearanceSaveError = error.GetType().Name; }
        catch (UnauthorizedAccessException error) { AppearanceSaveError = error.GetType().Name; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        settings?.RefreshSaveStatus();
    }
    internal void RetrySaveAppearance() => SaveAppearance();
    private void TryWrite(string file, string content) { try { File.WriteAllText(Path.Combine(dataPath, file), content); } catch { } }
    private void Log(string name, string value) => TryWrite(name + ".txt", DateTimeOffset.Now.ToString("O") + "\n" + value);
    private static string Time(double ms) { if (ms < 0) return "--:--"; var t = TimeSpan.FromMilliseconds(ms); return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}"; }
    private void Window_MouseEnter(object sender, MouseEventArgs e) { hovering = true; UpdateChrome(true); }
    private void Window_MouseLeave(object sender, MouseEventArgs e) { hovering = false; UpdateChrome(true); }
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (locked || FindButton(e.OriginalSource as DependencyObject)) return;
        try { DragMove(); KeepOnScreen(); SaveAppearance(); } catch (InvalidOperationException) { }
    }
    private static bool FindButton(DependencyObject? element)
    {
        while (element != null) { if (element is Button or System.Windows.Controls.Primitives.Thumb) return true; element = VisualTreeHelper.GetParent(element); }
        return false;
    }
    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => ShowSettings();
    private void Lock_Click(object sender, RoutedEventArgs e) => SetLocked(!locked);
    private void Mode_Click(object sender, RoutedEventArgs e) { Preferences.Mode = Preferences.Mode == "card" ? "text" : "card"; ApplyAppearance(); settings?.SyncControls(); }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    internal SettingsWindow ShowSettings()
    {
        if (settings == null) { settings = new SettingsWindow(this); settings.Closed += (_, _) => settings = null; }
        if (!settings.IsVisible) settings.Show();
        settings.Activate(); return settings;
    }
    private void CreateTray()
    {
        var menu = TrayMenuStyle.Create();
        trayMenu = menu;
        menu.Items.Add("显示歌词", null, (_, _) => Dispatcher.Invoke(ShowOverlay)).Name = "show";
        menu.Items.Add("收起歌词", null, (_, _) => Dispatcher.Invoke(Hide)).Name = "hide";
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("显示设置…", null, (_, _) => Dispatcher.Invoke(() => ShowSettings())).Name = "settings";
        lockMenu = new Forms.ToolStripMenuItem("锁定歌词（鼠标穿透）") { Name = "lock" };
        lockMenu.Click += (_, _) => Dispatcher.Invoke(() => SetLocked(!locked)); menu.Items.Add(lockMenu);
        menu.Items.Add("重置到屏幕中央", null, (_, _) => Dispatcher.Invoke(() => { SetLocked(false); Preferences.Left = Preferences.Top = null; PlaceWindow(); ShowOverlay(); SaveAppearance(); })).Name = "reset";
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Quit)).Name = "exit";
        TrayMenuStyle.Prepare(menu);
        tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/LyricBridge.ico")).Stream), Text = "蓝牙歌词", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowOverlay);
    }
    private void ShowOverlay() { Show(); WindowState = WindowState.Normal; }
    internal void ActivateFromLauncher()
    {
        if (disposed || quitting || Dispatcher.HasShutdownStarted) return;
        ShowOverlay(); Activate();
    }
    internal void SetLocked(bool value)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new nint(value ? style | 0x20 : style & ~0x20));
        locked = value;
        if (lockMenu != null) lockMenu.Checked = value;
        LockButton.Foreground = new SolidColorBrush(value ? palette.Accent : palette.Muted);
        if (value) { hovering = false; UpdateChrome(false); }
    }
    internal void Quit() { quitting = true; Close(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!quitting && !preview) { e.Cancel = true; Hide(); }
    }
    private void Cleanup()
    {
        disposed = true; providedSave?.Cancel(); providedSave?.Dispose(); SaveAppearance(); lyricLookup.Dispose(); timer.Stop(); bluetooth.Stop();
        settings?.Close(); tray?.Dispose(); trayMenu?.Dispose();
    }
    internal void SaveScreenshot(string path)
    {
        UpdateLayout(); Capture(CaptureRoot, path);
    }
    internal bool HasSyncedLyrics => clock.Snapshot is { Type: "media" } && !clock.Stale && lines.Count > 0;
    internal void SaveLiveVerification(string path)
    {
        SaveScreenshot(path);
        var s = clock.Snapshot;
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
        {
            visible = IsVisible, width = ActualWidth, height = ActualHeight,
            mode = Preferences.Mode, connected = s != null && !clock.Stale,
            package = s?.PackageName, positionKnown = s?.PositionKnown,
            positionMs = clock.PositionMs, lyricCount = lines.Count, simplifiedChinese = lines.All(l => ChineseText.Simplify(l.Text) == l.Text), source
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static void Capture(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 1.5), (int)Math.Ceiling(element.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private async Task CaptureTrayPreviews(string directory)
    {
        var menu = trayMenu ?? throw new InvalidOperationException("Tray menu unavailable");
        TrayMenuStyle.Prepare(menu);
        var anchor = PointToScreen(new Point(ActualWidth-20,20));
        menu.Show(new System.Drawing.Point((int)anchor.X-menu.Width,(int)anchor.Y-menu.Height));
        await Task.Delay(100);
        void CaptureMenu(string name)
        {
            using var bitmap = new System.Drawing.Bitmap(menu.Width,menu.Height);
            menu.DrawToBitmap(bitmap,new System.Drawing.Rectangle(System.Drawing.Point.Empty,menu.Size));
            bitmap.Save(Path.Combine(directory,name),System.Drawing.Imaging.ImageFormat.Png);
        }
        CaptureMenu("tray-menu.png");
        menu.Items["settings"]!.Select(); CaptureMenu("tray-menu-hover.png");
        SetLocked(true); lockMenu!.Select(); CaptureMenu("tray-menu-locked.png");
        lockMenu.PerformClick();
        bool trayUnlocks = !locked && !lockMenu.Checked;
        menu.Close();
        menu.Items["hide"]!.PerformClick(); bool menuHides = !IsVisible;
        menu.Items["show"]!.PerformClick(); bool menuShows = IsVisible;
        string[] names = { "show","hide","settings","lock","reset","exit" };
        bool actionsPreserved = names.All(name => menu.Items[name] is Forms.ToolStripMenuItem);
        File.WriteAllText(Path.Combine(directory,"tray-menu-checks.json"),JsonSerializer.Serialize(new {
            success = trayUnlocks && menuHides && menuShows && actionsPreserved && menu.AutoClose,
            actionsPreserved, actionCount = menu.Items.OfType<Forms.ToolStripMenuItem>().Count(),
            separatorCount = menu.Items.OfType<Forms.ToolStripSeparator>().Count(),
            trayUnlocks, menuHides, menuShows, autoClose = menu.AutoClose,
            width = menu.Width, height = menu.Height, dpi = menu.DeviceDpi
        },new JsonSerializerOptions {WriteIndented=true}));
    }
    internal async Task CapturePreviews(string directory)
    {
        Directory.CreateDirectory(directory);
        bool trayIconAvailable;
        try
        {
            CreateTray();
            trayIconAvailable = tray is { Visible: true, Icon: not null };
            if (!trayIconAvailable) throw new InvalidOperationException("Tray icon was not created.");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "ui-checks.json"), JsonSerializer.Serialize(new { success = false, trayIconAvailable = false, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Quit(); return;
        }
        await CaptureTrayPreviews(directory);
        hovering = false; Preferences.Mode = "card"; ApplyAppearance(false); UpdateLayout();
        await Task.Delay(400); SaveScreenshot(Path.Combine(directory, "card.png"));
        hovering = true; UpdateChrome(false); SaveScreenshot(Path.Combine(directory, "card-hover.png"));
        hovering = false; Preferences.Mode = "text"; ApplyAppearance(false); SaveScreenshot(Path.Combine(directory, "text.png"));
        LockButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool passThrough = locked && lockMenu?.Checked == true && (GetWindowLongPtr(new WindowInteropHelper(this).Handle, -20).ToInt64() & 0x20) != 0;
        lockMenu?.PerformClick();
        bool trayUnlocks = !locked && lockMenu?.Checked == false && (GetWindowLongPtr(new WindowInteropHelper(this).Handle, -20).ToInt64() & 0x20) == 0;
        passThrough &= trayUnlocks;
        Hide(); bool wasHidden = !IsVisible;
        ActivateFromLauncher(); UpdateLayout();
        bool launcherRestoresWindow = wasHidden && IsVisible && WindowState == WindowState.Normal;
        Preferences.Mode = "card"; ApplyAppearance(false);
        string beforeSettings = JsonSerializer.Serialize(Preferences);
        var panel = ShowSettings(); bool settingsPreserveAppearance = beforeSettings == JsonSerializer.Serialize(Preferences);
        var topToggle = (CheckBox)panel.FindName("TopToggle");
        bool topmostDefault = new Appearance().Topmost && topToggle.IsChecked == true && Topmost;
        topToggle.IsChecked = false; bool topmostOff = !Preferences.Topmost && !Topmost;
        topToggle.IsChecked = true; bool topmostOn = Preferences.Topmost && Topmost;
        bool topmostOnlyInSettings = FindName("PinButton") == null && HoverControls.Children.OfType<Button>().Count() == 4;
        await Task.Delay(200); panel.UpdateLayout(); Capture(panel.PreviewRoot, Path.Combine(directory, "settings.png"));
        panel.Close();
        bool noSizeControls = panel.FindName("WidthSlider") == null && panel.FindName("HeightSlider") == null && FindName("ResizeGrip") == null;
        bool fixedSize = ActualWidth == OverlayWidth && ActualHeight == OverlayHeight;
        Preferences.FontSize = 48; ApplyAppearance(false);
        SetLyric("把每一个平凡的瞬间，都轻轻写进今天的旋律", "下一句依旧淡淡陪伴，让音乐留在此刻", false);
        UpdateLayout(); SaveScreenshot(Path.Combine(directory, "long-line.png"));
        var currentBounds = CurrentLine.TransformToAncestor(CaptureRoot).TransformBounds(new Rect(CurrentLine.RenderSize));
        var nextBounds = NextLine.TransformToAncestor(CaptureRoot).TransformBounds(new Rect(NextLine.RenderSize));
        var footerBounds = Footer.TransformToAncestor(CaptureRoot).TransformBounds(new Rect(Footer.RenderSize));
        bool fullLineVisible = currentBounds.Left >= 30 && currentBounds.Right <= ActualWidth - 30 && currentBounds.Bottom <= nextBounds.Top && nextBounds.Bottom <= footerBounds.Top;
        bool fixedWithLongLine = ActualWidth == OverlayWidth && ActualHeight == OverlayHeight;
        Preferences.ShowNext = false; ApplyAppearance(false); UpdateLayout();
        SaveScreenshot(Path.Combine(directory, "single-line.png"));
        bool singleLine = NextLine.Visibility == Visibility.Collapsed;
        File.WriteAllText(Path.Combine(directory, "ui-checks.json"), JsonSerializer.Serialize(new
        {
            success = trayIconAvailable && passThrough && fullLineVisible && singleLine && settingsPreserveAppearance && fixedSize && fixedWithLongLine && noSizeControls && launcherRestoresWindow && topmostDefault && topmostOff && topmostOn && topmostOnlyInSettings,
            topmostOnlyInSettings, topmostDefaultChecked = topmostDefault, topmostCanDisable = topmostOff, topmostCanEnable = topmostOn,
            trayIconAvailable, width = ActualWidth, height = ActualHeight, fixedSize, fixedWithLongLine, noSizeControls, settingsPreserveAppearance,
            passThroughStyle = passThrough, longLyricsFitWithoutClipping = fullLineVisible, singleLineOption = singleLine, launcherRestoresWindow,
            currentBounds = new { currentBounds.Left, currentBounds.Top, currentBounds.Right, currentBounds.Bottom },
            nextBounds = new { nextBounds.Top, nextBounds.Bottom }, footerTop = footerBounds.Top
        }, new JsonSerializerOptions { WriteIndented = true }));
        Quit();
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
