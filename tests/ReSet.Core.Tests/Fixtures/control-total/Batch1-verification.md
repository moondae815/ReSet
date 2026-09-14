## 통합 데이터 정합성 검증 SQL 세트

### 4.1 레이트 스냅샷 정합성 검증

S02가 적재한 레이트 스냅샷이 이후 단계 실행 중 변경되지 않았는지 확인한다. 스냅샷 캡처 시점의 행 수와 검증 시점의 행 수를 각각 별도로 집계하여 비교한다.

```sql
-- 스냅샷 이후 TPGSettleRate 행 수 변동 여부 확인
WITH Captured AS (
    SELECT COUNT(*) AS CapturedCnt
    FROM batch.BatchControlTotal
    WHERE RunId = @p_runId AND StepCode = N'S02' AND ControlName = N'TPGSettleRate_RowCount'
),
Current AS (
    SELECT COUNT(*) AS CurrentCnt
    FROM SETTLE_POQ_DB.dbo.TPGSettleRate
    WHERE YMD = @p_batchYmd
)
SELECT c.CapturedCnt, cur.CurrentCnt
FROM Captured c CROSS JOIN Current cur
WHERE c.CapturedCnt <> cur.CurrentCnt;
```

### 4.2 `TSettleMst` 단계 간 정합성 검증

**4.2.1 원장 적재 단계(S03~S06) 건수/금액 대사**

```sql
-- 각 단계가 기록한 통제 합계와 현재 TSettleMst 집계를 독립적으로 산출 후 비교
WITH Expected AS (
    SELECT SUM(ControlValue) AS ExpectedTxAmt
    FROM batch.BatchControlTotal
    WHERE RunId = @p_runId AND StepCode IN (N'S03', N'S04', N'S05', N'S06')
      AND ControlName = N'TSettleMst_TXAMT_Sum'
),
Actual AS (
    SELECT SUM(ISNULL(TXAMT,0)) AS ActualTxAmt
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_batchYmd
)
SELECT e.ExpectedTxAmt, a.ActualTxAmt
FROM Expected e, Actual a
WHERE ISNULL(e.ExpectedTxAmt,0) <> ISNULL(a.ActualTxAmt,0);
```

**4.2.2 커미션/예외 보정 단계(S07~S09) 전후 스냅샷 비교**

```sql
-- S07 완료 시점 통제합계와 S08 시작 시점 원본 스냅샷을 독립 집계로 비교
WITH BeforeExc AS (
    SELECT SUM(ControlValue) AS SumBefore
    FROM batch.BatchControlTotal
    WHERE RunId = @p_runId AND StepCode = N'S07' AND ControlName = N'TSettleMst_CLCOMM_Sum'
),
AfterComm AS (
    SELECT SUM(ISNULL(CLCOMM,0)) AS SumAfter
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_batchYmd
)
SELECT b.SumBefore, a.SumAfter
FROM BeforeExc b, AfterComm a
WHERE b.SumBefore <> a.SumAfter;
```

**4.2.3 SET절 동시평가 재현 여부 검증(샘플 PLTID 재계산 대사)**

```sql
-- 샘플 PLTID에 대해 저장된 결과와 애플리케이션이 별도로 재계산한 기대값을 독립적으로 조회 후 비교
WITH Stored AS (
    SELECT PLTID, CLCOMM, CLVT
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_batchYmd AND PLTID = @p_samplePltid
),
Recomputed AS (
    SELECT PLTID, CLCOMM AS RecomputedCLCOMM, CLVT AS RecomputedCLVT
    FROM batch.BatchValidationIssue
    WHERE RunId = @p_runId AND IssueCode = N'RECOMPUTE_SAMPLE' AND ExpectedValue = @p_samplePltid
)
SELECT s.PLTID, s.CLCOMM, r.RecomputedCLCOMM, s.CLVT, r.RecomputedCLVT
FROM Stored s
JOIN Recomputed r ON s.PLTID = r.PLTID
WHERE s.CLCOMM <> r.RecomputedCLCOMM OR s.CLVT <> r.RecomputedCLVT;
```

### 4.3 섀도우 테이블 기반 Diff 검증 세트

```sql
-- 섀도우 테이블(캡처 당시)과 현재 운영 테이블 행 수를 각각 독립 집계 후 비교
DECLARE @v_shadow NVARCHAR(300) =
    N'batch_shadow.<Table>_' + CAST(@p_runId AS NVARCHAR(20)) + N'_<StepCode>';
DECLARE @v_sql NVARCHAR(MAX);
SET @v_sql = N'
WITH ShadowCnt AS (
    SELECT COUNT(*) AS Cnt FROM ' + @v_shadow + N'
),
LiveCnt AS (
    SELECT COUNT(*) AS Cnt FROM <SchemaTable> WHERE <RangeColumn> = @p_batchYmd
)
SELECT s.Cnt AS ShadowCnt, l.Cnt AS LiveCnt
FROM ShadowCnt s, LiveCnt l
WHERE s.Cnt <> l.Cnt;';
EXEC sp_executesql @v_sql, N'@p_batchYmd CHAR(8)', @p_batchYmd = @p_batchYmd;
```

### 4.4 집계/통계 단계 대사 쿼리(S11~S15)

