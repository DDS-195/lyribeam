using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LyricBridge;

public partial class SettingsWindow : Window
{
    private readonly MainWindow host;
    private bool updating = true;
    private readonly List<Button> presetButtons = new();
    private sealed record ColorPreset(string Name, string Lyric, string Background);
    private static readonly ColorPreset[] Presets = {
        new("月白", AppearanceColors.DefaultLyric, AppearanceColors.DefaultBackground),
        new("雾紫", "#E5D9FF", "#24202E"),
        new("松青", "#C9E4D8", "#192620"),
        new("暖砂", "#F1DFC4", "#29231E"),
        new("纸白", "#383340", "#F2EFEA")
    };
    internal FrameworkElement PreviewRoot => PreviewRootElement;
    internal int PaletteCount => Presets.Length;
    public SettingsWindow(MainWindow host)
    {
        this.host = host;
        InitializeComponent();
        Height = Math.Min(740, SystemParameters.WorkArea.Height - 24);
        if (host.IsVisible) Owner = host;
        VersionValue.Text = "蓝牙歌词  /  " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        CreatePresets();
        SyncControls(); RefreshStatus();
    }
    private void CreatePresets()
    {
        for (int index = 0; index < Presets.Length; index++)
        {
            int selected = index;
            var preset = Presets[index];
            var palette = AppearanceColors.Resolve(preset.Lyric, preset.Background);
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = "Aa", FontSize = 16, FontWeight = FontWeights.SemiBold,
                Foreground = Brush(palette.Current), HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(new TextBlock { Text = preset.Name, FontSize = 10, Margin = new Thickness(0,3,0,0),
                Foreground = Brush(palette.Muted), HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button { Content = content, Background = Brush(AppearanceColors.Parse(preset.Background)),
                BorderThickness = new Thickness(1.5), Style = (Style)FindResource("PalettePreset"),
                ToolTip = preset.Name + " · 点击应用配色" };
            System.Windows.Automation.AutomationProperties.SetName(button, "配色：" + preset.Name);
            button.Click += (_, _) => ApplyPreset(selected, true);
            presetButtons.Add(button); PresetPanel.Children.Add(button);
        }
    }
    internal void SyncControls()
    {
        updating = true;
        var p = host.Preferences;
        FontSlider.Value = p.FontSize; FontValue.Text = $"{p.FontSize:0} px";
        NextToggle.IsChecked = p.ShowNext; TopToggle.IsChecked = p.Topmost; OnlineToggle.IsChecked = p.Online;
        CardMode.Background = p.Mode == "card" ? new SolidColorBrush(Color.FromArgb(40,200,186,247)) : Brushes.Transparent;
        TextMode.Background = p.Mode == "text" ? new SolidColorBrush(Color.FromArgb(40,200,186,247)) : Brushes.Transparent;
        CardMode.BorderBrush = p.Mode == "card" ? new SolidColorBrush(Color.FromArgb(100,200,186,247)) : Brushes.Transparent;
        TextMode.BorderBrush = p.Mode == "text" ? new SolidColorBrush(Color.FromArgb(100,200,186,247)) : Brushes.Transparent;
        ModeHint.Text = p.Mode == "text" ? "只留下歌词，背景在悬停时出现。" : "柔和衬底，长句自动缩小以适应卡片。";
        RefreshColors();
        updating = false;
    }
    private static SolidColorBrush Brush(Color color) => new(color);
    private void RefreshColors()
    {
        var p = host.Preferences;
        var palette = AppearanceColors.Resolve(p.LyricColor,p.BackgroundColor);
        ColorPreview.Background = new LinearGradientBrush(palette.CardTop,palette.CardBottom,45);
        ColorPreview.BorderBrush = Brush(palette.Border);
        PreviewCaption.Foreground = Brush(palette.Muted);
        PreviewLyric.Foreground = Brush(palette.Current);
        PreviewNext.Foreground = Brush(palette.Next);
        LyricSwatch.Fill = Brush(palette.Current);
        BackgroundSwatch.Fill = Brush(AppearanceColors.Parse(p.BackgroundColor));
        LyricColorValue.Text = p.LyricColor; BackgroundColorValue.Text = p.BackgroundColor;
        RefreshSaveStatus();
        for (int i=0;i<Presets.Length;i++)
        {
            bool selected = p.LyricColor == Presets[i].Lyric && p.BackgroundColor == Presets[i].Background;
            presetButtons[i].BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(211,192,255))
                : new SolidColorBrush(Color.FromArgb(35,255,255,255));
        }
    }
    internal void RefreshSaveStatus()
    {
        bool unsaved = host.HasUnsavedAppearance;
        var p = host.Preferences;
        ColorHint.Text = unsaved ? "设置暂未保存，点击重试。"
            : p.Mode == "card" && AppearanceColors.Contrast(AppearanceColors.Parse(p.LyricColor),AppearanceColors.Parse(p.BackgroundColor)) < 3
                ? "文字与背景比较接近，换个歌词颜色会更清楚。" : "点击色块自定义，修改时实时预览。";
        ColorHint.Foreground = unsaved ? new SolidColorBrush(Color.FromRgb(211,192,255)) : (Brush)FindResource("Muted");
        ColorHint.Cursor = unsaved ? Cursors.Hand : null;
        ColorHint.Focusable = unsaved;
        ColorHint.ToolTip = unsaved ? "点击重试保存当前设置" : null;
    }
    private void ColorHint_Click(object sender,MouseButtonEventArgs e)
    {
        if (!host.HasUnsavedAppearance) return;
        host.RetrySaveAppearance(); RefreshSaveStatus(); e.Handled = true;
    }
    private void ColorHint_KeyDown(object sender,KeyEventArgs e)
    {
        if (!host.HasUnsavedAppearance || e.Key is not (Key.Enter or Key.Space)) return;
        host.RetrySaveAppearance(); RefreshSaveStatus(); e.Handled = true;
    }
    private void ApplyPreset(int index,bool persist)
    {
        var preset=Presets[index];host.Preferences.LyricColor=preset.Lyric;host.Preferences.BackgroundColor=preset.Background;
        host.ApplyAppearance(persist);SyncControls();
    }
    internal void PreviewPreset(int index) => ApplyPreset(index,false);
    internal void RefreshStatus()
    {
        OffsetValue.Text = $"{host.OffsetMs / 1000:+0.0;-0.0;0.0} 秒";
        SourceValue.Text = host.SourceText;
    }
    private void SetColor(bool lyric,string value)
    {
        if(lyric)host.Preferences.LyricColor=value;else host.Preferences.BackgroundColor=value;
    }
    private void EditColor(bool lyric)
    {
        string original = lyric ? host.Preferences.LyricColor : host.Preferences.BackgroundColor;
        bool confirmed = false;
        var picker = new ColorPickerWindow(this,lyric ? "歌词颜色" : "卡片背景",original,color => {
            SetColor(lyric,color);host.ApplyAppearance(false);RefreshColors();
        });
        try
        {
            confirmed = picker.ShowDialog() == true;
            if(confirmed) SetColor(lyric,picker.SelectedColor);
        }
        finally
        {
            if(!confirmed) SetColor(lyric,original);
            host.ApplyAppearance(confirmed);SyncControls();
        }
    }
    private void LyricColor_Click(object sender,RoutedEventArgs e) => EditColor(true);
    private void BackgroundColor_Click(object sender,RoutedEventArgs e) => EditColor(false);
    private void ResetColors_Click(object sender,RoutedEventArgs e)
    {
        host.Preferences.LyricColor=AppearanceColors.DefaultLyric;host.Preferences.BackgroundColor=AppearanceColors.DefaultBackground;
        host.ApplyAppearance();SyncControls();
    }
    private void CardMode_Click(object sender,RoutedEventArgs e) { host.Preferences.Mode="card";host.ApplyAppearance();SyncControls(); }
    private void TextMode_Click(object sender,RoutedEventArgs e) { host.Preferences.Mode="text";host.ApplyAppearance();SyncControls(); }
    private void Font_Changed(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(updating || FontValue==null)return;
        host.Preferences.FontSize=Math.Round(e.NewValue);FontValue.Text=$"{e.NewValue:0} px";host.ApplyAppearance();
    }
    private void Toggle_Changed(object sender,RoutedEventArgs e)
    {
        if(updating || NextToggle==null || TopToggle==null)return;
        host.Preferences.ShowNext=NextToggle.IsChecked==true;host.Preferences.Topmost=TopToggle.IsChecked==true;host.ApplyAppearance();
    }
    private void Online_Changed(object sender,RoutedEventArgs e)
    {
        if(updating)return;host.SetAutomaticMatching(OnlineToggle.IsChecked==true);
    }
    private void Later_Click(object sender,RoutedEventArgs e) => host.ChangeOffset(-200);
    private void Earlier_Click(object sender,RoutedEventArgs e) => host.ChangeOffset(200);
    private void Reset_Click(object sender,RoutedEventArgs e) => host.ChangeOffset(0,true);
    private void Match_Click(object sender,RoutedEventArgs e) => host.MatchAgain();
    private void Close_Click(object sender,RoutedEventArgs e) => Close();
    private void Header_Drag(object sender,MouseButtonEventArgs e)
    {
        if(e.Source is Button)return;try{DragMove();}catch(InvalidOperationException){}
    }
}
