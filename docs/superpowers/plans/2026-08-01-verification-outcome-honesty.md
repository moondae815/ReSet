# 검증 종료 상태 표기 일관화 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 검증 파이프라인의 네 가지 종료 상태가 세 종료 영역 모두에서 문서와 화면에 사실대로 표기되게 하고, 지시서의 명세서 링크가 실제 산출물 위치를 가리키게 한다.

**Architecture:** 종료 사유를 `VerificationOutcome` 열거형으로 표현하고, 문서 배너 렌더링을 `VerificationBanner` 한 곳으로 모은다. 세 종료 영역(구역별 순차 생성, 표준 재시도 루프, 통합 배치)이 같은 렌더러를 쓰고, `SpecificationDocumentFormatter`와 L3 승인 화면이 그 상태를 받아 표시한다. 지시서 링크는 `OutputPathResolver`로 계산하고 파일 존재를 확인한 뒤 찍는다.

**Tech Stack:** .NET 10, C#, xUnit, NSubstitute, Spectre.Console

**설계 문서:** `docs/superpowers/specs/2026-08-01-verification-outcome-honesty-design.md`

## Global Constraints

- 모든 작업은 TDD로 한다. 테스트를 먼저 쓰고 **실패를 눈으로 확인한 뒤** 구현한다.
- `dotnet build` 오류 0. 클린 빌드는 현재 `tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`에서 CS8600/CS8602 경고 8건을 낸다. 이 브랜치 소관이 아니며 **고치지 말고, 늘리지도 말 것.**
- `dotnet test` 전량 통과. 시작 시점 기준선은 329개다.
- 커밋은 태스크 단위로 한다.
- Korean 주석·문서 스타일, UTF-8.
- `OperationCanceledException`은 어떤 catch에서도 삼키지 않는다.
- **`QualityRejected` 배너 문구는 한 글자도 바꾸지 않는다.** 세 곳에 복제된 문자열을 하나로 합치는 것이 이번 변경의 실질이고, 문구가 달라지면 기존 산출물과 어긋난다.
- 점수 필드 출력 여부는 `review != null`이 아니라 **Outcome**이 결정한다. `L1Exhausted`·`ReviewNotRun`에서는 `review`가 non-null이어도 점수를 내보내지 않는다.

## File Structure

| 파일 | 책임 | 태스크 |
|---|---|---|
| `src/ReSet.Core/Models/VerificationOutcome.cs` | 종료 사유 열거형 (신규) | 1 |
| `src/ReSet.Core/Models/CodeObjectAnalysisModels.cs` | `Outcome` 필드 추가 | 1 |
| `src/ReSet.Core/Services/VerificationBanner.cs` | 문서 배너 렌더링 (신규) | 1 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | 세 종료 영역의 상태 확정·배너 호출 | 2·3·4 |
| `src/ReSet.Core/Services/SpecificationDocumentFormatter.cs` | Outcome 기반 헤더 | 5 |
| `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs` | 포매터 호출부 (재귀 객체) | 5 |
| `src/ReSet.Cli/Program.cs` | 포매터 호출부 (루트 SP), 지시서 호출부 | 5·8 |
| `src/ReSet.Cli/SpecHeaderReader.cs` | YAML 헤더 파싱 (신규 추출) | 6 |
| `src/ReSet.Cli/ConsoleUserInteraction.cs` | L3 화면 표시 | 6·7 |
| `src/ReSet.Core/Services/MetadataExporter.cs` | 지시서 링크 계산 | 8 |
| `AGENTS.md` | 테스트 개수 | 9 |

---

## Task 1: VerificationOutcome과 VerificationBanner

**Files:**
- Create: `src/ReSet.Core/Models/VerificationOutcome.cs`, `src/ReSet.Core/Services/VerificationBanner.cs`
- Modify: `src/ReSet.Core/Models/CodeObjectAnalysisModels.cs`
- Test: `tests/ReSet.Core.Tests/VerificationBannerTests.cs` (신규)

**Interfaces:**
- Consumes: 없음
- Produces:
  ```csharp
  namespace ReSet.Core.Models;
  public enum VerificationOutcome { Passed, L1Exhausted, QualityRejected, ReviewNotRun }

  // CodeObjectPipelineResult, CodeObjectAnalysisResult 양쪽에 추가:
  public VerificationOutcome Outcome { get; set; } = VerificationOutcome.Passed;

  namespace ReSet.Core.Services;
  public static class VerificationBanner
  {
      public static string L1Exhausted(IReadOnlyList<string> errors);
      public static string QualityRejected(ReviewResult review, int scoreThreshold);
      public static string ReviewNotRun(string reason);
  }
  ```
  태스크 2·3·4가 렌더러를, 태스크 5가 `Outcome` 필드를 소비한다.

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/VerificationBannerTests.cs`를 새로 만든다.

```csharp
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
```

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~VerificationBannerTests"
```

기대 결과: 컴파일 에러 — `VerificationBanner` 형식을 찾을 수 없음.

- [ ] **Step 3: 열거형을 만든다**

`src/ReSet.Core/Models/VerificationOutcome.cs`:

```csharp
namespace ReSet.Core.Models;

/// <summary>
/// 검증 파이프라인이 어떤 상태로 끝났는지를 나타낸다.
/// 네 값이 곧 루프의 네 종료 지점이며, 문서 헤더와 배너 표기의 기준이 된다.
/// </summary>
public enum VerificationOutcome
{
    /// <summary>L1 통과 + L2 결함 없음.</summary>
    Passed,

    /// <summary>L1 기계 검증 재시도를 모두 소진했다.</summary>
    L1Exhausted,

    /// <summary>L2 리뷰는 수행됐으나 점수 미달·결함이 남았다.</summary>
    QualityRejected,

    /// <summary>L2 리뷰 호출이 예외로 실패해 검증되지 않았다.</summary>
    ReviewNotRun
}
```

- [ ] **Step 4: 배너 렌더러를 만든다**

`src/ReSet.Core/Services/VerificationBanner.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services;

/// <summary>
/// 검증 종료 상태를 문서 본문 앞에 붙일 배너로 렌더링한다.
/// 통과 상태에는 붙일 배너가 없으므로 해당 메서드를 두지 않는다.
/// </summary>
public static class VerificationBanner
{
    public static string L1Exhausted(IReadOnlyList<string> errors)
    {
        var errorLines = errors is { Count: > 0 }
            ? string.Join("\n", errors.Select(error => $">   - {error}"))
            : ">   - (상세 오류가 기록되지 않았습니다.)";

        return "\n> [!CAUTION]\n> **[검증 미완료] L1 기계 검증을 통과하지 못했습니다.**"
            + " 재시도를 모두 소진하여 마지막 작성 버전을 그대로 사용합니다.\n"
            + "> - **잔존 오류**:\n"
            + errorLines
            + "\n\n";
    }

    public static string QualityRejected(ReviewResult review, int scoreThreshold) =>
        $"\n> [!CAUTION]\n> **[품질 불합격] 정합성/가독성 기준 미달 (최종 신뢰도 점수: {review.NormalizedScore}/100)**\n> - **평가 점수**: 정합성 {review.ScoreAccuracy}/10, CRUD {review.ScoreCrud}/10, 인터페이스 {review.ScoreInterface}/10, 가독성 {review.ScoreReadability}/10, 예외 {review.ScoreException}/10 (기준 점수: {scoreThreshold}/10)\n> - **최종 Critic 결함 피드백**:\n>   {review.FeedbackComment?.Replace("\n", "\n>   ")}\n\n";

    public static string ReviewNotRun(string reason) =>
        "> [!NOTE]\n> **L2 AI 교차 리뷰가 수행되지 않았습니다.** 리뷰 호출이 실패하여 정합성 검증 없이 확정된 문서입니다. 내용을 직접 검토하십시오.\n"
        + $"> - **실패 사유**: {reason}\n\n";
}
```

