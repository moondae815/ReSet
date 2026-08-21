# POQSettleProc16 산출물 정합성 감사

단위 32개(축 A 14 + 축 B 18) 전수 검증. 각 단위는 자기 파일만 읽는 서브에이전트 하나가 맡았고, 이 문서는 그 반환값을 합친 것이다.

## 1. 판정

| 축 | 판정 | 단위 수 | 신규 검증 | 캐시 재사용 | 검증 불가 |
|---|---|---|---|---|---|
| A — 원본 DDL ↔ `Spec.md` | **결함** | 14 | 14 | 0 | 0 |
| B — `Spec.md` ↔ 단계 지시서 | **결함** | 18 | 18 | 0 | 0 |

| 등급 | 축 A | 축 B | 합계 |
|---|---|---|---|
| 🔴 | 1 | 9 | 10 |
| 🟠 | 5 | 37 | 42 |
| 🟡 | 20 | 48 | 68 |
| ⚪ | 17 | 30 | 47 |
| **계** | **43** | **124** | **167** |

축 A는 14개 중 3개가 `정합`이고 11개가 `결함`이다. 축 B는 18개 전부 `결함`이다.

신설 6단계(S01·S02·S03·S16·S17·S18)는 레거시 대응이 없어 `검증 불가`로 출발했으나, 여섯 단위 모두 기준값 없이 확인 가능한 범위에서 결함이 나와 판정을 `결함`으로 올렸다. 대조가 성립한 범위는 §3-B에, 성립하지 않은 범위는 §6에 있다.

**두 축의 성격이 다르다.** 축 A의 🔴은 1건이고 나머지는 표기·추적성에 몰려 있다 — 명세서는 대체로 원본을 옳게 옮겼다. 축 B는 🔴 9건, 🟠 37건으로 무게중심이 다르다. 결함이 명세서가 아니라 **명세서에서 단계 지시서로 가는 구간**에 있다.

## 2. 검증 대상 확정

| 무엇을 | 어디서 읽었는가 |
|---|---|
| 소비 명세서 집합(14개 SP) | `raw/prompt-context.md`의 `[Approved Step List]`(4404–4422행) `Legacy:` 필드 |
| 단계 ↔ 레거시 매핑 | 같은 블록. 12개 단계가 SP와 1:1, 6개 단계는 `Legacy:` 공란(신설) |
| 단계 목록(18개) | `agent/steps/*.md` 파일명 |
| 원본 DDL | `output/Objects/[스키마].[이름].Procedure/raw/object_definition.sql` |

`output/Objects/`와 `output/Procedures/`는 분석기가 지금까지 만난 모든 객체를 담으므로 대상 목록으로 쓰지 않았다.

**중첩 SP 전개.** `dbo.UP_Util_Settle_Summary`(S12)가 `EXEC`로 두 SP를 호출한다.

| 하위 SP | 소비 명세서 집합에 있는가 | 처리 |
|---|---|---|
| `dbo.UP_Util_Settle_Summary_AcqManual` | 없음 | 축 A 대상에 포함해 검증(판정 `정합`) |
| `dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA` | 없음 | 축 A 대상에 포함해 검증(판정 `결함`) |

두 SP는 S12가 흡수했는데도 명세서가 이번 Job의 입력에 들어가지 않았다. 그 귀결은 §5의 `S12` 항목에 있다.

**조립본과 번들의 동일성.** `docs/BatchMigrationPlan.md`의 단계 절과 `agent/steps/SNN.md` 18개를 기계로 대조했고 차이가 없었다. 따라서 축 B는 번들만 기준으로 검증했다.

## 3. 단위별 커버리지

### 3-A. 축 A — SP 14개

| SP | 판정 | 상태 | 🔴 | 🟠 | 🟡 | ⚪ |
|---|---|---|---|---|---|---|
| `UP_UTIL_SETTLE_CANCEL_INS` | 정합 | 신규 |  |  |  | 4 |
| `UP_UTIL_SETTLE_COMM_UPD` | 결함 | 신규 |  | 1 | 4 |  |
| `UP_UTIL_SETTLE_EXCEPTION_PROC` | 결함 | 신규 | 1 | 2 | 2 | 1 |
| `UP_UTIL_SETTLE_EXPECT_PROC` | 결함 | 신규 |  | 1 | 3 | 2 |
| `UP_UTIL_SETTLE_INS` | 결함 | 신규 |  |  | 1 | 1 |
| `UP_UTIL_SETTLE_INS_EXTRA` | 결함 | 신규 |  | 1 | 2 | 2 |
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | 정합 | 신규 |  |  |  | 3 |
| `UP_UTIL_SETTLE_PROC_ETC` | 결함 | 신규 |  |  | 1 | 1 |
| `UP_UTIL_SETTLE_SUMMARY_ETC` | 결함 | 신규 |  |  | 1 | 1 |
| `UP_UTIL_SETTLE_SUMMARY_EXTRA` | 결함 | 신규 |  |  | 1 | 1 |
| `UP_UTIL_STAT_PGCOLLECT_INS` | 결함 | 신규 |  |  | 2 |  |
| `UP_Util_PG_Client_CMRate_Ins` | 결함 | 신규 |  |  | 1 |  |
| `UP_Util_Settle_Summary` | 결함 | 신규 |  |  | 2 | 1 |
| `UP_Util_Settle_Summary_AcqManual` | 정합 | 신규 |  |  |  |  |

근거 파일은 모든 단위가 같은 3종이다 — `object_definition.sql`, `docs/Spec.md`, `raw/metadata.json`.

### 3-B. 축 B — 단계 18개

| 단계 | 레거시 대응 | 판정 | 🔴 | 🟠 | 🟡 | ⚪ | 근거 파일(대상 단계 문서 외) |
|---|---|---|---|---|---|---|---|
| S01 | **없음(신설)** | 결함 | 2 | 4 | 5 |  | S02 · common 2종 · `verification/integrity-sql.md` · task-00 · task-01 |
| S02 | **없음(신설)** | 결함 | 1 | 3 | 2 |  | S01·S04·S05·S07·S10·S11·S18 · `00-architecture.md` · `SETTLE_INS` Spec |
| S03 | **없음(신설)** | 결함 | 1 | 4 | 6 | 2 | S16·S02·S14·S01 등 · `AbstractSettleTasklet.cs` · common 3종 · `integrity-sql.md` · `TSettleMst` DDL |
| S04 | `UP_Util_PG_Client_CMRate_Ins` | 결함 | 1 |  | 2 | 2 | 해당 SP `Spec.md` |
| S05 | `UP_UTIL_SETTLE_INS` | 결함 |  | 1 | 2 | 1 | 해당 SP `Spec.md` |
| S06 | `UP_UTIL_SETTLE_CANCEL_INS` | 결함 |  |  | 2 | 2 | 해당 SP `Spec.md` |
| S07 | `UP_UTIL_SETTLE_EXCEPTION_PROC` | 결함 | 2 | 3 | 2 | 1 | 해당 SP `Spec.md` |
| S08 | `UP_UTIL_SETTLE_COMM_UPD` | 결함 |  | 1 |  | 3 | 해당 SP `Spec.md` |
| S09 | `UP_UTIL_SETTLE_EXPECT_PROC` | 결함 |  |  | 5 | 1 | 해당 SP `Spec.md` |
| S10 | `UP_UTIL_SETTLE_INS_EXTRA` | 결함 |  | 1 |  | 3 | 해당 SP `Spec.md` |
| S11 | `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | 결함 | 1 | 2 | 1 | 2 | 해당 SP `Spec.md` |
| S12 | `UP_Util_Settle_Summary` | 결함 |  | 3 | 5 | 1 | 해당 SP `Spec.md` |
| S13 | `UP_UTIL_SETTLE_SUMMARY_ETC` | 결함 |  | 2 | 2 | 2 | 해당 SP `Spec.md` |
| S14 | `UP_UTIL_STAT_PGCOLLECT_INS` | 결함 |  | 1 |  | 5 | 해당 SP `Spec.md` |
| S15 | `UP_UTIL_SETTLE_PROC_ETC` | 결함 |  | 1 | 3 | 3 | 해당 SP `Spec.md` |
| S16 | **없음(신설)** | 결함 | 1 | 5 | 3 |  | `STAT_PGCOLLECT_INS`·`EXPECT_PROC`·`Util_Settle_Summary`·`Summary_AcqManual`·`SUMMARY_ETC`·`INS_EXTRA`·`INS_EXTRA4PLCARD`·`PROC_ETC` Spec 8종 |
| S17 | **없음(신설)** | 결함 |  | 3 | 5 | 1 | S16·S01·S02·S18 · common 2종 · `integrity-sql.md` · task-00 · task-17 |
| S18 | **없음(신설)** | 결함 |  | 3 | 3 | 1 | S02·S17·S01 · common 2종 · task-00 · task-18 · `integrity-sql.md` |

신설 6단계는 기준값이 없으므로 아래 범위에서만 대조가 성립했다.

| 단계 | 대조가 성립한 범위 | 결과 |
|---|---|---|
| S01 | 스키마 규약 · 업무 테이블 쓰기 0건 · 뒤 단계 데이터 선점 없음 | 세 항목 합격. 제어 테이블 계약에서 결함 |
| S02 | 스키마 규약 · 업무 테이블 DML 없음 · 잠금 해제 경로 · 재시작 결정의 안전성 | 앞 셋 합격. 재시작 결정에서 🔴 |
| S03 | 스키마 규약 · 업무 테이블은 SELECT만 · 기준선 범위가 실제 UPDATE 범위를 덮는가 · 페이로드 19컬럼 실재성 | 네 항목 합격. 세션 연속성·해시 계약에서 결함 |
| S16 | 업무 테이블 쓰기·그림자 복원 지시 없음 · 스키마 규약 · 검증식이 레거시 불변식인가(SP 8종 대조) | 앞 둘 합격. 검증식에서 🔴 |
| S17 | 업무 테이블 쓰기·그림자 승격 없음 · 스키마 규약 · 오류 코드 단일성 | 세 항목 합격. 공개 게이트 계약에서 결함 |
| S18 | 업무 테이블 쓰기 0건 · 스키마 규약 · 패턴 매칭 DROP 부재 · 그림자 즉시 삭제 안 함 | 네 항목 합격. 잠금 자원명·종료 상태에서 결함 |

## 4. 축 A 결함 — 원본 DDL ↔ `Spec.md`

§4-1의 공통 패턴으로 접힌 것은 아래 표에 없다.

| 등급 | SP | 기준값 앵커 | 산출물 앵커 | 기준값 | 산출물 | 영향 |
|---|---|---|---|---|---|---|
| 🔴 | `UP_UTIL_SETTLE_EXCEPTION_PROC` | object_definition.sql:362-367 | Spec.md:325-327,403-404 | 실행순서 13(-20)의 파생테이블 X에서 PG 원가 기준금액은 IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt). UF_GET_COMM4PG·4INTEREST·PGETC 세 곳 모두 프로모션건은 할인금액 기준(주석: 프로모션건 원가계산방식 변경 2023.01.01). CL측 인자는 (A.TxAmt-ISNULL(A.NonSettleAmt,0))로 서로 다름 | 파생테이블 X의 컬럼 표현식이 문서 어디에도 없음. SET 표현식은 ISNULL(X.PGCOMM,0) 수준에서 멈추고 "거래금액 기준 정율 수수료"로만 기술 | 명세만 보고 이행하면 프로모션 거래의 PG 원가수수료·할부원가·PG인증비를 TxAmt 기준으로 계산해 금액이 달라진다. CL측과 PG측 기준금액이 다르다는 사실도 소실 |
| 🟠 | `UP_UTIL_SETTLE_EXPECT_PROC` | object_definition.sql:39 | Spec.md:137,390 | A.PGName NOT IN ('PLCard','SamSungPay','SSGPayCard','KakaoPay','KakaoCard','impaymobile','NaverCard','ApplePay','TossCardAuth') — 9개 PG 제외 목록 리터럴 | "제외 PG 목록에 포함되지 않은 행"으로만 지칭, 9개 값 미열거 | 1-1 자동회수의 대상 행 집합을 문장1 절만 읽고 재현 불가. 전제: Spec 63행과 405-406행을 결합하면 역산되므로 원문과 모순은 없으나, 두 절을 함께 읽지 못하면 행 집합이 달라져 🟠 |
| 🟡 | `UP_UTIL_SETTLE_COMM_UPD` | object_definition.sql:63-64,160,227 | Spec.md:148 | 취소수수료 부가세는 CAST(...*(0.1) AS INT) 고정 10%이며 TPGSettleRate.incVTax를 쓰지 않음. incVTax는 문장 2(해외카드)에서만 사용 | incVTax 사용 관계: "해외카드 및 일부 취소수수료 부가가치세율 계산에 사용합니다" | 컬럼 사용처 오귀속. 이 행만 근거로 구현하면 취소수수료 부가세에 UF_GET_INCVTAXRATE를 적용해 금액이 달라질 수 있음. 전제: 문장 4·7 표현식 표가 상수 0.1을 정확히 보존 |
| 🟡 | `UP_UTIL_SETTLE_COMM_UPD` | object_definition.sql:72-74 | Spec.md:197 | 문장 2의 TPGSettleRate 조인 키는 YMD·PGNAME·MALLID 3개뿐(CLIENTID 컬럼 자체가 없음) | "YMD, CLIENTID, PGNAME, MALLID로 결합"으로 두 이력 테이블을 같은 키로 뭉뚱그림 | 조인 키 표기 오류(문서 내부 모순 — SELECT 표 70행은 정확). 없는 컬럼이라 구현 시 즉시 실패해 금액·행집합 위험은 낮음 |
| 🟡 | `UP_UTIL_SETTLE_EXPECT_PROC` | metadata.json:5162 (TSettleMst.EDIReqYmd varchar(8) NULL) | Spec.md:232,302-319 | 의존성 메타데이터에 EDIReqYmd가 정상 정의됨 | "제공된 스키마 정의에 포함되지 않은 UPDATE 대상 … 스키마 불일치 항목"이라 단정, 스키마 표에서도 누락 | 실재 컬럼을 "타입 미상"으로 표기해 불필요한 조사·오판 유발. 원인은 RawPromptContext 스키마 표가 참조 컬럼만 필터링한 것 |
| 🟡 | `UP_UTIL_SETTLE_EXPECT_PROC` | metadata.json:4376 (CollectMonth2/3·CollectDay2/3·CollectTxSDay2/3·CollectTxEDay2/3 tinyint NOT NULL) | Spec.md:341,269,289-301 | 의존성 메타데이터가 8개 컬럼을 모두 정의 | "제공된 스키마 표에 정의되지 않았습니다 … 타입과 Null 허용을 확정하지 않습니다" | 같은 원인(컬럼 필터링). "제공 스키마 전체 컬럼 영향 매핑" 절 제목이 실제로는 부분집합이라 회수일 계산 컬럼 추적이 끊김 |
| 🟡 | `UP_UTIL_SETTLE_EXPECT_PROC` | object_definition.sql:169,183,131-132 | Spec.md:96,291,87,316,217,63,102 | 주석의 코드 범례 — 회수구분(1:자동,7:수납,8:미회수,9:미지정), 지급상태(0:미지급,1:지급완료,2:지급예정,5:지급확정,9:지급불가), 정산주기 코드 | 실제 사용된 값만 기술, 코드 범례 전체 미수록 | 필터는 정확해 결과 동일하나 상태값 의미의 추적성 소실(OutState=9를 유지하는 이유가 "지급불가"임이 드러나지 않음) |
| 🟡 | `UP_UTIL_SETTLE_INS_EXTRA` | object_definition.sql:213 | Spec.md:144 | OutYMD = (SELECT OutYMD FROM dbo.UIF_SettleYMD(...)) — 우변은 TVF 반환 컬럼이지 TSettleMst.OutYMD가 아님 | "SET 우변에서 자기 자신을 참조합니다: OutYMD … 동시 평가 의미를 보존해야 합니다" | 파서 오탐(AstUpdateMappings.SelfReferencedColumns)을 그대로 전재. 없는 순서 의존성을 부과하고, 문장 3·5의 진짜 자기참조 경고와 섞여 신뢰도를 떨어뜨림 |
| 🟡 | `UP_Util_Settle_Summary` | object_definition.sql:17 | Spec.md:26,275-283 | SET NOCOUNT ON이 AS 직후 BEGIN TRAN 앞에 존재 | Spec 전체에 NOCOUNT 언급 없음 | 세션 옵션 누락. DONE_IN_PROC 동작이 달라져 호출 계층이 행수 메시지를 결과셋으로 오인 가능. 금액·행집합 영향 없음 |
| ⚪ | `UP_UTIL_SETTLE_CANCEL_INS` | object_definition.sql:38-52 | Spec.md:65 | B에서 실제 SELECT되지 않는 6개 컬럼(YMD,AYMD,CYMD,INSTATE,OUTSTATE,NonSettleAmt) | SELECT 참조 컬럼 목록에 포함 | ReferencedColumnsPerTable과 문자·순서까지 일치 → 파서 우선이므로 결함 아님. 파서 산출물 특성 |
| ⚪ | `UP_UTIL_SETTLE_CANCEL_INS` | metadata.json RawPromptContext 스키마 | Spec.md:67 | CompanySalesType 0~3=영세·중소1~3, 4/NULL=일반 | "코드에 없는 의미는 가정하지 않습니다"로 유보(같은 문서에서 USESTATE 등은 의미 사용) | 조건 서술은 정확해 행 집합 영향 없음. "영세·중소 건 취소정산 제외"라는 업무 의도가 문서에 없음 |
| ⚪ | `UP_UTIL_SETTLE_EXCEPTION_PROC` | object_definition.sql:131-139,163,188,341,343,346-353 | Spec.md:139-170 | SP가 TSettleMst의 CLCOMMTYPE·PGCOMMTYPE·PGVTTYPE·PGIntRealComm·ProcState 5개를 갱신 | "제공 스키마 전체 컬럼 대응" 표에 이 5개가 없음(제공 스키마 자체에 없음). 불일치를 언급하지 않음 | 스키마 원천이 SP 실제 사용 컬럼보다 오래됐음을 경고하지 않아 이행 스키마 설계 시 누락 가능 |
| ⚪ | `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | object_definition.sql:189,205 | Spec.md:205 | 두 UPDATE의 갱신 대상 인스턴스(별칭 A)에 WITH (NOLOCK)이 부여됨 | "원천 조회, 사전 검사 및 다수 조인에 NOLOCK이 사용됩니다"로 일괄 기술 | 행 집합·금액 영향 없음. 잠금·힌트 재현 논의에서의 추적성만 저하 |
| ⚪ | `UP_UTIL_SETTLE_PROC_ETC` | object_definition.sql:69-101 | Spec.md:170-173,189-190 | SET @v_intID=0 후 SELECT @v_intID=ID, 분기는 IF @v_intID>0. TSettleMiss.ID는 int NULL DEFAULT((0))이라 기존 행 ID가 0/NULL이면 INSERT 경로로 감 | ">0이면 갱신, 아니면 삽입"으로만 기술. 기존 행이 있어도 ID가 0/NULL이면 중복 삽입되는 경계 조건이 예외·제약 표에도 없음 | DDL과 모순되지는 않으나 경계 동작 누락. C# 이관 시 "기존 행 존재→갱신"으로 단순화하면 다른 행 집합이 생성될 수 있음(실데이터에 ID=0/NULL 행이 있을 때만 발현) |
| ⚪ | `UP_UTIL_SETTLE_SUMMARY_ETC` | object_definition.sql:4,39 | Spec.md:24 | 헤더·커서 주석이 업무 범위를 "신용카드, 문화상품권, 도서상품권"으로 명시 | 상태코드 기준으로만 서술, 결제수단 범위 의도 미언급 | 코드에 결제수단 필터가 없어 동작상 오도는 아님(주석이 코드보다 좁음). 주석·코드 범위 불일치라는 조사 단서가 남지 않음 |
| ⚪ | `UP_UTIL_SETTLE_SUMMARY_EXTRA` | object_definition.sql:193-200 vs 223-228 | Spec.md:220,222,246-247 | DELETE는 YMD>=@v_strReqYMD AND OUTYMD>=@v_strReqYMD AND ISNULL(OUTYMD,'')<>'', INSERT는 OUTYMD>= 조건이 없어 삭제가 삽입보다 좁음 | 비대칭은 정확히 기록. 다만 귀결이 "동일하다고 단정할 수 없습니다"라는 유보형에 그치고 중복 적재 가능성은 미명시 | 사실관계 누락 없음. 재작성 시 멱등성 판단을 스펙만으로 내리기 어려움 |
| ⚪ | `UP_Util_Settle_Summary` | object_definition.sql:40-41,52-53 vs 126-127,163-164 | Spec.md:280 | TPartialCancelByTX·TSettleByIN의 DELETE는 YMD 단독인데 대응 INSERT는 USESTATE=2·INSTATE=1을 추가로 걸어 삭제 범위가 더 넓음 | 개별 조건은 정확하나 대칭성 언급은 TSettleByOUT에만 있고 두 테이블의 삭제⊃삽입 비대칭은 미명시 | 오도는 아님. 재실행 시 삭제만 되고 복원되지 않는 행이 있을 수 있다는 점이 드러나지 않음 |

