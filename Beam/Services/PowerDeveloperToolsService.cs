using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Beam.Interop;

namespace Beam.Services
{
    public class InlineEvalResult
    {
        public bool IsValid { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Category { get; set; } = "Developer Tool";
        public string Badge { get; set; } = "Copy ↵";
        public string SvgPath { get; set; } = IconGenerator.PathHash;
        public SquircleTheme Theme { get; set; } = SquircleTheme.SystemPurple;
        public bool UseStrokeIcon { get; set; } = false;
        public string ActionType { get; set; } = "copy";
        public string ActionArg { get; set; } = string.Empty;
        public string ItemIdPrefix { get; set; } = "dev:";
    }

    public record PortBindingInfo(
        string Protocol,
        string LocalAddress,
        int LocalPort,
        string RemoteAddress,
        int RemotePort,
        string State,
        int Pid,
        string ProcessName,
        string ExecutablePath);

    public record TempFileCandidate(string FullPath, long SizeBytes, DateTime LastWriteUtc);

    public record TempCleanupPreview(
        List<TempFileCandidate> Files,
        long TotalBytes,
        int SkippedRecentOrLocked,
        string[] ScannedRoots);

    public record TempCleanupOutcome(int DeletedCount, int FailedCount, long FreedBytes);

    public record HungWindowInfo(int Pid, string ProcessName, string WindowTitle, IntPtr Hwnd);

    /// <summary>
    /// Developer PowerToys &amp; Safe SysAdmin Utilities for Kestrel.
    /// Provides zero-logging inline evaluators (JWT decode, colors, URL, NanoID/ULID/passwords, hashing, ports, timers),
    /// native TCP/UDP port inspection via iphlpapi.dll, clipboard hygiene transforms, and two-stage safe cleanup scanners.
    /// </summary>
    public static class PowerDeveloperToolsService
    {
        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const int UDP_TABLE_OWNER_PID = 1;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint NO_ERROR = 0;

        private const string NanoIdAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz-";
        private const string CrockfordBase32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        private static readonly HashSet<string> TrackingQueryParams = new(StringComparer.OrdinalIgnoreCase)
        {
            "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id",
            "fbclid", "gclid", "gbraid", "wbraid", "mc_cid", "mc_eid", "igshid", "si",
            "_hsenc", "_hsmi", "msclkid", "ref_src", "ref_url"
        };

        private static readonly object _timerLock = new();
        private static System.Threading.Timer? _countdownTimer;
        private static DateTime _timerEndUtc = DateTime.MinValue;
        private static string _timerLabel = string.Empty;

        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        private static extern uint DnsFlushResolverCache();

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref int pdwSize,
            [MarshalAs(UnmanagedType.Bool)] bool bOrder,
            int ulAf,
            int TableClass,
            uint Reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(
            IntPtr pUdpTable,
            ref int pdwSize,
            [MarshalAs(UnmanagedType.Bool)] bool bOrder,
            int ulAf,
            int TableClass,
            uint Reserved);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsHungAppWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
            public uint dwOwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_UDPROW_OWNER_PID
        {
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwOwningPid;
        }

