# 앵커 DML 의 「원본에 없는 최상위 술어」 — 원본 DDL 재파생 대조 (설계·되돌림 조건)

**이 문서는 코드보다 먼저 커밋한다.** 되돌릴 수 있으려면 「무엇이 나오면 되돌리는가」가 착수 전에
파일에 있어야 한다(N5 설계서 `2026-09-05-n5-join-pair-design.md` 와 같은 규약).

- 대상: 브레인스토밍 A(2026-09-11) — 「SELECT n · IF n 공백」을 기계로 막는다. 사람 결정 셋:
  1. 결함을 **기계로 막는다**.
  2. **1단계는 앵커 DML(INSERT·UPDATE·DELETE)의 술어 원문 대조**, SELECT 는 2단계.
  3. 기준값은 **원본 DDL 재파생**(A안 — N5 의 형제).
- 기준 커밋: `main` `a9e834c2`.
- 동기: 확인된 결함이 있다. 원본 `UP_UTIL_SETTLE_SUMMARY_EXTRA` 를 보면 `OUTYMD >= @v_strReqYMD` 는 **DELETE 4(193행)에만** 있고 **INSERT 4(210행)에는 없다**. 그런데 `POQSettleBatch6/S12` 와 `POQSettleBatch7/S12` 의 INSERT 4 는 DELETE 4 의 WHERE 를 그대로 베껴 이 항을 싣고 있다. 지금 L1 은 이것을 못 잡는다. 검사 B·C 는 **컬럼 이름**만 대조하는데, `OUTYMD` 는 다른 항(`ISNULL(OUTYMD,'') <> ''`)으로 원본에도 이미 있다.

### 범위 밖 — 그리고 한 가지 정정

- **SELECT 는 이 설계의 범위가 아니다(2단계).** 리더(`StepSqlStatementReader` 의 `DmlCollector`)가 SELECT 를 안 보는 것은 **의도된 것**이다. Task 16 C1 에서 SELECT 행이 검사 A 오류 177 중 70 을 만들었고, 그래서 `IsComparableDmlRow` 가 SELECT 를 뺐다. U-앵커 설계서 §8-7 은 이것을 「공백」이라고 적었는데, 이 표현은 따로 정정한다.
- IF n(제어 흐름)도 범위 밖이다.

---

## 1. 착수 조건 — 앞 층 셋 (v0 근사)

v0 는 버리는 Python 시제품이고 전 Job 에 돌렸다. **v0 의 키에는 대상 테이블이 없었다**(§6). 그래서 아래 도달 수는 **상한**이다. 실측은 §3 스윕에서 C# 자로 다시 잰다.

| 조건 | 값 | 판정 |
| :--- | ---: | :--- |
| ① 행 수 — 원본 명세서 · DML 행 · 최상위 항 | 14 · 92 · 421 | 통과 |
| ② 도달 — 앵커 DML 중 대조까지 간 문장 | 376 / 403 | 통과(상한) |
| ② 표적 — Batch6·7 `S12` INSERT 4 | 도달 · 발화 | 통과 |
| ③ 자 — 지금 검사가 쓰는 것 | `PredicateColumns`(컬럼 이름만) · `SetPredicateFact`(리터럴 IN·=/<> 만 분해) | **넓혀야 한다** |

Job 별 도달(앵커 DML / 도달):

| Job | 앵커 DML | 도달 |
| :--- | ---: | ---: |
| Batch1 | 91 | 86 |
| Batch4 | 75 | 71 |
| Batch5 | 89 | 84 |
| Batch6 | 67 | 57 |
| Batch7 | 81 | 78 |
| **계** | **403** | **376** |

도달하지 못한 27 문장은 원본에 최상위 사실이 없는 경우다. 도달한 문장의 항 대조 결과는 **일치 1462**, **E1 면제 42**, **원본에 없는 항을 더한 문장 14** 다.

---

## 2. 설계

### 2-1. 대조 단위 (①)

- **앵커 INSERT·UPDATE·DELETE 문장만** 대조한다. 앵커 창이 바뀌지 않도록 새 문장 종류는 만들지 않는다. 리더에는 데이터 필드 하나만 더한다.
- **키는 `(Kind, Ordinal, TargetTable)`** 이고 N5 와 같다.
  - 원본 쪽 서수는 `DmlScopeExtractor.BuildStatementOrdinals` 로 매긴다.
  - 원본 쪽 대상은 N5 의 `ResolvedTargetTable` 로 해석한다.
  - 이행 쪽은 `ResolveAnchoredStatements` 로 앵커를 해석하고, 청크 조각은 `(Ordinal, Kind)` 로 묶는다(검사 B·N5 와 같은 묶음).
