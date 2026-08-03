# 1단계 개별 SP 분석 플로우 견고화 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ReSet.Cli 메뉴 1번(개별 SP 역공학 분석)의 산출물 저장·보고 구간에서 취소 시 산출물 소실, 캐시 히트 시 타임스탬프 위조, 분석 범위 미고지, 무성 저장 실패 네 건을 고친다.

**Architecture:** 네 결함이 공통으로 요구하는 것 — 호출부가 파이프라인의 실제 결과를 알아야 한다 — 을 `SpAnalysisOutcome` 레코드로 모델링한다. 재귀 경로의 산출물 저장 책임을 `DependencyAnalysisOrchestrator.PersistArtifactsAsync`로 일원화하고, `Program`은 `Persistence`/`FromCache` 두 필드만 보고 저장 여부와 보고 내용을 분기한다.

**Tech Stack:** .NET 10, C# (nullable enable, ImplicitUsings enable), xUnit 2.9.3, NSubstitute 5.3.0, Spectre.Console, Serilog

## Global Constraints

- 설계 문서: `docs/superpowers/specs/2026-08-03-stage1-analysis-flow-hardening-design.md`
- **선행 조건**: `docs/superpowers/plans/2026-08-03-verification-honesty-followups.md`(A~E)가 **먼저 병합되어 있어야 한다.** Task 3은 A~E가 만드는 `VerificationDocumentFormatter.FormatSpecification`을 수정한다. A~E 미구현 상태에서 실행하면 그 타입이 존재하지 않는다.
- 모든 주석·로그·사용자 노출 문자열은 한국어로 쓴다. 기존 코드의 어조를 따른다.
- 소프트 페일 정책(`AGENTS.md` 범주 2)을 지킨다. 단, 호출부 결함(빈 DB명 등)은 조용히 삼키지 않고 즉시 드러낸다.
- 전체 테스트 실행 명령: `dotnet test --nologo -v q`
- 작업 시작 시점 기준선: 355개 테스트 통과. 각 태스크 종료 시 이 수가 줄어들면 안 된다.
- 커밋 메시지는 한국어 본문 + 영어 제목(`type: subject`) 형식을 따르고 아래 트레일러로 끝낸다.

  ```
  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  ```

## File Structure

**신규**

- `src/ReSet.Core/Models/SpAnalysisOutcome.cs` — 1단계 분석의 최종 결과 계약과 두 팩토리
- `tests/ReSet.Core.Tests/SpAnalysisOutcomeTests.cs`
- `tests/ReSet.Core.Tests/PipelineTestExtensions.cs` — 프로덕션에서 제거되는 `RunPipelineAsync`의 테스트 전용 대체

**수정**

- `src/ReSet.Core/Models/CodeObjectAnalysisModels.cs` — 열거형 3개, 결과 모델 필드
- `src/ReSet.Core/Services/VerificationBanner.cs` — 참조 미완 배너
- `src/ReSet.Core/Services/VerificationDocumentFormatter.cs` — `분석 범위` YAML 줄 (A~E 산출물)
- `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` — 코어 반환 타입, 캐시 정보, `RunPipelineAsync` 제거
- `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs` — 취소 시 부분 저장, 저장 실패 표면화, 배너·범위·타임스탬프 전달
- `src/ReSet.Cli/Program.cs` — 결과 레코드 배선, 저장 분기, 진단·패널 렌더링, 확인 프롬프트 안내
- 대응 테스트 파일들

---

### Task 1: 결과 계약 도입

**Files:**
- Modify: `src/ReSet.Core/Models/CodeObjectAnalysisModels.cs`
- Create: `src/ReSet.Core/Models/SpAnalysisOutcome.cs`
- Test: `tests/ReSet.Core.Tests/SpAnalysisOutcomeTests.cs`

**Interfaces:**
- Consumes: 기존 `CodeObjectPipelineResult`, `CodeObjectAnalysisResult`, `CodeObjectKey`, `VerificationOutcome`
- Produces: `AnalysisScope`, `GraphCompletion`, `ArtifactPersistence` 열거형. `CodeObjectPipelineResult.Completion`/`.Persistence`/`.PersistenceErrors`/`.FromCache`/`.AnalyzedAt`. `CodeObjectAnalysisResult.FromCache`/`.AnalyzedAt`. `SpAnalysisOutcome`과 정적 팩토리 `FromSingleObjectPipeline(CodeObjectPipelineResult)`, `FromDependencyGraph(CodeObjectPipelineResult, CodeObjectKey)`

- [ ] **Step 1: 실패 테스트 작성**

`tests/ReSet.Core.Tests/SpAnalysisOutcomeTests.cs` 신규 생성.

```csharp
using ReSet.Core.Models;

namespace ReSet.Core.Tests;

public sealed class SpAnalysisOutcomeTests
{
    [Fact]
    public void DefaultValues_AreTheSafeSideOfEachEnum()
    {
        // 대입을 빠뜨린 생성부가 "더 넓게 봤다"거나 "저장했다"고 자칭하지 않아야 한다.
        var outcome = new SpAnalysisOutcome();

        Assert.Equal(AnalysisScope.Transitive, outcome.Scope);
        Assert.Equal(GraphCompletion.Complete, outcome.Completion);
        Assert.Equal(ArtifactPersistence.NotAttempted, outcome.Persistence);
        Assert.Empty(outcome.PersistenceErrors);
    }

    [Fact]
    public void FromSingleObjectPipeline_MarksTransitiveScopeAndLeavesPersistenceToTheCaller()
    {
        var definition = new SpDefinition { Schema = "dbo", Name = "USP_A" };
        var review = new ReviewResult { ScoreAccuracy = 9 };
        var analyzedAt = new DateTime(2026, 8, 1, 14, 22, 3);
        var result = new CodeObjectPipelineResult
        {
            SpecMarkdown = "# 본문",
            SpDef = definition,
            Review = review,
            ThinkingText = "reasoning",
            Outcome = VerificationOutcome.Passed,
            FromCache = true,
            AnalyzedAt = analyzedAt
        };

        var outcome = SpAnalysisOutcome.FromSingleObjectPipeline(result);

        Assert.Equal("# 본문", outcome.SpecMarkdown);
        Assert.Same(definition, outcome.Definition);
        Assert.Same(review, outcome.Review);
        Assert.Equal("reasoning", outcome.ThinkingText);
        Assert.Equal(VerificationOutcome.Passed, outcome.Outcome);
        Assert.Equal(AnalysisScope.Transitive, outcome.Scope);
        Assert.Equal(GraphCompletion.Complete, outcome.Completion);
        Assert.True(outcome.FromCache);
        Assert.Equal(analyzedAt, outcome.AnalyzedAt);
        Assert.Equal(ArtifactPersistence.NotAttempted, outcome.Persistence);
    }

    [Fact]
    public void FromDependencyGraph_MarksDirectScopeAndCarriesGraphPersistence()
    {
        var rootKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Root", CodeObjectType.Procedure);
        var definition = new SpDefinition { ObjectKey = rootKey, Schema = "dbo", Name = "USP_Root" };
        var analyzedAt = new DateTime(2026, 8, 1, 9, 0, 0);
        var result = new CodeObjectPipelineResult
        {
            Completion = GraphCompletion.PartialCancelled,
            Persistence = ArtifactPersistence.Failed,
            PersistenceErrors = { "디스크 쓰기 거부" },
            AnalysisResults =
            {
                new CodeObjectAnalysisResult
                {
                    Key = rootKey,
                    Definition = definition,
                    SpecMarkdown = "# 루트",
                    Outcome = VerificationOutcome.Passed,
                    FromCache = true,
                    AnalyzedAt = analyzedAt
                }
            }
        };

        var outcome = SpAnalysisOutcome.FromDependencyGraph(result, rootKey);

        Assert.Equal("# 루트", outcome.SpecMarkdown);
        Assert.Equal(AnalysisScope.Direct, outcome.Scope);
        Assert.Equal(GraphCompletion.PartialCancelled, outcome.Completion);
        Assert.Equal(ArtifactPersistence.Failed, outcome.Persistence);
        Assert.Equal(new[] { "디스크 쓰기 거부" }, outcome.PersistenceErrors);
        Assert.True(outcome.FromCache);
        Assert.Equal(analyzedAt, outcome.AnalyzedAt);
    }

    [Fact]
    public void FromDependencyGraph_UsesTheRootNodeCacheStateNotAChildsState()
    {
        // 자식이 캐시였다고 루트까지 캐시였다고 말하면 안 된다.
        var rootKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Root", CodeObjectType.Procedure);
        var childKey = CodeObjectKey.Create("PaymentDB", "dbo", "FN_Child", CodeObjectType.Function);
        var result = new CodeObjectPipelineResult
        {
            AnalysisResults =
            {
                new CodeObjectAnalysisResult
                {
                    Key = childKey,
                    SpecMarkdown = "# 자식",
                    FromCache = true,
                    AnalyzedAt = new DateTime(2026, 7, 1, 0, 0, 0)
                },
                new CodeObjectAnalysisResult
                {
                    Key = rootKey,
                    SpecMarkdown = "# 루트",
                    FromCache = false
                }
            }
        };

        var outcome = SpAnalysisOutcome.FromDependencyGraph(result, rootKey);

        Assert.False(outcome.FromCache);
        Assert.Null(outcome.AnalyzedAt);
    }

    [Fact]
    public void FromDependencyGraph_MissingRoot_ReportsNoSpecificationAndNoReview()
    {
        var rootKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Root", CodeObjectType.Procedure);
        var result = new CodeObjectPipelineResult();

        var outcome = SpAnalysisOutcome.FromDependencyGraph(result, rootKey);

        Assert.Null(outcome.SpecMarkdown);
        Assert.Null(outcome.Definition);
        Assert.Null(outcome.Review);
        Assert.Equal(VerificationOutcome.ReviewNotRun, outcome.Outcome);
        Assert.Equal(AnalysisScope.Direct, outcome.Scope);
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~SpAnalysisOutcomeTests`
Expected: 컴파일 실패. `SpAnalysisOutcome`, `AnalysisScope`, `GraphCompletion`, `ArtifactPersistence`, `CodeObjectPipelineResult.Completion` 등이 존재하지 않는다.

- [ ] **Step 3: 열거형과 결과 모델 필드 추가**

`src/ReSet.Core/Models/CodeObjectAnalysisModels.cs`의 `AnalysisNodeStatus` 열거형 **바로 아래**에 세 열거형을 추가한다.

