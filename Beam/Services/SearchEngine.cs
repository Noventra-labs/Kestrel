using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Beam.Models;

namespace Beam.Services
{
    public class UsageRecord
    {
        public int Count { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }

    public static class SearchEngine
    {
        private static readonly ConcurrentDictionary<string, UsageRecord> UsageMap = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string FrecencyFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Beam",
            "frecency.json");

        static SearchEngine()
        {
            LoadFrecency();
        }

        public static void RecordUsage(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId) || itemId.StartsWith("math:") || itemId.StartsWith("web:"))
                return;

            UsageMap.AddOrUpdate(
                itemId,
                _ => new UsageRecord { Count = 1, LastUsedUtc = DateTime.UtcNow },
                (_, existing) =>
                {
                    existing.Count++;
                    existing.LastUsedUtc = DateTime.UtcNow;
                    return existing;
                });

            SaveFrecency();
        }

        public static double GetFrecencyBoost(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId) || !UsageMap.TryGetValue(itemId, out var rec))
                return 0;

            double hoursSince = Math.Max(0.1, (DateTime.UtcNow - rec.LastUsedUtc).TotalHours);
            double recencyMultiplier = hoursSince switch
            {
                < 1.0 => 4.0,
                < 24.0 => 2.5,
                < 168.0 => 1.5,
                _ => 0.8
            };

            return Math.Min(350.0, rec.Count * 22.0 * recencyMultiplier);
        }

        public static List<LauncherItem> RankAndFilter(IEnumerable<LauncherItem> pool, string query, int maxResults = 60)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                // Sort by Frecency first, then keep original category priority
                return pool
                    .Select((item, idx) => new { Item = item, Score = GetFrecencyBoost(item.Id) - idx * 0.05 })
                    .OrderByDescending(x => x.Score)
                    .Take(maxResults)
                    .Select(x => x.Item)
                    .ToList();
            }

            var q = query.Trim();
            var scored = new List<(LauncherItem Item, double Score)>();

            foreach (var item in pool)
            {
                double nameScore = ComputeFuzzyScore(item.Name, q);
                double catScore = ComputeFuzzyScore(item.Category, q) * 0.45;
                double subScore = item.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase) ? 65.0 : 0.0;

                double bestMatch = Math.Max(nameScore, Math.Max(catScore, subScore));
                if (bestMatch > 0)
                {
                    double total = bestMatch + GetFrecencyBoost(item.Id);
                    scored.Add((item, total));
                }
            }

            return scored
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Item.Name.Length)
                .Take(maxResults)
                .Select(x => x.Item)
                .ToList();
        }

        public static double ComputeFuzzyScore(string target, string query)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(query))
                return 0;

            // 1. Exact Match
            if (target.Equals(query, StringComparison.OrdinalIgnoreCase))
                return 1000.0;

            // 2. Exact Prefix Match
            if (target.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return 650.0 + (20.0 / Math.Max(1, target.Length));

            // 3. Acronym / Initials Match (e.g. "vsc" -> "Visual Studio Code", "lh" -> "Left Half", "wt" -> "Windows Terminal")
            var acronym = ExtractAcronym(target);
            if (acronym.Length >= 2)
            {
                if (acronym.Equals(query, StringComparison.OrdinalIgnoreCase))
                    return 580.0;
                if (acronym.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    return 480.0;
            }

            // 4. Word-Boundary Prefix Match (e.g. "studio" in "Visual Studio Code")
            int idx = target.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                char prev = target[idx - 1];
                if (prev == ' ' || prev == '-' || prev == '_' || prev == '.')
                    return 420.0 - idx;

                // Contiguous Substring Match
                return 260.0 - idx;
            }

            // 5. Subsequence Fuzzy Match (all characters of query appear in order in target)
            if (query.Length >= 2 && query.Length <= target.Length)
            {
                int qIdx = 0;
                int firstMatch = -1;
                int lastMatch = -1;
                int wordBoundaryHits = 0;

                for (int tIdx = 0; tIdx < target.Length && qIdx < query.Length; tIdx++)
                {
                    if (char.ToLowerInvariant(target[tIdx]) == char.ToLowerInvariant(query[qIdx]))
                    {
                        if (firstMatch == -1) firstMatch = tIdx;
                        lastMatch = tIdx;
                        if (tIdx == 0 || target[tIdx - 1] == ' ' || char.IsUpper(target[tIdx]))
                            wordBoundaryHits++;
                        qIdx++;
                    }
                }

                if (qIdx == query.Length)
                {
                    int span = (lastMatch - firstMatch) + 1;
                    int gaps = span - query.Length;
                    // Reject overly scattered subsequence matches
                    if (gaps <= query.Length * 2)
                    {
                        return Math.Max(40.0, 160.0 + (wordBoundaryHits * 25.0) - (gaps * 12.0) - firstMatch);
                    }
                }
            }

            return 0;
        }

        private static string ExtractAcronym(string text)
        {
            Span<char> buf = stackalloc char[16];
            int count = 0;
            bool takeNext = true;

            for (int i = 0; i < text.Length && count < buf.Length; i++)
            {
                char c = text[i];
                if (c == ' ' || c == '-' || c == '_' || c == '/')
                {
                    takeNext = true;
                }
                else if (takeNext || (i > 0 && char.IsUpper(c) && char.IsLower(text[i - 1])))
                {
                    if (char.IsLetterOrDigit(c))
                    {
                        buf[count++] = char.ToLowerInvariant(c);
                    }
                    takeNext = false;
                }
            }

            return new string(buf[..count]);
        }

        public static void ClearFrecency()
        {
            UsageMap.Clear();
            try
            {
                if (File.Exists(FrecencyFile))
                    File.Delete(FrecencyFile);
            }
            catch { }
        }

        public static int GetTrackedCount() => UsageMap.Count;

        private static void LoadFrecency()
        {
            try
            {
                if (File.Exists(FrecencyFile))
                {
                    var json = File.ReadAllText(FrecencyFile);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, UsageRecord>>(json);
                    if (dict != null)
                    {
                        foreach (var kv in dict)
                            UsageMap[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }
        }

        private static void SaveFrecency()
        {
            try
            {
                var dir = Path.GetDirectoryName(FrecencyFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(UsageMap);
                File.WriteAllText(FrecencyFile, json);
            }
            catch { }
        }
    }
}

