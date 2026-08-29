using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 생성 규칙이 요구하는데 Critic이 보지 않는 축은, 어긋나도 통과한다. 자가 수정은
    /// Critic의 지적으로만 돌므로 그 축은 회차를 아무리 돌려도 고쳐지지 않는다.
    /// 여기 있는 각 항목은 <c>ConsolidatedPlanRules</c>의 조항과 짝이 있다.
    /// </summary>
    public class CriticCriteriaCoverageTests
    {
        private static async Task<string> CaptureCriticPromptAsync()
        {
            var specs = new List<(string FileName, string Content)> { ("dbo.USP_Test1", "## 개요\n내용1") };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"HasDefects\\\": false}\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            await service.ReviewConsolidatedPlanAsync(specs, "## 통합 배치 아키텍처 개요", "Test_Job");

            // 요청 본문 전체가 아니라 system 메시지만 돌려준다. 본문에는 명세서 원문이
            // 함께 실리므로, `Succeeded`·`CROSS APPLY` 같은 토큰은 채점 기준이 빠져도
            // 명세서 쪽에서 걸려 통과할 수 있다 - fixture가 자라면 조용히 무력해진다.
            using var document = JsonDocument.Parse(mockHandler.LastRequestBody);
            return document.RootElement.GetProperty("messages")
                .EnumerateArray()
                .Single(message => message.GetProperty("role").GetString() == "system")
                .GetProperty("content").GetString()!;
        }

        [Fact]
        public async Task Critic_ShouldNotDemandAShadowTableUnconditionally()
        {
            // 규칙 4는 Shadow를 최후 수단으로 못박는다. 채점이 무조건 요구하면 단일
            // 트랜잭션으로 옳게 쓴 단계가 감점되고, 자가 수정이 그 단계에 필요 없는
            // Shadow와 보상 DELETE를 붙인다 - 롤백이 이미 되돌린 행을 한 번 더 지운다.
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("LAST RESORT", prompt);
            Assert.Contains("ONLY for steps that actually use a shadow", prompt);
        }

        [Fact]
        public async Task Critic_ShouldCheckThatNoNewStoredProcedureIsDefined()
        {
            // 규칙 3-1(SQL 거처)의 짝. 4단계의 성공 기준 첫 행이 "신규 SP 정의 수가
            // 0으로 수렴하는가"인데, Critic이 그 축을 보지 않으면 어긋나도 통과하고
            // 자가 수정이 거기 영영 닿지 않는다.
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("NO new stored procedure", prompt);
        }

        [Fact]
        public async Task Critic_ShouldPenalizeSqlSideControlFlow()
        {
            // 규칙 3-1이 제어 흐름의 거처를 정해도 채점이 보지 않으면 자가 수정이
            // 닿지 않는다. 3단계가 `GOTO` 조항을 규칙과 채점에서 함께 빼자 통제군
            // 한 편이 `IF @@ERROR <> 0 GOTO ERR_HANDLER`를 21번 냈다.
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("branches on its own outcome", prompt);
            Assert.Contains("`GOTO` error labels", prompt);
        }

        [Fact]
        public async Task Critic_ShouldPenalizeAHardCodedIsolationStatement()
        {
            // 규칙 4는 "격리를 거는 자리를 정하지 말라"고 한다. 채점에는 그 반대편
            // (안 적었다고 감점하지 말라)만 있어서, 단계 SQL에 직접 적어 버린 쪽이
            // 무주공산이었다 - 통제군 한 편이 실제로 5번 적었다.
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("penalize a step that writes the isolation statement into its own SQL", prompt);
        }

        [Fact]
        public async Task Critic_ShouldPenalizeNamingARealFrameworkType()
        {
            // 2차 통제군에서 Critic이 `conn.BeginTransaction(IsolationLevel.Snapshot)`을
            // 보고도 통과시켰다 - 격리 감점을 "단계 SQL에 쓰지 마라"로만 적어
            // 앱 코드 쪽 API 지정이 무주공산이었다. 규칙 3-1이 요구하는데 채점이
            // 보지 않는 축이므로 자가 수정이 영영 닿지 않는다.
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("names NO type from a real data-access framework", prompt);

            // 반대편도 함께 막는다 - 일반 자리표시자까지 감점하면 옳게 쓴 단계가
            // 깎이고 자가 수정이 T-SQL 철자로 되돌린다.
            Assert.Contains("do NOT penalize them", prompt);

            // 표기 분열: 한 문서가 같은 것을 두 이름으로 부르면 이행 라운드가
            // 존재한 적 없는 계약 둘을 화해시켜야 한다.
            Assert.Contains("two different invented names", prompt);
            Assert.Contains("not a quotation of the original", prompt);
        }

        [Fact]
        public async Task Critic_ShouldCheckTheBatchSchemaRule()
        {
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("batch_shadow", prompt);
            Assert.Contains("bootstrap round only creates objects", prompt);
        }

        [Fact]
        public async Task Critic_ShouldCheckTheControlTableVocabulary()
        {
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("Succeeded", prompt);
            Assert.Contains("ExecutionStatus", prompt);
        }

        [Fact]
        public async Task Critic_ShouldCheckThatNoStepAddedARestartParameter()
        {
            // 규칙 5의 짝. 재시작 건너뛰기는 단계 밖에서 일어나므로 단계가 그것을
            // 파라미터로 받으면 원본의 사전 검증 가드를 호출자가 끌 수 있게 된다.
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("NO step added an input parameter for restart", prompt);
        }

        [Fact]
        public async Task Critic_ShouldCheckTheStatementAnchors()
        {
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("U13", prompt);
            Assert.Contains("silently does not happen", prompt);
        }

        [Fact]
        public async Task Critic_ShouldCheckTheCrossApplyAndAggregateSubstitutions()
        {
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("CROSS APPLY", prompt);
            // 요청 본문은 JSON이라 '>'가 \u003E로 escape된다. 토큰만 본다.
            Assert.Contains("@@ROWCOUNT", prompt);
        }

        [Fact]
        public async Task Critic_ShouldCheckUnionBranchAlignment()
        {
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("same column list in the same order", prompt);
            Assert.Contains("USESTATE", prompt);
        }

        [Fact]
        public async Task Critic_ShouldCheckTheControlStepErrorCodeBand()
        {
            // 생성 규칙이 요구하는데 아무도 채점하지 않으면 어긋나도 통과하고,
            // 자가 수정이 그 축에 영영 닿지 않는다 - 직전 회차에서 실측한 실패 방식이다.
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("reserved block", prompt);
            Assert.Contains("does not compile", prompt);
        }

        [Fact]
        public async Task Critic_ShouldCheckTheStringCodeAxis()
        {
            // 생성 규칙이 요구하는데 아무도 채점하지 않으면 어긋나도 통과하고,
            // 자가 수정이 그 축에 영영 닿지 않는다.
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("string status code", prompt);
        }

        [Fact]
        public async Task Critic_ShouldExemptCheckpointStatusValuesFromTheStringCodeBan()
        {
            // FIX ROUND 1 - Important 1: 채점 기준 3의 바로 두 줄 위(AiService.cs:4369)가
            // "all steps spell ... status values identically (`Succeeded`, never
            // `Completed`)"라고 요구한다. 그 상태값은 문자 그대로 문자열로 대입된다
            // (`SET @v_stepStatus = N'Running'`). 예외를 달지 않으면 같은 ScoreInterface
            // 블록 안에서 Critic이 정상 계획서를 감점하고, 그 축에서 채점이 불가능해진다.
            var prompt = await CaptureCriticPromptAsync();

            Assert.Contains("are also not step error codes and stay strings", prompt);
        }
    }
}
