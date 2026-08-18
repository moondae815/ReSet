# 남은 후속 작업

`docs/superpowers/specs/` 하위 설계 문서(2026-07-26 ~ 2026-08-14, 38건)에 기록된 후속 항목을
소스 코드와 대조해 **실제로 열려 있는 것만** 남긴 목록이다.

- 작성일: 2026-08-13 / 재검증일: 2026-08-14, 2026-08-16
- **2026-08-16 재검증 결과**: 그 사이 `src/`에 커밋이 24건 있었고 파일 14개가 바뀌었다.
  그중 **3건이 해소**되어 P2에서 아래 완료 기록으로 옮겼다 — C# 아키텍처 규칙의 단일
  어셈블리 스코프, `StepLogicTests` 배치 위치 안내 부재, `ArtifactChangeDetector` TOCTOU.
  나머지 열린 항목은 전부 그대로이며, 바뀐 파일에 걸린 것들의 줄 번호를 이번에 갱신했다
  (`MechanicalValidator`가 특히 크게 밀렸다 — 예: `:750→:1174`, `:897→:1321`).
- **신규 발굴 3건 + 실측 3건**: 목록의 근거가 2026-08-13까지의 문서 37건이었는데, 그 뒤
  [2026-08-14 생성 번들 계약](superpowers/specs/2026-08-14-generated-bundle-contract-design.md)이
  추가됐다. 그 문서가 §제외·§남은 후속으로 미룬 것 중 **3건이 열려 있어 P1·P2에 넣었고**,
  돌려 봐야 아는 3건은 새 섹션 `실측이 필요한 것`으로 갈랐다. 셋 다 프롬프트 사안이라
  한 뿌리에서 나온다 — 그 문서가 "프롬프트는 재생성 결과가 비결정적이라 별도 설계로
  다룬다"며 통째로 제외했기 때문이다.
- **2026-08-17 — 프롬프트 유예를 부분적으로 깼다.** 위 "프롬프트는 재생성 결과가
  비결정적이라 별도 설계로 다룬다"는 판단은
  [축 A 명세서 충실도 설계](superpowers/specs/2026-08-17-axis-a-spec-fidelity-design.md)에서
  조건부로 해제됐다. **비결정성을 없애는 것이 아니라, 비결정적 산출물을 결정적으로
  검사한다** — 프롬프트 규칙을 혼자 추가하지 않고, 규칙과 L1 검사가 같은 추출기
  결과에서 나오는 경우에만 추가한다. 그 계약을 지키지 않는 프롬프트 사안(P1의
  `목차가 S01의 TargetTables를 채우지 못한다` 등)은 여전히 유예 상태다.
- 2026-08-14 재검증 때의 기록: 목록 자체가 코드와 어긋난 4건을 그때 고쳤다 — P2의
  **필수 H2 하드코딩**(3곳→4곳, 단계 라벨 오기), P3의 **모호성 오류 메시지**(절반은 이미
  해소), 줄 번호 2건. 제외였던
  [2026-08-13 AGENTS.md 재구조화](superpowers/specs/2026-08-13-agents-md-restructure-design.md)는
  `3d3568e`로 **완료**됐다.
- 줄 번호는 재검증일(2026-08-16) 기준으로 전부 확인했다. 시간이 지나면 멤버 이름으로 찾는
  편이 정확하다.

---

## P0 — 실사용 피해가 즉시 발생

### 코드 생성 루프 (`ReSet.Validator.Core`)

- [ ] **레거시 전체 Job 루프에 총 시도 상한이 없다** — `CodegenWorkflowOrchestrator.cs:123-205`

  산출물 있음 + 매핑 성립 + L1/L2 매번 실패 조합이면 `consecutiveNoArtifactRetries`와
  `consecutiveUnverified` 두 연속 캡 어디에도 닿지 않는다. `MaxL2Attempts: "unlimited"`
  (= `int.MaxValue`)에서 무인 배치가 끝나지 않는 유료 기동이 된다.

  출처: `2026-08-09-silent-failure-closure-design.md` §후속 작업 1

### 검증 파이프라인 (`ReSet.Core`)

