# 재귀 SP 명세 NOTE 헤더 일관성 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 루트 및 재귀 참조 코드 객체의 `Spec.md`가 같은 NOTE 메타데이터와 최종 신뢰도를 출력하게 한다.

**Architecture:** Core의 단일 문서 포매터가 YAML 점수 헤더와 NOTE를 생성한다. CLI 루트 저장과 재귀 `DependencyAnalysisOrchestrator` 저장이 이 포매터를 공유하며, 재귀 요청은 표시용 모델명과 Effort를 전달한다.

**Tech Stack:** .NET 10, C#, xUnit, NSubstitute

## Global Constraints

- NOTE 형식은 기존 루트 문서의 작성일시, 분석 AI 정보, 최종 신뢰도 및 세부 점수 표기를 유지한다.
- 리뷰 결과가 없으면 YAML과 신뢰도 줄은 생략하되 NOTE의 작성일시와 AI 정보는 출력한다.
- 문서 본문, Thinking 산출물, 의존성 링크 및 캐시 해석 동작을 바꾸지 않는다.
- 변경 전 실패하는 회귀 테스트와 변경 후 전체 테스트·솔루션 빌드를 실행한다.

---

### Task 1: 공통 명세 문서 포매터와 단위 테스트

**Files:**
- Create: `src/ReSet.Core/Services/SpecificationDocumentFormatter.cs`
- Create: `tests/ReSet.Core.Tests/SpecificationDocumentFormatterTests.cs`

**Interfaces:**
- Produces: `SpecificationDocumentFormatter.Format(string specification, ReviewResult? review, string provider, string modelName, string? effort, DateTime timestamp): string`
- Consumes: `ReviewResult.NormalizedScore`, `ScoreAccuracy`, `ScoreCrud`, `ScoreInterface`, `ScoreReadability`, `ScoreException`

- [ ] **Step 1: 실패 테스트를 작성한다.**

```csharp
[Fact]
public void Format_WithReview_WritesRootEquivalentYamlAndNoteHeader()
{
    var review = new ReviewResult { ScoreAccuracy = 10, ScoreCrud = 9, ScoreInterface = 8, ScoreReadability = 7, ScoreException = 6 };
    var result = SpecificationDocumentFormatter.Format("# 본문", review, "OpenAI", "gpt-test", "high", new DateTime(2026, 7, 31, 19, 4, 19));

    Assert.Contains("종합 신뢰도: 80", result);
    Assert.Contains("> [!NOTE]", result);
    Assert.Contains("> **문서 작성일시**: 2026-07-31 19:04:19", result);
    Assert.Contains("> **분석 AI 정보**: OpenAI (gpt-test, Effort: high)", result);
    Assert.Contains("> **AI 최종 신뢰도**: 80/100점 (정합성: 10, CRUD: 9, 연동: 8, 가독성: 7, 예외: 6)", result);
    Assert.EndsWith("# 본문", result);
}
```

- [ ] **Step 2: 실패를 확인한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~SpecificationDocumentFormatterTests.Format_WithReview_WritesRootEquivalentYamlAndNoteHeader`

Expected: `SpecificationDocumentFormatter` 형식을 찾을 수 없어 컴파일 실패.

- [ ] **Step 3: 최소 포매터를 구현한다.**

```csharp
public static string Format(string specification, ReviewResult? review, string provider, string modelName, string? effort, DateTime timestamp)
{
    // review가 있으면 기존 SaveOutputsAsync와 동일한 YAML 및 신뢰도 문자열을 만들고,
    // 항상 NOTE 작성일시·AI 정보를 연결한 뒤 specification을 반환한다.
}
```

`SaveOutputsAsync`의 YAML과 신뢰도 문자열을 문자 단위로 유지한다.

- [ ] **Step 4: 포매터 테스트를 통과시킨다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~SpecificationDocumentFormatterTests.Format_WithReview_WritesRootEquivalentYamlAndNoteHeader`

Expected: PASS, 실패 0건.

- [ ] **Step 5: 커밋한다.**

```bash
git add src/ReSet.Core/Services/SpecificationDocumentFormatter.cs tests/ReSet.Core.Tests/SpecificationDocumentFormatterTests.cs
git commit -m "feat: share specification metadata formatting"
```

### Task 2: 재귀 요청 메타데이터 전달 및 저장 통합

