# 채택본 응집과 재생성 신호 구조화 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 채택된 시도를 하나의 객체로 묶어 중복 규칙과 변수 어긋남을 없애고, 지역 모델 재생성 범위를 산문 키워드가 아니라 구조화된 점수로 정한다.

**Architecture:** `BestAttempt`가 후보를 `AttemptCandidate` 레코드 하나로 보관하면 "후보가 있는가"를 물을 곳이 하나가 되고(`Current != null`), 같은 레코드에 `AiResult`를 실으면 배치의 `finalAiResult`가 채택본과 어긋날 자리가 사라진다. 별개로 `RegenerationScopeSelector`가 Critic 점수와 L1 오류 종류에서 재생성 범위를 계산해, LLM 산문에 키워드를 매칭하던 약 35줄을 대체한다.

**Tech Stack:** .NET 10 / C#, xUnit, NSubstitute

설계 문서: [2026-08-05-adoption-cohesion-design.md](../specs/2026-08-05-adoption-cohesion-design.md)

## Global Constraints

- 대상 파일은 모두 `src/ReSet.Core/`와 `tests/ReSet.Core.Tests/` 아래에 있다. 저장소 루트에서 명령을 실행한다.
- 빌드: `dotnet build ReSet.slnx` / 테스트: `dotnet test tests/ReSet.Core.Tests`
- 착수 시점 기준 **635건 전부 통과** 상태다. 어떤 태스크도 이 수를 줄여서는 안 된다.
- 빌드 경고는 `tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`의 기존 nullable 경고 **8건뿐**이다. 새 경고는 결함이다.
- `OperationCanceledException`을 잡거나 감싸지 마라. `CancellationPolicyTests`가 Roslyn으로 `src/` 전체를 검사해 실패시킨다.
- 주석과 문서는 한국어로 쓴다. 클래스·메서드명은 영어를 유지한다.
- 텍스트·규칙은 단일 소유자 클래스에 둔다. 배너 문구는 `VerificationBanner`, 채택 규칙은 `RetryRescue`, 후보 갱신 규칙은 `BestAttempt`, 재생성 범위는 `RegenerationScopeSelector`가 소유한다.
- 네임스페이스: `ReviewResult`·`DetailedError`·`ErrorType`·`BestAttempt`·`RetryRescue`는 `ReSet.Core.Services`, `AiResult`·`ConsolidatedPipelineResult`는 `ReSet.Core.Models`에 있다. `AiResult`를 참조하는 `Services` 파일에는 `using ReSet.Core.Models;`가 필요하다.
- `VerificationPipelineOrchestrator.cs`의 순차 루프와 배치 루프는 **들여쓰기 깊이가 다르다.** 순차 루프가 한 단계(4칸) 더 깊다.
- 이 계획은 앞선 태스크가 행을 늘리므로 **인용된 행 번호가 뒤로 밀린다.** 항상 코드 내용으로 위치를 찾고, 행 번호는 참고로만 쓴다.

---

## Task 1: 후보를 단일 레코드로

`HasCandidate`가 `src/`에서 참조되지 않고 `RetryRescue`가 그 정의를 인라인으로 재진술하는 상태를 끝낸다. 프로퍼티를 그냥 호출할 수 없는 이유는 흐름 분석 때문이다 — 후보를 하나의 nullable 객체로 만들면 그 문제가 사라진다.

**Files:**
- Modify: `src/ReSet.Core/Services/BestAttempt.cs`
- Modify: `src/ReSet.Core/Services/RetryRescue.cs`
- Test: `tests/ReSet.Core.Tests/BestAttemptTests.cs`

**Interfaces:**
- Consumes: 기존 `ReviewResult.NormalizedScore`
- Produces: `sealed record AttemptCandidate(string Markdown, ReviewResult Review, int AttemptNumber)`, `BestAttempt.Current` (타입 `AttemptCandidate?`), `BestAttempt.TryRecord(int attemptNumber, string markdown, ReviewResult review)` → `bool`

