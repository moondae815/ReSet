### S18 — 통합 정합성 검증

#### 목적과 실행 경계

- S17 성공 후, S19 완료 게시 전에 순차 실행한다. 다른 단계와 병렬 실행하지 않는다.
- 비레거시 제어 단계이며 입력은 오케스트레이터가 보유한 `runId`와 `businessYmd`이다. 재시작·건너뛰기·우회 플래그는 받지 않는다.
- `batch.BatchStepJournal`에서 S02~S17의 성공 여부를 확인하고, `batch.BatchControlTotal`에 S11이 기록한 동결 합계와 현재 정산원장을 대조한다.
- 원장과 `TSettleByTX`, `TPartialCancelByTX`, `TSettleByIN`, `TSettleByOUT` 요약 합계를 독립 집계하여 비교하고, `TStatPGCollect`의 차변·대변 계산식을 검증한다.
- 모든 조회와 S18 검증 결과 기록은 하나의 **SNAPSHOT 격리 트랜잭션**에서 수행한다. 트랜잭션 구성 방식은 구현 라운드가 선택하되 이 격리 의무를 변경할 수 없다.
- 비청킹 단일 트랜잭션 단계이므로 Shadow Table과 실패 후 보상 DELETE를 사용하지 않는다. 모든 SQL에서 `NOLOCK` 힌트를 제거한다.
- 제어 단계 일반 실패 코드는 **-9180**이다. `currentStepErrorCode`는 `0`으로 초기화하고, 첫 검증문 실행 직전에 `-9180`으로 변경한다. 추가 오류 코드는 만들지 않는다.

#### 애플리케이션 제어 흐름