`QualityRejected`의 문자열은 `VerificationPipelineOrchestrator.cs`의 `:723`·`:1050`·`:1615`에 있던 것과 동일하다. 변수명만 `review`로 바뀌었다.

- [ ] **Step 5: 모델에 Outcome 필드를 추가한다**

`src/ReSet.Core/Models/CodeObjectAnalysisModels.cs`에서 두 클래스를 찾아 각각 필드를 더한다.

찾을 문자열 (1/2):
```csharp
    public ReviewResult? Review { get; set; }
    public string? ThinkingText { get; set; }
    public List<AnalysisNode> Nodes { get; set; } = new();
```

바꿀 문자열:
```csharp
    public ReviewResult? Review { get; set; }
    public VerificationOutcome Outcome { get; set; } = VerificationOutcome.Passed;
    public string? ThinkingText { get; set; }
    public List<AnalysisNode> Nodes { get; set; } = new();
```

찾을 문자열 (2/2):
```csharp
    public string? SpecMarkdown { get; set; }
    public ReviewResult? Review { get; set; }
    public string? ThinkingText { get; set; }
    public string? SpecPath { get; set; }
```

바꿀 문자열:
```csharp
    public string? SpecMarkdown { get; set; }
    public ReviewResult? Review { get; set; }
    public VerificationOutcome Outcome { get; set; } = VerificationOutcome.Passed;
    public string? ThinkingText { get; set; }
    public string? SpecPath { get; set; }
```

- [ ] **Step 6: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~VerificationBannerTests"
```

기대 결과: 4건 PASS.

- [ ] **Step 7: 전체 테스트와 빌드를 확인하고 커밋한다**

```bash
dotnet build 2>&1 | tail -3
dotnet test 2>&1 | tail -2
git add src/ReSet.Core/Models/VerificationOutcome.cs src/ReSet.Core/Services/VerificationBanner.cs src/ReSet.Core/Models/CodeObjectAnalysisModels.cs tests/ReSet.Core.Tests/VerificationBannerTests.cs
git commit -m "feat(verification): model terminal outcomes and share the banner renderer

The pipeline ends four ways but only the quality-failure case annotated the
document, and its banner was copy-pasted across three call sites. Name the
four outcomes and give the banners one home so a new terminal state cannot
quietly ship without its annotation.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: 표준 재시도 루프의 종료 상태 (A·②)

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:955~1070`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: `VerificationOutcome`, `VerificationBanner` (태스크 1)
- Produces: 표준 루프가 `CodeObjectPipelineResult.Outcome`을 채운다. 태스크 5가 소비한다.

이 영역은 `RunCodeObjectPipelineCoreAsync`의 `else` 분기(구역별 경로가 아닌 표준 경로)다. 현재 세 종료 지점이 있다.

| 위치 | 현재 | 바뀔 모습 |
|---|---|---|
| `:960-962` L1 소진 | 콘솔 알림 + `break` | + `L1Exhausted` 배너, Outcome 확정 |
| `:1044-1052` L2 품질 미달 | 인라인 배너 | 공유 렌더러, Outcome 확정 |
| `:1057-1070` 통과 | `NotifyValidationSuccess` | 리뷰 미수행이면 여기 오지 않음 |

- [ ] **Step 1: 실패하는 테스트 2개를 작성한다**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` 클래스 닫는 중괄호 앞에 추가한다.

```csharp
        [Fact]
        public async Task RunCodeObjectPipelineAsync_MarksSpecWhenCriticReviewCouldNotRun()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_NoReview", CodeObjectType.Procedure);

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_NoReview", DdlText = "SELECT 1;"
                }));
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = ValidSpecificationMarkdown() }));
            criticService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ReviewResult>(new InvalidOperationException("critic endpoint down")));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-test", criticService: criticService);

            var result = await orchestrator.RunCodeObjectPipelineAsync(
                "Server=(local);Database=PaymentDB", key, 1, "OpenAI", "rules", true,
                Path.Combine(Path.GetTempPath(), $"ReSet-Outcome-{Guid.NewGuid():N}"), false);

            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
            Assert.Contains("L2 AI 교차 리뷰가 수행되지 않았습니다", result.SpecMarkdown);
            userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_MarksSpecWhenL1RetriesAreExhausted()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_BadL1", CodeObjectType.Procedure);

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_BadL1", DdlText = "SELECT 1;"
                }));
            // 필수 H2 헤더가 없어 L1이 항상 실패한다.
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "# 헤더가 없는 본문" }));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-test", criticService: criticService);

            var result = await orchestrator.RunCodeObjectPipelineAsync(
                "Server=(local);Database=PaymentDB", key, 1, "OpenAI", "rules", true,
                Path.Combine(Path.GetTempPath(), $"ReSet-Outcome-{Guid.NewGuid():N}"), false);

            Assert.Equal(VerificationOutcome.L1Exhausted, result.Outcome);
            Assert.Contains("L1 기계 검증을 통과하지 못했습니다", result.SpecMarkdown);
            userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
        }

        private static string ValidSpecificationMarkdown() =>
            string.Join("\n", RequiredSpecHeaders().Select(header => header + "\n\n내용"));

        private static IEnumerable<string> RequiredSpecHeaders()
        {
            var headersField = typeof(MechanicalValidator).GetField(
                "RequiredHeaders",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return ((string[])headersField!.GetValue(new MechanicalValidator())!).Select(h => "## " + h);
        }
```

`RunCodeObjectPipelineAsync`의 정확한 인자 순서는 `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:105`를 읽어 맞춘다. `MechanicalValidator`의 필수 헤더 상수명도 그 파일에서 확인해 `RequiredSpecHeaders`를 조정한다. 두 헬퍼는 리플렉션 대신 상수를 직접 복사해도 무방하다 — L1을 통과하는 마크다운과 통과하지 못하는 마크다운을 만드는 것이 목적이다.

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~RunCodeObjectPipelineAsync_MarksSpec"
```

기대 결과: 2건 FAIL. `MarksSpecWhenCriticReviewCouldNotRun`은 `Outcome`이 `Passed`로 남고 `NotifyValidationSuccess`가 호출되어 실패한다.

- [ ] **Step 3: 종료 상태를 담을 지역 변수를 추가한다**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:162`을 찾아 바꾼다.

찾을 문자열:
```csharp
            ReviewResult? finalReview = null;
```

바꿀 문자열:
```csharp
            ReviewResult? finalReview = null;
            var verificationOutcome = VerificationOutcome.Passed;
```

- [ ] **Step 4: L1 소진 분기에 배너와 상태를 넣는다 (②)**

`:960-962`을 찾아 바꾼다.

찾을 문자열:
```csharp
                            Log.Error("[파이프라인] L1 기계 검증 최종 실패 - SP: {SpName}", selectedOption);
                            _userInteraction.NotifyError($"{selectedOption} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                            break;
```

