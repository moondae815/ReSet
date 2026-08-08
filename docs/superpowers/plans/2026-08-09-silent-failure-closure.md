# 조용한 실패 세 경로 닫기 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 계획서 구간 유실, 무인 배치 무한 재시도, 목차 블록 갈림 — 실패가 표면에 드러나지 않는 세 경로를 닫는다.

**Architecture:** 세 결함은 파일도 테스트도 겹치지 않는 독립 변경이다. 각각 순수 함수를 먼저 만들고(태스크 1·3·5) 그것을 호출부에 배선한다(태스크 2·4·6). 계획서 조각화는 고아 구간을 사례별로 막지 않고 "덮이지 않은 줄 범위"를 계산해 한 번에 흡수한다.

**Tech Stack:** .NET 10, C#, xunit 2.9.3, NSubstitute 5.3.0, Serilog

## Global Constraints

- 작업 위치는 워크트리 `.worktrees/silent-failure-closure`, 브랜치 `fix/silent-failure-closure`, 분기점 `42aaf65`
- 빌드 경고는 **8개 기준선**을 유지한다. 늘리지 않는다
- 예외를 새로 던지는 경로를 만들지 않는다. 새 `catch`를 쓸 때는 반드시
  `when (ex is not OperationCanceledException)`를 붙인다 (`CancellationPolicyTests`가 강제한다)
- 이 브랜치의 diff에 등장해도 되는 소스 파일은 정확히 넷이다:
  `PlanBoundaryResolver.cs`, `CodegenLoopPolicy.cs`, `CodegenWorkflowOrchestrator.cs`,
  `BatchStepPlan.cs`, `PlanStructureEnricher.cs`
- `MechanicalValidator.RequiredConsolidatedHeaders`의 내용과 순서는 무변경
- 배너(`VerificationBanner`), `TargetTables` 보강, `OpenAiClient` 재시도 정책,
  Claude 캐시 중단점, 회차(Staged) 경로의 루프 제어는 전부 무변경
- 주석과 로그 메시지는 한국어로 쓴다. 기존 파일의 어조를 따른다
- 커밋 메시지는 영어로 쓰고 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`로 끝낸다

---

## 파일 구조

| 파일 | 책임 | 태스크 |
|---|---|---|
| `src/ReSet.Core/Services/PlanBoundaryResolver.cs` | 계획서 조각화. `FindUncoveredRanges`(신규 공개) + `AbsorbUncoveredRegions`(신규 비공개) | 1, 2 |
| `tests/ReSet.Core.Tests/PlanBoundaryResolverTests.cs` | 위 검증 | 1, 2 |
| `src/ReSet.Validator.Core/Services/CodegenLoopPolicy.cs` | 루프 판단 순수 함수. `BuildUnverifiedFeedback` 추가 | 3 |
| `tests/ReSet.Core.Tests/CodegenLoopPolicyTests.cs` | 위 검증 | 3 |
| `src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs` | 레거시 자가 수정 루프 | 4 |
| `tests/ReSet.Core.Tests/CodegenWorkflowOrchestratorTests.cs` | 캡·피드백·중단 사유 검증 | 4 |
| `src/ReSet.Core/Services/BatchStepPlan.cs` | 목차 파서. `TryLocateStepsBlock` 공개 | 5 |
| `tests/ReSet.Core.Tests/BatchStepPlanParserTests.cs` | 위 검증 | 5 |
| `src/ReSet.Core/Services/PlanStructureEnricher.cs` | 오류코드 보강. 자기 정규식을 버리고 선택기를 쓴다 | 6 |
| `tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs` | 두 곳이 같은 블록을 고르는지 검증 | 6 |

의존 관계: 2는 1에, 4는 3에, 6은 5에 의존한다. 세 사슬은 서로 독립이다.

---

## 사전 확인

- [ ] **Step 0: 기준선 확보**

```bash
cd /Users/payletter/git-root/ReSet/.worktrees/silent-failure-closure
git branch --show-current   # fix/silent-failure-closure 여야 한다
git rev-parse HEAD
dotnet build 2>&1 | tail -3
dotnet test 2>&1 | tail -3
```

경고 개수와 테스트 통과 수를 적어 둔다. 이 계획이 끝날 때 경고는 같아야 하고 테스트는 늘어야 한다.

---

## Task 1: 덮이지 않은 줄 범위 계산

**Files:**
- Modify: `src/ReSet.Core/Services/PlanBoundaryResolver.cs`
- Test: `tests/ReSet.Core.Tests/PlanBoundaryResolverTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `PlanBoundaryResolver.FindUncoveredRanges(int lineCount, IEnumerable<(int Start, int End)> covered)` →
  `IReadOnlyList<(int Start, int End)>`. Task 2가 쓴다.

**배경:** `Resolve`의 성공 경로가 만드는 조각들이 문서의 모든 줄을 덮는지 아무도 확인하지 않는다.
지금 세 구간이 새고 있다. 사례별로 막으면 네 번째가 생겼을 때 똑같이 놓친다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/PlanBoundaryResolverTests.cs`의 클래스 안, 맨 아래
`Resolve_ShouldFallBackToWholeDocument_WhenBothSplitsFail` 뒤에 추가한다.

```csharp
        /// <summary>
        /// 조각이 덮은 범위를 모으면 덮이지 않은 구간이 계산된다. Resolve는 이 결과를
        /// 개요에 흡수해 "모든 줄이 어느 조각엔가 담긴다"는 불변식을 지킨다.
        /// </summary>
        [Fact]
        public void FindUncoveredRanges_NothingCovered_ShouldReturnWholeDocument()
        {
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, Array.Empty<(int, int)>());

            Assert.Equal(new[] { (0, 10) }, gaps);
        }

        [Fact]
        public void FindUncoveredRanges_FullyCovered_ShouldReturnNothing()
        {
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, new[] { (0, 10) });

            Assert.Empty(gaps);
        }

        [Fact]
        public void FindUncoveredRanges_ShouldFindLeadingMiddleAndTrailingGaps()
        {
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(20, new[] { (3, 7), (12, 15) });

            Assert.Equal(new[] { (0, 3), (7, 12), (15, 20) }, gaps);
        }

        [Fact]
        public void FindUncoveredRanges_UnorderedInput_ShouldGiveSameResult()
        {
            // Resolve는 조각을 만든 순서대로 범위를 넣는다. 그 순서가 문서 순서와
            // 같으리라고 기대하지 않는다.
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, new[] { (7, 10), (0, 3) });

            Assert.Equal(new[] { (3, 7) }, gaps);
        }

        [Fact]
        public void FindUncoveredRanges_OverlappingRanges_ShouldNotInventGaps()
        {
            // 문서가 기형이라 단계 구간이 검증 SQL 구간과 겹칠 수 있다. 겹침을 빈틈으로
            // 잘못 읽으면 이미 실린 내용이 개요에 한 번 더 실린다.
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, new[] { (0, 6), (3, 10) });

            Assert.Empty(gaps);
        }

        [Fact]
        public void FindUncoveredRanges_ShouldIgnoreEmptyRanges()
        {
            // End <= Start는 "그 조각은 만들어지지 않았다"는 뜻이다. 덮은 것으로 세지 않는다.
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, new[] { (0, 4), (6, 6), (6, 10) });

            Assert.Equal(new[] { (4, 6) }, gaps);
        }

        [Fact]
        public void FindUncoveredRanges_ShouldClampOutOfBoundsRanges()
        {
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, new[] { (-5, 3), (8, 99) });

            Assert.Equal(new[] { (3, 8) }, gaps);
        }

        [Fact]
        public void FindUncoveredRanges_EmptyDocument_ShouldReturnNothing()
        {
            Assert.Empty(PlanBoundaryResolver.FindUncoveredRanges(0, new[] { (0, 5) }));
        }
