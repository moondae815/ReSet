# 잠금 힌트·ORDER BY·객체 선언 기계 확정 재료 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 감사가 남긴 🟡 다섯을 닫는다 — 잠금 힌트·`ORDER BY`·함수 `WITH` 옵션을 모델의 서술이 아니라 파서가 확정하는 재료로 올린다.

**Architecture:** 기존 네 기계 확정 표(`DML 범위`·`집합 술어`·`참조 함수`·`파생 테이블 정의`)와 같은 골격을 따른다. 추출기가 `record` 사실을 뽑고 → `AiService`가 마크다운 표로 조립해 프롬프트에 싣고 → `MechanicalValidator`(L1)가 산출물에 그 표가 실렸는지 검사한다. 표 둘은 새로 만들고(`잠금 힌트`, `객체 선언`), `ORDER BY`는 기존 「DML 범위」 표의 칸으로 붙인다.

**Tech Stack:** .NET 10 / C# · Microsoft.SqlServer.TransactSql.ScriptDom 180.37.3 · xUnit

**Spec:** `docs/superpowers/specs/2026-08-21-machine-facts-design.md`

## Global Constraints

- 표 헤딩 리터럴은 `### <이름> (기계 확정 — 수정 금지)` 형식이다. 대시는 em dash(`—`)이고 하이픈이 아니다.
- 헤딩 리터럴은 추출기 클래스의 `public const string`으로 두고, 조립기와 L1이 그 상수 하나를 공유한다. 문구를 고칠 때 한쪽만 바뀌는 일을 막는다.
- 표 셀에 넣는 텍스트는 `AiService.EscapeTableCell`을 거친다(개행 → 공백, `|` → `\|`). 추출기가 내는 값 자체에는 개행이 없어야 한다 — L1이 접지 않은 원문과 대조하므로 어긋나면 통과 불가능한 실패가 난다(2026-08-20 실측).
- ScriptDom에서 하위 스코프로 내려가는 것을 막을 때는 `Visit`이 아니라 `ExplicitVisit`을 비운다. `Visit`은 순회 중 알림일 뿐이라 비워도 자식으로 계속 내려간다(2026-08-20 실측).
- 주석은 한국어로 쓰고, *무엇을* 하는지가 아니라 *왜* 그렇게 했는지를 실측 근거와 함께 적는다.
- 커밋 메시지는 한국어, 마지막 줄에 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.
- 모든 작업 후 `dotnet test --nologo -v q`가 통과해야 한다. 기준선은 2049건이다.

---

## File Structure

| 파일 | 책임 | 변경 |
|---|---|---|
| `src/ReSet.Core/Services/DmlScopeExtractor.cs` | DML 문장 단위 사실 추출. 잠금 힌트 추출기와 `LockHintFact`를 여기 둔다 — 기존 세 추출기와 같은 방문자 골격을 공유한다 | 수정 |
| `src/ReSet.Core/Services/ObjectDeclarationExtractor.cs` | `CREATE FUNCTION`의 `WITH` 옵션 추출. DML과 무관한 객체 선언부라 별도 파일로 둔다 | **신규** |
| `src/ReSet.Core/Services/AiService.cs` | 표 조립과 프롬프트 배선 | 수정 |
| `src/ReSet.Core/Services/SpecExpectations.cs` | L1이 대조할 기대값 수집 | 수정 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | L1 검사 | 수정 |
| `src/ReSet.Core/Services/CacheManager.cs` | 캐시 형식 7 → 8 | 수정 |
| `.claude/skills/reset-consistency-audit/references/axis-a.md` | 감사 대조 계약 | 수정 |
| `docs/architecture.md` | 캐시 버전 표기 | 수정 |

테스트는 기존 파일에 붙인다: `DmlScopeExtractorTests.cs` · `AiServiceTests_Rich.cs` · `MechanicalValidatorTests.cs` · `CacheManagerTests.cs`. `ObjectDeclarationExtractorTests.cs`만 신규다.

---

### Task 1: 잠금 힌트 추출기

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs`
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`

**Interfaces:**
- Consumes: 없음 (첫 작업)
- Produces:
  - `public sealed record LockHintFact(string Operation, int StatementOrdinal, int Line, string Table, string Alias, IReadOnlyList<string> Hints)`
  - `public const string LockHintTableHeading = "### 잠금 힌트 (기계 확정 — 수정 금지)"`
  - `public static IReadOnlyList<LockHintFact> ExtractLockHints(string? ddlText)`

