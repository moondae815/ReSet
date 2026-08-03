using ReSet.Core.Models;

namespace ReSet.Core.Services;

/// <summary>
/// 검증 산출물의 상단 헤더(YAML 프런트매터 + 메타 블록)를 렌더링한다.
///
/// 진입점은 문서 종류가 아니라 보장 수준으로 나뉜다. 정산 정책 문서와 단일 SP 계획서는
/// 종류가 전혀 다르지만 둘 다 파이프라인에 진입한 적이 없고, 명세서와 통합 계획서는
/// 종류가 다르지만 같은 파이프라인을 통과했다. 실제 축은 무엇이 보장되는가다.
/// </summary>
public static class VerificationDocumentFormatter
{
    /// <summary>
    /// 검증 파이프라인을 통과한 문서 - 명세서와 통합 계획서.
    /// </summary>
    public static string FormatVerifiedDocument(
        string body,
        ReviewResult? review,
        VerificationOutcome outcome,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp)
    {
        // 점수 노출 여부는 review의 null 여부가 아니라 종료 상태가 결정한다.
        // 1차 시도의 리뷰 결과가 남아 있어도 최종적으로 검증되지 않았다면 점수를 실으면 안 된다.
        var showScores = review is not null &&
            outcome is VerificationOutcome.Passed or VerificationOutcome.QualityRejected;

        // 점수 줄에 설명 주석을 붙이지 않는다. 이전 판은 Critic 프롬프트의 평가 기준을
        // 사람이 옮겨 적었는데, 연결을 강제하는 장치가 없어 드리프트했고 실제로 거짓이
        // 되었다. Critic은 셋(프로시저 명세서/UDF 명세서/통합 계획서)인데 이 포매터는
        // 그 셋을 구분할 수단이 없으므로, 어떤 문구를 쓰더라도 어딘가에서는 틀린다.
        var scoreLines = showScores
            ? $@"
종합 신뢰도: {review!.NormalizedScore}
정합성 점수: {review.ScoreAccuracy}/10
CRUD 점수: {review.ScoreCrud}/10
인터페이스 점수: {review.ScoreInterface}/10
가독성 점수: {review.ScoreReadability}/10
예외처리 점수: {review.ScoreException}/10"
            : string.Empty;

        // 이 주석은 남는다. 필드 자체의 설명이라 프롬프트에서 복제한 것이 아니고
        // 드리프트할 대상이 없다.
        var yamlFrontMatter = $@"---
검증 상태: {StatusLabel(outcome)} # 검증 파이프라인 종료 상태{scoreLines}
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

    /// <summary>
    /// 검증 파이프라인에 진입한 적 없는 문서 - 단일 SP 계획서와 정산 정책 문서.
    ///
    /// ReviewResult를 받지 않는다. 이런 문서에는 실을 수 있는 점수가 없고, 파라미터를
    /// 두지 않으면 어떤 호출부도 점수를 유출시킬 수 없다 - 없는 파라미터는 전달될 수 없다.
    ///
    /// sourceOutcome은 이 문서의 근거가 된 명세서의 종료 상태다. 정산 정책 문서는
    /// SP 정의와 프로파일링 데이터에서 직접 생성되어 인용할 근거가 없으므로 null이며,
    /// 이때는 근거 명세서 줄을 내지 않는다.
    /// </summary>
    public static string FormatUnverifiedDocument(
        string body,
        VerificationOutcome? sourceOutcome,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp)
    {
        var sourceLine = sourceOutcome is { } source
            ? $"근거 명세서 검증 상태: {StatusLabel(source)}\n"
            : string.Empty;

        var yamlFrontMatter = $@"---
검증 상태: 검증 없음 # 이 문서는 L1/L2 검증을 거치지 않음
{sourceLine}---

";

        var statusNote = sourceOutcome is { } noted
            ? $"> **검증 상태**: 이 문서는 검증 파이프라인을 거치지 않았습니다. 근거 명세서(Spec.md)는 '{StatusLabel(noted)}' 상태입니다.\n"
            : "> **검증 상태**: 이 문서는 검증 파이프라인을 거치지 않았습니다. 내용을 직접 검토하십시오.\n";

        return yamlFrontMatter + MetadataHeader(provider, modelName, effort, timestamp, string.Empty, statusNote) + body;
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
