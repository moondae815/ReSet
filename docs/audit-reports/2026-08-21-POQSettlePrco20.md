# POQSettlePrco20 산출물 정합성 감사

> 실행: 2026-08-21 · 축 A + 축 A 교차. 축 B는 미실행.
> 직전 보고서는 `ConsistencyReport-20260820b.md`, 그 이전은 `-20260820a.md`·`-20260819.md`.
>
> **이번 실행의 성격**: 추출기 결함 셋을 고친 뒤(main `35dfc74`) 재료가 실제로 바뀐
> SP 3개(`EXCEPTION_PROC`·`EXPECT_PROC`·`INS_EXTRA`)만 재생성했다. 그 폐포 17개가 새 산출물이고,
> 나머지 14개는 8/20자 그대로라 캐시가 적중했다.

## 1. 판정

| 축 | 판정 | 단위 수 | 신규 검증 | 캐시 재사용 | 검증 불가 |
|---|---|---|---|---|---|
| A (객체) | **결함** | 31 | 17 | 14 | 0 |
| A 교차 | **정합** | 8 | 5 | 3 | 0 |
| B (단계) | **미실행** | — | — | — | — |

결함 등급 합계: **🔴 0 · 🟠 1 · 🟡 9 · ⚪ 0**

`INS_EXTRA`의 `NOLOCK` 게이트 건은 단위가 배포 구성 전제를 적고 🔴로 올렸으나,
2026-08-21 사용자 확인으로 **이 배치가 단독 실행**임이 밝혀져 그 전제가 성립하지 않는다.
상위가 전제를 닫고 🟡로 내렸다(4-2절).

### 1-1. 직전 실행과의 대비

| | 8/19 | 8/20 | **8/21** |
|---|---|---|---|
| 🔴 | 7 | 1 | **0** |
| 🟠 | 4 | 0 | **1** |
| 🟡 | 59 | 12 | **9** |
| ⚪ | 43 | 1 | **0** |

**🔴이 사라졌고 축 A 교차가 8/8 정합이 됐다.** 기계 확정 「참조 함수」 표를 도입한 뒤
두 번째 실행이고, 직전에 남아 있던 🟡 1건(링크 대소문자)과 ⚪ 1건이 모두 닫혔다.

8/20의 🔴(`EXCEPTION_PROC` 자기참조 누락)은 추출기 수정으로 닫혔다.
🟠 하나는 `INS_EXTRA`에서 직전에 🟡이던 건을 이번 단위가 대상 행 집합 위험으로 올린 것이고,
동시성과 무관해 단독 실행 확인 뒤에도 유지된다(4-2절).

## 2. 검증 대상 확정

**소비 명세서 집합 12개** — `agent/MigrationInstructions.md`의 `Spec.md` 링크에서 읽었다.

**참조 폐포 31개** — 소비 12개 각각의 `raw/dependency-manifest.json` `Nodes[]` 합집합.
`Status`는 31개 전부 `Succeeded`이고, 경로 93개가 모두 실재한다.

| 종류 | 개수 | 이번에 재생성 |
|---|---|---|
| SP | 14 (소비 12 + 중첩 2) | 3 |
| 로컬 함수 | 10 | 9 |
| 외부 DB 함수 (`SETTLE_CARD_DB`) | 7 | 5 |

**폐포에만 있는 SP 2개**(`UP_Util_Settle_Summary_AcqManual`, `UP_UTIL_SETTLE_SUMMARY_EXTRA`)는
`UP_Util_Settle_Summary`가 `EXEC`로 부르는 하위 SP이고 최상위 실행 순서에 없어 소비 집합에서
빠진 것이 정상이다(사용자 확인). 축 A 대상에는 포함했다. 이번에 재생성되지 않아 캐시가 적중했다.

**축 A 교차 대상 8개** — 폐포 안에서 사용자 함수를 호출하는 객체 전부.
SP 6개와 함수 2개(`UIF_SettleYMD`, `UF_GET_COLLECTYMD`)다. 함수 2개는 호출이 전부 DML 밖이라
「참조 함수」 표가 없는 것이 정상이며, 두 단위 모두 3-2-1절 사각지대 점검으로 대신했다.

### 2-1. 재생성 범위를 어떻게 정했는가

