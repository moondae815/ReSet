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
    }
}
