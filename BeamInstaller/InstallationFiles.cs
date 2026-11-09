using System.Diagnostics;
using System.IO;
using System.Text;

namespace BeamInstaller;

internal static class InstallationFiles
{
    internal static readonly string[] OwnedFiles = { "Kestrel.exe", "beam.ico", "Uninstall-Kestrel.exe" };

    internal static string ValidateDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("Choose an absolute installation folder.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an application folder, not the root of a drive.");
        return full;
    }

    internal static string ResolveUninstallDirectory(string? processPath, string? registeredDirectory)
    {
        // A copied setup launched with --uninstall uses the registry. The installed
        // uninstaller prefers its own location, including after a custom install.
        if (string.Equals(Path.GetFileName(processPath), "Uninstall-Kestrel.exe", StringComparison.OrdinalIgnoreCase))
        {
            string candidate = ValidateDirectory(Path.GetDirectoryName(processPath!)!);
            if (File.Exists(Path.Combine(candidate, "Kestrel.exe"))) return candidate;
        }
        if (!string.IsNullOrWhiteSpace(registeredDirectory))
            return ValidateDirectory(registeredDirectory);
        throw new InvalidOperationException("Cannot locate the installed Kestrel folder. No files were removed.");
    }

    internal static string BuildCleanupScript(string directory, int processId)
    {
        directory = ValidateDirectory(directory);
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string paths = string.Join(",", OwnedFiles.Select(name => Quote(Path.Combine(directory, name))));
        // No recursive removal: a custom folder may also contain the user's files.
        return $"$ErrorActionPreference = 'Stop'; " +
            $"Wait-Process -Id {processId} -ErrorAction SilentlyContinue; " +
            $"foreach ($kestrelFile in @({paths})) {{ " +
            "if (Test-Path -LiteralPath $kestrelFile -PathType Leaf) { Remove-Item -LiteralPath $kestrelFile -Force } }; " +
            $"if ((Test-Path -LiteralPath {Quote(directory)} -PathType Container) -and " +
            $"-not (Get-ChildItem -LiteralPath {Quote(directory)} -Force | Select-Object -First 1)) " +
            $"{{ Remove-Item -LiteralPath {Quote(directory)} }}";
    }

    internal static void ScheduleCleanup(string directory)
    {
        string script = BuildCleanupScript(directory, Environment.ProcessId);
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-WindowStyle");
        info.ArgumentList.Add("Hidden");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(info) ?? throw new IOException("Unable to start file cleanup.");
    }
}
