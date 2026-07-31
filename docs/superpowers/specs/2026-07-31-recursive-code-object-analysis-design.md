# 재귀 코드 객체(SP/UDF) 분석 및 문서 연결 설계

## 목적

개별 Stored Procedure 분석에서 발견된 하위 Stored Procedure와 UDF를 최대 의존성 깊이까지 독립 분석해 각각의 `Spec.md`를 생성한다. 하나의 코드 객체는 호출 경로가 여러 개여도 한 번만 분석하며, 모든 호출자 문서는 동일한 분석 문서로 연결한다.

이 설계는 현재의 재귀 메타데이터 수집, L1/L2/L3 검증 파이프라인, DDL 해시 캐시와 소프트 페일 원칙을 재사용한다. 호출 관계를 AI가 요약만 하던 상태를 변경하되, 상위 객체 분석의 원자성이나 기존 산출물 경로 호환성을 깨지 않는다.

## 범위와 비범위

### 포함

- SQL Server `PROCEDURE`와 `FUNCTION`을 같은 독립 분석 단위로 처리한다.
- 직접·간접 호출 관계를 `MaxDependencyDepth`까지 추적한다.
- 각 객체의 독립 `Spec.md`, 원본 DDL 참조, 호출 관계 링크와 분석 상태를 저장한다.
- 다중 호출, 다이아몬드 의존성, 순환 호출, 외부 DB 객체, 권한·DDL 수집 실패를 안전하게 처리한다.
- 온라인 DB와 오프라인 스냅샷 모드에서 같은 분석 그래프 규칙을 적용한다.

### 제외

- 테이블·뷰는 독립 `Spec.md` 대상이 아니다. 기존처럼 스키마 메타데이터로만 보관한다.
- 동적 SQL로 확정할 수 없는 참조를 강제로 분석 대상이라고 단정하지 않는다. 발견된 후보는 경고와 함께 매니페스트에 남긴다.
- 개별 SP 분석 완료 뒤 외부 코딩 에이전트를 자동 실행하지 않는다.

## 결정 사항

### 1. 분석 단위와 식별자

`SpDefinition`을 즉시 대규모로 이름 변경하지 않는다. 대신 `CodeObjectType` (`Procedure`, `Function`)과 외부 DB를 포함하는 불변 식별자 `CodeObjectKey`를 추가한다.

```csharp
public sealed record CodeObjectKey(
    string? Database,
    string Schema,
    string Name,
    CodeObjectType Type);
```

- 같은 DB의 이름 없는 database 값은 현재 연결 DB의 정규화된 이름으로 변환한다.
- 키 비교는 대소문자를 구분하지 않는다.
- `CodeObjectType`은 SQL Server의 세부 `sys.objects.type`을 `Procedure` 또는 `Function`으로 정규화한다.
- 캐시 키와 출력 경로 키에도 이 식별자를 사용한다. 따라서 같은 이름의 SP와 UDF, 다른 DB의 동명 객체가 충돌하지 않는다.

`SpDefinition`에는 `ObjectType` 속성을 더한다. 호환성을 위해 기존 `GetSpDetailsAsync`와 `RunPipelineAsync`는 남기고, 내부적으로 새 일반화 API를 호출하는 Procedure 전용 어댑터로 전환한다.

### 2. 메타데이터 API 일반화

`IDbMetadataService`에 다음 API를 추가한다.

```csharp
Task<SpDefinition> GetCodeObjectDetailsAsync(
    string connectionString,
    CodeObjectKey objectKey,
    int maxDepth,
    CancellationToken cancellationToken = default);
```

`DbMetadataService`는 `sys.objects` 타입을 확인한 뒤 SP/UDF DDL을 수집하고, 동일한 `SqlStaticParser` 분석과 의존성 DFS를 적용한다. UDF 전용 프롬프트를 위해 함수 종류(Scalar/Inline TVF/Multistatement TVF)와 반환 형식·반환 테이블 스키마도 메타데이터로 보강한다.

`DependencyInfo`에는 `SourceObjectKey`를 추가한다. 기존의 평면 `Dependencies`와 `DiscoveryDepth`만으로는 정확한 부모-자식 호출 간선을 복원할 수 없기 때문이다. DFS는 어떤 객체가 각 의존성을 발견했는지 기록하며, 동적 SQL 후보 여부도 표시한다.

오프라인 모드의 `DbSnapshot` 키도 `CodeObjectKey`의 문자열 표현으로 확장한다. 이전 스냅샷의 SP 키는 읽을 수 있도록 역호환 로더를 제공한다.

### 3. 분석 그래프와 작업 레지스트리

새 `DependencyAnalysisOrchestrator`가 루트 객체 하나에서 다음 순서로 동작한다.

