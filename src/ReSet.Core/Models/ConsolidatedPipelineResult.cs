using ReSet.Core.Services;

namespace ReSet.Core.Models;

/// <summary>
/// 파이프라인이 왜 멈췄나. <c>VerificationOutcome</c> 에 값을 넣지 않는다 — 그 열거형은
/// 「문서의 검증 상태」를 뜻하고 배너·헤더가 읽는다. 중단 사유는 다른 축이라 섞으면
/// 두 뜻이 한 필드에 산다(설계 §4-3).
/// </summary>
public enum PipelineAbortReason
{
    QuotaExhausted
}

/// <summary>
/// 통합 배치 계획 파이프라인의 결과. 계획서가 어떤 상태로 끝났는지(Outcome)와
/// 그 판정의 근거가 된 L2 리뷰(Review)를 호출부까지 전달한다. 이전 튜플 반환은
/// 이 둘을 담지 못해 산출물에 검증 상태를 기록할 수 없었다.
/// </summary>
/// <param name="Plan">확정된 계획서 본문. 실패하거나 취소되면 null.</param>
/// <param name="Result">최종 생성 호출의 AI 결과(프롬프트 컨텍스트·추론 로그용).</param>
/// <param name="Review">최종 판정의 근거가 된 L2 리뷰. 리뷰를 수행하지 못했거나
/// L3 피드백으로 재생성된 경우 null이며, 이때 점수를 실어서는 안 된다.</param>
/// <param name="Outcome">검증 파이프라인 종료 상태.</param>
/// <param name="Layout">계획서를 만든 조각들. 산출물 분할의 경계 근거이며, 단일 호출로
/// 생성됐거나 파이프라인이 실패하면 null이다. 기본값이 null이므로 이 값을 쓰지 않는
/// 호출부는 변경할 필요가 없다.</param>
/// <param name="Coverage">실제로 실행된 검증량. `PlanLayout`이 문서의 구조를 담는 것과
/// 달리 이 값은 그 구조를 얼마나 검사했는가를 담으므로 형제 필드로 둔다. 문서가 없는
/// 경로(취소·실패)에서는 null이다.</param>
/// <param name="AbortReason">파이프라인이 실패가 아니라 중단으로 끝난 이유. `Plan`이
/// null인 경로에서만 뜻이 있다 — 값이 있어도 `Plan`이 있으면(구제 채택 등) 무시된다.</param>
public sealed record ConsolidatedPipelineResult(
    string? Plan,
    AiResult? Result,
    ReviewResult? Review,
    VerificationOutcome Outcome,
    PlanLayout? Layout = null,
    VerificationCoverage? Coverage = null,
    PipelineAbortReason? AbortReason = null);
