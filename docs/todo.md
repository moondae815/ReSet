# 남은 후속 작업

`docs/superpowers/specs/` 하위 설계 문서(2026-07-26 ~ 2026-08-13, 37건)에 기록된 후속 항목을
소스 코드와 대조해 **실제로 열려 있는 것만** 남긴 목록이다.

- 작성일: 2026-08-13
- 제외: [2026-08-13 AGENTS.md 재구조화](superpowers/specs/2026-08-13-agents-md-restructure-design.md)
  관련 작업(별도 진행)
- 줄 번호는 작성 시점의 것이다. 시간이 지나면 멤버 이름으로 찾는 편이 정확하다.

---

## P0 — 실사용 피해가 즉시 발생

### 코드 생성 루프 (`ReSet.Validator.Core`)

- [ ] **레거시 전체 Job 루프에 총 시도 상한이 없다** — `CodegenWorkflowOrchestrator.cs:123-205`

  산출물 있음 + 매핑 성립 + L1/L2 매번 실패 조합이면 `consecutiveNoArtifactRetries`와
  `consecutiveUnverified` 두 연속 캡 어디에도 닿지 않는다. `MaxL2Attempts: "unlimited"`
  (= `int.MaxValue`)에서 무인 배치가 끝나지 않는 유료 기동이 된다.

  출처: `2026-08-09-silent-failure-closure-design.md` §후속 작업 1

### 검증 파이프라인 (`ReSet.Core`)

- [ ] **통합 루프에 점수 임계값 강제가 없다** — `VerificationPipelineOrchestrator.cs:2004-2023`

  단일 객체 루프는 `:1092-1110`에서 5축을 직접 검사해 `HasDefects`를 덮어쓰지만, 통합
  계획서 루프에는 그 블록 자체가 없다. Critic이 낮은 점수와 함께 `HasDefects: false`를
  내면 `검증 상태: 통과` 옆에 낮은 종합 신뢰도가 나란히 찍힌다.

  출처: `2026-08-03-verification-annotation-cleanup-design.md`,
  `2026-08-03-cancellation-policy-design.md`

- [ ] **L2 리뷰 호출 재시도 인프라 부재** — `:1163-1186`(단일), `:2118-2144`(통합)

  일시적 API 오류 한 번에 `break`하며 `_maxAttempts`가 남아 있어도 재시도하지 않는다.
  `RetryRescue`가 이전 회차 최고점을 구제해 완화할 뿐, 그 회차의 검증 자체는 포기된다.
  **5개 문서가 반복 기록한 최다 이월 항목**이며, 재시도 횟수·백오프·취소 전파·비용
  정책 결정이 선행되어야 한다.

  출처: `2026-08-01-verification-outcome-honesty`, `2026-08-03-cancellation-policy`,
  `2026-08-03-stage1-analysis-flow-hardening`, `2026-08-03-verification-annotation-cleanup`,
  `2026-08-03-verification-honesty-followups`

---

## P1 — 정확성·품질 손실

### 검증 파이프라인

- [ ] **`SpecHeader`에 인터페이스 점수 필드가 없다** —
      `SpecHeaderReader.cs:6-12`, `VerificationDocumentFormatter.cs:50`

  포매터는 `인터페이스 점수`를 YAML 헤더에 쓰지만 리더는 5개 키만 읽어 승인 화면이
  무시한다(캐시 왕복에서는 살아남는다). **고칠 때 함정이 있다** — 필드를 추가하면
  `ConsoleUserInteraction.cs:192-196`의 `?? 10` 폴백 뒤에 놓여 여섯 번째 조작 만점
  위험이 생긴다.

- [ ] **생성 호출 실패 재시도 0회** — `:1002`(명세서), `:1932`(계획서)

  명세서 경로와 계획서 경로의 공통 정책이라 한쪽만 고칠 수 없다.

  출처: `2026-08-05-batch-structure-redraft-design.md` ⑤

### 배치 계획 생성

