using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Beam.Interop;
using Beam.Models;

namespace Beam.Services
{
    public class IndexedFileEntry
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string Directory { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public DateTime LastWrite { get; set; }
    }

    public static class FileSearchService
    {
        private static List<IndexedFileEntry> _indexedFiles = new();
        private static bool _isIndexing = false;

        private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", "bin", "obj", "venv", "__pycache__", ".vs", ".idea", "AppData", "packages"
        };

        public static int GetIndexedCount() => _indexedFiles.Count;

        public static void ForceReindex()
        {
            _isIndexing = false;
            StartBackgroundIndexing();
        }

        public static void StartBackgroundIndexing()
        {
            if (!BeamSettings.Current.EnableFileSearch) return;
            if (_isIndexing) return;
            _isIndexing = true;

            Task.Run(() =>
            {
                var results = new List<IndexedFileEntry>(2500);
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                var roots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Path.Combine(userProfile, "Downloads"),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Path.Combine(userProfile, "Projects")
                };

                foreach (var root in roots.Distinct())
                {
                    if (Directory.Exists(root))
                    {
                        CrawlDirectory(root, results, maxDepth: 4, currentDepth: 0, maxTotal: 3500);
                    }
                }

                _indexedFiles = results
                    .OrderByDescending(f => f.LastWrite)
                    .ToList();
                _isIndexing = false;
            });
        }

        private static void CrawlDirectory(string dir, List<IndexedFileEntry> collector, int maxDepth, int currentDepth, int maxTotal)
        {
            if (currentDepth > maxDepth || collector.Count >= maxTotal)
                return;

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (collector.Count >= maxTotal) break;
                    var name = Path.GetFileName(file);
                    if (name.StartsWith('.')) continue;

                    DateTime lw = DateTime.MinValue;
                    try { lw = File.GetLastWriteTime(file); } catch { }

                    collector.Add(new IndexedFileEntry
                    {
                        Name = name,
                        FullPath = file,
                        Directory = dir,
                        Extension = Path.GetExtension(file).ToLowerInvariant(),
                        LastWrite = lw
                    });
                }

                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    if (collector.Count >= maxTotal) break;
                    var folderName = Path.GetFileName(subDir);
                    if (folderName.StartsWith('.') || IgnoredFolders.Contains(folderName))
                        continue;

                    CrawlDirectory(subDir, collector, maxDepth, currentDepth + 1, maxTotal);
                }
            }
            catch { }
        }

        public static List<LauncherItem> SearchFiles(string query, int maxResults = 35)
        {
            var list = new List<LauncherItem>();
            var snapshot = _indexedFiles;

            IEnumerable<IndexedFileEntry> matches;
            if (string.IsNullOrWhiteSpace(query))
            {
                matches = snapshot.Take(maxResults);
            }
            else
            {
                var q = query.Trim();
                matches = snapshot
                    .Select(f => new { File = f, Score = SearchEngine.ComputeFuzzyScore(f.Name, q) })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.File.LastWrite)
                    .Take(maxResults)
                    .Select(x => x.File);
            }

            foreach (var f in matches)
            {
                string pathCopy = f.FullPath;
                list.Add(new LauncherItem
                {
                    Id = "file:" + pathCopy,
                    Name = f.Name,
                    Subtitle = f.Directory,
                    Category = string.IsNullOrEmpty(f.Extension) ? "File" : f.Extension.TrimStart('.').ToUpperInvariant() + " File",
                    ShortcutBadge = "Open ↵",
                    Icon = ShellIconHelper.GetIcon(pathCopy),
                    Path = pathCopy,
                    Action = () => AppScanner.LaunchFile(pathCopy)
                });
            }

            return list;
        }
    }
}

