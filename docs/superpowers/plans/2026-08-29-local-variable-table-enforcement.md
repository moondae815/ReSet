# 지역 변수 표 강제 이행 계획서

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 명세서의 「지역 변수 표」를 기계 확정 표로 승격해, 검사 D(`CheckSpecLocalVariablesDeclared`)가 모델 재량에 기대지 않게 만든다.

**Architecture:** 원본 DDL의 `DECLARE`를 뽑는 추출기를 새로 만들고, 그것을 (1) 기계 확정 표 카탈로그에 등록해 Critic 면제를 받고, (2) Actor 프롬프트에 완성된 표로 실어 모델이 베끼게 하고, (3) L1이 양방향으로 대조해 되돌림으로 강제한다. 이 저장소가 기계 확정 표 열한 종에 이미 놓아 둔 배선과 같은 모양이다.

**Tech Stack:** C# / .NET · `Microsoft.SqlServer.TransactSql.ScriptDom`(TSql160Parser) · xUnit

**Spec:** `docs/superpowers/specs/2026-08-29-local-variable-table-enforcement-design.md`

## Global Constraints

- **공유 체크아웃이다. 다른 세션이 같은 트리에서 일한다.** 모든 커밋은 `git commit -m "..." -- <경로>` 형태로 경로를 명시한다. 커밋 직전에 `git diff --cached --name-only`로 남의 파일이 안 섞였는지 본다.
- **테스트 게이트는 실패 0 · 건너뜀 0 · 빌드 경고 0이다.** 절대 통과 수는 게이트가 아니다(환경 내에서도 최대 5까지 흔들린다).
- **`--job-name`을 절대 주지 마라** — `output/Jobs/*/agent/steps/`와 `verification/`을 통째로 지운다. **`--sp`는 실제 LLM 재생성이라 돈이 나간다. 이 계획서의 어느 태스크도 둘을 부르지 않는다.**
- **`output.bak-2026-08-22` · `output.bak-cache17-20260827` · `output.bak-axis-b-20260823` · `output.bak-stage4-control-20260828`을 쓰기로 건드리지 마라.** 읽기는 된다.
- **코퍼스 수치는 실제 리더로 재라.** 정규식 근사가 이 저장소에서 세 번 틀렸다.
- **`SpecStatementFactsExtractor`에 넘기는 `FileName`에 `.md`를 붙이지 마라.** 그 리더가 키를 `MechanicalValidator.BareObjectName(fileName)`으로 잡으므로 `"dbo.UP_X.md"`는 맨이름이 `"md"`로 읽혀 14편이 한 키로 뭉갠다. 운영 값은 `Program.cs:879`가 만드는 `"{Schema}.{Name}"`이라 확장자가 없다.
- **표 헤딩 리터럴은 `"### 지역 변수 " + MachineConfirmedTables.HeadingSuffix`다.** 앞부분 `### 지역 변수`가 `SpecStatementFactsExtractor.LocalVariableHeadingPrefixes`의 원소와 `StartsWith`로 일치해야 검사 D가 이 표를 읽는다. **이 문자열을 바꾸면 검사 D가 조용히 꺼진다.**
- **표 헤더는 `| 변수 명칭 | 데이터 타입 | 초기값 |`이다.** `명칭`과 `데이터 타입`은 리더의 `FindColumn`이 찾는 조각이라 바꿀 수 없다.
- 새 `ErrorType` 멤버는 `General` **앞에** 넣는다(그 enum은 `(int)` 캐스트도 숫자 직렬화도 없어 서수 이동이 무해하다 — 파일에 그 주석이 있다).

---

## File Structure

| 파일 | 책임 | 태스크 |
| :--- | :--- | ---: |
| `src/ReSet.Core/Services/LocalVariableDeclarationExtractor.cs` (신) | DDL AST → `DECLARE` 사실. 표 헤딩 리터럴의 단일 출처 | 1 |
| `tests/ReSet.Core.Tests/LocalVariableDeclarationExtractorTests.cs` (신) | 위의 단위 테스트 | 1 |
| `src/ReSet.Core/Services/MachineConfirmedTables.cs` | 카탈로그 등록 | 2 |
| `tests/ReSet.Core.Tests/MachineConfirmedTablesExpansionTests.cs` | 「맨 뒤」 단언 갱신 · 새 표 등록·면제 단언 | 2 |
| `src/ReSet.Core/Services/SpecExpectations.cs` | 사실을 L1 기대값으로 배선(널 체인 항 포함) | 3 |
| `tests/ReSet.Core.Tests/SpecExpectationsLocalVariableTests.cs` (신) | 배선과 널 체인 단언 | 3 |
| `src/ReSet.Core/Services/AiService.cs` | 표 렌더 + 네 번째 presentation 파라미터 | 4 |
| `tests/ReSet.Core.Tests/LocalVariableTablePromptTests.cs` (신) | 다섯 갈래 배선 단언 | 4 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 양방향 L1 검사 + `ErrorType` | 5 |
| `tests/ReSet.Core.Tests/LocalVariableTableL1Tests.cs` (신) | 정·역 방향 단언 | 5 |
| `tests/ReSet.Core.Tests/LocalVariableTableSeamTests.cs` (신) | 리더 이음매 잠금(헤딩 접두사 · 헤더 칸) | 6 |
| `tests/ReSet.Core.Tests/LocalVariableTableCorpusTests.cs` (신) | 코퍼스 31 객체 만족 가능성 | 7 |
| `src/ReSet.Core/Services/CacheManager.cs` | 캐시 17 → 18 | 8 |
| `docs/audit-reports/sweeps/2026-08-29-local-variable-mutations.md` (신) | 변이 검증 기록 | 9 |
| `docs/known-defects.md` · `AGENTS.md` · 로드맵 메모 | 기록 | 10 |

**의존 그래프 (폭 1이 아니다 — 6·7은 5 뒤에서 병렬 가능)**

```
1 → 2 → 3 → 4 → 5 ┬→ 6
                  ├→ 7
                  └→ 8 → 9 → 10
```

1→2: 카탈로그가 `LocalVariableDeclarationExtractor.TableHeading`을 참조한다(컴파일 의존).
2→3: 널 체인 근거가 카탈로그 등록에 달려 있다(설계 의존).
3→4: 렌더러가 `SpecExpectations`가 아니라 추출기를 직접 부르지만, 갈래 배선의 근거가 3의 배선이다.
4→5: L1 검사가 대조하는 것이 4가 렌더한 표다.
5→9: 변이가 1~5의 구현 전부를 겨눈다.

---

## Task 1: `LocalVariableDeclarationExtractor`

**Files:**
- Create: `src/ReSet.Core/Services/LocalVariableDeclarationExtractor.cs`
- Test: `tests/ReSet.Core.Tests/LocalVariableDeclarationExtractorTests.cs`

**Interfaces:**
- Consumes: 없음(이 계획의 첫 태스크).
- Produces:
  - `public sealed record LocalVariableDeclarationFact(string Name, string DataType, string InitialValue)`
  - `public static class LocalVariableDeclarationExtractor`
    - `public const string TableHeading` — 값은 `"### 지역 변수 " + MachineConfirmedTables.HeadingSuffix`
    - `public static IReadOnlyList<LocalVariableDeclarationFact> Extract(string? ddlText)`
  - `InitialValue`는 초기값이 없으면 **빈 문자열**이다(널이 아니다). 표의 칸을 비우는 것이 곧 「초기값 없음」이다.

**왜 이 모양인가 (설계서 §5-1):**
- **`DeclareVariableElement`만 방문한다.** `SpecMaterialCensus.DeclaredVariableVisitor`가 같은 노드를 세어 DDL 사실 69를 냈다. 같은 노드를 쓰면 그 값과 직접 대조할 수 있다(Task 7이 그 대조를 한다).
- **커서는 저절로 빠진다** — `DECLARE c CURSOR FOR ...`는 `DeclareCursorStatement`라 이 노드가 아니다.
- **테이블 변수도 저절로 빠진다** — `DECLARE @t TABLE(...)`은 `DeclareTableVariableStatement`다.
- **프로시저 파라미터도 빠진다** — `ProcedureParameter`는 다른 노드다. 파라미터는 이미 `## 파라미터 목록`의 매개변수 표가 담는다.
- **타입은 `SqlDataTypeOption`이 아니라 토큰 원문으로 낸다.** `VARCHAR(20)`의 길이가 사라지면 안 되고, 이 표의 존재 이유가 타입 충실도이기 때문이다(검사 D의 메시지: 이름이 int를 시사하는 `@v_intCLTotal`이 원본은 `MONEY`).
- **이름으로 중복을 접는다(첫 등장 유지).** census가 `HashSet<string>(OrdinalIgnoreCase)`로 세므로 접지 않으면 두 수가 갈린다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/LocalVariableDeclarationExtractorTests.cs`:

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class LocalVariableDeclarationExtractorTests
    {
        [Fact]
        public void Extract_ShouldReturnNameTypeAndInitialValue()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P @po_intRetVal INT OUTPUT
AS
BEGIN
    DECLARE @v_intCLTotal MONEY = 0
    DECLARE @v_strClientID VARCHAR(20)
END";

            var facts = LocalVariableDeclarationExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Equal("@v_intCLTotal", facts[0].Name);
            Assert.Equal("MONEY", facts[0].DataType);
            Assert.Equal("0", facts[0].InitialValue);
            Assert.Equal("@v_strClientID", facts[1].Name);
            Assert.Equal("VARCHAR(20)", facts[1].DataType);
            Assert.Equal("", facts[1].InitialValue);
        }

        [Fact]
        public void Extract_ShouldNotReturnProcedureParameters()
        {
            // 파라미터는 `## 파라미터 목록`의 매개변수 표가 담는다. 여기 섞이면 같은
            // 사실이 두 표에 실리고 둘이 갈릴 때 어느 쪽이 정본인지 알 수 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P @po_intRetVal INT OUTPUT, @pi_strYMD VARCHAR(8)
AS
BEGIN
    DECLARE @v_only INT
