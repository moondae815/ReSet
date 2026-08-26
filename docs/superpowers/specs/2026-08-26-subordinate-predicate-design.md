# 하위 스코프 술어 재료 — 소실과 이전을 구분한다

> 2026-08-26 · 브랜치 `subordinate-predicate` · 기반 `8e29b71`

## 0. 왜 지금인가

2026-08-26 표본 판정이 검사 B 발화 중 **30건을 세 관용구의 구조적 거짓양성**으로
확정했다(`docs/known-defects.md` (5-2-4)). 셋 다 같은 일을 한다 — **원본이 최상위
WHERE에 두었던 술어를 이행이 하위 스코프로 옮겼다.**

| 부류 | 건수 | 이행이 한 일 |
|---|---:|---|
| `EXCEPTION_PROC UPDATE 2 · YMD, PGName, DiscountFlag` | 10 | 술어를 파생 테이블로 옮기고 `PLTID+ID`(행 동일성)로 조인 |
| `EXCEPTION_PROC UPDATE 17 · YMD, PGName` | 10 | 같은 관용구의 CTE 판(`;WITH FeeSource AS …`) |
| `EXCEPTION_PROC UPDATE 18 · PLTID` | 10 | `WHERE PLTID IN (하위질의)`를 `EXISTS (… B.PLTID = A.PLTID …)`로 재작성 |

검사 B는 "명세서가 확정한 최상위 술어 컬럼이 없다"고 말하는데, 그 컬럼은 **없어진
것이 아니라 옮겨간 것**이다. 이 설계는 그 둘을 구분한다.

### 기존 가드가 이 자리를 안 막는 이유, 그리고 그 근거가 무너진 것

`MechanicalValidator.cs:6397`에 이미 가드가 있다 — 조인 상대가 CTE·파생 테이블이면
(`HasOpaqueJoinSource`) **조인 키 대조를 접는다.** 그런데 **술어 컬럼 대조는 일부러
접지 않는다.** 주석이 이유를 단다:

> 최상위 WHERE 술어 컬럼 대조는 이 사각지대와 무관하므로(S07 U13의 실제 결함
> YMD·PGNAME 누락은 이쪽에서 여전히 잡힌다) 그대로 둔다.

**그 근거를 실물로 확인했더니 무너진다.** `POQSettleBatch1/S07`의 U13은 위 세
관용구와 **구조적으로 같다**:

```sql
;WITH CardCost AS (
    SELECT A.PLTID, A.ID, … FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
    INNER JOIN … 
    WHERE A.YMD = @pi_strYMD
      AND A.PGNAME IN (SELECT value FROM STRING_SPLIT(@v_strCardPGNames, '+'))
      AND (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))
)
UPDATE Y SET …
FROM SETTLE_POQ_DB.dbo.TSettleMst AS Y
INNER JOIN CardCost AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;   -- WHERE 절 없음
```

원본 명세서(`Spec.md:321-327`)는 `UPDATE 13`이 **최상위와 파생 테이블 X 양쪽에**
같은 필터를 갖는다고 확정한다 — 최상위 `Y.YMD = @pi_strYMD`·`Y.PGNAME IN (…)`,
파생 X `A.YMD = @pi_strYMD`·`A.PGNAME IN (…)`. X가 이미 그 행들로 좁히고
행 동일성으로 조인하므로 **최상위 필터는 원본에서도 중복**이다.

즉 주석이 "실제 결함"이라 부른 것이 다른 30건과 같은 이전 관용구다. **S07 U13은
이 설계의 판정 대상으로 다시 연다**(§7).

## 1. 범위

**포함** — 검사 B(`CheckAnchoredStatementFacts`)의 술어 컬럼 대조가 소실과 이전을
구분하게 만든다. 재료는 `StepSqlStatementReader`가 문장에서 뽑는다.

**제외** —

- **검사 C**(`CheckAnchoredStatementExtras`). 다른 재료가 필요하다 — §5.
- **의미 동등성 증명.** 이 설계는 "옮겨갔다"까지만 말한다 — §6.
- 조인 키 대조. 기존 `HasOpaqueJoinSource` 가드가 그대로 담당한다.

## 2. 지금 "최상위"가 무엇인지 — 양쪽이 같은 기준을 쓴다

