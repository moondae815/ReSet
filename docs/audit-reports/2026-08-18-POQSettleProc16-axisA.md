# POQSettleProc16 산출물 정합성 감사 — 축 A 재감사 (2026-08-18)

축 A 단위 14개(SP 하나당 하나) 전수 재검증. 각 단위는 자기 SP의 세 파일
(`object_definition.sql` · `docs/Spec.md` · `raw/metadata.json`)만 읽는 서브에이전트 하나가
맡았고, 이 문서는 그 반환값을 합친 것이다. **축 B는 이번 실행의 대상이 아니다.**

계기: 2026-08-17 축 A 결함 43건에 대응해 생성기를 고쳤고(설계
`docs/superpowers/specs/2026-08-17-axis-a-spec-fidelity-design.md`, 계획 12태스크),
그 뒤 14개 SP를 새 파이프라인으로 전수 재생성했다. 이 감사는 그 재생성 결과를 잰다.

## 1. 판정

| 축 | 판정 | 단위 수 | 신규 검증 | 캐시 재사용 | 검증 불가 |
|---|---|---|---|---|---|
| A | 결함 | 14 | 14 | 0 | 0 |
| B | (이번 실행 대상 아님) | — | — | — | — |

캐시는 14개 전부 미스였다 — 모든 `Spec.md`가 재생성되어 키의 해시가 바뀌었다.

### 직전 감사와의 대조

| 등급 | 직전(2026-08-17) | 이번 | 비고 |
|---|---|---|---|
| 🔴 | 1 | 1 | **같은 건이 아니다.** 직전 🔴은 닫혔고 같은 종류의 새 🔴이 생겼다 |
| 🟠 | 5 | 7 | |
| 🟡 | 20 | 17 | 반복 결함 3종을 4-1로 접은 뒤 수치 |
| ⚪ | 17 | 15 | 동일 |
| **합계** | **43** | **40** | 접기 전 원시 집계는 50건 |

**총량만 보면 43 → 40으로 거의 그대로다. 그러나 구성이 바뀌었다.** 직전 결함 중
내용이 보존된 것은 전부 닫혔고(아래), 새 결함의 대다수는 이번에 추가한 기계 확정
재료 자체의 공백에서 나왔다. 즉 "명세서가 원본을 잘못 옮기는" 결함은 줄었고
"기계가 만든 표가 약속한 범위를 못 채우는" 결함으로 무게가 옮겨 갔다.

### 직전 결함 중 내용이 보존된 것의 추적 결과

| 등급 | SP | 직전 결함 | 결과 |
|---|---|---|---|
| 🔴 | `EXCEPTION_PROC` | 문장 13의 파생 X(`IIF(ISNULL(A.DiscountFlag...))`) 정의 미수록 | **닫힘** — 파생 테이블 정의 표가 원문째 싣는다(`Spec.md:382-390`) |
| 🟠 | `COMM_UPD` | 문장 7의 갱신 대상 X에 `YMD` 필터 없음을 요약 표가 다르게 단언 | **닫힘** — DML 범위 표가 `UPDATE 7`만 `아니오`로 구분(`Spec.md:307`) |
| 🟠 | `EXPECT_PROC` | `PGName NOT IN` 9개 리터럴 미열거 | **잔존** |
| 🟡 | `SETTLE_INS` | 환불 분기의 블록 주석 계산식 누락 | **닫힘**(`Spec.md:248`) |
| ⚪ | `EXPECT_PROC` | `UF_GET_COLLECTYMD`의 `Collect*` 컬럼 | **닫힘** — 참조 컬럼이 `StaticAnalysis`와 일치 |

나머지 38건은 **판정불가**다. 직전 보고서가 🟡·⚪ 항목의 개별 내용과 앵커를 남기지
않아 동일성 대조가 불가능하다. 이번 보고서는 그 실수를 반복하지 않기 위해 모든
결함 행에 양쪽 앵커와 원문을 싣는다.

## 2. 검증 대상 확정

소비 명세서 집합은 `output/Jobs/POQSettleProc16/agent/MigrationInstructions.md`의
`Spec.md` 링크에서 읽었다 — 12개. 여기에 중첩 SP 전개로 `UP_Util_Settle_Summary`가
`EXEC`로 호출하는 `UP_Util_Settle_Summary_AcqManual`, `UP_UTIL_SETTLE_SUMMARY_EXTRA`
둘을 더해 **14개**가 축 A 대상이다(직전 감사와 동일한 집합).

## 3. 단위별 커버리지

