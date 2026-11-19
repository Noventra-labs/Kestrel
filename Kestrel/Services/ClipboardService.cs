using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kestrel.Interop;
using Kestrel.Models;

namespace Kestrel.Services
{
    public class ClipboardEntry
    {
        public string Text { get; set; } = string.Empty;
        public DateTime CopiedAt { get; set; } = DateTime.Now;
        public bool IsPinned { get; set; } = false;
    }

    public static class ClipboardService
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterClipboardFormat(string lpszFormat);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsClipboardFormatAvailable(uint format);

        private static readonly uint CfExcludeMonitor = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        private static readonly uint CfClipboardViewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
        private static readonly uint CfCanIncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");

        private static readonly List<ClipboardEntry> Entries = new();
        private static string _lastText = string.Empty;
        private static IntPtr _registeredHwnd = IntPtr.Zero;
        private const int MaxEntryCharacters = 65536; // 64 KB cap per clip

        internal static string HistoryFile { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kestrel",
            "clipboard_history.json");

        internal static string LegacyHistoryFile { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Beam",
            "clipboard_history.json");

        private static CancellationTokenSource? _saveDebounceCts;
        private static readonly object _saveLock = new();

        public static void InitializeEventDrivenListener(IntPtr hwnd)
        {
            LoadHistory();
            _registeredHwnd = hwnd;
            DwmApi.AddClipboardFormatListener(hwnd);
            OnClipboardUpdated();
        }

        public static void ShutdownListener()
        {
            if (_registeredHwnd != IntPtr.Zero)
            {
                DwmApi.RemoveClipboardFormatListener(_registeredHwnd);
                _registeredHwnd = IntPtr.Zero;
            }
            SaveHistoryDirect();
        }

        internal static bool IsHistoryFlagDisabled(object? data)
        {
            if (data is MemoryStream ms)
            {
                byte[] bytes = ms.ToArray();
                if (bytes.Length >= 4) return BitConverter.ToInt32(bytes, 0) == 0;
                if (bytes.Length > 0) return bytes[0] == 0;
                return false;
            }

            if (data is byte[] arr)
            {
                if (arr.Length >= 4) return BitConverter.ToInt32(arr, 0) == 0;
                if (arr.Length > 0) return arr[0] == 0;
                return false;
            }

            if (data is int iv) return iv == 0;
            if (data is uint uv) return uv == 0;
            if (data is bool bv) return !bv;
            return false;
        }

        private static bool IsClipboardExcluded()
        {
            if ((CfExcludeMonitor != 0 && IsClipboardFormatAvailable(CfExcludeMonitor)) ||
                (CfClipboardViewerIgnore != 0 && IsClipboardFormatAvailable(CfClipboardViewerIgnore)))
            {
                return true;
            }

            if (CfCanIncludeInHistory != 0 && IsClipboardFormatAvailable(CfCanIncludeInHistory))
            {
                try
                {
                    object? flag = System.Windows.Clipboard.GetData("CanIncludeInClipboardHistory");
                    if (IsHistoryFlagDisabled(flag))
                        return true;
                }
                catch
                {
                    return true;
                }
            }

            return false;
        }

