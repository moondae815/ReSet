# 축 A 명세서 충실도 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `Spec.md`가 원본 DDL과 어긋나는 세 뿌리(프롬프트 요구 부재 · 프롬프트 입력 오류 · 강제 부재)를 생성기에서 닫는다.

**Architecture:** 재료 하나를 순수 static 추출기가 만들고, 그 결과가 **프롬프트**와 **L1 검증기** 양쪽에 실린다. `SchemaPromptColumnSelector` ↔ `MechanicalValidator.CheckSchemaClaims` 쌍이 이미 그 형태다. 규칙만 있고 검사가 없는 상태가 구조적으로 생기지 않게 하는 것이 이 구조의 목적이다.

**Tech Stack:** .NET 10 / C# · xUnit 2.9.3 · `Microsoft.SqlServer.TransactSql.ScriptDom`(T-SQL AST) · Markdig · Serilog

**Spec:** [`docs/superpowers/specs/2026-08-17-axis-a-spec-fidelity-design.md`](../specs/2026-08-17-axis-a-spec-fidelity-design.md)

## Global Constraints

- **테스트 명령**: `dotnet test`. 실패 0, 건너뜀 0이어야 한다.
- **경고 상한**: `dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l`이 **9를 넘지 않아야** 한다. 증분 빌드는 경고를 다시 보고하지 않으므로 반드시 `clean` 후 센다.

  이 숫자는 실측이다. `ORIGINAL_BASE`(`ea66b82`)에서 직접 세어 **9건**이었고, 내역은
  `DbMetadataServiceTests.cs` 8건 + `AiServiceTests.cs` 1건이다. **`AGENTS.md:217`은
  "정확히 8건(모두 `DbMetadataServiceTests.cs`)"이라 하는데 그것이 낡았다** — 그 뒤
  `AiServiceTests.cs`에 하나가 늘었고 체크리스트가 따라가지 못했다. 이 계획의 초판도
  그 숫자를 그대로 옮겨 적었다가 Task 1 워커가 잡아냈다.

  `AGENTS.md`를 고치는 것은 이 계획의 범위가 아니다(이 계획이 만든 결함이 아니고,
  건드리면 모든 태스크의 쓰기 집합에 문서 한 개가 더 붙는다). 별건으로 올린다.