`BestAttempt`의 기존 공개 프로퍼티 `Markdown`/`Review`/`AttemptNumber`/`HasCandidate` 넷은 **삭제**된다. 오케스트레이터는 `TryRecord`만 쓰므로 영향받지 않는다(확인: `grep -n "bestAttempt\." src/` 결과가 `TryRecord` 2회뿐).

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/BestAttemptTests.cs`의 다섯 `[Fact]`를 아래로 통째 교체한다. 파일 상단의 `Attempt1/2/3` 헬퍼와 그 주석은 그대로 둔다.

```csharp
        [Fact]
        public void NoCandidateRecorded_ExposesEmptyState()
        {
            var best = new BestAttempt();

            Assert.Null(best.Current);
        }

        [Fact]
        public void FirstCandidate_IsAlwaysRecorded()
        {
            var best = new BestAttempt();

            Assert.True(best.TryRecord(1, "문서1", Attempt1()));
            Assert.NotNull(best.Current);
            Assert.Equal("문서1", best.Current!.Markdown);
            Assert.Equal(1, best.Current.AttemptNumber);
            Assert.Equal(70, best.Current.Review.NormalizedScore);
        }

        [Fact]
        public void HigherScore_ReplacesTheCurrentBest()
        {
            var best = new BestAttempt();
            best.TryRecord(1, "문서1", Attempt1());

            Assert.True(best.TryRecord(2, "문서2", Attempt2()));
            Assert.Equal("문서2", best.Current!.Markdown);
            Assert.Equal(2, best.Current.AttemptNumber);
            Assert.Equal(90, best.Current.Review.NormalizedScore);
        }

        // 이번 사고의 핵심. 78점짜리가 90점짜리를 밀어내면 안 된다.
        [Fact]
        public void LowerScore_DoesNotReplaceTheCurrentBest()
        {
            var best = new BestAttempt();
            best.TryRecord(1, "문서1", Attempt1());
            best.TryRecord(2, "문서2", Attempt2());

            Assert.False(best.TryRecord(3, "문서3", Attempt3()));
            Assert.Equal("문서2", best.Current!.Markdown);
            Assert.Equal(2, best.Current.AttemptNumber);
        }

        // 나중 시도가 더 낫다는 근거가 없고, 실제로 후속 시도가 다른 축을 망가뜨렸다.
        [Fact]
        public void EqualScore_KeepsTheEarlierAttempt()
        {
            var best = new BestAttempt();
            best.TryRecord(1, "문서1", Attempt2());

            Assert.False(best.TryRecord(2, "문서2", Attempt2()));
            Assert.Equal("문서1", best.Current!.Markdown);
            Assert.Equal(1, best.Current.AttemptNumber);
        }

        // 네 값이 한 덩어리로 움직인다 — 하나만 갱신되어 어긋날 자리가 없다.
        [Fact]
        public void Current_CarriesEveryValueOfTheSameAttempt()
        {
            var best = new BestAttempt();
            best.TryRecord(2, "문서2", Attempt2());

            var candidate = best.Current;

            Assert.NotNull(candidate);
            Assert.Equal("문서2", candidate!.Markdown);
            Assert.Equal(2, candidate.AttemptNumber);
            Assert.Equal(90, candidate.Review.NormalizedScore);
        }
```

- [ ] **Step 2: 실패 확인**

실행: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~BestAttemptTests"`

기대: 컴파일 실패 — `BestAttempt`에 `Current`가 없다.

- [ ] **Step 3: 최소 구현 — BestAttempt**

`src/ReSet.Core/Services/BestAttempt.cs`의 `namespace` 블록 내용을 아래로 교체한다. **클래스 XML 주석의 사고 경위는 그대로 보존한다** — 이 클래스가 존재하는 이유가 거기 적혀 있다.

```csharp
namespace ReSet.Core.Services
{
    /// <summary>
    /// 재시도 루프가 만든 후보 하나. 네 값이 함께 움직이므로 한 덩어리로 든다.
    ///
    /// 흩어 두면 두 가지가 깨진다. "후보가 있는가"를 물을 때마다 어느 필드를 봐야
    /// 하는지 정해야 하고(그 판단이 두 곳에 복제됐다), 채택된 시도를 가리키는 값들
    /// 중 하나만 갱신되어 서로 어긋날 수 있다.
    /// </summary>
    public sealed record AttemptCandidate(string Markdown, ReviewResult Review, int AttemptNumber);

    /// <summary>
    /// 재시도 루프가 만들어 낸 후보 중 가장 점수가 높은 하나를 보관한다.
    ///
    /// 이 클래스가 존재하는 이유: 재시도가 소진되면 파이프라인이 마지막 시도를 그대로
    /// 확정했다. 2026-08-04 dbo.UP_Util_PG_Client_CMRate_Ins 실행에서 시도 2가 90점,
    /// 시도 3이 78점이었는데 78점이 산출물이 됐다. 시도 2는 다섯 항목 중 예외 하나만
    /// 기준에 미달했고 나머지는 정합성 10, 인터페이스 9, 가독성 10이었다.
    ///
    /// 갱신 규칙을 이곳에서만 정의한다. 두 재시도 루프가 각자 비교식을 쓰면 한쪽만
    /// 고쳐지는 사고가 그대로 재발한다.
    /// </summary>
    public sealed class BestAttempt
    {
        /// <summary>보관 중인 최고 점수 후보. 아직 없으면 null.</summary>
        public AttemptCandidate? Current { get; private set; }

        /// <summary>
        /// 후보를 제시한다. 기존 최고보다 점수가 높을 때만 교체하고 교체 여부를 돌려준다.
        /// 동점이면 교체하지 않는다 — 나중 시도가 더 낫다는 근거가 없고, 실제로 후속
        /// 시도가 이미 만점이던 항목을 망가뜨리는 사례가 관찰됐다.
        /// </summary>
        public bool TryRecord(int attemptNumber, string markdown, ReviewResult review)
        {
            if (review == null)
            {
                return false;
            }

            if (Current != null && review.NormalizedScore <= Current.Review.NormalizedScore)
            {
                return false;
            }

            Current = new AttemptCandidate(markdown, review, attemptNumber);
            return true;
        }
    }
}
```

- [ ] **Step 4: 최소 구현 — RetryRescue 가드**

`src/ReSet.Core/Services/RetryRescue.cs`의 `TryRescue` 본문을 아래로 교체한다. 메서드 시그니처와 XML 주석은 그대로 둔다.

```csharp
            var candidate = best?.Current;
            if (candidate == null)
            {
                return null;
            }

            var context = reason.HasValue
                ? new RescueContext(reason.Value, abortedAttempt, candidate.AttemptNumber)
                : null;

            return new RescuedAttempt(
                VerificationBanner.QualityRejected(candidate.Review, scoreThreshold, context) + candidate.Markdown,
                candidate.Review,
                candidate.AttemptNumber);
```