- [ ] **통합 루프에 점수 임계값 강제가 없다** — `VerificationPipelineOrchestrator.cs:2024-2039`

  단일 객체 루프는 `:1092-1110`에서 5축을 직접 검사해 `HasDefects`를 덮어쓰지만, 통합
  계획서 루프에는 그 블록 자체가 없다. Critic이 낮은 점수와 함께 `HasDefects: false`를
  내면 `검증 상태: 통과` 옆에 낮은 종합 신뢰도가 나란히 찍힌다.

  출처: `2026-08-03-verification-annotation-cleanup-design.md`,
  `2026-08-03-cancellation-policy-design.md`

- [ ] **L2 리뷰 호출 재시도 인프라 부재** — `:1163-1186`(단일), `:2133-2162`(통합)

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

- [ ] **생성 호출 실패 재시도 0회** — `:984`(명세서), `:1923`(계획서)

  명세서 경로와 계획서 경로의 공통 정책이라 한쪽만 고칠 수 없다.

  출처: `2026-08-05-batch-structure-redraft-design.md` ⑤

### 배치 계획 생성

- [ ] **브레인스토밍 원문이 3/3에 전달되지 않는다** — `IAiService.cs:19`

  `GenerateConsolidatedBatchPlanAsync(planStructure, specs, targetLanguage, jobName, …)`
  시그니처에 자리가 없다. 아키텍처 판단(Tasklet/Chunk 선택 등)이 목차 제목에 살아남은
  만큼만 본문에 도달한다.

  출처: `2026-08-05-batch-structure-redraft-design.md` ①

- [ ] **목차가 S01의 `TargetTables`를 채우지 못한다** —
      `PlanStructureEnricher.cs:267/305`, `AiService.cs:2057`(`DraftBatchPlanStructureAsync`)

  **신규 항목 중 실측에서 실제 비용을 낸 유일한 것이다.** Proc14 회차에서 결함 1건으로
  잡혔고, 그 커밋(`87d6e30`, 2026-08-16)은 이를 자기 수정과 무관한 별개 사안으로 남겼다.
  보강기가 정적 분석의 쓰기 대상으로 `TargetTables`를 통째로 교체하는데도 비는 단계가
  생긴다. 왜 그 단계만 비는지부터 봐야 한다. 목차 생성 프롬프트 사안이라 재생성 결과가
  비결정적이고, 그래서 원 설계가 범위 밖으로 뒀다.

  출처: `2026-08-14-generated-bundle-contract-design.md` §제외

### 정적 분석 / 프롬프트 계약

- [ ] **호출된 객체가 참조하는 컬럼이 호출자의 스키마 표로 접혀 올라오지 않는다** —
      `SqlStaticParser.cs:180`(`ReferencedColumnsPerTable`)

  `UP_UTIL_SETTLE_EXPECT_PROC`가 부르는 UDF `UF_GET_COLLECTYMD`는
  `TPGCollectPeriodMst`에서 `CollectMonth2/3`·`CollectDay2/3`·`CollectTxSDay2/3`·
  `CollectTxEDay2/3` 8컬럼을 읽는다. 이 컬럼들은 `EXPECT_PROC` 자신의 SQL 본문에는 한
  번도 나오지 않으므로 `EXPECT_PROC`의 `ReferencedColumnsPerTable`에 애초에 들어가지
  않고, 그 결과 **`EXPECT_PROC`의 `Spec.md:341`이 실재하는 컬럼을 "제공된 스키마 표에
  정의되지 않았다"고 잘못 기록한다.** `UF_GET_COLLECTYMD` 자신의 `prompt-context.md`에는
  이 8컬럼이 정상적으로 실려 있어 — 컬럼 리졸버 자체는 옳고, 빠진 것은 **호출된 객체의
  컬럼 의존성을 호출자에게 병합하는 단계**다. 실측 확정(2026-08-17, §설계 1.3의 원래
  추정 "리졸버 결함"은 측정으로 반증됨) — `DetectOrphanedColumnKeys`를 넓혀도 닫히지
  않는다. 그 검출기는 `ReferencedColumnsPerTable`의 **키**가 병합 안 된 경우만 보는데, 이
  8컬럼은 SQL에 없어 애초에 키 자체가 생기지 않는다. 닫으려면 재귀 의존성 수집이 하위
  객체(`UF_GET_COLLECTYMD`)의 `ReferencedColumnsPerTable`을 상위(`EXPECT_PROC`)로 접어
  올리는 별도 설계가 필요하다 — 축 A 명세서 충실도가 아니라 프롬프트 입력 구성의 문제라
  이 계획의 범위 밖에 남겼다.

  출처: `2026-08-17-axis-a-spec-fidelity-design.md` §설계 1.3 (Task 3 실측)

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
      `AiService.cs:1609-1618`

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

