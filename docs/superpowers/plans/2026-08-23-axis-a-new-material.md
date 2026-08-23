# 축 A ③(b)와 계획 밖 결함 — 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 방문 범위가 좁아 표에 오지 못하던 사실 5건(🔴 1 · 🟡 4)을 기존 표 넷과 실행 의미 표의 종류 둘로 닫는다.

**Architecture:** 네 방문자 중 둘(`SetPredicateVisitor`·`ReferencedFunctionVisitor`)이 아직 DML 셋만 방문한다. ③(a)가 나머지 둘에 한 것과 같은 규칙을 더해 네 표의 문장 집합을 통일한다. 잠금 힌트는 문장 집합이 이미 넓으므로 FROM 절 바깥 하위 질의 세 모양만 더 훑는다. 🔴은 실행 의미 표에 종류 둘(`루프 내 재설정`·`비집계 대입`)을 더해 닫는다. **새 표는 만들지 않는다.**

**Tech Stack:** C# / .NET 10, Microsoft.SqlServer.TransactSql.ScriptDom (`TSql160Parser`), xUnit.

**Spec:** `docs/superpowers/specs/2026-08-23-axis-a-new-material-design.md`

## Global Constraints

- **STEP ZERO, 모든 작업:** 워크트리에 `output/` 코퍼스가 없다. 코퍼스를 읽는 테스트는 **없으면 조용히 통과한다.** 첫 테스트 실행 전에 걸어라:
  ```bash
  ln -s /Users/payletter/git-root/ReSet/output output
  ls output/Objects | head -3
  git status --short   # 여전히 clean이어야 한다 ("output"이 .git/info/exclude에 있다)
  ```
  보고하는 모든 테스트 실행은 코퍼스가 있는 상태여야 한다.
- **기존 문장 번호는 변하지 않아야 한다.** DML·`SELECT n`·`IF n` 전부. 네 방문자는 서로를 참조하지 않고도 같은 번호를 내는 계약이다.
- **새 기계 확정 표를 만들지 않는다.** `MachineConfirmedTables.All`은 한 줄도 바뀌지 않는다.
- **실행 의미 종류는 `AllKinds` 끝에 붙인다.** `ExecutionSemanticsFacts.AllKinds`의 주석이 못 박는다 — Critic 면제 블록이 이 순서를 그대로 열거하므로 순서를 흔들면 프롬프트 접두사 캐시가 깨진다. `MachineConfirmedTablesTests.EveryExecutionSemanticKindConstant_IsListedInAllKinds`가 강제한다.
- **소프트 페일 규약.** 파싱 실패는 예외를 던지지 않고 빈 목록으로 진행한다(AGENTS.md 범주 2). 기존 `Extract*` 진입점의 `try`/`catch` 구조를 그대로 둔다.
- **베이스라인: 2359 통과 · 실패 0 · 건너뜀 0** (코퍼스 있는 상태).
- 각 Task는 실패 테스트 → 실패 확인 → 최소 구현 → 통과 확인 → 커밋의 한 사이클이다.

## 앞 브랜치가 배운 것 — 그대로 적용한다

- **부재를 주장하기 전에 grep하라.** 앞 브랜치에서 자신 있는 주장 열 건이 검증에 실패했고, 전부 누군가 산출물을 열어봐서 잡혔다.
- **격리는 주석이 아니라 코드로 고정하라.** "재료가 이것 하나뿐"이라고 적는 대신, 그 하나를 뺀 쌍둥이가 `null`을 내는지 단언하라.
- **변이 증명은 그 단언을 격리해야 한다.** 앞 라운드의 증명 하나는 *앞선* 단언이 먼저 터져 아무것도 증명하지 못했다.
- **표의 내용을 서술하기 전에 렌더러의 헤더 줄을 먼저 읽어라.** `AGENTS.md`에 규칙으로 들어가 있다.
- **계획의 코드가 낡았을 수 있다.** 앞 브랜치에서 다섯 곳이 낡아 있었다. 코드와 계획이 어긋나면 코드가 이긴다 — 그리고 어긋난 자리를 보고하라.

---

### Task 1: `ReferencedFunctionVisitor`가 독립 SELECT와 `IF` 술어를 방문한다

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs` (`ReferencedFunctionVisitor`, 1662행 근처)
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`

**Interfaces:**
- Consumes: `HasFromClause(SelectStatement)` — 이미 `DmlScopeExtractor`의 파일 수준 `private static`이다. 새로 만들지 말고 그대로 부른다.
- Produces: `ReferencedFunctionCallFact.Operation`이 `"SELECT"`·`"IF"`인 사실. Task 2가 같은 채번 규칙을 쓴다.

- [ ] **Step 1: 실패 테스트를 쓴다**