- [ ] **Step 5: 통과 확인**

실행: `dotnet test tests/ReSet.Core.Tests`

기대: 전체 통과(636건 — 기존 635 + `Current_CarriesEveryValueOfTheSameAttempt` 1건). 기존 `RetryRescueTests` 3건과 오케스트레이터의 구제 테스트가 전부 그대로 통과해야 한다 — 그것이 이 리팩터링의 안전망이다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/BestAttempt.cs src/ReSet.Core/Services/RetryRescue.cs tests/ReSet.Core.Tests/BestAttemptTests.cs
git commit -m "refactor: hold the best attempt as one candidate record"
```

---

## Task 2: 채택본의 AiResult를 배치 산출물에 반영

배치의 `finalAiResult`는 **생성이 성공할 때만** 갱신되고 구제 경로는 그것을 건드리지 않는다. 1차 88점, 2차 생성 성공하나 64점, 3차 예외이면 채택본은 1차인데 `Thinking.md`와 `raw/prompt-context.md`는 2차를 서술한다.

**Files:**
- Modify: `src/ReSet.Core/Services/BestAttempt.cs`
- Modify: `src/ReSet.Core/Services/RetryRescue.cs`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (`TryRecord` 호출부 2곳, 배치 구제 자리 4곳)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 1의 `AttemptCandidate`, `BestAttempt.Current`, `BestAttempt.TryRecord`
- Produces: `AttemptCandidate(string Markdown, ReviewResult Review, int AttemptNumber, AiResult? Generation)`, `BestAttempt.TryRecord(int attemptNumber, string markdown, ReviewResult review, AiResult? generation)` → `bool`, `RescuedAttempt(string Markdown, ReviewResult Review, int AttemptNumber, AiResult? Generation)`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`의 `RunConsolidatedPipelineAsync_FirstGenerationThrows_StillReturnsNull` 바로 뒤에 넣는다.

```csharp
        // finalAiResult는 생성이 성공할 때만 갱신되므로 채택본과 어긋날 수 있었다.
        // 1차가 최고점인데 2차 생성이 성공(점수는 더 낮음)하고 3차가 죽으면,
        // 채택본은 1차인데 Thinking.md/prompt-context.md는 2차를 서술했다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_RescuedPlan_CarriesTheAdoptedAttemptsAiResult()
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
                    _ => Task.FromResult(new AiResult { Content = plan1, ThinkingText = "생각1", SystemPrompt = "시스템1", UserPrompt = "사용자1" }),
                    _ => Task.FromResult(new AiResult { Content = plan2, ThinkingText = "생각2", SystemPrompt = "시스템2", UserPrompt = "사용자2" }),
                    _ => throw new InvalidOperationException("generation timed out"));

            // 1차 88점이 최고, 2차는 생성에 성공하지만 64점.
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획1고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 9, ScoreCrud = 10, ScoreInterface = 9, ScoreReadability = 9, ScoreException = 7 }));
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획2고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 6, ScoreCrud = 5, ScoreInterface = 7, ScoreReadability = 7, ScoreException = 7 }));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            Assert.NotNull(result.Plan);
            Assert.Contains("계획1고유표시", result.Plan);

            // 산출물이 서술하는 시도와 채택된 시도가 같아야 한다.
            Assert.NotNull(result.Result);
            Assert.Equal("생각1", result.Result!.ThinkingText);
            Assert.Equal("시스템1", result.Result.SystemPrompt);
        }
```

`ConsolidatedPipelineResult`의 `AiResult` 프로퍼티 이름은 `Result`다(`AiResult`가 아니다).

- [ ] **Step 2: 실패 확인**

실행: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CarriesTheAdoptedAttemptsAiResult"`

기대: FAIL — `Assert.Equal("생각1", ...)`에서 실패하고 실제값은 `"생각2"`다. 그것이 이 결함의 증상이다.

- [ ] **Step 3: 최소 구현 — 레코드에 Generation 추가**

`src/ReSet.Core/Services/BestAttempt.cs` 맨 위에 using을 추가한다.

```csharp
using ReSet.Core.Models;
```

`AttemptCandidate` 선언을 교체한다.

```csharp
    public sealed record AttemptCandidate(string Markdown, ReviewResult Review, int AttemptNumber, AiResult? Generation);
```

`TryRecord` 시그니처와 대입을 교체한다.

```csharp
        /// <summary>
        /// 후보를 제시한다. 기존 최고보다 점수가 높을 때만 교체하고 교체 여부를 돌려준다.
        /// 동점이면 교체하지 않는다 — 나중 시도가 더 낫다는 근거가 없고, 실제로 후속
        /// 시도가 이미 만점이던 항목을 망가뜨리는 사례가 관찰됐다.
        ///
        /// generation이 nullable인 이유: 순차 SP 루프는 accumulatedThinking에 모든 시도의
        /// 추론을 누적해 내보내므로 채택본 하나를 가리킬 필요가 없다. 단일 AiResult를
        /// 스냅샷하는 배치 루프만 실제 값을 넘긴다.
        /// </summary>
        public bool TryRecord(int attemptNumber, string markdown, ReviewResult review, AiResult? generation)
        {
            if (review == null)
            {
                return false;
            }

            if (Current != null && review.NormalizedScore <= Current.Review.NormalizedScore)
            {
                return false;
            }

            Current = new AttemptCandidate(markdown, review, attemptNumber, generation);
            return true;
        }
```

