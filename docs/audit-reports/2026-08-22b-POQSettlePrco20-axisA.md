# POQSettlePrco20 산출물 정합성 감사 — 축 A 재감사

2026-08-22 · 명세서 전면 재생성 직후 · 축 A와 축 A 교차만 수행(축 B는 이번 지시 범위 밖)

## 1. 판정

| 축 | 판정 | 단위 수 | 신규 검증 | 캐시 재사용 | 검증 불가 |
|---|---|---|---|---|---|
| A (객체) | **결함** | 31 | 31 | 0 | 0 |
| A 교차 | **결함** | 8 | 8 | 0 | 0 |
| B | 미수행 | — | — | — | — |

축 A 객체 31단위 중 정합 17 · 결함 14. 교차 8단위 중 정합 6 · 결함 2.
결함 등급 분포: 🔴 3 · 🟠 8 · 🟡 23 · ⚪ 19.

이전 감사(2026-08-22 01:38) 이후 **폐포 31개 명세서가 전부 재생성**되어 캐시 적중이 0이다. DDL 해시는 31개 모두 이전과 같으므로 원본은 변하지 않았고, 바뀐 것은 산출물뿐이다.

## 2. 검증 대상 확정

**소비 명세서 집합 (SP 12)** — `agent/MigrationInstructions.md`의 `Spec.md` 링크에서 읽었다(폴백 ②는 볼 필요 없었다).

**참조 폐포 (31)** — 소비 SP 12개 각각의 `output/Procedures/[SP]/raw/dependency-manifest.json` `Nodes[]` 합집합이다. SP 14(소비 12 + 중첩 2) · 로컬 함수 10 · 외부 DB 함수 7. `Status`는 31개 전부 `Succeeded`이고 `SpecPath`·`DdlPath`가 가리키는 파일이 모두 실재해 **검증 불가 0건**이다.

폐포에만 있는 SP 둘(`UP_Util_Settle_Summary_AcqManual`, `UP_UTIL_SETTLE_SUMMARY_EXTRA`)은 `UP_Util_Settle_Summary`가 `EXEC`로 부르는 하위 SP다. 최상위 실행 순서에 없어 소비 집합에서 빠진 것이며, 축 A 대상에는 포함했다. 이것이 축 B의 결함인지는 축 B가 판정할 자리라 이 보고서에서 닫지 않는다.

**축 A 교차 대상 (8)** — 「참조 함수 (기계 확정 — 수정 금지)」 표를 실은 객체 6개(전부 SP)에, 표 없이 DML 밖에서만 사용자 함수를 부르는 함수 2개(`UF_GET_COLLECTYMD`·`UIF_SettleYMD`)를 더했다. 뒤의 둘은 표 부재가 정상이며 3-2-1절 사각지대 전담이다. 처음 표만으로 6개를 잡았다가 `UF_GET_COLLECTYMD` 축 A 단위의 보류가 누락을 짚어 바로잡았다.

## 3. 단위별 커버리지

| 축 | 단위 | 종류 | 판정 | 상태 | 근거 파일 |
|---|---|---|---|---|---|
| A | `dbo.UP_UTIL_SETTLE_CANCEL_INS.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_CANCEL_INS.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_INS.Procedure` | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_INS.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure` | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure` | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure` | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_SUMMARY_ETC.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_ETC.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure` | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure` | SP | 결함 | 신규 | `output/Objects/dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_Util_PG_Client_CMRate_Ins.Procedure` | SP | 결함 | 신규 | `output/Objects/dbo.UP_Util_PG_Client_CMRate_Ins.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_Util_Settle_Summary.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_Util_Settle_Summary.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UP_Util_Settle_Summary_AcqManual.Procedure` | SP | 정합 | 신규 | `output/Objects/dbo.UP_Util_Settle_Summary_AcqManual.Procedure/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_COMM4CLIENT.Function` | 외부함수 | 결함 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_COMM4CLIENT4INTEREST.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT4INTEREST.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function` | 외부함수 | 결함 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_COMM4PG.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4PG.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_COMM4PG4INTEREST.Function` | 외부함수 | 결함 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_COMM4PG4INTEREST.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_EXTRACOMM4CLIENT.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_GET_EXTRACOMM4CLIENT.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_Get_ExtraCardCommissionAmt.Function` | 외부함수 | 정합 | 신규 | `output/External/SETTLE_CARD_DB/Objects/dbo.UF_Get_ExtraCardCommissionAmt.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_CLIENTSECTIONRATE.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_CLIENTSECTIONRATE.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_COLLECTYMD.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_COLLECTYMD.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_INCVTAXRATE.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_INCVTAXRATE.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_OUTYMD4REFUND.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_OUTYMD4REFUND.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_PGCommOption.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_PGCommOption.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_ROUND4VAT.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_ROUND4VAT.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_SETTLE_EXCHANGERATE.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_GET_SETTLE_EXCHANGERATE.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_GET_WORKDAY2.Function` | 함수 | 결함 | 신규 | `output/Objects/dbo.UF_GET_WORKDAY2.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UF_Get_CLComm4MobileCo.Function` | 함수 | 정합 | 신규 | `output/Objects/dbo.UF_Get_CLComm4MobileCo.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A | `dbo.UIF_SettleYMD.Function` | 함수 | 결함 | 신규 | `output/Objects/dbo.UIF_SettleYMD.Function/raw/object_definition.sql` + `Spec.md` + `metadata.json` |
| A 교차 | `dbo.UF_GET_COLLECTYMD.Function` | 교차 | 결함 | 신규 | 호출 객체의 DDL·Spec.md·metadata.json |
| A 교차 | `dbo.UIF_SettleYMD.Function` | 교차 | 결함 | 신규 | 호출 객체의 DDL·Spec.md·metadata.json |
| A 교차 | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·Spec.md·metadata.json |
| A 교차 | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·Spec.md·metadata.json |
| A 교차 | `dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·Spec.md·metadata.json |
| A 교차 | `dbo.UP_UTIL_SETTLE_INS.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·Spec.md·metadata.json |
| A 교차 | `dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·Spec.md·metadata.json |
| A 교차 | `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure` | 교차 | 정합 | 신규 | 호출 객체의 DDL·Spec.md·metadata.json |

