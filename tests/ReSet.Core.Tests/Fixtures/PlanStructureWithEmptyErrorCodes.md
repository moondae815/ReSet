## 통합 배치 아키텍처 개요

### 문서 목적 및 적용 범위

본 문서는 `POQSettleProcDaily6`를 C# 기반의 **Tasklet 중심 일별 정산 배치 애플리케이션**으로 현대화하기 위한 최종 설계 문서의 목차와 실행 구조를 정의한다.

이 픽스처는 실측 `POQSettleProcDaily6`의 목차(14단계)를 4단계로 축약한 것이다 - 결함의 세 형태(출신 없는 단계, 선언이 있는 단계, 선언이 0개인데 명세서에는 있는 단계)를 최소로 재현한다.

### 최종 문서 목차

### 1. 아키텍처와 운영 제어

#### 1.6 단계별 실행 순서

| 순서 | 단계 | 목적 | 원장 영향 |
|---:|---|---|---|
| 0 | S00 | 실행 잠금, 파라미터·스키마·지급보호 사전검증 | 제어 테이블 |
| 1 | S01 | PG·고객사 수수료율 스냅샷 생성 | 수수료율 스냅샷 |
| 6 | S06 | 기본 수수료 계산 | `TSettleMst` |
| 8 | S08 | 예외 반영 후 총수수료·순이익·파생값 확정 | `TSettleMst` |

#### 1.5 레거시 반환 코드 보존 정책

레거시 프로시저의 반환 코드는 C# 예외 코드로 임의 변환하지 않는다.

- 원본 SQL 프로시저의 `RETURN` 코드와 C# 실행 결과를 함께 저장한다.
- 공급된 분석 자료에서 명시적으로 확인된 반환 코드는 `-9`뿐이다.
- 최종 구현 착수 전, 각 원본 프로시저 정의에서 모든 `RETURN` 문을 추출하여 아래 단계 매니페스트의 빈 `ErrorCodes` 항목을 확정해야 한다.

## 단계별 이행 상세 및 의사코드

### S00 — 실행 잠금 및 사전검증

#### 목적

- `RunYmd` 형식과 실행 가능 날짜를 확인한다.
- 동일 정산일의 중복 실행을 차단한다.

#### 의사코드

```text
BEGIN
  ValidateRunYmd(RunYmd)
  AcquireApplicationLock("POQSettleProcDaily6:" + RunYmd)
  CreateBatchExecution(RunYmd)
COMMIT
```

### S01 — 수수료율 스냅샷 생성

#### 레거시 대응 프로시저

- `UP_Util_PG_Client_CMRate_Ins`

#### 목적

정산일 기준의 PG·고객사 수수료율을 스냅샷 테이블에 생성한다.

#### 대상 테이블

- `dbo.TPGSettleRate`

#### 의사코드

```text
BEGIN TRANSACTION
  DeleteRateSnapshotsForRunYmd(RunYmd)
  InsertPgSettlementRates(RunYmd)
COMMIT
```

### S06 — 기본 수수료 계산

#### 레거시 대응 프로시저

- `UP_UTIL_SETTLE_COMM_UPD`

#### 목적

일반 정산 원장에 대해 기본 고객사 수수료, PG 원가 수수료, 해외카드 수수료, 취소 수수료, 할부 수수료, 분할정산 및 외화정산의 기초 값을 계산한다.

#### 대상 테이블

- `dbo.TSettleMst`

#### 의사코드

```text
BEGIN TRANSACTION
  SelectEligibleLedgerRowsExcludingProtectedAndExtraRows(RunYmd)
  UpdateBaseClientCommission()
  UpdateBasePgCommission()
COMMIT
```

### S08 — 총수수료·순이익·파생값 확정

#### 목적

S06 기본 수수료 계산 이후, 총수수료와 순이익 등 파생 금액을 재계산하여 `TSettleMst`의 금액 상태를 확정한다.

#### 대상 테이블

- `dbo.TSettleMst`

#### 의사코드

```text
BEGIN TRANSACTION
  RecalculateClientCommissionTotals()
  RecalculatePgCommissionTotals()
COMMIT
```

### 기계 판독용 단계 목록

```json
{
  "Steps": [
    {
      "Code": "S00",
      "Name": "실행 잠금 사전검증",
      "LegacyProcedures": [],
      "TargetTables": [
        "dbo.POQSettleBatchExecution"
      ],
      "ErrorCodes": [],
      "Chunkable": false
    },
    {
      "Code": "S01",
      "Name": "수수료율 스냅샷",
      "LegacyProcedures": [
        "UP_Util_PG_Client_CMRate_Ins"
      ],
      "TargetTables": [
        "dbo.TPGSettleRate"
      ],
      "ErrorCodes": [
        "-9"
      ],
      "Chunkable": false
    },
    {
      "Code": "S06",
      "Name": "기본 수수료 계산",
      "LegacyProcedures": [
        "UP_UTIL_SETTLE_COMM_UPD"
      ],
      "TargetTables": [
        "dbo.TSettleMst"
      ],
      "ErrorCodes": [],
      "Chunkable": false
    },
    {
      "Code": "S08",
      "Name": "수수료 총액 확정",
      "LegacyProcedures": [],
      "TargetTables": [
        "dbo.TSettleMst"
      ],
      "ErrorCodes": [],
      "Chunkable": false
    }
  ]
}
```

## 통합 데이터 정합성 검증 SQL 세트

### 검증 실행 원칙

- 아래 SQL은 마지막 단계 완료 후 실행한다.
- 검증 실패 시 배치 상태를 `Failed` 또는 `ManualAction`으로 전환한다.
