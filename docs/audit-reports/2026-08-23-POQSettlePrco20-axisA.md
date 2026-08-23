# POQSettlePrco20 산출물 정합성 감사 — 축 A 재감사 (9회차)

2026-08-23 · ③(b) 병합 후 **캐시 포맷 버전 13**으로 31개 객체 전건 재생성(08-23 17:47~18:32) 직후 · 축 A와 축 A 교차만 수행(축 B는 이번 지시 범위 밖) · 이전 판(8회차, `ConsistencyReport-20260822b.md`) 대조 포함

## 1. 판정

| 축 | 판정 | 단위 수 | 신규 검증 | 캐시 재사용 | 검증 불가 |
|---|---|---|---|---|---|
| A (객체) | **결함** — 정합 28 · 결함 3 | 31 (SP 14 · 로컬 함수 10 · 외부 함수 7) | 31 | 0 | 0 |
| A 교차 | **결함** — 정합 7 · 결함 1 | 8 | 8 | 0 | 0 |
| B | 수행하지 않음(지시 범위 밖) | — | — | — | — |

등급 합계(객체 + 교차): 🔴 0 · 🟠 1 · 🟡 3 · ⚪ 31. 8회차는 🔴 3 · 🟠 8 · 🟡 23 · ⚪ 19였다(1절 판정 표 기준, 보존본 `docs/audit-reports/2026-08-22b-POQSettlePrco20-axisA.md`). **🔴·🟠·🟡 34건 중 31건이 사라졌고, 🟠 1건은 회귀로 새로 났으며, 🟡 3건 중 2건은 새 자리, 1건은 자리가 같되 결함의 성격이 뒤집혔다** — 4-3절.

캐시는 39단위 전부 미스였다 — 31개 `Spec.md`·`metadata.json`이 오늘 v13으로 다시 만들어져 키의 해시가 전부 달라졌다. 8회차 캐시는 `.cache-20260822b.json`에 그대로 보존했고, 8회차의 보조 항목 넷(`axisA-hold:`·`axisA-recheck:`)은 이번 단위 집합 밖이라 새 캐시에서 뺐다(그 hold의 🟠·🟡 문구 둘은 재생성된 `UP_Util_Settle_Summary` 명세서에서 기계적으로 찾아 **사라졌음**을 확인했다 — 4-3절).

## 2. 검증 대상 확정

- **소비 명세서 집합 12개** — `agent/MigrationInstructions.md`의 `Spec.md` 링크 12개에서 읽었고, `raw/prompt-context.md`의 `^Filename:` 12행과 같다: `UP_Util_PG_Client_CMRate_Ins` · `UP_UTIL_SETTLE_INS` · `UP_UTIL_SETTLE_CANCEL_INS` · `UP_UTIL_SETTLE_EXCEPTION_PROC` · `UP_UTIL_SETTLE_COMM_UPD` · `UP_UTIL_SETTLE_EXPECT_PROC` · `UP_UTIL_SETTLE_INS_EXTRA` · `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` · `UP_UTIL_STAT_PGCOLLECT_INS` · `UP_Util_Settle_Summary` · `UP_UTIL_SETTLE_SUMMARY_ETC` · `UP_UTIL_SETTLE_PROC_ETC`.
- **참조 폐포 31개** — 12개 SP 각각의 `output/Procedures/[SP]/raw/dependency-manifest.json` `Nodes[]` 합집합(키는 4부 `Key`, 경로는 `SpecPath`·`DdlPath`를 객체 디렉터리 기준으로 풀었다). 구성: SP 14(소비 12 + 중첩 2 = `UP_Util_Settle_Summary_AcqManual`·`UP_UTIL_SETTLE_SUMMARY_EXTRA`) · 로컬 함수 10 · 외부 DB(`SETTLE_CARD_DB`) 함수 7. 31개 모두 `Status: Succeeded`, `Spec.md`·`object_definition.sql`·`metadata.json` 실재, `output/.sp_cache_index.json`의 `FormatVersion` 전부 **13**.
- **폐포에만 있는 SP 2개**는 `UP_Util_Settle_Summary`가 `EXEC`로 부르는 하위 SP다. 단계 흡수 여부는 축 B의 일이라 이번 판정 밖이고, 축 A 대상에는 포함했다.
- **교차 대상 8개** — `### 참조 함수 (기계 확정 — 수정 금지)` 표를 가진 객체 전부. SP 6(`SETTLE_INS`·`EXCEPTION_PROC`·`COMM_UPD`·`EXPECT_PROC`·`INS_EXTRA`·`INS_EXTRA4PLCARD`) + 함수 2(`UF_GET_COLLECTYMD`·`UIF_SettleYMD`). 8회차와 같은 8개지만 **함수 둘은 이번에 처음으로 표를 가졌다** — ③(b)가 참조 함수 표를 독립 SELECT·IF 술어 안 호출까지 넓힌 결과이며, 4-2절에서 그 사실을 확인했다.

