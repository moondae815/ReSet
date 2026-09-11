# 단계 검사 스윕

## 실행 조건

- 커밋: `6e5365c0`
- 작업 트리: 깨끗
- 캐시 인덱스 `FormatVersion` 집합: {22} — 항목 31개
- 측정 쌍: 83 (Job 5개)
- 단계 파일 누락: 0
- 목차 파싱 실패 Job: 없음
- 단계 번들 세대: 2026-09-04 ~ 2026-09-11
- 명세서 세대: 2026-09-10
- 미해결 프로시저 참조: 0
- 측정 쌍 0인 Job: 없음
- `stepInterfaces`를 `null`로 넘겼다(DB 메타데이터가 필요해 로컬에서 만들 수 없다). 검사 A~E는 이 값을 읽지 않는다.
- `runRowOwnedTables`를 `null`로 넘겼다(같은 이유). 검사 A~E는 이 값을 읽지 않는다.
- `knownTableNames`가 비어 유령 테이블 검사가 소프트 스킵됐다.

## 검사별 발화량

| 검사 | (A) 오늘 | (B) 캐시 17 모사 |
| :--- | ---: | ---: |
| A | 0 | 0 |
| B | 14 | 14 |
| C | 2 | 2 |
| D | 0 | 0 |
| E | 0 | 0 |
| P | 5 | 5 |
| 미분류 | 48 | 48 |

## Job별 발화량

| Job | 검사 | (A) 오늘 | (B) 캐시 17 모사 |
| :--- | :--- | ---: | ---: |
| POQSettleBatch1 | B | 3 | 3 |
| POQSettleBatch1 | 미분류 | 16 | 16 |
| POQSettleBatch4 | B | 2 | 2 |
| POQSettleBatch4 | C | 1 | 1 |
| POQSettleBatch4 | P | 2 | 2 |
| POQSettleBatch4 | 미분류 | 5 | 5 |
| POQSettleBatch5 | B | 3 | 3 |
| POQSettleBatch5 | 미분류 | 10 | 10 |
| POQSettleBatch6 | B | 3 | 3 |
| POQSettleBatch6 | C | 1 | 1 |
| POQSettleBatch6 | P | 2 | 2 |
| POQSettleBatch6 | 미분류 | 10 | 10 |
| POQSettleBatch7 | B | 3 | 3 |
| POQSettleBatch7 | P | 1 | 1 |
| POQSettleBatch7 | 미분류 | 7 | 7 |

## 조건 (B)는 상한이다

(B)는 모델이 「오류 코드」 표를 완전히 전사한다고 가정하고 원본 DDL에서 만든 사전을 주입한 값이다. 실제 재생성에서는 전사 오류가 나고, 그 오류는 `ErrorType.ErrorCodeTableMissing` 전사 대조가 따로 잡는다. **따라서 (B)는 축이 켜졌을 때의 상한이지 재생성 후 실제 발화량의 예측이 아니다.**

## 검사 B·C 발화 목록

판정 칸은 비어 있다 — 원본 DDL과 이행 SQL을 읽어 사람이 채운다.

