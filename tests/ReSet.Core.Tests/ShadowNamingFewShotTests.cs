using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// Shadow 이름 규약은 규칙 4-1과 Few-Shot 두 곳에서 말해진다. 둘이 어긋나면
    /// 실측에서는 예시가 이긴다 - 규칙은 산문이고 예시는 베껴 쓸 코드라서다.
    ///
    /// 실측(POQSettleBatch1 10:08:13): 예시가 `_RunId_`를 리터럴로 쓰라고 가르쳐
    /// 계획서가 `batch_shadow.TSettleMst_RunId_Sxx`를 그대로 적었다. 그러면 이름이
    /// 나르기로 한 실행 식별자가 사라져 모든 회차가 한 Shadow를 공유한다.
    /// </summary>
    public class ShadowNamingFewShotTests
    {
        private static async Task<string> CapturePlanPromptAsync()
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
            var result = await service.GenerateConsolidatedBatchPlanAsync(
                "## 목차", specs, "C#", "Job_Test");

            return result.SystemPrompt + "\n" + result.UserPrompt;
        }

        [Fact]
        public async Task FewShot_ShouldNotTeachTheLiteralRunIdToken()
        {
            // 규칙 4-1은 런타임 조립을 지시한다. 예시가 리터럴 자리표시자를 보여주면
            // 같은 프롬프트 안에서 두 지시가 맞선다.
            var prompt = await CapturePlanPromptAsync();

            // 리터럴을 쓰지 말라고 경고하는 문장에는 그 토큰이 등장해야 하므로,
            // 실행 가능한 SQL 줄에서만 찾는다.
            var sqlLines = prompt.Split('\n')
                .Where(line => !line.TrimStart().StartsWith("--"))
                .ToList();

            Assert.DoesNotContain(sqlLines, line => line.Contains("TargetTable_RunId_"));
        }

        [Fact]
        public async Task FewShot_ShouldShowTheAssembledShapeTheCollectorCanRead()
        {
            // 조립 자체는 자유가 아니다. BatchInfraObjectCollector가 부트스트랩에서
            // 만들 객체를 찾는 정규식이 "리터럴 접두사 + 표현식 + N'_<StepCode>'"
            // 모양만 읽는다. 다른 모양으로 조립하면 그 Shadow는 생성되지 않는다.
            var prompt = await CapturePlanPromptAsync();

            var collected = BatchInfraObjectCollector.Collect(prompt);

            Assert.Contains(collected.Names, name =>
                name.StartsWith("batch_shadow.TargetTable_", System.StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("_S13", System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
