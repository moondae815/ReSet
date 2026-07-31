using ReSet.Core.Services;

namespace ReSet.Core.Tests;

public sealed class SpecificationDocumentFormatterTests
{
    [Fact]
    public void Format_WithReview_WritesRootEquivalentYamlAndNoteHeader()
    {
        var review = new ReviewResult
        {
            ScoreAccuracy = 10,
            ScoreCrud = 9,
            ScoreInterface = 8,
            ScoreReadability = 7,
            ScoreException = 6
        };

        var result = SpecificationDocumentFormatter.Format(
            "# 본문",
            review,
            "OpenAI",
            "gpt-test",
            "high",
            new DateTime(2026, 7, 31, 19, 4, 19));

        Assert.Contains("종합 신뢰도: 80", result);
        Assert.Contains("> [!NOTE]", result);
        Assert.Contains("> **문서 작성일시**: 2026-07-31 19:04:19", result);
        Assert.Contains("> **분석 AI 정보**: OpenAI (gpt-test, Effort: high)", result);
        Assert.Contains(
            "> **AI 최종 신뢰도**: 80/100점 (정합성: 10, CRUD: 9, 연동: 8, 가독성: 7, 예외: 6)",
            result);
        Assert.EndsWith("# 본문", result);
    }
}
