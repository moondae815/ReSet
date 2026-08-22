# 축 A 재감사 ③(b)와 계획 밖 결함 — 설계

> 2026-08-23 · 대상: `2026-08-22-out-of-table-scope-design.md` §8이 "**새 재료가 필요한** 4건"으로
>미룬 ③(b), 그리고 그 브랜치가 실행 중에 발견한 계획 밖 결함 1건. 합 5건(🔴 1 · 🟡 4).

## 1. 배경

축 A 재감사 결함 34건 중 ①②(도구 버그·L1 부재)와 ③(a)(기존 표의 범위 확대, 11건)가 닫혔다.
남은 ③(b)를 앞 스펙은 이렇게 규정했다.

> **③ (b) 새 재료가 필요한 4건.**
> - 루프 내 변수 재설정(🔴 `PROC_ETC:69`) — 제어 흐름 안의 대입을 담는 재료가 새로 필요하다.
> - 함수→함수 사각지대(🔴 `UF_GET_COLLECTYMD`의 간격 0 특례 외) — `ReferencedFunctionCallFact`가
>   "요약을 정확하게 만드는 대신 요약 자체를 없앤다"는 결정으로 링크만 걸기로 한 결과다.
>   **그 계약을 다시 여는 판단이 선행해야 한다.**

**그 규정이 실측과 다르다.** 계약을 다시 열 필요가 없고, 새 표도 필요 없다.
다섯 건이 기존 표 넷의 방문 범위와 실행 의미 표의 종류 둘로 전부 닫힌다.

## 2. 확정된 원인

### 2.1 함수→함수 사각지대의 진짜 뿌리는 링크 계약이 아니라 방문 범위다

`UF_GET_COLLECTYMD`가 `UF_GET_WORKDAY2`를 부르는 자리는 두 곳이고(53·78행),
**둘 다 변수 대입 SELECT의 SELECT 목록 안 `CASE` 식**이다.

```sql
-- object_definition.sql:48-53
,@v_strCollectYMD      =
 CASE WHEN CollectType = 2 THEN                       --유동_일
           CASE WHEN HolidayProcFlag = 2 THEN
                CONVERT(VARCHAR(8), DATEADD(D, CollectDay, @pi_strYMD), 112)
           ELSE
                dbo.UF_GET_WORKDAY2(@pi_strYMD, CollectDay)
```

`ReferencedFunctionVisitor`는 `Visit(UpdateSpecification)`·`Visit(DeleteSpecification)`·
`Visit(InsertSpecification)` 셋만 오버라이드한다(`DmlScopeExtractor.cs:1673·1676·1679`).
독립 `SelectStatement`는 방문 대상이 아니므로 이 호출이 수집되지 않고,
**참조 함수 표가 아예 생기지 않는다** — `COLLECTYMD`의 기계 확정 표는 객체 선언 · 파생 테이블 정의 ·
실행 의미 · CASE 분기 넷뿐이다.

배선은 이미 있다. `AiService.cs:1700`이 함수 갈래에도 참조 함수 표를 붙이되
`functionCallsForFunctionDef.Count > 0` 조건이라, 수집이 0건이면 표가 통째로 빠진다.
잠금 힌트 표가 `UIF_SettleYMD`에서 겪은 것과 같은 모양이고, ③(a) Task 1이 그것을 방문 범위로 닫았다.

**표가 없으니 링크도 없고, 링크가 없으니 모델이 산문으로 요약한다.**
`COLLECTYMD` Spec.md:108이 그 산문이다.

> `dbo.UF_GET_WORKDAY2`는 기준일과 간격을 받아 `THoliday.HYMD` 존재 여부를 반복 조회하고,
> **휴일을 만나면 간격을 연장하여** `CHAR(8)` 날짜를 반환합니다.

간격 0 특례가 빠졌다. 그런데 **`UF_GET_WORKDAY2` 자신의 명세서에는 그 사실이 정확히 있다** —
67행("입력 간격이 `0`이면 `@v_intIdx`를 `-1`로 설정한 후 반복을 시작합니다"), 98행, mermaid까지.
원문도 그렇다(`UF_GET_WORKDAY2` object_definition.sql:26-28).

