# DDL 커버리지 맵 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 원본 SP의 AST 잎 문장을 좌표계로 두고, 각 문장이 `Spec.md`와 어떤 관계인지 4상태로 판정해 자립형 HTML 한 장으로 낸다.

**Architecture:** 좌표계(`DdlStatementEnumerator`)는 추출기를 참조하지 않고 ScriptDom만으로 잎 문장을 전수 열거한다. 그 위에 추출기 재료(`SpecExpectations`)와 문서 앵커(`SpecAnchorIndex`) 두 축을 겹쳐 `CoverageMapComposer`가 4상태를 확정하고, `CoverageMapHtmlWriter`가 렌더한다. DB·AI 호출이 없다 — 입력은 `output/` 산출물뿐이다.

**Tech Stack:** .NET 10.0, `Microsoft.SqlServer.TransactSql.ScriptDom` 180.37.3 (`TSql160Parser`), xUnit 2.9.3, `Xunit.SkippableFact` 1.5.61, Spectre.Console(CLI 출력)

**Spec:** [`docs/superpowers/specs/2026-08-24-ddl-coverage-map-design.md`](../specs/2026-08-24-ddl-coverage-map-design.md)

## Global Constraints

- **분석 파이프라인·캐시·L1 게이트에 손대지 않는다.** 이 계획은 읽기 전용 산출물만 더한다. `VerificationPipelineOrchestrator`·`MechanicalValidator`·`CacheManager`를 수정하는 태스크는 없다.
- **`Spec.md`를 쓰지 않는다.** 모든 컴포넌트는 읽기 전용이다.
- **DB·AI 호출 금지.** `IDbMetadataService`·`IAiService`를 참조하지 않는다.
- **파서 오류 정책:** `TSql160Parser(true)`로 파싱하고 오류가 하나라도 있으면 빈 결과를 낸다. `CaseBranchExtractor.Extract`(`CaseBranchExtractor.cs:50-60`)와 같은 정책이다.
- **`output/`은 `.gitignore` 대상이다.** 실물 산출물에 의존하는 테스트는 전부 `[SkippableFact]`/`[SkippableTheory]` + `Skip.IfNot(...)`이고, 건너뛴 사유가 출력에 남아야 한다.
- **빌드 경고 상한: 0.** `dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l`이 **0**이어야 한다. `dotnet test` 실패 0.

  > **[2026-08-24 실측 정정]** 이 줄의 첫 판은 `AGENTS.md:199`를 그대로 베껴 "정확히 8건
  > (`DbMetadataServiceTests.cs`의 CS8600/CS8602)"을 상한으로 적었다. **그 기준선은 낡았다** —
  > `ORIGINAL_BASE`(`e8a6949`)에서 clean 빌드를 돌리면 실제로 **0건**이다. Task 1·Task 2 워커가
  > 각자 독립적으로 같은 관측을 보고했고 코디네이터가 재확인했다. 상한을 8로 두면 이번 작업이
  > 경고를 8개까지 새로 넣고도 통과하므로 정정한다. `AGENTS.md` 쪽 갱신은 이 계획의 범위 밖이라
  > 사용자에게 별도로 보고한다.
- **네임스페이스:** 새 Core 파일은 `namespace ReSet.Core.Services`, 새 테스트는 `namespace ReSet.Core.Tests`.

---

## 설계서에서 확정한 것을 계획이 바꾼 두 자리

구현 배관을 확인하며 설계서보다 나은 방법을 찾았다. 계획은 이쪽을 따른다.

**(가) 컨테이너 판정에 유형 화이트리스트를 쓰지 않는다.**
설계서 §1은 `IfStatement`·`WhileStatement`·`BeginEndBlockStatement`·`TryCatchStatement` 넷을 컨테이너로 열거하고, 미확정 사항 3번에 "목록이 충분한가"를 남겼다. 목록은 필요 없다 — **다른 문장을 품고 있으면 컨테이너다.** 수집한 문장들의 토큰 범위(`FirstTokenIndex`..`LastTokenIndex`) 포함관계로 판정하면 유형을 몰라도 된다. 이 규칙은 `CreateProcedureStatement`(설계서 목록에 **빠져 있었다** — 이게 컨테이너가 아니면 SP 본문 전체가 잎 하나가 되어 맵이 통째로 무의미해진다)와 `DeclareCursorStatement` 같은 것까지 자동으로 받는다. **미확정 사항 3번은 이 규칙으로 닫힌다.**

**(나) 「분모에서 빼는 것」은 구조로 만족된다.**
설계서 §2는 주석·빈 줄·`GO`·`BEGIN`/`END`를 분모에서 빼라고 적었다. 커버리지를 **라인이 아니라 잎 문장 단위로** 세면 분모가 애초에 잎 문장 수라 그 줄들은 들어올 수 없다. 그래서 별도 제외 로직을 만들지 않는다. 대신 두 층위를 명시한다.

- **수치**는 잎 문장 단위다. 분모 = 잎 문장 수.
- **화면 색칠**은 라인 단위다. 잎 문장의 `[StartLine, EndLine]`을 그 문장 상태로 칠하고, **어떤 잎 문장에도 속하지 않은 줄은 무채색**으로 남긴다(판정 대상이 아니다).

---

## File Structure

| 파일 | 책임 |
|---|---|
| `src/ReSet.Core/Services/DdlStatementEnumerator.cs` (신규) | 원본 DDL → 잎/컨테이너 문장 목록. **좌표계. 추출기를 참조하지 않는다.** |
| `src/ReSet.Core/Services/SpecAnchorIndex.cs` (신규) | `Spec.md` → 라인 앵커 목록(출처·행 원문·주석 여부 포함) |
| `src/ReSet.Core/Services/CoverageMapComposer.cs` (신규) | 좌표계 + `SpecExpectations` + 앵커 → 문장별 4상태 |
| `src/ReSet.Core/Services/CoverageMapHtmlWriter.cs` (신규) | 판정 결과 → 자립형 HTML 한 장 |
| `src/ReSet.Cli/CoverageMapCommand.cs` (신규) | 대상 해석(Job 폐포 / 단일 객체) → 산출물 로드 → HTML 기록 |
| `src/ReSet.Cli/CliArgs.cs` (수정) | `CoverageMapTarget` 속성 추가 |
| `src/ReSet.Cli/Program.cs` (수정) | `--coverage-map` 파싱과 DB 연결 **이전** 분기 |
| `tests/ReSet.Core.Tests/DdlStatementEnumeratorTests.cs` (신규) | Task 1 |
| `tests/ReSet.Core.Tests/SpecAnchorIndexTests.cs` (신규) | Task 2 |
| `tests/ReSet.Core.Tests/CoverageMapComposerTests.cs` (신규) | Task 3 |
| `tests/ReSet.Core.Tests/CoverageMapProbeTests.cs` (신규) | Task 4 — 실측 게이트 |
| `tests/ReSet.Core.Tests/CoverageMapHtmlWriterTests.cs` (신규) | Task 5 |
| `tests/ReSet.Core.Tests/CoverageMapGoldenTests.cs` (신규) | Task 7 — 감사 이력 대조 |

**태스크 순서의 근거:** 설계서 §「위험」이 "🟧이 얼마나 나올지 모른다"를 열어 뒀고 그 숫자가 이 도구의 쓸모를 좌우한다. 그래서 **Task 4를 실측 게이트로 두고 HTML(Task 5)보다 앞에 놓는다.** 숫자를 보기 전에 렌더링에 투자하지 않는다.

---

### Task 1: `DdlStatementEnumerator` — 잎 문장 좌표계

**Files:**
- Create: `src/ReSet.Core/Services/DdlStatementEnumerator.cs`
- Test: `tests/ReSet.Core.Tests/DdlStatementEnumeratorTests.cs`

**Interfaces:**
- Consumes: `Microsoft.SqlServer.TransactSql.ScriptDom` (`TSql160Parser`, `TSqlFragmentVisitor`, `TSqlStatement`)
- Produces:
  - `public sealed record DdlStatement(int StartLine, int EndLine, string StatementType, int NestingDepth, bool IsContainer)`
  - `public static IReadOnlyList<DdlStatement> DdlStatementEnumerator.Enumerate(string? ddlText)` — 문서 순(StartLine, 그다음 EndLine 내림차순) 정렬. 파스 실패 시 빈 목록.
  - `public static IReadOnlyList<DdlStatement> DdlStatementEnumerator.Leaves(IReadOnlyList<DdlStatement> all)` — `IsContainer == false`인 것만.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/DdlStatementEnumeratorTests.cs`:

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class DdlStatementEnumeratorTests
    {
        [Fact]
        public void Enumerate_CreateProcedureBody_ShouldNotSwallowEverythingIntoOneLeaf()
        {
            // CreateProcedureStatement가 컨테이너로 잡히지 않으면 SP 본문 전체가
            // 잎 하나가 되어 맵이 통째로 무의미해진다. 이 계획이 설계서 목록에서
            // 빠진 것을 발견한 자리라 가드로 고정한다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T SET A = 1 WHERE B = 2
    DELETE FROM dbo.U WHERE C = 3
END";

            var all = DdlStatementEnumerator.Enumerate(ddl);
            var leaves = DdlStatementEnumerator.Leaves(all);

            Assert.Equal(2, leaves.Count);
            Assert.Contains(leaves, s => s.StatementType == "UpdateStatement");
            Assert.Contains(leaves, s => s.StatementType == "DeleteStatement");
            Assert.Contains(all, s => s.StatementType == "CreateProcedureStatement" && s.IsContainer);
        }

        [Fact]
        public void Enumerate_IfWithTwoStatements_ShouldCountTwoLeavesNotOne()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    IF @x = 1
    BEGIN
        UPDATE dbo.T SET A = 1
        UPDATE dbo.T SET A = 2
    END
END";

            var leaves = DdlStatementEnumerator.Leaves(DdlStatementEnumerator.Enumerate(ddl));

            Assert.Equal(2, leaves.Count);
            Assert.All(leaves, s => Assert.Equal("UpdateStatement", s.StatementType));
        }

        [Fact]
        public void Enumerate_MultiLineInsertSelect_ShouldSpanToItsLastLine()
        {
            // 앵커는 문장 시작점만 지목한다. 끝줄이 맞아야 20줄짜리 INSERT가
            // 술어 행들을 한 덩어리로 끌어안는다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.T (A, B)
    SELECT
        X.A,
        X.B
    FROM dbo.S AS X
    WHERE X.C = 1
END";

            var insert = DdlStatementEnumerator.Leaves(DdlStatementEnumerator.Enumerate(ddl))
                .Single(s => s.StatementType == "InsertStatement");

            Assert.Equal(3, insert.StartLine);
            Assert.Equal(8, insert.EndLine);
        }

        [Fact]
        public void Enumerate_NestedIf_ShouldReportNestingDepth()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    IF @x = 1
    BEGIN
        IF @y = 2
        BEGIN
            UPDATE dbo.T SET A = 1
        END
    END
END";

            var leaf = DdlStatementEnumerator.Leaves(DdlStatementEnumerator.Enumerate(ddl)).Single();

            // CREATE PROC > BEGIN..END > IF > BEGIN..END > IF > BEGIN..END = 6겹
            Assert.True(leaf.NestingDepth >= 4, $"깊이가 {leaf.NestingDepth}로 너무 얕다");
        }

        [Fact]
        public void Enumerate_SubqueryInWhere_ShouldStayOneLeaf()
        {
            // 하위 질의는 TSqlStatement가 아니라 QueryExpression이므로 잎을 쪼개지 않는다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T SET A = 1 WHERE B IN (SELECT C FROM dbo.S)
END";

            var leaves = DdlStatementEnumerator.Leaves(DdlStatementEnumerator.Enumerate(ddl));

            Assert.Single(leaves);
            Assert.Equal("UpdateStatement", leaves[0].StatementType);
        }

        [Fact]
        public void Enumerate_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(DdlStatementEnumerator.Enumerate("CREATE PROCEDURE ((("));
        }

        [Fact]
        public void Enumerate_NullOrBlank_ShouldReturnEmpty()
        {
            Assert.Empty(DdlStatementEnumerator.Enumerate(null));
            Assert.Empty(DdlStatementEnumerator.Enumerate("   "));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DdlStatementEnumeratorTests"`
