# 검증 커버리지 표기 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 산출물이 점수 옆에 실제로 실행된 검증량을 함께 말하게 한다.

**Architecture:** 사실 하나(`VerificationCoverage`)를 오케스트레이터에서 한 번 계산해 두 표면에 서로 다른 말투로 싣는다 — 문서 헤더는 사람이 읽을 비율, 지시서 §0은 에이전트가 따를 지침. `VerificationOutcome`은 바꾸지 않는다. 이 작업은 점수를 고치는 것이 아니라 점수 옆에 빠진 사실을 놓는 것이다.

**Tech Stack:** C# / .NET 10, xUnit, NSubstitute

## Global Constraints

- 대상 명세서: `docs/superpowers/specs/2026-08-13-verification-coverage-design.md`
- `StepsTotal`은 **null 허용**이다. 분할이 없었던 회차에 `0`을 넣지 않는다 — `0/0`은 비율처럼 보이는 거짓이다.
- `StepDefectKind.QualityFloor`는 `StepsVerified`에서 **빼지 않는다.** 그것은 검사가 돌았고 떨어진 것이다. `Unverifiable`만 뺀다.
- 커버리지 계산은 한 곳에서만 한다. 누락 코드 사전을 `AttachPipelineBanners` 밖에서 다시 계산하지 않는다.
- `VerificationOutcome`에 새 상태를 만들지 않는다.
- 주석과 사용자 표시 문자열은 한국어. 기존 파일의 어조를 따른다.
- 커밋 메시지 끝에 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`를 붙인다.

## File Structure

| 파일 | 책임 | 작업 |
|---|---|---|
| `src/ReSet.Core/Models/VerificationCoverage.cs` | 값 객체 (신규) | Task 1 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | 계산·운반 | Task 2 |
| `src/ReSet.Core/Models/ConsolidatedPipelineResult.cs` | 운반 계약 | Task 2 |
| `src/ReSet.Core/Services/VerificationDocumentFormatter.cs` | 사람용 표기 | Task 3 |
| `src/ReSet.Cli/Program.cs` | 배치 계획서 기록 배선 | Task 3 |
| `src/ReSet.Core/Services/InstructionEntryPointComposer.cs` | 에이전트용 §0 | Task 4 |
| `src/ReSet.Core/Services/InstructionBundleWriter.cs` | §0 입력 배선 | Task 4 |

Task 1 → 2 → 3, 4 순이다. Task 3과 4는 서로 독립이지만 둘 다 Task 2의 운반 필드를 소비한다.

---

### Task 1: `VerificationCoverage` 값 객체

**Files:**
- Create: `src/ReSet.Core/Models/VerificationCoverage.cs`
- Test: `tests/ReSet.Core.Tests/VerificationCoverageTests.cs` (신규)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record VerificationCoverage(int? StepsTotal, int StepsVerified, bool HasDocumentCodeGap)
  {
      public static VerificationCoverage From(
          IReadOnlyList<BatchStepPlan>? adoptedSteps,
          IReadOnlyDictionary<string, StepDefect> stepFloorViolations,
          bool hasDocumentCodeGap);

      public bool SplitRan { get; }
      public bool HasUnverifiedSteps { get; }
      public bool NeedsHumanAttention { get; }
  }
  ```

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/VerificationCoverageTests.cs`를 새로 만든다.

```csharp
using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class VerificationCoverageTests
    {
        // BatchStepPlan은 위치 기반 레코드다(Code, Name, LegacyProcedures,
        // TargetTables, ErrorCodes, Chunkable, SchemaTables) - 객체 초기자로는
        // 만들어지지 않는다.
        private static IReadOnlyList<BatchStepPlan> Steps(int count)
        {
            var list = new List<BatchStepPlan>();
            for (var i = 1; i <= count; i++)
            {
                list.Add(new BatchStepPlan(
                    $"S{i:00}",
                    $"{i}번 단계",
                    new[] { "UP_X" },
                    new[] { "dbo.T1" },
                    new[] { "-1" },
                    false,
                    System.Array.Empty<string>()));
            }

            return list;
        }

        // 분할이 없었던 회차에는 분모 자체가 없다. 0을 넣으면 "0/0"이 되어
        // 비율처럼 보이는 거짓이 된다 - 실측(POQSettleProc7)에서 단계가 하나도
        // 없는 문서가 가장 높은 점수를 받았고, 그 사실을 숫자로 가리면 안 된다.
        [Fact]
        public void From_WhenSplitDidNotRun_LeavesTotalUnreported()
        {
            var coverage = VerificationCoverage.From(
                adoptedSteps: null,
                stepFloorViolations: new Dictionary<string, StepDefect>(),
                hasDocumentCodeGap: false);

            Assert.Null(coverage.StepsTotal);
            Assert.False(coverage.SplitRan);
            Assert.True(coverage.NeedsHumanAttention);
        }

        // Unverifiable은 "대조할 재료가 없어 검사를 실행하지 못했다"이므로 빠진다.
        [Fact]
        public void From_SubtractsUnverifiableStepsFromTheVerifiedCount()
        {
            var violations = new Dictionary<string, StepDefect>
            {
                ["S01"] = new StepDefect(StepDefectKind.Unverifiable, "S01 (검증 불가)"),
                ["S03"] = new StepDefect(StepDefectKind.Unverifiable, "S03 (검증 불가)")
            };

            var coverage = VerificationCoverage.From(Steps(19), violations, false);

            Assert.Equal(19, coverage.StepsTotal);
            Assert.Equal(17, coverage.StepsVerified);
            Assert.True(coverage.HasUnverifiedSteps);
            Assert.True(coverage.NeedsHumanAttention);
        }

        // QualityFloor는 검사가 돌았고 떨어진 것이다. 여기서 빼면 "검사를 못 돌렸다"와
        // "검사에서 떨어졌다"가 다시 뭉개진다 - StepDefectKind가 그 둘을 가르려고
        // 존재한다.
        [Fact]
        public void From_DoesNotSubtractQualityFloorViolations()
        {
            var violations = new Dictionary<string, StepDefect>
            {
                ["S01"] = new StepDefect(StepDefectKind.QualityFloor, "S01 (하한 미달)"),
                ["S02"] = new StepDefect(StepDefectKind.QualityFloor, "S02 (하한 미달)")
            };

            var coverage = VerificationCoverage.From(Steps(19), violations, false);

            Assert.Equal(19, coverage.StepsVerified);
            Assert.False(coverage.HasUnverifiedSteps);
        }

        [Fact]
        public void From_MixedViolations_SubtractsOnlyTheUnverifiableOnes()
        {
            var violations = new Dictionary<string, StepDefect>
            {
                ["S01"] = new StepDefect(StepDefectKind.Unverifiable, "S01 (검증 불가)"),
                ["S02"] = new StepDefect(StepDefectKind.QualityFloor, "S02 (하한 미달)")
            };

            var coverage = VerificationCoverage.From(Steps(10), violations, false);

            Assert.Equal(9, coverage.StepsVerified);
        }

        [Fact]
        public void From_WhenEverythingIsClean_NeedsNoHumanAttention()
        {
            var coverage = VerificationCoverage.From(
                Steps(19), new Dictionary<string, StepDefect>(), hasDocumentCodeGap: false);

            Assert.Equal(19, coverage.StepsVerified);
            Assert.False(coverage.NeedsHumanAttention);
        }

        // 단계가 전부 검증됐어도 문서 전체 오류코드 대조에서 누락이 나오면
        // 사람이 봐야 한다. 세 조건은 각자 독립적으로 발화한다.
        [Fact]
        public void From_DocumentCodeGapAloneTriggersAttention()
        {
            var coverage = VerificationCoverage.From(
                Steps(19), new Dictionary<string, StepDefect>(), hasDocumentCodeGap: true);

            Assert.False(coverage.HasUnverifiedSteps);
            Assert.True(coverage.NeedsHumanAttention);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~VerificationCoverageTests"`
Expected: FAIL — `CS0246: 'VerificationCoverage' 형식 또는 네임스페이스 이름을 찾을 수 없습니다`

- [ ] **Step 3: 값 객체를 만든다**

`src/ReSet.Core/Models/VerificationCoverage.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;

namespace ReSet.Core.Models
{
    /// <summary>
    /// 이 산출물이 실제로 받은 기계 검증의 양.
    ///
    /// 점수(<see cref="ReviewResult"/>)와 나란히 놓이지만 다른 것을 잰다. 점수는
    /// 읽어 본 품질이고 이것은 대조해 본 분량이다. 실측 세 회차에서 둘이 정반대로
    /// 움직였다 - 원본 오류코드 76개 중 20개가 사라진 문서가 92점, 76개를 모두
    /// 지킨 문서가 88점이었다. Critic은 읽기 좋음을 보고 없는 것은 보지 못한다.
    /// </summary>
    /// <param name="StepsTotal">
    /// 채택된 목차의 단계 수. <c>null</c>은 분할이 실행되지 않았다는 뜻이며,
    /// <c>0</c>과 다르다 - 분모가 없는 상태를 0으로 적으면 비율처럼 보이는 거짓이
    /// 된다.
    /// </param>
    /// <param name="StepsVerified">하한 검사를 실제로 실행한 단계 수.</param>
    /// <param name="HasDocumentCodeGap">원본 오류코드 중 문서 어디에도 없는 것이 있는가.</param>
    public sealed record VerificationCoverage(
        int? StepsTotal,
        int StepsVerified,
        bool HasDocumentCodeGap)
    {
        /// <summary>분할 생성이 실행되어 단계 단위 검증이 성립했는가.</summary>
        public bool SplitRan => StepsTotal.HasValue;

        /// <summary>대조할 재료가 없어 검사를 못 돌린 단계가 남았는가.</summary>
        public bool HasUnverifiedSteps => StepsTotal.HasValue && StepsVerified < StepsTotal.Value;

        /// <summary>
        /// 종료 상태가 Passed라도 사람이 봐야 하는가. 세 사유가 각자 독립적으로 발화한다.
        /// </summary>
        public bool NeedsHumanAttention => !SplitRan || HasUnverifiedSteps || HasDocumentCodeGap;

        /// <summary>
        /// 파이프라인이 들고 있는 재료에서 커버리지를 만든다.
        ///
        /// <see cref="StepDefectKind.QualityFloor"/>는 빼지 않는다. 그것은 검사가
        /// 돌았고 떨어진 것이라 "검사를 실행했다"에 속한다. 두 종류를 합치면
        /// StepDefectKind가 가르려고 만들어진 구분이 여기서 다시 무너진다.
        /// </summary>
        public static VerificationCoverage From(
            IReadOnlyList<BatchStepPlan>? adoptedSteps,
            IReadOnlyDictionary<string, StepDefect> stepFloorViolations,
            bool hasDocumentCodeGap)
        {
            if (adoptedSteps == null)
            {
                return new VerificationCoverage(null, 0, hasDocumentCodeGap);
            }

            var unverifiable = stepFloorViolations?
                .Values.Count(defect => defect.Kind == StepDefectKind.Unverifiable) ?? 0;

            var verified = adoptedSteps.Count - unverifiable;
            return new VerificationCoverage(
                adoptedSteps.Count,
                verified < 0 ? 0 : verified,
                hasDocumentCodeGap);
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~VerificationCoverageTests"`
Expected: PASS (6/6)

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Models/VerificationCoverage.cs tests/ReSet.Core.Tests/VerificationCoverageTests.cs
git commit -m "$(cat <<'EOF'
feat: add VerificationCoverage, the counterpart the score never had

A Critic reads for quality and cannot see absence. Across three runs the
score moved opposite to completeness: the document missing 20 of 76
original error codes scored 92, the one keeping all 76 scored 88.

StepsTotal is nullable because a run where the split never happened has no
denominator, and writing 0 there would render as a ratio. QualityFloor
violations are not subtracted — those are checks that ran and failed, which
is the distinction StepDefectKind exists to draw.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: 오케스트레이터가 커버리지를 계산해 결과에 싣는다

**Files:**
- Modify: `src/ReSet.Core/Models/ConsolidatedPipelineResult.cs:18-23`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` — `AttachPipelineBanners`(`:2458`) 및 두 호출부(`:2180`, `:2424`), 결과 생성 지점(`:2187`, `:2201`)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 1의 `VerificationCoverage.From(...)`
- Produces: `ConsolidatedPipelineResult.Coverage` (`VerificationCoverage?`)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`VerificationPipelineOrchestratorTests.cs`에 추가한다. `SkeletonMarkdown`과 `HealthyStepSection`은 이 파일에 이미 있는 헬퍼다.

```csharp
        // 커버리지는 헤더와 지시서 §0 양쪽이 소비한다. 파이프라인 결과에 실리지
        // 않으면 두 소비자가 각자 계산하게 되고, 그러면 같은 사실이 두 곳에서
        // 갈라진다 - 이 저장소가 반복해서 겪은 실패다.
        [Fact]
        public async Task RunConsolidatedPipeline_WhenSplitRuns_ReportsStepCoverage()
        {
            var stepsJson = "```json\n{\n  \"Steps\": [\n    { \"Code\": \"S01\", \"Name\": \"첫 단계\", \"LegacyProcedures\": [\"USP_Spec1\"], \"TargetTables\": [\"dbo.T1\"], \"ErrorCodes\": [\"-1\"] }\n  ]\n}\n```";
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + stepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(), "2", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);
            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.NotNull(result.Coverage);
            Assert.Equal(1, result.Coverage!.StepsTotal);
            Assert.Equal(1, result.Coverage.StepsVerified);
            Assert.False(result.Coverage.NeedsHumanAttention);
        }

        // 분할이 무산된 회차. StepsTotal이 0이 아니라 null이어야 한다 - 0으로
        // 적으면 "0단계를 0개 검증했다"는 비율로 읽힌다.
        [Fact]
        public async Task RunConsolidatedPipeline_WhenSplitDidNotRun_LeavesStepTotalUnreported()
        {
            var emptyStepsJson = "```json\n{\n  \"Steps\": []\n}\n```";
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + emptyStepsJson });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = SkeletonMarkdown }));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(), "2", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);
            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.NotNull(result.Coverage);
            Assert.Null(result.Coverage!.StepsTotal);
            Assert.False(result.Coverage.SplitRan);
            Assert.True(result.Coverage.NeedsHumanAttention);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ReportsStepCoverage|FullyQualifiedName~LeavesStepTotalUnreported"`