```

파일 맨 위 `using` 목록에 `using System;`이 없으면 추가한다(`Array.Empty`가 필요하다).

- [ ] **Step 2: 빨간불 확인**

```bash
dotnet test --filter "FullyQualifiedName~PlanBoundaryResolverTests.FindUncoveredRanges" 2>&1 | tail -20
```

기대: 컴파일 실패. `'PlanBoundaryResolver' does not contain a definition for 'FindUncoveredRanges'`

- [ ] **Step 3: 구현한다**

`src/ReSet.Core/Services/PlanBoundaryResolver.cs`의 `Join` 헬퍼 바로 위에 추가한다.

```csharp
        /// <summary>
        /// [0, lineCount) 중 covered가 덮지 않은 구간을 오름차순으로 돌려준다.
        ///
        /// 조각을 새로 만들면 그 범위를 <see cref="Resolve"/>의 covered 목록에 반드시
        /// 등록해야 한다. 등록을 잊으면 그 구간이 개요에 <b>중복</b>으로 실리고(회차마다
        /// 읽는 파일이 부푼다), 범위만 등록하고 조각을 만들지 않으면 구간이 <b>사라진다</b>.
        /// 둘 다 눈으로는 드러나지 않으므로 이 계산을 조각 나누는 코드 옆에 둔다.
        ///
        /// 겹치는 범위는 병합한다. 문서가 기형이라 단계 구간이 검증 SQL 구간과 겹치는
        /// 경우가 있는데, 그것을 빈틈으로 읽으면 이미 실린 내용을 한 번 더 싣게 된다.
        /// </summary>
        public static IReadOnlyList<(int Start, int End)> FindUncoveredRanges(
            int lineCount, IEnumerable<(int Start, int End)> covered)
        {
            if (covered == null) throw new ArgumentNullException(nameof(covered));

            var gaps = new List<(int Start, int End)>();
            if (lineCount <= 0)
            {
                return gaps;
            }

            var normalized = covered
                .Select(range => (Start: Math.Max(0, range.Start), End: Math.Min(lineCount, range.End)))
                .Where(range => range.End > range.Start)
                .OrderBy(range => range.Start)
                .ToList();

            var cursor = 0;
            foreach (var range in normalized)
            {
                if (range.Start > cursor)
                {
                    gaps.Add((cursor, range.Start));
                }

                if (range.End > cursor)
                {
                    cursor = range.End;
                }
            }

            if (cursor < lineCount)
            {
                gaps.Add((cursor, lineCount));
            }

            return gaps;
        }
```

`using System;`과 `using System.Linq;`는 이 파일에 이미 있다.

- [ ] **Step 4: 초록불 확인**

```bash
dotnet test --filter "FullyQualifiedName~PlanBoundaryResolverTests.FindUncoveredRanges" 2>&1 | tail -5
```

기대: 8건 통과.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/PlanBoundaryResolver.cs tests/ReSet.Core.Tests/PlanBoundaryResolverTests.cs
git commit -F - <<'EOF'
feat: compute which plan lines no slice covers

Slicing the plan drops any region that falls between the slices, and the
success path currently drops three. Patching the three known cases would
leave the fourth to go the same way, so compute the gaps instead.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

## Task 2: 고아 구간을 개요에 흡수한다

**Files:**
- Modify: `src/ReSet.Core/Services/PlanBoundaryResolver.cs:314-349` (`Resolve`의 골격 분할 성공 경로)
- Test: `tests/ReSet.Core.Tests/PlanBoundaryResolverTests.cs`

**Interfaces:**
- Consumes: `PlanBoundaryResolver.FindUncoveredRanges(int, IEnumerable<(int Start, int End)>)` (Task 1)
- Produces: 없음. `PlanSlices`의 형태는 바뀌지 않는다

**배경:** 골격 H2 넷을 모두 찾은 경로에서 세 구간이 어느 조각에도 담기지 않는다.

1. `## 단계별 이행 상세 및 의사코드` 헤딩 줄 자체 — 개요는 그 앞에서 끝나고 공통 규약은 그 뒤에서 시작한다
2. 마지막 단계 끝 ~ 검증 SQL H2 — `Materialize`가 마지막 단계를 "다음 `## `"에서 끊으므로,
   그 헤딩이 검증 SQL이 아니면(`## 부록` 등) 그 구간이 샌다
3. 검증 SQL 섹션 뒤 — `verificationEnd`가 문서 끝이 아니라 다음 H2다

골격 탐색이 실패한 분기는 이미 `BuildWholeSkeletonAroundSteps`(`:397`)로 꼬리를 흡수한다.
성공 경로에만 그 대응물이 없다.

- [ ] **Step 1: 실패하는 테스트를 쓴다 — 불변식**

같은 테스트 파일에 추가한다. 이 파일에는 `!allFound` 분기용으로 같은 성질을 검사하는
테스트가 이미 있다(`:455-462`). 이것은 성공 경로용 짝이다.

```csharp
        /// <summary>
        /// 골격 분할이 성공한 경로에서도 모든 줄이 어느 조각엔가 담겨야 한다.
        /// 담기지 않은 줄은 코딩 에이전트가 읽을 방법이 아예 없다.
        ///
        /// 부록을 두 자리에 둔다 - 마지막 단계와 검증 SQL 사이, 그리고 검증 SQL 뒤.
        /// 두 자리 모두 종전에는 조각 사이로 샜다.
        /// </summary>
        [Fact]
        public void Resolve_SkeletonSplitSucceeded_ShouldPlaceEveryLineInSomeSlice()
        {
            var document = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

공통 규약 본문

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문

## 성능 고려사항

성능 메모 본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문

## 부록 - 운영 메모

운영 메모 본문
""";

            var slices = PlanBoundaryResolver.Resolve(document, LayoutWithSections());

            Assert.True(slices.SkeletonSplit);
            Assert.True(slices.StepsSplit);

            var covered = string.Join(
                "\n",
                new[] { slices.Preamble, slices.Architecture, slices.StepContract ?? "", slices.Verification ?? "" }
                    .Concat(slices.Steps.Values));

            foreach (var line in document.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                Assert.Contains(line, covered);
            }
        }
```

- [ ] **Step 2: 실패하는 테스트를 쓴다 — 흡수처와 헤딩**

```csharp
        /// <summary>
        /// 고아 구간은 개요로 간다. 개요(common/00-architecture.md)는 모든 회차가 무조건
        /// 읽는 유일한 파일이라, 어느 회차가 그것을 필요로 하는지 판별하지 못한 상태에서
        /// 고를 수 있는 유일한 자리다.
        /// </summary>
        [Fact]
        public void Resolve_OrphanRegions_ShouldBeAbsorbedIntoArchitecture()
        {
            var document = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

공통 규약 본문

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문

## 성능 고려사항

성능 메모 본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문

## 부록 - 운영 메모

운영 메모 본문
""";

            var slices = PlanBoundaryResolver.Resolve(document, LayoutWithSections());

            Assert.Contains("성능 메모 본문", slices.Architecture);
            Assert.Contains("운영 메모 본문", slices.Architecture);

            // 흡수는 담기지 않은 것만 담는다. 이미 다른 조각이 가진 내용을 개요에
            // 다시 실으면 회차마다 읽는 파일이 조용히 부푼다.
            Assert.DoesNotContain("정제된 S01 본문", slices.Architecture);
            Assert.DoesNotContain("검증 SQL 본문", slices.Architecture);
            Assert.DoesNotContain("공통 규약 본문", slices.Architecture);
        }

        /// <summary>
        /// 공통 규약 조각이 자기 제목을 갖는다. 종전에는 이 H2 줄이 개요의 끝과 공통 규약의
        /// 시작 사이에 끼어 어느 조각에도 없었다.
        /// </summary>
        [Fact]
        public void Resolve_StepContract_ShouldBeginWithItsOwnHeading()
        {
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, LayoutWithSections());

            Assert.NotNull(slices.StepContract);
            Assert.StartsWith("## 단계별 이행 상세 및 의사코드", slices.StepContract!);
            Assert.Contains("공통 규약 본문", slices.StepContract!);
        }

        /// <summary>
        /// 산문이 없으면 공통 규약 조각을 만들지 않는다. 헤딩을 무조건 붙이면 헤딩 한 줄짜리
        /// 파일이 생기고 진입점이 그것을 링크한다(InstructionBundleWriter.cs:61) - 회차마다
        /// 읽히는 빈 파일이 하나 는다. 남겨진 헤딩 줄은 개요가 받는다.
        /// </summary>
        [Fact]
        public void Resolve_StepContractWithoutProse_ShouldStayNullAndLeaveHeadingToArchitecture()
        {
            var noProse = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문
""";

            var slices = PlanBoundaryResolver.Resolve(noProse, LayoutWithSections());

            Assert.True(slices.SkeletonSplit);
            Assert.Null(slices.StepContract);
            Assert.Contains("## 단계별 이행 상세 및 의사코드", slices.Architecture);
        }

        /// <summary>
        /// 고아 구간이 없는 정상 문서에서는 개요가 종전과 똑같아야 한다. 흡수 기계를 넣고
        /// 나서 정상 문서의 개요가 달라지면 회차 입력이 조용히 부푼 것이다.
        /// </summary>
        [Fact]
        public void Resolve_DocumentWithoutOrphans_ShouldLeaveArchitectureUnchanged()
        {
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, LayoutWithSections());

            Assert.Equal(
                """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문
""",
                slices.Architecture);
        }
```

- [ ] **Step 3: 빨간불 확인**

```bash
dotnet test --filter "FullyQualifiedName~PlanBoundaryResolverTests" 2>&1 | tail -30
```

기대: 위 5건이 실패한다. 특히
`Resolve_SkeletonSplitSucceeded_ShouldPlaceEveryLineInSomeSlice`는 "성능 메모 본문"에서,
`Resolve_StepContract_ShouldBeginWithItsOwnHeading`은 `StartsWith`에서 깨진다.

