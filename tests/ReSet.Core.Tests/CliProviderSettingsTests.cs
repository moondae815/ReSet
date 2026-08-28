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
        //
        // 모델마다 목록이 갈리는 이유는 어느 백엔드가 그 모델을 서빙하는지가 다르기
        // 때문이다. 공유 목록이던 시절 실제로 난 사고: glm 캐시읽기 최저가인
        // sail-research는 deepseek를 서빙하지 않아, 그 이름을 1순위로 올리면
        // AllowFallbacks=false와 맞물려 Critic이 404 "No endpoints found"로 죽었다.
        // 아래 표에서 그 이름이 glm의 1순위로 올라가 있는 것이 ByModel이 이 충돌을
        // 없앴다는 증거다 - 이 항목은 glm 호출에만 적용되어 Critic에 닿지 않는다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_PinOpenRouterBackendsPerModel(string relativePath)
        {
            var configuration = Load(relativePath);

            foreach (var (model, expected) in PinnedBackendsPerModel)
            {
                var routing = ReSet.Cli.Program.ReadOpenRouterRouting(configuration, "OpenRouter", model);

                Assert.NotNull(routing);
                Assert.Equal(expected, routing!.Order);
            }
        }

        // 설정 파일이 고정하는 모델별 백엔드 순서. 근거(양자화·컨텍스트·캐시읽기
        // 단가·가동률)는 appsettings.json의 ByModel 주석에 실측값으로 적혀 있다.
        //
        // glm-5.3-flash에 streamlake가 없는 것은 빠뜨린 게 아니다 - 그 백엔드는
        // 이 모델을 서빙하지 않는다. 목록이 모델마다 갈리는 이유가 이것이다.
        //
        // glm-5.3의 목록이 한 줄인 것도 빠뜨린 게 아니다 - 이 모델을 서빙하는 곳이
        // Z.AI 본사 하나뿐이라 2순위로 적을 대상이 없다. 그래도 항목이 있어야 하는
        // 것은, 없으면 Default(streamlake·novita)로 도는데 그 둘이 서빙하지 않아
        // AllowFallbacks=false와 맞물려 404로 즉시 죽기 때문이다.
        public static readonly (string Model, string[] Order)[] PinnedBackendsPerModel =
        {
            ("z-ai/glm-5.2", new[] { "sail-research", "novita" }),
            ("z-ai/glm-5.3", new[] { "z-ai" }),
            ("deepseek/deepseek-v4-pro-0813", new[] { "gmicloud", "deepseek" }),
            ("z-ai/glm-5.3-flash", new[] { "novita", "z-ai" }),
            ("deepseek/deepseek-v4-flash-0731", new[] { "streamlake", "deepinfra" })
        };

        // 모델별 항목은 Order만 적고 AllowFallbacks는 Default에 한 번만 적는다.
        // 설정 파일에서도 그 상속이 실제로 성립하는지 본다 - 성립하지 않으면 모델별
        // 호출에서만 목록 밖 이동이 조용히 열린다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_PerModelRouting_KeepsFallbacksClosed(string relativePath)
        {
            var configuration = Load(relativePath);

            foreach (var (model, _) in PinnedBackendsPerModel)
            {
                var routing = ReSet.Cli.Program.ReadOpenRouterRouting(configuration, "OpenRouter", model);

                Assert.NotNull(routing);
                Assert.False(routing!.AllowFallbacks, $"{model}의 AllowFallbacks가 닫혀 있지 않습니다");
                Assert.Null(routing.RequireParameters);
            }
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

        // 검증기 CLI는 같은 로직의 복사본을 갖는다. 한쪽만 모델별 형식을 알면,
        // 새 형식 설정에서 검증기는 Routing 구획을 보고도 평면 Order를 찾지 못해
        // null을 돌려준다 - 라우팅이 오류 없이 사라지고 백엔드가 다시 흔들린다.
        [Fact]
        public void ValidatorCli_ReadOpenRouterRouting_ResolvesByModelLikeAnalyzerCli()
        {
            var configuration = Load("src/ReSet.Validator.Cli/appsettings.json");

            var routing = ReSet.Validator.Cli.Program.ReadOpenRouterRouting(
                configuration, "OpenRouter", "deepseek/deepseek-v4-pro-0813");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "gmicloud", "deepseek" }, routing!.Order);
            Assert.False(routing.AllowFallbacks);
        }

        // ── 모델별 라우팅 ────────────────────────────────────────────────────
        // Routing은 provider 단위 구획이라 Actor/Critic/Consolidator가 셋 다
        // OpenRouter면 같은 목록을 공유한다. 모델마다 서빙하는 백엔드와 캐시 읽기
        // 단가가 달라, 한 목록으로는 한쪽 모델에만 맞출 수 있다(실측: glm 캐시읽기
        // 최저가 sail-research는 deepseek를 아예 서빙하지 않아 Critic이 404로 죽는다).
        // ByModel은 모델 ID로 목록을 갈라 이 충돌을 없앤다.
        [Fact]
        public void ReadOpenRouterRouting_WithByModelEntry_UsesModelSpecificOrder()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiSettings:Providers:OpenRouter:Routing:Default:Order:0"] = "streamlake",
                    ["AiSettings:Providers:OpenRouter:Routing:ByModel:deepseek/deepseek-v4-pro-0813:Order:0"] = "streamlake",
                    ["AiSettings:Providers:OpenRouter:Routing:ByModel:deepseek/deepseek-v4-pro-0813:Order:1"] = "deepseek"
                })
                .Build();

            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(
                configuration, "OpenRouter", "deepseek/deepseek-v4-pro-0813");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "streamlake", "deepseek" }, routing!.Order);
        }

        // 목록에 없는 모델은 Default로 떨어진다. 모델을 바꿔 끼웠을 때 라우팅이
        // 통째로 사라져 기본 라우팅으로 도는 것보다, 공용 목록으로 도는 편이 낫다.
        [Fact]
        public void ReadOpenRouterRouting_WithUnlistedModel_FallsBackToDefault()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiSettings:Providers:OpenRouter:Routing:Default:Order:0"] = "streamlake",
                    ["AiSettings:Providers:OpenRouter:Routing:Default:Order:1"] = "novita",
                    ["AiSettings:Providers:OpenRouter:Routing:ByModel:z-ai/glm-5.2:Order:0"] = "sail-research"
                })
                .Build();

            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(
                configuration, "OpenRouter", "openai/gpt-5.6");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "streamlake", "novita" }, routing!.Order);
        }

        // ByModel 항목은 Default를 통째로 대체하지 않고 항목 단위로 덮어쓴다.
        // 대체 방식이면 Order만 적은 항목에서 AllowFallbacks가 조용히 null이 되어,
        // 이 구획이 막으려던 바로 그 사고(fp4 백엔드로의 무언의 이동)가 다시 열린다.
        [Fact]
        public void ReadOpenRouterRouting_ByModelEntry_InheritsUnsetFlagsFromDefault()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiSettings:Providers:OpenRouter:Routing:Default:Order:0"] = "streamlake",
                    ["AiSettings:Providers:OpenRouter:Routing:Default:AllowFallbacks"] = "false",
                    ["AiSettings:Providers:OpenRouter:Routing:Default:RequireParameters"] = "true",
                    ["AiSettings:Providers:OpenRouter:Routing:ByModel:z-ai/glm-5.2:Order:0"] = "novita"
                })
                .Build();

            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(
                configuration, "OpenRouter", "z-ai/glm-5.2");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "novita" }, routing!.Order);
            Assert.False(routing.AllowFallbacks);
            Assert.True(routing.RequireParameters);
        }

        // 모델 ID의 대소문자는 OpenRouter가 구분하지 않는다. 설정 쪽만 구분하면
        // 목록을 적어 두고도 조용히 Default로 떨어진다.
        [Fact]
        public void ReadOpenRouterRouting_MatchesModelIdCaseInsensitively()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiSettings:Providers:OpenRouter:Routing:Default:Order:0"] = "novita",
                    ["AiSettings:Providers:OpenRouter:Routing:ByModel:z-ai/glm-5.2:Order:0"] = "streamlake"
                })
                .Build();

            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(
                configuration, "OpenRouter", "Z-AI/GLM-5.2");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "streamlake" }, routing!.Order);
        }

        // 하위호환: ByModel도 Default도 없이 Routing 바로 아래에 항목을 적은
        // 기존 형식은 모델명을 넘겨도 그대로 읽혀야 한다.
        [Fact]
        public void ReadOpenRouterRouting_WithFlatLegacyShape_IgnoresModelName()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiSettings:Providers:OpenRouter:Routing:Order:0"] = "digitalocean",
                    ["AiSettings:Providers:OpenRouter:Routing:AllowFallbacks"] = "false"
                })
                .Build();

            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(
                configuration, "OpenRouter", "z-ai/glm-5.2");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "digitalocean" }, routing!.Order);
            Assert.False(routing.AllowFallbacks);
        }
    }
}
