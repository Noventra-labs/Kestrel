using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BeamInstaller
{
    public partial class MainWindow : Window
    {
        private int _currentStep = 1;
        private readonly bool _isUninstallMode;
        private readonly bool _isSilentMode;
        private string _installedExePath = string.Empty;

        private const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Beam_NoventraLabs";
        private const string RunRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public MainWindow(bool isUninstall, bool isSilent)
        {
            InitializeComponent();
            _isUninstallMode = isUninstall;
            _isSilentMode = isSilent;

            string defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Noventra Labs",
                "Beam");
            TxtInstallDir.Text = defaultDir;

            Loaded += MainWindow_Loaded;
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
                ShowStep(3);
                await PerformInstallationAsync();
                LaunchInstalledBeam();
                Close();
            }
        }

        private void ChkAcceptLicense_Changed(object sender, RoutedEventArgs e)
        {
            if (BtnNext != null && _currentStep == 1)
            {
                BtnNext.IsEnabled = ChkAcceptLicense.IsChecked == true;
            }
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new Forms.FolderBrowserDialog
            {
                Description = "Select Installation Folder for Beam (Noventra Labs)",
                SelectedPath = TxtInstallDir.Text,
                ShowNewFolderButton = true
            };

            if (dlg.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                TxtInstallDir.Text = dlg.SelectedPath;
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == 2)
            {
                ShowStep(1);
            }
        }

        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == 1)
            {
                ShowStep(2);
            }
            else if (_currentStep == 2)
            {
                ShowStep(3);
                await PerformInstallationAsync();
            }
            else if (_currentStep == 4)
            {
                if (ChkLaunchNow.IsChecked == true)
                {
                    LaunchInstalledBeam();
                }
                System.Windows.Application.Current.Shutdown();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private void ShowStep(int step)
        {
            _currentStep = step;

            PageWelcome.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            PageOptions.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
            PageInstalling.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
            PageComplete.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;

            UpdateStepIndicators(step);

            if (step == 1)
            {
                BtnBack.Visibility = Visibility.Collapsed;
                BtnNext.Content = "Next >";
                BtnNext.IsEnabled = ChkAcceptLicense.IsChecked == true;
            }
            else if (step == 2)
            {
                BtnBack.Visibility = Visibility.Visible;
                BtnNext.Content = "Install";
                BtnNext.IsEnabled = true;
            }
            else if (step == 3)
            {
                BtnBack.Visibility = Visibility.Collapsed;
                BtnNext.Visibility = Visibility.Collapsed;
                BtnCancel.IsEnabled = false;
            }
            else if (step == 4)
            {
                BtnBack.Visibility = Visibility.Collapsed;
                BtnNext.Visibility = Visibility.Visible;
                BtnNext.Content = "Finish";
                BtnNext.IsEnabled = true;
                BtnCancel.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateStepIndicators(int activeStep)
        {
            var indicators = new[] { StepIndicator1, StepIndicator2, StepIndicator3, StepIndicator4 };
            var titles = new[]
            {
                "1. Welcome & License",
                "2. Install Options",
                "3. Installing Files",
                "4. Finish Setup"
            };

            for (int i = 0; i < indicators.Length; i++)
            {
                int idx = i + 1;
                string prefix = idx < activeStep ? "✓  " : (idx == activeStep ? "●  " : "○  ");
                indicators[i].Text = prefix + titles[i];
                indicators[i].Foreground = idx == activeStep
                    ? System.Windows.Media.Brushes.White
                    : (idx < activeStep
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129))
                        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 114, 128)));
                indicators[i].FontWeight = idx == activeStep ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        private void AppendLog(string line, double progress)
        {
            Dispatcher.Invoke(() =>
            {
                InstallingStepLabel.Text = line;
                InstallProgressBar.Value = progress;
                InstallLogText.Text += $"[{DateTime.Now:HH:mm:ss}] {line}\n";
                LogScrollViewer.ScrollToBottom();
            });
        }

        private async Task PerformInstallationAsync()
        {
            string installDir = TxtInstallDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(installDir))
            {
                installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "Noventra Labs",
                    "Beam");
            }

            bool createDesktop = OptDesktopShortcut.IsChecked == true;
            bool createStartMenu = OptStartMenuShortcut.IsChecked == true;
            bool launchOnBoot = OptLaunchOnBoot.IsChecked == true;
            bool configureHook = OptConfigureWinHook.IsChecked == true;
            bool registerUninstall = OptRegisterUninstall.IsChecked == true;

            await Task.Run(async () =>
            {
                try
                {
                    AppendLog("Stopping any running Beam processes...", 10);
                    foreach (var p in Process.GetProcessesByName("Beam"))
                    {
                        try { p.Kill(); p.WaitForExit(1500); } catch { }
                    }
                    await Task.Delay(250);

                    AppendLog($"Creating destination folder: {installDir}", 25);
                    Directory.CreateDirectory(installDir);

                    string targetExe = Path.Combine(installDir, "Beam.exe");
                    string targetIco = Path.Combine(installDir, "beam.ico");
                    _installedExePath = targetExe;

                    AppendLog("Extracting Beam.exe (v2.3.0 — Noventra Labs)...", 45);
                    ExtractEmbeddedResource("Payload.Beam.exe", targetExe);

                    AppendLog("Extracting application icon (beam.ico)...", 58);
                    ExtractEmbeddedResource("Payload.beam.ico", targetIco);

                    // Also update legacy %LOCALAPPDATA%\Programs\Beam if present so existing paths stay synced
                    string legacyDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs",
                        "Beam");
                    if (!string.Equals(installDir, legacyDir, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Directory.CreateDirectory(legacyDir);
                            File.Copy(targetExe, Path.Combine(legacyDir, "Beam.exe"), true);
                            File.Copy(targetIco, Path.Combine(legacyDir, "beam.ico"), true);
                        }
                        catch { }
                    }

                    // Copy uninstaller binary
                    string uninstallerPath = Path.Combine(installDir, "Uninstall-Beam.exe");
                    var currentInstallerExe = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(currentInstallerExe) && File.Exists(currentInstallerExe))
                    {
                        try { File.Copy(currentInstallerExe, uninstallerPath, true); } catch { }
                    }

                    if (configureHook)
                    {
                        AppendLog("Configuring Windows LowLevelHooksTimeout for 24/7 Start Key Hook...", 70);
                        try
                        {
                            using var deskKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
                            deskKey?.SetValue("LowLevelHooksTimeout", "5000", RegistryValueKind.String);
                        }
                        catch { }
                    }

                    if (createDesktop)
                    {
                        AppendLog("Creating Desktop shortcut (Beam.lnk)...", 78);
                        string deskPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Beam.lnk");
                        ShortcutHelper.CreateShortcut(
                            deskPath,
                            targetExe,
                            installDir,
                            "Beam — Raycast for Windows by Noventra Labs",
                            targetIco);
                    }

                    if (createStartMenu)
                    {
                        AppendLog("Creating Start Menu shortcut (Noventra Labs\\Beam.lnk)...", 85);
                        string startPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                        string noventraFolder = Path.Combine(startPrograms, "Noventra Labs");
                        Directory.CreateDirectory(noventraFolder);

                        ShortcutHelper.CreateShortcut(
                            Path.Combine(noventraFolder, "Beam.lnk"),
                            targetExe,
                            installDir,
                            "Beam — Raycast for Windows by Noventra Labs",
                            targetIco);

                        ShortcutHelper.CreateShortcut(
                            Path.Combine(startPrograms, "Beam.lnk"),
                            targetExe,
                            installDir,
                            "Beam — Raycast for Windows by Noventra Labs",
                            targetIco);
                    }

                    if (launchOnBoot)
                    {
                        AppendLog("Registering Beam System Tray Daemon in Windows Startup...", 92);
                        using var runKey = Registry.CurrentUser.OpenSubKey(RunRegKey, true);
                        runKey?.SetValue("BeamLauncher", $"\"{targetExe}\" --tray", RegistryValueKind.String);
                    }

                    if (registerUninstall)
                    {
                        AppendLog("Registering Beam (Publisher: Noventra Labs) in Windows Installed Apps...", 97);
                        using var unKey = Registry.CurrentUser.CreateSubKey(UninstallRegKey);
                        if (unKey != null)
                        {
                            unKey.SetValue("DisplayName", "Beam", RegistryValueKind.String);
                            unKey.SetValue("Publisher", "Noventra Labs", RegistryValueKind.String);
                            unKey.SetValue("DisplayVersion", "2.3.0", RegistryValueKind.String);
                            unKey.SetValue("DisplayIcon", targetIco, RegistryValueKind.String);
                            unKey.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                            unKey.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall", RegistryValueKind.String);
                            unKey.SetValue("EstimatedSize", 2048, RegistryValueKind.DWord);
                            unKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
                            unKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                        }
                    }

                    AppendLog("Installation completed successfully!", 100);
                    await Task.Delay(350);
                }
                catch (Exception ex)
                {
                    AppendLog($"Error during install: {ex.Message}", 100);
                }
            });

            CompleteSummaryText.Text =
                $"Beam v2.3.0 (Published by Noventra Labs) has been installed to:\n{installDir}";
            ShowStep(4);
        }

        private static void ExtractEmbeddedResource(string resourceName, string outputPath)
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded payload '{resourceName}' not found.");
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(fs);
        }

        private void LaunchInstalledBeam()
        {
            try
            {
                if (!string.IsNullOrEmpty(_installedExePath) && File.Exists(_installedExePath))
                {
                    Process.Start(new ProcessStartInfo(_installedExePath, "--replace")
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(_installedExePath) ?? ""
                    });
                }
            }
            catch { }
        }

        private async Task RunUninstallerFlowAsync()
        {
            var confirm = System.Windows.MessageBox.Show(
                "Are you sure you want to completely uninstall Beam (Noventra Labs) from your computer?",
                "Uninstall Beam — Noventra Labs",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                Close();
                return;
            }

            ShowStep(3);
            InstallingHeaderTitle.Text = "Uninstalling Beam...";

            await Task.Run(async () =>
            {
                AppendLog("Stopping running Beam processes...", 25);
                foreach (var p in Process.GetProcessesByName("Beam"))
                {
                    try { p.Kill(); p.WaitForExit(1500); } catch { }
                }
                await Task.Delay(300);

                AppendLog("Removing Desktop & Start Menu shortcuts...", 55);
                try
                {
                    string deskLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Beam.lnk");
                    if (File.Exists(deskLink)) File.Delete(deskLink);

                    string startPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    string smLink1 = Path.Combine(startPrograms, "Beam.lnk");
                    string smLink2 = Path.Combine(startPrograms, "Noventra Labs", "Beam.lnk");
                    if (File.Exists(smLink1)) File.Delete(smLink1);
                    if (File.Exists(smLink2)) File.Delete(smLink2);
                }
                catch { }

                AppendLog("Removing Startup and Installed Apps registry entries...", 85);
                try
                {
                    using var runKey = Registry.CurrentUser.OpenSubKey(RunRegKey, true);
                    runKey?.DeleteValue("BeamLauncher", false);
                    Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKey, false);
                }
                catch { }

                AppendLog("Uninstallation complete!", 100);
                await Task.Delay(300);
            });

            System.Windows.MessageBox.Show(
                "Beam has been uninstalled from your computer.",
                "Beam Uninstaller — Noventra Labs",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Close();
        }
    }
}
