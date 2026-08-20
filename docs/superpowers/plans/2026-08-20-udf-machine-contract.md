# 참조 함수 기계 확정 표 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** SP 명세서가 참조 함수의 동작을 서술하지 못하게 하고, 조립기가 채우는 「참조 함수 (기계 확정 — 수정 금지)」 표로 대체한다.

**Architecture:** `DmlScopeExtractor`가 DDL에서 함수 호출 사실을 뽑고(집합 술어·DML 범위와 같은 패턴), `AiService`가 그 사실을 마크다운 표로 렌더해 프롬프트에 넣는다. LLM에게는 함수 동작 서술을 금지하는 계약을 세 곳에 건다. 캐시 포맷 버전을 올려 전 객체를 재분석시킨다.

**Tech Stack:** C# / .NET 10, Microsoft.SqlServer.TransactSql.ScriptDom (`TSql160Parser`), xUnit

**Spec:** `docs/superpowers/specs/2026-08-20-udf-machine-contract-design.md`

## Global Constraints

- 파싱 실패는 **소프트 페일**한다 — `Log.Warning` 후 빈 목록 반환. 예외를 밖으로 던지지 않는다(AGENTS.md 범주 2, 기존 `ExtractSetPredicates`와 동일).
- 표의 **헤더 열 수와 구분자 행 열 수가 반드시 같아야 한다.** 감사에서 이 결함이 두 번 나왔다(GFM이 표를 렌더하지 않는다).
- 새 코드의 주석은 **한국어**로, 기존 파일의 주석 밀도·어투를 따른다.
- 파일 경로는 워크트리 기준 상대 경로다: `/Users/payletter/git-root/ReSet/.claude/worktrees/udf-machine-contract`
- 테스트 실행은 `dotnet test --nologo -v q`. 기존 **2007개가 계속 통과**해야 한다.
- 커밋 메시지 말미에 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

## File Structure

| 파일 | 책임 |
|---|---|
| `src/ReSet.Core/Services/DmlScopeExtractor.cs` | `ReferencedFunctionCallFact` 레코드, `ExtractFunctionCalls`, 표 제목 상수 |
| `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs` | 추출기 단위 테스트 |
| `src/ReSet.Core/Services/AiService.cs` | 표 렌더러, 프롬프트 배선, 함수 서술 금지 계약 3곳 |
| `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs` | 프롬프트 종단 테스트 |
| `src/ReSet.Core/Services/CacheManager.cs` | 캐시 포맷 버전 4 → 5 |

---

### Task 1: 함수 호출 사실 추출기

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs` (레코드는 `SetPredicateFact` 선언 뒤인 96행 근처, 상수는 122행 옆, 추출기는 `ExtractSetPredicates`가 끝나는 183행 뒤)
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs` (파일 끝에 추가)

**Interfaces:**
- Consumes: 없음(이 작업이 첫 단계다)
- Produces:
  - `public sealed record ReferencedFunctionCallFact(string QualifiedName, string Operation, int StatementOrdinal, int Line, string CallExpression)`
  - `public static IReadOnlyList<ReferencedFunctionCallFact> DmlScopeExtractor.ExtractFunctionCalls(string? ddlText, IReadOnlyCollection<string> knownFunctionNames)`
  - `public const string DmlScopeExtractor.ReferencedFunctionTableHeading = "### 참조 함수 (기계 확정 — 수정 금지)"`