Expected: 컴파일 실패 — `DdlStatementEnumerator`가 없다.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Core/Services/DdlStatementEnumerator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <param name="StartLine">문장이 시작하는 원본 DDL 줄(1-based).</param>
    /// <param name="EndLine">문장의 마지막 토큰이 놓인 줄. 토큰 스트림이 없으면 StartLine과 같다.</param>
    /// <param name="StatementType">ScriptDom 노드 타입 이름(예: "UpdateStatement").</param>
    /// <param name="NestingDepth">이 문장을 품고 있는 다른 문장의 수.</param>
    /// <param name="IsContainer">다른 문장을 품고 있으면 true. 커버리지는 잎만 센다.</param>
    public sealed record DdlStatement(
        int StartLine,
        int EndLine,
        string StatementType,
        int NestingDepth,
        bool IsContainer);

    /// <summary>
    /// 원본 DDL의 문장을 전수 열거해 커버리지 맵의 <b>좌표계</b>를 만든다.
    ///
    /// [왜 추출기를 참조하지 않는가] 좌표계를 추출기로 만들면 추출기의 사각지대가
    /// 좌표계의 사각지대가 된다. 그러면 커버리지 맵이 답하려는 질문 — "우리 기계 확정
    /// 표가 원본의 무엇을 아예 안 보고 있나" — 에 영원히 답할 수 없다. 이 파일은
    /// ScriptDom 외에 아무것도 쓰지 않는다.
    ///
    /// [왜 컨테이너 유형 목록을 두지 않는가] 설계서 초안은 IfStatement·WhileStatement·
    /// BeginEndBlockStatement·TryCatchStatement 넷을 열거했는데 CreateProcedureStatement가
    /// 빠져 있었다 - 그게 잎으로 세어지면 SP 본문 전체가 잎 하나가 되어 맵이 통째로
    /// 무의미해진다. 목록은 언제든 다시 낡는다. 그래서 유형이 아니라 <b>사실</b>로
    /// 판정한다: 다른 문장을 품고 있으면 컨테이너다. 토큰 범위 포함관계로 본다.
    /// </summary>
    public static class DdlStatementEnumerator
    {
        public static IReadOnlyList<DdlStatement> Enumerate(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<DdlStatement>();

            TSqlFragment? fragment;
            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    // CaseBranchExtractor.Extract와 같은 정책 - 오류가 하나라도 있으면
                    // 빈 목록. 부분 파스 결과로 만든 좌표계는 없느니만 못하다.
                    return Array.Empty<DdlStatement>();
                }
            }
            catch (Exception)
            {
                return Array.Empty<DdlStatement>();
            }

            var visitor = new StatementCollector();
            fragment.Accept(visitor);
            var raw = visitor.Statements;
            if (raw.Count == 0) return Array.Empty<DdlStatement>();

            var result = new List<DdlStatement>(raw.Count);
            foreach (var s in raw)
            {
                var depth = 0;
                var isContainer = false;
                foreach (var other in raw)
                {
                    if (ReferenceEquals(other, s)) continue;
                    if (Contains(other, s)) depth++;
                    if (Contains(s, other)) isContainer = true;
                }

                result.Add(new DdlStatement(
                    s.StartLine,
                    EndLineOf(s.Node),
                    s.Node.GetType().Name,
                    depth,
                    isContainer));
            }

            return result
                .OrderBy(s => s.StartLine)
                .ThenByDescending(s => s.EndLine)
                .ToList();
        }

        public static IReadOnlyList<DdlStatement> Leaves(IReadOnlyList<DdlStatement> all) =>
            all.Where(s => !s.IsContainer).ToList();

        /// <summary>outer가 inner를 <b>진부분</b>으로 품는가. 범위가 같으면 false다 -
        /// 같은 범위끼리 서로를 컨테이너로 만들어 잎이 하나도 안 남는 것을 막는다.</summary>
        private static bool Contains(Collected outer, Collected inner) =>
            outer.First <= inner.First
            && outer.Last >= inner.Last
            && (outer.First < inner.First || outer.Last > inner.Last);

        private static int EndLineOf(TSqlFragment node)
        {
            var stream = node.ScriptTokenStream;
            if (stream == null) return node.StartLine;
            var index = node.LastTokenIndex;
            if (index < 0 || index >= stream.Count) return node.StartLine;
            var line = stream[index].Line;
            return line > 0 ? line : node.StartLine;
        }

        private sealed record Collected(TSqlStatement Node, int StartLine, int First, int Last);

        private sealed class StatementCollector : TSqlFragmentVisitor
        {
            public List<Collected> Statements { get; } = new();

            public override void Visit(TSqlStatement node)
            {
                if (node.StartLine <= 0) return;
                Statements.Add(new Collected(node, node.StartLine, node.FirstTokenIndex, node.LastTokenIndex));
            }
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DdlStatementEnumeratorTests"`
Expected: PASS (7 tests)

만약 `Enumerate_CreateProcedureBody_...`에서 잎이 2개보다 많이 나오면, `BEGIN...END`가 `BeginEndBlockStatement`로 잡혀 컨테이너가 되고 그 안의 둘만 잎이어야 정상이다. 3개 이상이 나오면 어떤 유형이 더 걸렸는지 `StatementType`을 출력해 확인한다.

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/DdlStatementEnumerator.cs tests/ReSet.Core.Tests/DdlStatementEnumeratorTests.cs
git commit -m "feat: 원본 DDL의 잎 문장을 좌표계로 전수 열거한다

컨테이너를 유형 목록이 아니라 토큰 범위 포함관계로 판정한다. 설계서 초안의
네 유형 목록에는 CreateProcedureStatement가 빠져 있었고, 그게 잎으로 세어지면
SP 본문 전체가 잎 하나가 되어 맵이 통째로 무의미해진다. 목록은 언제든 다시
낡으므로 유형이 아니라 사실로 판정한다.

추출기를 참조하지 않는다. 좌표계를 추출기로 만들면 추출기의 사각지대가
좌표계의 사각지대가 되어, 정작 알고 싶은 '우리 표가 원본의 무엇을 아예 안 보고
있나'에 답하지 못한다."
```

---

### Task 2: `SpecAnchorIndex` — 문서 앵커 수집

**Files:**
- Create: `src/ReSet.Core/Services/SpecAnchorIndex.cs`
- Test: `tests/ReSet.Core.Tests/SpecAnchorIndexTests.cs`

**Interfaces:**
- Consumes: 없음(순수 문자열 처리)
- Produces:
  - `public sealed record SpecAnchor(int Line, string Source, string RowText, bool IsCommentAnchor)`
  - `public static IReadOnlyList<SpecAnchor> SpecAnchorIndex.Build(string? specMarkdown)`
  - `public static int SpecAnchorIndex.CountLineBearingTables(string? specMarkdown)` — 맵 상단에 찍을 "읽은 표 종수"

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SpecAnchorIndexTests.cs`:

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SpecAnchorIndexTests
    {
        [Fact]
        public void Build_ShouldFindLineColumnByHeaderName_NotByPosition()
        {
            // CASE 분기 표는 '라인'이 1번째 칸이다. 위치를 상수로 박으면 '순서' 값
            // (1, 2, 3...)을 라인 번호로 줍는다 - 설계서 첫 판이 실제로 낸 오류다.
            const string md = @"### CASE 분기 (기계 확정 — 수정 금지)

| 라인 | 순서 | 조건 원문 | 결과 원문 |
| :--- | :--- | :--- | :--- |
| 412 | WHEN 1 | @x > 3 | 7 |
| 415 | ELSE | (그 외 전부) | 0 |
";

            var lines = SpecAnchorIndex.Build(md).Select(a => a.Line).ToList();

            Assert.Equal(new[] { 412, 415 }, lines);
            Assert.DoesNotContain(1, lines);
            Assert.DoesNotContain(2, lines);
        }

        [Fact]
        public void Build_SetPredicateTable_ShouldReadSecondColumn()
        {
            const string md = @"### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 | 연산 |
| :--- | :--- | :--- | :--- |
| DELETE 1 | 38 | PGNAME | IN |
";

            var anchor = Assert.Single(SpecAnchorIndex.Build(md));
            Assert.Equal(38, anchor.Line);
            Assert.False(anchor.IsCommentAnchor);
            Assert.Contains("집합 술어", anchor.Source);
            Assert.Contains("PGNAME", anchor.RowText);
        }

        [Theory]
        [InlineData("### 원본 주석 기록", "라인", "원본 주석")]
        [InlineData("### 원본 주석 보존", "라인", "원본 주석")]
        [InlineData("### 원본 주석 보존 내역", "라인", "원본 주석")]
        [InlineData("### 원본 주석 및 이력", "라인", "원문 주석 또는 선언")]
        [InlineData("### 원본 주석 및 구현 대조", "라인", "원문 주석")]
        [InlineData("### 원본 주석 및 실제 구현 대조", "라인", "원문 주석")]
        public void Build_CommentTableVariants_ShouldAllBeMarkedAsCommentAnchors(
            string heading, string firstColumn, string secondColumn)
        {
            // 실측: 주석 표 제목이 여섯으로 갈리고 컬럼명도 셋으로 갈린다.
            // 제목이나 컬럼명 하나로 식별하면 반드시 샌다.
            var md = $@"{heading}

| {firstColumn} | {secondColumn} |
| :--- | :--- |
| 77 | -- 정산 보류 처리 |
";

            var anchor = Assert.Single(SpecAnchorIndex.Build(md));
            Assert.Equal(77, anchor.Line);
            Assert.True(anchor.IsCommentAnchor, $"'{heading}'가 주석 앵커로 표시되지 않았다");
        }

        [Fact]
        public void Build_CommentTableWithoutItsOwnHeading_ShouldStillBeMarked()
        {
            // EXCEPTION_PROC 실측: '## 로직 흐름 요약' 아래 산문 뒤에 제목 없이 붙는다.
            const string md = @"## 로직 흐름 요약

원본 주석 및 이력은 다음과 같습니다.

| 라인 | 원본 주석 |
| :--- | :--- |
| 91 | -- 부가세 계산 |
";

            var anchor = Assert.Single(SpecAnchorIndex.Build(md));
            Assert.Equal(91, anchor.Line);
            Assert.True(anchor.IsCommentAnchor);
        }

        [Fact]
        public void Build_SectionHeading_ShouldPickOriginalDdlLine()
        {
            const string md =
                "### UPDATE 대상 테이블: SETTLE_POQ_DB.dbo.TSettleMst (갱신 1 · 원본 DDL 라인 38 · 원문 표기: TSettleMst)\n";

            var anchor = Assert.Single(SpecAnchorIndex.Build(md));
            Assert.Equal(38, anchor.Line);
            Assert.Equal("절 제목", anchor.Source);
            Assert.False(anchor.IsCommentAnchor);
        }

        [Fact]
        public void Build_ReferencedFunctionCell_ShouldPickParenthesizedLine()
        {
            const string md = @"### 참조 함수 (기계 확정 — 수정 금지)

| 함수 | 호출 문장 | 호출식 | 명세서 |
| :--- | :--- | :--- | :--- |
| dbo.UF_GET_ROUND4VAT | UPDATE 3 (라인 110) | dbo.UF_GET_ROUND4VAT(X) | [Spec](../a.md) |
";

            var anchors = SpecAnchorIndex.Build(md);
            Assert.Contains(anchors, a => a.Line == 110);
        }

        [Fact]
        public void Build_TableInsideCodeFence_ShouldBeIgnored()
        {
            const string md = @"### 예시

```
| 문장 | 라인 |
| :--- | :--- |
| DELETE 1 | 999 |
```
";

            Assert.Empty(SpecAnchorIndex.Build(md));
        }

        [Fact]
        public void CountLineBearingTables_ShouldCountDistinctTablesWithLineColumn()
        {
            const string md = @"### DML 범위 (기계 확정 — 수정 금지)

| 문장 | 라인 | 대상 |
| :--- | :--- | :--- |
| DELETE 1 | 35 | T |

### 파생 테이블 정의 (기계 확정 — 수정 금지)

| 별칭 | 컬럼 | 정의 표현식 |
| :--- | :--- | :--- |
| X | A | SUM(B) |
";

            // 파생 테이블 정의 표에는 '라인' 칸이 없다(실측). 1종만 세어야 한다.
            Assert.Equal(1, SpecAnchorIndex.CountLineBearingTables(md));
        }

        [Fact]
        public void Build_NullOrBlank_ShouldReturnEmpty()
        {
            Assert.Empty(SpecAnchorIndex.Build(null));
            Assert.Empty(SpecAnchorIndex.Build("   "));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SpecAnchorIndexTests"`
Expected: 컴파일 실패 — `SpecAnchorIndex`가 없다.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Core/Services/SpecAnchorIndex.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <param name="Line">이 앵커가 지목하는 원본 DDL 줄.</param>
    /// <param name="Source">"표: {제목}" · "절 제목" · "셀 내 (라인 N)".</param>
    /// <param name="RowText">근거 패널에 그대로 띄울 원문 한 줄.</param>
    /// <param name="IsCommentAnchor">원본 주석 표에서 나왔으면 true. 커버리지 판정에서 뺀다.</param>
    public sealed record SpecAnchor(int Line, string Source, string RowText, bool IsCommentAnchor);

    /// <summary>
    /// Spec.md가 지목하는 원본 DDL 줄을 전부 걷는다.
    ///
    /// [왜 제목 화이트리스트를 두지 않는가 - 2026-08-24 실측]
    /// 표 제목이 표준화돼 있지 않다. 주석 표 하나가 '원본 주석 기록'·'원본 주석 보존'·
    /// '원본 주석 보존 내역'·'원본 주석 및 이력'·'원본 주석 및 구현 대조'·
    /// '원본 주석 및 실제 구현 대조' 여섯으로 갈리고, EXCEPTION_PROC은 아예 제목 없이
    /// '## 로직 흐름 요약' 아래 산문 뒤에 붙인다. 그래서 <b>헤더에 '라인' 칸이 있는
    /// 표를 전부</b> 줍는다.
    ///
    /// [왜 칸 위치를 상수로 박지 않는가] '라인' 칸 위치가 표마다 다르다. 집합 술어·
    /// 잠금 힌트·DML 범위·실행 의미는 2번째인데 CASE 분기와 주석 표는 1번째다.
    /// 위치를 박으면 CASE 분기 표에서 '순서' 값(1, 2, 3...)을 라인 번호로 줍는다 -
    /// 설계서 첫 판이 실제로 낸 오류다.
    ///
    /// [왜 주석 앵커를 갈라 두는가] 원본 주석 표도 '라인' 칸을 갖는다(14개 SP 합 223행).
    /// 주석 표가 말하는 것은 "원본 38번 줄에 이런 주석이 있었다"이지 "38번 줄의 문장이
    /// 문서화됐다"가 아니다. 섞어 세면 주석이 빽빽한 SP일수록 커버리지가 높게 나와,
    /// 맵이 재려는 것과 정반대의 것을 재게 된다. 버리지는 않고 근거 패널에 참고로 띄운다.
    /// </summary>
    public static class SpecAnchorIndex
    {
        private const string LineColumnName = "라인";

        private static readonly Regex SectionHeadingLine =
            new(@"원본 DDL 라인\s*(\d+)", RegexOptions.Compiled);

        private static readonly Regex ParenthesizedLine =
            new(@"\(라인\s*(\d+)\)", RegexOptions.Compiled);

        private static readonly Regex HeadingLine =
            new(@"^#{2,6}\s+(.*)$", RegexOptions.Compiled);

        private static readonly Regex SeparatorRow =
            new(@"^\|[\s:|-]+\|\s*$", RegexOptions.Compiled);

        public static IReadOnlyList<SpecAnchor> Build(string? specMarkdown)
        {
            var anchors = new List<SpecAnchor>();
            if (string.IsNullOrWhiteSpace(specMarkdown)) return anchors;

            foreach (var (heading, header, row, isComment) in EnumerateLineBearingRows(specMarkdown))
            {
                var index = IndexOfLineColumn(header);
                var cells = SplitRow(row);
                if (index < 0 || index >= cells.Count) continue;
                if (!int.TryParse(cells[index].Trim(), out var line)) continue;

                anchors.Add(new SpecAnchor(
                    line,
                    $"표: {heading}",
                    row.Trim(),
                    isComment));
            }

            foreach (var raw in SplitLines(specMarkdown))
            {
                foreach (Match m in SectionHeadingLine.Matches(raw))
                {
                    anchors.Add(new SpecAnchor(int.Parse(m.Groups[1].Value), "절 제목", raw.Trim(), false));
                }

                foreach (Match m in ParenthesizedLine.Matches(raw))
                {
                    anchors.Add(new SpecAnchor(
                        int.Parse(m.Groups[1].Value), "셀 내 (라인 N)", raw.Trim(), false));
                }
            }

            return anchors;
        }

        public static int CountLineBearingTables(string? specMarkdown)
        {
            if (string.IsNullOrWhiteSpace(specMarkdown)) return 0;
            return EnumerateLineBearingRows(specMarkdown)
                .Select(r => r.Heading)
                .Distinct(StringComparer.Ordinal)
                .Count();
        }

        private static IEnumerable<(string Heading, List<string> Header, string Row, bool IsComment)>
            EnumerateLineBearingRows(string markdown)
        {
            var lines = SplitLines(markdown);
            var heading = "(제목 없음)";
            var inFence = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence) continue;

                var h = HeadingLine.Match(line);
                if (h.Success)
                {
                    heading = h.Groups[1].Value.Trim();
                    continue;
                }

                if (!line.StartsWith("|", StringComparison.Ordinal)) continue;
                if (i + 1 >= lines.Count || !SeparatorRow.IsMatch(lines[i + 1])) continue;

                var header = SplitRow(line);
                if (IndexOfLineColumn(header) < 0)
                {
                    i++;
                    continue;
                }

                var isComment = header.Any(c => c.Contains("주석", StringComparison.Ordinal));

                for (var j = i + 2; j < lines.Count && lines[j].StartsWith("|", StringComparison.Ordinal); j++)
                {
                    yield return (heading, header, lines[j], isComment);
                    i = j;
                }
            }
        }

        private static int IndexOfLineColumn(List<string> header) =>
            header.FindIndex(c => string.Equals(c.Trim(), LineColumnName, StringComparison.Ordinal));

        private static List<string> SplitRow(string row) =>
            row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

        private static List<string> SplitLines(string markdown) =>
            markdown.Replace("\r\n", "\n").Split('\n').ToList();
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SpecAnchorIndexTests"`
Expected: PASS (14 tests — `[Theory]` 6건 포함)

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/SpecAnchorIndex.cs tests/ReSet.Core.Tests/SpecAnchorIndexTests.cs
git commit -m "feat: Spec.md의 라인 앵커를 걷고 주석 앵커를 갈라 둔다

'라인' 칸을 헤더 이름으로 찾는다. 칸 위치가 표마다 달라(집합 술어는 2번째,
CASE 분기는 1번째) 위치를 상수로 박으면 '순서' 값을 라인 번호로 줍는다.

주석 표도 '라인' 칸을 갖는다(14개 SP 합 223행). 커버리지 근거로 세면 주석이
빽빽한 SP일수록 점수가 높게 나온다. 제목이 여섯 변종이고 컬럼명도 셋으로
갈려서 제목이 아니라 컬럼 구성으로 식별해 배제한다."
```

---

### Task 3: `CoverageMapComposer` — 4상태 판정

**Files:**
- Create: `src/ReSet.Core/Services/CoverageMapComposer.cs`
- Test: `tests/ReSet.Core.Tests/CoverageMapComposerTests.cs`

**Interfaces:**
- Consumes: `DdlStatement`·`DdlStatementEnumerator.Enumerate`/`Leaves`(Task 1), `SpecAnchor`·`SpecAnchorIndex.Build`/`CountLineBearingTables`(Task 2), `SpecExpectations.From`(`SpecExpectations.cs:172`), `SpDefinition`(`ReSet.Core.Models`)
- Produces:
  - `public enum CoverageState { Consistent, SpecMissing, ProseOnly, OutOfScope }`
  - `public sealed record StatementCoverage(DdlStatement Statement, CoverageState State, IReadOnlyList<int> ExtractorLines, IReadOnlyList<SpecAnchor> Anchors, IReadOnlyList<SpecAnchor> CommentAnchors, bool IsKnownUncovered)`
  - `public sealed record ObjectCoverage(string ObjectName, string DdlText, IReadOnlyList<StatementCoverage> Statements, int TableKindsRead)` — `Counts(CoverageState)`, `LeafCount` 편의 멤버 포함
  - `public static ObjectCoverage CoverageMapComposer.Compose(string objectName, SpDefinition spDef, string? specMarkdown)`

**상태 대응표** (설계서 §2와 같다):

| | 앵커 있음 | 앵커 없음 |
|---|---|---|
| **추출기 재료 있음** | `Consistent` 🟩 | `SpecMissing` 🟥 |
| **추출기 재료 없음** | `ProseOnly` 🟦 | `OutOfScope` 🟧 |

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/CoverageMapComposerTests.cs`:

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class CoverageMapComposerTests
    {
        private const string Ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DELETE FROM dbo.T WHERE PGNAME IN ('a', 'b')
    PRINT 'done'
END";
        // 라인 3 = DELETE, 라인 4 = PRINT

        private static SpDefinition Def() => new()
        {
            Schema = "dbo",
            Name = "P",
            DdlText = Ddl
        };

        [Fact]
        public void Compose_ExtractorMaterialAndAnchorBothPresent_ShouldBeConsistent()
        {
            const string spec = @"### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 | 연산 |
| :--- | :--- | :--- | :--- |
| DELETE 1 | 3 | PGNAME | IN |
";

            var delete = Compose(spec, "DeleteStatement");

            Assert.Equal(CoverageState.Consistent, delete.State);
        }

        [Fact]
        public void Compose_ExtractorMaterialButNoAnchor_ShouldBeSpecMissing()
        {
            // 명세서에 그 문장을 지목한 행이 없다 - 재생성으로 닫히는 결함.
            const string spec = "## 개요\n\n설명만 있고 표가 없다.\n";

            var delete = Compose(spec, "DeleteStatement");

            Assert.Equal(CoverageState.SpecMissing, delete.State);
        }

        [Fact]
        public void Compose_NoExtractorMaterialAndNoAnchor_ShouldBeOutOfScope()
        {
            // PRINT는 어떤 기계 확정 표의 관할도 아니다 - 도구를 고쳐야 닫힌다.
            const string spec = "## 개요\n";

            var print = Compose(spec, "PrintStatement");

            Assert.Equal(CoverageState.OutOfScope, print.State);
        }

        [Fact]
        public void Compose_AnchorWithoutExtractorMaterial_ShouldBeProseOnly()
        {
            // 문서가 PRINT 줄을 지목했지만 추출기가 낸 재료는 없다.
            const string spec = @"### 실행 의미 (기계 확정 — 수정 금지)

| 종류 | 라인 | 대상 | 확정 사실 |
| :--- | :--- | :--- | :--- |
| 기타 | 4 | PRINT | 로그를 남긴다 |
";

            var print = Compose(spec, "PrintStatement");

            Assert.Equal(CoverageState.ProseOnly, print.State);
        }

        [Fact]
        public void Compose_CommentAnchorAlone_ShouldNotMakeStatementConsistent()
        {
            // 주석이 붙어 있다고 그 문장이 문서화된 것이 아니다.
            const string spec = @"### 원본 주석 기록

| 라인 | 원본 주석 |
| :--- | :--- |
| 3 | -- 대상 삭제 |
";

            var delete = Compose(spec, "DeleteStatement");

            Assert.Equal(CoverageState.SpecMissing, delete.State);
            Assert.NotEmpty(delete.CommentAnchors);
            Assert.Empty(delete.Anchors);
        }

        [Fact]
        public void Compose_ContainerStatements_ShouldNotBeCounted()
        {
            var coverage = CoverageMapComposer.Compose("dbo.P", Def(), "## 개요\n");

            Assert.All(coverage.Statements, s => Assert.False(s.Statement.IsContainer));
            Assert.Equal(coverage.LeafCount, coverage.Statements.Count);
        }

        [Fact]
        public void Compose_Merge_ShouldBeMarkedAsKnownUncovered()
        {
            const string mergeDdl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    MERGE dbo.T AS D USING dbo.S AS S ON D.A = S.A
    WHEN MATCHED THEN UPDATE SET D.B = S.B;
END";
            var def = new SpDefinition { Schema = "dbo", Name = "P", DdlText = mergeDdl };

            var coverage = CoverageMapComposer.Compose("dbo.P", def, "## 개요\n");
            var merge = coverage.Statements.Single(s => s.Statement.StatementType == "MergeStatement");

            Assert.True(merge.IsKnownUncovered);
            Assert.Equal(CoverageState.OutOfScope, merge.State);
        }

        [Fact]
        public void Compose_ShouldReportTableKindsRead()
        {
            const string spec = @"### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 |
| :--- | :--- | :--- |
| DELETE 1 | 3 | PGNAME |
";

            Assert.Equal(1, CoverageMapComposer.Compose("dbo.P", Def(), spec).TableKindsRead);
        }

        private static StatementCoverage Compose(string spec, string statementType) =>
            CoverageMapComposer.Compose("dbo.P", Def(), spec)
                .Statements.Single(s => s.Statement.StatementType == statementType);
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CoverageMapComposerTests"`
Expected: 컴파일 실패 — `CoverageMapComposer`가 없다.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Core/Services/CoverageMapComposer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>설계서 §2의 4상태.</summary>
    public enum CoverageState
    {
        /// <summary>🟩 추출기 재료가 있고 명세서도 지목했다.</summary>
        Consistent,

        /// <summary>🟥 추출기 재료가 있는데 명세서가 지목하지 않았다. 재생성으로 닫힌다.</summary>
        SpecMissing,

        /// <summary>🟦 명세서는 지목했는데 추출기 재료가 없다. 검증 안 된 산문이다.</summary>
        ProseOnly,

        /// <summary>🟧 둘 다 없다. 도구를 고쳐야 닫히는 사각지대.</summary>
        OutOfScope
    }

    public sealed record StatementCoverage(
        DdlStatement Statement,
        CoverageState State,
        IReadOnlyList<int> ExtractorLines,
        IReadOnlyList<SpecAnchor> Anchors,
        IReadOnlyList<SpecAnchor> CommentAnchors,
        bool IsKnownUncovered);

    public sealed record ObjectCoverage(
        string ObjectName,
        string DdlText,
        IReadOnlyList<StatementCoverage> Statements,
        int TableKindsRead)
    {
        public int LeafCount => Statements.Count;

        public int Count(CoverageState state) => Statements.Count(s => s.State == state);
    }

    /// <summary>
    /// 좌표계(잎 문장)에 추출기 재료와 문서 앵커 두 축을 겹쳐 4상태를 확정한다.
    ///
    /// [주석 앵커는 상태를 바꾸지 않는다] SpecAnchorIndex가 갈라 준 IsCommentAnchor는
    /// 판정에서 빠지고 근거 패널용으로만 실린다. 이유는 SpecAnchorIndex 문서 참고.
    /// </summary>
    public static class CoverageMapComposer
    {
        public static ObjectCoverage Compose(string objectName, SpDefinition spDef, string? specMarkdown)
        {
            ArgumentNullException.ThrowIfNull(spDef);

            var ddl = spDef.DdlText ?? string.Empty;
            var leaves = DdlStatementEnumerator.Leaves(DdlStatementEnumerator.Enumerate(ddl));
            var allAnchors = SpecAnchorIndex.Build(specMarkdown);
            var extractorLines = ExtractorFactLines(spDef);
            var knownUncoveredLines = DmlScopeExtractor.ExtractUncoveredStatements(ddl)
                .Select(u => u.Line)
                .ToHashSet();

            var statements = new List<StatementCoverage>(leaves.Count);
            foreach (var leaf in leaves)
            {
                bool InRange(int line) => line >= leaf.StartLine && line <= leaf.EndLine;

                var mine = allAnchors.Where(a => InRange(a.Line)).ToList();
                var factAnchors = mine.Where(a => !a.IsCommentAnchor).ToList();
                var commentAnchors = mine.Where(a => a.IsCommentAnchor).ToList();
                var facts = extractorLines.Where(InRange).Distinct().OrderBy(l => l).ToList();

                var state = (facts.Count > 0, factAnchors.Count > 0) switch
                {
                    (true, true) => CoverageState.Consistent,
                    (true, false) => CoverageState.SpecMissing,
                    (false, true) => CoverageState.ProseOnly,
                    (false, false) => CoverageState.OutOfScope
                };

                statements.Add(new StatementCoverage(
                    leaf,
                    state,
                    facts,
                    factAnchors,
                    commentAnchors,
                    knownUncoveredLines.Any(InRange)));
            }

            return new ObjectCoverage(
                objectName,
                ddl,
                statements,
                SpecAnchorIndex.CountLineBearingTables(specMarkdown));
        }

        /// <summary>
        /// SpecExpectations가 낸 재료 중 <b>줄 번호를 가진 것</b>을 전부 모은다.
        /// 파생 테이블 정의(DerivedColumnDefinition)는 줄 번호가 없어 여기 들어오지
        /// 못한다 - 설계서 미확정 사항 5번의 실측 대상이다.
        /// </summary>
        private static IReadOnlyList<int> ExtractorFactLines(SpDefinition spDef)
        {
            var expectations = SpecExpectations.From(spDef);
            if (expectations == null) return Array.Empty<int>();

            var lines = new List<int>();
            lines.AddRange(expectations.DmlScopeFacts.Select(f => f.Line));
            lines.AddRange(expectations.SetPredicates.Select(f => f.Line));
            lines.AddRange(expectations.LockHints.Select(f => f.Line));
            lines.AddRange(expectations.CaseBranches.Select(f => f.Line));
            lines.AddRange(expectations.ReferencedFunctionCalls.Select(f => f.Line));
            lines.AddRange(expectations.RoundingCalls.Select(f => f.Line));

            // ExecutionSemanticFact.Line은 string이다(ExecutionSemanticsFacts.cs:12).
            // 숫자가 아닌 값("-" 등)은 조용히 버린다.
            foreach (var fact in expectations.ExecutionSemantics)
            {
                if (int.TryParse(fact.Line, out var line)) lines.Add(line);
            }

            return lines;
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CoverageMapComposerTests"`
Expected: PASS (8 tests)

두 곳이 걸릴 수 있다.

1. `DmlScopeExtractor.ExtractUncoveredStatements`의 정확한 시그니처를 확인한다: `grep -n "ExtractUncoveredStatements" src/ReSet.Core/Services/DmlScopeExtractor.cs`. 인자가 `string`이 아니면 맞춰 고친다.
2. `SpecExpectations.From`이 `SpDefinition`의 `StaticAnalysis`를 요구해 `null`을 낼 수 있다. 그러면 테스트의 `Def()`에 `StaticAnalysis = new()`를 채운다.

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/CoverageMapComposer.cs tests/ReSet.Core.Tests/CoverageMapComposerTests.cs
git commit -m "feat: 잎 문장에 추출기 재료와 문서 앵커를 겹쳐 4상태를 확정한다

🟥(추출기는 냈는데 명세서에 없음)과 🟧(둘 다 없음)을 가른다. 전자는 재생성으로
닫히고 후자는 도구를 고쳐야 닫히므로 조치가 다르다 - 지금 그 구분은 감사에서
사람이 손으로 하고 있다.

주석 앵커는 상태를 바꾸지 않고 근거용으로만 싣는다."
```

---

### Task 4: 실측 게이트 — 숫자를 먼저 본다

**Files:**
- Create: `tests/ReSet.Core.Tests/CoverageMapProbeTests.cs`
- Modify: `docs/superpowers/specs/2026-08-24-ddl-coverage-map-design.md` (「미확정 사항」 1·2·5를 실측으로 닫는다)

**Interfaces:**
- Consumes: `CoverageMapComposer.Compose`(Task 3)
- Produces: 산출물 코드 없음. **실측 수치와 그 수치로 갱신된 설계서.**

이 태스크는 설계서 §「위험」이 지정한 게이트다. 🟧이 얼마나 나오는지 모르는 채로 HTML에 투자하지 않는다.

- [ ] **Step 1: 실측 테스트를 쓴다**

`tests/ReSet.Core.Tests/CoverageMapProbeTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 설계서 §「위험」의 게이트. 🟧 비율을 실측해 출력에 남긴다.
    /// output/이 .gitignore 대상이라 CI에서는 건너뛴다.
    /// </summary>
    public class CoverageMapProbeTests
    {
        private readonly ITestOutputHelper _output;

        public CoverageMapProbeTests(ITestOutputHelper output) => _output = output;

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "output")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? string.Empty;
        }

        [SkippableTheory]
        [InlineData("dbo.UP_UTIL_SETTLE_EXCEPTION_PROC")]
        [InlineData("dbo.UP_UTIL_SETTLE_COMM_UPD")]
        [InlineData("dbo.UP_UTIL_SETTLE_CANCEL_INS")]
        public void Probe_RealProcedures_ShouldReportStateDistribution(string objectName)
        {
            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), "output/ 디렉터리를 찾지 못했다 - 실측 건너뜀");

            var baseDir = Path.Combine(root, "output", "Procedures", objectName);
            var metaPath = Path.Combine(baseDir, "raw", "metadata.json");
            var specPath = Path.Combine(baseDir, "docs", "Spec.md");
            Skip.IfNot(File.Exists(metaPath) && File.Exists(specPath),
                $"{objectName}의 산출물이 없다 - 실측 건너뜀");

            var spDef = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(metaPath));
            Assert.NotNull(spDef);

            var coverage = CoverageMapComposer.Compose(
                objectName, spDef!, File.ReadAllText(specPath));

            _output.WriteLine($"=== {objectName} ===");
            _output.WriteLine($"DDL 줄수      : {(spDef!.DdlText ?? string.Empty).Split('\n').Length}");
            _output.WriteLine($"읽은 표 종수  : {coverage.TableKindsRead}");
            _output.WriteLine($"잎 문장       : {coverage.LeafCount}");
            _output.WriteLine($"🟩 Consistent : {coverage.Count(CoverageState.Consistent)}");
            _output.WriteLine($"🟥 SpecMissing: {coverage.Count(CoverageState.SpecMissing)}");
            _output.WriteLine($"🟦 ProseOnly  : {coverage.Count(CoverageState.ProseOnly)}");
            _output.WriteLine($"🟧 OutOfScope : {coverage.Count(CoverageState.OutOfScope)}");
            _output.WriteLine("");
            _output.WriteLine("🟧 유형별:");
            foreach (var g in coverage.Statements
                         .Where(s => s.State == CoverageState.OutOfScope)
                         .GroupBy(s => s.Statement.StatementType)
                         .OrderByDescending(g => g.Count()))
            {
                _output.WriteLine($"  {g.Count(),4}  {g.Key}");
            }
            _output.WriteLine("");
            _output.WriteLine("🟥 자리:");
            foreach (var s in coverage.Statements.Where(s => s.State == CoverageState.SpecMissing))
            {
                _output.WriteLine($"  줄 {s.Statement.StartLine}-{s.Statement.EndLine}  {s.Statement.StatementType}");
            }

            // 좌표계가 무너지지 않았는지만 단언한다. 분포는 관측 대상이지 계약이 아니다.
            Assert.True(coverage.LeafCount > 0, "잎 문장이 하나도 없다 - 좌표계가 무너졌다");
        }
    }
}
```

- [ ] **Step 2: 돌려서 숫자를 본다**

Run: `dotnet test --filter "FullyQualifiedName~CoverageMapProbeTests" --logger "console;verbosity=detailed"`

`output/`이 있는 트리에서 돌려야 한다. 워크트리에는 `output/`이 없으므로 본 체크아웃에서 돌리거나, 본 체크아웃의 `output/`을 가리키도록 `RepoRoot()`가 올라가게 둔다.

- [ ] **Step 3: 수치를 설계서에 적는다**

설계서 「미확정 사항」의 1·2·5를 실측으로 닫고, 그 자리에 **관측된 수치**를 적는다. 3번은 Task 1이 유형 목록을 없애 이미 닫혔으므로 그 사실도 적는다. 최소한 다음을 남긴다.

- `EXCEPTION_PROC`의 잎 문장 수와 4상태 분포
- 🟧 유형 분포 상위 목록
- 🟥이 난 자리(있다면 각각의 줄 번호와 문장 유형)
- `SET` 대입이 실제로 관할 밖인지(미확정 2)
- 파생 테이블만 걸린 문장이 실제로 나왔는지(미확정 5)

- [ ] **Step 4: 🟧 백로그를 적는다**

🟧 유형 중 **기계 확정 표로 담을 수 있는데 아직 안 담은 것**을 골라 설계서 §「위험」 아래에 목록으로 남긴다. 이것이 다음 회차의 재료 확장 작업 목록이 된다.

- [ ] **Step 5: 커밋한다**

```bash
git add tests/ReSet.Core.Tests/CoverageMapProbeTests.cs docs/superpowers/specs/2026-08-24-ddl-coverage-map-design.md
git commit -m "test: 실물 SP의 4상태 분포를 실측하고 설계서 미확정 사항을 닫는다

