# INSERT 원천 술어 배선 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `StepSqlStatementReader`가 INSERT 원천 SELECT의 최상위 `WHERE`·`FROM`에서 술어·조인 컬럼을 실제로 뽑게 하고, 그 위에서 INSERT를 검사 B·C로 되돌린다.

**Architecture:** `DmlCollector.Add`의 절 인자를 단수에서 목록으로 바꿔 UPDATE·DELETE·INSERT가 한 벌의 수집 규칙을 공유하게 한다. INSERT는 `DmlScopeExtractor`에서 `internal static`으로 승격한 `SourceQuerySpecifications`로 원천 명세들을 꺼내 그 절들을 넘긴다. 그 다음 `MechanicalValidator`의 한시적 INSERT 배제를 걷고 코퍼스 전수 스윕으로 폭발 반경을 잰다.

**Tech Stack:** .NET 10 · C# · `Microsoft.SqlServer.TransactSql.ScriptDom` (`TSql160Parser`, `TSqlFragmentVisitor`) · xUnit

**Spec:** `docs/superpowers/specs/2026-08-26-insert-source-predicate-design.md`

## Global Constraints

- `output/` 쓰기 금지. 읽기만 한다. 스윕 보고서는 `docs/audit-reports/sweeps/`로 나간다.
- 이 저장소에는 `InternalsVisibleTo`가 없다(전수 확인). `internal` 멤버는 테스트에서 직접 못 부른다 — 공개 진입점(`StepSqlStatementReader.Read`, `MechanicalValidator.ValidateBatchStep`)을 통해서만 검증한다.
- 주석에서 다른 파일을 가리킬 때 **줄 번호가 아니라 멤버 이름**을 쓴다. 줄 번호 인용은 이 저장소에서 반복해 어긋났다(현재 `IsCandidateForAnchoredStatementCheck` 주석의 `StepSqlStatementReader.cs:464-465`는 실제 493-494다).
- 「귀속할 수 없으면 침묵한다」가 검증기의 규약이다. 새 검사 경로도 이를 따른다.
- 빌드는 경고 0으로 유지한다.
- 커밋 메시지는 한국어로 쓴다(저장소 관례).

## 파일 구조

| 파일 | 책임 | 태스크 |
|---|---|---|
| `src/ReSet.Core/Services/DmlScopeExtractor.cs` (수정) | `SourceQuerySpecifications`·`QuerySpecificationsOf`를 클래스 수준 `internal static`으로 승격 | 1 |
| `src/ReSet.Core/Services/StepSqlStatementReader.cs` (수정) | `Add`의 절 인자를 목록으로, INSERT 원천 배선 | 1 |
| `tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs` (수정) | 읽기 쪽 8개 테스트 | 1 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` (수정) | `(갱신 N)` 주석을 UPDATE에만 | 2 |
| `src/ReSet.Core/Services/StepSweepClassifier.cs` (수정) | 좌표 정규식에서 여는 괄호 요구를 뗀다 | 2 |
| `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` (수정) | 어법 테스트 · 재편입 후 검사 B·C 테스트 | 2·3 |
| `tests/ReSet.Core.Tests/StepSweepClassifierTests.cs` (수정) | 괄호 없는 좌표 추출 테스트 | 2 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` (수정) | `IsCandidateForAnchoredStatementCheck` 제거 | 3 |
| `docs/known-defects.md` (수정) | 배제 필터 주석이 담던 근거를 이관 | 3 |
| `docs/audit-reports/sweeps/2026-08-26-step-sweep-d.md` (생성) | 재편입 후 실측 | 4 |

**의존:** 태스크 1과 태스크 2는 파일이 겹치지 않아 병렬 가능하다(1은 `StepSqlStatementReader.cs`·`DmlScopeExtractor.cs`, 2는 `MechanicalValidator.cs`·`StepSweepClassifier.cs`). 태스크 3은 1과 2 둘 다 필요하다. 태스크 4는 3 뒤다.

---

### Task 1: 읽기 배선 — INSERT 원천 술어 수집

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs` — `DmlScopeVisitor` 안의 `SourceQuerySpecifications`·`QuerySpecificationsOf`를 `DmlScopeExtractor` 클래스 수준으로 옮기고 `internal static`으로
- Modify: `src/ReSet.Core/Services/StepSqlStatementReader.cs` — `DmlCollector.Add`, `Visit(UpdateStatement)`, `Visit(DeleteStatement)`, `Visit(InsertStatement)`, `DetectOpaqueJoinSource`
- Test: `tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs`

**Interfaces:**
- Consumes: 없음(첫 태스크)
- Produces:
  - `internal static IEnumerable<QuerySpecification> ReSet.Core.Services.DmlScopeExtractor.SourceQuerySpecifications(InsertSource? source)`
  - `internal static IEnumerable<QuerySpecification> ReSet.Core.Services.DmlScopeExtractor.QuerySpecificationsOf(QueryExpression? query)`
  - `StepSqlStatement` 레코드는 **모양이 바뀌지 않는다**. INSERT 문장의 `PredicateColumns`·`JoinColumns`·`SubordinatePredicateColumns`·`HasOpaqueJoinSource`가 이제 실제 값을 담는다는 점만 달라진다. 태스크 3이 이 값에 기댄다.

- [ ] **Step 1: 실패하는 테스트를 쓴다 — 원천 WHERE가 PredicateColumns로**

`tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs` 끝(마지막 `}` 직전)에 더한다.

```csharp
    // ─────────────────────────────────────────────────────────────────────
    // INSERT 원천 술어 배선(설계 2026-08-26-insert-source-predicate-design.md).
    //
    // InsertSpecification에는 WhereClause·FromClause 속성이 없다 - 술어는
    // InsertSource(→ SelectInsertSource.Select)의 QuerySpecification 안에 있다.
    // 예전에는 그 자리에 null을 넘겨 모든 INSERT의 PredicateColumns가 구조적으로
    // 항상 비었고, 그 빈 목록이 검사 B의 거짓양성 199건(코퍼스 스윕 269건 중 74%)을
    // 만들었다.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Insert_SourceWhere_FillsPredicateColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
            "SELECT S.YMD, SUM(S.TXAMT) FROM dbo.TSettleMst AS S\n" +
            "WHERE S.UseState = 0 AND S.YMD = @p\n" +
            "GROUP BY S.YMD;"));

        var statement = Assert.Single(statements);
        Assert.Equal("INSERT", statement.Kind);
        Assert.Contains("UseState", statement.PredicateColumns);
        Assert.Contains("YMD", statement.PredicateColumns);
    }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~Insert_SourceWhere_FillsPredicateColumns"`
