using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using Beam.Interop;
using Beam.Models;

namespace Beam.Services
{
    public static class SystemCommandsService
    {
        [DllImport("user32.dll")]
        private static extern bool LockWorkStation();

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const byte VK_VOLUME_MUTE = 0xAD;
        private const byte VK_VOLUME_DOWN = 0xAE;
        private const byte VK_VOLUME_UP = 0xAF;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public static List<LauncherItem> GetSystemCommands(
            Action openClipboardView,
            Action openProcessKillerView,
            Action openFileSearchView,
            Action openSettingsView,
            Action triggerConfetti,
            Action<bool, string> openGeminiChat)
        {
            var cfg = BeamSettings.Current;

            var iconAi = IconGenerator.GetSquircleIcon(SquircleTheme.BrandCoral, IconGenerator.PathSparkles);
            var iconClip = IconGenerator.GetSquircleIcon(SquircleTheme.ClipAmber, IconGenerator.PathClipboard);
            var iconProc = IconGenerator.GetSquircleIcon(SquircleTheme.ProcessRose, IconGenerator.PathCpu);
            var iconSearch = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathSearch);
            var iconLock = IconGenerator.GetSquircleIcon(SquircleTheme.SystemPurple, IconGenerator.PathLock);
            var iconPower = IconGenerator.GetSquircleIcon(SquircleTheme.ProcessRose, IconGenerator.PathPower);
            var iconTrash = IconGenerator.GetSquircleIcon(SquircleTheme.SettingsSlate, IconGenerator.PathTrash);
            var iconVol = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathVolume);
            var iconMoon = IconGenerator.GetSquircleIcon(SquircleTheme.SystemPurple, IconGenerator.PathMoon);
            var iconKey = IconGenerator.GetSquircleIcon(SquircleTheme.CalcGreen, IconGenerator.PathKey);
            var iconStar = IconGenerator.GetSquircleIcon(SquircleTheme.GoldStar, IconGenerator.PathStar);
            var iconGear = IconGenerator.GetSquircleIcon(SquircleTheme.SettingsSlate, IconGenerator.PathGear);
            var iconGlobe = IconGenerator.GetSquircleIcon(SquircleTheme.WebCyan, IconGenerator.PathGlobe);

            var commands = new List<LauncherItem>();

            // 1. AI & Embedded Gemini Chat Commands
            if (cfg.EnableAiFeatures)
            {
                commands.Add(new LauncherItem
                {
                    Id = "cmd:gemini_chat",
                    Name = "Gemini Chat",
                    Subtitle = "Open Google Gemini AI inside Kestrel",
                    Category = "AI Command",
                    Section = "Commands",
                    ShortcutBadge = "Tab",
                    Icon = iconAi,
                    KeepOpenOnExecute = true,
                    Action = () => openGeminiChat(true, "")
                });

                commands.Add(new LauncherItem
                {
                    Id = "cmd:ai_fix_grammar",
                    Name = "Fix Spelling & Grammar (Clipboard)",
                    Subtitle = "Gemini AI — Proofread and fix grammar of copied clipboard text",
                    Category = "AI Command",
                    Section = "Commands",
                    Icon = iconAi,
                    KeepOpenOnExecute = true,
                    Action = () =>
                    {
                        string clip = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
                        openGeminiChat(false, $"Fix the spelling and grammar of the following text and output only the corrected version:\n\n{clip}");
                    }
                });

                commands.Add(new LauncherItem
                {
                    Id = "cmd:ai_summarize",
                    Name = "Summarize Clipboard Text",
                    Subtitle = "Gemini AI — Create a concise bulleted summary of copied text",
                    Category = "AI Command",
                    Section = "Commands",
                    Icon = iconAi,
                    KeepOpenOnExecute = true,
                    Action = () =>
                    {
                        string clip = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
                        openGeminiChat(false, $"Summarize the following text into key bullet points:\n\n{clip}");
                    }
                });

                commands.Add(new LauncherItem
                {
                    Id = "cmd:ai_explain_code",
                    Name = "Explain Code in Clipboard",
                    Subtitle = "Gemini AI — Explain how the code in your clipboard works",
                    Category = "AI Command",
                    Section = "Commands",
                    Icon = iconAi,
                    KeepOpenOnExecute = true,
                    Action = () =>
                    {
                        string clip = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
                        openGeminiChat(false, $"Explain this code clearly and concisely:\n\n{clip}");
                    }
                });
            }

