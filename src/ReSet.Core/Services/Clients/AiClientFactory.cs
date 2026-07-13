using System;
using System.Net.Http;

namespace ReSet.Core.Services.Clients
{
    public static class AiClientFactory
    {
        public static IAiClient CreateClient(string provider, string modelName, string apiKey, string endpoint, HttpClient? httpClient = null, int? numCtx = null)
        {
            var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(300) };

            if (string.IsNullOrWhiteSpace(provider))
            {
                throw new ArgumentException("AI Provider가 지정되지 않았습니다.", nameof(provider));
            }

            return provider.ToLowerInvariant() switch
            {
                "openai" => new OpenAiClient(client, apiKey, endpoint, modelName, numCtx),
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
