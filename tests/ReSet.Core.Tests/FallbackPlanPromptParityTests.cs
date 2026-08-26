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
    /// 단일 호출 폴백(<c>GenerateConsolidatedBatchPlanAsync</c>)은 분할 생성이 실패했을
    /// 때만 도는 경로다. 그러나 그때 나오는 것도 같은 산출물이고 같은 검사를 받는다.
    /// 컨텍스트가 갈리면 어느 경로로 만들어졌느냐에 따라 다른 문서가 나온다.
    /// </summary>
    public class FallbackPlanPromptParityTests
    {
        private const string PlanStructure = @"## 목차
```json
{ ""Steps"": [
  { ""Code"": ""S01"", ""Name"": ""날짜 검증"", ""LegacyProcedures"": [""dbo.UP_A""] },
  { ""Code"": ""S02"", ""Name"": ""정산 원장"", ""LegacyProcedures"": [""dbo.UP_B""] }
] }
```";

        private static async Task<AiResult> CaptureAsync(IReadOnlyList<StepInterface>? interfaces = null)
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-test");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "ok" });

            IAiService service = new AiService(client, 0.2f);
            var specs = new List<(string FileName, string Content)> { ("dbo.UP_A", "본문") };

            return await service.GenerateConsolidatedBatchPlanAsync(
                PlanStructure, specs, "C#", "Job_Test", effort: null, stepInterfaces: interfaces);
        }

        [Fact]
        public async Task Fallback_ShouldCarryTheBatchControlContract()
        {
            // 제어 계약 표가 없으면 단계별 어휘가 갈린다(StepStatus vs ExecutionStatus,
            // Succeeded vs Completed). 하나의 DDL이 모든 단계를 만족시키지 못해
            // 재시작이 매 실행 막힌다.
            var result = await CaptureAsync();

            Assert.Contains("[Batch Control Table Contract]", result.UserPrompt);
            Assert.Contains("NEVER use the status value 'Completed'", result.UserPrompt);
        }

        [Fact]
        public async Task Fallback_ShouldCarryTheApprovedStepList()
        {
            var result = await CaptureAsync();

            Assert.Contains("[Approved Step List]", result.UserPrompt);
            Assert.Contains("S01 | 날짜 검증", result.UserPrompt);
            Assert.Contains("S02 | 정산 원장", result.UserPrompt);
        }

        [Fact]
        public async Task Fallback_ShouldCarryTheStatementAnchorRules()
        {
            // 앵커가 없으면 단계 검사가 문장을 명세서의 갱신 N에 붙일 수 없어
            // 조인 키·술어 컬럼 대조가 실패하는 것이 아니라 아예 실행되지 않는다.
            var result = await CaptureAsync();

            Assert.Contains("### 문장 앵커와 의미 보존 (필수)", result.UserPrompt);
            Assert.Contains("CROSS APPLY", result.UserPrompt);
            Assert.Contains("@@ROWCOUNT > 1", result.UserPrompt);
        }

        [Fact]
        public async Task Fallback_WithInterfaces_ShouldCarryTheTableRule5PointsAt()
        {
            // 규칙 5는 "[Original Procedure Interface] 표에 적힌 파라미터가 전부"라고
            // 말한다. 그 표가 없으면 지킬 방법이 없는 규칙을 주는 셈이다.
            var interfaces = StepInterfaceFacts.Build(
                BatchStepPlanParser.TryParse(PlanStructure)!,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["UP_A"] = new[] { "@pi_strYMD CHAR(8)" }
                });

            var result = await CaptureAsync(interfaces);

            Assert.Contains("[Original Procedure Interface]", result.UserPrompt);
            Assert.Contains("@pi_strYMD", result.UserPrompt);
        }

        [Fact]
        public async Task Fallback_WithoutInterfaces_ShouldOmitTheClauseRatherThanShowAnEmptyTable()
        {
            // 빈 표를 실으면 모델이 "원본 파라미터가 없다"로 읽어 있지도 않은 근거로
            // 파라미터를 새로 지어낸다.
            var result = await CaptureAsync();

            Assert.DoesNotContain("[Original Procedure Interface]", result.UserPrompt);
        }

        [Fact]
        public async Task Plan_ShouldCarryTheControlStepErrorCodeClause()
        {
            var result = await CaptureAsync();

            Assert.Contains("[Control Step Error Codes]", result.SystemPrompt);
            Assert.Contains("-9010..-9019", result.SystemPrompt);
        }
    }
}
