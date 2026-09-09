> UPDATE 17(라인 452)의 파생 테이블 X는 위 표의 마지막 4개 행(PGINCVTAX=B.incVTax, PGCOMM/PGCOMM4SUM의 point/card·coupon·money·point 결합식, PGETC4SUM=B.ETCAmt+(B.ETCAmt/10.0))으로 정의되며, `UNION ALL`로 결합된 두 SELECT(첫 번째는 A.PGName='pointpay' 대상, 두 번째는 A.PGName='payco' 대상)의 각 분기 산식입니다. 즉 UPDATE 17의 PGComm/PGVT 산출은 pointpay 분기(포인트결제 기준 수수료)와 payco 분기(카드·쿠폰·머니·포인트 4종 결제수단별 수수료 합산)라는 서로 다른 두 원천의 집계식을 `UNION ALL`로 합쳐 하나의 파생 결과 집합을 구성한 뒤 TPGProperty(Y)와 결합하는 구조이며, 단일 테이블로 단순화해서는 안 됩니다.

위 표에서 보듯 UPDATE 3과 UPDATE 4가 -1을 공유하고, UPDATE 5와 UPDATE 6이 -2를 공유합니다. 따라서 모든 문장이 서로 다른 고유 오류 코드를 갖는 것이 아니라, 문장별로 지정된 음수 코드(중복 포함)가 설정되는 구조입니다. 호출자는 `@po_intRetVal = -1` 또는 `-2`만으로는 정확히 어느 UPDATE 문에서 실패했는지 구분할 수 없습니다.

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
