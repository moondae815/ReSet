# 하위 스코프 술어 재료 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 검사 B가 "명세서가 확정한 최상위 술어 컬럼이 없다"고 말할 때, 그 컬럼이 **없어진 것**인지 하위 스코프로 **옮겨간 것**인지 구분하게 만든다.

**Architecture:** `StepSqlStatementReader`에 하위 스코프(CTE 본문·파생 테이블·`WHERE` 안의 하위질의)의 `WHERE` 컬럼만 모으는 방문자를 더하고, 그 결과를 `StepSqlStatement.SubordinatePredicateColumns`(기본값 있는 `init` 속성)로 싣는다. `CheckAnchoredStatementFacts`는 빠진 컬럼을 **컬럼 단위로** 거른다 — 하위 스코프에 있으면 그 컬럼만 침묵하고 나머지는 그대로 발화한다.

**Tech Stack:** .NET 10.0 · xUnit · `Microsoft.SqlServer.TransactSql.ScriptDom` (`TSql160Parser`, `TSqlFragmentVisitor`)

**Spec:** `docs/superpowers/specs/2026-08-26-subordinate-predicate-design.md`

## Global Constraints

- **`output/` 쓰기 금지. 읽기만 한다.** 보고서는 `docs/audit-reports/sweeps/`에 도구가 쓴다.
- **검사 C(`CheckAnchoredStatementExtras`)를 건드리지 않는다.** 설계 §5 — 다른 재료가 필요한 별개 작업이다.
- **조인 키 대조를 건드리지 않는다.** 기존 `HasOpaqueJoinSource` 가드(`MechanicalValidator.cs:6397`)가 그대로 담당한다.
- **`StepSqlStatement`의 위치 매개변수를 늘리지 않는다.** 기본값 있는 `init` 속성으로만 더한다 — 늘리면 기존 생성자 호출이 전부 깨진다.
- **코퍼스에 의존하는 테스트를 만들지 않는다.** `Skip.If`를 쓰지 않는다.
- 커밋 메시지는 저장소 관례 — 한국어 제목 + 근거를 적은 본문 + 트레일러 두 줄:
  ```
  Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01D2vaDEjbN6BZ3pJLk7A4vq
  ```
- 각 태스크 끝에서 `dotnet build`(경고 0·오류 0)와 `dotnet test`(실패 0)가 통과해야 한다.

## 파일 구조

| 파일 | 책임 | 태스크 |
|---|---|---|
| `src/ReSet.Core/Services/StepSqlStatementReader.cs` | `SubordinatePredicateColumns` 필드 · `SubordinatePredicateCollector` 방문자 · `Add(...)` 배선 | 1 |
| `tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs` | 재료가 옳게 모이는지 | 1 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | `ReportMissing`의 컬럼 단위 필터 | 2 |
| `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` | 판정이 옳게 갈리는지 | 2 |
| `docs/audit-reports/sweeps/2026-08-26-step-sweep-c.md` | 재측정 결과(도구가 만든다) | 3 |
| `docs/known-defects.md` · `docs/audit-defect-catalog.md` | 델타와 S07 U13 재판정 기록 | 3 |

## 배경 — 워커가 알아야 할 것

**지금 "최상위"가 무엇인지가 코드에 박혀 있다.** `StepSqlStatementReader`의 `ColumnCollector`가 `ScalarSubquery`와 `QueryDerivedTable` 진입을 `ExplicitVisit` 빈 구현으로 막는다. ScriptDom에서 `EXISTS`·`IN`의 하위질의도 `ScalarSubquery`이므로 셋 다 함께 막힌다. 명세서 쪽 열 이름도 `WHERE 최상위 술어 컬럼(조인 결합 포함 · 대상 한정 아님)`이라 **양쪽이 같은 기준**을 쓴다.

**문제는 이행이 구조를 바꿀 때 생긴다.** 원본이 최상위에 두었던 술어를 이행이 CTE·파생 테이블·`EXISTS`로 옮기면, 같은 기준으로 재는 대조가 "없어졌다"고 말한다. 2026-08-26 표본 판정이 그런 발화 30건을 확정했다(`docs/known-defects.md` (5-2-4)).