`Operation`은 `"INSERT"`·`"UPDATE"`·`"DELETE"` 중 하나. `StatementOrdinal`은 그 연산 종류 안에서의 1부터 시작하는 순번(기존 `SetPredicateFact`와 같은 규칙). `Alias`는 별칭이 없으면 `"-"`. `Hints`는 힌트가 없으면 빈 목록.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs` 맨 끝 클래스 닫는 괄호 앞에 넣는다.

```csharp
        [Fact]
        public void ExtractLockHints_FromClauseReferences_AreListedWithTheirHints()
        {
            // INS_EXTRA4PLCARD 실측 형태. 같은 TPGProperty가 별칭마다 힌트가 갈린다 -
            // 산문은 "5개 테이블의 조회 또는 조인에 사용됩니다"로 뭉갰고 그것이 🟡이었다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = 1
    FROM dbo.TSettleMst A WITH(NOLOCK)
    JOIN dbo.TPGProperty PG ON A.PGName = PG.PGName
    JOIN dbo.TPGProperty Y  WITH(NOLOCK) ON A.PGName = Y.PGName
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.Equal(3, facts.Count);
            Assert.Equal(new[] { "NOLOCK" }, Assert.Single(facts, f => f.Alias == "A").Hints);
            Assert.Empty(Assert.Single(facts, f => f.Alias == "PG").Hints);
            Assert.Equal(new[] { "NOLOCK" }, Assert.Single(facts, f => f.Alias == "Y").Hints);
        }

        [Fact]
        public void ExtractLockHints_TargetNodeWithoutFromClause_IsTheScan()
        {
            // 설계 초안은 "대상 노드를 싣지 않는다"였는데 프로브가 그 규칙이 사실을
            // 잃는 것을 보여 줬다. FROM 절이 없으면 대상 노드가 곧 스캔이고 힌트를 진다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DELETE FROM dbo.TSettleByOUT WITH(NOLOCK) WHERE OutYMD = '20260101'
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractLockHints(ddl));

            Assert.Equal("DELETE", fact.Operation);
            Assert.Equal("dbo.TSettleByOUT", fact.Table);
            Assert.Equal("-", fact.Alias);
            Assert.Equal(new[] { "NOLOCK" }, fact.Hints);
        }

        [Fact]
        public void ExtractLockHints_TargetNodeWithFromClause_IsNotDoubleCounted()
        {
            // UPDATE T ... FROM T A 에서 대상 T와 FROM의 A는 다른 노드이고 대상 쪽엔
            // 힌트가 없다. 둘 다 실으면 같은 테이블이 "힌트 있음/없음" 두 행으로 나와
            // 독자를 오도한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE TSettleMst SET C = 1 FROM dbo.TSettleMst A WITH(NOLOCK) WHERE A.X = 1
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractLockHints(ddl));

            Assert.Equal("A", fact.Alias);
            Assert.Equal(new[] { "NOLOCK" }, fact.Hints);
        }

        [Fact]
        public void ExtractLockHints_DerivedTableInterior_IsNotCollected()
        {
            // 파생 테이블 안의 참조는 그 스코프의 것이고 바깥 문장의 잠금 동작과 별개다.
            // ScriptDom은 Visit을 비워도 자식으로 내려가므로 ExplicitVisit을 비워야 한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = 1
    FROM (SELECT PLTID FROM dbo.THidden B WITH(NOLOCK)) X
        ,dbo.TSettleMst A WITH(NOLOCK)
    WHERE X.PLTID = A.PLTID
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.Single(facts);
            Assert.Equal("A", facts[0].Alias);
            Assert.DoesNotContain(facts, f => f.Table.Contains("THidden"));
        }

        [Fact]
        public void ExtractLockHints_StatementWithNoScan_ProducesNoRow()
        {
            // FROM도 없고 대상에 힌트도 없으면 스캔할 자리가 없다. 빈 행으로 채우지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.TSettleMst SET C = 1 WHERE X = 1
END";

            Assert.Empty(DmlScopeExtractor.ExtractLockHints(ddl));
        }

        [Fact]
        public void ExtractLockHints_MultipleHints_AreAllListed()
        {
            // 한 참조에 힌트가 여럿 붙을 수 있다. 칸은 불리언이 아니라 목록이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = 1 FROM dbo.TSettleMst A WITH(NOLOCK, READUNCOMMITTED) WHERE A.X = 1
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractLockHints(ddl));

            Assert.Equal(new[] { "NOLOCK", "READUNCOMMITTED" }, fact.Hints);
        }

        [Fact]
        public void ExtractLockHints_InsertSourceFromClause_IsCollected()
        {
            // INSERT는 원천 SELECT의 FROM이 스캔 자리다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TStat (A) SELECT X FROM dbo.TSource S WITH(NOLOCK)
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractLockHints(ddl));

            Assert.Equal("INSERT", fact.Operation);
            Assert.Equal("S", fact.Alias);
            Assert.Equal(new[] { "NOLOCK" }, fact.Hints);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --nologo --filter "FullyQualifiedName~ExtractLockHints"`
Expected: 7건 전부 FAIL — `ExtractLockHints`가 없어 컴파일 에러가 난다.

- [ ] **Step 3: 최소 구현을 쓴다**

`DmlScopeExtractor.cs`의 `ReferencedFunctionCallFact` 정의 뒤에 record와 헤딩 상수를 넣는다.

```csharp
    /// <summary>
    /// "이 문장이 어느 자리를 어떤 잠금 힌트로 읽는가"를 담는다.
    ///
    /// [행 단위가 (문장 × 스캔 자리)인 이유 - 2026-08-21 축 A 감사]
    /// 감사가 지적한 것은 "문장별로 힌트가 붙은 곳과 안 붙은 곳이 갈린다"였다.
    /// INS_EXTRA4PLCARD에서 TPGProperty가 P·Y 별칭에는 붙고 PG에는 안 붙는데,
    /// 명세서는 "5개 테이블의 조회 또는 조인에 사용됩니다"로 뭉갰다. 문장당 한 칸으로는
    /// 이 결함을 담을 수 없다.
    /// </summary>
    /// <param name="Alias">별칭이 없으면 "-".</param>
    /// <param name="Hints">힌트가 없으면 빈 목록. 한 참조에 여럿 붙을 수 있다.</param>
    public sealed record LockHintFact(
        string Operation,
        int StatementOrdinal,
        int Line,
        string Table,
        string Alias,
        IReadOnlyList<string> Hints);
```

헤딩 상수는 기존 셋 옆에 붙인다(`DmlScopeExtractor.cs:153-155` 부근).

```csharp
        public const string LockHintTableHeading = "### 잠금 힌트 (기계 확정 — 수정 금지)";