`knownFunctionNames`는 **한정자 없는 함수 이름**(예: `UF_GET_ROUND4VAT`)의 집합이다. 호출식의 마지막 식별자 조각을 이 집합과 대소문자 무시로 대조해 내장 함수를 걸러낸다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs` 파일 끝의 마지막 `}` 두 개 앞에 추가한다.

```csharp
        [Fact]
        public void ExtractFunctionCalls_ShouldNumberStatementsLikeDmlScopeTable()
        {
            // EXCEPTION_PROC 실측 형태. DML 범위 표가 "UPDATE 1 / UPDATE 2"로 세는 것과
            // 같은 번호가 나와야 두 표를 나란히 읽을 수 있다.
            var ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    UPDATE A SET A.CLVT = dbo.UF_GET_ROUND4VAT(A.CLCOMM)
    FROM   dbo.TSettleMst A
    WHERE  A.YMD = @pi_strYMD

    UPDATE B SET B.PGVT = dbo.UF_GET_ROUND4VAT(B.PGCOMM)
    FROM   dbo.TSettleMst B
    WHERE  B.YMD = @pi_strYMD
END";

            var facts = DmlScopeExtractor.ExtractFunctionCalls(
                ddl, new[] { "UF_GET_ROUND4VAT" });

            Assert.Equal(2, facts.Count);
            Assert.Equal("UPDATE", facts[0].Operation);
            Assert.Equal(1, facts[0].StatementOrdinal);
            Assert.Equal(2, facts[1].StatementOrdinal);
            // 라인도 DML 범위 표와 같은 기준(호출식이 있는 원본 줄)이어야 한다.
            Assert.Equal(6, facts[0].Line);
            Assert.Equal(10, facts[1].Line);
            Assert.All(facts, f => Assert.Equal("dbo.UF_GET_ROUND4VAT", f.QualifiedName));
        }

        [Fact]
        public void ExtractFunctionCalls_BuiltInFunctions_ShouldBeSkipped()
        {
            // ISNULL/ROUND/CAST는 Dependencies에 없으므로 knownFunctionNames에도 없다.
            // 이 표는 "어느 사용자 함수를 어디서 부르는가"만 답한다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = ROUND(ISNULL(A.X, 0), 0)
    FROM   dbo.T A
END";

            Assert.Empty(DmlScopeExtractor.ExtractFunctionCalls(
                ddl, new[] { "UF_GET_ROUND4VAT" }));
        }

        [Fact]
        public void ExtractFunctionCalls_InlineTvf_ShouldBeCaptured()
        {
            // 파서의 ReferencedFunctions는 인라인 TVF를 싣지 못한다(2026-08-20 실측:
            // EXPECT_PROC·INS_EXTRA 모두 UIF_SettleYMD가 Dependencies에만 있었다).
            // 이 추출기는 Dependencies에서 온 이름 집합을 쓰므로 그 구멍이 닫힌다.
            var ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    UPDATE A SET A.OutYMD = (SELECT OutYMD FROM dbo.UIF_SettleYMD(@pi_strYMD, A.PeriodID))
    FROM   dbo.TSettleMst A
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractFunctionCalls(
                ddl, new[] { "UIF_SettleYMD" }));

            Assert.Equal("dbo.UIF_SettleYMD", fact.QualifiedName);
            Assert.Equal("UPDATE", fact.Operation);
            Assert.Equal(1, fact.StatementOrdinal);
        }

        [Fact]
        public void ExtractFunctionCalls_NestedCalls_ShouldCaptureBoth()
        {
            // EXCEPTION_PROC UPDATE 3 실측 형태 - 바깥 ROUND4VAT과 안쪽 두 함수가
            // 모두 나와야 "이 문장이 무엇을 부르는가"가 빠짐없이 전달된다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.CLVT = dbo.UF_GET_ROUND4VAT(dbo.UF_GET_CLIENTSECTIONRATE(A.CLIENTID) * dbo.UF_GET_INCVTAXRATE(A.CLVTType))
    FROM   dbo.TSettleMst A
END";

            var facts = DmlScopeExtractor.ExtractFunctionCalls(
                ddl,
                new[] { "UF_GET_ROUND4VAT", "UF_GET_CLIENTSECTIONRATE", "UF_GET_INCVTAXRATE" });

            var names = facts.Select(f => f.QualifiedName).ToList();
            Assert.Equal(3, facts.Count);
            Assert.Contains("dbo.UF_GET_ROUND4VAT", names);
            Assert.Contains("dbo.UF_GET_CLIENTSECTIONRATE", names);
            Assert.Contains("dbo.UF_GET_INCVTAXRATE", names);
            Assert.All(facts, f => Assert.Equal(1, f.StatementOrdinal));
        }

        [Fact]
        public void ExtractFunctionCalls_StandaloneSelect_ShouldBeSkipped()
        {
            // DML 범위 표·집합 술어 표와 같은 경계다 - 세 표가 같은 문장 집합을
            // 같은 번호로 가리켜야 나란히 읽을 수 있다. 이 경계를 넓히려면 세 표를
            // 함께 넓혀야 하므로, 여기서 조용히 달라지지 않도록 못 박아 둔다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = dbo.UF_GET_ROUND4VAT(100)
END";

            Assert.Empty(DmlScopeExtractor.ExtractFunctionCalls(
                ddl, new[] { "UF_GET_ROUND4VAT" }));
        }

        [Fact]
        public void ExtractFunctionCalls_UnparsableDdl_ShouldReturnEmpty()
        {
            // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
            Assert.Empty(DmlScopeExtractor.ExtractFunctionCalls(
                "CREATE PROCEDURE ((( broken", new[] { "UF_X" }));
            Assert.Empty(DmlScopeExtractor.ExtractFunctionCalls(null, new[] { "UF_X" }));
            Assert.Empty(DmlScopeExtractor.ExtractFunctionCalls("SELECT 1", Array.Empty<string>()));
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --nologo -v q --filter "FullyQualifiedName~ExtractFunctionCalls"
```

Expected: 컴파일 실패 — `'DmlScopeExtractor' does not contain a definition for 'ExtractFunctionCalls'`

- [ ] **Step 3: 레코드와 상수를 추가한다**

`src/ReSet.Core/Services/DmlScopeExtractor.cs`의 `SetPredicateFact` 레코드 선언(96행에서 끝남) 바로 뒤에 추가한다.

```csharp
    /// <summary>
    /// "이 문장이 어느 사용자 함수를 부르는가"를 담는다.
    ///
    /// [왜 동작이 아니라 호출 사실만 담는가 - 2026-08-20 축 A 교차 대조]
    /// SP 명세서가 참조 함수의 동작을 산문으로 요약하던 자리에서 10행 중 8행이
    /// 결함이었고 그중 🔴이 5건이었다(필수 술어 USESTATE=0 누락, IIF 분기 누락,
    /// 기본값 0 반환 누락). 함수 DDL 전문은 이미 프롬프트에 들어가고 "분석하라"는
    /// 지시까지 있었는데도 그랬다 - 같은 함수를 SP마다 다르게 썼다.
    /// 그래서 요약을 정확하게 만드는 대신 요약 자체를 없앤다. 함수 동작의 단일
    /// 진실의 원천은 그 함수의 Spec.md이고, SP 명세서는 거기로 링크만 건다.
    /// </summary>
    /// <param name="QualifiedName">호출문에 적힌 그대로의 한정명(예: `dbo.UF_GET_ROUND4VAT`).</param>
    /// <param name="Operation">
    /// 이 호출을 담은 문장의 연산(UPDATE/INSERT/DELETE).
    ///
    /// [독립 SELECT 문의 호출은 담지 않는다] DML 범위 표·집합 술어 표가 세우는 경계와
    /// 같다 - 세 표가 같은 문장 집합을 같은 번호로 가리켜야 나란히 읽을 수 있다.
    /// 변수 대입용 SELECT(`SELECT @v = dbo.UF_X(...)`)의 호출은 이 표에 나오지 않는다.
    /// </param>
    /// <param name="StatementOrdinal">
    /// 연산 종류별 · 1부터인 문장 번호. DML 범위 표·집합 술어 표와 같은 채번이라
    /// 세 표를 나란히 읽을 수 있다(SetPredicateFact.StatementOrdinal 문서 참고).
    /// </param>
    /// <param name="Line">호출식이 있는 원본 줄 번호.</param>
    /// <param name="CallExpression">호출식 원문. 인자를 그대로 보여 준다.</param>
    public sealed record ReferencedFunctionCallFact(
        string QualifiedName,
        string Operation,
        int StatementOrdinal,
        int Line,
        string CallExpression);
```

같은 파일 122행의 `SetPredicateTableHeading` 상수 바로 아래에 추가한다.

```csharp
        public const string ReferencedFunctionTableHeading = "### 참조 함수 (기계 확정 — 수정 금지)";
```

- [ ] **Step 4: 추출기를 구현한다**

`ExtractSetPredicates`가 끝나는 183행 뒤에 추가한다.

```csharp
        /// <summary>
        /// DDL에서 사용자 정의 함수 호출을 문장 번호와 함께 뽑는다.
        /// </summary>
        /// <param name="knownFunctionNames">
        /// 한정자 없는 함수 이름 집합. SpDefinition.Dependencies의 FUNCTION 타입에서
        /// 온다 - StaticAnalysis.ReferencedFunctions를 쓰지 않는 이유는 그쪽이 인라인
        /// TVF를 싣지 못하기 때문이다(2026-08-20 실측: EXPECT_PROC·INS_EXTRA 모두
        /// UIF_SettleYMD가 Dependencies에만 있었다). 이 집합에 없는 이름은 내장
        /// 함수(ISNULL·ROUND·CAST)로 보고 건너뛴다.
        /// </param>
        public static IReadOnlyList<ReferencedFunctionCallFact> ExtractFunctionCalls(
            string? ddlText,
            IReadOnlyCollection<string> knownFunctionNames)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<ReferencedFunctionCallFact>();
            if (knownFunctionNames == null || knownFunctionNames.Count == 0)
                return Array.Empty<ReferencedFunctionCallFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null) return Array.Empty<ReferencedFunctionCallFact>();

                var visitor = new ReferencedFunctionVisitor(knownFunctionNames);
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[DmlScopeExtractor] 참조 함수 호출 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<ReferencedFunctionCallFact>();
            }
        }
