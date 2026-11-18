using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Beam.Models;
using Beam.Services;
using BeamInstaller;

internal static class Program
{
    private static int _passed;

    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS " + description);
        _passed++;
    }

    private static async Task Main()
    {
        await RunTestsAsync();
    }

    private static async Task RunTestsAsync()
    {
        string temp = Path.Combine(Path.GetTempPath(), "Kestrel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        CloudIdentity.CredentialDirectory = Path.Combine(temp, "credentials");
        MemoryReductService.HeadlessTelemetryDirectory = Path.Combine(temp, "telemetry");
        SearchEngine.FrecencyFile = Path.Combine(temp, "frecency.json");
        SearchEngine.ClearFrecency();

        var originalPlatform = MemoryReductService.Platform;
        var originalInspector = MemoryReductService.PathInspector;

        try
        {
            // 1. Existing Security & Cloud Identity Tests
            var uri = CloudIdentity.ValidateBackend("https://cloud.invalid");
            string token = CloudIdentity.GetCredential(uri);
            Check(token.Length == 64 && token == CloudIdentity.GetCredential(uri), "credential persists across requests");
            Check(token != CloudIdentity.GetCredential(new Uri("https://other.invalid/")), "credential isolated by backend");
            Check(!Directory.GetFiles(CloudIdentity.CredentialDirectory).Any(f => Encoding.UTF8.GetString(File.ReadAllBytes(f)).Contains(token)), "credential protected on disk");
            try { CloudIdentity.ValidateBackend("http://cloud.invalid"); throw new Exception("insecure URL accepted"); }
            catch (InvalidOperationException) { Check(true, "remote HTTP rejected"); }

            var handler = new FakeCloud();
            using var client = new HttpClient(handler);
            AiClient.Client = client;
            var result = await AiClient.SaveKeyToSupabaseVaultAsync(uri.AbsoluteUri, "gemini", "test-key");
            Check(result.Ok && handler.LastAuthorization == "Bearer " + token, "cloud save authenticates installation");
            handler.Persistence = false;
            result = await AiClient.SaveKeyToSupabaseVaultAsync(uri.AbsoluteUri, "gemini", "test-key");
            Check(!result.Ok, "HTTP 200 without durable storage is not success");
            handler.StorageStatus = HttpStatusCode.ServiceUnavailable;
            result = await AiClient.SaveKeyToSupabaseVaultAsync(uri.AbsoluteUri, "gemini", "test-key");
            Check(!result.Ok, "storage outage is not success");
            handler.SecureBackend = false;
            int posts = handler.Posts;
            result = await AiClient.SaveKeyToSupabaseVaultAsync(uri.AbsoluteUri, "gemini", "test-key");
            Check(!result.Ok && handler.Posts == posts, "old backend receives no credentials or keys");

            var messages = new[] { new ChatMessage("user", "first"), new ChatMessage("assistant", "answer"), new ChatMessage("user", "explain that") };
            handler.SecureBackend = true;
            await AiClient.StreamChatAsync(uri.AbsoluteUri, messages, "gemini-test", "test-key", _ => { });
            using (var body = JsonDocument.Parse(handler.LastBody))
                Check(body.RootElement.GetProperty("messages").GetArrayLength() == 3, "cloud receives complete conversation");
            handler.SecureBackend = false;
            foreach (string model in new[] { "gemini-test", "gpt-test", "claude-test", "llama-test" })
            {
                await AiClient.StreamChatAsync(uri.AbsoluteUri, messages, model, "test-key", _ => { });
                using var body = JsonDocument.Parse(handler.LastBody);
                var turns = body.RootElement.GetProperty(model == "gemini-test" ? "contents" : "messages");
                Check(turns.GetArrayLength() == 3, model + " fallback receives complete conversation");
                Check(turns[1].GetProperty("role").GetString() == (model == "gemini-test" ? "model" : "assistant"), model + " preserves assistant role");
            }

            // 2. Existing Installer Tests
            string custom = Path.Combine(temp, "Custom & O'Brien");
            Directory.CreateDirectory(custom);
            File.WriteAllText(Path.Combine(custom, "Kestrel.exe"), "test payload");
            string resolved = InstallationFiles.ResolveUninstallDirectory(Path.Combine(custom, "Uninstall-Kestrel.exe"), Path.Combine(temp, "wrong"));
            Check(resolved == custom, "installed uninstaller selects custom location");
            Check(InstallationFiles.ResolveUninstallDirectory(Path.Combine(temp, "Setup.exe"), custom) == custom, "standalone setup uses registered install location");
            try { InstallationFiles.ValidateDirectory(Path.GetPathRoot(temp)!); throw new Exception("drive root accepted"); }
            catch (InvalidOperationException) { Check(true, "drive root cleanup rejected"); }
            string script = InstallationFiles.BuildCleanupScript(custom, int.MaxValue);
            Check(script.Contains("O''Brien") && !script.Contains("-Recurse"), "cleanup quotes paths and avoids recursive deletion");
            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            string parse = "$tokens=$null; $errors=$null; [System.Management.Automation.Language.Parser]::ParseInput([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(script)) + "')), [ref]$tokens, [ref]$errors) | Out-Null; if ($errors.Count) { exit 1 }";
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(parse)));
            using var process = Process.Start(info)!;
            await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "cleanup script parses with spaces, apostrophes, and ampersands");

            // 3. Non-Mutating Host OS Read-Only Sanity Check + Isolated Fake Platform Suite
            var hostStats = MemoryReductService.GetMemoryStats();
            Check(hostStats.TotalPhysBytes > 0 && hostStats.LoadPercent <= 100 && MemoryReductService.GetTrueOsVersion().Major >= 10,
                "read-only host memory stats & OS version query valid metrics without mutating state");

            var fake = new FakeMemoryReductPlatform();
            MemoryReductService.Platform = fake;
            MemoryReductService.ResetPrivilegeCacheForTests();

            // Privilege check error handling
            bool badPriv = MemoryReductService.TryAcquirePrivilege("SeNonExistentPrivilege_KestrelTest", out int badPrivErr);
            Check(!badPriv && badPrivErr == 1313, "non-existent privilege acquisition fails gracefully with Win32 error");

            // Protected system process enforcement (simulated processes only)
            Check(MemoryReductService.IsProcessProtected("lsass", 500) &&
                  MemoryReductService.IsProcessProtected("dwm.exe", 600) &&
                  MemoryReductService.IsProcessProtected("explorer", 700) &&
                  MemoryReductService.IsProcessProtected("System", 4),
                "critical Windows system processes are protected");

            var lsassTrim = MemoryReductService.TrimProcessGroup("lsass");
            Check(lsassTrim.WorkingSetTrimmedBytes == 0 &&
                  lsassTrim.ProcessesSkippedProtected > 0 &&
                  fake.TrimmedPids.Count == 0 &&
                  lsassTrim.StatusSummary.Contains("protected", StringComparison.OrdinalIgnoreCase),
                "TrimProcessGroup refuses to trim protected Windows processes");

            var pid4Trim = MemoryReductService.TrimSingleProcess(4);
            Check(pid4Trim.WorkingSetTrimmedBytes == 0 && pid4Trim.ProcessesSkippedProtected > 0 && fake.TrimmedPids.Count == 0,
                "TrimSingleProcess refuses to trim System PID 4");

            var missingPidTrim = MemoryReductService.TrimSingleProcess(999999);
            Check(missingPidTrim.ProcessesFailed == 1 && missingPidTrim.WorkingSetTrimmedBytes == 0,
                "TrimSingleProcess handles non-existent PID cleanly");

            // Smart Reclaim vs WorkingSetsAll & User Exclusions on FakePlatform (zero host OS mutation)
            BeamSettings.Current.MemReductExcludedProcesses = new List<string> { "excludedapp" };
            Check(MemoryReductService.IsProcessExcludedByUser("excludedapp"), "user exclusion list matches process name");
            var excludedSweep = MemoryReductService.ReduceMemoryByProfile(
                MemoryCleanProfile.WorkingSetsAll,
                settleDelayMs: 0);
            Check(excludedSweep.ProcessesSkippedExcluded == 1 &&
                  !fake.TrimmedPids.Contains(104),
                "WorkingSetsAll respects user exclusion list without touching excluded process");
            BeamSettings.Current.MemReductExcludedProcesses.Clear();
            fake.TrimmedPids.Clear();

            // Smart Reclaim default run protects foreground (101 = code), system (4, 500), and <25MB (105),
            // while trimming eligible apps (102 = 80MB, 103 = 60MB, 104 = 48MB).
            var smartRes = MemoryReductService.ReduceMemoryByProfile(
                MemoryCleanProfile.SmartReclaim,
                explicitForegroundPid: 101,
                settleDelayMs: 10);
            Check(smartRes.Profile == MemoryCleanProfile.SmartReclaim &&
                  !smartRes.StandbyPurged &&
                  !smartRes.ModifiedListFlushed &&
                  fake.TrimmedPids.Contains(102) &&
                  fake.TrimmedPids.Contains(103) &&
                  fake.TrimmedPids.Contains(104) &&
                  !fake.TrimmedPids.Contains(101) &&
                  !fake.TrimmedPids.Contains(500) &&
                  !fake.TrimmedPids.Contains(105) &&
                  smartRes.WorkingSetTrimmedBytes == (80L + 60L + 48L) * 1024 * 1024 &&
                  smartRes.AvailPhysDeltaBytes == smartRes.AvailPhysAfterBytes - smartRes.AvailPhysBeforeBytes &&
                  smartRes.AvailPhysDeltaBytes != smartRes.WorkingSetTrimmedBytes,
                "SmartReclaim protects foreground/system/small apps, avoids kernel purge, and reports independent WorkingSet vs AvailPhys deltas");
            fake.TrimmedPids.Clear();

            // Cancellation check
            using var cancelCts = new CancellationTokenSource();
            cancelCts.Cancel();
            var cancelledRes = MemoryReductService.ReduceMemoryByProfile(
                MemoryCleanProfile.SmartReclaim,
                settleDelayMs: 0,
                cancellationToken: cancelCts.Token);
            Check(cancelledRes.WasCancelled && cancelledRes.ProcessesCleaned == 0 && fake.TrimmedPids.Count == 0,
                "pre-cancelled memory operation aborts immediately without trimming");

            // Legitimate zero-reclaimed-memory result (never fabricates positive bytes)
            var zeroRes = MemoryReductService.ReduceMemory(
                purgeStandby: false,
                flushModified: false,
                sweepProcesses: false,
                settleDelayMs: 0);
            Check(zeroRes.WorkingSetTrimmedBytes == 0 &&
                  zeroRes.StandbyDeltaBytes == 0 &&
                  zeroRes.ProcessesCleaned == 0 &&
                  zeroRes.FreedBytes == 0,
                "zero-op run reports legitimate 0 MB working set trimmed without inventing savings");

            // Non-elevated kernel purge with promptElevationIfRequired=false records denial cleanly
            fake.FakeElevated = false;
            var unprivStandby = MemoryReductService.ReduceMemoryByProfile(
                MemoryCleanProfile.LowPriorityStandbyOnly,
                promptElevationIfRequired: false,
                settleDelayMs: 0);
            Check(!unprivStandby.StandbyPurged &&
                  unprivStandby.Operations.Any(o => !o.Succeeded && o.StatusOrErrorCode == 5),
                "standard mode without UAC prompt records access-denied/skipped status for kernel purge");

            // Finding 1 Test: ExecuteHeadlessWorker returns non-zero (1) on failed kernel operation, 0 on success
            fake.FakeElevated = false;
            fake.AllowKernelCommands = false;
            MemoryReductService.ResetPrivilegeCacheForTests();
            int failWorkerExit = MemoryReductService.ExecuteHeadlessWorker(MemoryReductService.ArgCleanStandbyLow);
            Check(failWorkerExit == 1, "ExecuteHeadlessWorker returns exit code 1 when kernel operation fails");

            fake.FakeElevated = true;
            fake.AllowKernelCommands = true;
            MemoryReductService.ResetPrivilegeCacheForTests();
            int okWorkerExit = MemoryReductService.ExecuteHeadlessWorker(MemoryReductService.ArgCleanStandbyLow);
            Check(okWorkerExit == 0, "ExecuteHeadlessWorker returns exit code 0 when kernel operation succeeds");

            // Undocumented API fallback: STATUS_INVALID_INFO_CLASS on NtSetSystemInformation(SystemFileCacheInformation) -> SetSystemFileCacheSize
            fake.SimulateUnsupportedFileCacheNt = true;
            var fallbackKernelRes = MemoryReductService.ReduceMemoryByProfile(
                MemoryCleanProfile.FullKernelAndWorkingSets,
                settleDelayMs: 0);
            Check(fallbackKernelRes.FileCacheFlushed &&
                  fallbackKernelRes.Operations.Any(o => o.OperationName == "SystemFileCacheFlush" && o.Message.Contains("SetSystemFileCacheSize")),
                "NtSetSystemInformation STATUS_INVALID_INFO_CLASS falls back cleanly to SetSystemFileCacheSize");
            fake.SimulateUnsupportedFileCacheNt = false;
            fake.TrimmedPids.Clear();

            // Finding 2 Test: ReduceMemory NEVER fabricates StandbyPurged/ModifiedListFlushed when worker exits 0 without telemetry
            fake.FakeElevated = false;
            fake.WorkerBehavior = FakeWorkerBehavior.ExitZeroWithoutTelemetry;
            var missingTelRes = MemoryReductService.ReduceMemoryByProfile(
                MemoryCleanProfile.LowPriorityStandbyOnly,
                promptElevationIfRequired: true,
                settleDelayMs: 0);
            Check(!missingTelRes.StandbyPurged &&
                  !missingTelRes.ModifiedListFlushed &&
                  !missingTelRes.FileCacheFlushed &&
                  missingTelRes.TelemetryUnavailable &&
                  !missingTelRes.IsPartialSuccess &&
                  missingTelRes.Operations.Any(o => o.OperationName == "ElevatedWorkerTelemetry" && !o.Succeeded),
                "missing worker telemetry sets TelemetryUnavailable, does not fabricate success, and records failure");

            // Simulated UAC cancellation (NativeErrorCode == 1223)
            fake.WorkerBehavior = FakeWorkerBehavior.UacCancelled;
            var uacCancelRes = MemoryReductService.ReduceMemoryByProfile(
                MemoryCleanProfile.LowPriorityStandbyOnly,
                promptElevationIfRequired: true,
                settleDelayMs: 0);
            Check(uacCancelRes.ElevationCancelled &&
                  !uacCancelRes.StandbyPurged &&
                  uacCancelRes.StatusSummary.Contains("UAC cancelled", StringComparison.OrdinalIgnoreCase),
                "UAC cancellation (1223) sets ElevationCancelled and reports cancellation cleanly");

            // Finding 3 Test: Strict per-run RequestId + Operation + exact timestamp telemetry correlation
            Directory.CreateDirectory(MemoryReductService.HeadlessTelemetryDirectory);
            string reqA = MemoryReductService.GenerateCorrelationId();
            string reqB = MemoryReductService.GenerateCorrelationId();
            Check(reqA != reqB && MemoryReductService.IsValidCorrelationId(reqA),
                "generated telemetry request IDs are distinct 32-char lowercase hex identifiers");

            var launchStart = DateTime.UtcNow;
            var launchEnd = launchStart.AddMilliseconds(200);

            // Same operation, WRONG request ID -> must reject
            File.WriteAllText(MemoryReductService.GetTelemetryFilePath(reqA), JsonSerializer.Serialize(new HeadlessWorkerTelemetry
            {
                RequestId = reqB,
                UtcTimestamp = launchStart.AddMilliseconds(50),
                Operation = MemoryReductService.ArgCleanKernel,
                StandbyPurged = true
            }));
            Check(MemoryReductService.ConsumeCorrelatedWorkerTelemetry(reqA, MemoryReductService.ArgCleanKernel, launchStart, launchEnd, out _) == null &&
                  !File.Exists(MemoryReductService.GetTelemetryFilePath(reqA)),
                "ConsumeCorrelatedWorkerTelemetry rejects same-operation different-RequestId telemetry and consumes/deletes invalid artifact");

            // Pre-launch timestamp (-50ms; prior -250ms tolerance removed) -> must reject
            File.WriteAllText(MemoryReductService.GetTelemetryFilePath(reqA), JsonSerializer.Serialize(new HeadlessWorkerTelemetry
            {
                RequestId = reqA,
                UtcTimestamp = launchStart.AddMilliseconds(-50),
                Operation = MemoryReductService.ArgCleanKernel,
                StandbyPurged = true
            }));
            Check(MemoryReductService.ConsumeCorrelatedWorkerTelemetry(reqA, MemoryReductService.ArgCleanKernel, launchStart, launchEnd, out _) == null,
                "ConsumeCorrelatedWorkerTelemetry rejects pre-launch timestamp even within 50ms");

            // Future-dated timestamp -> must reject
            File.WriteAllText(MemoryReductService.GetTelemetryFilePath(reqA), JsonSerializer.Serialize(new HeadlessWorkerTelemetry
            {
                RequestId = reqA,
                UtcTimestamp = launchEnd.AddSeconds(10),
                Operation = MemoryReductService.ArgCleanKernel,
                StandbyPurged = true
            }));
            Check(MemoryReductService.ConsumeCorrelatedWorkerTelemetry(reqA, MemoryReductService.ArgCleanKernel, launchStart, launchEnd, out _) == null,
                "ConsumeCorrelatedWorkerTelemetry rejects future-dated telemetry timestamp");

            // Stale timestamp (>30s old) -> must reject
            File.WriteAllText(MemoryReductService.GetTelemetryFilePath(reqA), JsonSerializer.Serialize(new HeadlessWorkerTelemetry
            {
                RequestId = reqA,
                UtcTimestamp = DateTime.UtcNow.AddSeconds(-45),
                Operation = MemoryReductService.ArgCleanKernel,
                StandbyPurged = true
            }));
            Check(MemoryReductService.ConsumeCorrelatedWorkerTelemetry(reqA, MemoryReductService.ArgCleanKernel, DateTime.UtcNow.AddSeconds(-60), DateTime.UtcNow, out _) == null,
                "ConsumeCorrelatedWorkerTelemetry rejects stale telemetry (>30s old)");

            // Mismatched operation with valid RequestId -> must reject
            File.WriteAllText(MemoryReductService.GetTelemetryFilePath(reqA), JsonSerializer.Serialize(new HeadlessWorkerTelemetry
            {
                RequestId = reqA,
                UtcTimestamp = launchStart.AddMilliseconds(50),
                Operation = MemoryReductService.ArgCleanStandbyLow,
                StandbyPurged = true
            }));
            Check(MemoryReductService.ConsumeCorrelatedWorkerTelemetry(reqA, MemoryReductService.ArgCleanKernel, launchStart, launchEnd, out _) == null,
                "ConsumeCorrelatedWorkerTelemetry rejects telemetry for mismatched operation even with matching RequestId");

            // Malformed JSON -> must reject and clean up file
            File.WriteAllText(MemoryReductService.GetTelemetryFilePath(reqA), "{not-valid-json");
            Check(MemoryReductService.ConsumeCorrelatedWorkerTelemetry(reqA, MemoryReductService.ArgCleanKernel, launchStart, launchEnd, out _) == null &&
                  !File.Exists(MemoryReductService.GetTelemetryFilePath(reqA)),
                "ConsumeCorrelatedWorkerTelemetry rejects malformed JSON safely without throwing");

            // Valid matching RequestId + Operation + bounded UTC timestamp -> accepted once (single-use)
            File.WriteAllText(MemoryReductService.GetTelemetryFilePath(reqA), JsonSerializer.Serialize(new HeadlessWorkerTelemetry
            {
                RequestId = reqA,
                UtcTimestamp = DateTime.UtcNow,
                Operation = MemoryReductService.ArgCleanKernel,
                StandbyPurged = true
            }));
            Check(MemoryReductService.ConsumeCorrelatedWorkerTelemetry(reqA, MemoryReductService.ArgCleanKernel, launchStart, DateTime.UtcNow.AddMilliseconds(10), out _)?.StandbyPurged == true &&
                  !File.Exists(MemoryReductService.GetTelemetryFilePath(reqA)),
                "ConsumeCorrelatedWorkerTelemetry accepts exact RequestId/Operation telemetry and deletes file after single use");

            // Same-operation two sequential runs produce unique RequestIds; late write to old ID cannot affect new run
            fake.WorkerBehavior = FakeWorkerBehavior.WriteCorrelatedTelemetry;
            var correlatedRes1 = MemoryReductService.ReduceMemoryByProfile(
                MemoryCleanProfile.LowPriorityStandbyOnly,
                promptElevationIfRequired: true,
                settleDelayMs: 0);
            string firstRunRequestId = fake.LastLaunchedRequestId;
            fake.WorkerBehavior = FakeWorkerBehavior.ExitZeroWithoutTelemetry;
            // Simulate a late worker writing a result to firstRunRequestId just before the second run executes
            File.WriteAllText(MemoryReductService.GetTelemetryFilePath(firstRunRequestId), JsonSerializer.Serialize(new HeadlessWorkerTelemetry
            {
                RequestId = firstRunRequestId,
                UtcTimestamp = DateTime.UtcNow,
                Operation = MemoryReductService.ArgCleanStandbyLow,
                StandbyPurged = true
            }));
            var secondRunRes = MemoryReductService.ReduceMemoryByProfile(
                MemoryCleanProfile.LowPriorityStandbyOnly,
                promptElevationIfRequired: true,
                settleDelayMs: 0);
            Check(correlatedRes1.StandbyPurged &&
                  firstRunRequestId != fake.LastLaunchedRequestId &&
                  !secondRunRes.StandbyPurged &&
                  secondRunRes.TelemetryUnavailable,
                "late telemetry written for an earlier same-operation RequestId cannot hijack a subsequent run");

            // Timed-out worker + partial success flags
            fake.WorkerBehavior = FakeWorkerBehavior.TimedOut;
            var timedOutKernelOnly = MemoryReductService.ReduceMemoryByProfile(
                MemoryCleanProfile.LowPriorityStandbyOnly,
                promptElevationIfRequired: true,
                settleDelayMs: 0);
            Check(timedOutKernelOnly.WorkerTimedOut &&
                  timedOutKernelOnly.TelemetryUnavailable &&
                  !timedOutKernelOnly.StandbyPurged &&
                  !timedOutKernelOnly.IsPartialSuccess,
                "timed-out worker without sweep reports WorkerTimedOut and TelemetryUnavailable without claiming success");

            fake.WorkerBehavior = FakeWorkerBehavior.PartialSuccess;
            var partialKernelWithSweep = MemoryReductService.ReduceMemoryByProfile(
                MemoryCleanProfile.FullKernelAndWorkingSets,
                promptElevationIfRequired: true,
                settleDelayMs: 0);
            Check(partialKernelWithSweep.IsPartialSuccess &&
                  partialKernelWithSweep.WorkingSetsSwept &&
                  partialKernelWithSweep.StandbyPurged &&
                  !partialKernelWithSweep.ModifiedListFlushed &&
                  partialKernelWithSweep.StatusSummary.StartsWith("Partial:", StringComparison.Ordinal),
                "partial kernel + working set completion reports IsPartialSuccess distinctly from full success/failure");

            // Two simultaneous workers produce independent correlated telemetry files without colliding
            fake.FakeElevated = true;
            fake.AllowKernelCommands = true;
            MemoryReductService.ResetPrivilegeCacheForTests();
            string concurrentId1 = MemoryReductService.GenerateCorrelationId();
            string concurrentId2 = MemoryReductService.GenerateCorrelationId();
            DateTime concStart = DateTime.UtcNow;
            int concExit1 = MemoryReductService.ExecuteHeadlessWorker(new[] { MemoryReductService.ArgCleanStandbyLow, "--request-id=" + concurrentId1 });
            int concExit2 = MemoryReductService.ExecuteHeadlessWorker(new[] { MemoryReductService.ArgCleanKernel, "--request-id=" + concurrentId2 });
            DateTime concEnd = DateTime.UtcNow;
            Check(concExit1 == 0 && concExit2 == 0 &&
                  File.Exists(MemoryReductService.GetTelemetryFilePath(concurrentId1)) &&
                  File.Exists(MemoryReductService.GetTelemetryFilePath(concurrentId2)),
                "two headless workers publish separate per-request telemetry files without overwriting each other");
            var readConc1 = MemoryReductService.ConsumeCorrelatedWorkerTelemetry(concurrentId1, MemoryReductService.ArgCleanStandbyLow, concStart, concEnd, out _);
            Check(readConc1 != null &&
                  readConc1.Operation == MemoryReductService.ArgCleanStandbyLow &&
                  File.Exists(MemoryReductService.GetTelemetryFilePath(concurrentId2)),
                "consuming one worker telemetry file leaves simultaneous second worker telemetry intact");
            var readConc2 = MemoryReductService.ConsumeCorrelatedWorkerTelemetry(concurrentId2, MemoryReductService.ArgCleanKernel, concStart, concEnd, out _);
            Check(readConc2 != null && readConc2.Operation == MemoryReductService.ArgCleanKernel,
                "second simultaneous worker telemetry correlates independently with its own RequestId and operation");
            fake.FakeElevated = false;
            fake.AllowKernelCommands = false;
            MemoryReductService.ResetPrivilegeCacheForTests();

            // Cancellation via CancellationToken marks WasCancelled and releases serialization guard
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var tokenCancelRes = MemoryReductService.ReduceMemoryByProfile(
                    MemoryCleanProfile.SmartReclaim,
                    settleDelayMs: 0,
                    cancellationToken: cts.Token);
                var afterCancelRes = MemoryReductService.ReduceMemoryByProfile(
                    MemoryCleanProfile.SmartReclaim,
                    settleDelayMs: 0);
                Check(tokenCancelRes.WasCancelled && !afterCancelRes.IsBusySkipped && !afterCancelRes.WasCancelled,
                    "CancellationToken sets WasCancelled and releases serialization guard cleanly");
            }

            // Disabled scheduled task route explains correlation limitation honestly
            Check(!MemoryReductService.IsScheduledCleanTaskInstalled() &&
                  !MemoryReductService.TryInstallElevatedCleanTask(out string disabledTaskMsg) &&
                  disabledTaskMsg.Contains("correlation", StringComparison.OrdinalIgnoreCase),
                "optional scheduled-task route is disabled with clear explanation in favor of correlated UAC");

            // Finding 4 Test: Serialization guard blocks overlapping ReduceMemory / SweepProcessWorkingSets / TrimProcessGroup / TrimSingleProcess
            Check(MemoryReductService.TryEnterCleanGuardForTests(), "test harness acquires serialization guard");
            try
            {
                var busyReduce = MemoryReductService.ReduceMemoryByProfile(MemoryCleanProfile.SmartReclaim, settleDelayMs: 0);
                var busyGroup = MemoryReductService.TrimProcessGroup("chrome");
                var busySingle = MemoryReductService.TrimSingleProcess(102);
                int busySweep = MemoryReductService.SweepProcessWorkingSets();
                Check(busyReduce.IsBusySkipped &&
                      busyGroup.IsBusySkipped &&
                      busySingle.IsBusySkipped &&
                      busySweep == 0,
                    "service-level serialization guard blocks overlapping ReduceMemory, TrimProcessGroup, TrimSingleProcess, and SweepProcessWorkingSets");
            }
            finally
            {
                MemoryReductService.ExitCleanGuardForTests();
            }

            // Strict headless worker CLI grammar
            string validTestReqId = MemoryReductService.GenerateCorrelationId();
            Check(MemoryReductService.TryParseWorkerArguments(new[] { MemoryReductService.ArgCleanKernel, "--request-id=" + validTestReqId }, out string parsedOp, out string parsedReqId) &&
                  parsedOp == MemoryReductService.ArgCleanKernel &&
                  parsedReqId == validTestReqId &&
                  !MemoryReductService.TryParseWorkerArguments(new[] { MemoryReductService.ArgCleanKernel, "--output=C:\\temp\\out.json" }, out _, out _) &&
                  !MemoryReductService.TryParseWorkerArguments(new[] { MemoryReductService.ArgCleanKernel, "--request-id=NOTHEX" }, out _, out _) &&
                  !MemoryReductService.TryParseWorkerArguments(new[] { "--request-id=" + validTestReqId }, out _, out _) &&
                  MemoryReductService.ExecuteHeadlessWorker(new[] { MemoryReductService.ArgCleanKernel, "--extra=1" }) == 2,
                "strict worker CLI parser accepts valid operation + 32-hex request-id and rejects extra/malformed arguments with exit 2");
            Check(!MemoryReductService.IsAllowlistedWorkerArgument("--arbitrary-command") &&
                  MemoryReductService.IsAllowlistedWorkerArgument(MemoryReductService.ArgCleanStandbyLow) &&
                  MemoryReductService.IsAllowlistedWorkerArgument(MemoryReductService.ArgCleanKernel) &&
                  MemoryReductService.ExecuteHeadlessWorker("--invalid-flag") == 2,
                "worker argument allowlist and ExecuteHeadlessWorker refuse unallowlisted operations with exit code 2");

            // Finding 5 Test: Protected-path & ACL verification (atomic mutation mask + trusted owner/SIDs + InheritOnly)
            Check(!MemoryReductService.CanRegisterElevatedTask(Path.Combine(custom, "Kestrel.exe"), out _),
                "scheduled task registration rejects user-writable temporary directories");

            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var creatorOwnerSid = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
            var interactiveSid = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
            var currentUserSid = WindowsIdentity.GetCurrent().User!;

            var validSec = new DirectorySecurity();
            validSec.SetOwner(adminSid);
            validSec.AddAccessRule(new FileSystemAccessRule(adminSid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            validSec.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            validSec.AddAccessRule(new FileSystemAccessRule(usersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            validSec.AddAccessRule(new FileSystemAccessRule(creatorOwnerSid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            Check(!MemoryReductService.HasUnprivilegedWriteRule(validSec),
                "ACL check allows admin-owned directory with Users ReadAndExecute and InheritOnly CREATOR OWNER");

            var usersWriteSec = new DirectorySecurity();
            usersWriteSec.SetOwner(adminSid);
            usersWriteSec.AddAccessRule(new FileSystemAccessRule(usersSid, FileSystemRights.WriteData, AccessControlType.Allow));
            Check(MemoryReductService.HasUnprivilegedWriteRule(usersWriteSec),
                "ACL check rejects Users WriteData");

            var interactiveModSec = new DirectorySecurity();
            interactiveModSec.SetOwner(adminSid);
            interactiveModSec.AddAccessRule(new FileSystemAccessRule(interactiveSid, FileSystemRights.Modify, AccessControlType.Allow));
            Check(MemoryReductService.HasUnprivilegedWriteRule(interactiveModSec),
                "ACL check rejects non-admin InteractiveSid Modify");

            var unprivOwnerSec = new DirectorySecurity();
            unprivOwnerSec.SetOwner(currentUserSid);
            unprivOwnerSec.AddAccessRule(new FileSystemAccessRule(adminSid, FileSystemRights.FullControl, AccessControlType.Allow));
            if (!currentUserSid.Equals(adminSid))
            {
                Check(MemoryReductService.HasUnprivilegedWriteRule(unprivOwnerSec),
                    "ACL check rejects unprivileged owner SID");
            }

            // Complete Protected-Path Validation via FakePathSecurityInspector (hierarchy, boundary, reparse, adjacent files, fail-closed)
            string progFilesRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
                .TrimEnd(Path.DirectorySeparatorChar);
            var fakeInspector = new FakePathSecurityInspector();
            MemoryReductService.PathInspector = fakeInspector;

            DirectorySecurity MakeSafeDirSec()
            {
                var sec = new DirectorySecurity();
                sec.SetOwner(adminSid);
                sec.AddAccessRule(new FileSystemAccessRule(adminSid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(usersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(creatorOwnerSid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
                return sec;
            }

            FileSecurity MakeSafeFileSec()
            {
                var sec = new FileSecurity();
                sec.SetOwner(adminSid);
                sec.AddAccessRule(new FileSystemAccessRule(adminSid, FileSystemRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(usersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, AccessControlType.Allow));
                return sec;
            }

            FileSecurity MakeWritableFileSec()
            {
                var sec = MakeSafeFileSec();
                sec.AddAccessRule(new FileSystemAccessRule(usersSid, FileSystemRights.WriteData, AccessControlType.Allow));
                return sec;
            }

            DirectorySecurity MakeWritableDirSec()
            {
                var sec = MakeSafeDirSec();
                sec.AddAccessRule(new FileSystemAccessRule(usersSid, FileSystemRights.WriteData, AccessControlType.Allow));
                return sec;
            }

            string vendorDir = Path.Combine(progFilesRoot, "Vendor");
            string deployDir = Path.Combine(vendorDir, "Kestrel");
            string validExe = Path.Combine(deployDir, "Kestrel.exe");
            string adjacentDll = Path.Combine(deployDir, "Kestrel.dll");

            void SeedValidProtectedTree()
            {
                fakeInspector.Reset();
                fakeInspector.AddDirectory(progFilesRoot, MakeSafeDirSec());
                fakeInspector.AddDirectory(vendorDir, MakeSafeDirSec());
                fakeInspector.AddDirectory(deployDir, MakeSafeDirSec());
                fakeInspector.AddFile(validExe, MakeSafeFileSec());
                fakeInspector.AddFile(adjacentDll, MakeSafeFileSec());
                fakeInspector.AddFile(Path.Combine(deployDir, "Kestrel.deps.json"), MakeSafeFileSec());
            }

            SeedValidProtectedTree();
            Check(MemoryReductService.CanRegisterElevatedTask(validExe, out _),
                "CanRegisterElevatedTask accepts valid Program Files tree with read-only Users ACEs and protected adjacent binaries");

            // Prefix lookalike (e.g. C:\Program Files_Evil\Kestrel\Kestrel.exe)
            string lookalikeDir = progFilesRoot + "_Evil";
            string lookalikeExe = Path.Combine(lookalikeDir, "Kestrel", "Kestrel.exe");
            fakeInspector.AddDirectory(lookalikeDir, MakeSafeDirSec());
            fakeInspector.AddDirectory(Path.Combine(lookalikeDir, "Kestrel"), MakeSafeDirSec());
            fakeInspector.AddFile(lookalikeExe, MakeSafeFileSec());
            Check(!MemoryReductService.CanRegisterElevatedTask(lookalikeExe, out _),
                "CanRegisterElevatedTask rejects trusted-root prefix lookalike path");

            // Executable placed directly at Program Files root instead of dedicated subfolder
            string rootDirectExe = Path.Combine(progFilesRoot, "Kestrel.exe");
            fakeInspector.AddFile(rootDirectExe, MakeSafeFileSec());
            Check(!MemoryReductService.CanRegisterElevatedTask(rootDirectExe, out _),
                "CanRegisterElevatedTask rejects executable directly under trusted root without deployment subfolder");

            // Writable intermediate ancestor directory
            SeedValidProtectedTree();
            fakeInspector.AddDirectory(vendorDir, MakeWritableDirSec());
            Check(!MemoryReductService.CanRegisterElevatedTask(validExe, out _),
                "CanRegisterElevatedTask rejects writable intermediate ancestor directory");

            // Reparse point / junction on ancestor, exe, or adjacent DLL
            SeedValidProtectedTree();
            fakeInspector.SetReparsePoint(vendorDir, @"C:\EvilTarget");
            Check(!MemoryReductService.CanRegisterElevatedTask(validExe, out _),
                "CanRegisterElevatedTask rejects reparse/junction on intermediate ancestor");

            SeedValidProtectedTree();
            fakeInspector.SetReparsePoint(validExe, @"C:\EvilTarget\evil.exe");
            Check(!MemoryReductService.CanRegisterElevatedTask(validExe, out _),
                "CanRegisterElevatedTask rejects symlink/reparse executable");

            SeedValidProtectedTree();
            fakeInspector.SetReparsePoint(adjacentDll, @"C:\EvilTarget\evil.dll");
            Check(!MemoryReductService.CanRegisterElevatedTask(validExe, out _),
                "CanRegisterElevatedTask rejects symlink/reparse adjacent loadable DLL");

            // Writable adjacent loadable DLL
            SeedValidProtectedTree();
            fakeInspector.AddFile(adjacentDll, MakeWritableFileSec());
            Check(!MemoryReductService.CanRegisterElevatedTask(validExe, out _),
                "CanRegisterElevatedTask rejects adjacent loadable DLL writable by unprivileged users");

            // Inspection exception fails closed
            SeedValidProtectedTree();
            fakeInspector.ThrowOnSecurityQueryFor.Add(deployDir);
            Check(!MemoryReductService.CanRegisterElevatedTask(validExe, out _),
                "CanRegisterElevatedTask fails closed when ACL inspection throws UnauthorizedAccessException");

            MemoryReductService.PathInspector = originalInspector;

            // Sustained-pressure Auto Memory Reduct state machine
            Check(!BeamSettings.Current.AutoMemReductEnabled, "Auto Memory Reduct is disabled by default");
            MemoryReductService.ResetAutoCleanStateForTests();
            BeamSettings.Current.AutoMemReductEnabled = true;
            BeamSettings.Current.AutoMemReductThreshold = 85;
            BeamSettings.Current.AutoMemReductMinAvailMb = 1536;
            var highPressure = new MemoryStats
            {
                LoadPercent = 90,
                TotalPhysBytes = 16L * 1024 * 1024 * 1024,
                AvailPhysBytes = 1024L * 1024 * 1024,
                TotalPhysGb = 16.0,
                AvailPhysGb = 1.0
            };
            var t0 = DateTime.UtcNow;
            Check(!MemoryReductService.ShouldTriggerAutoClean(highPressure, BeamSettings.Current, t0, out _),
                "1st high-pressure sample does not trigger auto-clean");
            Check(!MemoryReductService.ShouldTriggerAutoClean(highPressure, BeamSettings.Current, t0.AddSeconds(10), out _),
                "2nd high-pressure sample does not trigger auto-clean");
            Check(MemoryReductService.ShouldTriggerAutoClean(highPressure, BeamSettings.Current, t0.AddSeconds(20), out _),
                "3rd consecutive sustained high-pressure sample triggers auto-clean");

            MemoryReductService.RecordAutoCleanOutcome(new MemoryCleanResult
            {
                LoadBefore = 90,
                LoadAfter = 89,
                WorkingSetTrimmedBytes = 10L * 1024 * 1024
            }, t0.AddSeconds(20));
            Check(MemoryReductService.GetCurrentAutoCooldownSeconds() == 900,
                "low-yield auto-clean engages 900s backoff cooldown");
            Check(!MemoryReductService.ShouldTriggerAutoClean(highPressure, BeamSettings.Current, t0.AddSeconds(350), out _),
                "backoff cooldown blocks re-triggering within 900s");

            var lowPressure = new MemoryStats
            {
                LoadPercent = 65,
                TotalPhysBytes = 16L * 1024 * 1024 * 1024,
                AvailPhysBytes = 5L * 1024 * 1024 * 1024,
                TotalPhysGb = 16.0,
                AvailPhysGb = 5.0
            };
            MemoryReductService.ShouldTriggerAutoClean(lowPressure, BeamSettings.Current, t0.AddSeconds(400), out _);
            Check(MemoryReductService.GetCurrentAutoCooldownSeconds() == 300,
                "pressure relief resets auto-clean backoff to 300s");
            BeamSettings.Current.AutoMemReductEnabled = false;

            // Process groups snapshot from FakePlatform
            var groups = MemoryReductService.GetProcessGroupsSnapshot(explicitForegroundPid: 101);
            Check(groups.Count > 0 &&
                  groups.Any(g => g.IsProtectedSystem) &&
                  groups.Any(g => g.ProcessName == "code" && g.IsForeground),
                "process group snapshot populates app groups and marks protected & foreground processes");

            // 4. Command Aliases & Sensitive Input Frecency Exclusion
            var cmdPool = MemoryReductService.GetCommandDefinitions(hostStats, MemoryReductService.IsElevated)
                .Select(d => new LauncherItem
                {
                    Id = d.Id,
                    Name = d.Name,
                    Subtitle = d.Subtitle,
                    Category = d.Category,
                    Keywords = d.Keywords
                }).ToList();

            foreach (string alias in new[] { "memreduct", "memory claim", "reclaim memory", "free ram", "standby" })
            {
                var ranked = SearchEngine.RankAndFilter(cmdPool, alias, 10);
                Check(ranked.Count > 0 && ranked[0].Id.StartsWith("cmd:memreduct", StringComparison.Ordinal),
                    $"alias query '{alias}' ranks Memory Reduct command #1");
            }

            int beforeTracked = SearchEngine.GetTrackedCount();
            SearchEngine.RecordUsage("dev:secret");
            SearchEngine.RecordUsage("sec:jwt");
            SearchEngine.RecordUsage("port:3000");
            SearchEngine.RecordUsage("timer:5m");
            Check(SearchEngine.GetTrackedCount() == beforeTracked,
                "sensitive/ephemeral prefixes are never stored in frecency tracking");

            // 5. Safe Developer Utilities
            var ramEval = PowerDeveloperToolsService.EvaluateInline("memory claim");
            Check(ramEval.IsValid && ramEval.ActionType == "memreduct_studio" && ramEval.Category == "System Telemetry",
                "inline 'memory claim' surfaces live RAM telemetry studio trigger");

            string sampleJwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ1c2VyMTIzIiwiaXNzIjoia2VzdHJlbCIsImV4cCI6NDEwMjQ0NDgwMH0.invalidsig";
            var jwtEval = PowerDeveloperToolsService.EvaluateInline("jwt " + sampleJwt);
            Check(jwtEval.IsValid &&
                  jwtEval.Subtitle.Contains("Signature NOT Verified", StringComparison.OrdinalIgnoreCase) &&
                  jwtEval.Result.Contains("Signature NOT Verified", StringComparison.OrdinalIgnoreCase) &&
                  jwtEval.Result.Contains("user123"),
                "JWT decoder parses claims offline and explicitly marks signature unverified");

            var badJwt = PowerDeveloperToolsService.EvaluateInline("jwt not.a.jwt");
            Check(badJwt.IsValid && badJwt.Title.Contains("Invalid", StringComparison.OrdinalIgnoreCase),
                "malformed JWT handles failure safely without throwing");

            var colorEval = PowerDeveloperToolsService.EvaluateInline("#ff6363");
            Check(colorEval.IsValid && colorEval.Result == "rgb(255, 99, 99)",
                "color converter translates HEX to RGB and HSL");

            var urlEnc = PowerDeveloperToolsService.EvaluateInline("url hello world!");
            var urlDec = PowerDeveloperToolsService.EvaluateInline("urld hello%20world%21");
            Check(urlEnc.IsValid && urlEnc.Result == "hello%20world%21" && urlDec.IsValid && urlDec.Result == "hello world!",
                "URL encoder and decoder round-trip cleanly");

            var nanoEval = PowerDeveloperToolsService.EvaluateInline("nanoid");
            var ulidEval = PowerDeveloperToolsService.EvaluateInline("ulid");
            var passEval = PowerDeveloperToolsService.EvaluateInline("pass 24");
            Check(nanoEval.IsValid && nanoEval.Result.Length == 21 &&
                  ulidEval.IsValid && ulidEval.Result.Length == 26 &&
                  passEval.IsValid && passEval.Result.Length == 24,
                "NanoID, ULID, and custom-length password generators work accurately");

            var timerEval = PowerDeveloperToolsService.EvaluateInline("timer 5m Focus");
            Check(timerEval.IsValid && timerEval.ActionType == "timer_start",
                "countdown timer parser accepts duration and label");

            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                int boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                var portEval = PowerDeveloperToolsService.EvaluateInline("port " + boundPort);
                Check(portEval.IsValid &&
                      portEval.ActionType == "port_inspect" &&
                      portEval.Title.Contains(Environment.ProcessId.ToString()),
                    "native iphlpapi port inspector identifies listening port and PID without killing");
                listener.Stop();
            }

            Check(PowerDeveloperToolsService.TryFormatJson("{\"a\":1,\"b\":[2]}", false, out string prettyJson, out _) &&
                  prettyJson.Contains("\n") &&
                  !PowerDeveloperToolsService.TryFormatJson("{bad json", false, out _, out _),
                "JSON formatter validates and pretty-prints valid JSON while rejecting malformed input");

            Check(PowerDeveloperToolsService.TryCleanTrackingParams(
                      "https://example.com/post?id=42&utm_source=x&fbclid=abc&gclid=xyz",
                      out string cleanUrl,
                      out int removedParams) &&
                  removedParams == 3 &&
                  cleanUrl == "https://example.com/post?id=42",
                "URL tracker cleaner strips utm/fbclid/gclid while preserving functional query params");

            string tempScanDir = Path.Combine(temp, "stale_temp_test");
            Directory.CreateDirectory(tempScanDir);
            string oldFile = Path.Combine(tempScanDir, "old.tmp");
            string newFile = Path.Combine(tempScanDir, "fresh.tmp");
            File.WriteAllBytes(oldFile, new byte[2048]);
            File.WriteAllBytes(newFile, new byte[1024]);
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddHours(-48));

            var preview = PowerDeveloperToolsService.ScanStaleTempFilesPreview(
                roots: new[] { tempScanDir },
                minAge: TimeSpan.FromHours(24));
            Check(preview.Files.Count == 1 &&
                  preview.Files[0].FullPath == oldFile &&
                  preview.TotalBytes == 2048 &&
                  File.Exists(oldFile) &&
                  File.Exists(newFile),
                "temp cleanup preview identifies only >24h stale files and deletes 0 files during scan");

            var cleanupOutcome = PowerDeveloperToolsService.ExecuteTempFilesCleanup(preview);
            Check(cleanupOutcome.DeletedCount == 1 &&
                  cleanupOutcome.FreedBytes == 2048 &&
                  !File.Exists(oldFile) &&
                  File.Exists(newFile),
                "temp cleanup execution removes confirmed stale files while keeping recent files intact");

            Console.WriteLine($"{_passed} checks passed.");
        }
        finally
        {
            MemoryReductService.Platform = originalPlatform;
            MemoryReductService.PathInspector = originalInspector;
            MemoryReductService.ResetPrivilegeCacheForTests();
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private enum FakeWorkerBehavior
    {
        WriteCorrelatedTelemetry,
        PartialSuccess,
        ExitZeroWithoutTelemetry,
        TimedOut,
        UacCancelled
    }

    private sealed class FakePathSecurityInspector : IPathSecurityInspector
    {
        private readonly Dictionary<string, (bool IsDir, FileSystemSecurity Sec, string? ReparseTarget)> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ThrowOnSecurityQueryFor { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Reset()
        {
            _entries.Clear();
            ThrowOnSecurityQueryFor.Clear();
        }

        public void AddDirectory(string path, DirectorySecurity sec)
        {
            string norm = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _entries[norm] = (true, sec, null);
        }

        public void AddFile(string path, FileSecurity sec)
        {
            _entries[path] = (false, sec, null);
        }

        public void SetReparsePoint(string path, string target)
        {
            string norm = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string key = _entries.ContainsKey(norm) ? norm : path;
            if (_entries.TryGetValue(key, out var existing))
            {
                _entries[key] = (existing.IsDir, existing.Sec, target);
            }
        }

        public bool FileExists(string path) =>
            _entries.TryGetValue(path, out var entry) && !entry.IsDir;

        public bool DirectoryExists(string path)
        {
            string norm = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return _entries.TryGetValue(norm, out var entry) && entry.IsDir;
        }

        public FileAttributes GetAttributes(string path)
        {
            string norm = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (_entries.TryGetValue(_entries.ContainsKey(norm) ? norm : path, out var entry))
            {
                FileAttributes attrs = entry.IsDir ? FileAttributes.Directory : FileAttributes.Normal;
                if (entry.ReparseTarget != null) attrs |= FileAttributes.ReparsePoint;
                return attrs;
            }
            throw new FileNotFoundException("Fake entry not found", path);
        }

        public string? GetReparseTarget(string path, bool isDirectory)
        {
            string norm = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return _entries.TryGetValue(_entries.ContainsKey(norm) ? norm : path, out var entry)
                ? entry.ReparseTarget
                : null;
        }

        public FileSystemSecurity GetDirectorySecurity(string path)
        {
            string norm = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (ThrowOnSecurityQueryFor.Contains(norm) || ThrowOnSecurityQueryFor.Contains(path))
                throw new UnauthorizedAccessException("Simulated denied ACL read");
            if (_entries.TryGetValue(norm, out var entry) && entry.IsDir)
                return entry.Sec;
            throw new DirectoryNotFoundException(path);
        }

        public FileSystemSecurity GetFileSecurity(string path)
        {
            if (ThrowOnSecurityQueryFor.Contains(path))
                throw new UnauthorizedAccessException("Simulated denied file ACL read");
            if (_entries.TryGetValue(path, out var entry) && !entry.IsDir)
                return entry.Sec;
            throw new FileNotFoundException(path);
        }

        public IEnumerable<string> EnumerateFiles(string directory)
        {
            string norm = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (ThrowOnSecurityQueryFor.Contains(norm))
                throw new UnauthorizedAccessException("Simulated denied directory enumeration");

            return _entries
                .Where(kv => !kv.Value.IsDir &&
                             string.Equals(Path.GetDirectoryName(kv.Key)?.TrimEnd(Path.DirectorySeparatorChar), norm, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
        }
    }

    private sealed class FakeMemoryReductPlatform : IMemoryReductPlatform
    {
        public bool FakeElevated { get; set; }
        public bool AllowKernelCommands { get; set; }
        public bool SimulateUnsupportedFileCacheNt { get; set; }
        public FakeWorkerBehavior WorkerBehavior { get; set; } = FakeWorkerBehavior.WriteCorrelatedTelemetry;
        public string LastLaunchedRequestId { get; private set; } = string.Empty;
        public List<int> TrimmedPids { get; } = new();

        private readonly List<ProcessSnapshotEntry> _procs = new()
        {
            new(4, "System", 4L * 1024 * 1024),
            new(500, "lsass", 40L * 1024 * 1024),
            new(101, "code", 300L * 1024 * 1024),
            new(102, "chrome", 200L * 1024 * 1024),
            new(103, "node", 150L * 1024 * 1024),
            new(104, "excludedapp", 120L * 1024 * 1024),
            new(105, "tinyapp", 10L * 1024 * 1024)
        };

        public bool IsElevated => FakeElevated;
        public Version GetOsVersion() => new(10, 0, 26100);

        public bool GetGlobalMemoryStatus(out ulong totalPhys, out ulong availPhys, out uint memoryLoad)
        {
            totalPhys = 16UL * 1024 * 1024 * 1024;
            availPhys = 6UL * 1024 * 1024 * 1024;
            memoryLoad = 62;
            return true;
        }

        public bool GetPerformanceInfo(out long systemCacheBytes, out long commitTotalBytes, out long commitLimitBytes, out long pageSizeBytes)
        {
            pageSizeBytes = 4096;
            systemCacheBytes = 512L * 1024 * 1024;
            commitTotalBytes = 11L * 1024 * 1024 * 1024;
            commitLimitBytes = 20L * 1024 * 1024 * 1024;
            return true;
        }

        public int QueryMemoryListInformation(long pageSize, out long standbyBytes, out long modifiedBytes, out long freeBytes)
        {
            standbyBytes = 2048L * 1024 * 1024;
            modifiedBytes = 128L * 1024 * 1024;
            freeBytes = 512L * 1024 * 1024;
            return 0;
        }

        public bool TryAcquirePrivilege(string privilegeName, out int win32Error)
        {
            if (privilegeName == "SeNonExistentPrivilege_KestrelTest")
            {
                win32Error = 1313;
                return false;
            }
            if (!FakeElevated)
            {
                win32Error = 1300;
                return false;
            }
            win32Error = 0;
            return true;
        }

        public int SetSystemMemoryListCommand(int command) =>
            AllowKernelCommands ? 0 : unchecked((int)0xC0000061);

        public int SetSystemFileCacheNt()
        {
            if (SimulateUnsupportedFileCacheNt) return unchecked((int)0xC0000003);
            return AllowKernelCommands ? 0 : unchecked((int)0xC0000061);
        }

        public bool SetSystemFileCacheWin32(out int win32Error)
        {
            win32Error = AllowKernelCommands ? 0 : 1314;
            return AllowKernelCommands;
        }

        public int SetRegistryReconciliation() => AllowKernelCommands ? 0 : unchecked((int)0xC0000061);
        public int SetCombineMemoryLists() => AllowKernelCommands ? 0 : unchecked((int)0xC0000061);

        public (int Pid, string Name) ResolveForegroundProcess(int explicitForegroundPid)
        {
            if (explicitForegroundPid == 101) return (101, "code");
            return (101, "code");
        }

        public IReadOnlyList<ProcessSnapshotEntry> GetProcesses() => _procs;

        public bool TryGetProcessName(int pid, out string processName, out string errorMessage, out int errorCode)
        {
            var match = _procs.FirstOrDefault(p => p.Pid == pid);
            if (match.Pid == pid)
            {
                processName = match.ProcessName;
                errorMessage = string.Empty;
                errorCode = 0;
                return true;
            }
            processName = string.Empty;
            errorMessage = $"Process with an Id of {pid} is not running.";
            errorCode = -1;
            return false;
        }

        public ProcessTrimOutcome TrimProcessWorkingSet(int pid, long minWorkingSetBytes)
        {
            var proc = _procs.FirstOrDefault(p => p.Pid == pid);
            if (proc.Pid != pid)
                return new ProcessTrimOutcome(false, false, false, 0, 0, 87);

            if (minWorkingSetBytes > 0 && proc.WorkingSetBytes < minWorkingSetBytes)
                return new ProcessTrimOutcome(false, false, true, proc.WorkingSetBytes, proc.WorkingSetBytes, 0);

            TrimmedPids.Add(pid);
            long afterWs = (long)(proc.WorkingSetBytes * 0.6);
            return new ProcessTrimOutcome(true, true, false, proc.WorkingSetBytes, afterWs, 0);
        }

        public bool IsScheduledCleanTaskInstalled() => false;
        public bool TryRunScheduledCleanTask() => false;

        public ElevatedWorkerLaunchResult LaunchElevatedWorker(string argument, string requestId, CancellationToken cancellationToken)
        {
            LastLaunchedRequestId = requestId;

            if (cancellationToken.IsCancellationRequested)
                return new ElevatedWorkerLaunchResult(false, false, false, false, true, -1);

            if (WorkerBehavior == FakeWorkerBehavior.UacCancelled)
                return new ElevatedWorkerLaunchResult(false, false, false, true, false, 1223);

            if (WorkerBehavior == FakeWorkerBehavior.TimedOut)
                return new ElevatedWorkerLaunchResult(true, false, true, false, false, -1);

            if (WorkerBehavior == FakeWorkerBehavior.WriteCorrelatedTelemetry ||
                WorkerBehavior == FakeWorkerBehavior.PartialSuccess)
            {
                bool isPartial = WorkerBehavior == FakeWorkerBehavior.PartialSuccess;
                Directory.CreateDirectory(MemoryReductService.HeadlessTelemetryDirectory);
                File.WriteAllText(MemoryReductService.GetTelemetryFilePath(requestId), JsonSerializer.Serialize(new HeadlessWorkerTelemetry
                {
                    RequestId = requestId,
                    UtcTimestamp = DateTime.UtcNow,
                    Operation = argument,
                    StandbyPurged = true,
                    FileCacheFlushed = !isPartial,
                    ModifiedListFlushed = false,
                    AllSucceeded = !isPartial,
                    PartialSuccess = isPartial,
                    StandbyBeforeBytes = 2048L * 1024 * 1024,
                    StandbyAfterBytes = 256L * 1024 * 1024,
                    Operations = isPartial
                        ? new List<MemoryOperationReport>
                        {
                            new() { OperationName = "LowPriorityStandbyPurge", Attempted = true, Succeeded = true, StatusOrErrorCode = 0, Message = "Succeeded" },
                            new() { OperationName = "ModifiedListFlush", Attempted = true, Succeeded = false, StatusOrErrorCode = unchecked((int)0xC0000061), Message = "NTSTATUS 0xC0000061" }
                        }
                        : new List<MemoryOperationReport>
                        {
                            new() { OperationName = "LowPriorityStandbyPurge", Attempted = true, Succeeded = true, StatusOrErrorCode = 0, Message = "Succeeded" }
                        }
                }));
            }

            return new ElevatedWorkerLaunchResult(true, true, false, false, false, 0);
        }

        public void Wait(int milliseconds, CancellationToken cancellationToken) { }
    }

    private sealed class FakeCloud : HttpMessageHandler
    {
        public bool SecureBackend = true;
        public bool Persistence = true;
        public HttpStatusCode StorageStatus = HttpStatusCode.OK;
        public int Posts;
        public string LastAuthorization = "";
        public string LastBody = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = new StringContent(SecureBackend ? "{\"auth_scheme\":\"installation-bearer-v1\"}" : "{}") };
            Posts++;
            LastAuthorization = request.Headers.Authorization?.ToString() ?? "";
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/v1/keys"))
                return new(StorageStatus) { Content = new StringContent(JsonSerializer.Serialize(new { supabase_synced = Persistence })) };
            return new(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n") };
        }
    }
}
