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

            var current = Process.GetCurrentProcess();
            var others = Process.GetProcessesByName(current.ProcessName)
                .Where(p => p.Id != current.Id)
                .ToList();

            if (others.Count > 0)
            {
                if (!forceReplace)
                {
                    // Try summoning the existing instance first
                    PostMessage((IntPtr)HWND_BROADCAST, (uint)WmBeamSummon, IntPtr.Zero, IntPtr.Zero);
                    Thread.Sleep(180);

                    // Verify if the existing instance actually came to the foreground on the interactive desktop
                    IntPtr fg = GetForegroundWindow();
                    GetWindowThreadProcessId(fg, out uint fgPid);
                    bool summonedOk = others.Any(p => (uint)p.Id == fgPid);

                    if (summonedOk)
                    {
                        Log("Existing Kestrel instance summoned to foreground. Exiting duplicate launch.");
                        Shutdown();
                        return;
                    }

                    Log("Existing Kestrel instance did not respond on interactive desktop; replacing it.");
                }

                foreach (var old in others)
                {
                    try { old.Kill(); old.WaitForExit(1500); } catch { }
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
            }
            catch (Exception ex)
            {
                Log($"MainWindow creation failed: {ex}");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log("Beam exiting cleanly.");
            base.OnExit(e);
        }
    }
}
