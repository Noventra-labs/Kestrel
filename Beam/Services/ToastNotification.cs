using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Beam.Interop;

namespace Beam.Services
{
    /// <summary>
    /// Lightweight, click-through, non-focus-stealing floating HUD toast (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE).
    /// Allows Beam to hide in 0ms while displaying visual confirmation over the desktop.
    /// </summary>
    public static class ToastNotification
    {
        private static Window? _toastWindow;
        private static TextBlock? _toastText;
        private static DispatcherTimer? _hideTimer;

        public static void Show(string message)
        {
            if (!BeamSettings.Current.EnableToastNotifications) return;
            if (System.Windows.Application.Current?.Dispatcher == null) return;

            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    EnsureWindowCreated();
                    if (_toastWindow == null || _toastText == null) return;

                    _toastText.Text = message;
                    _toastWindow.Opacity = 1.0;

                    // Position horizontally centered, 90px above primary screen work area bottom
                    var workArea = SystemParameters.WorkArea;
                    _toastWindow.UpdateLayout();
                    _toastWindow.Left = workArea.Left + (workArea.Width - _toastWindow.ActualWidth) / 2;
                    _toastWindow.Top = workArea.Bottom - 96;

                    if (!_toastWindow.IsVisible)
                    {
                        _toastWindow.Show();
                    }

                    _hideTimer?.Stop();
                    _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1350) };
                    _hideTimer.Tick += (s, e) =>
                    {
                        _hideTimer.Stop();
                        if (_toastWindow != null && _toastWindow.IsVisible)
                        {
                            var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(250));
                            fade.Completed += (_, _) => _toastWindow.Hide();
                            _toastWindow.BeginAnimation(UIElement.OpacityProperty, fade);
                        }
                    };
                    _hideTimer.Start();
                }
                catch { }
            });
        }

        private static void EnsureWindowCreated()
        {
            if (_toastWindow != null) return;

            _toastText = new TextBlock
            {
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 2, 0)
            };

            var diamond = new System.Windows.Shapes.Path
            {
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 99, 99)),
                VerticalAlignment = VerticalAlignment.Center,
                Data = Geometry.Parse("M 0,6 L 6,0 L 12,6 L 6,12 Z")
            };

            var stack = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(diamond);
            stack.Children.Add(_toastText);

            var pill = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(242, 22, 25, 34)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 72, 92)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(16, 8, 18, 8),
                Margin = new Thickness(16),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 4,
                    Opacity = 0.75,
                    Color = Colors.Black
                },
                Child = stack
            };

            _toastWindow = new Window
            {
                Title = "KestrelToast",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Focusable = false,
                IsHitTestVisible = false,
                Content = pill
            };

            _toastWindow.SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(_toastWindow).Handle;
                int exStyle = DwmApi.GetWindowLong(hwnd, DwmApi.GWL_EXSTYLE);
                DwmApi.SetWindowLong(
                    hwnd,
                    DwmApi.GWL_EXSTYLE,
                    exStyle | DwmApi.WS_EX_TRANSPARENT | DwmApi.WS_EX_NOACTIVATE | DwmApi.WS_EX_TOOLWINDOW);
            };
        }
    }
}
