using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Windows;

namespace BeamInstaller
{
    public partial class App : System.Windows.Application
    {
        private static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static bool RelaunchAsAdministrator(string[] args)
        {
            try
            {
                var processPath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(processPath) || !File.Exists(processPath)) return false;

                var psi = new ProcessStartInfo
                {
                    FileName = processPath,
                    Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
                    Verb = "runas",
                    UseShellExecute = true
                };
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!IsAdministrator())
            {
                if (RelaunchAsAdministrator(e.Args))
                {
                    Shutdown(0);
                    return;
                }
            }

            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kestrel");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "setup.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Unhandled] {ev.ExceptionObject}{Environment.NewLine}");
                }
                catch { }
            };

            DispatcherUnhandledException += (s, ev) =>
            {
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kestrel");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "setup.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [DispatcherUnhandled] {ev.Exception}{Environment.NewLine}");
                }
                catch { }
            };

            base.OnStartup(e);

            bool isUninstall = e.Args.Any(a =>
                a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase))
                || (Environment.ProcessPath?.Contains("uninstall", StringComparison.OrdinalIgnoreCase) == true);

            bool isSilent = e.Args.Any(a =>
                a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/S", StringComparison.OrdinalIgnoreCase));

            var win = new MainWindow(isUninstall, isSilent);
            win.Show();
        }
    }
}

