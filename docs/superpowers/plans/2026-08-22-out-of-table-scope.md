# 축 A 재감사 ③ — 기존 표의 범위 확대 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 기존 네 기계 확정 표의 방문 범위와 술어 표현력을 넓혀, 산문이 유일한 근거였던 11건(🟠 5 · 🟡 6)을 표가 확정하게 한다.

**Architecture:** `DmlScopeExtractor`의 방문자들이 지금 `Insert`/`Update`/`DeleteSpecification`만 방문하는 것이 뿌리다. 문장 컨텍스트 스택을 도입해 FROM 절이 있는 독립 `SelectStatement`를 `SELECT n`으로, `IF` 술어 안의 질의를 `IF n`으로 채번하고, DML 안의 하위 질의는 그 DML 문장에 `하위 질의` 범위로 귀속시킨다. 집합 술어 표는 행 단위를 원소에서 최상위 AND 항으로 올리고 「술어 원문」 열을 더해, 분해되는 항은 지금처럼 분해해서도 싣고 분해되지 않는 항은 원문만 싣는다. **표는 하나도 늘지 않는다** — 카탈로그 등록·Critic 면제 블록·프롬프트 갈래 배선은 건드리지 않는다.

**Tech Stack:** C# / .NET 10, Microsoft.SqlServer.TransactSql.ScriptDom (`TSql160Parser`), xUnit.

**Spec:** `docs/superpowers/specs/2026-08-22-out-of-table-scope-design.md`

## Global Constraints

- **①②가 병합된 뒤에 시작한다.** Task 8·9가 `MechanicalValidator.cs`·`SpecExpectations.cs`·`CacheManager.cs`를 고치는데, `2026-08-22-audit-defect-closure.md`의 Task 3~6이 같은 파일의 `ErrorType` enum과 `Validate` 호출부에 줄을 더한다. 충돌이 확실하다.
- **기존 DML 문장 번호는 변하지 않아야 한다.** `UPDATE 7`은 이 작업 뒤에도 `UPDATE 7`이다. 네 방문자(`DmlScopeVisitor`·`LockHintVisitor`·`SetPredicateVisitor`·`ReferencedFunctionVisitor`)가 서로를 참조하지 않고도 같은 번호를 내는 계약이 유지되어야 한다.
- **새 표를 만들지 않는다.** `MachineConfirmedTables.All`은 이 계획에서 한 줄도 바뀌지 않는다.
- **`SetPredicateVisitor`와 `ReferencedFunctionVisitor`는 넓히지 않는다.** 스펙 §3의 결정 1은 네 방문자에 같은 규칙을 더한다고 적었으나, 이번 11건 중 독립 SELECT의 **집합 술어**나 **함수 호출**이 새어 나간 사례는 실측되지 않았다. 넓히지 않아도 채번 계약은 깨지지 않는다 — 각 방문자는 종류별 카운터를 독립으로 들고, `UPDATE` 카운터는 어느 쪽에서도 변하지 않는다. 어떤 문장이 한 표에만 나타나는 것은 지금도 정상이다(WHERE 없는 UPDATE는 집합 술어 표에 나오지 않는다). YAGNI로 좁힌 자리이므로, 나중에 그 부류의 결함이 실측되면 Task 1·4와 같은 판정 함수를 그대로 재사용해 더한다.
- **렌더러는 단위 테스트로 직접 부르지 않는다.** `BuildSetPredicateTableLines` 등은 `private static`이고 `InternalsVisibleTo`가 없다(확인함). 이 저장소는 렌더된 모양의 마크다운을 테스트에 손으로 써서 L1으로 검증한다(`MechanicalValidatorTests.cs:3359`의 선례). 렌더러와 L1은 짝이므로 한 Task에서 함께 바꾼다.
- **`MechanicalValidator.Validate`는 인스턴스 메서드다.** 호출은 `new MechanicalValidator().Validate(markdown, expectations)`이다(`MechanicalValidator.cs:117`).
- **소프트 페일 규약.** 파싱 실패는 예외를 던지지 않고 빈 목록으로 진행한다(AGENTS.md 범주 2). 기존 `Extract*` 진입점의 `try`/`catch` 구조를 그대로 둔다.
- 각 Task는 실패 테스트 → 실패 확인 → 최소 구현 → 통과 확인 → 커밋의 한 사이클이다.
- 테스트 실행은 `dotnet test --filter "FullyQualifiedName~<TestClass>.<TestName>"`, 전체는 `dotnet test`.

---

## 실측으로 확정된 노드 형태

구현 전에 프로브로 확인했다(2026-08-22). 이 표가 Task 1~4의 판정 규칙의 근거다.

| 원문 형태 | ScriptDom 노드 | DML 깊이 | `FromClause` |
|---|---|---|---|
| `SELECT @a = 1` | `SelectStatement` | 밖 | **null** |
| `SELECT @v = MIN(x) FROM t WITH(NOLOCK)` | `SelectStatement` | 밖 | 있음 |
| `IF EXISTS(SELECT … WITH(NOLOCK))` | `IfStatement` + `ScalarSubquery` | 밖 | — |
| `DECLARE Cur CURSOR FOR SELECT … ORDER BY …` | `DeclareCursorStatement` + **`SelectStatement`** | 밖 | 있음 |
| `UPDATE … WHERE x IN (SELECT … WITH(NOLOCK))` | `UpdateSpecification` + `ScalarSubquery` | **안** | — |
| `INSERT INTO t SELECT … FROM …` | `InsertSpecification` **만** | — | — |
| 함수 본문 `SELECT @r = y FROM t WITH(NOLOCK)` | `SelectStatement` | 밖 | 있음 |

두 가지가 중요하다.

1. **커서 원천 질의는 `SelectStatement`로 방문된다.** `DeclareCursorStatement`를 따로 다룰 필요가 없다.
2. **`INSERT … SELECT`의 원천은 `SelectStatement`로 방문되지 않는다.** 따라서 독립 `SelectStatement`를 세어도 INSERT 원천이 중복으로 잡히지 않는다.

---

### Task 1: `LockHintVisitor`가 독립 SELECT를 `SELECT n`으로 담는다

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs` (`LockHintVisitor`)
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`

**Interfaces:**
- Produces: `LockHintFact.Operation`이 `"SELECT"`인 행. Task 2·3·4가 같은 채번 규칙을 쓴다.
- Produces: `LockHintVisitor` 안의 `_dmlDepth` 추적. Task 3이 이것을 읽는다.

- [ ] **Step 1: 실패 테스트를 쓴다**

`tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`에 추가한다.

