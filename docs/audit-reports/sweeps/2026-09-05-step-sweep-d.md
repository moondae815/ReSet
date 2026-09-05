# 단계 검사 스윕

## 실행 조건

- 커밋: `7f0ef939`
- 작업 트리: 깨끗
- 캐시 인덱스 `FormatVersion` 집합: {17} — 항목 31개
- 측정 쌍: 328 (Job 18개)
- 단계 파일 누락: 51
- 목차 파싱 실패 Job: POQSettleProc7
- 목차 단계 수 상한(40단계) 초과로 제외된 Job: POQSettleProc4 (선언 73단계)
- 단계 번들 세대: 2026-08-12 ~ 2026-09-04
- 명세서 세대: 2026-08-28 ~ 2026-09-05
- 미해결 프로시저 참조: 0
- 측정 쌍 0인 Job: POQSettleProc20, POQSettleProc5
- `stepInterfaces`를 `null`로 넘겼다(DB 메타데이터가 필요해 로컬에서 만들 수 없다). 검사 A~E는 이 값을 읽지 않는다.
- `runRowOwnedTables`를 `null`로 넘겼다(같은 이유). 검사 A~E는 이 값을 읽지 않는다.
- `knownTableNames`가 비어 유령 테이블 검사가 소프트 스킵됐다.

**단계 번들이 명세서보다 낡았다.** 축 B의 기준값은 명세서이므로, 이 스윕이 잡은 불일치 중 일부는 이행 결함이 아니라 **세대 차이**일 수 있다 — 폐기된 명세서로 만든 지시서를 현행 명세서와 맞댄 것이기 때문이다. 번들을 재생성한 뒤 다시 재는 것이 순서다(`docs/audit-defect-catalog.md` 3절).

## 검사별 발화량

| 검사 | (A) 오늘 | (B) 캐시 17 모사 |
| :--- | ---: | ---: |
| A | 18 | 18 |
| B | 27 | 27 |
| C | 20 | 20 |
| D | 9 | 9 |
| E | 56 | 56 |
| 미분류 | 1125 | 1125 |

## Job별 발화량

| Job | 검사 | (A) 오늘 | (B) 캐시 17 모사 |
| :--- | :--- | ---: | ---: |
| POQSettleBatch1 | B | 2 | 2 |
| POQSettleBatch1 | 미분류 | 2 | 2 |
| POQSettlePrco20 | B | 3 | 3 |
| POQSettlePrco20 | C | 2 | 2 |
| POQSettlePrco20 | E | 6 | 6 |
| POQSettlePrco20 | 미분류 | 9 | 9 |
| POQSettleProc1 | B | 1 | 1 |
| POQSettleProc1 | E | 4 | 4 |
| POQSettleProc1 | 미분류 | 36 | 36 |
| POQSettleProc10 | A | 9 | 9 |
| POQSettleProc10 | E | 1 | 1 |
| POQSettleProc10 | 미분류 | 99 | 99 |
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
| POQSettleProc13 | 미분류 | 115 | 115 |
| POQSettleProc14 | B | 2 | 2 |
| POQSettleProc14 | C | 4 | 4 |
| POQSettleProc14 | D | 9 | 9 |
| POQSettleProc14 | E | 6 | 6 |
| POQSettleProc14 | 미분류 | 46 | 46 |
| POQSettleProc15 | A | 1 | 1 |
| POQSettleProc15 | E | 3 | 3 |
| POQSettleProc15 | 미분류 | 159 | 159 |
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
| POQSettleProc8 | 미분류 | 28 | 28 |
| POQSettleProc9 | A | 3 | 3 |
| POQSettleProc9 | B | 4 | 4 |
| POQSettleProc9 | E | 5 | 5 |
| POQSettleProc9 | 미분류 | 36 | 36 |

## 조건 (B)는 상한이다

