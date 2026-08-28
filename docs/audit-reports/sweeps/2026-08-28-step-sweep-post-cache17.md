# 단계 검사 스윕

## 이 보고서의 자리

**캐시 16 → 17 승격과 명세서 전건 재생성 후의 측정이다.** 기준선은
`2026-08-27-step-sweep-pre-cache17.md`(커밋 `9b20244`, 조건 (B) 46 좌표).

- 승격 커밋 `58c1ef6` · 기준선 `dfd99c8` · 이 측정 `6c57905`
- 코퍼스: **31개 전부 `FormatVersion 17`** (측정 시각 2026-08-28 15:30)
- 재생성 제외 객체: **없음**
- 설계 `docs/superpowers/specs/2026-08-27-cache17-promotion-design.md`

## 결론 셋

### 1. 검사 B·C — 예측이 정확히 맞았다. 차분 네 버킷이 전부 0이다

| 영역 | 건수 |
| :--- | ---: |
| `pre-(B) ∩ post-(A)` 예측대로 켜졌다 | **46** |
| **`pre-(B) − post-(B)` 줄어든 자리** | **0** |
| `post-(B) − post-(A)` 전사 오류 | **0** |
| `post-(A) − post-(B)` 모사가 못 낸 발화 | **0** |
| `post-(B) − pre-(B)` 새로 켜졌다 | **0** |

승격 전에 「캐시 17이 들어오면 켜진다」고 예측한 **46 좌표가 승격 후 정확히 그 46**이다.
조용히 사라진 것도, 예측 밖에서 켜진 것도 없다.

**「(B)는 상한이지 예측이 아니다」는 유보가 이 회차에 한해 닫혔다.** 기준선 보고서는 모델이
표를 완전히 전사한다고 가정한 값이라 상한이라고 적었는데, **실제 전사가 완전해서 상한이
그대로 실측이 됐다**(전사 오류 0). L1의 `CheckErrorCodes`가 세 칸을 등호로 맞대는 강제가
그것을 지켰다.

### 2. 침묵 분모 — 열 값이 하나도 안 움직였다

| 분모 | 승격 전 | 승격 후 |
| :--- | ---: | ---: |
| 앵커가 서수로 해결된 문장 수 | 856 | 856 |
| 앵커는 있으나 서수로 환산되지 않은 문장 수 | 233 | 233 |
| (Kind, Ordinal) 모호성 가드가 버린 문장 수 | 84 | 84 |
| 계보 원천을 가진 문장 수 | 142 | 142 |
| 스테이징만 읽어 검사 C 가 면제한 문장 수 | 105 | 105 |
| 자기 대상을 읽는 문장 수 | 786 | 786 |
| **자기 대상을 읽어 스테이징 면제가 취소된 문장 수** | **35** | **35** |
| 하위 범위 술어 컬럼을 가진 문장 수 | 252 | 252 |
| 하위 범위 술어 컬럼의 총수 | 3408 | 3408 |
| 스테이징 원천의 총수 | 141 | 141 |

**새로 생긴 침묵이 없다.** 이 계수들은 조건 (B) 사전으로 세므로 승격 전에도 이미 「승격 후의
관할」을 재고 있었고, 그 값이 승격 후 실제와 일치했다. 계기 자체는 변이로 검증돼 있다
(`2026-08-27-silence-denominator-mutations.md` — 변이 셋 전부 죽음).

### 3. ⚠ 그러나 검사 D 가 통째로 꺼졌다 — 좌표 차분이 못 보는 자리였다

| 검사 | 승격 전 (A) | 승격 후 (A) |
| :--- | ---: | ---: |
| A | 20 | 20 |
| B | 0 | **26** |
| C | 0 | **20** |
| **D** | **18** | **0** |
| E | 59 | 59 |
| 미분류 | 1163 | **1095** |

**검사 D(`CheckSpecLocalVariablesDeclared`)의 발화 18건이 0이 됐다.**
승격 전 발화는 `POQSettleBatch1` 9건 · `POQSettleProc14` 9건이었다.

**단계 SQL 이 바뀐 것이 아니다.** 단계 번들은 이 회차에 재생성되지 않았다
(`output/Jobs` 최신 mtime 2026-08-24, 오늘 변경 0건). 즉 **검사가 재료를 잃었다.**

실측 — 명세서의 「지역 변수 표」 행 수:

