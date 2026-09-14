# T25 INSERT 위치 값 오탐 — 실물 픽스처

`output/Jobs/POQSettleBatch14/agent/steps/S01.md` · `S16.md` 를 **바이트 그대로** 복사했다(2026-09-14 배송본).
S01 은 `INSERT INTO batch.BatchRun (…, RunStatus, …) VALUES (…, N'Running', …)` 로만 `Running` 을 쓰고, 배송본 머리에 T25 오탐 「하한 미달」 배너가 붙어 있다.
판독: `docs/audit-reports/2026-09-14-T25-INSERT위치값-사전선언.md`