(B)는 모델이 「오류 코드」 표를 완전히 전사한다고 가정하고 원본 DDL에서 만든 사전을 주입한 값이다. 실제 재생성에서는 전사 오류가 나고, 그 오류는 `ErrorType.ErrorCodeTableMissing` 전사 대조가 따로 잡는다. **따라서 (B)는 축이 켜졌을 때의 상한이지 재생성 후 실제 발화량의 예측이 아니다.**

## 검사 B·C 발화 목록

판정 칸은 비어 있다 — 원본 DDL과 이행 SQL을 읽어 사람이 채운다.

| # | 검사 | 조건 | Job | 단계 | 문장 | 항목 | 판정 |
| ---: | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| 1 | B | A | POQSettleBatch1 | S05 | INSERT 1 | OrgYMD |  |
| 2 | B | B | POQSettleBatch1 | S05 | INSERT 1 | OrgYMD |  |
| 3 | B | A | POQSettleBatch1 | S06 | INSERT 1 | MALLID |  |
| 4 | B | B | POQSettleBatch1 | S06 | INSERT 1 | MALLID |  |
| 5 | B | A | POQSettlePrco20 | S06 | UPDATE 10 | MALLID |  |
| 6 | B | A | POQSettlePrco20 | S06 | UPDATE 10 | PGNAME, MALLID |  |
| 7 | B | A | POQSettlePrco20 | S06 | UPDATE 15 | MobileCo |  |
| 8 | C | A | POQSettlePrco20 | S06 | UPDATE 12 | PGName |  |
| 9 | B | B | POQSettlePrco20 | S06 | UPDATE 10 | MALLID |  |
| 10 | B | B | POQSettlePrco20 | S06 | UPDATE 10 | PGNAME, MALLID |  |
| 11 | B | B | POQSettlePrco20 | S06 | UPDATE 15 | MobileCo |  |
| 12 | C | B | POQSettlePrco20 | S06 | UPDATE 12 | PGName |  |
| 13 | C | A | POQSettlePrco20 | S07 | UPDATE 7 | UseState |  |
| 14 | C | B | POQSettlePrco20 | S07 | UPDATE 7 | UseState |  |
| 15 | B | A | POQSettleProc1 | S11 | DELETE 4 | OUTSTATE |  |
| 16 | B | B | POQSettleProc1 | S11 | DELETE 4 | OUTSTATE |  |
| 17 | B | A | POQSettleProc11 | S06 | UPDATE 10 | MALLID |  |
| 18 | B | A | POQSettleProc11 | S06 | UPDATE 10 | PGNAME, MALLID |  |
| 19 | B | A | POQSettleProc11 | S06 | UPDATE 13 | ID |  |
| 20 | C | A | POQSettleProc11 | S06 | UPDATE 13 | UseState, AYMD |  |
| 21 | C | A | POQSettleProc11 | S06 | UPDATE 18 | YMD, OutState |  |
| 22 | B | B | POQSettleProc11 | S06 | UPDATE 10 | MALLID |  |
| 23 | B | B | POQSettleProc11 | S06 | UPDATE 10 | PGNAME, MALLID |  |
| 24 | B | B | POQSettleProc11 | S06 | UPDATE 13 | ID |  |
| 25 | C | B | POQSettleProc11 | S06 | UPDATE 13 | UseState, AYMD |  |
| 26 | C | B | POQSettleProc11 | S06 | UPDATE 18 | YMD, OutState |  |
| 27 | C | A | POQSettleProc11 | S07 | UPDATE 7 | UseState, CommissionCancelFlag |  |
| 28 | C | B | POQSettleProc11 | S07 | UPDATE 7 | UseState, CommissionCancelFlag |  |
| 29 | C | A | POQSettleProc12 | S07 | UPDATE 1 | ExtraSettleFlag |  |
| 30 | C | A | POQSettleProc12 | S07 | UPDATE 12 | PGName |  |
| 31 | C | B | POQSettleProc12 | S07 | UPDATE 1 | ExtraSettleFlag |  |
| 32 | C | B | POQSettleProc12 | S07 | UPDATE 12 | PGName |  |
| 33 | B | A | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID, CollectPeriodID, CollectFlag |  |
| 34 | B | A | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID, CollectPeriodID |  |
| 35 | B | B | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID, CollectPeriodID, CollectFlag |  |
| 36 | B | B | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID, CollectPeriodID |  |
| 37 | B | A | POQSettleProc14 | S07 | UPDATE 10 | MALLID |  |
| 38 | B | A | POQSettleProc14 | S07 | UPDATE 10 | PGNAME, MALLID |  |
| 39 | C | A | POQSettleProc14 | S07 | UPDATE 1 | ExtraSettleFlag |  |
| 40 | C | A | POQSettleProc14 | S07 | UPDATE 12 | PGName |  |
| 41 | C | A | POQSettleProc14 | S07 | UPDATE 18 | YMD |  |
| 42 | B | B | POQSettleProc14 | S07 | UPDATE 10 | MALLID |  |
| 43 | B | B | POQSettleProc14 | S07 | UPDATE 10 | PGNAME, MALLID |  |
| 44 | C | B | POQSettleProc14 | S07 | UPDATE 1 | ExtraSettleFlag |  |
| 45 | C | B | POQSettleProc14 | S07 | UPDATE 12 | PGName |  |
| 46 | C | B | POQSettleProc14 | S07 | UPDATE 18 | YMD |  |
| 47 | C | A | POQSettleProc14 | S10 | UPDATE 4 | UseState |  |
| 48 | C | B | POQSettleProc14 | S10 | UPDATE 4 | UseState |  |
| 49 | C | A | POQSettleProc16 | S07 | UPDATE 12 | PGName |  |
| 50 | C | B | POQSettleProc16 | S07 | UPDATE 12 | PGName |  |
| 51 | C | A | POQSettleProc16 | S08 | UPDATE 7 | CommissionCancelFlag |  |
| 52 | C | B | POQSettleProc16 | S08 | UPDATE 7 | CommissionCancelFlag |  |
| 53 | C | A | POQSettleProc17 | S06 | INSERT 1 | CLIENT_INCVTAX, CLIENT_COMMISSIONTYPE, PG_INCVTAX, PG_COMMISSIONTYPE |  |
| 54 | C | B | POQSettleProc17 | S06 | INSERT 1 | CLIENT_INCVTAX, CLIENT_COMMISSIONTYPE, PG_INCVTAX, PG_COMMISSIONTYPE |  |
| 55 | B | A | POQSettleProc17 | S07 | UPDATE 10 | MALLID |  |
| 56 | B | A | POQSettleProc17 | S07 | UPDATE 10 | PGNAME, MALLID |  |
| 57 | B | A | POQSettleProc17 | S07 | UPDATE 13 | ID |  |
| 58 | C | A | POQSettleProc17 | S07 | UPDATE 12 | PGName |  |
| 59 | B | B | POQSettleProc17 | S07 | UPDATE 10 | MALLID |  |
| 60 | B | B | POQSettleProc17 | S07 | UPDATE 10 | PGNAME, MALLID |  |
| 61 | B | B | POQSettleProc17 | S07 | UPDATE 13 | ID |  |
| 62 | C | B | POQSettleProc17 | S07 | UPDATE 12 | PGName |  |
| 63 | B | A | POQSettleProc17 | S08 | UPDATE 7 | PLTID |  |
| 64 | C | A | POQSettleProc17 | S08 | UPDATE 7 | UseState |  |
| 65 | B | B | POQSettleProc17 | S08 | UPDATE 7 | PLTID |  |
| 66 | C | B | POQSettleProc17 | S08 | UPDATE 7 | UseState |  |
| 67 | C | A | POQSettleProc18 | S10 | UPDATE 4 | UseState |  |
| 68 | C | B | POQSettleProc18 | S10 | UPDATE 4 | UseState |  |
| 69 | B | A | POQSettleProc19 | S10 | UPDATE 10 | MALLID |  |
| 70 | B | A | POQSettleProc19 | S10 | UPDATE 10 | PGNAME, MALLID |  |
| 71 | C | A | POQSettleProc19 | S10 | UPDATE 12 | PGName |  |
| 72 | B | B | POQSettleProc19 | S10 | UPDATE 10 | MALLID |  |
| 73 | B | B | POQSettleProc19 | S10 | UPDATE 10 | PGNAME, MALLID |  |
| 74 | C | B | POQSettleProc19 | S10 | UPDATE 12 | PGName |  |
| 75 | B | A | POQSettleProc19 | S11 | UPDATE 7 | PLTID |  |
| 76 | B | A | POQSettleProc19 | S11 | UPDATE 10 | CYMD, AYMD, RefundFlag |  |
| 77 | B | B | POQSettleProc19 | S11 | UPDATE 7 | PLTID |  |
| 78 | B | B | POQSettleProc19 | S11 | UPDATE 10 | CYMD, AYMD, RefundFlag |  |
| 79 | C | A | POQSettleProc3 | S04 | UPDATE 1 | ExtraSettleFlag |  |
| 80 | C | B | POQSettleProc3 | S04 | UPDATE 1 | ExtraSettleFlag |  |
| 81 | B | A | POQSettleProc8 | S05 | INSERT 1 | PGName |  |
| 82 | C | A | POQSettleProc8 | S05 | INSERT 1 | ProcessingYMD |  |
| 83 | B | B | POQSettleProc8 | S05 | INSERT 1 | PGName |  |
| 84 | C | B | POQSettleProc8 | S05 | INSERT 1 | ProcessingYMD |  |
| 85 | B | A | POQSettleProc8 | S12 | DELETE 4 | OUTSTATE |  |
| 86 | B | B | POQSettleProc8 | S12 | DELETE 4 | OUTSTATE |  |
| 87 | B | A | POQSettleProc9 | S13 | DELETE 4 | OUTSTATE |  |
| 88 | B | A | POQSettleProc9 | S13 | INSERT 2 | USESTATE |  |
| 89 | B | A | POQSettleProc9 | S13 | INSERT 3 | INSTATE |  |
| 90 | B | A | POQSettleProc9 | S13 | INSERT 4 | OUTSTATE |  |
| 91 | B | B | POQSettleProc9 | S13 | DELETE 4 | OUTSTATE |  |
| 92 | B | B | POQSettleProc9 | S13 | INSERT 2 | USESTATE |  |
| 93 | B | B | POQSettleProc9 | S13 | INSERT 3 | INSTATE |  |
| 94 | B | B | POQSettleProc9 | S13 | INSERT 4 | OUTSTATE |  |