END";

            var names = LocalVariableDeclarationExtractor.Extract(ddl).Select(f => f.Name).ToList();

            Assert.Equal(new[] { "@v_only" }, names);
        }

        [Fact]
        public void Extract_ShouldNotReturnCursorOrTableVariables()
        {
            // 커서는 DeclareCursorStatement, 테이블 변수는 DeclareTableVariableStatement라
            // DeclareVariableElement가 아니다. 이 단언이 SpecMaterialCensus의 DDL 계수와
            // 같은 분모를 유지시킨다 - 갈리면 Task 7의 69 대조가 깨진다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_scalar INT
    DECLARE @v_table TABLE (Col INT)
    DECLARE cur CURSOR FOR SELECT 1
END";

            var names = LocalVariableDeclarationExtractor.Extract(ddl).Select(f => f.Name).ToList();

            Assert.Equal(new[] { "@v_scalar" }, names);
        }

        [Fact]
        public void Extract_ShouldFoldRepeatedNamesKeepingTheFirst()
        {
            // SpecMaterialCensus가 HashSet(OrdinalIgnoreCase)로 세므로 접지 않으면
            // 두 계수가 갈린다. 첫 등장을 남긴다 - 원본에서 먼저 선언된 타입이 정본이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF 1 = 1
        BEGIN DECLARE @v_dup INT END
    ELSE
        BEGIN DECLARE @V_DUP MONEY END
END";

            var facts = LocalVariableDeclarationExtractor.Extract(ddl);

            Assert.Single(facts);
            Assert.Equal("INT", facts[0].DataType);
        }

        [Fact]
        public void Extract_WhenDdlDoesNotParse_ShouldReturnEmpty()
        {
            // 부분 파스 결과가 기계 확정 표에 섞이면 표 전체의 신뢰가 무너진다
            // (SetAssignmentExtractor와 같은 정책).
            var facts = LocalVariableDeclarationExtractor.Extract("CREATE PROCEDURE ((( AS");

            Assert.Empty(facts);
        }

        [Fact]
        public void Extract_WhenDdlIsNullOrBlank_ShouldReturnEmpty()
        {
            Assert.Empty(LocalVariableDeclarationExtractor.Extract(null));
            Assert.Empty(LocalVariableDeclarationExtractor.Extract("   "));
        }

        [Fact]
        public void TableHeading_ShouldUseTheSharedSuffix()
        {
            Assert.Equal(
                "### 지역 변수 " + MachineConfirmedTables.HeadingSuffix,
                LocalVariableDeclarationExtractor.TableHeading);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 돌려 본다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~LocalVariableDeclarationExtractorTests"`
Expected: 빌드 실패 — `LocalVariableDeclarationExtractor`가 존재하지 않는다.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Core/Services/LocalVariableDeclarationExtractor.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <param name="Name">`@`를 포함한 변수 이름 원문.</param>
    /// <param name="DataType">선언 타입의 원문(`VARCHAR(20)`·`MONEY`). SqlDataTypeOption으로
    /// 접지 않는다 - 길이·정밀도가 사라지면 이 표의 존재 이유가 사라진다.</param>
    /// <param name="InitialValue">`DECLARE @x INT = 0`의 `0`. 초기값이 없으면 빈 문자열이다
    /// (널이 아니다) - 표의 빈 칸이 곧 "초기값 없음"이다.</param>
    public sealed record LocalVariableDeclarationFact(string Name, string DataType, string InitialValue);

    /// <summary>
    /// 원본 DDL의 `DECLARE` 지역 변수를 전수 뽑는다.
    ///
    /// [왜 이 추출기가 필요한가 - known-defects (5-3-7)]
    /// 명세서의 「지역 변수 표」는 기계 확정 카탈로그·L1 검사·프롬프트 문구 셋 중
    /// 어느 것도 요구하지 않는 표였다. 모델 교체(gpt-5.6-terra → deepseek-v4-pro-0813)
    /// 만으로 그 표가 코퍼스에서 통째로 사라졌고, 그 표를 재료로 쓰던 검사 D
    /// (CheckSpecLocalVariablesDeclared)가 18 → 0으로 조용히 꺼졌다. 잃은 18건은
    /// 진짜 결함이었다 - FETCH NEXT INTO 대상 변수에 DECLARE가 없어 컴파일 오류가 된다.
    ///
    /// [관할 경계] DeclareVariableElement만 본다.
    ///   - `DECLARE c CURSOR FOR ...`  → DeclareCursorStatement, 안 들어온다.
    ///   - `DECLARE @t TABLE (...)`    → DeclareTableVariableStatement, 안 들어온다.
    ///   - 프로시저 파라미터            → ProcedureParameter, 안 들어온다
    ///     (`## 파라미터 목록`의 매개변수 표가 담는다 - 관할이 겹치면 정본이 갈라진다).
    /// SpecMaterialCensus.DeclaredVariableVisitor가 같은 노드를 세어 DDL 사실 69를
    /// 냈다. 같은 노드를 쓰는 것이 그 값과의 대조를 성립시킨다
    /// (LocalVariableTableCorpusTests가 그 대조를 한다).
    /// </summary>
    public static class LocalVariableDeclarationExtractor
    {
        /// <summary>
        /// [이 문자열을 바꾸면 검사 D가 조용히 꺼진다]
        /// 앞부분 `### 지역 변수`가 SpecStatementFactsExtractor.LocalVariableHeadingPrefixes의
        /// 원소와 StartsWith로 일치해야 그 리더가 이 표를 읽는다.
        /// LocalVariableTableSeamTests가 그 이음매를 잠근다.
        /// </summary>
        public const string TableHeading =
            "### 지역 변수 " + MachineConfirmedTables.HeadingSuffix;

        public static IReadOnlyList<LocalVariableDeclarationFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<LocalVariableDeclarationFact>();

            TSqlFragment? fragment;
            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    // SetAssignmentExtractor.Extract와 같은 정책 - 부분 파스 결과가
                    // 기계 확정 표에 섞이면 표 전체의 신뢰가 무너진다.
                    return Array.Empty<LocalVariableDeclarationFact>();
                }
            }
            catch (Exception)
            {
                return Array.Empty<LocalVariableDeclarationFact>();
            }

            var visitor = new DeclarationVisitor();
            fragment.Accept(visitor);
            return visitor.Facts;
        }

        private sealed class DeclarationVisitor : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

            public List<LocalVariableDeclarationFact> Facts { get; } = new();

            public override void Visit(DeclareVariableElement node)
            {
                var name = node.VariableName?.Value;
                if (string.IsNullOrWhiteSpace(name)) return;

                // 이름으로 접는다(첫 등장 유지) - SpecMaterialCensus가 HashSet으로
                // 세므로 접지 않으면 두 계수가 갈린다. IF/ELSE 두 갈래가 같은 이름을
                // 선언하면 원본에서 먼저 나온 타입이 정본이다.
                if (!_seen.Add(name!)) return;

                Facts.Add(new LocalVariableDeclarationFact(
                    name!, TextOf(node.DataType), TextOf(node.Value)));
            }
        }

        /// <summary>
        /// 원문 토큰을 그대로 이어 붙인 뒤 개행만 접는다.
        ///
        /// [자기 사본을 쓰는 이유] SetAssignmentExtractor.TextOf와 같다 -
        /// DmlScopeExtractor.TextOf가 private이라 부를 수 없고, 자기 사본을 두는 것이
        /// 이 코드베이스의 관례다(DerivedTableColumnExtractor.cs:165 선례).
        ///
        /// [왜 개행만 접는가] AiService가 이 값을 렌더할 때 MarkdownTableCellCodec.Escape를
        /// 거치는데 Escape는 개행만 공백으로 바꾼다. MechanicalValidator는 모델이 그
        /// 렌더된 값을 베낀 텍스트를 접히지 않은 원본 fact와 대조하므로, fact에 개행이
        /// 남으면 어떤 산출물도 만족시킬 수 없는 요구가 된다.
        /// </summary>
        private static string TextOf(TSqlFragment? fragment)
        {
            if (fragment == null || fragment.ScriptTokenStream == null) return string.Empty;

            var stream = fragment.ScriptTokenStream;
            var first = fragment.FirstTokenIndex;
            var last = fragment.LastTokenIndex;
            if (first < 0 || last < first || last >= stream.Count) return string.Empty;

            var sb = new StringBuilder();
            for (var i = first; i <= last; i++)
            {
                sb.Append(stream[i].Text);
            }

            return MarkdownTableCellCodec.CollapseNewlines(sb.ToString().Trim());
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 돌려 본다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~LocalVariableDeclarationExtractorTests"`
Expected: 7 passed · 0 failed · 0 skipped

**여기서 빨간불이 나면 `DataType`·`InitialValue`의 공백 처리부터 의심하라.** `TextOf`가 토큰을 그대로 잇기 때문에 `VARCHAR ( 20 )`처럼 나올 수 있다. 그때는 **테스트의 기대값을 실제 산출로 고치지 말고**, 실제 산출을 보고서에 적은 뒤 `Trim` 범위를 넓힐지 판단하라 — 이 값은 L1이 모델의 사본과 문자 단위로 대조하는 값이므로 모양이 곧 계약이다.

- [ ] **Step 5: 전체 게이트를 돌린다**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0 · 빌드 경고 0

- [ ] **Step 6: 커밋**

```bash
git diff --cached --name-only   # 비어 있어야 한다
git add src/ReSet.Core/Services/LocalVariableDeclarationExtractor.cs \
        tests/ReSet.Core.Tests/LocalVariableDeclarationExtractorTests.cs
git diff --cached --name-only   # 위 둘만 나와야 한다
git commit -m "feat: 원본 DDL의 DECLARE 지역 변수를 뽑는 추출기를 더한다" -- \
  src/ReSet.Core/Services/LocalVariableDeclarationExtractor.cs \
  tests/ReSet.Core.Tests/LocalVariableDeclarationExtractorTests.cs
```

---

## Task 2: 기계 확정 표 카탈로그 등록

**Files:**
- Modify: `src/ReSet.Core/Services/MachineConfirmedTables.cs`
- Modify: `tests/ReSet.Core.Tests/MachineConfirmedTablesExpansionTests.cs:57-62`

**Interfaces:**
- Consumes: `LocalVariableDeclarationExtractor.TableHeading` (Task 1)
- Produces: `MachineConfirmedTables.All`의 마지막 원소가 지역 변수 표가 된다. `CriticExemptionBlock`의 전사 표 목록에 `### 지역 변수 (기계 확정 — 수정 금지)`가 실린다.

**⚠ 기존 테스트 하나가 반드시 빨개진다.** `MachineConfirmedTablesExpansionTests.All_ShouldContainErrorCodeTableAtTheEnd`(:57)가 `All[^1]`이 오류 코드 표임을 못박는다. **그 단언의 취지는 「기존 항목 사이에 끼우지 마라」이지 「오류 코드 표가 영원히 마지막」이 아니다** — 같은 파일의 `All_ShouldAppendNewTablesAtTheEnd`(:27)가 참조 함수 표 인덱스를 피벗으로 그 취지를 따로 적는다. 마지막 이름을 새 표로 바꾼다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MachineConfirmedTablesExpansionTests.cs`에 더한다:

```csharp
        [Fact]
        public void All_ShouldContainTheLocalVariableTable()
        {
            var headings = MachineConfirmedTables.All.Select(t => t.Heading).ToList();

            Assert.Contains(LocalVariableDeclarationExtractor.TableHeading, headings);
        }

        [Fact]
        public void All_ShouldAppendTheLocalVariableTableAfterTheErrorCodeTable()
        {
            // 순서가 곧 Critic 프롬프트에 실리는 순서다. 기존 항목 사이에 끼우면
            // 그 뒤 항목들의 바이트가 통째로 밀린다.
            var headings = MachineConfirmedTables.All.Select(t => t.Heading).ToList();

            Assert.True(
                headings.IndexOf(LocalVariableDeclarationExtractor.TableHeading)
                    > headings.IndexOf(DmlScopeExtractor.ErrorCodeTableHeading),
                "새 표는 기존 마지막 항목 뒤에 와야 한다");
        }

        [Fact]
        public void CriticExemptionBlock_ShouldCoverTheLocalVariableTable()
        {
            // All에 넣으면 Critic 면제가 자동으로 따라온다. 이것이 없으면 Critic이
            // 새 표를 환각으로 오판하고 L1은 반대로 전사를 요구해 교착이 된다.
            Assert.Contains("지역 변수", MachineConfirmedTables.CriticExemptionBlock);
        }
```

그리고 **기존 `All_ShouldContainErrorCodeTableAtTheEnd`(:57-62)를 이렇게 바꾼다**:

```csharp
        [Fact]
        public void All_ShouldContainTheLocalVariableTableAtTheEnd()
        {
            // [2026-08-29 갱신] 이 단언의 취지는 "끼우지 마라"이지 "오류 코드 표가
            // 영원히 마지막"이 아니다. 지역 변수 표가 새 마지막이 됐다(known-defects
            // (5-3-7)의 강제). 다음에 표를 더하는 사람은 여기 이름을 자기 표로 바꾸고,
            // 그 행위가 "맨 뒤에 붙였다"의 증거가 된다.
            var last = MachineConfirmedTables.All[^1];

            Assert.Equal(LocalVariableDeclarationExtractor.TableHeading, last.Heading);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 돌려 본다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MachineConfirmedTablesExpansionTests"`
Expected: FAIL — 새 단언 넷이 전부 실패한다(카탈로그에 아직 없다).

- [ ] **Step 3: 카탈로그에 등록한다**

`src/ReSet.Core/Services/MachineConfirmedTables.cs`의 `All` 배열 **맨 끝**, 오류 코드 표 항목 뒤에 더한다:

```csharp
            new MachineConfirmedTable(
                DmlScopeExtractor.ErrorCodeTableHeading,
                MachineConfirmedTableVerification.DdlTranscription),
            // DDL 본문의 DECLARE를 그대로 옮긴 전사 표다 - 변수명·타입·초기값 셋 다
            // 원문에 있다. known-defects (5-3-7)의 강제: 이 표가 카탈로그 밖에 있는
            // 동안 그 존재가 모델 재량이었고, 모델 교체만으로 코퍼스에서 통째로
            // 사라져 검사 D가 18 → 0으로 꺼졌다.
            new MachineConfirmedTable(
                LocalVariableDeclarationExtractor.TableHeading,
                MachineConfirmedTableVerification.DdlTranscription)
```

- [ ] **Step 4: 테스트가 통과하는지 돌려 본다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MachineConfirmedTables"`
Expected: 모두 통과. **`CriticExemptionBlock_UsesLineFeedsOnly`와 `EveryRegisteredHeadingEndsWithTheSharedSuffix`(`MachineConfirmedTablesTests.cs:191,198`)도 함께 초록이어야 한다** — 앞엣것은 `\r` 금지, 뒤엣것은 `HeadingSuffix` 강제다.

- [ ] **Step 5: 전체 게이트**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0 · 빌드 경고 0

- [ ] **Step 6: 커밋**

```bash
git commit -m "feat: 지역 변수 표를 기계 확정 카탈로그에 등록한다" -- \
  src/ReSet.Core/Services/MachineConfirmedTables.cs \
  tests/ReSet.Core.Tests/MachineConfirmedTablesExpansionTests.cs
```

---

## Task 3: `SpecExpectations`에 사실 배선

**Files:**
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs` (추출 호출부 · 널 체인 · 객체 초기화)
- Test: `tests/ReSet.Core.Tests/SpecExpectationsLocalVariableTests.cs` (신)

**Interfaces:**
- Consumes: `LocalVariableDeclarationExtractor.Extract(string?)` (Task 1)
- Produces: `SpecExpectations.LocalVariableDeclarations` — `IReadOnlyList<LocalVariableDeclarationFact>`, 기본값 `Array.Empty<...>()`. Task 5의 L1 검사가 이 속성을 읽는다.

**널 체인 항을 반드시 잇는다.** `SpecExpectations.From`은 모든 재료가 비면 `null`을 돌려준다. 지역 변수만 가진 객체(예: 본문 DML이 없는 스칼라 함수)에서 항을 안 이으면 **`From`이 널을 내고 새 검사가 한 번도 안 돈다** — 같은 파일의 `objectDeclaration == null` 항 주석이 그 실패 양식을 실측으로 적어 두었다(authoring-contract §1).

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SpecExpectationsLocalVariableTests.cs`:

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SpecExpectationsLocalVariableTests
    {
        private static SpDefinition Def(string ddl) => new()
        {
            ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            DdlText = ddl
        };

        [Fact]
        public void From_ShouldCarryLocalVariableDeclarations()
        {
            var expectations = SpecExpectations.From(Def(@"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intCLTotal MONEY = 0
    UPDATE T SET C = 1 WHERE K = 2
END"));

            Assert.NotNull(expectations);
            Assert.Contains(
                expectations!.LocalVariableDeclarations,
                f => f.Name == "@v_intCLTotal" && f.DataType == "MONEY");
        }

        [Fact]
        public void From_WhenLocalVariablesAreTheOnlyMaterial_ShouldNotReturnNull()
        {
            // 널 체인 항을 안 이으면 여기서 null이 나오고 새 L1 검사가 한 번도 안 돈다.
            // 같은 파일의 objectDeclaration 항 주석이 그 실패 양식을 실측으로 적었다.
            //
            // [이 DDL이 정말 다른 재료를 안 만드는지] 본문에 DML 문장이 없고 WITH 절도
            // 없다. 만약 다른 항이 함께 채워지면 이 테스트는 공허한 참이 되므로,
            // Step 4에서 널 체인의 지역 변수 항을 지워 실제로 빨개지는지 확인한다.
            var expectations = SpecExpectations.From(Def(@"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_only INT
END"));

            Assert.NotNull(expectations);
            Assert.Single(expectations!.LocalVariableDeclarations);
        }

        [Fact]
        public void LocalVariableDeclarations_ShouldDefaultToEmptyNotNull()
        {
            var expectations = SpecExpectations.From(Def(@"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE T SET C = 1 WHERE K = 2
END"));

            Assert.NotNull(expectations);
            Assert.Empty(expectations!.LocalVariableDeclarations);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 돌려 본다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~SpecExpectationsLocalVariableTests"`
Expected: 빌드 실패 — `LocalVariableDeclarations` 속성이 없다.

- [ ] **Step 3: 배선한다 — 세 자리**

**(가) 속성 선언** — `SpecExpectations.cs`의 `SetAssignments` 속성(`:127` 부근) 뒤에 더한다:

```csharp
        /// <summary>
        /// 원본 DDL의 DECLARE 지역 변수. 「지역 변수 (기계 확정 — 수정 금지)」 표의
        /// 기대값이다.
        ///
        /// [왜 이 재료가 L1 기대값이 되어야 하는가 - known-defects (5-3-7)]
        /// 이 표는 세 층(카탈로그·L1·프롬프트) 어디서도 요구되지 않아 존재 자체가
        /// 모델 재량이었고, 모델 교체만으로 코퍼스 14 프로시저 전량에서 사라졌다.
        /// 그 표를 재료로 쓰는 CheckSpecLocalVariablesDeclared(검사 D)가 18 → 0으로
        /// 조용히 꺼졌고, 잃은 18건은 진짜 결함이었다.
        /// </summary>
        public IReadOnlyList<LocalVariableDeclarationFact> LocalVariableDeclarations { get; init; } =
            Array.Empty<LocalVariableDeclarationFact>();
```

**(나) 추출 호출부** — `var setAssignments = SetAssignmentExtractor.Extract(spDef.DdlText);` 바로 아래:

```csharp
            var localVariableDeclarations = LocalVariableDeclarationExtractor.Extract(spDef.DdlText);
```

**(다) 널 체인 항** — `&& parameterColumnBindings.Count == 0)` **앞에** 더한다:

```csharp
                // localVariableDeclarations는 중복항이 아니다 - 본문에 DML이 하나도 없고
                // WITH 절도 없는 객체(계산만 하는 스칼라 함수 등)가 DECLARE만 가질 수
                // 있다. 이 항을 빠뜨리면 그 객체에서 From이 null을 돌려주고
                // CheckLocalVariableDeclarationTable이 한 번도 돌지 않는다 - 위
                // objectDeclaration 항이 실측으로 적은 것과 같은 실패 양식이고,
                // 이 계획이 닫으려는 (5-3-7)이 정확히 "검사가 재료를 잃어 조용히 꺼지는"
                // 그 모양이다(authoring-contract §1).
                && localVariableDeclarations.Count == 0
```

**(라) 객체 초기화** — `SetAssignments = setAssignments,` 뒤:

```csharp
                LocalVariableDeclarations = localVariableDeclarations,
```

- [ ] **Step 4: 통과 확인 + 널 체인 항이 공허하지 않은지 실증한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~SpecExpectationsLocalVariableTests"`
Expected: 3 passed

그다음 **(다)의 한 줄을 잠시 지우고** 다시 돌린다.
Expected: `From_WhenLocalVariablesAreTheOnlyMaterial_ShouldNotReturnNull`이 **FAIL**.
확인했으면 `git checkout -- src/ReSet.Core/Services/SpecExpectations.cs`로 되돌리고 (다)~(라)를 다시 넣는다.

**되돌릴 때 `mv`를 쓰지 마라 — `git checkout -- <경로>`를 쓴다.** 직전 회차에서 변이 검증 뒤 `dotnet test`가 낡은 DLL을 조용히 재사용한 사고가 있었다.

- [ ] **Step 5: 전체 게이트**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0 · 빌드 경고 0

- [ ] **Step 6: 커밋**

```bash
git commit -m "feat: 지역 변수 선언 사실을 SpecExpectations에 배선한다" -- \
  src/ReSet.Core/Services/SpecExpectations.cs \
  tests/ReSet.Core.Tests/SpecExpectationsLocalVariableTests.cs
```

---

## Task 4: 프롬프트 렌더 + 네 번째 presentation 파라미터

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs` — `BuildMachineFactBlockLines`(`:1400`) · 호출부 다섯(`:469` · `:1920` · `:3207` · `:3347` · `:3521`) · 새 렌더러
- Test: `tests/ReSet.Core.Tests/LocalVariableTablePromptTests.cs` (신)

**Interfaces:**
- Consumes: `LocalVariableDeclarationExtractor.Extract` · `.TableHeading` (Task 1)
- Produces: `BuildMachineFactBlockLines(SpDefinition, MachineFactPresentation executionSemanticsPresentation, MachineFactPresentation caseBranchPresentation, MachineFactPresentation uncoveredNoticePresentation, MachineFactPresentation localVariablePresentation)` — **다섯째 인자가 새로 붙는다. 기본값을 두지 않는다**(기본값을 두면 갈래 하나를 빠뜨려도 컴파일이 통과해, 이 파일의 설계 D5 주석이 막으려는 바로 그 회귀가 조용히 산다).

**갈래별 값 (설계서 §5-3, 근거는 표의 거처 실측):**

| 호출부 | 갈래 | 값 |
| :--- | :--- | :--- |
| `:469` | SP 전체 | `Table` |
| `:1920` | 함수 | `Table` |
| `:3207` | `OverviewAndParameters` | `Table` |
| `:3347` | `CrudAnalysis` | `Omit` |
| `:3521` | `LogicAndVisualization` | `Omit` |

**★ 이 표가 (5-3-7)의 마지막 실패 모드를 닫는다.** 인접 축(`reset-a4`)이 실측했다 — 현 코퍼스의 `EXCEPTION_PROC`은 **표를 썼는데 헤딩을 안 붙였고**(`Spec.md:87-92`, `## 파라미터 목록` 아래 산문 뒤에 표만 놓임) 리더가 헤딩으로만 구간을 잡으므로 0을 낸다. **렌더러가 헤딩 리터럴을 프롬프트에 함께 실으므로 그 실패 모드가 구조적으로 사라진다** — 모델이 헤딩을 지어낼 자리가 없다. 선례 `BuildSetAssignmentTableLines`(`AiService.cs:1292`)가 정확히 그렇게 한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/LocalVariableTablePromptTests.cs`:

**호출 관례는 `FeedbackSpecPromptTests`의 것을 그대로 쓴다** — `AiService`에 프롬프트용 테스트 진입점이 없고 `InternalsVisibleTo`도 저장소 어디에도 없다(전수 확인). 대신 `NSubstitute`로 `IAiClient`를 가짜로 두고 공개 비동기 메서드를 부르면 `AiResult.SystemPrompt`·`UserPrompt`에 조립된 프롬프트가 실려 돌아온다.

```csharp
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 지역 변수 표가 어느 프롬프트 갈래에 실리는지 잠근다.
    ///
    /// [왜 갈래를 잠그는가 - AiService의 Task 14/17 실측]
    /// 자기가 쓸 수 없는 H2에 표를 넣으라는 지시를 받은 모델은 둘 중 하나를 한다 -
    /// H2 제약을 어기고 헤딩을 합성하거나(같은 ### 가 두 번 생기고 LocateHeadingSection이
    /// 첫 일치만 보므로 뒤 사본이 조용히 사라진다), 표를 통째로 버린다.
    /// 이 표의 거처는 `## 파라미터 목록`이고 그것을 쓰는 갈래는 OverviewAndParameters다.
    ///
    /// [왜 System과 User를 이어 붙여 보는가] 이 표가 두 프롬프트 중 어느 쪽에 실리는지는
    /// 이 테스트의 관심사가 아니다 - 관심사는 "그 갈래의 모델이 이 표를 보는가"다.
    /// 한쪽만 단언하면 조립 자리가 바뀔 때 내용이 그대로인데도 빨개진다.
    /// </summary>
    public class LocalVariableTablePromptTests
    {
        private const string Ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intCLTotal MONEY = 0
    UPDATE T SET C = 1 WHERE K = 2
END";

        private static SpDefinition Def() => new()
        {
            ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            DdlText = Ddl
        };

        private static IAiService Service()
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-test");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "ok" });

            return new AiService(client, 0.2f);
        }

        private static string Both(AiResult result) =>
            (result.SystemPrompt ?? "") + "\n" + (result.UserPrompt ?? "");

        [Fact]
        public async Task WholeSpBranch_ShouldCarryTheTableWithItsHeadingAndRows()
        {
            var result = await Service().GenerateSpecificationAsync(Def(), "");
            var prompt = Both(result);

            Assert.Contains(LocalVariableDeclarationExtractor.TableHeading, prompt);
            Assert.Contains("| 변수 명칭 | 데이터 타입 | 초기값 |", prompt);
            Assert.Contains("@v_intCLTotal", prompt);
            Assert.Contains("MONEY", prompt);
        }

        [Fact]
        public async Task OverviewAndParametersBranch_ShouldCarryTheTable()
        {
            // 이 갈래가 `## 파라미터 목록`을 쓴다 - 표의 거처다.
            var result = await Service().GenerateSpecSectionAsync(Def(), "OverviewAndParameters", "");

            Assert.Contains(LocalVariableDeclarationExtractor.TableHeading, Both(result));
        }

        [Theory]
        [InlineData("CrudAnalysis")]
        [InlineData("LogicAndVisualization")]
        public async Task BranchesThatCannotWriteParameterList_ShouldNotCarryTheTable(string sectionType)
        {
            var result = await Service().GenerateSpecSectionAsync(Def(), sectionType, "");
            var prompt = Both(result);

            Assert.DoesNotContain(LocalVariableDeclarationExtractor.TableHeading, prompt);
            // 참고 재료 형태로도 새지 않아야 한다 - Omit은 아무것도 안 싣는다는 뜻이다.
            Assert.DoesNotContain("@v_intCLTotal", prompt);
        }
    }
}
```

**`sectionType` 문자열 셋은 실측으로 확정했다** — `BuildSpecSectionPrompts`가 `sectionType == "OverviewAndParameters"` · `"CrudAnalysis"` · `"LogicAndVisualization"`으로 분기한다(`AiService.cs:3108` 이후). 그 문자열이 갈래의 이름이다.

**`Service()`의 반환 타입이 `IAiService`인지 `AiService`인지는 기존 테스트를 따른다** — `FeedbackSpecPromptTests.Build()`가 `(IAiService, IAiClient)`를 낸다.

- [ ] **Step 2: 테스트가 실패하는지 돌려 본다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~LocalVariableTablePromptTests"`
Expected: FAIL — 표가 프롬프트에 없다.

- [ ] **Step 3: 렌더러를 더한다**

`AiService.cs`의 `BuildSetAssignmentTableLines` 바로 뒤에:

```csharp
        /// <summary>
        /// 「지역 변수」 표를 렌더한다. 헤딩 리터럴을 함께 실어 모델이 헤딩을 지어낼
        /// 자리를 없앤다.
        ///
        /// [왜 헤딩까지 싣는가 - 실측] 현 코퍼스의 EXCEPTION_PROC은 지역 변수 표를
        /// 실제로 썼는데 전용 헤딩을 안 붙였다(Spec.md:87-92, `## 파라미터 목록` 아래
        /// 산문 뒤에 표만). SpecStatementFactsExtractor.ReadLocalVariables는 헤딩으로만
        /// 구간을 잡으므로 그 표를 못 읽고 0을 낸다 - known-defects (5-3-7)의 소실
        /// 14건 중 1건이 그 원인이다. 헤딩을 프롬프트가 주면 그 실패 모드가 없다.
        ///
        /// [목적지가 `## 파라미터 목록`인 근거] 두 세대 실측 - 현 코퍼스의 유일한
        /// 잔존(UF_GET_OUTYMD4REFUND)도, 승격 전 스냅샷(output.bak-cache17-20260827)의
        /// 둘도 전부 그 절 아래에 있었다.
        /// </summary>
        private static List<string> BuildLocalVariableTableLines(
            IReadOnlyList<LocalVariableDeclarationFact> facts)
        {
            var lines = new List<string>
            {
                "   [CRITICAL LOCAL VARIABLE TABLE] The following DECLARE'd local variables are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## 파라미터 목록` under the exact heading shown. Never rename a variable, never change or abbreviate a declared type, and never add a row for a procedure parameter - the declared type is the contract, and an implementer who guesses a type from the variable name will truncate money values.",
                $"   {LocalVariableDeclarationExtractor.TableHeading}",
                "   | 변수 명칭 | 데이터 타입 | 초기값 |",
                "   | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                lines.Add(
                    $"   | {EscapeTableCell(fact.Name)} | {EscapeTableCell(fact.DataType)} | {EscapeTableCell(fact.InitialValue)} |");
            }

            lines.Add("");
            return lines;
        }
```

- [ ] **Step 4: `BuildMachineFactBlockLines`에 다섯째 인자를 더한다**

시그니처(`:1400`)를 바꾼다:

```csharp
        private static List<string> BuildMachineFactBlockLines(
            SpDefinition spDef,
            MachineFactPresentation executionSemanticsPresentation,
            MachineFactPresentation caseBranchPresentation,
            MachineFactPresentation uncoveredNoticePresentation,
            MachineFactPresentation localVariablePresentation)
```

본문에서 `BuildUncoveredStatementNoticeLines` 호출 **앞에** 더한다:

```csharp
            // [지역 변수 표 - known-defects (5-3-7)의 강제, 2026-08-29]
            // caseBranchPresentation을 재사용하지 않는다 - 그 셋(CASE 분기·트랜잭션
            // 경계·변수 대입)의 목적지는 `## 로직 흐름 요약`인데 이 표의 목적지는
            // `## 파라미터 목록`이라 갈래별 값이 다르다. 자기 파라미터를 갖는 이유가
            // 그것이다.
            //
            // Reference 변형을 만들지 않는다 - 이 표를 못 쓰는 두 갈래(CrudAnalysis·
            // LogicAndVisualization)는 변수 선언 목록을 산문으로 서술할 자리도 없다.
            // 그래서 Table이 아니면 아무것도 싣지 않는다(Reference == Omit).
            if (localVariablePresentation == MachineFactPresentation.Table)
            {
                var localVariables = LocalVariableDeclarationExtractor.Extract(spDef.DdlText);
                if (localVariables.Count > 0)
                {
                    lines.AddRange(BuildLocalVariableTableLines(localVariables));
                }
            }
```

- [ ] **Step 5: 호출부 다섯을 고친다**

각 자리에 `localVariablePresentation:` 명명 인자를 더한다. **명명 인자를 쓴다** — 같은 타입의 인자가 넷이라 위치로 넘기면 조용히 뒤바뀐다.

```csharp
// :469  SP 전체
                uncoveredNoticePresentation: MachineFactPresentation.Table,
                localVariablePresentation: MachineFactPresentation.Table));

// :1920 함수
                uncoveredNoticePresentation: MachineFactPresentation.Table,
                localVariablePresentation: MachineFactPresentation.Table);

// :3207 OverviewAndParameters — 이 갈래가 `## 파라미터 목록`을 쓴다
                uncoveredNoticePresentation: MachineFactPresentation.Reference,
                localVariablePresentation: MachineFactPresentation.Table));

// :3347 CrudAnalysis — `## CRUD 분석` 하나만 쓴다
                uncoveredNoticePresentation: MachineFactPresentation.Table,
                localVariablePresentation: MachineFactPresentation.Omit));

