# 대상 표가 없는 단계 픽스처

| 파일 | 출처 | 담은 모양 |
| :-- | :-- | :-- |
| `Batch18-S01-read-only.md` | `output/Jobs/POQSettleBatch18/docs/BatchMigrationPlan.md` 의 `### S01 입력 및 실행환경 검증` 절 전체(다음 단계 헤딩 또는 상위 `##` 헤딩까지) | 목차가 `TargetTables` 를 비운 단계이고 **본문이 아무것도 쓰지 않는다**(SQL 펜스 4 · 쓰기 문장 0). 배송 배너 「검증 불가」가 붙던 실물이다 |
| `Batch18-S02-lock-insert-fence.md` | 같은 문서의 `### S02` 절에 있는 `SQL_INSERT_RUN_LOCK` 펜스 하나 | **자리만 옮겼다** — 위 절에 붙여 「쓰는데 선언이 없다」를 만드는 재료 |
| `exec-fence.md` | `POQSettleBatch1` 의 **공통 계약 템플릿**에 있는 `SQL_CREATE_AND_CAPTURE_SHADOW` 펜스(`output/Jobs/POQSettleBatch1/agent/common/01-step-contract.md` 및 그 배송본 사본) | 동적 SQL(`sp_executesql`)로 `SELECT * INTO` 를 만든다 — 「무엇을 쓰는지 모른다」를 쓰기로 세는 재료. **개별 Job 의 단계 본문이 아니라 배치 간 공유 템플릿에서 왔다** |

줄바꿈만 LF 로 정규화했고 그 밖의 바이트는 원문과 같다.

**합성 재료도 있다(실물이 없어서다)**: `TRUNCATE`·`SELECT … INTO`·`EXECUTE AS` 세 모양은 ScriptDom 으로 코퍼스 SQL 펜스 5,514 개를 전수로 읽어도 **0 건**이다. 그 세 갈래는 `NoTargetStepTests` 안에 최소 SQL 로 직접 써 두었고, 시험 주석에 합성임을 적었다.
