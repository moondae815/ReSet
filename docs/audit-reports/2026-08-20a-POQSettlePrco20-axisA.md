# POQSettlePrco20 산출물 정합성 감사 (축 A 재감사)

감사일: 2026-08-20 · 대상 산출물 작성일: 2026-08-20 11:49 ~ 12:02

이전 감사(2026-08-19)는 `ConsistencyReport-20260819.md`에 보관했다.

## 1. 판정

| 축 | 판정 | 단위 수 | 신규 검증 | 캐시 재사용 | 검증 불가 |
|---|---|---|---|---|---|
| A (원본 DDL ↔ Spec.md) | **결함** (🔴 7 · 🟠 4 · 🟡 59 · ⚪ 43) | 31 + 교차 10 | 41 | 0 | 0 |
| B (Spec.md ↔ 단계 지시서) | **미실행** | 17 | 0 | 0 | — |

축 A만 실행했다. 축 B는 이 보고서가 다루지 않는다(6절).

**대상이 14개에서 31개로 넓어졌다.** 스킬을 고쳐 축 A 대상을 소비 명세서 집합이 아니라
그 집합의 **참조 폐포**로 잡았다(2절). 이전 감사가 보지 않던 함수 17개(로컬 10 · 외부 DB 7)가
이번에 처음 검증됐다.

| 부류 | 단위 | 🔴 | 🟠 | 🟡 | ⚪ | 정합 |
|---|---|---|---|---|---|---|
| SP | 14 | 0 | 4 | 15 | 13 | 4 |
| **함수 (신규)** | **17** | **2** | 0 | **34** | **25** | 1 |
| **교차 대조 (신규)** | **10** | **5** | 0 | **10** | **5** | 2 |
| 합계 | 41 | 7 | 4 | 59 | 43 | 7 |

캐시 재사용이 0인 이유: 명세서 14개가 2026-08-20에 전부 재생성되어 축 A 키의 `Spec.md`·
`metadata.json` 해시가 모두 바뀌었다. **원본 DDL 14개의 해시는 이전 감사와 동일**하므로,
이번에 달라진 것은 산출물뿐이고 기준값은 그대로다.

### 이전 감사와의 차이

| | 2026-08-19 | 2026-08-20 |
|---|---|---|
| 🟠 | 4 | 4 |
| 🟡 | 12 | 15 |
| ⚪ | 10 | 13 |
| 정합 판정 단위 | 4 | 4 |

**개수는 비슷하나 🟠의 내용은 완전히 교체됐다.** 이전 🟠 4건은 전부 "원본 필터가 명세서에서
사라진다"는 한 부류였고 **4건 모두 해소**됐다. 이번 🟠 4건은 그 부류가 아니며(해당 형태 0건),
"기계 확정 표는 정확한데 산문이 원문을 좁히거나 넓힌다"는 다른 부류다(4-1 ②).

## 2. 검증 대상 확정

소비 명세서 12개는 `raw/prompt-context.md`의 `^Filename:` 행에서 읽었다(`Feedback_Log.txt` 제외).

```
UP_Util_PG_Client_CMRate_Ins, UP_UTIL_SETTLE_CANCEL_INS, UP_UTIL_SETTLE_COMM_UPD,
UP_UTIL_SETTLE_EXCEPTION_PROC, UP_UTIL_SETTLE_EXPECT_PROC, UP_UTIL_SETTLE_INS,
UP_UTIL_SETTLE_INS_EXTRA, UP_UTIL_SETTLE_INS_EXTRA4PLCARD, UP_UTIL_SETTLE_PROC_ETC,
UP_Util_Settle_Summary, UP_UTIL_SETTLE_SUMMARY_ETC, UP_UTIL_STAT_PGCOLLECT_INS
```

**참조 폐포**: 소비 12개 각각의 `raw/dependency-manifest.json`에서 `Nodes[]`를 읽어 합집합을
취했다. 경로는 매니페스트의 `SpecPath`·`DdlPath`를 그대로 썼다(기준점은 `raw/`가 아니라
그 부모인 객체 디렉터리다).

| 부류 | 개수 | 자리 |
|---|---|---|
| SP | 14 | `output/Procedures/` |
| 로컬 함수 | 10 | `output/Functions/` |
| 외부 DB 함수 | 7 | `output/External/SETTLE_CARD_DB/Functions/` |
| **합계** | **31** | |

44개 노드 전부 `Status`가 `Succeeded`이고, 조립한 93개 경로가 모두 실재한다.

폐포에만 있는 SP는 `dbo.UP_Util_Settle_Summary_AcqManual`과 `dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA`
둘이다(`UP_Util_Settle_Summary`의 221·230행 `EXEC`). 최상위 실행 순서에 포함되지 않는 하위
호출이므로 선택에서 빠진 것이 운영상 정상이고, 축 B 결함으로 보고하지 않는다.

## 3. 단위별 커버리지

