# 검증 정직성 후속 과제 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 직전 사이클에서 남긴 검증 정직성 후속 과제 다섯 건(레거시 캐시 무효화, dynamic 영역 L1 플래그, 통합 계획서 검증 상태, 단일 SP 계획서의 타 문서 점수 도용, 헤더 별칭 커버리지)을 닫는다.

**Architecture:** `SpecificationDocumentFormatter`를 `VerificationDocumentFormatter`로 개명하고 진입점을 셋(명세서 / 검증된 통합 계획서 / 미검증 단일 SP 계획서)으로 늘려 골격을 공유하되 문서 종류별로 다른 점수 설명만 분리한다. `CacheEntry`에 포맷 버전을 도입해 수정 이전 코드가 남긴 엔트리를 캐시 미스로 떨어뜨린다. `RunConsolidatedPipelineAsync`의 반환값을 레코드로 바꿔 계획서의 종료 상태와 리뷰가 호출부까지 도달하게 한다.

**Tech Stack:** .NET 10 / C#, xUnit, NSubstitute, Serilog, Spectre.Console, System.Text.Json

## Global Constraints

- 대상 저장소: `/Users/payletter/git-root/ReSet`, 기준 브랜치 `main` (설계 커밋 `6c3b521`)
- 설계 문서: `docs/superpowers/specs/2026-08-03-verification-honesty-followups-design.md`
- API 키를 소스나 `appsettings.json`에 하드코딩하지 않는다
- Spectre.Console 출력에 들어가는 런타임 값은 `Markup.Escape()`로 감싼다
- `OperationCanceledException`은 절대 삼키지 않는다. IO/DB/AI 오류는 soft-fail한다
- 모든 신규 주석과 사용자 노출 문자열은 한국어로 작성한다
- 작업 시작 시점의 테스트는 **355개 전부 통과**, 클린 빌드 경고 8건(전부 `tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`의 기존 CS8600/CS8602). 이 경고 수를 늘리지 않는다
- 경고 확인은 반드시 클린 빌드로 한다. 증분 빌드는 이전에 발생한 경고를 재출력하지 않아 새 경고를 숨긴다
- `MechanicalValidator`는 인터페이스가 없는 구상 클래스이며 메서드가 `virtual`이 아니다. **NSubstitute로 대체할 수 없다.** L1 통과/실패는 반드시 실제 마크다운 본문으로 유도한다
  - 개별 명세서 필수 H2: `개요`, `파라미터 목록`, `CRUD 분석`, `로직 흐름 요약`, `비즈니스 흐름 시각화`
  - 통합 계획서 필수 H2: `통합 배치 아키텍처 개요`, `Mermaid 기반 통합 흐름도`, `단계별 이행 상세 및 의사코드`, `통합 데이터 정합성 검증 SQL 세트`
  - 두 경우 모두 유효한 ` ```mermaid ` 블록이 필요하다

## 파일 구조

| 파일 | 책임 | 작업 |
|---|---|---|
| `src/ReSet.Core/Services/VerificationDocumentFormatter.cs` | 문서 종류별 YAML 헤더·메타 블록 렌더링 | `SpecificationDocumentFormatter.cs`에서 개명 (Task 1) |
| `src/ReSet.Core/Models/CacheEntry.cs` | 캐시 엔트리 스키마 | `FormatVersion` 추가 (Task 2) |
| `src/ReSet.Core/Services/CacheManager.cs` | 캐시 유효성 판정 | 버전 게이트 (Task 2) |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | 검증 파이프라인 | dynamic L1 플래그 (Task 3), 반환 레코드·`planReview` (Task 4) |
| `src/ReSet.Core/Models/ConsolidatedPipelineResult.cs` | 통합 파이프라인 반환 타입 | 신규 (Task 4) |
| `src/ReSet.Cli/Program.cs` | 산출물 저장 배선 | 3개 호출부 교체 (Task 5) |
| `src/ReSet.Cli/ConsoleUserInteraction.cs` | L3 승인 화면 | 주석의 타입 이름 갱신 (Task 1) |
| `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs` | 재귀 분석 | 포매터 호출 이름 갱신 (Task 1) |

테스트는 기존 파일에 추가한다: `SpecificationDocumentFormatterTests.cs`(Task 1에서 `VerificationDocumentFormatterTests.cs`로 개명), `CacheManagerTests.cs`, `VerificationPipelineOrchestratorTests.cs`, `SpecHeaderReaderTests.cs`.

---

### Task 1: `VerificationDocumentFormatter` 개명 및 계획서 진입점 추가

**Files:**
- Create: `src/ReSet.Core/Services/VerificationDocumentFormatter.cs`
- Delete: `src/ReSet.Core/Services/SpecificationDocumentFormatter.cs`
- Modify: `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs` (`SpecificationDocumentFormatter.Format` 호출 1곳)
- Modify: `src/ReSet.Cli/Program.cs:1638` (`SpecificationDocumentFormatter.Format` 호출 1곳)
- Modify: `src/ReSet.Cli/ConsoleUserInteraction.cs:101` (주석 안 타입 이름)
- Test: `tests/ReSet.Core.Tests/VerificationDocumentFormatterTests.cs` (기존 `SpecificationDocumentFormatterTests.cs`에서 개명)

**Interfaces:**
- Consumes: `ReSet.Core.Models.VerificationOutcome`, `ReSet.Core.Models.ReviewResult`
- Produces:
  - `VerificationDocumentFormatter.FormatSpecification(string body, ReviewResult? review, VerificationOutcome outcome, string provider, string modelName, string? effort, DateTime timestamp) → string`
  - `VerificationDocumentFormatter.FormatConsolidatedPlan(string body, ReviewResult? review, VerificationOutcome outcome, string provider, string modelName, string? effort, DateTime timestamp) → string`
  - `VerificationDocumentFormatter.FormatUnverifiedPlan(string body, VerificationOutcome sourceOutcome, string provider, string modelName, string? effort, DateTime timestamp) → string`

- [ ] **Step 1: 기존 테스트 파일을 개명하고 타입 참조를 바꾼다**

`SpecificationDocumentFormatterTests.cs`의 5개 테스트는 이 개명의 회귀 방지선이다. 파일을 옮기고 호출만 바꾼다. **단언은 한 글자도 바꾸지 않는다.**

```bash
git mv tests/ReSet.Core.Tests/SpecificationDocumentFormatterTests.cs \
       tests/ReSet.Core.Tests/VerificationDocumentFormatterTests.cs