### 4-1. 전 SP 공통 결함

**A1. 조건 범위를 실제와 다르게 단언** — 🟠 4

Spec이 원본보다 좁은 적용 범위를 단언하거나 조인 키를 실제보다 넓게 뭉뚱그린다. 이관자가 Spec만 보고 필터를 넣으면 대상 행 집합이 달라진다. 축 B의 S07·S08에서 실제로 그 일이 일어났다.

| SP | 원본 | 산출물 |
|---|---|---|
| `UP_UTIL_SETTLE_COMM_UPD` 🟠 | 문장 7(Toss/TossPoint 최종 부분취소)의 갱신 대상 X에 YMD=@pi_strYMD 필터가 없음. K.ID=MAX(ID)는 해당 PLTID의 전체 기간에서 구함 | 요약 표의 취소수수료 행: "정산 행은 YMD = @pi_strYMD" |
| `UP_UTIL_SETTLE_EXCEPTION_PROC` 🟠 | 실행순서 18(-29)의 바깥 UPDATE에 YMD 필터가 없음. 서브쿼리만 정산일로 제한되고 갱신 대상은 모든 정산일에 걸침 | "YMD = @pi_strYMD를 기본 범위로 … 계산합니다"라고 일괄 기술, 무제한이라는 명시 없음 |
| `UP_UTIL_SETTLE_EXCEPTION_PROC` 🟠 | 실행순서 4(-1)의 조인 키는 YMD·CLIENTID·PGNAME 뿐이며 MallID 조인이 없음. 바로 앞 실행순서 3은 MALLID를 포함 | 실행순서 4 절은 조인 키를 전혀 기술하지 않음. 실행순서 3 절은 "정산일·고객사·PG·Mall 계약이 일치"로 명시 |
| `UP_UTIL_SETTLE_INS_EXTRA` 🟠 | 선행 EXISTS는 OutState IN (1,5) AND OutYMD IS NOT NULL 일 때만 -9 중단. DELETE에는 OutState/OutYMD 조건이 전혀 없음 | "지급 완료·확정 행은 선행 EXISTS 검사에서 중단되므로 삭제 대상에 포함되지 않습니다", 개요는 "미확정 데이터를 삭제" |

**A2. `ROUND` 3번째 인자의 값 의미 미기록** — 🟡 2 · ⚪ 1

원본 주석 `--0:반올림, 0<>절사`가 값 매핑을 명시하는데 Spec은 "반올림 또는 절사"로만 적는다. 금액 계산의 방향이 결정되는 자리다. `INS_EXTRA4PLCARD`의 Spec은 이 매핑을 정확히 기록한 반례다.

| SP | 원본 | 산출물 |
|---|---|---|
| `UP_UTIL_SETTLE_COMM_UPD` 🟡 | ROUND(식,0,dbo.UF_GET_PGCommOption(...))의 3번째 인수는 0이면 반올림, 0이 아니면 절사 | "PG 수수료 반올림 옵션으로 정수화합니다"로만 기술, 0이 아닌 값이 절사임을 설명하지 않음 |
| `UP_UTIL_SETTLE_INS_EXTRA` 🟡 | --0:반올림, 0<>절사 주석이 ROUND 3번째 인자 의미를 명시 | 수식은 전재하되 0/비0 매핑 미기재 |
| `UP_UTIL_SETTLE_INS` ⚪ | 인라인 주석 --0:반올림, 0<>절사 로 CommRoundFlag 계열 값의 의미를 코드화 | "반올림 또는 절사"로만 기술, 0=반올림/0이외=절사 매핑 미기록 |

**A3. 주석 처리된 블록 미기록 또는 부분 기록** — 🟡 6 · ⚪ 3

비실행 조건·대체 로직·PRINT·변경 이력 주석이 Spec에 없거나 "실행되지 않습니다" 한 문장으로 처리된다.

| SP | 원본 | 산출물 |
|---|---|---|
| `UP_UTIL_SETTLE_COMM_UPD` 🟡 | --AND ClientID NOT IN (SELECT ClientID FROM dbo.UF_GET_CLIENTID4TMONET()) --예외처리 제거(2021.11.29) 및 티모넷 블록의 동일 TVF | 주석 처리된 비실행 코드 표는 두 블록만 기재. 문장 13 내부의 주석 조건과 해당 TVF는 문서 어디에도 없음 |
| `UP_UTIL_SETTLE_INS` 🟡 | 환불 분기의 블록 주석(/* */)으로 비활성화된 구 CLCOMM/CLETC 계산식. 여기서만 등장하는 TRefundClient.FeeCharge, TRefundMst.CLIENTFEEAMT 참조 | Spec 전체에 주석 처리 블록 언급 없음(FeeCharge·CLIENTFEEAMT 미출현) |
| `UP_UTIL_SETTLE_PROC_ETC` 🟡 | 커서 원천 WHERE의 주석 조건 --AND C.ClientIDType <> 1 --0:일반,1:내부테스트용,2:Cafe24,3:MakeShop,4:MySoho. 검증 SELECT에는 이 주석이 아예 없음 | "주석 처리된 ClientIDType 조건은 실행되지 않습니다"만 기술. 조건식 원문·코드값·커서에만 존재한다는 사실 미기록 |
| `UP_UTIL_SETTLE_SUMMARY_ETC` 🟡 | 주석 처리된 PRINT 2건(루프 내 커서 키 추적, 종료 직전 처리 건수 출력) | 흐름 요약·제약 표 어디에도 미기록. @v_intRowCnt는 "출력하거나 반환하지 않습니다"로만 기술 |
| `UP_UTIL_STAT_PGCOLLECT_INS` 🟡 | 주석: tigerfive 2009.02.09 이력과 --,SUM(CASE WHEN ... OR CLIENTID IN ('PAYLETTER','PLTEST') ...) AS AHEADSETTLEAMT | "주석 처리된 과거 조건은 실행되지 않습니다" 한 문장만. 주석 식 내용·변경 주체·일자·사유 미기록 |
| `UP_Util_PG_Client_CMRate_Ins` 🟡 | TClientSettleRate 두 번째 UNION ALL 분기의 비활성 주석 조건 --AND B.ContractCancelYMD = @pi_strYMD (활성 조건 A OR B 의 이전 버전) | 활성 조건만 기술, 주석 처리된 이전 조건의 존재·의미 미언급 |
| `UP_UTIL_SETTLE_EXPECT_PROC` ⚪ | 주석 "1-3. 환불건 미회수 처리 …(2020.12.01 회수건부터)", "매입요청일(D)+1 : 집계 고려" | 두 주석 모두 미반영 |
| `UP_UTIL_SETTLE_INS_EXTRA` ⚪ | 주석 처리된 대체 로직 3개(대체 CLComm 판정, --AND C.ExtraSettleFlag=Y, TMONET 예외처리 제거 이력) | 언급 없음 |
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` ⚪ | 조건의 비즈니스 사유 주석(영중소 우대수수료는 국내카드만, KSNet 환급매입, 금액 유지) | 조건식은 정확히 기록하되 사유 주석은 미기재. 281행은 "회수금액/정산금액 유지"를 "회수상태 및 지급상태"로 바꿔 서술 |

**A4. 식별자 표기를 원문처럼 서술** — 🟡 3 · ⚪ 3

파서가 정규화한 3부 이름이나 별칭 스코프 오탐을 원문 표기처럼 제시한다. `INS_EXTRA4PLCARD`는 파서 오탐을 Spec이 명시적으로 정정한 반례다.

| SP | 원본 | 산출물 |
|---|---|---|
| `UP_UTIL_SETTLE_EXCEPTION_PROC` 🟡 | SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT 등 5개를 3부 식별자로 호출, 파서도 접두 포함 수집, 프롬프트에 DDL 제공됨 | 참조 코드 객체 절은 DB 한정자를 떨어뜨려 나열하고 "분석 생략(외부 객체)"라 표기. 같은 문서 UDF 표는 내부 규칙을 상세 기술하고 "정의가 제공되지 않은 호출은 없습니다"라고 단정 — 문서 내부 모순 |
| `UP_UTIL_SETTLE_SUMMARY_EXTRA` 🟡 | 3부 식별자는 PaymentDB.dbo.TExtraSettleIn 한 곳뿐. TSettleByTX·TSettleMst·TPartialCancelByTX·TSettleByIN·TSettleByOUT는 전부 비수식 | "PaymentDB와 SETTLE_POQ_DB의 3부 식별자를 사용하는 DB 간 참조"로 서술. 본문 표도 파서 정규화 이름만 노출 |
| `UP_UTIL_STAT_PGCOLLECT_INS` 🟡 | 모든 테이블 참조가 1부 식별자(비수식). 3부 식별자도 다른 DB 참조도 없음 | "3부 식별자 기반 크로스 데이터베이스 참조이며 Linked Server 원격 참조가 아닙니다" |
| `UP_UTIL_SETTLE_CANCEL_INS` ⚪ | INSERT 대상이 1부 식별자 INSERT INTO TSettleMst (미한정, 실행 DB 컨텍스트 의존) | 전 구간 SETTLE_POQ_DB.dbo.TSettleMst로 표기. 원본 표기는 개요 1행에서만 언급 |
| `UP_UTIL_SETTLE_INS_EXTRA` ⚪ | 원본은 TSettleMst를 무한정 1~2부 식별자로 참조 | "PaymentDB와 SETTLE_POQ_DB를 3부 식별자로 참조하지만…" |
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` ⚪ | A.ReqYMD의 별칭 A는 파생테이블 내부의 SETTLE_CARD_DB.dbo.TExtraTxMst이며 TSettleMst에서 ReqYMD를 읽는 곳은 없음 | 파서가 ReqYMD를 TSettleMst 참조 컬럼으로 귀속(별칭 스코프 오탐). Spec.md:76은 이를 정정해 TExtraTxMst.REQYMD로 기록 |

