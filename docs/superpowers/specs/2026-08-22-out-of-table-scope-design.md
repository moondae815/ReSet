# 축 A 재감사 ③ — 기존 표의 관할 밖(범위 확대분) 설계

> 2026-08-22 · 대상: `output/Jobs/POQSettlePrco20/consistency/ConsistencyReport.md`(축 A 재감사)의
> 결함 중 **③ 기존 표의 관할 밖**으로 분류된 약 15건. 그중 **기존 표의 범위를 넓히면 닫히는
> 11건(🟠 5 · 🟡 6)만** 이 스펙이 다룬다. 새 재료가 필요한 4건은 범위 밖이다(§8).

## 1. 배경

`2026-08-22-audit-defect-closure-design.md`가 결함 34건을 닫는 메커니즘으로 다섯 덩어리로
갈랐고 ①②만 다뤘다. 그 스펙의 §7은 ③을 "표 하나를 늘릴 때마다 추출기 · 헤딩 상수 ·
카탈로그 등록 · L1 검사 · 프롬프트 다섯 갈래 · 테스트 두 벌 · 캐시 버전 인상 · 전체
재생성이 따라온다"는 이유로 미뤘다.

그 비용 추정은 ③ 전체에는 맞지만 **일부에는 맞지 않는다**. ③ 15건을 닫는 메커니즘으로 다시
가르면 둘이다.

| | 부류 | 건수 | 드는 것 |
|---|---|---|---|
| (a) | 기존 표의 **범위**를 넓히면 닫힘 | 11 (🟠5·🟡6) | 추출기 · 렌더러 · 기존 L1 확장 · 테스트 · 캐시 |
| (b) | **새 재료**가 필요함 | 4 (🔴2 포함) | 위 전부 + 헤딩 상수 · 카탈로그 등록 · 프롬프트 다섯 갈래 |

**이 스펙은 (a)만 다룬다.** 표가 하나도 늘지 않으므로 §7이 세어 둔 비용 중 헤딩 상수 ·
카탈로그 등록 · 프롬프트 갈래 배선이 붙지 않는다. 대상 결함은 다음 11건이다.

- **잠금 힌트 5건(🟡)** — `UIF_SettleYMD` 107·133·148, `UP_UTIL_SETTLE_COMM_UPD` 145,
  `UP_UTIL_SETTLE_EXCEPTION_PROC` 529, `UP_UTIL_SETTLE_INS_EXTRA` 22·31,
  `UP_Util_PG_Client_CMRate_Ins` 21.
- **집합 술어 5건(🟠)** — `COMM_UPD` 78·341, `EXCEPTION_PROC` 320·442 및
  220·239·280·302·375(다섯 문장 한 건).
- **커서 원천 정렬 1건(🟡)** — `UP_UTIL_SETTLE_PROC_ETC` 62.

## 2. 확정된 원인

설계 전에 다섯 가지를 실측으로 못 박았다. 추정으로 남긴 것은 없다.

### 2.1 네 방문자가 전부 DML 세 종류만 방문한다

`DmlScopeVisitor` · `LockHintVisitor` · `SetPredicateVisitor` · `ReferencedFunctionVisitor`가
모두 `Visit(InsertSpecification)` · `Visit(UpdateSpecification)` · `Visit(DeleteSpecification)`
셋만 오버라이드한다(`DmlScopeExtractor.cs`의 424 · 616 · 924 · 1044행).
그래서 DML 밖의 스캔은 네 표 **어디에도** 자리가 없다. 실측한 형태는 넷이다.

| 형태 | 실물 | DML 문장 안인가 |
|---|---|---|
| WHERE 하위 질의 | `COMM_UPD:145`, `EXCEPTION_PROC:529` | **예** — 이미 문장 번호가 있다 |
| 변수 대입 SELECT | `INS_EXTRA:22` (`SELECT @v_strReqYMD = MIN(ReqYMD) FROM … WITH(NOLOCK)`) | 아니오 |
| 제어 흐름 술어 | `INS_EXTRA:31` (`IF EXISTS(SELECT PLTID FROM TSettleMst WITH(NOLOCK) …)`) | 아니오 |
| 커서 원천 · 함수 본문 SELECT | `PROC_ETC:62`, `UIF_SettleYMD:107` | 아니오 |