```

파일 안에서 `SpecificationDocumentFormatterTests` → `VerificationDocumentFormatterTests` (클래스명), `SpecificationDocumentFormatter.Format(` → `VerificationDocumentFormatter.FormatSpecification(` 로 치환한다.

- [ ] **Step 2: 계획서 진입점의 실패 테스트를 작성한다**

`tests/ReSet.Core.Tests/VerificationDocumentFormatterTests.cs` 끝에 추가한다.

```csharp
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
```

- [ ] **Step 3: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationDocumentFormatterTests"`
Expected: 컴파일 실패 — `VerificationDocumentFormatter` 형식을 찾을 수 없음 (CS0103 / CS0246)

- [ ] **Step 4: 새 포매터를 작성한다**

`src/ReSet.Core/Services/VerificationDocumentFormatter.cs`를 만든다.

```csharp
using ReSet.Core.Models;

namespace ReSet.Core.Services;

/// <summary>
/// 검증 산출물의 상단 헤더(YAML 프런트매터 + 메타 블록)를 렌더링한다.
/// 골격은 문서 종류와 무관하게 같고, 다른 것은 점수 항목의 설명 주석뿐이다.
/// </summary>
public static class VerificationDocumentFormatter
{
    /// <summary>YAML 점수 줄에 붙는 설명 주석. 문서 종류마다 평가 기준이 다르다.</summary>
    private sealed record ScoreLabels(
        string Overall,
        string Accuracy,
        string Crud,
        string Interface,
        string Readability,
        string Exception);

    // 개별 명세서 Critic 기준.
    private static readonly ScoreLabels SpecificationLabels = new(
        "100점 만점 기준 AI 최종 신뢰도",
        "SQL 대비 기능 정합성",
        "데이터 변경 및 조회 검증",
        "파라미터 및 반환셋 정합성",
        "코드 가독성 및 표준 준수",
        "트랜잭션 격리 및 에러 처리");

    // 통합 계획서 Critic 기준(AiService.ReviewConsolidatedPlanAsync). 같은 필드를
    // 쓰지만 평가 대상이 다르다 - 특히 가독성은 다이어그램 문법을 본다.
    private static readonly ScoreLabels PlanLabels = new(
        "100점 만점 기준 AI 최종 신뢰도",
        "업무 로직 및 흐름 정합성",
        "데이터 모델 및 CRUD 완결성",
        "연동 및 인터페이스 정의",
        "다이어그램 문법 및 가독성",
        "예외 처리 및 트랜잭션 격리 정책");

    public static string FormatSpecification(
        string body,
        ReviewResult? review,
        VerificationOutcome outcome,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp) =>
        FormatVerified(body, review, outcome, SpecificationLabels, provider, modelName, effort, timestamp);

    public static string FormatConsolidatedPlan(
        string body,
        ReviewResult? review,
        VerificationOutcome outcome,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp) =>
        FormatVerified(body, review, outcome, PlanLabels, provider, modelName, effort, timestamp);

    /// <summary>
    /// 검증 파이프라인을 거치지 않은 계획서용. 자기 자신의 검증 상태가 없으므로
    /// ReviewResult를 받지 않는다 - 없는 파라미터는 유출될 수 없다. sourceOutcome은
    /// 이 계획서의 근거가 된 명세서의 종료 상태이며, 그 사실을 명시적으로 밝힌다.
    /// </summary>
    public static string FormatUnverifiedPlan(
        string body,
        VerificationOutcome sourceOutcome,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp)
    {
        var sourceLabel = StatusLabel(sourceOutcome);

        var yamlFrontMatter = $@"---
검증 상태: 검증 없음 # 이 계획서는 L1/L2 검증을 거치지 않음
근거 명세서 검증 상태: {sourceLabel}
---

";

        var statusNote =
            $"> **검증 상태**: 이 계획서는 검증 파이프라인을 거치지 않았습니다. 근거 명세서(Spec.md)는 '{sourceLabel}' 상태입니다.\n";

        return yamlFrontMatter + MetadataHeader(provider, modelName, effort, timestamp, string.Empty, statusNote) + body;
    }

    private static string FormatVerified(
        string body,
        ReviewResult? review,
        VerificationOutcome outcome,
        ScoreLabels labels,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp)
    {
        // 점수 노출 여부는 review의 null 여부가 아니라 종료 상태가 결정한다.
        // 1차 시도의 리뷰 결과가 남아 있어도 최종적으로 검증되지 않았다면 점수를 실으면 안 된다.
        var showScores = review is not null &&
            outcome is VerificationOutcome.Passed or VerificationOutcome.QualityRejected;

        var scoreLines = showScores
            ? $@"
종합 신뢰도: {review!.NormalizedScore} # {labels.Overall}
정합성 점수: {review.ScoreAccuracy}/10 # {labels.Accuracy}
CRUD 점수: {review.ScoreCrud}/10 # {labels.Crud}
인터페이스 점수: {review.ScoreInterface}/10 # {labels.Interface}
가독성 점수: {review.ScoreReadability}/10 # {labels.Readability}
예외처리 점수: {review.ScoreException}/10 # {labels.Exception}"
            : string.Empty;

        var yamlFrontMatter = $@"---
검증 상태: {StatusLabel(outcome)} # 검증 파이프라인 종료 상태{scoreLines}
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

        return yamlFrontMatter + MetadataHeader(provider, modelName, effort, timestamp, scoreHeader, statusNote) + body;
    }

    private static string MetadataHeader(
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp,
        string scoreHeader,
        string statusNote)
    {
        var effortSuffix = string.IsNullOrWhiteSpace(effort) ? string.Empty : $", Effort: {effort}";
        return $"> [!NOTE]\n> **문서 작성일시**: {timestamp:yyyy-MM-dd HH:mm:ss}\n> **분석 AI 정보**: {provider} ({modelName}{effortSuffix})\n{scoreHeader}{statusNote}\n";
    }

    private static string StatusLabel(VerificationOutcome outcome) => outcome switch
    {
        VerificationOutcome.Passed => "통과",
        VerificationOutcome.QualityRejected => "품질 미달",
        VerificationOutcome.ReviewNotRun => "리뷰 미수행",
        VerificationOutcome.L1Exhausted => "L1 미통과",
        _ => "알 수 없음"
    };
}
```

- [ ] **Step 5: 옛 파일을 지우고 호출부 3곳을 갱신한다**

```bash
git rm src/ReSet.Core/Services/SpecificationDocumentFormatter.cs
```

`src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs`와 `src/ReSet.Cli/Program.cs:1638`의 `SpecificationDocumentFormatter.Format(` 를 `VerificationDocumentFormatter.FormatSpecification(` 으로 바꾼다. 인수는 그대로다.

`src/ReSet.Cli/ConsoleUserInteraction.cs:101`의 주석에서 `SpecificationDocumentFormatter가` → `VerificationDocumentFormatter가` 로 바꾼다. 주석이 존재하지 않는 타입을 가리키면 문서가 거짓이 된다.

남은 참조가 없는지 확인한다.

```bash
grep -rn "SpecificationDocumentFormatter" src/ tests/
```
Expected: 출력 없음

- [ ] **Step 6: 테스트를 실행해 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationDocumentFormatterTests"`
Expected: 10 passed (기존 5 + 신규 5)

- [ ] **Step 7: 전체 테스트와 클린 빌드를 확인한다**

```bash
dotnet clean && dotnet build 2>&1 | grep -c "warning"
dotnet test
```
Expected: 경고 8건(기존과 동일), 전체 테스트 통과

- [ ] **Step 8: 커밋**

```bash
git add -A
git commit -m "refactor(core): rename the spec formatter and add plan entry points

The consolidated-plan critic scores different things under the same five
field names - readability means diagram syntax, not code style - so reusing
the specification labels would stamp a false description on plan documents.
Share the skeleton, split only the labels.

FormatUnverifiedPlan takes no ReviewResult: a document that never entered
the pipeline has no score to report, and an absent parameter cannot leak.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: 캐시 포맷 버전 게이트

**Files:**
- Modify: `src/ReSet.Core/Models/CacheEntry.cs:6-17`
- Modify: `src/ReSet.Core/Services/CacheManager.cs` (`IsCacheValid` `:84-86` 부근, `UpdateCache` `:217-228`)
- Test: `tests/ReSet.Core.Tests/CacheManagerTests.cs`

**Interfaces:**
- Consumes: 없음 (Task 1과 독립)
- Produces: `CacheEntry.FormatVersion` (int, 레거시 JSON에서 0)

- [ ] **Step 1: 실패 테스트를 작성한다**

`tests/ReSet.Core.Tests/CacheManagerTests.cs` 끝에 추가한다. 파일 상단에 다음 두 개를 추가한다 — `using System.Linq;`, `using System.Text.Json.Nodes;`.

**JSON을 문자열 치환으로 조작하지 않는다.** `WriteIndented = true`로 직렬화된 인덱스에서 `"FormatVersion": 1`만 문자열로 지우면 후행 쉼표가 남아 JSON이 깨지고, `LoadCacheIndex`가 던진 예외를 `IsCacheValid`의 soft-fail이 삼켜 `false`를 반환한다. 그러면 테스트는 통과하지만 **버전 게이트를 전혀 검증하지 못한다.** `JsonNode`로 구조를 다뤄 이 거짓 통과를 막는다.

`JsonNode`를 쓰는 두 번째 이유: `CacheManager`는 `JsonStringEnumConverter`로 직렬화하므로 기본 옵션의 `JsonSerializer.Deserialize<CacheIndex>`는 문자열로 기록된 enum에서 실패한다.

```csharp
        [Fact]
        public void IsCacheValid_ReturnsFalse_ForEntriesWrittenBeforeTheFormatVersionExisted()
        {
            // 수정 이전 코드는 종료 상태와 무관하게 캐시를 썼다. 그 엔트리가 히트하면
            // 파이프라인은 무조건 Passed를 반환하고(VerificationPipelineOrchestrator.cs:164, :277)
            // 미검증 문서가 "통과"로 재발행된다. 어느 레거시 엔트리가 미검증이었는지
            // 판별할 방법이 없으므로 전량 무효화한다.
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "TestSp", CodeObjectType.Procedure);
            var hash = "expectedcompositehash12345";
            var specContent = "# Spec Report for TestSp";

            var specFilePath = _paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specFilePath)!);
            File.WriteAllText(specFilePath, specContent);

            // 정상 엔트리를 만든 뒤 FormatVersion만 제거해 레거시 JSON을 재현한다.
            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROC dbo.TestSp AS SELECT 1;" },
                hash,
                _paths,
                specContent);

            var indexPath = Path.Combine(_tempOutputDir, ".sp_cache_index.json");
            var root = JsonNode.Parse(File.ReadAllText(indexPath))!;
            foreach (var pair in root["Entries"]!.AsObject())
            {
                pair.Value!.AsObject().Remove("FormatVersion");
            }
            File.WriteAllText(indexPath, root.ToJsonString());

            // 인덱스가 여전히 유효한 JSON이어야 한다. 깨진 JSON이면 soft-fail 경로가
            // false를 반환해 게이트를 검증하지 않은 채 테스트가 통과해 버린다.
            var rewritten = File.ReadAllText(indexPath);
            Assert.DoesNotContain("FormatVersion", rewritten);
            Assert.NotNull(JsonNode.Parse(rewritten));

            // 해시도 경로도 파일 내용도 전부 일치하지만 포맷 버전이 없으므로 미스여야 한다.
            Assert.False(_cacheManager.IsCacheValid(key, hash, _paths));
        }

        [Fact]
        public void UpdateCache_StampsTheCurrentFormatVersion()
        {
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "TestSp", CodeObjectType.Procedure);
            var specContent = "# Spec Report for TestSp";
            var specFilePath = _paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specFilePath)!);
            File.WriteAllText(specFilePath, specContent);

            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROC dbo.TestSp AS SELECT 1;" },
                "hash",
                _paths,
                specContent);

            // CacheManager는 JsonStringEnumConverter로 직렬화하므로 기본 옵션의
            // Deserialize<CacheIndex>는 문자열 enum에서 실패한다. JsonNode로 읽는다.
            var root = JsonNode.Parse(
                File.ReadAllText(Path.Combine(_tempOutputDir, ".sp_cache_index.json")))!;
            var entry = root["Entries"]!.AsObject().Single().Value!;

            Assert.Equal(1, (int)entry["FormatVersion"]!);
        }

        [Fact]
        public void IsCacheValid_ReturnsFalse_ForEntriesFromAFutureFormatVersion()
        {
            // 신버전으로 캐시를 쌓은 뒤 구버전 바이너리로 롤백하면, '보다 작음' 검사는
            // 구버전이 해석할 수 없는 엔트리를 히트시킨다. 정확히 일치할 때만 신뢰한다.
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "TestSp", CodeObjectType.Procedure);
            var hash = "hash";
            var specContent = "# Spec Report for TestSp";
            var specFilePath = _paths.ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specFilePath)!);
            File.WriteAllText(specFilePath, specContent);

            _cacheManager.UpdateCache(
                key,
                new SpDefinition { DdlText = "CREATE PROC dbo.TestSp AS SELECT 1;" },
                hash,
                _paths,
                specContent);

            var indexPath = Path.Combine(_tempOutputDir, ".sp_cache_index.json");
            var root = JsonNode.Parse(File.ReadAllText(indexPath))!;
            foreach (var pair in root["Entries"]!.AsObject())
            {
                pair.Value!["FormatVersion"] = 99;
            }
            File.WriteAllText(indexPath, root.ToJsonString());

            Assert.False(_cacheManager.IsCacheValid(key, hash, _paths));
        }