"결함이 난 SP"가 아니라 **"추출 재료가 실제로 바뀐 객체"**를 기준으로 삼았다.
수정 전(`f09b7b0`)과 후(`35dfc74`) 두 바이너리로 폐포 24개 객체의 자기참조·집합 술어를
전부 뽑아 대조했더니 달라진 것은 두 객체뿐이었고(`EXCEPTION_PROC` 10줄, `EXPECT_PROC` 2줄),
의존성 이름 표기가 어긋난 것은 `INS_EXTRA` 하나였다. 나머지 21개는 바이트 단위로 동일했다.

그래서 8개 SP가 아니라 3개만 돌렸고, 폐포 때문에 실제 분석은 17개 객체였다.
**이 판단은 결과로 뒷받침된다** — 재생성한 17개에서 결함 6건이 나왔고 그중 2건은
재생성이 새로 만든 것이다(4-1절). 재료가 안 바뀐 14개를 함께 돌렸다면 같은 위험을
14개에 더 걸었을 것이다.

## 3. 단위별 커버리지

| 축 | 단위 | 종류 | 판정 | 상태 | 근거 파일 |
|---|---|---|---|---|---|
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_CANCEL_INS.Procedure | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_CANCEL_INS.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_CANCEL_INS/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_CANCEL_INS/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_INS.Procedure | SP | 결함 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_INS.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_INS/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_INS/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure | SP | 결함 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_SUMMARY_ETC.Procedure | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_ETC.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_ETC/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_ETC/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure | SP | 결함 | 캐시 | `output/Objects/dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_UTIL_STAT_PGCOLLECT_INS/docs/Spec.md`<br>`output/Procedures/dbo.UP_UTIL_STAT_PGCOLLECT_INS/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_Util_PG_Client_CMRate_Ins.Procedure | SP | 정합 | 캐시 | `output/Objects/dbo.UP_Util_PG_Client_CMRate_Ins.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_Util_PG_Client_CMRate_Ins/docs/Spec.md`<br>`output/Procedures/dbo.UP_Util_PG_Client_CMRate_Ins/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_Util_Settle_Summary.Procedure | SP | 정합 | 캐시 | `output/Objects/dbo.UP_Util_Settle_Summary.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_Util_Settle_Summary/docs/Spec.md`<br>`output/Procedures/dbo.UP_Util_Settle_Summary/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UP_Util_Settle_Summary_AcqManual.Procedure | SP | 결함 | 캐시 | `output/Objects/dbo.UP_Util_Settle_Summary_AcqManual.Procedure/raw/object_definition.sql`<br>`output/Procedures/dbo.UP_Util_Settle_Summary_AcqManual/docs/Spec.md`<br>`output/Procedures/dbo.UP_Util_Settle_Summary_AcqManual/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT.Function | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4INTEREST.Function | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT4INTEREST.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4INTEREST/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4INTEREST/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_GET_COMM4PG.Function | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4PG.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_GET_COMM4PG4INTEREST.Function | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4PG4INTEREST.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG4INTEREST/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG4INTEREST/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_GET_EXTRACOMM4CLIENT.Function | 외부함수 | 정합 | 캐시 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_EXTRACOMM4CLIENT.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_EXTRACOMM4CLIENT/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_EXTRACOMM4CLIENT/raw/metadata.json` |
| A | SETTLE_CARD_DB.dbo.UF_Get_ExtraCardCommissionAmt.Function | 외부함수 | 정합 | 캐시 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_Get_ExtraCardCommissionAmt.Function/raw/object_definition.sql`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_Get_ExtraCardCommissionAmt/docs/Spec.md`<br>`output/External/SETTLE_CARD_DB/Functions/dbo.UF_Get_ExtraCardCommissionAmt/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_CLIENTSECTIONRATE.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_CLIENTSECTIONRATE.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_CLIENTSECTIONRATE/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_CLIENTSECTIONRATE/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_COLLECTYMD.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_COLLECTYMD.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_COLLECTYMD/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_COLLECTYMD/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_INCVTAXRATE.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_INCVTAXRATE.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_INCVTAXRATE/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_INCVTAXRATE/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_OUTYMD4REFUND.Function | 함수 | 결함 | 신규 | `output/Objects/dbo.UF_GET_OUTYMD4REFUND.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_OUTYMD4REFUND/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_OUTYMD4REFUND/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_PGCommOption.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_PGCommOption.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_PGCommOption/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_PGCommOption/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_ROUND4VAT.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_ROUND4VAT.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_ROUND4VAT/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_SETTLE_EXCHANGERATE.Function | 함수 | 결함 | 캐시 | `output/Objects/dbo.UF_GET_SETTLE_EXCHANGERATE.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_SETTLE_EXCHANGERATE/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_SETTLE_EXCHANGERATE/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_GET_WORKDAY2.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_WORKDAY2.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_GET_WORKDAY2/docs/Spec.md`<br>`output/Functions/dbo.UF_GET_WORKDAY2/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UF_Get_CLComm4MobileCo.Function | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_Get_CLComm4MobileCo.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UF_Get_CLComm4MobileCo/docs/Spec.md`<br>`output/Functions/dbo.UF_Get_CLComm4MobileCo/raw/metadata.json` |
| A | SETTLE_POQ_DB.dbo.UIF_SettleYMD.Function | 함수 | 결함 | 신규 | `output/Objects/dbo.UIF_SettleYMD.Function/raw/object_definition.sql`<br>`output/Functions/dbo.UIF_SettleYMD/docs/Spec.md`<br>`output/Functions/dbo.UIF_SettleYMD/raw/metadata.json` |
| A 교차 | SETTLE_POQ_DB.dbo.UF_GET_COLLECTYMD.Function | 교차 | 정합 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UIF_SettleYMD.Function | 교차 | 정합 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure | 교차 | 정합 | 캐시 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure | 교차 | 정합 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure | 교차 | 정합 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_INS.Procedure | 교차 | 정합 | 캐시 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure | 교차 | 정합 | 신규 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
| A 교차 | SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure | 교차 | 정합 | 캐시 | 위 세 파일 + 사각지대에서 연 피호출 DDL |
## 4. 축 A 결함

| 등급 | 객체 | 원본 앵커 | 산출물 앵커 | 원본 | 산출물 | 영향 |
|---|---|---|---|---|---|---|
| 🟡 | `UP_UTIL_SETTLE_INS_EXTRA` | `object_definition.sql:30-41` | `Spec.md:44`, `:90`, `:104`, `:303`, `:312` | `IF EXISTS(SELECT PLTID FROM TSettleMst WITH(NOLOCK) WHERE … OutState IN (1,5) …) → RETURN -9`. `BEGIN TRAN`보다 앞에 있는 게이트다 | `:303`의 `NOLOCK` 일반 서술이 이 사전 검사를 열거하지 않고, 이 검사를 다루는 네 자리(`:44`·`:90`·`:104`·`:312`)는 `NOLOCK`을 전혀 언급하지 않는다 | 표기·추적성. **이 배치는 단독 실행이므로**(2026-08-21 사용자 확인) `NOLOCK`이 읽는 것과 커밋된 데이터가 같아 실제 오판은 일어나지 않는다. 다만 이관 개발자가 이 게이트의 더티 리드 노출을 모르고 옮기면, 새 시스템이 동시 실행 구성이 될 때 위험이 그대로 따라간다 |
| 🟠 | `UP_UTIL_SETTLE_INS_EXTRA` | `:47-53` vs `:197` | `Spec.md:49`, `:206-210`, `:314-315` (대조 문장 부재) | `DELETE`는 `TSettleMst.ProcYMD = @pi_strYMD AND YMD >= @v_strReqYMD`(범위), `INSERT` 원천은 `TExtraSettleIn.ResYMD = @pi_strYMD`(단일값, 다른 테이블·다른 필드) | 각 문장의 조건은 개별로 정확히 서술되나 "서로 다른 필드·범위로 대상을 정한다"는 대조가 문서 어디에도 없다 | 삭제한 범위와 재적재하는 범위가 일치한다는 보장이 서술되지 않아, 삭제됐지만 재적재되지 않는 행(유실) 또는 그 반대(중복·범위 밖 재적재) 가능성이 전달되지 않는다 |
| 🟡 | `UP_UTIL_SETTLE_INS_EXTRA` | `:276-279`, `:300-304` | `Spec.md:187-193`, `:195-204` | 갱신 4·5가 `CLCOMM`·`CLVT`·`CLETC`·`CLINTCOMM` 등을 `ISNULL` 없이 `+`·`-`로 직접 합산 | 산식만 옮기고 `NULL` 전파 가능성 언급이 없다 | 이 SP 안에서 현재 도달 가능한 `NULL` 소스는 확인되지 않아 표기·추적성. 단위가 피호출 함수 본문은 열지 않아 완전 배제는 못 했다 |
| 🟡 | `UF_GET_OUTYMD4REFUND` | `object_definition.sql:16-18` | `Spec.md:77` | `RETURNS VARCHAR(8)` → `AS` → `BEGIN`으로 이어져 `WITH SCHEMABINDING`이 **없음이 원문에서 확정**된다 | "스키마 바인딩 여부 또는 시스템 결정성 속성 메타데이터는 제공되지 않아 확인할 수 없음" | 표기·추적성. DDL에서 바로 읽히는 사실을 메타데이터 부재 탓으로 돌렸다. 호출 SP 1개 |
| 🟡 | `UIF_SettleYMD` | `metadata.json`의 `StaticAnalysis.ReferencedColumnsPerTable["…TSettlePeriodMst"]` 마지막 원소 `YMD` | `Spec.md:166`, `:173` | 파서가 확정한 참조 컬럼 목록에 `YMD`가 포함된다 | 조회 상세 표에서 `YMD`를 빼고, `:173`에서 "`TSettlePeriodMst`의 참조 컬럼으로 사용되지 않습니다"라고 **명시적으로 부정**한다 | 금액·행 집합 영향 없음. 계약이 재해석을 금지한 파서 확정값을 문서가 정면으로 반박. 호출 SP 3개 |
| 🟡 | `UF_GET_SETTLE_EXCHANGERATE` | `:9-15` | `Spec.md:229` | `CREATE FUNCTION … RETURNS DECIMAL(9,5) AS` — `WITH` 절이 없어 스키마 바인딩 아님이 확정 | "함수의 스키마 바인딩 여부"를 "제공된 정보에 포함되지 않는 항목"으로 분류 | 표기·추적성. 호출 SP 1개. **캐시 재사용**(8/20 판정) |
| 🟡 | `UP_UTIL_SETTLE_INS` | `:243-249` | `Spec.md` (해당 서술 없음) | `/* */`로 통째 주석 처리된 옛 `CLCOMM`/`CLETC` 계산식 블록 | 죽은 코드 블록의 존재가 없다 | 활성 계산식은 정확해 금액 영향 없음. 계약이 명시 요구한 "주석 처리된 블록" 누락. **캐시 재사용** |
| 🟡 | `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | `:20,189,205`(있음) / `:36,21,37,190,206`(없음) | `Spec.md:77` | `TSettleMst`는 사전확인·UPDATE1·UPDATE2에만 `NOLOCK`, `DELETE`에는 없다. `TPGProperty`는 `INSERT`의 `P`·`Y` 별칭에만 | "`NOLOCK` 힌트가 5개 테이블의 조회 또는 조인에 사용됩니다"로 뭉뚱그려 문장별 차이가 지워짐 | 이관 시 `NOLOCK`을 일괄 적용·누락해 잠금 동작이 달라질 수 있다. **캐시 재사용** |
| 🟡 | `UP_Util_Settle_Summary_AcqManual` | `:47` | `Spec.md:33` | `DELETE TSettleByOUT FROM … WITH(NOLOCK)` — `NOLOCK`이 걸리는 자리는 총 3곳 | "원천 조회와 커서 대상 조회에 `WITH(NOLOCK)`" — `DELETE` 대상 스캔의 `NOLOCK` 누락 | 격리수준 서술 범위가 실제보다 좁다. **캐시 재사용** |
| 🟡 | `UP_UTIL_STAT_PGCOLLECT_INS` | `:113` | `Spec.md` (해당 서술 없음) | `INSERT … SELECT … GROUP BY … ORDER BY INYMD, CLIENTID, PGNAME, MALLID` | `ORDER BY` 절이 문서 어디에도 없다 | 금액·행 집합 영향 없음(`INSERT…SELECT`의 `ORDER BY`는 삽입 순서를 보장하지 않는다). 추적성. **캐시 재사용** |

