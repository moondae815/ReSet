# 계약 밖 batch 표 컬럼 계약 픽스처

판독: `docs/audit-reports/2026-09-16-제어표-컬럼계약-검사-사전선언.md`

| 파일 | 원본 | 역할 |
| :--- | :--- | :--- |
| `Batch13-create-table.sql.md` | `output/Jobs/POQSettleBatch13/docs/BatchMigrationPlan.md` 8834~8848 행의 SQL 펜스를 그대로(줄바꿈만 LF) | S18 이 만드는 `batch.BatchReconciliation` 정의 — `ReconciliationName`·`IsMatched` 등 여덟 컬럼 |
| `Batch13-verification-query.sql.md` | 같은 문서 10388~10398 행의 SQL 펜스 | 검증 SQL 세트 V15 — `CheckCode`·`CheckName`·`ResultStatus`·`DifferenceCount`·`DetailMessage` 를 읽는다. 다섯 중 **하나도** 정의에 없다 |