- [ ] **4개 필수 H2가 4곳에 하드코딩** — `AiService.cs:668`(`ConsolidatedPlanRules`,
      3/3 생성 프롬프트), `:2063`(2/3 목차 프롬프트), `:2432`(Critic 프롬프트),
      `MechanicalValidator.cs:69`(`RequiredConsolidatedHeaders`, L1 검증기).
      L1 실패 시 내미는 수정 템플릿(`MechanicalValidator.cs:1791`)까지 세면 리터럴은 5곳이다.
      **2/3 단계의 실질 기여가 H3/H4뿐인 이유가 여기 있다** — 3/3 프롬프트가 이미 네 H2를
      직접 지시하므로 목차가 그것을 다시 정해줄 필요가 없다. 목차 단계 존치 여부는 실측이 필요하다
- [ ] **`agent/` 직하 중복 `task-*.md`에서 `FirstOrDefault`가 열거 순서에 의존** —
      `InstructionBundleWriter.cs:318/410/498`. 진입점보다 나중에 쓰인 파일만 남기면 해결된다
- [ ] **계획서가 SQL 배치 위치를 섞어 말한다** — `AiService.cs:668`(`ConsolidatedPlanRules`),
      `:2275-2344`(단계 섹션 프롬프트)

  한 계획서가 같은 로직을 두고 C# 인라인 SQL과 신규 저장 프로시저를 함께 지시한다.
  어느 프롬프트도 어느 쪽이 기본인지 말하지 않기 때문이다. **고치기 전에 기준을 정하는
  것이 선행되어야 한다** — 어느 쪽을 기본으로 삼을지, 예외를 허용한다면 무엇을 근거로
  가를지. 그것 없이 프롬프트만 손대면 다음 회차에 반대 방향으로 쏠린다.

  출처: `2026-08-14-generated-bundle-contract-design.md` §제외·§남은 후속

- [ ] **계획서가 `batch.*` 모듈의 이름만 주고 본문을 주지 않는다** —
      `TaskFileComposer.cs:332-333`

  에이전트가 `Spec.md`에서 본문을 재구성해야 하는데, 이는 `MigrationInstructions.md`
  규칙 7(자리표시자 금지)과 긴장 관계다. **부분 완화된 상태다** — 지시서가 "회차 0은
  DDL과 골격까지, 업무 로직 본문은 해당 단계 회차가 채운다"고 공백의 주인을 정했다.
  남은 것은 계획서가 본문을 직접 주게 하는 쪽이고, 그러려면 단계 섹션 프롬프트를 고쳐야 한다.

  출처: `2026-08-14-generated-bundle-contract-design.md` §제외·§남은 후속

- [ ] **`BuildUnverifiedFeedback`의 폴더 규약이 4개 중 2개만 말한다** —
      `CodegenLoopPolicy.cs:73-77`. `JobProjectDirectoryNames`가 인정하는 밑줄 제거 변형
      2개가 문구에서 빠졌다. 거짓은 아니고 누락이 실패를 만들지 않는다

### 스키마 주장 검증 게이트 잔여

- [ ] **`ComputeFenceLineFlags`에 미닫힘 펜스 폴백이 없다** —
      `MechanicalValidator.cs:1174-1191`. `MarkdownSectionLocator.FindIndexOutsideFence`가
      의도적으로 갖는 폴백("오탐보다 미탐이 훨씬 나쁘다")의 반쪽만 복제했다.
      실무상 도달 불가에 가깝다 — 펜스가 홀수면 Markdig 헤더 검사가 먼저 떨군다
- [ ] **`~~~` 펜스를 두 구현 다 인식하지 않는다** — 서로는 일치하나 Markdig와는 다르다
- [ ] **`SuggestedPromptFix` 5번 블록이 검출 패턴과 일치한다** — Actor가 이 피드백을
      문서의 "검증 이력" 류 섹션에 옮겨 적으면 게이트가 자기 지시문에 다시 걸린다.
      확률은 낮고 회귀 테스트가 없다
