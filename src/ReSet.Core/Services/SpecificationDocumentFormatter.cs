namespace ReSet.Core.Services;

public static class SpecificationDocumentFormatter
{
    public static string Format(
        string specification,
        ReviewResult? review,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp)
    {
        var yamlFrontMatter = string.Empty;
        var scoreHeader = string.Empty;
        if (review is not null)
        {
            yamlFrontMatter = $@"---
종합 신뢰도: {review.NormalizedScore} # 100점 만점 기준 AI 최종 신뢰도
정합성 점수: {review.ScoreAccuracy}/10 # SQL 대비 기능 정합성
CRUD 점수: {review.ScoreCrud}/10 # 데이터 변경 및 조회 검증
인터페이스 점수: {review.ScoreInterface}/10 # 파라미터 및 반환셋 정합성
가독성 점수: {review.ScoreReadability}/10 # 코드 가독성 및 표준 준수
예외처리 점수: {review.ScoreException}/10 # 트랜잭션 격리 및 에러 처리
---

";
            scoreHeader = $"> **AI 최종 신뢰도**: {review.NormalizedScore}/100점 (정합성: {review.ScoreAccuracy}, CRUD: {review.ScoreCrud}, 연동: {review.ScoreInterface}, 가독성: {review.ScoreReadability}, 예외: {review.ScoreException})\n";
        }

        var effortSuffix = string.IsNullOrWhiteSpace(effort) ? string.Empty : $", Effort: {effort}";
        var metadataHeader = $"> [!NOTE]\n> **문서 작성일시**: {timestamp:yyyy-MM-dd HH:mm:ss}\n> **분석 AI 정보**: {provider} ({modelName}{effortSuffix})\n{scoreHeader}\n";

        return yamlFrontMatter + metadataHeader + specification;
    }
}