1. 루트 객체의 메타데이터를 수집하고 코드 객체 간선을 가진 `CodeObjectGraph`를 만든다.
2. `CodeObjectKey`별 `AnalysisNode`를 등록한다. 상태는 `Queued`, `Running`, `Succeeded`, `Failed`, `SkippedExternal`, `SkippedDepth`다.
3. 동일 키가 다시 발견되면 기존 노드와 작업을 재사용한다. 이미 `Running`인 키는 기다리거나 현재 작업의 결과를 공유하며 새 작업을 만들지 않는다.
4. 최대 깊이 내의 노드를 자식 우선(post-order)으로 분석한다. 순환 간선은 노드 등록 시 차단하되 호출 링크는 보존한다.
5. 각 노드는 기존 검증 파이프라인을 일반화한 `RunCodeObjectPipelineAsync`로 분석한다. Procedure는 기존 `RunPipelineAsync` 경로를 유지한다.
6. 모든 노드가 종료된 뒤에만 문서 링크 주입과 매니페스트 저장을 수행한다.

자식 우선 방식은 상위 문서를 만들 때 실제 생성된 하위 문서의 상태를 확정할 수 있게 한다. 다만 실패한 자식이 있어도 상위 분석은 중단하지 않는다. 실패는 상위 문서와 매니페스트에 명시한다.

```text
루트 SP
  └─ 그래프 구성·중복 제거
       ├─ 하위 UDF/SP 분석 및 저장
       ├─ 동일 객체 재발견 → 기존 노드 재사용
       └─ 루트 SP 분석·저장
  └─ 모든 Spec.md에 호출 관계 링크 주입
```

### 4. 산출물과 중복 없는 DDL 보관

기존 문서 위치는 유지한다.

```text
output/
  Procedures/{schema}.{name}/docs/Spec.md                 # 현재 DB SP (기존 경로)
  Functions/{schema}.{name}/docs/Spec.md                  # 현재 DB UDF
  External/{database}/Procedures/{schema}.{name}/docs/Spec.md
  External/{database}/Functions/{schema}.{name}/docs/Spec.md
  Objects/{schema}.{name}.{type}/raw/object_definition.sql # 현재 DB의 표준 DDL
  Objects/{database}/{schema}.{name}.{type}/raw/object_definition.sql
```

현재 DB SP는 기존 `output/Procedures/{schema}.{name}` 경로를 유지한다. 외부 DB 객체에만 database 세그먼트를 넣어 충돌을 피하며, 파일명·디렉터리명에는 기존의 안전한 이름 정규화를 적용한다. 따라서 기존 문서의 이동·재작성이나 호환 링크는 필요하지 않다.

`Objects/`는 코드 객체 DDL의 단일 기준 저장소다. 각 분석 문서 폴더의 `raw/`에는 다음 파일만 둔다.

- `dependency-manifest.json`: 호출 관계, 객체 키, DDL SHA-256, 표준 DDL/Spec 상대 경로, 분석 상태·오류
- `metadata.json`: DDL 본문을 제외한 객체 메타데이터와 정적 분석 결과
- `prompt-context.md`: 실제 AI 요청 원문을 보존하는 감사용 파일이다. 참조 DDL이 들어갈 수 있으나, 이것은 재현성을 위한 텍스트 스냅샷이며 DDL 파일의 기준 저장소로 사용하지 않는다.

새 `OutputSettings:DependencyArtifactMode`는 `Reference`를 기본값으로 하며, 이 모드에서는 `raw/ddl/procedures`와 `raw/ddl/functions`에 참조 DDL 사본을 만들지 않는다. `PortableBundle`을 선택하면 기존처럼 해당 분석 폴더에 참조 DDL 사본을 저장한다. 어느 모드이든 실제 AI 요청 전문인 `prompt-context.md`는 보존한다. 포터블 모드는 외부 전달을 위한 명시적 선택이며, 일반 분석에는 사용하지 않는다.

### 5. 문서 링크 규칙

AI에게 Markdown 링크 생성을 맡기지 않는다. 분석·검증이 끝난 Markdown에 `SpecificationLinker`가 결정론적으로 섹션을 삽입 또는 갱신한다.

섹션 이름은 `## 참조 코드 객체`로 고정하며 각 항목에 아래를 표시한다.

- 객체 유형(SP/UDF), 완전한 객체 키, 발견 깊이
- 호출 목적의 정적 분석 요약
- 생성된 독립 `Spec.md` 상대 링크
- 생성 실패·깊이 초과·외부 DB·DDL 미수집이면 링크 없는 상태와 사유