```

- [ ] **Step 5: 방문자를 구현한다**

같은 파일의 `SetPredicateVisitor` 클래스가 끝나는 지점 뒤(다음 `private sealed class` 선언 앞)에 추가한다.

```csharp
        /// <summary>
        /// 문장마다 연산별 번호를 매기고 그 안의 사용자 함수 호출을 모은다.
        /// 번호를 매기는 규칙은 SetPredicateVisitor와 같다 - 두 방문자가 같은 파싱
        /// 트리를 같은 순서로 훑고 문장당 정확히 한 번 카운터를 늘리므로, 서로를
        /// 참조하지 않고도 항상 같은 번호가 나온다.
        /// </summary>
        private sealed class ReferencedFunctionVisitor : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _known;
            private readonly Dictionary<string, int> _perOperation =
                new(StringComparer.OrdinalIgnoreCase);

            public ReferencedFunctionVisitor(IReadOnlyCollection<string> knownFunctionNames) =>
                _known = new HashSet<string>(knownFunctionNames, StringComparer.OrdinalIgnoreCase);

            public List<ReferencedFunctionCallFact> Facts { get; } = new();

            public override void Visit(UpdateSpecification node) =>
                Collect("UPDATE", node, NextOrdinal("UPDATE"));

            public override void Visit(DeleteSpecification node) =>
                Collect("DELETE", node, NextOrdinal("DELETE"));

            public override void Visit(InsertSpecification node) =>
                Collect("INSERT", node, NextOrdinal("INSERT"));

            private int NextOrdinal(string operation)
            {
                _perOperation.TryGetValue(operation, out var n);
                _perOperation[operation] = ++n;
                return n;
            }

            private void Collect(string operation, TSqlFragment statement, int ordinal)
            {
                var calls = new CallCollector(_known);
                statement.Accept(calls);

                foreach (var (qualified, line, text) in calls.Calls)
                {
                    Facts.Add(new ReferencedFunctionCallFact(qualified, operation, ordinal, line, text));
                }
            }

            /// <summary>
            /// 문장 안의 모든 함수 호출을 훑는다. 중첩 호출은 바깥과 안쪽이 모두
            /// 나와야 "이 문장이 무엇을 부르는가"가 빠짐없이 전달되므로, 자식으로
            /// 계속 내려간다(base.ExplicitVisit 호출).
            /// </summary>
            private sealed class CallCollector : TSqlFragmentVisitor
            {
                private readonly HashSet<string> _known;

                public CallCollector(HashSet<string> known) => _known = known;

                public List<(string Qualified, int Line, string Text)> Calls { get; } = new();

                public override void ExplicitVisit(FunctionCall node)
                {
                    Record(node.FunctionName?.Value, node);
                    base.ExplicitVisit(node);
                }

                // 인라인 TVF는 FROM 절의 SchemaObjectFunctionTableReference로 나온다.
                public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
                {
                    Record(node.SchemaObject?.BaseIdentifier?.Value, node, node.SchemaObject);
                    base.ExplicitVisit(node);
                }

                private void Record(string? bareName, TSqlFragment node, SchemaObjectName? schemaObject = null)
                {
                    if (string.IsNullOrWhiteSpace(bareName) || !_known.Contains(bareName)) return;

                    Calls.Add((Qualify(bareName, schemaObject), node.StartLine, FragmentText(node)));
                }

                /// <summary>스칼라 함수는 호출식 원문에서, TVF는 SchemaObjectName에서 한정자를 얻는다.</summary>
                private static string Qualify(string bareName, SchemaObjectName? schemaObject)
                {
                    var schema = schemaObject?.SchemaIdentifier?.Value;
                    var database = schemaObject?.DatabaseIdentifier?.Value;

                    if (!string.IsNullOrWhiteSpace(database) && !string.IsNullOrWhiteSpace(schema))
                        return $"{database}.{schema}.{bareName}";
                    if (!string.IsNullOrWhiteSpace(schema))
                        return $"{schema}.{bareName}";
                    return bareName;
                }
            }
        }