        public static InlineEvalResult EvaluateInline(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new InlineEvalResult();

            var trimmed = query.Trim();

            // 1. Live Memory Telemetry & Quick Studio Trigger
            if (trimmed.Equals("ram", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("mem", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("memreduct", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("mem reduct", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("memory claim", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("reclaim memory", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("free ram", StringComparison.OrdinalIgnoreCase))
            {
                var stats = MemoryReductService.GetMemoryStats();
                if (stats.TotalPhysBytes > 0)
                {
                    return new InlineEvalResult
                    {
                        IsValid = true,
                        Title = $"RAM: {stats.LoadPercent}% Used ({stats.UsedPhysGb:F1} / {stats.TotalPhysGb:F1} GB) • Avail: {stats.AvailPhysGb:F1} GB",
                        Result = $"RAM Load: {stats.LoadPercent}% | Used: {stats.UsedPhysGb:F2} GB | Available: {stats.AvailPhysGb:F2} GB | System Cache: {stats.SystemCacheMb:N0} MB",
                        Subtitle = $"System Cache: {stats.SystemCacheMb:N0} MB • Commit: {stats.CommitTotalGb:F1}/{stats.CommitLimitGb:F1} GB • Enter: Open Memory Reduct Studio",
                        Category = "System Telemetry",
                        Badge = "Studio ↵",
                        SvgPath = IconGenerator.PathMemory,
                        Theme = SquircleTheme.ProcessRose,
                        ActionType = "memreduct_studio",
                        ItemIdPrefix = "mem:"
                    };
                }
            }

            // 2. Native TCP/UDP Port Inspector (Safe read-only lookup; never auto-kills)
            if (trimmed.Equals("ports", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("port ", StringComparison.OrdinalIgnoreCase))
            {
                int? portNum = null;
                if (trimmed.StartsWith("port ", StringComparison.OrdinalIgnoreCase))
                {
                    string arg = trimmed[5..].Trim();
                    if (!int.TryParse(arg, out int parsed) || parsed < 1 || parsed > 65535)
                        return new InlineEvalResult();
                    portNum = parsed;
                }

                if (portNum.HasValue)
                {
                    var bindings = GetActivePortBindings(portNum, resolveExecutablePath: false);
                    if (bindings.Count == 0)
                    {
                        return new InlineEvalResult
                        {
                            IsValid = true,
                            Title = $"Port {portNum.Value}: Free (No active TCP/UDP bindings)",
                            Result = $"Port {portNum.Value} has no active TCP/UDP listeners.",
                            Subtitle = "Enter: Open Port Inspector",
                            Category = "Port Inspector",
                            Badge = "Inspect ↵",
                            SvgPath = IconGenerator.PathNetwork,
                            Theme = SquircleTheme.WebCyan,
                            UseStrokeIcon = true,
                            ActionType = "port_inspect",
                            ActionArg = portNum.Value.ToString(CultureInfo.InvariantCulture),
                            ItemIdPrefix = "port:"
                        };
                    }

                    var top = bindings[0];
                    string extra = bindings.Count > 1 ? $" (+{bindings.Count - 1} more)" : string.Empty;
                    return new InlineEvalResult
                    {
                        IsValid = true,
                        Title = $"Port {portNum.Value}: {top.Protocol} {top.ProcessName} (PID {top.Pid}) [{top.State}]{extra}",
                        Result = $"{top.Protocol} {top.LocalAddress}:{top.LocalPort} -> {top.ProcessName} (PID {top.Pid}) [{top.State}]",
                        Subtitle = $"{top.LocalAddress}:{top.LocalPort} • Enter: Inspect in Port Inspector (No auto-kill)",
                        Category = "Port Inspector",
                        Badge = "Inspect ↵",
                        SvgPath = IconGenerator.PathNetwork,
                        Theme = SquircleTheme.WebCyan,
                        UseStrokeIcon = true,
                        ActionType = "port_inspect",
                        ActionArg = portNum.Value.ToString(CultureInfo.InvariantCulture),
                        ItemIdPrefix = "port:"
                    };
                }
                else
                {
                    var (listenCount, totalCount) = GetPortSocketCounts();
                    return new InlineEvalResult
                    {
                        IsValid = true,
                        Title = $"Active Network Ports: {listenCount} listening • {totalCount} total sockets",
                        Result = $"{listenCount} listening ports",
                        Subtitle = "Type 'port <number>' or press Enter to open Port Inspector (Ctrl+K / two-step confirmation for actions)",
                        Category = "Port Inspector",
                        Badge = "Inspect ↵",
                        SvgPath = IconGenerator.PathNetwork,
                        Theme = SquircleTheme.WebCyan,
                        UseStrokeIcon = true,
                        ActionType = "port_inspect",
                        ActionArg = string.Empty,
                        ItemIdPrefix = "port:"
                    };
                }
            }

            // 3. Unverified JWT Decoder (never implies signature verification; zero logging)
            if (trimmed.StartsWith("jwt ", StringComparison.OrdinalIgnoreCase))
            {
                string token = trimmed[4..].Trim();
                if (TryDecodeJwt(token, out string summary, out string claimsSub, out string decodedOutput))
                {
                    return new InlineEvalResult
                    {
                        IsValid = true,
                        Title = $"JWT: {summary}",
                        Result = decodedOutput,
                        Subtitle = $"⚠ Signature NOT Verified • {claimsSub}",
                        Category = "JWT (Unverified)",
                        Badge = "Copy JSON ↵",
                        SvgPath = IconGenerator.PathBraces,
                        Theme = SquircleTheme.AdminGold,
                        UseStrokeIcon = true,
                        ItemIdPrefix = "sec:"
                    };
                }

                if (token.Contains('.'))
                {
                    return new InlineEvalResult
                    {
                        IsValid = true,
                        Title = "Invalid JWT Format",
                        Result = string.Empty,
                        Subtitle = "⚠ Signature NOT Verified • Expected valid Base64Url header.payload[.sig] JSON segments",
                        Category = "JWT (Unverified)",
                        Badge = "Invalid",
                        SvgPath = IconGenerator.PathBraces,
                        Theme = SquircleTheme.ProcessRose,
                        UseStrokeIcon = true,
                        ItemIdPrefix = "sec:"
                    };
                }
            }

            // 4. Inline Color Converter (#RGB / #RRGGBB / rgb(...) / hsl(...))
            if (TryConvertColor(trimmed, out string colorTitle, out string colorCopyVal, out string colorSubtitle))
            {
                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = colorTitle,
                    Result = colorCopyVal,
                    Subtitle = colorSubtitle,
                    Category = "Color Converter",
                    Badge = "Copy ↵",
                    SvgPath = IconGenerator.PathPipette,
                    Theme = SquircleTheme.BrandCoral,
                    UseStrokeIcon = true,
                    ItemIdPrefix = "dev:"
                };
            }

            // 5. Quick Countdown Timer (timer 5m [label], timer 90s, timer status, timer off)
            if (trimmed.Equals("timer", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("timer status", StringComparison.OrdinalIgnoreCase))
            {
                var (active, remaining, label) = GetTimerStatus();
                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = active
                        ? $"Timer Active: {FormatDuration(remaining)} remaining" + (string.IsNullOrEmpty(label) ? "" : $" ({label})")
                        : "No Active Timer",
                    Result = active ? FormatDuration(remaining) : "Usage: timer 5m [Focus] or timer 90s",
                    Subtitle = active
                        ? "Type 'timer off' to cancel • Enter copies remaining time"
                        : "Examples: 'timer 5m', 'timer 25m Pomodoro', 'timer 90s'",
                    Category = "Timer",
                    Badge = active ? "Copy ↵" : "Info",
                    SvgPath = IconGenerator.PathClock,
                    Theme = SquircleTheme.WebCyan,
                    ActionType = "timer_status",
                    ItemIdPrefix = "timer:"
                };
            }

            if (trimmed.Equals("timer off", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("timer cancel", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("timer stop", StringComparison.OrdinalIgnoreCase))
            {
                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = "Cancel Active Countdown Timer",
                    Result = "Cancel Timer",
                    Subtitle = "Enter: Stop active countdown timer",
                    Category = "Timer",
                    Badge = "Cancel ↵",
                    SvgPath = IconGenerator.PathClock,
                    Theme = SquircleTheme.ProcessRose,
                    ActionType = "timer_cancel",
                    ItemIdPrefix = "timer:"
                };
            }

            if (trimmed.StartsWith("timer ", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseTimerCommand(trimmed[6..].Trim(), out var dur, out var timerLabel))
                {
                    string lbl = string.IsNullOrEmpty(timerLabel) ? "" : $" ({timerLabel})";
                    return new InlineEvalResult
                    {
                        IsValid = true,
                        Title = $"Start {FormatDuration(dur)} Countdown Timer{lbl}",
                        Result = $"{dur.TotalSeconds:0}|{timerLabel}",
                        Subtitle = "Non-blocking countdown with HUD notification on completion • Enter: Start",
                        Category = "Timer",
                        Badge = "Start ↵",
                        SvgPath = IconGenerator.PathClock,
                        Theme = SquircleTheme.CalcGreen,
                        ActionType = "timer_start",
                        ActionArg = $"{dur.TotalSeconds:0}|{timerLabel}",
                        ItemIdPrefix = "timer:"
                    };
                }
            }

            // 6. URL Encode / Decode
            if (trimmed.StartsWith("url ", StringComparison.OrdinalIgnoreCase))
            {
                string input = trimmed[4..];
                string encoded = Uri.EscapeDataString(input);
                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = encoded,
                    Result = encoded,
                    Subtitle = "URL-Encoded String • Enter: Copy",
                    Category = "URL Encoder",
                    SvgPath = IconGenerator.PathGlobe,
                    Theme = SquircleTheme.WebCyan,
                    ItemIdPrefix = "dev:"
                };
            }

            if (trimmed.StartsWith("urld ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("urldecode ", StringComparison.OrdinalIgnoreCase))
            {
                int prefixLen = trimmed.StartsWith("urldecode ", StringComparison.OrdinalIgnoreCase) ? 10 : 5;
                string input = trimmed[prefixLen..].Trim();
                try
                {
                    string decoded = Uri.UnescapeDataString(input);
                    return new InlineEvalResult
                    {
                        IsValid = true,
                        Title = decoded,
                        Result = decoded,
                        Subtitle = "URL-Decoded String • Enter: Copy",
                        Category = "URL Decoder",
                        SvgPath = IconGenerator.PathGlobe,
                        Theme = SquircleTheme.WebCyan,
                        ItemIdPrefix = "dev:"
                    };
                }
                catch { }
            }

            // 7. NanoID, ULID, Strong Password, and GUID Generators
            if (trimmed.Equals("nanoid", StringComparison.OrdinalIgnoreCase))
            {
                string id = GenerateNanoId(21);
                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = id,
                    Result = id,
                    Subtitle = "Cryptographic NanoID (21 chars, URL-safe) • Enter: Copy",
                    Category = "ID Generator",
                    SvgPath = IconGenerator.PathKey,
                    Theme = SquircleTheme.AdminGold,
                    ItemIdPrefix = "sec:"
                };
            }

            if (trimmed.Equals("ulid", StringComparison.OrdinalIgnoreCase))
            {
                string ulid = GenerateUlid();
                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = ulid,
                    Result = ulid,
                    Subtitle = "Lexicographically Sortable ULID (Crockford Base32) • Enter: Copy",
                    Category = "ID Generator",
                    SvgPath = IconGenerator.PathKey,
                    Theme = SquircleTheme.AdminGold,
                    ItemIdPrefix = "sec:"
                };
            }

            if (trimmed.Equals("pass", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("password", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("pass ", StringComparison.OrdinalIgnoreCase))
            {
                int len = 20;
                if (trimmed.StartsWith("pass ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(trimmed[5..].Trim(), out len))
                        return new InlineEvalResult();
                    len = Math.Clamp(len, 8, 128);
                }

                string pw = GenerateStrongPassword(len);
                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = pw,
                    Result = pw,
                    Subtitle = $"Cryptographic Password ({len} chars, not logged) • Enter: Copy",
                    Category = "Password Generator",
                    SvgPath = IconGenerator.PathKey,
                    Theme = SquircleTheme.AdminGold,
                    ItemIdPrefix = "sec:"
                };
            }

            if (trimmed.Equals("guid", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("uuid", StringComparison.OrdinalIgnoreCase))
            {
                var newGuid = Guid.NewGuid().ToString();
                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = newGuid,
                    Result = newGuid,
                    Subtitle = "Fresh UUID v4 • Click or Enter to Copy to Clipboard",
                    Category = "GUID Generator",
                    SvgPath = IconGenerator.PathKey,
                    Theme = SquircleTheme.AdminGold,
                    ItemIdPrefix = "dev:"
                };
            }

            // 8. Epoch & Unix Timestamp Converter
            if (trimmed.Equals("epoch", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("unix", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("timestamp", StringComparison.OrdinalIgnoreCase))
            {
                long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string localStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string utcStr = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = nowUnix.ToString(),
                    Result = nowUnix.ToString(),
                    Subtitle = $"Current Epoch Timestamp • Local: {localStr} • {utcStr}",
                    Category = "Timestamp",
                    SvgPath = IconGenerator.PathClock,
                    Theme = SquircleTheme.WebCyan,
                    ItemIdPrefix = "dev:"
                };
            }

            if (trimmed.StartsWith("unix ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("epoch ", StringComparison.OrdinalIgnoreCase))
            {
                string raw = trimmed[5..].Trim();
                if (long.TryParse(raw, out long val))
                {
                    try
                    {
                        DateTimeOffset dto = val > 9999999999
                            ? DateTimeOffset.FromUnixTimeMilliseconds(val)
                            : DateTimeOffset.FromUnixTimeSeconds(val);

                        string localStr = dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
                        string utcStr = dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

                        return new InlineEvalResult
                        {
                            IsValid = true,
                            Title = localStr,
                            Result = localStr,
                            Subtitle = $"Unix {val} → {utcStr} (Enter: Copy Local Time)",
                            Category = "Timestamp Converter",
                            SvgPath = IconGenerator.PathClock,
                            Theme = SquircleTheme.WebCyan,
                            ItemIdPrefix = "dev:"
                        };
                    }
                    catch { }
                }
            }

            // 9. Cryptographic Hashes (marked sec: to avoid logging input-derived IDs)
            if (trimmed.StartsWith("sha256 ", StringComparison.OrdinalIgnoreCase))
            {
                string input = trimmed[7..];
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
                string hex = Convert.ToHexString(hash).ToLowerInvariant();

                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = hex,
                    Result = hex,
                    Subtitle = "SHA256 Digest • Click or Enter to Copy Hash",
                    Category = "Hasher",
                    SvgPath = IconGenerator.PathHash,
                    Theme = SquircleTheme.SystemPurple,
                    ItemIdPrefix = "sec:"
                };
            }

            if (trimmed.StartsWith("md5 ", StringComparison.OrdinalIgnoreCase))
            {
                string input = trimmed[4..];
                byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
                string hex = Convert.ToHexString(hash).ToLowerInvariant();

                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = hex,
                    Result = hex,
                    Subtitle = "MD5 Digest • Click or Enter to Copy Hash",
                    Category = "Hasher",
                    SvgPath = IconGenerator.PathHash,
                    Theme = SquircleTheme.SystemPurple,
                    ItemIdPrefix = "sec:"
                };
            }

            if (trimmed.StartsWith("sha1 ", StringComparison.OrdinalIgnoreCase))
            {
                string input = trimmed[5..];
                byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(input));
                string hex = Convert.ToHexString(hash).ToLowerInvariant();

                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = hex,
                    Result = hex,
                    Subtitle = "SHA1 Digest • Click or Enter to Copy Hash",
                    Category = "Hasher",
                    SvgPath = IconGenerator.PathHash,
                    Theme = SquircleTheme.SystemPurple,
                    ItemIdPrefix = "sec:"
                };
            }

            // 10. Base64 Encode & Decode
            if (trimmed.StartsWith("base64 ", StringComparison.OrdinalIgnoreCase))
            {
                string input = trimmed[7..];
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(input));

                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = b64,
                    Result = b64,
                    Subtitle = "Base64 Encoded • Click or Enter to Copy",
                    Category = "Base64 Encoder",
                    SvgPath = IconGenerator.PathTerminal,
                    Theme = SquircleTheme.ClipAmber,
                    ItemIdPrefix = "sec:"
                };
            }

            if (trimmed.StartsWith("base64d ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("base64decode ", StringComparison.OrdinalIgnoreCase))
            {
                int prefixLen = trimmed.StartsWith("base64decode ", StringComparison.OrdinalIgnoreCase) ? 13 : 8;
                string input = trimmed[prefixLen..].Trim();
                try
                {
                    byte[] bytes = Convert.FromBase64String(input);
                    string decoded = Encoding.UTF8.GetString(bytes);

                    return new InlineEvalResult
                    {
                        IsValid = true,
                        Title = decoded,
                        Result = decoded,
                        Subtitle = "Base64 Decoded Text • Click or Enter to Copy",
                        Category = "Base64 Decoder",
                        SvgPath = IconGenerator.PathTerminal,
                        Theme = SquircleTheme.ClipAmber,
                        ItemIdPrefix = "sec:"
                    };
                }
                catch { }
            }

            return new InlineEvalResult();
        }

        public static bool TryDecodeJwt(string token, out string summary, out string claimsSubtitle, out string formattedOutput)
        {
            summary = string.Empty;
            claimsSubtitle = string.Empty;
            formattedOutput = string.Empty;

            if (string.IsNullOrWhiteSpace(token)) return false;

            string[] parts = token.Trim().Split('.');
            if (parts.Length < 2 || parts.Length > 3) return false;

            try
            {
                string headerJson = DecodeBase64UrlToUtf8(parts[0]);
                string payloadJson = DecodeBase64UrlToUtf8(parts[1]);

                using var headerDoc = JsonDocument.Parse(headerJson);
                using var payloadDoc = JsonDocument.Parse(payloadJson);

                var headerEl = headerDoc.RootElement;
                var payloadEl = payloadDoc.RootElement;

                string alg = headerEl.TryGetProperty("alg", out var algProp) ? algProp.ToString() : "unknown";
                string sub = payloadEl.TryGetProperty("sub", out var subProp) ? subProp.ToString() : string.Empty;
                string iss = payloadEl.TryGetProperty("iss", out var issProp) ? issProp.ToString() : string.Empty;

                string expSummary = "no exp";
                if (payloadEl.TryGetProperty("exp", out var expProp) && expProp.TryGetInt64(out long expUnix))
                {
                    var expTime = DateTimeOffset.FromUnixTimeSeconds(expUnix);
                    var delta = expTime - DateTimeOffset.UtcNow;
                    expSummary = delta.TotalSeconds >= 0
                        ? $"valid ({FormatDuration(delta)} left, {expTime.UtcDateTime:yyyy-MM-dd HH:mm} UTC)"
                        : $"EXPIRED ({FormatDuration(delta.Negate())} ago)";
                }

                var subParts = new List<string> { $"alg={alg}" };
                if (!string.IsNullOrEmpty(sub)) subParts.Add($"sub={sub}");
                if (!string.IsNullOrEmpty(iss)) subParts.Add($"iss={iss}");
                subParts.Add(expSummary);

                summary = string.IsNullOrEmpty(sub) ? $"alg={alg}" : $"sub={sub}";
                claimsSubtitle = string.Join(" • ", subParts);

                var prettyObj = new
                {
                    verification = "NOT VERIFIED (Base64Url decoded offline only)",
                    header = headerEl,
                    payload = payloadEl
                };
                string pretty = JsonSerializer.Serialize(prettyObj, new JsonSerializerOptions { WriteIndented = true });
                formattedOutput = $"// ⚠ WARNING: JWT Signature NOT Verified (Base64Url Decoded Only)\n{pretty}";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string DecodeBase64UrlToUtf8(string segment)
        {
            string padded = segment.Replace('-', '+').Replace('_', '/');
            int mod = padded.Length % 4;
            if (mod == 2) padded += "==";
            else if (mod == 3) padded += "=";
            else if (mod == 1) throw new FormatException("Invalid Base64Url length");

            byte[] bytes = Convert.FromBase64String(padded);
            return Encoding.UTF8.GetString(bytes);
        }

        public static bool TryConvertColor(string input, out string title, out string copyValue, out string subtitle)
        {
            title = string.Empty;
            copyValue = string.Empty;
            subtitle = string.Empty;

            if (string.IsNullOrWhiteSpace(input)) return false;
            string clean = input.Trim();

            int r = 0, g = 0, b = 0;
            bool parsed = false;
            bool startedWithHex = false;

            var hexMatch = Regex.Match(clean, @"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");
            if (hexMatch.Success)
            {
                string hex = hexMatch.Groups[1].Value;
                if (hex.Length == 3)
                {
                    hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
                }
                r = Convert.ToInt32(hex[..2], 16);
                g = Convert.ToInt32(hex.Substring(2, 2), 16);
                b = Convert.ToInt32(hex.Substring(4, 2), 16);
                parsed = true;
                startedWithHex = true;
            }
            else
            {
                var rgbMatch = Regex.Match(clean, @"^rgb\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)$", RegexOptions.IgnoreCase);
                if (rgbMatch.Success &&
                    int.TryParse(rgbMatch.Groups[1].Value, out r) &&
                    int.TryParse(rgbMatch.Groups[2].Value, out g) &&
                    int.TryParse(rgbMatch.Groups[3].Value, out b) &&
                    r <= 255 && g <= 255 && b <= 255)
                {
                    parsed = true;
                }
                else
                {
                    var hslMatch = Regex.Match(clean, @"^hsl\(\s*(\d{1,3})\s*,\s*(\d{1,3})%?\s*,\s*(\d{1,3})%?\s*\)$", RegexOptions.IgnoreCase);
                    if (hslMatch.Success &&
                        int.TryParse(hslMatch.Groups[1].Value, out int h) &&
                        int.TryParse(hslMatch.Groups[2].Value, out int s) &&
                        int.TryParse(hslMatch.Groups[3].Value, out int l) &&
                        h <= 360 && s <= 100 && l <= 100)
                    {
                        (r, g, b) = HslToRgb(h, s / 100.0, l / 100.0);
                        parsed = true;
                    }
                }
            }

            if (!parsed) return false;

            string hexOut = $"#{r:X2}{g:X2}{b:X2}";
            string rgbOut = $"rgb({r}, {g}, {b})";
            var (hOut, sOut, lOut) = RgbToHsl(r, g, b);
            string hslOut = $"hsl({hOut}, {sOut}%, {lOut}%)";

            title = $"{hexOut} • {rgbOut} • {hslOut}";
            copyValue = startedWithHex ? rgbOut : hexOut;
            subtitle = $"Color Converter • Enter copies {copyValue}";
            return true;
        }

        private static (int H, int S, int L) RgbToHsl(int r, int g, int b)
        {
            double rf = r / 255.0;
            double gf = g / 255.0;
            double bf = b / 255.0;
            double max = Math.Max(rf, Math.Max(gf, bf));
            double min = Math.Min(rf, Math.Min(gf, bf));
            double l = (max + min) / 2.0;

            if (Math.Abs(max - min) < 1e-9)
                return (0, 0, (int)Math.Round(l * 100));

            double d = max - min;
            double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            double h = max == rf
                ? (gf - bf) / d + (gf < bf ? 6 : 0)
                : max == gf
                    ? (bf - rf) / d + 2
                    : (rf - gf) / d + 4;
            h /= 6.0;

            return ((int)Math.Round(h * 360) % 360, (int)Math.Round(s * 100), (int)Math.Round(l * 100));
        }

        private static (int R, int G, int B) HslToRgb(double hDeg, double s, double l)
        {
            double h = (hDeg % 360) / 360.0;
            if (s <= 0)
            {
                int v = (int)Math.Round(l * 255);
                return (v, v, v);
            }

            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;

            static double HueToRgb(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
                if (t < 1.0 / 2.0) return q;
                if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
                return p;
            }

            return (
                (int)Math.Round(HueToRgb(p, q, h + 1.0 / 3.0) * 255),
                (int)Math.Round(HueToRgb(p, q, h) * 255),
                (int)Math.Round(HueToRgb(p, q, h - 1.0 / 3.0) * 255));
        }

        public static string GenerateNanoId(int length = 21)
        {
            char[] buf = new char[length];
            for (int i = 0; i < length; i++)
            {
                buf[i] = NanoIdAlphabet[RandomNumberGenerator.GetInt32(NanoIdAlphabet.Length)];
            }
            return new string(buf);
        }

        public static string GenerateUlid()
        {
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            char[] buf = new char[26];
            for (int i = 9; i >= 0; i--)
            {
                buf[i] = CrockfordBase32[(int)(ms & 31)];
                ms >>= 5;
            }

            byte[] rnd = RandomNumberGenerator.GetBytes(16);
            for (int i = 10; i < 26; i++)
            {
                buf[i] = CrockfordBase32[rnd[i - 10] & 31];
            }
            return new string(buf);
        }

        public static string GenerateStrongPassword(int length = 20)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()-_=+";
            char[] pass = new char[length];
            for (int i = 0; i < length; i++)
            {
                pass[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
            return new string(pass);
        }

        public static bool TryParseTimerCommand(string input, out TimeSpan duration, out string label)
        {
            duration = TimeSpan.Zero;
            label = string.Empty;
            if (string.IsNullOrWhiteSpace(input)) return false;

            var m = Regex.Match(input.Trim(), @"^(\d+)\s*(s|sec|secs|m|min|mins|h|hr|hrs)(?:\s+(.+))?$", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            if (!int.TryParse(m.Groups[1].Value, out int qty) || qty <= 0) return false;

            string unit = m.Groups[2].Value.ToLowerInvariant();
            duration = unit[0] switch
            {
                's' => TimeSpan.FromSeconds(Math.Min(qty, 86400)),
                'm' => TimeSpan.FromMinutes(Math.Min(qty, 1440)),
                'h' => TimeSpan.FromHours(Math.Min(qty, 24)),
                _ => TimeSpan.Zero
            };
            label = m.Groups[3].Value.Trim();
            return duration > TimeSpan.Zero;
        }

        public static void StartTimer(TimeSpan duration, string label, Action<string>? onCompleted = null)
        {
            lock (_timerLock)
            {
                _countdownTimer?.Dispose();
                _timerEndUtc = DateTime.UtcNow + duration;
                _timerLabel = label ?? string.Empty;
                string capturedLabel = _timerLabel;

                _countdownTimer = new System.Threading.Timer(_ =>
                {
                    lock (_timerLock)
                    {
                        _countdownTimer?.Dispose();
                        _countdownTimer = null;
                        _timerEndUtc = DateTime.MinValue;
                        _timerLabel = string.Empty;
                    }
                    string msg = string.IsNullOrEmpty(capturedLabel)
                        ? "⏰ Countdown Timer Completed"
                        : $"⏰ Timer Completed: {capturedLabel}";
                    onCompleted?.Invoke(msg);
                    ToastNotification.Show(msg);
                }, null, duration, Timeout.InfiniteTimeSpan);
            }
        }

        public static void CancelTimer()
        {
            lock (_timerLock)
            {
                _countdownTimer?.Dispose();
                _countdownTimer = null;
                _timerEndUtc = DateTime.MinValue;
                _timerLabel = string.Empty;
            }
        }

        public static (bool IsActive, TimeSpan Remaining, string Label) GetTimerStatus()
        {
            lock (_timerLock)
            {
                if (_countdownTimer == null || _timerEndUtc <= DateTime.UtcNow)
                    return (false, TimeSpan.Zero, string.Empty);
                return (true, _timerEndUtc - DateTime.UtcNow, _timerLabel);
            }
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalHours >= 1)
                return $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s";
            if (span.TotalMinutes >= 1)
                return $"{span.Minutes}m {span.Seconds}s";
            return $"{Math.Max(0, (int)Math.Ceiling(span.TotalSeconds))}s";
        }

        private readonly record struct RawSocketEntry(
            string Protocol,
            string LocalAddress,
            int LocalPort,
            string RemoteAddress,
            int RemotePort,
            string State,
            int Pid);

        private static (int ListenCount, int TotalCount) GetPortSocketCounts()
        {
            var rawEntries = new List<RawSocketEntry>(128);
            ReadTcpTable(rawEntries, null);
            ReadUdpTable(rawEntries, null);
            int listen = 0;
            for (int i = 0; i < rawEntries.Count; i++)
            {
                if (rawEntries[i].State == "LISTEN" || rawEntries[i].Protocol == "UDP")
                    listen++;
            }
            return (listen, rawEntries.Count);
        }

        public static List<PortBindingInfo> GetActivePortBindings(
            int? filterPort = null,
            string? textFilter = null,
            bool resolveExecutablePath = true)
        {
            var rawEntries = new List<RawSocketEntry>(filterPort.HasValue ? 8 : 128);
            ReadTcpTable(rawEntries, filterPort);
            ReadUdpTable(rawEntries, filterPort);

            if (rawEntries.Count == 0)
                return new List<PortBindingInfo>();

            var distinctPids = new HashSet<int>();
            for (int i = 0; i < rawEntries.Count; i++)
            {
                if (rawEntries[i].Pid > 0) distinctPids.Add(rawEntries[i].Pid);
            }

            var procCache = BuildProcessInfoLookup(distinctPids, resolveExecutablePath);
            var results = new List<PortBindingInfo>(rawEntries.Count);

            for (int i = 0; i < rawEntries.Count; i++)
            {
                var r = rawEntries[i];
                string procName = r.Pid == 0
                    ? "System Idle"
                    : (procCache.TryGetValue(r.Pid, out var info) ? info.Name : $"PID {r.Pid}");
                string procPath = r.Pid > 0 && procCache.TryGetValue(r.Pid, out var info2)
                    ? info2.Path
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(textFilter))
                {
                    string f = textFilter.Trim();
                    if (!r.LocalPort.ToString(CultureInfo.InvariantCulture).Contains(f, StringComparison.OrdinalIgnoreCase) &&
                        !procName.Contains(f, StringComparison.OrdinalIgnoreCase) &&
                        !r.Protocol.Contains(f, StringComparison.OrdinalIgnoreCase) &&
                        !r.Pid.ToString(CultureInfo.InvariantCulture).Equals(f, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                results.Add(new PortBindingInfo(
                    Protocol: r.Protocol,
                    LocalAddress: r.LocalAddress,
                    LocalPort: r.LocalPort,
                    RemoteAddress: r.RemoteAddress,
                    RemotePort: r.RemotePort,
                    State: r.State,
                    Pid: r.Pid,
                    ProcessName: procName,
                    ExecutablePath: procPath));
            }

            return results
                .OrderByDescending(b => b.State == "LISTEN")
                .ThenBy(b => b.LocalPort)
                .ThenBy(b => b.Protocol)
                .ToList();
        }

        private static Dictionary<int, (string Name, string Path)> BuildProcessInfoLookup(
            HashSet<int> targetPids,
            bool resolveExecutablePath)
        {
            var dict = new Dictionary<int, (string Name, string Path)>(targetPids.Count);
            if (targetPids.Count == 0) return dict;

            // Fast path when only a handful of PIDs matched (e.g. inline `port 3000` query)
            if (targetPids.Count <= 8)
            {
                foreach (int pid in targetPids)
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        string path = resolveExecutablePath ? TryGetProcessExePathFast(pid) : string.Empty;
                        dict[pid] = (p.ProcessName, path);
                    }
                    catch { }
                }
                return dict;
            }

            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        int pid = p.Id;
                        if (targetPids.Contains(pid))
                        {
                            string path = resolveExecutablePath ? TryGetProcessExePathFast(pid) : string.Empty;
                            dict[pid] = (p.ProcessName, path);
                        }
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch { }
            return dict;
        }

        private static string TryGetProcessExePathFast(int pid)
        {
            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return string.Empty;
            try
            {
                var sb = new StringBuilder(512);
                int len = sb.Capacity;
                return QueryFullProcessImageName(hProc, 0, sb, ref len) ? sb.ToString(0, len) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        private static void ReadTcpTable(
            List<RawSocketEntry> output,
            int? filterPort)
        {
            int size = 0;
            uint res = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (res != ERROR_INSUFFICIENT_BUFFER && res != NO_ERROR) return;
            if (size <= 4) return;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != NO_ERROR)
                    return;

                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                IntPtr rowPtr = IntPtr.Add(buffer, 4);

                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);

                    int localPort = DecodeNetworkPort(row.dwLocalPort);
                    if (filterPort.HasValue && localPort != filterPort.Value)
                        continue;

                    output.Add(new RawSocketEntry(
                        Protocol: "TCP",
                        LocalAddress: new IPAddress(row.dwLocalAddr).ToString(),
                        LocalPort: localPort,
                        RemoteAddress: new IPAddress(row.dwRemoteAddr).ToString(),
                        RemotePort: DecodeNetworkPort(row.dwRemotePort),
                        State: DecodeTcpState(row.dwState),
                        Pid: (int)row.dwOwningPid));
                }
            }
            catch { }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void ReadUdpTable(
            List<RawSocketEntry> output,
            int? filterPort)
        {
            int size = 0;
            uint res = GetExtendedUdpTable(IntPtr.Zero, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (res != ERROR_INSUFFICIENT_BUFFER && res != NO_ERROR) return;
            if (size <= 4) return;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buffer, ref size, true, AF_INET, UDP_TABLE_OWNER_PID, 0) != NO_ERROR)
                    return;

                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                IntPtr rowPtr = IntPtr.Add(buffer, 4);

                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);

                    int localPort = DecodeNetworkPort(row.dwLocalPort);
                    if (filterPort.HasValue && localPort != filterPort.Value)
                        continue;

                    output.Add(new RawSocketEntry(
                        Protocol: "UDP",
                        LocalAddress: new IPAddress(row.dwLocalAddr).ToString(),
                        LocalPort: localPort,
                        RemoteAddress: "*",
                        RemotePort: 0,
                        State: "BOUND",
                        Pid: (int)row.dwOwningPid));
                }
            }
            catch { }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static int DecodeNetworkPort(uint rawPort)
        {
            ushort p = (ushort)(rawPort & 0xFFFF);
            return ((p & 0xFF) << 8) | ((p >> 8) & 0xFF);
        }

        private static string DecodeTcpState(uint state) => state switch
        {
            1 => "CLOSED",
            2 => "LISTEN",
            3 => "SYN_SENT",
            4 => "SYN_RCVD",
            5 => "ESTABLISHED",
            6 => "FIN_WAIT1",
            7 => "FIN_WAIT2",
            8 => "CLOSE_WAIT",
            9 => "CLOSING",
            10 => "LAST_ACK",
            11 => "TIME_WAIT",
            12 => "DELETE_TCB",
            _ => $"STATE_{state}"
        };

        public static bool TryFormatJson(string input, bool minify, out string formatted, out string error)
        {
            formatted = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                error = "Clipboard does not contain JSON text.";
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(input);
                formatted = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = !minify });
                return true;
            }
            catch (JsonException ex)
            {
                error = $"Invalid JSON: {ex.Message}";
                return false;
            }
        }

        public static void FormatClipboardJson(bool minify)
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                ToastNotification.Show("Clipboard has no text to format");
                return;
            }

            string raw = System.Windows.Clipboard.GetText();
            if (TryFormatJson(raw, minify, out string formatted, out string err))
            {
                System.Windows.Clipboard.SetText(formatted);
                ToastNotification.Show(minify ? "✓ Minified Clipboard JSON" : "✓ Pretty-Printed Clipboard JSON");
            }
            else
            {
                ToastNotification.Show(err);
            }
        }

        public static bool TryCleanTrackingParams(string input, out string cleanedUrl, out int removedCount)
        {
            cleanedUrl = input;
            removedCount = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;

            if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            if (string.IsNullOrEmpty(uri.Query))
            {
                cleanedUrl = uri.AbsoluteUri;
                return true;
            }

            string rawQuery = uri.Query.TrimStart('?');
            var kept = new List<string>();
            foreach (string pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string key = eq >= 0 ? Uri.UnescapeDataString(pair[..eq]) : Uri.UnescapeDataString(pair);
                if (TrackingQueryParams.Contains(key) || key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
                {
                    removedCount++;
                }
                else
                {
                    kept.Add(pair);
                }
            }

            var builder = new UriBuilder(uri)
            {
                Query = string.Join("&", kept)
            };
            cleanedUrl = builder.Uri.AbsoluteUri;
            return true;
        }

        public static void CleanClipboardUrlTrackingParams()
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                ToastNotification.Show("Clipboard has no URL text");
                return;
            }

            string raw = System.Windows.Clipboard.GetText();
            if (TryCleanTrackingParams(raw, out string cleaned, out int removed))
            {
                System.Windows.Clipboard.SetText(cleaned);
                ToastNotification.Show(removed > 0
                    ? $"✓ Removed {removed} tracking parameter(s) from URL"
                    : "✓ URL already clean (0 tracking parameters found)");
            }
            else
            {
                ToastNotification.Show("Clipboard does not contain a valid HTTP/HTTPS URL");
            }
        }