**핵심 관찰:** `UPDATE`/`DELETE`의 최상위 `WHERE`는 `QuerySpecification`이 아니라 `UpdateSpecification`/`DeleteSpecification`에 달린다. 따라서 문장 안에서 만나는 **모든 `QuerySpecification`은 정의상 하위 스코프**다. "여기가 최상위인가"를 따로 판정할 필요가 없다.

---

### Task 1: 하위 스코프 술어를 모은다

**Files:**
- Modify: `src/ReSet.Core/Services/StepSqlStatementReader.cs`
- Test: `tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs`

**Interfaces:**
- Consumes: `ColumnCollector`(같은 파일의 `private sealed class`) · `StepSqlStatement`(`:29`)
- Produces: `StepSqlStatement.SubordinatePredicateColumns` — `IReadOnlyList<string>` `init` 속성, 기본값 `Array.Empty<string>()`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs`의 `FlagsGroupingWhenGroupByOrHavingPresent` 앞에 넣는다:

```csharp
    // ─────────────────────────────────────────────────────────────────────
    // 하위 스코프 술어 — 원본이 최상위에 두었던 술어를 이행이 CTE·파생 테이블·
    // EXISTS로 옮기면, 최상위만 보는 PredicateColumns로는 "없어졌다"로 보인다.
    // 소실과 이전을 구분하려면 옮겨간 자리도 재료로 실어야 한다.
    // 대상 행을 거를 수 있는 세 자리(WITH·FROM·최상위 WHERE)에서만 모은다.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void CollectsCtePredicatesIntoSubordinateColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH FeeSource AS (\n" +
            "    SELECT A.PLTID, A.ID FROM dbo.TSettleMst AS A\n" +
            "    WHERE A.YMD = @p AND A.PGName = 'pointpay'\n" +
            ")\n" +
            "UPDATE Y SET Y.PGComm = 0 FROM dbo.TSettleMst AS Y\n" +
            "INNER JOIN FeeSource AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;"));

        var statement = Assert.Single(statements);
        Assert.Contains("YMD", statement.SubordinatePredicateColumns);
        Assert.Contains("PGName", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("YMD", statement.PredicateColumns);
    }

    [Fact]
    public void CollectsDerivedTablePredicatesIntoSubordinateColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.PGComm = X.Amt FROM dbo.TSettleMst AS Y\n" +
            "INNER JOIN (\n" +
            "    SELECT S.PLTID, S.ID, 1 AS Amt FROM dbo.TSettleMst AS S\n" +
            "    WHERE S.DiscountFlag = 'Y'\n" +
            ") AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;"));

        var statement = Assert.Single(statements);
        Assert.Contains("DiscountFlag", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("DiscountFlag", statement.PredicateColumns);
    }

    [Fact]
    public void CollectsExistsSubqueryPredicatesIntoSubordinateColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.OutState = 9 FROM dbo.TSettleMst AS A\n" +
            "WHERE A.UseState = 0\n" +
            "  AND EXISTS (SELECT 1 FROM dbo.TSettleMst AS B\n" +
            "              WHERE B.PLTID = A.PLTID AND B.OutState = 9);"));

        var statement = Assert.Single(statements);
        Assert.Contains("PLTID", statement.SubordinatePredicateColumns);
        Assert.Contains("UseState", statement.PredicateColumns);
        Assert.DoesNotContain("PLTID", statement.PredicateColumns);
    }

    // 갱신할 "값"을 고르는 술어이지 갱신할 "행"을 고르는 술어가 아니다. 이것을
    // 세면 우연히 이름이 같은 컬럼이 진짜 소실을 가려 잘못 침묵시킨다.
    [Fact]
    public void DoesNotCollectPredicatesFromSetClauseSubqueries()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = (SELECT TOP 1 X.Amt FROM dbo.TCost AS X WHERE X.Hidden = 1)\n" +
            "FROM dbo.TSettleMst AS Y WHERE Y.YMD = @p;"));

        var statement = Assert.Single(statements);
        Assert.DoesNotContain("Hidden", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("Hidden", statement.PredicateColumns);
    }

    [Fact]
    public void CollectsNestedSubordinateScopes()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Outer1 AS (\n" +
            "    SELECT S.PLTID FROM dbo.TSettleMst AS S\n" +
            "    WHERE S.YMD = @p\n" +
            "      AND S.PLTID IN (SELECT T.PLTID FROM dbo.TTx AS T WHERE T.Cancelled = 1)\n" +
            ")\n" +
            "UPDATE Y SET Y.OutState = 9 FROM dbo.TSettleMst AS Y\n" +
            "INNER JOIN Outer1 AS X ON X.PLTID = Y.PLTID;"));

        var statement = Assert.Single(statements);
        Assert.Contains("YMD", statement.SubordinatePredicateColumns);
        Assert.Contains("Cancelled", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void SubordinateColumnsAreEmptyWhenNoSubordinateScopeExists()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y WHERE Y.YMD = @p;"));

        Assert.Empty(Assert.Single(statements).SubordinatePredicateColumns);
    }

