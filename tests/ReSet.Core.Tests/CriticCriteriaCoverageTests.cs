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
    }
}