```
합계  옛(스냅샷) 16행 → 새 0행
  UP_UTIL_SETTLE_EXCEPTION_PROC   4행 → 0   표가 통째로 사라짐
  UP_UTIL_SETTLE_PROC_ETC        12행 → 0   표가 통째로 사라짐
```

`CheckSpecLocalVariablesDeclared` 는 `facts.SelectMany(f => f.LocalVariables)` 를 재료로 쓰고
`if (variables.Count == 0) return;` 로 **조기 반환**한다. 표가 사라지자 검사가 조용히 꺼졌다.

**이 표는 아무도 강제하지 않는다.**

| | |
| :--- | :--- |
| `MachineConfirmedTables` 카탈로그 | **없음** |
| 존재를 요구하는 L1 검사 | **없음** |
| 프롬프트가 요구하는 문구 | **없음** |
| 그런데 검사 D 는 | 이 표를 재료로 쓴다 |

순전히 **모델 재량**이다. 옛 모델(`gpt-5.6-terra`)은 썼고 새 모델(`deepseek-v4-pro-0813`)은 안 쓴다.

### 원인 귀속 — 캐시 17 이 아니라 모델 교체다

같은 회차에 Actor/Critic 모델이 바뀌었다(`gpt-5.6-terra`/`claude-opus-5` →
`deepseek-v4-pro-0813`/`glm-5.2`). 둘을 갈라야 한다.

- **기계 확정 표**(추출기가 렌더하고 L1 이 등호로 강제): 프로시저에서 **차이 0**으로 실측됐다.
  검사 B·C 가 읽는 층이 여기라 그 결과가 깨끗한 것이다.
- **재량 절**(모델이 자유 서술): 바뀌었다. 프로시저 14개 대조에서 새 세대가
  **「오류 코드」를 12개 객체에 새로 싣고**(의도한 변화), 「원본 주석 기록」 3 · 「지역 변수 및
  시스템 값」 1 · 「내부 변수와 컬럼 관계」 1 · 「제약 및 일관성 고려사항」 1 · 「출력값 규약」 1
  등을 **잃었다.**

**따라서 검사 D 의 침묵과 미분류 −68 은 캐시 17 의 효과가 아니라 모델 교체의 효과다.**
캐시 17 탓으로 적으면 안 된다.

### 잰 것과 안 잰 것

- **잰 것**: 조건 (A)·(B) 발화, 3자 좌표 차분, 침묵 분모 전후, 기계 확정 표 전건 대조,
  지역 변수 표 전후, 절 목록 전후, 오류 코드 표 행 수 대 사실 수(12/12 일치)
- **안 잰 것**: 미분류 1095 의 내부 분포(어느 검사가 몇 건인지) · 검사 D 가 잃은 18 건이
  **진짜 결함이었는지**(승격 전 보고서가 좌표를 안 실었다) · 모델을 되돌렸을 때 지역 변수
  표가 돌아오는지

## 실행 조건

- 커밋: `6c57905`
- 작업 트리: 깨끗
- 캐시 인덱스 `FormatVersion` 집합: {17} — 항목 31개
- 측정 쌍: 326 (Job 18개)
- 단계 파일 누락: 51
- 목차 파싱 실패 Job: POQSettleProc7
- 목차 단계 수 상한(40단계) 초과로 제외된 Job: POQSettleProc4 (선언 73단계)
- 단계 번들 세대: 2026-08-12 ~ 2026-08-24
- 명세서 세대: 2026-08-28
- 미해결 프로시저 참조: 0
- 측정 쌍 0인 Job: POQSettleProc20, POQSettleProc5
- `stepInterfaces`를 `null`로 넘겼다(DB 메타데이터가 필요해 로컬에서 만들 수 없다). 검사 A~E는 이 값을 읽지 않는다.
- `runRowOwnedTables`를 `null`로 넘겼다(같은 이유). 검사 A~E는 이 값을 읽지 않는다.
- `knownTableNames`가 비어 유령 테이블 검사가 소프트 스킵됐다.

**단계 번들이 명세서보다 낡았다.** 축 B의 기준값은 명세서이므로, 이 스윕이 잡은 불일치 중 일부는 이행 결함이 아니라 **세대 차이**일 수 있다 — 폐기된 명세서로 만든 지시서를 현행 명세서와 맞댄 것이기 때문이다. 번들을 재생성한 뒤 다시 재는 것이 순서다(`docs/audit-defect-catalog.md` 3절).