```

**주의 — 스칼라 함수의 한정자.** ScriptDom의 `FunctionCall.FunctionName`은 한정자를 담지 않는다(`dbo.UF_X(...)`에서 `UF_X`만 나온다). 그래서 스칼라 호출은 `Qualify`가 `schemaObject: null`을 받아 **한정자 없는 이름**을 낸다. Task 2의 렌더러가 `Dependencies`에서 스키마·DB를 붙이므로 여기서는 그대로 둔다.

이 때문에 Step 1의 테스트 중 `dbo.UF_GET_ROUND4VAT`를 기대하는 것들이 실패한다. **Step 6에서 테스트를 원본 사실에 맞게 고친다** — 구현을 테스트에 맞추는 것이 아니라, 테스트가 기대한 값이 ScriptDom의 실제 출력과 달랐던 것이다.

- [ ] **Step 6: 테스트의 기대값을 실제 파싱 결과에 맞춘다**

Step 1에서 쓴 테스트의 `QualifiedName` 기대값을 고친다.

- `ExtractFunctionCalls_ShouldNumberStatementsLikeDmlScopeTable`:
  `Assert.All(facts, f => Assert.Equal("dbo.UF_GET_ROUND4VAT", f.QualifiedName));`
  → `Assert.All(facts, f => Assert.Equal("UF_GET_ROUND4VAT", f.QualifiedName));`
- `ExtractFunctionCalls_InlineTvf_ShouldBeCaptured`: `dbo.UIF_SettleYMD` 그대로 둔다(TVF는 `SchemaObjectName`에 한정자가 있다).
- `ExtractFunctionCalls_NestedCalls_ShouldCaptureBoth`: 세 기대값에서 `dbo.` 접두사를 뺀다.

그리고 한정자 처리를 못 박는 테스트를 하나 더 추가한다.

```csharp
        [Fact]
        public void ExtractFunctionCalls_ScalarCall_ShouldReportBareName()
        {
            // ScriptDom의 FunctionCall.FunctionName은 한정자를 담지 않는다.
            // 스키마·DB는 렌더러가 Dependencies에서 붙인다 - 여기서 추측하지 않는다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = SETTLE_CARD_DB.dbo.UF_GET_COMM4PG(A.CPID)
    FROM   dbo.T A
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractFunctionCalls(
                ddl, new[] { "UF_GET_COMM4PG" }));

            Assert.Equal("UF_GET_COMM4PG", fact.QualifiedName);
        }
