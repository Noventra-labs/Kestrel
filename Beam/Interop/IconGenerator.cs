using System;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;

namespace Beam.Interop
{
    public enum SquircleTheme
    {
        BrandCoral,     // Raycast / Beam / AI (#FF6363 -> #DC2D41)
        WindowBlue,     // Window Management (#3B82F6 -> #1D4ED8)
        CalcGreen,      // Calculator & Conversions (#10B981 -> #047857)
        ClipAmber,      // Clipboard & Snippets (#F59E0B -> #B45309)
        SystemPurple,   // System Commands (#8B5CF6 -> #5B21B6)
        ProcessRose,    // Process Killer & Task (#EC4899 -> #BE185D)
        WebCyan,        // Web Search & Links (#06B6D4 -> #0E7490)
        SettingsSlate,  // Settings & Tools (#64748B -> #334155)
        GoldStar,       // Suggestions & Featured (#FBBF24 -> #D97706)
        AdminGold,      // Elevated Administrator Shield (#EAB308 -> #CA8A04)
        TerminalSlate   // Shells & Command Line (#475569 -> #1E293B)
    }

    /// <summary>
    /// Clean Lucide-standard vector icon renderer for Beam.
    /// Every path matches official Lucide icons (24x24 viewBox, stroke-width 2 equivalent geometry).
    /// </summary>
    public static class IconGenerator
    {
        private static readonly ConcurrentDictionary<string, ImageSource> Cache = new();

        // =========================================================================
        // Lucide SVG Path Data (Exact geometric path representations)
        // =========================================================================

        // Lucide: terminal (M4 17l6-6-6-6 M12 19h8)
        public const string PathTerminal = "M4,5 L10,11 L4,17 M12,19 H20";

        // Lucide: shield-alert / shield (Admin elevation shield)
        public const string PathShield = "M12,2 L20,6 V12 C20,17 16,21 12,22 C8,21 4,17 4,12 V6 Z M12,8 V12 M12,16 H12.01";

        // Lucide: sparkles (AI / Gemini)
        public const string PathSparkles = "M12,3 L14,8 L19,10 L14,12 L12,17 L10,12 L5,10 L10,8 Z M19,17 L20,19 L22,20 L20,21 L19,23 L18,21 L16,20 L18,19 Z";

        // Lucide: calculator
        public const string PathCalculator = "M6,2 H18 A2,2 0 0,1 20,4 V20 A2,2 0 0,1 18,22 H6 A2,2 0 0,1 4,20 V4 A2,2 0 0,1 6,2 Z M8,6 H16 M16,14 H16.01 M12,14 H12.01 M8,14 H8.01 M16,18 H16.01 M12,18 H12.01 M8,18 H8.01";

        // Lucide: clipboard-list
        public const string PathClipboard = "M16,4 H18 A2,2 0 0,1 20,6 V20 A2,2 0 0,1 18,22 H6 A2,2 0 0,1 4,20 V6 A2,2 0 0,1 6,4 H8 M9,2 H15 A1,1 0 0,1 16,3 V5 A1,1 0 0,1 15,6 H9 A1,1 0 0,1 8,5 V3 A1,1 0 0,1 9,2 Z M9,12 H15 M9,16 H13";

        // Lucide: layout / window left half
        public const string PathWindowLeft = "M4,4 H20 A2,2 0 0,1 22,6 V18 A2,2 0 0,1 20,20 H4 A2,2 0 0,1 2,18 V6 A2,2 0 0,1 4,4 Z M12,4 V20";

        // Lucide: layout / window right half
        public const string PathWindowRight = "M4,4 H20 A2,2 0 0,1 22,6 V18 A2,2 0 0,1 20,20 H4 A2,2 0 0,1 2,18 V6 A2,2 0 0,1 4,4 Z M12,4 V20";

        // Lucide: maximize-2
        public const string PathWindowMax = "M15,3 H21 V9 M9,21 H3 V15 M21,3 L14,10 M3,21 L10,14";

        // Lucide: minimize-2
        public const string PathWindowCenter = "M4,14 H10 V20 M20,10 H14 V4 M10,14 L3,21 M21,3 L14,10";

        // Lucide: lock
        public const string PathLock = "M7,11 V7 A5,5 0 0,1 17,7 V11 M5,11 H19 A2,2 0 0,1 21,13 V20 A2,2 0 0,1 19,22 H5 A2,2 0 0,1 3,20 V13 A2,2 0 0,1 5,11 Z M12,15 V17";

        // Lucide: power
        public const string PathPower = "M12,2 V12 M18.36,5.64 A9,9 0 1,1 5.64,5.64";

        // Lucide: trash-2
        public const string PathTrash = "M3,6 H21 M19,6 V20 A2,2 0 0,1 17,22 H7 A2,2 0 0,1 5,20 V6 M8,6 V4 A2,2 0 0,1 10,2 H14 A2,2 0 0,1 16,4 V6 M10,11 V17 M14,11 V17";

