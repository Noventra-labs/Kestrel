using System;
using System.Windows.Media;

namespace Beam.Models
{
    public class ActionItem
    {
        public string Title { get; set; } = string.Empty;
        public string ShortcutHint { get; set; } = string.Empty;
        public string Category { get; set; } = "Action";
        public ImageSource? Icon { get; set; }
        public bool IsPrimary { get; set; } = false;
        public Action? Execute { get; set; }
    }
}