**Files:**
- Modify: `src/ReSet.Core/Services/IDependencyAnalysisOrchestrator.cs:7-19`
- Modify: `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:354-467`
- Modify: `src/ReSet.Cli/Program.cs:611-627,895-911,1397-1458,1605-1639`
- Modify: `tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs:120-164`
- Modify: `tests/ReSet.Core.Tests/CliArgsTests.cs:41-76`

**Interfaces:**
- Consumes: `SpecificationDocumentFormatter.Format(...)` from Task 1.
- Produces: `DependencyAnalysisRequest.ModelName`, `DependencyAnalysisRequest.ActorEffort`.
- Produces: 재귀 `Spec.md`의 루트와 같은 NOTE 헤더.

- [ ] **Step 1: 재귀 산출물 회귀 테스트를 확장한다.**

`AnalyzeAsync_PersistsChildReviewScoreAndThinkingArtifacts`의 요청에 `ModelName = "gpt-test"`, `ActorEffort = "high"`를 설정하고 다음 단언을 추가한다.

```csharp
Assert.Contains("> [!NOTE]", childSpec);
Assert.Contains("> **분석 AI 정보**: OpenAI (gpt-test, Effort: high)", childSpec);
Assert.Contains("> **AI 최종 신뢰도**: 82/100점 (정합성: 9, CRUD: 8, 연동: 7, 가독성: 9, 예외: 8)", childSpec);
```

- [ ] **Step 2: 기존 재귀 문서의 NOTE 누락으로 실패하는지 확인한다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests.AnalyzeAsync_PersistsChildReviewScoreAndThinkingArtifacts`

Expected: FAIL. `> [!NOTE]` 문자열을 찾지 못한다.

- [ ] **Step 3: 재귀 저장을 포매터로 전환한다.**

`DependencyAnalysisRequest`에 `ModelName`과 `ActorEffort` 속성을 추가한다. `BuildPersistedSpecification`은 요청을 받아 아래 호출 결과를 반환하게 한다.

```csharp
SpecificationDocumentFormatter.Format(
    analysis.SpecMarkdown ?? string.Empty, analysis.Review,
    request.Provider, request.ModelName, request.ActorEffort, DateTime.Now)
```

- [ ] **Step 4: 루트 저장도 포매터로 전환하고 CLI 메타데이터를 전달한다.**

`SaveOutputsAsync`의 YAML·NOTE 문자열 조합을 포매터 호출로 대체한다. `RunConfiguredAnalysisAsync`의 매개변수와 두 호출부에 `modelName`, `actorEffort`를 추가하고 생성한 `DependencyAnalysisRequest`에도 값을 설정한다. `CliArgsTests`의 직접 호출은 `"gpt-test"`, `"high"`를 전달한다.

- [ ] **Step 5: 재귀 회귀 및 CLI 테스트를 통과시킨다.**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~DependencyAnalysisOrchestratorTests.AnalyzeAsync_PersistsChildReviewScoreAndThinkingArtifacts|FullyQualifiedName~CliArgsTests"`

Expected: PASS, 실패 0건. 재귀 문서에 NOTE와 82/100점 신뢰도 행이 있다.

- [ ] **Step 6: 커밋한다.**

```bash
git add src/ReSet.Core/Services/IDependencyAnalysisOrchestrator.cs src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs src/ReSet.Cli/Program.cs tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs tests/ReSet.Core.Tests/CliArgsTests.cs
git commit -m "fix: align recursive specification note headers"
```

### Task 3: 전체 검증과 핵심 문서 동기화

**Files:**
- Modify only if stale: `README.md`, `AGENTS.md`, `docs/architecture.md`

**Interfaces:**
- Consumes: Task 1과 Task 2의 코드·테스트.
- Produces: 빌드 가능한 솔루션과 최신 핵심 문서.

- [ ] **Step 1: 전체 테스트를 실행한다.**

Run: `dotnet test`

Expected: PASS, 실패 0건.

- [ ] **Step 2: 솔루션을 복원 없이 빌드한다.**

Run: `dotnet build SettleProcDaily.slnx --no-restore --verbosity minimal`

Expected: 성공, 오류 0건.

- [ ] **Step 3: 핵심 문서를 코드와 대조한다.**

NOTE 메타데이터는 내부 저장 구현 세부사항이다. 기존 문서가 사용자 동작을 충분히 설명하면 변경하지 않고, 설명이 오래된 파일만 최소 수정한다.

- [ ] **Step 4: 문서 변경이 있을 때만 커밋한다.**

```bash
git add README.md AGENTS.md docs/architecture.md
git commit -m "docs: sync recursive specification metadata behavior"
```
