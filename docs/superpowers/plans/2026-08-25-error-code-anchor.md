# 오류 코드 앵커 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 검사 B·C가 닿는 코퍼스를 단계 파일 2개(0.6%)에서 197개(60%)로 넓혀 `POQSettleBatch1/S11` 🟠(갱신 9의 조인 키 `YMD`·`UseState` 결측)을 닫는다.

**Architecture:** `DmlScopeExtractor`가 `오류 코드 ↔ 갱신 번호` 표를 **자기 방문자 안에서** 뽑고(채번 주체가 하나여야 번호가 안 어긋난다), `SpecExpectations`가 실어 나르고, `AiService`가 표 뼈대로 강제하고, `MechanicalValidator`가 전사를 대조한다. 그 표를 `SpecStatementFactsExtractor`가 다시 읽어 매핑으로 만들고, `StepSqlStatementReader`가 단계의 `SET @v_currentStepId = -13;`을 코드 앵커로 읽어, 검사 B·C가 둘을 합쳐 Ordinal로 환산한다. 추론은 0 — 전사와 기계적 환산만 한다.

**Tech Stack:** .NET 10.0, `Microsoft.SqlServer.TransactSql.ScriptDom` 180.37.3 (`TSql160Parser`), xUnit 2.9.3

**Spec:** [`docs/superpowers/specs/2026-08-25-error-code-anchor-design.md`](../specs/2026-08-25-error-code-anchor-design.md)

**필수 참조 (구현 전에 읽어라):** `.claude/skills/reset-l1-check/references/authoring-contract.md` — 형제 검사들이 이미 정한 관례. 각 항목의 "실측" 줄은 그것을 어겨서 실제로 난 결함이다.

## Global Constraints

- **`namespace ReSet.Core.Services`**(구현) / **`ReSet.Core.Tests`**(테스트). 한국어 주석, 근거를 남기는 문체.
- **파서 오류 정책:** `new TSql160Parser(true)`. `DmlScopeExtractor.Extract`의 기존 정책을 그대로 따른다(그 메서드는 `fragment == null`만 보고 오류 목록은 `out _`로 버린다 — 새 메서드도 **같은 자리에서 같게** 행동한다. 형제 추출기와 다르다고 바꾸지 마라).
- **추론 금지.** 감싼 조건을 해석하지 않고, 코드에 의미를 붙이지 않고, 연속 범위로 접지 않는다. 원문 슬라이스만.
- **`MachineConfirmedTables.All`의 순서를 흔들지 마라.** 그 파일 주석: *"목록의 순서가 곧 Critic 프롬프트에 실리는 순서다. 프롬프트 접두사 캐시가 바이트 일치로 걸리므로 순서를 흔들지 마십시오."* **맨 끝에 append** — 11번째다.
- **`AiService`의 표 렌더는 `BuildMachineFactBlockLines` 안에서만.** 밖에서 직접 배선하면 진입점이 둘이 되어 "표 하나가 늘 때 한 갈래만 조용히 못 받는 회귀"를 막던 보호가 사라진다.
- **빌드 경고 상한 0.** `dotnet build 2>&1 | grep -cE "warning CS"`가 **0**. `dotnet test` 실패 0·건너뜀 0.
- **`output/` 쓰기 금지.** 읽기만 한다.
- **캐시 인상은 Task 9에서만.** 그 전에 Task 8 코퍼스 스윕으로 오탐을 먼저 잡는다 — 거짓 양성을 안은 채 전건을 돌리면 그 오탐이 곧바로 재시도 소진으로 번진다(`reset-l1-check` SKILL.md).
- **프로브·실측 파일은 반드시 자기 워크트리 안에서 만들고 끝나면 지운다.** (2026-08-24 격리 위반 사건 — 메인 체크아웃에 남은 프로브가 통합 검증을 오염시켰다.)

### 코퍼스 기준선 (2026-08-25 실측)

```
output/.sp_cache_index.json  FormatVersion 분포 → {15: 31}
코드 CurrentCacheFormatVersion                  → 16
```

**세대 15→16 재생성이 아직 안 돌았다.** 그래서 이 회차가 17로 올려도 재생성은 한 번이다. 이 창은 누가 먼저 16으로 재생성을 돌리면 닫힌다.

---

## File Structure

| 파일 | 책임 |
|---|---|
| `src/ReSet.Core/Services/DmlScopeExtractor.cs` (수정) | `ErrorCodeFact` + `ExtractErrorCodes` + 방문자 안 수집 (Task 1) |
| `src/ReSet.Core/Services/SpecExpectations.cs` (수정) | 속성 1개 + `From()` 배선 + **null 체인 1항** (Task 2) |
| `src/ReSet.Core/Services/MachineConfirmedTables.cs` (수정) | `All` 맨 끝에 11번째 항목 (Task 2) |
| `src/ReSet.Core/Services/AiService.cs` (수정) | 표 뼈대 1개, `BuildMachineFactBlockLines` 안 (Task 2) |
| `src/ReSet.Core/Services/MechanicalValidator.cs` (수정) | 전사 대조 검사 1개 + `ErrorType` 1개 (Task 3) · 검사 B·C 환산 (Task 6) |
| `src/ReSet.Core/Services/SpecStatementFactsExtractor.cs` (수정) | 새 표를 읽어 매핑을 `SpecStatementFacts`에 (Task 4) |
| `src/ReSet.Core/Services/StepSqlStatementReader.cs` (수정) | 코드 앵커 판독 (Task 5) |
| `src/ReSet.Core/Services/CoverageMapComposer.cs` (수정) | `ExtractorFactLines`에 컬렉션 1개 (Task 7) |
| `src/ReSet.Core/Services/CacheManager.cs` (수정) | `CurrentCacheFormatVersion` 16 → 17 (Task 9) |
| `tests/ReSet.Core.Tests/DmlScopeExtractorErrorCodeTests.cs` (신규) | Task 1 |
| `tests/ReSet.Core.Tests/MachineConfirmedTablesExpansionTests.cs` (수정) | Task 2 |
| `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` (수정) | Task 3 · 6 |
| `tests/ReSet.Core.Tests/SpecStatementFactsExtractorTests.cs` (수정) | Task 4 |
| `tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs` (수정) | Task 5 |
| `tests/ReSet.Core.Tests/CoverageMapComposerTests.cs` (수정) | Task 7 |

**의존:** Task 1 → 2·3·4 / Task 4·5 → 6 / Task 6 → 8 → 9 / Task 7은 Task 1 뒤 아무 때나.

---

### Task 1: `ErrorCodeFact`와 `ExtractErrorCodes`

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs`
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorErrorCodeTests.cs` (신규)

**Interfaces:**
- Consumes: `Microsoft.SqlServer.TransactSql.ScriptDom` 만
- Produces:
  - `public sealed record ErrorCodeFact(string Operation, int StatementOrdinal, string Code, string Variable)`
  - `DmlScopeExtractor.ErrorCodeTableHeading` (`public const string`)
  - `DmlScopeExtractor.ExtractErrorCodes(string? ddlText, string dateParameterName)` → `IReadOnlyList<ErrorCodeFact>`

