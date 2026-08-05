# 재시도 중단 시 최선본 구제 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 재시도 루프가 비정상 중단될 때 이미 검증을 마친 최고점 문서를 버리지 않고 채택하게 한다.

**Architecture:** 채택 규칙을 `RetryRescue` 한 곳이 소유하고, 순차 SP 루프와 배치 계획 루프의 여덟 자리가 모두 그 관용구를 쓴다. 후보가 없을 때의 동작은 한 곳도 바꾸지 않는다 — 구제는 순수한 추가다. 곁들여 L1 실패 회차가 누적 Critic 피드백을 덮어쓰던 문제를 합성으로 바꾼다.

**Tech Stack:** .NET 10 / C#, xUnit, NSubstitute

설계 문서: [2026-08-05-retry-rescue-design.md](../specs/2026-08-05-retry-rescue-design.md)

## Global Constraints

- 대상 파일은 모두 `src/ReSet.Core/`와 `tests/ReSet.Core.Tests/` 아래에 있다. 저장소 루트에서 명령을 실행한다.
- 빌드: `dotnet build ReSet.slnx` / 테스트: `dotnet test tests/ReSet.Core.Tests`
- 착수 시점 기준 테스트는 616건 전부 통과 상태다. 어떤 태스크도 이 수를 줄여서는 안 된다.
- `OperationCanceledException`을 잡거나 감싸지 마라. `CancellationPolicyTests`가 Roslyn으로 `src/` 전체를 검사해 실패시킨다.
- 주석과 문서는 한국어로 쓴다. 클래스·메서드명은 영어를 유지한다.
- 텍스트·규칙은 단일 소유자 클래스에 둔다. 배너 문구는 `VerificationBanner`, 피드백 조립은 `CriticFeedbackLog`, 채택 규칙은 `RetryRescue`가 소유한다. 호출부에서 문구를 짜지 마라.
- `VerificationPipelineOrchestrator.cs`의 순차 루프와 배치 루프는 **들여쓰기 깊이가 다르다.** 순차 루프가 한 단계 더 깊다. 코드를 복사해 옮길 때 반드시 맞춘다.

---

## Task 1: 배너에 채택 경위 줄 추가

**Files:**
- Create: `src/ReSet.Core/Services/RescueContext.cs`
- Modify: `src/ReSet.Core/Services/VerificationBanner.cs`
- Test: `tests/ReSet.Core.Tests/VerificationBannerTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `enum RetryAbortReason { GenerationFailed, L1Exhausted, ReviewFailed }`, `sealed record RescueContext(RetryAbortReason Reason, int AbortedAttempt, int AdoptedAttempt)`, `VerificationBanner.QualityRejected(ReviewResult review, int scoreThreshold, RescueContext? rescue = null)`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/VerificationBannerTests.cs` 맨 위의 using 목록에 `using System;`을 추가한다(현재 `System.Collections.Generic`만 있어 `Array.FindIndex`를 못 쓴다). 그리고 클래스 안에 아래 셋을 넣는다.

```csharp
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
```

- [ ] **Step 2: 실패 확인**

실행: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationBannerTests"`

기대: 컴파일 실패 — `RescueContext`와 `RetryAbortReason`이 존재하지 않는다.

- [ ] **Step 3: 최소 구현 — 새 타입**

`src/ReSet.Core/Services/RescueContext.cs`를 만든다.

```csharp
namespace ReSet.Core.Services
{
    /// <summary>
    /// 재시도 루프가 비정상으로 끝난 이유. 정상 소진에는 해당하는 값이 없다 —
    /// 그 경우 호출부가 null을 넘긴다.
    /// </summary>
    public enum RetryAbortReason
    {
        /// <summary>AI 생성 호출이 예외를 던졌거나 빈 응답을 반환했다.</summary>
        GenerationFailed,

        /// <summary>L1 기계 검증 재시도를 모두 소진했다.</summary>
        L1Exhausted,

        /// <summary>L2 리뷰 호출이 실패했다.</summary>
        ReviewFailed
    }

    /// <summary>
    /// 구제가 일어난 경위. 세 값은 항상 함께 움직이므로 하나로 묶는다 —
    /// 선택 인자 셋으로 흩어 놓으면 한둘만 넘기는 호출부가 생긴다.
    /// </summary>
    public sealed record RescueContext(RetryAbortReason Reason, int AbortedAttempt, int AdoptedAttempt);
}
```

- [ ] **Step 4: 최소 구현 — 배너**

`src/ReSet.Core/Services/VerificationBanner.cs`의 `QualityRejected`를 아래로 교체한다. 기존 한 줄 식(expression-bodied)을 유지하되 `RescueLine(rescue)`를 헤더와 평가 점수 사이에 끼운다.

```csharp
    public static string QualityRejected(ReviewResult review, int scoreThreshold, RescueContext? rescue = null) =>
        $"\n> [!CAUTION]\n> **[품질 불합격] {RejectionReason(review, scoreThreshold)} (최종 신뢰도 점수: {review.NormalizedScore}/100)**\n"
        + RescueLine(rescue)
        + $"> - **평가 점수**: 정합성 {review.ScoreAccuracy}/10, CRUD {review.ScoreCrud}/10, 인터페이스 {review.ScoreInterface}/10, 가독성 {review.ScoreReadability}/10, 예외 {review.ScoreException}/10 (기준 점수: {scoreThreshold}/10)\n> - **최종 Critic 결함 피드백**:\n>   {review.FeedbackComment?.Replace("\n", "\n>   ")}\n\n";
```

같은 파일의 `RejectionReason` 바로 아래에 넣는다.

```csharp
    /// <summary>
    /// 구제 시에만 붙는 첫 불릿. 뒤따르는 점수표가 어느 시도의 것인지 먼저 밝힌다.
    ///
    /// "다시 돌리면 나아진다" 같은 조언은 넣지 않는다. 사실만 적고 판단은 읽는
    /// 사람에게 맡긴다 — 3차가 쿼터로 죽은 경우와 정상 수행한 경우는 재실행 가치가
    /// 다른데, 그 판단에 필요한 사실이 바로 중단 사유다.
    /// </summary>
    private static string RescueLine(RescueContext? rescue)
    {
        if (rescue == null)
        {
            return string.Empty;
        }

        var cause = rescue.Reason switch
        {
            RetryAbortReason.GenerationFailed => "AI 생성 호출 실패",
            RetryAbortReason.L1Exhausted => "L1 기계 검증 실패",
            RetryAbortReason.ReviewFailed => "L2 리뷰 호출 실패",
            _ => "알 수 없는 사유"
        };

        return $"> - **채택 경위**: {rescue.AbortedAttempt}차 시도가 {cause}로 중단되어, "
            + $"검증을 마친 {rescue.AdoptedAttempt}차 시도를 채택했습니다.\n";
    }
```

