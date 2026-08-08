using System.Collections.Generic;
using ReSet.Core.Services;

namespace ReSet.Core.Models;

/// <summary>
/// 계획서가 어떤 조각들로 만들어졌는지를 산출물 작성부까지 나른다.
///
/// 조각을 나르는 이유는 본문을 다시 쓰기 위해서가 아니라 <b>경계를 알기 위해서</b>다.
/// split.Markdown이 나온 뒤에도 최종 문서는 L1 정제·자가 교정·구제 채택으로 계속
/// 바뀌므로(VerificationPipelineOrchestrator의 CleansedMarkdown/rescued 경로), 조각
/// 본문을 그대로 산출물에 실으면 BatchMigrationPlan.md와 steps/*.md의 내용이 조용히
/// 달라진다. Sections는 헤딩 앵커로만 쓰고 본문은 언제나 최종 문서에서 잘라낸다.
/// </summary>
/// <param name="Skeleton">개요·흐름도·검증 SQL·공통 규약. 단일 호출로 생성됐으면 null.</param>
/// <param name="Sections">단계 코드 → 단계 섹션 마크다운. 경계 앵커의 출처.</param>
/// <param name="Steps">목차가 선언한 단계 목록. 앵커 탐색이 실패했을 때의 2순위 근거이자 회차 정의.</param>
/// <param name="FloorViolations">단계 코드 → 판정 종류와 사유(StepDefect). 해당 단계 파일에 배너로 실린다.</param>
public sealed record PlanLayout(
    string? Skeleton,
    IReadOnlyDictionary<string, string>? Sections,
    IReadOnlyList<BatchStepPlan>? Steps,
    IReadOnlyDictionary<string, StepDefect>? FloorViolations)
{
    /// <summary>
    /// 단계 분할을 시도할 수 있는가. Sections가 비어 있으면 앵커가 없으므로 시도 자체가 성립하지 않는다.
    /// </summary>
    public bool IsSplitAvailable => Sections is { Count: > 0 };
}
