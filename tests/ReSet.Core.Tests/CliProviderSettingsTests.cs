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

        // 폐쇄 가중치 모델은 벤더가 직접 서빙해 /endpoints가 양자화를 unknown으로만
        // 보고한다. 그래서 Default의 fp8 하한을 그대로 물려받으면 후보가 0이 되어
        // 404 "No endpoints found"로 죽는다(실측 2026-09-10, routing_funnel).
        // 이 두 항목의 Quantizations 재정의를 지우면 그 모델 호출만 조용히 그 404로
        // 돌아가고, 설정 파일에는 아무 이상도 남지 않는다 - 그 되돌림을 여기서 잡는다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json", "openai/gpt-5.6-sol", "openai", "azure")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json", "openai/gpt-5.6-sol", "openai", "azure")]
        [InlineData("src/ReSet.Cli/appsettings.json", "qwen/qwen3.8-max-0902", "alibaba", null)]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json", "qwen/qwen3.8-max-0902", "alibaba", null)]
        public void AppSettings_ClosedWeightModels_LowerQuantizationFloorToUnknown(
            string relativePath, string modelId, string firstBackend, string? secondBackend)
        {
            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(
                Load(relativePath), "OpenRouter", modelId);

            Assert.NotNull(routing);
            Assert.Equal(
                new[] { "unknown" },
                routing!.Quantizations);

            var expectedOrder = secondBackend is null
                ? new[] { firstBackend }
                : new[] { firstBackend, secondBackend };
            Assert.Equal(expectedOrder, routing.Order);

            // 하한만 내리고 폴백까지 닫아 버리면 Order 밖으로 못 나가 가용성이 사라진다.
            Assert.True(routing.AllowFallbacks);
        }

        // [한 판만 좁히기 - scripts/run-plan-only-job.sh 의 PLANONLY_OPENROUTER_ONLY_BACKEND]
        // 저장소 기본값은 위 시험이 잠근 대로 가용성을 위해 폴백을 연다. 그런데 측정 판은
        // 백엔드가 섞이면 결론이 뒤집히고(Azure 는 같은 모델에 2.5~3 배 단가다), 그래서
        // 스크립트가 환경변수로 그 판만 한 백엔드에 못박는다. 이 시험은 그 덮어쓰기가
        // 실제 설정 파일 위에서 실제 환경변수 공급자로 성립하는지를 잠근다.
        //
        // 환경변수로는 배열 칸을 지울 수 없어 기본값의 뒤 칸(`azure`)을 공백으로 덮는다 -
        // OpenRouterRoutingOptions.Parse 가 빈 칸을 걸러 낸다. 빈 문자열도 이 환경에서는
        // 같은 결과였지만(2026-09-13 변이) 셸마다 빈 값 환경변수 취급이 달라 공백으로 통일한다.
        [Fact]
        public void AppSettings_PerRunEnvironmentOverride_PinsGptSolToOneBackendWithoutFallback()
        {
            const string prefix = "RESET_ROUTING_PIN_TEST_";
            const string byModel = prefix + "AiSettings__Providers__OpenRouter__Routing__ByModel__openai/gpt-5.6-sol__";
            var variables = new Dictionary<string, string>
            {
                [byModel + "Order__0"] = "openai",
                [byModel + "Order__1"] = " ",
                [byModel + "Order__2"] = " ",
                [byModel + "Order__3"] = " ",
                [byModel + "AllowFallbacks"] = "false",
            };

            try
            {
                foreach (var (name, value) in variables) Environment.SetEnvironmentVariable(name, value);

                var configuration = new ConfigurationBuilder()
                    .AddJsonFile(Path.Combine(RepoPaths.FindRepoRoot(), "src/ReSet.Cli/appsettings.json"), optional: false)
                    .AddEnvironmentVariables(prefix)
                    .Build();

                var routing = ReSet.Cli.Program.ReadOpenRouterRouting(configuration, "OpenRouter", "openai/gpt-5.6-sol");

                Assert.NotNull(routing);
                Assert.Equal(new[] { "openai" }, routing!.Order);
                Assert.False(routing.AllowFallbacks);
                Assert.Equal(new[] { "unknown" }, routing.Quantizations);
            }
            finally
            {
                foreach (var name in variables.Keys) Environment.SetEnvironmentVariable(name, null);
            }
        }

        // 열린 가중치 모델에서는 하한이 그대로 의미가 있다. kimi-k3은 백엔드 19개 중
        // fp8이 baseten 하나뿐이라, Default를 물려받는 것만으로 후보가 그 하나로 정해진다.
        // 여기에 Quantizations를 적어 넣는 "일관성" 수정이 들어오면 하한이 풀린다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_OpenWeightPinnedModel_KeepsInheritedQuantizationFloor(string relativePath)
        {
            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(
                Load(relativePath), "OpenRouter", "moonshotai/kimi-k3");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "baseten/fp8" }, routing!.Order);
            Assert.Equal(new[] { "fp8" }, routing.Quantizations);
            Assert.True(routing.AllowFallbacks);
        }

        // AllowFallbacks=false는 "이 목록 밖으로 넘어가지 말라"는 뜻이므로 목록이 비어
        // 있으면 갈 곳을 말하지 않고 길만 막는 요청이 된다. 두 설정 파일 어느 쪽에서든
        // Order를 지우면서 이 값을 false로 남겨 두는 조합을 막는다.
        //
        // 조건문이 아니라 총함수 단언(AllowFallbacks가 true거나, false면 Order가
        // 비지 않아야 한다)으로 적는다 - 예전에는 이 명제를 if로 쪼개 조건부 본문에
        // 넣었는데, AppSettings_OpenRouterDefault_DeclaresQuantizationFloorWithoutOrder가
        // 두 파일 모두에서 AllowFallbacks==true를 이미 못박고 있어 그 if 본문은 이
        // 검사 파일 안에서 도달 불가였다(데이터를 바꿔도 깨어나지 않고, 그 못박기
        // 자체를 지워야 하는데 그 변경은 형제 검사가 먼저 잡는다). 총함수 형태는
        // 죽은 가지 없이 같은 명제를 매 실행마다 실제로 평가한다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_OpenRouterRouting_NeverBlocksFallbacksWithoutOrder(string relativePath)
        {
            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(Load(relativePath), "OpenRouter");

            Assert.NotNull(routing);
            Assert.True(
                routing!.AllowFallbacks == true || routing.Order?.Count > 0,
                "AllowFallbacks가 false인데 Order가 비어 있습니다 - 갈 곳 없이 길만 막습니다");
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

        // 잎 노드 (경로, 값) 쌍을 재귀로 모아 두 구획을 통째로 비교한다. 이러면
        // Order·Quantizations·AllowFallbacks·RequireParameters처럼 이름을 아는
        // 필드뿐 아니라, 나중에 추가되는 키도 이 검사가 따라간다 - 개별 필드를
        // 하나씩 나열하는 방식은 새 키가 한쪽에만 추가돼도 조용히 통과시킨다.
        private static void AssertRoutingSectionsAreIdentical(IConfigurationSection cli, IConfigurationSection validator)
        {
            var cliLeaves = CollectLeaves(cli);
            var validatorLeaves = CollectLeaves(validator);

            Assert.Equal(cliLeaves, validatorLeaves);
        }

        private static Dictionary<string, string> CollectLeaves(IConfigurationSection section)
        {
            var leaves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CollectLeaves(section, leaves);
            return leaves;
        }

        private static void CollectLeaves(IConfigurationSection section, Dictionary<string, string> leaves)
        {
            var children = section.GetChildren().ToArray();

            if (children.Length == 0)
            {
                leaves[section.Path] = section.Value ?? string.Empty;
                return;
            }

            foreach (var child in children)
            {
                CollectLeaves(child, leaves);
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
                    ["AiSettings:Providers:OpenRouter:Routing:Default:AllowFallbacks"] = "true",
                    ["AiSettings:Providers:OpenRouter:Routing:ByModel:z-ai/glm-5.3:Order:0"] = "gmicloud/fp8"
                })
                .Build();

            var routing = ReSet.Validator.Cli.Program.ReadOpenRouterRouting(
                configuration, "OpenRouter", "z-ai/glm-5.3");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "gmicloud/fp8" }, routing!.Order);
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

        /// <summary>
        /// [벽시계 상한이 실사용 설정에 있는가 - 2026-09-16] 20 은 실측이 정한 값이다(리뷰 중 본문을 낸
        /// 최장 호출 11.8 분 · 전체 최장 24.9 분 · 병리 52.9 분 한 건).
        ///
        /// [정확히 무엇을 막는가 - 2026-09-16 리뷰 Minor 2] 설정에서 이 줄이 빠지면 상한이 꺼지는 것이
        /// <b>아니다</b> - `Program.cs` 가 `TryParse ... : 20` 으로 20 을 쓴다(이중 기본값). 이 시험이 막는 것은
        /// 「실사용 값이 코드 폴백에만 있는 상태」다 - 그러면 값을 바꾸려는 사람이 설정에서 찾지 못하고,
        /// 생성자 기본값 0(끔, 시험 하네스용)과 헷갈린다. 폴백에 기대지 말고 설정에 명시해 둔다.
        /// 판독: docs/audit-reports/2026-09-16-AI호출-벽시계-상한-사전선언.md
        /// </summary>
        [Fact]
        public void CliSettings_CarryTheCriticCallDeadline()
        {
            var configuration = Load("src/ReSet.Cli/appsettings.json");

            var raw = configuration["AiSettings:Critic:CallDeadlineMinutes"];
            Assert.False(string.IsNullOrWhiteSpace(raw), "AiSettings:Critic:CallDeadlineMinutes 가 없으면 상한이 꺼진 채 돈다");
            Assert.True(int.TryParse(raw, out var minutes), $"정수여야 한다: '{raw}'");
            // 성공한 최장 호출(24.9 분)보다 작고, 리뷰 중 최장 정상(11.8 분)보다 넉넉해야 한다.
            Assert.InRange(minutes, 12, 25);
        }
    }
}
