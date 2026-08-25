# 기계 확정 표 확장 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 트랜잭션 경계 표와 변수 대입 표를 기계 확정 표로 더해 커버리지 맵의 🟧 382건 중 202건을 관할에 넣는다.

**Architecture:** 두 신규 추출기가 AST에서 재료를 확정하고, `SpecExpectations`가 실어 나르고, `AiService`가 프롬프트에 표 뼈대로 강제하고, `MechanicalValidator`가 전사를 대조하고, `CoverageMapComposer`가 그 재료를 세어 🟧을 줄인다. 추론은 0 — 전사만 담는다.

**Tech Stack:** .NET 10.0, `Microsoft.SqlServer.TransactSql.ScriptDom` 180.37.3 (`TSql160Parser`), xUnit 2.9.3, `Xunit.SkippableFact` 1.5.61

**Spec:** [`docs/superpowers/specs/2026-08-24-machine-table-expansion-design.md`](../specs/2026-08-24-machine-table-expansion-design.md)

**필수 참조 (구현 전에 읽어라):** `.claude/skills/reset-l1-check/references/authoring-contract.md` — 형제 검사들이 이미 정한 관례 8항목. 각 항목의 "실측" 줄은 2026-08-22 회차에서 그것을 어겨서 **실제로 난 결함**이다.

## Global Constraints

- **`namespace ReSet.Core.Services`**(구현) / **`ReSet.Core.Tests`**(테스트). 한국어 주석, 근거를 남기는 문체.
- **파서 오류 정책:** `new TSql160Parser(true)`로 파싱하고 오류가 하나라도 있으면 **빈 목록**. `CaseBranchExtractor.Extract`(`src/ReSet.Core/Services/CaseBranchExtractor.cs:50-60`)와 같은 정책이다.
- **추론 금지.** 감싼 조건·타입 추론·요약·정규화를 하지 않는다. 원문 슬라이스만.
- **`MachineConfirmedTables.All`의 순서를 흔들지 마라.** 그 파일 주석: *"목록의 순서가 곧 Critic 프롬프트에 실리는 순서다. 프롬프트 접두사 캐시가 바이트 일치로 걸리므로 순서를 흔들지 마십시오."* **맨 끝에 append.**
- **`AiService`의 표 렌더는 `BuildMachineFactBlockLines` 안에서만.** 호출부 5곳(`AiService.cs:469` · `1814` · `2945` · `3085` · `3259`)이 전부 그 하나를 통한다. 밖에서 직접 배선하면 진입점이 둘이 되어 *"표 하나가 늘 때 한 갈래만 조용히 못 받는 회귀"*를 막던 보호가 사라진다.
- **빌드 경고 상한 0.** `dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l`이 **0**. (`AGENTS.md:199`의 "정확히 8건"은 낡았다 — 실측 0.) `dotnet test` 실패 0.
- **`output/` 쓰기 금지.** 읽기만 한다.
- **캐시 인상은 Task 8에서만.** 그 전에 코퍼스 스윕으로 오탐을 먼저 잡는다 — 거짓 양성을 안은 채 전건을 돌리면 그 오탐이 곧바로 재시도 소진으로 번진다(`reset-l1-check` SKILL.md).

### 코퍼스 기준선 (2026-08-24 실측)

```
output/.sp_cache_index.json  FormatVersion 분포 → {15: 31}   (균일)
코드 CurrentCacheFormatVersion          → 15
main의 CurrentCacheFormatVersion        → 15
```

세대 왜곡이 없다. 스윕 결과를 그대로 읽어도 된다.

---

## File Structure

| 파일 | 책임 |
|---|---|
| `src/ReSet.Core/Services/TransactionBoundaryExtractor.cs` (신규) | `BEGIN`/`COMMIT`/`ROLLBACK`/`SAVE` 문장 → 라인·종류·이름 |
| `src/ReSet.Core/Services/SetAssignmentExtractor.cs` (신규) | `SetVariableStatement` 전수 → 라인·변수·대입식 원문 |
| `src/ReSet.Core/Services/SpecExpectations.cs` (수정) | 속성 2개 + `From()` 배선 + **null 체인 항 2개** |
| `src/ReSet.Core/Services/MachineConfirmedTables.cs` (수정) | `All` 맨 끝에 항목 2개 |
| `src/ReSet.Core/Services/AiService.cs` (수정) | 표 뼈대 렌더 2개 (`BuildMachineFactBlockLines` 안) |
| `src/ReSet.Core/Services/MechanicalValidator.cs` (수정) | 검사 2개 + `ErrorType` 2개 + 등록(`:178` 부근) |
| `src/ReSet.Core/Services/CoverageMapComposer.cs` (수정) | `ExtractorFactLines`에 컬렉션 2개 |
| `src/ReSet.Core/Services/CoverageMapHtmlWriter.cs` (수정) | 전이 상태 각주 |
| `src/ReSet.Core/Services/CacheManager.cs` (수정) | `CurrentCacheFormatVersion` 15 → 16 |
| `tests/ReSet.Core.Tests/TransactionBoundaryExtractorTests.cs` (신규) | Task 1 |
| `tests/ReSet.Core.Tests/SetAssignmentExtractorTests.cs` (신규) | Task 2 |
| `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` (수정) | Task 5 — 기존 파일에 추가 |
| `tests/ReSet.Core.Tests/MachineConfirmedTablesTests.cs` (수정 또는 신규) | Task 4 |
| `tests/ReSet.Core.Tests/CoverageMapComposerTests.cs` (수정) | Task 7 |

**태스크 순서의 근거:** Task 1·2는 서로 독립이다. Task 6(코퍼스 스윕)은 **Task 8(캐시 인상)보다 반드시 앞**이다 — 거짓 양성을 안은 채 전건 재생성을 걸면 재시도가 소진된다.

> ### [2026-08-24 계획 정정] Task 4를 4a·4b로 가른다
>
> **계획의 첫 판이 기존 불변식을 어겼다.** `MachineConfirmedTablesTests.EveryMachineConfirmedHeadingConstant_IsRegisteredInTheCatalog`가 **리플렉션으로 어셈블리의 모든 `TableHeading` 상수를 찾아** `MachineConfirmedTables.All`에 등록됐는지 단언한다(`tests/ReSet.Core.Tests/MachineConfirmedTablesTests.cs:51-67`). 반대 방향 짝(`CatalogHasNoEntryWithoutAHeadingConstant`)도 있다.
>
> 즉 **`TableHeading` 선언과 `All` 등록은 같은 커밋에 있어야 한다.** 첫 판은 선언을 Task 1·2에, 등록을 Task 4에 갈라 놓아 그 사이 내내 스위트가 빨갛다. Task 1·2 워커가 **각자 독립적으로** 이것을 발견하고 범위 밖이라 고치지 않은 채 보고했다 — 옳은 판단이다.
>
> **정정:** Task 4를 둘로 가른다.
>
> - **Task 4a — `MachineConfirmedTables.All` 등록.** Task 1·2만 소비한다(두 `TableHeading` 상수). **웨이브 1 직후 단독으로 돌려 불변식을 복구한다.**
> - **Task 4b — `AiService` 표 렌더.** Task 3(`SpecExpectations` 속성)을 소비하므로 웨이브 3에 남는다.
>
> 두 등록을 한 태스크에 묶는 이유는 **둘 다 `MachineConfirmedTables.cs`의 같은 자리를 고치기 때문**이다 — 갈라서 병렬로 돌리면 체리픽이 충돌한다.
>
> 아래 「Task 4」 절은 **Task 4b**로 읽는다. Task 4a는 그 절의 Step 1~3(테스트·`All` 등록)만 떼어 수행하고, Step 4(`AiService` 렌더)는 Task 4b가 한다.

---

### Task 1: `TransactionBoundaryExtractor`

**Files:**
- Create: `src/ReSet.Core/Services/TransactionBoundaryExtractor.cs`
- Test: `tests/ReSet.Core.Tests/TransactionBoundaryExtractorTests.cs`

**Interfaces:**
- Consumes: `Microsoft.SqlServer.TransactSql.ScriptDom` 만
- Produces:
  - `public sealed record TransactionBoundaryFact(int Line, string Kind, string Name)`
  - `public static class TransactionBoundaryExtractor` — `public const string TableHeading`, `public static IReadOnlyList<TransactionBoundaryFact> Extract(string? ddlText)`

- [x] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/TransactionBoundaryExtractorTests.cs`:

```csharp
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class TransactionBoundaryExtractorTests
    {
        [Fact]
        public void Extract_BeginCommitRollback_ShouldRecordLineAndKindInDocumentOrder()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    UPDATE dbo.T SET A = 1
    IF @@ERROR <> 0
        ROLLBACK TRANSACTION
    COMMIT TRANSACTION
END";

            var facts = TransactionBoundaryExtractor.Extract(ddl);

            Assert.Equal(3, facts.Count);
            Assert.Equal(3, facts[0].Line);
            Assert.Equal("BEGIN TRANSACTION", facts[0].Kind);
            Assert.Equal("ROLLBACK TRANSACTION", facts[1].Kind);
            Assert.Equal("COMMIT TRANSACTION", facts[2].Kind);
        }

        [Fact]
        public void Extract_UnnamedTransaction_ShouldRecordPlaceholderName()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    COMMIT TRANSACTION
END";

            var facts = TransactionBoundaryExtractor.Extract(ddl);

            Assert.All(facts, f => Assert.Equal("(없음)", f.Name));
        }

        [Fact]
        public void Extract_NamedTransaction_ShouldKeepNameVerbatim()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION SettleTran
    COMMIT TRANSACTION SettleTran