- [ ] **Step 4: 공통 규약이 헤딩을 갖게 한다**

`src/ReSet.Core/Services/PlanBoundaryResolver.cs`의 `Resolve` 안, 기존 블록

```csharp
            // 공통 규약 = (H2③, 첫 단계 헤딩). 단계 경계를 모르면 끝점을 정할 수 없다.
            string? stepContract = null;
            if (steps.Split && steps.FirstStepLineIndex > positions[2])
            {
                stepContract = Join(lines, positions[2] + 1, steps.FirstStepLineIndex);
                if (stepContract.Length == 0)
                {
                    stepContract = null;
                }
            }
```

을 다음으로 바꾼다.

```csharp
            // 공통 규약 = [H2③, 첫 단계 헤딩). 헤딩 줄까지 담아 이 조각이 자기 제목을 갖게
            // 한다 - 종전에는 그 줄이 개요의 끝과 이 조각의 시작 사이에 끼어 사라졌다.
            //
            // 다만 "비었는가"는 산문만 보고 판정한다. 헤딩을 무조건 붙이면 산문이 없는
            // 문서에서 헤딩 한 줄짜리 파일이 생기고 진입점이 그것을 링크한다
            // (InstructionBundleWriter.cs:61). 산문이 없으면 조각을 만들지 않고,
            // 남겨진 헤딩 줄은 아래 흡수가 개요로 가져간다.
            string? stepContract = null;
            if (steps.Split && steps.FirstStepLineIndex > positions[2])
            {
                var prose = Join(lines, positions[2] + 1, steps.FirstStepLineIndex);
                if (prose.Length > 0)
                {
                    stepContract = Join(lines, positions[2], steps.FirstStepLineIndex);
                }
            }
```

- [ ] **Step 5: 검증 SQL의 끝점을 한 변수로 정리한다**

기존 블록

```csharp
            // 검증 SQL = [H2④, 다음 H2 또는 문서 끝)
            var verificationEnd = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, positions[3] + 1,
                line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal));
            var verification = Join(lines, positions[3], verificationEnd < 0 ? lines.Count : verificationEnd);
```

을 다음으로 바꾼다. `verificationEnd`를 아래 흡수 계산이 그대로 쓰므로, 삼항 연산을
변수에 흡수시켜 두 곳이 다른 값을 보지 않게 한다.

```csharp
            // 검증 SQL = [H2④, 다음 H2 또는 문서 끝)
            var nextH2AfterVerification = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, positions[3] + 1,
                line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal));
            var verificationEnd = nextH2AfterVerification < 0 ? lines.Count : nextH2AfterVerification;
            var verification = Join(lines, positions[3], verificationEnd);
```

- [ ] **Step 6: 흡수를 배선한다**

Step 5의 블록 바로 아래, 기존 `Log.Information("골격을 분할했습니다 ...")` **앞에** 넣는다.

```csharp
            // 어느 조각에도 담기지 않은 줄이 없어야 한다. 조각이 덮은 범위를 모아
            // 빈틈을 계산하고, 남은 것은 전부 개요가 받는다.
            //
            // 조각을 새로 만들면 이 목록에 범위를 등록해야 한다. 등록을 잊으면 그 구간이
            // 개요에 중복으로 실리고, 범위만 등록하고 조각을 만들지 않으면 사라진다.
            var covered = new List<(int Start, int End)>
            {
                (0, positions[0]),                 // Preamble
                (positions[0], architectureEnd),   // Architecture 본체
            };

            if (stepContract != null)
            {
                covered.Add((positions[2], steps.FirstStepLineIndex));
            }

            if (steps.Split)
            {
                covered.Add((steps.FirstStepLineIndex, steps.LastStepEndLineIndex));
            }

            if (verification.Length > 0)
            {
                covered.Add((positions[3], verificationEnd));
            }

            architecture = AbsorbUncoveredRegions(lines, architecture, covered);
```

`architecture`는 지금 `var architecture = Join(...)`로 선언돼 있어 재대입할 수 있다.
`using System.Collections.Generic;`은 이 파일에 이미 있다.

- [ ] **Step 7: 흡수 헬퍼를 만든다**

`BuildWholeSkeletonAroundSteps` 바로 아래, `Join` 위에 추가한다.

```csharp
        /// <summary>
        /// 어느 조각에도 담기지 않은 구간을 개요에 흡수한다.
        ///
        /// 개요(common/00-architecture.md)는 <b>모든</b> 회차가 무조건 읽는 유일한 파일이다
        /// (TaskFileComposer.Compose의 "먼저 읽을 것" 2번). 어느 회차가 그 구간을 필요로
        /// 하는지 판별하지 못한 상태이므로, 판별 없이도 반드시 읽히는 자리에 둔다.
        /// 골격 탐색이 실패한 분기가 이미 같은 판단을 내렸다.
        ///
        /// 배너를 올리지 않는다 - 이것은 결함 보고가 아니라 복구다. 사용자에게 요구할
        /// 조치가 없다. 대신 흡수한 줄 범위를 로그에 남겨 원인을 추적할 수 있게 한다.
        /// </summary>
        private static string AbsorbUncoveredRegions(
            IReadOnlyList<string> lines,
            string architecture,
            IReadOnlyList<(int Start, int End)> covered)
        {
            foreach (var range in FindUncoveredRanges(lines.Count, covered))
            {
                var text = Join(lines, range.Start, range.End);
                if (text.Length == 0)
                {
                    // 공백뿐인 구간은 담을 것이 없다. 개요에 빈 줄만 늘리지 않는다.
                    continue;
                }

                Log.Information(
                    "어느 조각에도 속하지 않은 구간을 개요에 흡수했습니다 - 줄 [{Start}, {End})",
                    range.Start, range.End);

                architecture = architecture.Length == 0 ? text : architecture + "\n\n" + text;
            }

            return architecture;
        }
```

- [ ] **Step 8: 초록불 확인 — 새 테스트와 기존 테스트 모두**

```bash
dotnet test --filter "FullyQualifiedName~PlanBoundaryResolverTests" 2>&1 | tail -10
```

기대: 전부 통과. 기존 테스트가 하나라도 깨지면 흡수가 이미 실린 내용을 다시 담고 있는
것이므로 `covered` 목록을 다시 본다 — 특히 `Assert.DoesNotContain(...)` 계열이 신호다.

- [ ] **Step 9: 전체 스위트와 빌드 경고 확인**

```bash
dotnet build 2>&1 | grep -c "warning"
dotnet test 2>&1 | tail -3
```

기대: 경고 개수가 Step 0과 같다.

- [ ] **Step 10: 커밋**

```bash
git add src/ReSet.Core/Services/PlanBoundaryResolver.cs tests/ReSet.Core.Tests/PlanBoundaryResolverTests.cs
git commit -F - <<'EOF'
fix: stop dropping plan regions that fall between slices

Three regions of a successfully split plan reached no output file: the step
detail heading itself, anything between the last step and the verification
H2, and anything after the verification section. An agent has no way to read
what is in none of the slices.

The skeleton-failure branch already absorbs its tail into the architecture
slice for the same reason; this gives the success path the same treatment,
driven by computed gaps rather than by enumerating the known cases.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

## Task 3: 미대조 피드백 문구

**Files:**
- Modify: `src/ReSet.Validator.Core/Services/CodegenLoopPolicy.cs`
- Test: `tests/ReSet.Core.Tests/CodegenLoopPolicyTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `CodegenLoopPolicy.BuildUnverifiedFeedback(string specDir, string codeDir, int attempt)` → `string`.
  Task 4가 쓴다.