```pseudocode
executeS18(runId, businessYmd):
    // S18IntegratedConsistencyValidator의 입력
    // runId -> p_runId
    // businessYmd -> p_ymd, 정확히 8자리 업무일자

    recordStepStart(runId, "S18")

    currentStepErrorCode = 0
    currentStatementName = NULL
    mismatches = []
    validationCheckCount = 0

    conn = connectionFactory.open()
    tx = conn.beginTransaction()  // SNAPSHOT 격리 보장

    TRY:
        currentStatementName = "SQL_FIND_INCOMPLETE_PRIOR_STEPS"
        currentStepErrorCode = -9180
        incompleteSteps = repository.queryRows(
            conn,
            tx,
            SQL_FIND_INCOMPLETE_PRIOR_STEPS,
            { p_runId: runId }
        )
        validationCheckCount += 16

        FOR each row in incompleteSteps:
            mismatches.add({
                check: "PriorStepSucceeded",
                key: row.StepCode,
                expected: "Succeeded",
                actual: row.ActualStatus
            })

        currentStatementName = "SQL_READ_S11_LEDGER_TOTALS"
        currentStepErrorCode = -9180
        frozenTotals = repository.queryRows(
            conn,
            tx,
            SQL_READ_S11_LEDGER_TOTALS,
            { p_runId: runId }
        )

        currentStatementName = "SQL_READ_CURRENT_LEDGER_TOTALS"
        currentStepErrorCode = -9180
        currentLedgerTotals = repository.queryRow(
            conn,
            tx,
            SQL_READ_CURRENT_LEDGER_TOTALS,
            { p_ymd: businessYmd }
        )

        requiredFrozenNames = [
            "LedgerRowCount",
            "LedgerTxAmt",
            "LedgerCLTotal",
            "LedgerPGTotal",
            "LedgerPOQIncome",
            "LedgerExtraTxAmt",
            "LedgerSeperateAmt",
            "LedgerForeignSettleAmt"
        ]

        FOR each controlName in requiredFrozenNames:
            validationCheckCount += 1
            expectedRows = frozenTotals where ControlName == controlName

            IF expectedRows.count != 1:
                mismatches.add({
                    check: "S11FrozenControlPresence",
                    key: controlName,
                    expected: "exactly one row",
                    actual: expectedRows.count
                })
            ELSE IF decimal38_4(expectedRows[0].ControlValue)
                    != decimal38_4(currentLedgerTotals[controlName]):
                mismatches.add({
                    check: "LedgerFreeze",
                    key: controlName,
                    expected: expectedRows[0].ControlValue,
                    actual: currentLedgerTotals[controlName]
                })

        currentStatementName = "SQL_READ_SUMMARY_RECONCILIATION_SIDES"
        currentStepErrorCode = -9180
        reconciliationSides = repository.queryRows(
            conn,
            tx,
            SQL_READ_SUMMARY_RECONCILIATION_SIDES,
            { p_ymd: businessYmd }
        )

        FOR each checkName in ["ByTX", "PartialCancel", "ByIN", "ByOUT"]:
            source = reconciliationSides where CheckName == checkName and Side == "Source"
            target = reconciliationSides where CheckName == checkName and Side == "Target"

            FOR each metric in [
                "EntryCount",
                "TxAmt",
                "CLTotal",
                "PGTotal",
                "POQIncome",
                "ExtraTxAmt",
                "SeperateAmt"
            ]:
                validationCheckCount += 1

                IF source.count != 1 OR target.count != 1:
                    mismatches.add({
                        check: checkName,
                        key: metric,
                        expected: "one source row and one target row",
                        actual: "missing or duplicate reconciliation side"
                    })
                ELSE IF decimal38_4(source[0][metric]) != decimal38_4(target[0][metric]):
                    mismatches.add({
                        check: checkName,
                        key: metric,
                        expected: source[0][metric],
                        actual: target[0][metric]
                    })

        currentStatementName = "SQL_COUNT_PG_COLLECT_VIOLATIONS"
        currentStepErrorCode = -9180
        pgCollectViolationCount = repository.queryScalar(
            conn,
            tx,
            SQL_COUNT_PG_COLLECT_VIOLATIONS,
            { p_ymd: businessYmd }
        )
        validationCheckCount += 1

        IF pgCollectViolationCount != 0:
            mismatches.add({
                check: "PGCollectAccounting",
                key: businessYmd,
                expected: 0,
                actual: pgCollectViolationCount
            })

        IF mismatches is not empty:
            currentStatementName = "INTEGRATED_VALIDATION_GATE"
            currentStepErrorCode = -9180
            raise logical validation failure containing bounded mismatch details

        currentStatementName = "SQL_INSERT_S18_CONTROL_TOTALS"
        currentStepErrorCode = -9180  // DML 직전에 설정
        repository.execute(
            conn,
            tx,
            SQL_INSERT_S18_CONTROL_TOTALS,
            {
                p_runId: runId,
                p_checkCount: validationCheckCount,
                p_mismatchCount: 0
            }
        )

        tx.commit()
        recordStepSuccess(
            runId,
            "S18",
            LegacyReturnCode: NULL
        )

    ON FAILURE observed by the application:
        tx.rollback()

        recordStepFailure(
            runId,
            "S18",
            LegacyReturnCode: currentStepErrorCode,
            currentStatementName: currentStatementName,
            observedSqlDiagnosticsOrMismatchDetails
        )
        stop the pipeline
```

#### 선행 단계 완료 게이트

S01은 RunId 발급 이전 단계이므로 완료 게이트에 포함하지 않는다. S14~S16은 공유 트랜잭션 커밋 이후 성공 처리되어야 하므로 S18 진입 시 모두 `Succeeded`여야 한다.

```sql
-- SQL_FIND_INCOMPLETE_PRIOR_STEPS
SELECT R.StepCode,
       COALESCE
       (
           (
               SELECT TOP (1) J.StepStatus
                 FROM batch.BatchStepJournal AS J
                WHERE J.RunId = @p_runId
                  AND J.StepCode = R.StepCode
                ORDER BY J.StartedAtUtc DESC
           ),
           N'Missing'
       ) AS ActualStatus
  FROM
  (
      VALUES
          (N'S02'), (N'S03'), (N'S04'), (N'S05'),
          (N'S06'), (N'S07'), (N'S08'), (N'S09'),
          (N'S10'), (N'S11'), (N'S12'), (N'S13'),
          (N'S14'), (N'S15'), (N'S16'), (N'S17')
  ) AS R(StepCode)
 WHERE NOT EXISTS
       (
           SELECT 1
             FROM batch.BatchStepJournal AS J
            WHERE J.RunId = @p_runId
              AND J.StepCode = R.StepCode
              AND J.StepStatus = N'Succeeded'
       )
 ORDER BY R.StepCode;
```

