using ReSet.Core.Services;

namespace ReSet.Core.Models;

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
public sealed record ConsolidatedPipelineResult(
    string? Plan,
    AiResult? Result,
    ReviewResult? Review,
    VerificationOutcome Outcome);