```csharp
[Fact]
public void ExtractLockHints_StandaloneSelectWithFrom_ShouldBeNumberedAsSelect()
{
    // INS_EXTRA:22 실측 - 변수 대입 SELECT의 NOLOCK이 표 밖이라
    // 문서 전체에서 한 번도 언급되지 않았다(2026-08-22 축 A 재감사 🟡).
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @v_strReqYMD = MIN(ReqYMD)
    FROM   PaymentDB.dbo.TExtraSettleIn WITH(NOLOCK)

    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A WITH(NOLOCK)
END";

    var facts = DmlScopeExtractor.ExtractLockHints(ddl);

    var select = Assert.Single(facts, f => f.Operation == "SELECT");
    Assert.Equal(1, select.StatementOrdinal);
    Assert.Equal("PaymentDB.dbo.TExtraSettleIn", select.Table);
    Assert.Equal("최상위", select.Scope);
    Assert.Equal(new[] { "NOLOCK" }, select.Hints);

    // 기존 DML 채번이 밀리지 않는다.
    var update = Assert.Single(facts, f => f.Operation == "UPDATE");
    Assert.Equal(1, update.StatementOrdinal);
}

[Fact]
public void ExtractLockHints_SelectWithoutFrom_ShouldNotBeNumbered()
{
    // 스캔할 자리가 없는 대입은 문장 번호를 소비하지 않는다.
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @a = 1

    SELECT @v = MIN(ReqYMD) FROM dbo.TA WITH(NOLOCK)
END";

    var facts = DmlScopeExtractor.ExtractLockHints(ddl);

    var select = Assert.Single(facts, f => f.Operation == "SELECT");
    Assert.Equal(1, select.StatementOrdinal);
    Assert.Equal("dbo.TA", select.Table);
}

[Fact]
public void ExtractLockHints_CursorSourceSelect_ShouldBeNumberedAsSelect()
{
    // PROC_ETC:62 실측 - 커서 원천 질의는 SelectStatement로 방문된다(프로브 확인).
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE Cur_SettlePost CURSOR FOR
    SELECT A.ClientID
    FROM   dbo.TSettleMst A WITH(NOLOCK)
    ORDER BY A.OutYMD, A.ClientID
END";

    var facts = DmlScopeExtractor.ExtractLockHints(ddl);

    var select = Assert.Single(facts);
    Assert.Equal("SELECT", select.Operation);
    Assert.Equal("dbo.TSettleMst", select.Table);
    Assert.Equal("A", select.Alias);
}

[Fact]
public void ExtractLockHints_InsertSelectSource_ShouldNotProduceSelectRow()
{
    // INSERT ... SELECT의 원천은 SelectStatement로 방문되지 않는다(프로브 확인).
    // 이 테스트는 그 사실이 깨지면 INSERT 원천이 두 번 실린다는 것을 잡는다.
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    INSERT INTO dbo.TF (C)
    SELECT C FROM dbo.TG WITH(NOLOCK)
END";

    var facts = DmlScopeExtractor.ExtractLockHints(ddl);

    Assert.Empty(facts.Where(f => f.Operation == "SELECT"));
    Assert.Single(facts, f => f.Operation == "INSERT");
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests.ExtractLockHints_StandaloneSelectWithFrom_ShouldBeNumberedAsSelect"`

Expected: FAIL — `Assert.Single(facts, f => f.Operation == "SELECT")`가 "The collection did not contain any matching elements"로 실패한다. `ExtractLockHints_InsertSelectSource_ShouldNotProduceSelectRow`는 이 시점에 **통과**한다(현재도 SELECT 행을 내지 않으므로). 그것이 회귀 가드라는 주장의 확인이다.

- [ ] **Step 3: 최소 구현**

`LockHintVisitor`의 세 `Visit` 오버라이드를 `ExplicitVisit`으로 바꾸고 DML 깊이를 추적한다. `base.ExplicitVisit(node)`가 자식 순회를 잇는다 — 이 파일의 `FromTableCollector.ExplicitVisit(QueryDerivedTable)`이 이미 같은 패턴을 쓴다.

```csharp
        private sealed class LockHintVisitor : TSqlFragmentVisitor
        {
            /// <summary>최상위 FROM에 직접 실린 참조. SetPredicateFact.Scope와 같은 문자열.</summary>
            private const string TopLevelScope = "최상위";

            /// <summary>파생 테이블 안의 참조.</summary>
            private const string DerivedScope = "파생";

            public List<LockHintFact> Facts { get; } = new();

            private readonly Dictionary<string, int> _ordinals = new(StringComparer.Ordinal);

            /// <summary>
            /// DML 문장 안인지 밖인지. `ExplicitVisit`로 진입/이탈을 감싸 추적한다.
            ///
            /// [왜 필요한가 - 2026-08-22 축 A 재감사] 같은 `ScalarSubquery`라도 DML 안에
            /// 있으면 그 문장의 하위 질의이고(그 문장 번호를 그대로 써야 한다), DML 밖에
            /// 있으면 제어 흐름 술어다(자기 번호를 받아야 한다). 노드 타입만으로는
            /// 갈리지 않는다 - 프로브 실측으로 확인했다.
            /// </summary>
            private int _dmlDepth;

            public override void ExplicitVisit(InsertSpecification node)
            {
                var ordinal = NextOrdinal("INSERT");

                // 원천이 UNION(BinaryQueryExpression)이면 갈래마다 FROM이 다르므로
                // QuerySpecificationsOf로 전부 훑는다. VALUES 원천이면 빈 시퀀스를 낸다.
                if (node.InsertSource is SelectInsertSource select)
                {
                    foreach (var spec in QuerySpecificationsOf(select.Select))
                    {
                        CollectFrom("INSERT", ordinal, spec.FromClause);
                    }
                }

                RecordTargetHint("INSERT", ordinal, node.Target);

                _dmlDepth++;
                base.ExplicitVisit(node);
                _dmlDepth--;
            }

            public override void ExplicitVisit(UpdateSpecification node)
            {
                var ordinal = NextOrdinal("UPDATE");
                CollectFrom("UPDATE", ordinal, node.FromClause);
                RecordTargetHint("UPDATE", ordinal, node.Target);

                _dmlDepth++;
                base.ExplicitVisit(node);
                _dmlDepth--;
            }

            public override void ExplicitVisit(DeleteSpecification node)
            {
                var ordinal = NextOrdinal("DELETE");
                CollectFrom("DELETE", ordinal, node.FromClause);
                RecordTargetHint("DELETE", ordinal, node.Target);

                _dmlDepth++;
                base.ExplicitVisit(node);
                _dmlDepth--;
            }

            /// <summary>
            /// DML 밖의 독립 SELECT. 변수 대입 SELECT · 커서 원천 질의 · 함수 본문
            /// SELECT가 전부 이 노드로 온다(프로브 실측 - `DECLARE CURSOR FOR SELECT`의
            /// 원천도 `SelectStatement`다). `INSERT ... SELECT`의 원천은 이 노드로 오지
            /// 않으므로 중복되지 않는다.
            ///
            /// [FROM이 없으면 세지 않는 이유] `SELECT @a = 1`에는 스캔할 자리가 없다.
            /// 번호를 소비하면 표에 낼 행도 없이 뒤 문장의 번호만 민다.
            /// `RecordTargetHint`가 "FROM도 없고 힌트도 없는 문장"을 싣지 않는 것과 같은
            /// 판단이다.
            /// </summary>
            public override void ExplicitVisit(SelectStatement node)
            {
                if (_dmlDepth == 0 && HasFromClause(node))
                {
                    CollectFromQuery("SELECT", NextOrdinal("SELECT"), node.QueryExpression);
                }

                base.ExplicitVisit(node);
            }

            private static bool HasFromClause(SelectStatement node) =>
                QuerySpecificationsOf(node.QueryExpression).Any(q => q.FromClause != null);

            /// <summary>UNION 갈래를 포함해 질의식의 모든 FROM을 훑는다.</summary>
            private void CollectFromQuery(string operation, int ordinal, QueryExpression? query)
            {
                if (query == null) return;

                foreach (var spec in QuerySpecificationsOf(query))
                {
                    CollectFrom(operation, ordinal, spec.FromClause);
                }
            }
```

`NextOrdinal` · `CollectFrom` · `RecordTargetHint` · `Add`는 그대로 둔다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests"`
Expected: 새 테스트 넷을 포함해 전부 PASS.

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0. `Visit`을 `ExplicitVisit`으로 바꾼 것이 기존 순회 동작을 바꾸지 않았다는 회귀 확인이다.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs
git commit -m "feat: 잠금 힌트 표가 DML 밖 독립 SELECT의 스캔을 담는다"
```

---

### Task 2: `LockHintVisitor`가 `IF` 술어 안 스캔을 `IF n`으로 담는다

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs` (`LockHintVisitor`)
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`

**Interfaces:**
- Consumes: Task 1의 `_dmlDepth`.
- Produces: `LockHintFact.Operation`이 `"IF"`인 행.

- [ ] **Step 1: 실패 테스트를 쓴다**

```csharp
[Fact]
public void ExtractLockHints_ControlFlowPredicate_ShouldBeNumberedAsIf()
{
    // INS_EXTRA:31 실측 - -9 차단 게이트의 판단 근거 스캔이다.
    // 축 A 계약이 이 자리를 제어 흐름 술어 하위 질의의 실물 사례로 지목한다.
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF EXISTS(SELECT PLTID
              FROM   TSettleMst WITH(NOLOCK)
              WHERE  ProcYMD = @pi_strYMD)
    BEGIN
        RETURN -9
    END
END";

    var facts = DmlScopeExtractor.ExtractLockHints(ddl);

    var fact = Assert.Single(facts);
    Assert.Equal("IF", fact.Operation);
    Assert.Equal(1, fact.StatementOrdinal);
    Assert.Equal("TSettleMst", fact.Table);
    Assert.Equal("최상위", fact.Scope);
    Assert.Equal(new[] { "NOLOCK" }, fact.Hints);
}