| 단위 | 판정 | 상태 | 🔴 | 🟠 | 🟡 | ⚪ |
|---|---|---|---|---|---|---|
| `UP_UTIL_SETTLE_CANCEL_INS` | 정합 | 신규 |  |  |  | 5 |
| `UP_UTIL_SETTLE_COMM_UPD` | 결함 | 신규 |  | 2 | 2 | 2 |
| `UP_UTIL_SETTLE_EXCEPTION_PROC` | 결함 | 신규 | 1 |  | 4 | 1 |
| `UP_UTIL_SETTLE_EXPECT_PROC` | 결함 | 신규 |  | 1 | 1 |  |
| `UP_UTIL_SETTLE_INS` | 결함 | 신규 |  |  | 2 | 1 |
| `UP_UTIL_SETTLE_INS_EXTRA` | 결함 | 신규 |  | 1 | 3 | 2 |
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | 결함 | 신규 |  | 1 | 1 | 2 |
| `UP_UTIL_SETTLE_PROC_ETC` | 결함 | 신규 |  | 1 | 1 | 1 |
| `UP_UTIL_SETTLE_SUMMARY_ETC` | 결함 | 신규 |  | 1 | 2 |  |
| `UP_UTIL_SETTLE_SUMMARY_EXTRA` | 결함 | 신규 |  |  | 1 | 1 |
| `UP_UTIL_STAT_PGCOLLECT_INS` | 결함 | 신규 |  |  | 2 |  |
| `UP_Util_PG_Client_CMRate_Ins` | 결함 | 신규 |  |  | 2 | 1 |
| `UP_Util_Settle_Summary` | 결함 | 신규 |  |  | 2 | 2 |
| `UP_Util_Settle_Summary_AcqManual` | 결함 | 신규 |  |  | 1 |  |

근거 파일은 모든 단위가 같은 3종이다 — `object_definition.sql`, `docs/Spec.md`, `raw/metadata.json`.

## 4. 축 A 결함 — 원본 DDL ↔ `Spec.md`

반복 결함 3종은 4-1에 한 번만 적고 여기서 뺐다.