## 3. 단위별 커버리지

| 축 | 단위 | 종류 | 판정 | 상태 | 근거 파일 |
|---|---|---|---|---|---|
| A | `dbo.UP_Util_PG_Client_CMRate_Ins.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_Util_PG_Client_CMRate_Ins.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_Util_PG_Client_CMRate_Ins/docs/Spec.md` + `output/Procedures/dbo.UP_Util_PG_Client_CMRate_Ins/raw/metadata.json` |
| A | `dbo.UF_GET_INCVTAXRATE.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_INCVTAXRATE.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_GET_INCVTAXRATE/docs/Spec.md` + `output/Functions/dbo.UF_GET_INCVTAXRATE/raw/metadata.json` |
| A | `dbo.UF_GET_ROUND4VAT.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_ROUND4VAT.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md` + `output/Functions/dbo.UF_GET_ROUND4VAT/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_INS.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_INS.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_INS/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_INS/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_CANCEL_INS.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_CANCEL_INS.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_CANCEL_INS/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_CANCEL_INS/raw/metadata.json` |
| A | `dbo.UF_GET_COMM4CLIENT.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT.Function/raw/object_definition.sql` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT/docs/Spec.md` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT/raw/metadata.json` |
| A | `dbo.UF_GET_COMM4CLIENT4INTEREST.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT4INTEREST.Function/raw/object_definition.sql` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4INTEREST/docs/Spec.md` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4INTEREST/raw/metadata.json` |
| A | `dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function/raw/object_definition.sql` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL/docs/Spec.md` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL/raw/metadata.json` |
| A | `dbo.UF_GET_COMM4PG.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4PG.Function/raw/object_definition.sql` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG/docs/Spec.md` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG/raw/metadata.json` |
| A | `dbo.UF_GET_COMM4PG4INTEREST.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4PG4INTEREST.Function/raw/object_definition.sql` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG4INTEREST/docs/Spec.md` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG4INTEREST/raw/metadata.json` |
| A | `dbo.UF_Get_CLComm4MobileCo.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_Get_CLComm4MobileCo.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_Get_CLComm4MobileCo/docs/Spec.md` + `output/Functions/dbo.UF_Get_CLComm4MobileCo/raw/metadata.json` |
| A | `dbo.UF_GET_CLIENTSECTIONRATE.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_CLIENTSECTIONRATE.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_GET_CLIENTSECTIONRATE/docs/Spec.md` + `output/Functions/dbo.UF_GET_CLIENTSECTIONRATE/raw/metadata.json` |
| A | `dbo.UF_GET_PGCommOption.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_PGCommOption.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_GET_PGCommOption/docs/Spec.md` + `output/Functions/dbo.UF_GET_PGCommOption/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/raw/metadata.json` |
| A | `dbo.UF_GET_SETTLE_EXCHANGERATE.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_SETTLE_EXCHANGERATE.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_GET_SETTLE_EXCHANGERATE/docs/Spec.md` + `output/Functions/dbo.UF_GET_SETTLE_EXCHANGERATE/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/raw/metadata.json` |
| A | `dbo.UF_GET_COLLECTYMD.Function` | 함수 | 결함 | 신규 | `output/Objects/dbo.UF_GET_COLLECTYMD.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_GET_COLLECTYMD/docs/Spec.md` + `output/Functions/dbo.UF_GET_COLLECTYMD/raw/metadata.json` |
| A | `dbo.UF_GET_OUTYMD4REFUND.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_OUTYMD4REFUND.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_GET_OUTYMD4REFUND/docs/Spec.md` + `output/Functions/dbo.UF_GET_OUTYMD4REFUND/raw/metadata.json` |
| A | `dbo.UF_GET_WORKDAY2.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_WORKDAY2.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_GET_WORKDAY2/docs/Spec.md` + `output/Functions/dbo.UF_GET_WORKDAY2/raw/metadata.json` |
| A | `dbo.UIF_SettleYMD.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UIF_SettleYMD.Function/raw/object_definition.sql` + `output/Functions/dbo.UIF_SettleYMD/docs/Spec.md` + `output/Functions/dbo.UIF_SettleYMD/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/raw/metadata.json` |
| A | `dbo.UF_Get_ExtraCardCommissionAmt.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_Get_ExtraCardCommissionAmt.Function/raw/object_definition.sql` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_Get_ExtraCardCommissionAmt/docs/Spec.md` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_Get_ExtraCardCommissionAmt/raw/metadata.json` |
| A | `dbo.UF_GET_EXTRACOMM4CLIENT.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_EXTRACOMM4CLIENT.Function/raw/object_definition.sql` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_EXTRACOMM4CLIENT/docs/Spec.md` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_EXTRACOMM4CLIENT/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure` | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD/raw/metadata.json` |
| A | `dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_STAT_PGCOLLECT_INS/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_STAT_PGCOLLECT_INS/raw/metadata.json` |
| A | `dbo.UP_Util_Settle_Summary.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_Util_Settle_Summary.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_Util_Settle_Summary/docs/Spec.md` + `output/Procedures/dbo.UP_Util_Settle_Summary/raw/metadata.json` |
| A | `dbo.UP_Util_Settle_Summary_AcqManual.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_Util_Settle_Summary_AcqManual.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_Util_Settle_Summary_AcqManual/docs/Spec.md` + `output/Procedures/dbo.UP_Util_Settle_Summary_AcqManual/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_SUMMARY_ETC.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_ETC.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_ETC/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_ETC/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/raw/metadata.json` |
| A 교차 | `dbo.UP_UTIL_SETTLE_INS.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json` + 피호출 함수 DDL(사각지대) |
| A 교차 | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json` + 피호출 함수 DDL(사각지대) |
| A 교차 | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json` + 피호출 함수 DDL(사각지대) |
| A 교차 | `dbo.UF_GET_COLLECTYMD.Function` | 교차 | 결함 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json` + 피호출 함수 DDL(사각지대) |
| A 교차 | `dbo.UIF_SettleYMD.Function` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json` + 피호출 함수 DDL(사각지대) |
| A 교차 | `dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json` + 피호출 함수 DDL(사각지대) |
| A 교차 | `dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json` + 피호출 함수 DDL(사각지대) |
| A 교차 | `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json` + 피호출 함수 DDL(사각지대) |

객체 31 + 교차 8 = 39단위, 1절과 일치. 검증 불가 0.

## 4. 축 A 결함

🔴·🟠·🟡만 싣는다. ⚪(정보)는 4-1절에 부류별로 접었다.

| 등급 | 객체 | 원본 앵커 | 산출물 앵커 | 원본 | 산출물 | 영향 |
|---|---|---|---|---|---|---|
| 🟠 | `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure` | object_definition.sql:21 | Spec.md:87 | IF EXISTS(라인 21)·DELETE 1(라인 37)·INSERT 1 파생 X의 P(라인 167)·UPDATE 1(라인 190)·UPDATE 2(라인 206) 다섯 문장 모두 `INNER JOIN TPGProperty ... ON A.PGName = PG.PGName AND PG.ExtraType IN (2,3)`으로 ExtraType 2·3인 PG의 행만 대상으로 한다 | Spec.md 전체에 리터럴 `ExtraType IN (2,3)`(또는 `2,3`)이 한 번도 없다. 라인 87은 '차액정산 관리 유형을 연결합니다', 라인 283 사전 검증 산문은 '`TPGProperty PG`를 결합해'라고만 쓰고, 라인 75 `-9` 조건 목록에도 없다. `DML 범위` 표(라인 175-178)는 조인 키 `PGName, ExtraType`만 싣고 `집합 술어` 표(라인 186-205)는 WHERE만 담아 ON 절 리터럴을 싣지 않는다 | ON 절 리터럴은 기계 확정 표가 소유하지 않으므로 DDL 원문이 기준값이다. 명세서대로 이행하면 사전 검증·DELETE 1·UPDATE 1·UPDATE 2의 대상 행 집합과 INSERT 1의 원천 행 집합이 ExtraType 무관한 전체 PG로 넓어진다(정산 마스터의 일반 PG 행까지 삭제·부호 반전·TOTAL 재계산 대상이 됨) — 복원 단서는 DDL 라인 21·37·167·190·206 뿐이며 명세서 어디에도 없다 |
| 🟡 | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | object_definition.sql:393-394, object_definition.sql:416 | Spec.md:34 | @pi_strYMD는 TSettleMst.YMD(A/AA/Y/무한정 YMD)와만 비교된다. TPLCardTxMst.YMD(B.YMD)는 393-394행에서 UF_GET_COMM4CLIENT4PARTIALCANCEL의 인자로만 쓰이고, TClientSettleRate4MobileCo.YMD는 416행 `A.AYMD = B.YMD`로 승인일(AYMD)에 결합되며 정산일 파라미터와는 연결되지 않는다 | 「파라미터와 변수의 컬럼 관계」 표가 @pi_strYMD의 연결 컬럼으로 `TPLCardTxMst.YMD`, `TClientSettleRate4MobileCo.YMD`를 나열한다 | 추적성 오기. TClientSettleRate4MobileCo를 정산일(@pi_strYMD)로 거르는 것으로 오독할 여지가 있으나, DML 범위 표(UPDATE 15 조인 키 AYMD, YMD)와 집합 술어 표(416행 `A.AYMD = B.YMD`)가 실제 술어를 보존하므로 표기 수준 결함으로 본다 |
| 🟡 | `dbo.UF_GET_COLLECTYMD.Function` | object_definition.sql:53 | Spec.md:93 | dbo.UF_GET_WORKDAY2(@pi_strYMD, CollectDay) 호출(라인 53·78) — 이 함수는 `### 참조 함수 (기계 확정 — 수정 금지)` 표(Spec.md:142-143)에 두 행으로 실려 있다 | "제공된 함수 정의에 따르면 기준일에서 지정 간격을 이동하되 `dbo.THoliday`에 존재하는 날짜를 확인하여 영업일을 계산합니다. 따라서 이 함수의 호출 경로는 `THoliday`를 간접적으로 반복 조회할 수 있으며…" — 표에 실린 참조 함수의 내부 동작(이동·휴일 확인 반복)을 산문으로 서술 | 호출 SP 1개 · 표에 실린 함수의 동작 서술 금지(axis-a 3-2) 위반. 서술 내용은 Dependencies.ReferencedDdlText의 UF_GET_WORKDAY2 원본(WHILE 루프 + IF EXISTS THoliday)과 어긋나지 않으므로 금액 영향은 없고 정본이 둘로 갈리는 추적성 결함이다. 이 함수를 부르는 SP 수: 1 |