## 캐시 17 선결 지표

| 지표 | 값 |
| :--- | ---: |
| 다중 레거시 SP 단계 수 | 2 |
| SP 표에는 있는데 단계에 없는 코드가 있는 단계 수 | 72 |
| 단계에는 있는데 SP 표에 없는 코드가 있는 단계 수 | 61 |
| 펜스 파싱 실패로 코드 집합 대조에서 제외한 단계 수 | 44 |
| 코드 앵커가 둘 이상의 문장에 붙은 단계 수 | 57 |

펜스 파싱 실패로 44개 단계를 코드 집합 대조에서 제외했다 - 위 두 코드 집합 지표(SP 표에는 있는데 단계에 없는 코드, 단계에는 있는데 SP 표에 없는 코드)의 분모가 그만큼 줄었다는 뜻이다. 이 값이 크면 두 지표가 코퍼스 전체를 대표하지 않는다.

## 침묵 분모

발화가 늘어난 자리만 보면 가려져 있던 침묵이 함께 켜지는 것을 못 본다. 승격 전에는 앵커가 안 풀려 면제가 도달 불가능하므로, 아래 값의 **증가분이 곧 이번에 새로 생긴 침묵**이다. 좌표 차분은 이 부류를 못 본다 - 가드가 조건 (A)에서도 (B)에서도 같은 좌표를 침묵시키면 차분이 정의상 0이기 때문이다.