### 4-1. 전 객체 공통 결함

**(1) 재생성은 재료가 안 바뀐 객체에서 복권이다 — 이번에 새 🟡 2건이 생겼다.**

재생성한 17개 중 두 객체에서, 8/20 판에는 없던 결함이 새로 나타났다. 상위가 백업본과
직접 대조해 확정했다(단위는 세 파일만 읽어 이 사실을 볼 수 없다).

| 객체 | 8/20 판 | 8/21 판 |
|---|---|---|
| `UF_GET_OUTYMD4REFUND` | 스키마 바인딩 언급 **자체가 없었다**(그래서 `정합`) | "확인할 수 없음" 문장을 **새로 넣었다** |
| `UIF_SettleYMD` | `YMD`를 "정적 분석 메타데이터가 제공한 항목"으로 **출처를 밝혀 실었다**(그래서 `정합`) | "사용되지 않습니다"로 **부정으로 바꿨다** |

두 건의 방향이 같다: **파서·원본이 확정한 것을 문서가 "모르겠다"거나 "아니다"로 뒤집었다.**
계약은 두 자리 모두 원본/파서가 기준값이라고 못 박고 있다.

닫힌 것도 있다 — `UF_GET_COMM4PG4INTEREST`의 "피지용" 오기 6곳이 전부 사라졌다(단위가 전수
grep으로 확인, "피지" 0건). 즉 재생성은 이번에 🟡 1건을 닫고 🟡 2건을 만들었다.