검증 불가 0건. 모든 단위가 기준 파일 셋을 전부 읽었다.

## 4. 축 A 결함

### 4-0. 🔴·🟠 — 금액 또는 대상 행 집합이 갈리는 것

| 등급 | 축 | 객체 | 원본 앵커 | 산출물 앵커 | 원본 | 산출물 | 영향 |
|---|---|---|---|---|---|---|---|
| 🔴 | A 교차 | `dbo.UF_GET_COLLECTYMD.Function` | UF_GET_WORKDAY2 object_definition.sql:26-28 | Spec.md:108 | IF @pi_intInterval = 0 → @v_intIdx = -1. 간격 0이면 오프셋 0(기준일 자신)부터 휴일 판정을 시작해 기준일 이후 첫 영업일을 반환 | 간격 0 특례가 없음. '간격만큼 영업일을 세어 이동'으로 구현하면 루프가 돌지 않아 기준일을 그대로 반환 | 호출 지점 78행이 CollectDay-1을 넘기므로 회수일 1일이면 간격이 정확히 0(CollectDay는 tinyint NOT NULL이라 스키마가 막지 못함). 1일이 휴일이면 원본은 당월 첫 영업일을 내는데 서술대로면 휴일이 그대로 남고, 호출자의 휴일 반영이 과거 방향(MAX)으로 가 회수일이 전월 말로 한 달 밀림. UF_GET_COLLECTYMD 호출 SP 1개(UP_UTIL_SETTLE_EXPECT_PROC)에 번짐 |
| 🔴 | A | `dbo.UF_GET_COMM4CLIENT.Function` | object_definition.sql:68 | Spec.md:150 | 라인 52 IF가 건너뛰어지면 @@ROWCOUNT가 0으로 리셋되어 라인 68의 @@ROWCOUNT<1이 참 → 3차 조회(최고율 TOP 1) 실행 | mermaid가 FoundFirst -->\|예\| BaseRate로 그려 1차 성공 시 3차를 건너뛰는 것으로 뒤집음. 산문 96·111-113도 단순 폴백 체인으로 서술 | 1차에 복수 행이 걸리는 가맹점에서 적용 수수료율이 최고율 TOP 1이 아니라 1차의 마지막 대입값이 되어 금액이 달라짐. 호출 SP 1개. 실행 의미 표 자체는 옳고 산문·mermaid가 표를 뒤집은 모양 |
| 🔴 | A | `dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure` | object_definition.sql:69 | Spec.md:181 | WHILE 루프 본문 첫 문장 SET @v_intID = 0 — 커서 행마다 재설정. 뒤따르는 SELECT @v_intID = ID는 비집계 대입이라 무결과 시 직전 값이 남음 | 로직 흐름 4단계에 루프 내 0 재설정 없음. 지역 변수 표의 '초기값 0'은 DECLARE 시점 값. mermaid에도 재설정 노드 없음 | 이대로 구현하면 무매칭 행에서 선행 ID가 남아 IF @v_intID > 0 분기로 들어가고 UPDATE가 0행 갱신 → 신규 INSERT 누락 → 금액 검증 불일치로 ROLLBACK + @po_intRetVal=-3. 배치 전량 롤백. 대상 행 집합과 금액이 함께 갈림 |
| 🟠 | A | `dbo.UF_GET_COMM4PG4INTEREST.Function` | metadata.json:891 | Spec.md:89 | UseState: tinyint, IsNullable true, 기본값 ((0)) | UseState와 IsPGFlag를 묶어 '널을 허용하지 않습니다'로 단정 (IsPGFlag만 맞음) | 호출 SP 1개. 필터 원문 USESTATE=0은 정확해 그대로 이행하면 금액 보존. NOT NULL 단정을 근거로 필터를 바꾸거나 이행 스키마에 NOT NULL을 세우면 USESTATE IS NULL 행이 대상에 들어와 금액이 바뀜. NULL 행 없는 배포면 🟡 |
| 🟠 | A | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | object_definition.sql:78 | Spec.md:437 (94·283·309-310) | AND (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD)) — 당일 이전 취소건 제외 | 로직 흐름 3은 '해외카드 조건을 만족하는 행'으로만 적음. A.YMD = A.AYMD 동등 비교와 OR/AND 구조가 문서 어디에도 없음 | 당일 이전 취소건이 해외카드 수수료 재계산 대상에 포함돼 대상 행 집합이 넓어짐. 같은 술어가 UPDATE 13에서는 Spec.md:446에 원문 그대로 실려 서술 불균형 |
| 🟠 | A | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | object_definition.sql:341 (주석 327-328) | Spec.md:443 (291·327-328) | AND ((A.USESTATE = 2) OR (A.CYMD > A.AYMD AND A.USESTATE = 1)) — 익일 이후 전체취소만 대상 | A.CYMD > A.AYMD 비교가 없음. 원본 327-328 주석(당일+전체취소 차감 / 익일이후+전체취소 보존)도 주석 기록 표에 없음 | 당일 전체취소건까지 PGCOMM·PGVT·CLCOMM·CLVT가 0으로 덮여 대상 행 집합이 달라짐. 복원 단서가 전혀 없음 |
| 🟠 | A | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | object_definition.sql:320 | Spec.md:479 | UPDATE 12의 최상위 필터 AND A.AYMD >= '20230101' | 문자열 20230101이 문서 전체에 0회. 근거 주석(라인 310)도 주석 보존 표에서 누락 | 2023-01-01 이전 승인건까지 PLCardDB의 DiscountFlag/DiscountAmt를 받고, 그 값이 UPDATE 13의 IIF로 원가 수수료에 들어가 금액 차이로 이어짐 |
| 🟠 | A | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | object_definition.sql:220,239,280,302,375 | Spec.md:477 | (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD)) — UPDATE 7·8·10·11·13 다섯 문장 | 술어 형태로 서술 없음. 집합 술어 표는 원소만 싣고 OR 결합을 못 담으며 컬럼 대 컬럼 등식은 어떤 표에도 없음 | 전일 이전 취소건까지 PG·고객사 최저수수료와 카카오 PGVT, INIVAcct 농협 예외가 재적용됨. 다섯 문장의 대상 행 집합이 동시에 확대 |
| 🟠 | A | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | object_definition.sql:145-147 | Spec.md:120 | UPDATE 4의 결합 조건은 YMD·CLIENTID·PGNAME 셋 (MALLID 없음) | CRUD 산문이 'MALLID 조인에 사용'으로 적어 같은 문서의 DML 범위 표를 뒤집음 | MALLID 조인을 넣어 이행하면 요율 행의 MallID가 다른 정산 행이 최저수수료 대상에서 탈락 |
| 🟠 | A | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | object_definition.sql:149 | Spec.md:476 | A.PGNAME IN ('KFTC','YELOPAY','INIBANK','settlevacct','inivacct') 다섯 | 로직 흐름 2항이 'KFTC의 고객사 최소수수료'로만 서술 — 집합 술어 표(원소 5개)를 뒤집음 | 요약만 읽고 이행하면 네 PG가 고객사 최저수수료 적용에서 빠짐 |
| 🟠 | A | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | object_definition.sql:442 | Spec.md:480 | UPDATE 16의 필터 AND TxAmt != CardAmt+CouponAmt+MoneyAmt+PointAmt | 산문·매핑 절 어디에도 조건 없음. 부등식 자체가 어떤 표에도 없음 | 합이 이미 일치하는 payco 취소 행까지 CardAmt=TxAmt로 덮고 나머지를 0으로 지움. UPDATE 17의 원가 수수료 입력이라 금액 구성이 바뀔 수 있음 |