`StepSqlStatementReader.ColumnCollector`가 그 정의를 코드로 갖고 있다:

```csharp
/// <summary>스칼라 하위질의 안쪽으로 내려가지 않는다 - 최상위 술어 컬럼만 센다.</summary>
public override void ExplicitVisit(ScalarSubquery node) { }

/// <summary>파생 테이블(FROM 절 안의 (SELECT …) 별칭) 안쪽도 최상위가 아니다.</summary>
public override void ExplicitVisit(QueryDerivedTable node) { }
```

ScriptDom에서 `EXISTS`·`IN`의 하위질의도 `ScalarSubquery`이므로 셋 다 함께 막힌다.

명세서 쪽도 같은 기준이다 — DML 범위 표의 열 이름이
`WHERE 최상위 술어 컬럼(조인 결합 포함 · 대상 한정 아님)`이다.

**양쪽이 같은 기준을 쓰는 것이 정상이고, 문제는 이행이 구조를 바꿀 때 생긴다.**
원본과 이행의 최상위가 서로 다른 것을 담게 되면, 같은 기준으로 재는 대조가
"없어졌다"고 말한다.

## 3. 재료 — 하위 스코프 술어 수집기

`StepSqlStatement`에 필드를 하나 더한다. 현재 레코드는 위치 매개변수 6개 +
기본값 2개다:

```csharp
public sealed record StepSqlStatement(
    string Kind, string TargetTable, int? Anchor,
    IReadOnlyList<string> PredicateColumns,
    IReadOnlyList<string> JoinColumns,
    bool HasGrouping,
    bool HasOpaqueJoinSource = false,
    string? CodeAnchor = null);
```

새 값은 **기본값 있는 `init` 속성**으로 더한다 — 위치 매개변수를 늘리면 기존
생성자 호출이 전부 깨진다(`SweepIndicators.StepsSkippedForParseFailure`·
`HarnessGaps.UnresolvedProcedureReferences`가 같은 선례다).

```csharp
public IReadOnlyList<string> SubordinatePredicateColumns { get; init; }
    = Array.Empty<string>();
```

### 수집기가 깔끔하게 떨어지는 이유

**`UPDATE`/`DELETE`의 최상위 WHERE는 `QuerySpecification`이 아니라
`UpdateSpecification`/`DeleteSpecification`에 달린다.** 따라서 문장 안에서 만나는
모든 `QuerySpecification`은 **정의상 하위 스코프**다 — CTE 본문이거나, 파생
테이블이거나, 하위질의다. 별도의 "여기는 최상위인가" 판정이 필요 없다.

```csharp
private sealed class SubordinatePredicateCollector : TSqlFragmentVisitor
{
    private readonly List<string> _columns = new();
    public IReadOnlyList<string> Columns => _columns;

    public override void Visit(QuerySpecification node)
    {
        if (node.WhereClause == null) return;

        // 그 스코프의 "최상위" WHERE만 - 같은 ColumnCollector 규칙을 재사용한다.
        // 더 안쪽 스코프는 이 방문자가 각각 따로 방문해 모은다.
        var inner = new ColumnCollector();
        node.WhereClause.Accept(inner);
        _columns.AddRange(inner.Columns);
    }
}
```

`ColumnCollector`를 재사용하는 것이 핵심이다 — 스코프마다 같은 규칙이 적용되고,
중첩은 바깥 방문자의 기본 순회가 처리한다.

**WHERE만 본다.** `SET` 절·선택 목록·`ON` 절은 안 본다. `SET`을 세면 갱신 대상
컬럼이 술어로 오인돼 잘못 침묵한다(예: `UPDATE 18`이 `SET OutState = 9`를 하는데
명세서가 `OutState`를 술어로 기대하면 접혀 버린다). `ON` 절은 조인이지 필터가
아니고, 조인 키 대조는 기존 가드 담당이다.

### 문장 전체를 순회하면 안 되는 이유 — 진입점을 넷으로 한정한다

`statement.Accept(subordinate)`로 전체를 순회하면 **`SET` 절 안의 하위질의까지
걸린다.** 기존 테스트가 그 모양을 이미 담고 있다
(`CollectsTopLevelPredicateAndJoinColumns_ButNotSubqueryColumns`):