**왜 별도 추출기 클래스를 만들지 않는가 (읽고 시작해라):** 갱신 번호를 채번하는 주체는 `DmlScopeVisitor`의 `NextOrdinal`이고, 그 파일 주석이 *"번호를 집는 자리는 다섯"*이라 적었다. 밖에서 같은 번호를 다시 세면 두 채번이 조용히 어긋나고, 어긋난 매핑은 **엉뚱한 행과 대조하는 거짓 시정 지시**가 된다(태스크 4 리뷰가 이미 같은 모양의 Critical을 냈다). **같은 방문자 안에서 수집하고, 새 `ExtractErrorCodes`는 그 방문자를 돌려 두 번째 컬렉션을 읽기만 한다.**

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/DmlScopeExtractorErrorCodeTests.cs`:

```csharp
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class DmlScopeExtractorErrorCodeTests
    {
        [Fact]
        public void ExtractErrorCodes_GuardAfterEachUpdate_ShouldPairOrdinalWithCode()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -1
        RETURN
    END
    UPDATE A SET A.Y = 2 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -2
        RETURN
    END
END";

            var facts = DmlScopeExtractor.ExtractErrorCodes(ddl, "@pi_strYMD");

            Assert.Equal(2, facts.Count);
            Assert.Equal("UPDATE", facts[0].Operation);
            Assert.Equal(1, facts[0].StatementOrdinal);
            Assert.Equal("-1", facts[0].Code);
            Assert.Equal("@po_intRetVal", facts[0].Variable);
            Assert.Equal(2, facts[1].StatementOrdinal);
            Assert.Equal("-2", facts[1].Code);
        }

        [Fact]
        public void ExtractErrorCodes_NoGuard_ShouldProduceNoRowForThatStatement()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    UPDATE A SET A.Y = 2 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN
        SET @po_intRetVal = -2
    END
END";

            var facts = DmlScopeExtractor.ExtractErrorCodes(ddl, "@pi_strYMD");

            // 가드가 없는 UPDATE 1은 행이 없다. 침묵이지 실패가 아니다.
            var single = Assert.Single(facts);
            Assert.Equal(2, single.StatementOrdinal);
            Assert.Equal("-2", single.Code);
        }

        [Fact]
        public void ExtractErrorCodes_NextSiblingIsAnotherDml_ShouldNotReachPastIt()
        {
            // UPDATE 1 다음 형제가 IF가 아니라 UPDATE 2다. UPDATE 1은 행이 없어야
            // 하며, 뒤쪽 IF의 코드를 훔쳐 오면 안 된다.
            const string ddl = @"CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    UPDATE A SET A.Y = 2 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN
        SET @po_intRetVal = -9
    END
END";

            var facts = DmlScopeExtractor.ExtractErrorCodes(ddl, "@pi_strYMD");

            Assert.DoesNotContain(facts, f => f.StatementOrdinal == 1);
        }

        [Fact]
        public void ExtractErrorCodes_InsertAndDelete_ShouldNumberPerKind()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P @po_intRetVal INT OUTPUT AS
BEGIN
    INSERT INTO dbo.T (X) VALUES (1)
    IF @@ERROR <> 0 BEGIN SET @po_intRetVal = -5 END
    DELETE FROM dbo.T
    IF @@ERROR <> 0 BEGIN SET @po_intRetVal = -6 END