| 등급 | SP | 원본 앵커 | 산출물 앵커 | 원본 | 산출물 | 영향 |
|---|---|---|---|---|---|---|
| 🔴 | `UP_UTIL_SETTLE_EXCEPTION_PROC` | `object_definition.sql:469-508 (473 A.PGETC, 474 PGINCVTAX=B.incVTax, 478/501 PGETC4SUM=B.ETCAmt+(B.ETCAmt/10.0))` | `Spec.md:391-394 (문장17의 BB만 수록, X 행 없음), :448-458` | 문장17(PointPay/Payco, -28)의 파생 X는 UNION ALL 두 갈래로 PGETC·PGINCVTAX·PGETC4SUM을 정의하고 이 셋이 BB.PGVT 식에 그대로 들어간다 | 파생 테이블 정의 표는 문장2의 X(:374-381)와 문장13의 X(:382-390)만 싣고 문장17의 X를 싣지 않는다. BB.PGVT는 X.PGETC·X.PGIncVTax·X.PGETC4SUM을 참조하는데 정의가 없다 | PointPay/Payco PG 부가세 금액이 달라진다. X.PGETC는 정산테이블 기존값인데 PGETC4SUM은 요율테이블 값이라 출처가 달라, 정의 없이 이행하면 같은 출처로 잡기 쉽다 |
| 🟠 | `UP_UTIL_SETTLE_COMM_UPD` | `object_definition.sql:240 (문장7 파생 D 내부), 233-245` | `Spec.md:370 (반복 :82,:307,:326)` | 문장7의 파생 D가 후보 PLTID를 A.YMD=@pi_strYMD로 당일 부분취소 건에 한정한다 | '이 문장은 @pi_strYMD를 직접 조건으로 적용하지 않습니다'. 파생표(:326)에는 D 출력컬럼 PLTID=A.PLTID만 있고 술어가 없다 | 명세만 보고 이행하면 후보 PLTID 집합이 당일->전 기간으로 벌어져 갱신 대상 행 집합이 달라진다 |
| 🟠 | `UP_UTIL_SETTLE_COMM_UPD` | `object_definition.sql:76-77` | `Spec.md:116,:302,:365,:145-153` | 문장2의 대상 한정 AND A.ABROADCHK=1 및 A.PGNAME IN ('ALLTHEGATE','DACOMCARD','UNIONPAY','INICARD','TOSSCARD','NICECARD') | 값이 어디에도 없다. '해외카드 및 특정 PG 조건'으로만. 문장1의 대응 목록은 명기되어 있어 누락이 문장2에만 발생 | 명세만으로 6개 PG 화이트리스트와 ABROADCHK=1을 복원할 수 없어 해외카드 수수료율이 국내건·타 PG건까지 적용될 수 있다 |
| 🟠 | `UP_UTIL_SETTLE_EXPECT_PROC` | `object_definition.sql:39 (변수 정의 :16)` | `Spec.md:121 (동일 표현 :43,:254 / 용어 정의 :82)` | 갱신 1의 필터는 인라인 리터럴 9개 A.PGName NOT IN ('PLCard','SamSungPay','SSGPayCard','KakaoPay','KakaoCard','impaymobile','NaverCard','ApplePay','TossCardAuth'). 변수 @v_PLCardSettlePeriodPG(5개)와 다른 집합이며 갱신 1은 그 변수를 쓰지 않는다 | 9개 리터럴을 한 번도 열거하지 않고 '원천 사용 PG 제외'로만 기술. Spec:82가 '원천 PG 목록'을 5개로 정의해 두어 Spec만으로 재현하면 5개로 읽힌다 | SSGPayCard·KakaoPay·KakaoCard·impaymobile 4개가 제외되지 않고 갱신 1 자동회수 대상에 편입 → InState/InYMD 오설정. 대상 행 집합 변화 |
| 🟠 | `UP_UTIL_SETTLE_INS_EXTRA` | `object_definition.sql:16,:21-25,:49` | `Spec.md:86,:283` | @v_strReqYMD를 ''로 초기화한 뒤 SELECT MIN(ReqYMD)로 덮어쓴다. 0건이면 NULL이 되어 이후 6개 문장의 YMD>=@v_strReqYMD가 UNKNOWN이 되고, INSERT만 이 변수를 쓰지 않아 그대로 실행된다 | 내부 변수 표가 설정 원천만 적고 초기값 ''·0건 시 NULL 전파·INSERT의 비대칭을 기술하지 않는다 | '행이 없으면 ''가 유지된다'고 읽으면 YMD>='' 가 항상 참이 되어 DELETE/UPDATE가 전 기간을 대상으로 삼는다 |
| 🟠 | `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | `object_definition.sql:21 (동일 패턴 37,190,206)` | `Spec.md:237` | INNER JOIN TPGProperty AS PG ... — TPGProperty 참조에 잠금 힌트가 없다. WITH(NOLOCK)은 TSettleMst A에만 붙는다 | 격리 수준 표가 TPGProperty까지 NOLOCK으로 귀속하고 '커밋되지 않은 정산행 또는 PG 속성을 읽을 수 있다'고 서술 | 이 표가 테이블별 격리수준 결정 근거이므로, TPGProperty에 READ UNCOMMITTED가 적용되면 미확정 ExtraType 행이 조인에 들어와 사전검증과 DELETE/UPDATE 대상 집합이 달라진다 |
| 🟠 | `UP_UTIL_SETTLE_PROC_ETC` | `object_definition.sql:78` | `Spec.md:91,:190 (vs 같은 문서 :145,:237)` | IF @@ROWCOUNT > 1 — 기존 TSettleMiss 조회가 2건 이상이면 MAX(ID) 재조회 | '조회 결과가 2건 초과인 경우 MAX(ID)를 재조회'. 같은 문서 :145와 mermaid MULTIROW(:237)는 @@ROWCOUNT>1로 올바르게 적혀 내부 모순 | 임계값 1 어긋남. 정확히 2건인 이월 케이스에서 재조회가 생략되어 비결정적 ID가 UPDATE 대상이 된다 |
| 🟠 | `UP_UTIL_SETTLE_SUMMARY_ETC` | `object_definition.sql:74-79,:126-131` | `Spec.md:243-245,:250-252,:273-278` | 두 오류 경로 모두 ROLLBACK->SET->RETURN으로 끝나고 CLOSE/DEALLOCATE GetDataCrsr(140-141)를 거치지 않아 커서가 열린 채 세션에 남는다 | 로직 흐름 6·8과 mermaid가 롤백·출력값·반환만 기술하고 커서가 닫히지 않는 사실을 적지 않는다 | 전제: 같은 세션을 재사용해 실패 후 재호출하면 레거시는 '커서가 이미 존재' 오류로 0건 처리하는데 이식본은 정상 처리한다. 연결이 매번 새것이면 ⚪ |
| 🟡 | `UP_UTIL_SETTLE_COMM_UPD` | `object_definition.sql:222,141,269,298,327-328,350,405-407` | `Spec.md:35-76 (주석 보존 표)` | 문장7·3·10·11의 의도와 적용 시점 이력을 담은 인라인 주석들 | 주석 보존 표에 해당 행이 없다(179행 다음이 곧바로 243행) | 추적성 단절. 동작 서술은 로직 흐름에 남아 행집합·금액 영향은 없다 |
| 🟡 | `UP_UTIL_SETTLE_EXCEPTION_PROC` | `metadata.json:7065-7073 (ReferencedColumnsPerTable)` | `Spec.md:136` | 파서는 TPGProperty 참조 컬럼을 PLTID, ID, CommMethod, CommRoundFlag, CommSumRoundFlag, VatRoundFlag, PGName 7개로 확정 | SELECT 대상 테이블 표는 5개만 기재(PLTID, ID 누락) | 파서 확정값과 어긋난다(계약상 파서가 이김). 컬럼 추적성 손상 |
| 🟡 | `UP_UTIL_SETTLE_EXCEPTION_PROC` | `object_definition.sql:227,239,247,...,312,...,432,450,523 (약 32건)` | `Spec.md:34-75 (원본 주석 보존 목록)` | 원본 인라인 주석 약 32건. DDL:312 '부분취소건: UP_UTIL_SETTLE_INS 에서 처리', DDL:432 'Payco 취소기한 180일 이후(20260510) 제거 가능' 등 | 보존 목록이 DDL:220까지는 조밀하나 이후로는 322,323,344,459,460,475,495 일곱 줄만 싣는다 | 추적성. DDL:312는 이행 범위 분담 사실, DDL:432는 폐기 예정 시점이라 이행 판단에 쓰이는데 둘 다 산출물에 없다 |
| 🟡 | `UP_UTIL_SETTLE_EXCEPTION_PROC` | `object_definition.sql:313-323` | `Spec.md:267` | 문장12(-19)의 WHERE에 PGName 필터가 없다. 대상 한정은 TPLCardTxMst와의 PLTID 조인으로만 | UPDATE 컬럼 설명이 '원천 카드 PG의 기준일 거래 중'으로 시작해 PG명 집합 필터가 있는 것처럼 읽힌다 | 이행 시 PGName 필터를 덧붙이면 대상 행 집합이 줄어든다. 기계 확정 표(:358)와 흐름 요약(:431)은 정확해 오독 위험 수준 |
| 🟡 | `UP_UTIL_SETTLE_EXCEPTION_PROC` | `object_definition.sql:416 (A.AYMD=B.YMD), :393` | `Spec.md:92` | TClientSettleRate4MobileCo.YMD는 @pi_strYMD가 아니라 A.AYMD와 조인되고, TPLCardTxMst.YMD는 UDF의 9번째 인자로만 쓰인다 | 파라미터 표의 @pi_strYMD 관련 컬럼에 둘이 나열되고 '조인 원본을 @pi_strYMD로 제한합니다'로 맺는다 | 이행 시 B.YMD=@pi_strYMD로 잘못 제한하면 impaymobile 통신사별 수수료 대상 행 집합이 달라진다. Spec:144,:361은 정확 |
| 🟡 | `UP_UTIL_SETTLE_INS` | `object_definition.sql:25,31 / 42,44 / 303,305` | `Spec.md:206,207,208` | -9 감지 IF는 라인25(SET 31), -1은 라인42(SET 44), -2는 라인303(SET 305). ControlFlowSummary도 같은 번호 체계 | '오류 코드 감지 라인 28' / '라인 39' / '라인 292' | 세 오류코드 원본 앵커가 실제 위치가 아니다. 라인292는 환불 분기 조인 술어로 오류처리와 무관. 역추적만 깨짐 |
| 🟡 | `UP_UTIL_SETTLE_INS` | `object_definition.sql:146` | `Spec.md:33-36,52 (문서 전체 0건)` | 전체거래 분기의 PaymentDB.dbo.TTxMst A WITH(NOLOCK, INDEX=CIDX_TTxMst_YMD) — 인덱스 힌트가 세 분기 중 이 분기에만 있다 | NOLOCK은 상세히 다루나 INDEX=CIDX_TTxMst_YMD는 문서에 한 번도 없다 | 금액·행집합 불변. 이관자가 힌트를 모른 채 재작성하면 TTxMst 풀스캔으로 배치 시간이 달라질 수 있다 |
| 🟡 | `UP_UTIL_SETTLE_INS_EXTRA` | `object_definition.sql:65,:153 / metadata.json:3293` | `Spec.md:136` | INSERT 컬럼에 X.PRODUCTNAME이 있고 값은 '영중소차액정산'. metadata Dependencies에 TSettleMst.ProductName varchar(120) NULL 실재 | '제공된 TSettleMst 스키마에는 PRODUCTNAME 컬럼이 없고 … 스키마 불일치입니다'로 단정 | 실재 컬럼을 없는 것으로 기술. 이행 시 ProductName 매핑을 원본 결함으로 보고 누락할 위험 |
| 🟡 | `UP_UTIL_SETTLE_INS_EXTRA` | `object_definition.sql:117,:162-165,:171-185,:302-303` | `Spec.md:259-266,:205` | 금액 절사는 3인자 ROUND 외에 CAST(... AS INT)로도 일어난다. 문장5는 부호반전 이후라 피연산자가 음수다 | '반올림 및 절사 규칙' 표가 ROUND 9건만 열거하고 CAST 절사를 한 줄도 안 적는다. 문장5도 '정수 변환하여'로만 | 이 절을 반올림 계약으로 삼아 이행하면 음수 금액에서 CLComm·CLVT·POQIncome이 행당 1원 단위로 달라진다 |
| 🟡 | `UP_UTIL_SETTLE_SUMMARY_ETC` | `object_definition.sql:78,:130,:147` | `Spec.md:22,:32` | 세 곳 모두 인자 없는 RETURN이라 실패(1001/1002) 시에도 EXEC @rc의 반환 상태는 0이다. 실패 신호는 @po_intRetVal에만 실린다 | '출력 매개변수와 RETURN으로 결과를 전달합니다', '헤더의 =0->성공 계약은 구현과 일치합니다'로만 적고 반환 상태가 항상 0인 점을 명시하지 않는다 | 이식 시 호출자가 반환 상태로 성패를 판정하면 실패가 성공으로 읽힌다 |
| 🟡 | `UP_UTIL_STAT_PGCOLLECT_INS` | `object_definition.sql:113` | `Spec.md:151-154,:91-108,:122-125` | 최종 집계 SELECT에 GROUP BY 뒤 ORDER BY INYMD, CLIENTID, PGNAME, MALLID가 붙어 있다 | Spec 어디에도 ORDER BY 언급이 없다 | 삽입 행 집합·금액은 불변이나 물리 삽입 순서를 규정하는 구문이 소실. 재작성 시 보존 여부 판단 근거가 없다 |
| 🟡 | `UP_Util_PG_Client_CMRate_Ins` | `object_definition.sql:214-221` | `Spec.md:95` | TClientCMRate는 세 용처의 상태 필터가 다르다. MobileCo INSERT(214)에는 상태 필터가 없어 전 상태를 복제한다 | 조인과 상태 조건을 용처 구분 없이 한 셀에 세미콜론으로 나열 | MobileCo 복제에도 USESTATE IN (0,4)가 걸리는 것으로 오독될 수 있다. 로직 흐름 13행은 정확해 CRUD 표 표기 층위의 결함 |
| 🟡 | `UP_Util_Settle_Summary` | `object_definition.sql:29,65,91 + metadata Dependencies(TSettleMst.YMD='거래 또는 취소일자')` | `Spec.md:19,278,281,284,287 (vs 같은 문서 92,60)` | 기준 컬럼 YMD는 거래(또는 취소)일자이고 정산일 계열은 OUTYMD/INYMD로 따로 있다 | 개요와 mermaid 4개 노드가 @pi_strYMD를 '입력 정산일'로 부르는데, 같은 문서 INSERT 매핑표(92)와 파라미터표(60)는 '거래일'로 부른다 | 표기 불일치. 술어는 정확해 실행 의미는 보존되나 이관자가 배치 입력일을 지급일/회수일로 오해할 여지 |
| 🟡 | `UP_Util_Settle_Summary` | `object_definition.sql:77-82,149-154,187-193 (RegDate 미지정, 기본값 getdate())` | `Spec.md:89-118,152-182,184-218` | 세 대상 테이블의 RegDate가 INSERT 컬럼 목록에서 빠져 DB 기본값 getdate()로 채워진다 | 세 INSERT 표가 명시 컬럼만 나열하고 RegDate가 기본값으로 채워진다는 사실을 적지 않는다 | 배포 구성 의존(높은 쪽). 이관 스키마가 DEFAULT를 함께 이관하지 않으면 등록일시가 달라지거나 NULL |
| ⚪ | `UP_UTIL_SETTLE_CANCEL_INS` | `object_definition.sql:47,49` | `Spec.md:81` | A.PLTID는 결합 키로만 쓰이고 INSERT되는 PLTID 값의 원천은 B.PLTID다 | TTxMst 행 용도를 '… INSERT 원천으로 제공합니다'로 서술해 A.PLTID도 원천인 것처럼 읽힌다 | 없음. 결합 조건상 두 값이 같고 매핑 표는 B.PLTID로 정확히 표기 |
| ⚪ | `UP_UTIL_SETTLE_CANCEL_INS` | `metadata.json Dependencies[TSettleMst].Columns (59열 중 INSERT 44열)` | `Spec.md:138-144` | INSERT에서 빠진 컬럼은 15개 | '명시적으로 제외된 컬럼' 표에 InYMD·OutYMD·CompanySalesType 3건만 열거 | 없음. 나머지 12열은 Null 허용이거나 기본값 보유, ID는 IDENTITY라 실패 위험 없음 |
| ⚪ | `UP_UTIL_SETTLE_CANCEL_INS` | `object_definition.sql:16-19 (SET NOCOUNT ON 없음)` | `Spec.md 전체` | 본문에 SET NOCOUNT ON이 없어 INSERT 행 수 메시지가 호출자에게 전달된다 | NOCOUNT 언급이 없다 | 없음(오기 아님, 미언급) |
| ⚪ | `UP_UTIL_SETTLE_CANCEL_INS` | `object_definition.sql:53-57,61` | `Spec.md:73` | 실패 경로도 값 없는 RETURN이라 반환 코드는 성공·실패 모두 0이고 실패는 @po_intRetVal로만 전달된다 | '양 경로 모두 정수 리터럴을 지정하지 않습니다'까지만 적고 그 귀결을 명시하지 않는다 | 없음. 헤더 규약과의 괴리는 Spec:55-56에서 이미 지적됨 |
| ⚪ | `UP_UTIL_SETTLE_CANCEL_INS` | `object_definition.sql:18 (파서는 방향 미표기)` | `Spec.md:65` | OUTPUT 방향 | '출력'으로 정확히 표기 | 없음 — 파서 공백을 DDL 원문으로 올바르게 메운 사례(긍정 확인) |
| ⚪ | `UP_UTIL_SETTLE_COMM_UPD` | `object_definition.sql:109` | `Spec.md:55-60,:328-335` | 주석 처리된 티모넷 블록이 인라인 TVF dbo.UF_GET_CLIENTID4TMONET()을 호출한다 | 주석 보존 표는 90-94행만 수록하고 109행을 누락. UDF 표에도 없다 | 정보. 두 호출 지점 모두 비활성이라 실행 영향 없음 |
| ⚪ | `UP_UTIL_SETTLE_COMM_UPD` | `object_definition.sql:451,455` | `Spec.md:288` | UF_GET_SETTLE_EXCHANGERATE(B.YMD, B.ClientID) — 인자는 항상 정산 행의 YMD이고 문장은 SettleYMDType=2로 한정된다 | '정산일 또는 승인일 기준 환율 함수 결과로 나누고' | 정보. 같은 셀에 원문 표현식이 있어 오독 가능성은 낮다 |
| ⚪ | `UP_UTIL_SETTLE_EXCEPTION_PROC` | `object_definition.sql:41,58,111,...,455 (16곳)` | `Spec.md:29` | 갱신 대상 별칭 자체(TSettleMst A/AA/Y)에도 WITH(NOLOCK)이 붙어 있다 | '다수의 조회 원본에 WITH(NOLOCK)을 사용합니다'로만 서술 | 정보. 갱신 대상 NOLOCK은 격리수준 설계 판단에 쓰일 수 있으나 구분되지 않는다 |
| ⚪ | `UP_UTIL_SETTLE_INS_EXTRA` | `object_definition.sql:134-137 vs :167,:188` | `Spec.md:55,:162` | ProcState 분기는 X.CLComm·X.PGComm이 ISNULL로 감싸져 NULL이 될 수 없어 항상 NULL이다(도달 불가) | 원문 조건을 그대로 재기술할 뿐 도달 불가라는 점을 기술하지 않는다 | 원본 주석의 이상 검출 장치가 사문화된 상태임을 드러내지 못함 |
| ⚪ | `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | `object_definition.sql:155-156` | `Spec.md:307-308 (대비 217-218,240)` | SETTLE_CARD_DB의 두 함수 정의가 metadata.json RawPromptContext에 실제로 포함되어 있다 | 참조 코드 객체 절은 '분석 생략(외부 객체)'로 표기하면서 217-218,240에서는 내부 테이블·분기·-1 반환까지 상세 서술 | 서술 내용은 함수 원문과 대조해 정확하다. 근거 표기만 모순 |
| ⚪ | `UP_UTIL_SETTLE_PROC_ETC` | `object_definition.sql:104-107 / metadata Dependencies(TSettleMiss.OutState DEFAULT 2)` | `Spec.md:104-116,:137-147` | INSERT가 OutState를 명시하지 않고 기본값 2에 의존한다. 직후 검증 조회는 OutState=2를 필터로 쓴다 | INSERT 매핑 표·정합성 고려사항 어디에도 기본값 의존이 기록되지 않았다 | 기본값을 재현하지 않는 재구현은 삽입 직후 집계에서 신규 행이 빠져 -3 롤백으로 귀결 |
| ⚪ | `UP_UTIL_SETTLE_SUMMARY_EXTRA` | `object_definition.sql:20` | `Spec.md:82` | DECLARE @v_strReqYMD VARCHAR(8) = '' 는 지역 변수이고 ProcedureParameters는 2건뿐이다 | '파라미터 목록' 표에 @v_strReqYMD를 3번째 행으로 넣고 방향 칸에 '내부 변수'로 표기 | 표를 호출 계약으로 읽으면 인자 수를 오인할 수 있다. 방향 칸 명시로 실질 위험은 낮다 |
| ⚪ | `UP_Util_PG_Client_CMRate_Ins` | `object_definition.sql:25,:209` | `Spec.md:73,:82` | 반환 코드 -9가 서로 다른 두 지점(기정산 사전 차단·MobileCo DELETE 오류)에서 설정된다 | 두 행 모두 정확히 등재되어 있으나 같은 코드가 두 의미를 갖는다는 사실을 명시하지 않는다 | 호출자가 -9만으로 두 실패 경로를 구분할 수 없다는 설계 특성이 드러나지 않는다 |
| ⚪ | `UP_Util_Settle_Summary` | `object_definition.sql:31-35,43-47,55-59,68-72,103-107,140-144,178-182,212-216` | `Spec.md:259-266` | 실행 순서는 ROLLBACK TRAN -> SET @po_intRetVal = -n -> RETURN | '오류 발생 시 -1을 설정하고 롤백합니다'로 순서를 뒤집어 서술. 같은 문서 mermaid는 올바른 순서라 내부에서도 엇갈린다 | 결과 동일(출력 파라미터는 롤백 대상 아님) |
| ⚪ | `UP_Util_Settle_Summary` | `object_definition.sql:14,221-225` | `Spec.md:61,76,267-268` | 첫 EXEC 시점까지 @po_intRetVal이 초기화되지 않으며 성공 판정이 하위 SP의 설정에 의존한다 | '하위 프로시저가 설정한 값을 유지한 채 롤백'이라고만 하고 미초기화 전달 사실은 기록하지 않는다 | 하위 SP가 값을 설정하지 않는 경로가 있거나 호출자가 0이 아닌 값을 넘기면 정상 처리 후에도 전체 롤백 |

