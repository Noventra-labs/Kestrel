using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Beam.Interop
{
    public class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const ushort VK_SPACE = 0x20;
        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_RWIN = 0x5C;
        private const int VK_MENU = 0x12; // Alt key
        private const ushort VK_LCONTROL = 0xA2; // Left Control used to mask Start Menu chord in Windows Shell

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        public static readonly IntPtr EXTRA_INFO_SIGNATURE = new(unchecked((int)0xBEAA0001));

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private readonly Action _onTrigger;

        private bool _winKeyDown = false;
        private bool _winKeyDownAlone = false;

        public bool EnableStartKeyTrigger { get; set; } = true;
        public bool EnableAltSpaceHotkey { get; set; } = true;
        public static IntPtr LastActiveWindow { get; private set; } = IntPtr.Zero;
        public static IntPtr BeamWindowHandle { get; set; } = IntPtr.Zero;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
            [FieldOffset(0)]
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public GlobalKeyboardHook(Action onTrigger)
        {
            _onTrigger = onTrigger;
            _proc = HookCallback;
            InstallHook();
        }

        private void InstallHook()
        {
            try
            {
                if (_hookId != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookId);
                    _hookId = IntPtr.Zero;
                }

                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                IntPtr modHandle = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, modHandle, 0);
            }
            catch { }
        }

        public void EnsureHookAlive()
        {
            if (_hookId == IntPtr.Zero)
            {
                InstallHook();
            }
        }

        public static void CaptureActiveWindow()
        {
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != BeamWindowHandle && !DwmApi.IsShellOrCloakedWindow(fg))
            {
                LastActiveWindow = fg;
            }
            else if (LastActiveWindow == IntPtr.Zero || !DwmApi.IsValidTargetWindow(LastActiveWindow, BeamWindowHandle))
            {
                var fallback = DwmApi.GetTopMostTargetWindow(BeamWindowHandle);
                if (fallback != IntPtr.Zero)
                {
                    LastActiveWindow = fallback;
                }
            }
        }

        private static INPUT CreateKeyInput(ushort vk, bool keyUp, IntPtr extraInfo)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = (ushort)MapVirtualKey(vk, 0),
                        dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u,
                        time = 0,
                        dwExtraInfo = extraInfo
                    }
                }
            };
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // 0. Always pass through our own synthesized masking/focus events immediately
                if (kbd.dwExtraInfo == EXTRA_INFO_SIGNATURE)
                {
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

                // 1. Check Alt + Space
                if (EnableAltSpaceHotkey && isKeyDown && kbd.vkCode == VK_SPACE)
                {
                    bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 || ((kbd.flags & 0x20) != 0);
                    if (altDown)
                    {
                        _winKeyDownAlone = false;
                        CaptureActiveWindow();
                        _onTrigger?.Invoke();
                        return (IntPtr)1; // Suppress native Windows system menu
                    }
                }

                // 2. Check Start Button (Windows Key) solo tap
                if (EnableStartKeyTrigger)
                {
                    if (kbd.vkCode == VK_LWIN || kbd.vkCode == VK_RWIN)
                    {
                        if (isKeyDown)
                        {
                            if (!_winKeyDown)
                            {
                                _winKeyDown = true;
                                _winKeyDownAlone = true;
                                // Capture active app window BEFORE Windows Shell reacts
                                CaptureActiveWindow();
                            }
                        }
                        else if (isKeyUp)
                        {
                            bool wasAlone = _winKeyDownAlone;
                            _winKeyDown = false;
                            _winKeyDownAlone = false;

                            if (wasAlone)
                            {
                                // 1. Synthesize neutral Left Control (0xA2) Down + Up with hardware scan code
                                //    BEFORE releasing Win key. Windows Shell intercepts this chord (Win + Ctrl)
                                //    and suppresses the Start Menu opening sequence.
                                // 2. Follow with synthetic Win Up stamped with EXTRA_INFO_SIGNATURE so the OS
                                //    modifier table cleanly releases the Win key state without triggering Start Menu.
                                var inputs = new INPUT[3];
                                inputs[0] = CreateKeyInput(VK_LCONTROL, false, EXTRA_INFO_SIGNATURE);
                                inputs[1] = CreateKeyInput(VK_LCONTROL, true, EXTRA_INFO_SIGNATURE);
                                inputs[2] = CreateKeyInput((ushort)kbd.vkCode, true, EXTRA_INFO_SIGNATURE);
                                SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

                                // 3. Trigger Kestrel launcher
                                _onTrigger?.Invoke();

                                // 4. Swallow the raw physical Win Up so explorer.exe never opens Start Menu
                                return (IntPtr)1;
                            }
                        }
                    }
                    else if (isKeyDown)
                    {
                        // User pressed another key while holding Win (e.g. Win+E, Win+R, Win+D, Win+V, Win+Shift+S)
                        _winKeyDownAlone = false;
                    }
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
    }
}
