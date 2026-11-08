using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beam.Services;
using Forms = System.Windows.Forms;

namespace Beam.Views
{
    public partial class ColorPickerWindow : Window
    {
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        private System.Drawing.Color _currentColor = System.Drawing.Color.White;

        public ColorPickerWindow()
        {
            InitializeComponent();
            Loaded += ColorPickerWindow_Loaded;
        }

        private void ColorPickerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            UpdateLoupeAtCurrentCursor();
        }

        private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            UpdateLoupeAtCurrentCursor();
        }

        private void UpdateLoupeAtCurrentCursor()
        {
            try
            {
                var pt = Forms.Cursor.Position;
                const int sampleSize = 15;
                const int center = sampleSize / 2;

                using var bmp = new Bitmap(sampleSize, sampleSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(pt.X - center, pt.Y - center, 0, 0, new System.Drawing.Size(sampleSize, sampleSize), CopyPixelOperation.SourceCopy);
                }

                _currentColor = bmp.GetPixel(center, center);

                IntPtr hBitmap = bmp.GetHbitmap();
                try
                {
                    var bs = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    MagnifierImage.Source = bs;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }

                string hex = $"#{_currentColor.R:X2}{_currentColor.G:X2}{_currentColor.B:X2}";
                string rgb = $"rgb({_currentColor.R}, {_currentColor.G}, {_currentColor.B})";

                HexCodeText.Text = hex;
                RgbCodeText.Text = rgb;
                ColorSwatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(_currentColor.R, _currentColor.G, _currentColor.B));

                // Position loupe offset from cursor
                var wpfPt = PointFromScreen(new System.Windows.Point(pt.X, pt.Y));
                double loupeX = wpfPt.X + 25;
                double loupeY = wpfPt.Y + 25;

                if (loupeX + LoupeContainer.Width > ActualWidth)
                    loupeX = wpfPt.X - LoupeContainer.Width - 25;
                if (loupeY + LoupeContainer.Height > ActualHeight)
                    loupeY = wpfPt.Y - LoupeContainer.Height - 25;

                Canvas.SetLeft(LoupeContainer, Math.Max(0, loupeX));
                Canvas.SetTop(LoupeContainer, Math.Max(0, loupeY));
            }
            catch { }
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                string hex = HexCodeText.Text;
                try
                {
                    System.Windows.Clipboard.SetText(hex);
                    ToastNotification.Show($"✓ Copied Color: {hex}");
                }
                catch { }
                Close();
            }
            else if (e.RightButton == MouseButtonState.Pressed)
            {
                string rgb = RgbCodeText.Text;
                try
                {
                    System.Windows.Clipboard.SetText(rgb);
                    ToastNotification.Show($"✓ Copied Color: {rgb}");
                }
                catch { }
                Close();
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
            else if (e.Key == Key.Space || e.Key == Key.Enter)
            {
                string hex = HexCodeText.Text;
                try
                {
                    System.Windows.Clipboard.SetText(hex);
                    ToastNotification.Show($"✓ Copied Color: {hex}");
                }
                catch { }
                Close();
            }
        }
    }
}
