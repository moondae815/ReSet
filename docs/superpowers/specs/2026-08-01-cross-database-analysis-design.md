# 크로스 데이터베이스 의존성 분석 활성화 설계

- 작성일: 2026-08-01
- 상태: 설계 승인됨 (구현 계획 수립 전)

## 배경

`SETTLE_POQ_DB.dbo.UP_UTIL_SETTLE_EXCEPTION_PROC`를 분석하면 참조 코드 객체 섹션에 다음 5개 UDF가 링크 없이 남는다.

```
dbo.UF_GET_COMM4CLIENT — 분석 생략(외부 객체): 외부 데이터베이스 연결이 허용되지 않았습니다.
dbo.UF_GET_COMM4CLIENT4INTEREST — ...
dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL — ...
dbo.UF_GET_COMM4PG — ...
dbo.UF_GET_COMM4PG4INTEREST — ...
```

이 객체들은 프로시저 본문에서 `SETTLE_CARD_DB.dbo.UF_GET_COMM4CLIENT(...)` 형태의 3-part 이름으로 호출된다. 분석 기준 DB가 `SETTLE_POQ_DB`이므로 `DependencyAnalysisOrchestrator.cs:147`의 조건에 걸려 재귀 분석 진입이 차단된다.

### 현행 코드의 실제 상태

조사 결과 세 가지 사실이 확인되었다.

1. **별도 커넥션이 필요하지 않다.** `DbMetadataService`의 조회 헬퍼(`GetObjectDdlAsync`, `GetCodeObjectTypeCodeAsync`, `GetFunctionReturnInfoAsync`)는 모두 `database` 파라미터를 받아 `[DB].sys.sql_modules` 형태의 3-part 쿼리를 구성한다(`DbMetadataService.cs:64`). 같은 인스턴스이고 로그인에 권한만 있으면 기존 커넥션으로 읽힌다. 실제로 `prompt-context.md`에는 이미 `SETTLE_CARD_DB` UDF들의 전체 DDL이 수집되어 있다. 차단된 것은 연결이 아니라 재귀 분석 진입이다.

2. **출력 레이아웃은 이미 구현되어 있다.** `OutputPathResolver.ResolveObjectDirectory`는 현재 DB가 아닌 객체를 `External/<DB>/<타입>/...` 아래로 보낸다(`OutputPathResolver.cs:82`). `dependency-manifest.json`에 기록된 미사용 `External/` 경로들이 이 로직의 산물이다.

3. **플래그를 켤 수단이 없다.** `DependencyAnalysisRequest.AllowExternalDatabaseConnections`는 정의되어 있으나(`IDependencyAnalysisOrchestrator.cs:18`), `Program.cs:1443`의 요청 생성 블록에 해당 속성이 누락되어 항상 `bool` 기본값 `false`다. `appsettings.json`에도 대응 키가 없다. 현재 이 값을 `true`로 두는 곳은 테스트 코드뿐이다.

## 목표와 범위

### 목표

외부 DB 코드 객체를 **완전 분석**한다. `output/External/<DB>/Functions/...` 아래에 Spec.md를 생성하고, 참조 섹션에서 상대 경로로 링크한다.

### 범위 밖

- **링크드 서버 및 타 인스턴스.** 3-part 조회로 접근 가능한 같은 인스턴스 내 DB만 대상으로 한다. 별도 커넥션 문자열 관리는 도입하지 않는다.
- **허용 목록(allowlist).** 대상 DB를 열거하는 방식 대신 단순 on/off 스위치를 쓴다. 분석 범위는 기존 `MaxDependencyDepth`로만 제한된다.
- **접근 실패의 생략 처리 강등.** 권한 부족이나 객체 부재는 기존 실패 경로를 그대로 타고 노출한다.

## 결정 사항

| 항목 | 결정 | 근거 |
|---|---|---|
| 분석 수준 | 외부 객체도 Spec.md까지 완전 생성 | 참조 문서의 완결성 |
| 대상 통제 | 단순 on/off (`AllowExternalDatabaseConnections`) | 기존 조건문을 그대로 사용, 구현 최소 |
| 설정 위치 | `appsettings.json`의 `DatabaseSettings` | `MaxDependencyDepth`와 동일한 패턴 (CLI에 `Option<bool>` 계열 스위치가 전무) |
| 기본값 | `false` | 기존 산출물의 동작 불변 보장 |
| 접근 실패 | `Failed`로 노출 | 권한 문제를 조기에 드러냄 |
| 경로 계산 | 분석 기준 DB를 파이프라인까지 전파 | 암묵적 가정 없이 두 경로 계산을 정의상 일치시킴 |

## 설계

