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
