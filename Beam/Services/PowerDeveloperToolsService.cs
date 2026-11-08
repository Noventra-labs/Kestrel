using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Beam.Interop;
using Beam.Models;

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
    }

    /// <summary>
    /// Developer PowerToys & SysAdmin Utilities for Beam.
    /// Supports inline evaluators (hashing, base64, guid, epoch) and system actions (hosts file, dns flush).
    /// </summary>
    public static class PowerDeveloperToolsService
    {
        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        private static extern uint DnsFlushResolverCache();

        public static InlineEvalResult EvaluateInline(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new InlineEvalResult();

            var trimmed = query.Trim();

            // 1. GUID / UUID Generator
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
                    Theme = SquircleTheme.AdminGold
                };
            }

            // 2. Epoch & Unix Timestamp Converter
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
                    Theme = SquircleTheme.WebCyan
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
                            Theme = SquircleTheme.WebCyan
                        };
                    }
                    catch { }
                }
            }

            // 3. SHA256 Hash
            if (trimmed.StartsWith("sha256 ", StringComparison.OrdinalIgnoreCase))
            {
                string input = trimmed[7..];
                using var sha = SHA256.Create();
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                string hex = Convert.ToHexString(hash).ToLowerInvariant();

                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = hex,
                    Result = hex,
                    Subtitle = $"SHA256(\"{input}\") • Click or Enter to Copy Hash",
                    Category = "Hasher",
                    SvgPath = IconGenerator.PathHash,
                    Theme = SquircleTheme.SystemPurple
                };
            }

            // 4. MD5 Hash
            if (trimmed.StartsWith("md5 ", StringComparison.OrdinalIgnoreCase))
            {
                string input = trimmed[4..];
                using var md5 = MD5.Create();
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                string hex = Convert.ToHexString(hash).ToLowerInvariant();

                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = hex,
                    Result = hex,
                    Subtitle = $"MD5(\"{input}\") • Click or Enter to Copy Hash",
                    Category = "Hasher",
                    SvgPath = IconGenerator.PathHash,
                    Theme = SquircleTheme.SystemPurple
                };
            }

            // 5. SHA1 Hash
            if (trimmed.StartsWith("sha1 ", StringComparison.OrdinalIgnoreCase))
            {
                string input = trimmed[5..];
                using var sha1 = SHA1.Create();
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
                string hex = Convert.ToHexString(hash).ToLowerInvariant();

                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = hex,
                    Result = hex,
                    Subtitle = $"SHA1(\"{input}\") • Click or Enter to Copy Hash",
                    Category = "Hasher",
                    SvgPath = IconGenerator.PathHash,
                    Theme = SquircleTheme.SystemPurple
                };
            }

            // 6. Base64 Encode
            if (trimmed.StartsWith("base64 ", StringComparison.OrdinalIgnoreCase))
            {
                string input = trimmed[7..];
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(input));

                return new InlineEvalResult
                {
                    IsValid = true,
                    Title = b64,
                    Result = b64,
                    Subtitle = $"Base64 Encoded • Click or Enter to Copy",
                    Category = "Base64 Encoder",
                    SvgPath = IconGenerator.PathTerminal,
                    Theme = SquircleTheme.ClipAmber
                };
            }

            // 7. Base64 Decode
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
                        Theme = SquircleTheme.ClipAmber
                    };
                }
                catch { }
            }

            return new InlineEvalResult();
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
                    Verb = "runas" // Request elevation so user can save changes
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