            // 2. Local File Search Command
            if (cfg.EnableFileSearch)
            {
                commands.Add(new LauncherItem
                {
                    Id = "cmd:filesearch",
                    Name = "Search Files",
                    Subtitle = "Instant fuzzy search across Desktop, Downloads, Documents, and Projects",
                    Category = "Command",
                    Section = "Commands",
                    ShortcutBadge = "Ctrl+F",
                    Icon = iconSearch,
                    KeepOpenOnExecute = true,
                    Action = openFileSearchView
                });
            }

            // 3. Clipboard History Command
            if (cfg.EnableClipboardHistory)
            {
                commands.Add(new LauncherItem
                {
                    Id = "cmd:clipboard",
                    Name = "Clipboard History",
                    Subtitle = "Search, view, and paste from your recent clipboard items",
                    Category = "Command",
                    Section = "Commands",
                    ShortcutBadge = "Ctrl+Shift+V",
                    Icon = iconClip,
                    KeepOpenOnExecute = true,
                    Action = openClipboardView
                });
            }

            // 4. Process Killer Command
            if (cfg.EnableProcessKiller)
            {
                commands.Add(new LauncherItem
                {
                    Id = "cmd:killproc",
                    Name = "Kill Process",
                    Subtitle = "Inspect running processes by RAM usage and terminate frozen apps",
                    Category = "Command",
                    Section = "Commands",
                    Icon = iconProc,
                    KeepOpenOnExecute = true,
                    Action = openProcessKillerView
                });
            }