- [ ] **Step 4: 최소 구현 — RescuedAttempt에 Generation 추가**

`src/ReSet.Core/Services/RetryRescue.cs` 맨 위에 using을 추가한다.

```csharp
using ReSet.Core.Models;
```

`RescuedAttempt` 선언을 교체한다.

```csharp
    /// <summary>구제로 채택된 문서와 그 리뷰. Markdown에는 배너가 이미 붙어 있다.</summary>
    public sealed record RescuedAttempt(string Markdown, ReviewResult Review, int AttemptNumber, AiResult? Generation);
```

`TryRescue`의 반환문을 교체한다.

```csharp
            return new RescuedAttempt(
                VerificationBanner.QualityRejected(candidate.Review, scoreThreshold, context) + candidate.Markdown,
                candidate.Review,
                candidate.AttemptNumber,
                candidate.Generation);
```

- [ ] **Step 5: 최소 구현 — TryRecord 호출부 2곳**

`VerificationPipelineOrchestrator.cs`에서 순차 루프의 호출(주변에 `specificationMarkdown`이 보인다)을 찾아 교체한다.

```csharp
                        bestAttempt.TryRecord(attempt, specificationMarkdown, l2Result, null);
```

배치 루프의 호출(주변에 `consolidatedPlan`이 보이고, **들여쓰기가 한 단계 얕다**)을 찾아 교체한다.

```csharp
                    bestAttempt.TryRecord(attempt, consolidatedPlan, l2Result, finalAiResult);
```

배치에서 `finalAiResult`를 넘기는 이유: 그 지점의 지역 변수 `aiResult`는 `try` 블록 안에 선언돼 스코프 밖이지만, `finalAiResult`는 루프 바깥에 선언돼 있고 같은 회차의 생성 직후 이미 갱신돼 있다.

- [ ] **Step 6: 최소 구현 — 배치 구제 자리 4곳**

네 자리 모두 `planReview = ...` 대입 **바로 앞**에 한 줄을 넣는다. 순차 루프의 구제 자리는 건드리지 않는다.

생성 실패 자리(주변 문구: `AI 생성이 중단되어 가장 높은 점수를 받은`):

```csharp
                    finalAiResult = rescued.Generation ?? finalAiResult;
                    planReview = rescued.Review;
```

L1 소진 자리(주변 문구: `[[L1 기계 검증]] 최종 보완 실패. 가장 높은 점수를 받은`):

```csharp
                            finalAiResult = rescued.Generation ?? finalAiResult;
                            planReview = rescued.Review;
```

L2 리뷰 실패 자리(주변 문구: `를 수행하지 못해 가장 높은 점수를 받은`):

```csharp
                        finalAiResult = rescued.Generation ?? finalAiResult;
                        planReview = rescued.Review;
```

정상 소진 자리(주변에 `var adoptedNumber = rescued?.AttemptNumber ?? attempt;`가 있다) — 여기서는 `rescued`가 nullable이다:

```csharp
                        finalAiResult = rescued?.Generation ?? finalAiResult;
                        planOutcome = VerificationOutcome.QualityRejected;
```

- [ ] **Step 7: 통과 확인**

실행: `dotnet test tests/ReSet.Core.Tests`

기대: 전체 통과(637건). `RetryRescueTests`가 컴파일 오류를 내면 `RescuedAttempt` 생성자 인자 수가 맞지 않는 것이니, 테스트가 아니라 호출부를 확인한다.

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core/Services/BestAttempt.cs src/ReSet.Core/Services/RetryRescue.cs src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "fix: report the adopted attempt's AI result in batch artifacts"
```

---

## Task 3: 재생성 범위 계산기

지역 모델 경로는 `feedbackLog`에 키워드 매칭을 걸어 재생성할 섹션을 정한다. `CriticFeedbackLog`가 넣는 점수 줄이 항상 `CRUD`라는 글자를 담으므로, 누적 이력이 있는 **모든 재시도 회차에서 CRUD 섹션이 무조건 재생성**된다. 이 태스크는 대체할 계산기를 만든다. 아직 아무도 쓰지 않는 순수 추가다.

**Files:**
- Create: `src/ReSet.Core/Services/RegenerationScope.cs`
- Test: `tests/ReSet.Core.Tests/RegenerationScopeSelectorTests.cs` (신규)

**Interfaces:**
- Consumes: 기존 `ReviewResult`(항목별 점수 5개), 기존 `DetailedError`/`ErrorType`(둘 다 `ReSet.Core.Services`, `MechanicalValidator.cs`에 정의)
- Produces: `sealed record RegenerationScope(bool RunStage1, bool Overview, bool Crud, bool Logic)` + `RegenerationScope.Everything`, `RegenerationScopeSelector.FromReview(ReviewResult review, int scoreThreshold)` → `RegenerationScope`, `RegenerationScopeSelector.FromL1Errors(IReadOnlyList<DetailedError> errors)` → `RegenerationScope`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/RegenerationScopeSelectorTests.cs`를 만든다.

