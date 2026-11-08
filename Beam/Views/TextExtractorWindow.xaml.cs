using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Beam.Services;
using Forms = System.Windows.Forms;

namespace Beam.Views
{
    public partial class TextExtractorWindow : Window
    {
        private System.Windows.Point _startPoint;
        private System.Drawing.Point _startScreenPt;
        private bool _isDragging = false;

        public TextExtractorWindow()
        {
            InitializeComponent();
            Loaded += TextExtractorWindow_Loaded;
        }

        private void TextExtractorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            // Center hint pill near top of screen
            Canvas.SetLeft(HintPill, (ActualWidth - HintPill.ActualWidth) / 2.0);
            Canvas.SetTop(HintPill, 40);
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isDragging = true;
                _startPoint = e.GetPosition(this);
                _startScreenPt = Forms.Cursor.Position;

                Canvas.SetLeft(SelectionRectangle, _startPoint.X);
                Canvas.SetTop(SelectionRectangle, _startPoint.Y);
                SelectionRectangle.Width = 0;
                SelectionRectangle.Height = 0;
                SelectionRectangle.Visibility = Visibility.Visible;

                CaptureMouse();
            }
        }

        private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDragging)
            {
                var cur = e.GetPosition(this);
                double x = Math.Min(_startPoint.X, cur.X);
                double y = Math.Min(_startPoint.Y, cur.Y);
                double w = Math.Abs(cur.X - _startPoint.X);
                double h = Math.Abs(cur.Y - _startPoint.Y);

                Canvas.SetLeft(SelectionRectangle, x);
                Canvas.SetTop(SelectionRectangle, y);
                SelectionRectangle.Width = w;
                SelectionRectangle.Height = h;
            }
        }

        private async void Window_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            ReleaseMouseCapture();

            var endScreenPt = Forms.Cursor.Position;
            int sx = Math.Min(_startScreenPt.X, endScreenPt.X);
            int sy = Math.Min(_startScreenPt.Y, endScreenPt.Y);
            int sw = Math.Abs(endScreenPt.X - _startScreenPt.X);
            int sh = Math.Abs(endScreenPt.Y - _startScreenPt.Y);

            // Hide overlay first so screenshot doesn't capture the overlay tint
            Hide();

            if (sw < 8 || sh < 8)
            {
                Close();
                return;
            }

            try
            {
                using var bmp = new Bitmap(sw, sh);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(sx, sy, 0, 0, new System.Drawing.Size(sw, sh), CopyPixelOperation.SourceCopy);
                }

                string recognizedText = await TextExtractorService.ExtractTextFromBitmapAsync(bmp);

                if (!string.IsNullOrWhiteSpace(recognizedText))
                {
                    System.Windows.Clipboard.SetText(recognizedText);
                    string preview = recognizedText.Replace("\r", " ").Replace("\n", " ");
                    if (preview.Length > 45) preview = preview[..42] + "...";
                    ToastNotification.Show($"✓ Copied OCR: \"{preview}\"");
                }
                else
                {
                    ToastNotification.Show("No readable text detected in selected area");
                }
            }
            catch (Exception ex)
            {
                ToastNotification.Show($"OCR failed: {ex.Message}");
            }
            finally
            {
                Close();
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }
    }
}
