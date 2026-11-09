using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Beam.Models;
using Beam.Services;
using BeamInstaller;

internal static class Program
{
    private static int _passed;
    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception(description);
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
        try
        {
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
            handler.SecureBackend = false; // exercises direct fallback without sending keys to old backend
            foreach (string model in new[] { "gemini-test", "gpt-test", "claude-test", "llama-test" })
            {
                await AiClient.StreamChatAsync(uri.AbsoluteUri, messages, model, "test-key", _ => { });
                using var body = JsonDocument.Parse(handler.LastBody);
                var turns = body.RootElement.GetProperty(model == "gemini-test" ? "contents" : "messages");
                Check(turns.GetArrayLength() == 3, model + " fallback receives complete conversation");
                Check(turns[1].GetProperty("role").GetString() == (model == "gemini-test" ? "model" : "assistant"), model + " preserves assistant role");
            }

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
            // Parse the generated script, without invoking cleanup or touching installed applications.
            var info = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            string parse = "$tokens=$null; $errors=$null; [System.Management.Automation.Language.Parser]::ParseInput([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(script)) + "')), [ref]$tokens, [ref]$errors) | Out-Null; if ($errors.Count) { exit 1 }";
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(parse)));
            using var process = System.Diagnostics.Process.Start(info)!;
            await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "cleanup script parses with spaces, apostrophes, and ampersands");

            // Mem Reduct Engine Tests
            var memStats = MemoryReductService.GetMemoryStats();
            Check(memStats.TotalPhysBytes > 0 && memStats.LoadPercent <= 100, "memory stats queries valid physical RAM metrics");

            var cleanResult = MemoryReductService.ReduceMemory(purgeStandby: false, flushModified: false, sweepProcesses: true);
            Check(cleanResult.ProcessesCleaned > 0, "least-privilege working set sweep cleans accessible user processes");
            Check(cleanResult.FreedBytes >= 0, "freed bytes calculation is non-negative");
            Check(!MemoryReductService.CanAutoClean, "cooldown blocks immediate consecutive auto-clean iterations");

            int workerCode = MemoryReductService.ExecuteHeadlessWorker(purgeStandbyOnly: true);
            Check(workerCode == 0, "headless standby clean worker executes cleanly");

            Console.WriteLine($"{_passed} checks passed.");
        }
        finally
        {
            // Only the unique test workspace is removed.
            Directory.Delete(temp, recursive: true);
        }
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