**🟠 `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` — `PG.ExtraType IN (2,3)` 증발은 회귀다.** 08-22 백업 명세서(`output.bak-2026-08-22/…/Spec.md`)에는 이 리터럴이 149·161·167행 산문 세 곳에 있었다. 오늘 v13 재생성본에는 한 번도 없다. 원인은 둘이 겹쳤다 — (1) 2026-08-23 C(버전 12)가 CRUD·SELECT 대상 표 **설명 칸의 술어 서술을 금지**해 167행 자리가 사라졌고, (2) 그 사실을 받아 줄 기계 확정 표가 없다: `집합 술어` 표는 **WHERE 절의 IN/=/<> 리터럴만** 싣고 JOIN **ON 절**의 리터럴은 싣지 않으며, `DML 범위` 표의 조인 키 칸은 `PGName, ExtraType`처럼 **컬럼 이름만** 적는다. 즉 설명 칸이라는 틀릴 수 있는 자리를 없애면서, 그 자리가 유일하게 품고 있던 사실을 표로 올리지 않았다. 폐포 31개 DDL을 기계적으로 훑으면 ON 절 리터럴 술어는 이 SP의 5문장(21·37·167·190·206행)뿐이다(`EXPECT_PROC:210`의 `ABS(IIF(...)) = ABS(E.Amt)`는 컬럼 대 컬럼 식이라 조인 키 칸이 보존한다). 노출은 한 객체지만 **부류는 구조적**이다 — ON 절에 리터럴을 두는 SP가 다음 Job에 있으면 같은 일이 난다. 처방은 명세서가 아니라 도구다: `집합 술어` 표(또는 `DML 범위` 표의 조인 키 칸)가 ON 절 리터럴 술어를 문장별로 싣게 한다.

