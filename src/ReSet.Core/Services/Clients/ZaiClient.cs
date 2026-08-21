using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services.Clients
{
    public class ZaiClient : IAiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _endpoint;
        private readonly string _modelName;

        public string ProviderName => "Z.ai";
        public string ModelName => _modelName;

        public ZaiClient(HttpClient httpClient, string apiKey, string endpoint, string modelName)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = apiKey;
            _modelName = modelName;

            var ep = string.IsNullOrWhiteSpace(endpoint) ? "https://api.z.ai/api" : endpoint.Trim();
            if (ep.EndsWith("/paas/v4/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                ep = ep.Substring(0, ep.Length - "/paas/v4/chat/completions".Length).TrimEnd('/');
            }
            _endpoint = ep;
        }

        public async Task<AiResult> ChatAsync(string systemPrompt, string userPrompt, float temperature, string? effort = null, string? volatileUserSuffix = null, CancellationToken cancellationToken = default)
        {
            userPrompt = PromptComposition.MergeVolatileSuffix(userPrompt, volatileUserSuffix);

            if (string.IsNullOrWhiteSpace(_apiKey) && _endpoint.Contains("z.ai"))
            {
                throw new ArgumentException("Z.ai API 키가 설정되지 않았습니다.");
            }

            var requestBody = new Dictionary<string, object>
            {
                { "model", _modelName },
                { "messages", new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                }
            };

            bool enableThinking = !string.IsNullOrWhiteSpace(effort) || _modelName.ToLowerInvariant().Contains("glm-5");

            if (enableThinking)
            {
                var apiEffort = (effort ?? "medium").ToLowerInvariant() switch
                {
                    "low" => "minimal",
                    "medium" => "high",
                    "high" => "max",
                    _ => "high"
                };
                requestBody.Add("reasoning_effort", apiEffort);
                requestBody.Add("thinking", new { type = "enabled" });
            }
            else
            {
                requestBody.Add("temperature", temperature);
            }

            var jsonPayload = JsonSerializer.Serialize(requestBody);
            var requestUri = $"{_endpoint.TrimEnd('/')}/paas/v4/chat/completions";
            Log.Debug("Z.ai API 요청 전송 준비 - URI: {Uri}\n[Payload JSON]:\n{Payload}", requestUri, jsonPayload);

            var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log.Error("Z.ai API HTTP 요청 실패 - StatusCode: {StatusCode} ({ReasonPhrase})\n[Error Response Content]:\n{ErrorContent}", (int)response.StatusCode, response.ReasonPhrase, errorContent);
                throw new HttpRequestException(
                    $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).\n상세 에러 내용: {errorContent}",
                    null,
                    response.StatusCode);
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Debug("Z.ai API HTTP 응답 수신 완료 - StatusCode: {StatusCode}\n[Response Content]:\n{ResponseContent}", (int)response.StatusCode, responseContent);

            using (var doc = JsonDocument.Parse(responseContent))
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errorElement))
                {
                    var errMsg = errorElement.TryGetProperty("message", out var msgElement) ? msgElement.GetString() : "알 수 없는 API 오류";
                    Log.Error("Z.ai API 응답 내 error 감지 - Message: {Message}", errMsg);
                    throw new InvalidOperationException($"Z.ai API 에러 응답 수신: {errMsg}");
                }

                if (!root.TryGetProperty("choices", out var choicesElement) || choicesElement.GetArrayLength() == 0)
                {
                    Log.Error("Z.ai API 응답 choices 속성 누락 또는 빈 배열");
                    throw new InvalidOperationException("Z.ai API 응답 데이터 내에 choices 속성이 존재하지 않거나 비어 있습니다.");
                }

                var firstChoice = choicesElement[0];
                if (!firstChoice.TryGetProperty("message", out var messageElement))
                {
                    Log.Error("Z.ai API 응답 choices[0] 내 message 속성 누락");
                    throw new InvalidOperationException("Z.ai API 응답 choices 내에 message 속성이 존재하지 않습니다.");
                }

                string? reasoningContent = null;
                if (messageElement.TryGetProperty("reasoning_content", out var reasoningElement))
                {
                    reasoningContent = reasoningElement.GetString();
                }
                else if (messageElement.TryGetProperty("reasoning", out var reasoningAltElement))
                {
                    reasoningContent = reasoningAltElement.GetString();
                }
                else if (messageElement.TryGetProperty("thinking", out var thinkingAltElement))
                {
                    reasoningContent = thinkingAltElement.GetString();
                }

                if (!string.IsNullOrWhiteSpace(reasoningContent))
                {
                    Log.Information("[Z.ai Reasoning Process]:\n{Reasoning}", reasoningContent);
                }

                if (!messageElement.TryGetProperty("content", out var contentElement))
                {
                    Log.Error("Z.ai API 응답 message 내 content 속성 누락");
                    throw new InvalidOperationException("Z.ai API 응답 message 내에 content 속성이 존재하지 않습니다.");
                }

                return new AiResult
                {
                    Content = contentElement.GetString() ?? string.Empty,
                    ThinkingText = reasoningContent
                };
            }
        }
    }
}
