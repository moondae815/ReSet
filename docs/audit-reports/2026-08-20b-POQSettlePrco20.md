# POQSettlePrco20 산출물 정합성 감사

> 실행: 2026-08-20 (명세서 31개 재생성 직후) · 축 A + 축 A 교차. 축 B는 미실행.
> 직전 보고서는 `ConsistencyReport-20260820a.md`(재생성 전), 그 이전은 `ConsistencyReport-20260819.md`.

## 1. 판정

| 축 | 판정 | 단위 수 | 신규 검증 | 캐시 재사용 | 검증 불가 |
|---|---|---|---|---|---|
| A (객체) | **결함** | 31 | 31 | 0 | 0 |
| A 교차 | **결함** | 8 | 8 | 0 | 0 |
| B (단계) | **미실행** | — | — | — | — |

결함 등급 합계: **🔴 1 · 🟠 0 · 🟡 12 · ⚪ 1**

4-2절이 실행 확인을 요구한 🔴 2건은 2026-08-20 SSMS 실행으로 **둘 다 반증되어 취소**했다.
각 단위가 스스로 정한 취소 조건에 그대로 걸린 결과다(4-2절 참조).

캐시는 전건 미스였다. 31개 `Spec.md`가 모두 재생성되어 축 A 키의 세 해시 중 하나가 바뀌었고,
교차 단위는 옛 이름 형식(`axisA-cross:[함수명]↔[SP명]`) 10건을 대조 계약 변경에 따라 폐기했다.

## 2. 검증 대상 확정

**소비 명세서 집합 12개** — `agent/MigrationInstructions.md`의 `Spec.md` 링크에서 읽었다(폴백 ② 불필요).

**참조 폐포 31개** — 소비 12개 각각의 `raw/dependency-manifest.json` `Nodes[]` 합집합.
`Status`는 31개 전부 `Succeeded`이고, `SpecPath`·`DdlPath`·`metadata.json` 93개 경로가 모두 실재한다.

| 종류 | 개수 |
|---|---|
| SP | 14 (소비 12 + 중첩 2) |
| 로컬 함수 | 10 |
| 외부 DB 함수 (`SETTLE_CARD_DB`) | 7 |

**폐포에만 있는 SP 2개** — `UP_Util_Settle_Summary_AcqManual`, `UP_UTIL_SETTLE_SUMMARY_EXTRA`.
둘 다 `UP_Util_Settle_Summary`가 `EXEC`로 부르는 하위 SP이고(그 매니페스트 3노드 = 자신 + 이 둘),
최상위 실행 순서에 포함되지 않아 소비 집합에서 빠진 것이 정상이다(사용자 확인). 축 A 대상에는 포함했다.

**축 A 교차 대상 8개** — 폐포 안에서 사용자 함수를 호출하는 객체 전부.
SP 6개(`SETTLE_INS`, `SETTLE_COMM_UPD`, `SETTLE_EXCEPTION_PROC`, `SETTLE_EXPECT_PROC`,
`SETTLE_INS_EXTRA`, `SETTLE_INS_EXTRA4PLCARD`)와 함수 2개(`UIF_SettleYMD`, `UF_GET_COLLECTYMD`).
함수 2개는 「참조 함수」 표가 없는 것이 정상이며(호출이 전부 DML 밖), 두 단위 모두 3-2-1절
사각지대 점검으로 대신했다.

**함수 공유 관계** (결함의 영향 범위 산정 근거, 폐포 DDL 역산):

| 함수 | 호출하는 객체 수 | 호출자 |
|---|---|---|
| `UF_GET_INCVTAXRATE` | 5 | COMM_UPD, EXCEPTION_PROC, SETTLE_INS, INS_EXTRA, INS_EXTRA4PLCARD |
| `UF_GET_ROUND4VAT` | 5 | 위와 동일 |
| `UF_GET_WORKDAY2` | 3 | UF_GET_COLLECTYMD, UIF_SettleYMD, INS_EXTRA |
| `UIF_SettleYMD` | 3 | EXPECT_PROC, INS_EXTRA, INS_EXTRA4PLCARD |
| `UF_GET_PGCommOption` | 2 | COMM_UPD, EXCEPTION_PROC |
| 나머지 12개 | 각 1 | — |