```

추출기 본체는 `ExtractFunctionCalls` 뒤에 둔다.

```csharp
        /// <summary>
        /// DML 문장이 읽는 자리와 그 잠금 힌트를 뽑는다.
        ///
        /// [행이 되는 자리가 셋인 이유 - 2026-08-21 프로브 실측]
        /// 처음에는 "대상 노드를 싣지 않는다"로 정했다가 규칙이 사실을 잃는 것을 봤다.
        ///   DELETE T FROM dbo.T A WITH(NOLOCK)  대상 (없음) · FROM NoLock  ← 대상은 껍데기
        ///   DELETE FROM dbo.T WITH(NOLOCK)      대상 NoLock · FROM 없음    ← 대상이 곧 스캔
        /// FROM이 있으면 대상 노드는 갱신 대상 지시자일 뿐 스캔이 아니고 힌트를 지지 않는다.
        /// 그대로 실으면 같은 테이블이 "힌트 있음/없음" 두 행으로 나와 독자를 오도한다.
        /// </summary>
        public static IReadOnlyList<LockHintFact> ExtractLockHints(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<LockHintFact>();

            var parser = new TSql160Parser(true);
            using var reader = new StringReader(ddlText);
            var fragment = parser.Parse(reader, out var errors);
            if (fragment == null || (errors != null && errors.Count > 0))
            {
                return Array.Empty<LockHintFact>();
            }

            var visitor = new LockHintVisitor();
            fragment.Accept(visitor);
            return visitor.Facts;
        }

        private sealed class LockHintVisitor : TSqlFragmentVisitor
        {
            public List<LockHintFact> Facts { get; } = new();

            private readonly Dictionary<string, int> _ordinals = new(StringComparer.Ordinal);

            public override void Visit(InsertSpecification node)
            {
                var from = (node.InsertSource as SelectInsertSource)?.Select is QuerySpecification qs
                    ? qs.FromClause
                    : null;
                Record("INSERT", node, node.Target, from);
            }

            public override void Visit(UpdateSpecification node) =>
                Record("UPDATE", node, node.Target, node.FromClause);

            public override void Visit(DeleteSpecification node) =>
                Record("DELETE", node, node.Target, node.FromClause);

            private void Record(
                string operation, TSqlFragment statement, TableReference target, FromClause? from)
            {
                _ordinals.TryGetValue(operation, out var n);
                _ordinals[operation] = ++n;
                var line = statement.StartLine;

                if (from != null)
                {
                    var collector = new FromTableCollector();
                    foreach (var reference in from.TableReferences) reference.Accept(collector);
                    foreach (var table in collector.Tables) Add(operation, n, line, table);
                }

                // 대상 노드는 FROM이 없을 때(그 자체가 스캔) 또는 힌트를 질 때
                // (INSERT INTO T WITH(TABLOCK)) 싣는다.
                if (target is NamedTableReference named &&
                    (from == null || named.TableHints.Count > 0))
                {
                    Add(operation, n, line, named);
                }
            }

            private void Add(string operation, int ordinal, int line, NamedTableReference node)
            {
                var table = string.Join(
                    ".", node.SchemaObject.Identifiers.Select(i => i.Value));
                var alias = string.IsNullOrEmpty(node.Alias?.Value) ? "-" : node.Alias!.Value;
                var hints = node.TableHints
                    .Select(h => h.HintKind.ToString().ToUpperInvariant())
                    .ToList();

                if (Facts.Any(f =>
                        f.Operation == operation && f.StatementOrdinal == ordinal &&
                        f.Table == table && f.Alias == alias))
                {
                    return;
                }

                Facts.Add(new LockHintFact(operation, ordinal, line, table, alias, hints));
            }

            /// <summary>
            /// FROM 절의 명명 테이블 참조를 모은다. 파생 테이블 안으로는 내려가지 않는다 -
            /// 그 스코프의 참조는 바깥 문장의 잠금 동작과 별개다. ScriptDom은 Visit을
            /// 비워도 자식으로 계속 내려가므로 ExplicitVisit을 비운다.
            /// </summary>
            private sealed class FromTableCollector : TSqlFragmentVisitor
            {
                public List<NamedTableReference> Tables { get; } = new();

                public override void Visit(NamedTableReference node) => Tables.Add(node);

                public override void ExplicitVisit(QueryDerivedTable node) { }
            }
        }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --nologo --filter "FullyQualifiedName~ExtractLockHints"`
Expected: 7건 PASS.

- [ ] **Step 5: 전체 회귀를 돌린다**

Run: `dotnet test --nologo -v q`
Expected: 2056건 통과(기준선 2049 + 신규 7), 실패 0.

- [ ] **Step 6: 실물로 확인한다**

`INS_EXTRA4PLCARD`의 실제 DDL에서 감사가 지적한 자리가 잡히는지 본다. `/private/tmp/.../scratchpad/`에 임시 콘솔 프로젝트를 만들어 `ReSet.Core`를 참조하고 `ExtractLockHints`를 호출한다. 기대: `TSettleMst`가 사전확인·UPDATE1·UPDATE2에서 `NOLOCK`을 지고, `TPGProperty`의 `PG` 별칭 4곳은 `(없음)`이며, `P`·`Y`는 `NOLOCK`을 진다.

기대와 다르면 단위 테스트가 픽스처의 우연으로 통과한 것이므로 Step 1로 돌아간다 — 2026-08-20·08-21에 두 번 그런 일이 있었다.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs
git commit -F- <<'EOF'
feat: 잠금 힌트를 (문장 × 스캔 자리) 단위로 뽑는다

감사가 지적한 것은 "문장별로 힌트가 붙은 곳과 안 붙은 곳이 갈린다"였다.
INS_EXTRA4PLCARD에서 TPGProperty가 P·Y 별칭에는 붙고 PG에는 안 붙는데
명세서는 "5개 테이블의 조회 또는 조인에 사용됩니다"로 뭉갰다.

행이 되는 자리를 셋으로 나눴다. 프로브가 "대상 노드를 싣지 않는다"는 초안이
DELETE FROM dbo.T WITH(NOLOCK)처럼 대상 자신이 힌트를 지는 형태를 잃는 것을
보여 줬다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

### Task 2: `ORDER BY`를 DML 범위 사실에 더한다

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs`
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`

**Interfaces:**
- Consumes: Task 1의 파일 변경(충돌 없음 — 다른 record를 건드린다)
- Produces: `DmlScopeFact`에 `IReadOnlyList<string> OrderByColumns` 추가. 기존 위치 인자 뒤에 붙이고 기본값 `null` 대신 빈 목록을 쓴다.

```csharp
public sealed record DmlScopeFact(
    string Operation,
    int Line,
    string Target,
    IReadOnlyList<string> PredicateColumns,
    bool DateParameterApplied,
    IReadOnlyList<string> JoinKeys,
    IReadOnlyList<string> OrderByColumns);
```

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        [Fact]
        public void ExtractDmlScope_InsertSelectWithOrderBy_CarriesTheColumns()
        {
            // STAT_PGCOLLECT_INS:113 실측. ORDER BY가 문서 어디에도 없어 🟡이었다.
            // 존재 여부가 아니라 컬럼 목록을 싣는다 - 더 충실하고 비용이 같다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TStat (A, B)
    SELECT INYMD, CLIENTID FROM dbo.TSource
    GROUP BY INYMD, CLIENTID
    ORDER BY INYMD, CLIENTID
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractDmlScope(ddl, null));

            Assert.Equal(new[] { "INYMD", "CLIENTID" }, fact.OrderByColumns);
        }

        [Fact]
        public void ExtractDmlScope_InsertWithoutOrderBy_HasEmptyList()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TStat (A) SELECT X FROM dbo.TSource
END";

            Assert.Empty(Assert.Single(DmlScopeExtractor.ExtractDmlScope(ddl, null)).OrderByColumns);
        }

        [Fact]
        public void ExtractDmlScope_UpdateAndDelete_HaveEmptyOrderBy()
        {
            // UPDATE·DELETE는 최상위 ORDER BY가 문법상 불가하다. 표에서는 "—"로 렌더된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T SET C = 1 WHERE X = 1
    DELETE FROM dbo.T WHERE X = 2
END";

            var facts = DmlScopeExtractor.ExtractDmlScope(ddl, null);

            Assert.All(facts, f => Assert.Empty(f.OrderByColumns));
        }
```

**주의**: `ExtractDmlScope`의 정확한 시그니처를 먼저 확인한다.

