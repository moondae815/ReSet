using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests;

public sealed class VerificationDocumentFormatterTests
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

        var result = VerificationDocumentFormatter.FormatSpecification(
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

        var result = VerificationDocumentFormatter.FormatSpecification(
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

        var result = VerificationDocumentFormatter.FormatSpecification(
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

        var result = VerificationDocumentFormatter.FormatSpecification(
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

        var result = VerificationDocumentFormatter.FormatSpecification(
            "# 본문", review, VerificationOutcome.QualityRejected,
            "OpenAI", "gpt-test", null, new DateTime(2026, 7, 31, 19, 4, 19));

        Assert.Contains("검증 상태: 품질 미달", result);
        Assert.Contains("종합 신뢰도: 50", result);
    }

    [Fact]
    public void FormatConsolidatedPlan_UsesPlanSpecificScoreDescriptions()
    {
        // 통합 계획서의 Critic 기준(AiService.cs:1997-2017)은 명세서 기준과 다르다.
        // 명세서 설명 주석을 계획서에 그대로 쓰면 문서가 거짓말을 한다.
        var review = new ReviewResult
        {
            HasDefects = false,
            ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9,
            ScoreReadability = 9, ScoreException = 9
        };

        var result = VerificationDocumentFormatter.FormatConsolidatedPlan(
            "## 통합 배치 아키텍처 개요", review, VerificationOutcome.Passed,
            "anthropic", "claude-opus-5", "high", new DateTime(2026, 8, 3, 14, 22, 1));

        Assert.Contains("가독성 점수: 9/10 # 다이어그램 문법 및 가독성", result);
        Assert.DoesNotContain("코드 가독성 및 표준 준수", result);
        Assert.Contains("검증 상태: 통과", result);
    }

    [Fact]
    public void FormatConsolidatedPlan_OmitsScoresWhenTheOutcomeIsNotScored()
    {
        // 점수 노출 규칙은 FormatSpecification과 동일하다: Passed 또는 QualityRejected에서만 싣는다.
        var review = new ReviewResult
        {
            HasDefects = false,
            ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9,
            ScoreReadability = 9, ScoreException = 9
        };

        var result = VerificationDocumentFormatter.FormatConsolidatedPlan(
            "## 통합 배치 아키텍처 개요", review, VerificationOutcome.ReviewNotRun,
            "anthropic", "claude-opus-5", "high", new DateTime(2026, 8, 3, 14, 22, 1));

        Assert.Contains("검증 상태: 리뷰 미수행", result);
        Assert.DoesNotContain("종합 신뢰도", result);
        Assert.DoesNotContain("가독성 점수", result);
    }

    [Fact]
    public void FormatUnverifiedPlan_StatesThatTheDocumentItselfWasNeverVerified()
    {
        // 단일 SP의 BatchMigrationPlan.md는 L1도 L2도 거치지 않는다(Program.cs:662).
        var result = VerificationDocumentFormatter.FormatUnverifiedPlan(
            "# 배치 전환 계획", VerificationOutcome.Passed,
            "anthropic", "claude-opus-5", "high", new DateTime(2026, 8, 3, 14, 22, 1));

        Assert.Contains("검증 상태: 검증 없음", result);
        Assert.Contains("근거 명세서 검증 상태: 통과", result);
        Assert.Contains("이 계획서는 검증 파이프라인을 거치지 않았습니다", result);
        Assert.Contains("# 배치 전환 계획", result);
    }

    [Fact]
    public void FormatUnverifiedPlan_NeverEmitsAnyScore()
    {
        // 이 진입점은 ReviewResult 파라미터를 받지 않는다. 점수가 실릴 경로 자체가 없어야 한다.
        var result = VerificationDocumentFormatter.FormatUnverifiedPlan(
            "# 배치 전환 계획", VerificationOutcome.QualityRejected,
            "anthropic", "claude-opus-5", null, new DateTime(2026, 8, 3, 14, 22, 1));

        Assert.Contains("근거 명세서 검증 상태: 품질 미달", result);
        Assert.DoesNotContain("종합 신뢰도", result);
        Assert.DoesNotContain("AI 최종 신뢰도", result);
        Assert.DoesNotContain("/10", result);
    }

    [Fact]
    public void FormatSpecification_KeepsSpecificationScoreDescriptions()
    {
        // 개명 과정에서 명세서 설명이 계획서 설명으로 오염되지 않았는지 고정한다.
        var review = new ReviewResult
        {
            HasDefects = false,
            ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9,
            ScoreReadability = 9, ScoreException = 9
        };

        var result = VerificationDocumentFormatter.FormatSpecification(
            "## 개요", review, VerificationOutcome.Passed,
            "anthropic", "claude-opus-5", "high", new DateTime(2026, 8, 3, 14, 22, 1));

        Assert.Contains("가독성 점수: 9/10 # 코드 가독성 및 표준 준수", result);
        Assert.Contains("정합성 점수: 9/10 # SQL 대비 기능 정합성", result);
        Assert.DoesNotContain("다이어그램 문법 및 가독성", result);
    }
}