### 4-1. 전 SP 공통 결함

세 항목 모두 **이번 재생성에서 새로 생긴 것**이고, 원인이 각각 하나다.

**A1. 「DML 범위 (기계 확정 — 수정 금지)」 표가 INSERT를 담지 않는다** — 🟡, 8개 SP

`UP_UTIL_SETTLE_INS`, `UP_UTIL_SETTLE_INS_EXTRA4PLCARD`, `UP_UTIL_SETTLE_PROC_ETC`,
`UP_UTIL_SETTLE_SUMMARY_ETC`, `UP_UTIL_SETTLE_SUMMARY_EXTRA`, `UP_UTIL_STAT_PGCOLLECT_INS`,
`UP_Util_Settle_Summary_AcqManual`, `UP_Util_PG_Client_CMRate_Ins`.
(`UP_Util_Settle_Summary`도 같은 상태이나 해당 단위가 결함 대신 보류로 반환했다.)

`DmlScopeExtractor`는 UPDATE/DELETE만 수집한다. 표 이름은 "DML 범위"이고 "기계 확정 —
수정 금지"라 못 박혀 있어 세 DML 전부를 담는다고 읽히는데 실제로는 둘만 담는다.
결과로 `UP_UTIL_STAT_PGCOLLECT_INS`는 삭제 전용 SP처럼 보이고, `UP_Util_PG_Client_CMRate_Ins`는
INSERT 5문이 라인 앵커가 붙은 유일한 표에서 통째로 빠져 추적 근거를 잃는다.
`UP_UTIL_SETTLE_SUMMARY_EXTRA`에서는 같은 문서의 상태코드 표가 "8개 DML 단계"라고
적어 **문서 내부 모순**까지 만든다.

