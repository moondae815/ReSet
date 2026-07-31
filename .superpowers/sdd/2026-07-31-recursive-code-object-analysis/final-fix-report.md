# 최종 리뷰 수정 통합 보고서

## 수정 범위

최종 전체 리뷰의 merge-blocking 지적 9건을 TDD로 재현하고 통합 수정했다.

1. `SpecMarkdown`가 null 또는 공백인 파이프라인 결과는 성공 분석으로 발행하지 않고 해당 노드를 `Failed`로 처리한다.
2. 직접 메타데이터 조회가 현재 DB의 직접 참조 테이블 컬럼·설명·인덱스와 참조 SP/UDF DDL을 보존하도록 했다. 오프라인 직접 조회도 참조 DDL을 유지하며, 외부 DB 객체는 그래프 상태만 남기고 추가 조회하지 않는다.
3. SQL Server `type_desc`의 inline TVF 및 CLR SP/UDF 별칭을 그래프 타입으로 정규화했다. TVF는 테이블이 아니라 코드 객체 DDL 수집 대상으로 분류하며, 타입을 확인할 수 없는 외부 객체도 외부 연결 허용 설정과 무관하게 추가 조회 없이 `Unresolved` 키의 `SkippedExternal` 노드로 남긴다.
4. 같은 객체가 여러 경로로 발견될 때 최소 탐색 깊이를 우선하여, 유효한 얕은 경로가 이후 깊이 초과 경로에 의해 `SkippedDepth`로 덮이지 않게 했다.
5. `Spec.md` 저장 실패를 노드 `Failed`로 반영했다. 상태 변경 후 성공 문서를 다시 연결하여 순환 그래프에서도 실패 문서를 가리키는 링크가 남지 않게 했다.
6. 캐시 본문 비교에서 결정론적으로 생성되는 `## 참조 코드 객체` 섹션을 정규화하여, 링크가 추가된 최종 재귀 명세서도 다음 실행에서 캐시 히트할 수 있게 했다. 캐시 히트 시 저장용 YAML/NOTE 헤더는 본문에서 분리하고 기존 Critic 점수를 복원하여 헤더 누적과 100점 왜곡을 막았다.
7. 루트뿐 아니라 성공한 모든 하위 SP/UDF의 최종 Critic 점수 헤더와 `Thinking.md`를 객체별 문서 경로에 저장한다.
8. 오프라인 재귀 분석은 세션/appsettings DB가 아니라 `DbSnapshot.Database`를 조회해 루트 `CodeObjectKey`를 만든다.
9. `CodeObjectKey.CanonicalName`, 객체 출력 경로 및 PortableBundle DDL 파일명에 충돌 방지 인코딩을 적용했다. 단순 식별자의 기존 경로는 유지하고, 점·퍼센트·경로/금지 문자는 percent encoding한다.

## 추가 회귀 테스트

- null 명세 결과의 실패 상태와 분석 결과 미발행
- 현재 DB 직접 스키마/인덱스/참조 DDL 보존
- SQL Server inline/CLR `type_desc` 별칭
- TVF 직접 의존성의 코드 객체 DDL 분류
- 외부 `UNKNOWN` 객체의 무조회 `SkippedExternal`
- 얕은 경로 우선 및 단일 실행
- `Spec.md` 쓰기 실패 시 부모 링크 제거
- 하위 Critic 점수와 Thinking 아티팩트
- 최종 링크 명세의 캐시 유효성
- 장식된 캐시 명세의 본문/기존 Critic 점수 복원
- 오프라인 snapshot DB 루트 키
- dotted identifier와 `A/B` 대 `A_B` 식별자/경로 충돌

## 문서 동기화

- `README.md`: 현재 사용자 기능과 출력 구조가 정확하여 변경하지 않았다.
- `AGENTS.md`: 재귀 실패·최소 깊이·직접 컨텍스트·하위 산출물·충돌 방지 규칙과 실제 테스트 수 296개를 반영했다.
- `docs/architecture.md`: 재귀 분석의 최소 깊이, 직접 메타데이터 경계, 오프라인 루트 DB, 충돌 방지 경로 정책을 반영했다.

## 검증 결과

- `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --no-restore --verbosity minimal -m:1 -nr:false`
  - 통과 296, 실패 0, 건너뜀 0
- `dotnet build tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --no-restore --verbosity minimal -m:1 -nr:false`
  - 경고 0, 오류 0
- `git diff --check`
  - 통과
- 독립 읽기 전용 최종 재검토
  - 최초 Important 3건(TVF direct 분류, `Unresolved` 외부 조회, cache-hit 헤더 누적)을 추가 TDD 수정
  - 재검토 Critical 0, Important 0, Verdict `Ready`
