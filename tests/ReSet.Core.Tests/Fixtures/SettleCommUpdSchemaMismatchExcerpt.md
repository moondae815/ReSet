### 스키마 불일치 컬럼

| 테이블명 | 원본 소스에서 사용한 컬럼명 | 제공된 스키마 존재 여부 | 사용 위치 |
|---|---|---|---|
| `dbo.TSettleMst` | `CLINTCOMM` | 존재하지 않음 | 할부이자 고객사 수수료, 취소거래 음수 전환, 총수수료, 부가가치세 포함 계산 |
| `dbo.TSettleMst` | `CLETC` | 존재하지 않음 | 할부이자 부가가치세, 취소거래 음수 전환, `inivacct`, `easybank`, 총수수료, 부가가치세 포함 계산 |
| `dbo.TSettleMst` | `PGINTEXPCOMM` | 존재하지 않음 | 할부이자 PG 예상수수료, 취소거래 음수 전환, PG 총수수료 계산 |
| `dbo.TSettleMst` | `PGINTREALCOMM` | 존재하지 않음 | 취소거래 음수 전환 및 PG 총수수료 계산 |
| `dbo.TSettleMst` | `PGETC` | 존재하지 않음 | 취소거래 음수 전환, `inivacct`, `easybank`, 총수수료 계산 |
| `dbo.TSettleMst` | `PointAmt` | 존재하지 않음 | 취소거래 음수 전환 |
| `dbo.TSettleMst` | `CardAmt` | 존재하지 않음 | 취소거래 음수 전환 |
| `dbo.TSettleMst` | `CouponAmt` | 존재하지 않음 | 취소거래 음수 전환 |
| `dbo.TSettleMst` | `MoneyAmt` | 존재하지 않음 | 취소거래 음수 전환 |
| `dbo.TSettleMst` | `PGTOTAL` | 존재하지 않음 | 총수수료 계산 및 부가가치세 포함 계산 |
| `dbo.TSettleMst` | `POQINCOME` | 존재하지 않음 | 총수수료 계산 및 부가가치세 포함 계산 |
| `dbo.TSettleMst` | `SettleCurrency` | 존재하지 않음 | 외화정산 통화 갱신 |
| `dbo.TSettleMst` | `ForeignSettleAmt` | 존재하지 않음 | 외화정산금액 갱신 |
| `dbo.TSettleMst` | `CLCOMMTYPE` | 존재하지 않음 | 주석 처리된 테스트건 수수료 0원 처리 |
| `dbo.TSettleMst` | `PGCOMMTYPE` | 존재하지 않음 | 주석 처리된 테스트건 수수료 0원 처리 |
