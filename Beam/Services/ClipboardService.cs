using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Beam.Interop;
using Beam.Models;

namespace Beam.Services
{
    public class ClipboardEntry
    {
        public string Text { get; set; } = string.Empty;
        public DateTime CopiedAt { get; set; } = DateTime.Now;
        public bool IsPinned { get; set; } = false;
    }

    public static class ClipboardService
    {
        private static readonly List<ClipboardEntry> Entries = new();
        private static string _lastText = string.Empty;
        private static IntPtr _registeredHwnd = IntPtr.Zero;
        private const int MaxEntryCharacters = 65536; // 64 KB cap per clip

        private static readonly string HistoryFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kestrel",
            "clipboard_history.json");

        private static readonly string LegacyHistoryFile = Path.Combine(
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

        public static void OnClipboardUpdated()
        {
            if (!BeamSettings.Current.EnableClipboardHistory) return;
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    var text = System.Windows.Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text) && text != _lastText)
                    {
                        _lastText = text;
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

            ScheduleSaveHistory();
        }

        public static void ClearHistory()
        {
            Entries.Clear();
            _lastText = string.Empty;
            SaveHistoryDirect();
        }

        public static int GetEntryCount() => Entries.Count;

        public static List<LauncherItem> GetClipboardLauncherItems(string filter, Action onDone)
        {
            var iconClip = IconGenerator.GetSquircleIcon(SquircleTheme.ClipAmber, IconGenerator.PathClipboard);
            var iconPin = IconGenerator.GetSquircleIcon(SquircleTheme.GoldStar, IconGenerator.PathStar);
            var list = new List<LauncherItem>();

            var source = string.IsNullOrWhiteSpace(filter)
                ? Entries
                : Entries.Where(e => e.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

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
                    Subtitle = $"{entry.Text.Length} chars • Copied {age} (Enter: Paste to App)",
                    Category = entry.IsPinned ? "Pinned Clip" : "Clipboard",
                    ShortcutBadge = "Paste ↵",
                    Icon = entry.IsPinned ? iconPin : iconClip,
                    Action = () =>
                    {
                        _lastText = entry.Text;
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

        private static void LoadHistory()
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
                    var json = File.ReadAllText(targetFile);
                    var loaded = JsonSerializer.Deserialize<List<ClipboardEntry>>(json);
                    if (loaded != null)
                    {
                        Entries.Clear();
                        Entries.AddRange(loaded);
                        if (Entries.Count > 0) _lastText = Entries[0].Text;
                    }

                    // If loaded from legacy, persist to new Kestrel path
                    if (targetFile == LegacyHistoryFile)
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

        private static void SaveHistoryDirect()
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
                File.WriteAllText(HistoryFile, json);
            }
            catch { }
        }
    }
}