설계서 §위험이 지정한 게이트다. 🟧이 얼마나 나오는지 모르는 채로 HTML에
투자하지 않는다. 관측된 수치와 🟧 유형 분포를 설계서에 적고, 표로 담을 수
있는 것을 다음 회차 백로그로 남긴다."
```

**게이트 판단:** 🟧이 잎 문장의 대다수를 차지하면 사람에게 보고하고 §「위험」의 3단계(외부 공개 여부와 프레이밍)를 상의한 뒤 Task 5로 넘어간다. 그 판단은 코드가 아니라 사람이 한다.

---

### Task 5: `CoverageMapHtmlWriter` — 자립형 HTML 한 장

**Files:**
- Create: `src/ReSet.Core/Services/CoverageMapHtmlWriter.cs`
- Test: `tests/ReSet.Core.Tests/CoverageMapHtmlWriterTests.cs`

**Interfaces:**
- Consumes: `ObjectCoverage`·`CoverageState`(Task 3)
- Produces: `public static string CoverageMapHtmlWriter.Render(IReadOnlyList<ObjectCoverage> objects, string title)`

**렌더 규약**(설계서 §3):
- 외부 CDN·폰트·스크립트 금지. CSS·JS 전부 인라인.
- 색만으로 구분하지 않는다 — 기호 병기(`■` 🟩 · `▲` 🟥 · `◆` 🟦 · `·` 🟧).
- 왼쪽 객체 목록은 **🟥 많은 순** 정렬.
- 줄 클릭 시 근거 패널에 앵커의 `RowText`를 원문 그대로.
- 어떤 잎 문장에도 속하지 않은 줄은 무채색.
- 라이트·다크 양쪽 대응.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/CoverageMapHtmlWriterTests.cs`:

