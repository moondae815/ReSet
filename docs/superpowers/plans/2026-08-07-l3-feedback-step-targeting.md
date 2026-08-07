# L3 피드백 단계 지목 재생성 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** L3 승인 화면의 사용자 피드백이 문서 전체를 통짜로 다시 만들지 않고, 사용자가 고른 단계만 분할 경로로 재생성하게 한다.

**Architecture:** 재시도 루프가 이미 쓰는 `GenerateBySplitAsync`를 L3 피드백 경로에서도 호출한다. 대상 단계는 모델이 산문에서 추론하지 않고 사용자가 다중 선택으로 고른다. 캐시된 골격·섹션을 재사용하려면 그것들이 화면에 보이는(채택된) 회차의 것이어야 하므로, 회차와 함께 움직여야 하는 다섯 값을 레코드 하나로 묶어 구제 채택 시 통째로 되돌린다.

**Tech Stack:** C# / .NET 10, xUnit, NSubstitute, Spectre.Console

**Spec:** `docs/superpowers/specs/2026-08-07-l3-feedback-step-targeting-design.md`

## Global Constraints

- 대상 프레임워크 `net10.0`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`.
- 코드 주석과 사용자 노출 문자열은 **한국어**. AI 프롬프트 본문은 영문(AGENTS.md 하이브리드 규칙) — 이 계획에는 새 프롬프트가 없다.
- **새 설정 키를 추가하지 않는다.**
- 취소 가능한 `await`를 감싸는 모든 `catch`에 `when (ex is not OperationCanceledException)` 필터를 단다. `CancellationPolicyTests`가 Roslyn 구문 트리로 자동 검사한다.
- `VerificationOutcome`에 값을 추가하지 않는다. L3 피드백 반영본은 `ReviewNotRun`으로 끝난다.
- L2를 재실행하지 않는다. `planReview = null`을 유지한다.
- 착수 시점 실측값: `dotnet clean && dotnet build` 경고 **8건**(`DbMetadataServiceTests`의 CS8600/CS8602), `dotnet test` **746건** 통과.
- 단일 SP 명세서 경로(`RequestHumanReviewAsync`를 `structureRedraftSupported: false`로 호출하는 쪽)의 동작을 바꾸지 않는다.

---

## File Structure

**수정 파일**

| 파일 | 변경 |
|---|---|
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | `AdoptedGenerationState` 레코드, 배너 재부착 추출, L3 분할 배선 |
| `src/ReSet.Core/Models/HumanReviewResult.cs` | `TargetStepCodes`, `RegenerateSkeleton` |
| `src/ReSet.Core/Services/IVerificationUserInteraction.cs` | `RequestHumanReviewAsync`에 `steps` 선택적 매개변수 |
| `src/ReSet.Cli/ConsoleUserInteraction.cs` | 다중 선택 프롬프트 + 선택 결과 매핑 |
| `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` | 채택 정합·L3 배선 테스트 |
| `tests/ReSet.Core.Tests/ConsoleUserInteractionTests.cs` (신규) | 선택 결과 매핑 단위 테스트 |
| `README.md`, `AGENTS.md`, `docs/architecture.md` | 문서 동기화 |

---

## Task 1: 채택 회차 상태를 레코드 하나로 묶기

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` — 지역 변수 선언부(1675·1682행), 스냅샷 지점(1911-1912행), 구제 채택 4곳(1823-1832, 1860-1868, 1987-1991, 2014-2022행), 새 private 멤버
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: 없음 (기존 코드 재구성)
- Produces:
  - `private sealed record AdoptedGenerationState(string PlanStructure, string? Skeleton, AiResult? SkeletonResult, IReadOnlyDictionary<string, string>? StepSections, IReadOnlyDictionary<string, string> FloorViolations)`
  - `private static void RestoreAdoptedGenerationState(AdoptedGenerationState adopted, out string? skeleton, out AiResult? skeletonResult, out Dictionary<string, string>? stepSections, out Dictionary<string, string> floorViolations)`
  - 지역 변수 `AdoptedGenerationState adoptedState` — Task 4가 L3에서 읽는다

### 배경 (구현자가 알아야 할 것)

재시도 루프를 빠져나온 시점에 다섯 값이 서로 다른 회차를 가리킬 수 있다. `consolidatedPlan`·`currentPlanStructure`·`stepFloorViolations`는 구제 채택 시 채택 회차로 되돌아가지만, `lastSkeleton`·`lastSkeletonResult`·`lastStepSections`는 **마지막으로 생성된 회차**를 그대로 가리킨다.

지금은 L3가 그 값들을 쓰지 않아 잠복 상태다. Task 4가 그것을 깨우므로 여기서 먼저 닫는다.

- [ ] **Step 1: 실패 테스트 작성**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` 파일 말미(마지막 `}` 두 개 앞)에 추가:

```csharp
        // 구제 채택은 문서·목차·하한 위반을 채택 회차로 되돌리지만, 캐시된
        // 골격과 단계 섹션은 되돌리지 않았다. L3가 그 캐시를 재사용하기 시작하면
        // 화면의 문서가 아니라 폐기된 회차의 섹션 위에 피드백이 얹힌다.
        [Fact]
        public async Task RunConsolidatedPipeline_WhenRescueAdoptsEarlierAttempt_RewindsCachedSkeletonAndSections()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });

            // 1차 골격과 3차 골격을 구분 가능하게 만든다.
            var skeletonCall = 0;
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => new AiResult
                {
                    Content = SkeletonMarkdown,
                    SystemPrompt = $"골격 시스템 프롬프트 #{++skeletonCall}"
                });

            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            // 1차는 통과 점수, 2차는 더 낮은 점수(재수립 유발), 3차는 결함 →
            // 예산 소진 시 RetryRescue가 최고점인 1차를 채택한다.
            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => ++reviewCall switch
                {
                    1 => new ReviewResult { HasDefects = true, FeedbackComment = "보완", ScoreAccuracy = 7, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 },
                    _ => new ReviewResult { HasDefects = true, FeedbackComment = "여전히 보완", ScoreAccuracy = 6, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 }
                });

            var result = await RunBatchPipeline(aiService);

            // 채택된 것은 1차이므로 finalAiResult도 1차 골격의 것이어야 한다.
            Assert.NotNull(result.Result);
            Assert.Equal("골격 시스템 프롬프트 #1", result.Result!.SystemPrompt);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RewindsCachedSkeletonAndSections"`