```csharp
/// <summary>루트 객체를 분석할 때 AI가 실제로 본 의존성의 범위.</summary>
public enum AnalysisScope
{
    /// <summary>maxDepth까지 전이 의존성을 포함한다(참조분석 OFF 경로).</summary>
    Transitive,

    /// <summary>직접 의존성만 포함한다(참조분석 ON 경로. 하위 객체는 각자 명세서를 갖는다).</summary>
    Direct
}

/// <summary>의존성 그래프 순회가 끝까지 갔는지 여부. 실행 단위 사실이며 문서 단위가 아니다.</summary>
public enum GraphCompletion
{
    Complete,
    PartialCancelled
}

/// <summary>산출물 저장을 오케스트레이터가 수행했는지, 그리고 성공했는지.</summary>
public enum ArtifactPersistence
{
    /// <summary>오케스트레이터가 저장하지 않았다. 호출부가 저장 책임을 갖는다.</summary>
    NotAttempted,

    Persisted,
    Failed
}
```

`CodeObjectPipelineResult` 클래스 안, `AnalysisResults` 속성 **바로 아래**에 추가한다.

```csharp
    /// <summary>그래프 순회가 사용자 취소로 중단되었는지. 비재귀 경로는 항상 Complete.</summary>
    public GraphCompletion Completion { get; set; }

    /// <summary>오케스트레이터가 산출물을 저장했는지. NotAttempted면 호출부가 저장해야 한다.</summary>
    public ArtifactPersistence Persistence { get; set; }

    public List<string> PersistenceErrors { get; set; } = new();

    /// <summary>이 결과가 AI 호출 없이 캐시에서 나왔는지(단일 객체 경로).</summary>
    public bool FromCache { get; set; }

    /// <summary>캐시에서 나온 경우 원본 문서의 분석 시각. 새로 분석했으면 null.</summary>
    public DateTime? AnalyzedAt { get; set; }
```

`CodeObjectAnalysisResult` 클래스 안, `DdlPath` 속성 **바로 아래**에 추가한다.

```csharp
    /// <summary>이 객체가 AI 호출 없이 캐시에서 나왔는지.</summary>
    public bool FromCache { get; set; }

    /// <summary>캐시에서 나온 경우 원본 문서의 분석 시각. 새로 분석했으면 null.</summary>
    public DateTime? AnalyzedAt { get; set; }
```

- [ ] **Step 4: `SpAnalysisOutcome` 작성**

`src/ReSet.Core/Models/SpAnalysisOutcome.cs` 신규 생성.

```csharp
namespace ReSet.Core.Models;

/// <summary>
/// 1단계 개별 SP 분석의 최종 결과. 호출부(CLI)는 이 레코드 하나만 보고
/// 저장 여부와 보고 내용을 결정한다. 필드 이름이 곧 계약이다.
/// </summary>
public sealed record SpAnalysisOutcome
{
    public string? SpecMarkdown { get; init; }
    public SpDefinition? Definition { get; init; }
    public ReviewResult? Review { get; init; }
    public string? ThinkingText { get; init; }
    public VerificationOutcome Outcome { get; init; }

    public AnalysisScope Scope { get; init; }
    public GraphCompletion Completion { get; init; }
    public bool FromCache { get; init; }
    public DateTime? AnalyzedAt { get; init; }
    public ArtifactPersistence Persistence { get; init; }
    public IReadOnlyList<string> PersistenceErrors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 참조분석 OFF 경로. 단일 객체 파이프라인 결과를 옮긴다.
    /// 저장은 호출부가 하므로 Persistence는 NotAttempted다.
    /// </summary>
    public static SpAnalysisOutcome FromSingleObjectPipeline(CodeObjectPipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new SpAnalysisOutcome
        {
            SpecMarkdown = result.SpecMarkdown,
            Definition = result.SpDef,
            Review = result.Review,
            ThinkingText = result.ThinkingText,
            Outcome = result.Outcome,
            Scope = AnalysisScope.Transitive,
            Completion = GraphCompletion.Complete,
            FromCache = result.FromCache,
            AnalyzedAt = result.AnalyzedAt,
            Persistence = ArtifactPersistence.NotAttempted
        };
    }

    /// <summary>
    /// 참조분석 ON 경로. 그래프에서 루트 분석 결과를 찾아 옮긴다.
    /// 캐시 상태는 루트 노드의 것이다 — 노드마다 다른 값을 하나로 접으면
    /// 어느 쪽으로 접어도 거짓이 된다.
    /// </summary>
    public static SpAnalysisOutcome FromDependencyGraph(
        CodeObjectPipelineResult result,
        CodeObjectKey rootKey)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rootKey);

        var root = result.AnalysisResults.FirstOrDefault(analysis => analysis.Key == rootKey);

        return new SpAnalysisOutcome
        {
            SpecMarkdown = root?.SpecMarkdown,
            Definition = root?.Definition,
            Review = root?.Review,
            ThinkingText = root?.ThinkingText,
            Outcome = root?.Outcome ?? VerificationOutcome.ReviewNotRun,
            Scope = AnalysisScope.Direct,
            Completion = result.Completion,
            FromCache = root?.FromCache ?? false,
            AnalyzedAt = root?.AnalyzedAt,
            Persistence = result.Persistence,
            PersistenceErrors = result.PersistenceErrors.ToArray()
        };
    }
}
```

`CodeObjectAnalysisResult.Definition`은 `new()` 기본값을 갖는 비-nullable이므로 `root?.Definition`은 `SpDefinition?`로 추론된다. `SpAnalysisOutcome.Definition`이 nullable이라 그대로 대입된다.

- [ ] **Step 5: 테스트 통과 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~SpAnalysisOutcomeTests`
Expected: PASS (5개)

- [ ] **Step 6: 전체 테스트 확인**

Run: `dotnet test --nologo -v q`
Expected: 360 통과, 0 실패

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Models/CodeObjectAnalysisModels.cs \
        src/ReSet.Core/Models/SpAnalysisOutcome.cs \
        tests/ReSet.Core.Tests/SpAnalysisOutcomeTests.cs
git commit -F - <<'EOF'
feat(analysis): introduce the SpAnalysisOutcome result contract

1단계 분석의 반환 튜플을 대체할 레코드와 세 열거형을 추가한다.
호출부가 캐시 히트·저장 책임·그래프 완결성·분석 범위를 알 수 있어야
남은 네 결함을 고칠 수 있다.

열거형의 0번 값은 대입을 빠뜨렸을 때 안전한 쪽에 둔다. AnalysisScope는
경로마다 안전한 방향이 달라 0번 값으로 막을 수 없으므로 생성부를 두
팩토리로 한정하고 테스트로 고정한다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 2: 참조 미완 배너

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationBanner.cs`
- Test: `tests/ReSet.Core.Tests/VerificationBannerTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `VerificationBanner.UnresolvedReferences(IReadOnlyList<string> objectNames) -> string`

- [ ] **Step 1: 실패 테스트 작성**

`tests/ReSet.Core.Tests/VerificationBannerTests.cs` 끝의 마지막 `}` **직전**에 추가한다.

```csharp
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
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~VerificationBannerTests`
Expected: 컴파일 실패. `UnresolvedReferences`가 존재하지 않는다.

- [ ] **Step 3: 배너 구현**

`src/ReSet.Core/Services/VerificationBanner.cs`의 `ReviewNotRun` 메서드 **아래**에 추가한다.

```csharp
    /// <summary>
    /// 사용자 취소로 이 문서의 참조 객체 일부가 분석되지 않았음을 알린다.
    /// 개수 대신 이름을 싣는다 — 읽는 사람이 다음에 할 일이 그 객체를 다시
    /// 분석하는 것이기 때문이다.
    /// </summary>
    public static string UnresolvedReferences(IReadOnlyList<string> objectNames)
    {
        var nameLines = objectNames is { Count: > 0 }
            ? string.Join("\n", objectNames.Select(name => $">   - {name}"))
            : ">   - (미분석 객체명이 기록되지 않았습니다.)";

        return "\n> [!CAUTION]\n> **[참조 미완] 사용자 취소로 아래 참조 객체가 분석되지 않았습니다.**\n"
            + nameLines
            + "\n\n";
    }
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~VerificationBannerTests`
Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationBanner.cs \
        tests/ReSet.Core.Tests/VerificationBannerTests.cs
git commit -F - <<'EOF'
feat(verification): add the unresolved-references banner

취소된 실행에서 순환 의존성이나 깊이 재등록 때문에 성공한 문서의 참조
목록에 미분석 객체가 남는 경우를 문서 상단에 알린다. L1Exhausted의 오류
목록과 같은 형태를 쓴다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 3: 포매터에 `분석 범위` 추가

> **선행 조건 확인:** 이 태스크는 A~E가 만든 `src/ReSet.Core/Services/VerificationDocumentFormatter.cs`를 수정한다. 파일이 없다면 A~E가 아직 병합되지 않은 것이다. 진행하지 말고 보고하라.

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationDocumentFormatter.cs`
- Test: `tests/ReSet.Core.Tests/VerificationDocumentFormatterTests.cs`

**Interfaces:**
- Consumes: `AnalysisScope` (Task 1)
- Produces: `VerificationDocumentFormatter.FormatSpecification(..., AnalysisScope? scope = null)`. 기존 호출부는 인자를 추가하지 않아도 컴파일된다.

- [ ] **Step 1: 실패 테스트 작성**

`tests/ReSet.Core.Tests/VerificationDocumentFormatterTests.cs` 끝의 마지막 `}` **직전**에 추가한다.

```csharp
    [Fact]
    public void FormatSpecification_WithoutScope_OmitsTheScopeLine()
    {
        var result = VerificationDocumentFormatter.FormatSpecification(
            "# 본문", null, VerificationOutcome.Passed,
            "OpenAI", "gpt-test", null, new DateTime(2026, 8, 3, 10, 0, 0));

        Assert.DoesNotContain("분석 범위", result);
    }

    [Fact]
    public void FormatSpecification_DirectScope_WritesTheRecursiveModeLabel()
    {
        var result = VerificationDocumentFormatter.FormatSpecification(
            "# 본문", null, VerificationOutcome.Passed,
            "OpenAI", "gpt-test", null, new DateTime(2026, 8, 3, 10, 0, 0),
            AnalysisScope.Direct);

        Assert.Contains("분석 범위: 직접 의존성", result);
    }

    [Fact]
    public void FormatSpecification_TransitiveScope_WritesTheSingleObjectLabel()
    {
        var result = VerificationDocumentFormatter.FormatSpecification(
            "# 본문", null, VerificationOutcome.Passed,
            "OpenAI", "gpt-test", null, new DateTime(2026, 8, 3, 10, 0, 0),
            AnalysisScope.Transitive);

        Assert.Contains("분석 범위: 전이 의존성", result);
    }

    [Fact]
    public void FormatSpecification_ScopeLineLivesInsideTheYamlBlockAlongsideScores()
    {
        var review = new ReviewResult
        {
            ScoreAccuracy = 10, ScoreCrud = 9, ScoreInterface = 8,
            ScoreReadability = 7, ScoreException = 6
        };

        var result = VerificationDocumentFormatter.FormatSpecification(
            "# 본문", review, VerificationOutcome.Passed,
            "OpenAI", "gpt-test", null, new DateTime(2026, 8, 3, 10, 0, 0),
            AnalysisScope.Direct);

        var yamlEnd = result.IndexOf("\n---", 3, StringComparison.Ordinal);
        var yaml = result[..yamlEnd];

        Assert.Contains("검증 상태: 통과", yaml);
        Assert.Contains("분석 범위: 직접 의존성", yaml);
        Assert.Contains("종합 신뢰도: 80", yaml);
    }
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~VerificationDocumentFormatterTests`
Expected: 컴파일 실패. `FormatSpecification`이 8번째 인자를 받지 않는다.

