using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
namespace LyricBridge;
public partial class MainWindow
{
    internal async Task CaptureColorPreviews(string directory)
    {
        Directory.CreateDirectory(directory);timer.Stop();
        Preferences.Mode="card";hovering=false;ApplyAppearance(false);
        SetLyric("让音乐，轻轻留在桌面","下一句，依然轻轻陪伴",false);UpdateLayout();
        string before=JsonSerializer.Serialize(Preferences);
        var panel=ShowSettings();await Task.Delay(150);panel.UpdateLayout();
        bool openingPreservesSettings=before==JsonSerializer.Serialize(Preferences);
        Capture(panel.PreviewRoot,Path.Combine(directory,"settings.png"));
        bool allPresetsRender=true,lightSurfaceReadable=true;
        for(int index=0;index<panel.PaletteCount;index++)
        {
            panel.PreviewPreset(index);UpdateLayout();await Task.Delay(40);
            SaveScreenshot(Path.Combine(directory,"palette-"+index+".png"));
            allPresetsRender &= ((SolidColorBrush)CurrentLine.Foreground).Color==AppearanceColors.Parse(Preferences.LyricColor)
                && Card.Background is LinearGradientBrush && ActualWidth==660 && ActualHeight==160;
            if(palette.LightSurface) lightSurfaceReadable &= AppearanceColors.Contrast(((SolidColorBrush)TrackTitle.Foreground).Color,palette.CardTop)>=4.5
                && AppearanceColors.Contrast(((SolidColorBrush)TimeCaption.Foreground).Color,palette.CardBottom)>=4.5;
        }
        var lyricButton=(FrameworkElement)panel.FindName("LyricColorButton");
        var backgroundButton=(FrameworkElement)panel.FindName("BackgroundColorButton");
        bool colorControlsVisible=lyricButton.ActualWidth>100&&backgroundButton.ActualWidth>100;
        bool noSizeControls=panel.FindName("WidthSlider")==null&&panel.FindName("HeightSlider")==null;
        Preferences.Mode="text";hovering=false;ApplyAppearance(false);UpdateLayout();
        bool textModeUsesChosenColor=Card.Background==Brushes.Transparent
            && ((SolidColorBrush)CurrentLine.Foreground).Color==AppearanceColors.Parse(Preferences.LyricColor)
            && ((SolidColorBrush)NextLine.Foreground).Color==AppearanceColors.Parse(Preferences.LyricColor);
        SaveScreenshot(Path.Combine(directory,"text-color.png"));
        Preferences.Mode="card";panel.PreviewPreset(1);UpdateLayout();panel.UpdateLayout();
        Capture(panel.PreviewRoot,Path.Combine(directory,"settings-purple.png"));
        SaveScreenshot(Path.Combine(directory,"card-purple.png"));
        bool success=openingPreservesSettings&&allPresetsRender&&lightSurfaceReadable&&colorControlsVisible&&noSizeControls&&textModeUsesChosenColor;
        File.WriteAllText(Path.Combine(directory,"color-ui-checks.json"),JsonSerializer.Serialize(new{
            success,openingPreservesSettings,allPresetsRender,lightSurfaceReadable,colorControlsVisible,noSizeControls,textModeUsesChosenColor,
            width=ActualWidth,height=ActualHeight,settingsWidth=panel.ActualWidth,settingsHeight=panel.ActualHeight,presetCount=panel.PaletteCount
        },new JsonSerializerOptions{WriteIndented=true}));
        var dialogChecks=panel.VerifyColorDialogs(directory,(element,name)=>Capture(element,Path.Combine(directory,name)));
        File.WriteAllText(Path.Combine(directory,"color-dialog-checks.json"),JsonSerializer.Serialize(new{success=dialogChecks.Values.All(value=>value),checks=dialogChecks},new JsonSerializerOptions{WriteIndented=true}));
        panel.Close();Quit();
    }
}


public partial class SettingsWindow
{
    internal Dictionary<string,bool> VerifyColorDialogs(string directory,Action<FrameworkElement,string> capture)
    {
        var checks=new Dictionary<string,bool>();
        string original=host.Preferences.LyricColor;
        void Dialog(Action<ColorPickerWindow> exercise)
        {
            Exception? failure=null;
            Dispatcher.BeginInvoke(new Action(()=>{
                ColorPickerWindow? picker=OwnedWindows.OfType<ColorPickerWindow>().FirstOrDefault();
                try { if(picker==null)throw new InvalidOperationException("Color dialog did not open");exercise(picker); }
                catch(Exception error){failure=error;picker?.Close();}
            }),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            EditColor(true);
            if(failure!=null)throw failure;
        }
        Dialog(picker=>{
            var input=(TextBox)picker.FindName("HexInput");
            input.Text="a3f";
            checks["Short HEX previews the same normalized color in settings and overlay"]=host.Preferences.LyricColor=="#AA33FF"
                && ((SolidColorBrush)PreviewLyric.Foreground).Color==AppearanceColors.Parse("#AA33FF");
            var hue=(Slider)picker.FindName("HueSlider");
            input.Text="#FF0000";hue.Value=120;
            checks["Hue interaction converts red to green"]=picker.SelectedColor=="#00FF00";
            hue.Value=240;checks["Hue interaction converts red to blue"]=picker.SelectedColor=="#0000FF";
            input.Text="#A3F";string last=host.Preferences.LyricColor;input.Text="invalid";
            checks["Invalid HEX keeps the last preview and disables confirmation"]=!((Button)picker.FindName("AcceptButton")).IsEnabled
                && host.Preferences.LyricColor==last;
            input.Text="#CDBBFF";picker.UpdateLayout();capture(picker,"color-picker.png");
            picker.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(picker),Environment.TickCount,Key.Escape)
                {RoutedEvent=Keyboard.PreviewKeyDownEvent});
        });
        checks["Escape restores the previous lyric color"]=host.Preferences.LyricColor==original;
        Dialog(picker=>{
            ((TextBox)picker.FindName("HexInput")).Text="#ABCDEF";
            ((Button)picker.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        checks["Confirm applies the selected custom color"]=host.Preferences.LyricColor=="#ABCDEF"&&LyricColorValue.Text=="#ABCDEF";
        host.Preferences.LyricColor=original;host.ApplyAppearance(false);SyncControls();
        Dialog(picker=>{
            ((TextBox)picker.FindName("HexInput")).Text="#78AABB";picker.Close();
        });
        checks["Closing the dialog restores the previous color"]=host.Preferences.LyricColor==original;
        return checks;
    }
}
