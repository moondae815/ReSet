using System.Text;
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

        /// <summary>
        /// 검증 대조 쌍을 하나도 찾지 못했을 때 지시서에 붙일 피드백.
        ///
        /// 이것이 없으면 재시도는 같은 명령을 신호 없이 다시 던지는 것이다. 에이전트는
        /// 무엇이 잘못됐는지 알 수 없고, 그래서 다음 시도도 같은 자리에서 끝난다.
        ///
        /// 매핑 규약은 FileMappingService가 소유한다(FileMappingService.cs:135-160).
        /// 여기서는 에이전트가 고칠 수 있는 형태로만 옮겨 적는다.
        /// </summary>
        public static string BuildUnverifiedFeedback(string specDir, string codeDir, int attempt)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"## 🚨 [검증 대조 실패 - Attempt {attempt}] 🚨");
            sb.AppendLine(
                "검증기가 설계서와 소스 코드의 대조 쌍을 **하나도** 찾지 못했습니다. " +
                "코드가 생성되었더라도 한 줄도 검증되지 않은 상태입니다.");
            sb.AppendLine();
            sb.AppendLine($"- 설계서 디렉터리: `{specDir}`");
            sb.AppendLine($"- 소스 디렉터리: `{codeDir}`");
            sb.AppendLine();
            // 아래 규약 문구는 두 판정부의 요약이다: 파일 쪽은 FileMappingService.ResolveMappings가,
            // 폴더 쪽은 CodegenArtifactNaming.JobProjectDirectoryNames가 단독으로 소유한다. 그 파일
            // 자신의 주석이 이미 경고하듯("예시를 손으로 적으면 판정부와 조용히 갈라진다") 이것도
            // 손으로 옮겨 적은 요약이라 원본이 바뀌면 같이 고쳐야 한다. 그럼에도 여기서 다시 옮겨
            // 적는 이유는 이 함수가 두 디렉터리 경로만 가질 뿐 Job 이름을 몰라 소유자 함수를
            // 직접 호출할 수 없기 때문이다.
            sb.AppendLine(
                "검증기는 설계서 폴더명에서 스키마를 뗀 이름으로 짝을 찾습니다. " +
                "예를 들어 설계서가 `dbo.CustOrderHist/docs/Spec.md`에 있으면 " +
                "소스 디렉터리에서 `CustOrderHist`라는 이름의 `.cs` 또는 `.java` **파일**(다른 확장자는 " +
                "인정되지 않습니다) 또는 같은 이름(혹은 `CustOrderHist.Batch`처럼 `.Batch`가 붙은 " +
                "형태)의 **폴더**를 찾습니다.");
            sb.AppendLine();
            sb.AppendLine("생성한 파일과 폴더의 이름이 이 규약을 따르는지 확인하고, 어긋나면 이름을 고치십시오.");

            return sb.ToString();
        }
    }
}
