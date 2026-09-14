# 통제 합계 표·통제명 검사의 실물 픽스처

2026-09-13 코퍼스 배송본을 **바이트 그대로** 복사했다(규격 기억으로 짓지 않는다).
판독: `docs/audit-reports/2026-09-13-통제합계-어휘-일치-사전선언.md`

| 파일 | 원본 | 역할 |
| :--- | :--- | :--- |
| `Batch11-S13.md` | `output/Jobs/POQSettleBatch11/agent/steps/S13.md` | 정본 `batch.BatchControlTotal` 에 `CROSS APPLY (VALUES (N'LedgerRowCount', …))` 로 쓴다. 산문에 별칭 언급 2 곳 — K1 음성 |
| `Batch11-S20.md` | `output/Jobs/POQSettleBatch11/agent/steps/S20.md` | 별칭 `batch.ControlTotal` 을 읽고 쓴다(K1 양성). `StepCode IN (N'S13', N'S20')` 몫으로 `LEDGER_*` 를 읽는다(K2 양성) |
| `Batch4-S16.md` | `output/Jobs/POQSettleBatch4/agent/steps/S16.md` | 자기가 쓴 이름을 자기가 읽는다 — K2 음성 |
| `Batch10-S17.md` | `output/Jobs/POQSettleBatch10/agent/steps/S17.md` | `@p_controlName` 매개변수로 쓴다 — K2 음성(이름을 모르면 침묵) |
| `Batch8-S22.md` | `output/Jobs/POQSettleBatch8/agent/steps/S22.md` | 리터럴로 쓰고 읽는 단계가 없다 — K2 음성 |
| `Batch11-skeleton.md` | `output/Jobs/POQSettleBatch11/raw/attempts/run-001/skeleton.md` | 공통 규약 원천. `N'LedgerRowCount'` 를 담는다 - K2 가 규약을 어긴 쪽이 쓰는 단계일 때 쓰는 단계를 지목하는 근거 |

## 검증 SQL 세트판(2026-09-14)

`*-verification.md` 는 배송본 `docs/BatchMigrationPlan.md` 의 `## 통합 데이터 정합성 검증 SQL 세트` H2 줄부터 다음 H2 직전까지를
**바이트 그대로** 잘라 낸 것이다(잘라 낸 바이트가 원본에 그대로 들어 있음을 확인했다). 단계 파일은 `agent/steps/*.md` 그대로다.
판독: `docs/audit-reports/2026-09-14-K2-검증SQL세트-사전선언.md`

| 파일 | 원본 | 역할 |
| :--- | :--- | :--- |
| `Batch13-verification.md` | `output/Jobs/POQSettleBatch13/docs/BatchMigrationPlan.md` 9629~10450 행 | V04 가 S11 몫을 `TSettleMst.RowCount`… 로 읽는데 이름이 조인한 CTE 에 있다 — 양성(조인으로 끌어온 이름) |
| `Batch13-S11.md` | `output/Jobs/POQSettleBatch13/agent/steps/S11.md` | `SettleFact*` 여섯 이름을 리터럴로 쓴다. 쓰기 SQL 이 `#### ` 하위 헤딩 아래에 있다 |
| `Batch11-verification.md` | `output/Jobs/POQSettleBatch11/docs/BatchMigrationPlan.md` 10337~11327 행 | S13 몫을 `LedgerRowCount`(맞음)·`LedgerPOQIncome`(틀림) 으로 읽는다 — 양성 |
| `Batch12-verification.md` | `output/Jobs/POQSettleBatch12/docs/BatchMigrationPlan.md` 9958~10704 행 | 제어 표에 매개변수 이름으로 쓰고 `StepCode` 리터럴 거름은 다른 표에 있다 — 음성 |
| `Batch12-S12.md` · `Batch12-S17.md` | `output/Jobs/POQSettleBatch12/agent/steps/` | 위 세트가 가리키는 단계 |
| `Batch1-verification.md` | `output/Jobs/POQSettleBatch1/docs/BatchMigrationPlan.md` 4434~4654 행 | S02~S07 몫을 리터럴로 읽지만 그 단계들이 이름을 리터럴로 안 쓴다 — 음성(쓰는 이름 모름) |
| `Batch1-S02.md` ~ `Batch1-S07.md` | `output/Jobs/POQSettleBatch1/agent/steps/` | 위 세트가 가리키는 단계 |