Expected: FAIL — `Assert.Equal() Failure`, 마지막 회차 골격의 프롬프트가 들어 있음

- [ ] **Step 3: 레코드와 복원 헬퍼 추가**

`VerificationPipelineOrchestrator.cs`의 `ClearSplitGenerationCacheAfterRedraft` 정의(2325행 부근) **바로 앞**에 추가:

```csharp
        /// <summary>
        /// 채택 후보(BestAttempt.Current)를 실제로 만들어 낸 상태 일체.
        /// 후보가 교체되는 그 자리에서 통째로 붙잡고, 구제 채택 시 통째로 되돌린다.
        ///
        /// 다섯 값을 개별 변수로 두면 "함께 움직여야 한다"가 규율이 되고, 규율은
        /// 깨진다 — 이 파이프라인에서 이미 세 번 깨졌다. 레코드로 묶으면 구조가 된다.
        ///
        /// 유지보수 불변식: 채택 문서를 이전 회차로 되돌리는 종료 경로를 새로
        /// 추가한다면 반드시 이 레코드를 통째로 되돌려야 한다. 개별 필드만 되돌리는
        /// 코드를 쓰지 말 것 — 그러려고 묶었다.
        /// </summary>
        private sealed record AdoptedGenerationState(
            string PlanStructure,
            string? Skeleton,
            AiResult? SkeletonResult,
            IReadOnlyDictionary<string, string>? StepSections,
            IReadOnlyDictionary<string, string> FloorViolations);

        /// <summary>
        /// 채택 상태를 살아있는 지역 변수들로 되돌린다. 사전은 복사해서 넘긴다 —
        /// 스냅샷을 그대로 참조시키면 이후 변형이 스냅샷을 오염시킨다.
        /// </summary>
        private static void RestoreAdoptedGenerationState(
            AdoptedGenerationState adopted,
            out string? skeleton,
            out AiResult? skeletonResult,
            out Dictionary<string, string>? stepSections,
            out Dictionary<string, string> floorViolations)
        {
            skeleton = adopted.Skeleton;
            skeletonResult = adopted.SkeletonResult;
            stepSections = adopted.StepSections == null
                ? null
                : new Dictionary<string, string>(adopted.StepSections);
            floorViolations = new Dictionary<string, string>(adopted.FloorViolations);
        }
```

- [ ] **Step 4: 지역 변수 교체**

1675행의 `string bestAttemptStructure = string.Empty;`부터 1682행의 `var bestAttemptStepFloorViolations = new Dictionary<string, string>();`까지(그 사이 주석 포함)를 아래로 교체:

```csharp
            // 최고점 후보(BestAttempt.Current)를 실제로 만들어 낸 상태 일체.
            // 목차·골격·골격 AiResult·단계 섹션·하한 위반이 한 몸으로 움직인다.
            //
            // 목차가 어긋나면 산출된 문서를 한 번도 만든 적 없는 목차가 기록으로
            // 남고, 하한 위반이 어긋나면 배너가 과다·과소 보고하며, 골격과 섹션이
            // 어긋나면 L3 지목 재생성이 화면의 문서가 아닌 폐기된 회차 위에 얹힌다.
            // 셋 다 실제로 발생했던 결함이라 개별 변수로 두지 않는다.
            var adoptedState = new AdoptedGenerationState(
                string.Empty, null, null, null, new Dictionary<string, string>());
```

- [ ] **Step 5: 스냅샷 지점 교체**

1911-1912행의 두 대입문을 아래로 교체(앞의 주석도 함께):

```csharp
                        // 후보가 교체되는 바로 그 자리에서 그 후보를 만든 상태를
                        // 통째로 붙잡는다. 다른 곳에서 갱신하면 어긋나는 순간이 생긴다.
                        adoptedState = new AdoptedGenerationState(
                            currentPlanStructure,
                            lastSkeleton,
                            lastSkeletonResult,
                            lastStepSections == null ? null : new Dictionary<string, string>(lastStepSections),
                            new Dictionary<string, string>(stepFloorViolations));
```

- [ ] **Step 6: 구제 채택 4곳 교체**

네 곳 모두 같은 형태다. 각 지점에서 `bestAttemptStructure`를 `adoptedState.PlanStructure`로 바꾸고, `stepFloorViolations = bestAttemptStepFloorViolations;` 줄을 복원 호출로 바꾼다.

```csharp
                    currentPlanStructure = await AdoptPlanStructureForRescueAsync(
                        outputRoot, jobName, currentPlanStructure, adoptedState.PlanStructure, cancellationToken);
                    RestoreAdoptedGenerationState(
                        adoptedState, out lastSkeleton, out lastSkeletonResult, out lastStepSections, out stepFloorViolations);
```

해당 지점: 1823·1860·1987·2014행의 `AdoptPlanStructureForRescueAsync` 호출과, 각각에 대응하는 1832·1868·1991·2022행의 `stepFloorViolations = bestAttemptStepFloorViolations;`.

**주의**: 1832·1868·1991·2022행의 대입은 `AdoptPlanStructureForRescueAsync` 호출과 몇 줄 떨어져 있다. 복원 호출은 `AdoptPlanStructureForRescueAsync` 바로 뒤에 두고, 기존 `stepFloorViolations = bestAttemptStepFloorViolations;` 줄은 지운다.

