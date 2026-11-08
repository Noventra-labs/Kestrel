using System;
using System.Windows.Media;

namespace Beam.Models
{
    public class LauncherItem
    {
        private string _name = string.Empty;
        private string _category = "Application";

        public string Id { get; set; } = string.Empty;

        public string Name
        {
            get => _name;
            set
            {
                _name = value ?? string.Empty;
                LowerName = _name.ToLowerInvariant();
                Acronym = ComputeAcronym(_name);
            }
        }

        public string LowerName { get; private set; } = string.Empty;
        public string Acronym { get; private set; } = string.Empty;

        public string Subtitle { get; set; } = string.Empty;

        public string Category
        {
            get => _category;
            set
            {
                _category = value ?? string.Empty;
                LowerCategory = _category.ToLowerInvariant();
            }
        }

        public string LowerCategory { get; private set; } = string.Empty;
        public string Section { get; set; } = "Results";
        public string ShortcutBadge { get; set; } = string.Empty;
        public ImageSource? Icon { get; set; }
        public Action? Action { get; set; }
        public string Path { get; set; } = string.Empty;
        public bool IsAdminCapable { get; set; } = false;
        public bool KeepOpenOnExecute { get; set; } = false;

        public static string ComputeAcronym(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            Span<char> buf = stackalloc char[16];
            int count = 0;
            bool takeNext = true;

            for (int i = 0; i < text.Length && count < buf.Length; i++)
            {
                char c = text[i];
                if (c == ' ' || c == '-' || c == '_' || c == '/')
                {
                    takeNext = true;
                }
                else if (takeNext || (i > 0 && char.IsUpper(c) && char.IsLower(text[i - 1])))
                {
                    if (char.IsLetterOrDigit(c))
                    {
                        buf[count++] = char.ToLowerInvariant(c);
                    }
                    takeNext = false;
                }
            }

            return new string(buf[..count]);
        }
    }
}
