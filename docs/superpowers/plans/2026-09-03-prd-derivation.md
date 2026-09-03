# 명세서 기반 요구사항 도출(PRD Derivation) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 이미 생성된 `Spec.md` 하나만을 근거로 사람이 읽는 요구사항 문서 `Prd.md`를 도출하고, 모든 요구 항목이 원본 명세서의 실재하는 인용을 들고 있는지 기계로 대조한다.

**Architecture:** 순수 함수 검증기(`PrdAttributionValidator`)를 먼저 TDD로 세우고, 그 위에 AI 생성 한 번과 교정 재호출 한 번을 감싸는 얇은 오케스트레이터(`PrdDerivationService`)를 얹는다. DB에 접속하지 않고 `output/` 트리의 파일만 읽으므로 이미 쌓인 산출물에 그대로 소급 적용된다. 검증 파이프라인(L2 Actor-Critic·L3 승인)에는 진입하지 않는다.

**Tech Stack:** .NET (C#), xUnit 2.9.3, Spectre.Console(TUI), Serilog

**Spec:** `docs/superpowers/specs/2026-09-03-prd-derivation-design.md`

## Global Constraints

- 산출물 경로: `output/Procedures/<스키마.이름>/docs/Prd.md`. 파일명 상수는 `OutputPathResolver.PrdFileName` 한 곳에만 둔다.
- **`CacheManager.CurrentCacheFormatVersion`을 올리지 않는다.** 올리면 PRD와 무관한 SP 전건이 재생성 대상이 된다.
- **`MechanicalValidator.cs`를 수정하지 않는다.** PRD 검사는 신규 클래스에 둔다(설계 §4.2).
- 새 코드는 `Procedures/`만 대상으로 한다. `Functions/`·`External/`은 범위 밖(설계 §7.1).
- `Prd.md`는 2·3번 메뉴(배치 계획서·코딩 지시서)의 입력으로 배선하지 않는다.
- Core는 UI에 의존하지 않는다 — `PrdDerivationService`는 Spectre.Console을 참조하지 않는다.
- 취소 가능한 `await`를 감싸는 광범위 `catch`에는 `when (ex is not OperationCanceledException)`을 붙인다.
- 완료 게이트(AGENTS.md): `dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"`가 **0**, `dotnet test`가 **실패 0 · 건너뜀 0**. 통과 수 절대값은 게이트로 쓰지 않는다.

## 문서 계약 요약 (모든 태스크가 참조)

`Prd.md`의 다섯 섹션과 각 섹션의 ID 접두사·허용 근거 원천:

| Prd.md 섹션 | ID 접두사 | 허용 근거 원천(Spec.md 헤딩) |
| :--- | :--- | :--- |
| `## 배경 및 목적` | `REQ-BG` | `## 개요` |
| `## 수행 조건 및 입력 계약` | `REQ-IN` | `## 파라미터 목록` |
| `## 데이터 요구사항` | `REQ-DATA` | `## CRUD 분석` |
| `## 기능 요구사항` | `REQ-FUNC` | `## 로직 흐름 요약` |
| `## 예외 및 비기능 요구사항` | `REQ-NFR` | `## CRUD 분석`, `## 로직 흐름 요약` |

각 섹션 본문은 네 칸짜리 표다:

```markdown
| ID | 요구사항 | 근거 | 확신도 |
| :--- | :--- | :--- | :--- |
| REQ-DATA-03 | 정산 마감 시 미집계 건은 일별 집계 테이블에 신규 적재되어야 한다 | ## CRUD 분석 > "TB_SETTLE_DAILY에 INSERT" | 도출 |
```

근거 칸 형식: `<Spec 헤딩> > "<원문 구절>"`. 확신도: `도출` 또는 `추정`.

---

### Task 1: PRD 문서 파서와 구조 검사

요구 표를 읽어 항목으로 만들고, Spec.md를 보지 않고도 판정할 수 있는 세 가지(섹션 존재·확신도 어휘·빈 근거)를 검사한다.

**Files:**
- Create: `src/ReSet.Core/Services/PrdSectionContract.cs`
- Create: `src/ReSet.Core/Services/PrdDocumentParser.cs`
- Create: `src/ReSet.Core/Services/PrdAttributionValidator.cs`
- Test: `tests/ReSet.Core.Tests/PrdDocumentParserTests.cs`
- Test: `tests/ReSet.Core.Tests/PrdAttributionValidatorTests.cs`

**Interfaces:**
- Consumes: `MarkdownSectionLocator.SplitLines`, `MarkdownSectionLocator.LocateSection` (기존, `src/ReSet.Core/Services/MarkdownSectionLocator.cs`)
- Produces:
  - `PrdSectionContract.Sections` → `IReadOnlyList<PrdSectionRule>`, `PrdSectionRule(string Heading, string IdPrefix, IReadOnlyList<string> AllowedSources)`
  - `PrdDocumentParser.Parse(string prdMarkdown)` → `IReadOnlyList<PrdRequirement>`
  - `PrdRequirement(string Section, string Id, string Text, string EvidenceRaw, string Confidence, int LineNumber)`
  - `PrdAttributionValidator.Validate(string prdMarkdown, string specMarkdown)` → `PrdValidationResult`
  - `PrdValidationResult { bool IsValid; IReadOnlyList<PrdDefect> Defects }`
  - `PrdDefect(PrdDefectType Type, string Section, string RequirementId, string Message)`
  - `PrdDefectType { SectionMissing, ConfidenceVocabulary, EvidenceMissing, IdPrefixMismatch, EvidenceSourceNotAllowed, EvidenceHeadingNotFound, EvidenceQuoteNotFound }`

- [ ] **Step 1: 계약 클래스를 쓴다**

`src/ReSet.Core/Services/PrdSectionContract.cs`:

```csharp
using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>Prd.md 한 섹션의 계약 - 헤딩, 요구 ID 접두사, 근거로 인용해도 되는 Spec 헤딩.</summary>
    public sealed record PrdSectionRule(
        string Heading,
        string IdPrefix,
        IReadOnlyList<string> AllowedSources);

    /// <summary>
    /// Prd.md 섹션이 Spec.md 섹션에서 파생되는 고정 관계.
    ///
    /// 이 표가 문서에만 있고 검사에 없으면 「파생 고정형」이라는 말이 지켜지지 않는다.
    /// 생성 프롬프트와 귀속 검사가 같은 표를 읽어야 둘이 갈라지지 않는다.
    /// </summary>
    public static class PrdSectionContract
    {
        public static readonly IReadOnlyList<PrdSectionRule> Sections = new[]
        {
            new PrdSectionRule("## 배경 및 목적", "REQ-BG", new[] { "## 개요" }),
            new PrdSectionRule("## 수행 조건 및 입력 계약", "REQ-IN", new[] { "## 파라미터 목록" }),
            new PrdSectionRule("## 데이터 요구사항", "REQ-DATA", new[] { "## CRUD 분석" }),
            new PrdSectionRule("## 기능 요구사항", "REQ-FUNC", new[] { "## 로직 흐름 요약" }),
            new PrdSectionRule(
                "## 예외 및 비기능 요구사항",
                "REQ-NFR",
                new[] { "## CRUD 분석", "## 로직 흐름 요약" }),
        };
    }
}
```

- [ ] **Step 2: 파서 실패 테스트를 쓴다**

`tests/ReSet.Core.Tests/PrdDocumentParserTests.cs`:

```csharp
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdDocumentParserTests
    {
        private const string TwoSectionPrd = @"## 배경 및 목적

| ID | 요구사항 | 근거 | 확신도 |
| :--- | :--- | :--- | :--- |
| REQ-BG-01 | 일별 정산 마감을 자동화한다 | ## 개요 > ""일별 정산 마감"" | 도출 |

## 데이터 요구사항

| ID | 요구사항 | 근거 | 확신도 |
| :--- | :--- | :--- | :--- |
| REQ-DATA-01 | 미집계 건을 적재한다 | ## CRUD 분석 > ""TB_SETTLE_DAILY에 INSERT"" | 도출 |
| REQ-DATA-02 | 중복 적재를 막는다 | ## CRUD 분석 > ""중복 검사"" | 추정 |
";

        [Fact]
        public void Parse_ShouldReadEveryRequirementRowWithItsSection()
        {
            var requirements = PrdDocumentParser.Parse(TwoSectionPrd);

            Assert.Equal(3, requirements.Count);
            Assert.Equal("## 배경 및 목적", requirements[0].Section);
            Assert.Equal("REQ-BG-01", requirements[0].Id);
            Assert.Equal("## 개요 > \"일별 정산 마감\"", requirements[0].EvidenceRaw);
            Assert.Equal("도출", requirements[0].Confidence);
            Assert.Equal("## 데이터 요구사항", requirements[2].Section);
            Assert.Equal("추정", requirements[2].Confidence);
        }

        [Fact]
        public void Parse_ShouldSkipHeaderAndSeparatorRows()
        {
            var requirements = PrdDocumentParser.Parse(TwoSectionPrd);

            Assert.DoesNotContain(requirements, r => r.Id == "ID");
            Assert.DoesNotContain(requirements, r => r.Id.StartsWith(":---"));
        }

        [Fact]
        public void Parse_ShouldIgnoreTableRowsInsideCodeFence()
        {
            // 생성 모델이 예시를 코드 펜스로 감싸는 일이 잦다. 그것을 요구로 세면
            // 검사가 존재하지 않는 항목을 고발한다.
            const string withFence = @"## 데이터 요구사항

            ```markdown
            | REQ-DATA-99 | 예시일 뿐이다 | ## CRUD 분석 > ""예시"" | 도출 |
            ```

| ID | 요구사항 | 근거 | 확신도 |
| :--- | :--- | :--- | :--- |
| REQ-DATA-01 | 진짜 요구 | ## CRUD 분석 > ""INSERT"" | 도출 |
";

            var requirements = PrdDocumentParser.Parse(withFence);

            Assert.Single(requirements);
            Assert.Equal("REQ-DATA-01", requirements[0].Id);
        }
    }
}
```

- [ ] **Step 3: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdDocumentParserTests`
Expected: 컴파일 실패 — `PrdDocumentParser` 없음

- [ ] **Step 4: 파서를 구현한다**

`src/ReSet.Core/Services/PrdDocumentParser.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>Prd.md 한 요구 표의 행 하나. LineNumber는 1부터 센다(결함 보고용).</summary>
    public sealed record PrdRequirement(
        string Section,
        string Id,
        string Text,
        string EvidenceRaw,
        string Confidence,
        int LineNumber);

    /// <summary>
    /// Prd.md의 요구 표를 읽는다. 섹션 경계와 코드 펜스 판정은
    /// <see cref="MarkdownSectionLocator"/>에 맡긴다 - 펜스 미닫힘 폴백을 두 곳이
    /// 각자 갖는 사고를 반복하지 않기 위해서다.
    /// </summary>
    public static class PrdDocumentParser
    {
        public static IReadOnlyList<PrdRequirement> Parse(string? prdMarkdown)
        {
            var lines = MarkdownSectionLocator.SplitLines(prdMarkdown);
            var fenceFlags = MarkdownSectionLocator.ComputeFenceFlags(lines);
            var requirements = new List<PrdRequirement>();

            foreach (var rule in PrdSectionContract.Sections)
            {
                var (headerIndex, endIndex) = MarkdownSectionLocator.LocateSection(lines, rule.Heading, "## ");
                if (headerIndex < 0)
                {
                    continue;
                }

                for (var i = headerIndex + 1; i < endIndex; i++)
                {
                    if (fenceFlags[i])
                    {
                        continue;
                    }

                    var cells = SplitRow(lines[i]);
                    if (cells is null || cells.Count < 4)
                    {
                        continue;
                    }

                    if (IsHeaderOrSeparator(cells))
                    {
                        continue;
                    }

                    requirements.Add(new PrdRequirement(
                        rule.Heading, cells[0], cells[1], cells[2], cells[3], i + 1));
                }
            }

            return requirements;
        }

        /// <summary>`| a | b |` 형태의 줄만 칸으로 가른다. 표가 아니면 null.</summary>
        private static List<string>? SplitRow(string line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                return null;
            }

            var body = trimmed.Trim('|');
            return body.Split('|').Select(c => c.Trim()).ToList();
        }

        private static bool IsHeaderOrSeparator(List<string> cells)
        {
            if (cells[0].Equals("ID", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return cells.All(c => c.Length > 0 && c.All(ch => ch == ':' || ch == '-'));
        }
    }
}
```

- [ ] **Step 5: 파서 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdDocumentParserTests`
Expected: PASS

- [ ] **Step 6: 구조 검사 실패 테스트를 쓴다**

`tests/ReSet.Core.Tests/PrdAttributionValidatorTests.cs`:

```csharp
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdAttributionValidatorTests
    {
        private const string Spec = @"## 개요

일별 정산 마감을 수행한다.

## 파라미터 목록

| 이름 | 타입 |
| :--- | :--- |
| @BaseDate | char(8) |

## CRUD 분석

TB_SETTLE_DAILY에 INSERT 한다. 중복 검사를 먼저 수행한다.

## 로직 흐름 요약

1. 기준일자를 검증한다.
2. 미집계 건을 조회한다.
";

        private static string PrdWith(params string[] rows) =>
            "## 배경 및 목적\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-BG-01 | 일별 정산을 마감한다 | ## 개요 > \"일별 정산 마감\" | 도출 |\n\n"
            + "## 수행 조건 및 입력 계약\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-IN-01 | 기준일자를 받는다 | ## 파라미터 목록 > \"@BaseDate\" | 도출 |\n\n"
            + "## 데이터 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + string.Join("\n", rows) + "\n\n"
            + "## 기능 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-FUNC-01 | 기준일자를 검증한다 | ## 로직 흐름 요약 > \"기준일자를 검증한다\" | 도출 |\n\n"
            + "## 예외 및 비기능 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-NFR-01 | 중복 적재를 막는다 | ## CRUD 분석 > \"중복 검사\" | 추정 |\n";

        private const string GoodDataRow =
            "| REQ-DATA-01 | 미집계 건을 적재한다 | ## CRUD 분석 > \"TB_SETTLE_DAILY에 INSERT\" | 도출 |";

        [Fact]
        public void Validate_ShouldPass_WhenEverySectionAndEvidenceIsSound()
        {
            var result = PrdAttributionValidator.Validate(PrdWith(GoodDataRow), Spec);

            Assert.True(result.IsValid, string.Join("; ", result.Defects.Select(d => d.Message)));
        }

        [Fact]
        public void Validate_ShouldReportSectionMissing_WhenASectionIsAbsent()
        {
            var prd = PrdWith(GoodDataRow).Replace("## 기능 요구사항", "## 기능 요구 사항");

            var result = PrdAttributionValidator.Validate(prd, Spec);

            Assert.Contains(result.Defects, d => d.Type == PrdDefectType.SectionMissing);
        }

        [Fact]
        public void Validate_ShouldReportConfidenceVocabulary_WhenValueIsNotDerivedOrInferred()
        {
            var row = "| REQ-DATA-01 | 미집계 건을 적재한다 | ## CRUD 분석 > \"TB_SETTLE_DAILY에 INSERT\" | 높음 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.ConfidenceVocabulary && d.RequirementId == "REQ-DATA-01");
        }

        [Fact]
        public void Validate_ShouldReportEvidenceMissing_WhenEvidenceCellIsEmpty()
        {
            var row = "| REQ-DATA-01 | 미집계 건을 적재한다 |  | 추정 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.EvidenceMissing && d.RequirementId == "REQ-DATA-01");
        }

        [Fact]
        public void Validate_ShouldReportEvidenceMissing_ForInferredRowsToo()
        {
            // 「추정」이라고 해서 근거가 면제되지 않는다 - 재구성의 출발점을 밝혀야 한다.
            var row = "| REQ-DATA-01 | 정산 정책이 바뀌면 재집계한다 |  | 추정 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(result.Defects, d => d.Type == PrdDefectType.EvidenceMissing);
        }
    }
}
```

- [ ] **Step 7: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdAttributionValidatorTests`
Expected: 컴파일 실패 — `PrdAttributionValidator` 없음

- [ ] **Step 8: 검사기의 구조 검사 부분을 구현한다**

`src/ReSet.Core/Services/PrdAttributionValidator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    public enum PrdDefectType
    {
        SectionMissing,
        ConfidenceVocabulary,
        EvidenceMissing,
        IdPrefixMismatch,
        EvidenceSourceNotAllowed,
        EvidenceHeadingNotFound,
        EvidenceQuoteNotFound,
    }

    public sealed record PrdDefect(
        PrdDefectType Type,
        string Section,
        string RequirementId,
        string Message);

    public sealed class PrdValidationResult
    {
        public PrdValidationResult(IReadOnlyList<PrdDefect> defects) => Defects = defects;

        public IReadOnlyList<PrdDefect> Defects { get; }

        public bool IsValid => Defects.Count == 0;
    }

    /// <summary>
    /// Prd.md의 요구 항목이 원본 Spec.md의 실재하는 자리를 인용하는지 대조한다.
    ///
    /// [무엇을 재고 무엇을 못 재는가]
    /// 이 검사가 참거짓을 세우는 것은 「인용이 실재하는가」까지다. 인용은 진짜인데
    /// 요구 서술이 그 인용과 무관한 경우(귀속 오배치)는 이 오라클로 잴 수 없다 -
    /// 모델이 Spec에서 아무 구절이나 복사해 붙이면 전부 통과한다. PRD에는 L2가
    /// 없으므로 그 구멍은 사람 검토에 남으며, 문서 배너가 그 사실을 명시한다.
    /// 검사가 실제보다 강한 척하는 쪽이 검사가 약한 것보다 위험하다.
    ///
    /// [MechanicalValidator에 넣지 않은 이유]
    /// 그쪽 재료는 SpecExpectations(원본 DDL·정적 분석 유래)인데 여기 오라클은
    /// Spec.md 텍스트 하나뿐이다. 재료가 겹치지 않는 검사를 같은 클래스에 넣으면
    /// IsConsolidated bool 분기가 3분기로 번진다.
    /// </summary>
    public static class PrdAttributionValidator
    {
        private static readonly string[] AllowedConfidence = { "도출", "추정" };

        public static PrdValidationResult Validate(string? prdMarkdown, string? specMarkdown)
        {
            var defects = new List<PrdDefect>();
            var prdLines = MarkdownSectionLocator.SplitLines(prdMarkdown);

            foreach (var rule in PrdSectionContract.Sections)
            {
                var (headerIndex, _) = MarkdownSectionLocator.LocateSection(prdLines, rule.Heading, "## ");
                if (headerIndex < 0)
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.SectionMissing,
                        rule.Heading,
                        string.Empty,
                        $"필수 섹션 '{rule.Heading}'이 없습니다."));
                }
            }

            foreach (var requirement in PrdDocumentParser.Parse(prdMarkdown))
            {
                if (!AllowedConfidence.Contains(requirement.Confidence))
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.ConfidenceVocabulary,
                        requirement.Section,
                        requirement.Id,
                        $"확신도는 '도출' 또는 '추정'이어야 합니다. 실제 값: '{requirement.Confidence}'"));
                }

                if (string.IsNullOrWhiteSpace(requirement.EvidenceRaw))
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.EvidenceMissing,
                        requirement.Section,
                        requirement.Id,
                        "근거 칸이 비어 있습니다. '추정' 항목도 재구성의 출발점이 된 인용을 달아야 합니다."));
                }
            }

            return new PrdValidationResult(defects);
        }
    }
}
```

- [ ] **Step 9: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~PrdDocumentParserTests|FullyQualifiedName~PrdAttributionValidatorTests"`
Expected: PASS (검사 2·3·4는 아직 없으므로 `Validate_ShouldPass_...`도 통과한다)