```csharp
[Fact]
public void ExtractFunctionCalls_CallInsideStandaloneSelectList_ShouldBeCollected()
{
    // COLLECTYMD:53 실측 - 변수 대입 SELECT의 SELECT 목록 안 CASE 식에서 함수를 부른다.
    // 이 호출이 수집되지 않아 참조 함수 표가 아예 생기지 않았고, 표가 없으니 링크도
    // 없어 모델이 산문으로 요약했다 - 그 요약에서 간격 0 특례가 빠진 것이 🔴이다.
    const string ddl = @"
CREATE FUNCTION dbo.F(@pi_strYMD VARCHAR(8)) RETURNS VARCHAR(8)
AS
BEGIN
    DECLARE @v VARCHAR(8)
    SELECT @v = CASE WHEN CollectType = 2
                     THEN dbo.UF_GET_WORKDAY2(@pi_strYMD, CollectDay)
                     ELSE CONVERT(VARCHAR(8), @pi_strYMD, 112)
                END
    FROM   dbo.TPGCollectPeriodMst WITH(NOLOCK)
    WHERE  CollectFlag = 1
    RETURN @v
END";

    var facts = DmlScopeExtractor.ExtractFunctionCalls(ddl, new[] { "UF_GET_WORKDAY2" });

    var fact = Assert.Single(facts);
    Assert.Equal("SELECT", fact.Operation);
    Assert.Equal(1, fact.StatementOrdinal);
    Assert.Equal("dbo.UF_GET_WORKDAY2", fact.QualifiedName);
    Assert.Contains("UF_GET_WORKDAY2", fact.CallExpression);
}

[Fact]
public void ExtractFunctionCalls_CallInsideIfPredicate_ShouldBeNumberedAsIf()
{
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF dbo.UF_GET_ROUND4VAT(100) > 0
    BEGIN
        RETURN
    END
END";

    var facts = DmlScopeExtractor.ExtractFunctionCalls(ddl, new[] { "UF_GET_ROUND4VAT" });

    var fact = Assert.Single(facts);
    Assert.Equal("IF", fact.Operation);
    Assert.Equal(1, fact.StatementOrdinal);
}

[Fact]
public void ExtractFunctionCalls_StandaloneSelectWithoutFrom_ShouldNotConsumeAnOrdinal()
{
    // FROM이 없는 대입은 스캔할 자리가 없다 - 잠금 힌트·DML 범위와 같은 판정
    // (HasFromClause)을 써야 네 표의 SELECT 번호가 같은 문장을 가리킨다.
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @a = dbo.UF_GET_ROUND4VAT(1)

    SELECT @b = dbo.UF_GET_ROUND4VAT(2) FROM dbo.TA WITH(NOLOCK)
END";

    var facts = DmlScopeExtractor.ExtractFunctionCalls(ddl, new[] { "UF_GET_ROUND4VAT" });

    var fact = Assert.Single(facts);
    Assert.Equal("SELECT", fact.Operation);
    Assert.Equal(1, fact.StatementOrdinal);
    Assert.Contains("2", fact.CallExpression);
}

[Fact]
public void ExtractFunctionCalls_DmlOrdinals_ShouldNotShift()
{
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @v = dbo.UF_GET_ROUND4VAT(1) FROM dbo.TA WITH(NOLOCK)

    UPDATE A SET A.X = dbo.UF_GET_ROUND4VAT(2) FROM dbo.TB A
END";

    var facts = DmlScopeExtractor.ExtractFunctionCalls(ddl, new[] { "UF_GET_ROUND4VAT" });

    var update = Assert.Single(facts, f => f.Operation == "UPDATE");
    Assert.Equal(1, update.StatementOrdinal);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests.ExtractFunctionCalls_CallInsideStandaloneSelectList_ShouldBeCollected"`

Expected: FAIL — `Assert.Single(facts)`가 "The collection was empty"로 실패한다. 방문자가 `UpdateSpecification`·`DeleteSpecification`·`InsertSpecification`만 오버라이드하므로 독립 SELECT는 방문되지 않는다.

`ExtractFunctionCalls_DmlOrdinals_ShouldNotShift`는 이 시점에 **통과**한다 — 그것이 회귀 가드라는 확인이다.

- [ ] **Step 3: 최소 구현**

`Visit` 셋을 `ExplicitVisit`으로 바꾸고 둘을 더한다. `Collect`가 `statement.Accept(calls)`로 문장 전체를 훑으므로, 방문 대상만 넓히면 SELECT 목록 안 `CASE` 식의 호출이 자동으로 잡힌다.