**(2) "원본이 확정한 사실을 문서가 확정하지 않는다"가 반복 부류다.**

스키마 바인딩 건이 두 객체에서 같은 모양으로 나왔다(`UF_GET_OUTYMD4REFUND` 신규,
`UF_GET_SETTLE_EXCHANGERATE` 8/20부터). `CREATE`의 `WITH` 절 유무는 DDL 원문에서 한 번
읽으면 끝나는 사실이고, 파서가 확정해 기계 재료로 실으면 이 부류가 닫힌다.
`NOLOCK` 문장별 유무(2건), `ORDER BY` 존재(1건), 주석 처리된 블록(1건)도 같은 성격이다 —
**추출 가능한 사실인데 모델의 서술에 맡겨 두어 실행마다 흔들린다.**

**(3) 추출기 수정 셋이 산출물에서 확인됐다.**

| 수정 | 확인 |
|---|---|
| 자기참조 별칭 해석 | `EXCEPTION_PROC` 자기참조 목록이 **4·6·7·8·13**으로 정정. 단위가 18개 UPDATE 전부를 DDL로 재현해 파서 값과 대조했고 완전 일치 |
| 집합 술어 좌변 일반화 | `EXPECT_PROC` 표에 `LEFT(D.PayToolType,1) IN 'C'`(`:251`)·`IN 'A','B'`(`:254`) 신설. 단위가 11개 UPDATE의 WHERE를 재추출해 30행과 1:1 대조, 누락·초과 0 |
| 의존성 이름 표기 | `INS_EXTRA` 표 링크가 `dbo.UF_GET_WORKDAY2`로 바뀌어 문서 하단 「참조 코드 객체」와 일치. 인자 칸의 원문 표기(`dbo.UF_Get_WorkDay2`)는 그대로 보존 |

