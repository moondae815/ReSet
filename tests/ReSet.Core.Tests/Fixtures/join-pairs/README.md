# 조인 짝 프롬프트 픽스처

판독: `docs/audit-reports/2026-09-16-조인짝-프롬프트-사전선언.md`

| 파일 | 원본 | 역할 |
| :--- | :--- | :--- |
| `UP_UTIL_SETTLE_EXPECT_PROC.sql` | `output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/raw/metadata.json` 의 `DdlText` 를 그대로(줄바꿈만 LF) | 오라클. UPDATE 11(245 행)의 `ON` 은 `A.MPLTID = B.PLTID` 와 `B ↔ TClientCMRate` 세 짝뿐이고, GPT 판 다섯이 여기에 `A.ClientID = B.ClientID` 를 더했다 |
