using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kestrel.Services
{
    public enum MemoryCleanProfile
    {
        SmartReclaim,
        WorkingSetsAll,
        LowPriorityStandbyOnly,
        FullKernelAndWorkingSets
    }

    public struct MemoryOperationReport
    {
        public string OperationName { get; init; }
        public bool Attempted { get; init; }
        public bool Succeeded { get; init; }
        public int StatusOrErrorCode { get; init; }
        public string Message { get; init; }
    }

    public record struct ProcessGroupMemoryInfo(
        string ProcessName,
        int ProcessCount,
        int RepresentativePid,
        long TotalWorkingSetBytes,
        bool IsProtectedSystem,
        bool IsExcludedByUser,
        bool IsForeground);

    public record MemoryReductCommandDescriptor(
        string Id,
        string Name,
        string Subtitle,
        string Category,
        string ShortcutBadge,
        string Keywords,
        MemoryCleanProfile Profile);

    public struct MemoryStats
    {
        public uint LoadPercent;
        public double TotalPhysGb;
        public double UsedPhysGb;
        public double AvailPhysGb;
        public long TotalPhysBytes;
        public long AvailPhysBytes;
        public long UsedPhysBytes;

        // Independent performance & kernel metrics
        public long SystemCacheBytes;
        public double SystemCacheMb => SystemCacheBytes / (1024.0 * 1024.0);
        public long CommitTotalBytes;
        public long CommitLimitBytes;
        public double CommitTotalGb => CommitTotalBytes / (1024.0 * 1024.0 * 1024.0);
        public double CommitLimitGb => CommitLimitBytes / (1024.0 * 1024.0 * 1024.0);
        public long PageSizeBytes;

        // Kernel Memory List breakdown (available when OS supports & privilege permits)
        public bool HasKernelListMetrics;
        public long StandbyBytes;
        public double StandbyMb => StandbyBytes / (1024.0 * 1024.0);
        public long ModifiedBytes;
        public double ModifiedMb => ModifiedBytes / (1024.0 * 1024.0);
        public long FreePageBytes;
        public double FreePageMb => FreePageBytes / (1024.0 * 1024.0);
    }

    public class MemoryCleanResult
    {
        public MemoryCleanProfile Profile { get; set; }
        public uint LoadBefore { get; set; }
        public uint LoadAfter { get; set; }
        public double TotalPhysGb { get; set; }
        public double UsedPhysGbBefore { get; set; }
        public double UsedPhysGbAfter { get; set; }
        public double AvailPhysGbBefore { get; set; }
        public double AvailPhysGbAfter { get; set; }

        // Independent Dimension 1: Process Working Sets (never summed with cache/avail deltas)
        public long WorkingSetBeforeBytes { get; set; }
        public long WorkingSetAfterBytes { get; set; }
        public long WorkingSetTrimmedBytes { get; set; }
        public double WorkingSetTrimmedMb => Math.Max(0, WorkingSetTrimmedBytes / (1024.0 * 1024.0));
        public int ProcessesScanned { get; set; }
        public int ProcessesCleaned { get; set; }
        public int ProcessesSkippedProtected { get; set; }
        public int ProcessesSkippedExcluded { get; set; }
        public int ProcessesSkippedBelowThreshold { get; set; }
        public int ProcessesAccessDenied { get; set; }
        public int ProcessesFailed { get; set; }

        // Independent Dimension 2: System & Kernel Cache Lists (never summed with working set deltas)
        public long SystemCacheBeforeBytes { get; set; }
        public long SystemCacheAfterBytes { get; set; }
        public long SystemCacheDeltaBytes => SystemCacheBeforeBytes > SystemCacheAfterBytes
            ? SystemCacheBeforeBytes - SystemCacheAfterBytes
            : 0;
        public double SystemCacheDeltaMb => SystemCacheDeltaBytes / (1024.0 * 1024.0);

        public long StandbyBeforeBytes { get; set; }
        public long StandbyAfterBytes { get; set; }
        public long StandbyDeltaBytes => StandbyBeforeBytes > StandbyAfterBytes
            ? StandbyBeforeBytes - StandbyAfterBytes
            : 0;
        public double StandbyDeltaMb => StandbyDeltaBytes / (1024.0 * 1024.0);

        public long ModifiedBeforeBytes { get; set; }
        public long ModifiedAfterBytes { get; set; }
        public long ModifiedDeltaBytes => ModifiedBeforeBytes > ModifiedAfterBytes
            ? ModifiedBeforeBytes - ModifiedAfterBytes
            : 0;
        public double ModifiedDeltaMb => ModifiedDeltaBytes / (1024.0 * 1024.0);

        // Independent Dimension 3: Physical Available RAM Delta (signed; may be <= 0 on concurrent allocation)
        public long AvailPhysBeforeBytes { get; set; }
        public long AvailPhysAfterBytes { get; set; }
        public long AvailPhysDeltaBytes => AvailPhysAfterBytes - AvailPhysBeforeBytes;
        public double AvailPhysDeltaMb => AvailPhysDeltaBytes / (1024.0 * 1024.0);

        // Backwards-compatible non-negative physical availability delta (never sums independent dimensions)
        public long FreedBytes => Math.Max(0, AvailPhysDeltaBytes);
        public double FreedMb => FreedBytes / (1024.0 * 1024.0);

        public bool StandbyPurged { get; set; }
        public bool WorkingSetsSwept { get; set; }
        public bool FileCacheFlushed { get; set; }
        public bool ModifiedListFlushed { get; set; }
        public bool IsElevated { get; set; }
        public bool UsedScheduledTask { get; set; }
        public bool WasCancelled { get; set; }
        public bool ElevationCancelled { get; set; }
        public bool IsPartialSuccess { get; set; }
        public bool TelemetryUnavailable { get; set; }
        public bool WorkerTimedOut { get; set; }
        public bool IsBusySkipped { get; set; }
        public int LastWin32Error { get; set; }
        public int LastNtStatus { get; set; }
        public string StatusSummary { get; set; } = string.Empty;
        public List<MemoryOperationReport> Operations { get; set; } = new();

        public string FormatSummary()
        {
            if (IsBusySkipped)
                return "Skipped: another memory operation is already in progress";

            if (WasCancelled)
                return $"Cancelled • Trimmed {WorkingSetTrimmedMb:N0} MB working sets across {ProcessesCleaned} processes";

            if (ElevationCancelled && !WorkingSetsSwept)
                return "Administrator permission was declined (UAC cancelled)";

            if (WorkerTimedOut && !WorkingSetsSwept)
                return "Elevated worker timed out before publishing verified telemetry";

            if (TelemetryUnavailable && !WorkingSetsSwept)
                return "Elevated worker completed without verified telemetry (result unconfirmed)";

            var parts = new List<string>();

            if (WorkingSetsSwept || ProcessesCleaned > 0)
            {
                parts.Add($"Working Sets: {WorkingSetTrimmedMb:N0} MB trimmed ({ProcessesCleaned} procs)");
            }

            if (StandbyPurged)
            {
                parts.Add(StandbyDeltaBytes > 0
                    ? $"Standby Cache: {StandbyDeltaMb:N0} MB purged"
                    : "Standby Cache: purged");
            }

            if (FileCacheFlushed || SystemCacheDeltaBytes > 0)
            {
                parts.Add($"System Cache: {SystemCacheDeltaMb:N0} MB delta");
            }

            if (ModifiedListFlushed)
            {
                parts.Add("Modified List: flushed");
            }

            if (IsPartialSuccess)
            {
                if (WorkerTimedOut) parts.Add("Kernel Purge: timed out");
                else if (TelemetryUnavailable) parts.Add("Kernel Purge: telemetry unverified");
                else if (ElevationCancelled) parts.Add("Kernel Purge: UAC cancelled");
                else if (Operations != null)
                {
                    var failedOp = Operations.FirstOrDefault(o =>
                        o.Attempted && !o.Succeeded &&
                        o.OperationName != "MemoryCombine" && o.OperationName != "RegistryReconciliation");
                    if (!string.IsNullOrEmpty(failedOp.OperationName))
                    {
                        parts.Add($"{failedOp.OperationName}: failed");
                    }
                }
            }
            else if (parts.Count == 0 && Operations != null)
            {
                var failedOp = Operations.LastOrDefault(o => o.Attempted && !o.Succeeded);
                if (!string.IsNullOrEmpty(failedOp.Message))
                {
                    parts.Add(failedOp.Message);
                }
            }

            string availSign = AvailPhysDeltaMb >= 0 ? "+" : "";
            parts.Add($"Avail RAM: {availSign}{AvailPhysDeltaMb:N0} MB");
            parts.Add($"Load: {LoadBefore}% → {LoadAfter}%");

            string body = string.Join(" • ", parts);
            return IsPartialSuccess ? $"Partial: {body}" : body;
        }
    }

    internal readonly record struct ProcessSnapshotEntry(int Pid, string ProcessName, long WorkingSetBytes);

    internal readonly record struct ProcessTrimOutcome(
        bool Attempted,
        bool Succeeded,
        bool SkippedBelowThreshold,
        long BeforeBytes,
        long AfterBytes,
        int Win32Error);

    internal readonly record struct ElevatedWorkerLaunchResult(
        bool Started,
        bool Exited,
        bool TimedOut,
        bool CancelledByUser,
        bool CancelledByToken,
        int ExitCode);

    internal interface IPathSecurityInspector
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        FileAttributes GetAttributes(string path);
        string? GetReparseTarget(string path, bool isDirectory);
        FileSystemSecurity GetDirectorySecurity(string path);
        FileSystemSecurity GetFileSecurity(string path);
        IEnumerable<string> EnumerateFiles(string directory);
    }

    internal interface IMemoryReductPlatform
    {
        bool IsElevated { get; }
        Version GetOsVersion();
        bool GetGlobalMemoryStatus(out ulong totalPhys, out ulong availPhys, out uint memoryLoad);
        bool GetPerformanceInfo(out long systemCacheBytes, out long commitTotalBytes, out long commitLimitBytes, out long pageSizeBytes);
        int QueryMemoryListInformation(long pageSize, out long standbyBytes, out long modifiedBytes, out long freeBytes);
        bool TryAcquirePrivilege(string privilegeName, out int win32Error);
        int SetSystemMemoryListCommand(int command);
        int SetSystemFileCacheNt();
        bool SetSystemFileCacheWin32(out int win32Error);
        int SetRegistryReconciliation();
        int SetCombineMemoryLists();
        (int Pid, string Name) ResolveForegroundProcess(int explicitForegroundPid);
        IReadOnlyList<ProcessSnapshotEntry> GetProcesses();
        bool TryGetProcessName(int pid, out string processName, out string errorMessage, out int errorCode);
        ProcessTrimOutcome TrimProcessWorkingSet(int pid, long minWorkingSetBytes);
        bool IsScheduledCleanTaskInstalled();
        bool TryRunScheduledCleanTask();
        ElevatedWorkerLaunchResult LaunchElevatedWorker(string argument, string requestId, CancellationToken cancellationToken);
        void Wait(int milliseconds, CancellationToken cancellationToken);
    }

    internal sealed class HeadlessWorkerTelemetry
    {
        public string RequestId { get; set; } = string.Empty;
        public DateTime UtcTimestamp { get; set; }
        public string Operation { get; set; } = string.Empty;
        public long StandbyBeforeBytes { get; set; }
        public long StandbyAfterBytes { get; set; }
        public long ModifiedBeforeBytes { get; set; }
        public long ModifiedAfterBytes { get; set; }
        public long SystemCacheBeforeBytes { get; set; }
        public long SystemCacheAfterBytes { get; set; }
        public long WorkingSetTrimmedBytes { get; set; }
        public int ProcessesCleaned { get; set; }
        public bool StandbyPurged { get; set; }
        public bool FileCacheFlushed { get; set; }
        public bool ModifiedListFlushed { get; set; }
        public bool AllSucceeded { get; set; }
        public bool PartialSuccess { get; set; }
        public List<MemoryOperationReport> Operations { get; set; } = new();
    }

    /// <summary>
    /// Safe Windows NT Memory Management &amp; Reduction Engine.
    /// Reports Working Set, Standby/System Cache, and Physical Available RAM deltas independently.
    /// Defaults to Smart Reclaim (least-privilege background trim protecting foreground &amp; critical OS processes).
    /// </summary>
    public static class MemoryReductService
    {
        // NT Information Classes
        private const int SystemFileCacheInformation = 21;
        private const int SystemMemoryListInformation = 80;
        private const int SystemCombinePhysicalMemoryInformation = 130;
        private const int SystemRegistryReconciliationInformation = 134;

        // NTSTATUS values
        private const int STATUS_SUCCESS = 0;
        private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
        private const int STATUS_INVALID_INFO_CLASS = unchecked((int)0xC0000003);
        private const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);
        private const int STATUS_PRIVILEGE_NOT_HELD = unchecked((int)0xC0000061);

        // Win32 Errors
        private const int ERROR_SUCCESS = 0;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_CANCELLED = 1223;
        private const int ERROR_NOT_ALL_ASSIGNED = 1300;

        public const string ArgCleanStandbyLow = "--clean-memory-standby-low";
        public const string ArgCleanKernel = "--clean-memory-kernel";
        public const string ArgLegacyCleanAll = "--clean-memory";
        public const string ArgLegacyCleanStandby = "--clean-memory-standby";
        public const string ArgRequestIdPrefix = "--request-id=";
        public const string ScheduledTaskName = @"\Kestrel\KestrelMemoryReduct";

        public static readonly IReadOnlySet<string> ProtectedSystemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System",
            "Idle",
            "Registry",
            "MemCompression",
            "smss",
            "csrss",
            "wininit",
            "services",
            "lsass",
            "lsm",
            "svchost",
            "winlogon",
            "dwm",
            "explorer",
            "audiodg",
            "fontdrvhost",
            "sihost",
            "taskhostw",
            "RuntimeBroker",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "SearchIndexer",
            "spoolsv",
            "MsMpEng",
            "NisSrv",
            "SecurityHealthService",
            "WmiPrvSE"
        };

        private enum MemoryListCommand : int
        {
            MemoryCaptureAccessedBits = 0,
            MemoryCaptureAndResetAccessedBits = 1,
            MemoryEmptyWorkingSets = 2,
            MemoryFlushModifiedList = 3,
            MemoryPurgeStandbyList = 4,
            MemoryPurgeLowPriorityStandbyList = 5,
            MemoryCommandMax = 6
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal;
            public UIntPtr CommitLimit;
            public UIntPtr CommitPeak;
            public UIntPtr PhysicalTotal;
            public UIntPtr PhysicalAvailable;
            public UIntPtr SystemCache;
            public UIntPtr KernelTotal;
            public UIntPtr KernelPaged;
            public UIntPtr KernelNonpaged;
            public UIntPtr PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_MEMORY_LIST_INFORMATION
        {
            public UIntPtr ZeroPageCount;
            public UIntPtr FreePageCount;
            public UIntPtr ModifiedPageCount;
            public UIntPtr ModifiedNoWritePageCount;
            public UIntPtr BadPageCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public UIntPtr[] PageCountByPriority;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public UIntPtr[] RepurposedPagesByPriority;
            public UIntPtr ModifiedPageCountPageFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_FILECACHE_INFORMATION
        {
            public IntPtr CurrentSize;
            public IntPtr PeakSize;
            public uint PageFaultCount;
            public IntPtr MinimumWorkingSet;
            public IntPtr MaximumWorkingSet;
            public IntPtr CurrentSizeIncludingTransitionInPages;
            public IntPtr PeakSizeIncludingTransitionInPages;
            public uint TransitionRePurposeCount;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_COMBINE_INFORMATION_EX
        {
            public IntPtr Handle;
            public IntPtr PagesCombined;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RTL_OSVERSIONINFOW
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        private const string SE_INCREASE_QUOTA_NAME = "SeIncreaseQuotaPrivilege";
        private const string SE_PROFILE_SINGLE_PROCESS_NAME = "SeProfileSingleProcessPrivilege";
        private const string SE_DEBUG_NAME = "SeDebugPrivilege";

        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS ppsmemCounters, uint cb);

        [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool K32GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetSystemFileCacheSize(IntPtr MinimumFileCacheSize, IntPtr MaximumFileCacheSize, uint Flags);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("ntdll.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern int NtSetSystemInformation(
            int SystemInformationClass,
            IntPtr SystemInformation,
            int SystemInformationLength);

        [DllImport("ntdll.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern int NtQuerySystemInformation(
            int SystemInformationClass,
            IntPtr SystemInformation,
            int SystemInformationLength,
            out int ReturnLength);

        [DllImport("ntdll.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW lpVersionInformation);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr TokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
            ref TOKEN_PRIVILEGES NewState,
            uint BufferLength,
            IntPtr PreviousState,
            IntPtr ReturnLength);

        private static readonly object _syncLock = new();
        private static readonly HashSet<int> _unsupportedInfoClasses = new();
        private static int _activeCleanInFlight;

        private static bool _hasQuotaPrivilege;
        private static bool _hasProfilePrivilege;
        private static bool _hasDebugPrivilege;

        public static bool HasQuotaPrivilege => _hasQuotaPrivilege;
        public static bool HasProfilePrivilege => _hasProfilePrivilege;
        public static bool HasDebugPrivilege => _hasDebugPrivilege;

        internal static IMemoryReductPlatform Platform { get; set; } = new Win32MemoryReductPlatform();
        internal static IPathSecurityInspector PathInspector { get; set; } = new Win32PathSecurityInspector();

        internal static bool TryEnterCleanGuardForTests() => Interlocked.CompareExchange(ref _activeCleanInFlight, 1, 0) == 0;
        internal static void ExitCleanGuardForTests() => Interlocked.Exchange(ref _activeCleanInFlight, 0);

        internal static void ResetPrivilegeCacheForTests()
        {
            _hasQuotaPrivilege = false;
            _hasProfilePrivilege = false;
            _hasDebugPrivilege = false;
            lock (_syncLock)
            {
                _unsupportedInfoClasses.Clear();
            }
        }

        // Sustained-pressure Auto Memory Reduct state
        private static int _consecutivePressureSamples;
        private static int _autoCleanBackoffLevel;
        private static DateTime _lastAutoCleanTime = DateTime.MinValue;
        private static DateTime _lastManualCleanTime = DateTime.MinValue;

        public const int BaseAutoCooldownSeconds = 300;
        public const int AutoReductCooldownSeconds = BaseAutoCooldownSeconds;

        internal static string HeadlessTelemetryDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kestrel",
            "memreduct-telemetry");

        internal static bool IsReparsePointPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
            catch
            {
                return true;
            }
        }

        internal static string GetTelemetryFilePath(string requestId)
        {
            if (!IsValidCorrelationId(requestId))
                throw new ArgumentException("Invalid worker correlation ID.", nameof(requestId));

            return Path.Combine(HeadlessTelemetryDirectory, $"memreduct-{requestId}.json");
        }

        public static bool IsElevated => Platform.IsElevated;

        public static Version GetTrueOsVersion() => Platform.GetOsVersion();

        public static bool CanAutoClean
        {
            get
            {
                lock (_syncLock)
                {
                    int cooldown = GetCurrentAutoCooldownSeconds();
                    return (DateTime.UtcNow - _lastAutoCleanTime).TotalSeconds >= cooldown &&
                           (DateTime.UtcNow - _lastManualCleanTime).TotalSeconds >= 30;
                }
            }
        }

        internal static void ResetAutoCleanStateForTests()
        {
            lock (_syncLock)
            {
                _consecutivePressureSamples = 0;
                _autoCleanBackoffLevel = 0;
                _lastAutoCleanTime = DateTime.MinValue;
                _lastManualCleanTime = DateTime.MinValue;
            }
        }

        public static int GetCurrentAutoCooldownSeconds()
        {
            return _autoCleanBackoffLevel switch
            {
                0 => 300,
                1 => 900,
                _ => 1800
            };
        }

        /// <summary>
        /// Evaluates sustained memory pressure (3 consecutive samples above threshold + low available RAM)
        /// with 300s base cooldown and exponential backoff (900s/1800s) when little memory is reclaimed.
        /// </summary>
        public static bool ShouldTriggerAutoClean(
            MemoryStats stats,
            KestrelSettings settings,
            DateTime utcNow,
            out string reason)
        {
            lock (_syncLock)
            {
                if (!settings.EnableMemReduct || !settings.AutoMemReductEnabled)
                {
                    _consecutivePressureSamples = 0;
                    reason = "Auto Memory Reduct is disabled";
                    return false;
                }

                // Relief hysteresis: reset pressure streak and backoff once load drops 5% below threshold
                if (stats.LoadPercent < Math.Max(0, settings.AutoMemReductThreshold - 5))
                {
                    _consecutivePressureSamples = 0;
                    _autoCleanBackoffLevel = 0;
                    reason = "Memory pressure below relief threshold";
                    return false;
                }

                long minAvailBytes = Math.Max(256, settings.AutoMemReductMinAvailMb) * 1024L * 1024L;
                bool underPressure = stats.LoadPercent >= settings.AutoMemReductThreshold &&
                                     stats.AvailPhysBytes <= minAvailBytes;

                if (!underPressure)
                {
                    _consecutivePressureSamples = 0;
                    reason = $"Pressure criteria not met (Load {stats.LoadPercent}%, Avail {stats.AvailPhysGb:F1} GB)";
                    return false;
                }

                _consecutivePressureSamples++;
                if (_consecutivePressureSamples < 3)
                {
                    reason = $"Sustained pressure sample {_consecutivePressureSamples}/3";
                    return false;
                }

                int cooldownSec = GetCurrentAutoCooldownSeconds();
                double sinceAuto = (utcNow - _lastAutoCleanTime).TotalSeconds;
                if (sinceAuto < cooldownSec)
                {
                    reason = $"Cooldown active ({cooldownSec - sinceAuto:F0}s remaining)";
                    return false;
                }

                double sinceManual = (utcNow - _lastManualCleanTime).TotalSeconds;
                if (sinceManual < 60)
                {
                    reason = "Recent manual memory reduction";
                    return false;
                }

                reason = $"Sustained high memory pressure (Load {stats.LoadPercent}%, Avail {stats.AvailPhysGb:F2} GB)";
                return true;
            }
        }

        public static void RecordAutoCleanOutcome(MemoryCleanResult result, DateTime utcNow)
        {
            if (result.IsBusySkipped || result.WasCancelled)
                return;

            lock (_syncLock)
            {
                _lastAutoCleanTime = utcNow;
                _consecutivePressureSamples = 0;

                bool lowYield = result.WorkingSetTrimmedBytes < (64L * 1024 * 1024) &&
                                ((int)result.LoadBefore - (int)result.LoadAfter) < 2;
                if (lowYield)
                {
                    _autoCleanBackoffLevel = Math.Min(2, _autoCleanBackoffLevel + 1);
                }
                else
                {
                    _autoCleanBackoffLevel = 0;
                }
            }
        }

        public static MemoryStats GetMemoryStats() => GetMemoryStats(includeKernelLists: false);

        public static MemoryStats GetDetailedMemoryStats() => GetMemoryStats(includeKernelLists: true);

        public static MemoryStats GetMemoryStats(bool includeKernelLists)
        {
            if (!Platform.GetGlobalMemoryStatus(out ulong totalPhys, out ulong availPhys, out uint memoryLoad))
            {
                return default;
            }

            ulong usedBytes = totalPhys > availPhys ? totalPhys - availPhys : 0;
            var stats = new MemoryStats
            {
                LoadPercent = memoryLoad,
                TotalPhysGb = totalPhys / (1024.0 * 1024.0 * 1024.0),
                UsedPhysGb = usedBytes / (1024.0 * 1024.0 * 1024.0),
                AvailPhysGb = availPhys / (1024.0 * 1024.0 * 1024.0),
                TotalPhysBytes = (long)totalPhys,
                AvailPhysBytes = (long)availPhys,
                UsedPhysBytes = (long)usedBytes,
                PageSizeBytes = Environment.SystemPageSize
            };

            if (Platform.GetPerformanceInfo(out long sysCacheBytes, out long commitTotalBytes, out long commitLimitBytes, out long pageSizeBytes))
            {
                if (pageSizeBytes > 0) stats.PageSizeBytes = pageSizeBytes;
                stats.SystemCacheBytes = sysCacheBytes;
                stats.CommitTotalBytes = commitTotalBytes;
                stats.CommitLimitBytes = commitLimitBytes;
            }

            if (includeKernelLists && TryQueryMemoryListInfo(stats.PageSizeBytes, out long standbyBytes, out long modifiedBytes, out long freeBytes))
            {
                stats.HasKernelListMetrics = true;
                stats.StandbyBytes = standbyBytes;
                stats.ModifiedBytes = modifiedBytes;
                stats.FreePageBytes = freeBytes;
            }

            return stats;
        }

        private static bool TryQueryMemoryListInfo(long pageSize, out long standbyBytes, out long modifiedBytes, out long freeBytes)
        {
            standbyBytes = 0;
            modifiedBytes = 0;
            freeBytes = 0;

            if (GetTrueOsVersion().Major < 6)
                return false;

            lock (_syncLock)
            {
                if (_unsupportedInfoClasses.Contains(SystemMemoryListInformation))
                    return false;
            }

            // Query requires SeProfileSingleProcessPrivilege on modern Windows; try acquiring if already held in token
            if (!_hasProfilePrivilege)
            {
                TryAcquirePrivilege(SE_PROFILE_SINGLE_PROCESS_NAME, out _);
            }

            int status = Platform.QueryMemoryListInformation(pageSize, out standbyBytes, out modifiedBytes, out freeBytes);
            if (status == STATUS_INVALID_INFO_CLASS || status == STATUS_INFO_LENGTH_MISMATCH)
            {
                lock (_syncLock) { _unsupportedInfoClasses.Add(SystemMemoryListInformation); }
                return false;
            }

            return status >= 0;
        }

        public static bool TryAcquirePrivilege(string privilegeName, out int win32Error)
        {
            bool ok = Platform.TryAcquirePrivilege(privilegeName, out win32Error);
            if (ok)
            {
                if (privilegeName == SE_INCREASE_QUOTA_NAME) _hasQuotaPrivilege = true;
                else if (privilegeName == SE_PROFILE_SINGLE_PROCESS_NAME) _hasProfilePrivilege = true;
                else if (privilegeName == SE_DEBUG_NAME) _hasDebugPrivilege = true;
            }
            return ok;
        }

        public static bool EnsurePrivileges()
        {
            bool quota = _hasQuotaPrivilege || TryAcquirePrivilege(SE_INCREASE_QUOTA_NAME, out _);
            bool profile = _hasProfilePrivilege || TryAcquirePrivilege(SE_PROFILE_SINGLE_PROCESS_NAME, out _);
            return quota && profile;
        }

        public static bool IsProcessProtected(string? processName, int pid)
        {
            if (pid <= 4) return true;
            if (string.IsNullOrWhiteSpace(processName)) return false;
            string clean = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;
            return ProtectedSystemProcesses.Contains(clean);
        }

        public static bool IsProcessExcludedByUser(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            var list = KestrelSettings.Current?.MemReductExcludedProcesses;
            if (list == null || list.Count == 0) return false;

            string clean = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;

            for (int i = 0; i < list.Count; i++)
            {
                string item = list[i]?.Trim() ?? string.Empty;
                if (item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    item = item[..^4];
                if (string.Equals(item, clean, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static (int Pid, string Name) ResolveForegroundProcess(int explicitForegroundPid) =>
            Platform.ResolveForegroundProcess(explicitForegroundPid);

        private static MemoryOperationReport ExecuteMemoryListCommand(string operationName, MemoryListCommand command)
        {
            var os = GetTrueOsVersion();
            if (os.Major < 6)
            {
                return new MemoryOperationReport
                {
                    OperationName = operationName,
                    Attempted = false,
                    Succeeded = false,
                    StatusOrErrorCode = STATUS_INVALID_INFO_CLASS,
                    Message = $"Unsupported on Windows {os}"
                };
            }

            lock (_syncLock)
            {
                if (_unsupportedInfoClasses.Contains(SystemMemoryListInformation))
                {
                    return new MemoryOperationReport
                    {
                        OperationName = operationName,
                        Attempted = false,
                        Succeeded = false,
                        StatusOrErrorCode = STATUS_INVALID_INFO_CLASS,
                        Message = "Kernel memory list class unavailable on this Windows build"
                    };
                }
            }

            if (!TryAcquirePrivilege(SE_PROFILE_SINGLE_PROCESS_NAME, out int privErr) && !_hasProfilePrivilege)
            {
                return new MemoryOperationReport
                {
                    OperationName = operationName,
                    Attempted = false,
                    Succeeded = false,
                    StatusOrErrorCode = privErr,
                    Message = $"Requires {SE_PROFILE_SINGLE_PROCESS_NAME} (Win32 {privErr})"
                };
            }

            int status = Platform.SetSystemMemoryListCommand((int)command);
            if (status == STATUS_INVALID_INFO_CLASS || status == STATUS_INFO_LENGTH_MISMATCH)
            {
                lock (_syncLock) { _unsupportedInfoClasses.Add(SystemMemoryListInformation); }
            }

            return new MemoryOperationReport
            {
                OperationName = operationName,
                Attempted = true,
                Succeeded = status >= 0,
                StatusOrErrorCode = status,
                Message = status >= 0
                    ? "Succeeded"
                    : status switch
                    {
                        STATUS_PRIVILEGE_NOT_HELD => "Privilege not held (Admin required)",
                        STATUS_ACCESS_DENIED => "Access denied",
                        STATUS_INVALID_INFO_CLASS => "Unsupported kernel info class",
                        _ => $"NTSTATUS 0x{status:X8}"
                    }
            };
        }

        private static MemoryOperationReport FlushSystemFileCache()
        {
            if (!TryAcquirePrivilege(SE_INCREASE_QUOTA_NAME, out int privErr) && !_hasQuotaPrivilege)
            {
                return new MemoryOperationReport
                {
                    OperationName = "SystemFileCacheFlush",
                    Attempted = false,
                    Succeeded = false,
                    StatusOrErrorCode = privErr,
                    Message = $"Requires {SE_INCREASE_QUOTA_NAME} (Win32 {privErr})"
                };
            }

            bool skipNtClass;
            lock (_syncLock)
            {
                skipNtClass = _unsupportedInfoClasses.Contains(SystemFileCacheInformation);
            }

            if (!skipNtClass)
            {
                int status = Platform.SetSystemFileCacheNt();
                if (status >= 0)
                {
                    return new MemoryOperationReport
                    {
                        OperationName = "SystemFileCacheFlush",
                        Attempted = true,
                        Succeeded = true,
                        StatusOrErrorCode = status,
                        Message = "Flushed via NtSetSystemInformation"
                    };
                }

                if (status == STATUS_INVALID_INFO_CLASS || status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    lock (_syncLock) { _unsupportedInfoClasses.Add(SystemFileCacheInformation); }
                }
                else
                {
                    return new MemoryOperationReport
                    {
                        OperationName = "SystemFileCacheFlush",
                        Attempted = true,
                        Succeeded = false,
                        StatusOrErrorCode = status,
                        Message = $"NTSTATUS 0x{status:X8}"
                    };
                }
            }

            // Documented Win32 fallback: SetSystemFileCacheSize
            bool win32Ok = Platform.SetSystemFileCacheWin32(out int err);
            return new MemoryOperationReport
            {
                OperationName = "SystemFileCacheFlush",
                Attempted = true,
                Succeeded = win32Ok,
                StatusOrErrorCode = err,
                Message = win32Ok ? "Flushed via Win32 SetSystemFileCacheSize" : $"Win32 error {err}"
            };
        }

        private static MemoryOperationReport FlushRegistryCache()
        {
            var os = GetTrueOsVersion();
            if (os < new Version(6, 3))
            {
                return new MemoryOperationReport
                {
                    OperationName = "RegistryCacheFlush",
                    Attempted = false,
                    Succeeded = false,
                    StatusOrErrorCode = STATUS_INVALID_INFO_CLASS,
                    Message = "Requires Windows 8.1 or newer"
                };
            }

            lock (_syncLock)
            {
                if (_unsupportedInfoClasses.Contains(SystemRegistryReconciliationInformation))
                {
                    return new MemoryOperationReport
                    {
                        OperationName = "RegistryCacheFlush",
                        Attempted = false,
                        Succeeded = false,
                        StatusOrErrorCode = STATUS_INVALID_INFO_CLASS,
                        Message = "Unsupported on this Windows build"
                    };
                }
            }

            int status = Platform.SetRegistryReconciliation();
            if (status == STATUS_INVALID_INFO_CLASS || status == STATUS_INFO_LENGTH_MISMATCH)
            {
                lock (_syncLock) { _unsupportedInfoClasses.Add(SystemRegistryReconciliationInformation); }
            }

            return new MemoryOperationReport
            {
                OperationName = "RegistryCacheFlush",
                Attempted = true,
                Succeeded = status >= 0,
                StatusOrErrorCode = status,
                Message = status >= 0 ? "Succeeded" : $"NTSTATUS 0x{status:X8}"
            };
        }

        private static MemoryOperationReport CombineMemoryLists()
        {
            var os = GetTrueOsVersion();
            if (os < new Version(6, 2))
            {
                return new MemoryOperationReport
                {
                    OperationName = "MemoryCombine",
                    Attempted = false,
                    Succeeded = false,
                    StatusOrErrorCode = STATUS_INVALID_INFO_CLASS,
                    Message = "Requires Windows 8 or newer"
                };
            }

            lock (_syncLock)
            {
                if (_unsupportedInfoClasses.Contains(SystemCombinePhysicalMemoryInformation))
                {
                    return new MemoryOperationReport
                    {
                        OperationName = "MemoryCombine",
                        Attempted = false,
                        Succeeded = false,
                        StatusOrErrorCode = STATUS_INVALID_INFO_CLASS,
                        Message = "Unsupported on this Windows build"
                    };
                }
            }

            int status = Platform.SetCombineMemoryLists();
            if (status == STATUS_INVALID_INFO_CLASS || status == STATUS_INFO_LENGTH_MISMATCH)
            {
                lock (_syncLock) { _unsupportedInfoClasses.Add(SystemCombinePhysicalMemoryInformation); }
            }

            return new MemoryOperationReport
            {
                OperationName = "MemoryCombine",
                Attempted = true,
                Succeeded = status >= 0,
                StatusOrErrorCode = status,
                Message = status >= 0 ? "Succeeded" : $"NTSTATUS 0x{status:X8}"
            };
        }

        public static int SweepProcessWorkingSets()
        {
            var res = ReduceMemoryByProfile(
                MemoryCleanProfile.SmartReclaim,
                promptElevationIfRequired: false,
                explicitForegroundPid: 0,
                settleDelayMs: 0,
                cancellationToken: CancellationToken.None);
            return res.ProcessesCleaned;
        }

        private sealed class SweepMetrics
        {
            public int Scanned;
            public int Cleaned;
            public int SkippedProtected;
            public int SkippedExcluded;
            public int SkippedBelowThreshold;
            public int AccessDenied;
            public int Failed;
            public int LastError;
            public long WorkingSetBeforeBytes;
            public long WorkingSetAfterBytes;
            public long WorkingSetTrimmedBytes;
            public bool WasCancelled;
        }

        private static SweepMetrics SweepProcessWorkingSetsDetailed(
            bool protectForegroundAndSystem,
            bool respectUserExclusions,
            int minWorkingSetMb,
            int explicitForegroundPid,
            CancellationToken cancellationToken,
            string? targetProcessName = null,
            int? targetPid = null)
        {
            var metrics = new SweepMetrics();
            int currentId = Environment.ProcessId;
            var (fgPid, fgName) = protectForegroundAndSystem ? ResolveForegroundProcess(explicitForegroundPid) : (0, string.Empty);
            long minThresholdBytes = Math.Max(0, minWorkingSetMb) * 1024L * 1024L;

            IReadOnlyList<ProcessSnapshotEntry> processes = Platform.GetProcesses();
            bool applyMinThreshold = !targetPid.HasValue && string.IsNullOrWhiteSpace(targetProcessName) && minThresholdBytes > 0;

            for (int i = 0; i < processes.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    metrics.WasCancelled = true;
                    break;
                }

                var proc = processes[i];
                int pid = proc.Pid;
                string procName = proc.ProcessName;

                if (targetPid.HasValue && pid != targetPid.Value)
                    continue;

                if (!string.IsNullOrWhiteSpace(targetProcessName) &&
                    !string.Equals(procName, targetProcessName, StringComparison.OrdinalIgnoreCase))
                    continue;

                metrics.Scanned++;

                if (IsProcessProtected(procName, pid))
                {
                    metrics.SkippedProtected++;
                    continue;
                }

                if (protectForegroundAndSystem)
                {
                    bool isSelf = pid == currentId;
                    bool isForeground = (fgPid > 0 && pid == fgPid) ||
                                        (!string.IsNullOrEmpty(fgName) && string.Equals(procName, fgName, StringComparison.OrdinalIgnoreCase));
                    if (isSelf || isForeground)
                    {
                        metrics.SkippedProtected++;
                        continue;
                    }
                }

                if (respectUserExclusions && IsProcessExcludedByUser(procName))
                {
                    metrics.SkippedExcluded++;
                    continue;
                }

                var outcome = Platform.TrimProcessWorkingSet(pid, applyMinThreshold ? minThresholdBytes : 0);
                if (outcome.SkippedBelowThreshold)
                {
                    metrics.SkippedBelowThreshold++;
                    continue;
                }

                if (!outcome.Attempted)
                {
                    metrics.LastError = outcome.Win32Error;
                    if (outcome.Win32Error == ERROR_ACCESS_DENIED)
                        metrics.AccessDenied++;
                    else
                        metrics.Failed++;
                    continue;
                }

                metrics.WorkingSetBeforeBytes += outcome.BeforeBytes;
                if (outcome.Succeeded)
                {
                    metrics.Cleaned++;
                    metrics.WorkingSetAfterBytes += outcome.AfterBytes;
                    if (outcome.BeforeBytes > outcome.AfterBytes)
                    {
                        metrics.WorkingSetTrimmedBytes += (outcome.BeforeBytes - outcome.AfterBytes);
                    }
                }
                else
                {
                    metrics.LastError = outcome.Win32Error;
                    metrics.WorkingSetAfterBytes += outcome.BeforeBytes;
                    if (outcome.Win32Error == ERROR_ACCESS_DENIED)
                        metrics.AccessDenied++;
                    else
                        metrics.Failed++;
                }
            }

            return metrics;
        }

        public static List<ProcessGroupMemoryInfo> GetProcessGroupsSnapshot(string filter = "", int explicitForegroundPid = 0)
        {
            var (fgPid, fgName) = ResolveForegroundProcess(explicitForegroundPid);
            var map = new Dictionary<string, (int Count, int RepPid, long TotalWs, bool IsProt, bool IsExcl, bool IsFg)>(
                StringComparer.OrdinalIgnoreCase);

            var processes = Platform.GetProcesses();
            for (int i = 0; i < processes.Count; i++)
            {
                var proc = processes[i];
                int pid = proc.Pid;
                string name = proc.ProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                long ws = proc.WorkingSetBytes;
                bool isProt = IsProcessProtected(name, pid);
                bool isExcl = IsProcessExcludedByUser(name);
                bool isFg = pid == Environment.ProcessId ||
                            (fgPid > 0 && pid == fgPid) ||
                            (!string.IsNullOrEmpty(fgName) && string.Equals(name, fgName, StringComparison.OrdinalIgnoreCase));

                if (map.TryGetValue(name, out var existing))
                {
                    map[name] = (
                        existing.Count + 1,
                        existing.RepPid,
                        existing.TotalWs + ws,
                        existing.IsProt || isProt,
                        existing.IsExcl || isExcl,
                        existing.IsFg || isFg);
                }
                else
                {
                    map[name] = (1, pid, ws, isProt, isExcl, isFg);
                }
            }

            IEnumerable<ProcessGroupMemoryInfo> query = map.Select(kv => new ProcessGroupMemoryInfo(
                kv.Key,
                kv.Value.Count,
                kv.Value.RepPid,
                kv.Value.TotalWs,
                kv.Value.IsProt,
                kv.Value.IsExcl,
                kv.Value.IsFg));

            if (!string.IsNullOrWhiteSpace(filter))
            {
                query = query.Where(g => g.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }

            return query
                .OrderByDescending(g => g.TotalWorkingSetBytes)
                .ToList();
        }

        private static MemoryCleanResult BuildBusyResult(MemoryCleanProfile profile, MemoryStats before)
        {
            return new MemoryCleanResult
            {
                Profile = profile,
                LoadBefore = before.LoadPercent,
                LoadAfter = before.LoadPercent,
                TotalPhysGb = before.TotalPhysGb,
                UsedPhysGbBefore = before.UsedPhysGb,
                UsedPhysGbAfter = before.UsedPhysGb,
                AvailPhysGbBefore = before.AvailPhysGb,
                AvailPhysGbAfter = before.AvailPhysGb,
                AvailPhysBeforeBytes = before.AvailPhysBytes,
                AvailPhysAfterBytes = before.AvailPhysBytes,
                IsElevated = IsElevated,
                IsBusySkipped = true,
                StatusSummary = "Skipped: another memory operation is already in progress",
                Operations = new List<MemoryOperationReport>
                {
                    new()
                    {
                        OperationName = "SerializationGuard",
                        Attempted = false,
                        Succeeded = false,
                        StatusOrErrorCode = 0,
                        Message = "Another memory operation is already in progress"
                    }
                }
            };
        }

        public static MemoryCleanResult TrimProcessGroup(string processName)
        {
            if (Interlocked.CompareExchange(ref _activeCleanInFlight, 1, 0) != 0)
            {
                return BuildBusyResult(MemoryCleanProfile.SmartReclaim, GetMemoryStats());
            }

            try
            {
                var before = GetMemoryStats();
                if (IsProcessProtected(processName, 100))
                {
                    return new MemoryCleanResult
                    {
                        Profile = MemoryCleanProfile.SmartReclaim,
                        LoadBefore = before.LoadPercent,
                        LoadAfter = before.LoadPercent,
                        TotalPhysGb = before.TotalPhysGb,
                        UsedPhysGbBefore = before.UsedPhysGb,
                        UsedPhysGbAfter = before.UsedPhysGb,
                        AvailPhysGbBefore = before.AvailPhysGb,
                        AvailPhysGbAfter = before.AvailPhysGb,
                        AvailPhysBeforeBytes = before.AvailPhysBytes,
                        AvailPhysAfterBytes = before.AvailPhysBytes,
                        ProcessesSkippedProtected = 1,
                        IsElevated = IsElevated,
                        StatusSummary = $"Skipped protected Windows system process '{processName}'"
                    };
                }

                var sweep = SweepProcessWorkingSetsDetailed(
                    protectForegroundAndSystem: false,
                    respectUserExclusions: false,
                    minWorkingSetMb: 0,
                    explicitForegroundPid: 0,
                    CancellationToken.None,
                    targetProcessName: processName);

                Platform.Wait(120, CancellationToken.None);
                var after = GetMemoryStats();

                var res = BuildResult(MemoryCleanProfile.SmartReclaim, before, after, sweep, new List<MemoryOperationReport>
                {
                    new()
                    {
                        OperationName = $"TrimProcessGroup:{processName}",
                        Attempted = true,
                        Succeeded = sweep.Cleaned > 0,
                        StatusOrErrorCode = sweep.LastError,
                        Message = sweep.Cleaned > 0
                            ? $"Trimmed {sweep.WorkingSetTrimmedBytes / (1024.0 * 1024.0):N1} MB across {sweep.Cleaned} process(es)"
                            : (sweep.AccessDenied > 0 ? "Access denied for elevated/protected instances" : "No eligible process instances found")
                    }
                }, usedScheduledTask: false, elevationCancelled: false);

                if (sweep.Cleaned > 0 && (sweep.Failed > 0 || sweep.AccessDenied > 0))
                {
                    res.IsPartialSuccess = true;
                    double trimmedMb = sweep.WorkingSetTrimmedBytes / (1024.0 * 1024.0);
                    res.StatusSummary = $"Partial: trimmed {trimmedMb:N1} MB ({sweep.Cleaned} ok, {sweep.AccessDenied + sweep.Failed} failed/denied)";
                }

                return res;
            }
            finally
            {
                Interlocked.Exchange(ref _activeCleanInFlight, 0);
            }
        }

        public static MemoryCleanResult TrimSingleProcess(int pid)
        {
            if (Interlocked.CompareExchange(ref _activeCleanInFlight, 1, 0) != 0)
            {
                return BuildBusyResult(MemoryCleanProfile.SmartReclaim, GetMemoryStats());
            }

            try
            {
                var before = GetMemoryStats();
                if (!Platform.TryGetProcessName(pid, out string procName, out string errMsg, out int errCode))
                {
                    return new MemoryCleanResult
                    {
                        Profile = MemoryCleanProfile.SmartReclaim,
                        LoadBefore = before.LoadPercent,
                        LoadAfter = before.LoadPercent,
                        TotalPhysGb = before.TotalPhysGb,
                        UsedPhysGbBefore = before.UsedPhysGb,
                        UsedPhysGbAfter = before.UsedPhysGb,
                        AvailPhysGbBefore = before.AvailPhysGb,
                        AvailPhysGbAfter = before.AvailPhysGb,
                        AvailPhysBeforeBytes = before.AvailPhysBytes,
                        AvailPhysAfterBytes = before.AvailPhysBytes,
                        ProcessesFailed = 1,
                        LastWin32Error = errCode,
                        IsElevated = IsElevated,
                        StatusSummary = $"Process PID {pid} not accessible: {errMsg}"
                    };
                }

                if (IsProcessProtected(procName, pid))
                {
                    return new MemoryCleanResult
                    {
                        Profile = MemoryCleanProfile.SmartReclaim,
                        LoadBefore = before.LoadPercent,
                        LoadAfter = before.LoadPercent,
                        TotalPhysGb = before.TotalPhysGb,
                        UsedPhysGbBefore = before.UsedPhysGb,
                        UsedPhysGbAfter = before.UsedPhysGb,
                        AvailPhysGbBefore = before.AvailPhysGb,
                        AvailPhysGbAfter = before.AvailPhysGb,
                        AvailPhysBeforeBytes = before.AvailPhysBytes,
                        AvailPhysAfterBytes = before.AvailPhysBytes,
                        ProcessesSkippedProtected = 1,
                        IsElevated = IsElevated,
                        StatusSummary = $"Skipped protected Windows system process '{procName}' (PID {pid})"
                    };
                }

                var sweep = SweepProcessWorkingSetsDetailed(
                    protectForegroundAndSystem: false,
                    respectUserExclusions: false,
                    minWorkingSetMb: 0,
                    explicitForegroundPid: 0,
                    CancellationToken.None,
                    targetPid: pid);

                Platform.Wait(100, CancellationToken.None);
                var after = GetMemoryStats();

                return BuildResult(MemoryCleanProfile.SmartReclaim, before, after, sweep, new List<MemoryOperationReport>
                {
                    new()
                    {
                        OperationName = $"TrimSingleProcess:{pid}",
                        Attempted = true,
                        Succeeded = sweep.Cleaned > 0,
                        StatusOrErrorCode = sweep.LastError,
                        Message = sweep.Cleaned > 0
                            ? $"Trimmed {sweep.WorkingSetTrimmedBytes / (1024.0 * 1024.0):N1} MB"
                            : $"Failed or access denied (Win32 {sweep.LastError})"
                    }
                }, usedScheduledTask: false, elevationCancelled: false);
            }
            finally
            {
                Interlocked.Exchange(ref _activeCleanInFlight, 0);
            }
        }

        private static readonly SecurityIdentifier SidLocalSystem = new(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier SidBuiltinAdmins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier SidTrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

        private const FileSystemRights AtomicMutationRights =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.WriteAttributes |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership |
            (FileSystemRights)0x50000000; // GENERIC_WRITE | GENERIC_ALL (excludes read/execute bits)

        public static bool CanRegisterElevatedTask(string? exePath, out string reason)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath))
                {
                    reason = "Executable path is missing or not fully qualified.";
                    return false;
                }

                if (exePath.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    reason = "Refusing UNC or device paths for elevated execution.";
                    return false;
                }

                string fullExe = Path.GetFullPath(exePath);
                if (fullExe.StartsWith(@"\\", StringComparison.Ordinal) ||
                    fullExe.EndsWith(Path.DirectorySeparatorChar) ||
                    fullExe.EndsWith(Path.AltDirectorySeparatorChar))
                {
                    reason = "Invalid normalized executable path.";
                    return false;
                }

                string? dir = Path.GetDirectoryName(fullExe);
                if (string.IsNullOrEmpty(dir))
                {
                    reason = "Invalid executable directory.";
                    return false;
                }

                string dirNorm = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string tempDir = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrEmpty(tempDir) && IsPathEqualOrUnder(fullExe, tempDir))
                {
                    reason = "Refusing elevated registration for executable in temporary directory.";
                    return false;
                }

                string winTempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? @"C:\Windows",
                    "Temp").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrEmpty(winTempDir) && IsPathEqualOrUnder(fullExe, winTempDir))
                {
                    reason = "Refusing elevated registration for executable in Windows Temp directory.";
                    return false;
                }

                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userProfile))
                {
                    string userNorm = Path.GetFullPath(userProfile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (IsPathEqualOrUnder(fullExe, userNorm))
                    {
                        reason = "Refusing elevated registration for executable inside user profile.";
                        return false;
                    }
                }

                var trustedRoots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                }
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Where(r => !string.IsNullOrEmpty(r) && !r.StartsWith(@"\\", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(r => r.Length)
                .ToList();

                string? matchedRoot = trustedRoots.FirstOrDefault(root =>
                    dirNorm.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

                if (matchedRoot == null)
                {
                    reason = "Executable must be installed in a dedicated subdirectory under a protected administrator-owned root (such as Program Files).";
                    return false;
                }

                // Walk every ancestor directory from the deployment folder up to and including the matched trusted root.
                string current = dirNorm;
                while (true)
                {
                    if (!PathInspector.DirectoryExists(current))
                    {
                        reason = $"Directory does not exist or cannot be accessed: {current}";
                        return false;
                    }

                    if (!ValidatePathComponentSecurity(current, isDirectory: true, out reason))
                        return false;

                    if (string.Equals(current, matchedRoot, StringComparison.OrdinalIgnoreCase))
                        break;

                    string? parent = Path.GetDirectoryName(current)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.IsNullOrEmpty(parent) || parent.Length < matchedRoot.Length)
                    {
                        reason = $"Ancestor hierarchy failed boundary verification at {current}.";
                        return false;
                    }

                    current = parent;
                }

                if (!PathInspector.FileExists(fullExe))
                {
                    reason = "Executable file does not exist.";
                    return false;
                }

                if (!ValidatePathComponentSecurity(fullExe, isDirectory: false, out reason))
                    return false;

                foreach (string candidate in PathInspector.EnumerateFiles(dirNorm))
                {
                    if (string.IsNullOrEmpty(candidate) || string.Equals(candidate, fullExe, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!IsAdjacentLoadableFile(candidate))
                        continue;

                    if (!ValidatePathComponentSecurity(candidate, isDirectory: false, out reason))
                    {
                        reason = $"Adjacent loadable file failed protection check ({Path.GetFileName(candidate)}): {reason}";
                        return false;
                    }
                }

                reason = "Executable, ancestor hierarchy, deployment directory, and adjacent loadable files verified.";
                return true;
            }
            catch (Exception ex)
            {
                reason = $"Unable to verify protected path security (failing closed): {ex.Message}";
                return false;
            }
        }

        private static bool IsPathEqualOrUnder(string pathNorm, string rootNorm)
        {
            string cleanPath = pathNorm.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string cleanRoot = rootNorm.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(cleanPath, cleanRoot, StringComparison.OrdinalIgnoreCase) ||
                   cleanPath.StartsWith(cleanRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAdjacentLoadableFile(string path)
        {
            string fileName = Path.GetFileName(path);
            if (fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string ext = Path.GetExtension(fileName);
            return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".manifest", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".winmd", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ValidatePathComponentSecurity(string path, bool isDirectory, out string reason)
        {
            FileAttributes attrs = PathInspector.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0 || PathInspector.GetReparseTarget(path, isDirectory) != null)
            {
                reason = $"Reparse points, junctions, or symlinks are not permitted at '{path}'.";
                return false;
            }

            FileSystemSecurity sec = isDirectory
                ? PathInspector.GetDirectorySecurity(path)
                : PathInspector.GetFileSecurity(path);

            if (HasUnprivilegedWriteRule(sec))
            {
                reason = $"'{path}' grants ownership or write/ACL permissions to non-administrator principals.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool IsTrustedAdminPrincipal(SecurityIdentifier sid) =>
            sid.Equals(SidLocalSystem) ||
            sid.Equals(SidBuiltinAdmins) ||
            sid.Equals(SidTrustedInstaller);

        internal static bool HasUnprivilegedWriteRule(FileSystemSecurity security)
        {
            if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner &&
                !IsTrustedAdminPrincipal(owner))
            {
                return true;
            }

            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (AuthorizationRule rule in rules)
            {
                if (rule is not FileSystemAccessRule fsRule || fsRule.AccessControlType != AccessControlType.Allow)
                    continue;

                // Inherit-only ACEs (such as CREATOR OWNER on Program Files) do not grant write access to the directory itself.
                if ((fsRule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                    continue;

                if ((fsRule.FileSystemRights & AtomicMutationRights) == 0)
                    continue;

                if (fsRule.IdentityReference is not SecurityIdentifier sid || !IsTrustedAdminPrincipal(sid))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsAllowlistedWorkerArgument(string? arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return false;
            return arg.Equals(ArgCleanStandbyLow, StringComparison.OrdinalIgnoreCase) ||
                   arg.Equals(ArgCleanKernel, StringComparison.OrdinalIgnoreCase) ||
                   arg.Equals(ArgLegacyCleanAll, StringComparison.OrdinalIgnoreCase) ||
                   arg.Equals(ArgLegacyCleanStandby, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsValidCorrelationId(string? requestId)
        {
            if (string.IsNullOrEmpty(requestId) || requestId.Length != 32)
                return false;

            for (int i = 0; i < requestId.Length; i++)
            {
                char c = requestId[i];
                bool isHexDigit = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHexDigit)
                    return false;
            }
            return true;
        }

        internal static string GenerateCorrelationId()
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        }

        public static bool TryParseWorkerArguments(string[]? args, out string operation, out string requestId)
        {
            operation = string.Empty;
            requestId = string.Empty;

            if (args == null || args.Length == 0 || args.Length > 2)
                return false;

            if (!IsAllowlistedWorkerArgument(args[0]))
                return false;

            operation = NormalizeWorkerOperation(args[0]);

            if (args.Length == 2)
            {
                string second = args[1];
                if (!second.StartsWith(ArgRequestIdPrefix, StringComparison.Ordinal))
                    return false;

                string id = second[ArgRequestIdPrefix.Length..];
                if (!IsValidCorrelationId(id))
                    return false;

                requestId = id;
            }

            return true;
        }

        private static string NormalizeWorkerOperation(string? op)
        {
            if (string.IsNullOrWhiteSpace(op)) return string.Empty;
            if (op.Equals(ArgLegacyCleanStandby, StringComparison.OrdinalIgnoreCase) ||
                op.Equals(ArgCleanStandbyLow, StringComparison.OrdinalIgnoreCase))
            {
                return ArgCleanStandbyLow;
            }
            if (op.Equals(ArgLegacyCleanAll, StringComparison.OrdinalIgnoreCase) ||
                op.Equals(ArgCleanKernel, StringComparison.OrdinalIgnoreCase))
            {
                return ArgCleanKernel;
            }
            return op;
        }

        // Optional scheduled task route is explicitly disabled: Task Scheduler cannot carry unpredictable per-run correlation IDs safely.
        public static bool IsScheduledCleanTaskSupported => false;

        public static bool IsScheduledCleanTaskInstalled() => false;

        public static bool TryInstallElevatedCleanTask(out string message)
        {
            message = "Scheduled-task execution is disabled because Task Scheduler does not carry per-run correlation IDs; Kestrel uses on-demand UAC with strict per-run request correlation instead.";
            return false;
        }

        private static ElevatedWorkerLaunchResult LaunchElevatedWorkerDetailed(string argument, string requestId, CancellationToken cancellationToken)
        {
            if (!IsAllowlistedWorkerArgument(argument) || !IsValidCorrelationId(requestId))
                return new ElevatedWorkerLaunchResult(false, false, false, false, false, ERROR_ACCESS_DENIED);

            return Platform.LaunchElevatedWorker(NormalizeWorkerOperation(argument), requestId, cancellationToken);
        }

        public static bool LaunchElevatedWorker(string argument = ArgCleanStandbyLow)
        {
            string reqId = GenerateCorrelationId();
            var res = LaunchElevatedWorkerDetailed(argument, reqId, CancellationToken.None);
            TryDeleteFileQuietly(GetTelemetryFilePath(reqId));
            return res.Exited && res.ExitCode == 0;
        }

        public static int ExecuteHeadlessWorker(string[]? args)
        {
            if (!TryParseWorkerArguments(args, out string op, out string requestId))
                return 2;

            return ExecuteHeadlessWorker(op, requestId);
        }

        public static int ExecuteHeadlessWorker(string argument, string requestId = "")
        {
            if (!IsAllowlistedWorkerArgument(argument))
                return 2;

            if (!string.IsNullOrEmpty(requestId) && !IsValidCorrelationId(requestId))
                return 2;

            bool standbyOnly = argument.Equals(ArgCleanStandbyLow, StringComparison.OrdinalIgnoreCase) ||
                               argument.Equals(ArgLegacyCleanStandby, StringComparison.OrdinalIgnoreCase);

            return ExecuteHeadlessWorker(standbyOnly, requestId);
        }

        public static int ExecuteHeadlessWorker(bool purgeStandbyOnly, string requestId = "")
        {
            if (!string.IsNullOrEmpty(requestId) && !IsValidCorrelationId(requestId))
                return 2;

            var before = GetDetailedMemoryStats();
            var ops = new List<MemoryOperationReport>();
            bool standbyPurged = false;
            bool fileCacheFlushed = false;
            bool modifiedFlushed = false;
            long trimmedWs = 0;
            int procsCleaned = 0;

            EnsurePrivileges();

            if (purgeStandbyOnly)
            {
                var lowOp = ExecuteMemoryListCommand("LowPriorityStandbyPurge", MemoryListCommand.MemoryPurgeLowPriorityStandbyList);
                ops.Add(lowOp);
                standbyPurged = lowOp.Succeeded;
            }
            else
            {
                var sweep = SweepProcessWorkingSetsDetailed(
                    protectForegroundAndSystem: true,
                    respectUserExclusions: true,
                    minWorkingSetMb: KestrelSettings.Current?.SmartReclaimMinWorkingSetMb ?? 25,
                    explicitForegroundPid: 0,
                    CancellationToken.None);
                trimmedWs = sweep.WorkingSetTrimmedBytes;
                procsCleaned = sweep.Cleaned;
                bool sweepOk = sweep.Cleaned > 0 || (sweep.Failed == 0 && sweep.AccessDenied == 0);
                ops.Add(new MemoryOperationReport
                {
                    OperationName = "WorkingSetSweep",
                    Attempted = true,
                    Succeeded = sweepOk,
                    StatusOrErrorCode = sweep.LastError,
                    Message = $"Cleaned {sweep.Cleaned}/{sweep.Scanned} processes ({trimmedWs / (1024.0 * 1024.0):N1} MB)"
                });

                var fileOp = FlushSystemFileCache();
                ops.Add(fileOp);
                fileCacheFlushed = fileOp.Succeeded;

                var modOp = ExecuteMemoryListCommand("ModifiedListFlush", MemoryListCommand.MemoryFlushModifiedList);
                ops.Add(modOp);
                modifiedFlushed = modOp.Succeeded;

                var lowOp = ExecuteMemoryListCommand("LowPriorityStandbyPurge", MemoryListCommand.MemoryPurgeLowPriorityStandbyList);
                ops.Add(lowOp);
                var standbyOp = ExecuteMemoryListCommand("StandbyListPurge", MemoryListCommand.MemoryPurgeStandbyList);
                ops.Add(standbyOp);
                standbyPurged = lowOp.Succeeded || standbyOp.Succeeded;

                ops.Add(FlushRegistryCache());
                ops.Add(CombineMemoryLists());
            }

            Platform.Wait(150, CancellationToken.None);
            var after = GetDetailedMemoryStats();

            bool anyKernelSucceeded = purgeStandbyOnly
                ? standbyPurged
                : (standbyPurged || fileCacheFlushed || modifiedFlushed);
            bool allCoreSucceeded = purgeStandbyOnly
                ? standbyPurged
                : (standbyPurged && fileCacheFlushed && modifiedFlushed);
            bool partialSuccess = anyKernelSucceeded && !allCoreSucceeded;

            if (IsValidCorrelationId(requestId))
            {
                try
                {
                    if (IsReparsePointPath(HeadlessTelemetryDirectory))
                    {
                        return anyKernelSucceeded ? 0 : 1;
                    }

                    var telemetry = new HeadlessWorkerTelemetry
                    {
                        RequestId = requestId,
                        UtcTimestamp = DateTime.UtcNow,
                        Operation = purgeStandbyOnly ? ArgCleanStandbyLow : ArgCleanKernel,
                        StandbyBeforeBytes = before.StandbyBytes,
                        StandbyAfterBytes = after.StandbyBytes,
                        ModifiedBeforeBytes = before.ModifiedBytes,
                        ModifiedAfterBytes = after.ModifiedBytes,
                        SystemCacheBeforeBytes = before.SystemCacheBytes,
                        SystemCacheAfterBytes = after.SystemCacheBytes,
                        WorkingSetTrimmedBytes = trimmedWs,
                        ProcessesCleaned = procsCleaned,
                        StandbyPurged = standbyPurged,
                        FileCacheFlushed = fileCacheFlushed,
                        ModifiedListFlushed = modifiedFlushed,
                        AllSucceeded = allCoreSucceeded,
                        PartialSuccess = partialSuccess,
                        Operations = ops
                    };

                    if (!Directory.Exists(HeadlessTelemetryDirectory))
                        Directory.CreateDirectory(HeadlessTelemetryDirectory);

                    if (IsReparsePointPath(HeadlessTelemetryDirectory))
                    {
                        return anyKernelSucceeded ? 0 : 1;
                    }

                    string finalPath = GetTelemetryFilePath(requestId);
                    string tempPath = Path.Combine(HeadlessTelemetryDirectory, $".tmp-{requestId}-{Guid.NewGuid():N}.json");
                    try
                    {
                        if (IsReparsePointPath(finalPath))
                        {
                            File.Delete(finalPath);
                        }

                        using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        using (var writer = new StreamWriter(fs))
                        {
                            writer.Write(JsonSerializer.Serialize(telemetry));
                            writer.Flush();
                        }
                        File.Move(tempPath, finalPath, overwrite: true);
                    }
                    finally
                    {
                        TryDeleteFileQuietly(tempPath);
                    }
                }
                catch { }
            }

            return anyKernelSucceeded ? 0 : 1;
        }

        public static MemoryCleanResult ReduceMemoryByProfile(
            MemoryCleanProfile profile,
            bool promptElevationIfRequired = false,
            int explicitForegroundPid = 0,
            int settleDelayMs = 180,
            CancellationToken cancellationToken = default)
        {
            return profile switch
            {
                MemoryCleanProfile.SmartReclaim => ReduceMemory(
                    purgeStandby: false,
                    flushModified: false,
                    sweepProcesses: true,
                    promptElevationIfRequired: false,
                    profile: MemoryCleanProfile.SmartReclaim,
                    protectForegroundAndSystem: true,
                    respectUserExclusions: true,
                    explicitForegroundPid: explicitForegroundPid,
                    settleDelayMs: settleDelayMs,
                    cancellationToken: cancellationToken),

                MemoryCleanProfile.WorkingSetsAll => ReduceMemory(
                    purgeStandby: false,
                    flushModified: false,
                    sweepProcesses: true,
                    promptElevationIfRequired: false,
                    profile: MemoryCleanProfile.WorkingSetsAll,
                    protectForegroundAndSystem: false,
                    respectUserExclusions: true,
                    explicitForegroundPid: explicitForegroundPid,
                    settleDelayMs: settleDelayMs,
                    cancellationToken: cancellationToken),

                MemoryCleanProfile.LowPriorityStandbyOnly => ReduceMemory(
                    purgeStandby: true,
                    flushModified: false,
                    sweepProcesses: false,
                    promptElevationIfRequired: promptElevationIfRequired,
                    profile: MemoryCleanProfile.LowPriorityStandbyOnly,
                    protectForegroundAndSystem: true,
                    respectUserExclusions: true,
                    explicitForegroundPid: explicitForegroundPid,
                    settleDelayMs: settleDelayMs,
                    cancellationToken: cancellationToken),

                MemoryCleanProfile.FullKernelAndWorkingSets => ReduceMemory(
                    purgeStandby: true,
                    flushModified: true,
                    sweepProcesses: true,
                    promptElevationIfRequired: promptElevationIfRequired,
                    profile: MemoryCleanProfile.FullKernelAndWorkingSets,
                    protectForegroundAndSystem: true,
                    respectUserExclusions: true,
                    explicitForegroundPid: explicitForegroundPid,
                    settleDelayMs: settleDelayMs,
                    cancellationToken: cancellationToken),

                _ => ReduceMemory(
                    purgeStandby: false,
                    flushModified: false,
                    sweepProcesses: true,
                    promptElevationIfRequired: false)
            };
        }

        public static MemoryCleanResult ReduceMemory(
            bool purgeStandby = false,
            bool flushModified = false,
            bool sweepProcesses = true,
            bool promptElevationIfRequired = false,
            MemoryCleanProfile profile = MemoryCleanProfile.SmartReclaim,
            bool protectForegroundAndSystem = true,
            bool respectUserExclusions = true,
            int explicitForegroundPid = 0,
            int settleDelayMs = 180,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _activeCleanInFlight, 1, 0) != 0)
            {
                return BuildBusyResult(profile, GetMemoryStats());
            }

            try
            {
                var before = GetMemoryStats(includeKernelLists: purgeStandby || flushModified);
                var ops = new List<MemoryOperationReport>();
                bool elevated = IsElevated;
                bool usedScheduledTask = false;
                bool elevationCancelled = false;
                bool standbyPurged = false;
                bool fileCacheFlushed = false;
                bool modifiedListFlushed = false;
                bool workerTimedOut = false;
                bool telemetryUnavailable = false;
                bool tokenCancelledDuringWorker = false;

                lock (_syncLock)
                {
                    _lastManualCleanTime = DateTime.UtcNow;
                }

                SweepMetrics sweep = new();
                if (sweepProcesses && !cancellationToken.IsCancellationRequested)
                {
                    if (!protectForegroundAndSystem && elevated && !_hasDebugPrivilege)
                    {
                        TryAcquirePrivilege(SE_DEBUG_NAME, out _);
                    }

                    int minWsMb = protectForegroundAndSystem
                        ? (KestrelSettings.Current?.SmartReclaimMinWorkingSetMb ?? 25)
                        : 0;
                    sweep = SweepProcessWorkingSetsDetailed(
                        protectForegroundAndSystem,
                        respectUserExclusions,
                        minWsMb,
                        explicitForegroundPid,
                        cancellationToken);

                    bool sweepSucceeded = !sweep.WasCancelled && (sweep.Cleaned > 0 || (sweep.Failed == 0 && sweep.AccessDenied == 0));
                    ops.Add(new MemoryOperationReport
                    {
                        OperationName = protectForegroundAndSystem ? "SmartReclaimWorkingSets" : "WorkingSetsSweepAll",
                        Attempted = true,
                        Succeeded = sweepSucceeded,
                        StatusOrErrorCode = sweep.LastError,
                        Message = $"Cleaned {sweep.Cleaned}/{sweep.Scanned} processes, skipped {sweep.SkippedProtected} protected, {sweep.SkippedExcluded} excluded, {sweep.AccessDenied} access denied"
                    });
                }

                HeadlessWorkerTelemetry? workerTelemetry = null;
                if ((purgeStandby || flushModified) && !cancellationToken.IsCancellationRequested)
                {
                    if (elevated)
                    {
                        EnsurePrivileges();

                        if (flushModified)
                        {
                            var fileOp = FlushSystemFileCache();
                            ops.Add(fileOp);
                            fileCacheFlushed = fileOp.Succeeded;

                            var modOp = ExecuteMemoryListCommand("ModifiedListFlush", MemoryListCommand.MemoryFlushModifiedList);
                            ops.Add(modOp);
                            modifiedListFlushed = modOp.Succeeded;
                        }

                        if (purgeStandby)
                        {
                            var lowOp = ExecuteMemoryListCommand("LowPriorityStandbyPurge", MemoryListCommand.MemoryPurgeLowPriorityStandbyList);
                            ops.Add(lowOp);
                            standbyPurged = lowOp.Succeeded;

                            if (profile == MemoryCleanProfile.FullKernelAndWorkingSets)
                            {
                                var fullStandbyOp = ExecuteMemoryListCommand("StandbyListPurge", MemoryListCommand.MemoryPurgeStandbyList);
                                ops.Add(fullStandbyOp);
                                standbyPurged = standbyPurged || fullStandbyOp.Succeeded;

                                ops.Add(FlushRegistryCache());
                                ops.Add(CombineMemoryLists());
                            }
                        }
                    }
                    else
                    {
                        string workerArg = profile == MemoryCleanProfile.LowPriorityStandbyOnly || (purgeStandby && !flushModified)
                            ? ArgCleanStandbyLow
                            : ArgCleanKernel;

                        if (promptElevationIfRequired)
                        {
                            string requestId = GenerateCorrelationId();
                            string telemetryPath = GetTelemetryFilePath(requestId);
                            TryDeleteFileQuietly(telemetryPath);

                            DateTime workerLaunchUtc = DateTime.UtcNow;
                            var workerRes = LaunchElevatedWorkerDetailed(workerArg, requestId, cancellationToken);
                            DateTime workerEndUtc = DateTime.UtcNow;

                            elevationCancelled = workerRes.CancelledByUser;

                            if (workerRes.CancelledByUser)
                            {
                                TryDeleteFileQuietly(telemetryPath);
                                ops.Add(new MemoryOperationReport
                                {
                                    OperationName = "ElevatedKernelPurge",
                                    Attempted = true,
                                    Succeeded = false,
                                    StatusOrErrorCode = ERROR_CANCELLED,
                                    Message = "Administrator permission was declined (UAC cancelled)"
                                });
                            }
                            else if (workerRes.CancelledByToken)
                            {
                                tokenCancelledDuringWorker = true;
                                TryDeleteFileQuietly(telemetryPath);
                                ops.Add(new MemoryOperationReport
                                {
                                    OperationName = "ElevatedKernelPurge",
                                    Attempted = true,
                                    Succeeded = false,
                                    StatusOrErrorCode = ERROR_CANCELLED,
                                    Message = "Elevated kernel purge cancelled before completion"
                                });
                            }
                            else if (workerRes.TimedOut)
                            {
                                workerTimedOut = true;
                                telemetryUnavailable = true;
                                TryDeleteFileQuietly(telemetryPath);
                                ops.Add(new MemoryOperationReport
                                {
                                    OperationName = "ElevatedWorkerTimeout",
                                    Attempted = true,
                                    Succeeded = false,
                                    StatusOrErrorCode = -1,
                                    Message = "Elevated worker timed out before publishing verified telemetry"
                                });
                            }
                            else if (!workerRes.Started)
                            {
                                TryDeleteFileQuietly(telemetryPath);
                                ops.Add(new MemoryOperationReport
                                {
                                    OperationName = "ElevatedKernelPurge",
                                    Attempted = true,
                                    Succeeded = false,
                                    StatusOrErrorCode = workerRes.ExitCode,
                                    Message = $"Elevated worker failed to launch (code {workerRes.ExitCode})"
                                });
                            }
                            else
                            {
                                workerTelemetry = ConsumeCorrelatedWorkerTelemetry(
                                    requestId,
                                    workerArg,
                                    workerLaunchUtc,
                                    workerEndUtc,
                                    out string rejectReason);

                                if (workerTelemetry != null)
                                {
                                    standbyPurged = workerTelemetry.StandbyPurged;
                                    fileCacheFlushed = workerTelemetry.FileCacheFlushed;
                                    modifiedListFlushed = workerTelemetry.ModifiedListFlushed;
                                    if (workerTelemetry.Operations != null && workerTelemetry.Operations.Count > 0)
                                    {
                                        ops.AddRange(workerTelemetry.Operations);
                                    }

                                    if (workerRes.ExitCode != 0 && !standbyPurged && !fileCacheFlushed && !modifiedListFlushed)
                                    {
                                        ops.Add(new MemoryOperationReport
                                        {
                                            OperationName = "ElevatedKernelPurge",
                                            Attempted = true,
                                            Succeeded = false,
                                            StatusOrErrorCode = workerRes.ExitCode,
                                            Message = $"Elevated worker reported kernel failure (exit code {workerRes.ExitCode})"
                                        });
                                    }
                                }
                                else
                                {
                                    telemetryUnavailable = true;
                                    ops.Add(new MemoryOperationReport
                                    {
                                        OperationName = "ElevatedWorkerTelemetry",
                                        Attempted = true,
                                        Succeeded = false,
                                        StatusOrErrorCode = workerRes.ExitCode,
                                        Message = workerRes.ExitCode == 0
                                            ? $"Elevated worker exited with code 0 without valid correlated telemetry ({rejectReason})"
                                            : $"Elevated worker failed with exit code {workerRes.ExitCode} ({rejectReason})"
                                    });
                                }
                            }
                        }
                        else
                        {
                            ops.Add(new MemoryOperationReport
                            {
                                OperationName = "KernelPurge",
                                Attempted = false,
                                Succeeded = false,
                                StatusOrErrorCode = ERROR_ACCESS_DENIED,
                                Message = "Kernel cache purge skipped (elevation not requested)"
                            });
                        }
                    }
                }

                if (settleDelayMs > 0 && !cancellationToken.IsCancellationRequested)
                {
                    Platform.Wait(settleDelayMs, cancellationToken);
                }

                var after = GetMemoryStats(includeKernelLists: purgeStandby || flushModified);
                var result = BuildResult(profile, before, after, sweep, ops, usedScheduledTask, elevationCancelled);

                if (workerTelemetry != null && result.StandbyBeforeBytes == 0 && workerTelemetry.StandbyBeforeBytes > 0)
                {
                    result.StandbyBeforeBytes = workerTelemetry.StandbyBeforeBytes;
                    result.StandbyAfterBytes = workerTelemetry.StandbyAfterBytes;
                    result.ModifiedBeforeBytes = workerTelemetry.ModifiedBeforeBytes;
                    result.ModifiedAfterBytes = workerTelemetry.ModifiedAfterBytes;
                }

                result.StandbyPurged = standbyPurged;
                result.FileCacheFlushed = fileCacheFlushed;
                result.ModifiedListFlushed = modifiedListFlushed;
                result.WorkerTimedOut = workerTimedOut;
                result.TelemetryUnavailable = telemetryUnavailable;
                result.WasCancelled = sweep.WasCancelled || tokenCancelledDuringWorker || cancellationToken.IsCancellationRequested;

                static bool IsOptionalKernelOp(string name) =>
                    string.Equals(name, "MemoryCombine", StringComparison.Ordinal) ||
                    string.Equals(name, "RegistryReconciliation", StringComparison.Ordinal);

                var primaryAttempted = ops.Where(o => o.Attempted && !IsOptionalKernelOp(o.OperationName)).ToList();
                bool hasPrimarySuccess = standbyPurged || fileCacheFlushed || modifiedListFlushed || sweep.Cleaned > 0 || primaryAttempted.Any(o => o.Succeeded);
                bool hasPrimaryFailure = workerTimedOut || telemetryUnavailable || elevationCancelled || primaryAttempted.Any(o => !o.Succeeded);
                result.IsPartialSuccess = !result.WasCancelled && hasPrimarySuccess && hasPrimaryFailure;
                result.StatusSummary = result.FormatSummary();
                return result;
            }
            finally
            {
                Interlocked.Exchange(ref _activeCleanInFlight, 0);
            }
        }

        private static void TryDeleteFileQuietly(string? path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        internal static HeadlessWorkerTelemetry? ConsumeCorrelatedWorkerTelemetry(
            string requestId,
            string expectedOperation,
            DateTime launchStartUtc,
            DateTime launchEndUtc,
            out string rejectReason)
        {
            rejectReason = string.Empty;
            if (!IsValidCorrelationId(requestId))
            {
                rejectReason = "invalid request ID";
                return null;
            }

            string path = GetTelemetryFilePath(requestId);
            try
            {
                if (IsReparsePointPath(HeadlessTelemetryDirectory))
                {
                    rejectReason = "telemetry directory is a reparse point";
                    return null;
                }

                if (!File.Exists(path))
                {
                    rejectReason = "telemetry file missing";
                    return null;
                }

                if (IsReparsePointPath(path))
                {
                    rejectReason = "telemetry file is a reparse point";
                    return null;
                }

                string json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<HeadlessWorkerTelemetry>(json);
                if (data == null)
                {
                    rejectReason = "malformed telemetry JSON";
                    return null;
                }

                if (!IsValidCorrelationId(data.RequestId) ||
                    !string.Equals(data.RequestId, requestId, StringComparison.Ordinal))
                {
                    rejectReason = "request ID mismatch";
                    return null;
                }

                string expectedNorm = NormalizeWorkerOperation(expectedOperation);
                string actualNorm = NormalizeWorkerOperation(data.Operation);
                if (string.IsNullOrEmpty(actualNorm) ||
                    !string.Equals(actualNorm, expectedNorm, StringComparison.OrdinalIgnoreCase))
                {
                    rejectReason = "operation mismatch";
                    return null;
                }

                if (data.UtcTimestamp == default || data.UtcTimestamp.Kind == DateTimeKind.Unspecified)
                {
                    rejectReason = "missing or unspecified UTC timestamp";
                    return null;
                }

                DateTime tsUtc = data.UtcTimestamp.ToUniversalTime();
                DateTime startUtc = launchStartUtc.ToUniversalTime();
                DateTime endUtc = launchEndUtc.ToUniversalTime();
                DateTime nowUtc = DateTime.UtcNow;

                if (tsUtc < startUtc)
                {
                    rejectReason = "timestamp predates worker launch";
                    return null;
                }

                if (tsUtc > endUtc || tsUtc > nowUtc)
                {
                    rejectReason = "future-dated telemetry timestamp";
                    return null;
                }

                if ((nowUtc - tsUtc).TotalSeconds > 30)
                {
                    rejectReason = "stale telemetry timestamp (>30s)";
                    return null;
                }

                return data;
            }
            catch (JsonException)
            {
                rejectReason = "malformed telemetry JSON";
                return null;
            }
            catch (Exception ex)
            {
                rejectReason = ex.Message;
                return null;
            }
            finally
            {
                TryDeleteFileQuietly(path);
            }
        }

        private static MemoryCleanResult BuildResult(
            MemoryCleanProfile profile,
            MemoryStats before,
            MemoryStats after,
            SweepMetrics sweep,
            List<MemoryOperationReport> ops,
            bool usedScheduledTask,
            bool elevationCancelled)
        {
            var res = new MemoryCleanResult
            {
                Profile = profile,
                LoadBefore = before.LoadPercent,
                LoadAfter = after.LoadPercent,
                TotalPhysGb = before.TotalPhysGb,
                UsedPhysGbBefore = before.UsedPhysGb,
                UsedPhysGbAfter = after.UsedPhysGb,
                AvailPhysGbBefore = before.AvailPhysGb,
                AvailPhysGbAfter = after.AvailPhysGb,

                WorkingSetBeforeBytes = sweep.WorkingSetBeforeBytes,
                WorkingSetAfterBytes = sweep.WorkingSetAfterBytes,
                WorkingSetTrimmedBytes = sweep.WorkingSetTrimmedBytes,
                ProcessesScanned = sweep.Scanned,
                ProcessesCleaned = sweep.Cleaned,
                ProcessesSkippedProtected = sweep.SkippedProtected,
                ProcessesSkippedExcluded = sweep.SkippedExcluded,
                ProcessesSkippedBelowThreshold = sweep.SkippedBelowThreshold,
                ProcessesAccessDenied = sweep.AccessDenied,
                ProcessesFailed = sweep.Failed,

                SystemCacheBeforeBytes = before.SystemCacheBytes,
                SystemCacheAfterBytes = after.SystemCacheBytes,
                StandbyBeforeBytes = before.StandbyBytes,
                StandbyAfterBytes = after.StandbyBytes,
                ModifiedBeforeBytes = before.ModifiedBytes,
                ModifiedAfterBytes = after.ModifiedBytes,

                AvailPhysBeforeBytes = before.AvailPhysBytes,
                AvailPhysAfterBytes = after.AvailPhysBytes,

                WorkingSetsSwept = sweep.Cleaned > 0,
                IsElevated = IsElevated,
                UsedScheduledTask = usedScheduledTask,
                WasCancelled = sweep.WasCancelled,
                ElevationCancelled = elevationCancelled,
                LastWin32Error = sweep.LastError,
                Operations = ops
            };
            res.StatusSummary = res.FormatSummary();
            return res;
        }

        public static Task<MemoryCleanResult> ReduceMemoryAsync(
            bool purgeStandby = false,
            bool flushModified = false,
            bool sweepProcesses = true,
            bool promptElevationIfRequired = false,
            MemoryCleanProfile profile = MemoryCleanProfile.SmartReclaim,
            int explicitForegroundPid = 0,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ReduceMemoryByProfile(
                profile,
                promptElevationIfRequired: promptElevationIfRequired,
                explicitForegroundPid: explicitForegroundPid,
                cancellationToken: cancellationToken));
        }

        public static List<MemoryReductCommandDescriptor> GetCommandDefinitions(MemoryStats mem, bool isElevated)
        {
            string ramSummary = $"RAM: {mem.LoadPercent}% ({mem.UsedPhysGb:F1}/{mem.TotalPhysGb:F1} GB) • Cache: {mem.SystemCacheMb:N0} MB";
            return new List<MemoryReductCommandDescriptor>
            {
                new(
                    Id: "cmd:memreduct_studio",
                    Name: "Memory Reduct Studio (Live RAM & Per-App Trimmer)",
                    Subtitle: $"{ramSummary} • Inspect app memory & reclaim safely",
                    Category: "System Tool",
                    ShortcutBadge: "Ctrl+M",
                    Keywords: "memreduct mem reduct memory claim reclaim free clear ram studio analyze monitor working set standby",
                    Profile: MemoryCleanProfile.SmartReclaim),

                new(
                    Id: "cmd:memreduct_clean",
                    Name: "Smart Reclaim Memory (Mem Reduct)",
                    Subtitle: $"{ramSummary} • Safe background working-set trim (protects foreground & OS)",
                    Category: "System Tool",
                    ShortcutBadge: "Smart RAM",
                    Keywords: "memreduct mem reduct memory claim reclaim free clear ram optimize smart working set",
                    Profile: MemoryCleanProfile.SmartReclaim),

                new(
                    Id: "cmd:memreduct_workingset",
                    Name: "Trim All User Working Sets",
                    Subtitle: "Mem Reduct — Trim working sets across all accessible user apps (zero prompt)",
                    Category: "System Tool",
                    ShortcutBadge: "Trim All",
                    Keywords: "memreduct trim working set all apps reclaim memory ram",
                    Profile: MemoryCleanProfile.WorkingSetsAll),

                new(
                    Id: "cmd:memreduct_standby",
                    Name: "Purge Low-Priority Standby Memory List (Opt-In)",
                    Subtitle: isElevated
                        ? $"Mem Reduct [Admin] — Flush low-priority standby cache ({mem.SystemCacheMb:N0} MB system cache)"
                        : "Mem Reduct — Flush low-priority standby cache (requires Admin UAC)",
                    Category: "System Tool",
                    ShortcutBadge: "Standby",
                    Keywords: "memreduct purge standby list cache ram memory kernel admin",
                    Profile: MemoryCleanProfile.LowPriorityStandbyOnly),

                new(
                    Id: "cmd:memreduct_kernel",
                    Name: "Deep Kernel Cache Purge (Admin Opt-In)",
                    Subtitle: isElevated
                        ? "Mem Reduct [Admin] — Flush System File Cache, Modified List & Standby Lists"
                        : "Mem Reduct — Full NT kernel cache & standby purge (requires Admin UAC)",
                    Category: "System Tool",
                    ShortcutBadge: "Kernel 🛡",
                    Keywords: "memreduct deep kernel cache purge standby modified system file cache admin",
                    Profile: MemoryCleanProfile.FullKernelAndWorkingSets)
            };
        }

        internal sealed class Win32MemoryReductPlatform : IMemoryReductPlatform
        {
            public bool IsElevated
            {
                get
                {
                    try
                    {
                        using var id = WindowsIdentity.GetCurrent();
                        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            public Version GetOsVersion()
            {
                try
                {
                    var info = new RTL_OSVERSIONINFOW
                    {
                        dwOSVersionInfoSize = (uint)Marshal.SizeOf<RTL_OSVERSIONINFOW>(),
                        szCSDVersion = string.Empty
                    };
                    if (RtlGetVersion(ref info) == STATUS_SUCCESS)
                    {
                        return new Version((int)info.dwMajorVersion, (int)info.dwMinorVersion, (int)info.dwBuildNumber);
                    }
                }
                catch { }
                return Environment.OSVersion.Version;
            }

            public bool GetGlobalMemoryStatus(out ulong totalPhys, out ulong availPhys, out uint memoryLoad)
            {
                var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (!GlobalMemoryStatusEx(ref mem))
                {
                    totalPhys = 0;
                    availPhys = 0;
                    memoryLoad = 0;
                    return false;
                }
                totalPhys = mem.ullTotalPhys;
                availPhys = mem.ullAvailPhys;
                memoryLoad = mem.dwMemoryLoad;
                return true;
            }

            public bool GetPerformanceInfo(out long systemCacheBytes, out long commitTotalBytes, out long commitLimitBytes, out long pageSizeBytes)
            {
                systemCacheBytes = 0;
                commitTotalBytes = 0;
                commitLimitBytes = 0;
                pageSizeBytes = Environment.SystemPageSize;
                try
                {
                    var perf = new PERFORMANCE_INFORMATION
                    {
                        cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>()
                    };
                    if (K32GetPerformanceInfo(out perf, perf.cb))
                    {
                        long pageSize = (long)perf.PageSize.ToUInt64();
                        if (pageSize <= 0) pageSize = Environment.SystemPageSize;
                        pageSizeBytes = pageSize;
                        systemCacheBytes = (long)perf.SystemCache.ToUInt64() * pageSize;
                        commitTotalBytes = (long)perf.CommitTotal.ToUInt64() * pageSize;
                        commitLimitBytes = (long)perf.CommitLimit.ToUInt64() * pageSize;
                        return true;
                    }
                }
                catch { }
                return false;
            }

            public int QueryMemoryListInformation(long pageSize, out long standbyBytes, out long modifiedBytes, out long freeBytes)
            {
                standbyBytes = 0;
                modifiedBytes = 0;
                freeBytes = 0;

                int size = Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>();
                IntPtr pBuf = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NtQuerySystemInformation(SystemMemoryListInformation, pBuf, size, out _);
                    if (status < 0) return status;

                    var listInfo = Marshal.PtrToStructure<SYSTEM_MEMORY_LIST_INFORMATION>(pBuf);
                    long standbyPages = 0;
                    if (listInfo.PageCountByPriority != null)
                    {
                        for (int i = 0; i < listInfo.PageCountByPriority.Length; i++)
                        {
                            standbyPages += (long)listInfo.PageCountByPriority[i].ToUInt64();
                        }
                    }

                    long modPages = (long)(listInfo.ModifiedPageCount.ToUInt64() + listInfo.ModifiedNoWritePageCount.ToUInt64());
                    long freePages = (long)(listInfo.FreePageCount.ToUInt64() + listInfo.ZeroPageCount.ToUInt64());

                    standbyBytes = standbyPages * pageSize;
                    modifiedBytes = modPages * pageSize;
                    freeBytes = freePages * pageSize;
                    return status;
                }
                catch
                {
                    return -1;
                }
                finally
                {
                    Marshal.FreeHGlobal(pBuf);
                }
            }

            public bool TryAcquirePrivilege(string privilegeName, out int win32Error)
            {
                win32Error = ERROR_SUCCESS;
                try
                {
                    if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var hToken))
                    {
                        win32Error = Marshal.GetLastWin32Error();
                        return false;
                    }

                    try
                    {
                        if (!LookupPrivilegeValue(null, privilegeName, out var luid))
                        {
                            win32Error = Marshal.GetLastWin32Error();
                            return false;
                        }

                        var tp = new TOKEN_PRIVILEGES
                        {
                            PrivilegeCount = 1,
                            Luid = luid,
                            Attributes = SE_PRIVILEGE_ENABLED
                        };

                        if (!AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                        {
                            win32Error = Marshal.GetLastWin32Error();
                            return false;
                        }

                        win32Error = Marshal.GetLastWin32Error();
                        return win32Error == ERROR_SUCCESS;
                    }
                    finally
                    {
                        CloseHandle(hToken);
                    }
                }
                catch
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return false;
                }
            }

            public int SetSystemMemoryListCommand(int command)
            {
                IntPtr pCmd = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    Marshal.WriteInt32(pCmd, command);
                    return NtSetSystemInformation(SystemMemoryListInformation, pCmd, sizeof(int));
                }
                catch
                {
                    return -1;
                }
                finally
                {
                    Marshal.FreeHGlobal(pCmd);
                }
            }

            public int SetSystemFileCacheNt()
            {
                var sfci = new SYSTEM_FILECACHE_INFORMATION
                {
                    MinimumWorkingSet = (IntPtr)(-1),
                    MaximumWorkingSet = (IntPtr)(-1)
                };

                int size = Marshal.SizeOf<SYSTEM_FILECACHE_INFORMATION>();
                IntPtr pBuf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(sfci, pBuf, false);
                    return NtSetSystemInformation(SystemFileCacheInformation, pBuf, size);
                }
                catch
                {
                    return -1;
                }
                finally
                {
                    Marshal.FreeHGlobal(pBuf);
                }
            }

            public bool SetSystemFileCacheWin32(out int win32Error)
            {
                bool ok = SetSystemFileCacheSize((IntPtr)(-1), (IntPtr)(-1), 0);
                win32Error = ok ? ERROR_SUCCESS : Marshal.GetLastWin32Error();
                return ok;
            }

            public int SetRegistryReconciliation()
            {
                try
                {
                    return NtSetSystemInformation(SystemRegistryReconciliationInformation, IntPtr.Zero, 0);
                }
                catch
                {
                    return -1;
                }
            }

            public int SetCombineMemoryLists()
            {
                try
                {
                    var combine = new MEMORY_COMBINE_INFORMATION_EX();
                    int size = Marshal.SizeOf<MEMORY_COMBINE_INFORMATION_EX>();
                    IntPtr pBuf = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(combine, pBuf, false);
                        return NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, pBuf, size);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pBuf);
                    }
                }
                catch
                {
                    return -1;
                }
            }

            public (int Pid, string Name) ResolveForegroundProcess(int explicitForegroundPid)
            {
                int fgPid = explicitForegroundPid;
                if (fgPid <= 0)
                {
                    try
                    {
                        IntPtr fgHwnd = GetForegroundWindow();
                        if (fgHwnd != IntPtr.Zero)
                        {
                            GetWindowThreadProcessId(fgHwnd, out uint p);
                            if (p > 0) fgPid = (int)p;
                        }
                    }
                    catch { }
                }

                if (fgPid <= 0) return (0, string.Empty);

                try
                {
                    using var p = Process.GetProcessById(fgPid);
                    return (fgPid, p.ProcessName);
                }
                catch
                {
                    return (fgPid, string.Empty);
                }
            }

            public IReadOnlyList<ProcessSnapshotEntry> GetProcesses()
            {
                var list = new List<ProcessSnapshotEntry>();
                try
                {
                    var procs = Process.GetProcesses();
                    for (int i = 0; i < procs.Length; i++)
                    {
                        var proc = procs[i];
                        try
                        {
                            long ws = 0;
                            try { ws = proc.WorkingSet64; } catch { }
                            list.Add(new ProcessSnapshotEntry(proc.Id, proc.ProcessName, ws));
                        }
                        catch { }
                        finally
                        {
                            proc.Dispose();
                        }
                    }
                }
                catch { }
                return list;
            }

            public bool TryGetProcessName(int pid, out string processName, out string errorMessage, out int errorCode)
            {
                processName = string.Empty;
                errorMessage = string.Empty;
                errorCode = 0;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    processName = p.ProcessName;
                    return true;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    errorCode = ex.HResult;
                    return false;
                }
            }

            public ProcessTrimOutcome TrimProcessWorkingSet(int pid, long minWorkingSetBytes)
            {
                IntPtr hProcess = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProcess == IntPtr.Zero)
                {
                    return new ProcessTrimOutcome(false, false, false, 0, 0, Marshal.GetLastWin32Error());
                }

                try
                {
                    uint pmcSize = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>();
                    long wsBefore = 0;
                    if (K32GetProcessMemoryInfo(hProcess, out var pmcBefore, pmcSize))
                    {
                        wsBefore = (long)pmcBefore.WorkingSetSize.ToUInt64();
                    }

                    if (minWorkingSetBytes > 0 && wsBefore > 0 && wsBefore < minWorkingSetBytes)
                    {
                        return new ProcessTrimOutcome(false, false, true, wsBefore, wsBefore, 0);
                    }

                    if (EmptyWorkingSet(hProcess))
                    {
                        long wsAfter = wsBefore;
                        if (K32GetProcessMemoryInfo(hProcess, out var pmcAfter, pmcSize))
                        {
                            wsAfter = (long)pmcAfter.WorkingSetSize.ToUInt64();
                        }
                        return new ProcessTrimOutcome(true, true, false, wsBefore, wsAfter, 0);
                    }

                    return new ProcessTrimOutcome(true, false, false, wsBefore, wsBefore, Marshal.GetLastWin32Error());
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }

            public bool IsScheduledCleanTaskInstalled() => false;

            public bool TryRunScheduledCleanTask() => false;

            public ElevatedWorkerLaunchResult LaunchElevatedWorker(string argument, string requestId, CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new ElevatedWorkerLaunchResult(false, false, false, false, true, -1);

                try
                {
                    string? exe = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                    {
                        exe = Process.GetCurrentProcess().MainModule?.FileName;
                    }
                    if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                    {
                        exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Kestrel.exe");
                    }
                    if (IsReparsePointPath(exe))
                    {
                        return new ElevatedWorkerLaunchResult(false, false, false, false, false, ERROR_ACCESS_DENIED);
                    }

                    string workerArgs = IsValidCorrelationId(requestId)
                        ? $"{argument} {ArgRequestIdPrefix}{requestId}"
                        : argument;

                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = workerArgs,
                        Verb = "runas",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc == null)
                        return new ElevatedWorkerLaunchResult(false, false, false, false, false, -1);

                    var sw = Stopwatch.StartNew();
                    while (!proc.HasExited)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return new ElevatedWorkerLaunchResult(true, false, false, false, true, -1);
                        }

                        if (sw.ElapsedMilliseconds >= 10000)
                        {
                            return new ElevatedWorkerLaunchResult(true, false, true, false, false, -1);
                        }

                        Thread.Sleep(50);
                    }

                    return new ElevatedWorkerLaunchResult(true, true, false, false, false, proc.ExitCode);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
                {
                    return new ElevatedWorkerLaunchResult(false, false, false, true, false, ERROR_CANCELLED);
                }
                catch (Win32Exception ex)
                {
                    return new ElevatedWorkerLaunchResult(false, false, false, false, false, ex.NativeErrorCode);
                }
                catch
                {
                    return new ElevatedWorkerLaunchResult(false, false, false, false, false, -1);
                }
            }

            public void Wait(int milliseconds, CancellationToken cancellationToken)
            {
                if (milliseconds <= 0 || cancellationToken.IsCancellationRequested) return;
                cancellationToken.WaitHandle.WaitOne(milliseconds);
            }
        }

        internal sealed class Win32PathSecurityInspector : IPathSecurityInspector
        {
            public bool FileExists(string path) => File.Exists(path);
            public bool DirectoryExists(string path) => Directory.Exists(path);
            public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

            public string? GetReparseTarget(string path, bool isDirectory)
            {
                FileSystemInfo? info = isDirectory
                    ? Directory.ResolveLinkTarget(path, returnFinalTarget: false)
                    : File.ResolveLinkTarget(path, returnFinalTarget: false);
                return info != null ? (info.LinkTarget ?? info.FullName) : null;
            }

            public FileSystemSecurity GetDirectorySecurity(string path) => new DirectoryInfo(path).GetAccessControl();
            public FileSystemSecurity GetFileSecurity(string path) => new FileInfo(path).GetAccessControl();
            public IEnumerable<string> EnumerateFiles(string directory) => Directory.EnumerateFiles(directory);
        }
    }
}