| # | 검사 | 조건 | Job | 단계 | 문장 | 항목 | 판정 |
| ---: | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| 1 | B | A | POQSettleBatch1 | S03 | INSERT 1 | PGName · 외부 조인 Y(LEFT OUTER) |  |
| 2 | B | B | POQSettleBatch1 | S03 | INSERT 1 | PGName · 외부 조인 Y(LEFT OUTER) |  |
| 3 | B | A | POQSettleBatch1 | S05 | INSERT 1 | OrgYMD, MALLID · 외부 조인 Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), 파생 테이블 X · C(LEFT OUTER), 파생 테이블 X · B(LEFT OUTER) |  |
| 4 | B | B | POQSettleBatch1 | S05 | INSERT 1 | OrgYMD, MALLID · 외부 조인 Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), 파생 테이블 X · C(LEFT OUTER), 파생 테이블 X · B(LEFT OUTER) |  |
| 5 | B | A | POQSettleBatch1 | S06 | INSERT 1 | MALLID, PLTID · 외부 조인 Y(LEFT OUTER) |  |
| 6 | B | B | POQSettleBatch1 | S06 | INSERT 1 | MALLID, PLTID · 외부 조인 Y(LEFT OUTER) |  |
| 7 | B | A | POQSettleBatch4 | S03 | INSERT 1 | PGName · 외부 조인 Y(LEFT OUTER) |  |
| 8 | B | B | POQSettleBatch4 | S03 | INSERT 1 | PGName · 외부 조인 Y(LEFT OUTER) |  |
| 9 | B | A | POQSettleBatch4 | S05 | INSERT 1 | PLTID · 외부 조인 Y(LEFT OUTER) |  |
| 10 | B | B | POQSettleBatch4 | S05 | INSERT 1 | PLTID · 외부 조인 Y(LEFT OUTER) |  |
| 11 | C | A | POQSettleBatch4 | S15 | INSERT 1 | OUTSTATE |  |
| 12 | C | B | POQSettleBatch4 | S15 | INSERT 1 | OUTSTATE |  |
| 13 | B | A | POQSettleBatch5 | S02 | INSERT 1 | PGName · 외부 조인 Y(LEFT OUTER) |  |
| 14 | B | B | POQSettleBatch5 | S02 | INSERT 1 | PGName · 외부 조인 Y(LEFT OUTER) |  |
| 15 | B | A | POQSettleBatch5 | S07 | INSERT 1 | MALLID · 외부 조인 Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), 파생 테이블 X · C(LEFT OUTER), 파생 테이블 X · B(LEFT OUTER) |  |
| 16 | B | B | POQSettleBatch5 | S07 | INSERT 1 | MALLID · 외부 조인 Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), 파생 테이블 X · C(LEFT OUTER), 파생 테이블 X · B(LEFT OUTER) |  |
| 17 | B | A | POQSettleBatch5 | S08 | INSERT 1 | PLTID · 외부 조인 Y(LEFT OUTER) |  |
| 18 | B | B | POQSettleBatch5 | S08 | INSERT 1 | PLTID · 외부 조인 Y(LEFT OUTER) |  |
| 19 | C | A | POQSettleBatch6 | S01 | DELETE 1 | PGNAME |  |
| 20 | C | B | POQSettleBatch6 | S01 | DELETE 1 | PGNAME |  |
| 21 | B | A | POQSettleBatch6 | S02 | INSERT 1 | PGName · 외부 조인 Y(LEFT OUTER) |  |
| 22 | B | B | POQSettleBatch6 | S02 | INSERT 1 | PGName · 외부 조인 Y(LEFT OUTER) |  |
| 23 | B | A | POQSettleBatch6 | S07 | INSERT 1 | MALLID · 외부 조인 Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), 파생 테이블 X · C(LEFT OUTER), 파생 테이블 X · B(LEFT OUTER) |  |
| 24 | B | B | POQSettleBatch6 | S07 | INSERT 1 | MALLID · 외부 조인 Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), 파생 테이블 X · C(LEFT OUTER), 파생 테이블 X · B(LEFT OUTER) |  |
| 25 | B | A | POQSettleBatch6 | S08 | INSERT 1 | PLTID · 외부 조인 Y(LEFT OUTER) |  |
| 26 | B | B | POQSettleBatch6 | S08 | INSERT 1 | PLTID · 외부 조인 Y(LEFT OUTER) |  |
| 27 | B | A | POQSettleBatch7 | S03 | INSERT 1 | PGName · 외부 조인 Y(LEFT OUTER) |  |
| 28 | B | B | POQSettleBatch7 | S03 | INSERT 1 | PGName · 외부 조인 Y(LEFT OUTER) |  |
| 29 | B | A | POQSettleBatch7 | S07 | INSERT 1 | MALLID · 외부 조인 Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), 파생 테이블 X · C(LEFT OUTER), 파생 테이블 X · B(LEFT OUTER) |  |
| 30 | B | B | POQSettleBatch7 | S07 | INSERT 1 | MALLID · 외부 조인 Y(LEFT OUTER), 파생 테이블 X · E(LEFT OUTER), 파생 테이블 X · C(LEFT OUTER), 파생 테이블 X · B(LEFT OUTER) |  |
| 31 | B | A | POQSettleBatch7 | S08 | INSERT 1 | PLTID · 외부 조인 Y(LEFT OUTER) |  |
| 32 | B | B | POQSettleBatch7 | S08 | INSERT 1 | PLTID · 외부 조인 Y(LEFT OUTER) |  |

## 검사 P 발화 목록