END";

            var facts = TransactionBoundaryExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Equal("SettleTran", facts[0].Name);
            Assert.Equal("SettleTran", facts[1].Name);
        }

        [Fact]
        public void Extract_SaveTransaction_ShouldBeRecordedAsItsOwnKind()
        {
            // 실측 코퍼스에는 0건이다. 그래도 담는 이유는 세이브포인트가 하나라도 있으면
            // 롤백 의미가 전체 취소가 아니라 지점 복귀로 바뀌기 때문이다 - 빠뜨리면 이 표가
            // "트랜잭션 경계는 이게 전부"라고 거짓말을 한다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    SAVE TRANSACTION Point1
    ROLLBACK TRANSACTION Point1
    COMMIT TRANSACTION
END";

            var facts = TransactionBoundaryExtractor.Extract(ddl);

            Assert.Equal(4, facts.Count);
            Assert.Contains(facts, f => f.Kind == "SAVE TRANSACTION" && f.Name == "Point1");
            Assert.Contains(facts, f => f.Kind == "ROLLBACK TRANSACTION" && f.Name == "Point1");
        }

        [Fact]
        public void Extract_NestedTransactions_ShouldRecordEveryStatement()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    BEGIN TRANSACTION
    COMMIT TRANSACTION
    COMMIT TRANSACTION
END";

            var facts = TransactionBoundaryExtractor.Extract(ddl);

            Assert.Equal(4, facts.Count);
            Assert.Equal(2, System.Linq.Enumerable.Count(facts, f => f.Kind == "BEGIN TRANSACTION"));
        }

        [Fact]
        public void Extract_NoTransaction_ShouldReturnEmpty()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T SET A = 1
END";

            Assert.Empty(TransactionBoundaryExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(TransactionBoundaryExtractor.Extract("CREATE PROCEDURE ((("));
        }

        [Fact]
        public void Extract_NullOrBlank_ShouldReturnEmpty()
        {
            Assert.Empty(TransactionBoundaryExtractor.Extract(null));
            Assert.Empty(TransactionBoundaryExtractor.Extract("   "));
        }

        [Fact]
        public void TableHeading_ShouldCarryTheMachineConfirmedSuffix()
        {
            Assert.EndsWith(
                MachineConfirmedTables.HeadingSuffix,
                TransactionBoundaryExtractor.TableHeading);
        }
    }
}
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~TransactionBoundaryExtractorTests"`
Expected: 컴파일 실패 — `TransactionBoundaryExtractor`가 없다.

- [x] **Step 3: 구현을 쓴다**

`src/ReSet.Core/Services/TransactionBoundaryExtractor.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <param name="Line">원본 DDL에서의 줄 번호(1부터).</param>
    /// <param name="Kind">"BEGIN TRANSACTION" · "COMMIT TRANSACTION" · "ROLLBACK TRANSACTION" · "SAVE TRANSACTION".</param>
    /// <param name="Name">트랜잭션/세이브포인트 이름. 없으면 "(없음)".</param>
    public sealed record TransactionBoundaryFact(int Line, string Kind, string Name);

    /// <summary>
    /// 트랜잭션 경계 문장을 전수 뽑는다. 줄·종류·이름만 담고 추론하지 않는다.
    ///
    /// [왜 감싼 조건을 담지 않는가] `ROLLBACK`이 어느 `IF` 아래인지를 담으면 이행 가치가
    /// 높다. 그럼에도 담지 않는 이유는 귀속이 틀리기 쉬운 자리이기 때문이다 - `ELSE` 분기,
    /// 중첩 `IF`, `BEGIN/END` 없는 단문 `IF`, `TRY/CATCH` 안의 `ROLLBACK`. 틀린 조건이
    /// 달린 행은 조건이 없는 행보다 나쁘다. 이 저장소는 이미 그 실패를 겪었다 - 감사 🔴이
    /// "파서가 잘못 계산했고, 모델은 충실히 옮겼고, Critic은 같은 목록으로 대조해 일치를
    /// 확인했다"였다. 감싼 조건은 별도 회차에서 `IF` 술어 귀속을 제대로 설계해 붙인다.
    ///
    /// [왜 SAVE TRANSACTION까지 담는가] 실측 코퍼스에는 0건이다. 그래도 담는 이유는
    /// 세이브포인트가 하나라도 있으면 롤백 의미가 통째로 달라지기 때문이다(전체 취소가
    /// 아니라 지점 복귀). 빠뜨리면 이 표가 "트랜잭션 경계는 이게 전부"라고 거짓말을 한다.
    /// </summary>
    public static class TransactionBoundaryExtractor
    {
        public const string TableHeading =
            "### 트랜잭션 경계 " + MachineConfirmedTables.HeadingSuffix;

        public const string NoName = "(없음)";

        public static IReadOnlyList<TransactionBoundaryFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<TransactionBoundaryFact>();

            TSqlFragment? fragment;
            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    // CaseBranchExtractor.Extract와 같은 정책 - 부분 파스 결과가 기계 확정
                    // 표에 섞이면 표 전체의 신뢰가 무너진다.
                    return Array.Empty<TransactionBoundaryFact>();
                }
            }
            catch (Exception)
            {
                return Array.Empty<TransactionBoundaryFact>();
            }

            var visitor = new BoundaryVisitor();
            fragment.Accept(visitor);
            return visitor.Facts.OrderBy(f => f.Line).ToList();
        }

        private sealed class BoundaryVisitor : TSqlFragmentVisitor
        {
            public List<TransactionBoundaryFact> Facts { get; } = new();

            public override void Visit(BeginTransactionStatement node) =>
                Add(node.StartLine, "BEGIN TRANSACTION", node.Name);

            public override void Visit(CommitTransactionStatement node) =>
                Add(node.StartLine, "COMMIT TRANSACTION", node.Name);

            public override void Visit(RollbackTransactionStatement node) =>
                Add(node.StartLine, "ROLLBACK TRANSACTION", node.Name);

            public override void Visit(SaveTransactionStatement node) =>
                Add(node.StartLine, "SAVE TRANSACTION", node.Name);

            private void Add(int line, string kind, IdentifierOrValueExpression? name)
            {
                if (line <= 0) return;
                Facts.Add(new TransactionBoundaryFact(line, kind, NameOf(name)));
            }

            /// <summary>이름은 식별자일 수도 변수일 수도 있다. 둘 다 원문 그대로 싣는다.</summary>
            private static string NameOf(IdentifierOrValueExpression? name)
            {
                if (name == null) return NoName;
                if (!string.IsNullOrWhiteSpace(name.Identifier?.Value)) return name.Identifier!.Value;
                if (name.ValueExpression is VariableReference v
                    && !string.IsNullOrWhiteSpace(v.Name))
                {
                    return v.Name;
                }
                return NoName;
            }
        }
    }
}
```

- [x] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~TransactionBoundaryExtractorTests"`
Expected: PASS (9 tests)

네 타입이 어셈블리에 실재함은 확인했다(`Microsoft.SqlServer.TransactSql.ScriptDom` 180.37.3). 넷 다 `TransactionStatement`를 상속하고 그 기반 타입이 `Name`을 `IdentifierOrValueExpression`으로 갖기 때문에 공용 `Add` 헬퍼 하나가 넷을 다 받는다. **컴파일이 깨지면 그 전제가 틀린 것이므로 실제 타입에 맞추고 CONCERNS에 적어라.**

- [x] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/TransactionBoundaryExtractor.cs tests/ReSet.Core.Tests/TransactionBoundaryExtractorTests.cs
git commit -m "feat: 트랜잭션 경계 문장을 전수 뽑는다

줄·종류·이름만 담고 감싼 조건은 담지 않는다. ROLLBACK이 어느 IF 아래인지는
ELSE·중첩 IF·단문 IF·TRY/CATCH에서 귀속을 틀리기 쉽고, 틀린 조건이 달린 행은
조건이 없는 행보다 나쁘다.