### 4-2. 등급 판정이 갈린 두 건 — 하나는 닫혔고 하나는 남았다

`UP_UTIL_SETTLE_INS_EXTRA`의 세 회귀는 8/20 판정에서 모두 🟡이었다. 이번 단위는 그중 둘을
🔴·🟠으로 올렸다. **결함 내용은 같고 등급 근거가 다르다.**

스킬은 "등급이 갈릴 때는 영향이 배포 구성에 달려 있으면 높은 쪽으로 매기고 그 전제를
결함 행에 적는다"고 정한다. 단위는 그 전제를 명시했다.

**🔴 → 🟡 (닫힘).** `NOLOCK` 게이트 건의 전제는 "동시 실행·동시 갱신 상황"이었다.
2026-08-21 사용자 확인으로 **이 배치는 단독 실행**임이 밝혀졌다. 그러면 `NOLOCK`이 읽는 것과
커밋된 데이터가 같아 더티 리드 오판이 일어날 수 없고, 단위가 든 두 실패 경로(거짓 양성 →
그날 배치 스킵, 거짓 음성 → 보호 대상 삭제·재계산)가 모두 성립하지 않는다.
전제가 닫혔으므로 상위가 🟡로 내렸다.

이것은 재판정이 아니다. 단위가 "이 전제가 참이면 🔴"이라고 조건을 걸어 반환했고, 그 전제의
참·거짓은 산출물이 아니라 배포 구성이 정하는 것이라 단위가 알 수 없었다. 상위가 그 사실을
공급해 조건을 해소한 것이다.