- [ ] **Step 10: 커밋**

```bash
git add src/ReSet.Core/Services/PrdSectionContract.cs \
        src/ReSet.Core/Services/PrdDocumentParser.cs \
        src/ReSet.Core/Services/PrdAttributionValidator.cs \
        tests/ReSet.Core.Tests/PrdDocumentParserTests.cs \
        tests/ReSet.Core.Tests/PrdAttributionValidatorTests.cs
git commit -m "feat: PRD 요구 표를 읽고 구조 결함을 잡는 검사기를 세운다"
```

---

### Task 2: 파생 대응 검사

§5.1의 파생 관계가 문서에만 있고 검사에 없으면 「파생 고정형」이 지켜지지 않는다. 두 겹으로 잰다 — ID 접두사가 자기 섹션과 맞는가, 근거 헤딩이 그 섹션에 허용된 원천인가.

**Files:**
- Modify: `src/ReSet.Core/Services/PrdAttributionValidator.cs` (Task 1이 만든 `Validate`의 요구 순회 루프)
- Test: `tests/ReSet.Core.Tests/PrdAttributionValidatorTests.cs` (Task 1이 만든 파일에 추가)

**Interfaces:**
- Consumes: Task 1의 `PrdRequirement`, `PrdDefectType`, `PrdSectionContract.Sections`
- Produces: `PrdDefectType.IdPrefixMismatch`, `PrdDefectType.EvidenceSourceNotAllowed` 발화. 근거 칸을 헤딩/인용으로 가르는 `PrdEvidenceReference(string Heading, string Quote)`와 `PrdAttributionValidator.TryParseEvidence(string raw, out PrdEvidenceReference reference)` — Task 3이 이 파서를 쓴다.