Expected: FAIL — `CS1061: 'ConsolidatedPipelineResult'에는 'Coverage'에 대한 정의가 포함되어 있지 않습니다`

- [ ] **Step 3: 운반 계약에 필드를 더한다**

`src/ReSet.Core/Models/ConsolidatedPipelineResult.cs`의 레코드를 아래로 바꾼다.

```csharp
public sealed record ConsolidatedPipelineResult(
    string? Plan,
    AiResult? Result,
    ReviewResult? Review,
    VerificationOutcome Outcome,
    PlanLayout? Layout = null,
    VerificationCoverage? Coverage = null);
```

`PlanLayout`에 넣지 않는다. `PlanLayout`은 문서의 *구조*를 담고 커버리지는 그 구조를 얼마나 *검사했는가*라, 형제로 두어야 각자 자기 이름으로 남는다.

- [ ] **Step 4: `AttachPipelineBanners`가 커버리지를 함께 돌려주게 한다**

반환형을 튜플로 바꾼다. 시그니처(`:2458` 부근):

```csharp
        private (string Plan, VerificationCoverage Coverage) AttachPipelineBanners(
            string consolidatedPlan,
            string documentBody,
            IReadOnlyDictionary<string, StepDefect> stepFloorViolations,
            IReadOnlyList<BatchStepPlan>? adoptedSteps,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string jobName)
```