동일 객체를 여러 곳에서 호출하면 모든 호출자의 섹션은 같은 표준 경로를 가리킨다. 순환 호출의 링크도 생성하지만, 링크 대상이 아직 실패했다면 실패 상태로 보인다. 링크 갱신은 마크다운·Mermaid 정화 이후에 실행하고, 다시 L1 검증을 수행해 문서 형식 훼손을 막는다.

### 6. 프롬프트와 검증

`AiService`는 `ObjectType`에 따라 영문 시스템 프롬프트의 분석 지시를 분기한다.

- Procedure: 기존의 트랜잭션, 오류 코드, CRUD, 입출력 계약 검증을 유지한다.
- Function: 반환 계약, 결정성·부작용, 호출별 계산 규칙, 참조 테이블/UDF, TVF 결과 스키마를 검증한다. 프로시저 전용 `BEGIN TRAN`, 오류 반환 강제 규칙은 적용하지 않는다.

공통 L1 마크다운·Mermaid 검증, L2 Critic, 최종 품질 경고 규칙은 동일하다. 모든 객체의 AI Thinking은 기존처럼 `Thinking.md`와 파일 전용 로그에만 저장한다.

### 7. 캐시·재시도·실패 격리

캐시 인덱스의 기존 `ProcedureName` 키를 `CodeObjectKey` 문자열 키로 확장한다. 복합 해시는 대상 DDL, 직접·간접 의존 코드 DDL 해시, 설정된 최대 깊이를 포함한다.

- 캐시 히트: 기존 독립 문서와 매니페스트 상태를 재사용하고 링크만 최신화한다.
- 개별 노드 실패: 실패 노드만 `Failed`로 기록하며 다른 노드와 루트 문서 저장을 계속한다.
- 메타데이터 권한·DDL 오류: 경고를 기록하고 링크 대상은 `분석 불가`로 남긴다.
- 외부 DB 객체: 명시적으로 허용된 연결이 없으면 `SkippedExternal`로 기록한다. 현재 DB 객체로 조용히 대체하지 않는다.
- 취소: 새 객체 분석 시작을 중단하고, 이미 저장된 문서는 보존한다. 매니페스트에는 취소 시점의 완료 상태를 남긴다.

### 8. CLI와 진행 상태

`DatabaseSettings:MaxDependencyDepth`를 재사용한다. 별도 활성화 설정으로 `AnalysisSettings:AnalyzeReferencedCodeObjects`(기본 `false`)를 추가한다. 개별 SP 메뉴에서 활성화 상태와 예상 분석 객체 수를 보여 주고, 사용자는 현재 실행에 한해 활성화·비활성화할 수 있다.

진행률은 루트 메타데이터 수집, 그래프 구성, 각 객체 분석, 링크 주입으로 나눈다. 객체 이름·유형·순번을 표시하되 AI Thinking은 TUI에 출력하지 않는다. 개별 노드 실패 시 빈 줄을 포함한 경고를 출력하고 다음 작업을 계속한다.

### 9. 테스트 전략

단위 테스트:

- `CodeObjectKey`의 DB·유형 포함 동등성 및 출력 경로 충돌 방지
- 다이아몬드 의존성에서 하위 UDF/SP가 정확히 한 번만 분석되는지
- 순환 호출에서 무한 재귀 없이 양방향 링크 상태가 기록되는지
- 최대 깊이, 동적 SQL 후보, 외부 DB, DDL 수집 실패의 상태와 소프트 페일
- UDF 프롬프트가 프로시저 전용 트랜잭션 요구를 포함하지 않는지
- `Reference` 모드에 중복 참조 DDL이 저장되지 않고 `PortableBundle` 모드에는 저장되는지
- 링크 주입 후 Markdown L1 검증과 상대 경로의 유효성
- 오프라인 스냅샷의 SP/UDF 조회와 이전 키 형식 호환성

통합 테스트:

- `SP A -> UDF X`, `SP B -> UDF X`에서 UDF 문서 하나와 두 개의 정상 링크가 생성되는지
- `SP A -> SP B -> UDF X`에서 자식 우선 분석과 모든 문서의 매니페스트가 일관되는지
- 자식 한 개가 실패해도 루트 `Spec.md`가 실패 사유를 포함해 저장되는지

## 수용 기준

- 하위 SP/UDF는 설정 깊이 안에서 각각 하나의 `Spec.md`를 가진다.
- 같은 객체는 한 실행에서 단 한 번의 AI 분석·파일 저장만 수행한다.
- 모든 호출자 문서에는 존재하는 문서로만 링크가 생성되고, 미생성 대상에는 상태와 사유가 표시된다.
- 기본 저장 모드에서는 참조 코드 객체의 DDL 파일이 호출자 폴더마다 중복 저장되지 않는다.
- 기존 루트 SP 단독 분석, 캐시, 오프라인 모드, L1/L2/L3 검증과 소프트 페일 동작은 유지된다.
