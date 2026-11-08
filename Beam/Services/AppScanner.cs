using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Beam.Interop;
using Beam.Models;

namespace Beam.Services
{
    public class CachedAppRecord
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
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

        public static List<LauncherItem> ScanApps(bool allowCache = true)
        {
            if (allowCache && File.Exists(CacheFile))
            {
                try
                {
                    var json = File.ReadAllText(CacheFile);
                    var cached = JsonSerializer.Deserialize<List<CachedAppRecord>>(json);
                    if (cached != null && cached.Count > 0 && cached.Any(c => c.Path.Contains("!")))
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
                            var displayPath = Path.GetDirectoryName(file) ?? file;

                            cacheRecords.Add(new CachedAppRecord
                            {
                                Name = fileName,
                                Path = file,
                                Subtitle = displayPath,
                                Category = "Application"
                            });

                            list.Add(new LauncherItem
                            {
                                Id = "app:" + fileName.ToLowerInvariant(),
                                Name = fileName,
                                Subtitle = displayPath,
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

                        cacheRecords.Add(new CachedAppRecord
                        {
                            Name = app.Name,
                            Path = aumid,
                            Subtitle = "Windows App",
                            Category = "Application"
                        });

                        list.Add(new LauncherItem
                        {
                            Id = "app:" + app.Name.ToLowerInvariant(),
                            Name = app.Name,
                            Subtitle = "Windows App",
                            Category = "Application",
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
                bool isUwp = r.Path.Contains("!") || r.Path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase) || r.Path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase);
                if (!isUwp && !File.Exists(r.Path)) continue;
                if (!scannedNames.Add(r.Name)) continue;

                var icon = ShellIconHelper.GetIcon(r.Path);
                string pathCopy = r.Path;
                bool isUwpApp = isUwp;

                list.Add(new LauncherItem
                {
                    Id = "app:" + r.Name.ToLowerInvariant(),
                    Name = r.Name,
                    Subtitle = r.Subtitle,
                    Category = r.Category,
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

            var systemTools = new (string Name, string Path, string Category, string Subtitle)[]
            {
                ("PowerShell (Run as Administrator)", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "Application", "Windows PowerShell (Admin Console)"),
                ("PowerShell", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "Application", "powershell.exe"),
                ("Command Prompt (Admin)", @"C:\Windows\System32\cmd.exe", "Application", "Elevated Administrator Command Prompt"),
                ("Command Prompt", @"C:\Windows\System32\cmd.exe", "Application", @"C:\Windows\System32\cmd.exe"),
                ("Component Services", @"C:\Windows\System32\dcomcnfg.exe", "Application", "dcomcnfg.exe"),
                ("Computer Management", @"C:\Windows\System32\compmgmt.msc", "Application", "compmgmt.msc"),
                ("Color Management", @"C:\Windows\System32\colorcpl.exe", "Control Panel", "colorcpl.exe"),
                ("Control the Computer Without the Mouse or Keyboard", @"C:\Windows\System32\utilman.exe", "Control Panel", "Ease of Access Center"),
                ("Task Manager", @"C:\Windows\System32\taskmgr.exe", "Application", "taskmgr.exe"),
                ("Windows Terminal", "wt.exe", "Application", "Windows Terminal (wt.exe)"),
                ("Windows Settings", "ms-settings:", "System Preference", "System Settings (ms-settings:)"),
                ("Microsoft Store", "ms-windows-store:", "Application", "Microsoft Store (ms-windows-store:)"),
                ("File Explorer", @"C:\Windows\explorer.exe", "Application", "explorer.exe"),
                ("Notepad", @"C:\Windows\System32\notepad.exe", "Application", "notepad.exe"),
                ("Calculator", @"C:\Windows\System32\calc.exe", "Application", "calc.exe"),
                ("Control Panel", @"C:\Windows\System32\control.exe", "Control Panel", "control.exe"),
                ("Device Manager", @"C:\Windows\System32\devmgmt.msc", "Control Panel", "devmgmt.msc"),
                ("Disk Management", @"C:\Windows\System32\diskmgmt.msc", "Control Panel", "diskmgmt.msc"),
                ("Services", @"C:\Windows\System32\services.msc", "Control Panel", "services.msc"),
                ("Registry Editor", @"C:\Windows\regedit.exe", "Application", "regedit.exe"),
                ("Resource Monitor", @"C:\Windows\System32\resmon.exe", "Application", "resmon.exe")
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
                        Subtitle = tool.Subtitle,
                        Category = tool.Category,
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

        public static void LaunchUwpApp(string aumid)
        {
            try
            {
                if (aumid.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
                {
                    var uriPsi = new ProcessStartInfo(aumid)
                    {
                        UseShellExecute = true
                    };
                    Process.Start(uriPsi);
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = aumid.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
                        ? aumid
                        : $@"shell:AppsFolder\{aumid}",
                    UseShellExecute = true,
                    CreateNoWindow = true
                };
                Process.Start(psi);
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
                if (path.Contains("!") || path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase) || path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
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
                if (path.Contains("!") || path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase) || path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
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
                if (path.Contains("!") || path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase) || path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start("explorer.exe", "shell:AppsFolder");
                    return;
                }

                if (File.Exists(path) || Directory.Exists(path))
                {
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                }
                else
                {
                    Process.Start("explorer.exe");
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to reveal file: " + ex.Message, "Kestrel", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