- 범위는 **WHERE 최상위 AND 항만** 이다. 최상위 판정은 `TopLevelPredicateCollector.CollectTerms` 한 곳이 소유하고, 사본을 만들지 않는다.
- **이행이 더한 항만 발화한다**(추가 전용). 잃은 항은 이 검사의 몫이 아니다.
- 한 문장에 오류는 하나다. 더한 항이 여럿이면 한 메시지에 모두 싣는다.
- **정규화기는 `DmlScopeExtractor` 의 공개 진입점 하나다.** DDL 경로와 리더가 같은 함수를 부른다(규칙이 두 곳에 있으면 조용히 갈린다 — `BareObjectName`·`ResolveOrdinal` 의 선례).
- **[작성 중 추가 — 2026-09-11 사람 승인] 조인 등식은 이 검사가 보지 않는다.** 두 한정자가 다른 컬럼=컬럼 등식(`HaveDifferentQualifiers`)은 N5 의 몫이다. 이 검사가 원본과 이행 양쪽에서 이 등식을 빼지 않으면, 이행이 조인 짝을 하나 더할 때 N5 와 이 검사가 **같은 자리에서 두 번 발화**한다.

### 2-2. 정규화 — ScriptDom AST 위에서 (②)

항을 텍스트가 아니라 **AST 로 다시 써서** 비교한다. 주석·공백·대소문자는 AST 가 이미 없앤다.

| 규칙 | 내용 |
| :--- | :--- |
| R1 | 한정자(별칭)와 3부 이름 접두(`SETTLE_POQ_DB.dbo.`)를 벗긴다 |
| R2 | 변수는 모두 `@V` 로 바꾼다 |
| R3 | 리터럴은 남긴다. IN 목록은 정렬한 집합으로 만든다 |
| R4 | 컬럼을 좌변으로 옮기고, 부등호는 방향을 뒤집는다 |
| R5 | 하위질의 안에도 R1·R2 를 적용하고, 잠금 힌트(`WITH(NOLOCK)`)와 테이블 별칭은 버린다. R3·R4 는 항의 불리언 골격(AND·OR·NOT·괄호)까지만 적용한다(계획 수립 중 좁힘 — 하위질의 본문의 IN 정렬·좌우 교환은 실물이 나올 때 더한다) |
| R6 | UNION(ALL) 가지의 항은 합집합으로 모은다 |

- **알려진 대가 1**: R2 때문에 **변수 동일성은 보지 않는다**. `YMD = @p_ymd` 가 `YMD = @p_otherYmd` 로 바뀌어도 이 검사는 조용하다.
- **알려진 대가 2**: 동치 재작성(`BETWEEN` ↔ `>= AND <=`, `ISNULL(x,'') <> ''` ↔ `x IS NOT NULL AND x <> ''`)은 **미리 만들지 않는다**. 스윕에서 실물로 나올 때만 규칙을 더한다.
- **알려진 대가 3**: 이행이 필터를 `ON` 절로 옮기면(`JOIN X ON … AND X.USESTATE = 0`) 이 검사는 그 항을 보지 못한다.

### 2-3. 면제 (③)

| 면제 | 조건 | 재사용하는 것 |
| :--- | :--- | :--- |
| E1 | **이행 쪽 항 단위**, R2 전에 적용한다. 다음 변수만 쓰는 항은 오케스트레이션 범위로 본다: `@p_from`·`@p_to`(단독이거나 뒤에 대문자가 오는 경우), `@p_runId`, `@p_stepCode`. OR 묶음은 **안의 변수가 모두** 오케스트레이션 변수일 때만 면제한다 | — |
| E2 | 커서 그룹 | `BuildCursorGroupExemptions`(`MechanicalValidator.cs:9543`) — DML 행 92 중 6 을 덮는다. **Batch6/S13 은 덮지 못한다** |
| E3 | 스테이징만 읽는 문장 | `ReadsOnlyStaging`(`:8990`) |
| E4 | 자연 침묵 — 원본과 같은 항 | — |

- 면제로 조용해진 건수는 스윕이 센다(§3). 면제는 활동이지 효력이 아니다.

### 2-4. 배선과 침묵 조건 (④)

- 배선 자리는 N5 바로 뒤(`MechanicalValidator.cs:648`)이고, `SafeCheck` 로 감싼다.
- **분할 SP 필터는 받지 않는다.** 이 검사는 검사 B·C·D·N5 와 같은 「앵커가 달린 문장은 정확해야 한다」 모양이다.

| 침묵 | 조건 |
| :--- | :--- |
| S1 | 원본 DDL 이 없다 |
| S2 | 키가 없거나 모호하다. 모호한 키는 N5 처럼 **제거한다**. Batch6 의 U-앵커와 종류별 서수가 어긋나는 자리(`AnchorKindOrdinalPairTests` 불일치 8)가 대상 테이블 불일치로 여기에 떨어진다 — §6 참고 |
| S3 | 원본의 최상위 항이 0 이다 |
| S4 | E2 또는 E3 에 해당한다 |
| S5 | 원본 명세서에 `VerificationBanner.L1Exhausted` 배너가 있다(오늘 31 편 중 0). L2 거절 배너는 막지 않는다 |