```csharp
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class RegenerationScopeSelectorTests
    {
        // 모든 항목이 기준(8)을 넘는 만점 리뷰. 각 테스트가 필요한 항목만 끌어내린다.
        private static ReviewResult Perfect() => new()
        {
            HasDefects = true,
            ScoreAccuracy = 10,
            ScoreCrud = 10,
            ScoreInterface = 10,
            ScoreReadability = 10,
            ScoreException = 10
        };

        // 정합성은 비즈니스 로직 자체가 틀렸다는 뜻이라 구조화 데이터를 다시 뽑아야 한다.
        [Fact]
        public void FromReview_AccuracyBelowThreshold_RerunsStage1AndLogic()
        {
            var review = Perfect();
            review.ScoreAccuracy = 5;

            var scope = RegenerationScopeSelector.FromReview(review, 8);

            Assert.True(scope.RunStage1);
            Assert.True(scope.Logic);
            Assert.False(scope.Overview);
            Assert.False(scope.Crud);
        }

        [Fact]
        public void FromReview_CrudBelowThreshold_RerunsStage1AndCrud()
        {
            var review = Perfect();
            review.ScoreCrud = 5;

            var scope = RegenerationScopeSelector.FromReview(review, 8);

            Assert.True(scope.RunStage1);
            Assert.True(scope.Crud);
            Assert.False(scope.Overview);
            Assert.False(scope.Logic);
        }

        // 인터페이스는 파라미터·반환 정의라 개요 섹션의 문제다. 구조는 멀쩡하다.
        [Fact]
        public void FromReview_InterfaceBelowThreshold_RegeneratesOverviewOnly()
        {
            var review = Perfect();
            review.ScoreInterface = 5;

            var scope = RegenerationScopeSelector.FromReview(review, 8);

            Assert.False(scope.RunStage1);
            Assert.True(scope.Overview);
            Assert.False(scope.Crud);
            Assert.False(scope.Logic);
        }

        [Fact]
        public void FromReview_ReadabilityBelowThreshold_RegeneratesLogicOnly()
        {
            var review = Perfect();
            review.ScoreReadability = 5;

            var scope = RegenerationScopeSelector.FromReview(review, 8);

            Assert.False(scope.RunStage1);
            Assert.True(scope.Logic);
            Assert.False(scope.Overview);
            Assert.False(scope.Crud);
        }

        [Fact]
        public void FromReview_ExceptionBelowThreshold_RegeneratesLogicOnly()
        {
            var review = Perfect();
            review.ScoreException = 5;

            var scope = RegenerationScopeSelector.FromReview(review, 8);

            Assert.False(scope.RunStage1);
            Assert.True(scope.Logic);
            Assert.False(scope.Overview);
            Assert.False(scope.Crud);
        }

        [Fact]
        public void FromReview_MultipleBelowThreshold_TakesTheUnion()
        {
            var review = Perfect();
            review.ScoreCrud = 5;
            review.ScoreInterface = 5;

            var scope = RegenerationScopeSelector.FromReview(review, 8);

            Assert.True(scope.RunStage1);
            Assert.True(scope.Overview);
            Assert.True(scope.Crud);
            Assert.False(scope.Logic);
        }

        // 점수는 다 통과했는데 Critic이 결함을 지적한 경로가 있다.
        // 어느 섹션인지 지역화할 근거가 없으므로 전부 다시 만든다.
        [Fact]
        public void FromReview_NothingBelowThreshold_FallsBackToEverything()
        {
            var scope = RegenerationScopeSelector.FromReview(Perfect(), 8);

            Assert.Equal(RegenerationScope.Everything, scope);
        }

        // L1은 형식 검증이라 구조화 데이터에 영향이 없다. Stage 1은 언제나 건너뛴다.
        [Fact]
        public void FromL1Errors_OnlyMermaid_RegeneratesLogicWithoutStage1()
        {
            var errors = new List<DetailedError>
            {
                new() { Type = ErrorType.MermaidQuoteMissing, Message = "따옴표 누락" },
                new() { Type = ErrorType.MermaidCliError, Message = "파스 실패" }
            };

            var scope = RegenerationScopeSelector.FromL1Errors(errors);

            Assert.False(scope.RunStage1);
            Assert.True(scope.Logic);
            Assert.False(scope.Overview);
            Assert.False(scope.Crud);
        }

        // 어느 헤더가 빠졌는지 메시지를 파싱해 추측하지 않는다. 보수적으로 전부 다시 만든다.
        [Fact]
        public void FromL1Errors_HeaderMissing_RegeneratesEverySectionWithoutStage1()
        {
            var errors = new List<DetailedError>
            {
                new() { Type = ErrorType.MermaidQuoteMissing, Message = "따옴표 누락" },
                new() { Type = ErrorType.HeaderMissing, Message = "## CRUD 분석 없음" }
            };

            var scope = RegenerationScopeSelector.FromL1Errors(errors);

            Assert.False(scope.RunStage1);
            Assert.True(scope.Overview);
            Assert.True(scope.Crud);
            Assert.True(scope.Logic);
        }

        [Fact]
        public void FromL1Errors_Empty_FallsBackToEverything()
        {
            var scope = RegenerationScopeSelector.FromL1Errors(new List<DetailedError>());

            Assert.Equal(RegenerationScope.Everything, scope);
        }
    }
}
```

- [ ] **Step 2: 실패 확인**

실행: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RegenerationScopeSelectorTests"`

기대: 컴파일 실패 — `RegenerationScope`가 존재하지 않는다.

- [ ] **Step 3: 최소 구현**

`src/ReSet.Core/Services/RegenerationScope.cs`를 만든다.

