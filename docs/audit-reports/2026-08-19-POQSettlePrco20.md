# POQSettlePrco20 산출물 정합성 감사

감사일: 2026-08-19 · 대상 산출물 작성일: 2026-08-19 20:50:24

## 1. 판정

| 축 | 판정 | 단위 수 | 신규 검증 | 캐시 재사용 | 검증 불가 |
|---|---|---|---|---|---|
| A (원본 DDL ↔ Spec.md) | **결함** (🟠 4 · 🟡 12 · ⚪ 10) | 14 | 14 | 0 | 0 |
| B (Spec.md ↔ 단계 지시서) | **미실행** | 17 | 0 | 0 | — |

축 A만 실행했다. 축 B는 이 보고서가 다루지 않는다(6절).

캐시 재사용이 0인 이유: 이전 감사(POQSettleProc16, 2026-08-18 00:28) 이후 명세서 14개가 전부
재생성되어(2026-08-18 20:23 ~ 2026-08-19 11:03) 축 A 키의 `Spec.md` 해시가 모두 바뀌었다.

## 2. 검증 대상 확정

소비 명세서 12개는 `raw/prompt-context.md`의 `^Filename:` 행에서 읽었다(`Feedback_Log.txt` 제외).

```
UP_Util_PG_Client_CMRate_Ins, UP_UTIL_SETTLE_INS, UP_UTIL_SETTLE_CANCEL_INS,
UP_UTIL_SETTLE_EXCEPTION_PROC, UP_UTIL_SETTLE_COMM_UPD, UP_UTIL_SETTLE_EXPECT_PROC,
UP_UTIL_SETTLE_INS_EXTRA, UP_UTIL_SETTLE_INS_EXTRA4PLCARD, UP_UTIL_STAT_PGCOLLECT_INS,
UP_Util_Settle_Summary, UP_UTIL_SETTLE_SUMMARY_ETC, UP_UTIL_SETTLE_PROC_ETC
```

**중첩 SP 전개**: `dbo.UP_Util_Settle_Summary`의 DDL 221·230행이 두 하위 SP를 `EXEC`로 호출한다.

| 하위 SP | 소비 집합에 있는가 | 조치 |
|---|---|---|
| `dbo.UP_Util_Settle_Summary_AcqManual` | 없음 | 축 A 대상에 포함 |
| `dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA` | 없음 | 축 A 대상에 포함 |

이 둘은 최상위 실행 순서에 포함되지 않는 하위 호출이므로 **선택에서 빠진 것이 운영상 정상**이다.
축 B 결함으로 보고하지 않는다. 다만 상위 명세서가 하위 로직을 요약 서술로만 담으므로,
계획서가 두 하위 호출을 어떻게 다루는지는 축 B에서 확인해야 한다.

축 A 대상은 12 + 2 = **14개 SP**다.

## 3. 단위별 커버리지

| 단위 | 판정 | 상태 | 근거 파일 |
|---|---|---|---|
| UP_Util_PG_Client_CMRate_Ins | 정합 | 신규 | DDL + Spec.md + metadata.json |
| UP_Util_Settle_Summary | 정합 | 신규 | 〃 |
| UP_Util_Settle_Summary_AcqManual | 정합 | 신규 | 〃 |
| UP_UTIL_SETTLE_SUMMARY_EXTRA | 정합 (⚪1) | 신규 | 〃 |
| UP_UTIL_STAT_PGCOLLECT_INS | 정합 (⚪1) | 신규 | 〃 |
| UP_UTIL_SETTLE_INS_EXTRA | 정합 (⚪1) | 신규 | 〃 |
| UP_UTIL_SETTLE_INS_EXTRA4PLCARD | 정합 (🟡1 ⚪1) | 신규 | 〃 |
| UP_UTIL_SETTLE_CANCEL_INS | 결함 (🟡1) | 신규 | 〃 |
| UP_UTIL_SETTLE_SUMMARY_ETC | 결함 (⚪2) | 신규 | 〃 |
| UP_UTIL_SETTLE_PROC_ETC | 결함 (🟡1 ⚪1) | 신규 | 〃 |
| UP_UTIL_SETTLE_EXPECT_PROC | 결함 (🟡1 ⚪1) | 신규 | 〃 |
| UP_UTIL_SETTLE_INS | 결함 (🟡3) | 신규 | 〃 |
| **UP_UTIL_SETTLE_COMM_UPD** | **결함 (🟠2 🟡1 ⚪1)** | 신규 | 〃 |
| **UP_UTIL_SETTLE_EXCEPTION_PROC** | **결함 (🟠2 🟡4 ⚪1)** | 신규 | 〃 |

검증 불가 단위 없음.

## 4. 축 A 결함

### 🟠 대상 행 집합이 달라지는 결함 (4건)