메서드 안에서 누락 코드 사전을 만드는 곳(`missingCodes` 지역, `:2553` 부근)의 결과를 커버리지에 넘겨야 하므로, 그 블록 위에 플래그를 선언하고 블록 안에서 채운다.

```csharp
            var hasDocumentCodeGap = false;
```

`missingCodes.Count > 0` 분기 안에서 `hasDocumentCodeGap = true;`를 세운다. **사전을 밖에서 다시 계산하지 않는다** — 같은 사실을 두 곳이 만들면 갈라진다.

메서드 마지막의 `return consolidatedPlan;`을 아래로 바꾼다.

```csharp
            return (consolidatedPlan,
                VerificationCoverage.From(adoptedSteps, stepFloorViolations, hasDocumentCodeGap));
```

- [ ] **Step 5: 두 호출부를 튜플 분해로 고친다**

두 호출부가 같은 지역 변수를 갱신해야 하므로, 첫 호출 **앞에** 변수를 선언한다. 두 번째 경로(L3 재생성)는 첫 번째보다 뒤에서 실행되며, 재생성으로 단계 구성이 바뀌면 이전 커버리지는 그 문서의 것이 아니다.

`:2180` 부근:

```csharp
            var adoptedSteps = BatchStepPlanParser.TryParse(currentPlanStructure);
            VerificationCoverage? coverage;
            (consolidatedPlan, coverage) = AttachPipelineBanners(
                consolidatedPlan, documentBodyForChecks, stepFloorViolations, adoptedSteps, specs, jobName);
```

