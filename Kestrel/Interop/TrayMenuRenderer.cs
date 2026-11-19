using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Kestrel.Interop
{
    /// <summary>
    /// Minimal dark renderer for the Kestrel system tray context menu.
    /// </summary>
    public sealed class TrayMenuRenderer : ToolStripRenderer
    {
        private static readonly Color BgColor = Color.FromArgb(15, 17, 24);
        private static readonly Color BorderColor = Color.FromArgb(34, 38, 50);
        private static readonly Color HoverBg = Color.FromArgb(30, 35, 48);
        private static readonly Color SeparatorColor = Color.FromArgb(28, 33, 46);
        private static readonly Color TextColor = Color.FromArgb(241, 245, 249);
        private static readonly Color TextSelectedColor = Color.FromArgb(255, 255, 255);
        private static readonly Color ShortcutColor = Color.FromArgb(126, 139, 155);
        private static readonly Color ShortcutSelected = Color.FromArgb(174, 186, 202);

        private const int MenuItemWidth = 204;
        private const int MenuItemHeight = 26;

        public static Font GetMenuFont()
        {
            try
            {
                using var test = new Font("Segoe UI Variable Text", 9.5f);
                if (string.Equals(test.FontFamily.Name, "Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase))
                {
                    return new Font("Segoe UI Variable Text", 9.5f, FontStyle.Regular);
                }
            }
            catch { }
            return new Font("Segoe UI", 9.5f, FontStyle.Regular);
        }

        protected override void Initialize(ToolStrip toolStrip)
        {
            base.Initialize(toolStrip);
            if (toolStrip is ContextMenuStrip cms)
            {
                cms.ShowImageMargin = false;
                cms.ShowCheckMargin = false;
                cms.BackColor = BgColor;
                cms.ForeColor = TextColor;
                cms.Padding = new Padding(4, 4, 4, 4);
                cms.DropShadowEnabled = true;
            }
        }

        protected override void InitializeItem(ToolStripItem item)
        {
            base.InitializeItem(item);
            if (item is ToolStripMenuItem mi)
            {
                mi.AutoSize = false;
                mi.Width = MenuItemWidth;
                mi.Height = MenuItemHeight;
                mi.Padding = Padding.Empty;
            }
            else if (item is ToolStripSeparator sep)
            {
                sep.AutoSize = false;
                sep.Width = MenuItemWidth;
                sep.Height = 7;
                sep.Margin = Padding.Empty;
                sep.Padding = Padding.Empty;
            }
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(BgColor);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(BorderColor, 1);
            var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.DrawRectangle(pen, rect);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected && e.Item.Enabled)
            {
                var rect = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
                using var path = CreateRoundedRectangle(rect, 4);
                using var brush = new SolidBrush(HoverBg);

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Ignore WinForms' duplicate shortcut pass if ShortcutKeyDisplayString was set externally
            if (e.Item is ToolStripMenuItem miGuard &&
                !string.IsNullOrEmpty(miGuard.ShortcutKeyDisplayString) &&
                string.Equals(e.Text, miGuard.ShortcutKeyDisplayString, StringComparison.Ordinal) &&
                !string.Equals(miGuard.Text, miGuard.ShortcutKeyDisplayString, StringComparison.Ordinal))
            {
                return;
            }

            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            bool isSelected = e.Item.Selected && e.Item.Enabled;
            Color foreColor = isSelected ? TextSelectedColor : TextColor;

            const int textLeft = 10;
            var textRect = new Rectangle(textLeft, 0, e.Item.Width - textLeft - 10, e.Item.Height);
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

            Font baseFont = e.TextFont ?? GetMenuFont();
            TextRenderer.DrawText(e.Graphics, e.Item.Text, baseFont, textRect, foreColor, flags);

            string? shortcutText = (e.Item.Tag as string) ?? (e.Item as ToolStripMenuItem)?.ShortcutKeyDisplayString;
            if (!string.IsNullOrEmpty(shortcutText))
            {
                Color scColor = isSelected ? ShortcutSelected : ShortcutColor;
                var scFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                using var scFont = new Font(baseFont.FontFamily, 8.5f, FontStyle.Regular);
                TextRenderer.DrawText(e.Graphics, shortcutText, scFont, textRect, scColor, scFlags);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(SeparatorColor, 1);
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int diameter = radius * 2;
            if (diameter > bounds.Width) diameter = bounds.Width;
            if (diameter > bounds.Height) diameter = bounds.Height;

            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