- [ ] **Step 5: 통과 확인**

실행: `dotnet test tests/ReSet.Core.Tests`

기대: 전체 통과. 기존 `QualityRejected_NamesEveryCategoryBelowThreshold_InScoreLineOrder`는 전체 문자열을 비교하므로, 이 테스트가 통과한다는 것이 곧 구제 아닌 경로의 출력이 바이트 단위로 같다는 증거다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/RescueContext.cs src/ReSet.Core/Services/VerificationBanner.cs tests/ReSet.Core.Tests/VerificationBannerTests.cs
git commit -m "feat: let the quality-rejected banner state how the attempt was adopted"
```

---

## Task 2: RetryRescue

**Files:**
- Create: `src/ReSet.Core/Services/RetryRescue.cs`
- Test: `tests/ReSet.Core.Tests/RetryRescueTests.cs` (신규)

**Interfaces:**
- Consumes: Task 1의 `RetryAbortReason`, `RescueContext`, `VerificationBanner.QualityRejected(ReviewResult, int, RescueContext?)`. 기존 `BestAttempt`의 `Markdown`/`Review`/`AttemptNumber`/`TryRecord(int, string, ReviewResult)`
- Produces: `sealed record RescuedAttempt(string Markdown, ReviewResult Review, int AttemptNumber)`, `RetryRescue.TryRescue(BestAttempt best, int scoreThreshold, int abortedAttempt, RetryAbortReason? reason)` → `RescuedAttempt?`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/RetryRescueTests.cs`를 만든다.

```csharp
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class RetryRescueTests
    {
        private static ReviewResult Review() => new()
        {
            HasDefects = true,
            ScoreAccuracy = 9,
            ScoreCrud = 10,
            ScoreInterface = 9,
            ScoreReadability = 9,
            ScoreException = 7,
            FeedbackComment = "예외 처리를 보완하십시오."
        };

        // 후보가 없으면 구제할 것이 없다. 호출부는 현행 폴백으로 가야 한다.
        [Fact]
        public void TryRescue_WithNoCandidate_ReturnsNull()
        {
            var best = new BestAttempt();

            var rescued = RetryRescue.TryRescue(best, 8, 3, RetryAbortReason.GenerationFailed);

            Assert.Null(rescued);
        }

        // 구제본은 배너가 붙은 상태로 나온다. 호출부가 배너를 다시 붙이면 안 된다.
        [Fact]
        public void TryRescue_WithCandidate_PrefixesTheBannerToTheStoredMarkdown()
        {
            var best = new BestAttempt();
            best.TryRecord(2, "본문내용", Review());

            var rescued = RetryRescue.TryRescue(best, 8, 3, RetryAbortReason.GenerationFailed);

            Assert.NotNull(rescued);
            Assert.Contains("[품질 불합격]", rescued!.Markdown);
            Assert.Contains("3차 시도가 AI 생성 호출 실패로 중단되어", rescued.Markdown);
            Assert.Contains("2차 시도를 채택했습니다", rescued.Markdown);
            Assert.EndsWith("본문내용", rescued.Markdown);
            Assert.Equal(2, rescued.AttemptNumber);
            Assert.Equal(88, rescued.Review.NormalizedScore);
        }

        // 정상 소진은 구제가 아니다. 루프가 끝까지 돌았으므로 중단 사유가 없다.
        [Fact]
        public void TryRescue_WithNullReason_OmitsTheAdoptionLine()
        {
            var best = new BestAttempt();
            best.TryRecord(2, "본문내용", Review());

            var rescued = RetryRescue.TryRescue(best, 8, 3, null);

            Assert.NotNull(rescued);
            Assert.Contains("[품질 불합격]", rescued!.Markdown);
            Assert.DoesNotContain("채택 경위", rescued.Markdown);
        }
    }
}
```

- [ ] **Step 2: 실패 확인**

실행: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RetryRescueTests"`

기대: 컴파일 실패 — `RetryRescue`가 존재하지 않는다.

- [ ] **Step 3: 최소 구현**

`src/ReSet.Core/Services/RetryRescue.cs`를 만든다.

```csharp
namespace ReSet.Core.Services
{
    /// <summary>구제로 채택된 문서와 그 리뷰. Markdown에는 배너가 이미 붙어 있다.</summary>
    public sealed record RescuedAttempt(string Markdown, ReviewResult Review, int AttemptNumber);

    /// <summary>
    /// 재시도 루프가 끝났을 때 보관 중인 최선본을 채택할지 결정한다.
    ///
    /// 이 클래스가 존재하는 이유: 실패 경로들이 <see cref="BestAttempt"/>의 존재를 몰라,
    /// 이미 L1을 통과하고 채점까지 받은 문서를 확보해 놓고도 버렸다. 특히 생성 호출이
    /// 예외를 던지면 SP 전체를 폐기해 좋은 문서까지 함께 사라졌다.
    ///
    /// 채택 규칙을 이곳에서만 정의한다. 호출 자리가 여덟 곳(순차 SP 루프 넷, 배치 계획
    /// 루프 넷)이라 각자 조립하면 반드시 어긋난다 — 같은 규칙이 쌍둥이 루프에 흩어져
    /// 생긴 사고가 이미 세 번 있었다.
    /// </summary>
    public static class RetryRescue
    {
        /// <summary>
        /// 보관 중인 후보가 없으면 null을 돌려준다 — 호출부는 현행 폴백으로 진행한다.
        /// reason이 null이면 정상 소진이며 배너에 채택 경위 줄이 붙지 않는다.
        ///
        /// 구제 자리에 도달한 후보는 반드시 결함을 갖는다. 결함 없는 시도도 TryRecord로
        /// 기록되지만, 그 직후 루프가 통과로 빠져나가 이 메서드까지 오지 않는다.
        /// 따라서 품질 불합격 배너가 항상 정확하다.
        /// </summary>
        public static RescuedAttempt? TryRescue(
            BestAttempt best, int scoreThreshold, int abortedAttempt, RetryAbortReason? reason)
        {
            if (best?.Review == null || best.Markdown == null)
            {
                return null;
            }

            var context = reason.HasValue
                ? new RescueContext(reason.Value, abortedAttempt, best.AttemptNumber)
                : null;

            return new RescuedAttempt(
                VerificationBanner.QualityRejected(best.Review, scoreThreshold, context) + best.Markdown,
                best.Review,
                best.AttemptNumber);
        }
    }
}
```