            // Mem Reduct Engine (henrypp/memreduct)
            if (cfg.EnableMemReduct)
            {
                var iconMem = IconGenerator.GetSquircleIcon(SquircleTheme.ProcessRose, IconGenerator.PathMemory);
                var mem = MemoryReductService.GetMemoryStats();
                bool isElevated = MemoryReductService.IsElevated;

                commands.Add(new LauncherItem
                {
                    Id = "cmd:memreduct_clean",
                    Name = "Clean & Reduce Memory (Mem Reduct)",
                    Subtitle = $"RAM: {mem.LoadPercent}% used ({mem.UsedPhysGb:F1} GB / {mem.TotalPhysGb:F1} GB) • {(isElevated ? "[Admin] Full kernel caches & working sets" : "Working sets sweep & RAM reduction")}",
                    Category = "System Tool",
                    Section = "Commands",
                    ShortcutBadge = "Clean RAM",
                    Icon = iconMem,
                    Action = () =>
                    {
                        Task.Run(() =>
                        {
                            var res = MemoryReductService.ReduceMemory(
                                purgeStandby: MemoryReductService.IsElevated,
                                flushModified: MemoryReductService.IsElevated,
                                sweepProcesses: true);

                            App.Current.Dispatcher.Invoke(() =>
                            {
                                if (res.IsElevated)
                                {
                                    ToastNotification.Show($"✓ Kernel Cache & Standby Purged • Freed {res.FreedMb:N0} MB RAM • Usage {res.LoadBefore}% → {res.LoadAfter}%");
                                }
                                else
                                {
                                    ToastNotification.Show($"✓ Working Sets Swept • Freed {res.FreedMb:N0} MB RAM ({res.ProcessesCleaned} processes) • Usage {res.LoadBefore}% → {res.LoadAfter}%");
                                }
                            });
                        });
                    }
                });

                commands.Add(new LauncherItem
                {
                    Id = "cmd:memreduct_standby",
                    Name = "Purge Standby Memory List",
                    Subtitle = isElevated
                        ? "Mem Reduct — Flush Windows cached standby memory into free physical RAM"
                        : "Mem Reduct — Flush Windows standby cache (Elevated worker via UAC)",
                    Category = "System Tool",
                    Section = "Commands",
                    Icon = iconMem,
                    Action = () =>
                    {
                        Task.Run(() =>
                        {
                            if (MemoryReductService.IsElevated)
                            {
                                var res = MemoryReductService.ReduceMemory(purgeStandby: true, flushModified: false, sweepProcesses: false);
                                App.Current.Dispatcher.Invoke(() =>
                                {
                                    ToastNotification.Show($"✓ Standby Purged: Freed {res.FreedMb:N0} MB RAM • Usage {res.LoadBefore}% → {res.LoadAfter}%");
                                });
                            }
                            else
                            {
                                var before = MemoryReductService.GetMemoryStats();
                                bool ok = MemoryReductService.LaunchElevatedWorker("--clean-memory-standby");
                                var after = MemoryReductService.GetMemoryStats();
                                long freed = after.AvailPhysBytes - before.AvailPhysBytes;
                                double freedMb = Math.Max(0, freed / (1024.0 * 1024.0));

                                App.Current.Dispatcher.Invoke(() =>
                                {
                                    if (ok)
                                    {
                                        ToastNotification.Show($"✓ Standby Purged (Elevated Worker) • Freed {freedMb:N0} MB RAM • Usage {before.LoadPercent}% → {after.LoadPercent}%");
                                    }
                                    else
                                    {
                                        ToastNotification.Show("ℹ Notice: Purging standby cache requires Administrator permission");
                                    }
                                });
                            }
                        });
                    }
                });

                commands.Add(new LauncherItem
                {
                    Id = "cmd:memreduct_workingset",
                    Name = "Trim Working Sets (All Processes)",
                    Subtitle = "Mem Reduct — Empty working set memory across all running apps (Least-privilege, zero prompt)",
                    Category = "System Tool",
                    Section = "Commands",
                    Icon = iconMem,
                    Action = () =>
                    {
                        Task.Run(() =>
                        {
                            var res = MemoryReductService.ReduceMemory(purgeStandby: false, flushModified: false, sweepProcesses: true);
                            App.Current.Dispatcher.Invoke(() =>
                            {
                                ToastNotification.Show($"✓ Working Sets Swept • Cleaned {res.ProcessesCleaned} processes • Freed {res.FreedMb:N0} MB");
                            });
                        });
                    }
                });

                if (!isElevated)
                {
                    commands.Add(new LauncherItem
                    {
                        Id = "cmd:kestrel_restart_admin",
                        Name = "Restart Kestrel as Administrator",
                        Subtitle = "Relaunch Kestrel with elevated Administrator privileges for full kernel memory control",
                        Category = "System Tool",
                        Section = "Commands",
                        ShortcutBadge = "Admin 🛡",
                        Icon = IconGenerator.GetSquircleIcon(SquircleTheme.ProcessRose, IconGenerator.PathShield),
                        Action = () =>
                        {
                            App.RestartAsAdministrator();
                        }
                    });
                }
            }

