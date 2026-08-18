# 집합 술어 재료 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** DML 최상위 WHERE의 `IN`/`NOT IN` 리터럴 목록을 기계 확정 재료로 뽑아 프롬프트 표로 강제하고, 그 표의 원소 집합을 L1이 대칭 비교한다.

**Architecture:** 기존 `TopLevelPredicateCollector`가 이미 `IN` 술어를 지나가므로 거기서 집합 사실을 함께 담고, `DmlScopeExtractor`에 두 번째 진입점 `ExtractSetPredicates`를 둔다. 프롬프트는 DML 범위 표가 실리는 세 지점에서 같은 헬퍼로 새 표를 렌더하고, `MechanicalValidator`는 표 구간 안에서 `라인+컬럼`으로 행을 찾아 `리터럴 목록` 칸의 원소 집합만 비교한다.

**Tech Stack:** .NET 10 / C#, `Microsoft.SqlServer.TransactSql.ScriptDom` 180.37.3, xUnit

**Spec:** `docs/superpowers/specs/2026-08-18-set-predicate-material-design.md`

## Global Constraints

- **지배 계약(설계 §0):** 추출기 하나가 사실을 내고 프롬프트와 L1이 **같은 사실**을 소비한다. 규칙만 있고 물리는 기계 검사가 없으면 그 규칙은 없는 것과 같다.
- **"최상위"의 주인은 한 곳이다(설계 §3.1):** `TopLevelPredicateCollector`. 순회 규칙을 다른 클래스에 복제하지 않는다.
- **담지 않는 것 셋(설계 §3.2):** 서브쿼리 `IN`(`node.Subquery != null`), 원소에 리터럴 아닌 것이 섞인 `IN`, 스칼라 리터럴 비교.
- **리터럴은 원문 그대로:** 문자열은 따옴표를 포함한다 — `'PLCard'`.
- **소프트 페일:** 파싱 실패는 예외를 던지지 않고 빈 목록으로 진행한다(AGENTS.md 범주 2). 기존 `Extract`와 같은 형태.
- **레드-그린 필수(설계 §6.1):** 모든 검사·추출기 변경은 되돌렸을 때 실제로 실패해야 한다.
- **경고 기준선 9개:** `dotnet build --no-incremental` 결과가 9를 넘으면 안 된다. 새 xUnit 분석기 경고(`xUnit2031`: `Assert.Single(x.Where(...))`)를 만들지 말 것 — `Assert.Single(x, predicate)` 오버로드를 쓴다.
- **한국어 주석:** 이 저장소의 주석·오류 메시지는 한국어다. 왜 그렇게 했는지를 적는다.

---

## File Structure

| 파일 | 책임 | 변경 |
|---|---|---|
| `src/ReSet.Core/Services/DmlScopeExtractor.cs` | `SetPredicateFact` 레코드, `ExtractSetPredicates` 진입점, `TopLevelPredicateCollector.SetPredicates` 수집 | 수정 |
| `src/ReSet.Core/Services/SpecExpectations.cs` | `SetPredicates` 속성, 조기 반환 항, `From`에서 추출기 호출 | 수정 |
| `src/ReSet.Core/Services/AiService.cs` | `BuildSetPredicateTableLines` 헬퍼 + 렌더 지점 세 곳 배선 | 수정 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | `ErrorType.SetPredicateMismatch`, `CheckSetPredicates`, `LocateSetPredicateSection` | 수정 |
| `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs` | 추출기 단위 테스트 | 수정 |
| `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` | L1 검사 + 배선 테스트 | 수정 |
| `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs` | 프롬프트 렌더 테스트(세 지점) | 수정 |
| `tests/ReSet.Core.Tests/AxisAGoldenCaseTests.cs` | 실물 코퍼스 골든 케이스 | 수정 |

새 파일은 만들지 않는다. 재료가 DML 문장 문맥(라인·연산)을 필요로 하고 "최상위"의 정의가 `DmlScopeExtractor` 안에 있기 때문이다.

---

## Task 1: `SetPredicateFact` 추출기

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs`
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`