- [ ] **Step 4: 통과 확인**

실행: `dotnet test tests/ReSet.Core.Tests`

기대: 전체 통과.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/RetryRescue.cs tests/ReSet.Core.Tests/RetryRescueTests.cs
git commit -m "feat: add RetryRescue to own the abort-time adoption rule"
```

---

## Task 3: 정상 소진 두 자리를 RetryRescue로 통일

결함 수정이 아니라 구조 정리다. 채택 관용구가 둘로 남으면 그것이 다음 드리프트의 씨앗이 된다. 기존 테스트 두 건(`RunPipelineAsync_RetriesExhausted_AdoptsHighestScoringAttemptNotTheLast`, `RunConsolidatedPipelineAsync_RetriesExhausted_AdoptsHighestScoringAttempt`)이 안전망이므로 새 테스트를 쓰지 않는다.

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1112-1123` (순차), `:1777-1790` (배치)

**Interfaces:**
- Consumes: Task 2의 `RetryRescue.TryRescue`
- Produces: 없음

- [ ] **Step 1: 초록 기준선 확인**

실행: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RetriesExhausted"`

기대: 2건 통과. 리팩터링 전후로 이 결과가 같아야 한다.

- [ ] **Step 2: 순차 루프 교체**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`에서 아래 블록을 찾는다(`:1109-1123` 부근).

```csharp
                            // 마지막이 아니라 최고점을 채택한다. 이 분기에 도달했다는 것은
                            // 직전 시도의 리뷰가 성공했다는 뜻이므로 후보는 반드시 존재하지만,
                            // 앞으로 이 루프가 바뀌어도 깨지지 않도록 폴백을 둔다.
                            var adoptedReview = bestAttempt.Review ?? l2Result;
                            var adoptedMarkdown = bestAttempt.Markdown ?? specificationMarkdown;

                            _userInteraction.NotifyError(
                                $"{selectedOption} - [[L2 AI 리뷰]] 최종 보완 실패. " +
                                $"가장 높은 점수를 받은 {bestAttempt.AttemptNumber}차 시도({adoptedReview.NormalizedScore}/100)를 채택합니다.");

                            finalReview = adoptedReview;
                            verificationOutcome = VerificationOutcome.QualityRejected;
                            specificationMarkdown =
                                VerificationBanner.QualityRejected(adoptedReview, _criticScoreThreshold) + adoptedMarkdown;
                            break;
```

아래로 교체한다. **`rescued.Markdown`에는 배너가 이미 붙어 있다 — 다시 붙이지 마라.**

```csharp
                            // 마지막이 아니라 최고점을 채택한다. 채택 규칙은 RetryRescue가
                            // 단독으로 소유한다. 정상 소진이므로 중단 사유는 null이다.
                            // 이 분기에 도달했다는 것은 직전 시도의 리뷰가 성공했다는 뜻이라
                            // 후보는 반드시 존재하지만, 루프가 바뀌어도 깨지지 않도록 폴백을 둔다.
                            var rescued = RetryRescue.TryRescue(bestAttempt, _criticScoreThreshold, attempt, null);
                            var adoptedReview = rescued?.Review ?? l2Result;
                            var adoptedNumber = rescued?.AttemptNumber ?? attempt;

                            _userInteraction.NotifyError(
                                $"{selectedOption} - [[L2 AI 리뷰]] 최종 보완 실패. " +
                                $"가장 높은 점수를 받은 {adoptedNumber}차 시도({adoptedReview.NormalizedScore}/100)를 채택합니다.");

                            finalReview = adoptedReview;
                            verificationOutcome = VerificationOutcome.QualityRejected;
                            specificationMarkdown = rescued?.Markdown
                                ?? VerificationBanner.QualityRejected(adoptedReview, _criticScoreThreshold) + specificationMarkdown;
                            break;
```

- [ ] **Step 3: 배치 루프 교체**

같은 파일 `:1777-1790` 부근의 아래 블록을 찾는다. **들여쓰기가 순차 루프보다 한 단계 얕다.**

```csharp
                        // 마지막이 아니라 최고점을 채택한다. 이 분기에 도달했다는 것은
                        // 직전 시도의 리뷰가 성공했다는 뜻이므로 후보는 반드시 존재하지만,
                        // 앞으로 이 루프가 바뀌어도 깨지지 않도록 폴백을 둔다.
                        var adoptedReview = bestAttempt.Review ?? l2Result;
                        var adoptedPlan = bestAttempt.Markdown ?? consolidatedPlan;

                        _userInteraction.NotifyError(
                            $"{jobName} - [[L2 AI 리뷰]] 최종 보완 실패. " +
                            $"가장 높은 점수를 받은 {bestAttempt.AttemptNumber}차 시도({adoptedReview.NormalizedScore}/100)를 채택합니다.");

                        planOutcome = VerificationOutcome.QualityRejected;
                        planReview = adoptedReview;
                        consolidatedPlan =
                            VerificationBanner.QualityRejected(adoptedReview, _criticScoreThreshold) + adoptedPlan;
                        break;
```

아래로 교체한다.

```csharp
                        // 마지막이 아니라 최고점을 채택한다. 채택 규칙은 RetryRescue가
                        // 단독으로 소유한다. 정상 소진이므로 중단 사유는 null이다.
                        var rescued = RetryRescue.TryRescue(bestAttempt, _criticScoreThreshold, attempt, null);
                        var adoptedReview = rescued?.Review ?? l2Result;
                        var adoptedNumber = rescued?.AttemptNumber ?? attempt;

                        _userInteraction.NotifyError(
                            $"{jobName} - [[L2 AI 리뷰]] 최종 보완 실패. " +
                            $"가장 높은 점수를 받은 {adoptedNumber}차 시도({adoptedReview.NormalizedScore}/100)를 채택합니다.");

                        planOutcome = VerificationOutcome.QualityRejected;
                        planReview = adoptedReview;
                        consolidatedPlan = rescued?.Markdown
                            ?? VerificationBanner.QualityRejected(adoptedReview, _criticScoreThreshold) + consolidatedPlan;
                        break;
