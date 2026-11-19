using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Kestrel.Services
{
    public sealed record BrowserInfo(string Id, string Name, string ExecutablePath, string PrivateFlag);

    public static class QuickOperationsService
    {
        private static readonly object _cacheLock = new();
        private static List<BrowserInfo>? _cachedBrowsers;
        private static DateTime _cacheExpiresUtc = DateTime.MinValue;

        private sealed record BrowserSpec(
            string Id,
            string Name,
            string ExeName,
            string PrivateFlag,
            string[] ProgIdKeywords,
            string[] CandidateRelativePaths);

        private static readonly BrowserSpec[] KnownBrowsers =
        {
            new("zen", "Zen Browser", "zen.exe", "-private-window",
                new[] { "zen" },
                new[]
                {
                    @"%LOCALAPPDATA%\Programs\Zen Browser\zen.exe",
                    @"%ProgramFiles%\Zen Browser\zen.exe"
                }),
            new("chrome", "Google Chrome", "chrome.exe", "--incognito",
                new[] { "chrome" },
                new[]
                {
                    @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
                    @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
                    @"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"
                }),
            new("edge", "Microsoft Edge", "msedge.exe", "-inprivate",
                new[] { "msedge", "edge" },
                new[]
                {
                    @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
                    @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"
                }),
            new("brave", "Brave", "brave.exe", "--incognito",
                new[] { "brave" },
                new[]
                {
                    @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe",
                    @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe",
                    @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\Application\brave.exe"
                }),
            new("firefox", "Firefox", "firefox.exe", "-private-window",
                new[] { "firefox" },
                new[]
                {
                    @"%ProgramFiles%\Mozilla Firefox\firefox.exe",
                    @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe"
                }),
            new("vivaldi", "Vivaldi", "vivaldi.exe", "--incognito",
                new[] { "vivaldi" },
                new[]
                {
                    @"%LOCALAPPDATA%\Vivaldi\Application\vivaldi.exe",
                    @"%ProgramFiles%\Vivaldi\Application\vivaldi.exe"
                }),
            new("opera", "Opera", "opera.exe", "--private",
                new[] { "opera" },
                new[]
                {
                    @"%LOCALAPPDATA%\Programs\Opera\opera.exe",
                    @"%LOCALAPPDATA%\Programs\Opera GX\opera.exe",
                    @"%ProgramFiles%\Opera\opera.exe"
                })
        };

        public static bool TryParseIncognitoQuery(string? query, out string target)
        {
            target = string.Empty;
            if (string.IsNullOrWhiteSpace(query))
                return false;

            string trimmed = query.Trim();
            foreach (string kw in new[] { "incognito", "inprivate", "private", "incog" })
            {
                if (trimmed.Equals(kw, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (trimmed.StartsWith(kw + " ", StringComparison.OrdinalIgnoreCase))
                {
                    target = trimmed[(kw.Length + 1)..].Trim();
                    return true;
                }
            }

            return false;
        }

        public static bool TryParseFileSearchQuery(string? query, out string fileQuery)
        {
            fileQuery = string.Empty;
            if (string.IsNullOrWhiteSpace(query))
                return false;

            string trimmed = query.Trim();
            foreach (string kw in new[] { "files", "file", "find" })
            {
                if (trimmed.Equals(kw, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (trimmed.StartsWith(kw + " ", StringComparison.OrdinalIgnoreCase))
                {
                    fileQuery = trimmed[(kw.Length + 1)..].Trim();
                    return true;
                }
            }

            if (trimmed.StartsWith("f ", StringComparison.OrdinalIgnoreCase))
            {
                fileQuery = trimmed[2..].Trim();
                return true;
            }

            return false;
        }

        public static string[] BuildPrivateBrowsingArguments(string privateFlag, string? urlOrQuery)
        {
            if (string.IsNullOrWhiteSpace(urlOrQuery))
                return new[] { privateFlag };

            string val = urlOrQuery.Trim();
            if (Uri.TryCreate(val, UriKind.Absolute, out var abs) &&
                (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            {
                return new[] { privateFlag, abs.AbsoluteUri };
            }

            if (!val.Contains(' ') &&
                val.Contains('.') &&
                !val.StartsWith('-') &&
                !val.StartsWith('.') &&
                !val.EndsWith('.') &&
                Uri.TryCreate("https://" + val, UriKind.Absolute, out var hostUri))
            {
                return new[] { privateFlag, hostUri.AbsoluteUri };
            }

            return new[] { privateFlag, "https://www.google.com/search?q=" + Uri.EscapeDataString(val) };
        }

        public static List<BrowserInfo> DetectInstalledBrowsers(bool forceRefresh = false)
        {
            lock (_cacheLock)
            {
                if (!forceRefresh && _cachedBrowsers != null && DateTime.UtcNow < _cacheExpiresUtc)
                {
                    return new List<BrowserInfo>(_cachedBrowsers);
                }
            }

            var found = new List<BrowserInfo>();
            foreach (var spec in KnownBrowsers)
            {
                string? exePath = ResolveBrowserExecutable(spec);
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    found.Add(new BrowserInfo(spec.Id, spec.Name, exePath, spec.PrivateFlag));
                }
            }

            string defaultProgId = GetDefaultHttpProgId();
            if (!string.IsNullOrEmpty(defaultProgId) && found.Count > 1)
            {
                int defaultIdx = found.FindIndex(b =>
                {
                    var spec = KnownBrowsers.First(k => k.Id == b.Id);
                    return spec.ProgIdKeywords.Any(kw => defaultProgId.Contains(kw, StringComparison.OrdinalIgnoreCase));
                });

                if (defaultIdx > 0)
                {
                    var defBrowser = found[defaultIdx];
                    found.RemoveAt(defaultIdx);
                    found.Insert(0, defBrowser);
                }
            }

            lock (_cacheLock)
            {
                _cachedBrowsers = found;
                _cacheExpiresUtc = DateTime.UtcNow.AddSeconds(60);
            }

            return new List<BrowserInfo>(found);
        }

        public static bool LaunchIncognito(BrowserInfo browser, string? urlOrQuery, out string? error)
        {
            error = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = browser.ExecutablePath,
                    UseShellExecute = false
                };

                foreach (string arg in BuildPrivateBrowsingArguments(browser.PrivateFlag, urlOrQuery))
                {
                    psi.ArgumentList.Add(arg);
                }

                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string? ResolveBrowserExecutable(BrowserSpec spec)
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{spec.ExeName}");
                    if (key?.GetValue(null) is string regPath)
                    {
                        string cleaned = regPath.Trim().Trim('"');
                        if (Path.IsPathFullyQualified(cleaned) &&
                            cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(cleaned))
                            return cleaned;
                    }
                }
                catch { }
            }

            foreach (string template in spec.CandidateRelativePaths)
            {
                try
                {
                    string expanded = Environment.ExpandEnvironmentVariables(template);
                    if (!expanded.Contains('%') && File.Exists(expanded))
                        return expanded;
                }
                catch { }
            }

            return null;
        }

        private static string GetDefaultHttpProgId()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
                return key?.GetValue("ProgId") as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
