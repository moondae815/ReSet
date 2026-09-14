# 이름 블록 검사 · 공통 규약 픽스처

판독: `docs/audit-reports/2026-09-14-이름블록-공통규약-사전선언.md`

| 파일 | 원본 | 역할 |
| :--- | :--- | :--- |
| `Batch16-S04-first-draft.md` | `output/logs-planonly-POQSettleBatch16/reset-20260914.log` 2325 행 응답 JSON(`gen-1789361680-b6XQZkgCGiPIOvXkQKse`)의 `choices[0].message.content` 를 UTF-8 로 그대로 | S04 첫 초안 — 공통 규약의 `SQL_INSERT_STEP_START`·`SQL_MARK_STEP_SUCCEEDED`·`SQL_MARK_STEP_FAILED` 를 부르기만 했다(판 안에서 이 사유만으로 재생성) |
| `Batch16-skeleton.md` | `output/Jobs/POQSettleBatch16/raw/attempts/run-001/skeleton.md` 바이트 복사(골격 시도 1 회뿐이라 판 안 골격과 같다) | 공통 규약 재료 |
| `Batch1-S08.md` | `output/Jobs/POQSettleBatch1/agent/steps/S08.md` 바이트 복사 | 배송본 — `SQL_CURRENT_RUN_ID`(공통 정의) + 공통에도 없는 다섯 |
| `Batch1-01-step-contract.md` | `output/Jobs/POQSettleBatch1/agent/common/01-step-contract.md` 바이트 복사 | 배송 공통 규약 — `-- SQL_CURRENT_RUN_ID` 정의와 의사코드 호출을 함께 담는다 |