END";

            var facts = DmlScopeExtractor.ExtractErrorCodes(ddl, "@pi_strYMD");

            Assert.Contains(facts, f => f.Operation == "INSERT" && f.StatementOrdinal == 1 && f.Code == "-5");
            Assert.Contains(facts, f => f.Operation == "DELETE" && f.StatementOrdinal == 1 && f.Code == "-6");
        }

        [Fact]
        public void ExtractErrorCodes_NonNumericAssignment_ShouldBeIgnored()
        {
            // 가드 안이라도 정수 리터럴이 아니면 담지 않는다 - 표는 코드를 담지
            // 식을 담지 않는다.
            const string ddl = @"CREATE PROCEDURE dbo.P @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T AS A
    IF @@ERROR <> 0 BEGIN
        SET @po_intRetVal = @@ERROR
    END
END";

            Assert.Empty(DmlScopeExtractor.ExtractErrorCodes(ddl, "@pi_strYMD"));
        }

        [Fact]
        public void ExtractErrorCodes_UnparsableDdl_ShouldReturnEmpty()
        {
            Assert.Empty(DmlScopeExtractor.ExtractErrorCodes("NOT SQL AT ALL (((", "@pi_strYMD"));
            Assert.Empty(DmlScopeExtractor.ExtractErrorCodes(null, "@pi_strYMD"));
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorErrorCodeTests"`
Expected: 컴파일 실패 — `ExtractErrorCodes`·`ErrorCodeFact`가 없다.

- [ ] **Step 3: `ErrorCodeFact` 레코드와 표 제목을 더한다**

`DmlScopeFact` 레코드 정의(`DmlScopeExtractor.cs:152-166`) **바로 뒤**에 붙인다:

```csharp
    /// <param name="Operation">"INSERT", "UPDATE", "DELETE" 중 하나. `DmlScopeFact.Operation`과 같은 어휘다.</param>
    /// <param name="StatementOrdinal">
    /// 종류별 순번. <c>DmlScopeVisitor.NextOrdinal</c>이 집는 바로 그 번호이므로
    /// DML 범위 표의 「문장」 칸(`UPDATE 9`)과 글자까지 같은 것을 가리킨다.
    ///
    /// [왜 이 레코드가 DmlScopeExtractor 안에 사는가] 번호를 집는 주체가 하나여야
    /// 한다. 밖에서 다시 세면 두 채번이 조용히 어긋나고, 어긋난 매핑은 엉뚱한 행과
    /// 대조하는 거짓 시정 지시가 된다.
    /// </param>
    /// <param name="Code">정수 리터럴 원문 그대로("-1"). 부호를 떼거나 정규화하지 않는다.</param>
    /// <param name="Variable">대입 대상 변수 원문("@po_intRetVal").</param>
    public sealed record ErrorCodeFact(
        string Operation,
        int StatementOrdinal,
        string Code,
        string Variable);
```

표 제목은 기존 넷 옆(`DmlScopeExtractor.cs:474` 다음 줄)에 더한다:

```csharp
        public const string ErrorCodeTableHeading = "### 오류 코드 (기계 확정 — 수정 금지)";
```

- [ ] **Step 4: 방문자에 수집 자리를 만든다**

`DmlScopeVisitor` 클래스 안에 컬렉션과 사전을 더한다:

```csharp
            /// <summary>(Operation, Ordinal) → 오류 코드. Step 5의 사전 계산이 채운다.</summary>
            public List<ErrorCodeFact> ErrorCodeFacts { get; } = new();

            /// <summary>
            /// DML 문장의 <c>*Specification</c> 객체 → 그 문장 바로 뒤 가드에서 읽은
            /// (코드, 변수). <see cref="ExplicitVisit(StatementList)"/>가 자식을 방문하기
            /// **전에** 채우므로, 각 Specification 방문 시점엔 이미 준비돼 있다.
            /// </summary>
            private readonly Dictionary<TSqlFragment, (string Code, string Variable)> _guardCodes = new();
```

- [ ] **Step 5: `StatementList`에서 형제 짝짓기를 사전 계산한다**

`DmlScopeVisitor` 안에 더한다. **`base.ExplicitVisit(node)`를 마지막에 부르는 순서가 중요하다** — 자식(각 `*Specification`)이 방문되기 전에 사전이 채워져야 한다.

```csharp
            /// <summary>
            /// 문장 목록을 형제 순서로 훑어 "DML 바로 다음 형제가 IF면 그 본문의
            /// 오류 코드를 그 DML에 귀속"시킨다.
            ///
            /// [왜 여기인가] 이 방문자는 `UpdateSpecification`을 방문하지 `UpdateStatement`를
            /// 방문하지 않는다. Specification에서는 형제 문장을 볼 수 없다. 문장 목록이
            /// 형제 순서를 아는 유일한 자리다.
            ///
            /// [왜 "바로 다음 형제"인가] 사이에 다른 문장이 끼면 귀속을 포기한다. 뒤쪽
            /// 가드까지 훑어 가면 가드 없는 DML이 다음 DML의 코드를 훔친다.
            /// </summary>
            public override void ExplicitVisit(StatementList node)
            {
                var statements = node.Statements;
                for (var i = 0; i + 1 < statements.Count; i++)
                {
                    var spec = DmlSpecificationOf(statements[i]);
                    if (spec == null) continue;

                    if (statements[i + 1] is not IfStatement guard) continue;

                    var code = FindErrorCodeAssignment(guard.ThenStatement);
                    if (code == null) continue;

                    _guardCodes[spec] = code.Value;
                }

                base.ExplicitVisit(node);
            }

            /// <summary>DML 문장이면 그 Specification을, 아니면 null을 준다.</summary>
            private static TSqlFragment? DmlSpecificationOf(TSqlStatement statement) => statement switch
            {
                UpdateStatement u => u.UpdateSpecification,
                InsertStatement ins => ins.InsertSpecification,
                DeleteStatement d => d.DeleteSpecification,
                _ => null
            };

            /// <summary>
            /// 가드 본문에서 `SET @v = &lt;정수 리터럴&gt;` 대입을 찾는다. 정확히 하나일
            /// 때만 돌려준다 - 둘 이상이면 어느 것이 이 문장의 코드인지 귀속할 수 없다.
            ///
            /// [왜 리터럴 텍스트로 판정하는가] ScriptDom은 `-1`을 `UnaryExpression`으로도
            /// `IntegerLiteral`로도 낼 수 있다. AST 모양을 맞히는 대신 원문 토큰을 이어
            /// 붙여 정수인지만 본다 - 이 표의 계약이 "원문 슬라이스"이므로 판정도 원문에서
            /// 하는 것이 일관된다.
            /// </summary>
            private static (string Code, string Variable)? FindErrorCodeAssignment(TSqlStatement? body)
            {
                if (body == null) return null;

                var finder = new GuardAssignmentFinder();
                body.Accept(finder);
                return finder.Found.Count == 1 ? finder.Found[0] : null;
            }

            private sealed class GuardAssignmentFinder : TSqlFragmentVisitor
            {
                public List<(string Code, string Variable)> Found { get; } = new();

                public override void Visit(SetVariableStatement node)
                {
                    var variable = node.Variable?.Name;
                    if (string.IsNullOrWhiteSpace(variable)) return;

                    var text = TextOf(node.Expression).Trim();
                    if (!IntegerLiteralPattern.IsMatch(text)) return;

                    Found.Add((text, variable!));
                }
            }
```

`DmlScopeExtractor` 클래스 수준에 정규식을 더한다(다른 `private static readonly Regex` 옆):

```csharp
        /// 부호 있는 정수 리터럴만. 공백을 낀 `- 1`도 파서가 그렇게 낼 수 있어 허용한다.
        private static readonly Regex IntegerLiteralPattern = new(@"^-\s*\d+$|^\d+$", RegexOptions.Compiled);
```

- [ ] **Step 6: 세 `*Specification` 방문자에서 귀속을 확정한다**

기존 세 자리(`ExplicitVisit(InsertSpecification)` · `(UpdateSpecification)` · `(DeleteSpecification)`)에서 `NextOrdinal` 바로 뒤에 한 줄씩 더한다. **`UpdateSpecification`의 예:**

```csharp
            public override void ExplicitVisit(UpdateSpecification node)
            {
                var ordinal = NextOrdinal("UPDATE");
                CollectFrom("UPDATE", ordinal, node.FromClause);
                CollectStatementSubqueries("UPDATE", ordinal, node);
                RecordTargetHint("UPDATE", ordinal, node.Target);
                RecordErrorCode("UPDATE", ordinal, node);   // <- 더하는 줄

                base.ExplicitVisit(node);
            }
```

`INSERT`·`DELETE`도 같은 자리에 `RecordErrorCode("INSERT", ordinal, node);` · `RecordErrorCode("DELETE", ordinal, node);`를 더한다. 헬퍼:

```csharp
            /// <summary>사전 계산된 가드 코드가 있으면 이 문장의 번호로 확정한다.</summary>
            private void RecordErrorCode(string operation, int ordinal, TSqlFragment specification)
            {
                if (!_guardCodes.TryGetValue(specification, out var found)) return;

                ErrorCodeFacts.Add(new ErrorCodeFact(operation, ordinal, found.Code, found.Variable));
            }
```

- [ ] **Step 7: `ExtractErrorCodes` 진입점을 더한다**

`ExtractLockHints`(`DmlScopeExtractor.cs:699`) 옆에 형제 메서드로 놓는다:

```csharp
        /// <summary>
        /// 「오류 코드」 표의 재료. DML 문장 바로 뒤 `IF` 가드 본문의 정수 리터럴 대입을
        /// 그 문장의 종류별 순번에 귀속시킨다.
        ///
        /// [왜 Extract와 같은 방문자를 다시 돌리는가] 번호를 집는 주체가 하나여야
        /// 하기 때문이다. 파스를 한 번 더 하는 비용은 형제 `Extract*` 메서드들이 이미
        /// 지불하고 있는 것과 같다.
        /// </summary>
        public static IReadOnlyList<ErrorCodeFact> ExtractErrorCodes(string? ddlText, string dateParameterName)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<ErrorCodeFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null) return Array.Empty<ErrorCodeFact>();

                var visitor = new DmlScopeVisitor(dateParameterName ?? string.Empty);
                fragment.Accept(visitor);
                return visitor.ErrorCodeFacts;
            }
            catch (Exception)
            {
                return Array.Empty<ErrorCodeFact>();
            }
        }
```

- [ ] **Step 8: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorErrorCodeTests"`
Expected: 6개 전부 PASS.

- [ ] **Step 9: 실물 DDL로 확인한다 (계획서의 핵심 검증)**

워크트리 안 스크래치 콘솔에서 `UP_UTIL_SETTLE_EXPECT_PROC`를 돌린다:

```bash
sed -n '195,215p' output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/docs/Spec.md
```

기대: `ExtractErrorCodes`가 **11행**을 내고 코드가 순서대로
`-1, -2, -3, -4, -5, -10, -11, -12, -13, -15, -17`이며, `UPDATE 9`의 코드가 **`-13`**이다.
(이 목록은 그 명세서 `docs/Spec.md:80`의 산문과 이미 일치함을 태스크 22가 대조했다.)

**측정 결과를 적어 둔다** — Task 8 스윕이 이 수치를 기준선으로 쓴다. 스크래치는 끝나면 지운다.

- [ ] **Step 10: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorErrorCodeTests.cs
git commit -m "feat: DML 문장 뒤 오류 가드에서 코드를 뽑아 갱신 번호에 귀속시킨다"
```

---

### Task 2: 재료를 프롬프트까지 실어 나른다

**Files:**
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs`
- Modify: `src/ReSet.Core/Services/MachineConfirmedTables.cs`
- Modify: `src/ReSet.Core/Services/AiService.cs`
- Test: `tests/ReSet.Core.Tests/MachineConfirmedTablesExpansionTests.cs` (수정)

**Interfaces:**
- Consumes: `ErrorCodeFact`, `DmlScopeExtractor.ExtractErrorCodes`, `DmlScopeExtractor.ErrorCodeTableHeading` (Task 1)
- Produces: `SpecExpectations.ErrorCodes` (`IReadOnlyList<ErrorCodeFact>`)

**null 체인이 이 태스크의 함정이다.** `SpecExpectations.From()`은 재료가 전부 비면 `null`을 돌려준다. 새 항을 그 조건에 안 더하면, **재료가 오류 코드 하나뿐인 SP에서 `From`이 null을 내고 검사가 한 번도 안 돈다.** `ee39d89`가 두 표를 넣을 때 그 파일에 남긴 주석이 같은 함정을 두 번 적었다 — 읽고 따라 해라.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MachineConfirmedTablesExpansionTests.cs`에 더한다:

```csharp
        [Fact]
        public void All_ShouldContainErrorCodeTableAtTheEnd()
        {
            var last = MachineConfirmedTables.All[^1];

            Assert.Equal(DmlScopeExtractor.ErrorCodeTableHeading, last.Heading);
        }

        [Fact]
        public void From_WhenErrorCodesAreTheOnlyMaterial_ShouldNotReturnNull()
        {
            // 재료가 오류 코드 하나뿐인 SP가 성립한다. 이 항을 null 체인에
            // 빠뜨리면 From이 null을 돌려주고 오류 코드 검사가 한 번도 안 돈다.
            const string ddl = @"CREATE PROCEDURE dbo.P @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T AS A
    IF @@ERROR <> 0 BEGIN SET @po_intRetVal = -1 END
END";

            Assert.NotEmpty(DmlScopeExtractor.ExtractErrorCodes(ddl, "@pi_strYMD"));
        }
```

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MachineConfirmedTablesExpansionTests"`
Expected: `All_ShouldContainErrorCodeTableAtTheEnd`가 FAIL — 마지막 항목이 아직 `SetAssignmentExtractor.TableHeading`이다.

- [ ] **Step 3: `SpecExpectations`에 속성과 배선을 더한다**

`SetAssignments` 속성 바로 뒤에:

```csharp
        /// <summary>DML 문장별 오류 코드. 갱신 번호와 코드만 담는다.</summary>
        public IReadOnlyList<ErrorCodeFact> ErrorCodes { get; init; }
            = Array.Empty<ErrorCodeFact>();
```

`From()` 안, `setAssignments` 지역 변수 옆에:

```csharp
            var errorCodes = DmlScopeExtractor.ExtractErrorCodes(spDef.DdlText, dateParameterName);
```

**`dateParameterName`은 `From()`이 이미 다른 `DmlScopeExtractor` 호출에 넘기는 값과 같은 것을 쓴다** — 그 자리를 찾아 같은 식을 넘겨라. 새 이름을 만들지 마라.

null 체인 조건에 항을 더한다(`&& setAssignments.Count == 0` 바로 뒤):

```csharp
                // errorCodes도 중복항이 아니다 - 오류 가드만 있고 다른 재료가 없는 SP가
                // 성립한다. 이 항을 빠뜨리면 재료가 이것 하나뿐인 픽스처에서 From이
                // null을 돌려주고 오류 코드 검사가 한 번도 돌지 않는다.
                && errorCodes.Count == 0
```

반환 객체 초기화에 `SetAssignments = setAssignments,` 다음 줄로:

```csharp
                ErrorCodes = errorCodes,
```

- [ ] **Step 4: `MachineConfirmedTables.All` 맨 끝에 항목을 더한다**

`SetAssignmentExtractor.TableHeading` 항목 **뒤에** append한다(순서를 흔들면 프롬프트 접두사 캐시가 깨진다):

```csharp
            new MachineConfirmedTable(
                DmlScopeExtractor.ErrorCodeTableHeading,
                "each row pairs a DML statement number with the literal error code its guard sets; "
                + "both come from the DDL and are checkable"),
```

- [ ] **Step 5: `AiService`에 표 뼈대를 더한다**

`BuildSetAssignmentTableLines` 옆에 형제 메서드로:

```csharp
        /// <summary>
        /// 「오류 코드」 표를 렌더한다. 코드 리터럴을 원문 그대로 싣는다 - 연속 범위로
        /// 접으면(`-1~-23`) 규약 9가 금지하는 바로 그 형태가 되고 갱신 번호와의 대응이
        /// 사라진다.
        /// </summary>
        private static List<string> BuildErrorCodeTableLines(
            IReadOnlyList<ErrorCodeFact> facts)
        {
            var lines = new List<string>
            {
                "   [CRITICAL ERROR CODE TABLE] The following statement-to-error-code pairs are MACHINE-DERIVED from the source DDL. Copy this table verbatim under the exact heading shown. Never merge rows into ranges and never renumber - the pairing is the contract.",
                $"   {DmlScopeExtractor.ErrorCodeTableHeading}",
                "   | 문장 | 오류 코드 | 설정 대상 |",
                "   | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                lines.Add($"   | {fact.Operation} {fact.StatementOrdinal} | {EscapeTableCell(fact.Code)} | {EscapeTableCell(fact.Variable)} |");
            }

            lines.Add("");
            return lines;
        }
```

`BuildMachineFactBlockLines` 안에서 변수 대입 표를 싣는 자리를 찾아, 그 바로 뒤에 같은 조건(`caseBranchPresentation == MachineFactPresentation.Table`)으로 호출을 더한다. **그 메서드 밖에서 배선하지 마라.**

- [ ] **Step 6: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MachineConfirmedTablesExpansionTests"`
Expected: PASS.

- [ ] **Step 7: 전체 테스트로 회귀를 본다**

Run: `dotnet build 2>&1 | grep -cE "warning CS" && dotnet test 2>&1 | tail -3`
Expected: 경고 0, 실패 0. **골든 테스트가 깨지면** — `CoverageMapGoldenTests`·`AiServiceTests_Rich`가 프롬프트 전문을 고정하고 있을 수 있다. 표가 하나 늘었으니 골든을 갱신하는 것이 맞다. 갱신 diff를 눈으로 읽어 **새 표 블록만** 늘었는지 확인해라.

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core/Services/SpecExpectations.cs src/ReSet.Core/Services/MachineConfirmedTables.cs src/ReSet.Core/Services/AiService.cs tests/
git commit -m "feat: 오류 코드 표를 기계 확정 목록과 프롬프트에 싣는다"
```

---

### Task 3: L1이 오류 코드 표의 전사를 대조한다

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` (수정)

**Interfaces:**
- Consumes: `SpecExpectations.ErrorCodes` (Task 2)
- Produces: `ErrorType.ErrorCodeTableMissing`

**따라 할 본보기:** `ee39d89`가 넣은 `ErrorType.SetAssignmentTableMissing` 검사. `git show ee39d89 -- src/ReSet.Core/Services/MechanicalValidator.cs`로 그 검사의 전체 모양(표 찾기 → 행 파싱 → 기대와 대조 → 메시지)을 읽고 **같은 구조로** 쓴다. 새 구조를 발명하지 마라.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`MechanicalValidatorTests.cs` 말미에:

```csharp
        [Fact]
        public void ErrorCodeTable_WhenSpecOmitsARow_ShouldReportMissing()
        {
            var expectations = new SpecExpectations
            {
                ErrorCodes = new[]
                {
                    new ErrorCodeFact("UPDATE", 1, "-1", "@po_intRetVal"),
                    new ErrorCodeFact("UPDATE", 2, "-2", "@po_intRetVal"),
                }
            };

            const string spec = @"## 로직 흐름 요약

### 오류 코드 (기계 확정 — 수정 금지)

| 문장 | 오류 코드 | 설정 대상 |
| :--- | :--- | :--- |
| UPDATE 1 | -1 | @po_intRetVal |
";

            var result = new MechanicalValidator().Validate(spec, expectations);

            Assert.Contains(result.Errors, e => e.Type == ErrorType.ErrorCodeTableMissing);
        }

        [Fact]
        public void ErrorCodeTable_WhenTranscribedVerbatim_ShouldNotReport()
        {
            var expectations = new SpecExpectations
            {
                ErrorCodes = new[] { new ErrorCodeFact("UPDATE", 9, "-13", "@po_intRetVal") }
            };

            const string spec = @"## 로직 흐름 요약

### 오류 코드 (기계 확정 — 수정 금지)

| 문장 | 오류 코드 | 설정 대상 |
| :--- | :--- | :--- |
| UPDATE 9 | -13 | @po_intRetVal |
";

            var result = new MechanicalValidator().Validate(spec, expectations);

            Assert.DoesNotContain(result.Errors, e => e.Type == ErrorType.ErrorCodeTableMissing);
        }

        [Fact]
        public void ErrorCodeTable_WhenThereAreNoFacts_ShouldNotRequireTheTable()
        {
            // 오류 가드가 없는 SP는 표가 없는 것이 정상이다. 요구하면 만족 불가능한
            // 지시가 되어 재시도를 소진한다(2026-08-24 검사 A C1과 같은 부류).
            var expectations = new SpecExpectations();

            var result = new MechanicalValidator().Validate("## 로직 흐름 요약\n", expectations);

            Assert.DoesNotContain(result.Errors, e => e.Type == ErrorType.ErrorCodeTableMissing);
        }
```

**주의:** `new MechanicalValidator().Validate(spec, expectations)`의 정확한 시그니처는 같은 파일의 이웃 테스트에서 확인해 맞춘다 — 위 호출은 모양만 보인 것이다.

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ErrorCodeTable"`
Expected: 컴파일 실패 — `ErrorType.ErrorCodeTableMissing`이 없다.

- [ ] **Step 3: `ErrorType`에 항목을 더하고 검사를 쓴다**

`ErrorType` 열거형에 `ErrorCodeTableMissing`을 더한다(기존 `SetAssignmentTableMissing` 옆).

검사는 `SetAssignmentTableMissing` 검사를 본떠 쓴다. **불변식 둘을 지켜라:**
1. `expectations.ErrorCodes.Count == 0`이면 **표를 요구하지 않는다**(조기 반환).
2. 메시지가 인쇄하는 근거와 판정 근거가 **같아야** 한다 — 검사 E가 2026-08-24에 이 규칙을 어겨 129건 중 70건에 거짓 문장을 인쇄했다.

- [ ] **Step 4: 검사를 등록한다**

`MechanicalValidator.cs:178` 부근의 검사 등록 목록에 `SafeCheck` 한 줄을 더한다. 이웃 등록 줄의 모양을 그대로 따른다.

- [ ] **Step 5: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ErrorCodeTable"`
Expected: 3개 PASS.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: 오류 코드 표의 전사를 L1이 대조한다"
```

---

### Task 4: `SpecStatementFactsExtractor`가 매핑을 읽는다

**Files:**
- Modify: `src/ReSet.Core/Services/SpecStatementFactsExtractor.cs`
- Test: `tests/ReSet.Core.Tests/SpecStatementFactsExtractorTests.cs` (수정)

**Interfaces:**
- Consumes: `DmlScopeExtractor.ErrorCodeTableHeading` (Task 1)
- Produces: `SpecStatementFacts.ErrorCodeToOrdinal` → `IReadOnlyDictionary<string, (string Kind, int Ordinal)>` (키는 코드 원문, 예 `"-13"`)

**왜 사전인가:** 단계 쪽은 코드(`-13`)를 갖고 Ordinal을 찾는다. 방향이 코드 → 번호다.

**중복 코드 처리:** 같은 코드가 두 문장에 붙으면 **그 코드를 사전에서 뺀다**(넣고 덮어쓰지 마라). 귀속할 수 없는 코드는 없는 것과 같다 — "귀속할 수 없으면 침묵"이 이 저장소의 규약이다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        [Fact]
        public void Extract_ErrorCodeTable_ShouldMapCodeToOrdinal()
        {
            const string spec = @"### 오류 코드 (기계 확정 — 수정 금지)

| 문장 | 오류 코드 | 설정 대상 |
| :--- | :--- | :--- |
| UPDATE 9 | -13 | @po_intRetVal |
| UPDATE 10 | -15 | @po_intRetVal |
";

            var facts = SpecStatementFactsExtractor.Extract(
                new[] { ("dbo.UP_X.md", spec) });

            var map = facts["UP_X"].ErrorCodeToOrdinal;

            Assert.Equal(("UPDATE", 9), map["-13"]);
            Assert.Equal(("UPDATE", 10), map["-15"]);
        }

        [Fact]
        public void Extract_DuplicateErrorCode_ShouldDropItEntirely()
        {
            const string spec = @"### 오류 코드 (기계 확정 — 수정 금지)

| 문장 | 오류 코드 | 설정 대상 |
| :--- | :--- | :--- |
| UPDATE 3 | -9 | @po_intRetVal |
| UPDATE 7 | -9 | @po_intRetVal |
";

            var facts = SpecStatementFactsExtractor.Extract(
                new[] { ("dbo.UP_X.md", spec) });

            Assert.DoesNotContain("-9", facts["UP_X"].ErrorCodeToOrdinal.Keys);
        }

        [Fact]
        public void Extract_NoErrorCodeTable_ShouldGiveEmptyMap()
        {
            var facts = SpecStatementFactsExtractor.Extract(
                new[] { ("dbo.UP_X.md", "## 로직 흐름 요약\n") });

            Assert.Empty(facts["UP_X"].ErrorCodeToOrdinal);
        }
```

**주의:** `Extract`의 키 규약은 `BareObjectName`이다(Task 17 C3). 위 테스트의 `"UP_X"` 키가 그 규약과 맞는지 기존 테스트에서 확인해 맞춘다.

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SpecStatementFactsExtractorTests"`
Expected: 컴파일 실패 — `ErrorCodeToOrdinal`이 없다.

- [ ] **Step 3: `SpecStatementFacts`에 속성을 더한다**

`SpecStatementFacts`는 **위치 레코드**다(`SpecStatementFactsExtractor.cs:37`):

```csharp
    public sealed record SpecStatementFacts(
        IReadOnlyList<SpecDmlRow> DmlRows,
        IReadOnlyList<SpecSetTarget> SetTargets,
        IReadOnlyList<SpecLocalVariable> LocalVariables);
```

새 속성은 **생성자 파라미터로 더하지 마라** — 그러면 기존 생성 자리가 전부 깨진다.
본문에 기본값 있는 `init` 속성으로 더한다:

```csharp
        /// <summary>
        /// 오류 코드 원문("-13") → 그 코드를 설정하는 문장의 (종류, 번호).
        ///
        /// [왜 이 방향인가] 단계 지시서는 코드를 갖고 번호를 찾는다.
        /// [중복 코드가 없는 이유] 같은 코드가 두 문장에 붙으면 귀속할 수 없으므로
        /// 아예 담지 않는다 - 덮어쓰면 둘 중 하나가 조용히 틀린 행과 대조된다.
        /// </summary>
        public IReadOnlyDictionary<string, (string Kind, int Ordinal)> ErrorCodeToOrdinal { get; init; }
            = new Dictionary<string, (string, int)>();
```

- [ ] **Step 4: 표를 읽는 코드를 더한다**

`Extract`의 `try` 블록 안, 다른 표를 읽는 자리 옆에 더한다. **표 찾기는 `MarkdownSectionLocator`로 제목 경계를 한정한다** — 직접 줄을 세지 마라(Task 3 브리프가 같은 함정을 적었다: 다른 표에도 `| UPDATE N |` 모양 행이 있다).

「문장」 칸(`UPDATE 9`)을 종류와 번호로 가르고, 「오류 코드」 칸을 키로 삼는다. 중복 키는 **양쪽 다 제거**한다.

- [ ] **Step 5: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SpecStatementFactsExtractorTests"`
Expected: 전부 PASS(기존 테스트 포함 — 이 추출기는 검사 A·D가 이미 쓰므로 회귀가 나면 안 된다).

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/SpecStatementFactsExtractor.cs tests/ReSet.Core.Tests/SpecStatementFactsExtractorTests.cs
git commit -m "feat: 명세서의 오류 코드 표를 코드→갱신 번호 사전으로 읽는다"
```

---

### Task 5: `StepSqlStatementReader`가 코드 앵커를 읽는다

**Files:**
- Modify: `src/ReSet.Core/Services/StepSqlStatementReader.cs`
- Test: `tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs` (수정)

**Interfaces:**
- Produces: `StepSqlStatement.CodeAnchor` (`string?`) — 코드 원문(`"-13"`), 못 읽으면 null

**핵심 규칙 — 태스크 22가 세운 「구간 내 유일성」을 그대로 재사용한다.** `ReadAnchor`가 이미 "직전 문장의 끝 ~ 이 문장의 시작" 구간을 계산한다. 그 **같은 구간**에서 `SET @<변수> = <음수 정수 리터럴>;`을 세어 **정확히 하나일 때만** 잡는다. 새 구간 계산을 만들지 마라 — 두 구간이 갈리면 두 앵커가 서로 다른 자리를 가리킨다.

**왜 음수로 좁히는가:** 규약 6-1이 요구하는 `DECLARE @v_currentStepId INT = 0;` 초기화와, 구간에 흔히 섞이는 `SET @v_cnt = @@ROWCOUNT;` 같은 관용구가 전부 비음수다. 음수로 좁혀야 이들이 후보에서 빠지고 「구간에 정확히 하나」가 실제로 성립한다. 부호를 안 가리면 대부분의 구간에서 후보가 둘 이상이 되어 검사가 통째로 침묵한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        [Fact]
        public void Read_CodeLabelBeforeUpdate_ShouldBeReadAsCodeAnchor()
        {
            const string step = @"```sql
-- -13: 원천카드 수동매입 지급일 및 매입요청일
SET @v_currentStepId = -13;
UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A;
```";

            var statement = Assert.Single(StepSqlStatementReader.Read(step));

            Assert.Equal("-13", statement.CodeAnchor);
        }

        [Fact]
        public void Read_TwoNegativeAssignmentsInInterval_ShouldStaySilent()
        {
            const string step = @"```sql
SET @v_currentStepId = -12;
SET @v_currentStepId = -13;
UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A;
```";

            var statement = Assert.Single(StepSqlStatementReader.Read(step));

            Assert.Null(statement.CodeAnchor);
        }

        [Fact]
        public void Read_NonNegativeAssignmentsInInterval_ShouldNotBeCandidates()
        {
            // 초기화 0과 @@ROWCOUNT 대입이 후보가 되면 「구간에 하나」가 절대
            // 성립하지 않는다.
            const string step = @"```sql
DECLARE @v_currentStepId INT = 0;
SET @v_cnt = @@ROWCOUNT;
SET @v_currentStepId = -13;
UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A;
```";

            var statement = Assert.Single(StepSqlStatementReader.Read(step));

            Assert.Equal("-13", statement.CodeAnchor);
        }

        [Fact]
        public void Read_VariableNameIsNotFixed()
        {
            // 규약 6-1은 @v_currentStepId를 예시로 들 뿐 이름을 못 박지 않는다.
            const string step = @"```sql
SET @v_step = -7;
UPDATE A SET A.X = 1 FROM dbo.T AS A;
```";

            var statement = Assert.Single(StepSqlStatementReader.Read(step));

            Assert.Equal("-7", statement.CodeAnchor);
        }

        [Fact]
        public void Read_UAnchorAndCodeAnchorCanCoexist()
        {
            const string step = @"```sql
/* U13: 카드사 원가 반영 */
SET @v_currentStepId = -13;
UPDATE A SET A.X = 1 FROM dbo.T AS A;
```";

            var statement = Assert.Single(StepSqlStatementReader.Read(step));

            Assert.Equal(13, statement.Anchor);
            Assert.Equal("-13", statement.CodeAnchor);
        }
```

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~StepSqlStatementReaderTests"`
Expected: 컴파일 실패 — `CodeAnchor`가 없다.

- [ ] **Step 3: `StepSqlStatement`에 속성을 더한다**

기존 `Anchor` 옆에 `public string? CodeAnchor { get; init; }`를 더한다. 기존 생성 자리가 안 깨지도록 **선택적**으로 둔다.

- [ ] **Step 4: `ReadCodeAnchor`를 쓴다**

`ReadAnchor`(`StepSqlStatementReader.cs:337` 부근)가 쓰는 **같은 구간 토큰 범위**를 받아 도는 형제 메서드로 쓴다:

```csharp
        /// 음수 정수 리터럴 대입만. `@v = 0`·`@v = @@ROWCOUNT`는 후보가 아니다.
        private static readonly Regex CodeAnchorPattern = new(
            @"\bSET\s+@[A-Za-z_][A-Za-z_0-9]*\s*=\s*(?<code>-\s*\d+)\s*;?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
```

`ReadAnchor`와 **똑같이** "일치가 정확히 1개일 때만 값을, 아니면 null"을 돌려준다.

- [ ] **Step 5: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~StepSqlStatementReaderTests"`
Expected: 전부 PASS(기존 테스트 포함 — 이 판독기는 검사 A가 이미 쓴다).

- [ ] **Step 6: 실물 S11로 확인한다**

`output/Jobs/POQSettleBatch1/agent/steps/S11.md`를 읽어 **11개 문장 전부** `CodeAnchor`가 잡히고 값이 `-1,-2,-3,-4,-5,-10,-11,-12,-13,-15,-17`인지 본다. 스크래치는 워크트리 안에서 만들고 끝나면 지운다.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/StepSqlStatementReader.cs tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs
git commit -m "feat: 단계의 음수 오류 코드 라벨을 구간 내 유일성으로 앵커로 읽는다"
```

---

### Task 6: 검사 B·C가 두 신원 축을 받는다

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` (수정)

**Interfaces:**
- Consumes: `SpecStatementFacts.ErrorCodeToOrdinal` (Task 4), `StepSqlStatement.CodeAnchor` (Task 5)

**바꿀 자리:** `CheckAnchoredStatementFacts`(`MechanicalValidator.cs:5895` 부근)와 `CheckAnchoredStatementExtras`(`:6098` 부근)의 **`anchored` 목록을 만드는 두 줄**이다. 지금은 `statements.Where(s => s.Anchor.HasValue)`다. 여기서 코드 앵커를 환산해 합친다.

**환산 판정표 — 다섯 경우를 전부 구현해야 한다:**

| U-앵커 | 코드 앵커 | 판정 |
| :--- | :--- | :--- |
| 있음 | 없음 | U-앵커 사용 |
| 없음 | 있음 | 코드 앵커를 환산해 사용 |
| 있음 | 있음·일치 | 사용 |
| 있음 | 있음·**불일치** | **그 문장을 후보에서 뺀다** |
| 없음 | 없음 | 후보 아님 |

**낡은 주석을 함께 고쳐라.** `:5905` 부근 주석이 *"이 코퍼스에서는 앵커가 항상 0개로 잡히고"*라고 단정한다 — 태스크 22가 앵커를 살린 뒤로 사실이 아니다. 이 태스크가 그 문단을 현재 사실로 다시 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

판정표 다섯 행 각각에 테스트 하나씩. 예(코드 앵커만 있는 경우):

```csharp
        [Fact]
        public void CheckB_WhenOnlyCodeAnchorExists_ShouldResolveOrdinalAndCompare()
        {
            var facts = MakeFacts(                       // 아래 헬퍼 블록 참고
                ordinal: 9, predicateColumns: new[] { "YMD", "UseState" }, code: "-13");

            const string step = @"```sql
SET @v_currentStepId = -13;
UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A;
```";

            var result = RunAnchoredChecks(step, facts);

            Assert.Contains(result.Errors, e => e.Contains("YMD"));
        }

        [Fact]
        public void CheckB_WhenTwoAnchorsDisagree_ShouldStaySilent()
        {
            var facts = MakeFacts(
                ordinal: 9, predicateColumns: new[] { "YMD" }, code: "-13");

            const string step = @"```sql
/* U4: 앵커는 4를 가리키는데 */
SET @v_currentStepId = -13;   -- 코드 앵커는 9를 가리킨다
UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A;
```";

            var result = RunAnchoredChecks(step, facts);

            Assert.Empty(result.Errors);
        }
```

**헬퍼 — `ValidateBatchStep`은 문장 목록이 아니라 단계 마크다운을 받는다**
(`MechanicalValidator.cs:267`). 문장은 검증기가 안에서 `StepSqlStatementReader.Read`로 읽는다.

```csharp
        private static StepValidationResult RunAnchoredChecks(
            string stepMarkdown, SpecStatementFacts facts)
        {
            var step = new BatchStepPlan
            {
                StepId = "S11",
                LegacyProcedures = new[] { "UP_UTIL_SETTLE_EXPECT_PROC" }
            };

            return new MechanicalValidator().ValidateBatchStep(
                stepMarkdown,
                step,
                knownTableNames: new[] { "TSettleMst" },
                conditionColumnsByProcedure: new Dictionary<string, SpecConditions>(),
                statementFactsByProcedure: new Dictionary<string, SpecStatementFacts>
                {
                    ["UP_UTIL_SETTLE_EXPECT_PROC"] = facts
                });
        }
```

**두 가지를 기존 검사 B 테스트에서 베껴 맞춰라:** `BatchStepPlan`의 실제 속성 이름
(`StepId`·`LegacyProcedures`가 맞는지)과 `SpecStatementFacts` 생성 방식. 후자는 위치
레코드이므로 **객체 초기화만으로는 못 만든다** — 생성자를 부른 뒤 새 속성만 초기화한다:

```csharp
        /// <summary>UPDATE 한 행짜리 명세서 사실 + 그 행을 가리키는 코드 매핑.</summary>
        private static SpecStatementFacts MakeFacts(
            int ordinal, IReadOnlyList<string> predicateColumns, string code)
        {
            return new SpecStatementFacts(
                DmlRows: new[]
                {
                    new SpecDmlRow(
                        Kind: "UPDATE",
                        Ordinal: ordinal,
                        SourceLine: 0,
                        TargetTable: "TSettleMst",
                        PredicateColumns: predicateColumns,
                        JoinKeys: Array.Empty<string>(),
                        GroupBy: Array.Empty<string>(),
                        OrderBy: Array.Empty<string>())
                },
                SetTargets: Array.Empty<SpecSetTarget>(),
                LocalVariables: Array.Empty<SpecLocalVariable>())
            {
                ErrorCodeToOrdinal =
                    new Dictionary<string, (string, int)> { [code] = ("UPDATE", ordinal) }
            };
        }
```

**`SpecDmlRow`의 인자 이름은 `JoinKeys`다**(`JoinColumns`가 아니다 — 그것은
`StepSqlStatement` 쪽 이름이고, 검사 B가 둘을 일부러 갈라 쓴다).

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CheckB_When"`
Expected: FAIL — 코드 앵커가 무시돼 후보가 0개다.

- [ ] **Step 3: 환산 헬퍼를 쓰고 두 자리에 배선한다**

```csharp
        /// <summary>
        /// 문장의 실효 Ordinal을 정한다 - U-앵커와 코드 앵커 둘을 합친다.
        /// 둘 다 있고 서로 다르면 null(귀속 불가 → 침묵). 판정표는 이 태스크의
        /// 계획서와 설계 문서 §3에 있다.
        /// </summary>
        private static int? ResolveOrdinal(
            StepSqlStatement statement,
            IReadOnlyDictionary<string, (string Kind, int Ordinal)> codeMap)
        {
            int? fromCode = null;
            if (statement.CodeAnchor != null
                && codeMap.TryGetValue(statement.CodeAnchor, out var mapped)
                && string.Equals(mapped.Kind, statement.Kind, StringComparison.OrdinalIgnoreCase))
            {
                fromCode = mapped.Ordinal;
            }

            if (statement.Anchor.HasValue && fromCode.HasValue)
            {
                return statement.Anchor.Value == fromCode.Value ? statement.Anchor : null;
            }

            return statement.Anchor ?? fromCode;
        }
```

`CheckAnchoredStatementFacts`·`CheckAnchoredStatementExtras`의 `anchored` 계산을 이 헬퍼로 바꾸고, 이후 `s.Anchor!.Value`를 쓰던 자리를 환산값으로 바꾼다.

- [ ] **Step 4: 낡은 주석을 고친다**

`:5905` 부근 조기 반환 주석에서 *"이 코퍼스에서는 앵커가 항상 0개로 잡히고"* 단정을 지우고, 현재 사실(태스크 22가 U-앵커를, 이 회차가 코드 앵커를 살렸다 — 조기 반환은 **둘 다 없을 때만** 걸린다)로 다시 쓴다.

- [ ] **Step 5: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test 2>&1 | tail -3`
Expected: 실패 0.

- [ ] **Step 6: 실물 회귀 둘을 확인한다**

- `POQSettleBatch1/S11` — 갱신 9의 조인 키 `YMD`·`UseState` 결측이 **새로 잡힌다**.
- `POQSettleBatch1/S07` — 갱신 13의 최상위 WHERE `YMD`·`PGNAME` 결측이 **계속 잡힌다**(회귀 0).

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: 검사 B·C가 U-앵커와 코드 앵커를 합쳐 문장을 귀속시킨다"
```

---

### Task 7: 커버리지 맵에 재료를 싣는다

**Files:**
- Modify: `src/ReSet.Core/Services/CoverageMapComposer.cs`
- Test: `tests/ReSet.Core.Tests/CoverageMapComposerTests.cs` (수정)

**따라 할 본보기:** `ee39d89`가 `ExtractorFactLines`에 트랜잭션 경계·변수 대입 두 컬렉션을 더한 diff. 같은 자리에 세 번째를 더한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        [Fact]
        public void ExtractorFactLines_ShouldCountErrorCodeFacts()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T AS A
    IF @@ERROR <> 0 BEGIN SET @po_intRetVal = -1 END
END";

            var lines = CoverageMapComposer.ExtractorFactLines(ddl, "@pi_strYMD");

            Assert.Contains(lines, l => l.Contains("오류 코드"));
        }