앞의 하나는 문장 번호가 이미 있고 나머지 셋은 없다. 이 차이가 §3의 결정 1을 가른다.

### 2.2 `A.YMD = A.AYMD`는 두 그물 사이로 샜다

`TopLevelPredicateCollector`의 문서 주석(`DmlScopeExtractor.cs:1128`)이 이렇게 적는다.

> 같은 별칭 안의 비교(`A.YMD = A.AYMD`)나 한정자를 알 수 없는 비교(`TID = CID`,
> `A.TID = CID`)는 조인이라고 주장할 근거가 없다(리뷰 라운드 2 실측: EXCEPTION_PROC
> 210/228/271/290행, COMM_UPD 58행, EXPECT_PROC 48행이 모두 이 오탐이었다).

조인 키에서 빼는 판단 자체는 옳다 — 그 여섯 자리는 실제로 조인이 아니다. 그런데 같은
술어가 집합 술어 표에서도 빠진다. 우변이 리터럴이 아니기 때문이다. **조인 키 그물에서
의도적으로 제외되고 집합 술어 그물에서 구조적으로 제외되어, 두 그물 사이로 샜다.**
🟠 결함 둘(`COMM_UPD:78`, `EXCEPTION_PROC:220 등 다섯 문장`)의 기전이 정확히 이것이다.

### 2.3 수집 연산자는 셋뿐이고 우변은 리터럴만 받는다

`UP_UTIL_SETTLE_EXCEPTION_PROC`의 집합 술어 표 41행을 전수로 셌다.

| 연산자 | 행 수 |
|---|---|
| `=` | 23 |
| `<>` | 7 |
| `IN` | 10 |

`>=` · `>` · `<=` · `<` · `!=`는 **0행**이다. `AND A.AYMD >= '20230101'`(UPDATE 12,
🟠)과 `AND TxAmt != CardAmt+CouponAmt+MoneyAmt+PointAmt`(UPDATE 16, 🟠)가 표에 없는
이유가 이것이다.

반면 **좌변은 이미 임의 식을 담는다** — 같은 표에 `ISNULL(A.ExtraSettleFlag,0)`과
`dbo.UF_GET_CLIENTSECTIONRATE(A.CLIENTID,A.PGNAME,A.MALLID,(A.TXAMT-ISNULL(A.NonSettleAmt,0)))`가
컬럼 칸에 그대로 실려 있다. 제한은 우변에만 있다.

### 2.4 OR 결합은 누락이 아니라 오독을 만든다

`EXCEPTION_PROC:210`의 원문은 `(A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))`인데
표에는 이렇게 실린다.

```
| UPDATE 7 | 210 | A.UseState | <> | 최상위 | 1 | 1 |
| UPDATE 7 | 210 | A.UseState | =  | 최상위 | 1 | 1 |
```

두 행이 나란히 있으므로 AND로 읽히고, **그렇게 읽으면 모순(공집합)이다.** 감사 보고서는
이 자리를 "술어 형태로 서술 없음"으로 적었으나 실제로는 그보다 나쁘다 — 빠진 것이
아니라 틀리게 실렸다. `A.YMD = A.AYMD` 항이 §2.2의 이유로 함께 사라져 OR의 오른쪽 가지가
통째로 없어진 결과다.

`CheckSetPredicates`의 주석(`MechanicalValidator.cs:2802`)은 이미 이 위험을 알고 있다 —
"`ExtractSetPredicates`는 그 경우를 합치지 않고 사실을 둘 낸다(**AND/OR 의미를 날조하지
않기 위해서**)". 사실을 둘 내는 판단은 옳고, 문제는 **그 둘이 어떤 관계인지를 표가 말할
자리가 없다**는 것이다.

### 2.5 함수 갈래는 배선이 아니라 조건 때문에 표가 빠진다

`UIF_SettleYMD`의 명세서에는 잠금 힌트 표가 없다. 배선이 없어서가 아니다 —
`AiService.cs:1561`이 `lockHintsForFunctionDef.Count > 0`일 때만 표를 붙이고, 이 함수는
DML이 0건이라 `ExtractLockHints`가 빈 목록을 낸다. 본문의 `WITH(NOLOCK)` 셋(107·133·148)은
`SELECT` 문에 붙어 있어 방문 대상이 아니다.

