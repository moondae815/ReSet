using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Models
{
    /// <summary>
    /// 코딩 엔진 1회 기동의 결과.
    ///
    /// 성공 여부를 나타내는 편의 속성을 일부러 두지 않는다. 루프 판단은
    /// ProducedArtifacts와 FailureKind의 조합으로 이뤄지고 종료 코드 단독으로는
    /// 아무것도 결정하지 않는다. Succeeded 같은 속성을 두면 이 설계가 고치려는
    /// 착각("0이면 성공")을 그대로 되살린다.
    /// </summary>
    /// <param name="ProducedArtifacts">작업 디렉터리에 실제 변화가 있었는가</param>
    /// <param name="ExitCode">프로세스 종료 코드</param>
    /// <param name="FailureKind">stderr로 분류한 실패 원인. 대화형에서는 항상 Unknown</param>
    /// <param name="Diagnostic">배치에서 캡처한 stderr 원문. 대화형에서는 null</param>
    public sealed record CodegenRunResult(
        bool ProducedArtifacts,
        int ExitCode,
        CliFailureKind FailureKind,
        string? Diagnostic);
}
