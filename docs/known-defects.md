# 알려진 결함

`docs/superpowers/specs/`의 설계 문서가 후속으로 미룬 것 중, **한 문서만 읽어서는 알 수 없는
것**을 모은다. 심각도 순위(어느 것이 P0인가)와 반복 횟수(같은 것을 몇 개의 설계가 별건으로
미뤘는가)가 그것이다. 나머지는 각 설계 문서의 `§남은 후속`에 원본이 있다.

> **이 문서는 정기 재검증을 하지 않는다.**
>
> 2026-08-13 ~ 08-21에 전수 재검증을 세 번 돌린 결과가 근거다. 9일 · src 커밋 150건 동안
> 최초 39건 중 **36건이 그대로**였고, 닫힌 4건은 전부 다른 일을 하다 우연히 닫힌 것이다
> (닫은 커밋 6개 중 이 목록을 근거로 든 것이 하나도 없다). 그동안 목록은 39 → 47로 커졌고,
> 재검증 한 회차가 앵커 70여 개 대조와 클린 빌드를 요구했다. **비용은 회차마다 들고 산출은
> 0에 수렴한다.**
>
> 그래서 위쪽 「살아 있는 목록」만 유지하고, 아래 「알려진 한계」는 **시점 기록**으로 둔다.
> 한계 쪽 항목은 **그 자리를 건드리는 사람이 그때 확인한다.** 적힌 앵커를 검증 없이 믿지 마라.

**앵커 규약: `타입.멤버`를 쓰고 줄 번호를 쓰지 않는다.** 한 멤버 안에서 자리를 좁혀야 하면
그 자리에 실제로 있는 식별자나 조건식을 함께 적는다. 이 목록은 줄 번호로 세 번 낡았다 —
`MechanicalValidator`가 1,800줄대에서 4,700줄대로 커지는 동안 멤버 이름은 하나도 바뀌지 않았다.

**여기에 없는 것**

- **산출물 정합성 결함** — `docs/audit-defect-catalog.md`가 갱신 규약과 함께 소유한다.
  아직 안 닫힌 부류(P14 원본 사실 미확정 · P6 주석 보존표 누락 · P11 표↔산문 불일치 ·
  P12 재생성 진동 · P5 `CAST(money AS INT)` 절사 · 축 B B1–B8 재감사)는 그 문서 §5에 있다.
  **여기에 옮겨 적지 마라** — 닫힘 판정 기준이 다르다(카탈로그는 재감사 회차를 요구하고
  이 문서는 코드 앵커를 본다). 두 곳에 적으면 갈라진다.
- **각 설계의 상세한 배경** — 아래 항목의 `출처`가 가리키는 문서에 있다.

---

## 살아 있는 목록

### P0 — 실사용 피해가 즉시 발생

- [ ] **레거시 전체 Job 루프에 총 시도 상한이 없다** —
      `CodegenWorkflowOrchestrator.RunSelfHealingWorkflowAsync`의 `while (attempt <= maxAttempts)` 루프

  산출물 있음 + 매핑 성립 + L1/L2 매번 실패 조합이면 같은 메서드의
  `consecutiveNoArtifactRetries`와 `consecutiveUnverified` 두 연속 캡 어디에도 닿지 않는다.
  `MaxL2Attempts: "unlimited"`(= `maxAttempts`에 들어가는 `int.MaxValue`)에서
  **무인 배치가 끝나지 않는 유료 기동**이 된다.

  출처: `2026-08-09-silent-failure-closure-design.md` §후속 작업 1

- [ ] **통합 루프에 점수 임계값 강제가 없다** —
      `VerificationPipelineOrchestrator.RunConsolidatedPipelineAsync`의 `bestAttempt.TryRecord` 직후

  단일 객체 루프(`RunCodeObjectPipelineCoreAsync`)는 `overriddenHasDefects` 블록에서 5축을
  직접 검사해 `HasDefects`를 덮어쓰지만, 통합 계획서 루프에는 그 블록 자체가 없다.
  Critic이 낮은 점수와 함께 `HasDefects: false`를 내면 `검증 상태: 통과` 옆에 낮은 종합
  신뢰도가 나란히 찍힌다.

  출처: `2026-08-03-verification-annotation-cleanup-design.md`,
  `2026-08-03-cancellation-policy-design.md`