```csharp
using System.Collections.Generic;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class CoverageMapHtmlWriterTests
    {
        private static ObjectCoverage Sample(string name, int specMissing)
        {
            var statements = new List<StatementCoverage>();
            for (var i = 0; i < specMissing; i++)
            {
                statements.Add(new StatementCoverage(
                    new DdlStatement(10 + i, 10 + i, "UpdateStatement", 1, false),
                    CoverageState.SpecMissing,
                    new[] { 10 + i },
                    new List<SpecAnchor>(),
                    new List<SpecAnchor>(),
                    false));
            }

            statements.Add(new StatementCoverage(
                new DdlStatement(1, 1, "DeleteStatement", 1, false),
                CoverageState.Consistent,
                new[] { 1 },
                new List<SpecAnchor> { new(1, "표: 집합 술어", "| DELETE 1 | 1 | PGNAME |", false) },
                new List<SpecAnchor>(),
                false));

            return new ObjectCoverage(name, "line1\nline2\nline3\n", statements, 3);
        }

        [Fact]
        public void Render_ShouldNotReferenceAnyExternalResource()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            Assert.DoesNotContain("http://", html);
            Assert.DoesNotContain("https://", html);
            Assert.DoesNotContain("<script src", html);
            Assert.DoesNotContain("<link rel=\"stylesheet\"", html);
        }

        [Fact]
        public void Render_ShouldSortObjectsBySpecMissingDescending()
        {
            var html = CoverageMapHtmlWriter.Render(
                new[] { Sample("dbo.Few", 1), Sample("dbo.Many", 5) }, "T");

            Assert.True(html.IndexOf("dbo.Many") < html.IndexOf("dbo.Few"),
                "🟥이 많은 객체가 먼저 와야 한다");
        }

        [Fact]
        public void Render_ShouldCarrySymbolsNotOnlyColors()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            Assert.Contains("■", html);
            Assert.Contains("▲", html);
            Assert.Contains("◆", html);
        }

        [Fact]
        public void Render_ShouldEmbedAnchorRowTextAsEvidence()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 0) }, "T");

            Assert.Contains("PGNAME", html);
        }

        [Fact]
        public void Render_ShouldEscapeHtmlInDdl()
        {
            var coverage = new ObjectCoverage(
                "dbo.A", "SELECT * FROM T WHERE A < 1 AND B > 2\n",
                new List<StatementCoverage>(), 0);

            var html = CoverageMapHtmlWriter.Render(new[] { coverage }, "T");

            Assert.Contains("&lt;", html);
            Assert.Contains("&gt;", html);
        }

        [Fact]
        public void Render_ShouldReportTableKindsRead()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            Assert.Contains("3", html);
        }

        [Fact]
        public void Render_ShouldSupportDarkMode()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            Assert.Contains("prefers-color-scheme", html);
        }

        [Fact]
        public void Render_ShouldFoldOutOfScopeByStatementType()
        {
            // 설계서 §2: 접지 않으면 SET 대입 수십 개가 목록을 덮어 신호가 죽는다.
            var statements = new List<StatementCoverage>();
            for (var i = 0; i < 12; i++)
            {
                statements.Add(new StatementCoverage(
                    new DdlStatement(i + 1, i + 1, "SetVariableStatement", 1, false),
                    CoverageState.OutOfScope,
                    System.Array.Empty<int>(),
                    new List<SpecAnchor>(), new List<SpecAnchor>(), false));
            }
            statements.Add(new StatementCoverage(
                new DdlStatement(20, 20, "ExecuteStatement", 1, false),
                CoverageState.OutOfScope,
                System.Array.Empty<int>(),
                new List<SpecAnchor>(), new List<SpecAnchor>(), false));

            var html = CoverageMapHtmlWriter.Render(
                new[] { new ObjectCoverage("dbo.A", "x\n", statements, 1) }, "T");

            // 유형과 개수가 함께 보여야 한다.
            Assert.Contains("SetVariableStatement", html);
            Assert.Contains("12", html);
            Assert.Contains("ExecuteStatement", html);
        }

        [Fact]
        public void Render_KnownUncoveredMerge_ShouldBeLabelledSeparately()
        {
            // 설계서 §2: 몰라서 빈 것과 알고 비운 것은 다른 사실이다.
            var merge = new StatementCoverage(
                new DdlStatement(5, 9, "MergeStatement", 1, false),
                CoverageState.OutOfScope,
                System.Array.Empty<int>(),
                new List<SpecAnchor>(), new List<SpecAnchor>(),
                IsKnownUncovered: true);

            var html = CoverageMapHtmlWriter.Render(
                new[] { new ObjectCoverage("dbo.A", "a\nb\nc\nd\ne\nf\ng\nh\ni\n", new[] { merge }, 1) },
                "T");

            Assert.Contains("알려진 사각지대", html);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CoverageMapHtmlWriterTests"`