**A5. 헤더 주석과 구현의 괴리 미기록** — 🟡 1 · ⚪ 1

헤더가 선언한 반환 계약·내부 호출 유무가 구현과 어긋나는데 Spec이 그 모순 자체를 적지 않는다.

| SP | 원본 | 산출물 |
|---|---|---|
| `UP_Util_Settle_Summary` 🟡 | 헤더 주석은 내부 SP 호출 없음(NONE)이라 선언하나 실제로 두 EXEC 존재 | 두 EXEC는 정확히 기술하나 헤더 주석이 모순된다는 사실(스테일 주석) 미기록 |
| `UP_UTIL_SETTLE_CANCEL_INS` ⚪ | 헤더 주석 -- Return Value : =0->성공, <>0->실패 | 성공 경로에서 미설정이며 호출자 초기화 필요를 정확히 기술. 다만 헤더 계약과 구현의 괴리 자체는 미언급 |

**A6. 파서 문장 순번을 그대로 전재** — 🟡 1 · ⚪ 1

파서 채번이 배치 중간에 리셋되거나 표기형태별로 독립 채번되어 "문장 1"이 여러 번 나온다. 앵커로 쓸 수 없다.

| SP | 원본 | 산출물 |
|---|---|---|
| `UP_UTIL_SETTLE_EXCEPTION_PROC` 🟡 | UPDATE 대상 표기가 3형태로 혼재(실제 대상은 동일) | 절 제목의 "(문장 N)"이 표기형태별로 독립 채번되어 중복(1·3·13이 모두 문장1 등) |
| `UP_UTIL_SETTLE_EXPECT_PROC` ⚪ | 파서 문장 순번이 배치 중간에 리셋됨 | 그대로 전재해 "문장 1" 3회, "문장 2" 2회 등장 |

## 5. 축 B 결함 — `Spec.md` ↔ 단계 지시서

§5-1의 공통 패턴으로 접힌 것은 아래 표에 없다.