```

**주의:** `ExtractorFactLines`의 정확한 시그니처는 기존 테스트에서 확인해 맞춘다.

- [ ] **Step 2: 실패 확인 → Step 3: 배선 → Step 4: 통과 확인**

Run: `dotnet test --filter "FullyQualifiedName~CoverageMapComposerTests"`

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/CoverageMapComposer.cs tests/ReSet.Core.Tests/CoverageMapComposerTests.cs
git commit -m "feat: 커버리지 맵이 오류 코드 재료를 센다"
```

---

### Task 8: 코퍼스 전수 스윕 — 캐시 인상 **전에** 돈다

**Files:**
- Modify: `docs/known-defects.md`
- 하네스: 워크트리 안 스크래치 콘솔 프로젝트 (**저장소 미커밋, 끝나면 삭제**)

**이 태스크가 게이트다.** 거짓 양성을 안은 채 전건 재생성을 걸면 그 오탐이 재시도 소진으로 번진다.

**닭-달걀과 그 해법(설계 §4):** 새 표는 아직 어느 `Spec.md`에도 없다(코퍼스 세대 15). 하네스가 명세서를 기다리지 말고 **`DmlScopeExtractor.ExtractErrorCodes`를 원본 DDL에 직접 돌려** 매핑을 만들고 그것을 `SpecStatementFacts.ErrorCodeToOrdinal`에 넣어 검증기에 먹인다. 기계 확정 표는 축자 전사 계약이라 **추출기 출력 = 재생성 후 표 내용**이다.