즉 **링크만 걸렸으면 결함이 없었다.** "요약을 없애고 링크만 건다"는 계약은 옳고 잘 작동하며,
SP 명세서에서는 실제로 그렇게 동작한다. 함수 명세서만 그 보호를 못 받고 있었다.
계약을 다시 열 이유가 없다.

### 2.2 집합 술어도 같은 자리에서 새고, 실물이 있다

`SetPredicateVisitor`도 DML 셋만 방문한다(`:1537·1540·1543`). 그래서 독립 SELECT의 최상위 WHERE가
표에 오지 않는다. 실물:

```sql
-- UF_GET_COLLECTYMD object_definition.sql:99-101
FROM   TPGCollectPeriodMst WITH(NOLOCK)
WHERE  CollectFlag     = 1          --회수구분(1:자동회수, 7:수납회수, 8:미회수, 9:미지정)
AND    CollectPeriodID = @pi_intCollectPeriodID
```

`CollectFlag = 1`은 리터럴 우변 등치로 집합 술어 표가 **담을 수 있는 형태**인데 담기지 않는다.
"자동회수 행만 조회한다"는 사실이 어떤 기계 확정 표에도 없고 산문에만 있다.

③(a)의 Global Constraints는 이 둘을 "그 부류의 결함이 실측되지 않았다"는 이유로 동결했다.
그 판단은 그때 옳았고, 이제 조건이 바뀌었다 — 참조 함수는 🔴 포함 3건, 집합 술어는 위 실물.

### 2.3 계획 밖 결함도 같은 뿌리다

앞 브랜치가 실행 중에 발견하고 재리뷰가 실측으로 확정했다.

```sql
-- UF_Get_CLComm4MobileCo object_definition.sql:31-32
ELSE (SELECT CommissionRate
      FROM   TClientCMRate WITH(NOLOCK)     -- 이 NOLOCK이 표에 오지 않는다
```

**SELECT 목록** 안 스칼라 하위 질의다. 같은 문장의 37행 `FROM TClientSettleRate4MobileCo WITH(NOLOCK)`은
`SELECT 1 · 최상위`로 실리므로, **표가 그 문장에 대해 채워진 것처럼 보이는데 두 스캔 중 하나가 빠진다** —
없는 것보다 나쁜 모양이다.

경계는 "FROM 절 바깥에서 열리는 하위 질의" 하나이고 세 모양이 있다.

| 모양 | 코퍼스 실물 | 표가 잃는 힌트 |
|---|---|---|
| 문장의 SELECT 목록 | 2건 | **1건** (`UF_Get_CLComm4MobileCo:32`) |
| DML의 `SET` 절 | 6건 | 0건 (원천이 전부 TVF 호출이라 힌트를 지지 않는다) |
| 독립 `SELECT n`의 WHERE | 0건 | 0건 |

앞 브랜치가 셋을 한 조각으로 닫으라고 남겼다 — 하나만 고치면 새 비대칭이 생긴다.

### 2.4 🔴 루프 내 변수 재설정은 두 사실의 조합이다

```sql
-- UP_UTIL_SETTLE_PROC_ETC object_definition.sql:67-79
WHILE (@@FETCH_STATUS = 0) BEGIN
    SET @v_intID = 0                       -- (1) 커서 행마다 재설정
    SELECT @v_intID  = ID                  -- (2) 비집계 대입
    FROM   TSettleMiss WITH(NOLOCK)
    WHERE  ...
    IF @@ROWCOUNT > 1 BEGIN
        SELECT @v_intID  = MAX(ID)         -- (3) 집계 대입 — 이미 표에 있다
```

실행 의미 표는 (3)만 담는다.

```
| 집계 대입 | 79 | SELECT @v_intID = MAX(...) | 집계 SELECT는 무결과여도 한 행을 돌려주므로
  대입이 항상 일어납니다. 무결과 시 NULL이 대입됩니다 — DECLARE의 초기값은 유지되지 않습니다. |
```