**Interfaces:**
- Consumes: 없음(이 계획의 첫 태스크)
- Produces:
  - `public sealed record SetPredicateFact(string Operation, int Line, string Column, bool IsNegated, IReadOnlyList<string> Literals)`
  - `public static IReadOnlyList<SetPredicateFact> DmlScopeExtractor.ExtractSetPredicates(string? ddlText)`
  - `public const string DmlScopeExtractor.SetPredicateTableHeading = "### 집합 술어 (기계 확정 — 수정 금지)"`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs` 끝(마지막 `}` 두 개 앞)에 추가한다.

```csharp
        [Fact]
        public void ExtractSetPredicates_TopLevelNotIn_ShouldCaptureEveryLiteral()
        {
            // EXPECT_PROC 갱신 1(object_definition.sql:39) 실측 형태. 명세서는 이 9개
            // 자리에 5개짜리 다른 목록을 그럴듯한 대체물로 채워 넣었다 - 집합의 크기와
            // 원소는 컬럼 이름으로 추측할 수 없다는 것이 이 재료의 존재 이유다.
            var ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    UPDATE A SET A.InState = 1
    FROM   dbo.TSettleMst A
    WHERE  A.YMD = @pi_strYMD
    AND    A.PGName NOT IN ('PLCard','SamSungPay','SSGPayCard','KakaoPay','KakaoCard','impaymobile','NaverCard','ApplePay','TossCardAuth')
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("UPDATE", fact.Operation);
            Assert.Equal("PGName", fact.Column);
            Assert.True(fact.IsNegated);
            Assert.Equal(9, fact.Literals.Count);
            Assert.Equal("'PLCard'", fact.Literals[0]);
            Assert.Contains("'SSGPayCard'", fact.Literals);
            Assert.Contains("'KakaoCard'", fact.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_PositiveInWithNumbers_ShouldKeepRawLiterals()
        {
            // 숫자 리터럴도 담는다. 표에서 대조하므로 앵커 문제가 생기지 않는다
            // (설계 §5.1 - 산문에서 "0"을 찾는 것이 아니다).
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DELETE FROM dbo.T WHERE UseState IN (0, 1)
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("DELETE", fact.Operation);
            Assert.Equal("UseState", fact.Column);
            Assert.False(fact.IsNegated);
            Assert.Equal(new[] { "0", "1" }, fact.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_SubqueryIn_ShouldBeSkipped()
        {
            // 집합이 리터럴이 아니므로 옮겨 적을 목록 자체가 없다(설계 §3.2).
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.T SET C = 1 WHERE PLTID IN (SELECT PLTID FROM dbo.S)
END";

            Assert.Empty(DmlScopeExtractor.ExtractSetPredicates(ddl));
        }

        [Fact]
        public void ExtractSetPredicates_MixedValues_ShouldBeSkipped()
        {
            // 원소에 리터럴 아닌 것이 하나라도 섞이면 담지 않는다 - 리터럴 집합으로
            // 렌더하면 명세서에 거짓 집합이 실린다(설계 §3.2).
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = 1 FROM dbo.T A JOIN dbo.S B ON A.Id = B.Id WHERE A.PGName IN ('PLCard', B.PGName)
END";

            Assert.Empty(DmlScopeExtractor.ExtractSetPredicates(ddl));
        }

        [Fact]
        public void ExtractSetPredicates_InsideScalarSubquery_ShouldBeSkipped()
        {
            // "최상위"의 정의는 TopLevelPredicateCollector가 갖는다 - 스칼라 서브쿼리
            // 안의 IN은 대상 범위를 정하지 않는다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.T SET C = 1
    WHERE  Amt = (SELECT MAX(Amt) FROM dbo.S WHERE PGName IN ('A','B'))
END";

            Assert.Empty(DmlScopeExtractor.ExtractSetPredicates(ddl));
        }

        [Fact]
        public void ExtractSetPredicates_TwoInPredicatesInOneStatement_ShouldKeepBoth()
        {
            // 한 문장에 IN이 둘일 수 있다 - 그래서 L1의 행 키가 라인 하나로는
            // 부족하고 라인+컬럼이어야 한다(설계 §5).
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.T SET C = 1 WHERE PGName IN ('A','B') AND UseState IN (0,1)
END";

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Contains(facts, f => f.Column == "PGName");
            Assert.Contains(facts, f => f.Column == "UseState");
            Assert.All(facts, f => Assert.Equal(6, f.Line));
        }

        [Fact]
        public void ExtractSetPredicates_ScalarLiteralComparisons_ShouldBeSkipped()
        {
            // 설계 §2. 코퍼스 실측에서 스칼라 리터럴 비교는 474건(집합 리터럴은 약
            // 104건)이라, 담으면 부피가 5배가 되고 "값까지 대조하면 노이즈"라는 축 B의
            // 기존 판단이 그대로 옳은 지점이 된다. 둘을 가르는 것은 구조다 -
            // INSTATE = 0은 컬럼 이름만 봐도 존재를 알지만, 집합의 크기와 원소는
            // 컬럼 이름으로 추측할 수 없다.
            var ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    UPDATE dbo.T SET C = 1
    WHERE  YMD = @pi_strYMD AND InState = 0 AND PGName = 'PLCard' AND UseState <> 1
END";

            Assert.Empty(DmlScopeExtractor.ExtractSetPredicates(ddl));
        }

        [Fact]
        public void ExtractSetPredicates_NullDdl_ShouldReturnEmpty()
        {
            Assert.Empty(DmlScopeExtractor.ExtractSetPredicates(null));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~ExtractSetPredicates"`
Expected: 컴파일 실패 — `ExtractSetPredicates`와 `SetPredicateFact`가 없다.

- [ ] **Step 3: 레코드와 헤딩 상수를 더한다**

`src/ReSet.Core/Services/DmlScopeExtractor.cs`에서 `DmlScopeFact` 레코드 선언 **바로 아래**에 추가한다.

```csharp
    /// <param name="Operation">"INSERT", "UPDATE", "DELETE" 중 하나.</param>
    /// <param name="Line">원본 DDL에서 그 문장이 시작하는 줄 번호(1부터).</param>
    /// <param name="Column">IN 좌변의 컬럼 이름.</param>
    /// <param name="IsNegated">NOT IN이면 true.</param>
    /// <param name="Literals">
    /// 집합의 원소를 원문 그대로 담는다 - 문자열은 따옴표를 포함한다('PLCard').
    /// 파생 테이블 정의 표가 표현식 원문을 그대로 싣는 것과 같은 이유이고, 표에서
    /// 문자열과 숫자를 구분할 수 있게 한다.
    /// </param>
    public sealed record SetPredicateFact(
        string Operation,
        int Line,
        string Column,
        bool IsNegated,
        IReadOnlyList<string> Literals);
```

`DmlScopeExtractor` 클래스 안, `DmlScopeTableHeading` 선언 바로 아래에 추가한다.

```csharp
        public const string SetPredicateTableHeading = "### 집합 술어 (기계 확정 — 수정 금지)";
```

- [ ] **Step 4: 수집기를 넓힌다**

`TopLevelPredicateCollector` 클래스(파일 내 `private sealed class TopLevelPredicateCollector`)의 `JoinKeys` 선언 아래에 추가한다.

```csharp
            /// <summary>
            /// 최상위 IN/NOT IN의 리터럴 집합. Column은 좌변 컬럼 이름, IsNegated는
            /// NOT 여부, Literals는 원문 그대로다. Operation과 Line은 이 수집기가
            /// 모르므로(문장 문맥은 호출부가 안다) 호출부가 채운다.
            /// </summary>
            public List<(string Column, bool IsNegated, List<string> Literals)> SetPredicates { get; } = new();
```

같은 클래스의 기존 `ExplicitVisit(InPredicate)`를 아래로 **교체**한다. 기존 본문(`Expression`과 `Values`를 방문하는 부분)은 그대로 두고 수집만 앞에 더한다 — 그 방문은 컬럼 수집이라는 별개 책임이므로 없애면 안 된다.

```csharp
            public override void ExplicitVisit(InPredicate node)
            {
                RecordSetPredicate(node);

                node.Expression?.Accept(this);

                if (node.Subquery == null && node.Values != null)
                {
                    foreach (var value in node.Values)
                    {
                        value.Accept(this);
                    }
                }
            }

            /// <summary>
            /// 리터럴만으로 이뤄진 최상위 IN을 집합 사실로 담는다.
            ///
            /// [담지 않는 셋] 서브쿼리 IN은 옮겨 적을 리터럴 목록이 없다. 원소에
            /// 리터럴 아닌 것이 섞이면 리터럴 집합으로 렌더할 때 명세서에 거짓
            /// 집합이 실린다. 좌변이 단순 컬럼 참조가 아니면(예: 식) 표의 "컬럼"
            /// 칸에 쓸 이름이 없다.
            /// </summary>
            private void RecordSetPredicate(InPredicate node)
            {
                if (node.Subquery != null || node.Values == null || node.Values.Count == 0) return;

                if (node.Expression is not ColumnReferenceExpression columnRef) return;
                var column = columnRef.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value;
                if (string.IsNullOrWhiteSpace(column)) return;

                var literals = new List<string>();
                foreach (var value in node.Values)
                {
                    if (value is not Literal literal) return;   // 하나라도 아니면 통째로 버린다
                    literals.Add(TextOfFragment(literal));
                }

                SetPredicates.Add((column!, node.NotDefined, literals));
            }

            /// <summary>토큰 원문을 그대로 잇는다 - 문자열 리터럴의 따옴표를 보존한다.</summary>
            private static string TextOfFragment(TSqlFragment fragment)
            {
                if (fragment.ScriptTokenStream == null) return string.Empty;

                return string.Concat(
                    fragment.ScriptTokenStream
                        .Skip(fragment.FirstTokenIndex)
                        .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                        .Select(t => t.Text)).Trim();
            }
```

- [ ] **Step 5: 두 번째 진입점을 더한다**

`DmlScopeExtractor` 클래스 안, 기존 `Extract` 메서드 **바로 아래**에 추가한다.

```csharp
        /// <summary>
        /// DML 최상위 WHERE의 IN/NOT IN 리터럴 목록을 뽑는다.
        ///
        /// [왜 별도 진입점인가] "어디까지가 대상 범위를 정하는 술어인가"라는 지식은
        /// TopLevelPredicateCollector 한 곳에 인코딩돼 있다. 새 추출기가 그 순회를
        /// 다시 구현하면 두 정의가 갈라지고, 그 순간 이 재료는 프롬프트가 말하는
        /// "최상위"와 다른 것을 뜻하게 된다. 그래서 수집기를 넓히고 진입점만 나눈다 -
        /// 순회는 두 번 돌지만 비용은 무시할 수준이고 주인은 계속 한 곳이다.
        /// </summary>
        public static IReadOnlyList<SetPredicateFact> ExtractSetPredicates(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<SetPredicateFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null) return Array.Empty<SetPredicateFact>();

                var visitor = new SetPredicateVisitor();
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[DmlScopeExtractor] 집합 술어 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<SetPredicateFact>();
            }
        }
```

- [ ] **Step 6: 문장 문맥을 붙이는 방문자를 더한다**

`DmlScopeVisitor` 클래스 **바로 아래**에 추가한다.

```csharp
        /// <summary>
        /// DML 문장을 찾아 그 최상위 WHERE에서 집합 술어를 모으고, 수집기가 모르는
        /// 문장 문맥(연산 종류·시작 줄)을 붙인다.
        /// </summary>
        private sealed class SetPredicateVisitor : TSqlFragmentVisitor
        {
            public List<SetPredicateFact> Facts { get; } = new();

            public override void Visit(UpdateSpecification node) =>
                Collect("UPDATE", node, node.WhereClause);

            public override void Visit(DeleteSpecification node) =>
                Collect("DELETE", node, node.WhereClause);

            public override void Visit(InsertSpecification node)
            {
                // INSERT ... SELECT의 대상 범위는 원천 SELECT의 최상위 WHERE가 정한다
                // (DmlScopeExtractor.Visit(InsertSpecification)와 같은 판단). UNION으로
                // 묶인 원천은 갈래마다 WHERE가 다르므로 전부 훑는다.
                if (node.InsertSource is not SelectInsertSource select) return;

                foreach (var spec in QuerySpecificationsOf(select.Select))
                {
                    Collect("INSERT", node, spec.WhereClause);
                }
            }

            private void Collect(string operation, TSqlFragment statement, WhereClause? where)
            {
                if (where?.SearchCondition == null) return;

                var top = new TopLevelPredicateCollector();
                where.SearchCondition.Accept(top);

                foreach (var (column, isNegated, literals) in top.SetPredicates)
                {
                    Facts.Add(new SetPredicateFact(
                        operation, statement.StartLine, column, isNegated, literals));
                }
            }
        }
```

`QuerySpecificationsOf`는 `DmlScopeVisitor`에 이미 있는 private static 메서드다. `SetPredicateVisitor`에서 쓰려면 `DmlScopeVisitor`의 그것을 `DmlScopeExtractor` 클래스 수준의 private static으로 **끌어올린다**(선언을 `DmlScopeVisitor` 밖으로 옮기고 두 방문자가 함께 쓴다). 옮길 때 XML 주석도 함께 옮긴다.

- [ ] **Step 7: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~ExtractSetPredicates"`
Expected: PASS, 8 tests

- [ ] **Step 8: 레드-그린을 확인한다**

`RecordSetPredicate` 본문 첫 줄에 `return;`을 임시로 넣고 위 명령을 다시 돌린다.
Expected: 최소 4개 실패(`TopLevelNotIn`, `PositiveInWithNumbers`, `TwoInPredicates`, 그리고 `Assert.Single`이 빈 목록에 걸리는 것들). 확인 후 `return;`을 지운다.

- [ ] **Step 9: 전체 스위트와 경고를 확인한다**

Run: `dotnet test` → 0 failed
Run: `dotnet build --no-incremental 2>&1 | grep -E "경고 [0-9]+개"` → `경고 9개`

- [ ] **Step 10: 커밋**

```bash
git add src/ReSet.Core/Services/DmlScopeExtractor.cs tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs
git commit -m "feat: DML 최상위 IN 리터럴 집합을 재료로 뽑는다"
```

---

## Task 2: 프롬프트 표 렌더

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs` (헬퍼 신설 + 렌더 지점 3곳)
- Test: `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`

**Interfaces:**
- Consumes: `DmlScopeExtractor.ExtractSetPredicates(string?)`, `SetPredicateFact`, `DmlScopeExtractor.SetPredicateTableHeading` (Task 1)
- Produces: `private static List<string> AiService.BuildSetPredicateTableLines(IReadOnlyList<SetPredicateFact>)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/AiServiceTests_Rich.cs` 끝(마지막 `}` 두 개 앞)에 추가한다. `MockHttpMessageHandler`·`OpenAiClient` 사용법은 같은 파일의 기존 테스트를 그대로 따른다.

```csharp
        private static SpDefinition SetPredicateSpDefinition() => new()
        {
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.StoredProcedure,
            DdlText = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    UPDATE A SET A.InState = 1
    FROM   dbo.TSettleMst A
    WHERE  A.YMD = @pi_strYMD
    AND    A.PGName NOT IN ('PLCard','SSGPayCard','KakaoCard')
END"
        };

        [Fact]
        public async Task GenerateSpecificationAsync_WithSetPredicate_ShouldRenderTheTable()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(SetPredicateSpDefinition(), "rules");
            var body = result.SystemPrompt;

            Assert.Contains(DmlScopeExtractor.SetPredicateTableHeading, body);
            Assert.Contains("| UPDATE 1 | 5 | PGName | NOT IN | 3 |", body);
            Assert.Contains("'SSGPayCard'", body);
            Assert.Contains("'KakaoCard'", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_WithSetPredicate_ShouldRenderTheTable()
        {
            // 지역 모델의 최초 생성 경로는 BuildSpecificationPrompts를 아예 호출하지
            // 않는다 - Task 4의 Critical이 정확히 이 비대칭이었다.
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## CRUD 분석\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecSectionAsync(
                SetPredicateSpDefinition(), "CrudAnalysis", "rules", null);

            Assert.Contains(DmlScopeExtractor.SetPredicateTableHeading, result.SystemPrompt);
            Assert.Contains("'SSGPayCard'", result.SystemPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_FunctionWithSetPredicate_ShouldRenderTheTable()
        {
            var functionDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "FN_X",
                ObjectType = CodeObjectType.Function,
                DdlText = @"
CREATE FUNCTION dbo.FN_X()
RETURNS @R TABLE (Id INT)
AS
BEGIN
    INSERT INTO @R (Id) VALUES (1)
    DELETE FROM @R WHERE Id IN (7, 8)
    RETURN
END",
                FunctionReturn = new FunctionReturnInfo
                {
                    IsTableValued = true,
                    Columns = new System.Collections.Generic.List<ColumnInfo>
                    {
                        new ColumnInfo { ColumnName = "Id", DataType = "INT", IsNullable = false }
                    }
                }
            };

            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 함수 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(functionDef, "rules");

            Assert.Contains(DmlScopeExtractor.SetPredicateTableHeading, result.SystemPrompt);
            Assert.Contains("| DELETE 1 | 7 | Id | IN | 2 |", result.SystemPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithoutSetPredicate_ShouldNotRenderTheTable()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.StoredProcedure,
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN UPDATE dbo.T SET C = 1 WHERE Id = 1 END"
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(new HttpClient(new MockHttpMessageHandler(mockResponse)), "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");

            Assert.DoesNotContain(DmlScopeExtractor.SetPredicateTableHeading, result.SystemPrompt);
        }
```

`GenerateSpecSectionAsync`의 정확한 시그니처는 같은 파일의 기존
`GenerateSpecSectionAsync_CrudAnalysis_WithUpdateMappings_ShouldPrefillTheTable` 테스트를 읽어 그대로 맞춘다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~SetPredicate"`
Expected: 4개 실패 — 표가 프롬프트에 없다.

- [ ] **Step 3: 렌더 헬퍼를 더한다**

`src/ReSet.Core/Services/AiService.cs`의 `BuildDmlScopeTableLines` 메서드 **바로 아래**에 추가한다.

```csharp
        /// <summary>
        /// 기계 확정 집합 술어 표 본문을 만든다.
        ///
        /// [원소 수를 별도 칸으로 두는 이유] 2026-08-18 축 A 감사 실측: EXPECT_PROC의
        /// 9개짜리 집합 자리에 명세서가 5개짜리 다른 목록을 그럴듯한 대체물로 채워
        /// 넣었다. 목록만 있으면 눈으로 세어야 알지만, 수가 칸으로 있으면 어긋남이
        /// 즉시 보인다.
        ///
        /// 헤딩 리터럴은 DmlScopeExtractor.SetPredicateTableHeading 하나가 유일한
        /// 출처다 - 프롬프트와 L1(CheckSetPredicates)이 같은 상수를 쓴다.
        /// </summary>
        private static List<string> BuildSetPredicateTableLines(
            IReadOnlyList<SetPredicateFact> setPredicates)
        {
            var lines = new List<string>
            {
                "   [CRITICAL SET PREDICATE TABLE] The following set predicates are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## CRUD 분석` under the exact heading shown. Do NOT drop, add, abbreviate, or summarize any literal - the membership of each set is what determines the target rows, and it cannot be inferred from the column name.",
                $"   {DmlScopeExtractor.SetPredicateTableHeading}",
                "   | 문장 | 라인 | 컬럼 | 연산 | 원소 수 | 리터럴 목록 |",
                "   | :--- | :--- | :--- | :--- | :--- | :--- |"
            };

            // 연산 종류별로 번호를 매긴다 - DML 범위 표와 같은 규칙이라 두 표의
            // "UPDATE 3"이 같은 문장을 가리킨다.
            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lastLineByOperation = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var fact in setPredicates)
            {
                // 같은 문장에 IN이 여럿이면 문장 번호는 하나여야 한다.
                if (!lastLineByOperation.TryGetValue(fact.Operation, out var lastLine)
                    || lastLine != fact.Line)
                {
                    ordinals.TryGetValue(fact.Operation, out var n);
                    ordinals[fact.Operation] = n + 1;
                    lastLineByOperation[fact.Operation] = fact.Line;
                }

                var literals = string.Join(", ", fact.Literals);
                lines.Add(
                    $"   | {fact.Operation} {ordinals[fact.Operation]} | {fact.Line} | "
                    + $"{EscapeTableCell(fact.Column)} | {(fact.IsNegated ? "NOT IN" : "IN")} | "
                    + $"{fact.Literals.Count} | {EscapeTableCell(literals)} |");
            }

            lines.Add("");
            return lines;
        }
```

- [ ] **Step 4: 렌더 지점 세 곳에 배선한다**

세 곳 모두 `BuildDmlScopeTableLines` 호출 **바로 다음 줄**에 같은 형태로 넣는다. 각 지점의 지역 변수 이름이 다르므로 아래를 그대로 쓴다.

`AiService.cs:392` 부근(`BuildSpecificationPrompts`) — `dmlScopeFacts` 계산부 근처에 추출을 더하고:

```csharp
            var setPredicates = DmlScopeExtractor.ExtractSetPredicates(spDef.DdlText);
```

렌더:

```csharp
            if (setPredicates.Count > 0)
            {
                rules.AddRange(BuildSetPredicateTableLines(setPredicates));
            }
```

`AiService.cs:908` 부근(`BuildFunctionSpecificationPrompts`):

```csharp
            var setPredicates = DmlScopeExtractor.ExtractSetPredicates(functionDef.DdlText);
            if (setPredicates.Count > 0)
            {
                dmlScopeLines.AddRange(BuildSetPredicateTableLines(setPredicates));
            }
```

`AiService.cs:1987` 부근(`BuildSpecSectionPrompts`의 `CrudAnalysis` 분기):

```csharp
                    var setPredicatesForCrud = DmlScopeExtractor.ExtractSetPredicates(spDef.DdlText);
                    if (setPredicatesForCrud.Count > 0)
                    {
                        sbRules.AddRange(BuildSetPredicateTableLines(setPredicatesForCrud));
                    }
```

각 지점에서 `dmlScopeLines`/`sbRules`/`rules`가 실제로 어떤 이름인지는 그 줄의 기존 `BuildDmlScopeTableLines` 호출을 보고 맞춘다.

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~SetPredicate"`
Expected: PASS, 4 tests

- [ ] **Step 6: 레드-그린을 확인한다 — 지점별로**

세 렌더 지점을 **하나씩** 주석 처리하고 매번 위 명령을 돌린다. 지점을 끌 때마다 해당 테스트 하나가 실패해야 한다. 셋 다 확인한 뒤 복원한다.

Expected: 지점 1 끄면 `GenerateSpecificationAsync_WithSetPredicate` 실패, 지점 2 끄면 `FunctionWithSetPredicate` 실패, 지점 3 끄면 `GenerateSpecSectionAsync_CrudAnalysis_WithSetPredicate` 실패.

- [ ] **Step 7: 전체 스위트와 경고를 확인한다**

Run: `dotnet test` → 0 failed
Run: `dotnet build --no-incremental 2>&1 | grep -E "경고 [0-9]+개"` → `경고 9개`

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/AiServiceTests_Rich.cs
git commit -m "feat: 집합 술어 표를 세 프롬프트 지점에 렌더한다"
```

---

## Task 3: L1 검사와 배선

**Files:**
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs`
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `DmlScopeExtractor.ExtractSetPredicates`, `SetPredicateFact`, `DmlScopeExtractor.SetPredicateTableHeading` (Task 1)
- Produces: `SpecExpectations.SetPredicates` (init 속성), `ErrorType.SetPredicateMismatch`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`에 추가한다. `EmptyExpectations()`와 `RequiredHeadersMarkdown()`은 같은 파일의 기존 헬퍼다.

```csharp
        private static SetPredicateFact NineePgFact() => new(
            "UPDATE", 39, "PGName", true,
            new[]
            {
                "'PLCard'", "'SamSungPay'", "'SSGPayCard'", "'KakaoPay'", "'KakaoCard'",
                "'impaymobile'", "'NaverCard'", "'ApplePay'", "'TossCardAuth'"
            });

        private static string SetPredicateSection(string literalCell) =>
            "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
            + "| 문장 | 라인 | 컬럼 | 연산 | 원소 수 | 리터럴 목록 |\n"
            + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
            + $"| UPDATE 1 | 39 | PGName | NOT IN | 9 | {literalCell} |\n";

        [Fact]
        public void Validate_SetPredicateTableMissing_ShouldBeAnError()
        {
            var expectations = EmptyExpectations() with { SetPredicates = new[] { NineePgFact() } };

            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void Validate_SetPredicateWithAllLiterals_ShouldPass()
        {
            var expectations = EmptyExpectations() with { SetPredicates = new[] { NineePgFact() } };
            var markdown = RequiredHeadersMarkdown()
                + SetPredicateSection(
                    "'PLCard', 'SamSungPay', 'SSGPayCard', 'KakaoPay', 'KakaoCard', 'impaymobile', 'NaverCard', 'ApplePay', 'TossCardAuth'");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void Validate_SetPredicateDroppingTwoLiterals_ShouldBeAnError()
        {
            // 2026-08-18 축 A 감사의 실제 실패 방식. 명세서는 9개 중 7개를 문서
            // 어딘가에 담고 있었고, 빠진 것은 SSGPayCard와 KakaoCard다. 행 골격만
            // 요구하면 이 문서가 통과한다.
            var expectations = EmptyExpectations() with { SetPredicates = new[] { NineePgFact() } };
            var markdown = RequiredHeadersMarkdown()
                + SetPredicateSection(
                    "'PLCard', 'SamSungPay', 'KakaoPay', 'impaymobile', 'NaverCard', 'ApplePay', 'TossCardAuth'");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            var error = Assert.Single(
                result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
            Assert.Contains("SSGPayCard", error.Message);
            Assert.Contains("KakaoCard", error.Message);
        }

        [Fact]
        public void Validate_SetPredicateWithNumericLiterals_ShouldNotBeSatisfiedByLineNumber()
        {
            // 설계 §5.1. 행 전체를 부분 문자열로 훑으면 라인 번호 108이 이미 0과 1을
            // 담아 UseState IN (0,1) 대조가 무조건 통과한다 - 검사가 아무것도 묻지
            // 않게 된다. 대조 대상은 리터럴 목록 칸 하나여야 한다.
            var fact = new SetPredicateFact("UPDATE", 108, "UseState", false, new[] { "0", "1" });
            var expectations = EmptyExpectations() with { SetPredicates = new[] { fact } };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 원소 수 | 리터럴 목록 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 108 | UseState | IN | 2 | (생략) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void Validate_SetPredicateRowKeyedByLineAndColumn_ShouldDistinguishTwoInsOnOneStatement()
        {
            // 한 문장에 IN이 둘이면 라인만으로는 행을 특정할 수 없다.
            var facts = new[]
            {
                new SetPredicateFact("UPDATE", 30, "PGName", false, new[] { "'A'", "'B'" }),
                new SetPredicateFact("UPDATE", 30, "UseState", false, new[] { "0", "1" })
            };
            var expectations = EmptyExpectations() with { SetPredicates = facts };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 원소 수 | 리터럴 목록 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 30 | PGName | IN | 2 | 'A', 'B' |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            // UseState 행이 없으므로 하나만 걸려야 한다 - PGName 행이 라인 30을
            // 담았다고 UseState까지 통과시키면 안 된다.
            var error = Assert.Single(
                result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
            Assert.Contains("UseState", error.Message);
        }

        [Fact]
        public void From_WithSetPredicates_ShouldExposeThemAndNeverReturnNull()
        {
            // [조기 반환과 이 재료의 관계 - 설계 §6.3의 예외] SpecExpectations.From의
            // 조기 반환은 순수 AND-체인이고, 보통은 "이 재료만 만드는 픽스처"로 자기
            // 항을 지킨다. 그런데 이 재료는 그 격리가 <b>원리적으로 불가능하다</b> -
            // ExtractSetPredicates와 Extract가 UpdateSpecification·DeleteSpecification·
            // InsertSpecification이라는 같은 세 문장만 방문하므로, SetPredicates가
            // 비지 않으면 DmlScopeFacts도 결코 비지 않는다. 즉 setPredicates 항은
            // 단독 판별자가 될 수 없다.
            //
            // 그래서 이 테스트는 격리 대신 <b>그 불변식 자체</b>를 단언한다. 불변식이
            // 깨지는 날(예: 추출기 하나가 다른 문장까지 훑게 되는 날) 이 테스트가
            // 먼저 실패해, 조기 반환 항이 그때부터 실제로 필요해졌음을 알린다.
            var sp = new SpDefinition
            {
                DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DELETE FROM dbo.T WHERE UseState IN (0, 1)
END"
            };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            var fact = Assert.Single(expectations!.SetPredicates);
            Assert.Equal("UseState", fact.Column);
            Assert.Equal(new[] { "0", "1" }, fact.Literals);

            // 불변식: 집합 술어가 있으면 DML 사실도 반드시 있다. 이것이 성립하는
            // 동안 setPredicates 항은 조기 반환의 중복항이다.
            Assert.NotEmpty(expectations.DmlScopeFacts);

            // 나머지 재료는 이 픽스처가 만들지 않는다 - 이 테스트가 무엇을 증명하는지
            // 좁혀 둔다.
            Assert.Empty(expectations.DerivedColumns);
            Assert.Empty(expectations.RoundingCalls);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~SetPredicate"`
Expected: 컴파일 실패 — `ErrorType.SetPredicateMismatch`와 `SpecExpectations.SetPredicates`가 없다.

> **주의 — 조기 반환 항은 오늘 중복항이다.** `setPredicates.Count == 0` 항을 체인에
> 더하지만, 위 테스트가 단언하듯 이 재료는 `DmlScopeFacts`와 같은 세 문장에서만
> 나오므로 항이 없어도 판정은 바뀌지 않는다. **그래도 더한다** — 설계 §6.3이 요구하는
> 규율("재료를 추가하는 태스크는 이 식에 자기 항을 반드시 잇는다")을 깨면 다음 사람이
> 그 규율을 선택적인 것으로 읽고, 불변식이 깨지는 순간 검사가 조용히 죽는다. 항 옆에
> 아래 주석을 함께 단다.
>
> ```csharp
>                 // 오늘은 중복항이다 - ExtractSetPredicates와 Extract가 같은 세 문장만
>                 // 방문하므로 setPredicates가 비지 않으면 dmlScopeFacts도 비지 않는다.
>                 // From_WithSetPredicates_ShouldExposeThemAndNeverReturnNull이 그 불변식을
>                 // 지키고, 깨지는 날 이 항이 실제로 필요해진다.
>                 && setPredicates.Count == 0
> ```

- [ ] **Step 3: `SpecExpectations`에 재료를 잇는다**

`src/ReSet.Core/Services/SpecExpectations.cs`의 `DmlScopeFacts` 속성 선언 아래에 추가한다.

```csharp
        /// <summary>
        /// DML 최상위 WHERE의 IN/NOT IN 리터럴 집합. CheckSetPredicates가 소비한다.
        /// </summary>
        public IReadOnlyList<SetPredicateFact> SetPredicates { get; init; } = Array.Empty<SetPredicateFact>();
```

`From` 메서드에서 `derivedColumns` 계산 다음 줄에 추가한다.

```csharp
            var setPredicates = DmlScopeExtractor.ExtractSetPredicates(spDef.DdlText);
```

조기 반환 AND-체인의 마지막 항 `&& derivedColumns.Count == 0` 다음에 추가한다.

```csharp
                && setPredicates.Count == 0
```

반환 객체 초기화의 `DerivedColumns = derivedColumns` 다음에 추가한다(쉼표 주의).

```csharp
                ,SetPredicates = setPredicates
```

- [ ] **Step 4: L1 검사를 더한다**

`src/ReSet.Core/Services/MechanicalValidator.cs`의 `ErrorType` 열거형에서 `DerivedTableDefinitionMissing` 다음에 추가한다.

```csharp
        SetPredicateMismatch,
```

`Validate`의 검사 디스패치에서 `CheckDerivedTableDefinitions(cleansed, expectations, result);` 다음 줄에 추가한다.

```csharp
                    CheckSetPredicates(cleansed, expectations, result);
```

`CheckDerivedTableDefinitions` 메서드 아래에 추가한다.

```csharp
        /// <summary>
        /// 기계 확정 집합 술어 표가 명세서에 옮겨졌고, 각 행의 원소 집합이 원본과
        /// 같은지 본다.
        ///
        /// [행 키는 라인 + 컬럼] 한 문장에 IN이 둘 이상일 수 있어 라인만으로는 행을
        /// 특정할 수 없다.
        ///
        /// [대조 대상은 행이 아니라 리터럴 목록 칸 하나다] 행 전체를 부분 문자열로
        /// 훑으면 숫자 리터럴에서 퇴화한다 - `| UPDATE 3 | 108 | UseState | IN | 2 |
        /// 0, 1 |`에서 "0"과 "1"을 찾으면 라인 번호 108이 이미 둘 다 담고 있어 무조건
        /// 통과한다. 칸을 꺼내 원소 집합으로 대칭 비교하면 숫자든 문자열이든 같은
        /// 규칙이 적용되고 오류 메시지가 구체화된다.
        ///
        /// [문서 전체를 훑지 않는 이유] 2026-08-18 축 A 감사 실측: EXPECT_PROC의
        /// 9개 리터럴 중 7개가 <b>다른 문장</b>에 등장한다. "각 리터럴이 문서
        /// 어딘가에 있는가"를 물으면 그 우연 덕분에 통과한다 - HeaderContractTerms의
        /// Fix Round 2가 같은 이유로 판정 단위를 문서에서 문장으로 좁혔다.
        /// </summary>
        private static void CheckSetPredicates(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.SetPredicates.Count == 0) return;

            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var (headingIndex, endIndex) = LocateSetPredicateSection(lines);

            if (headingIndex < 0)
            {
                var message =
                    $"기계 확정 집합 술어 표가 명세서에 없습니다. `{DmlScopeExtractor.SetPredicateTableHeading}` "
                    + $"헤딩과 {expectations.SetPredicates.Count}개 행을 그대로 옮겨야 합니다.";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.SetPredicateMismatch,
                    Message = message
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

            foreach (var fact in expectations.SetPredicates)
            {
                var lineToken = fact.Line.ToString();
                var row = rowLines.FirstOrDefault(r =>
                {
                    var cells = r.Split('|').Select(c => c.Trim()).ToList();
                    return cells.Any(c => c == lineToken)
                        && cells.Any(c => string.Equals(c, fact.Column, StringComparison.OrdinalIgnoreCase));
                });

                if (row == null)
                {
                    var missingRow =
                        $"집합 술어 표에 원본 DDL 라인 {fact.Line}의 컬럼 `{fact.Column}` 행이 없습니다. "
                        + "표는 기계가 확정한 것이므로 행을 생략하거나 합칠 수 없습니다.";
                    result.Errors.Add(missingRow);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.SetPredicateMismatch,
                        Message = missingRow,
                        RawContext = $"{fact.Operation} @ line {fact.Line} · {fact.Column}"
                    });
                    continue;
                }

                var cellsOfRow = row.Split('|').Select(c => c.Trim()).ToList();
                var literalCell = cellsOfRow.Count > 0 ? cellsOfRow[cellsOfRow.Count - 1] : string.Empty;
                if (literalCell.Length == 0 && cellsOfRow.Count >= 2)
                {
                    // 행이 `|`로 끝나면 마지막 조각이 빈 문자열이다.
                    literalCell = cellsOfRow[cellsOfRow.Count - 2];
                }

                var written = literalCell
                    .Split(',')
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToHashSet(StringComparer.Ordinal);
                var expected = fact.Literals.ToHashSet(StringComparer.Ordinal);

                var missing = expected.Except(written).ToList();
                var extra = written.Except(expected).ToList();
                if (missing.Count == 0 && extra.Count == 0) continue;

                var parts = new List<string>();
                if (missing.Count > 0) parts.Add($"누락: {string.Join(", ", missing)}");
                if (extra.Count > 0) parts.Add($"추가: {string.Join(", ", extra)}");

                var message =
                    $"집합 술어 표의 라인 {fact.Line} 컬럼 `{fact.Column}` 행에서 리터럴 목록이 "
                    + $"원본과 다릅니다({string.Join(" / ", parts)}). 집합의 멤버십이 대상 행을 "
                    + "정하므로 원소를 줄이거나 요약할 수 없습니다.";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.SetPredicateMismatch,
                    Message = message,
                    RawContext = $"{fact.Operation} @ line {fact.Line} · {fact.Column}"
                });
            }
        }

        /// <summary>
        /// 집합 술어 헤딩과 그 표가 끝나는 인덱스를 찾는다. LocateDmlScopeSection과
        /// 같은 이유로 다음 H2뿐 아니라 다음 H3에도 막힌다.
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateSetPredicateSection(IReadOnlyList<string> lines)
        {
            var headerIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, line => line.Trim() == DmlScopeExtractor.SetPredicateTableHeading);
            if (headerIndex < 0) return (-1, -1);

            var endIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, headerIndex + 1,
                line =>
                {
                    var trimmed = line.TrimStart();
                    return trimmed.StartsWith("## ", StringComparison.Ordinal)
                        || trimmed.StartsWith("### ", StringComparison.Ordinal);
                });

            return (headerIndex, endIndex < 0 ? lines.Count : endIndex);
        }
```

- [ ] **Step 5: 통과를 확인하고 배선 픽스처를 실측한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~SetPredicate"`
Expected: PASS, 6 tests

`From_WithSetPredicates_ShouldExposeThemAndNeverReturnNull`이 실패하면 어느 단언이 깨졌는지 본다 - `Assert.NotEmpty(expectations.DmlScopeFacts)`가 깨졌다면 Step 6의 불변식이 틀린 것이므로 거기 적힌 대로 보고한다.

- [ ] **Step 6: 조기 반환 불변식을 확인한다**

이 재료는 조기 반환 항의 레드-그린이 **성립하지 않는다** — 중복항이기 때문이다.
항을 지워도 `From_WithSetPredicates_ShouldExposeThemAndNeverReturnNull`은 계속 통과한다.
그것이 정상이며, 그 사실을 직접 확인해 계획의 주장이 맞는지 본다.

`SpecExpectations.From`의 `&& setPredicates.Count == 0` 항을 임시로 지운다.

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~From_WithSetPredicates"`
Expected: **PASS** (중복항이므로 판정이 바뀌지 않는다)

**여기서 FAIL이 나오면 계획의 불변식 주장이 틀린 것이다.** 그 경우 항을 복원하고,
어떤 DDL에서 `SetPredicates`가 비지 않는데 `DmlScopeFacts`가 비는지 찾아 보고한다 —
그것은 추출기 둘의 방문 범위가 어긋났다는 뜻이라 별개 결함이다.

확인 후 항을 복원한다.

- [ ] **Step 7: 검사 자체를 레드-그린으로 확인한다**

`CheckSetPredicates`의 첫 줄을 `return;`으로 임시 교체한다.

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~Validate_SetPredicate"`
Expected: 4개 실패. 확인 후 복원한다.

- [ ] **Step 8: 전체 스위트와 경고를 확인한다**

Run: `dotnet test` → 0 failed
Run: `dotnet build --no-incremental 2>&1 | grep -E "경고 [0-9]+개"` → `경고 9개`

- [ ] **Step 9: 커밋**

```bash
git add src/ReSet.Core/Services/SpecExpectations.cs src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: 집합 술어 표의 원소 집합을 L1이 대칭 비교한다"
```

---

## Task 4: 실물 코퍼스 골든 케이스

**Files:**
- Modify: `tests/ReSet.Core.Tests/AxisAGoldenCaseTests.cs`

**Interfaces:**
- Consumes: `DmlScopeExtractor.ExtractSetPredicates`, `SetPredicateFact` (Task 1)
- Produces: 없음(최종 안전판)

- [ ] **Step 1: 두 동기 사례를 못 박는 테스트를 쓴다**

`tests/ReSet.Core.Tests/AxisAGoldenCaseTests.cs`의 `TryReadObjectDefinition` 헬퍼 **위**에 추가한다. 이 파일의 기존 테스트와 같이, 산출물이 없으면 조용히 건너뛴다.

```csharp
        [Fact]
        public void ExtractSetPredicates_OnExpectProc_ShouldCarryTheNinePgLiterals()
        {
            // 2026-08-18 축 A 감사의 🟠. object_definition.sql:39의 9개 리터럴이
            // 명세서 어디에도 하나의 집합으로 제시되지 않아, 이관하면 4개 PG가
            // 자동회수 대상에 잘못 편입된다. 픽스처가 아니라 실물 DDL로 잡는 이유는
            // 최종 리뷰의 Critical이 "12개 태스크 리뷰가 전부 픽스처만 썼고 실물
            // 코퍼스를 안 봐서 감사의 그 문서가 통과했다"였기 때문이다.
            var ddl = TryReadObjectDefinition("dbo.UP_UTIL_SETTLE_EXPECT_PROC");
            if (ddl == null) return;

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);
            var pgName = Assert.Single(
                facts, f => f.Line == 27 && f.Column.Equals("PGName", StringComparison.OrdinalIgnoreCase));

            Assert.True(pgName.IsNegated);
            Assert.Equal(9, pgName.Literals.Count);
            Assert.Contains("'SSGPayCard'", pgName.Literals);
            Assert.Contains("'KakaoCard'", pgName.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_OnCommUpd_ShouldCarryTheSixPgWhitelist()
        {
            // 같은 감사의 두 번째 🟠. object_definition.sql:77의 6개 화이트리스트가
            // 명세서에 없어, 해외카드 수수료율이 국내건·타 PG건까지 적용될 수 있다.
            var ddl = TryReadObjectDefinition("dbo.UP_UTIL_SETTLE_COMM_UPD");
            if (ddl == null) return;

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);
            var whitelist = Assert.Single(
                facts, f => f.Literals.Count == 6 && f.Literals.Contains("'DACOMCARD'"));

            Assert.False(whitelist.IsNegated);
            Assert.Contains("'INICARD'", whitelist.Literals);
            Assert.Contains("'TOSSCARD'", whitelist.Literals);
        }

        [Theory]
        [InlineData("dbo.UP_UTIL_SETTLE_CANCEL_INS")]
        [InlineData("dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD")]
        [InlineData("dbo.UP_Util_Settle_Summary_AcqManual")]
        public void ExtractSetPredicates_ShouldNotExplodeOnGoldenProcedures(string procedureName)
        {
            // 배너가 잦으면 사람이 읽지 않는다 - 재료가 폭주하지 않는지 본다.
            // 상한 40은 SourceCommentExtractor.MaxBlocks와 같은 값이고, 코퍼스 전체
            // IN 리터럴 목록이 약 104건(SP당 평균 7)이라는 실측에 비추면 넉넉하다.
            var ddl = TryReadObjectDefinition(procedureName);
            if (ddl == null) return;

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            Assert.InRange(facts.Count, 0, 40);
            // 빈 집합은 표에 쓸 것이 없다 - 추출기가 그런 사실을 내면 안 된다.
            Assert.All(facts, f => Assert.NotEmpty(f.Literals));
            Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Column)));
        }
```

- [ ] **Step 2: 라인 번호를 실측으로 맞춘다**

위 첫 테스트의 `f.Line == 27`은 **추정값이다**. `SetPredicateFact.Line`은 IN이 있는 줄이 아니라 그 문장(UPDATE)이 시작하는 줄이다. 실제 값을 확인한다.

Run:
```bash
grep -n "PGName NOT IN" output/Objects/dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure/raw/object_definition.sql
sed -n '20,40p' output/Objects/dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure/raw/object_definition.sql
```

그 IN을 담은 UPDATE 문이 시작하는 줄 번호를 찾아 `f.Line == 27`을 그 값으로 바꾼다. `output/`이 없는 환경이면 이 테스트는 어차피 건너뛰므로, 값을 못 찾으면 조건에서 `f.Line` 절을 빼고 `f.Column`과 `f.Literals.Count == 9`로만 특정한다.

- [ ] **Step 3: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~AxisAGoldenCaseTests"`
Expected: PASS (산출물이 있으면 실제 대조, 없으면 조용히 통과)

- [ ] **Step 4: 골든 케이스가 실제로 물리는지 확인한다**

`RecordSetPredicate`에서 리터럴 수집 루프를 `literals.Add(TextOfFragment(literal));` 대신 `if (literals.Count < 5) literals.Add(TextOfFragment(literal));`로 임시 변경한다(원소를 잘라 내는 결함 흉내).

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~AxisAGoldenCaseTests"`
Expected: **FAIL** — `OnExpectProc`가 9개를 요구하는데 5개만 온다. 산출물이 없는 환경이면 이 확인을 할 수 없으므로 그 사실을 커밋 메시지에 적는다.

확인 후 복원한다.

- [ ] **Step 5: 전체 스위트와 경고를 확인한다**

Run: `dotnet test` → 0 failed
Run: `dotnet build --no-incremental 2>&1 | grep -E "경고 [0-9]+개"` → `경고 9개`

- [ ] **Step 6: 커밋**

```bash
git add tests/ReSet.Core.Tests/AxisAGoldenCaseTests.cs
git commit -m "test: 두 동기 사례를 실물 코퍼스로 못 박는다"
```

---

## Task 5: 백로그 정리

**Files:**
- Modify: `docs/todo.md`

**Interfaces:**
- Consumes: Task 1~4의 완료
- Produces: 없음

- [ ] **Step 1: 두 항목을 닫는다**

`docs/todo.md`의 `### 축 A 재감사 잔여 (2026-08-18)` 절에 있는 두 체크박스를 찾는다.

- `**`EXPECT_PROC`의 `PGName NOT IN` 9개 리터럴이 명세서에 없다**`
- `**UPDATE 매핑 표 밖의 대상 한정 리터럴이 재료로 실리지 않는다**`

두 항목을 그 절에서 지우고, 파일 아래쪽 `## 완료 기록 — 코드상 해소된 뒤 문서까지 닫은 것` 절에 다음을 추가한다.

```markdown
### 2026-08-18 집합 술어 재료로 닫은 2건 (둘 다 🟠)

- **`EXPECT_PROC`의 `PGName NOT IN` 9개 리터럴** / **`COMM_UPD` 문장 2의 6개 PG
  화이트리스트** — DML 최상위 WHERE의 `IN`/`NOT IN` 리터럴 목록을 기계 확정 재료로
  뽑아 `### 집합 술어 (기계 확정 — 수정 금지)` 표로 강제하고, L1이 그 표의
  `리터럴 목록` 칸에서 원소 집합을 대칭 비교한다. 스칼라 리터럴 비교 474건은
  노이즈로 제외했다.

  설계: [집합 술어 재료](superpowers/specs/2026-08-18-set-predicate-material-design.md)
```

절이 비면 `### 축 A 재감사 잔여 (2026-08-18)` 헤딩 자체도 지운다.

- [ ] **Step 2: 남은 참조가 없는지 확인한다**

Run: `grep -n "PGName NOT IN\|대상 한정 리터럴" docs/todo.md`
Expected: 완료 기록 절의 새 항목만 나온다.

- [ ] **Step 3: 커밋**

```bash
git add docs/todo.md
git commit -m "docs: 집합 술어 재료로 닫은 축 A 🟠 2건을 백로그에서 내린다"
```