Expected: 컴파일 실패 — `CoverageMapHtmlWriter`가 없다.

- [ ] **Step 3: 구현을 쓴다**

`src/ReSet.Core/Services/CoverageMapHtmlWriter.cs`를 만든다. 뼈대는 아래와 같고, 스타일 세부는 재량이되 위 일곱 단언을 전부 만족해야 한다.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 커버리지 판정을 자립형 HTML 한 장으로 렌더한다.
    ///
    /// [왜 외부 자원을 하나도 안 쓰는가] 이 파일은 메일로 넘겨지고 망 분리 환경에서
    /// 열린다. CDN 하나가 걸리면 회의실에서 빈 화면이 뜬다.
    ///
    /// [왜 색만으로 구분하지 않는가] 색각 이상이 있는 리뷰어에게 빨강과 초록은 같은
    /// 회색이다. 상태마다 기호를 병기한다.
    ///
    /// [보안] 이 HTML에는 원본 SP 전문이 그대로 실린다. 로컬 파일로만 쓰고,
    /// 외부 호스팅에 올릴지는 사람이 그때 판단한다. 이 클래스는 파일만 만든다.
    /// </summary>
    public static class CoverageMapHtmlWriter
    {
        private static readonly IReadOnlyDictionary<CoverageState, (string Symbol, string Label, string Css)> Legend =
            new Dictionary<CoverageState, (string, string, string)>
            {
                [CoverageState.Consistent] = ("■", "정합", "st-ok"),
                [CoverageState.SpecMissing] = ("▲", "명세서 결함", "st-missing"),
                [CoverageState.ProseOnly] = ("◆", "산문만", "st-prose"),
                [CoverageState.OutOfScope] = ("·", "관할 밖", "st-out")
            };

        public static string Render(IReadOnlyList<ObjectCoverage> objects, string title)
        {
            ArgumentNullException.ThrowIfNull(objects);

            var ordered = objects
                .OrderByDescending(o => o.Count(CoverageState.SpecMissing))
                .ThenBy(o => o.ObjectName, StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"ko\"><head><meta charset=\"utf-8\">");
            sb.AppendLine($"<title>{WebUtility.HtmlEncode(title)}</title>");
            sb.AppendLine("<style>");
            AppendStyle(sb);
            sb.AppendLine("</style></head><body>");

            AppendSummary(sb, ordered);
            AppendObjectList(sb, ordered);
            foreach (var o in ordered) AppendObjectPane(sb, o);

            sb.AppendLine("<div id=\"evidence\"></div>");
            sb.AppendLine("<script>");
            AppendScript(sb);
            sb.AppendLine("</script></body></html>");
            return sb.ToString();
        }

        /// <summary>
        /// 줄 번호(1-based) → 그 줄을 덮는 잎 문장. 어떤 잎에도 안 속한 줄은 없다(무채색).
        /// 컨테이너를 뺐으므로 잎끼리는 겹치지 않는다.
        /// </summary>
        private static Dictionary<int, StatementCoverage> BuildLineMap(ObjectCoverage o)
        {
            var map = new Dictionary<int, StatementCoverage>();
            foreach (var s in o.Statements)
            {
                for (var line = s.Statement.StartLine; line <= s.Statement.EndLine; line++)
                {
                    map[line] = s;
                }
            }
            return map;
        }

        private static void AppendObjectPane(StringBuilder sb, ObjectCoverage o)
        {
            var map = BuildLineMap(o);
            var lines = o.DdlText.Replace("\r\n", "\n").Split('\n');

            sb.AppendLine($"<section class=\"pane\" id=\"pane-{WebUtility.HtmlEncode(o.ObjectName)}\">");
            sb.AppendLine($"<h2>{WebUtility.HtmlEncode(o.ObjectName)}</h2>");
            AppendOutOfScopeFold(sb, o);
            sb.AppendLine("<pre class=\"ddl\">");

            for (var i = 0; i < lines.Length; i++)
            {
                var lineNo = i + 1;
                var text = WebUtility.HtmlEncode(lines[i]);

                if (!map.TryGetValue(lineNo, out var s))
                {
                    sb.AppendLine($"<span class=\"row\"><i class=\"ln\">{lineNo}</i> <i class=\"sym\">&nbsp;</i>{text}</span>");
                    continue;
                }

                var (symbol, _, css) = Legend[s.State];
                var evidence = WebUtility.HtmlEncode(
                    string.Join("\n", s.Anchors.Select(a => $"[{a.Source}] {a.RowText}")));
                var comments = WebUtility.HtmlEncode(
                    string.Join("\n", s.CommentAnchors.Select(a => $"[{a.Source}] {a.RowText}")));
                var known = s.IsKnownUncovered ? " data-known=\"알려진 사각지대\"" : string.Empty;

                sb.AppendLine(
                    $"<span class=\"row {css}\" data-state=\"{s.State}\" data-evidence=\"{evidence}\" " +
                    $"data-comment=\"{comments}\"{known}>" +
                    $"<i class=\"ln\">{lineNo}</i> <i class=\"sym\">{symbol}</i>{text}</span>");
            }

            sb.AppendLine("</pre></section>");
        }

        /// <summary>🟧을 문장 유형별로 접는다. 접지 않으면 SET 대입 수십 개가 목록을 덮는다.</summary>
        private static void AppendOutOfScopeFold(StringBuilder sb, ObjectCoverage o)
        {
            var groups = o.Statements
                .Where(s => s.State == CoverageState.OutOfScope)
                .GroupBy(s => s.Statement.StatementType)
                .OrderByDescending(g => g.Count())
                .ToList();
            if (groups.Count == 0) return;

            var total = groups.Sum(g => g.Count());
            sb.AppendLine($"<details class=\"fold\"><summary>· 관할 밖 {total}</summary><ul>");
            foreach (var g in groups)
            {
                var known = g.Any(s => s.IsKnownUncovered) ? " <em>알려진 사각지대</em>" : string.Empty;
                sb.AppendLine(
                    $"<li>{g.Count()} &middot; {WebUtility.HtmlEncode(g.Key)}{known}</li>");
            }
            sb.AppendLine("</ul></details>");
        }

        // AppendStyle: :root와 @media (prefers-color-scheme: dark) 양쪽에 팔레트를 정의한다.
        //   .st-ok/.st-missing/.st-prose/.st-out의 왼쪽 테두리 색과 .row.hidden{display:none}.
        // AppendSummary: 잎 문장 총계·4상태 집계·읽은 표 종수·범례(기호 병기)·🟥만/🟧만 필터 버튼.
        // AppendObjectList: 왼쪽 목록. 객체마다 4상태 비율 막대와 🟥 개수(이미 정렬된 순서대로).
        // AppendScript: .row 클릭 → #evidence에 data-evidence를 넣고, data-comment가 비어 있지
        //   않으면 "원본 주석(참고)" 절을 덧붙인다. data-known이 있으면 그 문구를 함께 띄운다.
        //   필터 버튼 → data-state가 대상이 아닌 .row에 hidden 클래스를 토글한다.
    }
}
```

구현 시 주의:
- `WebUtility.HtmlEncode`를 DDL 본문·`RowText`·객체 이름에 **빠짐없이** 적용한다. 원본 SQL에 `<`·`>`가 흔하다. `data-*` 속성에 넣는 값도 반드시 인코딩한다 — 안 하면 표 원문의 따옴표가 속성을 깨뜨린다.
- `data-comment`는 **판정에 쓰지 않는다.** 근거 패널에 "원본 주석(참고)"으로만 띄운다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CoverageMapHtmlWriterTests"`
Expected: PASS (9 tests)

