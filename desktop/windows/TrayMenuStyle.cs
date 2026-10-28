using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace LyricBridge;

internal static class TrayMenuStyle
{
    private static readonly Drawing.Color Surface = Drawing.Color.FromArgb(33, 31, 41);
    private static readonly Drawing.Color Border = Drawing.Color.FromArgb(70, 64, 79);
    private static readonly Drawing.Color Text = Drawing.Color.FromArgb(245, 243, 248);
    private static readonly Drawing.Color Muted = Drawing.Color.FromArgb(174, 166, 188);
    private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(57, 48, 68);
    private static readonly Drawing.Color Accent = Drawing.Color.FromArgb(207, 188, 249);
    private static readonly Drawing.Color Exit = Drawing.Color.FromArgb(220, 169, 178);
    private static readonly Drawing.Color Divider = Drawing.Color.FromArgb(58, 52, 67);

    internal static Forms.ContextMenuStrip Create()
    {
        var menu = new StyledMenu {
            AutoSize = false,
            AutoClose = true,
            BackColor = Surface,
            ForeColor = Text,
            ShowImageMargin = false,
            ShowCheckMargin = false,
            DropShadowEnabled = true,
            Renderer = new Renderer()
        };
        menu.Opening += (_, _) => Prepare(menu);
        menu.DpiChangedAfterParent += (_, _) => Prepare(menu);
        return menu;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    internal static void Prepare(Forms.ContextMenuStrip menu)
    {
        float scale = Scale(menu);
        int width = Pixel(228, scale), padding = Pixel(6, scale), height = padding * 2;
        menu.SuspendLayout();
        try
        {
            menu.AutoSize = false;
            menu.Padding = new Forms.Padding(padding);
            foreach (Forms.ToolStripItem item in menu.Items)
            {
                int row = Pixel(item is Forms.ToolStripSeparator ? 8 : 32, scale);
                item.AutoSize = false;
                item.Margin = Forms.Padding.Empty;
                item.Padding = Forms.Padding.Empty;
                item.Size = new Drawing.Size(width - padding * 2, row);
                item.TextAlign = Drawing.ContentAlignment.MiddleLeft;
                height += row;
            }
            menu.Size = new Drawing.Size(width, height);
        }
        finally { menu.ResumeLayout(false); }
        menu.PerformLayout();
        if (menu is StyledMenu styled) styled.UpdateShape();
        menu.Invalidate();
    }

    private static float Scale(Forms.ToolStrip? menu) => Math.Max(1f, (menu?.DeviceDpi ?? 96) / 96f);
    private static int Pixel(float value, float scale) => (int)Math.Round(value * scale);

    private static Drawing2D.GraphicsPath Rounded(Drawing.RectangleF rectangle, float radius)
    {
        var path = new Drawing2D.GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (diameter <= 0) return path;
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class StyledMenu : Forms.ContextMenuStrip
    {
        private readonly Drawing.Font ownedFont = new("Microsoft YaHei UI", 9.25f, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Point);
        private Drawing.Region? ownedRegion;
        private bool processingMouseUp, releasingCapture;

        internal StyledMenu() { Font = ownedFont; }
        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (!Visible || IsDisposed || Disposing) return;
            // A tray menu has no parent ToolStrip to provide focus or pointer capture.
            SetForegroundWindow(Handle);
            Focus();
            Capture = true;
        }

        protected override void OnMouseDown(Forms.MouseEventArgs e)
        {
            if (!Visible || IsDisposed || Disposing) return;
            if (!ClientRectangle.Contains(e.Location) || Region?.IsVisible(e.Location) == false)
            {
                Close(Forms.ToolStripDropDownCloseReason.AppClicked);
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Visible && !IsDisposed && !Disposing && !processingMouseUp && !releasingCapture && !Capture)
                Close(Forms.ToolStripDropDownCloseReason.AppFocusChange);
        }

        protected override void WndProc(ref Forms.Message message)
        {
            bool mouseUp = message.Msg is 0x202 or 0x205 or 0x208 or 0x20C;
            if (!mouseUp) { base.WndProc(ref message); return; }
            processingMouseUp = true;
            try { base.WndProc(ref message); }
            finally
            {
                processingMouseUp = false;
                // Control releases capture after MouseUp, including clicks on popup padding.
                // Reacquire only when this popup is still the active foreground interaction.
                if (Visible && !IsDisposed && !Disposing && !Capture)
                {
                    if (GetCapture() == 0 && GetForegroundWindow() == Handle) Capture = true;
                    else Close(Forms.ToolStripDropDownCloseReason.AppFocusChange);
                }
            }
        }

        private void ReleaseMenuCapture()
        {
            releasingCapture = true;
            try { if (IsHandleCreated && Capture) Capture = false; }
            finally { releasingCapture = false; }
        }

        protected override void OnClosed(Forms.ToolStripDropDownClosedEventArgs e)
        {
            ReleaseMenuCapture();
            base.OnClosed(e);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateShape();
        }

        internal void UpdateShape()
        {
            if (IsDisposed || Width <= 0 || Height <= 0) return;
            using var path = Rounded(new Drawing.RectangleF(0, 0, Width, Height), Pixel(12, TrayMenuStyle.Scale(this)));
            var replacement = new Drawing.Region(path);
            var previous = ownedRegion;
            ownedRegion = replacement;
            Region = replacement;
            previous?.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseMenuCapture();
                Region = null;
                ownedRegion?.Dispose();
                ownedRegion = null;
            }
            base.Dispose(disposing);
            if (disposing) ownedFont.Dispose();
        }
    }