**메시지는 N5 를 따른다.** 이행 쪽은 **단계 원문**, 원본 쪽은 **DDL 원문**을 싣는다(정규형을 싣지 않는다 — 사람이 읽는 것은 원문이다).

```
{Code} 섹션의 {Kind} {Ordinal}({target}) 문장이 원본에 없는 최상위 술어 `{이행 원문 항들}`을(를) 씁니다.
원본의 최상위 술어는 `{원본 원문 항들}`뿐입니다 — 행을 고르는 조건을 더하면 원본이 고르던 행이 아닌 행을 고릅니다.
```

- `StepSweepService` 의 `SweepIndicators` 에 침묵 분모를 더한다. S1~S5 와 E1~E3 을 각각 센다.
- **캐시 버전은 올리지 않는다.** Job 경로는 `CurrentCacheFormatVersion` 을 쓰지 않는다. 대신 **스윕 차분이 0 이 아니어야 한다**(S12 둘).

---

## 3. 검증 (⑤)

### 3-1. TDD — N5 의 네 층을 그대로 따른다

1. **추출기 층**
   - 같은 SQL 을 DDL 경로와 단계 경로로 넣었을 때 정규형이 같아야 한다(대칭 시험).
   - R1~R6 마다 시험을 하나씩 둔다.
   - COMM_UPD UPDATE 3 의 실물 모양(인라인 주석, `WITH(NOLOCK)`, `@pi_`↔`@p_` 변수가 섞인 NOT IN 하위질의)을 픽스처로 넣는다. **픽스처는 DDL 과 단계 파일에서 오려 온다.** 기억으로 짓지 않는다.
2. **리더 층**
   - UPDATE, DELETE, INSERT…SELECT UNION ALL 문장에서 새 필드가 채워지는지 본다.
   - **문장 수와 앵커가 전과 같은지** 확인한다(앵커 창 불변).
3. **검사 층**
   - 발화: S12 모양(DELETE 의 술어가 INSERT 로 복사됨).
   - 침묵: 별칭만 다름, 오케스트레이션 변수(E1), 커서(E2), 스테이징(E3), DDL 없음(S1), 키 없음·모호(S2), 원본 항 0(S3), L1Exhausted 배너(S5), 조인 등식만 더함(N5 의 몫).
4. **스윕 층**: 침묵 분모를 센다. 여기에 `ValidateBatchStep` 를 거치는 **배선 시험** 하나를 더한다(발화를 재는 자와 배선을 재는 자는 다른 축이다).

### 3-2. 코퍼스 스윕 — 합격선 (한 벌 여섯의 뒤 층)

격리 워크트리에서 `--sweep` 를 BASE(구현 브랜치가 갈라진 main 커밋 — 해시를 이 문서에 적는다)와 AFTER(구현 브랜치 끝)로 돌린다. `POQSettleBatch7-resume` 이 코퍼스에 있으면 제외한다.

| 뒤 층 | 합격선 |
| :--- | :--- |
| ④ 발화 | **정확히 Batch6/S12 · Batch7/S12 의 INSERT 4 둘.** 그 밖의 발화는 전부 연다 |
| ⑤ 오탐 전수 열람 | 오탐 0. Batch6/S13 은 발화하든 안 하든 연다. COMM_UPD UPDATE 3 다섯 곳은 **조용해야 한다** — 조용하지 않으면 정규화기 결함이므로 면제를 더하지 말고 정규화를 고친다 |
| ⑥ 없앴을 때 | ① 검사 호출 한 줄을 되돌리면 S12 두 발화가 사라지고 다른 발화는 변하지 않는다. ② 정규화 규칙 R2·R5 를 하나씩 끄면 COMM_UPD 다섯 곳이 오탐으로 튀어나온다 — 규칙이 일하고 있다는 **양성 대조군**이다 |