- [ ] **L2 리뷰 호출 재시도 인프라 부재** — 두 루프의 `if (!reviewSuccess)` 분기
      (`RunCodeObjectPipelineCoreAsync` 및 `RunConsolidatedPipelineAsync`)

  일시적 API 오류 한 번에 `break`하며 `_maxAttempts`가 남아 있어도 재시도하지 않는다.
  `RetryRescue`가 이전 회차 최고점을 구제해 완화할 뿐, 그 회차의 검증 자체는 포기된다.
  **5개 설계가 반복 기록한 최다 이월 항목**이며, 재시도 횟수·백오프·취소 전파·비용
  정책 결정이 선행되어야 한다.

  출처: `2026-08-01-verification-outcome-honesty`, `2026-08-03-cancellation-policy`,
  `2026-08-03-stage1-analysis-flow-hardening`, `2026-08-03-verification-annotation-cleanup`,
  `2026-08-03-verification-honesty-followups`

### 정책 결정이 선행되어야 하는 것

코드 변경 전에 기준을 정해야 한다. **`반복` 칸이 이 표의 존재 이유다** — 여러 설계가 같은
것을 각자 "별건"으로 미룬 횟수이고, 설계 문서 한 편만 읽어서는 이 숫자가 보이지 않는다.

| 항목 | 반복 | 요지 |
|---|---|---|
| **시도 간 진동 억제** | 3 | Actor가 매번 백지에서 재작성해 점수가 20점 이상 출렁인다. `IAiService` 인터페이스와 프롬프트를 함께 바꿔야 한다(이전 명세서를 넘겨 수정하게 하는 방향). 실행과 실행 사이의 진동은 별개이며 카탈로그 P12가 그것이다 |
| **합격 기준 정책** | 3 | 다섯 항목 전부가 기준을 넘어야 하는 현행 게이트 유지 vs 종합 점수 게이트 병행 |
| **조기 종료** | 1 | 종합 점수가 충분히 높으면 재시도 중단. 합격 기준 변경을 수반한다 |
| **구조화 출력(`--json-schema`)** | 1 | 세 CLI 모두 지원하며 Critic 채점 JSON 파싱을 견고하게 만든다. 다만 스키마 정의가 API 경로와 CLI 경로의 동작을 갈라놓는다 |
| **`ActorEffort: dynamic`의 CLI 동시 실행 제어** | 1 | dynamic을 쓰면 프로세스 3개가 동시에 뜨고 쿼터 소진이 빨라진다. `74d53ec`(2026-08-20)가 무인 배치의 CLI provider 차단을 `AiSettings:AllowCliProviderInBatch` 옵트인으로 열면서 이 위험이 도달 가능해졌다 — `CliProviderBatchGuard`는 경고만 남기고 동시 실행 자체는 제어하지 않는다 |
| **계획서의 SQL 배치 위치** | 1 | 한 계획서가 같은 로직을 C# 인라인 SQL과 신규 저장 프로시저로 함께 지시한다. 어느 프롬프트도 기본을 말하지 않기 때문이다. 기준 없이 프롬프트만 손대면 다음 회차에 반대로 쏠린다. 자리: `AiService`의 `ConsolidatedPlanRules` 상수와 `AiService.GenerateBatchStepSectionAsync`의 단계 섹션 프롬프트 |
| **B4 — 축 B 나머지 10건** | 1 | 레거시 불변식인지 여부를 `Spec.md`로 판정해야 판단이 선다. 축 A 재생성으로 기준값이 바뀌었으므로 재감사가 선행된다 |

### 해결 불가로 기록된 것

- codex-cli의 전역 `AGENTS.md` 주입
- codex/agy의 출력 절단 감지
- agy의 Windows 명령행 stdin 한계 — 명확한 예외로 알리는 데서 멈춘다
- **`BatchSourceWatermark`·`BatchImmutableLedgerBaseline`의 컬럼 확정** — 어느 원천을
  워터마킹하고 어느 원장을 기준선으로 잡는지에 따라 컬럼이 달라지는 **Job 형상** 객체다.
  ReSet이 정할 수 있는 사실이 아니므로 스키마·명명 규칙만 적용하고 DDL은 계획서에 맡긴다
  (`2026-08-18-axis-b-batch-skeleton-design.md` §7)
- **B5 `NOLOCK` 전면 제거 7건** — 전부 ⚪이고 "배치가 단독 실행되고 원천에 동시 커밋이
  없다"는 전제부 판단이다. 계약 결함이 아니다(같은 문서 §7)