- [ ] **Step 3: 포매터 수정**

`VerificationDocumentFormatter.FormatSpecification`의 시그니처 끝에 선택적 파라미터를 추가하고, 공유 private 코어까지 전달한다.

```csharp
    public static string FormatSpecification(
        string body, ReviewResult? review, VerificationOutcome outcome,
        string provider, string modelName, string? effort, DateTime timestamp,
        AnalysisScope? scope = null) =>
        FormatCore(body, review, outcome, provider, modelName, effort, timestamp,
            SpecificationLabels, scope);
```

private 코어에서 `검증 상태` 줄 **바로 다음**에 범위 줄을 만든다. `scoreLines` 앞에 놓아야 `종합 신뢰도`보다 위에 온다.

```csharp
        var scopeLine = scope switch
        {
            AnalysisScope.Direct => "\n분석 범위: 직접 의존성 # 참조 SP/UDF 재귀 분석 모드",
            AnalysisScope.Transitive => "\n분석 범위: 전이 의존성 # 단일 객체 분석 모드",
            _ => string.Empty
        };

        var yamlFrontMatter = $@"---
검증 상태: {statusLabel} # 검증 파이프라인 종료 상태{scopeLine}{scoreLines}
---

";
```

`FormatConsolidatedPlan`과 `FormatUnverifiedPlan`은 코어에 `scope: null`을 넘긴다. 계획서에는 분석 범위 개념이 없다.

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~VerificationDocumentFormatterTests`
Expected: PASS

- [ ] **Step 5: 전체 테스트 확인**

Run: `dotnet test --nologo -v q`
Expected: 실패 0. 기존 포매터 테스트가 `분석 범위` 없이 그대로 통과해야 한다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationDocumentFormatter.cs \
        tests/ReSet.Core.Tests/VerificationDocumentFormatterTests.cs
git commit -F - <<'EOF'
feat(verification): record the analysis scope in the specification header

참조분석 ON/OFF에 따라 루트 SP가 본 의존성 범위가 달라지는데 문서에
그 사실이 남지 않았다. YAML에 한 줄로 기록한다.

그래프 완결성은 싣지 않는다. 그것은 실행 단위 사실이라 문서 단위
헤더에 넣으면 어긋난다 — 취소된 실행에서도 저장된 문서 각각은 완전하다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 4: 파이프라인이 캐시 정보를 결과에 싣는다

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: `CodeObjectPipelineResult.FromCache`/`.AnalyzedAt` (Task 1)
- Produces: `RunCodeObjectPipelineCoreAsync`가 튜플 대신 `CodeObjectPipelineResult`를 반환한다. 캐시 히트 시 `FromCache = true`, `AnalyzedAt`에 원본 `문서 작성일시`가 실린다. `ParseCachedSpecification`이 3-튜플 `(string Specification, ReviewResult Review, DateTime? AnalyzedAt)`을 반환한다. `ResolveCurrentDatabase`가 `public static`이 된다.

- [ ] **Step 1: 실패 테스트 작성**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`의 `RunPipelineAsync_CacheHit_ReturnsUndecoratedBodyAndPreservesReviewScores` **바로 아래**에 추가한다. 이 테스트는 튜플 대신 `RunCodeObjectPipelineAsync`를 직접 호출한다.

```csharp
        [Fact]
        public async Task RunCodeObjectPipelineAsync_CacheHit_ReportsCacheReuseAndTheOriginalAnalysisTimestamp()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var cacheManager = Substitute.For<ICacheManager>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService,
                aiService,
                new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(),
                "1",
                "gpt-4",
                cacheManager,
                aiService,
                aiService);
            var key = CodeObjectKey.Create(
                "PaymentDB", "dbo", "USP_CacheStamp", CodeObjectType.Procedure);
            var definition = new SpDefinition
            {
                ObjectKey = key,
                Schema = key.Schema,
                Name = key.Name
            };
            dbService.GetCodeObjectDetailsAsync(
                    Arg.Any<string>(), key, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(definition);
            cacheManager.ComputeCompositeHash(definition, 3).Returns("hash");
            cacheManager.IsCacheValid(key, "hash", Arg.Any<OutputPathResolver>()).Returns(true);

            var outputRoot = Path.Combine(
                Path.GetTempPath(), $"ReSet-CacheStamp-{Guid.NewGuid():N}");
            var specPath = new OutputPathResolver(key.Database, outputRoot).ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            await File.WriteAllTextAsync(
                specPath,
                """
                ---
                검증 상태: 통과
                종합 신뢰도: 78
                ---

                > [!NOTE]
                > **문서 작성일시**: 2026-08-01 14:22:03
                > **분석 AI 정보**: OpenAI

                ## 개요
                cached body
                """);

            try
            {
                var result = await orchestrator.RunCodeObjectPipelineAsync(
                    "Server=(local);Database=PaymentDB",
                    key,
                    3,
                    "OpenAI",
                    "rules",
                    isBatchMode: true,
                    outputRoot,
                    enableCache: true);

                Assert.True(result.FromCache);
                Assert.Equal(new DateTime(2026, 8, 1, 14, 22, 3), result.AnalyzedAt);
                Assert.StartsWith("## 개요", result.SpecMarkdown);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_CacheHitWithUnparsableTimestamp_LeavesAnalyzedAtNull()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var cacheManager = Substitute.For<ICacheManager>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService,
                aiService,
                new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(),
                "1",
                "gpt-4",
                cacheManager,
                aiService,
                aiService);
            var key = CodeObjectKey.Create(
                "PaymentDB", "dbo", "USP_CacheNoStamp", CodeObjectType.Procedure);
            var definition = new SpDefinition
            {
                ObjectKey = key,
                Schema = key.Schema,
                Name = key.Name
            };
            dbService.GetCodeObjectDetailsAsync(
                    Arg.Any<string>(), key, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(definition);
            cacheManager.ComputeCompositeHash(definition, 3).Returns("hash");
            cacheManager.IsCacheValid(key, "hash", Arg.Any<OutputPathResolver>()).Returns(true);

            var outputRoot = Path.Combine(
                Path.GetTempPath(), $"ReSet-CacheNoStamp-{Guid.NewGuid():N}");
            var specPath = new OutputPathResolver(key.Database, outputRoot).ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            await File.WriteAllTextAsync(
                specPath,
                """
                ---
                검증 상태: 통과
                ---

                > [!NOTE]
                > **분석 AI 정보**: OpenAI

                ## 개요
                cached body
                """);

            try
            {
                var result = await orchestrator.RunCodeObjectPipelineAsync(
                    "Server=(local);Database=PaymentDB",
                    key,
                    3,
                    "OpenAI",
                    "rules",
                    isBatchMode: true,
                    outputRoot,
                    enableCache: true);

                Assert.True(result.FromCache);
                Assert.Null(result.AnalyzedAt);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~RunCodeObjectPipelineAsync_CacheHit`
Expected: 컴파일 실패. `CodeObjectPipelineResult.FromCache`는 Task 1에서 생겼으므로 컴파일은 되지만 `Assert.True(result.FromCache)`가 FAIL한다.

- [ ] **Step 3: `ParseCachedSpecification`이 작성일시를 함께 반환하게 수정**

`VerificationPipelineOrchestrator.cs`의 `ParseCachedSpecification` 시그니처와 본문을 고친다. NOTE 블록을 지우는 정규식보다 **먼저** 파싱해야 한다.

```csharp
        private static (string Specification, ReviewResult Review, DateTime? AnalyzedAt)
            ParseCachedSpecification(string cachedArtifact)
        {
```

YAML 블록 처리 직후, `> [!NOTE]` 제거 정규식 **직전**에 삽입한다.

```csharp
            // NOTE 블록을 지우기 전에 원본 분석 시각을 확보한다. 캐시 히트는 AI를
            // 호출하지 않았으므로 이 값을 그대로 다시 써야 새 날짜가 찍히지 않는다.
            DateTime? analyzedAt = null;
            var stampMatch = Regex.Match(
                specification,
                @"(?m)^>\s*\*\*문서 작성일시\*\*:\s*(?<stamp>[^\r\n]+?)\s*$");
            if (stampMatch.Success &&
                DateTime.TryParse(
                    stampMatch.Groups["stamp"].Value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsedStamp))
            {
                analyzedAt = parsedStamp;
            }
            else
            {
                // A가 레거시 캐시를 전량 무효화하므로 히트하는 문서는 반드시 신형
                // 포맷이다. 여기 도달했다면 포매터 출력이 깨졌다는 뜻이고, 그 사실이
                // 날짜보다 중요하다.
                Log.Warning("[파이프라인] 캐시 문서에서 작성일시를 읽지 못했습니다.");
            }
```

마지막 `return` 문을 고친다.

```csharp
            return (specification.TrimStart('\r', '\n'), review, analyzedAt);
```

- [ ] **Step 4: 코어가 `CodeObjectPipelineResult`를 반환하게 수정**

`RunCodeObjectPipelineCoreAsync`의 반환 타입을 바꾼다.

```csharp
        private async Task<CodeObjectPipelineResult> RunCodeObjectPipelineCoreAsync(
```

`var verificationOutcome = VerificationOutcome.Passed;` 선언 **바로 아래**에 로컬 함수를 추가한다. 이 함수는 호출 시점의 `verificationOutcome` 값을 읽는다.

```csharp
            // 9곳의 반환 지점이 같은 형태를 쓰도록 모은다. verificationOutcome은
            // 호출 시점 값이 읽히므로 각 지점에서 따로 넘기지 않는다.
            CodeObjectPipelineResult Result(
                string? spec,
                SpDefinition? definition,
                ReviewResult? review,
                string? thinking,
                bool fromCache = false,
                DateTime? analyzedAt = null) => new()
            {
                SpecMarkdown = spec,
                SpDef = definition,
                Review = review,
                ThinkingText = thinking,
                Outcome = verificationOutcome,
                FromCache = fromCache,
                AnalyzedAt = analyzedAt
            };
```

9개 반환 지점을 모두 바꾼다.

