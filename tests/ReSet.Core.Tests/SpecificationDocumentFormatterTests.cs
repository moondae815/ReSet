using ReSet.Core.Models;
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
            VerificationOutcome.Passed,
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

    [Fact]
    public void Format_Passed_WritesPassedStatusAndScores()
    {
        var review = new ReviewResult
        {
            ScoreAccuracy = 10, ScoreCrud = 9, ScoreInterface = 8,
            ScoreReadability = 7, ScoreException = 6
        };

        var result = SpecificationDocumentFormatter.Format(
            "# 본문", review, VerificationOutcome.Passed,
            "OpenAI", "gpt-test", "high", new DateTime(2026, 7, 31, 19, 4, 19));

        Assert.StartsWith("---", result);
        Assert.Contains("검증 상태: 통과", result);
        Assert.Contains("종합 신뢰도: 80", result);
    }

    [Fact]
    public void Format_ReviewNotRun_OmitsScoresEvenWhenAReviewObjectIsPresent()
    {
        // 1차 시도의 리뷰 결과가 남아 있어도 최종 상태가 미수행이면 점수를 실으면 안 된다.
        var staleReview = new ReviewResult
        {
            ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10,
            ScoreReadability = 10, ScoreException = 10
        };

        var result = SpecificationDocumentFormatter.Format(
            "# 본문", staleReview, VerificationOutcome.ReviewNotRun,
            "OpenAI", "gpt-test", null, new DateTime(2026, 7, 31, 19, 4, 19));

        Assert.Contains("검증 상태: 리뷰 미수행", result);
        Assert.DoesNotContain("종합 신뢰도", result);
        Assert.DoesNotContain("AI 최종 신뢰도", result);
    }

    [Fact]
    public void Format_L1Exhausted_OmitsScoresEvenWhenAReviewObjectIsPresent()
    {
        var staleReview = new ReviewResult { ScoreAccuracy = 9 };

        var result = SpecificationDocumentFormatter.Format(
            "# 본문", staleReview, VerificationOutcome.L1Exhausted,
            "OpenAI", "gpt-test", null, new DateTime(2026, 7, 31, 19, 4, 19));

        Assert.Contains("검증 상태: L1 미통과", result);
        Assert.DoesNotContain("종합 신뢰도", result);
    }

    [Fact]
    public void Format_QualityRejected_KeepsScores()
    {
        var review = new ReviewResult
        {
            ScoreAccuracy = 5, ScoreCrud = 5, ScoreInterface = 5,
            ScoreReadability = 5, ScoreException = 5
        };

        var result = SpecificationDocumentFormatter.Format(
            "# 본문", review, VerificationOutcome.QualityRejected,
            "OpenAI", "gpt-test", null, new DateTime(2026, 7, 31, 19, 4, 19));

        Assert.Contains("검증 상태: 품질 미달", result);
        Assert.Contains("종합 신뢰도: 50", result);
    }
}
