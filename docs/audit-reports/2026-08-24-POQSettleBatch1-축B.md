# POQSettleBatch1 산출물 정합성 감사

감사일 2026-08-24. 이 판은 **축 B 재감사**다 — 축 A·축 A 교차는 2026-08-24 POQSettlePrco20 축 A 재감사(10회차)의
판정을 캐시로 그대로 재사용했고, 새로 검증한 것은 `agent/` 번들의 단계 지시서 16개다.

## 1. 판정

| 축 | 판정 | 단위 수 | 신규 검증 | 캐시 재사용 | 검증 불가 |
| :--- | :--- | ---: | ---: | ---: | ---: |
| A (객체) | 결함 1건(🟡) | 31 | 0 | 31 | 0 |
| A 교차 | 정합 | 8 | 0 | 8 | 0 |
| B (단계) | 결함 — 🔴 2 · 🟠 7 · 🟡 16 · ⚪ 21 | 16 | 16 | 0 | 4 |

축 B는 16단계 중 10단계가 `결함`, 2단계가 `정합`, 4단계가 `검증 불가`다. **🔴 두 건은 S07·S14**에 있다.

## 2. 검증 대상 확정

**소비 명세서 집합(12개)** — `agent/MigrationInstructions.md`의 `Spec.md` 링크 12개에서 읽었고,
`raw/prompt-context.md`의 `^Filename:` 행 12개(`Feedback_Log.txt` 제외)와 일치한다.
POQSettlePrco20의 소비 집합과 **글자까지 동일**하다(diff 결과 차이 0).

**축 A 대상 = 참조 폐포 31개** — 소비 SP 12개의 `raw/dependency-manifest.json` `Nodes[]` 합집합.
구성은 SP 14개(소비 12 + 중첩 2: `UP_UTIL_SETTLE_SUMMARY_EXTRA`, `UP_Util_Settle_Summary_AcqManual`),
로컬 함수 10개, 외부 DB 함수 7개. `Status`는 31개 전부 `Succeeded`이고 `SpecPath`·`DdlPath`·`metadata.json` 모두 실재한다.
폐포에만 있는 SP 2개는 `UP_Util_Settle_Summary`(S12)가 `EXEC`로 부르는 하위 호출이므로 최상위 실행 순서에 없는 것이 정상이다 —
S12 단위가 그 호출 경로를 확인했고 흡수 누락이 아니다.

**단계 ↔ 레거시 매핑** — `raw/prompt-context.md`의 `[Approved Step List]`(폴백 ② 불필요).
목록의 `Legacy:` 12건과 **단계 본문의 `UP_` 토큰 합집합이 일치**한다(각 단계 파일에서 `grep -ohE '\bUP_[A-Za-z_0-9]+'` 실행):
S04~S15가 각 SP 하나씩이고, S01·S02·S03·S16은 `Legacy:`가 비어 있으며 본문에도 레거시 참조가 없는 **신설 단계**다.
한 단계가 SP 둘 이상을 흡수한 자리는 없다.

**축 A 캐시 이관** — `output/Jobs/POQSettlePrco20/consistency/.cache.json`에서 `axisA`로 시작하는 39개 항목만 옮겼다.
폐포 31개 전부에 대응하는 `axisA:` 항목이 있고, 각 항목의 `key` 해시 집합이 현재 파일 해시와 **전건 일치**한다.
교차 8개도 자기 객체의 세 해시를 포함하며 나머지 해시가 모두 현 폐포 파일의 것이었다. 폐포 밖 항목·이전 Job의 축 B 항목은 옮기지 않았다.

## 3. 단위별 커버리지