**🟡 `UF_GET_COLLECTYMD`(객체·교차 같은 자리, Spec.md:93)** — 8회차에는 같은 문단(당시 108행)이 '동작 서술이 **부족하다**'(🔴 간격 0 특례 없음·🟡 부호 방향 없음)는 결함이었다. 이번에는 `UF_GET_WORKDAY2`가 참조 함수 표에 두 행으로 실렸으므로 그 문단은 '**있으면 안 되는** 동작 서술'이 됐다(3-2절 서술 금지). 내용은 피호출 DDL과 어긋나지 않아 🟡다. 객체 단위와 교차 단위가 같은 줄을 각자 잡았고 판정은 같다 — 건수로는 하나다.

**🟡 `UP_UTIL_SETTLE_EXCEPTION_PROC` Spec.md:34** — 「파라미터와 변수의 컬럼 관계」 표가 `@pi_strYMD`의 연결 컬럼으로 `TPLCardTxMst.YMD`·`TClientSettleRate4MobileCo.YMD`를 적는데, 전자는 함수 인자로만 쓰이고 후자는 `A.AYMD = B.YMD`(승인일)로 결합된다. 기계 확정 표 두 곳(DML 범위 UPDATE 15 조인 키 `AYMD, YMD`·집합 술어 416행)이 실제 술어를 보존하므로 추적성이다. 8회차에 없던 새 자리다.

### 4-1. 전 객체 공통 결함 (⚪ 부류별 · 보류)

⚪ 31건과 보류 15건을 원인별로 접는다. 모두 금액·행 집합 영향이 없다고 단위가 판정했다.

