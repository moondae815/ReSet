using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class VerificationBannerTests
{
    // 실패 사유는 실제로 기준에 미달한 항목에서 계산해야 한다.
    // 이전에는 "정합성/가독성"이 하드코딩되어 있어, 가독성 8/10으로 통과한 문서에도
    // "가독성 기준 미달"이라고 적혔다. 헤더가 자기모순이면 읽는 사람이 어느 항목을
    // 고쳐야 하는지 알 수 없다.
    [Fact]
    public void QualityRejected_NamesEveryCategoryBelowThreshold_InScoreLineOrder()
    {
        var review = new ReviewResult
        {
            ScoreAccuracy = 6,
            ScoreCrud = 7,
            ScoreInterface = 5,
            ScoreReadability = 8,   // 유일하게 기준(8)을 만족한다
            ScoreException = 4,
            FeedbackComment = "첫 줄\n둘째 줄"
        };

        var banner = VerificationBanner.QualityRejected(review, 8);

        var expected =
            "\n> [!CAUTION]\n> **[품질 불합격] 정합성/CRUD/인터페이스/예외 기준 미달 (최종 신뢰도 점수: 60/100)**\n"
            + "> - **평가 점수**: 정합성 6/10, CRUD 7/10, 인터페이스 5/10, 가독성 8/10, 예외 4/10 (기준 점수: 8/10)\n"
            + "> - **최종 Critic 결함 피드백**:\n>   첫 줄\n>   둘째 줄\n\n";
        Assert.Equal(expected, banner);
    }

    // 2026-08-04 dbo.UP_Util_PG_Client_CMRate_Ins 산출물에서 실제로 나온 점수 조합.
    // 다섯 항목 중 인터페이스 하나만 미달인데 배너는 "정합성/가독성 미달"이라고 적었다.
    [Fact]
    public void QualityRejected_SingleFailingCategory_DoesNotNameThePassingOnes()
    {
        var review = new ReviewResult
        {
            ScoreAccuracy = 9,
            ScoreCrud = 10,
            ScoreInterface = 7,
            ScoreReadability = 10,
            ScoreException = 8
        };

        var banner = VerificationBanner.QualityRejected(review, 8);

        Assert.Contains("**[품질 불합격] 인터페이스 기준 미달", banner);
        Assert.DoesNotContain("정합성 기준 미달", banner);
        Assert.DoesNotContain("가독성 기준 미달", banner);
    }

    // 점수는 모두 기준을 넘겼는데 Critic이 결함을 지적한 경우다. 미달 항목이 없으므로
    // 항목명을 지어내면 안 된다.
    [Fact]
    public void QualityRejected_NoCategoryBelowThreshold_ReportsCriticDefectInstead()
    {
        var review = new ReviewResult
        {
            HasDefects = true,
            ScoreAccuracy = 9,
            ScoreCrud = 9,
            ScoreInterface = 9,
            ScoreReadability = 9,
            ScoreException = 9
        };

        var banner = VerificationBanner.QualityRejected(review, 8);

        Assert.Contains("**[품질 불합격] Critic 결함 지적", banner);
        Assert.DoesNotContain("기준 미달", banner);
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

    [Fact]
    public void UnresolvedReferences_ListsEveryUnanalyzedObjectName()
    {
        var banner = VerificationBanner.UnresolvedReferences(
            new[] { "dbo.USP_Calc", "dbo.FN_Rate" });

        Assert.Contains("> [!CAUTION]", banner);
        Assert.Contains("[참조 미완]", banner);
        Assert.Contains(">   - dbo.USP_Calc", banner);
        Assert.Contains(">   - dbo.FN_Rate", banner);
    }

    [Fact]
    public void UnresolvedReferences_EmptyList_StillRendersTheHeadingWithoutBlankBullets()
    {
        // 호출부가 빈 목록으로 부르는 일은 없어야 하지만, 부르더라도
        // 내용 없는 불릿이 문서에 남지 않아야 한다.
        var banner = VerificationBanner.UnresolvedReferences(Array.Empty<string>());

        Assert.Contains("[참조 미완]", banner);
        Assert.DoesNotContain(">   - \n", banner);
    }

    [Fact]
    public void L1Exhausted_EmptyList_RendersThePlaceholderVerbatim()
    {
        // 헬퍼 리팩터링이 이 문자열을 바꾸면 즉시 드러나야 한다.
        var banner = VerificationBanner.L1Exhausted(Array.Empty<string>());

        Assert.Contains(">   - (상세 오류가 기록되지 않았습니다.)", banner);
    }

    [Fact]
    public void UnresolvedReferences_EmptyList_RendersThePlaceholderVerbatim()
    {
        var banner = VerificationBanner.UnresolvedReferences(Array.Empty<string>());

        Assert.Contains(">   - (미분석 객체명이 기록되지 않았습니다.)", banner);
    }

    // 구제가 아닐 때 배너가 한 글자도 달라지면 안 된다.
    // Task 3의 통일 리팩터링이 정상 소진 경로의 출력을 바꾸지 않았음을 지키는 안전망이다.
    [Fact]
    public void QualityRejected_WithoutRescue_OmitsTheAdoptionLine()
    {
        var review = new ReviewResult
        {
            ScoreAccuracy = 9,
            ScoreCrud = 10,
            ScoreInterface = 9,
            ScoreReadability = 9,
            ScoreException = 7,
            FeedbackComment = "예외 처리를 보완하십시오."
        };

        var banner = VerificationBanner.QualityRejected(review, 8);

        Assert.DoesNotContain("채택 경위", banner);
    }

    // 구제본은 마지막 시도가 아니다. 뒤따르는 점수표가 어느 시도의 것인지 먼저 밝혀야 한다.
    [Fact]
    public void QualityRejected_WithRescue_LeadsWithTheAdoptionLine()
    {
        var review = new ReviewResult
        {
            ScoreAccuracy = 9,
            ScoreCrud = 10,
            ScoreInterface = 9,
            ScoreReadability = 9,
            ScoreException = 7,
            FeedbackComment = "예외 처리를 보완하십시오."
        };

        var banner = VerificationBanner.QualityRejected(
            review, 8, new RescueContext(RetryAbortReason.GenerationFailed, 3, 2));

        var lines = banner.Split('\n');
        var headerIndex = Array.FindIndex(lines, line => line.Contains("[품질 불합격]"));

        Assert.Contains("채택 경위", lines[headerIndex + 1]);
        Assert.Contains("3차 시도가 AI 생성 호출 실패로 중단되어", lines[headerIndex + 1]);
        Assert.Contains("2차 시도를 채택했습니다", lines[headerIndex + 1]);
        Assert.Contains("평가 점수", lines[headerIndex + 2]);
    }

    [Theory]
    [InlineData(RetryAbortReason.GenerationFailed, "AI 생성 호출 실패")]
    [InlineData(RetryAbortReason.L1Exhausted, "L1 기계 검증 실패")]
    [InlineData(RetryAbortReason.ReviewFailed, "L2 리뷰 호출 실패")]
    public void QualityRejected_NamesTheAbortCause(RetryAbortReason reason, string expectedCause)
    {
        var review = new ReviewResult
        {
            ScoreAccuracy = 9,
            ScoreCrud = 10,
            ScoreInterface = 9,
            ScoreReadability = 9,
            ScoreException = 7
        };

        var banner = VerificationBanner.QualityRejected(review, 8, new RescueContext(reason, 3, 2));

        Assert.Contains(expectedCause, banner);
    }

    // Task 10: 하한 미달 단계를 배너에 표기. stepFloorViolations(Task 8)의 값은
    // 이미 "{Code} (사유)" 형식으로 완성된 표시 문자열이다.
    [Fact]
    public void StepFloorViolations_ListsEveryStep()
    {
        var banner = VerificationBanner.StepFloorViolations(new[] { "S10 (하한 미달)", "S06 (생성 실패)" });

        Assert.Contains("하한 미달", banner);
        Assert.Contains(">   - S10 (하한 미달)", banner);
        Assert.Contains(">   - S06 (생성 실패)", banner);
    }

    [Fact]
    public void StepFloorViolations_WithEmptyList_StillRendersPlaceholder()
    {
        var banner = VerificationBanner.StepFloorViolations(new string[0]);

        Assert.Contains(">   - ", banner);
    }

    // 목차 커버리지 누락: StepFloorViolations(내용이 부실한 단계)와 다른 사실이다 —
    // 이건 그 프로시저를 다룰 단계 자체가 목차에 없다는 뜻이다. 개수 대신
    // 프로시저명을 실어야 읽는 사람이 무엇을 직접 확인할지 안다.
    [Fact]
    public void UncoveredProcedures_NamesEveryUncoveredProcedure()
    {
        var banner = VerificationBanner.UncoveredProcedures(
            new[] { "dbo.UP_UTIL_SETTLE_EXCEPTION_PROC", "dbo.UP_UTIL_SETTLE_COMM_UPD" });

        Assert.StartsWith("\n> [!WARNING]", banner);
        Assert.Contains("[커버리지 누락]", banner);
        Assert.Contains(">   - dbo.UP_UTIL_SETTLE_EXCEPTION_PROC", banner);
        Assert.Contains(">   - dbo.UP_UTIL_SETTLE_COMM_UPD", banner);
    }

    [Fact]
    public void UncoveredProcedures_EmptyList_RendersThePlaceholderVerbatim()
    {
        var banner = VerificationBanner.UncoveredProcedures(Array.Empty<string>());

        Assert.Contains(">   - (프로시저명이 기록되지 않았습니다.)", banner);
    }
}
