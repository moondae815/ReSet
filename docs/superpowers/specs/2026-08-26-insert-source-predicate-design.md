# INSERT 원천 술어 배선 설계 (로드맵 3-a)

## 1. 무엇이 문제인가

`StepSqlStatementReader.DmlCollector`가 INSERT 문장을 이렇게 읽는다.

```csharp
public override void Visit(InsertStatement node) =>
    Add("INSERT", node, node.InsertSpecification?.Target, null, null,
        node.WithCtesAndXmlNamespaces);
```

`where`·`from` 자리에 항상 `null`이 간다. 같은 클래스의 `Visit(UpdateStatement)`·
`Visit(DeleteStatement)`는 실제 `WhereClause`·`FromClause`를 넘긴다. 그래서 모든
INSERT 문장의 `PredicateColumns`·`JoinColumns`는 **SQL 내용과 무관하게 구조적으로
항상 빈 목록**이다.

`InsertSpecification`에는 `WhereClause`·`FromClause` 속성이 없다 — 술어는
`InsertSource`(→ `SelectInsertSource.Select`) 안의 `QuerySpecification`에 있다.
`null`을 넘긴 것은 그 자리에 넘길 속성이 없어서였다.

이 결함 때문에 검사 B가 INSERT마다 "명세서가 확정한 술어 컬럼이 없다"고 오인했고
(코퍼스 전수 스윕에서 269건 중 199건, 74%), `IsCandidateForAnchoredStatementCheck`가
INSERT를 검사 B·C 후보에서 **한시적으로** 뺐다. 그 결과 오늘 INSERT는 어느 검사도
받지 않는다.

## 2. 실측 — 노출량

DML 범위 표(「집합 술어」표와 구분해서 셌다. 두 표 모두 행이 `| INSERT n |`로
시작하므로 헤더로 구간을 갈라야 한다):

| | |
|---|---:|
| DML 범위 표 INSERT 행 | 21 |
| ├ 최상위 술어 컬럼 있음 | 16 |
| ├ `(없음)` | 5 |
| └ 조인 키 있음 | 7 |
| INSERT 행을 가진 SP를 참조하는 단계 | 147 |
| INSERT 전용 SP만 참조해 검사 B·C 커버리지가 0인 단계 | 16 |

`DELETE`는 이미 검사 B·C를 받는다. INSERT만 빠져 있다.

## 3. 명세서가 INSERT 행에 무엇을 적는가

`output/Procedures/dbo.UP_Util_PG_Client_CMRate_Ins/docs/Spec.md`의 DML 범위 표:

```
INSERT 2 | 술어= CLIENTID, USESTATE, ContractCancelYMD | 조인= CLIENTID
INSERT 4 | 술어= ClientID, PGName, MallID, UseState, ContractCancelYMD | 조인= ClientID, PGName, MallID
```

이 컬럼들은 **원천 SELECT의 최상위 `WHERE`·`FROM`**에서 온다. `DmlScopeExtractor`가
그렇게 뽑기 때문이다:

```csharp
public override void ExplicitVisit(InsertSpecification node)
{
    var ordinal = NextOrdinal("INSERT");
    if (node.InsertSource is SelectInsertSource select)
        foreach (var spec in QuerySpecificationsOf(select.Select))
            CollectFrom("INSERT", ordinal, spec.FromClause);
    ...
}
```

`UP_UTIL_SETTLE_INS`의 `INSERT 1`이 `술어=(없음)`인 것은 하위로 미뤄서가 아니라 그
원천 SELECT에 최상위 `WHERE`가 없어서다 — 실제 필터는 파생 테이블 X 안에 있고,
명세서는 그것을 「집합 술어」표에 `파생 테이블 X`로 따로 적는다.

**따라서 읽기 쪽도 원천 SELECT의 최상위 `WHERE`를 `PredicateColumns`에 담아야 한다.**
`SubordinatePredicateColumns`로 미루는 설계는 명세서와 어긋나며 검사 B가 INSERT를
영구히 못 보게 만든다.

## 4. 설계

### 4.1 `Add`의 절 인자를 복수형으로

INSERT 원천이 `UNION`이면 `QuerySpecification`이 여럿이고, 각각 자기 `WhereClause`·
`FromClause`를 갖는다. `DmlScopeExtractor`가 그것들을 **같은 서수 하나로 합치므로**
읽기 쪽도 합쳐야 한다. 그래서 `Add`가 단수 절 대신 목록을 받는다.

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

    // (기존 주석 유지 - 대상 행을 거를 수 있는 네 자리에서만 모은다)
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