컬럼 매핑·필터 정보 자체는 다른 절에 온전히 있어 금액·행 집합 영향은 없다. 다만
축 B의 단계 지시서가 이 표를 쓰기 집합의 배타적 근거로 삼는 구성이면 🟠로 올라간다
(`UP_Util_Settle_Summary_AcqManual` 단위가 그 전제를 명시했다).

**A2. 생성기 프롬프트 지시문이 명세서 본문으로 유출된다** — 🟡, 3개 SP

`UP_UTIL_SETTLE_COMM_UPD`(17곳), `UP_UTIL_SETTLE_INS_EXTRA`(5곳),
`UP_UTIL_SETTLE_INS_EXTRA4PLCARD`(3곳).

"…유일성 여부를 추측하지 **마십시오**", "…이 사실을 `## CRUD 분석`에 명시적으로
기술**하십시오**" 같은 2인칭 명령문이 그대로 실린다. UPDATE + FROM 절 문장에 붙는
보일러플레이트가 원인이며, 그런 문장이 없는 나머지 11개 SP에서는 **전부 없음**으로
확인됐다. 특히 "`## CRUD 분석`에 기술하십시오"는 그 절 안에서 자기 자신을 가리켜
미완성 초안처럼 읽힌다.

**A3. 같은 문장에 두 개의 번호가 붙는다** — 🟡, 2개 SP