### 1. 설정과 진입 경로

`appsettings.json`에 키를 추가한다.

```jsonc
"DatabaseSettings": {
  "Server": "localhost",
  "Database": "Northwind",
  "MaxDependencyDepth": 3,
  "AllowExternalDatabaseConnections": false,  // 같은 인스턴스 내 타 DB 객체까지 재귀 분석
  "OfflineSnapshotPath": ""
}
```

변경 지점은 세 곳이다.

- `Program.cs`의 설정 파싱 구간(`maxDepth`를 읽는 137행 인근)에서 값을 읽는다.
- `RunConfiguredAnalysisAsync`(`Program.cs:1401`)에 `bool allowExternalDatabaseConnections` 파라미터를 추가한다.
- `Program.cs:1443`의 요청 객체에 속성을 채운다.

`DependencyAnalysisRequest.AllowExternalDatabaseConnections`와 이를 검사하는 `DependencyAnalysisOrchestrator.cs:147`은 이미 존재하므로 수정하지 않는다.

### 2. 경로 계산 단일화

#### 문제

`OutputPathResolver`는 생성자 인자를 "현재 DB"로 간주하고, 그와 다른 DB의 객체만 `External/` 아래로 보낸다. 그런데 두 호출부가 서로 다른 값을 넣는다.

| 위치 | 생성자 인자 | 외부 UDF의 Spec 경로 |
|---|---|---|
| `DependencyAnalysisOrchestrator.cs:362` (최종 저장) | `rootKey.Database` = `SETTLE_POQ_DB` | `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4PG/docs/Spec.md` |
| `VerificationPipelineOrchestrator.cs:227` (캐시 판정) | `cacheObjectKey.Database` = `SETTLE_CARD_DB` | `output/Functions/dbo.UF_GET_COMM4PG/docs/Spec.md` |

현재는 외부 객체가 파이프라인에 진입하지 못해 두 값이 항상 같으므로 문제가 드러나지 않는다. 플래그를 켜면 두 가지가 발생한다.

- **캐시가 영구히 무효화된다.** `IsCacheValid`는 `output/Functions/...`에서 Spec을 읽어 해시를 대조하는데(`CacheManager.cs:80`), 실제 파일은 `output/External/...`에 기록되므로 대조가 성립하지 않는다. 외부 객체는 매 실행마다 LLM을 다시 태운다.
- **이름 충돌 위험이 생긴다.** `SETTLE_CARD_DB.dbo.UF_GET_COMM4PG`가 `output/Functions/dbo.UF_GET_COMM4PG/`를 차지하므로, 같은 이름의 로컬 함수가 존재하면 서로의 산출물을 덮어쓴다. `External/` 계층이 막으려던 상황이 우회된다.

#### 해결

`DependencyAnalysisRequest`에 `AnalysisDatabase` 속성을 추가하고, `AnalyzeAsync`를 이 값의 유일한 출처로 삼는다.

```csharp
var effectiveRequest = request with { AnalysisDatabase = rootKey.Database };
```

이를 위해 `DependencyAnalysisRequest`를 `sealed class`에서 `sealed record`로 전환한다. 같은 파일 5행의 `DependencyAnalysisProgress`가 이미 record이며, 기존 object-initializer 호출부는 문법 변경 없이 컴파일된다. `with` 없이 12개 속성을 수동 복사하면 이후 속성이 추가될 때 조용히 누락되므로 record 전환이 더 안전하다. record 전환으로 값 동등성 의미가 생기지만 현재 이 타입을 비교하는 코드는 없다.

`RunCodeObjectPipelineAsync`와 `RunCodeObjectPipelineCoreAsync`에 `string? analysisDatabase = null` 파라미터를 추가하고 `VerificationPipelineOrchestrator.cs:227`을 바꾼다.

```csharp
outputPaths = new OutputPathResolver(
    analysisDatabase ?? cacheObjectKey.Database,   // null이면 기존 동작 유지
    outputDirectory);
```

`null` 폴백은 `RunCodeObjectPipelineAsync`를 단독 호출하는 경로(`VerificationPipelineOrchestrator.cs:91`)와 기존 테스트 2건을 건드리지 않기 위한 것이다. 의존성 분석 경로에서만 `DependencyAnalysisOrchestrator.cs:18`의 델리게이트가 `request.AnalysisDatabase`를 전달하며, 그 결과 캐시 판정 경로와 최종 저장 경로가 정의상 동일한 값을 사용한다.

### 3. 정적 파서 호환성 수준

`GetDatabaseCompatibilityLevelAsync`는 `WHERE name = DB_NAME()`으로 커넥션의 기본 DB만 조회한다(`DbMetadataService.cs:180`). 외부 DB 객체를 파싱할 때 잘못된 호환성 수준이 `SqlStaticParser`에 전달되면 구문 해석이 어긋날 수 있다.