**남은 위험**: 결함 자체는 사라지지 않았다. 명세서가 이 게이트의 더티 리드 노출을 적지 않으므로,
이관 개발자가 그대로 옮긴 뒤 새 시스템이 동시 실행 구성이 되면 위험이 따라간다.
단독 실행이라는 사실은 **현재 배포**에 대한 것이지 이관 후 설계에 대한 것이 아니다.

**🟠 (유지).** 삭제·삽입 범위 대조 건은 동시성과 무관하다. `DELETE`가 지우는 범위
(`ProcYMD = @pi_strYMD AND YMD >= @v_strReqYMD`)와 `INSERT`가 재적재하는 범위
(`TExtraSettleIn.ResYMD = @pi_strYMD`)는 서로 다른 테이블의 다른 필드로 정해지고,
혼자 돌아도 삭제된 행이 재적재되지 않으면 그대로 유실이다. 단독 실행 확인은 이 건의
전제를 건드리지 않으므로 단위 판정을 유지한다.

## 4-3. 축 A 교차 — 명세서가 참조 함수를 다루는 자리

**8단위 전부 `정합`.** 직전 판의 🟡 1건과 ⚪ 1건이 모두 닫혔다.

| 호출 객체 | 표 위치 | 행 수 | 판정 | 확인 내용 |
|---|---|---|---|---|
| `UP_UTIL_SETTLE_EXCEPTION_PROC` | `Spec.md:337` | 29 | 정합 | 18개 UPDATE 전수 순회로 함수별 호출 수 재현(9·7·3·2·2·2·1·1·1·1)=29. 정렬 공백·이중 공백까지 축자 보존. 외부 5개 포함 링크 10개 실재 |
| `UP_UTIL_SETTLE_EXPECT_PROC` | `:266` | 9 | 정합 | 라인 9개 전부 DDL 실측과 일치. `UPDATE 10`의 `CASE WHEN`(`:227`)과 `ELSE`(`:229`)가 별도 2행으로 정확히 분리 |
| `UP_UTIL_SETTLE_INS_EXTRA` | `:262` | 5 | 정합 | **링크 대소문자 🟡 해소.** 표(`:266`)와 「참조 코드 객체」(`:377`)가 이제 일치. 중첩 1건을 안·바깥 2행으로 분리한 것 포함 5행이 DDL과 일치 |
| `UP_UTIL_SETTLE_COMM_UPD` | `:358` | 23 | 정합 | **캐시 재사용** |
| `UP_UTIL_SETTLE_INS` | `:179` | 3 | 정합 | **캐시 재사용** |
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | — | 6 | 정합 | **캐시 재사용** |
| `UIF_SettleYMD` (함수) | 표 없음 | — | 정합 | 표가 없는 것이 정상(호출 2건이 전부 `SELECT` 안). 3-2-1 다섯 항목 위반 없음. 직전 판의 ⚪ 1건도 사라짐 |
| `UF_GET_COLLECTYMD` (함수) | 표 없음 | — | 정합 | 표가 없는 것이 정상(DML 자체가 없음). `UF_GET_WORKDAY2` 산문 5단계를 원본과 항목별 대조, 정확 |

**표의 사각지대에서 본 것** — SP 6개는 함수 호출이 전부 `INSERT`/`UPDATE`/`DELETE` 안에만
있어 사각지대가 비어 있음을 각 단위가 DDL 전수 순회로 재확인했다. 함수 2개는 반대로 호출이
전부 DML 밖이라 표가 없고, 그 구간을 3-2-1절 산문 대조가 덮었다.
**기계 확정 표의 존재가 `SELECT`·`SET`·`IF` 안의 호출까지 검증했다는 뜻은 아니다** —
그 자리는 함수 2단위의 산문 대조가 맡았고, SP 6개에는 그런 자리가 없었다.

