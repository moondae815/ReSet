using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // 품질 하한은 이름이 아니라 성질로 지킨다. 이 선언이 사라지면 fp4·unknown
        // 백엔드가 말없이 후보에 들어오고, 명세서 품질이 조용히 갈린다.
        // Default가 Order를 가지면 안 되는 이유는 따로다 - 그것은 "다른 모델의
        // 목록"을 남의 모델에 물려주는 자리이고, 그렇게 물려받은 백엔드가 그 모델을
        // 서빙하지 않으면 404 "No endpoints found"로 즉시 죽는다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_OpenRouterDefault_DeclaresQuantizationFloorWithoutOrder(string relativePath)
        {
            var configuration = Load(relativePath);
            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(configuration, "OpenRouter");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "fp8" }, routing!.Quantizations);
            Assert.True(routing.AllowFallbacks, "미등록 모델이 404로 죽지 않으려면 폴백이 열려 있어야 합니다");
            Assert.Null(routing.Order);
        }

        // ByModel 키는 네임스페이스가 붙은 OpenRouter 모델 ID여야 한다. 네임스페이스를
        // 빼면 어느 벤더로 풀릴지가 OpenRouter의 판단에 달려 재현성이 없다.
        // 낡은 항목의 자동 탐지는 이 저장소에서 불가능하다 - OpenRouter 모델은
        // gitignore된 appsettings.local.json에만 살아, 커밋된 설정과 대조할 수 없다.
        // 표가 한 줄이라 사람이 지우는 것으로 감당한다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_ByModelEntries_AreNamespacedWithNonEmptyOrder(string relativePath)
        {
            var byModel = Load(relativePath)
                .GetSection("AiSettings:Providers:OpenRouter:Routing:ByModel");

            Assert.True(byModel.Exists(), $"{relativePath}에 ByModel 구획이 없습니다");

            foreach (var entry in byModel.GetChildren())
            {
                Assert.Contains("/", entry.Key);

                var order = entry.GetSection("Order").GetChildren()
                    .Select(child => child.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();

                Assert.True(
                    order.Length > 0,
                    $"{relativePath}의 ByModel:{entry.Key}에 Order가 없습니다 - " +
                    "Order를 적지 않을 항목이면 항목째 지우십시오(Default가 그 일을 합니다)");
            }
        }

        // 모델별 항목은 Order만 적는다. 설정 파일에서도 그 상속이 실제로 성립하는지
        // 본다 - 성립하지 않으면 그 모델 호출에서만 양자화 하한이 조용히 사라진다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_PerModelRouting_InheritsQuantizationFloor(string relativePath)
        {
            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(
                Load(relativePath), "OpenRouter", "z-ai/glm-5.3");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "gmicloud/fp8", "baseten/fp8" }, routing!.Order);
            Assert.Equal(new[] { "fp8" }, routing.Quantizations);
            Assert.True(routing.AllowFallbacks);
        }

        // AllowFallbacks=false는 "이 목록 밖으로 넘어가지 말라"는 뜻이므로 목록이 비어
        // 있으면 갈 곳을 말하지 않고 길만 막는 요청이 된다. 두 설정 파일 어느 쪽에서든
        // Order를 지우면서 이 값을 false로 남겨 두는 조합을 막는다.
        //
        // [현재 상태] Default.AllowFallbacks가 지금 두 설정 모두 true라, 아래
        // if 블록의 전건(AllowFallbacks == false)이 거짓이라 본문이 돌지 않는다.
        // 즉 이 검사는 지금 잠들어 있다 - 누가 나중에 AllowFallbacks를 false로
        // 되돌리면서 Order를 비우는 회귀가 나면 그때 깨어나 잡는다. Assert.NotNull
        // 한 줄을 조건 밖에 둔 것은 "잠든 채로 초록"과 "라우팅 구획 자체가
        // 사라져도 초록"을 구분하기 위해서다 - 조건문 안 단언만 있으면 후자도
        // 통과해 버려, 이 검사가 아무것도 안 보는 상태를 코드에서 알아챌 수 없다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_OpenRouterRouting_NeverBlocksFallbacksWithoutOrder(string relativePath)
        {
            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(Load(relativePath), "OpenRouter");

            Assert.NotNull(routing);

            if (routing!.AllowFallbacks == false)
            {
                Assert.NotNull(routing.Order);
                Assert.NotEmpty(routing.Order!);
            }
        }

        // 두 설정 파일이 ByModel 항목 집합에서 서로 갈라지는 것을 잡는다. 옛
        // PinnedBackendsPerModel 표는 두 파일이 같은 표의 부분집합이자 상위집합이어야
        // 한다는 사실로 이 어긋남을 간접적으로 막았다 - 표를 폐기하면서 그 부수
        // 효과도 함께 없어졌다. 한쪽에만 새 ByModel 항목이 구조적으로 유효하게
        // (네임스페이스 있고 Order 비지 않게) 추가되면, 다른 개별 검사는 그 항목의
        // 존재 자체를 요구하지 않으므로 아무것도 잡지 못한다. 이 검사가 그 자리를
        // 직접 막는다.
        [Fact]
        public void AppSettings_BothCliConfigs_DeclareIdenticalOpenRouterRouting()
        {
            var cli = Load("src/ReSet.Cli/appsettings.json")
                .GetSection("AiSettings:Providers:OpenRouter:Routing");
            var validator = Load("src/ReSet.Validator.Cli/appsettings.json")
                .GetSection("AiSettings:Providers:OpenRouter:Routing");

            AssertRoutingSectionsAreIdentical(cli, validator);
        }

        private static void AssertRoutingSectionsAreIdentical(IConfigurationSection cli, IConfigurationSection validator)
        {
            var cliDefault = cli.GetSection("Default");
            var validatorDefault = validator.GetSection("Default");

            Assert.Equal(
                ReadArray(cliDefault, "Quantizations"),
                ReadArray(validatorDefault, "Quantizations"));
            Assert.Equal(cliDefault["AllowFallbacks"], validatorDefault["AllowFallbacks"]);
            Assert.Equal(
                ReadArray(cliDefault, "Order"),
                ReadArray(validatorDefault, "Order"));

            var cliByModel = cli.GetSection("ByModel");
            var validatorByModel = validator.GetSection("ByModel");

            var cliKeys = cliByModel.GetChildren().Select(c => c.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
            var validatorKeys = validatorByModel.GetChildren().Select(c => c.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();

            Assert.Equal(
                cliKeys,
                validatorKeys);

            foreach (var key in cliKeys)
            {
                Assert.Equal(
                    ReadArray(cliByModel.GetSection(key), "Order"),
                    ReadArray(validatorByModel.GetSection(key), "Order"));
            }
        }

        private static string[] ReadArray(IConfigurationSection section, string key) =>
            section.GetSection(key).GetChildren()
                .Select(child => child.Value ?? string.Empty)
                .ToArray();

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

        // 설정의 Quantizations가 실제로 읽히는지 본다. 읽히지 않으면 설정 파일만
        // 바뀌고 요청은 그대로다.
        [Fact]
        public void ReadOpenRouterRouting_WithConfiguredQuantizations_ReadsThem()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiSettings:Providers:OpenRouter:Routing:Default:Quantizations:0"] = "fp8",
                    ["AiSettings:Providers:OpenRouter:Routing:Default:AllowFallbacks"] = "true",
                    ["AiSettings:Providers:OpenRouter:Routing:ByModel:z-ai/glm-5.3:Order:0"] = "gmicloud/fp8"
                })
                .Build();

            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(
                configuration, "OpenRouter", "z-ai/glm-5.3");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "gmicloud/fp8" }, routing!.Order);
            Assert.Equal(new[] { "fp8" }, routing.Quantizations);
            Assert.True(routing.AllowFallbacks);
        }

        // 검증기 CLI는 같은 로직의 복사본을 갖는다. 한쪽만 고치면 검증기 호출에서만
        // 양자화 하한이 사라져, fp4 백엔드가 L2 리뷰를 조용히 맡게 된다.
        [Fact]
        public void ValidatorCli_ReadOpenRouterRouting_ReadsQuantizationsLikeAnalyzerCli()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiSettings:Providers:OpenRouter:Routing:Default:Quantizations:0"] = "fp8",
                    ["AiSettings:Providers:OpenRouter:Routing:Default:AllowFallbacks"] = "true"
                })
                .Build();

            var routing = ReSet.Validator.Cli.Program.ReadOpenRouterRouting(
                configuration, "OpenRouter", "z-ai/glm-5.3");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "fp8" }, routing!.Quantizations);
            Assert.True(routing.AllowFallbacks);
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
