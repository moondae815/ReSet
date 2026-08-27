using System;
using System.Net.Http;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Services.Clients
{
    public static class AiClientFactory
    {
        /// <summary>
        /// 우리 기계의 자원으로 모델을 돌리는 provider인가. AST 분할 파이프라인 라우팅과
        /// 1단계 온도 고정, &lt;think&gt; 유도 프롬프트의 대상을 가른다.
        /// Ollama Cloud(<c>ollama-cloud</c>)는 프로토콜이 로컬 Ollama와 같아도 원격
        /// GPU를 쓰므로 여기에 들지 않는다.
        /// </summary>
        public static bool IsLocalProvider(string provider)
        {
            var p = provider?.ToLowerInvariant();
            return p == "ollama" || p == "local-openai" || p == "mlx" || p == "vllm";
        }

        /// <summary>
        /// 단일 GPU를 공유해 동시 실행이 순차보다 느려지거나 메모리가 터질 수 있는
        /// 로컬 provider인가. <see cref="IsLocalProvider"/>와 달리 vLLM은 제외한다 —
        /// vLLM은 연속 배칭(continuous batching)이 강점이라 동시 요청을 묶어 처리량을
        /// 올리므로, "동시성을 낮추라"는 조언이 다른 로컬 provider와 반대로 뒤집힌다.
        /// StepConcurrency 경고처럼 "동시 실행을 줄이라"는 조언이 맞는지 여부를
        /// 판정할 때만 쓰고, 청킹 파이프라인 등 다른 로컬 전용 분기는 계속
        /// <see cref="IsLocalProvider"/>를 써야 한다.
        /// </summary>
        public static bool IsSingleGpuLocalProvider(string provider)
        {
            var p = provider?.ToLowerInvariant();
            return p == "ollama" || p == "local-openai" || p == "mlx";
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
            string? command = null,
            OpenRouterRoutingOptions? openRouterRouting = null)
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
                // Ollama Cloud는 로컬과 같은 네이티브 /api/chat을 쓰므로 클라이언트를
                // 공유한다. 다른 것은 Bearer 인증, 기본 엔드포인트, 그리고 로컬로
                // 분류되지 않는다는 점뿐이다.
                "ollama-cloud" => new OllamaClient(client, endpoint, modelName, numCtx, apiKey, isCloud: true),
                "claude" => new ClaudeClient(client, apiKey, endpoint, modelName),
                "anthropic" => new ClaudeClient(client, apiKey, endpoint, modelName),
                "google" => new GoogleClient(client, apiKey, endpoint, modelName),
                // OpenRouter는 OpenAI 호환 규격이지만 OpenAiClient를 공유하지 않는다 —
                // 그쪽은 모델명에 gpt-5가 들어가면 Responses API로 분기하는데,
                // OpenRouter의 모델 ID는 openai/gpt-5.6처럼 네임스페이스가 붙어
                // 그 분기에 그대로 걸린다. 원격 HTTP API이므로 로컬·CLI 어느 분류에도
                // 들지 않는다.
                "openrouter" => new OpenRouterClient(client, apiKey, endpoint, modelName, numCtx, openRouterRouting),
                "z.ai" => new ZaiClient(client, apiKey, endpoint, modelName),
                "zai" => new ZaiClient(client, apiKey, endpoint, modelName),
                _ => throw new NotSupportedException($"지원되지 않는 AI Provider입니다: {provider}")
            };
        }
    }
}