| 부류 | 객체 | 실제 | 소속 |
|---|---|---|---|
| **(A) DML 범위 표 아래 고정 상용구가 객체와 맞지 않음** | `UF_GET_COMM4PG4INTEREST:97` · `UP_Util_PG_Client_CMRate_Ins:244` · `UP_UTIL_SETTLE_SUMMARY_ETC:166` · `UP_Util_Settle_Summary_AcqManual:130` (4객체 ⚪) | "`기준일 파라미터 적용` 칸의 `아니오`는 … 하위 질의·파생 테이블 안에서 기준일을 쓰는 문장이 있으므로"를 `AiService.cs` `BuildDmlScopeTableLines`가 **모든 객체에** 붙인다. 하위 질의가 없거나 기준일 파라미터 자체가 없는 객체에서는 거짓 문장이다 | 도구(프롬프트 조립기). 조건부로 붙이면 닫힌다 |
| **(B) `기준일 파라미터 적용` 칸이 함수·SELECT 행에서 `—`** | `UF_GET_OUTYMD4REFUND:79` · `UF_GET_COMM4CLIENT4PARTIALCANCEL` · `UF_Get_ExtraCardCommissionAmt` · `UF_Get_CLComm4MobileCo:78` · `UF_GET_EXTRACOMM4CLIENT:104`(⚪) · `UP_UTIL_SETTLE_SUMMARY_EXTRA:246` · `UP_UTIL_SETTLE_PROC_ETC`(보류 7건) | 최상위 WHERE에 `YMD = @pi_strYMD`류가 있는데도 칸이 `—`다. 단위들은 '이 칸이 SP 전용인지·SELECT 행은 계산하지 않는 규약인지'를 코드로 확인할 수 없어 보류했다. 집합 술어 표가 같은 술어를 문장별로 실어 사실은 보존된다 | 도구 규약 확인 사항(`DmlScopeExtractor`의 기준일 판정 범위). 결함으로 세지 않았다 |
| **(C) 실행 의미 표에 `@@ROWCOUNT`·비집계/집계 대입 행이 없는 자리** | `UF_GET_COMM4CLIENT4PARTIALCANCEL:148`(⚪) · `UF_Get_ExtraCardCommissionAmt` · `UF_GET_EXTRACOMM4CLIENT` · `UIF_SettleYMD` · `UP_UTIL_SETTLE_PROC_ETC`(보류) | `IF @@ROWCOUNT …`가 SELECT 대입 직후인 자리, IIF/CASE 식 대입, `MAX(ID)+1` 같은 집계식 대입에 행이 없다. `UF_GET_CLIENTSECTIONRATE` 단위는 `RowCountBoundaryExtractor`가 직전 형제가 IF인 자리만 잡는다고 읽어 정상으로 봤고, 다른 단위들은 규칙을 못 읽어 보류했다. 산문은 전부 원본과 맞다 | 도구 규약 확인 사항(추출기 적용 범위). 결함 아님 |
| **(D) 「파라미터 목록」 표에 지역 변수 행** | `UP_UTIL_SETTLE_EXPECT_PROC:41`(`@v_PLCardSettlePeriodPG`) · `UP_UTIL_SETTLE_COMM_UPD:83`(`@v_valIncVat`) | 구분 칸에 '지역 변수'라 명시돼 있어 오독은 없으나 표 제목과 내용이 어긋난다 | 명세서 표기. 프롬프트가 파라미터 표의 행 출처를 `ProcedureParameters`로 못 박으면 닫힌다 |
| **(E) 산문 요약이 조건·순서·열거를 줄임** | `UF_GET_CLIENTSECTIONRATE:47`(ISNULL 생략) · `UF_GET_COMM4CLIENT:188`(연산 순서) · `UF_GET_COMM4PG:87` · `UF_GET_COLLECTYMD:75`(변수 열거 누락) · `UP_UTIL_SETTLE_INS:335`(필터 열거 불균형) · `UP_UTIL_SETTLE_INS:127` · `UP_UTIL_SETTLE_SUMMARY_EXTRA:349`(GROUP BY 키 생략) · `UP_UTIL_SETTLE_INS_EXTRA:369`(위험 서술의 전제 오류) · `UP_UTIL_SETTLE_INS_EXTRA4PLCARD:289`(문단 위치) · `UP_Util_Settle_Summary:300`(하위 SP 동작 단정) · `UP_UTIL_SETTLE_PROC_ETC:225`(문장 중복)·`:64` | 전부 기계 확정 표 또는 같은 문서의 원문 블록이 사실을 확정하고 있어 ⚪ | 명세서 산문 품질. 표가 덮는 자리라 재생성 없이 둬도 이행 결과는 같다 |
| **(F) mermaid가 코드 구조를 다르게 그림** | `UF_GET_ROUND4VAT:95`(암시적 NULL/범위 검사를 노드로) · `UP_UTIL_SETTLE_EXPECT_PROC:346`(FAIL 노드 문장 순서) · `UP_UTIL_STAT_PGCOLLECT_INS:243`(UNION 분기 선행 간선 없음) | 산문과 표는 맞다 | 명세서 도식 품질 |
| **(G) 파서 귀속·추출기 전사 한계를 명세서가 그대로 옮김** | `UP_UTIL_SETTLE_EXCEPTION_PROC:122`(`TPGProperty`에 `PLTID, ID` — 파생 테이블 X의 비한정 컬럼 과잉 귀속) · `UF_Get_CLComm4MobileCo:133`(CASE ELSE 원문이 공백 접힘으로 `--` 주석 뒤에 FROM/WHERE가 한 줄에 이어짐) · `UIF_SettleYMD`·`UF_GET_COLLECTYMD`(동일 파생 테이블 정의 두 문장이 표 1행으로 합쳐짐 — 보류) | 계약상 파서가 이기므로 명세서 결함이 아니다. `EXCEPTION_PROC`의 `PLTID, ID`는 8회차 🟡였던 것이 재생성 뒤에도 같은 자리에 남아 **파서 쪽**으로 확정됐다(4-3절·`known-defects` (가)) | 도구(파서의 파생 테이블 컬럼 귀속 · `TextOf`의 인라인 주석 토큰) |
| **(H) 유보·범례 서술** | `UF_GET_COMM4CLIENT4INTEREST:37`(결정성 '확인할 수 없습니다' 유보 — 같은 줄 앞 문장이 확정 서술) · `UF_GET_SETTLE_EXCHANGERATE:77·119`(DDL 주석 범례와 코드/메타데이터 설명이 다른 것을 코드 쪽으로 옮기고 불일치는 짚지 않음) | 정보 | 명세서 산문 |