- [ ] **Step 7: 테스트 통과 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"`
Expected: PASS (기존 전부 + 신규 1건). `bestAttemptStructure`·`bestAttemptStepFloorViolations` 참조가 남아 있으면 컴파일 에러로 드러난다.

- [ ] **Step 8: 커밋**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CancellationPolicyTests"
dotnet test
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "fix: rewind the whole adopted generation state, not three fifths of it"
```

---

## Task 2: 배너 재부착 로직을 한 메서드로 추출

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` — 2050~2120행 부근의 배너·커버리지 블록
- Test: 기존 테스트가 그대로 통과하는 것이 검증이다 (순수 리팩터링)

**Interfaces:**
- Consumes: Task 1의 변경과 무관 (독립)
- Produces: `private string AttachPipelineBanners(string consolidatedPlan, IReadOnlyDictionary<string, string> stepFloorViolations, string currentPlanStructure, List<(string FileName, string Content)> specs, string jobName)` — 배너가 붙은 마크다운을 돌려준다. Task 4가 L3에서 같은 메서드를 호출한다

### 왜 추출하는가

L3 피드백 재생성 후에도 하한 미달 배너와 커버리지 배너를 다시 붙여야 한다. 같은 로직을 두 벌로 두면 한쪽만 고쳐지는 날이 온다.

- [ ] **Step 1: 현재 동작을 고정하는 테스트가 이미 있는지 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~PrependsWarningBanner|FullyQualifiedName~DoesNotPrependWarningBanner|FullyQualifiedName~Uncovered"`
Expected: PASS. 이 테스트들이 추출 전후의 동작 동일성을 보증한다. 하나라도 없으면 추출을 멈추고 보고하라 — 안전망 없이 리팩터링하지 않는다.

- [ ] **Step 2: 메서드 추출**

2050행 부근의 `var stepFloorViolationMessages = ...`부터 커버리지 배너 부착이 끝나는 지점까지(주석 전부 포함)를 잘라내어, `RunConsolidatedPipelineAsync` **뒤**에 private 메서드로 옮긴다.

```csharp
        /// <summary>
        /// 파이프라인이 문서를 사용자에게 건네기 직전에 붙는 배너를 모두 부착한다.
        ///
        /// 재시도 루프 종료 직후와 L3 피드백 재생성 직후, 두 자리에서 호출된다.
        /// 두 벌로 두면 한쪽만 고쳐지는 날이 온다.
        /// </summary>
        private string AttachPipelineBanners(
            string consolidatedPlan,
            IReadOnlyDictionary<string, string> stepFloorViolations,
            string currentPlanStructure,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string jobName)
        {
            // ... 잘라낸 본문을 한 글자도 바꾸지 않고 그대로 ...
            // 단, 마지막에 consolidatedPlan을 return 한다.
            return consolidatedPlan;
        }
```

원래 자리에는 호출만 남긴다.

```csharp
            consolidatedPlan = AttachPipelineBanners(
                consolidatedPlan, stepFloorViolations, currentPlanStructure, specs, jobName);
```

**주의**: 잘라낸 블록 안의 긴 주석들(특히 커버리지 재계산의 유지보수 불변식 주석)을 반드시 함께 옮긴다. 그 주석들이 이 코드가 왜 이 모양인지에 대한 유일한 기록이다.

- [ ] **Step 3: 테스트 통과 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"`
Expected: PASS, 개수 변화 없음. 하나라도 깨지면 추출이 동작을 바꾼 것이다.