| 단위 | 종류 | 판정 | 상태 | 근거 파일 |
|---|---|---|---|---|
| UP_UTIL_SETTLE_INS_EXTRA | SP | 정합 | 신규 | DDL + Spec.md + metadata.json |
| UP_UTIL_SETTLE_SUMMARY_EXTRA | SP | 정합 | 신규 | 〃 |
| UP_Util_Settle_Summary | SP | 정합 (⚪2) | 신규 | 〃 |
| UP_UTIL_SETTLE_SUMMARY_ETC | SP | 정합 (⚪3) | 신규 | 〃 |
| UP_UTIL_SETTLE_CANCEL_INS | SP | 결함 (🟡1) | 신규 | 〃 |
| UP_UTIL_SETTLE_COMM_UPD | SP | 결함 (🟡1) | 신규 | 〃 |
| UP_UTIL_STAT_PGCOLLECT_INS | SP | 결함 (🟡2) | 신규 | 〃 |
| UP_UTIL_SETTLE_INS | SP | 결함 (🟡3) | 신규 | 〃 |
| UP_Util_PG_Client_CMRate_Ins | SP | 결함 (🟡2 ⚪1) | 신규 | 〃 |
| UP_Util_Settle_Summary_AcqManual | SP | 결함 (🟡1 ⚪2) | 신규 | 〃 |
| UP_UTIL_SETTLE_EXCEPTION_PROC | SP | 결함 (🟡4 ⚪1) | 신규 | 〃 |
| **UP_UTIL_SETTLE_INS_EXTRA4PLCARD** | SP | **결함 (🟠1 ⚪1)** | 신규 | 〃 |
| **UP_UTIL_SETTLE_EXPECT_PROC** | SP | **결함 (🟠1 ⚪2)** | 신규 | 〃 |
| **UP_UTIL_SETTLE_PROC_ETC** | SP | **결함 (🟠2 🟡1 ⚪1)** | 신규 | 〃 |
| UF_GET_CLIENTSECTIONRATE | 함수 | 결함 (🟡2 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_COLLECTYMD | 함수 | 결함 (🟡4) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_INCVTAXRATE | 함수 | 결함 (🟡1) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_OUTYMD4REFUND | 함수 | 정합 (⚪2) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_PGCommOption | 함수 | 결함 (🟡1 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_ROUND4VAT | 함수 | 결함 (🟡2 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_SETTLE_EXCHANGERATE | 함수 | 결함 (🟡1 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_WORKDAY2 | 함수 | 결함 (🟡1 ⚪2) | 신규 | DDL + Spec.md + metadata.json |
| **UF_Get_CLComm4MobileCo** | 함수 | **결함 (🔴1 🟡4 ⚪1)** | 신규 | DDL + Spec.md + metadata.json |
| UIF_SettleYMD | 함수 | 결함 (🟡2 ⚪2) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_COMM4CLIENT | 외부함수 | 결함 (🟡1 ⚪2) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_COMM4CLIENT4INTEREST | 외부함수 | 결함 (🟡4 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_COMM4CLIENT4PARTIALCANCEL | 외부함수 | 결함 (🟡4 ⚪2) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_COMM4PG | 외부함수 | 결함 (🟡2 ⚪3) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_COMM4PG4INTEREST | 외부함수 | 결함 (🟡3 ⚪3) | 신규 | DDL + Spec.md + metadata.json |
| UF_GET_EXTRACOMM4CLIENT | 외부함수 | 결함 (🟡1 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| UF_Get_ExtraCardCommissionAmt | 외부함수 | 결함 (🟡1 ⚪3) | 신규 | DDL + Spec.md + metadata.json |

검증 불가 단위 없음(`Status`가 `Succeeded`가 아닌 노드 0개). 보류 항목은 6절에 정리했다.

## 4. 축 A 결함

### 🔴 결과 금액이 달라지는 결함 (1건)

| 등급 | 객체 | 원본 앵커 | 산출물 앵커 | 원본 | 산출물 | 영향 |
|---|---|---|---|---|---|---|
| 🔴 | `UF_Get_CLComm4MobileCo` (함수, 호출 SP 1) | `:44, 46` | `Spec.md:36, 39, 45, 203, 206, 213-217, 232` | `SET @po_mnyCLComm = @pi_mnyAmt * (@v_intCommRate/100.0)` 뒤 `RETURN CAST(@po_mnyCLComm AS INT)`. money→int 변환은 **절사**(0 방향 버림)이고 음수 금액이면 `-1.7 → -1`로 0 쪽으로 잘린다 | **어디에서도 절사라고 말하지 않는다.** `int로 변환`이라고만 쓰고, 반환 계약 절에 자릿수·절사 규칙 항목 자체가 없다 | 산문만 보고 재구현하면 `ROUND`를 쓰기 쉽고 그러면 수수료가 1원 단위로 달라진다. **정율(예: 2.5%)이라 소수부 발생이 정상 경로이므로 거의 모든 건에서 차이가 난다.** `EXCEPTION_PROC` UPDATE 15의 고객사 수수료 전체. 전제: 명세서를 재구현 입력으로 쓰는 구성 — 원문 SQL 블록만 이식하면 영향 없어, 갈릴 때 높은 쪽으로 매겼다 |

### 🟠 대상 행 집합이 달라지는 결함 (4건)

| 등급 | SP | 원본 앵커 | 산출물 앵커 | 원본 | 산출물 | 영향 |
|---|---|---|---|---|---|---|
| 🟠 | PROC_ETC | `:78-85` | `Spec.md:55, 192` | `IF @@ROWCOUNT > 1` — 직전 `SELECT @v_intID` 가 **2건 이상** 일치했을 때 `MAX(ID)`를 재조회. `metadata.json`의 `ControlFlowSummary`도 `IF (@@ROWCOUNT > 1)`로 확정 | 두 곳 모두 "**2건 초과**이면 `MAX(ID)`를 다시 조회"로 임계값을 한 칸 올려 적음 | 중복이 정확히 2건일 때 원본은 `MAX(ID)` 행을 갱신하나, 명세서대로면 재조회를 건너뛰고 첫 `SELECT`가 마지막 스캔한 임의 행 ID를 쓴다. `CLSettleAmt/CLComm/CLVT` 누적액이 엉뚱한 오정산 행에 실린다. **합계가 같아 `-3` 검증에 걸리지 않고 조용히 통과** |
| 🟠 | PROC_ETC | `:42, 137-141` | `Spec.md:52, 60, 213` | `DECLARE Cur_SettlePost CURSOR READ_ONLY` — `LOCAL`/`GLOBAL` 미지정이라 스코프가 DB 옵션 의존. `-3` 경로는 `CLOSE`/`DEALLOCATE` 없이 `RETURN` | 커서를 "읽기 전용"으로만 서술하고 선언 스코프가 없음. 정리 문장이 없다는 **사실**은 적었으나 커서가 세션에 존속한다는 **결과**는 없음 | 전제: `default to local cursor = OFF`. `-3` 이후 같은 연결에서 재호출하면 `DECLARE`가 "cursor already exists"로 실패, `CATCH`로 떨어져 `4000`이 되고 그 회차는 한 건도 처리하지 못한다. 스코프가 명세에 없어 이식 구현이 지역 커서를 택하면 이 거동이 사라져 처리 행 집합이 달라진다. `ON`이면 🟡 수준 |
| 🟠 | EXPECT_PROC | `:209-210` | `Spec.md:115, 121, 206, 247, 320` | 2-4 조인 술어 `ABS(IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt)) = ABS(E.Amt)` | "절대금액 비교", "금액 조건 일치"로만 서술. `IIF`/`ISNULL` 분기 규칙도 `ABS` 적용도 문장으로 없음 — **문서 전체에서 `ABS` 0회**, `Discount`는 컬럼 나열 2회뿐 | 이 서술로 재구현하면 할인건에도 `TxAmt`를 비교하거나 절대값을 빼먹어 `TPLCardEDIMst` 매칭 행이 달라진다. 매칭 실패 행은 `OutState`(0→2)·`OutYMD`·`EDIReqYmd` 갱신을 통째로 못 받고, 오매칭 시 다른 매입요청일 기준 지급일이 설정된다 |
| 🟠 | INS_EXTRA4PLCARD | `:20-21, 36-37, 189-190, 205-206` | `Spec.md:269, 271` | 사전 확인·DELETE·두 UPDATE에서 `INNER JOIN TPGProperty`에는 **잠금 힌트가 없다**. `NOLOCK`은 `TSettleMst`에만 붙는다 | 잠금 힌트 표 첫 행이 적용 위치를 "사전 확인의 `TSettleMst`, `TPGProperty`"로 묶고 `WITH(NOLOCK)`을 단일 값으로 적음. 힌트가 없는 테이블이라는 구분이 문서 어디에도 없음 | 이 표대로 이전하면 `TPGProperty` 읽기에 없던 READ UNCOMMITTED가 부여된다. 동시 쓰기가 있는 배포에서는 미커밋·누락·중복 행을 읽어 `PG.ExtraType IN (2,3)` 조인 결과가 달라지고, `-9` 조기 중단 발동 여부와 DELETE/UPDATE 대상 행 집합이 원본과 달라진다. 전제: 영향이 동시 갱신 빈도와 격리 수준(RCSI)에 달려 있어 높은 쪽으로 매김. 동시 쓰기가 없는 구성이면 실질 🟡 |

### 🟡 표기·추적성 결함 (15건)

| 등급 | SP | 원본 앵커 | 산출물 앵커 | 내용 |
|---|---|---|---|---|
| 🟡 | SETTLE_INS | `:146` | `Spec.md:92, 108, 291` | `WITH(NOLOCK, INDEX=CIDX_TTxMst_YMD)` 중 **인덱스 강제 힌트가 명세서에 0회**. 92행은 세 분기가 같은 힌트를 쓰는 것처럼 읽힘(부분취소·환불은 `NOLOCK`만). 이관 시 실행계획 역행 위험 |
| 🟡 | SETTLE_INS | `:32, 45, 306, 311` | `Spec.md:28-29, 85-90` | 네 `RETURN`이 모두 값 없는 bare `RETURN`이라 실패(-9/-1/-2)와 성공이 똑같이 반환 상태 0인데, 명세서는 "반환 상태로 성공을 나타낸다"고 서술. 이관 호출자가 그대로 따르면 **`-9` 실패를 성공으로 읽는다** |
| 🟡 | SETTLE_INS | `:74-83, 301` | `Spec.md:140-141` | `TPGProperty`가 `LEFT OUTER JOIN`이라 미매칭 시 `ROUND(...,0,NULL)`이 NULL을 반환해 `PGCOMM/PGVT`에 NULL이 적재되는데 "환불은 `0`입니다"라고 단정. 전제: 해당 PG가 `TPGProperty`에 누락된 배포에서만 발현 |
| 🟡 | COMM_UPD | `:95, 120, 141, …447` (39곳) | `Spec.md:48, 50-89` | 48행이 "주석을 보존한 내용"이라 선언하나 목록은 40항목뿐. 섹션 표제, 대상 한정 주석(전체취소만/부분취소만), 계산 규칙 주석, 이력 주석(`2023.12.13 적용`, `2021.02.19 적용`)이 누락. **전제: 과거 정산일 백필 배포 구성이 있다면 🟠로 올려야 함** |
| 🟡 | EXCEPTION_PROC | `metadata.json` | `Spec.md:101` | 파서 확정 `TPGProperty` 7컬럼을 5개로 축소 표기 — 파서가 진실의 원천이라는 계약 위반 |
| 🟡 | EXCEPTION_PROC | `:165` | `Spec.md:160, 420` | `WHEN ABS(A.TXAMT) <= 150 THEN A.TXAMT`의 **결과값이 설명문에서 사라지고** "150원 … 규칙"으로 서술. 150은 임계값이지 대입값이 아님. 축자 CASE가 같은 셀에 있어 계산식 자체는 보존 |
| 🟡 | EXCEPTION_PROC | `:393-394, 416` | `Spec.md:83` | 파라미터 표가 `TClientSettleRate4MobileCo.YMD`·`TPLCardTxMst.YMD`를 기준일 관련 컬럼으로 나열. 실제로 `@pi_strYMD`와 등치되는 것은 `TSettleMst.YMD`뿐이고 전자는 `A.AYMD = B.YMD`(승인일) |
| 🟡 | EXCEPTION_PROC | `:288, 310, 312, …523` | `Spec.md:34-75` | 구역·정책 주석 누락. 특히 432행 "Payco 취소기한 180일 이후(20260510)에는 아래 로직 제거해도 됨"이라는 **폐기 시한이 명세서 어디에도 없어** UPDATE 16이 영구 규칙으로 굳어질 수 있음 |
| 🟡 | EXPECT_PROC | — | — | (해당 SP의 나머지는 ⚪. 표 아래 4-1 ⑤ 참조) |
| 🟡 | PROC_ETC | `:62` | `Spec.md:92, 180-189, 228-229` | 커서 질의의 `ORDER BY A.OutYMD, A.ClientID`가 명세서에 없음. 신규 등록이 `MAX(ID)+1` 순차 채번이라 순회 순서가 곧 ID 배정 순서 |
| 🟡 | STAT_PGCOLLECT_INS | `:27, 116` | `Spec.md:165, 206` | 오류값 설정 위치를 "라인 20", "라인 104"로 적었으나 실제는 27·116. 20은 `BEGIN TRAN`, 104는 `,0 AS AHEADSALESVT`. `ControlFlowSummary`는 정확하므로 `RawPromptContext`의 어긋난 목록을 옮긴 것 |
| 🟡 | STAT_PGCOLLECT_INS | `:113` | `Spec.md:96, 129-151, 204` | 외부 질의의 `ORDER BY INYMD, CLIENTID, PGNAME, MALLID`가 **문서에 0회** |
| 🟡 | PG_Client_CMRate_Ins | `:112-114, 188-194` | `Spec.md:66` | 해지 분기 술어가 INSERT 2(OR 형태)와 INSERT 4(단독)로 다른데, `TClientContract` 행이 두 INSERT를 포괄한다면서 OR 형태만 적음 |
| 🟡 | PG_Client_CMRate_Ins | `:113-114` | `Spec.md:66, 265` | 원본의 **괄호로 묶인 OR 그룹**을 괄호 없이 표기. 백틱 문자열을 그대로 옮기면 AND 우선순위상 `(USESTATE=5 AND A.CCY=@ymd) OR (B.CCY=@ymd)`로 파싱되어 대상이 넓어짐 |
| 🟡 | CANCEL_INS | `:19-27` | `Spec.md:23, 185-186` | `SET NOCOUNT ON`이 없어 INSERT가 rows-affected 토큰을 흘리는데 "결과셋 없음"이라고만 씀. 문서 전체에 `NOCOUNT` 0회 |
| 🟡 | Summary_AcqManual | `:64` | `Spec.md:94` | 매핑 셀이 `SUM(ISNULL(TsetTleMst.TxAmt,0))`로 테이블명 오기. 같은 표 나머지 30행은 `TSettleMst` |

### 4-1. 전 SP 공통 결함

**① 이전 감사의 최대 결함 부류는 해소됐다.**

이전 🟠 4건은 모두 "원본 WHERE·파생 테이블의 필터 술어가 명세서 어디에도 없다"였다.
**이번 감사에서 그 형태는 14개 SP 중 0건**이다. 담당 단위들이 독립적으로 확인한 바:

- `COMM_UPD` — 15개 UPDATE의 최상위 술어, UPDATE 3의 `NOT IN` 하위질의, UPDATE 7의
  파생 테이블 `D`/`K` 내부 술어까지 전부 필터로 서술됨
- `EXCEPTION_PROC` — 18개 UPDATE의 최상위·하위질의·파생 테이블 술어가 `ISNULL`/`CAST`/`ABS`/UDF
  래핑 좌변을 포함해 전부 대상 한정 조건으로 서술됨. UPDATE 18의 "최상위에 기준일 없음 +
  하위질의에서 사용"이라는 까다로운 구분도 정확
- `INS_EXTRA` — `ISNULL(CompanySalesType,4) IN (0,1,2,3)` 래핑 좌변 7곳 전부 보존
- `SUMMARY_EXTRA` — WHERE 술어 50개 전수 일치, `DELETE 4`에만 있고 `INSERT 4`에는 없는
  `OUTYMD >= @v_strReqYMD` 비대칭까지 정확히 갈라 서술

집합 술어 표의 `연산`·`범위` 열과 파생 테이블 술어 수집이 실제로 작동했다.

**② 대신 새 부류가 드러났다 — 기계 확정 표는 정확한데 산문이 원문을 좁히거나 넓힌다.**

이번 🟠 4건과 🟡 여러 건이 같은 형태다. 기계가 채운 표(집합 술어·DML 범위·SET 매핑)는
원본과 일치하는데, **사람이 읽는 산문·요약·힌트 표가 그 옆에서 다른 말을 한다.**

| SP | 기계 표 | 산문 |
|---|---|---|
| PROC_ETC | `ControlFlowSummary`: `@@ROWCOUNT > 1` | "2건 **초과**" |
| EXPECT_PROC | DML 범위에 `DiscountFlag` 등재 | "절대금액 비교" (`ABS`·`IIF` 규칙 소실) |
| EXCEPTION_PROC | 축자 `CASE` 인용 | "150원 … 규칙" (결과값 `A.TXAMT` 소실) |
| INS_EXTRA4PLCARD | — | 잠금 힌트 표가 두 테이블을 한 칸에 묶음 |
| PG_Client_CMRate_Ins | — | OR 그룹의 괄호 소실 |

이전 부류(필터가 아예 없음)는 기계 재료를 늘려 막았지만, 이 부류는 **기계 표와 산문이
어긋나도 아무도 대조하지 않는다**는 같은 구조적 빈틈에서 나온다. `MechanicalValidator`는
문서 내부의 표 ↔ 산문 일관성을 검사하지 않는다.

**③ 주석 기록표의 부분 누락 — 여전하다. 🟡, 2개 SP**

`COMM_UPD`(약 39건)와 `EXCEPTION_PROC`(9곳)에서 보존표가 항목을 빠뜨렸다. 표에 실린 라인
번호 자체는 원본과 정확히 일치한다. 로직은 SET/WHERE 표에 보존되어 금액·행 집합 영향은
없고 운영 판단 근거의 추적성만 손실된다. 다만 두 건은 성격이 다르다 — `2023.12.13 적용`
(hectofirm), `20260510 이후 제거 가능`(Payco)은 **한시적 로직임을 알리는 유일한 근거**다.

**④ 결과에 영향 없는 실행 속성의 반복 누락 — 🟡, 3개 SP**

인덱스 강제 힌트(`SETTLE_INS`), `ORDER BY`(`PROC_ETC`, `STAT_PGCOLLECT_INS`), `SET NOCOUNT ON`
부재(`CANCEL_INS`), 커서 `READ_ONLY`(`Summary_AcqManual`). 모두 행 집합·금액은 그대로지만
이관 코드의 성능·물리 적재 순서·호출 규약이 달라진다. 명세서 템플릿에 이 속성들을 담는
자리가 없다는 것이 공통 원인으로 보인다.

**⑤ 반환 상태 계약의 미서술 — 🟡, 2개 SP**

`SETTLE_INS`와 `EXPECT_PROC`는 오류 경로와 성공 경로가 **모두 값 없는 `RETURN`**이라
프로시저 반환 상태가 실패 시에도 0이다. 성공/실패 구분은 출력 파라미터로만 가능한데,
`SETTLE_INS` 명세서는 반대로 "반환 상태로 성공을 나타낸다"고 적었다. 이관 호출자가
이 문장을 따르면 실패를 성공으로 읽는다.

**⑥ `갱신 0` 절 제목 — 이전 감사 ③이 그대로다.**

`EXPECT_PROC` 11개, `COMM_UPD` 15개, `EXCEPTION_PROC` 18개의 UPDATE 매핑 절 제목이 전부
"갱신 0"이다. 파서의 `GlobalStatementOrdinal`이 전건 0인 것을 템플릿이 반영한 결과다.
**이 항목은 담당 단위가 제기한 것이 아니라 감사 상위에서 기계적으로 센 것**이므로,
등급을 매기지 않고 사실만 기록한다.

### 4-2. 교차 대조 — UDF 활용 규칙 표 10행 (추가 실행)

축 A의 단위 분업은 함수 단위가 호출부를, SP 단위가 함수 본문을 각각 읽지 않는다.
`EXCEPTION_PROC/Spec.md:397-406`의 「UDF 활용 규칙 및 제약」 표는 **10개 함수의 동작을
산문으로 요약**하는데, 이 자리를 어느 축도 검증하지 않았다. 10행을 각각 함수 원본 DDL 및
호출 지점과 대조했다.

| 행 | 함수 | 판정 | 결함 |
|---|---|---|---|
| `:398` | `UF_GET_CLIENTSECTIONRATE` | 결함 | 🟡1 |
| `:399` | `UF_GET_INCVTAXRATE` | **정합** | ⚪1 |
| `:400` | `UF_GET_ROUND4VAT` | 결함 | 🟡1 ⚪1 |
| `:401` | **`UF_GET_PGCommOption`** | **결함** | **🔴1** |
| `:402` | **`UF_GET_COMM4CLIENT`** | **결함** | **🔴1** 🟡2 |
| `:403` | `UF_GET_COMM4CLIENT4INTEREST` | **정합** | 없음 |
| `:404` | `UF_GET_COMM4PG` | 결함 | 🟡2 |
| `:405` | **`UF_GET_COMM4PG4INTEREST`** | **결함** | **🔴1** 🟡2 ⚪1 |
| `:406` | **`UF_GET_COMM4CLIENT4PARTIALCANCEL`** | **결함** | **🔴1** 🟡2 ⚪2 |
| `:407` | `UF_Get_CLComm4MobileCo` | 결함 | 🟡1 |

**이 한 표에서 🔴 4건이 나왔다.** 전체 감사의 🔴 5건 중 4건이 여기 있다.

#### 🔴 4건

| 함수 | 원본이 하는 일 | 요약이 빠뜨린 것 | 영향 |
|---|---|---|---|
| `UF_GET_COMM4CLIENT` (`:402`) | 조회 **키와 값이 모두** `@pi_intFreeInterestFlag`로 갈린다 — 키는 `IIF(flag IN (0,2), CardCPID, FreeInterestInsCPID)`, 요율은 `IIF(flag IN (0,2), CommissionRate, FreeInterestInstCommRate)` | 분기 자체가 사라지고 키를 무조건 `CardCPID`, 값을 무조건 `CommissionRate`로 단정 | `CLInterest = 1`인 카드 거래에서 다른 계약 행을 잡거나 일반 요율을 곱해 `CLCOMM`·`CLVT`가 달라진다 |
| `UF_GET_COMM4CLIENT4PARTIALCANCEL` (`:406`) | 위와 **같은 이중 분기**가 1차·2차 조회 모두에 있다 | 동일하게 통째로 누락 | `CLInterest = 1` 부분취소 건에서 요율·매칭 계약이 모두 달라진다 |
| `UF_GET_COMM4PG4INTEREST` (`:405`) | 2차 조회 WHERE에 `USESTATE = 0`(주석: `0:정상 1:비정상`)이 **필수 술어**로 있다 | `IsPGFlag = 1`만 "PG"라는 낱말로 암시하고 **사용상태 필터가 통째로 빠졌다** | 비정상 이력 행이 함께 잡히고 `SELECT @변수 =` 가 마지막 행을 남겨 폐기된 요율이 적용된다 |
| **`UF_GET_COMM4CLIENT` (`:402`) — 두 번째 🔴** | 거짓 `IF`가 `@@ROWCOUNT`를 **0으로 리셋**하므로(실측) 1차가 성공하면 `:68`이 반드시 참이 되어 3차가 실행된다. 3차는 1차와 **같은 테이블·같은 WHERE**인데 `TOP 1 ORDER BY 요율 DESC`라 1차 결과를 덮어쓴다 | "가맹점번호 우선 계약, 이력 계약, 수수료율 내림차순 계약 **순으로** 조회" — **존재하지 않는 폴백 체인** | 실효 규칙은 폴백이 아니라 "`TClientCardContractDtl`에 행이 있으면 **요율 최대 행**, 없으면 `Hist`에서 `Version` 최대"다. 계약 상세가 복수 행인 가맹점에서 `CLCOMM`·`CLVT`가 달라진다 |
| `UF_GET_PGCommOption` (`:401`) | 변수를 `0`으로 초기화한 뒤 SELECT로 덮으므로 **미조회 시 0(=반올림)** 이 반환된다 | 기본값 0도 NULL 전파도 언급 없음 | 미조회 시 NULL을 반환하도록 구현하면 `TPGProperty`에 없는 PG의 `PGVT`가 NULL이 된다 |

#### 왜 하필 이 표인가

**같은 문서의 다른 표는 정확했다.** 집합 술어 표(`:355-356`)와 DML 범위 표(`:312`)는
기계 확정값이라 원본 술어를 빠짐없이 싣는다. 축 A가 SP 단위에서 🟠 4건을 잡은 것도
그 표들이 기준값을 제공했기 때문이다.

**「UDF 활용 규칙」 표만 기계 재료가 없다.** 파서는 `ReferencedFunctions`에 함수 *이름*만
담고 함수가 *무엇을 하는지*는 담지 않는다. 그래서 이 표는 순수한 LLM 산문이고,
받쳐 주는 기준값이 없다. **조건이 사라지는 자리가 정확히 거기다.**

4-1 ②에서 "기계 표는 정확한데 산문이 다른 말을 한다"고 적었는데, 이 표는 그 극단이다 —
옆에 놓인 기계 표조차 없다.

#### 표 내부의 서술 수준 불균형

같은 표 안에서 어떤 행은 조건을 명시하고 어떤 행은 빠뜨린다. 단위들이 반복해서 이 대비를
지적했다.

- `:398`은 "사용상태 0 조건"을 명시 ↔ `:405`는 같은 성격의 `USESTATE = 0`을 누락
- `:398`은 "조회 결과가 없으면 0을 반환하며"를 명시 ↔ `:401`·`:405`는 기본값 0 누락
- `:402`·`:407`은 "거래금액에 수수료율을 곱한 후 정수로 변환"을 명시 ↔ `:404`·`:406`은 산출식 자체를 누락
- `:404`는 "할인구분이 Y이면 할인금액을"을 명시 ↔ `:405`는 **같은 호출문의 동일 인자식**인데 누락

#### 반대 방향 — 호출자 책임을 함수 책임으로 옮긴 것

`:404`(`UF_GET_COMM4PG`)는 "할인구분이 Y이면 할인금액을, 그렇지 않으면 거래금액을 기준으로"
라고 적는데, 함수 파라미터 7개에 `DiscountFlag`·`DiscountAmt`가 없다. 할인 판정은
호출 SP의 인자식 `IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt)`(`:362`)가 한다.
이 표의 열 이름은 `실제 로직`이고 대상은 함수인데, 호출자의 인자 선택을 함수 동작으로 옮겨
적었다. 🟡 — 이 호출 지점만 놓고 보면 금액은 같다.

#### 단위 간 판정이 갈렸던 지점 — 실행으로 닫았다

`UF_GET_COMM4CLIENT`의 3차 조회 진입 조건 `IF @@ROWCOUNT < 1`(`:68`)이 1차 성공으로 2차 IF
블록이 건너뛰어졌을 때 무엇을 읽는지에 대해 두 단위의 입장이 갈렸다.

- 축 A 함수 단위: "`IF`가 `@@ROWCOUNT`를 보존하므로 결과는 동일" → ⚪
- 교차 대조 단위: 보존인지 리셋인지에 따라 금액이 갈린다 → 보류

**2026-08-20 SSMS 실행으로 확정했다. 거짓 `IF`는 `@@ROWCOUNT`를 리셋한다.**

```sql
CREATE TABLE #t (v INT);  INSERT INTO #t VALUES (1);
DECLARE @x INT;
SELECT @x = v FROM #t;      -- @@ROWCOUNT = 1
IF @@ROWCOUNT < 1  BEGIN  SELECT @x = -99;  END   -- 거짓, 건너뜀
IF @@ROWCOUNT < 1  BEGIN  SELECT @x = -77;  END   -- ← 참이 됐다
SELECT @x;                  -- 결과: -77
```

**교차 대조 단위의 우려가 맞았고, 축 A 함수 단위의 ⚪는 틀린 전제 위에 있었다.** 두 판정을
모두 🔴로 올렸다 — 함수 자신의 `Spec.md`(mermaid를 if-else 사슬로 그림)와 `EXCEPTION_PROC`의
UDF 표(폴백 체인 서술)가 **각각 다른 문서에서 같은 오독**을 하고 있다.

이 패턴(`@@ROWCOUNT` 2회 + 연쇄 IF)을 폐포 31개 객체 전체에서 찾은 결과 **`UF_GET_COMM4CLIENT`
하나뿐**이다. 다른 5개 객체(`UF_GET_CLIENTSECTIONRATE`, `UF_GET_COMM4CLIENT4PARTIALCANCEL`,
`UF_Get_ExtraCardCommissionAmt`, `UF_GET_EXTRACOMM4CLIENT`, `UP_UTIL_SETTLE_PROC_ETC`)는
`@@ROWCOUNT`가 1회이고 SELECT 직후 단일 IF라 연쇄 문제가 없다.

#### 정합 2건

`UF_GET_INCVTAXRATE`(`:399`)는 반환값 매핑이 전건 일치하고, 호출 위치 목록
`UPDATE 2, 3, 4, 8, 9, 13, 14, 15, 17`이 실제 9곳과 **정확히 일치**한다(단위가 UPDATE 경계
18곳을 라인 범위로 환산해 대조했다). `UF_GET_COMM4CLIENT4INTEREST`(`:403`)도 결함 없음이다.

### 4-3. 함수 17개의 공통 결함

**⑴ `CAST(... AS INT)`의 절사가 서술되지 않는다 — 7개 함수.**

금액을 확정하는 자리가 전부 `CAST(money AS INT)`인데, 이것이 반올림이 아니라 **0 방향 절사**
라는 사실을 대부분의 명세서가 적지 않는다. 계산식 자체는 원문 그대로 인용하므로 SQL을 그대로
이식하면 값이 같지만, **명세서를 근거로 다른 런타임에 재구현하면 건당 최대 1원씩 어긋난다.**

| 함수 | 등급 | 서술 |
|---|---|---|
| `UF_Get_CLComm4MobileCo` | 🔴 | 절사·반올림·버림 단어가 문서에 0건 |
| `UF_GET_COMM4CLIENT` | 🟡 | 〃 (`grep` 0건) |
| `UF_GET_COMM4CLIENT4INTEREST` | 🟡 | 〃 |
| `UF_GET_COMM4CLIENT4PARTIALCANCEL` | 🟡 | "절삭 여부를 포함한 동작은 `CAST` 규칙에 따른다"로 확정 회피 |
| `UF_GET_COMM4PG4INTEREST` | 🟡 | "정수 변환 결과를 기반으로 합니다" |
| `UF_GET_COMM4PG` | ⚪ | "소수 부분은 제거된다" — 방향 미명시 |
| **`UF_GET_EXTRACOMM4CLIENT`** | — | **"소수 부분이 제거된 정수 금액"으로 절사를 확정 서술 — 유일하게 옳게 적음** |

반올림으로 **뒤집어** 적은 곳은 한 곳도 없다. 전부 누락이지 오기가 아니다.

**⑵ `WITH` 옵션 부재를 적지 않는다 — 13개 함수.**

모든 함수가 `WITH SCHEMABINDING` 없이 선언돼 있고 이는 DDL 원문에서 **확정적으로 읽히는
사실**인데, 명세서는 두 가지 방식으로 이를 놓친다.

- **누락형** — 결정성 절이 `NOLOCK`·데이터 의존성만 적고 옵션 부재를 언급하지 않음
- **유보형** — "제공된 메타데이터에는 …선언 정보가 없습니다"라며 판단을 유보.
  `UF_GET_SETTLE_EXCHANGERATE`, `UF_GET_COMM4CLIENT4INTEREST`, `UF_GET_COMM4PG4INTEREST`가
  이 형태다. **파서에 항목이 없다는 것을 원본에서 확인 불가로 옮겨 적은 것**으로,
  스킬 3절이 명시적으로 금지한 오독("파서에 없다는 것을 원본에 없다로 읽지 마라")이다.

`RETURNS NULL ON NULL INPUT` 부재는 여러 함수의 NULL 전파 서술의 전제인데, 그 근거가
문서에 남지 않는다.

**⑶ 마크다운 표가 깨진다 — 2개 함수.**

`UF_GET_PGCommOption`(헤더 6열 vs 구분자 5열)과 `UF_GET_COMM4PG`(4열 vs 3열)에서 GFM이
표를 인식하지 못해 절 전체가 파이프 섞인 평문으로 렌더된다. **이전 감사에서 `EXPECT_PROC`에
있던 것과 같은 부류이고, 그 SP에서는 이번에 해소됐는데 함수 문서에서 다시 나타났다.**
문서 종류를 가리지 않는 생성기 수준의 문제로 보인다.

**⑷ 함수가 잘한 것.**

주석 처리된 코드를 활성 로직으로 오서술한 사례는 **17개 중 0건**이다. 오히려 여러 단위가
원본 주석이 실제 로직과 어긋나는 지점을 능동적으로 짚었다 — `UF_GET_PGCommOption`(주석은
"부가세 포함 여부 조회"인데 실제 CASE 범위가 다름), `UF_GET_COMM4CLIENT`(주석 3곳의
불일치 적시), `UF_GET_COMM4PG4INTEREST`("수수료 + 무이자할부 합산" 주석이 실제 식과 다름),
`UF_GET_COLLECTYMD`(`HolidayPayFlag` 주석 도메인 0·1 vs 실행 조건 `= 2`).

`UF_Get_ExtraCardCommissionAmt` 단위는 명세서의 인용 SQL 블록 9개를 공백 정규화 후 DDL과
기계 대조해 전부 부분문자열로 일치함을 확인했다 — 변조·요약이 없다.

**⑦ 명세서가 잘한 것 — 주석 상태 판별과 비대칭 서술**

주석 처리되어 실행되지 않는 조건을 활성 로직으로 오서술한 사례는 **14개 SP 중 0건**이다.
`INS_EXTRA`(3곳), `STAT_PGCOLLECT_INS`(`CLIENTID IN ('PAYLETTER','PLTEST')`),
`PROC_ETC`(`--AND C.ClientIDType <> 1`), `SETTLE_INS`(블록 주석 구 환불수수료식),
`COMM_UPD`(블록 주석 안의 `-4`·`-15`), `EXCEPTION_PROC`(VIRTUALBANK/BANKTOWNBANK)를
전부 정확히 구분했다. 반대 방향(활성 코드를 주석이라 서술)도 0건이다.

원본의 미묘한 비대칭도 여러 단위가 정확히 짚었다 — `SUMMARY_ETC`의 "DELETE 조건에는
`OUTSTATE`가 있는데 INSERT 집계에는 없다", `INS_EXTRA4PLCARD`의 "파생 테이블만 `ISNULL`
없이 `A.CompanySalesType IN (...)`", `Util_Settle_Summary`의 "헤더 `Inner SP : NONE`이
실제 `EXEC` 2건과 모순".

## 5. 축 B 결함

미실행. 6절 참조.

## 6. 이 감사가 보증하지 않는 것

- **축 B 전체.** `Spec.md` ↔ `agent/steps/S01..S17.md` 17단위를 대조하지 않았다.
  4절의 🟠 4건이 계획서로 전파됐는지는 이 보고서로 알 수 없다. 더구나 현재
  `POQSettlePrco20/agent/`의 단계 지시서는 **2026-08-19에 옛 명세서로 생성된 것**이므로,
  지금 축 B를 돌리면 새 명세서와 옛 계획서를 대조하게 되어 결과가 성립하지 않는다.
  축 B에 앞서 배치 잡을 재생성해야 한다.
- **~~UDF·TVF 본문~~ — 이번 감사에서 해소됐다.** 이전 보고서가 한계로 적었던 함수 10종은
  이번에 축 A 단위로 검증했고, 외부 DB 함수 7종도 함께 넣었다. 이전 감사의 보류 24건 중
  6건이 이 사유였고 모두 닫혔다. 다만 **함수의 함수**는 폐포에 없으므로 여전히 밖이다
  (`UIF_SettleYMD`와 `UF_GET_COLLECTYMD`가 부르는 `UF_GET_WORKDAY2`는 폐포에 있어 검증됐다).
- **테이블 스키마 의존 주장.** 컬럼 nullability·기본값·PK 유무에 기댄 서술은 대상 테이블
  DDL이 단위 열람 범위 밖이라 보류했다(`CANCEL_INS` Spec.md:69·111,
  `SUMMARY_ETC` `TSettleByOUT` PK 부재, `INS_EXTRA` Spec.md:130·184,
  `INS_EXTRA4PLCARD` Spec.md:125·169·180 등).
- **컬럼 한글 명칭.** `SUMMARY_ETC`와 `Util_Settle_Summary`에서 업무 명칭이
  `metadata.json` `Dependencies`의 컬럼 `Description`에 근거하는데, 같은 컬럼에 대해
  원천 테이블과 대상 테이블의 설명이 서로 다르다(예: `YMD`가 한쪽은 "거래 또는 취소일자",
  다른 쪽은 "거래일"). **어느 쪽을 규범으로 삼을지가 대조 계약에 정의돼 있지 않아**
  정오를 판정하지 않았다. 계약 보완이 필요한 지점이다.
- **1부 식별자의 해석.** `CANCEL_INS`, `Summary_AcqManual`, `INS_EXTRA4PLCARD`에서 원본이
  무한정(1부) 테이블 이름을 쓰는데 명세서가 3부로 정규화했다. `StaticAnalysis`와는
  일치하므로 계약상 결함이 아니나, SP를 다른 DB에 배포하면 원본은 현재 DB로 해석되고
  명세서는 고정 DB를 지시한다. 배포 DB 정보가 범위 밖이라 판정을 보류했다.
- **함수 단위가 호출부를 못 본 것은 한계가 아니다.** 17개 단위 모두 "호출 SP의 원문은 범위
  밖"이라고 보류에 적었는데, 이는 분업이다. 그 사이에 남았던 자리 — `EXCEPTION_PROC`의
  「UDF 활용 규칙」 표 10행 — 은 **4-2절에서 전건 대조해 닫았다.**

  **다만 이 표는 `EXCEPTION_PROC`에만 있다.** 다른 13개 SP의 명세서가 참조 함수 동작을
  어떤 형태로 서술하는지, 그 서술에도 같은 부류의 결함이 있는지는 대조하지 않았다.
  이번 결과(10행 중 8행 결함, 🔴 4건)를 보면 **다른 SP에도 같은 자리가 있다면 우선순위가
  높다.**

- **~~단위 간 판정이 갈린 자리~~ — 실행으로 닫았다.** `IF @@ROWCOUNT` 리셋 여부를
  2026-08-20 SSMS에서 확인해 두 판정을 🔴로 확정했다(4-2절). **이 감사에서 유일하게
  텍스트 대조가 아닌 실행으로 확인한 항목이다.**

- **`spt_values` 등 시스템 객체의 실제 내용.** `UIF_SettleYMD`·`UF_GET_COLLECTYMD`의 휴일
  탐색 상한 판정은 `MASTER..SPT_VALUES`의 `TYPE='P'` 행이 0부터 연속이라는 SQL Server 표준
  구성을 전제로 했다. 해당 의존성은 `Type: UNKNOWN`, `Columns: []`로 비어 있다.

- **실행 대조 없음.** 원본 SP와 이행 대상을 실제로 실행해 결과를 비교하지 않았다.
  모든 판정은 텍스트 대조다.