```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~Subordinate"
```

Expected: 컴파일 실패 — `StepSqlStatement`에 `SubordinatePredicateColumns`가 없다.

- [ ] **Step 3: 레코드에 필드를 더한다**

`src/ReSet.Core/Services/StepSqlStatementReader.cs`의 `StepSqlStatement` 선언(`:29` 부근)을 이렇게 바꾼다. **위치 매개변수는 하나도 건드리지 않는다:**

```csharp
    public sealed record StepSqlStatement(
        string Kind,
        string TargetTable,
        int? Anchor,
        IReadOnlyList<string> PredicateColumns,
        IReadOnlyList<string> JoinColumns,
        bool HasGrouping,
        bool HasOpaqueJoinSource = false,
        string? CodeAnchor = null)
    {
        /// <summary>
        /// 하위 스코프(CTE 본문·파생 테이블·최상위 WHERE 안의 하위질의)의 WHERE에
        /// 나오는 컬럼. <see cref="PredicateColumns"/>와 겹치지 않는다.
        ///
        /// [무엇을 위한 값인가] 원본이 최상위 WHERE에 두었던 술어를 이행이 하위
        /// 스코프로 옮기는 관용구가 실재한다(2026-08-26 표본 판정 30건). 최상위만
        /// 보는 대조는 그것을 "없어졌다"로 읽는다. 이 값이 있으면 검사 B가
        /// <b>소실과 이전을 구분</b>할 수 있다.
        ///
        /// [무엇을 뜻하지 않는가] 하위 스코프에 있다고 의미 동등은 아니다.
        /// 동등성은 조인이 대상 행 집합을 보존하느냐에 달렸고 그 전제는 로컬에서
        /// 검증할 수 없다. 이 값은 "옮겨갔다"까지만 말한다.
        ///
        /// [SET 절은 세지 않는다] 갱신할 "값"을 고르는 하위질의의 술어는 갱신할
        /// "행"을 고르는 술어가 아니다. 세면 우연히 이름이 같은 컬럼이 진짜 소실을
        /// 가린다.
        /// </summary>
        public IReadOnlyList<string> SubordinatePredicateColumns { get; init; }
            = Array.Empty<string>();
    }
```

파일 상단에 `using System;`이 없으면 더한다.

- [ ] **Step 4: 수집기를 만든다**

같은 파일의 `ColumnCollector` 클래스 **바로 뒤**에 넣는다:

```csharp
        /// <summary>
        /// 하위 스코프의 WHERE 컬럼만 모은다.
        ///
        /// [왜 QuerySpecification이 곧 하위 스코프인가] UPDATE·DELETE의 최상위
        /// WHERE는 QuerySpecification이 아니라 UpdateSpecification·
        /// DeleteSpecification에 달린다. 그래서 이 방문자가 만나는 모든
        /// QuerySpecification은 정의상 CTE 본문이거나 파생 테이블이거나
        /// 하위질의다 - "여기가 최상위인가"를 따로 판정할 필요가 없다.
        ///
        /// [ColumnCollector를 재사용하는 이유] 스코프마다 "그 스코프의 최상위
        /// WHERE만"이라는 같은 규칙이 적용된다. 더 안쪽 스코프는 이 방문자의
        /// 기본 순회가 각각 따로 방문해 모은다.
        /// </summary>
        private sealed class SubordinatePredicateCollector : TSqlFragmentVisitor
        {
            private readonly List<string> _columns = new();
            public IReadOnlyList<string> Columns => _columns;

            public override void Visit(QuerySpecification node)
            {
                if (node.WhereClause == null) return;

                var inner = new ColumnCollector();
                node.WhereClause.Accept(inner);
                _columns.AddRange(inner.Columns);
            }
        }
```

- [ ] **Step 5: `Add(...)`가 CTE 절을 받게 하고 수집기를 배선한다**

같은 파일의 방문자에서 세 곳을 고친다.

먼저 `Visit` 셋이 CTE 절을 넘기게 한다:

```csharp
            public override void Visit(UpdateStatement node) =>
                Add("UPDATE", node, node.UpdateSpecification?.Target,
                    node.UpdateSpecification?.WhereClause, node.UpdateSpecification?.FromClause,
                    node.WithCtesAndXmlNamespaces);

            public override void Visit(DeleteStatement node) =>
                Add("DELETE", node, node.DeleteSpecification?.Target,
                    node.DeleteSpecification?.WhereClause, node.DeleteSpecification?.FromClause,
                    node.WithCtesAndXmlNamespaces);

            public override void Visit(InsertStatement node) =>
                Add("INSERT", node, node.InsertSpecification?.Target, null, null,
                    node.WithCtesAndXmlNamespaces);
```

그다음 `Add`의 시그니처에 매개변수를 더한다:

```csharp
            private void Add(
                string kind,
                TSqlStatement statement,
                TableReference? target,
                WhereClause? where,
                FromClause? from,
                WithCtesAndXmlNamespaces? ctes)
```

그리고 본문에서 수집기를 돌려 결과를 싣는다. **문장 전체를 순회하지 않는다** — 대상 행을 거를 수 있는 세 자리에서만 모은다:

```csharp
                var predicates = new ColumnCollector();
                var joins = new ColumnCollector();
                var grouping = new GroupingProbe();

                where?.Accept(predicates);
                from?.Accept(joins);
                statement.Accept(grouping);

                // 대상 행을 거를 수 있는 세 자리에서만 모은다. statement.Accept로
                // 문장 전체를 훑으면 SET 절 안의 하위질의까지 걸리는데, 그건 갱신할
                // "값"을 고르는 술어이지 갱신할 "행"을 고르는 술어가 아니다.
                var subordinate = new SubordinatePredicateCollector();
                ctes?.Accept(subordinate);
                from?.Accept(subordinate);
                where?.Accept(subordinate);

                Found.Add((
                    new StepSqlStatement(
                        kind,
                        ResolveTargetTable(target, from),
                        Anchor: null,
                        predicates.Columns.ToList(),
                        joins.Columns.ToList(),
                        grouping.Found,
                        HasOpaqueJoinSource: DetectOpaqueJoinSource(statement, from))
                    {
                        SubordinatePredicateColumns = subordinate.Columns.ToList(),
                    },
                    statement.StartOffset,
                    statement.StartOffset + statement.FragmentLength));
```