| 등급 | 단계 | 기준값 앵커 | 산출물 앵커 | 기준값 | 산출물 | 영향 |
|---|---|---|---|---|---|---|
| 🔴 | `S03` | agent/src/AbstractSettleTasklet.cs:25-41 · S05.md:501 · S08.md:534 | S03.md:3,28,194 | S03이 "기준점"이 되려면 S04–S15가 S03과 동일 세션·동일 스냅샷을 이어받거나, 이어받을 수 없다는 사실과 대체 수단이 문서에 적혀 있어야 함 | AbstractSettleTasklet.Execute가 단계마다 context.MainDb.CreateConnection()으로 새 커넥션을 열고 그 안에서 SNAPSHOT을 설정·커밋한다. S03은 자기 커넥션에서 SNAPSHOT 후 COMMIT TRAN으로 스냅샷을 종료하는데 S05/S08은 "S03에서 고정한 기준점과 동일 실행 세션의 SNAPSHOT을 사용한다"고 서술. S03.md는 세션 연속성 문제를 한 줄도 다루지 않음 | 전제: 실행 중 원천에 동시 쓰기가 있는 경우. S04–S15가 각자 다른 시점의 스냅샷을 열어 단계별로 서로 다른 원천을 읽어 수수료·집계 금액이 달라질 수 있음. 동시 쓰기가 없다면 🟡(허위 보증 표기) |
| 🔴 | `S07` | Spec.md:80,138 | S07.md:188 | 실행순서 2에서 TPGSettleRate.ETCAmt를 "직접 참조: 예 / PG 공제금액과 부가세 계산"으로 명시. TPGSettleRate를 쓰는 UPDATE는 실행순서 2뿐 | 부가세 식이 (D.PGCommRaw + D.PGETC)로 계약 테이블의 B.ETCAmt가 아니라 TSettleMst.PGETC를 사용. B.ETCAmt는 DML 2 어디에도 없고 CommMethod=1 분기에는 ETC 항 자체가 없음 | 할인 반영 비원천 PG의 PGVT 금액이 달라짐 |
| 🔴 | `S07` | Spec.md:233 (원천 표현식 B.COMMISSIONRATE/100) | S07.md:306,308,312 외 :149,:155,:714,:721,:755 | 수수료율 나눗셈 상수가 정수 리터럴 100 | 전 구간에서 100.0으로 변경(/100.0, /10.0) | 전제: 율 컬럼이 정수형이거나 decimal 나눗셈 스케일이 달라지는 경우. 실행순서 5는 결과가 ROUND(...,-1,1) 절삭과 <=150 경계 비교에 바로 들어가므로 경계행의 PGComm이 뒤집힐 수 있음 |
| 🔴 | `S11` | Spec.md:93-134,269 | S11.md:324-334 vs :335-420 | INSERT 대상 컬럼 42개에 각각 대응하는 원천 값 42개를 삽입 | 컬럼 목록은 42개인데 SELECT 값 목록은 40개(스크립트 카운트 확인). 컬럼 21~31에 상수 0이 11개 필요한데 :356-364에 9개만 있음. 결과적으로 CAST(ISNULL(X.RawCLComm,0) AS INT)가 PGINTEXPCOMM에, UF_GET_ROUND4VAT가 PGINTREALCOMM에, PGCOMM/PGVT 식이 CLCOMM/CLVT에, 상수 0/2가 PGCOMM/PGVT에, NULL이 INSTATE에, OUTYMD 하위질의가 OUTSTATE에, X.YMD가 INYMD에 대응되고 ProcState·ExtraSettleFlag는 값이 없음 | 의사코드대로 이행하면 컬럼 수/값 수 불일치로 INSERT 자체가 실패해 차액정산 행이 하나도 생성되지 않음. 개수만 맞춰 봉합하면 수수료·부가세·지급상태·지급일이 전혀 다른 컬럼에 저장되어 금액이 완전히 달라짐 |
| 🟠 | `S01` | S01.md:18-19,74-84 | S01.md:164-177,194-204 | 표 18행과 C# 의사코드는 완료 체크포인트·저널의 BatchDate/JobName이 현재 요청과 다르면 BATCH-VAL-002로 차단 | SQL은 체크포인트·저널을 RunId+StepCode로만 조회하고 건너뛰기 경로에서 기준일·작업명 대조 없이 그대로 WasSkipped=1 반환 | 선언한 BATCH-VAL-002 경로가 SQL에 없음. 다른 기준일로 기록된 완료 이력이 차단 대신 건너뛰기로 통과 → 뒤 단계가 잘못된 기준일 컨텍스트로 실행됨 |
| 🟠 | `S02` | S02.md:12 (본문 선언) | S02.md:51-62 | "재개 후보가 둘 이상이면 BATCH-RST-001로 중단하고 자동 재시작하지 않는다" | 의사코드는 MAX(CASE WHEN BR.RunStatus IN ('Running','Failed','Restarting') THEN BR.RunId END)로 후보를 말없이 하나 고름. 후보 개수를 세는 분기도 BATCH-RST-001을 세우는 경로도 없음 | 선언된 안전 장치가 본문에 미구현. 동일 기준일에 미완료 실행이 둘 이상이면 임의의 RunId가 채택되어 그 실행의 체크포인트가 완료로 신뢰되고 실제로는 미완료인 단계가 건너뛰어짐 |
| 🟠 | `S03` | SQL Server SNAPSHOT은 참여 DB마다 ALLOW_SNAPSHOT_ISOLATION ON을 요구 · common/00-architecture.md:98은 READ_COMMITTED_SNAPSHOT만 언급 | S03.md:28,134 | SNAPSHOT의 DB 수준 선행 조건과 크로스 DB(SETTLE_POQ_DB, PaymentDB, PLCardDB, SETTLE_CARD_DB) 스냅샷 트랜잭션의 전제가 명시되어야 함 | ALLOW_SNAPSHOT_ISOLATION은 산출물 전체에 등장하지 않음. 00-architecture.md:98은 READ_COMMITTED_SNAPSHOT을 켜지 말라고만 함. S03은 SNAPSHOT 안에서 4부 이름을 읽는데 그 DB의 선행 설정 요구를 적지 않음 | 설정이 꺼져 있으면 S03의 첫 원천 읽기가 SQL 오류 3952로 실패해 BATCH-SNAP-001로 배치 전체 중단. 이미 켜져 있다면 🟡 |
| 🟠 | `S03` | agent/verification/integrity-sql.md:56-93 (b.RowHash <> HASHBYTES('SHA2_256', CONCAT_WS('\|', m.ID, m.OutState, m.OutYMD, m.TxAmt, m.CLComm, m.CLVT, m.PGComm, m.PGVT))) | S03.md:131-132,137-158 | 기준선 RowHash의 생성 알고리즘과 유일한 소비자의 재계산 알고리즘이 같아야 함 | S03은 19개 컬럼의 FOR JSON PATH 페이로드를 SHA2_256 해싱. 소비자는 CONCAT_WS로 8개 컬럼만 해싱하며 그중 CLComm·CLVT·PGComm·PGVT는 S03이 페이로드에 담지도 않음 | 두 해시는 구조적으로 절대 일치하지 않으므로 불변성 검증 쿼리가 모든 기준선 행을 변조로 판정. 정상 실행도 공개 차단 |
| 🟠 | `S03` | agent/verification/integrity-sql.md:57 (b.ImmutableState) | S03.md:108-121 | 소비자가 참조하는 컬럼은 생산자가 채워야 함 | S03의 BatchImmutableLedgerBaseline INSERT 컬럼에 ImmutableState가 없음. task-00-bootstrap.md:38도 DDL 정의를 이 문서들에 위임 | 불변성 검증 SQL이 컴파일되지 않거나, 부트스트랩이 컬럼을 만들면 항상 NULL인 무의미 컬럼이 됨 |
| 🟠 | `S07` | Spec.md:189,421 / Spec.md:163,432 | S07.md:129 | 실행순서 1의 조건 열거에 ExtraSettleFlag가 없음(ExtraSettleFlag는 원천 PG 프로모션 = 실행순서 12에 귀속) | DML 1의 WHERE에 ISNULL(A.ExtraSettleFlag,0)=0을 추가 | 비원천 PG 프로모션 할인 적용 대상 행이 축소됨 |
| 🟠 | `S07` | Spec.md:200,331,377 | S07.md:219-220,594-595,851-852 | 실행순서 2·13·17 모두 파생결과와 갱신대상의 결합 키가 PLTID + ID | 세 곳 모두 ON C.ID = A.ID 단일 키로 결합(CTE는 PLTID를 투영만 하고 쓰지 않음) | 전제: ID 단독 유일성이 보장되지 않는 경우 결합 행 집합과 적용 원천값이 달라짐 |
| 🟠 | `S07` | Spec.md:78,385 | S07.md:858-861 (검증 SQL도 :912-919) | 실행순서 18의 갱신 범위도 TSettleMst의 기본 범위인 입력 정산일 | 갱신 대상 A에 YMD 필터가 없음. A.UseState=0 + EXISTS(B.YMD=@pi_strYMD ...)만 적용 | 동일 PLTID를 가진 타 정산일의 정상 거래까지 OutState=9로 갱신되어 대상 행 집합 확대 |
| 🟠 | `S08` | Spec.md:202 | S08.md:293-335 | 문장 7의 B.CommissionCancelFlag=1은 내부 대상 PLTID 선별 조건(A.YMD, A.PGNAME IN (Toss,TossPoint), A.USESTATE=2와 동일 위치). 외부는 원거래일·고객사·PG·몰 결합만 서술 | 내부 SELECT DISTINCT PLTID 하위 조회에서 취소수수료 플래그 조건을 제거하고, 외부 UPDATE의 WHERE C.CommissionCancelFlag=1(MAX(ID) 대상 행의 AYMD 기준 이력)로 옮김 | 대상 행 집합 변동 가능. 전제: 동일 PLTID 안에서 AYMD·ClientID·MallID가 불변이면 동일하나, 부분취소 행(UseState=2)과 MAX(ID) 행의 결합 키가 다르면 갱신 대상 PLTID가 달라짐 |
| 🟠 | `S12` | Spec.md:213-216,259-262,281 | S12.md:377-392,420 | 두 종속 SP가 네 집계 테이블에 가하는 추가 삭제·삽입 조건이 Spec에 기술됨. 특히 Spec:281은 "EXTRA의 TSettleByOUT 삭제에는 OUTYMD >= @v_strReqYMD가 있으나 삽입에는 없다. 동일 조건으로 단순화할 수 없다"고 명시적으로 경고 | 두 처리를 batch.usp_S12ManualAcquisitionSummary·batch.usp_S12ExtraSettlementSummary라는 신규 배치 전용 헬퍼로 위임하고, "batch 스키마에 생성한다, 별도 커밋 없이 호출 트랜잭션에 참여해야 한다" 두 문장 외에 삭제·삽입 조건·PG명 목록·CompanySalesType/ExtraSettleFlag 조건·커서 단위·비대칭 경고를 전혀 이관하지 않음(임의로 채우지도 않고 빈 껍데기로 위임) | 구현자는 S12 지시서만으로 두 헬퍼를 작성할 수 없고 별도 입력이 필요한데 그 사실이 어디에도 전제로 적히지 않음. 이름만 보고 자체 재구성하면 Spec:281의 비대칭이 대칭화되어 TSettleByOUT 재구축 행 집합이 달라짐 |
| 🟠 | `S15` | Spec.md:83,173,190 | S15.md:219 | 신규 ID는 필터 없이 SELECT @v_intID = MAX(ID)+1로 계산하며, "MAX(ID)가 NULL이면 MAX(ID)+1도 NULL이고 프로시저는 이를 0이나 1로 보정하지 않는다"고 제약사항으로 명시 | SELECT @v_intID = ISNULL(MAX(ID), 0) + 1로 NULL을 0으로 보정 | 전제: TSettleMiss가 비어 있는 경우에만 발현. 레거시는 ID=NULL 행을 삽입하고 그 행은 이후 MAX(ID) 조회와 WHERE ID=@v_intID 어디에도 걸리지 않아 매 실행 새 행이 추가됨. 산출물은 ID=1 행을 만들어 이후 실행이 UPDATE 누적 분기로 들어가므로 TSettleMiss 행 집합이 달라짐 |
| 🟠 | `S16` | (동일) | S16.md:221-226 | 집계 테이블이 비어 있으면(누락) 반드시 위반으로 검출 | CROSS JOIN 결과가 공집합이면 양쪽 SUM이 모두 NULL→0이 되어 HAVING 0<>0이 거짓 → 이슈 0건. TSettleByTX가 통째로 비어도 S16이 통과 | 오탐과 반대 방향의 오검출 누락. 집계 전량 소실 상태에서 S16이 성공 체크포인트를 남기고 S17이 결과를 공개 |
| 🟠 | `S16` | S16.md:18 (필수 선행 체크포인트 "누락"을 오류 조건으로 선언) | S16.md:60-85 | 체크포인트 행이 없는 경우도 검출 | IF EXISTS (… StepCode IN ('S01'..'S15') AND CheckpointStatus <> 'Completed') — 존재하면서 미완료인 행만 검출. S01~S15 중 일부의 체크포인트 행이 아예 없으면 조건이 거짓이 되어 통과 | 선언한 오류 조건("누락")을 의사코드가 검출하지 못함. 선행 단계가 실행되지 않은 실행에서 S16이 성공 체크포인트를 남기고 S17이 미완성 결과를 공개할 수 있음 |
| 🟠 | `S18` | S02.md:27-34 (SET @v_lockResource = CONCAT(N'POQSettleProc16:', @pi_strYMD)), S02.md:13 | S18.md:31-33,95-97,142-144 | S02가 획득한 것과 동일한 자원명(POQSettleProc16:<기준일 YMD>)으로 sp_releaseapplock 호출 | 세 곳 모두 N'POQSettleProc16:' + CONVERT(NVARCHAR(36), @RunId) 즉 RunId 기반 자원명으로 해제 시도 | S02의 잠금이 S18의 어느 경로(스킵·성공·CATCH)에서도 해제되지 않음. 미보유 자원 해제는 음수 반환 → S18.md:99-100의 IF @v_lockResult<0 THROW 51019가 정상 완료 실행에서도 발동. 잠금은 연결 종료 시에만 풀림 |
| 🟠 | `S18` | common/00-architecture.md:70,79,198 · 00-architecture.md:78 · common/01-step-contract.md:162 | S18.md:3,106-119 | "S18은 성공·차단·기술 실패·데이터 품질 실패·수동 복구 필요 상태 모두에서 실행", 종료 상태를 Succeeded/Blocked/Failed/DataQualityFailure/ManualRecoveryRequired로 구분 | S18.md:3은 'S17 성공 뒤에만 정상 완료 경로로 실행'이라 하여 아키텍처와 모순되고 비성공 경로용 상태 분기가 의사코드에 없음. CATCH는 RunStatus='Failed', ErrorCode='BATCH-FIN-001'을 WHERE RunStatus<>N'Succeeded'로 일괄 덮어씀 | 원 실패 원인(S07의 -9, S16의 BATCH-DQ-001, S17의 BATCH-PUB-001)이 BATCH-FIN-001로 소실. Blocked/DataQualityFailure/ManualRecoveryRequired가 모두 Failed로 격하되어 (a) S02가 자동 재시작 후보로 채택 → **S15 비멱등 단계의 자동 재개 금지 규정 위반**, (b) 상태값으로 구분하는 그림자 자동 정리 정책의 판정 근거도 무의미해짐 |
| 🟡 | `S01` | S01.md:15-18 | S01.md:206-262,297 | BATCH-VAL-002는 "재시작 상태 정합성" 실패에만 부여 | DML 순번 3~6의 인프라 예외(중복키, 제약 위반)에도 COALESCE(@v_currentErrorCode,'BATCH-VAL-002')로 같은 코드를 반환 | 운영자가 인프라 장애를 재시작 정합성 실패로 오분류 |
| 🟡 | `S01` | S01.md:164-169 | S01.md:244-259,264-271 | 동일 RunId+S01의 체크포인트·저널은 단일 확정 행이어야 대조가 성립 | 체크포인트를 존재 확인·MERGE 없이 무조건 INSERT하고 조회 SELECT에 TOP(1)/ORDER BY가 없음. 저널 UPDATE는 StepStatus=N'Running'인 모든 행을 갱신 | 이전 중단으로 미완료 체크포인트·Running 저널이 남아 있으면 중복 행 생성 및 비결정적 변수 대입(또는 PK 중복 예외). S02의 대조 결과가 실행마다 달라질 수 있음 |
| 🟡 | `S03` | prompt-context.md:4420 (S16 Tables·ErrorCodes에 해당 항목 없음) · S16.md:8-11,17-20 | S03.md:20,227-248,269 | S03이 "S16이 소비한다"고 선언한 계약은 S16 문서 안에 실제로 존재해야 함 | S16.md의 대상 테이블 표와 오류 코드 표에는 두 기준선 테이블도 BATCH-SNAP-001도 없고, S16.md 전문에 Watermark/Baseline 문자열이 한 번도 나오지 않음 | 워터마크는 기록만 되고 아무도 읽지 않음. S03이 선언한 원천 변경 탐지·공개 차단 통제가 실행 경로에 존재하지 않음 |
| 🟡 | `S03` | S03.md:230-248 (S03 자신이 제시한 S16용 템플릿) | S03.md:20,269 | "동일 범위에서 후속 변경이 발생했는지 검사"하려면 저장된 워터마크를 현재 CHANGE_TRACKING_CURRENT_VERSION과 비교하는 술어가 있어야 함 | 템플릿의 첫 쿼리는 저장된 워터마크 행을 그대로 SELECT할 뿐 현재 버전과의 비교가 없음. 세 번째 쿼리도 원장 행 비교일 뿐 Change Tracking 비교가 아님 | S03.md:269가 BATCH-SNAP-001 발동 조건으로 든 "Change Tracking 변경 검출"에 대응하는 판정식이 문서 내에 없음 |
| 🟡 | `S03` | S03.md:12 ("원천 기준점과 원장 기준선이 하나의 실행 단위로 함께 확정되어야 하므로 단일 트랜잭션으로 처리") | S03.md:10,50-58,96-103 | 단일 트랜잭션 주장이 성립하려면 워터마크 값도 그 트랜잭션 안에서 획득되어야 함 | Change Tracking 버전은 SourceWatermarkReader가 "고정된 연결 정보"로 4개 DB에서 각각 미리 읽어 파라미터로 전달. 즉 4개 원천 시점 + 기준선 시점의 서로 다른 5개 시점이며 S03 트랜잭션은 이미 확정된 상수를 INSERT할 뿐 | "함께 확정"이 성립하지 않음. 워터마크와 기준선 사이의 원천 변경은 어떤 검사로도 잡히지 않음 |
| 🟡 | `S03` | S04.md:25,93 · S05.md:136 (라이브 TSettleMst 직접 조회로 -9 판정) | S03.md:21 | S03이 "S04와 S05의 지급 완료·확정 정산 보호 조건으로 사용한다"고 선언했으면 S04/S05가 기준선 테이블을 참조해야 함 | S04·S05는 BatchImmutableLedgerBaseline을 전혀 참조하지 않고 라이브 TSettleMst를 매번 다시 조회. 두 문서에 Baseline 문자열이 없음 | 선언된 소비 계약이 허위. 기준선은 S04/S05 보호에 아무 역할을 하지 않음 |
| 🟡 | `S04` | Spec.md:61,248,252,291 | S04.md:26,105-140 | -1은 TPGSettleRate 삭제 실패 한 가지에만 대응하며 테이블별 실패 코드가 -1~-10으로 일대일 구분(−9만 원본 중복) | 그림자 준비 구간을 단일 @v_currentErrorCode=-1로 묶어 5개 테이블 백업 실패 전부를 -1로 반환 | 금액·행 집합 영향 없음. 장애 원인 추적성 저하 |
| 🟡 | `S05` | Spec.md:118-126,245 | S05.md:92-104,111 | INSERT 42개 컬럼 중 CLTOTAL/PGTOTAL/POQINCOME/CLINTCOMM/PGINTEXPCOMM/PGINTREALCOMM/SeperateAmt 7개는 원천 분기가 아니라 삽입 시점 상수 0 | "세 UNION ALL 분기가 동일 순서로 투영할 원장 계약"에 42개를 나열하고 7개 상수도 분기가 투영한다고 지시. 그러나 실제 CTE 3개 분기는 35개만 투영하고 7개는 외부 SELECT에서 채움 | 값 결과는 동일(모두 0)이나 계약 표(42)와 의사 SQL(35)이 어긋나 계약 표대로 구현하면 CTE↔INSERT 정렬이 깨짐 |
| 🟡 | `S05` | Spec.md:79 | S05.md:237-251,501 | 전체 거래 분기의 TTxMst 조회에 INDEX=CIDX_TTxMst_YMD 힌트 | 인덱스 힌트 없음. 제거 사실도 미언급(:501은 NOLOCK 제거만 명시) | 결과값·행 집합 불변, 계획 선택만 달라짐. 의도적 제거 여부 추적 불가 |
| 🟡 | `S07` | Spec.md:401-404,599-603 | S07.md:496-537,897 | 카드 원가 UDF 4종은 "분석 생략(외부 객체)"로 인자 목록 미기술. 리터럴 시그니처를 주는 것은 UF_GET_COMM4CLIENT4PARTIALCANCEL(9인자) 하나뿐 | UF_GET_COMM4CLIENT(8), UF_GET_COMM4CLIENT4INTEREST(6), UF_GET_COMM4PG(5), UF_GET_COMM4PG4INTEREST(4)를 미확정 표시 없이 확정 코드로 서술 | Spec 기준으로 인자 개수·순서 검증 불가. 틀렸을 경우 실행순서 13 전 금액이 달라지는데 문서상 근거가 남지 않음 |
| 🟡 | `S09` | Spec.md:29,34-35 | S09.md:18,63-66,355-358,376-379 | 결과 집합을 반환하지 않으며 실패 정보는 @po_intRetVal로만 전달 | 표는 SettlementScheduleResult가 5개 필드를 반환한다고 하나 실제 코드는 SettlementStepResult를 반환하고 SQL SELECT는 SqlErrorNumber·AffectedRows 컬럼을 만들지 않음(성공 경로는 3개 컬럼) | MarkSucceededAsync(AffectedRows)·MarkFailedAsync(SqlErrorNumber)가 채워질 근거가 문서에 없어 구현 시 임의 해석 발생 |
| 🟡 | `S11` | Spec.md:129 | S11.md:407 | OUTSTATE = IIF(ISNULL(X.CompanySalesType,4)=4, 9, X.OUTSTATE)이며 X.OUTSTATE는 2. 외곽 IIF 래핑이 원본에 존재 | 외곽 IIF가 사라지고 상수 2만 남음 | 현재 원천 필터가 NULL과 4를 배제하므로 실제 저장값은 항상 2로 동일. 원본 방어 분기가 소실되어 필터가 완화될 때 9 처리가 재현되지 않음 |
| 🟡 | `S12` | Spec.md:27,253 | S12.md:374-375,384-385,411-414 | 0은 모든 직접 DML과 두 종속 프로시저가 성공한 경우에만 설정 | 종속 호출 구간에서 SET @v_currentErrorCode=0으로 설정. CATCH는 @v_childLegacyCode<>0일 때만 자식 코드를 쓰고 그 외에는 0을 @po_intRetVal에 넣음 | 자식 SP가 코드를 채우지 못한 채 실행 중단 오류(EXEC 자체 실패, 커밋 실패)가 나면 실패 상황에 성공 코드 0이 설정되어 저널상 판정이 뒤집히고 재시작 스킵 로직이 오염됨 |
| 🟡 | `S12` | Spec.md:137-164,170-201 | S12.md:290-291,331-332 | TSettleByIN 28개, TSettleByOUT 32개 컬럼의 삽입 매핑이 컬럼 단위로 확정 | TSettleByTX(27)·TPartialCancelByTX(28)는 명시적 컬럼 목록을 쓰면서 TSettleByIN·TSettleByOUT은 INSERT INTO ... SELECT로 컬럼 목록 생략(항목 수·순서는 일치) | 현재 스키마에서는 일치하나 컬럼 추가·순서 변경 시 IN/OUT 두 INSERT만 조용히 어긋남. 넷 중 둘만 목록을 갖는 비일관 |
| 🟡 | `S12` | Spec.md:259-262 | S12.md:24-25 vs 377-392,420 | 두 종속 처리는 외부 트랜잭션에 참여하는 단일 실행 경로 | 같은 처리를 C# 컴포넌트로도, T-SQL 헬퍼 프로시저로도 각각 정의하며 어느 쪽이 정본인지 밝히지 않음 | 두 층 모두 구현하거나 한쪽만 구현할 수 있고, C# 층으로 구현하면 T-SQL EXEC 블록의 @v_childLegacyCode 보존·THROW 흐름이 성립하지 않음 |
| 🟡 | `S13` | Spec.md:30,43,171,176,188 | S13.md:52,366 | 출력 코드 집합은 1000(기본), 0, 1001, 1002이며 DELETE/INSERT 직후 @@ERROR만 검사. 그 밖의 위치(커서 선언·열기·FETCH·CLOSE·DEALLOCATE·COMMIT) 오류는 감지 경로가 없어 @po_intRetVal이 기본값 1000으로 남음 | BEGIN CATCH에서 SET @po_intRetVal = @v_currentErrorCode로 일괄 대입. @v_currentErrorCode는 NULL로 초기화되므로 DELETE/INSERT 이외 위치에서 예외가 나면 @po_intRetVal이 NULL이 됨 | 레거시가 1000을 유지하는 경로에서 호출자가 NULL 반환코드를 받음. 코드 집합 {1000} 미보존 |
| 🟡 | `S15` | Spec.md:39,172,179 | S15.md:26-45,174-183 | 동일 기준일 재호출 시에도 커서는 같은 그룹을 다시 만들고 기존 행에 금액을 재누적하며 그 결과 @v_intPostChkAmt1<>@v_intPostChkAmt2가 되어 -3으로 롤백·반환 | batch.BatchMissProcessKey의 PK와 조회 조건이 BatchDate·ClientID·SourceYMD·OutYMDKey·IssueType이며 RunId는 컬럼으로만 보관하고 키·조회 조건에서 제외 | 중복 방지 장치 자체는 단일 정상 실행의 삽입 행을 걸러내지 않음(커서 그룹 키와 업무 키가 1:1 대응). 다만 스코프가 RunId가 아니라 BatchDate이므로 재시작뿐 아니라 새 RunId의 의도적 동일 기준일 재실행까지 업무 DML을 건너뜀. 레거시가 -3을 반환할 상황에서 산출물은 0을 반환. OutYMD가 NULL인 그룹에서는 행 집합 차이로도 발현 |
| 🟡 | `S16` | S16.md:18-19 (두 오류 코드 모두 BatchValidationIssue와 BatchStepJournal에 기록이라고 선언) | S16.md:74-85 | 선언한 대로 두 테이블에 기록 | 선행 체크포인트 실패 경로는 BatchStepJournal만 UPDATE하고 BatchValidationIssue에는 아무것도 남기지 않은 채 RETURN | 해당 실패의 이슈 증적이 남지 않아 추적성이 끊김 |
| 🟡 | `S16` | — (문서 내부) | S16.md:190-193 vs 384 | 통제합 대상의 시간 축 표기 일관 | TSettleMiss.RowCount를 WHERE YMD=@BatchYMD로 집계하지만 레거시는 기존 누적 행의 YMD를 당일로 덮어씀. 이 행 수는 "당일 생성분"이 아니라 "당일에 손댄 누적 행 수" | 통제합 명칭이 실제 의미와 달라 증적 해석이 어긋남 |
| 🟡 | `S17` | raw/prompt-context.md:5196,5205 | S17.md:22,89-96 | 승인 계획대로 공개 시각·공개자·검증 통제합 버전을 기록해야 함(PublishAsync(..., context.OperatorId, ...)) | 공개자 자리에 상수 PublishedByStepCode=N'S17'을 기록하고 OperatorId와 통제합 버전은 Job 산출물 전체에 존재하지 않음 | 공개 주체·검증 근거 버전의 추적성 상실. 어떤 통제합으로 공개했는지 사후 확인 불가 |
| 🟡 | `S17` | (AttemptNo 사용처가 S17.md뿐, task-00-bootstrap.md:31-45 DDL 목록에도 근거 없음) | S17.md:72-78,113-119,175-181,250-256 | 재시도 회차 키를 쓰려면 저널이 회차별 다중 행 모델이어야 하고 다른 단계도 같은 모델을 따라야 함 | S17만 MAX(AttemptNo) 서브쿼리로 최신 회차를 고르는데 S01·S03·S16 등은 RunId+StepCode 단일 행 갱신 모델 | 저널 데이터 모델이 단계마다 상충. S17의 회차 선택이 성립하지 않거나 다른 단계 SQL이 다중 행을 무차별 갱신 |
| 🟡 | `S18` | S01.md:33, S03.md:223 (레거시 SP가 없으므로 LegacyReturnCode는 설정하지 않는다), S16.md:30 | S18.md:150 | 레거시 대응이 없는 배치 제어 단계이므로 BATCH-FIN-001은 ErrorCode/BatchErrorCode에 기록 | "BATCH-FIN-001 발생 시에는 SettlementStepResult.LegacyReturnCode에 해당 코드를 기록" | 레거시 반환코드 필드에 배치 제어 코드가 섞여 레거시 코드 보존 여부를 판정하는 운영 조회(S07.md:107의 J.LegacyReturnCode=-9)와 결과 모델의 의미가 오염됨 |
| ⚪ | `S03` | common/01-step-contract.md:20-21 · S05.md:115 | S03.md:21,159-161 | — | 기준선은 YMD=@pi_strYMD AND OutState IN (1,5) 행을 담는데 해당 YMD의 원장 행은 이 실행의 S05/S06이 비로소 생성. 클린 실행에서 기준선은 구조적으로 공집합이고, 비어 있지 않은 경우는 곧 S04/S05가 -9로 차단하는 경우 | 기준선이 실제로 보호 대상 행을 담는 시나리오가 사실상 존재하지 않음. 상위 결함들을 고칠 때 캡처 범위 재검토 필요 |
| ⚪ | `S03` | — | S03.md:265 | — | 검증 템플릿의 ISNULL(B.OutYMD,'') 좌변은 캡처 조건 M.OutYMD IS NOT NULL에 의해 NULL일 수 없어 죽은 분기 | 표기 잉여 |
| ⚪ | `S05` | Spec.md:155,177,232-239 | S05.md:172-173 | YMD=@pi_strYMD 행이 EXISTS로 확인된 경우에만 DELETE | 선행 존재 검사 없이 무조건 DELETE | 삭제 대상 행 집합 동일(0건 삭제로 귀결) |
| ⚪ | `S06` | Spec.md:29,45,189-190 | S06.md:15-19,80-81 | 반환 코드는 -1(INSERT 실패)과 0뿐 | 보존 코드 집합 {-1,0}에 종속 차단용 -9 추가 | Spec 코드 집합이 부분집합으로 보존됨. -9는 S05 차단 전파용으로 :18에서 분리 서술 |
| ⚪ | `S08` | Spec.md:54 / Spec.md:202 | S08.md:293-335 | 요약 표는 "정산 행은 YMD=@pi_strYMD"라 적었으나 문장 7 상세는 전체 TSettleMst에서 PLTID별 SUM(TxAmt)=0, MAX(ID) 행 갱신으로 YMD 필터 없음 | 상세 서술을 따름. CTE 집계·MAX(ID) 선정·갱신 대상 모두 YMD 필터 없고 YMD 제한은 내부 PLTID 선별에만 | 없음(원본 보존 방향). 요약 표를 따랐다면 대상 행이 좁아졌을 것 |
| ⚪ | `S08` | Spec.md:448-452 | S08.md:87,501-526 | 각 UPDATE 직후 @@ERROR 검사 → ROLLBACK → RETURN. TRY/CATCH와 XACT_ABORT 없음 | XACT_ABORT ON + TRY/CATCH + THROW로 대체하고 CATCH에서 BatchStepJournal에 DML 순번·SQL 오류번호 INSERT | 오류 코드 값·롤백 범위 동일 보존. 쓰기 집합에 저널 추가되나 업무 데이터 변경 없음 |
| ⚪ | `S10` | Spec.md:138,140,214,216 | S10.md:355 | 문장 1의 OutYMD 우변은 스칼라 서브쿼리 (SELECT OutYMD FROM dbo.UIF_SettleYMD(...))이며 정산주기 행이 없으면 OutState=2인 채 OutYMD만 NULL이 될 수 있음 | 동일 호출을 CROSS APPLY로 바꾸고 A.OutYMD = SY.OutYMD로 대입(함수명·인자 2개·순서 동일) | 전제: Spec:140·214가 "호출당 한 행 반환"을 단언하므로 그 전제에서는 동등. 0행 반환이 가능한 경우에만 CROSS APPLY가 해당 행을 갱신 대상에서 제외(그 경우 🟠) |
| ⚪ | `S10` | Spec.md:131,224,243,249,258-265 | S10.md:146-147,377-378,17-24 | 레거시는 INSERT 직후와 부호 반전 UPDATE 직후에 @@ERROR 검사가 없어 두 문장의 실패를 전용 코드로 보존하지 않음 | TRY/CATCH + XACT_ABORT로 감싸고 INSERT 실패에 -2, 부호 반전 UPDATE 실패에 -4 부여 | 반환 코드 집합은 동일하나 레거시가 코드를 남기지 않던 두 지점에서 기존 코드가 새로 발생. 성공 결과에는 영향 없음 |
| ⚪ | `S11` | Spec.md:201-203,299-304 | S11.md:15-21,269-270,425-426 | INSERT 직후와 첫 UPDATE 직후에는 @@ERROR 검사가 없고 -2는 두 번째 UPDATE 직후 오류에만 대응 | DML 순번 2(INSERT)와 3(부호 반전 UPDATE) 실패에도 -2를 대입 | 반환 코드 집합 보존(⊆ 충족). 레거시에서 값이 대입되지 않던 상황에 -2가 관측됨 |
| ⚪ | `S11` | Spec.md:295 | S11.md:212,497 | 성공 경로는 값 없는 RETURN이고 오류 전달 채널은 @po_intRetVal 하나 | RETURN -9 / RETURN @v_currentErrorCode로 프로시저 반환값도 함께 내보내고 C#은 SqlReturnCode로 별도 보존 | 신규 출력 파라미터는 없음. 반환값이라는 추가 채널이 생겼을 뿐 |
| ⚪ | `S12` | Spec.md:37-40 | S12.md:29-32,69,94 | 파라미터는 @pi_strYMD와 @po_intRetVal 둘뿐이며 반환 코드 집합에 -9 없음 | 요청 레코드에 BypassPreCheck를, 코드 표에 -9(상위 실행 컨텍스트의 지급확정 보호 코드)를 둠 | 신규 출력 파라미터 신설은 없음. -9는 "S12 집계 SQL에서 신규 의미로 재할당하지 않는다"고 명시되어 이 문서 범위에서는 인터페이스 훼손 아님. 다만 두 요소의 출처는 S12 문서 안에서 확인 불가 |
| ⚪ | `S13` | Spec.md:59,71,155 | S13.md:35,79-119 | 커서 DISTINCT 대상에 A.OUTSTATE가 포함되고 @v_intOutState에 담기지만 삭제·재집계 필터에는 미사용 | 후보 키(batch.S13SettlementOutKey)에 OutState가 없음 | 커서 원천이 A.OutState=9로 고정되어 DISTINCT 결과 행 집합은 동일. 추적성 관점의 정보 |
| ⚪ | `S14` | Spec.md:143 | S14.md:194-198 | 삽입 원천 조회에 ORDER BY INYMD, CLIENTID, PGNAME, MALLID 존재(단 저장 순서 무보장) | 외부 GROUP BY만 있고 ORDER BY 없음 | Spec 자체가 무보장이라 기술 → 결과 영향 없음. 원문 요소 누락 추적성 |
| ⚪ | `S14` | Spec.md:41,137 | S14.md:252 | 성공 경로에서 0을 포함한 어떤 값도 할당하지 않으므로 호출자가 호출 전 @po_intRetVal을 0으로 초기화해야 함 | 의사코드가 poIntRetVal: null로 전달 | 출력 파라미터 신설 없음, 단계 성공 코드는 별도 0 기록. Spec이 요구한 호출 전 0 초기화 지시만 누락 |
| ⚪ | `S14` | Spec.md:63-65 | S14.md:144-148,166-170,188-192 | 각 원천 분기는 원본 컬럼으로 그룹화하고 SELECT에서 LOWER() 적용 | 내부 분기 GROUP BY가 LOWER(CLIENTID), LOWER(PGNAME), LOWER(MALLID) | 외부에서 소문자 키로 재집계하므로 최종 13개 컬럼 금액은 동일. 내부 그룹 경계만 달라져 LEFTSUMAMT의 NULL 제외 경계에 이론상 영향 가능 |
| ⚪ | `S14` | Spec.md:64-65,82-84 | S14.md:161-163,183-185 | 로그 없는 두 PG 원천의 AHEADSALESCOMM/VT/SETTLEAMT는 각각 상수 0 | CAST(0 AS MONEY)로 명시 캐스팅 | 타입 우선순위상 통상 결과 변화 없음. 원문에 없는 타입 지정 추가 |
| ⚪ | `S15` | Spec.md:170-172 | S15.md:188-196 | @v_intID를 0으로 초기화하고 기존 행 조회 후 @@ROWCOUNT>1이면 MAX(ID)를 다시 선택하며 @v_intID>0일 때 UPDATE 분기 | 조회를 SELECT @v_intID = MAX(ID) 한 문장으로 합치고 분기 조건을 @v_intID IS NOT NULL로 변경 | 0건이면 NULL, 1건·다건 모두 MAX(ID)이므로 결과 동등. ID가 0 또는 음수인 기존 행이 있을 때만 분기가 갈리며 실현 가능성은 사실상 없음 |
| ⚪ | `S15` | Spec.md:160,195 | S15.md:84-113 | 프로시저 진입 시 @@TRANCOUNT<>0이면 TRY 바깥에서 이름 없는 ROLLBACK TRAN 실행 | 진입부 @@TRANCOUNT 검사와 선행 롤백이 없고 XACT_ABORT ON + 자체 BEGIN TRAN으로 시작 | 단계별 독립 트랜잭션으로 대체. 호출자 트랜잭션을 임의 롤백하지 않아 레거시보다 좁게 동작 |
| ⚪ | `S15` | Spec.md:36-40,49 | S15.md:50-56 | 반환 코드 집합은 0·-3·4000이며 출력 파라미터는 @po_intRetVal 하나 | 세 코드를 같은 의미로 보존하고 -1·-9를 상위 호환 코드로 추가하되 자체 DML 오류에는 재할당하지 않는다고 명시 | Spec 코드 집합이 포함되며 의미도 보존. 출력 파라미터 신설 없음 |
| ⚪ | `S17` | raw/prompt-context.md:4647 | S17.md:3,20 | — | 공개 게이트는 소비자 조회 계층의 자발적 준수에만 의존하며 TSettleMst 등은 S05~S15에서 이미 커밋되어 공개 상태와 무관하게 조회 가능 | 미공개 실행 결과가 기존 리포트·인터페이스에는 그대로 보임. 설계상 알려진 한계 |
| ⚪ | `S18` | — | S18.md:18,106-117 | 선언한 @v_currentErrorCode를 오류 기록 경로에서 사용 | 선언만 하고 CATCH는 리터럴 N'BATCH-FIN-001'을 사용 | 기능 영향 없음, 문서 내부 일관성만 저하 |

