using ReSet.Core.Models;

namespace ReSet.Core.Services;

/// <summary>
/// 검증 산출물의 상단 헤더(YAML 프런트매터 + 메타 블록)를 렌더링한다.
/// 골격은 문서 종류와 무관하게 같고, 다른 것은 점수 항목의 설명 주석뿐이다.
/// </summary>
public static class VerificationDocumentFormatter
{
    /// <summary>YAML 점수 줄에 붙는 설명 주석. 문서 종류마다 평가 기준이 다르다.</summary>
    private sealed record ScoreLabels(
        string Overall,
        string Accuracy,
        string Crud,
        string Interface,
        string Readability,
        string Exception);

    // 개별 명세서 Critic 기준.
    private static readonly ScoreLabels SpecificationLabels = new(
        "100점 만점 기준 AI 최종 신뢰도",
        "SQL 대비 기능 정합성",
        "데이터 변경 및 조회 검증",
        "파라미터 및 반환셋 정합성",
        "코드 가독성 및 표준 준수",
        "트랜잭션 격리 및 에러 처리");

    // 통합 계획서 Critic 기준(AiService.ReviewConsolidatedPlanAsync). 같은 필드를
    // 쓰지만 평가 대상이 다르다 - 특히 가독성은 다이어그램 문법을 본다.
    private static readonly ScoreLabels PlanLabels = new(
        "100점 만점 기준 AI 최종 신뢰도",
        "업무 로직 및 흐름 정합성",
        "데이터 모델 및 CRUD 완결성",
        "연동 및 인터페이스 정의",
        "다이어그램 문법 및 가독성",
        "예외 처리 및 트랜잭션 격리 정책");

    public static string FormatSpecification(
        string body,
        ReviewResult? review,
        VerificationOutcome outcome,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp,
        AnalysisScope? scope = null) =>
        FormatVerified(body, review, outcome, SpecificationLabels, provider, modelName, effort, timestamp, scope);

    public static string FormatConsolidatedPlan(
        string body,
        ReviewResult? review,
        VerificationOutcome outcome,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp) =>
        FormatVerified(body, review, outcome, PlanLabels, provider, modelName, effort, timestamp, scope: null);

    /// <summary>
    /// 검증 파이프라인을 거치지 않은 계획서용. 자기 자신의 검증 상태가 없으므로
    /// ReviewResult를 받지 않는다 - 없는 파라미터는 유출될 수 없다. sourceOutcome은
    /// 이 계획서의 근거가 된 명세서의 종료 상태이며, 그 사실을 명시적으로 밝힌다.
    /// </summary>
    public static string FormatUnverifiedPlan(
        string body,
        VerificationOutcome sourceOutcome,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp)
    {
        var sourceLabel = StatusLabel(sourceOutcome);

        var yamlFrontMatter = $@"---
검증 상태: 검증 없음 # 이 계획서는 L1/L2 검증을 거치지 않음
근거 명세서 검증 상태: {sourceLabel}
---

";

        var statusNote =
            $"> **검증 상태**: 이 계획서는 검증 파이프라인을 거치지 않았습니다. 근거 명세서(Spec.md)는 '{sourceLabel}' 상태입니다.\n";

        return yamlFrontMatter + MetadataHeader(provider, modelName, effort, timestamp, string.Empty, statusNote) + body;
    }

    private static string FormatVerified(
        string body,
        ReviewResult? review,
        VerificationOutcome outcome,
        ScoreLabels labels,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp,
        AnalysisScope? scope)
    {
        // 점수 노출 여부는 review의 null 여부가 아니라 종료 상태가 결정한다.
        // 1차 시도의 리뷰 결과가 남아 있어도 최종적으로 검증되지 않았다면 점수를 실으면 안 된다.
        var showScores = review is not null &&
            outcome is VerificationOutcome.Passed or VerificationOutcome.QualityRejected;

        var scoreLines = showScores
            ? $@"
종합 신뢰도: {review!.NormalizedScore} # {labels.Overall}
정합성 점수: {review.ScoreAccuracy}/10 # {labels.Accuracy}
CRUD 점수: {review.ScoreCrud}/10 # {labels.Crud}
인터페이스 점수: {review.ScoreInterface}/10 # {labels.Interface}
가독성 점수: {review.ScoreReadability}/10 # {labels.Readability}
예외처리 점수: {review.ScoreException}/10 # {labels.Exception}"
            : string.Empty;

        // 참조분석 ON/OFF에 따라 루트 SP가 본 의존성 범위가 달라진다. 계획서 진입점에서는
        // scope가 항상 null이라 이 줄 자체가 생기지 않는다 - 분석 범위는 명세서 단위 개념이다.
        var scopeLine = scope switch
        {
            AnalysisScope.Direct => "\n분석 범위: 직접 의존성 # 참조 SP/UDF 재귀 분석 모드",
            AnalysisScope.Transitive => "\n분석 범위: 전이 의존성 # 단일 객체 분석 모드",
            _ => string.Empty
        };

        var yamlFrontMatter = $@"---
검증 상태: {StatusLabel(outcome)} # 검증 파이프라인 종료 상태{scopeLine}{scoreLines}
---

";

        var scoreHeader = showScores
            ? $"> **AI 최종 신뢰도**: {review!.NormalizedScore}/100점 (정합성: {review.ScoreAccuracy}, CRUD: {review.ScoreCrud}, 연동: {review.ScoreInterface}, 가독성: {review.ScoreReadability}, 예외: {review.ScoreException})\n"
            : string.Empty;

        var statusNote = outcome switch
        {
            VerificationOutcome.ReviewNotRun =>
                "> **검증 상태**: L2 AI 교차 리뷰가 수행되지 않았습니다. 내용을 직접 검토하십시오.\n",
            VerificationOutcome.L1Exhausted =>
                "> **검증 상태**: L1 기계 검증을 통과하지 못한 채 확정되었습니다.\n",
            _ => string.Empty
        };

        return yamlFrontMatter + MetadataHeader(provider, modelName, effort, timestamp, scoreHeader, statusNote) + body;
    }

    private static string MetadataHeader(
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp,
        string scoreHeader,
        string statusNote)
    {
        var effortSuffix = string.IsNullOrWhiteSpace(effort) ? string.Empty : $", Effort: {effort}";
        return $"> [!NOTE]\n> **문서 작성일시**: {timestamp:yyyy-MM-dd HH:mm:ss}\n> **분석 AI 정보**: {provider} ({modelName}{effortSuffix})\n{scoreHeader}{statusNote}\n";
    }

    /// <summary>
    /// 종료 상태의 한국어 표기. 지시서 번들(MetadataExporter)도 같은 표기를 써야 하므로
    /// 공개한다 - 같은 switch를 여러 곳에 복제하면 한 곳이 새 상태를 빠뜨렸을 때
    /// 그 문서만 조용히 다른 말을 하게 된다.
    /// </summary>
    public static string StatusLabel(VerificationOutcome outcome) => outcome switch
    {
        VerificationOutcome.Passed => "통과",
        VerificationOutcome.QualityRejected => "품질 미달",
        VerificationOutcome.ReviewNotRun => "리뷰 미수행",
        VerificationOutcome.L1Exhausted => "L1 미통과",
        _ => "알 수 없음"
    };
}
