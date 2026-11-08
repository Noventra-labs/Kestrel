using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Beam.Services
{
    public class UpdateInfo
    {
        public string LatestTag { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string HtmlUrl { get; set; } = string.Empty;
        public string InstallerUrl { get; set; } = string.Empty;
        public bool IsNewer { get; set; } = false;
    }

    public static class UpdateService
    {
        public static readonly Version CurrentVersion = new Version(2, 3, 1);
        private static readonly HttpClient _httpClient = new HttpClient();
        private static DateTime _lastCheckTime = DateTime.MinValue;
        public static UpdateInfo? CachedUpdateInfo { get; private set; }

        static UpdateService()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Kestrel-Client/2.3.1 (Windows NT 10.0; Win64; x64)");
            _httpClient.Timeout = TimeSpan.FromSeconds(6);
        }

        public static async Task<UpdateInfo?> CheckForUpdatesAsync(bool force = false)
        {
            if (!force && DateTime.UtcNow - _lastCheckTime < TimeSpan.FromHours(4) && CachedUpdateInfo != null)
            {
                return CachedUpdateInfo;
            }

            try
            {
                _lastCheckTime = DateTime.UtcNow;
                var res = await _httpClient.GetStringAsync("https://api.github.com/repos/Noventra-labs/Kestrel/releases/latest");
                using var doc = JsonDocument.Parse(res);
                var root = doc.RootElement;

                string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
                string body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";
                string htmlUrl = root.TryGetProperty("html_url", out var htmlElem) ? htmlElem.GetString() ?? "" : "";

                string cleanTag = tagName.TrimStart('v', 'V');
                bool isNewer = false;
                if (Version.TryParse(cleanTag, out var latestVer))
                {
                    isNewer = latestVer > CurrentVersion;
                }

                string installerUrl = "";
                if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsElem.EnumerateArray())
                    {
                        string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (name.Equals("Kestrel-Setup.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            installerUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() ?? "" : "";
                            break;
                        }
                    }
                }

                CachedUpdateInfo = new UpdateInfo
                {
                    LatestTag = tagName,
                    ReleaseNotes = body,
                    HtmlUrl = htmlUrl,
                    InstallerUrl = installerUrl,
                    IsNewer = isNewer
                };

                return CachedUpdateInfo;
            }
            catch
            {
                return CachedUpdateInfo;
            }
        }

        public static void OpenReleasePage(string? url = null)
        {
            try
            {
                string target = !string.IsNullOrEmpty(url) ? url : (CachedUpdateInfo?.HtmlUrl ?? "https://github.com/Noventra-labs/Kestrel/releases/latest");
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