```sql
UPDATE Y SET Y.CLCOMM = (SELECT TOP 1 X.Amt FROM dbo.TCost AS X WHERE X.Hidden = 1)
FROM dbo.TSettleMst AS Y INNER JOIN dbo.TCost AS C ON C.PLTID = Y.PLTID
WHERE Y.YMD = @p AND Y.UseState = 1;
```

`X.Hidden`은 **갱신할 값을 고르는 술어**이지 **갱신 대상 행을 고르는 술어**가
아니다. 이것을 하위 스코프 술어로 세면, 우연히 이름이 같은 컬럼이 진짜 소실을
가려 잘못 침묵시킨다.

그래서 문장 전체가 아니라 **대상 행을 거를 수 있는 네 자리에서만** 수집한다:

```csharp
var subordinate = new SubordinatePredicateCollector();
ctes?.Accept(subordinate);    // WITH 절 - CTE 본문
from?.Accept(subordinate);    // FROM 절 - 파생 테이블 + JOIN ON 절 안의 하위질의
where?.Accept(subordinate);   // 최상위 WHERE 안의 EXISTS·IN·스칼라 하위질의
```

`ctes`는 `Visit(UpdateStatement node)`의 `node.WithCtesAndXmlNamespaces`를
`Add(...)`로 함께 넘겨 받는다 — 타입을 추론하거나 캐스트하지 않는다.
`DeleteStatement`도 같다. `INSERT`는 검사 B·C의 후보가 아니므로
(`IsCandidateForAnchoredStatementCheck`) 넘기지 않아도 되지만, 넘겨도 무해하다.

**네 번째 자리 — `from?.Accept(subordinate)`가 JOIN `ON` 절 안의 하위질의까지
훑는다.** 코드는 세 번의 `Accept` 호출(`ctes`·`from`·`where`)만 하지만, `from`
순회는 파생 테이블뿐 아니라 `JOIN ... ON` 절의 하위질의도 함께 방문한다 —
`FromClause`의 기본 순회가 `ON` 절 안으로 내려가기 때문이다. 실측:

```sql
UPDATE Y SET Y.X = 1 FROM dbo.TSettleMst AS Y
INNER JOIN dbo.TCost AS C ON C.PLTID = Y.PLTID
  AND C.ID IN (SELECT Z.ID FROM dbo.TZ AS Z WHERE Z.Hidden = 1)
WHERE Y.YMD = @p;
```

`SubordinatePredicateColumns`에 `Hidden`이 잡힌다(`Sub=[Hidden]`). 이 자리는
설계 초안에 없었다 — **동작 자체는 방어 가능하다**: `INNER JOIN`이면 그 하위질의가
대상 행을 실제로 거르므로(조인 파트너 행이 `WHERE Z.Hidden = 1`을 만족하지 않으면
조인 자체가 성립하지 않는다), 갱신 대상 행을 거르는 술어로 세는 것이 의도와
어긋나지 않는다. 문서화가 뒤늦게 이 자리를 인정하는 것뿐이다.

## 4. 판정 — 컬럼 단위로 거른다

`CheckAnchoredStatementFacts`의 `ReportMissing`이 지금은 이렇다:

```csharp
var missing = expected.Where(c => !present.Contains(c)).ToList();
```

여기에 하위 스코프 집합을 더한다:

```csharp
var relocated = new HashSet<string>(
    group.SelectMany(a => a.Statement.SubordinatePredicateColumns),
    StringComparer.OrdinalIgnoreCase);

var missing = expected
    .Where(c => !present.Contains(c) && !relocated.Contains(c))
    .ToList();
```

**전부-접기가 아니라 컬럼별이다.** `YMD`는 CTE로 이전했고 `PGNAME`은 어디에도
없다면 `PGNAME`만 발화한다. 전부-접기보다 정밀하고, 진짜 소실이 이전에 가려지지
않는다.

`group`은 청크 분할 조각들을 묶은 것이므로(`같은 (앵커, 종류)`), 조각 어디의 하위
스코프에 있어도 이전으로 본다 — 조각들이 논리적으로 한 문장이라는 기존 전제와 같다.