### 5-1. 전 단계 공통 결함

**B1. `@pi_bypassPreCheck` — 원본에 없는 보호 우회 입력** — 🔴 1 · 🟠 3 · 🟡 2 · ⚪ 1

원본 SP 여럿은 지급 확정 원장(`OutState IN (1,5) AND OutYMD IS NOT NULL`)이 하나라도 있으면 트랜잭션 시작 전 `-9`로 무조건 중단하며 **우회 수단이 없다**. 단계 지시서들은 이 검사를 `IF @pi_bypassPreCheck = 0` 안에 넣어 조건부로 만들었다. 개별 단위만 보면 "정상 흐름에서 도달 불가한 죽은 경로"로 보이지만, **S02가 재시작 모드에서 실행 컨텍스트 전체에 `PiBypassPreCheck = true`를 고정하므로 재개 시작 단계(정의상 미완료 단계)와 그 이후 전부가 `1`을 받는다.** 죽은 경로가 아니다.

| 단계 | 기준값 | 산출물 | 영향 |
|---|---|---|---|
| S02 🔴 | 계약상 @pi_bypassPreCheck=1은 "체크포인트상 이미 성공한 단계의 재실행 방지에만" 쓰고 "실패 단계의 업무 보호 검증을 우회하지 않음". 개별 단계도 완료 체크포인트가 있는 단계에만 1을 준다(S04.md:21, S05.md:76, S10.md:12-13) | S02는 if (decision.Mode == "Restart") 한 번에 실행 컨텍스트 전체를 PiBypassPreCheck=true로 고정. 이 값이 이후 모든 단계 호출에 그대로 전달되므로(S07.md:34, S11.md:506) 재개 시작 단계(정의상 미완료)와 그 이후 모든 단계가 1을 받음. 해당 단계들은 IF @pi_bypassPreCheck=0 안에 -9 보호 검사를 두므로(S05:129, S10:114, S07:100, S11:194, S04:79) 플래그가 1이면 검사가 실행되지 않고 곧바로 DELETE FROM TSettleMst가 진행(S05:172, S10:139) | 레거시는 YMD=@pi_strYMD AND OutState IN (1,5) AND OutYMD IS NOT NULL 행이 하나라도 있으면 트랜잭션 시작 전 -9로 무조건 중단하고 삭제하지 않음. 재시작 경로에서 이 하드 스톱이 통째로 사라져 지급 확정 정산 원장이 삭제·재생성됨 — 대상 행 집합과 정산 금액이 모두 달라짐 |
| S05 🟠 | OutState IN (1,5) AND OutYMD IS NOT NULL 행이 하나라도 있으면 무조건 -9 종료, 우회 수단 없음 | 원본에 없는 입력 @pi_bypassPreCheck 신설. 본문 :76은 "체크포인트 완료 단계의 재호출에는 1로 전달"이라 지시 | 전제: C# 예시는 항상 false를 넘기고 체크포인트 완료 시 조기 반환하므로 현재 코드 경로에서는 도달 불가(죽은 경로). 그러나 :76 지시를 따르는 구현이 나오면 지급 확정 원장이 -9 차단 없이 삭제·재생성됨 |
| S10 🟠 | 지급 완료·확정 행 EXISTS 검사는 무조건 선행되며 존재하면 -9로 무변경 종료(트랜잭션 시작 전 유일한 보호 장치) | 원본에 없는 입력 @pi_bypassPreCheck를 신설하고 가드를 IF @pi_bypassPreCheck=0 AND EXISTS(...)로 조건화 | 전제: 같은 문서 11-13행은 "bypass=1이면 SQL을 아예 재수행하지 않는다"고 못박아 정상 흐름에서는 도달 불가(문서 내부 모순). 다만 의사코드만 보고 구현하면 bypass=1 호출 시 -9 보호 없이 DELETE/INSERT가 진행되어 지급 완료·확정 행이 삭제·재생성될 수 있어 높은 쪽으로 매김 |
| S11 🟠 | 사전 EXISTS(OutState IN (1,5) AND OutYMD IS NOT NULL)에 걸리면 무조건 -9로 즉시 종료. 우회 경로 없음 | @pi_bypassPreCheck=1이면 사전검증 블록 전체를 건너뛰고 곧바로 DELETE·INSERT·UPDATE로 진입. 게다가 :10은 "완료 상태면 SQL을 재실행하지 않고 SkippedSucceeded", :11은 "건너뛰는 경우에 bypass=1을 사용"이라 해 우회 플래그가 DML 실행 경로에서 언제 켜지는지 서로 모순 | 지급 확정(OutState IN (1,5)) 차액정산 행이 남아 있어도 DELETE 조건에 걸리면 그대로 삭제·재생성됨. 레거시가 보호하던 행 집합이 달라짐 |
| S06 🟡 | 파라미터는 @pi_strYMD와 @po_intRetVal 둘뿐이며 실행 조건 분기용 입력 없음 | @pi_bypassPreCheck BIT를 신설하고 "재시작 경로에서만 허용"이라 서술하나, 의사코드 어디에서도 이 변수를 읽지 않음 | 선언만 있고 동작 미정의. 구현자가 S05 게이트 우회로 해석하면 삽입 대상 행 집합이 달라질 수 있음 |
| S09 🟡 | 파라미터는 @pi_strYMD 입력과 @po_intRetVal OUTPUT 둘뿐 | 본문은 2개로 정확히 적었으나 C# ExecuteAsync가 원본에 없는 bool piBypassPreCheck를 세 번째 인자로 받아 저장소 계층까지 전달. 의사코드에는 이 플래그를 소비하는 분기가 전혀 없음 | 전제: SQL 어디에도 연결되지 않아 현재 문서 범위에서는 영향 없음. 구현자가 임의로 사전검증 우회에 배선하면 대상 행 집합이 달라짐(그 경우 🟠) |
| S04 ⚪ | 선행 검증은 무조건 수행되며 해당 정산건 존재 시 -9로 차단(원본 입력은 @pi_strYMD뿐) | 재시작용 입력 @pi_bypassPreCheck 신설, BatchCheckpoint의 S04 Completed 확인 시 선행 검증 생략 | 출력 파라미터 신설 없음. 바이패스 경로는 쓰기가 없어 대상 행 집합 불변 |
| S07 ⚪ | 반환 코드 16종에 -9 없음 | 선행 단계 저널에 -9가 있으면 DML을 시작하지 않고 -9를 반환하는 게이트 신설 | 근거 명시 + Spec 코드 집합의 상위집합이므로 결함 아님. 원본에 없는 반환 경로 추가 사실만 기록 |

