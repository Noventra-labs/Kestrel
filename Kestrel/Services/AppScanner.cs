using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Kestrel.Interop;
using Kestrel.Models;

namespace Kestrel.Services
{
    public class CachedAppRecord
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Keywords { get; set; } = string.Empty;
    }

    public static class AppScanner
    {
        private static readonly List<FileSystemWatcher> Watchers = new();
        private static readonly string CacheFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kestrel",
            "app-index.json");

        public static void StartWatchingChanges(Action onAppsChanged)
        {
            if (Watchers.Count > 0) return;

            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs",
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + @"\Programs",
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(folder, "*.lnk")
                    {
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true
                    };
                    watcher.Created += (s, e) => onAppsChanged();
                    watcher.Deleted += (s, e) => onAppsChanged();
                    watcher.Renamed += (s, e) => onAppsChanged();
                    Watchers.Add(watcher);
                }
                catch { }
            }
        }

        internal static bool IsUwpOrProtocolPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) ||
                   (path.Contains('!') && !Path.IsPathFullyQualified(path));
        }

        public static List<LauncherItem> ScanApps(bool allowCache = true)
        {
            if (allowCache && File.Exists(CacheFile))
            {
                try
                {
                    var json = File.ReadAllText(CacheFile);
                    var cached = JsonSerializer.Deserialize<List<CachedAppRecord>>(json);
                    if (cached != null && cached.Count > 0 && cached.Any(c => IsUwpOrProtocolPath(c.Path)))
                    {
                        return ReconstructFromCache(cached);
                    }
                }
                catch { }
            }

            var list = new List<LauncherItem>();
            var cacheRecords = new List<CachedAppRecord>();
            var scannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var foldersToScan = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs",
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + @"\Programs",
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            foreach (var folder in foldersToScan)
            {
                if (!Directory.Exists(folder)) continue;

                try
                {
                    var files = Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        if (fileName.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (scannedNames.Add(fileName))
                        {
                            var icon = ShellIconHelper.GetIcon(file);

                            cacheRecords.Add(new CachedAppRecord
                            {
                                Name = fileName,
                                Path = file,
                                Subtitle = string.Empty,
                                Category = "Application"
                            });

                            list.Add(new LauncherItem
                            {
                                Id = "app:" + fileName.ToLowerInvariant(),
                                Name = fileName,
                                Subtitle = string.Empty,
                                Category = "Application",
                                Icon = icon,
                                Path = file,
                                IsAdminCapable = true,
                                Action = () => LaunchFile(file)
                            });
                        }
                    }
                }
                catch { }
            }

            // Scan modern packaged apps from Microsoft Store / Windows UWP
            ScanStoreApps(list, cacheRecords, scannedNames);

            // Persist app index cache asynchronously
            Task.Run(() =>
            {
                try
                {
                    var dir = Path.GetDirectoryName(CacheFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    var json = JsonSerializer.Serialize(cacheRecords);
                    File.WriteAllText(CacheFile, json);
                }
                catch { }
            });

            AppendSystemTools(list, scannedNames);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static void ScanStoreApps(List<LauncherItem> list, List<CachedAppRecord> cacheRecords, HashSet<string> scannedNames)
        {
            try
            {
                var storeApps = StaWorker.Run(EnumerateStoreAppsInternal);
                foreach (var app in storeApps)
                {
                    if (scannedNames.Add(app.Name))
                    {
                        var icon = ShellIconHelper.GetUwpIcon(app.Aumid);
                        string aumid = app.Aumid;

                        string keywords = "app store windows app uwp msix";
                        if (app.Name.Equals("Microsoft Store", StringComparison.OrdinalIgnoreCase) || aumid.Contains("WindowsStore", StringComparison.OrdinalIgnoreCase))
                        {
                            keywords = "store app appstore shop market windows store uwp download software microsoft buy";
                        }

                        cacheRecords.Add(new CachedAppRecord
                        {
                            Name = app.Name,
                            Path = aumid,
                            Subtitle = string.Empty,
                            Category = "Application",
                            Keywords = keywords
                        });

                        list.Add(new LauncherItem
                        {
                            Id = "app:" + app.Name.ToLowerInvariant(),
                            Name = app.Name,
                            Subtitle = string.Empty,
                            Category = "Application",
                            Keywords = keywords,
                            Icon = icon,
                            Path = aumid,
                            IsAdminCapable = false,
                            Action = () => LaunchUwpApp(aumid)
                        });
                    }
                }
            }
            catch { }
        }

        private static List<(string Name, string Aumid)> EnumerateStoreAppsInternal()
        {
            var result = new List<(string Name, string Aumid)>();
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return result;

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic appsFolder = shell.NameSpace("shell:AppsFolder");
                if (appsFolder == null) return result;

                foreach (var item in appsFolder.Items())
                {
                    try
                    {
                        string name = item.Name;
                        string path = item.Path;

                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                            continue;

                        // Only target packaged UWP/MSIX apps (AUMID contains '!')
                        if (!path.Contains("!"))
                            continue;

                        // Skip uninstalled placeholders, resource strings, or uninstallers
                        if (name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("@{", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                            continue;

                        result.Add((name, path));
                    }
                    catch { }
                }
            }
            catch { }

            return result;
        }

        private static List<LauncherItem> ReconstructFromCache(List<CachedAppRecord> records)
        {
            var list = new List<LauncherItem>(records.Count + 20);
            var scannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in records)
            {
                bool isUwp = IsUwpOrProtocolPath(r.Path);
                if (isUwp)
                {
                    if (r.Path.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0 || r.Path.Contains("..", StringComparison.Ordinal))
                        continue;
                }
                else
                {
                    if (!Path.IsPathFullyQualified(r.Path) || !File.Exists(r.Path))
                        continue;
                }
                if (!scannedNames.Add(r.Name)) continue;

                var icon = ShellIconHelper.GetIcon(r.Path);
                string pathCopy = r.Path;
                bool isUwpApp = isUwp;

                string kw = r.Keywords;
                if (string.IsNullOrEmpty(kw))
                {
                    if (r.Name.Equals("Microsoft Store", StringComparison.OrdinalIgnoreCase) || r.Path.Contains("WindowsStore", StringComparison.OrdinalIgnoreCase))
                    {
                        kw = "store app appstore shop market windows store uwp download software microsoft buy";
                    }
                    else if (r.Subtitle.Equals("Windows App", StringComparison.OrdinalIgnoreCase) || IsUwpOrProtocolPath(r.Path))
                    {
                        kw = "app store windows app uwp msix";
                    }
                }

                list.Add(new LauncherItem
                {
                    Id = "app:" + r.Name.ToLowerInvariant(),
                    Name = r.Name,
                    Subtitle = string.Empty,
                    Category = r.Category,
                    Keywords = kw ?? string.Empty,
                    Icon = icon,
                    Path = pathCopy,
                    IsAdminCapable = !isUwpApp,
                    Action = () =>
                    {
                        if (isUwpApp)
                        {
                            LaunchUwpApp(pathCopy);
                        }
                        else
                        {
                            LaunchFile(pathCopy);
                        }
                    }
                });
            }

            AppendSystemTools(list, scannedNames);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static void AppendSystemTools(List<LauncherItem> list, HashSet<string> scannedNames)
        {
            var defaultSysIcon = IconGenerator.GetSquircleIcon(SquircleTheme.SettingsSlate, IconGenerator.PathGear);
            var defaultTermIcon = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathTerminal);

            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string localWinAppsWt = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "wt.exe");
            string wtPath = File.Exists(localWinAppsWt) ? localWinAppsWt : "wt.exe";

            var systemTools = new (string Name, string Path, string Category)[]
            {
                ("PowerShell (Run as Administrator)", Path.Combine(sysDir, "WindowsPowerShell", "v1.0", "powershell.exe"), "Application"),
                ("PowerShell", Path.Combine(sysDir, "WindowsPowerShell", "v1.0", "powershell.exe"), "Application"),
                ("Command Prompt (Admin)", Path.Combine(sysDir, "cmd.exe"), "Application"),
                ("Command Prompt", Path.Combine(sysDir, "cmd.exe"), "Application"),
                ("Component Services", Path.Combine(sysDir, "dcomcnfg.exe"), "Application"),
                ("Computer Management", Path.Combine(sysDir, "compmgmt.msc"), "Application"),
                ("Color Management", Path.Combine(sysDir, "colorcpl.exe"), "Control Panel"),
                ("Ease of Access Center", Path.Combine(sysDir, "utilman.exe"), "Control Panel"),
                ("Task Manager", Path.Combine(sysDir, "taskmgr.exe"), "Application"),
                ("Windows Terminal", wtPath, "Application"),
                ("Windows Settings", "ms-settings:", "System Preference"),
                ("Microsoft Store", "ms-windows-store:", "Application"),
                ("File Explorer", Path.Combine(winDir, "explorer.exe"), "Application"),
                ("Notepad", Path.Combine(sysDir, "notepad.exe"), "Application"),
                ("Calculator", Path.Combine(sysDir, "calc.exe"), "Application"),
                ("Control Panel", Path.Combine(sysDir, "control.exe"), "Control Panel"),
                ("Device Manager", Path.Combine(sysDir, "devmgmt.msc"), "Control Panel"),
                ("Disk Management", Path.Combine(sysDir, "diskmgmt.msc"), "Control Panel"),
                ("Services", Path.Combine(sysDir, "services.msc"), "Control Panel"),
                ("Registry Editor", Path.Combine(winDir, "regedit.exe"), "Application"),
                ("Resource Monitor", Path.Combine(sysDir, "resmon.exe"), "Application")
            };

            foreach (var tool in systemTools)
            {
                if (scannedNames.Add(tool.Name))
                {
                    bool isAdminTarget = tool.Name.Contains("Admin", StringComparison.OrdinalIgnoreCase);

                    ImageSource icon;
                    if (isAdminTarget)
                    {
                        icon = IconGenerator.GetSquircleIcon(SquircleTheme.AdminGold, IconGenerator.PathShield, asStroke: true);
                    }
                    else if (tool.Name.Contains("PowerShell") || tool.Name.Contains("Command Prompt") || tool.Name.Contains("Terminal"))
                    {
                        icon = IconGenerator.GetSquircleIcon(SquircleTheme.TerminalSlate, IconGenerator.PathTerminal, asStroke: true);
                    }
                    else if (tool.Path.StartsWith("ms-windows-store:", StringComparison.OrdinalIgnoreCase))
                    {
                        icon = ShellIconHelper.GetUwpIcon("Microsoft.WindowsStore_8wekyb3d8bbwe!App");
                    }
                    else if (File.Exists(tool.Path))
                    {
                        icon = ShellIconHelper.GetIcon(tool.Path);
                    }
                    else
                    {
                        icon = defaultSysIcon;
                    }

                    list.Add(new LauncherItem
                    {
                        Id = "sys:" + tool.Name.ToLowerInvariant(),
                        Name = tool.Name,
                        Subtitle = string.Empty,
                        Category = tool.Category,
                        Keywords = GetSystemToolKeywords(tool.Name, tool.Path),
                        Icon = icon,
                        Path = tool.Path,
                        IsAdminCapable = !tool.Path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase),
                        Action = () =>
                        {
                            if (isAdminTarget)
                            {
                                LaunchAsAdmin(tool.Path);
                            }
                            else
                            {
                                LaunchFile(tool.Path);
                            }
                        }
                    });
                }
            }
        }

        private static string GetSystemToolKeywords(string name, string path)
        {
            if (name.Equals("Microsoft Store", StringComparison.OrdinalIgnoreCase) || path.StartsWith("ms-windows-store:", StringComparison.OrdinalIgnoreCase))
                return "store app appstore shop market windows store uwp download software microsoft buy";
            if (name.Contains("Settings") || path.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
                return "settings system control preferences configure config options";
            if (name.Contains("Calculator") || path.Contains("calc"))
                return "calculator calc math calculate compute arithmetic";
            if (name.Contains("Task Manager") || path.Contains("taskmgr"))
                return "task manager taskmgr activity performance kill processes monitor";
            if (name.Contains("Notepad"))
                return "notepad text editor notes";
            if (name.Contains("Command Prompt") || path.Contains("cmd"))
                return "cmd command prompt terminal console admin";
            if (name.Contains("PowerShell"))
                return "powershell pwsh terminal console admin scripting";
            if (name.Contains("Terminal"))
                return "terminal wt console bash cmd prompt powershell";
            if (name.Contains("File Explorer"))
                return "explorer files folders browser";
            if (name.Contains("Registry"))
                return "regedit registry editor";
            if (name.Contains("Resource Monitor"))
                return "resmon resource monitor cpu memory disk";
            if (name.Contains("Device Manager"))
                return "devmgmt device manager drivers hardware";
            if (name.Contains("Disk Management"))
                return "diskmgmt disk management partitions storage";
            if (name.Contains("Services"))
                return "services msc background daemon";
            if (name.Contains("Control Panel"))
                return "control panel system settings";
            return string.Empty;
        }

        public static void LaunchUwpApp(string aumid)
        {
            try
            {
                // 1. Direct URI protocol launch (e.g. ms-windows-store:, ms-settings:)
                if (aumid.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo(aumid) { UseShellExecute = true });
                    return;
                }

                // If Microsoft Store AUMID, prefer direct ms-windows-store: protocol
                if (aumid.Contains("WindowsStore", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo("ms-windows-store:") { UseShellExecute = true });
                    return;
                }

                // 2. Direct ShellExecute on shell:AppsFolder without spawning secondary explorer.exe process
                string target = aumid.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
                    ? aumid
                    : $@"shell:AppsFolder\{aumid}";

                try
                {
                    var psi = new ProcessStartInfo(target)
                    {
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    return;
                }
                catch { }

                // 3. Fallback: Native Shell.Application COM FolderItem InvokeVerb
                try
                {
                    var shellType = Type.GetTypeFromProgID("Shell.Application");
                    if (shellType != null)
                    {
                        dynamic shell = Activator.CreateInstance(shellType)!;
                        dynamic appsFolder = shell.NameSpace("shell:AppsFolder");
                        if (appsFolder != null)
                        {
                            foreach (var item in appsFolder.Items())
                            {
                                if (string.Equals((string)item.Path, aumid, StringComparison.OrdinalIgnoreCase))
                                {
                                    item.InvokeVerb();
                                    return;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to launch application: " + ex.Message, "Kestrel", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void LaunchFile(string path)
        {
            try
            {
                if (IsUwpOrProtocolPath(path))
                {
                    LaunchUwpApp(path);
                    return;
                }

                var psi = new ProcessStartInfo(path)
                {
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to launch: " + ex.Message, "Kestrel", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void LaunchAsAdmin(string path)
        {
            try
            {
                if (IsUwpOrProtocolPath(path))
                {
                    // Packaged / UWP apps run inside an AppContainer sandbox and cannot be elevated via Win32 'runas'
                    LaunchUwpApp(path);
                    return;
                }

                var psi = new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to launch as Administrator: " + ex.Message, "Kestrel", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public static void RevealInExplorer(string path)
        {
            try
            {
                string explorerExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                if (!File.Exists(explorerExe)) explorerExe = "explorer.exe";

                if (IsUwpOrProtocolPath(path))
                {
                    Process.Start(new ProcessStartInfo(explorerExe) { ArgumentList = { "shell:AppsFolder" } });
                    return;
                }

                if (!string.IsNullOrWhiteSpace(path) &&
                    path.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) < 0 &&
                    Path.IsPathFullyQualified(path) &&
                    (File.Exists(path) || Directory.Exists(path)))
                {
                    string full = Path.GetFullPath(path);
                    Process.Start(explorerExe, $"/select,\"{full}\"");
                }
                else
                {
                    Process.Start(explorerExe);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to reveal file: " + ex.Message, "Kestrel", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