**조인 키 대조는 건드리지 않는다.** `HasOpaqueJoinSource` 가드가 그대로 담당한다.

## 5. 검사 C가 분리되는 이유 — 방향이 반대다

원래 이 작업은 「CTE 가드를 검사 B·C로 확장」이라는 한 항목이었다. **그 표현이
부정확했다.**

검사 C는 "명세서에 없는 컬럼을 **최상위에** 썼다"를 본다. **이전은 최상위에서
밖으로 나가는 방향**이라 초과를 만들 수 없다. 문장 쪽 하위 스코프 정보는 검사 C에
쓸모가 없다.

검사 C의 거울상은 **반대 방향**이다 — 원본이 파생 스코프에 두었던 술어를 이행이
최상위로 끌어올리면 발화한다. 그걸 막으려면 **명세서 쪽 스코프 정보**가 필요하다.
명세서의 「집합 술어」 표(`DmlScopeExtractor.SetPredicateTableHeading`)가
`최상위` / `파생 테이블 X` 라벨을 실제로 갖고 있지만, `SpecStatementFacts`는 그것을
싣지 않는다:

```csharp
public sealed record SpecStatementFacts(
    IReadOnlyList<SpecDmlRow> DmlRows,
    IReadOnlyList<SpecSetTarget> SetTargets,
    IReadOnlyList<SpecLocalVariable> LocalVariables)
```

**따라서 검사 C는 「집합 술어 표를 재료로 올리기」라는 별개 작업이다.** 재료가
다르고, 추출기(`SpecStatementFactsExtractor`)가 새 표를 읽어야 하며, 이 설계의
어느 부분도 재사용되지 않는다. 다음 회차 항목으로 넘긴다.

## 6. 이 설계가 주장하지 않는 것

**하위 스코프에 있다고 의미 동등이 아니다.** 동등성은 조인이 대상 행 집합을
보존하느냐에 달렸다 — `UPDATE 2`·`UPDATE 13`은 `PLTID+ID`로 조인하므로 그 쌍이
`TSettleMst`의 행을 유일하게 지목할 때만 동등하다.

