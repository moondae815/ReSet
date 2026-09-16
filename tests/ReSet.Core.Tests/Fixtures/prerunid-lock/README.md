# 발급 전 잠금 쓰기 픽스처

실물 배송본에서 **단계 절을 그대로 오려 온 것**이다(줄바꿈만 LF 로 — 그 밖의 바이트는 동일). 절 경계는 `### Sxx` 가 열고 다음 단계 헤딩 또는 상위 `##` 헤딩이 닫는 자리다.

| 파일 | 출처 | 담은 모양 |
| :-- | :-- | :-- |
| `Batch17-S02-lock-before-issue.md` | `output/Jobs/POQSettleBatch17/docs/BatchMigrationPlan.md` 1088 행 `### S02 — 영업일 실행 잠금 획득` | 발급(S03) **전** 절이 `batch.BatchRunLock` 에 `OwnerRunId` 를 INSERT·UPDATE 한다. 셋째 UPDATE(`SQL_REFRESH_OWN_LOCK`)는 `OwnerRunId` 를 **WHERE 에만** 쓴다 — SET 과 WHERE 를 가르는 시험의 재료다 |
| `Batch17-S03-issuer.md` | 같은 문서 1306 행 `### S03 실행 및 저널 초기화` | `INSERT INTO batch.BatchRun` — 발급 절 |
| `Batch17-S01-mentions-lock-table.md` | 같은 문서 828 행 `### S01 …` | 발급 전 절이 `batch.BatchRunLock` 을 **`OBJECT_ID` 존재 확인 목록의 리터럴로만** 언급한다(쓰기 없음) |
| `Batch17-S02-refresh-only.md` | 같은 절에서 **`SQL_REFRESH_OWN_LOCK` 펜스 하나만** 오려 온 것(헤딩 + 그 펜스) | `OwnerRunId` 가 **WHERE 에만** 있는 절 — 쓰기가 아니므로 통째로 침묵해야 한다 |
| `Batch17-release-moved-before-issue.md` | `POQSettleBatch17` S22 의 `SQL_RELEASE_RUN_LOCK`·`SQL_VERIFY_RUN_LOCK_RELEASE` 펜스 + B17 S02 의 헤딩 | **SQL 은 실물이고 자리만 옮겼다** — `LockStatus`(계약이 `NOT NULL` 로 정했으나 run id 자리가 **아니다**)만 쓰고 `OwnerRunId` 는 WHERE·SELECT 에서만 읽는다. 계약이 축을 말한다는 설계를 가르는 입력이다 |
| `Batch16-S02-lock-reserved-zero.md` | `POQSettleBatch16` 892 행 `### S02 — 정산일 실행 잠금 획득` | 같은 자리인데 `OwnerRunId = CAST(0 AS BIGINT)` **예약값을 지어냈다** |
| `Batch16-S03-issuer.md` | 같은 문서 1075 행 `### S03 — 배치 실행 등록` | 발급 절(+ 잠금 소유권 승계 UPDATE — Critic 이 넣게 한 것) |
| `Batch16-claim-moved-before-issue.md` | `POQSettleBatch16` S03 의 `SQL_CLAIM_RUN_LOCK` 펜스 + B16 S02 의 헤딩 | **SQL 은 실물이고 자리만 옮겼다** — `OwnerRunId` 가 SET 과 WHERE 에 함께 있는 유일한 실물 문장이다(코퍼스 전수에서 둘, 둘 다 발급 절 안이라 검사의 사정 거리 밖이었다). 어휘가 SET 줄만 싣는지 가른다 |
| `Batch11-S02-issuer.md` | `POQSettleBatch11` 819 행 `### S02 \| 배치 실행 등록` | 발급 절, 잠금 쓰기 없음 |
| `Batch11-S03-lock-after-issue.md` | 같은 문서 947 행 `### S03 \| 업무일자 실행 잠금` | **발급 뒤** 절이 잠금을 잡는다 — 침묵해야 하는 모양 |
| `Batch12-S02-issuer-and-lock.md` | `POQSettleBatch12` 768 행 `### S02 실행 등록 잠금 및 저널 초기화` | 발급 절이 **같은 절에서** 잠금까지 잡는다(배송본 13 편 중 11 편의 모양) |
