# 실행 순서 오라클 픽스처

| 파일 | 출처 | 담은 모양 |
| :-- | :-- | :-- |
| `Batch19-flow-range-heading.md` | `output/Jobs/POQSettleBatch19/docs/BatchMigrationPlan.md` 의 `## Mermaid 기반 통합 흐름도` 절 안 `### S13～S16 요약 복합 트랜잭션` 조각(다음 `#`~`###` 헤딩까지) | 단계 절이 아닌데 `^###\s*(S\d{2})\b` 에 걸리는 **범위 헤딩** — 문서 순서로 S13 을 발급 절 앞에 올린 원인 |
| `Batch19-attempt1-S01.md` | `output/logs-planonly-POQSettleBatch19/reset-20260917.log` 의 **첫 회차** S01 단계 섹션 응답 본문 | 발급 전 사전 점검 절 |
| `Batch19-attempt1-S02-issuer.md` | 같은 로그의 첫 회차 S02 응답 본문 | `INSERT INTO batch.BatchRun` — 발급 절 |
| `Batch19-attempt1-S16-gate.md` | 같은 로그의 첫 회차 S16 응답 본문 | `SQL_S16_VERIFY_READY_TO_COMMIT` 이 `S13` 을 `Succeeded` 로 요구한다 — **첫 L1 회차에 고발된 그 게이트** |

**왜 첫 회차 원문인가**: 배송본은 수리 회차가 게이트에서 S13 을 **지운 뒤**라 오탐이 재현되지 않는다(그 삭제가 오탐의 대가였다). 처음엔 배송본의 S16 에서 S13 을 되돌리는 치환으로 재현하려 했는데 그 문장은 `Succeeded` 조건이 없는 상태 조회라 게이트로 안 쳐져 **재현에 실패했다** — 그래서 로그에서 실제로 고발된 문장을 오려 왔다.

흐름도 조각만 배송본에서 왔다(첫 회차 골격 원문은 로그 응답에서 절 경계를 확정할 수 없었다). 줄바꿈만 LF 로 정규화했다.