**보류 중 도구 계약 문서에 관한 것** — 단위 넷(`UF_GET_WORKDAY2`·`UP_UTIL_SETTLE_EXCEPTION_PROC`·`UP_Util_Settle_Summary_AcqManual`·`UP_UTIL_SETTLE_INS_EXTRA4PLCARD`)이 같은 사실을 따로 적었다: **잠금 힌트 표가 `IF EXISTS` 하위 질의·커서 SELECT·최상위 WHERE 하위 질의의 스캔 행을 싣고 있다.** 행 내용은 전부 DDL과 일치하므로 결함이 아니지만, `axis-a.md`는 그 범위를 '표 밖'이라고 적고 있다 — ③(b)가 표를 넓힌 뒤 **계약 문서가 도구보다 낡았다.** `UIF_SettleYMD` 단위도 실행 의미 표의 `비집계 대입` 종류가 `axis-a.md`의 다섯 종류 열거에 없다고 적었다. 감사 계약 문서 갱신 항목이다.

### 4-2. 축 A 교차 — 명세서가 참조 함수를 다루는 자리

| 호출 객체 | 표 위치 | 표 행 수 | DDL 호출 수 | 행별 판정 | 표 밖 호출 | 피호출 DDL 개봉 |
|---|---|---|---|---|---|---|
| `dbo.UP_UTIL_SETTLE_INS.Procedure` | Spec.md:220-222 | 3 | 3 | 3/3 정합 | 없음 | 아니오 |
| `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | Spec.md:423-451 | 29 | 29 | 29/29 정합 (함수 10종 · 링크 10개 실재 · 외부 DB 5종 포함) | 없음 (`STRING_SPLIT`은 내장) | 아니오 |
| `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | Spec.md:406-428 | 23 | 23 | 23/23 정합 (UPDATE 1·2·5·7·8·9·15) | 없음 (`UF_GET_CLIENTID4TMONET`은 주석 블록 — Spec.md:69·75가 명시) | 아니오 |
| `dbo.UF_GET_COLLECTYMD.Function` | Spec.md:142-143 | 2 | 2 | 2/2 정합 (SELECT 1 라인 53·78 — **독립 SELECT 대입문 안 호출이 표에 실림**) | 없음 | 예 — Spec.md:93 동작 서술이 원본과 어긋나지 않음을 확인 → 🟡 |
| `dbo.UIF_SettleYMD.Function` | Spec.md:137-138 | 2 | 2 | 2/2 정합 (SELECT 1 라인 61·86, CASE 식 안) | 없음 (`UIF_SettleNextYMD`는 주석 처리 — Spec.md:223·227) | 아니오 |
| `dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure` | Spec.md:248-256 | 9 | 9 | 9/9 정합 (인라인 TVF `UIF_SettleYMD` 포함) | 없음 | 아니오 |
| `dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure` | Spec.md:277-281 | 5 | 5 | 5/5 정합 | 없음 (`UF_GET_CLIENTID4TMONET` 312행은 주석 — Spec.md:74) | 아니오 |
| `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure` | Spec.md:213-218 | 6 | 6 | 6/6 정합 (외부 DB 2종 포함 · 링크 5개 실재) | 없음 | 아니오 |

