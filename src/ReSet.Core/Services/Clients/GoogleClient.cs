using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services.Clients
{
    public class GoogleClient : IAiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _endpoint;
        private readonly string _modelName;

        public string ProviderName => "Google";
        public string ModelName => _modelName;

        public GoogleClient(HttpClient httpClient, string apiKey, string endpoint, string modelName)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = apiKey;
            _modelName = modelName;

            var ep = string.IsNullOrWhiteSpace(endpoint) ? "https://generativelanguage.googleapis.com" : endpoint.Trim();
            _endpoint = ep;
        }

        public async Task<AiResult> ChatAsync(string systemPrompt, string userPrompt, float temperature, string? effort = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new ArgumentException("Google API 키가 설정되지 않았습니다.");
            }

            var lowerModel = _modelName.ToLowerInvariant();
            bool supportsThinking = lowerModel.Contains("thinking") || 
                                    lowerModel.Contains("gemini-2.0") || 
                                    lowerModel.Contains("gemini-2.5") || 
                                    lowerModel.Contains("gemini-3");

            bool enableThinking = supportsThinking;
            object generationConfig;

            if (enableThinking)
            {
                object thinkingConfig;
                bool isGemini3 = lowerModel.Contains("gemini-3");
                
                string apiEffort = "medium";
                if (!string.IsNullOrWhiteSpace(effort))
                {
                    apiEffort = effort;
                }

                if (isGemini3)
                {
                    string thinkingLevel = apiEffort.ToLowerInvariant() switch
                    {
                        "low" => "LOW",
                        "medium" => "MEDIUM",
                        "high" => "HIGH",
                        "xhigh" => "HIGH",
                        _ => "MEDIUM"
                    };

                    thinkingConfig = new
                    {
                        thinkingLevel = thinkingLevel,
                        includeThoughts = true
                    };
                }
                else
                {
                    int thinkingBudget = apiEffort.ToLowerInvariant() switch
                    {
                        "low" => 1024,
                        "medium" => 4096,
                        "high" => 16384,
                        "xhigh" => -1,
                        _ => 4096
                    };

                    thinkingConfig = new
                    {
                        thinkingBudget = thinkingBudget,
                        includeThoughts = true
                    };
                }

                generationConfig = new
                {
                    thinkingConfig = thinkingConfig
                };
            }
            else
            {
                generationConfig = new
                {
                    temperature = temperature
                };
            }

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new { text = userPrompt }
                        }
                    }
                },
                systemInstruction = new
                {
                    parts = new[]
                    {
                        new { text = systemPrompt }
                    }
                },
                generationConfig = generationConfig
            };

            var jsonPayload = JsonSerializer.Serialize(requestBody);
            
            // Assembly Google/Gemini Endpoint URI
            var url = $"{_endpoint.TrimEnd('/')}/v1beta/models/{_modelName}:generateContent?key={_apiKey}";
            var logUrl = $"{_endpoint.TrimEnd('/')}/v1beta/models/{_modelName}:generateContent?key=******";
            Log.Debug("Google Gemini API 요청 전송 준비 - URI: {Uri}\n[Payload JSON]:\n{Payload}", logUrl, jsonPayload);
            
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log.Error("Google Gemini API HTTP 요청 실패 - StatusCode: {StatusCode} ({ReasonPhrase})\n[Error Response Content]:\n{ErrorContent}", (int)response.StatusCode, response.ReasonPhrase, errorContent);
                throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).\n상세 에러 내용: {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Debug("Google Gemini API HTTP 응답 수신 완료 - StatusCode: {StatusCode}\n[Response Content]:\n{ResponseContent}", (int)response.StatusCode, responseContent);

            using (var doc = JsonDocument.Parse(responseContent))
            {
                var root = doc.RootElement;

                // promptFeedback 차단 여부 확인
                if (root.TryGetProperty("promptFeedback", out var promptFeedback))
                {
                    if (promptFeedback.TryGetProperty("blockReason", out var blockReason))
                    {
                        var reasonStr = blockReason.GetString();
                        Log.Error("Google Gemini API 요청이 안전 필터에 의해 차단되었습니다. (원인: {Reason})", reasonStr);
                        throw new InvalidOperationException($"Google Gemini API 요청이 안전 필터에 의해 차단되었습니다. (원인: {reasonStr})");
                    }
                }

                // candidates 존재 및 빈 값 여부 확인
                if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                {
                    Log.Error("Google Gemini API 응답 candidates 누락 또는 빈 배열");
                    throw new InvalidOperationException("Google Gemini API 응답에서 생성된 후보군(candidates)을 찾을 수 없습니다.");
                }

                var firstCandidate = candidates[0];

                // finishReason에 따른 오류 처리
                if (firstCandidate.TryGetProperty("finishReason", out var finishReason))
                {
                    var reason = finishReason.GetString();
                    if (!string.Equals(reason, "STOP", StringComparison.OrdinalIgnoreCase) && 
                        !string.Equals(reason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Error("Google Gemini API 생성이 정상 완료되지 않음. finishReason: {Reason}", reason);
                        throw new InvalidOperationException($"Google Gemini API 생성이 정상 완료되지 않았습니다. (이유: {reason})");
                    }
                }

                // content 및 parts, text 탐색 및 반환
                if (!firstCandidate.TryGetProperty("content", out var contentElement))
                {
                    Log.Error("Google Gemini API 응답 candidate 내 content 누락");
                    throw new InvalidOperationException("Google Gemini API 응답 후보군 내에 content 속성이 존재하지 않습니다. (안전 필터 등에 의한 차단 가능성)");
                }

                if (!contentElement.TryGetProperty("parts", out var partsElement) || partsElement.GetArrayLength() == 0)
                {
                    Log.Error("Google Gemini API 응답 content 내 parts 누락 또는 빈 배열");
                    throw new InvalidOperationException("Google Gemini API 응답 content 내에 parts 속성이 존재하지 않거나 비어 있습니다.");
                }

                string? thinkingText = null;
                var sbResult = new StringBuilder();
                foreach (var part in partsElement.EnumerateArray())
                {
                    bool isThought = false;
                    if (part.TryGetProperty("thought", out var thoughtElem))
                    {
                        isThought = thoughtElem.ValueKind == JsonValueKind.True || (thoughtElem.ValueKind == JsonValueKind.False ? false : thoughtElem.GetBoolean());
                    }

                    if (isThought && part.TryGetProperty("text", out var textElem))
                    {
                        thinkingText = textElem.GetString();
                    }
                    else if (part.TryGetProperty("text", out var normalTextElem))
                    {
                        sbResult.Append(normalTextElem.GetString());
                    }
                }

                if (!string.IsNullOrWhiteSpace(thinkingText))
                {
                    Log.Information("[Google Gemini Thinking Process]:\n{Thinking}", thinkingText);
                }

                var resultText = sbResult.ToString();
                if (string.IsNullOrEmpty(resultText))
                {
                    Log.Error("Google Gemini API 응답 parts 내 text 누락");
                    throw new InvalidOperationException("Google Gemini API 응답 parts 내에 실제 응답 텍스트(text)가 존재하지 않습니다.");
                }

                return new AiResult
                {
                    Content = resultText,
                    ThinkingText = thinkingText
                };
            }
        }
    }
}