**배경:** 레거시 자가 수정 루프는 검증 대조 쌍을 하나도 찾지 못했을 때
(`CodegenWorkflowOrchestrator.cs:139`) 지시서를 손대지 않고 그대로 재시도한다. 에이전트는
무엇이 잘못됐는지 알 방법이 없다. 회차 경로는 같은 상황에서 피드백을 붙인다(`:461`).

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/CodegenLoopPolicyTests.cs`의 클래스 안에 추가한다.

```csharp
        /// <summary>
        /// 대조 쌍을 못 찾았을 때의 피드백은 에이전트가 실제로 고칠 수 있는 것을 말해야 한다.
        /// 경로 두 개와 이름 규약이 그것이다. 이 문구가 없으면 재시도는 같은 명령을 신호 없이
        /// 다시 던지는 것이라 다음 시도도 같은 결과로 끝난다.
        /// </summary>
        [Fact]
        public void BuildUnverifiedFeedback_ShouldCarryBothDirectoriesAndTheNamingRule()
        {
            var feedback = CodegenLoopPolicy.BuildUnverifiedFeedback(
                @"C:\out\Procedures", @"C:\out\Jobs\MyJob\src", attempt: 2);

            Assert.Contains("Attempt 2", feedback);
            Assert.Contains(@"C:\out\Procedures", feedback);
            Assert.Contains(@"C:\out\Jobs\MyJob\src", feedback);
            // 이름 규약을 말해 주지 않으면 에이전트가 무엇을 고쳐야 할지 모른다.
            Assert.Contains("스키마", feedback);
            Assert.Contains("CustOrderHist", feedback);
        }

        /// <summary>
        /// 지시서 끝에 여러 번 붙으므로 시도 회차로 구별돼야 한다.
        /// </summary>
        [Fact]
        public void BuildUnverifiedFeedback_DifferentAttempts_ShouldBeDistinguishable()
        {
            var first = CodegenLoopPolicy.BuildUnverifiedFeedback("/spec", "/code", attempt: 1);
            var second = CodegenLoopPolicy.BuildUnverifiedFeedback("/spec", "/code", attempt: 2);

            Assert.NotEqual(first, second);
            Assert.Contains("Attempt 1", first);
            Assert.Contains("Attempt 2", second);
        }

        /// <summary>
        /// 이 실패는 CLI 기동 문제가 아니다. 엔진 설정을 확인하라고 말하면 사람을 엉뚱한
        /// 곳으로 보낸다 - 기동은 성공했고 산출물도 나왔다.
        /// </summary>
        [Fact]
        public void BuildUnverifiedFeedback_ShouldNotBlameTheEngineConfiguration()
        {
            var feedback = CodegenLoopPolicy.BuildUnverifiedFeedback("/spec", "/code", attempt: 1);

            Assert.DoesNotContain("CodegenSettings", feedback);
            Assert.DoesNotContain("AiSettings", feedback);
        }
```

- [ ] **Step 2: 빨간불 확인**

```bash
dotnet test --filter "FullyQualifiedName~CodegenLoopPolicyTests.BuildUnverifiedFeedback" 2>&1 | tail -20
```

기대: 컴파일 실패. `does not contain a definition for 'BuildUnverifiedFeedback'`

- [ ] **Step 3: 구현한다**

`src/ReSet.Validator.Core/Services/CodegenLoopPolicy.cs`의 `Decide` 아래에 추가하고,
파일 맨 위 `using` 목록에 `using System.Text;`를 추가한다.

```csharp
        /// <summary>
        /// 검증 대조 쌍을 하나도 찾지 못했을 때 지시서에 붙일 피드백.
        ///
        /// 이것이 없으면 재시도는 같은 명령을 신호 없이 다시 던지는 것이다. 에이전트는
        /// 무엇이 잘못됐는지 알 수 없고, 그래서 다음 시도도 같은 자리에서 끝난다.
        ///
        /// 매핑 규약은 FileMappingService가 소유한다(FileMappingService.cs:135-160).
        /// 여기서는 에이전트가 고칠 수 있는 형태로만 옮겨 적는다.
        /// </summary>
        public static string BuildUnverifiedFeedback(string specDir, string codeDir, int attempt)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"## 🚨 [검증 대조 실패 - Attempt {attempt}] 🚨");
            sb.AppendLine(
                "검증기가 설계서와 소스 코드의 대조 쌍을 **하나도** 찾지 못했습니다. " +
                "코드가 생성되었더라도 한 줄도 검증되지 않은 상태입니다.");
            sb.AppendLine();
            sb.AppendLine($"- 설계서 디렉터리: `{specDir}`");
            sb.AppendLine($"- 소스 디렉터리: `{codeDir}`");
            sb.AppendLine();
            sb.AppendLine(
                "검증기는 설계서 폴더명에서 스키마를 뗀 이름으로 짝을 찾습니다. " +
                "예를 들어 설계서가 `dbo.CustOrderHist/docs/Spec.md`에 있으면 " +
                "소스 디렉터리에서 `CustOrderHist`라는 이름의 **파일**(확장자 무관) 또는 " +
                "같은 이름의 **폴더**를 찾습니다.");
            sb.AppendLine();
            sb.AppendLine("생성한 파일과 폴더의 이름이 이 규약을 따르는지 확인하고, 어긋나면 이름을 고치십시오.");

            return sb.ToString();
        }
```

- [ ] **Step 4: 초록불 확인**

```bash
dotnet test --filter "FullyQualifiedName~CodegenLoopPolicyTests" 2>&1 | tail -5
```

기대: 기존 테스트 + 새 3건 전부 통과.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Validator.Core/Services/CodegenLoopPolicy.cs tests/ReSet.Core.Tests/CodegenLoopPolicyTests.cs
git commit -F - <<'EOF'
feat: tell the agent why nothing could be verified

When the verifier resolves zero spec-to-source pairs, the legacy loop retries
without touching the instructions, so the agent gets no signal and the next
attempt ends the same way. This composes the missing feedback: both
directories and the naming rule the mapper actually applies.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

## Task 4: 레거시 루프가 반드시 끝난다

**Files:**
- Modify: `src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs:62-176` (`RunSelfHealingWorkflowAsync`)
- Test: `tests/ReSet.Core.Tests/CodegenWorkflowOrchestratorTests.cs`

**Interfaces:**
- Consumes: `CodegenLoopPolicy.BuildUnverifiedFeedback(string specDir, string codeDir, int attempt)` (Task 3)
- Produces: 없음

**배경:** `nothingVerified`(`:129`)에 상한이 없다. `MaxL2Attempts: "unlimited"`면
`maxAttempts = int.MaxValue`(`:63`)라 무인 배치에서 끝나지 않는 유료 기동이 된다.
회차 경로는 `MaxConsecutiveUnverifiedRetries`로 같은 상황을 막는다(`:469`).

- [ ] **Step 1: 테스트 하네스에 목 노출과 지시서 심기를 추가한다**

`tests/ReSet.Core.Tests/CodegenWorkflowOrchestratorTests.cs`에서 `BuildOrchestrator`가
만든 `IMetadataExporter` 목을 테스트가 볼 수 있어야 한다. 필드를 추가하고 대입한다.

클래스 필드 선언부(`private readonly string _instructionsPath;` 아래)에 추가:

```csharp
        /// <summary>BuildOrchestrator가 마지막으로 만든 목. 피드백 호출을 검사하는 데 쓴다.</summary>
        private IMetadataExporter _metadataExporter = null!;
```

`BuildOrchestrator` 안의 `var metadataExporter = Substitute.For<IMetadataExporter>();`를
다음으로 바꾼다:

```csharp
            _metadataExporter = Substitute.For<IMetadataExporter>();
```

그리고 마지막 `return`의 인자도 `_metadataExporter`로 바꾼다:

```csharp
            return new CodegenWorkflowOrchestrator(engine, verifier, _metadataExporter, maxL2Attempts);
```

`SeedVerifiableJob` 아래에 지시서 파일을 심는 헬퍼를 추가한다. 루프는
`File.Exists(instructionsFilePath)`일 때만 피드백을 붙이므로(`:157`), 이것 없이는
피드백 호출을 관측할 수 없다.

```csharp
        /// <summary>
        /// 지시서 파일을 실제로 만든다. 루프는 파일이 있을 때만 피드백을 붙이므로
        /// (RunSelfHealingWorkflowAsync의 File.Exists 분기), 이것 없이는 피드백이
        /// 호출되지 않는다.
        /// </summary>
        private void SeedInstructionsFile()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_instructionsPath)!);
            File.WriteAllText(_instructionsPath, "# 마이그레이션 지시서\n\n본문\n");
        }