```

- [ ] **Step 4: 통과 확인**

실행: `dotnet test tests/ReSet.Core.Tests`

기대: 전체 통과. 배너가 두 번 붙었다면 `RetriesExhausted` 테스트의 `Assert.Contains("90/100", resultSpec)`는 여전히 통과하지만 문서에 `[품질 불합격]`이 두 번 나온다. 의심되면 아래로 직접 확인한다.

실행: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RetriesExhausted" -v n`

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs
git commit -m "refactor: route normal retry exhaustion through RetryRescue"
```

---

## Task 4: 생성 실패 구제

이 계획의 핵심이다. 앞의 결함들은 "좋은 문서 대신 나쁜 문서"지만 이것은 "좋은 문서 대신 아무것도 없음"이다.

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:986-989` (순차), `:1701-1704` (배치)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 2의 `RetryRescue.TryRescue`, Task 1의 `RetryAbortReason.GenerationFailed`
- Produces: 없음

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`의 `RunConsolidatedPipelineAsync_RetriesExhausted_AdoptsHighestScoringAttempt` 바로 뒤에 셋을 넣는다.

```csharp
        // 3차 시도의 생성 호출이 죽으면 2차가 만든 검증된 문서까지 함께 사라졌다.
        // 변수에는 그 내용이 그대로 남아 있는데 genSuccess가 false라 버려졌다.
        [Fact]
        public async Task RunPipelineAsync_LastGenerationThrows_AdoptsTheBestScoredAttemptInsteadOfReturningNull()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            const string body = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```\n\n";
            var spec1 = body + "시도1고유표시";
            var spec2 = body + "시도2고유표시";

            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = spec1 }),
                    _ => Task.FromResult(new AiResult { Content = spec2 }),
                    _ => throw new InvalidOperationException("generation timed out"));

            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도1고유표시")))
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 8, ScoreCrud = 8, ScoreInterface = 7, ScoreReadability = 5, ScoreException = 7 }));
            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도2고유표시")))
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 9, ScoreCrud = 10, ScoreInterface = 9, ScoreReadability = 9, ScoreException = 7 }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.NotNull(resultSpec);
            Assert.Contains("시도2고유표시", resultSpec);
            Assert.Contains("3차 시도가 AI 생성 호출 실패로 중단되어", resultSpec);
            Assert.Contains("88/100", resultSpec);
        }

        // 후보가 하나도 없으면 구제할 것이 없다. 현행대로 전체 실패다.
        [Fact]
        public async Task RunPipelineAsync_FirstGenerationThrows_StillReturnsNull()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<Task<AiResult>>(_ => throw new InvalidOperationException("generation timed out"));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.Null(resultSpec);
        }

        // 배치 계획 루프도 같은 결함을 갖는다. 한쪽만 고치면 증상이 이쪽에 남는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_LastGenerationThrows_AdoptsTheBestScoredAttempt()
        {
            var specs = new List<(string, string)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };

            const string body = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n\n";
            var plan1 = body + "계획1고유표시";
            var plan2 = body + "계획2고유표시";

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = plan1 }),
                    _ => Task.FromResult(new AiResult { Content = plan2 }),
                    _ => throw new InvalidOperationException("generation timed out"));

            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획1고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 8, ScoreCrud = 8, ScoreInterface = 7, ScoreReadability = 5, ScoreException = 7 }));
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획2고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 9, ScoreCrud = 10, ScoreInterface = 9, ScoreReadability = 9, ScoreException = 7 }));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            Assert.NotNull(result.Plan);
            Assert.Contains("계획2고유표시", result.Plan);
            Assert.Contains("3차 시도가 AI 생성 호출 실패로 중단되어", result.Plan);
        }
```

- [ ] **Step 2: 실패 확인**

실행: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~LastGenerationThrows"`

기대: 2건 FAIL — `Assert.NotNull(resultSpec)`에서 실패한다(현재는 null이 돌아온다). `RunPipelineAsync_FirstGenerationThrows_StillReturnsNull`은 지금도 통과한다 — 회귀 가드이므로 정상이다.

- [ ] **Step 3: 순차 루프 구현**

`:986-989`의 아래 블록을 찾는다.

```csharp
                    if (!genSuccess || string.IsNullOrEmpty(specificationMarkdown))
                    {
                        return Result(null, spDef, null, null);
                    }
```

아래로 교체한다.

```csharp
                    if (!genSuccess || string.IsNullOrEmpty(specificationMarkdown))
                    {
                        // 여기서 그냥 돌아가면 앞선 시도가 만든 검증된 문서까지 함께 사라진다.
                        // 이것이 이 파일에서 가장 큰 손실이었다 — 나쁜 문서가 아니라 무(無)가 나갔다.
                        var rescued = RetryRescue.TryRescue(
                            bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.GenerationFailed);
                        if (rescued == null)
                        {
                            return Result(null, spDef, null, null);
                        }

                        _userInteraction.NotifyError(
                            $"{selectedOption} - AI 생성이 중단되어 가장 높은 점수를 받은 " +
                            $"{rescued.AttemptNumber}차 시도({rescued.Review.NormalizedScore}/100)를 채택합니다.");

                        finalReview = rescued.Review;
                        verificationOutcome = VerificationOutcome.QualityRejected;
                        specificationMarkdown = rescued.Markdown;
                        break;
                    }
```

- [ ] **Step 4: 배치 루프 구현**

`:1701-1704`의 아래 블록을 찾는다. **들여쓰기가 한 단계 얕다.**

```csharp
                if (!genSuccess || string.IsNullOrEmpty(consolidatedPlan))
                {
                    return new ConsolidatedPipelineResult(null, null, null, planOutcome);
                }
```

아래로 교체한다.

```csharp
                if (!genSuccess || string.IsNullOrEmpty(consolidatedPlan))
                {
                    // 여기서 그냥 돌아가면 앞선 시도가 만든 검증된 계획서까지 함께 사라진다.
                    var rescued = RetryRescue.TryRescue(
                        bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.GenerationFailed);
                    if (rescued == null)
                    {
                        return new ConsolidatedPipelineResult(null, null, null, planOutcome);
                    }

                    _userInteraction.NotifyError(
                        $"{jobName} - AI 생성이 중단되어 가장 높은 점수를 받은 " +
                        $"{rescued.AttemptNumber}차 시도({rescued.Review.NormalizedScore}/100)를 채택합니다.");

                    planReview = rescued.Review;
                    planOutcome = VerificationOutcome.QualityRejected;
                    consolidatedPlan = rescued.Markdown;
                    break;
                }
```

- [ ] **Step 5: 통과 확인**

실행: `dotnet test tests/ReSet.Core.Tests`

기대: 전체 통과.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "fix: keep the best scored attempt when generation aborts the retry loop"
```

---

## Task 5: L1 소진 구제

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1007-1015` (순차), `:1719-1726` (배치)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 2의 `RetryRescue.TryRescue`, Task 1의 `RetryAbortReason.L1Exhausted`
- Produces: 없음

- [ ] **Step 1: 실패하는 테스트 작성**