```csharp
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 지역 모델 경로에서 이번 회차에 다시 만들 범위.
    /// Overview는 `## 개요`+`## 파라미터 목록`, Crud는 `## CRUD 분석`,
    /// Logic은 `## 로직 흐름 요약`+`## 비즈니스 흐름 시각화`에 해당한다.
    /// </summary>
    public sealed record RegenerationScope(bool RunStage1, bool Overview, bool Crud, bool Logic)
    {
        public static readonly RegenerationScope Everything = new(true, true, true, true);
    }

    /// <summary>
    /// 재생성 범위를 구조화된 신호에서 계산한다.
    ///
    /// 이 클래스가 존재하는 이유: 이전 구현은 Actor에게 보낼 피드백 문자열에 키워드를
    /// 매칭해 범위를 정했다. CriticFeedbackLog가 넣는 항목별 점수 줄이 항상 "CRUD"라는
    /// 글자를 포함하므로, 누적 이력이 있는 모든 재시도 회차에서 CRUD 섹션이 무조건
    /// 재생성됐다. 더 근본적으로는 LLM이 쓴 산문에 키워드를 거는 방식이라 프롬프트
    /// 문구가 바뀌면 아무 신호 없이 오작동한다.
    ///
    /// Critic은 이미 항목별 점수를 구조화된 값으로 돌려주고, MechanicalValidator는
    /// 오류를 ErrorType으로 분류해 둔다. 그 둘을 쓴다.
    /// </summary>
    public static class RegenerationScopeSelector
    {
        /// <summary>
        /// L2 리뷰 점수에서 범위를 정한다. 정합성·CRUD가 미달이면 구조화 데이터 자체가
        /// 틀렸다는 뜻이므로 Stage 1을 다시 돈다. 나머지 셋은 표현의 문제라 이미 뽑아
        /// 둔 구조를 재사용한다.
        /// </summary>
        public static RegenerationScope FromReview(ReviewResult review, int scoreThreshold)
        {
            if (review == null)
            {
                return RegenerationScope.Everything;
            }

            bool accuracy = review.ScoreAccuracy < scoreThreshold;
            bool crud = review.ScoreCrud < scoreThreshold;
            bool interfaceDefinition = review.ScoreInterface < scoreThreshold;
            bool readability = review.ScoreReadability < scoreThreshold;
            bool exception = review.ScoreException < scoreThreshold;

            var scope = new RegenerationScope(
                RunStage1: accuracy || crud,
                Overview: interfaceDefinition,
                Crud: crud,
                Logic: accuracy || readability || exception);

            // 점수는 모두 기준을 넘겼는데 결함이 지적된 경로가 있다.
            // 어느 섹션인지 지역화할 근거가 없으므로 전부 다시 만든다.
            return scope.Overview || scope.Crud || scope.Logic
                ? scope
                : RegenerationScope.Everything;
        }

        /// <summary>
        /// L1 오류 종류에서 범위를 정한다. L1은 형식 검증이라 구조화 데이터에 영향이
        /// 없으므로 Stage 1은 언제나 건너뛴다.
        ///
        /// HeaderMissing 메시지에서 헤더 이름을 파싱해 섹션을 특정하지 않는다 —
        /// 산문 추측을 없애자는 것이 이 클래스의 취지이므로 자기모순이 된다.
        /// </summary>
        public static RegenerationScope FromL1Errors(IReadOnlyList<DetailedError> errors)
        {
            if (errors == null || errors.Count == 0)
            {
                return RegenerationScope.Everything;
            }

            bool allMermaid = errors.All(e =>
                e.Type == ErrorType.MermaidQuoteMissing || e.Type == ErrorType.MermaidCliError);

            return allMermaid
                ? new RegenerationScope(RunStage1: false, Overview: false, Crud: false, Logic: true)
                : new RegenerationScope(RunStage1: false, Overview: true, Crud: true, Logic: true);
        }
    }
}
```

- [ ] **Step 4: 통과 확인**

실행: `dotnet test tests/ReSet.Core.Tests`

기대: 전체 통과(647건 — 637 + 신규 10건).

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/RegenerationScope.cs tests/ReSet.Core.Tests/RegenerationScopeSelectorTests.cs
git commit -m "feat: compute regeneration scope from scores instead of prose keywords"
```

---

## Task 4: 계산기를 오케스트레이터에 배선

키워드 매칭 두 블록을 `RegenerationScope` 읽기로 교체한다. **순차 SP 루프의 지역 모델 경로만 바뀐다** — 배치는 청킹 경로가 없고, API·CLI 제공자는 이 블록에 진입하지 않는다.

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`

**Interfaces:**
- Consumes: Task 3의 `RegenerationScope`, `RegenerationScope.Everything`, `RegenerationScopeSelector.FromReview`, `RegenerationScopeSelector.FromL1Errors`
- Produces: 없음

이 태스크에는 새 테스트가 없다. 규칙은 Task 3에서 못박혔고, 여기서 바뀌는 것은 배선뿐이다. 지역 모델 경로는 NSubstitute 하네스가 진입하지 않아(테스트의 provider가 `"OpenAI"`) 단위 테스트로 도달할 수 없다 — 설계 문서의 "검증의 한계"에 기록된 사항이다. 기존 635+건이 그대로 통과하는 것이 회귀 안전망이다.

- [ ] **Step 1: 초록 기준선 확인**

실행: `dotnet test tests/ReSet.Core.Tests`

기대: 647건 통과. 배선 전후로 이 결과가 같아야 한다.

- [ ] **Step 2: 상태 변수 선언**

순차 루프의 `string? feedbackLog = null;`(`:290` 부근, 바로 위에 `var feedbackHistory = ...`가 있다)을 찾아 **그 아래**에 한 줄을 넣는다.

```csharp
            // 재생성 범위는 feedbackLog와 같은 자리에서 정해진다. 둘 다 "직전 회차가
            // 무엇을 지적당했나"를 표현하지만, 이쪽은 산문이 아니라 구조화된 값이다.
            RegenerationScope? regenScope = null;
