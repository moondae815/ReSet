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
        public async Task FewShot_ShouldCaptureTheShadowWithEveryColumn()
        {
            // Shadow는 이 단계가 곧 파괴할 행의 백업이다. 컬럼을 골라 담으면 범위 키가
            // 빠지고, 복원이 그 키로 행을 찾지 못해 0행을 되돌린다 - 지운 행은 사라진다.
            // 반대로 백업이 아니라 새 데이터의 적재소로 쓰면 범위 키가 NULL인 행이
            // 본 테이블에 들어가, 다음 회차의 DELETE가 그 행을 영영 지우지 못한다.
            var prompt = await CapturePlanPromptAsync();

            Assert.Contains(
                "INSERT INTO ' + @v_shadow + N' SELECT * FROM dbo.TargetTable WHERE BatchDate = @p_batchDate;",
                prompt);
        }

        [Fact]
        public async Task FewShot_ShouldNotDemonstrateAShadowForASingleTransactionStep()
        {
            // 규칙 4는 단일 트랜잭션 단계에 Shadow와 CATCH 보상을 금지하고, Critic도
            // 그것을 감점 조항으로 갖는다. 예시가 그 형태를 보여주면 프롬프트가 제
            // 채점 기준에 걸리는 코드를 가르치는 셈이다.
            var prompt = await CapturePlanPromptAsync();

            Assert.Contains("COMMITS the", prompt);
            // [2026-08-29] 옛 단언은 "compensate in CATCH"라는 T-SQL 철자를 고정했다.
            // 규칙 3-1이 새 SQL의 TRY/CATCH를 금지하므로 예시에서 그 철자를 걷었다 -
            // **계약은 같다**: 단일 트랜잭션 단계는 Shadow도 사후 보상도 쓰지 않는다.
            Assert.Contains("NOT use a shadow and must NOT compensate afterwards", prompt);
            Assert.DoesNotContain("compensate in CATCH", prompt);
        }

        [Fact]
        public async Task FewShot_ShouldNotAddARunIdInputParameter()
        {
            // 규칙 5: 단계의 인터페이스는 원본 프로시저의 파라미터 목록 그대로다.
            // `@pi_` 접두사는 이 프롬프트 안에서 원본 입력 파라미터를 뜻하므로,
            // `@pi_runId`는 예시가 규칙 5 위반을 시연하는 것이 된다.
            var prompt = await CapturePlanPromptAsync();

            Assert.DoesNotContain("@pi_runId", prompt);
            Assert.Contains("FROM batch.BatchRun", prompt);
        }

        [Fact]
        public async Task FewShot_ShouldShowTheAssembledShapeTheCollectorCanRead()
        {
            // 조립 자체는 자유가 아니다. BatchInfraObjectCollector가 부트스트랩에서
            // 만들 객체를 찾는 정규식이 "리터럴 접두사 + 표현식 + N'_<StepCode>'"
            // 모양만 읽는다. 다른 모양으로 조립하면 그 Shadow는 생성되지 않는다.
            var prompt = await CapturePlanPromptAsync();

            // 주석 줄을 걷어내고 실행 가능한 SQL만 넘긴다. 산문이 같은 이름을 한 번이라도
            // 언급하면 ObjectRegex 경로가 그것으로 이름을 만들어, 조립형이 컬렉터가 못 읽는
            // 모양으로 바뀌어도 이 검사가 통과한다 - 변이 검증에서 실제로 그랬다
            // (조립을 CONCAT(...)으로 바꿔도 통과했다). 검사해야 하는 것은 "언급이 있다"가
            // 아니라 "조립형을 읽어낸다"이다.
            var sqlOnly = string.Join("\n", prompt.Split('\n')
                .Where(line => !line.TrimStart().StartsWith("--")));

            var collected = BatchInfraObjectCollector.Collect(sqlOnly);

            Assert.Contains(collected.Names, name =>
                name.StartsWith("batch_shadow.TargetTable_", System.StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("_S13", System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