            // Microsoft PowerToys Suite
            if (cfg.EnablePowerTools)
            {
                var iconPipette = IconGenerator.GetSquircleIcon(SquircleTheme.BrandCoral, IconGenerator.PathPipette);
                var iconOcr = IconGenerator.GetSquircleIcon(SquircleTheme.WebCyan, IconGenerator.PathScanText);
                var iconCoffee = IconGenerator.GetSquircleIcon(SquircleTheme.ClipAmber, IconGenerator.PathCoffee);
                var iconPin = IconGenerator.GetSquircleIcon(SquircleTheme.WindowBlue, IconGenerator.PathPin);
                var iconDev = IconGenerator.GetSquircleIcon(SquircleTheme.SystemPurple, IconGenerator.PathHash);

                commands.Add(new LauncherItem
                {
                    Id = "cmd:powertoys_colorpicker",
                    Name = "Color Picker (Screen Eyedropper)",
                    Subtitle = "PowerToys Color Picker — 8x pixel loupe magnifier to pick and copy HEX/RGB color",
                    Category = "PowerToys",
                    Section = "Commands",
                    ShortcutBadge = "Win+Shift+C",
                    Icon = iconPipette,
                    Action = () =>
                    {
                        var picker = new Beam.Views.ColorPickerWindow();
                        picker.Show();
                    }
                });

                commands.Add(new LauncherItem
                {
                    Id = "cmd:powertoys_text_extractor",
                    Name = "Text Extractor (Screen OCR)",
                    Subtitle = "PowerToys Text Extractor — Snip any area of screen to copy OCR text to clipboard",
                    Category = "PowerToys",
                    Section = "Commands",
                    ShortcutBadge = "Win+Shift+T",
                    Icon = iconOcr,
                    Action = () =>
                    {
                        var ocr = new Beam.Views.TextExtractorWindow();
                        ocr.Show();
                    }
                });

                commands.Add(new LauncherItem
                {
                    Id = "cmd:powertoys_awake_indefinite",
                    Name = "Awake: Keep Screen Awake (Indefinitely)",
                    Subtitle = "PowerToys Awake — Keep computer & screen awake indefinitely (prevent sleep & lock)",
                    Category = "PowerToys",
                    Section = "Commands",
                    ShortcutBadge = "Awake ☕",
                    Icon = iconCoffee,
                    Action = () =>
                    {
                        AwakeService.SetMode(AwakeMode.Indefinite);
                        ToastNotification.Show("☕ Awake: Screen will stay awake indefinitely");
                    }
                });

                commands.Add(new LauncherItem
                {
                    Id = "cmd:powertoys_awake_1h",
                    Name = "Awake: Keep Screen Awake (1 Hour)",
                    Subtitle = "PowerToys Awake — Keep computer & screen awake for 1 hour",
                    Category = "PowerToys",
                    Section = "Commands",
                    Icon = iconCoffee,
                    Action = () =>
                    {
                        AwakeService.SetMode(AwakeMode.Hours1);
                        ToastNotification.Show("☕ Awake: Screen will stay awake for 1 hour");
                    }
                });

                commands.Add(new LauncherItem
                {
                    Id = "cmd:powertoys_awake_30m",
                    Name = "Awake: Keep Screen Awake (30 Minutes)",
                    Subtitle = "PowerToys Awake — Keep computer & screen awake for 30 minutes",
                    Category = "PowerToys",
                    Section = "Commands",
                    Icon = iconCoffee,
                    Action = () =>
                    {
                        AwakeService.SetMode(AwakeMode.Minutes30);
                        ToastNotification.Show("☕ Awake: Screen will stay awake for 30 minutes");
                    }
                });

                commands.Add(new LauncherItem
                {
                    Id = "cmd:powertoys_awake_off",
                    Name = "Awake: Off (Allow Screen Sleep)",
                    Subtitle = "PowerToys Awake — Restore normal Windows power and screen sleep timers",
                    Category = "PowerToys",
                    Section = "Commands",
                    Icon = iconCoffee,
                    Action = () =>
                    {
                        AwakeService.SetMode(AwakeMode.Off);
                        ToastNotification.Show("✓ Awake disabled • Normal sleep mode active");
                    }
                });

                commands.Add(new LauncherItem
                {
                    Id = "cmd:powertoys_always_on_top",
                    Name = "Always on Top (Pin Window)",
                    Subtitle = "PowerToys Always on Top — Pin or unpin active window to stay topmost",
                    Category = "PowerToys",
                    Section = "Commands",
                    ShortcutBadge = "Pin 📌",
                    Icon = iconPin,
                    Action = () => WindowManagerService.ToggleAlwaysOnTop()
                });

                commands.Add(new LauncherItem
                {
                    Id = "cmd:powertoys_hosts_file",
                    Name = "Edit Windows Hosts File",
                    Subtitle = "PowerToys Hosts File Editor — Open hosts file in elevated Notepad",
                    Category = "PowerToys",
                    Section = "Commands",
                    Icon = iconDev,
                    Action = PowerDeveloperToolsService.OpenHostsFile
                });
            }

