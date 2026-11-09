using System;
using Beam.Models;
using System.Collections.Generic;
using System.Linq;
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
        internal static HttpClient Client { get; set; } = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 20,
            EnableMultipleHttp2Connections = true
        })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        private static async Task<Uri> VerifyBackendAsync(string backendUrl, CancellationToken token)
        {
            var backend = CloudIdentity.ValidateBackend(backendUrl);
            using var response = await Client.GetAsync(new Uri(backend, "health"), token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!doc.RootElement.TryGetProperty("auth_scheme", out var scheme) ||
                scheme.GetString() != "installation-bearer-v1")
                throw new HttpRequestException("The cloud backend needs the security update. No credentials were sent.");
            return backend;
        }

        public static async Task<bool> CheckBackendHealthAsync(string backendUrl)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await VerifyBackendAsync(backendUrl, cts.Token);
                return true;
            }
            catch { return false; }
        }

        private static async Task<HttpRequestMessage> CloudRequestAsync(
            string backendUrl, string endpoint, object payload, CancellationToken token)
        {
            var backend = await VerifyBackendAsync(backendUrl, token);
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(backend, endpoint));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudIdentity.GetCredential(backend));
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return request;
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
                var payload = new { provider, api_key = apiKey.Trim() };
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var request = await CloudRequestAsync(backendUrl, "v1/keys", payload, cts.Token);
                using var res = await Client.SendAsync(request, cts.Token);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
                    bool persisted = doc.RootElement.TryGetProperty("supabase_synced", out var synced) &&
                        synced.ValueKind == JsonValueKind.True;
                    return persisted
                        ? (true, $"Encrypted & stored '{provider}' key in Supabase Vault")
                        : (false, "Cloud storage did not confirm saving the key. Your local key is unchanged.");
                }
                var err = await res.Content.ReadAsStringAsync(cts.Token);
                return (false, $"Vault error ({(int)res.StatusCode}): {err}");
            }
            catch
            {
                return (false, "Cloud save failed. Your local key is unchanged; check the backend connection and configuration.");
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
            IReadOnlyList<ChatMessage> messages,
            string model,
            string? apiKeyOverride,
            Action<string> onDelta,
            CancellationToken cancellationToken = default)
        {
            var effectiveModel = string.IsNullOrWhiteSpace(model) ? "gemini-2.0-flash" : model.Trim();
            var provider = BeamSettings.InferProviderFromModel(effectiveModel);
            var effectiveKey = ResolveLocalApiKey(effectiveModel, apiKeyOverride);
            bool receivedDelta = false;
            var originalOnDelta = onDelta;
            onDelta = delta => { receivedDelta = true; originalOnDelta(delta); };

            try
            {

                var payload = new
                {
                    messages,
                    model = effectiveModel,
                    api_key_override = string.IsNullOrWhiteSpace(effectiveKey) ? null : effectiveKey,
                    stream = true
                };

                using var request = await CloudRequestAsync(backendUrl, "v1/chat", payload, cancellationToken);

                using var response = await Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException($"Cloud AI Service Error ({response.StatusCode}): {err}");
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
            catch (HttpRequestException) when (!receivedDelta && !string.IsNullOrWhiteSpace(effectiveKey))
            {
                // 100% Local Direct Provider Fallback when Cloud Backend is not running
                switch (provider)
                {
                    case "openai":
                        await StreamDirectOpenAiCompatibleAsync(
                            "https://api.openai.com/v1/chat/completions",
                            messages,
                            effectiveModel,
                            effectiveKey,
                            onDelta,
                            cancellationToken);
                        break;
                    case "groq":
                        await StreamDirectOpenAiCompatibleAsync(
                            "https://api.groq.com/openai/v1/chat/completions",
                            messages,
                            effectiveModel,
                            effectiveKey,
                            onDelta,
                            cancellationToken);
                        break;
                    case "anthropic":
                        await StreamDirectAnthropicAsync(
                            messages,
                            effectiveModel,
                            effectiveKey,
                            onDelta,
                            cancellationToken);
                        break;
                    default:
                        await StreamDirectGeminiAsync(
                            messages,
                            effectiveModel,
                            effectiveKey,
                            onDelta,
                            cancellationToken);
                        break;
                }
            }
        }

        private static async Task StreamDirectGeminiAsync(
            IReadOnlyList<ChatMessage> messages,
            string model,
            string apiKey,
            Action<string> onDelta,
            CancellationToken cancellationToken)
        {
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse&key={apiKey}";
            var payload = new
            {
                contents = messages.Select(m => new
                {
                    role = m.Role == "assistant" ? "model" : "user",
                    parts = new[] { new { text = m.Content } }
                }).ToArray()
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
            IReadOnlyList<ChatMessage> messages,
            string model,
            string apiKey,
            Action<string> onDelta,
            CancellationToken cancellationToken)
        {
            var payload = new
            {
                model,
                messages,
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
            IReadOnlyList<ChatMessage> messages,
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
                messages,
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