[Fact]
public void ExtractLockHints_TwoControlFlowPredicates_ShouldNumberIndependently()
{
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF EXISTS(SELECT 1 FROM dbo.TA WITH(NOLOCK)) RETURN -1
    IF EXISTS(SELECT 1 FROM dbo.TB WITH(NOLOCK)) RETURN -2
END";

    var facts = DmlScopeExtractor.ExtractLockHints(ddl);

    Assert.Equal(2, facts.Count);
    Assert.Equal(1, facts[0].StatementOrdinal);
    Assert.Equal("dbo.TA", facts[0].Table);
    Assert.Equal(2, facts[1].StatementOrdinal);
    Assert.Equal("dbo.TB", facts[1].Table);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests.ExtractLockHints_ControlFlowPredicate_ShouldBeNumberedAsIf"`
Expected: FAIL — `Assert.Single(facts)`가 "The collection was empty"로 실패한다.

- [ ] **Step 3: 최소 구현**

`IfStatement`의 술어 안에 있는 동안을 표시하고, 그 안의 `ScalarSubquery`를 `IF n`으로 센다. `IfStatement` 전체가 아니라 **술어(`Predicate`)만** 감싸는 것이 핵심이다 — `THEN`/`ELSE` 본문 안의 DML은 자기 번호를 받아야 한다.

```csharp
            /// <summary>
            /// 제어 흐름 술어 안의 스캔. `IF EXISTS(SELECT ... WITH(NOLOCK))`이 실물이다.
            ///
            /// [본문이 아니라 술어만 감싸는 이유] `IF ... BEGIN UPDATE ... END`의 UPDATE는
            /// 자기 문장이고 자기 번호를 받아야 한다. 술어만 훑고 본문은 평소대로
            /// 자식 순회에 맡긴다.
            /// </summary>
            public override void ExplicitVisit(IfStatement node)
            {
                var ordinal = NextOrdinal("IF");
                var collected = false;

                foreach (var query in SubqueriesOf(node.Predicate))
                {
                    CollectFromQuery("IF", ordinal, query);
                    collected = true;
                }

                if (!collected)
                {
                    // 스캔이 없었으면 번호를 돌려준다 - 뒤 IF가 밀리지 않는다.
                    _ordinals["IF"] = ordinal - 1;
                }

                base.ExplicitVisit(node);
            }

            /// <summary>불리언 식 안의 하위 질의를 모은다.</summary>
            private static IEnumerable<QueryExpression> SubqueriesOf(BooleanExpression? predicate)
            {
                if (predicate == null) yield break;

                var collector = new SubqueryCollector();
                predicate.Accept(collector);

                foreach (var query in collector.Queries) yield return query;
            }

            private sealed class SubqueryCollector : TSqlFragmentVisitor
            {
                public List<QueryExpression> Queries { get; } = new();

                public override void Visit(ScalarSubquery node) => Queries.Add(node.QueryExpression);
            }
```

`IfStatement`의 술어 안 `ScalarSubquery`는 `_dmlDepth`가 0이므로 Task 3의 하위 질의 처리와 겹치지 않는다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests"`
Expected: PASS.

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs
git commit -m "feat: 잠금 힌트 표가 제어 흐름 술어 안의 스캔을 담는다"
```

---

### Task 3: `LockHintVisitor`가 DML 안 하위 질의를 `하위 질의` 범위로 담는다

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs` (`LockHintVisitor`)
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`

**Interfaces:**
- Consumes: Task 1의 `_dmlDepth`, Task 2의 `SubqueryCollector`.
- Produces: `LockHintFact.Scope`의 세 번째 값 `"하위 질의"`.

- [ ] **Step 1: 실패 테스트를 쓴다**

```csharp
[Fact]
public void ExtractLockHints_SubqueryInsideDml_ShouldKeepStatementOrdinalAndMarkScope()
{
    // COMM_UPD:145 · EXCEPTION_PROC:529 실측 - 최상위 WHERE 하위 질의의 NOLOCK이
    // 표 밖이라 산문도 함께 침묵했다(2026-08-22 축 A 재감사 🟡).
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TSettleMst A WITH(NOLOCK)
    WHERE  A.PLTID IN (SELECT PLTID FROM PaymentDB.dbo.TCCanceledMst WITH(NOLOCK))
END";

    var facts = DmlScopeExtractor.ExtractLockHints(ddl);

    Assert.All(facts, f => Assert.Equal("UPDATE", f.Operation));
    Assert.All(facts, f => Assert.Equal(1, f.StatementOrdinal));

    var sub = Assert.Single(facts, f => f.Table == "PaymentDB.dbo.TCCanceledMst");
    Assert.Equal("하위 질의", sub.Scope);
    Assert.Equal(new[] { "NOLOCK" }, sub.Hints);

    var top = Assert.Single(facts, f => f.Table == "dbo.TSettleMst");
    Assert.Equal("최상위", top.Scope);
}

[Fact]
public void ExtractLockHints_DmlInsideIfBody_ShouldGetItsOwnOrdinal()
{
    // IF 본문 안의 DML은 술어가 아니라 자기 문장이다.
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF EXISTS(SELECT 1 FROM dbo.TA WITH(NOLOCK))
    BEGIN
        UPDATE B SET B.X = 1 FROM dbo.TB B WITH(NOLOCK)
    END
END";

    var facts = DmlScopeExtractor.ExtractLockHints(ddl);

    var ifFact = Assert.Single(facts, f => f.Operation == "IF");
    Assert.Equal(1, ifFact.StatementOrdinal);

    var update = Assert.Single(facts, f => f.Operation == "UPDATE");
    Assert.Equal(1, update.StatementOrdinal);
    Assert.Equal("최상위", update.Scope);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests.ExtractLockHints_SubqueryInsideDml_ShouldKeepStatementOrdinalAndMarkScope"`
Expected: FAIL — `Assert.Single(facts, f => f.Table == "PaymentDB.dbo.TCCanceledMst")`가 매칭 원소 없음으로 실패한다. `CollectFrom`은 `FromClause`만 훑으므로 WHERE 절 안의 참조는 잡히지 않는다.

- [ ] **Step 3: 최소 구현**

DML 문장의 `WhereClause` 안 하위 질의를 그 문장 번호로 훑는다. 세 `ExplicitVisit`이 `CollectFrom` 다음에 한 줄씩 더한다.

```csharp
            /// <summary>파생 테이블도 아니고 최상위 FROM도 아닌, 술어 안 하위 질의의 참조.</summary>
            private const string SubqueryScope = "하위 질의";

            /// <summary>
            /// DML 문장의 WHERE 안 하위 질의를 그 문장 번호로 훑는다.
            ///
            /// 범위를 `하위 질의`로 다는 이유는 `파생`과 같다 - 빼지 않고 표시해서 싣는다.
            /// 별도 문장 번호를 주지 않는 이유는 이 스캔이 이미 그 DML 문장의 일부라서,
            /// 새로 세면 같은 UPDATE가 두 번호로 나타나 다른 표와 대조할 수 없기 때문이다.
            /// </summary>
            private void CollectWhereSubqueries(string operation, int ordinal, WhereClause? where)
            {
                if (where?.SearchCondition == null) return;

                var collector = new SubqueryCollector();
                where.SearchCondition.Accept(collector);

                foreach (var query in collector.Queries)
                {
                    foreach (var spec in QuerySpecificationsOf(query))
                    {
                        if (spec.FromClause == null) continue;

                        var tables = new FromTableCollector();
                        foreach (var reference in spec.FromClause.TableReferences) reference.Accept(tables);
                        foreach (var (table, _) in tables.Tables) Add(operation, ordinal, table, SubqueryScope);
                    }
                }
            }
```

호출은 세 자리에 한 줄씩이다.

```csharp
            public override void ExplicitVisit(UpdateSpecification node)
            {
                var ordinal = NextOrdinal("UPDATE");
                CollectFrom("UPDATE", ordinal, node.FromClause);
                CollectWhereSubqueries("UPDATE", ordinal, node.WhereClause);
                RecordTargetHint("UPDATE", ordinal, node.Target);

                _dmlDepth++;
                base.ExplicitVisit(node);
                _dmlDepth--;
            }
```

`DELETE`도 같은 모양이다. `INSERT`는 원천 SELECT의 `WhereClause`를 쓴다.

```csharp
                if (node.InsertSource is SelectInsertSource select)
                {
                    foreach (var spec in QuerySpecificationsOf(select.Select))
                    {
                        CollectFrom("INSERT", ordinal, spec.FromClause);
                        CollectWhereSubqueries("INSERT", ordinal, spec.WhereClause);
                    }
                }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests"`
Expected: PASS.

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs
git commit -m "feat: 잠금 힌트 표가 DML 안 하위 질의의 스캔을 범위로 갈라 담는다"
```

---

### Task 4: `DmlScopeVisitor`가 같은 문장 집합을 담는다

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs` (`DmlScopeVisitor`, `DmlScopeFact`)
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`

**Interfaces:**
- Consumes: Task 1~2가 세운 `SELECT n` · `IF n` 채번 규칙(같은 판정을 이 방문자에도 그대로 적용한다).
- Produces: `DmlScopeFact.Operation`이 `"SELECT"`인 사실. 커서 원천의 `ORDER BY`·`GROUP BY`가 여기 실린다.

- [ ] **Step 1: 실패 테스트를 쓴다**

```csharp
[Fact]
public void Extract_CursorSourceSelect_ShouldCarryOrderByAndGroupBy()
{
    // PROC_ETC:62 실측 - 커서 원천의 ORDER BY가 문서 전체에 없었다.
    // 처리 순서가 MAX(ID)+1 채번 결과와 -3 중단 지점을 가른다(2026-08-22 축 A 재감사 🟡).
    const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    DECLARE Cur_SettlePost CURSOR FOR
    SELECT A.ClientID, A.YMD, A.OutYMD
    FROM   dbo.TSettleMst A WITH(NOLOCK)
    WHERE  A.YMD = @pi_strYMD
    GROUP BY A.ClientID, A.YMD, A.OutYMD
    ORDER BY A.OutYMD, A.ClientID
END";

    var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

    var fact = Assert.Single(facts);
    Assert.Equal("SELECT", fact.Operation);
    Assert.Equal(1, fact.StatementOrdinal);
    Assert.Equal(new[] { "A.OutYMD", "A.ClientID" }, fact.OrderByExpressions);
    Assert.Equal(new[] { "A.ClientID", "A.YMD", "A.OutYMD" }, fact.GroupByColumns);
}

[Fact]
public void Extract_StandaloneSelect_ShouldNotDisturbDmlOrdinals()
{
    const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    SELECT @v = MIN(ReqYMD) FROM dbo.TA WITH(NOLOCK)

    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A WHERE A.YMD = @pi_strYMD
END";

    var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

    var update = Assert.Single(facts, f => f.Operation == "UPDATE");
    Assert.Equal(1, update.StatementOrdinal);
    Assert.True(update.DateParameterApplied);

    var select = Assert.Single(facts, f => f.Operation == "SELECT");
    Assert.Equal(1, select.StatementOrdinal);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests.Extract_CursorSourceSelect_ShouldCarryOrderByAndGroupBy"`
Expected: FAIL — `Assert.Single(facts)`가 "The collection was empty"로 실패한다.

실패 이유가 `DmlScopeFact`의 프로퍼티 이름 불일치로 인한 컴파일 오류라면, 실제 정의(`DmlScopeExtractor.cs`의 `DmlScopeFact` 레코드)와 맞춘 뒤 다시 돌린다.

- [ ] **Step 3: 최소 구현**

`DmlScopeVisitor`에 Task 1과 **같은 판정**으로 `ExplicitVisit(SelectStatement)`을 더한다. 대상과 기준일 파라미터는 갱신 대상이 없으므로 비운다.

```csharp
            /// <summary>
            /// DML 밖의 독립 SELECT. LockHintVisitor의 같은 이름 오버라이드와 판정이
            /// 같아야 두 표의 문장 번호가 같은 것을 가리킨다 - FROM이 있는 것만,
            /// DML 안이 아닌 것만 센다.
            ///
            /// [왜 이 표에 싣는가 - 2026-08-22 축 A 재감사] 커서 원천 질의의 ORDER BY와
            /// GROUP BY를 담을 자리가 이 표의 기존 칸이다. 새 표를 만들지 않고 문장
            /// 집합만 넓히면 그 칸이 저절로 채워진다.
            /// </summary>
            public override void ExplicitVisit(SelectStatement node)
            {
                if (_dmlDepth == 0 && HasFromClause(node))
                {
                    RecordStandaloneSelect(node, NextOrdinal("SELECT"));
                }

                base.ExplicitVisit(node);
            }

            private void RecordStandaloneSelect(SelectStatement node, int ordinal)
            {
                var predicateColumns = new List<string>();
                var joinKeys = new List<string>();
                var groupByPerBranch = new List<List<string>>();

                foreach (var spec in QuerySpecificationsOf(node.QueryExpression))
                {
                    var top = new TopLevelPredicateCollector();
                    spec.WhereClause?.SearchCondition?.Accept(top);
                    predicateColumns.AddRange(top.Columns);
                    joinKeys.AddRange(top.JoinKeys);

                    // UNION 갈래마다 모아 뒀다가 ResolveGroupByColumns로 합친다 -
                    // 갈래마다 다르면 비운다(DmlScopeFact.GroupByColumns 제약 7).
                    groupByPerBranch.Add(CollectGroupByColumns(spec));
                }

                Facts.Add(new DmlScopeFact(
                    "SELECT",
                    node.StartLine,
                    string.Empty,
                    predicateColumns.Distinct().ToList(),
                    false,
                    joinKeys.Distinct().ToList(),
                    OrderByExpressionsOf(node.QueryExpression),
                    ResolveGroupByColumns(groupByPerBranch)));
            }
```

`OrderByExpressionsOf`는 지금 `InsertSource?`를 받는다(`DmlScopeExtractor.cs:776`). 독립 SELECT는 `QueryExpression`을 들고 있으므로 오버로드를 더하고 기존 것이 그 오버로드에 위임하게 한다 — 본문이 둘로 갈리지 않는다.

```csharp
            /// <summary>
            /// 질의식의 최상위 ORDER BY. UNION으로 묶인 원천의 ORDER BY는 어느 갈래에도
            /// 붙지 않고 UNION 노드 자신에 붙으므로(DmlScopeFact.OrderByExpressions의
            /// 프로브 실측 근거 참고), QueryExpression 그대로 받아 OrderByClause에 바로
            /// 접근한다.
            /// </summary>
            private static IReadOnlyList<string> OrderByExpressionsOf(QueryExpression? query)
            {
                var orderBy = query?.OrderByClause;
                if (orderBy == null) return Array.Empty<string>();

                return orderBy.OrderByElements
                    .Select(e => CollapseWhitespace(TextOf(e)))
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();
            }

            private static IReadOnlyList<string> OrderByExpressionsOf(InsertSource? source) =>
                OrderByExpressionsOf((source as SelectInsertSource)?.Select);
```

`DmlScopeFact`의 인자는 위치 기준이다 — `(Operation, Line, Target, PredicateColumns, DateParameterApplied, JoinKeys, OrderByExpressions, GroupByColumns?)`(`DmlScopeExtractor.cs:91`). `Target`은 빈 문자열로 두고 렌더러가 `—`로 낸다. `OrderByExpressions`가 빈 목록일 때 렌더러가 `—`와 `(없음)`을 가르는 것과 같은 분업이다. `DateParameterApplied`는 갱신 대상 범위를 정하는 칸이라 독립 SELECT에서는 항상 `false`이고, 역시 렌더러가 `—`로 낸다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests"`
Expected: PASS.

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs
git commit -m "feat: DML 범위 표가 커서 원천을 포함한 독립 SELECT를 담는다"
```

---

### Task 5: `SetPredicateFact`에 「술어 원문」을 더한다

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs` (`SetPredicateFact`, `SetPredicateVisitor.CollectFrom`)
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`

**Interfaces:**
- Produces: `SetPredicateFact.PredicateText` (`string`). Task 6·7·8이 소비한다.

- [ ] **Step 1: 실패 테스트를 쓴다**

```csharp
[Fact]
public void ExtractSetPredicates_DecomposableTerm_ShouldCarryBothDecompositionAndText()
{
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TSettleMst A
    WHERE  A.PGNAME IN ('KFTC', 'YELOPAY')
END";

    var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

    var fact = Assert.Single(facts);
    Assert.Equal("A.PGNAME", fact.Column);
    Assert.Equal("IN", fact.Operator);
    Assert.Equal(new[] { "'KFTC'", "'YELOPAY'" }, fact.Literals);
    Assert.Equal("A.PGNAME IN ('KFTC', 'YELOPAY')", fact.PredicateText);
}

[Fact]
public void ExtractSetPredicates_MultiLineTerm_ShouldCollapseWhitespaceInText()
{
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TSettleMst A
    WHERE  A.PGNAME IN ('KFTC',
                        'YELOPAY')
END";

    var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

    var fact = Assert.Single(facts);
    Assert.DoesNotContain("\n", fact.PredicateText);
    Assert.Equal("A.PGNAME IN ('KFTC', 'YELOPAY')", fact.PredicateText);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests.ExtractSetPredicates_DecomposableTerm_ShouldCarryBothDecompositionAndText"`
Expected: FAIL — 컴파일 오류 `'SetPredicateFact' does not contain a definition for 'PredicateText'`.

- [ ] **Step 3: 최소 구현**

레코드에 프로퍼티를 더한다. 기본값을 주어 기존 생성 지점이 깨지지 않게 한다.

```csharp
    public sealed record SetPredicateFact(
        string Operation,
        int Line,
        string Column,
        bool IsNegated,
        IReadOnlyList<string> Literals,
        int StatementOrdinal = 0,
        string Operator = "IN",
        string Scope = "최상위",
        string PredicateText = "");
```

`TopLevelPredicateCollector`가 항마다 그 항의 노드를 함께 내도록 하고, `CollectFrom`이 원문을 채운다. 컬렉터의 `SetPredicates` 튜플에 노드를 더한다.

```csharp
                foreach (var (column, op, literals, node) in top.SetPredicates)
                {
                    Facts.Add(new SetPredicateFact(
                        operation, node.StartLine, column,
                        op == "NOT IN", literals, ordinal, op, scope,
                        CollapseWhitespace(TextOf(node))));
                }
```

`Line`이 `statement.StartLine`에서 `node.StartLine`으로 바뀐다 — 스펙 §4 C가 요구한 변경이다. 같은 문장의 술어들이 서로 다른 줄로 찍혀 원문에서 찾을 수 있게 된다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests"`
Expected: PASS.

Run: `dotnet test`
Expected: 라인 칸을 문장 줄로 기대하던 기존 테스트가 있으면 여기서 실패한다. 실패하면 그 테스트의 기대값을 술어 자신의 줄로 고치고, 고친 이유를 테스트 주석에 남긴다(이 계획의 Task 5 · 스펙 §4 C를 인용). **기대값을 고치기 전에 실제 원문에서 그 술어의 줄 번호를 확인한다.**

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs
git commit -m "feat: 집합 술어 사실이 술어 원문과 자기 줄 번호를 진다"
```

---

### Task 6: 분해되지 않는 항도 행을 낸다

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs` (`TopLevelPredicateCollector`)
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`

**Interfaces:**
- Consumes: Task 5의 `PredicateText`.
- Produces: `Column`·`Operator`가 `"—"`이고 `Literals`가 빈 목록인 사실. Task 7의 렌더러와 Task 8의 L1이 이 모양을 안다.

- [ ] **Step 1: 실패 테스트를 쓴다**

```csharp
[Fact]
public void ExtractSetPredicates_OrCombinedTerm_ShouldProduceOneRowWithTextOnly()
{
    // EXCEPTION_PROC:220 실측 - 지금은 UseState <> 1과 UseState = 1 두 행이 나란히
    // 실려 AND로 읽히고, 그렇게 읽으면 모순(공집합)이다. A.YMD = A.AYMD 항은
    // 조인 키에서도 집합 술어에서도 빠져 두 그물 사이로 샜다(2026-08-22 축 A 재감사 🟠).
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TSettleMst A
    WHERE  (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))
END";

    var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

    var fact = Assert.Single(facts);
    Assert.Equal("—", fact.Column);
    Assert.Equal("—", fact.Operator);
    Assert.Empty(fact.Literals);
    Assert.Equal("(A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))", fact.PredicateText);
}

[Fact]
public void ExtractSetPredicates_InequalityTerm_ShouldProduceRow()
{
    // EXCEPTION_PROC:320 실측 - 수집 연산자가 =·<>·IN 셋뿐이라 >= 가 0행이었다.
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A
    WHERE  A.AYMD >= '20230101'
END";

    var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

    var fact = Assert.Single(facts);
    Assert.Equal("A.AYMD >= '20230101'", fact.PredicateText);
}

[Fact]
public void ExtractSetPredicates_ArithmeticRightHandSide_ShouldProduceRow()
{
    // EXCEPTION_PROC:442 실측 - 우변이 리터럴이 아니라 어떤 표에도 없었다.
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A
    WHERE  TxAmt != CardAmt+CouponAmt+MoneyAmt+PointAmt
END";

    var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

    var fact = Assert.Single(facts);
    Assert.Equal("TxAmt != CardAmt+CouponAmt+MoneyAmt+PointAmt", fact.PredicateText);
}

[Fact]
public void ExtractSetPredicates_MixedTerms_ShouldKeepDecomposableOnesDecomposed()
{
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A
    WHERE  A.PGNAME IN ('KFTC', 'YELOPAY')
    AND    (A.UseState <> 1 OR A.YMD = A.AYMD)
END";

    var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

    Assert.Equal(2, facts.Count);

    var decomposed = Assert.Single(facts, f => f.Column == "A.PGNAME");
    Assert.Equal(new[] { "'KFTC'", "'YELOPAY'" }, decomposed.Literals);

    var textOnly = Assert.Single(facts, f => f.Column == "—");
    Assert.Equal("(A.UseState <> 1 OR A.YMD = A.AYMD)", textOnly.PredicateText);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests.ExtractSetPredicates_OrCombinedTerm_ShouldProduceOneRowWithTextOnly"`
Expected: FAIL — 두 행이 나오므로 `Assert.Single(facts)`가 "The collection contained 2 matching elements"로 실패한다. `ExtractSetPredicates_InequalityTerm_ShouldProduceRow`는 "The collection was empty"로 실패한다.

- [ ] **Step 3: 최소 구현**

`TopLevelPredicateCollector`가 최상위 `AND` 항을 평탄화한 뒤, 항마다 분해를 시도하고 실패하면 원문 전용 사실을 낸다.

```csharp
            /// <summary>분해되지 않는 항의 컬럼·연산 칸에 쓰는 표기.</summary>
            internal const string NotDecomposed = "—";

            /// <summary>
            /// 최상위 AND 항을 평탄화한다. OR로 묶인 것은 통째로 한 항이다 - 안으로
            /// 내려가면 갈래마다의 조건이 AND처럼 나란히 실려 모순으로 읽힌다
            /// (2026-08-22 축 A 재감사 실측: EXCEPTION_PROC:220이 정확히 그 모양이었다).
            /// </summary>
            private static IEnumerable<BooleanExpression> TopLevelAndTerms(BooleanExpression? node)
            {
                if (node == null) yield break;

                if (node is BooleanBinaryExpression binary
                    && binary.BinaryExpressionType == BooleanBinaryExpressionType.And)
                {
                    foreach (var term in TopLevelAndTerms(binary.FirstExpression)) yield return term;
                    foreach (var term in TopLevelAndTerms(binary.SecondExpression)) yield return term;
                    yield break;
                }

                if (node is BooleanParenthesisExpression paren
                    && paren.Expression is BooleanBinaryExpression inner
                    && inner.BinaryExpressionType == BooleanBinaryExpressionType.And)
                {
                    foreach (var term in TopLevelAndTerms(paren.Expression)) yield return term;
                    yield break;
                }

                yield return node;
            }
```

항 하나를 분해하는 기존 로직(`InPredicate`·`BooleanComparisonExpression` 처리)을 `TryDecompose`로 감싸고, 실패하면 원문 전용 사실을 낸다. 분해 성공 판정은 **우변이 전부 리터럴일 때**로 좁힌다 — `A.YMD = A.AYMD`는 여기서 실패해 원문 전용이 된다.

```csharp
            public void CollectTerms(BooleanExpression? searchCondition)
            {
                foreach (var term in TopLevelAndTerms(searchCondition))
                {
                    if (TryDecompose(term, out var column, out var op, out var literals))
                    {
                        SetPredicates.Add((column, op, literals, term));
                        continue;
                    }

                    SetPredicates.Add((NotDecomposed, NotDecomposed, Array.Empty<string>(), term));
                }
            }
```

`Columns`·`Parameters`·`JoinKeys` 수집은 지금 하던 대로 전체 식을 훑어 계속 모은다 — DML 범위 표의 술어 컬럼 칸과 조인 키 칸이 이 작업으로 좁아져서는 안 된다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests"`
Expected: PASS.

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0. 기존 집합 술어 테스트가 행 수를 기대하던 자리에서 실패하면, 원문 전용 행이 새로 늘어난 것인지 기존 분해 행이 사라진 것인지 **먼저 가른다.** 후자면 회귀이므로 구현을 고친다. 전자면 기대값을 고치고 이유를 주석에 남긴다.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs
git commit -m "feat: 집합 술어 표가 OR 결합과 부등식 항을 원문으로 담는다"
```

---

### Task 7: 렌더러와 L1을 새 모양에 함께 맞춘다

렌더러와 L1은 짝이다 — 한쪽만 바꾸면 모델이 표를 원문 그대로 옮겨도 L1이 틀렸다고 하는 실패 모양이 된다(`ExtractSetPredicateLiteralCell` 문서의 실측 근거). 그래서 한 Task로 묶는다.

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs` (`BuildSetPredicateTableLines`, `BuildDmlScopeTableLines`)
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`CheckSetPredicates`, `ExtractSetPredicateLiteralCell`; `CheckLockHints`는 확인만 하고 필요할 때만 고친다)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: Task 4의 `DmlScopeFact("SELECT", …)`, Task 5·6의 `SetPredicateFact.PredicateText`.
- Produces: 집합 술어 표의 8열 모양(`문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문`). Task 9(재생성 확인)가 이 모양을 grep한다.
- 기존 `ErrorType.SetPredicateMismatch`를 그대로 쓴다. **새 enum 값을 더하지 않는다** — ①②와의 충돌면을 줄인다.

- [ ] **Step 1: 실패 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`에 추가한다. 마크다운은 Task 7이 렌더할 모양을 손으로 적은 것이다 — 이 저장소가 렌더 계약을 테스트하는 방식이다(`MechanicalValidatorTests.cs:3359`의 선례).

```csharp
[Fact]
public void CheckSetPredicates_LiteralCellShiftedByNewColumn_ShouldStillCompare()
{
    // 「술어 원문」이 마지막 열이 되면서 리터럴 칸이 뒤에서 세 번째가 됐다.
    // 파서가 인덱스를 안 고치면 원문 칸을 리터럴로 읽어 옳은 표를 틀렸다고 한다 -
    // §0이 막으려는 "모델이 옳게 옮겨도 L1이 틀렸다고 하는" 실패 모양이다.
    var expectations = new SpecExpectations
    {
        SetPredicates = new[]
        {
            new SetPredicateFact(
                "UPDATE", 130, "A.PGNAME", false, new[] { "'KFTC'", "'YELOPAY'" }, 4, "IN", "최상위",
                "A.PGNAME IN ('KFTC', 'YELOPAY')")
        }
    };

    const string markdown = @"## CRUD 분석

### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| UPDATE 4 | 130 | A.PGNAME | IN | 최상위 | 2 | 'KFTC', 'YELOPAY' | A.PGNAME IN ('KFTC', 'YELOPAY') |
";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.DoesNotContain(
        result.DetailedErrors,
        e => e.Type == ErrorType.SetPredicateMismatch);
}

[Fact]
public void CheckSetPredicates_PredicateTextSummarized_ShouldReport()
{
    // 분해되지 않은 항은 원문 칸이 유일한 기록처다. 요약해 옮기면 그 필터가 사라진다.
    var expectations = new SpecExpectations
    {
        SetPredicates = new[]
        {
            new SetPredicateFact(
                "UPDATE", 220, "—", false, Array.Empty<string>(), 7, "—", "최상위",
                "(A.UseState <> 1 OR A.YMD = A.AYMD)")
        }
    };

    const string markdown = @"## CRUD 분석

### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| UPDATE 7 | 220 | — | — | 최상위 | — | — | 당일 이전 취소건 제외 |
";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.Contains(
        result.DetailedErrors,
        e => e.Type == ErrorType.SetPredicateMismatch);
}

[Fact]
public void CheckSetPredicates_UndecomposedRowCopiedVerbatim_ShouldPass()
{
    var expectations = new SpecExpectations
    {
        SetPredicates = new[]
        {
            new SetPredicateFact(
                "UPDATE", 220, "—", false, Array.Empty<string>(), 7, "—", "최상위",
                "(A.UseState <> 1 OR A.YMD = A.AYMD)")
        }
    };

    const string markdown = @"## CRUD 분석

### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| UPDATE 7 | 220 | — | — | 최상위 | — | — | (A.UseState <> 1 OR A.YMD = A.AYMD) |
";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.DoesNotContain(
        result.DetailedErrors,
        e => e.Type == ErrorType.SetPredicateMismatch);
}
```

`CheckLockHints`도 새 문장 종류를 그대로 흘려보내는지 확인한다. 스펙 §4 E가 "변경이 없을 것으로 보이나 테스트로 확인한다"고 남긴 자리다 — 확인 없이 넘기면 표만 넓어지고 검사는 침묵하는 상태가 된다.

```csharp
[Fact]
public void CheckLockHints_SelectAndIfRows_ShouldCompareLikeDmlRows()
{
    // 잠금 힌트 표는 이미 행 단위 대조라 새 문장 종류가 그대로 흘러가야 한다.
    var expectations = new SpecExpectations
    {
        LockHints = new[]
        {
            new LockHintFact("SELECT", 1, 22, "PaymentDB.dbo.TExtraSettleIn", "-", "최상위", new[] { "NOLOCK" }),
            new LockHintFact("IF", 1, 31, "TSettleMst", "-", "최상위", new[] { "NOLOCK" }),
            new LockHintFact("UPDATE", 12, 529, "TSettleMst", "-", "하위 질의", new[] { "NOLOCK" })
        }
    };

    const string markdown = @"## CRUD 분석

### 잠금 힌트 (기계 확정 — 수정 금지)

| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| SELECT 1 | 22 | PaymentDB.dbo.TExtraSettleIn | - | 최상위 | NOLOCK |
| IF 1 | 31 | TSettleMst | - | 최상위 | NOLOCK |
| UPDATE 12 | 529 | TSettleMst | - | 하위 질의 | NOLOCK |
";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.DoesNotContain(
        result.DetailedErrors,
        e => e.Type == ErrorType.LockHintTableMissing);
}