```csharp
            public override void ExplicitVisit(UpdateSpecification node)
            {
                Collect("UPDATE", node, NextOrdinal("UPDATE"));
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(DeleteSpecification node)
            {
                Collect("DELETE", node, NextOrdinal("DELETE"));
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(InsertSpecification node)
            {
                Collect("INSERT", node, NextOrdinal("INSERT"));
                base.ExplicitVisit(node);
            }

            /// <summary>
            /// DML 밖의 독립 SELECT. 변수 대입 SELECT · 커서 원천 · 함수 본문이 전부 이 노드로 온다.
            ///
            /// [왜 넓히는가 - 2026-08-23 축 A ③(b)] COLLECTYMD:53·78이 변수 대입 SELECT의
            /// SELECT 목록 안 CASE 식에서 UF_GET_WORKDAY2를 부른다. 이 호출이 수집되지 않아
            /// 참조 함수 표가 아예 생기지 않았고, 표가 없으니 링크도 없어 모델이 산문으로
            /// 요약했다 - 그 요약에서 간격 0 특례가 빠진 것이 🔴이다. 링크만 걸렸으면
            /// 결함이 없었다(UF_GET_WORKDAY2 자신의 명세서에는 그 사실이 정확히 있다).
            ///
            /// [FROM이 없으면 세지 않는 이유] 네 표가 같은 문장을 같은 번호로 가리켜야
            /// 하므로 판정을 공유한다 - HasFromClause는 DmlScopeExtractor의 파일 수준
            /// 헬퍼이고 LockHintVisitor·DmlScopeVisitor가 이미 그것을 부른다.
            /// </summary>
            public override void ExplicitVisit(SelectStatement node)
            {
                if (HasFromClause(node))
                {
                    Collect("SELECT", node, NextOrdinal("SELECT"));
                }

                base.ExplicitVisit(node);
            }

            /// <summary>
            /// 제어 흐름 술어 안의 함수 호출. 술어만 훑고 본문은 자식 순회에 맡긴다 -
            /// IF 본문의 DML은 자기 문장이고 자기 번호를 받아야 한다.
            /// 스캔이 아니라 호출을 세므로 FROM 유무를 묻지 않는다.
            /// </summary>
            public override void ExplicitVisit(IfStatement node)
            {
                var calls = new CallCollector(_known);
                node.Predicate?.Accept(calls);

                if (calls.Calls.Count > 0)
                {
                    var ordinal = NextOrdinal("IF");
                    foreach (var (qualified, line, text) in calls.Calls)
                    {
                        Facts.Add(new ReferencedFunctionCallFact(qualified, "IF", ordinal, line, text));
                    }
                }

                base.ExplicitVisit(node);
            }
```

`IF` 갈래가 `Collect`를 쓰지 않는 이유: `Collect`는 문장 전체를 훑는데 `IF` 본문의 호출까지 술어로 귀속시키면 안 된다. 그리고 번호는 호출이 있을 때만 소비한다 — 앞 브랜치의 `LockHintVisitor.ExplicitVisit(IfStatement)`가 같은 판단을 했고 그 근거가 그 자리 주석에 있다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests"`
Expected: PASS.

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0. `Visit`을 `ExplicitVisit`으로 바꾼 것이 순회 동작을 바꾸지 않았다는 회귀 확인이다. 기존 테스트가 깨지면 **행이 늘어난 것인지 기존 행이 사라진 것인지 먼저 가른다** — 후자면 회귀다.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs
git commit -m "feat: 참조 함수 표가 독립 SELECT와 IF 술어의 호출을 담는다"
```

---

### Task 2: `SetPredicateVisitor`가 같은 문장 집합을 방문한다

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs` (`SetPredicateVisitor`, 1530행 근처)
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`

**Interfaces:**
- Consumes: Task 1이 쓴 것과 같은 `HasFromClause`, 같은 채번 규칙.
- Produces: `SetPredicateFact.Operation`이 `"SELECT"`인 사실.

- [ ] **Step 1: 실패 테스트를 쓴다**

```csharp
[Fact]
public void ExtractSetPredicates_StandaloneSelectWhere_ShouldBeCollected()
{
    // COLLECTYMD:100 실측 - "회수구분이 1(자동회수)인 행만 조회한다"는 사실이
    // 어떤 기계 확정 표에도 없고 산문에만 있었다.
    const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT
AS
BEGIN
    DECLARE @v INT
    SELECT @v = CollectDay
    FROM   dbo.TPGCollectPeriodMst WITH(NOLOCK)
    WHERE  CollectFlag = 1
    RETURN @v
END";

    var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

    var fact = Assert.Single(facts);
    Assert.Equal("SELECT", fact.Operation);
    Assert.Equal(1, fact.StatementOrdinal);
    Assert.Equal("CollectFlag", fact.Column);
    Assert.Equal("=", fact.Operator);
    Assert.Equal(new[] { "1" }, fact.Literals);
    Assert.Equal("CollectFlag = 1", fact.PredicateText);
}

[Fact]
public void ExtractSetPredicates_StandaloneSelectWithoutFrom_ShouldNotConsumeAnOrdinal()
{
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @a = 1

    SELECT @b = C FROM dbo.TA WITH(NOLOCK) WHERE X = 7
END";

    var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

    var fact = Assert.Single(facts);
    Assert.Equal("SELECT", fact.Operation);
    Assert.Equal(1, fact.StatementOrdinal);
    Assert.Equal(new[] { "7" }, fact.Literals);
}