`BuildLockHintTableLines`의 주석이 "세 배선 경로(SP 최초 생성 · 함수 명세서 · CrudAnalysis
분기)가 이 헬퍼를 공유해야 같은 표가 나간다는 것이 코드로 보장된다"고 적는다.
**방문 범위만 넓히면 이 함수의 표는 프롬프트 쪽 작업 없이 저절로 생긴다.**

## 3. 결정 사항

1. **문장 채번을 넓히되 DML 채번은 건드리지 않는다.** `SELECT n` · `IF n` 종류를 더한다.
   네 방문자는 지금도 서로를 참조하지 않고 같은 트리를 같은 순서로 훑어 같은 번호를 내는
   계약이므로(`ReferencedFunctionVisitor` 주석), 같은 규칙을 넷에 똑같이 더하면 계약이
   유지된다. 기존 `UPDATE 7`은 이후에도 `UPDATE 7`이다.
2. **WHERE 하위 질의는 새 채번이 아니라 범위 칸으로 담는다.** 이미 그 DML 문장의 일부이므로
   문장을 새로 세면 같은 UPDATE가 두 번호로 나타난다. 잠금 힌트 표의 범위 칸(지금
   `최상위` · `파생`)에 `하위 질의`를 더한다.
3. **술어는 분해할 수 있으면 분해해서도 싣고, 못 하면 원문만 싣는다.** 어느 경우에도 항
   자체는 빠지지 않는다. 연산자 화이트리스트를 넓히는 방식과 달리 앞으로 나올 미지의 술어
   형태에도 샘이 없다. §2.4가 인용한 "AND/OR 의미를 날조하지 않는다"는 기존 결정과 같은
   방향이다 — 구조를 추론하지 않고 원문을 그대로 넘긴다.
4. **새 표를 만들지 않는다.** 따라서 `MachineConfirmedTables.All`의 등록도, Critic 면제
   블록도, 그 목록 순서에 의존하는 프롬프트 접두사 캐시도 바뀌지 않는다.
5. **새 L1 검사를 만들지 않는다.** `CheckSetPredicates` · `CheckLockHints`가 이미 있고
   행 단위 대조를 한다. 넓힌 표를 그 검사가 보게 한다.
6. **캐시 버전을 11로 올린다.** ①②가 10으로 올리므로 그다음이다. 프롬프트에 실리는 표가
   바뀌므로 옛 엔트리를 재사용하면 새 검사가 캐시 히트에서 발동하지 않는다.

## 4. 작업 항목

### A. 문장 채번에 `SELECT` · `IF` 종류를 더한다

`DmlScopeExtractor`의 네 방문자에 같은 규칙을 더한다.

- 독립 `SelectStatement`(DML 문장 안에 있지 않은 것) → `SELECT n`. 변수 대입 SELECT와
  `DECLARE CURSOR`의 원천 질의가 여기 들어간다.
- `IfStatement`의 술어 안에 있는 질의 → `IF n`.

번호는 종류별로 1부터이며 기존 `NextOrdinal`의 사전을 그대로 쓴다. 네 방문자가 같은 판정
함수를 공유해야 번호가 갈리지 않으므로, 판정은 `DmlScopeExtractor`의 정적 헬퍼 하나로 둔다.

### B. `LockHintVisitor`가 새 문장 종류와 하위 질의를 방문한다

- A가 센 `SELECT n` · `IF n`의 `FROM` 절을 기존 `CollectFrom`으로 훑는다.
- DML 문장의 WHERE 절 안 하위 질의는 그 문장의 번호를 그대로 쓰고 범위를 `하위 질의`로
  단다. `FromTableCollector`가 지금 `FROM` 절만 보므로 WHERE 절의 `ScalarSubquery` ·
  `ExistsPredicate` 안쪽을 별도로 훑는 경로가 필요하다.

### C. 집합 술어 표에 「술어 원문」 열을 더한다

`SetPredicateFact`에 `PredicateText`를 더하고 행 단위를 원소에서 **최상위 AND 항**으로 올린다.

- 원문은 `CollapseWhitespace(TextOf(항))`로 얻는다. 두 헬퍼 모두 이미
  `DmlScopeExtractor` 안에 있다(`869`행 근처).
