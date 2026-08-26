# 단계 검사 스윕

## 실행 조건

- 커밋: `cd6be15`
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
| B | 1 | 70 |
| C | 0 | 38 |
| D | 18 | 18 |
| E | 59 | 59 |
| 미분류 | 977 | 977 |

## Job별 발화량

| Job | 검사 | (A) 오늘 | (B) 캐시 17 모사 |
| :--- | :--- | ---: | ---: |
| POQSettleBatch1 | A | 2 | 2 |
| POQSettleBatch1 | B | 1 | 2 |
| POQSettleBatch1 | C | 0 | 2 |
| POQSettleBatch1 | D | 9 | 9 |
| POQSettleBatch1 | E | 3 | 3 |
| POQSettleBatch1 | 미분류 | 8 | 8 |
| POQSettlePrco20 | B | 0 | 7 |
| POQSettlePrco20 | C | 0 | 4 |
| POQSettlePrco20 | E | 6 | 6 |
| POQSettlePrco20 | 미분류 | 12 | 12 |
| POQSettleProc1 | B | 0 | 6 |
| POQSettleProc1 | E | 4 | 4 |
| POQSettleProc1 | 미분류 | 38 | 38 |
| POQSettleProc10 | A | 9 | 9 |
| POQSettleProc10 | E | 1 | 1 |
| POQSettleProc10 | 미분류 | 99 | 99 |
| POQSettleProc11 | B | 0 | 6 |
| POQSettleProc11 | C | 0 | 5 |
| POQSettleProc11 | E | 2 | 2 |
| POQSettleProc11 | 미분류 | 27 | 27 |
| POQSettleProc12 | B | 0 | 6 |
| POQSettleProc12 | C | 0 | 3 |
| POQSettleProc12 | E | 1 | 1 |
| POQSettleProc12 | 미분류 | 52 | 52 |
| POQSettleProc13 | A | 1 | 1 |
| POQSettleProc13 | B | 0 | 4 |
| POQSettleProc13 | C | 0 | 2 |
| POQSettleProc13 | E | 1 | 1 |
| POQSettleProc13 | 미분류 | 108 | 108 |
| POQSettleProc14 | B | 0 | 7 |
| POQSettleProc14 | C | 0 | 5 |
| POQSettleProc14 | D | 9 | 9 |
| POQSettleProc14 | E | 6 | 6 |
| POQSettleProc14 | 미분류 | 47 | 47 |
| POQSettleProc15 | A | 1 | 1 |
| POQSettleProc15 | B | 0 | 2 |
| POQSettleProc15 | C | 0 | 2 |
| POQSettleProc15 | E | 3 | 3 |
| POQSettleProc15 | 미분류 | 158 | 158 |
| POQSettleProc16 | B | 0 | 5 |
| POQSettleProc16 | C | 0 | 4 |
| POQSettleProc16 | E | 3 | 3 |
| POQSettleProc16 | 미분류 | 117 | 117 |
| POQSettleProc17 | B | 0 | 7 |
| POQSettleProc17 | C | 0 | 4 |
| POQSettleProc17 | E | 8 | 8 |
| POQSettleProc17 | 미분류 | 18 | 18 |
| POQSettleProc18 | C | 0 | 2 |
| POQSettleProc18 | E | 9 | 9 |
| POQSettleProc18 | 미분류 | 11 | 11 |
| POQSettleProc19 | B | 0 | 8 |
| POQSettleProc19 | C | 0 | 4 |
| POQSettleProc19 | E | 6 | 6 |
| POQSettleProc19 | 미분류 | 11 | 11 |
| POQSettleProc2 | 미분류 | 65 | 65 |
| POQSettleProc3 | A | 1 | 1 |
| POQSettleProc3 | B | 0 | 4 |
| POQSettleProc3 | C | 0 | 1 |
| POQSettleProc3 | 미분류 | 21 | 21 |
| POQSettleProc6 | 미분류 | 132 | 132 |
| POQSettleProc8 | A | 3 | 3 |
| POQSettleProc8 | B | 0 | 2 |
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
| 1 | B | A | POQSettleBatch1 | S07 | UPDATE 13 | YMD, PGNAME | 진짜 결함(앞 회차 실물 확인) |
| 2 | B | B | POQSettleBatch1 | S07 | UPDATE 13 | YMD, PGNAME | 진짜 결함(앞 회차 실물 확인) |
| 3 | C | B | POQSettleBatch1 | S09 | UPDATE 2 | USESTATE | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 4 | C | B | POQSettleBatch1 | S10 | UPDATE 2 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 5 | B | B | POQSettleBatch1 | S11 | UPDATE 9 | YMD, UseState |  |
| 6 | B | B | POQSettlePrco20 | S06 | UPDATE 2 | YMD, PGName, DiscountFlag | 거짓양성 — 계산용 서브쿼리 관용구(술어가 파생 테이블로, 최상위는 PLTID+ID 행 동일성 조인) |
| 7 | B | B | POQSettlePrco20 | S06 | UPDATE 10 | MALLID |  |
| 8 | B | B | POQSettlePrco20 | S06 | UPDATE 10 | PGNAME, MALLID |  |
| 9 | B | B | POQSettlePrco20 | S06 | UPDATE 13 | YMD, PGNAME |  |
| 10 | B | B | POQSettlePrco20 | S06 | UPDATE 15 | MobileCo |  |
| 11 | B | B | POQSettlePrco20 | S06 | UPDATE 17 | YMD, PGName | 거짓양성 — CTE 관용구(술어가 CTE 안으로) |
| 12 | B | B | POQSettlePrco20 | S06 | UPDATE 18 | PLTID | 거짓양성 — IN→EXISTS 재작성으로 PLTID가 상관 하위질의로 이동 |
| 13 | C | B | POQSettlePrco20 | S06 | UPDATE 12 | PGName |  |
| 14 | C | B | POQSettlePrco20 | S07 | UPDATE 7 | UseState |  |
| 15 | C | B | POQSettlePrco20 | S08 | UPDATE 4 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 16 | C | B | POQSettlePrco20 | S09 | UPDATE 2 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 17 | B | B | POQSettleProc1 | S04 | UPDATE 2 | YMD, PGName, DiscountFlag | 거짓양성 — 계산용 서브쿼리 관용구(술어가 파생 테이블로, 최상위는 PLTID+ID 행 동일성 조인) |
| 18 | B | B | POQSettleProc1 | S04 | UPDATE 13 | PLTID, YMD, PGNAME |  |
| 19 | B | B | POQSettleProc1 | S04 | UPDATE 17 | YMD, PGName | 거짓양성 — CTE 관용구(술어가 CTE 안으로) |
| 20 | B | B | POQSettleProc1 | S04 | UPDATE 18 | PLTID | 거짓양성 — IN→EXISTS 재작성으로 PLTID가 상관 하위질의로 이동 |
| 21 | B | B | POQSettleProc1 | S05 | UPDATE 7 | PLTID |  |
| 22 | B | B | POQSettleProc1 | S11 | DELETE 4 | OUTSTATE |  |
| 23 | B | B | POQSettleProc11 | S06 | UPDATE 2 | YMD, PGName, DiscountFlag | 거짓양성 — 계산용 서브쿼리 관용구(술어가 파생 테이블로, 최상위는 PLTID+ID 행 동일성 조인) |
| 24 | B | B | POQSettleProc11 | S06 | UPDATE 10 | MALLID |  |
| 25 | B | B | POQSettleProc11 | S06 | UPDATE 10 | PGNAME, MALLID |  |
| 26 | B | B | POQSettleProc11 | S06 | UPDATE 13 | ID |  |
| 27 | B | B | POQSettleProc11 | S06 | UPDATE 17 | YMD, PGName | 거짓양성 — CTE 관용구(술어가 CTE 안으로) |
| 28 | C | B | POQSettleProc11 | S06 | UPDATE 13 | UseState, AYMD |  |
| 29 | C | B | POQSettleProc11 | S06 | UPDATE 18 | YMD, OutState |  |
| 30 | C | B | POQSettleProc11 | S07 | UPDATE 7 | UseState, CommissionCancelFlag |  |
| 31 | C | B | POQSettleProc11 | S08 | UPDATE 4 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 32 | B | B | POQSettleProc11 | S09 | UPDATE 2 | ExtraType |  |
| 33 | C | B | POQSettleProc11 | S09 | UPDATE 2 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 34 | B | B | POQSettleProc12 | S07 | UPDATE 2 | YMD, PGName, DiscountFlag | 거짓양성 — 계산용 서브쿼리 관용구(술어가 파생 테이블로, 최상위는 PLTID+ID 행 동일성 조인) |
| 35 | B | B | POQSettleProc12 | S07 | UPDATE 13 | PLTID, YMD, PGNAME |  |
| 36 | B | B | POQSettleProc12 | S07 | UPDATE 17 | YMD, PGName | 거짓양성 — CTE 관용구(술어가 CTE 안으로) |
| 37 | B | B | POQSettleProc12 | S07 | UPDATE 18 | PLTID | 거짓양성 — IN→EXISTS 재작성으로 PLTID가 상관 하위질의로 이동 |
| 38 | C | B | POQSettleProc12 | S07 | UPDATE 1 | ExtraSettleFlag |  |
| 39 | C | B | POQSettleProc12 | S07 | UPDATE 12 | PGName |  |
| 40 | B | B | POQSettleProc12 | S08 | UPDATE 7 | PLTID |  |
| 41 | B | B | POQSettleProc12 | S11 | UPDATE 2 | PGName, ExtraType |  |
| 42 | C | B | POQSettleProc12 | S11 | UPDATE 2 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 43 | B | B | POQSettleProc13 | S07 | UPDATE 2 | YMD, DiscountFlag | 거짓양성 — 계산용 서브쿼리 관용구(술어가 파생 테이블로, 최상위는 PLTID+ID 행 동일성 조인) |
| 44 | B | B | POQSettleProc13 | S08 | UPDATE 7 | PLTID |  |
| 45 | B | B | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID, CollectPeriodID, CollectFlag | 진짜 결함(앞 회차 실물 확인) |
| 46 | B | B | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID, CollectPeriodID | 진짜 결함(앞 회차 실물 확인) |
| 47 | C | B | POQSettleProc13 | S10 | UPDATE 4 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 48 | C | B | POQSettleProc13 | S11 | UPDATE 2 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 49 | B | B | POQSettleProc14 | S07 | UPDATE 2 | YMD, PGName, DiscountFlag | 거짓양성 — 계산용 서브쿼리 관용구(술어가 파생 테이블로, 최상위는 PLTID+ID 행 동일성 조인) |
| 50 | B | B | POQSettleProc14 | S07 | UPDATE 10 | MALLID |  |
| 51 | B | B | POQSettleProc14 | S07 | UPDATE 10 | PGNAME, MALLID |  |
| 52 | B | B | POQSettleProc14 | S07 | UPDATE 13 | YMD, PGNAME |  |
| 53 | B | B | POQSettleProc14 | S07 | UPDATE 17 | YMD, PGName | 거짓양성 — CTE 관용구(술어가 CTE 안으로) |
| 54 | B | B | POQSettleProc14 | S07 | UPDATE 18 | PLTID | 거짓양성 — IN→EXISTS 재작성으로 PLTID가 상관 하위질의로 이동 |
| 55 | C | B | POQSettleProc14 | S07 | UPDATE 1 | ExtraSettleFlag |  |
| 56 | C | B | POQSettleProc14 | S07 | UPDATE 12 | PGName |  |
| 57 | C | B | POQSettleProc14 | S07 | UPDATE 18 | YMD | 진짜 후보 — 원본에 없는 최상위 YMD 추가(범위 축소) |
| 58 | B | B | POQSettleProc14 | S08 | UPDATE 7 | PLTID |  |
| 59 | C | B | POQSettleProc14 | S10 | UPDATE 4 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 60 | C | B | POQSettleProc14 | S11 | UPDATE 2 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 61 | B | B | POQSettleProc15 | S07 | UPDATE 2 | YMD, DiscountFlag | 거짓양성 — 계산용 서브쿼리 관용구(술어가 파생 테이블로, 최상위는 PLTID+ID 행 동일성 조인) |
| 62 | B | B | POQSettleProc15 | S07 | UPDATE 18 | PLTID | 거짓양성 — IN→EXISTS 재작성으로 PLTID가 상관 하위질의로 이동 |
| 63 | C | B | POQSettleProc15 | S10 | UPDATE 4 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 64 | C | B | POQSettleProc15 | S11 | UPDATE 2 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 65 | B | B | POQSettleProc16 | S07 | UPDATE 2 | YMD, PGName, DiscountFlag | 거짓양성 — 계산용 서브쿼리 관용구(술어가 파생 테이블로, 최상위는 PLTID+ID 행 동일성 조인) |
| 66 | B | B | POQSettleProc16 | S07 | UPDATE 13 | PLTID, YMD, PGNAME |  |
| 67 | B | B | POQSettleProc16 | S07 | UPDATE 17 | YMD, PGName | 거짓양성 — CTE 관용구(술어가 CTE 안으로) |
| 68 | B | B | POQSettleProc16 | S07 | UPDATE 18 | PLTID | 거짓양성 — IN→EXISTS 재작성으로 PLTID가 상관 하위질의로 이동 |
| 69 | C | B | POQSettleProc16 | S07 | UPDATE 12 | PGName |  |
| 70 | B | B | POQSettleProc16 | S08 | UPDATE 7 | PLTID |  |
| 71 | C | B | POQSettleProc16 | S08 | UPDATE 7 | CommissionCancelFlag |  |
| 72 | C | B | POQSettleProc16 | S10 | UPDATE 4 | UseState |  |
| 73 | C | B | POQSettleProc16 | S11 | UPDATE 2 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 74 | B | B | POQSettleProc17 | S07 | UPDATE 2 | YMD, PGName, DiscountFlag | 거짓양성 — 계산용 서브쿼리 관용구(술어가 파생 테이블로, 최상위는 PLTID+ID 행 동일성 조인) |
| 75 | B | B | POQSettleProc17 | S07 | UPDATE 10 | MALLID | 진짜 결함(앞 회차 실물 확인) |
| 76 | B | B | POQSettleProc17 | S07 | UPDATE 10 | PGNAME, MALLID | 진짜 결함(앞 회차 실물 확인) |
| 77 | B | B | POQSettleProc17 | S07 | UPDATE 13 | ID |  |
| 78 | B | B | POQSettleProc17 | S07 | UPDATE 17 | YMD, PGName | 거짓양성 — CTE 관용구(술어가 CTE 안으로) |
| 79 | B | B | POQSettleProc17 | S07 | UPDATE 18 | PLTID | 거짓양성 — IN→EXISTS 재작성으로 PLTID가 상관 하위질의로 이동 |
| 80 | C | B | POQSettleProc17 | S07 | UPDATE 12 | PGName |  |
| 81 | B | B | POQSettleProc17 | S08 | UPDATE 7 | PLTID |  |
| 82 | C | B | POQSettleProc17 | S08 | UPDATE 7 | UseState |  |
| 83 | C | B | POQSettleProc17 | S10 | UPDATE 4 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 84 | C | B | POQSettleProc17 | S11 | UPDATE 2 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 85 | C | B | POQSettleProc18 | S10 | UPDATE 4 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 86 | C | B | POQSettleProc18 | S11 | UPDATE 2 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 87 | C | B | POQSettleProc19 | S08 | UPDATE 4 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 88 | C | B | POQSettleProc19 | S09 | UPDATE 2 | UseState | 거짓 귀속 — 같은 코드 라벨이 여러 문장에 붙어 오환산 |
| 89 | B | B | POQSettleProc19 | S10 | UPDATE 2 | YMD, PGName, DiscountFlag | 거짓양성 — 계산용 서브쿼리 관용구(술어가 파생 테이블로, 최상위는 PLTID+ID 행 동일성 조인) |
| 90 | B | B | POQSettleProc19 | S10 | UPDATE 10 | MALLID |  |
| 91 | B | B | POQSettleProc19 | S10 | UPDATE 10 | PGNAME, MALLID |  |
| 92 | B | B | POQSettleProc19 | S10 | UPDATE 13 | YMD, PGNAME |  |
| 93 | B | B | POQSettleProc19 | S10 | UPDATE 17 | YMD, PGName | 거짓양성 — CTE 관용구(술어가 CTE 안으로) |
| 94 | B | B | POQSettleProc19 | S10 | UPDATE 18 | PLTID | 거짓양성 — IN→EXISTS 재작성으로 PLTID가 상관 하위질의로 이동 |
| 95 | C | B | POQSettleProc19 | S10 | UPDATE 12 | PGName |  |
| 96 | B | B | POQSettleProc19 | S11 | UPDATE 7 | PLTID |  |
| 97 | B | B | POQSettleProc19 | S11 | UPDATE 10 | CYMD, AYMD, RefundFlag |  |
| 98 | C | B | POQSettleProc19 | S11 | UPDATE 11 | RefundFlag, CYMD, AYMD |  |
| 99 | B | B | POQSettleProc3 | S04 | UPDATE 2 | YMD, PGName, DiscountFlag | 거짓양성 — 계산용 서브쿼리 관용구(술어가 파생 테이블로, 최상위는 PLTID+ID 행 동일성 조인) |
| 100 | B | B | POQSettleProc3 | S04 | UPDATE 17 | YMD, PGName | 거짓양성 — CTE 관용구(술어가 CTE 안으로) |
| 101 | B | B | POQSettleProc3 | S04 | UPDATE 18 | PLTID | 거짓양성 — IN→EXISTS 재작성으로 PLTID가 상관 하위질의로 이동 |
| 102 | C | B | POQSettleProc3 | S04 | UPDATE 1 | ExtraSettleFlag |  |
| 103 | B | B | POQSettleProc3 | S05 | UPDATE 7 | PLTID |  |
| 104 | B | B | POQSettleProc8 | S10 | UPDATE 5 | ProcYMD, YMD, PGNAME, CompanySalesType, TxAmt, CLVTType, ExtraSettleFlag |  |
| 105 | B | B | POQSettleProc8 | S12 | DELETE 4 | OUTSTATE |  |
| 106 | B | B | POQSettleProc9 | S06 | UPDATE 2 | YMD, PGName, DiscountFlag | 거짓양성 — 계산용 서브쿼리 관용구(술어가 파생 테이블로, 최상위는 PLTID+ID 행 동일성 조인) |
| 107 | B | B | POQSettleProc9 | S06 | UPDATE 17 | YMD, PGName | 거짓양성 — CTE 관용구(술어가 CTE 안으로) |
| 108 | B | B | POQSettleProc9 | S06 | UPDATE 18 | PLTID | 거짓양성 — IN→EXISTS 재작성으로 PLTID가 상관 하위질의로 이동 |
| 109 | B | B | POQSettleProc9 | S13 | DELETE 4 | OUTSTATE |  |

## 캐시 17 선결 지표

| 지표 | 값 |
| :--- | ---: |
| 다중 레거시 SP 단계 수 | 2 |
| SP 표에는 있는데 단계에 없는 코드가 있는 단계 수 | 68 |
| 단계에는 있는데 SP 표에 없는 코드가 있는 단계 수 | 63 |
| 펜스 파싱 실패로 코드 집합 대조에서 제외한 단계 수 | 46 |

펜스 파싱 실패로 46개 단계를 코드 집합 대조에서 제외했다 - 위 두 코드 집합 지표(SP 표에는 있는데 단계에 없는 코드, 단계에는 있는데 SP 표에 없는 코드)의 분모가 그만큼 줄었다는 뜻이다. 이 값이 크면 두 지표가 코퍼스 전체를 대표하지 않는다.

