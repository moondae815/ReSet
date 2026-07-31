# Task 6 구현 보고서

## 구현 범위

- `SpecificationLinker`를 추가해 성공한 직접 자식 객체에만 결정론적 상대 `Spec.md` 링크를 생성했다.
- 실패, 깊이 제한, 외부 객체, 취소 상태는 깨진 링크 대신 상태별 사유를 문서에 기록한다.
- 링크 갱신 뒤 `MechanicalValidator.Validate`를 호출하고, 성공 여부와 관계없이 `CleansedMarkdown`을 반환한다.
- `ExportCodeObjectArtifactsAsync`를 추가해 표준 DDL(`object_definition.sql`), SHA-256, 상태, 오류, 호출 간선 및 상대 Spec/DDL 경로를 `dependency-manifest.json`에 저장한다.
- `Reference` 모드는 참조 DDL 복제를 생략하고, `PortableBundle` 모드는 부모의 참조 코드 객체 DDL만 raw 번들에 저장한다.
- 그래프 노드와 분석 결과에 Spec/DDL 경로를 보존하도록 모델을 확장했다.

## 테스트

- RED 확인: 신규 타입 `SpecificationLinker` 부재로 테스트 컴파일 실패를 확인했다.
- 집중 테스트: `SpecificationLinkerTests`, `MetadataExporterTests` 9건 통과.
- 전체 회귀: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj` — 268건 통과, 실패 0건.

## 유의사항

- 전체 및 집중 테스트는 샌드박스의 MSBuild named-pipe 권한 제한으로 권한 확장 환경에서 실행했다.
- 기존 `DbMetadataServiceTests`의 nullable 경고는 계속 출력되지만 이번 변경에서 새로 발생한 경고는 없다.

## Fix round 1 — 리뷰 Critical 1 / Important 3

- 재귀 분석 완료 뒤 `DependencyAnalysisOrchestrator`가 모든 성공 객체의 최종 Spec을 링크 갱신·저장하고 canonical DDL 및 매니페스트를 내보내도록 연결했다.
- `ExportCodeObjectArtifactsAsync`는 별도 인자가 없으면 `SpDefinition.RawPromptContext`를 사용하며, 값이 비어도 빈 `prompt-context.md`를 생성한다.
- `PortableBundle`은 부모 `raw/ddl`에 참조 코드 객체 DDL만 저장한다. 대상 객체 DDL은 canonical 파일 하나만 유지하며 `raw/ddl/sp_definition.sql`은 만들지 않는다.
- EOF에 끝난 참조 섹션도 교체하고, 링크 텍스트·사유는 Markdown 이스케이프, URL 세그먼트는 percent-encoding 한다.

### Fix round 1 테스트

- 신규 RED: 재귀 완료 아티팩트 연결용 생성자 부재로 컴파일 실패를 확인했다.
- 집중: `SpecificationLinkerTests`, `ExportCodeObjectArtifactsAsync`, 재귀 아티팩트 통합 테스트 8건 통과.
- 전체 회귀: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj` — 274건 통과, 실패 0건.

## Fix round 3 — SQL/CLR type_desc 별칭

- `SQL_INLINE_TABLE_VALUED_FUNCTION`, `CLR_SCALAR_FUNCTION`, `CLR_TABLE_VALUED_FUNCTION`을 functions로 정규화했다.
- `CLR_STORED_PROCEDURE`를 procedures로 정규화했다.
- SQL Server의 기존 코드 값 및 SQL type_desc 별칭은 계속 허용하고, 비코드 객체 차단 규칙은 유지한다.

### Fix round 3 테스트

- RED: inline table-valued 함수 DDL이 복제되지 않음을 확인했다.
- 집중: Task 6 관련 15건 통과.
- 전체 회귀: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj` — 274건 통과, 실패 0건.

## Fix round 2 — PortableBundle 코드 객체 한정

- PortableBundle의 참조 DDL 복제는 정규화된 Procedure/Function 타입만 허용하도록 제한했다.
- `USER_TABLE`, `VIEW` 등 비코드 객체는 `ReferencedDdlText`가 있어도 raw DDL에 복제하지 않는다.
- `SQL_STORED_PROCEDURE`, `SQL_SCALAR_FUNCTION` 등 기존 코드 객체 별칭은 각각 procedures/functions 저장을 유지한다.

### Fix round 2 테스트

- RED: 테이블 DDL이 `functions` 폴더에 잘못 생성됨을 확인했다.
- 집중: Task 6 관련 15건 통과.
- 전체 회귀: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj` — 274건 통과, 실패 0건.