바꿀 문자열:
```csharp
                            Log.Error("[파이프라인] L1 기계 검증 최종 실패 - SP: {SpName}", selectedOption);
                            _userInteraction.NotifyError($"{selectedOption} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                            verificationOutcome = VerificationOutcome.L1Exhausted;
                            specificationMarkdown = VerificationBanner.L1Exhausted(l1Result.Errors) + specificationMarkdown;
                            break;
```

- [ ] **Step 5: L2 품질 미달 분기를 공유 렌더러로 바꾼다**

`:1048-1051`을 찾아 바꾼다.

찾을 문자열:
```csharp
                            // 최종 품질 불합격 경고 배너 삽입
                            finalReview = l2Result;
                            var warningBanner = $"\n> [!CAUTION]\n> **[품질 불합격] 정합성/가독성 기준 미달 (최종 신뢰도 점수: {l2Result.NormalizedScore}/100)**\n> - **평가 점수**: 정합성 {l2Result.ScoreAccuracy}/10, CRUD {l2Result.ScoreCrud}/10, 인터페이스 {l2Result.ScoreInterface}/10, 가독성 {l2Result.ScoreReadability}/10, 예외 {l2Result.ScoreException}/10 (기준 점수: {_criticScoreThreshold}/10)\n> - **최종 Critic 결함 피드백**:\n>   {l2Result.FeedbackComment?.Replace("\n", "\n>   ")}\n\n";
                            specificationMarkdown = warningBanner + specificationMarkdown;
```

바꿀 문자열:
```csharp
                            // 최종 품질 불합격 경고 배너 삽입
                            finalReview = l2Result;
                            verificationOutcome = VerificationOutcome.QualityRejected;
                            specificationMarkdown =
                                VerificationBanner.QualityRejected(l2Result, _criticScoreThreshold) + specificationMarkdown;
```

- [ ] **Step 6: 리뷰 미수행을 통과와 분리한다 (A)**

`:1057`의 성공 분기 바로 앞에 새 분기를 넣는다. 반드시 "결함 발견" 분기(`:1027`) **뒤**에 와야 한다. 앞에 두면 결함 재시도 경로를 삼킨다.

찾을 문자열:
```csharp
                    if (l1Result.IsValid && (l2Result == null || !l2Result.HasDefects))
                    {
                        Log.Information("[파이프라인] L1+L2 검증 최종 통과 - SP: {SpName}, 최종 시도 횟수: {Attempt}", selectedOption, attempt);
```

바꿀 문자열:
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

                    if (l1Result.IsValid && (l2Result == null || !l2Result.HasDefects))
                    {
                        Log.Information("[파이프라인] L1+L2 검증 최종 통과 - SP: {SpName}, 최종 시도 횟수: {Attempt}", selectedOption, attempt);
```

- [ ] **Step 7: 실패 사유를 기록한다**

`:972` 부근의 리뷰 상태 변수 선언과 catch를 찾아 사유를 담는다.

찾을 문자열:
```csharp
                    bool reviewSuccess = false;
```

바꿀 문자열:
```csharp
                    bool reviewSuccess = false;
                    string? reviewFailureReason = null;
```

그리고 같은 영역의 리뷰 catch 블록을 찾아 사유를 채운다. `catch` 안에서 `NotifyError`를 호출하는 줄 바로 앞에 `reviewFailureReason = ex.Message;`를 추가한다. 정확한 위치는 `_criticService.ReviewSpecificationAsync` 호출을 감싼 try/catch(약 `:977-1005`)에서 찾는다.

- [ ] **Step 8: 결과에 Outcome을 싣는다**

`RunCodeObjectPipelineCoreAsync`의 반환 튜플은 Outcome을 나르지 않는다. `RunCodeObjectPipelineAsync`(`:105`)가 `CodeObjectPipelineResult`를 조립하는 지점을 찾아 `Outcome`을 채운다. 코어 메서드의 반환 튜플에 `VerificationOutcome Outcome`을 더하고, 두 반환 지점 모두에서 `verificationOutcome`을 넘긴다. 컴파일러가 누락된 반환 지점을 알려준다.

- [ ] **Step 9: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"
```

기대 결과: 신규 2건 PASS, 기존 전부 PASS.

- [ ] **Step 10: 커밋한다**

```bash
dotnet build 2>&1 | tail -3
dotnet test 2>&1 | tail -2
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "fix(pipeline): stop reporting an unrun review as a passing one

A critic exception left l2Result null, which the success branch read as no
defects, so the standard retry loop announced validation success for a spec
nobody cross-checked -- on the most-used menu and on every recursively
analyzed object. Separate the case, and give L1 exhaustion the document
banner the other terminal states already had.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: 구역별 순차 생성 경로의 종료 상태 (A)

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:636~737`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: `VerificationOutcome`, `VerificationBanner` (태스크 1), `verificationOutcome` 지역 변수 (태스크 2 Step 3)
- Produces: 없음

이 경로는 후보 생성 → Critic 채점 → 합성 → 최종 L2 검토로 진행하며, L1 재시도 소진에 해당하는 종료가 없다. 세 상태만 쓴다.

| 조건 | Outcome |
|---|---|
| `finalReview != null && !HasDefects` | `Passed` |
| `finalReview != null && HasDefects` | `QualityRejected` |
| `finalReview == null` | `ReviewNotRun` |

- [ ] **Step 1: 실패하는 테스트를 작성한다**

구역별 경로는 로컬 공급자 조건에서만 진입한다. 진입 조건은 `RunCodeObjectPipelineCoreAsync`의 해당 `if`를 읽어 확인하고, 테스트의 `IAiService` 대역이 그 조건을 만족하도록 `ProviderName`을 설정한다.

```csharp
        [Fact]
        public async Task RunCodeObjectPipelineAsync_SectionalPath_MarksSpecWhenFinalReviewCouldNotRun()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Sectional", CodeObjectType.Procedure);

            // 구역별 경로 진입 조건을 만족시킨다.
            aiService.ProviderName.Returns("Ollama");
            criticService.ProviderName.Returns("Ollama");

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_Sectional", DdlText = "SELECT 1;"
                }));
            criticService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ReviewResult>(new InvalidOperationException("critic endpoint down")));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "ollama-test", criticService: criticService);

            var result = await orchestrator.RunCodeObjectPipelineAsync(
                "Server=(local);Database=PaymentDB", key, 1, "Ollama", "rules", true,
                Path.Combine(Path.GetTempPath(), $"ReSet-Outcome-{Guid.NewGuid():N}"), false);

            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
            Assert.Contains("L2 AI 교차 리뷰가 수행되지 않았습니다", result.SpecMarkdown);
            userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
        }
```

구역별 경로는 여러 단계(구역 생성, 후보 채점, 합성)를 거치므로 대역이 더 필요할 수 있다. `RunCodeObjectPipelineCoreAsync`의 해당 분기를 읽어 호출되는 `IAiService` 멤버를 모두 대역으로 채운다. 목표는 "최종 L2 검토가 예외를 던지는 상태로 그 분기 끝까지 도달하는 것"이다.

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~SectionalPath_MarksSpecWhenFinalReviewCouldNotRun"
```

기대 결과: FAIL. `Outcome`이 `Passed`로 남고 `NotifyValidationSuccess`가 호출된다.

- [ ] **Step 3: 최종 검토 실패 사유를 기록한다**

`:660-664`을 찾아 바꾼다.

찾을 문자열:
```csharp
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "최종 합성본 L2 Critic 검토 중 실패 (무시하고 계속 진행)");
                    }
