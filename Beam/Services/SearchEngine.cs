using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
            "Kestrel",
            "frecency.json");

        private static readonly string LegacyFrecencyFile = Path.Combine(
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

            Task.Run(SaveFrecency);
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
                var list = pool as IList<LauncherItem> ?? pool.ToList();
                var scoredEmpty = new List<(LauncherItem Item, double Score)>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    scoredEmpty.Add((item, GetFrecencyBoost(item.Id) - i * 0.05));
                }
                scoredEmpty.Sort((a, b) => b.Score.CompareTo(a.Score));
                int count = Math.Min(maxResults, scoredEmpty.Count);
                var results = new List<LauncherItem>(count);
                for (int i = 0; i < count; i++)
                {
                    results.Add(scoredEmpty[i].Item);
                }
                return results;
            }

            var q = query.Trim();
            var lowerQuery = q.ToLowerInvariant();
            var scored = new List<(LauncherItem Item, double Score)>();

            foreach (var item in pool)
            {
                double nameScore = ComputeFuzzyScoreFast(item.Name, item.LowerName, item.Acronym, q, lowerQuery);
                double catScore = ComputeFuzzyScoreFast(item.Category, item.LowerCategory, string.Empty, q, lowerQuery) * 0.45;
                double subScore = item.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase) ? 65.0 : 0.0;

                double bestMatch = Math.Max(nameScore, Math.Max(catScore, subScore));
                if (bestMatch > 0)
                {
                    double total = bestMatch + GetFrecencyBoost(item.Id);
                    scored.Add((item, total));
                }
            }

            scored.Sort((a, b) =>
            {
                int cmp = b.Score.CompareTo(a.Score);
                return cmp != 0 ? cmp : a.Item.Name.Length.CompareTo(b.Item.Name.Length);
            });

            int resultCount = Math.Min(maxResults, scored.Count);
            var ranked = new List<LauncherItem>(resultCount);
            for (int i = 0; i < resultCount; i++)
            {
                ranked.Add(scored[i].Item);
            }
            return ranked;
        }

        public static double ComputeFuzzyScoreFast(string target, string lowerTarget, string acronym, string query, string lowerQuery)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(query))
                return 0;

            // 1. Exact Match
            if (string.Equals(lowerTarget, lowerQuery, StringComparison.Ordinal))
                return 1000.0;

            // 2. Exact Prefix Match
            if (lowerTarget.StartsWith(lowerQuery, StringComparison.Ordinal))
                return 650.0 + (20.0 / Math.Max(1, target.Length));

            // 3. Acronym Match (pre-computed)
            if (acronym.Length >= 2)
            {
                if (string.Equals(acronym, lowerQuery, StringComparison.Ordinal))
                    return 580.0;
                if (acronym.StartsWith(lowerQuery, StringComparison.Ordinal))
                    return 480.0;
            }

            // 4. Word-Boundary Prefix Match
            int idx = lowerTarget.IndexOf(lowerQuery, StringComparison.Ordinal);
            if (idx > 0)
            {
                char prev = target[idx - 1];
                if (prev == ' ' || prev == '-' || prev == '_' || prev == '.')
                    return 420.0 - idx;

                // Contiguous Substring Match
                return 260.0 - idx;
            }

            // 5. Subsequence Fuzzy Match (using lower strings directly, zero allocations)
            if (lowerQuery.Length >= 2 && lowerQuery.Length <= lowerTarget.Length)
            {
                int qIdx = 0;
                int firstMatch = -1;
                int lastMatch = -1;
                int wordBoundaryHits = 0;

                for (int tIdx = 0; tIdx < lowerTarget.Length && qIdx < lowerQuery.Length; tIdx++)
                {
                    if (lowerTarget[tIdx] == lowerQuery[qIdx])
                    {
                        if (firstMatch == -1) firstMatch = tIdx;
                        lastMatch = tIdx;
                        if (tIdx == 0 || target[tIdx - 1] == ' ' || char.IsUpper(target[tIdx]))
                            wordBoundaryHits++;
                        qIdx++;
                    }
                }

                if (qIdx == lowerQuery.Length)
                {
                    int span = (lastMatch - firstMatch) + 1;
                    int gaps = span - lowerQuery.Length;
                    if (gaps <= lowerQuery.Length * 2)
                    {
                        return Math.Max(40.0, 160.0 + (wordBoundaryHits * 25.0) - (gaps * 12.0) - firstMatch);
                    }
                }
            }

            return 0;
        }

        public static double ComputeFuzzyScore(string target, string query)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(query))
                return 0;

            string lowerTarget = target.ToLowerInvariant();
            string lowerQuery = query.ToLowerInvariant();
            string acronym = LauncherItem.ComputeAcronym(target);

            return ComputeFuzzyScoreFast(target, lowerTarget, acronym, query, lowerQuery);
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
                string target = File.Exists(FrecencyFile)
                    ? FrecencyFile
                    : (File.Exists(LegacyFrecencyFile) ? LegacyFrecencyFile : string.Empty);

                if (!string.IsNullOrEmpty(target))
                {
                    var json = File.ReadAllText(target);
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
