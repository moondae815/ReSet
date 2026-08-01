using ReSet.Core.Models;

namespace ReSet.Core.Services;

public static class SpecificationDocumentFormatter
{
    public static string Format(
        string specification,
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

        var statusLabel = outcome switch
        {
            VerificationOutcome.Passed => "통과",
            VerificationOutcome.QualityRejected => "품질 미달",
            VerificationOutcome.ReviewNotRun => "리뷰 미수행",
            VerificationOutcome.L1Exhausted => "L1 미통과",
            _ => "알 수 없음"
        };

        var scoreLines = showScores
            ? $@"
종합 신뢰도: {review!.NormalizedScore} # 100점 만점 기준 AI 최종 신뢰도
정합성 점수: {review.ScoreAccuracy}/10 # SQL 대비 기능 정합성
CRUD 점수: {review.ScoreCrud}/10 # 데이터 변경 및 조회 검증
인터페이스 점수: {review.ScoreInterface}/10 # 파라미터 및 반환셋 정합성
가독성 점수: {review.ScoreReadability}/10 # 코드 가독성 및 표준 준수
예외처리 점수: {review.ScoreException}/10 # 트랜잭션 격리 및 에러 처리"
            : string.Empty;

        var yamlFrontMatter = $@"---
검증 상태: {statusLabel} # 검증 파이프라인 종료 상태{scoreLines}
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

        var effortSuffix = string.IsNullOrWhiteSpace(effort) ? string.Empty : $", Effort: {effort}";
        var metadataHeader = $"> [!NOTE]\n> **문서 작성일시**: {timestamp:yyyy-MM-dd HH:mm:ss}\n> **분석 AI 정보**: {provider} ({modelName}{effortSuffix})\n{scoreHeader}{statusNote}\n";

        return yamlFrontMatter + metadataHeader + specification;
    }
}