### 4-0-1. 🟡 — 추적성·표기

| 등급 | 축 | 객체 | 원본 앵커 | 산출물 앵커 | 원본 | 산출물 | 영향 |
|---|---|---|---|---|---|---|---|
| 🟡 | A 교차 | `dbo.UF_GET_COLLECTYMD.Function` | UF_GET_WORKDAY2 object_definition.sql:22-24 | Spec.md:108 | IF @pi_intInterval < 0 → @v_intFlag = -1. 음수 간격이면 탐색·연장 방향이 과거로 반전 | '휴일을 만나면 간격을 연장'만 있고 부호에 따라 방향이 갈린다는 술어 없음 | CollectDay가 tinyint NOT NULL이라 이 호출자에서는 CollectDay=0(비정상 데이터)에서만 도달 — 스키마 제약 근거로 🔴에서 하향 |
| 🟡 | A | `dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function` | object_definition.sql:58-67 | Spec.md:87,110 | 2차 조회에 TOP 절 없음 — 다중 행이면 마지막 처리 행의 값이 변수 5개에 남음(비결정) | '정렬이 없습니다'까지만 적어 단일 행이 얻어지는 것처럼 읽힘. 1차 조회의 비결정은 명시한 것과 불균형 | 이행 시 First()/Single()로 옮기면 다른 CardCode의 수수료율을 잡아 금액이 갈릴 수 있음. 원본 자체가 비결정이라 🟡. 호출 SP 1개 |
| 🟡 | A | `dbo.UF_GET_WORKDAY2.Function` | object_definition.sql:30 | Spec.md:80 | @pi_intInterval이 NULL이면 루프가 안 돌고 DATEADD(dd,0,@pi_strYMD)로 기준일자 자체를 반환 | '@pi_strYMD 또는 @pi_intInterval이 NULL이면 반환값도 NULL이 될 수 있습니다'로 두 파라미터를 묶음 | 호출 SP 3개. 이 서술대로 NULL 가드를 넣으면 간격 NULL일 때 정산일이 NULL이 되어 레거시(기준일자)와 갈림. '될 수 있습니다' 유보 서술이라 🟡 |
| 🟡 | A | `dbo.UIF_SettleYMD.Function` | object_definition.sql:107 | Spec.md:26 | TSettlePeriodMst(107)·THoliday(133·148)에만 WITH(NOLOCK), MASTER..SPT_VALUES(129·144)에는 없음 | 셋을 묶어 'NOLOCK 읽기 상태에 따라'로 뭉갬. CRUD 표도 힌트 미표기 — 어느 스캔이 NOLOCK인지 복원 불가 | 호출 SP 3개. 금액 불변이나 이행 시 힌트 배치가 원본과 달라져도 문서로 안 잡힘. 기계 확정 잠금 힌트 표의 범위 밖이라 DDL 원문이 기준값 |
| 🟡 | A 교차 | `dbo.UIF_SettleYMD.Function` | UF_GET_WORKDAY2 object_definition.sql:19,26-30,33-38 | Spec.md:95 | 휴일 검사 대상은 @v_intIdx가 1부터 올라가는 날들이라 기준일 자신은 검사하지 않음. 간격 0일 때만 @v_intIdx=-1로 시작해 기준일을 검사 | '휴일이면 간격을 조정해 계산일을 반환합니다' — 어느 날부터 세는지, 간격 0 특례가 있는지 없음 | 이 문단만으로 구현하면 기준일이 휴일일 때 하루 밀리고(간격>=1), 간격 0 경로는 휴일 회피가 사라짐. 사각지대라 받쳐 주는 기계 확정 표가 없어 산문이 유일 근거. 정본 링크가 실재해 🔴로 올리지 않음 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | metadata.json ReferencedColumnsPerTable TSettleMst (원본 430·450-451) | Spec.md:92-96 (특히 94) | 파서 확정 34개 컬럼에 SeperateAmt·SettleCurrency·ForeignSettleAmt 포함, UPDATE 15의 FROM TSettleMst B가 조회 측 참조 | 세 컬럼이 없고 UPDATE 15의 TSettleMst B 참조가 통째로 빠짐 | 파서가 진실의 원천이라는 계약 위반. 값은 갱신 14·15 매핑 표에 남아 이행 손실은 없음. 추적성 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | object_definition.sql:145 | Spec.md:99 | SELECT PLTID FROM PaymentDB.dbo.TCCanceledMst WITH(NOLOCK) — 최상위 WHERE 하위 질의 | NOLOCK이 잠금 힌트 표 28행 밖으로는 한 번도 언급되지 않음 | LockHintVisitor 범위 밖이라 표 부재는 정상이나 산문도 침묵. 이행 시 더티 리드 허용이 사라짐 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | object_definition.sql:244 | Spec.md:429 | 파생 테이블 D의 GROUP BY PLTID | D의 그룹화가 산문·표 어디에도 없음 | K의 HAVING SUM=0·MAX 결과는 배수 관계로 불변이라 금액·행 집합 동일. 표기·추적성 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure` | object_definition.sql의 실행 블록 헤더 주석 12개 및 405-407·327-328·141·222 | Spec.md:31-72 | 실행 블록 헤더 주석 12개 + 계산 규칙 주석 | 주석 기록 표가 처리 블록 셋만 싣고 나머지 실행 블록 헤더를 전부 누락. 비실행 블록 주석은 4행 다 실음 | 전수를 표방한 표가 선택적이라 원본 의도 추적이 끊김. 327-328 누락은 위 🟠 결함의 복원 단서까지 지움 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | metadata.json ReferencedColumnsPerTable TPGProperty | Spec.md:119 | 파서 확정 7개(PLTID, ID 포함) | 앞의 둘을 뺀 다섯만 기재 (나머지 11개 테이블은 전수 일치) | 표기·추적성. 금액 영향 없음 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | object_definition.sql의 주석 77건 | Spec.md:33-73 | 주석 77건 | 보존 표가 40건만 싣고 37건 누락(라인 220 이후 사실상 수집이 끊김). 실린 40건은 전수 정확 | 표기·추적성. 라인 310 누락은 위 AYMD 필터 결함의 마지막 단서까지 지움 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure` | object_definition.sql:529 | Spec.md:482 | UPDATE 18 하위 질의의 SELECT PLTID FROM TSettleMst WITH (NOLOCK) | 잠금 힌트가 문서 어디에도 없음 (표는 관할 밖이라 0행이 정상) | 표기·추적성. 이행 시 이 하위 질의만 격리 수준이 달라질 수 있음 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_INS.Procedure` | object_definition.sql:264,265,299 | Spec.md:33-74 | 주석 40행 | 주석 기록 표가 3행 누락(264 [PG원가수수료일치], 265 수수료합계, 299 비과세용 몰아이디) | 환불 분기 주석 계보만 사라진 불균형. 비과세 규칙 자체는 집합 술어 표와 로직 흐름에 남아 금액 영향 없음. 추적성 손실 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_INS.Procedure` | object_definition.sql:243-249 | Spec.md:69 | /* */로 통째 주석 처리된 대안 CLCOMM/CLETC 산출식 | 비활성 블록의 존재가 미기록. 그 블록 안쪽 주석(248행)만 살아 있는 주석처럼 표에 실림 | 248행 주석을 근거로 죽은 로직을 되살릴 오독 경로. 현행 CLComm/CLEtc는 253-257 CASE가 확정하고 표에 정확히 실려 금액은 어긋나지 않음 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure` | object_definition.sql:65,153 | Spec.md:121 | INSERT 대상 목록의 X.PRODUCTNAME은 한정자 표기이고 TSettleMst에 ProductName varchar(120)이 실재 | 'PRODUCTNAME 또는 X.PRODUCTNAME 컬럼이 없습니다. 스키마 불일치입니다'로 단정 | 실재 컬럼을 없다고 단정. 원인은 파서가 X. 한정자째로 잡아 프롬프트 스키마 블록이 ProductName을 쳐낸 것. 이행자가 상수 매핑을 통째로 뺄 수 있음. 행 수·금액은 불변 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure` | object_definition.sql:190,193,209,232,252,274,293,295,296,297 | Spec.md:32-73 | 수수료정책변경 이력·원단위 절사 규칙 등 주석 10줄 | 주석 기록 표에 없음. 같은 성격의 구획 주석과 주석 처리 블록은 실어 표가 불균형 | UPDATE 2·3·5의 존재 이유와 절사 규칙, 2021-07-21 정책 변경 이력이 추적 근거를 잃음 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure` | object_definition.sql:22,31 | Spec.md:262-276 | DML 밖 스캔 두 곳의 NOLOCK. 라인 31은 -9 차단 게이트의 판단 근거 스캔 | 표는 관할 밖이라 정상이나 직후 산문도 표에 실린 자리만 열거 — 라인 22·31은 문서 전체에서 한 번도 언급 없음 | 축 A 계약이 이 SP의 라인 31을 제어 흐름 술어 하위 질의의 실물 사례로 지목한 자리. 이행 시 -9 게이트가 커밋되지 않은 데이터를 읽는다는 사실이 전달되지 않음 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure` | object_definition.sql:54 | Spec.md:87,103 | INSERT 대상 컬럼의 X.PRODUCTNAME은 한정 컬럼 참조이고 TSettleMst에 ProductName이 실재(스키마 59개 중 15번째) — 정상 컴파일 코드 | '제공된 TSettleMst 스키마에 존재하지 않는 컬럼명이며 스키마 불일치'로 단정 | 함께 제공된 메타데이터가 반박하는 단정. 실제 대상 컬럼명을 끝내 밝히지 않아 이행자가 원본 버그로 보고 상수 삽입을 떨어뜨릴 수 있음. 매핑 표의 컬럼 칸 보존 자체는 옳고 결함은 덧붙인 산문 진단 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure` | object_definition.sql:62 | Spec.md:173 | 커서 원천 질의의 ORDER BY A.OutYMD, A.ClientID | 커서 정렬 서술이 문서 전체에 없음 (GROUP BY는 실림). DML 범위 표의 ORDER BY 칸은 UPDATE/INSERT용이라 커서 SELECT를 담지 않음 — DDL 원문이 기준값 | 처리 순서 소실. MAX(ID)+1 채번 결과와 -3 중단 지점이 달라짐. 행별 금액은 불변 |
| 🟡 | A | `dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure` | object_definition.sql:210 | Spec.md:209 | TSettleByOUT | TSetTleByOUT 오타 (나머지 30행은 정상) | 매핑 표를 식별자 원천으로 삼는 이행·grep·자동 대조가 이 행에서 어긋남. 실행은 대소문자 비구분이라 무해 |
| 🟡 | A | `dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure` | object_definition.sql:22 | Spec.md:102 | 조립기 원문의 DML 범위 표 구분행은 8칸 (AiService.cs:812) | 구분행이 7칸, 헤더·데이터행은 8칸 — 수정 금지 블록을 옮기며 구분셀 하나 누락 | GFM이 표로 인식하지 않아 기계 확정 표 전체가 평문으로 무너짐. 값은 원본과 일치해 금액 영향 없음 |
| 🟡 | A | `dbo.UP_UTIL_STAT_PGCOLLECT_INS.Procedure` | object_definition.sql:31 | Spec.md:72 | INSERT 매핑 표 헤더·데이터행 4칸 | 구분행 3칸 | 표 미렌더링 또는 설명 열 잘림으로 13개 삽입 컬럼의 원천 수식 추적 불가 |
| 🟡 | A | `dbo.UP_Util_PG_Client_CMRate_Ins.Procedure` | object_definition.sql:21 | Spec.md:58 (21·272도 동일) | IF EXISTS(SELECT PLTID FROM TSettleMst WITH(NOLOCK) WHERE ...) — 사전 검증 스캔에 NOLOCK | SELECT 대상 표·개요·로직 흐름 어디에도 이 스캔의 잠금 힌트 없음 | 잠금 힌트 표의 관할 밖(제어 흐름 술어 안 하위 질의)이라 DDL 원문이 유일한 기준값인데 옮기지 않음. 이행이 기본 격리 수준으로 구현하면 잠금 대기가 원본과 달라짐. 추적성 손실 |

### 4-0-2. ⚪ — 정보

| 축 | 객체 | 원본 앵커 | 산출물 앵커 | 내용 | 영향 |
|---|---|---|---|---|---|
| A | `dbo.UF_GET_CLIENTSECTIONRATE.Function` | object_definition.sql:23 | Spec.md:51-55 | 실행 의미 표에 @@ROWCOUNT 행 없음, DB 배치 1행만 | 명세서 결함 아님 — metadata.json의 RawPromptContext에도 DB 배치뿐이라 조립기가 재료를 싣지 않은 것. 도구 쪽 신호 |
| A | `dbo.UF_GET_CLIENTSECTIONRATE.Function` | object_definition.sql:27-29 | Spec.md:32 | '음수이면 -1을 곱해 음수로 반환' 서술 | 표기 정밀도만. 같은 문서 30·31행이 두 경우를 따로 확정해 오독 경로는 닫힘 |
| A | `dbo.UF_GET_COLLECTYMD.Function` | object_definition.sql:126 | Spec.md:101 | CRUD 표는 20개, YMD 제외 — 파서가 파생 테이블 계산 컬럼을 물리 테이블에 잘못 귀속시킨 것이고 명세서가 그 사실을 명시 | 금액 무관. 이행 시 파서 값을 그대로 옮기면 없는 컬럼을 잡음 |
| A | `dbo.UF_GET_COLLECTYMD.Function` | object_definition.sql:131 | Spec.md:34 | 개요가 'DB 간 참조'로 분류 — 실행 의미 표 DB 배치 행의 확정 문장을 산문이 따른 것 | 실행 결과 동일. 이행 시 별도 DB로 오인 가능 |
| A | `dbo.UF_GET_COMM4PG.Function` | object_definition.sql:42-43,60,64 | Spec.md:132 | NULL 반환 경로를 '조회 결과 없음' 하나로만 예시 | 호출 SP 1개. 값 판정은 원본과 일치, 서술 완결성 정보 |
| A | `dbo.UF_GET_EXTRACOMM4CLIENT.Function` | object_definition.sql:52 | Spec.md:91-94 | 실행 의미 표에 @@ROWCOUNT 종류 행 없음 | 호출 SP 1개. 금액·행 집합 영향 없음. 산문이 술어와 귀속을 정확히 서술해 실질 손실 없음 |
| A | `dbo.UF_GET_ROUND4VAT.Function` | object_definition.sql:13 | Spec.md:51 | 'INT 반환 계약에 맞게 반환됩니다'로만 서술, 범위 제약 침묵 | 호출 SP 5개. 값 동등이라 금액 영향 없음. INT 범위 초과 시 오버플로 정보만 부재 |
| A | `dbo.UF_GET_WORKDAY2.Function` | object_definition.sql:36 | Spec.md:123 | '간격을 -1 감소시켜'로 이중부정 읽힘 | 표기 명확성만. 값 영향 없음 |
| A | `dbo.UF_Get_CLComm4MobileCo.Function` | object_definition.sql:31-35 | Spec.md:112 | CASE 분기 표 ELSE 행이 주석을 포함한 채 한 줄로 접힘 (CaseBranchExtractor.TextOf의 기계 확정값 — 계약상 결함 아님) | 판정 영향 없음. 표 셀을 SQL로 재사용하면 -- 뒤가 주석 처리됨. 호출 SP 1개 |
| A | `dbo.UF_Get_ExtraCardCommissionAmt.Function` | metadata.json Dependencies CommissionRate0..3 Description | Spec.md:137-140 | '신용카드 우대수수료율' (명세서 쪽이 DDL과 맞음 — 원천 DB 주석이 복사 실수) | 명세서 수정 불필요. 이행 시 DB 컬럼 주석을 믿으면 신용/체크가 뒤바뀜. 호출 SP 1개 |
| A | `dbo.UIF_SettleYMD.Function` | object_definition.sql:38 | Spec.md:87 | 지역 변수 표가 @v_intSettleDay 행에만 '읽지 않습니다'를 달아 비대칭 | 호출 SP 3개. 반환값 영향 없음 |
| A | `dbo.UP_UTIL_SETTLE_CANCEL_INS.Procedure` | object_definition.sql:19 | Spec.md:171 | NOCOUNT 자리를 언급하지 않음 (거짓 주장은 없음) | 이행 시 rowcount 메시지 억제 여부를 원본과 다르게 정할 수 있음. 금액·행 집합 영향 없음 |
| A | `dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure` | object_definition.sql:185-187,206-209,247-250 | Spec.md:254 | 실행 의미 표 DB 배치 행이 4건 전부를 '그 밖'으로 열거 — RawPromptContext 원문 그대로라 명세서 결함 아님. 조립기(ExecutionSemanticsFacts DB 배치 문장 템플릿) 문제 | 이 SP 판정 영향 없음. 같은 템플릿을 쓰는 다른 명세서에서 '전부 크로스 DB'로 오독 소지 |
| A | `dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure` | object_definition.sql:41-45 등 11개 IF | Spec.md:332-335 등 | mermaid 판정 노드의 두 출력 간선에 참/거짓 라벨 없음 | 로직 흐름 1~11항이 분기마다 명시해 추적성 손실 없음. 다이어그램 단독 판독 시에만 모호 |
| A | `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD.Procedure` | object_definition.sql:164-167,173 | Spec.md:230→23 | DB 배치 행이 넷 다 '그 밖'으로 열거하고 개요 산문이 그대로 따름 — 명세서는 표를 충실히 따른 것 | 사실 생성기(ThreePartObjectReferences를 소속 DB 여과 없이 문장화) 문제. 같은 문장이 붙는 모든 SP에 번짐 |
| A | `dbo.UP_UTIL_SETTLE_SUMMARY_ETC.Procedure` | object_definition.sql:97-109 | Spec.md:88 | '13개 키와 ISNULL 둘 비교'로 적어 13+2=15로 오독 가능 (DELETE 행은 정확히 갈라 적음) | 표기 모호성만. DML 범위 표가 13개를 전수 확정해 오독 경로는 막힘 |
| A | `dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure` | object_definition.sql:196 vs 223-228 | Spec.md:234,328,247 | 두 조건을 각각 정확히 옮겼으나 비대칭 자체를 짚지 않음 | 재실행 시 OUTYMD < @v_strReqYMD 행이 삭제되지 않은 채 재등록돼 중복 적재 가능. 이행 설계 근거를 명세서만으로는 못 얻음 |
| A | `dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure` | object_definition.sql:42,78,94,131,147,185,202,234 | Spec.md:76,316-330,341-362 | 두 경로를 동등하게 살아 있는 것으로 서술 | DDL 원문 그대로라 불일치는 아님. 반환코드 규약을 그대로 재현하면 실제 런타임과 달라짐. 실행 쿼리로 닫을 수 있음 |
| A | `dbo.UP_Util_Settle_Summary_AcqManual.Procedure` | object_definition.sql:8 | Spec.md:24 | 3건 전부를 크로스 DB 참조로 서술 — 실행 의미 표 DB 배치 행의 확정 문장을 따른 것(DatabasePlacementExtractor가 홈 DB와 비교하지 않는 의도된 동작) | 금액 불변. 표기 수준 |
### 4-1. 전 객체 공통 결함

같은 원인으로 여러 단위에 반복된 것을 여기 한 번만 적는다. 4-0 표에는 객체별로 이미 실려 있다.

**(1) 표 밖 `NOLOCK`의 침묵 — 🟡 5객체.** `LockHintVisitor`는 `INSERT`/`UPDATE`/`DELETE`의 `FROM`·대상 노드만 방문한다. 그래서 제어 흐름 술어 안의 하위 질의(`IF EXISTS(... WITH(NOLOCK))`), 최상위 `WHERE` 하위 질의, 커서 선언 `SELECT`, 변수 대입 `SELECT`의 잠금 힌트는 「잠금 힌트」 표에 실리지 않는다 — 표 부재는 정상이다. 그 자리는 DDL 원문이 유일한 기준값인데 산문도 함께 침묵한 것이 결함이다. 해당: `UP_Util_PG_Client_CMRate_Ins`(21행) · `UP_UTIL_SETTLE_COMM_UPD`(145행) · `UP_UTIL_SETTLE_EXCEPTION_PROC`(529행) · `UP_UTIL_SETTLE_INS_EXTRA`(22·31행) · `UIF_SettleYMD`(129·144행 SPT_VALUES를 NOLOCK 셋과 뭉갬).

**(2) 「원본 헤더 및 주석 기록」 표의 선택적 수집 — 🟡 4객체.** 전수를 표방하는 표가 일부만 싣는다. `UP_UTIL_SETTLE_EXCEPTION_PROC`는 77건 중 40건만(라인 220 이후 사실상 끊김), `UP_UTIL_SETTLE_COMM_UPD`는 실행 블록 헤더 12개를 통째로, `UP_UTIL_SETTLE_INS`는 3건, `UP_UTIL_SETTLE_INS_EXTRA`는 10건을 뺐다. 실린 행의 라인 번호와 원문은 네 객체 모두 정확하다. **누락이 다른 결함의 복원 단서까지 지운 사례가 둘 있다** — `EXCEPTION_PROC`의 라인 310 주석은 4-0의 `AYMD >= '20230101'` 필터 소실을, `COMM_UPD`의 라인 327-328 주석은 `A.CYMD > A.AYMD` 술어 소실을 되짚을 마지막 근거였다.

### 4-1-1. 명세서가 아니라 도구 쪽인 것

아래 셋은 명세서가 기계 확정 표를 충실히 옮긴 결과다. 산출물을 고칠 자리가 아니라 조립기·추출기를 고칠 자리라 따로 적는다.

**(A) 「실행 의미」 `DB 배치` 문장 템플릿 — 4객체.** `DatabasePlacementExtractor`가 `ThreePartObjectReferences`를 소속 DB와 비교하지 않고 전부 "소속 DB … 그 밖입니다"로 문장화한다. `UP_UTIL_SETTLE_EXPECT_PROC`는 4건 중 2건이, `UP_UTIL_SETTLE_INS_EXTRA4PLCARD`도 4건 중 2건이, `UP_Util_Settle_Summary_AcqManual`은 3건 중 2건이 실제로는 홈 DB다. 명세서 산문은 이 확정 문장을 따랐을 뿐이라 계약 위반이 아니다. 같은 문장이 붙는 모든 객체에 번진다.

**(B) `X.PRODUCTNAME` 스키마 불일치 오단정 — 2 SP.** 파서가 참조 컬럼을 한정자째(`X.PRODUCTNAME`)로 수집해 프롬프트의 스키마 블록에서 실재 컬럼 `ProductName`(`varchar(120)`, "상품명")이 쳐내지고, 모델이 "제공된 스키마에 없는 컬럼 — 스키마 불일치"로 단정한다. `UP_UTIL_SETTLE_INS_EXTRA`·`UP_UTIL_SETTLE_INS_EXTRA4PLCARD` 둘 다 같은 모양이다. 실제 문제는 컬럼 부재가 아니라 컬럼 목록에 남은 `X.` 한정자다.

**(C) 「실행 의미」의 `@@ROWCOUNT` 행 부재는 정상 — 닫힘.** 네 단위가 "DDL에 `IF @@ROWCOUNT`가 있는데 표에 행이 없다"고 보류를 걸었으나, 두 단위(`UF_GET_COMM4CLIENT`·`UF_GET_COMM4CLIENT4PARTIALCANCEL`)가 `RowCountBoundaryExtractor.BlockVisitor`를 직접 읽어 닫았다 — 이 추출기는 **직전 형제 문장이 `IfStatement`인 자리만** 행으로 낸다. 앞이 `SELECT`인 `IF @@ROWCOUNT`는 재료 부재가 정상이다.

### 4-2. 축 A 교차 — 명세서가 참조 함수를 다루는 자리

**기계 확정 「참조 함수」 표 6개, 총 75행이 전수 정합이다.** 행 수는 호출 지점이 아니라 **호출 수** 기준이며(중첩 호출은 바깥·안쪽 각각 한 행), 여섯 객체 모두 원본 DDL의 호출 수와 정확히 맞았다.

| 객체 | 표 위치 | 행 수 | 판정 |
|---|---|---|---|
| `UP_UTIL_SETTLE_EXCEPTION_PROC` | `Spec.md:348` | 29 | 정합 (29/29) |
| `UP_UTIL_SETTLE_COMM_UPD` | `Spec.md:340` | 23 | 정합 (23/23) |
| `UP_UTIL_SETTLE_EXPECT_PROC` | `Spec.md:200` | 9 | 정합 (9/9) |
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | `Spec.md:197` | 6 | 정합 (6/6) |
| `UP_UTIL_SETTLE_INS_EXTRA` | `Spec.md:252` | 5 | 정합 (5/5) |
| `UP_UTIL_SETTLE_INS` | `Spec.md:176` | 3 | 정합 (3/3) |

인자 원문은 정렬 공백까지 보존됐고, 명세서 링크는 전부 `docs/` 기준으로 실재한다. `(명세서 없음)` 행은 0건이다. 표에 실린 함수의 반환값·분기·필터·기본값을 서술한 **금지 산문도 0건**이다. 세 단위가 경계 사례로 같은 문장을 지목했는데(「3인자 `ROUND`의 세 번째 인자가 0이면 반올림」), 이는 T-SQL 내장 `ROUND`의 의미 서술이지 그 자리에 오는 UDF의 반환값 단정이 아니라 위반이 아니다.

여섯 SP 모두 `Dependencies`를 기준값으로 썼다. `ReferencedFunctions`는 인라인 TVF(`UIF_SettleYMD`)를 담지 않아 세 객체에서 과소 집계됐고, 표는 그것과 무관하게 TVF를 옳게 실었다.

**표의 사각지대에서 본 것 — 여기서 결함 2건이 나왔다.** 기계 확정 표의 존재가 이 구간까지 검증한다는 뜻이 아니다.

SP 여섯은 사각지대가 비어 있었다. 호출이 전부 DML 문장 안이라 표 밖으로 샌 호출이 0건임을 각 단위가 DDL 전수로 확인했고, 그래서 피호출 함수 원본을 한 건도 열지 않았다.

함수 둘은 반대다. `UF_GET_COLLECTYMD`(호출 2곳)와 `UIF_SettleYMD`(호출 2곳)는 `UF_GET_WORKDAY2`를 `SELECT`의 `CASE` 안에서만 부른다. `ReferencedFunctionVisitor`의 진입점이 DML뿐이라 표가 없고, 표가 없으니 동작 서술 금지도 걸리지 않는다 — 두 명세서의 산문이 `UF_GET_WORKDAY2`의 동작을 적은 것 자체는 허용된다. 그래서 판정은 "그 서술이 원본과 맞는가"로만 했고, **둘 다 같은 자리에서 어긋났다**: `UF_GET_WORKDAY2`의 휴일 판정이 어느 날부터 시작하는지(기준일 자신은 검사하지 않는다)와 간격 0 특례(`@v_intIdx = -1`)가 두 서술 모두에 없다. `UF_GET_COLLECTYMD` 쪽이 🔴인 것은 호출 지점이 `CollectDay-1`을 넘겨 **회수일 1일이면 간격이 정확히 0**이 되고, `CollectDay`가 `tinyint NOT NULL`이라 스키마가 그 경로를 막지 못하기 때문이다.

## 5. 축 B 결함

**수행하지 않았다.** 이번 지시가 축 A 재감사로 한정됐다. `output/Jobs/POQSettlePrco20/agent/`에 번들(`MigrationInstructions.md`·`steps/`·`raw/prompt-context.md`)이 있으므로 축 B는 언제든 돌릴 수 있다 — 번들 미생성으로 인한 검증 불가가 아니다.

## 6. 이 감사가 보증하지 않는 것

**축 B를 대조하지 않았다.** 명세서와 `output/Jobs/POQSettlePrco20/` 계획서·단계 지시서 사이의 정합은 이 보고서가 말하지 않는다. 4-0의 🔴·🟠가 단계 지시서에 어떻게 옮겨졌는지도 확인하지 않았다.

**폐포에만 있는 SP 둘의 처리를 판정하지 않았다.** `UP_Util_Settle_Summary_AcqManual`·`UP_UTIL_SETTLE_SUMMARY_EXTRA`가 소비 명세서 집합에 없는 것이 정상(최상위 실행 순서 밖)인지 축 B의 결함(단계에 흡수됐는데 명세서가 입력되지 않음)인지는 축 B가 판정할 자리다.

**실행으로 닫은 항목이 없다.** 로컬 Docker는 빈 스키마이고 운영 데이터에 접속할 수 없어 SQL을 한 건도 실행하지 않았다. 실행으로 닫아야 할 것이 셋 남아 있다.
- `UP_UTIL_SETTLE_SUMMARY_EXTRA`의 `IF @@ERROR` 8분기가 `BEGIN TRY` 안이라 도달 불가인지: `BEGIN TRY DELETE dbo.NoSuchTable WHERE 1=1; IF @@ERROR<>0 SELECT 4001; END TRY BEGIN CATCH SELECT 4000 END CATCH` — 4000이면 ⚪ 유지.
- `UIF_SettleYMD`의 간격 0 특례 도달 가능성: `SELECT SettleType, SettleDayFlag, COUNT(*) FROM SETTLE_POQ_DB.dbo.TSettlePeriodMst WHERE SettleState = 1 AND SettleTarget = 1 AND SettleDay = 0 GROUP BY SettleType, SettleDayFlag;` — 0행이면 해당 🟡이 ⚪로 내려간다.
- `UF_GET_COMM4PG4INTEREST`의 `UseState` NOT NULL 오기가 🟠인지 🟡인지: `TFreeInterestInstCommission`에 `UseState IS NULL` 행이 실재하는 배포인지에 달려 있다.

**보류 1건이 열려 있다.** `UF_GET_EXTRACOMM4CLIENT` 단위가 「CASE 분기」 표 `ELSE` 행의 조건 칸 `(그 외 전부)`가 `CaseBranchExtractor`의 고정 문구인지 확인하지 못했다(기준 파일 셋 밖). 15행이 원본과 전수 일치하므로 판정에는 영향이 없다.

**축 A 교차의 사각지대는 두 함수만 실측했다.** SP 여섯은 각 단위가 DDL 전수로 "표 밖 호출 0건"을 확인했으나, 그 확인은 사용자 함수 호출에 한정된다.

**단위 지시의 오류 하나를 여기 적어 둔다.** 상위가 `UIF_SettleYMD`를 "인라인 TVF"로 지시했으나 원본은 다중 문장 TVF다. 단위가 원본을 보고 바로잡았고 판정에는 영향이 없다.