// :3521 LogicAndVisualization — `## 로직 흐름 요약`·`## 비즈니스 흐름 시각화`만 쓴다
                uncoveredNoticePresentation: MachineFactPresentation.Reference,
                localVariablePresentation: MachineFactPresentation.Omit));
```

- [ ] **Step 6: 테스트가 통과하는지 돌려 본다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~LocalVariableTablePromptTests"`
Expected: 4 passed

- [ ] **Step 7: 전체 게이트**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0 · 빌드 경고 0

**프롬프트 바이트를 단언하는 기존 테스트가 빨개질 수 있다.** 빨개지면 그 테스트가 「바이트가 이 값이다」를 못박는지 「이 조각을 담는다」를 못박는지 먼저 읽어라. 전자면 기대값 갱신이 맞고(캐시 18 승격이 Task 8에서 그 변화를 정당화한다), 후자면 **내 변경이 그 조각을 밀어냈다는 뜻이므로 기대값이 아니라 내 코드를 의심하라.**

- [ ] **Step 8: 커밋**

```bash
git commit -m "feat: 지역 변수 표를 Actor 프롬프트의 세 갈래에 싣는다" -- \
  src/ReSet.Core/Services/AiService.cs \
  tests/ReSet.Core.Tests/LocalVariableTablePromptTests.cs
```

