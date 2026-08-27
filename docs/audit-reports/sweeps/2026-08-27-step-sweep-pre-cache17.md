# 단계 검사 스윕

## 이 보고서의 자리

**캐시 16 → 17 승격 전 기준선이다.** 로드맵 4의 사후 측정(`2026-08-27-step-sweep-post-cache17.md`)이
이 문서의 **조건 (B) 좌표 집합 46건을 `pre-(B)` 로** 삼아 3자 차분을 낸다.

- 착수 커밋: `faf3c28` / 이 측정의 커밋: `9b20244`
- 설계: `docs/superpowers/specs/2026-08-27-cache17-promotion-design.md`
- 계획: `docs/superpowers/plans/2026-08-27-cache17-promotion.md`

### 물려받은 수치를 옮겨 적지 않고 직접 떴다

`2026-08-27-step-sweep-c.md`(커밋 `be1f9b7`)는 조건 (B) **46**건이었다. 이 기준선도 **46**건으로
같다(검사 B 26 · C 20). 즉 `be1f9b7` 이후 이 회차가 넣은 변경(가시성 개방·`BuildSpecTargets`
추출·침묵 분모 계측·보고서 절)은 **발화를 하나도 움직이지 않았다.** 계측 추가가 순수 가산임이
이것으로도 확인된다.

### 조건 (A)의 검사 B·C가 0인 것이 이 회차의 출발점이다

「검사별 발화량」 표의 (A) 열에서 검사 B·C가 둘 다 **0**이다. 명세서에 「오류 코드」 표가 없어
코드 앵커가 서수로 환산되지 않기 때문이고, **캐시 17이 그 표를 실으면 이 46건이 켜진다.**

### 침묵 분모를 함께 싣는다 — 이 회차에 새로 생긴 절

발화가 늘어난 자리만 보면 가려져 있던 침묵이 함께 켜지는 것을 못 본다. 아래 「침묵 분모」 절의
열 값이 사후 측정과 맞대어질 기준선이다. 계기 자체는 변이로 검증했다
(`2026-08-27-silence-denominator-mutations.md` — 변이 셋 전부 죽었고 안 죽은 변이는 없다).

**특히 「자기 대상을 읽어 스테이징 면제가 취소된 문장 수」 = 35 를 기억할 것.** 2026-08-27
`staging-lineage` 최종 리뷰가 Critical 1 로 보고한 35 좌표와 같은 수이고, 그 방어가 살아 있는지를
재는 자리다.

### 잰 것과 안 잰 것

- **잰 것**: 조건 (A)·(B) 발화, 침묵 분모 열, 캐시 17 선결 지표, 위 46 좌표
- **안 잰 것**: 재생성의 시간·비용 · 재생성 후 실제 발화량 · 모델의 「오류 코드」 표 전사 정확도 ·
  침묵의 좌표 귀속(설계상 범위 밖)

## 실행 조건

- 커밋: `9b20244`
- 작업 트리: 깨끗
- 캐시 인덱스 `FormatVersion` 집합: {16} — 항목 31개
- 측정 쌍: 326 (Job 18개)
- 단계 파일 누락: 51
- 목차 파싱 실패 Job: POQSettleProc7
- 목차 단계 수 상한(40단계) 초과로 제외된 Job: POQSettleProc4 (선언 73단계)
- 단계 번들 세대: 2026-08-12 ~ 2026-08-24
- 명세서 세대: 2026-08-25
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
| B | 0 | 26 |
| C | 0 | 20 |
| D | 18 | 18 |
| E | 59 | 59 |
| 미분류 | 1163 | 1163 |

## Job별 발화량