### 돌려 봐야 아는 것

기준을 정하는 문제가 아니라 실측이 필요한 것이다. 지금 할 수 있는 일이 없으므로
체크박스를 달지 않는다. **감사 소관 실측(축 B 재감사·P5·`INS_EXTRA`)은 여기 없다** —
`audit-defect-catalog.md` §5에 있다.

| 항목 | 무엇을 봐야 하나 | 빗나갔을 때 |
|---|---|---|
| **인프라 객체 수집 목록의 과부족** | 67종은 POQSettleProc9 하나의 수치다. 두 번째 데이터 점이 있다 — POQSettlePrco20의 `task-00-bootstrap.md`가 19건을 싣는다. 그 19건이 계획서 SQL이 실제로 참조하는 것과 과부족 없이 맞는지는 아직 대조하지 않았다 | 과하면 회차 0이 필요 없는 객체를 만들고, 모자라면 SQL이 없는 테이블을 참조한다 |
| **생략 주석 배너의 빈도** | 배너가 한 Job에 몇 건이나 뜨는지. POQSettlePrco20은 통합 계획서 산출물이 없어(`docs/`에 `BatchMigrationPlan.md`뿐) 이 회차로는 못 잰다 | 수십 건이면 사람이 읽지 않게 된다. 그때는 패턴을 좁힐지, 차단으로 승격할지 다시 판단해야 한다 |
| **유령 테이블 결함이 재생성으로 고쳐지는지** | 결함 메시지를 받은 모델이 실재하는 이름으로 바꾸는지, 이름만 바꿔 다른 유령을 만드는지 | 후자면 검사가 재생성만 태우고 아무것도 고치지 못한다 |
| **함수 DDL 본문을 프롬프트에서 뺄지** | 동작 서술을 금지한 뒤에도 *호출하는 문장*을 정확히 쓰려면 본문이 필요할 수 있다. 빼면 토큰이 크게 줄지만 측정 없이 지울 일이 아니다 | 지나치게 빼면 "함수가 0을 반환하는 행은 갱신 대상에서 빠진다" 같은 호출부 서술이 무너진다 |

출처: `2026-08-14-generated-bundle-contract-design.md` §실측이 필요한 것,
`2026-08-20-udf-machine-contract-design.md` §5

---

## 알려진 한계 — 재검증하지 않음

**2026-08-21 시점의 기록이다.** 그 뒤로 확인하지 않았으므로 앵커도 판정도 낡을 수 있다.
이 자리를 건드리게 되면 그때 확인하라. 대부분 각자의 설계 문서에 원본이 있고, `출처`가
없는 항목은 **리뷰나 감사 실측에서 이 문서로 직접 들어온 것**이라 여기가 유일한 기록이다.

### 검증 파이프라인

- **`SpecHeader`에 인터페이스 점수 필드가 없다** — `ReSet.Cli/SpecHeaderReader.cs`의
  `SpecHeader` 레코드가 5개 키만 읽어, `VerificationDocumentFormatter.FormatVerifiedDocument`가
  YAML에 쓴 `인터페이스 점수`를 승인 화면이 무시한다(캐시 왕복에서는 살아남는다).
  **고칠 때 함정** — 필드를 추가하면 `ConsoleUserInteraction.RequestHumanReviewAsync`의
  `?? 10` 폴백 뒤에 놓여 여섯 번째 조작 만점 위험이 생긴다.
- **생성 호출 실패 재시도 0회** — 두 루프의 `if (!genSuccess || …)` 분기. 명세서 경로와
  계획서 경로의 공통 정책이라 한쪽만 고칠 수 없다.
  출처: `2026-08-05-batch-structure-redraft-design.md` ⑤
- **2/3가 빈 응답을 내도 일반 방어가 없다** — `RunConsolidatedPipelineAsync`가
  `DraftBatchPlanStructureAsync`의 반환을 곧바로 `PlanStructureEnricher.Enrich`에 넘긴다.
  재수립 경로만 방어한다.
- **`Task.WhenAll`이 첫 예외만 표면화한다** — `RunCodeObjectPipelineCoreAsync`의
  `Task.WhenAll(tasks)`와 `Task.WhenAll(reviewTasks)`. 로컬 프로바이더 병렬 분기에서
  `IOException`과 `OperationCanceledException`이 동시에 발생하면 필터가 통과해 취소가
  삼켜진다. 취소 정책 스캐너는 필터가 있으므로 잡지 못한다.
