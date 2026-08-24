# POQSettlePrco20 산출물 정합성 감사 — 축 A 재감사 (10회차)

2026-08-24 · **캐시 포맷 버전 15** 전건 재생성(08-23 21:00~08-24 07:27, 소비 SP 12 + 폐포 함수·하위 SP 전부) 직후 · 축 A와 축 A 교차만 수행(축 B는 이번 지시 범위 밖) · 이전 판(9회차, `ConsistencyReport-20260823-r9.md`) 대조 포함

## 1. 판정

| 축 | 판정 | 단위 수 | 신규 검증 | 캐시 재사용 | 검증 불가 |
|---|---|---|---|---|---|
| A (객체) | **결함** — 정합 30 · 결함 1 | 31 (SP 14 · 로컬 함수 10 · 외부 함수 7) | 31 | 0 | 0 |
| A 교차 | **정합** — 8/8 | 8 | 8 | 0 | 0 |
| B | 수행하지 않음(지시 범위 밖) | — | — | — | — |

등급 합계(객체 + 교차): **🔴 0 · 🟠 0 · 🟡 1 · ⚪ 3**. 9회차는 🔴 0 · 🟠 1 · 🟡 3 · ⚪ 31이었다(보존본 `docs/audit-reports/2026-08-23-POQSettlePrco20-axisA.md`). 9회차의 🟠 1·🟡 3이 전부 사라졌고, 이번 🟡 1은 **새 자리**다(4-3절). 열 회차 중 처음으로 교차 8단위가 결함·보류 0으로 끝났고, 함수 17개는 ⚪조차 없다.

캐시는 39단위 전부 미스였다 — 31객체가 v15로 재생성돼 해시가 전부 달라졌다. 9회차 캐시는 `.cache-20260823-r9.json`에, 9회차 보고서는 `ConsistencyReport-20260823-r9.md`에 보존했다.

## 2. 검증 대상 확정

