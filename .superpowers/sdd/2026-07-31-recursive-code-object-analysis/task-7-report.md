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

- 재귀 오케스트레이터가 의존 그래프를 동적으로 수집하므로 전체 객체 수는 분석 종료 시 확정된다. CLI는 확정된 노드 목록을 기준으로 `n/total. SP|UDF 객체명 분석 중` 상태를 출력한다.