`UP_UTIL_SETTLE_EXPECT_PROC`, `UP_UTIL_SETTLE_INS_EXTRA`.

절 제목은 파서 서수(`AstUpdateMappings.StatementOrdinal`)를 그대로 쓰는데 이 값은
채번이 리셋된다. `EXPECT_PROC`에서는 라인 182와 245가 둘 다 "문장 1"이 되고, 본문·오류
코드 매핑·UDF 표는 같은 것을 "갱신 8"·"갱신 11"로 센다. "갱신 8"을 찾아 절 제목
"문장 8"을 열면 다른 UPDATE가 나온다. 양쪽 다 원본 라인 번호를 병기하는 SP에서는
위험이 낮아 ⚪로 매겨졌다.

## 5. 축 B 결함

이번 실행의 대상이 아니다. 직전 감사(2026-08-17)의 축 B 결과(🔴 9 · 🟠 37 · 단위 18개
전부 결함)는 그대로 남아 있으며, 그 사이 `Spec.md` 14개가 전부 재생성되었으므로
**축 B는 기준값이 바뀐 상태다 — 재감사 없이는 유효하지 않다.**

## 6. 이 감사가 보증하지 않는 것

- **축 B를 보지 않았다.** 위 5절대로 축 B는 기준값이 갈아엎힌 상태이며 재감사가 필요하다.
- **실행 대조를 하지 않았다.** 14개 단위 전부 정적 대조만 했고, 실제 SQL 실행·행 수
  비교는 없다.
- **UDF/TVF 본문 서술의 진위를 대부분 보증하지 않는다.** 각 단위는 자기 SP의 세 파일만
  읽으므로, `Spec.md`가 적은 함수 내부 로직(예: `UF_GET_INCVTAXRATE`가 0이면 10%,
  `UF_GET_COLLECTYMD`의 휴일 보정)은 호출부의 함수명·인자 개수·순서만 대조했다.
  `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` 단위만 예외로, 두 외부 함수 정의가
  `metadata.json`의 `RawPromptContext`에 들어 있어 본문까지 대조했다.
- **직전 결함 43건 중 38건의 동일성은 판정하지 못했다.** 직전 보고서가 🟡·⚪ 개별
  항목의 내용과 앵커를 남기지 않았다. 따라서 "43 → 40"이라는 수치는 **같은 항목의
  증감이 아니라 두 번의 독립 측정값 비교**로 읽어야 한다.
- **스키마 정의 파일을 읽지 않았다.** 컬럼의 업무 의미(예: `UseState` 코드 체계,
  상태값 9의 뜻)를 다투는 서술은 판정을 보류한 단위가 있다.
- **프런트매터의 점수·신뢰도**는 생성기 자체 메타데이터라 대조 대상에서 제외했다.
