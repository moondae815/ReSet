# RunId 발급 전 단계를 요구하는 게이트 — 실물 픽스처

2026-09-14 코퍼스 배송본 `output/Jobs/POQSettleBatch{11,12,13}/agent/steps/*.md` 를 **바이트 그대로** 복사했다. 검증 세트는 `../control-total/Batch{12,13}-verification.md` 를 함께 쓴다.
판독: `docs/audit-reports/2026-09-14-RunId이전단계-게이트-사전선언.md`

| 파일 | 역할 |
| :--- | :--- |
| `Batch11-S01.md` · `Batch12-S01.md` · `Batch13-S01.md` | RunId 발급 전에 도는 단계 — 「RunId 는 S02 가 발급」을 스스로 적고 저널·체크포인트를 쓰지 않는다 |
| `Batch11-S02.md` · `Batch12-S02.md` · `Batch13-S02.md` | `INSERT INTO batch.BatchRun` 으로 RunId 를 발급한다 |
| `Batch11-S21.md` | 게이트 `StepCode IN (N'S01', …)` — 양성 |
| `Batch12-S17.md` | 게이트 `VALUES (N'S01'), …` → 체크포인트·저널 JOIN — 양성 |
| `Batch12-S18.md` | 게이트 VALUES + `StepCode IN (…)` — 양성 |
| `Batch13-S19.md` | 게이트 VALUES 가 `NOT EXISTS (… NOT EXISTS (체크포인트))` 안 — 양성 |