여덟 표 79행 전부 함수명·호출 위치·인자 원문·명세서 링크가 DDL과 일치했고, 8회차에 있던 교차 🔴 1(`COLLECTYMD`의 간격 0 특례)·🟡 2는 모두 사라졌다 — `UF_GET_WORKDAY2`가 두 함수의 표에 실리면서 동작 서술이 표의 링크로 위임됐기 때문이다. 남은 교차 🟡 1건은 그 위임이 덜 된 산문 한 문단(4절)이다. **8회차까지 '함수 객체는 표가 0/4'라던 실측이 뒤집혔다** — ③(b)가 독립 SELECT·IF 술어 안 호출을 표로 올린 결과로, `axis-a.md` 3-2-1절의 '함수 0/2' 서술은 더 이상 사실이 아니다. 사각지대(표 밖 호출)는 여덟 객체 모두 0건이어서 피호출 DDL은 `UF_GET_COLLECTYMD` 단위 하나만 열었다(서술 금지 위반의 내용 대조용).

### 4-3. 이전 판(8회차) 대조

8회차 1절 기준 🔴 3 · 🟠 8 · 🟡 23 · ⚪ 19 → 9회차 🔴 0 · 🟠 1 · 🟡 3 · ⚪ 31. 단위별로 8회차 캐시(`.cache-20260822b.json`)의 등급 열과 이번 등급 열을 맞댔다.

| 객체 | 8회차 | 9회차 | 무엇이 닫혔나 / 남았나 |
|---|---|---|---|
| `UF_GET_COLLECTYMD` (객체) | 🔴🟡 | 🟡 | 🔴 간격 0 특례·🟡 부호 방향 → 표 위임으로 소멸. 남은 🟡은 같은 문단의 **과잉 서술**(성격 반전) |
| `UF_GET_COLLECTYMD` (교차) | ⚪⚪ | ⚪🟡 | 위와 같은 줄 |
| `UIF_SettleYMD` (객체·교차) | ⚪🟡 / 🟡 | ⚪ / — | NOLOCK 뭉갬 🟡·WORKDAY2 동작 서술 🟡 → 잠금 힌트 표·참조 함수 표로 소멸 |
| `UF_GET_COMM4CLIENT` | 🔴 | ⚪ | mermaid 분기 뒤집힘 🔴 → 소멸 (u06: 객체 선언~실행 의미 다섯 표 전수 일치) |
| `UF_GET_COMM4CLIENT4PARTIALCANCEL` | 🟡 | ⚪ | 2차 조회 다중 행 비결정성 🟡 → 소멸 — `known-defects` (나) 1/3 |
| `UF_GET_COMM4PG4INTEREST` | 🟠 | ⚪ | NULL 허용 단정 🟠 → 소멸(`NullabilityClaimMismatch` L1 + 재료) |
| `UF_GET_WORKDAY2` | ⚪🟡 | — | NULL 파라미터 둘 묶음 🟡 → 소멸 — `known-defects` (나) 2/3 |
| `UP_UTIL_SETTLE_COMM_UPD` | 🟠🟠🟡🟡🟡🟡 | ⚪⚪ | `A.YMD = A.AYMD`·`A.CYMD > A.AYMD` 🟠 둘 → 집합 술어 표 문장별 확정 + 설명 칸 술어 금지로 소멸. `SeperateAmt`… 참조 컬럼 🟡 → 소멸 — `known-defects` (가) 1/2. D의 `GROUP BY PLTID` 🟡 → 파생 테이블 표로 소멸 — (나) 3/3. NOLOCK·주석 전수 🟡 → 「주석은 전수가 아니다」 계약으로 결함 아님 |
| `UP_UTIL_SETTLE_EXCEPTION_PROC` | 🟠×5 🟡×3 | ⚪🟡 | `20230101`·OR 결합·`MALLID` 설명 칸·KFTC 최소수수료·부등식 🟠 다섯 → 전부 소멸(집합 술어 표 확장·설명 칸 금지). `TPGProperty PLTID·ID` 🟡 → **같은 자리에 남았고 단위가 파서 귀속으로 판정**(⚪) — `known-defects` (가) 2/2는 **파서 쪽**으로 확정. 새 🟡 1(파라미터-컬럼 관계 표) |
| `UP_UTIL_SETTLE_INS` | 🟡🟡 | ⚪⚪⚪ | 주석 3행 누락·비활성 블록 미표시 🟡 → 계약상 결함 아님으로 재분류 |
| `UP_UTIL_SETTLE_INS_EXTRA` | 🟡🟡🟡 | ⚪ | `X.PRODUCTNAME` 스키마 단정 🟡 → 소멸(입력원 ⑤). 주석 누락 🟡 둘 → 결함 아님 |
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | ⚪🟡 | ⚪🟠 | 스키마 단정 🟡 → 소멸. **🟠 신규 — 회귀**(4절) |
| `UP_UTIL_SETTLE_PROC_ETC` | 🔴🟡 | ⚪⚪ | 루프 내 0 재설정 🔴 → 실행 의미 표(루프 내 변수 재설정)로 소멸. 커서 정렬 🟡 → 소멸 |
| `UP_UTIL_SETTLE_SUMMARY_EXTRA` (객체 + hold) | ⚪⚪🟡 / 🟠🟡 | ⚪ | `TSetTleByOUT` 오타 🟡 → 소멸. hold의 🟠(술어 뭉갬)·🟡(4000~4008 연속 범위 서술) → `UP_Util_Settle_Summary` 300행에서 해당 문구 **소멸**(기계 확인) |
| `UP_UTIL_STAT_PGCOLLECT_INS` | 🟡🟡 | ⚪ | 표 구분행 칸 수 불일치 🟡 둘 → 소멸(`MachineTableShapeBroken` L1) |
| `UP_Util_PG_Client_CMRate_Ins` | 🟡 | ⚪ | NOLOCK 미언급 🟡 → 잠금 힌트 표(`IF 1` 행 포함)로 소멸 |

