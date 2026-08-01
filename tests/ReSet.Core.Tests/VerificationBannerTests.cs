using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class VerificationBannerTests
{
    [Fact]
    public void QualityRejected_MatchesTheTextTheOrchestratorUsedBefore()
    {
        var review = new ReviewResult
        {
            ScoreAccuracy = 6,
            ScoreCrud = 7,
            ScoreInterface = 5,
            ScoreReadability = 8,
            ScoreException = 4,
            FeedbackComment = "첫 줄\n둘째 줄"
        };

        var banner = VerificationBanner.QualityRejected(review, 8);

        // 아래 기대값은 통합 전 세 호출부가 만들던 문자열과 글자 단위로 같아야 한다.
        var expected =
            "\n> [!CAUTION]\n> **[품질 불합격] 정합성/가독성 기준 미달 (최종 신뢰도 점수: 60/100)**\n"
            + "> - **평가 점수**: 정합성 6/10, CRUD 7/10, 인터페이스 5/10, 가독성 8/10, 예외 4/10 (기준 점수: 8/10)\n"
            + "> - **최종 Critic 결함 피드백**:\n>   첫 줄\n>   둘째 줄\n\n";
        Assert.Equal(expected, banner);
    }

    [Fact]
    public void QualityRejected_ToleratesMissingFeedbackComment()
    {
        var review = new ReviewResult { ScoreAccuracy = 1 };

        var banner = VerificationBanner.QualityRejected(review, 8);

        Assert.Contains("> - **최종 Critic 결함 피드백**:\n>   \n", banner);
    }

    [Fact]
    public void ReviewNotRun_KeepsTheExistingSentenceAndAddsTheReason()
    {
        var banner = VerificationBanner.ReviewNotRun("critic endpoint down");

        // 통합 경로가 이미 쓰던 문장이다. 기존 테스트가 이 부분 문자열을 잠그고 있다.
        Assert.Contains("L2 AI 교차 리뷰가 수행되지 않았습니다", banner);
        Assert.StartsWith("> [!NOTE]", banner);
        Assert.Contains("critic endpoint down", banner);
    }

    [Fact]
    public void L1Exhausted_ListsTheRemainingErrors()
    {
        var banner = VerificationBanner.L1Exhausted(new List<string> { "헤더 누락", "Mermaid 구문 오류" });

        Assert.StartsWith("\n> [!CAUTION]", banner);
        Assert.Contains("L1 기계 검증을 통과하지 못했습니다", banner);
        Assert.Contains("헤더 누락", banner);
        Assert.Contains("Mermaid 구문 오류", banner);
    }
}
