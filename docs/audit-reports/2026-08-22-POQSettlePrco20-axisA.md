# POQSettlePrco20 산출물 정합성 감사 — 축 A

> 감사일 2026-08-22 · 범위 **축 A(객체 단위)와 축 A 교차** 한정. 축 B(계획서·단계 지시서)는 이번 실행의 범위가 아니다(6절).

## 1. 판정

| 축 | 판정 | 단위 수 | 신규 검증 | 캐시 재사용 | 검증 불가 |
|---|---|---|---|---|---|
| A (객체) | **결함** | 31 | 31 | 0 | 0 |
| A 교차 | **결함** | 8 | 8 | 0 | 0 |
| A 보류 종결 | **결함** | 1 | 1 | 0 | 0 |
| A 재검증 | **정합** | 3 | 3 | 0 | 0 |
| B | 검증 안 함 (범위 밖) | — | — | — | — |

축 A 객체 31단위 중 24단위가 결함, 7단위가 정합. 축 A 교차 8단위 중 1단위가 결함, 7단위가 정합. 여기에 **보류 종결 단위 1개**와 **재검증 단위 3개**가 더해져 총 43단위다 — `UP_Util_Settle_Summary` 단위가 남긴 보류를 `UP_UTIL_SETTLE_SUMMARY_EXTRA` 원본으로 닫은 것이고, 그 자체가 결함을 냈다(4-0-1). 재검증 3단위는 실행 대조로 전제가 무너진 판정을 다시 내린 것이고 셋 다 정합으로 닫혔다(4-3).

**캐시 재사용 0건.** 직전 감사(2026-08-21 14:25)가 남긴 `axisA:` 31건·`axisA-cross:` 8건은 키가 전부 어긋났다 — 명세서가 2026-08-21 22:58 ~ 2026-08-22 00:09에 재생성되어 `Spec.md`·`metadata.json` 해시가 바뀌었고 `object_definition.sql` 해시만 그대로였다. 39단위 전량을 다시 검증했다.

| 등급 | 건수 |
|---|---|
| 🔴 | 2 |
| 🟠 | 7 |
| 🟡 | 56 |
| ⚪ | 60 |

**실행 대조를 했다.** 로컬 Docker 컨테이너 `sql-server`(SQL Server 2022, 16.0.4255.1)에 실 DB 4개(`SETTLE_POQ_DB`·`SETTLE_CARD_DB`·`PaymentDB`·`PLCardDB`)가 올라와 있어, 단위들이 등급이 갈릴 수 있다며 남긴 자립 실행 쿼리 **6건을 전부 돌렸다**(6절). 그 결과 🔴이 하나 늘고(🟡 → 🔴 승격), 전제가 무너진 판정 3건이 재검증으로 닫혔다.

## 2. 검증 대상 확정

**소비 명세서 집합 (12개)** — `output/Jobs/POQSettlePrco20/agent/MigrationInstructions.md`의 `Spec.md` 링크에서 읽었다. `raw/prompt-context.md` 폴백은 쓰지 않았다.

```
dbo.UP_Util_PG_Client_CMRate_Ins      dbo.UP_UTIL_SETTLE_CANCEL_INS
dbo.UP_UTIL_SETTLE_COMM_UPD           dbo.UP_UTIL_SETTLE_EXCEPTION_PROC
dbo.UP_UTIL_SETTLE_EXPECT_PROC        dbo.UP_UTIL_SETTLE_INS
dbo.UP_UTIL_SETTLE_INS_EXTRA          dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD
dbo.UP_UTIL_SETTLE_PROC_ETC           dbo.UP_UTIL_SETTLE_SUMMARY_ETC
dbo.UP_Util_Settle_Summary            dbo.UP_UTIL_STAT_PGCOLLECT_INS
```

**참조 폐포 (31개 객체)** — 위 12개 SP의 `raw/dependency-manifest.json` `Nodes[]` 합집합. 경로는 매니페스트의 `SpecPath`·`DdlPath`를 객체 디렉터리 `output/Procedures/[SP]/` 기준으로 풀어 썼다(조립하지 않았다).

| 구성 | 개수 |
|---|---|
| SP (소비 12 + 중첩 2) | 14 |
| 로컬 UDF·TVF (`SETTLE_POQ_DB`) | 10 |
| 외부 DB UDF (`SETTLE_CARD_DB`) | 7 |
| **합계** | **31** |

31개 노드 전부 `Status: Succeeded`이고 `object_definition.sql`·`Spec.md`·`metadata.json`이 모두 실재했다 — **검증 불가 0건**.

**폐포에만 있는 SP 2개** — `UP_UTIL_SETTLE_SUMMARY_EXTRA`, `UP_Util_Settle_Summary_AcqManual`. 둘 다 `UP_Util_Settle_Summary`가 `EXEC`으로 부르는 하위 SP이고 최상위 실행 순서에 직접 오르지 않으므로 소비 집합에서 빠진 것이 정상이다. 다만 산출물이므로 축 A 대상에는 포함했고, 두 SP 모두 결함이 나왔다(4절). 단계 흡수 여부는 축 B에서 판정할 사안이라 이번 범위 밖이다.

**축 A 교차 대상 (8개)** — 사용자 함수를 호출하는 객체 전부를 원본 DDL에서 실측했다. SP 6개 + **함수 2개**(`UF_GET_COLLECTYMD`·`UIF_SettleYMD`가 각각 `UF_GET_WORKDAY2`를 부른다).

| 호출하는 객체 | 종류 | 호출 건수 | 기계 확정 표 |
|---|---|---|---|
| `UP_UTIL_SETTLE_EXCEPTION_PROC` | SP | 29 | 있음 |
| `UP_UTIL_SETTLE_COMM_UPD` | SP | 23 | 있음 |
| `UP_UTIL_SETTLE_EXPECT_PROC` | SP | 9 | 있음 |
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | SP | 6 | 있음 |
| `UP_UTIL_SETTLE_INS_EXTRA` | SP | 5 | 있음 |
| `UP_UTIL_SETTLE_INS` | SP | 3 | 있음 |
| `UF_GET_COLLECTYMD` | 함수 | 2 | 없음 (정상 — 3-2-1 사각지대) |
| `UIF_SettleYMD` | 함수 | 2 | 없음 (정상 — 3-2-1 사각지대) |

SP 6개의 호출은 **75건 전부** 기계 확정 표에 실렸다(계약의 실측치와 일치). 함수 2개는 유일한 DML 안에 함수 호출이 없어 표가 생성되지 않는 것이 정상이며, 그 호출은 `axis-a.md` 3-2-1절(사각지대)이 맡았다.

## 3. 단위별 커버리지

