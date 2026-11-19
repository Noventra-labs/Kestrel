using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kestrel.Interop;

namespace Kestrel.Services
{
    /// <summary>
    /// Minimal, click-through, non-focus-stealing floating HUD toast (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE).
    /// </summary>
    public static class ToastNotification
    {
        private static Window? _toastWindow;
        private static TextBlock? _toastText;
        private static DispatcherTimer? _hideTimer;

        internal static string NormalizeMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            ReadOnlySpan<char> span = message.AsSpan().Trim();
            while (!span.IsEmpty)
            {
                char c = span[0];
                if (c is '✓' or '✔' or '⚠' or 'ℹ' or '✦' or '•' or '\uFE0F' or ' ')
                {
                    span = span[1..];
                }
                else
                {
                    break;
                }
            }

            return span.Trim().ToString();
        }

        public static void Show(string message)
        {
            if (!KestrelSettings.Current.EnableToastNotifications) return;
            if (System.Windows.Application.Current?.Dispatcher == null) return;

            string clean = NormalizeMessage(message);
            if (string.IsNullOrEmpty(clean)) return;

            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    EnsureWindowCreated();
                    if (_toastWindow == null || _toastText == null) return;

                    _toastText.Text = clean;
                    _toastWindow.Opacity = 1.0;

                    var workArea = SystemParameters.WorkArea;
                    _toastWindow.UpdateLayout();
                    _toastWindow.Left = workArea.Left + (workArea.Width - _toastWindow.ActualWidth) / 2;
                    _toastWindow.Top = workArea.Bottom - 72;

                    if (!_toastWindow.IsVisible)
                    {
                        _toastWindow.Show();
                    }

                    _hideTimer?.Stop();
                    _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
                    _hideTimer.Tick += (s, e) =>
                    {
                        _hideTimer.Stop();
                        if (_toastWindow != null && _toastWindow.IsVisible)
                        {
                            var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(160));
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
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249)),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0)
            };

            var pill = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 12, 14, 20)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(36, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(8),
                Child = _toastText
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