## 검사별 발화량

| 검사 | (A) 오늘 | (B) 캐시 17 모사 |
| :--- | ---: | ---: |
| A | 20 | 20 |
| B | 26 | 26 |
| C | 20 | 20 |
| D | 0 | 0 |
| E | 59 | 59 |
| 미분류 | 1095 | 1095 |

## Job별 발화량

| Job | 검사 | (A) 오늘 | (B) 캐시 17 모사 |
| :--- | :--- | ---: | ---: |
| POQSettleBatch1 | A | 2 | 2 |
| POQSettleBatch1 | B | 1 | 1 |
| POQSettleBatch1 | E | 3 | 3 |
| POQSettleBatch1 | 미분류 | 6 | 6 |
| POQSettlePrco20 | B | 3 | 3 |
| POQSettlePrco20 | C | 2 | 2 |
| POQSettlePrco20 | E | 6 | 6 |
| POQSettlePrco20 | 미분류 | 9 | 9 |
| POQSettleProc1 | B | 1 | 1 |
| POQSettleProc1 | E | 4 | 4 |
| POQSettleProc1 | 미분류 | 36 | 36 |
| POQSettleProc10 | A | 9 | 9 |
| POQSettleProc10 | E | 1 | 1 |
| POQSettleProc10 | 미분류 | 94 | 94 |
| POQSettleProc11 | B | 3 | 3 |
| POQSettleProc11 | C | 3 | 3 |
| POQSettleProc11 | E | 2 | 2 |
| POQSettleProc11 | 미분류 | 23 | 23 |
| POQSettleProc12 | C | 2 | 2 |
| POQSettleProc12 | E | 1 | 1 |
| POQSettleProc12 | 미분류 | 50 | 50 |
| POQSettleProc13 | A | 1 | 1 |
| POQSettleProc13 | B | 2 | 2 |
| POQSettleProc13 | E | 1 | 1 |
| POQSettleProc13 | 미분류 | 110 | 110 |
| POQSettleProc14 | B | 2 | 2 |
| POQSettleProc14 | C | 4 | 4 |
| POQSettleProc14 | E | 6 | 6 |
| POQSettleProc14 | 미분류 | 46 | 46 |
| POQSettleProc15 | A | 1 | 1 |
| POQSettleProc15 | E | 3 | 3 |
| POQSettleProc15 | 미분류 | 154 | 154 |
| POQSettleProc16 | C | 2 | 2 |
| POQSettleProc16 | E | 3 | 3 |
| POQSettleProc16 | 미분류 | 137 | 137 |
| POQSettleProc17 | B | 4 | 4 |
| POQSettleProc17 | C | 3 | 3 |
| POQSettleProc17 | E | 8 | 8 |
| POQSettleProc17 | 미분류 | 14 | 14 |
| POQSettleProc18 | C | 1 | 1 |
| POQSettleProc18 | E | 9 | 9 |
| POQSettleProc18 | 미분류 | 10 | 10 |
| POQSettleProc19 | B | 4 | 4 |
| POQSettleProc19 | C | 1 | 1 |
| POQSettleProc19 | E | 6 | 6 |
| POQSettleProc19 | 미분류 | 15 | 15 |
| POQSettleProc2 | 미분류 | 126 | 126 |
| POQSettleProc3 | A | 1 | 1 |
| POQSettleProc3 | C | 1 | 1 |
| POQSettleProc3 | 미분류 | 17 | 17 |
| POQSettleProc6 | 미분류 | 203 | 203 |
| POQSettleProc8 | A | 3 | 3 |
| POQSettleProc8 | B | 2 | 2 |
| POQSettleProc8 | C | 1 | 1 |
| POQSettleProc8 | E | 1 | 1 |
| POQSettleProc8 | 미분류 | 22 | 22 |
| POQSettleProc9 | A | 3 | 3 |
| POQSettleProc9 | B | 4 | 4 |
| POQSettleProc9 | E | 5 | 5 |
| POQSettleProc9 | 미분류 | 23 | 23 |

## 조건 (B)는 상한이다

