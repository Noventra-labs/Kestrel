using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Beam.Interop
{
    internal static class StaWorker
    {
        private static readonly BlockingCollection<Action> _queue = new();

        static StaWorker()
        {
            var thread = new Thread(ProcessQueue)
            {
                IsBackground = true,
                Name = "Kestrel.StaIconWorker"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private static void ProcessQueue()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try { action(); } catch { }
            }
        }

        public static T Run<T>(Func<T> func)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                return func();
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task.GetAwaiter().GetResult();
        }
    }

    public static class ShellIconHelper
    {
        private static readonly ConcurrentDictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ImageSource> ExtensionCache = new(StringComparer.OrdinalIgnoreCase);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("shell32.dll", EntryPoint = "#727")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [ComImport]
        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
            [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
            [PreserveSig] int Draw(IntPtr pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(
                [In] SIZE size,
                [In] int flags,
                [Out] out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int x, int y) { cx = x; cy = y; }
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_SYSICONINDEX = 0x000004000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const int SHIL_LARGE = 0x00; // 32x32 crisp shell icon (perfect match for 26px list items)
        private const int ILD_TRANSPARENT = 0x00000001;

        public static ImageSource GetFastFileIcon(string path, string extension)
        {
            var ext = (extension ?? string.Empty).ToLowerInvariant();
            if (ext == ".exe" || ext == ".lnk" || ext == ".ico" || string.IsNullOrEmpty(ext))
            {
                return GetIcon(path);
            }

            if (ExtensionCache.TryGetValue(ext, out var cached))
                return cached;

            try
            {
                var extracted = StaWorker.Run(() => ExtractExtensionIconInternal(ext));
                if (extracted != null)
                {
                    ExtensionCache[ext] = extracted;
                    return extracted;
                }
            }
            catch { }

            // Fallback to standard path-based extractor
            var standard = GetIcon(path);
            ExtensionCache[ext] = standard;
            return standard;
        }

        private static ImageSource? ExtractExtensionIconInternal(string ext)
        {
            try
            {
                var shinfo = new SHFILEINFO();
                var res = SHGetFileInfo(
                    ext,
                    FILE_ATTRIBUTE_NORMAL,
                    ref shinfo,
                    (uint)Marshal.SizeOf<SHFILEINFO>(),
                    SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);

                if (res != IntPtr.Zero && shinfo.iIcon > 0)
                {
                    var iidImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                    if (SHGetImageList(SHIL_LARGE, ref iidImageList, out var imgList) == 0 && imgList != null)
                    {
                        if (imgList.GetIcon(shinfo.iIcon, ILD_TRANSPARENT, out var hIcon32) == 0 && hIcon32 != IntPtr.Zero)
                        {
                            try
                            {
                                var bs = Imaging.CreateBitmapSourceFromHIcon(
                                    hIcon32,
                                    Int32Rect.Empty,
                                    BitmapSizeOptions.FromEmptyOptions());
                                bs.Freeze();
                                return bs;
                            }
                            finally
                            {
                                DestroyIcon(hIcon32);
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        public static ImageSource GetIcon(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathTerminal);

            if (IconCache.TryGetValue(path, out var cached))
                return cached;

            if (path.Contains("!") || path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
            {
                return GetUwpIcon(path);
            }

            var extracted = ExtractHighDpiIcon(path)
                            ?? IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathTerminal);

            IconCache[path] = extracted;
            return extracted;
        }

        public static ImageSource GetUwpIcon(string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid))
                return IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathTerminal);

            if (IconCache.TryGetValue(aumid, out var cached))
                return cached;

            var extracted = ExtractUwpIcon(aumid)
                            ?? IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathTerminal);

            IconCache[aumid] = extracted;
            return extracted;
        }

        public static ImageSource? ExtractUwpIcon(string aumid)
        {
            try
            {
                return StaWorker.Run(() => ExtractUwpIconInternal(aumid));
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource? ExtractUwpIconInternal(string aumid)
        {
            try
            {
                string parsingName = aumid.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
                    ? aumid
                    : @"shell:AppsFolder\" + aumid;

                var guid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"); // IShellItemImageFactory
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, guid, out var factory);
                if (factory != null)
                {
                    var size = new SIZE(32, 32);
                    int hr = factory.GetImage(size, 0x0, out var hBitmap);
                    if (hr == 0 && hBitmap != IntPtr.Zero)
                    {
                        try
                        {
                            var bs = Imaging.CreateBitmapSourceFromHBitmap(
                                hBitmap,
                                IntPtr.Zero,
                                Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            bs.Freeze();
                            return bs;
                        }
                        finally
                        {
                            DeleteObject(hBitmap);
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static ImageSource? ExtractHighDpiIcon(string path)
        {
            try
            {
                return StaWorker.Run(() => ExtractHighDpiIconInternal(path));
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource? ExtractHighDpiIconInternal(string path)
        {
            try
            {
                // 1. Try 32x32 Large Icon via SHGetImageList
                var shinfo = new SHFILEINFO();
                var res = SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX);
                if (res != IntPtr.Zero && shinfo.iIcon > 0)
                {
                    var iidImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                    if (SHGetImageList(SHIL_LARGE, ref iidImageList, out var imgList) == 0 && imgList != null)
                    {
                        if (imgList.GetIcon(shinfo.iIcon, ILD_TRANSPARENT, out var hIcon32) == 0 && hIcon32 != IntPtr.Zero)
                        {
                            try
                            {
                                var bs = Imaging.CreateBitmapSourceFromHIcon(
                                    hIcon32,
                                    Int32Rect.Empty,
                                    BitmapSizeOptions.FromEmptyOptions());
                                bs.Freeze();
                                return bs;
                            }
                            finally
                            {
                                DestroyIcon(hIcon32);
                            }
                        }
                    }
                }

                // 2. Fallback to standard SHGFI_ICON | SHGFI_LARGEICON
                shinfo = new SHFILEINFO();
                res = SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
                if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var bs = Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bs.Freeze();
                        return bs;
                    }
                    finally
                    {
                        DestroyIcon(shinfo.hIcon);
                    }
                }

                // 3. Fallback to ExtractAssociatedIcon
                if (File.Exists(path))
                {
                    using var sysIcon = Icon.ExtractAssociatedIcon(path);
                    if (sysIcon != null)
                    {
                        var bs = Imaging.CreateBitmapSourceFromHIcon(
                            sysIcon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bs.Freeze();
                        return bs;
                    }
                }
            }
            catch
            {
                // Fallback to generated squircle icon
            }

            return null;
        }
    }
}
