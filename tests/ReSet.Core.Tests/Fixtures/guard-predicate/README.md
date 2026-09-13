# 가드 술어 대조 검사의 실물 픽스처

2026-09-13 코퍼스 배송본과 원본 DDL(`output/Procedures/<SP>/raw/metadata.json` 의 `DdlText`)을 **바이트 그대로** 옮겼다.
판독: `docs/audit-reports/2026-09-13-가드술어-표류-측정.md`

| 파일 | 원본 | 대체 SP | 역할 |
| :--- | :--- | :--- | :--- |
| `Batch11-S05.md` | `output/Jobs/POQSettleBatch11/agent/steps/S05.md` | `dbo.UP_Util_PG_Client_CMRate_Ins` | 발화 — `PLTID = 'POQ'` 추가 |
| `Batch12-S03.md` | `output/Jobs/POQSettleBatch12/agent/steps/S03.md` | `dbo.UP_Util_PG_Client_CMRate_Ins` | 발화 — `PLTID = 1` 추가 |
| `Batch12-S10.md` | `output/Jobs/POQSettleBatch12/agent/steps/S10.md` | `dbo.UP_UTIL_SETTLE_INS_EXTRA` | 발화 — `OutYMD IS NOT NULL` → `OutYMD <= @p_currYmd` |
| `Batch11-S11.md` | `output/Jobs/POQSettleBatch11/agent/steps/S11.md` | `dbo.UP_UTIL_SETTLE_INS_EXTRA` | 발화 — `OutYMD IS NOT NULL` → `ISNULL(OutYMD, '') <> ''`(데이터 의존, 처방은 옳다) |
| `Batch8-S03.md` | `output/Jobs/POQSettleBatch8/agent/steps/S03.md` | `dbo.UP_Util_PG_Client_CMRate_Ins` | 음성 — 같은 `EXISTS (SELECT 1 …)` 관용구로 원본대로 |
| `Batch1-S02.md` | `output/Jobs/POQSettleBatch1/agent/steps/S02.md` | `dbo.UP_Util_PG_Client_CMRate_Ins` | 음성 — `SELECT TOP 1 PLTID` 모양으로 원본대로 |
| `Batch10-S04.md` | `output/Jobs/POQSettleBatch10/agent/steps/S04.md` | `dbo.UP_UTIL_SETTLE_INS` | 음성 — `SELECT COUNT(1)` 모양으로 원본대로 |
| `Batch10-S08.md` | `output/Jobs/POQSettleBatch10/agent/steps/S08.md` | `dbo.UP_UTIL_SETTLE_INS_EXTRA` | 발화 — claude-cli 판. 주석으로 「원본 IF 1 가드의 이행」이라 밝히고 `OutState IN (3, 4)`·`PLTID IS NOT NULL` 을 쓰고 `OutYMD IS NOT NULL` 을 뺐다 |
| `Batch8-S09.md` | `output/Jobs/POQSettleBatch8/agent/steps/S09.md` | `dbo.UP_UTIL_SETTLE_INS_EXTRA` | 음성 — 가드 조건 7 개 원본대로(R7 시험의 바탕) |
| `UP_*.sql` | 원본 DDL | — | 오라클 |