**B2. `batch` 제어 테이블의 컬럼명·상태값 어휘 불일치** — 🔴 1 · 🟠 5 · 🟡 3

같은 세 테이블(`BatchRun`·`BatchStepJournal`·`BatchCheckpoint`)에 대해 단계마다 다른 컬럼명과 상태 어휘를 쓴다. 확정 DDL이 번들 어디에도 없어 어느 쪽이 정본인지 판정할 수 없고, 어느 쪽으로 만들어도 반대편 단계가 컴파일되지 않는다.

| 단계 | 기준값 | 산출물 | 영향 |
|---|---|---|---|
| S01 🔴 | S02는 저널 완료를 SJ.ExecutionStatus = N'Completed'로 읽고 체크포인트 Completed와 정확히 일치해야 재시작 허용 | S01은 저널을 컬럼 StepStatus에 값 N'Running'→N'Succeeded'로 기록(컬럼명·값 도메인 모두 상이). 체크포인트만 N'Completed' | S01이 정상 성공한 직후 S02가 "완료 체크포인트는 있는데 저널은 완료 아님"으로 판정 → BATCH-RST-001로 실행 중단. 정합성 게이트가 상시 차단되어 후속 정산 금액이 산출되지 않음 |
| S01 🟠 | S02가 재개 후보로 인정하는 BR.RunStatus는 'Running','Failed','Restarting' | S01이 RunStatus = N'Validating'으로 설정 — S02의 후보 목록에 없는 값 | 신규 실행에서 @pi_resumeRunId가 NULL이면 S02의 @v_resumeRunId가 NULL로 남아 UPDATE … WHERE RunId=NULL이 0건, 저널이 NULL RunId로 기록됨 |
| S01 🟠 | batch.BatchRun의 작업명 컬럼은 BatchJobName | S01은 BR.JobName / JobName = N'POQSettleProc16' 사용 | 같은 신설 테이블에 두 컬럼명이 선언됨. 어느 쪽으로 DDL을 만들어도 반대편 단계가 컴파일 불가 |
| S01 🟠 | 검증 SQL이 주장하는 "S01…S16의 저널 상태가 모두 Succeeded" 및 CanPublish 게이트가 S01이 실제로 남기는 상태를 읽어야 함 | 검증 SQL은 j.Status·j.StartedAt·j.CompletedAt·r.Status를 읽는데 S01은 StepStatus·StartedAtUtc·CompletedAtUtc·RunStatus에 기록 | 검증 SQL이 존재하지 않는 컬럼을 대조 → 필수 단계 검증과 S17 공개 게이트가 정상 실행에서도 오류 또는 CanPublish=0으로 상시 실패 보고 |
| S02 🟠 | 같은 테이블 batch.BatchStepJournal의 완료 상태 컬럼명·값이 단계 간에 하나여야 S02의 저널/체크포인트 대조가 성립 | S01은 컬럼 StepStatus에 값 'Succeeded'를, S02는 컬럼 ExecutionStatus에 값 'Completed'를 쓰며 대조도 SJ.ExecutionStatus=N'Completed'로 함. S03(StepStatus='Completed'), S16(StepStatus='Succeeded'), S15(ExecutionStatus='Failed')까지 어휘가 제각각 | 컬럼명을 맞춘다 해도 값 어휘가 달라 CP.CheckpointStatus='Completed' AND SJ.ExecutionStatus<>'Completed' 조건이 S01·S16이 완료시킨 모든 단계에서 참이 됨 → 모든 재시작이 BATCH-RST-001로 무조건 차단되거나 반대로 대조가 무효화됨. 전제: 세 테이블 DDL이 산출물에 없어 어느 컬럼명이 정본인지 확정 불가 — 표기 문제로 보면 🟡이나 재개 시작점이 달라지는 쪽으로 갈리므로 높은 등급 |
| S17 🟠 | S17의 S16 완료 게이트는 S16이 실제로 기록하는 컬럼·값을 읽어야 함 | S16은 CheckpointStatus='Completed', StepStatus='Succeeded'를 쓰는데 S17은 CheckpointState='Completed', StepState='Completed'를 읽음(컬럼명 2건·상태값 1건 불일치) | S16이 정상 성공해도 @v_s16JournalCompleted=0 → PublicationDependencyNotCompleted → 공개 상시 차단. 단일 DDL로 양쪽을 동시에 만족시킬 수 없음 |
| S18 🟠 | 성공 종료 확정 UPDATE의 WHERE 조건이 선행 단계가 실제로 남기는 상태값과 맞아야 함 | WHERE RunId=@RunId AND RunStatus=N'Publishing' — Publishing을 쓰는 단계가 산출물 전체에 존재하지 않음(유일 등장이 S18.md:48) → @@ROWCOUNT=0 → THROW 51018 | 정상 실행에서도 항상 CATCH로 진입하여 BatchRun이 Failed/BATCH-FIN-001로 기록되고 저널에 S18 Failed가 남음. 성공 실행이 실패로 표기되어 다음 실행의 S02 재시작 판단이 오염됨 |
| S02 🟡 | 동일 테이블 batch.BatchRun의 컬럼명이 단계 간에 일치해야 함 | S01은 JobName, S02는 BatchJobName. 시각 컬럼도 S01 StartedAtUtc/CompletedAtUtc vs S02 StartedAt/CompletedAt, 메시지도 S01 ErrorMessage vs S02 DetailMessage, S02만 CheckpointValue를 추가로 씀 | 같은 3개 테이블에 대해 두 가지 스키마가 문서화되어 그대로 구현하면 컴파일·실행 실패 |
| S03 🟡 | 저널 성공 상태값은 프로젝트 전역에서 동일해야 함 | S03만 SET StepStatus = N'Completed'를 씀(체크포인트의 CheckpointStatus='Completed'와 혼동한 것으로 보임) | 앞 결함(저널 행 미생성)이 고쳐지더라도 S03은 완결성 검증에서 계속 실패로 검출됨 |
| S17 🟡 | CanPublish=1(= BatchValidationIssue에 Error/Critical 없음 + S16 저널 Succeeded)일 때만 Published 전이 | S17은 batch.BatchValidationIssue를 전혀 조회하지 않고 체크포인트/저널 상태만 봄. 게다가 CanPublish SQL은 저널 상태 컬럼을 j.Status로, S16은 StepStatus로, S17은 StepState로 서로 다르게 부름(3중 불일치) | 승인된 공개 게이트 계약이 단계 문서에서 소실. S16 체크포인트가 수동 완료 표시되면 미해결 검증 이슈가 있어도 공개 가능 |
| S18 🟡 | 다른 단계와 동일한 체크포인트 컬럼명·상태값(CheckpointStatus, Completed) 사용 | S18만 체크포인트 컬럼을 CompletionStatus로, 값을 Succeeded로 기록·조회 | 존재하지 않는 컬럼 참조로 S18의 멱등 스킵 판정이 성립하지 않고 S18 체크포인트를 S02의 완료 체인 판정이 인식하지 못함 |

**B3. 제어 행의 생성 지점 부재 — UPDATE만 존재** — 🔴 1 · 🟠 3 · 🟡 2

`INSERT INTO batch.BatchRun`이 번들 전체에 0건이고, S03·S06·S17은 자기 저널·체크포인트 행을 만드는 지점 없이 `UPDATE`만 한다. `@@ROWCOUNT` 검사가 있는 곳은 상시 실패하고, 없는 곳은 0행 갱신을 오류 없이 지나간다.

| 단계 | 기준값 | 산출물 | 영향 |
|---|---|---|---|
| S01 🔴 | "S01은 batch.BatchRun 실행 정보를 생성 또는 검증한다" — 신규 실행의 제어 행을 S01이 생성 | IF NOT EXISTS (… FROM batch.BatchRun …)이면 BATCH-VAL-001로 즉시 실패시키고 이후 UPDATE만 수행. 생성(INSERT/MERGE)이 없음 | 전제: 번들 전체 grep 결과 INSERT INTO batch.BatchRun이 어느 단계·부트스트랩에도 없고 task-00-bootstrap.md:40은 테이블 DDL만 지시 — 이 전제가 맞으면 최초 실행이 항상 BATCH-VAL-001로 차단되어 S04 이후 정산 원장이 한 건도 생성되지 않음. 호스트 계층이 문서 밖에서 행을 만든다면 🟡로 낮아짐 |
| S02 🟠 | New 모드에서도 제어 행이 존재해야 UPDATE와 저널·체크포인트 기록이 의미를 가짐 | 잡 산출물 전체에 INSERT INTO batch.BatchRun이 단 한 곳도 없음(agent/·docs/ 전수 검색 0건). S02는 WHERE RunId=@v_resumeRunId로 UPDATE만 하며 New 모드에서는 @v_resumeRunId가 NULL이라 UPDATE가 0행을 갱신하고도 오류 없이 지나가고 이어지는 저널·체크포인트 INSERT는 RunId=NULL로 기록됨 | 생성 지점 없이 UPDATE만 하는 제어 행. 신규 실행에서 RunStatus/ResumeFromStepCode/LockAcquiredAt이 남지 않고 고아 저널·체크포인트가 쌓임. 다음 재시작 시 이 행들이 완료 판정 대상에 섞여 재개 시작 단계가 달라질 수 있음 |
| S03 🟠 | UPDATE batch.BatchStepJournal … WHERE StepCode='S03'이 성립하려면 그 행의 생성 지점이 있어야 함 | S03 의사코드는 성공/실패 양쪽 모두 UPDATE만 하고 INSERT가 없음. 산출물 전체에서 StepCode='S03' 저널 행을 만드는 곳이 없음(S02.md:100의 목록은 재개 순서 판정용 VALUES이지 저널 삽입이 아님) | 두 UPDATE 모두 0행 갱신. S03.md:223이 규정한 오류 코드 전달 경로가 성립하지 않아 코드가 유실됨. 또한 integrity-sql.md:40-47이 S01–S16 전 단계의 저널 행을 요구하므로 S03이 항상 Missing으로 검출 → S16 BATCH-DQ-001 → S17 공개 차단 |
| S17 🟠 | 각 단계가 자기 BatchStepJournal·BatchCheckpoint 행을 INSERT한 뒤 상태를 전이하듯 S17도 자기 제어 행의 생성 지점을 가져야 함 | S17은 두 행을 INSERT하는 곳이 Job 전체 어디에도 없고 UPDATE … IF @@ROWCOUNT<>1 THROW만 수행 | 정상 실행에서도 ROWCOUNT=0 → PublicationJournalMissing/PublicationCheckpointTransitionRejected → 공개 상시 실패(BATCH-PUB-001). 공개되는 실행 집합이 항상 공집합 |
| S06 🟡 | 원본에는 중복 삽입 방지 장치가 없음 | :12는 "삽입과 체크포인트 완료 기록을 동일 트랜잭션에 포함해 재삽입을 방지"라 단언하나 :192는 UPDATE batch.BatchCheckpoint뿐이고 S06 체크포인트 행을 생성하는 지점이 문서 어디에도 없음 | 행이 선재하지 않으면 UPDATE가 0행에 적용되어 완료 표시가 남지 않고 :12가 약속한 재삽입 방지가 성립하지 않음 |
| S17 🟡 | UPDATE의 WHERE가 참조하는 상태값에는 생성 지점이 있어야 함 | 전이 허용 집합 ('Pending','Retrying','Unpublished') 중 어느 값도 어디서도 설정되지 않으며 유일한 생산 지점은 실패 CATCH의 PublicationState='Pending' | 초기값 미정의 시 정상 경로 UPDATE가 0행 → PublicationStateTransitionRejected. Retrying/Unpublished는 도달 불가 사문 |

**B4. 검증 SQL이 레거시가 보장하지 않는 불변식을 주장** — 🔴 1 · 🟠 8 · 🟡 2

정합성 검증식이 레거시가 **정상적으로 만드는 상태**를 위반으로 판정한다. 이 결과는 S16으로 모이고, S16은 이슈가 1건이라도 있으면 체크포인트를 완료로 올리지 않아 **S17 공개가 상시 차단**된다.

