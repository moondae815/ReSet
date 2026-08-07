using ReSet.Core.Models;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Validator.Core.Services
{
    public enum CodegenLoopDecision
    {
        /// <summary>산출물이 있다. Critic 검증으로 넘긴다.</summary>
        Validate,

        /// <summary>산출물이 없지만 일시적 실패일 수 있다. 검증을 건너뛰고 다시 기동한다.</summary>
        RetryWithoutValidation,

        /// <summary>재시도해도 결과가 같은 실패다. 루프를 끝낸다.</summary>
        Abort
    }

    /// <summary>
    /// 코딩 에이전트 1회 기동 결과로 자가 수정 루프를 계속할지 판단한다.
    ///
    /// 프로세스도 검증기도 끼지 않는 순수 함수라 조합을 전부 테스트할 수 있다.
    /// CodeVerificationOrchestrator가 구상 클래스라 루프 전체를 목으로 감쌀 수 없기에
    /// 판단만 따로 떼어냈다.
    /// </summary>
    public static class CodegenLoopPolicy
    {
        public static CodegenLoopDecision Decide(CodegenRunResult run)
        {
            // 종료 코드는 보지 않는다. 부분 산출물도 L1/L2가 볼 가치가 있다.
            if (run.ProducedArtifacts)
            {
                return CodegenLoopDecision.Validate;
            }

            return run.FailureKind switch
            {
                CliFailureKind.QuotaExhausted => CodegenLoopDecision.Abort,
                CliFailureKind.NotAuthenticated => CodegenLoopDecision.Abort,
                CliFailureKind.ToolPermissionDenied => CodegenLoopDecision.Abort,
                _ => CodegenLoopDecision.RetryWithoutValidation
            };
        }
    }
}