#### S11 동결 합계와 현재 원장 대조

`batch.BatchControlTotal`에서 S11이 기록한 다음 여덟 개 이름을 정확히 한 건씩 읽는다. 누락이나 중복도 정합성 실패로 처리한다.

```sql
-- SQL_READ_S11_LEDGER_TOTALS
SELECT ControlName,
       ControlValue
  FROM batch.BatchControlTotal
 WHERE RunId = @p_runId
   AND StepCode = N'S11'
   AND ControlName IN
       (
           N'LedgerRowCount',
           N'LedgerTxAmt',
           N'LedgerCLTotal',
           N'LedgerPGTotal',
           N'LedgerPOQIncome',
           N'LedgerExtraTxAmt',
           N'LedgerSeperateAmt',
           N'LedgerForeignSettleAmt'
       )
 ORDER BY ControlName;
```

```sql
-- SQL_READ_CURRENT_LEDGER_TOTALS
SELECT CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS LedgerRowCount,
       COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0) AS LedgerTxAmt,
       COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), 0) AS LedgerCLTotal,
       COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), 0) AS LedgerPGTotal,
       COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), 0) AS LedgerPOQIncome,
       COALESCE(SUM(CAST(ISNULL(ExtraTxAmt, 0) AS DECIMAL(38,4))), 0) AS LedgerExtraTxAmt,
       COALESCE(SUM(CAST(ISNULL(SeperateAmt, 0) AS DECIMAL(38,4))), 0) AS LedgerSeperateAmt,
       COALESCE(SUM(CAST(ISNULL(ForeignSettleAmt, 0) AS DECIMAL(38,4))), 0) AS LedgerForeignSettleAmt
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd;
```

#### 원장과 요약 테이블 독립 집계

원천과 대상을 각각 독립적으로 집계해 행으로 반환하고 애플리케이션에서 같은 `CheckName`의 두 스칼라 집계를 비교한다. 집계 간 `CROSS JOIN`은 사용하지 않는다.

```sql
-- SQL_READ_SUMMARY_RECONCILIATION_SIDES
SELECT N'ByTX' AS CheckName,
       N'Source' AS Side,
       CAST(COUNT_BIG(*) AS DECIMAL(38,4)) AS EntryCount,
       COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0) AS TxAmt,
       COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), 0) AS CLTotal,
       COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), 0) AS PGTotal,
       COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), 0) AS POQIncome,
       COALESCE(SUM(CAST(ISNULL(ExtraTxAmt, 0) AS DECIMAL(38,4))), 0) AS ExtraTxAmt,
       COALESCE(SUM(CAST(ISNULL(SeperateAmt, 0) AS DECIMAL(38,4))), 0) AS SeperateAmt
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd

UNION ALL

SELECT N'ByTX',
       N'Target',
       COALESCE(SUM(CAST(ISNULL(TXCNT, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(ExtraTxAmt, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(SeperateAmt, 0) AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleByTX
 WHERE YMD = @p_ymd

UNION ALL

SELECT N'PartialCancel',
       N'Source',
       CAST(COUNT_BIG(*) AS DECIMAL(38,4)),
       COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(ExtraTxAmt, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(SeperateAmt, 0) AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
   AND USESTATE = 2

UNION ALL

SELECT N'PartialCancel',
       N'Target',
       COALESCE(SUM(CAST(ISNULL(TXCNT, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(ExtraTxAmt, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(SeperateAmt, 0) AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TPartialCancelByTX
 WHERE YMD = @p_ymd
   AND USESTATE = 2

UNION ALL

SELECT N'ByIN',
       N'Source',
       CAST(COUNT_BIG(*) AS DECIMAL(38,4)),
       COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(ExtraTxAmt, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(SeperateAmt, 0) AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
   AND INSTATE = 1

UNION ALL

SELECT N'ByIN',
       N'Target',
       COALESCE(SUM(CAST(ISNULL(INCNT, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(ExtraTxAmt, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(SeperateAmt, 0) AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleByIN
 WHERE YMD = @p_ymd

UNION ALL

SELECT N'ByOUT',
       N'Source',
       CAST(COUNT_BIG(*) AS DECIMAL(38,4)),
       COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(ExtraTxAmt, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(SeperateAmt, 0) AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE YMD = @p_ymd
   AND OUTSTATE IN (2,9)

UNION ALL

SELECT N'ByOUT',
       N'Target',
       COALESCE(SUM(CAST(ISNULL(OUTCNT, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(TXAMT, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(CLTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(PGTOTAL, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(POQINCOME, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(ExtraTxAmt, 0) AS DECIMAL(38,4))), 0),
       COALESCE(SUM(CAST(ISNULL(SeperateAmt, 0) AS DECIMAL(38,4))), 0)
  FROM SETTLE_POQ_DB.dbo.TSettleByOUT
 WHERE YMD = @p_ymd
   AND OUTSTATE IN (2,9);
```

