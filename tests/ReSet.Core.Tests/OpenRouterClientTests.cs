using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients;

namespace ReSet.Core.Tests
{
    public class OpenRouterClientTests
    {
        [Fact]
        public async Task ChatAsync_ShouldPostToChatCompletionsWithBearerAndModel()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5");

            await client.ChatAsync("System", "User", 0.2f);

            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", spy.LastRequestUri);
            Assert.Equal("Bearer", spy.LastAuthScheme);
            Assert.Equal("sk-or-test", spy.LastAuthParameter);

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            var root = doc.RootElement;
            Assert.Equal("anthropic/claude-sonnet-5", root.GetProperty("model").GetString());

            var messages = root.GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Equal("System", messages[0].GetProperty("content").GetString());
            Assert.Equal("user", messages[1].GetProperty("role").GetString());
            Assert.Equal("User", messages[1].GetProperty("content").GetString());
        }

        [Fact]
        public async Task ChatAsync_ShouldReturnContentAndReasoningFromResponse()
        {
            var spy = new OpenRouterRequestSpyHandler(
                "{\"choices\":[{\"message\":{\"content\":\"본문\",\"reasoning\":\"생각\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "openai/gpt-5.6");

            var result = await client.ChatAsync("System", "User", 0.2f);

            Assert.Equal("본문", result.Content);
            Assert.Equal("생각", result.ThinkingText);
            Assert.Equal("OpenRouter", client.ProviderName);
            Assert.Equal("openai/gpt-5.6", client.ModelName);
        }

        [Theory]
        [InlineData("low", "low")]
        [InlineData("medium", "medium")]
        [InlineData("high", "high")]
        [InlineData("xhigh", "high")]
        public async Task ChatAsync_WithEffort_ShouldSendReasoningEffortWithoutTemperature(string effort, string expected)
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5");

            await client.ChatAsync("System", "User", 0.2f, effort: effort);

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("reasoning", out var reasoning));
            Assert.Equal(expected, reasoning.GetProperty("effort").GetString());
            Assert.False(root.TryGetProperty("temperature", out _));
        }

        [Fact]
        public async Task ChatAsync_WithoutEffort_ShouldSendTemperatureWithoutReasoning()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5");

            await client.ChatAsync("System", "User", 0.35f);

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            var root = doc.RootElement;
            Assert.Equal(0.35f, root.GetProperty("temperature").GetSingle());
            Assert.False(root.TryGetProperty("reasoning", out _));
        }

        [Fact]
        public async Task ChatAsync_WithNumCtx_ShouldSendMaxTokens()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5", numCtx: 32768);

            await client.ChatAsync("System", "User", 0.2f);

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            Assert.Equal(32768, doc.RootElement.GetProperty("max_tokens").GetInt32());
        }

        [Fact]
        public async Task ChatAsync_WithoutNumCtx_ShouldOmitMaxTokens()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5");

            await client.ChatAsync("System", "User", 0.2f);

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            Assert.False(doc.RootElement.TryGetProperty("max_tokens", out _));
        }

        [Fact]
        public async Task ChatAsync_WithVolatileSuffixOnFirstSend_ShouldSplitBlocksWithoutCacheControl()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5",
                cacheBreakpointPolicy: new PromptCacheBreakpointPolicy());

            await client.ChatAsync("System", "공유 접두사", 0.2f, volatileUserSuffix: "가변 지시");

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content");
            Assert.Equal(JsonValueKind.Array, content.ValueKind);
            Assert.Equal(2, content.GetArrayLength());
            Assert.Equal("공유 접두사", content[0].GetProperty("text").GetString());
            Assert.Equal("가변 지시", content[1].GetProperty("text").GetString());
            Assert.False(content[0].TryGetProperty("cache_control", out _));
        }

        [Fact]
        public async Task ChatAsync_WithSamePrefixOnSecondSend_ShouldMarkCacheControlOnSharedBlock()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5",
                cacheBreakpointPolicy: new PromptCacheBreakpointPolicy());

            await client.ChatAsync("System", "공유 접두사", 0.2f, volatileUserSuffix: "1회차");
            await client.ChatAsync("System", "공유 접두사", 0.2f, volatileUserSuffix: "2회차");

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content");
            Assert.Equal("ephemeral", content[0].GetProperty("cache_control").GetProperty("type").GetString());
            Assert.False(content[1].TryGetProperty("cache_control", out _));
        }

        [Fact]
        public async Task ChatAsync_WithoutVolatileSuffix_ShouldKeepPlainStringContentEvenOnSecondSend()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5",
                cacheBreakpointPolicy: new PromptCacheBreakpointPolicy());

            await client.ChatAsync("System", "공유 접두사", 0.2f);
            await client.ChatAsync("System", "공유 접두사", 0.2f);

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content");
            Assert.Equal(JsonValueKind.String, content.ValueKind);
            Assert.Equal("공유 접두사", content.GetString());
        }

        [Fact]
        public async Task ChatAsync_WithoutRouting_ShouldOmitProviderField()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5");

            await client.ChatAsync("System", "User", 0.2f);

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            Assert.False(doc.RootElement.TryGetProperty("provider", out _));
        }

        [Fact]
        public async Task ChatAsync_WithEmptyRouting_ShouldOmitProviderField()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5",
                routing: new OpenRouterRoutingOptions());

            await client.ChatAsync("System", "User", 0.2f);

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            Assert.False(doc.RootElement.TryGetProperty("provider", out _));
        }

        [Fact]
        public async Task ChatAsync_WithRouting_ShouldSendOnlyConfiguredPreferences()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var routing = new OpenRouterRoutingOptions
            {
                Order = new[] { "anthropic", "google-vertex" },
                AllowFallbacks = false
            };
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5", routing: routing);

            await client.ChatAsync("System", "User", 0.2f);

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            var provider = doc.RootElement.GetProperty("provider");
            var order = provider.GetProperty("order");
            Assert.Equal(2, order.GetArrayLength());
            Assert.Equal("anthropic", order[0].GetString());
            Assert.Equal("google-vertex", order[1].GetString());
            Assert.False(provider.GetProperty("allow_fallbacks").GetBoolean());
            Assert.False(provider.TryGetProperty("require_parameters", out _));
        }

        [Fact]
        public async Task ChatAsync_WithRequireParameters_ShouldSendRequireParameters()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5",
                routing: new OpenRouterRoutingOptions { RequireParameters = true });

            await client.ChatAsync("System", "User", 0.2f);

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            Assert.True(doc.RootElement.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
        }

        [Fact]
        public async Task ChatAsync_WithHttpError_ShouldThrowWithResponseBody()
        {
            var spy = new OpenRouterRequestSpyHandler(
                "{\"error\":{\"message\":\"No endpoints found\"}}", HttpStatusCode.NotFound);
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(
                () => client.ChatAsync("System", "User", 0.2f));

            Assert.Contains("No endpoints found", ex.Message);
            Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        }

        [Fact]
        public async Task ChatAsync_WithErrorObjectInSuccessBody_ShouldThrowInvalidOperation()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"error\":{\"message\":\"Rate limited\"}}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.ChatAsync("System", "User", 0.2f));

            Assert.Contains("Rate limited", ex.Message);
        }

        [Fact]
        public async Task ChatAsync_WithoutApiKey_ShouldThrowArgumentException()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "", "", "anthropic/claude-sonnet-5");

            await Assert.ThrowsAsync<ArgumentException>(() => client.ChatAsync("System", "User", 0.2f));
            Assert.Null(spy.LastRequestContent);
        }

        [Fact]
        public async Task ChatAsync_WithoutChoices_ShouldThrowInvalidOperation()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "anthropic/claude-sonnet-5");

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync("System", "User", 0.2f));
        }

        [Theory]
        [InlineData("https://openrouter.ai/api/v1/chat/completions")]
        [InlineData("https://openrouter.ai/api/v1/")]
        public async Task ChatAsync_WithRedundantEndpointSuffix_ShouldNormalizeUri(string endpoint)
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", endpoint, "anthropic/claude-sonnet-5");

            await client.ChatAsync("System", "User", 0.2f);

            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", spy.LastRequestUri);
        }
    }

    public class OpenRouterRoutingOptionsTests
    {
        [Fact]
        public void Parse_WithNothingConfigured_ReturnsNull()
        {
            Assert.Null(OpenRouterRoutingOptions.Parse(null, null, null));
        }

        [Fact]
        public void Parse_WithEmptyOrderAndBlankFlags_ReturnsNull()
        {
            Assert.Null(OpenRouterRoutingOptions.Parse(new string[0], "", "   "));
        }

        [Fact]
        public void Parse_WithUnparsableFlags_IgnoresThem()
        {
            Assert.Null(OpenRouterRoutingOptions.Parse(null, "yes", "1"));
        }

        [Fact]
        public void Parse_WithOrder_KeepsOrderAndDropsBlankEntries()
        {
            var options = OpenRouterRoutingOptions.Parse(new[] { "anthropic", "  ", "google-vertex", null! }, null, null);

            Assert.NotNull(options);
            Assert.Equal(new[] { "anthropic", "google-vertex" }, options!.Order);
            Assert.Null(options.AllowFallbacks);
            Assert.Null(options.RequireParameters);
        }

        [Fact]
        public void Parse_WithFlags_ReadsBooleans()
        {
            var options = OpenRouterRoutingOptions.Parse(null, "false", "true");

            Assert.NotNull(options);
            Assert.False(options!.AllowFallbacks);
            Assert.True(options.RequireParameters);
            Assert.Null(options.Order);
        }
    }

    public class OpenRouterRequestSpyHandler : HttpMessageHandler
    {
        private readonly string _responseContent;
        private readonly HttpStatusCode _statusCode;

        public string? LastRequestContent { get; private set; }
        public string? LastRequestUri { get; private set; }
        public string? LastAuthScheme { get; private set; }
        public string? LastAuthParameter { get; private set; }

        public OpenRouterRequestSpyHandler(string responseContent, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseContent = responseContent;
            _statusCode = statusCode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            LastAuthScheme = request.Headers.Authorization?.Scheme;
            LastAuthParameter = request.Headers.Authorization?.Parameter;

            if (request.Content != null)
            {
                LastRequestContent = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseContent, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
