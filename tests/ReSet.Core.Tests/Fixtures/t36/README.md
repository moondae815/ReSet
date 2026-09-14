# T36(목차에 없는 쓰기 대상) · 제어 계약 표 면제 픽스처

판독: `docs/audit-reports/2026-09-14-목차밖-계약표-면제-사전선언.md`

| 파일 | 원본 | 역할 |
| :--- | :--- | :--- |
| `Batch16-S03.md` | `output/Jobs/POQSettleBatch16/agent/steps/S03.md` 바이트 복사(검증 불가 배너 포함 — 시험이 `InstructionBundleWriter.StripFloorBanner` 로 벗긴다) | Critic 지적으로 `batch.BatchRunLock` 소유권 승계 UPDATE 를 넣은 섹션. 목차 S03 의 `TargetTables` 는 `batch.BatchRun` · `batch.BatchStepJournal`(`raw/PlanStructure.md`), `ErrorCodes` `-9030`, 레거시 프로시저 없음. 종전 T36 발화 1(`BatchRunLock`) |