```

- [ ] **Step 7: 테스트가 통과하는지 확인한다**

```bash
dotnet test --nologo -v q --filter "FullyQualifiedName~ExtractFunctionCalls"
```

Expected: PASS (7개)

- [ ] **Step 8: 전체 회귀를 돌린다**

```bash
dotnet test --nologo -v q
```

Expected: 실패 0, 통과 2014 (기존 2007 + 신규 7)

- [ ] **Step 9: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs
git commit -m "$(cat <<'EOF'
feat: 문장 번호와 함께 사용자 함수 호출을 뽑는 추출기를 세운다

SP 명세서가 참조 함수 동작을 산문으로 요약하던 자리에서 10행 중 8행이
결함이었고 🔴이 5건이었다. 요약을 정확하게 만드는 대신 없애기로 했고,
그 자리를 채울 기계 사실이 필요하다.

이름 집합은 Dependencies에서 받는다 - ReferencedFunctions는 인라인 TVF를
싣지 못한다. 문장 채번은 SetPredicateVisitor와 같은 규칙이라 DML 범위 표와
번호가 맞는다. 중첩 호출은 바깥과 안쪽을 모두 낸다.

ScriptDom의 FunctionCall.FunctionName이 한정자를 담지 않으므로 스칼라
호출은 한정자 없는 이름을 낸다. 스키마·DB는 렌더러가 Dependencies에서
붙인다 - 추출기가 추측하지 않는다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: 기계 확정 표 렌더러

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs` (렌더러는 `BuildSetPredicateTableLines`가 끝나는 909행 뒤, 배선은 `rules.AddRange(BuildSetPredicateTableLines(...))`가 있는 411행 근처)
- Test: `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs` (파일 끝에 추가)

**Interfaces:**
- Consumes: Task 1의 `ReferencedFunctionCallFact`, `ExtractFunctionCalls`, `ReferencedFunctionTableHeading`
- Produces: `private static List<string> AiService.BuildReferencedFunctionTableLines(IReadOnlyList<ReferencedFunctionCallFact> calls, SpDefinition spDef)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/AiServiceTests_Rich.cs` 파일 끝의 마지막 `}` 두 개 앞에 추가한다.

```csharp
        private static SpDefinition ReferencedFunctionSpDefinition() => new()
        {
            ObjectKey = new CodeObjectKey
            {
                Database = "SETTLE_POQ_DB", Schema = "dbo",
                Name = "P", Type = CodeObjectType.Procedure
            },
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.CLVT = dbo.UF_GET_ROUND4VAT(A.CLCOMM)
                ,A.PGCOMM = SETTLE_CARD_DB.dbo.UF_GET_COMM4PG(A.CPID)
    FROM   dbo.TSettleMst A
END",
            Dependencies = new List<DependencyInfo>
            {
                new() { Database = null, Schema = "dbo", Name = "UF_GET_ROUND4VAT", Type = "SQL_SCALAR_FUNCTION" },
                new() { Database = "SETTLE_CARD_DB", Schema = "dbo", Name = "UF_GET_COMM4PG", Type = "SQL_SCALAR_FUNCTION" },
                new() { Database = null, Schema = "dbo", Name = "TSettleMst", Type = "USER_TABLE" }
            }
        };

        [Fact]
        public async Task GenerateSpecification_ShouldRenderReferencedFunctionTable()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(ReferencedFunctionSpDefinition(), "rules");
            var body = result.SystemPrompt;

            Assert.Contains(DmlScopeExtractor.ReferencedFunctionTableHeading, body);
            // 로컬 함수와 외부 DB 함수의 링크 경로가 다르다.
            Assert.Contains("../../../Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md", body);
            Assert.Contains("../../../External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG/docs/Spec.md", body);
            // 테이블은 이 표에 실리지 않는다.
            var section = ExtractTableSection(body, DmlScopeExtractor.ReferencedFunctionTableHeading);
            Assert.DoesNotContain("TSettleMst", section);
        }

        [Fact]
        public async Task ReferencedFunctionTable_HeaderAndSeparator_ShouldHaveSameColumnCount()
        {
            // 2026-08-20 축 A 감사에서 헤더와 구분자 열 수가 어긋나 GFM이 표를
            // 통째로 렌더하지 못한 결함이 두 번 나왔다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(ReferencedFunctionSpDefinition(), "rules");
            var section = ExtractTableSection(result.SystemPrompt, DmlScopeExtractor.ReferencedFunctionTableHeading);

            var rows = section.Split('\n')
                              .Select(l => l.Trim())
                              .Where(l => l.StartsWith("|"))
                              .ToList();

            Assert.True(rows.Count >= 2, "표에 헤더와 구분자 행이 있어야 한다.");
            Assert.Equal(rows[0].Count(c => c == '|'), rows[1].Count(c => c == '|'));
        }

        [Fact]
        public async Task GenerateSpecification_NoFunctionCalls_ShouldOmitTable()
        {
            var spDef = new SpDefinition
            {
                ObjectKey = new CodeObjectKey
                {
                    Database = "SETTLE_POQ_DB", Schema = "dbo",
                    Name = "P", Type = CodeObjectType.Procedure
                },
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 END",
                Dependencies = new List<DependencyInfo>()
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");

            Assert.DoesNotContain(DmlScopeExtractor.ReferencedFunctionTableHeading, result.SystemPrompt);
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --nologo -v q --filter "FullyQualifiedName~ReferencedFunction"
```