```

바꿀 문자열:
```csharp
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Warning(ex, "최종 합성본 L2 Critic 검토 중 실패 (검증 미수행으로 표시하고 계속 진행)");
                        finalReviewFailureReason = ex.Message;
                    }
```

같은 메서드의 `finalL2Result` 선언 바로 앞(`:638` 부근)에 사유 변수를 추가한다.

찾을 문자열:
```csharp
                    ReviewResult? finalL2Result = null;
```

바꿀 문자열:
```csharp
                    ReviewResult? finalL2Result = null;
                    string? finalReviewFailureReason = null;
```

- [ ] **Step 4: 품질 미달 배너를 공유 렌더러로 바꾸고 미수행 분기를 추가한다**

`:720-725`을 찾아 바꾼다.

찾을 문자열:
```csharp
                    // [추가] 최종 보완 후 여전히 결함이 감지된 경우(최종 Critic 검토 기준 점수 미달), 경고 배너 삽입
                    if (finalReview != null && finalReview.HasDefects)
                    {
                        var warningBanner = $"\n> [!CAUTION]\n> **[품질 불합격] 정합성/가독성 기준 미달 (최종 신뢰도 점수: {finalReview.NormalizedScore}/100)**\n> - **평가 점수**: 정합성 {finalReview.ScoreAccuracy}/10, CRUD {finalReview.ScoreCrud}/10, 인터페이스 {finalReview.ScoreInterface}/10, 가독성 {finalReview.ScoreReadability}/10, 예외 {finalReview.ScoreException}/10 (기준 점수: {_criticScoreThreshold}/10)\n> - **최종 Critic 결함 피드백**:\n>   {finalReview.FeedbackComment?.Replace("\n", "\n>   ")}\n\n";
                        specificationMarkdown = warningBanner + specificationMarkdown;
                    }
```

바꿀 문자열:
```csharp
                    // [추가] 최종 보완 후 여전히 결함이 감지된 경우(최종 Critic 검토 기준 점수 미달), 경고 배너 삽입
                    if (finalReview != null && finalReview.HasDefects)
                    {
                        verificationOutcome = VerificationOutcome.QualityRejected;
                        specificationMarkdown =
                            VerificationBanner.QualityRejected(finalReview, _criticScoreThreshold) + specificationMarkdown;
                    }
                    else if (finalReview == null)
                    {
                        // 최종 L2 검토를 수행하지 못했다. 통과로 표시하지 않는다.
                        verificationOutcome = VerificationOutcome.ReviewNotRun;
                        specificationMarkdown =
                            VerificationBanner.ReviewNotRun(finalReviewFailureReason ?? "사유가 기록되지 않았습니다.")
                            + specificationMarkdown;
                    }