SAVE TRANSACTION은 코퍼스에 0건이지만 담는다. 세이브포인트가 있으면 롤백이
전체 취소가 아니라 지점 복귀가 되므로, 빠뜨리면 표가 '이게 전부'라고 거짓말한다."
```

---

### Task 2: `SetAssignmentExtractor`

**Files:**
- Create: `src/ReSet.Core/Services/SetAssignmentExtractor.cs`
- Test: `tests/ReSet.Core.Tests/SetAssignmentExtractorTests.cs`

**Interfaces:**
- Consumes: `Microsoft.SqlServer.TransactSql.ScriptDom` 만
- Produces:
  - `public sealed record SetAssignmentFact(int Line, string Variable, string Expression)`
  - `public static class SetAssignmentExtractor` — `public const string TableHeading`, `public static IReadOnlyList<SetAssignmentFact> Extract(string? ddlText)`

- [x] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SetAssignmentExtractorTests.cs`:

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SetAssignmentExtractorTests
    {
        [Fact]
        public void Extract_SimpleAssignment_ShouldKeepVariableAndExpressionVerbatim()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT
    SET @v = @@ERROR
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal(4, fact.Line);
            Assert.Equal("@v", fact.Variable);
            Assert.Equal("@@ERROR", fact.Expression);
        }

        [Fact]
        public void Extract_SelfReferencingIncrement_ShouldKeepWholeExpression()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @c INT
    SET @c = @c + 1
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal("@c", fact.Variable);
            Assert.Equal("@c + 1", fact.Expression);
        }

        [Fact]
        public void Extract_FunctionCallExpression_ShouldNotSummarise()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @d VARCHAR(8)
    SET @d = CONVERT(VARCHAR(8), GETDATE(), 112)
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Contains("CONVERT", fact.Expression);
            Assert.Contains("112", fact.Expression);
        }

        [Fact]
        public void Extract_SelectAssignment_ShouldNotBeCollected()
        {
            // 관할 경계다. `SELECT @v = ...`는 ScriptDom에서 SelectSetVariable이고
            // AggregateAssignmentExtractor(:104)·NonAggregateAssignmentExtractor(:75)가
            // 그 타입만 본다. 이 표가 그것까지 담으면 정본이 둘로 갈린다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT
    SELECT @v = COUNT(*) FROM dbo.T
END";

            Assert.Empty(SetAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_DeclareWithInitializer_ShouldNotBeCollected()
        {
            // DECLARE @v INT = 15는 DeclareVariableStatement다. 백로그 ④의 몫이라
            // 이 표는 담지 않는다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT = 15
END";

            Assert.Empty(SetAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_MultipleAssignments_ShouldBeOrderedByLine()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @a INT, @b INT
    SET @a = 1
    SET @b = 2
END";

            var facts = SetAssignmentExtractor.Extract(ddl);

            Assert.Equal(new[] { "@a", "@b" }, facts.Select(f => f.Variable).ToArray());
            Assert.True(facts[0].Line < facts[1].Line);
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(SetAssignmentExtractor.Extract("CREATE PROCEDURE ((("));
        }

        [Fact]
        public void Extract_NullOrBlank_ShouldReturnEmpty()
        {
            Assert.Empty(SetAssignmentExtractor.Extract(null));
            Assert.Empty(SetAssignmentExtractor.Extract("   "));
        }

        [Fact]
        public void TableHeading_ShouldCarryTheMachineConfirmedSuffix()
        {
            Assert.EndsWith(
                MachineConfirmedTables.HeadingSuffix,
                SetAssignmentExtractor.TableHeading);
        }
    }
}
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SetAssignmentExtractorTests"`
Expected: 컴파일 실패 — `SetAssignmentExtractor`가 없다.

- [x] **Step 3: 구현을 쓴다**

`src/ReSet.Core/Services/SetAssignmentExtractor.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <param name="Line">원본 DDL에서의 줄 번호(1부터).</param>
    /// <param name="Variable">대입 대상 변수 이름(`@`를 포함한 원문).</param>
    /// <param name="Expression">대입식 원문 그대로. 요약·정규화하지 않는다.</param>
    public sealed record SetAssignmentFact(int Line, string Variable, string Expression);

    /// <summary>
    /// `SET @v = <식>` 대입을 전수 뽑는다.
    ///
    /// [관할 경계] `SELECT @v = ...`는 여기 안 들어온다 - ScriptDom에서 그것은
    /// `SelectSetVariable`이고 `AggregateAssignmentExtractor`·
    /// `NonAggregateAssignmentExtractor`가 그 타입만 본다. `DECLARE @v INT = 15`도
    /// 안 들어온다(`DeclareVariableStatement`). 관할이 겹치면 정본이 갈라진다.
    ///
    /// [`LoopVariableResetExtractor`와의 관계] `WHILE` 최상위 상수 재설정은 실행 의미
    /// 표에도 있지만 여기서도 담는다. 중복이 아니라 층이 다르다 - 이 표는 "어떤 대입이
    /// 있나"(원본 전사)에, 실행 의미 표는 "매 반복 다시 설정된다"(DDL 원문이 말하지 않는
    /// 실행 시점의 사실)에 답한다. 여기서 빼면 표가 전수가 아니게 되고, 다음 사람이 왜
    /// 이 줄만 빠졌는지를 찾아야 한다.
    /// </summary>
    public static class SetAssignmentExtractor
    {
        public const string TableHeading =
            "### 변수 대입 " + MachineConfirmedTables.HeadingSuffix;

        public static IReadOnlyList<SetAssignmentFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<SetAssignmentFact>();

            TSqlFragment? fragment;
            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<SetAssignmentFact>();
                }
            }
            catch (Exception)
            {
                return Array.Empty<SetAssignmentFact>();
            }

            var visitor = new AssignmentVisitor();
            fragment.Accept(visitor);
            return visitor.Facts.OrderBy(f => f.Line).ToList();
        }

        private sealed class AssignmentVisitor : TSqlFragmentVisitor
        {
            public List<SetAssignmentFact> Facts { get; } = new();

            public override void Visit(SetVariableStatement node)
            {
                if (node.StartLine <= 0) return;

                var variable = node.Variable?.Name;
                if (string.IsNullOrWhiteSpace(variable)) return;

                var expression = TextOf(node.Expression);
                if (string.IsNullOrWhiteSpace(expression)) return;

                Facts.Add(new SetAssignmentFact(node.StartLine, variable!, expression));
            }
        }

        /// <summary>
        /// 원문 토큰을 그대로 이어 붙인다.
        ///
        /// [자기 사본을 쓰는 이유] `DmlScopeExtractor.TextOf`는 그 클래스 내부 private이라
        /// 부를 수 없다. `DerivedTableColumnExtractor.cs:165`가 이미 같은 로직의 자기
        /// 사본을 갖고 있는 것이 이 코드베이스의 관례다.
        /// </summary>
        private static string TextOf(TSqlFragment? fragment)
        {
            if (fragment == null || fragment.ScriptTokenStream == null) return string.Empty;

            var sb = new StringBuilder();
            var stream = fragment.ScriptTokenStream;
            var first = fragment.FirstTokenIndex;
            var last = fragment.LastTokenIndex;
            if (first < 0 || last < first || last >= stream.Count) return string.Empty;

            for (var i = first; i <= last; i++)
            {
                sb.Append(stream[i].Text);
            }

            return CollapseWhitespace(sb.ToString());
        }

        /// <summary>표 셀에 개행이 들어가면 마크다운 표가 깨진다. 공백 하나로 접는다.</summary>
        private static string CollapseWhitespace(string text)
        {
            var sb = new StringBuilder(text.Length);
            var pendingSpace = false;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch)) { pendingSpace = sb.Length > 0; continue; }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
```

- [x] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SetAssignmentExtractorTests"`
Expected: PASS (9 tests)

`Extract_SelfReferencingIncrement_...`의 기대값 `"@c + 1"`은 토큰 사이 공백을 하나로 접은 결과다. `CollapseWhitespace`가 원문 공백을 정확히 재현하지 못하므로, 실제 출력이 다르면 **기대값을 실측에 맞추되 그 사실을 CONCERNS에 적어라** — "원문 그대로"가 어디까지인지가 이 표의 계약이다.

- [x] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/SetAssignmentExtractor.cs tests/ReSet.Core.Tests/SetAssignmentExtractorTests.cs
git commit -m "feat: SET 변수 대입을 전수 뽑는다

SELECT @v = ...(SelectSetVariable)와 DECLARE @v INT = 15
(DeclareVariableStatement)는 담지 않는다 - 각각 기존 추출기와 백로그 4번의
몫이고, 관할이 겹치면 정본이 갈라진다.

WHILE 최상위 상수 재설정은 실행 의미 표에도 있지만 여기서도 담는다. 중복이
아니라 층이 다르다 - 이 표는 '어떤 대입이 있나', 실행 의미 표는 '매 반복 다시
설정된다'에 답한다."
```

---

### Task 3: `SpecExpectations` 배선 — null 체인이 이 태스크의 본체다

**Files:**
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs`
- Test: `tests/ReSet.Core.Tests/SpecExpectationsTests.cs` (없으면 신규)

**Interfaces:**
- Consumes: Task 1·2의 `TransactionBoundaryExtractor.Extract` · `SetAssignmentExtractor.Extract`
- Produces: `SpecExpectations.TransactionBoundaries` (`IReadOnlyList<TransactionBoundaryFact>`) · `SpecExpectations.SetAssignments` (`IReadOnlyList<SetAssignmentFact>`)

> **작성 계약 1번이 이 태스크의 전부다.** `SpecExpectations.From`은 모든 재료가 비면 `null`을 돌려주고, `Validate`는 `expectations != null` 블록을 통째로 건너뛴다. **AND-체인(`SpecExpectations.cs:326-418`)에 자기 항을 잇지 않으면 그 재료만 있는 명세서에서 검사가 한 번도 안 돈다 — 그리고 스위트는 초록으로 남는다.** 그 파일 주석이 직접 경고하는 자리다.
>
> **반대 방향도 본다.** 항을 더하면 `From`이 객체를 돌려주는 경우가 **넓어진다.** 이전에 재료가 없어 L1을 아예 안 받던 명세서가 이제 **모든** 검사를 받는다. 각 검사가 자기 재료가 빌 때 조용히 early-return 하는지 확인하라(Task 6 스윕이 이걸 실측으로 잡는다).

- [x] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SpecExpectationsTransactionAndSetTests
    {
        private static SpDefinition Def(string ddl) => new()
        {
            ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            DdlText = ddl
        };

        [Fact]
        public void From_TransactionOnlyProcedure_ShouldNotReturnNull()
        {
            // 작성 계약 1: null 체인에 자기 항을 잇지 않으면 이 명세서에서 L1이
            // 한 번도 안 돈다. 스위트는 초록으로 남는다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    COMMIT TRANSACTION
END";

            var expectations = SpecExpectations.From(Def(ddl));

            Assert.NotNull(expectations);
            Assert.Equal(2, expectations!.TransactionBoundaries.Count);
        }

        [Fact]
        public void From_SetAssignmentOnlyProcedure_ShouldNotReturnNull()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT
    SET @v = 1
END";

            var expectations = SpecExpectations.From(Def(ddl));

            Assert.NotNull(expectations);
            Assert.Single(expectations!.SetAssignments);
        }

        [Fact]
        public void From_EmptyProcedure_ShouldStillReturnNull()
        {
            // 체인을 넓히되 "아무 재료도 없으면 null"이라는 계약은 지켜야 한다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    PRINT 'x'
END";

            Assert.Null(SpecExpectations.From(Def(ddl)));
        }
    }
}
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SpecExpectationsTransactionAndSetTests"`
Expected: 컴파일 실패 — `TransactionBoundaries`·`SetAssignments` 속성이 없다.

