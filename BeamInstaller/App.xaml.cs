using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace BeamInstaller
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
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