(B)는 모델이 「오류 코드」 표를 완전히 전사한다고 가정하고 원본 DDL에서 만든 사전을 주입한 값이다. 실제 재생성에서는 전사 오류가 나고, 그 오류는 `ErrorType.ErrorCodeTableMissing` 전사 대조가 따로 잡는다. **따라서 (B)는 축이 켜졌을 때의 상한이지 재생성 후 실제 발화량의 예측이 아니다.**

## 검사 B·C 발화 목록

**판정 칸을 채웠다 (2026-08-28).** 좌표마다 단계 SQL · 명세서의 DML 범위 표와 집합 술어 표 ·
`raw/metadata.json`의 원본 DDL을 열어 대조했다. 부류 번호의 정의와 각 부류의 근거는
`docs/known-defects.md` **(5-3-8)**에 있다 — 이 표는 그 판정의 좌표별 색인이다.

**판정 칸은 부류 번호만 싣는다 — 유보는 (5-3-8)에만 있다.** 특히 부류 9(구조적 오탐)의 등가는
제약이 아니라 IDENTITY 관례에 기댄 것이고(`TSettleMst`에 PK도 고유 인덱스도 없다), 부류 12는
위임된 본문이 코퍼스에 없어 판정 불가다. 이 표만 훑지 말고 (5-3-8)을 함께 볼 것.

같은 좌표가 조건 (A)·(B) 두 행으로 나오므로 **행 92 = 좌표 46**이다. 판을 접으면 원본 문장
17가지이고 원인으로 접으면 12부류다. 합계: **진짜 결함 33 · 구조적 오탐 10 · 판정 불가 3.**

