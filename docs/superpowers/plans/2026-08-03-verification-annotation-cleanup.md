# 검증 표기 정리 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 직전 사이클이 남긴 후속 과제 중 정답이 이미 정해진 다섯 건을 닫는다 — 삼켜지는 취소, 거짓 점수 설명, 모순된 소스 주석, 중복된 상태 표기, 표기 없는 정산 정책 문서.

**Architecture:** 점수 줄의 설명 주석을 없애면 `FormatSpecification`과 `FormatConsolidatedPlan`의 유일한 차이가 사라지므로 두 진입점을 `FormatVerifiedDocument` 하나로 합친다. 미검증 문서용 진입점은 `FormatUnverifiedDocument`로 일반화해 단일 SP 계획서와 정산 정책 문서를 함께 처리한다. 진입점의 축이 문서 종류가 아니라 보장 수준이 된다. 세 곳의 `catch { }`는 같은 파일이 이미 쓰는 형태로 바꿔 취소를 전파한다.

**Tech Stack:** .NET 10 / C#, xUnit, NSubstitute, Serilog, Spectre.Console

## Global Constraints

- 대상 저장소: `/Users/payletter/git-root/ReSet`, 기준 브랜치 `main` (설계 커밋 `4d9ff61`)
- 설계 문서: `docs/superpowers/specs/2026-08-03-verification-annotation-cleanup-design.md`
- API 키를 소스나 `appsettings.json`에 하드코딩하지 않는다
- Spectre.Console 출력에 들어가는 런타임 값은 `Markup.Escape()`로 감싼다
- `OperationCanceledException`은 절대 삼키지 않는다. IO/DB/AI 오류는 soft-fail한다
- 모든 신규 주석과 사용자 노출 문자열은 한국어로 작성한다
- 작업 시작 시점의 테스트는 **391개 전부 통과**. 클린 빌드 경고는 **고유 8건**(전부 `tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`의 기존 CS8600/CS8602). 이 수를 늘리지 않는다
- 경고 확인은 반드시 클린 빌드(`dotnet clean && dotnet build`)로 한다. 증분 빌드는 이전 경고를 재출력하지 않아 새 경고를 숨긴다
- **경고 개수를 셀 때 중복을 제거한다.** `dotnet build`는 각 경고를 두 번 출력하므로 단순 `grep -c`는 16을 반환한다. `sort -u`로 고유 개수를 확인한다
- 솔루션 파일은 `ReSet.slnx`다 (`.sln`은 없다)
- `MechanicalValidator`는 인터페이스가 없고 메서드가 `virtual`이 아니라 **NSubstitute로 대체할 수 없다.** L1 통과/실패는 반드시 실제 마크다운 본문으로 유도한다
  - 개별 명세서 필수 H2: `개요`, `파라미터 목록`, `CRUD 분석`, `로직 흐름 요약`, `비즈니스 흐름 시각화`
  - 통합 계획서 필수 H2: `통합 배치 아키텍처 개요`, `Mermaid 기반 통합 흐름도`, `단계별 이행 상세 및 의사코드`, `통합 데이터 정합성 검증 SQL 세트`
  - 두 경우 모두 유효한 ` ```mermaid ` 블록이 필요하다

## 파일 구조

| 파일 | 책임 | 작업 |
|---|---|---|
| `src/ReSet.Core/Services/VerificationDocumentFormatter.cs` | 문서 헤더 렌더링 | 진입점 3 → 2, 라벨 기계장치 삭제 (Task 1) |
| `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:462` | 재귀 분석 산출물 | 호출 개명 (Task 1) |
| `src/ReSet.Cli/Program.cs` | 산출물 저장 배선 | 호출 개명 5곳 (Task 1), 정산 문서 2곳 (Task 2) |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | 검증 파이프라인 | 주석 1줄 (Task 1), `catch` 3곳 (Task 4) |
| `src/ReSet.Cli/ConsoleUserInteraction.cs` | L3 승인 화면 | 중복 switch 제거 (Task 3) |
| `AGENTS.md`, `docs/architecture.md` | 문서 | 테스트 수·타입 표기 (Task 5) |

테스트는 기존 파일에 추가한다: `VerificationDocumentFormatterTests.cs`, `VerificationPipelineOrchestratorTests.cs`, `SpecHeaderReaderTests.cs`.

---

### Task 1: 포매터 진입점 통합과 라벨 제거

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationDocumentFormatter.cs` (전면 재작성)
- Modify: `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:462`
- Modify: `src/ReSet.Cli/Program.cs:730`, `:1186`, `:1650`, `:1667`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1586` (주석)
- Test: `tests/ReSet.Core.Tests/VerificationDocumentFormatterTests.cs`
- Test: `tests/ReSet.Core.Tests/SpecHeaderReaderTests.cs` (주석 문구만)

**Interfaces:**
- Consumes: `ReSet.Core.Models.VerificationOutcome`, `ReSet.Core.Models.ReviewResult`
- Produces:
  - `VerificationDocumentFormatter.FormatVerifiedDocument(string body, ReviewResult? review, VerificationOutcome outcome, string provider, string modelName, string? effort, DateTime timestamp) → string`
  - `VerificationDocumentFormatter.FormatUnverifiedDocument(string body, VerificationOutcome? sourceOutcome, string provider, string modelName, string? effort, DateTime timestamp) → string`
  - `VerificationDocumentFormatter.StatusLabel(VerificationOutcome outcome) → string` (변경 없음)

- [ ] **Step 1: 폐기되는 두 테스트를 지우고 대체 테스트를 쓴다**

`tests/ReSet.Core.Tests/VerificationDocumentFormatterTests.cs`에서 다음 두 테스트를 **삭제**한다. 두 라벨 테이블이 서로 다름을 단언하는 것이 존재 이유이므로, 라벨과 함께 폐기한다.

- `FormatConsolidatedPlan_UsesPlanSpecificScoreDescriptions`
- `FormatSpecification_KeepsSpecificationScoreDescriptions`

그 자리에 대체 테스트를 넣는다.

```csharp
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
```

- [ ] **Step 2: 기존 테스트의 호출 이름을 바꾼다**

같은 파일에서 이름만 치환한다. **단언은 한 줄을 제외하고 전부 그대로 둔다.**

- `VerificationDocumentFormatter.FormatSpecification(` → `VerificationDocumentFormatter.FormatVerifiedDocument(`
- `VerificationDocumentFormatter.FormatConsolidatedPlan(` → `VerificationDocumentFormatter.FormatVerifiedDocument(`
- `VerificationDocumentFormatter.FormatUnverifiedPlan(` → `VerificationDocumentFormatter.FormatUnverifiedDocument(`
- 테스트 메서드명 `FormatUnverifiedPlan_StatesThatTheDocumentItselfWasNeverVerified` → `FormatUnverifiedDocument_StatesThatTheDocumentItselfWasNeverVerified`
- 테스트 메서드명 `FormatUnverifiedPlan_NeverEmitsAnyScore` → `FormatUnverifiedDocument_NeverEmitsAnyScore`
- 테스트 메서드명 `FormatConsolidatedPlan_OmitsScoresWhenTheOutcomeIsNotScored` → `FormatVerifiedDocument_OmitsScoresWhenTheOutcomeIsNotScored`

**바뀌는 단언은 이 한 줄뿐이다.** `FormatUnverifiedDocument_StatesThatTheDocumentItselfWasNeverVerified` 안에서:

```csharp
Assert.Contains("이 계획서는 검증 파이프라인을 거치지 않았습니다", result);
```

를 다음으로 바꾼다.

```csharp
// 같은 메서드가 정산 정책 문서(계획서가 아니다)도 처리하게 되어 문구를 중립화했다.
Assert.Contains("이 문서는 검증 파이프라인을 거치지 않았습니다", result);
```

`Format_WithReview_…`, `Format_Passed_…`, `Format_ReviewNotRun_…`, `Format_L1Exhausted_…`, `Format_QualityRejected_…` 다섯 개는 `Assert.Contains("종합 신뢰도: 80", …)` 형태로 주석을 단언하지 않으므로 **단언을 한 글자도 바꾸지 않는다.** 이들은 지난 사이클 개명의 회귀 방어선이며 이번에도 같은 역할을 한다.

- [ ] **Step 3: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationDocumentFormatterTests"`
Expected: 컴파일 실패 — `FormatVerifiedDocument` / `FormatUnverifiedDocument` 메서드가 없음 (CS0117)

- [ ] **Step 4: 포매터를 재작성한다**

`src/ReSet.Core/Services/VerificationDocumentFormatter.cs`를 다음 내용으로 전면 교체한다.

```csharp
using ReSet.Core.Models;

namespace ReSet.Core.Services;

/// <summary>
/// 검증 산출물의 상단 헤더(YAML 프런트매터 + 메타 블록)를 렌더링한다.
///
/// 진입점은 문서 종류가 아니라 보장 수준으로 나뉜다. 정산 정책 문서와 단일 SP 계획서는
/// 종류가 전혀 다르지만 둘 다 파이프라인에 진입한 적이 없고, 명세서와 통합 계획서는
/// 종류가 다르지만 같은 파이프라인을 통과했다. 실제 축은 무엇이 보장되는가다.
/// </summary>
public static class VerificationDocumentFormatter
{
    /// <summary>
    /// 검증 파이프라인을 통과한 문서 - 명세서와 통합 계획서.
    /// </summary>
    public static string FormatVerifiedDocument(
        string body,
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

        // 점수 줄에 설명 주석을 붙이지 않는다. 이전 판은 Critic 프롬프트의 평가 기준을
        // 사람이 옮겨 적었는데, 연결을 강제하는 장치가 없어 드리프트했고 실제로 거짓이
        // 되었다. Critic은 셋(프로시저 명세서/UDF 명세서/통합 계획서)인데 이 포매터는
        // 그 셋을 구분할 수단이 없으므로, 어떤 문구를 쓰더라도 어딘가에서는 틀린다.
        var scoreLines = showScores
            ? $@"
종합 신뢰도: {review!.NormalizedScore}
정합성 점수: {review.ScoreAccuracy}/10
CRUD 점수: {review.ScoreCrud}/10
인터페이스 점수: {review.ScoreInterface}/10
가독성 점수: {review.ScoreReadability}/10
예외처리 점수: {review.ScoreException}/10"
            : string.Empty;

        // 이 주석은 남는다. 필드 자체의 설명이라 프롬프트에서 복제한 것이 아니고
        // 드리프트할 대상이 없다.
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

    /// <summary>
    /// 검증 파이프라인에 진입한 적 없는 문서 - 단일 SP 계획서와 정산 정책 문서.
    ///
    /// ReviewResult를 받지 않는다. 이런 문서에는 실을 수 있는 점수가 없고, 파라미터를
    /// 두지 않으면 어떤 호출부도 점수를 유출시킬 수 없다 - 없는 파라미터는 전달될 수 없다.
    ///
    /// sourceOutcome은 이 문서의 근거가 된 명세서의 종료 상태다. 정산 정책 문서는
    /// SP 정의와 프로파일링 데이터에서 직접 생성되어 인용할 근거가 없으므로 null이며,
    /// 이때는 근거 명세서 줄을 내지 않는다.
    /// </summary>
    public static string FormatUnverifiedDocument(
        string body,
        VerificationOutcome? sourceOutcome,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp)
    {
        var sourceLine = sourceOutcome is { } source
            ? $"근거 명세서 검증 상태: {StatusLabel(source)}\n"
            : string.Empty;

        var yamlFrontMatter = $@"---
검증 상태: 검증 없음 # 이 문서는 L1/L2 검증을 거치지 않음
{sourceLine}---

";

        var statusNote = sourceOutcome is { } noted
            ? $"> **검증 상태**: 이 문서는 검증 파이프라인을 거치지 않았습니다. 근거 명세서(Spec.md)는 '{StatusLabel(noted)}' 상태입니다.\n"
            : "> **검증 상태**: 이 문서는 검증 파이프라인을 거치지 않았습니다. 내용을 직접 검토하십시오.\n";

        return yamlFrontMatter + MetadataHeader(provider, modelName, effort, timestamp, string.Empty, statusNote) + body;
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

    /// <summary>
    /// 종료 상태의 한국어 표기. 지시서 번들(MetadataExporter)도 같은 표기를 써야 하므로
    /// 공개한다 - 같은 switch를 여러 곳에 복제하면 한 곳이 새 상태를 빠뜨렸을 때
    /// 그 문서만 조용히 다른 말을 하게 된다.
    /// </summary>
    public static string StatusLabel(VerificationOutcome outcome) => outcome switch
    {
        VerificationOutcome.Passed => "통과",
        VerificationOutcome.QualityRejected => "품질 미달",
        VerificationOutcome.ReviewNotRun => "리뷰 미수행",
        VerificationOutcome.L1Exhausted => "L1 미통과",
        _ => "알 수 없음"
    };
}
```

- [ ] **Step 5: 프로덕션 호출부 5곳을 바꾼다**

| 파일:줄 | 현재 | 변경 후 |
|---|---|---|
| `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:462` | `FormatSpecification(` | `FormatVerifiedDocument(` |
| `src/ReSet.Cli/Program.cs:730` | `FormatConsolidatedPlan(` | `FormatVerifiedDocument(` |
| `src/ReSet.Cli/Program.cs:1186` | `FormatConsolidatedPlan(` | `FormatVerifiedDocument(` |
| `src/ReSet.Cli/Program.cs:1650` | `FormatSpecification(` | `FormatVerifiedDocument(` |
| `src/ReSet.Cli/Program.cs:1667` | `FormatUnverifiedPlan(` | `FormatUnverifiedDocument(` |

인수는 모두 그대로다. `:1667`이 넘기는 `outcome`은 `VerificationOutcome`이고 파라미터는 `VerificationOutcome?`이므로 암시적 변환이 적용된다.

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1586`의 주석에서 `VerificationDocumentFormatter.FormatConsolidatedPlan` → `VerificationDocumentFormatter.FormatVerifiedDocument`로 바꾼다. 주석이 존재하지 않는 메서드를 가리키면 문서가 거짓이 된다.

- [ ] **Step 6: `SpecHeaderReaderTests`의 주석을 고쳐 쓴다**

`tests/ReSet.Core.Tests/SpecHeaderReaderTests.cs`의 `Read_NormalizesRealArtifactScoreLines` 첫 두 줄 주석을 바꾼다. **테스트 데이터와 단언은 건드리지 않는다.**

```csharp
        // 주석 제거 로직은 계속 필요하다. 현재 산출물은 점수 줄에 설명 주석을 붙이지
        // 않지만, 디스크에 남아 있는 기존 문서와 손으로 편집한 헤더에는 붙어 있고
        // '검증 상태' 줄에는 지금도 붙는다. 이 테스트는 그 입력들을 다룬다.
```

- [ ] **Step 7: 남은 참조가 없는지 확인한다**

```bash
grep -rn "FormatSpecification\|FormatConsolidatedPlan\|FormatUnverifiedPlan\|ScoreLabels\|SpecificationLabels\|PlanLabels" src/ tests/
```
Expected: 출력 없음

- [ ] **Step 8: 테스트와 클린 빌드를 확인한다**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
dotnet test
```
Expected: 고유 경고 8건, 전체 테스트 통과 (391 - 2 + 1 = 390)

- [ ] **Step 9: 커밋**

```bash
git add -A
git commit -m "refactor(core): drop the score-line comments and merge the verified entry points

The comments transcribed the critic prompts' evaluation criteria by hand, with
nothing enforcing the link, so they drifted - the readability description was
outright false, since that criterion scores Mermaid syntax rather than code
style. There are three critics and the formatter cannot tell them apart, so no
wording could be right everywhere. A comment that cannot exist cannot drift.

Removing them left FormatSpecification and FormatConsolidatedPlan identical, so
they collapse into one. The split now runs along what is guaranteed rather than
what kind of document it is - which is why the unverified entry point also takes
the settlement rulebook in the next task.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: 정산 정책 문서 표기

**Files:**
- Modify: `src/ReSet.Cli/Program.cs:553-558` (배치 경로), `:1382-1387` (TUI 경로)
- Test: `tests/ReSet.Core.Tests/VerificationDocumentFormatterTests.cs`

**Interfaces:**
- Consumes: `VerificationDocumentFormatter.FormatUnverifiedDocument(string body, VerificationOutcome? sourceOutcome, string provider, string modelName, string? effort, DateTime timestamp)` (Task 1)
- Produces: 없음

**배경:** `SettlementPolicyService.cs:104`가 AI 결과를 그대로 반환하고 `Program.cs`가 직접 조립한 메타 헤더를 붙여 파일로 쓴다. L1도 L2도 없다. 단일 SP 계획서와 같은 범주이면서 표기만 없다.

**테스트에 관한 정직한 한계:** 이 태스크의 실제 배선은 `Program.cs`의 최상위 문 안에 있어 단위 테스트로 격리할 수 없다. Step 1의 테스트는 Task 1이 구현한 `sourceOutcome == null` 동작을 고정하는 **특성화 테스트**이며 배선 자체를 검증하지 않는다. 배선의 검증은 (a) 이 특성화 테스트, (b) 클린 빌드, (c) 옛 조립 코드가 사라졌는지 확인하는 grep 세 가지다. 배선을 위한 새 자동화 테스트를 만들지 않으며, 만든 척하지 않는다.

- [ ] **Step 1: 실패 테스트를 작성한다**

`tests/ReSet.Core.Tests/VerificationDocumentFormatterTests.cs` 끝에 추가한다.

```csharp
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
```

- [ ] **Step 2: 테스트를 실행한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~FormatUnverifiedDocument_WithNoSource"`
Expected: PASS — Task 1이 이미 `sourceOutcome`을 nullable로 구현했으므로 이 테스트는 그 동작을 고정하는 특성화 테스트다. RED 단계가 없다.

**만약 실패하면** 그것은 Task 1의 구현 결함이다. 기대값을 실제 출력에 맞춰 고치지 말고, 멈춘 뒤 실패 내용과 함께 보고한다.

- [ ] **Step 3: 배치 경로를 교체한다**

`src/ReSet.Cli/Program.cs:553-558`을 다음으로 바꾼다.

```csharp
                        var rulebookName = string.IsNullOrEmpty(cliArgs.JobName) ? "Settlement_Policy_Rulebook.md" : $"{cliArgs.JobName}_Settlement_Policy_Rulebook.md";
                        var rulebookPath = Path.Combine(outputDir, rulebookName);

                        // 이 문서는 SettlementPolicyService가 AI 결과를 그대로 반환한 것이며
                        // L1도 L2도 거치지 않는다. 검증 파이프라인 산출물과 같은 형식의
                        // 헤더를 쓰되, 검증되지 않았다는 사실을 명시한다.
                        await File.WriteAllTextAsync(
                            rulebookPath,
                            VerificationDocumentFormatter.FormatUnverifiedDocument(
                                rulebook, null, provider, modelName, actorEffort, DateTime.Now));
```

`effortSuffix`와 `metadataHeader` 지역 변수는 이 블록에서만 쓰이므로 함께 지운다.

- [ ] **Step 4: TUI 경로를 교체한다**

`src/ReSet.Cli/Program.cs:1382-1387`을 같은 형태로 바꾼다.

```csharp
                            var rulebookName = $"{jobName}_Settlement_Policy_Rulebook.md";
                            var rulebookPath = Path.Combine(outputDir, rulebookName);

                            // 이 문서는 SettlementPolicyService가 AI 결과를 그대로 반환한 것이며
                            // L1도 L2도 거치지 않는다. 검증 파이프라인 산출물과 같은 형식의
                            // 헤더를 쓰되, 검증되지 않았다는 사실을 명시한다.
                            await File.WriteAllTextAsync(
                                rulebookPath,
                                VerificationDocumentFormatter.FormatUnverifiedDocument(
                                    rulebook, null, provider, modelName, actorEffort, DateTime.Now));
```

여기서도 그 블록의 `effortSuffix`와 `metadataHeader`를 지운다.

- [ ] **Step 5: 빌드하고 잔재를 확인한다**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
grep -n "metadataHeader" src/ReSet.Cli/Program.cs
```
Expected: 고유 경고 8건. `metadataHeader` grep은 **출력 없음** — Task 1 이후 `Program.cs`에 남은 조립부는 이 두 곳뿐이었다.

- [ ] **Step 6: 전체 테스트를 실행한다**

Run: `dotnet test`
Expected: 전부 통과 (391)

- [ ] **Step 7: 커밋**

```bash
git add -A
git commit -m "fix(cli): state that the settlement rulebook was never verified

SettlementPolicyService hands the AI's output straight back and Program.cs
wrote it out under a hand-assembled header. No L1, no L2, and nothing saying
so - the same category as the single-SP plan, minus the notice.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: `StatusLabel` 단일화

**Files:**
- Modify: `src/ReSet.Cli/ConsoleUserInteraction.cs:125-131`

**Interfaces:**
- Consumes: `VerificationDocumentFormatter.StatusLabel(VerificationOutcome outcome) → string` (기존, Task 1에서 변경 없음)
- Produces: 없음

**테스트에 관한 정직한 한계:** `ConsoleUserInteraction`은 `AnsiConsole`에 직접 쓰는 대화형 클래스라 단위 테스트 기반이 없고, 이 태스크를 위해 만들지 않는다. 검증은 (a) `StatusLabel`이 네 상태를 모두 다룬다는 기존 포매터 테스트, (b) 컴파일, (c) 치환 전후 문자열이 동일함을 눈으로 대조하는 것이다. 새 테스트를 만들지 않으며, 만든 척하지 않는다.

- [ ] **Step 1: 치환 전 문자열을 확인한다**

```bash
sed -n '119,135p' src/ReSet.Cli/ConsoleUserInteraction.cs
```

현재 switch가 내는 값이 `StatusLabel`과 일치하는지 대조한다: `L1Exhausted → "L1 미통과"`, `QualityRejected → "품질 미달"`, `ReviewNotRun → "리뷰 미수행"`, `_ → "알 수 없음"`. 네 값이 모두 같아야 하며, 다르면 화면 표기가 바뀌는 것이므로 멈추고 보고한다.

- [ ] **Step 2: switch를 호출로 바꾼다**

`src/ReSet.Cli/ConsoleUserInteraction.cs:125-131`의 지역 switch를 지우고 다음으로 바꾼다.

```csharp
                // 표기는 VerificationDocumentFormatter가 단독으로 소유한다. 같은 switch를
                // 복제하면 VerificationOutcome에 상태가 추가됐을 때 한 곳이 빠뜨릴 수 있고,
                // 그러면 승인 화면만 다른 말을 하게 된다.
                var statusLabel = VerificationDocumentFormatter.StatusLabel(outcome);
```

기존 switch가 `Passed`를 다루지 않는 것은 이 코드가 `if (!isVerified)` 안에 있어 도달하지 않기 때문이다. `StatusLabel`은 `Passed`를 다루므로 동작 차이가 없다.

`using ReSet.Core.Services;`는 파일 상단(`:5`)에 이미 있으므로 추가할 것이 없다.

- [ ] **Step 3: 빌드하고 테스트한다**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
dotnet test
```
Expected: 고유 경고 8건, 전체 테스트 통과 (391)

- [ ] **Step 4: 커밋**

```bash
git add -A
git commit -m "refactor(cli): read the status label from its single owner

The approval screen carried its own copy of the outcome switch. A copy is how
one site quietly misses a new state and starts telling a different story than
the document it is showing.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: 취소 전파

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:642`, `:1447`, `:1803`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: 없음 (내부 동작 수정)

**배경:** 세 지점 모두 `cancellationToken`을 받는 AI 호출을 `catch { }`로 감싸 예외를 통째로 버린다. 증상은 `:1803`에서 가장 뚜렷하다 — 통합 계획서 L3 승인 화면에서 피드백 재생성 중 Ctrl-C를 누르면 취소가 삼켜지고 `rePlan`이 수정 전 값을 유지한 채 승인 화면으로 되돌아간다. **사용자의 취소가 무시되고 같은 질문을 다시 받는다.**

상위 처리는 이미 있다. `Program.cs:968`(명세서 파이프라인)과 `:1262`(통합 파이프라인)가 `catch (OperationCanceledException)`으로 메시지를 찍고 메뉴로 돌아간다.

**도달 조건은 세 지점 모두 확인해 두었다.**

| 지점 | 도달 조건 |
|---|---|
| `:642` | `actorEffort: "dynamic"` + 합성본이 L1 실패 (`:608`의 `!finalL1.IsValid`) |
| `:1447` | L3 `ProvideFeedback` + 재생성본이 L1 실패 + **비로컬 프로바이더** (`AiClientFactory.IsLocalProvider`가 `ollama`/`local-openai`/`mlx`/`vllm`만 true이므로 `"OpenAI"`를 쓰면 단순 분기로 간다) |
| `:1803` | 통합 L3 `ProvideFeedback` + 재생성본이 L1 실패 |

- [ ] **Step 1: 세 실패 테스트를 작성한다**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` 끝에 추가한다.

```csharp
        // 아래 세 테스트는 취소가 삼켜지지 않는지 확인한다. 세 지점 모두 cancellationToken을
        // 받는 AI 호출을 catch { }로 감싸고 있어, 사용자가 Ctrl-C를 눌러도 작업이 계속되고
        // 승인 화면까지 도달했다. 상위 호출부(Program.cs:968, :1262)는 이미
        // OperationCanceledException을 받아 메뉴로 돌아가므로 전파만 하면 된다.

        [Fact]
        public async Task RunCodeObjectPipelineAsync_Sectional_CancelDuringSelfFix_Propagates()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_CancelSelfFix", CodeObjectType.Procedure);

            aiService.ProviderName.Returns("Ollama");
            criticService.ProviderName.Returns("Ollama");

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_CancelSelfFix", DdlText = "SELECT 1;"
                }));

            // 필수 H2 헤더가 없어 L1을 통과하지 못한다 - 이것이 자가 수정 경로의 진입 조건이다.
            var l1Invalid = "# 헤더가 없는 본문";

            // 호출 순서: 후보 3개, 합성본, 자가 수정. 다섯 번째에서 취소한다.
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = l1Invalid }),
                    Task.FromResult(new AiResult { Content = l1Invalid }),
                    Task.FromResult(new AiResult { Content = l1Invalid }),
                    Task.FromResult(new AiResult { Content = l1Invalid }),
                    Task.FromException<AiResult>(new OperationCanceledException()));

            var candidateReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 7, ScoreCrud = 7, ScoreInterface = 7, ScoreException = 7, ScoreReadability = 7
            };
            criticService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(candidateReview));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "ollama-test", criticService: criticService, actorEffort: "dynamic");

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                orchestrator.RunCodeObjectPipelineAsync(
                    "Server=(local);Database=PaymentDB", key, 1, "Ollama", "rules", true,
                    Path.Combine(Path.GetTempPath(), $"ReSet-CancelSelfFix-{Guid.NewGuid():N}"), false,
                    cancellationToken: CancellationToken.None, directDependenciesOnly: true));
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_CancelDuringL3FeedbackSelfFix_Propagates()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_CancelL3", CodeObjectType.Procedure);

            aiService.ProviderName.Returns("OpenAI");

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_CancelL3", DdlText = "SELECT 1;"
                }));

            var l1Valid =
                "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            var l1Invalid = "# 헤더가 없는 본문";

            // 호출 순서: 1차 생성(L1 통과), L3 피드백 재생성(L1 실패), L3 자가 수정(취소).
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = l1Valid }),
                    Task.FromResult(new AiResult { Content = l1Invalid }),
                    Task.FromException<AiResult>(new OperationCanceledException()));

            var cleanReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
            };
            aiService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(cleanReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult
                {
                    Decision = UserDecision.ProvideFeedback, UserFeedback = "보완해 주세요"
                }));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction, "1", "gpt-4");

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                orchestrator.RunCodeObjectPipelineAsync(
                    "Server=(local);Database=PaymentDB", key, 1, "OpenAI", "rules", true,
                    Path.Combine(Path.GetTempPath(), $"ReSet-CancelL3-{Guid.NewGuid():N}"), false,
                    cancellationToken: CancellationToken.None, directDependenciesOnly: true));
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_CancelDuringL3FeedbackL1Refix_Propagates()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var validPlan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            var l1InvalidPlan = "# 헤더가 없는 계획서";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });

            // 호출 순서: 1차 생성(L1 통과), L3 피드백 재생성(L1 실패), L1 재보완(취소).
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = validPlan }),
                    Task.FromResult(new AiResult { Content = l1InvalidPlan }),
                    Task.FromException<AiResult>(new OperationCanceledException()));

            var goodReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
            };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult
                {
                    Decision = UserDecision.ProvideFeedback, UserFeedback = "보완해 주세요"
                }));

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                orchestrator.RunConsolidatedPipelineAsync(
                    specs, "C#", "TestJobCancelRefix", "OpenAI", _consolidatedOutputRoot, isBatchMode: false));
        }
```

- [ ] **Step 2: 세 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~Propagates"`
Expected: 3건 모두 FAIL — `Assert.ThrowsAsync`가 예외를 받지 못한다(취소가 `catch { }`에 삼켜지므로).

**세 테스트 중 하나라도 다른 이유로 실패하면** 그 지점의 도달 조건이 예상과 다른 것이다. 먼저 실제 호출 순서를 확인한다 — `aiService.ReceivedCalls().Count()`를 임시로 출력하거나, 해당 영역을 직접 읽는다. NSubstitute는 시퀀스를 소진하면 마지막 값을 반복 반환하므로 호출이 예상보다 많으면 취소가 엉뚱한 시점에 발생한다. 시퀀스 길이 조정은 허용하지만 **`Assert.ThrowsAsync<OperationCanceledException>` 단언은 바꾸지 않는다.**

도달 조건을 만들 수 없는 지점이 있으면 테스트 없이 넘어가지 말고 그 사실을 보고한다.

- [ ] **Step 3: 세 `catch`를 고친다**

같은 파일 `:671`, `:722`가 이미 쓰는 형태로 맞춘다.

`:642` (합성본 자가 수정):

```csharp
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.Warning(ex, "합성본 자가 수정 실패 (이전 버전 유지)");
                        }
```

`:1447` (명세서 L3 피드백 재생성):

```csharp
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                Log.Warning(ex, "명세서 L3 피드백 반영 재생성 실패");
                            }
```

`:1803` (통합 계획서 L1 재보완):

```csharp
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.Warning(ex, "통합 계획서 L1 재보완 실패 (직전 버전 유지)");
                        }
```

취소가 아닌 예외에 대한 기존 동작 — 이전 값을 유지하고 계속 진행 — 은 그대로다. 로그가 추가될 뿐이다.

- [ ] **Step 4: 테스트를 실행해 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"`
Expected: 전부 통과. 기존 테스트가 하나라도 깨지면 취소 외 예외의 흐름을 바꾼 것이므로 되돌아가 확인한다

- [ ] **Step 5: 클린 빌드와 전체 테스트**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
dotnet test
```
Expected: 고유 경고 8건, 전체 통과 (394)

- [ ] **Step 6: 커밋**

```bash
git add -A
git commit -m "fix(verification): stop swallowing cancellation in three self-fix paths

Each bare catch wrapped an AI call that takes the cancellation token. The
clearest symptom is the consolidated L3 path: Ctrl-C during the feedback
re-fix was swallowed, rePlan kept its pre-fix value, and the user was handed
the same approval prompt again - their cancellation silently ignored.

Callers already handle OperationCanceledException (Program.cs:968, :1262), so
propagating needs no new plumbing. Behaviour for every other exception is
unchanged: keep the previous value and carry on, now with a log line.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: 문서 갱신

**Files:**
- Modify: `AGENTS.md`
- Modify: `docs/architecture.md`

**Interfaces:**
- Consumes: Task 1~4의 최종 상태
- Produces: 없음

- [ ] **Step 1: 실제 테스트 수를 확인한다**

```bash
dotnet test 2>&1 | tail -3
```

출력에 나온 실제 통과 수를 기록한다. **예상치를 적지 말고 실제 실행 결과를 쓴다.** 직전 사이클에서 이 숫자를 추정으로 적어 문서가 6만큼 틀린 적이 있고, 그 직후 수정 웨이브가 테스트를 하나 더 추가하면서 또 1만큼 어긋났다.

- [ ] **Step 2: 낡은 서술을 찾는다**

```bash
grep -rn "FormatSpecification\|FormatConsolidatedPlan\|FormatUnverifiedPlan" AGENTS.md docs/architecture.md
grep -n "개의 단위 테스트" AGENTS.md
```

- [ ] **Step 3: 찾은 것만 고친다**

테스트 수를 Step 1의 실제값으로 바꾼다. 포매터 메서드명을 언급하는 곳이 있으면 `FormatVerifiedDocument` / `FormatUnverifiedDocument`로 바꾼다.

**범위 규율:** 지금 거짓이 된 서술만 고친다. 새 절을 만들거나, 원래 문서화되지 않았던 것(진입점 통합의 근거, `sourceOutcome`의 의미 등)을 새로 서술하지 않는다. grep이 아무것도 반환하지 않으면 그 파일은 손대지 않고 보고서에 그렇게 적는다.

`docs/superpowers/plans/` 및 `docs/superpowers/specs/` 아래의 과거 문서는 작성 시점의 기록이므로 고치지 않는다.

- [ ] **Step 4: 최종 확인**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
dotnet test
grep -rn "FormatSpecification\|FormatConsolidatedPlan\|FormatUnverifiedPlan" src/ tests/ AGENTS.md docs/architecture.md
```
Expected: 고유 경고 8건, 전체 통과, grep 출력 없음

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "docs: update the test count and formatter names after the cleanup

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## 실행 순서와 의존 관계

```
Task 1 (포매터 통합) ──→ Task 2 (정산 문서) ──┐
Task 3 (StatusLabel) ────────────────────────┼─→ Task 5 (문서)
Task 4 (취소 전파) ──────────────────────────┘
```

Task 2만 Task 1을 기다린다 — `FormatUnverifiedDocument`가 있어야 배선할 수 있다. Task 3은 Task 1이 건드리지 않는 `StatusLabel`만 쓰므로 독립이고, Task 4는 포매터와 무관하다.

## 자체 검토 결과

**스펙 커버리지**

| 스펙 요구사항 | 담당 |
|---|---|
| A 취소 전파 3곳 (`:642`, `:1447`, `:1803`) | Task 4 |
| A 정정: `Program.cs:1683`은 대상 아님 | Task 4 범위에서 제외 (계획에 명시) |
| B 점수 설명 주석 제거 | Task 1 Step 4 |
| C `:30` 모순 주석 삭제 | Task 1 Step 4 (파일 전면 교체로 소멸) |
| D `StatusLabel` 단일화 | Task 3 |
| E 정산 정책 문서 표기 | Task 2 |
| 진입점 3 → 2 통합 | Task 1 |
| `sourceOutcome` nullable | Task 1 Step 4, Task 2 Step 1 |
| 문구 중립화("이 문서는") + 단언 1줄 변경 | Task 1 Step 2 |
| 폐기 테스트 2개와 대체 테스트 | Task 1 Step 1 |
| `SpecHeaderReaderTests` 주석 재작성 | Task 1 Step 6 |
| 호출부 7곳 | Task 1 Step 5 (5곳), Task 2 Step 3-4 (2곳) |
| `VerificationPipelineOrchestrator.cs:1586` 주석 | Task 1 Step 5 |

누락 없음.

**타입 일관성**

`FormatVerifiedDocument`, `FormatUnverifiedDocument`, `StatusLabel` — Task 1에서 정의한 이름이 Task 2·3에서 그대로 쓰인다. `VerificationOutcome?`(nullable) 파라미터는 Task 1 Step 4의 시그니처와 Task 2 Step 1의 `null` 인수가 일치한다.

**계획 수립 중 확인한 사실**

1. 세 `catch` 지점의 도달 조건을 모두 확인했다. 특히 `:1447`은 `AiClientFactory.IsLocalProvider`가 `ollama`/`local-openai`/`mlx`/`vllm`만 참으로 판정하므로, 프로바이더를 `"OpenAI"`로 주면 3분할 재생성이 아닌 단순 분기로 들어간다.
2. 기존 포매터 테스트 5개는 `Assert.Contains("종합 신뢰도: 80", …)`와 `> **AI 최종 신뢰도**: …` 블록쿼트만 단언하고 YAML 점수 주석은 단언하지 않는다. 주석 제거의 영향을 받지 않는다.
3. 경고 개수를 `grep -c`로 세면 16이 나온다. `dotnet build`가 각 경고를 두 번 출력하기 때문이며 고유 개수는 8이다. Global Constraints에 명시했다.