[Fact]
public void CheckLockHints_DroppedSelectRow_ShouldReport()
{
    var expectations = new SpecExpectations
    {
        LockHints = new[]
        {
            new LockHintFact("SELECT", 1, 22, "PaymentDB.dbo.TExtraSettleIn", "-", "최상위", new[] { "NOLOCK" })
        }
    };

    // 표는 있으나 SELECT 행이 빠졌다.
    const string markdown = @"## CRUD 분석

### 잠금 힌트 (기계 확정 — 수정 금지)

| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |
| :--- | :--- | :--- | :--- | :--- | :--- |
";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.NotEmpty(result.DetailedErrors);
}
```

`CheckLockHints_DroppedSelectRow_ShouldReport`가 Step 2에서 **통과**하면 그것이 "이미 행 단위 대조라 손댈 것이 없다"는 주장의 확인이다. 실패하면 그때 `CheckLockHints`를 고친다 — 스펙이 남긴 미확정이 여기서 닫힌다.

`SpecExpectations`의 다른 필수 속성이 있어 위 초기화가 컴파일되지 않으면, 같은 파일의 기존 테스트가 `SpecExpectations`를 만드는 방식(예: `ExpectClvtAndPgvt()` 같은 헬퍼, `MechanicalValidatorTests.cs:836`)을 그대로 따른다. `LockHintFact`의 인자는 위치 기준이다 — `(Operation, StatementOrdinal, Line, Table, Alias, Scope, Hints)`(`DmlScopeExtractor.cs:219`).

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests.CheckSetPredicates_LiteralCellShiftedByNewColumn_ShouldStillCompare"`