- [x] **Step 3: 속성과 배선을 더한다**

`SpecExpectations` 레코드 본문에 (`CaseBranches` 옆, `SpecExpectations.cs:119` 부근):

```csharp
        /// <summary>트랜잭션 경계 문장. 줄·종류·이름만 담는다.</summary>
        public IReadOnlyList<TransactionBoundaryFact> TransactionBoundaries { get; init; }
            = Array.Empty<TransactionBoundaryFact>();

        /// <summary>`SET @v = <식>` 대입 전수. `SELECT @v = ...`는 여기 없다.</summary>
        public IReadOnlyList<SetAssignmentFact> SetAssignments { get; init; }
            = Array.Empty<SetAssignmentFact>();
```

`From()` 안, 다른 추출기 호출들 옆에:

```csharp
            var transactionBoundaries = TransactionBoundaryExtractor.Extract(spDef.DdlText);
            var setAssignments = SetAssignmentExtractor.Extract(spDef.DdlText);
```

**null 판정 체인(`SpecExpectations.cs:326-418`)의 마지막 항 앞에 두 항을 잇는다:**

```csharp
                && caseBranches.Count == 0
                && transactionBoundaries.Count == 0      // ← 추가
                && setAssignments.Count == 0             // ← 추가
                && insertTargetTables.Count == 0
```

그리고 반환하는 객체 초기화에 두 속성을 채운다.

- [x] **Step 4: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SpecExpectations"`
Expected: PASS

전체도 돌려 기존 테스트가 안 깨지는지 본다: `dotnet test --filter "FullyQualifiedName~ReSet.Core.Tests"`

- [x] **Step 5: 되돌림으로 계약 1번을 확인한다**

두 `&& ...Count == 0` 항을 **임시로 지우고** `From_TransactionOnlyProcedure_ShouldNotReturnNull`을 돌려 **실패하는지** 보라. 실패하지 않으면 그 테스트는 계약 1번을 잠그지 못하는 것이다. 확인 후 원복하고, 방법과 결과를 보고에 적어라.

- [x] **Step 6: 커밋한다**

```bash
git add src/ReSet.Core/Services/SpecExpectations.cs tests/ReSet.Core.Tests/SpecExpectationsTransactionAndSetTests.cs
git commit -m "feat: 트랜잭션 경계·SET 대입 재료를 SpecExpectations에 싣는다

null 판정 체인에 자기 항을 함께 잇는다. 빠뜨리면 그 재료만 있는 명세서에서
L1이 한 번도 안 돌고 스위트는 초록으로 남는다(작성 계약 1번, 2026-08-22에
실제로 난 결함)."
```

---

### Task 4: 프롬프트 배선 — 표 뼈대 둘

**Files:**
- Modify: `src/ReSet.Core/Services/MachineConfirmedTables.cs`
- Modify: `src/ReSet.Core/Services/AiService.cs` (`BuildMachineFactBlockLines` 내부)
- Test: `tests/ReSet.Core.Tests/MachineConfirmedTablesTests.cs`

**Interfaces:**
- Consumes: Task 1·2의 `TableHeading` 상수, Task 3의 `SpecExpectations` 속성
- Produces: 프롬프트에 실리는 표 뼈대. 뒤 태스크가 소비하는 새 심볼은 없다.

- [x] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class MachineConfirmedTablesExpansionTests
    {
        [Fact]
        public void All_ShouldContainTheTwoNewTables()
        {
            var headings = MachineConfirmedTables.All.Select(t => t.Heading).ToList();

            Assert.Contains(TransactionBoundaryExtractor.TableHeading, headings);
            Assert.Contains(SetAssignmentExtractor.TableHeading, headings);
        }

        [Fact]
        public void All_ShouldAppendNewTablesAtTheEnd()
        {
            // 순서가 곧 Critic 프롬프트에 실리는 순서다. 프롬프트 접두사 캐시가 바이트
            // 일치로 걸리므로 기존 항목 사이에 끼우면 캐시가 통째로 깨진다.
            var headings = MachineConfirmedTables.All.Select(t => t.Heading).ToList();
            var referencedFunctionIndex =
                headings.IndexOf(DmlScopeExtractor.ReferencedFunctionTableHeading);

            Assert.True(
                headings.IndexOf(TransactionBoundaryExtractor.TableHeading) > referencedFunctionIndex,
                "새 표는 기존 마지막 항목 뒤에 와야 한다");
            Assert.True(
                headings.IndexOf(SetAssignmentExtractor.TableHeading) > referencedFunctionIndex,
                "새 표는 기존 마지막 항목 뒤에 와야 한다");
        }

        [Fact]
        public void CriticExemptionBlock_ShouldCoverTheTwoNewTables()
        {
            // All에 넣으면 Critic 면제가 자동으로 따라온다. 이것이 없으면 Critic이
            // 새 표를 환각으로 오판하고 L1은 반대로 전사를 요구해 교착이 된다
            // (2026-08-22 재생성에서 실제로 세 번 났다).
            var block = MachineConfirmedTables.CriticExemptionBlock;

            Assert.Contains("트랜잭션 경계", block);
            Assert.Contains("변수 대입", block);
        }
    }
}
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MachineConfirmedTablesExpansionTests"`
Expected: FAIL — `All`에 새 항목이 없다.

- [x] **Step 3: `MachineConfirmedTables.All` 맨 끝에 두 항목을 더한다**

`src/ReSet.Core/Services/MachineConfirmedTables.cs`의 `All` 배열 **마지막 요소 뒤**에:

```csharp
            // 둘 다 DDL 본문에서 그대로 읽히는 전사 표다.
            new MachineConfirmedTable(
                TransactionBoundaryExtractor.TableHeading,
                MachineConfirmedTableVerification.DdlTranscription),
            new MachineConfirmedTable(
                SetAssignmentExtractor.TableHeading,
                MachineConfirmedTableVerification.DdlTranscription)
```

- [x] **Step 4: `AiService`에 표 렌더 둘을 더한다**

`BuildCaseBranchTableLines`(`AiService.cs:1241`) 옆에 같은 모양으로:

```csharp
        private static List<string> BuildTransactionBoundaryTableLines(
            IReadOnlyList<TransactionBoundaryFact> facts)
        {
            var lines = new List<string>
            {
                "   [CRITICAL TRANSACTION BOUNDARY TABLE] The following transaction statements are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## 로직 흐름 요약` under the exact heading shown. Never merge rows, never omit a ROLLBACK, and never describe a boundary in prose instead of listing it - the batch implementation must reproduce every one of them.",
                $"   {TransactionBoundaryExtractor.TableHeading}",
                "   | 라인 | 종류 | 이름 |",
                "   | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                lines.Add($"   | {fact.Line} | {EscapeTableCell(fact.Kind)} | {EscapeTableCell(fact.Name)} |");
            }

            lines.Add("");
            return lines;
        }

        private static List<string> BuildSetAssignmentTableLines(
            IReadOnlyList<SetAssignmentFact> facts)
        {
            var lines = new List<string>
            {
                "   [CRITICAL VARIABLE ASSIGNMENT TABLE] The following SET assignments are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## 로직 흐름 요약` under the exact heading shown. Never summarise an assignment expression and never merge rows - the verbatim expression text is the contract.",
                $"   {SetAssignmentExtractor.TableHeading}",
                "   | 라인 | 변수 | 대입식 원문 |",
                "   | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                lines.Add($"   | {fact.Line} | {EscapeTableCell(fact.Variable)} | {EscapeTableCell(fact.Expression)} |");
            }

            lines.Add("");
            return lines;
        }
```

**`BuildMachineFactBlockLines`(`AiService.cs:1326`) 안**에서 `CaseBranch`가 처리되는 자리와 같은 방식으로 두 표를 호출한다. 둘 다 `## 로직 흐름 요약` 소관이므로 `CaseBranch`와 **같은 `MachineFactPresentation` 분기**를 탄다.

> **밖에서 배선하지 마라.** 호출부 5곳(`:469`·`1814`·`2945`·`3085`·`3259`)이 전부 이 함수 하나를 통하는 것이 *"표 하나가 늘 때 한 갈래만 조용히 못 받는 회귀"*를 막는 구조다.

- [x] **Step 5: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MachineConfirmedTablesExpansionTests"`
Expected: PASS (3 tests)

Run: `dotnet test --filter "FullyQualifiedName~AiService"`
Expected: 기존 테스트 전부 통과. 프롬프트 스냅샷을 비교하는 테스트가 있으면 새 표가 추가돼 깨질 수 있다 — 그때는 **기대값을 갱신하되 무엇이 왜 늘었는지 CONCERNS에 적어라.**