Task 4에서 추가한 테스트들 뒤에 둘을 넣는다. L1 실패는 필수 H2 헤더가 없는 본문으로 유발한다 — 기존 `RunPipelineAsync_L1ValidationError_AttemptsSelfCorrection`이 쓰는 방식이다.

```csharp
        // 1차가 채점을 마쳤는데 2·3차가 L1에서 깨지면, 검증된 1차를 버리고
        // L1이 깨진 3차에 "통과 못 함" 경고를 붙여 내보냈다.
        [Fact]
        public async Task RunPipelineAsync_L1Exhausted_AdoptsTheEarlierScoredAttempt()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            const string body = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```\n\n";
            var goodSpec = body + "시도1고유표시";

            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = goodSpec }),
                    _ => Task.FromResult(new AiResult { Content = "헤더가 없는 잘못된 문서" }),
                    _ => Task.FromResult(new AiResult { Content = "헤더가 없는 잘못된 문서" }));

            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도1고유표시")))
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 9, ScoreCrud = 10, ScoreInterface = 9, ScoreReadability = 9, ScoreException = 7 }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.NotNull(resultSpec);
            Assert.Contains("시도1고유표시", resultSpec);
            Assert.Contains("3차 시도가 L1 기계 검증 실패로 중단되어", resultSpec);
            Assert.DoesNotContain("L1 기계 검증을 통과하지 못했습니다", resultSpec);
        }

        // 채점된 시도가 하나도 없으면 순위를 매길 수 없다. 현행 L1 소진 경로를 유지한다.
        [Fact]
        public async Task RunPipelineAsync_L1ExhaustedWithNoScoredAttempt_KeepsTheL1ExhaustedBanner()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "헤더가 없는 잘못된 문서" }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.NotNull(resultSpec);
            Assert.Contains("L1 기계 검증을 통과하지 못했습니다", resultSpec);
        }
```

- [ ] **Step 2: 실패 확인**

실행: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~L1Exhausted"`

기대: `RunPipelineAsync_L1Exhausted_AdoptsTheEarlierScoredAttempt`가 FAIL — 현재는 L1 소진 배너가 붙은 잘못된 문서가 돌아온다. 두 번째 테스트는 지금도 통과한다.

- [ ] **Step 3: 순차 루프 구현**

`:1007-1015`의 아래 블록을 찾는다.

```csharp
                        else
                        {
                            Log.Error("[파이프라인] L1 기계 검증 최종 실패 - SP: {SpName}", selectedOption);
                            _userInteraction.NotifyError($"{selectedOption} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                            verificationOutcome = VerificationOutcome.L1Exhausted;
                            specificationMarkdown = VerificationBanner.L1Exhausted(l1Result.Errors ?? new System.Collections.Generic.List<string>()) + specificationMarkdown;
                            break;
                        }
```

아래로 교체한다.

```csharp
                        else
                        {
                            Log.Error("[파이프라인] L1 기계 검증 최종 실패 - SP: {SpName}", selectedOption);

                            // 앞선 시도가 이미 L1을 통과하고 채점까지 받았다면, L1이 깨진
                            // 마지막 시도보다 그쪽이 낫다. 후보가 없을 때만 현행 경로로 간다.
                            var rescued = RetryRescue.TryRescue(
                                bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.L1Exhausted);
                            if (rescued != null)
                            {
                                _userInteraction.NotifyError(
                                    $"{selectedOption} - [[L1 기계 검증]] 최종 보완 실패. 가장 높은 점수를 받은 " +
                                    $"{rescued.AttemptNumber}차 시도({rescued.Review.NormalizedScore}/100)를 채택합니다.");

                                finalReview = rescued.Review;
                                verificationOutcome = VerificationOutcome.QualityRejected;
                                specificationMarkdown = rescued.Markdown;
                                break;
                            }

                            _userInteraction.NotifyError($"{selectedOption} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                            verificationOutcome = VerificationOutcome.L1Exhausted;
                            specificationMarkdown = VerificationBanner.L1Exhausted(l1Result.Errors ?? new System.Collections.Generic.List<string>()) + specificationMarkdown;
                            break;
                        }
```

- [ ] **Step 4: 배치 루프 구현**

`:1719-1726`의 아래 블록을 찾는다. **들여쓰기가 한 단계 얕고, `l1Result.Errors`에 null 병합이 없다.**

```csharp
                    else
                    {
                        _userInteraction.NotifyError($"{jobName} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                        planOutcome = VerificationOutcome.L1Exhausted;
                        consolidatedPlan = VerificationBanner.L1Exhausted(l1Result.Errors) + consolidatedPlan;
                        break;
                    }
```

아래로 교체한다.

```csharp
                    else
                    {
                        var rescued = RetryRescue.TryRescue(
                            bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.L1Exhausted);
                        if (rescued != null)
                        {
                            _userInteraction.NotifyError(
                                $"{jobName} - [[L1 기계 검증]] 최종 보완 실패. 가장 높은 점수를 받은 " +
                                $"{rescued.AttemptNumber}차 시도({rescued.Review.NormalizedScore}/100)를 채택합니다.");

                            planReview = rescued.Review;
                            planOutcome = VerificationOutcome.QualityRejected;
                            consolidatedPlan = rescued.Markdown;
                            break;
                        }

                        _userInteraction.NotifyError($"{jobName} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                        planOutcome = VerificationOutcome.L1Exhausted;
                        consolidatedPlan = VerificationBanner.L1Exhausted(l1Result.Errors) + consolidatedPlan;
                        break;
                    }
```

- [ ] **Step 5: 통과 확인**

실행: `dotnet test tests/ReSet.Core.Tests`

기대: 전체 통과.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "fix: keep the best scored attempt when L1 retries are exhausted"
```

---

## Task 6: L2 리뷰 실패 구제

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1126-1136` (순차), `:1795-1804` (배치)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 2의 `RetryRescue.TryRescue`, Task 1의 `RetryAbortReason.ReviewFailed`
- Produces: 없음

- [ ] **Step 1: 실패하는 테스트 작성**

Task 5에서 추가한 테스트들 뒤에 하나를 넣는다. 기존 `RunPipelineAsync_AllReviewsFail_KeepsReviewNotRunPath`가 후보 없는 짝을 이미 지키고 있으므로 그쪽은 새로 쓰지 않는다.