```

- [ ] **Step 2: 실패하는 테스트를 쓴다**

같은 파일의 클래스 안, `RunSelfHealingWorkflowAsync_RealMappingPasses_ShouldStopAtFirstAttempt`
뒤에 추가한다.

```csharp
        /// <summary>
        /// MaxL2Attempts가 "unlimited"(-1)면 maxAttempts가 int.MaxValue가 된다. 대조가
        /// 계속 실패하는 상태에서 상한이 없으면 무인 배치가 끝나지 않는 유료 기동이 된다.
        /// 회차 경로는 같은 상황을 연속 캡으로 막는다.
        /// </summary>
        [Fact]
        public async Task RunSelfHealingWorkflowAsync_UnlimitedAttempts_ShouldStopAfterTwoUnverifiedRuns()
        {
            // 계획서도 소스도 심지 않는다 - 매핑이 0건이 되는 실제 조건 그대로다.
            var withArtifact = new CodegenRunResult(true, 0, CliFailureKind.Unknown, null);
            var engine = new ScriptedCodingEngine(withArtifact);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: -1);

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(2, engine.CallCount);
        }

        /// <summary>
        /// 중단 사유는 배치 구성 문제를 가리켜야 한다. BuildAbortResult를 재사용하면
        /// CliFailureClassifier가 만든 "CLI 기동 실패" 안내가 나가는데, 여기서는 기동이
        /// 성공하고 산출물까지 나왔으므로 그 안내는 사람을 엉뚱한 곳으로 보낸다.
        /// </summary>
        [Fact]
        public async Task RunSelfHealingWorkflowAsync_UnverifiedCapReached_AbortReasonNamesTheDirectories()
        {
            var withArtifact = new CodegenRunResult(true, 0, CliFailureKind.Unknown, null);
            var engine = new ScriptedCodingEngine(withArtifact);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: -1);

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.NotNull(result.AbortReason);
            Assert.Contains(_specDir, result.AbortReason);
            Assert.Contains(_codeDir, result.AbortReason);
            Assert.DoesNotContain("CodegenSettings:Engines", result.AbortReason);
        }

        /// <summary>
        /// 미대조 시도에도 피드백이 붙어야 한다. 붙이지 않으면 재시도는 같은 명령을
        /// 신호 없이 다시 던지는 것이다.
        /// </summary>
        [Fact]
        public async Task RunSelfHealingWorkflowAsync_NothingVerified_ShouldAppendFeedbackToInstructions()
        {
            SeedInstructionsFile();
            var withArtifact = new CodegenRunResult(true, 0, CliFailureKind.Unknown, null);
            var engine = new ScriptedCodingEngine(withArtifact);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: -1);

            await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            await _metadataExporter.Received().AppendFeedbackToInstructionsAsync(
                _instructionsPath,
                Arg.Is<string>(feedback => feedback.Contains("검증 대조 실패")),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// 캡은 통과 판정보다 뒤에 온다. 1회차에 대조가 실패해도 2회차에 성립하면 그대로
        /// 끝나야 한다 - 캡 판정을 통과 판정 앞에 두면 이 경로가 죽는다.
        /// </summary>
        [Fact]
        public async Task RunSelfHealingWorkflowAsync_MappingAppearsOnSecondRun_ShouldSucceed()
        {
            var engine = new SeedingCodingEngine(SeedVerifiableJob);

            var orchestrator = BuildOrchestrator(engine, maxL2Attempts: -1);

            var result = await orchestrator.RunSelfHealingWorkflowAsync(
                "TestJob", _instructionsPath, _specDir, _codeDir, isBatchMode: true, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(2, engine.CallCount);
        }
```

`ScriptedCodingEngine` 정의 아래에 두 번째 가짜 엔진을 추가한다.

```csharp
        /// <summary>
        /// 두 번째 호출에서 파일 시스템에 부수효과를 일으키는 엔진. 에이전트가 드디어
        /// 규약에 맞는 이름으로 파일을 만든 상황을 재현한다.
        /// </summary>
        private sealed class SeedingCodingEngine : ICodingEngine
        {
            private readonly Action _seedOnSecondCall;

            public SeedingCodingEngine(Action seedOnSecondCall)
            {
                _seedOnSecondCall = seedOnSecondCall;
            }

            public string Name => "seeding-engine";
            public string Command => "seeding";
            public int CallCount { get; private set; }

            public Task<CodegenRunResult> GenerateCodeAsync(
                SpDefinition? spDef, string instructionsFilePath, string targetProjectDir, CancellationToken cancellationToken)
            {
                CallCount++;
                if (CallCount == 2)
                {
                    _seedOnSecondCall();
                }

                return Task.FromResult(new CodegenRunResult(true, 0, CliFailureKind.Unknown, null));
            }
        }
```

- [ ] **Step 3: 기존 테스트의 단언을 갱신한다**

`RunSelfHealingWorkflowAsync_NothingWasVerified_ShouldNotReportSuccess`의
`Assert.Null(result.AbortReason);` 한 줄과 그 위 주석을 바꾼다. 캡이 생기면 그 시점에
사유가 붙으므로 `Null` 단언은 성립하지 않는다. **테스트를 지우지 말고** 원래 의도
("산출물을 못 만든 경로가 아니다")를 유지한 채 교체한다.

바꾸기 전:

```csharp
            Assert.False(result.Succeeded);
            // 산출물은 나왔으므로 "산출물을 못 만들었다"는 중단 사유 경로가 아니다.
            Assert.Null(result.AbortReason);
            // 통과로 읽었다면 1회에 끊겼을 것이다. 시도를 모두 소진했어야 한다.
            Assert.Equal(2, engine.CallCount);
```

바꾼 뒤:

```csharp
            Assert.False(result.Succeeded);
            // 산출물은 나왔으므로 "산출물을 못 만들었다"는 중단 사유 경로가 아니다.
            // 미대조 연속 캡이 생긴 뒤로는 사유가 붙되, 그 사유가 가리키는 것은
            // 기동 실패가 아니라 대조 실패여야 한다.
            Assert.NotNull(result.AbortReason);
            Assert.Contains(_specDir, result.AbortReason);
            Assert.DoesNotContain("CodegenSettings:Engines", result.AbortReason);
            // 통과로 읽었다면 1회에 끊겼을 것이다.
            Assert.Equal(2, engine.CallCount);
```

- [ ] **Step 4: 빨간불 확인**

```bash
dotnet test --filter "FullyQualifiedName~CodegenWorkflowOrchestratorTests" 2>&1 | tail -30
```

기대: 새 4건과 갱신한 1건이 실패한다.
`RunSelfHealingWorkflowAsync_UnlimitedAttempts_ShouldStopAfterTwoUnverifiedRuns`는
**타임아웃 없이 끝나지 않을 수 있다** — `int.MaxValue`만큼 돌기 때문이다. 이 확인 단계에서는
이 테스트 하나를 빼고 돌려 컴파일과 나머지 실패를 먼저 확인한 뒤 Step 5로 넘어간다.

```bash
dotnet test --filter "FullyQualifiedName~CodegenWorkflowOrchestratorTests&FullyQualifiedName!~UnlimitedAttempts" 2>&1 | tail -30
```

- [ ] **Step 5: 카운터를 선언한다**

`src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs`의
`RunSelfHealingWorkflowAsync` 안, `int consecutiveNoArtifactRetries = 0;`(`:67`) 아래에
추가한다.

```csharp
            // 검증 대조 쌍을 하나도 찾지 못한 시도의 연속 횟수. 회차 경로(:457)와 같은
            // 성격이다. 산출물은 나왔으므로 위 무산출물 카운터로는 잡히지 않는다.
            int consecutiveUnverified = 0;
```

- [ ] **Step 6: 카운터를 갱신하고 로그에 담는다**

기존 블록

```csharp
                bool nothingVerified = validationResults.Count == 0;
                bool allPassed = !nothingVerified && failedResults.Count == 0;

                if (allPassed)
                {
                    Log.Information("[SelfHealing] 모든 검증 통과 (MATCH)! 루프 종료.");
                    isSuccess = true;
                    break;
                }

                if (nothingVerified)
                {
                    // 에이전트에게 붙일 L1/L2 피드백이 없다(대조 자체를 못 했다).
                    // 조용히 재시도하면 무엇이 잘못됐는지 어디에도 남지 않는다.
                    Log.Error(
                        "[SelfHealing] 검증 대상을 하나도 찾지 못했습니다(통과 아님) - 설계서 디렉터리: {SpecDir}, 소스 디렉터리: {CodeDir}",
                        specDir, codeDir);
                }
```

을 다음으로 바꾼다.

```csharp
                bool nothingVerified = validationResults.Count == 0;
                bool allPassed = !nothingVerified && failedResults.Count == 0;

                if (allPassed)
                {
                    Log.Information("[SelfHealing] 모든 검증 통과 (MATCH)! 루프 종료.");
                    isSuccess = true;
                    break;
                }

                // 대조가 한 번이라도 성립하면 리셋한다. 캡은 "계속 못 찾는 상태"를 접기
                // 위한 것이지 누적 횟수를 벌하기 위한 것이 아니다.
                consecutiveUnverified = nothingVerified ? consecutiveUnverified + 1 : 0;

                if (nothingVerified)
                {
                    Log.Error(
                        "[SelfHealing] 검증 대상을 하나도 찾지 못했습니다(통과 아님) - 설계서 디렉터리: {SpecDir}, 소스 디렉터리: {CodeDir}, 연속 미대조: {Consecutive}/{Cap}",
                        specDir, codeDir, consecutiveUnverified, MaxConsecutiveUnverifiedRetries);
                }
```

- [ ] **Step 7: 피드백을 붙인다**

기존 블록

```csharp
                // 4. 실패 시 피드백을 지시서에 Append.
                // 대조 자체를 못 한 경우(failedResults가 비어 있는데 통과도 아닌 경우)는
                // 붙일 L1/L2 결과가 없다 - 머리글만 남는 빈 피드백을 쓰지 않는다.
                if (attempt < maxAttempts)
                {
                    if (failedResults.Count > 0)
                    {
                        Log.Information("[SelfHealing] 검증 실패. 피드백을 지시서에 추가하고 에이전트를 재기동합니다.");

                        if (File.Exists(instructionsFilePath))
                        {
                            await _metadataExporter.AppendFeedbackToInstructionsAsync(
                                instructionsFilePath,
                                BuildCriticFeedback($"## 🚨 [AI L1/L2 Critic Feedback - Attempt {attempt}] 🚨", failedResults),
                                cancellationToken);
                        }
                        else
                        {
                            Log.Warning("[SelfHealing] 지시서 파일을 찾을 수 없습니다: {Path}", instructionsFilePath);
                        }
                    }
                }
```

을 다음으로 바꾼다.

```csharp
                // 4. 실패 시 피드백을 지시서에 Append.
                // 대조 자체를 못 한 경우에는 붙일 L1/L2 결과가 없다. 그래도 빈손으로
                // 재시도하지는 않는다 - 무엇을 못 찾았는지 알려 주는 별도 피드백이 있다.
                if (attempt < maxAttempts)
                {
                    var feedback = failedResults.Count > 0
                        ? BuildCriticFeedback($"## 🚨 [AI L1/L2 Critic Feedback - Attempt {attempt}] 🚨", failedResults)
                        : nothingVerified
                            ? CodegenLoopPolicy.BuildUnverifiedFeedback(specDir, codeDir, attempt)
                            : null;

                    if (feedback != null)
                    {
                        Log.Information("[SelfHealing] 검증 실패. 피드백을 지시서에 추가하고 에이전트를 재기동합니다.");

                        if (File.Exists(instructionsFilePath))
                        {
                            await _metadataExporter.AppendFeedbackToInstructionsAsync(
                                instructionsFilePath, feedback, cancellationToken);
                        }
                        else
                        {
                            Log.Warning("[SelfHealing] 지시서 파일을 찾을 수 없습니다: {Path}", instructionsFilePath);
                        }
                    }
                }
```

- [ ] **Step 8: 캡을 판정한다**

Step 7 블록에 이어지는 `else { Log.Warning("[SelfHealing] 최대 재시도 횟수 ...") }` **뒤**,
`attempt++;` **앞**에 추가한다.

```csharp
                // 피드백을 먼저 붙이고 접는다. 마지막 시도에서 끊더라도 지시서에는 이유가
                // 남아 사람이 열어 볼 수 있다. 회차 경로가 같은 순서다(:461 -> :469).
                if (consecutiveUnverified >= MaxConsecutiveUnverifiedRetries)
                {
                    // BuildAbortResult를 쓰지 않는다. 그 헬퍼는 CliFailureClassifier로 사유를
                    // 만들어(:806) 설치 여부와 CodegenSettings:Engines:<name>:Command를
                    // 확인하라고 말한다. 여기서는 기동이 성공하고 산출물까지 나왔으므로
                    // 그 안내는 사람을 엉뚱한 곳으로 보낸다.
                    var reason =
                        $"[SelfHealing] 검증 대조 쌍을 찾지 못한 시도가 {MaxConsecutiveUnverifiedRetries}회 연속 발생했습니다. " +
                        $"설계서 디렉터리와 소스 디렉터리에서 짝을 찾지 못했습니다 - 설계서: {specDir}, 소스: {codeDir}. " +
                        "피드백을 붙여도 대조가 성립하지 않으므로 루프를 중단합니다.";

                    Log.Error("{Reason}", reason);
                    return new CodegenWorkflowResult(false, reason);
                }

                attempt++;
```

- [ ] **Step 9: 초록불 확인**

```bash
dotnet test --filter "FullyQualifiedName~CodegenWorkflowOrchestratorTests" 2>&1 | tail -10
```

기대: 전부 통과. `UnlimitedAttempts` 테스트가 몇 초 안에 끝나야 한다 — 오래 걸리면 캡이
안 걸린 것이므로 Step 8의 위치를 다시 본다(`attempt++` 뒤에 두면 영영 닿지 않는다).

- [ ] **Step 10: 전체 스위트와 빌드 경고 확인**

```bash
dotnet build 2>&1 | grep -c "warning"
dotnet test 2>&1 | tail -3
```

- [ ] **Step 11: 커밋**

```bash
git add src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs tests/ReSet.Core.Tests/CodegenWorkflowOrchestratorTests.cs
git commit -F - <<'EOF'
fix: bound the legacy loop when nothing can be verified

With MaxL2Attempts set to "unlimited" the legacy self-healing loop had no
ceiling on the case where artifacts appear but the verifier resolves zero
pairs, so an unattended batch billed forever without ever looking like a
failure. The staged path already caps the same condition.

The abort reason is composed here rather than through BuildAbortResult: that
helper explains a CLI launch failure, and this run launched fine.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

## Task 5: 목차 블록 선택기를 공개한다

**Files:**
- Modify: `src/ReSet.Core/Services/BatchStepPlan.cs:50-69`
- Test: `tests/ReSet.Core.Tests/BatchStepPlanParserTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `BatchStepPlanParser.StepsBlockLocation` — `readonly record struct(int BodyIndex, int BodyLength, string Body, IReadOnlyList<BatchStepPlan> Steps)`
  - `BatchStepPlanParser.TryLocateStepsBlock(string? planStructureMarkdown)` → `StepsBlockLocation?`

  Task 6이 둘 다 쓴다.

**배경:** `BatchStepPlanParser`(`BatchStepPlan.cs:46`)와 `PlanStructureEnricher`(`:26`)가
바이트 단위로 같은 정규식을 각자 갖고 있지만 유효성 판정이 다르다. 파서는 `Code`/`Name`이
빠진 항목이 있거나 `Steps`가 비면 블록을 버리는데, 보강기는 `Steps`가 배열이기만 하면
받아들인다. 블록이 둘 이상이고 첫 블록이 그런 상태면 **두 곳이 서로 다른 블록을 고른다.**

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/BatchStepPlanParserTests.cs`의 클래스 안에 추가한다.

```csharp
        /// <summary>
        /// 선택기가 돌려주는 범위는 원본 마크다운에서 그대로 잘라낼 수 있어야 한다.
        /// 보강기가 이 범위만 갈아 끼우므로, 어긋나면 펜스가 깨지거나 산문이 잘린다.
        /// </summary>
        [Fact]
        public void TryLocateStepsBlock_ShouldPointAtTheExactBodySpan()
        {
            var markdown = """
# 목차

산문

```json
{ "Steps": [ { "Code": "S01", "Name": "첫 단계" } ] }
```

뒤 산문
""";

            var located = BatchStepPlanParser.TryLocateStepsBlock(markdown);

            Assert.NotNull(located);
            Assert.Equal(
                located!.Value.Body,
                markdown.Substring(located.Value.BodyIndex, located.Value.BodyLength));
            Assert.Single(located.Value.Steps);
            Assert.Equal("S01", located.Value.Steps[0].Code);
        }

        /// <summary>
        /// 파서가 버리는 블록은 선택기도 버린다. 이 성질이 보강기와의 일치를 만든다 -
        /// 두 곳이 각자 판정하면 첫 블록에서 갈린다.
        /// </summary>
        [Fact]
        public void TryLocateStepsBlock_ShouldSkipBlocksTheParserRejects()
        {
            var markdown = """
# 목차

```json
{ "Steps": [ { "Name": "Code가 없는 항목" } ] }
```

```json
{ "Steps": [ { "Code": "S02", "Name": "성한 항목" } ] }
```
""";

            var located = BatchStepPlanParser.TryLocateStepsBlock(markdown);

            Assert.NotNull(located);
            Assert.Contains("S02", located!.Value.Body);
            Assert.DoesNotContain("Code가 없는 항목", located.Value.Body);
        }

        [Fact]
        public void TryLocateStepsBlock_NoValidBlock_ShouldReturnNull()
        {
            var markdown = """
# 목차

```json
{ "NotSteps": [] }
```
""";

            Assert.Null(BatchStepPlanParser.TryLocateStepsBlock(markdown));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void TryLocateStepsBlock_BlankInput_ShouldReturnNull(string? markdown)
        {
            Assert.Null(BatchStepPlanParser.TryLocateStepsBlock(markdown));
        }

        /// <summary>
        /// TryParse는 선택기 위의 얇은 껍데기다. 두 결과가 갈리면 파서 안에서 이미
        /// 목차가 둘로 나뉜 것이다.
        /// </summary>
        [Fact]
        public void TryParse_ShouldReturnTheLocatedBlocksSteps()
        {
            var markdown = """
```json
{ "Steps": [ { "Code": "S01", "Name": "첫 단계" }, { "Code": "S02", "Name": "둘째 단계" } ] }
```
""";

            var parsed = BatchStepPlanParser.TryParse(markdown);
            var located = BatchStepPlanParser.TryLocateStepsBlock(markdown);

            Assert.NotNull(parsed);
            Assert.NotNull(located);
            Assert.Equal(parsed!.Select(s => s.Code), located!.Value.Steps.Select(s => s.Code));
        }
```

파일 맨 위 `using` 목록에 `using System.Linq;`가 없으면 추가한다.

- [ ] **Step 2: 빨간불 확인**

```bash
dotnet test --filter "FullyQualifiedName~BatchStepPlanParserTests.TryLocateStepsBlock" 2>&1 | tail -20
```

기대: 컴파일 실패. `does not contain a definition for 'TryLocateStepsBlock'`

- [ ] **Step 3: 선택기를 만들고 TryParse를 그 위에 올린다**

`src/ReSet.Core/Services/BatchStepPlan.cs`의 기존 `TryParse`(`:50-69`)를 다음으로 바꾼다.

```csharp
        /// <summary>
        /// 목차에서 유효한 단계 목록 블록의 위치와 파싱 결과.
        /// </summary>
        /// <param name="BodyIndex">원본 마크다운에서 ```json 본문이 시작하는 문자 인덱스.</param>
        /// <param name="BodyLength">본문의 길이. 이 구간만 갈아 끼우면 펜스는 보존된다.</param>
        /// <param name="Body">본문 원문.</param>
        /// <param name="Steps">그 본문을 파싱한 결과. 비어 있지 않다.</param>
        public readonly record struct StepsBlockLocation(
            int BodyIndex,
            int BodyLength,
            string Body,
            IReadOnlyList<BatchStepPlan> Steps);

        /// <summary>
        /// 파서와 보강기가 <b>같은</b> 블록을 고르게 하는 단일 진입점.
        ///
        /// 두 곳이 각자 블록을 고르면 PlanStructure.md에 기록된 목차와 파이프라인이 실제로
        /// 쓰는 목차가 갈라진다. 그 불일치는 어디에도 드러나지 않는다 - 파일을 여는 사람은
        /// 자기가 보는 것이 쓰인 것이라고 믿는다.
        ///
        /// 유효성 판정은 TryParseBlock 하나다. 그것이 버리는 블록은 이 선택기도 버린다.
        /// </summary>
        public static StepsBlockLocation? TryLocateStepsBlock(string? planStructureMarkdown)
        {
            if (string.IsNullOrWhiteSpace(planStructureMarkdown))
            {
                return null;
            }

            foreach (Match match in JsonBlockRegex.Matches(planStructureMarkdown))
            {
                var body = match.Groups["body"];
                var parsed = TryParseBlock(body.Value);
                if (parsed != null)
                {
                    return new StepsBlockLocation(body.Index, body.Length, body.Value, parsed);
                }
            }

            return null;
        }

        public static IReadOnlyList<BatchStepPlan>? TryParse(string? planStructureMarkdown)
        {
            // 빈 입력은 "목차가 아직 없다"는 뜻이라 경고할 일이 아니다. 종전 동작 그대로다.
            if (string.IsNullOrWhiteSpace(planStructureMarkdown))
            {
                return null;
            }

            var located = TryLocateStepsBlock(planStructureMarkdown);
            if (located == null)
            {
                Log.Warning("목차에서 유효한 단계 목록 JSON을 찾지 못했습니다. 분할 생성을 건너뜁니다.");
                return null;
            }

            Log.Information("목차에서 단계 목록을 읽었습니다 - 단계 수: {Count}개", located.Value.Steps.Count);
            return located.Value.Steps;
        }
```

- [ ] **Step 4: 초록불 확인**

```bash
dotnet test --filter "FullyQualifiedName~BatchStepPlanParserTests" 2>&1 | tail -5
```

기대: 기존 테스트 + 새 7건(Theory 3건 포함) 전부 통과. 기존 테스트가 깨지면 `TryParse`의
로그 동작이 달라진 것이므로 빈 입력 조기 반환이 남아 있는지 확인한다.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/BatchStepPlan.cs tests/ReSet.Core.Tests/BatchStepPlanParserTests.cs
git commit -F - <<'EOF'
feat: expose the one place that picks the steps block

The parser and the enricher each carried a byte-identical fence regex but
disagreed on which blocks are valid, so a document with two json blocks could
have them read different ones. Give the parser a single entry point that
returns both the span and the parsed steps, so the choice can be shared
rather than reimplemented.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

## Task 6: 보강기가 파서와 같은 블록을 본다

**Files:**
- Modify: `src/ReSet.Core/Services/PlanStructureEnricher.cs:22-74`
- Test: `tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs`

**Interfaces:**
- Consumes: `BatchStepPlanParser.TryLocateStepsBlock(string?)` → `BatchStepPlanParser.StepsBlockLocation?` (Task 5)
- Produces: 없음. `PlanStructureEnricher.Enrich`의 시그니처는 바뀌지 않는다

**바뀌는 동작:** 보강기는 이제 뒤 블록으로 넘어가지 않는다. 파서가 고른 블록의 재작성이
실패하면(중복 키 → `ArgumentException`) 원본을 그대로 돌려준다. 다른 블록을 보강하면
파일에 기록된 목차와 실제로 쓰이는 목차가 갈라지는데, 그것이 이 작업이 닫으려는 결함이다.
보강되지 않은 단계는 하한 검사가 "검증 불가"로 정직하게 보고한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs`의 클래스 안에 추가한다.

```csharp
        /// <summary>
        /// 파서가 버리는 블록이 앞에 있으면 보강도 그 블록을 건너뛰고 파서와 같은 블록을
        /// 골라야 한다. 종전에는 보강기가 자기 기준으로 첫 블록을 받아들여, 파일에 기록된
        /// 목차와 파이프라인이 쓰는 목차가 갈렸다.
        /// </summary>
        [Fact]
        public void Enrich_FirstBlockRejectedByParser_ShouldEnrichTheBlockTheParserReads()
        {
            var markdown = """
# 목차

```json
{ "Steps": [ { "Name": "Code가 없어 파서가 버리는 항목", "LegacyProcedures": ["UP_A"], "ErrorCodes": [] } ] }
```

```json
{ "Steps": [ { "Code": "S01", "Name": "성한 항목", "LegacyProcedures": ["UP_A"], "ErrorCodes": [] } ] }
```
""";

            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["up_a"] = new[] { "-101", "-102" }
            };

            var enriched = PlanStructureEnricher.Enrich(markdown, codes);

            // 파서가 읽는 블록(둘째)에만 코드가 들어가야 한다.
            var located = BatchStepPlanParser.TryLocateStepsBlock(enriched);
            Assert.NotNull(located);
            Assert.Equal(new[] { "-101", "-102" }, located!.Value.Steps[0].ErrorCodes);

            // 첫 블록은 손대지 않는다.
            Assert.Contains("Code가 없어 파서가 버리는 항목", enriched);
            var firstBlockEnd = enriched.IndexOf("```", enriched.IndexOf("Code가 없어", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain("-101", enriched[..firstBlockEnd]);
        }

        /// <summary>
        /// 중복 프로퍼티 이름이 있는 블록은 JsonNode가 던져 보강할 수 없다. 그때 뒤 블록으로
        /// 넘어가면 파서가 읽는 블록(앞의 것)과 갈린다. 보강을 포기하는 편이 옳다 -
        /// 보강되지 않은 단계는 하한 검사가 "검증 불가"로 보고한다.
        /// </summary>
        [Fact]
        public void Enrich_DuplicateKeysInTheParsedBlock_ShouldNotFallThroughToAnotherBlock()
        {
            var markdown = """
# 목차

```json
{ "Steps": [ { "Code": "S01", "Name": "중복 키", "LegacyProcedures": ["UP_A"], "LegacyProcedures": ["UP_A"], "ErrorCodes": [] } ] }
```

```json
{ "Steps": [ { "Code": "S99", "Name": "뒤 블록", "LegacyProcedures": ["UP_A"], "ErrorCodes": [] } ] }
```
""";

            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["up_a"] = new[] { "-101" }
            };

            var enriched = PlanStructureEnricher.Enrich(markdown, codes);

            // 아무것도 보강되지 않는다. 특히 뒤 블록이 조용히 보강되면 안 된다.
            Assert.Equal(markdown, enriched);
        }

        /// <summary>
        /// 블록이 하나뿐인 정상 목차에서는 종전과 같은 결과여야 한다.
        /// </summary>
        [Fact]
        public void Enrich_SingleValidBlock_ShouldStillEnrichInPlace()
        {
            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["up_settlecommupd"] = new[] { "-201" }
            };

            var enriched = PlanStructureEnricher.Enrich(Structure, codes);

            Assert.Contains("산문은 그대로 보존되어야 한다.", enriched);
            Assert.NotEqual(Structure, enriched);
        }

        /// <summary>
        /// 블록 추출 정규식은 소스 트리에 정확히 한 번만 존재해야 한다. 두 벌이 되는 순간
        /// 한쪽만 고쳐지고, 그 갈림은 어디에도 드러나지 않는다.
        ///
        /// 찾는 것은 정규식 패턴 문자열이지 ```json 이라는 낱말이 아니다 - AiService는
        /// 프롬프트 본문에서 그 낱말을 여러 번 쓰고 그것들은 이 검사의 대상이 아니다.
        /// </summary>
        [Fact]
        public void JsonBlockRegexLiteral_ShouldExistExactlyOnceInSourceTree()
        {
            const string literal = @"```json\s*\r?\n(?<body>.*?)```";
            var srcRoot = Path.Combine(RepoPaths.FindRepoRoot(), "src");

            var separator = Path.DirectorySeparatorChar;
            var hits = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path =>
                    !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal) &&
                    !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
                .Where(path => File.ReadAllText(path).Contains(literal, StringComparison.Ordinal))
                .Select(path => Path.GetRelativePath(srcRoot, path).Replace(separator, '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(new[] { "ReSet.Core/Services/BatchStepPlan.cs" }, hits);
        }
```

파일 맨 위 `using` 목록에 `using System;`, `using System.IO;`가 없으면 추가한다.
`System.Collections.Generic`과 `System.Linq`는 이미 있다.

- [ ] **Step 2: 빨간불 확인**

```bash
dotnet test --filter "FullyQualifiedName~PlanStructureEnricherTests" 2>&1 | tail -30
```

기대: `Enrich_FirstBlockRejectedByParser_...`, `Enrich_DuplicateKeysInTheParsedBlock_...`,
`JsonBlockRegexLiteral_...` 세 건이 실패한다. 마지막 것은 `hits`가 두 개
(`BatchStepPlan.cs`와 `PlanStructureEnricher.cs`)라 실패한다.

- [ ] **Step 3: 보강기를 선택기 위로 옮긴다**

`src/ReSet.Core/Services/PlanStructureEnricher.cs`에서 `JsonBlockRegex` 필드(`:24-28`)를
**삭제**하고, `Enrich`의 블록 순회 루프(`:56-73`)를 다음으로 바꾼다.

바꾸기 전:

```csharp
            foreach (Match match in JsonBlockRegex.Matches(planStructureMarkdown))
            {
                var body = match.Groups["body"].Value;
                var rewritten = TryRewriteBlock(body, codesByProcedure);
                if (rewritten == null)
                {
                    // 파서와 같은 규칙: 유효하지 않은 블록은 건너뛰고 다음을 본다.
                    continue;
                }

                var bodyGroup = match.Groups["body"];
                return planStructureMarkdown[..bodyGroup.Index]
                    + rewritten
                    + planStructureMarkdown[(bodyGroup.Index + bodyGroup.Length)..];
            }

            Log.Warning("목차에서 보강할 단계 목록 JSON 블록을 찾지 못했습니다. 원본을 그대로 사용합니다.");
            return planStructureMarkdown;
```

바꾼 뒤:

```csharp
            // 블록 선택은 파서가 소유한다. 여기서 따로 고르면 파일에 기록된 목차와
            // 파이프라인이 실제로 쓰는 목차가 갈라진다.
            var located = BatchStepPlanParser.TryLocateStepsBlock(planStructureMarkdown);
            if (located == null)
            {
                Log.Warning("목차에서 보강할 단계 목록 JSON 블록을 찾지 못했습니다. 원본을 그대로 사용합니다.");
                return planStructureMarkdown;
            }

            var rewritten = TryRewriteBlock(located.Value.Body, codesByProcedure);
            if (rewritten == null)
            {
                // 뒤 블록으로 넘어가지 않는다. 파서가 읽는 블록을 보강하지 못했다면 보강을
                // 포기하는 것이 맞다 - 다른 블록을 고치면 두 목차가 갈라지고, 그 불일치는
                // 어디에도 드러나지 않는다. 보강되지 않은 단계는 하한 검사가 "검증 불가"로
                // 보고하므로 침묵하지도 않는다.
                return planStructureMarkdown;
            }

            return planStructureMarkdown[..located.Value.BodyIndex]
                + rewritten
                + planStructureMarkdown[(located.Value.BodyIndex + located.Value.BodyLength)..];
```

`TryRewriteBlock`, `MergeCodes`, `ReadStringArray`, `WriteOptions`는 손대지 않는다.
`TryRewriteBlock`을 감싸는 `catch (Exception ex) when (ex is not OperationCanceledException)`도
그대로 둔다 — 중복 키 블록이 여전히 `ArgumentException`을 던지고, 그것이 파이프라인 밖으로
새 나가면 안 되는 것도 그대로다.

- [ ] **Step 4: 쓰이지 않는 using을 정리한다**

`JsonBlockRegex`를 지웠으므로 `using System.Text.RegularExpressions;`(`:6`)가 남는지 확인한다.
파일에 다른 정규식 사용이 없으면 그 줄을 삭제한다.

```bash
grep -n "Regex\|Match" src/ReSet.Core/Services/PlanStructureEnricher.cs
```

출력이 `using` 줄뿐이면 삭제한다.

- [ ] **Step 5: 초록불 확인**

```bash
dotnet test --filter "FullyQualifiedName~PlanStructureEnricherTests" 2>&1 | tail -10
```

기대: 기존 테스트 + 새 4건 전부 통과.

- [ ] **Step 6: 전체 스위트와 빌드 경고 확인**

```bash
dotnet build 2>&1 | grep -c "warning"
dotnet test 2>&1 | tail -3
```

기대: 경고 개수가 Step 0과 같다. `using`을 지운 뒤 경고가 줄었다면 그것은 개선이므로
기준선을 새 값으로 기록해 둔다.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/PlanStructureEnricher.cs tests/ReSet.Core.Tests/PlanStructureEnricherTests.cs
git commit -F - <<'EOF'
fix: enrich the same json block the parser reads

The enricher accepted blocks the parser discards, so a plan whose first json
block was malformed could have the file record one step list while the
pipeline ran on another. It now takes the parser's choice and, when that
block cannot be rewritten, leaves the file alone instead of quietly editing a
different one.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

## 마무리

- [ ] **Step 1: 범위 확인**

이 브랜치가 손댄 소스 파일이 다섯 개를 넘지 않아야 한다.

```bash
git diff --stat 42aaf65..HEAD -- src/
```

기대: `PlanBoundaryResolver.cs`, `CodegenLoopPolicy.cs`, `CodegenWorkflowOrchestrator.cs`,
`BatchStepPlan.cs`, `PlanStructureEnricher.cs` 다섯 개만.

- [ ] **Step 2: 전체 검증**

```bash
dotnet build 2>&1 | tail -3
dotnet test 2>&1 | tail -3
git status --short
```

기대: 경고 8개 / 오류 0개, 전체 통과, 작업 트리 깨끗.

- [ ] **Step 3: 구현 후 기록을 스펙에 남긴다**

`docs/superpowers/specs/2026-08-09-silent-failure-closure-design.md` 끝에
`## 구현 후 기록 (YYYY-MM-DD)` 절을 추가한다. 담을 것:

- 최종 테스트 수와 빌드 경고 수
- 이 설계에서 틀린 것으로 드러난 서술 (있으면)
- 구현 중 사람 판정으로 결정한 지점
- 남은 후속 작업
- 자동 테스트가 덮지 못해 사람이 직접 확인해야 하는 것

```bash
git add docs/superpowers/specs/2026-08-09-silent-failure-closure-design.md
git commit -F - <<'EOF'
docs: record the implementation outcome

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

- [ ] **Step 4: 문서 동기화**

`/reset-doc-sync`를 돌린다. **다른 세션이 `docs/architecture.md`와 `AGENTS.md`를 함께
건드리고 있으므로**, 동기화 전에 `main`의 최신 상태를 확인하고 충돌 지점을 먼저 본다.

예상 갱신 지점:

- `docs/architecture.md` 2.2 — `PlanBoundaryResolver` 행에 "모든 줄이 어느 조각엔가 담긴다"
  불변식 한 줄, `BatchStepPlanParser` 행에 선택기 공개
- `AGENTS.md` — 계획서 분할 규칙에 "조각을 새로 만들면 `FindUncoveredRanges`의 covered
  목록에 범위를 등록하십시오" 추가, 체크리스트의 단위 테스트 개수 갱신
- `README.md` — 변경 없을 전망. 설정 키도 사용 방법도 바뀌지 않는다

- [ ] **Step 5: 브랜치 마무리**

`superpowers:finishing-a-development-branch`를 쓴다. 워크트리는
`.worktrees/silent-failure-closure`이고 분기점은 `42aaf65`다.