- [x] **Step 6: 커밋한다**

```bash
git add src/ReSet.Core/Services/MachineConfirmedTables.cs src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/MachineConfirmedTablesExpansionTests.cs
git commit -m "feat: 트랜잭션 경계·변수 대입 표를 프롬프트에 강제한다

All의 맨 끝에 붙인다 - 순서가 곧 Critic 프롬프트 순서이고 접두사 캐시가 바이트
일치로 걸린다. All에 넣으면 Critic 면제가 자동으로 따라와, 새 표를 환각으로
오판해 재시도를 소진시키던 교착을 막는다.

렌더는 BuildMachineFactBlockLines 안에서만 한다. 호출부 5곳이 전부 그 하나를
통하는 것이 표 하나가 늘 때 한 갈래만 조용히 못 받는 회귀를 막는 구조다."
```

---

### Task 5: L1 검사 둘

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` (기존 파일에 추가)

**Interfaces:**
- Consumes: Task 3의 `SpecExpectations.TransactionBoundaries`·`SetAssignments`, Task 1·2의 `TableHeading`
- Produces: `ErrorType.TransactionBoundaryTableMissing` · `ErrorType.SetAssignmentTableMissing`

> **`CheckCaseBranches`(`MechanicalValidator.cs:4310-4370`)를 먼저 읽고 그 모양을 그대로 따르라.** 작성 계약 2·3·4·5·6·7번이 그 함수에 이미 구현돼 있다 — `LocateHeadingSection`으로 자기 절만 보고, `SplitTableRowCells`로 셀을 읽고, 자기 `try/catch`를 두고, 재료가 비면 early-return 한다. **새 판단을 만들지 마라.**

**테스트 관례(작성 계약):**
- `CodeObjectKey`는 위치 레코드다. `CodeObjectKey.Create("DB", "dbo", "NAME", CodeObjectType.Procedure)`를 쓴다. 객체 초기화는 컴파일되지 않는다.
- 최소 명세서는 기존 헬퍼 `WrapSpec(string crudBody)`가 만든다. **새 헬퍼를 만들지 마라.**
- **픽스처를 실제 코퍼스 모양으로 써라** — 인접 표·산문이 낀 표를 포함한다.

- [x] **Step 1: 실패하는 테스트를 쓴다**

`MechanicalValidatorTests.cs`에 추가:

```csharp
        private static SpecExpectations TransactionExpectations() =>
            SpecExpectations.From(new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                DdlText = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    COMMIT TRANSACTION
END"
            })!;

        [Fact]
        public void Validate_TransactionBoundaryTableMissing_ShouldReport()
        {
            var markdown = WrapSpec("### SELECT 대상 테이블\n\n내용 없음\n");

            var result = new MechanicalValidator().Validate(markdown, TransactionExpectations());

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.TransactionBoundaryTableMissing);
        }

        [Fact]
        public void Validate_TransactionBoundaryRowsPresent_ShouldNotReport()
        {
            var markdown = WrapSpec(
                "### SELECT 대상 테이블\n\n내용 없음\n\n"
                + TransactionBoundaryExtractor.TableHeading + "\n\n"
                + "| 라인 | 종류 | 이름 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 3 | BEGIN TRANSACTION | (없음) |\n"
                + "| 4 | COMMIT TRANSACTION | (없음) |\n");

            var result = new MechanicalValidator().Validate(markdown, TransactionExpectations());

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.TransactionBoundaryTableMissing);
        }

        [Fact]
        public void Validate_TransactionBoundaryRowMissing_ShouldReportThatRow()
        {
            var markdown = WrapSpec(
                TransactionBoundaryExtractor.TableHeading + "\n\n"
                + "| 라인 | 종류 | 이름 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 3 | BEGIN TRANSACTION | (없음) |\n");

            var result = new MechanicalValidator().Validate(markdown, TransactionExpectations());

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.TransactionBoundaryTableMissing
                     && e.Message.Contains("4"));
        }

        [Fact]
        public void Validate_TransactionTableFollowedByAnotherTable_ShouldNotBleedIntoIt()
        {
            // 작성 계약 4: 표 경계는 빈 줄이고, `|`로 시작하지 않는 임의의 줄도 표를
            // 끝낸다. 인접 표를 합치면 뒤 표 헤더가 앞 표 너비와 비교돼 거짓 오류가 난다
            // (2026-08-22 실측: 코퍼스 31개 중 9개에 거짓 양성 10건).
            var markdown = WrapSpec(
                TransactionBoundaryExtractor.TableHeading + "\n\n"
                + "| 라인 | 종류 | 이름 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 3 | BEGIN TRANSACTION | (없음) |\n"
                + "| 4 | COMMIT TRANSACTION | (없음) |\n"
                + "\n"
                + "보조 설명 한 줄.\n"
                + "\n"
                + "| 다른 표 | 칸 |\n"
                + "| :--- | :--- |\n"
                + "| x | y |\n");

            var result = new MechanicalValidator().Validate(markdown, TransactionExpectations());

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.TransactionBoundaryTableMissing);
        }

        [Fact]
        public void Validate_IndentedTransactionHeading_ShouldStillBeFound()
        {
            // 작성 계약 5: 프롬프트는 헤딩을 3칸 들여써서 렌더한다. 모델이 그것을
            // 보존하는 회차가 오면 접두사 비교가 실패해 검사가 조용히 죽는다.
            var markdown = WrapSpec(
                "   " + TransactionBoundaryExtractor.TableHeading + "\n\n"
                + "| 라인 | 종류 | 이름 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 3 | BEGIN TRANSACTION | (없음) |\n"
                + "| 4 | COMMIT TRANSACTION | (없음) |\n");

            var result = new MechanicalValidator().Validate(markdown, TransactionExpectations());

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.TransactionBoundaryTableMissing);
        }

        [Fact]
        public void Validate_NoTransactionMaterial_ShouldNotReportAnything()
        {
            // 작성 계약 1의 뒷면: null 체인을 넓혔으므로 이전에 L1을 안 받던 명세서가
            // 이제 모든 검사를 받는다. 자기 재료가 비면 조용히 넘어가야 한다.
            var expectations = SpecExpectations.From(new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "Q", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "Q",
                DdlText = @"CREATE PROCEDURE dbo.Q AS
BEGIN
    DECLARE @v INT
    SET @v = 1
END"
            });

            var result = new MechanicalValidator().Validate(WrapSpec("내용\n"), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.TransactionBoundaryTableMissing);
        }
    }
}
```

**SET 대입 검사 테스트도 같은 파일에 이어 쓴다:**

```csharp
        private static SpecExpectations SetAssignmentExpectations() =>
            SpecExpectations.From(new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "S", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "S",
                DdlText = @"CREATE PROCEDURE dbo.S AS
BEGIN
    DECLARE @v INT
    SET @v = 1
    SET @v = @v + 1
END"
            })!;

        [Fact]
        public void Validate_SetAssignmentTableMissing_ShouldReport()
        {
            var markdown = WrapSpec("### SELECT 대상 테이블\n\n내용 없음\n");

            var result = new MechanicalValidator().Validate(markdown, SetAssignmentExpectations());

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetAssignmentTableMissing);
        }

        [Fact]
        public void Validate_SetAssignmentRowsPresent_ShouldNotReport()
        {
            var markdown = WrapSpec(
                "### SELECT 대상 테이블\n\n내용 없음\n\n"
                + SetAssignmentExtractor.TableHeading + "\n\n"
                + "| 라인 | 변수 | 대입식 원문 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 4 | @v | 1 |\n"
                + "| 5 | @v | @v + 1 |\n");

            var result = new MechanicalValidator().Validate(markdown, SetAssignmentExpectations());

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetAssignmentTableMissing);
        }

        [Fact]
        public void Validate_SetAssignmentExpressionParaphrased_ShouldReportThatRow()
        {
            // 대입식을 말로 바꾸면 원문에서 찾을 수 없다. CheckCaseBranches가 조건
            // 원문까지 대조하는 것과 같은 강도다.
            var markdown = WrapSpec(
                SetAssignmentExtractor.TableHeading + "\n\n"
                + "| 라인 | 변수 | 대입식 원문 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 4 | @v | 1 |\n"
                + "| 5 | @v | 1씩 증가시킵니다 |\n");

            var result = new MechanicalValidator().Validate(markdown, SetAssignmentExpectations());

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetAssignmentTableMissing
                     && e.Message.Contains("5"));
        }

        [Fact]
        public void Validate_SetAssignmentTableFollowedByAnotherTable_ShouldNotBleedIntoIt()
        {
            // 작성 계약 4: 표 경계는 빈 줄이고 `|`로 시작하지 않는 임의의 줄도 표를
            // 끝낸다. 2026-08-22 실측: 인접 표를 합쳐 코퍼스 31개 중 9개에 거짓 양성 10건.
            var markdown = WrapSpec(
                SetAssignmentExtractor.TableHeading + "\n\n"
                + "| 라인 | 변수 | 대입식 원문 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 4 | @v | 1 |\n"
                + "| 5 | @v | @v + 1 |\n"
                + "\n"
                + "보조 설명 한 줄.\n"
                + "\n"
                + "| 다른 표 | 칸 |\n"
                + "| :--- | :--- |\n"
                + "| x | y |\n");

            var result = new MechanicalValidator().Validate(markdown, SetAssignmentExpectations());

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetAssignmentTableMissing);
        }

        [Fact]
        public void Validate_IndentedSetAssignmentHeading_ShouldStillBeFound()
        {
            // 작성 계약 5: 프롬프트는 헤딩을 3칸 들여쓴다.
            var markdown = WrapSpec(
                "   " + SetAssignmentExtractor.TableHeading + "\n\n"
                + "| 라인 | 변수 | 대입식 원문 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 4 | @v | 1 |\n"
                + "| 5 | @v | @v + 1 |\n");

            var result = new MechanicalValidator().Validate(markdown, SetAssignmentExpectations());

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetAssignmentTableMissing);
        }

        [Fact]
        public void Validate_NoSetAssignmentMaterial_ShouldNotReportAnything()
        {
            // 작성 계약 1의 뒷면: 체인을 넓혔으므로 이전에 L1을 안 받던 명세서가 이제
            // 모든 검사를 받는다. 자기 재료가 비면 조용히 넘어가야 한다.
            var expectations = SpecExpectations.From(new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "T", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "T",
                DdlText = @"CREATE PROCEDURE dbo.T AS
BEGIN
    BEGIN TRANSACTION
    COMMIT TRANSACTION
END"
            });

            var result = new MechanicalValidator().Validate(WrapSpec("내용\n"), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetAssignmentTableMissing);
        }
