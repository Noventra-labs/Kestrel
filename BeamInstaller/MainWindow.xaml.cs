using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BeamInstaller
{
    public partial class MainWindow : Window
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFile(string lpFileName);

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
                "Beam");
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
                await PerformInstallationAsync();
                await LaunchInstalledBeamAsync();
                Close();
            }
        }

        private void BrowseFolder_Click(object sender, MouseButtonEventArgs e)
        {
            using var dlg = new Forms.FolderBrowserDialog
            {
                Description = "Select Installation Folder for Beam",
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
                    await LaunchInstalledBeamAsync();
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
            PageInstalling.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
            PageComplete.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

            if (step == 1)
            {
                BtnCancel.Visibility = Visibility.Visible;
                BtnPrimary.Visibility = Visibility.Visible;
                TxtPrimaryBtn.Text = "Install Beam";
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
                TxtPrimaryBtn.Text = "Done";
            }
        }

        private void UpdateProgress(string status, int percent)
        {
            Dispatcher.Invoke(() =>
            {
                InstallingStepLabel.Text = status;
                InstallPercentText.Text = $"{percent}%";

                double targetWidth = 380.0 * (Math.Clamp(percent, 0, 100) / 100.0);
                var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                InstallProgressFill.BeginAnimation(WidthProperty, anim);
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
                    "Beam");
            }

            bool createShortcuts = OptCreateShortcuts.IsChecked == true;
            bool launchOnBoot = OptLaunchOnBoot.IsChecked == true;
            bool configureHook = OptConfigureWinHook.IsChecked == true;

            await Task.Run(async () =>
            {
                try
                {
                    UpdateProgress("Closing active instances...", 15);
                    foreach (var p in Process.GetProcessesByName("Beam"))
                    {
                        try { p.Kill(); p.WaitForExit(1500); } catch { }
                    }
                    await Task.Delay(220);

                    UpdateProgress("Preparing installation folder...", 35);
                    Directory.CreateDirectory(installDir);

                    string targetExe = Path.Combine(installDir, "Beam.exe");
                    string targetIco = Path.Combine(installDir, "beam.ico");
                    _installedExePath = targetExe;

                    UpdateProgress("Extracting application files...", 60);
                    ExtractEmbeddedResource("Payload.Beam.exe", targetExe);
                    ExtractEmbeddedResource("Payload.beam.ico", targetIco);

                    // Unblock Zone.Identifier alternate data stream so Windows never prompts or blocks startup
                    try { DeleteFile(targetExe + ":Zone.Identifier"); } catch { }
                    try { DeleteFile(targetIco + ":Zone.Identifier"); } catch { }

                    await Task.Delay(240);

                    string uninstallerPath = Path.Combine(installDir, "Uninstall-Beam.exe");
                    var currentInstallerExe = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(currentInstallerExe) && File.Exists(currentInstallerExe))
                    {
                        try
                        {
                            File.Copy(currentInstallerExe, uninstallerPath, true);
                            DeleteFile(uninstallerPath + ":Zone.Identifier");
                        }
                        catch { }
                    }

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
                        string deskPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Beam.lnk");
                        ShortcutHelper.CreateShortcut(
                            deskPath,
                            targetExe,
                            installDir,
                            "Beam — Fast Command Bar for Windows",
                            targetIco);

                        string startPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                        string noventraFolder = Path.Combine(startPrograms, "Noventra Labs");
                        Directory.CreateDirectory(noventraFolder);

                        ShortcutHelper.CreateShortcut(
                            Path.Combine(noventraFolder, "Beam.lnk"),
                            targetExe,
                            installDir,
                            "Beam — Fast Command Bar for Windows",
                            targetIco);

                        ShortcutHelper.CreateShortcut(
                            Path.Combine(startPrograms, "Beam.lnk"),
                            targetExe,
                            installDir,
                            "Beam — Fast Command Bar for Windows",
                            targetIco);
                    }

                    if (launchOnBoot)
                    {
                        UpdateProgress("Registering system tray startup...", 93);
                        using var runKey = Registry.CurrentUser.OpenSubKey(RunRegKey, true);
                        runKey?.SetValue("BeamLauncher", $"\"{targetExe}\" --tray", RegistryValueKind.String);
                    }

                    // Always register clean uninstaller in Windows Installed Apps
                    try
                    {
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
                    catch { }

                    UpdateProgress("Installation complete", 100);
                    await Task.Delay(320);
                }
                catch (Exception ex)
                {
                    UpdateProgress($"Notice: {ex.Message}", 100);
                    await Task.Delay(600);
                }
            });

            ShowStep(3);
        }

        private static void ExtractEmbeddedResource(string resourceName, string outputPath)
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded payload '{resourceName}' not found.");
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(fs);
        }

        private async Task LaunchInstalledBeamAsync()
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
            var confirm = System.Windows.MessageBox.Show(
                "Uninstall Beam from your computer?",
                "Uninstall Beam",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                Close();
                return;
            }

            ShowStep(2);
            InstallingHeaderTitle.Text = "Uninstalling Beam...";

            await Task.Run(async () =>
            {
                UpdateProgress("Stopping active Beam processes...", 30);
                foreach (var p in Process.GetProcessesByName("Beam"))
                {
                    try { p.Kill(); p.WaitForExit(1500); } catch { }
                }
                await Task.Delay(250);

                UpdateProgress("Removing shortcuts...", 65);
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

                UpdateProgress("Cleaning registry entries...", 90);
                try
                {
                    using var runKey = Registry.CurrentUser.OpenSubKey(RunRegKey, true);
                    runKey?.DeleteValue("BeamLauncher", false);
                    Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKey, false);
                }
                catch { }

                UpdateProgress("Uninstalled", 100);
                await Task.Delay(300);
            });

            Close();
        }
    }
}