Expected: FAIL — `Assert.Contains() Failure` (표 제목이 프롬프트에 없다)

- [ ] **Step 3: 렌더러를 구현한다**

`src/ReSet.Core/Services/AiService.cs`의 `BuildSetPredicateTableLines`가 끝나는 909행 뒤에 추가한다.

```csharp
        /// <summary>
        /// 「참조 함수」 표를 렌더한다. 이 절은 조립기가 채우고 LLM은 손대지 않는다.
        ///
        /// [왜 동작 서술 칸이 없는가 - 2026-08-20 축 A 교차 대조]
        /// 이 자리에 "실제 로직" 칸이 있던 시절 EXCEPTION_PROC의 10행 중 8행이
        /// 결함이었고 🔴이 5건이었다(USESTATE=0 술어 누락, IIF 분기 누락, 기본값 0
        /// 반환 누락). 함수 DDL 전문이 이미 프롬프트에 있었는데도 그랬다.
        /// 그래서 서술 칸 자체를 없애고 함수 Spec.md로 링크만 건다.
        /// </summary>
        private static List<string> BuildReferencedFunctionTableLines(
            IReadOnlyList<ReferencedFunctionCallFact> calls,
            SpDefinition spDef)
        {
            var functionDeps = (spDef.Dependencies ?? new List<DependencyInfo>())
                .Where(d => d.Type != null && d.Type.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var lines = new List<string>
            {
                "   [CRITICAL REFERENCED FUNCTION TABLE] The following function calls are MACHINE-DERIVED from the source DDL. Copy this table verbatim into the document under the exact heading shown. Do NOT add a column describing what a function does, and do NOT describe any function's behaviour, return value, branches, filters, or defaults ANYWHERE in the document - not in this section, not in CRUD 분석, not in 로직 흐름. When a SET expression calls a function, name the call and leave it at that. The single source of truth for a function's behaviour is that function's own Spec.md, which this table links to.",
                $"   {DmlScopeExtractor.ReferencedFunctionTableHeading}",
                "   | 함수 | 호출 위치 | 인자 | 명세서 |",
                "   | :--- | :--- | :--- | :--- |"
            };

            foreach (var call in calls)
            {
                var dep = functionDeps.FirstOrDefault(d =>
                    string.Equals(LastSegment(d.Name), LastSegment(call.QualifiedName), StringComparison.OrdinalIgnoreCase));

                var display = dep != null ? $"{dep.Schema}.{dep.Name}" : call.QualifiedName;
                var link = dep != null ? BuildFunctionSpecRelativePath(dep, spDef) : "(명세서 없음)";

                lines.Add(
                    $"   | {EscapeTableCell(display)} | {call.Operation} {call.StatementOrdinal} (라인 {call.Line}) | "
                    + $"{EscapeTableCell(call.CallExpression)} | {link} |");
            }

            lines.Add("");
            return lines;
        }

        /// <summary>한정명의 마지막 조각만 낸다(`SETTLE_CARD_DB.dbo.UF_X` → `UF_X`).</summary>
        private static string LastSegment(string? qualified) =>
            string.IsNullOrWhiteSpace(qualified)
                ? string.Empty
                : qualified.Split('.').Last();

        /// <summary>
        /// SP 명세서(`output/Procedures/[SP]/docs/Spec.md`)에서 함수 명세서로 가는
        /// 상대 경로를 만든다. 「참조 코드 객체」 절이 이미 쓰는 것과 같은 형태다.
        /// 로컬 함수는 `output/Functions/`, 다른 DB의 함수는
        /// `output/External/[DB]/Functions/` 아래에 있다.
        /// </summary>
        private static string BuildFunctionSpecRelativePath(DependencyInfo dep, SpDefinition spDef)
        {
            var isExternal =
                !string.IsNullOrWhiteSpace(dep.Database) &&
                !string.Equals(dep.Database, spDef.ObjectKey?.Database, StringComparison.OrdinalIgnoreCase);

            var folder = isExternal
                ? $"../../../External/{dep.Database}/Functions"
                : "../../../Functions";

            return $"[Spec]({folder}/{dep.Schema}.{dep.Name}/docs/Spec.md)";
        }
```

