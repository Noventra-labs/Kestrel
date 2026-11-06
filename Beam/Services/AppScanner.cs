using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Beam.Interop;
using Beam.Models;

namespace Beam.Services
{
    public static class AppScanner
    {
        private static readonly List<FileSystemWatcher> Watchers = new();

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

        public static List<LauncherItem> ScanApps()
        {
            var list = new List<LauncherItem>();
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
                        IsAdminCapable = true,
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

            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public static void LaunchFile(string path)
        {
            try
            {
                var psi = new ProcessStartInfo(path)
                {
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to launch: " + ex.Message, "Beam", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void LaunchAsAdmin(string path)
        {
            try
            {
                var psi = new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to launch as Administrator: " + ex.Message, "Beam", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public static void RevealInExplorer(string path)
        {
            try
            {
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
                System.Windows.MessageBox.Show("Failed to reveal file: " + ex.Message, "Beam", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