| 축 | 단위 | 종류 | 판정 | 상태 | 근거 파일 |
|---|---|---|---|---|---|
| A | `UP_UTIL_SETTLE_CANCEL_INS` | SP | 정합 (⚪2) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_UTIL_SETTLE_COMM_UPD` | SP | 결함 (🟡2 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_UTIL_SETTLE_EXCEPTION_PROC` | SP | 결함 (🟡4 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_UTIL_SETTLE_EXPECT_PROC` | SP | 결함 (🟡4 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_UTIL_SETTLE_INS` | SP | 결함 (🟡4 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_UTIL_SETTLE_INS_EXTRA` | SP | 결함 (🟠1 🟡5) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | SP | 결함 (🟡1 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_UTIL_SETTLE_PROC_ETC` | SP | 결함 (🟡4 ⚪3) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_UTIL_SETTLE_SUMMARY_ETC` | SP | 결함 (🟠1 🟡2 ⚪2) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_UTIL_SETTLE_SUMMARY_EXTRA` | SP | 결함 (🟠1 🟡1 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_UTIL_STAT_PGCOLLECT_INS` | SP | 결함 (🟡2 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_Util_PG_Client_CMRate_Ins` | SP | 정합 (⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_Util_Settle_Summary` | SP | 결함 (🟡1 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UP_Util_Settle_Summary_AcqManual` | SP | 결함 (🟡1 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `SETTLE_CARD_DB.UF_GET_COMM4CLIENT` | 외부함수 | 결함 (🟡2 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `SETTLE_CARD_DB.UF_GET_COMM4CLIENT4INTEREST` | 외부함수 | 결함 (🔴1 ⚪2) | 신규 | DDL + Spec.md + metadata.json |
| A | `SETTLE_CARD_DB.UF_GET_COMM4CLIENT4PARTIALCANCEL` | 외부함수 | 결함 (🟡1 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `SETTLE_CARD_DB.UF_GET_COMM4PG` | 외부함수 | 결함 (🟡1 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `SETTLE_CARD_DB.UF_GET_COMM4PG4INTEREST` | 외부함수 | 결함 (🟡2 ⚪3) | 신규 | DDL + Spec.md + metadata.json |
| A | `SETTLE_CARD_DB.UF_GET_EXTRACOMM4CLIENT` | 외부함수 | 결함 (🟡3 ⚪4) | 신규 | DDL + Spec.md + metadata.json |
| A | `SETTLE_CARD_DB.UF_Get_ExtraCardCommissionAmt` | 외부함수 | 정합 (⚪3) | 신규 | DDL + Spec.md + metadata.json |
| A | `UF_GET_CLIENTSECTIONRATE` | 함수 | 정합 (⚪3) | 신규 | DDL + Spec.md + metadata.json |
| A | `UF_GET_COLLECTYMD` | 함수 | 결함 (🟡2 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UF_GET_INCVTAXRATE` | 함수 | 결함 (🟡1 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UF_GET_OUTYMD4REFUND` | 함수 | 정합 (⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UF_GET_PGCommOption` | 함수 | 결함 (🟡3 ⚪3) | 신규 | DDL + Spec.md + metadata.json |
| A | `UF_GET_ROUND4VAT` | 함수 | 정합 (⚪2) | 신규 | DDL + Spec.md + metadata.json |
| A | `UF_GET_SETTLE_EXCHANGERATE` | 함수 | 결함 (🟡2 ⚪3) | 신규 | DDL + Spec.md + metadata.json |
| A | `UF_GET_WORKDAY2` | 함수 | 정합 (⚪2) | 신규 | DDL + Spec.md + metadata.json |
| A | `UF_Get_CLComm4MobileCo` | 함수 | 결함 (🟡2 ⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A | `UIF_SettleYMD` | 함수 | 결함 (🟠3 🟡5 ⚪2) | 신규 | DDL + Spec.md + metadata.json |
| A 교차 | `UP_UTIL_SETTLE_COMM_UPD` | 교차 | 정합 (⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A 교차 | `UP_UTIL_SETTLE_EXCEPTION_PROC` | 교차 | 정합 | 신규 | DDL + Spec.md + metadata.json |
| A 교차 | `UP_UTIL_SETTLE_EXPECT_PROC` | 교차 | 정합 | 신규 | DDL + Spec.md + metadata.json |
| A 교차 | `UP_UTIL_SETTLE_INS` | 교차 | 정합 | 신규 | DDL + Spec.md + metadata.json |
| A 교차 | `UP_UTIL_SETTLE_INS_EXTRA` | 교차 | 정합 (⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A 교차 | `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | 교차 | 정합 (⚪1) | 신규 | DDL + Spec.md + metadata.json |
| A 교차 | `UF_GET_COLLECTYMD` | 교차 | 정합 (⚪1) | 신규 | DDL + Spec.md + metadata.json + 피호출 함수 DDL |
| A 교차 | `UIF_SettleYMD` | 교차 | 결함 (🟡1 ⚪1) | 신규 | DDL + Spec.md + metadata.json + 피호출 함수 DDL |
| A 보류 종결 | `UP_UTIL_SETTLE_SUMMARY_EXTRA` | 보류종결 | 결함 (🟠1 🟡1 ⚪1) | 신규 | DDL + Spec.md + metadata.json + **호출자 `UP_Util_Settle_Summary`의 Spec.md** |
| A 재검증 | `SETTLE_CARD_DB.UF_GET_COMM4CLIENT4PARTIALCANCEL` | 재검증 | 정합 (⚪1) | 신규 | DDL + Spec.md + 호출 SP DDL |
| A 재검증 | `SETTLE_CARD_DB.UF_GET_COMM4PG` | 재검증 | 정합 (⚪1) | 신규 | DDL + Spec.md + 호출 SP DDL |
| A 재검증 | `SETTLE_CARD_DB.UF_GET_EXTRACOMM4CLIENT` | 재검증 | 정합 (⚪1) | 신규 | DDL + Spec.md + 호출 SP DDL |

43단위 = 축 A 객체 31(SP 14 · 함수 10 · 외부함수 7) + 축 A 교차 8 + 보류 종결 1 + 재검증 3. 1절 판정 표와 일치한다. 검증 불가 0건.

보류 종결 단위의 캐시 키는 해시 **4개**다 — 계약의 하한(DDL·Spec·metadata) 셋에, 그 단위가 기준값으로 실제로 읽은 호출자 명세서 `UP_Util_Settle_Summary/docs/Spec.md`의 해시를 더했다. 넣지 않으면 그 문장이 바뀌었는데 캐시가 유효로 남는다.
## 4. 축 A 결함

반복되는 무리는 **4-1로 한 번만** 적고, 여기에는 개별 결함만 둔다.

### 4-0-1. 🔴 · 🟠 — 금액 또는 대상 행 집합이 갈리는 결함 9건

**🔴 `SETTLE_CARD_DB.UF_GET_COMM4CLIENT4INTEREST`** — 원본 `object_definition.sql:68` ↔ 산출물 `Spec.md:138` · 호출 객체 1개

- **원본**: SET @po_intCommissionAmt = CAST(@pi_intTxAmt * @v_intFreeInterestRate AS INT) — 피연산자 둘 다 MONEY이므로 money→int 변환은 절사가 아니라 반올림
- **산출물**: 'CAST(... AS INT) 변환으로 소수 부분은 반환값에 유지되지 않습니다'가 유일한 서술. Spec.md:49,133,218,232 어디에도 반올림/절사 방향 없음
- **영향**: 절사 구현 시 1원 차이(12,345×0.0250=308.625 → 원본 309, 절사 308). 호출 SP 1개(UP_UTIL_SETTLE_EXCEPTION_PROC:357, CLIntComm 컬럼).
- **실행 확정**: 실행 대조 2026-08-22 · SQL Server 2022 16.0.4255.1 (로컬 Docker `sql-server`) — 단위가 남긴 쿼리 결과 **309**. 규칙("309면 🔴 확정")대로 🔴 확정. 이 산식은 `/100.0`이 CAST 밖이라 `money*money → int` 경로이고 0에서 먼 쪽으로 반올림한다(12.5→13, -12.5→-13).
- (원래 남긴 쿼리) `DECLARE @amt MONEY = 12345, @rate MONEY; SELECT @rate = CAST(2.50 AS numeric(5,2)) / 100.0; SELECT CAST(@amt * @rate AS INT);  → 309면 🔴 확정, 308이면 ⚪로 하향`

**🔴 `SETTLE_CARD_DB.UF_GET_COMM4CLIENT`** (실행 대조로 🟡 → 🔴 승격) — 원본 `object_definition.sql:68` ↔ 산출물 `Spec.md:197` · 호출 SP 1개

- **원본**: 3차 조회 진입 조건은 `IF @@ROWCOUNT < 1`(68)이다. 실행 대조 결과 그 앞의 `IF` 문(52)이 `@@ROWCOUNT`를 0으로 리셋하므로, 1차 조회(40-49)가 행을 찾아 2차 블록(52-65)이 건너뛰어져도 68행의 조건은 참이 되어 **3차 조회가 돈다**. 3차 조회는 1차와 같은 테이블·같은 WHERE에 `TOP 1 … ORDER BY 수수료율 DESC`만 더한 것이라 값을 최대 수수료율로 덮어쓴다.
- **산출물**: mermaid(275-280)가 `FoundCurrent -->\|예\| BaseRate`로 1차 성공 시 2·3차를 모두 건너뛰는 것으로 그린다. 산문(Spec.md:197)은 "두 번째 조회 후의 @@ROWCOUNT < 1이면"으로 단정해 2차 미실행 경로를 서술하지 않는다 — 실행 결과 도식 쪽이 원본과 어긋난다.
- **실행 확정**: 실행 대조 2026-08-22 · SQL Server 2022 16.0.4255.1 (로컬 Docker `sql-server`) — 원본 구조(SELECT → `IF @@ROWCOUNT<1 BEGIN…END` → `IF @@ROWCOUNT<1 BEGIN…END`)를 그대로 재현한 결과 **THIRD_RAN**. `IF` 문이 `@@ROWCOUNT`를 0으로 리셋하므로 1차 조회가 행을 찾아도 3차 조회가 돈다. 두 경로 실측: 1차 성공 → 2차 건너뜀 → **3차 실행(값 덮어씀)**, 1차 0행·2차 성공 → 3차 건너뜀. 단위가 남긴 규칙("3차 실행됨이면 mermaid가 원본과 어긋난 것이므로 🔴로 승격")대로 승격.
- **승격 근거**: DDL 재확인: 3차 조회(68-81)는 주석이 말하는 "ClientID + CardCode"가 아니라 **1차와 같은 테이블(`TClientCardContractDtl`)·같은 WHERE 세 술어**이고, 차이는 `TOP 1 … ORDER BY IIF(@pi_intFreeInterestFlag IN(0,2), A.CommissionRate, A.FreeInterestInstCommRate) DESC`뿐이다. 즉 실제 동작은 **항상 최대 수수료율 행이 적용**되는 것이고, 1차의 임의 행 선택은 무의미하다. 명세서 mermaid(275-280)는 1차 성공 시 3차를 건너뛰는 것으로 그려 금액 결정 규칙 자체를 다르게 서술한다.
- **영향**: 명세서 mermaid대로 이행하면 1차 조회가 찾은 임의 행의 수수료율이 남는데, 원본은 3차가 항상 덮어써 최대 수수료율을 적용한다. 같은 ClientID+CardCPID에 수수료율이 다른 행이 둘 이상이면 반환 금액이 갈린다. 호출 SP 1개(UP_UTIL_SETTLE_EXCEPTION_PROC:356 → CLCOMM)의 예외 정산 수수료에 직접 번진다.

**🟠 `UIF_SettleYMD`** — 원본 `object_definition.sql:91-96` ↔ 산출물 `Spec.md:141` · 호출 객체 3개

- **원본**: SettleCount=2(91-96)는 제2 거래일 구간만 검사하고 나머지는 기본 SettleMonth·SettleDay로 떨어짐. 제3 구간(SettleTxSDay3/EDay3 → SettleMonth3·SettleDay3)은 ELSE 분기(97-104)에만 있음
- **산출물**: 'SettleCount = 2 또는 그 외 값이면 … 제2 또는 제3 거래일 구간에 속하는지에 따라'로 두 분기에 같은 3단 판정을 부여
- **영향**: SettleCount=2면서 일자가 제3 구간에 드는 정산주기에서 원본은 기본값을 쓰는데 이행본은 SettleMonth3·SettleDay3 적용 → OutYMD가 달라짐. 호출 객체 3개 전부에 번짐

**🟠 `UIF_SettleYMD`** — 원본 `object_definition.sql:74-79` ↔ 산출물 `Spec.md:140` · 호출 객체 3개

- **원본**: CASE WHEN DATEPART(DW,기준일) > SettleDW THEN SettleDW+7 ELSE SettleDW END - DATEPART(DW,기준일) — 엄격 초과일 때만 +7
- **산출물**: '요일 값과 SettleDW를 비교해 SettleDW 또는 SettleDW+7의 차이를 더합니다'로 방향·등호 포함 여부 모두 생략
- **영향**: 요일이 정확히 일치하는 경계에서 원본은 오프셋 0, >=로 읽으면 +7 → OutYMD가 일주일 어긋남. 호출 객체 3개

**🟠 `UIF_SettleYMD`** — 원본 `object_definition.sql:75,79,80,88,93,95,99,101,103` ↔ 산출물 `Spec.md:140-141` · 호출 객체 3개

- **원본**: 모든 고정·유동_월 산식이 CONVERT(VARCHAR(6),DATEADD(M,SettleMonthN,@pi_strYMD),112) + RIGHT('0'+CONVERT(VARCHAR(2),SettleDayN),2)로 일자를 2자리 영 채움
- **산출물**: '결합합니다'만 적고 RIGHT('0'+…,2)를 옮기지 않음
- **영향**: SettleDay가 한 자리면 7자 문자열이 되어 VARCHAR(8) 날짜로 성립 안 됨. 유동_월·고정 전 분기, 호출 객체 3개

**🟠 `UP_UTIL_SETTLE_INS_EXTRA`** — 원본 `object_definition.sql:16,21-25` ↔ 산출물 `Spec.md:85,341`

- **원본**: DECLARE @v_strReqYMD VARCHAR(8) = ''(16) 뒤 집계 SELECT @v_strReqYMD = MIN(ReqYMD)…(21-25). 집계는 0건이어도 한 행을 돌려주므로 ''가 아니라 NULL이 대입됨
- **산출물**: 지역 변수 표(85)의 '원천 또는 초기값' 칸이 MIN(...)만 적어 초기값 ''를 통째로 누락. 로직 흐름 1(341)도 '최소값을 대입합니다'로만 서술. 명세서 전체에 ''도 NULL도 한 번도 없음
- **영향**: @v_strReqYMD는 IF EXISTS(33)·DELETE(49)·갱신 1~5(220,237,266,281,306) 전부의 YMD >= @v_strReqYMD 하한. 원본은 무결과 시 NULL → 삭제·갱신 0행(INSERT는 이 변수를 안 써서 삽입만 되고 OutState/OutYMD 미설정 행이 남는 것이 원본 동작). 명세서만 보고 '무결과면 대입 생략' 관용으로 이행하면 ''가 남아 YMD >= ''가 전 행에 참 → 대상 행 집합이 통째로 달라짐
- **실행 확정**: 실행 대조 2026-08-22 · SQL Server 2022 16.0.4255.1 (로컬 Docker `sql-server`) — 무결과 집계 대입 결과 **`<NULL>`**. 원본 의미(후속 DML 대상 0행)가 확인됐고, 명세서가 초기값 `''`와 NULL 귀결을 모두 누락한 것이 🟠으로 확정.
- (원래 남긴 쿼리) `DECLARE @v VARCHAR(8)=''; DECLARE @t TABLE(ReqYMD VARCHAR(8)); SELECT @v = MIN(ReqYMD) FROM @t; SELECT ISNULL(@v,'<NULL>') AS Result;  → NULL이면 원본 의미(후속 DML 0행), ''이면 🟠 확정`

**🟠 `UP_UTIL_SETTLE_SUMMARY_ETC`** — 원본 `object_definition.sql:74-79,126-131` ↔ 산출물 `Spec.md:167,171,190-197`

- **원본**: 두 IF @@ERROR<>0 경로가 ROLLBACK→SET→RETURN으로 끝나고 CLOSE/DEALLOCATE(140-141)는 정상 종료 경로에만 있음. 커서는 BEGIN TRAN(53)보다 먼저 OPEN(51)되어 롤백으로도 안 닫힘
- **산출물**: 로직 흐름 4·6단계와 흐름도 종단이 롤백·반환만 서술, 커서가 열린 채 남는다는 사실 부재
- **영향**: DECLARE CURSOR에 LOCAL이 없어 범위가 default_to_local_cursor에 달림. GLOBAL(기본값)이면 같은 연결 재호출 시 오류 16915로 DECLARE 실패 → 처리 대상 행이 통째로 0. LOCAL이면 리소스 점유에 그쳐 🟡

**🟠 `UP_UTIL_SETTLE_SUMMARY_EXTRA`** — 원본 `object_definition.sql:20,25-29` ↔ 산출물 `Spec.md:87 (로직 흐름 동일 누락: Spec.md:332)`

- **원본**: DECLARE @v_strReqYMD VARCHAR(8) = '' 뒤 집계 SELECT @v_strReqYMD = MIN(ReqYMD)…가 항상 실행되므로 0건이면 변수가 ''이 아니라 NULL. 이후 8개 DML의 YMD >= @v_strReqYMD(35,50,86,102,139,155,193,210)와 DELETE 4의 OUTYMD >= @v_strReqYMD(196)가 UNKNOWN이 되어 대상 0건
- **산출물**: 내부 변수 표에 초기값 ''과 원천 MIN(...)만 적고, 로직 흐름 2단계도 '최솟값을 조회하여 저장합니다'로만 서술. 명세서 전체에 @v_strReqYMD의 NULL 언급 0건
- **영향**: 무결과 시 ''을 유지하는 자연스러운 구현이면 YMD >= ''가 전부 참이 되어 해당 집계 행 전량 삭제·재삽입. 원본은 0건 — 대상 행 집합이 '전부'와 '없음'으로 정반대

**🟠 `UP_UTIL_SETTLE_SUMMARY_EXTRA`** (보류 종결 단위) — 원본 `object_definition.sql:36-40,61-65,87-92,113-118,140-145,166-171,194-200,223-228` ↔ 산출물 **호출자** `UP_Util_Settle_Summary/docs/Spec.md:267`

- **원본**: 세 조건의 이름·값은 전수 일치한다(`PGNAME IN ('allthegate','dacomcard','tosscard','nicecard')` 8/8 · `CompanySalesType IN (0,1,2,3)` 8/8 · `ExtraSettleFlag = 1` 8/8). 그러나 여덟 문장 **전부에** `ProcYMD = @pi_strYMD`와 `YMD >= @v_strReqYMD`가 더 있고, 문장별로 부분취소 쌍의 `USESTATE = 2`(91,117) · 회수 쌍의 `ISNULL(INYMD,'') <> ''`(142,168) · 지급 쌍의 `ISNULL(OUTYMD,'') <> ''`(197,225)가 있다. `DELETE TSettleByOUT`에만 `OUTYMD >= @v_strReqYMD`(196)가 있고 대응 `INSERT`에는 없어 삭제 범위가 등록 범위보다 좁다.
- **산출물**: 「정해진 PG명과 사업자 매출구분, 차액정산구분 조건으로」라는 열거가 실제 술어 집합의 **부분집합**이고, 여덟 문장의 서로 다른 술어를 하나로 뭉갰다. 「삭제 후 재등록」이 함의하는 대칭도 성립하지 않는다.
- **영향**: 이 문장을 근거로 이행하면 `ProcYMD`·`YMD` 하한이 사라져 해당 일자가 아닌 과거 집계까지 삭제되고, 부분취소 쌍에서 `USESTATE = 2`가 빠지면 `TPartialCancelByTX`의 건수·합계 금액이 갈린다. **등급 전제**: 하위 SP 이행이 자기 `Spec.md`만을 정본으로 삼는 배치라면 🟡(정본 이중화), 호출자 명세서의 이 문장이 근거로 쓰일 수 있는 배치라면 🟠 — 배포 구성에 달려 높은 쪽으로 매겼다.

### 4-0-2. 🟡 — 개별 결함 (반복 무리는 4-1로 접었다)

| 객체 | 종류 | 원본 앵커 | 산출물 앵커 | 요지 | 영향 |
|---|---|---|---|---|---|
| `SETTLE_CARD_DB.UF_GET_COMM4CLIENT4PARTIALCANCEL` | 외부함수 | `object_definition.sql:48,49,63,64` | `Spec.md:32` | '원본 DDL은 테이블을 두 부분 식별자로 참조합니다'로 단정 | 금액·행 집합 불변. 원본은 세션 기본 스키마 의존인데 dbo.로 고정된 것처럼 읽힘. 호출 SP 1개 |
| `SETTLE_CARD_DB.UF_GET_EXTRACOMM4CLIENT` | 외부함수 | `object_definition.sql:72,75` | `Spec.md:76` | 파라미터 표 '용도 및 사용 위치'가 '조인 A.YMD = B.YMD에 사용됩니다'로 적어 파라미터가 조인 조건에 있는 것처럼 기술 | 술어 출처가 뒤바뀌어 추적성 손상. 행 집합·금액 불변. 호출 SP 1개 |
| `SETTLE_CARD_DB.UF_GET_EXTRACOMM4CLIENT` | 외부함수 | `object_definition.sql:87,92` | `Spec.md:29-32` | '반환 가능 값' 목록이 전수인 양 셋만 열거하고 NULL 원인을 조회 실패에만 귀속. 입력 금액발 NULL 전파 경로 누락 | 비-nullable로 잡고 NULL을 0으로 접으면 수수료가 0으로 확정(원본은 NULL). 호출 SP 1개 |
| `UF_GET_COLLECTYMD` | 함수 | `object_definition.sql:131,146 (+metadata ObjectKey.Database=SETTLE_POQ_DB, Dependencies[THoliday].Database=null)` | `Spec.md:38` | 둘을 한데 묶어 '동일 인스턴스 내 다른 데이터베이스 참조'로 서술 | THoliday에 불필요한 DB 컨텍스트·권한 배정, 진짜 크로스 DB의 특수성 희석. 호출 SP 1개(UP_UTIL_SETTLE_EXPECT_PROC:29,50) |
| `UF_GET_COLLECTYMD` | 함수 | `object_definition.sql:75-97 (ELSE --고정)` | `Spec.md:197-231 (특히 199)` | 'CollectType = 1이면 고정 회수 유형입니다'로 적고 1~4 밖의 값 처리를 한 줄도 두지 않음. 산문 4개 섹션과 mermaid(360-364)가 모두 4분류 | CollectType은 tinyint NOT NULL이고 CHECK 제약 없음. 도메인 밖 값(0,5~255)이면 원본은 고정 산식, 이행본은 미정/NULL → 회수일이 갈림 → 그 경우 🔴. 호출 SP 1개 |
| `UF_GET_PGCommOption` | 함수 | `metadata.json:169 (UseState) · object_definition.sql:27-28 (WHERE에 UseState 술어 없음)` | `Spec.md:153-159` | '참조되지 않는 테이블 컬럼' 표가 SeqNo 한 행만 실음. 18개 누락. UseState는 명세서 229행 어디에도 없음 | WHERE가 UseState를 안 걸러 사용중지(2) 행도 옵션 공급원인데 명세서만으론 알 수 없음. Spec.md:55,151의 복수 행 무보장과 결합. 호출 SP 2개 |
| `UF_GET_SETTLE_EXCHANGERATE` | 함수 | `object_definition.sql:14,18,26-28,39` | `Spec.md:250 (연관 56,168-175,216-248)` | 'decimal(9,5) 반환 변수에 대입된 후 반환됩니다'로 대입 사실만 서술. 반올림 미명시. Spec.md:56 오류 방지 로직 행은 BasicSettleRate=0만 열거 | 호출 SP 1개(UP_UTIL_SETTLE_COMM_UPD:451, ForeignSettleAmt의 분모). double/무제한 decimal 이행 시 ROUND(...,2) 경계에서 원 단위가 갈림 |
| `UF_Get_CLComm4MobileCo` | 함수 | `object_definition.sql:31-35` | `Spec.md:118,154` | CRUD 요약(118)은 '통신사 코드가 1~6에 해당하지 않을 때 통합 수수료율을 조회', 실행 조건 칸(154)은 'ELSE 분기'만 — 바깥 행 존재라는 선행 조건 누락 | 바깥 요율 행이 없는 조합에서 원본은 0, 이행본은 CommissionRate 적용 → 금액 차이. Spec.md:109,217,250은 옳게 적음. 호출 SP 1개 |
| `UIF_SettleYMD` | 함수 | `object_definition.sql:107` | `Spec.md:118,37` | THoliday 행(120)에만 NOLOCK 명시, TSettlePeriodMst 행(118)에는 없음. 37행이 '테이블 조회에 NOLOCK 힌트가 있으므로'로 뭉갬 | 스캔 자리별 힌트 차이가 평탄화 — TSettlePeriodMst에 NOLOCK 누락되거나 SPT_VALUES에 잘못 붙음. 호출 객체 3개 공통 |
| `UIF_SettleYMD` | 함수 | `object_definition.sql:31-32,160` | `Spec.md:35` | NULL 사유를 '정산주기 조회 결과 없음'·'휴일 후보 조회 결과 없음' 둘로만 열거 | 2999년 이상 거래일에 NULL이 아닌 값을 기대하게 됨. 추적성 손실 |
| `UIF_SettleYMD` | 함수 | `object_definition.sql:125,140` | `Spec.md:35` | '변수에 값이 할당되지 않아 NULL일 수 있습니다'로 서술 — T-SQL 의미와 어긋남 | 이 함수에선 결과가 같으나 '직전 값 유지' 의미로 이행하면 다른 문맥에서 갈림 |
| `UIF_SettleYMD` | 함수 | `object_definition.sql:85-89` | `Spec.md:141` | '달력일 기준이면'으로 값 2에 묶이는 표현. 같은 문서 Spec.md:138은 '그 외에는'으로 올바르게 여집합 사용 — 문서 내 불균형 | SettleDayFlag는 NOT NULL DEFAULT((0))이라 0이 존재 가능. flag==2로 판정하면 어디에도 안 걸리거나 영업일 분기로 오락 → OutYMD 달라짐. 0 적재 배포에선 🟠 상당 |
| `UIF_SettleYMD` | 함수 | `object_definition.sql:61,86` | `Spec.md:121` | CRUD 표가 '입력 @pi_strYMD, SettleDay'만 실음(61행 인자). 조건 칸은 두 지점을 옳게 열거 — 인자 칸만 어긋남 | 표만 보면 고정 정산 영업일 기준일이 전월 말일이 아니라 거래일이 됨. Spec.md:141은 옳게 서술 |
| `UP_UTIL_SETTLE_COMM_UPD` | SP | `object_definition.sql:145` | `Spec.md:54` | 활성 NOLOCK 29개 중 표가 담는 28개는 전수 전재했으나 표 밖의 이 1개만 산문에서도 누락. Spec.md:54는 ' ' UNION ALL까지 옮기면서 힌트만 빠뜨림 | 이관 시 강제취소 목록 조회가 공유 잠금을 검. 행 집합·금액은 동일. 동시 실행 구성에서는 차단·교착 유발 여지 |
| `UP_UTIL_SETTLE_COMM_UPD` | SP | `object_definition.sql:43-44` | `Spec.md:367` | 로직 흐름 요약 순서 2의 '대상 조건 및 주요 수식' 칸이 PG 집합만 적고 두 술어 누락. 순서 3·9·10·11·12·14는 상태 술어를 적음 — 문서 내 불균형 | 이 행만 보고 이관하면 할부이자 갱신 대상이 넓어져 금액이 달라짐. 집합 술어 표(243-245)와 Spec.md:49가 전수로 실어 문서 전체에서는 보존 |
| `UP_UTIL_SETTLE_EXCEPTION_PROC` | SP | `object_definition.sql:187-199` | `Spec.md:118` | CRUD SELECT 대상 표의 TPGCMRate 행이 사용 문장에 UPDATE 6을 포함 | 존재하지 않는 결합이 생겨 결합 실패 시 INIBANK 최저수수료 갱신 대상이 통째로 사라짐(🟠 후보). DML 범위(288)·잠금 힌트(399)·갱신 6 절·로직 흐름 7행이 단일 테이블임을 확정해 오독 차단 |
| `UP_UTIL_SETTLE_EXCEPTION_PROC` | SP | `object_definition.sql:145-148` | `Spec.md:117` | TClientSettleRate 행이 UPDATE 3과 4를 한 줄로 묶어 '고객사, PG명, 쇼핑몰, 정산일 조건으로 결합됩니다'로 적음 | UPDATE 4에 MallID 결합을 넣으면 최저수수료 대상이 줄고 다른 요율이 매칭되면 CLCOMM·CLVT가 갈림(🔴/🟠 후보). DML 범위 표(286)가 조인 키를 기계 확정해 오독 차단 |
| `UP_UTIL_SETTLE_EXCEPTION_PROC` | SP | `object_definition.sql:227,239,247,249,260,269,272,279,280,288,292,299,300,302,310,312,332,334,335,337,345,353,358,365,372,374,375,381,390,409,422,423,432,450,477,500,523` | `Spec.md:37-76` | '헤더 및 원본 주석 기록' 표가 40행만 싣고 37행 누락(실린 40행은 줄·문구 일치). 누락이 220행 이후에 몰림. 대표: 312(SP 간 책임 분담), 334·335(판가/원가 정책 변경 일자), 337, 345(카드사 원가 VAT 없음 — 갱신 13 PGVT=0 근거), 279(카카오머니만 강제회수 — UPDATE 10 A.TID=A.CID 근거), 423(UPDATE 15 MobileCo 필터 근거), 432(Payco 취소기한 180일 후 로직 제거 가능 — 폐기 예정 표시), 523 | 추적성·의도 손실. 조건식·SET 식은 기계 확정 표와 갱신 N 절에 원문 보존되어 금액·행 집합 불변. 432의 폐기 예정 표시와 312의 SP 간 분담이 사라지면 이관 판단 근거가 없어짐 |
| `UP_UTIL_SETTLE_EXCEPTION_PROC` | SP | `object_definition.sql:529` | `Spec.md:277,491` | 하위 질의를 서술한 두 자리가 YMD·USESTATE=1·OUTSTATE=9만 적고 NOLOCK 누락. Spec.md:275의 NOLOCK 산문은 일반론 | 원본 NOLOCK 41자리 중 40자리는 표가 정확히 실었고 표 밖으로 새는 것은 이 한 자리. 이관 시 동시성 동작이 달라짐. 행 집합·금액은 통상 동일 |
| `UP_UTIL_SETTLE_EXPECT_PROC` | SP | `object_definition.sql:205` | `Spec.md:101-102` | SELECT 대상 테이블 표의 TSettleMst 행이 18개뿐 — EDIReqYmd 누락(OutYMD 행과 PLTID 행 사이 자리) | 기준값과 어긋남. 갱신 9 SET 표(202)와 TPLCardEDIMst.ReqYMD 행(126)에 남아 대입은 소실 안 됨. 컬럼 인벤토리 추적성 손실 |
| `UP_UTIL_SETTLE_EXPECT_PROC` | SP | `object_definition.sql:37 (술어는 58행 A.INSTATE = 0)` | `Spec.md:337` | 로직 흐름 2행이 'UPDATE 1과 같은 기준일·결합·회수구분 조건에 더하여…'로 요약하는데, 1행이 UPDATE 1 조건을 명시 열거해 세 항목이 YMD·조인·CollectFlag=1만 가리켜 A.INSTATE=0이 어느 쪽에도 안 잡힘. 3행(UPDATE 3)은 같은 술어를 명시 — 문서 내 불균형 | 이미 회수된(InState=1) 행까지 잡혀 INYMD가 재계산 → 대상 행 집합이 넓어짐. 집합 술어 표(248)가 정본을 보유해 🟡, 그 표가 없었다면 🟠 |
| `UP_UTIL_SETTLE_EXPECT_PROC` | SP | `object_definition.sql:230` | `Spec.md:137` | UPDATE 대상 테이블 도입부가 'UPDATE 1~4, 6~11은 FROM 절에 대상 별칭을 포함합니다'로 단정. 자신의 잠금 힌트 표(321)는 별칭 칸을 '-'로 적어 어긋남 | 별칭 기반 자기조인 갱신으로 오독 여지. 갱신 10 표(204-209)가 정확해 금액·행 집합 불변 |
| `UP_UTIL_SETTLE_EXPECT_PROC` | SP | `object_definition.sql:69-80` | `Spec.md:109` | TPGCMRate.CollectPeriodID 행이 'UPDATE 1~3에서 … 함수 인자로 사용한다'로 적어 UPDATE 3도 포함되는 것처럼 읽힘 | UPDATE 3에 없는 INYMD 대입이 생길 수 있음. 갱신 3 SET 표(155-159)와 참조 함수 표(280-281)가 정본 유지 |
| `UP_UTIL_SETTLE_INS` | SP | `object_definition.sql:243-249` | `Spec.md:99 (부재)` | 개요·CRUD·로직 흐름 어디에도 이 죽은 블록의 존재가 없음. 재료 부재 아님 — AstInsertMappings.SourceQueryBlock에 CLIENTFEEAMT·FeeCharge가 각 4회 실려 있었음 | 계약의 '주석 처리된 블록' 항목이 빈 칸. 살아 있는 식은 정확해 금액 불변. 환불 수수료 계산 변경 이력 추적 근거 소실 |
| `UP_UTIL_SETTLE_INS` | SP | `object_definition.sql:248` | `Spec.md:329` | '원본 실행 주석 기록' 표가 살아 있는 주석들과 구분 없이 한 행으로 실음 | 환불 분기 CLETC 근거로 읽으면 뺄셈 로직을 되살릴 수 있음. Spec.md:101이 살아 있는 식을 정확히 적어 교정 가능 |
| `UP_UTIL_SETTLE_INS` | SP | `object_definition.sql:264,265,299` | `Spec.md:303-334` | '원본 실행 주석 기록' 표가 이 3건을 누락. 나머지 28건+헤더 12건은 실려 전수처럼 보임. 상류 프롬프트 체크리스트 자체에 누락 | 환불 분기만 도입 일자·사유 주석이 없는 것처럼 보임. 필터 자체는 Spec.md:253·집합 술어 표에 남아 행 집합은 보존 |
| `UP_UTIL_SETTLE_INS` | SP | `object_definition.sql:159,209-221,285-298` | `Spec.md:70` | '위 결합 조건과 …를 적용합니다'로 되짚어, '위 결합 조건'이 필터를 포함하는지 문장이 가르지 않음 | 앞 열거로 읽으면 부분취소·환불에 A.YMD 필터가 추가돼 기준일 이전 승인 건이 통째로 빠짐(🟠 후보). Spec.md:51,252,253이 분기별 기준일 컬럼을 분리해 교정 |
| `UP_UTIL_SETTLE_INS_EXTRA` | SP | `object_definition.sql:211-218,233-235,254-264,205` | `Spec.md:161,169,185` | 갱신 1·2·3 각각에 '여러 원천 행이 결합될 경우 … 비결정적일 수 있습니다'라는 같은 문장을 붙임. 정작 행 증식이 가능한 INSERT 1의 LEFT OUTER JOIN TPGProperty Y ON X.PGName = Y.PGName(205, TPGProperty PK는 SeqNo뿐이라 PGName 비유일)은 어디에서도 언급 안 함 | 원본에 없는 비결정성을 세 문장이나 선언해 임의 타이브레이크(TOP 1·DISTINCT·ROW_NUMBER) 삽입을 유도. 반대로 실제 위험 자리는 무경고 — 서술 수준 불균형. PK 제약이 오독 경로를 막아 🟡 |
| `UP_UTIL_SETTLE_INS_EXTRA` | SP | `object_definition.sql:30-31` | `Spec.md:96` | '일부 읽기와 갱신 대상 별칭에 WITH(NOLOCK) 힌트가 있습니다'라는 뭉갠 한 문장 — 어느 읽기인지, 사전 확인이 포함되는지 판별 불가. '갱신 대상 별칭'도 부정확(갱신 3의 264행 FROM에는 별칭이 없고 표의 별칭 칸도 '-', 갱신 4·5는 FROM 자체가 없으며 DELETE 대상에도 힌트 없음) | 문장당 한 칸으로 뭉갠 산문 — 표가 담지 않는 유일한 잠금 힌트 자리가 명세서에서도 미확정. 사전 확인 조회의 더티 리드 허용 여부가 근거 없이 결정됨 |
| `UP_UTIL_SETTLE_INS_EXTRA` | SP | `object_definition.sql:308` | `Spec.md:72` | '(0:영세, 1:중소1, 2:중소2, 3:일반)'으로 인용 — 3:중소3이 사라지고 4:일반이 3:일반으로 바뀜. 같은 표의 다른 다섯 행(46,67,68,69,71 — DDL 37,222,239,268,283)은 올바르게 인용 | 코드값 3·4의 의미가 뒤집혀 읽히고 ISNULL(...,4)의 4가 '일반'이라는 근거가 문서 안에서 무너짐. 실제 술어는 '명세 반영' 칸과 집합 술어 표(251)가 보존 |
| `UP_UTIL_SETTLE_INS_EXTRA` | SP | `object_definition.sql:190,193,209,232,252,274,293,295-297` | `Spec.md:33-74` | '원문 주석 기록 및 구현 대조' 표가 라인 3~312의 38행을 실어 전수를 표방하는데 계산 규칙과 변경 이력을 담은 이 아홉 자리만 빠짐 | 295-297은 갱신 5(300-311)의 CAST(CLComm/@v_valIncVat AS INT) 삼중 절사식이 '수수료합계→공급가액→부가세' 규칙임을 설명하는 유일한 원문 근거. 193은 조인 키가 요청일이 아니라 원거래일(A.OrgYMD = B.YMD, 194-195)인 이유, 252는 갱신 3의 아홉 컬럼 부호 반전(254-263) 근거. 계산식 자체는 기계 확정 표와 로직 흐름에 보존되어 금액 불변 |
| `UP_UTIL_SETTLE_PROC_ETC` | SP | `object_definition.sql:62` | `Spec.md:143,79` | 로직 흐름 2와 CRUD 표 모두 GROUP BY·집계만 옮기고 정렬 미서술. 문서 전체에서 ORDER BY·정렬 언급 0건(기계 확정 DML 범위 표 헤더 제외). 커서 SELECT는 표 관할 밖이라 기준값은 DDL 원문 | 행 처리 순서가 비결정적이 됨. YMD 고정으로 최종 누적 금액은 불변이나, 137-141이 불일치 시 -3으로 즉시 RETURN하므로 어느 행까지 처리된 뒤 롤백되는지·재처리 지점이 갈림 |
| `UP_UTIL_SETTLE_PROC_ETC` | SP | `object_definition.sql:116-128` | `Spec.md:155` | 로직 흐름 5가 '원천 TSettleMst의 고객사·지급일·상태 2 금액 합계'로 조인 2개와 TaxFGBill 필터를 통째로 지운 단일 테이블 요약(프롬프트 규칙 18의 다중 소스 결합 축약 금지 위반). Spec.md:44,79,80에는 옳게 남음 | 요약만 보고 이행하면 @v_intPostChkAmt1이 커져 @v_intPostChkAmt2와 상시 불일치 → 정상 데이터에서도 -3 롤백. 요약만 읽는 경로를 전제하면 🔴 후보 |
| `UP_UTIL_SETTLE_SUMMARY_ETC` | SP | `object_definition.sql:77,129` | `Spec.md:55` | '삭제 실패 문자열'·'재등록 실패 문자열'로 요약, 리터럴이 명세서 전체에 한 번도 없음 | @po_strErrMsg 값이 레거시와 달라져 로그 파싱·알림 규칙을 쓰는 호출자가 조용히 어긋남. 금액·행 집합 불변 |
| `UP_UTIL_SETTLE_SUMMARY_ETC` | SP | `object_definition.sql:55,137` | `Spec.md:163` | 로직 흐름 2단계가 13개만 나열 — @v_intOutState(A.OUTSTATE) 누락. '정산상태' 한 항목으로는 UseState·OutState 둘을 못 덮음 | 이 문단으로 FETCH INTO 순서를 재구성하면 14번째 자리가 밀려 컬럼-변수 대응이 어긋남. 내부 변수 표(Spec.md:72)에 정본 존재 |
| `UP_UTIL_SETTLE_SUMMARY_EXTRA` | SP | `object_definition.sql:26` | `Spec.md:87` | 내부 변수 표 매핑 칸이 'TExtraSettleByTX.YMD가 아니라 실제 대상인 …'으로 시작 — 존재하지 않는 테이블을 부정문으로 끌어들임 | 폐포에 없는 테이블을 찾아 헤매거나 부정문을 놓쳐 대상으로 오독. 술어·매핑 자체는 정확 |
| `UP_UTIL_STAT_PGCOLLECT_INS` | SP | `object_definition.sql:26-28,115-117,120-122` | `Spec.md:25,167,178,180` | '인수 없는 RETURN'을 성공 경로(25,180)에만 명시하고 오류 경로(167,178)는 'RETURN합니다'로만 적어 반환 상태 값을 밝히지 않음. 헤더 주석 '=0->성공, <>0->실패'(6행) 불일치 서술(27)도 @po_intRetVal만 다룸 | '오류·반환 코드 전체 집합' 항목이 부분만 남음. EXEC @rc = …로 상태를 보는 호출자에게 실패 시에도 0이 온다는 사실이 사라져, 오류 신호 경로를 재현 못 하거나 반환 상태를 -1로 바꿔 호출자 계약을 바꿀 여지. 금액·행 집합 불변 |
| `UP_UTIL_STAT_PGCOLLECT_INS` | SP | `object_definition.sql:22-23,74,92,109` | `Spec.md:23 vs 102,58,61,62` | 개요(23)는 '지정 정산일의 기존 통계 데이터를 삭제하고'인데 DELETE 절(102)은 '동일한 회수일자', 원천 필터(58,61,62)는 INYMD/COLLECTYMD — 문서 안에서 명칭이 갈림 | 기준일의 의미가 지급일자로 오독될 수 있음. 술어 자체는 다른 자리에 정확히 보존되어 대상 행 집합 불변 |
| `UP_Util_Settle_Summary` | SP | `object_definition.sql:128,165,205` | `Spec.md:118,151,184` | 세 매핑 표의 YMD 행이 '조건을 만족하는 거래일입니다'로만 적고 그룹화 키임을 밝히지 않음. INSERT 1의 YMD 행(Spec.md:86)은 '그룹화 키입니다'로 표기 — 문서 내부 불균형. 이 명세서에서 GROUP BY 키를 담는 유일한 자리가 매핑 표 설명 칸 | 표로 GROUP BY를 재구성하면 INSERT 2·3·4에서 YMD 누락. 세 문장 모두 WHERE가 YMD를 단일값에 고정해 금액은 불변이고 컴파일 오류로 즉시 드러남 |
| `UP_Util_Settle_Summary_AcqManual` | SP | `object_definition.sql:73-79` | `Spec.md:174 (같은 붕괴가 Spec.md:53에도)` | 로직 흐름 5-3이 14개만 열거하며 서로 다른 두 상태 컬럼(USESTATE 거래상태 0:정상/1:취소, OUTSTATE 지급상태 0/1/2/5/9)을 '상태' 하나로 뭉갬. Spec.md:53의 '집계 단위' 칸도 같은 방식으로 뭉갠 뒤 '등을 포함한'으로 닫음 | 산문만 보고 이행하면 그룹 키 하나가 빠져 USESTATE·OUTSTATE가 다른 행이 한 집계 행으로 병합돼 TSettleByOUT의 행 수와 금액 배분이 달라짐(🟠 후보). INSERT 매핑 표(116-117)가 두 컬럼을 각각 '그룹화 기준'으로 명시해 복원 가능 — 이행자가 매핑 표를 함께 읽는다는 전제로 🟡 |
| `UP_UTIL_SETTLE_SUMMARY_EXTRA` | SP (보류 종결) | `object_definition.sql:31,42-45,78-81,94-97,131-134,147-150,185-188,202-205,234-237,240,242-245` | `UP_Util_Settle_Summary/docs/Spec.md:267` | '오류 시 코드 4000부터 4008을 출력 파라미터에 설정합니다'라는 연속 범위 서술이 4000을 단계 코드의 첫 값처럼 읽히게 해, 여덟 IF @@ERROR<>0가 전부 BEGIN TRY 안이라 실제로는 CATCH의 4000만 지배적이라는 사실을 지움(4001~4008은 실행 경로상 사실상 도달 불가) | 유일한 호출자가 @po_intRetVal <> 0만 보고 롤백해 행 집합·금액은 불변. 단계별 코드를 살려 이행하면 레거시가 4000을 내던 자리에서 4001~4008이 나와 모니터링·상위 로깅이 다른 값을 봄 |

🟡 총 56건 = 개별 41건(위 표, 보류 종결 단위 1건 포함) + 4-1로 접은 14건 + 교차 1건(4-2). 직전 판까지 이 표에 있던 `UF_GET_COMM4CLIENT` Spec.md:197 행은 실행 대조로 🔴이 되어 4-0-1로 옮겼다.

⚪(정보) 총 55건(보류 종결 1 · 재검증 3 포함) = 4-1로 접은 15건 + 나머지 40건. 나머지는 도달 불가 분기 열거, 원문 인용의 공백·대괄호 정규화, 타입 폭 불일치, 원천 메타데이터 오기 경고 등이며 금액·대상 행 집합을 바꾸지 않는다. 전문은 `consistency/.cache.json`의 각 단위 `defects` 배열에 양쪽 앵커와 함께 남아 있다.
### 4-1. 전 객체 공통 결함

같은 원인이 여러 객체에 반복된 것은 여기에 한 번만 적고 해당 객체를 나열한다. 접지 않으면 이 다섯 무리만으로 🟡·⚪ 29건이 되어 🔴 1건과 🟠 6건을 가린다.

**F1 — 파서가 확정한 값을 "단언할 수 없습니다"로 되짚음 (🟡 7건 · ⚪ 2건)**

`StaticAnalysis`의 `ThreePartObjectReferences`·`LinkedServerReferences`가 빈 배열이고 `ReferencedTables`가 소속 DB까지 해소해 두었으므로 "크로스 DB 참조가 아니다"는 **확정값**인데, 명세서가 이를 "원본 구문만으로는 단언할 수 없습니다"로 되돌린다. `axis-a.md` 3-1절이 `객체 선언` 표의 `(없음)`을 "확인할 수 없음"으로 되짚는 것을 결함으로 규정한 것과 같은 형태다. 여러 객체가 같은 문서 안에서 앞줄은 확정 서술, 뒷줄은 유보로 자기모순을 일으킨다.

| 객체 | 산출물 앵커 | 등급 |
|---|---|---|
| `SETTLE_CARD_DB.UF_GET_COMM4PG` | Spec.md:129,165 | 🟡 |
| `SETTLE_CARD_DB.UF_GET_COMM4PG4INTEREST` | Spec.md:64 | 🟡 |
| `SETTLE_CARD_DB.UF_GET_EXTRACOMM4CLIENT` | Spec.md:108 | 🟡 |
| `UF_GET_INCVTAXRATE` | Spec.md:93 | 🟡 |
| `UF_GET_PGCommOption` | Spec.md:43 · 165-166 | 🟡 · ⚪ |
| `UF_GET_SETTLE_EXCHANGERATE` | Spec.md:30 | 🟡 |
| `UF_Get_CLComm4MobileCo` | Spec.md:28 | 🟡 |
| `SETTLE_CARD_DB.UF_GET_COMM4CLIENT4INTEREST` | Spec.md:35 | ⚪ |

**영향** — 금액은 바뀌지 않는다. 확정된 사실이 재조사 대상으로 되돌아가고, 이행 담당자가 불필요한 3부 한정자나 별도 커넥션 분기를 넣을 여지가 남는다. `UF_GET_INCVTAXRATE`(호출 SP 5개)와 `UF_GET_PGCommOption`(2개)은 여러 SP의 명세서에 동시에 걸린다.

**F2 — 실재하는 스키마 컬럼을 "없습니다"로 단정 (🟡 3건)**

프롬프트가 실어 준 `Dependencies` 컬럼 목록에 **있는** 컬럼을 명세서가 "제공 스키마에 없다"고 단정한다. 프롬프트 자신이 "참조 컬럼이 스키마에 없다고 기술하지 마십시오"라고 못박은 자리라 지시 위반이기도 하다.

| 객체 | 산출물 앵커 | 없다고 단정한 컬럼 | 실재 근거 |
|---|---|---|---|
| `UP_UTIL_SETTLE_PROC_ETC` | Spec.md:36 | `TClient.ClientIDType` | metadata.json:682 |
| `UP_UTIL_SETTLE_INS_EXTRA` | Spec.md:125 | `TSettleMst.ProductName` | Dependencies 59개 중 15번째 |
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | Spec.md:104 | `TSettleMst.ProductName` | 같음 |

**영향** — `ProductName`은 두 SP에서 상수 `'영중소차액정산'`을 적재하는 자리다. 이 경고를 따라 매핑을 드롭하면 상품명이 NULL이 되어 차액정산 행의 식별 표기값이 사라진다(컬럼이 NULL 허용이라 INSERT는 실패하지 않으므로 조용히 넘어간다). `ClientIDType`은 내부테스트용 고객사 배제 요건이 "컬럼이 없어 복구 불가"로 오판돼 재검토 대상에서 지워진다.

**F3 — 무이자 할부 두 함수의 반올림 방향 미명시 (🔴 1 · 🟡 1)**

> **이 무리는 실행 대조로 범위가 좁혀졌다.** 직전 판에서는 외부 `SETTLE_CARD_DB` 수수료 함수 5개 공통 결함(🔴 1 · 🟡 2 · ⚪ 2)으로 적었으나, 실측 결과 **7개 중 2개에만 해당**한다. 나머지는 명세서 서술이 원본과 맞았고 재검증 3단위가 정합으로 닫았다(4-3).

일곱 형제 함수가 똑같아 보이는 금액 산식을 쓰는데 **괄호 하나로 반올림 동작이 갈린다.** `decimal/numeric`이 `money`보다 데이터 형식 우선순위가 높아, 리터럴 `100.0`(= `numeric(4,1)`)이 `CAST` 안에 있으면 `money` 피연산자가 `numeric`으로 승격돼 결과가 절사되고, 밖에 있으면 `money * money`가 그대로 남아 반올림된다.

```
CAST(@pi_intTxAmt * (@v_intCommission / 100.0) AS INT)   -- numeric → int : 0 방향 절사
CAST(@pi_intTxAmt *  @v_intFreeInterestRate    AS INT)   -- money   → int : 0에서 먼 쪽 반올림
```

같은 값으로 실측했다 — `10050 × 1.50%`가 앞은 `150`, 뒤는 `151`이다.

| 경로 | 함수 | 명세서 | 등급 |
|---|---|---|---|
| **money → 반올림** | `UF_GET_COMM4CLIENT4INTEREST`(:68) | Spec.md:138 「소수 부분은 반환값에 유지되지 않습니다」 — 사실과 반대로 읽힌다 | 🔴 (4-0-1) |
| **money → 반올림** | `UF_GET_COMM4PG4INTEREST`(:75) | 방향을 말하지 않는다(Spec.md:38·206-212·241) | 🟡 (4-0-2) |
| numeric → 절사 | `UF_GET_COMM4CLIENT`(:99) · `UF_GET_COMM4CLIENT4PARTIALCANCEL`(:86) · `UF_GET_COMM4PG`(:70) · `UF_GET_EXTRACOMM4CLIENT`(:87) · `UF_Get_ExtraCardCommissionAmt`(:62,63) | 서술이 원본과 일치하거나 중립 | 결함 아님 |

**영향** — 무이자 할부 수수료 두 자리에서 **건당 최대 1원**이 어긋난다. `UF_GET_COMM4CLIENT4INTEREST` → `UP_UTIL_SETTLE_EXCEPTION_PROC:357`의 `CLIntComm`, `UF_GET_COMM4PG4INTEREST` → 같은 SP `:363`의 `PGIntComm`이다. 식을 그대로 SQL로 옮기면 값이 보존되지만, 산술을 애플리케이션 계층으로 옮기며 `(int)` 캐스트나 `Math.Floor`를 쓰면 갈린다 — C#의 정수 캐스트는 절사이므로 **자연스러운 번역이 바로 틀린 쪽**이다. 나머지 다섯 함수는 반대로 C# 절사가 원본과 맞는다. 명세서 어디에도 이 갈림이 없다.

**F4 — 주석 건수를 "N건"으로 단언하면서 구분선 주석을 세지 않음 (⚪ 7건)**

`-----------------------------------------------------------------` 형태의 헤더 구분선도 T-SQL 한 줄 주석인데, 「원본 주석 보존」 표가 내용 주석만 세고 "주석 N건"이라 단언한다. 실린 내용 주석의 라인·원문은 전부 정확했다.

`SETTLE_CARD_DB.UF_GET_COMM4PG`(40 중 28 기재) · `UF_GET_INCVTAXRATE`(10 중 8) · `UF_GET_PGCommOption`(9 중 7) · `UF_GET_WORKDAY2`(11 중 9) · `UF_Get_CLComm4MobileCo`(10 중 8) · `UP_UTIL_STAT_PGCOLLECT_INS`(구분선 2줄) · `UP_Util_PG_Client_CMRate_Ins`(SSMS 배너 1줄).

**영향** — 없다. 구분선은 의미를 담지 않는다. 다음 감사자가 "N건 중 M건 누락"으로 재판정하지 않도록, **구분선을 세지 않는 것이 이 표의 규칙**임을 여기 한 번 못박는다. 이와 별개로 **내용 주석이 실제로 누락된 것은 4절의 개별 행**으로 남겼다(`UP_UTIL_SETTLE_EXCEPTION_PROC` 37행, `UP_UTIL_SETTLE_INS_EXTRA` 9자리, `UP_UTIL_SETTLE_INS` 3건) — 그쪽은 계산 규칙·변경 이력·SP 간 책임 분담을 담은 주석이라 성격이 다르다.

**F5 — mermaid 도식이 같은 문서의 산문·원본과 어긋남 (🟡 2 · ⚪ 4)**

흐름도가 대입 시점을 분기 뒤로 옮기거나, 값 선택 노드를 분기 노드로 겸용하거나, 종료 간선을 빠뜨린다. 여섯 자리 모두 같은 문서의 산문이 원본과 일치해 정본은 살아 있다.

`UF_GET_PGCommOption`(Spec.md:198,213-218 — `CASE` 1회 평가를 재분기로) · `UP_UTIL_SETTLE_PROC_ETC`(Spec.md:184,216 — `-3` 종료 노드에 나가는 간선 없음 / Spec.md:168,194-198 — `NOCOUNT` 순서) · `SETTLE_CARD_DB.UF_Get_ExtraCardCommissionAmt`(Spec.md:158-174) · `UF_GET_CLIENTSECTIONRATE`(Spec.md:169-173) · `UF_GET_SETTLE_EXCHANGERATE`(Spec.md:272,285-290).

**영향** — 반환값·금액은 갈리지 않는다. 도식만 보고 이행하면 종료 경로나 분기 우선순위를 오독할 수 있다.

### 4-2. 축 A 교차 — 명세서가 참조 함수를 다루는 자리

**대조한 것은 기계 확정 표 8자리다.** SP 6개는 표를 갖고, 함수 2개는 표가 없는 것이 정상이었다.

| 호출하는 객체 | 표 위치 | 표 행 수 | DDL 실측 | 판정 |
|---|---|---|---|---|
| `UP_UTIL_SETTLE_EXCEPTION_PROC` | Spec.md:349 / 353-381 | 29 | 29 | 정합 |
| `UP_UTIL_SETTLE_COMM_UPD` | Spec.md:279 / 283-305 | 23 | 23 | 정합 |
| `UP_UTIL_SETTLE_EXPECT_PROC` | Spec.md:276 / 280-288 | 9 | 9 | 정합 |
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | Spec.md:198 / 202-207 | 6 | 6 | 정합 |
| `UP_UTIL_SETTLE_INS_EXTRA` | Spec.md:256-264 | 5 | 5 | 정합 |
| `UP_UTIL_SETTLE_INS` | Spec.md:155 / 158-160 | 3 | 3 | 정합 |
| `UF_GET_COLLECTYMD` | 표 없음 (정상) | — | 2 (`SELECT` 대입문 안) | 정합 |
| `UIF_SettleYMD` | 표 없음 (정상) | — | 2 (`SELECT` 대입문 안) | **결함** |

**기계 확정 표는 75행 전부 정합이다.** 행 누락·과잉 0건, 중첩 호출은 바깥·안쪽이 각각 한 행으로 정상 분리됐고(`UP_UTIL_SETTLE_COMM_UPD` 라인 63은 3행), 인자 칸은 75행 모두 DDL 호출식 원문과 축자 일치했다(원문 공백·오타까지 보존). 다중 문장 TVF `UIF_SettleYMD`는 `ReferencedFunctions`에서 빠져 있는데도 `Dependencies` 기준으로 7행이 빠짐없이 실렸다. 명세서 링크는 전부 실재했고 `(명세서 없음)` 행은 0건이다. 표에 실린 함수의 반환값·분기·필터·기본값을 서술한 자리는 없었다 — 폐지된 「UDF 활용 규칙」류 산문 절도 남아 있지 않다.

**결함은 표가 아니라 표가 없는 두 자리에 몰렸다.** 함수 객체 2개는 유일한 DML(`INSERT … VALUES`)에 함수 호출이 없어 도구가 표를 싣지 않는다(`AiService.cs`의 `functionCalls.Count > 0`). 그 구간은 서술 금지 계약이 걸리지 않아 명세서가 `UF_GET_WORKDAY2`의 동작을 **산문으로** 서술하고 있고, 그 진위는 `axis-a.md` 3-2-1절이 맡았다.

#### 표의 사각지대에서 본 것

- **`UIF_SettleYMD` 🟡** — 원본 `object_definition.sql:61,86` ↔ 산출물 `Spec.md:121`. CRUD 표의 `UF_GET_WORKDAY2` 행이 참조 컬럼 칸에 「입력 `@pi_strYMD`, `SettleDay`」만 적어 **두 호출의 제1 인자 차이를 지웠다**. 61행은 `(@pi_strYMD, SettleDay)`, 86행은 `(CONVERT(VARCHAR(8), EOMONTH(DATEADD(M, SettleMonth-1, @pi_strYMD)), 112), SettleDay)`다. 이 표만 보고 이행하면 고정·`SettleCount = 1`·`SettleDayFlag = 1` 경로가 전월 말일 대신 거래일을 기준일로 넘겨 정산일이 통째로 달라진다(🔴 후보). `Spec.md:141`의 로직 흐름이 원문과 일치하게 보존해 🟡로 내렸다. **축 A 함수 단위와 교차 단위가 이 자리를 독립적으로 각각 잡았고 판정이 일치한다.**
- **`UIF_SettleYMD` ⚪** — `Spec.md:128`의 "음수 간격이면 방향 반전" 서술은 `UF_GET_WORKDAY2` 원본과 맞지만, 이 함수의 두 호출은 제2 인자로 `SettleDay`(`tinyint NOT NULL`)만 넘겨 음수가 될 수 없어 호출자 기준으로는 도달 불가 분기다.
- **`UF_GET_COLLECTYMD` ⚪ (보류 종결)** — 축 A 함수 단위가 「호출 사용자 정의 함수」 절의 다섯 진술을 보류로 남겼고, 교차 단위가 `UF_GET_WORKDAY2` 원본을 열어 **다섯 진술 전부 일치**로 닫았다(음수→`@v_intFlag = -1`, 0→`@v_intIdx = -1`, `THoliday.HYMD` 존재 시 간격 조정, `CHAR(8)` 반환, 주말 미판정). 인자는 원본의 오타 `+ + '01'`까지 그대로 옮겨져 있었다.
- **SP 6개의 사각지대는 전부 0건이다.** 호출 75건이 모두 `INSERT`/`UPDATE` 문장 범위 안이라 표가 전수를 덮는다. `UP_UTIL_SETTLE_COMM_UPD`에서 함수명이 2번 더 나오지만(`UF_GET_CLIENTID4TMONET`) 하나는 `/* */` 블록(라인 89-117) 안, 하나는 줄 주석(라인 419)이라 활성 호출이 아니고 명세서도 비활성으로만 서술했다. `UP_UTIL_SETTLE_INS`의 `/* */` 블록(라인 243-249)에도 함수 호출은 없다.

**기계 확정 표가 정합이라는 사실이 이 구간까지 검증됐다는 뜻이 아니다.** 위 목록은 표 밖을 DDL 원문으로 따로 훑은 결과이며, SP 6개에서 0건인 것은 "보지 않았다"가 아니라 "훑어서 없었다"는 뜻이다.

### 4-3. 재검증 — 실행 대조로 전제가 무너진 판정 3건

축 A 객체 단위 여럿이 **`money → int` CAST가 0 방향 절사라고 공유 전제**했다. 실행해 보니 `money → int`는 반올림이라 그 전제가 무너졌고, 그 전제 위에 선 판정 셋을 각각 재검증 단위에 넘겼다. **상위가 등급을 고치지 않았다 — 그것은 재판정이다.**

셋 다 **정합**으로 닫혔다. 이유는 같다: 세 함수의 산식은 `/100.0`이 `CAST` 안에 있어 애초에 `money → int`가 아니라 `numeric → int`이고, 그것은 실제로 0 방향 절사다. 직전 단위들의 **결론은 옳았고 근거가 틀렸다.**

| 재검증 단위 | 직전 판정 | 재검증 판정 | 확정된 근거 |
|---|---|---|---|
| `SETTLE_CARD_DB.UF_GET_COMM4PG` | 통과("절삭 서술 정확") | 정합 ⚪ | `numeric(38,6) → int`. Spec.md:39 「소수 부분은 제거됨」이 사실과 일치 |
| `SETTLE_CARD_DB.UF_GET_EXTRACOMM4CLIENT` | ⚪ | 정합 ⚪ | 같음. 실측 `1250 × 1.00% = 12.5 → 12`, `-1260 × 1.00% → -12` |
| `SETTLE_CARD_DB.UF_GET_COMM4CLIENT4PARTIALCANCEL` | ⚪ | 정합 ⚪ | 같음. 절사 방향이 부호에 무관해 부분취소 음수 경로가 등급을 가르지 않음 |

세 단위의 캐시 키는 해시 **3개**다 — 대상 함수의 DDL·`Spec.md`에 더해, 반환값이 어느 컬럼으로 적재되는지 확인하려고 읽은 **호출 SP의 DDL** 해시를 넣었다.

**재검증이 남긴 관찰 하나** — `UF_GET_EXTRACOMM4CLIENT` 단위가 판정 범위 밖에서 짚었다: 호출부 `UP_UTIL_SETTLE_INS_EXTRA4PLCARD:108`의 `CAST(ISNULL(X.CLCOMM,0) AS INT)`는 **진짜 `money → int`라 반올림**이다. 지금은 함수가 이미 정수값 `money`를 돌려주어 소수부가 없으므로 금액 차이가 없지만, 그 자리는 해당 SP 단위의 몫으로 남는다.

## 5. 축 B 결함

**검증하지 않았다. 그리고 지금은 돌려서도 안 된다.**

이번 실행은 인자로 축 A만 지정됐다. 그런데 그와 별개로, **`output/Jobs/POQSettlePrco20/agent/` 번들은 지금의 `Spec.md`로 만든 산출물이 아니다**(2026-08-22 사용자 확인). 명세서는 2026-08-21 22:58 ~ 2026-08-22 00:09에 재생성됐고 번들은 그보다 앞선 2026-08-19 21:03에 만들어졌다 — 1절의 캐시 전량 미스가 같은 사실을 해시로 보여 준다.

축 B의 기준값은 `Spec.md`이므로(`axis-b.md`), 지금 축 B를 돌리면 **이미 폐기된 명세서로 만든 계획서·단계 지시서를 현행 명세서와 대조**하게 된다. 거기서 나오는 불일치는 이행 결함이 아니라 세대 차이이고, 그것을 결함으로 세면 보고서가 통째로 오염된다.

**축 B는 명세서 결함을 고치고 Job 설계 문서를 다시 만든 뒤에 돌린다.** 그때 이번 축 A 결과에서 넘어가는 입력이 셋이다.

- 폐포에만 있는 SP 2개(`UP_UTIL_SETTLE_SUMMARY_EXTRA`, `UP_Util_Settle_Summary_AcqManual`)가 어느 단계에 흡수됐는지 — 흡수됐는데 명세서가 입력되지 않았다면 축 B의 결함이다.
- `[Approved Step List]`의 `Legacy:` 필드는 단계당 SP를 하나만 싣는다. 단계 본문 grep과 **합집합**을 취해야 흡수를 과소보고하지 않는다.
- `UP_Util_Settle_Summary`의 하위 SP 서술 두 자리(`Spec.md:265`·`:267`)는 **정본이 하위 SP 명세서와 이중화**돼 있고 `:267`에서 🟠이 나왔다(4-0-1). 단계 지시서가 어느 쪽 문서를 근거로 삼았는지 확인해야 한다 — 호출자 쪽 문장을 근거로 삼았다면 그 자체가 축 B의 결함이다.

## 6. 이 감사가 보증하지 않는 것

**축 B를 통째로 보지 않았다.** 계획서·단계 지시서와 `Spec.md`의 대조는 이 보고서에 없다. 5절 참조.

**실행 대조를 했다 — 6건 전부.** 로컬 Docker 컨테이너 `sql-server`(SQL Server 2022, 16.0.4255.1)에서 직접 실행했고, 각 단위가 스스로 남긴 판정 규칙을 그대로 적용했다. 상위가 등급을 고른 자리는 없다.

| 대상 | 실행 결과 | 단위 규칙 적용 |
|---|---|---|
| `UF_GET_COMM4CLIENT4INTEREST` | `309` | 🔴 **확정** — money 경로, 반올림 |
| `UF_GET_COMM4CLIENT` | `THIRD_RAN` | 🟡 → 🔴 **승격** — `IF`가 `@@ROWCOUNT`를 리셋해 3차 조회가 항상 돈다 |
| `UP_UTIL_SETTLE_INS_EXTRA` | `<NULL>` | 🟠 **확정** — 원본은 무결과 시 후속 DML 0행 |
| `UF_GET_SETTLE_EXCHANGERATE` | 대입 전 `417.16049003100000000` → 후 `417.16049`, 금액은 둘 다 `23971.59` | 🟡 **확정, 🔴 아님** |
| `UF_Get_ExtraCardCommissionAmt` | `<NULL>` 실재, 호출 인자 `TExtraTxMst.TxAmt`는 실 DB에서 `money NOT NULL` | ⚪ **유지** |
| `UF_GET_COMM4PG4INTEREST` | `289` | 🟡 **유지** — money 경로, 명세서 표현이 중립 |

이 실행이 **판정을 하나 승격시키고 셋을 되돌렸다**(4-3). 앞으로 등급이 갈리는 자리는 이 컨테이너로 닫을 수 있다.

**실행한 것과 하지 못한 것의 경계가 분명하다.** 붙은 인스턴스의 네 DB는 **스키마만 있고 테이블에 데이터가 없다**(2026-08-22 사용자 확인). 운영 데이터는 접근할 수 없다. 따라서 이 감사가 실행으로 확정한 것은 두 종류뿐이다.

- **타입·연산 의미** — `money → int`와 `numeric → int`의 반올림 방향, `IF` 문의 `@@ROWCOUNT` 리셋, 무결과 집계 대입의 NULL, `decimal(9,5)` 대입 시 반올림. 데이터와 무관해 결론이 확정적이다.
- **스키마 사실** — 컬럼 타입·널 허용·PK·인덱스. `TExtraTxMst.TxAmt`가 `money NOT NULL`이라는 확인이 여기 속하고, ⚪ 등급 유지의 근거가 됐다.

**데이터에 의존하는 주장은 하나도 검증하지 못했다.** 특히 🔴 `UF_GET_COMM4CLIENT`의 영향("같은 `ClientID`+`CardCPID`에 수수료율이 다른 행이 둘 이상이면 금액이 갈린다")이 운영에서 실제로 몇 건인지는 셀 수 없었다 — 0건일 수도, 전건일 수도 있다. 등급은 그 분포를 모른 채 "갈릴 수 있다"를 근거로 매긴 것이다. 같은 한계가 `UIF_SettleYMD`의 `SettleDayFlag = 0` 적재 여부, `UF_GET_COLLECTYMD`의 `CollectType` 도메인 밖 값 존재 여부에도 걸린다(바로 아래 항목).

**실제 데이터로 SP를 돌려 이행 코드와 결과를 대조한 적도 없다.** 이 감사는 문서 대조이지 동작 대조가 아니다.

**등급이 배포 구성에 달린 항목이 3건 있다.** 실제 배포를 확인하지 않았고, 규정대로 높은 쪽으로 매겼다.

- `UP_UTIL_SETTLE_SUMMARY_ETC` 🟠 — `DECLARE CURSOR`에 `LOCAL`이 없어 커서 범위가 `default_to_local_cursor`에 달려 있다. GLOBAL(SQL Server 기본값)이면 🟠, LOCAL이면 🟡.
- `UIF_SettleYMD` 🟡(`Spec.md:141`, `SettleDayFlag`) — `TSettlePeriodMst.SettleDayFlag`는 `NOT NULL DEFAULT ((0))`이라 값 0이 적재될 수 있다. 실제로 적재된 배포에서는 🟠 상당이다.
- `UF_GET_COLLECTYMD` 🟡(`Spec.md:197-231`) — `CollectType`에 CHECK 제약이 없어 도메인 밖 값(0, 5~255)이 저장되면 🔴이다.

**보류는 전부 닫혔다.** `UP_Util_Settle_Summary` 축 A 단위가 하위 SP 서술 두 자리를 정본 이중화 위험으로 보류에 넘겼고, 둘 다 해당 SP의 원본으로 종결했다.

- `Spec.md:265`(`UP_Util_Settle_Summary_AcqManual`) — 그 SP의 축 A 단위가 네 주장을 원본과 대조해 **전부 일치**로 닫았다. 서술되지 않은 것(성공 시 `@po_intRetVal = 0`, 커서 대상 필터)은 틀린 서술이 아니라 요약의 생략이다.
- `Spec.md:267`(`UP_UTIL_SETTLE_SUMMARY_EXTRA`) — 별도 보류 종결 단위가 다섯 주장을 쪼개 대조했고 **🟠 1 · 🟡 1 · ⚪ 1**이 나왔다(4-0-1 및 4-0-2). 주장 1·2·4(대상 범위, 요청일 조회, 네 테이블 DELETE→INSERT 구조)는 원본과 어긋나지 않았고, 어긋난 것은 주장 3(술어 열거가 부분집합)과 주장 5(반환 코드의 우선관계)다.

두 자리 모두 **하위 SP 동작의 정본이 두 문서에 갈려 있다**는 구조 문제가 남는다. 호출자 명세서의 문장을 「호출한다 + 링크」 수준으로 줄이고 정본을 각 하위 SP의 `Spec.md`로 못 박으면, 위 🟠의 등급 전제도 함께 닫힌다.

**읽지 않은 파일.** 피호출 함수의 `Spec.md`는 교차 단위가 열지 않았다(계약상 표의 링크 실재 여부만 확인한다). 축 A 교차에서 금지된 동작 서술이 발견되지 않은 6개 SP에서는 피호출 함수의 원본 DDL도 열지 않았다 — 그 함수들은 축 A 객체 단위가 별도로 전문 대조했으므로 폐포 안에서 미검증으로 남은 파일은 없다. `output/Objects/`·`output/Procedures/` 전역 목록은 이 Job의 대상이 아니므로 열거하지 않았고, 폐포 밖 객체는 어느 것도 검증하지 않았다.

**두 단위의 판정이 갈린 자리는 없었다.** `UIF_SettleYMD`의 `Spec.md:121`은 축 A 객체 단위와 교차 단위가 독립적으로 같은 결함을 같은 등급으로 잡았다 — 상충이 아니라 일치다.