| 등급 | SP | 원본 앵커 | 산출물 앵커 | 원본 | 산출물 | 영향 |
|---|---|---|---|---|---|---|
| 🟠 | COMM_UPD | `object_definition.sql:169` | `Spec.md:162-163, 276, 348` | UPDATE 4 최상위 WHERE의 `AND C.CommissionCancelFlag = 1` | SET표·로직흐름·DML범위표 어디에도 없음. `= 1` 값이 문서 전체에 부재 | 취소수수료 미부과(`flag=0`) 계약의 CheckPay·Toss·TossPoint 전체취소 행까지 `PGCOMM += CommissionCancelAmt` 적용 → 행 집합과 PG 수수료·부가세 금액 변동 |
| 🟠 | COMM_UPD | `object_definition.sql:243` | `Spec.md:350, 310-319` | UPDATE 7 파생 테이블 `D`의 `AND B.CommissionCancelFlag = 1` | 로직 흐름 7이 이 필터를 빠뜨림. 104행은 "부과 여부와 취소수수료를 조회합니다"로 필터를 조회로 약화 | 미부과 계약의 부분취소 묶음까지 최종 수수료 재계산 대상에 포함 → 행 집합과 고객사·PG 수수료 금액 변동 |
| 🟠 | EXCEPTION_PROC | `object_definition.sql:423` | `Spec.md:401, 315, 322-333` | UPDATE 15의 `AND ISNULL(A.MobileCo,'') IN ('1','2','3','4','5','6')` | 리터럴 집합이 문서 전체에 없음. 「집합 술어(기계 확정)」표도 UPDATE 15만 누락 | `MobileCo`가 NULL·기타값인 impaymobile 건까지 `CLComm`/`CLVT`가 `TClientCMRate` 기준으로 덮어써짐 → 행 집합과 금액 변동 |
| 🟠 | EXCEPTION_PROC | `object_definition.sql:375` | `Spec.md:313, 399` | UPDATE 13 파생 테이블 `X`의 `(A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))` — 당일 이전 취소건 제외 | DML 범위의 술어 컬럼은 `PLTID, ID, YMD, PGNAME`뿐. 파생 테이블 표는 컬럼식만 실음 | 원천PG 카드 10개 컬럼 재계산이 전일 취소건에도 적용 → 행 집합·금액 변동 |

### 🟡 표기·추적성 결함 (개별)

| 등급 | SP | 원본 앵커 | 산출물 앵커 | 내용 |
|---|---|---|---|---|
| 🟡 | SETTLE_INS | `:146` | `Spec.md:135, 241` | 전체거래 분기의 `INDEX=CIDX_TTxMst_YMD` 강제 힌트가 Spec에 0회 언급. 이관 시 실행계획이 달라짐 |
| 🟡 | SETTLE_INS | `:243-249` | `Spec.md:287` | 블록 주석으로 비활성화된 구 CLCOMM/CLETC 산식을 주석표에 일반 주석으로 등재하고 비활성 사실을 밝히지 않음 → 이관 시 되살릴 위험 |
| 🟡 | EXPECT_PROC | `:29,139,160,184,204,227,246` | `Spec.md:248-252` | UDF 표 헤더가 6열인데 구분자 행이 5열 → GFM에서 표 전체가 렌더링되지 않아 함수 호출 매핑이 전달되지 않을 수 있음 |
| 🟡 | PROC_ETC | `:42, 137-141` | `Spec.md:186, 150` | `-3` 조기 반환 경로가 커서를 `CLOSE`/`DEALLOCATE` 없이 남긴다는 사실과 커서 스코프(전역·READ_ONLY) 미서술 |
| 🟡 | CANCEL_INS | `:53-59` | `Spec.md:200, 203` | Mermaid `ERRORCHECK` 분기 두 간선에 참/거짓 라벨 없음 (본문 171-172행 서술은 정확) |
| 🟡 | INS_EXTRA4PLCARD | `:189-190, 205-206` | `Spec.md:256` | UPDATE/DELETE의 `TPGProperty` 조인에는 NOLOCK이 없는데 열거 구조상 있는 것처럼 읽힘 |
| 🟡 | EXCEPTION_PROC | `:313-323` | `Spec.md:398` | UPDATE 12에 PGName 필터가 없는데 "원천 PG 거래 중"이라 서술해 있는 것처럼 읽힘 (기계표는 정확) |
| 🟡 | EXCEPTION_PROC | metadata.json | `Spec.md:125` | 파서 확정 `TPGProperty` 7컬럼을 5개로 축소 표기 — 파서가 진실의 원천이라는 계약 위반 |
| 🟡 | EXCEPTION_PROC | `:356-357, 362-363, 393` | `Spec.md:546-550` | 참조 코드 객체 목록이 `SETTLE_CARD_DB` 한정자를 떨어뜨려 표기 → 동일 DB 함수로 오인 소지 |

### 4-1. 전 SP 공통 결함

**① 원본 필터가 명세서 산문·표에서 사라진다 — 🟠 4건 전부가 이 한 가지 부류다.**