`:2424` 부근(L3 재생성 경로) — 같은 `coverage` 지역에 다시 대입한다.

```csharp
                    adoptedSteps = BatchStepPlanParser.TryParse(currentPlanStructure);
                    (consolidatedPlan, coverage) = AttachPipelineBanners(
                        consolidatedPlan, consolidatedPlan, stepFloorViolations, adoptedSteps, specs, jobName);
```

`coverage`를 `VerificationCoverage?`로 선언하되 `AttachPipelineBanners`는 항상 값을 돌려주므로, 결과 생성 지점에서 null 검사는 필요하지 않다. 선언 위치가 두 호출부를 모두 덮는지 확인할 것 — 첫 호출이 L3 루프 밖이고 두 번째가 안이므로, 루프 밖 선언이어야 한다.

- [ ] **Step 6: 결과 생성 지점 두 곳에 커버리지를 싣는다**

`:2187`과 `:2201`의 `new ConsolidatedPipelineResult(...)` 호출에 `coverage`를 마지막 인자로 더한다.

```csharp
                return new ConsolidatedPipelineResult(
                    consolidatedPlan, finalAiResult, planReview, planOutcome, BuildLayout(adoptedSteps), coverage);
```

`:1913`과 `:2205`의 `new ConsolidatedPipelineResult(null, null, null, planOutcome)`는 문서가 없는 경로이므로 그대로 둔다.