- `TopLevelPredicateCollector`가 지금 분해하는 형태(`=` · `<>` · `IN` · `NOT IN`)는 컬럼 ·
  연산 · 원소 칸을 지금처럼 채우고 원문 칸을 함께 채운다.
- 분해하지 못하는 항(OR 결합, 컬럼 대 컬럼, 산술식 우변, 미수집 연산자)은 컬럼 · 연산 ·
  원소 칸을 `—`로 두고 원문 칸만 채운다.
- `Line`을 문장 시작줄에서 **그 항 자신의 줄**로 내린다. 지금은 `statement.StartLine`이라
  `UPDATE 7`의 세 행이 모두 210이다.

### D. `DmlScopeVisitor`가 새 문장 종류를 방문한다

A가 센 `SELECT n` · `IF n`을 DML 범위 표에 싣는다. 대상 · 기준일 파라미터 적용 칸은
갱신 대상이 없으므로 `—`로 낸다. `ORDER BY` · `GROUP BY` 칸은 이미 있으므로
`PROC_ETC:62`의 `ORDER BY A.OutYMD, A.ClientID`와 `GROUP BY A.ClientID, A.YMD, A.OutYMD`가
그대로 채워진다.

### E. L1 두 검사를 넓힌다

- `CheckSetPredicates` — 행 키를 `(Operation, Line, Column)`에서
  `(Operation, Line, PredicateText)`로 올린다. 주석이 적어 둔 키 비유일 문제
  (`A.X IN (1) AND A.X IN (2)`)가 이 변경으로 함께 해소된다. 원문 칸 대조를 더하고,
  분해 불가 행은 리터럴 칸이 `—`인지 본다. 칸이 하나 늘므로
  `ExtractSetPredicateLiteralCell`의 칸 인덱스를 맞춘다.
- `CheckLockHints` — 이미 행 단위 대조라 새 행이 그대로 흘러간다. 범위 칸에 `하위 질의`가
  들어와도 문자열 대조이므로 변경이 없을 것으로 보이나, 테스트로 확인한다.

### F. 캐시 버전 11과 문서 반영

`CacheManager.CurrentCacheFormatVersion`을 11로 올리고 기존 양식대로 주석을 더한다.
`docs/architecture.md`와 `AGENTS.md`의 표 설명을 동기화한다.

### G. 재생성으로 실제로 닫혔는지 확인한다

11건의 앵커를 그대로 재대조한다. 특히 `UIF_SettleYMD`에 잠금 힌트 표가 **생겼는지**,
`EXCEPTION_PROC:210` 행이 두 행에서 한 행(원문 통째)으로 **바뀌었는지**를 본다.

## 5. 표의 최종 모양

**집합 술어** — 열이 하나 늘고 행 단위가 올라간다.

```
| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |
| UPDATE 4 | 130 | A.PGNAME | IN | 최상위 | 5 | 'KFTC', … | A.PGNAME IN ('KFTC', …) |
| UPDATE 7 | 210 | — | — | 최상위 | — | — | (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD)) |
| UPDATE 12 | 320 | — | — | 최상위 | — | — | A.AYMD >= '20230101' |
```

**잠금 힌트** — 열은 그대로, 행 종류와 범위 값이 는다.

```
| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |
| UPDATE 18 | 529 | TSettleMst | - | 하위 질의 | NOLOCK |
| SELECT 1 | 22 | PaymentDB.dbo.TExtraSettleIn | - | 최상위 | NOLOCK |
| IF 1 | 31 | TSettleMst | - | 최상위 | NOLOCK |
```

**DML 범위** — 행 종류가 는다.

```
| 문장 | 라인 | 대상 | … | GROUP BY | ORDER BY |
| SELECT 1 | 62 | — | … | A.ClientID, A.YMD, A.OutYMD | A.OutYMD, A.ClientID |
```

## 6. 검증

추출기 단위 테스트와 L1 테스트 두 벌을 세운다. 앵커는 이 설계에서 실측한 자리를 그대로 쓴다.