- [ ] **Step 1: 실패 테스트를 쓴다**

`tests/ReSet.Core.Tests/PrdAttributionValidatorTests.cs`의 클래스 안에 추가:

```csharp
        [Fact]
        public void Validate_ShouldReportIdPrefixMismatch_WhenRowSitsInTheWrongSection()
        {
            var row = "| REQ-FUNC-01 | 미집계 건을 적재한다 | ## CRUD 분석 > \"TB_SETTLE_DAILY에 INSERT\" | 도출 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.IdPrefixMismatch && d.RequirementId == "REQ-FUNC-01");
        }

        [Fact]
        public void Validate_ShouldReportEvidenceSourceNotAllowed_WhenCitingAnUnmappedSection()
        {
            // 기능 요구사항은 로직 흐름 요약에서만 파생한다. 파라미터 목록 인용은
            // 실재하더라도 파생 계약 위반이다.
            var prd = PrdWith(GoodDataRow).Replace(
                "| REQ-FUNC-01 | 기준일자를 검증한다 | ## 로직 흐름 요약 > \"기준일자를 검증한다\" | 도출 |",
                "| REQ-FUNC-01 | 기준일자를 검증한다 | ## 파라미터 목록 > \"@BaseDate\" | 도출 |");

            var result = PrdAttributionValidator.Validate(prd, Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.EvidenceSourceNotAllowed && d.RequirementId == "REQ-FUNC-01");
        }

        [Fact]
        public void Validate_ShouldAllowEitherSource_ForTheNonFunctionalSection()
        {
            // 예외 및 비기능 요구사항만 원천이 둘이다. 둘 다 통과해야 한다.
            var prd = PrdWith(GoodDataRow).Replace(
                "| REQ-NFR-01 | 중복 적재를 막는다 | ## CRUD 분석 > \"중복 검사\" | 추정 |",
                "| REQ-NFR-01 | 중복 적재를 막는다 | ## 로직 흐름 요약 > \"미집계 건을 조회한다\" | 추정 |");

            var result = PrdAttributionValidator.Validate(prd, Spec);

            Assert.DoesNotContain(result.Defects, d => d.Type == PrdDefectType.EvidenceSourceNotAllowed);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdAttributionValidatorTests`
Expected: 새 테스트 3건 중 최소 2건 FAIL (`IdPrefixMismatch`·`EvidenceSourceNotAllowed`가 발화하지 않음)

- [ ] **Step 3: 근거 칸 파서를 더한다**

`PrdAttributionValidator.cs`의 클래스 안에 추가:

```csharp
        /// <summary>근거 칸을 헤딩과 인용 구절로 가른 것.</summary>
        public sealed record PrdEvidenceReference(string Heading, string Quote);

        /// <summary>
        /// `## CRUD 분석 &gt; "TB_SETTLE_DAILY에 INSERT"` 형태를 가른다.
        ///
        /// 줄번호가 아니라 헤딩+인용을 쓰는 이유: 줄번호는 Spec을 다시 생성하는
        /// 순간 전부 거짓이 되지만, 헤딩과 원문 구절은 두 문서가 같은 이야기를
        /// 하는 한 살아 있다.
        /// </summary>
        public static bool TryParseEvidence(string? raw, out PrdEvidenceReference reference)
        {
            reference = new PrdEvidenceReference(string.Empty, string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var separator = raw.IndexOf('>');
            if (separator < 0)
            {
                return false;
            }

            var heading = raw[..separator].Trim();
            var rest = raw[(separator + 1)..].Trim();

            var first = rest.IndexOfAny(new[] { '"', '“' });
            var last = rest.LastIndexOfAny(new[] { '"', '”' });
            if (first < 0 || last <= first)
            {
                return false;
            }

            var quote = rest[(first + 1)..last].Trim();
            if (heading.Length == 0 || quote.Length == 0)
            {
                return false;
            }

            reference = new PrdEvidenceReference(heading, quote);
            return true;
        }
```

- [ ] **Step 4: 파생 대응 검사를 요구 순회 루프에 더한다**

`Validate`의 `foreach (var requirement in ...)` 안, 근거 빈 칸 검사 **뒤에** 넣는다:

```csharp
                var rule = PrdSectionContract.Sections.First(s => s.Heading == requirement.Section);

                if (!requirement.Id.StartsWith(rule.IdPrefix + "-", StringComparison.Ordinal))
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.IdPrefixMismatch,
                        requirement.Section,
                        requirement.Id,
                        $"'{requirement.Section}'의 요구 ID는 '{rule.IdPrefix}-'로 시작해야 합니다."));
                }

                if (!TryParseEvidence(requirement.EvidenceRaw, out var evidence))
                {
                    if (!string.IsNullOrWhiteSpace(requirement.EvidenceRaw))
                    {
                        defects.Add(new PrdDefect(
                            PrdDefectType.EvidenceMissing,
                            requirement.Section,
                            requirement.Id,
                            "근거 칸이 '## 헤딩 > \"원문 구절\"' 형식이 아닙니다."));
                    }

                    continue;
                }

                if (!rule.AllowedSources.Contains(evidence.Heading))
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.EvidenceSourceNotAllowed,
                        requirement.Section,
                        requirement.Id,
                        $"'{requirement.Section}'은 {string.Join(", ", rule.AllowedSources)}에서만 파생할 수 있습니다. 실제 인용: '{evidence.Heading}'"));
                }
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdAttributionValidatorTests`
Expected: PASS

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/PrdAttributionValidator.cs \
        tests/ReSet.Core.Tests/PrdAttributionValidatorTests.cs
git commit -m "feat: PRD 섹션이 허용된 Spec 원천에서만 파생하는지 검사한다"
```

---

### Task 3: Spec 대조 검사 (헤딩 실재·인용 실재)

여기가 이 설계의 오라클이다. 기준값은 모델이 건드릴 수 없는 원본 `Spec.md`다.

