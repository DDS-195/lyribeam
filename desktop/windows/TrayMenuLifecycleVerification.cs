using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LyricBridge;

internal static class TrayMenuLifecycleVerification
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetActiveWindow();
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    internal static int Run(string output)
    {
        int exitCode = 1;
        var checks = new Dictionary<string, bool>();
        var events = new List<object>();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var host = new Window { Title = "歌词托盘检查", Width = 320, Height = 100, ShowInTaskbar = false };
        host.Loaded += async (_, _) =>
        {
            using var menu = TrayMenuStyle.Create();
            foreach (var text in new[] { "显示歌词", "收起歌词", "显示设置…", "锁定歌词（鼠标穿透）", "重置到屏幕中央", "退出" }) menu.Items.Add(text);
            using var icon = new Forms.NotifyIcon { Icon = Drawing.SystemIcons.Application, Text = "歌词托盘检查", ContextMenuStrip = menu, Visible = true };
            var show = typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingMethodException("NotifyIcon.ShowContextMenu");
            menu.Opened += (_, _) => events.Add(new { action = "opened", foreground = GetForegroundWindow().ToInt64(), active = GetActiveWindow().ToInt64() });
            menu.Closed += (_, e) => events.Add(new { action = "closed", reason = e.CloseReason.ToString() });
            Process? focusTarget = null;
            try
            {
                host.Hide();
                show.Invoke(icon, null);
                await Task.Delay(650);
                checks["opensThroughNotifyIcon"] = menu.Visible;
                checks["menuOwnsForeground"] = GetForegroundWindow() == menu.Handle;
                checks["menuCapturesPointer"] = menu.Capture;
                PostMessage(menu.Handle, 0x100, 0x1B, 0);
                await Task.Delay(350);
                checks["escapeCloses"] = !menu.Visible;
                checks["escapeReleasesCapture"] = !menu.Capture;
                menu.Close();
                show.Invoke(icon, null);
                await Task.Delay(200);
                PostMessage(new WindowInteropHelper(host).Handle, 0x201, 1, 0);
                PostMessage(new WindowInteropHelper(host).Handle, 0x202, 0, 0);
                await Task.Delay(350);
                checks["outsideClickCloses"] = !menu.Visible;
                menu.Close();
                show.Invoke(icon, null);
                await Task.Delay(650);
                checks["staysOpenWithoutAction"] = menu.Visible;
                int blankPoint = ((menu.Height - 2) << 16) | (menu.Width / 2);
                PostMessage(menu.Handle, 0x201, 1, new nint(blankPoint));
                PostMessage(menu.Handle, 0x202, 0, new nint(blankPoint));
                await Task.Delay(350);
                checks["paddingClickKeepsMenuOpen"] = menu.Visible;
                checks["paddingClickKeepsCapture"] = menu.Capture;
                menu.Close();
                int selectedActions = 0;
                menu.Items[0].Click += (_, _) => selectedActions++;
                show.Invoke(icon, null);
                await Task.Delay(200);
                menu.Items[0].Select();
                PostMessage(menu.Handle, 0x100, 0x0D, 0);
                await Task.Delay(350);
                checks["selectionRunsAndCloses"] = selectedActions == 1 && !menu.Visible;
                menu.Close();
                var sourceHost = new Window { Title = "托盘来源窗口验证", Left = 80, Top = 80, Width = 320, Height = 100, Topmost = true, ShowInTaskbar = false };
                sourceHost.Show();
                await Task.Delay(150);
                var sourceHandle = new WindowInteropHelper(sourceHost).Handle;

                show.Invoke(icon, null);
                await Task.Delay(200);
                await Task.Delay(2100);
                nint foregroundBeforeHide = GetForegroundWindow();
                checks["sourceVisibleKeepsMenuOpen"] = menu.Visible;
                sourceHost.Hide();
                await Task.Delay(3500);
                checks["sourceHideKeepsForeground"] = foregroundBeforeHide == GetForegroundWindow();
                checks["automaticTrayCollapseKeepsMenuOpen"] = menu.Visible;
                events.Add(new { action = "source-host-hidden", foreground = GetForegroundWindow().ToInt64(), active = GetActiveWindow().ToInt64(), visible = menu.Visible, sourceHandle = sourceHandle.ToInt64(), observedOpenMs = 5800 });
                menu.Close();
                sourceHost.Show();
                await Task.Delay(150);

                sourceHost.Hide();
                show.Invoke(icon, null);
                await Task.Delay(650);
                checks["sourceHiddenBeforeOpeningStillUsable"] = menu.Visible;
                menu.Close();
                menu.Close();
                show.Invoke(icon, null);
                await Task.Delay(150);
                int outsidePoint = unchecked((int)0xFFF8FFF8); // captured point (-8,-8), outside the popup.
                PostMessage(menu.Handle, 0x201, 1, new nint(outsidePoint));
                PostMessage(menu.Handle, 0x202, 0, new nint(outsidePoint));
                await Task.Delay(350);
                checks["capturedOutsideClickCloses"] = !menu.Visible;
                checks["outsideClickReleasesCapture"] = !menu.Capture;
                sourceHost.Close();
                menu.Close();
                show.Invoke(icon, null);
                await Task.Delay(200);
                string focusEvidence = Path.ChangeExtension(output, ".focus.json");
                focusTarget = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "--tray-focus-target", focusEvidence } });
                for (int attempt = 0; attempt < 35 && !File.Exists(focusEvidence); attempt++) await Task.Delay(100);
                await Task.Delay(1000);
                var target = File.Exists(focusEvidence) ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(focusEvidence)) : null;
                checks["focusActuallyChanged"] = target != null && target["handle"] == GetForegroundWindow().ToInt64();
                events.Add(new { action = "external-foreground", foreground = GetForegroundWindow().ToInt64(), active = GetActiveWindow().ToInt64(), visible = menu.Visible });
                checks["foregroundChangeCloses"] = !menu.Visible;
                exitCode = checks.Values.All(value => value) ? 0 : 1;
                File.WriteAllText(output, JsonSerializer.Serialize(new { success = exitCode == 0, checks, events }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error) { File.WriteAllText(output, JsonSerializer.Serialize(new { success = false, checks, events, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true })); }
            finally { menu.Close(); if (focusTarget != null) { if (!focusTarget.HasExited) focusTarget.CloseMainWindow(); focusTarget.Dispose(); } icon.Visible = false; app.Shutdown(); }
        };
        app.Run(host);
        return exitCode;
    }

    internal static int FocusTarget(string output)
    {
        var app = new Application();
        var host = new Window { Title = "歌词菜单焦点验证", Width = 320, Height = 100, ShowInTaskbar = false };
        host.Loaded += async (_, _) =>
        {
            host.Activate();
            await Task.Delay(150);
            File.WriteAllText(output, JsonSerializer.Serialize(new { handle = new WindowInteropHelper(host).Handle.ToInt64(), foreground = GetForegroundWindow().ToInt64() }));
            await Task.Delay(6000); host.Close();
        };
        app.Run(host); return 0;
    }
}