| 축 | 단위 | 종류 | 판정 | 상태 | 근거 파일 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| A | UF_GET_COMM4CLIENT | 외부함수 | 정합 | 캐시 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT.Function/raw/object_definition.sql` ↔ `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT/docs/Spec.md` |
| A | UF_GET_COMM4CLIENT4INTEREST | 외부함수 | 정합 | 캐시 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT4INTEREST.Function/raw/object_definition.sql` ↔ `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4INTEREST/docs/Spec.md` |
| A | UF_GET_COMM4CLIENT4PARTIALCANCEL | 외부함수 | 정합 | 캐시 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function/raw/object_definition.sql` ↔ `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL/docs/Spec.md` |
| A | UF_GET_COMM4PG | 외부함수 | 정합 | 캐시 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4PG.Function/raw/object_definition.sql` ↔ `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG/docs/Spec.md` |
| A | UF_GET_COMM4PG4INTEREST | 외부함수 | 정합 | 캐시 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4PG4INTEREST.Function/raw/object_definition.sql` ↔ `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG4INTEREST/docs/Spec.md` |
| A | UF_GET_EXTRACOMM4CLIENT | 외부함수 | 정합 | 캐시 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_EXTRACOMM4CLIENT.Function/raw/object_definition.sql` ↔ `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_EXTRACOMM4CLIENT/docs/Spec.md` |
| A | UF_Get_ExtraCardCommissionAmt | 외부함수 | 정합 | 캐시 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_Get_ExtraCardCommissionAmt.Function/raw/object_definition.sql` ↔ `output/External/SETTLE_CARD_DB/Functions/dbo.UF_Get_ExtraCardCommissionAmt/docs/Spec.md` |
| A | UF_GET_CLIENTSECTIONRATE | 함수 | 정합 | 캐시 | `output/Objects/dbo.UF_GET_CLIENTSECTIONRATE.Function/raw/object_definition.sql` ↔ `output/Functions/dbo.UF_GET_CLIENTSECTIONRATE/docs/Spec.md` |
| A | UF_GET_COLLECTYMD | 함수 | 정합 | 캐시 | `output/Objects/dbo.UF_GET_COLLECTYMD.Function/raw/object_definition.sql` ↔ `output/Functions/dbo.UF_GET_COLLECTYMD/docs/Spec.md` |
| A | UF_GET_INCVTAXRATE | 함수 | 정합 | 캐시 | `output/Objects/dbo.UF_GET_INCVTAXRATE.Function/raw/object_definition.sql` ↔ `output/Functions/dbo.UF_GET_INCVTAXRATE/docs/Spec.md` |
| A | UF_GET_OUTYMD4REFUND | 함수 | 정합 | 캐시 | `output/Objects/dbo.UF_GET_OUTYMD4REFUND.Function/raw/object_definition.sql` ↔ `output/Functions/dbo.UF_GET_OUTYMD4REFUND/docs/Spec.md` |
| A | UF_GET_PGCommOption | 함수 | 정합 | 캐시 | `output/Objects/dbo.UF_GET_PGCommOption.Function/raw/object_definition.sql` ↔ `output/Functions/dbo.UF_GET_PGCommOption/docs/Spec.md` |
| A | UF_GET_ROUND4VAT | 함수 | 정합 | 캐시 | `output/Objects/dbo.UF_GET_ROUND4VAT.Function/raw/object_definition.sql` ↔ `output/Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md` |
| A | UF_GET_SETTLE_EXCHANGERATE | 함수 | 정합 | 캐시 | `output/Objects/dbo.UF_GET_SETTLE_EXCHANGERATE.Function/raw/object_definition.sql` ↔ `output/Functions/dbo.UF_GET_SETTLE_EXCHANGERATE/docs/Spec.md` |
| A | UF_GET_WORKDAY2 | 함수 | 정합 | 캐시 | `output/Objects/dbo.UF_GET_WORKDAY2.Function/raw/object_definition.sql` ↔ `output/Functions/dbo.UF_GET_WORKDAY2/docs/Spec.md` |
| A | UF_Get_CLComm4MobileCo | 함수 | 정합 | 캐시 | `output/Objects/dbo.UF_Get_CLComm4MobileCo.Function/raw/object_definition.sql` ↔ `output/Functions/dbo.UF_Get_CLComm4MobileCo/docs/Spec.md` |
| A | UIF_SettleYMD | 함수 | 정합 | 캐시 | `output/Objects/dbo.UIF_SettleYMD.Function/raw/object_definition.sql` ↔ `output/Functions/dbo.UIF_SettleYMD/docs/Spec.md` |
| A | UP_UTIL_SETTLE_CANCEL_INS | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_CANCEL_INS.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_UTIL_SETTLE_CANCEL_INS/docs/Spec.md` |
| A | UP_UTIL_SETTLE_COMM_UPD | SP | 결함 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/docs/Spec.md` |
| A | UP_UTIL_SETTLE_EXCEPTION_PROC | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md` |
| A | UP_UTIL_SETTLE_EXPECT_PROC | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/docs/Spec.md` |
| A | UP_UTIL_SETTLE_INS | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_INS.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_UTIL_SETTLE_INS/docs/Spec.md` |
| A | UP_UTIL_SETTLE_INS_EXTRA | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/docs/Spec.md` |
| A | UP_UTIL_SETTLE_INS_EXTRA4PLCARD | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD/docs/Spec.md` |
| A | UP_UTIL_SETTLE_PROC_ETC | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC/docs/Spec.md` |
| A | UP_UTIL_SETTLE_SUMMARY_ETC | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_ETC.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_ETC/docs/Spec.md` |
| A | UP_UTIL_SETTLE_SUMMARY_EXTRA | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/docs/Spec.md` |
| A | UP_UTIL_STAT_PGCOLLECT_INS | SP | 정합 | 캐시 | `output/Objects/dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_UTIL_STAT_PGCOLLECT_INS/docs/Spec.md` |
| A | UP_Util_PG_Client_CMRate_Ins | SP | 정합 | 캐시 | `output/Objects/dbo.UP_Util_PG_Client_CMRate_Ins.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_Util_PG_Client_CMRate_Ins/docs/Spec.md` |
| A | UP_Util_Settle_Summary | SP | 정합 | 캐시 | `output/Objects/dbo.UP_Util_Settle_Summary.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_Util_Settle_Summary/docs/Spec.md` |
| A | UP_Util_Settle_Summary_AcqManual | SP | 정합 | 캐시 | `output/Objects/dbo.UP_Util_Settle_Summary_AcqManual.Procedure/raw/object_definition.sql` ↔ `output/Procedures/dbo.UP_Util_Settle_Summary_AcqManual/docs/Spec.md` |
| A 교차 | UF_GET_COLLECTYMD | 교차 | 정합 | 캐시 | `output/Functions/dbo.UF_GET_COLLECTYMD/docs/Spec.md` 의 참조 함수 표 |
| A 교차 | UIF_SettleYMD | 교차 | 정합 | 캐시 | `output/Functions/dbo.UIF_SettleYMD/docs/Spec.md` 의 참조 함수 표 |
| A 교차 | UP_UTIL_SETTLE_COMM_UPD | 교차 | 정합 | 캐시 | `output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/docs/Spec.md` 의 참조 함수 표 |
| A 교차 | UP_UTIL_SETTLE_EXCEPTION_PROC | 교차 | 정합 | 캐시 | `output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md` 의 참조 함수 표 |
| A 교차 | UP_UTIL_SETTLE_EXPECT_PROC | 교차 | 정합 | 캐시 | `output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/docs/Spec.md` 의 참조 함수 표 |
| A 교차 | UP_UTIL_SETTLE_INS | 교차 | 정합 | 캐시 | `output/Procedures/dbo.UP_UTIL_SETTLE_INS/docs/Spec.md` 의 참조 함수 표 |
| A 교차 | UP_UTIL_SETTLE_INS_EXTRA | 교차 | 정합 | 캐시 | `output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/docs/Spec.md` 의 참조 함수 표 |
| A 교차 | UP_UTIL_SETTLE_INS_EXTRA4PLCARD | 교차 | 정합 | 캐시 | `output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD/docs/Spec.md` 의 참조 함수 표 |
| B | S01 날짜 실행 검증 | 신설단계 | 검증 불가 | 신규 | `agent/steps/S01.md` ↔ (레거시 없음) |
| B | S02 영업일 잠금 획득 | 신설단계 | 검증 불가 | 신규 | `agent/steps/S02.md` ↔ (레거시 없음) |
| B | S03 입력 기준시점 고정 | 신설단계 | 검증 불가 | 신규 | `agent/steps/S03.md` ↔ (레거시 없음) |
| B | S04 수수료율 스냅샷 생성 | 단계 | 결함 | 신규 | `agent/steps/S04.md` ↔ `dbo.UP_Util_PG_Client_CMRate_Ins` |
| B | S05 일반 정산 원장 생성 | 단계 | 결함 | 신규 | `agent/steps/S05.md` ↔ `dbo.UP_UTIL_SETTLE_INS` |
| B | S06 취소 정산 반영 | 단계 | 정합 | 신규 | `agent/steps/S06.md` ↔ `dbo.UP_UTIL_SETTLE_CANCEL_INS` |
| B | S07 예외 정책 적용 | 단계 | 결함 | 신규 | `agent/steps/S07.md` ↔ `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC` |
| B | S08 수수료 금액 확정 | 단계 | 결함 | 신규 | `agent/steps/S08.md` ↔ `dbo.UP_UTIL_SETTLE_COMM_UPD` |
| B | S09 우대 추가정산 생성 | 단계 | 결함 | 신규 | `agent/steps/S09.md` ↔ `dbo.UP_UTIL_SETTLE_INS_EXTRA` |
| B | S10 원카드 추가정산 생성 | 단계 | 정합 | 신규 | `agent/steps/S10.md` ↔ `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD` |
| B | S11 수납 지급 일정 산정 | 단계 | 결함 | 신규 | `agent/steps/S11.md` ↔ `dbo.UP_UTIL_SETTLE_EXPECT_PROC` |
| B | S12 정산 요약 생성 | 단계 | 결함 | 신규 | `agent/steps/S12.md` ↔ `dbo.UP_Util_Settle_Summary` |
| B | S13 지급 요약 보정 | 단계 | 결함 | 신규 | `agent/steps/S13.md` ↔ `dbo.UP_UTIL_SETTLE_SUMMARY_ETC` |
| B | S14 미수 후처리 정산 | 단계 | 결함 | 신규 | `agent/steps/S14.md` ↔ `dbo.UP_UTIL_SETTLE_PROC_ETC` |
| B | S15 PG 수납 통계 생성 | 단계 | 결함 | 신규 | `agent/steps/S15.md` ↔ `dbo.UP_UTIL_STAT_PGCOLLECT_INS` |
| B | S16 통합 검증 실행 확정 | 신설단계 | 검증 불가 | 신규 | `agent/steps/S16.md` ↔ (레거시 없음) |

검증 불가 4건은 모두 **신설 단계**다(S01·S02·S03·S16). 사유는 5절 표와 5-1에 적었다 —
레거시 `Spec.md`라는 기준값이 없고, 대체 기준인 `[Approved Step List]`·`[Batch Control Table Contract]`가
그 단계가 쓰는 표 전부를 덮지 않아 컬럼 수·값 수 대조가 성립하지 않는 구간이 남는다.

## 4. 축 A 결함

| 등급 | 객체 | 원본 앵커 | 산출물 앵커 | 원본 | 산출물 | 영향 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| ⚪ | UP_UTIL_SETTLE_CANCEL_INS | object_definition.sql:29 | Spec.md:185 | INSERT INTO TSettleMst … SELECT … WHERE A.PLTID=B.PLTID AND A.YMDCANCEL=@pi_strYMD … — 필터는 INSERT 문 원천 SELECT의 일부다 | mermaid 흐름이 INSERTROW["취소 정산 행 INSERT"] → SOURCEFILTER["A와 B 결합 및 취소일자와 상태 조건 적용"] 순으로 그려 INSERT 뒤에 필터가 적용되는 것처럼 읽힐 수 있다 | 같은 한 문장의 하위 단계를 나눠 그린 표기 문제일 뿐이며, 바로 위 로직 흐름 요약(163~167행)은 순서를 정확히 서술한다. 행 집합·금액에 영향 없음 |
| 🟡 | UP_UTIL_SETTLE_COMM_UPD | object_definition.sql:340 | Spec.md:280 | 기계 확정 DML 범위 표의 UPDATE 10 행(프롬프트 원문, raw/metadata.json RawPromptContext) 술어 컬럼 칸: CLIENTID, PGNAME, MALLID, YMD, USESTATE, CYMD, AYMD, RefundFlag — DDL 336–342 술어와 부합 | Spec.md의 같은 행 칸: CLIENTID, PGNAME, MALLID, YMD, PGNAME, USESTATE, CYMD, AYMD, RefundFlag — 「수정 금지」 표에 PGNAME 한 개가 추가로 끼어듦 | DDL에 PGNAME 술어가 실제로 둘(337 조인, 340 IN) 있어 행 집합·금액은 불변. 다만 기계 확정 표의 축자 전사 계약 위반으로 L1 행 대조가 이 행에서 어긋난다 — 표기·추적성 결함 |
| ⚪ | UP_UTIL_SETTLE_INS_EXTRA | object_definition.sql:196 | Spec.md:111 | LEFT OUTER JOIN TClientContract E WITH(NOLOCK) ON A.CLIENTID = E.CLIENTID | SELECT 대상 테이블 표의 TClientContract 설명 칸이 `A.CLIENTID = E.CLIENTID` 조인 술어를 직접 적었다 | 2026-08-23부터 금지된 '설명 칸의 술어 서술'이나, 내용이 DDL 및 DML 범위 표(INSERT 1 조인 키 CLIENTID)와 일치하고 단일 문장(INSERT 1)만 언급해 문장 묶음 오류가 없다 — 표와 어긋나지 않으므로 정보로만 기록 |
| ⚪ | UP_Util_PG_Client_CMRate_Ins | object_definition.sql:4 | Spec.md:26 | -- ProcedureName   : UP_Util_PG_Client_CMRate_Ins (CREATE 구문의 객체명 [dbo].[UP_Util_PG_Client_CMRate_Ins]과 완전히 동일) | 헤더 주석의 `ProcedureName : UP_Util_PG_Client_CMRate_Ins`는 실제 생성 객체 `UP_Util_PG_Client_CMRate_Ins`와 대소문자 표기만 다릅니다. | 인용된 두 이름이 글자까지 동일한데 '대소문자만 다르다'고 서술 — 대문자 표기는 라인 2의 스크립트 배너 주석(UP_UTIL_PG_CLIENT_CMRATE_INS)에만 있다. 구현·행 집합·금액에 영향 없음, 정보 수준의 서술 오류 |

### 4-1. 전 객체 공통 결함

없다. 축 A 31단위 중 결함 판정은 `UP_UTIL_SETTLE_COMM_UPD` 하나(🟡)이고, 나머지 3건은 정합 판정 안의 ⚪ 기록이다.
네 건 모두 **표기·추적성** 등급으로 행 집합·금액에는 영향이 없다.

🟡 `COMM_UPD`의 DML 범위 표 UPDATE 10 술어 컬럼 칸 중복 전사는 2026-08-24에 L1 `MechanicalValidator`가
그 칸의 렌더 문자열 정확 일치를 요구하도록 조여져(`CheckDmlScopeTable`), **다음 재생성 때 시정 지시로 실려 닫힌다** —
명세서를 강제로 다시 만들 필요는 없다. 축 B에서 이 자리가 S08로 흘러 들어간 흔적은 없다(S08 결함 4건은 모두 SET 산식 쪽이다).

### 4-2. 축 A 교차 — 명세서가 참조 함수를 다루는 자리

교차 8단위 전부 `정합`이다. 대조 대상은 각 명세서의 「### 참조 함수 (기계 확정 — 수정 금지)」 표이며,
폐포 31개 명세서를 이 판에서 다시 훑은 결과 표를 가진 객체는 정확히 8개 — SP 6개(`UP_UTIL_SETTLE_INS`·
`UP_UTIL_SETTLE_EXCEPTION_PROC`·`UP_UTIL_SETTLE_COMM_UPD`·`UP_UTIL_SETTLE_EXPECT_PROC`·
`UP_UTIL_SETTLE_INS_EXTRA`·`UP_UTIL_SETTLE_INS_EXTRA4PLCARD`)와 **함수 2개**
(`UIF_SettleYMD`, `UF_GET_COLLECTYMD`)이고, 교차 단위 8개와 글자까지 일치한다. 나머지 23개에는 표가 없다.
표가 없는 것은 DML 문장 안 호출이 없다는 뜻이므로 결함이 아니다(`axis-a.md` 3-2절 첫 행).

한 가지는 기록해 둔다 — `SKILL.md` 1절은 실측으로 "함수 객체는 0/4가 실린다, `UIF_SettleYMD`도
`UF_GET_COLLECTYMD`도 표가 없는 것이 정상"이라고 적고 있는데, **현재 산출물에서는 그 두 함수가 표를 가진다**.
스킬의 실측 기록이 지금 도구의 동작보다 낡았다. 이번 판정은 실측을 따랐다.
행별 판정과 사각지대(`SELECT`·`SET`·`IF` 안의 호출) 확인 결과는 캐시 항목 `axisA-cross:*`의 `note`에 그대로 남아 있다.
이 판에서 새로 연 함수 DDL은 없다 — 8단위 모두 캐시 재사용이다.

## 5. 축 B 결함

| 등급 | 단계 | Spec 앵커 | 산출물 앵커 | Spec | 산출물 | 영향 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| 🔴 | S07 | Spec.md:121-237 | S07.md:143-163,237-241 | 갱신 4~11·14·15의 SET 산식이 명세서에 전문으로 있다: 갱신 4 CLCOMMTYPE/CLCOMM/CLVT의 ABS 비교 동시평가 CASE, 갱신 5 PGCOMM의 5분기 구간 CASE(150·150·ROUND(...,-1,1)·1500·3000)와 PGCOMMTYPE=1·PGVT=0, 갱신 6 IIF(MALLID='LOLLETTER4',200,180)/IIF(...,20,18), 갱신 7 CAST(B.CommissionMinAmt AS INT)*IIF(PGCOMM<0,-1,1)와 PGVT의 0.1 계수, 갱신 8·9 MinCommissionAmt 부호 보존과 UF_GET_ROUND4VAT 산식, 갱신 10·11 CAST(ROUND(x*0.1,0,dbo.UF_GET_PGCommOption(A.PGNAME,5)) AS INT), 갱신 14 UF_GET_COMM4CLIENT4PARTIALCANCEL(9인자)와 UseState=2 필터, 갱신 15 UF_Get_CLComm4MobileCo(6인자)와 MobileCo IN ('1'~'6')·MobileCoCommApply='Y' | 단계는 해당 자리에 SET @v_currentStepId 대입과 한 줄 주석만 남기고 UPDATE 문 본문을 전혀 싣지 않는다(예: S07.md:144 `/* U4: ... 고객사 최저수수료 */`, S07.md:147, 151, 154, 157, 160, 163, 238, 241). S07.md:353이 산문으로 '원본대로 유지한다'고만 적는다 | 18개 갱신 중 10개의 상수·계수·부호·반올림 자릿수와 UDF 인자가 지시서에 없다. 이 절만으로 구현하면 CLCOMM·CLVT·PGCOMM·PGVT·PGETC·CLCOMMTYPE·PGCOMMTYPE 결과 금액이 원본과 달라진다. 특히 갱신 4·6·7·8의 SET 우변 동시평가(Spec.md:129,149,158,167)는 산식이 없으면 순차 대입으로 이관되어 조용히 값이 틀어진다 |
| 🔴 | S14 | Spec.md:62-75 | S14.md:11-14 | 지역 변수 표가 @v_intID INT, @v_strClientID VARCHAR(20), @v_strYMD VARCHAR(8), @v_strOutYMD VARCHAR(8), @v_intCLTotal MONEY, @v_intCLComm MONEY, @v_intCLVT MONEY, @v_intPostChkAmt1 MONEY, @v_intPostChkAmt2 MONEY의 타입을 확정한다 | 단계 SQL의 DECLARE 블록은 @v_currentStepId, @v_intIssueType, @v_strComment, @v_strHUserID 4개만 선언하고, 위 9개 변수는 선언 없이 FETCH INTO·SET·SELECT 대입에 그대로 사용된다 | 금액 3종(@v_intCLTotal, @v_intCLComm, @v_intCLVT)의 MONEY 타입이 단계에서 사라졌고 변수명은 int를 시사한다. 이행자가 Spec 지역 변수 표를 따로 참조하지 않고 이름대로 INT/DECIMAL로 선언하면 커서 집계의 소수부가 절삭되어 TSettleMiss.CLSettleAmt·CLComm·CLVT 값과 -3 금액 검증 결과가 원본과 달라진다. 선언이 아예 없어 제시된 블록 자체는 그대로 실행되지도 않는다 |
| 🟠 | S07 | Spec.md:279 | S07.md:353 | DML 범위 표 UPDATE 7의 GROUP BY 칸은 `—`이고, 집합 술어 표(Spec.md:331-336)의 UPDATE 7 술어는 A.PGNAME=B.PGNAME, A.MALLID=B.MALLID, A.YMD=@pi_strYMD, A.PGNAME IN ('CheckPay','Toss','TossPoint'), ABS(A.PGCOMM) < B.CommissionMinAmt, (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))뿐이다. 집계도 HAVING도 없다 | "특히 U7의 `PLTID`별 `HAVING SUM(TxAmt) = 0` 집계 … 는 단일 키로 분할하지 않는다" | 명세서에 존재하지 않는 PLTID 단위 집계 필터를 단계가 원본 로직으로 서술한다. 이 서술대로 구현하면 U7의 갱신 대상 행 집합이 PLTID 합계가 0인 건으로 좁혀져 CheckPay·Toss·TossPoint의 PG 최저수수료가 원본보다 훨씬 적은 행에만 적용된다 |
| 🟠 | S07 | Spec.md:285,372,373 | S07.md:215-235 | DML 범위 표 UPDATE 13의 최상위 술어 컬럼은 PLTID, ID, YMD, PGNAME이고, 집합 술어 표에 최상위 `Y.YMD = @pi_strYMD`(라인 380)와 `Y.PGNAME IN (SELECT Value FROM STRING_SPLIT(@v_strCardPGNames,'+'))`(라인 381)가 명시된다 | 최상위 UPDATE는 `FROM SETTLE_POQ_DB.dbo.TSettleMst AS Y INNER JOIN CardCost AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;`로 끝나고 WHERE 절 자체가 없다. YMD·PGNAME 필터는 파생 테이블 CardCost 안(S07.md:211-212)에만 남았다 | (PLTID, ID)가 TSettleMst에서 유일하지 않은 배포에서는 기준일·원천PG 조건 밖의 행까지 조인되어 CLCOMM·CLVT·PGCOMM·PGVT·PGETC·PGVTTYPE·PGIntRealComm·ProcState가 덮어써진다. 유일하면 무해하므로 배포 구성에 달린 결함으로 높은 쪽 등급을 매긴다 |
| 🟠 | S09 | Spec.md:364 | S09.md:50 | IF 1(-9 사전 검증)의 조건은 ProcYMD = @pi_strYMD, YMD >= @v_strReqYMD, OutState IN (1,5), OutYMD IS NOT NULL, 지정 PG명, 지정 사업자 매출구분, ExtraSettleFlag = 1 일곱 개다. TxAmt 술어는 없다(집합 술어 표에도 IF 1 행이 없어 다른 기준값이 존재하지 않는다) | 같은 EXISTS에 `AND SM.TxAmt = 0`을 하나 더 붙여 여덟 개 조건으로 검사한다 | 가드가 보는 행 집합이 좁아진다. 이미 지급 처리된(OutState IN (1,5)) 행이 TxAmt <> 0이면 원본은 -9로 즉시 반환하지만 단계는 통과시켜 DELETE 1 → INSERT 1로 그 처리일자의 차액정산 행을 다시 만든다. 중복 정산 방지 가드가 약해지는 방향이다 |
| 🟠 | S09 | Spec.md:165 | S09.md:256 | UPDATE 1의 OutYMD는 `(SELECT OutYMD FROM dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID))` 스칼라 하위 질의 결과를 대입한다 | `CROSS APPLY dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID) AS S`로 결합하고 `A.OutYMD = S.OutYMD`를 대입한다 | UIF_SettleYMD가 행을 돌려주지 않는 인자 조합에서 원본은 OutYMD에 NULL을 넣으면서 OutState = 2는 대입하지만, CROSS APPLY는 그 행 자체를 갱신 대상에서 제외해 OutState도 갱신되지 않는다. 뒤이은 UPDATE 2가 `OutState = 2`를 조건으로 삼으므로 지급일 재계산 대상까지 함께 빠진다. 전제: 함수가 모든 (YMD, SettlePeriodID)에 대해 정확히 한 행을 보장하면 영향이 없고 등급은 ⚪로 내려간다. 보장 여부가 배포 데이터에 달려 있어 높은 쪽으로 매겼다(OUTER APPLY를 쓰면 원본과 같아진다) |
| 🟠 | S11 | Spec.md:207 | S11.md:172 | UPDATE 9의 조인 키는 PLTID, YMD, UseState, DiscountFlag, DiscountAmt, TxAmt, Amt, ClientID, PGName, MallID이고, SELECT 대상 표(Spec.md:101)도 TPLCardEDIMst의 참조 컬럼으로 PLTID, YMD, UseState, Amt, ReqYMD, AcqType을 든다 | -13 블록의 TPLCardEDIMst 결합이 ON A.PLTID = E.PLTID AND ABS(IIF(ISNULL(A.DiscountFlag,'N')='Y',A.DiscountAmt,A.TxAmt)) = ABS(E.Amt) 두 조건뿐이고, 조인 키 YMD와 UseState에 해당하는 결합이 없다(E.YMD·E.UseState는 단계 SQL 어디에도 등장하지 않는다) | 매입요청 원장 E와의 결합이 원본보다 느슨해져 같은 PLTID·같은 금액의 다른 일자·다른 상태 행까지 매칭된다. 대상 행 집합이 넓어질 뿐 아니라 하나의 A 행에 복수 E 행이 붙어 EDIReqYmd = E.ReqYMD와 UIF_SettleYMD(E.ReqYMD, B.SettlePeriodID)로 산출되는 지급일이 비결정적으로 정해진다 |
| 🟠 | S13 | Spec.md:26 | S13.md:16,134 | 성공 시에만 @po_intRetVal = 0이며, 삭제 오류는 1001, 삽입 오류는 1002다. 그 밖의 경로에서는 선언 기본값 1000이 유지된다(Spec.md:56). | DECLARE @v_currentStepId INT = 0으로 시작하고 CATCH가 SET @po_intRetVal = @v_currentStepId를 무조건 수행하므로, 커서 DECLARE·OPEN·첫 FETCH 실패나 커서 행 0건일 때의 CLOSE/DEALLOCATE/COMMIT 실패가 성공 코드 0으로 보고된다. | 실패가 성공으로 보고되면 오케스트레이터가 단계를 Succeeded로 기록해 재실행하지 않고, TSettleByOUT 보정이 누락된 채 후속 정산이 진행된다. 트랜잭션은 롤백되므로 잘못된 금액이 기록되는 것이 아니라 보정 대상 행이 통째로 빠지는 쪽이라 🟠로 매겼다. 전제: 발현 경로가 DML 바깥 오류로 한정되며, 이 0 초기화는 common/01-step-contract.md의 공통 CATCH 패턴을 그대로 따른 결과다. |
| 🟠 | S14 | Spec.md:196-199, 226-230 | S14.md:52-59 | 반복 시작마다 SET @v_intID = 0으로 재설정한 뒤 비집계 SELECT 2로 TSettleMiss.ID를 대입하고(무결과면 0 유지), @@ROWCOUNT > 1일 때만 SELECT 3의 MAX(ID)를 다시 대입하며, 갱신/삽입 분기는 @v_intID > 0으로 가른다 | SELECT 2·SELECT 3과 @@ROWCOUNT > 1 분기를 집계 SELECT @v_intID = MAX(ID) 한 문장으로 합치고, 분기 조건을 IF @v_intID IS NOT NULL로 바꾸었다(SET @v_intID = 0 재설정도 없다) | 일치 행이 0건이거나 2건 이상일 때는 원본과 결과가 같지만, 조건에 맞는 TSettleMiss 행의 ID가 0 이하인 배포에서는 원본이 INSERT로 가는 자리에서 단계는 UPDATE로 가 변경 대상 행 집합이 달라진다. ID를 MAX(ID)+1로 채우는 관행상 좁은 경로지만 시드/보정 데이터에 0 이하 ID가 있으면 성립한다 |
| 🟡 | S03 | raw/prompt-context.md:5101 | S03.md:39-50 | [Approved Step List] S03 행은 대상 테이블로 batch.SourceSnapshot, batch.ControlTotal, batch.BatchCheckpoint, batch.BatchStepJournal 넷을 선언한다 | 단계 본문의 DML은 batch.BatchStepJournal·batch.BatchCheckpoint 둘에만 있고, batch.SourceSnapshot과 batch.ControlTotal에 대한 INSERT/UPDATE는 한 줄도 없다. 그 자리는 산출물 어디에도 정의가 없는 batch.usp_CaptureSourceSnapshot·batch.usp_WriteS03ControlTotal 두 프로시저 EXEC과 '원천을 물리화한다', '건수·금액·해시 통제값을 기록한다'는 주석으로 대체되어 있다(두 이름은 Job 산출물 전체 grep에서 S03.md 외 0건) | 선언된 쓰기 집합의 절반에 대해 컬럼 집합·원천 테이블·필터가 전혀 특정되지 않아 대조할 대상 자체가 없다. MigrationInstructions.md 32행(§1-7)의 플레이스홀더 금지에도 걸리며, 구현자는 스냅샷 원천과 통제값 정의를 스스로 지어내야 한다. 재실행 시 SourceSnapshot 중복 적재 여부도 판정 불가다 |
| 🟡 | S03 | raw/prompt-context.md:5102-5114 | S03.md:91 | S04~S15의 [Approved Step List] Tables 칸은 모두 SETTLE_POQ_DB.dbo.* 원본 테이블만 대상으로 삼으며 batch.SourceSnapshot을 입력으로 지정하지 않는다 | S03 말미는 'batch.SourceSnapshot과 batch.ControlTotal은 S04 이후 단계의 재조회 대신 기준 입력과 검증 기준으로 사용한다'고 단언하지만, agent/steps/*.md 전체에서 batch.SourceSnapshot을 참조하는 단계는 S03 하나뿐이다(S04~S15 0건). batch.ControlTotal만 S16이 검증 기준으로 읽는다 | 단계 간 계약이 한쪽에서만 선언되어 있다. 구현자가 S03 문장을 그대로 믿으면 S04 이후를 스냅샷 기반으로 재작성하게 되어 각 단계의 원본 필터와 달라지고, 반대로 각 단계 지시서를 따르면 S03의 스냅샷 적재는 아무도 읽지 않는 비용으로 남는다 |
| 🟡 | S05 | Spec.md:42 | S05.md:23 | -9는 `YMD=@pi_strYMD` · `OutState IN (1,5)` · `OutYMD IS NOT NULL` 행이 존재하는 기정산 조건에만 대응한다 | `DECLARE @v_currentStepId INT = -9`로 초기화하고 사전 검증 질의를 TRY 안에 두어, 그 질의 자체가 실패해도 CATCH가 `@po_intRetVal = -9`(S05.md:218)를 반환한다 | 기정산 존재라는 업무 조건과 사전 검증 질의의 SQL 장애가 같은 코드로 보고되어 운영 오진단·재실행 판단 오류를 낳는다. 금액·행 집합은 바뀌지 않는다 |
| 🟡 | S07 | Spec.md:75 | S07.md:360-377 | 명세서의 TSettleMst 참조 컬럼 집합(SELECT 대상 테이블 표)에 CLTOTAL, PGTOTAL, PGINTEXPCOMM은 없다. 이 SP는 CLCOMM·CLVT·CLETC·CLIntComm·PGCOMM·PGVT·PGETC·PGIntRealComm만 다루며 총액 컬럼을 읽지도 쓰지도 않는다 | 완료 검증 쿼리가 A.CLTOTAL, A.PGTOTAL, A.PGINTEXPCOMM을 읽어 `CLTOTAL <> CLCOMM+CLVT+CLETC+CLINTCOMM` 등 총액 항등식 위반을 S07의 완료 조건으로 삼는다 | S07이 총액 컬럼을 갱신하지 않으므로, 총액을 뒤 단계가 채우는 순서라면 이 검증은 S07 직후 항상 실패한다. 실행 결과가 아니라 검증 게이트만 오탐하므로 표기·추적성 등급 |
| 🟡 | S08 | Spec.md:86, Spec.md:493 | S08.md:5 | 실행 UPDATE 8(inivacct)의 @@ERROR<>0 분기가 @po_intRetVal = -9를 설정한다 — -9는 이 프로시저의 실행 DML에 대응하는 코드다 | "-9도 이 프로시저의 실행 DML에 임의 배정하지 않는다"고 서술한다 | 단계 본문이 자기 SQL(S08.md:125의 SET @v_currentStepId = -9; 직후 UPDATE 8)과 정면으로 어긋난다. Job 전역에서 -9가 기정산 원장 사전 검증 코드로 쓰이는 탓에(common/00-architecture.md:67-75 전 단계 목록 선두, common/01-step-contract.md:7) 생긴 혼선으로 보이나, 문언대로 -9 배정을 걷어내면 UPDATE 8 실패 시 원본과 다른 코드가 반환된다. 코드 블록이 올바르므로 등급은 추적성에 둔다 |
| 🟡 | S08 | Spec.md:243 | S08.md:170-173 | 갱신 13의 SET 대상은 CLTotal, CLEtc, CLComm, CLVT, POQIncome 5개이며 CLEtc = 0이다 | "CLTotal, CLComm, CLVT, POQIncome의 모든 SET 우변은 갱신 전 값으로 계산하며"만 적고 CLEtc는 CAST(CLEtc/@v_valIncVat AS INT) 피연산자로만 등장한다 — SET 대상 CLEtc = 0이 어디에도 없다 | UPDATE 13 전체가 이 주석 하나로만 기술되므로, 단계만 보고 구현하면 CLVTType=1 대상 행의 고객사인증비가 0으로 초기화되지 않고 종전 값이 TSettleMst에 남는다. 같은 문장 안에서 CLTotal이 갱신 전 (CLComm+CLEtc+CLIntComm)로 계산되므로 총액 자체는 보존되지만 CLEtc 컬럼 값이 원본과 달라진다 |
| 🟡 | S08 | Spec.md:204-212 | S08.md:134-136 | 갱신 9(easybank)의 SET은 갱신 8(inivacct)과 다르다 — PGVT는 조건 없이 0이고(갱신 8은 0 + IIF(ISNULL(C.CommissionCancelFlag,0)=0, 0, CAST(...))), CLVT는 dbo.UF_GET_ROUND4VAT(B.CommissionCancelAmt*dbo.UF_GET_INCVTAXRATE(A.CLVTType))로 갱신 8에 있는 A.CLComm 항이 없다 | "UPDATE 8과 동일한 조인 구조를 유지하되 A.YMD=@pi_strYMD, A.USESTATE IN (1,2), A.PGNAME IN ('easybank')를 적용한다"만 적고 SET에 대한 언급이 전혀 없다 | 차이를 WHERE에만 한정해 서술한 탓에 SET까지 UPDATE 8과 같다고 읽히기 쉽다. 그대로 복제하면 easybank 취소건의 PGVT와 CLVT 금액이 원본과 달라진다(그 경우 영향은 🔴). 단계가 틀린 산식을 적시하지는 않았으므로 등급은 추적성에 둔다 |
| 🟡 | S08 | Spec.md:127-130, Spec.md:136-139, Spec.md:166-167, Spec.md:189-192 | S08.md:24-34, S08.md:37-48, S08.md:80-83, S08.md:105-123 | 갱신 1·2·4·7의 SET 원천 표현식이 상수와 반올림 방식까지 확정되어 있다 — 예: PGVT = PGVT + CAST(C.CommissionCancelAmt*(0.1) AS INT)(갱신 4), 3인자 ROUND의 반올림 모드로 dbo.UF_GET_PGCommOption(A.PGNAME,3)/(A.PGNAME,5)를 전달하는 중첩식(갱신 1·2), CAST((B.TXAMT-ISNULL(B.NonSettleAmt,0))*(CAST(A.ALLOTPERIOD AS INT)*0.01) AS INT)(갱신 1) | 조인·WHERE만 옮겨 적고 SET은 "PG 취소수수료", "원문 그대로 적용한다", "원문 순서대로 적용한다" 같은 지시로 대체했다 | 계수 0.01·0.1, 반올림 자릿수와 3인자 ROUND의 모드 인자, CAST AS INT 절사 시점, UF_GET_ROUND4VAT·UF_GET_INCVTAXRATE의 인자가 단계 안에 존재하지 않아 계산식과 UDF 호출 항목을 단계만으로는 대조할 수 없다. 자리 표시 주석 12건은 이 Job 16개 단계 중 S08에만 몰려 있어(다른 단계는 0~1건) 이 단계 고유의 서술 누락이다 |
| 🟡 | S12 | Spec.md:103-204 | S12.md:75-108 | Spec은 TPartialCancelByTX 28개, TSettleByIN 28개, TSettleByOUT 32개 컬럼의 대상 컬럼명과 원천 매핑(그룹 키 열 대 SUM(ISNULL(...,0)) 집계 열)을 표로 전부 확정한다 | 단계는 INSERT 1만 컬럼 27개/값 27개를 명시하고, INSERT 2·3·4는 대상 컬럼 목록 자체가 없이 `INSERT INTO ... SELECT`이며 SELECT 목록을 `/* TSettleByTX와 동일한 금액 집계 열 및 원본 GROUP BY 열 */`, `/* 원본의 COUNT 및 SUM ISNULL 산식 전체 */` 주석으로 대체했다 | 컬럼 수·값 수 대조가 단계 문서만으로는 불가능하다. 특히 컬럼 목록 없는 INSERT는 대상 테이블의 물리 컬럼 순서에 의존하는데 Spec은 TPartialCancelByTX에서 PLTID를 세 번째 컬럼으로 놓아 TSettleByTX와 순서가 어긋나므로, 구현 시 열 누락·오정렬이 검출되지 않고 통과할 수 있다 |
| 🟡 | S13 | Spec.md:55 | S13.md:137-138 | 삭제 오류 시 'TSettleByOUT DELETE 실패('에 고객사·결제수단·거래일을 연결한 문자열을, 삽입 오류 시 'TSETTLEBYOUT에 재 등록 실패('에 동일 차원 값을 연결한 문자열을 @po_strErrMsg에 대입한다. | 차원 값 없이 고정 문자열 'TSettleByOUT DELETE 실패' / 'TSettleByOUT 재등록 실패'만 대입한다. | 실패한 커서 차원(고객사·결제수단·거래일)이 batch.BatchStepJournal.ErrorMessage에 남지 않아, 어느 차원에서 보정이 멈췄는지 운영에서 역추적할 수 없다. 삽입 실패 문구도 원문과 다르다. |
| 🟡 | S14 | Spec.md:211 | S14.md:8-17 | 실행 시작 시 SET NOCOUNT ON을 설정하고, 기존 세션에 열린 트랜잭션이 있으면 @@TRANCOUNT <> 0 조건에서 즉시 ROLLBACK TRAN을 실행한다 | 단계 SQL은 SET XACT_ABORT ON, SET TRANSACTION ISOLATION LEVEL SNAPSHOT만 설정하고 바로 BEGIN TRY / BEGIN TRAN으로 들어간다. 진입부의 @@TRANCOUNT 검사와 사전 ROLLBACK, SET NOCOUNT ON이 모두 사라졌다 | 오케스트레이터가 단계 호출 바깥에서 트랜잭션을 열지 않는 구성이면 무영향이지만, 열어 두는 구성에서는 원본이 그 트랜잭션을 끊고 시작하는 것과 달리 단계는 중첩 트랜잭션이 되어 -3 경로의 ROLLBACK TRAN이 앞 단계 결과까지 되돌린다. 단계 지시서에 전제가 적혀 있지 않아 이행자가 판단할 근거가 없다 |
| 🟡 | S15 | Spec.md:103-105 | S15.md:98,112 | 외부 집계의 대상 컬럼 매핑이 LOWER(TBL1.CLIENTID)·LOWER(TBL1.PGNAME)·LOWER(TBL1.MALLID)로, 원본은 파생 테이블에서 한 번 소문자화한 값을 외부 SELECT·GROUP BY에서 다시 LOWER로 감싼다 | 외부 SELECT와 GROUP BY가 CLIENTID, PGNAME, MALLID를 LOWER 없이 그대로 참조한다 | LOWER는 멱등이고 세 UNION 분기가 모두 이미 소문자화하므로 삽입 값과 그룹 경계는 동일하다. 결과 금액·행 집합에는 영향이 없고 명세서 매핑 표와 단계 SQL의 문자열 대조가 어긋나는 표기·추적성 결함이다 |
| 🟡 | S15 | Spec.md:133 | S15.md:112 | DML 범위 표(기계 확정)의 INSERT 1 행 ORDER BY 칸이 INYMD, CLIENTID, PGNAME, MALLID이다 | 단계 SQL의 INSERT ... SELECT에는 ORDER BY 절이 없고 GROUP BY만 있다 | INSERT ... SELECT의 ORDER BY는 저장된 행 집합이나 금액에 영향을 주지 않으므로 결과는 동일하다. 기계 확정 표의 칸과 단계 렌더 문자열이 불일치해 추적성만 손상된다 |
| 🟡 | S16 | raw/prompt-context.md:5150 | S16.md:56 | `batch.BatchValidationIssue.ActualValue`는 nvarchar(200)이다 (ExpectedValue도 같음, 5149행) | `B161` 경로가 길이 제한 없는 `ex.Message`를 `actualValue`로 그대로 넘긴다 (`actualValue: ex.Message`). 절단·검증 규정이 단계 어디에도 없다 | 200자를 넘는 예외 메시지에서 INSERT가 문자열 절단 오류로 실패한다. 이 INSERT는 CATCH 안의 실패 기록 트랜잭션 첫 문장이라, 실패하면 같은 트랜잭션의 저널·BatchRun 실패 기록까지 함께 날아가 실행이 원인 불명으로 남는다 |
| ⚪ | S01 | raw/prompt-context.md:5117 | S01.md:60 | [Batch Control Table Contract]는 batch.BatchRun·BatchStepJournal·BatchCheckpoint·BatchValidationIssue 네 표의 컬럼만 고정하며 batch.ControlTotal의 컬럼 계약은 어디에도 없다 | S01은 'INSERT INTO batch.ControlTotal VALUES /* RunId, S01, BatchYmd, … */'처럼 컬럼 목록과 값 목록을 주석 자리표시자로만 남긴다(60~61행, 67~68행) | ControlTotal 자체에 기준값이 없어 컬럼 수·값 수 대조가 성립하지 않는다. S03(46~50행)·S16(72행)도 같은 방식으로 추상 참조만 하므로 S01 고유의 이탈은 아니고, Job 전체에 걸친 계약 공백이라 정보로만 남긴다 |
| ⚪ | S02 | output/Jobs/POQSettleBatch1/agent/steps/S01.md:57 | S02.md:63-67 | 실패 경로에서 batch.BatchRun을 Failed로 전이할 때 CompletedAtUtc = SYSUTCDATETIME()을 함께 기록한다(S16.md의 MarkFailedAsync 경로도 동일 관례를 따른다) | S02의 catch 블록은 UPDATE batch.BatchRun SET RunStatus = 'Failed', ErrorMessage = @ErrorMessage 만 수행하고 CompletedAtUtc를 갱신하지 않는다 | B110으로 중단된 실행은 RunStatus='Failed'이면서 CompletedAtUtc가 NULL로 남아 실행 종료 시각 추적과 소요시간 집계가 S01·S16 실패 건과 달라진다. 배치 제어 테이블 계약이 CompletedAtUtc를 NULL 허용으로 두므로 계약 위반은 아니며 금액·행 집합에는 영향이 없다. 신설 단계라 기준 Spec.md가 없어 인접 단계 관례를 앵커로 삼은 정보성 항목이다 |
| ⚪ | S04 | Spec.md:75 | S04.md:19-26 | SELECT 대상 표가 TSettleMst의 참조 컬럼으로 PLTID, YMD, OutState, OutYMD를 적는다 | 단계의 EXISTS는 SELECT 1과 YMD·OutState·OutYMD 세 조건만 쓰고 PLTID를 전혀 언급하지 않는다 | Spec의 로직 흐름 요약 1항과 집합 술어 표는 IF 1의 조건으로 세 컬럼만 확정하므로 PLTID는 EXISTS 내부 투영으로 보이며 그렇다면 의미 차이가 없다. 다만 PLTID가 실제로는 술어였을 가능성은 Spec만으로 배제되지 않으니 축 A의 원본 대조로 확인이 필요하다 |
| ⚪ | S05 | Spec.md:19 | S05.md:14 | 단계 본문은 이 단계를 `SettleLedgerGenerationStep`으로 부른다(S05.md:9) | 프로시저 이름이 `batch.ExecuteS05_SetttleLedgerGeneration`으로 `Settt`(t 3개) 오타다 | 파일 안에서는 한 번만 등장해 일관되므로 동작에는 영향이 없으나, 그대로 배포되면 객체 이름에 오타가 남는다 |
| ⚪ | S09 | Spec.md:354 | S09.md:205 | PGComm의 dacomcard/tosscard 분기는 `CAST((A.TxAmt * ((B.CommissionRate * 1.1) / 100.0)) AS INT) - CAST((A.TxAmt * ((B.CommRate0 * 1.1) / 100.0)) AS INT)`로 나눗셈을 먼저 묶는다 | `CAST(A.TxAmt * (B.CommissionRate * 1.1) / 100.0 AS INT) - CAST(A.TxAmt * (B.CommRate0 * 1.1) / 100.0 AS INT)`로 왼쪽 결합에 맡겨 곱셈을 먼저 한다(WHEN 0~3 네 분기 모두) | 정보. 곱셈은 정확 연산이고 나눗셈 결과 스케일도 두 형태가 같게 계산되어 통상 수수료율 정밀도에서는 값이 같다. 다만 T-SQL decimal 스케일 규칙상 피연산자 정밀도가 커지면 절사 지점이 달라질 수 있으므로 Spec 원문 괄호를 그대로 두는 편이 안전하다. 비-dacomcard 분기(S09.md:217~220)는 Spec과 괄호까지 동일하다 |
| ⚪ | S10 | Spec.md:280 | S10.md:65 | INSERT 1 직후에는 별도의 @@ERROR 검사나 출력 매개변수 오류 대입이 없다 - 원본은 -2를 두 번째 UPDATE 직후에만 한 번 대입한다 | INSERT 1과 UPDATE 1 직전에도 @v_currentStepId = -2를 설정하므로 두 문장의 실패도 XACT_ABORT/TRY-CATCH로 롤백되고 -2가 반환된다 | 원본에서 INSERT 1이 실패하면 DELETE 1 결과만 커밋되고 호출자는 오류를 볼 수 없었으나 이식본은 전량 롤백하고 -2를 낸다. 오류 코드 집합은 늘지 않고(-9,-1,-2 그대로) 단계 본문이 '삽입 및 후속 갱신 -2'로 명시하며 common/01-step-contract.md의 공통 TRY/CATCH 규약을 따른 결과이므로 결함이 아닌 기록으로 남긴다 |
| ⚪ | S12 | Spec.md:59,290-291 | S12.md:138-144 | 하위 프로시저가 비영 값을 반환하면 그 값을 유지한 채 롤백하고 반환한다 | 단계의 CATCH는 `@po_intRetVal = @v_currentStepId`로 반환하는데, 하위 SP 호출 구간에서는 @v_currentStepId가 INSERT 4의 -8로 남아 있어 하위 SP가 예외를 던진 경우 -8로 보고된다(반환값이 비영인 정상 경로는 @v_childRetVal로 올바르게 전달됨) | Spec이 정의한 반환값 경로는 보존되며, 예외 전파 경로는 원본에 TRY/CATCH가 없어 Spec에 기준값이 없다. 장애 원인 추적 시 -8이 하위 SP 예외를 가릴 수 있다는 정보성 지적이다 |
| ⚪ | S13 | Spec.md:56 | S13.md:17 | @po_intRetVal INT = 1000 OUT — 선언 기본값이 1000이다. | SET @po_intRetVal = NULL로 초기화한다. | 호출자가 '설정되지 않음'을 1000으로 식별하는 경우 값이 달라진다. 단계 내 모든 경로가 값을 대입하므로 실제 영향은 위 🟠 결함과 같은 뿌리다. |
| ⚪ | S13 | Spec.md:72,94 | S13.md:24-28,40-44 | 커서는 A.OUTSTATE도 읽어 @v_intOutState에 대입한다(삭제 조건에는 직접 쓰이지 않는다). | 커서 SELECT DISTINCT 목록과 FETCH 대상이 13개 차원뿐이고 OUTSTATE가 빠져 있다. | A.OUTSTATE = 9 필터로 값이 상수라 DISTINCT 결과 행 집합이 같고, 이 변수는 삭제·삽입 조건과 오류 메시지 어디에도 쓰이지 않아 동작은 동등하다. 명세서 내부 변수 표와의 대응만 끊긴다. |
| ⚪ | S13 | Spec.md:27 | S13.md:13-14 | SET NOCOUNT ON을 설정하므로 각 DML의 '영향받은 행 수' 메시지를 호출 계층으로 반환하지 않는다. | 세션 제어로 SET XACT_ABORT ON과 SET TRANSACTION ISOLATION LEVEL SNAPSHOT만 두고 SET NOCOUNT ON이 없다. | 커서 반복마다 행 수 메시지가 호출 계층으로 올라가 ExecuteNonQuery 반환값과 메시지 흐름이 원본과 달라진다. 정산 결과값에는 영향이 없다. |
| ⚪ | S14 | Spec.md:239, 243 | S14.md:112-116, 129, 142 | -3 조기 반환 경로에서는 커서의 CLOSE와 DEALLOCATE에 도달하지 않으며, 마지막 RETURN 문에는 리터럴 반환값이 없어 프로시저 정수 반환값 규약이 구현에 없다(호출자는 @po_intRetVal을 확인해야 한다) | -3 경로에서 CLOSE/DEALLOCATE를 먼저 수행한 뒤 롤백하고, 세 경로 모두 RETURN -3 / RETURN 0 / RETURN 4000으로 리터럴 반환값을 신설했다(CATCH에도 CURSOR_STATUS 기반 정리 추가) | 커서 누수 정리는 원본 결함을 닫는 방향이고 금액·행 집합에 영향이 없다. 리터럴 RETURN 신설도 단계 본문이 C# 어댑터는 OUTPUT 값을 LegacyReturnCode로 전달한다고 못박아 두어 호출 계약이 흔들리지 않는다. 원본과 달라진 사실만 기록한다 |
| ⚪ | S15 | Spec.md:141-147 | S15.md:132-148 | 세 원천 필터와 파생 테이블 TBL1의 집계 정의가 기준값이며, 검증 질의는 이를 독립적으로 재현해야 비교가 성립한다 | 검증 SQL의 Expected CTE가 FROM SourceRows를 참조하는데 그 질의 안에 SourceRows CTE 정의가 없고(주석 '/* 본 단계 SourceRows와 동일한 ... */'로 대체), COUNT_BIG(*) AS RowCount는 예약어를 대괄호 없이 별칭으로 쓴다 | 검증 질의가 그대로는 실행되지 않아 이행 검증 절차의 실효성이 떨어진다. 본 이행 DML 자체에는 영향이 없어 정보 등급으로 남긴다 |
| ⚪ | S16 | raw/prompt-context.md:5114 | S16.md:3 | 승인 단계 표는 S16의 `Tables:` 첫 항목으로 `batch.ControlTotal`을 적었다 (00-architecture.md:24도 동일) | S16은 `batch.ControlTotal`을 기준값 조회로만 읽고(3·72행) 한 건도 쓰지 않는다. 쓰기 대상은 ValidationResult·BatchValidationIssue·BatchStepJournal·BatchRun·BatchCheckpoint·BatchRunLock뿐이다 | 승인 표의 대상 테이블 집합과 단계의 실제 변경 집합이 어긋난다. 검증 단계가 통제합계를 소비만 하는 설계라면 의도된 차이지만, 표만 보고 DDL·권한을 잡으면 S16에 불필요한 쓰기 권한을 부여하게 된다 |
| ⚪ | S16 | S16.md:75 | S16.md:89 | 주석이 `-- 집계 양측을 독립 스칼라로 계산한다. CROSS JOIN은 사용하지 않는다.`라고 선언한다 | 바로 아래 SQL이 `INNER JOIN (...) AS ActualSummary ON 1 = 1`로 두 스칼라 파생 테이블을 결합한다 (89·97행) | `ON 1 = 1` 결합은 의미상 CROSS JOIN이므로 주석이 코드와 어긋난다. 양측이 각각 한 행이라 결과는 같지만, 주석을 규칙으로 읽은 구현자가 실제 코드와 다른 판단을 하게 된다 |

### 5-1. 전 단계 공통 결함

아래 네 군은 **한 원인이 여러 단계에 같은 모양으로 반복된 것**이라 위 표에서 빼고 여기에 한 번만 적는다.
표에 남은 37행과 합해 축 B 결함은 모두 46건이다.

**(가) 잠금 힌트 일괄 제거 — S04 🟡 · S05 ⚪ · S07 ⚪ (3건)**
명세서의 잠금 힌트 표(기계 확정)는 원천 조회 전부에 `WITH(NOLOCK)`이 걸려 있음을 확정한다(S04 17곳, S07 42곳).
단계 SQL은 힌트를 하나도 싣지 않는다. S07·S12는 `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`으로 치환했고,
S04에는 치환도 그 사실을 적은 문장도 없다. `common/01-step-contract.md:5`가 이 치환을 Job 전역 정책으로 두므로
대부분 의도된 차이지만, **단위별 등급이 갈렸다**(S04는 🟡, 나머지는 ⚪) — S04만 대체 격리 수준 선언이 없어서다.
S05에는 여기에 더해 `INDEX=CIDX_TTxMst_YMD` **인덱스 힌트**까지 함께 사라졌는데, 전역 정책은 인덱스 힌트를 다루지 않는다.
금액·행 집합은 어느 단계에서도 달라지지 않는다.

**(나) 검증 SQL의 예약어 별칭 `AS RowCount` — S06 ⚪ · S16 🟡 (2건, S15도 같은 모양)**
`ROWCOUNT`는 T-SQL 예약어라 `AS RowCount`는 구문 오류다. S06(67·77·85-86·89행), S16(84·92행), S15(132-148행)의
사후 검증 블록이 모두 이 별칭을 쓴다. 이행 DML 자체에는 영향이 없고, **행 수 검증 블록만 조용히 실행되지 않는다**.
`AS [RowCount]`로 바꾸면 세 단계가 함께 닫힌다. S15 항목은 `SourceRows` CTE 미정의라는 별개 문제를 함께 안고 있어 표에 남겼다.

**(다) 리터럴 `RETURN <정수>` 신설 — S05 ⚪ · S13 ⚪ (2건, S14도 같은 모양)**
원본 SP들은 값 없는 `RETURN`만 쓰고 상태를 `@po_intRetVal` OUTPUT으로만 전달한다. 단계들은 `RETURN -9`,
`RETURN @v_currentStepId`처럼 반환 코드에도 음수·오류 코드를 싣는다. `common/01-step-contract.md:33`의 공통 CATCH
패턴을 따른 결과이고 OUTPUT 계약은 그대로 유지되므로 C# 어댑터의 판정은 달라지지 않는다.

**(라) `ISNULL(COUNT(*),0)` ↔ `COUNT(*)` 표기 뒤바뀜 — S12 ⚪ · S13 ⚪ (2건)**
S12는 원본이 `COUNT(*)`인 `TSettleByIN.INCNT`까지 `ISNULL(COUNT(*),0)`로 일괄 서술했고, S13은 반대로 원본이
`ISNULL(COUNT(*),0)`인 `TSettleByOUT.OUTCNT`를 `COUNT(*)`로 적었다. `GROUP BY` 하의 `COUNT(*)`는 NULL이 될 수 없어
두 방향 모두 값이 같다.

접지 않은 결함 중에도 되풀이되는 **패턴**이 둘 있다. 접지 않은 이유는 단계마다 어긋난 자리와 금액 영향이 다르기 때문이다.

- **DML 본문의 산문 대체** — S07 🔴, S08 🟡 4건, S12 🟡. 명세서에 SET 산식·INSERT 컬럼 목록이 전문으로 있는데
  단계가 `/* U4: 고객사 최저수수료 */` 같은 주석이나 "원문 그대로 적용한다" 류의 지시로 대체했다. 지시서만으로는
  상수·계수·부호·반올림 자릿수·UDF 인자가 복원되지 않는다. **S07이 🔴인 것은 18개 갱신 중 10개가 통째로 비어서**이고,
  S08은 갱신 단위로 흩어져 각 🟡, S12는 컬럼 목록 없는 `INSERT … SELECT`가 물리 컬럼 순서에 의존하는 자리다.
- **공통 CATCH의 `@v_currentStepId` 초기값** — S05 🟡(`-9`), S13 🟠(`0`). 초기값이 업무 코드와 겹쳐, DML 바깥에서
  난 장애가 업무 조건 코드(S05) 또는 **성공 코드**(S13)로 보고된다. S13이 🟠인 것은 실패가 `0`으로 보고되면
  오케스트레이터가 단계를 `Succeeded`로 기록해 재실행하지 않기 때문이다.

**신설 단계 4개의 `검증 불가`**(S01·S02·S03·S16)도 한 뿌리다. `batch.ControlTotal`은
`[Batch Control Table Contract]`가 컬럼을 정의하지 않는 유일한 배치 제어 표인데, S01(60-61행)·S03(46-50행)·S16(72행)이
모두 이 표를 주석 자리표시자나 추상 참조로만 다룬다. 기준값이 없으니 컬럼 수·값 수 대조가 성립하지 않는다.

## 6. 이 감사가 보증하지 않는 것

- **축 A·축 A 교차는 이 판에서 다시 읽지 않았다.** 39단위 전부 2026-08-24 POQSettlePrco20 축 A 재감사(10회차)의
  판정을 캐시로 재사용했다. 재사용의 근거는 파일 해시 일치뿐이다 — 폐포 31개의 `object_definition.sql`·`Spec.md`·
  `metadata.json` 해시가 그 판정 당시와 같고, 교차 8단위의 키 해시가 모두 현 폐포 파일의 것이었다.
  Job이 바뀌어도 세 경로가 `output/Jobs/` 바깥이라 판정이 유지된다는 규약(`SKILL.md` 2-1절)에 기댄 것이다.
- **실행 대조를 하지 않았다.** SQL을 돌려 행 집합·금액을 비교한 자리는 하나도 없다. 축 B 결함의 `영향` 칸은
  전부 정적 판단이며, 특히 S07 🟠(`HAVING SUM(TxAmt)=0`), S09 🟠(`CROSS APPLY` 이관), S11 🟠(조인 키 누락),
  S14 🟠(`MAX(ID)` 통합)은 **데이터에 따라 영향이 갈리는** 종류다.
- **계획서(`docs/BatchMigrationPlan.md`) 전문을 대조하지 않았다.** 단위들이 자기 단계 본문에 해당하는 자리를
  표본으로 확인했을 뿐이다(S05·S08·S12·S13은 5/5, S10·S14는 3/3 일치). 계획서 고유의 서술은 검증 범위 밖이다.
- **`agent/src/`·`agent/tests/`·`agent/verification/`·`agent/common/`은 대조 대상이 아니었다.**
  단위들이 `common/*.md`를 규약 근거로 참조했을 뿐, 그 파일들 자체를 `Spec.md`와 대조하지는 않았다.
- **신설 단계 4개는 레거시 기준값이 없다.** 대체 기준(`[Approved Step List]`·`[Batch Control Table Contract]`)이
  덮지 않는 구간의 판단은 보류했고, 그 사실을 `검증 불가`로 표시했다. 이 단계들이 "정합"이라는 뜻이 아니다.
- **이전 판 대조를 하지 못했다.** POQSettleBatch1에는 이전 감사 보고서가 없다(`consistency/`가 이번에 처음 생겼다).
  다른 Job(POQSettleProc16)의 축 B 캐시가 있으나 번들과 단계 구성이 달라 대조 대상이 아니다.
  따라서 "이번에 사라진 결함"·"재발한 결함"을 이 판은 말하지 않는다.