| 기존 | 변경 후 |
|---|---|
| `return (null, null, null, null, verificationOutcome);` (메타데이터 수집 실패) | `return Result(null, null, null, null);` |
| `return (cachedSpec, spDef, cachedReview, null, verificationOutcome);` (캐시 히트) | `return Result(cachedSpec, spDef, cachedReview, null, fromCache: true, analyzedAt: cachedAnalyzedAt);` |
| `return (null, spDef, null, null, verificationOutcome);` (dynamic 후보 생성 실패) | `return Result(null, spDef, null, null);` |
| `return (null, spDef, null, null, verificationOutcome);` (Critic 검토 실패) | `return Result(null, spDef, null, null);` |
| `return (null, spDef, null, null, verificationOutcome);` (dynamic 합성 실패) | `return Result(null, spDef, null, null);` |
| `return (null, spDef, null, null, verificationOutcome);` (단일 생성 실패) | `return Result(null, spDef, null, null);` |
| `return (specificationMarkdown, spDef, finalReview, accumulatedThinking.ToString(), verificationOutcome);` (L3 승인) | `return Result(specificationMarkdown, spDef, finalReview, accumulatedThinking.ToString());` |
| `return (null, spDef, null, null, verificationOutcome);` (L3 취소) | `return Result(null, spDef, null, null);` |
| `return (specificationMarkdown, spDef, finalReview, accumulatedThinking.ToString(), verificationOutcome);` (배치 모드 종료) | `return Result(specificationMarkdown, spDef, finalReview, accumulatedThinking.ToString());` |

캐시 히트 지점의 구조분해도 3-튜플로 바꾼다.

```csharp
                            var (cachedSpec, cachedReview, cachedAnalyzedAt) =
                                ParseCachedSpecification(cachedArtifact);
                            return Result(
                                cachedSpec, spDef, cachedReview, null,
                                fromCache: true, analyzedAt: cachedAnalyzedAt);
```

`RunCodeObjectPipelineAsync`는 코어 결과를 그대로 반환하게 단순화한다.

```csharp
        public async Task<CodeObjectPipelineResult> RunCodeObjectPipelineAsync(
            string connectionString,
            CodeObjectKey key,
            int maxDepth,
            string provider,
            string instructions,
            bool isBatchMode,
            string outputDirectory,
            bool enableCache = false,
            CancellationToken cancellationToken = default,
            bool directDependenciesOnly = false,
            bool includeExternalCodeObjects = true,
            string? analysisDatabase = null) =>
            await RunCodeObjectPipelineCoreAsync(
                connectionString,
                key,
                maxDepth,
                provider,
                instructions,
                isBatchMode,
                outputDirectory,
                enableCache,
                cancellationToken,
                directDependenciesOnly,
                includeExternalCodeObjects,
                analysisDatabase);
```

`RunPipelineAsync`는 아직 남겨둔다(Task 8에서 이전). 코어 반환 타입이 바뀌었으므로 본문만 고친다.

```csharp
            var result = await RunCodeObjectPipelineAsync(
                connectionString, key, maxDepth, provider, instructions, isBatchMode,
                outputDirectory, enableCache, cancellationToken);

            return (result.SpecMarkdown, result.SpDef, result.Review, result.ThinkingText, result.Outcome);
```

`ResolveCurrentDatabase`를 `public static`으로 바꾼다. Task 8과 Task 9가 이 로직을 그대로 써야 하고, 복제하면 동작이 갈라진다.

```csharp
        /// <summary>연결 문자열의 InitialCatalog를 꺼낸다. 없거나 파싱 불가면 null.</summary>
        public static string? ResolveCurrentDatabase(string connectionString)
```

- [ ] **Step 5: 테스트 통과 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~RunCodeObjectPipelineAsync_CacheHit`
Expected: PASS (2개)

- [ ] **Step 6: 전체 테스트 확인**

Run: `dotnet test --nologo -v q`
Expected: 실패 0. 기존 `RunPipelineAsync_*` 40여 개가 그대로 통과해야 한다.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs \
        tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -F - <<'EOF'
feat(verification): carry cache provenance out of the pipeline

캐시 히트는 AI를 호출하지 않았는데도 호출부가 그 사실을 알 수 없어
문서에 새 작성일시가 찍혔다. 파이프라인 코어가 튜플 대신
CodeObjectPipelineResult를 반환하게 하고 FromCache와 원본 작성일시를
싣는다.

ParseCachedSpecification은 NOTE 블록을 지우기 전에 작성일시를 파싱한다.
ResolveCurrentDatabase는 호출부 두 곳이 같은 로직을 쓰도록 공개한다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 5: 취소 시 완료분 저장

**Files:**
- Modify: `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:49-80`
- Test: `tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`

**Interfaces:**
- Consumes: `GraphCompletion` (Task 1)
- Produces: `AnalyzeAsync`가 취소 시 `OperationCanceledException`을 던지지 않고 `Completion = GraphCompletion.PartialCancelled`인 결과를 반환한다. 빈 DB명이면 `ArgumentException`을 던진다.

- [ ] **Step 1: 실패 테스트 작성**

`tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`의 마지막 `[Fact]` 아래, private 헬퍼 **위**에 추가한다.

```csharp
    [Fact]
    public async Task AnalyzeAsync_CancelledMidGraph_PersistsCompletedObjectsAndReportsPartialCompletion()
    {
        // 완료된 객체의 AI 비용이 취소로 버려지면 안 된다.
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Cancel-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var doneChild = Key("FN_Done", CodeObjectType.Function);
        var cancelledChild = Key("FN_Cancelled", CodeObjectType.Function);
        var metadata = CreateMetadataService(
            Definition(root, doneChild, cancelledChild),
            Definition(doneChild),
            Definition(cancelledChild));
        using var cts = new CancellationTokenSource();
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) =>
            {
                if (key == cancelledChild)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }

                return Task.FromResult(PipelineResult(key));
            });

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot),
                cts.Token);

            Assert.Equal(GraphCompletion.PartialCancelled, result.Completion);
            Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(doneChild).Status);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            Assert.True(File.Exists(paths.ResolveSpecPath(doneChild)));
            Assert.False(File.Exists(paths.ResolveSpecPath(cancelledChild)));
            Assert.False(File.Exists(paths.ResolveSpecPath(root)));
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_CompletedGraph_ReportsCompleteCompletion()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Complete-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot),
                CancellationToken.None);

            Assert.Equal(GraphCompletion.Complete, result.Completion);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyRootDatabase_ThrowsInsteadOfSilentlySkippingAllArtifacts()
    {
        // 빈 DB명은 OutputPathResolver 생성을 막아 모든 산출물을 조용히
        // 사라지게 했다. 호출부 결함이므로 즉시 드러낸다.
        var root = CodeObjectKey.Create("", "dbo", "USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService();
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => sut.AnalyzeAsync(root, Request(), CancellationToken.None));

        Assert.Contains("데이터베이스", exception.Message);
    }
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests`
Expected: `AnalyzeAsync_CancelledMidGraph_*`가 `OperationCanceledException`으로 FAIL. `AnalyzeAsync_EmptyRootDatabase_*`가 `ArgumentException`이 아닌 다른 결과로 FAIL.

- [ ] **Step 3: `AnalyzeAsync` 수정**

`src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs`의 `AnalyzeAsync` 전체를 교체한다.

```csharp
    public async Task<CodeObjectPipelineResult> AnalyzeAsync(
        CodeObjectKey rootKey,
        DependencyAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(request);

        // 빈 DB명은 OutputPathResolver 생성을 막아 모든 산출물 저장을 조용히
        // 무산시킨다. 호출부 결함이므로 폴백하지 않고 즉시 드러낸다.
        if (string.IsNullOrWhiteSpace(rootKey.Database))
        {
            throw new ArgumentException(
                "분석 기준 데이터베이스를 확인할 수 없어 산출물 경로를 계산할 수 없습니다.",
                nameof(rootKey));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 호출자가 무엇을 넣었든 루트 객체의 DB가 분석 기준이 된다.
        // 캐시 판정(VerificationPipelineOrchestrator)과 최종 저장(PersistArtifactsAsync)이
        // 같은 OutputPathResolver 기준을 쓰도록 보장하는 지점이다.
        var effectiveRequest = request with { AnalysisDatabase = rootKey.Database };

        var execution = new ExecutionState(rootKey.Database);
        var completion = GraphCompletion.Complete;

        try
        {
            await DiscoverAsync(rootKey, 0, effectiveRequest, execution, cancellationToken);

            // 호출부 표기(sys.sql_expression_dependencies·AST)가 아니라 카탈로그의 실제 객체명을
            // 그래프의 단일 표기로 확정한다. 파이프라인 실행 전에 적용해야 캐시 키와 산출물 경로가
            // 호출한 SP마다 갈라지지 않는다.
            execution.ApplyCanonicalKeys();
            await ExecuteDiscoveredNodesAsync(effectiveRequest, execution, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 취소를 예외로 흘려보내면 "완료분은 저장됐다"는 사실이 호출부에
            // 도달하지 못한다. 결과 레코드가 계약이므로 상태로 바꾼다.
            completion = GraphCompletion.PartialCancelled;
            Log.Information(
                "[의존성 분석] 사용자 취소 - 완료된 객체만 저장합니다: {ObjectKey}",
                rootKey.CanonicalName);
        }

        var result = new CodeObjectPipelineResult
        {
            Nodes = execution.Nodes.Values.ToList(),
            DependencyEdges = execution.Edges,
            AnalysisResults = execution.AnalysisResults,
            Completion = completion
        };

        // 취소된 토큰을 그대로 넘기면 저장부의 ThrowIfCancellationRequested가
        // 즉시 던져 아무것도 쓰지 못한다. CancellationToken.None은 네트워크
        // 드라이브에서 무한정 매달릴 수 있으므로 상한을 둔다.
        using var persistCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await PersistArtifactsAsync(rootKey, effectiveRequest, result, persistCts.Token);
        return result;
    }
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests`
Expected: PASS

- [ ] **Step 5: 전체 테스트 확인**

Run: `dotnet test --nologo -v q`
Expected: 실패 0

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs \
        tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs
git commit -F - <<'EOF'
fix(analysis): persist completed objects when the graph run is cancelled

AnalyzeAsync는 그래프 실행을 전부 마친 뒤에야 디스크에 썼고, 취소는
그 앞에서 예외로 빠져나갔다. 객체 20개 중 19번째에 취소하면 이미 AI
호출과 L3 승인을 마친 18개가 한 줄도 남지 않았다.

취소를 GraphCompletion.PartialCancelled 상태로 바꾸고 30초 grace 토큰으로
저장을 마친 뒤 결과를 반환한다. 빈 DB명은 모든 산출물을 조용히 무산시키던
호출부 결함이므로 진입부에서 드러낸다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 6: 저장 실패 표면화와 Thinking 헤더 보강

**Files:**
- Modify: `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:368-499`
- Test: `tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`

**Interfaces:**
- Consumes: `ArtifactPersistence` (Task 1)
- Produces: `PersistArtifactsAsync`가 `graph.Persistence`와 `graph.PersistenceErrors`를 채운다. `PersistThinkingAsync`가 `DependencyAnalysisRequest`를 받아 provider/model/effort/작성일시 헤더를 쓴다.

- [ ] **Step 1: 실패 테스트 작성**

`tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`에 추가한다.

```csharp
    [Fact]
    public async Task AnalyzeAsync_ArtifactRootUnwritable_ReportsPersistenceFailureInsteadOfLoggingSilently()
    {
        // 저장이 통째로 실패했는데 화면에 성공 패널이 뜨던 결함.
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-PersistFail-{Guid.NewGuid():N}");
        // 출력 루트 자리에 파일을 만들어 하위 디렉터리 생성을 실패시킨다.
        await File.WriteAllTextAsync(outputRoot, "not a directory");

        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot),
                CancellationToken.None);

            Assert.Equal(ArtifactPersistence.Failed, result.Persistence);
            Assert.NotEmpty(result.PersistenceErrors);
        }
        finally
        {
            if (File.Exists(outputRoot)) File.Delete(outputRoot);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_SuccessfulRun_ReportsPersisted()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Persisted-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot),
                CancellationToken.None);

            Assert.Equal(ArtifactPersistence.Persisted, result.Persistence);
            Assert.Empty(result.PersistenceErrors);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_ThinkingLogCarriesTheAnalysisModelIdentity()
    {
        // 재귀 모드의 하위 객체 Thinking.md가 루트보다 정보가 적을 이유가 없다.
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Thinking-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
            {
                SpDef = Definition(key),
                SpecMarkdown = "# Spec",
                ThinkingText = "private reasoning"
            }));

        try
        {
            await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot, modelName: "gpt-test", actorEffort: "high"),
                CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var thinkingPath = Path.Combine(paths.ResolveDocsDirectory(root), "Thinking.md");
            var thinking = await File.ReadAllTextAsync(thinkingPath);

            Assert.Contains("**기본 분석 AI 정보**: OpenAI (gpt-test, Effort: high)", thinking);
            Assert.Contains("**문서 작성일시**:", thinking);
            Assert.Contains("private reasoning", thinking);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests`
Expected: 세 테스트 FAIL. `Persistence`가 항상 `NotAttempted`이고 Thinking 헤더에 모델 정보가 없다.

- [ ] **Step 3: `PersistArtifactsAsync` 수정**

성공을 낙관하지 말고 끝까지 도달했을 때만 `Persisted`로 표시한다. `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs`의 `PersistArtifactsAsync`에서 `try` 블록의 마지막 `foreach` **종료 직후**에 성공 표시를 넣고, 두 catch 블록을 아래 형태로 **교체**한다.

```csharp
            // 노드 하나라도 저장에 실패했으면 전체를 Failed로 부른다. 사용자가
            // 알아야 하는 것은 "일부가 디스크에 없다"는 사실이고, 어느 노드인지는
            // PersistenceErrors와 노드 Status가 말해 준다.
            graph.Persistence = graph.PersistenceErrors.Count > 0
                ? ArtifactPersistence.Failed
                : ArtifactPersistence.Persisted;
        }
        catch (OperationCanceledException)
        {
            // AnalyzeAsync가 30초 grace 토큰을 넘기므로 이 취소는 사용자 Ctrl+C가
            // 아니라 저장 제한 시간 초과다. 다시 던지면 호출부가 결과를 못 받아
            // "저장에 실패했다"는 사실조차 전달되지 않으므로 상태로 바꾼다.
            graph.Persistence = ArtifactPersistence.Failed;
            graph.PersistenceErrors.Add("저장 제한 시간(30초)을 초과했습니다.");
            Log.Warning("[의존성 분석] 저장 제한 시간 초과: {ObjectKey}", rootKey.CanonicalName);
        }
        catch (Exception ex)
        {
            graph.Persistence = ArtifactPersistence.Failed;
            graph.PersistenceErrors.Add(ex.Message);
            Log.Warning(ex, "[의존성 분석] 객체 아티팩트 저장 중 오류가 발생했습니다: {ObjectKey}", rootKey.CanonicalName);
        }