- [ ] **Step 4: 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs
git commit -m "refactor: give banner attachment one home before L3 needs it too"
```

---

## Task 3: 사용자가 대상 단계를 고르는 UI

**Files:**
- Modify: `src/ReSet.Core/Models/HumanReviewResult.cs`
- Modify: `src/ReSet.Core/Services/IVerificationUserInteraction.cs:37-41`
- Modify: `src/ReSet.Cli/ConsoleUserInteraction.cs:98-102`(시그니처), `:169-183`(피드백 블록)
- Create: `tests/ReSet.Core.Tests/ConsoleUserInteractionTests.cs`

**Interfaces:**
- Consumes: `BatchStepPlan` (기존, `ReSet.Core.Services`)
- Produces:
  - `HumanReviewResult.TargetStepCodes` (`List<string>`, 기본 빈 목록)
  - `HumanReviewResult.RegenerateSkeleton` (`bool`)
  - `RequestHumanReviewAsync(..., bool structureRedraftSupported = false, IReadOnlyList<BatchStepPlan>? steps = null)`
  - `public static (List<string> TargetStepCodes, bool RegenerateSkeleton) MapStepSelection(IReadOnlyList<string> selectedLabels, IReadOnlyList<BatchStepPlan> steps)` — `ConsoleUserInteraction`의 public static 헬퍼
  - `public const string SkeletonSelectionLabel = "(골격) 개요 · Mermaid 흐름도 · 검증 SQL 세트";`

### 설계 규칙 (그대로 구현할 것)

- 골격 항목을 고르면 `RegenerateSkeleton = true`이고, 그 항목은 `TargetStepCodes`에 넣지 않는다 — 단계 코드가 아니다
- **골격을 고르면 단계 선택과 무관하게 전 단계를 재생성한다.** 공통 규약이 골격에 있고 모든 단계 섹션이 그것을 전제로 쓰였으므로, 규약이 바뀌면 인용한 섹션도 다시 써야 한다. 구현에서는 골격 선택 시 `TargetStepCodes`를 **비운다**(= 전체)
- 아무것도 고르지 않아도 전체 재생성이다. "미선택"과 "전체"를 같은 뜻으로 둔다

- [ ] **Step 1: 실패 테스트 작성**

`tests/ReSet.Core.Tests/ConsoleUserInteractionTests.cs` 신규 생성:

```csharp
using System.Collections.Generic;
using ReSet.Cli;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ConsoleUserInteractionTests
    {
        private static IReadOnlyList<BatchStepPlan> ThreeSteps() => new[]
        {
            new BatchStepPlan("S01", "수수료율 스냅샷", new[] { "UP_A" }, new[] { "dbo.T1" }, new[] { "-1" }, false),
            new BatchStepPlan("S02", "정산 원장 생성", new[] { "UP_B" }, new[] { "dbo.T2" }, new[] { "-2" }, false),
            new BatchStepPlan("S03", "취소 원장 반영", new[] { "UP_C" }, new[] { "dbo.T3" }, new[] { "-3" }, false)
        };

        private static string LabelOf(BatchStepPlan step) => $"{step.Code}  {step.Name}";

        [Fact]
        public void MapStepSelection_WithNoSelection_MeansFullRegeneration()
        {
            var (codes, skeleton) = ConsoleUserInteraction.MapStepSelection(new string[0], ThreeSteps());

            Assert.Empty(codes);
            Assert.False(skeleton);
        }

        [Fact]
        public void MapStepSelection_WithSomeSteps_ReturnsOnlyThoseCodes()
        {
            var steps = ThreeSteps();
            var (codes, skeleton) = ConsoleUserInteraction.MapStepSelection(
                new[] { LabelOf(steps[0]), LabelOf(steps[2]) }, steps);

            Assert.Equal(new[] { "S01", "S03" }, codes);
            Assert.False(skeleton);
        }

        // 골격의 공통 규약이 바뀌면 그것을 인용한 모든 섹션이 낡는다.
        // 그래서 골격 선택은 단계 선택을 덮어써 전체 재생성이 된다.
        [Fact]
        public void MapStepSelection_WithSkeleton_ForcesFullRegenerationRegardlessOfSteps()
        {
            var steps = ThreeSteps();
            var (codes, skeleton) = ConsoleUserInteraction.MapStepSelection(
                new[] { ConsoleUserInteraction.SkeletonSelectionLabel, LabelOf(steps[1]) }, steps);

            Assert.True(skeleton);
            Assert.Empty(codes);
        }

        [Fact]
        public void MapStepSelection_WithSkeletonOnly_ForcesFullRegeneration()
        {
            var (codes, skeleton) = ConsoleUserInteraction.MapStepSelection(
                new[] { ConsoleUserInteraction.SkeletonSelectionLabel }, ThreeSteps());

            Assert.True(skeleton);
            Assert.Empty(codes);
        }

        // 라벨이 목록에 없으면 조용히 무시한다. 프롬프트가 돌려주는 값만
        // 들어오므로 발생하지 않지만, 매핑이 예외를 던지면 승인 화면이 죽는다.
        [Fact]
        public void MapStepSelection_WithUnknownLabel_IgnoresIt()
        {
            var (codes, skeleton) = ConsoleUserInteraction.MapStepSelection(
                new[] { "존재하지 않는 라벨" }, ThreeSteps());

            Assert.Empty(codes);
            Assert.False(skeleton);
        }

        [Fact]
        public void HumanReviewResult_DefaultsToFullRegeneration()
        {
            var result = new ReSet.Core.Models.HumanReviewResult();

            Assert.NotNull(result.TargetStepCodes);
            Assert.Empty(result.TargetStepCodes);
            Assert.False(result.RegenerateSkeleton);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~ConsoleUserInteractionTests"`
Expected: 컴파일 실패 — `MapStepSelection`·`SkeletonSelectionLabel`·`TargetStepCodes`가 없음

- [ ] **Step 3: `HumanReviewResult` 확장**

`src/ReSet.Core/Models/HumanReviewResult.cs`의 `RedraftStructure` 뒤에 추가:

```csharp
        /// <summary>
        /// 사용자가 지목한 재생성 대상 단계 코드. Decision이 ProvideFeedback이고
        /// RedraftStructure가 false일 때만 의미가 있다.
        ///
        /// 비어 있으면 전체 재생성이다 — "아무것도 안 고름"과 "전체"를 같은 뜻으로
        /// 둔다. 골격을 고른 경우에도 비운다(RegenerateSkeleton 주석 참조).
        /// </summary>
        public List<string> TargetStepCodes { get; set; } = new();

        /// <summary>
        /// 골격(개요·Mermaid 흐름도·검증 SQL 세트)도 다시 만들지 여부.
        ///
        /// 공통 규약이 골격에 있고 모든 단계 섹션이 그것을 전제로 쓰였으므로,
        /// 이 값이 true면 TargetStepCodes는 비어야 한다 — 규약이 바뀌면 그것을
        /// 인용한 섹션도 전부 다시 써야 한다.
        /// </summary>
        public bool RegenerateSkeleton { get; set; }
```

파일 상단에 `using System.Collections.Generic;`이 없으면 추가한다.

- [ ] **Step 4: 인터페이스에 매개변수 추가**

`src/ReSet.Core/Services/IVerificationUserInteraction.cs:37-41`을 교체:

```csharp
        Task<HumanReviewResult> RequestHumanReviewAsync(
            string selectedOption,
            string specificationMarkdown,
            VerificationOutcome outcome,
            bool structureRedraftSupported = false,
            IReadOnlyList<BatchStepPlan>? steps = null);
```

파일 상단에 `using System.Collections.Generic;`이 없으면 추가한다.

- [ ] **Step 5: `ConsoleUserInteraction` 구현**

`src/ReSet.Cli/ConsoleUserInteraction.cs:98-102`의 시그니처를 인터페이스와 동일하게 맞춘다.

클래스에 public 멤버 두 개를 추가한다 (`RequestHumanReviewAsync` 앞):

```csharp
        /// <summary>
        /// 단계 선택 목록에서 골격을 가리키는 항목. 매핑과 프롬프트가 같은
        /// 문자열을 써야 하므로 상수로 둔다.
        /// </summary>
        public const string SkeletonSelectionLabel = "(골격) 개요 · Mermaid 흐름도 · 검증 SQL 세트";

        /// <summary>
        /// 다중 선택 결과를 재생성 대상으로 옮긴다.
        ///
        /// 프롬프트에서 분리한 이유: AnsiConsole은 단위 테스트에서 구동하기 어렵고,
        /// 정작 틀리기 쉬운 것은 프롬프트가 아니라 이 매핑 규칙이다.
        /// </summary>
        public static (List<string> TargetStepCodes, bool RegenerateSkeleton) MapStepSelection(
            IReadOnlyList<string> selectedLabels,
            IReadOnlyList<BatchStepPlan> steps)
        {
            var regenerateSkeleton = selectedLabels.Contains(SkeletonSelectionLabel);

            // 골격을 고르면 공통 규약이 바뀌므로 그것을 인용한 섹션이 전부 낡는다.
            // 단계를 함께 골랐더라도 전체 재생성으로 승격한다.
            if (regenerateSkeleton)
            {
                return (new List<string>(), true);
            }

            var codes = steps
                .Where(step => selectedLabels.Contains(StepSelectionLabel(step)))
                .Select(step => step.Code)
                .ToList();

            return (codes, false);
        }

        private static string StepSelectionLabel(BatchStepPlan step) => $"{step.Code}  {step.Name}";
```

`RequestHumanReviewAsync`의 `redraftStructure` 계산(173-174행) **바로 뒤**에 단계 선택을 추가한다:

```csharp
            // 구조가 바뀌면 단계 목록 자체가 바뀌므로 지금 고른 단계는 의미가 없다.
            // 답을 쓸 곳이 있을 때만 묻는다 — 위 구조 질문과 같은 원칙이다.
            var targetStepCodes = new List<string>();
            var regenerateSkeleton = false;
            if (!redraftStructure && steps is { Count: > 0 })
            {
                var choices = new List<string> { SkeletonSelectionLabel };
                choices.AddRange(steps.Select(StepSelectionLabel));

                var selected = AnsiConsole.Prompt(
                    new MultiSelectionPrompt<string>()
                        .Title("어느 단계에 대한 피드백입니까? [grey](Space로 선택, Enter로 확정, 미선택 시 전체)[/]")
                        .NotRequired()
                        .PageSize(20)
                        .AddChoices(choices));

                (targetStepCodes, regenerateSkeleton) = MapStepSelection(selected, steps);
            }
```

반환문(177-183행)에 두 값을 싣는다:

```csharp
            return new HumanReviewResult
            {
                Decision = UserDecision.ProvideFeedback,
                UserFeedback = userFeedback,
                RedraftStructure = redraftStructure,
                TargetStepCodes = targetStepCodes,
                RegenerateSkeleton = regenerateSkeleton
            };
```

파일 상단 `using`에 `System.Linq`와 `ReSet.Core.Services`가 없으면 추가한다.

- [ ] **Step 6: 다른 구현체 확인**

Run: `grep -rn "RequestHumanReviewAsync" src tests --include=*.cs | grep -v "/bin/\|/obj/"`

`IVerificationUserInteraction`을 구현하는 다른 클래스(예: `ValidationUiProxy`)가 있으면 시그니처를 맞춘다. 선택적 매개변수라 **호출부는 바뀌지 않지만 구현체는 반드시 맞춰야 한다.**

- [ ] **Step 7: 테스트 통과 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~ConsoleUserInteractionTests"`
Expected: PASS (6건)

- [ ] **Step 8: 커밋**

```bash
dotnet clean && dotnet build   # 경고 8건 유지 확인
dotnet test
git add src/ReSet.Core/Models/HumanReviewResult.cs src/ReSet.Core/Services/IVerificationUserInteraction.cs src/ReSet.Cli/ConsoleUserInteraction.cs tests/ReSet.Core.Tests/ConsoleUserInteractionTests.cs
git commit -m "feat: let the reviewer say which steps their feedback is about"
```

---

## Task 4: L3 피드백을 분할 경로로 배선

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` — L3 루프(2128행 부근~), 특히 `RequestHumanReviewAsync` 호출(2130-2131행)과 재생성 블록(2176행 이후)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 1의 `adoptedState`, Task 2의 `AttachPipelineBanners`, Task 3의 `HumanReviewResult.TargetStepCodes`/`RegenerateSkeleton` 및 `steps` 매개변수
- Produces: 없음 (오케스트레이터 내부 배선)

### 동작 명세

| 사용자 답변 | 목차 | 골격 | 단계 섹션 |
|---|---|---|---|
| `RedraftStructure = true` | 재수립 | 재생성 | 전부 |
| `RegenerateSkeleton = true` | 유지 | 재생성 | 전부 |
| `TargetStepCodes` 비어 있음 | 유지 | 재사용 | 전부 |
| `TargetStepCodes` 지목 있음 | 유지 | 재사용 | 지목분만 |

- [ ] **Step 1: 실패 테스트 작성**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` 파일 말미에 추가:

```csharp
        // L3 피드백이 통짜 단일 호출로 가면 분할이 확보한 단계 본문이 무너진다.
        // 지목이 있으면 그 단계만, 골격은 재사용해야 한다.
        [Fact]
        public async Task RunConsolidatedPipeline_L3FeedbackWithTargetedSteps_RegeneratesOnlyThoseSteps()
        {
            var aiService = SplitCapableAiService();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);

            var reviewCount = 0;
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(_ => ++reviewCount == 1
                    ? new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback,
                        UserFeedback = "S02의 트랜잭션 경계를 명시해줘",
                        RedraftStructure = false,
                        TargetStepCodes = new List<string> { "S02" }
                    }
                    : new HumanReviewResult { Decision = UserDecision.Approve });

            var result = await RunBatchPipelineWithUi(aiService, userInteraction, isBatchMode: false);

            Assert.NotNull(result.Plan);
            // 통짜 호출은 한 번도 일어나지 않아야 한다.
            await aiService.DidNotReceive().GenerateConsolidatedBatchPlanAsync(
                Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            // 골격은 최초 1회뿐 — 지목 재생성은 재사용한다.
            await aiService.Received(1).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            // S02는 최초 1회 + 피드백 1회 = 2회, S01은 1회.
            await aiService.Received(2).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S02"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await aiService.Received(1).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S01"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RunConsolidatedPipeline_L3FeedbackWithNoTargets_RegeneratesEveryStepButReusesSkeleton()
        {
            var aiService = SplitCapableAiService();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);

            var reviewCount = 0;
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(_ => ++reviewCount == 1
                    ? new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback,
                        UserFeedback = "전반적으로 트랜잭션 서술을 강화해줘",
                        RedraftStructure = false
                    }
                    : new HumanReviewResult { Decision = UserDecision.Approve });

            await RunBatchPipelineWithUi(aiService, userInteraction, isBatchMode: false);

            await aiService.Received(1).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await aiService.Received(2).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S01"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RunConsolidatedPipeline_L3FeedbackWithSkeletonSelected_RegeneratesSkeletonAndEveryStep()
        {
            var aiService = SplitCapableAiService();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);

            var reviewCount = 0;
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(_ => ++reviewCount == 1
                    ? new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback,
                        UserFeedback = "검증 SQL 세트에 제어합계를 추가해줘",
                        RedraftStructure = false,
                        RegenerateSkeleton = true
                    }
                    : new HumanReviewResult { Decision = UserDecision.Approve });

            await RunBatchPipelineWithUi(aiService, userInteraction, isBatchMode: false);

            await aiService.Received(2).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        // 단계 목록은 사용자에게 전달돼야 고를 수 있다.
        [Fact]
        public async Task RunConsolidatedPipeline_PassesStepListToTheReviewPrompt()
        {
            var aiService = SplitCapableAiService();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(new HumanReviewResult { Decision = UserDecision.Approve });

            await RunBatchPipelineWithUi(aiService, userInteraction, isBatchMode: false);

            await userInteraction.Received(1).RequestHumanReviewAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(),
                Arg.Is<bool>(b => b),
                Arg.Is<IReadOnlyList<BatchStepPlan>?>(s => s != null && s.Count == 2));
        }
```

**도우미 두 개가 필요하다.** 기존 `RunBatchPipeline` 도우미는 `isBatchMode: true`로 고정돼 L3에 닿지 않는다.

- `SplitCapableAiService()` — 기존 테스트들이 반복하는 fake 설정(Brainstorm, Draft with `StepsJson`, Skeleton with `SkeletonMarkdown`, Step with `HealthyStepSection`, Review 통과)을 한 곳으로 뽑은 private 도우미
- `RunBatchPipelineWithUi(IAiService, IVerificationUserInteraction, bool isBatchMode)` — 기존 `RunBatchPipeline`과 같되 `IVerificationUserInteraction`을 인자로 받고 `isBatchMode`를 넘긴다

기존 `RunBatchPipeline`은 건드리지 않는다 — 94개 호출부가 걸려 있다.

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~L3Feedback|FullyQualifiedName~PassesStepListToTheReviewPrompt"`
Expected: FAIL — 통짜 `GenerateConsolidatedBatchPlanAsync`가 호출됨 / `steps` 인자가 null

- [ ] **Step 3: 단계 목록을 승인 화면에 전달**

2130-2131행의 호출을 교체:

```csharp
                // 이 경로에만 다시 세울 목차가 있으므로 구조 변경 질문을 여기서만 허용한다.
                // 단계 목록도 함께 넘긴다 — 사용자가 피드백 대상을 고를 수 있어야 한다.
                // adoptedSteps는 채택된 문서를 만든 목차에서 파싱한 것이다(AttachPipelineBanners
                // 앞에서 계산됨). 살아있는 currentSteps를 쓰면 폐기된 회차의 목록을 보여줄 수 있다.
                var reviewResult = await _userInteraction.RequestHumanReviewAsync(
                    jobName, consolidatedPlan, planOutcome, structureRedraftSupported: true, steps: adoptedSteps);
```

`adoptedSteps`는 Task 2에서 `AttachPipelineBanners` 안으로 옮겨졌다. **L3에서도 필요하므로 메서드 밖으로 다시 끌어낸다** — `AttachPipelineBanners` 호출 앞에서 계산해 인자로 넘기고, 지역 변수로도 남긴다.

```csharp
            var adoptedSteps = BatchStepPlanParser.TryParse(currentPlanStructure);
            consolidatedPlan = AttachPipelineBanners(
                consolidatedPlan, stepFloorViolations, adoptedSteps, specs, jobName);
```

`AttachPipelineBanners`의 시그니처에서 `currentPlanStructure`를 `IReadOnlyList<BatchStepPlan>? adoptedSteps`로 바꾸고, 내부의 `TryParse` 호출을 지운다. **커버리지 재계산의 유지보수 불변식 주석은 호출부로 옮긴다** — 이제 파싱이 그쪽에서 일어나므로.

- [ ] **Step 4: 재생성 블록을 분할 경로로 교체**

2196행 부근의 `var aiResult = await _consolidatorService.GenerateConsolidatedBatchPlanAsync(...)` 블록을 아래로 교체한다.

```csharp
                    // 분할 상태가 있으면 분할로 재생성한다. 통짜 단일 호출은 단계마다
                    // 확보한 본문을 한 번에 무너뜨린다 — 이 경로가 존재하는 이유다.
                    string rePlan = string.Empty;
                    Dictionary<string, string> reViolations = stepFloorViolations;

                    var stepsForRegeneration = BatchStepPlanParser.TryParse(structureForRegeneration);
                    if (stepsForRegeneration != null)
                    {
                        // 구조 재수립·골격 지목이면 골격부터 다시 만든다. 그 외에는
                        // 캐시된 골격을 재사용하되, 지목이 없으면 전 단계를 다시 만든다.
                        var reuseSkeleton =
                            !reviewResult.RedraftStructure &&
                            !reviewResult.RegenerateSkeleton &&
                            lastSkeleton != null &&
                            lastStepSections != null;

                        var split = await GenerateBySplitAsync(
                            structureForRegeneration, stepsForRegeneration, specsCopy, targetLanguage, jobName,
                            progressScopeForL3,
                            reuseSkeleton ? lastSkeleton : null,
                            reuseSkeleton ? lastSkeletonResult : null,
                            reuseSkeleton ? lastStepSections : null,
                            stepFloorViolations,
                            reuseSkeleton ? reviewResult.TargetStepCodes : new List<string>(),
                            cancellationToken);

                        if (split != null)
                        {
                            rePlan = split.Markdown;
                            reViolations = split.FloorViolations;
                            lastSkeleton = split.Skeleton;
                            lastSkeletonResult = split.Generation;
                            lastStepSections = split.Sections;
                            currentSteps = stepsForRegeneration;
                            finalAiResult = split.Generation;
                        }
                    }
                    else
                    {
                        // 목차가 단계 목록을 못 냈다. 분할 자체가 불가능하므로
                        // 기존 단일 호출로 간다 — 이 경로의 문서는 애초에 분할로
                        // 만들어지지 않았다.
                        try
                        {
                            var aiResult = await _consolidatorService.GenerateConsolidatedBatchPlanAsync(
                                structureForRegeneration, specsCopy, targetLanguage, jobName, _consolidatorEffort, cancellationToken);
                            rePlan = aiResult.Content;
                            finalAiResult = aiResult;
                        }
                        // 취소는 전파한다. 삼키면 아래 continue가 돌아 취소한
                        // 사용자에게 같은 승인 화면을 한 번 더 내민다.
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _userInteraction.NotifyError($"피드백 반영 재생성 실패: {ex.Message}");
                        }
                    }
```

`progressScopeForL3`는 이 블록을 감싸는 `using var progressScopeForL3 = _userInteraction.CreateProgressScope("피드백 반영 재생성") ?? NullProgressScope.Instance;`로 만든다. `GenerateBySplitAsync`가 진행률 스코프를 요구하기 때문이다.

- [ ] **Step 5: 실패 시 통짜 폴백을 하지 않도록 정리**

`if (string.IsNullOrEmpty(rePlan)) { ... continue; }` 는 그대로 둔다. 분할이 null을 돌려주면 `rePlan`이 비어 이 분기로 들어가 **직전 문서로 되돌아간다.** 통짜 폴백을 추가하지 않는다 — L3에는 이미 승인 대기 중인 좋은 문서가 있고, 그것을 통짜로 갈아엎는 것은 개선이 아니다.

L1 재검사 뒤의 **통짜 보완 호출**(현재 `specsRe.Add(("L1_Re_Fix.txt", ...))` 후 `GenerateConsolidatedBatchPlanAsync`를 부르는 블록)은 분할 경로에서 실행되지 않도록 `stepsForRegeneration == null`일 때로 한정한다.

```csharp
                    var l1Re = _validator.ValidateConsolidated(rePlan);
                    rePlan = l1Re.CleansedMarkdown ?? rePlan;
                    if (!l1Re.IsValid && stepsForRegeneration == null)
                    {
                        // 분할로 만든 문서에는 이 보완을 적용하지 않는다. 문서 전체를
                        // 한 번에 다시 써서 단계마다 확보한 본문을 무너뜨리기 때문이다.
                        // 분할 경로에서 L1이 실패하는 원인(H2 누락·Mermaid 문법)은
                        // 골격이 만드는 것이므로, 사용자가 골격을 지목해 다시 시도하면 된다.
                        ... 기존 블록 그대로 ...
                    }
```

- [ ] **Step 6: 배너 재부착**

`consolidatedPlan = rePlan;` 뒤에 추가:

```csharp
                    stepFloorViolations = reViolations;
                    var reSteps = BatchStepPlanParser.TryParse(currentPlanStructure);
                    consolidatedPlan = AttachPipelineBanners(
                        consolidatedPlan, stepFloorViolations, reSteps, specs, jobName);
```

`planReview = null;`과 `planOutcome = VerificationOutcome.ReviewNotRun;`은 그대로 둔다.

- [ ] **Step 7: 테스트 통과 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"`
Expected: PASS (기존 전부 + 신규 4건)

- [ ] **Step 8: 커밋**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CancellationPolicyTests"
dotnet clean && dotnet build   # 경고 8건
dotnet test
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "feat: route L3 feedback through split generation"
```

---

## Task 5: 문서 동기화

**Files:**
- Modify: `docs/architecture.md` — §3.1 Mermaid의 L3 분기, §4.4.3
- Modify: `AGENTS.md` — `VerificationPipelineOrchestrator.cs` 항목
- Modify: `README.md` — L3 설명

**Interfaces:**
- Consumes: Task 1~4의 최종 동작
- Produces: 없음

- [ ] **Step 1: `docs/architecture.md` §3.1 Mermaid**

L3 분기 노드를 갱신한다. 현재 `Regen` 노드가 "구조 변경 피드백이면 목차부터 재수립, 아니면 목차는 유지한 채 본문만 재생성"으로 적혀 있다. 단계 지목을 반영한다. 노드 라벨은 큰따옴표로 감싸고 줄바꿈은 `<br/>`만 쓴다(기존 파일 규칙).

```
Human -- "2. 피드백" --> Regen["구조 변경이면 목차부터 재수립,<br/>아니면 사용자가 지목한 단계만 분할 재생성<br/>(지목이 없거나 골격을 고르면 전 단계)<br/>L2를 다시 거치지 않으므로<br/>종료 상태를 리뷰 미수행으로 되돌림"]
```

- [ ] **Step 2: `docs/architecture.md` §4.4.3**

「구조 변경 피드백 (통합 배치 계획 전용)」 불릿 뒤에 추가:

```markdown
* **단계 지목 재생성 (통합 배치 계획 전용)**: 구조를 바꾸지 않는 피드백에 대해서는 그 피드백이 어느 단계에 관한 것인지 사용자에게 직접 고르게 하고, 지목된 단계만 다시 만듭니다. 골격(개요·흐름도·검증 SQL)과 손대지 않은 단계의 본문은 그대로 재사용하므로, 한 단계를 고치자고 나머지를 잃는 일이 없습니다. 아무것도 고르지 않으면 전 단계를 다시 만들고, 골격을 고르면 공통 규약이 바뀌므로 그것을 인용한 전 단계도 함께 다시 만듭니다. 대상을 모델이 피드백 산문에서 추론하게 하지 않는 이유는 정답을 아는 사람이 화면 앞에 있기 때문입니다.
```

- [ ] **Step 3: `AGENTS.md`**

`VerificationPipelineOrchestrator.cs` 항목 말미에 추가:

```
채택 회차의 목차·골격·골격 AiResult·단계 섹션·하한 위반은 `AdoptedGenerationState` 하나로 묶여 있습니다 — 구제 채택 지점에서 **통째로만** 되돌리십시오. 개별 필드만 되돌리는 코드를 쓰면 화면의 문서와 재생성에 쓰이는 상태가 어긋나고, 그 증상은 "엉뚱한 회차 위에 피드백이 얹힘"이라 추적이 매우 어렵습니다. L3 피드백 재생성은 통짜 `GenerateConsolidatedBatchPlanAsync`가 아니라 `GenerateBySplitAsync`를 거칩니다.
```

- [ ] **Step 4: `README.md`**

「대칭형 검증 적용」 항목의 L3 설명 말미에 추가:

```
구조를 바꾸지 않는 피드백은 대상 단계를 직접 고를 수 있어, 한 단계를 보완하려고 문서 전체를 다시 만들지 않습니다.
```

- [ ] **Step 5: 링크 검증 후 커밋**

```bash
grep -o '](\.\./src/[^)]*)' docs/architecture.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "docs/$p" ] || echo "BROKEN architecture.md: $p"; done
grep -ho '](\./[^)]*)' AGENTS.md README.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "$p" ] || echo "BROKEN: $p"; done
dotnet test 2>&1 | tail -1
grep -n "개의 단위 테스트" AGENTS.md   # 숫자가 실측과 다르면 갱신

