using System;
using System.Net.Http;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Services.Clients
{
    public static class AiClientFactory
    {
        public static bool IsLocalProvider(string provider)
        {
            var p = provider?.ToLowerInvariant();
            return p == "ollama" || p == "local-openai" || p == "mlx" || p == "vllm";
        }

        /// <summary>
        /// 로컬에 설치된 CLI 코딩 에이전트를 백엔드로 쓰는 provider인가.
        /// API 키가 필요 없고, 무인 배치 모드에서는 사용할 수 없다.
        /// </summary>
        public static bool IsCliProvider(string provider)
        {
            var p = provider?.ToLowerInvariant();
            return p == "claude-cli" || p == "codex-cli" || p == "agy-cli";
        }

        public static IAiClient CreateClient(
            string provider,
            string modelName,
            string apiKey,
            string endpoint,
            HttpClient? httpClient = null,
            int? numCtx = null,
            string? command = null)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                throw new ArgumentException("AI Provider가 지정되지 않았습니다.", nameof(provider));
            }

            var normalizedProvider = provider.ToLowerInvariant();

            // CLI provider는 HttpClient를 쓰지 않는다. 아래에서 새로 만들기 전에 분기한다.
            // 타임아웃은 호출부가 AiSettings:TimeoutSeconds로 구성한 HttpClient에서
            // 읽어, 설정이 한 곳에서만 관리되도록 한다.
            if (IsCliProvider(normalizedProvider))
            {
                var timeout = httpClient?.Timeout ?? TimeSpan.FromSeconds(300);

                return normalizedProvider switch
                {
                    "claude-cli" => new ClaudeCliClient(command ?? "claude", modelName, timeout),
                    "codex-cli" => new CodexCliClient(command ?? "codex", modelName, timeout),
                    "agy-cli" => new AntigravityCliClient(command ?? "agy", modelName, timeout),
                    _ => throw new NotSupportedException($"지원되지 않는 AI Provider입니다: {provider}")
                };
            }

            var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(300) };

            return normalizedProvider switch
            {
                "openai" => new OpenAiClient(client, apiKey, endpoint, modelName, numCtx, "OpenAI"),
                "local-openai" => new OpenAiClient(client, apiKey, endpoint, modelName, numCtx, "local-openai"),
                "mlx" => new OpenAiClient(client, apiKey, endpoint, modelName, numCtx, "mlx"),
                "vllm" => new OpenAiClient(client, apiKey, endpoint, modelName, numCtx, "vllm"),
                "ollama" => new OllamaClient(client, endpoint, modelName, numCtx),
                "claude" => new ClaudeClient(client, apiKey, endpoint, modelName),
                "anthropic" => new ClaudeClient(client, apiKey, endpoint, modelName),
                "google" => new GoogleClient(client, apiKey, endpoint, modelName),
                "z.ai" => new ZaiClient(client, apiKey, endpoint, modelName),
                "zai" => new ZaiClient(client, apiKey, endpoint, modelName),
                _ => throw new NotSupportedException($"지원되지 않는 AI Provider입니다: {provider}")
            };
        }
    }
}