Run: `grep -n "public static IReadOnlyList<DmlScopeFact> ExtractDmlScope" src/ReSet.Core/Services/DmlScopeExtractor.cs`

두 번째 인자가 기준일 파라미터 이름이다. 위 테스트의 `null`을 실제 시그니처에 맞춘다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --nologo --filter "FullyQualifiedName~ExtractDmlScope_Insert|FullyQualifiedName~ExtractDmlScope_UpdateAndDelete"`
Expected: FAIL — `OrderByColumns`가 없어 컴파일 에러.

- [ ] **Step 3: 최소 구현을 쓴다**

`DmlScopeFact`에 필드를 더하고, `record`에 XML 주석을 붙인다.

```csharp
    /// <param name="OrderByColumns">
    /// INSERT ... SELECT 의 최상위 ORDER BY 컬럼. UPDATE·DELETE는 최상위 ORDER BY가
    /// 문법상 불가하므로 항상 빈 목록이고 표에서 "—"로 렌더된다.
    ///
    /// [존재 여부가 아니라 목록인 이유 - 2026-08-21 축 A 감사]
    /// STAT_PGCOLLECT_INS:113의 `ORDER BY INYMD, CLIENTID, PGNAME, MALLID`가 문서
    /// 어디에도 없었다. 불리언으로 담으면 "있다"만 알고 무엇으로 정렬하는지는 여전히
    /// 모른다. 컬럼 목록을 담는 비용이 같으므로 더 충실한 쪽을 택한다.
    /// </param>
```

DML 범위 방문자에서 `InsertSpecification`을 처리하는 자리에 다음을 더한다.

```csharp
                var orderBy = (node.InsertSource as SelectInsertSource)?.Select
                    is QuerySpecification qs && qs.OrderByClause != null
                        ? qs.OrderByClause.OrderByElements
                            .Select(e => TextOf(e.Expression))
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .ToList()
                        : new List<string>();
```

`UPDATE`·`DELETE` 경로에는 `new List<string>()`을 넘긴다. 기존 `new DmlScopeFact(...)` 호출부 전부에 인자를 추가한다 — 컴파일러가 빠뜨린 곳을 잡아 준다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --nologo --filter "FullyQualifiedName~ExtractDmlScope"`
Expected: 전부 PASS.

- [ ] **Step 5: 전체 회귀**

Run: `dotnet test --nologo -v q`
Expected: 2059건 통과(2056 + 신규 3), 실패 0.

기존 `DmlScopeFact` 생성 테스트가 인자 개수 때문에 깨질 수 있다. 깨지면 그 테스트에 빈 목록 인자를 더한다 — 테스트가 잘못된 게 아니라 시그니처가 바뀐 것이다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs
git commit -F- <<'EOF'
feat: DML 범위 사실이 INSERT의 ORDER BY 컬럼을 담는다

STAT_PGCOLLECT_INS:113의 ORDER BY INYMD, CLIENTID, PGNAME, MALLID가 문서
어디에도 없어 🟡이었다. 행 집합이 이미 DML 문장 단위이므로 표를 새로 만들지
않고 기존 사실에 필드를 더한다.

존재 여부가 아니라 컬럼 목록을 담는다 - 비용이 같고 더 충실하다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

### Task 3: 객체 선언 추출기

**Files:**
- Create: `src/ReSet.Core/Services/ObjectDeclarationExtractor.cs`
- Test: `tests/ReSet.Core.Tests/ObjectDeclarationExtractorTests.cs` (신규)

**Interfaces:**
- Consumes: 없음
- Produces:
  - `public sealed record ObjectDeclarationFact(string QualifiedName, IReadOnlyList<string> WithOptions)`
  - `public const string ObjectDeclarationTableHeading = "### 객체 선언 (기계 확정 — 수정 금지)"`
  - `public static ObjectDeclarationFact? Extract(string? ddlText)` — 함수가 아니면 `null`.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

새 파일 `tests/ReSet.Core.Tests/ObjectDeclarationExtractorTests.cs`:

```csharp
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ObjectDeclarationExtractorTests
    {
        [Fact]
        public void Extract_FunctionWithoutOptions_ReportsEmptyList()
        {
            // UF_GET_OUTYMD4REFUND:16-18 실측. WITH 절이 없다는 것이 원문에서 확정되는데
            // 명세서가 "확인할 수 없음"으로 적어 🟡이었다. 빈 목록이 곧 "스키마 바인딩 아님"이다.
            const string ddl =
                "CREATE FUNCTION dbo.UF_GET_OUTYMD4REFUND(@a VARCHAR(8)) " +
                "RETURNS VARCHAR(8) AS BEGIN RETURN '' END";

            var fact = ObjectDeclarationExtractor.Extract(ddl);

            Assert.NotNull(fact);
            Assert.Equal("dbo.UF_GET_OUTYMD4REFUND", fact!.QualifiedName);
            Assert.Empty(fact.WithOptions);
        }

        [Fact]
        public void Extract_FunctionWithSchemaBinding_ReportsIt()
        {
            const string ddl =
                "CREATE FUNCTION dbo.F(@a INT) RETURNS INT WITH SCHEMABINDING " +
                "AS BEGIN RETURN 1 END";

            Assert.Equal(
                new[] { "SCHEMABINDING" },
                ObjectDeclarationExtractor.Extract(ddl)!.WithOptions);
        }

        [Fact]
        public void Extract_FunctionWithSeveralOptions_ListsAll()
        {
            const string ddl =
                "CREATE FUNCTION dbo.F(@a INT) RETURNS INT " +
                "WITH SCHEMABINDING, RETURNS NULL ON NULL INPUT AS BEGIN RETURN 1 END";

            var options = ObjectDeclarationExtractor.Extract(ddl)!.WithOptions;

            Assert.Contains("SCHEMABINDING", options);
            Assert.Equal(2, options.Count);
        }

        [Fact]
        public void Extract_InlineTableValuedFunction_IsCovered()
        {
            // 인라인 TVF도 WITH 옵션을 질 수 있다. 스칼라와 같게 다룬다.
            const string ddl =
                "CREATE FUNCTION dbo.UIF_T(@a INT) RETURNS TABLE " +
                "WITH SCHEMABINDING AS RETURN (SELECT 1 AS X)";

            Assert.Equal(
                new[] { "SCHEMABINDING" },
                ObjectDeclarationExtractor.Extract(ddl)!.WithOptions);
        }

        [Fact]
        public void Extract_Procedure_ReturnsNull()
        {
            // 프로시저에는 SCHEMABINDING 옵션 자체가 없다. 표를 싣지 않는다.
            const string ddl = "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 END";

            Assert.Null(ObjectDeclarationExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_UnparsableDdl_ReturnsNull()
        {
            Assert.Null(ObjectDeclarationExtractor.Extract("CREATE FUNCTION ((("));
        }

        [Fact]
        public void Extract_EmptyDdl_ReturnsNull()
        {
            Assert.Null(ObjectDeclarationExtractor.Extract(null));
            Assert.Null(ObjectDeclarationExtractor.Extract("   "));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --nologo --filter "FullyQualifiedName~ObjectDeclarationExtractorTests"`
