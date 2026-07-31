# Task 7 완료 보고: CLI 설정·재귀 코드 객체 분석 연결

## 변경 사항

- `AnalysisSettings:AnalyzeReferencedCodeObjects` 기본값을 `false`로 추가했다.
- `OutputSettings:DependencyArtifactMode` 기본값을 `Reference`로 추가하고 CLI에서 안전하게 파싱한다.
- 배치와 대화형 단일 SP 분석은 활성화 시 `DependencyAnalysisOrchestrator.AnalyzeAsync`를 사용한다.
- 대화형 분석은 루트 선택 뒤 참조 SP/UDF 포함 여부를 기본 설정값으로 확인한다.
- 재귀 분석 결과의 L2 검토 점수와 Thinking 텍스트를 보존해 기존 저장 흐름의 문서 헤더와 `Thinking.md` 출력을 유지한다. Thinking은 TUI에 출력하지 않는다.
- 실패 노드는 이스케이프된 경고와 빈 줄로 알리고, 다른 노드와 루트 성공 문서 저장은 계속 진행한다.
- `Reference` 모드에서는 기존 raw DDL 복제 저장을 비활성화해 표준 DDL 아티팩트만 유지한다.

## TDD 증적

1. `AppSettings_DefaultsReferencedCodeObjectAnalysisToFalse`를 먼저 추가했고, `DependencyArtifactMode` 설정 키 부재로 실패함을 확인했다.
2. 설정을 추가한 뒤 `CliArgsTests` 2건 통과를 확인했다.
3. 재귀 결과의 `Review`/`ThinkingText` 보존 테스트를 추가했고, 새 속성 부재 컴파일 실패를 확인했다.
4. 모델과 오케스트레이터 반영 후 관련 테스트 3건 통과를 확인했다.

## 검증

```text
dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj
통과: 276, 실패: 0, 건너뜀: 0
```

`git diff --check`도 오류 없이 통과했다.

## 우려 사항

- 재귀 오케스트레이터가 의존 그래프를 동적으로 수집하므로 진행 이벤트의 `total`은 해당 이벤트 발생 시점까지 발견된 객체 수다.

## Fix 1: 실시간 진행 UI와 무음 하위 파이프라인

- `DependencyAnalysisRequest.Progress` 콜백과 `DependencyAnalysisProgress`를 추가했다. 각 객체의 검증 파이프라인 바로 전에 발생하므로 완료 후 상태를 재생하지 않는다.
- CLI는 콜백마다 `n/total. SP|UDF 객체명 분석 중` 한 줄만 출력한다.
- 재귀 분석 전용 `RecursiveAnalysisUserInteraction`은 하위 파이프라인의 상태, L1/L2, 경고, 오류 출력을 무음 처리한다. 대화형 L3 인간 검토와 DB 동기화 확인만 기존 UI에 위임한다.
- 실패 노드는 분석 완료 뒤 `Markup.Escape` 처리된 경고와 빈 줄을 출력한다.

### Fix 1 TDD·검증

1. `AnalyzeAsync_ReportsEachCodeObjectBeforeItsPipelineStarts`를 추가했고, 진행 타입·콜백 속성 부재의 컴파일 실패를 확인했다.
2. `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter FullyQualifiedName~ReportsEachCodeObjectBeforeItsPipelineStarts` — 1 통과, 0 실패.

## Fix 2: 전체 발견 뒤 고정 진행 합계

- `AnalyzeAsync`를 발견 단계와 실행 단계로 분리했다.
- 발견 단계는 직접 메타데이터로 전체 그래프와 자식 우선 실행 순서를 먼저 수집하고, 순환·중복·최대 깊이·외부 DB 제외 상태를 기존처럼 유지한다.
- 실행 단계는 수집 완료된 실행 순서를 순회하며 고정된 `ExecutionOrder.Count`를 진행 콜백의 total로 사용한다.
- `AnalyzeAsync_ReportsFixedTotalAfterDiscoveringAllAnalysisTargets`는 형제 노드가 있는 그래프에서 모든 진행 이벤트가 처음부터 `3`이라는 동일 total을 보고하는지 검증한다.

### Fix 2 TDD·검증

1. 고정 total 테스트는 기존 혼합 탐색/실행 구현에서 첫 이벤트가 `1/2`로 보고되어 실패함을 확인했다.
2. 발견/실행 분리 후 `DependencyAnalysisOrchestratorTests` focused 실행 9건이 통과했다.