- [ ] **Step 7: 통과를 확인한다**

Run: `dotnet test`
Expected: PASS, 실패 0

- [ ] **Step 8: 커밋한다**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat: compute verification coverage once and carry it on the result

The three materials — adopted steps, floor violations, and the missing-code
dictionary — all live inside AttachPipelineBanners, so that is where the
coverage is built. Recomputing the dictionary outside would put the same
fact in two places, which is how this repository has drifted before.

The L3 regeneration path recomputes it: if regeneration changes the step
set, the earlier coverage no longer describes the document being shipped.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: 문서 헤더에 비율을 싣는다 (사람용)

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationDocumentFormatter.cs:19-27` (시그니처), `:38-46` (렌더링)
- Modify: `src/ReSet.Cli/Program.cs:870`, `:1426` (배치 계획서 기록 두 곳)
- Test: `tests/ReSet.Core.Tests/VerificationDocumentFormatterTests.cs`

**Interfaces:**
- Consumes: Task 1의 `VerificationCoverage`, Task 2의 `ConsolidatedPipelineResult.Coverage`
- Produces: `FormatVerifiedDocument(..., AnalysisScope? scope = null, VerificationCoverage? coverage = null)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/VerificationDocumentFormatterTests.cs`에 추가한다.

```csharp
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
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~RendersTheStepRatio"`
Expected: FAIL — `CS1739: 'FormatVerifiedDocument'에 가장 적합한 오버로드에는 'coverage' 매개 변수가 없습니다`

