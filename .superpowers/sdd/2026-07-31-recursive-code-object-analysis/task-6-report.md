# Task 6 구현 보고서

## 구현 범위

- `SpecificationLinker`를 추가해 성공한 직접 자식 객체에만 결정론적 상대 `Spec.md` 링크를 생성했다.
- 실패, 깊이 제한, 외부 객체, 취소 상태는 깨진 링크 대신 상태별 사유를 문서에 기록한다.
- 링크 갱신 뒤 `MechanicalValidator.Validate`를 호출하고, 성공 여부와 관계없이 `CleansedMarkdown`을 반환한다.
- `ExportCodeObjectArtifactsAsync`를 추가해 표준 DDL(`object_definition.sql`), SHA-256, 상태, 오류, 호출 간선 및 상대 Spec/DDL 경로를 `dependency-manifest.json`에 저장한다.
- `Reference` 모드는 참조 DDL 복제를 생략하고, `PortableBundle` 모드만 기존 raw DDL 분산 저장을 호출한다.
- 그래프 노드와 분석 결과에 Spec/DDL 경로를 보존하도록 모델을 확장했다.

## 테스트

- RED 확인: 신규 타입 `SpecificationLinker` 부재로 테스트 컴파일 실패를 확인했다.
- 집중 테스트: `SpecificationLinkerTests`, `MetadataExporterTests` 9건 통과.
- 전체 회귀: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj` — 268건 통과, 실패 0건.

## 유의사항

- 전체 및 집중 테스트는 샌드박스의 MSBuild named-pipe 권한 제한으로 권한 확장 환경에서 실행했다.
- 기존 `DbMetadataServiceTests`의 nullable 경고는 계속 출력되지만 이번 변경에서 새로 발생한 경고는 없다.