        // Lucide: cpu (Process killer)
        public const string PathCpu = "M6,4 H18 A2,2 0 0,1 20,6 V18 A2,2 0 0,1 18,20 H6 A2,2 0 0,1 4,18 V6 A2,2 0 0,1 6,4 Z M9,9 H15 V15 H9 Z M9,1 V4 M15,1 V4 M9,20 V23 M15,20 V23 M20,9 H23 M20,15 H23 M1,9 H4 M1,15 H4";

        // Lucide: search
        public const string PathSearch = "M11,4 A7,7 0 1,1 4,11 A7,7 0 0,1 11,4 Z M16,16 L21,21";

        // Lucide: settings
        public const string PathGear = "M12,8 A4,4 0 1,0 16,12 A4,4 0 0,0 12,8 Z M19.4,15 A1.65,1.65 0 0,0 19.73,16.82 L20.15,17.24 A2,2 0 0,1 17.32,20.07 L16.9,19.65 A1.65,1.65 0 0,0 15.08,19.32 A1.65,1.65 0 0,0 14,20.87 V21.46 A2,2 0 0,1 10,21.46 V20.87 A1.65,1.65 0 0,0 8.92,19.32 A1.65,1.65 0 0,0 7.1,19.65 L6.68,20.07 A2,2 0 0,1 3.85,17.24 L4.27,16.82 A1.65,1.65 0 0,0 4.6,15 A1.65,1.65 0 0,0 3.05,13.92 H2.46 A2,2 0 0,1 2.46,9.92 H3.05 A1.65,1.65 0 0,0 4.6,8.84 A1.65,1.65 0 0,0 4.27,7.02 L3.85,6.6 A2,2 0 0,1 6.68,3.77 L7.1,4.19 A1.65,1.65 0 0,0 8.92,4.52 A1.65,1.65 0 0,0 10,2.97 V2.38 A2,2 0 0,1 14,2.38 V2.97 A1.65,1.65 0 0,0 15.08,4.52 A1.65,1.65 0 0,0 16.9,4.19 L17.32,3.77 A2,2 0 0,1 20.15,6.6 L19.73,7.02 A1.65,1.65 0 0,0 19.4,8.84 A1.65,1.65 0 0,0 20.95,9.92 H21.54 A2,2 0 0,1 21.54,13.92 H20.95 A1.65,1.65 0 0,0 19.4,15 Z";

        // Lucide: star
        public const string PathStar = "M12,2 L15.09,8.26 L22,9.27 L17,14.14 L18.18,21.02 L12,17.77 L5.82,21.02 L7,14.14 L2,9.27 L8.91,8.26 Z";

        // Lucide: volume-2
        public const string PathVolume = "M11,5 L6,9 H2 V15 H6 L11,19 V5 Z M15.54,8.46 A5,5 0 0,1 15.54,15.54 M19.07,4.93 A10,10 0 0,1 19.07,19.07";

        // Lucide: moon
        public const string PathMoon = "M21,12.79 A9,9 0 1,1 11.21,3 A7,7 0 0,0 21,12.79 Z";

        // Lucide: key-round
        public const string PathKey = "M2,18 L7.5,12.5 M9,11 L10.5,12.5 M11,9 L12.5,10.5 M15,4 A5,5 0 1,1 11.5,12.5 L2,22 H6 V18 H10 V16 L11.5,14.5 A5,5 0 0,1 15,4 Z M16.5,7.5 H16.51";

        // Lucide: globe
        public const string PathGlobe = "M12,2 A10,10 0 1,0 22,12 A10,10 0 0,0 12,2 Z M2,12 H22 M12,2 A15.3,15.3 0 0,1 16,12 A15.3,15.3 0 0,1 12,22 A15.3,15.3 0 0,1 8,12 A15.3,15.3 0 0,1 12,2 Z";

        // Lucide: play
        public const string PathPlay = "M5,3 L19,12 L5,21 Z";

        // Lucide: copy
        public const string PathCopy = "M8,4 H18 A2,2 0 0,1 20,6 V16 M4,8 H14 A2,2 0 0,1 16,10 V20 A2,2 0 0,1 14,22 H4 A2,2 0 0,1 2,20 V10 A2,2 0 0,1 4,8 Z";

        // Lucide: folder-open
        public const string PathFolderOpen = "M2,6 A2,2 0 0,1 4,4 H9 L11,6 H20 A2,2 0 0,1 22,8 V18 A2,2 0 0,1 20,20 H4 A2,2 0 0,1 2,18 V6 Z";

        // Lucide: refresh-cw
        public const string PathRefresh = "M21,2 V8 H15 M3,12 A9,9 0 0,1 18.36,5.64 L21,8 M3,22 V16 H9 M21,12 A9,9 0 0,1 5.64,18.36 L3,16";