| 앵커 | 무엇을 확인하는가 |
|---|---|
| `EXCEPTION_PROC:210` | OR 결합 항이 한 행으로, 원문 통째로 실린다 |
| `EXCEPTION_PROC:320` | `>=` 항이 실린다(지금은 0행) |
| `EXCEPTION_PROC:442` | `!=`와 산술식 우변이 실린다 |
| `EXCEPTION_PROC:529` | 하위 질의 스캔이 `UPDATE 12` 행에 범위 `하위 질의`로 붙는다 |
| `INS_EXTRA:22` | 변수 대입 SELECT가 `SELECT n`으로 채번된다 |
| `INS_EXTRA:31` | `IF EXISTS` 술어 안 스캔이 `IF n`으로 채번된다 |
| `PROC_ETC:62` | 커서 원천의 `ORDER BY` · `GROUP BY`가 DML 범위 표에 실린다 |
| `UIF_SettleYMD:107` | DML 0건 함수에 잠금 힌트 표가 생긴다 |
| `UIF_SettleYMD:129·144` | 힌트 없는 `MASTER..SPT_VALUES` 스캔이 `(없음)`으로 실려 NOLOCK 셋과 갈린다 |
| `COMM_UPD:145` | 최상위 WHERE 하위 질의 스캔이 그 UPDATE 행에 붙는다 |
| `PG_Client_CMRate_Ins:21` | 제어 흐름 술어 스캔이 `IF n`으로 채번된다 |
| 기존 DML 행 | 문장 번호가 변경 전과 같다(채번 회귀) |

## 7. 순서 제약

①②의 Task 3~6이 `MechanicalValidator.cs` · `SpecExpectations.cs` · `CacheManager.cs`를
고치고 있고 이 설계의 E · F가 같은 세 파일을 건드린다. `ErrorType` enum과 `Validate`
호출부는 양쪽이 줄을 더하는 자리라 텍스트 충돌이 확실하다.

**구현은 ①②가 병합된 뒤 시작한다.** 이 설계 문서와 뒤따르는 구현 계획의 커밋은 새
파일이므로 지금 해도 충돌하지 않는다.

## 8. 이 설계의 범위 밖

**③ (b) 새 재료가 필요한 4건.**

- 루프 내 변수 재설정(🔴 `PROC_ETC:69`) — `WHILE` 본문 첫 문장 `SET @v_intID = 0`이
  어떤 표에도 자리가 없다. 제어 흐름 안의 대입을 담는 재료가 새로 필요하다.
- 함수→함수 사각지대(🔴 `UF_GET_COLLECTYMD`의 간격 0 특례 외) —
  `ReferencedFunctionCallFact`가 "요약을 정확하게 만드는 대신 요약 자체를 없앤다"는
  결정으로 링크만 걸기로 한 결과다. 그 계약을 다시 여는 판단이 선행해야 한다.

**하위 질의 안의 술어.** 이 설계는 잠금 힌트만 하위 질의로 넓힌다. 집합 술어는 여전히
최상위와 파생 테이블만 본다 — `TopLevelPredicateCollector`가 "EXISTS(… B.YMD =
@pi_strYMD …)는 대상 범위를 좁히지 않는다"는 근거로 세운 경계이고, 이번 감사에서 이
경계 때문에 샌 결함은 실측되지 않았다.

**`MERGE`.** 실측 SP 24건에 없다(전수 grep 확인, `DmlScopeExtractor` 주석).

**④와 ⑤.** 각각 별도 사이클이다.

## 9. 한계

- **표가 얼마나 커지는지 재지 않았다.** `PROC_ETC`의 줄머리 `SELECT`가 6개이고
  `EXCEPTION_PROC`은 542줄에 `UPDATE` 18 · `WHERE` 23 · 줄머리 `AND` 86이다. 행 증가폭과
  토큰 증가폭은 재생성 후 측정 대상이다.
- **`IF` 술어 안의 질의를 어디까지 셀지는 구현 시점에 확정한다.** `IF EXISTS(…)`는 명확하나
  `IF (SELECT COUNT(*) …) > 0` 같은 스칼라 서브쿼리 형태가 실측 코퍼스에 있는지 세지
  않았다. 없으면 `ExistsPredicate`만으로 좁힌다.
- **원문 칸의 이스케이프.** 술어 원문에 `|`가 들어가면 표가 무너진다.
  `EscapeTableCell`이 이미 그 자리를 맡고 있으나, 대괄호 식별자(`A.[C|D]`)를 두고
  `CheckSetPredicates` 주석이 기록한 실패 모양이 있으므로 테스트로 확인한다.
