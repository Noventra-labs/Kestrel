using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Kestrel.Services
{
    public class UpdateInfo
    {
        public string LatestTag { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string HtmlUrl { get; set; } = string.Empty;
        public string InstallerUrl { get; set; } = string.Empty;
        public string ExecutableUrl { get; set; } = string.Empty;
        public long ExecutableSizeBytes { get; set; } = 0;
        public string ExecutableSha256 { get; set; } = string.Empty;
        public string ChecksumsUrl { get; set; } = string.Empty;
        public bool IsNewer { get; set; } = false;
    }

    public enum UpdateStage
    {
        Downloading,
        Verifying,
        Installing
    }

    public readonly record struct UpdateProgressReport(
        UpdateStage Stage,
        long BytesReceived,
        long TotalBytes,
        int Percentage);

    public readonly record struct SelfUpdateResult(bool Started, bool Cancelled, string? ErrorMessage);

    public static class UpdateService
    {
        public static readonly Version CurrentVersion = new Version(2, 4, 0);
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly HttpClient _downloadClient = new HttpClient();
        private static DateTime _lastCheckTime = DateTime.MinValue;
        public static UpdateInfo? CachedUpdateInfo { get; private set; }

        private const string UninstallRegPath = @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Kestrel_NoventraLabs";

        static UpdateService()
        {
            string ua = $"Kestrel-Client/{CurrentVersion} (Windows NT 10.0; Win64; x64)";
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
            _httpClient.Timeout = TimeSpan.FromSeconds(6);

            _downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
            _downloadClient.Timeout = TimeSpan.FromMinutes(10);
        }

        public static async Task<UpdateInfo?> CheckForUpdatesAsync(bool force = false)
        {
            if (!force && DateTime.UtcNow - _lastCheckTime < TimeSpan.FromHours(4) && CachedUpdateInfo != null)
            {
                return CachedUpdateInfo;
            }

            try
            {
                _lastCheckTime = DateTime.UtcNow;
                var res = await _httpClient.GetStringAsync("https://api.github.com/repos/Noventra-labs/Kestrel/releases/latest");
                CachedUpdateInfo = ParseReleaseJson(res, CurrentVersion);
                return CachedUpdateInfo;
            }
            catch
            {
                return CachedUpdateInfo;
            }
        }

        internal static UpdateInfo ParseReleaseJson(string json, Version currentVersion)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
            string body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url", out var htmlElem) ? htmlElem.GetString() ?? "" : "";

            string cleanTag = tagName.TrimStart('v', 'V');
            bool isNewer = false;
            if (Version.TryParse(cleanTag, out var latestVer))
            {
                isNewer = latestVer > currentVersion;
            }

            string installerUrl = "";
            string executableUrl = "";
            long executableSizeBytes = 0;
            string executableSha256 = "";
            string checksumsUrl = "";

            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string dlUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() ?? "" : "";

                    if (name.Equals("Kestrel-Setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        installerUrl = dlUrl;
                    }
                    else if (name.Equals("Kestrel.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        executableUrl = dlUrl;
                        if (asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long parsedSize))
                        {
                            executableSizeBytes = parsedSize;
                        }
                        if (asset.TryGetProperty("digest", out var dig))
                        {
                            string digest = dig.GetString() ?? "";
                            if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            {
                                executableSha256 = digest["sha256:".Length..].Trim();
                            }
                        }
                    }
                    else if (name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        checksumsUrl = dlUrl;
                    }
                }
            }

            return new UpdateInfo
            {
                LatestTag = tagName,
                ReleaseNotes = body,
                HtmlUrl = htmlUrl,
                InstallerUrl = installerUrl,
                ExecutableUrl = executableUrl,
                ExecutableSizeBytes = executableSizeBytes,
                ExecutableSha256 = executableSha256,
                ChecksumsUrl = checksumsUrl,
                IsNewer = isNewer
            };
        }

        internal static string ResolveDownloadTargetUrl(UpdateInfo? info)
        {
            if (info == null)
                return "https://github.com/Noventra-labs/Kestrel/releases/latest";

            if (!string.IsNullOrWhiteSpace(info.ExecutableUrl))
                return info.ExecutableUrl;

            if (!string.IsNullOrWhiteSpace(info.InstallerUrl))
                return info.InstallerUrl;

            if (!string.IsNullOrWhiteSpace(info.HtmlUrl))
                return info.HtmlUrl;

            return "https://github.com/Noventra-labs/Kestrel/releases/latest";
        }

        internal static string? ParseChecksumForAsset(string checksumsContent, string fileName)
        {
            if (string.IsNullOrWhiteSpace(checksumsContent) || string.IsNullOrWhiteSpace(fileName))
                return null;

            // Format 1: PowerShell `Get-FileHash | Format-List`
            // Algorithm : SHA256
            // Hash      : <64 hex>
            // Path      : ...\Kestrel.exe
            string? pendingHash = null;
            foreach (string rawLine in checksumsContent.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    pendingHash = null;
                    continue;
                }

                if (line.StartsWith("Hash", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = line.IndexOf(':');
                    if (colon >= 0)
                    {
                        string candidate = line[(colon + 1)..].Trim();
                        if (IsSha256Hex(candidate))
                            pendingHash = candidate;
                    }
                    continue;
                }

                if (line.StartsWith("Path", StringComparison.OrdinalIgnoreCase) && pendingHash != null)
                {
                    int colon = line.IndexOf(':');
                    if (colon >= 0)
                    {
                        string pathVal = line[(colon + 1)..].Trim();
                        if (string.Equals(Path.GetFileName(pathVal), fileName, StringComparison.OrdinalIgnoreCase))
                            return pendingHash;
                    }
                    continue;
                }

                // Format 2: standard sha256sum `<64 hex>  Kestrel.exe`
                var match = Regex.Match(line, @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                if (match.Success)
                {
                    string candidatePath = match.Groups[2].Value.Trim();
                    if (string.Equals(Path.GetFileName(candidatePath), fileName, StringComparison.OrdinalIgnoreCase))
                        return match.Groups[1].Value;
                }
            }

            return null;
        }

        private static bool IsSha256Hex(string value)
        {
            if (value.Length != 64) return false;
            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        private static string? SanitizeVersionToken(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            string clean = raw.Trim().TrimStart('v', 'V').Trim();
            if (clean.Length == 0 || clean.Length > 32)
                return null;

            foreach (char c in clean)
            {
                bool allowed = (c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || c == '.'
                    || c == '-'
                    || c == '_';
                if (!allowed)
                    return null;
            }

            return clean;
        }

        public static string? ParseUpdatedVersionArg(string[]? args)
        {
            if (args == null || args.Length == 0)
                return null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg))
                    continue;

                if (arg.Equals("--updated", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        return SanitizeVersionToken(args[i + 1]);
                    return null;
                }

                const string prefix = "--updated=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return SanitizeVersionToken(arg[prefix.Length..]);
                }
            }

            return null;
        }

        public static bool IsStandaloneKestrelExe(string? processPath = null)
        {
            string? exePath = processPath ?? Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath))
                return false;

            return string.Equals(Path.GetFileName(exePath), "Kestrel.exe", StringComparison.OrdinalIgnoreCase);
        }

        public static bool CanSelfUpdate(string? processPath = null)
        {
            string? exePath = processPath ?? Environment.ProcessPath;
            if (!IsStandaloneKestrelExe(exePath) || string.IsNullOrWhiteSpace(exePath))
                return false;

            string? dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return false;

            string probe = Path.Combine(dir, $".kestrel-update-probe-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void CleanupPostUpdateArtifacts(string? processPath = null)
        {
            try
            {
                string? exePath = processPath ?? Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath))
                    return;

                string? dir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    return;

                foreach (string artifactName in new[] { "Kestrel.exe.old", "Kestrel.exe.update" })
                {
                    string candidate = Path.Combine(dir, artifactName);
                    if (File.Exists(candidate))
                    {
                        try { File.Delete(candidate); } catch { }
                    }
                }
            }
            catch { }
        }

        internal static string BuildSelfUpdateScript(string currentExePath, string stagedExePath, int processId, string? newDisplayVersion)
        {
            if (!Path.IsPathFullyQualified(currentExePath))
                throw new InvalidOperationException("Current executable path must be fully qualified.");
            if (!Path.IsPathFullyQualified(stagedExePath))
                throw new InvalidOperationException("Staged executable path must be fully qualified.");

            string fullCurrent = Path.GetFullPath(currentExePath);
            string fullStaged = Path.GetFullPath(stagedExePath);
            string backupPath = fullCurrent + ".old";

            static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

            string? cleanVersion = SanitizeVersionToken(newDisplayVersion);
            string regUpdate = cleanVersion != null
                ? $"if (Test-Path -LiteralPath {Quote(UninstallRegPath)}) {{ " +
                  $"Set-ItemProperty -LiteralPath {Quote(UninstallRegPath)} -Name 'DisplayVersion' -Value {Quote(cleanVersion)} -ErrorAction SilentlyContinue }}; "
                : "";

            string relaunchArgs = cleanVersion != null
                ? $"@('--replace', '--updated', {Quote(cleanVersion)})"
                : "'--replace'";

            return "$ErrorActionPreference = 'Stop'; " +
                $"Wait-Process -Id {processId} -Timeout 20 -ErrorAction SilentlyContinue; " +
                "$moved = $false; " +
                $"$backup = {Quote(backupPath)}; " +
                "for ($i = 0; $i -lt 30; $i++) { " +
                "try { " +
                $"if (Test-Path -LiteralPath {Quote(fullCurrent)} -PathType Leaf) {{ Move-Item -LiteralPath {Quote(fullCurrent)} -Destination $backup -Force }}; " +
                $"Move-Item -LiteralPath {Quote(fullStaged)} -Destination {Quote(fullCurrent)} -Force; " +
                "$moved = $true; break " +
                "} catch { " +
                $"if ((Test-Path -LiteralPath $backup -PathType Leaf) -and -not (Test-Path -LiteralPath {Quote(fullCurrent)} -PathType Leaf)) {{ " +
                $"try {{ Move-Item -LiteralPath $backup -Destination {Quote(fullCurrent)} -Force }} catch {{}} }}; " +
                "Start-Sleep -Milliseconds 200 } }; " +
                "if (-not $moved) { exit 1 }; " +
                "Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue; " +
                regUpdate +
                $"Start-Process -FilePath {Quote(fullCurrent)} -ArgumentList {relaunchArgs}";
        }

        public static async Task<SelfUpdateResult> DownloadAndApplyUpdateAsync(
            UpdateInfo info,
            IProgress<UpdateProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(info.ExecutableUrl) ||
                !Uri.TryCreate(info.ExecutableUrl, UriKind.Absolute, out var downloadUri) ||
                !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return new SelfUpdateResult(false, false, "No direct executable update URL available.");
            }

            string? currentExePath = Environment.ProcessPath;
            if (!IsStandaloneKestrelExe(currentExePath) || string.IsNullOrWhiteSpace(currentExePath))
            {
                return new SelfUpdateResult(false, false, "Self-update is only supported when running Kestrel.exe directly.");
            }

            bool dirWritable = CanSelfUpdate(currentExePath);
            string stagedPath;
            if (dirWritable)
            {
                string dir = Path.GetDirectoryName(currentExePath)!;
                stagedPath = Path.Combine(dir, "Kestrel.exe.update");
            }
            else
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "Kestrel-Update");
                Directory.CreateDirectory(tempDir);
                stagedPath = Path.Combine(tempDir, "Kestrel.exe.update");
            }

            try
            {
                if (File.Exists(stagedPath))
                    File.Delete(stagedPath);

                using var response = await _downloadClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? info.ExecutableSizeBytes;
                long downloaded = 0;

                await using (var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var fileStream = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    byte[] buffer = new byte[81920];
                    int lastReportedPct = -1;
                    long lastReportTick = Environment.TickCount64;
                    int read;

                    while ((read = await remoteStream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        downloaded += read;

                        int pct = totalBytes > 0 ? (int)Math.Clamp(downloaded * 100L / totalBytes, 0, 100) : 0;
                        long nowTick = Environment.TickCount64;
                        if ((totalBytes > 0 && pct != lastReportedPct) || nowTick - lastReportTick >= 150)
                        {
                            lastReportedPct = pct;
                            lastReportTick = nowTick;
                            progress?.Report(new UpdateProgressReport(UpdateStage.Downloading, downloaded, totalBytes, pct));
                        }
                    }

                    progress?.Report(new UpdateProgressReport(UpdateStage.Downloading, downloaded, totalBytes > 0 ? totalBytes : downloaded, 100));
                }

                progress?.Report(new UpdateProgressReport(UpdateStage.Verifying, downloaded, totalBytes > 0 ? totalBytes : downloaded, 100));

                // Verify SHA-256 checksum if available from release metadata or checksums.txt
                string? expectedHash = !string.IsNullOrWhiteSpace(info.ExecutableSha256)
                    ? info.ExecutableSha256
                    : null;

                if (expectedHash == null && !string.IsNullOrWhiteSpace(info.ChecksumsUrl))
                {
                    try
                    {
                        string checksumsText = await _httpClient.GetStringAsync(info.ChecksumsUrl, cancellationToken);
                        expectedHash = ParseChecksumForAsset(checksumsText, "Kestrel.exe");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch { /* Non-fatal if checksums.txt cannot be fetched */ }
                }

                if (!string.IsNullOrWhiteSpace(expectedHash))
                {
                    string actualHash;
                    await using (var verifyStream = File.OpenRead(stagedPath))
                    {
                        byte[] hashBytes = await SHA256.HashDataAsync(verifyStream, cancellationToken);
                        actualHash = Convert.ToHexString(hashBytes);
                    }

                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteFile(stagedPath);
                        return new SelfUpdateResult(false, false, "Downloaded update failed SHA-256 verification.");
                    }
                }

                progress?.Report(new UpdateProgressReport(UpdateStage.Installing, downloaded, totalBytes > 0 ? totalBytes : downloaded, 100));

                string script = BuildSelfUpdateScript(currentExePath, stagedPath, Environment.ProcessId, info.LatestTag);
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                string psExe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");

                ProcessStartInfo psi;
                if (dirWritable)
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = psExe,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    psi.ArgumentList.Add("-NoProfile");
                    psi.ArgumentList.Add("-NonInteractive");
                    psi.ArgumentList.Add("-WindowStyle");
                    psi.ArgumentList.Add("Hidden");
                    psi.ArgumentList.Add("-EncodedCommand");
                    psi.ArgumentList.Add(encoded);
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = psExe,
                        Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                }

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    TryDeleteFile(stagedPath);
                    return new SelfUpdateResult(false, false, "Unable to start update helper.");
                }

                return new SelfUpdateResult(true, false, null);
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(stagedPath);
                return new SelfUpdateResult(false, true, "Update cancelled.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                TryDeleteFile(stagedPath);
                return new SelfUpdateResult(false, true, "Elevation cancelled.");
            }
            catch (Exception ex)
            {
                TryDeleteFile(stagedPath);
                return new SelfUpdateResult(false, false, ex.Message);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        public static void OpenReleasePage(string? url = null)
        {
            try
            {
                string target = !string.IsNullOrEmpty(url) ? url : (CachedUpdateInfo?.HtmlUrl ?? "https://github.com/Noventra-labs/Kestrel/releases/latest");
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