| 분모 | 값 |
| :--- | ---: |
| 앵커가 서수로 해결된 문장 수 | 906 |
| 앵커는 있으나 서수로 환산되지 않은 문장 수 | 229 |
| (Kind, Ordinal) 모호성 가드가 버린 문장 수 | 80 |
| 계보 원천을 가진 문장 수 | 142 |
| 스테이징만 읽어 검사 C 가 면제한 문장 수 | 105 |
| 자기 대상을 읽는 문장 수 | 807 |
| 자기 대상을 읽어 스테이징 면제가 취소된 문장 수 | 35 |
| 하위 범위 술어 컬럼을 가진 문장 수 | 255 |
| 하위 범위 술어 컬럼의 총수 | 3452 |
| 스테이징 원천의 총수 | 141 |

**「자기 대상을 읽어 스테이징 면제가 취소된 문장 수」가 0 이면 그 방어가 도달하지 못한 것이다.** 수정이 살아 있다는 증거가 아니라 재지 않았다는 증거로 읽는다 (2026-08-27 staging-lineage 최종 리뷰 Critical 1).

이 표는 **사유가 아니라 분모**다. 어느 좌표가 어느 가드에 침묵당했는지는 세지 않는다 - 그러려면 검증기가 판정 사유를 내보내야 한다.