Expected: FAIL — 리터럴 칸 자리에서 원문 칸을 읽어 원소 집합이 어긋난다고 보고한다(`SetPredicateMismatch`가 나온다).

Run: `dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests.CheckSetPredicates_PredicateTextSummarized_ShouldReport"`

Expected: FAIL — 원문 칸을 대조하지 않으므로 요약해 옮겨도 통과한다(`Assert.Contains`가 실패한다).

- [ ] **Step 3: 렌더러를 고친다**

`AiService.BuildSetPredicateTableLines`의 헤더와 행을 8열로 바꾼다. 분해되지 않은 사실은 원소 수·리터럴 칸을 `—`로 낸다.

```csharp
            var lines = new List<string>
            {
                "   [CRITICAL SET PREDICATE TABLE] The following set predicates are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## CRUD 분석` under the exact heading shown. Do NOT drop, add, abbreviate, or summarize any literal - the membership of each set is what determines the target rows, and it cannot be inferred from the column name. The 범위 column says where the predicate sits - `최상위` is the statement's own WHERE, `파생 테이블 X` is the WHERE inside that derived table. A predicate inside a derived table narrows the target rows just as much as a top-level one, so it must be described as a filter, never softened into `조회합니다`. The 술어 원문 column carries the term exactly as written in the DDL. When the other columns hold `—` the term could not be decomposed into a column and a literal set (an OR-combined condition, a column-to-column comparison, an arithmetic right-hand side), and 술어 원문 is then the ONLY record of that filter - copy it verbatim and describe the filter from it. Never omit such a row because it looks unlike the others.",
                $"   {DmlScopeExtractor.SetPredicateTableHeading}",
                "   | 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |",
                "   | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |"
            };

            foreach (var fact in setPredicates)
            {
                var decomposed = fact.Literals.Count > 0;
                var literals = decomposed ? string.Join(", ", fact.Literals) : "—";
                var count = decomposed ? fact.Literals.Count.ToString() : "—";

                lines.Add(
                    $"   | {fact.Operation} {fact.StatementOrdinal} | {fact.Line} | "
                    + $"{EscapeTableCell(fact.Column)} | {EscapeTableCell(fact.Operator)} | "
                    + $"{EscapeTableCell(fact.Scope)} | {count} | {EscapeTableCell(literals)} | "
                    + $"{EscapeTableCell(fact.PredicateText)} |");
            }