## 3. 단위별 커버리지

| 축 | 단위 | 종류 | 판정 | 상태 | 근거 파일 |
|---|---|---|---|---|---|
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_CANCEL_INS.Procedure | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_CANCEL_INS.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_CANCEL_INS/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_CANCEL_INS/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_INS.Procedure | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_INS.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_INS/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_INS/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_SUMMARY_ETC.Procedure | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_ETC.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_ETC/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_ETC/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_STAT_PGCOLLECT_INS/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_STAT_PGCOLLECT_INS/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_Util_PG_Client_CMRate_Ins.Procedure | SP | 정합 | 신규 | `output/Objects/dbo.UP_Util_PG_Client_CMRate_Ins.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_Util_PG_Client_CMRate_Ins/docs/Spec.md`<br>`output/Procedures/dbo.UP_Util_PG_Client_CMRate_Ins/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_Util_Settle_Summary.Procedure | SP | 정합 | 신규 | `output/Objects/dbo.UP_Util_Settle_Summary.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_Util_Settle_Summary/docs/Spec.md`<br>`output/Procedures/dbo.UP_Util_Settle_Summary/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_Util_Settle_Summary_AcqManual.Procedure | SP | 결함 | 신규 | `output/Objects/dbo.UP_Util_Settle_Summary_AcqManual.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_Util_Settle_Summary_AcqManual/docs/Spec.md`<br>`output/Procedures/dbo.UP_Util_Settle_Summary_AcqManual/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT.Function | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4INTEREST.Function | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT4INTEREST.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4INTEREST/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4INTEREST/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_GET_COMM4PG.Function | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4PG.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_GET_COMM4PG4INTEREST.Function | 외부함수 | 결함 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4PG4INTEREST.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG4INTEREST/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG4INTEREST/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_GET_EXTRACOMM4CLIENT.Function | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_EXTRACOMM4CLIENT.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_EXTRACOMM4CLIENT/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_EXTRACOMM4CLIENT/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_Get_ExtraCardCommissionAmt.Function | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_Get_ExtraCardCommissionAmt.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_Get_ExtraCardCommissionAmt/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_Get_ExtraCardCommissionAmt/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_CLIENTSECTIONRATE.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_CLIENTSECTIONRATE.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_CLIENTSECTIONRATE/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_CLIENTSECTIONRATE/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_COLLECTYMD.Function | 함수 | 결함 | 신규 | `output/Objects/dbo.UF_GET_COLLECTYMD.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_COLLECTYMD/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_COLLECTYMD/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_INCVTAXRATE.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_INCVTAXRATE.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_INCVTAXRATE/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_INCVTAXRATE/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_OUTYMD4REFUND.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_OUTYMD4REFUND.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_OUTYMD4REFUND/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_OUTYMD4REFUND/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_PGCommOption.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_PGCommOption.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_PGCommOption/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_PGCommOption/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_ROUND4VAT.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_ROUND4VAT.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_ROUND4VAT/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_SETTLE_EXCHANGERATE.Function | 함수 | 결함 | 신규 | `output/Objects/dbo.UF_GET_SETTLE_EXCHANGERATE.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_SETTLE_EXCHANGERATE/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_SETTLE_EXCHANGERATE/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_WORKDAY2.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_WORKDAY2.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_WORKDAY2/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_WORKDAY2/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_Get_CLComm4MobileCo.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_Get_CLComm4MobileCo.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_Get_CLComm4MobileCo/docs/Spec.md`<br>`output/Functions/dbo.UF_Get_CLComm4MobileCo/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UIF_SettleYMD.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UIF_SettleYMD.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UIF_SettleYMD/docs/Spec.md`<br>`output/Functions/dbo.UIF_SettleYMD/raw/metadata.json` |
| A 교차 | SETTLE_POQ_DB.dbo.UF_GET_COLLECTYMD.Function | 교차 | 정합 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UIF_SettleYMD.Function | 교차 | 정합 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure | 교차 | 정합 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure | 교차 | 정합 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure | 교차 | 정합 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_INS.Procedure | 교차 | 정합 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure | 교차 | 결함 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure | 교차 | 정합 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |

## 4. 축 A 결함