```

개별 노드 저장 실패는 기존 do/while 재링크 루프가 계속 처리한다. 그 실패도 사유를 남긴다 — `MarkFailed(node, ex, "명세서 파일 저장")` 호출 **직후**에 추가한다.

```csharp
                        graph.PersistenceErrors.Add($"{analysis.Key.Schema}.{analysis.Key.Name}: {ex.Message}");
```

이 경우 루프는 계속 돌아 마지막 판정에 도달하고, `PersistenceErrors`가 비어 있지 않으므로 `Failed`가 된다. Step 1의 `AnalyzeAsync_ArtifactRootUnwritable_*` 테스트가 밟는 경로가 바로 이것이다 — 출력 루트 자리에 파일이 있으면 `Directory.CreateDirectory`가 `IOException`을 던지고 이 안쪽 catch가 받는다.

- [ ] **Step 4: `PersistThinkingAsync` 헤더 보강**

시그니처에 `request`를 추가하고 헤더를 `SaveOutputsAsync` 수준으로 맞춘다.

```csharp
    private static async Task PersistThinkingAsync(
        CodeObjectAnalysisResult analysis,
        OutputPathResolver paths,
        DependencyAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(analysis.ThinkingText))
        {
            return;
        }

        try
        {
            var thinkingPath = Path.Combine(
                paths.ResolveDocsDirectory(analysis.Key),
                "Thinking.md");
            var effortSuffix = string.IsNullOrWhiteSpace(request.ActorEffort)
                ? string.Empty
                : $", Effort: {request.ActorEffort}";
            var header =
                "# AI 추론 과정 로그 (Thinking Process Log)\n\n" +
                $"- **기본 분석 AI 정보**: {request.Provider} ({request.ModelName}{effortSuffix})\n" +
                $"- **문서 작성일시**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n" +
                "본 문서는 저장 프로시저 역공학 및 검증 파이프라인 수행 중 사용된 AI 모델들의 추론 과정(Thinking Process)을 기록한 마크다운 문서입니다.\n\n" +
                "---\n\n";

            await File.WriteAllTextAsync(
                thinkingPath,
                header + analysis.ThinkingText,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(
                ex,
                "[의존성 분석] 추론 로그 저장 실패 (계속 진행): {ObjectKey}",
                analysis.Key.CanonicalName);
        }
    }
```

호출부도 고친다.

```csharp
                await PersistThinkingAsync(analysis, paths, request, cancellationToken);
```

- [ ] **Step 5: 테스트 통과 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests`
Expected: PASS

- [ ] **Step 6: 전체 테스트 확인**

Run: `dotnet test --nologo -v q`
Expected: 실패 0

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs \
        tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs
git commit -F - <<'EOF'
fix(analysis): surface artifact persistence failures to the caller

PersistArtifactsAsync 전체가 하나의 try로 감싸여 최종 catch가 로그만
남겼다. OutputPathResolver 생성이 실패하면 모든 하위 명세서가 하나도
기록되지 않는데 화면에는 아무 표시가 없었다.

저장 결과를 graph.Persistence와 PersistenceErrors에 싣는다. 재귀 모드가
루트 저장까지 맡게 되므로 Thinking.md 헤더도 SaveOutputsAsync 수준으로
맞춘다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 7: 참조 미완 배너·분석 범위·캐시 타임스탬프 반영

**Files:**
- Modify: `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs` (`ExecuteDiscoveredNodesAsync`, `BuildPersistedSpecification`)
- Test: `tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`

**Interfaces:**
- Consumes: `VerificationBanner.UnresolvedReferences` (Task 2), `VerificationDocumentFormatter.FormatSpecification(..., AnalysisScope?)` (Task 3), `CodeObjectAnalysisResult.FromCache`/`.AnalyzedAt` (Task 1)
- Produces: 저장되는 모든 명세서가 `분석 범위: 직접 의존성`을 갖고, 캐시 히트 노드는 원본 작성일시를 유지하며, 참조 목록에 미완료 항목이 있는 문서만 배너를 단다.

- [ ] **Step 1: 실패 테스트 작성**

```csharp
    [Fact]
    public async Task AnalyzeAsync_CyclicGraphCancelled_AddsTheUnresolvedReferenceBannerToTheSurvivingDocument()
    {
        // 후위 순회라 보통은 부모가 자식보다 뒤에 실행되지만, 순환에서는
        // TryRegisterDepth가 재진입을 막아 자식이 부모보다 뒤에 온다.
        // 이때만 성공한 문서의 참조 목록에 미완료 항목이 남는다.
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-CycleBanner-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var partner = Key("USP_Partner", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(
            Definition(root, partner),
            Definition(partner, root));
        using var cts = new CancellationTokenSource();
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) =>
            {
                if (key == root)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }

                return Task.FromResult(PipelineResult(key));
            });

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot),
                cts.Token);

            Assert.Equal(GraphCompletion.PartialCancelled, result.Completion);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var partnerSpec = await File.ReadAllTextAsync(paths.ResolveSpecPath(partner));

            Assert.Contains("[참조 미완]", partnerSpec);
            Assert.Contains("dbo.USP_Root", partnerSpec);
            // 배너와 참조 섹션이 같은 사실을 말해야 한다.
            Assert.Contains("분석 취소", partnerSpec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_CompletedGraph_LeavesNoUnresolvedReferenceBanner()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-NoBanner-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            await sut.AnalyzeAsync(root, Request(outputDirectory: outputRoot), CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var rootSpec = await File.ReadAllTextAsync(paths.ResolveSpecPath(root));

            Assert.DoesNotContain("[참조 미완]", rootSpec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_WritesDirectAnalysisScopeIntoEverySpecification()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Scope-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            await sut.AnalyzeAsync(root, Request(outputDirectory: outputRoot), CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);

            Assert.Contains("분석 범위: 직접 의존성", await File.ReadAllTextAsync(paths.ResolveSpecPath(root)));
            Assert.Contains("분석 범위: 직접 의존성", await File.ReadAllTextAsync(paths.ResolveSpecPath(child)));
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_CachedNode_KeepsTheOriginalAnalysisTimestamp()
    {
        // 캐시 히트는 AI를 호출하지 않았다. 링크 갱신 때문에 파일은 다시
        // 써야 하지만 작성일시까지 새로 찍으면 거짓 주장이 된다.
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-CacheStampGraph-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var analyzedAt = new DateTime(2026, 8, 1, 14, 22, 3);
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
            {
                SpDef = Definition(key),
                SpecMarkdown = "# Spec",
                FromCache = true,
                AnalyzedAt = analyzedAt
            }));

        try
        {
            await sut.AnalyzeAsync(root, Request(outputDirectory: outputRoot), CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var spec = await File.ReadAllTextAsync(paths.ResolveSpecPath(root));

            Assert.Contains("**문서 작성일시**: 2026-08-01 14:22:03", spec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests`