| Job | 검사 | (A) 오늘 | (B) 캐시 17 모사 |
| :--- | :--- | ---: | ---: |
| POQSettleBatch1 | A | 2 | 2 |
| POQSettleBatch1 | B | 0 | 1 |
| POQSettleBatch1 | D | 9 | 9 |
| POQSettleBatch1 | E | 3 | 3 |
| POQSettleBatch1 | 미분류 | 10 | 10 |
| POQSettlePrco20 | B | 0 | 3 |
| POQSettlePrco20 | C | 0 | 2 |
| POQSettlePrco20 | E | 6 | 6 |
| POQSettlePrco20 | 미분류 | 13 | 13 |
| POQSettleProc1 | B | 0 | 1 |
| POQSettleProc1 | E | 4 | 4 |
| POQSettleProc1 | 미분류 | 38 | 38 |
| POQSettleProc10 | A | 9 | 9 |
| POQSettleProc10 | E | 1 | 1 |
| POQSettleProc10 | 미분류 | 99 | 99 |
| POQSettleProc11 | B | 0 | 3 |
| POQSettleProc11 | C | 0 | 3 |
| POQSettleProc11 | E | 2 | 2 |
| POQSettleProc11 | 미분류 | 27 | 27 |
| POQSettleProc12 | C | 0 | 2 |
| POQSettleProc12 | E | 1 | 1 |
| POQSettleProc12 | 미분류 | 52 | 52 |
| POQSettleProc13 | A | 1 | 1 |
| POQSettleProc13 | B | 0 | 2 |
| POQSettleProc13 | E | 1 | 1 |
| POQSettleProc13 | 미분류 | 115 | 115 |
| POQSettleProc14 | B | 0 | 2 |
| POQSettleProc14 | C | 0 | 4 |
| POQSettleProc14 | D | 9 | 9 |
| POQSettleProc14 | E | 6 | 6 |
| POQSettleProc14 | 미분류 | 51 | 51 |
| POQSettleProc15 | A | 1 | 1 |
| POQSettleProc15 | E | 3 | 3 |
| POQSettleProc15 | 미분류 | 158 | 158 |
| POQSettleProc16 | C | 0 | 2 |
| POQSettleProc16 | E | 3 | 3 |
| POQSettleProc16 | 미분류 | 141 | 141 |
| POQSettleProc17 | B | 0 | 4 |
| POQSettleProc17 | C | 0 | 3 |
| POQSettleProc17 | E | 8 | 8 |
| POQSettleProc17 | 미분류 | 18 | 18 |
| POQSettleProc18 | C | 0 | 1 |
| POQSettleProc18 | E | 9 | 9 |
| POQSettleProc18 | 미분류 | 14 | 14 |
| POQSettleProc19 | B | 0 | 4 |
| POQSettleProc19 | C | 0 | 1 |
| POQSettleProc19 | E | 6 | 6 |
| POQSettleProc19 | 미분류 | 20 | 20 |
| POQSettleProc2 | 미분류 | 130 | 130 |
| POQSettleProc3 | A | 1 | 1 |
| POQSettleProc3 | C | 0 | 1 |
| POQSettleProc3 | 미분류 | 21 | 21 |
| POQSettleProc6 | 미분류 | 203 | 203 |
| POQSettleProc8 | A | 3 | 3 |
| POQSettleProc8 | B | 0 | 2 |
| POQSettleProc8 | C | 0 | 1 |
| POQSettleProc8 | E | 1 | 1 |
| POQSettleProc8 | 미분류 | 26 | 26 |
| POQSettleProc9 | A | 3 | 3 |
| POQSettleProc9 | B | 0 | 4 |
| POQSettleProc9 | E | 5 | 5 |
| POQSettleProc9 | 미분류 | 27 | 27 |

## 조건 (B)는 상한이다

(B)는 모델이 「오류 코드」 표를 완전히 전사한다고 가정하고 원본 DDL에서 만든 사전을 주입한 값이다. 실제 재생성에서는 전사 오류가 나고, 그 오류는 `ErrorType.ErrorCodeTableMissing` 전사 대조가 따로 잡는다. **따라서 (B)는 축이 켜졌을 때의 상한이지 재생성 후 실제 발화량의 예측이 아니다.**

## 검사 B·C 발화 목록

판정 칸은 비어 있다 — 원본 DDL과 이행 SQL을 읽어 사람이 채운다.

