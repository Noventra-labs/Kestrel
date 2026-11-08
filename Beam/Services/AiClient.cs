using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Beam.Services
{
    public class AiClient
    {
        private static readonly HttpClient Client = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 20,
            EnableMultipleHttp2Connections = true
        })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        public static async Task<bool> CheckBackendHealthAsync(string backendUrl)
        {
            try
            {
                var healthUrl = backendUrl.TrimEnd('/') + "/health";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var res = await Client.GetAsync(healthUrl, cts.Token);
                return res.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<(bool Ok, string Message)> SaveKeyToSupabaseVaultAsync(
            string backendUrl,
            string modelOrProvider,
            string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return (false, "API key is empty.");

            string provider = BeamSettings.InferProviderFromModel(modelOrProvider);
            var raw = (modelOrProvider ?? "").Trim().ToLowerInvariant();
            if (raw is "gemini" or "openai" or "groq" or "anthropic")
            {
                provider = raw;
            }

            try
            {
                var url = backendUrl.TrimEnd('/') + "/v1/keys";
                var payload = new
                {
                    provider,
                    api_key = apiKey.Trim(),
                    key_label = "Saved from Beam Desktop"
                };
                var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var res = await Client.PostAsync(url, jsonContent, cts.Token);
                if (res.IsSuccessStatusCode)
                {
                    return (true, $"Encrypted & stored '{provider}' key in Supabase Vault");
                }
                var err = await res.Content.ReadAsStringAsync(cts.Token);
                return (false, $"Vault error ({(int)res.StatusCode}): {err}");
            }
            catch
            {
                return (false, $"Saved '{provider}' API key locally (Cloud Backend offline)");
            }
        }

        public static string ResolveLocalApiKey(string model, string? explicitKey)
        {
            var provider = BeamSettings.InferProviderFromModel(model);
            var providerKey = BeamSettings.Current.GetApiKeyForProvider(provider);
            if (!string.IsNullOrWhiteSpace(providerKey))
                return providerKey.Trim();

            if (!string.IsNullOrWhiteSpace(explicitKey))
                return explicitKey.Trim();

            return provider switch
            {
                "openai" => Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "",
                "groq" => Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "",
                "anthropic" => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "",
                _ => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? ""
            };
        }

        public static async Task StreamChatAsync(
            string backendUrl,
            string prompt,
            string model,
            string? apiKeyOverride,
            Action<string> onDelta,
            CancellationToken cancellationToken = default)
        {
            var effectiveModel = string.IsNullOrWhiteSpace(model) ? "gemini-2.0-flash" : model.Trim();
            var provider = BeamSettings.InferProviderFromModel(effectiveModel);
            var effectiveKey = ResolveLocalApiKey(effectiveModel, apiKeyOverride);

            try
            {
                var chatUrl = backendUrl.TrimEnd('/') + "/v1/chat";

                var payload = new
                {
                    messages = new[] { new { role = "user", content = prompt } },
                    model = effectiveModel,
                    api_key_override = string.IsNullOrWhiteSpace(effectiveKey) ? null : effectiveKey,
                    stream = true
                };

                var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, chatUrl) { Content = jsonContent };

                using var response = await Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Cloud AI Service Error ({response.StatusCode}): {err}");
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);

                string? line;
                while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.StartsWith("data: "))
                    {
                        var data = line[6..].Trim();
                        if (data == "[DONE]") break;

                        try
                        {
                            using var doc = JsonDocument.Parse(data);
                            if (doc.RootElement.TryGetProperty("delta", out var deltaElem))
                            {
                                var delta = deltaElem.GetString();
                                if (!string.IsNullOrEmpty(delta))
                                {
                                    onDelta(delta);
                                }
                            }
                        }
                        catch
                        {
                            onDelta(data);
                        }
                    }
                    else
                    {
                        onDelta(line);
                    }
                }
            }
            catch (HttpRequestException) when (!string.IsNullOrWhiteSpace(effectiveKey))
            {
                // 100% Local Direct Provider Fallback when Cloud Backend is not running
                switch (provider)
                {
                    case "openai":
                        await StreamDirectOpenAiCompatibleAsync(
                            "https://api.openai.com/v1/chat/completions",
                            prompt,
                            effectiveModel,
                            effectiveKey,
                            onDelta,
                            cancellationToken);
                        break;
                    case "groq":
                        await StreamDirectOpenAiCompatibleAsync(
                            "https://api.groq.com/openai/v1/chat/completions",
                            prompt,
                            effectiveModel,
                            effectiveKey,
                            onDelta,
                            cancellationToken);
                        break;
                    case "anthropic":
                        await StreamDirectAnthropicAsync(
                            prompt,
                            effectiveModel,
                            effectiveKey,
                            onDelta,
                            cancellationToken);
                        break;
                    default:
                        await StreamDirectGeminiAsync(
                            prompt,
                            effectiveModel,
                            effectiveKey,
                            onDelta,
                            cancellationToken);
                        break;
                }
            }
        }

        private static async Task StreamDirectGeminiAsync(
            string prompt,
            string model,
            string apiKey,
            Action<string> onDelta,
            CancellationToken cancellationToken)
        {
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse&key={apiKey}";
            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[] { new { text = prompt } }
                    }
                }
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = jsonContent };
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"Gemini Direct API Error ({response.StatusCode}): {err}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            string? line;
            while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (!line.StartsWith("data: ")) continue;
                var json = line[6..].Trim();
                if (string.IsNullOrEmpty(json)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var text = doc.RootElement
                        .GetProperty("candidates")[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text")
                        .GetString();

                    if (!string.IsNullOrEmpty(text))
                    {
                        onDelta(text);
                    }
                }
                catch { }
            }
        }

        private static async Task StreamDirectOpenAiCompatibleAsync(
            string endpoint,
            string prompt,
            string model,
            string apiKey,
            Action<string> onDelta,
            CancellationToken cancellationToken)
        {
            var payload = new
            {
                model,
                messages = new[] { new { role = "user", content = prompt } },
                stream = true
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = jsonContent };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"Direct Provider API Error ({response.StatusCode}): {err}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            string? line;
            while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (!line.StartsWith("data: ")) continue;
                var json = line[6..].Trim();
                if (string.IsNullOrEmpty(json) || json == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("delta", out var deltaObj) &&
                        deltaObj.TryGetProperty("content", out var contentElem))
                    {
                        var text = contentElem.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            onDelta(text);
                        }
                    }
                }
                catch { }
            }
        }

        private static async Task StreamDirectAnthropicAsync(
            string prompt,
            string model,
            string apiKey,
            Action<string> onDelta,
            CancellationToken cancellationToken)
        {
            var endpoint = "https://api.anthropic.com/v1/messages";
            var payload = new
            {
                model,
                max_tokens = 2048,
                messages = new[] { new { role = "user", content = prompt } },
                stream = true
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = jsonContent };
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"Anthropic Direct API Error ({response.StatusCode}): {err}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            string? line;
            while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (!line.StartsWith("data: ")) continue;
                var json = line[6..].Trim();
                if (string.IsNullOrEmpty(json)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("type", out var typeElem) &&
                        typeElem.GetString() == "content_block_delta" &&
                        doc.RootElement.TryGetProperty("delta", out var deltaObj) &&
                        deltaObj.TryGetProperty("text", out var textElem))
                    {
                        var text = textElem.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            onDelta(text);
                        }
                    }
                }
                catch { }
            }
        }
    }
}