| 단계 | 기준값 | 산출물 | 영향 |
|---|---|---|---|
| S16 🔴 | 원장 합계와 집계 합계를 각각 독립 집계한 뒤 비교(부질의 2개 또는 CTE 비교) | FROM TSettleMst AS M CROSS JOIN TSettleByTX AS T … HAVING ISNULL(SUM(M.TXAMT),0) <> ISNULL(SUM(T.TXAMT),0) — 카티전 곱이므로 좌변=\|T\|×SUM_M, 우변=\|M\|×SUM_T가 되어 \|M\|≠\|T\|인 정상 데이터에서 항상 불일치 | 정상 실행이 매번 BATCH-DQ-001로 실패해 S17이 상시 차단. 동시에 BatchValidationIssue.ExpectedValue/ActualValue에 카티전 배수만큼 부풀려진 틀린 금액이 증적으로 남음. 수억 행 카티전 곱이라 실행 자체가 불가능할 개연성도 있음 |
| S13 🟠 | 재집계 삽입의 GROUP BY는 CompanySalesType·ExtraSettleFlag·SettleCurrency의 원본 컬럼값을 사용하므로 NULL 그룹과 치환값(4/9) 그룹, NULL 통화와 빈 문자열 통화는 서로 다른 행으로 정상 생성됨(필터에서만 ISNULL 치환이 동일 취급) | 정합성 검증 SQL의 LEFT JOIN이 ISNULL(T.SettleCurrency,'')=ISNULL(E.SettleCurrency,''), ISNULL(T.CompanySalesType,4)=ISNULL(E.CompanySalesType,4), ISNULL(T.ExtraSettleFlag,9)=ISNULL(E.ExtraSettleFlag,9)로 치환 조인 | 레거시가 정상적으로 만드는 NULL/4(및 NULL/9, NULL/빈문자열) 분리 행이 교차 매칭되어 금액이 어긋난 것으로 보이고 정상 상태를 위반으로 오판. 운영자가 이 오판으로 복구를 실행하면 실제 데이터가 변경됨 |
| S14 🟠 | LEFTSUMAMT=ISNULL(SUM(COLLECTAMT+PGCOMM+PGVT),0)이며 세 값 중 하나가 NULL인 중간 행은 덧셈이 NULL이 되어 SUM에서 제외. 반면 개별 컬럼은 각각 ISNULL(SUM(x),0)이라 같은 행의 비-NULL 항을 포함. 즉 LEFTSUMAMT ≠ COLLECTAMT+PGCOMM+PGVT는 레거시가 정의한 정상 결과 | 정합성 검증 SQL이 T.LEFTSUMAMT <> T.COLLECTAMT+T.PGCOMM+T.PGVT를 위반으로 판정하고 적중 행을 S16 데이터 품질 이슈로 전달하며 "운영자 승인 복구 시 그림자 복원 절차를 사용한다"고 지시 | 레거시와 동일하게 산출된 정상 행이 결함으로 오탐. 전제: 오탐 후 운영자가 그림자 복원을 승인하면 TStatPGCollect의 해당 INYMD 행 집합이 재생성 이전 값으로 되돌아감(→🟠). 보고 단계에서 그치면 🟡. 같은 SQL의 RIGHTSUMAMT 조건은 구성상 항상 성립 |
| S16 🟠 | 레거시가 보장하는 관계만 불변식으로 검증 | WHERE INYMD=@BatchYMD AND ISNULL(LEFTSUMAMT,0) <> ISNULL(RIGHTSUMAMT,0)를 위반으로 기록 | 레거시는 두 계를 서로 다른 식으로 독립 산출. 우변에는 OUTYMD<@pi_strYMD AND OUTSTATE=1 행의 외상매출(CLCOMM+CLETC+CLINTCOMM, CLVT)과 선급금(TXAMT-CLTOTAL)이 예수금과 별도로 추가 가산되므로 선지급/외상 행이 1건이라도 있으면 좌·우변이 원래 다름. TTArs/TBArs 분기는 좌변이 CLCOLLECTAMT+PGCOMM, 우변이 CLRATETOTXAMT로 아예 다른 컬럼 |
| S16 🟠 | 레거시가 보장하는 관계만 불변식으로 검증 | OutState=2 AND NULLIF(OutYMD,'') IS NULL을 BATCH-DQ-SCHEDULE-001 위반으로 기록 | S09 환불건 처리는 UF_GET_OUTYMD4REFUND 결과가 NULL/빈 문자열이면 기존 OutYMD를 그대로 두고 OutState만 2로 설정. S10·S11은 UIF_SettleYMD가 NULL을 반환해도 "별도로 검증하지 않고" OutState=2를 설정. 레거시가 정상적으로 만드는 상태를 위반으로 판정해 S17 차단 |
| S16 🟠 | 최종 상태의 TSettleByOUT 구성 범위와 일치하는 비교식 | "지급 집계 = TSettleMst의 YMD=@BatchYMD AND OUTSTATE IN (2,9) ↔ TSettleByOUT" 등식 | TSettleByOUT은 S12 직접 INSERT 이후 두 번 더 재구축됨. AcqManual은 (OutYMD, ClientID, PGName)만으로 삭제·재집계하며 커서 선정의 OutState IN (2,9)를 재집계 쿼리에 다시 적용하지 않고 YMD 필터도 없음. S13 SUMMARY_ETC도 집계 조회에 OutState 필터가 없음. 따라서 YMD=@BatchYMD이면서 OutState∉(2,9)인 행이 정상적으로 존재해 등식이 깨짐 → S17 차단 |
| S17 🟠 | S16이 정상 실행에서 위반을 보고하지 않아야 S17의 차단 조건이 의미를 가짐 | S16의 LedgerSummaryMismatch 검증이 TSettleMst CROSS JOIN TSettleByTX 뒤 HAVING SUM(M.TXAMT)<>SUM(T.TXAMT) — 카티전 곱으로 양변이 각각 상대 건수배가 되어 사실상 항상 불일치 | S16 저널이 항상 Failed, 체크포인트 미완료 → S17이 상시 BATCH-PUB-001로 차단(S12·S14·S09 검증 SQL 오탐과 동일 유형) |
| S04 🟡 | 변경 범위는 기준일(YMD=@pi_strYMD) 행에 한정 | 검증 SQL 4번 주석은 "기준일 외 행이 변경되지 않았는지 확인"이라 선언하나 실제 쿼리는 5개 테이블 모두 WHERE YMD=@pi_strYMD 행 수만 셈 | 선언한 검증이 수행되지 않음. CATCH의 전체 DELETE·복원이 기준일 외 행을 건드렸는지 탐지 불가 |
| S09 🟡 | 문장 8은 OutState=2로 설정하되 UF_GET_OUTYMD4REFUND 결과가 NULL/빈 문자열이면 기존 OutYMD 유지(기존값이 NULL이면 NULL로 남음) | 정합성 검증 SQL 4번이 OutState=2 AND ISNULL(OutYMD,'')='' 행을 이상 건으로 집계 | 레거시가 정상적으로 만드는 상태를 결함으로 보고 → 검증 단계가 정상 실행을 실패로 판정할 수 있음 |
| S09 🟡 | 문장 3의 원복은 TPGCMRate·TPGCollectPeriodMst 조인과 CollectFlag=1을 만족하는 행에만, 문장 5의 회수 초기화는 UseState=3 AND ISNULL(InYMD,'')<>'' 행에만 적용 | 검증 SQL 1번은 조인·CollectFlag 제한 없이 InState=1 AND ISNULL(InYMD,'')=''를 위반으로 봄. 검증 SQL 3번은 UseState=3 전 행에 InState<>0 OR InYMD IS NOT NULL을 위반으로 보나 문장 5는 InYMD가 빈 문자열인 행을 갱신 대상에서 제외 | 레거시가 보장하지 않는 불변식을 검증식이 주장. 정상 결과에도 위반 행이 출력되어 이관 검증 신뢰도 저하 |
| S12 🟡 | 두 종속 SP 호출이 TSettleByOUT 및 네 집계 테이블을 추가 재구축하므로 커밋 시점 집계 내용은 TSettleMst 원천 집계와 같지 않음 | 커밋 직후 검증 SQL이 TSettleMst 원천 집계와 TSettleByTX를 FULL OUTER JOIN으로 비교. 게다가 TSettleByTX에 YMD=@pi_strYMD 필터가 없어 전 기간 행이 비교 대상 | 정상 실행에서도 타 거래일 전량과 차액정산 보정 행이 모두 MismatchCount로 계상되어 검증이 항상 실패. S16 통제합으로 전달되어 배치가 상시 경보 상태가 됨 |
| S15 🟡 | 금액 대조는 커서가 반환한 현재 @v_strClientID·@v_strOutYMD 한 건에 대해서만 수행. 커서 원천이 A.YMD=@pi_strYMD로 한정되므로 대조 대상 조합도 그 기준일이 만든 조합에 한정 | 검증 SQL의 SourceAmount/MissAmount CTE에 기준일·OutYMD 범위 필터가 전혀 없고 WHERE A.OutState=2 AND ISNULL(B.TaxFGBill,2)=1만 적용한 뒤 전체 이력을 ClientID·OutYMD로 그룹화해 FULL OUTER JOIN | 레거시가 이번 배치에서 손대지 않은 (ClientID, OutYMD) 조합(아직 후취정산이 안 된 지급예정일, 프로시저 도입 이전 구간, IssueType이 15가 아닌 이력만 있는 조합)이 모두 불일치로 잡힘. 정상 상태를 위반으로 판정하는 오탐 |
| S16 🟡 | 선언한 검증 영역에 대응하는 의사코드 존재, 그리고 레거시 누적 의미와 정합 | 오류 코드 표와 검증 범위 표는 "후취정산 키 무결성 오류"와 "누적 일치"를 선언하지만 의사코드에는 TSettleMiss 관련 검증문이 전혀 없고 통제합 행 수 집계만 있음 | 문서 내부 불일치. 또한 TSettleMiss는 여러 실행일에 걸쳐 누적되고 기존 행 YMD가 당일로 덮어써지므로 선언대로 @BatchYMD 단일 범위와 "누적 일치"를 구현하면 오탐이 하나 더 추가됨 |

**B5. `NOLOCK` 전면 제거 → `SNAPSHOT` 격리 일괄 대체** — ⚪ 7

원본 SP 전반의 `WITH(NOLOCK)`을 제거하고 S03 기준점의 `SNAPSHOT`으로 대체했다. 더티 리드를 없애는 방향이라 개별 단계에서는 ⚪지만, 근거지인 S03에 `NOLOCK`이라는 단어조차 없고 결과 변경 가능성·회귀 대조 방법이 어디에도 없다(그 자체는 S03의 🟡로 계상).

| 단계 | 기준값 | 산출물 | 영향 |
|---|---|---|---|
| S12 🟠 | 직접 집계 원천 조회에 WITH(NOLOCK) 사용, 외부 프로시저에 XACT_ABORT 없음 | XACT_ABORT ON + SNAPSHOT 격리로 대체하고 NOLOCK 제거. 변경 사실·근거·레거시 대비 차이 가능성 서술 없음 | 동시 쓰기 환경에서 집계 대상 행 집합이 달라질 수 있음. SNAPSHOT은 ALLOW_SNAPSHOT_ISOLATION 활성화를 요구하는데 그 선행 조건도 미기재 |
| S03 🟡 | 원본 SP 전반의 WITH(NOLOCK)을 SNAPSHOT 기준점으로 대체하는 결정의 근거지인 S03에, 그 대체가 읽히는 행 집합과 결과를 바꿀 수 있다는 사실이 기술되어야 함 | S03.md에는 NOLOCK이라는 단어 자체가 없음. 결과 변경 가능성·회귀 대조 방법 언급도 없음 | 레거시 대비 결과 차이의 최대 원인이 어디에도 기록되지 않아, 이행 후 금액 차이 발생 시 원인 추적 근거가 없음 |
| S04 ⚪ | 선행 검사와 모든 원천 조회에 WITH(NOLOCK) | SNAPSHOT 격리로 대체, NOLOCK 전면 제거 | 의도적 이행 결정. 극단적 경합에서 선정 행이 달라질 수 있음 |
| S06 ⚪ | 두 원천 모두 WITH(NOLOCK) | NOLOCK 제거 후 SNAPSHOT 격리 | 명시적 의도 변경. Spec이 지적한 위험 제거 방향 |
| S08 ⚪ | 원천과 자체 조인에 NOLOCK 광범위 사용 | 전면 제거 + S03 기준점과 동일 세션의 SNAPSHOT 격리 | 의도된 이행 결정. 단일 배치 실행 전제에서는 무영향 |
| S09 ⚪ | UF_GET_OUTYMD4REFUND는 갱신 대상과 동일한 TSettleMst를 NOLOCK으로 다시 조회해 OutState=2 행의 MIN(OutYMD)를 찾음 | NOLOCK 전면 제거 + SNAPSHOT 격리 | 문장 10의 UDF가 트랜잭션 시작 시점 스냅샷만 보게 되어 동시 커밋된 OutState=2 행이 후보에서 빠질 수 있음(S09 병렬 실행 금지로 실무 영향 제한적) |
| S13 ⚪ | 커서 자기조인 A/B와 재집계 조회 모두 WITH(NOLOCK) | SNAPSHOT 격리로 대체 | 더티 리드 제거로 후보 집합이 미세하게 달라질 수 있으나 방향은 엄격화 |
| S14 ⚪ | 세 원천 조회 모두 WITH(NOLOCK)이며 더티 리드·비반복 읽기·행 누락/중복 가능성이 명시 | SET TRANSACTION ISOLATION LEVEL SNAPSHOT으로 대체, NOLOCK 힌트 없음 | 필터·조인 조건은 동일하나 원천 읽기 시점 의미가 달라짐. 더티 리드를 제거하는 방향이며 커밋된 데이터만 읽으므로 집계 결과가 레거시보다 좁아질 수 있음. 크로스 DB에 스냅샷 격리 활성화가 전제 |

**B6. 그림자 백업·복구 장치가 작동하지 않음** — 🔴 1 · 🟠 4 · 🟡 1

다섯 단계가 각기 다른 이유로 복구 불능이다 — 트랜잭션 안에서 만든 `SELECT INTO` 그림자, `EXEC()` 동적 배치의 변수 스코프, `WHERE` 없는 전량 복원, 치환 조인에 의한 중복 백업, 그리고 정리 주체 부재.

