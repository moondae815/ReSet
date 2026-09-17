# 통제 합계 표·통제명 검사의 실물 픽스처

2026-09-13 코퍼스 배송본을 **바이트 그대로** 복사했다(규격 기억으로 짓지 않는다).
판독: `docs/audit-reports/2026-09-13-통제합계-어휘-일치-사전선언.md`

| 파일 | 원본 | 역할 |
| :--- | :--- | :--- |
| `Batch11-S13.md` | `output/Jobs/POQSettleBatch11/agent/steps/S13.md` | 정본 `batch.BatchControlTotal` 에 `CROSS APPLY (VALUES (N'LedgerRowCount', …))` 로 쓴다. 산문에 별칭 언급 2 곳 — K1 음성 |
| `Batch11-S20.md` | `output/Jobs/POQSettleBatch11/agent/steps/S20.md` | 별칭 `batch.ControlTotal` 을 읽고 쓴다(K1 양성). `StepCode IN (N'S13', N'S20')` 몫으로 `LEDGER_*` 를 읽는다(K2 양성) |
| `Batch4-S16.md` | `output/Jobs/POQSettleBatch4/agent/steps/S16.md` | 자기가 쓴 이름을 자기가 읽는다 — K2 음성 |
| `Batch10-S17.md` | `output/Jobs/POQSettleBatch10/agent/steps/S17.md` | `@p_controlName` 매개변수로 쓴다 — K2 음성(이름을 모르면 침묵) |
| `Batch8-S22.md` | `output/Jobs/POQSettleBatch8/agent/steps/S22.md` | 리터럴로 쓰고 읽는 단계가 없다 — K2 음성 |
| `Batch11-skeleton.md` | `output/Jobs/POQSettleBatch11/raw/attempts/run-001/skeleton.md` | 공통 규약 원천. `N'LedgerRowCount'` 를 담는다 - K2 가 규약을 어긴 쪽이 쓰는 단계일 때 쓰는 단계를 지목하는 근거 |

## 검증 SQL 세트판(2026-09-14)

`*-verification.md` 는 배송본 `docs/BatchMigrationPlan.md` 의 `## 통합 데이터 정합성 검증 SQL 세트` H2 줄부터 다음 H2 직전까지를
**바이트 그대로** 잘라 낸 것이다(잘라 낸 바이트가 원본에 그대로 들어 있음을 확인했다). 단계 파일은 `agent/steps/*.md` 그대로다.
판독: `docs/audit-reports/2026-09-14-K2-검증SQL세트-사전선언.md`

| 파일 | 원본 | 역할 |
| :--- | :--- | :--- |
| `Batch13-verification.md` | `output/Jobs/POQSettleBatch13/docs/BatchMigrationPlan.md` 9629~10450 행 | V04 가 S11 몫을 `TSettleMst.RowCount`… 로 읽는데 이름이 조인한 CTE 에 있다 — 양성(조인으로 끌어온 이름) |
| `Batch13-S11.md` | `output/Jobs/POQSettleBatch13/agent/steps/S11.md` | `SettleFact*` 여섯 이름을 리터럴로 쓴다. 쓰기 SQL 이 `#### ` 하위 헤딩 아래에 있다 |
| `Batch11-verification.md` | `output/Jobs/POQSettleBatch11/docs/BatchMigrationPlan.md` 10337~11327 행 | S13 몫을 `LedgerRowCount`(맞음)·`LedgerPOQIncome`(틀림) 으로 읽는다 — 양성 |
| `Batch12-verification.md` | `output/Jobs/POQSettleBatch12/docs/BatchMigrationPlan.md` 9958~10704 행 | 제어 표에 매개변수 이름으로 쓰고 `StepCode` 리터럴 거름은 다른 표에 있다 — 음성 |
| `Batch12-S12.md` · `Batch12-S17.md` | `output/Jobs/POQSettleBatch12/agent/steps/` | 위 세트가 가리키는 단계 |
| `Batch1-verification.md` | `output/Jobs/POQSettleBatch1/docs/BatchMigrationPlan.md` 4434~4654 행 | S02~S07 몫을 리터럴로 읽지만 그 단계들이 이름을 리터럴로 안 쓴다 — 음성(쓰는 이름 모름) |
| `Batch1-S02.md` ~ `Batch1-S07.md` | `output/Jobs/POQSettleBatch1/agent/steps/` | 위 세트가 가리키는 단계 |