```

- [ ] **Step 3: L1 실패 재시도 자리에 배선**

`feedbackLog = CriticFeedbackLog.ComposeAfterL1Failure(l1Result.SuggestedPromptFix, feedbackHistory);`를 찾는다. **두 곳 있다** — 순차(들여쓰기 28칸)와 배치(24칸). 순차 쪽만 바꾼다. 그 줄 **바로 아래**에 넣는다.

```csharp
                            regenScope = RegenerationScopeSelector.FromL1Errors(l1Result.DetailedErrors);
```

- [ ] **Step 4: L2 결함 재시도 자리에 배선**

순차 루프의 `CriticFeedbackLog.Record(feedbackHistory, attempt, l2Result, _criticScoreThreshold);`를 찾는다(들여쓰기 28칸). 그 줄 **바로 위**에 넣는다.

```csharp
                            regenScope = RegenerationScopeSelector.FromReview(l2Result, _criticScoreThreshold);
```

- [ ] **Step 5: Stage 1 판단 교체**

`bool shouldRunStage1 = true;`로 시작해 그 아래 주석 5줄과 `if (attempt > 1 && !string.IsNullOrEmpty(feedbackLog))` 블록(`isLogicError` 계산과 `shouldRunStage1 = false` 대입을 포함)까지를 통째로 아래로 교체한다. 구제 수정 웨이브에서 붙인 오염 경고 주석도 여기서 함께 사라진다 — 그 주석이 경고하던 상황 자체가 없어지기 때문이다.

```csharp
                            var scope = regenScope ?? RegenerationScope.Everything;
                            bool shouldRunStage1 = scope.RunStage1;
                            if (!shouldRunStage1)
                            {
                                Log.Information("[파이프라인] 재생성 범위가 표현 계층에 한정되어 1단계(추론)를 건너뛰고 기존 구조화 데이터 재사용");
                            }
```

- [ ] **Step 6: 섹션 선택 교체**

`bool regenPart1 = true;` / `bool regenPart2 = true;` / `bool regenPart3 = true;` 세 줄부터, 그 아래 `if (attempt > 1 && !string.IsNullOrEmpty(feedbackLog)) { ... }` 블록 전체(`logUpper` 계산, 세 `if` 조건, 두 폴백 포함)까지를 아래로 교체한다.

```csharp
                                bool regenPart1 = scope.Overview;
                                bool regenPart2 = scope.Crud;
                                bool regenPart3 = scope.Logic;

                                // 이전 결과 누락 시 전체 재생성. 이 조건만 호출부의 지역
                                // 상태에 달려 있어 RegenerationScopeSelector가 알 수 없다.
                                if (ollamaPart1 == null || ollamaPart2 == null || ollamaPart3 == null)
                                {
                                    regenPart1 = regenPart2 = regenPart3 = true;
                                }
```

`scope`는 Step 5에서 선언한 것을 그대로 쓴다 — 두 블록은 같은 `if (IsLocalProvider...)` 안에 있다. 세 플래그가 모두 false로 남는 경우는 `RegenerationScopeSelector`가 이미 `Everything`으로 막으므로, 옛 코드의 "전부 false면 전체 재생성" 폴백은 옮기지 않는다.

- [ ] **Step 7: 통과 확인**

```bash
dotnet build ReSet.slnx
dotnet test tests/ReSet.Core.Tests
```

기대: 빌드 성공, 647건 통과, 새 경고 없음. `logUpper`가 더 이상 쓰이지 않으므로 미사용 변수 경고가 뜨면 남은 선언을 지운다.

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs
git commit -m "refactor: drive local-model regeneration from the structured scope"
```

---

## Task 5: 문서 동기화

**Files:**
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: Task 1~4의 결과
- Produces: 없음

- [ ] **Step 1: BestAttempt 항목 갱신**

`AGENTS.md`의 `BestAttempt.cs` 항목 전체를 아래로 교체한다.

```markdown
    *   [BestAttempt.cs](./src/ReSet.Core/Services/BestAttempt.cs): 재시도 루프가 만든 후보 중 `NormalizedScore`가 가장 높은 하나를 `AttemptCandidate` 레코드로 보관하는 클래스. 갱신 규칙(엄격 부등호 — 동점이면 먼저 나온 시도 유지)을 단독 소유합니다. 순차 SP 루프와 배치 계획 루프가 같은 규칙을 쓰도록 이곳에서만 비교하십시오. 재시도 소진 시 **마지막이 아니라 최고점**을 채택하는 것이 이 클래스의 존재 이유입니다. 후보를 네 값이 흩어진 프로퍼티가 아니라 **레코드 하나**로 드는 이유는 두 가지입니다 — "후보가 있는가"를 물을 곳이 `Current != null` 하나가 되고, 채택된 시도를 가리키는 값 중 하나만 갱신되어 어긋날 자리가 없어집니다. `Generation`(AiResult)이 nullable인 것은 순차 루프가 `accumulatedThinking`으로 모든 시도를 이미 기록해 채택본 하나를 가리킬 필요가 없기 때문이며, 단일 `AiResult`를 스냅샷하는 배치 루프만 실제 값을 넘깁니다.
```

