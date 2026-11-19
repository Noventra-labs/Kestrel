// Avoid touching real user settings, startup registry, or log files in the regression harness.
using System.Collections.Generic;

namespace Kestrel
{
    internal static class App
    {
        public static void Log(string _) { }
    }
}

namespace Kestrel.Services
{
    public sealed class KestrelSettings
    {
        public static KestrelSettings Current { get; } = new();
        public bool EnableToastNotifications { get; set; } = false;
        public bool EnableMemReduct { get; set; } = true;
        public bool AutoMemReductEnabled { get; set; } = false;
        public int AutoMemReductThreshold { get; set; } = 85;
        public int AutoMemReductMinAvailMb { get; set; } = 1536;
        public int SmartReclaimMinWorkingSetMb { get; set; } = 25;
        public List<string> MemReductExcludedProcesses { get; set; } = new();
        public bool EnablePowerTools { get; set; } = true;
        public string GetApiKeyForProvider(string provider) => "";
        public void Save(bool notify = true) { }
        public static string InferProviderFromModel(string? model) => model switch
        {
            "gpt-test" or "openai" => "openai",
            "claude-test" or "anthropic" => "anthropic",
            "llama-test" or "groq" => "groq",
            _ => "gemini"
        };
    }
}