- **`ScoreReadability` 라벨이 사실과 다르다** — 두 Critic 모두 코드 가독성이 아니라
  Mermaid 문법을 채점하는데 `axisScoreLines`가 `가독성 점수`로 찍는다. 명세서에서도 틀렸고
  라벨 문자열이 테스트로 고정되어 있다.

### 배치 계획 생성

- **브레인스토밍 원문이 3/3에 전달되지 않는다** — `IAiService.GenerateConsolidatedBatchPlanAsync`
  시그니처에 자리가 없어, 아키텍처 판단(Tasklet/Chunk 선택 등)이 목차 제목에 살아남은
  만큼만 본문에 도달한다.
  출처: `2026-08-05-batch-structure-redraft-design.md` ①
- **목차가 S01의 `TargetTables`를 채우지 못한다** — `PlanStructureEnricher.RewriteTables`,
  `AiService.DraftBatchPlanStructureAsync`. Proc14 회차에서 결함 1건으로 실제 비용을 냈다
  (`87d6e30`이 별개 사안으로 남김). 보강기가 정적 분석의 쓰기 대상으로 `TargetTables`를
  통째로 교체하는데도 비는 단계가 생긴다 — **왜 그 단계만 비는지부터** 봐야 한다.
  출처: `2026-08-14-generated-bundle-contract-design.md` §제외
- **4개 필수 H2가 5곳에 하드코딩** — `AiService`의 `ConsolidatedPlanRules` 상수,
  `AiService.DraftBatchPlanStructureAsync`, `AiService.ReviewConsolidatedPlanAsync`,
  `MechanicalValidator`의 `RequiredConsolidatedHeaders`, `MechanicalValidator.BuildSuggestedPromptFix`의
  `IsConsolidated` 분기. **2/3 단계의 실질 기여가 H3/H4뿐인 이유가 여기 있다** — 3/3
  프롬프트가 이미 네 H2를 직접 지시하므로 목차가 다시 정해줄 필요가 없다.
- **계획서가 `batch.*` 모듈의 이름만 주고 본문을 주지 않는다** — `TaskFileComposer.AppendInfraObjects`.
  에이전트가 `Spec.md`에서 본문을 재구성해야 하는데 `MigrationInstructions.md` 규칙 7
  (자리표시자 금지)과 긴장 관계다. 지시서가 "회차 0은 DDL과 골격까지"로 공백의 주인을
  정해 부분 완화됐고, 남은 것은 단계 섹션 프롬프트를 고치는 쪽이다.
  출처: `2026-08-14-generated-bundle-contract-design.md` §제외·§남은 후속

### 정적 분석 / 프롬프트 계약

- **호출된 객체가 참조하는 컬럼이 호출자의 스키마 표로 접혀 올라오지 않는다** —
  `SqlStaticParser`의 `ReferencedColumnsPerTable`. `EXPECT_PROC`이 부르는 UDF
  `UF_GET_COLLECTYMD`가 읽는 `TPGCollectPeriodMst`의 8컬럼이 `EXPECT_PROC` 자신의 SQL
  본문에 없어 애초에 수집되지 않고, 그 결과 명세서가 실재하는 컬럼을 "스키마 표에 정의되지
  않았다"고 잘못 기록한다. **실측으로 원인이 뒤집힌 항목이다** — 원 추정("리졸버 결함")은
  반증됐고(UDF 자신의 `prompt-context.md`에는 8컬럼이 정상), `SchemaPromptColumnSelector.DetectOrphanedColumnKeys`를
  넓혀도 닫히지 않는다(그 검출기는 **키**가 병합 안 된 경우만 보는데 이 컬럼들은 키 자체가
  생기지 않는다). 닫으려면 하위 객체의 `ReferencedColumnsPerTable`을 상위로 접어 올리는
  별도 설계가 필요하다. 2026-08-20 참조 함수 기계 계약은 이 자리를 닫지 않는다 — 그 표는
  호출 사실과 링크만 싣는다.
  출처: `2026-08-17-axis-a-spec-fidelity-design.md` §설계 1.3
