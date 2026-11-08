using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

namespace Beam.Interop
{
    /// <summary>
    /// Native Win32 COM IShellLinkW shortcut creator with AppUserModelID registration and Shell notification.
    /// </summary>
    public static class ShortcutHelper
    {
        public const string AppId = "NoventraLabs.Kestrel";

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PropertyKey pkey);
            void GetValue(ref PropertyKey key, out PropVariant pv);
            void SetValue(ref PropertyKey key, ref PropVariant pv);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;

            public PropertyKey(Guid guid, uint pid)
            {
                fmtid = guid;
                this.pid = pid;
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pwszVal;

            public static PropVariant FromString(string val)
            {
                return new PropVariant
                {
                    vt = 31, // VT_LPWSTR
                    pwszVal = Marshal.StringToCoTaskMemUni(val)
                };
            }
        }

        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public const int SHCNE_ASSOCCHANGED = 0x08000000;
        public const int SHCNF_FLUSH = 0x1000;

        public static void NotifyShell()
        {
            try
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        public static bool CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string description, string iconPath)
        {
            bool success = false;

            void Action()
            {
                try
                {
                    string? dir = Path.GetDirectoryName(shortcutPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(shortcutPath))
                    {
                        try { File.Delete(shortcutPath); } catch { }
                    }

                    var link = (IShellLinkW)new ShellLink();
                    link.SetDescription(description);
                    link.SetPath(targetPath);
                    link.SetWorkingDirectory(workingDirectory);
                    if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                    {
                        link.SetIconLocation(iconPath, 0);
                    }

                    // Register official AppUserModelID for Windows 11 Start Menu
                    try
                    {
                        var pStore = (IPropertyStore)link;
                        var appUserModelIdKey = new PropertyKey(new Guid("{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}"), 5);
                        var pv = PropVariant.FromString(AppId);
                        pStore.SetValue(ref appUserModelIdKey, ref pv);
                        pStore.Commit();
                    }
                    catch { }

                    var file = (IPersistFile)link;
                    file.Save(shortcutPath, true);
                    success = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Native shortcut creation failed: {ex.Message}");
                }
            }

            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                Action();
            }
            else
            {
                var thread = new Thread(new ThreadStart(Action));
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join(3500);
            }

            // Fallback: WScript.Shell COM
            if (!success)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(shortcutPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        dynamic wscript = Activator.CreateInstance(shellType)!;
                        dynamic sc = wscript.CreateShortcut(shortcutPath);
                        sc.TargetPath = targetPath;
                        sc.WorkingDirectory = workingDirectory;
                        sc.Description = description;
                        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                        {
                            sc.IconLocation = iconPath + ",0";
                        }
                        sc.Save();
                        success = true;
                    }
                }
                catch { }
            }

            return success;
        }
    }
}