- [ ] **Step 1: 하네스를 만든다**

`VerificationPipelineOrchestrator.GenerateStepSectionWithFloorRetryAsync`의 `_validator.ValidateBatchStep(...)` 호출을 그대로 본뜬다. `stepInterfaces`·`runRowOwnedTables`는 DB 메타데이터가 필요해 로컬에서 못 만드므로 `null`을 넘긴다(그 값이 관여하는 검사는 측정 대상이 아니다 — 2026-08-24 태스크 19가 같은 방식을 썼다).

- [ ] **Step 2: 다섯 수치를 잰다**

```
① 오류 코드 표가 나오는 SP 수 / 31          (설계 「미확정 사항」 1)
② 가드 안에 비음수 코드를 두는 SP 수         (설계 「미확정 사항」 2)
③ TRY…CATCH로 현대화돼 가드가 없는 SP 수     (설계 「미확정 사항」 3)
④ 코드 앵커가 잡히는 단계 파일 수 / 326      (기대: ~197)
⑤ 검사 A·B·C·D·E 각각의 발화량               (기준선: A=20, B=1, C=0, D=52, E=59)
```

- [ ] **Step 3: 표본으로 오탐을 판정한다**

검사 B·C 발화가 **≤30건이면 전건**, **>30건이면 최소 10건**을 단계 파일과 명세서를 직접 열어 확인한다. 진짜/거짓을 좌표와 함께 적는다.