Expected: FAIL — `PredicateColumns`가 비어 있어 `Assert.Contains("UseState", ...)`가 터진다.

- [ ] **Step 3: 헬퍼를 승격한다**

`src/ReSet.Core/Services/DmlScopeExtractor.cs`에서 `DmlScopeVisitor` 안의 두 `private static` 메서드를 잘라내, `public static class DmlScopeExtractor`의 클래스 수준(예: `DmlScopeVisitor` 선언 바로 앞)에 `internal static`으로 붙인다. 문서 주석은 그대로 옮긴다.

```csharp
        /// <summary>
        /// INSERT의 원천에서 QuerySpecification을 전부 끌어낸다. VALUES 원천이면
        /// 아무것도 내지 않는다 - 조건 없이 실리는 행이라 대조할 술어가 없다.
        ///
        /// [왜 internal인가] StepSqlStatementReader.DmlCollector가 같은 규칙을
        /// 써야 한다. 재구현하면 이 저장소가 이미 두 벌 들고 있는 중복이 세 벌이
        /// 되고, 그 중복이 정확히 INSERT 술어 결함을 만든 원인이다.
        /// </summary>
        internal static IEnumerable<QuerySpecification> SourceQuerySpecifications(InsertSource? source) =>
            source is SelectInsertSource select
                ? QuerySpecificationsOf(select.Select)
                : Enumerable.Empty<QuerySpecification>();

        /// <summary>
        /// QueryExpression 안의 QuerySpecification을 전부 낸다 - UNION(BinaryQueryExpression)과
        /// 괄호(QueryParenthesisExpression) 갈래를 모두 편다.
        /// </summary>
        internal static IEnumerable<QuerySpecification> QuerySpecificationsOf(QueryExpression? query)
        {
            switch (query)
            {
                case QuerySpecification spec:
                    yield return spec;
                    break;
                case BinaryQueryExpression binary:
                    foreach (var s in QuerySpecificationsOf(binary.FirstQueryExpression)) yield return s;
                    foreach (var s in QuerySpecificationsOf(binary.SecondQueryExpression)) yield return s;
                    break;
                case QueryParenthesisExpression paren:
                    foreach (var s in QuerySpecificationsOf(paren.QueryExpression)) yield return s;
                    break;
            }
        }
```

`DmlScopeVisitor`는 `DmlScopeExtractor`의 중첩 클래스이므로 기존 호출부(`ExplicitVisit(InsertSpecification)` 등)는 **한 글자도 고치지 않는다** — 이름 해석이 그대로 닿는다.

- [ ] **Step 4: `Add`의 절 인자를 목록으로 바꾼다**

`src/ReSet.Core/Services/StepSqlStatementReader.cs`의 `DmlCollector`에서 `Add`의 시그니처와 본문을 바꾼다. `where?.Accept(...)` 세 곳이 `foreach`로 바뀌고, `ResolveTargetTable`이 새 `targetAliasScope` 인자를 받는다. 기존 `// 대상 행을 거를 수 있는 네 자리...` 주석 블록은 **그대로 둔다**.

```csharp
            private void Add(
                string kind,
                TSqlStatement statement,
                TableReference? target,
                IReadOnlyList<WhereClause> wheres,
                IReadOnlyList<FromClause> froms,
                FromClause? targetAliasScope,
                WithCtesAndXmlNamespaces? ctes)
            {
                var predicates = new ColumnCollector();
                var joins = new ColumnCollector();
                var grouping = new GroupingProbe();

                foreach (var where in wheres) where.Accept(predicates);
                foreach (var from in froms) from.Accept(joins);
                statement.Accept(grouping);

                // (기존 "대상 행을 거를 수 있는 네 자리..." 주석 블록을 여기 그대로 둔다)
                var subordinate = new SubordinatePredicateCollector();
                ctes?.Accept(subordinate);
                foreach (var from in froms) from.Accept(subordinate);
                foreach (var where in wheres) where.Accept(subordinate);

                Found.Add((
                    new StepSqlStatement(
                        kind,
                        ResolveTargetTable(target, targetAliasScope),
                        Anchor: null,
                        predicates.Columns.ToList(),
                        joins.Columns.ToList(),
                        grouping.Found,
                        HasOpaqueJoinSource: DetectOpaqueJoinSource(statement, froms))
                    {
                        SubordinatePredicateColumns = subordinate.Columns.ToList(),
                    },
                    statement.StartOffset,
                    statement.StartOffset + statement.FragmentLength));
            }

            /// <summary>
            /// 절 하나를 목록으로 감싼다. UPDATE·DELETE는 절이 최대 하나이므로
            /// 이걸 쓰고, INSERT만 원천 명세 수만큼 여럿을 넘긴다.
            /// </summary>
            private static IReadOnlyList<T> One<T>(T? node) where T : class =>
                node is null ? Array.Empty<T>() : new[] { node };
```

`Found.Add`의 마지막 두 줄(`statement.StartOffset,` / `statement.StartOffset + statement.FragmentLength));`)은 현재 파일과 글자 그대로 같다 — 바꾸지 않는다.

- [ ] **Step 5: 세 호출부를 고친다**

같은 파일에서:

```csharp
            public override void Visit(UpdateStatement node) =>
                Add("UPDATE", node, node.UpdateSpecification?.Target,
                    One(node.UpdateSpecification?.WhereClause),
                    One(node.UpdateSpecification?.FromClause),
                    node.UpdateSpecification?.FromClause,
                    node.WithCtesAndXmlNamespaces);

            public override void Visit(DeleteStatement node) =>
                Add("DELETE", node, node.DeleteSpecification?.Target,
                    One(node.DeleteSpecification?.WhereClause),
                    One(node.DeleteSpecification?.FromClause),
                    node.DeleteSpecification?.FromClause,
                    node.WithCtesAndXmlNamespaces);

            /// <summary>
            /// INSERT의 술어는 InsertSpecification이 아니라 원천 SELECT에 있다.
            /// UNION 원천이면 QuerySpecification이 여럿이고, DmlScopeExtractor는
            /// 그것들을 같은 서수 하나로 합쳐 명세서 DML 범위 표에 적는다 -
            /// 그래서 읽기 쪽도 합친다.
            ///
            /// targetAliasScope가 null인 이유: INSERT 대상은 별칭일 수 없고
            /// (`INSERT INTO &lt;별칭&gt;`은 문법에 없다), 원천 SELECT의 FROM은
            /// 대상과 다른 이름 범위다. 거기에 `FROM dbo.TFoo AS TSettleMst`가
            /// 있으면 `INSERT INTO TSettleMst`의 대상이 TFoo로 잘못 풀린다.
            /// </summary>
            public override void Visit(InsertStatement node)
            {
                var specs = DmlScopeExtractor
                    .SourceQuerySpecifications(node.InsertSpecification?.InsertSource)
                    .ToList();

                Add("INSERT", node, node.InsertSpecification?.Target,
                    specs.Select(s => s.WhereClause).OfType<WhereClause>().ToList(),
                    specs.Select(s => s.FromClause).OfType<FromClause>().ToList(),
                    targetAliasScope: null,
                    node.WithCtesAndXmlNamespaces);
            }
```

- [ ] **Step 6: `DetectOpaqueJoinSource`를 목록으로 바꾼다**

같은 파일에서 시그니처와 마지막 두 줄만 바꾼다. `cteNames` 수집부는 그대로다.

```csharp
            private static bool DetectOpaqueJoinSource(TSqlStatement statement, IReadOnlyList<FromClause> froms)
            {
                if (froms.Count == 0) return false;

                var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (statement is StatementWithCtesAndXmlNamespaces withCtes &&
                    withCtes.WithCtesAndXmlNamespaces != null)
                {
                    foreach (var cte in withCtes.WithCtesAndXmlNamespaces.CommonTableExpressions)
                    {
                        if (!string.IsNullOrWhiteSpace(cte.ExpressionName?.Value))
                        {
                            cteNames.Add(cte.ExpressionName!.Value);
                        }
                    }
                }

                // UNION 원천의 한 갈래만 불투명해도 접는다 - 오탐보다 침묵이 안전한 방향이다.
                var probe = new OpaqueJoinSourceProbe(cteNames);
                foreach (var from in froms) from.Accept(probe);
                return probe.Found;
            }
```

- [ ] **Step 7: 테스트가 통과하는지 본다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~Insert_SourceWhere_FillsPredicateColumns"`
Expected: PASS

- [ ] **Step 8: 나머지 읽기 테스트 일곱 개를 더한다**

같은 파일, Step 1의 테스트 바로 아래에 붙인다.

```csharp
    [Fact]
    public void Insert_SourceJoin_FillsJoinColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
            "SELECT S.YMD, S.TXAMT FROM dbo.TSettleMst AS S\n" +
            "INNER JOIN dbo.TCost AS C ON C.PLTID = S.PLTID;"));

        var statement = Assert.Single(statements);
        Assert.Contains("PLTID", statement.JoinColumns);
    }

    [Fact]
    public void Insert_UnionSource_MergesBothBranches()
    {
        // DmlScopeExtractor는 UNION 갈래들을 같은 서수 하나로 합쳐 명세서에 적는다.
        // 읽기 쪽이 한 갈래만 보면 나머지 갈래의 술어가 "없어졌다"로 보인다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
            "SELECT S.YMD, S.TXAMT FROM dbo.TSettleMst AS S WHERE S.UseState = 0\n" +
            "UNION ALL\n" +
            "SELECT T.YMD, T.TXAMT FROM dbo.TSettleEtc AS T WHERE T.Cancelled = 1;"));

        var statement = Assert.Single(statements);
        Assert.Contains("UseState", statement.PredicateColumns);
        Assert.Contains("Cancelled", statement.PredicateColumns);
    }

    [Fact]
    public void Insert_ValuesSource_CollectsNothing()
    {
        // VALUES 원천은 조건 없이 실리는 행이라 대조할 술어가 없다.
        // SourceQuerySpecifications가 빈 열거를 내고, 그 결과 목록이 비어야 한다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO batch.BatchStepJournal (RunId, StepCode) VALUES (1, 'S07');"));

        var statement = Assert.Single(statements);
        Assert.Empty(statement.PredicateColumns);
        Assert.Empty(statement.JoinColumns);
        Assert.Empty(statement.SubordinatePredicateColumns);
        Assert.False(statement.HasOpaqueJoinSource);
    }

    [Fact]
    public void Insert_DerivedTableSource_GoesToSubordinate()
    {
        // UP_UTIL_SETTLE_INS의 INSERT 1이 이 모양이다 - 명세서는 최상위 술어 칸에
        // "(없음)"을 적고 실제 필터는 「집합 술어」표에 "파생 테이블 X"로 따로 적는다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO dbo.TSettleMst (YMD, PGNAME)\n" +
            "SELECT X.YMD, X.PGNAME FROM (\n" +
            "  SELECT A.YMD, A.PGNAME FROM dbo.TRaw AS A WHERE A.UseState = 0\n" +
            ") AS X;"));

        var statement = Assert.Single(statements);
        Assert.Contains("UseState", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("UseState", statement.PredicateColumns);
    }

    [Fact]
    public void Insert_TargetNotResolvedFromSourceAlias()
    {
        // INSERT 대상은 별칭일 수 없다. 원천 FROM의 별칭 사전을 대상 해석에 쓰면
        // 여기서 대상이 "TFoo"로 잘못 풀린다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO TSettleMst (YMD)\n" +
            "SELECT TSettleMst.YMD FROM dbo.TFoo AS TSettleMst WHERE TSettleMst.UseState = 0;"));

        var statement = Assert.Single(statements);
        Assert.Equal("TSettleMst", statement.TargetTable);
    }

    [Fact]
    public void Insert_OpaqueSourceJoin_SetsHasOpaqueJoinSource()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH CardCost AS (SELECT A.PLTID, A.Amt FROM dbo.TCost AS A WHERE A.YMD = @p)\n" +
            "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
            "SELECT S.YMD, C.Amt FROM dbo.TSettleMst AS S\n" +
            "INNER JOIN CardCost AS C ON C.PLTID = S.PLTID;"));

        var statement = Assert.Single(statements);
        Assert.True(statement.HasOpaqueJoinSource);
    }

    [Fact]
    public void Insert_CteBodyPredicate_GoesToSubordinate()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH CardCost AS (SELECT A.PLTID FROM dbo.TCost AS A WHERE A.YMD = @p)\n" +
            "INSERT INTO dbo.TSettleSum (PLTID)\n" +
            "SELECT C.PLTID FROM CardCost AS C;"));

        var statement = Assert.Single(statements);
        Assert.Contains("YMD", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("YMD", statement.PredicateColumns);
    }

    [Fact]
    public void Update_UnchangedAfterPluralClauseSignature()
    {
        // Add의 절 인자가 목록이 된 뒤에도 UPDATE 경로의 관측 동작은 그대로다.
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = 1\n" +
            "FROM dbo.TSettleMst AS Y INNER JOIN dbo.TCost AS C ON C.PLTID = Y.PLTID\n" +
            "WHERE Y.YMD = @p AND Y.UseState = 1;"));

        var statement = Assert.Single(statements);
        Assert.Equal("TSettleMst", statement.TargetTable);
        Assert.Contains("YMD", statement.PredicateColumns);
        Assert.Contains("UseState", statement.PredicateColumns);
        Assert.Contains("PLTID", statement.JoinColumns);
    }
```