        public static void SetSensitiveText(string text)
        {
            lock (Entries)
            {
                _lastText = text;
            }

            var data = new System.Windows.DataObject();
            data.SetText(text);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream());
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            System.Windows.Clipboard.SetDataObject(data, true);
        }

        public static void OnClipboardUpdated()
        {
            if (!KestrelSettings.Current.EnableClipboardHistory) return;
            try
            {
                if (IsClipboardExcluded()) return;

                if (System.Windows.Clipboard.ContainsText())
                {
                    var text = System.Windows.Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lock (Entries)
                        {
                            if (text == _lastText) return;
                            _lastText = text;
                        }
                        AddEntry(text);
                    }
                }
            }
            catch
            {
                // Ignore transient clipboard lock from other processes
            }
        }

        public static void AddEntry(string text)
        {
            var trimmed = text.Trim();
            if (string.IsNullOrEmpty(trimmed)) return;

            if (trimmed.Length > MaxEntryCharacters)
            {
                trimmed = trimmed[..MaxEntryCharacters] + "\n...[truncated: clip exceeds 64KB]";
            }

            lock (Entries)
            {
                var existing = Entries.FirstOrDefault(x => x.Text == trimmed);
                bool wasPinned = existing?.IsPinned ?? false;

                Entries.RemoveAll(x => x.Text == trimmed);
                Entries.Insert(0, new ClipboardEntry
                {
                    Text = trimmed,
                    CopiedAt = DateTime.Now,
                    IsPinned = wasPinned
                });

                // Sort pinned items first
                var sorted = Entries.OrderByDescending(x => x.IsPinned).ThenByDescending(x => x.CopiedAt).ToList();
                Entries.Clear();
                Entries.AddRange(sorted.Take(60));
            }

            ScheduleSaveHistory();
        }

        public static void ClearHistory()
        {
            lock (Entries)
            {
                Entries.Clear();
                _lastText = string.Empty;
            }
            SaveHistoryDirect();
        }

        public static int GetEntryCount()
        {
            lock (Entries)
            {
                return Entries.Count;
            }
        }

        public static List<LauncherItem> GetClipboardLauncherItems(string filter, Action onDone)
        {
            var iconClip = IconGenerator.GetSquircleIcon(SquircleTheme.ClipAmber, IconGenerator.PathClipboard);
            var iconPin = IconGenerator.GetSquircleIcon(SquircleTheme.GoldStar, IconGenerator.PathStar);
            var list = new List<LauncherItem>();

            List<ClipboardEntry> source;
            lock (Entries)
            {
                source = string.IsNullOrWhiteSpace(filter)
                    ? new List<ClipboardEntry>(Entries)
                    : Entries.Where(e => e.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            foreach (var entry in source)
            {
                string oneLine = entry.Text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (oneLine.Length > 75) oneLine = oneLine[..75] + "...";

                string age = GetRelativeTime(entry.CopiedAt);
                string prefix = entry.IsPinned ? "📌 " : "";

                list.Add(new LauncherItem
                {
                    Id = "clip:" + entry.CopiedAt.Ticks,
                    Name = prefix + oneLine,
                    Subtitle = $"{entry.Text.Length} chars • {age}",
                    Category = entry.IsPinned ? "Pinned Clip" : "Clipboard",
                    ShortcutBadge = "Paste ↵",
                    Icon = entry.IsPinned ? iconPin : iconClip,
                    Action = () =>
                    {
                        lock (Entries)
                        {
                            _lastText = entry.Text;
                        }
                        onDone();
                        DwmApi.PasteTextToPreviousWindow(entry.Text);
                    }
                });
            }

            if (list.Count == 0)
            {
                list.Add(new LauncherItem
                {
                    Id = "clip:empty",
                    Name = "No Clipboard History Found",
                    Subtitle = "Copy any text (Ctrl+C) anywhere in Windows and it appears here instantly",
                    Category = "Clipboard",
                    Icon = iconClip,
                    KeepOpenOnExecute = true
                });
            }

            return list;
        }

        private static string GetRelativeTime(DateTime dt)
        {
            var diff = DateTime.Now - dt;
            if (diff.TotalSeconds < 60) return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            return dt.ToString("MMM d, HH:mm");
        }

        internal static void LoadHistory()
        {
            try
            {
                string targetFile = HistoryFile;
                if (!File.Exists(targetFile) && File.Exists(LegacyHistoryFile))
                {
                    targetFile = LegacyHistoryFile;
                }

                if (File.Exists(targetFile))
                {
                    var raw = File.ReadAllText(targetFile).Trim();
                    bool wasProtected = CloudIdentity.IsProtectedSecret(raw);
                    string json = wasProtected ? CloudIdentity.UnprotectSecret(raw) : raw;
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var loaded = JsonSerializer.Deserialize<List<ClipboardEntry>>(json);
                        if (loaded != null)
                        {
                            lock (Entries)
                            {
                                Entries.Clear();
                                Entries.AddRange(loaded);
                                _lastText = Entries.Count > 0 ? Entries[0].Text : string.Empty;
                            }
                        }
                    }

                    // Upgrade legacy path or plaintext history on disk to DPAPI-encrypted storage.
                    if (string.Equals(targetFile, LegacyHistoryFile, StringComparison.OrdinalIgnoreCase) || !wasProtected)
                    {
                        SaveHistoryDirect();
                    }
                }
            }
            catch { }
        }

        private static void ScheduleSaveHistory()
        {
            lock (_saveLock)
            {
                _saveDebounceCts?.Cancel();
                _saveDebounceCts = new CancellationTokenSource();
                var token = _saveDebounceCts.Token;

                Task.Delay(800, token).ContinueWith(t =>
                {
                    if (!t.IsCanceled)
                    {
                        SaveHistoryDirect();
                    }
                }, TaskScheduler.Default);
            }
        }

        internal static void SaveHistoryDirect()
        {
            try
            {
                var dir = Path.GetDirectoryName(HistoryFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                List<ClipboardEntry> snapshot;
                lock (Entries)
                {
                    snapshot = new List<ClipboardEntry>(Entries);
                }

                var json = JsonSerializer.Serialize(snapshot);
                var protectedPayload = CloudIdentity.ProtectSecret(json);
                string tempFile = Path.Combine(dir ?? Path.GetTempPath(), $".clip-{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllText(tempFile, protectedPayload);
                    File.Move(tempFile, HistoryFile, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                }
            }
            catch { }
        }
    }
}