---

## Task 5: 양방향 L1 검사

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` — `ErrorType` enum(`General` 앞) · `Validate`의 검사 목록(`CheckSetAssignments` 호출 근처, `:201`) · 새 검사 본체(`CheckSetAssignments` 뒤, `:4935` 부근)
- Test: `tests/ReSet.Core.Tests/LocalVariableTableL1Tests.cs` (신)

**Interfaces:**
- Consumes: `SpecExpectations.LocalVariableDeclarations` (Task 3) · `LocalVariableDeclarationExtractor.TableHeading` (Task 1)
- Produces: `ErrorType.LocalVariableTableMismatch` · `private static void CheckLocalVariableDeclarationTable(string markdown, SpecExpectations expectations, ValidationResult result)`

**양방향인 이유 (설계서 §5-5).** 전사 표이므로 **사실 없는 행은 그 자체로 위반**이다(모델이 지어낸 변수). (5-3-6)이 기존 셋의 단방향을 결함으로 적었는데 새 검사에서 되풀이할 이유가 없다. **단 이것이 (5-3-6)을 닫지 않는다** — 기존 셋의 역방향은 재생성이 함께 필요해 이 회차 범위 밖이다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/LocalVariableTableL1Tests.cs`:

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class LocalVariableTableL1Tests
    {
        private const string Ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intCLTotal MONEY = 0
    DECLARE @v_strClientID VARCHAR(20)
    UPDATE T SET C = 1 WHERE K = 2
END";

        // Validate는 인스턴스 메서드다 - `public ValidationResult Validate(string, SpecExpectations?)`
        // (MechanicalValidator.cs:154). 생성자는 `MechanicalValidator(bool useMermaidCli = false)`라
        // 인자가 필요 없다. 기존 테스트도 `new MechanicalValidator()`를 쓴다
        // (VerificationPipelineOrchestratorTests.cs:38).
        private static ValidationResult Validate(string markdown, SpecExpectations expectations) =>
            new MechanicalValidator().Validate(markdown, expectations);

        private static SpecExpectations Expectations() =>
            SpecExpectations.From(new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                DdlText = Ddl
            })!;

        private static string DocWithTable(string rows) =>
            "## 파라미터 목록\n\n"
            + LocalVariableDeclarationExtractor.TableHeading + "\n"
            + "| 변수 명칭 | 데이터 타입 | 초기값 |\n"
            + "| :--- | :--- | :--- |\n"
            + rows
            + "\n### 다음 절\n";

        private const string CompleteRows =
            "| @v_intCLTotal | MONEY | 0 |\n| @v_strClientID | VARCHAR(20) |  |\n";

        [Fact]
        public void WhenTableIsCompletelyTranscribed_ShouldNotReport()
        {
            var result = Validate(DocWithTable(CompleteRows), Expectations());

            Assert.DoesNotContain(result.Errors, e => e.Contains("지역 변수"));
        }

        [Fact]
        public void WhenTheHeadingIsMissing_ShouldReportOnce()
        {
            var result = Validate("## 파라미터 목록\n\n본문뿐입니다.\n", Expectations());

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.LocalVariableTableMismatch);
            Assert.Contains(result.Errors, e => e.Contains(LocalVariableDeclarationExtractor.TableHeading));
        }

        [Fact]
        public void WhenARowIsMissing_ShouldReportThatVariable()
        {
            var result = Validate(
                DocWithTable("| @v_intCLTotal | MONEY | 0 |\n"), Expectations());

            Assert.Contains(result.Errors, e => e.Contains("@v_strClientID"));
        }

        [Fact]
        public void WhenADeclaredTypeIsChanged_ShouldReportThatVariable()
        {
            // 이 검사가 존재하는 이유다 - 이름이 int를 시사하는 MONEY 변수를 모델이
            // INT로 적으면 이행자가 그대로 선언해 금액이 절삭된다.
            var result = Validate(
                DocWithTable("| @v_intCLTotal | INT | 0 |\n| @v_strClientID | VARCHAR(20) |  |\n"),
                Expectations());

            Assert.Contains(result.Errors, e => e.Contains("@v_intCLTotal") && e.Contains("MONEY"));
        }

        [Fact]
        public void WhenTheTableHasAnInventedRow_ShouldReportIt()
        {
            // 역방향. 전사 표이므로 사실 없는 행은 그 자체로 위반이다.
            var result = Validate(
                DocWithTable(CompleteRows + "| @v_invented | INT | 0 |\n"), Expectations());

            Assert.Contains(result.Errors, e => e.Contains("@v_invented"));
        }

        [Fact]
        public void WhenThereAreNoDeclarations_ShouldStaySilent()
        {
            var expectations = SpecExpectations.From(new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "Q", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "Q",
                DdlText = "CREATE PROCEDURE dbo.Q AS BEGIN UPDATE T SET C = 1 WHERE K = 2 END"
            })!;

            var result = Validate("## 파라미터 목록\n\n본문뿐입니다.\n", expectations);

            Assert.DoesNotContain(result.Errors, e => e.Contains("지역 변수"));
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 돌려 본다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~LocalVariableTableL1Tests"`
Expected: 빌드 실패 — `ErrorType.LocalVariableTableMismatch`가 없다.