Expected: 네 테스트 FAIL

- [ ] **Step 3: 노드별 캐시 정보를 결과로 옮긴다**

`ExecuteDiscoveredNodesAsync`의 `execution.AnalysisResults.Add(new CodeObjectAnalysisResult { ... })` 초기화자에 두 줄을 추가한다.

```csharp
                    FromCache = pipelineResult.FromCache,
                    AnalyzedAt = pipelineResult.AnalyzedAt,
```

- [ ] **Step 4: `BuildPersistedSpecification` 수정**

기존 식 본문(expression-bodied) 메서드를 블록 본문으로 바꾸고 그래프를 받는다.

```csharp
    private static string BuildPersistedSpecification(
        CodeObjectAnalysisResult analysis,
        DependencyAnalysisRequest request,
        CodeObjectPipelineResult graph)
    {
        var body = analysis.SpecMarkdown ?? string.Empty;

        // 배너는 analysis.SpecMarkdown에 되쓰지 않는다. 재링크 루프가 이 메서드를
        // 여러 번 부를 수 있는데, 되쓰면 배너가 겹겹이 쌓인다.
        var unresolved = CollectUnresolvedReferences(analysis.Key, graph);
        if (unresolved.Count > 0)
        {
            body = VerificationBanner.UnresolvedReferences(unresolved) + body;
        }

        return VerificationDocumentFormatter.FormatSpecification(
            body,
            analysis.Review,
            analysis.Outcome,
            request.Provider,
            request.ModelName,
            request.ActorEffort,
            analysis.AnalyzedAt ?? DateTime.Now,
            AnalysisScope.Direct);
    }

    /// <summary>
    /// 이 문서가 참조하는 객체 중 분석이 끝나지 않은 것들의 이름을 모은다.
    /// 참조 섹션 생성과 같은 상태(자식 노드 Status)를 보므로 두 표기가 어긋나지 않는다.
    /// </summary>
    private static IReadOnlyList<string> CollectUnresolvedReferences(
        CodeObjectKey parentKey,
        CodeObjectPipelineResult graph)
    {
        var nodesByKey = graph.Nodes.ToDictionary(node => node.Key);

        return graph.DependencyEdges
            .Where(edge => edge.Source.Equals(parentKey))
            .Select(edge => edge.Target)
            .Distinct()
            .Where(target =>
                nodesByKey.TryGetValue(target, out var node) &&
                node.Status is AnalysisNodeStatus.Cancelled or AnalysisNodeStatus.Queued)
            .Select(target => $"{target.Schema}.{target.Name}")
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
```

호출부도 고친다.

```csharp
                            BuildPersistedSpecification(analysis, request, graph),
```

- [ ] **Step 5: 테스트 통과 확인**

Run: `dotnet test --nologo -v q --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests`
Expected: PASS

- [ ] **Step 6: 전체 테스트 확인**

Run: `dotnet test --nologo -v q`
Expected: 실패 0

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs \
        tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs
git commit -F - <<'EOF'
feat(analysis): record scope, cache timestamp, and unresolved references

재귀 경로가 저장하는 명세서에 세 가지를 반영한다.

- 분석 범위(직접 의존성)를 YAML에 남겨 참조분석 ON의 트레이드오프를
  사후에 확인할 수 있게 한다
- 캐시 히트 노드는 원본 작성일시를 유지한다. 링크 정확성 때문에 파일은
  다시 써야 하지만 분석하지 않은 날짜를 찍으면 안 된다
- 참조 목록에 미완료 항목이 남는 문서(순환·깊이 재등록)에만 배너를 단다

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 8: `RunPipelineAsync`를 테스트 확장 메서드로 이전

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (`RunPipelineAsync` 제거)
- Create: `tests/ReSet.Core.Tests/PipelineTestExtensions.cs`

**Interfaces:**
- Consumes: `VerificationPipelineOrchestrator.RunCodeObjectPipelineAsync`, `VerificationPipelineOrchestrator.ResolveCurrentDatabase` (Task 4)
- Produces: 테스트 전용 `RunPipelineAsync` 확장 메서드. 기존 40여 개 테스트가 수정 없이 통과한다.

- [ ] **Step 1: 확장 메서드 작성**

`tests/ReSet.Core.Tests/PipelineTestExtensions.cs` 신규 생성. 프로덕션에서 제거되는 메서드의 시그니처를 그대로 옮긴다.

```csharp
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests;

/// <summary>
/// 프로덕션에서 제거된 VerificationPipelineOrchestrator.RunPipelineAsync의
/// 테스트 전용 대체. 40여 개 기존 테스트가 쓰던 튜플 반환 형태를 유지한다.
/// 프로덕션 호출부는 RunCodeObjectPipelineAsync를 직접 쓴다.
/// </summary>
internal static class PipelineTestExtensions
{
    public static async Task<(string? SpecMarkdown, SpDefinition? SpDef, ReviewResult? Review, string? ThinkingText, VerificationOutcome Outcome)>
        RunPipelineAsync(
            this VerificationPipelineOrchestrator orchestrator,
            string connectionString,
            string schema,
            string name,
            int maxDepth,
            string provider,
            string instructions,
            bool isBatchMode,
            string outputDirectory = "./output",
            bool enableCache = false,
            CancellationToken cancellationToken = default)
    {
        var database = VerificationPipelineOrchestrator.ResolveCurrentDatabase(connectionString)
            ?? string.Empty;
        var key = CodeObjectKey.Create(database, schema, name, CodeObjectType.Procedure);
        var result = await orchestrator.RunCodeObjectPipelineAsync(
            connectionString,
            key,
            maxDepth,
            provider,
            instructions,
            isBatchMode,
            outputDirectory,
            enableCache,
            cancellationToken);

        return (result.SpecMarkdown, result.SpDef, result.Review, result.ThinkingText, result.Outcome);
    }
}
```

- [ ] **Step 2: 프로덕션 메서드 제거**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`에서 `RunPipelineAsync` 메서드 전체를 삭제한다.

- [ ] **Step 3: 전체 테스트 확인**

Run: `dotnet test --nologo -v q`
Expected: 실패 0. 기존 `RunPipelineAsync_*` 테스트 40여 개가 **한 줄도 수정하지 않은 채** 통과해야 한다. 실패한다면 확장 메서드 시그니처가 원본과 다르다는 뜻이므로 파라미터 이름·순서·기본값을 대조하라.

- [ ] **Step 4: 프로덕션 호출부가 남아 있지 않은지 확인**

Run: `grep -rn "RunPipelineAsync" src/`
Expected: 출력 없음

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs \
        tests/ReSet.Core.Tests/PipelineTestExtensions.cs
git commit -F - <<'EOF'
refactor(verification): move RunPipelineAsync into a test-only extension

호출부가 CodeObjectPipelineResult를 받아야 하므로 프로덕션은
RunCodeObjectPipelineAsync를 직접 쓴다. 그 결과 튜플 래퍼는 테스트
전용이 되는데, 얇은 위임을 프로덕션에 남기지 않는다는 원칙에 따라
테스트 프로젝트로 옮긴다.

시그니처가 동일하므로 기존 테스트 40여 개는 수정하지 않는다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 9: CLI 배선

**Files:**
- Modify: `src/ReSet.Cli/Program.cs` (`RunConfiguredAnalysisAsync`, `RenderDependencyAnalysisFailures`, `SaveOutputsAsync`, 메뉴 1번 블록, 배치 모드 블록)

**Interfaces:**
- Consumes: `SpAnalysisOutcome`, `ArtifactPersistence`, `GraphCompletion`, `AnalysisScope` (Task 1), `VerificationPipelineOrchestrator.RunCodeObjectPipelineAsync`·`ResolveCurrentDatabase` (Task 4), `VerificationDocumentFormatter.FormatSpecification(..., AnalysisScope?)` (Task 3)
- Produces: 없음 (최종 소비부)

- [ ] **Step 1: `RunConfiguredAnalysisAsync` 반환 타입 교체**

`src/ReSet.Cli/Program.cs`의 `RunConfiguredAnalysisAsync` 시그니처와 본문을 고친다. 파라미터 목록은 그대로다.

```csharp
        public static async Task<SpAnalysisOutcome> RunConfiguredAnalysisAsync(
            bool analyzeReferencedCodeObjects,
            IDependencyAnalysisOrchestrator dependencyAnalysisOrchestrator,
            VerificationPipelineOrchestrator verificationPipelineOrchestrator,
            IDbMetadataService metadataService,
            string connectionString,
            string configuredDatabase,
            string schema,
            string name,
            int maxDepth,
            string provider,
            string modelName,
            string? actorEffort,
            string instructions,
            bool isBatchMode,
            string outputDirectory,
            bool enableCache,
            bool allowExternalDatabaseConnections,
            DependencyArtifactMode dependencyArtifactMode,
            CancellationToken cancellationToken)
        {
            if (!analyzeReferencedCodeObjects)
            {
                // 기존 RunPipelineAsync가 하던 키 조립을 그대로 옮긴다.
                var singleObjectDatabase =
                    VerificationPipelineOrchestrator.ResolveCurrentDatabase(connectionString)
                    ?? string.Empty;
                var singleObjectKey = CodeObjectKey.Create(
                    singleObjectDatabase, schema, name, CodeObjectType.Procedure);
                var pipelineResult = await verificationPipelineOrchestrator.RunCodeObjectPipelineAsync(
                    connectionString,
                    singleObjectKey,
                    maxDepth,
                    provider,
                    instructions,
                    isBatchMode,
                    outputDirectory,
                    enableCache,
                    cancellationToken);

                return SpAnalysisOutcome.FromSingleObjectPipeline(pipelineResult);
            }

            var database = await ResolveAnalysisDatabaseAsync(
                connectionString,
                configuredDatabase,
                metadataService,
                cancellationToken);
            var rootKey = CodeObjectKey.Create(database, schema, name, CodeObjectType.Procedure);
            var result = await dependencyAnalysisOrchestrator.AnalyzeAsync(
                rootKey,
                new DependencyAnalysisRequest
                {
                    ConnectionString = connectionString,
                    MaxDepth = maxDepth,
                    Provider = provider,
                    ModelName = modelName,
                    ActorEffort = actorEffort,
                    Instructions = instructions,
                    IsBatchMode = isBatchMode,
                    OutputDirectory = outputDirectory,
                    EnableCache = enableCache,
                    AllowExternalDatabaseConnections = allowExternalDatabaseConnections,
                    DependencyArtifactMode = dependencyArtifactMode
                },
                cancellationToken);

            RenderAnalysisDiagnostics(result);

            return SpAnalysisOutcome.FromDependencyGraph(result, rootKey);
        }