| # | 검사 | 조건 | Job | 단계 | 문장 | 항목 | 판정 |
| ---: | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| 1 | B | A | POQSettleBatch1 | S11 | UPDATE 9 | YMD, UseState | 부류 3 · 진짜 결함 |
| 2 | B | B | POQSettleBatch1 | S11 | UPDATE 9 | YMD, UseState | 부류 3 · 진짜 결함 |
| 3 | B | A | POQSettlePrco20 | S06 | UPDATE 10 | MALLID | 부류 1 · 진짜 결함 |
| 4 | B | A | POQSettlePrco20 | S06 | UPDATE 10 | PGNAME, MALLID | 부류 1 · 진짜 결함 |
| 5 | B | A | POQSettlePrco20 | S06 | UPDATE 15 | MobileCo | 부류 3 · 진짜 결함 |
| 6 | C | A | POQSettlePrco20 | S06 | UPDATE 12 | PGName | 부류 2 · 진짜 결함 |
| 7 | B | B | POQSettlePrco20 | S06 | UPDATE 10 | MALLID | 부류 1 · 진짜 결함 |
| 8 | B | B | POQSettlePrco20 | S06 | UPDATE 10 | PGNAME, MALLID | 부류 1 · 진짜 결함 |
| 9 | B | B | POQSettlePrco20 | S06 | UPDATE 15 | MobileCo | 부류 3 · 진짜 결함 |
| 10 | C | B | POQSettlePrco20 | S06 | UPDATE 12 | PGName | 부류 2 · 진짜 결함 |
| 11 | C | A | POQSettlePrco20 | S07 | UPDATE 7 | UseState | 부류 4 · 진짜 결함 |
| 12 | C | B | POQSettlePrco20 | S07 | UPDATE 7 | UseState | 부류 4 · 진짜 결함 |
| 13 | B | A | POQSettleProc1 | S11 | DELETE 4 | OUTSTATE | 부류 5 · 진짜 결함 |
| 14 | B | B | POQSettleProc1 | S11 | DELETE 4 | OUTSTATE | 부류 5 · 진짜 결함 |
| 15 | B | A | POQSettleProc11 | S06 | UPDATE 10 | MALLID | 부류 1 · 진짜 결함 |
| 16 | B | A | POQSettleProc11 | S06 | UPDATE 10 | PGNAME, MALLID | 부류 1 · 진짜 결함 |
| 17 | B | A | POQSettleProc11 | S06 | UPDATE 13 | ID | 부류 8 · 구조적 오탐 |
| 18 | C | A | POQSettleProc11 | S06 | UPDATE 13 | UseState, AYMD | 부류 8 · 구조적 오탐 |
| 19 | C | A | POQSettleProc11 | S06 | UPDATE 18 | YMD, OutState | 부류 8 · 구조적 오탐 |
| 20 | B | B | POQSettleProc11 | S06 | UPDATE 10 | MALLID | 부류 1 · 진짜 결함 |
| 21 | B | B | POQSettleProc11 | S06 | UPDATE 10 | PGNAME, MALLID | 부류 1 · 진짜 결함 |
| 22 | B | B | POQSettleProc11 | S06 | UPDATE 13 | ID | 부류 8 · 구조적 오탐 |
| 23 | C | B | POQSettleProc11 | S06 | UPDATE 13 | UseState, AYMD | 부류 8 · 구조적 오탐 |
| 24 | C | B | POQSettleProc11 | S06 | UPDATE 18 | YMD, OutState | 부류 8 · 구조적 오탐 |
| 25 | C | A | POQSettleProc11 | S07 | UPDATE 7 | UseState, CommissionCancelFlag | 부류 4 · 진짜 결함 |
| 26 | C | B | POQSettleProc11 | S07 | UPDATE 7 | UseState, CommissionCancelFlag | 부류 4 · 진짜 결함 |
| 27 | C | A | POQSettleProc12 | S07 | UPDATE 1 | ExtraSettleFlag | 부류 2 · 진짜 결함 |
| 28 | C | A | POQSettleProc12 | S07 | UPDATE 12 | PGName | 부류 2 · 진짜 결함 |
| 29 | C | B | POQSettleProc12 | S07 | UPDATE 1 | ExtraSettleFlag | 부류 2 · 진짜 결함 |
| 30 | C | B | POQSettleProc12 | S07 | UPDATE 12 | PGName | 부류 2 · 진짜 결함 |
| 31 | B | A | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID, CollectPeriodID, CollectFlag | 부류 3 · 진짜 결함 |
| 32 | B | A | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID, CollectPeriodID | 부류 3 · 진짜 결함 |
| 33 | B | B | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID, CollectPeriodID, CollectFlag | 부류 3 · 진짜 결함 |
| 34 | B | B | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID, CollectPeriodID | 부류 3 · 진짜 결함 |
| 35 | B | A | POQSettleProc14 | S07 | UPDATE 10 | MALLID | 부류 1 · 진짜 결함 |
| 36 | B | A | POQSettleProc14 | S07 | UPDATE 10 | PGNAME, MALLID | 부류 1 · 진짜 결함 |
| 37 | C | A | POQSettleProc14 | S07 | UPDATE 1 | ExtraSettleFlag | 부류 2 · 진짜 결함 |
| 38 | C | A | POQSettleProc14 | S07 | UPDATE 12 | PGName | 부류 2 · 진짜 결함 |
| 39 | C | A | POQSettleProc14 | S07 | UPDATE 18 | YMD | 부류 2 · 진짜 결함 |
| 40 | B | B | POQSettleProc14 | S07 | UPDATE 10 | MALLID | 부류 1 · 진짜 결함 |
| 41 | B | B | POQSettleProc14 | S07 | UPDATE 10 | PGNAME, MALLID | 부류 1 · 진짜 결함 |
| 42 | C | B | POQSettleProc14 | S07 | UPDATE 1 | ExtraSettleFlag | 부류 2 · 진짜 결함 |
| 43 | C | B | POQSettleProc14 | S07 | UPDATE 12 | PGName | 부류 2 · 진짜 결함 |
| 44 | C | B | POQSettleProc14 | S07 | UPDATE 18 | YMD | 부류 2 · 진짜 결함 |
| 45 | C | A | POQSettleProc14 | S10 | UPDATE 4 | UseState | 부류 10 · 구조적 오탐 |
| 46 | C | B | POQSettleProc14 | S10 | UPDATE 4 | UseState | 부류 10 · 구조적 오탐 |
| 47 | C | A | POQSettleProc16 | S07 | UPDATE 12 | PGName | 부류 2 · 진짜 결함 |
| 48 | C | B | POQSettleProc16 | S07 | UPDATE 12 | PGName | 부류 2 · 진짜 결함 |
| 49 | C | A | POQSettleProc16 | S08 | UPDATE 7 | CommissionCancelFlag | 부류 4 · 진짜 결함 |
| 50 | C | B | POQSettleProc16 | S08 | UPDATE 7 | CommissionCancelFlag | 부류 4 · 진짜 결함 |
| 51 | C | A | POQSettleProc17 | S06 | INSERT 1 | CLIENT_INCVTAX, CLIENT_COMMISSIONTYPE, PG_INCVTAX, PG_COMMISSIONTYPE | 부류 7 · 진짜 결함 |
| 52 | C | B | POQSettleProc17 | S06 | INSERT 1 | CLIENT_INCVTAX, CLIENT_COMMISSIONTYPE, PG_INCVTAX, PG_COMMISSIONTYPE | 부류 7 · 진짜 결함 |
| 53 | B | A | POQSettleProc17 | S07 | UPDATE 10 | MALLID | 부류 1 · 진짜 결함 |
| 54 | B | A | POQSettleProc17 | S07 | UPDATE 10 | PGNAME, MALLID | 부류 1 · 진짜 결함 |
| 55 | B | A | POQSettleProc17 | S07 | UPDATE 13 | ID | 부류 8 · 구조적 오탐 |
| 56 | C | A | POQSettleProc17 | S07 | UPDATE 12 | PGName | 부류 2 · 진짜 결함 |
| 57 | B | B | POQSettleProc17 | S07 | UPDATE 10 | MALLID | 부류 1 · 진짜 결함 |
| 58 | B | B | POQSettleProc17 | S07 | UPDATE 10 | PGNAME, MALLID | 부류 1 · 진짜 결함 |
| 59 | B | B | POQSettleProc17 | S07 | UPDATE 13 | ID | 부류 8 · 구조적 오탐 |
| 60 | C | B | POQSettleProc17 | S07 | UPDATE 12 | PGName | 부류 2 · 진짜 결함 |
| 61 | B | A | POQSettleProc17 | S08 | UPDATE 7 | PLTID | 부류 9 · 구조적 오탐 |
| 62 | C | A | POQSettleProc17 | S08 | UPDATE 7 | UseState | 부류 4 · 진짜 결함 |
| 63 | B | B | POQSettleProc17 | S08 | UPDATE 7 | PLTID | 부류 9 · 구조적 오탐 |
| 64 | C | B | POQSettleProc17 | S08 | UPDATE 7 | UseState | 부류 4 · 진짜 결함 |
| 65 | C | A | POQSettleProc18 | S10 | UPDATE 4 | UseState | 부류 10 · 구조적 오탐 |
| 66 | C | B | POQSettleProc18 | S10 | UPDATE 4 | UseState | 부류 10 · 구조적 오탐 |
| 67 | B | A | POQSettleProc19 | S10 | UPDATE 10 | MALLID | 부류 1 · 진짜 결함 |
| 68 | B | A | POQSettleProc19 | S10 | UPDATE 10 | PGNAME, MALLID | 부류 1 · 진짜 결함 |
| 69 | C | A | POQSettleProc19 | S10 | UPDATE 12 | PGName | 부류 2 · 진짜 결함 |
| 70 | B | B | POQSettleProc19 | S10 | UPDATE 10 | MALLID | 부류 1 · 진짜 결함 |
| 71 | B | B | POQSettleProc19 | S10 | UPDATE 10 | PGNAME, MALLID | 부류 1 · 진짜 결함 |
| 72 | C | B | POQSettleProc19 | S10 | UPDATE 12 | PGName | 부류 2 · 진짜 결함 |
| 73 | B | A | POQSettleProc19 | S11 | UPDATE 7 | PLTID | 부류 9 · 구조적 오탐 |
| 74 | B | A | POQSettleProc19 | S11 | UPDATE 10 | CYMD, AYMD, RefundFlag | 부류 6 · 진짜 결함 |
| 75 | B | B | POQSettleProc19 | S11 | UPDATE 7 | PLTID | 부류 9 · 구조적 오탐 |
| 76 | B | B | POQSettleProc19 | S11 | UPDATE 10 | CYMD, AYMD, RefundFlag | 부류 6 · 진짜 결함 |
| 77 | C | A | POQSettleProc3 | S04 | UPDATE 1 | ExtraSettleFlag | 부류 2 · 진짜 결함 |
| 78 | C | B | POQSettleProc3 | S04 | UPDATE 1 | ExtraSettleFlag | 부류 2 · 진짜 결함 |
| 79 | B | A | POQSettleProc8 | S05 | INSERT 1 | PGName | 부류 11 · 구조적 오탐 |
| 80 | C | A | POQSettleProc8 | S05 | INSERT 1 | ProcessingYMD | 부류 11 · 구조적 오탐 |
| 81 | B | B | POQSettleProc8 | S05 | INSERT 1 | PGName | 부류 11 · 구조적 오탐 |
| 82 | C | B | POQSettleProc8 | S05 | INSERT 1 | ProcessingYMD | 부류 11 · 구조적 오탐 |
| 83 | B | A | POQSettleProc8 | S12 | DELETE 4 | OUTSTATE | 부류 5 · 진짜 결함 |
| 84 | B | B | POQSettleProc8 | S12 | DELETE 4 | OUTSTATE | 부류 5 · 진짜 결함 |
| 85 | B | A | POQSettleProc9 | S13 | DELETE 4 | OUTSTATE | 부류 5 · 진짜 결함 |
| 86 | B | A | POQSettleProc9 | S13 | INSERT 2 | USESTATE | 부류 12 · 판정 불가 |
| 87 | B | A | POQSettleProc9 | S13 | INSERT 3 | INSTATE | 부류 12 · 판정 불가 |
| 88 | B | A | POQSettleProc9 | S13 | INSERT 4 | OUTSTATE | 부류 12 · 판정 불가 |
| 89 | B | B | POQSettleProc9 | S13 | DELETE 4 | OUTSTATE | 부류 5 · 진짜 결함 |
| 90 | B | B | POQSettleProc9 | S13 | INSERT 2 | USESTATE | 부류 12 · 판정 불가 |
| 91 | B | B | POQSettleProc9 | S13 | INSERT 3 | INSTATE | 부류 12 · 판정 불가 |
| 92 | B | B | POQSettleProc9 | S13 | INSERT 4 | OUTSTATE | 부류 12 · 판정 불가 |

