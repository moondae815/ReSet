`### 오류 코드` 표에서 보듯 문장별 오류 코드는 서로 다른 고유값이 아니라 일부 중복됩니다: UPDATE 3과 UPDATE 4가 -1을 공유하고, UPDATE 5와 UPDATE 6이 -2를 공유합니다. 따라서 `@po_intRetVal = -1` 또는 `-2`만으로는 어느 UPDATE 문에서 실패했는지 단독으로 특정할 수 없습니다.

3. 이후 18개의 `UPDATE` 문이 순차적으로 실행되며, 각 문장 직후 `IF @@ERROR <> 0` 검사를 통해 오류 발생 시 `ROLLBACK TRAN` → `@po_intRetVal`에 해당 문장 고유(단, 일부 중복 포함) 음수 코드 대입 → `RETURN`으로 즉시 프로시저를 종료합니다.

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