- 기존 검사(A·B·C·D·N5 와 나머지)의 발화 차분은 0 이어야 한다.
- 도달(C# 실측)과 침묵 분모 S1~S5·E1~E3 을 이 문서에 덧붙인다. v0 의 376 보다 낮게 나올 것으로 예상한다(키에 대상 테이블이 들어가므로).

### 3-3. 게이트

- 실패는 의도된 `AnchorKindOrdinalPairTests` 하나뿐이어야 한다(Batch6 불일치 8 — 약하게 만들지도, 8 로 못박지도 않는다).
- 건너뜀 0, 경고 0.

---

## 4. 되돌림 조건

아래 중 하나라도 나오면 **병합하지 않고 설계로 돌아온다.** 즉석 면제로 막지 않는다.

1. 원인을 설명할 수 없는 발화나 오탐이 하나라도 나온 경우.
2. 기존 검사의 발화 차분이 0 이 아닌 경우.
3. S12 두 발화 중 하나라도 나오지 않은 경우(표적 미도달).
4. §3-2 ⑥ 의 양성 대조군이 튀어나오지 않는 경우(정규화 규칙이 일하지 않는다).

---

## 5. 알려진 대가 — 한곳에

- 변수 동일성은 보지 않는다(R2).
- 동치 재작성은 미리 만들지 않는다.
- `ON` 절로 옮긴 필터는 보지 못한다.
- 잃은 항은 보지 않는다(추가 전용).
- 조인 등식은 N5 에 맡긴다.
- 키가 어긋난 U-앵커 자리(Batch6)는 도달하지 못한다(S2).
- E2 가 Batch6/S13 을 덮지 못한다.
- **명세서가 DML 범위 표를 잃으면 E3 가 넓어진다**(Task 5 리뷰, 2026-09-11). 스테이징 대상 집합(`BuildSpecTargets`)이 비면, 계보를 가진 모든 앵커 문장이 「스테이징만 읽는다」로 면제된다. 오라클이 원본 DDL 이어도 면제 판정이 명세서 표에 기대는 자리가 있다는 뜻이다. 검사 C 도 같은 자리를 공유한다. 스윕의 E3 분모가 급증하면 이 경로를 먼저 의심한다.

---

## 6. v0 후보 14 문장 — 사전 판정 (2026-09-11, 원본 DDL 대조)

| 후보 | 건수 | 판정 | 근거 |
| :--- | ---: | :--- | :--- |
| Batch6·7 / S12 INSERT 4 `OUTYMD >= @v` | 2 | **참양성** | 원본 INSERT 4(`TSettleByOUT`, 210행)에는 이 항이 없고 DELETE 4(193행)에만 있다. 키 `(INSERT, 4, TSettleByOUT)` 로 맞물린다 |
| COMM_UPD UPDATE 3 `PLTID NOT IN (SELECT ' ' UNION ALL …)`(Batch1/S07, 4/S08, 5/S05, 6/S05, 7/S06) | 5 | v0 흔적 | 원본에는 인라인 주석과 `WITH(NOLOCK)` 이 있고 변수는 `@pi_strYMD` 다. R2·R5 를 거치면 같아진다 |
| Batch6 / S01 INSERT 2 · INSERT 4 | 2 | v0 흔적(키) | `CMRate_Ins` 다섯 INSERT 는 E1·R2 만 적용해도 원본과 글자까지 같다. v0 가 `U2`→INSERT 2, `U4`→INSERT 4 로 **U 번호를 종류별 서수로 읽었다**. 실제 키는 대상 테이블에서 어긋나 S2 로 떨어진다 |
| 커서 SP — Batch1/S12 AcqManual DELETE 1 · INSERT 1, Batch4/S11 PROC_ETC, Batch4/S15 SUMMARY_ETC | 4 | E2 면제 예상 | 스윕에서 확인한다 |
| Batch6 / S13 SUMMARY_ETC | 1 | **열지 않음** | E2 가 덮지 못하는 자리다. §3-2 에서 반드시 연다 |

---

## 7. 실측 (2026-09-11, BASE 58da1f1a · AFTER 6e5365c0)

**결론 먼저.** §4 의 되돌림 조건 넷은 문자 그대로는 하나도 걸리지 않았다. 다섯 발화 모두 원인을 설명할 수 있고, 기존 검사 차분은 0 이다. S12 두 발화가 나왔고, 양성 대조군 둘도 튀었다. **그러나 §3-2 합격선 둘이 깨졌다.** ④ 는 「정확히 S12 둘」이어야 하는데 발화 좌표가 **다섯**이다. ⑤ 는 「오탐 0」이어야 하는데 오탐이 **1**이다(Batch4/S11). 나머지 둘은 v0 가 E2 로 예상했던 자리에서 나온 **참양성**이다. 병합은 사람이 정한다. 면제·정규화·시험은 한 줄도 바꾸지 않았다.

### 7-1. 코퍼스 조립

- 각 워크트리 루트에 `output` 심링크 하나(`→ /Users/payletter/git-root/ReSet/output`)만 걸었다. 조립본은 만들지 않았다. `appsettings.local.json` 에 `OutputSettings` 가 없어서 두 워크트리 모두 기본값 `./output` 을 읽는다. 곧 **같은 코퍼스**다.
- 시작 목록(`ls output/Jobs/`): `POQSettleBatch1 · POQSettleBatch4 · POQSettleBatch5 · POQSettleBatch6 · POQSettleBatch7`. `-resume` 은 없다.
- 게이트 뒤 목록: `POQSettleBatch1 · POQSettleBatch4 · POQSettleBatch5 · POQSettleBatch6 · POQSettleBatch7`. 시작 목록과 같다. 측정 창 안에 복사본이 끼지 않았다.
- 두 보고서의 실행 조건은 같다. 측정 쌍 83(Job 5) · 캐시 `FormatVersion` {22}·항목 31 · 명세서 세대 2026-09-10 · 작업 트리 깨끗 · 미해결 프로시저 참조 0.
- 보고서 둘의 위치:
  - BASE: `docs/audit-reports/sweeps/2026-09-11-step-sweep-base.md`. BASE 워크트리에 `2026-09-11-step-sweep.md` 로 생긴 것을 이름만 바꿔 옮겼다(바이트 동일 확인). 커밋은 본문 「커밋: `58da1f1a`」 줄로 가린다.
  - AFTER: `docs/audit-reports/sweeps/2026-09-11-step-sweep.md`.
- 스윕은 조건 (A)·(B) 로 한 번씩 돈다. 그래서 P 목록의 좌표 하나가 두 행으로 실린다. 아래의 「좌표」는 `(Job, 단계, 종류, 서수)` 를 중복 없이 센 것이다.

### 7-2. 합격선

| 합격선 | 기대 | 실측 | 판정 |
| :--- | :--- | :--- | :--- |
| A·B·C·D·E·미분류 | BASE = AFTER | 두 조건 모두 A 0 · B 14 · C 2 · D 0 · E 0 · 미분류 48 로 같다. Job 별 표도 P 행을 빼면 같다. B·C 발화 목록 32 행도 같다 | 통과 |
| P 발화 좌표(중복 제거) | 정확히 Batch6·7 `S12` INSERT 4 | **5** — Batch4/S11 UPDATE 1 · Batch4/S15 INSERT 1 · Batch6/S12 INSERT 4 · Batch6/S13 DELETE 1 · Batch7/S12 INSERT 4. (A)·(B) 가 같은 좌표라 10 행이다 | **불통과** |
| S12 표적 둘 | 발화 | 둘 다 발화 | 통과 |
| COMM_UPD UPDATE 3 다섯 곳 | P 에 없다 | 없다. 다섯 곳(Batch1/S07 · Batch4/S08 · Batch5/S05 · Batch6/S05 · Batch7/S06) 모두 **일치(Matched)** | 통과 |
| 오탐 | 0 | **1**(Batch4/S11 UPDATE 1, §7-3) | **불통과** |
| 도달 = 원본과 대조까지 간 문장 | v0 376 보다 낮다 | **383 / 434**(88.2 %). v0 는 376 / 403(93.3 %) | 수는 예측과 반대이고 비율은 예측대로다 — 아래 |

**도달의 분자·분모·자.**
- 분자는 `PredicateTermStatementsCompared`(Fired + Matched = 5 + 378)다.
- 분모는 `ResolveAnchoredStatements` 가 푼 `(Kind, Ordinal)` 묶음 434 이고, 「앵커가 서수로 해결된 문장 수」 434 와 같다.
- 자는 조건 (B) 사전이다.
- v0 의 분모(403)는 다른 자다. 앵커 수가 Job 마다 다르다.

| Job | C# 앵커 / 도달 | v0 앵커 / 도달 |
| :--- | ---: | ---: |
| Batch1 | 92 / 83 | 91 / 86 |
| Batch4 | 78 / 73 | 75 / 71 |
| Batch5 | 92 / 83 | 89 / 84 |
| Batch6 | 80 / 61 | 67 / 57 |
| Batch7 | 92 / 83 | 81 / 78 |
| **계** | **434 / 383** | **403 / 376** |

분모가 31 다르므로 「v0 가 상한」은 **수로는 성립하지 않는다**. 도달률은 모든 Job 에서 v0 이하다. 분모 31 의 차이는 v0 코드가 저장소에 없어서 이 창에서 귀속하지 못했다. 발화가 아니라 비교 기준의 차이이므로 되돌림 조건은 아니다.

### 7-3. P 전 좌표 판정 (원본 DDL · 단계 파일 대조)

원본은 `output/Objects/<SP>.Procedure/raw/object_definition.sql` 을, 이행은 `output/Jobs/<Job>/agent/steps/<단계>.md` 를 읽었다. 항 원문은 스윕과 같은 재료로 뽑았다. `EvaluateAnchoredPredicateTerms` 결과를 문장마다 덤프하는 임시 한 줄을 넣었다가 되돌렸다. 커밋하지 않았다.

| # | 좌표 | SP · 대상 | 더한 항(이행 원문) | 판정 | 근거 |
| ---: | :--- | :--- | :--- | :--- | :--- |
| 1 | Batch6/S12 INSERT 4 | SUMMARY_EXTRA · TSettleByOUT | `OUTYMD >= @v_strReqYMD` (S12.md:331) | **참양성** | 원본 INSERT 4(210행)의 WHERE(223~228행)에는 이 항이 없다. DELETE 4(196행)에만 있다. 설계 동기의 결함 그대로다 |
| 2 | Batch7/S12 INSERT 4 | SUMMARY_EXTRA · TSettleByOUT | `OUTYMD >= @p_reqYmd` (S12.md:209) | **참양성** | 1 과 같다 |
| 3 | Batch4/S15 INSERT 1 | SUMMARY_ETC · TSettleByOUT | `A.OUTSTATE = 9` · `A.USESTATE = 0` · `A.OUTYMD IS NOT NULL` (S15.md:161) | **참양성** | 결함을 만드는 항은 `A.OUTSTATE = 9` 하나다(아래 첫째 글머리). 나머지 둘은 커서가 넘긴 값과 겹쳐 무해하다 |
| 4 | Batch6/S13 DELETE 1 | SUMMARY_ETC · TSettleByOUT | `EXISTS (SELECT 1 FROM (커서 SELECT DISTINCT …) K WHERE K.YMD = …TSettleByOUT.YMD AND … AND K.OUTSTATE = …TSettleByOUT.OUTSTATE AND K.CompanySalesType = …TSettleByOUT.CompanySalesType …)` (S13.md:130~155) | **참양성(내용 기준)** | 커서 행 단위 DELETE(원본 59~72행, 변수 등식 13)를 EXISTS 한 항으로 바꿨다. 그 항 안에 원본에 없는 조건이 둘 있다(아래 둘째 글머리). 고르는 행이 원본과 다르다 |
| 5 | Batch4/S11 UPDATE 1 | PROC_ETC · TSettleMiss | `OutState = @p_intOutState` (S11.md:221) | **오탐** | 원본은 `OutState = 2`(97행)다. 이행은 리터럴 2 를 매개변수로 올렸고 호출마다 `p_intOutState: 2` 를 바인딩한다(S11.md:56·62·75). 고르는 행이 같다. 원인은 아래 셋째 글머리 |

- **3 의 결함.** 원본 INSERT 1(81행)의 WHERE(97~109행)는 커서 변수 등식뿐이고 OUTSTATE 를 거르지 않는다. 그래서 같은 키의 OUTSTATE ≠ 9 행까지 다시 집계한다. GROUP BY 에도 OUTSTATE 가 있다. 앞선 DELETE 1(S15.md:124, 원본 59행과 일치)은 OUTSTATE 와 무관하게 그 키의 행을 모두 지운다. 따라서 이행은 **OUTSTATE ≠ 9 집계 행을 지우고 다시 넣지 않는다.** `A.USESTATE = 0` 과 `A.OUTYMD IS NOT NULL` 은 커서가 넘긴 `@p_UseState`(=0)·`@p_OutYMD`(비 NULL)와 겹친다.
- **4 의 조건 둘과 발화 모양.**
  - (가) `K.OUTSTATE = …OUTSTATE`: 원본 DELETE 는 OUTSTATE 를 보지 않는다. 이행은 OUTSTATE = 9 행만 지운다.
  - (나) NULL 비안전 등식 `K.CompanySalesType = …`·`K.ExtraSettleFlag = …`: 원본은 `ISNULL(CompanySalesType,4) = ISNULL(@v,4)` 라 NULL 행도 지운다. 이행은 NULL 행을 못 지운다.
  - 다만 **이 검사가 그 둘을 짚어 발화한 것은 아니다.** EXISTS 항 통째가 원본에 없어서 발화했다. 그래서 충실한 집합 치환이었어도 같은 자리에서 발화했을 것이다. 예컨대 같은 SP 를 옮긴 Batch4/S15 의 `SQL_CREATE_AND_CAPTURE_SHADOW`(S15.md:87~101)는 ISNULL 을 지키고 OUTSTATE 를 대조하지 않는다. 이것은 **「커서 → EXISTS 집합 치환」 모양의 잠재 오탐**이다. 이번 코퍼스에서는 내용이 결함이라 참양성이 됐을 뿐이다.
- **5 의 원인.** R3 은 리터럴을 남기고 R2 는 변수를 `@V` 로 지운다. 그래서 `OUTSTATE = 2` ≠ `OUTSTATE = @V` 가 된다. 설계 §2-2 「알려진 대가 2 — 동치 재작성은 미리 만들지 않는다」의 실물이다(리터럴 → 바인딩 매개변수). 원인은 설명된다.
- **v0 의 「E2 면제 예상」이 3·5 에서 빗나간 이유.** `BuildCursorGroupExemptions`(MechanicalValidator.cs:9724)는 쓰기 문장의 술어 집합이 어떤 SELECT 의 GROUP BY 와 **같을 때만** 면제한다.
  - PROC_ETC: 커서 SELECT 의 GROUP BY 는 `[ClientID, YMD, OutYMD]` 이고 UPDATE 1 의 술어는 `[ID, ClientID, OutYMD, OutState, IssueType]` 이라 다르다.
  - SUMMARY_ETC: 커서가 행을 순회해 GROUP BY 가 없다.
  - 그 함수의 주석(:9708~9713)이 「후자는 술어가 사라지면 진짜 결함이다」라고 이미 적고 있다. 곧 결함이 아니라 설계대로의 비면제다.
- **Batch6/S13 을 열었다(§6 의 약속).** DELETE 1 은 위 4 다. INSERT 2 는 S2 로 침묵했다. 이행 앵커는 `U2 … (INSERT 1)`(S13.md:198)인데, 앵커가 `(INSERT, 2)` 로 풀려 원본 키 `(INSERT, 1, TSettleByOUT)` 와 어긋난다(Batch6 U-앵커 불일치). 그런데 이 INSERT 는 3 과 같은 `A.OUTSTATE = 9` 를 싣고 있다(S13.md:262). 곧 **S2 가 가린 참양성 후보**다. 검사가 거기까지 가지 못한 것은 §5 「키가 어긋난 U-앵커 자리는 도달하지 못한다」의 실물이다.

### 7-4. v0 후보 14 의 결말

| 후보 | 건수 | §6 사전 판정 | C# 결말 |
| :--- | ---: | :--- | :--- |
| Batch6·7 / S12 INSERT 4 | 2 | 참양성 | **발화 2**(참양성) |
| COMM_UPD UPDATE 3 (Batch1/S07 · 4/S08 · 5/S05 · 6/S05 · 7/S06) | 5 | v0 흔적 | **일치 5** |
| Batch6 / S01 INSERT 2 · INSERT 4 | 2 | v0 흔적(키) | **S2 2** — 대상 `TPGSettleRate`·`TClientSettleRate`. S01 은 DELETE 1 만 일치했고 나머지 9 문장이 S2 다 |
| 커서 — Batch1/S12 AcqManual DELETE 1 · INSERT 1 | 2 | E2 | **E2 2** |
| 커서 — Batch4/S11 PROC_ETC | 1 | E2 | **발화**(UPDATE 1, 오탐). 같은 단계 INSERT 1 은 S3 |
| 커서 — Batch4/S15 SUMMARY_ETC | 1 | E2 | **발화**(INSERT 1, 참양성). 같은 단계 DELETE 1 은 일치 |
| Batch6 / S13 SUMMARY_ETC | 1 | 열지 않음 | **발화**(DELETE 1, 참양성). 같은 단계 INSERT 2 는 S2 |

사전 판정과 결말이 갈린 것은 둘이다(Batch4/S11·S15 는 E2 가 아니라 발화). 나머지 12 는 사전 판정대로 갔다.

### 7-5. 침묵 분모 아홉 (AFTER, 조건 (B) 사전)

| 분모 | 값 |
| :--- | ---: |
| 원본과 대조까지 간 앵커 DML 문장 | 383 |
| 발화한 문장 | 5 |
| S1 원본 DDL 없음 | 0 |
| S2 키 없음·모호 | 12 |
| S3 원본 최상위 항 없음 | 31 |
| E2 커서 그룹 면제 | 8 |
| E3 스테이징만 읽음 | 0 |
| S5 L1 소진 배너 | 0 |
| E1 오케스트레이션 항으로 면제한 **항** 수 | 18 |

- **합이 앵커 창을 덮는다.** 383 + 12 + 31 + 8 + 0 + 0 + 0 = **434** = 「앵커가 서수로 해결된 문장 수」. 빠진 문장이 없다.
- **S2 12 는 전부 Batch6 이다.** 내역은 S01 CMRate_Ins 9(DELETE 3 ~ INSERT 10) · S09 PGCOLLECT_INS DELETE 22·INSERT 31 · S13 SUMMARY_ETC INSERT 2 다. §2-4 가 예고한 「Batch6 U-앵커 불일치가 대상 테이블 불일치로 S2 에 떨어진다」와 맞는다(`AnchorKindOrdinalPairTests` 의 불일치 8 과는 다른 자이므로 수를 맞대지 않는다).
- **S3 31 의 SP 별 내역.** CMRate_Ins 3 · COMM_UPD 5(UPDATE 7, Job 마다) · INS 5 · INS_EXTRA 4 · INS_EXTRA4PLCARD 5 · PROC_ETC 5(INSERT 1 `VALUES`, Job 마다) · PGCOLLECT_INS 4.
- **E2 8 의 내역.** AcqManual DELETE 1·INSERT 1 × 4 Job(Batch1/S12 · Batch5/S11 · Batch6/S11 · Batch7/S11).
- **E3 0 은 「안전하다」가 아니라 「재지 않았다」로 읽는다.** 같은 보고서의 「계보 원천을 가진 문장 수」가 0 이다. `ReadsOnlyStaging` 은 계보 원천이 있어야 참이 되므로, 이 코퍼스에서 E3 는 **도달 불가**다. 그러니 §5 의 경고(명세서가 DML 표를 잃으면 E3 가 넓어진다)는 여기서 급증으로 나타날 수가 없다. 명세서마다 DML 표가 있는지는 따로 재지 않았다. 재료 분모에 DmlRows 명세서 행 102(14 프로시저)가 있지만 프로시저별 존재는 「잴 수 없음」이다.
- **「발화한 문장」 지표는 배선을 재지 않는다.** 되돌림 ① 에서 검사 호출을 끄자 P 가 0 이 됐지만 이 지표는 5 로 남았다. 지표는 판정 함수를 직접 부르기 때문이다. 배선을 재는 자는 P 발화 수 쪽이다.

### 7-6. 없앴을 때 셋

실험마다 한 파일을 고치고 스윕을 돈 뒤 `git checkout -- <파일>` 로 되돌렸다. 커밋하지 않았다. 실험 보고서(`-b`)도 워크트리 밖으로 옮겼고 커밋하지 않았다.

| 실험 | 바꾼 것 | 기대 | 실측 | 판정 |
| :--- | :--- | :--- | :--- | :--- |
| ① 배선 | `MechanicalValidator.cs:650~651` 의 `SafeCheck(() => CheckAnchoredStatementPredicateTerms(…))` 주석 처리 | P = 0, 나머지는 AFTER 와 같다 | P 0 / 0 · A 0 · B 14 · C 2 · D 0 · E 0 · 미분류 48 · B·C 목록 32 행이 AFTER 와 같다 | 통과 |
| ② R2 | `DmlScopeExtractor.PredicateTerms.cs:263` `Visit(VariableReference)` → `{ }` | COMM_UPD 다섯 곳이 P 에 나온다 | 다섯 곳 모두 나왔다(각 A·B 2 행). P 좌표 366(Job 별 81·73·70·61·81). A~E·미분류는 그대로 | 통과(거친 대조군) |
| ③ R5 | 같은 파일 `Visit(NamedTableReference)` → `{ }` | COMM_UPD 다섯 곳이 P 에 나온다 | 다섯 곳 모두 나왔다. P 좌표 15 | 통과(좁은 대조군) |

- **② 가 거친 이유.** 변수를 쓰는 거의 모든 항이 튄다. 그래서 COMM_UPD 만의 증거는 아니고 「R2 가 전역에서 일한다」의 증거다.
- **③ 의 15 좌표.** AFTER 의 5 에, COMM_UPD UPDATE 3 다섯과 `UP_UTIL_SETTLE_EXCEPTION_PROC` UPDATE 18 다섯(Batch1/S08 · 4/S07 · 5/S04 · 6/S04 · 7/S05)이 더해진 것이다. 이 코퍼스에서 R5 에 기대는 문장은 이 둘(× 5 Job)뿐이다. 그래서 좁은 대조군이다.

### 7-7. 게이트

| 명령 | 결과 |
| :--- | :--- |
| `dotnet build -warnaserror --no-incremental` | 경고 0 · 오류 0. `CoreCompile` 8 회, 5 프로젝트(ReSet.Core · Validator.Core · Validator.Cli · Cli · Core.Tests)를 새로 컴파일했다 |
| `dotnet test tests/ReSet.Core.Tests` | 실패 1 · 통과 4205 · **건너뜀 0** · 전체 4206 |

- **빌드를 비증분으로 다시 잰 이유.** 첫 빌드는 되돌림 직후인데도 1.7 초 만에 「경고 0」으로 끝났다. 컴파일 없이 통과했을 수 있어 비증분으로 다시 쟀다.
- **실패는 `AnchorKindOrdinalPairTests.EveryUAnchorPointsAtAStatementTheSpecActuallyDeclares` 하나다.** 의도된 실패다(§3-3). 출력 꼬리는 「대조한 단계 70 · 앵커 보유 문장 434 · 불일치 8」이다.
  - 불일치 8 은 전부 Batch6 이다. S01 앵커 6~10 · S09 앵커 22·31 · S13 INSERT 앵커 2.
  - 이 시험의 「앵커 보유 문장 434」는 스윕의 「앵커가 서수로 해결된 문장 수」 434 와 같다. 두 자가 같은 앵커 창을 보고 있다.

### 7-8. 되돌림 조건 대조 (§4)

| 조건 | 결과 |
| :--- | :--- |
| 1. 원인을 설명할 수 없는 발화·오탐 | 없다. 다섯 발화 모두 원인을 댔다(§7-3). 다만 **설명된 오탐 1** 이 있다 |
| 2. 기존 검사 발화 차분 ≠ 0 | 아니다(차분 0) |
| 3. S12 둘 중 미발화 | 아니다(둘 다 발화) |
| 4. 양성 대조군 미발현 | 아니다(② ③ 모두 발현) |

§4 는 걸리지 않았다. §3-2 합격선 ④(정확히 둘)와 ⑤(오탐 0)는 깨졌다. 사람이 정할 것은 셋이다. 이 창에서는 셋 다 손대지 않았다.

1. Batch4/S11 의 리터럴 → 바인딩 매개변수를 동치로 볼 규칙을 둘 것인가(§2-2 대가 2 가 말한 「실물로 나올 때」가 이것이다).
2. 「커서 → EXISTS 집합 치환」 모양의 구조적 발화를 어떻게 다룰 것인가(Batch6/S13).
3. 새로 드러난 참양성 둘(Batch4/S15 INSERT 1 · Batch6/S13 DELETE 1)과 S2 가 가린 후보 하나(Batch6/S13 INSERT 2)를 결함 목록에 올릴 것인가.