            // 5. Preferences & Confetti (Always available)
            commands.Add(new LauncherItem
            {
                Id = "cmd:settings",
                Name = "Kestrel Preferences & Settings",
                Subtitle = "Enable/disable File Search, AI, Window Snap, Hotkeys & Cloud AI",
                Category = "Preferences",
                Section = "Commands",
                ShortcutBadge = "Ctrl+,",
                Icon = iconGear,
                KeepOpenOnExecute = true,
                Action = openSettingsView
            });

            commands.Add(new LauncherItem
            {
                Id = "cmd:confetti",
                Name = "Confetti",
                Subtitle = "Shoot celebratory Raycast confetti across your screen",
                Category = "Command",
                Section = "Commands",
                Icon = iconStar,
                KeepOpenOnExecute = true,
                Action = triggerConfetti
            });

            // 6. System & Developer Controls
            if (cfg.EnableSystemCommands)
            {
                var iconShield = IconGenerator.GetSquircleIcon(SquircleTheme.AdminGold, IconGenerator.PathShield, asStroke: true);
                var iconTerm = IconGenerator.GetSquircleIcon(SquircleTheme.TerminalSlate, IconGenerator.PathTerminal, asStroke: true);

                commands.AddRange(new LauncherItem[]
                {
                    new()
                    {
                        Id = "cmd:powershell_admin",
                        Name = "PowerShell (Admin)",
                        Subtitle = "Open Windows PowerShell with elevated Administrator privileges",
                        Category = "Developer",
                        Section = "Commands",
                        Icon = iconShield,
                        Action = () => AppScanner.LaunchAsAdmin(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")
                    },
                    new()
                    {
                        Id = "cmd:cmd_admin",
                        Name = "Command Prompt (Admin)",
                        Subtitle = "Open Command Prompt (cmd.exe) with elevated Administrator privileges",
                        Category = "Developer",
                        Section = "Commands",
                        Icon = iconShield,
                        Action = () => AppScanner.LaunchAsAdmin(@"C:\Windows\System32\cmd.exe")
                    },
                    new()
                    {
                        Id = "cmd:wt_admin",
                        Name = "Windows Terminal (Admin)",
                        Subtitle = "Open Windows Terminal (wt.exe) with elevated Administrator privileges",
                        Category = "Developer",
                        Section = "Commands",
                        Icon = iconShield,
                        Action = () => AppScanner.LaunchAsAdmin("wt.exe")
                    },
                    new()
                    {
                        Id = "cmd:uuid",
                        Name = "Generate UUID / GUID",
                        Subtitle = "Generate a new v4 UUID and copy directly to clipboard",
                        Category = "Developer",
                        Section = "Commands",
                        Icon = iconKey,
                        Action = () =>
                        {
                            var uuid = Guid.NewGuid().ToString();
                            System.Windows.Clipboard.SetText(uuid);
                            ToastNotification.Show($"✓ Copied UUID: {uuid}");
                        }
                    },
                    new()
                    {
                        Id = "cmd:password",
                        Name = "Generate Strong Password",
                        Subtitle = "Generate a 20-character cryptographic password to clipboard",
                        Category = "Developer",
                        Section = "Commands",
                        Icon = iconKey,
                        Action = () =>
                        {
                            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()-_=+";
                            var bytes = RandomNumberGenerator.GetBytes(20);
                            var pass = new char[20];
                            for (int i = 0; i < 20; i++) pass[i] = chars[bytes[i] % chars.Length];
                            System.Windows.Clipboard.SetText(new string(pass));
                            ToastNotification.Show("✓ Copied 20-character Strong Password to Clipboard");
                        }
                    },
                    new()
                    {
                        Id = "cmd:local_ip",
                        Name = "Copy Local IPv4 Address",
                        Subtitle = $"Copy your LAN IP address ({GetLocalIpAddress()}) to clipboard",
                        Category = "Network",
                        Section = "Commands",
                        Icon = iconGlobe,
                        Action = () =>
                        {
                            var ip = GetLocalIpAddress();
                            System.Windows.Clipboard.SetText(ip);
                            ToastNotification.Show($"✓ Copied Local IPv4: {ip}");
                        }
                    },
                    new()
                    {
                        Id = "cmd:flush_dns",
                        Name = "Flush DNS Cache",
                        Subtitle = "Clear Windows DNS resolver cache (ipconfig /flushdns)",
                        Category = "Network",
                        Section = "System",
                        Icon = iconGlobe,
                        Action = () =>
                        {
                            RunHiddenCmd("ipconfig /flushdns");
                            ToastNotification.Show("✓ Windows DNS Cache Flushed");
                        }
                    },
                    new()
                    {
                        Id = "sys:darkmode",
                        Name = "Toggle Windows Dark / Light Mode",
                        Subtitle = "Switch Windows 11 system and app theme between Dark and Light",
                        Category = "System",
                        Section = "System",
                        Icon = iconMoon,
                        Action = () =>
                        {
                            ToggleWindowsDarkMode();
                            ToastNotification.Show("✓ Toggled Windows Dark / Light Theme");
                        }
                    },
                    new()
                    {
                        Id = "sys:screenshot",
                        Name = "Take Screenshot (Snipping Tool)",
                        Subtitle = "Open Windows region screen snip overlay",
                        Category = "System",
                        Section = "System",
                        ShortcutBadge = "Win+Shift+S",
                        Icon = iconStar,
                        Action = () => AppScanner.LaunchFile("ms-screenclip:")
                    },
                    new()
                    {
                        Id = "sys:vol_mute",
                        Name = "Mute / Unmute System Audio",
                        Subtitle = "Toggle master speaker volume mute state",
                        Category = "Audio",
                        Section = "System",
                        Icon = iconVol,
                        Action = () => { SendMediaKey(VK_VOLUME_MUTE); ToastNotification.Show("✓ Toggled Audio Mute"); }
                    },
                    new()
                    {
                        Id = "sys:vol_up",
                        Name = "Volume Up (+10%)",
                        Subtitle = "Increase master system audio volume",
                        Category = "Audio",
                        Section = "System",
                        Icon = iconVol,
                        Action = () => { for (int i = 0; i < 5; i++) SendMediaKey(VK_VOLUME_UP); ToastNotification.Show("✓ Volume +10%"); }
                    },
                    new()
                    {
                        Id = "sys:vol_down",
                        Name = "Volume Down (-10%)",
                        Subtitle = "Decrease master system audio volume",
                        Category = "Audio",
                        Section = "System",
                        Icon = iconVol,
                        Action = () => { for (int i = 0; i < 5; i++) SendMediaKey(VK_VOLUME_DOWN); ToastNotification.Show("✓ Volume -10%"); }
                    },
                    new()
                    {
                        Id = "sys:empty_trash",
                        Name = "Empty Recycle Bin",
                        Subtitle = "Permanently delete all files currently in the Recycle Bin",
                        Category = "System",
                        Section = "System",
                        Icon = iconTrash,
                        Action = () =>
                        {
                            try { SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI); } catch { }
                            ToastNotification.Show("✓ Emptied Recycle Bin");
                        }
                    },
                    new()
                    {
                        Id = "sys:lock",
                        Name = "Lock Screen",
                        Subtitle = "Lock Windows workstation immediately",
                        Category = "System",
                        Section = "System",
                        ShortcutBadge = "Win+L",
                        Icon = iconLock,
                        Action = () => LockWorkStation()
                    },
                    new()
                    {
                        Id = "sys:sleep",
                        Name = "Sleep Computer",
                        Subtitle = "Put PC into low-power sleep state",
                        Category = "System",
                        Section = "System",
                        Icon = iconMoon,
                        Action = () => SetSuspendState(false, true, true)
                    },
                    new()
                    {
                        Id = "sys:restart_explorer",
                        Name = "Restart Windows Explorer",
                        Subtitle = "Restart explorer.exe to refresh taskbar and shell",
                        Category = "System",
                        Section = "System",
                        Icon = iconProc,
                        Action = () => RunHiddenCmd("taskkill /f /im explorer.exe & start explorer.exe")
                    },
                    new()
                    {
                        Id = "sys:env_vars",
                        Name = "Edit Environment Variables",
                        Subtitle = "Open Windows System Environment Variables dialog",
                        Category = "System",
                        Section = "System",
                        Icon = iconGear,
                        Action = () => RunHiddenCmd("rundll32.exe sysdm.cpl,EditEnvironmentVariables")
                    },
                    new()
                    {
                        Id = "sys:startup_apps",
                        Name = "Startup Apps Settings",
                        Subtitle = "Manage applications that launch when Windows starts",
                        Category = "System",
                        Section = "System",
                        Icon = iconGear,
                        Action = () => AppScanner.LaunchFile("ms-settings:startupapps")
                    },
                    new()
                    {
                        Id = "sys:restart",
                        Name = "Restart Computer",
                        Subtitle = "Reboot Windows (5-second delay)",
                        Category = "System",
                        Section = "System",
                        Icon = iconPower,
                        Action = () => RunHiddenCmd("shutdown /r /t 5")
                    },
                    new()
                    {
                        Id = "sys:shutdown",
                        Name = "Shut Down Computer",
                        Subtitle = "Power off Windows (5-second delay)",
                        Category = "System",
                        Section = "System",
                        Icon = iconPower,
                        Action = () => RunHiddenCmd("shutdown /s /t 5")
                    }
                });
            }