(1)과 (2)가 없다. 둘 중 하나만 알면 위험이 보이지 않는다 —
(2)만 알면 원본이 실제로 재설정한다는 사실이 사라져 이행자가 그것을 빠뜨리고,
(1)만 알면 왜 재설정이 필요한지가 사라진다. 감사가 지적한 것도 (1)이다:
"로직 흐름 4단계에 루프 내 0 재설정 없음. 지역 변수 표의 '초기값 0'은 DECLARE 시점 값."

**노이즈 실측:** `WHILE`을 가진 객체는 코퍼스 24개 중 **4개**, 변수 대입 SELECT는 객체당 1~5개다.
행 증가가 작다.

## 3. 결정 사항

1. **링크 계약을 다시 열지 않는다.** §2.1이 실측으로 보였듯 계약은 옳고 작동한다.
   함수 명세서가 그 보호를 받지 못한 것이 결함이고, 방문 범위로 닫는다.
2. **네 표의 문장 집합을 통일한다.** `SetPredicateVisitor`와 `ReferencedFunctionVisitor`에
   ③(a)가 나머지 둘에 한 것과 같은 규칙을 더한다. 셋만 넓히고 하나를 두면 새 비대칭이 된다 —
   앞 브랜치가 같은 논리로 배운 것이다.
3. **판정은 이미 공유되고 있다.** `HasFromClause`가 파일 수준 `private static`이므로
   새로 만들지 않고 그대로 부른다. 네 방문자가 같은 판정을 쓴다.
4. **FROM 절 바깥 하위 질의 세 모양을 한꺼번에 닫는다.** 실물 손실은 SELECT 목록 1건뿐이지만
   하나만 고치면 비대칭이 이동할 뿐이다.
5. **새 표를 만들지 않는다.** 실행 의미 표에 종류 둘을 더한다 —
   `MachineConfirmedTables.All`도 Critic 면제 블록도 프롬프트 접두사 캐시 순서도 바뀌지 않는다.
6. **캐시 버전을 12로 올린다.** 프롬프트에 실리는 표가 바뀐다.

## 4. 작업 항목

### A. `ReferencedFunctionVisitor`가 독립 SELECT와 `IF` 술어를 방문한다

`Visit` 셋을 `ExplicitVisit`으로 바꾸고 `SelectStatement`·`IfStatement`를 더한다.
이 방문자는 `Collect`에서 `statement.Accept(calls)`로 **문장 전체를 훑으므로**,
방문 대상만 넓히면 SELECT 목록 안 `CASE` 식의 호출이 자동으로 잡힌다.
`COLLECTYMD:53·78`이 그 실물이다.

### B. `SetPredicateVisitor`가 같은 문장 집합을 방문한다

같은 규칙. 독립 SELECT의 최상위 WHERE와 파생 테이블 WHERE가 표에 온다.
`COLLECTYMD:100`의 `CollectFlag = 1`이 그 실물이다.

### C. 잠금 힌트가 FROM 절 바깥 하위 질의 세 모양을 담는다

`LockHintVisitor`는 문장 집합이 이미 넓다. `CollectWhereSubqueries`가 WHERE만 훑는 것을 넓혀
SELECT 목록과 `SET` 절의 하위 질의도 같은 `하위 질의` 범위로 싣는다.
`SubqueryCollector`가 이미 깊이를 세며 `ScalarSubquery`를 모으므로 그 수집기를 재사용한다.

### D. 실행 의미 표에 `루프 내 재설정`과 `비집계 대입`을 더한다

- **루프 내 재설정** — `WhileStatement` 본문에서 대입되는 변수. 확정 사실은
  "반복마다 초기화되므로 `DECLARE`의 초기값과 다르다".
- **비집계 대입** — 집계 함수 없는 `SELECT @v = 컬럼`. 확정 사실은
  "무결과 시 대입이 일어나지 않아 **직전 값이 남는다**".