- [ ] **Step 3: 시그니처와 렌더링을 고친다**

`VerificationDocumentFormatter.cs`의 시그니처에 파라미터를 더한다.

```csharp
    public static string FormatVerifiedDocument(
        string body,
        ReviewResult? review,
        VerificationOutcome outcome,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp,
        AnalysisScope? scope = null,
        VerificationCoverage? coverage = null)
```

`scoreLines` 계산부 아래에 커버리지 줄을 더한다. 기존 `scopeLine`이 쓰는 것과 같은 방식이다 — 값이 없으면 줄 자체가 생기지 않는다.

```csharp
        // 점수 옆에 검증량을 놓는다. 점수는 읽어 본 품질이고 이것은 대조해 본
        // 분량이라 서로를 대신하지 못한다. 단일 SP 명세서에는 단계 개념이 없으므로
        // coverage가 null이면 줄 자체를 만들지 않는다 - scope와 같은 규칙이다.
        var coverageLine = coverage switch
        {
            null => string.Empty,
            { StepsTotal: null } => "\n단계 검증: 미실행 (목차가 단계 목록을 내지 못함)",
            var c => $"\n단계 검증: {c.StepsVerified}/{c.StepsTotal}"
        };
```

`yamlFrontMatter`의 `{scoreLines}` 뒤에 `{coverageLine}`을 넣는다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~FormatVerifiedDocument"`
Expected: PASS

- [ ] **Step 5: 배치 계획서 기록 두 곳을 배선한다**

`src/ReSet.Cli/Program.cs:870`과 `:1426`의 호출에 인자를 더한다. 두 곳 모두 `pipelineResult`가 스코프에 있다.

```csharp
                                VerificationDocumentFormatter.FormatVerifiedDocument(
                                    consolidatedPlan,
                                    pipelineResult.Review,
                                    pipelineResult.Outcome,
                                    provider,
                                    modelName,
                                    consolidatorEffort,
                                    DateTime.Now,
                                    scope: null,
                                    coverage: pipelineResult.Coverage));
```

**나머지 세 호출부는 손대지 않는다** — `DependencyAnalysisOrchestrator.cs:521`과 `Program.cs`의 명세서 경로는 단계 개념이 없다. `Program.cs:2003`의 `FormatUnverifiedDocument`도 대상이 아니다.

- [ ] **Step 6: 전체 통과를 확인한다**

Run: `dotnet test`
Expected: PASS, 실패 0

- [ ] **Step 7: 커밋한다**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat: put the step-verification ratio beside the score in the header

A reader deciding whether to trust 88 or 92 needs to know how much was
actually checked. The line is omitted entirely when there is no coverage to
report, because this formatter also writes single-procedure specifications,
which have no steps — the same rule the scope line already follows.

A run whose split never happened prints "미실행", not 0/0: there is no
denominator, and writing one would read as a measurement.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: 지시서 §0가 세 사유를 모두 본다 (에이전트용)