#### PG 수금통계 회계식 검증

```sql
-- SQL_COUNT_PG_COLLECT_VIOLATIONS
SELECT COUNT_BIG(*) AS ViolationCount
  FROM SETTLE_POQ_DB.dbo.TStatPGCollect
 WHERE INYMD = @p_ymd
   AND
   (
       CAST(ISNULL(LEFTSUMAMT, 0) AS DECIMAL(38,4))
           <> CAST
              (
                  ISNULL(COLLECTAMT, 0)
                + ISNULL(PGCOMM, 0)
                + ISNULL(PGVT, 0)
                  AS DECIMAL(38,4)
              )
       OR
       CAST(ISNULL(RIGHTSUMAMT, 0) AS DECIMAL(38,4))
           <> CAST
              (
                  ISNULL(SETTLEWILLAMT, 0)
                + ISNULL(AHEADSALESCOMM, 0)
                + ISNULL(AHEADSALESVT, 0)
                + ISNULL(AHEADSETTLEAMT, 0)
                  AS DECIMAL(38,4)
              )
       OR
       CAST(ISNULL(LEFTSUMAMT, 0) AS DECIMAL(38,4))
           <> CAST(ISNULL(RIGHTSUMAMT, 0) AS DECIMAL(38,4))
   );
```

#### 검증 결과 기록

모든 검증이 통과한 경우에만 S18의 검사 수와 불일치 건수 0을 `batch.BatchControlTotal`에 삽입한다. 실패하면 이 INSERT를 실행하지 않거나, 실행 도중 실패한 경우 같은 트랜잭션 롤백으로 제거된다.

```sql
-- SQL_INSERT_S18_CONTROL_TOTALS
INSERT INTO batch.BatchControlTotal
(
    RunId,
    StepCode,
    ControlName,
    ControlValue,
    CapturedAtUtc
)
SELECT @p_runId,
       N'S18',
       V.ControlName,
       V.ControlValue,
       SYSUTCDATETIME()
  FROM
  (
      VALUES
          (N'ValidationCheckCount',    CAST(@p_checkCount AS DECIMAL(38,4))),
          (N'ValidationMismatchCount', CAST(@p_mismatchCount AS DECIMAL(38,4)))
  ) AS V(ControlName, ControlValue);
```

검증 불일치, 조회 실패 또는 결과 기록 실패 시 열린 트랜잭션을 롤백하고 `batch.BatchStepJournal.LegacyReturnCode`에 **-9180**, `ErrorMessage`에 검사명·키·기대값·실제값을 기록한다. 체크포인트는 `Pending`으로 유지되므로 재호출 시 전체 검증을 다시 수행하며, 성공한 S18은 외부 오케스트레이터가 `batch.BatchCheckpoint`를 확인하여 호출 자체를 생략한다.