이미 있는 `집계 대입`과 정반대 동작이라 표에 나란히 놓이면 대비가 선명하다.
`PROC_ETC`는 71행이 비집계, 79행이 집계로 둘 다 실린다.

### E. 렌더러·L1·캐시

표 셋의 행이 늘고 종류가 둘 는다. 렌더러는 기존 헬퍼가 그대로 처리한다(열이 늘지 않는다).
L1은 `CheckSetPredicates`·`CheckExecutionSemantics`가 행 단위 대조이므로 새 행이 흘러간다 —
**확인만 하고 필요할 때만 고친다.** 캐시 버전 12.

### F. 재생성으로 확인한다

`COLLECTYMD` · `WORKDAY2` · `UF_Get_CLComm4MobileCo` · `PROC_ETC` · `UIF_SettleYMD`.

## 5. 검증

| 앵커 | 무엇을 확인하는가 |
|---|---|
| `COLLECTYMD:53·78` | 참조 함수 표가 생기고 `UF_GET_WORKDAY2` 링크가 걸린다 |
| `COLLECTYMD` Spec.md 산문 | 피호출 함수 동작 요약이 **사라진다**(프롬프트 규칙이 발동한다) |
| `COLLECTYMD:100` | `CollectFlag = 1`이 집합 술어 표에 실린다 |
| `UF_Get_CLComm4MobileCo:32` | `TClientCMRate WITH(NOLOCK)`이 `하위 질의` 범위로 실린다 |
| `PROC_ETC:69` | `루프 내 재설정` 행 |
| `PROC_ETC:71` | `비집계 대입` 행 — 79행 `집계 대입`과 나란히 |
| 기존 DML 행 | 문장 번호가 변경 전과 같다(채번 회귀) |
| 네 표 합의 | 같은 DDL에서 네 표의 `SELECT n`이 같은 문장을 가리킨다 |

## 6. 범위 밖

**④ 산문이 기계 확정 표를 뒤집음(3건, 🔴1 포함).** `UF_GET_COMM4CLIENT`의 mermaid가
실행 의미 표를 뒤집은 것이 그 🔴이다. 표는 옳고 산문이 틀린 부류라 이 설계와 메커니즘이 다르다.

**⑤ 감사 기준과 도구 정책의 불일치(5건).** 다른 세션이 이미 착수했다
(`main`의 "감사 계약에 '주석은 전수가 아니다'를 명시" 커밋).

**`Add`의 중복 제거 키에 `Hints`가 없다.** 같은 줄·같은 별칭의 힌트 갈린 스캔 둘 중 하나가 사라진다.
코퍼스 실물 0건이라 앞 브랜치가 후속으로 남겼다. C가 하위 질의 수집을 넓히면 노출면이 커지므로
**C를 하면서 이 키를 함께 볼 가치가 있으나, 실물이 나오지 않으면 문서화로 닫는다.**

**`—` 마커가 네 곳에 선언돼 있다.** 앞 브랜치의 후속 항목. 계약이 걸린 두 곳은 왕복 테스트가 못박는다.

## 7. 한계

- **행 증가폭을 재지 않았다.** 집합 술어 표는 ③(a)에서 코퍼스 전체 200 → 495행이 됐다.
  독립 SELECT의 WHERE가 더해지면 또 는다. 함수 명세서에는 집합 술어 표가 새로 생긴다.
  증가폭은 재생성 후 측정 대상이다.
- **`IF` 술어 안 함수 호출의 실물을 확인하지 않았다.** A가 `IfStatement`를 방문 대상에 넣지만
  코퍼스에 그 형태가 있는지 세지 않았다. 없더라도 네 표 통일이라는 결정 자체는 성립한다.
- **L1이 손댈 곳이 있는지 확정하지 않았다.** E는 "확인만 하고 필요할 때만 고친다"로 두었다.
  ③(a)의 `CheckLockHints`가 그랬듯 행 단위 대조라 그대로 흘러갈 가능성이 높으나,
  실제로 그런지는 테스트로 확인해야 한다.