- [ ] **Step 3: `ErrorType` 멤버를 더한다**

`General` **앞에** 넣는다:

```csharp
        // 지역 변수 표(기계 확정)의 전사 대조 앵커. known-defects (5-3-7) - 이 표는
        // 강제가 없어 모델 교체만으로 사라졌고 검사 D가 조용히 꺼졌다. 위 항목들과
        // 같은 이유로 서수 이동은 기능에 영향이 없다.
        LocalVariableTableMismatch,
        General
```

- [ ] **Step 4: 검사 본체를 쓴다**

`CheckSetAssignments` 뒤에 더한다:

```csharp
        /// <summary>
        /// 기계 확정 지역 변수 표의 전사를 양방향으로 대조한다.
        ///
        /// [왜 양방향인가 - (5-3-6)을 되풀이하지 않는다]
        /// CheckErrorCodes·CheckSetAssignments·CheckTransactionBoundaries 셋은
        /// `foreach (var fact in expectations.X)`로만 돌아 "모든 사실에 행이 있는가"만
        /// 본다. 모델이 표에 행을 더해도 통과하고, 그 가짜 행이 앵커 해결을 망가뜨릴 수
        /// 있다((5-3-6)). 전사 표에서 사실 없는 행은 그 자체로 위반이므로 새 검사는
        /// 처음부터 양방향으로 둔다. <b>이것이 기존 셋의 역방향을 닫지는 않는다</b> -
        /// 그쪽은 넣는 순간 실제 위반이 발화해 재생성이 함께 필요하다.
        ///
        /// [자기 try/catch를 두는 이유] Validate의 catch-all은 검사 하나가 던지면
        /// Errors를 통째로 지우고 통과시킨다. 새 검사의 실패가 기존 검사의 판정까지
        /// 삼키면 안 된다(CheckMachineTableShape와 같은 근거).
        /// </summary>
        private static void CheckLocalVariableDeclarationTable(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.LocalVariableDeclarations.Count == 0) return;

            try
            {
                var lines = MarkdownSectionLocator.SplitLines(markdown);
                var (headingIndex, endIndex) = LocateHeadingSection(
                    lines, LocalVariableDeclarationExtractor.TableHeading);

                if (headingIndex < 0)
                {
                    var missing =
                        $"기계 확정 지역 변수 표가 명세서에 없습니다. "
                        + $"`{LocalVariableDeclarationExtractor.TableHeading}` 헤딩과 "
                        + $"{expectations.LocalVariableDeclarations.Count}개 행을 `## 파라미터 목록`에 "
                        + "그대로 옮겨야 합니다 — 표만 두고 헤딩을 빼면 리더가 그 표를 못 읽습니다.";
                    result.Errors.Add(missing);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.LocalVariableTableMismatch,
                        Message = missing
                    });
                    return;
                }

                var rowCells = new List<IReadOnlyList<string>>();
                for (var i = headingIndex + 1; i < endIndex; i++)
                {
                    if (!lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal)) continue;

                    var cells = SplitTableRowCells(lines[i]);
                    // 헤더 행과 구분 행은 대조 대상이 아니다. 구분 행은 `:---` 모양이고
                    // 헤더 행은 `@`로 시작하는 칸이 없다 - 아래 판정이 둘 다 걸러 낸다.
                    rowCells.Add(cells);
                }

                // 정방향 - 모든 DECLARE 사실에 행이 있는가.
                foreach (var fact in expectations.LocalVariableDeclarations)
                {
                    var present = rowCells.Any(cells =>
                        cells.Any(c => string.Equals(c, fact.Name, StringComparison.OrdinalIgnoreCase))
                        && cells.Any(c => c == fact.DataType));
                    if (present) continue;

                    var message =
                        $"지역 변수 표에 `{fact.Name}` 행이 없거나 선언 타입이 다릅니다. "
                        + $"원본 DDL은 이 변수를 `{fact.DataType}`으로 선언합니다 — 그대로 옮겨야 합니다. "
                        + "타입을 이름으로 추측하면 금액 변수가 정수로 선언되어 절삭됩니다.";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.LocalVariableTableMismatch,
                        Message = message,
                        RawContext = fact.Name
                    });
                }

                // 역방향 - 모든 행에 DECLARE 사실이 있는가.
                var known = new HashSet<string>(
                    expectations.LocalVariableDeclarations.Select(f => f.Name),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var cells in rowCells)
                {
                    var name = cells.FirstOrDefault(c =>
                        c.StartsWith("@", StringComparison.Ordinal)
                        && !c.StartsWith("@@", StringComparison.Ordinal));
                    if (name == null || known.Contains(name)) continue;

                    var message =
                        $"지역 변수 표에 원본 DDL이 선언하지 않은 `{name}` 행이 있습니다. "
                        + "이 표는 기계 확정 전사표이므로 행을 더하면 안 됩니다 — "
                        + "원본에 없는 변수는 지우십시오.";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.LocalVariableTableMismatch,
                        Message = message,
                        RawContext = name
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MechanicalValidator] 지역 변수 표 대조 실패 - 이 검사만 건너뜁니다.");
            }
        }
