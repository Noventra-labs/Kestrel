using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Beam.Services
{
    public class BeamSettings
    {
        // 1. General & Hotkey Triggers
        public bool TriggerOnStartKey { get; set; } = true;
        public bool EnableAltSpaceHotkey { get; set; } = true;
        public bool LaunchOnStartup { get; set; } = true;
        public bool HideOnClickOutside { get; set; } = true;
        public bool EnableToastNotifications { get; set; } = true;
        public bool TrimMemoryOnHide { get; set; } = true;

        // 2. Modules & Extensions (Live Enable / Disable)
        public bool EnableAiFeatures { get; set; } = true;
        public bool EnableFileSearch { get; set; } = true;
        public bool EnableClipboardHistory { get; set; } = true;
        public bool EnableWindowManagement { get; set; } = true;
        public bool EnableCalculator { get; set; } = true;
        public bool EnableProcessKiller { get; set; } = true;
        public bool EnableSystemCommands { get; set; } = true;
        public bool EnableWebFallbacks { get; set; } = true;

        // 3. Gemini & Multi-Provider Local + Cloud AI Configuration
        public bool UseEmbeddedGeminiWeb { get; set; } = true;
        public string CloudBackendUrl { get; set; } = "https://beam-cloud-ai-77889729488.asia-south1.run.app";
        public string DefaultAiModel { get; set; } = "";
        public string ActiveAiProvider { get; set; } = "gemini";
        public string UserApiKeyOverride { get; set; } = "";

        // Local Multi-Provider API Key Vault (Users can insert & switch between keys locally anytime)
        public string GeminiApiKey { get; set; } = "";
        public string OpenAiApiKey { get; set; } = "";
        public string GroqApiKey { get; set; } = "";
        public string AnthropicApiKey { get; set; } = "";

        // Optional named custom key profiles so users can swap between multiple keys for the same or different providers
        public Dictionary<string, string> SavedKeyProfiles { get; set; } = new();

        public static BeamSettings Current { get; private set; } = new();
        public static event Action<BeamSettings>? SettingsChanged;

        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Beam");

        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");

        static BeamSettings()
        {
            Current = Load();
        }

        public static string InferProviderFromModel(string? model)
        {
            var m = (model ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(m))
                return Current?.ActiveAiProvider ?? "gemini";
            if (m.StartsWith("gpt") || m.StartsWith("o1") || m.StartsWith("o3") || m.Contains("openai"))
                return "openai";
            if (m.StartsWith("claude") || m.Contains("anthropic"))
                return "anthropic";
            if (m.StartsWith("llama") || m.StartsWith("mixtral") || m.StartsWith("deepseek") || m.Contains("groq"))
                return "groq";
            return "gemini";
        }

        public string GetApiKeyForProvider(string? provider)
        {
            var p = (provider ?? "gemini").Trim().ToLowerInvariant();
            string key = p switch
            {
                "openai" => OpenAiApiKey,
                "groq" => GroqApiKey,
                "anthropic" => AnthropicApiKey,
                _ => GeminiApiKey
            };

            if (!string.IsNullOrWhiteSpace(key))
                return key.Trim();

            // Fallback to legacy single field if migrating
            if (!string.IsNullOrWhiteSpace(UserApiKeyOverride))
                return UserApiKeyOverride.Trim();

            return "";
        }

        public void SetApiKeyForProvider(string? provider, string? apiKey)
        {
            var p = (provider ?? "gemini").Trim().ToLowerInvariant();
            var cleaned = (apiKey ?? "").Trim();

            switch (p)
            {
                case "openai":
                    OpenAiApiKey = cleaned;
                    break;
                case "groq":
                    GroqApiKey = cleaned;
                    break;
                case "anthropic":
                    AnthropicApiKey = cleaned;
                    break;
                default:
                    GeminiApiKey = cleaned;
                    break;
            }

            // Keep UserApiKeyOverride synced with the currently active provider
            if (string.Equals(ActiveAiProvider, p, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(InferProviderFromModel(DefaultAiModel), p, StringComparison.OrdinalIgnoreCase))
            {
                UserApiKeyOverride = cleaned;
            }
        }

        public string GetApiKeyForModel(string? model)
        {
            var provider = InferProviderFromModel(model);
            var key = GetApiKeyForProvider(provider);
            if (!string.IsNullOrWhiteSpace(key))
                return key;

            return UserApiKeyOverride?.Trim() ?? "";
        }

        public static BeamSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    var settings = JsonSerializer.Deserialize<BeamSettings>(json);
                    if (settings != null)
                    {
                        if (string.Equals(settings.DefaultAiModel, "gemini-2.0-flash", StringComparison.OrdinalIgnoreCase))
                        {
                            settings.DefaultAiModel = "";
                        }

                        // Migrate legacy single key into provider-specific slot if empty
                        if (!string.IsNullOrWhiteSpace(settings.UserApiKeyOverride))
                        {
                            var inferred = InferProviderFromModel(settings.DefaultAiModel);
                            if (string.IsNullOrWhiteSpace(settings.GetApiKeyForProvider(inferred)))
                            {
                                settings.SetApiKeyForProvider(inferred, settings.UserApiKeyOverride);
                            }
                        }
                        Current = settings;
                        return settings;
                    }
                }
            }
            catch { }

            var defaultSettings = new BeamSettings();
            Current = defaultSettings;
            defaultSettings.Save(notify: false);
            return defaultSettings;
        }

        public void Save(bool notify = true)
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                {
                    Directory.CreateDirectory(SettingsFolder);
                }

                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);

                Current = this;
                ApplyWindowsStartupRegistry(LaunchOnStartup);

                if (notify)
                {
                    SettingsChanged?.Invoke(this);
                }
            }
            catch { }
        }

        public static void ApplyWindowsStartupRegistry(bool enable)
        {
            try
            {
                const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
                using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
                if (key == null) return;

                if (enable)
                {
                    var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exePath) && exePath.EndsWith("Beam.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue("BeamLauncher", $"\"{exePath}\" --tray");
                    }
                }
                else
                {
                    key.DeleteValue("BeamLauncher", false);
                }
            }
            catch { }
        }
    }
}