## 재료 분모

**이 절의 분모는 프로시저다.** 위 표들의 (Job, 단계) 쌍과는 단위가 다르므로 그 쌍 수로 나누지 마라.

**이 수는 소실을 세지 원인을 귀속하지 않는다.** DDL 사실 수는 있는데 명세서 행 수가 0이어도, 「모델이 표를 안 썼다」와 「리더가 못 읽는다」가 같은 수로 보인다.

SpecMaterialCensus.Count는 jobs가 null일 때만 조기 반환한다. jobs가 비어 있거나 프로시저 해석이 전부 실패해도 조기 반환하지 않고 접은 프로시저 수 0인 행을 낸다 - 아래 분모 줄과 「조사 실패」 인쇄가 그 경우의 침묵을 죽인다.

SetTargets는 추출되지만 MechanicalValidator의 어느 검사도 안 쓴다 - 소비자가 공집합이다. 그래서 이 재료의 소실은 급하지 않다.

StepTableSets는 SpecTargetTableExtractor가 명세서 마크다운을 전혀 읽지 않고 원본 DDL 정적 분석 결과를 그대로 프로시저별로 접은 것이다 - 「명세서 쪽 행 수」라는 개념 자체가 없다.

- 접은 프로시저 14개 · DDL 파싱 실패 0개

| 재료 | DDL 사실 수 | 명세서 행 수 | 소실 프로시저 |
| :--- | ---: | ---: | :--- |
| DmlRows | 안 쟀음 | 102 | 잴 수 없음 |
| ErrorCodeToOrdinal | 안 쟀음 | 78 | 잴 수 없음 |
| SetTargets | 안 쟀음 | 52 | 잴 수 없음 |
| LocalVariables | 69 | 12 | dbo.UP_UTIL_SETTLE_CANCEL_INS, dbo.UP_UTIL_SETTLE_COMM_UPD, dbo.UP_UTIL_SETTLE_EXCEPTION_PROC, dbo.UP_UTIL_SETTLE_EXPECT_PROC, dbo.UP_UTIL_SETTLE_INS, dbo.UP_UTIL_SETTLE_INS_EXTRA, dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD, dbo.UP_UTIL_SETTLE_SUMMARY_ETC, dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA, dbo.UP_UTIL_STAT_PGCOLLECT_INS, dbo.UP_Util_PG_Client_CMRate_Ins, dbo.UP_Util_Settle_Summary, dbo.UP_Util_Settle_Summary_AcqManual |
| SpecConditions | 잴 수 없음 | 안 쟀음 | 잴 수 없음 |
| RoundingShapes | 잴 수 없음 | 안 쟀음 | 잴 수 없음 |
| StepTableSets | 잴 수 없음 | 해당 없음 | 잴 수 없음 |
| SpecReturnCodes | 안 쟀음 | 안 쟀음 | 잴 수 없음 |

