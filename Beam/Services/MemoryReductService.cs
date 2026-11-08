using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        public int ProcessesCleaned { get; set; }
    }

    /// <summary>
    /// Low-Level Windows NT Memory Management & Reduction Engine.
    /// Inspired by henrypp/memreduct (https://github.com/henrypp/memreduct).
    /// </summary>
    public static class MemoryReductService
    {
        private const int SystemMemoryListInformation = 80;

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

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("ntdll.dll")]
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

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

        private static bool _privilegesAcquired = false;

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

        private static void EnsurePrivileges()
        {
            if (_privilegesAcquired) return;

            try
            {
                if (OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var hToken))
                {
                    try
                    {
                        EnablePrivilege(hToken, SE_INCREASE_QUOTA_NAME);
                        EnablePrivilege(hToken, SE_PROFILE_SINGLE_PROCESS_NAME);
                        _privilegesAcquired = true;
                    }
                    finally
                    {
                        CloseHandle(hToken);
                    }
                }
            }
            catch { }
        }

        private static void EnablePrivilege(IntPtr hToken, string privilege)
        {
            if (LookupPrivilegeValue(null, privilege, out var luid))
            {
                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                };
                AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
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

        public static MemoryCleanResult ReduceMemory(bool purgeStandby = true, bool flushModified = true, bool sweepProcesses = true)
        {
            var before = GetMemoryStats();
            int procsCleaned = 0;

            // 1. Flush managed .NET GC first
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
            }
            catch { }

            // 2. Empty System Working Sets via NT kernel
            ExecuteMemoryListCommand(MemoryListCommand.MemoryEmptyWorkingSets);

            // 3. Sweep all accessible user process working sets
            if (sweepProcesses)
            {
                try
                {
                    var processes = Process.GetProcesses();
                    foreach (var proc in processes)
                    {
                        try
                        {
                            if (!proc.HasExited && proc.Id > 4)
                            {
                                if (EmptyWorkingSet(proc.Handle))
                                {
                                    procsCleaned++;
                                }
                            }
                        }
                        catch { }
                        finally
                        {
                            proc.Dispose();
                        }
                    }
                }
                catch { }
            }

            // 4. Flush Modified Page List
            if (flushModified)
            {
                ExecuteMemoryListCommand(MemoryListCommand.MemoryFlushModifiedList);
            }

            // 5. Purge Windows Standby Lists (turning standby cached memory into free physical RAM)
            bool standbyPurged = false;
            if (purgeStandby)
            {
                bool r1 = ExecuteMemoryListCommand(MemoryListCommand.MemoryPurgeLowPriorityStandbyList);
                bool r2 = ExecuteMemoryListCommand(MemoryListCommand.MemoryPurgeStandbyList);
                standbyPurged = r1 || r2;
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
                ProcessesCleaned = procsCleaned
            };
        }

        public static Task<MemoryCleanResult> ReduceMemoryAsync(bool purgeStandby = true, bool flushModified = true, bool sweepProcesses = true)
        {
            return Task.Run(() => ReduceMemory(purgeStandby, flushModified, sweepProcesses));
        }
    }
}