```

> 위 두 묶음의 픽스처 줄 번호(트랜잭션 3·4, SET 4·5)는 각 `DdlText` 리터럴에서 세어 나온 값이다. **`WrapSpec`이나 리터럴을 손대면 줄 번호가 밀린다** — 그때는 기대값을 추측하지 말고 추출기를 한 번 돌려 실제 값을 확인하라.

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests"`
Expected: 컴파일 실패 — `ErrorType.TransactionBoundaryTableMissing`이 없다.

- [x] **Step 3: `ErrorType` 둘을 더한다**

`MechanicalValidator.cs`의 `ErrorType` enum(`:53` 부근 `CaseBranchTableMissing` 옆):

```csharp
        TransactionBoundaryTableMissing,
        SetAssignmentTableMissing,
```

- [x] **Step 4: 검사 둘을 구현한다**

`CheckCaseBranches`(`:4310`)를 그대로 본떠 쓴다. 대조 키는 **라인 + 종류**(트랜잭션) / **라인 + 변수 + 대입식 원문**(SET)이다.

```csharp
        private static void CheckTransactionBoundaries(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.TransactionBoundaries.Count == 0) return;

            try
            {
                var lines = MarkdownSectionLocator.SplitLines(markdown);
                var (headingIndex, endIndex) = LocateHeadingSection(
                    lines, TransactionBoundaryExtractor.TableHeading);

                if (headingIndex < 0)
                {
                    var missing =
                        $"기계 확정 트랜잭션 경계 표가 명세서에 없습니다. "
                        + $"`{TransactionBoundaryExtractor.TableHeading}` 헤딩과 "
                        + $"{expectations.TransactionBoundaries.Count}개 행을 그대로 옮겨야 합니다.";
                    result.Errors.Add(missing);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.TransactionBoundaryTableMissing,
                        Message = missing
                    });
                    return;
                }

                var rowLines = new List<string>();
                for (var i = headingIndex + 1; i < endIndex; i++)
                {
                    if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                    {
                        rowLines.Add(lines[i]);
                    }
                }

                foreach (var fact in expectations.TransactionBoundaries)
                {
                    var lineToken = fact.Line.ToString();
                    var present = rowLines.Any(row =>
                    {
                        var cells = SplitTableRowCells(row);
                        return cells.Any(c => c == lineToken) && cells.Any(c => c == fact.Kind);
                    });
                    if (present) continue;

                    var message =
                        $"트랜잭션 경계 표에 라인 {fact.Line}의 `{fact.Kind}` 행이 없습니다. "
                        + "배치 구현이 재현해야 할 경계이므로 산문으로 대신하거나 행을 합치면 안 됩니다.";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.TransactionBoundaryTableMissing,
                        Message = message,
                        RawContext = $"{fact.Kind} @ line {fact.Line}"
                    });
                }
            }
            catch (Exception ex)
            {
                // 작성 계약 6: Validate의 catch-all은 Errors를 통째로 지우고 소프트
                // 패스시킨다. 가드가 없으면 이 검사의 예외가 기존 검사 전부의 판정을
                // 삼킨다. 이 catch는 메서드 전체 입도이므로 한 행에서 던지면 나머지
                // 행도 대조되지 않는다.
                Log.Warning(ex, "트랜잭션 경계 표 검사 중 오류가 발생하여 이 표의 대조를 건너뜁니다.");
            }
        }
```

`CheckSetAssignments`도 같은 모양으로 쓴다. 대조 키는 `fact.Line` + `fact.Variable` + `fact.Expression`이다(`CheckCaseBranches`가 조건 원문까지 대조하는 것과 같은 강도).

- [x] **Step 5: 검사 목록에 등록한다**

`MechanicalValidator.cs:178`의 `CheckCaseBranches(...)` 바로 아래:

```csharp
                    CheckTransactionBoundaries(cleansed, expectations, result);
                    CheckSetAssignments(cleansed, expectations, result);
```

- [x] **Step 6: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests"`
Expected: PASS (신규 12건 포함, 기존 전부 유지)

- [x] **Step 7: 되돌림으로 등록을 확인한다**

Step 5의 두 줄을 **임시로 지우고** Step 1의 "table missing" 테스트를 돌려 **실패하는지** 보라. 실패하지 않으면 검사가 등록 없이도 도는 것이거나 테스트가 무의미한 것이다. 확인 후 원복하고 방법을 보고에 적어라.

- [x] **Step 8: 커밋한다**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: 트랜잭션 경계·변수 대입 표의 전사를 L1이 대조한다

CheckCaseBranches의 모양을 그대로 따른다 - 자기 절만 보고, 자기 try/catch를
두고, 재료가 비면 조용히 넘어간다. 작성 계약 2~7번이 그 함수에 이미 구현돼
있어 새 판단을 만들지 않았다."
```

---

### Task 6: 코퍼스 전수 스윕 — 캐시 인상 **전에** 돈다

**Files:**
- Create: 스크래치 디렉터리의 `sweep.csproj` · `Program.cs` (커밋하지 않는다)
- Modify: `docs/superpowers/specs/2026-08-24-machine-table-expansion-design.md` (스윕 결과 기록)

**Interfaces:**
- Consumes: Task 3·5의 `SpecExpectations`·`MechanicalValidator`
- Produces: 산출물 코드 없음. **숫자.**

> **이 태스크가 `reset-l1-check`의 핵심이다.** 단위 테스트로는 두 부류가 안 보인다 — **검사가 한 번도 안 도는 것**과 **실제 명세서에만 나는 거짓 양성**. 둘 다 스위트가 초록인 채로 지나간다.
>
> 거짓 양성이 특히 위험하다. `VerificationPipelineOrchestrator`가 L1 오류를 재생성 트리거로 삼고 소진되면 `L1Exhausted` 배너를 붙인다. 캐시를 올린 회차라면 전건이 재생성되므로 **거짓 고발이 곧바로 재시도 소진**으로 이어지고, 시정 문구가 틀렸으면 모델이 **베껴야 할 기계 확정 표 자체를 망가뜨린다.**

- [x] **Step 1: 세대 왜곡을 확인한다**

```bash
python3 -c "
import json,io
from collections import Counter
idx=json.load(io.open('/Users/payletter/git-root/ReSet/output/.sp_cache_index.json',encoding='utf-8-sig'))
ent=idx.get('Entries') or idx
print(dict(sorted(Counter(v.get('FormatVersion') for v in ent.values()).items())))
"
grep -n 'CurrentCacheFormatVersion = ' src/ReSet.Core/Services/CacheManager.cs
```

Expected: `{15: 31}`이고 코드도 15. **분포가 갈려 있으면 스윕 결과가 노이즈다** — 그 차이가 만든 오류 종류를 먼저 걸러내고 읽어라. (2026-08-24 코디네이터 실측: `{15: 31}`, 코드 15, main 15 — 왜곡 없음.)

- [x] **Step 2: 스윕 하네스를 만든다**

`.claude/skills/reset-l1-check/references/corpus-sweep.md`의 `sweep.csproj`와 `Program.cs`를 **그대로** 스크래치 디렉터리에 만든다. `ImplicitUsings`를 빼면 `Console`·`Path`·`List`가 전부 미해결로 떨어진다.

- [x] **Step 3: BASE와 NEW를 각각 돌린다**

차분으로 읽는다. 절대 건수보다 **수정 전후 비교**가 판정을 만든다.

```bash
git archive <이 브랜치의 Task 1 직전 SHA> src/ReSet.Core | tar -x -C <임시경로-base>
```

BASE(신규 검사 없음)와 NEW(현재)를 각각 돌려 `ErrorType` 집계를 비교한다.

- [x] **Step 4: 읽고 판정한다**

- **새 두 검사가 0건이면 검사가 자기 존재 이유를 놓친 것이다.** 재료 필터가 너무 좁거나 `From`이 null을 돌려주는 경우다. 이 회차에서는 **31개 객체 전부가 새 표를 안 갖고 있으므로 두 검사가 대부분의 객체에서 발동하는 것이 정상**이다 — 트랜잭션이 있는 객체 수만큼, SET이 있는 객체 수만큼.
- **다른 검사 종류의 건수가 BASE와 달라졌다면 재료 확장이 옆 검사에 번진 것이다.** 작성 계약 1번이 경고한 바로 그 자리 — `From`의 null 체인을 넓혀 이전에 L1을 안 받던 명세서가 이제 모든 검사를 받게 됐기 때문이다. **하나라도 있으면 원인을 밝히고, 의도한 것이 아니면 좁혀라.**
- `null expectations` 개수가 BASE보다 줄었으면 그것이 체인 확장의 실물 증거다. 숫자를 적어라.

- [x] **Step 5: 숫자를 설계서에 적는다**

```
코퍼스 N쌍 · 로드 실패 0 · null expectations BASE x → NEW y
  TransactionBoundaryTableMissing: X건 (객체명 나열)
  SetAssignmentTableMissing:       Y건 (객체명 나열)
  다른 검사 카운트: BASE와 동일 / 달라졌다면 무엇이 왜