- **소비 명세서 집합 12개** — `agent/MigrationInstructions.md`의 `Spec.md` 링크(= `raw/prompt-context.md`의 `^Filename:` 12행). 9회차와 동일.
- **참조 폐포 31개** — 12개 SP의 `dependency-manifest.json` `Nodes[]` 합집합. SP 14(소비 12 + 중첩 2) · 로컬 함수 10 · 외부 DB(`SETTLE_CARD_DB`) 함수 7. 31개 전부 `Status: Succeeded`, `output/.sp_cache_index.json`의 `FormatVersion` 전부 **15**.
- **교차 대상 8개** — `### 참조 함수 (기계 확정 — 수정 금지)` 표를 가진 객체 전부(SP 6 + 함수 2). 9회차와 동일.

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
| A | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/raw/metadata.json` |
| A | `dbo.UF_GET_SETTLE_EXCHANGERATE.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_SETTLE_EXCHANGERATE.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_GET_SETTLE_EXCHANGERATE/docs/Spec.md` + `output/Functions/dbo.UF_GET_SETTLE_EXCHANGERATE/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/raw/metadata.json` |
| A | `dbo.UF_GET_COLLECTYMD.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_COLLECTYMD.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_GET_COLLECTYMD/docs/Spec.md` + `output/Functions/dbo.UF_GET_COLLECTYMD/raw/metadata.json` |
| A | `dbo.UF_GET_OUTYMD4REFUND.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_OUTYMD4REFUND.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_GET_OUTYMD4REFUND/docs/Spec.md` + `output/Functions/dbo.UF_GET_OUTYMD4REFUND/raw/metadata.json` |
| A | `dbo.UF_GET_WORKDAY2.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_WORKDAY2.Function/raw/object_definition.sql` + `output/Functions/dbo.UF_GET_WORKDAY2/docs/Spec.md` + `output/Functions/dbo.UF_GET_WORKDAY2/raw/metadata.json` |
| A | `dbo.UIF_SettleYMD.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UIF_SettleYMD.Function/raw/object_definition.sql` + `output/Functions/dbo.UIF_SettleYMD/docs/Spec.md` + `output/Functions/dbo.UIF_SettleYMD/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/raw/metadata.json` |
| A | `dbo.UF_Get_ExtraCardCommissionAmt.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_Get_ExtraCardCommissionAmt.Function/raw/object_definition.sql` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_Get_ExtraCardCommissionAmt/docs/Spec.md` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_Get_ExtraCardCommissionAmt/raw/metadata.json` |
| A | `dbo.UF_GET_EXTRACOMM4CLIENT.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_EXTRACOMM4CLIENT.Function/raw/object_definition.sql` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_EXTRACOMM4CLIENT/docs/Spec.md` + `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_EXTRACOMM4CLIENT/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD/raw/metadata.json` |
| A | `dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_STAT_PGCOLLECT_INS/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_STAT_PGCOLLECT_INS/raw/metadata.json` |
| A | `dbo.UP_Util_Settle_Summary.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_Util_Settle_Summary.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_Util_Settle_Summary/docs/Spec.md` + `output/Procedures/dbo.UP_Util_Settle_Summary/raw/metadata.json` |
| A | `dbo.UP_Util_Settle_Summary_AcqManual.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_Util_Settle_Summary_AcqManual.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_Util_Settle_Summary_AcqManual/docs/Spec.md` + `output/Procedures/dbo.UP_Util_Settle_Summary_AcqManual/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_SUMMARY_ETC.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_ETC.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_ETC/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_ETC/raw/metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure/raw/object_definition.sql` + `output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/docs/Spec.md` + `output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/raw/metadata.json` |
| A 교차 | `dbo.UP_UTIL_SETTLE_INS.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json`(키에 폐포 함수 DDL 해시 포함) |
| A 교차 | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json`(키에 폐포 함수 DDL 해시 포함) |
| A 교차 | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json`(키에 폐포 함수 DDL 해시 포함) |
| A 교차 | `dbo.UF_GET_COLLECTYMD.Function` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json`(키에 폐포 함수 DDL 해시 포함) |
| A 교차 | `dbo.UIF_SettleYMD.Function` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json`(키에 폐포 함수 DDL 해시 포함) |
| A 교차 | `dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json`(키에 폐포 함수 DDL 해시 포함) |
| A 교차 | `dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json`(키에 폐포 함수 DDL 해시 포함) |
| A 교차 | `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·`Spec.md`·`metadata.json`(키에 폐포 함수 DDL 해시 포함) |

객체 31 + 교차 8 = 39단위, 1절과 일치. 검증 불가 0. **교차 8단위 모두 사각지대(표 밖 호출)가 0건**이라 피호출 함수 DDL을 연 단위가 없다(캐시 키에는 폐포 함수 DDL 해시를 보수적으로 포함).

## 4. 축 A 결함

| 등급 | 객체 | 원본 앵커 | 산출물 앵커 | 원본 | 산출물 | 영향 |
|---|---|---|---|---|---|---|
| 🟡 | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | object_definition.sql:340 | Spec.md:280 | 기계 확정 DML 범위 표의 UPDATE 10 행(프롬프트 원문, raw/metadata.json RawPromptContext) 술어 컬럼 칸: CLIENTID, PGNAME, MALLID, YMD, USESTATE, CYMD, AYMD, RefundFlag — DDL 336–342 술어와 부합 | Spec.md의 같은 행 칸: CLIENTID, PGNAME, MALLID, YMD, PGNAME, USESTATE, CYMD, AYMD, RefundFlag — 「수정 금지」 표에 PGNAME 한 개가 추가로 끼어듦 | DDL에 PGNAME 술어가 실제로 둘(337 조인, 340 IN) 있어 행 집합·금액은 불변. 다만 기계 확정 표의 축자 전사 계약 위반으로 L1 행 대조가 이 행에서 어긋난다 — 표기·추적성 결함 |

**🟡 `UP_UTIL_SETTLE_COMM_UPD` — 기계 확정 표의 축자 전사에 토큰 하나가 끼어들었다.** DML 범위 표 UPDATE 10 행의 술어 컬럼 칸이 기계 원문(RawPromptContext) 8개 토큰에 `PGNAME`을 하나 더해 9개로 옮겨졌다. DDL 336-342에 PGNAME 술어가 실제로 둘(337 조인·340 IN) 있어 **행 집합·금액은 불변**이고, 같은 사실은 집합 술어 표가 문장별로 보존한다. 남는 것은 「수정 금지」 계약 위반 하나다. **도구 쪽 관찰**: 이 행이 L1(`CheckDmlScopeTable`)을 통과해 저장됐다는 것은 술어 컬럼 칸 대조가 중복 토큰을 허용한다는 뜻이다(집합 비교) — 축자 전사를 완전히 강제하려면 그 칸의 다중집합 대조가 필요하다. `known-defects`의 L1 절 후보로 적었다.

### 4-1. 전 객체 공통 결함 (⚪ 3)

| 객체 | 앵커 | 내용 |
|---|---|---|
| `dbo.UP_Util_PG_Client_CMRate_Ins.Procedure` | Spec.md:26 | 헤더 주석의 `ProcedureName : UP_Util_PG_Client_CMRate_Ins`는 실제 생성 객체 `UP_Util_PG_Client_CMRate_Ins`와 대소문자 표기만 다릅니다. — 인용된 두 이름이 글자까지 동일한데 '대소문자만 다르다'고 서술 — 대문자 표기는 라인 2의 스크립트 배너 주석(UP_UTIL_PG_CLIENT_CMRATE_INS)에만 있다. 구현·행 집합·금액에 |
| `dbo.UP_UTIL_SETTLE_CANCEL_INS.Procedure` | Spec.md:185 | mermaid 흐름이 INSERTROW["취소 정산 행 INSERT"] → SOURCEFILTER["A와 B 결합 및 취소일자와 상태 조건 적용"] 순으로 그려 INSERT 뒤에 필터가 적용되는 것처럼 읽힐 수 있다 — 같은 한 문장의 하위 단계를 나눠 그린 표기 문제일 뿐이며, 바로 위 로직 흐름 요약(163~167행)은 순서를 정확히 서술한다. 행 집합·금액에 영향 없음 |
| `dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure` | Spec.md:111 | SELECT 대상 테이블 표의 TClientContract 설명 칸이 `A.CLIENTID = E.CLIENTID` 조인 술어를 직접 적었다 — 2026-08-23부터 금지된 '설명 칸의 술어 서술'이나, 내용이 DDL 및 DML 범위 표(INSERT 1 조인 키 CLIENTID)와 일치하고 단일 문장(INSERT 1)만 언급해 문장 묶음  |

⚪ 3건은 전부 산문·도식의 표기 수준(mermaid 단계 순서 표기 2건, 헤더 주석 대소문자 서술 오류 1건)과 설명 칸 술어 1건(`INS_EXTRA`, 표와 일치하고 문장 묶음 없음 — v15 산출물에서 금지 위반의 유일한 관측)이다. 9회차의 공통 부류 여덟(도구 상용구·파라미터 표 지역 변수·파서 귀속·기준일 칸 보류·실행 의미 보류 등)은 **하나도 재관측되지 않았다** — 각각 캐시 15의 (A)·(D)·(G)와 `axis-a.md` 규약 문서화가 닫았다. 보류(holds)도 39단위 전체에서 1건(`Settle_Summary`의 하위 SP 역할 서술 — 단위 범위 밖 선언)뿐이다.

### 4-2. 축 A 교차 — 명세서가 참조 함수를 다루는 자리

| 호출 객체 | 표 행 수 | 판정 | 표 밖 호출 | 동작 서술 금지 |
|---|---|---|---|---|
| `dbo.UP_UTIL_SETTLE_INS.Procedure` | 3 | 전 행 정합 | 0건 | 위반 0 |
| `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | 29 | 전 행 정합 | 0건 | 위반 0 |
| `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | 23 | 전 행 정합 | 0건 | 위반 0 |
| `dbo.UF_GET_COLLECTYMD.Function` | 2 | 전 행 정합 | 0건 | 위반 0 |
| `dbo.UIF_SettleYMD.Function` | 2 | 전 행 정합 | 0건 | 위반 0 |
| `dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure` | 9 | 전 행 정합 | 0건 | 위반 0 |
| `dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure` | 5 | 전 행 정합 | 0건 | 위반 0 |
| `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure` | 6 | 전 행 정합 | 0건 | 위반 0 |

여덟 표 79행 전부 함수명·호출 위치·인자 원문·링크 실재가 DDL과 일치했고, 표 밖(`SET`·`RETURN`·FROM 없는 SELECT) 호출은 여덟 객체 모두 0건, 표에 실린 함수의 동작 서술은 0건이다. 9회차의 교차 🟡(`UF_GET_COLLECTYMD:93` 동작 서술)은 v15 재생성본에서 소멸을 이 감사가 확인했다.

### 4-3. 이전 판(9회차) 대조

9회차 🔴 0 · 🟠 1 · 🟡 3 · ⚪ 31 → 10회차 🔴 0 · 🟠 0 · 🟡 1 · ⚪ 3.

| 9회차 결함 | 닫은 것 | 10회차 관측 |
|---|---|---|
| 🟠 `INS_EXTRA4PLCARD` ON 절 리터럴 `ExtraType IN (2,3)` 증발(회귀) | 집합 술어 표의 `조인 ON T` 범위(캐시 14, `5674b2d`) | **소멸** — 조인 ON 4행이 표에 실리고 산문이 필터를 되찾음. `EXPECT_PROC`의 `조인 ON E` 행도 정합 |
| 🟡 `UF_GET_COLLECTYMD:93` 표에 실린 함수의 동작 서술 | v14 재생성이 지움(도구 변경 없음) | **재발 없음** — 객체·교차 단위 모두 무결함, 동작 서술 0건 |
| 🟡 `EXCEPTION_PROC:34` 파라미터-컬럼 관계 표 오기 | L1 `CheckParameterColumnClaims`(`78fba3f`·`765c683`) + v15 재생성 | **소멸** — 객체 단위 무결함 |
| ⚪ 부류 (A) DML 범위 표 고정 상용구 거짓 4객체 | 조건부 렌더(캐시 15) | **재발 없음** |
| ⚪ 부류 (D) 파라미터 표 지역 변수 행 3객체 | L1 `CheckParameterTableRows` | **재발 없음** |
| ⚪ 부류 (G) 파서 파생 테이블 컬럼 과잉 귀속 | `SqlStaticParser` 수정(캐시 15) | **재발 없음** — `EXCEPTION_PROC`의 `TPGProperty` 참조 컬럼에 `PLTID`·`ID` 없음 |
| 보류 (B)(C) 기준일 칸·실행 의미 행 15건 | `axis-a.md` 규약 문서화 | **보류 0건** — 단위들이 규약 목록을 인용하며 "범위 밖 정상"으로 판정 |

새로 난 것은 🟡 1(`COMM_UPD` 전사 중복 토큰 — 위 4절)과 ⚪ 3뿐이다. 재생성은 AI 재작성이므로 회차마다 새 표기 편차가 날 수 있고, 이번 것은 전부 표·집합 술어가 사실을 보존하는 자리다.

## 5. 축 B 결함

수행하지 않았다(지시 범위 밖). `agent/` 번들은 2026-08-19에 만들어졌고 `Spec.md`는 v15로 재생성됐으므로, 지금 축 B를 돌리면 폐기된 명세서로 만든 지시서를 현행 명세서와 대조하게 된다 — 번들 재생성 후에 돌려야 한다.

## 6. 이 감사가 보증하지 않는 것

- **축 B 전체**와 폐포에만 있는 SP 2개의 단계 흡수 여부.
- **실행 대조 없음** — 모든 판정은 정적 대조다.
- **`COMM_UPD` 🟡의 원인 단정** — 모델 전사 오류로 보이지만, 프롬프트 원문(RawPromptContext)과 산출물의 차이만 확인했고 L1의 술어 컬럼 칸 대조가 중복을 허용하는지는 코드로 재확인하지 않았다(단위의 추정).
- **교차 단위의 피호출 함수 DDL** — 사각지대 0건이라 여덟 단위 모두 열지 않았다. 참조 함수 표 행 판정은 호출 객체 쪽 파일과 링크 실재만으로 했다.
- **⚪·🟡의 "새 자리" 판정** — 9회차 캐시와 등급·앵커를 맞댄 결과이고, 9회차 이전 회차들과의 전수 대조는 하지 않았다.
- **이 판정의 유효 기간** — 08-24 07:27까지의 v15 산출물 기준이다. 프롬프트 계약이 다시 바뀌면 전건이 다시 미스가 난다.
