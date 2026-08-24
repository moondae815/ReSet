# 축 B 단계 검사 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 명세서의 기계 확정 표를 단계 검사(`ValidateBatchStep`)까지 날라 단계 지시서와 **문장 단위**로 대조하고, POQSettleBatch1 축 B 감사의 🔴 2건 · 🟠 7건을 닫는다.

**Architecture:** 새 통로를 만들지 않는다. `VerificationPipelineOrchestrator`가 재시도 루프 밖에서 명세서 재료를 만드는 자리(`MergeSpecMaterials(...)` 옆)에 `SpecStatementFactsExtractor.Extract(specs)`를 더하고, `ValidateBatchStep`이 인자 하나를 더 받는다. 단계 SQL의 문장은 ScriptDom으로 파싱하고, 명세서의 "갱신 N"과는 개수 → 앵커 → 앵커 요구의 3단 폴백으로 대응시킨다. 시정 지시는 이미 있는 `SuggestedPromptFix` → `floorFeedback` 경로를 그대로 탄다.

**Tech Stack:** C# / .NET 10, xUnit, `Microsoft.SqlServer.TransactSql.ScriptDom`(`TSql160Parser`), Serilog.

**Spec:** `docs/superpowers/specs/2026-08-24-axis-b-step-check-design.md`

## Global Constraints

- 네임스페이스는 `ReSet.Core.Services`. 새 파일도 같은 네임스페이스에 둔다.
- **각 검사는 자기 `try/catch`를 가진다.** 하나가 던져도 나머지 검사가 죽지 않는다. 이것은 이 저장소의 L1 규약이다.
- **귀속할 수 없으면 침묵한다.** 재료가 없거나(표 없음·앵커 없음·파싱 실패) 레거시 출신이 없는 신설 단계면 오류를 만들지 않는다.
- **컬럼 이름만 대조하고 값은 보지 않는다.** `UseState IN (0)` ↔ `UseState = 0` 같은 동등 표현이 실측 미검출의 27%였고 그 전부가 오탐이었다.
- 이름 비교는 전부 `StringComparer.OrdinalIgnoreCase`. 명세서는 `USESTATE`, 단계는 `UseState`로 쓴다.
- 축 B는 **원본 `.sql` DDL을 읽지 않는다.** 기준값은 `Spec.md`뿐이다.
- 주석은 "무엇을"이 아니라 **"왜"**를 적는다. 실측 근거가 있으면 그 수치를 적는다(이 저장소의 기존 주석 양식).
- 테스트는 TDD로 쓴다 — 실패하는 테스트를 먼저 쓰고, 실패를 눈으로 확인한 뒤 구현한다.
- 전체 테스트 통과 기준은 **건너뜀 0**이다(`CorpusSkip`이 걸리면 `output/` 심링크를 먼저 놓는다).

---

### Task 1: `SpecStatementFactsExtractor` — 명세서 표를 읽는다

**Files:**
- Create: `src/ReSet.Core/Services/SpecStatementFactsExtractor.cs`
- Test: `tests/ReSet.Core.Tests/SpecStatementFactsExtractorTests.cs`

