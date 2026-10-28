using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LyricBridge;

public partial class ColorPickerWindow : Window
{
    private readonly Action<string> previewChanged;
    private bool updating = true;
    private double hue, saturation, value;
    internal string SelectedColor { get; private set; } = AppearanceColors.DefaultLyric;

    internal ColorPickerWindow(Window owner, string title, string initialColor, Action<string> previewChanged)
    {
        this.previewChanged = previewChanged;
        InitializeComponent();
        Owner = owner;
        Title = title;
        PickerTitle.Text = title;
        Height = Math.Min(450, Math.Max(240, SystemParameters.WorkArea.Height - 32));
        BuildSwatches();
        SelectedColor = AppearanceColors.Normalize(initialColor, AppearanceColors.DefaultLyric);
        ReadHsv(AppearanceColors.Parse(SelectedColor));
        RefreshVisuals(true);
        updating = false;
        Loaded += (_, _) => { MovePointer(); HexInput.Focus(); HexInput.SelectAll(); };
    }

    private void BuildSwatches()
    {
        foreach (var entry in new (string Name, string Hex)[] {
            ("月白", "#F5F3F8"), ("雾紫", "#D3C1FF"), ("海盐", "#BBDFF1"),
            ("薄荷", "#BFE2D0"), ("暖杏", "#F0D3B4"), ("蔷薇", "#E6BDCB"),
            ("纸白", "#F1EEE8"), ("石墨", "#4A4357"), ("夜色", "#211F29")
        })
        {
            var button = new Button {
                Tag = entry.Hex, Width = 29, Height = 29,
                Style = (Style)FindResource("SwatchButton"),
                ToolTip = entry.Name + " · " + entry.Hex,
                Content = new Ellipse {
                    Width = 19, Height = 19, Fill = new SolidColorBrush(AppearanceColors.Parse(entry.Hex)),
                    Stroke = new SolidColorBrush(Color.FromArgb(45,255,255,255)), StrokeThickness = 1
                }
            };
            System.Windows.Automation.AutomationProperties.SetName(button, entry.Name + " " + entry.Hex);
            button.Click += Swatch_Click;
            SwatchPanel.Children.Add(button);
        }
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button {Tag: string hex}) return;
        SelectedColor = hex;
        ReadHsv(AppearanceColors.Parse(hex));
        RefreshVisuals(true);
        previewChanged(SelectedColor);
    }

    private void ReadHsv(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g,b)), min = Math.Min(r, Math.Min(g,b)), delta = max - min;
        value = max;
        saturation = max <= 0 ? 0 : delta / max;
        if (delta <= 0) { hue = 0; return; }
        double sector = max == r ? (g-b)/delta : max == g ? (b-r)/delta+2 : (r-g)/delta+4;
        hue = (sector*60+360)%360;
    }

    private Color HsvColor()
    {
        double h = ((hue%360)+360)%360 / 60;
        double c = value*saturation, x = c*(1-Math.Abs(h%2-1)), m = value-c;
        (double r,double g,double b) = h switch {
            <1 => (c,x,0d), <2 => (x,c,0d), <3 => (0d,c,x),
            <4 => (0d,x,c), <5 => (x,0d,c), _ => (c,0d,x)
        };
        byte Channel(double channel) => (byte)Math.Clamp(Math.Round((channel+m)*255),0,255);
        return Color.FromRgb(Channel(r),Channel(g),Channel(b));
    }

    private void RefreshVisuals(bool updateHex)
    {
        bool wasUpdating = updating;
        updating = true;
        HueSlider.Value = hue;
        SelectedSwatch.Background = new SolidColorBrush(AppearanceColors.Parse(SelectedColor));
        double currentSaturation=saturation, currentValue=value;
        saturation=1;value=1;
        HueSurface.Background = new SolidColorBrush(HsvColor());
        saturation=currentSaturation;value=currentValue;
        if (updateHex) HexInput.Text = SelectedColor;
        ValidationHint.Text = "";
        AcceptButton.IsEnabled = true;
        foreach (var button in SwatchPanel.Children.OfType<Button>())
            button.BorderBrush = string.Equals(button.Tag as string,SelectedColor,StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(200,186,247)) : Brushes.Transparent;
        MovePointer();
        updating = wasUpdating;
    }

    private void MovePointer()
    {
        if (ColorPointer == null || Spectrum == null || Spectrum.ActualWidth <= 0 || Spectrum.ActualHeight <= 0) return;
        Canvas.SetLeft(ColorPointer, saturation*Spectrum.ActualWidth-8);
        Canvas.SetTop(ColorPointer, (1-value)*Spectrum.ActualHeight-8);
    }

    private void Spectrum_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ColorSurface == null) return;
        ColorSurface.Clip = new RectangleGeometry(new Rect(0,0,e.NewSize.Width,e.NewSize.Height),10,10);
        MovePointer();
    }

    private void ChoosePoint(Point point)
    {
        if (Spectrum.ActualWidth <= 0 || Spectrum.ActualHeight <= 0) return;
        saturation = Math.Clamp(point.X/Spectrum.ActualWidth,0,1);
        value = 1-Math.Clamp(point.Y/Spectrum.ActualHeight,0,1);
        SelectedColor = AppearanceColors.ToHex(HsvColor());
        RefreshVisuals(true);
        previewChanged(SelectedColor);
    }

    private void Spectrum_Down(object sender, MouseButtonEventArgs e)
    {
        Spectrum.Focus(); Spectrum.CaptureMouse(); ChoosePoint(e.GetPosition(Spectrum)); e.Handled=true;
    }
    private void Spectrum_Move(object sender, MouseEventArgs e)
    {
        if (Spectrum.IsMouseCaptured && e.LeftButton==MouseButtonState.Pressed) ChoosePoint(e.GetPosition(Spectrum));
    }
    private void Spectrum_Up(object sender, MouseButtonEventArgs e)
    {
        if (Spectrum.IsMouseCaptured) { ChoosePoint(e.GetPosition(Spectrum)); Spectrum.ReleaseMouseCapture(); }
        e.Handled=true;
    }
    private void Spectrum_KeyDown(object sender, KeyEventArgs e)
    {
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .1 : .01;
        switch (e.Key) {
            case Key.Left: saturation=Math.Max(0,saturation-step); break;
            case Key.Right: saturation=Math.Min(1,saturation+step); break;
            case Key.Up: value=Math.Min(1,value+step); break;
            case Key.Down: value=Math.Max(0,value-step); break;
            default: return;
        }
        SelectedColor = AppearanceColors.ToHex(HsvColor()); RefreshVisuals(true); previewChanged(SelectedColor); e.Handled=true;
    }
    private void Hue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating || HueSurface == null) return;
        hue = e.NewValue; SelectedColor = AppearanceColors.ToHex(HsvColor()); RefreshVisuals(true); previewChanged(SelectedColor);
    }

    private static bool TryInput(string input, out string normalized)
    {
        string text=input.Trim();
        return AppearanceColors.TryNormalize(text.StartsWith('#') ? text : "#"+text,out normalized);
    }
    private void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (updating || AcceptButton == null) return;
        if (!TryInput(HexInput.Text,out var normalized)) {
            AcceptButton.IsEnabled=false;
            ValidationHint.Text="请输入 #RGB 或 #RRGGBB 色值";
            return;
        }
        SelectedColor=normalized; ReadHsv(AppearanceColors.Parse(normalized)); RefreshVisuals(false); previewChanged(SelectedColor);
    }
    private void Hex_Focused(object sender, KeyboardFocusChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(HexInput.SelectAll));
    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (!TryInput(HexInput.Text,out var normalized)) return;
        SelectedColor=normalized;DialogResult=true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult=false;
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key==Key.Escape) { DialogResult=false;e.Handled=true; }
        else if(e.Key==Key.Enter && AcceptButton.IsEnabled) { Accept_Click(sender,e);e.Handled=true; }
    }
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if(e.Source is Button) return;
        try{DragMove();}catch(InvalidOperationException){}
    }
}