```

**`@@` 접두사를 역방향에서 빼는 이유**: 옛 세대의 표는 `@@ERROR` 같은 시스템 값 행을 함께 실었다(`EXCEPTION_PROC` 실물). 그 행은 `DECLARE` 사실이 아니므로 역방향이 전부 발화시킨다. T-SQL 문법상 `@@`는 사용자가 `DECLARE`할 수 없는 시스템 전역값이라 **언제나 안전하게 제외할 수 있다** — 검사 D가 같은 이유로 같은 방어를 갖는다.

- [ ] **Step 5: `Validate`에 배선한다**

`CheckSetAssignments(cleansed, expectations, result);`(`:201`) 바로 뒤:

```csharp
                    CheckLocalVariableDeclarationTable(cleansed, expectations, result);
```

- [ ] **Step 6: 테스트가 통과하는지 돌려 본다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~LocalVariableTableL1Tests"`
Expected: 6 passed

- [ ] **Step 7: 전체 게이트**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0 · 빌드 경고 0

- [ ] **Step 8: 커밋**

```bash
git diff --cached --name-only   # 비어 있어야 한다. MechanicalValidator.cs는 다른 세션도 만지는 파일이다
git commit -m "feat: 지역 변수 표를 양방향으로 대조하는 L1 검사를 더한다" -- \
  src/ReSet.Core/Services/MechanicalValidator.cs \
  tests/ReSet.Core.Tests/LocalVariableTableL1Tests.cs
```

---

## Task 6: 리더 이음매 잠금

**Files:**
- Test: `tests/ReSet.Core.Tests/LocalVariableTableSeamTests.cs` (신)

**Interfaces:**
- Consumes: `LocalVariableDeclarationExtractor.TableHeading` (Task 1) · `SpecStatementFactsExtractor.Extract` (기존)
- Produces: 없음(테스트 전용 태스크).

**이 태스크가 없으면 이 계획 전체가 헛돈다.** 새 표가 검사 D에 닿는 유일한 통로가 `SpecStatementFactsExtractor.ReadLocalVariables`이고, 그 통로는 **헤딩 접두사 두 개와 헤더 칸 이름 두 개**라는 우연한 일치로만 열려 있다. 누가 헤딩을 `### 로컬 변수`로 바꾸거나 헤더를 `| 변수 이름 |`로 바꾸면 **검사 D가 또 조용히 꺼진다** — (5-3-7)이 정확히 그 모양이었다.

- [ ] **Step 1: 이음매 테스트를 쓴다**

```csharp
using System.Collections.Generic;
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 새 기계 확정 표가 검사 D의 리더에 실제로 닿는지 잠근다.
    ///
    /// [왜 이 테스트가 있는가 - known-defects (5-3-7)]
    /// 검사 D(CheckSpecLocalVariablesDeclared)는 SpecStatementFactsExtractor가 읽은
    /// LocalVariables가 비면 조용히 반환한다. 이 계획이 만든 표가 그 리더에 안 걸리면
    /// 강제 세 층을 다 세우고도 검사는 여전히 꺼져 있다 - 그리고 그 사실은
    /// 아무 테스트도 빨갛게 만들지 않는다.
    /// </summary>
    public class LocalVariableTableSeamTests
    {
        private static string SpecMarkdown() =>
            "## 파라미터 목록\n\n"
            + LocalVariableDeclarationExtractor.TableHeading + "\n"
            + "| 변수 명칭 | 데이터 타입 | 초기값 |\n"
            + "| :--- | :--- | :--- |\n"
            + "| @v_intCLTotal | MONEY | 0 |\n"
            + "| @v_strClientID | VARCHAR(20) |  |\n"
            + "\n## 다음 절\n";

        [Fact]
        public void TheMachineHeading_ShouldBeReadableByTheCheckDReader()
        {
            // FileName에 .md를 붙이면 안 된다 - BareObjectName이 "md"로 뭉갠다.
            // Extract는 IReadOnlyDictionary<string, SpecStatementFacts>를 낸다
            // (SpecStatementFactsExtractor.cs:142). 키는 BareObjectName(fileName)이다.
            var facts = SpecStatementFactsExtractor.Extract(
                new List<(string FileName, string Content)> { ("dbo.P", SpecMarkdown()) });

            var variables = facts.Values.SelectMany(f => f.LocalVariables).ToList();

            Assert.Equal(2, variables.Count);
            Assert.Contains(variables, v => v.Name == "@v_intCLTotal" && v.TypeOrKind == "MONEY");
            Assert.Contains(variables, v => v.Name == "@v_strClientID");
        }

        [Fact]
        public void TheMachineHeading_ShouldStartWithAKnownReaderPrefix()
        {
            // 리더가 구간을 잡는 접두사 목록과 새 헤딩의 일치를 직접 못박는다.
            // 위 테스트가 이미 통로를 재지만, 이 단언은 깨졌을 때 원인을 곧바로 말한다.
            Assert.StartsWith("### 지역 변수", LocalVariableDeclarationExtractor.TableHeading);
        }

        [Fact]
        public void TheMachineHeader_ShouldCarryTheTwoColumnFragmentsTheReaderLooksFor()
        {
            // 리더는 이름 칸을 "명칭"으로, 타입 칸을 "데이터 타입"으로 찾는다.
            const string header = "| 변수 명칭 | 데이터 타입 | 초기값 |";

            Assert.Contains("명칭", header);
            Assert.Contains("데이터 타입", header);
        }
    }
}
```

- [ ] **Step 2: 돌려서 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~LocalVariableTableSeamTests"`
Expected: 3 passed

**여기서 첫 테스트가 빨개지면 그것이 이 계획의 가장 중요한 발견이다.** 표가 프롬프트에 실리고 L1이 대조해도 리더가 못 읽으면 검사 D는 여전히 꺼져 있다. 그때는 **리더의 `LocalVariableHeadingPrefixes`나 `FindColumn` 후보를 넓히는 것이 정답인지 조율자에게 판단을 올려라** — 리더를 넓히면 옛 세대 문서까지 판정 범위가 바뀐다.

- [ ] **Step 3: 커밋**

```bash
git commit -m "test: 새 지역 변수 표가 검사 D의 리더에 닿는 이음매를 잠근다" -- \
  tests/ReSet.Core.Tests/LocalVariableTableSeamTests.cs
```

---

## Task 7: 코퍼스 만족 가능성 테스트

**Files:**
- Test: `tests/ReSet.Core.Tests/LocalVariableTableCorpusTests.cs` (신)

**Interfaces:**
- Consumes: `LocalVariableDeclarationExtractor.Extract` (Task 1) · `CheckLocalVariableDeclarationTable`을 태우는 `MechanicalValidator.Validate` (Task 5)
- Produces: 없음(테스트 전용 태스크).

**왜 필요한가 (설계서 §6-1).** 이 회차는 재생성을 안 하므로 새 검사는 **한 번도 실행돼 본 적 없는 검사**가 된다. 그 검사가 만족 불가능한 지시라면 다음 재생성에서 31개 객체가 한꺼번에 재시도를 소진한다. `ErrorCodeTableCorpusTests`가 로드맵 4에서 같은 위험을 승격 **전에** 닫았고 예측이 맞았다 — **그 파일을 본떠 쓴다.**

**세 루트를 다 돈다.** `output/Procedures`(14) · `output/Functions`(10) · `output/External/*/*`(7) = 31. 프로시저만 돌면 함수 쪽 추출기가 통째로 비어도 하한이 프로시저만으로 만족돼 **조용히 통과한다**. `output.bak-*`는 걷지 않는다.

- [ ] **Step 1: 선례를 연다**

```bash
sed -n '1,200p' tests/ReSet.Core.Tests/ErrorCodeTableCorpusTests.cs
```

**이 파일을 뼈대로 복사해 쓴다.** 이미 확인한 것들(다시 조사하지 마라):

- `CorpusPaths.RepoRoot()` · `CorpusSkip.Reason`이 있고, 테스트는 `[SkippableFact]` + `Skip.If`로 코퍼스 부재를 다룬다.
- 세 루트를 `(ObjectKind Kind, string Label, string Dir)` 배열로 돌고, **External 밑 임의 DB 폴더를 재귀로 잡는다**(DB 이름을 하드코딩하지 않는다).
- 객체 디렉터리는 `raw/metadata.json`의 **조부모**다 — `Procedures/<obj>/raw/`와 `External/<db>/Functions/<obj>/raw/`의 깊이 차이를 이 관계가 흡수한다.
- `SpDefinition`은 그 `metadata.json`을 `JsonSerializer`(`PropertyNameCaseInsensitive = true`)로 읽어 만든다.
- `MechanicalValidator`는 `new MechanicalValidator()`로 만들어 재사용한다.

