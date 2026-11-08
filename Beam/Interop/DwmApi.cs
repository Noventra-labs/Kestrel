using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Beam.Interop
{
    public static class DwmApi
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;
        public const int WM_CLIPBOARDUPDATE = 0x031D;

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        private const byte VK_MENU = 0x12; // Alt key
        private const byte VK_CONTROL = 0x11;
        private const byte VK_V = 0x56;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private static readonly UIntPtr EXTRA_SIG = new(0xBEAA0001u);

        // DWM Window Attributes
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWA_CLOAKED = 14;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        // Window Corner Preferences
        public const int DWMWCP_ROUND = 2;

        // System Backdrop Types (Windows 11 Build 22621+)
        public const int DWMSBT_AUTO = 0;
        public const int DWMSBT_NONE = 1;
        public const int DWMSBT_MAINWINDOW = 2;       // Mica
        public const int DWMSBT_TRANSIENTWINDOW = 3;  // Acrylic (Frosted Glass)
        public const int DWMSBT_TABBEDWINDOW = 4;     // Mica Alt

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;

            public MARGINS(int left, int right, int top, int bottom)
            {
                cxLeftWidth = left;
                cxRightWidth = right;
                cyTopHeight = top;
                cyBottomHeight = bottom;
            }
        }

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        public static void EnableAcrylic(IntPtr hwnd)
        {
            try
            {
                var margins = new MARGINS(-1, -1, -1, -1);
                DwmExtendFrameIntoClientArea(hwnd, ref margins);

                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                int cornerPreference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

                int backdropType = DWMSBT_TRANSIENTWINDOW; // Windows 11 Fluent Acrylic blur glass effect
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            }
            catch { }
        }

        /// <summary>
        /// Reliably forces the Beam window to foreground on Windows 11 without AttachThreadInput deadlocks.
        /// Uses the Win32 Alt-Key permission grant pattern.
        /// </summary>
        public static void ForceForegroundWindow(IntPtr beamHwnd)
        {
            if (beamHwnd == IntPtr.Zero) return;
            try
            {
                keybd_event(VK_MENU, 0, 0, EXTRA_SIG);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, EXTRA_SIG);
                ShowWindow(beamHwnd, SW_RESTORE);
                BringWindowToTop(beamHwnd);
                SetForegroundWindow(beamHwnd);
                SetActiveWindow(beamHwnd);
            }
            catch { }
        }

        public static bool IsShellOrCloakedWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return true;

            // Check DWM cloaked state (e.g. hidden UWP / StartMenuExperienceHost / SearchHost)
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            var sb = new StringBuilder(128);
            GetClassName(hwnd, sb, sb.Capacity);
            var cls = sb.ToString();

            return cls is "Shell_TrayWnd"
                or "SecondaryTrayWnd"
                or "Progman"
                or "WorkerW"
                or "Windows.UI.Core.CoreWindow"
                or "XamlExplorerHostIslandWindow"
                or "TopLevelWindowForOverflowXamlIsland"
                or "Shell_InputSwitchTopLevelWindow"
                or "DV2ControlHost";
        }

        public static bool IsValidTargetWindow(IntPtr hwnd, IntPtr excludeBeamHwnd)
        {
            if (hwnd == IntPtr.Zero || hwnd == excludeBeamHwnd) return false;
            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
            if (IsShellOrCloakedWindow(hwnd)) return false;

            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;

            return GetWindowTextLength(hwnd) > 0;
        }

        /// <summary>
        /// Checks whether the current foreground HWND belongs to Beam or its hosted child WebView2 HWND.
        /// Prevents false deactivation when clicking inside the embedded Gemini WebView2 control.
        /// </summary>
        public static bool IsForegroundOwnedByBeamOrChild(IntPtr beamHwnd)
        {
            if (beamHwnd == IntPtr.Zero) return false;
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (fg == beamHwnd || IsChild(beamHwnd, fg)) return true;

            GetWindowThreadProcessId(fg, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }

        public static IntPtr GetTopMostTargetWindow(IntPtr excludeBeamHwnd)
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    if (IsValidTargetWindow(hWnd, excludeBeamHwnd))
                    {
                        found = hWnd;
                        return false; // Stop enumeration at top-most valid app window
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        public static void TrimMemoryWorkingSet()
        {
            try
            {
                GC.Collect(1, GCCollectionMode.Optimized, false);
                using var proc = Process.GetCurrentProcess();
                EmptyWorkingSet(proc.Handle);
            }
            catch { }
        }

        public static async void PasteTextToPreviousWindow(string text)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                IntPtr target = GlobalKeyboardHook.LastActiveWindow;
                if (!IsValidTargetWindow(target, GlobalKeyboardHook.BeamWindowHandle))
                {
                    target = GetTopMostTargetWindow(GlobalKeyboardHook.BeamWindowHandle);
                }

                if (target != IntPtr.Zero && target != GlobalKeyboardHook.BeamWindowHandle)
                {
                    SetForegroundWindow(target);
                    await Task.Delay(65);

                    // Synthesize Ctrl + V
                    keybd_event(VK_CONTROL, 0, 0, EXTRA_SIG);
                    keybd_event(VK_V, 0, 0, EXTRA_SIG);
                    keybd_event(VK_V, 0, KEYEVENTF_KEYUP, EXTRA_SIG);
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, EXTRA_SIG);
                }
            }
            catch { }
        }
    }
}