**Files:**
- Modify: `src/ReSet.Core/Services/InstructionEntryPointComposer.cs:12-30` (입력 레코드), `:103-120` (§0 렌더링), `:68` (호출)
- Modify: `src/ReSet.Core/Services/InstructionBundleWriter.cs:199-203`
- Test: `tests/ReSet.Core.Tests/InstructionEntryPointComposerTests.cs`

**Interfaces:**
- Consumes: Task 1의 `VerificationCoverage`, Task 2의 `ConsolidatedPipelineResult.Coverage`
- Produces: `PlanVerificationSection(VerificationOutcome planOutcome, VerificationCoverage? coverage)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`InstructionEntryPointComposerTests.cs`에 추가한다.

```csharp
        // 종전에는 FloorViolations에 Unverifiable이 있는지만 봤다. 단계가 아예
        // 없으면 위반도 없으므로 플래그가 꺼졌고, 가장 적게 검증된 문서가 가장
        // 깨끗한 배지를 달았다 - 실측(POQSettleProc7)에서 단계별 섹션이 하나도
        // 없고 원본 오류코드 20개가 빠진 문서가 ✅ "모두 통과"로 나갔다.
        [Fact]
        public void PlanVerificationSection_WhenSplitDidNotRun_WarnsInsteadOfClaimingCleanPass()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(null, 0, false));

            Assert.Contains("⚠️", section);
            Assert.DoesNotContain("✅", section);
            Assert.Contains("단계 단위 기계 검증이 실행되지 않았", section);
        }

        [Fact]
        public void PlanVerificationSection_WhenStepsAreUnverified_Warns()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(19, 17, false));

            Assert.Contains("⚠️", section);
            Assert.Contains("검증되지 못한 단계", section);
        }

        [Fact]
        public void PlanVerificationSection_WhenDocumentCodesAreMissing_Warns()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(19, 19, true));

            Assert.Contains("⚠️", section);
            Assert.Contains("원본 오류코드", section);
        }

        // 참인 사유만 나열해야 한다. 해당 없는 사유를 적으면 읽는 사람이 실제
        // 결함을 흘려보낸다.
        [Fact]
        public void PlanVerificationSection_ListsOnlyTheReasonsThatApply()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(19, 17, false));

            Assert.DoesNotContain("원본 오류코드", section);
            Assert.DoesNotContain("실행되지 않았", section);
        }

        // 부재 확인. 조건이 뒤집히면 정상 산출물마다 거짓 경고가 붙는데, 그것을
        // 잡는 테스트는 이것뿐이다.
        [Fact]
        public void PlanVerificationSection_WhenEverythingIsVerified_KeepsTheCleanPass()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(19, 19, false));

            Assert.Contains("✅", section);
            Assert.DoesNotContain("⚠️", section);
            Assert.DoesNotContain("다만", section);
        }

        // Passed가 아닌 경로는 이미 ⚠️와 "사람의 검토가 필요합니다"를 쓴다.
        [Fact]
        public void PlanVerificationSection_NonPassedOutcome_IsUnchanged()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.QualityRejected,
                new VerificationCoverage(19, 19, false));

            Assert.Contains("⚠️", section);
            Assert.Contains("사람의 검토가 필요합니다", section);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~PlanVerificationSection_WhenSplitDidNotRun"`
Expected: FAIL — `CS1503: 'VerificationCoverage'에서 'bool'로 변환할 수 없습니다`

- [ ] **Step 3: §0 렌더링을 고친다**

`InstructionEntryPointComposer.cs`의 `PlanVerificationSection`에서 `Passed` 분기를 아래로 교체한다.