Expected: 전부 FAIL — 클래스가 없어 컴파일 에러.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Core/Services/ObjectDeclarationExtractor.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <summary>
    /// CREATE FUNCTION 선언부의 WITH 옵션을 뽑는다.
    ///
    /// [왜 별도 파일인가]
    /// DmlScopeExtractor는 DML 문장 단위 사실을 다루는데 이것은 객체 선언부의 사실이라
    /// 행 단위도 방문 대상도 다르다. 한 파일에 넣으면 "DML 범위 추출기"라는 이름이
    /// 거짓이 된다.
    ///
    /// [왜 필요한가 - 2026-08-21 축 A 감사]
    /// UF_GET_OUTYMD4REFUND와 UF_GET_SETTLE_EXCHANGERATE 둘 다 WITH 절이 없다는 것이
    /// DDL 원문에서 확정되는데, 명세서가 "제공되지 않아 확인할 수 없음"으로 적었다.
    /// 같은 자리가 재생성마다 다른 답을 냈다 - 8/20 판에는 언급이 아예 없었고 8/21 판에서
    /// "확인할 수 없음"이 새로 생겼다. 재료로 확정하면 이 흔들림이 닫힌다.
    /// </summary>
    public static class ObjectDeclarationExtractor
    {
        public const string ObjectDeclarationTableHeading =
            "### 객체 선언 (기계 확정 — 수정 금지)";

        /// <param name="WithOptions">
        /// 빈 목록이 곧 "스키마 바인딩 아님"이다. 표에서는 "(없음)"으로 렌더된다.
        /// </param>
        public sealed record ObjectDeclarationFact(
            string QualifiedName,
            IReadOnlyList<string> WithOptions);

        /// <summary>
        /// 함수가 아니거나 파싱에 실패하면 null. 프로시저에는 이 옵션 자체가 없으므로
        /// 표를 싣지 않는 것이 맞다.
        /// </summary>
        public static ObjectDeclarationFact? Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return null;

            var parser = new TSql160Parser(true);
            using var reader = new StringReader(ddlText);
            var fragment = parser.Parse(reader, out var errors);
            if (fragment == null || (errors != null && errors.Count > 0)) return null;

            var visitor = new CreateFunctionVisitor();
            fragment.Accept(visitor);
            return visitor.Fact;
        }

        private sealed class CreateFunctionVisitor : TSqlFragmentVisitor
        {
            public ObjectDeclarationFact? Fact { get; private set; }

            public override void Visit(CreateFunctionStatement node)
            {
                if (Fact != null) return;

                var name = string.Join(".", node.Name.Identifiers.Select(i => i.Value));
                var options = node.Options
                    .Select(o => Render(o.OptionKind))
                    .ToList();

                Fact = new ObjectDeclarationFact(name, options);
            }

            /// <summary>
            /// ScriptDom의 열거 이름(SchemaBinding)을 T-SQL 표기(SCHEMABINDING)로 옮긴다.
            /// 명세서 독자가 원본 DDL에서 찾을 수 있는 형태여야 한다.
            /// </summary>
            private static string Render(FunctionOptionKind kind) => kind switch
            {
                FunctionOptionKind.SchemaBinding => "SCHEMABINDING",
                FunctionOptionKind.Encryption => "ENCRYPTION",
                FunctionOptionKind.ReturnsNullOnNullInput => "RETURNS NULL ON NULL INPUT",
                FunctionOptionKind.CalledOnNullInput => "CALLED ON NULL INPUT",
                _ => kind.ToString().ToUpperInvariant()
            };
        }
    }
}
```

`FunctionOptionKind`의 실제 멤버 이름을 먼저 확인한다:

Run: `grep -rn "FunctionOptionKind" ~/.nuget/packages/microsoft.sqlserver.transactsql.scriptdom/180.37.3/ 2>/dev/null | head -3`

찾지 못하면 프로브로 확인한다 — Task 3 Step 1의 테스트가 실제 이름을 강제하므로, 이름이 틀리면 컴파일 에러로 즉시 드러난다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --nologo --filter "FullyQualifiedName~ObjectDeclarationExtractorTests"`
Expected: 7건 PASS.

- [ ] **Step 5: 전체 회귀**

Run: `dotnet test --nologo -v q`
Expected: 2066건 통과(2059 + 신규 7), 실패 0.

- [ ] **Step 6: 실물로 확인한다**

`UF_GET_OUTYMD4REFUND`와 `UF_GET_SETTLE_EXCHANGERATE`의 실제 DDL에서 `WithOptions`가 빈 목록으로 나오는지, `UP_UTIL_SETTLE_INS_EXTRA`(프로시저)에서 `null`이 나오는지 프로브로 확인한다.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/ObjectDeclarationExtractor.cs tests/ReSet.Core.Tests/ObjectDeclarationExtractorTests.cs
git commit -F- <<'EOF'
feat: 함수 선언부의 WITH 옵션을 뽑는다

UF_GET_OUTYMD4REFUND와 UF_GET_SETTLE_EXCHANGERATE 둘 다 WITH 절이 없다는 것이
DDL 원문에서 확정되는데 명세서가 "확인할 수 없음"으로 적어 🟡이었다. 같은 자리가
재생성마다 다른 답을 냈다 - 8/20 판에는 언급이 아예 없다가 8/21 판에서
"확인할 수 없음"이 새로 생겼다.

DML 문장이 아니라 객체 선언부의 사실이라 별도 파일에 둔다. 프로시저에는 이 옵션
자체가 없으므로 null을 돌려 표를 싣지 않는다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