**⚠ `Skip`이 켜지면 게이트가 깨진다.** 이 저장소의 합격 기준은 **건너뜀 0**이다. 테스트를 돌리기 전에 `output/`이 실제로 있는지 확인하라. 격리 워크트리에서 잰다면 `output`과 `output.bak-2026-08-22`를 **둘 다** 심링크해야 코퍼스 테스트가 안 건너뛴다.

- [ ] **Step 2: 코퍼스 테스트를 쓴다**

`ErrorCodeTableCorpusTests`의 순회를 그대로 두고 **재는 것만 바꾼다** — `expectations.ErrorCodes` 자리에 `expectations.LocalVariableDeclarations`를 넣는다.

```csharp
        [SkippableFact]
        public void LocalVariableTable_RenderedFromDdl_IsAcceptedByTheCheck()
        {
            // ... ErrorCodeTableCorpusTests와 같은 루트 순회 ...

                    var facts = expectations.LocalVariableDeclarations;
                    if (facts.Count > 0)
                    {
                        objectsWithFacts++;
                        factTotal += facts.Count;
                        kindTotals.ObjectsWithFacts++;
                        kindTotals.FactTotal += facts.Count;
                    }
                    byKind[kind] = kindTotals;

                    // 갈래 1 - 완전 전사된 표. 사실이 있든 없든 발화가 없어야 한다.
                    foreach (var message in LocalVariableMessages(
                                 validator, PerfectTranscription(facts), expectations))
                    {
                        violations.Add($"[{label}] {label2} [전사됨] {message}");
                    }

                    // 갈래 2 - 표가 아예 없는 문서. 사실 0건인 객체는 침묵(조기 반환),
                    // 사실이 있는 객체는 반드시 발화해야 한다 - 발화하지 않으면 검사가
                    // 아무것도 지키지 않는다는 뜻이다.
                    var withoutTable = "## 파라미터 목록\n\n표가 없는 문서다.\n";
                    var missing = LocalVariableMessages(validator, withoutTable, expectations).ToList();

                    if (facts.Count == 0 && missing.Count > 0)
                    {
                        violations.Add($"[{label}] {label2} [사실 0건인데 표를 요구] {missing[0]}");
                    }

                    if (facts.Count > 0 && missing.Count == 0)
                    {
                        violations.Add($"[{label}] {label2} [사실 {facts.Count}건인데 표 부재에 침묵]");
                    }

                    _output.WriteLine($"[{label,-12}] {label2,-45} DECLARE 사실 {facts.Count,3}");

            // ... 순회 끝 ...

            Assert.True(objects > 0, "코퍼스 객체를 하나도 못 읽었다");

            // 하한이다. 정확값으로 박으면 코퍼스가 늘 때마다 빨개지고 다음 사람이
            // 관측을 읽는 대신 기대값을 고친다 - 그 근거는 ErrorCodeTableCorpusTests의
            // 클래스 주석에 있다. 하한은 루트 하나가 통째로 빠지는 회귀를 잡는다.
            Assert.True(objects >= 31, $"코퍼스 객체가 {objects}개다 - 31 이상이어야 한다");

            // 재료가 살아 있는가 - 추출기가 조용히 망가져 전부 비는 경우를 잡는다.
            // 이것이 없으면 이 테스트는 "발화 0"으로 통과하는데 그 0이 "검사가
            // 만족된다"가 아니라 "잴 재료가 없다"일 수 있다.
            Assert.True(objectsWithFacts >= 1, "DECLARE 사실을 가진 객체가 하나도 없다");
            Assert.True(factTotal >= 1, "DECLARE 사실 합이 0이다");

            Assert.Empty(violations);
        }

        private static IEnumerable<string> LocalVariableMessages(
            MechanicalValidator validator, string markdown, SpecExpectations expectations) =>
            validator.Validate(markdown, expectations).DetailedErrors
                .Where(e => e.Type == ErrorType.LocalVariableTableMismatch)
                .Select(e => e.Message);

        /// <summary>
        /// 완전 전사된 표를 테스트가 직접 만든다.
        ///
        /// [왜 AiService의 렌더러를 안 부르는가] 그것은 private이고, 설령 열려 있어도
        /// 부르면 안 된다 - 렌더러의 버그가 검사의 버그를 가려 준다. 두 자리가 같은
        /// 모양을 각자 적고 있으므로, 어긋나면 이 테스트가 빨개지는 것이 옳다.
        /// </summary>
        private static string PerfectTranscription(
            IReadOnlyList<LocalVariableDeclarationFact> facts)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 파라미터 목록");
            sb.AppendLine();
            sb.AppendLine(LocalVariableDeclarationExtractor.TableHeading);
            sb.AppendLine("| 변수 명칭 | 데이터 타입 | 초기값 |");
            sb.AppendLine("| :--- | :--- | :--- |");
            foreach (var f in facts)
            {
                sb.AppendLine($"| {f.Name} | {f.DataType} | {f.InitialValue} |");
            }
            return sb.ToString();
        }
```

**선례가 `StepSweepService.RenderErrorCodeTable(facts)`라는 공개 렌더러를 쓴다는 점은 알아 두되, 그것을 따라 새 공개 렌더러를 만들지 마라** — 공개 표면을 넓히는 것은 이 계획의 범위 밖이고, 위 사유대로 테스트가 자기 사본을 갖는 편이 이 검사에는 더 낫다.

- [ ] **Step 3: 실측값을 `ITestOutputHelper`로 찍고 기록한다**

`ErrorCodeTableCorpusTests`가 그렇게 한다(사실을 가진 객체 12 · 사실 합 84 · 발화 0을 문서에 남겼다). 같은 세 수를 찍는다.

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~LocalVariableTableCorpusTests" --logger "console;verbosity=detailed"`

찍힌 값을 적어 둔다 — **Task 10이 그것을 known-defects에 싣는다.**

- [ ] **Step 4: DDL 사실 합을 69와 대조한다**

**이것이 설계서 §9의 「안 잰 것」 첫 항목을 닫는 걸음이다.**

프로시저 14편만의 사실 합이 `SpecMaterialCensus`가 낸 **69**와 같아야 한다 — 같은 `DeclareVariableElement` 노드를 세기 때문이다.

- **같으면**: 두 계수기가 같은 것을 본다는 확인이다. 보고서에 적는다.
- **다르면 멈추고 원인을 가른다.** 후보 셋: (a) 내 추출기가 이름으로 접는데 census는 `HashSet`이라 같아야 한다 — 다르면 접기 규칙이 어긋난 것. (b) 커서·테이블 변수 경계가 갈렸다. (c) census의 69가 프로시저 14편이 아니라 다른 분모였다. **기대값을 고쳐 맞추지 마라 — 어느 쪽이 옳은지 판정하고 적어라.**

- [ ] **Step 5: 전체 게이트 + 커밋**

```bash
dotnet test
git commit -m "test: 지역 변수 표가 코퍼스 31 객체에서 만족 가능한지 잰다" -- \
  tests/ReSet.Core.Tests/LocalVariableTableCorpusTests.cs
```

---

## Task 8: 캐시 17 → 18 승격

**Files:**
- Modify: `src/ReSet.Core/Services/CacheManager.cs:197`

**Interfaces:**
- Consumes: Task 2의 카탈로그 등록(프롬프트 바이트를 바꾼 것이 승격의 근거다)
- Produces: `CurrentCacheFormatVersion == 18`

**AGENTS.md 95행이 「새 표는 카탈로그에 등록한 뒤 `CurrentCacheFormatVersion`도 함께 올리십시오」로 못박는다.**

- [ ] **Step 1: 번호 충돌을 확인한다**

`reset-l1-check` 스킬의 규칙이고, 캐시 17 승격 때 코디네이터가 전 브랜치를 확인한 선례가 `CacheManager.cs:194-196` 주석에 있다.

```bash
git fetch --all --quiet 2>/dev/null || true
for ref in $(git for-each-ref --format='%(refname)' refs/heads refs/remotes); do
  v=$(git show "$ref:src/ReSet.Core/Services/CacheManager.cs" 2>/dev/null \
      | grep -o 'CurrentCacheFormatVersion = [0-9]*' | grep -o '[0-9]*')
  [ -n "$v" ] && echo "$v  $ref"
done | sort -n
```

Expected: 18을 쓰는 브랜치가 **없어야 한다.** 있으면 멈추고 조율자에게 올려라.

- [ ] **Step 2: 동료 세션에 알린다**

**이것은 공유 상태다.** 올리면 다음에 생성을 돌리는 사람이 전건 재생성을 문다.

`reset-a4`는 2026-08-29에 「`--sp`도 계획서 재생성도 돌릴 계획이 없다 · 순서 맞출 것 없이 올려도 된다」고 답했다. **그 답이 이 회차보다 오래됐으면 다시 물어라.** `ListAgents`로 살아 있는 세션을 보고 `SendMessage`로 알린다.

- [ ] **Step 3: 번호를 올리고 근거를 적는다**

`CacheManager.cs:197`을 `18`로 바꾸고, 기존 17 주석 블록과 같은 자리에 근거를 더한다:

```csharp
        //   [18] 2026-08-29 - 기계 확정 「지역 변수」 표 신설(known-defects (5-3-7)).
        //     MachineConfirmedTables.All에 표가 하나 늘어 Critic 면제 블록의 바이트가
        //     바뀌고, Actor 프롬프트의 세 갈래(SP 전체·함수·OverviewAndParameters)에
        //     새 표가 실린다. AGENTS.md 95행이 카탈로그 등록과 함께 올리라고 못박는다.
        //     [이 회차는 재생성을 하지 않는다] 강제만 걸고 다음 재생성이 켜게 둔다 -
        //     그래서 이 승격은 "다음에 생성을 돌리는 사람이 전건 재생성을 문다"는 뜻이다.
        //     한 번도 안 돌아 본 검사가 오탐을 안고 켜지는 위험은 승격 전에 닫았다:
        //     LocalVariableTableCorpusTests가 31 객체 전건에서 만족 가능성을 잰다
        //     (ErrorCodeTableCorpusTests가 캐시 17 승격 때 한 것과 같은 자).
        //     번호 충돌 확인: 전 브랜치에서 18이 비어 있음을 확인했다.
        private const int CurrentCacheFormatVersion = 18;
```

- [ ] **Step 4: 전체 게이트**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0 · 빌드 경고 0

**캐시 버전을 못박는 테스트가 있으면 함께 갱신한다.**

```bash
grep -rn "CurrentCacheFormatVersion\|FormatVersion = 17\|{17}" tests/ | head
```

- [ ] **Step 5: 커밋**