| 등급 | 객체 | 원본 앵커 | 산출물 앵커 | 원본 | 산출물 | 영향 |
|---|---|---|---|---|---|---|
| 🔴 | `UP_UTIL_SETTLE_EXCEPTION_PROC` | `object_definition.sql:187-196` (UPDATE 6, INIBANK) | `Spec.md:150`, `:189-195` | `PGVT` 계산식이 같은 문장이 갱신하는 `PGCOMM`의 **갱신 전 값**(`A.PGCOMM`)을 읽는다. UPDATE 7(`:210-225`)과 같은 패턴 | 「자기참조 SET 표현식」 문단이 갱신 2·4·7·8·12·13·17만 나열하고 **갱신 6이 없다**. 개별 SET 표에도 언급 없음 | 순차형(행 단위) 이관에서 `PGCOMM`을 먼저 갱신하고 그 새 값으로 `PGVT`를 계산하면 부가세 금액이 원본과 달라진다 |
| 🟡 | `UP_UTIL_SETTLE_INS_EXTRA` | 8/20 판 `Spec.md`의 「`NULL` 수수료 전파」 행 | `Spec.md` (해당 서술 없음) | 8/20 판은 "`CLTOTAL`·`PGTOTAL`·`POQINCOME`은 `+`와 `-` 연산이라 구성 수수료 중 `NULL`이 있으면 결과도 `NULL`이 될 수 있다"고 서술했다 | 그 서술이 사라졌다 | 추적성. 단위가 제시한 발생 경로(`ROUND` 세 번째 인자 `NULL`)는 실행으로 반증됐으나(4-2절), 사라진 문장 자체는 `ROUND`와 무관하게 성립하므로 손실은 손실이다. **🔴에서 내렸다** |
| 🟡 | `UP_UTIL_SETTLE_INS_EXTRA` | `:30-38` | `Spec.md:31` (포괄 문장만) | `IF EXISTS(SELECT PLTID FROM TSettleMst WITH(NOLOCK) WHERE … OutState IN (1,5)) → RETURN -9` | `NOLOCK` 포괄 문장만 있고, 이 게이트가 `NOLOCK` 기반이며 프로시저 실행 여부 자체를 가른다는 연결이 없음 | 동시성 조건에 따라 🟠까지 갈 수 있다(오탐 시 정상 재처리 스킵, 누락 시 중복 처리) |
| 🟡 | `UP_UTIL_SETTLE_INS_EXTRA` | `:47-53` vs `:197-201` | `Spec.md` (종합 서술 없음) | `DELETE`는 `ProcYMD = @pi_strYMD AND YMD >= @v_strReqYMD`, `INSERT` 원천은 `A.ResYMD = @pi_strYMD`만 | 개별 조건은 정확히 보존되나 "서로 다른 필드·범위로 결정된다"는 대조가 없음 | 추적성 |
| 🟡 | `UP_UTIL_SETTLE_INS` | `:243-249` | `Spec.md` (해당 서술 없음) | `/* */`로 통째 주석 처리된 옛 `CLCOMM`/`CLETC` 계산식(`FeeCharge`, `CLIENTFEEAMT` 사용) | 죽은 코드 블록의 존재가 없다. 주석 표는 블록 안 트레일링 주석만 활성 코드인 양 옮겼다 | 활성 계산식은 `INSERT` 매핑 표에 정확해 금액 영향 없음. 계약이 명시 요구한 "주석 처리된 블록" 누락 |
| 🟡 | `UP_UTIL_SETTLE_EXPECT_PROC` | `:146`, `:168` | `Spec.md:230-261` (집합 술어 표) | UPDATE 6 `LEFT(D.PayToolType,1) IN ('C')`, UPDATE 7 `LEFT(D.PayToolType,1) IN ('A','B')` — 둘 다 최상위 `WHERE`의 리터럴 `IN` 술어 | 기계 확정 표에 두 행 모두 없음 | 「DML 범위」 표와 로직 흐름 요약에는 남아 정보 손실은 아니다. 기계 확정 표 자체의 불완전성 — 도구 결함(4-1절) |
| 🟡 | `UP_UTIL_SETTLE_EXCEPTION_PROC` | `:55-71`, `:313-321`, `:341-344`, `:452-468` | `Spec.md:150` | 갱신 2·12·13(일부)·17의 SET 식은 파생/원천 테이블 값을 그대로 대입할 뿐 대상 행의 기존 값을 읽지 않는다 | 같은 문단이 이 8개 컬럼을 "자기참조"로 잘못 포함 | 순서 무관이라 기능 위험은 낮으나, 실제 자기참조 항목과 섞여 문단 신뢰도가 떨어진다 |
| 🟡 | `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | `:20,189,205`(있음) / `:36,21,37,190,206`(없음) | `Spec.md:77` | `TSettleMst`는 사전확인·UPDATE1·UPDATE2에만 `NOLOCK`, `DELETE`에는 없다. `TPGProperty`는 `INSERT`의 `P`·`Y` 별칭에만, `PG` 별칭 조인 4곳에는 없다 | "`NOLOCK` 힌트가 5개 테이블의 조회 또는 조인에 사용됩니다"로 뭉뚱그려 문장별 차이가 지워짐 | 이관 시 `NOLOCK`을 일괄 적용·누락해 잠금 동작이 달라질 수 있다 |
| 🟡 | `UP_Util_Settle_Summary_AcqManual` | `:47` | `Spec.md:33` | `DELETE TSettleByOUT FROM … WITH(NOLOCK)` — `NOLOCK`이 걸리는 자리는 총 3곳 | "원천 조회와 커서 대상 조회에 `WITH(NOLOCK)`" — `DELETE` 대상 스캔의 `NOLOCK` 누락 | 격리수준 서술 범위가 실제보다 좁다 |
| 🟡 | `UP_UTIL_STAT_PGCOLLECT_INS` | `:113` | `Spec.md` (해당 서술 없음) | `INSERT … SELECT … GROUP BY INYMD, CLIENTID, PGNAME, MALLID ORDER BY INYMD, CLIENTID, PGNAME, MALLID` | `ORDER BY` 절이 문서 어디에도 없다(DML 범위 표·파생 테이블 표·로직 흐름 요약 모두 `GROUP BY`까지만) | 금액·행 집합 영향 없음(`INSERT…SELECT`의 `ORDER BY`는 삽입 순서를 보장하지 않는다). 추적성 |
| 🟡 | `UF_GET_SETTLE_EXCHANGERATE` | `:9-15` | `Spec.md:229` | `CREATE FUNCTION … RETURNS DECIMAL(9,5) AS` — `WITH` 절이 없어 "스키마 바인딩 아님"이 원문에서 확정된다 | "함수의 스키마 바인딩 여부"를 "제공된 정보에 포함되지 않는 항목"으로 분류 | 표기·추적성. 호출 SP 1개(`COMM_UPD`) |
| 🟡 | `UF_GET_COMM4PG4INTEREST` | `:72` | `Spec.md:25,122,133,151,163,284` | 원본 주석 `--PG여부 (1:PG용 2:가맹점용)` | "피지용"이라는 없는 용어로 6곳 일관 오기 | 필터 `IsPGFlag = 1`은 정확해 금액 영향 없음. 추적성. 호출 SP 1개(`EXCEPTION_PROC`) |

### 4-1. 전 객체 공통 결함

**(1) 재생성이 원본의 "경고성 서술"을 함께 깎았다 — 회귀 4건, 2개 객체.**

재생성 전 판(`ConsistencyReport-20260820a.md` 시점의 `Spec.md`)이 담고 있던 서술이 이번 판에서
사라졌다. 상위가 백업본과 직접 대조해 확정한 것이다(단위는 세 파일만 읽어 판단을 보류했다).

| 객체 | 사라진 서술 | 이번 등급 |
|---|---|---|
| `UP_UTIL_SETTLE_INS_EXTRA` | `NULL` 수수료 전파 | 🟡 (실행으로 🔴에서 내림) |
| `UP_UTIL_SETTLE_INS_EXTRA` | 지급 완료·확정 보호(`OutState IN (1,5)` 사전 검사의 `NOLOCK`) | 🟡 |
| `UP_UTIL_SETTLE_INS_EXTRA` | 삭제와 삽입 범위 차이 | 🟡 |
| `UP_UTIL_SETTLE_INS` | 주석 처리된 옛 환불 수수료 계산식 블록 | 🟡 |

두 객체 다 문서가 짧아졌고(-72줄, -24줄), 잃은 것이 전부 **원본의 죽은 코드·제약·`NULL` 위험**
같은 부류다. 「참조 함수」 표 도입으로 산문 서술을 금지·축소하면서 함께 깎인 것으로 보인다.
네 건 모두 🟡에 머물러 금액이 달라지는 결함은 아니지만, 사라진 것이 하나같이 이관자가 알아야 할
**원본의 함정**이라는 점이 문제다. 기계 확정 표가 늘어난 만큼 산문에서 무엇을 반드시 남길지
프롬프트에 명시하는 것이 보정 후보다.

**(2) 집합 술어 추출기가 `LEFT()`로 감싼 컬럼을 놓친다 — 도구 결함.**

`UP_UTIL_SETTLE_EXPECT_PROC`의 `LEFT(D.PayToolType,1) IN ('C')`(원본 `:146`)와
`IN ('A','B')`(`:168`)가 「집합 술어 (기계 확정 — 수정 금지)」 표에서 빠졌다.
같은 SP에서 `ISNULL(...)`로 감싼 컬럼(`ISNULL(A.InYMD,'')`, `ISNULL(C.UnCollectImpose,1)`)은
정상 캡처되므로, 함수로 감싼 컬럼을 원천적으로 못 담는 것이 아니라 `LEFT()` 한정 누락이다.
"수정 금지"를 붙인 표가 불완전하면 그 계약 자체가 약해진다.

**(3) 문장 번호 결함은 전건 해소됐다 (확인 사항).**

직전 판까지 UPDATE 절 제목의 문장 번호가 전건 `갱신 0`이던 결함이 이번 판에서 사라졌다.
네 SP의 단위가 각각 `StaticAnalysis.AstUpdateMappings`의 `GlobalStatementOrdinal`·`SourceLine`을
원본 DDL 순회 결과와 대조해 **45건 전부 일치**를 확인했다 —
`EXCEPTION_PROC` 18, `COMM_UPD` 15, `EXPECT_PROC` 11, `PROC_ETC` 1.

### 4-2. 실행으로 닫아야 하는 보류 2건

두 🔴은 T-SQL 런타임 동작에 판정이 달려 있었다. 각 단위가 자립 실행 쿼리를 함께 반환했고,
**2026-08-20 SSMS에서 실행한 결과 둘 다 반증되어 취소했다.**

**(a) `UF_GET_COLLECTYMD` — `+ +` 부호 중복**

```sql
DECLARE @base VARCHAR(6) = '202601';
SELECT (@base + + '01') AS result_value,
       SQL_VARIANT_PROPERTY(CAST(@base + + '01' AS SQL_VARIANT), 'BaseType') AS result_type;