**`known-defects.md` 「돌려 봐야 아는 것」의 미귀속 🟡 5건 판정** — (가) 2건: `COMM_UPD` 참조 컬럼 셋은 **안 났다**; `EXCEPTION_PROC` `TPGProperty PLTID·ID`는 **같은 자리에 다시 났고** 단위가 "파서가 파생 테이블 X의 비한정 컬럼을 물리 테이블에 귀속한 것을 명세서가 그대로 옮김"으로 판정해 ⚪로 내렸다 → 그 행이 예고한 대로 **파서의 파생 테이블 컬럼 귀속을 고칠 자리**다. (나) 3건: 셋 다 **안 났다**(②·③(b)가 닫음). 따라서 그 행은 지우되, (가)-`EXCEPTION_PROC`만 파서 항목으로 옮겨 적는다.

## 5. 축 B 결함

수행하지 않았다(지시 범위 밖). 8회차와 같다.

## 6. 이 감사가 보증하지 않는 것

- **축 B 전체** — 계획서·단계 지시서와 `Spec.md`의 대조는 하지 않았다. 폐포에만 있는 SP 2개가 단계에 흡수됐는지도 판정하지 않았다.
- **실행 대조** — 어떤 SQL도 실행하지 않았다. 모든 판정은 DDL·`Spec.md`·`metadata.json`의 정적 대조다.
- **4-1절 (B)·(C)의 도구 규약** — `기준일 파라미터 적용` 칸의 판정 범위와 실행 의미 추출기들의 적용 범위를 **코드로 확인하지 않았다**. 단위들이 보류한 15건은 그 범위가 좁아서 정상인지, 행이 빠진 것인지 이 감사로는 가를 수 없다. 전부 집합 술어 표나 산문이 사실을 보존하고 있어 이행 결과에는 영향이 없다고 판정했다.
- **교차 8객체의 피호출 함수 DDL** — 사각지대가 0건이라 `UF_GET_COLLECTYMD` 한 단위만 열었다. 나머지 일곱의 표 행 판정은 호출 객체 DDL·메타데이터·링크 실재 여부만으로 했다.
- **ON 절 리터럴 노출 조사(4절)** — 줄 단위 정규식으로 31개 DDL을 훑은 근사치다. 여러 줄에 걸친 ON 절이나 괄호로 감싼 조건은 놓쳤을 수 있다. `EXCEPTION_PROC`·`COMM_UPD`처럼 ON 절이 긴 SP에서 0건으로 나온 것을 전수 보증으로 읽지 말 것.
- **파서 쪽으로 확정한 (가)-`EXCEPTION_PROC`** — 파서 코드를 열어 귀속 규칙을 확인하지는 않았다. "재생성 뒤 같은 자리에 남았다"와 단위의 DDL 대조(라인 60-61의 비한정 `PLTID`·`ID`가 파생 X의 컬럼)가 근거의 전부다.
- **이전 판 대조의 한계** — 8회차 캐시의 결함 행과 이번 결함 행을 객체 단위 등급 열로 맞댔고, 사라진 결함 각각의 '사라진 이유'는 단위 note와 설계 문서의 처방을 이어 붙인 추정이다. 본문이 정말 바뀌어 사라졌는지를 결함 34건 전부에 대해 줄 단위로 다시 확인하지는 않았다(`SUMMARY_EXTRA` hold 2건과 `INS_EXTRA4PLCARD` 회귀 1건만 원문 grep으로 확인).
- **캐시 버전 13과 다음 재생성** — 이 판정은 오늘 18:32까지의 산출물에 대한 것이다. 프롬프트 계약이 다시 바뀌어 전건이 재생성되면 39단위가 전부 다시 미스가 난다.
