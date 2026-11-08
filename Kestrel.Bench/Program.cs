using System.Diagnostics;
using System.IO;
using System.Reflection;
using Beam.Models;
using Beam.Services;

// Kestrel micro-benchmark harness.
// Measures per-query latency (p50 / p95 / max) and managed allocations for the search hot paths.
// Run: dotnet run --project Kestrel.Bench -c Release
internal static class Program
{
    private static readonly string[] Queries =
    {
        "c", "ch", "chr", "chrome", "vsc", "visual", "visual studio code", "term",
        "set", "settings", "pwsh", "note", "xyzq", "lh", "color", "mem reduct"
    };

    [STAThread]
    private static int Main()
    {
        try
        {
            var rng = new Random(42);
            var pool = BuildPool(600, rng);
            InjectFileIndex(BuildFiles(3500, rng));

            Console.WriteLine($"Kestrel.Bench  |  .NET {Environment.Version}  |  {DateTime.Now:yyyy-MM-dd HH:mm}");
            Console.WriteLine($"Pool: {pool.Count} launcher items, 3500 indexed files, {Queries.Length} queries\n");
            Console.WriteLine($"{"Benchmark",-34} {"p50 µs",9} {"p95 µs",9} {"max µs",9} {"alloc KB/q",11}");
            Console.WriteLine(new string('-', 76));

            Run("SearchEngine.RankAndFilter (600)", q => SearchEngine.RankAndFilter(pool, q, 40));
            Run("FileSearch.SearchFiles (3500)", q => FileSearchService.SearchFiles(q, 6));
            Run("CalculatorService.Evaluate", q => CalculatorService.Evaluate(q));
            Run("CalculatorService.Evaluate (math)", _ => CalculatorService.Evaluate("12*(3+4)/7"));
            Run("DevTools.EvaluateInline", q => PowerDeveloperToolsService.EvaluateInline(q));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL EXCEPTION: " + ex);
            return 1;
        }
    }

    private static void Run(string name, Func<string, object> work)
    {
        // Warm-up (JIT + tiering)
        for (int i = 0; i < 300; i++) work(Queries[i % Queries.Length]);

        const int iterations = 2000;
        var samples = new double[iterations];
        long allocStart = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            string q = Queries[i % Queries.Length];
            long t0 = Stopwatch.GetTimestamp();
            work(q);
            samples[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        }
        long alloc = GC.GetAllocatedBytesForCurrentThread() - allocStart;

        Array.Sort(samples);
        Console.WriteLine($"{name,-34} {samples[iterations / 2],9:F1} {samples[(int)(iterations * 0.95)],9:F1} {samples[^1],9:F1} {alloc / 1024.0 / iterations,11:F2}");
    }

    private static List<LauncherItem> BuildPool(int count, Random rng)
    {
        string[] words = { "Visual", "Studio", "Code", "Chrome", "Google", "Terminal", "Windows", "Settings", "Notepad",
                           "Power", "Shell", "Adobe", "Photoshop", "Spotify", "Discord", "Slack", "Teams", "Office",
                           "Word", "Excel", "Calculator", "Paint", "Explorer", "Edge", "Steam", "Zoom", "Git", "Bash" };
        var list = new List<LauncherItem>(count);
        for (int i = 0; i < count; i++)
        {
            int n = rng.Next(1, 4);
            var name = string.Join(' ', Enumerable.Range(0, n).Select(_ => words[rng.Next(words.Length)])) + (i % 7 == 0 ? $" {i}" : "");
            list.Add(new LauncherItem
            {
                Id = "app:" + i,
                Name = name,
                Subtitle = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\" + name,
                Category = i % 5 == 0 ? "Command" : "Application"
            });
        }
        return list;
    }

    private static List<IndexedFileEntry> BuildFiles(int count, Random rng)
    {
        string[] exts = { ".pdf", ".txt", ".cs", ".json", ".png", ".docx", ".md", ".zip", ".xlsx", ".py" };
        string[] stems = { "report", "invoice", "notes", "main", "settings", "photo", "readme", "budget", "draft", "config" };
        var list = new List<IndexedFileEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var ext = exts[rng.Next(exts.Length)];
            var name = $"{stems[rng.Next(stems.Length)]}_{i}{ext}";
            list.Add(new IndexedFileEntry
            {
                Name = name,
                FullPath = @"C:\Users\Bench\Documents\" + name,
                Directory = @"C:\Users\Bench\Documents",
                Extension = ext,
                LastWrite = DateTime.Now.AddMinutes(-i)
            });
        }
        return list;
    }

    private static void InjectFileIndex(List<IndexedFileEntry> files)
    {
        var field = typeof(FileSearchService).GetField("_indexedFiles", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException("FileSearchService._indexedFiles not found");
        field.SetValue(null, files);
    }
}