        public static string TransformText(string input, string mode)
        {
            if (string.IsNullOrEmpty(input)) return input;

            switch (mode.ToLowerInvariant())
            {
                case "sort":
                {
                    var lines = input.Replace("\r\n", "\n").Split('\n');
                    Array.Sort(lines, StringComparer.OrdinalIgnoreCase);
                    return string.Join(Environment.NewLine, lines);
                }
                case "dedup":
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var output = new List<string>();
                    foreach (var line in input.Replace("\r\n", "\n").Split('\n'))
                    {
                        if (seen.Add(line)) output.Add(line);
                    }
                    return string.Join(Environment.NewLine, output);
                }
                case "upper":
                    return input.ToUpperInvariant();
                case "lower":
                    return input.ToLowerInvariant();
                case "title":
                    return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(input.ToLower(CultureInfo.CurrentCulture));
                default:
                    return input;
            }
        }

        public static void TransformClipboardText(string mode, string label)
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                ToastNotification.Show("Clipboard has no text to transform");
                return;
            }

            string raw = System.Windows.Clipboard.GetText();
            string transformed = TransformText(raw, mode);
            System.Windows.Clipboard.SetText(transformed);
            ToastNotification.Show($"✓ Clipboard Transformed: {label}");
        }

        /// <summary>
        /// Stage 1 read-only dry-run scan of stale, unlocked temporary files older than minAge (default 24h).
        /// Never deletes or modifies files during preview.
        /// </summary>
        public static TempCleanupPreview ScanStaleTempFilesPreview(
            string[]? roots = null,
            TimeSpan? minAge = null,
            int maxFiles = 5000)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] targetRoots = roots ?? new[]
            {
                Path.GetTempPath(),
                Path.Combine(localAppData, "CrashDumps")
            };

            TimeSpan age = minAge ?? TimeSpan.FromHours(24);
            DateTime cutoffUtc = DateTime.UtcNow - age;
            var candidates = new List<TempFileCandidate>();
            long totalBytes = 0;
            int skipped = 0;
            var distinctRoots = targetRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            foreach (string root in distinctRoots)
            {
                if (!Directory.Exists(root)) continue;

                var dirs = new Stack<string>();
                dirs.Push(root);

                while (dirs.Count > 0 && candidates.Count < maxFiles)
                {
                    string currentDir = dirs.Pop();
                    try
                    {
                        foreach (string subDir in Directory.EnumerateDirectories(currentDir))
                        {
                            try
                            {
                                var di = new DirectoryInfo(subDir);
                                if ((di.Attributes & FileAttributes.ReparsePoint) == 0)
                                    dirs.Push(subDir);
                            }
                            catch { }
                        }
                    }
                    catch { }

                    try
                    {
                        foreach (string filePath in Directory.EnumerateFiles(currentDir))
                        {
                            if (candidates.Count >= maxFiles) break;
                            try
                            {
                                var fi = new FileInfo(filePath);
                                if ((fi.Attributes & (FileAttributes.System | FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) != 0 ||
                                    fi.LastWriteTimeUtc > cutoffUtc)
                                {
                                    skipped++;
                                    continue;
                                }

                                // Non-destructive lock check via FileShare.None probe (read-only metadata access)
                                using (new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None)) { }

                                candidates.Add(new TempFileCandidate(filePath, fi.Length, fi.LastWriteTimeUtc));
                                totalBytes += fi.Length;
                            }
                            catch
                            {
                                skipped++;
                            }
                        }
                    }
                    catch { }
                }
            }

            return new TempCleanupPreview(candidates, totalBytes, skipped, distinctRoots);
        }

        public static TempCleanupOutcome ExecuteTempFilesCleanup(TempCleanupPreview preview)
        {
            if (preview == null || preview.Files.Count == 0)
                return new TempCleanupOutcome(0, 0, 0);

            int deleted = 0;
            int failed = 0;
            long freed = 0;

            foreach (var item in preview.Files)
            {
                try
                {
                    if (File.Exists(item.FullPath))
                    {
                        File.Delete(item.FullPath);
                        deleted++;
                        freed += item.SizeBytes;
                    }
                }
                catch
                {
                    failed++;
                }
            }

            return new TempCleanupOutcome(deleted, failed, freed);
        }

        public static List<HungWindowInfo> ScanUnresponsiveWindows()
        {
            var results = new List<HungWindowInfo>();
            var seenPids = new HashSet<int>();
            int selfPid = Environment.ProcessId;

            try
            {
                EnumWindows((hWnd, _) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hWnd) || !IsHungAppWindow(hWnd))
                            return true;

                        GetWindowThreadProcessId(hWnd, out uint rawPid);
                        int pid = (int)rawPid;
                        if (pid <= 4 || pid == selfPid || !seenPids.Add(pid))
                            return true;

                        using var proc = Process.GetProcessById(pid);
                        if (MemoryReductService.IsProcessProtected(proc.ProcessName, pid))
                            return true;

                        var sb = new StringBuilder(256);
                        GetWindowText(hWnd, sb, sb.Capacity);
                        string title = sb.ToString().Trim();
                        if (string.IsNullOrEmpty(title)) title = proc.ProcessName;

                        results.Add(new HungWindowInfo(pid, proc.ProcessName, title, hWnd));
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            return results;
        }

        public static void FlushDns()
        {
            try
            {
                uint res = DnsFlushResolverCache();
                if (res != 0)
                {
                    ToastNotification.Show("✓ Windows DNS Resolver Cache Flushed");
                    return;
                }
            }
            catch { }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ipconfig",
                    Arguments = "/flushdns",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                ToastNotification.Show("✓ Windows DNS Cache Flushed (ipconfig /flushdns)");
            }
            catch (Exception ex)
            {
                ToastNotification.Show($"Failed to flush DNS: {ex.Message}");
            }
        }

        public static void OpenHostsFile()
        {
            try
            {
                string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string hostsPath = Path.Combine(windir, "System32", "drivers", "etc", "hosts");

                var psi = new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{hostsPath}\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                ToastNotification.Show("✓ Opened Hosts File (Elevated Notepad)");
            }
            catch (Exception ex)
            {
                ToastNotification.Show($"Could not open hosts file: {ex.Message}");
            }
        }

        public static string GetNetworkSummary()
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up &&
                        ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        var ipProps = ni.GetIPProperties();
                        foreach (var addr in ipProps.UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                sb.Append($"{ni.Name}: {addr.Address} • ");
                            }
                        }
                    }
                }
            }
            catch { }

            string res = sb.ToString().TrimEnd(' ', '•');
            return string.IsNullOrEmpty(res) ? "No active network adapters found" : res;
        }
    }
}
