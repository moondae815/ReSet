# T25 INSERT 위치 값 오탐 — 실물 픽스처

`output/Jobs/POQSettleBatch14/agent/steps/S01.md` · `S16.md` 를 **바이트 그대로** 복사했다(2026-09-14 배송본).
S01 은 `INSERT INTO batch.BatchRun (…, RunStatus, …) VALUES (…, N'Running', …)` 로만 `Running` 을 쓰고, 배송본 머리에 T25 오탐 「하한 미달」 배너가 붙어 있다.
판독: `docs/audit-reports/2026-09-14-T25-INSERT위치값-사전선언.md`

`Batch15-S01.md` 는 `output/Jobs/POQSettleBatch15/agent/steps/S01.md` 를 바이트 그대로 복사했다 — **검증 불가** 배너가 붙은 실물이다.
`Batch14-S01.md`(하한 미달 배너)와 함께 스윕 배너 벗기기 시험(`StepFileBannerStripTests`)이 쓴다. 판독: `docs/audit-reports/2026-09-14-가드PLTID-날조-원인-측정.md` §2