```

- [ ] **Step 5: 성공 알림을 상태에 따라 갈라준다**

`:737`을 찾아 바꾼다.

찾을 문자열:
```csharp
                _userInteraction.NotifyValidationSuccess(selectedOption);
            }
            else
            {
```

바꿀 문자열:
```csharp
                if (verificationOutcome == VerificationOutcome.ReviewNotRun)
                {
                    _userInteraction.NotifyError(
                        $"{selectedOption} - [[L2 AI 리뷰]] 를 수행하지 못해 교차 검증 없이 명세서를 확정합니다.");
                }
                else
                {
                    _userInteraction.NotifyValidationSuccess(selectedOption);
                }
            }
            else
            {
```

- [ ] **Step 6: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"
```

기대 결과: 신규 1건 PASS, 기존 전부 PASS.

- [ ] **Step 7: 커밋한다**

```bash
dotnet build 2>&1 | tail -3
dotnet test 2>&1 | tail -2
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "fix(pipeline): mark unreviewed specs on the sectional path too

The sectional generation path swallowed its final critic review in a catch
that logged and continued, then called NotifyValidationSuccess with no
condition at all -- the same defect as the standard loop, in the same method,
one branch over.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: 통합 배치 경로의 L1 배너와 공유 렌더러 (②)

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1565~1630`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: `VerificationBanner` (태스크 1)
- Produces: 없음

통합 경로는 직전 브랜치에서 `ReviewNotRun` 배너를 이미 받았다. 남은 것은 L1 소진 배너와 품질 미달 배너의 공유 렌더러 전환이다.

- [ ] **Step 1: 실패하는 테스트를 작성한다**

```csharp
        [Fact]
        public async Task RunConsolidatedPipelineAsync_MarksPlanWhenL1RetriesAreExhausted()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Consolidated-{Guid.NewGuid():N}");
            var jobName = $"Job_{Guid.NewGuid():N}";
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();

            aiService.BrainstormBatchPlanAsync(
                    Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "brainstorm body" }));
            aiService.DraftBatchPlanStructureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "structure body" }));
            // 필수 H2 헤더가 없어 L1이 항상 실패한다.
            aiService.GenerateConsolidatedBatchPlanAsync(
                    Arg.Any<string>(), Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "## 엉뚱한 헤더\n\n내용" }));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction, "1", "gpt-test");

            try
            {
                var (plan, _) = await orchestrator.RunConsolidatedPipelineAsync(
                    new List<(string FileName, string Content)> { ("dbo.USP_Test", "## 개요") },
                    "C#", jobName, "OpenAI", outputRoot, isBatchMode: true);

                Assert.Contains("L1 기계 검증을 통과하지 못했습니다", plan);
                userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }
```

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~RunConsolidatedPipelineAsync_MarksPlanWhenL1RetriesAreExhausted"
```

기대 결과: FAIL — 배너 문자열이 계획서에 없다.

- [ ] **Step 3: L1 소진 분기에 배너를 넣는다**

`:1569-1571`을 찾아 바꾼다.

찾을 문자열:
```csharp
                    else
                    {
                        _userInteraction.NotifyError($"{jobName} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                        break;
                    }
```

바꿀 문자열:
```csharp
                    else
                    {
                        _userInteraction.NotifyError($"{jobName} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                        consolidatedPlan = VerificationBanner.L1Exhausted(l1Result.Errors) + consolidatedPlan;
                        break;
                    }
```

- [ ] **Step 4: 품질 미달 배너를 공유 렌더러로 바꾼다**

`:1614-1616`을 찾아 바꾼다.

찾을 문자열:
```csharp
                        // 최종 품질 불합격 경고 배너 삽입
                        var warningBanner = $"\n> [!CAUTION]\n> **[품질 불합격] 정합성/가독성 기준 미달 (최종 신뢰도 점수: {l2Result.NormalizedScore}/100)**\n> - **평가 점수**: 정합성 {l2Result.ScoreAccuracy}/10, CRUD {l2Result.ScoreCrud}/10, 인터페이스 {l2Result.ScoreInterface}/10, 가독성 {l2Result.ScoreReadability}/10, 예외 {l2Result.ScoreException}/10 (기준 점수: {_criticScoreThreshold}/10)\n> - **최종 Critic 결함 피드백**:\n>   {l2Result.FeedbackComment?.Replace("\n", "\n>   ")}\n\n";
                        consolidatedPlan = warningBanner + consolidatedPlan;
```

바꿀 문자열:
```csharp
                        // 최종 품질 불합격 경고 배너 삽입
                        consolidatedPlan =
                            VerificationBanner.QualityRejected(l2Result, _criticScoreThreshold) + consolidatedPlan;
```

- [ ] **Step 5: 리뷰 미수행 배너도 공유 렌더러로 바꾼다**

`:1626-1628`을 찾아 바꾼다. 실패 사유를 함께 싣는다.

찾을 문자열:
```csharp
                    consolidatedPlan =
                        "> [!NOTE]\n> **L2 AI 교차 리뷰가 수행되지 않았습니다.** 리뷰 호출이 실패하여 정합성 검증 없이 확정된 문서입니다. 내용을 직접 검토하십시오.\n\n" +
                        consolidatedPlan;
```

바꿀 문자열:
```csharp
                    consolidatedPlan =
                        VerificationBanner.ReviewNotRun(reviewFailureReason ?? "사유가 기록되지 않았습니다.") + consolidatedPlan;
```

`reviewFailureReason`은 이 메서드에 없다. `:1577`의 `bool reviewSuccess = false;` 뒤에 `string? reviewFailureReason = null;`을 추가하고, 같은 영역의 리뷰 catch 블록에서 `reviewFailureReason = ex.Message;`를 채운다.

- [ ] **Step 6: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"
```

기대 결과: 신규 1건 PASS. 기존 `RunConsolidatedPipelineAsync_MarksPlanWhenCriticReviewCouldNotRun`도 계속 PASS해야 한다 — 그 테스트는 `"L2 AI 교차 리뷰가 수행되지 않았습니다"` 부분 문자열을 보므로 사유가 덧붙어도 통과한다.

- [ ] **Step 7: 커밋한다**

```bash
dotnet build 2>&1 | tail -3
dotnet test 2>&1 | tail -2
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "refactor(pipeline): route consolidated banners through the shared renderer

Also gives L1 exhaustion the banner it lacked, so all four terminal states of
the consolidated path now leave a trace in the document rather than only in
the console.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: 포매터 확장과 두 호출부

**Files:**
- Modify: `src/ReSet.Core/Services/SpecificationDocumentFormatter.cs`, `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:458-467`, `src/ReSet.Cli/Program.cs:1625-1632`
- Test: `tests/ReSet.Core.Tests/SpecificationDocumentFormatterTests.cs`

**Interfaces:**
- Consumes: `VerificationOutcome` (태스크 1), `CodeObjectPipelineResult.Outcome` (태스크 2)
- Produces:
  ```csharp
  public static string Format(
      string specification,
      ReviewResult? review,
      VerificationOutcome outcome,
      string provider,
      string modelName,
      string? effort,
      DateTime timestamp);
  ```
  `outcome`은 `review` 바로 뒤에 온다. 태스크 6·7이 이 출력의 `검증 상태` 필드를 읽는다.

- [ ] **Step 1: 실패하는 테스트 3개를 작성한다**

`tests/ReSet.Core.Tests/SpecificationDocumentFormatterTests.cs`의 기존 테스트 뒤에 추가하고, 기존 테스트의 호출에도 `VerificationOutcome.Passed`를 넣는다.

```csharp
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
```

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~SpecificationDocumentFormatterTests"
```

기대 결과: 컴파일 에러 — `Format`에 7번째 인자가 없다.

- [ ] **Step 3: 포매터를 다시 쓴다**

`src/ReSet.Core/Services/SpecificationDocumentFormatter.cs` 전체를 아래로 바꾼다.

```csharp
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
```

- [ ] **Step 4: 재귀 객체 호출부를 갱신한다**

`src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs`에서 찾아 바꾼다.

찾을 문자열:
```csharp
        SpecificationDocumentFormatter.Format(
            analysis.SpecMarkdown ?? string.Empty,
            analysis.Review,
            request.Provider,
```

바꿀 문자열:
```csharp
        SpecificationDocumentFormatter.Format(
            analysis.SpecMarkdown ?? string.Empty,
            analysis.Review,
            analysis.Outcome,
            request.Provider,
```

같은 파일에서 `CodeObjectAnalysisResult`를 만드는 지점(`ExecuteDiscoveredNodesAsync` 내부, `Review = pipelineResult.Review`가 있는 객체 초기화)을 찾아 `Outcome = pipelineResult.Outcome,`을 더한다.

- [ ] **Step 5: 루트 SP 호출부를 갱신한다**

`src/ReSet.Cli/Program.cs`에서 찾아 바꾼다.

찾을 문자열:
```csharp
                SpecificationDocumentFormatter.Format(
                    specMarkdown,
                    review,
                    provider,
```

바꿀 문자열:
```csharp
                SpecificationDocumentFormatter.Format(
                    specMarkdown,
                    review,
                    outcome,
                    provider,
```

`outcome` 변수는 이 스코프에 없다. `RunCodeObjectPipelineAsync`(또는 `RunPipelineAsync`)의 결과에서 받아 온다. 파이프라인 결과를 담는 변수를 찾아 `.Outcome`을 꺼내 지역 변수로 둔다. 컴파일 에러가 정확한 위치를 알려준다.

- [ ] **Step 6: 테스트를 실행해 통과를 확인한다**

```bash
dotnet build 2>&1 | grep -E "error" | head
dotnet test --filter "FullyQualifiedName~SpecificationDocumentFormatterTests"
```

기대 결과: 신규 4건 + 기존 1건 PASS.

- [ ] **Step 7: 커밋한다**

```bash
dotnet build 2>&1 | tail -3
dotnet test 2>&1 | tail -2
git add src/ReSet.Core/Services/SpecificationDocumentFormatter.cs src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs src/ReSet.Cli/Program.cs tests/ReSet.Core.Tests/SpecificationDocumentFormatterTests.cs
git commit -m "feat(spec): state the verification outcome in the document header

A null review used to drop the whole YAML block, leaving an unreviewed spec
indistinguishable from an old-format one. Always emit the header, name the
outcome in it, and let the outcome -- not the presence of a review object --
decide whether scores appear, so a stale first-attempt score cannot ride
along on a document that was never verified.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: YAML 헤더 파서 추출

**Files:**
- Create: `src/ReSet.Cli/SpecHeaderReader.cs`
- Modify: `src/ReSet.Cli/ConsoleUserInteraction.cs:105-146`
- Test: `tests/ReSet.Core.Tests/SpecHeaderReaderTests.cs` (신규)

**Interfaces:**
- Consumes: 없음
- Produces:
  ```csharp
  namespace ReSet.Cli;
  public sealed record SpecHeader(
      string? VerificationStatus,
      int? NormalizedScore,
      int? Accuracy,
      int? Crud,
      int? Readability,
      int? Exception);

  public static class SpecHeaderReader
  {
      public static SpecHeader Read(string markdown);
  }
  ```
  태스크 7이 `VerificationStatus`를 소비한다.

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/SpecHeaderReaderTests.cs`를 새로 만든다.

```csharp
using ReSet.Cli;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class SpecHeaderReaderTests
{
    [Fact]
    public void Read_ParsesStatusAndScores()
    {
        var markdown = "---\n검증 상태: 통과 # 검증 파이프라인 종료 상태\n종합 신뢰도: 80 # 설명\n정합성 점수: 9/10 # 설명\n---\n\n# 본문";

        var header = SpecHeaderReader.Read(markdown);

        Assert.Equal("통과", header.VerificationStatus);
        Assert.Equal(80, header.NormalizedScore);
        Assert.Equal(9, header.Accuracy);
    }

    [Fact]
    public void Read_ReturnsNullStatusForLegacyDocumentWithoutTheField()
    {
        var markdown = "---\n종합 신뢰도: 70\n---\n\n# 본문";

        var header = SpecHeaderReader.Read(markdown);

        Assert.Null(header.VerificationStatus);
        Assert.Equal(70, header.NormalizedScore);
    }

    [Fact]
    public void Read_ReturnsEmptyHeaderWhenThereIsNoYamlBlock()
    {
        var header = SpecHeaderReader.Read("# 본문만 있는 문서");

        Assert.Null(header.VerificationStatus);
        Assert.Null(header.NormalizedScore);
    }

    [Fact]
    public void Read_ParsesReviewNotRunStatus()
    {
        var markdown = "---\n검증 상태: 리뷰 미수행 # 검증 파이프라인 종료 상태\n---\n\n# 본문";

        var header = SpecHeaderReader.Read(markdown);

        Assert.Equal("리뷰 미수행", header.VerificationStatus);
        Assert.Null(header.NormalizedScore);
    }
}
```

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~SpecHeaderReaderTests"
```

기대 결과: 컴파일 에러 — `SpecHeaderReader` 형식을 찾을 수 없음.

- [ ] **Step 3: 파서를 추출한다**

`src/ReSet.Cli/SpecHeaderReader.cs`를 새로 만든다. 값 정규화 규칙(주석 `#` 제거, 괄호 설명 제거, `9/10` → `9`)과 키 별칭은 `ConsoleUserInteraction.cs:105-146`의 기존 로직을 그대로 옮긴다. 별칭을 줄이면 기존 문서가 파싱되지 않는다.

```csharp
using System;

namespace ReSet.Cli
{
    /// <summary>명세서 상단 YAML 헤더에서 검증 상태와 점수를 읽는다.</summary>
    public sealed record SpecHeader(
        string? VerificationStatus,
        int? NormalizedScore,
        int? Accuracy,
        int? Crud,
        int? Readability,
        int? Exception);

    public static class SpecHeaderReader
    {
        public static SpecHeader Read(string markdown)
        {
            string? status = null;
            int? score = null, acc = null, crud = null, read = null, ex = null;

            if (!string.IsNullOrEmpty(markdown) && markdown.StartsWith("---"))
            {
                var endOfYaml = markdown.IndexOf("---", 3, StringComparison.Ordinal);
                if (endOfYaml > 0)
                {
                    foreach (var line in markdown.Substring(3, endOfYaml - 3).Split('\n'))
                    {
                        var parts = line.Split(':', 2);
                        if (parts.Length != 2) continue;

                        var key = parts[0].Trim();
                        var val = parts[1].Trim();

                        var commentIdx = val.IndexOf('#');
                        if (commentIdx >= 0) val = val.Substring(0, commentIdx).Trim();

                        var parenIdx = val.IndexOf('(');
                        if (parenIdx >= 0) val = val.Substring(0, parenIdx).Trim();

                        if (key == "검증 상태")
                        {
                            status = string.IsNullOrWhiteSpace(val) ? null : val;
                            continue;
                        }

                        var slashIdx = val.IndexOf('/');
                        var numberPart = slashIdx >= 0 ? val.Substring(0, slashIdx).Trim() : val;

                        if ((key == "AiConfidenceScore" || key == "종합 신뢰도 점수" || key == "종합 신뢰도" || key == "종합신뢰도") && int.TryParse(numberPart, out var scoreVal)) score = scoreVal;
                        else if ((key == "AccuracyScore" || key == "정합성 점수" || key == "정합성") && int.TryParse(numberPart, out var accVal)) acc = accVal;
                        else if ((key == "CrudScore" || key == "CRUD 점수" || key == "CRUD") && int.TryParse(numberPart, out var crudVal)) crud = crudVal;
                        else if ((key == "ReadabilityScore" || key == "가독성 점수" || key == "가독성") && int.TryParse(numberPart, out var readVal)) read = readVal;
                        else if ((key == "ExceptionScore" || key == "예외처리 점수" || key == "예외처리" || key == "예외 처리 점수" || key == "예외 처리") && int.TryParse(numberPart, out var exVal)) ex = exVal;
                    }
                }
            }

            return new SpecHeader(status, score, acc, crud, read, ex);
        }
    }
}
```

- [ ] **Step 4: `ConsoleUserInteraction`이 파서를 쓰게 한다**

`src/ReSet.Cli/ConsoleUserInteraction.cs`의 인라인 파싱 블록(`if (specificationMarkdown.StartsWith("---"))` 부터 그 닫는 중괄호까지, 약 `:105-146`)을 아래로 바꾼다. 이 단계에서는 표시 동작을 바꾸지 않는다.

```csharp
            var header = SpecHeaderReader.Read(specificationMarkdown);
            var score = header.NormalizedScore ?? 0;
            var acc = header.Accuracy ?? 0;
            var crud = header.Crud ?? 0;
            var read = header.Readability ?? 0;
            var ex = header.Exception ?? 0;
            var scoreFound = header.NormalizedScore.HasValue;
```

블록 앞에 있던 `int score = 0;` 같은 선언들이 중복되면 제거한다. 컴파일러가 알려준다.

- [ ] **Step 5: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~SpecHeaderReaderTests"
dotnet test 2>&1 | tail -2
```

기대 결과: 신규 4건 PASS, 전체 통과.

- [ ] **Step 6: 커밋한다**

```bash
dotnet build 2>&1 | tail -3
git add src/ReSet.Cli/SpecHeaderReader.cs src/ReSet.Cli/ConsoleUserInteraction.cs tests/ReSet.Core.Tests/SpecHeaderReaderTests.cs
git commit -m "refactor(cli): extract the spec header parser so it can be tested

Forty lines of YAML parsing sat inside a method that also prompts the user,
so none of it was reachable from a test. Lift it out unchanged -- same key
aliases, same value normalisation -- before the next task adds a field to it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: L3 승인 화면에 검증 상태 표시

**Files:**
- Modify: `src/ReSet.Cli/ConsoleUserInteraction.cs` (Rule 표시부와 프롬프트 제목)

**Interfaces:**
- Consumes: `SpecHeaderReader.Read`, `SpecHeader.VerificationStatus` (태스크 6)
- Produces: 없음

이 태스크는 TUI 렌더링이라 단위 테스트를 붙이지 않는다. 파싱은 태스크 6에서 이미 테스트로 잠겼고, 남은 것은 Spectre.Console 출력이라 테스트에서 도달할 수 없다. 검증은 빌드와 전체 스위트 통과로 한다.

- [ ] **Step 1: Rule에 상태를 붙인다**

찾을 문자열:
```csharp
            string scoreText = "";
            if (scoreFound)
            {
                var color = score >= 90 ? "green" : (score >= 70 ? "yellow" : "red");
                scoreText = $" | [bold {color}]AI 신뢰도: {score}/100점 (정합성:{acc}, CRUD:{crud}, 가독성:{read}, 예외:{ex})[/]";
            }
```

바꿀 문자열:
```csharp
            string scoreText = "";
            if (scoreFound)
            {
                var color = score >= 90 ? "green" : (score >= 70 ? "yellow" : "red");
                scoreText = $" | [bold {color}]AI 신뢰도: {score}/100점 (정합성:{acc}, CRUD:{crud}, 가독성:{read}, 예외:{ex})[/]";
            }

            // 검증 상태가 통과가 아니면 승인 직전에 눈에 띄어야 한다.
            // 필드가 없는 기존 문서는 표시하지 않는다. 정상 문서까지 경고처럼 보이면 신호가 죽는다.
            var statusText = "";
            var isVerified = header.VerificationStatus is null or "통과";
            if (header.VerificationStatus is not null && !isVerified)
            {
                statusText = $" | [bold red]검증 상태: {Markup.Escape(header.VerificationStatus)}[/]";
            }
```

- [ ] **Step 2: Rule과 프롬프트 제목에 반영한다**

찾을 문자열:
```csharp
            AnsiConsole.Write(new Rule($"[yellow]{selectedOption}{scoreText}[/]") { Justification = Justify.Left });
```

바꿀 문자열:
```csharp
            AnsiConsole.Write(new Rule($"[yellow]{selectedOption}{scoreText}{statusText}[/]") { Justification = Justify.Left });
```

찾을 문자열:
```csharp
                    .Title($"[bold blue]{selectedOption} 명세서 검증 완료.[/] 다음 작업을 선택하세요:")
```

바꿀 문자열:
```csharp
                    .Title(isVerified
                        ? $"[bold blue]{selectedOption} 명세서 검증 완료.[/] 다음 작업을 선택하세요:"
                        : $"[bold red]{selectedOption} 명세서가 검증을 완료하지 못했습니다.[/] 다음 작업을 선택하세요:")
```

- [ ] **Step 3: 빌드하고 전체 테스트를 실행한다**

```bash
dotnet build 2>&1 | tail -3
dotnet test 2>&1 | tail -2
```

기대 결과: 오류 0, 전체 통과.

- [ ] **Step 4: 커밋한다**

```bash
git add src/ReSet.Cli/ConsoleUserInteraction.cs
git commit -m "feat(cli): show the verification status on the approval screen

The approval prompt announced 명세서 검증 완료 unconditionally while the score
line sat blank, so a spec whose cross-check never ran looked the same as one
that passed. Surface the status where the user is about to decide.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: 지시서의 Spec.md 링크 정확성 (①)

**Files:**
- Modify: `src/ReSet.Core/Services/IMetadataExporter.cs:54`, `src/ReSet.Core/Services/MetadataExporter.cs:351-356, 441-449`, `src/ReSet.Cli/Program.cs:737, 1208`
- Test: `tests/ReSet.Core.Tests/MetadataExporterTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  ```csharp
  Task ExportConsolidatedMigrationInstructionsAsync(
      List<SpDefinition> spDefs,
      string consolidatedPlan,
      string jobName,
      string baseOutputDir,
      string targetLanguage,
      OutputPathResolver paths);
  ```

- [ ] **Step 1: 실패하는 테스트 2개를 작성한다**

```csharp
        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_LinksExternalProcedureUnderExternalDirectory()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Instructions-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("AuditDB", "dbo", "USP_External", CodeObjectType.Procedure);
            var paths = new OutputPathResolver("PaymentDB", outputRoot);
            var specPath = paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            await File.WriteAllTextAsync(specPath, "# Spec");

            var spDef = new SpDefinition
            {
                ObjectKey = key, Schema = "dbo", Name = "USP_External", DdlText = "SELECT 1;"
            };

            try
            {
                await new MetadataExporter().ExportConsolidatedMigrationInstructionsAsync(
                    new System.Collections.Generic.List<SpDefinition> { spDef },
                    "## 통합 배치 아키텍처 개요",
                    "Job1",
                    Path.Combine(outputRoot, "Jobs", "Job1"),
                    "C#",
                    paths);

                var instructions = await File.ReadAllTextAsync(
                    Path.Combine(outputRoot, "Jobs", "Job1", "agent", "MigrationInstructions.md"));
                Assert.Contains("External/AuditDB/Procedures/dbo.USP_External/docs/Spec.md", instructions);
                Assert.DoesNotContain("../../../Procedures/dbo.USP_External/docs/Spec.md", instructions);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_WritesReasonWhenSpecFileIsMissing()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Instructions-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Gone", CodeObjectType.Procedure);
            var paths = new OutputPathResolver("PaymentDB", outputRoot);
            var spDef = new SpDefinition
            {
                ObjectKey = key, Schema = "dbo", Name = "USP_Gone", DdlText = "SELECT 1;"
            };

            try
            {
                await new MetadataExporter().ExportConsolidatedMigrationInstructionsAsync(
                    new System.Collections.Generic.List<SpDefinition> { spDef },
                    "## 통합 배치 아키텍처 개요",
                    "Job1",
                    Path.Combine(outputRoot, "Jobs", "Job1"),
                    "C#",
                    paths);

                var instructions = await File.ReadAllTextAsync(
                    Path.Combine(outputRoot, "Jobs", "Job1", "agent", "MigrationInstructions.md"));
                Assert.Contains("명세서 파일을 찾을 수 없습니다", instructions);
                Assert.DoesNotContain("[Spec.md](", instructions);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }
```

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~ExportConsolidatedMigrationInstructionsAsync_Links|FullyQualifiedName~ExportConsolidatedMigrationInstructionsAsync_WritesReason"
```

기대 결과: 컴파일 에러 — 6번째 인자가 없다.

- [ ] **Step 3: 인터페이스와 구현의 시그니처를 바꾼다**

`src/ReSet.Core/Services/IMetadataExporter.cs`와 `src/ReSet.Core/Services/MetadataExporter.cs` 양쪽에서 파라미터 목록의 마지막에 `OutputPathResolver paths`를 추가한다.

찾을 문자열 (두 파일 공통):
```csharp
            string baseOutputDir,
            string targetLanguage)
```

바꿀 문자열:
```csharp
            string baseOutputDir,
            string targetLanguage,
            OutputPathResolver paths)
```

- [ ] **Step 4: 링크 생성을 바꾼다**

`src/ReSet.Core/Services/MetadataExporter.cs`에서 찾아 바꾼다.

찾을 문자열:
```csharp
                foreach (var spDef in spDefs)
                {
                    var spCleanName = $"{spDef.Schema}.{spDef.Name}";
                    var specPath = $"../../../Procedures/{spDef.Schema}.{spDef.Name}/docs/Spec.md";
                    sb.AppendLine($"- **{spCleanName}**:");
                    sb.AppendLine($"  - [Spec.md]({specPath}) (UPDATE/INSERT 상세 매핑 수식 포함)");
                }
```

바꿀 문자열:
```csharp
                foreach (var spDef in spDefs)
                {
                    var spCleanName = $"{spDef.Schema}.{spDef.Name}";
                    sb.AppendLine($"- **{spCleanName}**:");

                    // 경로 규칙은 OutputPathResolver 한 곳에만 둔다. External DB 분기와
                    // 식별자 인코딩이 함께 따라온다.
                    var objectKey = spDef.ObjectKey ?? CodeObjectKey.Create(
                        paths.CurrentDatabase, spDef.Schema, spDef.Name, CodeObjectType.Procedure);
                    var absoluteSpecPath = paths.ResolveSpecPath(objectKey);

                    if (File.Exists(absoluteSpecPath))
                    {
                        var relativeSpecPath = Path.GetRelativePath(agentFolder, absoluteSpecPath)
                            .Replace(Path.DirectorySeparatorChar, '/')
                            .Replace(Path.AltDirectorySeparatorChar, '/');
                        sb.AppendLine($"  - [Spec.md]({relativeSpecPath}) (UPDATE/INSERT 상세 매핑 수식 포함)");
                    }
                    else
                    {
                        sb.AppendLine("  - 명세서 파일을 찾을 수 없습니다. 이 스텝의 비즈니스 로직은 참조할 수 없습니다.");
                    }
                }
```

`OutputPathResolver`의 `_currentDatabase`는 private이다. `paths.CurrentDatabase`를 쓰려면 `src/ReSet.Core/Services/OutputPathResolver.cs`에 읽기 전용 속성을 노출한다.

찾을 문자열:
```csharp
    internal string OutputRoot { get; }
```

바꿀 문자열:
```csharp
    internal string OutputRoot { get; }

    /// <summary>산출물 레이아웃의 기준이 되는 분석 루트 DB.</summary>
    public string CurrentDatabase => _currentDatabase;
```

- [ ] **Step 5: 두 호출부를 갱신한다**

`src/ReSet.Cli/Program.cs`의 두 호출부에서 마지막 인자를 더한다.

찾을 문자열 (2회 등장, 각각 개별 수정):
```csharp
                                jobsOutputDir,
                                targetLanguage);