| # | 검사 | 조건 | Job | 단계 | 문장 | 항목 | 판정 |
| ---: | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| 1 | B | B | POQSettleBatch1 | S11 | UPDATE 9 | YMD, UseState |  |
| 2 | B | B | POQSettlePrco20 | S06 | UPDATE 10 | MALLID |  |
| 3 | B | B | POQSettlePrco20 | S06 | UPDATE 10 | PGNAME, MALLID |  |
| 4 | B | B | POQSettlePrco20 | S06 | UPDATE 15 | MobileCo |  |
| 5 | C | B | POQSettlePrco20 | S06 | UPDATE 12 | PGName |  |
| 6 | C | B | POQSettlePrco20 | S07 | UPDATE 7 | UseState |  |
| 7 | B | B | POQSettleProc1 | S11 | DELETE 4 | OUTSTATE |  |
| 8 | B | B | POQSettleProc11 | S06 | UPDATE 10 | MALLID |  |
| 9 | B | B | POQSettleProc11 | S06 | UPDATE 10 | PGNAME, MALLID |  |
| 10 | B | B | POQSettleProc11 | S06 | UPDATE 13 | ID |  |
| 11 | C | B | POQSettleProc11 | S06 | UPDATE 13 | UseState, AYMD |  |
| 12 | C | B | POQSettleProc11 | S06 | UPDATE 18 | YMD, OutState |  |
| 13 | C | B | POQSettleProc11 | S07 | UPDATE 7 | UseState, CommissionCancelFlag |  |
| 14 | C | B | POQSettleProc12 | S07 | UPDATE 1 | ExtraSettleFlag |  |
| 15 | C | B | POQSettleProc12 | S07 | UPDATE 12 | PGName |  |
| 16 | B | B | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID, CollectPeriodID, CollectFlag |  |
| 17 | B | B | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID, CollectPeriodID |  |
| 18 | B | B | POQSettleProc14 | S07 | UPDATE 10 | MALLID |  |
| 19 | B | B | POQSettleProc14 | S07 | UPDATE 10 | PGNAME, MALLID |  |
| 20 | C | B | POQSettleProc14 | S07 | UPDATE 1 | ExtraSettleFlag |  |
| 21 | C | B | POQSettleProc14 | S07 | UPDATE 12 | PGName |  |
| 22 | C | B | POQSettleProc14 | S07 | UPDATE 18 | YMD |  |
| 23 | C | B | POQSettleProc14 | S10 | UPDATE 4 | UseState |  |
| 24 | C | B | POQSettleProc16 | S07 | UPDATE 12 | PGName |  |
| 25 | C | B | POQSettleProc16 | S08 | UPDATE 7 | CommissionCancelFlag |  |
| 26 | C | B | POQSettleProc17 | S06 | INSERT 1 | CLIENT_INCVTAX, CLIENT_COMMISSIONTYPE, PG_INCVTAX, PG_COMMISSIONTYPE |  |
| 27 | B | B | POQSettleProc17 | S07 | UPDATE 10 | MALLID |  |
| 28 | B | B | POQSettleProc17 | S07 | UPDATE 10 | PGNAME, MALLID |  |
| 29 | B | B | POQSettleProc17 | S07 | UPDATE 13 | ID |  |
| 30 | C | B | POQSettleProc17 | S07 | UPDATE 12 | PGName |  |
| 31 | B | B | POQSettleProc17 | S08 | UPDATE 7 | PLTID |  |
| 32 | C | B | POQSettleProc17 | S08 | UPDATE 7 | UseState |  |
| 33 | C | B | POQSettleProc18 | S10 | UPDATE 4 | UseState |  |
| 34 | B | B | POQSettleProc19 | S10 | UPDATE 10 | MALLID |  |
| 35 | B | B | POQSettleProc19 | S10 | UPDATE 10 | PGNAME, MALLID |  |
| 36 | C | B | POQSettleProc19 | S10 | UPDATE 12 | PGName |  |
| 37 | B | B | POQSettleProc19 | S11 | UPDATE 7 | PLTID |  |
| 38 | B | B | POQSettleProc19 | S11 | UPDATE 10 | CYMD, AYMD, RefundFlag |  |
| 39 | C | B | POQSettleProc3 | S04 | UPDATE 1 | ExtraSettleFlag |  |
| 40 | B | B | POQSettleProc8 | S05 | INSERT 1 | PGName |  |
| 41 | C | B | POQSettleProc8 | S05 | INSERT 1 | ProcessingYMD |  |
| 42 | B | B | POQSettleProc8 | S12 | DELETE 4 | OUTSTATE |  |
| 43 | B | B | POQSettleProc9 | S13 | DELETE 4 | OUTSTATE |  |
| 44 | B | B | POQSettleProc9 | S13 | INSERT 2 | USESTATE |  |
| 45 | B | B | POQSettleProc9 | S13 | INSERT 3 | INSTATE |  |
| 46 | B | B | POQSettleProc9 | S13 | INSERT 4 | OUTSTATE |  |

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