[Fact]
public void ExtractSetPredicates_DmlOrdinals_ShouldNotShift()
{
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @v = C FROM dbo.TA WITH(NOLOCK) WHERE X = 1

    UPDATE A SET A.Y = 1 FROM dbo.TB A WHERE A.Z = 2
END";

    var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

    var update = Assert.Single(facts, f => f.Operation == "UPDATE");
    Assert.Equal(1, update.StatementOrdinal);
    Assert.Equal(new[] { "2" }, update.Literals);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests.ExtractSetPredicates_StandaloneSelectWhere_ShouldBeCollected"`
Expected: FAIL — "The collection was empty".

- [ ] **Step 3: 최소 구현**

`SetPredicateVisitor`의 `Visit` 셋을 `ExplicitVisit`으로 바꾸고 `SelectStatement`를 더한다. 기존 `Collect(operation, node, where, ordinal)`을 그대로 쓴다 — 그 안에서 최상위 WHERE와 파생 테이블 WHERE를 둘 다 훑는다.

```csharp
            public override void ExplicitVisit(UpdateSpecification node)
            {
                Collect("UPDATE", node, node.WhereClause, NextOrdinal("UPDATE"));
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(DeleteSpecification node)
            {
                Collect("DELETE", node, node.WhereClause, NextOrdinal("DELETE"));
                base.ExplicitVisit(node);
            }

            /// <summary>
            /// DML 밖의 독립 SELECT의 최상위 WHERE. 판정은 Task 1·앞 브랜치와 같은
            /// HasFromClause를 쓴다 - 네 표가 같은 문장을 같은 번호로 가리켜야 한다.
            ///
            /// [실물 - 2026-08-23 축 A ③(b)] COLLECTYMD:100의 `CollectFlag = 1`은
            /// 리터럴 우변 등치라 이 표가 담을 수 있는 형태인데 독립 SELECT라
            /// 담기지 않았다. "자동회수 행만 조회한다"가 산문에만 있었다.
            /// </summary>
            public override void ExplicitVisit(SelectStatement node)
            {
                if (HasFromClause(node))
                {
                    var ordinal = NextOrdinal("SELECT");
                    foreach (var spec in QuerySpecificationsOf(node.QueryExpression))
                    {
                        Collect("SELECT", node, spec.WhereClause, ordinal);
                    }
                }

                base.ExplicitVisit(node);
            }
```

`InsertSpecification`의 기존 `Visit`은 그대로 둔다 — 그 안에서 `NextOrdinal`을 먼저 부르고 UNION 갈래마다 `Collect`를 호출하는 구조이고, 그 이유가 그 자리 주석에 실측 근거와 함께 적혀 있다. **`ExplicitVisit`으로 바꿀 필요가 없다.**

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests"`
Expected: PASS.

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0. **골든 테스트가 깨질 수 있다** — `AxisAGoldenCaseTests`의 집합 술어 개수 기대값(`CANCEL_INS` 4/2, `INS_EXTRA4PLCARD` 20/13, `AcqManual` 6/0, `EXCEPTION_PROC` 102/30)이 독립 SELECT 술어만큼 늘어난다. 깨지면 **원본 DDL을 열어 그 객체의 독립 SELECT WHERE를 세고** 새 기대값을 실측으로 정한다. 구현 결과를 그대로 베끼지 말 것.

`AcqManual`은 커서 원천 SELECT(`:33-35`)가 `B.AcqType = 1`·`A.OutState IN (2,9)`를 갖고 있어 이 Task로 처음 실린다 — 앞 브랜치가 "SetPredicateVisitor는 넓히지 않는다"는 이유로 0으로 두었던 자리다.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs tests/ReSet.Core.Tests/AxisAGoldenCaseTests.cs
git commit -m "feat: 집합 술어 표가 독립 SELECT의 최상위 술어를 담는다"
```

---

### Task 3: 잠금 힌트가 FROM 절 바깥 하위 질의 세 모양을 담는다

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs` (`LockHintVisitor`)
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`

**Interfaces:**
- Consumes: `SubqueryCollector`(깊이를 세며 `ScalarSubquery`를 모은다), `CollectSubqueryScans`, `SubqueryScope` 상수 — 전부 앞 브랜치가 만들었다.
- Produces: SELECT 목록·`SET` 절 하위 질의의 `하위 질의` 범위 행.

- [ ] **Step 1: 실패 테스트를 쓴다**

```csharp
[Fact]
public void ExtractLockHints_SubqueryInSelectList_ShouldUseSubqueryScope()
{
    // UF_Get_CLComm4MobileCo:31-32 실측 - SELECT 목록 안 스칼라 하위 질의의 NOLOCK이
    // 표에 오지 않았다. 같은 문장의 FROM은 최상위로 실리므로, 표가 그 문장에 대해
    // 채워진 것처럼 보이는데 두 스캔 중 하나가 빠진다 - 없는 것보다 나쁜 모양이다.
    const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT
AS
BEGIN
    DECLARE @v INT
    SELECT @v = CASE WHEN MobileCo1 > 0 THEN MobileCo1
                     ELSE (SELECT CommissionRate
                           FROM   dbo.TClientCMRate WITH(NOLOCK)
                           WHERE  ClientID = '1')
                END
    FROM   dbo.TClientSettleRate4MobileCo WITH(NOLOCK)
    RETURN @v
END";

    var facts = DmlScopeExtractor.ExtractLockHints(ddl);

    var outer = Assert.Single(facts, f => f.Table == "dbo.TClientSettleRate4MobileCo");
    Assert.Equal("최상위", outer.Scope);

    var inner = Assert.Single(facts, f => f.Table == "dbo.TClientCMRate");
    Assert.Equal("하위 질의", inner.Scope);
    Assert.Equal(new[] { "NOLOCK" }, inner.Hints);
    Assert.Equal(outer.StatementOrdinal, inner.StatementOrdinal);
}

[Fact]
public void ExtractLockHints_SubqueryInSetClause_ShouldUseSubqueryScope()
{
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.OutYMD = (SELECT MAX(YMD) FROM dbo.TB WITH(NOLOCK))
    FROM   dbo.TA A WITH(NOLOCK)
END";

    var facts = DmlScopeExtractor.ExtractLockHints(ddl);

    var inner = Assert.Single(facts, f => f.Table == "dbo.TB");
    Assert.Equal("하위 질의", inner.Scope);
    Assert.Equal("UPDATE", inner.Operation);
    Assert.Equal(1, inner.StatementOrdinal);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests.ExtractLockHints_SubqueryInSelectList_ShouldUseSubqueryScope"`
Expected: FAIL — `Assert.Single(facts, f => f.Table == "dbo.TClientCMRate")`가 매칭 원소 없음으로 실패한다.

- [ ] **Step 3: 최소 구현**

**먼저 실물을 읽어라.** 앞 브랜치가 `CollectWhereSubqueries`와 `CollectSubqueryScans`를 만들었고 `SubqueryScope` 상수와 `SubqueryCollector`가 이미 있다. 그 구조를 확인한 뒤, WHERE만 훑던 것을 **문장 전체의 `ScalarSubquery`**로 넓힌다.

```bash
grep -n "CollectWhereSubqueries\|CollectSubqueryScans\|class SubqueryCollector\|SubqueryScope" src/ReSet.Core/Services/DmlScopeExtractor.cs
```

핵심은 수집 대상을 `where.SearchCondition`에서 문장 노드 전체로 넓히되, **FROM 절이 이미 `CollectFrom`으로 훑어지므로 중복이 같은 라벨로 흡수되는지** 확인하는 것이다. `Add`의 중복 제거 키는 `(Operation, StatementOrdinal, Table, Alias, Line)`이고 `Scope`를 포함하지 않으므로, 같은 참조가 두 경로로 오면 먼저 등록된 라벨이 남는다. 앞 브랜치가 `ScopeOf`의 우선순위(`하위 질의` > `파생` > `최상위`)로 이 순서 의존을 없앴다 — 그 우선순위가 여전히 성립하는지 테스트로 확인하라.

**주의:** 문장 전체를 훑으면 `IF` 술어 안 하위 질의가 DML 경로에서도 잡힐 수 있다. `IF`는 DML 안에 나타날 수 없으므로 오늘은 겹치지 않지만, 그 근거를 주석에 남겨라.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests"`
Expected: PASS.

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs
git commit -m "feat: 잠금 힌트 표가 SELECT 목록과 SET 절의 하위 질의를 담는다"
```

---

### Task 4: `비집계 대입` 종류를 더한다

**Files:**
- Create: `src/ReSet.Core/Services/NonAggregateAssignmentExtractor.cs`
- Modify: `src/ReSet.Core/Services/ExecutionSemanticsFacts.cs` (상수 · `AllKinds` · `Collect`)
- Test: `tests/ReSet.Core.Tests/NonAggregateAssignmentExtractorTests.cs` (신규)

**Interfaces:**
- Produces: `NonAggregateAssignmentFact(int Line, string Variable, string Column, string Sentence)` — Task 5는 이 형태를 본떠 자기 레코드를 만든다.
- Produces: `ExecutionSemanticsFacts.NonAggregateAssignmentKind = "비집계 대입"`.

- [ ] **Step 1: 실패 테스트를 쓴다**

`AggregateAssignmentExtractorTests.cs`의 스타일을 먼저 읽고 따르라.

```csharp
[Fact]
public void Extract_NonAggregateAssignment_ShouldSayThePreviousValueSurvives()
{
    // PROC_ETC:71 실측 - SELECT @v_intID = ID는 비집계 대입이라 무결과 시
    // 직전 값이 남는다. 79행의 집계 대입(MAX)은 무결과 시 NULL이 대입되므로
    // 정반대다. 둘이 표에 나란히 놓여야 대비가 보인다.
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intID INT
    SELECT @v_intID = ID
    FROM   dbo.TSettleMiss WITH(NOLOCK)
    WHERE  ClientID = '1'
END";

    var facts = NonAggregateAssignmentExtractor.Extract(ddl);

    var fact = Assert.Single(facts);
    Assert.Equal("@v_intID", fact.Variable);
    Assert.Equal("ID", fact.Column);
    Assert.Contains("직전 값", fact.Sentence);
}

[Fact]
public void Extract_AggregateAssignment_ShouldNotBeCollected()
{
    // 집계는 AggregateAssignmentExtractor의 몫이다. 두 추출기가 같은 문장을
    // 각각 내면 표에 모순되는 두 행이 실린다.
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = MAX(ID) FROM dbo.TA WITH(NOLOCK)
END";

    Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
}

[Fact]
public void Extract_AssignmentWithoutFrom_ShouldNotBeCollected()
{
    // SELECT @v = 1은 조회가 아니라 대입이다. 무결과라는 개념이 없다.
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = 1
END";

    Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
}

[Fact]
public void Extract_UnparseableDdl_ShouldReturnEmpty()
{
    // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
    Assert.Empty(NonAggregateAssignmentExtractor.Extract("CREATE PROCEDURE ((("));
    Assert.Empty(NonAggregateAssignmentExtractor.Extract(null));
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~NonAggregateAssignmentExtractorTests"`
Expected: FAIL — 컴파일 오류 `'NonAggregateAssignmentExtractor' 이름이 현재 컨텍스트에 없습니다`.

- [ ] **Step 3: 최소 구현**

`AggregateAssignmentExtractor.cs`를 본떠 만든다 — 같은 `TSql160Parser` + `try`/`catch` + 방문자 구조다. 그 파일을 먼저 읽고 진입점 모양을 그대로 따르라.

판정: `SelectStatement`의 `QuerySpecification`에서 `SelectSetVariable` 요소를 찾고, 그 `Expression`이 **집계 함수 호출이 아니며** 문장에 `FromClause`가 있을 때만 사실을 낸다. 확정 사실 문장은 이 형태로 쓴다.

```
비집계 SELECT는 결과가 없으면 대입 자체가 일어나지 않습니다. 무결과 시 변수에는 직전 값이 그대로 남습니다 — DECLARE의 초기값이 아니라 이 문장 직전의 값입니다.
```

`ExecutionSemanticsFacts`에 셋을 더한다.

```csharp
        public const string NonAggregateAssignmentKind = "비집계 대입";
```

`AllKinds`의 **끝에** 붙인다(Global Constraints — 기존 다섯의 순서를 흔들지 않는다).

```csharp
        public static readonly IReadOnlyList<string> AllKinds = new[]
        {
            DatabasePlacementKind,
            AggregateAssignmentKind,
            RowCountKind,
            CursorKind,
            TypePathKind,
            NonAggregateAssignmentKind
        };
```

`Collect`에 갈래를 더한다(기존 갈래들과 같은 모양).

```csharp
            foreach (var fact in NonAggregateAssignmentExtractor.Extract(ddlText))
            {
                facts.Add(new ExecutionSemanticFact(
                    NonAggregateAssignmentKind,
                    fact.Line.ToString(),
                    $"SELECT {fact.Variable} = {fact.Column}",
                    fact.Sentence));
            }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~NonAggregateAssignmentExtractorTests"`
Expected: PASS.

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0. `MachineConfirmedTablesTests.EveryExecutionSemanticKindConstant_IsListedInAllKinds`가 새 상수를 `AllKinds`에서 찾는다 — 빠뜨렸으면 여기서 걸린다.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/NonAggregateAssignmentExtractor.cs src/ReSet.Core/Services/ExecutionSemanticsFacts.cs tests/ReSet.Core.Tests/NonAggregateAssignmentExtractorTests.cs
git commit -m "feat: 실행 의미 표가 비집계 대입의 무결과 동작을 담는다"
```

---

### Task 5: `루프 내 재설정` 종류를 더한다

**Files:**
- Create: `src/ReSet.Core/Services/LoopVariableResetExtractor.cs`
- Modify: `src/ReSet.Core/Services/ExecutionSemanticsFacts.cs` (상수 · `AllKinds` · `Collect`)
- Test: `tests/ReSet.Core.Tests/LoopVariableResetExtractorTests.cs` (신규)

**Interfaces:**
- Consumes: Task 4가 `AllKinds` 끝에 상수를 붙인 자리 — 그 **뒤에** 붙인다.
- Produces: `ExecutionSemanticsFacts.LoopVariableResetKind = "루프 내 재설정"`.

- [ ] **Step 1: 실패 테스트를 쓴다**

```csharp
[Fact]
public void Extract_SetInsideWhileBody_ShouldSayItResetsEachIteration()
{
    // PROC_ETC:69 실측(🔴) - WHILE 본문 첫 문장 SET @v_intID = 0이 커서 행마다
    // 재설정한다. 이 사실이 없으면 이행자가 재설정을 빠뜨리고, 무매칭 행에서
    // 선행 ID가 남아 UPDATE가 0행 갱신 → 신규 INSERT 누락 → 금액 검증 불일치로
    // 배치 전량 롤백된다.
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intID INT = 0
    WHILE (@@FETCH_STATUS = 0) BEGIN
        SET @v_intID = 0
        SELECT @v_intID = ID FROM dbo.TA WITH(NOLOCK)
    END
END";

    var facts = LoopVariableResetExtractor.Extract(ddl);

    var fact = Assert.Single(facts);
    Assert.Equal("@v_intID", fact.Variable);
    Assert.Contains("반복마다", fact.Sentence);
}

[Fact]
public void Extract_SetOutsideLoop_ShouldNotBeCollected()
{
    // 루프 밖 SET은 DECLARE 초기값과 다르지 않다 - 담을 사실이 없다.
    const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SET @v = 0
END";

    Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
}

[Fact]
public void Extract_UnparseableDdl_ShouldReturnEmpty()
{
    Assert.Empty(LoopVariableResetExtractor.Extract("CREATE PROCEDURE ((("));
    Assert.Empty(LoopVariableResetExtractor.Extract(null));
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~LoopVariableResetExtractorTests"`
Expected: FAIL — 컴파일 오류.

- [ ] **Step 3: 최소 구현**

`WhileStatement`를 `ExplicitVisit`으로 감싸 본문 안인지 추적하고, 그 안의 `SetVariableStatement`를 모은다. 앞 브랜치의 `LockHintVisitor._dmlDepth`가 죽은 코드로 판정돼 제거된 선례가 있으니, **깊이 추적이 실제로 가르는 것이 있는지 테스트로 확인하라** — 루프 밖 `SET`이 수집되지 않는다는 테스트가 그것이다.

확정 사실 문장:

```
이 대입은 WHILE 본문 안에 있어 반복마다 다시 실행됩니다. DECLARE의 초기값이 아니라 매 반복의 시작값입니다 — 이행 시 루프 안에서 초기화하지 않으면 직전 반복의 값이 남습니다.
```

`ExecutionSemanticsFacts`에 상수·`AllKinds` 끝 추가·`Collect` 갈래를 Task 4와 같은 모양으로 더한다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~LoopVariableResetExtractorTests"`
Expected: PASS.

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/LoopVariableResetExtractor.cs src/ReSet.Core/Services/ExecutionSemanticsFacts.cs tests/ReSet.Core.Tests/LoopVariableResetExtractorTests.cs
git commit -m "feat: 실행 의미 표가 루프 내 변수 재설정을 담는다"
```

---

### Task 6: L1 확인, 캐시 버전 13, 문서

**Files:**
- Modify: `src/ReSet.Core/Services/CacheManager.cs`
- Modify: `tests/ReSet.Core.Tests/CacheManagerTests.cs` (버전 리터럴 — **설계된 트립와이어다**)
- Modify: `docs/architecture.md`, `AGENTS.md`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` (확인용)

- [ ] **Step 1: L1이 새 행을 그대로 흘려보내는지 확인하는 테스트를 쓴다**

`CheckSetPredicates`와 `CheckExecutionSemantics`가 행 단위 대조이므로 새 행이 그냥 통과할 것으로 보이나, **확인 없이 넘기면 표만 넓어지고 검사는 침묵한다.** 앞 브랜치가 `CheckLockHints`에서 같은 확인을 요구했고 그때는 실제로 손댈 것이 없었다.

```csharp
[Fact]
public void CheckSetPredicates_SelectRow_ShouldCompareLikeDmlRows()
{
    var expectations = new SpecExpectations
    {
        SetPredicates = new[]
        {
            new SetPredicateFact(
                "SELECT", 100, "CollectFlag", false, new[] { "1" }, 1, "=", "최상위",
                "CollectFlag = 1")
        }
    };

    const string markdown = @"## CRUD 분석

### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| SELECT 1 | 100 | CollectFlag | = | 최상위 | 1 | 1 | CollectFlag = 1 |
";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.DoesNotContain(
        result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
}

[Fact]
public void CheckSetPredicates_SelectRowDropped_ShouldReport()
{
    var expectations = new SpecExpectations
    {
        SetPredicates = new[]
        {
            new SetPredicateFact(
                "SELECT", 100, "CollectFlag", false, new[] { "1" }, 1, "=", "최상위",
                "CollectFlag = 1")
        }
    };

    const string markdown = @"## CRUD 분석

### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.NotEmpty(result.DetailedErrors);
}
```

`MechanicalValidator.Validate`는 인스턴스 메서드다(`new MechanicalValidator().Validate(...)`).

- [ ] **Step 2: 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests.CheckSetPredicates_SelectRow"`

두 테스트가 **통과하면** L1은 손댈 것이 없다 — 그것이 "행 단위 대조라 그대로 흘러간다"는 주장의 확인이다. 실패하면 그때 `CheckSetPredicates`를 고친다. 어느 쪽이든 보고하라.

- [ ] **Step 3: 캐시 버전을 13으로 올린다**(계획 시점에는 12였다 — `main`이 2026-08-23 ④ 진단으로 12를 먼저 써서 병합 때 13으로 밀렸다)

```bash
grep -n "CurrentCacheFormatVersion" src/ReSet.Core/Services/CacheManager.cs
```

`13`이어야 한다 — 병합으로 밀렸다(계획 시점 기대값은 `11`이었고, 이 회차가 12로 올렸다가 `main`이 먼저 쓴 12와 부딪혀 13이 됐다). **`10` 이하면 멈추고 보고하라** — 앞 브랜치가 병합되지 않았다는 뜻이다.

`13`으로 바꾸고 기존 양식대로 주석을 더한다: 참조 함수·집합 술어 표가 독립 SELECT와 `IF` 술어를 담게 됐고, 잠금 힌트가 SELECT 목록·`SET` 절 하위 질의를 담으며, 실행 의미 표에 종류 둘이 늘었다. 옛 엔트리를 재사용하면 새 행 없는 산출물이 남는다.

`CacheManagerTests`의 버전 리터럴도 함께 간다 — 그 테스트 주석이 스스로를 트립와이어로 선언한다("버전을 올리면 이 테스트가 깨지고, 깨진 자리에서 '정말 전건 재분석을 의도했는가'를 한 번 더 묻게 된다").

- [ ] **Step 4: 문서를 동기화한다**

```bash
grep -n "집합 술어\|참조 함수\|잠금 힌트\|실행 의미" docs/architecture.md AGENTS.md
```

**표의 내용을 서술하기 전에 렌더러의 헤더 줄을 먼저 읽어라** — `AGENTS.md`에 규칙으로 있고, 앞 브랜치에서 이 규칙을 어겨 여섯 자리에 거짓이 실렸다. 표마다 갈라 적어라: 네 표가 이제 같은 문장 집합을 보되 **DML 범위 표에는 범위 칸이 없고 `IF n` 행도 없다.**

- [ ] **Step 5: 전체 테스트와 커밋**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0.

```bash
git add src/ReSet.Core/Services/CacheManager.cs tests/ReSet.Core.Tests/CacheManagerTests.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs docs/architecture.md AGENTS.md
git commit -m "chore: 캐시 버전 13과 네 표의 문장 집합 통일을 문서에 반영한다"
```

---

### Task 7: 재생성으로 실제로 닫혔는지 확인한다

**Files:** 없음(확인만 한다). 결함이 남으면 그 자리를 고치는 별도 커밋을 낸다.

- [ ] **Step 1: 백업하고 재생성한다**

재생성은 외부 AI CLI(`codex-cli`·`Claude`)를 호출하고 `output/`을 덮어쓴다. **먼저 백업하라.**

```bash
cp -R /Users/payletter/git-root/ReSet/output /Users/payletter/git-root/ReSet/output.bak-<날짜>
dotnet run --project src/ReSet.Cli -- --sp UP_UTIL_SETTLE_PROC_ETC < /dev/null 2>&1 | tail -30
```

`COLLECTYMD`·`WORKDAY2`·`UF_Get_CLComm4MobileCo`·`UIF_SettleYMD`는 함수라 `--sp`로 직접 지정할 수 없다. 파이프라인이 SP의 의존성으로 함께 재생성하므로, 그 함수들을 참조하는 SP를 돌려라 — `UP_UTIL_SETTLE_EXPECT_PROC`이 `UF_GET_COLLECTYMD`를 부른다. 로그(`output/logs/reset-<날짜>.log`)에서 `분석 시작 - Type: UDF, Key: ...` 줄로 어느 함수가 돌았는지 확인하라.

- [ ] **Step 2: 앵커를 대조한다**

| 확인 | 명령 | 기대 |
|---|---|---|
| 참조 함수 표 신설 | `grep -n "참조 함수" output/Functions/dbo.UF_GET_COLLECTYMD/docs/Spec.md` | 히트 |
| 링크가 걸린다 | 같은 표에서 `UF_GET_WORKDAY2` 행 | Spec.md 링크 칸이 채워짐 |
| **산문 요약이 사라진다** | `grep -n "휴일을 만나면 간격을 연장" output/Functions/dbo.UF_GET_COLLECTYMD/docs/Spec.md` | **0건** |
| 집합 술어 | `grep -n "CollectFlag" output/Functions/dbo.UF_GET_COLLECTYMD/docs/Spec.md` | 표에 `= 1` 행 |
| SELECT 목록 하위 질의 | `grep -n "TClientCMRate" output/Functions/dbo.UF_Get_CLComm4MobileCo/docs/Spec.md` | 잠금 힌트 표에 `하위 질의` |
| 루프 내 재설정 | `grep -n "루프 내 재설정" output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/docs/Spec.md` | 69행 행 |
| 비집계 대입 | `grep -n "비집계 대입" output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/docs/Spec.md` | 71행 행, 79행 집계와 나란히 |

**표 행을 셀 때는 섹션으로 잘라라.** 앞 브랜치에서 `grep -c`가 센 문자열 출현 횟수를 표 행 수로 읽은 오류가 있었다.

- [ ] **Step 3: 채번 회귀를 확인한다**

```bash
diff <(grep -oE "^\| (UPDATE|INSERT|DELETE) [0-9]+" output.bak-<날짜>/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/docs/Spec.md | sort -u) \
     <(grep -oE "^\| (UPDATE|INSERT|DELETE) [0-9]+" output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/docs/Spec.md | sort -u)
```

Expected: 차이 없음. 차이가 있으면 Global Constraints가 깨진 것이다.

- [ ] **Step 4: L1이 조용한지 확인한다**

재생성 로그에 `SetPredicateMismatch`나 실행 의미 관련 오류가 반복되면 표를 넓힌 쪽과 검사를 넓힌 쪽이 어긋난 것이다. 마지막 객체까지 `[L1/L2 자동 검증] 모두 통과!`가 나와야 한다.

- [ ] **Step 5: 결과를 기록하고 커밋한다**

닫힌 결함과 남은 결함을 감사 카탈로그에 반영한다.

```bash
git add docs/
git commit -m "docs: 축 A ③(b) 재생성 확인 결과를 기록한다"
```

---

## 완료 기준

- Task 1~6의 커밋 여섯 개가 있고, 마지막 커밋 시점에 `dotnet test`가 실패 0 · 건너뜀 0이다(코퍼스 있는 상태).
- Task 7의 확인 일곱 개가 전부 기대대로다.
- 기존 DML 문장 번호가 재생성 전후로 같다.
- 닫힌 결함: 🔴 1건(루프 내 변수 재설정) · 🟡 4건(함수→함수 3 · SELECT 목록 하위 질의 1).

## 이 계획이 닫지 않는 것

스펙 §6 그대로다. ④ 산문이 표를 뒤집음(3건, 🔴1), ⑤ 감사 기준과 도구 정책의 불일치(5건, 다른 세션이 착수), `Add`의 중복 제거 키에 `Hints`가 없는 것, `—` 마커의 네 곳 선언.
