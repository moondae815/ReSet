#### UPDATE 대상 테이블: SETTLE_POQ_DB.dbo.TSettleMst (갱신 1 · 원본 DDL 라인 38 · 원문 표기: SETTLE_POQ_DB.dbo.TSettleMst)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| SETTLE_POQ_DB.dbo.TSettleMst | DiscountFlag | 'Y' | 프로모션 원 할인금액(B.OrgDiscountAmt)이 존재하는 원천PG외 거래에 대해 할인구분을 'Y'로 표시 |
| SETTLE_POQ_DB.dbo.TSettleMst | DiscountAmt | A.TxAmt - B.OrgDiscountAmt | 거래금액에서 프로모션 최초 할인금액을 차감한 값으로 정산상 할인금액을 재산정 |