> **[2026-08-24 정정]** 이 줄은 `7`이라고 적혀 있었다. Step 1에 테스트 둘
> (`Render_ShouldFoldOutOfScopeByStatementType`·`Render_KnownUncoveredMerge_ShouldBeLabelledSeparately`)을
> 나중에 더하면서 개수를 안 고친 것이다. Task 5 워커가 실행 중에 잡았다.
> 낡은 기대 개수는 워커가 테스트를 덜 썼는지 더 썼는지 판단할 근거를 망가뜨린다.

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/CoverageMapHtmlWriter.cs tests/ReSet.Core.Tests/CoverageMapHtmlWriterTests.cs
git commit -m "feat: 커버리지 판정을 자립형 HTML 한 장으로 렌더한다

외부 자원을 하나도 쓰지 않는다 - 메일로 넘어가고 망 분리 환경에서 열린다.
색만으로 구분하지 않고 기호를 병기한다. 줄을 클릭하면 어느 표의 어느 행이
그 문장을 앵커했는지 표 원문 그대로 뜬다 - 색칠은 요약이고 이 패널이 증거다."
```

---

### Task 6: CLI 배선 — `--coverage-map`

**Files:**
- Modify: `src/ReSet.Cli/CliArgs.cs`
- Modify: `src/ReSet.Cli/Program.cs:40-83`(파싱 루프), `src/ReSet.Cli/Program.cs:218` 직후(분기)
- Test: `tests/ReSet.Core.Tests/CliArgsTests.cs`(기존 파일에 추가)

**Interfaces:**
- Consumes: `CoverageMapComposer.Compose`(Task 3), `CoverageMapHtmlWriter.Render`(Task 5)
- Produces: `CliArgs.CoverageMapTarget` (string?)

**배치 위치가 중요하다.** `--extract-snapshot` 분기는 DB 연결 **뒤**에 있다(`Program.cs:415`). 커버리지 맵은 DB가 필요 없으므로 `var outputDir = ...`(`Program.cs:218`) **직후**, 세션 복원·로그인 흐름보다 앞에 둔다. DB 없는 환경에서 돌아야 한다는 것이 이 도구의 핵심 성질이다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/CliArgsTests.cs`에 추가한다. (기존 파일의 네임스페이스·using을 그대로 따른다. 파싱 메서드가 `internal`이면 기존 테스트가 쓰는 접근 방식을 그대로 쓴다 — 먼저 `head -30 tests/ReSet.Core.Tests/CliArgsTests.cs`로 확인한다.)