대상 DB를 파라미터로 받도록 바꾸고, `null`이면 기존처럼 `DB_NAME()`을 사용한다. 호출부는 `DbMetadataService.cs:474`와 `:562` 두 곳이다. 조회 실패 시 160으로 폴백하는 기존 soft-fail 동작은 유지한다.

### 4. 오류 처리

강등 로직을 만들지 않는다. 외부 DB 접근이 실패하면 기존 `MarkFailed(node, exception, "메타데이터 수집")` 경로를 타고, Spec 참조 섹션에 `분석 불가: {예외 메시지}`로 기록된다. 이 절은 추가 코드가 아니라 기존 동작의 확인이다.

동작상 확인된 사실 두 가지다.

- 외부 노드가 `Failed`가 되면 `ExecutionOrder`에 등록되지 않으므로 **LLM 호출이 발생하지 않는다.** 접근 불가한 DB가 비용을 만들지 않는다.
- `DiscoverAsync`는 자식 실패 후 `return`할 뿐 상위 반복을 중단하지 않으므로 루트 프로시저 분석은 정상 완료된다.

## 알려진 리스크 (이번 범위에서 수정하지 않음)

`GetObjectDdlAsync`에는 스키마가 `dbo`인데 조회 결과가 비면 스키마 조건을 제거하고 `SELECT TOP 1`로 재조회하는 폴백이 있다(`DbMetadataService.cs:90`). 대상 DB가 늘어나면 다른 스키마의 동명 객체를 잘못 선택할 확률이 올라간다. 기존 동작이며 이번 변경이 만든 문제가 아니므로 손대지 않되, 오탐이 관측되면 별도 과제로 다룬다.

## 테스트 계획

### 회귀 가드 (기존, 수정 없음)

`DependencyAnalysisOrchestratorTests.cs:338`과 `:380`은 플래그 기본값 `false`에서 `SkippedExternal`을 검증한다. 그대로 통과해야 하며, 이것이 "기본 동작 불변" 보증이다.

### 신규

1. **외부 객체 완전 분석** — `AllowExternalDatabaseConnections = true` + 다른 DB의 함수 의존성 → 노드가 `Succeeded`이고 `SpecPath`가 `output/External/<DB>/Functions/dbo.X/docs/Spec.md`로 계산되는지. 경로 규칙 검증은 `OutputPathResolverTests.cs`의 패턴을 따른다.
2. **분석 기준 DB 전파** — 파이프라인 러너 델리게이트를 대역으로 두고 전달받은 `request.AnalysisDatabase`를 캡처하여, 외부 객체 분석 시에도 값이 `rootKey.Database`와 같은지 확인한다. 2절의 불일치가 재발하면 이 테스트가 깨진다.
3. **외부 객체 접근 실패** — 외부 객체 메타데이터 조회가 예외를 던질 때 해당 노드는 `Failed`, 루트 노드는 `Succeeded`, 루트 Spec에 `분석 불가` 문자열이 포함되는지. `DependencyAnalysisOrchestratorTests.cs:495` 인근의 기존 실패 테스트와 같은 구조를 재사용한다.

4. **호환성 수준 soft-fail 유지** — `DbMetadataServiceDetailsTests.cs`는 리플렉션으로 private 메서드를 직접 호출하는 패턴을 쓰고 있으므로, 같은 방식으로 `GetDatabaseCompatibilityLevelAsync`를 잘못된 커넥션 문자열과 임의의 DB 이름으로 호출하여 예외 없이 160을 반환하는지 확인한다. 파라미터 추가가 기존 soft-fail 경로를 깨뜨리지 않음을 보증한다.

특정 DB의 실제 호환성 수준이 정확히 조회되는지는 실 SQL Server 연결이 필요하므로 자동 테스트 대상이 아니며, 아래 검증 시나리오에서 수동으로 확인한다.

## 검증 시나리오

플래그를 `true`로 두고 `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC`를 재분석했을 때:

- `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_COMM4CLIENT/docs/Spec.md` 등 5개 파일이 생성된다.
- `Spec.md`의 참조 코드 객체 섹션에서 5개 UDF가 `분석 생략(외부 객체)` 대신 상대 경로 링크로 표시된다.
- `dependency-manifest.json`의 해당 노드 `Status`가 `SkippedExternal`에서 `Succeeded`로 바뀌고 `Sha256`이 채워진다.
- 동일 조건으로 재실행하면 외부 객체 5개가 모두 캐시 적중하여 LLM 호출이 발생하지 않는다.