네 건 모두 "원본 WHERE 또는 파생 테이블에 있는 필터 술어가 명세서 어디에도 없다"는 같은 형태이고,
그중 둘(`COMM_UPD:243`, `EXCEPTION_PROC:375`)은 **파생 테이블 내부의 필터**, 하나(`EXCEPTION_PROC:423`)는
**`ISNULL()`로 래핑된 술어**다. 담당 단위가 남긴 관찰에 따르면 `ISNULL` 래핑 탓에 파서의 집합 술어
수집에서 빠졌고, 기계 확정표에 없으니 산문에서도 함께 빠졌다.

구조적으로 이 자리를 지키는 검사가 없다. `MechanicalValidator.CheckMissingConditionColumns`는
**Spec → 계획서** 방향(축 B)만 대조하며, **원본 DDL → Spec** 방향에는 조건 보존 검사가 존재하지 않는다.
명세서 생성 단계에서 필터가 빠지면 그 뒤의 모든 검사는 빠진 상태를 기준값으로 삼는다.

**② 주석 기록표의 부분 누락 — 🟡, 4개 SP**

`SETTLE_INS`(:264,265,299), `COMM_UPD`(약 30건, `2023.12.13`·`2021.02.19` 등 시행일자 주석),
`INS_EXTRA`(:232 외 구획 주석), `EXCEPTION_PROC`(:312,334-335,353,423,432 — 정책 전환 이력과
"20251111 … 180일 이후 제거해도 됨" 같은 제거 예정 로직 표시)에서 주석 보존표가 일부 항목을 빠뜨렸다.
표에 실린 라인 번호 자체는 네 SP 모두 원본과 정확히 일치했다. 로직은 SET/WHERE 표에 보존되어
금액·행 집합 영향은 없고, 운영 판단 근거의 추적성만 손실된다.

**③ UPDATE 절 제목이 모두 "갱신 0" — ⚪, 3개 SP**

`EXPECT_PROC`(11개), `COMM_UPD`(15개), `EXCEPTION_PROC`(18개)의 UPDATE 매핑 절 제목이 전부
"갱신 0"이다. 파서의 `GlobalStatementOrdinal`이 전건 0인 것을 템플릿이 그대로 반영한 결과이며
(`StatementOrdinal`은 1..N으로 채워져 있다), 대조 계약상 파서값에 충실하므로 불일치는 아니다.
같은 문서의 오류 코드표·함수표가 갱신 1~N 번호를 쓰므로 절 제목만으로는 상호 참조가 끊긴다.

**④ 명세서가 잘한 것 — 주석 상태 판별**

주석 처리되어 실행되지 않는 조건을 명세서가 활성 로직으로 오서술한 사례는 **14개 SP 중 0건**이었다.
`PROC_ETC:58`(`--AND C.ClientIDType <> 1`), `STAT_PGCOLLECT_INS:71`(`CLIENTID IN ('PAYLETTER','PLTEST')`),
`INS_EXTRA:168·202·312`, `PG_Client_CMRate_Ins:115`, `SETTLE_INS:243-249`(블록 주석),
`COMM_UPD:89-117·368-386`(블록 주석 안의 반환 코드 -4·-15)를 전부 "주석 처리되어 실행되지 않는다"로
정확히 구분했다. `Util_Settle_Summary`는 헤더의 `Inner SP : NONE`이 실제 하위 호출 2건과 모순된다는
사실까지 명시적으로 기록했다.

## 5. 축 B 결함

미실행. 6절 참조.

## 6. 이 감사가 보증하지 않는 것

- **축 B 전체.** `Spec.md` ↔ `agent/steps/S01..S17.md` 17단위를 대조하지 않았다. 4절의 🟠 4건이
  계획서로 전파됐는지는 이 보고서로 알 수 없다. 특히 `COMM_UPD`는 계획서의 수수료 계산 단계,
  `EXCEPTION_PROC`는 예외 수수료 단계의 기준값이다.
- **UDF·TVF 본문.** 단위마다 함수 호출 위치·인자·중첩 순서는 대조했으나 함수 자체의 정의는
  읽지 않았다(단위별 열람 범위 밖). 특히 `SETTLE_INS` 담당 단위가 헤더 이력의 "부가세 원단위 절사"와
  Spec의 "반올림" 서술이 표면상 어긋난다고 보고했다 — `dbo.UF_GET_ROUND4VAT` 본문 대조가 별도로 필요하다.
- **테이블 스키마 의존 주장.** 컬럼 nullability, 기본값, PK 유무에 기댄 명세서 서술 일부는
  대상 테이블 DDL이 단위 열람 범위 밖이라 판정을 보류했다(`CANCEL_INS` Spec.md:129-135,
  `SUMMARY_ETC` Spec.md:274, `INS_EXTRA` Spec.md:90·296 등).
- **실행 대조 없음.** 원본 SP와 이행 대상을 실제로 실행해 결과를 비교하지 않았다. 모든 판정은
  텍스트 대조다.
- **컬럼 한글 명칭 해석.** `SUMMARY_ETC`의 "CLETC=고객사인증료" 같은 업무 명칭은 원본에 근거가 없어
  검증하지 않았다.