```

`UpdateCache_And_IsCacheValid_ReturnsTrue_WhenBothExistAndMatch`(`:202`)가 정상 왕복 히트를 이미 고정하고 있으므로 별도 테스트를 추가하지 않는다.

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CacheManagerTests"`
Expected: 3건 실패. `IsCacheValid_ReturnsFalse_ForEntriesWrittenBeforeTheFormatVersionExisted`는 `Assert.DoesNotContain("FormatVersion", stripped)` 이전 단계에서 컴파일 실패(`CacheEntry`에 `FormatVersion` 없음, CS1061)

- [ ] **Step 3: 모델에 필드를 추가한다**

`src/ReSet.Core/Models/CacheEntry.cs`의 `CacheEntry` 클래스에 추가한다.

```csharp
        // 캐시 스키마 버전. 이 키가 없는 레거시 JSON은 0으로 역직렬화되어 무효 처리된다.
        // 수정 이전 코드는 검증 종료 상태와 무관하게 엔트리를 기록했고, 어느 것이
        // 미검증이었는지 판별할 정보가 저장되어 있지 않다.
        public int FormatVersion { get; set; }
```

- [ ] **Step 4: 게이트와 기록을 구현한다**

`src/ReSet.Core/Services/CacheManager.cs`의 `CacheIndexFileName` 상수 옆에 추가한다.

