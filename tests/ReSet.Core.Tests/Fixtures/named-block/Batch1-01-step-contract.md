## 단계별 이행 상세 및 의사코드

### 공통 SQL 오류 추적 패턴

모든 단계는 단계-로컬 상태 변수(`currentStepErrorCode`)를 유지한다. 이 변수는 각 DML 문장 실행 직전에 그 문장이 원본 절차에서 가졌던 오류 코드로 갱신되며(레거시 절차를 대체하는 단계는 규칙 9에 따라 원본 코드를 그대로 사용하고, 레거시 기원이 없는 제어 단계(S01, S16~S18)는 규칙 6-2의 예약 블록만 사용한다), 단계가 실패하면 그 시점의 값이 `batch.BatchStepJournal.LegacyReturnCode`에 기록된다. 상태 변수는 `NULL`로 시작하며(아직 어떤 실패 지점에도 도달하지 않았음을 의미), 실제 코드값으로 초기화하지 않는다. 한 문장이 실패하면 그 단계는 자신의 작업 단위 전체를 완료하거나, 대상을 전혀 건드리지 않은 상태로 남긴다 — 부분 커밋을 남기지 않는다.

```pseudocode
// (rule 6-1) 단계 진입 시 초기화. 숫자 리터럴이 아닌 NULL로 시작한다.
currentStepErrorCode = NULL

runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: "POQSettleBatch1", p_ymd: batchYmd })

writeStepJournal(runId, StepCode, status: "Running", legacyReturnCode: NULL)

beginTransaction()
try:
    // 문장 실행 직전에 코드를 갱신한다 - 실패 시점에 이미 이 문장을 가리키고 있어야 한다.
    currentStepErrorCode = <이 문장의 원본 오류 코드 또는 예약 블록 코드>
    execute(SQL_STATEMENT_N, { ... })
    ...
    commit()
    writeStepJournal(runId, StepCode, status: "Succeeded", legacyReturnCode: 0)
    writeCheckpoint(runId, StepCode, status: "Succeeded")
catch failure:
    rollbackIfOpen()
    writeStepJournal(runId, StepCode, status: "Failed", legacyReturnCode: currentStepErrorCode)
    stop the pipeline
```

```sql
-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';
```

### Shadow Table 및 복구 정책

기본 원칙은 단일 트랜잭션 롤백이다: 단계의 작업이 하나의 트랜잭션 안에서 끝난다면(청크로 나뉘지 않고 집계를 재구축하지도 않는다면), 실패 시 그 트랜잭션의 롤백만으로 대상 테이블은 완전히 복원되며, 이 경우 섀도우 테이블도 보정용 DELETE도 만들지 않는다 — 이미 복원된 행을 다시 지우는 것은 데이터 손상이다.

청크 단위로 커밋되거나(S02~S06) 집계를 재구축하는(S11~S15의 GROUP BY 대상 테이블) 단계만 섀도우를 사용한다. 이 경우 다음이 모두 지켜져야 한다.

- **캡처 시점**: 섀도우는 롤백 가능한 트랜잭션이 열리기 전에, 그 트랜잭션 밖에서 생성·채움을 완료한다.
- **복구 범위**: 복구 시 DELETE는 그 단계가 원래 삭제했던 것과 정확히 동일한 범위(`WHERE` 조건)로 수행한다. `WHERE` 없는 전체 삭제는 금지한다.
- **파라미터 바인딩**: 섀도우 캡처/복구 문장에 값을 텍스트로 붙여 넣지 않고 전부 파라미터로 전달한다. 조립이 허용되는 것은 테이블 이름 문자열뿐이다.
- **다중 대상 테이블**: 단계가 여러 테이블을 갱신한다면 섀도우 전략은 그 테이블 전부를 포괄해야 한다.
- **보존 기간**: 섀도우 테이블은 캡처 후 24시간 뒤 부트스트랩 정리 작업에 의해 자동 삭제된다.