```

`BuildDmlScopeTableLines`는 `SELECT` 행에서 대상과 기준일 칸을 `—`로 낸다.

```csharp
                var isStandaloneSelect = fact.Operation == "SELECT";
                var target = isStandaloneSelect ? "—" : EscapeTableCell(fact.Target);
```

기준일 칸의 기존 문구는 한 글자도 바꾸지 않는다. 고치기 전에 그 자리의 현재 문자열을 읽는다.

```bash
grep -n "최상위 기준 · 하위 질의는 별도 확인" src/ReSet.Core/Services/AiService.cs
```

읽은 문자열을 그대로 쓰고 `SELECT` 행만 `—`로 가른다.

- [ ] **Step 4: L1을 고친다**

리터럴 칸 인덱스를 하나 앞으로 옮긴다. `MarkdownTableCellCodec.SplitRow("| a | b |")`는 `["", "a", "b", ""]`를 낸다(앞뒤 빈 조각 포함, 구현 확인함). 행이 `|`로 끝나면 마지막 조각은 빈 문자열이고 그 앞이 「술어 원문」, 그 앞이 리터럴 목록이다.

```csharp
        private static HashSet<string> ExtractSetPredicateLiteralCell(string row)
        {
            var cellsOfRow = SplitTableRowCells(row);

            // [칸 인덱스가 하나 밀린 이유 - 2026-08-22 축 A 재감사] 「술어 원문」이 마지막
            // 열로 들어왔다. 행이 `|`로 끝나면 마지막 조각은 빈 문자열이고, 그 앞이 원문
            // 칸이며, 리터럴 목록은 그 하나 앞이다. 인덱스를 안 고치면 원문 칸을 리터럴로
            // 읽어 옳게 옮긴 표를 틀렸다고 한다.
            var trailingBlank = cellsOfRow.Count > 0 && cellsOfRow[cellsOfRow.Count - 1].Length == 0 ? 1 : 0;
            var literalIndex = cellsOfRow.Count - trailingBlank - 2;
            var literalCell = literalIndex >= 0 ? cellsOfRow[literalIndex] : string.Empty;

            return TokenizeLiteralCell(literalCell).ToHashSet(StringComparer.Ordinal);
        }