private static IReadOnlyList<T> One<T>(T? node) where T : class =>
    node is null ? Array.Empty<T>() : new[] { node };
```

호출부 셋:

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

`UPDATE`·`DELETE`의 관측 동작은 그대로다 — 절이 하나면 목록 순회는 기존 단일
`Accept`와 같다.

### 4.2 왜 INSERT는 `targetAliasScope`가 `null`인가

`ResolveTargetTable`은 대상 이름이 한정되지 않은 한 글자 이상 식별자일 때 `FROM`
절의 별칭 사전을 뒤진다. `UPDATE A SET ... FROM dbo.TSettleMst AS A` 같은 형태를
풀기 위해서다.

INSERT 대상은 별칭일 수 없다 — `INSERT INTO <별칭>`은 문법에 없다. 반면 원천
SELECT의 `FROM`은 **대상과 다른 이름 범위**다. 거기에 `FROM dbo.TFoo AS TSettleMst`가
있으면 `INSERT INTO TSettleMst`의 대상이 `TFoo`로 잘못 해석된다. 그래서 INSERT는
별칭 범위를 넘기지 않는다.

이 구분이 `froms`와 별개의 인자를 두는 이유다. `froms.FirstOrDefault()`로 갈음하면
위 오해석이 살아난다.

### 4.3 `DetectOpaqueJoinSource`를 복수 `FROM`으로

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

    var probe = new OpaqueJoinSourceProbe(cteNames);
    foreach (var from in froms) from.Accept(probe);
    return probe.Found;
}
```

`UNION` 원천의 한 갈래만 불투명해도 조인 키 대조를 접는다 — 접는 쪽이 안전한
방향이다(오탐보다 침묵).

### 4.4 헬퍼 승격

`SourceQuerySpecifications`·`QuerySpecificationsOf`는 지금
`DmlScopeExtractor.DmlScopeVisitor`(private nested) 안의 `private static`이라
밖에서 못 쓴다. 둘을 `DmlScopeExtractor` 클래스 수준으로 올리고 `internal static`으로
바꾼다. `DmlScopeVisitor`는 중첩 클래스라 호출부를 고칠 필요가 없다(이름 해석이
그대로 닿는다).

재구현하지 않는 이유: `DmlScopeExtractor`와 `StepSqlStatementReader`가 이미 같은
규칙을 두 벌 들고 있고, **이번 결함이 정확히 그 중복에서 났다**. 세 벌째를 만들 자리가
아니다.

`VALUES` 원천이면 `SourceQuerySpecifications`가 빈 열거를 낸다 — 조건 없이 실리는
행이라 대조할 술어가 없다. INSERT의 `wheres`·`froms`가 빈 목록이 되고, 오늘과 같은
결과(빈 `PredicateColumns`)가 나온다. 이때는 명세서 쪽도 `(없음)`이므로 검사 B가
발화할 것이 없다.

### 4.5 하위 범위 수집은 그대로다

`SubordinatePredicateCollector`는 `QuerySpecification`을 만날 때 그 `WhereClause`를
훑는다. INSERT에서 넘기는 것은 원천의 `WhereClause`·`FromClause` **노드**이지
`QuerySpecification` 자신이 아니므로, 원천의 최상위 `WHERE`가 하위로 두 번 세어지는
일은 없다(`WhereClause` 자체는 `QuerySpecification`이 아니다). UPDATE에서 오늘
성립하는 성질과 같다.

파생 테이블 X 안의 필터는 `froms`를 타고 하위로 잡힌다 — `UP_UTIL_SETTLE_INS`의
`INSERT 1`이 정확히 그 모양이다.

### 4.6 재편입이 드러내는 메시지 어법 결함

검사 B·C의 메시지가 `Kind`와 무관하게 `(갱신 N)`을 붙인다.

```csharp
$"{step.Code} 섹션의 {row.Kind} {row.Ordinal}(갱신 {row.Ordinal}) 문장에 명세서가 확정한 " +
```

`갱신`은 명세서가 **UPDATE 갱신 절 표**에만 쓰는 말이다. INSERT·DELETE에는 그 표가
없다 — 명세서 전체에서 `(삽입 N`·`(삭제 N`은 0건이고, DML 범위 표의 문장 칸은
`INSERT 1`·`DELETE 2`처럼 영문으로 적힌다(`SpecSetTarget` 문서 주석의 실측).