### Task 4: 표 조립과 프롬프트 배선

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs`
- Test: `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`

**Interfaces:**
- Consumes: Task 1~3의 `LockHintFact`·`DmlScopeFact.OrderByColumns`·`ObjectDeclarationFact`와 헤딩 상수 셋
- Produces:
  - `private static List<string> BuildLockHintTableLines(IReadOnlyList<LockHintFact> facts)`
  - `private static List<string> BuildObjectDeclarationTableLines(ObjectDeclarationFact fact)`
  - 기존 `BuildDmlScopeTableLines`가 `ORDER BY` 칸을 낸다

- [ ] **Step 1: 배선 지점을 전수로 뽑는다**

먼저 세어 본다. 이 프로젝트는 이 부류를 세 번 연속 놓쳤다 — "지점 3개"라 했는데 4개였고, 다시 세니 5개였고, 최종 리뷰가 6번째를 찾았다.

```bash
grep -n "BuildDmlScopeTableLines\|BuildSetPredicateTableLines\|BuildReferencedFunctionTableLines\|BuildDerivedTableColumnLines" src/ReSet.Core/Services/AiService.cs | grep -v "private static"
```

기대: 세 경로가 나온다 — SP 최초 생성(`:391-442` 부근), 함수 명세서 경로(`:1103-1177` 부근), `BuildSpecSectionPrompts`의 `CrudAnalysis` 분기(`:2314-2357` 부근). 줄 번호는 앞 작업들 때문에 밀려 있으므로 **출력된 실제 번호를 쓴다.**

`OverviewAndParameters` 분기 위치도 확인한다:

```bash
grep -n 'sectionType == "OverviewAndParameters"' src/ReSet.Core/Services/AiService.cs
```

- [ ] **Step 2: 실패하는 테스트를 쓴다**

`AiServiceTests_Rich.cs`에 넣는다. 이 파일의 기존 테스트가 `BuildSpecificationPrompts`를 어떻게 부르는지 먼저 읽고 같은 방식을 쓴다.

```csharp
        [Fact]
        public void BuildSpecificationPrompts_ShouldCarryTheLockHintTable()
        {
            // 표 셋을 프롬프트에 싣지 않으면 모델은 이 사실을 볼 수 없다.
            var spDef = SpDefinitionWithLockHints();

            var result = new AiService(StubConfig()).BuildSpecificationPrompts(spDef);

            Assert.Contains(DmlScopeExtractor.LockHintTableHeading, result.UserPrompt);
            Assert.Contains("| UPDATE 1 |", result.UserPrompt);
            Assert.Contains("NOLOCK", result.UserPrompt);
        }

        [Fact]
        public void BuildSpecificationPrompts_DmlScopeTable_ShouldCarryOrderByColumn()
        {
            var spDef = SpDefinitionWithOrderBy();

            var result = new AiService(StubConfig()).BuildSpecificationPrompts(spDef);

            Assert.Contains("ORDER BY", result.UserPrompt);
            Assert.Contains("INYMD, CLIENTID", result.UserPrompt);
        }
```

`SpDefinitionWithLockHints`·`SpDefinitionWithOrderBy`·`StubConfig`는 이 파일의 기존 헬퍼 이름을 따른다. 없으면 기존 테스트가 쓰는 픽스처 구성 방식을 그대로 복사해 만든다.

- [ ] **Step 3: 실패를 확인한다**

Run: `dotnet test --nologo --filter "FullyQualifiedName~ShouldCarryTheLockHintTable|FullyQualifiedName~ShouldCarryOrderByColumn"`
Expected: FAIL — 표가 프롬프트에 없다.

- [ ] **Step 4: 조립기 헬퍼를 쓴다**

기존 `BuildReferencedFunctionTableLines` 옆에 둔다.

```csharp
        /// <summary>
        /// 잠금 힌트 표 본문을 만든다. 헤딩 리터럴은 추출기의 상수를 쓴다 - L1이 산출물을
        /// 대조할 때 찾는 접두와 같아야 하고, 문구를 고칠 때 한쪽만 바뀌는 일을 막는다.
        /// 세 배선 경로가 이 헬퍼를 공유해야 같은 표가 나간다는 것이 코드로 보장된다.
        /// </summary>
        private static List<string> BuildLockHintTableLines(IReadOnlyList<LockHintFact> facts)
        {
            var lines = new List<string>
            {
                $"   {LockHintIntroText}",
                $"   {DmlScopeExtractor.LockHintTableHeading}",
                "   | 문장 | 라인 | 테이블 | 별칭 | 힌트 |",
                "   | :--- | :--- | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                var hints = fact.Hints.Count == 0 ? "(없음)" : string.Join(", ", fact.Hints);
                lines.Add(
                    $"   | {fact.Operation} {fact.StatementOrdinal} | {fact.Line} | " +
                    $"{EscapeTableCell(fact.Table)} | {EscapeTableCell(fact.Alias)} | {hints} |");
            }

            return lines;
        }

        private const string LockHintIntroText =
            "[CRITICAL LOCK HINT TABLE] The following lock hints are MACHINE-DERIVED from the source DDL. " +
            "Copy this table verbatim into `## CRUD 분석` under the exact heading shown. " +
            "A row with `(없음)` means that scan carries NO hint - do not omit those rows and do not " +
            "generalise across statements: the same table may carry a hint in one statement and not another.";

        /// <summary>
        /// 객체 선언 표. 함수에만 실린다 - 프로시저에는 이 옵션 자체가 없다.
        /// </summary>
        private static List<string> BuildObjectDeclarationTableLines(
            ObjectDeclarationExtractor.ObjectDeclarationFact fact)
        {
            var options = fact.WithOptions.Count == 0
                ? "(없음)"
                : string.Join(", ", fact.WithOptions);

            return new List<string>
            {
                $"   {ObjectDeclarationIntroText}",
                $"   {ObjectDeclarationExtractor.ObjectDeclarationTableHeading}",
                "   | 객체 | WITH 옵션 |",
                "   | :--- | :--- |",
                $"   | {EscapeTableCell(fact.QualifiedName)} | {options} |"
            };
        }

        private const string ObjectDeclarationIntroText =
            "[CRITICAL OBJECT DECLARATION TABLE] The WITH options below are MACHINE-DERIVED from the " +
            "CREATE statement. Copy this table verbatim into `## 개요` under the exact heading shown. " +
            "`(없음)` settles the question: the object is NOT schema-bound. Never write that schema " +
            "binding could not be determined.";
```

`BuildDmlScopeTableLines`에는 칸을 더한다 — 헤더 행, 구분 행, 각 데이터 행 셋 다 고쳐야 한다.

```csharp
                var orderBy = fact.Operation == "INSERT"
                    ? (fact.OrderByColumns.Count == 0
                        ? "(없음)"
                        : EscapeTableCell(string.Join(", ", fact.OrderByColumns)))
                    : "—";   // UPDATE·DELETE는 최상위 ORDER BY가 문법상 불가하다