```csharp
        // 1차가 채점을 마쳤는데 2차 리뷰 호출이 죽으면, 검증된 1차를 버리고
        // 미검토 상태인 2차를 "리뷰 안 됨" 경고와 함께 내보냈다.
        [Fact]
        public async Task RunPipelineAsync_ReviewCallFails_AdoptsTheEarlierScoredAttempt()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            const string body = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```\n\n";
            var spec1 = body + "시도1고유표시";
            var spec2 = body + "시도2고유표시";

            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = spec1 }),
                    _ => Task.FromResult(new AiResult { Content = spec2 }));

            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도1고유표시")))
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 9, ScoreCrud = 10, ScoreInterface = 9, ScoreReadability = 9, ScoreException = 7 }));
            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도2고유표시")))
                .Returns<Task<ReviewResult>>(_ => throw new InvalidOperationException("critic down"));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.NotNull(resultSpec);
            Assert.Contains("시도1고유표시", resultSpec);
            Assert.Contains("2차 시도가 L2 리뷰 호출 실패로 중단되어", resultSpec);
            Assert.DoesNotContain("L2 AI 교차 리뷰가 수행되지 않았습니다", resultSpec);
        }
```

- [ ] **Step 2: 실패 확인**

실행: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~ReviewCallFails"`

기대: FAIL — 현재는 `시도2고유표시`에 "리뷰 안 됨" 배너가 붙어 돌아온다.

- [ ] **Step 3: 순차 루프 구현**

`:1126-1136`의 아래 블록을 찾는다.

```csharp
                    // 리뷰를 수행하지 못한 경우: 소프트 페일로 계속 진행하되 통과와 구분해 표시한다.
                    if (!reviewSuccess)
                    {
                        _userInteraction.NotifyError(
                            $"{selectedOption} - [[L2 AI 리뷰]] 를 수행하지 못해 교차 검증 없이 명세서를 확정합니다.");
                        verificationOutcome = VerificationOutcome.ReviewNotRun;
                        specificationMarkdown =
                            VerificationBanner.ReviewNotRun(reviewFailureReason ?? "사유가 기록되지 않았습니다.") + specificationMarkdown;
                        break;
                    }
```

아래로 교체한다.

```csharp
                    // 리뷰를 수행하지 못한 경우: 소프트 페일로 계속 진행하되 통과와 구분해 표시한다.
                    if (!reviewSuccess)
                    {
                        // 앞선 시도가 리뷰를 마쳤다면 미검토 문서보다 그쪽이 낫다.
                        var rescued = RetryRescue.TryRescue(
                            bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.ReviewFailed);
                        if (rescued != null)
                        {
                            _userInteraction.NotifyError(
                                $"{selectedOption} - [[L2 AI 리뷰]] 를 수행하지 못해 가장 높은 점수를 받은 " +
                                $"{rescued.AttemptNumber}차 시도({rescued.Review.NormalizedScore}/100)를 채택합니다.");

                            finalReview = rescued.Review;
                            verificationOutcome = VerificationOutcome.QualityRejected;
                            specificationMarkdown = rescued.Markdown;
                            break;
                        }

                        _userInteraction.NotifyError(
                            $"{selectedOption} - [[L2 AI 리뷰]] 를 수행하지 못해 교차 검증 없이 명세서를 확정합니다.");
                        verificationOutcome = VerificationOutcome.ReviewNotRun;
                        specificationMarkdown =
                            VerificationBanner.ReviewNotRun(reviewFailureReason ?? "사유가 기록되지 않았습니다.") + specificationMarkdown;
                        break;
                    }
```

- [ ] **Step 4: 배치 루프 구현**

`:1795-1804`의 아래 블록을 찾는다. **들여쓰기가 한 단계 얕다.**

```csharp
                // 리뷰를 수행하지 못한 경우: 소프트 페일로 계속 진행하되 통과와 구분해 표시한다.
                if (!reviewSuccess)
                {
                    _userInteraction.NotifyError(
                        $"{jobName} - [[L2 AI 리뷰]] 를 수행하지 못해 교차 검증 없이 계획서를 확정합니다.");
                    planOutcome = VerificationOutcome.ReviewNotRun;
                    consolidatedPlan =
                        VerificationBanner.ReviewNotRun(reviewFailureReason ?? "사유가 기록되지 않았습니다.") + consolidatedPlan;
                    break;
                }
```

아래로 교체한다.

```csharp
                // 리뷰를 수행하지 못한 경우: 소프트 페일로 계속 진행하되 통과와 구분해 표시한다.
                if (!reviewSuccess)
                {
                    var rescued = RetryRescue.TryRescue(
                        bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.ReviewFailed);
                    if (rescued != null)
                    {
                        _userInteraction.NotifyError(
                            $"{jobName} - [[L2 AI 리뷰]] 를 수행하지 못해 가장 높은 점수를 받은 " +
                            $"{rescued.AttemptNumber}차 시도({rescued.Review.NormalizedScore}/100)를 채택합니다.");

                        planReview = rescued.Review;
                        planOutcome = VerificationOutcome.QualityRejected;
                        consolidatedPlan = rescued.Markdown;
                        break;
                    }

                    _userInteraction.NotifyError(
                        $"{jobName} - [[L2 AI 리뷰]] 를 수행하지 못해 교차 검증 없이 계획서를 확정합니다.");
                    planOutcome = VerificationOutcome.ReviewNotRun;
                    consolidatedPlan =
                        VerificationBanner.ReviewNotRun(reviewFailureReason ?? "사유가 기록되지 않았습니다.") + consolidatedPlan;
                    break;
                }
```

- [ ] **Step 5: 통과 확인**

실행: `dotnet test tests/ReSet.Core.Tests`

기대: 전체 통과. 특히 `RunPipelineAsync_AllReviewsFail_KeepsReviewNotRunPath`가 그대로 통과해야 한다 — 후보 없는 경로가 바뀌지 않았다는 증거다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "fix: keep the best scored attempt when the critic call fails"
```

---

## Task 7: L1 실패 회차의 피드백 합성

**Files:**
- Modify: `src/ReSet.Core/Services/CriticFeedbackLog.cs`, `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1003` (순차), `:1716` (배치)
- Test: `tests/ReSet.Core.Tests/CriticFeedbackLogTests.cs`

**Interfaces:**
- Consumes: 기존 `CriticFeedbackLog.Compose(IReadOnlyList<string>, string)`
- Produces: `CriticFeedbackLog.ComposeAfterL1Failure(string? l1Fix, IReadOnlyList<string> history)` → `string`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/CriticFeedbackLogTests.cs`의 클래스 안에 둘을 넣는다.