**Interfaces:**
- Consumes: `MarkdownSectionLocator.SplitLines`, `MarkdownTableCellCodec.SplitRow`
- Produces: `SpecStatementFacts`, `SpecDmlRow`, `SpecSetTarget`, `SpecLocalVariable`, `SpecStatementFactsExtractor.Extract(IReadOnlyList<(string FileName, string Content)>)` → `IReadOnlyDictionary<string, SpecStatementFacts>` (키는 `dbo.UP_…` 형태의 SP 이름, `OrdinalIgnoreCase`)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class SpecStatementFactsExtractorTests
{
    // COMM_UPD 명세서의 실물 모양을 그대로 오려 왔다. 열 순서에 기대지 않고
    // 헤더 이름으로 찾는지, `(없음)`·`—`를 빈 목록으로 읽는지를 함께 본다.
    private const string Spec = """
        ### 지역 변수 및 시스템 값

        | 명칭 | 데이터 타입 또는 구분 | 사용 위치 | 관계 |
        | :--- | :--- | :--- | :--- |
        | `@v_valIncVat` | `DECIMAL(2,1)` | UPDATE 13 | 값 `1.1`로 선언됩니다. |
        | `@@ERROR` | SQL Server 시스템 값 | UPDATE 1부터 15 | 오류 여부를 검사합니다. |

        ## CRUD 분석

        ### UPDATE 대상 테이블: SETTLE_POQ_DB.dbo.TSettleMst (갱신 1 · 원본 DDL 라인 30 · 원문 표기: TSettleMst)

        | 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
        | :--- | :--- | :--- | :--- |
        | SETTLE_POQ_DB.dbo.TSettleMst | CLINTCOMM | CAST(B.TXAMT AS INT) | 설명 |
        | SETTLE_POQ_DB.dbo.TSettleMst | CLVT | dbo.UF_GET_ROUND4VAT(1) | 설명 |

        ### DML 범위 (기계 확정 — 수정 금지)

        | 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼(조인 결합 포함 · 대상 한정 아님) | 기준일 파라미터 적용(최상위 WHERE 기준) | 조인 키 | GROUP BY | ORDER BY |
        | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
        | UPDATE 1 | 30 | TSettleMst | PLTID, YMD, USESTATE | 예 | PLTID | — | — |
        | UPDATE 3 | 122 | TSettleMst | YMD, USESTATE, PLTID | 예 | (없음) | — | — |
        """;

    private static SpecStatementFacts Extract() =>
        SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_UTIL_SETTLE_COMM_UPD", Spec) })["dbo.UP_UTIL_SETTLE_COMM_UPD"];

    [Fact]
    public void DmlRows_AreReadWithOrdinalAndColumns()
    {
        var rows = Extract().DmlRows;

        Assert.Equal(2, rows.Count);
        var first = rows[0];
        Assert.Equal("UPDATE", first.Kind);
        Assert.Equal(1, first.Ordinal);
        Assert.Equal(30, first.SourceLine);
        Assert.Equal("TSettleMst", first.TargetTable);
        Assert.Equal(new[] { "PLTID", "YMD", "USESTATE" }, first.PredicateColumns);
        Assert.Equal(new[] { "PLTID" }, first.JoinKeys);
        Assert.Empty(first.GroupBy);
    }

    [Fact]
    public void NoneAndDashCells_BecomeEmptyLists()
    {
        var third = Extract().DmlRows.Single(r => r.Ordinal == 3);

        Assert.Empty(third.JoinKeys);     // "(없음)"
        Assert.Empty(third.OrderBy);      // "—"
    }

    [Fact]
    public void SetTargets_AreReadPerUpdateSection()
    {
        var target = Assert.Single(Extract().SetTargets);

        Assert.Equal(1, target.Ordinal);
        Assert.Equal("TSettleMst", target.TargetTable);
        Assert.Equal(new[] { "CLINTCOMM", "CLVT" }, target.Columns);
    }

    [Fact]
    public void SystemValues_AreMarkedAndNotTreatedAsLocalVariables()
    {
        var variables = Extract().LocalVariables;

        var local = Assert.Single(variables, v => v.Name == "@v_valIncVat");
        Assert.False(local.IsSystemValue);
        Assert.Equal("DECIMAL(2,1)", local.TypeOrKind);

        var system = Assert.Single(variables, v => v.Name == "@@ERROR");
        Assert.True(system.IsSystemValue);
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter SpecStatementFactsExtractorTests`
Expected: 컴파일 실패 — `SpecStatementFactsExtractor`가 없다.

- [ ] **Step 3: 구현한다**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    public sealed record SpecDmlRow(
        string Kind,
        int Ordinal,
        int SourceLine,
        string TargetTable,
        IReadOnlyList<string> PredicateColumns,
        IReadOnlyList<string> JoinKeys,
        IReadOnlyList<string> GroupBy,
        IReadOnlyList<string> OrderBy);

    public sealed record SpecSetTarget(int Ordinal, string TargetTable, IReadOnlyList<string> Columns);

    public sealed record SpecLocalVariable(string Name, string TypeOrKind, bool IsSystemValue);

    public sealed record SpecStatementFacts(
        IReadOnlyList<SpecDmlRow> DmlRows,
        IReadOnlyList<SpecSetTarget> SetTargets,
        IReadOnlyList<SpecLocalVariable> LocalVariables);

    /// <summary>
    /// 명세서의 기계 확정 표를 읽어 단계 검사가 쓸 사실로 만든다.
    ///
    /// [왜 필요한가 - POQSettleBatch1 축 B 감사 실측]
    /// ValidateBatchStep이 받는 기준값은 목차와 조건 컬럼 목록뿐이라, 명세서가
    /// 확정한 UPDATE 15개 중 10개를 단계가 통째로 빼먹어도 통과했다(S07 🔴).
    /// 대조가 "문서 어딘가에 이 컬럼이 있나" 수준이라 YMD가 42곳에 흩어진 문서는
    /// 갱신 13의 최상위 WHERE에서 YMD가 빠져도 통과했다(S07 🟠).
    ///
    /// [열 순서에 기대지 않는 이유]
    /// DML 범위 표의 열은 회차마다 늘었다(GROUP BY·ORDER BY가 나중에 붙었다).
    /// 인덱스로 읽으면 열이 하나 늘 때 모든 칸이 한 칸씩 밀려 조용히 오독한다.
    /// </summary>
    public static class SpecStatementFactsExtractor
    {
        private const string DmlScopeHeading = "### DML 범위 (기계 확정 — 수정 금지)";
        private const string LocalVariableHeading = "### 지역 변수 및 시스템 값";
        private const string SystemValueMarker = "SQL Server 시스템 값";

        private static readonly Regex UpdateSectionPattern = new(
            @"^###\s+(?<kind>UPDATE|INSERT|DELETE)\s+대상 테이블:\s*(?<table>[^\(]+?)\s*\(\s*(?:갱신|삽입|삭제)\s*(?<ordinal>\d+)",
            RegexOptions.Compiled);

        private static readonly Regex StatementCellPattern = new(
            @"^(?<kind>UPDATE|INSERT|DELETE|SELECT)\s+(?<ordinal>\d+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static IReadOnlyDictionary<string, SpecStatementFacts> Extract(
            IReadOnlyList<(string FileName, string Content)> specs)
        {
            var result = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase);
            if (specs == null) return result;

            foreach (var (fileName, content) in specs)
            {
                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(content)) continue;

                // 한 명세서가 못 읽혀도 나머지는 읽는다 - 재료가 통째로 비면
                // 검사가 전부 침묵해 결함이 소리 없이 통과한다.
                try
                {
                    var lines = MarkdownSectionLocator.SplitLines(content);
                    result[fileName] = new SpecStatementFacts(
                        ReadDmlRows(lines),
                        ReadSetTargets(lines),
                        ReadLocalVariables(lines));
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "명세서 기계 확정 표를 읽지 못했습니다 - Spec: {Spec}", fileName);
                }
            }

            return result;
        }

        private static IReadOnlyList<SpecDmlRow> ReadDmlRows(IReadOnlyList<string> lines)
        {
            var rows = new List<SpecDmlRow>();
            var table = ReadTable(lines, DmlScopeHeading);
            if (table == null) return rows;

            int Col(params string[] fragments) => FindColumn(table.Value.Header, fragments);

            var iStatement = Col("문장");
            var iLine = Col("라인");
            var iTarget = Col("대상");
            var iPredicate = Col("술어 컬럼");
            var iJoin = Col("조인 키");
            var iGroup = Col("GROUP BY");
            var iOrder = Col("ORDER BY");
            if (iStatement < 0 || iTarget < 0) return rows;

            foreach (var cells in table.Value.Rows)
            {
                var statement = Cell(cells, iStatement);
                var match = StatementCellPattern.Match(statement);
                if (!match.Success) continue;

                rows.Add(new SpecDmlRow(
                    match.Groups["kind"].Value.ToUpperInvariant(),
                    int.Parse(match.Groups["ordinal"].Value),
                    int.TryParse(Cell(cells, iLine), out var line) ? line : 0,
                    BareName(Cell(cells, iTarget)),
                    SplitColumns(Cell(cells, iPredicate)),
                    SplitColumns(Cell(cells, iJoin)),
                    SplitColumns(Cell(cells, iGroup)),
                    SplitColumns(Cell(cells, iOrder))));
            }

            return rows;
        }

        private static IReadOnlyList<SpecSetTarget> ReadSetTargets(IReadOnlyList<string> lines)
        {
            var targets = new List<SpecSetTarget>();

            for (int i = 0; i < lines.Count; i++)
            {
                var match = UpdateSectionPattern.Match(lines[i]);
                if (!match.Success) continue;

                var ordinal = int.Parse(match.Groups["ordinal"].Value);
                var target = BareName(match.Groups["table"].Value);
                var columns = new List<string>();

                // 이 절의 표만 읽는다. 다음 `### `를 만나면 끝이다.
                for (int j = i + 1; j < lines.Count && !lines[j].StartsWith("### ", StringComparison.Ordinal); j++)
                {
                    var cells = MarkdownTableCellCodec.SplitRow(lines[j]);
                    if (cells.Count < 3 || IsSeparator(cells)) continue;
                    if (cells[1].Equals("컬럼명", StringComparison.Ordinal)) continue;
                    var column = Clean(cells[1]);
                    if (column.Length > 0) columns.Add(column);
                }

                if (columns.Count > 0) targets.Add(new SpecSetTarget(ordinal, target, columns));
            }

            return targets;
        }

        private static IReadOnlyList<SpecLocalVariable> ReadLocalVariables(IReadOnlyList<string> lines)
        {
            var variables = new List<SpecLocalVariable>();
            var table = ReadTable(lines, LocalVariableHeading);
            if (table == null) return variables;

            var iName = FindColumn(table.Value.Header, "명칭");
            var iType = FindColumn(table.Value.Header, "데이터 타입", "구분");
            if (iName < 0) return variables;

            foreach (var cells in table.Value.Rows)
            {
                var name = Clean(Cell(cells, iName));
                if (!name.StartsWith("@", StringComparison.Ordinal)) continue;

                var type = Clean(Cell(cells, iType));
                variables.Add(new SpecLocalVariable(
                    name, type, type.Contains(SystemValueMarker, StringComparison.Ordinal)));
            }

            return variables;
        }

        private static (List<string> Header, List<List<string>> Rows)? ReadTable(
            IReadOnlyList<string> lines, string heading)
        {
            var start = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].TrimEnd().Equals(heading, StringComparison.Ordinal)) { start = i; break; }
            }
            if (start < 0) return null;

            List<string>? header = null;
            var rows = new List<List<string>>();

            for (int i = start + 1; i < lines.Count && !lines[i].StartsWith("### ", StringComparison.Ordinal); i++)
            {
                var cells = MarkdownTableCellCodec.SplitRow(lines[i]);
                if (cells.Count == 0) continue;
                if (IsSeparator(cells)) continue;
                if (header == null) { header = cells; continue; }
                rows.Add(cells);
            }

            return header == null ? null : (header, rows);
        }

        // 헤더 칸은 회차마다 길어졌다("조인 키"가 "조인 키(등식)"이 된 적이 있다).
        // 포함으로 찾아야 그런 확장에 견딘다.
        private static int FindColumn(IReadOnlyList<string> header, params string[] fragments)
        {
            for (int i = 0; i < header.Count; i++)
            {
                if (fragments.All(f => header[i].Contains(f, StringComparison.OrdinalIgnoreCase))) return i;
            }
            return -1;
        }

        private static bool IsSeparator(IReadOnlyList<string> cells) =>
            cells.Count > 0 && cells.All(c => c.Trim().Trim(':').All(ch => ch == '-') && c.Contains('-'));

        private static string Cell(IReadOnlyList<string> cells, int index) =>
            index >= 0 && index < cells.Count ? cells[index] : string.Empty;

        private static IReadOnlyList<string> SplitColumns(string cell)
        {
            var cleaned = Clean(cell);
            if (cleaned.Length == 0 || cleaned == "(없음)" || cleaned == "—" || cleaned == "-")
            {
                return Array.Empty<string>();
            }

            return cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(BareName)
                .Where(c => c.Length > 0)
                .ToList();
        }

        // `A.YMD` → `YMD`. 별칭은 문서마다 다르고 대조에 쓸 수 없다.
        private static string BareName(string value)
        {
            var cleaned = Clean(value);
            var dot = cleaned.LastIndexOf('.');
            return dot >= 0 ? cleaned[(dot + 1)..] : cleaned;
        }

        private static string Clean(string value) =>
            (value ?? string.Empty).Trim().Trim('`', '*', ' ');
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter SpecStatementFactsExtractorTests`
Expected: PASS 4건

- [ ] **Step 5: 실물 명세서로 확인한다**

실물 명세서가 몇 행을 내는지 눈으로 확인한다.

```bash
grep -c '^| UPDATE ' output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/docs/Spec.md
```
Expected: 15 (명세서가 확정한 갱신 수. Task 3의 검사 A가 이 숫자를 기준값으로 쓴다)

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/SpecStatementFactsExtractor.cs tests/ReSet.Core.Tests/SpecStatementFactsExtractorTests.cs
git commit -m "feat: 명세서의 DML 범위·갱신 절·지역 변수 표를 단계 검사용 사실로 뽑는다"
```

---

### Task 2: `StepSqlStatementReader` — 단계 SQL의 문장과 앵커를 읽는다

**Files:**
- Create: `src/ReSet.Core/Services/StepSqlStatementReader.cs`
- Test: `tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs`

**Interfaces:**
- Consumes: `Microsoft.SqlServer.TransactSql.ScriptDom`
- Produces: `StepSqlStatement`, `StepSqlStatementReader.Read(string stepMarkdown)` → `IReadOnlyList<StepSqlStatement>`

