using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;

namespace Beam.Services
{
    public struct MemoryStats
    {
        public uint LoadPercent;
        public double TotalPhysGb;
        public double UsedPhysGb;
        public double AvailPhysGb;
        public long TotalPhysBytes;
        public long AvailPhysBytes;
        public long UsedPhysBytes;
    }

    public class MemoryCleanResult
    {
        public uint LoadBefore { get; set; }
        public uint LoadAfter { get; set; }
        public long FreedBytes { get; set; }
        public double FreedMb => Math.Max(0, FreedBytes / (1024.0 * 1024.0));
        public double TotalPhysGb { get; set; }
        public double UsedPhysGbBefore { get; set; }
        public double UsedPhysGbAfter { get; set; }
        public bool StandbyPurged { get; set; }
        public bool WorkingSetsSwept { get; set; }
        public bool FileCacheFlushed { get; set; }
        public bool ModifiedListFlushed { get; set; }
        public int ProcessesCleaned { get; set; }
        public bool IsElevated { get; set; }
    }

    /// <summary>
    /// Low-Level Windows NT Memory Management & Reduction Engine.
    /// Aligned with the official henrypp/memreduct architecture (https://github.com/henrypp/memreduct).
    /// Supports a Dual-Mode/Hybrid Execution Model:
    /// - Non-elevated: Least-privilege process working set sweep + .NET GC (instant, zero-prompt).
    /// - Elevated / Headless worker: Deep NT kernel cache flushing (Standby, Modified, System File Cache, Deduplication).
    /// </summary>
    public static class MemoryReductService
    {
        // NT Information Classes
        private const int SystemFileCacheInformation = 21;
        private const int SystemMemoryListInformation = 80;
        private const int SystemCombinePhysicalMemoryInformation = 130;
        private const int SystemRegistryReconciliationInformation = 134;

        // SYSTEM_MEMORY_LIST_COMMAND enum matching ntdll / ntexapi.h & henrypp/memreduct
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

        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("ntdll.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern int NtSetSystemInformation(
            int SystemInformationClass,
            IntPtr SystemInformation,
            int SystemInformationLength);

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

        private static bool _privilegesAcquired = false;
        private static readonly object _syncLock = new object();
        private static DateTime _lastCleanTime = DateTime.MinValue;
        public const int AutoReductCooldownSeconds = 30;

        public static bool IsElevated
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

        public static bool CanAutoClean
        {
            get
            {
                lock (_syncLock)
                {
                    return (DateTime.UtcNow - _lastCleanTime).TotalSeconds >= AutoReductCooldownSeconds;
                }
            }
        }