- [ ] **RAG/청크 경로가 테이블 단위 substring 필터를 쓴다** — `AiService.cs:1085-1105`.
      완전성 문장도 붙지 않아 단일 권위가 컬럼 단위로만 성립한다. Stage 1 전용이고
      실제 섹션 생성은 `BuildSpMetadataTexts`를 쓰므로 현재는 무해하다

  출처: `2026-08-09-schema-claim-verification-gate-design.md` §남은 후속 1·2·4·6

### 헤더 계약 모순 검사

- [ ] **인정 문장이 어느 계약을 인정했는지 구분하지 못한다** —
      `MechanicalValidator.CheckHeaderContractContradiction`. 헤더 주석 블록에는
      Inner SP 말고 반환값 등 다른 계약도 있어서, 그 중 하나를 인정한 문장이
      "주석"+"불일치"만으로 이 검사를 통과시킨다. 코퍼스에 그 형태가 실재한다 —
      `UP_UTIL_STAT_PGCOLLECT_INS/docs/Spec.md:53`("반환값 헤더 주석의 계약은
      실제 구현과 일부 불일치합니다"). 그 SP는 EXEC가 0건이라 검사가 발화하지
      않아 현재는 무해하다. `HeaderContractTerms`를 내부 호출 지시어로 좁히는
      안은 기각했다 — 호출 대상을 이름으로만 지목한 정당한 인정 문장이 함께
      걸린다(테스트 `Validate_AcknowledgementSentenceWithDottedInternalCallIdentifier_ShouldPass`).
      근본 해법은 `SpecExpectations.HasInternalProcedureCall`을 bool에서 **호출
      대상 이름 목록**으로 바꾸고, 인정 문장이 그 이름 중 하나를 담았는지 보는 것

  출처: 2026-08-17 14개 SP 전수 재생성 실측

### 축 A 재감사 잔여 (2026-08-18)

- [ ] **`EXPECT_PROC`의 `PGName NOT IN` 9개 리터럴이 명세서에 없다** — 🟠, 직전
      감사에서도 났고 이번에도 잔존. `object_definition.sql:39`의 인라인 리터럴 9개
      (`PLCard`·`SamSungPay`·`SSGPayCard`·`KakaoPay`·`KakaoCard`·`impaymobile`·
      `NaverCard`·`ApplePay`·`TossCardAuth`)를 한 번도 열거하지 않고 "원천 사용 PG
      제외"로만 적는데, 같은 문서 `Spec.md:82`가 "원천 PG 목록"을 5개짜리 변수
      `@v_PLCardSettlePeriodPG`로 정의해 두어 5개로 읽힌다. 갱신 1은 그 변수를 쓰지
      않는다. 이행하면 4개 PG가 자동회수 대상에 잘못 편입된다
- [ ] **UPDATE 매핑 표 밖의 대상 한정 리터럴이 재료로 실리지 않는다** — 위 항목의
      일반형. `COMM_UPD` 문장 2의 `ABROADCHK = 1` 및 6개 PG 화이트리스트도 같은
      이유로 명세서에서 사라졌다(🟠). WHERE 최상위의 **값**은 DML 범위 표가 담지
      않는다(컬럼 이름만 담는다). 값까지 담으면 노이즈라는 판단이 있었으나, 대상
      집합을 정하는 `IN` 리터럴 목록은 예외로 다룰 근거가 두 SP에서 나왔다

  출처: `output/Jobs/POQSettleProc16/consistency/ConsistencyReport-AxisA-2026-08-18.md` §4
  설계: [집합 술어 재료](superpowers/specs/2026-08-18-set-predicate-material-design.md)
  (2026-08-18 작성. 두 항목 모두 이 설계 하나가 닫는다 — `IN`/`NOT IN` 리터럴 목록만
  재료로 삼고 스칼라 비교 474건은 노이즈로 제외한다)

### 정적 분석

- [ ] **`DependencyInfo.Type` 타입화** — `DependencyInfo.cs:12`가 여전히 `string`.
      문자열 가드가 아니라 타입 시스템으로 원시 판정을 차단한다. 근본적이지만
      직렬화·스냅샷 호환성까지 번진다
- [ ] **`DbMetadataService` 재귀 의존성 경로에 동작 테스트가 없다** — 라이브 DB가
      있어야 실행되어 커버되지 않는다. 이 경로에서 분류기 위임을 통째로 되돌리는
      편집이 있어도 지금은 테스트로 잡히지 않는다
- [ ] **`NormalizeQualifiedName`이 per-segment 정규화가 아니다** —
      `MechanicalValidator.cs:1321-1322`. `[DB].[dbo].[T]`가 `DB].[dbo].[T`가 된다.
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
      `ReSet.Cli/Program.cs:2002`. 식별자에 `.`이나 파일명 금지문자가 있으면 캐시 조회
      경로와 저장 경로가 갈라진다

---

## P3 — 인지됨, 영향 미미

- [ ] **`AGENTS.md`의 경고 개수가 낡았다** — `AGENTS.md:217`

  "경고가 **정확히 8건**(모두 `DbMetadataServiceTests.cs`의 기존 CS8600/CS8602)"이라
  적혀 있으나, 이 브랜치의 기준 커밋에서 `dotnet clean && dotnet build`로 다시 세면
  **9건**이다 — 기록된 8건에 `AiServiceTests.cs:681`의 `CS8604` 1건이 더 있다. 이 항목을
  이 계획이 만들지 않았고 `AGENTS.md` 수정은 이 계획의 쓰기 범위 밖이라 고치지 않고
  기록만 한다. 같은 체크리스트 바로 아랫줄(`:218`)이 스스로 경고하는 바로 그 위험이다
  — 낡은 숫자는 올바른 빌드에서 항목을 실패시켜 다음 사람이 체크리스트를 무시하도록
  길들인다. `:218`의 테스트 개수 항목은 이미 "하루 만에 네 번" 낡아 그 교훈으로 숫자
  자체를 지우고 이유를 설명하는 문장으로 바꿨는데, `:217`의 경고 개수는 아직 그 처방을
  받지 못했다 — 이번이 다섯 번째로 낡은 것이다.

  출처: Task 12 실측(`2026-08-17-axis-a-spec-fidelity-design.md` 구현 중 `AGENTS.md:217`
  대조, 2026-08-17)

- [ ] `ScoreReadability` 라벨이 사실과 다르다 — 두 Critic 모두 코드 가독성이 아니라
      Mermaid 다이어그램 문법을 채점한다(`VerificationDocumentFormatter.cs:52`).
      명세서에서도 틀렸다. 라벨 문자열이 테스트로 고정되어 있다
- [ ] 2/3가 빈 응답을 내도 일반 방어가 없다 — `:1851-1857`. 재수립 경로만 방어한다
- [ ] 프롬프트의 pseudo-XML 블록이 `<`, `>`, `"`를 이스케이프하지 않는다 —
      `AiService.cs:196-209`. 진짜 XML로 파싱하는 곳은 없다
- [ ] 모호성 오류가 충돌하는 **기대**를 나열하지 않는다 —
      `MechanicalValidator.cs:1238-1243`(`ResolveSectionBody` 마지막 분기).
      후보 **섹션**(`candidateSections`)은 이미 메시지에 찍는다. 빠진 쪽은
      `candidateExpectations`라, 후보 섹션이 하나뿐인데 같은 마지막 파트를 요구하는 UPDATE
      대상이 여럿이라 모호해진 경우에는 무엇과 충돌했는지 알 수 없다
- [ ] `SpecExpectationsWiringPolicyScanner`가 `this._validator`를 못 잡는다 —
      `:47`이 `IdentifierNameSyntax`만 본다. 현재 그런 사용은 없다
- [ ] SP 목록이 시작 시 1회만 로드되어 세션 중 DB 변경이 반영되지 않는다
- [ ] TUI 선택 목록이 객체 디렉터리 이름만 렌더링해 서로 다른 DB의 동명 프로시저가
      구분되지 않는다
- [ ] 비재귀 경로가 `DependencyAnalysisOrchestrator`로 통일되지 않았다 — 요청 모델과
      파이프라인 호출, 배치 모드를 함께 재배선해야 한다
- [ ] 낡은 줄번호 인용 1건 잔존 — `CodegenWorkflowOrchestrator.cs:193`의 `(:806)`.
      실제로 그 주석이 가리키려던 곳은 `:816`(`BuildAbortResult`)과
      `:838`(`CliFailureClassifier` 호출)이다. 나머지 5곳은 해소됐다.
      줄 번호 대신 멤버 이름을 쓰는 편이 낫다
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

## 실측이 필요한 것

기준을 정하는 문제가 아니라 **돌려 봐야 아는 것**이다. 위 정책 결정 표와 같은 이유로
체크박스를 달지 않는다 — 지금 할 수 있는 일이 없고, 열린 항목 수를 오염시킨다.

2026-08-14 생성 번들 계약 설계가 세 검사를 넣으면서 그 정확도를 자기 실측 하나
(POQSettleProc9)로만 확인했다. 다른 Job에서 어떻게 나오는지가 남았다.

| 항목 | 무엇을 봐야 하나 | 빗나갔을 때 |
|---|---|---|
| **② 인프라 객체 수집 목록의 과부족** | 67종은 POQSettleProc9 하나의 수치다. 다른 Job에서 `BatchInfraObjectCollector`가 무엇을 놓치고 무엇을 과하게 잡는지 | 과하면 회차 0이 필요 없는 객체를 만들고, 모자라면 SQL이 없는 테이블을 참조한다 |
| **⑦ 생략 주석 배너의 빈도** | 배너가 한 Job에 몇 건이나 뜨는지 | 수십 건이면 사람이 읽지 않게 된다. 그때는 패턴을 좁힐지, 반대로 차단으로 승격할지 다시 판단해야 한다 |
| **③ 유령 테이블 결함이 재생성으로 고쳐지는지** | 결함 메시지를 받은 모델이 실재하는 이름으로 바꾸는지, 이름만 바꿔 다른 유령을 만드는지 | 후자면 검사가 재생성만 태우고 아무것도 고치지 못한다 |

출처: `2026-08-14-generated-bundle-contract-design.md` §실측이 필요한 것

---

## 완료 기록 — 코드상 해소된 뒤 문서까지 닫은 것

**전부 반영 완료(2026-08-13, 2026-08-14·2026-08-16 재확인).** 각 설계 문서의 해당 항목에
취소선과 해소 근거를 달았다. 아래는 어디를 어떻게 고쳤는지의 기록이며, 새로 할 일은 없다.

### 2026-08-16 재검증에서 닫은 3건 (전부 P2)

- [x] **C# 아키텍처 규칙이 단일 어셈블리 스코프** — `8e4af04`가 `DataAccessPolicy.cs`의
      `ArchitectureTests` 스텁에 `Targets`(`:129-148`)를 넣어, 테스트 어셈블리가 참조하는
      `ReSet.Batch.*` 를 전부 훑게 했다. 네 규칙이 대상 0건으로 조용히 통과하던 경로가
      닫혔고, "하나도 없다"를 실패로 보는 판정은 조립 회차에만 켜진다.
      `c86d7b7`이 `AssemblyCompletenessTests`에도 같은 워밍업을 넣어 xUnit의 클래스 병렬
      실행 순서에 좌우되던 거짓 실패까지 함께 없앴다
- [x] **`StepLogicTests` 배치 위치가 어떤 지시서에도 없다** — `TaskFileComposer.cs:254-261`이
      이제 `tests/StepLogicTests{확장자}`를 경로째로 안내한다(C#·Java 공통, `TestFileExtension`).
      `MetadataExporter.cs:561/602`가 만드는 위치와 일치한다. `8d9ba62`·`37cd381`이
      "이 파일 자체는 지우지 말 것 / 내용을 `LogicTests_<단계코드>`로 복사할 것"까지 붙여,
      원래 문제였던 "어디에 있는지 모른다"와 그 과정에서 드러난 "어느 원본을 지우라는 건지
      모호하다"를 함께 닫았다
- [x] **`ArtifactChangeDetector.Snapshot`의 TOCTOU 플래키** — `69a080c`가 열거와 읽기
      사이에 사라진 파일을 건너뛰게 했다(`SnapshotFiles`로 목록 주입 지점을 갈라 테스트가
      그 동작을 고정한다). `b577f65`는 원인 제공자였던 `CodingEngineTests`가 공유 실행
      디렉터리를 스냅샷하지 않게 했다. **커밋 메시지가 밝힌 한계를 그대로 옮긴다** —
      두 테스트가 경합 자체를 재현하지는 못하므로, 이 수정은 원인 경로가 더는 던지지
      않는다는 것만 보이고 플래키가 사라졌음을 증명하지는 않는다

### 그 이전에 닫힌 것

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