**그 전제는 로컬에서 검증할 수 없다.** 로컬 컨테이너는 빈 스키마이고 운영 데이터에
접속할 수 없다. `docs/known-defects.md`가 같은 전제를 이미 미확인으로 기록해 뒀다
(Proc11/S06 `CROSS APPLY` 판정 — "PLTID+ID 유일성 전제가 실제로 깨지는 배포가
있는지는 확인 못 함").

그래서 이 설계는 **"소실과 이전을 구분한다"**까지만 주장한다. 이전으로 판정된
것이 실제로 동등한지는 사람의 판정이고, 그 판정 자리는 스윕 보고서의 「판정」
칸이다. 이 구분만으로도 30건 이상이 "확인해야 할 것"에서 "구조를 안 것"으로
바뀐다.

**동명 컬럼이 진짜 소실을 가릴 수 있다.** `SubordinatePredicateColumns`가 모으는
것은 **테이블 한정 없이 이름만**이다 — 어느 스코프의 어느 테이블 컬럼인지는
버리고 문자열만 남는다. 그래서 이행이 최상위 `WHERE` 필터를 정말로 잃었더라도,
**무관한 테이블의 동명 컬럼**이 하위 스코프 어딘가에 있으면 그 소실을 침묵시킬
수 있다. 예:

```sql
WHERE Y.PLTID IN (SELECT Z.PLTID FROM dbo.TZ AS Z WHERE Z.YMD = @p)
```

`Y.YMD` 필터가 최상위에서 사라졌더라도, `TZ`(무관한 테이블)의 `Z.YMD`가
`SubordinatePredicateColumns`에 `YMD`로 잡혀 "이전됐다"로 오판할 수 있다
(실측: `Sub=[YMD]`). §0이 이미 인식한 것과 같은 부류의 위험이다 — 거기서는
`SET` 절의 동명 컬럼이 갱신 대상 컬럼을 술어로 오인해 잘못 침묵시키는 것을
근거로 `SET` 절을 배제했다. **여기 남은 세 자리(CTE 본문·파생 테이블·JOIN
ON/최상위 WHERE 하위질의)에도 같은 위험이 원리상 남아 있다** — 테이블 한정이
없다는 것 자체가 원인이라, `SET` 절을 뺀 것으로는 막히지 않는다.

**오늘 코퍼스에는 이 경로로 침묵한 건이 없다.** 2026-08-26 재측정에서 사라진
41건(`UPDATE 2·17·18`의 33건 + `UPDATE 13`의 8건)을 전부 부류별로 추적한
결과, 모두 원본 명세서에도 같은 필터가 실제로 존재하는 정당한 이전이었다 —
동명이나 우연에 기댄 침묵은 하나도 없었다. 그래서 이 위험은 **오늘은 이론
상의 것**이지만, 코퍼스가 늘거나 새 관용구가 들어오면 실제로 침묵을 만들 수
있다. 닫으려면 `SubordinatePredicateColumns`가 이름만이 아니라 **테이블(또는
별칭) 한정 수집**으로 바뀌어야 한다 — 다음 회차 개선 항목으로 남긴다.

## 7. S07 U13 재판정

이 가드가 들어가면 `POQSettleBatch1/S07`의 U13도 침묵한다(§0에서 구조를 확인했다).
그러면 감사 기록 하나가 뒤집힌다:

- `docs/audit-defect-catalog.md` 11회차 행이 **"🟠 7건 중 2건이 검사로 닫힘"**의
  하나로 `S07 갱신13 최상위 WHERE`를 든다.
- 그 판정이 **"닫힘"에서 "구조적 거짓양성이었다"로** 바뀐다.

**정정한다.** 근거(원본 명세서의 최상위·파생 이중 필터, 이행의 행 동일성 조인,
같은 관용구 30건과의 구조 일치)를 함께 적는다. 카탈로그가 "미확인으로 남긴 부류는
사라지지 않는다"를 적어 둘 만큼 이력을 중시하는 저장소이므로, **판정이 바뀐 사실
자체가 기록 대상**이다.

바뀌지 않는 것도 적는다 — 감사가 그 자리를 지목한 것 자체는 옳았다. 원본이 최상위에
두었던 필터가 이행의 최상위에 없는 것은 사실이고, 그것이 동등한지가 판정의 내용이다.

## 8. 테스트

전부 합성 SQL로. 코퍼스에 의존하지 않는다.

**재료(`StepSqlStatementReaderTests`)**

- CTE 본문의 WHERE 컬럼이 `SubordinatePredicateColumns`에 들어가고
  `PredicateColumns`에는 안 들어간다
- 파생 테이블(FROM 절의 `(SELECT …) AS X`)도 같다
- `EXISTS`·`IN` 하위질의도 같다
- **`SET` 절 컬럼은 안 들어간다** — 잘못 침묵하는 경로를 막는 못
- 중첩 스코프(CTE 안의 파생 테이블)도 모인다
- 하위 스코프가 없으면 빈 목록이다

**판정(`MechanicalValidatorTests`)**

- 명세서가 `YMD, PGNAME`을 요구하고 둘 다 CTE에 있으면 검사 B가 침묵한다
- `YMD`만 CTE에 있고 `PGNAME`은 어디에도 없으면 **`PGNAME`만** 발화한다
  (컬럼 단위 판정을 못으로 박는다 — 전부-접기 구현이 이 테스트에 죽는다)
- 하위 스코프가 없으면 기존대로 발화한다
- 조인 키 대조는 영향받지 않는다

**뮤테이션으로 확인할 것** — 수집기를 항상 빈 목록으로 / `SET` 절도 수집하게 /
컬럼별 필터를 전부-접기로. 각각 의도한 테스트가 죽는지 직접 돌린다.

## 9. 재측정

`ReSet.Cli --sweep`을 다시 돌려 델타를 잰다. 기대는 검사 B가 68에서 **30건 이상**
줄어드는 것이다(세 관용구 30건 + S07 U13). 실제 값이 기대와 크게 다르면 멈추고
원인을 찾는다 — 특히 **예상보다 많이 줄면** 수집기가 너무 넓게 잡는다는 신호다.

전후 보고서를 둘 다 남기고, 판정이 이관되는 행은 이관한다.
`docs/known-defects.md`에 델타와 S07 U13 재판정을 기록한다.