- [ ] **Step 4: 프롬프트에 배선한다**

같은 파일 411행의 `rules.AddRange(BuildSetPredicateTableLines(setPredicates));`가 속한 `if` 블록 **뒤**에 추가한다.

```csharp
            // 참조 함수 표도 같은 이유로 기계가 채운다 - 2026-08-20 축 A 교차 대조에서
            // 이 자리를 LLM이 쓰던 시절 10행 중 8행이 결함이었다(🔴 5건).
            var knownFunctionNames = (spDef.Dependencies ?? new List<DependencyInfo>())
                .Where(d => d.Type != null && d.Type.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            var functionCalls = DmlScopeExtractor.ExtractFunctionCalls(spDef.DdlText, knownFunctionNames);
            if (functionCalls.Count > 0)
            {
                rules.AddRange(BuildReferencedFunctionTableLines(functionCalls, spDef));
            }
```

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

```bash
dotnet test --nologo -v q --filter "FullyQualifiedName~ReferencedFunction"
```

Expected: PASS (3개)

- [ ] **Step 6: 전체 회귀를 돌린다**

```bash
dotnet test --nologo -v q
```

Expected: 실패 0, 통과 2017 (2007 + Task 1의 7 + 신규 3)

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/AiServiceTests_Rich.cs
git commit -m "$(cat <<'EOF'
feat: 참조 함수 표를 조립기가 채우게 한다

동작 서술 칸이 없는 표다 - 함수·호출 위치·인자·명세서 링크 넷뿐이다.
그 자리에 "실제 로직" 칸이 있던 시절 EXCEPTION_PROC의 10행 중 8행이
결함이었고 🔴이 5건이었다. 서술 칸을 없애 결함 부류를 구조적으로 지운다.

링크는 로컬 함수와 외부 DB 함수의 경로가 다르다 - 「참조 코드 객체」 절이
이미 쓰는 형태를 따랐다. 호출이 없으면 절 자체를 내지 않는다.

헤더와 구분자 행의 열 수가 같은지 테스트로 못 박았다 - 감사에서 이 결함이
두 번 나와 GFM이 표를 통째로 렌더하지 못했다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: 함수 서술 금지 계약과 캐시 버전

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs:328` (마크다운 생성 규칙)
- Modify: `src/ReSet.Core/Services/AiService.cs:1674` (JSON 추출 스키마 프롬프트 · 규칙 5)
- Modify: `src/ReSet.Core/Services/AiService.cs:1850` (같은 문장의 두 번째 사본 · 규칙 5)
- Modify: `src/ReSet.Core/Services/CacheManager.cs:22-28` (포맷 버전)
- Test: `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`

**Interfaces:**
- Consumes: Task 2의 `ReferencedFunctionTableHeading`
- Produces: 없음(마지막 작업이다)

**왜 세 곳인가.** `:1674`·`:1850`은 `DeconstructedLogic`을 만드는 JSON 추출 프롬프트인데, 그 결과가 `<deconstructed-logic-source-of-truth>`로 명세서 생성 프롬프트에 다시 주입된다(`AiService.cs:2389-2394`). 한 곳만 고치면 함수 공식이 다른 경로로 되돌아온다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`의 Task 2 테스트 뒤에 추가한다.

```csharp
        [Fact]
        public async Task GenerateSpecification_ShouldForbidDescribingFunctionBehaviour()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(ReferencedFunctionSpDefinition(), "rules");
            var body = result.SystemPrompt;

            // 옛 지시는 함수 로직을 분석하라고 시켰다.
            Assert.DoesNotContain("analyze its logic", body);
            Assert.DoesNotContain("detail their formulas", body);
            // 새 계약은 문서 어디에서도 서술을 금지한다.
            Assert.Contains("do NOT describe any function's behaviour", body);
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --nologo -v q --filter "FullyQualifiedName~ShouldForbidDescribingFunctionBehaviour"
```

Expected: FAIL — `Assert.DoesNotContain() Failure` (`analyze its logic`이 아직 있다)

- [ ] **Step 3: 마크다운 생성 규칙을 바꾼다**

`src/ReSet.Core/Services/AiService.cs`의 326-328행을 다음으로 교체한다.

