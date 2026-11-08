using System;
using System.Linq;
using System.Windows;

namespace BeamInstaller
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool isUninstall = e.Args.Any(a =>
                a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));

            bool isSilent = e.Args.Any(a =>
                a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/S", StringComparison.OrdinalIgnoreCase));

            var win = new MainWindow(isUninstall, isSilent);
            win.Show();
        }
    }
}