```csharp
        // 아직 L2 라운드가 없으면 붙일 누적이 없다. 가장 흔한 경우의 프롬프트가
        // 오늘과 달라지면 안 되므로 L1 지시를 그대로 돌려준다.
        [Fact]
        public void ComposeAfterL1Failure_WithEmptyHistory_ReturnsTheL1FixVerbatim()
        {
            var composed = CriticFeedbackLog.ComposeAfterL1Failure("표 축약어를 제거하십시오.", new List<string>());

            Assert.Equal("표 축약어를 제거하십시오.", composed);
        }

        // Actor는 매번 백지에서 다시 쓴다. L1 지시만 보내면 그 회차는 내용 교정 이력이
        // 전부 빠진 채 생성된다. 이전 구현이 실제로 그랬다.
        [Fact]
        public void ComposeAfterL1Failure_KeepsTheAccumulatedCriticFeedbackBehindTheL1Fix()
        {
            var history = new List<string>();
            CriticFeedbackLog.Record(history, 1, Review("조인 서술을 고치십시오."), 8);
            CriticFeedbackLog.Record(history, 2, Review("NOLOCK 영향을 보완하십시오."), 8);

            var composed = CriticFeedbackLog.ComposeAfterL1Failure("표 축약어를 제거하십시오.", history);

            Assert.StartsWith("[L1 기계 검증 오류", composed);
            Assert.Contains("표 축약어를 제거하십시오.", composed);
            Assert.Contains("[L2 AI 리뷰 누적 피드백 (최근 2개 라운드)]", composed);
            Assert.Contains("조인 서술을 고치십시오.", composed);
            Assert.Contains("NOLOCK 영향을 보완하십시오.", composed);
            Assert.Contains("위 형식 오류를 먼저 해소하고", composed);
        }
```

- [ ] **Step 2: 실패 확인**

실행: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~ComposeAfterL1Failure"`

기대: 컴파일 실패 — `ComposeAfterL1Failure`가 존재하지 않는다.

- [ ] **Step 3: 최소 구현**

`src/ReSet.Core/Services/CriticFeedbackLog.cs`의 `Compose` 바로 아래에 넣는다.

```csharp
        /// <summary>
        /// L1 실패 회차의 프롬프트를 조립한다. 이번 회차에 반드시 해소해야 할 형식 오류를
        /// 앞에 두고, 지금까지 누적된 Critic 지적을 뒤에 붙인다.
        ///
        /// 이전에는 호출부가 L1 수정 지시로 feedbackLog를 통째로 덮어썼다. Actor는 매번
        /// 백지에서 다시 쓰므로 그 회차는 내용 교정 이력이 전부 빠진 채 생성됐다.
        /// history 자체는 살아남아 다음 L2 실패 때 되살아나므로 영구 손실은 아니었지만,
        /// 한 회차가 비어서 나가는 것만으로도 품질이 무너진다.
        ///
        /// 아직 L2 라운드가 없으면 l1Fix를 그대로 돌려준다 — 가장 흔한 경우의 프롬프트가
        /// 달라지지 않아야 한다.
        /// </summary>
        public static string ComposeAfterL1Failure(string? l1Fix, IReadOnlyList<string> history)
        {
            var fix = l1Fix ?? string.Empty;

            if (history == null || history.Count == 0)
            {
                return fix;
            }

            return $"[L1 기계 검증 오류 — 이번 회차에 반드시 해소]\n{fix}\n\n" +
                Compose(history,
                    "※ 지시사항: 위 형식 오류를 먼저 해소하고, 누적 피드백에서 이미 반영한 " +
                    "내용 교정의 서술 수준을 낮추지 마십시오. 원본 DDL을 절대적 기준으로 삼으십시오.");
        }
```

- [ ] **Step 4: 순차·배치 호출부 배선**

`VerificationPipelineOrchestrator.cs:1003`(순차)의 한 줄을 바꾼다.

```csharp
                            feedbackLog = l1Result.SuggestedPromptFix;
```

→

```csharp
                            feedbackLog = CriticFeedbackLog.ComposeAfterL1Failure(l1Result.SuggestedPromptFix, feedbackHistory);
```

`:1716`(배치)에도 같은 교체를 한다. **들여쓰기가 한 단계 얕다.** 두 줄은 문자열이 같으므로 일괄 치환하지 말고 한 줄씩 확인하며 바꾼다.

- [ ] **Step 5: 통과 확인**

실행: `dotnet test tests/ReSet.Core.Tests`

기대: 전체 통과.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/CriticFeedbackLog.cs src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/CriticFeedbackLogTests.cs
git commit -m "fix: keep accumulated critic feedback when L1 fails a round"
```

---

## Task 8: 문서 동기화

**Files:**
- Modify: `src/ReSet.Cli/appsettings.json`, `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:74`, `AGENTS.md`

**Interfaces:**
- Consumes: Task 1~7의 결과
- Produces: 없음

- [ ] **Step 1: 설정 주석 정정**

`src/ReSet.Cli/appsettings.json`의 `MaxL2Attempts` 줄 주석을 아래로 바꾼다. **JSONC이므로 주석이 허용된다.**

```
    "MaxL2Attempts": 2,                // 총 시도 예산(1차 + 재시도). 이름과 달리 L2 전용이 아니라 L1 실패와 공유하므로, L1에서 소진되면 채점된 후보 수가 이 값보다 적어질 수 있습니다. 그 경우에도 최고점 후보는 RetryRescue가 구제합니다. (1 이상의 정수 또는 "unlimited")
```

- [ ] **Step 2: 산식 옆 주석 추가**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:74`의 아래 줄을 찾는다.

```csharp
            _maxAttempts = _maxL2Attempts == -1 ? -1 : 1 + _maxL2Attempts;
```

바로 위에 주석을 넣는다.

```csharp
            // 이 예산은 L1 실패와 L2 실패가 공유한다. 설정 이름(MaxL2Attempts)과 달리
            // L2 전용이 아니다 — L1에서 소진되면 채점된 후보 수가 설정값보다 적어진다.
            // 2026-08-05 실행에서 3회 예산 중 1회를 L1 실패가 가져가 채점된 시도가 2회뿐이었다.
            // 예산을 나누지 않기로 한 이유는 RetryRescue가 최고점 후보를 구제하므로
            // 남는 손해가 "좋은 문서 상실"이 아니라 "개선 기회 1회 상실"이기 때문이다.
            _maxAttempts = _maxL2Attempts == -1 ? -1 : 1 + _maxL2Attempts;
```

- [ ] **Step 3: AGENTS.md에 RetryRescue 추가**

`AGENTS.md:44`의 `BestAttempt.cs` 항목 **바로 뒤**에 한 줄을 넣는다.

