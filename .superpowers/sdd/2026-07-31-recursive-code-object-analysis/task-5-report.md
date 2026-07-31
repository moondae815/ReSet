# Task 5 보고서 — 재귀 코드 객체 그래프 실행

## 상태

완료. `DependencyAnalysisOrchestrator`가 코드 객체 키별 단일 작업 레지스트리를 사용해 재귀 분석을 수행한다.

## 변경 사항

- `IDependencyAnalysisOrchestrator.AnalyzeAsync`와 실행 요청 DTO를 추가했다.
- 메타데이터의 직접 `PROCEDURE`/`FUNCTION` 의존성만 그래프 간선으로 등록하고, `CodeObjectKey`로 노드와 작업을 중복 제거한다.
- DFS 방문 중인 키는 새 작업을 만들거나 기다리지 않아 순환 호출의 간선만 보존한다. 자식 작업이 끝난 후 부모의 공통 검증 파이프라인을 실행한다.
- 노드별 메타데이터/그래프/AI 파이프라인 예외는 `Failed`와 오류 사유로 격리한다. `OperationCanceledException`만 `Cancelled` 상태 후 재전파한다.
- 외부 DB 연결이 허용되지 않으면 `SkippedExternal`, 최대 깊이를 초과하면 `SkippedDepth`와 사유를 기록한다.
- 실행 횟수와 그래프 조회를 관찰할 수 있도록 `AnalysisNode.AnalysisAttempts`, `CodeObjectPipelineResult.Edges`, `GetNode`을 보강했다.
- 새 테스트는 다이아몬드 그래프의 공유 UDF 단 한 번 실행 및 자식 우선 순서, 순환 재큐 방지, 자식 분석 실패의 루트 격리를 검증한다.

## RED/GREEN 증거

1. RED

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests
   ```

   새 테스트 추가 직후 `DependencyAnalysisRequest` 미정의(`CS0246`)로 컴파일 실패했다.

2. GREEN — Task 5 집중 테스트

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests --no-restore --verbosity minimal -m:1 --disable-build-servers
   ```

   3건 통과, 실패 0건.

3. GREEN — Core 전체 회귀

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --no-restore --verbosity minimal -m:1 --disable-build-servers
   ```

   262건 통과, 실패 0건.

4. 변경 검사

   ```text
   git diff --check
   ```

   공백 오류 없음.

## 우려

- 실제 SQL Server 다중 DB 연결 환경은 제공되지 않아, 외부 DB 건너뛰기와 공통 파이프라인 위임은 단위 테스트 대역과 키/상태 계약으로 검증했다.
- 기본 샌드박스는 VSTest 통신 소켓 바인딩을 허용하지 않아 전체 회귀는 승인된 실행 환경에서 수행했다.

## Fix Round 1/5

### 변경 사항

- `DependencyInfo.DiscoveryDepth`가 각 직접 메타데이터 조회에서 다시 1로 시작하는 값을 신뢰하지 않고, 오케스트레이터가 전달한 실제 DFS 경로 깊이(`parent + 1`)로 깊이 제한을 판정하도록 수정했다.
- `IDbMetadataService.GetCodeObjectDetailsDirectAsync`를 추가했다. 온라인 구현은 최상위 객체와 `sys.sql_expression_dependencies`의 직접 의존성만 수집하며, 직접 의존 객체의 DDL/하위 의존성에는 접근하지 않는다. 오프라인 구현은 기존 스냅샷 조회로 호환된다.
- 재귀 그래프 오케스트레이터와 그 공통 검증 파이프라인 경로는 직접 메타데이터 조회를 사용한다. 따라서 외부 DB가 허용되지 않을 때 외부 객체는 `SkippedExternal`으로 기록된 뒤 추가 메타데이터/AI 실행 없이 종료된다.
- `A → B → C`, `MaxDepth = 1`에서 C가 `SkippedDepth`이고 메타데이터/AI 실행 목록에 없는지, 외부 UDF가 `SkippedExternal` 사유와 함께 직접 조회 이후 추가 조회·AI 실행 없이 남는지 회귀 테스트를 추가했다.

### RED/GREEN 증거

1. RED — 직접 조회 계약 부재

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests --no-restore --verbosity minimal -m:1 --disable-build-servers
   ```

   `IDbMetadataService.GetCodeObjectDetailsDirectAsync` 미정의(`CS1061`)로 컴파일 실패했다.

2. RED — 실제 경로 깊이

   같은 집중 테스트에서 `AnalyzeAsync_UsesTraversalDepthToSkipGrandchildBeyondMaximum`은 기대 `SkippedDepth`, 실제 `Succeeded`로 실패했다. 이는 하위 호출에서 다시 1이 되는 `DependencyInfo.DiscoveryDepth`를 사용한 결함을 재현한다.

3. GREEN — Task 5 집중 테스트

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~DependencyAnalysisOrchestratorTests --no-restore --verbosity minimal -m:1 --disable-build-servers
   ```

   5건 통과, 실패 0건.

4. GREEN — Core 전체 회귀

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --no-restore --verbosity minimal -m:1 --disable-build-servers
   ```

   264건 통과, 실패 0건.

### 우려

- 온라인 직접 조회는 실제 SQL Server 인스턴스 없이 구현·대역 계약으로 검증했다. 직접 조회가 `sys.sql_expression_dependencies`에서 외부 DB 이름을 반환하는 실제 권한 환경의 통합 검증은 후속으로 필요하다.