## 생산자 검사(D1) 오탐(2026-09-14)

| 파일 | 원본 | 역할 |
| :--- | :--- | :--- |
| `Batch8-S22-attempt6.md` | `output/Jobs/POQSettleBatch8/raw/attempts/run-001/steps/S22.md`(manifest 상 6 차본 — L1 이 발화한 판) | 한 펜스에 `BatchStepJournal` 자기 제외 읽기와 `BatchControlTotal` INSERT 가 함께 있다 — 생산자 검사 음성(펜스 단위로 짝지어 거짓 발화했던 입력). 배송본 `Batch8-S22.md`(2 차본)와 다른 파일이다. 판독 `docs/audit-reports/2026-09-14-L1-귀속실패-측정.md` |

## 조기 반환 좁히기 회차(2026-09-16) 추가

| 파일 | 출처 | 담은 모양 |
| :-- | :-- | :-- |
| `Batch17-verification.md` | `output/Jobs/POQSettleBatch17/docs/BatchMigrationPlan.md` 의 `## 통합 데이터 정합성 검증 SQL 세트` 절 전체 | V09-01 이 S12 몫을 `Ledger.RowCount`·`Ledger.TxAmount` 로 읽고, 같은 세트에 `ControlName` 이 매개변수인 범용 헬퍼(`SQL_CAPTURE_CONTROL_TOTAL`)가 있다 — 그 「모름」 쓰기가 종전에 검사 전체를 껐다 |
| `Batch17-S12.md` | 같은 문서의 `### S12` 절 | 쓰는 이름이 `LedgerRowCount`·`TxAmt`… 다 |
| `Batch16-verification.md` | `POQSettleBatch16` 의 같은 절 | 세트가 `LedgerRowCount`… 를 **스스로 리터럴로 쓰고** 같은 이름으로 S12 몫을 읽는다(정당한 침묵) |
| `Batch16-S12.md` | 같은 문서의 `### S12` 절 | 위 침묵의 상대 |
| `Batch16-runtime-name-fence.md` | `POQSettleBatch16` 의 `N'Rule_' + R.RuleCode + N'_Rows'` 펜스 하나 | **SQL 은 실물이고 자리만 옮겼다** — 런타임 이어 붙이기 안전판을 가르는 재료(코퍼스 전수에서 이 모양은 셋: B13 둘 · B16 하나) |

줄바꿈만 LF 로 정규화했고 그 밖의 바이트는 원문과 같다.

## CTE 안 UNION ALL 쓰기 이름 회차(2026-09-17) 추가

사전 선언: `docs/audit-reports/2026-09-17-K2-CTE-UNION-쓰기이름-사전선언.md`. 단계 파일은 BOM 포함 바이트 그대로다.

| 파일 | 출처 | 담은 모양 |
| :-- | :-- | :-- |
| `Batch20-S11-attempt1.md` | 로그 `output/logs-planonly-POQSettleBatch20/reset-20260917.log` 1358 행 응답의 `message.content`(= `raw/attempts/run-001/steps/S11.md` 와 바이트 일치) | `ControlValueSet` CTE 의 UNION ALL 가지 8 이 리터럴 이름 → `SELECT … C.ControlName FROM ControlValueSet AS C` 로 쓴다(한정자 있음) |
| `Batch20-S18-attempt1.md` | 같은 로그 1975 행 응답(1 회차 — run-001 사본은 2 회차라 다르다) | S11 몫을 `LedgerTxAmt`… 8 이름으로 읽는다. S11 과의 교집합은 `LedgerRowCount` 하나 |
| `Batch15-S18.md` | `output/Jobs/POQSettleBatch15/agent/steps/S18.md` | CTE-UNION 리터럴, `ControlName` 한정자 없음 |
| `Batch19-S20.md` | `output/Jobs/POQSettleBatch19/agent/steps/S20.md` | CTE-UNION 리터럴, 한정자 없음, 앞에 CTE 셋 · 가지 일부가 다른 CTE 에서 값을 끌어온다 |
| `Batch13-S18.md` | `output/Jobs/POQSettleBatch13/agent/steps/S18.md` | CTE-UNION 위에서 `MetricName + N'.Expected'` 로 이름을 조합한다 — 모름 유지 |