**Files:**
- Modify: `src/ReSet.Core/Services/PrdAttributionValidator.cs`
- Test: `tests/ReSet.Core.Tests/PrdAttributionValidatorTests.cs`

**Interfaces:**
- Consumes: Task 2의 `TryParseEvidence`, `PrdEvidenceReference`
- Produces: `PrdDefectType.EvidenceHeadingNotFound`, `PrdDefectType.EvidenceQuoteNotFound` 발화

- [ ] **Step 1: 실패 테스트를 쓴다 (결함 주입 회귀 포함)**

`PrdAttributionValidatorTests` 클래스 안에 추가:

```csharp
        [Fact]
        public void Validate_ShouldReportHeadingNotFound_WhenSpecHasNoSuchHeading()
        {
            var specWithoutCrud = Spec.Replace("## CRUD 분석", "## 데이터 조작 분석");
            var prd = PrdWith(GoodDataRow);

            var result = PrdAttributionValidator.Validate(prd, specWithoutCrud);

            Assert.Contains(result.Defects, d => d.Type == PrdDefectType.EvidenceHeadingNotFound);
        }

        [Fact]
        public void Validate_ShouldReportQuoteNotFound_WhenTheQuoteIsNotInThatSection()
        {
            // 인용 구절이 Spec 어디에도 없다.
            var row = "| REQ-DATA-01 | 미집계 건을 적재한다 | ## CRUD 분석 > \"TB_SETTLE_MONTHLY에 INSERT\" | 도출 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.EvidenceQuoteNotFound && d.RequirementId == "REQ-DATA-01");
        }

        [Fact]
        public void Validate_ShouldReportQuoteNotFound_WhenTheQuoteLivesInAnotherSection()
        {
            // 구절은 Spec에 있지만 인용한 헤딩 아래가 아니다. 문서 전체 검색으로
            // 대조하면 이것을 놓친다.
            var row = "| REQ-DATA-01 | 기준일자를 검증한다 | ## CRUD 분석 > \"기준일자를 검증한다\" | 도출 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.EvidenceQuoteNotFound && d.RequirementId == "REQ-DATA-01");
        }

        [Fact]
        public void Validate_ShouldTolerateMarkdownEmphasisAndSpacingInTheSpec()
        {
            // Spec 본문의 강조 표기는 인용과 글자가 달라 보이게 만든다. 이것으로
            // 오탐이 나면 검사는 곧 꺼진다.
            var emphasised = Spec.Replace(
                "TB_SETTLE_DAILY에 INSERT 한다.",
                "**TB_SETTLE_DAILY**에 `INSERT` 한다.");

            var result = PrdAttributionValidator.Validate(PrdWith(GoodDataRow), emphasised);

            Assert.DoesNotContain(result.Defects, d => d.Type == PrdDefectType.EvidenceQuoteNotFound);
        }

        [Fact]
        public void Validate_ShouldFire_WhenASingleCharacterOfTheQuoteIsAltered()
        {
            // 결함 주입 회귀. 이것이 없으면 검사가 살아 있는지 알 수 없다.
            var tampered = PrdWith(GoodDataRow).Replace(
                "TB_SETTLE_DAILY에 INSERT",
                "TB_SETTLE_DAIL7에 INSERT");

            var result = PrdAttributionValidator.Validate(tampered, Spec);

            Assert.Contains(result.Defects, d => d.Type == PrdDefectType.EvidenceQuoteNotFound);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdAttributionValidatorTests`
Expected: 새 테스트 중 `HeadingNotFound`·`QuoteNotFound`·`SingleCharacter` FAIL

- [ ] **Step 3: 정규화와 섹션 본문 추출을 더한다**

`PrdAttributionValidator.cs`의 클래스 안에 추가:

```csharp
        /// <summary>
        /// 인용 대조용 정규화. 공백과 마크다운 강조·표 파이프를 걷어낸다.
        ///
        /// 이것이 없으면 Spec 본문의 `**강조**`나 표 정렬 공백 때문에 멀쩡한 인용이
        /// 결함으로 보고된다. 오탐이 잦은 검사는 곧 꺼지므로, 대조는 두 문자열이
        /// 같은 내용을 말하는지만 본다.
        /// </summary>
        private static string NormalizeForQuoteMatch(string text)
        {
            var kept = text.Where(ch => !char.IsWhiteSpace(ch)
                                        && ch != '*' && ch != '`' && ch != '|'
                                        && ch != '_' && ch != '~');
            return string.Concat(kept);
        }

        /// <summary>지정 헤딩 아래 본문만 이어 붙인다. 헤딩이 없으면 null.</summary>
        private static string? ExtractSectionBody(IReadOnlyList<string> specLines, string heading)
        {
            var (headerIndex, endIndex) = MarkdownSectionLocator.LocateSection(specLines, heading, "## ");
            if (headerIndex < 0)
            {
                return null;
            }

            return string.Join("\n", specLines.Skip(headerIndex + 1).Take(endIndex - headerIndex - 1));
        }
```

- [ ] **Step 4: 대조 검사를 요구 순회 루프에 더한다**

`Validate` 메서드 첫머리에 Spec 라인을 준비한다:

```csharp
            var specLines = MarkdownSectionLocator.SplitLines(specMarkdown);
            var sectionBodyCache = new Dictionary<string, string?>(StringComparer.Ordinal);
```

그리고 Task 2가 넣은 `EvidenceSourceNotAllowed` 검사 **뒤에** 이어 붙인다:

```csharp
                if (!sectionBodyCache.TryGetValue(evidence.Heading, out var body))
                {
                    body = ExtractSectionBody(specLines, evidence.Heading);
                    sectionBodyCache[evidence.Heading] = body;
                }

                if (body is null)
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.EvidenceHeadingNotFound,
                        requirement.Section,
                        requirement.Id,
                        $"근거로 인용한 헤딩 '{evidence.Heading}'이 원본 명세서에 없습니다."));
                    continue;
                }

                if (!NormalizeForQuoteMatch(body).Contains(
                        NormalizeForQuoteMatch(evidence.Quote), StringComparison.Ordinal))
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.EvidenceQuoteNotFound,
                        requirement.Section,
                        requirement.Id,
                        $"인용 구절 \"{evidence.Quote}\"을 '{evidence.Heading}' 절 본문에서 찾을 수 없습니다."));
                }
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdAttributionValidatorTests`
Expected: PASS (전건)

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/PrdAttributionValidator.cs \
        tests/ReSet.Core.Tests/PrdAttributionValidatorTests.cs
git commit -m "feat: PRD 근거 인용이 Spec의 해당 절에 실재하는지 대조한다"
```

---

### Task 4: 교정 피드백과 미검증 배너

검사가 결함을 찾았을 때 (a) 모델에게 되돌릴 문장과 (b) 사람에게 보일 배너를 만든다. 배너는 결함이 없을 때도 붙는다 — §6.2의 구멍을 독자가 알아야 하기 때문이다.

**Files:**
- Create: `src/ReSet.Core/Services/PrdAttributionReport.cs`
- Test: `tests/ReSet.Core.Tests/PrdAttributionReportTests.cs`

**Interfaces:**
- Consumes: Task 1~3의 `PrdValidationResult`, `PrdDefect`, `PrdDefectType`
- Produces:
  - `PrdAttributionReport.BuildPromptFix(PrdValidationResult result)` → `string`
  - `PrdAttributionReport.BuildBanner(PrdValidationResult result)` → `string`

- [ ] **Step 1: 실패 테스트를 쓴다**

`tests/ReSet.Core.Tests/PrdAttributionReportTests.cs`:

```csharp
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdAttributionReportTests
    {
        private static PrdValidationResult ResultWith(params PrdDefect[] defects) =>
            new(new List<PrdDefect>(defects));

        [Fact]
        public void BuildBanner_ShouldStateTheUncheckedGap_EvenWhenThereAreNoDefects()
        {
            // 결함이 없다고 해서 「요구와 근거의 대응」이 검증된 것이 아니다.
            // 그 사실을 숨기면 독자가 검사를 실제보다 강하게 믿는다.
            var banner = PrdAttributionReport.BuildBanner(ResultWith());

            Assert.Contains("실재", banner);
            Assert.Contains("미검증", banner);
            Assert.Contains("추정", banner);
        }

        [Fact]
        public void BuildBanner_ShouldListDefects_WhenAttributionFailed()
        {
            var banner = PrdAttributionReport.BuildBanner(ResultWith(new PrdDefect(
                PrdDefectType.EvidenceQuoteNotFound,
                "## 데이터 요구사항",
                "REQ-DATA-01",
                "인용 구절을 찾을 수 없습니다.")));

            Assert.Contains("REQ-DATA-01", banner);
            Assert.Contains("CAUTION", banner);
        }

        [Fact]
        public void BuildPromptFix_ShouldNameEveryDefectiveRequirement()
        {
            var fix = PrdAttributionReport.BuildPromptFix(ResultWith(
                new PrdDefect(PrdDefectType.ConfidenceVocabulary, "## 데이터 요구사항", "REQ-DATA-02", "확신도 어휘 위반"),
                new PrdDefect(PrdDefectType.SectionMissing, "## 기능 요구사항", string.Empty, "섹션 없음")));

            Assert.Contains("REQ-DATA-02", fix);
            Assert.Contains("## 기능 요구사항", fix);
        }

        [Fact]
        public void BuildPromptFix_ShouldBeEmpty_WhenValid()
        {
            Assert.Equal(string.Empty, PrdAttributionReport.BuildPromptFix(ResultWith()));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdAttributionReportTests`