```

바꿀 문자열:
```csharp
                                jobsOutputDir,
                                targetLanguage,
                                new OutputPathResolver(database, outputDir));
```

들여쓰기가 두 자리에서 다를 수 있다. 컴파일 에러가 위치를 알려준다.

- [ ] **Step 6: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~MetadataExporterTests"
```

기대 결과: 신규 2건 PASS, 기존 전부 PASS.

- [ ] **Step 7: 커밋한다**

```bash
dotnet build 2>&1 | tail -3
dotnet test 2>&1 | tail -2
git add src/ReSet.Core/Services/IMetadataExporter.cs src/ReSet.Core/Services/MetadataExporter.cs src/ReSet.Core/Services/OutputPathResolver.cs src/ReSet.Cli/Program.cs tests/ReSet.Core.Tests/MetadataExporterTests.cs
git commit -m "fix(export): resolve instruction spec links instead of assuming Procedures/

The link was hardcoded to ../../../Procedures/, so a step from another
database pointed at a path that does not exist -- and the same document tells
the coding agent not to read the original SQL, making that link its only
route to the logic. Compute it with the resolver, which knows the External
layout and the identifier encoding, and write a reason when the file is
absent rather than a link that goes nowhere.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: 최종 검증과 문서 동기화

**Files:**
- Modify: `AGENTS.md:236`

**Interfaces:**
- Consumes: 태스크 1~8 전부
- Produces: 없음

- [ ] **Step 1: 클린 빌드와 전체 테스트를 실행한다**

```bash
dotnet clean -v q
dotnet build 2>&1 | tail -3
dotnet test 2>&1 | tail -2
```

기대 결과: 오류 0. 경고는 `tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`의 기존 8건만. 통과 개수를 기록한다.

- [ ] **Step 2: 경고가 늘지 않았는지 확인한다**

```bash
dotnet build 2>&1 | grep -c "warning"
dotnet build 2>&1 | grep "warning" | grep -v "DbMetadataServiceTests.cs" | head
```

두 번째 명령의 출력이 비어 있어야 한다. 비어 있지 않으면 이번 브랜치가 새 경고를 만든 것이므로 고친다.

- [ ] **Step 3: AGENTS.md의 테스트 개수를 실제 값으로 갱신한다**

`<실제개수>`는 Step 1의 출력에서 읽은 숫자로 대체한다. 계획이 예상한 값이 아니라 측정값을 쓴다.

찾을 문자열:
```
- [ ] `dotnet test` 명령어를 실행하여 329개의 단위 테스트가 모두 예외 없이 100% 통과(Passed)하였는가?
```

바꿀 문자열:
```
- [ ] `dotnet test` 명령어를 실행하여 <실제개수>개의 단위 테스트가 모두 예외 없이 100% 통과(Passed)하였는가?
```

- [ ] **Step 4: 문서 링크 유효성을 확인한다**

```bash
grep -o '](\.\./src/[^)]*)' docs/architecture.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "docs/$p" ] || echo "BROKEN architecture.md: $p"; done
grep -ho '](\./[^)]*)' AGENTS.md README.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "$p" ] || echo "BROKEN: $p"; done
```

기대 결과: 빈 출력.

- [ ] **Step 5: 커밋하고 작업 트리를 확인한다**

```bash
git add AGENTS.md
git commit -m "docs: update the unit test count after the outcome work

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
git status --short
git log --oneline -10
```

기대 결과: 변경 없음.

---

## 자체 검토 결과

**스펙 커버리지** — 모든 요구가 태스크에 대응된다.

| 스펙 항목 | 태스크 |
|---|---|
| §1 `VerificationOutcome` + `Outcome` 필드 | 1 |
| §2 `VerificationBanner` | 1 |
| §2 세 종료 영역이 같은 렌더러 사용 | 2·3·4 |
| §2 구역별 경로의 3상태 매핑 | 3 |
| §3 포매터 확장, Outcome이 점수 노출을 결정 | 5 |
| §3 Outcome이 두 호출부 모두에 전달 | 5 (Step 4·5) |
| §4 L3 화면 표시 | 7 |
| §4 YAML 파싱 순수 함수 추출 | 6 |
| §5 지시서 링크 리솔버 계산 + 존재 확인 | 8 |
| §5 `ObjectKey` null 폴백 | 8 (Step 4) |
| §6 오류 처리 | 1·3·8에 분산 (렌더러 무IO, catch에 `OperationCanceledException` 제외, `File.Exists` 가드) |
| 검증 시나리오 | 9 |

**타입 일관성** — `VerificationOutcome`(태스크 1)의 네 값이 태스크 2·3·4의 상태 확정과 태스크 5의 `switch`에 그대로 쓰인다. `VerificationBanner`의 세 메서드 시그니처가 태스크 2·3·4의 호출과 일치한다. `SpecHeader.VerificationStatus`(태스크 6)를 태스크 7이 소비한다. `OutputPathResolver.CurrentDatabase`는 태스크 8에서 신설하고 같은 태스크에서 소비한다.

**계획이 남긴 판단 지점** — 태스크 2 Step 7·8, 태스크 3 Step 1, 태스크 5 Step 5는 정확한 코드 위치를 컴파일러나 파일 읽기로 찾도록 지시한다. 해당 지점들은 1,240줄 메서드 내부라 라인 번호가 앞선 태스크의 편집으로 밀리기 때문이다. 각 지시에 무엇을 찾아야 하는지와 목표 상태를 명시했다.

이 지점들이 이 계획에서 가장 약한 부분이다. 다른 태스크는 찾을 문자열을 그대로 실었지만, 여기서는 구현자가 판단해야 한다. 태스크 3 Step 1(구역별 경로 진입 조건과 필요한 대역)이 특히 그렇다 — 그 분기는 여러 AI 호출을 거치므로 테스트가 끝까지 도달하게 만드는 데 시행착오가 필요할 수 있다. 구현자가 막히면 진입 조건을 만족시키지 못한 것이므로, `RunCodeObjectPipelineCoreAsync`의 해당 `if` 조건을 먼저 읽고 대역을 맞추는 것이 빠른 길이다.