```

그룹 키와 행 매칭에 원문을 더한다.

```csharp
            var groups = expectations.SetPredicates
                .GroupBy(f => (
                    Operation: f.Operation.ToUpperInvariant(),
                    f.Line,
                    Column: f.Column.ToUpperInvariant(),
                    Scope: f.Scope.ToUpperInvariant(),
                    PredicateText: FoldNewlinesLikeRenderedCell(f.PredicateText)));
```

```csharp
                var displayPredicateText = FoldNewlinesLikeRenderedCell(facts[0].PredicateText);

                var matchingRows = rowLines.Where(r =>
                {
                    var cells = MarkdownTableCellCodec.SplitRow(r);
                    return cells.Any(c => c == lineToken)
                        && cells.Any(c => string.Equals(c, displayColumn, StringComparison.OrdinalIgnoreCase))
                        && cells.Any(c => string.Equals(c, displayScope, StringComparison.OrdinalIgnoreCase))
                        && cells.Any(c => string.Equals(c, displayPredicateText, StringComparison.Ordinal));
                }).ToList();
```

분해되지 않은 사실은 `Literals`가 비어 있어 기존 다중집합 비교가 빈 집합끼리 맞춰지므로, 그 갈래는 손대지 않아도 통과한다. 행 수 불일치 메시지에 원문을 실어 어느 술어인지 보이게 한다.

```csharp
                    var countMessage =
                        $"집합 술어 표에서 원본 DDL 라인 {line} 술어 `{displayPredicateText}` "
                        + $"키를 가진 사실이 {facts.Count}개인데 행은 {matchingRows.Count}개 있습니다. 「술어 원문」 "
                        + "칸은 DDL 원문 그대로여야 합니다 - 요약하거나 바꿔 쓸 수 없고, 행을 합치거나 생략할 수 "
                        + "없으며, 범위(최상위 / 파생 테이블 / 하위 질의)도 사실대로 적어야 합니다.";
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests"`
Expected: 새 테스트 셋을 포함해 전부 PASS.

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0. 기존 집합 술어 L1 테스트가 7열 마크다운을 쓰고 있으면 여기서 실패한다 — 그 마크다운에 「술어 원문」 열을 더하고, 기대값에도 `PredicateText`를 채운다. **열만 더하고 나머지 칸의 값은 바꾸지 않는다.**

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/AiService.cs src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: 집합 술어 표에 술어 원문 열을 싣고 L1이 그 칸을 대조한다"
```

---

### Task 8: 캐시 버전 11과 문서 반영

**Files:**
- Modify: `src/ReSet.Core/Services/CacheManager.cs`
- Modify: `docs/architecture.md`
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: Task 1~7 전부.

- [ ] **Step 1: 현재 버전을 확인한다**

```bash
grep -n "CurrentCacheFormatVersion" src/ReSet.Core/Services/CacheManager.cs
```

Expected: ①②가 병합됐다면 `= 10`이다. **`= 9`가 나오면 ①②가 아직 병합되지 않은 것이므로 여기서 멈추고 확인한다** — Global Constraints의 순서 제약이 지켜지지 않았다는 신호다.

- [ ] **Step 2: 버전을 올리고 주석을 더한다**

`10`을 `11`로 바꾸고, 10번 주석 바로 아래에 기존 양식대로 더한다.

```csharp
        // 11: 집합 술어 표에 「술어 원문」 열이 생기고 행 단위가 최상위 AND 항으로 올라갔다.
        //     잠금 힌트·DML 범위 표가 DML 밖 독립 SELECT(`SELECT n`)와 제어 흐름 술어
        //     (`IF n`), DML 안 하위 질의(범위 `하위 질의`)를 담는다. 프롬프트에 실리는
        //     표가 바뀌므로 옛 엔트리를 재사용하면 틀린 재료로 만든 산출물이 남고
        //     넓힌 L1도 캐시 히트에서는 발동하지 않는다.
