using System;
using System.Globalization;

namespace Kestrel.Services
{
    public static class ThemeAppearance
    {
        public const string DefaultAccentHex = "#FF6363";
        public const double DefaultOpacity = 0.40;
        public const double MinOpacity = 0.25;
        public const double MaxOpacity = 1.00;

        public static readonly (string Name, string Hex)[] AccentPresets =
        {
            ("Coral", "#FF6363"),
            ("Blue", "#3B82F6"),
            ("Emerald", "#10B981"),
            ("Violet", "#8B5CF6"),
            ("Amber", "#F59E0B"),
            ("Rose", "#F43F5E"),
            ("Cyan", "#06B6D4")
        };

        public static double ClampOpacity(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return DefaultOpacity;

            return Math.Clamp(value, MinOpacity, MaxOpacity);
        }

        public static bool TryNormalizeHexColor(string? input, out string normalized)
        {
            normalized = DefaultAccentHex;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            string raw = input.Trim();
            if (raw.StartsWith('#'))
                raw = raw[1..];

            if (raw.Length == 3)
            {
                foreach (char c in raw)
                {
                    if (!IsHexDigit(c)) return false;
                }
                normalized = $"#{char.ToUpperInvariant(raw[0])}{char.ToUpperInvariant(raw[0])}{char.ToUpperInvariant(raw[1])}{char.ToUpperInvariant(raw[1])}{char.ToUpperInvariant(raw[2])}{char.ToUpperInvariant(raw[2])}";
                return true;
            }

            if (raw.Length == 6)
            {
                foreach (char c in raw)
                {
                    if (!IsHexDigit(c)) return false;
                }
                normalized = "#" + raw.ToUpperInvariant();
                return true;
            }

            return false;
        }

        public static string NormalizeHexColor(string? input, string fallback = DefaultAccentHex)
        {
            return TryNormalizeHexColor(input, out string normalized) ? normalized : fallback;
        }

        public static (byte R, byte G, byte B) ParseRgb(string? hex)
        {
            string normalized = NormalizeHexColor(hex);
            byte r = byte.Parse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = byte.Parse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = byte.Parse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (r, g, b);
        }

        public static (byte TopAlpha, byte MidAlpha, byte BottomAlpha) ComputeShellAlphas(double opacity)
        {
            double clamped = ClampOpacity(opacity);
            double t = (clamped - MinOpacity) / (MaxOpacity - MinOpacity);
            byte top = (byte)Math.Round(24 + t * (242 - 24));
            byte mid = (byte)Math.Round(34 + t * (248 - 34));
            byte bot = (byte)Math.Round(44 + t * (252 - 44));
            return (top, mid, bot);
        }

        private static bool IsHexDigit(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
}
