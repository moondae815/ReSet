using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 1/3 브레인스토밍은 아키텍처 판단이 나오는 자리이고, 그 판단을 문서에 쓰는 것은
    /// 3/3 골격이다(아키텍처 개요와 흐름도). 원문이 전달되지 않으면 목차 제목에
    /// 살아남은 만큼만 본문에 도달한다.
    /// </summary>
    public class BrainstormingDeliveryTests
    {
        private static IAiClient Client()
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-test");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "ok" });
            return client;
        }

        private static readonly List<BatchStepPlan> Steps = new()
        {
            new BatchStepPlan("S01", "날짜 검증", new[] { "dbo.UP_A" }, new string[0], new string[0], false, new string[0])
        };

        [Fact]
        public async Task Skeleton_ShouldCarryTheBrainstormingText()
        {
            IAiService service = new AiService(Client(), 0.2f);
            var specs = new List<(string FileName, string Content)> { ("dbo.UP_A", "본문") };

            var result = await service.GenerateBatchPlanSkeletonAsync(
                Steps, "## 목차", specs, "C#", "Job_Test",
                effort: null, brainstorming: "S05는 GROUP BY 집계라 청크 분할이 불가능하다.");

            Assert.Contains("[Architecture Brainstorming", result.UserPrompt);
            Assert.Contains("S05는 GROUP BY 집계라 청크 분할이 불가능하다.", result.UserPrompt);
        }

        [Fact]
        public async Task Skeleton_WithoutBrainstorming_ShouldOmitTheClause()
        {
            // 브레인스토밍을 돌지 않은 경로(지목 재생성 등)에서 빈 머리글을 실으면
            // "분석 결과가 없다"는 거짓 전제를 준다.
            IAiService service = new AiService(Client(), 0.2f);
            var specs = new List<(string FileName, string Content)> { ("dbo.UP_A", "본문") };

            var result = await service.GenerateBatchPlanSkeletonAsync(
                Steps, "## 목차", specs, "C#", "Job_Test");

            Assert.DoesNotContain("[Architecture Brainstorming", result.UserPrompt);
        }

        [Fact]
        public async Task Skeleton_ShouldKeepTheSharedPrefixAheadOfTheBrainstorming()
        {
            // 골격과 단계 호출은 AppendSharedStepContext까지 바이트가 같아야 접두사
            // 캐시가 산다. 브레인스토밍이 그 구간 안에 끼면 명세서 전량의 캐시가 죽는다.
            IAiService service = new AiService(Client(), 0.2f);
            var specs = new List<(string FileName, string Content)> { ("dbo.UP_A", "본문") };

            var result = await service.GenerateBatchPlanSkeletonAsync(
                Steps, "## 목차", specs, "C#", "Job_Test",
                effort: null, brainstorming: "분석 원문");

            var prompt = result.UserPrompt!;
            Assert.True(
                prompt.IndexOf("[Batch Control Table Contract]", System.StringComparison.Ordinal)
                < prompt.IndexOf("[Architecture Brainstorming", System.StringComparison.Ordinal),
                "브레인스토밍은 공유 접두사 뒤에 와야 한다");
        }

        [Fact]
        public async Task Fallback_ShouldAlsoCarryTheBrainstormingText()
        {
            // 단일 호출 폴백은 골격이 하는 일(아키텍처 개요·흐름도)을 문서 전체와 함께
            // 한 번에 한다. 브레인스토밍이 도달해야 하는 자리가 정확히 여기다 -
            // known-defects가 지목했던 것도 이 시그니처였다.
            IAiService service = new AiService(Client(), 0.2f);
            var specs = new List<(string FileName, string Content)> { ("dbo.UP_A", "본문") };

            var result = await service.GenerateConsolidatedBatchPlanAsync(
                "## 목차", specs, "C#", "Job_Test",
                effort: null, stepInterfaces: null, brainstorming: "S05는 GROUP BY 집계라 청크 분할이 불가능하다.");

            Assert.Contains("[Architecture Brainstorming", result.UserPrompt);
            Assert.Contains("S05는 GROUP BY 집계라 청크 분할이 불가능하다.", result.UserPrompt);
        }

        [Fact]
        public async Task Skeleton_ShouldCarryTheInterfaceTableRule5PointsAt()
        {
            // 골격도 ConsolidatedPlanRules를 통째로 받는다. 규칙 5가 가리키는 표가
            // 없으면 프롬프트에 없는 근거를 가리키는 지시가 된다.
            IAiService service = new AiService(Client(), 0.2f);
            var specs = new List<(string FileName, string Content)> { ("dbo.UP_A", "본문") };
            var interfaces = StepInterfaceFacts.Build(
                Steps,
                new Dictionary<string, IReadOnlyList<string>> { ["UP_A"] = new[] { "@pi_strYMD CHAR(8)" } });

            var result = await service.GenerateBatchPlanSkeletonAsync(
                Steps, "## 목차", specs, "C#", "Job_Test",
                effort: null, brainstorming: null, stepInterfaces: interfaces);

            Assert.Contains("[Original Procedure Interface]", result.UserPrompt);
            Assert.Contains("@pi_strYMD", result.UserPrompt);
        }

        [Fact]
        public async Task Brainstorm_ShouldNotForceAnUnrelatedFrameworksVocabulary()
        {
            // 산출물은 batch 스키마의 T-SQL 프로시저다. Spring Batch의 Tasklet/Chunk를
            // 강제하면 다음 단계가 쓸 수 없는 어휘로 결론이 나온다. 물어야 할 것은
            // 목차가 실제로 나르는 것 - 단계별 청크 가능 여부다.
            IAiService service = new AiService(Client(), 0.2f);
            var specs = new List<(string FileName, string Content)> { ("dbo.UP_A", "본문") };

            var result = await service.BrainstormBatchPlanAsync(specs, "C#", "Job_Test");

            Assert.DoesNotContain("Tasklet", result.SystemPrompt);
            Assert.Contains("committed chunks or must complete as one unit", result.SystemPrompt);
            Assert.Contains("per-step boolean", result.SystemPrompt);
        }
    }
}