            // 7. Window Management Commands
            if (cfg.EnableWindowManagement)
            {
                commands.AddRange(WindowManagerService.GetWindowCommands());
            }

            return commands;
        }

        private record struct ProcessEntry(int Id, string Name, string Title, long MemMb);
        private static List<ProcessEntry>? _processSnapshot;
        private static DateTime _lastProcessSnapshotTime = DateTime.MinValue;
        private static readonly object _processSnapshotLock = new();

        public static void InvalidateProcessSnapshot()
        {
            lock (_processSnapshotLock)
            {
                _processSnapshot = null;
            }
        }

        public static List<LauncherItem> GetProcessKillerItems(string filter, Action onRefresh)
        {
            var iconProc = IconGenerator.GetSquircleIcon(SquircleTheme.ProcessRose, IconGenerator.PathCpu);
            var list = new List<LauncherItem>();

            try
            {
                List<ProcessEntry> snapshot;
                lock (_processSnapshotLock)
                {
                    if (_processSnapshot == null || (DateTime.UtcNow - _lastProcessSnapshotTime).TotalSeconds > 2.0)
                    {
                        var procs = Process.GetProcesses();
                        var entries = new List<ProcessEntry>(procs.Length);
                        for (int i = 0; i < procs.Length; i++)
                        {
                            var p = procs[i];
                            try
                            {
                                string name = p.ProcessName;
                                if (!string.IsNullOrWhiteSpace(name))
                                {
                                    long memMb = 0;
                                    string title = "";
                                    try { memMb = p.WorkingSet64 / (1024 * 1024); } catch { }
                                    try { title = p.MainWindowTitle; } catch { }
                                    entries.Add(new ProcessEntry(p.Id, name, title, memMb));
                                }
                            }
                            catch { }
                            finally
                            {
                                p.Dispose();
                            }
                        }
                        entries.Sort((a, b) => b.MemMb.CompareTo(a.MemMb));
                        _processSnapshot = entries;
                        _lastProcessSnapshotTime = DateTime.UtcNow;
                    }
                    snapshot = _processSnapshot;
                }

                IEnumerable<ProcessEntry> filtered = snapshot;
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    filtered = snapshot.Where(x =>
                        x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        x.Title.Contains(filter, StringComparison.OrdinalIgnoreCase));
                }

                foreach (var item in filtered.Take(40))
                {
                    string sub = string.IsNullOrWhiteSpace(item.Title)
                        ? $"PID: {item.Id}"
                        : $"{item.Title} (PID: {item.Id})";

                    int pid = item.Id;
                    list.Add(new LauncherItem
                    {
                        Id = $"proc:{pid}",
                        Name = item.Name,
                        Subtitle = sub,
                        Category = $"{item.MemMb:N0} MB",
                        ShortcutBadge = "Kill ↵",
                        Icon = iconProc,
                        KeepOpenOnExecute = true,
                        Action = () =>
                        {
                            try
                            {
                                using var target = Process.GetProcessById(pid);
                                string pName = target.ProcessName;
                                target.Kill();
                                ToastNotification.Show($"✓ Terminated {pName} (PID {pid})");
                                InvalidateProcessSnapshot();
                                onRefresh();
                            }
                            catch (Exception ex)
                            {
                                ToastNotification.Show($"Could not kill PID {pid}: {ex.Message}");
                            }
                        }
                    });
                }
            }
            catch { }

            return list;
        }

        public static List<LauncherItem> GetWebFallbackItems(string query, Action<bool, string> openGeminiChat)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<LauncherItem>();

            var cfg = BeamSettings.Current;
            var list = new List<LauncherItem>();

            var iconWeb = IconGenerator.GetSquircleIcon(SquircleTheme.WebCyan, IconGenerator.PathGlobe);
            var iconAi = IconGenerator.GetSquircleIcon(SquircleTheme.BrandCoral, IconGenerator.PathSparkles);
            var encoded = Uri.EscapeDataString(query);

            if (cfg.EnableAiFeatures)
            {
                list.Add(new LauncherItem
                {
                    Id = "web:gemini",
                    Name = $"Ask Gemini AI \"{query}\"",
                    Subtitle = "Open Embedded Gemini Chat inside Kestrel",
                    Category = "Gemini AI",
                    ShortcutBadge = "Tab",
                    Icon = iconAi,
                    KeepOpenOnExecute = true,
                    Action = () => openGeminiChat(cfg.UseEmbeddedGeminiWeb, query)
                });
            }

            if (cfg.EnableWebFallbacks)
            {
                list.AddRange(new LauncherItem[]
                {
                    new()
                    {
                        Id = "web:google",
                        Name = $"Search Google for \"{query}\"",
                        Subtitle = "Open in default web browser",
                        Category = "Web Search",
                        Icon = iconWeb,
                        Action = () => AppScanner.LaunchFile($"https://www.google.com/search?q={encoded}")
                    },
                    new()
                    {
                        Id = "web:youtube",
                        Name = $"Search YouTube for \"{query}\"",
                        Subtitle = "Open YouTube search results",
                        Category = "Web Search",
                        Icon = iconWeb,
                        Action = () => AppScanner.LaunchFile($"https://www.youtube.com/results?search_query={encoded}")
                    },
                    new()
                    {
                        Id = "web:github",
                        Name = $"Search GitHub for \"{query}\"",
                        Subtitle = "Search repositories and code on GitHub",
                        Category = "Web Search",
                        Icon = iconWeb,
                        Action = () => AppScanner.LaunchFile($"https://github.com/search?q={encoded}")
                    }
                });
            }

            return list;
        }

        private static void SendMediaKey(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static void ToggleWindowsDarkMode()
        {
            try
            {
                const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
                using var key = Registry.CurrentUser.OpenSubKey(keyPath, true);
                if (key != null)
                {
                    int current = Convert.ToInt32(key.GetValue("AppsUseLightTheme", 0));
                    int next = current == 0 ? 1 : 0;
                    key.SetValue("AppsUseLightTheme", next, RegistryValueKind.DWord);
                    key.SetValue("SystemUsesLightTheme", next, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                if (socket.LocalEndPoint is IPEndPoint endPoint)
                    return endPoint.Address.ToString();
            }
            catch { }
            return "127.0.0.1";
        }

        private static void RunHiddenCmd(string command)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
            }
            catch { }
        }
    }
}