## 캐시 17 선결 지표

| 지표 | 값 |
| :--- | ---: |
| 다중 레거시 SP 단계 수 | 2 |
| SP 표에는 있는데 단계에 없는 코드가 있는 단계 수 | 68 |
| 단계에는 있는데 SP 표에 없는 코드가 있는 단계 수 | 63 |
| 펜스 파싱 실패로 코드 집합 대조에서 제외한 단계 수 | 46 |
| 코드 앵커가 둘 이상의 문장에 붙은 단계 수 | 60 |

펜스 파싱 실패로 46개 단계를 코드 집합 대조에서 제외했다 - 위 두 코드 집합 지표(SP 표에는 있는데 단계에 없는 코드, 단계에는 있는데 SP 표에 없는 코드)의 분모가 그만큼 줄었다는 뜻이다. 이 값이 크면 두 지표가 코퍼스 전체를 대표하지 않는다.

## 침묵 분모

발화가 늘어난 자리만 보면 가려져 있던 침묵이 함께 켜지는 것을 못 본다. 승격 전에는 앵커가 안 풀려 면제가 도달 불가능하므로, 아래 값의 **증가분이 곧 이번에 새로 생긴 침묵**이다. 좌표 차분은 이 부류를 못 본다 - 가드가 조건 (A)에서도 (B)에서도 같은 좌표를 침묵시키면 차분이 정의상 0이기 때문이다.