```

- [ ] **Step 2: 진단 렌더러 확장**

`RenderDependencyAnalysisFailures`를 `RenderAnalysisDiagnostics`로 교체한다.

```csharp
        /// <summary>
        /// 그래프 분석 중 사용자가 알아야 할 사실을 모두 화면에 낸다.
        /// 실패 노드만 보여주던 기존 렌더러는 스킵·부분 완료·저장 실패를 놓쳤다.
        /// </summary>
        private static void RenderAnalysisDiagnostics(CodeObjectPipelineResult result)
        {
            foreach (var node in result.Nodes.Where(node => node.Status == AnalysisNodeStatus.Failed))
            {
                var objectName = $"{node.Key.Schema}.{node.Key.Name}";
                var error = string.IsNullOrWhiteSpace(node.Error) ? "알 수 없는 오류" : node.Error;
                AnsiConsole.MarkupLine($"[yellow]경고:[/] {Markup.Escape(objectName)} 분석 실패 - {Markup.Escape(error)}");
                AnsiConsole.WriteLine();
            }

            foreach (var group in result.Nodes
                .Where(node => node.Status is AnalysisNodeStatus.SkippedDepth or AnalysisNodeStatus.SkippedExternal)
                .GroupBy(node => node.Status))
            {
                var label = group.Key == AnalysisNodeStatus.SkippedDepth ? "깊이 제한" : "외부 객체";
                AnsiConsole.MarkupLine($"[grey]안내:[/] {label}으로 {group.Count()}개 객체를 분석하지 않았습니다.");
            }

            if (result.Completion == GraphCompletion.PartialCancelled)
            {
                var succeeded = result.Nodes.Count(node => node.Status == AnalysisNodeStatus.Succeeded);
                var unpersisted = result.Nodes
                    .Where(node => node.Status != AnalysisNodeStatus.Succeeded)
                    .Select(node => $"{node.Key.Schema}.{node.Key.Name}")
                    .OrderBy(objectName => objectName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var shown = string.Join(", ", unpersisted.Take(10));
                var suffix = unpersisted.Count > 10 ? $" 외 {unpersisted.Count - 10}건" : string.Empty;

                AnsiConsole.Write(new Panel(new Markup(
                    "[yellow]사용자 취소로 분석이 중단되었습니다.[/]\n" +
                    $"[bold]완료:[/] {succeeded} / [bold]발견:[/] {result.Nodes.Count} 객체\n" +
                    $"[bold]저장되지 않은 객체:[/] {Markup.Escape(shown)}{suffix}"))
                {
                    Border = BoxBorder.Rounded,
                    Header = new PanelHeader(" 부분 완료 ")
                });
            }

            if (result.Persistence == ArtifactPersistence.Failed)
            {
                foreach (var error in result.PersistenceErrors)
                {
                    AnsiConsole.MarkupLine($"[red]저장 실패:[/] {Markup.Escape(error)}");
                }

                AnsiConsole.WriteLine();
            }
        }
```

- [ ] **Step 3: `SaveOutputsAsync`를 둘로 나눈다**

기존 `SaveOutputsAsync`를 삭제하고 두 메서드로 대체한다. 원천 산출물 저장부는 기존 본문의 `if (spDef != null) { ... }` 블록을 그대로 옮긴 것이다.

```csharp
        /// <summary>
        /// 원천 산출물(raw/*)을 저장한다. 캐시 히트에도 실행한다 — raw는 타임스탬프를
        /// 담지 않아 거짓 주장을 만들 수 없고, SaveRawJson을 뒤늦게 켠 사용자에게
        /// metadata.json이 영영 생기지 않는 함정을 막는다.
        /// </summary>
        private static async Task SaveRawArtifactsAsync(
            ReSet.Core.Models.SpDefinition? spDef,
            string outputDir,
            string instructionsFile,
            IMetadataExporter metadataExporter,
            bool saveRawJson,
            bool saveRawContext,
            bool saveRawFiles,
            string schema,
            string name)
        {
            if (spDef == null)
            {
                return;
            }

            var spOutputDir = Path.Combine(outputDir, "Procedures", $"{schema}.{name}");
            Directory.CreateDirectory(spOutputDir);

            try
            {
                var dependenciesText = new System.Text.StringBuilder();
                var tableSchemasText = new System.Text.StringBuilder();
                var referenceDdlsText = new System.Text.StringBuilder();
                var warningsText = new System.Text.StringBuilder();

                if (spDef.Warnings.Count > 0)
                {
                    warningsText.AppendLine("[DB 메타데이터 수집 중 발생한 경고/오류 목록]");
                    foreach (var warn in spDef.Warnings)
                    {
                        warningsText.AppendLine($"- {warn}");
                    }
                    warningsText.AppendLine();
                }

                foreach (var dep in spDef.Dependencies)
                {
                    dependenciesText.AppendLine($"- Schema: {dep.Schema}, Name: {dep.Name}, Type: {dep.Type} (발견 깊이: {dep.DiscoveryDepth}단계)");
                    if (dep.Columns.Count > 0)
                    {
                        tableSchemasText.AppendLine($"### 테이블: {dep.Schema}.{dep.Name} ({dep.Type})");
                        foreach (var col in dep.Columns)
                        {
                            tableSchemasText.AppendLine($"| {col.ColumnName} | {col.DataType} | {(col.IsNullable ? "Yes" : "No")} |");
                        }
                    }
                    if (!string.IsNullOrEmpty(dep.ReferencedDdlText))
                    {
                        referenceDdlsText.AppendLine($"### {dep.Type}: {dep.Schema}.{dep.Name}");
                        referenceDdlsText.AppendLine(dep.ReferencedDdlText);
                    }
                }

                var rawPromptContext = $@"
[시스템 규칙 지침]
{(File.Exists(instructionsFile) ? await File.ReadAllTextAsync(instructionsFile) : "기본 마크다운 규칙을 적용하여 분석해 주세요.")}

{warningsText}
[수집된 DB 메타데이터 의존관계 목록]
{dependenciesText}

[의존하는 참조 테이블 상세 스키마 정보]
{tableSchemasText}

[의존하는 참조 UDF/SP 소스 코드]
{referenceDdlsText}

[Stored Procedure DDL SQL 원본]
{spDef.DdlText}
";
                await metadataExporter.ExportRawMetadataAsync(
                    spDef,
                    spDef.RawPromptContext ?? rawPromptContext,
                    spOutputDir,
                    saveRawJson,
                    saveRawContext,
                    saveRawFiles);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]원천 산출물(Raw Metadata) 저장 중 경고:[/] {Markup.Escape(ex.Message)}");
            }
        }

        /// <summary>
        /// 사람이 읽는 문서(docs/*)를 저장한다. 캐시 히트면 호출하지 않는다 —
        /// 파일이 이미 그 내용이고, 다시 쓰면 분석하지 않은 날짜가 찍힌다.
        /// </summary>
        private static async Task SaveDocumentsAsync(
            string specMarkdown,
            string? migrationPlan,
            string outputDir,
            string schema,
            string name,
            string provider,
            string modelName,
            ReviewResult? review,
            VerificationOutcome outcome,
            string? thinkingText,
            string? effort,
            AnalysisScope scope)
        {
            var docsDir = Path.Combine(outputDir, "Procedures", $"{schema}.{name}", "docs");
            Directory.CreateDirectory(docsDir);

            await File.WriteAllTextAsync(
                Path.Combine(docsDir, "Spec.md"),
                VerificationDocumentFormatter.FormatSpecification(
                    specMarkdown, review, outcome, provider, modelName, effort, DateTime.Now, scope));

            if (!string.IsNullOrEmpty(migrationPlan))
            {
                // 단일 SP 계획서는 검증을 거치지 않았다(A~E의 결함 D 참조).
                await File.WriteAllTextAsync(
                    Path.Combine(docsDir, "BatchMigrationPlan.md"),
                    VerificationDocumentFormatter.FormatUnverifiedPlan(
                        migrationPlan, outcome, provider, modelName, effort, DateTime.Now));
            }

            if (string.IsNullOrWhiteSpace(thinkingText))
            {
                return;
            }

            try
            {
                var oldTxtFile = Path.Combine(docsDir, "Thinking.txt");
                if (File.Exists(oldTxtFile))
                {
                    try { File.Delete(oldTxtFile); } catch { }
                }

                var effortSuffix = string.IsNullOrWhiteSpace(effort) ? "" : $", Effort: {effort}";
                var thinkingHeader =
                    "# AI 추론 과정 로그 (Thinking Process Log)\n\n" +
                    $"- **기본 분석 AI 정보**: {provider} ({modelName}{effortSuffix})\n" +
                    $"- **문서 작성일시**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n" +
                    "본 문서는 저장 프로시저 역공학 및 검증 파이프라인 수행 중 사용된 AI 모델들의 추론 과정(Thinking Process)을 기록한 마크다운 문서입니다.\n\n" +
                    "---\n\n";

                await File.WriteAllTextAsync(Path.Combine(docsDir, "Thinking.md"), thinkingHeader + thinkingText);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]추론 로그(Thinking Log) 저장 중 경고:[/] {Markup.Escape(ex.Message)}");
            }
        }
```

`configuration` 파라미터는 기존 `SaveOutputsAsync`에서도 쓰이지 않았으므로 두 메서드 어디에도 두지 않는다.

- [ ] **Step 4: 결과 패널 렌더러 추가**

```csharp
        private static void RenderAnalysisResultPanel(
            string selectedOption,
            string outputDir,
            string schema,
            string name,
            SpAnalysisOutcome result)
        {
            if (result.Persistence == ArtifactPersistence.Failed)
            {
                var detail = result.PersistenceErrors.Count > 0
                    ? string.Join("\n", result.PersistenceErrors)
                    : "상세 사유가 기록되지 않았습니다.";
                AnsiConsole.Write(new Panel(new Markup(
                    $"[red]산출물 저장에 실패했습니다.[/]\n{Markup.Escape(detail)}"))
                {
                    Border = BoxBorder.Rounded,
                    Header = new PanelHeader($" {Markup.Escape(selectedOption)} 저장 실패 ")
                });
                return;
            }

            // 부분 완료 패널은 RenderAnalysisDiagnostics가 이미 냈다.
            if (result.Completion == GraphCompletion.PartialCancelled)
            {
                return;
            }

            var specPath = Path.Combine(outputDir, "Procedures", $"{schema}.{name}", "docs", "Spec.md");
            var cacheNote = result.FromCache
                ? result.AnalyzedAt is { } analyzedAt
                    ? $"\n[grey]캐시 재사용 (원본 분석: {analyzedAt:yyyy-MM-dd HH:mm:ss})[/]"
                    : "\n[grey]캐시 재사용 (원본 분석 시각 불명)[/]"
                : string.Empty;

            AnsiConsole.Write(new Panel(new Markup(
                $"[green]성공적으로 파일이 생성되었습니다![/]\n[bold]저장 경로:[/] {Markup.Escape(specPath)}{cacheNote}"))
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader($" {Markup.Escape(selectedOption)} 분석 완료 ")
            });
        }