| # | 조건 | Job | 단계 | 문장 | 판정 |
| ---: | :--- | :--- | :--- | :--- | :--- |
| 1 | A | POQSettleBatch4 | S11 | UPDATE 1 |  |
| 2 | B | POQSettleBatch4 | S11 | UPDATE 1 |  |
| 3 | A | POQSettleBatch4 | S15 | INSERT 1 |  |
| 4 | B | POQSettleBatch4 | S15 | INSERT 1 |  |
| 5 | A | POQSettleBatch6 | S12 | INSERT 4 |  |
| 6 | B | POQSettleBatch6 | S12 | INSERT 4 |  |
| 7 | A | POQSettleBatch6 | S13 | DELETE 1 |  |
| 8 | B | POQSettleBatch6 | S13 | DELETE 1 |  |
| 9 | A | POQSettleBatch7 | S12 | INSERT 4 |  |
| 10 | B | POQSettleBatch7 | S12 | INSERT 4 |  |

## 캐시 17 선결 지표

| 지표 | 값 |
| :--- | ---: |
| 다중 레거시 SP 단계 수 | 0 |
| SP 표에는 있는데 단계에 없는 코드가 있는 단계 수 | 55 |
| 단계에는 있는데 SP 표에 없는 코드가 있는 단계 수 | 0 |
| 펜스 파싱 실패로 코드 집합 대조에서 제외한 단계 수 | 0 |
| 코드 앵커가 둘 이상의 문장에 붙은 단계 수 | 0 |

## 침묵 분모

발화가 늘어난 자리만 보면 가려져 있던 침묵이 함께 켜지는 것을 못 본다. 승격 전에는 앵커가 안 풀려 면제가 도달 불가능하므로, 아래 값의 **증가분이 곧 이번에 새로 생긴 침묵**이다. 좌표 차분은 이 부류를 못 본다 - 가드가 조건 (A)에서도 (B)에서도 같은 좌표를 침묵시키면 차분이 정의상 0이기 때문이다.

| 분모 | 값 |
| :--- | ---: |
| 앵커가 서수로 해결된 문장 수 | 434 |
| 앵커는 있으나 서수로 환산되지 않은 문장 수 | 0 |
| (Kind, Ordinal) 모호성 가드가 버린 문장 수 | 0 |
| 원본에 있는데 이행 최상위에 없는 조인 짝 수 (발화 아님) | 116 |
| 계보 원천을 가진 문장 수 | 0 |
| 스테이징만 읽어 검사 C 가 면제한 문장 수 | 0 |
| 자기 대상을 읽는 문장 수 | 210 |
| 자기 대상을 읽어 스테이징 면제가 취소된 문장 수 | 0 |
| 하위 범위 술어 컬럼을 가진 문장 수 | 62 |
| 하위 범위 술어 컬럼의 총수 | 1089 |
| 스테이징 원천의 총수 | 0 |
| 최상위 술어 - 원본과 대조까지 간 앵커 DML 문장 수 | 383 |
| 최상위 술어 - 발화한 문장 수 | 5 |
| 최상위 술어 - 원본 DDL 이 없어 침묵(S1) | 0 |
| 최상위 술어 - 원본에 키가 없거나 모호해 침묵(S2) | 12 |
| 최상위 술어 - 원본 문장에 최상위 항이 없어 침묵(S3) | 31 |
| 최상위 술어 - 커서 그룹 면제(E2) | 8 |
| 최상위 술어 - 스테이징만 읽어 면제(E3) | 0 |
| 최상위 술어 - 명세서 L1 소진 배너로 침묵(S5) | 0 |
| 최상위 술어 - 오케스트레이션 항으로 면제한 항 수(E1) | 18 |

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
| LocalVariables | 69 | 40 | dbo.UP_UTIL_SETTLE_CANCEL_INS, dbo.UP_UTIL_SETTLE_INS, dbo.UP_UTIL_STAT_PGCOLLECT_INS, dbo.UP_Util_PG_Client_CMRate_Ins, dbo.UP_Util_Settle_Summary |
| IsL1Exhausted | 잴 수 없음 | 안 쟀음 | 잴 수 없음 |
| SpecConditions | 잴 수 없음 | 안 쟀음 | 잴 수 없음 |
| RoundingShapes | 잴 수 없음 | 안 쟀음 | 잴 수 없음 |
| StepTableSets | 잴 수 없음 | 해당 없음 | 잴 수 없음 |
| SpecReturnCodes | 안 쟀음 | 안 쟀음 | 잴 수 없음 |

