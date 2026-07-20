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
    public class OpenAiClient : IAiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _endpoint;
        private readonly string _modelName;
        private readonly bool _isOllama;
        private readonly int? _numCtx;

        public string ProviderName => "OpenAI";
        public string ModelName => _modelName;

        public OpenAiClient(HttpClient httpClient, string apiKey, string endpoint, string modelName, int? numCtx = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = apiKey;
            _modelName = modelName;
            _numCtx = numCtx;

            var ep = string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1" : endpoint.Trim();
            if (ep.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                ep = ep.Substring(0, ep.Length - "/chat/completions".Length).TrimEnd('/');
            }
            _endpoint = ep;

            // 로컬 Ollama 포트(11434) 포함 여부, API Key 공백 여부, 혹은 gemma4 등 로컬 모델 명칭을 기준으로 Ollama 여부 감지
            _isOllama = ep.Contains("11434") || string.IsNullOrWhiteSpace(apiKey) || modelName.ToLowerInvariant().Contains("gemma4") || modelName.ToLowerInvariant().Contains("ollama");
        }

        public async Task<AiResult> ChatAsync(string systemPrompt, string userPrompt, float temperature, string? effort = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) && _endpoint.Contains("openai.com"))
            {
                throw new ArgumentException("OpenAI API 키가 설정되지 않았습니다.");
            }

            var lowerModel = _modelName.ToLowerInvariant();
            bool isResponsesApi = lowerModel.Contains("gpt-5");

            if (isResponsesApi)
            {
                var requestBody = new Dictionary<string, object>
                {
                    { "model", _modelName },
                    { "input", new[]
                        {
                            new { role = "system", content = systemPrompt },
                            new { role = "user", content = userPrompt }
                        }
                    },
                    { "reasoning", new { effort = effort?.ToLowerInvariant() switch
                        {
                            "low" => "low",
                            "medium" => "medium",
                            "high" => "high",
                            "xhigh" => "high",
                            _ => "medium"
                        },
                        summary = "auto"
                      }
                    }
                };

                var jsonPayload = JsonSerializer.Serialize(requestBody);
                var requestUri = $"{_endpoint.TrimEnd('/')}/responses";
                Log.Debug("OpenAI Responses API 요청 전송 준비 - URI: {Uri}\n[Payload JSON]:\n{Payload}", requestUri, jsonPayload);

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
                    Log.Error("OpenAI Responses API HTTP 요청 실패 - StatusCode: {StatusCode} ({ReasonPhrase})\n[Error Response Content]:\n{ErrorContent}", (int)response.StatusCode, response.ReasonPhrase, errorContent);
                    throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).\n상세 에러 내용: {errorContent}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                Log.Debug("OpenAI Responses API HTTP 응답 수신 완료 - StatusCode: {StatusCode}\n[Response Content]:\n{ResponseContent}", (int)response.StatusCode, responseContent);

                using (var doc = JsonDocument.Parse(responseContent))
                {
                    var root = doc.RootElement;

                    // 에러 응답 먼저 확인 (error가 null이 아니거나 존재할 때)
                    if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
                    {
                        var errMsg = errorElement.TryGetProperty("message", out var msgElement) ? msgElement.GetString() : "알 수 없는 API 오류";
                        throw new InvalidOperationException($"OpenAI Responses API 에러 응답 수신: {errMsg}");
                    }

                    // root 자체는 Object이고, 실제 결과 목록은 "output" 프로퍼티(Array)에 들어있음
                    if (root.TryGetProperty("output", out var outputElem) && outputElem.ValueKind == JsonValueKind.Array)
                    {
                        string? resultText = null;
                        string? reasoningText = null;

                        foreach (var item in outputElem.EnumerateArray())
                        {
                            if (item.TryGetProperty("type", out var typeElem))
                            {
                                var typeStr = typeElem.GetString();
                                if (typeStr == "reasoning" && item.TryGetProperty("summary", out var summaryElem) && summaryElem.ValueKind == JsonValueKind.Array)
                                {
                                    var sb = new StringBuilder();
                                    foreach (var sumItem in summaryElem.EnumerateArray())
                                    {
                                        if (sumItem.TryGetProperty("type", out var sumType) && sumType.GetString() == "summary_text" && sumItem.TryGetProperty("text", out var textElem))
                                        {
                                            sb.Append(textElem.GetString());
                                        }
                                    }
                                    reasoningText = sb.ToString();
                                }
                                else if (typeStr == "message" && item.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.Array)
                                {
                                    var sb = new StringBuilder();
                                    foreach (var conItem in contentElem.EnumerateArray())
                                    {
                                        if (conItem.TryGetProperty("type", out var conType) && conType.GetString() == "output_text" && conItem.TryGetProperty("text", out var textElem))
                                        {
                                            sb.Append(textElem.GetString());
                                        }
                                    }
                                    resultText = sb.ToString();
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(reasoningText))
                        {
                            Log.Information("[OpenAI Responses API Reasoning Summary]:\n{Reasoning}", reasoningText);
                        }

                        return new AiResult
                        {
                            Content = resultText ?? string.Empty,
                            ThinkingText = reasoningText
                        };
                    }
                    else
                    {
                        throw new InvalidOperationException("OpenAI Responses API 응답 내에 output 배열 속성이 존재하지 않습니다.");
                    }
                }
            }
            else
            {
                float targetTemp = temperature;

                // o1, o3 모델은 temperature = 1.0f 필수 제약 적용
                bool isReasoningEnforcedModel = 
                    lowerModel.StartsWith("o1") || 
                    lowerModel.StartsWith("o3");
                
                // Gemma 4 모델은 베스트 프랙티스에 따라 temperature = 1.0f 강제
                bool isGemma4 = lowerModel.Contains("gemma4");
                
                // Qwen3.6 모델은 베스트 프랙티스에 따라 temperature = 0.6f 강제
                bool isQwen3_6 = lowerModel.Contains("qwen3.6") || lowerModel.Contains("qwen-3.6");

                if (isReasoningEnforcedModel || isGemma4)
                {
                    targetTemp = 1.0f;
                }
                else if (isQwen3_6)
                {
                    targetTemp = 0.6f;
                }
                else if (_isOllama && !string.IsNullOrWhiteSpace(effort))
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

                var requestBody = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "model", _modelName },
                    { "messages", new[]
                        {
                            new { role = "system", content = systemPrompt },
                            new { role = "user", content = userPrompt }
                        }
                    }
                };

                if (_isOllama)
                {
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

                    var optionsObj = new Dictionary<string, object>();
                    if (_numCtx.HasValue)
                    {
                        optionsObj.Add("num_ctx", _numCtx.Value);
                    }
                    
                    if (isGemma4)
                    {
                        optionsObj.Add("top_p", 0.95f);
                        optionsObj.Add("top_k", 64);
                    }
                    else if (isQwen3_6)
                    {
                        optionsObj.Add("top_p", 0.95f);
                        optionsObj.Add("top_k", 20);
                    }

                    if (optionsObj.Count > 0)
                    {
                        requestBody.Add("options", optionsObj);
                    }
                }

                if (isReasoningEnforcedModel)
                {
                    var apiEffort = "medium";
                    if (!string.IsNullOrWhiteSpace(effort))
                    {
                        apiEffort = effort.ToLowerInvariant() switch
                        {
                            "low" => "low",
                            "medium" => "medium",
                            "high" => "high",
                            "xhigh" => "high",
                            _ => "medium"
                        };
                    }
                    requestBody.Add("reasoning_effort", apiEffort);
                }
                else
                {
                    requestBody.Add("temperature", targetTemp);
                }

                var jsonPayload = JsonSerializer.Serialize(requestBody);
                var requestUri = $"{_endpoint.TrimEnd('/')}/chat/completions";
                Log.Debug("OpenAI API 요청 전송 준비 - URI: {Uri}\n[Payload JSON]:\n{Payload}", requestUri, jsonPayload);

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
                    Log.Error("OpenAI API HTTP 요청 실패 - StatusCode: {StatusCode} ({ReasonPhrase})\n[Error Response Content]:\n{ErrorContent}", (int)response.StatusCode, response.ReasonPhrase, errorContent);
                    throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).\n상세 에러 내용: {errorContent}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                Log.Debug("OpenAI API HTTP 응답 수신 완료 - StatusCode: {StatusCode}\n[Response Content]:\n{ResponseContent}", (int)response.StatusCode, responseContent);

                using (var doc = JsonDocument.Parse(responseContent))
                {
                    var root = doc.RootElement;

                    // 에러 응답 확인
                    if (root.TryGetProperty("error", out var errorElement))
                    {
                        var errMsg = errorElement.TryGetProperty("message", out var msgElement) ? msgElement.GetString() : "알 수 없는 API 오류";
                        Log.Error("OpenAI API 응답 내 error 감지 - Message: {Message}", errMsg);
                        throw new InvalidOperationException($"OpenAI API 에러 응답 수신: {errMsg}");
                    }

                    if (!root.TryGetProperty("choices", out var choicesElement) || choicesElement.GetArrayLength() == 0)
                    {
                        Log.Error("OpenAI API 응답 choices 속성 누락 또는 빈 배열");
                        throw new InvalidOperationException("OpenAI API 응답 데이터 내에 choices 속성이 존재하지 않거나 비어 있습니다.");
                    }

                    var firstChoice = choicesElement[0];
                    if (!firstChoice.TryGetProperty("message", out var messageElement))
                    {
                        Log.Error("OpenAI API 응답 choices[0] 내 message 속성 누락");
                        throw new InvalidOperationException("OpenAI API 응답 choices 내에 message 속성이 존재하지 않습니다.");
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
                        Log.Information("[OpenAI Reasoning Process]:\n{Reasoning}", reasoningContent);
                    }

                    if (!messageElement.TryGetProperty("content", out var contentElement))
                    {
                        Log.Error("OpenAI API 응답 message 내 content 속성 누락");
                        throw new InvalidOperationException("OpenAI API 응답 message 내에 content 속성이 존재하지 않습니다.");
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
}