```

- [ ] **Step 5: 세 경로에 배선한다**

Step 1에서 뽑은 실제 줄 번호를 쓴다.

- SP 최초 생성 경로: `BuildReferencedFunctionTableLines` 호출 뒤에 잠금 힌트 추가
- 함수 명세서 경로: 같은 자리에 잠금 힌트 추가 **+ 객체 선언 추가**
- `CrudAnalysis` 분기: 잠금 힌트 추가
- `OverviewAndParameters` 분기: 객체 선언 추가 (`ObjectDeclarationExtractor.Extract`가 `null`이 아닐 때만)

각 자리에서 재료가 비면 표를 싣지 않는다 — 기존 세 표와 같은 규칙이다.

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test --nologo --filter "FullyQualifiedName~AiServiceTests_Rich"`
Expected: 전부 PASS.

- [ ] **Step 7: 배선을 검산한다**

```bash
grep -n "BuildLockHintTableLines\|BuildObjectDeclarationTableLines" src/ReSet.Core/Services/AiService.cs | grep -v "private static"
```

기대: 잠금 힌트 3곳, 객체 선언 2곳. 개수가 다르면 놓친 경로가 있다.

- [ ] **Step 8: 전체 회귀 + 커밋**

Run: `dotnet test --nologo -v q` → 2068건 통과(2066 + 신규 2).

```bash
git add src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/AiServiceTests_Rich.cs
git commit -F- <<'EOF'
feat: 잠금 힌트·객체 선언 표를 프롬프트에 싣고 ORDER BY 칸을 더한다

배선 지점은 grep으로 전수로 뽑았다. 2026-08-20 참조 함수 표 작업에서 이 부류를
세 번 연속 놓쳤다 - "지점 3개"라 했는데 4개였고, 다시 세니 5개였고, 최종 리뷰가
6번째를 찾았다. 그래서 이번에는 세어서 시작하고 세어서 끝냈다.

잠금 힌트 3곳(SP 최초 생성·함수 경로·CrudAnalysis 분기), 객체 선언 2곳(함수
경로·OverviewAndParameters 분기). ORDER BY는 BuildDmlScopeTableLines 한 곳만
고치면 세 경로가 헬퍼를 공유하므로 따라온다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

### Task 5: L1 앵커

**Files:**
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs`
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: Task 1~4
- Produces: `SpecExpectations`에 `LockHints`·`ObjectDeclaration` 필드, `MechanicalValidator`에 검사 둘

`ORDER BY`는 기존 「DML 범위」 표의 칸이므로 그 표의 기존 L1 검사가 이미 덮는다 — 새 검사를 만들지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        [Fact]
        public void Validate_ShouldFlagMissingLockHintTable()
        {
            // 표만 넣고 검사를 안 세우면 모델이 옮겼는지 아무도 모른다. 참조 함수 표가
            // 그 상태로 한 판 나갔고 L1 앵커를 나중에 따로 붙여야 했다.
            var markdown = "## 개요\n내용\n\n## CRUD 분석\n표 없음\n";

            var result = new MechanicalValidator().Validate(
                markdown, SpecExpectations.From(SpWithLockHints()));

            Assert.False(result.IsValid);
            Assert.Contains(
                result.DetailedErrors,
                e => e.Message.Contains(DmlScopeExtractor.LockHintTableHeading));
        }

        [Fact]
        public void Validate_ShouldFlagMissingObjectDeclarationTable()
        {
            var markdown = "## 개요\n내용\n";

            var result = new MechanicalValidator().Validate(
                markdown, SpecExpectations.From(FunctionWithoutWithOptions()));

            Assert.False(result.IsValid);
            Assert.Contains(
                result.DetailedErrors,
                e => e.Message.Contains(
                    ObjectDeclarationExtractor.ObjectDeclarationTableHeading));
        }

        [Fact]
        public void Validate_ShouldNotFlagWhenThereIsNoMaterial()
        {
            // 재료가 없으면 검사하지 않는다. 잠금 힌트가 없는 객체, 함수가 아닌 객체.
            var markdown = "## 개요\n내용\n\n## CRUD 분석\n표 없음\n";

            var result = new MechanicalValidator().Validate(
                markdown, SpecExpectations.From(SpWithoutAnyScan()));

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Message.Contains(DmlScopeExtractor.LockHintTableHeading));
        }
```

픽스처 헬퍼(`SpWithLockHints`·`FunctionWithoutWithOptions`·`SpWithoutAnyScan`)는 이 파일의 기존 픽스처(`ReferencedFunctionSp()` 등)를 본떠 만든다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --nologo --filter "FullyQualifiedName~LockHintTable|FullyQualifiedName~ObjectDeclarationTable|FullyQualifiedName~NoMaterial"`
Expected: FAIL.

- [ ] **Step 3: 기대값을 수집한다**

`SpecExpectations.cs`에 필드를 더한다.

```csharp
        public IReadOnlyList<LockHintFact> LockHints { get; init; } = Array.Empty<LockHintFact>();

        public ObjectDeclarationExtractor.ObjectDeclarationFact? ObjectDeclaration { get; init; }
```

`From()`에서 채운다.

```csharp
            var lockHints = DmlScopeExtractor.ExtractLockHints(spDef.DdlText);
            var objectDeclaration = ObjectDeclarationExtractor.Extract(spDef.DdlText);
```

**조기 반환 AND 사슬에도 넣는다.** 빠뜨리면 재료가 있는데 기대값이 비어 검사가 통째로 꺼진다 — 2026-08-20에 그 한 줄을 놓칠 뻔했다.

```csharp
                && lockHints.Count == 0
                && objectDeclaration == null
```

record 생성에도 더한다: `LockHints = lockHints, ObjectDeclaration = objectDeclaration`.

- [ ] **Step 4: 검사를 쓴다**

`CheckReferencedFunctions` 옆에 둔다. 그 메서드를 본떠 같은 모양으로 만든다 — 재료가 비면 즉시 반환, 헤딩이 없으면 오류, 헤딩은 있는데 행이 빠졌으면 오류.

`Validate`의 검사 등록부에 두 줄을 더한다.

```csharp
            CheckLockHints(cleansed, expectations, result);
            CheckObjectDeclaration(cleansed, expectations, result);
```

`ErrorType`은 기존 `SetPredicateMismatch`를 재사용해도 되고 새로 만들어도 된다. **어느 쪽이든 지적은 모델에게 닿는다** — `BuildSuggestedPromptFix`가 2026-08-20에 catch-all 버킷을 얻어 열거되지 않은 타입도 내용이 실려 나간다.