```
취소 조건: `20260101` / `varchar`.
**실행 결과 `20260101` / `varchar`** — 이중 `+`는 산술 덧셈이 아니라 문자열 이어붙이기로 평가된다.
명세서의 "기준월 첫째 날 `YYYYMM01`을 만든다"는 서술이 옳았다. **이 행을 취소한다.**

**(b) `UP_UTIL_SETTLE_INS_EXTRA` — `ROUND` 세 번째 인자 `NULL`**

```sql
SELECT ROUND(1000, 0, NULL) AS r;
```
취소 조건: `NULL`이 아닌 값.
**실행 결과 `1000`** — `ROUND`의 세 번째 인자가 `NULL`이어도 결과가 `NULL`이 되지 않는다.
단위가 제시한 `NULL` 전파 경로는 성립하지 않는다. **🔴을 취소한다.**
다만 8/20 판이 담고 있던 「`NULL` 수수료 전파」 서술이 사라진 것은 별개 사실이고,
그 문장이 말하는 산술 `NULL` 전파(`+`·`-` 피연산자에 `NULL`이 있으면 결과가 `NULL`)는
`ROUND`와 무관하게 성립하므로 4절에 🟡로 남겼다.

## 4-3. 축 A 교차 — 명세서가 참조 함수를 다루는 자리

이번 감사에서 처음으로 「참조 함수 (기계 확정 — 수정 금지)」 표를 대조했다.
직전 판에는 이 표 자체가 없었고, 명세서는 함수를 산문으로 서술했다.

| 호출 객체 | 표 위치 | 행 수 | 판정 | 확인 내용 |
|---|---|---|---|---|
| `UP_UTIL_SETTLE_EXCEPTION_PROC` | `Spec.md:365` | 29 | 정합 | 18개 `UPDATE` 문 전수 순회로 함수별 호출 수 재현(9·7·3·2·2·6) = 29. 외부 DB 함수 5개 포함 링크 10개 실재 |
| `UP_UTIL_SETTLE_COMM_UPD` | `:358` | 23 | 정합 | DDL 실측 23건과 일치. 주석 처리된 티모넷 블록의 `UF_GET_CLIENTID4TMONET` 호출 2건이 표에서 정확히 제외됨 |
| `UP_UTIL_SETTLE_EXPECT_PROC` | `:263` | 9 | 정합 | 9건 일치. `UF_GET_OUTYMD4REFUND`가 같은 `CASE`의 조건절(`:227`)과 `ELSE`절(`:229`)에 각각 등장해 2행으로 분리된 것이 규칙과 부합 |
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | — | 6 | 정합 | 인라인 TVF `UIF_SettleYMD` 포함 6건 일치 |
| `UP_UTIL_SETTLE_INS_EXTRA` | `:259` | 5 | **결함** | 행·인자는 정합. 링크 1개의 대소문자가 정본과 어긋남(아래) |
| `UP_UTIL_SETTLE_INS` | `:179` | 3 | 정합 | 중첩 호출을 안·바깥 각각 1행으로 분리한 것이 규칙과 부합 |
| `UIF_SettleYMD` (함수) | 표 없음 | — | 정합 | 표가 없는 것이 정상(호출 2건이 전부 `SELECT` 안, 유일한 `INSERT`엔 없음). 3-2-1 사각지대 5항목 위반 없음 (⚪ 1) |
| `UF_GET_COLLECTYMD` (함수) | 표 없음 | — | 정합 | 표가 없는 것이 정상(DML 자체가 없음). `Spec.md:287-306`의 `UF_GET_WORKDAY2` 산문 5단계를 원본과 전수 대조, 정확 |

**교차 결함 1건**

| 등급 | 객체 | 원본 앵커 | 산출물 앵커 | 원본 | 산출물 | 영향 |
|---|---|---|---|---|---|---|
| 🟡 | `UP_UTIL_SETTLE_INS_EXTRA` | `output/Functions/dbo.UF_GET_WORKDAY2/` (정본 디렉터리) | `Spec.md:266` | 정본 디렉터리는 `dbo.UF_GET_WORKDAY2`. DDL 호출부(`:234`)의 `dbo.UF_Get_WorkDay2`는 T-SQL 대소문자 무시 표기일 뿐이다 | 링크가 `../../../Functions/dbo.UF_Get_WorkDay2/docs/Spec.md`. 같은 문서 `:372`의 「참조 코드 객체」 링크는 `dbo.UF_GET_WORKDAY2`로 정본을 써 문서 내부에서 불일치 | 대소문자 구분 파일시스템에서 깨지는 링크. **조립기 결함** — 근본 원인은 `metadata.json`의 `Dependencies[].Name`이 카탈로그가 아니라 호출식 표기(`'UF_Get_WorkDay2'`)를 담는 것이고, `AiService.BuildFunctionSpecRelativePath`가 그 값을 그대로 경로에 쓴다. 매니페스트의 `SpecPath`는 정본 표기라 그것을 쓰면 닫힌다 |

**⚪ 1건** — `UIF_SettleYMD`: `@v_intIdx = -1` 설정이 "기준일 자신의 휴일 여부를 검사하느냐"를
가르는 지점인데 그 귀결까지는 서술하지 않았다(`Spec.md:240-244`). 반환값·분기를 틀리게 적은 곳은 없다.

**표의 사각지대에서 본 것** — SP 6개는 모두 함수 호출이 `INSERT`/`UPDATE`/`DELETE` 문 안에만
있어 사각지대가 비어 있음을 각 단위가 DDL 전수 순회로 재확인했다(75/75). 함수 2개는 반대로
호출이 전부 DML 밖이라 표가 없고, 그 구간을 3-2-1절이 산문 대조로 덮었다.
**기계 확정 표의 존재가 `SELECT`·`SET`·`IF` 안의 호출까지 검증했다는 뜻은 아니다** —
함수 2개 단위의 산문 대조가 그 자리를 맡았고, SP 6개에는 그런 자리가 없었다.

**서술 금지 준수** — 8개 단위 전부가 표 밖에서 함수의 반환값·분기·필터·기본값을 서술한 곳을
찾지 못했다. 폐지된 「UDF 활용 규칙」류 산문 절도 남아 있지 않다. `COMM_UPD`의 `Spec.md:400`은
"해당 호출식의 값과 관련한 함수 동작은 참조 함수 명세서를 확인해야 합니다"라고 명시적으로
정본을 함수 `Spec.md`로 넘긴다.

## 5. 축 B 결함

미실행. 이번 감사는 축 A와 축 A 교차만 돌렸다.
현재 `output/Jobs/POQSettlePrco20/agent/`의 단계 지시서는 2026-08-19에 만들어진 것이라
이번에 재생성된 31개 `Spec.md`를 반영하지 않는다. 배치 Job을 다시 만든 뒤 돌려야 한다.

## 6. 이 감사가 보증하지 않는 것

- **축 B를 대조하지 않았다.** 단계 지시서가 `Spec.md`를 보존했는지는 이 보고서가 말하지 않는다.
- **실행 대조는 4-2절의 두 쿼리로 한정된다.** 그 둘은 사용자가 SSMS에서 돌려 닫았고 결과를
  4-2절에 적었다. 나머지 결함은 전부 정적 대조로만 판정한 것이며, 이 감사는 SP나 함수를
  실제로 실행해 산출 금액을 대조하지 않았다.
- **`ROUND(1000, 0, NULL)` 검증의 한계**: 정수 `1000`은 반올림하든 절사하든 결과가 같아,
  이 쿼리는 "결과가 `NULL`이 되는가"만 가른다(그 질문에는 충분하다). 세 번째 인자가 `NULL`일 때
  반올림과 절사 중 어느 쪽으로 동작하는지는 확인하지 않았다. 또 실행한 것은 리터럴 `NULL`이고
  원본은 타입이 있는 컬럼(`Y.CommRoundFlag`)의 `NULL`이다.
- **오류 코드 앵커의 줄 번호 수정은 산출물로 재확인할 수 없었다.** 그 앵커는 프롬프트 재료로만
  쓰이고 명세서 본문에 인용되지 않으며, `raw/prompt-context.md`는 이번 실행이 다시 쓰지 않았다
  (`UP_UTIL_SETTLE_COMM_UPD`의 그 파일은 2026-08-12자 그대로다). 수정 자체는 단위 테스트가 보증한다.
- **피호출 함수의 DDL을 교차 단위가 대부분 열지 않았다.** 계약대로 "금지된 서술을 실제로
  발견했을 때만" 열게 되어 있고, 8개 단위 중 2개(`UIF_SettleYMD`, `UF_GET_COLLECTYMD`)만 열었다.
- **`UP_UTIL_SETTLE_INS` 단위가 `metadata.json`의 `DdlText`·`RawPromptContext`·`DeconstructedLogic`을
  열지 않았다**(298KB). 계약이 지정한 대조 항목에 없어 건너뛴 것이다.
- **`UP_Util_PG_Client_CMRate_Ins`의 mermaid 다이어그램은 논리 흐름 대응만 확인했고**
  파서 수준의 구문 검증은 하지 않았다.
- **함수 결함의 "호출하는 SP 수"는 단위가 아니라 상위가 채웠다.** 단위는 세 파일만 읽어 셀 수
  없었고, 상위가 폐포 31개 객체의 DDL을 역산했다(2절 표).
- 이전 판과의 회귀 대조는 `UP_UTIL_SETTLE_INS`·`UP_UTIL_SETTLE_INS_EXTRA` 두 객체에 대해서만
  했다. 나머지 29개 객체가 이전 판 대비 무엇을 잃었는지는 확인하지 않았다.