```csharp
        private const int CurrentCacheFormatVersion = 1;
```

`IsCacheValid`의 `TryGetEntry` 성공 블록 맨 앞, `currentSpecContentHash` 계산보다 **앞에** 조기 반환을 넣는다.

```csharp
                if (cacheIndex != null &&
                    TryGetEntry(cacheIndex, objectKey, outputPaths, out var entry))
                {
                    // 파일 읽기와 해시 계산보다 먼저 판정한다. 해석할 수 없는 스키마의
                    // 엔트리는 내용이 일치하더라도 신뢰할 근거가 없다.
                    if (entry.FormatVersion != CurrentCacheFormatVersion)
                    {
                        Log.Information(
                            "캐시 미스(포맷 버전 {EntryVersion} != {CurrentVersion}) - 코드 객체: {ObjectKey}",
                            entry.FormatVersion,
                            CurrentCacheFormatVersion,
                            cacheKey);
                        return false;
                    }

                    string currentSpecContentHash = string.Empty;
```

`UpdateCache`의 `var entry = new CacheEntry { ... }` 초기화에 `FormatVersion`을 추가한다. `ProcedureName` 바로 아래에 둔다.

```csharp
                    var entry = new CacheEntry
                    {
                        ProcedureName = $"{objectKey.Schema}.{objectKey.Name}",
                        FormatVersion = CurrentCacheFormatVersion,
                        ObjectKey = objectKey,
```

`MigrateLegacyCaches`는 **변경하지 않는다.** 병합된 레거시 엔트리는 `FormatVersion = 0`인 채로 남아 다음 조회에서 미스가 되어야 한다. 병합 시점에 버전을 채우면 무효화의 목적이 무너진다.

- [ ] **Step 5: 테스트를 실행해 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CacheManagerTests"`
Expected: 전부 통과. 특히 기존 `UpdateCache_And_IsCacheValid_ReturnsTrue_WhenBothExistAndMatch`가 계속 통과해야 한다 — 정상 왕복이 깨졌다면 `UpdateCache`의 `FormatVersion` 대입이 누락된 것이다

- [ ] **Step 6: 커밋**

```bash
git add -A
git commit -m "fix(cache): invalidate entries written before outcome gating existed

Pre-fix code cached regardless of the verification outcome, and a hit
returns Passed unconditionally, so a rejected document gets re-published
with a 통과 header while its CAUTION banner is still in the body - the
banner-stripping regex only targets [!NOTE].

The check is != rather than < so a rollback to an older binary does not
trust entries it cannot interpret.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: dynamic 영역 L1 플래그 갱신

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:698-723`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: 없음 (내부 동작 수정)

**배경:** `consolidatedL1Valid`는 `:606`에서 설정되고 자가 수정 후 `:624`에서 갱신되지만, L2 결함 보완 재생성이 L1을 통과했을 때는 갱신되지 않는다. 그 결과 `:733`이 L1을 통과한 문서에 `L1 미통과` 배너를 단다.

- [ ] **Step 1: 실패 테스트를 작성한다**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`에 추가한다. `RunCodeObjectPipelineAsync_Sectional...L1Exhausted` 테스트(`:2418` 부근)의 구조를 그대로 따르되, AI 응답을 순차적으로 바꾼다.

`MechanicalValidator`는 대체할 수 없으므로 L1 통과/실패는 실제 본문으로 만든다.

```csharp
        [Fact]
        public async Task RunCodeObjectPipelineAsync_Sectional_L2FixThatPassesL1_DropsTheL1ExhaustedBanner()
        {
            // consolidatedL1Valid는 자가 수정 직후(:624)까지만 갱신되고, L2 결함 보완
            // 재생성본이 L1을 통과해도(:698-719) 그대로 false로 남아 있었다. 그 결과
            // L1을 통과한 최종 문서에 "L1 미통과" 배너가 붙는다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_SectionalL2Recovers", CodeObjectType.Procedure);

            aiService.ProviderName.Returns("Ollama");
            criticService.ProviderName.Returns("Ollama");

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_SectionalL2Recovers", DdlText = "SELECT 1;"
                }));

            // 필수 H2 헤더가 전부 있는 본문만 L1을 통과한다.
            var l1Invalid = "# 헤더가 없는 본문";
            var l1Valid =
                "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";

            // 이 경로에서 GenerateSpecificationAsync는 순서대로: 후보 3개, 합성본,
            // 자가 수정본, 그리고 L2 결함 보완본으로 호출된다. 마지막만 L1을 통과시킨다.
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = l1Invalid }),   // 후보 1
                    Task.FromResult(new AiResult { Content = l1Invalid }),   // 후보 2
                    Task.FromResult(new AiResult { Content = l1Invalid }),   // 후보 3
                    Task.FromResult(new AiResult { Content = l1Invalid }),   // 합성본
                    Task.FromResult(new AiResult { Content = l1Invalid }),   // 자가 수정본 (여전히 실패)
                    Task.FromResult(new AiResult { Content = l1Valid }));    // L2 결함 보완본 (통과)

            var defectiveReview = new ReviewResult
            {
                HasDefects = true, FeedbackComment = "결함이 있습니다",
                ScoreAccuracy = 5, ScoreCrud = 5, ScoreInterface = 5, ScoreException = 5, ScoreReadability = 5
            };
            var cleanReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
            };

            // 후보 채점 → 최종 합성본 검토(결함) → 보완본 재검토(통과) 순으로 호출된다.
            // 마지막 재검토만 깨끗한 결과를 준다.
            criticService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(defectiveReview), Task.FromResult(defectiveReview),
                    Task.FromResult(defectiveReview), Task.FromResult(defectiveReview),
                    Task.FromResult(defectiveReview), Task.FromResult(defectiveReview),
                    Task.FromResult(cleanReview));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "ollama-test", criticService: criticService, actorEffort: "dynamic");

            var result = await orchestrator.RunCodeObjectPipelineAsync(
                "Server=(local);Database=PaymentDB", key, 1, "Ollama", "rules", true,
                Path.Combine(Path.GetTempPath(), $"ReSet-L2Recovers-{Guid.NewGuid():N}"), false,
                cancellationToken: CancellationToken.None, directDependenciesOnly: true);

            // 최종 문서는 L1을 통과했다. L1 미통과 배너가 붙어서는 안 된다.
            Assert.DoesNotContain("L1 기계 검증을 통과하지 못했습니다", result.SpecMarkdown);
            Assert.NotEqual(VerificationOutcome.L1Exhausted, result.Outcome);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RunCodeObjectPipelineAsync_Sectional_L2FixThatPassesL1_DropsTheL1ExhaustedBanner"`