- [ ] **Step 5: 통과를 확인한다 + 전체 회귀**

Run: `dotnet test --nologo -v q`
Expected: 2071건 통과(2068 + 신규 3), 실패 0.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/SpecExpectations.cs src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -F- <<'EOF'
feat: 잠금 힌트·객체 선언 표에 L1 앵커를 세운다

조립기가 표를 넣지만 모델이 그것을 옮겼는지는 아무도 확인하지 않는다 - 참조 함수
표가 그 상태로 한 판 나갔고 L1 앵커를 나중에 따로 붙여야 했다. 같은 실수를
반복하지 않는다.

ORDER BY는 기존 DML 범위 표의 칸이므로 그 표의 검사가 이미 덮는다.

SpecExpectations.From의 조기 반환 AND 사슬에도 넣었다 - 빠뜨리면 재료가 있는데
기대값이 비어 검사가 통째로 꺼진다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

### Task 6: 캐시 상향과 문서

**Files:**
- Modify: `src/ReSet.Core/Services/CacheManager.cs`
- Modify: `tests/ReSet.Core.Tests/CacheManagerTests.cs`
- Modify: `.claude/skills/reset-consistency-audit/references/axis-a.md`
- Modify: `docs/architecture.md`

**Interfaces:**
- Consumes: Task 1~5
- Produces: 없음 (마무리 작업)

- [ ] **Step 1: 캐시 버전 테스트를 고친다**

`CacheManagerTests.cs`의 `UpdateCache_StampsTheCurrentFormatVersion`에서 리터럴을 8로 바꾸고 주석을 갱신한다. 이 리터럴은 일부러 못 박혀 있다 — 버전을 올리면 테스트가 깨지고, 깨진 자리에서 "정말 전건 재분석을 의도했는가"를 한 번 더 묻게 된다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --nologo --filter "FullyQualifiedName~StampsTheCurrentFormatVersion"`
Expected: FAIL — `Expected: 8, Actual: 7`.

- [ ] **Step 3: 버전을 올린다**

`CacheManager.cs`의 버전 주석 목록에 항목을 더하고 상수를 8로 바꾼다.

```csharp
        // 8: 잠금 힌트·객체 선언 표가 새로 실리고 DML 범위 표에 ORDER BY 칸이 붙었다
        //    (2026-08-21 축 A 감사의 🟡 다섯). 프롬프트 입력이 달라졌으므로 옛 엔트리를
        //    재사용하면 산출물이 옛 재료 그대로 남는다.
        private const int CurrentCacheFormatVersion = 8;
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --nologo -v q`
Expected: 2071건 통과, 실패 0.

- [ ] **Step 5: 감사 계약을 갱신한다**

`.claude/skills/reset-consistency-audit/references/axis-a.md`에서 기존 네 표의 대조 계약이 적힌 자리를 찾아, 새 표 둘과 `ORDER BY` 칸을 같은 모양으로 더한다.

적을 것:
- 「잠금 힌트」 표: 행 단위가 (문장 × 스캔 자리)이고 전수라는 것. `FROM`이 없고 대상에 힌트도 없는 문장은 행이 없는 것이 정상이며, 「DML 범위」 표와 나란히 보면 어느 문장이 빠졌는지 보인다는 것.
- 「객체 선언」 표: 함수에만 실린다. 프로시저에 없는 것은 결함이 아니다. `(없음)`이 곧 "스키마 바인딩 아님"이다.
- `ORDER BY` 칸: `UPDATE`·`DELETE`의 `—`는 문법상 불가라는 뜻이지 누락이 아니다.

- [ ] **Step 6: 아키텍처 문서를 갱신한다**

`docs/architecture.md`의 캐시 포맷 버전 표기를 7에서 8로 바꾸고 사유를 한 줄 더한다.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/CacheManager.cs tests/ReSet.Core.Tests/CacheManagerTests.cs .claude/skills/reset-consistency-audit/references/axis-a.md docs/architecture.md
git commit -F- <<'EOF'
chore: 캐시 형식을 8로 올리고 감사 계약에 새 표 셋을 적는다

이 브랜치가 프롬프트에 싣는 재료를 셋 바꿨으므로 옛 엔트리를 재사용하면 산출물이
옛 재료 그대로 남는다. 31개 전건 재분석이 된다.

감사 계약에는 새 표의 "빈 것이 정상인 경우"를 명시했다 - FROM 없는 문장이 잠금
힌트 표에 없는 것, 프로시저에 객체 선언 표가 없는 것, UPDATE의 ORDER BY가 —인 것.
이것을 안 적으면 다음 감사가 정상을 결함으로 센다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
```

---

## 실물 검증 (전 작업 완료 후)

단위 테스트가 픽스처의 우연으로 통과하는 일을 두 번 겪었다 — 2026-08-20 파생 테이블 별칭, 2026-08-21 정규화 가드. 둘 다 실물 대조가 잡았다. 그러므로 생략하지 않는다.

임시 콘솔 프로젝트를 만들어 `ReSet.Core`를 참조하고, 아래 다섯 객체의 실제 DDL로 추출기 셋을 돌려 감사 판정과 대조한다.

| 객체 | 기대 |
|---|---|
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | `TSettleMst`가 사전확인·UPDATE1·UPDATE2에서 `NOLOCK`, `TPGProperty`의 `PG` 4곳은 `(없음)`, `P`·`Y`는 `NOLOCK` |
| `UP_Util_Settle_Summary_AcqManual` | `NOLOCK`이 3곳(커서 조회 둘 + `DELETE` 대상 스캔) |
| `UP_UTIL_STAT_PGCOLLECT_INS` | `INSERT 1`의 `OrderByColumns`가 `INYMD, CLIENTID, PGNAME, MALLID` |
| `UF_GET_OUTYMD4REFUND` | `WithOptions`가 빈 목록 |
| `UF_GET_SETTLE_EXCHANGERATE` | `WithOptions`가 빈 목록 |

기대와 다르면 그 작업의 Step 1로 돌아간다.

## 다음 단계 (이 계획서 밖)

1. 31개 재생성 — 캐시 8로 전건 무효.
2. 축 A 재감사 — 🟡 다섯이 닫혔는지, 재생성이 새 결함을 만들지 않았는지.
3. 남은 🟡 넷(주석 블록·`INS_EXTRA`의 추론 셋·`UIF_SettleYMD`의 파서 값 부정)은 이 설계가 닫지 않는다. 스펙의 마지막 절에 이유를 적어 두었다.