```

- [ ] **Step 5: 메뉴 1번 블록 배선**

`Program.cs:899-958` 구간을 교체한다. 확인 프롬프트 앞에 안내를 낸다.

```csharp
                        AnsiConsole.MarkupLine(
                            "[grey]참조 분석을 켜면 참조 객체마다 별도 명세서와 승인 화면이 생기고,[/]");
                        AnsiConsole.MarkupLine(
                            "[grey]루트 SP는 직접 의존성만으로 분석됩니다(하위 SP가 쓰는 테이블 스키마는 루트 컨텍스트에서 제외).[/]");
                        var analyzeSelectedReferences = AnsiConsole.Confirm(
                            "선택한 SP가 참조하는 SP/UDF도 함께 분석하시겠습니까?",
                            analyzeReferencedCodeObjects);
```

분석 호출과 저장 분기를 교체한다.

```csharp
                            var result = await RunConfiguredAnalysisAsync(
                                analyzeSelectedReferences,
                                dependencyAnalysisOrchestrator,
                                orchestrator,
                                dbService,
                                connectionString,
                                database ?? string.Empty,
                                schema,
                                name,
                                maxDepth,
                                provider,
                                modelName,
                                actorEffort,
                                instructions,
                                isBatchMode: false,
                                outputDir,
                                enableCache,
                                allowExternalDatabaseConnections,
                                dependencyArtifactMode,
                                activeCts.Token);

                            if (string.IsNullOrEmpty(result.SpecMarkdown))
                            {
                                AnsiConsole.MarkupLine("[red]분석이 중단되었거나 명세서 생성에 실패했습니다.[/]");
                                continue;
                            }

                            if (!Directory.Exists(outputDir))
                            {
                                Directory.CreateDirectory(outputDir);
                            }

                            // 재귀 경로는 오케스트레이터가 이미 저장했다(Persistence != NotAttempted).
                            if (result.Persistence == ArtifactPersistence.NotAttempted)
                            {
                                await SaveRawArtifactsAsync(
                                    result.Definition, outputDir, instructionsFile, metadataExporter,
                                    saveRawJson, saveRawContext, saveRawFiles, schema, name);

                                if (!result.FromCache)
                                {
                                    // 분석과 전환 분리 요구에 따라, 개별 분석 시에는 배치 전환 설계서를 생성하지 않음
                                    await SaveDocumentsAsync(
                                        result.SpecMarkdown, migrationPlan: null, outputDir, schema, name,
                                        provider, modelName, result.Review, result.Outcome,
                                        result.ThinkingText, actorEffort, result.Scope);
                                }
                            }

                            RenderAnalysisResultPanel(selectedOption, outputDir, schema, name, result);
```

- [ ] **Step 6: 배치 모드 블록 배선**

`Program.cs:624-680` 구간을 교체한다. 배치 모드는 취소 시 루프를 빠져나가야 하는데, `AnalyzeAsync`가 더 이상 던지지 않으므로 상태로 판정한다.

```csharp
                        var result = await RunConfiguredAnalysisAsync(
                            analyzeReferencedCodeObjects,
                            dependencyAnalysisOrchestrator,
                            orchestrator,
                            dbService,
                            connectionString,
                            database ?? string.Empty,
                            schema,
                            name,
                            maxDepth,
                            provider,
                            modelName,
                            actorEffort,
                            instructions,
                            isBatchMode: true,
                            outputDir,
                            enableCache,
                            allowExternalDatabaseConnections,
                            dependencyArtifactMode,
                            globalCts.Token);

                        if (string.IsNullOrEmpty(result.SpecMarkdown))
                        {
                            throw new Exception("검증 파이프라인을 통과한 명세서 획득 실패");
                        }

                        specsData.Add(("docs/Spec.md", result.SpecMarkdown));
                        if (result.Definition != null)
                        {
                            spDefs.Add(result.Definition);
                        }

                        var thinkingText = result.ThinkingText;
                        string? migrationPlan = null;
                        if (migrationEnabled && result.Definition != null)
                        {
                            AnsiConsole.MarkupLine($"[yellow]{schema}.{name}[/] - 배치 전환 계획 설계서 작성 중 ({targetLanguage})...");
                            var migrationResult = await aiService.GenerateBatchMigrationPlanAsync(
                                result.Definition, targetLanguage, globalCts.Token);
                            migrationPlan = migrationResult.Content;
                            if (!string.IsNullOrWhiteSpace(migrationResult.ThinkingText))
                            {
                                thinkingText = (thinkingText ?? "") + "\n=== Batch Migration Plan Thinking ===\n" + migrationResult.ThinkingText + "\n";
                            }
                        }

                        if (!Directory.Exists(outputDir))
                        {
                            Directory.CreateDirectory(outputDir);
                        }

                        if (result.Persistence == ArtifactPersistence.NotAttempted)
                        {
                            await SaveRawArtifactsAsync(
                                result.Definition, outputDir, instructionsFile, metadataExporter,
                                saveRawJson, saveRawContext, saveRawFiles, schema, name);

                            if (!result.FromCache)
                            {
                                await SaveDocumentsAsync(
                                    result.SpecMarkdown, migrationPlan, outputDir, schema, name,
                                    provider, modelName, result.Review, result.Outcome,
                                    thinkingText, actorEffort, result.Scope);
                            }
                        }

                        AnsiConsole.MarkupLine($"[green]성공:[/] {selectedOption} 분석 완료 및 저장!");

                        // AnalyzeAsync가 더 이상 예외로 취소를 알리지 않으므로 상태로 판정한다.
                        if (result.Completion == GraphCompletion.PartialCancelled)
                        {
                            AnsiConsole.MarkupLine("\n[red]사용자에 의해 배치 분석 작업이 중단되었습니다. 프로세스를 종료합니다.[/]");
                            break;
                        }
```

기존 `var (specMarkdown, spDef, reviewResult, thinkingText, outcome) = ...` 구조분해와 그 아래 `spDef`/`specMarkdown`/`reviewResult`/`outcome` 참조를 모두 위 형태로 바꾼다. 재귀 경로에서는 `migrationPlan`이 저장되지 않는데, 이는 기존 동작과 같다 — 기존 코드도 재귀 모드에서 `SaveOutputsAsync`를 부르며 계획서를 함께 넘겼으나, 이제 저장 책임이 오케스트레이터로 넘어가 계획서가 빠진다. **배치 모드에서 재귀 분석과 `MigrationSettings:Enabled`를 동시에 켜면 단일 SP 계획서가 생성되지 않는다**는 점을 아래 Step 8에서 문서화한다.

- [ ] **Step 7: 빌드와 전체 테스트 확인**

Run: `dotnet build -v q --nologo`
Expected: 오류 0

Run: `dotnet test --nologo -v q`
Expected: 실패 0

- [ ] **Step 8: 알려진 동작 변경을 설계 문서에 기록**

`docs/superpowers/specs/2026-08-03-stage1-analysis-flow-hardening-design.md`의 「범위 밖」 목록 끝에 추가한다.

```markdown
- 배치 모드에서 참조분석과 `MigrationSettings:Enabled`를 동시에 켜면 단일 SP의 `BatchMigrationPlan.md`가 생성되지 않는다. 저장 책임이 오케스트레이터로 넘어갔는데 오케스트레이터는 계획서 개념을 갖지 않기 때문이다. A~E의 결함 D가 지적했듯 이 계획서는 애초에 검증을 거치지 않으므로 손실이 크지 않다고 판단했다. 필요하면 별도 사이클에서 계획서 생성을 파이프라인 안으로 옮긴다.
```

- [ ] **Step 9: 수동 확인 항목 기록**

CLI 메뉴 1번은 실 DB 연결과 AI 호출이 필요해 자동 검증 대상이 아니다. `dotnet run --project src/ReSet.Cli`는 TUI 로그인 프롬프트에서 입력을 기다리므로 CI 단계로 넣지 않는다. 아래를 수동 확인 항목으로 PR 본문에 남긴다.

- 메뉴 1번 진입 시 참조분석 확인 프롬프트 위에 회색 안내 두 줄이 보인다
- 참조분석 OFF로 SP 하나를 분석하면 기존과 같은 성공 패널이 뜨고 `docs/Spec.md`에 `분석 범위: 전이 의존성`이 있다
- 참조분석 ON으로 분석하면 `분석 범위: 직접 의존성`이 있고 루트 `Spec.md`가 한 번만 기록된다
- 분석 도중 Ctrl+C를 누르면 부분 완료 패널이 뜨고, 완료된 하위 객체의 `Spec.md`가 디스크에 남아 있다
- `EnableCache: true`로 같은 SP를 두 번 분석하면 두 번째에 "캐시 재사용" 표시가 뜨고 `문서 작성일시`가 바뀌지 않는다

- [ ] **Step 10: 커밋**

```bash
git add src/ReSet.Cli/Program.cs docs/superpowers/specs/2026-08-03-stage1-analysis-flow-hardening-design.md
git commit -F - <<'EOF'
feat(cli): drive stage-1 output from the analysis result contract

Program이 5-튜플 대신 SpAnalysisOutcome을 받아 저장 여부와 보고 내용을
필드 두 개(Persistence, FromCache)로만 분기한다.

- 재귀 경로는 오케스트레이터가 저장했으므로 CLI가 다시 쓰지 않는다
  (루트 Spec.md/Thinking.md/raw의 이중 기록이 사라진다)
- 캐시 히트면 문서 저장을 건너뛰고 원본 분석 시각을 화면에 표시한다
- SaveOutputsAsync를 원천 산출물과 문서로 나눠, 캐시 히트에도
  raw/metadata.json이 채워지게 한다
- 저장 실패와 부분 완료를 성공 패널 대신 전용 패널로 보고한다
- 참조분석 확인 프롬프트에 컨텍스트 범위 트레이드오프를 고지한다

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## 완료 확인

모든 태스크를 마친 뒤 실행한다.

- [ ] `dotnet build -v q --nologo` — 오류 0
- [ ] `dotnet test --nologo -v q` — 실패 0, 통과 수가 기준선 355보다 크다
- [ ] `grep -rn "RunPipelineAsync" src/` — 출력 없음
- [ ] `grep -rn "SaveOutputsAsync\|RenderDependencyAnalysisFailures" src/` — 출력 없음
- [ ] `docs/superpowers/specs/2026-08-03-stage1-analysis-flow-hardening-design.md`의 「테스트 전략」 항목이 전부 대응 테스트를 갖는다