```csharp
            if (hasUdf)
            {
                // [왜 "분석하라"에서 "서술하지 마라"로 뒤집었는가 - 2026-08-20 축 A 교차 대조]
                // 옛 지시는 "UDF 소스가 있으면 그 로직을 분석하라"였고, 함수 DDL 전문이
                // 실제로 프롬프트에 들어갔다. 그런데도 EXCEPTION_PROC의 UDF 요약 표
                // 10행 중 8행이 결함이었고 🔴이 5건이었다. 같은 함수를 SP마다 다르게
                // 썼다 - UF_GET_INCVTAXRATE를 다섯 SP가 "0이면 0.1…"부터 "계산에
                // 사용합니다"까지 제각각으로 서술했다. 요약을 정확하게 만드는 대신
                // 요약 자체를 없앤다.
                rules.Add($"{ruleIndex++}. Do NOT describe what any referenced User Defined Function (UDF) does. Its behaviour - return value, branches, filters, defaults, rounding - belongs only in that function's own Spec.md, which the machine-derived 참조 함수 table links to. State where each function is called and with which arguments; say nothing about what it returns.");
            }
```

- [ ] **Step 4: JSON 추출 프롬프트 두 곳을 바꾼다**

`AiService.cs:1674`와 `:1850`의 다음 문장을 찾는다(두 곳 모두 동일하다).

```
5. Identify all referenced User Defined Functions (UDFs) and detail their formulas, especially for calculations like CLVT and PGVT.
```

각각 다음으로 교체한다.

```
5. Identify all referenced User Defined Functions (UDFs) by name and calling location only. Do NOT detail their formulas, return values, or internal logic - that belongs in each function's own specification, not here.
```

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

```bash
dotnet test --nologo -v q --filter "FullyQualifiedName~ShouldForbidDescribingFunctionBehaviour"
```

Expected: PASS

- [ ] **Step 6: 캐시 포맷 버전을 올린다**

`src/ReSet.Core/Services/CacheManager.cs`의 28행 `private const int CurrentCacheFormatVersion = 4;`를 `5`로 바꾸고, 27행 뒤에 주석을 추가한다.

```csharp
        // 5: 참조 함수 표가 조립기 산출물로 바뀌었고 함수 동작 서술이 금지되었다.
        //    프롬프트 입력과 출력 계약이 둘 다 달라졌으므로 옛 산출물은 재분석해야 한다.
        //    2026-08-20 축 A 교차 대조에서 이 표의 10행 중 8행이 결함이었고 🔴이 5건이었다.
        private const int CurrentCacheFormatVersion = 5;
```

- [ ] **Step 7: 전체 회귀를 돌린다**

```bash
dotnet test --nologo -v q
```

Expected: 실패 0, 통과 2018 (2007 + 7 + 3 + 1)

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core/Services/AiService.cs src/ReSet.Core/Services/CacheManager.cs tests/ReSet.Core.Tests/AiServiceTests_Rich.cs
git commit -m "$(cat <<'EOF'
feat: 함수 동작 서술을 금지하고 캐시 포맷을 5로 올린다

지시를 세 곳에서 뒤집는다. 마크다운 생성 규칙(:328)과, DeconstructedLogic을
만드는 JSON 추출 프롬프트 두 곳(:1674, :1850)이다. 후자를 빼먹으면 함수
공식이 <deconstructed-logic-source-of-truth>로 명세서 프롬프트에 되돌아온다.

계약은 "참조 함수 절"이 아니라 "문서 어디에서도"로 넓게 쓴다. 표를 뺏어도
로직 흐름이나 CRUD 분석 산문에서 계속 서술할 여지가 남기 때문이다.

캐시 포맷 4 → 5. 프롬프트 입력과 출력 계약이 둘 다 달라져 전 객체를
재분석해야 한다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## 구현 뒤 실물 확인 (계획 밖 · 사람이 판단할 자리)

세 작업이 끝나면 **명세서를 재생성해 봐야** 계약이 실제로 먹혔는지 알 수 있다. 단위 테스트는
프롬프트에 무엇이 들어갔는지만 증명하고, LLM이 그 계약을 지키는지는 증명하지 못한다.

1. SP 하나(`UP_UTIL_SETTLE_EXCEPTION_PROC`)를 재생성한다.
2. 「참조 함수」 절이 조립기 출력 그대로인지 본다.
3. **문서 전체를 훑어** 함수 동작을 서술한 산문이 남았는지 본다 — 위험 1(7절)이 여기서 드러난다.
4. 링크가 실제 파일을 가리키는지 확인한다(로컬 5개 · 외부 5개).

최종 측정은 **축 A 교차 대조 28행**(대조한 10 + 미대조 18)을 다시 돌려 **🔴 5건이 닫혔는지**
보는 것이다. 감사 스킬의 3-2절이 그 절차를 규정한다.
