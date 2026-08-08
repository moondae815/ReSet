using ReSet.Core.Models;
using ReSet.Core.Services.Clients.Cli;
using ReSet.Validator.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class CodegenLoopPolicyTests
    {
        [Theory]
        [InlineData(CliFailureKind.QuotaExhausted)]
        [InlineData(CliFailureKind.NotAuthenticated)]
        [InlineData(CliFailureKind.ToolPermissionDenied)]
        [InlineData(CliFailureKind.Unknown)]
        public void Decide_ShouldValidate_WheneverArtifactsExist(CliFailureKind kind)
        {
            // 산출물이 있으면 종료 코드와 분류를 보지 않는다. 부분 산출물도 검증 대상이다.
            var run = new CodegenRunResult(ProducedArtifacts: true, ExitCode: 1, FailureKind: kind, Diagnostic: null);

            Assert.Equal(CodegenLoopDecision.Validate, CodegenLoopPolicy.Decide(run));
        }

        [Theory]
        [InlineData(CliFailureKind.QuotaExhausted)]
        [InlineData(CliFailureKind.NotAuthenticated)]
        [InlineData(CliFailureKind.ToolPermissionDenied)]
        public void Decide_ShouldAbort_WhenNoArtifactsAndFailureIsPermanent(CliFailureKind kind)
        {
            var run = new CodegenRunResult(ProducedArtifacts: false, ExitCode: 0, FailureKind: kind, Diagnostic: "…");

            Assert.Equal(CodegenLoopDecision.Abort, CodegenLoopPolicy.Decide(run));
        }

        [Theory]
        [InlineData(CliFailureKind.Unknown)]
        [InlineData(CliFailureKind.Timeout)]
        public void Decide_ShouldRetryWithoutValidation_WhenNoArtifactsAndFailureMayBeTransient(CliFailureKind kind)
        {
            var run = new CodegenRunResult(ProducedArtifacts: false, ExitCode: 0, FailureKind: kind, Diagnostic: null);

            Assert.Equal(CodegenLoopDecision.RetryWithoutValidation, CodegenLoopPolicy.Decide(run));
        }

        /// <summary>
        /// 대조 쌍을 못 찾았을 때의 피드백은 에이전트가 실제로 고칠 수 있는 것을 말해야 한다.
        /// 경로 두 개와 이름 규약이 그것이다. 이 문구가 없으면 재시도는 같은 명령을 신호 없이
        /// 다시 던지는 것이라 다음 시도도 같은 결과로 끝난다.
        /// </summary>
        [Fact]
        public void BuildUnverifiedFeedback_ShouldCarryBothDirectoriesAndTheNamingRule()
        {
            var feedback = CodegenLoopPolicy.BuildUnverifiedFeedback(
                @"C:\out\Procedures", @"C:\out\Jobs\MyJob\src", attempt: 2);

            Assert.Contains("Attempt 2", feedback);
            Assert.Contains(@"C:\out\Procedures", feedback);
            Assert.Contains(@"C:\out\Jobs\MyJob\src", feedback);
            // 이름 규약을 말해 주지 않으면 에이전트가 무엇을 고쳐야 할지 모른다.
            Assert.Contains("스키마", feedback);
            Assert.Contains("CustOrderHist", feedback);
            // 파일 규칙: ResolveMappings는 .cs/.java로 확장자를 제한한 뒤 이름을 비교한다.
            // "확장자 무관"이라고 말하면 실제로 매치되지 않는 파일을 매치된다고 속이는 것이다.
            Assert.Contains(".cs", feedback);
            Assert.Contains(".java", feedback);
            Assert.DoesNotContain("확장자 무관", feedback);
            // 폴더 규칙: JobProjectDirectoryNames는 맨 이름뿐 아니라 `.Batch` 접미사 형태도 인정한다.
            Assert.Contains(".Batch", feedback);
        }

        /// <summary>
        /// 지시서 끝에 여러 번 붙으므로 시도 회차로 구별돼야 한다.
        /// </summary>
        [Fact]
        public void BuildUnverifiedFeedback_DifferentAttempts_ShouldBeDistinguishable()
        {
            var first = CodegenLoopPolicy.BuildUnverifiedFeedback("/spec", "/code", attempt: 1);
            var second = CodegenLoopPolicy.BuildUnverifiedFeedback("/spec", "/code", attempt: 2);

            Assert.NotEqual(first, second);
            Assert.Contains("Attempt 1", first);
            Assert.Contains("Attempt 2", second);
        }

        /// <summary>
        /// 이 실패는 CLI 기동 문제가 아니다. 엔진 설정을 확인하라고 말하면 사람을 엉뚱한
        /// 곳으로 보낸다 - 기동은 성공했고 산출물도 나왔다.
        /// </summary>
        [Fact]
        public void BuildUnverifiedFeedback_ShouldNotBlameTheEngineConfiguration()
        {
            var feedback = CodegenLoopPolicy.BuildUnverifiedFeedback("/spec", "/code", attempt: 1);

            Assert.DoesNotContain("CodegenSettings", feedback);
            Assert.DoesNotContain("AiSettings", feedback);
        }
    }
}