기존 테스트가 `Program.ParseCommandLineArgs(args)`를 직접 부른다(`CliArgsTests.cs:30`). 같은 방식을 쓴다.

```csharp
        [Fact]
        public void ParseCommandLineArgs_CoverageMap_ShouldCaptureTarget()
        {
            CliArgs result = Program.ParseCommandLineArgs(new[] { "--coverage-map", "POQSettlePrco20" });

            Assert.Equal("POQSettlePrco20", result.CoverageMapTarget);
        }

        [Fact]
        public void ParseCommandLineArgs_CoverageMap_ShouldBeBatchMode()
        {
            // 커버리지 맵은 TUI 로그인 흐름을 타면 안 된다.
            CliArgs result = Program.ParseCommandLineArgs(new[] { "--coverage-map", "dbo.UP_X" });

            Assert.True(result.IsBatchMode);
        }

        [Fact]
        public void ParseCommandLineArgs_CoverageMapWithoutValue_ShouldLeaveTargetNull()
        {
            CliArgs result = Program.ParseCommandLineArgs(new[] { "--coverage-map" });

            Assert.Null(result.CoverageMapTarget);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CliArgsTests"`
Expected: 컴파일 실패 — `CoverageMapTarget`이 없다.

- [ ] **Step 3: `CliArgs`에 속성을 더한다**

`src/ReSet.Cli/CliArgs.cs`:

```csharp
        public string? ExtractSnapshotPath { get; set; }

        /// <summary>--coverage-map의 대상. Job 이름이거나 객체 이름이다.
        /// DB·AI 없이 output/ 산출물만 읽는다.</summary>
        public string? CoverageMapTarget { get; set; }

        public bool IsBatchMode => AnalyzeAll || TargetProcedures.Count > 0 || GeneratePolicy
            || !string.IsNullOrEmpty(ExtractSnapshotPath)
            || !string.IsNullOrEmpty(CoverageMapTarget);
```

- [ ] **Step 4: 파싱 분기를 더한다**

`src/ReSet.Cli/Program.cs`의 `--extract-snapshot` 분기 바로 아래(`Program.cs:83` 부근):

```csharp
                else if (arg.Equals("--coverage-map", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    cliArgs.CoverageMapTarget = args[++i];
                }
```

- [ ] **Step 5: 실행 분기를 더한다**

`src/ReSet.Cli/Program.cs`의 `var outputDir = configuration["OutputSettings:Directory"] ?? "./output";`(`Program.cs:218`) **직후**:

```csharp
            // 커버리지 맵 모드 - DB·AI 없이 output/ 산출물만 읽는다. 그래서 로그인
            // 흐름보다 앞에 둔다(--extract-snapshot과 달리 연결이 필요 없다).
            if (!string.IsNullOrEmpty(cliArgs.CoverageMapTarget))
            {
                var written = CoverageMapCommand.Run(outputDir, cliArgs.CoverageMapTarget);
                if (written == null)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]커버리지 맵 대상을 찾지 못했습니다: {Markup.Escape(cliArgs.CoverageMapTarget)}[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]커버리지 맵 생성 완료:[/] {Markup.Escape(written)}");
                return;
            }
```

- [ ] **Step 6: `CoverageMapCommand`를 만든다**

**실측으로 확인한 경로 규약**(2026-08-24):

- `{outputDir}/Jobs/{job}/raw/prompt-context.md`의 `^Filename:` 행이 **소비 명세서 집합**이다(`Feedback_Log.txt` 제외). `POQSettlePrco20`은 13행 중 12개다.
- 각 소비 명세서의 `raw/dependency-manifest.json` → `Nodes[]`의 합집합이 **참조 폐포**다. 노드는 `Key`·`Status`·`SpecPath`를 갖는다.
- `SpecPath`는 **그 객체 디렉터리 기준 상대경로**다(예: `../../Functions/dbo.X/docs/Spec.md`). 로컬·외부 객체 모두 같은 규약이고, 둘 다 `raw/metadata.json`을 갖는다(실측 확인).

`src/ReSet.Cli/CoverageMapCommand.cs`(신규):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Spectre.Console;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Cli
{
    /// <summary>
    /// DB·AI 없이 output/ 산출물만으로 커버리지 맵을 낸다.
    ///
    /// [왜 빠진 객체를 화면에 남기는가] 폐포 31개 중 몇 개가 조용히 빠지면 맵은
    /// 멀쩡해 보이는데 대조 범위가 줄어든 것을 아무도 모른다. 감사 축 A와 대상
    /// 정의를 맞춰 둔 이유가 무너진다.
    /// </summary>
    public static class CoverageMapCommand
    {
        /// <summary>산출한 HTML 경로. 대상을 못 찾으면 null.</summary>
        public static string? Run(string outputDir, string target)
        {
            var jobDir = Path.Combine(outputDir, "Jobs", target);
            if (Directory.Exists(jobDir)) return RunJob(outputDir, jobDir, target);

            foreach (var kind in new[] { "Procedures", "Functions" })
            {
                var objectDir = Path.Combine(outputDir, kind, target);
                if (!Directory.Exists(objectDir)) continue;

                var coverage = LoadObject(objectDir, target);
                if (coverage == null) return null;

                var path = Path.Combine(objectDir, "docs", "CoverageMap.html");
                Write(path, new[] { coverage }, target);
                return path;
            }

            return null;
        }

        private static string? RunJob(string outputDir, string jobDir, string job)
        {
            var contextPath = Path.Combine(jobDir, "raw", "prompt-context.md");
            if (!File.Exists(contextPath))
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(contextPath)}이 없어 소비 명세서 집합을 정할 수 없습니다.[/]");
                return null;
            }

            var consumed = File.ReadAllLines(contextPath)
                .Where(l => l.StartsWith("Filename:", StringComparison.Ordinal))
                .Select(l => l["Filename:".Length..].Trim())
                .Where(n => n.Length > 0 && !n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 참조 폐포 - 소비 명세서 각각의 Nodes[] 합집합. 감사 축 A와 같은 정의다.
            var objectDirs = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in consumed)
            {
                var dir = Path.Combine(outputDir, "Procedures", name);
                if (!Directory.Exists(dir))
                {
                    AnsiConsole.MarkupLine($"[yellow]건너뜀: {Markup.Escape(name)} - 산출물 디렉터리가 없습니다.[/]");
                    continue;
                }

                objectDirs[name] = dir;
                foreach (var (key, nodeDir) in ClosureOf(dir))
                {
                    objectDirs[key] = nodeDir;
                }
            }

            var covered = new List<ObjectCoverage>();
            foreach (var (name, dir) in objectDirs)
            {
                var coverage = LoadObject(dir, name);
                if (coverage == null) continue;
                covered.Add(coverage);
            }

            AnsiConsole.MarkupLine(
                $"[grey]소비 명세서 {consumed.Count}개 → 폐포 {objectDirs.Count}개 → 대조 {covered.Count}개[/]");

            var path = Path.Combine(jobDir, "coverage", "CoverageMap.html");
            Write(path, covered, job);
            return path;
        }

        private static IEnumerable<(string Key, string Dir)> ClosureOf(string objectDir)
        {
            var manifestPath = Path.Combine(objectDir, "raw", "dependency-manifest.json");
            if (!File.Exists(manifestPath)) yield break;

            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("Nodes", out var nodes)) yield break;

            foreach (var node in nodes.EnumerateArray())
            {
                var status = node.TryGetProperty("Status", out var s) ? s.GetString() : null;
                var key = node.TryGetProperty("Key", out var k) ? k.GetString() : null;
                var specPath = node.TryGetProperty("SpecPath", out var p) ? p.GetString() : null;
                if (key == null) continue;

                if (!string.Equals(status, "Succeeded", StringComparison.Ordinal))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]건너뜀: {Markup.Escape(key)} - 상태가 {Markup.Escape(status ?? "없음")}입니다.[/]");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(specPath)) continue;

                // SpecPath는 객체 디렉터리 기준이고 .../docs/Spec.md로 끝난다.
                var specFull = Path.GetFullPath(Path.Combine(objectDir, specPath));
                var nodeDir = Path.GetDirectoryName(Path.GetDirectoryName(specFull));
                if (nodeDir != null && Directory.Exists(nodeDir)) yield return (key, nodeDir);
            }
        }

        private static ObjectCoverage? LoadObject(string objectDir, string displayName)
        {
            var metaPath = Path.Combine(objectDir, "raw", "metadata.json");
            var specPath = Path.Combine(objectDir, "docs", "Spec.md");

            if (!File.Exists(metaPath) || !File.Exists(specPath))
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]건너뜀: {Markup.Escape(displayName)} - metadata.json 또는 Spec.md가 없습니다.[/]");
                return null;
            }

            // metadata.json에는 BOM이 붙어 있다. File.ReadAllText가 자동으로 벗긴다.
            var spDef = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(metaPath));
            if (spDef == null)
            {
                AnsiConsole.MarkupLine($"[yellow]건너뜀: {Markup.Escape(displayName)} - metadata.json 역직렬화 실패.[/]");
                return null;
            }

            return CoverageMapComposer.Compose(displayName, spDef, File.ReadAllText(specPath));
        }

        private static void Write(string path, IReadOnlyList<ObjectCoverage> objects, string title)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, CoverageMapHtmlWriter.Render(objects, $"{title} 커버리지 맵"));
        }
    }
}
```

- [ ] **Step 7: 통과를 확인하고 실물로 돌린다**

```bash
dotnet test --filter "FullyQualifiedName~CliArgsTests"
dotnet build
dotnet run --project src/ReSet.Cli -- --coverage-map dbo.UP_UTIL_SETTLE_EXCEPTION_PROC
```

Expected: 테스트 PASS, 그리고 `output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/CoverageMap.html`이 생긴다. 브라우저로 열어 줄 클릭이 동작하는지 눈으로 확인한다.

```bash
dotnet run --project src/ReSet.Cli -- --coverage-map POQSettlePrco20
```

Expected: `output/Jobs/POQSettlePrco20/coverage/CoverageMap.html`에 폐포 31개가 담긴다. 객체 수를 화면 출력으로 확인한다 — 31보다 적으면 어느 객체가 왜 빠졌는지 사유가 함께 찍혀야 한다.

- [ ] **Step 8: 커밋한다**

```bash
git add src/ReSet.Cli/CliArgs.cs src/ReSet.Cli/Program.cs src/ReSet.Cli/CoverageMapCommand.cs tests/ReSet.Core.Tests/CliArgsTests.cs
git commit -m "feat: --coverage-map으로 DB 없이 커버리지 맵을 낸다