        public static ImageSource GetSquircleIcon(SquircleTheme theme, string svgPath, bool asStroke = false)
        {
            string key = $"{theme}:{svgPath}:{asStroke}";
            if (Cache.TryGetValue(key, out var cached))
                return cached;

            var (c1, c2) = GetThemeColors(theme);
            var group = new DrawingGroup();

            // 1. Raycast Squircle Rounded Box (32x32 with subtle gradient and light inner border)
            var bgBrush = new LinearGradientBrush(c1, c2, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
            var rectGeo = new RectangleGeometry(new Rect(0, 0, 32, 32), 8.0, 8.0);
            var borderPen = new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 255, 255)), 1.0);
            group.Children.Add(new GeometryDrawing(bgBrush, borderPen, rectGeo));

            // 2. Lucide Vector Icon scaled & centered precisely inside 32x32
            try
            {
                var pathGeo = Geometry.Parse(svgPath);
                var bounds = pathGeo.Bounds;
                var transformGroup = new TransformGroup();

                double maxDim = Math.Max(bounds.Width, bounds.Height);
                if (maxDim > 0)
                {
                    double scale = 16.5 / Math.Max(maxDim, 18.0);
                    transformGroup.Children.Add(new TranslateTransform(-bounds.Left - bounds.Width / 2.0, -bounds.Top - bounds.Height / 2.0));
                    transformGroup.Children.Add(new ScaleTransform(scale, scale));
                    transformGroup.Children.Add(new TranslateTransform(16.0, 16.0));
                }

                var transformedGeo = pathGeo.Clone();
                transformedGeo.Transform = transformGroup;

                // Lucide uses clean strokes (thickness 2 normalized) or crisp fills
                System.Windows.Media.Brush iconBrush = new SolidColorBrush(Colors.White);
                System.Windows.Media.Pen? iconPen = null;

                if (asStroke || svgPath.StartsWith("M4,5") || svgPath.StartsWith("M12,2 L20") || svgPath.StartsWith("M15,3") || svgPath.StartsWith("M4,14") || svgPath.StartsWith("M12,2 V12"))
                {
                    iconPen = new System.Windows.Media.Pen(iconBrush, 2.0)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round,
                        LineJoin = PenLineJoin.Round
                    };
                    group.Children.Add(new GeometryDrawing(null, iconPen, transformedGeo));
                }
                else
                {
                    iconPen = new System.Windows.Media.Pen(iconBrush, 0.75)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round,
                        LineJoin = PenLineJoin.Round
                    };
                    group.Children.Add(new GeometryDrawing(iconBrush, iconPen, transformedGeo));
                }
            }
            catch
            {
                group.Children.Add(new GeometryDrawing(System.Windows.Media.Brushes.White, null, new EllipseGeometry(new System.Windows.Point(16, 16), 5, 5)));
            }

            var drawingImage = new DrawingImage(group);
            drawingImage.Freeze();
            Cache[key] = drawingImage;
            return drawingImage;
        }

        private static (System.Windows.Media.Color, System.Windows.Media.Color) GetThemeColors(SquircleTheme theme)
        {
            return theme switch
            {
                SquircleTheme.BrandCoral => (System.Windows.Media.Color.FromRgb(255, 99, 99), System.Windows.Media.Color.FromRgb(220, 45, 65)),
                SquircleTheme.WindowBlue => (System.Windows.Media.Color.FromRgb(59, 130, 246), System.Windows.Media.Color.FromRgb(29, 78, 216)),
                SquircleTheme.CalcGreen => (System.Windows.Media.Color.FromRgb(16, 185, 129), System.Windows.Media.Color.FromRgb(4, 120, 87)),
                SquircleTheme.ClipAmber => (System.Windows.Media.Color.FromRgb(245, 158, 11), System.Windows.Media.Color.FromRgb(180, 83, 9)),
                SquircleTheme.SystemPurple => (System.Windows.Media.Color.FromRgb(139, 92, 246), System.Windows.Media.Color.FromRgb(91, 33, 182)),
                SquircleTheme.ProcessRose => (System.Windows.Media.Color.FromRgb(236, 72, 153), System.Windows.Media.Color.FromRgb(190, 24, 93)),
                SquircleTheme.WebCyan => (System.Windows.Media.Color.FromRgb(6, 182, 212), System.Windows.Media.Color.FromRgb(14, 116, 144)),
                SquircleTheme.SettingsSlate => (System.Windows.Media.Color.FromRgb(100, 116, 139), System.Windows.Media.Color.FromRgb(51, 65, 85)),
                SquircleTheme.GoldStar => (System.Windows.Media.Color.FromRgb(251, 191, 36), System.Windows.Media.Color.FromRgb(217, 119, 6)),
                SquircleTheme.AdminGold => (System.Windows.Media.Color.FromRgb(234, 179, 8), System.Windows.Media.Color.FromRgb(180, 83, 9)),
                SquircleTheme.TerminalSlate => (System.Windows.Media.Color.FromRgb(51, 65, 85), System.Windows.Media.Color.FromRgb(30, 41, 59)),
                _ => (System.Windows.Media.Color.FromRgb(255, 99, 99), System.Windows.Media.Color.FromRgb(220, 45, 65))
            };
        }
    }
}