- [ ] **Step 9: 전체 테스트를 돌린다**

Run: `dotnet test tests/ReSet.Core.Tests`
Expected: 전부 통과. 기존 테스트가 하나라도 깨지면 UPDATE·DELETE 경로에 회귀가 난 것이므로 멈추고 원인을 찾는다.

- [ ] **Step 10: 돌연변이 시험 — "아무 일도 안 일어남" 테스트가 정말 무는지**

`Insert_ValuesSource_CollectsNothing`과 `Insert_TargetNotResolvedFromSourceAlias`는 둘 다 부재를 주장하는 테스트라 조용히 통과할 수 있다. 각각 한 번씩 코드를 일부러 망가뜨려 빨간불을 확인하고 되돌린다.

1. `Visit(InsertStatement)`의 `targetAliasScope: null`을 `froms.FirstOrDefault()`가 아니라 임시로 `specs.Select(s => s.FromClause).OfType<FromClause>().FirstOrDefault()`로 바꾼다 → `Insert_TargetNotResolvedFromSourceAlias`가 FAIL(대상이 `TFoo`)해야 한다. 되돌린다.
2. `SourceQuerySpecifications`의 `VALUES` 갈래를 `Enumerable.Empty<QuerySpecification>()` 대신 예외를 던지게 임시로 바꾼다 → `Insert_ValuesSource_CollectsNothing`이 FAIL해야 한다. 되돌린다.

두 시험 모두 빨간불을 못 내면 그 테스트는 계약을 지키지 못하는 것이므로 단언을 고친다.

- [ ] **Step 11: 스윕 회귀 게이트 — 관측 변화가 0인지 확인한다**

배제 필터가 아직 INSERT를 막고 있으므로 검증기 발화는 **변하지 않아야 한다**.

```bash
dotnet run --project src/ReSet.Cli -- --sweep
```

새로 생긴 `docs/audit-reports/sweeps/2026-08-26-step-sweep-d.md`의 판정 표를 본다.

**주의 — `sweep-c`의 미분류 977과 대조하지 말 것.** `sweep-c`는 `fcf26a6`에서 생성됐고
그 뒤 병합된 `c09985c`가 검사 둘을 새로 넣어 미분류가 1138로 늘었다. 그 +161은 이번
변경과 무관하다. 검사 A~E만 대조하거나, 태스크 4 Step 2의 통제 스윕 방법을 쓴다.

검사 A~E 기대값:

```
| 검사 | (A) 오늘 | (B) 캐시 17 모사 |
| A | 20 | 20 |
| B |  0 | 31 |
| C |  0 | 18 |
| D | 18 | 18 |
| E | 59 | 59 |
```

다르면 UPDATE·DELETE 경로 회귀다 — 멈추고 원인을 찾는다. 같으면 이 확인용 보고서는 **커밋하지 않고 지운다**(태스크 4가 진짜 보고서를 만든다).

```bash
rm docs/audit-reports/sweeps/2026-08-26-step-sweep-d.md
```

- [ ] **Step 12: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs \
        src/ReSet.Core/Services/StepSqlStatementReader.cs \
        tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs
git commit -m "fix: INSERT 원천 SELECT의 최상위 WHERE·FROM에서 술어를 실제로 읽는다"
```

---

### Task 2: 메시지 어법 — `(갱신 N)` 주석을 UPDATE에만 붙인다

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` — 검사 B 메시지(현재 `$"{step.Code} 섹션의 {row.Kind} {row.Ordinal}(갱신 {row.Ordinal}) 문장에 명세서가 확정한 "`)와 검사 C 메시지(`... 문장이 명세서에 없는 "`)
- Modify: `src/ReSet.Core/Services/StepSweepClassifier.cs` — `CoordinatePattern`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`, `tests/ReSet.Core.Tests/StepSweepClassifierTests.cs`

**왜 지금 필요한가:** 두 메시지는 `Kind`와 무관하게 `(갱신 N)`을 붙인다. `갱신`은 명세서가 UPDATE 갱신 절 표에만 쓰는 말이고 INSERT·DELETE에는 그 표가 아예 없다(`SpecSetTarget` 문서 주석: 명세서 전체에서 `(삽입 N`·`(삭제 N`은 0건). DELETE는 이미 검사 B·C 후보라 오늘도 `DELETE 3(갱신 3)`을 낸다 — 태스크 3이 INSERT를 되돌리면 이 오기가 21행으로 번진다.

**Interfaces:**
- Consumes: 없음(태스크 1과 병렬 가능 — 파일이 겹치지 않는다)
- Produces: 검사 B·C 메시지가 UPDATE에서는 `S07 섹션의 UPDATE 13(갱신 13) 문장에...`, 그 밖에서는 `S07 섹션의 INSERT 2 문장에...`. 태스크 3의 테스트가 이 어법을 단언한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`에 더한다. 기존 헬퍼 `LegacyStep`을 쓰고, DELETE 행을 담은 새 재료 헬퍼를 함께 넣는다.

```csharp
        /// <summary>
        /// DELETE 행 하나짜리 명세서 재료. 기존 FactsWithCode는 Kind를 "UPDATE"로
        /// 못 박고 있어 어법 테스트에는 쓸 수 없다.
        /// </summary>
        private static IReadOnlyDictionary<string, SpecStatementFacts> FactsWithDeleteRow(
            int ordinal, IReadOnlyList<string> predicateColumns)
        {
            var facts = new SpecStatementFacts(
                new[]
                {
                    new SpecDmlRow("DELETE", ordinal, ordinal * 10, "TSettleMst",
                        predicateColumns, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
                },
                Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>());

            return new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = facts
            };
        }

        [Fact]
        public void ValidateBatchStep_CheckB_NonUpdateKind_OmitsUpdateGloss()
        {
            // "(갱신 N)"은 명세서의 UPDATE 갱신 절 표를 가리키는 말이다. DELETE·INSERT에는
            // 그 표가 없다(명세서 전체에서 `(삽입 N`·`(삭제 N`은 0건 - SpecSetTarget 문서).
            var facts = FactsWithDeleteRow(3, new[] { "YMD" });

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- DELETE 3\n" +
                "DELETE A FROM dbo.TSettleMst AS A;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            var error = Assert.Single(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
            Assert.Contains("DELETE 3 문장에", error);
            Assert.DoesNotContain("갱신 3", error);
        }

        [Fact]
        public void ValidateBatchStep_CheckB_UpdateKind_KeepsUpdateGloss()
        {
            var facts = FactsWithCode(13, new[] { "YMD" }, code: null);

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U13\n" +
                "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            var error = Assert.Single(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
            Assert.Contains("UPDATE 13(갱신 13) 문장에", error);
        }
```

앵커 표기는 `-- DELETE 3`이다. `AnchorPattern`은 `U4`·`갱신 4`·`UPDATE 4`·`INSERT 4`·`DELETE 4` 다섯 표기를 인정하고 `삭제 4`는 인정하지 않는다. 앵커가 안 붙으면 검사 B가 좌표를 못 잡아 침묵하고 테스트가 조용히 무효가 되므로, 이 표기를 바꾸지 않는다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CheckB_NonUpdateKind_OmitsUpdateGloss"`
Expected: FAIL — 메시지가 `DELETE 3(갱신 3) 문장에`라서 `Assert.DoesNotContain("갱신 3", error)`가 터진다.

- [ ] **Step 3: 두 메시지를 고친다**

`src/ReSet.Core/Services/MechanicalValidator.cs`. 검사 B(`ReportMissing`)와 검사 C 각각의 메시지 조립 직전에 주석 문자열을 만든다.

```csharp
            // "갱신 N"은 명세서의 UPDATE 갱신 절 표를 가리키는 말이다. INSERT·DELETE에는
            // 그 표가 없으므로(명세서 전체에서 `(삽입 N`·`(삭제 N`은 0건 - SpecSetTarget
            // 문서 주석) 붙이지 않는다.
            var gloss = row.Kind.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
                ? $"(갱신 {row.Ordinal})"
                : string.Empty;
```

검사 B 메시지의 첫 줄을 이렇게 바꾼다.

```csharp
                        $"{step.Code} 섹션의 {row.Kind} {row.Ordinal}{gloss} 문장에 명세서가 확정한 " +
```

검사 C 메시지의 첫 줄도 같은 모양으로 바꾼다.

```csharp
                    $"{step.Code} 섹션의 {row.Kind} {row.Ordinal}{gloss} 문장이 명세서에 없는 " +
```

나머지 줄(`명세서 DML 범위 표 {row.Kind} {row.Ordinal} 행의 값은 ...`)은 손대지 않는다.

- [ ] **Step 4: 스윕 좌표 정규식을 고친다**

`src/ReSet.Core/Services/StepSweepClassifier.cs`의 `CoordinatePattern`이 여는 괄호를 **필수로** 요구한다. 주석을 고치지 않으면 INSERT·DELETE 발화가 좌표를 잃는다(발화 수는 세지만 (종류, 서수)가 빈다).

```csharp
        // "S07 섹션의 UPDATE 13(갱신 13) 문장에" / "S07 섹션의 INSERT 2 문장에" 양쪽을 잡는다.
        // 여는 괄호는 UPDATE에만 붙으므로(MechanicalValidator의 gloss 참고) 경계로 쓸 수 없다 -
        // 서수 뒤의 공백이나 괄호 어느 쪽이든 받는다.
        private static readonly Regex CoordinatePattern = new(
            @"섹션의\s+(?<kind>[A-Z]+)\s+(?<ordinal>\d+)(?=\s|\()",
            RegexOptions.Compiled);
```

- [ ] **Step 5: 분류기 테스트를 더한다**

`tests/ReSet.Core.Tests/StepSweepClassifierTests.cs`에 더한다.

```csharp
    [Fact]
    public void Describe_NonUpdateKind_WithoutGloss_StillExtractsCoordinates()
    {
        var message =
            "S07 섹션의 INSERT 2 문장에 명세서가 확정한 최상위 WHERE 술어 컬럼 UseState이(가) " +
            "없습니다. 명세서 DML 범위 표 INSERT 2 행의 값은 `UseState`입니다 — " +
            "이 컬럼이 빠지면 갱신 대상 행 집합이 원본과 달라집니다.";

        var finding = StepSweepClassifier.Describe(
            "POQSettleBatch1", "S07", SweepCheck.B, SweepCondition.AsIs, message);

        Assert.Equal("INSERT", finding.Kind);
        Assert.Equal(2, finding.Ordinal);
    }
```

`SweepFinding.Kind`는 `string?`, `Ordinal`은 `int?`다(`StepSweepModels.cs:23-24`). `SweepCondition`의 값은 `AsIs`와 `SimulatedCache17` 둘뿐이다.

- [ ] **Step 6: 테스트가 통과하는지 본다**

Run: `dotnet test tests/ReSet.Core.Tests`
Expected: 전부 통과.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs \
        src/ReSet.Core/Services/StepSweepClassifier.cs \
        tests/ReSet.Core.Tests/MechanicalValidatorTests.cs \
        tests/ReSet.Core.Tests/StepSweepClassifierTests.cs
git commit -m "fix: 갱신 절 주석을 UPDATE 발화에만 붙이고 스윕 좌표 추출을 맞춘다"
```

---

### Task 3: INSERT를 검사 B·C로 되돌린다

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` — `IsCandidateForAnchoredStatementCheck` 제거, `ResolveAnchoredStatements`의 호출 제거
- Modify: `src/ReSet.Core/Services/StepSqlStatementReader.cs` — `Add` 안의 주석 어법만 (Step 0)
- Modify: `docs/known-defects.md` — 지워지는 주석의 근거를 이관
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: 태스크 1이 채우는 INSERT 문장의 `PredicateColumns`·`JoinColumns`, 태스크 2의 `gloss` 어법
- Produces: 검사 B·C가 INSERT 문장을 후보로 받는다. 태스크 4가 이 상태를 잰다.

**왜 서수가 흔들리지 않는가:** `ResolveOrdinal`은 위치가 아니라 신원으로 서수를 정한다 — `codeMap` 조회가 `Kind` 일치를 요구하고(`string.Equals(mapped.Kind, statement.Kind, ...)`), U-앵커는 문장에 직접 붙는다. 명세서 쪽 `IsComparableDmlRow`도 이미 INSERT를 통과시킨다. 따라서 후보 필터를 걷는 것이 UPDATE·DELETE 판정에 미치는 영향은 없다 — Step 1의 테스트가 이를 못 박는다.

- [ ] **Step 0: `Add`의 주석이 이제 INSERT도 서술하게 고친다**

태스크 1 리뷰의 Minor 지적이다. `src/ReSet.Core/Services/StepSqlStatementReader.cs`의
`Add` 안에 있는 "대상 행을 거를 수 있는 네 자리..." 주석은 UPDATE·DELETE 어법으로 쓰였다
("대상 행" = 이미 있는 행을 거른다). INSERT에는 거를 대상 행이 없다 — 어떤 **원천 행이
실릴지**를 고른다. 동작은 검증됐고(태스크 1의 `Insert_CteBodyPredicate_GoesToSubordinate`·
`Insert_DerivedTableSource_GoesToSubordinate`), 어법만 어긋난다.

주석의 기존 내용(네 자리 열거, JOIN ON 하위질의 실측, SET 절을 세지 않는 이유)은 **한 줄도
지우지 말고**, 첫 문장만 세 종류를 함께 서술하게 고친다. 예:

```
// 실릴·바뀔 행을 고를 수 있는 네 자리(WITH 본문·파생 테이블·JOIN ON 절 안의
// 하위질의·최상위 WHERE 안의 하위질의)에서만 모은다. UPDATE·DELETE에서는 "거를
// 대상 행"이고 INSERT에서는 "실릴 원천 행"이다 - 셋 다 같은 네 자리를 본다.
```

이 파일에서 이 주석 말고는 아무것도 건드리지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`에 더한다. 태스크 2에서 넣은 `FactsWithDeleteRow` 옆에 INSERT용 재료 헬퍼를 함께 둔다.

```csharp
        /// <summary>
        /// INSERT 행 하나짜리 명세서 재료.
        /// </summary>
        private static IReadOnlyDictionary<string, SpecStatementFacts> FactsWithInsertRow(
            int ordinal, IReadOnlyList<string> predicateColumns)
        {
            var facts = new SpecStatementFacts(
                new[]
                {
                    new SpecDmlRow("INSERT", ordinal, ordinal * 10, "TSettleSum",
                        predicateColumns, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
                },
                Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>());

            return new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = facts
            };
        }

        [Fact]
        public void ValidateBatchStep_CheckB_InsertMissingPredicate_Reports()
        {
            // 명세서가 INSERT 1의 최상위 술어로 UseState를 확정했는데 단계 SQL의
            // 원천 SELECT에 그 필터가 없다 - 실릴 행 집합이 원본과 달라진다.
            var facts = FactsWithInsertRow(1, new[] { "UseState" });

            var markdown = "### S07 단계\n\n```sql\n" +
                "/* U1: 정산 요약 적재 */\n" +
                "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
                "SELECT S.YMD, S.TXAMT FROM dbo.TSettleMst AS S;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleSum" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            var error = Assert.Single(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
            Assert.Contains("INSERT 1 문장에", error);
            var reported = error[..error.IndexOf("이(가) 없습니다", StringComparison.Ordinal)];
            Assert.Contains("UseState", reported);
        }

        [Fact]
        public void ValidateBatchStep_CheckB_InsertWithPredicate_Silent()
        {
            // 오탐 회귀 방지 - 이 침묵이 깨지면 코퍼스 스윕 199건(전체의 74%)의
            // 구조적 거짓양성이 되살아난 것이다.
            var facts = FactsWithInsertRow(1, new[] { "UseState" });

            var markdown = "### S07 단계\n\n```sql\n" +
                "/* U1: 정산 요약 적재 */\n" +
                "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
                "SELECT S.YMD, S.TXAMT FROM dbo.TSettleMst AS S WHERE S.UseState = 0;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleSum" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void ValidateBatchStep_CheckC_InsertExtraPredicate_Reports()
        {
            // 명세서가 INSERT 1의 최상위 술어를 UseState 하나로 확정했는데 단계가
            // YMD를 더 붙였다 - 실릴 행 집합이 원본보다 좁아진다.
            var facts = FactsWithInsertRow(1, new[] { "UseState" });

            var markdown = "### S07 단계\n\n```sql\n" +
                "/* U1: 정산 요약 적재 */\n" +
                "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
                "SELECT S.YMD, S.TXAMT FROM dbo.TSettleMst AS S\n" +
                "WHERE S.UseState = 0 AND S.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleSum" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            var error = Assert.Single(result.Errors, e => e.Contains("명세서에 없는"));
            Assert.Contains("INSERT 1 문장이", error);
            Assert.Contains("YMD", error);
        }

        [Fact]
        public void ValidateBatchStep_CheckB_InsertPresence_DoesNotShiftUpdateOrdinal()
        {
            // ResolveOrdinal은 위치가 아니라 신원(U-앵커·코드 앵커)으로 서수를 정한다.
            // 같은 단계에 INSERT가 섞여도 UPDATE 13의 판정은 그대로여야 한다.
            var facts = FactsWithCode(13, new[] { "YMD" }, code: null);

            var markdown = "### S07 단계\n\n```sql\n" +
                "INSERT INTO dbo.TSettleSum (YMD) SELECT S.YMD FROM dbo.TSettleMst AS S;\n" +
                "-- U13\n" +
                "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            var error = Assert.Single(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
            Assert.Contains("UPDATE 13(갱신 13) 문장에", error);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CheckB_InsertMissingPredicate_Reports"`
Expected: FAIL — INSERT가 후보에서 빠져 있어 발화가 0건이고 `Assert.Single`이 터진다.

- [ ] **Step 3: 지워질 주석의 근거를 `docs/known-defects.md`로 옮긴다**

코드에서 사라지는 것과 기록에서 사라지는 것은 다르다. `docs/known-defects.md`의 `(5-3-1)` 다음에 새 절을 더한다. 줄 번호가 아니라 멤버 이름으로 적는다.

```markdown
### (5-3-2) INSERT 술어 배선 결함과 그 한시적 좁힘 — 해소됨 (2026-08-26)

`StepSqlStatementReader`의 `DmlCollector.Visit(InsertStatement)`이 `Add`의 where·from
자리에 항상 `null`을 넘겼다. 같은 클래스의 `Visit(UpdateStatement)`·`Visit(DeleteStatement)`가
실제 절을 넘기는 것과 대조적이다. `InsertSpecification`에는 `WhereClause`·`FromClause`
속성이 없고 술어는 `InsertSource`(→ `SelectInsertSource.Select`)의 `QuerySpecification`
안에 있는데, 그 자리에 넘길 속성이 없어 `null`이 갔다.

그 결과 모든 INSERT 문장의 `PredicateColumns`·`JoinColumns`가 SQL 내용과 무관하게
구조적으로 항상 빈 목록이었고, 검사 B가 그 빈 목록을 "명세서가 확정한 술어 컬럼이
없다"로 오인했다. 코퍼스 전수 스윕(2026-08-25, 326개 단계)에서 코드 앵커를 켠 뒤
검사 B 발화가 1건 → 269건으로 늘었는데 그중 **199건(74%, 15개 조합)**이 이 축
하나의 구조적 거짓양성이었다. 실물: `output/Jobs/POQSettleBatch1/agent/steps/S04.md`의
`INSERT INTO ... SELECT ... WHERE USESTATE = 0`이 실제로는 술어를 담고 있는데도 오탐이 났다.

임시 대응으로 `MechanicalValidator`에 `IsCandidateForAnchoredStatementCheck`를 두어
INSERT를 검사 B·C 후보에서 뺐다. 검사 C도 함께 좁혔는데, 한쪽만 좁히면 두 검사가
서로 다른 후보 집합을 보게 되어 배선이 고쳐질 때 검사 C만 조용히 INSERT를 다시 보기
시작하는 비대칭이 생기기 때문이다.

2026-08-26에 배선을 고쳤다(`DmlCollector.Add`의 절 인자를 목록으로 바꾸고
`DmlScopeExtractor.SourceQuerySpecifications`로 원천 명세들의 절을 넘긴다). 좁힘은
걷어냈다. 설계는 `docs/superpowers/specs/2026-08-26-insert-source-predicate-design.md`,
재측정은 `docs/audit-reports/sweeps/2026-08-26-step-sweep-d.md`.
```

- [ ] **Step 4: 배제 필터를 지운다**

`src/ReSet.Core/Services/MechanicalValidator.cs`에서 `IsCandidateForAnchoredStatementCheck` 메서드와 그 문서 주석을 **통째로 지운다**. `=> true`로 바꿔 남기지 않는다 — 항상 참인 술어는 "왜 이게 있는가"를 다음 사람이 다시 풀게 만든다.

`ResolveAnchoredStatements`에서 호출도 함께 뗀다.

```csharp
            // 전
            var resolved = statements
                .Where(IsCandidateForAnchoredStatementCheck)
                .Select(s => (Statement: s, Ordinal: ResolveOrdinal(s, codeMap)))
                .Where(a => a.Ordinal.HasValue)
                .ToList();

            // 후
            var resolved = statements
                .Select(s => (Statement: s, Ordinal: ResolveOrdinal(s, codeMap)))
                .Where(a => a.Ordinal.HasValue)
                .ToList();
```

- [ ] **Step 5: 테스트가 통과하는지 본다**

Run: `dotnet test tests/ReSet.Core.Tests`
Expected: 전부 통과. 기존 검사 B·C 테스트가 깨지면 그 픽스처에 INSERT가 섞여 있었다는 뜻이므로, 픽스처를 고치지 말고 **왜 새 발화가 났는지 먼저 읽는다** — 진짜 결함일 수 있다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs \
        src/ReSet.Core/Services/StepSqlStatementReader.cs \
        tests/ReSet.Core.Tests/MechanicalValidatorTests.cs \
        docs/known-defects.md
git commit -m "fix: INSERT를 검사 B·C 후보로 되돌리고 한시적 좁힘의 근거를 기록으로 옮긴다"
```

---

### Task 4: 재측정과 판정

**Files:**
- Create: `docs/audit-reports/sweeps/2026-08-26-step-sweep-d.md` (스윕 도구가 생성)
- Modify: `docs/known-defects.md` (판정 결과에 따라)

**Interfaces:**
- Consumes: 태스크 3까지의 통합 상태 · `ReSet.Cli --sweep`
- Produces: 증가분에 대한 판정과 그 근거

- [ ] **Step 1: 스윕을 돌린다**

```bash
dotnet run --project src/ReSet.Cli -- --sweep
```

`docs/audit-reports/sweeps/2026-08-26-step-sweep-d.md`가 생긴다. `output/`은 읽기만 한다.

- [ ] **Step 2: 통제 스윕과 대조한다**

**`sweep-c`를 기준선으로 쓰지 말 것.** 그 보고서는 `fcf26a6`에서 생성됐는데, 그 뒤 병합된
`c09985c`가 검사 둘(`CheckControlStepErrorCodeBand`·`ValidateSplitProcedureObligations`)을
새로 넣었다. 그 발화는 분류기 표지 A~E에 없어 전부 미분류로 들어가므로, `sweep-c`의
미분류 977과 대조하면 **+161의 거짓 경보**가 난다(태스크 1 통합 때 실측으로 확인).

올바른 기준선은 **같은 베이스에서 내 변경만 뺀 통제 스윕**이다.

```bash
cd /Users/payletter/git-root/ReSet
git worktree add --detach .worktrees/control-<base> <ORIGINAL_BASE>
ln -s /Users/payletter/git-root/ReSet/output .worktrees/control-<base>/output
cd .worktrees/control-<base> && dotnet run --project src/ReSet.Cli -- --sweep
```

두 보고서를 전문 대조한다. 커밋 해시 줄(`- 커밋:`) 말고 다른 차이가 이번 변경의 결과다.

```bash
diff .worktrees/control-<base>/docs/audit-reports/sweeps/2026-08-26-step-sweep-d.md \
     .worktrees/insert-source-predicate/docs/audit-reports/sweeps/2026-08-26-step-sweep-d.md
```

참고 — `ORIGINAL_BASE`(8002668)의 통제값:

```
| 검사 | (A) 오늘 | (B) 캐시 17 모사 |
| A | 20 | 20 |
| B |  0 | 31 |
| C |  0 | 18 |
| D | 18 | 18 |
| E | 59 | 59 |
| 미분류 | 1138 | 1138 |
```

검사 A·D·E는 **변하지 않아야 한다** — 이번 변경이 닿지 않는 검사다. 변했으면 회귀이므로 멈춘다.
미분류도 변하지 않아야 한다 — INSERT 재편입은 검사 B·C 표지를 단 메시지를 늘릴 뿐이다.
끝나면 통제 워크트리를 지운다.

검사 B·C의 증가분이 이번 라운드의 결과물이다. 선결 지표(다중 레거시 SP 단계 수, 집합 어긋남, 펜스 파싱 실패, 코드 앵커 중복)도 함께 대조해 **모수가 줄어서 발화가 준 것이 아닌지** 확인한다.

- [ ] **Step 3: 증가분을 표본 판정한다**

증가분의 (Job, 단계, 종류, 서수)마다 실물 두 개를 연다.

- `output/Jobs/<Job>/agent/steps/<단계>.md` — 단계 SQL의 그 INSERT 문장
- `output/Procedures/<SP>/docs/Spec.md` — DML 범위 표의 그 INSERT 행, 그리고 「집합 술어」표에 같은 서수 행이 있는지

판정은 셋 중 하나다.

| 판정 | 근거 |
|---|---|
| 진짜 결함 | 명세서가 확정한 술어가 단계 SQL 어디에도 없다(최상위·하위 모두) |
| 구조적 오탐 | 술어가 하위 범위에 있는데 최상위로 기대됐다, 또는 명세서 행 자체가 단계와 대응하지 않는다 |
| 판정 불가 | 원본 DDL이나 데이터 없이는 못 가른다 — 그대로 적는다 |

**앞 회차의 실패를 반복하지 않는다:** 실물을 열지 않고 앞선 판정을 옮겨 적지 않는다. 인용할 때는 인용한 문서가 그 행을 실제로 확인했는지까지 확인한다(Task 22의 S07 U13은 CTE 본문을 생략한 채 인용해 세 회차가 잘못된 결론을 이어받았다).

- [ ] **Step 4: 판정에 따라 갈라진다**

- **진짜 결함이 있다** → 그대로 두고 `docs/known-defects.md`에 축 B 결함으로 싣는다. 각 항목에 Job·단계·서수·명세서 행·실물 인용을 담는다.
- **전부 구조적 오탐이다** → 태스크 3만 되돌린다(`git revert` 후 태스크 1·2는 남긴다). 원인을 `docs/known-defects.md`에 적고 다음 라운드로 넘긴다. 되돌리는 것은 실패가 아니라 설계된 분기다.
- **증가분이 0이다** → 그대로 둔다. 커버리지가 21행 늘고 발화가 없다는 것은 그 행들에 대해 산출물이 명세서와 맞다는 뜻이다. 그 사실을 보고서 요약에 적는다.

- [ ] **Step 5: 보고서와 판정을 커밋한다**

```bash
git add docs/audit-reports/sweeps/2026-08-26-step-sweep-d.md docs/known-defects.md
git commit -m "docs: INSERT 재편입 후 코퍼스 재측정과 증가분 판정"
```

---

## 하지 않는 것 (설계 §9)

- `MERGE` — `DmlCollector`가 아예 읽지 않는다. 별개 항목이다.
- `SubordinatePredicateColumns` 중복 제거 — 별개 항목이다.
- 검사 C의 명세서 쪽 거울(집합 술어 범위 라벨을 `SpecStatementFacts`로) — 별개 항목이다.
- 로드맵 3-b(코드 집합 대조 방어) — 별개 브레인스토밍으로 간다.
- `GroupingProbe`가 파생 테이블 안 `GROUP BY`까지 잡는 기존 성질 — UPDATE에서도 오늘 그러하다. 이번에 바꾸지 않는다.
- 캐시 16 → 17 승격과 명세서 전건 재생성 — 로드맵 4다.