```markdown
    *   [RetryRescue.cs](./src/ReSet.Core/Services/RetryRescue.cs): 재시도 루프가 비정상 중단됐을 때 [BestAttempt](./src/ReSet.Core/Services/BestAttempt.cs)가 보관한 최선본을 채택할지 결정하는 정적 클래스. 채택 규칙(후보 없으면 현행 폴백, 있으면 배너를 붙여 `QualityRejected`로 확정)을 단독 소유합니다. 호출 자리가 여덟 곳(순차 SP 루프 넷, 배치 계획 루프 넷)이라 각자 조립하면 반드시 어긋납니다. 특히 **생성 호출 실패 시 그냥 반환하면 앞선 시도가 만든 검증된 문서까지 함께 사라집니다** — 이 경로를 지우지 마십시오. 중단 사유 문구는 [VerificationBanner](./src/ReSet.Core/Services/VerificationBanner.cs)가 소유합니다.
```

- [ ] **Step 4: 오케스트레이터 항목 갱신**

`AGENTS.md:39`의 `VerificationPipelineOrchestrator.cs` 설명 끝에 아래 문장을 덧붙인다.

```markdown
재시도가 생성 실패·L1 소진·L2 리뷰 실패로 중단될 때도 [RetryRescue](./src/ReSet.Core/Services/RetryRescue.cs)를 통해 최선본을 채택합니다. `MaxL2Attempts`는 이름과 달리 L1 실패와 공유하는 총 시도 예산입니다.
```

- [ ] **Step 5: 스테일 문구 정정**

`AGENTS.md:161`은 아직 옛 동작을 서술한다. 아래 문장을 찾는다.

```markdown
**컨텍스트 윈도우 오염 방지를 위해 누적된 이전 피드백을 지우고 최신 피드백만을 Stateful Checklist 포맷으로 단일 압축 주입**하여 회귀 결함(Regression)을 예방하십시오.
```

아래로 교체한다. 누적으로 바꾼 것이 2026-08-04이며, 지우던 시절이 바로 회귀 결함의 원인이었다.

```markdown
**최근 3개 라운드의 Critic 피드백을 항목별 점수와 함께 누적 주입**하여 회귀 결함(Regression)을 예방하십시오. Actor는 이전 명세서를 받지 않고 매번 백지에서 다시 쓰므로, 누적을 끊고 최신 피드백만 넣으면 앞 라운드에서 정리된 오류가 되살아납니다. 조립은 [CriticFeedbackLog](./src/ReSet.Core/Services/CriticFeedbackLog.cs)가 소유하며, L1 실패 회차에는 `ComposeAfterL1Failure`가 L1 수정 지시와 누적 피드백을 함께 보냅니다.
```

- [ ] **Step 6: 최종 검증**

```bash
dotnet build ReSet.slnx
dotnet test tests/ReSet.Core.Tests
```

기대: 빌드 성공, 전체 통과.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Cli/appsettings.json src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs AGENTS.md
git commit -m "docs: record the rescue contract and the shared retry budget"
```

---

## Self-Review

**스펙 커버리지**

| 스펙 항목 | 구현 태스크 |
|---|---|
| 배너 채택 경위 줄 (사유 3종) | Task 1 |
| `RetryRescue` 단일 소유 | Task 2 |
| 정상 소진 두 자리 통일 | Task 3 |
| 생성 실패 구제 (순차 + 배치) | Task 4 |
| L1 소진 구제 (순차 + 배치) | Task 5 |
| L2 예외 구제 (순차 + 배치) | Task 6 |
| 피드백 합성 (`ComposeAfterL1Failure`) | Task 7 |
| 예산 이름·문서 정정 | Task 8 Step 1~2 |
| 경계: 후보 없음 (생성 실패) | Task 4 Step 1의 `FirstGenerationThrows_StillReturnsNull` |
| 경계: 후보 없음 (L1 소진) | Task 5 Step 1의 `L1ExhaustedWithNoScoredAttempt_KeepsTheL1ExhaustedBanner` |
| 경계: 후보 없음 (L2 예외) | 기존 `RunPipelineAsync_AllReviewsFail_KeepsReviewNotRunPath` (Task 6 Step 5에서 확인) |
| 경계: 사용자 취소 | 코드 변경 없음. `OperationCanceledException`을 잡지 않으므로 자동 성립하며 `CancellationPolicyTests`가 지킨다 |
| 콘솔 알림 계약 | Task 4·5·6의 각 구현 단계에 `NotifyError` 문구 포함 |
| 문서 동기화 | Task 8 |

**스펙보다 강화한 곳**

스펙의 테스트 표는 배치 루프 테스트를 생성 실패 1건만 요구했다. 계획은 배치 L1 소진과 L2 예외의 구현을 포함하되 테스트는 순차 쪽만 둔다 — 배치 쌍둥이는 Task 3의 기존 배치 테스트와 Task 4의 배치 생성 실패 테스트가 `RetryRescue` 배선을 검증하고, L1·L2 분기는 순차와 동일한 코드 형태다. 다만 **이것이 바로 원래 사고의 원인이 된 추론**이므로, 리뷰어가 부족하다고 판단하면 Task 5·6에 배치 테스트를 추가하는 것이 옳다.

**타입 일관성**

`RetryAbortReason`(enum, Task 1) / `RescueContext(Reason, AbortedAttempt, AdoptedAttempt)`(Task 1) / `RescuedAttempt(Markdown, Review, AttemptNumber)`(Task 2) / `RetryRescue.TryRescue(BestAttempt, int, int, RetryAbortReason?)`(Task 2) — Task 3~6이 모두 같은 이름과 인자 순서로 호출한다. `VerificationBanner.QualityRejected(ReviewResult, int, RescueContext?)`는 Task 1에서 정의하고 Task 2·3이 쓴다. `CriticFeedbackLog.ComposeAfterL1Failure(string?, IReadOnlyList<string>)`는 Task 7에서 정의하고 같은 태스크에서 쓴다. 불일치 없음.

**주의점 (구현자에게)**

- `RetryRescue.TryRescue`가 돌려주는 `Markdown`에는 배너가 **이미 붙어 있다.** 호출부에서 `VerificationBanner.QualityRejected(...) + rescued.Markdown`처럼 다시 붙이면 배너가 두 번 나온다. 테스트가 `Assert.Contains`만 하므로 이 실수는 초록으로 통과할 수 있다.
- 순차 루프와 배치 루프는 들여쓰기 깊이가 다르다. Edit이 유일 일치를 요구하므로 잘못된 들여쓰기는 실패로 드러나지만, 복사한 코드를 손으로 맞출 때 주의한다.
- Task 7의 두 호출부는 문자열이 완전히 같다. `replace_all`을 쓰면 둘 다 바뀌지만 들여쓰기가 달라 실패한다. 한 줄씩 바꾼다.
