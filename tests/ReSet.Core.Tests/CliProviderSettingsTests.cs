using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ReSet.Core.Tests
{
    public class CliProviderSettingsTests
    {
        private static IConfiguration Load(string relativePath)
        {
            var fullPath = Path.Combine(RepoPaths.FindRepoRoot(), relativePath);
            Assert.True(File.Exists(fullPath), $"설정 파일을 찾을 수 없습니다: {fullPath}");

            // 기존 appsettings.json은 주석을 쓴다. IConfiguration의 JSON 공급자는 이를 허용한다.
            return new ConfigurationBuilder()
                .AddJsonFile(fullPath, optional: false)
                .Build();
        }

        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_DeclareAllThreeCliProviders(string relativePath)
        {
            var configuration = Load(relativePath);

            Assert.Equal("claude", configuration["AiSettings:Providers:claude-cli:Command"]);
            Assert.Equal("codex", configuration["AiSettings:Providers:codex-cli:Command"]);
            Assert.Equal("agy", configuration["AiSettings:Providers:agy-cli:Command"]);
        }

        // CLI provider는 API 키를 갖지 않는다. 빈 키라도 넣어두면 다른 곳의
        // "키가 있으니 API provider겠지" 판단을 흐린다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_CliProvidersDeclareNoApiKey(string relativePath)
        {
            var configuration = Load(relativePath);

            Assert.Null(configuration["AiSettings:Providers:claude-cli:ApiKey"]);
            Assert.Null(configuration["AiSettings:Providers:codex-cli:ApiKey"]);
            Assert.Null(configuration["AiSettings:Providers:agy-cli:ApiKey"]);
        }

        // OpenRouter는 모델 ID에 네임스페이스가 붙어 다른 provider와 설정 모양이
        // 다르지 않지만, 엔드포인트 기본값이 비어 있으면 클라이언트가 openrouter.ai로
        // 되돌아가는 대신 빈 URI를 만든다. 두 설정 파일 모두에서 고정한다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_DeclareOpenRouterProvider(string relativePath)
        {
            var configuration = Load(relativePath);

            Assert.Equal("https://openrouter.ai/api/v1", configuration["AiSettings:Providers:OpenRouter:Endpoint"]);
            Assert.Equal(string.Empty, configuration["AiSettings:Providers:OpenRouter:ApiKey"]);
        }

        // 저장소 기본값은 백엔드를 고정한다. 고정하지 않으면 회차마다 다른 백엔드로
        // 가서 1회차에 쓴 프롬프트 캐시를 2회차가 읽지 못한다(실측 z-ai/glm-5.2,
        // 접두사 10,220토큰: 미고정 적중 0에 $0.00776, 고정 시 적중 10,112에 $0.00171).
        // 순서까지 고정하는 것은 1순위가 막혔을 때 fp4 양자화 백엔드로 조용히
        // 떨어지지 않게 하기 위해서다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_PinOpenRouterBackendsInOrder(string relativePath)
        {
            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(Load(relativePath), "OpenRouter");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "digitalocean", "streamlake" }, routing!.Order);
            Assert.False(routing.AllowFallbacks);
            Assert.Null(routing.RequireParameters);
        }

        // AllowFallbacks=false는 "이 목록 밖으로 넘어가지 말라"는 뜻이므로 목록이 비어
        // 있으면 갈 곳을 말하지 않고 길만 막는 요청이 된다. 두 설정 파일 어느 쪽에서든
        // Order를 지우면서 이 값을 false로 남겨 두는 조합을 막는다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_OpenRouterRouting_NeverBlocksFallbacksWithoutOrder(string relativePath)
        {
            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(Load(relativePath), "OpenRouter");

            if (routing?.AllowFallbacks == false)
            {
                Assert.NotNull(routing.Order);
                Assert.NotEmpty(routing.Order!);
            }
        }

        [Fact]
        public void ReadOpenRouterRouting_WithConfiguredOrder_ReadsArrayAndFlags()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiSettings:Providers:OpenRouter:Routing:Order:0"] = "anthropic",
                    ["AiSettings:Providers:OpenRouter:Routing:Order:1"] = "google-vertex",
                    ["AiSettings:Providers:OpenRouter:Routing:AllowFallbacks"] = "false",
                    ["AiSettings:Providers:OpenRouter:Routing:RequireParameters"] = "true"
                })
                .Build();

            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(configuration, "OpenRouter");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "anthropic", "google-vertex" }, routing!.Order);
            Assert.False(routing.AllowFallbacks);
            Assert.True(routing.RequireParameters);
        }

        // 다른 provider에는 이 구획이 없다. 없는 구획을 읽어도 조용히 null이어야 한다.
        [Fact]
        public void ReadOpenRouterRouting_WithoutRoutingSection_ReturnsNull()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiSettings:Providers:Claude:ApiKey"] = "sk-test"
                })
                .Build();

            Assert.Null(ReSet.Cli.Program.ReadOpenRouterRouting(configuration, "Claude"));
        }
    }
}
