using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services.Clients
{
    public class OllamaClient : IAiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _endpoint;
        private readonly string _modelName;
        private readonly int? _numCtx;

        public string ProviderName => "Ollama";
        public string ModelName => _modelName;

        public OllamaClient(HttpClient httpClient, string endpoint, string modelName, int? numCtx = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _modelName = modelName;
            _numCtx = numCtx;

            var ep = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint.Trim();
            
            // Ollama의 네이티브 엔드포인트 경로(/api/chat) 자동 보정
            if (ep.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                ep = ep.Substring(0, ep.Length - 3);
            }
            if (!ep.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase))
            {
                ep = ep.TrimEnd('/') + "/api/chat";
            }
            
            _endpoint = ep;
        }

        public async Task<AiResult> ChatAsync(string systemPrompt, string userPrompt, float temperature, string? effort = null, CancellationToken cancellationToken = default)
        {
            float targetTemp = temperature;
            var lowerModel = _modelName.ToLowerInvariant();
            
            bool isGemma4 = lowerModel.Contains("gemma4");
            bool isQwen3_6 = lowerModel.Contains("qwen3.6") || lowerModel.Contains("qwen-3.6");

            if (isGemma4)
            {
                targetTemp = 1.0f;
            }
            else if (isQwen3_6)
            {
                targetTemp = 0.6f;
            }
            else if (!string.IsNullOrWhiteSpace(effort))
            {
                targetTemp = effort.ToLowerInvariant() switch
                {
                    "low" => 0.1f,
                    "medium" => 0.4f,
                    "high" => 0.7f,
                    "max" => 0.9f,
                    _ => targetTemp
                };
            }

            var optionsObj = new Dictionary<string, object>
            {
                { "temperature", targetTemp },
                { "repeat_penalty", 1.1f }
            };

            if (_numCtx.HasValue)
            {
                optionsObj["num_ctx"] = _numCtx.Value;
            }

            if (isGemma4)
            {
                optionsObj["top_p"] = 0.95f;
                optionsObj["top_k"] = 64;
                optionsObj["repeat_penalty"] = 1.05f;
            }
            else if (isQwen3_6)
            {
                optionsObj["top_p"] = 0.95f;
                optionsObj["top_k"] = 20;
                optionsObj["repeat_penalty"] = 1.1f;
            }

            var requestBody = new Dictionary<string, object>
            {
                { "model", _modelName },
                { "stream", false },
                { "messages", new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                },
                { "options", optionsObj }
            };

            object thinkValue = true;
            if (!string.IsNullOrWhiteSpace(effort))
            {
                var lowerEffort = effort.ToLowerInvariant();
                if (lowerEffort == "low" || lowerEffort == "medium" || lowerEffort == "high" || lowerEffort == "max")
                {
                    thinkValue = lowerEffort;
                }
            }
            requestBody.Add("think", thinkValue);

            var jsonPayload = JsonSerializer.Serialize(requestBody);
            Log.Debug("Ollama API 요청 전송 준비 - URI: {Uri}\n[Payload JSON]:\n{Payload}", _endpoint, jsonPayload);

            var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log.Error("Ollama API HTTP 요청 실패 - StatusCode: {StatusCode} ({ReasonPhrase})\n[Error Response Content]:\n{ErrorContent}", (int)response.StatusCode, response.ReasonPhrase, errorContent);
                throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).\n상세 에러 내용: {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Debug("Ollama API HTTP 응답 수신 완료 - StatusCode: {StatusCode}\n[Response Content]:\n{ResponseContent}", (int)response.StatusCode, responseContent);

            var result = new AiResult();
            using (var doc = JsonDocument.Parse(responseContent))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var errorElement))
                {
                    var errMsg = errorElement.GetString() ?? "알 수 없는 API 오류";
                    throw new InvalidOperationException($"Ollama API 에러 응답 수신: {errMsg}");
                }

                if (!root.TryGetProperty("message", out var messageElement))
                {
                    throw new InvalidOperationException("Ollama API 응답 내에 message 속성이 존재하지 않습니다.");
                }

                if (!messageElement.TryGetProperty("content", out var contentElement))
                {
                    throw new InvalidOperationException("Ollama API 응답 message 내에 content 속성이 존재하지 않습니다.");
                }

                result.Content = contentElement.GetString() ?? string.Empty;
                
                if (messageElement.TryGetProperty("thinking", out var thinkingElement))
                {
                    result.ThinkingText = thinkingElement.GetString() ?? string.Empty;
                }
            }

            var content = result.Content;

            if (string.IsNullOrWhiteSpace(result.ThinkingText))
            {
            // 1. Gemma 4의 공식 제어 토큰 파싱
            int gemmaStart = content.IndexOf("<|channel>thought", StringComparison.OrdinalIgnoreCase);
            if (gemmaStart != -1)
            {
                int gemmaEnd = content.IndexOf("<channel|>", gemmaStart, StringComparison.OrdinalIgnoreCase);
                if (gemmaEnd != -1)
                {
                    int headerLength = 17;
                    var sub = content.Substring(gemmaStart + headerLength);
                    if (sub.StartsWith("\n")) sub = sub.Substring(1);
                    else if (sub.StartsWith("\r\n")) sub = sub.Substring(2);

                    int actualStart = gemmaStart + headerLength + (content.Substring(gemmaStart + headerLength).Length - sub.Length);
                    var extractedThinking = content.Substring(actualStart, gemmaEnd - actualStart).Trim();
                    result.ThinkingText = extractedThinking;

                    var beforeThink = content.Substring(0, gemmaStart);
                    var afterThink = content.Substring(gemmaEnd + 10);
                    result.Content = (beforeThink + afterThink).Trim();
                    content = result.Content;
                }
            }

            // 2. 일반 모델의 <think>...</think> 태그 파싱 (Fallback)
            int startTag = content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (startTag != -1)
            {
                int endTag = content.IndexOf("</think>", startTag + 7, StringComparison.OrdinalIgnoreCase);
                if (endTag != -1)
                {
                    var extractedThinking = content.Substring(startTag + 7, endTag - (startTag + 7)).Trim();
                    result.ThinkingText = extractedThinking;

                    var beforeThink = content.Substring(0, startTag);
                    var afterThink = content.Substring(endTag + 8);
                    result.Content = (beforeThink + afterThink).Trim();
                }
            }
            else
            {
                // 시작 태그 <think>가 유실되었으나 </think>가 있는 경우 방어
                int endTag = content.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                int endTagLength = 8;
                
                if (endTag == -1)
                {
                    endTag = content.IndexOf("<|end of thought|>", StringComparison.OrdinalIgnoreCase);
                    endTagLength = 18;
                }

                if (endTag != -1)
                {
                    var extractedThinking = content.Substring(0, endTag).Trim();
                    result.ThinkingText = extractedThinking;

                    var afterThink = content.Substring(endTag + endTagLength);
                    result.Content = afterThink.Trim();
                }
            }
            }

            if (!string.IsNullOrWhiteSpace(result.ThinkingText))
            {
                Log.Information("[Ollama Reasoning Process]:\n{Reasoning}", result.ThinkingText);
            }

            return result;
        }
    }
}