- **정확 일치 타입 테이블 2곳이 분류기 밖에 있다** — `DependencyAnalysisOrchestrator.TryParseCodeObjectType`,
  `MetadataExporter.NormalizeCodeObjectDdlFolder`. `"P"`/`"FN"`/`"TF"`는 두 테이블에서
  Procedure/Function이지만 `SqlObjectTypeClassifier`에서는 `Unresolved`이고,
  `AGGREGATE_FUNCTION`/`EXTENDED_STORED_PROCEDURE`는 반대다. 오늘 오작동하지 않는 것은
  실제 `Type` 값이 전부 `type_desc`에서 오기 때문이지 게이트가 막아서가 아니다.
  출처: `2026-08-09-type-classification-policy-design.md` §후속 4
- **내부 방어 가드의 경고가 프롬프트로 샌다** — `SqlStaticParser.RecordUpdateMapping`의
  `"내부 방어 가드 작동"` 경고가 `ControlFlowSummary`를 거쳐 `AiService.BuildSpMetadataTexts`에서
  `"식별된 제어 흐름 구조 요약 (IF/WHILE)"`이라는 어긋난 머리말 아래 실린다. 현재 호출
  그래프에서는 도달 불가하지만 `RecordDmlTarget` 계약이 바뀌면 열린다.
  출처: `2026-08-09-update-mapping-contract-design.md` §남은 후속 1
- **`CrudAnalysis` 분기에 INSERT fill-in 표가 없다** — `AiService.BuildSpecSectionPrompts`의
  `sectionType == "CrudAnalysis"` 분기. UPDATE는 `BuildUpdateMappingTemplateLines` 공유
  헬퍼로 두 경로가 정리됐으나 INSERT는 `BuildSpecificationPrompts`에만 있다. INSERT에는
  `CheckUpdateMappings`에 대응하는 L1 대조가 없어 오늘 실패를 만들지 않는다.
  출처: `2026-08-09-update-mapping-contract-design.md` §남은 후속 8
- **RAG/청크 경로가 테이블 단위 substring 필터를 쓴다** — `AiService.BuildChunkDeconstructionPrompts`의
  `ragSchemas` 조립이 `depFullName.Contains(cleanRefTable)`와 `cleanRefTable.Contains(dep.Name)`
  양방향 부분 문자열이라 이름이 겹치는 테이블을 함께 끌어온다. Stage 1 전용이고 실제 섹션
  생성은 `BuildSpMetadataTexts`를 쓰므로 현재는 무해하다.
- **`DependencyInfo.Type` 타입화** — 여전히 `string`. 문자열 가드가 아니라 타입 시스템으로
  원시 판정을 차단하는 쪽이 근본적이지만 직렬화·스냅샷 호환성까지 번진다.
- **`AliasTargetFinder`가 FROM 절 하위 트리 전체를 돈다** — `SqlStaticParser`의 중첩 클래스
  (`ResolveAliasWithinFromClause`가 생성). `ExplicitVisit`을 오버라이드하지 않아 기본 순회가
  자식까지 내려가, 중첩 서브쿼리가 바깥 대상과 같은 이름의 별칭을 쓰면 그것을 잡는다.
  근본 수정은 최상위 `TableReference`만 훑는 것. `549541a`가 별칭 바인딩 순서와 별칭 없는
  자리의 조기 결론은 고쳤으나 순회 범위는 그대로다.

### L1 기계 검증기

- **DML 범위 표·집합 술어 표의 "문장" 칸을 L1이 한 번도 검증하지 않는다** —
  `MechanicalValidator.CheckDmlScopeTable`과 `CheckSetPredicates`는 대상·WHERE 술어 컬럼·
  조인 키·리터럴 목록 등 값 칸은 대조하지만 `UPDATE N`/`DELETE N` 같은 "문장" 칸 자체는
  어느 검사도 읽지 않는다. 그 결과 채번 헬퍼(`AiService.BuildStatementOrdinals`)의 회귀 —
  "같은 줄 두 문장이 번호를 덮어쓰는" 결함이나 두 표가 다른 번호를 내는 정렬 붕괴 — 가
  L1을 그대로 통과한다. `AiServiceTests_Rich`의 단위 테스트가 지금은 잡지만, 그 테스트를
  지우면 L1은 구조적으로 알아채지 못한다.
  **함께 닫을 것** — `CheckSetPredicates`의 행 매칭(`matchingRows`를 고르는 `r.Split('|')`)은
  순진한 분할인데 같은 파일의 `ExtractSetPredicateLiteralCell`은 이미 `SplitTableRowCells`
  (이스케이프된 `\|`를 존중)로 바뀌어 있다. `'x|10|y'`가 `'x\|10\|y'`로 렌더되면 행 매칭
  쪽이 유령 칸(`10`)을 만들어 다른 사실과 거짓 매칭될 수 있다.
  근거: 2026-08-18 최종 브랜치 리뷰(FIX ROUND 3) — **이 문서가 유일한 기록**
