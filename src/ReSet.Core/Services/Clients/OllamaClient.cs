using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;

namespace ReSet.Core.Services.Clients
{
    public class OllamaClient : IAiClient
    {
        private readonly OpenAiClient _openAiClient;

        public string ProviderName => "Ollama";
        public string ModelName => _openAiClient.ModelName;

        public OllamaClient(HttpClient httpClient, string endpoint, string modelName, int? numCtx = null)
        {
            var ep = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint.Trim();
            
            // Ollama의 OpenAI 호환 엔드포인트 경로(/v1) 자동 보정
            if (!ep.Contains("/v1", StringComparison.OrdinalIgnoreCase))
            {
                ep = ep.TrimEnd('/') + "/v1";
            }

            // Ollama does not require an API key by default
            _openAiClient = new OpenAiClient(httpClient, string.Empty, ep, modelName, numCtx);
        }

        public async Task<AiResult> ChatAsync(string systemPrompt, string userPrompt, float temperature, string? effort = null, CancellationToken cancellationToken = default)
        {
            var result = await _openAiClient.ChatAsync(systemPrompt, userPrompt, temperature, effort, cancellationToken);
            if (result != null)
            {
                var content = result.Content ?? string.Empty;

                // 1. Gemma 4의 공식 제어 토큰 (<|channel>thought ... <channel|>) 파싱
                int gemmaStart = content.IndexOf("<|channel>thought", StringComparison.OrdinalIgnoreCase);
                if (gemmaStart != -1)
                {
                    int gemmaEnd = content.IndexOf("<channel|>", gemmaStart, StringComparison.OrdinalIgnoreCase);
                    if (gemmaEnd != -1)
                    {
                        int headerLength = 17; // "<|channel>thought"
                        var sub = content.Substring(gemmaStart + headerLength);
                        if (sub.StartsWith("\n")) sub = sub.Substring(1);
                        else if (sub.StartsWith("\r\n")) sub = sub.Substring(2);

                        int actualStart = gemmaStart + headerLength + (content.Substring(gemmaStart + headerLength).Length - sub.Length);
                        var extractedThinking = content.Substring(actualStart, gemmaEnd - actualStart).Trim();
                        result.ThinkingText = extractedThinking;

                        var beforeThink = content.Substring(0, gemmaStart);
                        var afterThink = content.Substring(gemmaEnd + 10); // "<channel|>" length
                        result.Content = (beforeThink + afterThink).Trim();
                        content = result.Content; // 갱신
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

                        // <think>...</think> 블록 및 내부 텍스트 전체를 본문에서 제거
                        var beforeThink = content.Substring(0, startTag);
                        var afterThink = content.Substring(endTag + 8);
                        result.Content = (beforeThink + afterThink).Trim();
                    }
                }
            }
            return result ?? new AiResult();
        }
    }
}
