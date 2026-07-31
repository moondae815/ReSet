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
