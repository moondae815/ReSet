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
- **R7 의 대가**(§8-1, 2026-09-12): 이행이 원본 리터럴을 매개변수로 바꾸고 **다른 값을 바인딩**하면 조용하다 — Batch4/S11 이 2 가 아니라 3 을 바인딩해도 못 본다. 대가 1(R2 — 변수 동일성을 안 본다)과 같은 부류다.
- **커서 → EXISTS 집합 치환은 모양으로 발화한다**(§8-2, 2026-09-12). 행을 순회하는 커서(GROUP BY 없음 — E2 밖)를 EXISTS 로 **충실히** 옮겨도, 원본 최상위에 EXISTS 항이 없으므로 발화한다. 발화문은 문자 그대로 참이지만 행을 좁히는 실제 조건을 짚지 않는다. 오늘 코퍼스의 실물은 Batch6/S13 DELETE 1 하나이고, 그 자리는 진짜 결함 위에 났다.

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

**결론 먼저 — §4 되돌림 조건 1 이 걸렸다. 병합하지 않고 설계로 돌아온다(사람 결정 대기).**

- **걸린 자리.** Batch4/S11 UPDATE 1 이 오탐이다(§7-3 #5).
- **왜 「걸렸다」로 적는가.** 조건 1 「원인을 설명할 수 없는 발화나 오탐」은 「설명할 수 없는」이 발화에만 걸리는지 오탐에도 걸리는지가 문구로는 갈리지 않는다. 좁게 읽으면 오탐도 원인을 설명할 수 없어야 걸리는데, 그러면 조건 1 의 오탐 쪽은 사실상 걸릴 일이 없다. DDL 을 읽어 찾아낸 오탐은 언제나 누군가 원인을 댈 수 있기 때문이다. 넓게 읽을 근거는 셋이다.
  1. §4 는 「즉석 면제로 막지 않는다」고 적었다.
  2. §2-2 대가 2 는 동치 규칙을 「스윕에서 실물로 나올 때」 더한다고 적었다. 그것은 설계 변경이지 측정 창의 몫이 아니다.
  3. 좁게 읽으면 합격선 ⑤ 「오탐 0」은 깨져도 아무 결과가 없다.

  그래서 조건 1 은 **걸린 것**으로 적는다(조정자·리뷰어 판정, 2026-09-11). 첫 판(`fb3a6f74`)은 좁은 읽기로 「§4 는 걸리지 않았다」고 적었고, 이 판에서 고쳤다.
- **나머지 조건 2~4 는 걸리지 않았다.** 기존 검사 차분은 0 이다. S12 두 발화가 나왔다. 양성 대조군 둘도 튀었다.
- **합격선은 둘이 깨졌다.** ④ 는 「정확히 S12 둘」인데 발화 좌표가 **5** 다. ⑤ 는 「오탐 0」인데 오탐이 **1** 이다.
- **다섯 발화의 내역은 오탐 1 · 모양 발화 1 · 참양성 3 이다.** 참양성 3 은 S12 둘과 Batch4/S15 INSERT 1 이다. 모양 발화 1(Batch6/S13 DELETE 1)은 검사가 일한다는 증거가 **아니다**. EXISTS 항 통째가 원본에 없어서 발화했고, 충실한 커서 → 집합 치환이었어도 같은 자리에서 발화했을 것이다. 실제 결함과 겹친 것은 우연이다(§7-3).
- 면제·정규화·시험은 한 줄도 바꾸지 않았다.

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
| 오탐 | 0 | **오탐 1**(Batch4/S11 UPDATE 1). 여기에 **모양 발화 1**(Batch6/S13 DELETE 1)이 더 있다 — 실제 결함과 우연히 겹쳤을 뿐 참양성으로 세지 않는다(§7-3) | **불통과** |
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

**두 보고서의 차이 — 검사가 아닌 것 하나.** 두 보고서를 `diff` 로 대조했다. 차이는 다섯 자리다.

- 「커밋」 줄.
- 발화량 표의 P 행(검사별 1 행, Job 별 3 행).
- 「검사 P 발화 목록」 절.
- 침묵 분모 아홉 줄.
- 재료 분모 표의 `IsL1Exhausted` 행(`잴 수 없음 · 안 쟀음 · 잴 수 없음`).

마지막 행은 AFTER 에만 있다. Task 4 가 더한 재료(S5 의 근거)가 재료 목록에 오른 것이고 **어느 검사의 발화 수와도 무관하다**. 나머지 넷은 모두 검사 P 가 더한 것이다.

### 7-3. P 전 좌표 판정 (원본 DDL · 단계 파일 대조)

원본은 `output/Objects/<SP>.Procedure/raw/object_definition.sql` 을, 이행은 `output/Jobs/<Job>/agent/steps/<단계>.md` 를 읽었다.

**행 번호 규약.** 문장을 가리키면 그 문장이 시작하는 행이고, 항을 가리키면 그 항이 있는 행이다. 예컨대 SUMMARY_EXTRA DELETE 4 는 193 행에서 시작하고(§6 의 「193행」은 문장 시작), 그 문장의 항 `OUTYMD >= @v_strReqYMD` 는 196 행에 있다(아래 #1 의 「196행」은 항).

**문장별 덤프 — 커밋하지 않았다. 재현법을 적는다.** 아래 표의 항 원문, §7-4 의 결말, §7-5 의 자리별 내역(S2·S3·E2 자리, COMM_UPD 「일치 5」)은 커밋하지 않은 덤프에서 왔다. 보고서에는 문장별 사유가 실리지 않는다. 덤프는 이렇게 만들었다.

1. AFTER(`6e5365c0`)의 `src/ReSet.Core/Services/StepSweepService.cs` 를 연다.
2. 침묵 분모 루프(`foreach (var evaluation in MechanicalValidator.EvaluateAnchoredPredicateTerms(...))`) 안, `predicateTermOrchestration += evaluation.OrchestrationTermsExempted;` 바로 뒤에 아래 코드를 넣는다.
3. `RESET_PROBE_PATH=<덤프 파일> dotnet run --project src/ReSet.Cli -- --sweep` 로 돈다.
4. `git checkout -- src/ReSet.Core/Services/StepSweepService.cs` 로 되돌리고, 그 판이 낸 `-b` 보고서를 지운다.

```csharp
var probePath = Environment.GetEnvironmentVariable("RESET_PROBE_PATH");
if (!string.IsNullOrEmpty(probePath))
{
    System.IO.File.AppendAllText(probePath,
        $"{job.JobName}\t{step.Code}\t{string.Join(",", step.LegacyProcedures)}\t{evaluation.Kind} {evaluation.Ordinal}\t{evaluation.TargetTable}\t{evaluation.Outcome}\tE1={evaluation.OrchestrationTermsExempted}\tADDED={string.Join(" || ", evaluation.Added.Select(t => t.Raw.Replace('\n', ' ') + " => " + t.Normalized))}\tORIG={string.Join(" || ", evaluation.Original.Select(t => t.Raw.Replace('\n', ' ') + " => " + t.Normalized))}\n");
}
```

- **덤프가 보고서와 같은 자리를 읽었다는 증거.** 덤프는 434 행이다(= 「앵커가 서수로 해결된 문장 수」). 결말 합계는 일치 378 · 발화 5 · S2 12 · S3 31 · E2 8 이고 E1 항은 18 이다. 보고서의 침묵 분모와 한 칸도 다르지 않다.
- **자.** 사전은 스윕의 침묵 분모와 같은 조건 (B)다. 검사 발화는 (A)·(B) 가 같은 좌표였다.

| # | 좌표 | SP · 대상 | 더한 항(이행 원문) | 판정 | 근거 |
| ---: | :--- | :--- | :--- | :--- | :--- |
| 1 | Batch6/S12 INSERT 4 | SUMMARY_EXTRA · TSettleByOUT | `OUTYMD >= @v_strReqYMD` (S12.md:331) | **참양성** | 원본 INSERT 4(문장 시작 210행)의 WHERE(223~228행)에는 이 항이 없다. DELETE 4(문장 시작 193행)의 196행에만 있다. 설계 동기의 결함 그대로다 |
| 2 | Batch7/S12 INSERT 4 | SUMMARY_EXTRA · TSettleByOUT | `OUTYMD >= @p_reqYmd` (S12.md:209) | **참양성** | 1 과 같다 |
| 3 | Batch4/S15 INSERT 1 | SUMMARY_ETC · TSettleByOUT | `A.OUTSTATE = 9` · `A.USESTATE = 0` · `A.OUTYMD IS NOT NULL` (S15.md:161) | **참양성** | 결함을 만드는 항은 `A.OUTSTATE = 9` 하나다(아래 첫째 글머리). 나머지 둘은 커서가 넘긴 값과 겹쳐 무해하다 |
| 4 | Batch6/S13 DELETE 1 | SUMMARY_ETC · TSettleByOUT | `EXISTS (SELECT 1 FROM (커서 SELECT DISTINCT …) K WHERE K.YMD = …TSettleByOUT.YMD AND … AND K.OUTSTATE = …TSettleByOUT.OUTSTATE AND K.CompanySalesType = …TSettleByOUT.CompanySalesType …)` (S13.md:130~155) | **모양 발화**(실제 결함과 우연히 겹침 — 참양성으로 세지 않는다) | 커서 행 단위 DELETE(원본 59~72행, 변수 등식 13)를 EXISTS 한 항으로 바꿨다. 검사는 그 EXISTS 통째가 원본에 없어서 발화했다. 그 항 안에 원본에 없는 조건 둘이 있어 고르는 행이 실제로 다르지만, 검사가 그것을 짚은 것은 아니다(아래 둘째 글머리) |
| 5 | Batch4/S11 UPDATE 1 | PROC_ETC · TSettleMiss | `OutState = @p_intOutState` (S11.md:221) | **오탐** | 원본은 `OutState = 2`(97행)다. 이행은 리터럴 2 를 매개변수로 올렸다. 이 UPDATE 의 실행 자리는 `execute(SQL_UPDATE_MISS, …)` 하나(S11.md:71~76)이고, 거기서 `p_intOutState: 2` 를 바인딩한다(S11.md:75). 고르는 행이 같다. 원인은 아래 셋째 글머리 |

- **3 의 결함.** 원본 INSERT 1(81행)의 WHERE(97~109행)는 커서 변수 등식뿐이고 OUTSTATE 를 거르지 않는다. 그래서 같은 키의 OUTSTATE ≠ 9 행까지 다시 집계한다. GROUP BY 에도 OUTSTATE 가 있다. 앞선 DELETE 1(S15.md:124, 원본 59행과 일치)은 OUTSTATE 와 무관하게 그 키의 행을 모두 지운다. 따라서 이행은 **OUTSTATE ≠ 9 집계 행을 지우고 다시 넣지 않는다.** `A.USESTATE = 0` 과 `A.OUTYMD IS NOT NULL` 은 커서가 넘긴 `@p_UseState`(=0)·`@p_OutYMD`(비 NULL)와 겹친다.
- **4 는 모양 발화다 — 결함이 있지만 검사가 그것을 잡은 것은 아니다.**
  - 결함 (가) `K.OUTSTATE = …OUTSTATE`: 원본 DELETE 는 OUTSTATE 를 보지 않는다. 이행은 OUTSTATE = 9 행만 지운다.
  - 결함 (나) NULL 비안전 등식 `K.CompanySalesType = …`·`K.ExtraSettleFlag = …`: 원본은 `ISNULL(CompanySalesType,4) = ISNULL(@v,4)` 라 NULL 행도 지운다. 이행은 NULL 행을 못 지운다.
  - **검사는 이 둘을 짚어 발화하지 않았다.** EXISTS 항 통째가 원본에 없어서 발화했다. 메시지도 EXISTS 전체를 인용할 뿐, 실제로 행을 좁히는 `K.OUTSTATE = T.OUTSTATE` 를 가리키지 않는다. 충실한 집합 치환이었어도 같은 자리에서 같은 모양으로 발화했을 것이다.
  - **근거(리뷰어 확인).** 같은 SP 의 충실한 EXISTS 치환이 Batch4 `S15.md:87~101`(`SQL_CREATE_AND_CAPTURE_SHADOW`)에 있다. 그것은 ISNULL 을 지키고 OUTSTATE 항이 없다. 그 문장은 앵커 DML 이 아니어서 대조되지 않았다. 그러나 같은 EXISTS 가 DELETE 1 자리에 있었다면, 원본에 EXISTS 항이 없으므로 똑같이 발화했을 것이다. **이 검사가 EXISTS 를 가르는 기준은 내용이 아니라 모양이다.**
  - 그래서 4 는 참양성으로 세지 않는다. **「모양 발화 1 — 실제 결함과 우연히 겹침」**으로 센다. 결함 자체는 §7-8 결정 (c) 에 싣는다.
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
| Batch6 / S13 SUMMARY_ETC | 1 | 열지 않음(예측 없음) | **발화**(DELETE 1, 모양 발화 — 결함과 우연히 겹침). 같은 단계 INSERT 2 는 S2 |

14 의 집계는 셋으로 나뉜다.

- **사전 판정대로 간 것 11.** S12 둘 · COMM_UPD 다섯 · Batch6/S01 둘 · AcqManual 둘.
- **갈린 것 2.** Batch4/S11·S15 는 E2 가 아니라 발화했다.
- **예측이 없던 것 1.** Batch6/S13 은 「열지 않음」이었다.

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

**출력 꼬리 원문.** 모두 첫 판(`fb3a6f74`)을 쓸 때 잰 출력이다. 이 고침 판에서는 다시 돌리지 않았다.

첫 게이트 빌드 `dotnet build -warnaserror 2>&1 | tail -3`(증분):

```
    경고 0개
    오류 0개

경과 시간: 00:00:01.70
```

비증분 재빌드 `dotnet build -warnaserror --no-incremental 2>&1 | tail -5`:

```
빌드했습니다.
    경고 0개
    오류 0개

경과 시간: 00:00:01.93
```

비증분도 2 초 남짓으로 끝나, 컴파일이 정말 돌았는지 `-v:n` 을 붙여 한 번 더 돌렸다. `CoreCompile` 이 8 회였고, 5 프로젝트의 `-> …dll` 줄이 모두 나왔으며, 끝은 역시 `경고 0개 · 오류 0개` 였다.

`dotnet test tests/ReSet.Core.Tests 2>&1 | tail -25`:

```
  표준 출력 메시지:
 대조한 단계 70 · 앵커 보유 문장 434 · 불일치 8
   [Job] POQSettleBatch1 · 단계 14 · 앵커 보유 문장 92 · 불일치 0
   [Job] POQSettleBatch4 · 단계 14 · 앵커 보유 문장 78 · 불일치 0
   [Job] POQSettleBatch5 · 단계 14 · 앵커 보유 문장 92 · 불일치 0
   [Job] POQSettleBatch6 · 단계 14 · 앵커 보유 문장 80 · 불일치 8
   [Job] POQSettleBatch7 · 단계 14 · 앵커 보유 문장 92 · 불일치 0
   POQSettleBatch6/S01 · INSERT 앵커 6 · 대상 TPGSettleRate4Extra — 명세서에 그 쌍이 없다
   POQSettleBatch6/S01 · DELETE 앵커 7 · 대상 TClientSettleRate4Extra — 명세서에 그 쌍이 없다
   POQSettleBatch6/S01 · INSERT 앵커 8 · 대상 TClientSettleRate4Extra — 명세서에 그 쌍이 없다
   POQSettleBatch6/S01 · DELETE 앵커 9 · 대상 TClientSettleRate4MobileCo — 명세서에 그 쌍이 없다
   POQSettleBatch6/S01 · INSERT 앵커 10 · 대상 TClientSettleRate4MobileCo — 명세서에 그 쌍이 없다
   POQSettleBatch6/S09 · DELETE 앵커 22 · 대상 TStatPGCollect — 명세서에 그 쌍이 없다
   POQSettleBatch6/S09 · INSERT 앵커 31 · 대상 TStatPGCollect — 명세서에 그 쌍이 없다
   POQSettleBatch6/S13 · INSERT 앵커 2 · 대상 TSettleByOUT — 명세서에 그 쌍이 없다



실패!  - 실패:     1, 통과:  4205, 건너뜀:     0, 전체:  4206, 기간: 56 s - ReSet.Core.Tests.dll (net10.0)
```

### 7-8. 되돌림 조건 대조 (§4)

| 조건 | 결과 |
| :--- | :--- |
| 1. 원인을 설명할 수 없는 발화나 오탐 | **걸렸다** — Batch4/S11 UPDATE 1 오탐. 판정 근거는 §7 머리의 「왜 「걸렸다」로 적는가」 |
| 2. 기존 검사 발화 차분 ≠ 0 | 아니다(차분 0) |
| 3. S12 둘 중 미발화 | 아니다(둘 다 발화) |
| 4. 양성 대조군 미발현 | 아니다(② ③ 모두 발현) |

**결과: 병합하지 않고 설계로 돌아온다 — 사람 결정 대기.** §3-2 합격선 ④(정확히 둘)와 ⑤(오탐 0)도 깨졌다. §4 가 「즉석 면제로 막지 않는다」고 했으므로, 이 창에서는 면제·정규화·시험을 한 줄도 바꾸지 않았다. 사람이 정할 것은 셋이다.

**(a) 리터럴 ↔ 바인딩 매개변수를 동치로 볼 규칙을 둘 것인가.**
- 표본은 Batch4/S11 하나다. 이 UPDATE 의 실행 자리는 하나(S11.md:71~76)이고 `p_intOutState: 2` 를 바인딩한다.
- 같은 PROC_ETC 를 옮긴 다른 Job(Batch1/S09 · Batch5/S14 · Batch6/S14 · Batch7/S15)은 리터럴 `OutState = 2` 를 그대로 둔다.
- §2-2 대가 2 가 말한 「스윕에서 실물로 나올 때」가 이것이다. 규칙을 더하면 설계 변경이다. 이 바인딩 값이 리터럴과 같은지를 기계가 어디서 읽을지(단계 의사코드의 `execute` 인자)도 함께 정해야 한다.

**(b) 「커서 → EXISTS 집합 치환」 모양 발화를 어떻게 다룰 것인가.**
- 실물은 Batch6/S13 DELETE 1 이다. 검사는 EXISTS 를 내용이 아니라 모양으로 가른다. 같은 SP 의 충실한 EXISTS(Batch4 `S15.md:87~101`, ISNULL 유지 · OUTSTATE 항 없음)도 같은 모양이다.
- 메시지는 실제로 행을 좁히는 조건(`K.OUTSTATE = T.OUTSTATE`)을 짚지 않는다.
- 선택지는 세 갈래다. 이 모양을 침묵시킬 것인가, EXISTS 안쪽 상관 등식을 원본 항과 맞대는 규칙을 둘 것인가, 모양 발화로 두고 메시지를 바꿀 것인가.

**(c) 새로 드러난 실제 결함 셋을 결함 목록에 올릴 것인가.**
- Batch4/S15 INSERT 1 — 재집계에 `A.OUTSTATE = 9` 를 더했다. 앞선 DELETE 1 이 키의 모든 OUTSTATE 행을 지우므로, **OUTSTATE ≠ 9 집계 행이 지워지고 다시 들어가지 않는다.** 검사 P 가 잡은 참양성이다.
- Batch6/S13 DELETE 1 — **OUTSTATE = 9 행만 지운다.** 또 `ISNULL(CompanySalesType,4)`·`ISNULL(ExtraSettleFlag,9)` 대조를 버려 NULL 행을 못 지운다. 검사 P 는 모양으로만 발화했다(b).
- Batch6/S13 INSERT 2 — Batch4/S15 와 **같은 `A.OUTSTATE = 9` 결함**이다(S13.md:262). 키가 어긋나 S2 로 침묵했다(§5 「키가 어긋난 U-앵커 자리는 도달하지 못한다」의 실물).

---

## 8. 2차 설계 — 사람 결정 (2026-09-12)

§7-8 의 셋을 사람이 추천대로 정했다. **이 절은 코드보다 먼저 커밋한다** — 재측정(Task 9)의 합격선과 되돌림 조건이 여기 있다.

### 8-1. (a) R7 — 원본 리터럴 ↔ 이행 매개변수

| 규칙 | 내용 |
| :--- | :--- |
| R7 | 원본 항이 `컬럼식 = 리터럴`(한 변이 리터럴 — 부호 붙은 수·괄호 포함, 다른 변이 컬럼을 품는다)이면, 그 항은 이행의 `컬럼식 = @V` 와도 일치로 본다. **한 방향이다** — 원본의 변수를 이행이 리터럴로 굳힌 것(`IssueType = @v_intIssueType` → `IssueType = 15`)은 여전히 발화한다. **`=` 만** 받는다 — `<>`·부등호·IN 목록은 실물이 나올 때 더한다 |

- **바인딩 값은 읽지 않는다.** §7-8 (a) 가 물은 「바인딩 값이 리터럴과 같은지를 어디서 읽을지」의 답은 「읽지 않는다」다. 근거 둘:
  - R2 가 이미 변수 이름을 지우므로, 이 검사는 원래 바인딩 값을 보지 못한다. R7 이 새로 버리는 정보가 없다.
  - 단계 의사코드의 `execute` 인자는 산문과 표가 섞인 자리라, 파싱하면 새 오탐 원천이 된다.
- **대가**는 §5 에 더했다 — 다른 값을 바인딩해도 조용하다.
- **구현**: 정규화기가 항마다 `LiteralAsParameter`(원본 쪽 대조 키, 해당 없으면 null)를 함께 낸다. 판정은 이행 항의 `Normalized` 가 원본의 `Normalized` 또는 `LiteralAsParameter` 중 하나와 같으면 일치로 본다.

### 8-2. (b) EXISTS 모양 발화 — 알려진 대가로 둔다

- 규칙을 만들지 않는다. 오늘 코퍼스에서 이 모양의 발화는 Batch6/S13 DELETE 1 하나이고, 진짜 결함 위에 났다. 발화문(「원본에 없는 최상위 술어」)은 문자 그대로 참이다.
- 잠재 오탐 부류로 §5 에 적었다. **충실한** EXISTS 치환이 앵커 문장에서 발화하는 실물이 나오면 그때 규칙을 만든다 — R7 과 같은 규약이다.

### 8-3. (c) 결함 셋 — 기록했다

`docs/audit-reports/2026-09-05-축B-잔여결함-분류.md` §22 에 올렸다(Batch4/S15 INSERT 1 · Batch6/S13 DELETE 1 · Batch6/S13 INSERT 2).

### 8-4. 재측정 합격선 (Task 9 — 착수 전에 적는다)

BASE 는 §7 과 같은 `58da1f1a`, AFTER 는 §7 의 `6e5365c0`, AFTER2 는 R7 을 통합한 끝이다.

| 합격선 | 기대 |
| :--- | :--- |
| ① A–E · 미분류 | BASE 와 같다 |
| ② P 발화 좌표(조건 무관, 중복 제거) | **정확히 넷** — Batch6/S12 INSERT 4 · Batch7/S12 INSERT 4 · Batch4/S15 INSERT 1 · Batch6/S13 DELETE 1 |
| ③ 오탐 | **0**. Batch6/S13 DELETE 1 은 모양 발화(§8-2)로 따로 세고, 오탐으로 세지 않는다 |
| ④ R7 의 효과 | AFTER 대비 사라진 P 좌표가 **정확히 Batch4/S11 UPDATE 1 하나**, 새로 생긴 좌표 0. 「대조까지 간 문장 수」는 AFTER(383)와 같다 |
| ⑤ 양성 대조군 | R7 대조를 끄면 Batch4/S11 UPDATE 1 이 다시 발화한다 · 배선을 끄면 P 가 0 이다 |
| ⑥ COMM_UPD UPDATE 3 다섯 곳 | 조용하다 |
| ⑦ 게이트 | 실패는 `AnchorKindOrdinalPairTests` 하나 · 건너뜀 0 · 경고 0 · `output/Jobs` 에 `*-resume` 없음(시작과 끝 둘 다) |

### 8-5. 2차 되돌림 조건

§4 의 넷에 아래 셋을 더한다. 하나라도 나오면 병합하지 않고 다시 설계로 돌아온다. 즉석 면제로 막지 않는다.

5. ② 의 넷 밖 좌표가 발화하거나, 넷 중 하나가 발화하지 않는다.
6. ④ 에서 사라지거나 새로 생긴 좌표가 Batch4/S11 UPDATE 1 밖에 있다.
7. ⑤ 의 R7 양성 대조군이 튀어나오지 않는다.

---

## 9. 재측정 (2026-09-12, BASE 58da1f1a · AFTER 6e5365c0 · AFTER2 69cd1a99)

**결론 먼저 — §8-4 합격선 ①~⑦ 이 모두 통과했고, 되돌림 조건(§4 의 넷 + §8-5 의 셋)은 하나도 걸리지 않았다.**

- P 발화 좌표는 **정확히 넷**이다. §7 의 다섯에서 **Batch4/S11 UPDATE 1 하나만 사라졌고 새로 생긴 좌표는 없다**.
- 오탐 0. 남은 넷은 참양성 3 · 모양 발화 1(Batch6/S13 DELETE 1 — §8-2 의 알려진 대가)이다.
- 면제·정규화기·검사·시험은 한 줄도 바꾸지 않았다. 되돌림 실험 둘은 버리는 워크트리에서 돌렸고 커밋하지 않았다.

### 9-1. 코퍼스 조립과 측정 창

- 워크트리 루트에 `output` 심링크 하나(`→ /Users/payletter/git-root/ReSet/output`)만 걸었다. 조립본은 만들지 않았다(§7-1 과 같은 길).
- **시작 목록**(`ls output/Jobs/`): `POQSettleBatch1 · POQSettleBatch4 · POQSettleBatch5 · POQSettleBatch6 · POQSettleBatch7`. `-resume` 없음.
- **게이트 뒤 목록**: 같다. 측정 창 안에 다른 세션의 복사본이 끼지 않았다.
- 실행 조건은 §7 과 같다. 측정 쌍 83(Job 5) · 캐시 `FormatVersion` {22}·항목 31 · 명세서 세대 2026-09-10 · 단계 번들 세대 2026-09-04~2026-09-11 · 작업 트리 깨끗 · 미해결 프로시저 참조 0.
- **BASE 재실행.** `58da1f1a` 를 분리한 임시 워크트리에서 다시 돌렸다. 나온 보고서는 이미 커밋된 `docs/audit-reports/sweeps/2026-09-11-step-sweep-base.md` 와 **바이트 동일**하다(`diff` 차이 0 — 「커밋」 줄까지 같다). 그래서 **중복 파일을 새로 더하지 않는다**. §7 의 A–E·미분류가 그대로 재현된다는 뜻이다.
- AFTER2 보고서: `docs/audit-reports/sweeps/2026-09-12-step-sweep.md`.

### 9-2. 합격선 §8-4 ①~⑦

| 합격선 | 기대 | 실측 | 판정 |
| :--- | :--- | :--- | :--- |
| ① A–E·미분류 | BASE 와 같다 | 두 조건 모두 A 0 · B 14 · C 2 · D 0 · E 0 · 미분류 48. BASE 재실행본과 같고 §7 과도 같다. 검사 B·C 발화 목록 32 행도 한 행도 다르지 않다 | **통과** |
| ② P 좌표(조건 무관, 중복 제거) | 정확히 넷 | **넷** — Batch4/S15 INSERT 1 · Batch6/S12 INSERT 4 · Batch6/S13 DELETE 1 · Batch7/S12 INSERT 4. (A)·(B) 가 같은 좌표라 8 행이다 | **통과** |
| ③ 오탐 | 0 | **0**. 넷의 내역은 참양성 3 · 모양 발화 1(Batch6/S13 DELETE 1, §8-2) | **통과** |
| ④ R7 의 효과 | 사라진 좌표 정확히 Batch4/S11 UPDATE 1 하나 · 새 좌표 0 · 대조 383 불변 | 사라진 좌표 **{Batch4/S11 UPDATE 1}** 하나뿐 · 새 좌표 **0** · `PredicateTermStatementsCompared` **383**(AFTER 와 같다) | **통과** |
| ⑤ 양성 대조군 | R7 대조를 끄면 Batch4/S11 재발화 · 배선을 끄면 P 0 | 둘 다 그대로 나왔다(§9-5) | **통과** |
| ⑥ COMM_UPD UPDATE 3 다섯 곳 | 조용하다 | 다섯 곳 모두 **일치(Matched)**. P 목록에 없다 | **통과** |
| ⑦ 게이트 | 실패는 `AnchorKindOrdinalPairTests` 하나 · 건너뜀 0 · 경고 0 · `*-resume` 없음 | 실패 1 · 통과 4210 · 건너뜀 0 · 경고 0 · 시작과 끝 목록 모두 `-resume` 없음 | **통과** |

### 9-3. P 네 좌표 — 원본 DDL·단계 파일로 다시 판정했다

§7-3 을 베끼지 않고 원본(`output/Objects/<SP>.Procedure/raw/object_definition.sql`)과 이행(`output/Jobs/<Job>/agent/steps/<단계>.md`)을 다시 열어 판정했다. **네 좌표 모두 §7-3 의 판정과 같다 — 다른 의견 없음.** 행 번호 규약은 §7-3 과 같다(문장을 가리키면 문장 시작 행, 항을 가리키면 그 항의 행).

| # | 좌표 | SP · 대상 | 더한 항(이행 원문) | 판정 | 근거(이 창에서 직접 읽은 것) |
| ---: | :--- | :--- | :--- | :--- | :--- |
| 1 | Batch6/S12 INSERT 4 | SUMMARY_EXTRA · TSettleByOUT | `OUTYMD >= @v_strReqYMD` (S12.md:331) | **참양성** | 원본 INSERT 4 의 WHERE(223~228행)는 `ProcYMD` · `YMD >=` · `ISNULL(OUTYMD,'') <> ''` · `PGNAME IN` · `CompanySalesType IN` · `ExtraSettleFlag = 1` 여섯뿐이고 `OUTYMD >=` 가 **없다**. 그 항은 DELETE 4 의 196행에만 있다. 이행은 DELETE 의 WHERE 를 INSERT 로 베꼈다 |
| 2 | Batch7/S12 INSERT 4 | SUMMARY_EXTRA · TSettleByOUT | `OUTYMD >= @p_reqYmd` (S12.md:209) | **참양성** | 1 과 같은 자리·같은 원본. 변수 이름만 다르다(R2 가 `@V` 로 지운다) |
| 3 | Batch4/S15 INSERT 1 | SUMMARY_ETC · TSettleByOUT | `A.OUTSTATE = 9` · `A.USESTATE = 0` · `A.OUTYMD IS NOT NULL` (S15.md:161) | **참양성** | 원본 INSERT 1(81행)의 WHERE(97~109행)는 커서 변수 등식 열둘뿐이고 OUTSTATE 를 거르지 않는다. 앞선 DELETE 1(S15.md:124~131)은 원본 DELETE(59~72행)와 같아 그 키의 **모든** OUTSTATE 행을 지운다. 그래서 이행은 OUTSTATE ≠ 9 집계 행을 지우고 다시 넣지 않는다. `A.USESTATE = 0`·`A.OUTYMD IS NOT NULL` 은 커서가 넘긴 값과 겹쳐 무해하고, 결함을 만드는 항은 `A.OUTSTATE = 9` 하나다 |
| 4 | Batch6/S13 DELETE 1 | SUMMARY_ETC · TSettleByOUT | `EXISTS (SELECT 1 FROM (SELECT DISTINCT …) K WHERE K.YMD = …TSettleByOUT.YMD AND …)` (S13.md:130~155) | **모양 발화**(§8-2 — 오탐으로 세지 않는다) | 원본은 커서 행 단위 DELETE(59~72행, 변수 등식 열둘)다. 이행은 EXISTS 한 항으로 바꿨고, 검사는 **그 EXISTS 통째가 원본 최상위에 없어서** 발화했다. 그 안에 진짜 결함 둘이 있다 — (가) `K.OUTSTATE = …OUTSTATE`(S13.md:151)는 원본에 없다(OUTSTATE = 9 행만 지운다), (나) `K.CompanySalesType = …`(152)·`K.ExtraSettleFlag = …`(154)는 원본의 `ISNULL(CompanySalesType,4) = ISNULL(@v,4)`·`ISNULL(ExtraSettleFlag,9) = ISNULL(@v,9)`(71~72행)를 버려 NULL 행을 못 지운다. **그러나 검사가 짚은 것은 이 둘이 아니다** — 충실한 치환이었어도 같은 자리에서 같은 모양으로 발화했을 것이다 |

**사라진 좌표(§7-3 #5)도 열어 확인했다.** Batch4/S11 UPDATE 1 은 원본 `OutState = 2`(PROC_ETC 97행)를 이행이 `OutState = @p_intOutState`(S11.md:221)로 올린 자리다. 그 UPDATE 의 실행 자리는 `execute(SQL_UPDATE_MISS, …)` 하나(S11.md:71~76)이고 거기서 `p_intOutState: 2`(S11.md:75)를 바인딩한다. 고르는 행이 같으므로 **결함이 아니다**. R7 이 이 자리를 일치로 돌렸고, 문장별 덤프에서 결말이 `Matched` 임을 확인했다. R7 이 막는 것은 여기까지다 — 다른 값을 바인딩했다면 조용했을 것이다(§5 의 R7 대가).

### 9-4. §8-4 ④ — P 좌표 차분과 침묵 분모 아홉

**P 좌표 차분(AFTER → AFTER2).** 두 보고서를 `diff` 로 맞댔다. 차이는 네 자리뿐이다: 「커밋」 줄(`6e5365c0` → `69cd1a99`), 발화량 표의 P 행(5 → 4, Batch4 의 Job 별 P 2 → 1), P 발화 목록에서 `Batch4/S11 UPDATE 1` 의 두 행(조건 A·B) 삭제, 침묵 분모의 「발화한 문장 수」(5 → 4). **사라진 좌표는 {Batch4/S11 UPDATE 1} 하나, 새로 생긴 좌표는 0 이다.** 다른 검사의 행은 한 줄도 움직이지 않았다.

**침묵 분모 아홉(AFTER2, 조건 (B) 사전) — §7-5 와 나란히.**

| 분모 | AFTER(§7-5) | AFTER2 | 차 |
| :--- | ---: | ---: | ---: |
| 원본과 대조까지 간 앵커 DML 문장(`PredicateTermStatementsCompared`) | 383 | **383** | 0 |
| 그중 일치(Matched) | 378 | **379** | **+1** |
| 발화한 문장(Fired) | 5 | **4** | **−1** |
| S1 원본 DDL 없음 | 0 | 0 | 0 |
| S2 키 없음·모호 | 12 | 12 | 0 |
| S3 원본 최상위 항 없음 | 31 | 31 | 0 |
| E2 커서 그룹 면제 | 8 | 8 | 0 |
| E3 스테이징만 읽음 | 0 | 0 | 0 |
| S5 L1 소진 배너 | 0 | 0 | 0 |
| E1 오케스트레이션 항으로 면제한 **항** 수 | 18 | 18 | 0 |

- **기대대로 Matched 가 +1, Fired 가 −1 이고 나머지 일곱은 전부 0 이다.** 곧 R7 은 한 문장을 발화에서 일치로 옮겼을 뿐, 도달 자체(383)를 줄이지 않았다. 침묵으로 숨긴 것이 아니다 — 침묵 분모 S1~S5·E1~E3 이 한 칸도 늘지 않았다.
- **합이 앵커 창을 덮는다.** 383 + 12 + 31 + 8 + 0 + 0 + 0 = **434** = 「앵커가 서수로 해결된 문장 수」. 빠진 문장이 없다.

**문장별 덤프 — 커밋하지 않았다. 재현법은 §7-5 의 것을 그대로 썼다.** 같은 코드를 같은 자리(`StepSweepService.cs` 의 `predicateTermOrchestration += evaluation.OrchestrationTermsExempted;` 바로 뒤)에 넣고 `RESET_PROBE_PATH=<덤프> dotnet run --project src/ReSet.Cli -- --sweep` 로 돌렸다. 다른 점 하나: **AFTER2 워크트리를 더럽히지 않으려고 `69cd1a99` 에서 분리한 버리는 워크트리에 probe 를 넣고 돌린 뒤 그 워크트리를 통째로 지웠다**(§7-5 는 제자리에서 고치고 `git checkout --` 로 되돌렸다). 그래서 이 창에서는 AFTER2 워크트리의 `src`·`tests` 가 측정 내내 `69cd1a99` 와 한 바이트도 다르지 않았다.

- **덤프가 보고서와 같은 자리를 읽었다는 증거.** 덤프는 **434 행**이다(= 「앵커가 서수로 해결된 문장 수」). 결말 합계는 일치 379 · 발화 4 · S2 12 · S3 31 · E2 8 이고 E1 항은 18 이다. **보고서의 침묵 분모와 한 칸도 다르지 않다.**
- **덤프로 확인한 것 둘.** (ㄱ) 합격선 ⑥ — COMM_UPD UPDATE 3 다섯 곳(Batch1/S07 · Batch4/S08 · Batch5/S05 · Batch6/S05 · Batch7/S06)이 모두 `Matched` 다(같은 SP 의 UPDATE 7 다섯은 §7-5 처럼 S3). (ㄴ) Batch4/S11 UPDATE 1 이 `Matched` 다(같은 단계 INSERT 1 은 S3 — §7-4 와 같다).

### 9-5. 없앴을 때 둘 (§8-4 ⑤)

R2·R5 는 R7 이 건드리지 않으므로 반복하지 않았다(계획 Task 9). 대신 합격선 ⑥ 을 덤프로 다시 확인했다(위 (ㄱ)). **두 실험 모두 `69cd1a99` 에서 분리한 버리는 워크트리에서 돌렸고, 실험이 낸 보고서와 고친 코드는 커밋하지 않았다.**

| 실험 | 바꾼 것 | 기대 | 실측 | 판정 |
| :--- | :--- | :--- | :--- | :--- |
| ④ R7 대조 | `MechanicalValidator.EvaluateAnchoredPredicateTerms` 의 `originalKeys.UnionWith(originalTerms.Select(t => t.LiteralAsParameter).OfType<string>());` 한 줄 삭제 | Batch4/S11 UPDATE 1 이 P 에 다시 나온다 | **다시 나왔다.** P 5 좌표(10 행) = AFTER(§7-2)와 같은 다섯. A 0 · B 14 · C 2 · D 0 · E 0 · 미분류 48 과 B·C 목록 32 행은 그대로. 대조 383 불변, 발화 4 → 5 | **통과** |
| ① 배선 | `MechanicalValidator.cs:650~651` 의 `SafeCheck(() => CheckAnchoredStatementPredicateTerms(…))` 주석 처리 | P = 0, 나머지는 AFTER2 와 같다 | **P 0 · 발화 목록 빈 표.** A 0 · B 14 · C 2 · D 0 · E 0 · 미분류 48 · B·C 목록 32 행이 BASE 보고서와 한 행도 다르지 않다 | **통과** |

- **④ 는 R7 한 줄이 그 한 좌표를 정확히 사 주고 있다는 양성 대조군이다.** 되돌리면 §7 의 다섯으로 완전히 돌아가고, 되돌리지 않으면 넷이다. 차분이 그 한 좌표에만 걸린다.
- **① 에서 「발화한 문장 수」 지표는 4 로 남았다.** §7-5 가 적은 그대로다 — 이 지표는 판정 함수를 직접 부르므로 **배선을 재지 않는다**. 배선을 재는 자는 P 발화 수 쪽이다. 이번에도 같은 모양이 재현됐다.

### 9-6. 게이트 (§8-4 ⑦)

| 명령 | 결과 |
| :--- | :--- |
| `dotnet build -warnaserror` | 경고 0 · 오류 0 |
| `dotnet build -warnaserror --no-incremental` | 경고 0 · 오류 0 |
| `dotnet test tests/ReSet.Core.Tests` | 실패 1 · 통과 4210 · **건너뜀 0** · 전체 4211 |

**출력 꼬리 원문.** `dotnet build -warnaserror 2>&1 | tail -5`:

```
빌드했습니다.
    경고 0개
    오류 0개

경과 시간: 00:00:02.03
```

`dotnet test tests/ReSet.Core.Tests 2>&1 | tail -30` (꼬리):

```
   POQSettleBatch6/S01 · INSERT 앵커 6 · 대상 TPGSettleRate4Extra — 명세서에 그 쌍이 없다
   POQSettleBatch6/S01 · DELETE 앵커 7 · 대상 TClientSettleRate4Extra — 명세서에 그 쌍이 없다
   POQSettleBatch6/S01 · INSERT 앵커 8 · 대상 TClientSettleRate4Extra — 명세서에 그 쌍이 없다
   POQSettleBatch6/S01 · DELETE 앵커 9 · 대상 TClientSettleRate4MobileCo — 명세서에 그 쌍이 없다
   POQSettleBatch6/S01 · INSERT 앵커 10 · 대상 TClientSettleRate4MobileCo — 명세서에 그 쌍이 없다
   POQSettleBatch6/S09 · DELETE 앵커 22 · 대상 TStatPGCollect — 명세서에 그 쌍이 없다
   POQSettleBatch6/S09 · INSERT 앵커 31 · 대상 TStatPGCollect — 명세서에 그 쌍이 없다
   POQSettleBatch6/S13 · INSERT 앵커 2 · 대상 TSettleByOUT — 명세서에 그 쌍이 없다



실패!  - 실패:     1, 통과:  4210, 건너뜀:     0, 전체:  4211, 기간: 55 s - ReSet.Core.Tests.dll (net10.0)
```

- **실패는 `AnchorKindOrdinalPairTests.EveryUAnchorPointsAtAStatementTheSpecActuallyDeclares` 하나다.** 의도된 실패다(§3-3). 꼬리는 「대조한 단계 70 · 앵커 보유 문장 434 · 불일치 8」이고, 불일치 8 은 §7-7 과 같은 자리(전부 Batch6 — S01 앵커 6~10 · S09 앵커 22·31 · S13 INSERT 앵커 2)다. 약하게 만들지도, 8 로 못박지도 않았다.
- **통과 수가 §7-7 의 4205 에서 4210 으로 늘었다.** Task 8 이 더한 R7 시험 다섯(정규화기 2 · 검사 3)이다. 통과 절대 수는 게이트가 아니다 — 게이트는 실패 1·건너뜀 0·경고 0 이다.

### 9-7. 되돌림 조건 대조 (§4 넷 + §8-5 셋)

| 조건 | 결과 |
| :--- | :--- |
| 1. 원인을 설명할 수 없는 발화나 오탐 | **아니다** — 오탐 0. 남은 넷은 참양성 3 과 §8-2 가 미리 알려진 대가로 받아 둔 모양 발화 1 이다 |
| 2. 기존 검사 발화 차분 ≠ 0 | **아니다** — A·B·C·D·E·미분류와 B·C 목록 32 행이 BASE 와 같다 |
| 3. S12 둘 중 미발화 | **아니다** — 둘 다 발화 |
| 4. 양성 대조군 미발현 | **아니다** — §9-5 의 둘 모두 발현 |
| 5. ② 의 넷 밖 좌표 발화 · 넷 중 미발화 | **아니다** — 발화 좌표가 정확히 그 넷이다 |
| 6. ④ 의 차분이 Batch4/S11 UPDATE 1 밖 | **아니다** — 사라진 좌표 하나뿐, 새 좌표 0 |
| 7. ⑤ 의 R7 양성 대조군 미발현 | **아니다** — R7 줄을 지우자 Batch4/S11 이 정확히 되살아났다 |

**결과: 되돌림 조건이 하나도 걸리지 않았다.** §8-4 의 일곱 합격선도 모두 통과다. 병합 여부는 사람이 정한다(계획서 「병합」 절 — 최종 전체 리뷰 → 사람 승인 순서).

**남아 있는 것 — 이 창이 닫지 않은 것.** 아래는 이번 측정의 결론을 바꾸지 않지만 §5·§8-3 에 이미 적힌 그대로 남아 있다.

- Batch6/S13 INSERT 2 는 Batch4/S15 와 같은 `A.OUTSTATE = 9` 결함인데 **S2(키 어긋남)로 여전히 침묵한다**(§7-3, §8-3). R7 은 이 자리를 건드리지 않는다.
- E3 0 은 「안전하다」가 아니라 「재지 않았다」다 — 계보 원천을 가진 문장이 이 코퍼스에서 0 이라 도달 불가다(§7-5 와 같다).
- 모양 발화(Batch6/S13 DELETE 1)의 메시지는 실제로 행을 좁히는 `K.OUTSTATE = T.OUTSTATE` 를 짚지 않는다. §8-2 의 규약대로 **충실한** EXISTS 치환이 앵커 문장에서 발화하는 실물이 나오면 그때 규칙을 만든다.
