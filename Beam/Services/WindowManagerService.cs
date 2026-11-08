using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Beam.Interop;
using Beam.Models;

namespace Beam.Services
{
    public enum WindowSnapLayout
    {
        LeftHalf,
        RightHalf,
        TopHalf,
        BottomHalf,
        Center,
        AlmostMaximize,
        Maximize,
        Restore,
        TopLeftQuarter,
        TopRightQuarter,
        BottomLeftQuarter,
        BottomRightQuarter,
        FirstThird,
        LastTwoThirds
    }

    public static class WindowManagerService
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out DwmApi.RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        private const int SW_RESTORE = 9;
        private const int SW_MAXIMIZE = 3;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public DwmApi.RECT rcMonitor;
            public DwmApi.RECT rcWork;
            public uint dwFlags;
        }

        public static bool ToggleAlwaysOnTop()
        {
            IntPtr target = GlobalKeyboardHook.LastActiveWindow;
            if (!DwmApi.IsValidTargetWindow(target, GlobalKeyboardHook.BeamWindowHandle))
            {
                target = DwmApi.GetTopMostTargetWindow(GlobalKeyboardHook.BeamWindowHandle);
            }

            if (target == IntPtr.Zero || target == GlobalKeyboardHook.BeamWindowHandle)
            {
                ToastNotification.Show("No active application window found to pin");
                return false;
            }

            var sb = new StringBuilder(256);
            GetWindowText(target, sb, 256);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) title = "Active Window";

            int exStyle = GetWindowLong(target, GWL_EXSTYLE);
            bool isTopmost = (exStyle & WS_EX_TOPMOST) != 0;

            if (isTopmost)
            {
                SetWindowPos(target, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                ToastNotification.Show($"📌 Unpinned Window: {title}");
                return false;
            }
            else
            {
                SetWindowPos(target, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                ToastNotification.Show($"📌 Pinned Always on Top: {title}");
                return true;
            }
        }

        public static void ApplySnap(WindowSnapLayout layout)
        {
            IntPtr target = GlobalKeyboardHook.LastActiveWindow;
            if (!DwmApi.IsValidTargetWindow(target, GlobalKeyboardHook.BeamWindowHandle))
            {
                target = DwmApi.GetTopMostTargetWindow(GlobalKeyboardHook.BeamWindowHandle);
            }

            if (target == IntPtr.Zero || target == GlobalKeyboardHook.BeamWindowHandle)
            {
                ToastNotification.Show("No active application window found to snap");
                return;
            }

            if (layout == WindowSnapLayout.Maximize)
            {
                if (DwmApi.IsIconic(target)) ShowWindowAsync(target, SW_RESTORE);
                ShowWindow(target, SW_MAXIMIZE);
                DwmApi.SetForegroundWindow(target);
                ToastNotification.Show("✓ Window Maximized");
                return;
            }

            if (layout == WindowSnapLayout.Restore)
            {
                ShowWindow(target, SW_RESTORE);
                DwmApi.SetForegroundWindow(target);
                ToastNotification.Show("✓ Window Restored");
                return;
            }

            // Restore first if minimized or maximized so SetWindowPos applies exact bounds
            ShowWindow(target, SW_RESTORE);

            IntPtr hMonitor = MonitorFromWindow(target, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref mi))
                return;

            int workX = mi.rcWork.Left;
            int workY = mi.rcWork.Top;
            int workW = mi.rcWork.Right - mi.rcWork.Left;
            int workH = mi.rcWork.Bottom - mi.rcWork.Top;

            int x = workX, y = workY, w = workW, h = workH;

            switch (layout)
            {
                case WindowSnapLayout.LeftHalf:
                    w = workW / 2;
                    break;
                case WindowSnapLayout.RightHalf:
                    x = workX + workW / 2;
                    w = workW - (workW / 2);
                    break;
                case WindowSnapLayout.TopHalf:
                    h = workH / 2;
                    break;
                case WindowSnapLayout.BottomHalf:
                    y = workY + workH / 2;
                    h = workH - (workH / 2);
                    break;
                case WindowSnapLayout.Center:
                    w = (int)(workW * 0.75);
                    h = (int)(workH * 0.75);
                    x = workX + (workW - w) / 2;
                    y = workY + (workH - h) / 2;
                    break;
                case WindowSnapLayout.AlmostMaximize:
                    w = (int)(workW * 0.94);
                    h = (int)(workH * 0.94);
                    x = workX + (workW - w) / 2;
                    y = workY + (workH - h) / 2;
                    break;
                case WindowSnapLayout.TopLeftQuarter:
                    w = workW / 2;
                    h = workH / 2;
                    break;
                case WindowSnapLayout.TopRightQuarter:
                    x = workX + workW / 2;
                    w = workW - (workW / 2);
                    h = workH / 2;
                    break;
                case WindowSnapLayout.BottomLeftQuarter:
                    y = workY + workH / 2;
                    w = workW / 2;
                    h = workH - (workH / 2);
                    break;
                case WindowSnapLayout.BottomRightQuarter:
                    x = workX + workW / 2;
                    y = workY + workH / 2;
                    w = workW - (workW / 2);
                    h = workH - (workH / 2);
                    break;
                case WindowSnapLayout.FirstThird:
                    w = workW / 3;
                    break;
                case WindowSnapLayout.LastTwoThirds:
                    x = workX + workW / 3;
                    w = (workW * 2) / 3;
                    break;
            }

            SetWindowPos(target, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_SHOWWINDOW);
            DwmApi.SetForegroundWindow(target);
            ToastNotification.Show($"✓ Snapped: {layout}");
        }

        public static void MinimizeAllWindows()
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                SendMessage(tray, 0x0111, (IntPtr)419, IntPtr.Zero); // MIN_ALL
                ToastNotification.Show("✓ Minimized All Windows");
            }
        }

        public static List<LauncherItem> GetWindowCommands()
        {
            var iconPin = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathPin);
            var iconLeft = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathWindowLeft);
            var iconRight = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathWindowRight);
            var iconMax = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathWindowMax);
            var iconCenter = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathWindowCenter);

            return new List<LauncherItem>
            {
                new() { Id = "win:pin_top", Name = "Always on Top (Pin Window)", Subtitle = "PowerToys Always on Top — Keep active window pinned above all others", Category = "Window Management", ShortcutBadge = "Pin 📌", Icon = iconPin, Action = () => ToggleAlwaysOnTop() },
                new() { Id = "win:left", Name = "Left Half", Subtitle = "Window Management — Snap active window to left 50%", Category = "Window Management", ShortcutBadge = "Win+←", Icon = iconLeft, Action = () => ApplySnap(WindowSnapLayout.LeftHalf) },
                new() { Id = "win:right", Name = "Right Half", Subtitle = "Window Management — Snap active window to right 50%", Category = "Window Management", ShortcutBadge = "Win+→", Icon = iconRight, Action = () => ApplySnap(WindowSnapLayout.RightHalf) },
                new() { Id = "win:max", Name = "Maximize Window", Subtitle = "Window Management — Expand active window to full screen", Category = "Window Management", ShortcutBadge = "Win+↑", Icon = iconMax, Action = () => ApplySnap(WindowSnapLayout.Maximize) },
                new() { Id = "win:almostmax", Name = "Almost Maximize", Subtitle = "Window Management — Resize active window to 92% centered", Category = "Window Management", Icon = iconCenter, Action = () => ApplySnap(WindowSnapLayout.AlmostMaximize) },
                new() { Id = "win:center", Name = "Center Window", Subtitle = "Window Management — Center active window on screen", Category = "Window Management", Icon = iconCenter, Action = () => ApplySnap(WindowSnapLayout.Center) },
                new() { Id = "win:top", Name = "Top Half", Subtitle = "Window Management — Snap active window to top 50%", Category = "Window Management", Icon = iconMax, Action = () => ApplySnap(WindowSnapLayout.TopHalf) },
                new() { Id = "win:bottom", Name = "Bottom Half", Subtitle = "Window Management — Snap active window to bottom 50%", Category = "Window Management", Icon = iconMax, Action = () => ApplySnap(WindowSnapLayout.BottomHalf) },
                new() { Id = "win:tl", Name = "Top Left Quarter", Subtitle = "Window Management — Snap to top-left corner", Category = "Window Management", Icon = iconLeft, Action = () => ApplySnap(WindowSnapLayout.TopLeftQuarter) },
                new() { Id = "win:tr", Name = "Top Right Quarter", Subtitle = "Window Management — Snap to top-right corner", Category = "Window Management", Icon = iconRight, Action = () => ApplySnap(WindowSnapLayout.TopRightQuarter) },
                new() { Id = "win:bl", Name = "Bottom Left Quarter", Subtitle = "Window Management — Snap to bottom-left corner", Category = "Window Management", Icon = iconLeft, Action = () => ApplySnap(WindowSnapLayout.BottomLeftQuarter) },
                new() { Id = "win:br", Name = "Bottom Right Quarter", Subtitle = "Window Management — Snap to bottom-right corner", Category = "Window Management", Icon = iconRight, Action = () => ApplySnap(WindowSnapLayout.BottomRightQuarter) },
                new() { Id = "win:firstthird", Name = "First Third", Subtitle = "Window Management — Snap to left 33% of display", Category = "Window Management", Icon = iconLeft, Action = () => ApplySnap(WindowSnapLayout.FirstThird) },
                new() { Id = "win:lasttwothirds", Name = "Last Two Thirds", Subtitle = "Window Management — Snap to right 66% of display", Category = "Window Management", Icon = iconRight, Action = () => ApplySnap(WindowSnapLayout.LastTwoThirds) },
                new() { Id = "win:restore", Name = "Restore Window", Subtitle = "Window Management — Unmaximize active window", Category = "Window Management", Icon = iconCenter, Action = () => ApplySnap(WindowSnapLayout.Restore) },
                new() { Id = "win:minall", Name = "Minimize All Windows", Subtitle = "Window Management — Show Desktop", Category = "Window Management", ShortcutBadge = "Win+D", Icon = iconCenter, Action = MinimizeAllWindows }
            };
        }
    }
}