**predicate 쪽 CTE 사각지대를 특히 본다** — 태스크 22가 `HasOpaqueJoinSource`로 조인 키 체크만 접었고, 최상위 WHERE 술어 체크가 같은 사각지대에 걸리는지는 확인된 적이 없다. 걸리는 것으로 판명되면 **그때** 좁히고 근거를 수치로 남긴다. 미리 접지 마라 — 접으면 S07 갱신 13처럼 이 체크가 실제로 잡은 진짜 결함까지 죽는다.

- [ ] **Step 4: 회귀를 확인한다**

검사 A·D·E 발화량이 기준선(A=20, D=52, E=59)과 **같아야** 한다. 다르면 이 회차가 건드리지 않은 자리가 움직인 것이므로 원인을 찾기 전에는 진행하지 않는다.

- [ ] **Step 5: `docs/known-defects.md`에 기록한다**

「축 B 단계 검사」 계열 항목들과 같은 자리에, 같은 문체로 적는다. **은폐하지 않는다** — 못 잰 것은 "미확인"으로 명시하고, 거짓 양성은 좌표와 원인과 되돌릴 지점을 함께 적는다.

- [ ] **Step 6: 하네스를 지우고 트리가 깨끗한지 확인한다**

```bash
git status --short
```
Expected: 스크래치 파일이 하나도 안 잡힌다.