    private sealed class Renderer : Forms.ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            using var path = Rounded(new Drawing.RectangleF(0, 0, e.ToolStrip.Width, e.ToolStrip.Height), Pixel(12, Scale(e.ToolStrip)));
            using var brush = new Drawing.SolidBrush(Surface);
            e.Graphics.FillPath(brush, path);
            e.Graphics.Restore(state);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            using var path = Rounded(new Drawing.RectangleF(.5f, .5f, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), Pixel(12, Scale(e.ToolStrip)));
            using var pen = new Drawing.Pen(Border, 1f);
            e.Graphics.DrawPath(pen, path);
            e.Graphics.Restore(state);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            float scale = Scale(e.ToolStrip);
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            bool selected = e.Item.Enabled && (e.Item.Selected || e.Item.Pressed);
            if (selected)
            {
                // Native drop-down layout can offset the item; keep both outer insets equal.
                int menuWidth = e.ToolStrip?.ClientSize.Width ?? e.Item.Bounds.Right;
                float inset = Math.Max(e.Item.Bounds.Left, menuWidth - e.Item.Bounds.Right);
                float left = inset - e.Item.Bounds.Left;
                float width = Math.Max(1, menuWidth - 2 * inset);
                using var path = Rounded(new Drawing.RectangleF(left, scale, width, e.Item.Height - 2 * scale), 7 * scale);
                using var brush = new Drawing.SolidBrush(Hover);
                e.Graphics.FillPath(brush, path);
            }
            var ink = e.Item.Name == "exit" ? Exit : selected ? Accent : Muted;
            if (!e.Item.Enabled) ink = Drawing.Color.FromArgb(100, ink);
            DrawIcon(e.Graphics, e.Item.Name ?? "", 14 * scale, (e.Item.Height - 16 * scale) / 2, scale, ink);
            if (e.Item is Forms.ToolStripMenuItem { Checked: true })
            {
                using var pen = new Drawing.Pen(Accent, 1.6f * scale) { StartCap = Drawing2D.LineCap.Round, EndCap = Drawing2D.LineCap.Round, LineJoin = Drawing2D.LineJoin.Round };
                float left = e.Item.Width - 23 * scale, top = e.Item.Height / 2f;
                e.Graphics.DrawLines(pen, new[] {
                    new Drawing.PointF(left, top), new Drawing.PointF(left + 3 * scale, top + 3 * scale),
                    new Drawing.PointF(left + 9 * scale, top - 4 * scale)
                });
            }
            e.Graphics.Restore(state);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            float scale = Scale(e.ToolStrip);
            var rectangle = new Drawing.Rectangle(Pixel(40, scale), 0,
                Math.Max(1, e.Item.Width - Pixel(66, scale)), e.Item.Height);
            var color = e.Item.Enabled ? e.Item.Name == "exit" ? Exit : Text : Muted;
            Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.Item.Font, rectangle, color,
                Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine
                | Forms.TextFormatFlags.EndEllipsis | Forms.TextFormatFlags.NoPrefix);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            float scale = Scale(e.ToolStrip);
            using var pen = new Drawing.Pen(Divider, Math.Max(1, scale));
            float y = e.Item.Height / 2f;
            e.Graphics.DrawLine(pen, 12 * scale, y, e.Item.Width - 12 * scale, y);
        }

        // Checkmarks and glyphs share our layout instead of native image gutters.
        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e) { }
        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }

        private static void DrawIcon(Drawing.Graphics graphics, string name, float x, float y, float scale, Drawing.Color color)
        {
            var state = graphics.Save();
            graphics.TranslateTransform(x, y);
            graphics.ScaleTransform(scale, scale);
            using var pen = new Drawing.Pen(color, 1.35f) { StartCap = Drawing2D.LineCap.Round, EndCap = Drawing2D.LineCap.Round, LineJoin = Drawing2D.LineJoin.Round };
            switch (name)
            {
                case "show":
                    using (var frame = Rounded(new Drawing.RectangleF(1.5f, 2.5f, 13, 11), 2)) graphics.DrawPath(pen, frame);
                    graphics.DrawLine(pen, 2, 5.5f, 14, 5.5f);
                    graphics.DrawLines(pen, new[] { new Drawing.PointF(6.5f, 8), new Drawing.PointF(10, 10), new Drawing.PointF(6.5f, 12) });
                    break;
                case "hide":
                    graphics.DrawLine(pen, 2.5f, 8, 13.5f, 8);
                    break;
                case "settings":
                    graphics.DrawEllipse(pen, 3.5f, 3.5f, 9, 9);
                    graphics.DrawEllipse(pen, 6, 6, 4, 4);
                    for (int index = 0; index < 8; index++)
                    {
                        double angle = index * Math.PI / 4;
                        graphics.DrawLine(pen, 8 + (float)Math.Cos(angle) * 4.6f, 8 + (float)Math.Sin(angle) * 4.6f,
                            8 + (float)Math.Cos(angle) * 6.5f, 8 + (float)Math.Sin(angle) * 6.5f);
                    }
                    break;
                case "lock":
                    using (var body = Rounded(new Drawing.RectangleF(3.5f, 7, 9, 7), 1.5f)) graphics.DrawPath(pen, body);
                    graphics.DrawArc(pen, 5, 1.5f, 6, 10, 180, 180);
                    graphics.DrawLine(pen, 5, 6.5f, 5, 7);
                    graphics.DrawLine(pen, 11, 6.5f, 11, 7);
                    graphics.DrawLine(pen, 8, 9.5f, 8, 11.5f);
                    break;
                case "reset":
                    graphics.DrawArc(pen, 3, 3, 10, 10, 210, 285);
                    graphics.DrawLines(pen, new[] { new Drawing.PointF(2.2f, 2.5f), new Drawing.PointF(2.2f, 6.3f), new Drawing.PointF(6, 6.3f) });
                    break;
                case "exit":
                    graphics.DrawArc(pen, 2.5f, 2.5f, 11, 11, -50, 280);
                    graphics.DrawLine(pen, 8, 1.5f, 8, 8);
                    break;
            }
            graphics.Restore(state);
        }
    }
}