Expected: 컴파일 실패 — `PrdAttributionReport` 없음

- [ ] **Step 3: 구현한다**

`src/ReSet.Core/Services/PrdAttributionReport.cs`:

```csharp
using System.Linq;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 귀속 검사 결과를 두 독자에게 옮긴다 - 교정 재호출을 받을 모델과, 문서를 읽을 사람.
    /// </summary>
    public static class PrdAttributionReport
    {
        /// <summary>
        /// 문서 상단 배너. 결함이 없어도 낸다.
        ///
        /// 기계가 확인한 것은 「인용이 실재하는가」까지이고 「요구와 근거가 대응하는가」는
        /// 확인하지 않았다. 그 경계를 적지 않으면 독자가 검사를 실제보다 강하게 믿는다.
        /// </summary>
        public static string BuildBanner(PrdValidationResult result)
        {
            var sb = new StringBuilder();

            if (result.IsValid)
            {
                sb.AppendLine("> [!NOTE]");
                sb.AppendLine("> **귀속 검사**: 모든 요구 항목의 근거 인용이 원본 명세서에 **실재**함을 기계로 확인했습니다.");
            }
            else
            {
                sb.AppendLine("> [!CAUTION]");
                sb.AppendLine($"> **귀속 검사 미통과**: 아래 {result.Defects.Count}건의 결함이 남아 있습니다.");
                foreach (var defect in result.Defects)
                {
                    var subject = string.IsNullOrEmpty(defect.RequirementId)
                        ? defect.Section
                        : $"{defect.Section} / {defect.RequirementId}";
                    sb.AppendLine($"> - `{subject}` — {defect.Message}");
                }
            }

            sb.AppendLine("> ");
            sb.AppendLine("> 검증된 것은 근거 인용의 **실재**뿐입니다. 요구와 근거의 **대응**은 **미검증**이며,");
            sb.AppendLine("> `추정` 항목은 원본 명세서에 없는 재구성입니다. 이 문서는 L2/L3 검증 파이프라인을 거치지 않았습니다.");
            sb.AppendLine();

            return sb.ToString();
        }

        /// <summary>교정 재호출에 실을 결함 목록. 통과했으면 빈 문자열이다.</summary>
        public static string BuildPromptFix(PrdValidationResult result)
        {
            if (result.IsValid)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine("[귀속 검사 피드백] 아래 결함을 모두 고쳐 문서 전체를 다시 출력하십시오.");
            sb.AppendLine("근거 칸은 반드시 `## <Spec 헤딩> > \"<원문 구절>\"` 형식이어야 하며, 인용 구절은 원본 명세서에서 글자 그대로 옮겨야 합니다.");
            sb.AppendLine();

            foreach (var group in result.Defects.GroupBy(d => d.Section))
            {
                sb.AppendLine($"### {group.Key}");
                foreach (var defect in group)
                {
                    var subject = string.IsNullOrEmpty(defect.RequirementId) ? "(섹션 전체)" : defect.RequirementId;
                    sb.AppendLine($"- {subject}: {defect.Message}");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdAttributionReportTests`
Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/PrdAttributionReport.cs \
        tests/ReSet.Core.Tests/PrdAttributionReportTests.cs
git commit -m "feat: 귀속 검사 결과를 교정 피드백과 미검증 배너로 옮긴다"
```

---

### Task 5: AI 생성 메서드

**Files:**
- Modify: `src/ReSet.Core/Services/IAiService.cs` (인터페이스 메서드 추가)
- Modify: `src/ReSet.Core/Services/AiService.cs` (구현 추가 — `GenerateSettlementPolicyRulebookAsync` 바로 뒤, 파일 끝 근처)
- Test: `tests/ReSet.Core.Tests/PrdPromptTests.cs`

**Interfaces:**
- Consumes: `PrdSectionContract.Sections` (Task 1), `IAiClient.ChatAsync`, `AiResult`
- Produces: `IAiService.GeneratePrdFromSpecAsync(string objectLabel, string specMarkdown, string? attributionFeedback = null, string? effort = null, CancellationToken cancellationToken = default)` → `Task<AiResult>`

- [ ] **Step 1: 프롬프트 계약 테스트를 쓴다**

`IAiService`를 구현한 모든 대역(NSubstitute 대체 포함)이 깨지지 않도록 인터페이스 추가부터 확인하고, 프롬프트가 계약을 싣는지 본다.

`tests/ReSet.Core.Tests/PrdPromptTests.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdPromptTests
    {
        private static (AiService Service, IAiClient Client) Build()
        {
            var client = Substitute.For<IAiClient>();
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "## 배경 및 목적" }));

            // 생성자 인자는 src/ReSet.Cli/Program.cs:606의 실제 생성 구문과 같은 순서다.
            // temperature는 float이고 contextScope는 string?(설정 값)이다.
            var service = new AiService(client, 0.2f, false, 8, true, null);
            return (service, client);
        }

        [Fact]
        public async Task GeneratePrdFromSpecAsync_ShouldCarryEverySectionAndItsAllowedSources()
        {
            var (service, _) = Build();

            var result = await service.GeneratePrdFromSpecAsync("dbo.UP_TEST", "## 개요\n\n본문", null, null, CancellationToken.None);

            foreach (var rule in PrdSectionContract.Sections)
            {
                Assert.Contains(rule.Heading, result.SystemPrompt);
                Assert.Contains(rule.IdPrefix, result.SystemPrompt);
                foreach (var source in rule.AllowedSources)
                {
                    Assert.Contains(source, result.SystemPrompt!);
                }
            }
        }

        [Fact]
        public async Task GeneratePrdFromSpecAsync_ShouldForbidEvidenceOutsideTheSpec()
        {
            var (service, _) = Build();

            var result = await service.GeneratePrdFromSpecAsync("dbo.UP_TEST", "## 개요\n\n본문", null, null, CancellationToken.None);

            Assert.Contains("도출", result.SystemPrompt);
            Assert.Contains("추정", result.SystemPrompt);
            Assert.Contains("verbatim", result.SystemPrompt);
        }

        [Fact]
        public async Task GeneratePrdFromSpecAsync_ShouldCarryTheAttributionFeedback_WhenRetrying()
        {
            var (service, _) = Build();

            var result = await service.GeneratePrdFromSpecAsync(
                "dbo.UP_TEST", "## 개요\n\n본문", "REQ-DATA-01: 인용을 찾을 수 없습니다.", null, CancellationToken.None);

            Assert.Contains("REQ-DATA-01", result.UserPrompt);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdPromptTests`
Expected: 컴파일 실패 — `GeneratePrdFromSpecAsync` 없음

> 생성자를 바꾸지 않는다. 인자가 어긋나면 `src/ReSet.Cli/Program.cs:606`의 실제 생성 구문을 그대로 베껴 맞춘다:
> `new AiService(aiClient, temp, enableOllamaThinking, criticThresholdScore, enableLocalChunking, promptContextScope)`

- [ ] **Step 3: 인터페이스에 메서드를 더한다**

`src/ReSet.Core/Services/IAiService.cs`의 `GenerateSettlementPolicyRulebookAsync` 선언 아래에 추가:

```csharp
        Task<AiResult> GeneratePrdFromSpecAsync(string objectLabel, string specMarkdown, string? attributionFeedback = null, string? effort = null, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: `AiService`에 구현을 더한다**

`src/ReSet.Core/Services/AiService.cs`의 `GenerateSettlementPolicyRulebookAsync` 메서드 **뒤**에 추가:

```csharp
        /// <summary>
        /// 완성된 명세서 하나만을 근거로 요구사항 문서를 도출한다.
        ///
        /// [왜 원본 DDL을 싣지 않는가] 근거를 Spec.md로 한정해야 귀속 검사의 오라클이
        /// 성립한다. DDL을 함께 실으면 모델이 명세서에 없는 사실을 요구로 올리고,
        /// 그것은 어떤 인용으로도 대조할 수 없다.
        /// </summary>
        public async Task<AiResult> GeneratePrdFromSpecAsync(
            string objectLabel,
            string specMarkdown,
            string? attributionFeedback = null,
            string? effort = null,
            CancellationToken cancellationToken = default)
        {
            var contract = new StringBuilder();
            foreach (var rule in PrdSectionContract.Sections)
            {
                contract.AppendLine(
                    $"   - `{rule.Heading}` — requirement IDs MUST start with `{rule.IdPrefix}-`; evidence MUST cite only: {string.Join(" or ", rule.AllowedSources)}");
            }

            var systemPrompt = $@"You are a business analyst reconstructing the product requirements that a legacy stored procedure implements.
Your ONLY source is the Korean specification document supplied by the user. You have no access to the original SQL.

[Absolute rules]
1. Every requirement MUST carry evidence quoted verbatim from the specification. Never invent a fact that is not in the document.
2. Write the document in Korean using EXACTLY these five H2 sections, in this order:
{contract}3. Each section's body is a four-column markdown table with this exact header row:
   | ID | 요구사항 | 근거 | 확신도 |
4. The 근거 cell format is `## <specification heading> > ""<verbatim excerpt>""`. The excerpt MUST appear verbatim inside that heading's section. Never cite line numbers.
5. The 확신도 cell is exactly one of two words: `도출` (the excerpt directly supports the requirement) or `추정` (you reconstructed it from several facts). A `추정` row still requires the excerpt it started from.
6. Requirement IDs are `<prefix>-<two digits>`, numbered from 01 within each section.
7. Do not include Mermaid diagrams. Do not wrap the response in a markdown code block. Do not add greetings or a closing summary.
8. Describe what the business needs, not how the procedure implements it. Do not restate SQL, table joins, or control flow.";

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine($"[Target object] {objectLabel}");
            userPrompt.AppendLine();
            userPrompt.AppendLine("[Specification document — the only source of truth]");
            userPrompt.AppendLine(specMarkdown);

            if (!string.IsNullOrWhiteSpace(attributionFeedback))
            {
                userPrompt.AppendLine();
                userPrompt.AppendLine("[Attribution check feedback — the previous draft failed these]");
                userPrompt.AppendLine(attributionFeedback);
            }

            Log.Information("AI 요구사항 문서 도출 요청 전송 - 대상: {Object}", objectLabel);

            // cancellationToken을 위치 인자로 넘기면 volatileUserSuffix에 바인딩된다.
            // 명명 인자를 쓴다 - 기존 호출부도 모두 이 방식이다.
            var aiResult = await _aiClient.ChatAsync(
                systemPrompt,
                userPrompt.ToString(),
                _temperature,
                effort: effort,
                cancellationToken: cancellationToken)
                ?? new AiResult();

            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt.ToString();

            Log.Information("AI 요구사항 문서 도출 완료 - 응답 길이: {Length}", aiResult.Content?.Length ?? 0);

            return aiResult;
        }
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdPromptTests`
Expected: PASS

- [ ] **Step 6: 전체 테스트로 인터페이스 파급을 확인한다**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0. `IAiService`를 손으로 구현한 테스트 대역이 있으면 새 메서드를 더해 고친다.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/IAiService.cs \
        src/ReSet.Core/Services/AiService.cs \
        tests/ReSet.Core.Tests/PrdPromptTests.cs
git commit -m "feat: 명세서만을 근거로 요구사항 문서를 생성하는 AI 호출을 더한다"
```

---

### Task 6: `PrdDerivationService`

생성 → 검사 → (실패 시) 교정 재호출 1회 → 배너 → 저장. UI를 참조하지 않는다.

**Files:**
- Create: `src/ReSet.Core/Services/IPrdDerivationService.cs`
- Create: `src/ReSet.Core/Services/PrdDerivationService.cs`
- Modify: `src/ReSet.Core/Services/OutputPathResolver.cs` (파일명 상수 추가)
- Test: `tests/ReSet.Core.Tests/PrdDerivationServiceTests.cs`

**Interfaces:**
- Consumes: `IAiService.GeneratePrdFromSpecAsync` (Task 5), `PrdAttributionValidator.Validate` (Task 3), `PrdAttributionReport` (Task 4), `VerificationDocumentFormatter.FormatUnverifiedDocument` (기존)
- Produces:
  - `OutputPathResolver.SpecFileNamePublic` → `"Spec.md"`, `OutputPathResolver.PrdFileName` → `"Prd.md"`
  - `IPrdDerivationService.DeriveAsync(string docsDirectory, string objectLabel, string? effort, CancellationToken ct)` → `Task<PrdDerivationOutcome>`
  - `PrdDerivationOutcome(string PrdPath, bool AttributionClean, IReadOnlyList<PrdDefect> Defects)`

- [ ] **Step 1: 경로 상수를 공개한다**

`src/ReSet.Core/Services/OutputPathResolver.cs`의 기존 `private const string SpecFileName = "Spec.md";` 바로 아래에 추가:

```csharp
    /// <summary>
    /// 산출물 파일명의 단일 출처. PRD 도출은 이미 발견한 docs 디렉터리 옆에 쓰므로
    /// CodeObjectKey를 만들지 않지만, 파일명만은 여기서 가져가 조립처가 갈라지지 않게 한다.
    /// </summary>
    public const string PrdFileName = "Prd.md";

    /// <summary>위 상수와 같은 이유로 공개한다 - 디렉터리 스캔이 이 이름을 찾는다.</summary>
    public const string SpecFileNamePublic = SpecFileName;
```

- [ ] **Step 2: 실패 테스트를 쓴다**

`tests/ReSet.Core.Tests/PrdDerivationServiceTests.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdDerivationServiceTests : IDisposable
    {
        private readonly string _docsDir;

        public PrdDerivationServiceTests()
        {
            _docsDir = Path.Combine(Path.GetTempPath(), "reset-prd-" + Guid.NewGuid().ToString("N"), "docs");
            Directory.CreateDirectory(_docsDir);
            File.WriteAllText(Path.Combine(_docsDir, "Spec.md"), Spec);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(_docsDir)!, true); } catch { }
        }

        private const string Spec = @"## 개요

일별 정산 마감을 수행한다.

## 파라미터 목록

@BaseDate 를 받는다.

## CRUD 분석

TB_SETTLE_DAILY에 INSERT 한다.

## 로직 흐름 요약

기준일자를 검증한다.
";

        private static string SoundPrd() =>
            "## 배경 및 목적\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-BG-01 | 일별 정산을 마감한다 | ## 개요 > \"일별 정산 마감\" | 도출 |\n\n"
            + "## 수행 조건 및 입력 계약\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-IN-01 | 기준일자를 받는다 | ## 파라미터 목록 > \"@BaseDate\" | 도출 |\n\n"
            + "## 데이터 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-DATA-01 | 미집계 건을 적재한다 | ## CRUD 분석 > \"TB_SETTLE_DAILY에 INSERT\" | 도출 |\n\n"
            + "## 기능 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-FUNC-01 | 기준일자를 검증한다 | ## 로직 흐름 요약 > \"기준일자를 검증한다\" | 도출 |\n\n"
            + "## 예외 및 비기능 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-NFR-01 | 중복 적재를 막는다 | ## CRUD 분석 > \"INSERT\" | 추정 |\n";

        private static string BrokenPrd() =>
            SoundPrd().Replace("TB_SETTLE_DAILY에 INSERT\"", "TB_SETTLE_MONTHLY에 INSERT\"");

        private static IAiService AiReturning(params string[] bodies)
        {
            var ai = Substitute.For<IAiService>();
            var call = 0;
            ai.GeneratePrdFromSpecAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new AiResult { Content = bodies[Math.Min(call++, bodies.Length - 1)] }));
            ai.ProviderName.Returns("TestProvider");
            ai.ModelName.Returns("test-model");
            return ai;
        }

        [Fact]
        public async Task DeriveAsync_ShouldWritePrdBesideSpec()
        {
            var service = new PrdDerivationService(AiReturning(SoundPrd()));

            var outcome = await service.DeriveAsync(_docsDir, "dbo.UP_TEST", null, CancellationToken.None);

            Assert.Equal(Path.Combine(_docsDir, "Prd.md"), outcome.PrdPath);
            Assert.True(File.Exists(outcome.PrdPath));
            Assert.True(outcome.AttributionClean);
        }

        [Fact]
        public async Task DeriveAsync_ShouldRetryOnce_WhenAttributionFails()
        {
            var ai = AiReturning(BrokenPrd(), SoundPrd());
            var service = new PrdDerivationService(ai);

            var outcome = await service.DeriveAsync(_docsDir, "dbo.UP_TEST", null, CancellationToken.None);

            Assert.True(outcome.AttributionClean);
            await ai.Received(2).GeneratePrdFromSpecAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task DeriveAsync_ShouldSaveWithDefectBanner_WhenRetryStillFails()
        {
            // 결함이 있다고 문서를 버리면 사람이 볼 것도, 무엇이 틀렸는지도 사라진다.
            var service = new PrdDerivationService(AiReturning(BrokenPrd(), BrokenPrd()));

            var outcome = await service.DeriveAsync(_docsDir, "dbo.UP_TEST", null, CancellationToken.None);

            Assert.False(outcome.AttributionClean);
            Assert.True(File.Exists(outcome.PrdPath));
            var written = await File.ReadAllTextAsync(outcome.PrdPath);
            Assert.Contains("CAUTION", written);
            Assert.Contains("REQ-DATA-01", written);
        }

        [Fact]
        public async Task DeriveAsync_ShouldNotRetry_WhenTheFirstDraftIsClean()
        {
            var ai = AiReturning(SoundPrd());
            var service = new PrdDerivationService(ai);

            await service.DeriveAsync(_docsDir, "dbo.UP_TEST", null, CancellationToken.None);

            await ai.Received(1).GeneratePrdFromSpecAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task DeriveAsync_ShouldThrow_WhenSpecIsAbsent()
        {
            var emptyDir = Path.Combine(Path.GetTempPath(), "reset-prd-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(emptyDir);
            var service = new PrdDerivationService(AiReturning(SoundPrd()));

            await Assert.ThrowsAsync<FileNotFoundException>(
                () => service.DeriveAsync(emptyDir, "dbo.UP_TEST", null, CancellationToken.None));

            Directory.Delete(emptyDir, true);
        }
    }
}
```

- [ ] **Step 3: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdDerivationServiceTests`
Expected: 컴파일 실패 — `PrdDerivationService` 없음

- [ ] **Step 4: 인터페이스를 쓴다**

`src/ReSet.Core/Services/IPrdDerivationService.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReSet.Core.Services
{
    /// <summary>PRD 도출 한 건의 결과. Defects는 최종 저장본에 남은 결함이다.</summary>
    public sealed record PrdDerivationOutcome(
        string PrdPath,
        bool AttributionClean,
        IReadOnlyList<PrdDefect> Defects);

    public interface IPrdDerivationService
    {
        Task<PrdDerivationOutcome> DeriveAsync(
            string docsDirectory,
            string objectLabel,
            string? effort,
            CancellationToken cancellationToken = default);
    }
}
```

- [ ] **Step 5: 서비스를 구현한다**

`src/ReSet.Core/Services/PrdDerivationService.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 완성된 Spec.md 하나를 요구사항 문서로 옮긴다.
    ///
    /// [왜 DB에 접속하지 않는가] 입력이 파일 하나뿐이어야 이미 쌓인 산출물에
    /// 재분석 없이 소급 적용된다. 그것이 이 기능을 1번 분석에 붙이지 않고
    /// 별도로 기동하기로 한 실질적 이유다.
    ///
    /// [왜 재호출이 한 번인가] 이 문서에는 L2 Actor-Critic 보정 루프가 없다.
    /// 수렴하지 않는 루프를 새로 만드는 대신, 한 번 되돌리고 남은 결함은
    /// 배너에 박아 사람 검토로 넘긴다.
    /// </summary>
    public sealed class PrdDerivationService : IPrdDerivationService
    {
        private readonly IAiService _aiService;

        public PrdDerivationService(IAiService aiService) =>
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));

        public async Task<PrdDerivationOutcome> DeriveAsync(
            string docsDirectory,
            string objectLabel,
            string? effort,
            CancellationToken cancellationToken = default)
        {
            var specPath = Path.Combine(docsDirectory, OutputPathResolver.SpecFileNamePublic);
            if (!File.Exists(specPath))
            {
                throw new FileNotFoundException("근거가 될 명세서가 없습니다.", specPath);
            }

            var specMarkdown = await File.ReadAllTextAsync(specPath, cancellationToken);

            var draft = await _aiService.GeneratePrdFromSpecAsync(
                objectLabel, specMarkdown, null, effort, cancellationToken);
            var body = draft.Content ?? string.Empty;
            var validation = PrdAttributionValidator.Validate(body, specMarkdown);

            if (!validation.IsValid)
            {
                Log.Information(
                    "PRD 귀속 검사 미통과 - 대상: {Object}, 결함 {Count}건. 교정 재호출 1회를 시도합니다.",
                    objectLabel, validation.Defects.Count);

                var retry = await _aiService.GeneratePrdFromSpecAsync(
                    objectLabel, specMarkdown, PrdAttributionReport.BuildPromptFix(validation), effort, cancellationToken);
                var retryBody = retry.Content ?? string.Empty;
                var retryValidation = PrdAttributionValidator.Validate(retryBody, specMarkdown);

                // 재시도가 더 나빠졌으면 첫 초안을 지킨다 - 결함 수가 유일하게
                // 비교 가능한 척도다.
                if (retryValidation.Defects.Count <= validation.Defects.Count)
                {
                    body = retryBody;
                    validation = retryValidation;
                }
            }

            var document = PrdAttributionReport.BuildBanner(validation)
                + VerificationDocumentFormatter.FormatUnverifiedDocument(
                    body, null, _aiService.ProviderName, _aiService.ModelName, effort, DateTime.Now);

            var prdPath = Path.Combine(docsDirectory, OutputPathResolver.PrdFileName);
            await File.WriteAllTextAsync(prdPath, document, cancellationToken);

            Log.Information("PRD 저장 완료 - {Path} (귀속 결함 {Count}건)", prdPath, validation.Defects.Count);

            return new PrdDerivationOutcome(prdPath, validation.IsValid, validation.Defects);
        }
    }
}
```

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdDerivationServiceTests`
Expected: PASS

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/IPrdDerivationService.cs \
        src/ReSet.Core/Services/PrdDerivationService.cs \
        src/ReSet.Core/Services/OutputPathResolver.cs \
        tests/ReSet.Core.Tests/PrdDerivationServiceTests.cs