로그인 흐름보다 앞에 분기를 둔다 - --extract-snapshot과 달리 DB 연결이 필요
없고, 초 단위로 끝나고 몇 번을 돌리든 공짜라는 것이 이 도구의 성질이다.
대상은 감사 축 A와 같은 정의(참조 폐포)로 잡는다.

산출물이 없어 빠진 객체는 사유를 화면에 남긴다. 조용히 빠지면 폐포가 줄어든
것을 아무도 모른다."
```

---

### Task 7: 골든 대조 — 맵이 맞다는 것을 고정한다

**Files:**
- Create: `tests/ReSet.Core.Tests/CoverageMapGoldenTests.cs`
- Modify: `docs/superpowers/specs/2026-08-24-ddl-coverage-map-design.md`(완료 기준 체크)

**Interfaces:**
- Consumes: `CoverageMapComposer.Compose`(Task 3)
- Produces: 없음(검증 전용)

설계서 §6의 세 요구를 고정한다. `output.bak-*`는 `.gitignore` 대상이므로 전부 `Skippable`이고, **건너뛴 사유가 출력에 남아야 한다.**

- [ ] **Step 1: 세 요구를 테스트로 쓴다**

`tests/ReSet.Core.Tests/CoverageMapGoldenTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 설계서 §6 — 맵이 사람 감사와 같은 것을 보고 있는지 고정한다.
    ///
    /// [왜 감사 10회차의 🟡을 재현 목표로 삼지 않는가] 그 🟡은 COMM_UPD DML 범위 표의
    /// PGNAME 중복 전사다. "적힌 것이 이상하다"이지 "안 적혔다"가 아니라 맵이
    /// 원리적으로 못 본다. 그걸 요구하면 통과할 수 없는 테스트가 된다.
    /// </summary>
    public class CoverageMapGoldenTests
    {
        private readonly ITestOutputHelper _output;

        public CoverageMapGoldenTests(ITestOutputHelper output) => _output = output;

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "output")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? string.Empty;
        }

        private static ObjectCoverage? Load(string root, string outputDirName, string objectName)
        {
            var baseDir = Path.Combine(root, outputDirName, "Procedures", objectName);
            var metaPath = Path.Combine(baseDir, "raw", "metadata.json");
            var specPath = Path.Combine(baseDir, "docs", "Spec.md");
            if (!File.Exists(metaPath) || !File.Exists(specPath)) return null;

            var spDef = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(metaPath));
            if (spDef == null) return null;

            return CoverageMapComposer.Compose(objectName, spDef, File.ReadAllText(specPath));
        }

        [SkippableFact]
        public void Requirement1_CurrentEdition_SpecMissingShouldBeNearZero()
        {
            // 감사 10회차가 🔴 0 · 🟠 0으로 끝났다. 맵이 🟥을 다수 내면 맵이 틀렸거나
            // 감사가 놓친 것이다 - 어느 쪽이든 조사할 값어치가 있는 신호다.
            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), "output/을 찾지 못했다");

            var dir = Path.Combine(root, "output", "Procedures");
            Skip.IfNot(Directory.Exists(dir), "output/Procedures가 없다");

            var total = 0;
            foreach (var objectDir in Directory.GetDirectories(dir))
            {
                var coverage = Load(root, "output", Path.GetFileName(objectDir));
                if (coverage == null) continue;

                var missing = coverage.Count(CoverageState.SpecMissing);
                total += missing;
                if (missing > 0)
                {
                    _output.WriteLine($"{coverage.ObjectName}: 🟥 {missing}");
                    foreach (var s in coverage.Statements.Where(s => s.State == CoverageState.SpecMissing))
                    {
                        _output.WriteLine($"   줄 {s.Statement.StartLine}-{s.Statement.EndLine} {s.Statement.StatementType}");
                    }
                }
            }

            _output.WriteLine($"현재 판 🟥 총계: {total}");
            Assert.True(total == 0,
                $"🟥이 {total}건 났다. 감사 10회차(🔴 0 · 🟠 0)와 어긋난다 - " +
                "맵이 틀렸는지 감사가 놓쳤는지 조사하고 사유를 설계서에 적어라. " +
                "조사 결과 맵이 맞다면 이 단언을 그때의 실측값으로 고쳐라.");
        }

        [SkippableFact]
        public void Requirement2_AgainstPriorEdition_DefectsShouldHaveShrunk()
        {
            // 카탈로그 9회차: "8회차 34건 중 31건 소멸". 맵도 같은 방향을 보여야 한다.
            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), "output/을 찾지 못했다");
            Skip.IfNot(Directory.Exists(Path.Combine(root, "output.bak-2026-08-22")),
                "output.bak-2026-08-22이 없다 - 과거 판 대조 건너뜀");

            var priorTotal = 0;
            var currentTotal = 0;
            foreach (var objectDir in Directory.GetDirectories(Path.Combine(root, "output.bak-2026-08-22", "Procedures")))
            {
                var name = Path.GetFileName(objectDir);
                var prior = Load(root, "output.bak-2026-08-22", name);
                var current = Load(root, "output", name);
                if (prior == null || current == null) continue;

                priorTotal += prior.Count(CoverageState.SpecMissing) + prior.Count(CoverageState.OutOfScope);
                currentTotal += current.Count(CoverageState.SpecMissing) + current.Count(CoverageState.OutOfScope);
            }

            _output.WriteLine($"과거 판 🟥+🟧: {priorTotal}");
            _output.WriteLine($"현재 판 🟥+🟧: {currentTotal}");

            Assert.True(currentTotal <= priorTotal,
                $"현재 판({currentTotal})이 과거 판({priorTotal})보다 나쁘다. " +
                "카탈로그 9회차가 기록한 방향(34건 중 31건 소멸)과 어긋난다.");
        }

        [SkippableFact]
        public void Requirement3_JoinOnLiteral_ShouldFlipFromOutOfScopeToConsistent()
        {
            // 9회차 🟠: INS_EXTRA4PLCARD의 ON 절 리터럴 PG.ExtraType IN (2,3)이
            // 집합 술어 표가 WHERE만 담아 받아 줄 표가 없었다(③ 사각지대).
            // 10회차에 조인 ON 행이 생기며 닫혔다. 맵도 뒤집혀야 한다.
            const string name = "dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD";
            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), "output/을 찾지 못했다");

            var prior = Load(root, "output.bak-2026-08-22", name);
            var current = Load(root, "output", name);
            Skip.If(prior == null, "과거 판 산출물이 없다 - 건너뜀");
            Skip.If(current == null, "현재 판 산출물이 없다 - 건너뜀");

            int ScoreOf(ObjectCoverage c) =>
                c.Count(CoverageState.Consistent) - c.Count(CoverageState.OutOfScope);

            _output.WriteLine($"과거 판: 🟩 {prior!.Count(CoverageState.Consistent)} · 🟧 {prior.Count(CoverageState.OutOfScope)}");
            _output.WriteLine($"현재 판: 🟩 {current!.Count(CoverageState.Consistent)} · 🟧 {current.Count(CoverageState.OutOfScope)}");

            Assert.True(ScoreOf(current) > ScoreOf(prior),
                "ON 절 리터럴이 조인 ON 행으로 닫혔는데도 맵이 개선을 못 봤다.");
        }
    }
}
```

- [ ] **Step 2: 돌린다**

Run: `dotnet test --filter "FullyQualifiedName~CoverageMapGoldenTests" --logger "console;verbosity=detailed"`

- [ ] **Step 3: 결과에 따라 처리한다**

- **요구 1이 실패하면** — 🟥이 난 자리를 하나씩 원본과 대조한다. 맵이 틀렸으면 Task 3으로 돌아가 고친다. 맵이 맞으면 **감사가 놓친 결함을 찾은 것**이므로 사유를 설계서에 적고 단언을 그때의 실측값으로 고친다. 어느 쪽인지 결론 없이 단언만 낮추지 않는다.
- **요구 2·3이 건너뛰면** — 그 사실을 설계서 완료 기준에 그대로 적는다. "돌지 않았다"와 "통과했다"는 다르다.

- [ ] **Step 4: 설계서 완료 기준을 채운다**

설계서의 완료 기준 체크박스를 실측 결과로 채운다. 건너뛴 항목은 체크하지 말고 사유를 적는다.

- [ ] **Step 5: 전체 검증**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
dotnet test
```

Expected: 경고 8 이하, 테스트 실패 0.

- [ ] **Step 6: 커밋한다**

```bash
git add tests/ReSet.Core.Tests/CoverageMapGoldenTests.cs docs/superpowers/specs/2026-08-24-ddl-coverage-map-design.md
git commit -m "test: 맵이 사람 감사와 같은 결론을 내는지 세 요구로 고정한다

현재 판 🟥이 감사 10회차(🔴 0 · 🟠 0)와 맞는지, 과거 판 대비 감소가 카탈로그
9회차 기록과 같은 방향인지, INS_EXTRA4PLCARD의 ON 절 리터럴 자리가 뒤집히는지.

감사 10회차의 🟡을 재현 목표로 삼지 않는다 - 중복 전사라 맵이 원리적으로 못
보고, 그걸 요구하면 통과 불가능한 테스트가 된다."
```

---

## 완료 기준 (계획 전체)

- [ ] Task 1~7의 모든 단계가 체크됐다
- [ ] `dotnet clean && dotnet build`의 `warning CS` 유일 건수가 **0**(기준선 실측값 — Global Constraints 참고)
- [ ] `dotnet test` 실패 0
- [ ] `--coverage-map POQSettlePrco20`이 DB·AI 없이 폐포 31개에 대해 HTML 한 장을 낸다
- [ ] Task 4의 실측 수치와 🟧 백로그가 설계서에 적혔다
- [ ] 설계서 「미확정 사항」 1·2·3·5가 닫혔다(3은 Task 1이 유형 목록을 없애 닫음)
- [ ] 설계서 §6의 세 요구가 통과했거나, 건너뛴 사유가 적혔다
- [ ] `VerificationPipelineOrchestrator`·`MechanicalValidator`·`CacheManager`에 변경이 없다 (`git diff --stat main` 으로 확인)