        public static MemoryStats GetMemoryStats()
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref mem))
            {
                return default;
            }

            ulong usedBytes = mem.ullTotalPhys > mem.ullAvailPhys ? mem.ullTotalPhys - mem.ullAvailPhys : 0;
            return new MemoryStats
            {
                LoadPercent = mem.dwMemoryLoad,
                TotalPhysGb = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0),
                UsedPhysGb = usedBytes / (1024.0 * 1024.0 * 1024.0),
                AvailPhysGb = mem.ullAvailPhys / (1024.0 * 1024.0 * 1024.0),
                TotalPhysBytes = (long)mem.ullTotalPhys,
                AvailPhysBytes = (long)mem.ullAvailPhys,
                UsedPhysBytes = (long)usedBytes
            };
        }

        public static bool EnsurePrivileges()
        {
            if (_privilegesAcquired) return true;

            try
            {
                if (OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var hToken))
                {
                    try
                    {
                        bool quotaOk = EnablePrivilege(hToken, SE_INCREASE_QUOTA_NAME);
                        bool profileOk = EnablePrivilege(hToken, SE_PROFILE_SINGLE_PROCESS_NAME);
                        _privilegesAcquired = quotaOk && profileOk;
                        return _privilegesAcquired;
                    }
                    finally
                    {
                        CloseHandle(hToken);
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool EnablePrivilege(IntPtr hToken, string privilege)
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid))
                return false;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };

            if (!AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            // In Win32, AdjustTokenPrivileges returns TRUE even if the privilege was not assigned.
            // We must verify GetLastWin32Error() == ERROR_SUCCESS (0).
            // Error 1300 (0x514) = ERROR_NOT_ALL_ASSIGNED.
            return Marshal.GetLastWin32Error() == 0;
        }

        private static bool ExecuteMemoryListCommand(MemoryListCommand command)
        {
            EnsurePrivileges();

            IntPtr pCmd = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(pCmd, (int)command);
                int status = NtSetSystemInformation(SystemMemoryListInformation, pCmd, sizeof(int));
                return status >= 0; // NT_SUCCESS
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(pCmd);
            }
        }

        private static bool FlushSystemFileCache()
        {
            EnsurePrivileges();

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
                int status = NtSetSystemInformation(SystemFileCacheInformation, pBuf, size);
                return status >= 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(pBuf);
            }
        }

        private static bool FlushRegistryCache()
        {
            try
            {
                int status = NtSetSystemInformation(SystemRegistryReconciliationInformation, IntPtr.Zero, 0);
                return status >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool CombineMemoryLists()
        {
            try
            {
                var combine = new MEMORY_COMBINE_INFORMATION_EX();
                int size = Marshal.SizeOf<MEMORY_COMBINE_INFORMATION_EX>();
                IntPtr pBuf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(combine, pBuf, false);
                    int status = NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, pBuf, size);
                    return status >= 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(pBuf);
                }
            }
            catch
            {
                return false;
            }
        }

        public static int SweepProcessWorkingSets()
        {
            int procsCleaned = 0;
            int currentId = Process.GetCurrentProcess().Id;

            // 1. Flush current process managed GC & local working set
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
                procsCleaned++;
            }
            catch { }

            // 2. Iterate all accessible user processes with least-privilege OpenProcess
            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                return procsCleaned;
            }

            foreach (var proc in processes)
            {
                try
                {
                    int pid = proc.Id;
                    if (pid <= 4 || pid == currentId) continue;

                    IntPtr hProcess = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (hProcess != IntPtr.Zero)
                    {
                        try
                        {
                            if (EmptyWorkingSet(hProcess))
                            {
                                procsCleaned++;
                            }
                        }
                        finally
                        {
                            CloseHandle(hProcess);
                        }
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }

            return procsCleaned;
        }

        public static bool LaunchElevatedWorker(string argument = "--clean-memory-standby")
        {
            try
            {
                string? exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Kestrel.exe");
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = argument,
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(7000);
                    return proc.ExitCode == 0;
                }
                return false;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED (The user dismissed/cancelled the UAC prompt)
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static int ExecuteHeadlessWorker(bool purgeStandbyOnly)
        {
            EnsurePrivileges();

            if (purgeStandbyOnly)
            {
                ExecuteMemoryListCommand(MemoryListCommand.MemoryPurgeLowPriorityStandbyList);
                ExecuteMemoryListCommand(MemoryListCommand.MemoryPurgeStandbyList);
            }
            else
            {
                ExecuteMemoryListCommand(MemoryListCommand.MemoryEmptyWorkingSets);
                FlushSystemFileCache();
                ExecuteMemoryListCommand(MemoryListCommand.MemoryFlushModifiedList);
                ExecuteMemoryListCommand(MemoryListCommand.MemoryPurgeLowPriorityStandbyList);
                ExecuteMemoryListCommand(MemoryListCommand.MemoryPurgeStandbyList);
                FlushRegistryCache();
                CombineMemoryLists();
                SweepProcessWorkingSets();
            }

            return 0;
        }

        public static MemoryCleanResult ReduceMemory(
            bool purgeStandby = true,
            bool flushModified = true,
            bool sweepProcesses = true,
            bool promptElevationIfRequired = false)
        {
            var before = GetMemoryStats();
            int procsCleaned = 0;
            bool standbyPurged = false;
            bool fileCacheFlushed = false;
            bool modifiedListFlushed = false;
            bool workingSetsKernelSwept = false;
            bool elevated = IsElevated;

            lock (_syncLock)
            {
                _lastCleanTime = DateTime.UtcNow;
            }

            // 1. Process Working Set Sweeping (least-privilege)
            if (sweepProcesses)
            {
                procsCleaned = SweepProcessWorkingSets();
            }

            // 2. Kernel Memory List Operations
            if (elevated)
            {
                EnsurePrivileges();

                // Empty System & Process Working Sets via NT kernel
                workingSetsKernelSwept = ExecuteMemoryListCommand(MemoryListCommand.MemoryEmptyWorkingSets);

                // Flush System File Cache
                fileCacheFlushed = FlushSystemFileCache();

                // Flush Modified Page List
                if (flushModified)
                {
                    modifiedListFlushed = ExecuteMemoryListCommand(MemoryListCommand.MemoryFlushModifiedList);
                }

                // Purge Windows Standby Lists
                if (purgeStandby)
                {
                    bool r1 = ExecuteMemoryListCommand(MemoryListCommand.MemoryPurgeLowPriorityStandbyList);
                    bool r2 = ExecuteMemoryListCommand(MemoryListCommand.MemoryPurgeStandbyList);
                    standbyPurged = r1 || r2;
                }

                // Registry Reconciliation & Memory Combining
                FlushRegistryCache();
                CombineMemoryLists();
            }
            else if ((purgeStandby || flushModified) && promptElevationIfRequired)
            {
                bool workerSuccess = LaunchElevatedWorker(purgeStandby && !flushModified ? "--clean-memory-standby" : "--clean-memory");
                if (workerSuccess)
                {
                    standbyPurged = purgeStandby;
                    modifiedListFlushed = flushModified;
                    workingSetsKernelSwept = true;
                    fileCacheFlushed = true;
                }
            }

            var after = GetMemoryStats();
            long freed = after.AvailPhysBytes - before.AvailPhysBytes;
            if (freed < 0) freed = before.UsedPhysBytes - after.UsedPhysBytes;

            return new MemoryCleanResult
            {
                LoadBefore = before.LoadPercent,
                LoadAfter = after.LoadPercent,
                FreedBytes = Math.Max(0, freed),
                TotalPhysGb = before.TotalPhysGb,
                UsedPhysGbBefore = before.UsedPhysGb,
                UsedPhysGbAfter = after.UsedPhysGb,
                StandbyPurged = standbyPurged,
                WorkingSetsSwept = workingSetsKernelSwept || procsCleaned > 0,
                FileCacheFlushed = fileCacheFlushed,
                ModifiedListFlushed = modifiedListFlushed,
                ProcessesCleaned = procsCleaned,
                IsElevated = elevated
            };
        }

        public static Task<MemoryCleanResult> ReduceMemoryAsync(
            bool purgeStandby = true,
            bool flushModified = true,
            bool sweepProcesses = true,
            bool promptElevationIfRequired = false)
        {
            return Task.Run(() => ReduceMemory(purgeStandby, flushModified, sweepProcesses, promptElevationIfRequired));
        }
    }
}