git commit -m "feat: 명세서에서 요구사항 문서를 도출해 저장하는 서비스를 세운다"
```

---

### Task 7: 대상 발견과 CLI 메뉴 배선

**Files:**
- Create: `src/ReSet.Core/Services/PrdTargetDiscovery.cs`
- Modify: `src/ReSet.Cli/Program.cs:1166-1190` (메뉴 배열과 분기)
- Test: `tests/ReSet.Core.Tests/PrdTargetDiscoveryTests.cs`

**Interfaces:**
- Consumes: `OutputPathResolver.SpecFileNamePublic`, `OutputPathResolver.PrdFileName` (Task 6), `IPrdDerivationService.DeriveAsync` (Task 6)
- Produces: `PrdTargetDiscovery.Find(string outputRoot)` → `IReadOnlyList<PrdTarget>`, `PrdTarget(string Label, string DocsDirectory, bool HasExistingPrd)`

- [ ] **Step 1: 발견 로직의 실패 테스트를 쓴다**

`tests/ReSet.Core.Tests/PrdTargetDiscoveryTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdTargetDiscoveryTests : IDisposable
    {
        private readonly string _root;

        public PrdTargetDiscoveryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "reset-discovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void Seed(string relativeObjectDir, bool withSpec, bool withPrd)
        {
            var docs = Path.Combine(_root, relativeObjectDir, "docs");
            Directory.CreateDirectory(docs);
            if (withSpec) File.WriteAllText(Path.Combine(docs, "Spec.md"), "## 개요");
            if (withPrd) File.WriteAllText(Path.Combine(docs, "Prd.md"), "## 배경 및 목적");
        }

        [Fact]
        public void Find_ShouldListOnlyObjectsThatHaveASpec()
        {
            Seed(Path.Combine("Procedures", "dbo.UP_A"), withSpec: true, withPrd: false);
            Seed(Path.Combine("Procedures", "dbo.UP_B"), withSpec: false, withPrd: false);

            var targets = PrdTargetDiscovery.Find(_root);

            Assert.Single(targets);
            Assert.Equal("dbo.UP_A", targets[0].Label);
        }

        [Fact]
        public void Find_ShouldFlagObjectsThatAlreadyHaveAPrd()
        {
            Seed(Path.Combine("Procedures", "dbo.UP_A"), withSpec: true, withPrd: true);

            var targets = PrdTargetDiscovery.Find(_root);

            Assert.True(targets[0].HasExistingPrd);
        }

        [Fact]
        public void Find_ShouldIgnoreFunctionsAndExternal()
        {
            // 1차 범위는 Procedures 뿐이다(설계 §7.1).
            Seed(Path.Combine("Procedures", "dbo.UP_A"), withSpec: true, withPrd: false);
            Seed(Path.Combine("Functions", "dbo.UF_A"), withSpec: true, withPrd: false);
            Seed(Path.Combine("External", "OtherDb", "Procedures", "dbo.UP_C"), withSpec: true, withPrd: false);

            var targets = PrdTargetDiscovery.Find(_root);

            Assert.Single(targets);
            Assert.Equal("dbo.UP_A", targets[0].Label);
        }

        [Fact]
        public void Find_ShouldReturnEmpty_WhenOutputRootDoesNotExist()
        {
            var targets = PrdTargetDiscovery.Find(Path.Combine(_root, "nope"));

            Assert.Empty(targets);
        }

        [Fact]
        public void Find_ShouldSortByLabel()
        {
            Seed(Path.Combine("Procedures", "dbo.UP_B"), withSpec: true, withPrd: false);
            Seed(Path.Combine("Procedures", "dbo.UP_A"), withSpec: true, withPrd: false);

            var targets = PrdTargetDiscovery.Find(_root);

            Assert.Equal(new[] { "dbo.UP_A", "dbo.UP_B" }, targets.Select(t => t.Label).ToArray());
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdTargetDiscoveryTests`
Expected: 컴파일 실패 — `PrdTargetDiscovery` 없음

- [ ] **Step 3: 발견 로직을 구현한다**

`src/ReSet.Core/Services/PrdTargetDiscovery.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>PRD를 도출할 수 있는 대상 하나 - 명세서가 이미 있는 객체.</summary>
    public sealed record PrdTarget(string Label, string DocsDirectory, bool HasExistingPrd);

    /// <summary>
    /// output/Procedures 아래에서 명세서가 있는 객체를 찾는다.
    ///
    /// Functions·External은 1차 범위 밖이다 - 함수 명세서에서 「업무 요구」를 뽑는 것은
    /// 의미가 얇다. 넓힐 때는 여기 한 곳만 고치면 된다.
    /// </summary>
    public static class PrdTargetDiscovery
    {
        public static IReadOnlyList<PrdTarget> Find(string outputRoot)
        {
            var proceduresRoot = Path.Combine(outputRoot, "Procedures");
            if (!Directory.Exists(proceduresRoot))
            {
                return Array.Empty<PrdTarget>();
            }

            var targets = new List<PrdTarget>();
            foreach (var objectDir in Directory.EnumerateDirectories(proceduresRoot))
            {
                var docs = Path.Combine(objectDir, "docs");
                var specPath = Path.Combine(docs, OutputPathResolver.SpecFileNamePublic);
                if (!File.Exists(specPath))
                {
                    continue;
                }

                targets.Add(new PrdTarget(
                    Path.GetFileName(objectDir),
                    docs,
                    File.Exists(Path.Combine(docs, OutputPathResolver.PrdFileName))));
            }

            return targets.OrderBy(t => t.Label, StringComparer.Ordinal).ToList();
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PrdTargetDiscoveryTests`
Expected: PASS

- [ ] **Step 5: 메뉴 배열을 고친다**

`src/ReSet.Cli/Program.cs`의 `choicesMenu` 배열(약 1168행)을 다음으로 바꾼다:

```csharp
                    var choicesMenu = new[]
                    {
                        "1. 개별 Stored Procedure 역공학 분석 (SP Analysis)",
                        "2. 통합 배치 마이그레이션 설계 (Batch Design)",
                        "3. 마이그레이션 코딩 에이전트 구동 (Code Generation)",
                        "4. 통합 정산 정책 문서 도출 (Policy Extraction)",
                        "5. 명세서 기반 요구사항 도출 (PRD Derivation)",
                        "6. 프로그램 종료 (Exit)"
                    };
```

같은 블록의 종료 분기를 `selectedMenu.StartsWith("5")`에서 **`selectedMenu.StartsWith("6")`**으로 바꾼다.

- [ ] **Step 6: PRD 분기를 더한다**

종료 분기 바로 뒤, `else if (selectedMenu.StartsWith("3"))` 앞에 넣는다:

```csharp
                    else if (selectedMenu.StartsWith("5"))
                    {
                        var targets = PrdTargetDiscovery.Find(outputDir);
                        if (targets.Count == 0)
                        {
                            AnsiConsole.MarkupLine("[yellow]경고: 명세서(Spec.md)가 있는 분석 산출물이 없습니다. 개별 SP 분석을 먼저 수행하세요.[/]");
                            continue;
                        }

                        var picked = AnsiConsole.Prompt(
                            new MultiSelectionPrompt<string>()
                                .Title("[bold green]요구사항 문서를 도출할 대상을 선택하세요[/]")
                                .PageSize(20)
                                .InstructionsText("[grey](스페이스로 선택, 엔터로 확정)[/]")
                                .AddChoices(targets.Select(t =>
                                    t.HasExistingPrd ? $"{t.Label} (기존 Prd.md 있음)" : t.Label)));

                        if (picked.Count == 0)
                        {
                            continue;
                        }

                        var selectedTargets = targets
                            .Where(t => picked.Any(p => p.StartsWith(t.Label, StringComparison.Ordinal)))
                            .ToList();

                        if (selectedTargets.Any(t => t.HasExistingPrd)
                            && !AnsiConsole.Confirm("[yellow]기존 Prd.md가 있는 대상이 포함되어 있습니다. 덮어쓰시겠습니까?[/]", false))
                        {
                            selectedTargets = selectedTargets.Where(t => !t.HasExistingPrd).ToList();
                        }

                        IPrdDerivationService prdService = new PrdDerivationService(aiService);

                        foreach (var target in selectedTargets)
                        {
                            try
                            {
                                PrdDerivationOutcome? outcome = null;
                                await AnsiConsole.Status()
                                    .StartAsync($"{Markup.Escape(target.Label)} 요구사항 문서 도출 중...", async _ =>
                                    {
                                        outcome = await prdService.DeriveAsync(
                                            target.DocsDirectory, target.Label, actorEffort, activeCts.Token);
                                    });

                                if (outcome is null)
                                {
                                    continue;
                                }

                                if (outcome.AttributionClean)
                                {
                                    AnsiConsole.MarkupLine($"[green]완료:[/] {Markup.Escape(outcome.PrdPath)}");
                                }
                                else
                                {
                                    AnsiConsole.MarkupLine(
                                        $"[yellow]완료(귀속 결함 {outcome.Defects.Count}건, 배너 표기):[/] {Markup.Escape(outcome.PrdPath)}");
                                }

                                AnsiConsole.WriteLine();
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                AnsiConsole.MarkupLine(
                                    $"[red]에러: {Markup.Escape(target.Label)} 요구사항 문서 도출 실패:[/] {Markup.Escape(ex.Message)}");
                            }
                        }

                        AnsiConsole.MarkupLine("[yellow]아무 키나 누르면 계속합니다...[/]");
                        Console.ReadKey(true);
                        continue;
                    }
```

> `activeCts`가 이 스코프에 없으면 같은 루프의 다른 분기가 쓰는 취소 토큰 변수명을 그대로 쓴다. 새 `CancellationTokenSource`를 만들지 않는다.

- [ ] **Step 7: 빌드하고 경고를 센다**

Run: `dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"`
Expected: `0`

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core/Services/PrdTargetDiscovery.cs \
        src/ReSet.Cli/Program.cs \
        tests/ReSet.Core.Tests/PrdTargetDiscoveryTests.cs
git commit -m "feat: 명세서 기반 요구사항 도출 메뉴를 배선한다"
```

---

### Task 8: 도입 스윕과 문서 동기화

검사를 만든 것과 검사가 무언가를 잡는 것은 다르다. 합격 기준을 **돌리기 전에** 선언하고, 그 다음에 돌린다.

**Files:**
- Modify: `README.md`, `AGENTS.md`, `docs/architecture.md` (`reset-doc-sync` 스킬 사용)
- Modify: `docs/output-artifacts.md` (산출물 목록에 `Prd.md` 추가)

**Interfaces:**
- Consumes: Task 1~7 전부
- Produces: 없음(검증·문서 태스크)

- [ ] **Step 1: 합격 기준을 먼저 적는다**

작업 로그(또는 커밋 메시지 초안)에 돌리기 **전에** 적는다:

- 전체 게이트: `dotnet test`가 실패 0 · 건너뜀 0, `dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"`가 0
- 결함 주입 회귀(`Validate_ShouldFire_WhenASingleCharacterOfTheQuoteIsAltered`)가 **통과**한다 = 검사가 살아 있다
- 실제 SP 최소 3건에 PRD를 도출했을 때, **발화 0이면 합격이 아니라 의심 신호**다. 그 경우 주입 회귀부터 다시 확인하고, 그래도 0이면 검사가 실제로 무엇을 보고 있는지 손으로 대조한다
- 발화가 나오면 최소 3건을 열어 오탐인지 진짜 결함인지 판정하고 결과를 남긴다

- [ ] **Step 2: 전체 게이트를 돌린다**

```bash
dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"
dotnet test
```
Expected: 경고 수 `0`, 테스트 실패 0 · 건너뜀 0

- [ ] **Step 3: 실제 산출물에 도출을 돌린다**

CLI를 띄워 5번 메뉴에서 `output/Procedures/` 대상 **최소 3건**을 선택해 도출한다. 각 건의 `Prd.md` 상단 배너에서 귀속 결함 수를 기록한다.

- [ ] **Step 4: 발화량을 판정한다**

Step 1에 선언한 기준으로 판정하고 결과를 적는다. 발화가 있으면 최소 3건을 열어 오탐/진짜를 가른다. 오탐이면 원인(정규화 부족·헤딩 변형 등)을 고치고 Task 3의 테스트로 회귀를 남긴다.

- [ ] **Step 5: 문서를 동기화한다**

`reset-doc-sync` 스킬을 호출해 `README.md`·`AGENTS.md`·`docs/architecture.md`를 새 메뉴와 새 클래스에 맞춘다. `AGENTS.md`는 바이트 상한이 걸린 문서이므로 근거 서술은 `docs/architecture.md`에 두고 규칙 줄만 짧게 더한다.

`docs/output-artifacts.md`에 `Prd.md` 항목을 더하고, **L1/L2/L3를 거치지 않으며 귀속 인용의 실재만 기계 확인된 문서**임을 명시한다.

- [ ] **Step 6: 커밋**

```bash
git add README.md AGENTS.md docs/architecture.md docs/output-artifacts.md
git commit -m "docs: 세 문서와 산출물 목록을 PRD 도출까지 동기화한다"
```

---

## 범위 밖 (이 계획이 하지 않는 것)

- `Prd.md`를 배치 계획서·코딩 지시서의 입력으로 배선하기
- 근거 명세서의 검증 상태를 `Spec.md` YAML 머리말에서 읽어 `FormatUnverifiedDocument`의 `sourceOutcome`에 싣기 (지금은 `null` — 배너가 「직접 검토하십시오」로 나온다)
- UDF·외부 DB 객체의 PRD 도출
- 귀속 오배치(인용은 진짜인데 요구와 무관)를 잡는 L2 상호 대조 리뷰
- 여러 SP를 묶은 통합 PRD, 그리고 4번 메뉴 「통합 정산 정책 문서」와의 관계 정리