```

**"확인했다"는 근거가 아니다. 숫자로 적어라.**

그리고 **추출기가 실제로 낸 행 수**를 설계서 「미확정 사항」1번에 적어 백로그 예측(트랜잭션 105 · SET 97)과 대조하라. **예측이 빗나가면 그 자체가 보고 내용이다 — 숫자를 맞추려고 추출기를 조정하지 마라.**

- [x] **Step 6: 저장소에 남는 코퍼스 스모크 테스트를 더한다**

스윕 하네스는 폐기용이다. **저장소에 남아 다음 회차를 지키는 테스트**를 따로 둔다(설계서 §4 층 2). `output/`이 `.gitignore` 대상이라 CI에서는 건너뛰므로 `SkippableTheory`다 — `AxisAGoldenCaseTests`가 그 선례다.

`tests/ReSet.Core.Tests/MachineTableExpansionCorpusTests.cs` (신규):

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
    /// 실물 코퍼스에서 두 추출기가 폭주하지 않는지 보고 실제 건수를 출력한다.
    /// 합성 픽스처가 못 보는 것을 잡는 자리다 - 실제 DDL에만 있는 모양(파이프·개행이
    /// 든 대입식, 이름 있는 트랜잭션)은 여기서만 드러난다.
    /// </summary>
    public class MachineTableExpansionCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public MachineTableExpansionCorpusTests(ITestOutputHelper output) => _output = output;

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "output")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? string.Empty;
        }

        [SkippableFact]
        public void Extractors_OverTheCorpus_ShouldReportCountsWithoutExploding()
        {
            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), "output/을 찾지 못했다 - 코퍼스 스모크 건너뜀");

            var procedures = Path.Combine(root, "output", "Procedures");
            Skip.IfNot(Directory.Exists(procedures), "output/Procedures가 없다 - 건너뜀");

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            int tranTotal = 0, setTotal = 0, objects = 0;
            int pipeOrNewlineInExpression = 0;

            foreach (var dir in Directory.GetDirectories(procedures))
            {
                var meta = Path.Combine(dir, "raw", "metadata.json");
                if (!File.Exists(meta)) continue;

                var def = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(meta), opts);
                if (def == null) continue;
                objects++;

                var trans = TransactionBoundaryExtractor.Extract(def.DdlText);
                var sets = SetAssignmentExtractor.Extract(def.DdlText);
                tranTotal += trans.Count;
                setTotal += sets.Count;

                // 설계서 「미확정 사항」2번 - 셀 이스케이프 왕복이 실전에서 검증되는가.
                pipeOrNewlineInExpression += sets.Count(
                    f => f.Expression.Contains('|') || f.Expression.Contains('\n'));

                _output.WriteLine($"{Path.GetFileName(dir),-45} 트랜잭션 {trans.Count,3} · SET {sets.Count,3}");
            }

            _output.WriteLine("");
            _output.WriteLine($"객체 {objects} · 트랜잭션 합 {tranTotal} · SET 합 {setTotal}");
            _output.WriteLine($"대입식에 파이프/개행이 든 건수: {pipeOrNewlineInExpression}");
            _output.WriteLine("백로그 예측: 트랜잭션 105 · SET 97");

            // 건수는 관측 대상이지 계약이 아니다. 추출기가 죽지 않았다는 것만 단언한다.
            Assert.True(objects > 0, "코퍼스 객체를 하나도 못 읽었다");
        }
    }
}
```

**출력된 건수를 설계서 「미확정 사항」1번에, 파이프/개행 건수를 2번에 적어라.** 예측(105 · 97)과 다르면 **그 차이 자체가 보고 내용이다 — 숫자를 맞추려고 추출기를 조정하지 마라.**

- [x] **Step 7: 스크래치를 지우고 커밋한다**

```bash
git add docs/superpowers/specs/2026-08-24-machine-table-expansion-design.md tests/ReSet.Core.Tests/MachineTableExpansionCorpusTests.cs
git commit -m "docs: 코퍼스 전수 스윕 결과를 적고 스모크 테스트를 남긴다

캐시 인상 전에 돌려 거짓 양성을 먼저 잡는다. 거짓 양성을 안은 채 전건 재생성을
걸면 그 오탐이 곧바로 재시도 소진으로 번지고, 시정 문구가 틀렸으면 모델이
베껴야 할 기계 확정 표 자체를 망가뜨린다."
```

**거짓 양성이 하나라도 있으면 여기서 멈추고 원인을 고친다. Task 7·8로 넘어가지 않는다.**

---

### Task 7: 커버리지 맵 배선 + 전이 상태 각주

**Files:**
- Modify: `src/ReSet.Core/Services/CoverageMapComposer.cs` (`ExtractorFactLines`)
- Modify: `src/ReSet.Core/Services/CoverageMapHtmlWriter.cs` (전이 각주)
- Test: `tests/ReSet.Core.Tests/CoverageMapComposerTests.cs` · `CoverageMapHtmlWriterTests.cs`

**Interfaces:**
- Consumes: Task 3의 `SpecExpectations.TransactionBoundaries`·`SetAssignments`
- Produces: 없음

> **이 배선이 빠지면 아무것도 안 깨지면서 목적만 달성 안 된다.** 표는 생기는데 커버리지 맵이 그 재료를 안 세어 🟧이 그대로다. 그것을 잠그는 테스트가 이 태스크의 본체다.

- [x] **Step 1: 실패하는 테스트를 쓴다**

`CoverageMapComposerTests.cs`에 추가:

```csharp
        [Fact]
        public void Compose_TransactionStatement_ShouldCountAsExtractorMaterial()
        {
            // 배선 7번을 잠근다. ExtractorFactLines에 TransactionBoundaries가 없으면
            // 표는 생기는데 🟧이 그대로다 - 아무것도 안 깨지면서 목적만 달성 안 된다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    COMMIT TRANSACTION
END";
            var def = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                DdlText = ddl
            };

            var coverage = CoverageMapComposer.Compose("dbo.P", def, "## 개요\n");
            var begin = coverage.Statements.Single(
                s => s.Statement.StatementType == "BeginTransactionStatement");

            // 재료는 있고 앵커는 없으므로 SpecMissing이다 - 설계서 §3의 전이 상태.
            Assert.NotEmpty(begin.ExtractorLines);
            Assert.Equal(CoverageState.SpecMissing, begin.State);
        }

        [Fact]
        public void Compose_SetAssignment_ShouldCountAsExtractorMaterial()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT
    SET @v = 1
END";
            var def = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                DdlText = ddl
            };

            var coverage = CoverageMapComposer.Compose("dbo.P", def, "## 개요\n");
            var set = coverage.Statements.Single(
                s => s.Statement.StatementType == "SetVariableStatement");

            Assert.NotEmpty(set.ExtractorLines);
            Assert.Equal(CoverageState.SpecMissing, set.State);
        }
```

- [x] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CoverageMapComposerTests"`
Expected: FAIL — `ExtractorLines`가 비어 있고 상태가 `OutOfScope`다.

- [x] **Step 3: `ExtractorFactLines`에 두 컬렉션을 더한다**

`CoverageMapComposer.cs`의 `ExtractorFactLines` 안, 다른 `lines.AddRange(...)` 옆:

```csharp
            lines.AddRange(expectations.TransactionBoundaries.Select(f => f.Line));
            lines.AddRange(expectations.SetAssignments.Select(f => f.Line));
```

같은 메서드의 doc 주석에 두 재료가 이제 포함된다는 것을 적는다 — 그 주석의 부정확이 이전 회차에서 Critical 오판을 부른 자리다.

- [x] **Step 4: 전이 상태 각주를 HTML에 더한다**

`CoverageMapHtmlWriter`의 요약 절, 「명세서 결함」 각주 옆에 한 줄:

> ※ 명세서가 현재 캐시 버전보다 오래된 판이면 「명세서 결함」이 크게 나옵니다 — 도구가 새로 아는 사실을 명세서가 아직 담지 못한 예정된 중간 상태이며, 재생성 후 사라집니다.

그리고 그것을 잠그는 테스트를 `CoverageMapHtmlWriterTests.cs`에 더한다(문자열이 렌더된 HTML에 있는지).

- [x] **Step 5: 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CoverageMap"`
Expected: PASS

- [x] **Step 6: 되돌림으로 배선을 확인한다**

Step 3의 두 줄을 **임시로 지우고** Step 1의 테스트를 돌려 **실패하는지** 보라. 확인 후 원복하고 방법을 보고에 적어라.

- [x] **Step 7: 커밋한다**