**계약 적용의 정밀함이 두 자리에서 드러났다.**
`UIF_SettleYMD` 단위는 `SettleDay`가 `tinyint NOT NULL`이라 `UF_GET_WORKDAY2`의 "간격 < 0"
분기가 이 호출 경로에서 구조적으로 죽은 코드임을 확인하고도 결함으로 세지 않았다 —
명세서가 그 분기를 "이 호출에서 실행된다"고 단정하지 않고 함수 자체의 일반 알고리즘으로만
서술하기 때문이다. 계약이 든 실패 패턴(호출 WHERE가 한정하는데 ELSE를 실행되는 동작으로
단정)과 다르다는 판단이다.

`INS_EXTRA` 단위는 링크 경로는 정본 표기로 가야 하고 **인자 칸은 원문 표기를 보존해야
한다**는 것을 갈랐다. DDL이 `dbo.UF_Get_WorkDay2(@v_strCurrYMD,2)`로 부르는 것을 인자 칸이
그대로 인용하는 것은 정상이고, 링크만 `dbo.UF_GET_WORKDAY2`로 가는 것이 옳다.

## 5. 축 B 결함

미실행. `output/Jobs/POQSettlePrco20/agent/`의 단계 지시서는 2026-08-19자라 이번에 재생성된
17개 `Spec.md`를 반영하지 않는다. 배치 Job을 다시 만든 뒤 돌려야 한다.

## 6. 이 감사가 보증하지 않는 것

- **축 B를 대조하지 않았다.** 단계 지시서가 `Spec.md`를 보존했는지는 이 보고서가 말하지 않는다.
- **14개 객체는 이번에 검증하지 않았다.** 캐시 적중이므로 판정과 결함 목록은 8/20 실행의
  것을 그대로 재사용했다. 그 객체들의 `Spec.md`는 8/20자 그대로이고 파일 해시가 같다.
- **실행 대조를 하지 않았다.** 이번 실행에서 실행 쿼리로 닫아야 할 보류는 나오지 않았다.
  직전 실행의 두 🔴은 SSMS 실행으로 반증되어 취소됐고(`+ + '01'` → `20260101`/`varchar`,
  `ROUND(1000,0,NULL)` → `1000`), 이번 단위들에 그 결과를 들려 보내 재판정을 막았다.
- **`INS_EXTRA`의 🟠은 여전히 열려 있다.** 삭제 범위와 재적재 범위가 실제로 일치하는지는
  이 감사가 확인하지 않았다 — 두 범위가 다른 필드로 정해진다는 **서술이 없다**는 것만 확인했다.
  실제로 어긋나는 행이 있는지는 데이터로 확인해야 한다.
- **단독 실행이라는 사실은 사용자 확인으로 받았고, 이 감사가 관측한 것이 아니다.**
  `OutState`를 쓰는 객체가 폐포 안에 셋 있다는 것(`EXPECT_PROC` 5곳, `EXCEPTION_PROC` 1곳,
  `INS_EXTRA` 1곳)은 확인했으나, 폐포 **밖에서** `TSettleMst.OutState`를 쓰는 주체가 있는지는
  분석 범위 밖이라 보지 않았다. 그 사실이 달라지면 4-2절의 🟡은 다시 🔴이 된다.
- **회귀 대조는 상위가 백업본으로 했고, 재생성한 17개에 한한다.** 단위는 세 파일만 읽으므로
  이전 판을 볼 수 없다. 캐시 적중한 14개는 파일이 안 바뀌었으므로 회귀 여부를 물을 필요가 없다.
- **`EXCEPTION_PROC` 단위가 파서 흠결 1건을 관찰했다**(⚪, 결함 아님): `ReferencedColumnsPerTable`의
  `TPGProperty`에 실제 스키마에 없는 `PLTID`·`ID`가 실려 있다. 파생 테이블의 비한정 컬럼이
  조인된 테이블에 잘못 귀속된 것으로 보이며, 명세서는 그 둘을 빼서 오히려 원본 의미에 부합한다.
  이 보고서는 그 파서 흠결을 결함으로 세지 않았고, 고치지도 않았다.
- **기계 확정 표 3종(DML 범위·집합 술어·파생 테이블)의 조립 알고리즘을 재실행하지 않았다.**
  단위들은 DDL 원문과의 논리적 일치를 셀 단위로 검증했다.