- [ ] **Step 7: 커밋**

```bash
git add docs/known-defects.md
git commit -m "docs: 오류 코드 앵커 코퍼스 스윕 실측을 기록한다 (Task 8)"
```

---

### Task 9: 캐시 인상과 문서 — 스윕이 깨끗할 때만

**Files:**
- Modify: `src/ReSet.Core/Services/CacheManager.cs`
- Modify: `docs/audit-defect-catalog.md`, `docs/known-defects.md`
- Test: `tests/ReSet.Core.Tests/CacheManagerTests.cs` (수정)

**진입 조건 — Task 8이 다음을 만족했을 때만 이 태스크를 시작한다:**
- 검사 B·C 표본 오탐이 확인·기록됐고, 좁힐 것이 있으면 좁혔다.
- 검사 A·D·E 발화량이 기준선과 같다.
- S11 갱신 9가 잡히고 S07 갱신 13이 계속 잡힌다.

**만족하지 않으면 여기서 멈추고 사용자에게 보고한다.** 캐시를 올리면 31개 전건이 다시 만들어진다 — 되돌리기 비싼 지점이다.

- [ ] **Step 1: 테스트를 고친다**

`CacheManagerTests`가 버전을 상수로 고정하고 있다면 16 → 17로 고친다.

- [ ] **Step 2: `CurrentCacheFormatVersion`을 올린다**

