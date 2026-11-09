using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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

            Loaded += MainWindow_Loaded;
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
                BtnCancel.Visibility = Visibility.Visible;
                BtnPrimary.Visibility = Visibility.Visible;
                TxtPrimaryBtn.Text = "Install Kestrel";
            }
            else if (step == 2)
            {
                BtnCancel.Visibility = Visibility.Collapsed;
                BtnPrimary.Visibility = Visibility.Collapsed;
            }
            else if (step == 3)
            {
                BtnCancel.Visibility = Visibility.Collapsed;
                BtnPrimary.Visibility = Visibility.Visible;
                TxtPrimaryBtn.Text = "Launch & Finish";
            }
            else if (step == 4)
            {
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

            await Task.Run(async () =>
            {
                try
                {
                    installDir = InstallationFiles.ValidateDirectory(installDir);
                    UpdateProgress("Closing active instances...", 15);
                    foreach (var name in new[] { "Kestrel", "Beam" })
                    {
                        foreach (var p in Process.GetProcessesByName(name))
                        {
                            try { p.Kill(); p.WaitForExit(1500); } catch { }
                        }
                    }
                    await Task.Delay(220);

                    UpdateProgress("Preparing installation folder...", 35);
                    Directory.CreateDirectory(installDir);

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
                    var currentInstallerExe = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(currentInstallerExe) && File.Exists(currentInstallerExe))
                    {
                        File.Copy(currentInstallerExe, uninstallerPath, true);
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
                            unKey.SetValue("EstimatedSize", 2048, RegistryValueKind.DWord);
                            unKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
                            unKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                            unKey.SetValue("HelpLink", "https://github.com/Noventra-labs/Kestrel", RegistryValueKind.String);
                            unKey.SetValue("URLInfoAbout", "https://github.com/Noventra-labs/Kestrel", RegistryValueKind.String);
                            unKey.SetValue("URLUpdateInfo", "https://github.com/Noventra-labs/Kestrel/releases", RegistryValueKind.String);
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
                _installedExePath = string.Empty;
                Environment.ExitCode = 1;
                ShowStep(4);
                InstallingHeaderTitle.Text = "Installation failed";
                UpdateProgress(failure.Message, 0);
                return false;
            }
            Environment.ExitCode = 0;
            ShowStep(3);
            return true;
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
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private async Task LaunchInstalledKestrelAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(_installedExePath) && File.Exists(_installedExePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = _installedExePath,
                        Arguments = "--replace",
                        WorkingDirectory = Path.GetDirectoryName(_installedExePath) ?? "",
                        UseShellExecute = false
                    };
                    Process.Start(psi);
                    await Task.Delay(350);
                }
            }
            catch { }
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