- **소프트 페일 (AGENTS.md 범주 2)**: 추출기·검사기 자체 예외는 try-catch로 격리하고 파이프라인을 죽이지 않는다. 재료를 빈 목록으로 두고 생성을 계속한다.
- **취소 정책**: 취소 가능한 `await`를 감싸는 `catch`에는 `when (ex is not OperationCanceledException)` 필터가 필수다. `CancellationPolicyTests`가 Roslyn으로 자동 검사한다. **이 계획의 추출기는 전부 동기 순수 함수이므로 해당 없음** — `await`를 넣지 마라.
- **L1 오류로 만들 수 있는 것**: 모델이 프롬프트에서 **실제로 받은** 재료만. 프롬프트에 없는 것을 L1이 요구하면 무한 재시도가 된다. 프롬프트·파서가 거짓을 말한 경우는 `SpecExpectations.InputDefects`(경고 채널)로 보낸다.
- **골든 케이스**: 감사가 `정합`으로 판정한 세 SP — `UP_UTIL_SETTLE_CANCEL_INS`, `UP_UTIL_SETTLE_INS_EXTRA4PLCARD`, `UP_Util_Settle_Summary_AcqManual`. 새 검사에서 이 셋이 결함으로 잡히면 **검사가 틀린 것**이다.
- **커밋 메시지**: 한국어 본문. 끝에 `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.

---

## File Structure

**신규 (전부 `src/ReSet.Core/Services/`)** — 재료 1건당 파일 1개. 각 파일은 순수 static 추출기 하나와 그 반환 레코드만 갖는다.

| 파일 | 책임 |
|---|---|
| `SourceCommentExtractor.cs` | DDL 주석 중 세 부류만 뽑고 앵커 토큰을 붙인다 |
| `RoundingSemanticsExtractor.cs` | `ROUND` 3인자 호출을 뽑는다 |
| `SessionOptionsExtractor.cs` | 프로시저 본문의 세션 옵션을 뽑는다 |
| `DmlScopeExtractor.cs` | DML 문장별 적용 범위 사실을 뽑는다 |
| `DerivedTableColumnExtractor.cs` | UPDATE FROM 절 파생 테이블의 컬럼 정의를 뽑는다 |

**수정**

| 파일 | 무엇을 |
|---|---|
| `src/ReSet.Core/Models/SpDefinition.cs` | `AstUpdateMapping`에 `SourceLine`·`RawTargetText`, `SpStaticAnalysisResult`에 `ThreePartTableReferences` |
| `src/ReSet.Core/Services/SqlStaticParser.cs` | 자기참조 스코프, 문장 라인, UPDATE 컬럼 귀속, 3부 참조 수집 |
| `src/ReSet.Core/Services/StaticAnalysisNormalizer.cs` | 새 필드 이월 (정규화하지 않는다) |
| `src/ReSet.Core/Services/AiService.cs` | 프롬프트 렌더·규칙·체크리스트 |
| `src/ReSet.Core/Services/SpecExpectations.cs` | 새 재료를 `init` 속성으로 싣는다 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | L1 검사 신설 + `ErrorType` 확장 |
| `docs/todo.md` | 프롬프트 유예 서술 갱신 |

**테스트 (전부 `tests/ReSet.Core.Tests/`)** — 신규 파일은 대상 클래스명 + `Tests`. 기존 파일 확장은 `SqlStaticParserTests` · `AiServiceTests_Rich` · `SpecExpectationsTests` · `MechanicalValidatorTests`.

### 테스트 계층에 관한 주의

설계 문서는 테스트를 세 층으로 나눴다 — 추출기 / L1 검사 / **프롬프트 골든**. 앞의 둘은 이 계획의 각 태스크에 들어 있다. **세 번째 층은 이 계획이 자동화하지 않는다.**

`output/Objects/*/raw/prompt-context.md`는 분석 실행이 만드는 산출물이라 재생성에 DB 연결과 LLM 호출이 필요하다. 단위테스트의 전제로 삼으면 CI가 외부 환경에 묶인다. 대신 **`AiServiceTests_Rich`의 프롬프트 단언**이 같은 자리를 지킨다 — `CreateProbe()`가 HTTP 핸들러를 가로채므로 실제 전송 본문을 LLM 호출 없이 검사할 수 있다(Task 2 Step 5, Task 9 Step 6).

디스크의 `prompt-context.md`와 실제로 diff를 떠 보는 것은 **구현 후 사람이 한 번 하는 확인**이지 태스크의 완료 조건이 아니다. 특히 Task 1을 고친 뒤에는 `자기 자신을 참조합니다: OutState, OutYMD` → `... OutState`로 바뀌어야 한다.

---

# 1단계 — 입력 정확성 (설계 1)

이 단계의 결함은 **재생성이 고칠 수 없는 코드 버그**다. L1 오류를 새로 만들지 않는다(Task 4의 표기 주장 검사만 예외 — 그건 모델이 프롬프트에서 받은 재료 위에 서 있다).

## Task 1: 거짓 자기참조 제거

**Files:**
- Modify: `src/ReSet.Core/Services/SqlStaticParser.cs:431-471` (`FindSelfReferences`, `ColumnReferenceCollector`)
- Test: `tests/ReSet.Core.Tests/SqlStaticParserTests.cs`

**Interfaces:**
- Consumes: 없음 (첫 태스크)
- Produces: `AstUpdateMapping.SelfReferencedColumns`의 의미가 좁아진다 — 중첩 질의 컬럼과 대상이 아닌 별칭의 컬럼은 더 이상 담기지 않는다.

**배경:** `ColumnReferenceCollector`가 `NewValue` 식 트리 전체를 훑으며 **맨이름만** 비교한다. `OutYMD = (SELECT OutYMD FROM dbo.UIF_SettleYMD(...))`에서 서브쿼리 안의 `OutYMD`를 자기참조로 단정한다. 실측 근거는 `output/Objects/dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure/raw/prompt-context.md:67`.

- [ ] **Step 1: 실패하는 테스트 두 개를 쓴다**

`tests/ReSet.Core.Tests/SqlStaticParserTests.cs`의 클래스 안에 추가한다.

```csharp
[Fact]
public void Analyze_SetRightHandScalarSubquery_IsNotASelfReference()
{
    // EXPECT_PROC:203-205 실측 형태. OutState는 참인 자기참조이고
    // OutYMD는 TVF가 돌려주는 남의 컬럼이다. 둘이 한 SET 절에 있어서
    // "판정을 통째로 끄는" 편법으로는 이 테스트를 통과할 수 없다.
    var ddlText = @"
CREATE PROCEDURE dbo.UpdateSettleDates
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE dbo.TSettleMst
    SET    OutState = IIF(OutState=0, 2, OutState)
          ,OutYMD   = (SELECT OutYMD FROM dbo.UIF_SettleYMD(A.YMD, B.SettlePeriodID))
    FROM   dbo.TSettleMst    A
    JOIN   dbo.TClientCMRate B ON A.ClientID = B.ClientID
    WHERE  A.YMD = @pi_strYMD
END";

    var result = new SqlStaticParser().Analyze(ddlText);

    var mapping = Assert.Single(result.AstUpdateMappings);
    Assert.Contains("OutState", mapping.SelfReferencedColumns);
    Assert.DoesNotContain("OutYMD", mapping.SelfReferencedColumns);
}

[Fact]
public void Analyze_SetRightHandOtherAliasColumn_IsNotASelfReference()
{
    // 갱신 대상 별칭이 A일 때 B.OutYMD는 이름만 같은 남의 컬럼이다.
    var ddlText = @"
CREATE PROCEDURE dbo.CopySettleDates
AS
BEGIN
    UPDATE A
    SET    A.OutState = IIF(A.OutState=0, 2, A.OutState)
          ,A.OutYMD   = B.OutYMD
    FROM   dbo.TSettleMst  A
    JOIN   dbo.TSettleHist B ON A.ID = B.ID
END";

    var result = new SqlStaticParser().Analyze(ddlText);

    var mapping = Assert.Single(result.AstUpdateMappings);
    Assert.Contains("OutState", mapping.SelfReferencedColumns);
    Assert.DoesNotContain("OutYMD", mapping.SelfReferencedColumns);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SqlStaticParserTests&FullyQualifiedName~SelfReference"`
Expected: 두 테스트 모두 FAIL — `SelfReferencedColumns`에 `OutYMD`가 들어 있다.

- [ ] **Step 3: `ColumnReferenceCollector`가 중첩 질의로 내려가지 않게 한다**

`SqlStaticParser.cs:461-471`의 `ColumnReferenceCollector`를 통째로 교체한다.

```csharp
private sealed class ColumnReferenceCollector : TSqlFragmentVisitor
{
    /// <summary>컬럼 이름이 아니라 참조 노드를 담는다 - 한정자 판정에 필요하다.</summary>
    public List<ColumnReferenceExpression> Columns { get; } = new();

    /// <summary>
    /// 중첩 질의 안으로 내려가지 않는다. 그 스코프의 컬럼은 다른 테이블 소속이다.
    /// (SELECT OutYMD FROM dbo.UIF_SettleYMD(...))의 OutYMD를 거둬 오면 갱신
    /// 대상과 이름만 같은 남의 컬럼을 자기참조로 단정한다 - EXPECT_PROC:203-205
    /// 에서 실측된 오탐이며, 그 거짓 문장이 그대로 프롬프트에 실렸다.
    ///
    /// base를 부르지 않는 것이 곧 하위 순회 중단이다.
    /// </summary>
    public override void ExplicitVisit(ScalarSubquery node) { }

    public override void Visit(ColumnReferenceExpression node) => Columns.Add(node);
}
```

- [ ] **Step 4: `FindSelfReferences`에 한정자 규칙을 넣는다**

`SqlStaticParser.cs:436-458`의 `FindSelfReferences` 본문을 교체하고, 아래 두 헬퍼를 그 뒤에 추가한다.

```csharp
private static List<string> FindSelfReferences(
    UpdateSpecification node, List<AstUpdateAssignment> assignments)
{
    var targets = new HashSet<string>(
        assignments.Select(a => a.Column), StringComparer.OrdinalIgnoreCase);
    var targetAlias = ExtractTargetAlias(node);
    var found = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var clause in node.SetClauses.OfType<AssignmentSetClause>())
    {
        if (clause.NewValue == null) continue;

        var collector = new ColumnReferenceCollector();
        clause.NewValue.Accept(collector);

        foreach (var reference in collector.Columns)
        {
            var column = LastIdentifier(reference.MultiPartIdentifier);
            if (column == null || !targets.Contains(column)) continue;

            // 한정자가 붙었고 갱신 대상 별칭을 알 때만 한정자를 본다.
            // 대상이 별칭이 아니라 테이블 이름이면(UPDATE dbo.T SET ...)
            // ExtractTargetAlias가 null을 돌려주고 이 규칙은 적용되지 않는다.
            var qualifier = QualifierOf(reference.MultiPartIdentifier);
            if (targetAlias != null
                && qualifier != null
                && !string.Equals(qualifier, targetAlias, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(column)) found.Add(column);
        }
    }

    return found;
}

/// <summary>
/// UPDATE A SET ... FROM T A 형태의 갱신 대상 별칭. 대상이 한정된 테이블
/// 이름이면(부(部)가 둘 이상) 별칭이 아니므로 null이다 - 이 경우 한정자
/// 규칙을 적용하지 않는 쪽이 안전하다.
/// </summary>
private static string? ExtractTargetAlias(UpdateSpecification node)
{
    if (node.Target is NamedTableReference named)
    {
        var identifiers = named.SchemaObject?.Identifiers;
        if (identifiers != null && identifiers.Count == 1)
        {
            return identifiers[0].Value;
        }
    }

    return null;
}

private static string? QualifierOf(MultiPartIdentifier? identifier)
{
    var parts = identifier?.Identifiers;
    if (parts == null || parts.Count < 2) return null;
    return parts[parts.Count - 2].Value;
}
```

- [ ] **Step 5: 테스트 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SqlStaticParserTests"`
Expected: PASS. 기존 42개 테스트도 함께 통과해야 한다 — 실패하면 자기참조 판정을 과도하게 좁힌 것이다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/SqlStaticParser.cs tests/ReSet.Core.Tests/SqlStaticParserTests.cs
git commit -m "$(cat <<'EOF'
fix: SET 우변의 중첩 질의 컬럼을 자기참조로 세지 않는다

EXPECT_PROC의 OutYMD = (SELECT OutYMD FROM dbo.UIF_SettleYMD(...))에서
서브쿼리 안의 OutYMD가 자기참조로 잡혀 프롬프트가 거짓을 단언했다.
수집기가 중첩 질의로 내려가지 않게 하고, 한정자가 붙은 참조는 그 별칭이
갱신 대상일 때만 자기참조로 본다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: 문장 앵커에 원본 라인을 병기

**Files:**
- Modify: `src/ReSet.Core/Models/SpDefinition.cs:27-41` (`AstUpdateMapping`)
- Modify: `src/ReSet.Core/Services/SqlStaticParser.cs:391-400` (`RecordUpdateMapping`)
- Modify: `src/ReSet.Core/Services/StaticAnalysisNormalizer.cs:67-87`
- Modify: `src/ReSet.Core/Services/AiService.cs:196`, `:559`
- Test: `tests/ReSet.Core.Tests/SqlStaticParserTests.cs`, `tests/ReSet.Core.Tests/StaticAnalysisNormalizerTests.cs`, `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`

**Interfaces:**
- Consumes: Task 1의 `AstUpdateMapping`
- Produces: `AstUpdateMapping.SourceLine` (`int`, 1부터. 파싱 실패 시 0). 프롬프트 헤딩이 `### UPDATE 대상 테이블: {표} (문장 N · 원본 DDL 라인 L)` 형태가 된다.

**배경:** `_updateOrdinals`(`:187`, 채번 `:389-395`)가 대상 테이블별 카운터인데 청킹 경로가 파서를 여러 번 돌려 리셋된다. 실측 산출물에 같은 대상 테이블로 `문장 1`이 두 번 나오고 `문장 2` 다음이 `문장 8`이다. 순번은 `CheckUpdateMappings`가 쓰므로 **없애지 않고 병기**한다.

**⚠️ 기존 테스트가 깨진다:** `AiServiceTests_Rich.cs:381`의 `Assert.Contains("### UPDATE 대상 테이블: DB.dbo.TCommMst (문장 2)", body)`가 실패한다. Step 5에서 함께 고친다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SqlStaticParserTests.cs`:

```csharp
[Fact]
public void Analyze_UpdateMapping_ShouldCarryTheSourceLine()
{
    // 청킹 경로가 카운터를 리셋해 StatementOrdinal이 앵커로 못 쓰인다.
    // 라인은 청킹과 무관하게 유일하고 object_definition.sql로 대조된다.
    var ddlText = @"CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.T SET C = 1
END";

    var result = new SqlStaticParser().Analyze(ddlText);

    var mapping = Assert.Single(result.AstUpdateMappings);
    Assert.Equal(4, mapping.SourceLine);
}
```

`tests/ReSet.Core.Tests/StaticAnalysisNormalizerTests.cs`:

```csharp
[Fact]
public void Normalize_ShouldPreserveTheUpdateMappingSourceLine()
{
    // 정규화는 테이블 이름만 다룬다. 라인을 잃으면 앵커가 프롬프트에 닿지 않는다.
    var analysis = new SpStaticAnalysisResult();
    var mapping = new AstUpdateMapping
    {
        TargetTable = "dbo.T",
        StatementOrdinal = 1,
        SourceLine = 42
    };
    mapping.Assignments.Add(new AstUpdateAssignment { Column = "C", SourceExpression = "1" });
    analysis.AstUpdateMappings.Add(mapping);

    var normalized = StaticAnalysisNormalizer.Normalize(analysis, "DB", "dbo");

    Assert.Equal(42, Assert.Single(normalized.AstUpdateMappings).SourceLine);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SourceLine"`
Expected: FAIL — `AstUpdateMapping`에 `SourceLine`이 없어 컴파일 오류.

- [ ] **Step 3: 모델·파서·정규화기에 필드를 넣는다**

`src/ReSet.Core/Models/SpDefinition.cs`의 `AstUpdateMapping` 안, `StatementOrdinal` 바로 아래:

```csharp
/// <summary>
/// 원본 DDL에서 이 UPDATE 문장이 시작하는 줄 번호(1부터). 파싱 실패 시 0.
///
/// StatementOrdinal이 앵커로 못 쓰이기 때문에 있다 - 채번이 대상 테이블별이고
/// 청킹 경로가 파서를 여러 번 돌려 리셋되므로 "문장 1"이 여러 번 나온다.
/// 라인은 청킹과 무관하게 유일하고 object_definition.sql로 사람이 대조한다.
/// </summary>
public int SourceLine { get; set; }
```

`src/ReSet.Core/Services/SqlStaticParser.cs`의 `RecordUpdateMapping` 안 `new AstUpdateMapping { ... }` 초기화자에 한 줄 추가:

```csharp
SourceLine = node.StartLine,
```

`src/ReSet.Core/Services/StaticAnalysisNormalizer.cs`의 `new AstUpdateMapping { ... }` 초기화자에 한 줄 추가:

```csharp
SourceLine = mapping.SourceLine,
```

- [ ] **Step 4: 프롬프트 렌더에 병기한다**

`src/ReSet.Core/Services/AiService.cs:559`:

```csharp
lines.Add($"   ### UPDATE 대상 테이블: {mapping.TargetTable} (문장 {mapping.StatementOrdinal} · 원본 DDL 라인 {mapping.SourceLine})");
```

`src/ReSet.Core/Services/AiService.cs:196`:

```csharp
staticAnalysisText.AppendLine($"    <update-target table=\"{mapping.TargetTable}\" statement=\"{mapping.StatementOrdinal}\" line=\"{mapping.SourceLine}\">");
```

- [ ] **Step 5: 깨진 기존 테스트를 고치고 새 단언을 넣는다**

`tests/ReSet.Core.Tests/AiServiceTests_Rich.cs:205-216`의 `Mapping()` 헬퍼에 `SourceLine = 77,`을 초기화자에 추가한다. 그리고 `:381`의 단언을 바꾼다.

```csharp
Assert.Contains("### UPDATE 대상 테이블: DB.dbo.TCommMst (문장 2 · 원본 DDL 라인 77)", body);
```

- [ ] **Step 6: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0, 건너뜀 0.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Models/SpDefinition.cs src/ReSet.Core/Services/SqlStaticParser.cs \
        src/ReSet.Core/Services/StaticAnalysisNormalizer.cs src/ReSet.Core/Services/AiService.cs \
        tests/ReSet.Core.Tests/SqlStaticParserTests.cs \
        tests/ReSet.Core.Tests/StaticAnalysisNormalizerTests.cs \
        tests/ReSet.Core.Tests/AiServiceTests_Rich.cs
git commit -m "$(cat <<'EOF'
feat: UPDATE 문장 앵커에 원본 DDL 라인을 병기한다

문장 순번은 대상 테이블별 채번이고 청킹 경로가 파서를 여러 번 돌려
리셋되므로 앵커로 쓸 수 없다. 실측 산출물에 같은 대상으로 "문장 1"이
두 번, 그다음이 "문장 8"이다. 순번은 CheckUpdateMappings가 쓰므로
없애지 않고 라인을 병기한다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: UPDATE SET 대상 컬럼을 스키마 표에 살린다

**Files:**
- Modify: `src/ReSet.Core/Services/SqlStaticParser.cs:898-913`
- Test: `tests/ReSet.Core.Tests/SqlStaticParserTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `SpStaticAnalysisResult.ReferencedColumnsPerTable`이 한정자 없는 UPDATE SET 대상 컬럼을 포함한다. 이것이 `SchemaPromptColumnSelector.Select`의 `keepCols`에 들어가 프롬프트 스키마 표에 실린다.

**배경:** 컬럼 리졸버의 폴백 블록(`:898-913`)에 **INSERT 분기(`:900`)는 있는데 UPDATE 분기가 없다.** 한정자 없는 `SET EDIReqYmd = ...`는 로컬 스코프에서 못 풀고 → INSERT 분기에 안 걸리고 → `ReferencedTables.Count == 1` 폴백도 큰 SP에서는 거짓 → `"Unknown"`으로 버려진다.

그 귀결이 실측됐다. `EDIReqYmd`가 `prompt-context.md:82`(UPDATE 표)·`:766`(정적분석)·`:1026`(원본 DDL)에 있는데 `<referenced-table-schemas>`(144–323행)에는 **없다.** 규칙 `AiService.cs:389`가 "스키마에 없으면 스키마 불일치로 표기하라"고 명령하므로 명세서는 시킨 대로 했다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
[Fact]
public void Analyze_UnqualifiedUpdateSetColumn_ShouldBeAttributedToTheUpdateTarget()
{
    // EXPECT_PROC 실측: EDIReqYmd가 UPDATE 표에는 실리는데 스키마 표에서는
    // 사라져, 규칙 389("스키마에 없으면 불일치로 표기")가 명세서에 거짓
    // 스키마 불일치를 쓰게 했다. 리졸버 폴백에 UPDATE 분기가 없어서다.
    var ddlText = @"
CREATE PROCEDURE dbo.SetEdiDate
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE dbo.TSettleMst
    SET    EDIReqYmd = E.ReqYMD
    FROM   dbo.TSettleMst   A
    JOIN   dbo.TPLCardEDIMst E ON A.PLTID = E.PLTID
    WHERE  A.YMD = @pi_strYMD
END";

    var result = new SqlStaticParser().Analyze(ddlText);

    var attributed = result.ReferencedColumnsPerTable
        .Where(kvp => kvp.Key.EndsWith("TSettleMst", StringComparison.OrdinalIgnoreCase))
        .SelectMany(kvp => kvp.Value)
        .ToList();

    Assert.Contains("EDIReqYmd", attributed, StringComparer.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~UnqualifiedUpdateSetColumn"`
Expected: FAIL — `EDIReqYmd`가 어느 테이블에도 귀속되지 않았다.

- [ ] **Step 3: 폴백에 UPDATE 분기를 넣는다**

`SqlStaticParser.cs:898`의 `if (!resolvedLocally)` 블록에서, INSERT 분기(`:900`) **바로 다음**에 `else if` 하나를 끼운다. 대상을 새로 추론하지 않고 `RecordUpdateMapping`이 이미 푼 것을 쓴다.

```csharp
if (!resolvedLocally)
{
    if (_statementContext.Count > 0 && _statementContext.Peek() == "INSERT" && !string.IsNullOrEmpty(_currentInsertTarget))
    {
        targetTable = _currentInsertTarget;
    }
    // 한정자 없는 SET 대상 컬럼은 갱신 대상 테이블의 것이다. 이 분기가
    // 없으면 "Unknown"으로 버려져 프롬프트 스키마 표에서 사라지고, 규칙
    // 389가 명세서에 거짓 "스키마 불일치"를 쓰게 한다(EXPECT_PROC 실측).
    // 대상을 새로 추론하지 않는다 - RecordUpdateMapping이 이미 푼 값만 쓴다.
    else if (_statementContext.Count > 0 && _statementContext.Peek() == "UPDATE"
             && _dmlTargetResolved && !string.IsNullOrEmpty(_currentUpdateTarget))
    {
        targetTable = _currentUpdateTarget;
    }
    else if (ReferencedTables.Count == 1)
    {
        targetTable = ReferencedTables[0];
    }
    else if (ReferencedTables.Count == 0 && CreatedTempTables.Count == 1)
    {
        targetTable = CreatedTempTables[0];
    }
}
```

`_currentUpdateTarget` 필드를 `_currentInsertTarget` 선언 옆에 추가하고, `ExplicitVisit(UpdateSpecification)`에서 `RecordDmlTarget`이 푼 값을 넣고 빠져나올 때 되돌린다.

```csharp
private string? _currentUpdateTarget;
```

`ExplicitVisit(UpdateSpecification node)` 안 (`:329-350`):

```csharp
var prevUpdateTarget = _currentUpdateTarget;
_currentUpdateTarget = _dmlTargetResolved ? resolvedTarget : null;

// ... 기존 RecordUpdateMapping 호출과 base.ExplicitVisit(node) ...

_currentUpdateTarget = prevUpdateTarget;
```

- [ ] **Step 4: 테스트 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SqlStaticParserTests"`
Expected: PASS, 기존 테스트 포함 전부.

- [ ] **Step 5: 남은 8컬럼이 닫혔는지 실측한다**

설계 문서가 미확정으로 남긴 잔여다. 보고서가 `EXPECT_PROC`에서 함께 든 `CollectMonth2/3` · `CollectDay2/3` · `CollectTxSDay2/3` · `CollectTxEDay2/3`는 **읽기 컬럼이라 경로가 다르다.** Step 3의 수정으로 닫히는지 확인한다.

```bash
# 이 8컬럼이 원본에서 어떤 표기로 참조되는지 본다
grep -n "CollectMonth2\|CollectDay2\|CollectTxSDay2\|CollectTxEDay2" \
  output/Objects/dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure/raw/object_definition.sql

# 프롬프트 스키마 표에 실렸는지 본다 (144-323행이 <referenced-table-schemas>)
sed -n '144,323p' output/Objects/dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure/raw/prompt-context.md \
  | grep -c "CollectMonth2"
```

판정과 처리는 둘 중 하나다.

- **한정자가 붙어 있고(예: `B.CollectMonth2`) 별칭이 풀리는 경우** — Step 3과 무관하게 이미 귀속됐거나, 별칭 맵이 못 푼 것이다. 후자면 이 태스크의 범위를 넘으므로 아래 처리를 따른다.
- **어느 쪽이든 프롬프트 스키마 표에 실리지 않는 경우** — `SchemaPromptColumnSelector.DetectOrphanedColumnKeys`가 경고로 표면화하도록 넓힌다. **L1 오류로 만들지 마라** — 프롬프트가 그 컬럼을 보여 주지 않았으므로 재생성이 고칠 수 없다(Global Constraints).

결과를 어느 쪽이든 커밋 메시지 본문에 한 줄로 남긴다. 이 확인을 했다는 사실이 다음 사람에게 필요하다.

- [ ] **Step 6: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0, 건너뜀 0. `SpecExpectationsTests`가 특히 중요하다 — `PromptSchemaColumns`가 넓어지면 `CheckSchemaClaims`의 대조 기준이 바뀐다.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/SqlStaticParser.cs tests/ReSet.Core.Tests/SqlStaticParserTests.cs
git commit -m "$(cat <<'EOF'
fix: 한정자 없는 UPDATE SET 대상 컬럼을 갱신 대상 테이블에 귀속한다

컬럼 리졸버 폴백에 INSERT 분기는 있는데 UPDATE 분기가 없어
SET EDIReqYmd = ... 같은 컬럼이 Unknown으로 버려졌다. 그 결과 프롬프트가
"이 컬럼을 UPDATE한다"와 "스키마에 이 컬럼이 없다"를 동시에 말했고,
규칙 389가 명세서에 거짓 스키마 불일치를 쓰게 했다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: 표기 출처 병기와 표기 주장 검사

**Files:**
- Modify: `src/ReSet.Core/Models/SpDefinition.cs` (`AstUpdateMapping.RawTargetText`, `SpStaticAnalysisResult.ThreePartTableReferences`)
- Modify: `src/ReSet.Core/Services/SqlStaticParser.cs`
- Modify: `src/ReSet.Core/Services/StaticAnalysisNormalizer.cs`
- Modify: `src/ReSet.Core/Services/AiService.cs:559`, 규칙 목록
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs`
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/SqlStaticParserTests.cs`, `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: Task 2의 `AstUpdateMapping.SourceLine`
- Produces:
  - `AstUpdateMapping.RawTargetText` (`string?`) — `UPDATE` 대상의 원문 표기
  - `SpStaticAnalysisResult.ThreePartTableReferences` (`List<string>`) — 3부 이상으로 쓰인 테이블 참조의 원문
  - `SpecExpectations.HasThreePartReference` (`bool`), `SpecExpectations.HasLinkedServerReference` (`bool`)
  - `ErrorType.IdentifierNotationClaim`

**배경:** 프롬프트가 전 구간을 `SETTLE_POQ_DB.dbo.TSettleMst`(정규화)로 쓰는데 원본은 `UPDATE dbo.TSettleMst`(2부)다. `UP_UTIL_STAT_PGCOLLECT_INS`는 원본에 3부 참조가 **0건인데도** Spec이 "3부 식별자 기반 크로스 데이터베이스 참조이며 Linked Server 원격 참조가 아닙니다"라고 단언했다.

- [ ] **Step 1: 파서 테스트를 쓴다**

```csharp
[Fact]
public void Analyze_ShouldRecordTheRawTargetNotationAndThreePartReferences()
{
    var ddlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.TSettleMst
    SET    C = 1
    FROM   SETTLE_POQ_DB.dbo.TSettleMst A
END";

    var result = new SqlStaticParser().Analyze(ddlText);

    Assert.Equal("dbo.TSettleMst", Assert.Single(result.AstUpdateMappings).RawTargetText);
    Assert.Contains("SETTLE_POQ_DB.dbo.TSettleMst", result.ThreePartTableReferences);
}

[Fact]
public void Analyze_WithOnlyOnePartNames_ShouldReportNoThreePartReferences()
{
    // STAT_PGCOLLECT_INS 실측: 모든 참조가 비수식인데 Spec이 3부 식별자
    // 크로스 DB 참조라고 단언했다.
    var ddlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    INSERT INTO TStatPGCollect (C) SELECT C FROM TSettleMst
END";

    var result = new SqlStaticParser().Analyze(ddlText);

    Assert.Empty(result.ThreePartTableReferences);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ThreePart|FullyQualifiedName~RawTargetNotation"`
Expected: FAIL — 두 멤버가 없어 컴파일 오류.

- [ ] **Step 3: 모델과 파서에 원문 표기를 싣는다**

`SpDefinition.cs`의 `AstUpdateMapping`에 추가:

```csharp
/// <summary>
/// UPDATE 대상의 원문 표기. TargetTable은 정규화된 3부 이름이라 원본이
/// 실제로 몇 부로 썼는지 잃는다. 명세서가 정규화 이름을 원문처럼 서술해
/// "3부 식별자 크로스 DB 참조" 같은 없는 사실을 단언한 실측이 있다.
/// 정규화기는 이 값을 canonicalize하지 않고 그대로 옮긴다.
/// </summary>
public string? RawTargetText { get; set; }
```

`SpStaticAnalysisResult`에 추가:

```csharp
/// <summary>
/// 원본이 3부 이상으로 표기한 테이블 참조의 원문. 비어 있으면 이 SP에
/// 크로스 DB 참조가 없다는 뜻이며, L1이 명세서의 표기 주장을 이것으로
/// 반증한다. 정규화 대상이 아니다 - 원문이어야 근거가 된다.
/// </summary>
public List<string> ThreePartTableReferences { get; set; } = new();
```

`SqlStaticParser.cs`의 `RecordUpdateMapping` 초기화자에 추가:

```csharp
RawTargetText = GetFragmentText(node.Target),
```

방문자에 `SchemaObjectName` 수집을 추가한다(`ExplicitVisit(UpdateSpecification)` 근처, 다른 `ExplicitVisit`들과 나란히).

```csharp
public List<string> ThreePartTableReferences { get; } = new();

public override void ExplicitVisit(SchemaObjectName node)
{
    var identifiers = node.Identifiers;
    if (identifiers != null && identifiers.Count >= 3)
    {
        var text = string.Join(".", identifiers.Select(i => i.Value));
        if (!ThreePartTableReferences.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            ThreePartTableReferences.Add(text);
        }
    }

    base.ExplicitVisit(node);
}
```

`Analyze`의 결과 조립부(`:69` 근처, `result.ReferencedColumnsPerTable = visitor.ReferencedColumnsPerTable;` 옆)에 추가:

```csharp
result.ThreePartTableReferences = visitor.ThreePartTableReferences;
```

`StaticAnalysisNormalizer.cs`: `AstUpdateMapping` 복사 초기화자에 `RawTargetText = mapping.RawTargetText,`를 추가하고, `normalized` 조립부에 `normalized.ThreePartTableReferences = new List<string>(analysis.ThreePartTableReferences);`를 추가한다. **둘 다 canonicalize하지 않는다.**

- [ ] **Step 4: 프롬프트에 원문을 병기하고 규칙을 붙인다**

`AiService.cs:559`를 다시 고친다(Task 2에서 라인을 넣은 그 줄).

```csharp
var rawNotation = string.IsNullOrWhiteSpace(mapping.RawTargetText)
    ? string.Empty
    : $" · 원문 표기: {mapping.RawTargetText}";
lines.Add($"   ### UPDATE 대상 테이블: {mapping.TargetTable} (문장 {mapping.StatementOrdinal} · 원본 DDL 라인 {mapping.SourceLine}{rawNotation})");
```

규칙 목록(`AiService.cs:389` 바로 다음)에 한 줄 추가한다. **(a) 병기 없이 (b) 규칙만 넣으면 모델에게 지킬 근거가 없으므로 둘은 짝이다.**

```csharp
rules.Add($"{ruleIndex++}. Table names in the static analysis metadata are PARSER-NORMALIZED three-part names, not the source's own notation. When you describe how many parts the source identifier has (one-part, two-part, three-part, cross-database, Linked Server), base it ONLY on <sp-source-ddl>. Do not claim a cross-database or three-part reference that does not appear there.");
```

- [ ] **Step 5: L1 표기 주장 검사를 쓴다 (실패 테스트 먼저)**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`:

```csharp
[Fact]
public void Validate_ThreePartClaimWithoutAnyThreePartReference_ShouldBeAnError()
{
    // STAT_PGCOLLECT_INS 실측. 원본은 전부 1부 표기인데 Spec이 3부
    // 크로스 DB 참조라고 단언했다.
    var expectations = EmptyExpectations() with
    {
        HasThreePartReference = false,
        HasLinkedServerReference = false
    };
    var markdown = RequiredHeadersMarkdown()
        + "\n이 프로시저는 3부 식별자 기반 크로스 데이터베이스 참조이며 Linked Server 원격 참조가 아닙니다.\n";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.IdentifierNotationClaim);
}

[Fact]
public void Validate_ThreePartClaimWithAThreePartReference_ShouldPass()
{
    var expectations = EmptyExpectations() with { HasThreePartReference = true };
    var markdown = RequiredHeadersMarkdown()
        + "\n이 프로시저는 3부 식별자로 다른 데이터베이스를 참조합니다.\n";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.IdentifierNotationClaim);
}
```

두 헬퍼를 `MechanicalValidatorTests` 클래스 하단에 추가한다. **`RequiredHeadersMarkdown`은 기존 `WrapSpec(crudBody)`(`:1189`)에 얹는다** — 필수 H2 다섯 개와 Mermaid 블록을 이미 옳게 만들고 있으므로 병렬 골격을 새로 두면 둘이 어긋난다.

```csharp
private static SpecExpectations EmptyExpectations() =>
    new(
        Array.Empty<UpdateColumnExpectation>(),
        new Dictionary<string, IReadOnlySet<string>>(),
        new HashSet<string>(),
        Array.Empty<string>());

/// <summary>
/// L1 구조 검사를 통과하는 최소 명세서. 아래 테스트들은 여기에 문장을 이어
/// 붙여 쓰는데, WrapSpec이 닫는 코드 펜스 뒤에 붙으므로 ComputeFenceLineFlags가
/// 펜스 밖으로 본다 - 검사 대상이 된다.
/// </summary>
private static string RequiredHeadersMarkdown() => WrapSpec("내용");
```

- [ ] **Step 6: `SpecExpectations`에 두 플래그를 싣는다**

`SpecExpectations.cs`의 record 본문(포지셔널 파라미터 **뒤**, 중괄호 안)에 추가한다. 포지셔널을 늘리면 기존 호출부가 전부 깨지므로 `init` 속성으로 둔다.

```csharp
/// <summary>원본이 3부 이상으로 표기한 테이블 참조가 하나라도 있는가.</summary>
public bool HasThreePartReference { get; init; }

/// <summary>원본에 Linked Server(4부) 참조가 있는가.</summary>
public bool HasLinkedServerReference { get; init; }
```

**⚠️ 이 태스크에서 가장 놓치기 쉬운 곳이다.** `From`은 세 재료가 전부 비면 `null`을 돌려주고(`:77-80`), 호출부는 `null`을 "종전 동작 = 대조 건너뜀"으로 받는다. 새 재료를 실어도 **조기 반환 조건을 함께 넓히지 않으면 새 검사가 조용히 한 번도 돌지 않는다.**

`analysis == null`을 조건에 넣는 방식은 쓰지 마라. `SpDefinition.StaticAnalysis`는 `= new()`로 선언된 비-nullable 속성이라(`SpDefinition.cs:16`) 절대 `null`이 아니고, 그 항은 죽은 코드가 되면서 `From`이 사실상 never-null이 된다.

대신 **재료를 먼저 계산하고 "하나라도 있는가"를 한 곳에서 판정한다.** 뒤 태스크가 재료를 더할 때 이 식에 항을 하나씩 잇는다.

`From`의 기존 조기 반환(`:77-80`)과 반환문(`:82-83`)을 아래로 통째로 교체한다.

```csharp
var analysis = spDef.StaticAnalysis;
var hasThreePartReference = analysis.ThreePartTableReferences.Count > 0;
var hasLinkedServerReference = analysis.LinkedServerReferences.Count > 0;

// 대조할 것이 하나도 없을 때만 null이다. 재료를 추가하는 태스크는 이 식에
// 자기 항을 반드시 이어야 한다 - 빠뜨리면 그 검사가 한 번도 돌지 않고,
// 스위트는 초록으로 남는다.
if (updateColumns.Count == 0
    && promptSchemaColumns.Count == 0
    && inputDefects.Count == 0
    && !hasThreePartReference
    && !hasLinkedServerReference)
{
    return null;
}

return new SpecExpectations(
    updateColumns, promptSchemaColumns, columnlessDependencyTables, inputDefects)
{
    HasThreePartReference = hasThreePartReference,
    HasLinkedServerReference = hasLinkedServerReference
};
```

이 태스크의 두 L1 테스트가 그 배선을 지킨다 — `EmptyExpectations()`를 직접 만들어 `Validate`에 넘기므로 `From`을 우회하지만, 뒤 태스크(Task 5 Step 9의 전체 실행)에서 실제 파이프라인이 돈다.

- [ ] **Step 7: L1 검사를 구현한다**

`MechanicalValidator.cs`의 `ErrorType` enum에 `IdentifierNotationClaim`을 추가하고, `Validate`의 `if (expectations != null)` 블록에 `CheckIdentifierNotationClaims(cleansed, expectations, result);`를 더한 뒤 메서드를 구현한다.

```csharp
private static readonly string[] ThreePartClaimTokens =
{
    "3부 식별자", "세 부분 식별자", "크로스 데이터베이스 참조", "크로스 DB 참조"
};

/// <summary>
/// 원본에 3부 참조가 하나도 없는데 명세서가 3부·크로스 DB 참조를 단언하는지 본다.
///
/// 파서가 정규화한 이름을 프롬프트가 원문처럼 보여 준 것이 원인이라 재생성으로
/// 고칠 수 있다 - 그래서 InputDefects가 아니라 L1 오류다. 다만 프롬프트가
/// 원문 표기를 함께 주기 시작한 뒤에만 성립한다(AiService의 원문 병기와 규칙).
///
/// Linked Server 주장은 별도로 보지 않는다. LinkedServerReferences가 비었는데
/// 4부 참조를 단언하는 경우는 같은 조건에 걸린다.
/// </summary>
private static void CheckIdentifierNotationClaims(
    string markdown, SpecExpectations expectations, ValidationResult result)
{
    if (expectations.HasThreePartReference || expectations.HasLinkedServerReference) return;

    var lines = MarkdownSectionLocator.SplitLines(markdown);
    var fenceFlags = ComputeFenceLineFlags(lines);

    for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
    {
        if (fenceFlags[lineIndex]) continue;

        var line = lines[lineIndex];
        if (!Array.Exists(ThreePartClaimTokens, t => line.Contains(t, StringComparison.Ordinal)))
        {
            continue;
        }

        var message =
            "명세서가 3부 식별자 또는 크로스 데이터베이스 참조를 단언했으나, "
            + "원본 DDL에는 3부 이상으로 표기된 테이블 참조가 없습니다. "
            + "식별자 표기는 <sp-source-ddl>만 근거로 삼아야 합니다.";
        result.Errors.Add(message);
        result.DetailedErrors.Add(new DetailedError
        {
            Type = ErrorType.IdentifierNotationClaim,
            Message = message,
            RawContext = line.Trim()
        });
        return; // 한 건만 보고한다 - 같은 원인의 문장이 여러 줄일 수 있다.
    }
}
```

- [ ] **Step 8: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0, 건너뜀 0.

- [ ] **Step 9: 경고 수를 확인한다**

Run: `dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l`
Expected: `8`

- [ ] **Step 10: 커밋**

```bash
git add src/ReSet.Core tests/ReSet.Core.Tests
git commit -m "$(cat <<'EOF'
feat: 식별자 표기의 출처를 프롬프트에 병기하고 거짓 표기 주장을 L1이 잡는다

프롬프트가 파서 정규화 3부 이름만 보여 줘서 명세서가 그것을 원문 표기처럼
서술했다. STAT_PGCOLLECT_INS는 원본에 3부 참조가 0건인데도 크로스 DB
참조라고 단언했다. 원문 표기를 병기하고, 그 근거 위에서만 L1이 주장을
반증한다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

# 2단계 — 재료 3종 (설계 2)

여기부터 **L1 오류를 새로 만든다.** 모든 검사가 프롬프트에 실린 재료 위에만 선다.

## Task 5: `SourceCommentExtractor`

**Files:**
- Create: `src/ReSet.Core/Services/SourceCommentExtractor.cs`
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs`
- Modify: `src/ReSet.Core/Services/AiService.cs` (체크리스트 · `hasComments` 제거)
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/SourceCommentExtractorTests.cs` (신규), `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: Task 4의 `SpecExpectations` init 속성 패턴
- Produces:
  - `public sealed record SourceCommentBlock(string Kind, string Text, int Line, IReadOnlyList<string> Anchors)` — `Kind`는 `"NonExecutable"` · `"CodeLegend"` · `"Header"` 셋 중 하나
  - `public static IReadOnlyList<SourceCommentBlock> SourceCommentExtractor.Extract(string? ddlText)`
  - `SpecExpectations.SourceComments` (`IReadOnlyList<SourceCommentBlock>`)
  - `ErrorType.SourceCommentMissing`

**배경:** `AiService.cs:303`과 `:1542`의 `hasComments`가 **계산만 되고 어디에서도 쓰이지 않는다.** 원본 DDL 전문은 `<sp-source-ddl>`(`:502-506`)로 프롬프트에 들어가므로 주석 결함 9건은 정보 부족이 아니라 **요구 부재**다.

주석을 전부 뽑지 않는다. `OmissionCommentScanner`의 교훈 — *"패턴을 좁게 유지한다. 배너가 잦으면 사람이 읽지 않는다."*

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SourceCommentExtractorTests.cs` (신규 파일):

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SourceCommentExtractorTests
    {
        [Fact]
        public void Extract_NonExecutableCodeComment_ShouldCarryIdentifierAnchors()
        {
            // COMM_UPD 실측 형태. 이 주석이 명세서에 통째로 빠졌다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T SET C = 1
    WHERE  ID > 0
    --AND ClientID NOT IN (SELECT ClientID FROM dbo.UF_GET_CLIENTID4TMONET()) --예외처리 제거(2021.11.29)
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            var block = Assert.Single(blocks, b => b.Kind == "NonExecutable");
            Assert.Contains("UF_GET_CLIENTID4TMONET", block.Anchors);
            Assert.Contains("2021.11.29", block.Anchors);
        }

        [Fact]
        public void Extract_CodeLegendComment_ShouldCarryNumberLabelAnchors()
        {
            // PROC_ETC 실측: 0:일반,1:내부테스트용,... 범례가 명세서에 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    -- ClientIDType 0:일반,1:내부테스트용,2:Cafe24
    SELECT 1
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            var block = Assert.Single(blocks, b => b.Kind == "CodeLegend");
            Assert.Contains("0:일반", block.Anchors);
            Assert.Contains("2:Cafe24", block.Anchors);
        }

        [Fact]
        public void Extract_HeaderComment_ShouldBeClassifiedAsHeader()
        {
            const string ddl = @"-- Return Value : =0->성공, <>0->실패
-- 내부 SP 호출 : NONE
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT 1
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            Assert.Contains(blocks, b => b.Kind == "Header" && b.Text.Contains("NONE"));
        }

        [Fact]
        public void Extract_PlainProseComment_ShouldHaveNoAnchors()
        {
            // 앵커가 없으면 프롬프트에만 싣고 L1은 대조하지 않는다.
            // 억지로 대조하면 오탐만 낳는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    --매입요청일(D)+1 : 집계 고려
    SELECT 1
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            Assert.All(blocks, b => Assert.Empty(b.Anchors));
        }

        [Fact]
        public void Extract_EmptyDdl_ShouldReturnEmpty()
        {
            Assert.Empty(SourceCommentExtractor.Extract(null));
            Assert.Empty(SourceCommentExtractor.Extract("   "));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SourceCommentExtractorTests"`
Expected: FAIL — 클래스가 없어 컴파일 오류.

- [ ] **Step 3: 추출기를 만든다**

`src/ReSet.Core/Services/SourceCommentExtractor.cs` (신규):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <param name="Kind">"NonExecutable" · "CodeLegend" · "Header" 중 하나.</param>
    /// <param name="Text">주석 원문(주석 기호 제외).</param>
    /// <param name="Line">원본 DDL에서의 줄 번호(1부터).</param>
    /// <param name="Anchors">
    /// 명세서 본문에서 그대로 찾을 수 있는 토큰. 비어 있으면 L1이 대조하지
    /// 않는다 - 왜 검사하지 않는지가 이 필드로 코드에 남는다.
    /// </param>
    public sealed record SourceCommentBlock(
        string Kind, string Text, int Line, IReadOnlyList<string> Anchors);

    /// <summary>
    /// 원본 DDL의 주석 중 명세서가 반드시 옮겨야 하는 것만 뽑는다.
    ///
    /// 전부 뽑지 않는 이유는 OmissionCommentScanner가 남긴 교훈과 같다 -
    /// "패턴을 좁게 유지한다. 배너가 잦으면 사람이 읽지 않는다." 큰 SP는 주석이
    /// 수백 줄이고, 전부 실으면 체크리스트가 무의미해진다.
    ///
    /// 이 추출기 하나가 프롬프트 체크리스트와 L1 대조 기준의 단일 권위다.
    /// AiService 안에만 두면 L1이 알 수 없고, 렌더링의 부수효과로 기록하면
    /// 렌더 경로가 둘이라 결과가 달라진다(SchemaPromptColumnSelector와 같은 판단).
    /// </summary>
    public static class SourceCommentExtractor
    {
        private const int MaxBlocks = 40;

        private static readonly Regex LineCommentRegex =
            new(@"--(?<body>.*)$", RegexOptions.Compiled);

        /// <summary>SQL 토큰이 들어 있으면 코드가 주석 처리된 것으로 본다.</summary>
        private static readonly Regex SqlTokenRegex = new(
            @"\b(AND|OR|SELECT|FROM|WHERE|JOIN|INSERT|UPDATE|DELETE|SUM|CASE|WHEN|NOT\s+IN|IN)\b|=",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>0:반올림, 1:자동 같은 코드 범례.</summary>
        private static readonly Regex CodeLegendRegex =
            new(@"\d+\s*:\s*[^\s,;]+", RegexOptions.Compiled);

        /// <summary>식별자 앵커. 밑줄이 있거나 대문자가 섞인 3자 이상 토큰.</summary>
        private static readonly Regex IdentifierAnchorRegex =
            new(@"\b[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+\b|\b[A-Z][a-z]+[A-Z][A-Za-z0-9]*\b",
                RegexOptions.Compiled);

        /// <summary>날짜 앵커. 2021.11.29 / 2021-11-29 / 2021.11.29자 모두.</summary>
        private static readonly Regex DateAnchorRegex =
            new(@"\b\d{4}[.\-]\d{1,2}[.\-]\d{1,2}\b", RegexOptions.Compiled);

        public static IReadOnlyList<SourceCommentBlock> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<SourceCommentBlock>();

            var blocks = new List<SourceCommentBlock>();
            var lines = ddlText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var createSeen = false;

            for (var i = 0; i < lines.Length && blocks.Count < MaxBlocks; i++)
            {
                var line = lines[i];

                if (!createSeen
                    && line.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
                {
                    createSeen = true;
                }

                var match = LineCommentRegex.Match(line);
                if (!match.Success) continue;

                var body = match.Groups["body"].Value.Trim();
                if (body.Length == 0) continue;

                var kind = !createSeen ? "Header"
                    : CodeLegendRegex.IsMatch(body) ? "CodeLegend"
                    : SqlTokenRegex.IsMatch(body) ? "NonExecutable"
                    : "Prose";

                if (kind == "Prose")
                {
                    // 앵커가 없으므로 프롬프트 전용이다. 재료에는 남긴다 -
                    // 체크리스트가 이 주석의 존재를 알려야 한다.
                    blocks.Add(new SourceCommentBlock(kind, body, i + 1, Array.Empty<string>()));
                    continue;
                }

                blocks.Add(new SourceCommentBlock(kind, body, i + 1, BuildAnchors(kind, body)));
            }

            return blocks;
        }

        private static IReadOnlyList<string> BuildAnchors(string kind, string body)
        {
            var anchors = new List<string>();

            if (kind == "CodeLegend")
            {
                foreach (Match m in CodeLegendRegex.Matches(body))
                {
                    var token = Regex.Replace(m.Value, @"\s+", string.Empty);
                    if (!anchors.Contains(token, StringComparer.Ordinal)) anchors.Add(token);
                }

                return anchors;
            }

            foreach (Match m in IdentifierAnchorRegex.Matches(body))
            {
                if (!anchors.Contains(m.Value, StringComparer.OrdinalIgnoreCase)) anchors.Add(m.Value);
            }

            foreach (Match m in DateAnchorRegex.Matches(body))
            {
                if (!anchors.Contains(m.Value, StringComparer.Ordinal)) anchors.Add(m.Value);
            }

            return anchors;
        }
    }
}
```

- [ ] **Step 4: 추출기 테스트 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SourceCommentExtractorTests"`
Expected: PASS (5개)

- [ ] **Step 5: 커밋 (추출기 단독)**

```bash
git add src/ReSet.Core/Services/SourceCommentExtractor.cs tests/ReSet.Core.Tests/SourceCommentExtractorTests.cs
git commit -m "$(cat <<'EOF'
feat: 명세서가 옮겨야 할 원본 주석 세 부류를 뽑는 추출기

비실행 코드 주석·코드 범례·헤더 주석만 뽑고 앵커 토큰을 붙인다.
앵커가 없는 산문 주석은 프롬프트 전용으로 표시해, 왜 L1이 대조하지
않는지가 자료구조에 남게 한다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 6: L1 검사 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`:

```csharp
[Fact]
public void Validate_MissingCommentAnchor_ShouldBeAnError()
{
    var expectations = EmptyExpectations() with
    {
        SourceComments = new[]
        {
            new SourceCommentBlock(
                "NonExecutable",
                "AND ClientID NOT IN (SELECT ClientID FROM dbo.UF_GET_CLIENTID4TMONET())",
                12,
                new[] { "UF_GET_CLIENTID4TMONET" })
        }
    };

    var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

    Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SourceCommentMissing);
}

[Fact]
public void Validate_CommentAnchorPresent_ShouldPass()
{
    var expectations = EmptyExpectations() with
    {
        SourceComments = new[]
        {
            new SourceCommentBlock(
                "NonExecutable", "AND ClientID NOT IN (...)", 12,
                new[] { "UF_GET_CLIENTID4TMONET" })
        }
    };
    var markdown = RequiredHeadersMarkdown()
        + "\n주석 처리된 조건은 `dbo.UF_GET_CLIENTID4TMONET()`를 호출하며 실행되지 않습니다.\n";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SourceCommentMissing);
}

[Fact]
public void Validate_AnchorlessProseComment_ShouldNotBeChecked()
{
    // 앵커가 없는 항목은 L1이 손대지 않는다.
    var expectations = EmptyExpectations() with
    {
        SourceComments = new[]
        {
            new SourceCommentBlock("Prose", "매입요청일(D)+1 : 집계 고려", 7, Array.Empty<string>())
        }
    };

    var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

    Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SourceCommentMissing);
}
```

- [ ] **Step 7: `SpecExpectations`에 재료를 싣고 L1 검사를 구현한다**

`SpecExpectations.cs` record 본문에 추가:

```csharp
/// <summary>명세서가 옮겨야 할 원본 주석. 앵커가 있는 항목만 L1이 대조한다.</summary>
public IReadOnlyList<SourceCommentBlock> SourceComments { get; init; }
    = Array.Empty<SourceCommentBlock>();
```

`From`에서 지역 변수로 뽑고, **조기 반환 조건과 initializer 양쪽에** 넣는다. 조건에 잇는 것을 빠뜨리면 이 검사가 한 번도 돌지 않는다(Task 4의 경고).

```csharp
var sourceComments = SourceCommentExtractor.Extract(spDef.DdlText);
```

조기 반환 조건에 `&& sourceComments.Count == 0`을 잇고, initializer에 `SourceComments = sourceComments,`를 넣는다.

`MechanicalValidator.cs`: `ErrorType`에 `SourceCommentMissing`을 추가하고, `Validate`의 `if (expectations != null)` 블록에 `CheckSourceComments(cleansed, expectations, result);`를 더한 뒤 구현한다.

```csharp
/// <summary>
/// 원본 주석의 앵커 토큰이 명세서 본문에 있는지 본다.
///
/// 앵커가 없는 항목은 건너뛴다 - 순수 산문 주석을 자연어로 대조하면 오탐만
/// 낳는다. 축 B의 조건 컬럼 검사가 실측 15건 중 14건 오탐이었던 전례가 있다.
///
/// 앵커 하나만 있으면 통과로 본다. 한 주석의 모든 토큰을 요구하면 명세서가
/// 요약하는 정상 서술까지 결함이 된다.
/// </summary>
private static void CheckSourceComments(
    string markdown, SpecExpectations expectations, ValidationResult result)
{
    if (expectations.SourceComments.Count == 0) return;

    foreach (var block in expectations.SourceComments)
    {
        if (block.Anchors.Count == 0) continue;

        var found = block.Anchors.Any(
            anchor => markdown.Contains(anchor, StringComparison.OrdinalIgnoreCase));
        if (found) continue;

        var message =
            $"원본 DDL {block.Line}행의 주석이 명세서에 기록되지 않았습니다: "
            + $"`{block.Text}`. 조건식 원문·도입 일자·사유를 제약 절에 기술해야 합니다. "
            + $"(대조 앵커: {string.Join(", ", block.Anchors)})";
        result.Errors.Add(message);
        result.DetailedErrors.Add(new DetailedError
        {
            Type = ErrorType.SourceCommentMissing,
            Message = message,
            RawContext = block.Text
        });
    }
}
```

- [ ] **Step 8: 프롬프트 체크리스트를 붙이고 `hasComments`를 없앤다**

`AiService.cs:303`과 `:1542`의 `bool hasComments = ...` 선언을 **삭제**한다. 대신 `BuildSpecificationPrompts`의 체크리스트 조립부(`checklistSb`, `:444` 근처)에 추가한다.

```csharp
var sourceComments = SourceCommentExtractor.Extract(spDef.DdlText);
if (sourceComments.Count > 0)
{
    checklistSb.AppendLine(
        $"- [ ] 원본 DDL의 주석 {sourceComments.Count}건(비실행 조건·코드 범례·헤더 선언)을 "
        + "본문에 기록하셨습니까? 조건식 원문·도입 일자·사유를 그대로 옮기고, "
        + "\"실행되지 않습니다\" 한 문장으로 대신하지 마십시오. 대조 대상:");
    foreach (var block in sourceComments)
    {
        checklistSb.AppendLine($"      * (라인 {block.Line}) {block.Text}");
    }
}
```

- [ ] **Step 9: 전체 테스트와 경고 수를 확인한다**

Run: `dotnet test`
Expected: 실패 0, 건너뜀 0.

Run: `dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l`
Expected: `8` — `hasComments`를 지웠으므로 미사용 변수 경고가 늘지 않는다.

- [ ] **Step 10: 커밋**

```bash
git add src/ReSet.Core tests/ReSet.Core.Tests
git commit -m "$(cat <<'EOF'
feat: 원본 주석 기록을 프롬프트가 요구하고 L1이 대조한다

hasComments는 계산만 되고 어디에서도 쓰이지 않는 죽은 변수였다. 원본 DDL
전문은 이미 프롬프트에 들어가므로 주석 결함 9건은 정보 부족이 아니라
요구 부재였다. 체크리스트와 L1 대조가 같은 추출기 결과에서 나온다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: `RoundingSemanticsExtractor`

**Files:**
- Create: `src/ReSet.Core/Services/RoundingSemanticsExtractor.cs`
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs`, `AiService.cs`, `MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/RoundingSemanticsExtractorTests.cs` (신규), `MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: Task 5의 `SpecExpectations` init 속성 패턴
- Produces:
  - `public sealed record RoundingCall(int Line, string ThirdArgument)`
  - `public static IReadOnlyList<RoundingCall> RoundingSemanticsExtractor.Extract(string? ddlText)`
  - `SpecExpectations.RoundingCalls` (`IReadOnlyList<RoundingCall>`)
  - `ErrorType.RoundingSemanticsMissing`

**배경:** 원본 주석 `--0:반올림, 0<>절사`가 값 매핑을 명시하는데 Spec은 "반올림 또는 절사"로만 적는다. **3번째 인자의 의미는 이 SP의 사정이 아니라 T-SQL 명세다** — 0이면 반올림, 0이 아니면 절사. 재료가 그 문장을 상수로 들고 있으면 되고 추측이 아니다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/RoundingSemanticsExtractorTests.cs` (신규):

```csharp
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class RoundingSemanticsExtractorTests
    {
        [Fact]
        public void Extract_ThreeArgumentRound_ShouldCaptureTheThirdArgument()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T
    SET    PGComm = ROUND(A.TxAmt * B.Rate / 100, 0, dbo.UF_GET_PGCommOption(A.PGName))
END";

            var calls = RoundingSemanticsExtractor.Extract(ddl);

            var call = Assert.Single(calls);
            Assert.Contains("UF_GET_PGCommOption", call.ThirdArgument);
        }

        [Fact]
        public void Extract_TwoArgumentRound_ShouldBeIgnored()
        {
            // 2인자 ROUND는 항상 반올림이므로 기술할 값 매핑이 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT ROUND(1.5, 0)
END";

            Assert.Empty(RoundingSemanticsExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_EmptyDdl_ShouldReturnEmpty()
        {
            Assert.Empty(RoundingSemanticsExtractor.Extract(null));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~RoundingSemanticsExtractorTests"`
Expected: FAIL — 클래스가 없어 컴파일 오류.

- [ ] **Step 3: 추출기를 만든다**

`src/ReSet.Core/Services/RoundingSemanticsExtractor.cs` (신규):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">원본 DDL에서의 줄 번호(1부터).</param>
    /// <param name="ThirdArgument">ROUND의 세 번째 인자 원문.</param>
    public sealed record RoundingCall(int Line, string ThirdArgument);

    /// <summary>
    /// 3인자 ROUND 호출을 뽑는다.
    ///
    /// 세 번째 인자의 의미는 이 SP의 사정이 아니라 T-SQL 명세다 - 0이면 반올림,
    /// 0이 아니면 절사. 그래서 재료는 그 문장을 상수로 들고 있으면 되고 추측이
    /// 아니다. 원본 주석 --0:반올림, 0&lt;&gt;절사는 그 명세를 재확인해 줄 뿐이다.
    ///
    /// 2인자 호출은 담지 않는다. 항상 반올림이라 기술할 값 매핑이 없다.
    /// </summary>
    public static class RoundingSemanticsExtractor
    {
        /// <summary>프롬프트와 L1이 함께 쓰는 의미 문장. 두 곳이 다르게 말하면 안 된다.</summary>
        public const string SemanticsSentence =
            "ROUND의 세 번째 인자는 0이면 반올림, 0이 아니면 절사입니다.";

        public static IReadOnlyList<RoundingCall> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<RoundingCall>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);

                // 구문 오류가 있어도 파싱된 조각으로 계속한다 - 파서 본체가
                // 소프트 페일하는 것과 같은 판단이다.
                if (fragment == null) return Array.Empty<RoundingCall>();

                var visitor = new RoundCallVisitor();
                fragment.Accept(visitor);
                return visitor.Calls;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[RoundingSemanticsExtractor] ROUND 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<RoundingCall>();
            }
        }

        private sealed class RoundCallVisitor : TSqlFragmentVisitor
        {
            public List<RoundingCall> Calls { get; } = new();

            public override void Visit(FunctionCall node)
            {
                if (!string.Equals(node.FunctionName?.Value, "ROUND", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (node.Parameters == null || node.Parameters.Count < 3) return;

                var third = node.Parameters[2];
                var text = string.Concat(
                    third.ScriptTokenStream
                        .Skip(third.FirstTokenIndex)
                        .Take(third.LastTokenIndex - third.FirstTokenIndex + 1)
                        .Select(t => t.Text));

                Calls.Add(new RoundingCall(node.StartLine, text.Trim()));
            }
        }
    }
}
```

- [ ] **Step 4: 추출기 테스트 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~RoundingSemanticsExtractorTests"`
Expected: PASS (3개)

- [ ] **Step 5: L1 검사 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`:

```csharp
[Fact]
public void Validate_RoundWithoutTruncationSemantics_ShouldBeAnError()
{
    var expectations = EmptyExpectations() with
    {
        RoundingCalls = new[] { new RoundingCall(63, "dbo.UF_GET_PGCommOption(A.PGName)") }
    };
    var markdown = RequiredHeadersMarkdown()
        + "\nPG 수수료 반올림 옵션으로 정수화합니다.\n";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.RoundingSemanticsMissing);
}

[Theory]
[InlineData("절사")]
[InlineData("버림")]
[InlineData("내림")]
[InlineData("truncate")]
public void Validate_RoundWithTruncationSemantics_ShouldPass(string synonym)
{
    // INS_EXTRA4PLCARD의 Spec이 이 매핑을 정확히 기록한 반례다(골든 케이스).
    var expectations = EmptyExpectations() with
    {
        RoundingCalls = new[] { new RoundingCall(63, "dbo.UF_GET_PGCommOption(A.PGName)") }
    };
    var markdown = RequiredHeadersMarkdown()
        + $"\n세 번째 인자가 0이면 반올림, 0이 아니면 {synonym}합니다.\n";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.RoundingSemanticsMissing);
}
```

- [ ] **Step 6: `SpecExpectations`와 L1 검사를 구현한다**

`SpecExpectations.cs` record 본문:

```csharp
/// <summary>원본의 3인자 ROUND 호출. 값 매핑 기술 여부를 L1이 본다.</summary>
public IReadOnlyList<RoundingCall> RoundingCalls { get; init; } = Array.Empty<RoundingCall>();
```

`From`에서 `var roundingCalls = RoundingSemanticsExtractor.Extract(spDef.DdlText);`로 뽑고, 조기 반환 조건에 `&& roundingCalls.Count == 0`을 이은 뒤 initializer에 `RoundingCalls = roundingCalls,`를 넣는다.

`MechanicalValidator.cs`: `ErrorType`에 `RoundingSemanticsMissing`을 추가하고 `Validate`에 `CheckRoundingSemantics(cleansed, expectations, result);`를 더한다.

```csharp
/// <summary>
/// 3인자 ROUND가 있는데 명세서가 절사 쪽 의미를 적지 않았는지 본다.
///
/// 동의어 집합으로 판정한다. 명세서가 "내림"이라 써도 값 매핑은 전달된
/// 것이므로, 한 단어만 요구하면 정상 서술이 결함이 된다.
///
/// 호출별로 보지 않고 문서 전체에 한 번만 요구한다. 같은 의미를 호출
/// 개수만큼 반복하라는 요구가 되면 명세서가 장황해진다.
/// </summary>
private static readonly string[] TruncationSynonyms = { "절사", "버림", "내림", "truncate", "TRUNCATE" };

private static void CheckRoundingSemantics(
    string markdown, SpecExpectations expectations, ValidationResult result)
{
    if (expectations.RoundingCalls.Count == 0) return;

    var stated = Array.Exists(
        TruncationSynonyms, s => markdown.Contains(s, StringComparison.OrdinalIgnoreCase));
    if (stated) return;

    var lines = string.Join(", ", expectations.RoundingCalls.Select(c => $"라인 {c.Line}"));
    var message =
        $"원본에 3인자 ROUND 호출이 {expectations.RoundingCalls.Count}건 있으나({lines}) "
        + $"명세서가 절사 쪽 의미를 기술하지 않았습니다. {RoundingSemanticsExtractor.SemanticsSentence}";
    result.Errors.Add(message);
    result.DetailedErrors.Add(new DetailedError
    {
        Type = ErrorType.RoundingSemanticsMissing,
        Message = message,
        RawContext = expectations.RoundingCalls[0].ThirdArgument
    });
}
```

- [ ] **Step 7: 프롬프트 체크리스트를 붙인다**

`AiService.cs`의 `checklistSb` 조립부, Task 5가 넣은 주석 항목 다음:

```csharp
var roundingCalls = RoundingSemanticsExtractor.Extract(spDef.DdlText);
if (roundingCalls.Count > 0)
{
    checklistSb.AppendLine(
        $"- [ ] 원본의 3인자 ROUND 호출 {roundingCalls.Count}건에 대해 "
        + $"{RoundingSemanticsExtractor.SemanticsSentence} "
        + "이 값 매핑을 명세서에 기술하셨습니까? \"반올림 또는 절사\"처럼 "
        + "어느 값이 어느 동작인지 흐리게 적지 마십시오.");
}
```

- [ ] **Step 8: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0, 건너뜀 0.

- [ ] **Step 9: 커밋**

```bash
git add src/ReSet.Core tests/ReSet.Core.Tests
git commit -m "$(cat <<'EOF'
feat: ROUND 3인자의 값 의미를 프롬프트가 요구하고 L1이 대조한다

세 번째 인자의 의미는 T-SQL 명세이지 이 SP의 사정이 아니므로 재료가
상수 문장으로 들고 있으면 된다. 명세서가 "반올림 또는 절사"로만 적으면
금액 계산의 방향이 소실된다. 절사 동의어 집합으로 판정해 정상 서술을
결함으로 잡지 않는다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: `SessionOptionsExtractor`

**Files:**
- Create: `src/ReSet.Core/Services/SessionOptionsExtractor.cs`
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs`, `AiService.cs`, `MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/SessionOptionsExtractorTests.cs` (신규), `MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: Task 6의 패턴
- Produces:
  - `public static IReadOnlyList<string> SessionOptionsExtractor.Extract(string? ddlText)` — 옵션 이름 목록(예: `"NOCOUNT"`, `"XACT_ABORT"`)
  - `SpecExpectations.SessionOptions` (`IReadOnlyList<string>`)
  - `ErrorType.SessionOptionMissing`

**배경:** `UP_Util_Settle_Summary`의 🟡 — `SET NOCOUNT ON`이 `AS` 직후 `BEGIN TRAN` 앞에 있는데 Spec 전체에 언급이 없다. `SET ANSI_NULLS ON`은 CREATE 배치 앞머리의 관례적 노이즈이므로 **`AS` 이후 본문의 것만** 뽑는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SessionOptionsExtractorTests.cs` (신규):

```csharp
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SessionOptionsExtractorTests
    {
        [Fact]
        public void Extract_BodyOption_ShouldBeCaptured()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SET NOCOUNT ON
    BEGIN TRAN
    COMMIT TRAN
END";

            Assert.Contains("NOCOUNT", SessionOptionsExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_BatchPreambleOption_ShouldBeIgnored()
        {
            // CREATE 앞의 SET ANSI_NULLS ON은 배치 관례이지 이 SP의 로직이 아니다.
            const string ddl = @"SET ANSI_NULLS ON
GO
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT 1
END";

            Assert.Empty(SessionOptionsExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_EmptyDdl_ShouldReturnEmpty()
        {
            Assert.Empty(SessionOptionsExtractor.Extract(null));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SessionOptionsExtractorTests"`
Expected: FAIL — 클래스가 없어 컴파일 오류.

- [ ] **Step 3: 추출기를 만든다**

`src/ReSet.Core/Services/SessionOptionsExtractor.cs` (신규):

```csharp
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 프로시저 본문의 세션 옵션을 뽑는다.
    ///
    /// AS 이후의 것만 담는다. CREATE 배치 앞머리의 SET ANSI_NULLS ON 같은 것은
    /// 관례적 노이즈이지 이 SP의 로직이 아니다 - 담으면 모든 명세서가 같은
    /// 결함을 하나씩 갖게 되고, 그러면 이 검사를 아무도 믿지 않는다.
    ///
    /// Util_Settle_Summary의 SET NOCOUNT ON이 AS 직후 BEGIN TRAN 앞에 있는데
    /// 명세서 전체에 언급이 없었던 것이 이 재료가 있는 이유다.
    /// </summary>
    public static class SessionOptionsExtractor
    {
        private static readonly Regex CreateBodyStartRegex = new(
            @"\bCREATE\s+(?:OR\s+ALTER\s+)?PROC(?:EDURE)?\b.*?\bAS\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex SetOptionRegex = new(
            @"^\s*SET\s+(?<option>NOCOUNT|XACT_ABORT|ARITHABORT|ANSI_WARNINGS|ANSI_NULLS|"
            + @"QUOTED_IDENTIFIER|CONCAT_NULL_YIELDS_NULL|TRANSACTION\s+ISOLATION\s+LEVEL)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

        public static IReadOnlyList<string> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<string>();

            var bodyStart = CreateBodyStartRegex.Match(ddlText);
            if (!bodyStart.Success) return Array.Empty<string>();

            var body = ddlText[(bodyStart.Index + bodyStart.Length)..];

            var options = new List<string>();
            foreach (Match match in SetOptionRegex.Matches(body))
            {
                var option = Regex.Replace(match.Groups["option"].Value, @"\s+", " ").ToUpperInvariant();
                if (!options.Contains(option, StringComparer.Ordinal)) options.Add(option);
            }

            return options;
        }
    }
}
```

- [ ] **Step 4: 추출기 테스트 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SessionOptionsExtractorTests"`
Expected: PASS (3개)

- [ ] **Step 5: L1 검사 테스트를 쓴다**

```csharp
[Fact]
public void Validate_MissingSessionOption_ShouldBeAnError()
{
    var expectations = EmptyExpectations() with { SessionOptions = new[] { "NOCOUNT" } };

    var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

    Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SessionOptionMissing);
}

[Fact]
public void Validate_StatedSessionOption_ShouldPass()
{
    var expectations = EmptyExpectations() with { SessionOptions = new[] { "NOCOUNT" } };
    var markdown = RequiredHeadersMarkdown()
        + "\n`SET NOCOUNT ON`으로 행 수 메시지를 억제합니다.\n";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SessionOptionMissing);
}
```

- [ ] **Step 6: `SpecExpectations`와 L1 검사를 구현한다**

`SpecExpectations.cs`:

```csharp
/// <summary>프로시저 본문의 세션 옵션 이름. 배치 앞머리의 것은 담지 않는다.</summary>
public IReadOnlyList<string> SessionOptions { get; init; } = Array.Empty<string>();
```

`From`에서 `var sessionOptions = SessionOptionsExtractor.Extract(spDef.DdlText);`로 뽑고, 조기 반환 조건에 `&& sessionOptions.Count == 0`을 이은 뒤 initializer에 `SessionOptions = sessionOptions,`를 넣는다.

`MechanicalValidator.cs`: `ErrorType`에 `SessionOptionMissing`을 추가하고 `Validate`에 `CheckSessionOptions(cleansed, expectations, result);`를 더한다.

```csharp
/// <summary>
/// 본문 세션 옵션이 명세서에 언급되는지 본다. 옵션 이름 자체가 앵커라
/// 대조가 자명하다 - 이 재료에는 판정 불가 항목이 없다.
/// </summary>
private static void CheckSessionOptions(
    string markdown, SpecExpectations expectations, ValidationResult result)
{
    foreach (var option in expectations.SessionOptions)
    {
        if (markdown.Contains(option, StringComparison.OrdinalIgnoreCase)) continue;

        var message =
            $"프로시저 본문이 `SET {option}`을 설정하는데 명세서가 이를 기술하지 않았습니다. "
            + "세션 옵션은 호출 계층의 동작을 바꿀 수 있으므로 기록해야 합니다.";
        result.Errors.Add(message);
        result.DetailedErrors.Add(new DetailedError
        {
            Type = ErrorType.SessionOptionMissing,
            Message = message,
            RawContext = $"SET {option}"
        });
    }
}
```

- [ ] **Step 7: 프롬프트 체크리스트를 붙인다**

`AiService.cs`의 `checklistSb`, Task 6 항목 다음:

```csharp
var sessionOptions = SessionOptionsExtractor.Extract(spDef.DdlText);
if (sessionOptions.Count > 0)
{
    checklistSb.AppendLine(
        $"- [ ] 프로시저 본문이 설정하는 세션 옵션({string.Join(", ", sessionOptions)})과 "
        + "그것이 호출 계층에 미치는 영향을 기술하셨습니까?");
}
```

- [ ] **Step 8: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0, 건너뜀 0.

- [ ] **Step 9: 커밋**

```bash
git add src/ReSet.Core tests/ReSet.Core.Tests
git commit -m "$(cat <<'EOF'
feat: 프로시저 본문의 세션 옵션을 프롬프트가 요구하고 L1이 대조한다

Util_Settle_Summary의 SET NOCOUNT ON이 명세서 전체에 언급되지 않아
DONE_IN_PROC 동작 변화가 기록되지 않았다. CREATE 배치 앞머리의 관례적
SET은 담지 않는다 - 담으면 모든 명세서가 같은 결함을 하나씩 갖게 되고
그러면 이 검사를 아무도 믿지 않는다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: 헤더 주석과 구현의 모순 (A5)

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Modify: `src/ReSet.Core/Services/AiService.cs` (체크리스트)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: Task 5의 `SpecExpectations.SourceComments`(`Kind == "Header"`)
- Produces:
  - `SpecExpectations.HasInternalProcedureCall` (`bool`)
  - `ErrorType.HeaderContractContradiction`

**배경:** `UP_Util_Settle_Summary`의 헤더 주석이 내부 SP 호출을 `NONE`이라 선언하는데 실제로 `EXEC`가 둘 있다. Spec은 두 `EXEC`를 정확히 기술하면서 **헤더가 모순된다는 사실 자체**는 적지 않았다.

**좁게 한 패턴만 본다.** 헤더 주석에 `NONE`이 있고 정적 분석에 내부 SP 호출이 있으면 오류. 넓히지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
[Fact]
public void Validate_HeaderClaimsNoInternalCallButExecExists_ShouldBeAnError()
{
    var expectations = EmptyExpectations() with
    {
        HasInternalProcedureCall = true,
        SourceComments = new[]
        {
            new SourceCommentBlock("Header", "내부 SP 호출 : NONE", 3, Array.Empty<string>())
        }
    };
    var markdown = RequiredHeadersMarkdown()
        + "\n이 프로시저는 두 개의 하위 프로시저를 EXEC로 호출합니다.\n";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
}

[Fact]
public void Validate_HeaderContradictionAcknowledged_ShouldPass()
{
    var expectations = EmptyExpectations() with
    {
        HasInternalProcedureCall = true,
        SourceComments = new[]
        {
            new SourceCommentBlock("Header", "내부 SP 호출 : NONE", 3, Array.Empty<string>())
        }
    };
    var markdown = RequiredHeadersMarkdown()
        + "\n헤더 주석은 내부 SP 호출이 NONE이라 선언하나 실제로는 EXEC가 둘 있어 "
        + "주석이 구현과 모순됩니다(스테일 주석).\n";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~HeaderC"`
Expected: FAIL — `HasInternalProcedureCall`이 없어 컴파일 오류.

- [ ] **Step 3: `SpecExpectations`에 플래그를 싣는다**

`SpecExpectations.cs`:

```csharp
/// <summary>정적 분석이 내부 SP 호출(EXEC)을 발견했는가. 헤더 주석 모순 판정에 쓴다.</summary>
public bool HasInternalProcedureCall { get; init; }
```

`From`의 initializer — `ControlFlowSummary`가 `EXEC`를 담으므로 그것을 근거로 삼는다.

```csharp
HasInternalProcedureCall = analysis?.ControlFlowSummary
    .Any(s => s.Contains("EXEC", StringComparison.OrdinalIgnoreCase)) == true,
```

- [ ] **Step 4: L1 검사를 구현한다**

`MechanicalValidator.cs`: `ErrorType`에 `HeaderContractContradiction`을 추가하고 `Validate`에 `CheckHeaderContractContradiction(cleansed, expectations, result);`를 더한다.

```csharp
private static readonly string[] ContradictionAcknowledgementTokens =
{
    "모순", "스테일", "일치하지 않", "다릅니다", "어긋"
};

/// <summary>
/// 헤더 주석이 내부 SP 호출을 NONE이라 선언했는데 실제로 EXEC가 있고,
/// 명세서가 그 모순 자체를 적지 않았는지 본다.
///
/// 이 한 패턴만 본다. 헤더 주석이 선언할 수 있는 계약은 여러 가지이고
/// 대부분은 기계가 구현과 대조할 수 없다 - 넓히면 오탐이 된다.
/// </summary>
private static void CheckHeaderContractContradiction(
    string markdown, SpecExpectations expectations, ValidationResult result)
{
    if (!expectations.HasInternalProcedureCall) return;

    var headerClaimsNone = expectations.SourceComments.Any(
        b => b.Kind == "Header"
             && b.Text.Contains("NONE", StringComparison.OrdinalIgnoreCase));
    if (!headerClaimsNone) return;

    var acknowledged = Array.Exists(
        ContradictionAcknowledgementTokens,
        t => markdown.Contains(t, StringComparison.Ordinal));
    if (acknowledged) return;

    const string message =
        "헤더 주석이 내부 SP 호출을 NONE으로 선언했으나 실제로는 EXEC 호출이 있습니다. "
        + "명세서가 이 모순(스테일 주석) 자체를 기록하지 않았습니다.";
    result.Errors.Add(message);
    result.DetailedErrors.Add(new DetailedError
    {
        Type = ErrorType.HeaderContractContradiction,
        Message = message
    });
}
```

- [ ] **Step 5: 프롬프트 체크리스트를 붙인다**

`AiService.cs`의 `checklistSb`, Task 7 항목 다음:

```csharp
if (sourceComments.Any(b => b.Kind == "Header"))
{
    checklistSb.AppendLine(
        "- [ ] 헤더 주석이 선언한 계약(반환값 규약, 내부 SP 호출 유무 등)이 "
        + "실제 구현과 어긋나는 부분이 있다면, 그 모순 자체를 명세서에 "
        + "기록하셨습니까? 구현만 옳게 적고 주석이 낡았다는 사실을 빠뜨리면 "
        + "다음 사람이 같은 조사에 다시 들어갑니다.");
}
```

- [ ] **Step 6: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0, 건너뜀 0.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core tests/ReSet.Core.Tests
git commit -m "$(cat <<'EOF'
feat: 헤더 주석이 NONE이라 선언한 내부 SP 호출의 모순을 L1이 잡는다

Util_Settle_Summary는 헤더가 내부 SP 호출 NONE이라 선언하는데 EXEC가 둘
있다. 명세서는 두 EXEC를 정확히 적으면서 헤더가 모순된다는 사실은 빠뜨렸다.
기계가 대조할 수 있는 이 한 패턴만 본다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

# 3단계 — 기계 확정 표 (설계 3)

**서술을 판정하지 않는다.** 프롬프트가 표를 미리 채워 주고, L1은 행의 존재와 확정 값의 보존만 본다. `CheckUpdateMappings`와 같은 형태다.

## Task 9: `DmlScopeExtractor`와 DML 범위 표 프롬프트

**Files:**
- Create: `src/ReSet.Core/Services/DmlScopeExtractor.cs`
- Modify: `src/ReSet.Core/Services/AiService.cs`
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs` (신규), `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`

**Interfaces:**
- Consumes: Task 2의 `AstUpdateMapping.SourceLine`
- Produces:
  - `public sealed record DmlScopeFact(string Operation, int Line, string Target, IReadOnlyList<string> PredicateColumns, bool DateParameterApplied, IReadOnlyList<string> JoinKeys)` — `Operation`은 `"UPDATE"` 또는 `"DELETE"`
  - `public static IReadOnlyList<DmlScopeFact> DmlScopeExtractor.Extract(string? ddlText, string dateParameterName)`
  - `public const string DmlScopeTableHeading = "### DML 범위 (기계 확정 — 수정 금지)"`

**배경:** A1 4건 + 🔴 1건의 공통 구조는 *Spec이 "범위가 이러이러하다"고 단언하는데 원본에는 그 필터가 없다*는 것이다. **부재를 서술했는지는 자연어 판정이라 앵커가 없다.** 그래서 표를 강제한다.

`dateParameterName`은 `SpStaticAnalysisResult.ProcedureParameters` 중 이름에 `YMD`가 들어간 첫 번째를 호출부가 고른다. 없으면 빈 문자열이고 `DateParameterApplied`는 전부 `false`가 되며 그 칸은 렌더하지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs` (신규):

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class DmlScopeExtractorTests
    {
        [Fact]
        public void Extract_DateParameterOnlyInSubquery_ShouldReportNotApplied()
        {
            // EXCEPTION_PROC 실행순서 18 실측: 바깥 UPDATE에 YMD 필터가 없고
            // 서브쿼리만 정산일로 제한되는데 Spec은 "YMD = @pi_strYMD를 기본
            // 범위로"라 일괄 기술했다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A
    SET    A.OutState = 9
    FROM   dbo.TSettleMst A
    WHERE  A.UseState = 0
    AND    EXISTS (SELECT 1 FROM dbo.TSettleMst B WHERE B.YMD = @pi_strYMD AND B.PLTID = A.PLTID)
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var fact = Assert.Single(facts);
            Assert.Equal("UPDATE", fact.Operation);
            Assert.False(fact.DateParameterApplied);
            Assert.Contains("UseState", fact.PredicateColumns);
        }

        [Fact]
        public void Extract_DateParameterOnTheTarget_ShouldReportApplied()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A
    SET    A.OutState = 2
    FROM   dbo.TSettleMst A
    WHERE  A.YMD = @pi_strYMD
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            Assert.True(Assert.Single(facts).DateParameterApplied);
        }

        [Fact]
        public void Extract_JoinKeys_ShouldBeCaptured()
        {
            // EXCEPTION_PROC 실행순서 4 실측: 조인 키에 MallID가 없는데
            // Spec은 조인 키를 아예 기술하지 않았다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A
    SET    A.CLComm = B.CLComm
    FROM   dbo.TSettleMst  A
    JOIN   dbo.TClientRate B ON A.YMD = B.YMD AND A.ClientID = B.ClientID AND A.PGName = B.PGName
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var joinKeys = Assert.Single(facts).JoinKeys;
            Assert.Contains("ClientID", joinKeys);
            Assert.DoesNotContain("MallID", joinKeys);
        }

        [Fact]
        public void Extract_Delete_ShouldBeIncluded()
        {
            // INS_EXTRA 실측: DELETE에 OutState/OutYMD 조건이 전혀 없는데
            // Spec은 "지급 완료·확정 행은 삭제 대상에 포함되지 않습니다"라 단언했다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    DELETE FROM dbo.TSettleMst WHERE YMD = @pi_strYMD AND ClientID = 'X'
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var fact = Assert.Single(facts);
            Assert.Equal("DELETE", fact.Operation);
            Assert.Contains("YMD", fact.PredicateColumns);
            Assert.DoesNotContain("OutState", fact.PredicateColumns);
        }

        [Fact]
        public void Extract_EmptyDdl_ShouldReturnEmpty()
        {
            Assert.Empty(DmlScopeExtractor.Extract(null, "@pi_strYMD"));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests"`
Expected: FAIL — 클래스가 없어 컴파일 오류.

- [ ] **Step 3: 추출기를 만든다**

`src/ReSet.Core/Services/DmlScopeExtractor.cs` (신규):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Operation">"UPDATE" 또는 "DELETE".</param>
    /// <param name="Line">원본 DDL에서의 줄 번호(1부터).</param>
    /// <param name="Target">갱신·삭제 대상의 원문 표기.</param>
    /// <param name="PredicateColumns">WHERE 최상위가 거르는 컬럼 이름.</param>
    /// <param name="DateParameterApplied">
    /// 기준일 파라미터가 <b>대상 범위에</b> 적용되는가. 서브쿼리 안에만 있으면 false다.
    /// 이 칸 하나가 A1 결함 넷 중 셋을 드러낸다.
    /// </param>
    /// <param name="JoinKeys">FROM 절 조인의 ON 조건이 쓰는 컬럼 이름.</param>
    public sealed record DmlScopeFact(
        string Operation,
        int Line,
        string Target,
        IReadOnlyList<string> PredicateColumns,
        bool DateParameterApplied,
        IReadOnlyList<string> JoinKeys);

    /// <summary>
    /// DML 문장별로 "무엇이 대상 범위를 정하는가"를 뽑는다.
    ///
    /// 명세서가 부재를 서술했는지는 자연어 판정이라 앵커가 없다. 그래서 이 재료는
    /// 서술을 요구하지 않고 <b>표</b>를 강제하는 데 쓴다 - 프롬프트가 표를 채워
    /// 주고 L1은 행의 존재와 확정 값의 보존만 본다. CheckUpdateMappings와 같은 형태다.
    ///
    /// 값과 연산자는 담지 않는다. 축 B가 이미 결론 낸 지점이다 - 값까지 대조하면
    /// 노이즈다(SpecConditionColumnExtractor 주석). 조인 키의 유일성도 판정하지
    /// 않는다 - 프롬프트 규칙이 이미 "추측하지 마라"고 못박았다.
    /// </summary>
    public static class DmlScopeExtractor
    {
        public const string DmlScopeTableHeading = "### DML 범위 (기계 확정 — 수정 금지)";

        public static IReadOnlyList<DmlScopeFact> Extract(string? ddlText, string dateParameterName)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<DmlScopeFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null) return Array.Empty<DmlScopeFact>();

                var visitor = new DmlScopeVisitor(dateParameterName ?? string.Empty);
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DmlScopeExtractor] DML 범위 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<DmlScopeFact>();
            }
        }

        private sealed class DmlScopeVisitor : TSqlFragmentVisitor
        {
            private readonly string _dateParameter;

            public DmlScopeVisitor(string dateParameter) => _dateParameter = dateParameter;

            public List<DmlScopeFact> Facts { get; } = new();

            public override void Visit(UpdateSpecification node) =>
                Record("UPDATE", node, node.Target, node.WhereClause, node.FromClause);

            public override void Visit(DeleteSpecification node) =>
                Record("DELETE", node, node.Target, node.WhereClause, node.FromClause);

            private void Record(
                string operation,
                TSqlFragment statement,
                TableReference? target,
                WhereClause? where,
                FromClause? from)
            {
                var predicateColumns = new List<string>();
                var dateApplied = false;

                if (where?.SearchCondition != null)
                {
                    // 최상위 술어만 본다. 서브쿼리 안의 조건은 대상 범위를
                    // 정하지 않는다 - 그 구분이 이 추출기의 존재 이유다.
                    var top = new TopLevelPredicateCollector();
                    where.SearchCondition.Accept(top);
                    predicateColumns.AddRange(top.Columns);
                    dateApplied = _dateParameter.Length > 0
                        && top.Parameters.Contains(_dateParameter, StringComparer.OrdinalIgnoreCase);
                }

                var joinKeys = new List<string>();
                if (from != null)
                {
                    var joins = new JoinConditionCollector();
                    from.Accept(joins);
                    joinKeys.AddRange(joins.Columns);
                }

                Facts.Add(new DmlScopeFact(
                    operation,
                    statement.StartLine,
                    TextOf(target),
                    predicateColumns,
                    dateApplied,
                    joinKeys));
            }

            private static string TextOf(TSqlFragment? fragment)
            {
                if (fragment == null) return string.Empty;

                return string.Concat(
                    fragment.ScriptTokenStream
                        .Skip(fragment.FirstTokenIndex)
                        .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                        .Select(t => t.Text)).Trim();
            }
        }

        /// <summary>
        /// WHERE 최상위 술어의 컬럼과 파라미터. 서브쿼리 안으로 내려가지 않는다 -
        /// EXISTS(... B.YMD = @pi_strYMD ...)는 대상 범위를 좁히지 않기 때문이다.
        /// </summary>
        private sealed class TopLevelPredicateCollector : TSqlFragmentVisitor
        {
            public List<string> Columns { get; } = new();
            public List<string> Parameters { get; } = new();

            public override void ExplicitVisit(ScalarSubquery node) { }
            public override void ExplicitVisit(ExistsPredicate node) { }
            public override void ExplicitVisit(InPredicate node) { }

            public override void Visit(ColumnReferenceExpression node)
            {
                var name = node.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value;
                if (!string.IsNullOrWhiteSpace(name)
                    && !Columns.Contains(name!, StringComparer.OrdinalIgnoreCase))
                {
                    Columns.Add(name!);
                }
            }

            public override void Visit(VariableReference node)
            {
                if (!Parameters.Contains(node.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Parameters.Add(node.Name);
                }
            }
        }

        /// <summary>조인 ON 조건이 쓰는 컬럼.</summary>
        private sealed class JoinConditionCollector : TSqlFragmentVisitor
        {
            public List<string> Columns { get; } = new();

            public override void Visit(QualifiedJoin node)
            {
                if (node.SearchCondition == null) return;

                var collector = new TopLevelPredicateCollector();
                node.SearchCondition.Accept(collector);

                foreach (var column in collector.Columns)
                {
                    if (!Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                    {
                        Columns.Add(column);
                    }
                }
            }
        }
    }
}
```

- [ ] **Step 4: 추출기 테스트 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests"`
Expected: PASS (5개)

- [ ] **Step 5: 프롬프트에 표를 렌더한다**

`AiService.cs`의 규칙 목록, UPDATE fill-in 템플릿(`:366-367`) 다음에 추가한다.

```csharp
var dateParameter = spDef.StaticAnalysis?.ProcedureParameters
    .FirstOrDefault(p => p.Contains("YMD", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
var dmlScopeFacts = DmlScopeExtractor.Extract(spDef.DdlText, dateParameter);

if (dmlScopeFacts.Count > 0)
{
    rules.Add($"{ruleIndex++}. [CRITICAL SCOPE TABLE] The following table is MACHINE-DERIVED from the source DDL. Copy it verbatim into `## CRUD 분석` under the exact heading shown, and make sure no sentence in your document contradicts it. Do NOT change any cell. In particular: when a row says the date parameter is NOT applied to the target, you must NOT write that the statement is limited to the settlement date.");
    rules.Add($"   {DmlScopeExtractor.DmlScopeTableHeading}");
    rules.Add("   | 문장 | 라인 | 대상 | 대상에 적용된 WHERE 술어 컬럼 | 기준일 파라미터 적용 | 조인 키 |");
    rules.Add("   | :--- | :--- | :--- | :--- | :--- | :--- |");

    for (var i = 0; i < dmlScopeFacts.Count; i++)
    {
        var fact = dmlScopeFacts[i];
        var predicates = fact.PredicateColumns.Count == 0
            ? "(없음)" : string.Join(", ", fact.PredicateColumns);
        var joinKeys = fact.JoinKeys.Count == 0 ? "(없음)" : string.Join(", ", fact.JoinKeys);
        var applied = dateParameter.Length == 0
            ? "(기준일 파라미터 없음)"
            : fact.DateParameterApplied ? "예" : "**아니오**";

        rules.Add(
            $"   | {fact.Operation} {i + 1} | {fact.Line} | {EscapeTableCell(fact.Target)} | "
            + $"{predicates} | {applied} | {joinKeys} |");
    }

    rules.Add("");
}
```

- [ ] **Step 6: 프롬프트 렌더 테스트를 쓴다**

`tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`:

```csharp
[Fact]
public async Task GenerateSpecificationAsync_WithDmlScopeFacts_ShouldPrefillTheScopeTable()
{
    // 부재를 서술했는지는 자연어 판정이라 앵커가 없다. 표를 미리 채워 주고
    // L1이 행의 보존만 보는 것이 이 설계의 핵심이다.
    var (service, handler) = CreateProbe();
    var spDef = ProbeSpDef();
    spDef.DdlText = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A
    SET    A.OutState = 9
    FROM   dbo.TSettleMst A
    WHERE  A.UseState = 0
    AND    EXISTS (SELECT 1 FROM dbo.TSettleMst B WHERE B.YMD = @pi_strYMD)
END";
    spDef.StaticAnalysis.ProcedureParameters.Add("@pi_strYMD");

    await service.GenerateSpecificationAsync(spDef, "지침", null);

    var body = DecodeMessageContents(handler.LastRequestBody);
    Assert.Contains("### DML 범위 (기계 확정 — 수정 금지)", body);
    Assert.Contains("**아니오**", body);
}
```

- [ ] **Step 7: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0, 건너뜀 0.

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core tests/ReSet.Core.Tests
git commit -m "$(cat <<'EOF'
feat: DML 문장별 적용 범위를 기계가 확정해 프롬프트 표로 준다

Spec이 "YMD = @pi_strYMD를 기본 범위로"라 단언한 문장들이 실제로는 바깥
UPDATE에 그 필터를 갖고 있지 않았다. 부재를 서술했는지는 자연어 판정이라
앵커가 없으므로, 서술을 요구하지 않고 표를 강제한다. 기준일 파라미터가
서브쿼리 안에만 있는지를 AST가 결정적으로 판정한다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: DML 범위 표 L1 대조

**Files:**
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs`
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: Task 9의 `DmlScopeFact`, `DmlScopeExtractor.DmlScopeTableHeading`
- Produces: `SpecExpectations.DmlScopeFacts` (`IReadOnlyList<DmlScopeFact>`), `ErrorType.DmlScopeTableMissing`

**배경:** 표를 프롬프트로 주는 것만으로는 모델이 옮겼는지 알 수 없다. **행의 존재와 확정 값의 보존**을 본다. 자연어는 읽지 않으므로 축 B의 15/14 오탐 사고가 재현되지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
[Fact]
public void Validate_MissingDmlScopeTable_ShouldBeAnError()
{
    var expectations = EmptyExpectations() with
    {
        DmlScopeFacts = new[]
        {
            new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false, new[] { "PLTID" })
        }
    };

    var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

    Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.DmlScopeTableMissing);
}

[Fact]
public void Validate_DmlScopeRowMissingTheLine_ShouldBeAnError()
{
    // 헤딩만 옮기고 행을 빠뜨리는 것을 잡는다.
    var expectations = EmptyExpectations() with
    {
        DmlScopeFacts = new[]
        {
            new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false, new[] { "PLTID" }),
            new DmlScopeFact("UPDATE", 331, "A", new[] { "YMD" }, true, Array.Empty<string>())
        }
    };
    var markdown = RequiredHeadersMarkdown()
        + "\n### DML 범위 (기계 확정 — 수정 금지)\n"
        + "| 문장 | 라인 | 대상 |\n| :--- | :--- | :--- |\n| UPDATE 1 | 227 | A |\n";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.Contains(
        result.DetailedErrors,
        e => e.Type == ErrorType.DmlScopeTableMissing && e.Message.Contains("331"));
}

[Fact]
public void Validate_DmlScopeTableFullyCopied_ShouldPass()
{
    var expectations = EmptyExpectations() with
    {
        DmlScopeFacts = new[]
        {
            new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false, new[] { "PLTID" })
        }
    };
    var markdown = RequiredHeadersMarkdown()
        + "\n### DML 범위 (기계 확정 — 수정 금지)\n"
        + "| 문장 | 라인 | 대상 | 술어 | 기준일 | 조인 키 |\n"
        + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
        + "| UPDATE 1 | 227 | A | UseState | **아니오** | PLTID |\n";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.DmlScopeTableMissing);
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScope"`
Expected: FAIL — `SpecExpectations.DmlScopeFacts`가 없어 컴파일 오류.

- [ ] **Step 3: `SpecExpectations`에 재료를 싣는다**

`SpecExpectations.cs`:

```csharp
/// <summary>DML 문장별 적용 범위. 명세서가 이 표를 그대로 옮겼는지 L1이 본다.</summary>
public IReadOnlyList<DmlScopeFact> DmlScopeFacts { get; init; } = Array.Empty<DmlScopeFact>();
```

`From`에서 지역 변수로 뽑는다. **프롬프트와 같은 기준일 파라미터 선택 규칙을 써야 한다** — 두 곳이 다르게 고르면 재료가 갈라진다.

```csharp
var dmlScopeFacts = DmlScopeExtractor.Extract(spDef.DdlText, ResolveDateParameter(analysis));
```

조기 반환 조건에 `&& dmlScopeFacts.Count == 0`을 잇고, initializer에 `DmlScopeFacts = dmlScopeFacts,`를 넣는다.

같은 파일 하단에 헬퍼를 둔다.

```csharp
/// <summary>
/// 기준일 파라미터를 고르는 단일 규칙. AiService의 프롬프트 렌더도 이 메서드를
/// 부른다 - 두 곳이 다르게 고르면 프롬프트의 표와 L1의 기대가 갈라지고,
/// 그러면 모델이 옳게 옮겨도 L1이 틀렸다고 한다.
/// </summary>
public static string ResolveDateParameter(SpStaticAnalysisResult? analysis) =>
    analysis?.ProcedureParameters
        .FirstOrDefault(p => p.Contains("YMD", StringComparison.OrdinalIgnoreCase))
    ?? string.Empty;
```

그리고 **Task 9 Step 5에서 `AiService`에 인라인으로 썼던 선택 로직을 이 헬퍼 호출로 바꾼다.**

```csharp
var dateParameter = SpecExpectations.ResolveDateParameter(spDef.StaticAnalysis);
```

- [ ] **Step 4: L1 검사를 구현한다**

`MechanicalValidator.cs`: `ErrorType`에 `DmlScopeTableMissing`을 추가하고 `Validate`에 `CheckDmlScopeTable(cleansed, expectations, result);`를 더한다.

```csharp
/// <summary>
/// 기계 확정 DML 범위 표가 명세서에 옮겨졌는지 본다.
///
/// 자연어를 읽지 않는다 - 헤딩의 존재와 각 문장의 라인 번호가 표 행으로
/// 나타나는지만 본다. 부재 서술을 판정하려 들면 축 B가 겪은 오탐(실측
/// 15건 중 14건)이 그대로 재현된다.
///
/// 라인 번호를 대조 키로 쓰는 이유는 그것이 유일하고 청킹과 무관하기
/// 때문이다. 문장 순번은 채번이 리셋되므로 키가 될 수 없다.
/// </summary>
private static void CheckDmlScopeTable(
    string markdown, SpecExpectations expectations, ValidationResult result)
{
    if (expectations.DmlScopeFacts.Count == 0) return;

    if (!markdown.Contains(DmlScopeExtractor.DmlScopeTableHeading, StringComparison.Ordinal))
    {
        var message =
            $"기계 확정 DML 범위 표가 명세서에 없습니다. `{DmlScopeExtractor.DmlScopeTableHeading}` "
            + $"헤딩과 {expectations.DmlScopeFacts.Count}개 행을 그대로 옮겨야 합니다.";
        result.Errors.Add(message);
        result.DetailedErrors.Add(new DetailedError
        {
            Type = ErrorType.DmlScopeTableMissing,
            Message = message
        });
        return;
    }

    var rowLines = MarkdownSectionLocator.SplitLines(markdown)
        .Where(l => l.TrimStart().StartsWith("|", StringComparison.Ordinal))
        .ToList();

    foreach (var fact in expectations.DmlScopeFacts)
    {
        var lineToken = fact.Line.ToString();
        var present = rowLines.Any(
            row => row.Split('|').Any(cell => cell.Trim() == lineToken));
        if (present) continue;

        var message =
            $"DML 범위 표에 원본 DDL 라인 {fact.Line}의 {fact.Operation} 행이 없습니다. "
            + "표는 기계가 확정한 것이므로 행을 생략하거나 합칠 수 없습니다.";
        result.Errors.Add(message);
        result.DetailedErrors.Add(new DetailedError
        {
            Type = ErrorType.DmlScopeTableMissing,
            Message = message,
            RawContext = $"{fact.Operation} @ line {fact.Line}"
        });
    }
}
```

- [ ] **Step 5: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0, 건너뜀 0.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core tests/ReSet.Core.Tests
git commit -m "$(cat <<'EOF'
feat: DML 범위 표가 명세서에 옮겨졌는지 L1이 대조한다

자연어를 읽지 않고 헤딩과 라인 행의 존재만 본다. 부재 서술을 판정하려
들면 축 B가 겪은 오탐(실측 15건 중 14건)이 재현된다. 기준일 파라미터
선택 규칙을 SpecExpectations.ResolveDateParameter 한 곳에 모아 프롬프트의
표와 L1의 기대가 갈라지지 않게 한다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: 파생 테이블 정의 표 (🔴 1건)

**Files:**
- Create: `src/ReSet.Core/Services/DerivedTableColumnExtractor.cs`
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs`, `AiService.cs`, `MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/DerivedTableColumnExtractorTests.cs` (신규), `MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: Task 10의 패턴
- Produces:
  - `public sealed record DerivedColumnDefinition(string Alias, string Column, string Expression, IReadOnlyList<string> Anchors)`
  - `public static IReadOnlyList<DerivedColumnDefinition> DerivedTableColumnExtractor.Extract(string? ddlText)`
  - `public const string DerivedTableHeading = "### 파생 테이블 정의 (기계 확정 — 수정 금지)"`
  - `SpecExpectations.DerivedColumns`, `ErrorType.DerivedTableDefinitionMissing`

**배경:** 🔴 1건은 조건 범위가 아니라 **표현식 깊이**의 문제다. `EXCEPTION_PROC`의 SET 우변이 `ISNULL(X.PGCOMM,0)`에서 멈추는데 `X`는 파생 테이블이고 그 안에 `IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt)` — 프로모션 건의 원가 기준금액 — 이 들어 있다. Spec은 X의 정의를 어디에도 적지 않았다.

여기는 **앵커 방식이 성립한다** — 표현식 안의 식별자가 그대로 앵커다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/DerivedTableColumnExtractorTests.cs` (신규):

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class DerivedTableColumnExtractorTests
    {
        [Fact]
        public void Extract_UpdateFromDerivedTable_ShouldCaptureColumnExpressions()
        {
            // EXCEPTION_PROC 실행순서 13 실측 형태. Spec은 SET 우변을
            // ISNULL(X.PGCOMM,0)까지만 적고 X의 정의를 어디에도 적지 않았다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A
    SET    A.PGComm = ISNULL(X.PGCOMM, 0)
    FROM   dbo.TSettleMst A
    JOIN   (SELECT PLTID,
                   IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt) AS PGCOMM
            FROM   dbo.TSettleMst A) X ON X.PLTID = A.PLTID
END";

            var definitions = DerivedTableColumnExtractor.Extract(ddl);

            var pgComm = Assert.Single(definitions, d => d.Column == "PGCOMM");
            Assert.Equal("X", pgComm.Alias);
            Assert.Contains("DiscountFlag", pgComm.Anchors);
            Assert.Contains("DiscountAmt", pgComm.Anchors);
        }

        [Fact]
        public void Extract_NoDerivedTable_ShouldReturnEmpty()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = B.C FROM dbo.T A JOIN dbo.U B ON A.ID = B.ID
END";

            Assert.Empty(DerivedTableColumnExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_EmptyDdl_ShouldReturnEmpty()
        {
            Assert.Empty(DerivedTableColumnExtractor.Extract(null));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DerivedTableColumnExtractorTests"`
Expected: FAIL — 클래스가 없어 컴파일 오류.

- [ ] **Step 3: 추출기를 만든다**

`src/ReSet.Core/Services/DerivedTableColumnExtractor.cs` (신규):

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Alias">파생 테이블의 별칭(예: "X").</param>
    /// <param name="Column">파생 테이블이 노출하는 컬럼 이름.</param>
    /// <param name="Expression">그 컬럼의 정의 표현식 원문.</param>
    /// <param name="Anchors">표현식 안의 식별자. 명세서 본문에서 그대로 찾는다.</param>
    public sealed record DerivedColumnDefinition(
        string Alias, string Column, string Expression, IReadOnlyList<string> Anchors);

    /// <summary>
    /// UPDATE의 FROM 절에 있는 파생 테이블의 컬럼 정의를 뽑는다.
    ///
    /// SET 우변이 X.PGCOMM에서 멈추면 명세서도 거기서 멈춘다. X 안의
    /// IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt)가 프로모션
    /// 건의 원가 기준금액인데, 그 사실이 통째로 소실된 것이 이번 감사의
    /// 유일한 축 A 🔴이다.
    ///
    /// 표현식 안의 식별자가 그대로 앵커이므로 여기는 앵커 방식이 성립한다.
    /// </summary>
    public static class DerivedTableColumnExtractor
    {
        public const string DerivedTableHeading = "### 파생 테이블 정의 (기계 확정 — 수정 금지)";

        private static readonly Regex IdentifierRegex =
            new(@"\b[A-Za-z][A-Za-z0-9_]{2,}\b", RegexOptions.Compiled);

        /// <summary>SQL 키워드와 흔한 내장 함수는 앵커가 아니다.</summary>
        private static readonly HashSet<string> NonAnchors = new(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "JOIN", "ON", "AND", "OR", "NOT", "NULL", "CASE",
            "WHEN", "THEN", "ELSE", "END", "AS", "IIF", "ISNULL", "CAST", "CONVERT",
            "SUM", "MIN", "MAX", "COUNT", "AVG", "ROUND", "INT", "VARCHAR", "MONEY", "dbo"
        };

        public static IReadOnlyList<DerivedColumnDefinition> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<DerivedColumnDefinition>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null) return Array.Empty<DerivedColumnDefinition>();

                var visitor = new DerivedTableVisitor();
                fragment.Accept(visitor);
                return visitor.Definitions;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DerivedTableColumnExtractor] 파생 테이블 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<DerivedColumnDefinition>();
            }
        }

        private sealed class DerivedTableVisitor : TSqlFragmentVisitor
        {
            public List<DerivedColumnDefinition> Definitions { get; } = new();

            public override void Visit(QueryDerivedTable node)
            {
                var alias = node.Alias?.Value;
                if (string.IsNullOrWhiteSpace(alias)) return;
                if (node.QueryExpression is not QuerySpecification spec) return;

                foreach (var element in spec.SelectElements.OfType<SelectScalarExpression>())
                {
                    var column = element.ColumnName?.Value
                        ?? (element.Expression as ColumnReferenceExpression)
                            ?.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value;
                    if (string.IsNullOrWhiteSpace(column)) continue;

                    var expression = TextOf(element.Expression);
                    Definitions.Add(new DerivedColumnDefinition(
                        alias!, column!, expression, BuildAnchors(expression)));
                }
            }

            private static string TextOf(TSqlFragment? fragment)
            {
                if (fragment == null) return string.Empty;

                var text = string.Concat(
                    fragment.ScriptTokenStream
                        .Skip(fragment.FirstTokenIndex)
                        .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                        .Select(t => t.Text));

                return Regex.Replace(text, @"\s+", " ").Trim();
            }

            private static IReadOnlyList<string> BuildAnchors(string expression)
            {
                var anchors = new List<string>();
                foreach (Match match in IdentifierRegex.Matches(expression))
                {
                    if (NonAnchors.Contains(match.Value)) continue;
                    if (anchors.Contains(match.Value, StringComparer.OrdinalIgnoreCase)) continue;
                    anchors.Add(match.Value);
                }

                return anchors;
            }
        }
    }
}
```

- [ ] **Step 4: 추출기 테스트 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DerivedTableColumnExtractorTests"`
Expected: PASS (3개)

- [ ] **Step 5: L1 검사 테스트를 쓴다**

```csharp
[Fact]
public void Validate_MissingDerivedTableDefinition_ShouldBeAnError()
{
    var expectations = EmptyExpectations() with
    {
        DerivedColumns = new[]
        {
            new DerivedColumnDefinition(
                "X", "PGCOMM",
                "IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt)",
                new[] { "DiscountFlag", "DiscountAmt", "TxAmt" })
        }
    };
    var markdown = RequiredHeadersMarkdown()
        + "\nPG 수수료는 `ISNULL(X.PGCOMM, 0)`으로 계산합니다.\n";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.DerivedTableDefinitionMissing);
}

[Fact]
public void Validate_DerivedTableDefinitionPresent_ShouldPass()
{
    var expectations = EmptyExpectations() with
    {
        DerivedColumns = new[]
        {
            new DerivedColumnDefinition(
                "X", "PGCOMM",
                "IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt)",
                new[] { "DiscountFlag", "DiscountAmt", "TxAmt" })
        }
    };
    var markdown = RequiredHeadersMarkdown()
        + "\n### 파생 테이블 정의 (기계 확정 — 수정 금지)\n"
        + "| 별칭 | 컬럼 | 정의 표현식 |\n| :--- | :--- | :--- |\n"
        + "| X | PGCOMM | IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt) |\n";

    var result = new MechanicalValidator().Validate(markdown, expectations);

    Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.DerivedTableDefinitionMissing);
}
```

- [ ] **Step 6: `SpecExpectations`와 L1 검사를 구현한다**

`SpecExpectations.cs`:

```csharp
/// <summary>UPDATE FROM 절 파생 테이블의 컬럼 정의. SET 우변만 적고 멈추는 것을 막는다.</summary>
public IReadOnlyList<DerivedColumnDefinition> DerivedColumns { get; init; }
    = Array.Empty<DerivedColumnDefinition>();
```

`From`에서 `var derivedColumns = DerivedTableColumnExtractor.Extract(spDef.DdlText);`로 뽑고, 조기 반환 조건에 `&& derivedColumns.Count == 0`을 이은 뒤 initializer에 `DerivedColumns = derivedColumns,`를 넣는다.

`MechanicalValidator.cs`: `ErrorType`에 `DerivedTableDefinitionMissing`을 추가하고 `Validate`에 `CheckDerivedTableDefinitions(cleansed, expectations, result);`를 더한다.

```csharp
/// <summary>
/// 파생 테이블 컬럼의 정의 표현식이 명세서에 있는지 본다.
///
/// 헤딩 존재만으로는 부족하다. SET 우변이 X.PGCOMM에서 멈추면 명세서도 거기서
/// 멈추는데, 그 컬럼이 무엇으로 계산되는지가 금액을 결정한다. 그래서 표현식의
/// 앵커까지 본다.
///
/// 앵커 하나만 있으면 통과다. 전부 요구하면 표현식을 풀어 설명한 정상 서술이
/// 결함이 된다.
/// </summary>
private static void CheckDerivedTableDefinitions(
    string markdown, SpecExpectations expectations, ValidationResult result)
{
    if (expectations.DerivedColumns.Count == 0) return;

    foreach (var definition in expectations.DerivedColumns)
    {
        if (definition.Anchors.Count == 0) continue;

        var found = definition.Anchors.Any(
            anchor => markdown.Contains(anchor, StringComparison.OrdinalIgnoreCase));
        if (found) continue;

        var message =
            $"파생 테이블 `{definition.Alias}`의 컬럼 `{definition.Column}` 정의가 "
            + $"명세서에 없습니다: `{definition.Expression}`. "
            + $"SET 우변이 `{definition.Alias}.{definition.Column}`에서 멈추면 "
            + "그 값이 무엇으로 계산되는지가 소실됩니다. "
            + $"(대조 앵커: {string.Join(", ", definition.Anchors)})";
        result.Errors.Add(message);
        result.DetailedErrors.Add(new DetailedError
        {
            Type = ErrorType.DerivedTableDefinitionMissing,
            Message = message,
            RawContext = definition.Expression
        });
    }
}
```

- [ ] **Step 7: 프롬프트에 표를 렌더한다**

`AiService.cs`의 규칙 목록, Task 9가 넣은 DML 범위 표 다음:

```csharp
var derivedColumns = DerivedTableColumnExtractor.Extract(spDef.DdlText);
if (derivedColumns.Count > 0)
{
    rules.Add($"{ruleIndex++}. [CRITICAL DERIVED TABLE TABLE] The following derived-table column definitions are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## CRUD 분석` under the exact heading shown. When a SET expression references one of these aliases, you MUST NOT stop at the alias reference - the definition below is what determines the amount.");
    rules.Add($"   {DerivedTableColumnExtractor.DerivedTableHeading}");
    rules.Add("   | 별칭 | 컬럼 | 정의 표현식 |");
    rules.Add("   | :--- | :--- | :--- |");

    foreach (var definition in derivedColumns)
    {
        rules.Add(
            $"   | {definition.Alias} | {definition.Column} | {EscapeTableCell(definition.Expression)} |");
    }

    rules.Add("");
}
```

- [ ] **Step 8: 전체 테스트와 경고 수를 확인한다**

Run: `dotnet test`
Expected: 실패 0, 건너뜀 0.

Run: `dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l`
Expected: `8`

- [ ] **Step 9: 커밋**

```bash
git add src/ReSet.Core tests/ReSet.Core.Tests
git commit -m "$(cat <<'EOF'
feat: 파생 테이블 컬럼 정의를 표로 강제하고 L1이 대조한다

EXCEPTION_PROC의 SET 우변이 ISNULL(X.PGCOMM,0)에서 멈추고 X의 정의가
문서 어디에도 없었다. X 안의 IIF(ISNULL(A.DiscountFlag,'N')='Y', ...)가
프로모션 건의 원가 기준금액이라, 명세만 보고 이행하면 TxAmt 기준으로
계산해 금액이 달라진다. 이번 감사의 유일한 축 A 🔴이다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: 골든 케이스 회귀 테스트와 문서 갱신

**Files:**
- Create: `tests/ReSet.Core.Tests/AxisAGoldenCaseTests.cs`
- Modify: `docs/todo.md`
- Test: 자기 자신

**Interfaces:**
- Consumes: Task 1–11의 모든 추출기와 L1 검사
- Produces: 없음 (회귀 안전판)

**배경:** 감사가 `정합`으로 판정한 세 SP가 새 검사에서 결함으로 잡히면 **검사가 틀린 것**이다. 그 판정을 코드에 고정한다. 산출물 원본은 저장소에 있다.

- [ ] **Step 1: 골든 케이스 테스트를 쓴다**

`tests/ReSet.Core.Tests/AxisAGoldenCaseTests.cs` (신규):

```csharp
using System;
using System.IO;
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 감사가 `정합`으로 판정한 세 SP는 새 검사에서 결함으로 잡히면 안 된다.
    /// 잡힌다면 검사가 틀린 것이다.
    ///
    /// 추출기만 돌린다 - 명세서 본문 대조(L1)는 그 SP의 Spec.md가 필요한데,
    /// 산출물 경로가 환경마다 달라 단위테스트의 전제로 삼기에 불안정하다.
    /// 여기서 고정하는 것은 "추출기가 이 원본에서 폭발하거나 터무니없는 양의
    /// 재료를 쏟아내지 않는다"는 하한이다.
    /// </summary>
    public class AxisAGoldenCaseTests
    {
        private static readonly string[] GoldenProcedures =
        {
            "dbo.UP_UTIL_SETTLE_CANCEL_INS",
            "dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD",
            "dbo.UP_Util_Settle_Summary_AcqManual"
        };

        [Theory]
        [InlineData("dbo.UP_UTIL_SETTLE_CANCEL_INS")]
        [InlineData("dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD")]
        [InlineData("dbo.UP_Util_Settle_Summary_AcqManual")]
        public void Extractors_ShouldNotThrowOnGoldenProcedures(string procedureName)
        {
            var ddl = TryReadObjectDefinition(procedureName);
            if (ddl == null) return; // 산출물이 없는 환경에서는 건너뛴다.

            var comments = SourceCommentExtractor.Extract(ddl);
            var rounding = RoundingSemanticsExtractor.Extract(ddl);
            var options = SessionOptionsExtractor.Extract(ddl);
            var scopes = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");
            var derived = DerivedTableColumnExtractor.Extract(ddl);

            // 배너가 잦으면 사람이 읽지 않는다 - 재료가 폭주하지 않는지 본다.
            Assert.InRange(comments.Count, 0, 40);
            Assert.All(rounding, c => Assert.False(string.IsNullOrWhiteSpace(c.ThirdArgument)));
            Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o)));
            Assert.All(scopes, s => Assert.True(s.Line > 0));
            Assert.All(derived, d => Assert.False(string.IsNullOrWhiteSpace(d.Column)));
        }

        [Fact]
        public void GoldenProcedureList_ShouldMatchTheAuditVerdict()
        {
            // 감사 보고서 §3-A에서 `정합`으로 판정된 셋. 이 목록을 줄이려면
            // 감사를 다시 돌려 근거를 바꿔야 한다.
            Assert.Equal(3, GoldenProcedures.Length);
        }

        private static string? TryReadObjectDefinition(string procedureName)
        {
            var root = FindRepositoryRoot();
            if (root == null) return null;

            var path = Path.Combine(
                root, "output", "Objects", $"{procedureName}.Procedure", "raw", "object_definition.sql");

            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static string? FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ReSet.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return null;
        }
    }
}
```

- [ ] **Step 2: 테스트를 돌린다**

Run: `dotnet test --filter "FullyQualifiedName~AxisAGoldenCaseTests"`
Expected: PASS (4개). 실패하면 추출기가 실제 원본에서 폭주하거나 예외를 던진 것이다 — 해당 추출기의 태스크로 돌아간다.

- [ ] **Step 3: `docs/todo.md`의 프롬프트 유예 서술을 갱신한다**

`docs/todo.md:12-17`의 불릿에 다음 문단을 잇는다.

```markdown
- **2026-08-17 — 프롬프트 유예를 부분적으로 깼다.** 위 "프롬프트는 재생성 결과가
  비결정적이라 별도 설계로 다룬다"는 판단은
  [축 A 명세서 충실도 설계](superpowers/specs/2026-08-17-axis-a-spec-fidelity-design.md)에서
  조건부로 해제됐다. **비결정성을 없애는 것이 아니라, 비결정적 산출물을 결정적으로
  검사한다** — 프롬프트 규칙을 혼자 추가하지 않고, 규칙과 L1 검사가 같은 추출기
  결과에서 나오는 경우에만 추가한다. 그 계약을 지키지 않는 프롬프트 사안(P1의
  `목차가 S01의 TargetTables를 채우지 못한다` 등)은 여전히 유예 상태다.
```

- [ ] **Step 4: 전체 테스트와 경고 수를 최종 확인한다**

Run: `dotnet test`
Expected: 실패 0, 건너뜀 0.

Run: `dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l`
Expected: `8`

- [ ] **Step 5: 커밋**

```bash
git add tests/ReSet.Core.Tests/AxisAGoldenCaseTests.cs docs/todo.md
git commit -m "$(cat <<'EOF'
test: 감사가 정합으로 판정한 세 SP를 회귀 안전판으로 고정한다

새 검사에서 이 셋이 결함으로 잡히면 검사가 틀린 것이다. 산출물이 없는
환경에서는 건너뛰어 CI를 원본 존재에 묶지 않는다.

todo.md의 프롬프트 유예 서술도 함께 갱신했다 — 규칙과 검사가 같은 추출기
결과에서 나오는 경우에만 해제된다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```
