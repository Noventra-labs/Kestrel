using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Beam.Interop
{
    /// <summary>
    /// Modern dark-fluent renderer for the Kestrel system tray context menu.
    /// Provides smooth rounded selection pills, crisp Segoe typography, right-aligned
    /// shortcut keycaps, and seamless dark mode styling matching Kestrel's aesthetic.
    /// </summary>
    public sealed class TrayMenuRenderer : ToolStripRenderer
    {
        private static readonly Color BgColor = Color.FromArgb(15, 17, 24);            // #0F1118
        private static readonly Color BorderColor = Color.FromArgb(40, 46, 62);        // #282E3E
        private static readonly Color HoverBg = Color.FromArgb(32, 38, 54);            // #202636
        private static readonly Color HoverBorder = Color.FromArgb(52, 60, 82);        // #343C52
        private static readonly Color SeparatorColor = Color.FromArgb(28, 33, 46);     // #1C212E
        private static readonly Color TextColor = Color.FromArgb(241, 245, 249);        // #F1F5F9
        private static readonly Color TextSelectedColor = Color.FromArgb(255, 255, 255);
        private static readonly Color ShortcutColor = Color.FromArgb(126, 139, 155);   // #7E8B9B
        private static readonly Color ShortcutSelected = Color.FromArgb(174, 186, 202);
        private static readonly Color BrandAccent = Color.FromArgb(255, 99, 99);       // #FF6363

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
                cms.Padding = new Padding(4, 6, 4, 6);
                cms.DropShadowEnabled = true;
            }
        }

        protected override void InitializeItem(ToolStripItem item)
        {
            base.InitializeItem(item);
            if (item is ToolStripMenuItem mi)
            {
                mi.AutoSize = true;
                mi.Padding = new Padding(12, 6, 12, 6);
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
                var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
                using var path = CreateRoundedRectangle(rect, 5);
                using var brush = new SolidBrush(HoverBg);
                using var pen = new Pen(HoverBorder, 1);

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            bool isSelected = e.Item.Selected && e.Item.Enabled;
            Color foreColor = isSelected ? TextSelectedColor : TextColor;
            bool isPrimary = string.Equals(e.Item.Tag as string, "Primary", StringComparison.OrdinalIgnoreCase);

            int textLeft = 14;

            // Draw clean Kestrel brand diamond for the primary "Open Kestrel" item
            if (isPrimary)
            {
                int diamondSize = 7;
                int dy = (e.Item.Height - diamondSize) / 2;
                int dx = 14;
                var diamondPoints = new Point[]
                {
                    new(dx + diamondSize / 2, dy),
                    new(dx + diamondSize, dy + diamondSize / 2),
                    new(dx + diamondSize / 2, dy + diamondSize),
                    new(dx, dy + diamondSize / 2)
                };

                using var brandBrush = new SolidBrush(BrandAccent);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPolygon(brandBrush, diamondPoints);

                textLeft = 14 + diamondSize + 8;
            }

            var textRect = new Rectangle(textLeft, 0, e.Item.Width - textLeft - 8, e.Item.Height);
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

            Font baseFont = e.TextFont ?? GetMenuFont();
            Font font = baseFont;
            bool disposeFont = false;
            if (isPrimary && !baseFont.Bold)
            {
                font = new Font(baseFont, FontStyle.Bold);
                disposeFont = true;
            }

            try
            {
                TextRenderer.DrawText(e.Graphics, e.Text, font, textRect, foreColor, flags);
            }
            finally
            {
                if (disposeFont)
                {
                    font.Dispose();
                }
            }

            // Draw right-aligned shortcut if specified
            if (e.Item is ToolStripMenuItem mi && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString))
            {
                Color scColor = isSelected ? ShortcutSelected : ShortcutColor;
                var scRect = new Rectangle(14, 0, e.Item.Width - 28, e.Item.Height);
                var scFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;

                using var scFont = new Font(baseFont.FontFamily, 8.5f, FontStyle.Regular);
                TextRenderer.DrawText(e.Graphics, mi.ShortcutKeyDisplayString, scFont, scRect, scColor, scFlags);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(SeparatorColor, 1);
            e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // Suppress white/grey gutter
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
