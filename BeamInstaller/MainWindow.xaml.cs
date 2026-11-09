using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BeamInstaller
{
    public partial class MainWindow : Window
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteFile(string name);

        private int _currentStep = 1;
        private readonly bool _isUninstallMode;
        private readonly bool _isSilentMode;
        private string _installedExePath = string.Empty;

        private const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Kestrel_NoventraLabs";
        private const string LegacyUninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Beam_NoventraLabs";
        private const string RunRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public MainWindow(bool isUninstall, bool isSilent)
        {
            InitializeComponent();
            _isUninstallMode = isUninstall;
            _isSilentMode = isSilent;

            string defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Kestrel");
            TxtInstallDir.Text = defaultDir;

            if (_isSilentMode || _isUninstallMode)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(async () =>
                {
                    if (_isUninstallMode)
                    {
                        await RunUninstallerFlowAsync();
                    }
                    else if (_isSilentMode)
                    {
                        ShowStep(2);
                        if (!await PerformInstallationAsync())
                        {
                            System.Windows.Application.Current.Shutdown(1);
                            return;
                        }
                        await LaunchInstalledKestrelAsync();
                        Close();
                        System.Windows.Application.Current.Shutdown(0);
                    }
                }));
            }
            else
            {
                Loaded += MainWindow_Loaded;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isUninstallMode)
            {
                await RunUninstallerFlowAsync();
                return;
            }

            if (_isSilentMode)
            {
                ShowStep(2);
                if (!await PerformInstallationAsync())
                {
                    System.Windows.Application.Current.Shutdown(1);
                    return;
                }
                await LaunchInstalledKestrelAsync();
                Close();
                System.Windows.Application.Current.Shutdown(0);
            }
        }

        private void BrowseFolder_Click(object sender, MouseButtonEventArgs e)
        {
            using var dlg = new Forms.FolderBrowserDialog
            {
                Description = "Select Installation Folder for Kestrel",
                SelectedPath = TxtInstallDir.Text,
                ShowNewFolderButton = true
            };

            if (dlg.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                TxtInstallDir.Text = dlg.SelectedPath;
            }
        }

        private async void BtnPrimary_Click(object sender, MouseButtonEventArgs e)
        {
            if (_currentStep == 4)
            {
                ShowStep(1);
                return;
            }
            if (_currentStep == 1)
            {
                ShowStep(2);
                await PerformInstallationAsync();
            }
            else if (_currentStep == 3)
            {
                BtnPrimary.IsEnabled = false;
                TxtPrimaryBtn.Text = "Starting...";

                if (ChkLaunchNow.IsChecked == true)
                {
                    await LaunchInstalledKestrelAsync();
                }

                System.Windows.Application.Current.Shutdown();
            }
        }

        private async void BtnAutoFix_Click(object sender, MouseButtonEventArgs e)
        {
            if (_currentStep != 4) return;
            await PerformAutoDiagnoseAndFixAsync();
        }

        private void BtnCancel_Click(object sender, MouseButtonEventArgs e)
        {
            if (_currentStep == 2) return; // Prevent accidental close mid-extraction
            System.Windows.Application.Current.Shutdown();
        }

        private void ShowStep(int step)
        {
            _currentStep = step;

            PageSetup.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            PageInstalling.Visibility = step is 2 or 4 ? Visibility.Visible : Visibility.Collapsed;
            PageComplete.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

            if (step == 1)
            {
                BtnAutoFix.Visibility = Visibility.Collapsed;
                FailureDetailBox.Visibility = Visibility.Collapsed;
                BtnCancel.Visibility = Visibility.Visible;
                BtnPrimary.Visibility = Visibility.Visible;
                TxtPrimaryBtn.Text = "Install Kestrel";
            }
            else if (step == 2)
            {
                BtnAutoFix.Visibility = Visibility.Collapsed;
                FailureDetailBox.Visibility = Visibility.Collapsed;
                BtnCancel.Visibility = Visibility.Collapsed;
                BtnPrimary.Visibility = Visibility.Collapsed;
            }
            else if (step == 3)
            {
                BtnAutoFix.Visibility = Visibility.Collapsed;
                FailureDetailBox.Visibility = Visibility.Collapsed;
                BtnCancel.Visibility = Visibility.Collapsed;
                BtnPrimary.Visibility = Visibility.Visible;
                TxtPrimaryBtn.Text = "Launch & Finish";
            }
            else if (step == 4)
            {
                BtnAutoFix.Visibility = Visibility.Visible;
                FailureDetailBox.Visibility = Visibility.Visible;
                BtnCancel.Visibility = Visibility.Visible;
                BtnPrimary.Visibility = Visibility.Visible;
                TxtPrimaryBtn.Text = "Back to Setup";
            }
        }

        private void UpdateProgress(string stepLabel, int percentage)
        {
            Dispatcher.Invoke(() =>
            {
                InstallingStepLabel.Text = stepLabel;
                InstallPercentText.Text = $"{percentage}%";

                double trackWidth = 380.0;
                InstallProgressFill.Width = (percentage / 100.0) * trackWidth;
            });
        }

        private async Task<bool> PerformInstallationAsync()
        {
            string installDir = TxtInstallDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(installDir))
            {
                installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "Kestrel");
            }

            bool createShortcuts = OptCreateShortcuts.IsChecked == true;
            bool launchOnBoot = OptLaunchOnBoot.IsChecked == true;
            bool configureHook = OptConfigureWinHook.IsChecked == true;
            Exception? failure = null;
            InstallingHeaderTitle.Text = "Installing Kestrel...";
            Log($"PerformInstallationAsync started for dir: {installDir}");

            await Task.Run(async () =>
            {
                try
                {
                    installDir = InstallationFiles.ValidateDirectory(installDir);
                    UpdateProgress("Closing active instances...", 15);
                    CloseConflictingProcesses();
                    await Task.Delay(250);

                    UpdateProgress("Preparing installation folder...", 35);
                    EnsureDirectoryReady(installDir);

                    string targetExe = Path.Combine(installDir, "Kestrel.exe");
                    string targetIco = Path.Combine(installDir, "beam.ico");
                    _installedExePath = targetExe;

                    UpdateProgress("Extracting application files...", 60);
                    ExtractPayload(targetExe, targetIco);

                    // Unblock Zone.Identifier alternate data stream so Windows never prompts or blocks startup
                    try { DeleteFile(targetExe + ":Zone.Identifier"); } catch { }
                    try { DeleteFile(targetIco + ":Zone.Identifier"); } catch { }

                    await Task.Delay(240);

                    string uninstallerPath = Path.Combine(installDir, "Uninstall-Kestrel.exe");
                    var currentInstallerExe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(currentInstallerExe) && File.Exists(currentInstallerExe))
                    {
                        bool copiedUninstaller = false;
                        for (int attempt = 0; attempt < 5; attempt++)
                        {
                            try
                            {
                                if (File.Exists(uninstallerPath))
                                {
                                    try { File.SetAttributes(uninstallerPath, FileAttributes.Normal); } catch { }
                                }
                                File.Copy(currentInstallerExe, uninstallerPath, true);
                                copiedUninstaller = true;
                                break;
                            }
                            catch when (attempt < 4)
                            {
                                try
                                {
                                    string old = uninstallerPath + ".old." + Guid.NewGuid().ToString("N");
                                    File.Move(uninstallerPath, old, true);
                                    try { File.Delete(old); } catch { }
                                }
                                catch { }
                                Thread.Sleep(200);
                            }
                        }

                        if (!copiedUninstaller)
                        {
                            File.Copy(currentInstallerExe, uninstallerPath, true);
                        }
                        DeleteFile(uninstallerPath + ":Zone.Identifier");
                    }
                    else throw new IOException("Cannot locate the setup executable to install the uninstaller.");

                    if (configureHook)
                    {
                        UpdateProgress("Configuring global shortcuts...", 75);
                        try
                        {
                            using var deskKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
                            deskKey?.SetValue("LowLevelHooksTimeout", "5000", RegistryValueKind.String);
                        }
                        catch { }
                    }

                    if (createShortcuts)
                    {
                        UpdateProgress("Creating Desktop & Start Menu shortcuts...", 85);

                        // Desktop shortcut
                        string deskDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                        if (string.IsNullOrEmpty(deskDir)) deskDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                        string deskPath = Path.Combine(deskDir, "Kestrel.lnk");
                        ShortcutHelper.CreateShortcut(
                            deskPath,
                            targetExe,
                            installDir,
                            "Kestrel — Fast Command Bar for Windows",
                            targetIco);

                        // Start Menu Programs root shortcut (Appears under 'K' in Windows 11 Start Menu)
                        string startPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                        string rootLnk = Path.Combine(startPrograms, "Kestrel.lnk");
                        ShortcutHelper.CreateShortcut(
                            rootLnk,
                            targetExe,
                            installDir,
                            "Kestrel — Fast Command Bar for Windows",
                            targetIco);

                        // Publisher subfolder shortcut (Appears under 'N' for Noventra-Labs)
                        string noventraFolder = Path.Combine(startPrograms, "Noventra-Labs");
                        ShortcutHelper.CreateShortcut(
                            Path.Combine(noventraFolder, "Kestrel.lnk"),
                            targetExe,
                            installDir,
                            "Kestrel — Fast Command Bar for Windows",
                            targetIco);

                        // Flush Windows Shell cache immediately
                        ShortcutHelper.NotifyShell();
                    }

                    if (launchOnBoot)
                    {
                        UpdateProgress("Registering system tray startup...", 93);
                        using var runKey = Registry.CurrentUser.OpenSubKey(RunRegKey, true);
                        runKey?.SetValue("KestrelLauncher", $"\"{targetExe}\" --tray", RegistryValueKind.String);
                        runKey?.DeleteValue("BeamLauncher", false);
                    }

                    // Always register clean uninstaller in Windows Installed Apps
                    try
                    {
                        using var unKey = Registry.CurrentUser.CreateSubKey(UninstallRegKey);
                        if (unKey != null)
                        {
                            unKey.SetValue("DisplayName", "Kestrel", RegistryValueKind.String);
                            unKey.SetValue("Publisher", "Noventra-Labs", RegistryValueKind.String);
                            unKey.SetValue("DisplayVersion", "2.3.1", RegistryValueKind.String);
                            unKey.SetValue("DisplayIcon", targetIco, RegistryValueKind.String);
                            unKey.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                            unKey.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall", RegistryValueKind.String);
                            unKey.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" --uninstall --silent", RegistryValueKind.String);
                            unKey.SetValue("EstimatedSize", 55000, RegistryValueKind.DWord);
                            unKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
                            unKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                            unKey.SetValue("HelpLink", "https://github.com/Noventra-labs/Kestrel", RegistryValueKind.String);
                            unKey.SetValue("URLInfoAbout", "https://github.com/Noventra-labs/Kestrel", RegistryValueKind.String);
                            unKey.SetValue("URLUpdateInfo", "https://github.com/Noventra-labs/Kestrel/releases", RegistryValueKind.String);
                            unKey.SetValue("Comments", "Fast, keyboard-first Raycast alternative for Windows 11", RegistryValueKind.String);
                        }
                    }
                    catch (Exception ex) { throw new IOException("Could not register Kestrel in Windows Installed Apps.", ex); }

                    UpdateProgress("Installation complete", 100);
                    await Task.Delay(320);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            if (failure != null)
            {
                Log($"PerformInstallationAsync failed: {failure}");
                _installedExePath = string.Empty;
                Environment.ExitCode = 1;
                ShowStep(4);
                InstallingHeaderTitle.Text = "Installation failed";
                UpdateProgress(failure.Message, 0);

                if (failure is UnauthorizedAccessException || failure.Message.Contains("denied", StringComparison.OrdinalIgnoreCase))
                {
                    FailureDiagnosticText.Text = "Diagnosis: Access is locked by a running instance of Kestrel or background process.\n" +
                                                  "Click 'Auto Diagnose & Fix' to terminate conflicting processes, repair access, and finish setup automatically.";
                }
                else if (failure is IOException || failure.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase))
                {
                    FailureDiagnosticText.Text = "Diagnosis: Files in the installation folder are held open by Windows.\n" +
                                                  "Click 'Auto Diagnose & Fix' to release file handles and resume installation.";
                }
                else
                {
                    FailureDiagnosticText.Text = $"Diagnosis: {failure.Message}\n" +
                                                  "Click 'Auto Diagnose & Fix' to scan for conflicts and resolve automatically.";
                }
                return false;
            }
            Log("PerformInstallationAsync completed successfully.");
            Environment.ExitCode = 0;
            ShowStep(3);
            return true;
        }

        private static void CloseConflictingProcesses()
        {
            string[] names = { "Kestrel", "Beam", "Uninstall-Kestrel" };
            int currentPid = Environment.ProcessId;

            foreach (var name in names)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    if (p.Id == currentPid) continue;
                    try { p.Kill(); p.WaitForExit(1000); } catch { }
                }
            }

            foreach (var name in names)
            {
                try
                {
                    var psi = new ProcessStartInfo("taskkill.exe", $"/F /T /IM {name}.exe")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(800);
                }
                catch { }
            }
        }

        private static void EnsureDirectoryReady(string installDir)
        {
            Exception? lastEx = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (!Directory.Exists(installDir))
                    {
                        Directory.CreateDirectory(installDir);
                    }
                    else
                    {
                        var di = new DirectoryInfo(installDir);
                        di.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.Hidden);
                    }

                    string probe = Path.Combine(installDir, ".probe." + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    try
                    {
                        string user = Environment.UserName;
                        var psi = new ProcessStartInfo("icacls.exe", $"\"{installDir}\" /grant \"{user}:(OI)(CI)F\" /T /Q /C")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var proc = Process.Start(psi);
                        proc?.WaitForExit(1000);
                    }
                    catch { }
                    Thread.Sleep(250);
                }
            }

            if (lastEx != null)
            {
                throw lastEx;
            }
        }

        private async Task PerformAutoDiagnoseAndFixAsync()
        {
            ShowStep(2);
            InstallingHeaderTitle.Text = "Diagnosing & Auto-Fixing...";
            UpdateProgress("Scanning system for locks and conflicting processes...", 15);

            string installDir = TxtInstallDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(installDir))
            {
                installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "Kestrel");
            }

            await Task.Run(async () =>
            {
                // Stage 1: Terminate all running Kestrel / Beam / uninstaller processes
                UpdateProgress("Terminating locking processes and handlers...", 30);
                CloseConflictingProcesses();
                await Task.Delay(350);

                // Stage 2: Clear file attributes, remove read-only, remove old temp/backup files
                UpdateProgress("Clearing file locks and stale temporary files...", 55);
                try
                {
                    if (Directory.Exists(installDir))
                    {
                        var di = new DirectoryInfo(installDir);
                        di.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.Hidden);

                        foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
                        {
                            try { f.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.Hidden); } catch { }
                        }

                        foreach (var f in Directory.GetFiles(installDir, "*.tmp", SearchOption.TopDirectoryOnly))
                        {
                            try { File.Delete(f); } catch { }
                        }
                        foreach (var f in Directory.GetFiles(installDir, "*.old*", SearchOption.TopDirectoryOnly))
                        {
                            try { File.Delete(f); } catch { }
                        }
                    }
                }
                catch { }

                // Stage 3: Repair directory permissions
                UpdateProgress("Granting write access permissions on destination...", 75);
                try
                {
                    EnsureDirectoryReady(installDir);
                }
                catch { }

                await Task.Delay(300);
            });

            UpdateProgress("Resuming installation...", 85);
            await PerformInstallationAsync();
        }

        private static void ExtractPayload(string targetExe, string targetIco)
        {
            var asm = Assembly.GetExecutingAssembly();
            string[] names = asm.GetManifestResourceNames();

            string exeRes = Array.Find(names, n => n.EndsWith("Payload.Kestrel.exe", StringComparison.OrdinalIgnoreCase))
                         ?? Array.Find(names, n => n.EndsWith("Payload.Beam.exe", StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidOperationException("Embedded Kestrel payload executable not found.");

            string icoRes = Array.Find(names, n => n.EndsWith("Payload.beam.ico", StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidOperationException("Embedded icon resource not found.");

            ExtractAtomically(asm, exeRes, targetExe);
            ExtractAtomically(asm, icoRes, targetIco);
        }

        private static void ExtractAtomically(Assembly assembly, string resource, string destination)
        {
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = assembly.GetManifestResourceStream(resource)!)
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.CopyTo(file);
                    file.Flush(true);
                }

                bool moved = false;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (File.Exists(destination))
                        {
                            try { File.SetAttributes(destination, FileAttributes.Normal); } catch { }
                        }
                        File.Move(temporary, destination, overwrite: true);
                        moved = true;
                        break;
                    }
                    catch when (attempt < 4)
                    {
                        Thread.Sleep(200);
                    }
                }

                if (!moved && File.Exists(destination))
                {
                    // Fallback: Windows permits renaming an in-use executable, freeing up the primary path
                    string oldBackup = destination + ".old." + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Move(destination, oldBackup, overwrite: true);
                        File.Move(temporary, destination, overwrite: true);
                        moved = true;
                        try { File.Delete(oldBackup); } catch { }
                    }
                    catch { }
                }

                if (!moved)
                {
                    File.Move(temporary, destination, overwrite: true);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); } catch { }
                }
            }
        }

        private static void Log(string msg)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kestrel");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "setup.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        private async Task LaunchInstalledKestrelAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(_installedExePath) && File.Exists(_installedExePath))
                {
                    Log($"Launching installed Kestrel: {_installedExePath}");
                    var psi = new ProcessStartInfo
                    {
                        FileName = _installedExePath,
                        Arguments = "--replace",
                        WorkingDirectory = Path.GetDirectoryName(_installedExePath) ?? "",
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    await Task.Delay(350);
                }
            }
            catch (Exception ex)
            {
                Log($"LaunchInstalledKestrelAsync error: {ex}");
            }
        }

        private async Task RunUninstallerFlowAsync()
        {
            if (!_isSilentMode)
            {
                var confirm = System.Windows.MessageBox.Show(
                    "Uninstall Kestrel from your computer?",
                    "Uninstall Kestrel",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                {
                    Close();
                    return;
                }
            }

            string installDir;
            try
            {
                using var installed = Registry.CurrentUser.OpenSubKey(UninstallRegKey);
                using var legacy = Registry.CurrentUser.OpenSubKey(LegacyUninstallRegKey);
                string? registered = (installed?.GetValue("InstallLocation") ?? legacy?.GetValue("InstallLocation")) as string;
                installDir = InstallationFiles.ResolveUninstallDirectory(Environment.ProcessPath, registered);
                // Validate and schedule before removing registry metadata. Cleanup waits for this process.
                InstallationFiles.ScheduleCleanup(installDir);
            }
            catch (Exception ex)
            {
                if (!_isSilentMode)
                    System.Windows.MessageBox.Show(ex.Message, "Uninstall failed", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown(1);
                return;
            }

            ShowStep(2);
            InstallingHeaderTitle.Text = "Uninstalling Kestrel...";

            await Task.Run(async () =>
            {
                UpdateProgress("Stopping active processes...", 30);
                foreach (var name in new[] { "Kestrel", "Beam" })
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(); p.WaitForExit(1500); } catch { }
                    }
                }
                await Task.Delay(250);

                UpdateProgress("Removing shortcuts...", 65);
                try
                {
                    string deskDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    string altDeskDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    foreach (var fn in new[] { "Kestrel.lnk", "Beam.lnk" })
                    {
                        foreach (var d in new[] { deskDir, altDeskDir })
                        {
                            if (!string.IsNullOrEmpty(d))
                            {
                                string p = Path.Combine(d, fn);
                                if (File.Exists(p)) File.Delete(p);
                            }
                        }
                    }

                    string startPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    foreach (var fn in new[] { "Kestrel.lnk", "Beam.lnk" })
                    {
                        string p1 = Path.Combine(startPrograms, fn);
                        if (File.Exists(p1)) File.Delete(p1);
                        // Also remove shortcuts created under the former publisher spelling.
                        foreach (var publisherFolder in new[] { "Noventra-Labs", "Noventra Labs" })
                        {
                            string p2 = Path.Combine(startPrograms, publisherFolder, fn);
                            if (File.Exists(p2)) File.Delete(p2);
                        }
                    }

                    foreach (var publisherFolder in new[] { "Noventra-Labs", "Noventra Labs" })
                    {
                        string noventraFolder = Path.Combine(startPrograms, publisherFolder);
                        if (Directory.Exists(noventraFolder) && Directory.GetFileSystemEntries(noventraFolder).Length == 0)
                        {
                            try { Directory.Delete(noventraFolder); } catch { }
                        }
                    }

                    ShortcutHelper.NotifyShell();
                }
                catch { }

                UpdateProgress("Cleaning registry entries...", 90);
                try
                {
                    using var runKey = Registry.CurrentUser.OpenSubKey(RunRegKey, true);
                    runKey?.DeleteValue("KestrelLauncher", false);
                    runKey?.DeleteValue("BeamLauncher", false);
                    Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKey, false);
                    Registry.CurrentUser.DeleteSubKeyTree(LegacyUninstallRegKey, false);
                }
                catch { }

                UpdateProgress("Uninstalled", 100);
                await Task.Delay(300);
            });

            Close();
        }

        private void NoventraLink_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/Noventra-labs/Kestrel") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