`CacheManager.cs:174`의 `16`을 `17`로.

- [ ] **Step 3: 전체 테스트**

Run: `dotnet build 2>&1 | grep -cE "warning CS" && dotnet test 2>&1 | tail -3`
Expected: 경고 0, 실패 0, 건너뜀 0.

- [ ] **Step 4: 문서를 갱신한다**

- `docs/audit-defect-catalog.md` — 11회차 행의 미달 3건 중 **S11이 닫혔음**을 반영한다(닫힘 5 / 유도 2 / 미달 2). §3 본문도 같이 고친다.
- `docs/known-defects.md` — 「문장↔spec 행 대응 재설계(태스크 22)」 항목의 *"S11 갱신 9는 못 닫았다 — 정직한 미해결"*에 **닫힘 표지**를 단다(지우지 말고, 이 회차가 어떻게 닫았는지로 잇는다).

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/CacheManager.cs tests/ReSet.Core.Tests/CacheManagerTests.cs docs/
git commit -m "chore: 캐시 형식 버전을 17로 올리고 S11 닫힘을 문서에 반영한다"
```

---

## 재생성 (계획 밖 — 사용자 확인 후)

캐시 17로 31개 전건을 재생성하면 **세 표(트랜잭션 경계·변수 대입·오류 코드)가 한 번에 실린다.** 이 재생성이 원래 순서의 **2단계(재생성 실측)** 를 겸한다 — S09 🟠 `CROSS APPLY`·S14 🟠 `MAX(ID)` 프롬프트 규약 두 조항이 실제로 닫히는지도 같은 회차에서 잰다.

**AI 호출 비용이 크므로 사용자 확인 없이 돌리지 않는다.**
