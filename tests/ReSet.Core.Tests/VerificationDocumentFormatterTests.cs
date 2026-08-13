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

        var result = VerificationDocumentFormatter.FormatVerifiedDocument(
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

        var result = VerificationDocumentFormatter.FormatVerifiedDocument(
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

        var result = VerificationDocumentFormatter.FormatVerifiedDocument(
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

        var result = VerificationDocumentFormatter.FormatVerifiedDocument(
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

        var result = VerificationDocumentFormatter.FormatVerifiedDocument(
            "# 본문", review, VerificationOutcome.QualityRejected,
            "OpenAI", "gpt-test", null, new DateTime(2026, 7, 31, 19, 4, 19));

        Assert.Contains("검증 상태: 품질 미달", result);
        Assert.Contains("종합 신뢰도: 50", result);
    }

    [Fact]
    public void FormatVerifiedDocument_OmitsScoresWhenTheOutcomeIsNotScored()
    {
        // 점수 노출 규칙은 다른 종료 상태와 동일하다: Passed 또는 QualityRejected에서만 싣는다.
        var review = new ReviewResult
        {
            HasDefects = false,
            ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9,
            ScoreReadability = 9, ScoreException = 9
        };

        var result = VerificationDocumentFormatter.FormatVerifiedDocument(
            "## 통합 배치 아키텍처 개요", review, VerificationOutcome.ReviewNotRun,
            "anthropic", "claude-opus-5", "high", new DateTime(2026, 8, 3, 14, 22, 1));

        Assert.Contains("검증 상태: 리뷰 미수행", result);
        Assert.DoesNotContain("종합 신뢰도", result);
        Assert.DoesNotContain("가독성 점수", result);
    }

    [Fact]
    public void FormatUnverifiedDocument_StatesThatTheDocumentItselfWasNeverVerified()
    {
        // 단일 SP의 BatchMigrationPlan.md는 L1도 L2도 거치지 않는다(Program.cs:662).
        var result = VerificationDocumentFormatter.FormatUnverifiedDocument(
            "# 배치 전환 계획", VerificationOutcome.Passed,
            "anthropic", "claude-opus-5", "high", new DateTime(2026, 8, 3, 14, 22, 1));

        Assert.Contains("검증 상태: 검증 없음", result);
        Assert.Contains("근거 명세서 검증 상태: 통과", result);
        // 같은 메서드가 정산 정책 문서(계획서가 아니다)도 처리하게 되어 문구를 중립화했다.
        Assert.Contains("이 문서는 검증 파이프라인을 거치지 않았습니다", result);
        Assert.Contains("# 배치 전환 계획", result);
    }

    [Fact]
    public void FormatUnverifiedDocument_NeverEmitsAnyScore()
    {
        // 이 진입점은 ReviewResult 파라미터를 받지 않는다. 점수가 실릴 경로 자체가 없어야 한다.
        var result = VerificationDocumentFormatter.FormatUnverifiedDocument(
            "# 배치 전환 계획", VerificationOutcome.QualityRejected,
            "anthropic", "claude-opus-5", null, new DateTime(2026, 8, 3, 14, 22, 1));

        Assert.Contains("근거 명세서 검증 상태: 품질 미달", result);
        Assert.DoesNotContain("종합 신뢰도", result);
        Assert.DoesNotContain("AI 최종 신뢰도", result);
        Assert.DoesNotContain("/10", result);
    }

    [Fact]
    public void FormatVerifiedDocument_EmitsScoreLinesWithoutDescriptiveComments()
    {
        // 점수 줄의 설명 주석은 Critic 프롬프트를 사람이 옮겨 적은 것이었고, 둘의 연결을
        // 강제하는 장치가 없어 드리프트했다 - 가독성 설명("코드 가독성 및 표준 준수")은
        // 실제로 거짓이 되어 있었다(AiService.cs:1585-1589는 Mermaid 문법을 채점한다).
        // 주석 자체를 없앴으므로 거짓이 될 문구가 존재하지 않는다.
        var review = new ReviewResult
        {
            HasDefects = false,
            ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9,
            ScoreReadability = 9, ScoreException = 9
        };

        var result = VerificationDocumentFormatter.FormatVerifiedDocument(
            "## 개요", review, VerificationOutcome.Passed,
            "anthropic", "claude-opus-5", "high", new DateTime(2026, 8, 3, 14, 22, 1));

        Assert.Contains("가독성 점수: 9/10", result);
        Assert.DoesNotContain("가독성 점수: 9/10 #", result);
        Assert.DoesNotContain("코드 가독성 및 표준 준수", result);
        Assert.DoesNotContain("다이어그램 문법 및 가독성", result);
        Assert.DoesNotContain("SQL 대비 기능 정합성", result);

        // 필드 자체를 설명하는 이 주석은 남는다 - 프롬프트에서 복제한 것이 아니라
        // 드리프트할 대상이 없다.
        Assert.Contains("검증 상태: 통과 # 검증 파이프라인 종료 상태", result);
    }

    [Fact]
    public void FormatUnverifiedDocument_WithNoSource_StatesNoVerificationAndCitesNothing()
    {
        // 정산 정책 문서는 SP 정의와 프로파일링 데이터에서 직접 생성되어 명세서를
        // 거치지 않는다. 인용할 근거가 없으므로 근거 명세서 줄을 내서는 안 된다.
        var result = VerificationDocumentFormatter.FormatUnverifiedDocument(
            "# 정산 정책 룰북", null,
            "anthropic", "claude-opus-5", "high", new DateTime(2026, 8, 3, 14, 22, 1));

        Assert.Contains("검증 상태: 검증 없음", result);
        Assert.Contains("이 문서는 검증 파이프라인을 거치지 않았습니다", result);
        Assert.Contains("내용을 직접 검토하십시오", result);
        Assert.DoesNotContain("근거 명세서", result);
        Assert.Contains("# 정산 정책 룰북", result);

        // 점수는 어떤 경로로도 실릴 수 없다.
        Assert.DoesNotContain("종합 신뢰도", result);
        Assert.DoesNotContain("/10", result);
    }

    [Fact]
    public void FormatVerifiedDocument_WithoutScope_OmitsTheScopeLine()
    {
        var result = VerificationDocumentFormatter.FormatVerifiedDocument(
            "# 본문", null, VerificationOutcome.Passed,
            "OpenAI", "gpt-test", null, new DateTime(2026, 8, 3, 10, 0, 0));

        Assert.DoesNotContain("분석 범위", result);
    }

    [Fact]
    public void FormatVerifiedDocument_DirectScope_WritesTheRecursiveModeLabel()
    {
        var result = VerificationDocumentFormatter.FormatVerifiedDocument(
            "# 본문", null, VerificationOutcome.Passed,
            "OpenAI", "gpt-test", null, new DateTime(2026, 8, 3, 10, 0, 0),
            AnalysisScope.Direct);

        Assert.Contains("분석 범위: 직접 의존성", result);
    }

    [Fact]
    public void FormatVerifiedDocument_TransitiveScope_WritesTheSingleObjectLabel()
    {
        var result = VerificationDocumentFormatter.FormatVerifiedDocument(
            "# 본문", null, VerificationOutcome.Passed,
            "OpenAI", "gpt-test", null, new DateTime(2026, 8, 3, 10, 0, 0),
            AnalysisScope.Transitive);

        Assert.Contains("분석 범위: 전이 의존성", result);
    }

    [Fact]
    public void FormatVerifiedDocument_ScopeLineLivesInsideTheYamlBlockAlongsideScores()
    {
        var review = new ReviewResult
        {
            ScoreAccuracy = 10, ScoreCrud = 9, ScoreInterface = 8,
            ScoreReadability = 7, ScoreException = 6
        };

        var result = VerificationDocumentFormatter.FormatVerifiedDocument(
            "# 본문", review, VerificationOutcome.Passed,
            "OpenAI", "gpt-test", null, new DateTime(2026, 8, 3, 10, 0, 0),
            AnalysisScope.Direct);

        var yamlEnd = result.IndexOf("\n---", 3, StringComparison.Ordinal);
        var yaml = result[..yamlEnd];

        Assert.Contains("검증 상태: 통과", yaml);
        Assert.Contains("분석 범위: 직접 의존성", yaml);
        Assert.Contains("종합 신뢰도: 80", yaml);
    }

    // 사람이 점수를 믿을지 판단할 재료다. 실측에서 계약을 20군데 깬 문서가
    // 92점, 100% 지킨 문서가 88점이었다 - 점수만으로는 구분되지 않는다.
    [Fact]
    public void FormatVerifiedDocument_WithCoverage_RendersTheStepRatio()
    {
        var review = new ReviewResult
        {
            HasDefects = false, ScoreAccuracy = 9, ScoreCrud = 9,
            ScoreInterface = 8, ScoreReadability = 9, ScoreException = 9
        };

        var markdown = VerificationDocumentFormatter.FormatVerifiedDocument(
            "본문", review, VerificationOutcome.Passed, "OpenAI", "gpt-4o", "high",
            new DateTime(2026, 8, 13),
            scope: null,
            coverage: new VerificationCoverage(19, 17, false));

        Assert.Contains("단계 검증: 17/19", markdown);
    }

    // 분모가 없는 상태를 "0/0"으로 적으면 비율처럼 보이는 거짓이 된다.
    [Fact]
    public void FormatVerifiedDocument_WhenSplitDidNotRun_SaysSoInsteadOfARatio()
    {
        var review = new ReviewResult
        {
            HasDefects = false, ScoreAccuracy = 9, ScoreCrud = 9,
            ScoreInterface = 9, ScoreReadability = 9, ScoreException = 9
        };

        var markdown = VerificationDocumentFormatter.FormatVerifiedDocument(
            "본문", review, VerificationOutcome.Passed, "OpenAI", "gpt-4o", "high",
            new DateTime(2026, 8, 13),
            scope: null,
            coverage: new VerificationCoverage(null, 0, false));

        Assert.Contains("단계 검증: 미실행", markdown);
        Assert.DoesNotContain("0/0", markdown);
    }

    // 이 포매터는 단일 SP 명세서도 쓴다. 단계 개념이 없는 문서에 이 줄이
    // 붙으면 매 명세서마다 의미 없는 필드가 생긴다.
    [Fact]
    public void FormatVerifiedDocument_WithoutCoverage_OmitsTheLineEntirely()
    {
        var review = new ReviewResult
        {
            HasDefects = false, ScoreAccuracy = 9, ScoreCrud = 9,
            ScoreInterface = 9, ScoreReadability = 9, ScoreException = 9
        };

        var markdown = VerificationDocumentFormatter.FormatVerifiedDocument(
            "본문", review, VerificationOutcome.Passed, "OpenAI", "gpt-4o", "high",
            new DateTime(2026, 8, 13));

        Assert.DoesNotContain("단계 검증", markdown);
    }
}