Expected: FAIL — `Assert.DoesNotContain` 실패. 최종 마크다운에 "L1 기계 검증을 통과하지 못했습니다"가 들어 있다

**만약 다른 이유로 실패한다면** (예: AI 호출 횟수가 예상과 달라 시퀀스가 어긋남) 먼저 실제 호출 순서를 확인한다. NSubstitute는 시퀀스를 소진하면 마지막 값을 반복 반환하므로, 호출이 예상보다 많으면 마지막 값이 계속 나온다. `criticService.ReceivedCalls().Count()`를 임시로 출력해 실제 횟수를 맞춘 뒤 진행한다. 시퀀스 길이를 조정하는 것은 허용되지만, **단언 두 줄은 바꾸지 않는다.**

- [ ] **Step 3: 플래그 갱신을 구현한다**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`의 `if (fixL1Result.IsValid)` 블록(`:699`)에 두 줄을 추가한다.

```csharp
                            if (fixL1Result.IsValid)
                            {
                                specificationMarkdown = fixL1Result.CleansedMarkdown ?? finalConsolidatedFixResult.Content;
                                // 보완본이 L1을 통과했으므로 이전 시도의 L1 판정을 그대로 들고
                                // 가면 안 된다. 최종 배너 삽입부(:733)가 이 플래그를 본다.
                                consolidatedL1Valid = true;
                                consolidatedL1Errors = fixL1Result.Errors;
                                spDef.RawPromptContext = ...;
```

`else` 분기(`:720-723`)는 변경하지 않는다. 이전 버전을 최종본으로 유지하므로 기존 플래그가 이미 정확하다.

- [ ] **Step 4: 테스트를 실행해 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"`
Expected: 전부 통과. 특히 기존 `...L1Exhausted` 테스트가 계속 통과해야 한다 — 그 테스트의 Critic 스텁은 `HasDefects = false`를 돌려주므로 `:682`의 `if (finalL2Result != null && finalL2Result.HasDefects)` 가드에서 단락되어 L2 결함 보완 블록 자체가 실행되지 않는다. 즉 `fixL1Result`는 계산조차 되지 않으며(“`fixL1Result.IsValid`가 false로 남는다”는 설명은 사실이 아니다), 새 분기는 애초에 도달 불가라서 영향을 받지 않는다

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "fix(verification): refresh the L1 flag when the L2 fix passes validation

In the dynamic region, consolidatedL1Valid stopped being updated after the
self-correction attempt, so a defect-fix regeneration that did pass L1 still
carried the L1 미통과 banner. The direction was safe - claiming worse than
reality - but a banner that is factually wrong erodes the banner itself.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: `ConsolidatedPipelineResult` 도입, `planReview` 추적, 산출물 배선

**Files:**
- Create: `src/ReSet.Core/Models/ConsolidatedPipelineResult.cs`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1561-1807`
- Modify: `src/ReSet.Cli/Program.cs:727-730`, `:1175-1178`, `:1629-1633`, `:1647-1651`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: `VerificationOutcome`, `ReviewResult`, `AiResult`
- Consumes (배선 단계): `VerificationDocumentFormatter.FormatConsolidatedPlan` / `FormatUnverifiedPlan` (Task 1)
- Produces:
  - `ReSet.Core.Models.ConsolidatedPipelineResult(string? Plan, AiResult? Result, ReviewResult? Review, VerificationOutcome Outcome)`
  - `VerificationPipelineOrchestrator.RunConsolidatedPipelineAsync(...) → Task<ConsolidatedPipelineResult>` (파라미터는 불변)

**이 태스크는 반환 타입 변경과 호출부 배선을 함께 담는다.** 둘을 나누면 중간 커밋이 솔루션 전체 빌드를 깨뜨려 `git bisect`가 그 지점에서 쓸모없어진다. 리뷰어가 한쪽만 승인하는 것도 불가능하다 — 타입만 바꾼 상태로는 컴파일되지 않는다.

**참고:** `RunConsolidatedPipelineAsync`는 인터페이스에 선언되어 있지 않다. 구상 클래스만 수정하면 된다.

- [ ] **Step 1: 실패 테스트를 작성한다**

기존 테스트들은 `result.Plan`과 `result.Result`로 접근하므로 레코드 전환 후에도 그대로 동작한다. 새 필드만 고정한다.

```csharp
        [Fact]
        public async Task RunConsolidatedPipelineAsync_ReportsTheOutcomeAndReviewToTheCaller()
        {
            // planOutcome은 :1584부터 정확히 추적되지만 반환 튜플에 없어서 호출부가
            // 알 수 없었다(:1581-1583 주석). 그 때문에 BatchMigrationPlan.md에 검증
            // 상태가 전혀 기록되지 않았다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var validPlan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = validPlan }));

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "TestJobOutcomeReported", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.Equal(VerificationOutcome.Passed, result.Outcome);
            Assert.Same(goodReview, result.Review);
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_WhenTheReviewCallFails_ReportsReviewNotRunWithNoScores()
        {
            // 리뷰를 수행하지 못한 계획서에 이전 점수가 실리면 안 된다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var validPlan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = validPlan }));

            // await 시점에 예외가 던져져 :1677의 catch로 들어간다.
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ReviewResult>(new InvalidOperationException("리뷰 서비스 장애")));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "TestJobReviewFailed", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
            Assert.Null(result.Review);
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_L3Feedback_ClearsTheReviewAlongWithTheOutcome()
        {
            // 명세서 경로(:1451-1453)는 재생성 시 finalReview를 null로 비운다.
            // 계획서 경로도 같아야 한다 - 재생성된 계획서에 이전 계획서의 점수가
            // 남으면 "한 번도 리뷰받지 않은 문서가 이전 점수를 자칭"하게 된다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var initial = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            var regenerated = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\nC-->D\n```";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = initial }), Task.FromResult(new AiResult { Content = regenerated }));

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Add C to D" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "TestJobFeedbackClearsReview", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
            Assert.Null(result.Review);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RunConsolidatedPipelineAsync"`
Expected: 컴파일 실패 — 튜플에 `Outcome`/`Review` 멤버가 없음 (CS1061)

- [ ] **Step 3: 반환 타입을 만든다**

`src/ReSet.Core/Models/ConsolidatedPipelineResult.cs`를 만든다.

```csharp
namespace ReSet.Core.Models;

/// <summary>
/// 통합 배치 계획 파이프라인의 결과. 계획서가 어떤 상태로 끝났는지(Outcome)와
/// 그 판정의 근거가 된 L2 리뷰(Review)를 호출부까지 전달한다. 이전 튜플 반환은
/// 이 둘을 담지 못해 산출물에 검증 상태를 기록할 수 없었다.
/// </summary>
/// <param name="Plan">확정된 계획서 본문. 실패하거나 취소되면 null.</param>
/// <param name="Result">최종 생성 호출의 AI 결과(프롬프트 컨텍스트·추론 로그용).</param>
/// <param name="Review">최종 판정의 근거가 된 L2 리뷰. 리뷰를 수행하지 못했거나
/// L3 피드백으로 재생성된 경우 null이며, 이때 점수를 실어서는 안 된다.</param>
/// <param name="Outcome">검증 파이프라인 종료 상태.</param>
public sealed record ConsolidatedPipelineResult(
    string? Plan,
    AiResult? Result,
    ReviewResult? Review,
    VerificationOutcome Outcome);
```

- [ ] **Step 4: 시그니처와 `planReview`를 구현한다**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1561`의 시그니처를 바꾼다.

```csharp
        public async Task<ConsolidatedPipelineResult> RunConsolidatedPipelineAsync(
```

`:1584`의 `planOutcome` 선언 옆에 리뷰 변수를 추가하고, `:1581-1583`의 낡은 주석을 갱신한다.

```csharp
            // 계획서의 종료 상태와 그 근거 리뷰. 반환 레코드로 호출부까지 전달되어
            // 산출물 헤더(VerificationDocumentFormatter.FormatConsolidatedPlan)와
            // 승인 화면(RequestHumanReviewAsync)이 같은 사실을 쓴다.
            var planOutcome = VerificationOutcome.Passed;
            ReviewResult? planReview = null;
```

각 종료 지점을 다음과 같이 맞춘다.

| 위치 | 변경 |
|---|---|
| `:1635` `return (null, null);` (생성 실패) | `return new ConsolidatedPipelineResult(null, null, null, planOutcome);` |
| `:1655` L1 소진 | `planOutcome = L1Exhausted;` 유지. `planReview`는 null 그대로 (L2 미수행) |
| `:1703` 품질 미달 | `planOutcome = QualityRejected;` 아래에 `planReview = l2Result;` 추가 |
| `:1715` 리뷰 미수행 | `planOutcome = ReviewNotRun;` 유지. `planReview`는 null 그대로 |
| `:1722` 통과 분기 | `_userInteraction.NotifyValidationSuccess(jobName);` 앞에 `planReview = l2Result;` 추가 |
| `:1741` 배치 모드 반환 | `return new ConsolidatedPipelineResult(consolidatedPlan, finalAiResult, planReview, planOutcome);` |
| `:1750` L3 승인 반환 | `return new ConsolidatedPipelineResult(consolidatedPlan, finalAiResult, planReview, planOutcome);` |
| `:1754` 취소 반환 | `return new ConsolidatedPipelineResult(null, null, null, planOutcome);` |
| `:1804-1805` L3 재생성 | `planOutcome = ReviewNotRun;` 아래에 `planReview = null;` 추가 |

`:1799-1803`의 주석에서 "반환 튜플이 ReviewResult를 포함하지 않는다"는 문장은 더 이상 사실이 아니므로 지운다. 대신 다음으로 대체한다.

```csharp
                    // 이 계획서도 전체가 재생성되어 L1만 재검사할 뿐 L2는 재수행되지 않는다.
                    // 이전 판정과 점수를 그대로 들고 가면 재생성된, 한 번도 리뷰받지 않은
                    // 계획서가 이전 계획서의 통과 판정을 자칭하게 된다. 명세서 경로
                    // (:1451-1453)와 동일하게 리뷰를 비우고 미수행으로 명시한다.
                    consolidatedPlan = rePlan;
                    planReview = null;
                    planOutcome = VerificationOutcome.ReviewNotRun;
```

- [ ] **Step 5: Core 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RunConsolidatedPipelineAsync"`
Expected: 전부 통과. 기존 통합 파이프라인 테스트들도 `result.Plan` / `result.Result` 접근이 그대로 유지되므로 통과해야 한다

이 시점에 `src/ReSet.Cli`는 컴파일되지 않는다 — 호출부가 아직 튜플을 기대한다. **여기서 커밋하지 않는다.** 다음 세 단계에서 배선을 마친 뒤 하나의 커밋으로 남긴다.

- [ ] **Step 6: 배치 통합 경로를 교체한다**

`src/ReSet.Cli/Program.cs:727-730`을 다음으로 바꾼다.

```csharp
                            var planFileName = Path.Combine(docsDir, "BatchMigrationPlan.md");
                            await File.WriteAllTextAsync(
                                planFileName,
                                VerificationDocumentFormatter.FormatConsolidatedPlan(
                                    consolidatedPlan,
                                    pipelineResult.Review,
                                    pipelineResult.Outcome,
                                    provider,
                                    modelName,
                                    consolidatorEffort,
                                    DateTime.Now));
```

`effortSuffix`와 `metadataHeader` 지역 변수는 이 블록에서만 쓰이므로 함께 지운다.

- [ ] **Step 7: TUI 통합 경로를 교체한다**

`src/ReSet.Cli/Program.cs:1175-1178`을 Step 6과 **동일한 코드**로 바꾼다. 두 경로는 같은 산출물을 쓰며 같은 헤더를 가져야 한다.

```csharp
                            var planFileName = Path.Combine(docsDir, "BatchMigrationPlan.md");
                            await File.WriteAllTextAsync(
                                planFileName,
                                VerificationDocumentFormatter.FormatConsolidatedPlan(
                                    consolidatedPlan,
                                    pipelineResult.Review,
                                    pipelineResult.Outcome,
                                    provider,
                                    modelName,
                                    consolidatorEffort,
                                    DateTime.Now));
```

여기서도 그 블록의 `effortSuffix`와 `metadataHeader`를 지운다.

- [ ] **Step 8: 단일 SP 경로를 교체한다**

`src/ReSet.Cli/Program.cs:1630-1633`에서 `scoreHeader`와 `metadataHeader` 선언을 지운다.

**`effortSuffix`(`:1629`)는 지우지 않는다.** `:1669` 부근의 `thinkingHeader`가 이 변수를 쓰므로 함께 지우면 빌드가 깨진다.

`:1647-1651`의 계획서 저장을 다음으로 바꾼다.

```csharp
            if (!string.IsNullOrEmpty(migrationPlan))
            {
                // 이 계획서는 GenerateBatchMigrationPlanAsync가 만든 그대로이며 L1도 L2도
                // 거치지 않는다. 명세서의 점수를 여기에 실으면 계획서가 그 점수를 받은
                // 것처럼 읽히므로, 검증 없음을 밝히고 근거 명세서의 상태만 전달한다.
                var planFileName = Path.Combine(docsDir, "BatchMigrationPlan.md");
                await File.WriteAllTextAsync(
                    planFileName,
                    VerificationDocumentFormatter.FormatUnverifiedPlan(
                        migrationPlan, outcome, provider, modelName, effort, DateTime.Now));
            }
```

- [ ] **Step 9: 클린 빌드하고 잔재를 확인한다**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "error|warning" | sort | uniq -c
```
Expected: error 0건, warning 8건(전부 `DbMetadataServiceTests.cs`)

```bash
grep -n "metadataHeader\|scoreHeader" src/ReSet.Cli/Program.cs
```
Expected: 출력 없음

```bash
grep -n "effortSuffix" src/ReSet.Cli/Program.cs
```
Expected: `:1629` 선언과 `:1669` 부근 `thinkingHeader` 사용 2곳만 남음

- [ ] **Step 10: 전체 테스트를 실행한다**

Run: `dotnet test`
Expected: 전부 통과

- [ ] **Step 11: 커밋**

```bash
git add -A
git commit -m "feat(verification): carry the plan outcome into the plan documents

RunConsolidatedPipelineAsync tracked planOutcome correctly but the tuple
could not carry it out, so BatchMigrationPlan.md recorded no verification
state at all - least of all in batch mode, which returns before the L3 loop.
planReview completes the symmetry with the specification path: both now
clear the review when a document is regenerated without being re-reviewed.

The single-SP plan never enters the pipeline (Program.cs:662) yet carried
'> **AI 최종 신뢰도**: 87/100점' - a number earned by Spec.md. Gating that on
the outcome would have left the false claim intact whenever the specification
passed, so the score is gone and the source is named.

The type change and the wiring ship together: split apart, the intermediate
commit does not compile.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

**테스트에 관한 정직한 한계:** `Program.cs`의 배선 구간은 최상위 문 안의 지역 흐름과 `SaveOutputsAsync` 지역 함수라 단위 테스트로 격리할 수 없다. 배선의 검증은 (a) 렌더링 동작을 이미 고정한 Task 1의 포매터 테스트, (b) 클린 빌드, (c) 옛 조립 코드가 남아 있지 않은지 확인하는 grep 세 가지다. 배선 자체를 위한 새 자동화 테스트를 만들지 않는다.

---

### Task 5: `SpecHeaderReader` 별칭 커버리지

**Files:**
- Test: `tests/ReSet.Core.Tests/SpecHeaderReaderTests.cs`

**Interfaces:**
- Consumes: `ReSet.Cli.SpecHeaderReader.Read(string) → SpecHeader`
- Produces: 없음 (테스트 전용)

**배경:** 소비부(`ConsoleUserInteraction.cs:105-109`)는 파싱 실패를 `?? 10`(만점)으로 폴백한다. 별칭 하나가 어긋나면 화면에 진짜 점수와 지어낸 만점이 섞여 나온다. 이 태스크는 프로덕션 코드를 바꾸지 않는다 — 기존 동작을 고정하기만 한다.

- [ ] **Step 1: 별칭 테스트를 작성한다**

`tests/ReSet.Core.Tests/SpecHeaderReaderTests.cs` 끝에 추가한다. 기존 5개 테스트는 변경하지 않는다.

```csharp
    // 소비부(ConsoleUserInteraction.cs:105-109)는 파싱 실패를 만점으로 폴백한다.
    // 별칭 하나가 어긋나면 지어낸 10점이 진짜 점수와 섞여 표시되므로 전부 고정한다.

    [Theory]
    [InlineData("AiConfidenceScore")]
    [InlineData("종합 신뢰도 점수")]
    [InlineData("종합 신뢰도")]
    [InlineData("종합신뢰도")]
    public void Read_AcceptsEveryOverallScoreAlias(string key)
    {
        var header = SpecHeaderReader.Read($"---\n{key}: 80\n---\n\n# 본문");

        Assert.Equal(80, header.NormalizedScore);
    }

    [Theory]
    [InlineData("AccuracyScore")]
    [InlineData("정합성 점수")]
    [InlineData("정합성")]
    public void Read_AcceptsEveryAccuracyAlias(string key)
    {
        var header = SpecHeaderReader.Read($"---\n{key}: 9/10\n---\n\n# 본문");

        Assert.Equal(9, header.Accuracy);
    }

    [Theory]
    [InlineData("CrudScore")]
    [InlineData("CRUD 점수")]
    [InlineData("CRUD")]
    public void Read_AcceptsEveryCrudAlias(string key)
    {
        var header = SpecHeaderReader.Read($"---\n{key}: 8/10\n---\n\n# 본문");

        Assert.Equal(8, header.Crud);
    }

    [Theory]
    [InlineData("ReadabilityScore")]
    [InlineData("가독성 점수")]
    [InlineData("가독성")]
    public void Read_AcceptsEveryReadabilityAlias(string key)
    {
        var header = SpecHeaderReader.Read($"---\n{key}: 7/10\n---\n\n# 본문");

        Assert.Equal(7, header.Readability);
    }

    [Theory]
    [InlineData("ExceptionScore")]
    [InlineData("예외처리 점수")]
    [InlineData("예외처리")]
    [InlineData("예외 처리 점수")]
    [InlineData("예외 처리")]
    public void Read_AcceptsEveryExceptionAlias(string key)
    {
        var header = SpecHeaderReader.Read($"---\n{key}: 6/10\n---\n\n# 본문");

        Assert.Equal(6, header.Exception);
    }

    [Fact]
    public void Read_StripsCommentThenParenthesisThenDenominatorInThatOrder()
    {
        // 실제 산출물은 분모와 주석을 함께 싣는다
        // (VerificationDocumentFormatter: "정합성 점수: 9/10 # SQL 대비 기능 정합성").
        // 세 정규화가 이 순서로 적용되지 않으면 값이 어긋난다.
        var markdown =
            "---\n" +
            "종합 신뢰도: 80 # 100점 만점 기준 AI 최종 신뢰도\n" +
            "정합성 점수: 9/10 # SQL 대비 기능 정합성\n" +
            "CRUD 점수: 8 (양호)\n" +
            "가독성 점수: 7/10 (우수) # 코드 가독성 및 표준 준수\n" +
            "---\n\n# 본문";

        var header = SpecHeaderReader.Read(markdown);

        Assert.Equal(80, header.NormalizedScore);
        Assert.Equal(9, header.Accuracy);
        Assert.Equal(8, header.Crud);
        Assert.Equal(7, header.Readability);
    }
```

- [ ] **Step 2: 테스트를 실행한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~SpecHeaderReaderTests"`
Expected: 전부 통과 (기존 동작을 고정하는 테스트이므로 RED 단계가 없다)

**하나라도 실패하면 그것은 발견된 결함이다.** 테스트를 기대값에 맞춰 고치지 말고 멈춘 뒤 보고한다. 별칭이 실제로 파싱되지 않는다면 그 항목은 프로덕션에서 만점으로 폴백되고 있다는 뜻이다.

- [ ] **Step 3: 커밋**

```bash
git add -A
git commit -m "test(cli): pin every SpecHeaderReader alias

The consumer falls back to a perfect 10 when a sub-score fails to parse
(ConsoleUserInteraction.cs:105-109), and scoreFound only checks the overall
score - so a single broken alias shows an invented 10 next to real numbers.
All 19 keys and the comment/parenthesis/denominator stripping order are now
covered.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: 문서 갱신

**Files:**
- Modify: `AGENTS.md` (테스트 수, 포매터 타입 이름)
- Modify: `docs/architecture.md` (포매터 타입 이름, 통합 파이프라인 반환 타입)

**Interfaces:**
- Consumes: Task 1~5의 최종 상태
- Produces: 없음

- [ ] **Step 1: 실제 테스트 수를 확인한다**

```bash
dotnet test 2>&1 | tail -5
```

출력에 나온 실제 통과 수를 기록한다. **예상치를 적지 말고 실제 실행 결과를 쓴다.**

- [ ] **Step 2: `AGENTS.md`를 갱신한다**

테스트 수를 Step 1의 실제값으로 바꾼다. `SpecificationDocumentFormatter`를 언급하는 곳이 있으면 `VerificationDocumentFormatter`로 바꾼다.

```bash
grep -n "SpecificationDocumentFormatter\|355" AGENTS.md docs/architecture.md
```

- [ ] **Step 3: `docs/architecture.md`를 갱신한다**

포매터 타입 이름과, 통합 파이프라인이 `ConsolidatedPipelineResult`를 반환한다는 사실을 반영한다. 문서에 해당 서술이 없다면 추가하지 않는다 — 없는 절을 새로 만들지 않는다.

- [ ] **Step 4: 최종 확인**

```bash
dotnet clean && dotnet build 2>&1 | grep -c "warning"
dotnet test
grep -rn "SpecificationDocumentFormatter" src/ tests/ docs/ AGENTS.md
```
Expected: 경고 8건, 테스트 전부 통과, grep 출력 없음

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "docs: update the test count and formatter name after the follow-ups

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## 실행 순서와 의존 관계

```
Task 1 (포매터) ──→ Task 4 (반환 타입 + 배선) ─┐
Task 2 (캐시)   ──────────────────────────────┤
Task 3 (L1 플래그) ───────────────────────────┼─→ Task 6 (문서)
Task 5 (별칭)   ──────────────────────────────┘
```

Task 2, 3, 5는 서로 독립이며 Task 1/4와도 독립이다. Task 4만 Task 1을 기다린다 — 포매터 진입점이 있어야 배선할 수 있다.

## 자체 검토 결과

**스펙 커버리지**

| 스펙 요구사항 | 담당 태스크 |
|---|---|
| A 캐시 포맷 버전 (`!=` 비교, `MigrateLegacyCaches` 불변) | Task 2 |
| B dynamic 영역 L1 플래그 | Task 3 |
| C `ConsolidatedPipelineResult` + `planReview` 5개 지점 | Task 4 Step 1-5 |
| C `Program.cs` 3개 호출부 | Task 4 Step 6-8 |
| D 단일 SP 계획서 점수 제거 | Task 4 Step 8 |
| E 별칭 19개 + 정규화 순서 | Task 5 |
| `VerificationDocumentFormatter` 개명, 3개 진입점, `ScoreLabels` | Task 1 |
| `FormatConsolidatedPlan`의 `showScores` 규칙 | Task 1 Step 2 두 번째 테스트 |
| `ConsoleUserInteraction.cs:101` 주석 갱신 | Task 1 Step 5 |

누락 없음.

**타입 일관성**

`FormatSpecification` / `FormatConsolidatedPlan` / `FormatUnverifiedPlan`, `ConsolidatedPipelineResult(Plan, Result, Review, Outcome)`, `CacheEntry.FormatVersion`, `CurrentCacheFormatVersion` — Task 1과 2에서 정의한 이름이 Task 4에서 그대로 쓰인다.

**계획 수립 중 확인한 함정 세 가지**

1. `MechanicalValidator`는 인터페이스가 없고 메서드가 `virtual`이 아니라 NSubstitute로 대체할 수 없다. Task 3의 L1 통과/실패는 실제 마크다운 본문으로 유도해야 한다 (Global Constraints에 필수 H2 목록을 명시했다).
2. `Program.cs:1629`의 `effortSuffix`는 `:1669`의 `thinkingHeader`에서도 쓰인다. `metadataHeader`와 함께 지우면 빌드가 깨진다 (Task 4 Step 8에 명시했다).
3. Task 2의 테스트 초안이 문자열 치환으로 `FormatVersion` 키를 지우려 했다. 필드가 클래스 마지막에 선언되면 후행 쉼표가 남아 JSON이 깨지고, `LoadCacheIndex`의 예외를 `IsCacheValid`의 soft-fail이 삼켜 `false`를 반환한다 — 게이트를 검증하지 않은 채 **테스트가 통과하는 거짓 통과**다. `JsonNode`로 구조를 다루고, 재작성된 JSON이 파싱 가능한지 단언하도록 고쳤다.
