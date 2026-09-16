# 최상위 술어 대조(P) · 괄호 정규화 픽스처

판독: `docs/audit-reports/2026-09-16-술어항-괄호정규화-사전선언.md`

| 파일 | 원본 | 역할 |
| :--- | :--- | :--- |
| `UP_UTIL_SETTLE_EXCEPTION_PROC.sql` | `output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/raw/metadata.json` 의 `DdlText` 를 바이트 그대로 | 오라클. UPDATE 3(109~119 행)의 최상위 술어에 `(A.TXAMT-ISNULL(A.NonSettleAmt,0))` 로 인자를 감싼 괄호가 있다 |
| `Batch16-S07-first-draft-update3.sql` | `output/logs-planonly-POQSettleBatch16/reset-20260914.log` 의 S07 **첫 초안** 응답에서 `-- SQL_UPDATE_03` 블록을 그대로 오림 | 그 괄호만 없는 이행. 여섯 판 전부 이 모양으로 재생성됐다 |