```bash
git add src/ReSet.Core/Services/CoverageMapComposer.cs src/ReSet.Core/Services/CoverageMapHtmlWriter.cs tests/ReSet.Core.Tests/CoverageMapComposerTests.cs tests/ReSet.Core.Tests/CoverageMapHtmlWriterTests.cs
git commit -m "feat: 커버리지 맵이 새 두 재료를 세고 전이 상태를 각주로 밝힌다

이 배선이 빠지면 표는 생기는데 🟧이 그대로여서 아무것도 안 깨지면서 목적만
달성 안 된다. 되돌림으로 잠갔다.

재생성 전에는 재료만 있고 앵커가 없어 🟥이 크게 나온다. 회귀로 오인되지 않게
산출물에 각주를 남긴다."
```

---

### Task 8: 캐시 버전 인상 — 스윕이 깨끗할 때만

**Files:**
- Modify: `src/ReSet.Core/Services/CacheManager.cs:160`

**Interfaces:**
- Consumes: Task 6의 스윕 결과(거짓 양성 0)
- Produces: 없음

> **Task 6이 거짓 양성 0으로 끝났을 때만 이 태스크를 한다.** 거짓 양성을 안은 채 전건 재생성을 걸면 재시도가 소진된다.

- [x] **Step 1: main과 대조해 번호를 정한다**

```bash
git show main:src/ReSet.Core/Services/CacheManager.cs | grep "CurrentCacheFormatVersion ="
grep -n "CurrentCacheFormatVersion = " src/ReSet.Core/Services/CacheManager.cs
```

**둘 다 15이면 새 번호는 16이다**(2026-08-24 코디네이터 실측). **main이 이미 16 이상이면 한 번호 더 올려라** — 같은 번호 아래 두 계약이 생기면 먼저 병합된 쪽의 캐시가 다른 쪽 코드에서 **적중**해, 새 행이 빠진 명세서가 조용히 복사된다(2026-08-23에 두 세션이 각자 11→12로 올려 실제로 일어났다).

**이 저장소에서 다른 세션이 동시에 작업 중이다.** 이 단계에서 반드시 다시 확인하라 — 계획을 쓴 시점의 값이 낡았을 수 있다.

- [x] **Step 2: 올린다**

```csharp
        private const int CurrentCacheFormatVersion = 16;
```

주석으로 사유를 남긴다 — 프롬프트 입력(표 뼈대 둘)과 출력 계약(명세서에 표 둘)이 함께 바뀌었다.

- [x] **Step 3: 전체 검증**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
dotnet test
```

Expected: 경고 0, 실패 0.

캐시 인상으로 `CacheManagerTests`의 버전 기대값이 깨질 수 있다 — 그때는 갱신하고 CONCERNS에 적어라.

- [x] **Step 4: 커밋한다**

```bash
git add src/ReSet.Core/Services/CacheManager.cs tests/ReSet.Core.Tests/CacheManagerTests.cs
git commit -m "chore: 캐시 형식 버전을 16으로 올린다

프롬프트 입력(표 뼈대 둘)과 출력 계약(명세서에 표 둘)이 함께 바뀌었다. 안 올리면
재생성이 캐시 적중으로 건너뛰어져 검증 자체가 일어나지 않고, 옛 계약으로 만든
산출물이 다음 감사에서 결함으로 잡힌다.

코퍼스 스윕을 인상 전에 돌려 거짓 양성 0을 확인했다."
```

---

## 완료 기준 (계획 전체)

- [x] Task 1~8의 모든 단계가 체크됐다
- [x] `dotnet clean && dotnet build`의 `warning CS` 유일 건수가 **0**, `dotnet test` 실패 0
- [x] `MachineConfirmedTables.All`에 새 헤딩 2개가 **맨 끝에** 있다(순서 불변 테스트가 잠근다)
- [x] **코퍼스 스윕 숫자가 설계서에 적혔다** — 새 두 검사의 건수 · **거짓 양성 0** · 다른 검사 카운트가 BASE와 같은지
- [x] 추출기가 실제로 낸 행 수가 설계서 「미확정 사항」1번에, 대입식의 파이프/개행 건수가 2번에 적혔고, 백로그 예측(105 · 97)과의 차이가 기록됐다
- [x] 저장소에 남는 코퍼스 스모크 테스트(`MachineTableExpansionCorpusTests`)가 커밋됐다 — 스윕 하네스는 폐기용이라 별개다
- [x] 배선 7번(커버리지 맵)을 잠그는 테스트가 있고 **되돌림으로 확인**됐다
- [x] 계약 1번(null 체인)을 잠그는 테스트가 있고 **되돌림으로 확인**됐다
- [x] 캐시 버전이 **main과 대조해** 정해졌고, 스윕이 깨끗한 뒤에 올랐다
- [x] 재생성은 돌리지 않았고, 그 사실과 설계서 「미확정 사항」3번(L1 완전일치가 실물 재생성에서 통과하는지 미확인)이 남았다

---

## 완료 기록 (2026-08-25)

계획의 8개 태스크가 전부 브랜치에 들어갔다. **위 체크박스는 이 회차에 일괄로 채웠다** —
Task 1~4·7은 앞선 세션들이 커밋으로 끝냈으나 체크를 남기지 않았고, Task 5·6·8은 이 회차가 했다.
근거는 커밋과 아래 실측이다.

| Task | 커밋 |
|---|---|
| 1 `TransactionBoundaryExtractor` | `0df1791` · `57391b8` |
| 2 `SetAssignmentExtractor` | `e0d0000` · `0d0fba1` · `308579e` |
| 3 `SpecExpectations` 배선 | `81b2af1` |
| 4a `MachineConfirmedTables.All` | `6bc19ea` |
| 4b `AiService` 표 뼈대 | `b849c7d` |
| 7 커버리지 맵 + 전이 각주 | `abbc0c6` · `91fc429` · `41a6932` |
| **5 L1 검사 둘** | `889958e` |
| **6 코퍼스 전수 스윕** | `8477947` |
| **8 캐시 버전 16** | `44c0fa7` |

**병합 선행 작업.** Task 5를 시작하기 전에 `axis-b-step-check`를 병합했다(충돌 0). 그 브랜치의
7개 커밋이 전부 `MechanicalValidator.cs`를 고치고 있어서 — 앵커 재설계·파서 펜스 복구·단계 하한·
검사 A 정정 — 병합 없이 같은 파일에 검사 둘을 얹으면 나중에 큰 충돌이 난다.

**코퍼스 심링크.** 워크트리에는 `output/`이 없어 코퍼스 테스트 15건이 조용히 건너뛰고 있었다.
메인 저장소의 `output/`과 `output.bak-2026-08-22/`를 심링크해 **건너뜀 0**으로 만든 뒤에 모든
게이트를 판정했다(`reset-l1-check` 완료 기준 1번). 심링크 전 2683통과·15건너뜀 → 후 2727통과·0건너뜀.

### 되돌림 검증 (완료 기준 두 항)

- **검사 등록(Task 5 Step 7).** `Validate`의 검사 목록에서 두 줄을 지우니
  `Validate_TransactionBoundaryTableMissing_ShouldReport`·`Validate_SetAssignmentTableMissing_ShouldReport`가
  둘 다 실패했다. 원복 후 통과.
- **배선 7번(커버리지 맵).** `ExtractorFactLines`의 `AddRange` 두 줄을 지우니 3건이 실패했다 —
  `CoverageMapComposerTests.Compose_TransactionStatement_ShouldCountAsExtractorMaterial` ·
  `Compose_SetAssignment_ShouldCountAsExtractorMaterial` ·
  `CoverageMapGoldenTests.Requirement1_CurrentEdition_SpecMissingShouldMatchTransitionWindowCount`.
  원복 후 통과.
- **계약 1번(null 체인).** `From`의 AND 사슬에서 `transactionBoundaries`·`setAssignments` 두 항을
  지우니 `SpecExpectationsTransactionAndSetTests`의 2건(`From_TransactionOnlyProcedure_ShouldNotReturnNull` ·
  `From_SetAssignmentOnlyProcedure_ShouldNotReturnNull`)이 실패했다. 원복 후 통과.

### 최종 게이트 실측

```
dotnet clean && dotnet build   →  warning CS 유일 건수 0 · 오류 0
dotnet test                    →  실패 0 · 건너뜀 0 · 통과 2740
코퍼스 전수 스윕 (31쌍)         →  거짓 양성 0 · 다른 검사 카운트 BASE와 동일
MachineConfirmedTables.All     →  새 헤딩 2개가 맨 끝 (순서 불변 테스트가 잠금)
CacheManager                   →  15 → 16 (main 15, 다른 브랜치 전부 15 이하 확인)
```

### 남은 것

**재생성은 이 회차에서 돌리지 않았다**(계획의 비목표). 따라서 설계서 「미확정 사항」3번 —
*L1 완전일치가 실물 재생성에서 통과하는가* — 는 **확인되지 않은 채 남는다.** 캐시가 16으로
올랐으므로 다음 재생성은 전건이고, 그것이 새 두 검사가 실물에서 처음 도는 자리다.
`reset-l1-check` 스킬의 경고대로 그 회차의 로그에서 `(시도 N/6)` 오류를 반드시 읽어야 한다.

커버리지 맵의 🟥 205는 **재생성 전까지 유효한 전이 상태**다(설계서 §3). 재생성이 끝나면
내려가야 하고, 0에 도달하면 `CoverageMapGoldenTests.TransitionWindowSpecMissing` 상수를 지우고
원래 계약(총계 0)으로 돌아간다.