```

- [ ] **Step 3: 문서를 동기화한다**

`docs/architecture.md`와 `AGENTS.md`에서 집합 술어 표와 잠금 힌트 표를 설명하는 자리를 찾는다.

```bash
grep -n "집합 술어\|잠금 힌트" docs/architecture.md AGENTS.md
```

찾은 자리마다 이 작업이 바꾼 것을 반영한다 — 집합 술어 표의 열이 여덟이고 행 단위가 최상위 AND 항이라는 것, 잠금 힌트·DML 범위 표의 문장 칸에 `SELECT n`·`IF n`이 올 수 있다는 것, 범위 칸의 값이 셋(`최상위`·`파생`·`하위 질의`)이라는 것. **표의 개수는 바뀌지 않았으므로 표 목록은 건드리지 않는다.**

- [ ] **Step 4: 전체 테스트**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/CacheManager.cs docs/architecture.md AGENTS.md
git commit -m "chore: 캐시 버전 11과 표 설명을 동기화한다"
```

---

### Task 9: 재생성으로 실제로 닫혔는지 확인한다

**Files:**
- 없음(확인만 한다). 결함이 남으면 그 자리를 고치는 별도 커밋을 낸다.

**Interfaces:**
- Consumes: Task 1~8의 결과 전부.

- [ ] **Step 1: 대상 객체를 재생성한다**

```bash
dotnet run --project src/ReSet.Cli -- --sp UP_UTIL_SETTLE_EXCEPTION_PROC,UP_UTIL_SETTLE_COMM_UPD,UP_UTIL_SETTLE_PROC_ETC,UP_UTIL_SETTLE_INS_EXTRA,UP_Util_PG_Client_CMRate_Ins < /dev/null 2>&1 | tail -30
```

함수 쪽도 돌린다.

```bash
dotnet run --project src/ReSet.Cli -- --function UIF_SettleYMD < /dev/null 2>&1 | tail -30
```

CLI 인자 이름이 다르면 `src/ReSet.Cli/CliArgs.cs`에서 확인해 맞춘다.

- [ ] **Step 2: 11건의 앵커를 대조한다**

| 확인 | 명령 | 기대 |
|---|---|---|
| OR 결합이 한 행에 원문으로 | `grep -n "UseState <> 1 OR" output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md` | 히트 |
| `>=` 항이 실린다 | `grep -c "20230101" output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md` | 1 이상 |
| `!=` 산술 항이 실린다 | `grep -n "TxAmt != CardAmt" output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md` | 히트 |
| 하위 질의 범위 | `grep -n "하위 질의" output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md` | 히트 |
| 커서 정렬 | `grep -n "A.OutYMD, A.ClientID" output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/docs/Spec.md` | 히트 |
| 함수에 잠금 힌트 표 | `grep -n "잠금 힌트" output/Functions/dbo.UIF_SettleYMD/docs/Spec.md` | 히트 |
| 힌트 없는 스캔 구분 | `grep -n "SPT_VALUES" output/Functions/dbo.UIF_SettleYMD/docs/Spec.md` | 힌트 칸이 `(없음)` |
| 제어 흐름 술어 | `grep -n "IF 1" output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/docs/Spec.md` | 히트 |

- [ ] **Step 3: 채번이 밀리지 않았는지 확인한다**

재생성 전 산출물을 백업해 두고 DML 문장 번호를 비교한다.

```bash
diff <(grep -oE "^\| (UPDATE|INSERT|DELETE) [0-9]+" output.bak-2026-08-22/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md | sort -u) \
     <(grep -oE "^\| (UPDATE|INSERT|DELETE) [0-9]+" output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md | sort -u)
```

Expected: 차이 없음. 차이가 있으면 Global Constraints의 "기존 DML 문장 번호는 변하지 않아야 한다"가 깨진 것이므로 원인을 찾아 고친다.

- [ ] **Step 4: L1이 조용한지 확인한다**

재생성 로그에 `SetPredicateMismatch`나 `LockHintTableMissing`이 반복해 나오면, 표를 넓힌 쪽과 검사를 넓힌 쪽이 어긋난 것이다. 로그를 근거로 어느 쪽이 틀렸는지 가른 뒤 고친다.

- [ ] **Step 5: 결과를 기록한다**

닫힌 결함과 남은 결함을 `docs/audit-defect-catalog.md`(또는 그 자리의 실제 카탈로그 파일)에 반영하고 커밋한다.

```bash
git add docs/
git commit -m "docs: 축 A 재감사 ③ 범위 확대분의 재생성 확인 결과를 기록한다"
```

---

## 완료 기준

- Task 1~8의 커밋 여덟 개가 있고, 마지막 커밋 시점에 `dotnet test`가 실패 0 · 건너뜀 0이다.
- Task 9의 확인 여덟 개가 전부 기대대로다.
- 기존 DML 문장 번호가 재생성 전후로 같다.
- 닫힌 결함: 🟠 5건(집합 술어) · 🟡 6건(잠금 힌트 5 · 커서 정렬 1).

## 이 계획이 닫지 않는 것

스펙 §8 그대로다. ③ (b) 새 재료 4건(🔴 2 포함), 하위 질의 **안의 술어**, `MERGE`, 그리고 ④·⑤는 각각 별도 사이클이다.