```bash
git commit -m "feat: 지역 변수 표 신설로 캐시 포맷을 17에서 18로 올린다" -- \
  src/ReSet.Core/Services/CacheManager.cs
```

---

## Task 9: 변이 검증

**Files:**
- Create: `docs/audit-reports/sweeps/2026-08-29-local-variable-mutations.md`
- Modify (필요할 때만): 살아남은 변이가 가리키는 **테스트 파일** — 제품 코드는 이 태스크에서 안 고친다.

**Interfaces:**
- Consumes: Task 1~8의 구현 전부
- Produces: 변이 보고서. Task 10이 인용한다.

**직전 회차의 교훈이 이 목록의 모양을 정했다.** 그때 변이 여덟이 **전부 계수 로직**을 겨눴는데 **살아남은 결함 둘은 둘 다 표시 계층**에 있었다. 이번 목록은 출력과 이음매를 겨눈다.

| # | 변이 | 죽어야 할 테스트 |
| ---: | :--- | :--- |
| 1 | `TableHeading`을 `"### 로컬 변수 " + HeadingSuffix`로 바꾼다 | `LocalVariableTableSeamTests` 둘 |
| 2 | 렌더러 헤더의 `변수 명칭` 칸을 `변수 이름`으로 바꾼다 | `LocalVariableTablePromptTests` · 이음매 |
| 3 | `MachineConfirmedTables.All`에서 등록을 뺀다 | `MachineConfirmedTablesExpansionTests` 넷 |
| 4 | `:3207`(OverviewAndParameters)을 `Omit`으로 바꾼다 | `OverviewAndParametersBranch_ShouldCarryTheTable` |
| 5 | `:3347`(CrudAnalysis)을 `Table`로 바꾼다 | `BranchesThatCannotWriteParameterList_ShouldNotCarryTheTable` |
| 6 | L1의 역방향 절을 통째로 지운다 | `WhenTheTableHasAnInventedRow_ShouldReportIt` |
| 7 | `ErrorType`을 `General`로 바꾼다 | `WhenTheHeadingIsMissing_ShouldReportOnce` |
| 8 | 렌더러가 `InitialValue`를 언제나 빈 칸으로 낸다 | `WholeSpBranch_ShouldCarryTheTableWithItsHeadingAndRows` |
| 9 | `SpecExpectations`의 널 체인 항을 지운다 | `From_WhenLocalVariablesAreTheOnlyMaterial_ShouldNotReturnNull` |
| 10 | 추출기의 이름 접기(`_seen.Add`)를 없앤다 | `Extract_ShouldFoldRepeatedNamesKeepingTheFirst` |
| 11 | `CacheManager`를 17로 되돌린다 | (Task 8 Step 4에서 찾은 테스트가 있으면 그것) |

- [ ] **Step 1: 변이를 하나씩 넣고 돌린다**

각 변이마다:

```bash
# 변이를 넣는다 (편집)
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~LocalVariable"
# 결과를 적는다 (죽음/생존)
git checkout -- <그 파일>     # mv 금지 - 낡은 DLL 재사용 사고의 원인이다
```

**변이 11은 죽을 테스트가 없을 수 있다.** 그러면 「생존」으로 적는다 — 캐시 버전을 잠그는 테스트가 없다는 사실 자체가 발견이다.

- [ ] **Step 2: 살아남은 변이마다 테스트를 보강한다**

**제품 코드는 안 고친다.** 살아남은 변이는 **테스트의 결함**이다.

보강 뒤 **같은 변이를 다시 넣어 이번에는 죽는 것까지 확인한다.** 직전 회차가 그 확인을 했고, 그 확인이 「보강했다」와 「보강이 들었다」를 갈랐다.

**「실물에서 도달 불가」인 자리는 테스트 대신 주석이 옳을 수 있다.** 그때는 고치지 말고 판단을 조율자에게 올려라(직전 회차의 변이 5가 그런 자리였고, 리뷰어가 「결함이 아니라 정당하고 공개된 선택」으로 판정했다).

- [ ] **Step 3: 보고서를 쓴다**

`docs/audit-reports/sweeps/2026-08-29-local-variable-mutations.md`.

**「몇이 죽고 몇이 살았는지」와 「계획서의 예측이 빗나간 자리」를 맨 앞에 적는다.** 묻지 않아도 적는다 — **숨기면 그 문단 자체가 (5-3-7)이 된다**는 것이 직전 회차 보고서의 결론이다.

헤더에 **잰 시각 · 커밋 해시 · 작업 트리 청결도**를 적는다. 커밋 해시는 **`main`에서 도달 가능한 것**이어야 한다(직전 회차가 격리 워크트리 커밋을 적어 gc 뒤 사라질 뻔했다).

- [ ] **Step 4: 커밋**

```bash
dotnet test    # 게이트 재확인
git commit -m "test: 지역 변수 표 강제의 변이 검증을 기록한다" -- \
  docs/audit-reports/sweeps/2026-08-29-local-variable-mutations.md \
  tests/ReSet.Core.Tests/
```

---

## Task 10: 기록

**Files:**
- Modify: `docs/known-defects.md` — (5-3-7)에 강제 절을 더한다
- Modify: `AGENTS.md` — **거처 판정을 먼저 하고, 해당할 때만**
- Modify: `/Users/payletter/.claude/projects/-Users-payletter-git-root-ReSet/memory/axis-b-roadmap.md`

**Interfaces:**
- Consumes: Task 7의 코퍼스 실측값 · Task 9의 변이 결과
- Produces: 없음(기록 태스크).

- [ ] **Step 1: (5-3-7)에 강제 절을 더한다**

기존 실측 배너는 **그대로 둔다.** 그 아래에 더한다:

```markdown
  > **[2026-08-29 강제] 이 표가 기계 확정 표가 됐다.** 설계서
  > `docs/superpowers/specs/2026-08-29-local-variable-table-enforcement-design.md`.
  > 세 층이 전부 섰다 — 카탈로그(`MachineConfirmedTables.All`의 마지막 항목) ·
  > 프롬프트(`BuildLocalVariableTableLines`, 갈래 셋에 `Table`) ·
  > L1(`CheckLocalVariableDeclarationTable`, **양방향**).
  > 재료의 출처가 모델 재량에서 `LocalVariableDeclarationExtractor`로 옮겨졌다.
  >
  > **이 회차는 재생성을 하지 않았다.** 캐시는 18로 올렸으므로 다음 재생성이 켠다.
  > **그러므로 오늘 검사 D는 여전히 침묵한다** — 발화 0을 통과로 읽지 마라.
  >
  > **한 번도 안 돌아 본 검사의 위험은 승격 전에 닫았다** —
  > `LocalVariableTableCorpusTests`가 31 객체 전건에서 만족 가능성을 잰다
  > (실측: 사실을 가진 객체 N · 사실 합 M · 완전 전사에 발화 0).
  >
  > **소실 14건의 원인 분해가 닫혔다** (인접 축 `reset-a4`의 실측과 이 회차의
  > 헤딩 실측을 합쳐서). 12편은 「지역 변수」 문자열조차 없고, `SUMMARY_ETC`는
  > mermaid 노드 라벨뿐이며, **`EXCEPTION_PROC`은 표를 썼는데 헤딩을 안 붙여
  > 리더가 못 읽는 자리**였다(`Spec.md:87-92`). 즉 13은 「모델이 안 썼다」,
  > 1은 「리더가 못 읽는다」다. **후자는 프롬프트가 헤딩 리터럴을 함께 실으면서
  > 구조적으로 사라진다.**
```

**N·M은 Task 7이 찍은 실제 값으로 채운다. 자리표시자를 남기지 마라.**

- [ ] **Step 2: AGENTS.md 거처 판정**

**먼저 판정한다** — `reset-doc-sync`의 3-0이다. 「사람/에이전트의 판단만이 잡는다」에 해당할 때만 넣는다.

이 회차의 규칙 후보는 하나다:

> 기계 확정 지역 변수 표의 헤딩·헤더 문자열을 바꾸면 검사 D가 조용히 꺼진다.

**판정: 기계가 잡는다 → AGENTS.md에 넣지 않는다.** `LocalVariableTableSeamTests`가 그 이음매를 빨간불로 만든다. **이 판정을 커밋 메시지에 적어라** — 다음 사람이 「왜 안 넣었나」를 되묻지 않게.

다만 **캐시 18 승격 사실**은 다르다. `AGENTS.md` 95~96행이 캐시 규약을 이미 적고 있으므로 **새 규칙이 아니라 기존 규칙의 적용**이다. 넣지 않는다.

**결론: 이 회차는 AGENTS.md를 안 고친다.** 한 항목 600바이트 상한과 `DocumentationBudgetTests`를 건드릴 일이 없다. **README·`docs/architecture.md`도 안 고친다** — 회차 뒤 `/reset-doc-sync`로 한 번에 닫는다.

- [ ] **Step 3: 로드맵 메모를 갱신한다**

`memory/axis-b-roadmap.md`의 「⚠⚠ 새 결함 — 검사 D」 절에서 **후보 1을 「완료」로 바꾼다.** 후보 2(계수기 확대)는 그대로 남긴다.

적을 것 넷:
1. 강제 세 층이 섰고 **재생성은 안 했다** — 검사 D는 오늘도 침묵한다.
2. **캐시 18이 올라갔다** — 다음에 생성을 돌리는 사람이 전건 재생성을 문다.
3. 소실 14의 원인 분해가 닫혔다(13 모델 · 1 리더).
4. 변이 결과 — **살아남은 것이 있었으면 그것을 적는다.**

- [ ] **Step 4: 커밋**

```bash
git commit -m "docs: 지역 변수 표 강제를 (5-3-7)에 기록한다" -- \
  docs/known-defects.md
```

메모리 파일은 저장소 밖이라 커밋 대상이 아니다.

---

## 완료 조건

- [ ] `dotnet test` — 실패 0 · 건너뜀 0 · 빌드 경고 0
- [ ] `git status --short` — 비어 있다
- [ ] `LocalVariableTableSeamTests`가 초록 — **새 표가 검사 D의 리더에 실제로 닿는다**
- [ ] `LocalVariableTableCorpusTests`가 초록 — 31 객체에서 만족 가능하다
- [ ] 변이 보고서에 **죽은 수·산 수·예측이 빗나간 자리**가 맨 앞에 적혀 있다
- [ ] (5-3-7)에 강제 절이 있고 **「오늘도 검사 D는 침묵한다」가 명시**돼 있다
- [ ] `CurrentCacheFormatVersion == 18`이고 근거 주석이 있다
- [ ] **`--sp`를 한 번도 부르지 않았다**