DELETE는 이미 검사 B·C 후보이므로 오늘도 `DELETE 3(갱신 3)`을 낸다. 재편입은 이
오기를 INSERT 21행으로 넓힌다. 그래서 재편입 **앞에** 고친다.

```csharp
var gloss = row.Kind.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
    ? $"(갱신 {row.Ordinal})"
    : string.Empty;
```

**딸린 결합:** `StepSweepClassifier.CoordinatePattern`이 여는 괄호를 **필수로**
요구한다.

```csharp
@"섹션의\s+(?<kind>[A-Z]+)\s+(?<ordinal>\d+)\s*\("
```

주석을 떼면 INSERT·DELETE 발화가 좌표를 잃는다 — 발화 수는 세지만 (종류, 서수)가
비어 판정표가 그 행을 좌표 없이 싣는다. 괄호를 경계로 쓰지 않게 바꾼다.

```csharp
@"섹션의\s+(?<kind>[A-Z]+)\s+(?<ordinal>\d+)(?=\s|\()"
```

이 둘은 읽기 배선과 파일이 겹치지 않으므로 병렬로 진행할 수 있다.

## 5. 검사 재편입

`ResolveOrdinal`은 위치가 아니라 신원으로 서수를 정한다.

```csharp
if (statement.CodeAnchor != null
    && codeMap.TryGetValue(statement.CodeAnchor, out var mapped)
    && string.Equals(mapped.Kind, statement.Kind, StringComparison.OrdinalIgnoreCase))
```

`codeMap` 조회가 이미 `Kind` 일치를 요구하고, U-앵커는 문장에 직접 붙는다. 따라서
`IsCandidateForAnchoredStatementCheck`에서 INSERT 배제를 걷어도 **UPDATE·DELETE의
서수는 흔들리지 않는다**. 명세서 쪽도 `IsComparableDmlRow`가 이미 INSERT를 통과시킨다.

배제를 걷는 것은 `IsCandidateForAnchoredStatementCheck` 메서드와 그 긴 문서 주석을
**통째로 지우고** `ResolveAnchoredStatements`의 호출을 함께 걷는 것이다.

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

`=> true`로 바꿔 남겨 두지 않는다 — 항상 참인 술어는 "왜 이게 있는가"를 다음 사람이
다시 풀게 만든다.

지우기 전에 그 주석이 담은 근거(스윕 실측 269→199건, 원인 배선, 되돌릴 지점)는
`docs/known-defects.md`로 옮긴다 — 코드에서 사라지되 기록에서는 사라지지 않게.

## 6. 단계 분할과 측정

읽기만 고치면 배제 필터가 여전히 INSERT를 막아 **관측 변화가 0**이다. 그래서 둘로
나눈다.

**1단계 — 읽기 배선 수정.** 4.1~4.4를 구현하고 읽기 단위 테스트를 붙인다. 배제
필터는 그대로 둔다. 이 시점의 스윕 결과는 `2026-08-26-step-sweep-c.md`와
**완전히 같아야 한다**(검사 A 20 · B 0 · C 0 · D 18 · E 59 / 캐시 17 모사 B 31 · C 18).
다르면 UPDATE·DELETE 경로에 회귀가 났다는 뜻이므로 멈춘다.

**2단계 — 배제 제거.** 5절의 변경을 하고 다시 스윕한다. 증가분을 표본 판정한다.

판정 규칙을 미리 못 박는다.

- 증가분이 **진짜 결함**이면 유지하고 `docs/known-defects.md`에 싣는다.
- 증가분이 **구조적 오탐**이면 2단계만 되돌린다(1단계 읽기 수정은 남긴다). 원인을
  기록하고 다음 라운드로 넘긴다.
- 증가분이 **0**이면 그대로 유지한다 — 커버리지가 늘고 발화가 없다는 것은 산출물이
  그 21행에 대해 명세서와 맞다는 뜻이다.

스윕은 `--sweep`으로 돌리고 보고서를 `docs/audit-reports/sweeps/2026-08-26-step-sweep-d.md`에
남긴다. `output/`은 읽기만 한다.

## 7. 테스트

읽기 쪽(1단계):