```sql
-- TSettleMst 합계와 TSettleByTX 합계를 각각 독립 집계 후 비교
WITH LedgerSum AS (
    SELECT SUM(ISNULL(TXAMT,0)) AS TxAmtSum
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE YMD = @p_batchYmd
),
SummarySum AS (
    SELECT SUM(ISNULL(TXAMT,0)) AS TxAmtSum
    FROM SETTLE_POQ_DB.dbo.TSettleByTX
    WHERE YMD = @p_batchYmd
)
SELECT l.TxAmtSum AS LedgerTxAmt, s.TxAmtSum AS SummaryTxAmt
FROM LedgerSum l, SummarySum s
WHERE l.TxAmtSum <> s.TxAmtSum;
```

```sql
-- TSettleMst 회수완료 합계와 TStatPGCollect 회수금액 합계를 각각 독립 집계 후 비교
WITH LedgerCollect AS (
    SELECT SUM(TXAMT-PGTOTAL) AS CollectSum
    FROM SETTLE_POQ_DB.dbo.TSettleMst
    WHERE INYMD = @p_batchYmd AND INSTATE = 1
),
StatCollect AS (
    SELECT SUM(ISNULL(COLLECTAMT,0)) AS CollectSum
    FROM SETTLE_POQ_DB.dbo.TStatPGCollect
    WHERE INYMD = @p_batchYmd
)
SELECT l.CollectSum AS LedgerCollectSum, s.CollectSum AS StatCollectSum
FROM LedgerCollect l, StatCollect s
WHERE ISNULL(l.CollectSum,0) <> ISNULL(s.CollectSum,0);
```

### 4.5 `TSettleMiss` 사후정산 재무 대사(S09 전용)

원본 `UP_UTIL_SETTLE_PROC_ETC`의 SELECT 5/SELECT 6 금액 체크 로직을 재현한다.

```sql
-- TSettleMst 후취정산 대상 합계(SELECT 5 재현)와 TSettleMiss 후취정산 합계(SELECT 6 재현)를 독립 집계 후 비교
WITH MstSum AS (
    SELECT SUM(A.CLTotal) AS PostChkAmt1
    FROM SETTLE_POQ_DB.dbo.TSettleMst A
    JOIN SETTLE_POQ_DB.dbo.TClientSettleRate B
      ON A.YMD = B.YMD AND A.ClientID = B.ClientID AND A.PGName = B.PGName AND A.MallID = B.MallID
    JOIN SETTLE_POQ_DB.dbo.TClient C ON A.ClientID = C.ClientID
    WHERE ISNULL(B.TaxFGBill,2) = 1
      AND A.ClientID = @p_clientId
      AND A.OutYMD = @p_outYmd
      AND A.OutState = 2
),
MissSum AS (
    SELECT SUM(CLSettleAmt) AS PostChkAmt2
    FROM SETTLE_POQ_DB.dbo.TSettleMiss
    WHERE ClientID = @p_clientId
      AND OutYMD = @p_outYmd
      AND OutState = 2
      AND ISNULL(IssueType,0) = 15
)
SELECT m.PostChkAmt1, s.PostChkAmt2
FROM MstSum m, MissSum s
WHERE ISNULL(m.PostChkAmt1,0) <> ISNULL(s.PostChkAmt2,0);
```

### 4.6 실행 저널·체크포인트·락 무결성 검증

```sql
-- 체크포인트가 Succeeded인데 저널이 Failed인 모순 탐지 (재시작 시 중복/누락 방지)
SELECT cp.StepCode, cp.CheckpointStatus, j.StepStatus
FROM batch.BatchCheckpoint cp
JOIN batch.BatchStepJournal j
  ON cp.RunId = j.RunId AND cp.StepCode = j.StepCode
WHERE cp.RunId = @p_runId
  AND cp.CheckpointStatus = N'Succeeded'
  AND j.StepStatus <> N'Succeeded';
```

```sql
-- 배치 락이 Held 상태로 남아 있는데 BatchRun은 이미 종료된 경우 탐지
WITH RunEnded AS (
    SELECT RunId FROM batch.BatchRun
    WHERE RunId = @p_runId AND RunStatus IN (N'Succeeded', N'Failed')
),
LockHeld AS (
    SELECT OwnerRunId FROM batch.BatchRunLock
    WHERE OwnerRunId = @p_runId AND LockStatus = N'Held'
)
SELECT r.RunId, l.OwnerRunId
FROM RunEnded r
JOIN LockHeld l ON r.RunId = l.OwnerRunId;
```

### 4.7 최종 게시 전 종합 체크리스트 쿼리

```sql
-- 전체 18개 단계 완료 플래그, 미해결 오류, 락 해제 상태를 각각 독립 집계 후 종합
WITH StepCompletion AS (
    SELECT COUNT(*) AS SucceededCnt
    FROM batch.BatchCheckpoint
    WHERE RunId = @p_runId AND CheckpointStatus = N'Succeeded'
),
UnresolvedIssues AS (
    SELECT COUNT(*) AS CriticalCnt
    FROM batch.BatchValidationIssue
    WHERE RunId = @p_runId AND Severity = N'Critical'
),
LockState AS (
    SELECT COUNT(*) AS HeldCnt
    FROM batch.BatchRunLock
    WHERE OwnerRunId = @p_runId AND LockStatus = N'Held'
)
SELECT sc.SucceededCnt, ui.CriticalCnt, ls.HeldCnt
FROM StepCompletion sc, UnresolvedIssues ui, LockState ls
WHERE sc.SucceededCnt < 18 OR ui.CriticalCnt > 0;
```