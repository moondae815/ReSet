using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services.Clients
{
    /// <summary>
    /// OpenRouter(https://openrouter.ai) 클라이언트. OpenAI 호환 규격이지만
    /// <see cref="OpenAiClient"/>를 공유하지 않는다 - 그쪽은 모델명에 <c>gpt-5</c>가
    /// 들어가면 Responses API로 분기하는데, OpenRouter의 모델 ID는
    /// <c>openai/gpt-5.6</c>처럼 네임스페이스가 붙어 그 분기에 그대로 걸리기 때문이다.
    /// </summary>
    public class OpenRouterClient : IAiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _endpoint;
        private readonly string _modelName;
        private readonly int? _numCtx;
        private readonly PromptCacheBreakpointPolicy _cacheBreakpointPolicy;
        private readonly OpenRouterRoutingOptions? _routing;

        public string ProviderName => "OpenRouter";
        public string ModelName => _modelName;

        public OpenRouterClient(HttpClient httpClient, string apiKey, string endpoint, string modelName, int? numCtx = null,
            OpenRouterRoutingOptions? routing = null,
            PromptCacheBreakpointPolicy? cacheBreakpointPolicy = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = apiKey;
            _modelName = modelName;
            _numCtx = numCtx;
            _cacheBreakpointPolicy = cacheBreakpointPolicy ?? new PromptCacheBreakpointPolicy();
            _routing = routing;

            var ep = string.IsNullOrWhiteSpace(endpoint) ? "https://openrouter.ai/api/v1" : endpoint.Trim();
            if (ep.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                ep = ep.Substring(0, ep.Length - "/chat/completions".Length);
            }
            _endpoint = ep.TrimEnd('/');
        }

        /// <summary>
        /// 메시지를 만든다. 가변 접미사가 있을 때만 user 내용을 타입 블록 배열로 보내고,
        /// 그 접두사를 전에 보낸 적이 있으면 공유 블록에 cache_control을 찍는다.
        ///
        /// 접미사가 없으면 평문 문자열을 유지한다 - 표현이 바뀌는 것 자체가 접두사를
        /// 바꿔, 접미사 없는 호출들끼리의 캐시를 깨기 때문이다. ClaudeClient와
        /// OpenAiClient가 같은 이유로 같은 판단을 한다.
        ///
        /// 모델 계열별 분기는 두지 않는다. OpenRouter가 블록 단위 표시를 백엔드
        /// 규격으로 번역해 준다 - cache_control은 OpenAI 계열로 갈 때
        /// prompt_cache_breakpoint가 되고, Anthropic·Google로 갈 때는 그대로
        /// 5분 TTL의 cache_control로 전달된다.
        /// </summary>
        private object[] BuildMessages(string systemPrompt, string userPrompt, string? volatileUserSuffix)
        {
            var systemMessage = new { role = "system", content = (object)systemPrompt };

            if (string.IsNullOrWhiteSpace(volatileUserSuffix))
            {
                return new object[]
                {
                    systemMessage,
                    new { role = "user", content = (object)userPrompt }
                };
            }

            object sharedBlock = _cacheBreakpointPolicy.ShouldMarkBreakpoint(systemPrompt, userPrompt)
                ? new { type = "text", text = userPrompt, cache_control = new { type = "ephemeral" } }
                : new { type = "text", text = userPrompt };

            var blocks = new object[]
            {
                sharedBlock,
                new { type = "text", text = volatileUserSuffix }
            };

            return new object[]
            {
                systemMessage,
                new { role = "user", content = (object)blocks }
            };
        }

        public async Task<AiResult> ChatAsync(string systemPrompt, string userPrompt, float temperature, string? effort = null, string? volatileUserSuffix = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) && _endpoint.Contains("openrouter.ai"))
            {
                throw new ArgumentException(
                    "OpenRouter API 키가 설정되지 않았습니다. " +
                    "appsettings.json의 AiSettings:Providers:OpenRouter:ApiKey 에 지정하십시오.");
            }

            var requestBody = new Dictionary<string, object>
            {
                { "model", _modelName },
                { "messages", BuildMessages(systemPrompt, userPrompt, volatileUserSuffix) }
            };

            // reasoning과 temperature는 배타로 보낸다. effort가 없다는 것은 추론
            // 모델을 쓰지 않겠다는 뜻이므로, 그때 reasoning을 얹으면 라우팅이
            // 추론 지원 백엔드로 좁혀질 뿐 얻을 것이 없다. ZaiClient와 같은 판단이다.
            if (!string.IsNullOrWhiteSpace(effort))
            {
                var apiEffort = effort.ToLowerInvariant() switch
                {
                    "low" => "low",
                    "medium" => "medium",
                    "high" => "high",
                    "xhigh" => "high",
                    _ => "medium"
                };
                requestBody.Add("reasoning", new { effort = apiEffort });
            }
            else
            {
                requestBody.Add("temperature", temperature);
            }

            if (_numCtx.HasValue)
            {
                requestBody.Add("max_tokens", _numCtx.Value);
            }

            if (_routing is { IsEmpty: false })
            {
                var preferences = new Dictionary<string, object>();

                if (_routing.Order is { Count: > 0 })
                {
                    preferences.Add("order", _routing.Order);
                }
                if (_routing.AllowFallbacks.HasValue)
                {
                    preferences.Add("allow_fallbacks", _routing.AllowFallbacks.Value);
                }
                if (_routing.RequireParameters.HasValue)
                {
                    preferences.Add("require_parameters", _routing.RequireParameters.Value);
                }

                requestBody.Add("provider", preferences);
            }

            var jsonPayload = JsonSerializer.Serialize(requestBody);
            var requestUri = $"{_endpoint}/chat/completions";
            Log.Debug("OpenRouter API 요청 전송 준비 - URI: {Uri}\n[Payload JSON]:\n{Payload}", requestUri, jsonPayload);

            var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("OpenRouter API HTTP 요청 실패 - StatusCode: {StatusCode} ({ReasonPhrase})\n[Error Response Content]:\n{ErrorContent}", (int)response.StatusCode, response.ReasonPhrase, responseContent);
                throw new HttpRequestException(
                    $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).\n상세 에러 내용: {responseContent}",
                    null,
                    response.StatusCode);
            }

            Log.Debug("OpenRouter API HTTP 응답 수신 완료 - StatusCode: {StatusCode}\n[Response Content]:\n{ResponseContent}", (int)response.StatusCode, responseContent);

            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;

            // OpenRouter는 라우팅 단계 실패를 200 본문 안의 error 객체로 돌려주기도 한다.
            if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
            {
                var errMsg = errorElement.TryGetProperty("message", out var msgElement) ? msgElement.GetString() : "알 수 없는 API 오류";
                Log.Error("OpenRouter API 응답 내 error 감지 - Message: {Message}", errMsg);
                throw new InvalidOperationException($"OpenRouter API 에러 응답 수신: {errMsg}");
            }

            if (!root.TryGetProperty("choices", out var choicesElement)
                || choicesElement.ValueKind != JsonValueKind.Array
                || choicesElement.GetArrayLength() == 0)
            {
                Log.Error("OpenRouter API 응답 choices 속성 누락 또는 빈 배열");
                throw new InvalidOperationException("OpenRouter API 응답 데이터 내에 choices 속성이 존재하지 않거나 비어 있습니다.");
            }

            if (!choicesElement[0].TryGetProperty("message", out var messageElement))
            {
                Log.Error("OpenRouter API 응답 choices[0] 내 message 속성 누락");
                throw new InvalidOperationException("OpenRouter API 응답 choices 내에 message 속성이 존재하지 않습니다.");
            }

            string contentValue = messageElement.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString() ?? string.Empty
                : string.Empty;

            string? reasoningContent = null;
            if (messageElement.TryGetProperty("reasoning", out var reasoningElement) && reasoningElement.ValueKind == JsonValueKind.String)
            {
                reasoningContent = reasoningElement.GetString();
            }

            return new AiResult
            {
                Content = contentValue,
                ThinkingText = reasoningContent
            };
        }
    }
}
