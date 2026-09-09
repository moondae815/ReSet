본 명세서는 스토어드 프로시저 `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC`을 분석한 결과이다. 이 프로시저는 `SETTLE_POQ_DB` 데이터베이스에 소속되며, 일별 정산내역 테이블(`SETTLE_POQ_DB.dbo.TSettleMst`)에 대해 PG사·고객사별로 상이한 예외 수수료 규칙(최저수수료, 구간별 고정수수료, 통신사별 수수료, 프로모션 할인, 부분취소 원가 재계산 등)을 순차적으로 적용하는 배치성 UPDATE 전용 프로시저이다. 총 18개의 UPDATE 문이 하나의 트랜잭션 안에서 순차 실행되며, 각 UPDATE 직후 `@@ERROR` 값을 검사하여 오류 발생 시 즉시 `ROLLBACK TRAN` 후 고유한 음수 오류 코드를 출력 파라미터에 설정하고 종료한다. 모든 UPDATE가 성공하면 `COMMIT TRAN` 후 종료한다.

프로시저는 `BEGIN TRAN`(라인 32)으로 시작하여 18개의 UPDATE 문을 순차 실행하며, 각 UPDATE 직후 `IF @@ERROR <> 0` 조건으로 오류를 검사한다. 오류가 감지되면 즉시 `ROLLBACK TRAN` 후 해당 단계 고유의 음수 오류 코드를 `@po_intRetVal`에 설정하고 `RETURN`으로 프로시저를 종료한다. 오류가 없으면 다음 UPDATE로 진행하며, 18번째 UPDATE(라인 525)까지 모두 통과하면 `COMMIT TRAN`(라인 540) 후 `RETURN`으로 종료한다.

주목할 점은 UPDATE 3과 UPDATE 4가 동일한 오류 코드 -1을 공유하고, UPDATE 5와 UPDATE 6이 동일한 오류 코드 -2를 공유한다는 것이다. 이는 두 개의 서로 다른 업무 로직 단계가 같은 실패 코드로 매핑되어 있어, 호출자가 `@po_intRetVal = -1` 또는 `-2`만으로는 정확히 어느 UPDATE 단계에서 실패했는지 구분할 수 없다는 한계를 의미한다.

### 오류 코드 (기계 확정 — 수정 금지)
| 문장 | 오류 코드 | 설정 대상 |
| :--- | :--- | :--- |
| UPDATE 1 | -101 | @po_intRetVal |
| UPDATE 2 | -102 | @po_intRetVal |
| UPDATE 3 | -1 | @po_intRetVal |
| UPDATE 4 | -1 | @po_intRetVal |
| UPDATE 5 | -2 | @po_intRetVal |
| UPDATE 6 | -2 | @po_intRetVal |
| UPDATE 7 | -3 | @po_intRetVal |
| UPDATE 8 | -4 | @po_intRetVal |
| UPDATE 9 | -5 | @po_intRetVal |
| UPDATE 10 | -10 | @po_intRetVal |
| UPDATE 11 | -11 | @po_intRetVal |
| UPDATE 12 | -19 | @po_intRetVal |
| UPDATE 13 | -20 | @po_intRetVal |
| UPDATE 14 | -201 | @po_intRetVal |
| UPDATE 15 | -21 | @po_intRetVal |
| UPDATE 16 | -27 | @po_intRetVal |
| UPDATE 17 | -28 | @po_intRetVal |
| UPDATE 18 | -29 | @po_intRetVal |