```pseudocode
// 청크 커밋 단계에서만 사용. 단일 트랜잭션 단계는 이 블록을 사용하지 않는다.
execute(SQL_CREATE_AND_CAPTURE_SHADOW, { p_runId: runId, p_batchYmd: batchYmd })
shadowCaptured = true

beginTransaction()
execute(SQL_DELETE_RANGE, { p_batchYmd: batchYmd })
commit()

FOR EACH chunk IN chunkRanges(SQL_CHUNK_BOUNDS, { p_batchYmd: batchYmd }, size: 10000):
    beginTransaction()
    currentStepErrorCode = <해당 INSERT의 원본 오류 코드>
    execute(SQL_INSERT_CHUNK, { p_batchYmd: batchYmd, p_from: chunk.from, p_to: chunk.to })
    commit()
```

```sql
-- SQL_CREATE_AND_CAPTURE_SHADOW
DECLARE @v_shadow NVARCHAR(300) =
    N'batch_shadow.<Table>_' + CAST(@p_runId AS NVARCHAR(20)) + N'_<StepCode>';
DECLARE @v_sql NVARCHAR(MAX);
SET @v_sql = N'SELECT * INTO ' + @v_shadow + N' FROM <SchemaTable> WHERE 1 = 0;';
EXEC sp_executesql @v_sql;
SET @v_sql = N'INSERT INTO ' + @v_shadow + N' SELECT * FROM <SchemaTable> WHERE <RangeColumn> = @p_batchYmd;';
EXEC sp_executesql @v_sql, N'@p_batchYmd CHAR(8)', @p_batchYmd = @p_batchYmd;
```

### 청크 페이징 정책

청크 가능 단계(S02~S06)는 다음 템플릿을 따른다. 청크 경계는 원본 업무 필터(예: `USESTATE`, `PGName IN (...)`)와 청크 범위 조건을 `AND`로 결합하여 산출하며, 원본 필터를 제거하지 않는다. 각 청크 반복은 자신만의 트랜잭션 경계를 열고 닫아, 중간 실패가 이전 청크의 커밋을 훼손하지 않도록 한다. DELETE-INSERT 패턴을 청크로 나눌 때는 청크 키를 DELETE의 WHERE 절에도 반드시 포함시켜, 전체 테이블 삭제로 번지는 것을 막는다. 청크 키는 대상 스키마에 실제로 존재하는 컬럼(또는 컬럼 조합)이어야 하며, 존재하지 않는 키를 임의로 만들어내지 않는다.

```pseudocode
FOR EACH chunk IN chunkRanges(SQL_CHUNK_BOUNDS, { p_batchYmd: batchYmd }, size: 10000):
    beginTransaction()
    currentStepErrorCode = <이 INSERT/DELETE 문장의 원본 오류 코드>
    execute(SQL_DELETE_CHUNK, { p_batchYmd: batchYmd, p_from: chunk.from, p_to: chunk.to })
    execute(SQL_INSERT_CHUNK, { p_batchYmd: batchYmd, p_from: chunk.from, p_to: chunk.to })
    commit()
```

```sql
-- SQL_CHUNK_BOUNDS - 원본 업무 필터를 경계 조회에도 반드시 포함한다
SELECT MIN(<ChunkKey>), MAX(<ChunkKey>) FROM <SourceTable>
 WHERE <원본 필터 조건>;

-- SQL_DELETE_CHUNK - 청크 키를 DELETE WHERE에 결합
DELETE FROM <TargetTable>
 WHERE <원본 필터 조건>
   AND <ChunkKey> >= @p_from AND <ChunkKey> < @p_to;

-- SQL_INSERT_CHUNK
INSERT INTO <TargetTable> (...)
SELECT ... FROM <SourceTable>
 WHERE <원본 필터 조건>
   AND <ChunkKey> >= @p_from AND <ChunkKey> < @p_to;
```

GROUP BY 집계 또는 다중 소스 크로스 DB 조인으로 인해 단일 키 청크가 수학적으로 불가능한 단계(S07~S15)는 'Single-Transaction Shadow Swap'(또는 트랜잭션 단독 롤백, 섀도우 불필요 여부는 위 정책에 따름)을 사용하며, 이 경우 가짜 청크 키를 만들어 붙이지 않는다.