- [ ] **브레인스토밍 원문이 3/3에 전달되지 않는다** — `IAiService.cs:19`

  `GenerateConsolidatedBatchPlanAsync(planStructure, specs, targetLanguage, jobName, …)`
  시그니처에 자리가 없다. 아키텍처 판단(Tasklet/Chunk 선택 등)이 목차 제목에 살아남은
  만큼만 본문에 도달한다.

  출처: `2026-08-05-batch-structure-redraft-design.md` ①

### 정적 분석 / 프롬프트 계약

- [ ] **정확 일치 타입 테이블 2곳이 분류기 밖에 있다** —
      `DependencyAnalysisOrchestrator.cs:327`(`TryParseCodeObjectType`),
      `MetadataExporter.cs:159`(`NormalizeCodeObjectDdlFolder`)

  `"P"`/`"FN"`/`"TF"`는 두 테이블에서 Procedure/Function이지만 `SqlObjectTypeClassifier`
  에서는 `Unresolved`이고, `AGGREGATE_FUNCTION`/`EXTENDED_STORED_PROCEDURE`는 그 반대다.
  오늘 오작동하지 않는 것은 실제 `Type` 값이 전부 `type_desc`에서 오기 때문이지 게이트가
  막고 있어서가 아니다.

  출처: `2026-08-09-type-classification-policy-design.md` §후속 4

- [ ] **내부 방어 가드의 경고가 프롬프트로 샌다** —
      `SqlStaticParser.cs:363-369` → `AiService.cs:244`

  `RecordUpdateMapping`의 `"내부 방어 가드 작동"` 경고가 `ControlFlowSummary`를 거쳐
  `"식별된 제어 흐름 구조 요약 (IF/WHILE)"`이라는 어긋난 머리말 아래 LLM 프롬프트에
  실린다. 현재 호출 그래프에서는 도달 불가하지만 `RecordDmlTarget` 계약이 바뀌면 열린다.

  출처: `2026-08-09-update-mapping-contract-design.md` §남은 후속 1

- [ ] **`BuildSpecSectionPrompts`의 `CrudAnalysis` 분기에 INSERT fill-in 표가 없다** —
      `AiService.cs:1592-1598`

  UPDATE는 `BuildUpdateMappingTemplateLines` 공유 헬퍼로 정리됐으나 INSERT는
  `BuildSpecificationPrompts`에만 fill-in 표가 있는 비대칭이 남아 있다. INSERT에는
  `CheckUpdateMappings`에 대응하는 L1 기계 대조가 없어 오늘 실패를 만들지는 않는다.

  출처: `2026-08-09-update-mapping-contract-design.md` §남은 후속 8

### 메타데이터 / 지시서

- [ ] **`AllowExternalDatabaseConnections`가 메타데이터 계층에 도달하지 않는다** —
      `DependencyAnalysisOrchestrator.cs:29`, `DbMetadataService.cs:420`

  `includeExternalCodeObjects: true`가 모든 호출부에 하드코딩되어 있어 설정이 무시된다.
  비재귀 경로도 같다.

  출처: `2026-08-03-stage1-analysis-flow-hardening-design.md` §범위 밖

- [ ] **`{specRoot}`가 `<outputRoot>/Procedures`만 덮는다** —
      `ArgumentTemplateResolver.cs:77-89`

  `External/<db>/Procedures/`와 `Functions/`의 명세서는 코딩 에이전트가 볼 수 없다.

  출처: `2026-08-07-migration-instructions-split-design.md` §남은 후속

- [ ] **Job 이름에 `.`이 들어가면 지시서 안내와 게이트 탐색이 어긋난다** —
      `FileMappingService.cs:141`

  게이트가 마지막 점 앞을 버린다. 진입점 파일명 경로는 여전히 성립한다.

  출처: `2026-08-07-migration-instructions-split-design.md` §남은 후속

---

## P2 — 잠재 결함 / 커버리지 공백

### 배치 계획·지시서

- [ ] **4개 필수 H2가 3곳에 하드코딩** — `AiService.cs:2044`(3/3 프롬프트),
      `:2413`(Critic 프롬프트), `MechanicalValidator.cs:70`(L1 검증기).
      2/3 단계의 실질 기여가 H3/H4뿐인 원인. 목차 단계 존치 여부는 실측이 필요하다