```csharp
            if (planOutcome == VerificationOutcome.Passed)
            {
                var attention = coverage?.NeedsHumanAttention ?? false;
                sb.AppendLine(attention ? "## ⚠️ 0. 이 계획서의 검증 상태" : "## ✅ 0. 이 계획서의 검증 상태");
                sb.AppendLine();
                sb.AppendLine($"**{label}** — L1 기계 검증과 L2 AI 교차 리뷰를 모두 통과한 계획입니다.");

                if (attention)
                {
                    // 참인 사유만 싣는다. 해당 없는 사유를 적으면 읽는 사람이 실제
                    // 결함을 흘려보낸다.
                    var reasons = new List<string>();
                    if (!coverage!.SplitRan)
                    {
                        reasons.Add("목차가 단계 목록을 내지 못해 단계 단위 기계 검증이 실행되지 않았고");
                    }
                    else if (coverage.HasUnverifiedSteps)
                    {
                        reasons.Add("대조할 재료가 목차에 없어 검증되지 못한 단계가 있고");
                    }

                    if (coverage!.HasDocumentCodeGap)
                    {
                        reasons.Add("원본 오류코드 일부가 문서에서 확인되지 않았고");
                    }

                    var joined = string.Join(" ", reasons);
                    if (joined.EndsWith("고"))
                    {
                        joined = joined[..^1] + "습니다.";
                    }

                    sb.AppendLine($"다만 {joined} 구현 전에 사람의 확인이 필요합니다.");
                }

                return sb.ToString();
            }
```

시그니처와 `:68`의 호출도 함께 바꾼다.

```csharp
        public static string PlanVerificationSection(
            VerificationOutcome planOutcome, VerificationCoverage? coverage)
```

```csharp
            sb.AppendLine(PlanVerificationSection(inputs.PlanOutcome, inputs.Coverage));
```

`EntryPointInputs`의 `bool HasUnverifiableSteps`를 `VerificationCoverage? Coverage`로 교체하고, doc-comment도 새 의미로 다시 쓴다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~InstructionEntryPointComposerTests"`
Expected: PASS

- [ ] **Step 5: 번들 작성기의 입력 배선을 고친다**

`InstructionBundleWriter.cs:199-203`의 `HasUnverifiableSteps` 계산을 지우고 커버리지를 그대로 넘긴다. `InstructionBundleInputs`(`:18` 부근, `PlanLayout? Layout` 옆)에 `VerificationCoverage? Coverage` 필드를 더하고, 그 레코드를 만드는 호출부에 `pipelineResult.Coverage`를 넘긴다.

```csharp
                // 커버리지는 파이프라인이 한 번 계산해 넘긴 값을 그대로 쓴다.
                // 여기서 다시 세면 헤더와 §0이 서로 다른 수를 말하게 된다.
                Coverage: inputs.Coverage));
```

- [ ] **Step 6: 전체 통과를 확인한다**

Run: `dotnet test`
Expected: PASS, 실패 0

기존 테스트가 `HasUnverifiableSteps`를 쓰고 있으면 `VerificationCoverage`로 바꾼다. 불리언을 남겨 두 경로가 각자 계산하게 하지 말 것.

- [ ] **Step 7: 커밋한다**

```bash
git add -A
git commit -m "$(cat <<'EOF'
fix: let §0 see the two failures it was blind to

The qualifier keyed off floor violations alone, so a run that produced no
steps produced no violations and earned a green check — the least-verified
document got the cleanest badge. POQSettleProc7 shipped that way: no
per-step sections, 20 of 76 original error codes gone, and a ✅ above the
warnings that said so.

The section now reads the same coverage the header does, and names only the
reasons that actually apply.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## 완료 후

- `AGENTS.md`의 단위 테스트 개수를 실제 값으로 갱신한다(현재 1413, 이 계획으로 약 17개 증가).
- `docs/architecture.md`의 `VerificationDocumentFormatter`·`InstructionEntryPointComposer` 서술에 커버리지 표기를 한 문단으로 더한다.
- 다음 배치 실행에서 헤더의 `단계 검증: N/M`과 §0의 문구를 실물로 확인한다.