| 분모 | 값 |
| :--- | ---: |
| 앵커가 서수로 해결된 문장 수 | 856 |
| 앵커는 있으나 서수로 환산되지 않은 문장 수 | 233 |
| (Kind, Ordinal) 모호성 가드가 버린 문장 수 | 84 |
| 계보 원천을 가진 문장 수 | 142 |
| 스테이징만 읽어 검사 C 가 면제한 문장 수 | 105 |
| 자기 대상을 읽는 문장 수 | 786 |
| 자기 대상을 읽어 스테이징 면제가 취소된 문장 수 | 35 |
| 하위 범위 술어 컬럼을 가진 문장 수 | 252 |
| 하위 범위 술어 컬럼의 총수 | 3408 |
| 스테이징 원천의 총수 | 141 |

**「자기 대상을 읽어 스테이징 면제가 취소된 문장 수」가 0 이면 그 방어가 도달하지 못한 것이다.** 수정이 살아 있다는 증거가 아니라 재지 않았다는 증거로 읽는다 (2026-08-27 staging-lineage 최종 리뷰 Critical 1).

이 표는 **사유가 아니라 분모**다. 어느 좌표가 어느 가드에 침묵당했는지는 세지 않는다 - 그러려면 검증기가 판정 사유를 내보내야 한다.