| 단계 | 기준값 | 산출물 | 영향 |
|---|---|---|---|
| S04 🔴 | 오류 경로는 ROLLBACK TRAN → 출력 파라미터 설정 → RETURN이 전부. 롤백만으로 5개 이력 테이블 기준일 자료가 원상 복구되며 실패 시 추가 쓰기 없음 | 그림자 백업(SELECT INTO batch_shadow.*)을 BEGIN TRAN 이후에 생성하고, CATCH에서 ROLLBACK 뒤에 5개 대상 테이블의 YMD=@pi_strYMD 행을 다시 DELETE한 다음 그림자에서 복원 | SELECT INTO 그림자는 롤백과 함께 소멸 → 롤백으로 이미 복원된 기준일 행을 CATCH의 DELETE(자동 커밋)가 다시 지우고 복원 INSERT는 객체 없음 오류로 실패. 실패 1회에 기준일 수수료율 5개 테이블이 비어 S05 정산 원장의 수수료·정산금액이 달라짐 |
| S11 🟠 | DELETE 대상 범위는 @pi_strYMD 기준으로 결정되며 이 범위 산정이 실패하면 안 됨(실패 시 -1 롤백) | 그림자 백업이 EXEC(N'INSERT INTO ' + @v_shadowTableName + N' SELECT A.* … WHERE A.ProcYMD = @pi_strYMD …') 형태. EXEC() 동적 배치는 바깥 배치의 @pi_strYMD를 볼 수 없어 스칼라 변수 미선언 오류. 같은 EXEC 안의 SELECT TOP (0) * INTO도 IDENTITY 속성을 복제하므로 SELECT A.*로 ID를 넣으려면 IDENTITY_INSERT가 필요한데 복구 프로시저에만 언급 | 의사코드대로 이행하면 DML 1 진입 전 CATCH로 빠져 -1을 반환하고 @v_shadowCaptured=0이라 복구도 돌지 않음. 기준일의 차액정산 행이 전혀 생성되지 않음 |
| S12 🟠 | 처리 단위는 @pi_strYMD와 일치하는 거래일 YMD이며 네 테이블의 삭제·삽입은 모두 해당 거래일(및 TSettleByOUT의 OUTSTATE IN (2,9))로 한정 | 복구 절차가 DELETE FROM ...TSettleByTX; 등 네 테이블을 WHERE 절 없이 전량 삭제한 뒤, 실행 시작 시점의 전체 스냅샷 그림자(S12.md:144-166, 역시 WHERE 없는 SELECT * INTO)를 통째로 재삽입 | 복구가 발동하면 당일 외 거래일 행과 S12 커밋 이후 다른 경로가 반영한 변경까지 실행 시작 시점으로 되돌아감. 레거시에 없는 전역 행 집합 변경 경로 |
| S13 🟠 | 커서 후보는 원본값 조합에 DISTINCT를 적용하므로 NULL 후보와 4/9 후보가 각각 별개 커서 행으로 존재할 수 있고, 각 후보의 삭제·재집계 필터는 ISNULL 치환 비교(레거시는 커서를 순차 처리하므로 겹치는 후보끼리 뒤 반복이 앞 결과를 덮어써 최종 행 집합이 1벌로 수렴) | 그림자 백업 INSERT와 검증용 ExpectedSummary CTE는 대상 테이블/원장을 batch.S13SettlementOutKey와 집합 조인하면서 같은 치환 비교를 사용 | 원본 1행이 NULL 키와 4(또는 9) 키 두 개에 동시 매칭되어 그림자에 같은 행이 중복 백업되고(복구 시 TSettleByOUT에 중복 재삽입 → 행 집합·집계 금액 변동), ExpectedSummary의 COUNT/SUM은 배수로 팽창해 정상 상태를 위반으로 오판 |
| S13 🟡 | 입력은 @pi_strYMD만 존재 | 그림자 백업을 EXEC(N'INSERT INTO ' + @v_shadowTableName + N' … ON K.RunId = @RunId …')로 조립 | EXEC 동적 배치는 별도 스코프라 로컬 변수 @RunId를 볼 수 없어 실행 시 오류. 의사코드대로 구현하면 그림자 백업이 만들어지지 않아 복구 절차의 전제가 무너짐(sp_executesql 파라미터화 필요) |
| S18 🟡 | 실행별 batch_shadow.<Table>_<RunId>_<StepCode>를 지연 삭제할 책임 주체와 트리거가 어딘가에 명시되어야 함 | S18은 그림자를 전혀 다루지 않고 아키텍처 구성요소 표에도 정리 담당 구성요소가 등록되어 있지 않음. "24시간 후 자동 삭제"를 누가 언제 실행하는지 정의한 문서가 없음 | 실행마다 최대 13개 그림자 테이블(S04 5, S05/S10/S11 각 1, S12 4, S13 1, S14 1)이 생성되지만 삭제 주체가 없어 batch_shadow에 무한 누적. 전제: 24시간 정책의 실행 주체가 산출물 밖 운영 절차에 있다면 ⚪ |
| S10 ⚪ | 쓰기 대상은 TSettleMst 단일이며 임시 테이블 미사용 | 동일 대상 범위를 batch_shadow.TSettleMst_<RunId>_S10에 영속 백업하는 그림자 테이블 신설 | 업무 테이블 변경 집합은 동일. 그림자는 복구용 부가 산출물 |

**B7. `THROW`로 예외 재전파 — OUTPUT 경로 유실** — 🟡 4 · ⚪ 1

레거시는 실패해도 정상 반환하며 결과를 `@po_intRetVal`로만 전달한다. 단계들은 코드를 설정한 직후 `THROW`한다. 예외로 종료하면 OUTPUT 파라미터가 호출자에게 채워지지 않아 레거시 코드가 유실될 수 있다.

| 단계 | 기준값 | 산출물 | 영향 |
|---|---|---|---|
| S07 🟡 | 실패 시 @po_intRetVal에 음수 설정 후 RETURN. 성공·실패 판정은 OUTPUT 기준 | SET @po_intRetVal 직후 THROW로 예외를 재전파 | 예외 전파 시 OUTPUT 파라미터 값이 호출자에게 채워지지 않아 16종 레거시 코드가 유실될 수 있음 |
| S09 🟡 | 각 UPDATE 실패 시 롤백 후 단계별 음수 코드를 @po_intRetVal에 설정해 호출자에게 전달 | CATCH가 Succeeded=0, LegacyReturnCode 결과 집합을 SELECT한 직후 THROW. 반면 C# 흐름은 result.Succeeded==false 분기로 MarkFailedAsync를 호출하도록 되어 있고 예외를 결과 객체로 환원하는 경로가 문서에 없음 | 레거시 코드 -1~-17이 저널에 남는 경로가 문서 내부에서 모순. 예외가 전파되면 단계별 코드가 유실될 수 있음 |
| S12 🟡 | 실패 시 롤백 후 @po_intRetVal에 -1~-8(또는 종속 SP 값)을 담아 정상 종료하며 업무 결과는 오직 출력 파라미터로 전달 | CATCH에서 @po_intRetVal 설정 뒤 THROW로 예외 재발생. @v_legacyErrorMessage를 CONCAT으로 조립하지만 어디에도 기록·반환하지 않음(선언 후 미사용) | 예외 종료 시 OUTPUT 값이 전달되지 않아 -1~-8 및 종속 SP 코드(4000~4008)가 SqlException으로 대체될 수 있음. BatchStepJournal 기록 경로도 SQL에 미정의이며 같은 트랜잭션에서 기록하면 롤백으로 소실 |
| S15 🟡 | CATCH는 활성 트랜잭션을 롤백하고 @po_intRetVal=4000을 설정한 뒤 정상 반환하며 호출자는 출력 파라미터로 판정 | SET @po_intRetVal=4000 직후 THROW로 예외를 상위에 재전파 | 출력 파라미터 경로가 아닌 예외 경로로 결과가 전달됨. 통합 계층이 저널의 LegacyReturnCode를 읽는 설계라 금액·행 집합 영향은 없음 |

**B8. 선언만 있고 소비되지 않는 입력 · 만들어지지 않는 출력** — 🟡 4

표에 선언한 입력을 의사코드가 한 번도 읽지 않거나, 선언한 반환 필드를 SQL이 만들지 않는다. 구현자가 임의로 배선하게 된다.

| 단계 | 기준값 | 산출물 | 영향 |
|---|---|---|---|
| S01 🟡 | @pi_bypassPreCheck=1은 "이미 완료된 S01을 건너뛰기 위한 제어값"으로 동작해야 함 | SQL 템플릿에 해당 파라미터의 선언·참조가 전혀 없고 C# 의사코드는 값에 관계없이 동일하게 건너뛰며 로그 문자열만 바꿈 | 선언만 있고 의사코드가 읽지 않는 입력 |
| S01 🟡 | S01은 ProcessYmd·ReprocessFromYmd·ReprocessToYmd를 주요 사용 항목으로 갖고 "날짜 역할"을 검증 | 단일 batchDate / @pi_strYMD만 받아 8자리 형식과 BatchRun 일치만 확인. 재처리 범위 3종을 읽지도 검증하지도 않음 | 검증되지 않은 재처리 범위가 S10·S16으로 그대로 전달됨 |
| S01 🟡 | 표 9행은 batch.BatchRun을 "검증" 대상으로만 선언, 33행은 반환에 Status·StepCode·StartedAtUtc·CompletedAtUtc 포함 | SQL은 batch.BatchRun을 UPDATE하고 결과 집합은 Succeeded/WasSkipped/BatchErrorCode/ErrorMessage 4개만 반환. 건너뛰기 성공 경로가 정상 메시지를 ErrorMessage 컬럼에 실음 | 의사코드가 만들지 않는 출력 컬럼 + 선언 역할과 실제 쓰기 불일치 |
| S02 🟡 | 단계 입력 파라미터는 한 이름으로 선언·소비되어야 함 | 의사코드는 @pi_resumeRunId를 입력으로 쓰지만 같은 문서의 검증 SQL은 선언된 적 없는 @pi_runId를 씀. 두 이름 모두 파라미터 표로 정의되어 있지 않으며 @pi_strYMD·@po_resultCode·@po_resultMessage도 선언 없이 사용 | 입력 계약이 표로 고정되지 않아 검증 SQL이 실제 실행 컨텍스트와 다른 RunId를 겨눌 수 있음 |
| S17 🟡 | 선언한 입력은 의사코드에서 소비되어야 함 | SettlementBatchContext.BatchDate, BypassPreCheck, S16 검증 결과 셋 다 본문에서 한 번도 참조되지 않음. @RunId는 DECLARE 없이 사용 | S17.md:6이 주장하는 @pi_bypassPreCheck 동작은 코드 근거가 없음 |

## 6. 이 감사가 보증하지 않는 것

### 6-1. 실행 대조를 하지 않았다

**이 감사는 문서 대조이며, 어떤 SQL도 실행하지 않았다.** 레거시 SP와 이관 대상을 같은 데이터에 돌려 결과를 비교한 회귀 검증이 아니다. "🔴 결과 금액이 달라짐"은 **문서를 그대로 구현했을 때 금액이 달라진다**는 판정이지, 실측된 금액 차이가 아니다. 반대로 이 감사가 통과시킨 항목이 실행에서 일치한다는 보장도 없다.

### 6-2. 축별로 보지 않은 것

**축 A** — 파서가 수집한 항목은 `StaticAnalysis`를 진실의 원천으로 삼았고 DDL에서 손으로 다시 뽑지 않았다. 파서가 수집하지 않는 항목(인라인 TVF 호출, `OUTPUT` 방향, 오류 코드 집합, 분기 조건식, 트랜잭션 경계, 주석 블록)만 DDL 원문으로 채웠다. 따라서 **파서 자체의 오수집은 이 축이 잡지 못한다** — 실제로 `INS_EXTRA4PLCARD`에서 별칭 스코프 오탐이 발견됐는데, 그것은 Spec이 정정해 둔 덕에 드러났을 뿐이다.

**축 B** — 기준값은 `Spec.md`이지 원본 DDL이 아니다. Spec 자체가 원본과 어긋난 경우는 축 A가 덮는다. 두 축을 나란히 읽어야 전체가 덮이며, **한 축만으로는 "원본 → 단계 지시서" 경로 전체를 보증하지 않는다.**

### 6-3. 기준값이 없어 판정을 보류한 것

| 무엇 | 왜 | 어느 단위 |
|---|---|---|
| 카드 원가 UDF 4종(`UF_GET_COMM4CLIENT`, `..._4INTEREST`, `UF_GET_COMM4PG`, `..._4INTEREST`)의 인자 개수·순서 | Spec이 "분석 생략(외부 객체)"로 처리해 기준값이 없다. 단계 지시서는 미확정 표시 없이 확정 코드로 서술했다 | S07 |
| `dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA`의 집계 범위 | 이 SP의 Spec이 `output/Procedures/` 아래에 없다. S16의 일반 거래·회수 집계 등식이 레거시 불변식인지 확정하지 못해 결함에 계상하지 않았다 | S16 |
| `PaymentDB.dbo.TExtraSettleIn.ExtraSettleYMD`의 NULL 허용 여부 | 스키마 근거가 없어 `InState=1 → InYMD 필수` 검증이 오탐인지 확정하지 못했다 | S16 |
| 실행순서 13의 파생테이블 X 컬럼 표현식, 실행순서 17의 `CommMethod=1` 부가세 식 | Spec이 SET 우변만 `X.*`로 기술하고 X의 정의를 기술하지 않아 산식 대조가 불가능하다 | S07 |
| 각 SP가 호출하는 UDF·TVF의 내부 로직 | 해당 함수 DDL이 단위의 대상 파일에 없다. 호출 인자와 반환값 사용 방식만 대조했다 | 축 A 다수 |

### 6-4. 확정 DDL이 없어 정오를 가리지 못한 것

`batch.BatchRun`, `batch.BatchStepJournal`, `batch.BatchCheckpoint`, `batch.BatchSourceWatermark`, `batch.BatchImmutableLedgerBaseline`의 **컬럼 정의가 번들 어디에도 없다.** `task-00-bootstrap.md`는 객체 이름만 나열하고 정의를 단계 문서에 위임하는데, 단계마다 다른 컬럼명·상태 어휘를 쓴다(§5-1의 B2). **어느 쪽이 정본인지는 판정하지 않았고, 불일치의 존재만 확정했다.** DDL이 확정되면 B2의 각 항목이 "어느 단계를 고쳐야 하는가"로 바뀐다.

`TPGProperty`의 기본키가 `SeqNo`이고 `PGName` 유일성이 보장되지 않는다는 Spec의 주장도 스키마를 열람하지 않아 전제의 사실 여부를 보류했다(`INS_EXTRA4PLCARD`).

### 6-5. 등급 판정에 깔린 전제

여러 결함이 배포 구성에 따라 등급이 갈린다. 규칙에 따라 **높은 쪽으로 매기고 전제를 각 결함 행에 적었다.** 주요한 것:

- `S01`의 🔴은 "호스트 계층이 문서 밖에서 `batch.BatchRun` 행을 만들지 않는다"는 전제 위에 있다. 만든다면 🟡로 내려간다.
- `S03`의 🔴(세션 연속성)은 "실행 중 원천에 동시 쓰기가 있다"는 전제 위에 있다. 없다면 🟡(허위 보증 표기)다.
- `S03`의 `ALLOW_SNAPSHOT_ISOLATION` 🟠는 "대상 DB에서 아직 켜져 있지 않다"는 전제 위에 있다. 켜져 있다면 🟡다.
- `S07`의 `100` → `100.0` 🔴은 "율 컬럼이 정수형이거나 decimal 나눗셈 스케일이 달라진다"는 전제 위에 있다.
- `S07`의 결합 키 🟠는 "`ID` 단독 유일성이 보장되지 않는다"는 전제 위에 있다.
- `S14`의 🟠는 "오탐 후 운영자가 그림자 복원을 승인한다"는 전제 위에 있다. 보고 단계에서 그치면 🟡다.
- `B5`(NOLOCK 제거) 전체가 "배치가 단독 실행되고 원천에 동시 커밋이 없다"는 전제에서만 ⚪다.

### 6-6. 읽지 않은 파일

- `docs/BatchMigrationPlan.md` — 단계 절을 이어붙인 조립본이며, 번들과 동일함을 기계로 확인한 뒤 각 단위에서 읽지 않도록 지시했다.
- `docs/Thinking.md` — 사고 기록이며 계약이 아니다.
- 축 A 단위는 자기 SP의 3개 파일만, 축 B의 레거시 대응 단계는 자기 단계 문서와 대응 Spec만 읽었다. 신설 6단계가 추가로 읽은 파일은 §3-B의 근거 파일 칸에 있다.

### 6-7. 이 감사가 판정을 바꾸지 않은 지점

각 단위의 반환값을 합치기만 했고, 상위가 원본이나 계획서를 다시 읽어 판정을 뒤집지 않았다. 단위가 `정합`이라 쓰고 🟡 이상을 함께 낸 경우는 결함 목록을 따랐다. 단위 간에 같은 대상을 다르게 본 곳은 없었으나, **개별 단위가 "죽은 경로"로 판단한 `@pi_bypassPreCheck`가 S02 단위에서 살아 있는 경로로 확인된 것은 예외다** — 이 경우 단위의 판정을 고치지 않고, §5-1의 B1에서 두 관측을 나란히 두었다.