- **인정 문장이 어느 계약을 인정했는지 구분하지 못한다** —
  `MechanicalValidator.CheckHeaderContractContradiction`과 `HeaderContractTerms`. 헤더 주석
  블록에는 Inner SP 말고 반환값 등 다른 계약도 있어서, 그 중 하나를 인정한 문장이
  "주석"+"불일치"만으로 검사를 통과시킨다. 코퍼스에 실재한다 — `UP_UTIL_STAT_PGCOLLECT_INS`의
  `Spec.md`("반환값 헤더 주석의 계약은 실제 구현과 일부 불일치합니다"). 그 SP는 EXEC가
  0건이라 오늘은 무해하다. **`HeaderContractTerms`를 내부 호출 지시어로 좁히는 안은
  기각했다** — 호출 대상을 이름으로만 지목한 정당한 인정 문장이 함께 걸린다(테스트
  `Validate_AcknowledgementSentenceWithDottedInternalCallIdentifier_ShouldPass`). 근본
  해법은 `SpecExpectations.HasInternalProcedureCall`을 bool에서 **호출 대상 이름 목록**으로
  바꾸고 인정 문장이 그 이름을 담았는지 보는 것.
  근거: 2026-08-17 14개 SP 전수 재생성 실측 — **이 문서가 유일한 기록**