- [ ] **`StepLogicTests` 배치 위치가 어떤 지시서에도 없다**(C#·Java 공통) —
      파일은 `MetadataExporter.cs:775/825`가 만들지만 `TaskFileComposer.cs:335-336`은
      `ArchitectureTests`만 안내한다
- [ ] **C# 아키텍처 규칙이 단일 어셈블리 스코프** — `DataAccessPolicy.cs:126`의
      `typeof(…).Assembly`. 계약과 구현을 다른 프로젝트에 두는 Hexagonal 배치에서
      오탐이 난다. Java 쪽은 클래스패스 전체를 본다
- [ ] **`agent/` 직하 중복 `task-*.md`에서 `FirstOrDefault`가 열거 순서에 의존** —
      `InstructionBundleWriter.cs:292/384/472`. 진입점보다 나중에 쓰인 파일만 남기면 해결된다
- [ ] **`BuildUnverifiedFeedback`의 폴더 규약이 4개 중 2개만 말한다** —
      `CodegenLoopPolicy.cs:73-77`. `JobProjectDirectoryNames`가 인정하는 밑줄 제거 변형
      2개가 문구에서 빠졌다. 거짓은 아니고 누락이 실패를 만들지 않는다

### 스키마 주장 검증 게이트 잔여

- [ ] **`ComputeFenceLineFlags`에 미닫힘 펜스 폴백이 없다** —
      `MechanicalValidator.cs:750-767`. `MarkdownSectionLocator.FindIndexOutsideFence`가
      의도적으로 갖는 폴백("오탐보다 미탐이 훨씬 나쁘다")의 반쪽만 복제했다.
      실무상 도달 불가에 가깝다 — 펜스가 홀수면 Markdig 헤더 검사가 먼저 떨군다
- [ ] **`~~~` 펜스를 두 구현 다 인식하지 않는다** — 서로는 일치하나 Markdig와는 다르다
- [ ] **`SuggestedPromptFix` 5번 블록이 검출 패턴과 일치한다** — Actor가 이 피드백을
      문서의 "검증 이력" 류 섹션에 옮겨 적으면 게이트가 자기 지시문에 다시 걸린다.
      확률은 낮고 회귀 테스트가 없다
- [ ] **RAG/청크 경로가 테이블 단위 substring 필터를 쓴다** — `AiService.cs:1073-1090`.
      완전성 문장도 붙지 않아 단일 권위가 컬럼 단위로만 성립한다. Stage 1 전용이고
      실제 섹션 생성은 `BuildSpMetadataTexts`를 쓰므로 현재는 무해하다

  출처: `2026-08-09-schema-claim-verification-gate-design.md` §남은 후속 1·2·4·6

### 정적 분석

- [ ] **`DependencyInfo.Type` 타입화** — `DependencyInfo.cs:12`가 여전히 `string`.
      문자열 가드가 아니라 타입 시스템으로 원시 판정을 차단한다. 근본적이지만
      직렬화·스냅샷 호환성까지 번진다
- [ ] **`DbMetadataService` 재귀 의존성 경로에 동작 테스트가 없다** — 라이브 DB가
      있어야 실행되어 커버되지 않는다. 이 경로에서 분류기 위임을 통째로 되돌리는
      편집이 있어도 지금은 테스트로 잡히지 않는다
- [ ] **`NormalizeQualifiedName`이 per-segment 정규화가 아니다** —
      `MechanicalValidator.cs:897-898`. `[DB].[dbo].[T]`가 `DB].[dbo].[T`가 된다.
      오늘은 대조 양쪽에 같은 변환을 적용해 무해하다
- [ ] **`AliasTargetFinder`가 FROM 절 하위 트리 전체를 돈다** —
      `SqlStaticParser.cs:619-645`. `ExplicitVisit`을 오버라이드하지 않아 기본 순회가
      자식까지 내려간다. 중첩 서브쿼리가 바깥 대상과 같은 이름의 별칭을 쓰면 그것을
      잡는다. 근본 수정은 최상위 `TableReference`만 훑는 것

### 기타

- [ ] **`Task.WhenAll`이 첫 예외만 표면화한다** —
      `VerificationPipelineOrchestrator.cs:349`, `:431`. 로컬 프로바이더 병렬 분기에서
      `IOException`과 `OperationCanceledException`이 동시에 발생하면 필터가 통과해
      취소가 삼켜진다. 취소 정책 스캐너는 이를 잡지 못한다(필터가 있으므로)
- [ ] **`SaveMigrationPlanAsync`가 `EncodePathSegment`를 쓰지 않는다** —
      `Program.cs:2003`. 식별자에 `.`이나 파일명 금지문자가 있으면 캐시 조회 경로와
      저장 경로가 갈라진다
- [ ] **`ArtifactChangeDetector.Snapshot`의 TOCTOU 플래키** — `:33-42`의
      `EnumerateFiles` → `FileInfo.Length`. **3개 문서가 반복 기록한 유일한 실제 플래키**로,
      전체 실행 중 관측되고 단독 재실행에서는 통과한다

---

## P3 — 인지됨, 영향 미미

- [ ] `ScoreReadability` 라벨이 사실과 다르다 — 두 Critic 모두 코드 가독성이 아니라
      Mermaid 다이어그램 문법을 채점한다(`VerificationDocumentFormatter.cs:52`).
      명세서에서도 틀렸다. 라벨 문자열이 테스트로 고정되어 있다
- [ ] 2/3가 빈 응답을 내도 일반 방어가 없다 — `:1824-1841`. 재수립 경로만 방어한다
- [ ] 프롬프트의 pseudo-XML 블록이 `<`, `>`, `"`를 이스케이프하지 않는다 —
      `AiService.cs:196-209`. 진짜 XML로 파싱하는 곳은 없다
- [ ] 모호성 오류가 충돌하는 형제 후보를 나열하지 않는다 — `ResolveSectionBody` 마지막 분기
- [ ] `SpecExpectationsWiringPolicyScanner`가 `this._validator`를 못 잡는다 —
      `:48`이 `IdentifierNameSyntax`만 본다. 현재 그런 사용은 없다
- [ ] SP 목록이 시작 시 1회만 로드되어 세션 중 DB 변경이 반영되지 않는다
- [ ] TUI 선택 목록이 객체 디렉터리 이름만 렌더링해 서로 다른 DB의 동명 프로시저가
      구분되지 않는다
- [ ] 비재귀 경로가 `DependencyAnalysisOrchestrator`로 통일되지 않았다 — 요청 모델과
      파이프라인 호출, 배치 모드를 함께 재배선해야 한다
- [ ] 낡은 줄번호 인용 1건 잔존 — `CodegenWorkflowOrchestrator.cs:193`의 `(:806)`.
      나머지 5곳은 해소됐다. 줄 번호 대신 멤버 이름을 쓰는 편이 낫다
- [ ] 뮤테이션 저항 없는 테스트 2건 —
      `FindUncoveredRanges_EmptyDocument_ShouldReturnNothing`(조기 반환을 지워도 통과,
      값싼 경계 방어로 의도적 유지),
      `Pipeline_ShouldNotEnrichTablesWhenDefinitionsAreOmitted`("보강 스킵"과
      "보강했으나 결과가 빔"을 구별 못 함)
- [ ] `output/Jobs/POQSettleProc7/` 산출물 폐기 판단 (운영 결정)

---

## 정책 결정이 선행되어야 하는 것

코드 변경 전에 기준을 정해야 하는 항목이다. 반복 횟수는 여러 설계 문서가 같은 항목을
"별건"으로 미룬 횟수다.

| 항목 | 반복 | 요지 |
|---|---|---|
| **시도 간 진동 억제** | 3 | Actor가 매번 백지에서 재작성해 점수가 20점 이상 출렁인다. `IAiService` 인터페이스와 프롬프트를 함께 바꿔야 한다(이전 명세서를 넘겨 수정하게 하는 방향) |
| **합격 기준 정책** | 3 | 다섯 항목 전부가 기준을 넘어야 하는 현행 게이트 유지 vs 종합 점수 게이트 병행 |
| **조기 종료** | 1 | 종합 점수가 충분히 높으면 재시도 중단. 합격 기준 변경을 수반한다 |
| **구조화 출력(`--json-schema`)** | 1 | 세 CLI 모두 지원하며 Critic 채점 JSON 파싱을 견고하게 만든다. 다만 스키마 정의가 API 경로와 CLI 경로의 동작을 갈라놓는다 |
| **`ActorEffort: dynamic`의 CLI 동시 실행 제어** | 1 | dynamic을 쓰면 프로세스 3개가 동시에 뜨고 쿼터 소진이 빨라진다 |

### 해결 불가로 기록된 것

각 CLI가 제공하는 수단으로는 ReSet에서 해결할 수 없다고 판단된 항목이다.

- codex-cli의 전역 `AGENTS.md` 주입
- codex/agy의 출력 절단 감지
- agy의 Windows 명령행 stdin 한계 — 명확한 예외로 알리는 데서 멈춘다

---

## 문서 갱신이 필요한 것 (코드상 이미 해소, 문서에는 열린 채)

**전부 반영 완료(2026-08-13).** 각 설계 문서의 해당 항목에 취소선과 해소 근거를 달았다.
아래는 어디를 어떻게 고쳤는지의 기록이다.

- [x] `2026-08-08-step-error-code-verification-design.md` §후속 1 —
      **보강기와 파서의 "유효한 블록" 판정 불일치**는 `fix/silent-failure-closure`의
      세 커밋(`2ae7a2b`·`25319f6`·`933fb39`, 2026-08-09)이
      `BatchStepPlanParser.TryLocateStepsBlock` 공유로 닫았다. 보강기가 그 블록을 다시
      쓰지 못하면 뒤 블록으로 넘어가지 않고 원본을 그대로 둔다. 정규식 리터럴 중복도
      함께 사라졌다(`PlanStructureEnricher`에 `Regex` 0건)
- [x] `2026-08-08-step-error-code-verification-design.md` §후속 4 —
      **Claude 프롬프트 캐시 중단점**은 `PromptCacheBreakpointPolicy`로 해소(2026-08-12).
      두 번째 전송부터 찍는 정책이라 1회차에 끝나는 잡에서 캐시 쓰기 손실이 없다
- [x] `2026-08-07-migration-instructions-split-design.md` §남은 후속 —
      **`PlanBoundaryResolver`의 `allFound == true` 공백**은 `acf5210`(2026-08-09)의
      `AbsorbUncoveredRegions`(`:371`)로 해소
- [x] `2026-08-07-migration-instructions-split-design.md` §남은 후속 —
      **레거시 전체 Job 경로의 `nothingVerified` 무한 재시도**는 `e1ccfbd`(2026-08-09)의
      `MaxConsecutiveUnverifiedRetries`로 닫혔다. **부분 해소로 기록했다** — 위 P0 ①의
      다른 조합은 여전히 열려 있고, 문서에도 그 사실을 함께 적었다
- [x] `2026-08-13-outline-roster-and-split-failure-visibility-design.md` §남은 후속 —
      **신뢰도 점수와 검증 커버리지의 분리 표기**는 `VerificationCoverage` 모델과
      포매터의 `coverageLine`으로 해소
      (`2026-08-13-verification-coverage-design.md`가 닫음)
- [x] `2026-08-08-static-analysis-identity-design.md` §후속 1·2·3 /
      `2026-08-09-type-classification-policy-design.md` §후속 2 —
      **UPDATE 컬럼 매핑표 / `UPDATE … FROM` 자기참조 / `SET` 절 동시평가** 3건은
      `2026-08-09-update-mapping-contract`가 닫았다(`AiService.cs:554-577`)
- [x] **(목록 밖에서 추가 확인)** `2026-08-08-static-analysis-identity-design.md` §후속 4 /
      `2026-08-09-type-classification-policy-design.md` §후속 3 —
      **명세서 재발 방지 검증 게이트**도 이미 닫혀 있었다. `63483f2`(2026-08-10)가
      L2 Critic 기준이 아니라 **L1 기계 검증**에 `CheckSchemaClaims`
      (`ErrorType.SchemaClaimFalse`)를 넣었고, `ab6dd5b`가 코드 펜스 오탐을 닫았다.
      잔여 한계는 `2026-08-09-schema-claim-verification-gate` §남은 후속에 있으며
      그중 4건은 위 P2에 반영되어 있다