**왜 `CleanedSqlFences`를 쓰지 않는가:** 그 헬퍼는 `BlankCommentsAndStrings`로 주석을 공백으로 지운다. 앵커(`/* U4: … */`)는 주석 안에 있으므로 지워진 사본에서는 읽을 수 없다. ScriptDom은 주석을 `ScriptTokenStream`에 토큰으로 남기므로 **원본 펜스를 파싱하면** 문장과 그 앞 주석을 함께 얻는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class StepSqlStatementReaderTests
{
    private static string Fence(string sql) => $"### S07 단계\n\n```sql\n{sql}\n```\n";

    [Fact]
    public void CountsStatementsByKindAndTable()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
            "UPDATE A SET A.CLVT = 2 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
            "INSERT INTO batch.BatchStepJournal (RunId) VALUES (1);"));

        Assert.Equal(2, statements.Count(s => s.Kind == "UPDATE" && s.TargetTable == "TSettleMst"));
        Assert.Single(statements, s => s.Kind == "INSERT" && s.TargetTable == "BatchStepJournal");
    }

    [Fact]
    public void ReadsAnchorFromLeadingComment()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "/* U13: 카드사 원가 반영 */\n" +
            "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y WHERE Y.YMD = @p;"));

        Assert.Equal(13, Assert.Single(statements).Anchor);
    }

    [Theory]
    [InlineData("-- 갱신 4")]
    [InlineData("-- UPDATE 4")]
    [InlineData("/* U4 */")]
    public void AcceptsThreeAnchorSpellings(string comment)
    {
        var statements = StepSqlStatementReader.Read(Fence(
            comment + "\nUPDATE A SET A.CLVT = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;"));

        Assert.Equal(4, Assert.Single(statements).Anchor);
    }

    [Fact]
    public void CollectsTopLevelPredicateAndJoinColumns_ButNotSubqueryColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = (SELECT TOP 1 X.Amt FROM dbo.TCost AS X WHERE X.Hidden = 1)\n" +
            "FROM dbo.TSettleMst AS Y INNER JOIN dbo.TCost AS C ON C.PLTID = Y.PLTID\n" +
            "WHERE Y.YMD = @p AND Y.UseState = 1;"));

        var statement = Assert.Single(statements);
        Assert.Contains("YMD", statement.PredicateColumns);
        Assert.Contains("UseState", statement.PredicateColumns);
        Assert.Contains("PLTID", statement.JoinColumns);
        Assert.DoesNotContain("Hidden", statement.PredicateColumns);   // 스칼라 하위질의 안쪽
    }

    [Fact]
    public void FlagsGroupingWhenGroupByOrHavingPresent()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = 0 FROM dbo.TSettleMst AS Y\n" +
            "WHERE Y.PLTID IN (SELECT PLTID FROM dbo.TTx GROUP BY PLTID HAVING SUM(TxAmt) = 0);"));

        Assert.True(Assert.Single(statements).HasGrouping);
    }

    [Fact]
    public void UnparsableFence_IsSilentlySkipped()
    {
        // 단계 문서의 펜스에는 T-SQL이 아닌 것도 온다. 재료가 없다는 사실은
        // 다른 검사가 들고, 이 읽기는 조용히 건너뛴다.
        var statements = StepSqlStatementReader.Read(Fence("이것은 SQL이 아니다 <<<>>>"));

        Assert.Empty(statements);
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter StepSqlStatementReaderTests`
Expected: 컴파일 실패 — `StepSqlStatementReader`가 없다.

- [ ] **Step 3: 구현한다**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <param name="Anchor">주석에서 읽은 갱신 번호. 없으면 null.</param>
    public sealed record StepSqlStatement(
        string Kind,
        string TargetTable,
        int? Anchor,
        IReadOnlyList<string> PredicateColumns,
        IReadOnlyList<string> JoinColumns,
        bool HasGrouping);

    /// <summary>
    /// 단계 지시서의 ```sql 펜스에서 DML 문장을 읽는다.
    ///
    /// [왜 정규식이 아니라 ScriptDom인가]
    /// 정규식으로 UPDATE를 세면 문자열 리터럴 안의 단어와 주석에 적힌 예시가 함께
    /// 잡힌다. 단계 문서는 산문과 SQL이 섞여 있어 그 오검출이 개수 대조를 무의미하게
    /// 만든다.
    ///
    /// [왜 CleanedSqlFences를 쓰지 않는가]
    /// 그 헬퍼는 주석을 공백으로 지운다. 앵커(`/* U4: … */`)가 주석 안에 있어
    /// 지워진 사본에서는 읽을 수 없다. ScriptDom은 주석을 토큰으로 남기므로
    /// 원본 펜스를 파싱하면 문장과 그 앞 주석을 함께 얻는다.
    /// </summary>
    public static class StepSqlStatementReader
    {
        private static readonly Regex FencePattern = new(
            @"```sql(?<sql>.*?)```", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // `U4` · `갱신 4` · `UPDATE 4` 세 표기를 인정한다. S07이 이미 `/* U4: … */`를 쓴다.
        private static readonly Regex AnchorPattern = new(
            @"(?:\bU|갱신\s*|\bUPDATE\s+|\bINSERT\s+|\bDELETE\s+)(?<ordinal>\d{1,2})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static IReadOnlyList<StepSqlStatement> Read(string? stepMarkdown)
        {
            var statements = new List<StepSqlStatement>();
            if (string.IsNullOrWhiteSpace(stepMarkdown)) return statements;

            foreach (Match fence in FencePattern.Matches(stepMarkdown))
            {
                // 펜스 하나가 T-SQL이 아니어도 나머지 펜스는 읽는다.
                try
                {
                    statements.AddRange(ReadFence(fence.Groups["sql"].Value));
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "단계 SQL 펜스를 읽지 못했습니다 - 이 펜스는 건너뜁니다.");
                }
            }

            return statements;
        }

        private static IEnumerable<StepSqlStatement> ReadFence(string sql)
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            var fragment = parser.Parse(new StringReader(sql), out var errors);

            // 파싱에 실패한 펜스는 침묵한다 - 의사코드·C# 조각이 온다.
            if (fragment == null || errors is { Count: > 0 }) yield break;

            var tokens = fragment.ScriptTokenStream;
            var visitor = new DmlCollector();
            fragment.Accept(visitor);

            foreach (var (statement, firstTokenIndex) in visitor.Found)
            {
                yield return statement with { Anchor = ReadAnchor(tokens, firstTokenIndex) };
            }
        }

        /// <summary>
        /// 문장 바로 앞의 주석 토큰에서 갱신 번호를 읽는다. 공백과 주석만 거슬러
        /// 올라가고, 다른 토큰을 만나면 멈춘다 - 앞 문장의 꼬리 주석을 자기 앵커로
        /// 삼으면 대응이 한 칸씩 밀린다.
        /// </summary>
        private static int? ReadAnchor(IList<TSqlParserToken> tokens, int firstTokenIndex)
        {
            for (int i = firstTokenIndex - 1; i >= 0; i--)
            {
                var token = tokens[i];
                if (token.TokenType is TSqlTokenType.WhiteSpace) continue;
                if (token.TokenType is not (TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
                {
                    return null;
                }

                var match = AnchorPattern.Match(token.Text);
                if (match.Success) return int.Parse(match.Groups["ordinal"].Value);
            }

            return null;
        }

        private sealed class DmlCollector : TSqlFragmentVisitor
        {
            /// <summary>문장과 그 첫 토큰 위치. 앵커는 ReadFence가 토큰 스트림에서 채운다.</summary>
            public List<(StepSqlStatement Statement, int FirstTokenIndex)> Found { get; } = new();

            public override void Visit(UpdateStatement node) =>
                Add("UPDATE", node, node.UpdateSpecification?.Target,
                    node.UpdateSpecification?.WhereClause, node.UpdateSpecification?.FromClause);

            public override void Visit(DeleteStatement node) =>
                Add("DELETE", node, node.DeleteSpecification?.Target,
                    node.DeleteSpecification?.WhereClause, node.DeleteSpecification?.FromClause);

            public override void Visit(InsertStatement node) =>
                Add("INSERT", node, node.InsertSpecification?.Target, null, null);

            private void Add(
                string kind,
                TSqlStatement statement,
                TableReference? target,
                WhereClause? where,
                FromClause? from)
            {
                var predicates = new ColumnCollector();
                var joins = new ColumnCollector();
                var grouping = new GroupingProbe();

                where?.Accept(predicates);
                from?.Accept(joins);
                statement.Accept(grouping);

                Found.Add((
                    new StepSqlStatement(
                        kind,
                        NameOf(target),
                        Anchor: null,
                        predicates.Columns.ToList(),
                        joins.Columns.ToList(),
                        grouping.Found),
                    statement.FirstTokenIndex));
            }

            private static string NameOf(TableReference? target) =>
                target is NamedTableReference named
                    ? named.SchemaObject?.BaseIdentifier?.Value ?? string.Empty
                    : string.Empty;
        }

**`DmlCollector.Statements` 대신 `Found`를 쓰는 이유:** 앵커는 토큰 스트림에서 읽어야 하는데
방문자는 토큰 스트림을 갖지 않는다. 방문자는 `(문장, 첫 토큰 위치)`만 모으고, 스트림을 가진
`ReadFence`가 앵커를 채워 최종 레코드를 만든다.

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter StepSqlStatementReaderTests`
Expected: PASS 8건(Theory 3건 포함)

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/StepSqlStatementReader.cs tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs
git commit -m "feat: 단계 SQL 펜스에서 DML 문장·앵커·최상위 술어 컬럼을 읽는다"
```

---

### Task 3: 검사 A — 문장 개수 대조와 배선

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`ValidateBatchStep` 시그니처, 검사 추가)
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:3169-3171, 3224-3225`
- Modify: `tests/ReSet.Core.Tests/AxisBGoldenCaseTests.cs` (기존 `Validate` 헬퍼에 인자 추가)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `SpecStatementFacts`(Task 1), `StepSqlStatementReader.Read`(Task 2)
- Produces: `ValidateBatchStep(..., IReadOnlyDictionary<string, SpecStatementFacts>? statementFactsByProcedure = null)` — 기본값 `null`로 두어 기존 호출부가 깨지지 않는다

- [ ] **Step 1: 실패하는 테스트를 쓴다** (`MechanicalValidatorTests.cs` 말미에 추가)

```csharp
// ─────────────────────────────────────────────────────────────────────
// 검사 A - 문장 개수 대조. POQSettleBatch1 축 B 감사 S07 🔴:
// 명세서가 TSettleMst에 UPDATE 15개를 확정했는데 단계는 5개만 담고
// 나머지 10개를 `/* U4: … */` 주석 한 줄로 대체했다.
// ─────────────────────────────────────────────────────────────────────

private static IReadOnlyDictionary<string, SpecStatementFacts> FactsWithUpdates(int count)
{
    var rows = Enumerable.Range(1, count)
        .Select(i => new SpecDmlRow("UPDATE", i, i * 10, "TSettleMst",
            new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()))
        .ToList();

    return new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
    {
        ["dbo.UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
            rows, Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
    };
}

private static BatchStepPlan LegacyStep(string code) => new(
    Code: code, Name: $"{code} 단계",
    LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_EXCEPTION_PROC" },
    TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
    ErrorCodes: new[] { "-9" }, Chunkable: false, SchemaTables: Array.Empty<string>());

[Fact]
public void ValidateBatchStep_FewerStatementsThanSpecConfirms_ShouldBeAnError()
{
    var markdown = "### S07 단계\n\n```sql\n" +
        "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
        "```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
        new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(15));

    Assert.Contains(result.Errors, e => e.Contains("UPDATE") && e.Contains("15"));
}

[Fact]
public void ValidateBatchStep_MoreStatementsThanSpec_IsSilent()
{
    // 단계는 배치 제어 테이블에 정당하게 더 쓴다. 초과는 결함이 아니다.
    var markdown = "### S07 단계\n\n```sql\n" +
        "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
        "UPDATE A SET A.CLVT = 2 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
        "```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
        new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(1));

    Assert.DoesNotContain(result.Errors, e => e.Contains("DML 범위 표는"));
}

[Fact]
public void ValidateBatchStep_NewStepWithoutLegacy_IsSilent()
{
    var step = new BatchStepPlan("S01", "S01 단계", Array.Empty<string>(),
        new[] { "batch.BatchRun" }, Array.Empty<string>(), false, Array.Empty<string>());

    var result = new MechanicalValidator().ValidateBatchStep(
        "### S01 단계\n\n```sql\nSELECT 1;\n```\n", step, new[] { "batch.BatchRun" },
        new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(15));

    Assert.DoesNotContain(result.Errors, e => e.Contains("DML 범위 표는"));
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter ValidateBatchStep_FewerStatementsThanSpecConfirms`
Expected: 컴파일 실패 — `ValidateBatchStep`이 인자 7개를 받지 않는다.

- [ ] **Step 3: 시그니처를 늘리고 검사 A를 구현한다**

`ValidateBatchStep`의 마지막에 인자를 더한다:

```csharp
public StepValidationResult ValidateBatchStep(
    string? stepMarkdown,
    BatchStepPlan step,
    IReadOnlyCollection<string> knownTableNames,
    IReadOnlyDictionary<string, SpecConditions> conditionColumnsByProcedure,
    IReadOnlyList<StepInterface>? stepInterfaces = null,
    IReadOnlyCollection<string>? runRowOwnedTables = null,
    IReadOnlyDictionary<string, SpecStatementFacts>? statementFactsByProcedure = null)
```

기존 검사들을 부르는 자리 뒤에 다음을 더한다. **재료 해석은 한 번만 하고 검사 5개가 나눠 쓴다.**

```csharp
// 명세서의 기계 확정 표를 문장 단위로 대조한다. 재료가 없거나 레거시 출신이
// 없는 단계는 조용히 지나간다 - 물려받을 원본이 없다.
if (statementFactsByProcedure != null && step.LegacyProcedures.Count > 0)
{
    var facts = step.LegacyProcedures
        .Select(name => statementFactsByProcedure.TryGetValue(name, out var f) ? f : null)
        .Where(f => f != null)
        .ToList();

    if (facts.Count > 0)
    {
        var statements = StepSqlStatementReader.Read(stepMarkdown);

        // 검사 하나가 던져도 나머지가 죽지 않는다.
        SafeCheck(() => CheckStatementCountAgainstSpec(facts!, statements, step, result));
    }
}
```

`SafeCheck`가 없으면 만든다(이 파일의 기존 관행을 따른다):

```csharp
private static void SafeCheck(Action check)
{
    try { check(); }
    catch (Exception ex) { Serilog.Log.Warning(ex, "단계 검사 하나가 실패해 건너뜁니다."); }
}
```

검사 A:

```csharp
/// <summary>
/// 명세서가 확정한 DML 문장 수를 단계가 실제로 담았는지 본다.
///
/// [POQSettleBatch1 축 B 감사 S07 🔴]
/// 명세서가 TSettleMst에 UPDATE 15개를 확정했는데 단계는 5개만 담고 나머지 10개를
/// `/* U4: 고객사 최저수수료 */` 같은 주석 한 줄로 대체했다. 상수·계수·부호·반올림
/// 자릿수·UDF 인자가 지시서 어디에도 없어, 이 절만으로 구현하면 CLCOMM·CLVT·PGCOMM·
/// PGVT가 원본과 달라진다.
///
/// [부족만 오류로 드는 이유]
/// 단계는 배치 제어 테이블(BatchStepJournal·BatchCheckpoint)에 자기 행을 쓰고,
/// 청크 처리를 위해 문장을 나누기도 한다. 초과를 오류로 들면 그 정상 구조가 전부
/// 걸린다.
/// </summary>
private static void CheckStatementCountAgainstSpec(
    IReadOnlyList<SpecStatementFacts> facts,
    IReadOnlyList<StepSqlStatement> statements,
    BatchStepPlan step,
    StepValidationResult result)
{
    var expected = facts
        .SelectMany(f => f.DmlRows)
        .GroupBy(r => (r.Kind, r.TargetTable), StringTupleComparer)
        .ToList();

    foreach (var group in expected)
    {
        var actual = statements.Count(s =>
            s.Kind.Equals(group.Key.Kind, StringComparison.OrdinalIgnoreCase) &&
            s.TargetTable.Equals(group.Key.TargetTable, StringComparison.OrdinalIgnoreCase));

        if (actual >= group.Count()) continue;

        var missing = group.Select(r => r.Ordinal).OrderBy(o => o).Skip(actual);
        result.Errors.Add(
            $"{step.Code} 섹션이 `{group.Key.TargetTable}`에 대한 {group.Key.Kind}를 {actual}개만 담고 " +
            $"있습니다. 명세서 DML 범위 표는 {group.Count()}개를 확정합니다(빠진 것으로 보이는 번호: " +
            $"{string.Join(", ", missing)}). 각 문장의 본문을 전문으로 실으십시오 — 주석이나 " +
            "\"원문 그대로 적용한다\"는 지시는 상수·계수·반올림 자릿수·UDF 인자를 복원하지 못합니다.");
    }
}
```

`StringTupleComparer`는 이 파일 안에 만든다 — `(문장 종류, 대상 테이블)`을 대소문자 무시로 묶는다.
명세서는 `USESTATE`·`TSettleMst`, 단계는 `UseState`·`TSETTLEMST`로 쓴다.

```csharp
private static readonly IEqualityComparer<(string Kind, string Table)> StringTupleComparer =
    new KindTableComparer();

private sealed class KindTableComparer : IEqualityComparer<(string Kind, string Table)>
{
    public bool Equals((string Kind, string Table) x, (string Kind, string Table) y) =>
        string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string Kind, string Table) obj) =>
        HashCode.Combine(
            obj.Kind?.ToUpperInvariant()?.GetHashCode() ?? 0,
            obj.Table?.ToUpperInvariant()?.GetHashCode() ?? 0);
}
```

- [ ] **Step 4: 배선한다** (`VerificationPipelineOrchestrator.cs`)

3169행 근처:

```csharp
var conditionColumns = MergeSpecMaterials(
    SpecConditionColumnExtractor.Extract(specs),
    SpecRoundingShapeExtractor.Extract(specs));

// 명세서가 문장 단위로 확정한 사실(DML 범위 표·갱신 절·지역 변수 표). 조건 컬럼과
// 같은 이유로 재시도 루프 밖에서 한 번만 만든다 - 단계마다 뽑아도 결과가 같다.
var statementFacts = SpecStatementFactsExtractor.Extract(specs);
```

3224행 근처:

```csharp
var stepResult = _validator.ValidateBatchStep(
    content, step, knownTableNames, conditionColumns, stepInterfaces, runRowOwnedTables,
    statementFacts);
```

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter MechanicalValidatorTests`
Expected: PASS (새 3건 포함, 기존 전건 유지)

- [ ] **Step 6: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 전건 통과, **건너뜀 0**

- [ ] **Step 7: 커밋**

```bash
git add -A && git commit -m "feat: 단계 검사가 명세서 DML 범위 표의 문장 수를 대조한다"
```

---

### Task 4: 검사 B — 앵커 문장의 조인 키·술어 컬럼 누락

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`, `tests/ReSet.Core.Tests/AxisBGoldenCaseTests.cs`

**Interfaces:**
- Consumes: Task 3의 `facts`/`statements` 해석 블록
- Produces: `CheckAnchoredStatementFacts(facts, statements, step, result)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
// ─────────────────────────────────────────────────────────────────────
// 검사 B - 앵커 문장의 조인 키·최상위 WHERE 술어 컬럼 누락.
// S07 🟠: 갱신 13의 최상위 WHERE(Y.YMD, Y.PGNAME)가 통째로 빠졌다.
// S11 🟠: 갱신 9의 TPLCardEDIMst 조인에서 YMD·UseState 결합이 빠졌다.
// ─────────────────────────────────────────────────────────────────────

[Fact]
public void ValidateBatchStep_AnchoredStatementMissingPredicateColumn_ShouldBeAnError()
{
    var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
    {
        ["dbo.UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
            new[] { new SpecDmlRow("UPDATE", 13, 410, "TSettleMst",
                new[] { "PLTID", "ID", "YMD", "PGNAME" }, new[] { "PLTID", "ID" },
                Array.Empty<string>(), Array.Empty<string>()) },
            Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
    };

    var markdown = "### S07 단계\n\n```sql\n" +
        "/* U13: 카드사 원가 반영 */\n" +
        "UPDATE Y SET Y.CLCOMM = X.Amt FROM dbo.TSettleMst AS Y\n" +
        "INNER JOIN CardCost AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;\n" +
        "```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
        new Dictionary<string, SpecConditions>(), null, null, facts);

    Assert.Contains(result.Errors, e => e.Contains("갱신 13") && e.Contains("YMD"));
    Assert.Contains(result.Errors, e => e.Contains("PGNAME"));
}

[Fact]
public void ValidateBatchStep_AnchoredStatementMissingJoinKey_ShouldBeAnError()
{
    var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
    {
        ["dbo.UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
            new[] { new SpecDmlRow("UPDATE", 9, 300, "TSettleMst",
                new[] { "PLTID" }, new[] { "PLTID", "YMD", "UseState" },
                Array.Empty<string>(), Array.Empty<string>()) },
            Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
    };

    var markdown = "### S11 단계\n\n```sql\n" +
        "-- 갱신 9\n" +
        "UPDATE A SET A.EDIReqYMD = E.ReqYMD FROM dbo.TSettleMst AS A\n" +
        "INNER JOIN dbo.TPLCardEDIMst AS E ON A.PLTID = E.PLTID\n" +
        "WHERE A.PLTID > 0;\n" +
        "```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, LegacyStep("S11"), new[] { "dbo.TSettleMst" },
        new Dictionary<string, SpecConditions>(), null, null, facts);

    Assert.Contains(result.Errors, e => e.Contains("조인 키") && e.Contains("YMD"));
}

[Fact]
public void ValidateBatchStep_WithoutAnchors_AsksForAnchorsOnce()
{
    var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
    {
        ["dbo.UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
            new[]
            {
                new SpecDmlRow("UPDATE", 1, 30, "TSettleMst", new[] { "YMD" },
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                new SpecDmlRow("UPDATE", 2, 58, "TSettleMst", new[] { "YMD" },
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
            },
            Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
    };

    var markdown = "### S07 단계\n\n```sql\n" +
        "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
        "UPDATE A SET A.CLVT = 2 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
        "```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
        new Dictionary<string, SpecConditions>(), null, null, facts);

    Assert.Single(result.Errors, e => e.Contains("갱신 번호를 주석"));
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter AnchoredStatement`
Expected: FAIL — 오류가 나지 않는다.

- [ ] **Step 3: 구현한다**

Task 3의 해석 블록에 두 줄을 더한다:

```csharp
SafeCheck(() => CheckAnchoredStatementFacts(facts!, statements, step, result));
```

```csharp
/// <summary>
/// 앵커가 달린 문장이 명세서 그 행의 조인 키와 최상위 WHERE 술어 컬럼을
/// 전부 담았는지 본다.
///
/// [POQSettleBatch1 축 B 감사]
/// S07 🟠 - 갱신 13의 최상위 WHERE(Y.YMD = @pi_strYMD, Y.PGNAME IN …)가 통째로
/// 빠졌다. (PLTID, ID)가 유일하지 않은 배포에서는 기준일 밖의 행까지 갱신된다.
/// S11 🟠 - 갱신 9의 TPLCardEDIMst 결합에서 YMD·UseState가 빠져 같은 금액의
/// 다른 일자 행까지 매칭된다.
///
/// [왜 앵커가 달린 문장만 보는가]
/// 순서로 대응시키면(k번째 UPDATE ↔ 갱신 k) 단계가 문장 하나를 빼먹는 순간
/// 이후가 전부 어긋나 오탐이 쏟아진다. S07이 정확히 10개를 빼먹은 문서다.
/// 앵커가 하나도 없으면 요구를 1건만 내고(아래) 문장별 오류는 내지 않는다.
///
/// [왜 이름만 보고 값은 보지 않는가]
/// 같은 조건을 명세서는 `UseState IN (0)`, 단계는 `UseState = 0`으로 쓴다.
/// 값까지 보면 실측 미검출의 27%가 이런 동등 표현이었고 그 전부가 오탐이었다.
/// </summary>
private static void CheckAnchoredStatementFacts(
    IReadOnlyList<SpecStatementFacts> facts,
    IReadOnlyList<StepSqlStatement> statements,
    BatchStepPlan step,
    StepValidationResult result)
{
    var rows = facts.SelectMany(f => f.DmlRows).ToList();
    if (rows.Count == 0) return;

    var anchored = statements.Where(s => s.Anchor.HasValue).ToList();
    if (anchored.Count == 0)
    {
        // 앵커가 하나도 없다. 문장별 오류를 쏟지 않고 요구를 1건만 낸다.
        result.Errors.Add(
            $"{step.Code} 섹션의 SQL에 명세서의 갱신 번호가 주석으로 달려 있지 않습니다. " +
            "각 DML 문장 바로 앞에 `/* U13: … */`처럼 갱신 번호를 다십시오 — 번호가 있어야 " +
            "명세서 DML 범위 표의 조인 키·술어 컬럼과 문장 단위로 대조됩니다.");
        return;
    }

    foreach (var statement in anchored)
    {
        var row = rows.FirstOrDefault(r =>
            r.Ordinal == statement.Anchor!.Value &&
            r.Kind.Equals(statement.Kind, StringComparison.OrdinalIgnoreCase));
        if (row == null) continue;

        var present = new HashSet<string>(
            statement.PredicateColumns.Concat(statement.JoinColumns), StringComparer.OrdinalIgnoreCase);

        ReportMissing("최상위 WHERE 술어 컬럼", row.PredicateColumns);
        ReportMissing("조인 키", row.JoinKeys);

        void ReportMissing(string label, IReadOnlyList<string> expected)
        {
            var missing = expected.Where(c => !present.Contains(c)).ToList();
            if (missing.Count == 0) return;

            result.Errors.Add(
                $"{step.Code} 섹션의 {row.Kind} {row.Ordinal}(갱신 {row.Ordinal}) 문장에 명세서가 확정한 " +
                $"{label} {string.Join(", ", missing)}이(가) 없습니다. 명세서 DML 범위 표 " +
                $"{row.Kind} {row.Ordinal} 행의 값은 `{string.Join(", ", expected)}`입니다 — " +
                "이 컬럼이 빠지면 갱신 대상 행 집합이 원본과 달라집니다.");
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter AnchoredStatement`
Expected: PASS 3건

- [ ] **Step 5: 커밋**

```bash
git add -A && git commit -m "feat: 앵커 문장의 조인 키·최상위 술어 컬럼 누락을 잡는다"
```

---

### Task 5: 검사 C — 명세서에 없는 술어·집계가 붙었는가

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: Task 3의 해석 블록, `BatchControlContract.Tables`
- Produces: `CheckAnchoredStatementExtras(facts, statements, step, result)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
// ─────────────────────────────────────────────────────────────────────
// 검사 C - 명세서에 없는 술어·집계가 붙었는가.
// S09 🟠: -9 사전 검증 EXISTS에 SM.TxAmt = 0을 하나 더 붙여 가드가 좁아졌다.
// S07 🟠: 명세서에 없는 HAVING SUM(TxAmt) = 0 집계를 원본 로직으로 서술했다.
// ─────────────────────────────────────────────────────────────────────

[Fact]
public void ValidateBatchStep_AnchoredStatementWithExtraPredicateColumn_ShouldBeAnError()
{
    var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
    {
        ["dbo.UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
            new[] { new SpecDmlRow("UPDATE", 1, 30, "TSettleMst",
                new[] { "YMD", "OutState" }, Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>()) },
            Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
    };

    var markdown = "### S09 단계\n\n```sql\n" +
        "/* U1 */\n" +
        "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A\n" +
        "WHERE A.YMD = @p AND A.OutState IN (1,5) AND A.TxAmt = 0;\n" +
        "```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, LegacyStep("S09"), new[] { "dbo.TSettleMst" },
        new Dictionary<string, SpecConditions>(), null, null, facts);

    Assert.Contains(result.Errors, e => e.Contains("TxAmt") && e.Contains("명세서에 없는"));
}

[Fact]
public void ValidateBatchStep_GroupingWhereSpecHasNone_ShouldBeAnError()
{
    var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
    {
        ["dbo.UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
            new[] { new SpecDmlRow("UPDATE", 7, 223, "TSettleMst",
                new[] { "PLTID" }, Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>()) },
            Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
    };

    var markdown = "### S07 단계\n\n```sql\n" +
        "/* U7 */\n" +
        "UPDATE Y SET Y.CLCOMM = 0 FROM dbo.TSettleMst AS Y\n" +
        "WHERE Y.PLTID IN (SELECT PLTID FROM dbo.TTx GROUP BY PLTID HAVING SUM(TxAmt) = 0);\n" +
        "```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
        new Dictionary<string, SpecConditions>(), null, null, facts);

    Assert.Contains(result.Errors, e => e.Contains("GROUP BY") || e.Contains("집계"));
}

[Fact]
public void ValidateBatchStep_BatchControlColumnsAreNotExtras()
{
    // 단계는 배치 제어 컬럼으로 자기 실행을 한정한다. 이것을 결함으로 들면
    // 모든 단계가 걸린다.
    var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
    {
        ["dbo.UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
            new[] { new SpecDmlRow("UPDATE", 1, 30, "TSettleMst",
                new[] { "YMD" }, Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>()) },
            Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
    };

    var markdown = "### S09 단계\n\n```sql\n" +
        "/* U1 */\n" +
        "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A\n" +
        "WHERE A.YMD = @p AND A.RunId = @runId;\n" +
        "```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, LegacyStep("S09"), new[] { "dbo.TSettleMst" },
        new Dictionary<string, SpecConditions>(), null, null, facts);

    Assert.DoesNotContain(result.Errors, e => e.Contains("명세서에 없는"));
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter ExtraPredicate`
Expected: FAIL

- [ ] **Step 3: 구현한다**

```csharp
SafeCheck(() => CheckAnchoredStatementExtras(facts!, statements, step, result));
```

```csharp
/// <summary>
/// 앵커가 달린 문장에 명세서 그 행에 없는 술어 컬럼이나 집계가 붙었는지 본다.
///
/// [POQSettleBatch1 축 B 감사]
/// S09 🟠 - `-9` 사전 검증 EXISTS에 `SM.TxAmt = 0`을 하나 더 붙였다. 이미 지급
/// 처리된 행이 TxAmt <> 0이면 원본은 -9로 즉시 반환하는데 단계는 통과시켜
/// DELETE → INSERT로 지급 확정 원장을 다시 만든다.
/// S07 🟠 - 명세서에 없는 `HAVING SUM(TxAmt) = 0` 집계를 원본 로직으로 서술했다.
/// 그대로 구현하면 갱신 대상이 PLTID 합계 0인 건으로 좁혀진다.
///
/// [예외 목록이 필요한 이유]
/// 단계는 배치 제어 컬럼(RunId·StepCode·BatchYmd 등)으로 자기 실행을 한정한다.
/// 그것까지 "명세서에 없는 술어"로 들면 모든 단계가 걸려 검사의 변별력이 사라진다.
/// </summary>
private static void CheckAnchoredStatementExtras(
    IReadOnlyList<SpecStatementFacts> facts,
    IReadOnlyList<StepSqlStatement> statements,
    BatchStepPlan step,
    StepValidationResult result)
{
    var rows = facts.SelectMany(f => f.DmlRows).ToList();
    if (rows.Count == 0) return;

    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var table in BatchControlContract.Tables)
    {
        foreach (var column in table.Columns) allowed.Add(column.Name);
    }

    foreach (var statement in statements.Where(s => s.Anchor.HasValue))
    {
        var row = rows.FirstOrDefault(r =>
            r.Ordinal == statement.Anchor!.Value &&
            r.Kind.Equals(statement.Kind, StringComparison.OrdinalIgnoreCase));
        if (row == null) continue;

        var known = new HashSet<string>(
            row.PredicateColumns.Concat(row.JoinKeys).Concat(row.GroupBy).Concat(row.OrderBy),
            StringComparer.OrdinalIgnoreCase);

        var extras = statement.PredicateColumns
            .Where(c => !known.Contains(c) && !allowed.Contains(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extras.Count > 0)
        {
            result.Errors.Add(
                $"{step.Code} 섹션의 {row.Kind} {row.Ordinal}(갱신 {row.Ordinal}) 문장이 명세서에 없는 " +
                $"술어 컬럼 {string.Join(", ", extras)}을(를) 씁니다. 명세서 DML 범위 표 " +
                $"{row.Kind} {row.Ordinal} 행의 최상위 술어 컬럼은 `{string.Join(", ", row.PredicateColumns)}`뿐입니다 — " +
                "조건을 더하면 원본이 처리하던 행이 처리되지 않습니다.");
        }

        if (statement.HasGrouping && row.GroupBy.Count == 0)
        {
            result.Errors.Add(
                $"{step.Code} 섹션의 {row.Kind} {row.Ordinal}(갱신 {row.Ordinal}) 문장에 GROUP BY 또는 " +
                "HAVING 집계가 있는데, 명세서 DML 범위 표의 GROUP BY 칸은 비어 있습니다(`—`). " +
                "원본에 없는 집계를 더하면 갱신 대상 행 집합이 좁아집니다.");
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter ValidateBatchStep`
Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add -A && git commit -m "feat: 명세서에 없는 술어 컬럼·집계가 문장에 붙었는지 본다"
```

---

### Task 6: 검사 D — 지역 변수 표의 변수가 선언되었는가

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `SpecStatementFacts.LocalVariables`
- Produces: `CheckSpecLocalVariablesDeclared(facts, stepMarkdown, step, result)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
// ─────────────────────────────────────────────────────────────────────
// 검사 D - 지역 변수 선언. S14 🔴: 지역 변수 9개가 DECLARE 없이 쓰였고
// 그중 금액 3종이 원본 MONEY인데 변수명은 int를 시사한다.
// ─────────────────────────────────────────────────────────────────────

private static IReadOnlyDictionary<string, SpecStatementFacts> FactsWithVariables(
    params SpecLocalVariable[] variables) =>
    new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
    {
        ["dbo.UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
            Array.Empty<SpecDmlRow>(), Array.Empty<SpecSetTarget>(), variables)
    };

[Fact]
public void ValidateBatchStep_SpecVariableUsedWithoutDeclare_ShouldBeAnErrorWithItsType()
{
    var markdown = "### S14 단계\n\n```sql\n" +
        "DECLARE @v_currentStepId INT = 0;\n" +
        "SET @v_intCLTotal = 100;\n" +
        "```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, LegacyStep("S14"), new[] { "dbo.TSettleMiss" },
        new Dictionary<string, SpecConditions>(), null, null,
        FactsWithVariables(new SpecLocalVariable("@v_intCLTotal", "MONEY", false)));

    Assert.Contains(result.Errors, e => e.Contains("@v_intCLTotal") && e.Contains("MONEY"));
}

[Fact]
public void ValidateBatchStep_SystemValues_AreNotRequiredToBeDeclared()
{
    var markdown = "### S14 단계\n\n```sql\nIF @@ERROR <> 0 RETURN -1;\n```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, LegacyStep("S14"), new[] { "dbo.TSettleMiss" },
        new Dictionary<string, SpecConditions>(), null, null,
        FactsWithVariables(new SpecLocalVariable("@@ERROR", "SQL Server 시스템 값", true)));

    Assert.DoesNotContain(result.Errors, e => e.Contains("@@ERROR"));
}

[Fact]
public void ValidateBatchStep_VariableNotUsedByTheStep_IsSilent()
{
    // 단계가 그 변수를 아예 쓰지 않으면 선언을 요구할 이유가 없다.
    var markdown = "### S14 단계\n\n```sql\nSELECT 1;\n```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, LegacyStep("S14"), new[] { "dbo.TSettleMiss" },
        new Dictionary<string, SpecConditions>(), null, null,
        FactsWithVariables(new SpecLocalVariable("@v_intCLTotal", "MONEY", false)));

    Assert.DoesNotContain(result.Errors, e => e.Contains("@v_intCLTotal"));
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter SpecVariable`
Expected: FAIL

- [ ] **Step 3: 구현한다**

Task 3의 해석 블록에서 이 검사는 `statements`가 아니라 **마크다운 본문**을 쓴다:

```csharp
SafeCheck(() => CheckSpecLocalVariablesDeclared(facts!, stepMarkdown!, step, result));
```

```csharp
/// <summary>
/// 명세서 지역 변수 표의 변수가 단계에서 쓰이는데 DECLARE가 없는지 본다.
///
/// [POQSettleBatch1 축 B 감사 S14 🔴]
/// 지역 변수 9개가 선언 없이 쓰였다. 그중 @v_intCLTotal·@v_intCLComm·@v_intCLVT는
/// 원본에서 MONEY인데 이름은 int를 시사한다 - 이행자가 명세서 표를 따로 보지 않으면
/// int로 선언해 금액이 절삭된다. 그래서 메시지에 타입을 함께 싣는다.
///
/// [시스템 값을 빼는 이유]
/// 표는 @@ERROR·@@ROWCOUNT를 `SQL Server 시스템 값` 구분으로 함께 싣는다.
/// 이것은 선언 대상이 아니다.
/// </summary>
private static void CheckSpecLocalVariablesDeclared(
    IReadOnlyList<SpecStatementFacts> facts,
    string stepMarkdown,
    BatchStepPlan step,
    StepValidationResult result)
{
    var variables = facts.SelectMany(f => f.LocalVariables)
        .Where(v => !v.IsSystemValue)
        .DistinctBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (variables.Count == 0) return;

    foreach (var (cleaned, _) in CleanedSqlFences(stepMarkdown))
    {
        // 선언이 있는지는 펜스별로 본다 - 문서 전체를 한 덩어리로 보면
        // 다른 펜스의 선언이 이 펜스의 사용을 덮는다.
        foreach (var variable in variables)
        {
            var used = Regex.IsMatch(cleaned, $@"(?<![\w@]){Regex.Escape(variable.Name)}\b",
                RegexOptions.IgnoreCase);
            if (!used) continue;

            var declared = Regex.IsMatch(
                cleaned, $@"\bDECLARE\b[^;]*?{Regex.Escape(variable.Name)}\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (declared) continue;

            var type = string.IsNullOrWhiteSpace(variable.TypeOrKind) ? "명세서 지역 변수 표 참조" : variable.TypeOrKind;
            result.Errors.Add(
                $"{step.Code} 섹션이 `{variable.Name}`을(를) 선언 없이 씁니다. 명세서 지역 변수 표는 " +
                $"이 변수의 타입을 `{type}`으로 확정합니다 — DECLARE를 두고 그 타입을 그대로 쓰십시오. " +
                "타입을 이름으로 추측하면 금액 변수가 정수로 선언되어 절삭됩니다.");
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter SpecVariable`
Expected: PASS 3건

- [ ] **Step 5: 커밋**

```bash
git add -A && git commit -m "feat: 명세서 지역 변수 표의 변수가 선언 없이 쓰이는지 본다"
```

---

### Task 7: 검사 E — 상태 변수 초기값이 오류 코드와 겹치는가

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `BatchStepPlan.ErrorCodes`
- Produces: `CheckStepIdInitialValue(stepMarkdown, step, result)` — 명세서 재료가 필요 없으므로 `statementFactsByProcedure`와 무관하게 부른다

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
// ─────────────────────────────────────────────────────────────────────
// 검사 E - 상태 변수 초기값. S13 🟠: @v_currentStepId INT = 0으로 시작하고
// CATCH가 SET @po_intRetVal = @v_currentStepId를 무조건 수행해, DML 바깥에서
// 난 장애(커서 DECLARE/OPEN, 행 0건)가 성공 코드 0으로 보고된다.
// ─────────────────────────────────────────────────────────────────────

private static BatchStepPlan StepWithCodes(string code, params string[] errorCodes) => new(
    Code: code, Name: $"{code} 단계",
    LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_SUMMARY_ETC" },
    TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" },
    ErrorCodes: errorCodes, Chunkable: false, SchemaTables: Array.Empty<string>());

[Fact]
public void ValidateBatchStep_StatusVariableInitializedToSuccessCode_ShouldBeAnError()
{
    var markdown = "### S13 단계\n\n```sql\n" +
        "DECLARE @v_currentStepId INT = 0;\n" +
        "BEGIN TRY\n  SET @v_currentStepId = 1001;\nEND TRY\n" +
        "BEGIN CATCH\n  SET @po_intRetVal = @v_currentStepId;\nEND CATCH\n" +
        "```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, StepWithCodes("S13", "-9", "0", "1001", "1002"), new[] { "dbo.TSettleByOUT" },
        new Dictionary<string, SpecConditions>());

    Assert.Contains(result.Errors, e => e.Contains("@v_currentStepId") && e.Contains("0"));
}

[Fact]
public void ValidateBatchStep_StatusVariableInitializedOutsideErrorCodeSet_IsSilent()
{
    var markdown = "### S13 단계\n\n```sql\n" +
        "DECLARE @v_currentStepId INT = -999;\n" +
        "BEGIN CATCH\n  SET @po_intRetVal = @v_currentStepId;\nEND CATCH\n" +
        "```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, StepWithCodes("S13", "-9", "0", "1001", "1002"), new[] { "dbo.TSettleByOUT" },
        new Dictionary<string, SpecConditions>());

    Assert.DoesNotContain(result.Errors, e => e.Contains("@v_currentStepId"));
}

[Fact]
public void ValidateBatchStep_NoCatchReturnStructure_IsSilent()
{
    var markdown = "### S13 단계\n\n```sql\nDECLARE @v_currentStepId INT = 0;\nSELECT 1;\n```\n";

    var result = new MechanicalValidator().ValidateBatchStep(
        markdown, StepWithCodes("S13", "0", "1001"), new[] { "dbo.TSettleByOUT" },
        new Dictionary<string, SpecConditions>());

    Assert.DoesNotContain(result.Errors, e => e.Contains("@v_currentStepId"));
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter StatusVariable`
Expected: FAIL

- [ ] **Step 3: 구현한다**

`ValidateBatchStep`의 검사 목록에 더한다(명세서 재료와 무관):

```csharp
SafeCheck(() => CheckStepIdInitialValue(stepMarkdown, step, result));
```

```csharp
/// <summary>
/// CATCH가 돌려주는 상태 변수의 초기값이 업무 오류 코드나 성공 코드와 겹치는지 본다.
///
/// [POQSettleBatch1 축 B 감사]
/// S13 🟠 - `DECLARE @v_currentStepId INT = 0`으로 시작하고 CATCH가 그 값을 무조건
/// 반환한다. 커서 DECLARE·OPEN·첫 FETCH에서 난 장애와 행 0건일 때의 COMMIT이
/// 성공 코드 0으로 보고된다. 실패가 성공으로 보고되면 오케스트레이터가 단계를
/// Succeeded로 기록해 재실행하지 않고, TSettleByOUT 보정이 누락된 채 후속 정산이 진행된다.
/// S05 🟡 - 같은 모양의 `= -9`. 기정산 조건과 사전 검증 질의의 SQL 장애가 같은 코드로 보고된다.
///
/// 명세서 재료가 필요 없다 - 목차의 ErrorCodes와 단계 SQL만 본다.
/// </summary>
private static void CheckStepIdInitialValue(
    string stepMarkdown, BatchStepPlan step, StepValidationResult result)
{
    if (step.ErrorCodes.Count == 0) return;

    var codes = new HashSet<string>(
        step.ErrorCodes.Select(c => c.Trim()).Where(c => c.Length > 0), StringComparer.Ordinal);
    codes.Add("0");   // 성공 코드는 목차에 없을 수도 있다

    foreach (var (cleaned, _) in CleanedSqlFences(stepMarkdown))
    {
        var returned = Regex.Match(
            cleaned, @"SET\s+@po_intRetVal\s*=\s*(?<var>@\w+)", RegexOptions.IgnoreCase);
        if (!returned.Success) continue;

        var name = returned.Groups["var"].Value;
        var declared = Regex.Match(
            cleaned, $@"DECLARE\s+{Regex.Escape(name)}\s+\w+(\s*\(\s*\d+\s*\))?\s*=\s*(?<value>-?\d+)",
            RegexOptions.IgnoreCase);
        if (!declared.Success) continue;

        var initial = declared.Groups["value"].Value;
        if (!codes.Contains(initial)) continue;

        result.Errors.Add(
            $"{step.Code} 섹션이 `{name}`을(를) `{initial}`로 초기화하고 CATCH에서 그 값을 " +
            $"`@po_intRetVal`로 돌려줍니다. `{initial}`은(는) 이 단계의 오류 코드 집합 " +
            $"({string.Join(", ", step.ErrorCodes)})에 이미 있는 값이라, DML 바깥에서 난 장애가 " +
            "업무 코드(성공 코드일 수도 있습니다)로 보고됩니다. 어느 코드와도 겹치지 않는 " +
            "값으로 초기화하십시오.");
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter StatusVariable`
Expected: PASS 3건

- [ ] **Step 5: 전체 테스트**

Run: `dotnet test`
Expected: 전건 통과, 건너뜀 0

- [ ] **Step 6: 커밋**

```bash
git add -A && git commit -m "feat: CATCH가 돌려주는 상태 변수의 초기값이 오류 코드와 겹치는지 본다"
```

---

### Task 8: 프롬프트 — 앵커 요구와 규약 2조항, `maxTries` 5

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs:3887-3940` (`GenerateBatchStepSectionAsync`)
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:3165` (`maxTries`)
- Modify: `src/ReSet.Core/Services/CacheManager.cs:160` 또는 번들 캐시 축(**Step 1에서 확인**)
- Test: `tests/ReSet.Core.Tests/AiServiceTests.cs`

- [ ] **Step 1: 번들 캐시 축을 확인한다**

Run:
```bash
grep -rn 'CurrentCacheFormatVersion' src --include='*.cs'
grep -rn 'GetOrCreate\|캐시' src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs | grep -i 'step\|plan' | head
```
`GenerateBatchStepSectionAsync`의 결과가 `CacheManager`를 거치면 `CurrentCacheFormatVersion`을 15 → 16으로 올린다. 거치지 않으면(단계 본문이 매 실행 새로 생성되면) **버전 인상 없이** 다음 스텝으로 간다. 어느 쪽인지 확인한 결과를 커밋 메시지에 한 줄로 적는다.

- [ ] **Step 2: 실패하는 테스트를 쓴다**

```csharp
[Fact]
public void BatchStepPrompt_DemandsAnchorCommentsAndForbidsSemanticSubstitutions()
{
    var prompt = AiService.BuildBatchStepSectionPromptForTest(
        new BatchStepPlan("S07", "예외 정책 적용",
            new[] { "dbo.UP_UTIL_SETTLE_EXCEPTION_PROC" },
            new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
            new[] { "-9" }, false, Array.Empty<string>()));

    Assert.Contains("갱신 번호", prompt);
    Assert.Contains("CROSS APPLY", prompt);
    Assert.Contains("@@ROWCOUNT", prompt);
}
```

`BuildBatchStepSectionPromptForTest`가 없으면, 프롬프트를 만드는 private 메서드를 `internal`로 열고 `InternalsVisibleTo`를 쓰는 이 저장소의 기존 방식을 따른다. `AiServiceTests.cs`에서 유사 선례를 찾아 같은 방식으로 맞춘다.

- [ ] **Step 3: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter BatchStepPrompt_Demands`
Expected: FAIL

- [ ] **Step 4: 프롬프트에 세 문단을 더한다**

```csharp
// [축 B 감사가 요구하는 세 가지 - POQSettleBatch1 2026-08-24]
// 앵커가 없으면 단계 검사가 문장을 명세서의 갱신 N에 붙일 수 없어 조인 키·술어
// 컬럼 대조가 통째로 꺼진다. 규약 두 조항은 실측에서 금액·행 집합을 바꾼 치환이다.
sb.AppendLine("### 문장 앵커와 의미 보존 (필수)");
sb.AppendLine();
sb.AppendLine("- **각 DML 문장 바로 앞에 명세서의 갱신 번호를 주석으로 답니다.** " +
    "`/* U13: 카드사 원가 반영 */` 형식입니다(`갱신 13`·`UPDATE 13`도 인정됩니다). " +
    "번호가 있어야 검증이 명세서 DML 범위 표의 조인 키·술어 컬럼과 문장 단위로 대조합니다.");
sb.AppendLine("- **스칼라 하위질의를 `CROSS APPLY`/`OUTER APPLY`로 바꾸지 마십시오.** " +
    "명세서가 대입 우변을 스칼라 하위질의로 적은 자리는 무결과일 때 `NULL`이 대입되는 자리입니다. " +
    "`CROSS APPLY`는 그 행을 갱신 대상에서 통째로 제외해, 같은 문장의 다른 컬럼 대입까지 사라집니다.");
sb.AppendLine("- **비집계 조회 여러 문장을 집계 한 문장으로 합치지 마십시오.** " +
    "명세서가 `SELECT @v = col` 뒤에 `@@ROWCOUNT > 1` 분기를 둔 자리는 \"없음\"과 \"여럿\"을 " +
    "가르는 자리입니다. `MAX(col)` 한 문장으로 합치면 \"없음\"의 표현이 `0`에서 `NULL`로 바뀌어 " +
    "분기가 역전됩니다.");
sb.AppendLine();
```

이 블록은 **`floorFeedback`보다 앞**, 캐시 접두사 안쪽에 둔다. `floorFeedback`은 반드시 프롬프트 말미에 붙는다는 기존 주석(`AiService.cs:3887`)을 깨지 않는다.

- [ ] **Step 5: `maxTries`를 5로 올린다**

`VerificationPipelineOrchestrator.cs:3165`:

```csharp
// 검사가 5개 늘었다(문장 개수·조인 키·추가 술어·지역 변수·상태 변수 초기값).
// 2회로는 첫 시도에서 2건 이상 걸린 단계가 하한 미달로 확정된다 - 축 A는 6회다.
const int maxTries = 5;   // 최초 1회 + 재시도 4회
```

- [ ] **Step 6: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter BatchStepPrompt_Demands`
Expected: PASS

- [ ] **Step 7: 전체 테스트**

Run: `dotnet test`
Expected: 전건 통과, 건너뜀 0

- [ ] **Step 8: 커밋**

```bash
git add -A && git commit -m "feat: 단계 프롬프트가 문장 앵커를 요구하고 의미 바꾸는 치환 둘을 금지한다"
```

---

### Task 9: 코퍼스 스윕 — 거짓 양성 0을 확인한다

**Files:**
- Create: 스크래치 하네스 (`$SCRATCH/sweep-stepl1/`, 저장소에 커밋하지 않는다)

- [ ] **Step 1: 하네스를 만든다**

```bash
SCRATCH=/private/tmp/claude-501/-Users-payletter-git-root-ReSet/c5a30bfa-e9ae-4359-af7c-b2e0b422cf4b/scratchpad
mkdir -p $SCRATCH/sweep-stepl1
```
(다른 세션에서 실행한다면 그 세션의 스크래치패드 경로로 바꾼다. 저장소에는 커밋하지 않는다.)

`Program.cs`는 `output/Jobs/*/agent/steps/*.md` 326개를 돌면서, 각 단계에 대해
`PlanStructure.md`의 `BatchStepPlan`과 그 단계가 흡수한 SP의 `Spec.md`를 재료로
`ValidateBatchStep`을 부르고 **새 검사 5개의 오류만** 골라 CSV로 낸다.
`sweep-main`/`sweep-wt`의 기존 `Program.cs`를 골격으로 삼는다(`--sp` 인자 처리, `ProjectReference` 방식 동일).

- [ ] **Step 2: 스윕을 돌린다**

Run:
```bash
dotnet run --project $SCRATCH/sweep-stepl1 > $SCRATCH/sweep-stepl1/result.csv
wc -l $SCRATCH/sweep-stepl1/result.csv
cut -d, -f3 $SCRATCH/sweep-stepl1/result.csv | sort | uniq -c | sort -rn
```

- [ ] **Step 3: 이번 9건 자리가 잡히는지 확인한다**

Run:
```bash
grep -E 'POQSettleBatch1,(S07|S09|S11|S13|S14)' $SCRATCH/sweep-stepl1/result.csv
```
Expected: 다섯 단계가 모두 나오고, S07은 검사 A·B·C가, S14는 D가, S13은 E가 잡는다.

**하나라도 안 잡히면 그 검사로 돌아가 테스트를 추가하고 고친다.** 이것이 이 계획의 핵심 통과 조건이다.

- [ ] **Step 4: 거짓 양성을 확인한다**

검출 건수가 30건 이하면 **전건**, 그보다 많으면 검사별로 무작위 10건씩 표본을 뽑아
해당 `Spec.md`와 단계 파일을 직접 열어 실제 결함인지 확인한다. 오탐이 하나라도 있으면
그 원인을 침묵 규칙이나 예외 목록에 반영하고 스윕을 다시 돌린다.

A·D가 대량 검출될 수 있다. 그것은 오탐이 아니라 축 B 산출물 전반의 실제 상태다 —
그 사실을 다음 스텝의 기록에 남긴다.

- [ ] **Step 5: 결과를 기록한다**

`docs/known-defects.md`에 스윕 결과를 한 문단으로 적는다: 검사별 검출 건수, 표본 확인
방법, 거짓 양성 수. 커밋한다.

```bash
git add docs/known-defects.md && git commit -m "docs: 축 B 단계 검사 코퍼스 스윕 결과를 적는다"
```

---

### Task 10: 재생성 실측과 문서 갱신

**Files:**
- Modify: `docs/architecture.md`, `docs/known-defects.md`, `docs/audit-defect-catalog.md`
- Modify: `output/Jobs/POQSettleBatch1/agent/steps/*.md` (재생성 산출물)

- [ ] **Step 1: POQSettleBatch1 번들을 재생성한다**

Run: README의 번들 재생성 절차를 따른다(오프라인 스냅샷을 쓰므로 DB 연결이 필요 없다).
단계 16개가 생성되고, 새 검사에 걸린 단계는 `maxTries = 5` 안에서 자가 수정된다.
진행 중 `* SNN 단계가 하한 검사를 통과하지 못해 다시 생성합니다` 로그를 보관한다.

- [ ] **Step 2: 9건이 사라졌는지 확인한다**

다섯 단계를 직접 열어 감사 보고서의 앵커와 대조한다.

```bash
REPORT=output/Jobs/POQSettleBatch1/consistency/ConsistencyReport.md
grep -E '^\| (🔴|🟠) \| S(07|09|11|13|14) ' $REPORT
```

각 행에 대해:
- S07 🔴 — `grep -c 'UPDATE' output/Jobs/POQSettleBatch1/agent/steps/S07.md`가 명세서의 갱신 수와 맞는가
- S07 🟠 U13 — 갱신 13 문장에 `YMD`·`PGNAME`이 있는가
- S07 🟠 HAVING — `HAVING SUM(TxAmt)`가 사라졌는가
- S09 🟠 ×2 — `TxAmt = 0`이 사라졌는가, `CROSS APPLY`가 스칼라 하위질의로 돌아왔는가
- S11 🟠 — 갱신 9 조인에 `YMD`·`UseState`가 있는가
- S13 🟠 — `@v_currentStepId` 초기값이 오류 코드 집합 밖인가
- S14 🔴 — 지역 변수 9개에 `DECLARE`가 있고 금액 3종이 `MONEY`인가
- S14 🟠 — `MAX(ID)` 대신 비집계 2단계 조회로 돌아왔는가

**닫히지 않은 것이 있으면 그 사실을 그대로 기록한다.** 규약 2조항은 프롬프트 유도라
검사보다 약하다 — S09·S14의 구조 변경 2건이 남을 수 있고, 그러면 그것을 다음 회차
과제로 남긴다.

- [ ] **Step 3: 문서를 갱신한다**

- `docs/architecture.md` — 단계 검사가 명세서 기계 확정 표를 받는다는 사실과 검사 5개 이름
- `docs/known-defects.md` — 닫힌 항목을 `~~취소선~~`으로 바꾸고 닫은 근거(스윕·재생성 실측)를 적는다. 닫히지 않은 것은 열린 항목으로 남긴다
- `docs/audit-defect-catalog.md` — POQSettleBatch1 축 B 회차 행에 결과를 적는다

- [ ] **Step 4: 커밋**

```bash
git add -A
git commit -m "fix: 축 B 단계 검사로 POQSettleBatch1 🔴 2건·🟠 N건을 닫는다"
```

- [ ] **Step 5: 결과를 보고한다**

9건 각각에 대해 닫힘/열림과 근거를 한 줄씩 적어 보고한다. 열린 것이 있으면 그 이유와
다음 수단(검사로 갈지, 규약을 조일지)을 함께 적는다.