- [ ] **Step 2: RetryRescue 항목에 Generation 추가**

`AGENTS.md`의 `RetryRescue.cs` 항목 끝에 아래 문장을 덧붙인다.

```markdown
`RescuedAttempt`는 채택본의 `AiResult`도 함께 싣습니다 — 배치 경로의 `Thinking.md`와 `raw/prompt-context.md`가 채택본이 아닌 시도를 서술하던 문제를 막습니다.
```

- [ ] **Step 3: RegenerationScope 항목 추가**

`AGENTS.md`의 `CriticFeedbackLog.cs` 항목 **바로 뒤**에 한 줄을 넣는다.

```markdown
    *   [RegenerationScope.cs](./src/ReSet.Core/Services/RegenerationScope.cs): 지역 모델(`ollama`/`local-openai`/`mlx`/`vllm`) 경로에서 이번 회차에 다시 만들 섹션과 Stage 1 재실행 여부를 정하는 계산기. **Critic의 항목별 점수와 `MechanicalValidator`의 `ErrorType`에서 계산하며, 피드백 산문에 키워드를 매칭하지 마십시오** — 이전 구현이 그랬고, `CriticFeedbackLog`의 점수 줄이 항상 `CRUD`라는 글자를 포함해 모든 재시도 회차에서 CRUD 섹션이 무조건 재생성됐습니다. 프롬프트 문구가 바뀌어도 조용히 깨지지 않는 것이 이 클래스의 존재 이유입니다.
```

- [ ] **Step 4: 최종 검증**

```bash
dotnet build ReSet.slnx
dotnet test tests/ReSet.Core.Tests
```

기대: 빌드 성공, 647건 통과.

- [ ] **Step 5: 커밋**

```bash
git add AGENTS.md
git commit -m "docs: record the single-candidate and regeneration-scope contracts"
```

---

## Self-Review

**스펙 커버리지**

| 스펙 항목 | 구현 태스크 |
|---|---|
| 구성요소 A — 단일 후보 레코드 | Task 1 |
| 구성요소 A — `RetryRescue` 가드 한 줄 | Task 1 Step 4 |
| 구성요소 A — `Generation` 추가와 배치 반영 | Task 2 |
| 구성요소 B — `RegenerationScope` / `Selector` | Task 3 |
| 구성요소 B — 점수→섹션 대응표 | Task 3 Step 3의 `FromReview` |
| 구성요소 B — L1 오류→섹션 | Task 3 Step 3의 `FromL1Errors` |
| 구성요소 B — 폴백 셋 | `FromReview`의 미달 없음 폴백, `FromL1Errors`의 빈 목록 폴백, Task 4 Step 6의 이전 산출물 누락 폴백 |
| 구성요소 B — 배선 | Task 4 |
| 테스트: `BestAttemptTests` | Task 1 Step 1 |
| 테스트: `RetryRescueTests` | 기존 3건이 Task 1·2에서 그대로 통과해야 함(Task 1 Step 5, Task 2 Step 7) |
| 테스트: `RegenerationScopeSelectorTests` | Task 3 Step 1 |
| 테스트: 문제 2의 결정적 증거 | Task 2 Step 1 |
| 문서 동기화 | Task 5 |

**의도적으로 넣지 않은 것**

`RetryRescueTests`를 다시 쓰지 않는다. 그 3건은 `RetryRescue`의 **공개 계약**(후보 없으면 null, 배너가 이미 붙어 나옴, 정상 소진엔 구제 줄 없음)만 검증하며 `BestAttempt`의 내부 표현에 의존하지 않는다. Task 1·2에서 그대로 통과하는 것이 리팩터링이 계약을 지켰다는 증거이므로, 손대면 안전망을 스스로 없애는 셈이다. Task 2에서 `RescuedAttempt`에 인자가 하나 늘지만 테스트는 프로퍼티만 읽으므로 컴파일에 영향이 없다.

**타입 일관성**

`AttemptCandidate`는 Task 1에서 3-인자로 정의되고 Task 2에서 `Generation`이 붙어 4-인자가 된다. `TryRecord`도 Task 1의 3-인자에서 Task 2의 4-인자로 바뀐다 — 두 태스크가 같은 파일을 연속으로 고치므로 중간 상태가 커밋되며, 각 커밋 시점의 빌드는 성립한다. `RescuedAttempt`는 Task 2에서만 바뀐다. `RegenerationScope(bool RunStage1, bool Overview, bool Crud, bool Logic)`와 `Everything`은 Task 3에서 정의되고 Task 4에서 같은 이름으로 쓰인다. 불일치 없음.

**테스트 수 추적**

착수 635 → Task 1 후 636 → Task 2 후 637 → Task 3 후 647 → Task 4·5 변화 없음.

**범위**

단일 구현 계획으로 적정하다. 구성요소 A와 B는 서로 독립이지만 둘 다 작고, 같은 파일(`VerificationPipelineOrchestrator.cs`)을 만지므로 순서를 한 계획에서 통제하는 편이 낫다.