- [ ] **Step 6: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSqlStatementReaderTests"
```

Expected: 새 6개 포함 전부 PASS. 특히 기존 `CollectsTopLevelPredicateAndJoinColumns_ButNotSubqueryColumns`가 **여전히** 통과해야 한다 — `PredicateColumns`의 의미는 안 바뀐다.

`CollectsCtePredicatesIntoSubordinateColumns`가 실패하면 `WithCtesAndXmlNamespaces`가 `Visit(UpdateStatement)`에서 `null`일 수 있다. 그때는 ScriptDom에서 CTE 절이 어디에 달리는지 직접 확인하라 — **추측으로 캐스트를 넣지 말고** 디버그 출력으로 확인한 뒤 고쳐라.

- [ ] **Step 7: 뮤테이션으로 테스트가 실제로 잠그는지 확인한다**

각각 적용 → 돌리기 → 되돌리기. 결과를 보고서에 적는다.

1. `SubordinatePredicateCollector.Visit`이 아무것도 안 모으게(즉시 `return`) → CTE·파생·EXISTS 테스트 셋이 죽어야 한다
2. `ctes?.Accept(subordinate);` 한 줄 삭제 → CTE 테스트만 죽어야 한다
3. `statement.Accept(subordinate);`로 바꿈(세 진입점 대신 전체 순회) → `DoesNotCollectPredicatesFromSetClauseSubqueries`가 죽어야 한다

**3번이 이 태스크에서 가장 중요하다** — 설계가 진입점을 셋으로 좁힌 이유가 그 테스트다.

- [ ] **Step 8: 전체 빌드·테스트 후 커밋**

```bash
dotnet build && dotnet test
git add src/ReSet.Core/Services/StepSqlStatementReader.cs tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs
git commit
```

제목: `feat: 하위 스코프의 WHERE 술어 컬럼을 재료로 싣는다`

---

### Task 2: 검사 B가 소실과 이전을 구분한다

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`CheckAnchoredStatementFacts`의 `ReportMissing`)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `StepSqlStatement.SubordinatePredicateColumns`(태스크 1) · `FactsWithCode(int ordinal, IReadOnlyList<string> predicateColumns, string? code)`(테스트 파일의 기존 헬퍼) · `LegacyStep(string code)`(같은 파일의 기존 헬퍼)
- Produces: 없음(검사 동작 변경)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`의 `ValidateBatchStep_CheckB_UAnchorOnly_UsesUAnchor` 앞에 넣는다:

```csharp
        // ─────────────────────────────────────────────────────────────────────
        // 하위 스코프 이전 — 원본이 최상위에 두었던 술어를 이행이 CTE·파생
        // 테이블·EXISTS로 옮긴다. 그 컬럼은 없어진 것이 아니라 옮겨간 것이므로
        // 검사 B가 발화하면 거짓양성이다(2026-08-26 표본 판정 30건).
        //
        // 컬럼 단위로 거른다 - 전부-접기가 아니다. 하나는 이전이고 하나는 진짜
        // 소실이면 소실만 발화해야 한다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_CheckB_PredicateRelocatedIntoCte_IsSilent()
        {
            var facts = FactsWithCode(13, new[] { "YMD", "PGName" }, code: null);

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U13\n" +
                ";WITH CardCost AS (\n" +
                "    SELECT A.PLTID, A.ID FROM dbo.TSettleMst AS A\n" +
                "    WHERE A.YMD = @p AND A.PGName = 'PLCard'\n" +
                ")\n" +
                "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y\n" +
                "INNER JOIN CardCost AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("갱신 13"));
        }

        // 전부-접기 구현은 이 테스트에 죽는다.
        [Fact]
        public void ValidateBatchStep_CheckB_OneRelocatedOneMissing_ReportsOnlyTheMissing()
        {
            var facts = FactsWithCode(13, new[] { "YMD", "PGName" }, code: null);

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U13\n" +
                ";WITH CardCost AS (\n" +
                "    SELECT A.PLTID, A.ID FROM dbo.TSettleMst AS A\n" +
                "    WHERE A.YMD = @p\n" +
                ")\n" +
                "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y\n" +
                "INNER JOIN CardCost AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            var error = Assert.Single(result.Errors, e => e.Contains("갱신 13"));
            Assert.Contains("PGName", error);
            Assert.DoesNotContain("YMD", error);
        }

        [Fact]
        public void ValidateBatchStep_CheckB_NoSubordinateScope_StillReportsMissing()
        {
            var facts = FactsWithCode(13, new[] { "YMD" }, code: null);

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U13\n" +
                "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y WHERE Y.UseState = 0;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("갱신 13") && e.Contains("YMD"));
        }

```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~Relocated|FullyQualifiedName~NoSubordinateScope"
```

Expected: 앞의 두 개가 FAIL(검사 B가 아직 발화한다), 세 번째는 PASS.

- [ ] **Step 3: `ReportMissing`에 하위 스코프 필터를 더한다**

`src/ReSet.Core/Services/MechanicalValidator.cs`의 `CheckAnchoredStatementFacts` 안, `ReportMissing` 지역 함수 **바로 앞**에 이 줄을 넣는다:

```csharp
                // [하위 스코프 이전 - 소실과 구분한다]
                // 원본이 최상위 WHERE에 두었던 술어를 이행이 CTE·파생 테이블·
                // EXISTS로 옮기는 관용구가 실재한다(2026-08-26 표본 판정 30건 -
                // EXCEPTION_PROC UPDATE 2·17·18). 그 컬럼은 없어진 것이 아니라
                // 옮겨간 것이므로 요구로 들면 거짓양성이고, 그 요구는
                // SuggestedPromptFix를 타고 재생성 프롬프트에 실려 재시도를
                // 소진시킨다.
                //
                // group은 청크 분할 조각들을 묶은 것이므로 조각 어디의 하위 스코프에
                // 있어도 이전으로 본다 - 조각들이 논리적으로 한 문장이라는 기존
                // 전제와 같다.
                //
                // 이것이 의미 동등을 증명하지는 않는다(설계 §6). 동등성은 조인이
                // 대상 행 집합을 보존하느냐에 달렸고 그 전제는 로컬에서 검증할 수
                // 없다. 여기서 말하는 것은 "옮겨갔다"까지다.
                var relocated = new HashSet<string>(
                    group.SelectMany(a => a.Statement.SubordinatePredicateColumns),
                    StringComparer.OrdinalIgnoreCase);
