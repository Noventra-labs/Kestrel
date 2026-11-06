using System;
using System.Windows.Media;

namespace Beam.Models
{
    public class LauncherItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Category { get; set; } = "Application";
        public string Section { get; set; } = "Results";
        public string ShortcutBadge { get; set; } = string.Empty;
        public ImageSource? Icon { get; set; }
        public Action? Action { get; set; }
        public string Path { get; set; } = string.Empty;
        public bool IsAdminCapable { get; set; } = false;
        public bool KeepOpenOnExecute { get; set; } = false;
    }
}
