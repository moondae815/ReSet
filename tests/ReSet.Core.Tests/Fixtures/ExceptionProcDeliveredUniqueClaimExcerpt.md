>   3. [정확성 - 기계 확정 표와 모순] 개요의 '각기 다른 음수 코드를 @po_intRetVal에 설정' 및 로직 흐름 요약의 '고유 음수값 설정' 표현은 `### 오류 코드 (기계 확정 — 수정 금지)` 표와 모순됩니다. 실제로는 UPDATE 3과 UPDATE 4가 -1을 공유하고, UPDATE 5와 UPDATE 6이 -2를 공유하므로 모든 문장의 오류 코드가 서로 다르지 않습니다. '문장별로 지정된 음수 코드(중복 포함)'로 수정하십시오.

| @po_intRetVal | INT | OUTPUT | (DDL 미명시) | 처리 결과 코드. 오류 발생 시 문장별로 고유한 음수값이 대입되며, 정상 종료 시에는 본문에서 값을 대입하지 않습니다(호출 책임: 호출자가 사전 초기화 또는 반환 후 별도 판단 필요). |

프로시저는 `BEGIN TRAN`으로 시작하여 18개의 UPDATE 문을 순차 실행합니다. 각 UPDATE 직후 `IF @@ERROR <> 0` 검사를 수행하며, 오류 발생 시 `ROLLBACK TRAN` → `@po_intRetVal`에 고유 음수값 설정 → `RETURN`으로 즉시 종료합니다. 18개 UPDATE 모두 정상 처리되면 `COMMIT TRAN` 후 `RETURN`으로 종료합니다(이때 `@po_intRetVal`은 별도로 설정되지 않음).

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
