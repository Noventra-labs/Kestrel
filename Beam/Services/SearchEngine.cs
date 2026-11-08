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
            var tokens = lowerQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var scored = new List<(LauncherItem Item, double Score)>();

            foreach (var item in pool)
            {
                double score = ScoreItem(item, q, lowerQuery, tokens);
                if (score > 0)
                {
                    double total = score + GetFrecencyBoost(item.Id);
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

        public static double ScoreItem(LauncherItem item, string q, string lowerQuery, string[] tokens)
        {
            if (string.IsNullOrEmpty(item.Name) || tokens.Length == 0)
                return 0;

            // 1. Exact full name
            if (string.Equals(item.LowerName, lowerQuery, StringComparison.Ordinal))
                return 2500.0;

            // 2. Exact full name prefix
            if (item.LowerName.StartsWith(lowerQuery, StringComparison.Ordinal))
                return 1800.0 + (20.0 / Math.Max(1, item.Name.Length));

            // 3. Direct canonical boost for Microsoft Store and app queries
            bool isStore = item.Name.Equals("Microsoft Store", StringComparison.OrdinalIgnoreCase);
            if (isStore)
            {
                if (lowerQuery == "store" || lowerQuery == "apps" || lowerQuery == "appstore" ||
                    lowerQuery == "windows store" || lowerQuery == "store app" || lowerQuery == "app store")
                    return 1900.0;

                if (lowerQuery == "app" || lowerQuery == "microsoft app" || lowerQuery == "microsft store" ||
                    lowerQuery == "micorsoft store" || lowerQuery == "stire")
                    return 1600.0;
            }

            // 4. Single-token query evaluation
            if (tokens.Length == 1)
            {
                string token = tokens[0];
                double best = 0;

                // Match against words in item Name
                var nameWords = item.NameWords;
                for (int i = 0; i < nameWords.Length; i++)
                {
                    double s = ScoreTokenAgainstWord(token, nameWords[i]);
                    if (s > 0)
                    {
                        double val = s * 1.5;
                        if (val > best) best = val;
                    }
                }

                // Match against pre-computed acronym
                if (item.Acronym.Length >= 2)
                {
                    if (string.Equals(item.Acronym, token, StringComparison.Ordinal))
                        best = Math.Max(best, 580.0);
                    else if (item.Acronym.StartsWith(token, StringComparison.Ordinal))
                        best = Math.Max(best, 480.0);
                }

                // Match word boundary in full name
                int idx = item.LowerName.IndexOf(token, StringComparison.Ordinal);
                if (idx > 0)
                {
                    char prev = item.Name[idx - 1];
                    if (prev == ' ' || prev == '-' || prev == '_' || prev == '.')
                        best = Math.Max(best, 440.0 - idx);
                    else
                        best = Math.Max(best, 260.0 - idx);
                }

                // Match against rich Keywords
                var kwWords = item.KeywordWords;
                for (int i = 0; i < kwWords.Length; i++)
                {
                    double s = ScoreTokenAgainstWord(token, kwWords[i]);
                    if (s > 0)
                    {
                        double val = 180.0 + (s * 0.4);
                        if (val > best) best = val;
                    }
                }

                // Match against Subtitle words
                var subWords = item.SubtitleWords;
                for (int i = 0; i < subWords.Length; i++)
                {
                    double s = ScoreTokenAgainstWord(token, subWords[i]);
                    if (s > 0)
                    {
                        double val = 100.0 + (s * 0.25);
                        if (val > best) best = val;
                    }
                }

                // Category match: only as a modest tiebreaker, never overwhelming
                if (lowerQuery.Length >= 4 && item.LowerCategory.Contains(lowerQuery, StringComparison.Ordinal))
                {
                    best = Math.Max(best, 40.0);
                }

                return best;
            }

            // 5. Multi-token query evaluation: ALL tokens must match something in the item
            double total = 0;
            int nameHits = 0;

            for (int t = 0; t < tokens.Length; t++)
            {
                string token = tokens[t];
                double tokenBest = 0;
                bool hitName = false;

                // Check name words
                var nameWords = item.NameWords;
                for (int i = 0; i < nameWords.Length; i++)
                {
                    double s = ScoreTokenAgainstWord(token, nameWords[i]);
                    if (s > 0)
                    {
                        double val = s * 2.2;
                        if (val > tokenBest)
                        {
                            tokenBest = val;
                            hitName = true;
                        }
                    }
                }

                // Check keywords
                var kwWords = item.KeywordWords;
                for (int i = 0; i < kwWords.Length; i++)
                {
                    double s = ScoreTokenAgainstWord(token, kwWords[i]);
                    if (s > 0)
                    {
                        double val = 90.0 + (s * 0.3);
                        if (val > tokenBest) tokenBest = val;
                    }
                }

                // Check subtitle words
                var subWords = item.SubtitleWords;
                for (int i = 0; i < subWords.Length; i++)
                {
                    double s = ScoreTokenAgainstWord(token, subWords[i]);
                    if (s > 0)
                    {
                        double val = 60.0 + (s * 0.2);
                        if (val > tokenBest) tokenBest = val;
                    }
                }

                // If any token failed to match at all, this item does not match the multi-word query
                if (tokenBest <= 0)
                    return 0;

                if (hitName) nameHits++;
                total += tokenBest;
            }

            double multiScore = 400.0 + total;
            if (nameHits == tokens.Length)
                multiScore += 500.0; // All tokens directly matched the app name
            else if (nameHits > 0)
                multiScore += (nameHits * 120.0);

            return multiScore;
        }

        public static double ScoreTokenAgainstWord(string token, string word)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(word))
                return 0;

            // 1. Exact match
            if (string.Equals(token, word, StringComparison.Ordinal))
                return 350.0;

            // 2. Prefix match
            if (word.StartsWith(token, StringComparison.Ordinal))
                return 280.0 + (10.0 / Math.Max(1, word.Length));

            // 3. Substring match
            if (word.Contains(token, StringComparison.Ordinal))
                return 180.0;

            // 4. Subsequence fuzzy match
            if (token.Length >= 3 && token.Length <= word.Length)
            {
                int qIdx = 0;
                for (int wIdx = 0; wIdx < word.Length && qIdx < token.Length; wIdx++)
                {
                    if (word[wIdx] == token[qIdx])
                        qIdx++;
                }
                if (qIdx == token.Length)
                    return 130.0;
            }

            // 5. Zero-allocation typo tolerance (Damerau-Levenshtein distance with transpositions)
            if (token.Length >= 4)
            {
                int maxAllowed = token.Length >= 7 ? 2 : 1;
                int dist = FastEditDistance(token.AsSpan(), word.AsSpan(), maxAllowed);
                if (dist <= maxAllowed)
                {
                    return 220.0 - (dist * 50.0);
                }
            }

            return 0;
        }

        public static int FastEditDistance(ReadOnlySpan<char> s, ReadOnlySpan<char> t, int maxAllowed = 2)
        {
            int n = s.Length;
            int m = t.Length;
            if (Math.Abs(n - m) > maxAllowed) return maxAllowed + 1;
            if (n == 0) return m;
            if (m == 0) return n;

            // Zero-allocation stack buffer for up to 32-char words
            Span<int> prevPrev = stackalloc int[m + 1];
            Span<int> prev = stackalloc int[m + 1];
            Span<int> curr = stackalloc int[m + 1];

            for (int j = 0; j <= m; j++) prev[j] = j;

            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                int minRow = curr[0];

                for (int j = 1; j <= m; j++)
                {
                    int cost = (s[i - 1] == t[j - 1]) ? 0 : 1;
                    int del = prev[j] + 1;
                    int ins = curr[j - 1] + 1;
                    int sub = prev[j - 1] + cost;
                    int val = Math.Min(del, Math.Min(ins, sub));

                    // Adjacent character transposition check
                    if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                    {
                        int trans = prevPrev[j - 2] + cost;
                        if (trans < val) val = trans;
                    }

                    curr[j] = val;
                    if (val < minRow) minRow = val;
                }

                if (minRow > maxAllowed) return maxAllowed + 1;

                prev.CopyTo(prevPrev);
                curr.CopyTo(prev);
            }

            return curr[m];
        }

        public static double ComputeFuzzyScoreFast(string target, string lowerTarget, string acronym, string query, string lowerQuery)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(query))
                return 0;

            if (string.Equals(lowerTarget, lowerQuery, StringComparison.Ordinal))
                return 1000.0;

            if (lowerTarget.StartsWith(lowerQuery, StringComparison.Ordinal))
                return 650.0 + (20.0 / Math.Max(1, target.Length));

            if (acronym.Length >= 2)
            {
                if (string.Equals(acronym, lowerQuery, StringComparison.Ordinal))
                    return 580.0;
                if (acronym.StartsWith(lowerQuery, StringComparison.Ordinal))
                    return 480.0;
            }

            int idx = lowerTarget.IndexOf(lowerQuery, StringComparison.Ordinal);
            if (idx > 0)
            {
                char prev = target[idx - 1];
                if (prev == ' ' || prev == '-' || prev == '_' || prev == '.')
                    return 420.0 - idx;
                return 260.0 - idx;
            }

            return ScoreTokenAgainstWord(lowerQuery, lowerTarget);
        }

        public static double ComputeFuzzyScore(string target, string query)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(query))
                return 0;

            string lowerTarget = target.ToLowerInvariant();
            string lowerQuery = query.ToLowerInvariant();
            return ScoreTokenAgainstWord(lowerQuery, lowerTarget);
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
