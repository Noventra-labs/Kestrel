using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Beam.Services;

namespace Beam
{
    public partial class App : System.Windows.Application
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const int HWND_BROADCAST = 0xffff;
        public static readonly int WmBeamSummon = RegisterWindowMessage("KESTREL_LAUNCHER_SUMMON_MSG");

        private static Mutex? _singleInstanceMutex;
        private MainWindow? _mainWindow;
        private static readonly string LogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kestrel",
            "kestrel.log");

        public static void Log(string msg)
        {
            try
            {
                var dir = Path.GetDirectoryName(LogFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            Log("Kestrel v2.3 process starting...");

            bool forceReplace = e.Args.Any(a => a.Equals("--replace", StringComparison.OrdinalIgnoreCase));
            bool startInTray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            const string mutexName = @"Local\Kestrel.Launcher.SingleInstance";
            _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew && !forceReplace)
            {
                // Instant notification to existing instance without scanning all processes or sleeping
                PostMessage((IntPtr)HWND_BROADCAST, (uint)WmBeamSummon, IntPtr.Zero, IntPtr.Zero);
                Log("Existing Kestrel instance summoned. Fast exiting duplicate launch.");
                Shutdown();
                return;
            }

            if (forceReplace && !createdNew)
            {
                var current = Process.GetCurrentProcess();
                var others = Process.GetProcessesByName(current.ProcessName)
                    .Where(p => p.Id != current.Id)
                    .ToList();
                foreach (var old in others)
                {
                    try { old.Kill(); old.WaitForExit(1500); } catch { }
                    finally { old.Dispose(); }
                }
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                Log($"Unhandled Exception: {ev.ExceptionObject}");
            };

            DispatcherUnhandledException += (s, ev) =>
            {
                Log($"Dispatcher Exception: {ev.Exception}");
                ev.Handled = true;
            };

            try
            {
                _mainWindow = new MainWindow();
                _mainWindow.Show();

                if (startInTray)
                {
                    _mainWindow.HideAndTrim(false);
                    ToastNotification.Show("Beam is active in System Tray • Press Windows Key or Alt+Space");
                }
                else
                {
                    _mainWindow.Activate();
                    _mainWindow.Summon();
                }

                Log("MainWindow v2.3 initialized and active.");
                EnsureShortcuts();
            }
            catch (Exception ex)
            {
                Log($"MainWindow creation failed: {ex}");
            }
        }

        private static void EnsureShortcuts()
        {
            Task.Run(() =>
            {
                try
                {
                    var exe = Process.GetCurrentProcess().MainModule?.FileName;
                    if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

                    string startPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    string startLnk = Path.Combine(startPrograms, "Kestrel.lnk");
                    string workDir = Path.GetDirectoryName(exe) ?? string.Empty;
                    string ico = Path.Combine(workDir, "beam.ico");
                    if (!File.Exists(ico)) ico = exe;

                    if (!File.Exists(startLnk))
                    {
                        Beam.Interop.ShortcutHelper.CreateShortcut(
                            startLnk,
                            exe,
                            workDir,
                            "Kestrel — Fast Command Bar for Windows",
                            ico);
                        Beam.Interop.ShortcutHelper.NotifyShell();
                    }
                }
                catch { }
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log("Kestrel exiting cleanly.");
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }
            catch { }
            base.OnExit(e);
        }
    }
}