```

그리고 `ReportMissing` 본문의 첫 줄을 바꾼다:

```csharp
                void ReportMissing(string label, IReadOnlyList<string> expected, HashSet<string> present)
                {
                    // 컬럼 단위로 거른다 - 전부-접기가 아니다. 하나는 이전이고 하나는
                    // 진짜 소실이면 소실만 발화해야 한다.
                    var missing = expected
                        .Where(c => !present.Contains(c) && !relocated.Contains(c))
                        .ToList();
                    if (missing.Count == 0) return;
```

**나머지 본문은 건드리지 않는다.** 조인 키 대조(`ReportMissing("조인 키", …)`)도 같은 필터를 지나가는데, 그건 의도된 것이다 — 조인 키가 하위 스코프에 있으면 그것도 이전이다.

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~MechanicalValidatorTests"
```

Expected: 전부 PASS. 기존 테스트가 하나라도 깨지면 **테스트를 고치지 말고 멈춰서 보고하라** — 이 변경이 닫혀 있던 자리를 여는 것일 수 있고, 그 판정은 코디네이터의 몫이다.

- [ ] **Step 5: 뮤테이션으로 확인한다**

각각 적용 → 돌리기 → 되돌리기. 결과를 보고서에 적는다.

1. `&& !relocated.Contains(c)`를 지운다 → 이전 테스트 둘이 죽어야 한다
2. 컬럼별 필터를 전부-접기로 바꾼다(`if (missing.Any(c => relocated.Contains(c))) return;`) → `OneRelocatedOneMissing_ReportsOnlyTheMissing`이 죽어야 한다
3. `relocated`를 항상 빈 집합으로 만든다 → 1번과 같은 둘이 죽어야 한다

**2번이 이 태스크에서 가장 중요하다** — 컬럼 단위 판정이 전부-접기와 다르다는 것을 그 테스트만 잠근다.

- [ ] **Step 6: 전체 빌드·테스트 후 커밋**

```bash
dotnet build && dotnet test
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit
```

제목: `fix: 검사 B가 하위 스코프로 이전한 술어를 소실로 보지 않는다`

---

### Task 3: 재측정과 기록 — S07 U13 재판정

**Files:**
- Create: `docs/audit-reports/sweeps/2026-08-26-step-sweep-c.md` (도구가 만든다)
- Modify: `docs/known-defects.md`
- Modify: `docs/audit-defect-catalog.md`

**Interfaces:**
- Consumes: 태스크 1·2의 결과 · `ReSet.Cli --sweep`

- [ ] **Step 1: 스윕을 돌린다**

`output/`이 이 워크트리에 없으면 심링크를 붙인다(커밋하지 마라 — 이미 `.git/info/exclude`에 있다):

```bash
ln -sfn /Users/payletter/git-root/ReSet/output output
dotnet run --project src/ReSet.Cli -- --sweep
```

기존 보고서 둘이 있으므로 `2026-08-26-step-sweep-c.md`가 만들어진다.

- [ ] **Step 2: 델타를 대조한다**

직전 측정(`2026-08-26-step-sweep-b.md`)은 이랬다:

```
| 검사 | (A) 오늘 | (B) 캐시 17 모사 |
| A | 20 | 20 |
| B |  1 | 68 |
| C |  0 | 18 |
| D | 18 | 18 |
| E | 59 | 59 |
```

**기대: 검사 B가 68에서 30건 이상 줄어든다**(세 관용구 30건 + S07 U13). 검사 A·C·D·E는 안 변해야 한다 — 이 변경은 검사 B의 술어 대조만 건드렸다.

- **예상보다 많이 줄면** 수집기가 너무 넓게 잡는다는 신호다. 멈추고 어느 부류가 사라졌는지 보고하라.
- **검사 C가 변하면** 범위를 넘은 것이다. 멈추고 보고하라.

사라진 부류를 뽑아 보고서에 적어라:

```bash
python3 - <<'PY'
import re
def rows(p):
    out=[];inb=False
    for l in open(p,encoding='utf-8'):
        if l.startswith('## 검사 B·C 발화 목록'): inb=True;continue
        if l.startswith('## 캐시 17'): break
        if inb and re.match(r'^\| \d',l):
            c=[x.strip() for x in l.strip().strip('|').split('|')]
            out.append((c[1],c[2],c[3],c[4],c[5],c[6]))
    return out
old=rows('docs/audit-reports/sweeps/2026-08-26-step-sweep-b.md')
new=rows('docs/audit-reports/sweeps/2026-08-26-step-sweep-c.md')
gone=[r for r in old if r not in new]
added=[r for r in new if r not in old]
import collections
print(f"{len(old)} → {len(new)} (사라짐 {len(gone)}, 새로 생김 {len(added)})")
for k,n in collections.Counter((r[0],r[4],r[5]) for r in gone).most_common():
    print(f"  [{n:2d}건] 검사 {k[0]} · {k[1]} · {k[2]}")
if added: print("새로 생김:", added)
PY
```

- [ ] **Step 3: 판정을 이관한다**

`-b` 보고서의 「판정」 칸에 채워진 값을 `-c`의 살아남은 행으로 옮긴다. 키는 `(검사, 조건, Job, 단계, 문장, 항목)`이다:

```bash
python3 - <<'PY'
import re
OLD='docs/audit-reports/sweeps/2026-08-26-step-sweep-b.md'
NEW='docs/audit-reports/sweeps/2026-08-26-step-sweep-c.md'
verdict={};inb=False
for l in open(OLD,encoding='utf-8'):
    if l.startswith('## 검사 B·C 발화 목록'): inb=True;continue
    if l.startswith('## 캐시 17'): break
    if inb and re.match(r'^\| \d',l):
        c=[x.strip() for x in l.strip().strip('|').split('|')]
        if len(c)>=8 and c[7]: verdict[(c[1],c[2],c[3],c[4],c[5],c[6])]=c[7]
lines=open(NEW,encoding='utf-8').read().split('\n')
n=0
for i,l in enumerate(lines):
    m=re.match(r'^\| (\d+) \| ([BC]) \| ([AB]) \| (\S+) \| (\S+) \| ([A-Z]+ \d+) \| (.*?) \|  \|$', l)
    if not m: continue
    _,chk,cond,job,step,stmt,items=m.groups()
    v=verdict.get((chk,cond,job,step,stmt,items))
    if v: lines[i]=l[:-2]+v+' |'; n+=1
open(NEW,'w',encoding='utf-8').write('\n'.join(lines))
rest=sum(1 for l in lines if re.match(r'^\| \d+ \|.*\|  \|$', l))
print(f"판정 이관 {n}건 · 미판정 {rest}건")
PY
```

- [ ] **Step 4: `known-defects.md`에 기록한다**

「캐시 17 인상 전 선결 조건」 절의 `(5-2-5)` **앞**에 `(5-2-6)`을 새로 넣는다. 기존 서술은 지우지 말고 더하기만 한다. 담을 것:

1. **무엇을 고쳤는가** — 하위 스코프 술어 재료, 진입점 셋, 컬럼 단위 판정.
2. **실측 델타** — 검사 B 68 → 실제 값. 사라진 부류 목록(스텝 2의 출력).
3. **기존 가드의 근거가 무너진 것** — `MechanicalValidator.cs:6397`의 주석이 "S07 U13의 실제 결함은 이쪽에서 잡힌다"를 근거로 술어 대조를 안 접었는데, S07 U13이 그 30건과 같은 관용구임을 실물로 확인했다(CTE가 필터를 갖고 바깥 UPDATE에 WHERE가 없으며 `PLTID+ID`로 조인, 원본 명세서도 최상위·파생 양쪽에 같은 필터).
4. **S07 U13 재판정** — 감사 🟠가 "검사 B로 닫힘"에서 "구조적 거짓양성이었다"로 바뀐다. **감사가 그 자리를 지목한 것 자체는 옳았다**는 점도 적어라 — 원본이 최상위에 두었던 필터가 이행의 최상위에 없는 것은 사실이고, 그것이 동등한지가 판정의 내용이다.
5. **이 변경이 주장하지 않는 것** — 의미 동등성. `(PLTID, ID)` 유일성은 로컬에서 검증 불가.
6. **검사 C는 별개 작업** — 이전은 최상위 밖으로 나가는 방향이라 초과를 만들 수 없다. 거울상은 반대 방향이고 명세서 쪽 「집합 술어」 표 스코프 라벨이 필요한데 `SpecStatementFacts`가 싣지 않는다. 다음 회차 항목.

- [ ] **Step 5: `audit-defect-catalog.md`의 11회차 행을 정정한다**

그 행이 지금 이렇게 적혀 있다:

> 🟠 7건 중 **2건**이 검사로 **닫힘**(S13 상태 변수 초기값 `0` — 검사 E / S07 갱신13 최상위 WHERE — 검사 B, …)

`S07 갱신13`을 닫힘에서 빼고 **구조적 거짓양성으로 재판정됐다**고 적되, **근거 절을 함께 달아라**(`docs/known-defects.md` (5-2-6)). 카탈로그의 갱신 규약 8이 "숫자를 다른 문서로 옮길 때는 근거 절을 함께 적어야 한다"를 요구한다.

닫힘 건수가 2에서 1로 준다. 그 사실을 감추지 말고 적어라 — **이 카탈로그의 목적은 기록이 아니라 예측**이고, 판정이 뒤집힌 이력 자체가 다음 사람에게 값이다.

- [ ] **Step 6: 빌드·테스트 후 커밋**

```bash
dotnet build && dotnet test
git status --short   # output 심링크가 잡히면 안 된다
git add docs/audit-reports/sweeps/2026-08-26-step-sweep-c.md docs/known-defects.md docs/audit-defect-catalog.md
git commit
```

제목: `docs: 하위 스코프 이전 재측정과 S07 U13 재판정`

---

## 이 계획이 닫지 않는 것

- **검사 C의 거울상.** 명세서의 「집합 술어」 표를 `SpecStatementFacts`에 올리는 별개 작업이다(설계 §5).
- **의미 동등성 판정.** `(PLTID, ID)` 유일성은 로컬에서 검증할 수 없다. 이전으로 판정된 것이 실제로 동등한지는 보고서 「판정」 칸의 사람 몫이다(설계 §6).
- **미판정 잔여의 개별 판정.** 재측정 후 남는 행들은 다음 작업이다.