git add README.md AGENTS.md docs/architecture.md
git commit -m "docs: record step-targeted L3 regeneration"
```

---

## Self-Review

**1. Spec coverage**

| 스펙 절 | 구현 태스크 |
|---|---|
| §1 피드백 처리 흐름 | Task 3 (질문 순서), Task 4 (분기) |
| §2 재사용/재생성 표 | Task 4 Step 4 |
| §3 `GenerateBySplitAsync` 재사용 | Task 4 Step 4 |
| §4 채택 회차 정합 (`AdoptedGenerationState`) | Task 1 |
| §5 인터페이스 변경 | Task 3 Step 3·4 |
| §6 다중 선택 UI + 매핑 규칙 3건 | Task 3 Step 1·5 |
| §7 배너 재부착·커버리지 항상 재계산 | Task 2, Task 4 Step 6 |
| §8 문서 L1 검사 (통짜 보완 제외) | Task 4 Step 5 |
| §9 실패 처리 | Task 4 Step 5 |
| 테스트 목록 | Task 1·3·4 각 Step 1 |
| 문서 동기화 | Task 5 |

**2. Placeholder scan** — "TBD"·"TODO" 없음. Task 2 Step 2의 `... 잘라낸 본문을 한 글자도 바꾸지 않고 그대로 ...`는 자리표시자가 아니라 의도적 지시다. 70줄 가까운 주석 블록을 이 문서에 다시 옮겨 적으면 원문과 갈라질 위험이 이득보다 크므로, 잘라낼 범위와 검증 방법(기존 배너 테스트 통과)을 대신 명시했다. Task 4 Step 5의 `... 기존 블록 그대로 ...`도 같은 이유다.

**3. Type consistency** — 확인한 항목:
- `AdoptedGenerationState`의 필드 순서가 Task 1의 정의·스냅샷·복원에서 일치
- `MapStepSelection`의 반환 튜플 이름(`TargetStepCodes`, `RegenerateSkeleton`)이 `HumanReviewResult`의 속성명과 일치
- `RequestHumanReviewAsync`의 매개변수 순서(`..., bool structureRedraftSupported = false, IReadOnlyList<BatchStepPlan>? steps = null`)가 인터페이스·구현·테스트의 `Arg.Any` 나열에서 일치
- `GenerateBySplitAsync`의 인자 순서를 Task 4가 **읽어서 맞추도록** 지시함 — 이 계획은 그 시그니처를 재현하지 않는다. Task 8 시절 한 번 밀렸던 값이라 실물 확인이 안전하다

**발견해 고친 것**: 초안은 `adoptedSteps`를 Task 2에서 `AttachPipelineBanners` 안으로 넣었다가 Task 4에서 다시 꺼내는 순서였다. Task 2가 만든 것을 Task 4가 곧바로 뒤집는 모양이라, Task 2의 시그니처를 처음부터 `IReadOnlyList<BatchStepPlan>? adoptedSteps`를 받는 형태로 두고 파싱은 호출부에 남기도록 Task 4 Step 3에 명시했다.
