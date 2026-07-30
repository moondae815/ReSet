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
    public class ClaudeClient : IAiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _endpoint;
        private readonly string _modelName;

        public string ProviderName => "Claude";
        public string ModelName => _modelName;

        public ClaudeClient(HttpClient httpClient, string apiKey, string endpoint, string modelName)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = apiKey;
            _modelName = modelName;

            var ep = string.IsNullOrWhiteSpace(endpoint) ? "https://api.anthropic.com" : endpoint.Trim();
            if (ep.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
            {
                ep = ep.Substring(0, ep.Length - "/v1/messages".Length).TrimEnd('/');
            }
            _endpoint = ep;
        }

        public async Task<AiResult> ChatAsync(string systemPrompt, string userPrompt, float temperature, string? effort = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new ArgumentException("Claude API 키가 설정되지 않았습니다.");
            }

            // Claude 모델별 최대 출력 토큰(max_tokens) 및 Thinking 설정 대응
            var lowerModel = _modelName.ToLowerInvariant();
            double version = GetClaudeVersion(_modelName);
            int maxTokens = 4096; // 기본 안전 한도 (Claude 3 Opus 등)

            bool is4thGenOrNewer = version >= 4.0;

            if (is4thGenOrNewer)
            {
                if (lowerModel.Contains("haiku-4-5") || lowerModel.Contains("haiku-5"))
                {
                    maxTokens = 64000; // Claude Haiku 4.5/5 한도
                }
                else
                {
                    maxTokens = 128000; // Claude 4th/5th Gen Opus/Sonnet 한도 (128k)
                }
            }
            else if (version >= 3.5)
            {
                maxTokens = 8192; // Claude 3.5 / 3.7 Sonnet 및 Haiku 등 최신 모델 한도
            }

            bool enableThinking = version >= 3.7 && (!string.IsNullOrWhiteSpace(effort) || lowerModel.Contains("opus-4-8") || lowerModel.Contains("sonnet-4-6") || version >= 5.0);

            // 프롬프트 캐싱 적용 (System 영역 전체를 ephemeral 캐시로 지정)
            var systemBlocks = new[]
            {
                new
                {
                    type = "text",
                    text = systemPrompt,
                    cache_control = new { type = "ephemeral" }
                }
            };

            object requestBody;

            if (enableThinking)
            {
                if (is4thGenOrNewer)
                {
                    string apiEffort = "medium";
                    if (!string.IsNullOrWhiteSpace(effort))
                    {
                        apiEffort = effort.ToLowerInvariant() switch
                        {
                            "low" => "low",
                            "medium" => "medium",
                            "high" => "high",
                            "xhigh" => "max",
                            _ => "medium"
                        };
                    }

                    var requestMap = new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "model", _modelName },
                        { "system", systemBlocks },
                        { "messages", new[] { new { role = "user", content = userPrompt } } },
                        { "max_tokens", maxTokens },
                        { "thinking", new { type = "adaptive", display = "summarized" } },
                        { "output_config", new { effort = apiEffort } }
                    };

                    requestBody = requestMap;
                }
                else
                {
                    int budgetTokens = 4000; // 기본값
                    if (!string.IsNullOrWhiteSpace(effort))
                    {
                        budgetTokens = effort.ToLowerInvariant() switch
                        {
                            "low" => 2000,
                            "medium" => 4000,
                            "high" => 16000,
                            "xhigh" => 32000,
                            _ => 4000
                        };
                    }

                    if (budgetTokens >= maxTokens)
                    {
                        budgetTokens = maxTokens - 1000;
                        if (budgetTokens < 1000) budgetTokens = 1000;
                    }

                    requestBody = new
                    {
                        model = _modelName,
                        system = systemBlocks,
                        messages = new[]
                        {
                            new { role = "user", content = userPrompt }
                        },
                        max_tokens = maxTokens,
                        thinking = new
                        {
                            type = "enabled",
                            budget_tokens = budgetTokens,
                            display = "summarized"
                        }
                    };
                }
            }
            else
            {
                if (is4thGenOrNewer)
                {
                    requestBody = new
                    {
                        model = _modelName,
                        system = systemBlocks,
                        messages = new[]
                        {
                            new { role = "user", content = userPrompt }
                        },
                        max_tokens = maxTokens
                    };
                }
                else
                {
                    requestBody = new
                    {
                        model = _modelName,
                        system = systemBlocks,
                        messages = new[]
                        {
                            new { role = "user", content = userPrompt }
                        },
                        max_tokens = maxTokens,
                        temperature = temperature
                    };
                }
            }

            var jsonPayload = JsonSerializer.Serialize(requestBody);
            var requestUri = $"{_endpoint.TrimEnd('/')}/v1/messages";
            Log.Debug("Claude API 요청 전송 준비 - URI: {Uri}\n[Payload JSON]:\n{Payload}", requestUri, jsonPayload);

            var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Log.Error("Claude API HTTP 요청 실패 - StatusCode: {StatusCode} ({ReasonPhrase})\n[Error Response Content]:\n{ErrorContent}", (int)response.StatusCode, response.ReasonPhrase, errorContent);
                throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).\n상세 에러 내용: {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Debug("Claude API HTTP 응답 수신 완료 - StatusCode: {StatusCode}\n[Response Content]:\n{ResponseContent}", (int)response.StatusCode, responseContent);

            using (var doc = JsonDocument.Parse(responseContent))
            {
                var root = doc.RootElement;

                // 에러 응답 확인
                if (root.TryGetProperty("error", out var errorElement))
                {
                    var errMsg = errorElement.TryGetProperty("message", out var msgElement) ? msgElement.GetString() : "알 수 없는 API 오류";
                    Log.Error("Claude API 응답 내 error 감지 - Message: {Message}", errMsg);
                    throw new InvalidOperationException($"Claude API 에러 응답 수신: {errMsg}");
                }

                if (!root.TryGetProperty("content", out var contentElement) || contentElement.GetArrayLength() == 0)
                {
                    Log.Error("Claude API 응답 content 속성 누락 또는 빈 배열");
                    throw new InvalidOperationException("Claude API 응답 데이터 내에 content 속성이 존재하지 않거나 비어 있습니다.");
                }

                // Thinking 모드에서는 content 배열에 { "type": "thinking", ... } 블록이
                // 먼저 오고, 실제 텍스트는 { "type": "text", ... } 블록에 위치합니다.
                // 배열을 순회하여 type == "text"인 항목을 찾아 반환합니다.
                string? resultText = null;
                string? thinkingText = null;
                foreach (var contentItem in contentElement.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("type", out var typeElem))
                    {
                        var typeStr = typeElem.GetString();
                        if (typeStr == "thinking" && contentItem.TryGetProperty("thinking", out var thinkElem))
                        {
                            thinkingText = thinkElem.GetString();
                        }
                        else if (typeStr == "text" && contentItem.TryGetProperty("text", out var textElem))
                        {
                            resultText = textElem.GetString();
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(thinkingText))
                {
                    Log.Information("[Claude Thinking Process]:\n{Thinking}", thinkingText);
                }

                if (resultText == null)
                {
                    // 디버깅을 위해 content 배열의 type 목록을 로그로 기록
                    var types = new System.Collections.Generic.List<string>();
                    foreach (var item in contentElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var t))
                            types.Add(t.GetString() ?? "unknown");
                    }
                    Log.Error("Claude API 응답 content 배열에 type=text 항목 없음 - 수신된 type 목록: [{Types}]", string.Join(", ", types));
                    throw new InvalidOperationException("Claude API 응답 content 내에 text 속성이 존재하지 않습니다.");
                }

                return new AiResult
                {
                    Content = resultText,
                    ThinkingText = thinkingText
                };
            }
        }

        private static double GetClaudeVersion(string modelName)
        {
            var lower = modelName.ToLowerInvariant();
            
            // 모델명에서 숫자가 들어간 부분을 추출하기 위해 대시(-)로 나눈 뒤 검사합니다.
            var parts = lower.Split('-');
            var numbers = new System.Collections.Generic.List<double>();
            
            foreach (var part in parts)
            {
                if (double.TryParse(part, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num))
                {
                    numbers.Add(num);
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var c in part)
                    {
                        if (char.IsDigit(c) || c == '.')
                        {
                            sb.Append(c);
                        }
                    }
                    var extracted = sb.ToString();
                    if (double.TryParse(extracted, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var extractedNum))
                    {
                        if (extractedNum < 100) // 버전 번호 수준만 취합
                        {
                            numbers.Add(extractedNum);
                        }
                    }
                }
            }
            
            if (numbers.Count == 0)
            {
                return 3.0; // 기본값
            }
            
            // claude-3-5-sonnet, claude-sonnet-4-6 등 두 자리 버전 번호가 순차 추출된 경우 (예: [3, 5], [4, 6])
            if (numbers.Count >= 2 && numbers[0] < 10 && numbers[1] < 10)
            {
                return numbers[0] + (numbers[1] / 10.0);
            }
            
            return numbers[0];
        }
    }
}