| 이름 | 확인하는 것 |
|---|---|
| `Insert_SourceWhere_FillsPredicateColumns` | `INSERT INTO T (..) SELECT .. FROM S WHERE S.UseState = 0` → `Predicates=[UseState]` |
| `Insert_SourceJoin_FillsJoinColumns` | 원천 `INNER JOIN ... ON A.ID = B.ID` → `Joins=[ID]` |
| `Insert_UnionSource_MergesBothBranches` | 두 갈래의 `WHERE` 컬럼이 한 문장에 합쳐진다 |
| `Insert_ValuesSource_CollectsNothing` | `INSERT INTO T VALUES (..)` → 빈 목록, 예외 없음 |
| `Insert_DerivedTableSource_GoesToSubordinate` | 파생 테이블 안 `WHERE`는 `SubordinatePredicateColumns`로, `PredicateColumns`는 빈 채 |
| `Insert_TargetNotResolvedFromSourceAlias` | `INSERT INTO TSettleMst .. FROM dbo.TFoo AS TSettleMst` → 대상은 `TSettleMst` (4.2 회귀) |
| `Insert_OpaqueSourceJoin_SetsHasOpaqueJoinSource` | 원천이 CTE·파생 테이블에 조인하면 참 |
| `Update_UnchangedAfterPluralSignature` | 기존 UPDATE 기대값 그대로 (회귀) |

어법 쪽(재편입 앞):

| 이름 | 확인하는 것 |
|---|---|
| `CheckB_NonUpdateKind_OmitsUpdateGloss` | DELETE 발화에 `(갱신 N)`이 붙지 않는다 |
| `CheckB_UpdateKind_KeepsUpdateGloss` | UPDATE 발화에는 그대로 붙는다 |
| `Describe_NonUpdateKind_WithoutGloss_StillExtractsCoordinates` | 괄호 없는 메시지에서도 스윕이 (종류, 서수)를 뽑는다 |

검증 쪽(2단계):

| 이름 | 확인하는 것 |
|---|---|
| `CheckB_InsertMissingPredicate_Reports` | 명세서 INSERT 행이 확정한 술어 컬럼이 단계 SQL에 없으면 발화 |
| `CheckB_InsertWithPredicate_Silent` | 있으면 침묵 (오탐 회귀 방지 — 199건의 원인) |
| `CheckC_InsertExtraPredicate_Reports` | 명세서에 없는 술어가 붙으면 발화 |
| `CheckB_InsertPresence_DoesNotShiftUpdateOrdinal` | 같은 단계에 INSERT와 UPDATE가 섞여도 UPDATE 서수 판정이 그대로 |

`Insert_ValuesSource_CollectsNothing`과 `Insert_TargetNotResolvedFromSourceAlias`는
돌연변이 시험으로 실제로 무는지 확인한다 — 둘 다 "아무 일도 안 일어남"을 주장하는
테스트라 조용히 통과할 수 있다.

## 8. 곁다리 수정 하나

`IsCandidateForAnchoredStatementCheck`의 주석이 `StepSqlStatementReader.cs:464-465`를
가리키는데 실제 위치는 493-494다. 이 주석은 5절에서 통째로 지워지므로
`docs/known-defects.md`로 옮길 때 **줄 번호 대신 멤버 이름**(`DmlCollector.Visit(InsertStatement)`)
으로 적는다. 줄 번호 인용은 이 저장소에서 반복해 어긋났다.

## 9. 하지 않는 것

- `MERGE` — `DmlCollector`가 아예 읽지 않는다. 별개 항목이다.
- `SubordinatePredicateColumns` 중복 제거 — 별개 항목이다.
- 검사 C의 명세서 쪽 거울(집합 술어 범위 라벨을 `SpecStatementFacts`로) — 별개 항목이다.
- 로드맵 3-b(코드 집합 대조 방어) — 별개 브레인스토밍으로 간다.
- `GroupingProbe`가 파생 테이블 안 `GROUP BY`까지 잡는 기존 성질 — UPDATE에서도
  오늘 그러하다. 이번에 바꾸지 않는다.

## 10. 위험

**재편입이 발화 폭증을 낳을 수 있다.** 21행 · 147단계가 새로 대조 대상이 된다.
6절의 판정 규칙이 이 위험의 출구다 — 되돌리는 것이 실패가 아니라 설계된 분기다.

**`UNION` 합치기가 명세서와 어긋날 수 있다.** `DmlScopeExtractor`가 같은 서수로
합치는 것을 보고 맞춘 것이지, 명세서에 `UNION` 원천 INSERT 실물이 있는지는 확인하지
않았다. 1단계 스윕이 같은 결과를 내는지로 간접 확인하고, 어긋나면 2단계에서 표본
판정으로 잡는다.