- **`ComputeFenceLineFlags`에 미닫힘 펜스 폴백이 없다** —
  `MarkdownSectionLocator.FindIndexOutsideFence`가 의도적으로 갖는 폴백("오탐보다 미탐이
  훨씬 나쁘다")의 반쪽만 복제했다. 펜스가 홀수면 Markdig 헤더 검사가 먼저 떨궈 실무상
  도달 불가에 가깝다.
- **`~~~` 펜스를 두 구현 다 인식하지 않는다** — `ComputeFenceLineFlags`와
  `MarkdownSectionLocator` 어디에도 `~~~` 리터럴이 없다. 서로는 일치하나 Markdig와 다르다.
- **`SuggestedPromptFix` 5번 블록이 검출 패턴과 일치한다** — `BuildSuggestedPromptFix`.
  Actor가 이 피드백을 문서의 "검증 이력" 류 섹션에 옮겨 적으면 게이트가 자기 지시문에
  다시 걸린다. 확률은 낮고 회귀 테스트가 없다.
- **`NormalizeQualifiedName`이 per-segment 정규화가 아니다** — `[DB].[dbo].[T]`가
  `DB].[dbo].[T`가 된다. 대조 양쪽에 같은 변환을 적용해 오늘은 무해하다.
- **모호성 오류가 충돌하는 기대를 나열하지 않는다** — `ResolveSectionBody`의 마지막 분기가
  후보 섹션(`candidateSections`)은 찍지만 `candidateExpectations`는 빠뜨린다. 후보 섹션이
  하나뿐인데 같은 마지막 파트를 요구하는 UPDATE 대상이 여럿이라 모호해진 경우 무엇과
  충돌했는지 알 수 없다.

  위 4건 출처: `2026-08-09-schema-claim-verification-gate-design.md` §남은 후속 1·2·4·6

### 메타데이터 / 지시서

- **`AllowExternalDatabaseConnections`가 메타데이터 계층에 도달하지 않는다** —
  `DependencyAnalysisOrchestrator`의 편의 생성자와 `DbMetadataService.GetCodeObjectDetailsAsync`가
  `includeExternalCodeObjects: true`를 하드코딩한다. **부분 완화** — 재귀 탐색 쪽에는
  게이트가 있고(`DiscoverAsync`가 외부 DB 노드를 `SkippedExternal` 처리), `01a0d5b`가
  `appsettings.json` 기본값을 `false`로 되돌렸다. 남은 것은 조회 호출 자체다.
  출처: `2026-08-03-stage1-analysis-flow-hardening-design.md` §범위 밖
- **`{specRoot}`가 `<outputRoot>/Procedures`만 덮는다** — `ArgumentTemplateResolver.ResolveSpecRootDirectory`.
  `External/<db>/Procedures/`와 `Functions/`의 명세서는 코딩 에이전트가 볼 수 없다.
- **Job 이름에 `.`이 들어가면 지시서 안내와 게이트 탐색이 어긋난다** —
  `FileMappingService.ResolveMappings`의 `baseName.LastIndexOf('.')`가 마지막 점 앞을 버린다.
  진입점 파일명 경로는 여전히 성립한다.

  위 2건 출처: `2026-08-07-migration-instructions-split-design.md` §남은 후속

- **`agent/` 직하 중복 `task-*.md`에서 `FirstOrDefault`가 열거 순서에 의존** —
  `InstructionBundleWriter`의 `DescribeStep`·`DependenciesForStep`·`SpecPathForStep`.
  진입점보다 나중에 쓰인 파일만 남기면 해결된다.
- **`BuildUnverifiedFeedback`의 폴더 규약이 4개 중 2개만 말한다** —
  `CodegenLoopPolicy.BuildUnverifiedFeedback`이 `CustOrderHist`와 `CustOrderHist.Batch`만
  들어, `CodegenArtifactNaming.JobProjectDirectoryNames`가 인정하는 밑줄 제거 변형 2개가
  빠졌다. 거짓은 아니고 누락이 실패를 만들지 않는다.
- **`SaveMigrationPlanAsync`가 `EncodePathSegment`를 쓰지 않는다** — `ReSet.Cli/Program.cs`가
  `Path.Combine(outputDir, "Procedures", $"{schema}.{name}", "docs")`를 직접 조립해,
  식별자에 `.`이나 파일명 금지문자가 있으면 `OutputPathResolver.EncodePathSegment`를 거치는
  캐시 조회 경로와 갈라진다.

### 테스트 커버리지

- **`DbMetadataService` 재귀 의존성 경로에 동작 테스트가 없다** — 라이브 DB가 있어야 실행돼
  커버되지 않는다(테스트의 `GetCodeObjectDetails*` 호출은 전부 NSubstitute 목). 이 경로에서
  분류기 위임을 통째로 되돌려도 지금은 테스트로 잡히지 않는다.
- **뮤테이션 저항 없는 테스트 2건** —
  `PlanBoundaryResolverTests.FindUncoveredRanges_EmptyDocument_ShouldReturnNothing`(조기
  반환을 지워도 통과, 값싼 경계 방어로 **의도적 유지**),
  `VerificationPipelineOrchestratorTests.Pipeline_ShouldNotEnrichTablesWhenDefinitionsAreOmitted`
  ("보강 스킵"과 "보강했으나 결과가 빔"을 구별 못 함).
- **`SpecExpectationsWiringPolicyScanner`가 `this._validator`를 못 잡는다** —
  `member.Expression is not IdentifierNameSyntax receiver` 판정이 `IdentifierNameSyntax`만
  본다. 현재 그런 사용은 없다.

### 그 밖

- **프롬프트의 pseudo-XML 블록이 `<`, `>`, `"`를 이스케이프하지 않는다** —
  `AiService.BuildDeconstructionPrompts`와 `BuildSpecSectionPrompts`의 `<sp-source-ddl>`
  조립 지점. 진짜 XML로 파싱하는 곳은 없다.
- **낡은 줄번호 인용 1건 잔존** — `CodegenWorkflowOrchestrator.RunSelfHealingWorkflowAsync`의
  주석이 `(:806)`을 가리킨다. 가리키려던 곳은 같은 클래스의 `BuildAbortResult`와
  `BuildAbortReason`(`CliFailureClassifier` 호출)이다. 이 문서의 앵커 규약과 같은 처방을 쓰면 된다.
- **SP 목록이 시작 시 1회만 로드**되어 세션 중 DB 변경이 반영되지 않는다.
- **TUI 선택 목록이 객체 디렉터리 이름만 렌더링**해 서로 다른 DB의 동명 프로시저가
  구분되지 않는다.
- **비재귀 경로가 `DependencyAnalysisOrchestrator`로 통일되지 않았다** — 요청 모델과
  파이프라인 호출, 배치 모드를 함께 재배선해야 한다.
- **더티 리드 노출을 명세서가 적지 않는다** — 6회차 감사가 배포 구성 확인으로 등급을 🟡까지
  내렸으나 서술 부재는 그대로다. 이관 후 동시 실행 구성이 되면 위험이 따라간다.
- **`output/Jobs/POQSettleProc7/` 산출물 폐기 판단** (운영 결정).

---

## 닫힌 것

상세한 경위는 각 설계 문서의 해당 항목에 취소선과 함께 달려 있고, 커밋 메시지가 그 근거다.
여기서는 **무엇이 왜 닫혔는지만** 남긴다.

| 무엇 | 어떻게 |
|---|---|
| 축 A 재감사 🟠 2건 (`PGName NOT IN` 리터럴 · PG 화이트리스트) | 집합 술어를 기계 확정 재료로 승격. `DmlScopeExtractor.ExtractSetPredicates` → `AiService.BuildSetPredicateTableLines` → `MechanicalValidator.CheckSetPredicates`. 설계: `2026-08-18-set-predicate-material-design.md` |
| C# 아키텍처 규칙이 단일 어셈블리 스코프 | `8e4af04`가 `ArchitectureTests` 스텁에 `Targets`를 넣어 대상 0건 통과를 막고, `c86d7b7`이 워밍업으로 xUnit 병렬 순서 의존 거짓 실패를 없앴다 |
| `StepLogicTests` 배치 위치가 어느 지시서에도 없다 | `TaskFileComposer`가 `tests/StepLogicTests{확장자}`를 경로째 안내(`8d9ba62`·`37cd381`이 "원본을 지우지 말 것"까지 명시) |
| `ArtifactChangeDetector.Snapshot` TOCTOU 플래키 | `69a080c`·`b577f65`. **한계 그대로 옮김** — 두 테스트가 경합 자체를 재현하지 못하므로 원인 경로가 더는 던지지 않는다는 것만 보인다 |
| `AGENTS.md`의 경고 개수가 낡았다 | `d545f51`이 `AiServiceTests`의 `CS8604`를 없애 실제가 기록(8건)을 따라잡았다. 2026-08-21 클린 빌드로 확인 |
| 보강기·파서의 "유효한 블록" 판정 불일치 | `2ae7a2b`·`25319f6`·`933fb39`가 `BatchStepPlanParser.TryLocateStepsBlock` 공유로 닫음 |
| Claude 프롬프트 캐시 중단점 | `PromptCacheBreakpointPolicy` (두 번째 전송부터 찍어 1회차 잡의 캐시 쓰기 손실 없음) |
| `PlanBoundaryResolver`의 `allFound == true` 공백 | `acf5210`의 `AbsorbUncoveredRegions` |
| 레거시 전체 Job의 `nothingVerified` 무한 재시도 | `e1ccfbd`의 `MaxConsecutiveUnverifiedRetries`. **부분 해소** — 위 P0 ①의 다른 조합은 열려 있다 |
| 신뢰도 점수와 검증 커버리지의 분리 표기 | `VerificationCoverage` 모델과 포매터의 `coverageLine` |
| UPDATE 컬럼 매핑표 / `UPDATE … FROM` 자기참조 / `SET` 절 동시평가 | `2026-08-09-update-mapping-contract` |
| 명세서 재발 방지 검증 게이트 | `63483f2`가 L2 Critic이 아니라 **L1**에 `MechanicalValidator.CheckSchemaClaims`를 넣고 `ab6dd5b`가 코드 펜스 오탐을 닫음 |

### 이 목록 밖에서 닫힌 것

2026-08-18 ~ 08-21의 큰 작업들은 여기 등재된 적이 없다. 감사가 새로 찾은 결함에 대응한
것이고, 그 파이프라인은 `docs/audit-reports/` → `audit-defect-catalog.md` → 새 설계 문서다.
이 문서가 아니라 그쪽이 지금 일을 움직인다.

- **축 B 배치 골격 계약** — `BatchControlContract`·`StepInterfaceFacts` + L1 검사 6종
- **참조 함수 기계 확정 표** — 함수 동작 서술 전면 금지, 5회차 7/8 → 6회차 8/8 정합
- **추출기 결함 셋** — 자기참조 별칭(`88b0aa2`) · 집합 술어 `LEFT()` 좌변(`39050de`) ·
  의존성 이름 표기(`a1bbcfe`). 6회차 재감사가 셋 다 산출물에서 확인, 축 A 🔴이 0이 됐다
- **`GlobalStatementOrdinal`의 "갱신 0"** — 정규화의 값 유실을 캐시 형식 6에서 닫음
