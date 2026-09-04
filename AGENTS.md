# 🤖 ReSet (**RE**verse engineering **SET**tlement) Agent Guidelines (AGENTS.md)

이 문서는 **SQL Server Stored Procedure Reverse Engineering Tool (ReSet (REverse engineering SETtlement))** 프로젝트를 분석하고, 수정하며, 확장하고자 하는 AI 에이전트를 위한 시스템 지침서입니다. 본 프로젝트의 아키텍처 정합성과 코드의 무결성을 유지하기 위해 다음 가이드라인을 반드시 준수하여 개발을 진행해 주십시오.

---

## 📌 프로젝트 개요 (Overview)

본 프로젝트는 SQL Server에 구현된 Stored Procedure(SP)를 재귀적으로 분석하여 비즈니스 기능 명세서(`Spec.md`)와 여러 SP 기반의 통합 배치 전환 계획서(`BatchMigrationPlan.md`)를 작성하는 .NET Core 기반 CLI/TUI 도구입니다.

- **핵심 목표**: 레거시 DB 비즈니스 로직(SP)을 효율적으로 역공학하여 현대적인 애플리케이션 아키텍처(C#, Java Spring Batch 등)로 마이그레이션하기 위한 설계 산출물을 자동 생성 및 검증하는 것입니다.
- **신뢰성 보장**: AI가 단순 생성만 하고 끝나는 것이 아니라 **3단계 신뢰성 검증 파이프라인**을 통해 마크다운 문법, AI 자가 교정, 인간 피드백을 수렴하여 고품질의 설계를 유도합니다.

---

## 🗺 어디를 만지면 무엇을 먼저 읽는가 (Routing)

클래스 목록은 이 문서가 갖고 있지 않습니다. 클래스별 역할은
[docs/architecture.md §2.2](./docs/architecture.md), 설계 근거는 각 클래스의 `<summary>`
주석과 [§4 핵심 아키텍처 메커니즘](./docs/architecture.md)에 있습니다. 여기에는 **무엇을
먼저 읽어야 하는가**만 둡니다 — 클래스를 여기 다시 나열하면 카탈로그가 이름만 바꿔
부활하고, 그것이 이 문서를 108KB로 만들었습니다.

| 만지는 대상 | 먼저 읽을 것 |
| :--- | :--- |
| 검증 파이프라인 — 재시도·구제 채택·목차 재설계 | `architecture.md §4.4` + 범주 4 |
| 프롬프트 캐시·중단점·토큰 비용 | `architecture.md §4.13` + 범주 2 |
| 지시서 번들·회차 단위 코드 생성 | `architecture.md §4.11` + 범주 6 |
| 단계 하한 검사·목차 보강 재료 | `architecture.md §4.12` + 범주 4 |
| 커버리지 맵·문장 단위 대조 판정 | `architecture.md §5.7` + 범주 4 |
| 코퍼스 단계 스윕·검사 발화량 측정 | `architecture.md §5.8` + 범주 4 |
| 정적 분석·SQL 객체 타입 판정 | `architecture.md §4.3` + `TypeClassificationPolicyTests` |
| 재귀 의존성 수집·Soft Fail | `architecture.md §4.1` + 범주 2 |
| AI 공급자 추가·CLI 제공자 | `architecture.md §4.5` + 범주 4 |
| 정합성 검증기(Validator) | `architecture.md §4.6` + 범주 5 |
| `Prd.md` 도출·귀속 검사 | `architecture.md §4.14` + 범주 4 |
| 취소 처리 | 범주 2 + `CancellationPolicyTests` |
| 프롬프트 문구·환각 차단 규칙 | `architecture.md §4.9` + 범주 7 |

---

## 🚨 에이전트 핵심 준수 규칙 (Development Rules)

모든 작업은 아래 기술된 안전성과 무결성 범주에 맞춰 엄격히 격리되어 진행되어야 합니다.

### 🛡️ 범주 1. 보안 및 크레덴셜 제약 (Security)
1.  **절대 비공개 API Key를 소스 코드나 [appsettings.json](./src/ReSet.Cli/appsettings.json)에 포함하여 커밋하지 마십시오.**
    *   로컬 개발용 API Key는 Git 추적 제외 대상인 `src/ReSet.Cli/appsettings.local.json`을 새로 생성하여 관리해야 합니다.
    *   이 파일은 API Key 전용이 아니라 `appsettings.json`의 **모든 키를 덮어쓸 수 있습니다**. 저장소 기본값은 보수적으로 두고(예: API provider, 재귀 분석 off) 개인 환경 설정은 이쪽에 두십시오.
    *   검증기(`ReSet.Validator.Cli`)는 이 파일에서 **`ApiKey`만** 가져갑니다. 파일을 통째로 병합하는 코드를 되살리지 마십시오 — 나중에 추가된 구성 소스가 이기므로 분석기 쪽 provider가 검증기를 덮어써, CLI provider를 쓰는 순간 검증기의 무인 배치가 `CliProviderBatchGuard`에 걸려 종료 코드 1로 죽습니다. (`ValidatorConfigurationTests.LoadConfiguration_DoesNotMergeTheCliProjectsLocalSettings`/`ApiKeyFallback_StillReadsOnlyTheApiKeyFromTheCliProject`가 소스 검사)

### ⚡ 범주 2. 예외 처리 및 안정성 (Stability & Soft Fail)
2.  **전방위적 소프트 페일(Soft Fail) 및 예외 격리 정책을 준수하십시오.**
    *   [DbMetadataService.cs](./src/ReSet.Core/Services/DbMetadataService.cs)의 권한·동적 SQL 조회 오류는 `Warnings`에 남기고 건너뛰십시오. [MetadataExporter.cs](./src/ReSet.Core/Services/MetadataExporter.cs)와 [CacheManager.cs](./src/ReSet.Core/Services/CacheManager.cs)의 IO 오류도 핵심 파이프라인과 격리하십시오.
    *   [SpExecutionService.cs](./src/ReSet.Validator.Core/Services/SpExecutionService.cs)의 연결·실행 오류는 테스트 케이스를 `FAIL`로 만들고 예외 메시지를 `ErrorCode`에 기록해 직렬화하십시오(`SpExecutionService_ShouldSoftFail_OnInvalidConnectionString`).
    *   [DependencyAnalysisOrchestrator.cs](./src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs)는 하위 실패만 `Failed`로 격리하고, 깊이 제한은 `SkippedDepth`, 미진입 외부 DB는 `SkippedExternal`, 활성 조회 실패는 `Failed`로 기록하며 최소 깊이 경로를 우선합니다(`DependencyAnalysisOrchestratorTests`).
    *   개별 SP 분석의 기동과 저장은 [DependencyAnalysisOrchestrator.cs](./src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs) 하나만 쓰십시오(참조분석을 꺼도 같습니다). CLI에서 파이프라인을 직접 부르거나 출력 경로를 손조립하지 말고 `OutputPathResolver`를 경유하십시오 — 손조립은 이름에 `.`이 있는 순간 캐시 조회 경로와 갈라져 산출물이 두 자리로 흩어집니다.
    *   실패 객체에는 명세서 링크를 만들지 마십시오. 성공 객체의 Critic 점수·`Thinking.md`를 보존하고, 카탈로그 표기 대소문자를 사용하며, 객체 키와 경로는 충돌 없이 인코딩하십시오(`AnalyzeAsync_SpecWriteFailureMarksChildFailedAndParentDoesNotLinkIt` 등).
    *   설정된 `OfflineSnapshotPath`가 없으면 DB 프롬프트로 폴백하지 말고 즉시 실패하십시오.
    *   취소 가능한 `await`의 광범위 `catch`에는 `when (ex is not OperationCanceledException)`을 적용하십시오(`CancellationPolicyTests`). 취소 뒤에는 후속 생성을 중단하되 완료 산출물은 보존하고 미분석 참조를 문서에 표시하십시오(`DependencyAnalysisOrchestratorTests`).
    *   SQL 객체 타입은 [SqlObjectTypeClassifier](./src/ReSet.Core/Services/SqlObjectTypeClassifier.cs)만 사용하십시오. 부분 문자열 판정이나 분류기 사본은 금지합니다(`TypeClassificationPolicyTests`).
3.  **AI API 응답 널 가드(TryGetProperty) 및 모델 파라미터 매핑을 준수하십시오.**
    *   AI 클라이언트의 JSON 응답은 `TryGetProperty`로 검사하고 필수 필드·오류 응답 누락 시 거절 사유를 담은 `InvalidOperationException`을 던지십시오(`ChatAsync_WithErrorResponse_ShouldThrowInvalidOperationException`, `ChatAsync_WithMissingCandidates_ShouldThrowInvalidOperationException`).
    *   AI 리뷰 호출은 `AiCallRetry`로 감싸고, 클라이언트는 HTTP 상태 코드와 CLI 실패 유형을 예외에 보존하십시오(`AiRetryPolicyTests`). 재시도를 소진한 실패는 `OperationCanceledException`을 상속하지 않는 `AiCallFailedException`으로 올려야 타임아웃이 사용자 취소로 둔갑하지 않습니다.
    *   모델별 전송 규격과 캐시 중단점은 기존 배선을 유지하십시오(`architecture.md §4.5`, `§4.13`).
    *   gpt-5 Responses의 비어 있지 않은 모든 `summary_text`를 누적하고 뒤의 빈 summary가 앞선 추론을 지우지 않게 하십시오(`ChatAsync_WithGpt5MixedReasoningSummaries_ShouldPreserveNonEmptyReasoningText`).
    *   Ollama effort별 temperature와 로컬 모델 페널티 주입은 기존 매핑을 유지하십시오(`ChatAsync_ShouldDiversifyTemperatureBasedOnEffort`, `architecture.md §4.5`).
    *   Gemma 4에만 `<|think|>`를 시스템 프롬프트 선두에 넣고, `</think>`와 `<|end of thought|>`를 모두 파싱해 추론이 명세서에 새지 않게 하십시오(`architecture.md §4.5`).
    *   `ollama-cloud`는 `OllamaClient(isCloud: true)`를 공유하되 `ProviderName`을 `Ollama Cloud`로 두고 `IsLocalProvider`에서 제외하며, 키는 요청마다 붙이십시오(`OllamaCloudClientTests`).
    *   OpenRouter는 전용 `OpenRouterClient`를 쓰고(`OpenAiClient`는 모델 ID의 `gpt-5`를 Responses API로 오분기) 로컬·CLI 분류에서 제외하며, 캐시 중단점은 가변 접미사가 있을 때만 블록으로 갈라 찍으십시오(`OpenRouterClientTests`, `IsLocalProvider_WithOpenRouter_ReturnsFalse`).
    *   AI 본문의 대화형 인사·요약과 응답 전체의 코드 블록 래핑을 금지하는 프롬프트를 유지하십시오. Mermaid 블록만 예외입니다.

### 🎨 범주 3. 인터페이스 및 Spectre.Console 예외 회피 (UI/UX)
4.  **TUI 인터페이스의 시각적 안정성 및 사용자 입력을 지원하십시오.**
    *   DB 메타데이터·AI 원문·경로 등 외부 문자열은 `Markup.Escape()` 후 출력하십시오.
    *   필수 경로가 없으면 재입력을 받고 `TextPrompt.ShowChoices(false)`로 슬래시 렌더링을 보호하십시오. Validator는 `output/Jobs/`의 Job 선택 메뉴를 제공하십시오.
    *   [ConsoleUserInteraction.cs](./src/ReSet.Cli/ConsoleUserInteraction.cs)는 설정 파일을 바꾸지 않고 서버·DB를 즉석 교체할 수 있게 하며, 배치 단계 선택은 순차 루프로 순서를 보존하십시오.
    *   단일 Actor의 상위 태스크에 모델·Effort를 표시하되 하위 단계에는 반복하지 마십시오(`architecture.md §5.6`). L1/L2 결함 출력 뒤에는 빈 줄을 두십시오.
    *   진행 태스크 완료·실패 시 원래 Description을 보존하십시오. 재귀 SP/UDF도 일반 진행 스코프에 위임하고 일반 콘솔 상태 출력과 혼용하지 마십시오.
5.  **TUI 비파괴식 Serilog 파일 로깅 및 마크업 자동 정화를 준수하십시오.**
    *   Serilog는 File Sink 전용으로 사용하고, 기록 전 Spectre 마크업을 제거하며 종료 시 `Serilog.Log.CloseAndFlush()`를 호출하십시오.

### ⚙️ 범주 4. 검증 오케스트레이션 및 파이프라인 흐름 (Verification Workflow)
6.  **3단계 검증 파이프라인의 역할 분리 및 L2 Actor-Critic을 운용하십시오.**
    *   L1 [MechanicalValidator.cs](./src/ReSet.Core/Services/MechanicalValidator.cs)는 필수 섹션, Anti-Shortcut Fast-Fail, Mermaid 린팅을 담당합니다. `CleanseMermaidCode`를 린팅 전에 적용하고 `CleansedMarkdown`은 성공 여부와 무관하게 최종 원문에 반영하십시오(`architecture.md §4.4.1`).
    *   하한 검사용 `ErrorCodes`의 빈 배열은 검증 불가이며(원본 SP 없는 단계만 예외), `TargetTables`는 `SpecTargetTableExtractor`의 쓰기 집합으로 채우고 `SchemaTables`와 합치지 마십시오(`MechanicalValidatorTests`, `SpecTargetTableExtractorTests`, `architecture.md §4.12`).
    *   스키마 주장은 DB 전체가 아니라 프롬프트에 실린 컬럼과 대조하십시오(`SchemaPromptColumnSelectorTests`, `SchemaClaimGateRegressionTests`). Mermaid의 `@@ERROR`는 허용합니다.
    *   L2 [AiService.cs](./src/ReSet.Core/Services/AiService.cs)는 `MaxL2Attempts` 안에서 보완하고 [CriticFeedbackLog.cs](./src/ReSet.Core/Services/CriticFeedbackLog.cs)의 최근 3라운드를 누적하십시오(`architecture.md §4.4.2`).
    *   로컬 분기는 `AiClientFactory.IsLocalProvider()`, CLI 분기는 같은 팩토리의 `IsCliProvider()`만 사용하십시오 — `PromptContextScope`의 Full/Narrow 판정도 이것을 부릅니다. 사본을 두면 `-cli`로 끝나지 않는 CLI 제공자가 Full에 남습니다. 확정된 범위가 필요하면 `IAiService.ContextScope`를 읽고 `ResolveMode`를 다시 부르지 마십시오. 분할 생성 진행도는 Stage 1·2를 1/4~4/4로 통합하고 Stage 1 추론도 `Thinking.md`에 누적하십시오.
    *   품질 임계치는 감쇄하지 마십시오. 최종 점수 미달 문서도 `[!CAUTION]` 배너와 점수·피드백을 붙여 보존하십시오. 종료 상태는 [VerificationOutcome.cs](./src/ReSet.Core/Models/VerificationOutcome.cs)의 네 값만 사용합니다(`architecture.md §4.4.4`).
    *   L3 [VerificationPipelineOrchestrator.cs](./src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs)만 미리보기·DB 역동기화를 제어하고 배치 모드만 자동 승인하십시오. Core는 UI에 의존하지 않습니다(`architecture.md §4.4`).
    *   신규 공급자는 [IAiClient.cs](./src/ReSet.Core/Services/IAiClient.cs)를 구현해 [AiClientFactory.cs](./src/ReSet.Core/Services/Clients/AiClientFactory.cs)에 등록하십시오.
    *   `claude-cli`·`codex-cli`·`agy-cli`는 `Command`만 사용하고 실패를 [CliFailureClassifier.cs](./src/ReSet.Core/Services/Clients/Cli/CliFailureClassifier.cs)로 보고하며 자동 폴백하지 마십시오. 응답 봉투의 토큰을 기록하되 미보고 항목을 0으로 채우지 마십시오([CliUsage.cs](./src/ReSet.Core/Services/Clients/Cli/CliUsage.cs)).
    *   Actor·Critic·Consolidator 중 하나라도 CLI provider면 [CliProviderBatchGuard.cs](./src/ReSet.Core/Services/Clients/Cli/CliProviderBatchGuard.cs)가 DB 연결 전에 차단합니다. `AiSettings:AllowCliProviderInBatch` 옵트인은 claude/codex만 열며 `agy-cli`는 항상 차단합니다(`CliProviderBatchGuardTests`).
    *   파서·검증기가 강제하는 상한·형식과 필수 필드(`ErrorCodes`, `MaxSteps` 상한 40, `LegacyProcedures` 등)는 프롬프트에도 명시하십시오. JSON 예시에만 등장하는 필드는 선택 사항으로 오해됩니다.
    *   Critic 프롬프트에도 분석과 같은 테이블 스키마·UDF DDL·AST 메타데이터와 대상 SP DDL을 포함하십시오([ReviewSpecificationAsync](./src/ReSet.Core/Services/AiService.cs)).
    *   Critic 프롬프트의 기계 확정 표 면제 블록은 [MachineConfirmedTables.cs](./src/ReSet.Core/Services/MachineConfirmedTables.cs)에서만 조립하고, 새 표는 카탈로그에 등록한 뒤 `CurrentCacheFormatVersion`도 함께 올리십시오(`MachineConfirmedTablesTests`, `architecture.md §4.8`, `§4.9`).
    *   캐시 버전 인상을 미룰 때는 선결 조건과 근거를 `architecture.md §4.8`에 남기십시오. 안 남기면 다음 세션이 그것을 미등록 결함으로 오판해 인상하고, 전건 재생성이 오탐을 안은 채 걸립니다.
    *   `AnalysisSettings:AnalyzeReferencedCodeObjects` 사용 시 SP/UDF마다 동일 L1/L2/L3와 캐시를 적용하되 외부 DB 테이블·뷰 상세와 링크드 서버는 제외하십시오(`architecture.md §4.1.1`).
    *   시스템 프롬프트는 영어를 원칙으로 하고 최종 출력·체크리스트 동작 지시에만 한국어 출력 조건과 영어 매칭 트리거를 사용하십시오. 메타데이터 밖 컬럼, DDL 밖 오류 상수, UNION/JOIN·오류 분기 축약을 금지하는 Anti-Shortcut 규칙을 유지하십시오.
    *   `GenerateBySplitAsync`의 첫 단계 캐시 워밍을 유지하고, 예외 재시도에만 지연을 적용하십시오(`RunConsolidatedPipeline_WarmsCacheBeforeFanningOut`, `RunConsolidatedPipeline_WhenStepGenerationThrows_DelaysRetryWithJitter`).
    *   `ValidateBatchStep`에는 스키마 카탈로그를 세 번째 인자로 넘기고 2인자 오버로드를 만들지 마십시오(`KnownTableWiringPolicyTests`).
    *   Critic 점수와 기준 점수의 대조는 [CriticScoreGate](./src/ReSet.Core/Services/CriticScoreGate.cs)만 사용하고 사본을 만들지 마십시오. 사본을 두면 불합격 배너의 미달 항목과 재시도 판정이 갈립니다(`CriticScoreGateTests`).
    *   `Prd.md` 귀속 검사(`PrdAttributionValidator`)는 `MechanicalValidator`에 합치지 마십시오 — 오라클이 다릅니다(`SpecExpectations`가 아니라 `Spec.md` 텍스트 하나). 캐시도 타지 않으므로 `CurrentCacheFormatVersion`을 올리지 마십시오. 이 검사가 확인하는 것은 근거 인용의 실재뿐이니 '검증됨'으로 서술하지 마십시오(`architecture.md §4.14`).

### 🔒 범주 5. 타겟 런타임 격리 및 리소스 정리 (Lifecycle & Sandbox)
7.  **타겟 러너 격리 및 모의 데이터(Mock Data) 적재 수명주기를 준수하십시오.**
    *   [CSharpReflectionRunner.cs](./src/ReSet.Validator.Core/Services/CSharpReflectionRunner.cs)의 `DbTransaction`은 항상 `Rollback()`하고 `ValueTask`·`ValueTask<T>`도 동적으로 await하십시오. Java 프로세스는 30초로 제한합니다.
    *   관계 시드 캐시와 [SandboxSeedingService.cs](./src/ReSet.Validator.Core/Services/SandboxSeedingService.cs)로 모의 데이터를 적재하고 테스트 뒤 자동 소거하십시오.

### 🔌 범주 6. 외부 코딩 에이전트 및 프로세스 제어 (External Agent & Codegen)
8.  **지시서 번들 생성 및 코딩 에이전트 CLI 프로세스 제어를 적용하십시오.**
    *   통합 배치만 진입점·공통 문서·단계 본문·회차별 지시서로 나누고 각 회차에는 해당 `task-*.md`만 연결하십시오. 폴더를 먼저 만들고 `docs/`·`src/`를 `output/Jobs/{JobName}/` 안에 격리하십시오. 단계 경계 하나라도 실패하거나 정화된 파일명이 충돌하면 부분 번들 없이 전체 단일 파일로 폴백하되 지침을 앞에 두십시오(`architecture.md §4.11`, `InstructionBundleWriterTests`, `PlanBoundaryResolverTests`).
    *   `agent/progress.json`은 도구가 원자적으로 쓰고 `agent/todo.md`는 여기서 렌더링하십시오. 읽기 실패 상태는 덮어쓰지 말고, AI 생성 이름은 파일명으로 쓰기 전에 정화하십시오.
    *   지시서의 SQL/ORM 경계와 계약 스텁은 [DataAccessPolicy.cs](./src/ReSet.Core/Services/DataAccessPolicy.cs)만 참조하십시오(`AgentContractStubTests`).
    *   개별 SP 분석 직후 자동 기동하지 마십시오. 스탠드얼론 재개는 `agent/MigrationInstructions.md`를 찾은 뒤 단일 문서/회차 번들을 판정하고, 번들 복원 실패 시 전체 Job으로 폴백하지 말고 거부하십시오.
    *   [ExternalCliCodingEngine.cs](./src/ReSet.Core/Services/ExternalCliCodingEngine.cs)의 대화형 모드는 부모 콘솔을 상속하고 취소 시 프로세스 트리를 종료하십시오. 공백 포함 프롬프트는 하나의 인자로 전달하십시오.
    *   `Arguments`와 `BatchArguments`를 분리하고 무인 모드에서 대화형 인자로 폴백하지 마십시오. 빈 `BatchArguments`는 거부하며 `{jobDir}`·`{specRoot}`만 열고 출력 루트 전체는 허용하지 마십시오(`CodingEngineTests`, `ArgumentTemplateResolverTests`).
    *   배치 `--job-name`은 L3를 건너뛰고 계획·번들 생성부터 외부 프로세스까지 연속 실행합니다.
    *   코드 생성은 0회차→단계 1..N→조립을 순차 실행합니다. 회차 실패는 기록 후 다음으로 진행하되 쿼터·인증·권한처럼 전역적인 실패면 중단하십시오.
    *   검증 대상을 못 찾은 회차는 실패로 닫고 재시도 상한 전에 사유를 지시서에 붙이십시오. 0회차는 산출물만 검사하고 조립 L2는 전 단계 통과 시에만 실행합니다(`CodegenStagedWorkflowTests`).
    *   회차별 코드→테스트→수정→리뷰→커밋의 L0 루프 뒤 L1·L2를 수행하고, 불일치 피드백은 해당 회차 지시서에 누적하십시오.
    *   회차별 테스트 파일명은 단계 코드로 시작하지 마십시오(`TaskFileComposerTests`).

### 🧹 범주 7. 메타데이터 정화 및 주석 보완 (Cleansing & Annotation)
9.  **메타데이터 정화 및 정책 문서 수립 가이드를 준수하십시오.**
    *   로컬 모델의 스키마 환각을 막기 위해 `*_MetadataCleansing.sql` 자동 생성은 비활성 상태로 유지하십시오. 수동으로 만든 파일이 실제로 있을 때만 승인 후 적용하며, 크로스 DB 파일은 DB 접두사로 구분하고 연결 대상이 아닌 DB에는 실행하지 마십시오.
    *   C# 보간 프롬프트의 `{}`는 `{{}}`로 이스케이프하십시오. 정산 정책서는 DDL 분기와 데이터 프로파일링을 결합해 지정된 5개 헤더를 따릅니다.
    *   CRUD·컬럼 매핑은 `외 다수`·`등`으로 줄이지 말고 물리 컬럼과 원천값을 1:1로 모두 적으십시오. UPDATE 매핑은 `SqlStaticParser.AstUpdateMappings`가 채우며 AI fill-in 방식으로 되돌리지 마십시오(`MechanicalValidatorTests.Validate_WhenAnExpectedUpdateColumnIsMissing_ShouldReportIt`).
    *   파라미터·컬럼 제약은 DDL의 타입·기본값만 근거로 쓰고 임의의 `NOT NULL`을 만들지 마십시오. 스키마 덤프는 `SchemaPromptColumnSelector`가 고른 참조·키·인덱스 컬럼으로 제한하십시오(`SchemaPromptColumnSelectorTests`).
    *   L1이 함께 보는 것과 프롬프트 스키마 표·`DB 배치`의 입력원은 기존 배선을 유지하십시오(`architecture.md §4.9`, `§4.12`).
    *   「집합 술어」 표는 최상위 `AND` 항 단위이고 「술어 원문」 칸은 DDL 원문 그대로여야 합니다 — 요약·번역·생략하지 마십시오(`architecture.md §4.12`).
    *   「집합 술어」 범위 칸의 `조인 ON T`는 WHERE 항과 같은 필터로, 외부 조인이면 짝이 되는 조건으로 서술하십시오(`architecture.md §4.12`).
    *   「참조 함수」 표에는 문장 칸이 없고 「호출 위치」 칸이 그 역할을 합니다(`architecture.md §4.12`).
    *   「잠금 힌트」와 「참조 함수」의 `IF n`을 가로질러 대조하지 마십시오 — 채번 조건이 달라 같은 번호가 다른 문장입니다(`architecture.md §4.12`).
    *   `[MACHINE NOTICE]`가 실린 문장은 산문으로 서술하되 기계 확정이 아님을 밝히고 네 표에 행을 만들지 마십시오(`architecture.md §4.12`).
    *   「실행 의미」 표에 종류를 더할 때는 `ExecutionSemanticsFacts.AllKinds`에도 함께 등재하십시오 — 빠뜨리면 Critic 면제를 잃습니다(`MachineConfirmedTablesTests`).
    *   표마다 문장 집합이 다른 것은 의도된 비대칭이니 맞추지 마십시오(네 표 중 「잠금 힌트」만 `IF n` 행과 범위 칸을 갖습니다). 표의 내용을 서술하기 전에 렌더러의 헤더 줄을 먼저 읽으십시오(`architecture.md §4.12`).
    *   통합 배치 계획은 다음 7대 제약을 지킵니다.
        1. SNAPSHOT 격리에서 `WITH (NOLOCK)`을 사용하지 않습니다.
        2. INSERT-only 롤백은 Shadow 백업 대신 단일 트랜잭션 롤백 또는 `DELETE WHERE [ChunkKey]` 보상으로 설계합니다. 규칙 본문에 T-SQL 트랜잭션 철자를 쓰지 않습니다(`ConsolidatedPlanRules_DropTheTsqlSpellingFromTheRewrittenRules`).
        3. Chunk Key는 실제 타겟 컬럼 또는 PK 해시만 쓰고 원본 필터·오류 코드를 보존하며, 각 청크 회차가 자기 트랜잭션으로 독립 커밋합니다.
        4. 체크포인트로 재시작 멱등성을 보장하고 Shadow 복원은 같은 범위를 먼저 `DELETE`한 뒤 삽입합니다.
        5. 실패한 문장은 부분 커밋을 남기지 않고 실패 지점의 원본 오류 코드를 기록합니다.
        6. 단계 로직은 타겟 언어 앱이 소유하고 신규 저장 프로시저를 만들지 않습니다 — `CREATE PROCEDURE`는 원본 인용일 때만 쓰며, 규칙은 의무만 정하고 트랜잭션 API는 정하지 않습니다(`ConsolidatedPlanRules_ForbidNewStoredProcedures`).
        7. 제어 흐름도 앱이 소유하므로 단계가 보내는 SQL은 자기 결과로 분기하지 않습니다 — `GOTO` 오류 라벨·`IF @@ERROR <> 0` 분기·`IF @@ROWCOUNT` 분기·`BEGIN TRY`/`END CATCH` 감싸기는 원본 인용에서만 허용합니다(`ConsolidatedPlanRules_ForbidSqlSideControlFlow`).
    *   `NOT IN`·`ISNULL` 복합 조건은 포함/제외와 NULL 치환 의미를 정확히 기술하십시오.
    *   Mermaid 연결 라벨에는 큰따옴표를 쓰지 않고 노드 텍스트에는 `@` 변수를 자연어로 바꾸십시오. `@@ERROR`만 전체 따옴표로 허용합니다(`PostProcessMarkdown_ShouldCleanseMermaidCode`).
    *   3부 식별자는 같은 인스턴스의 크로스 DB 참조이며 4부 Linked Server로 서술하지 마십시오.

### 🌳 범주 8. 버전 관리 및 작업 공간 제어 (Version Control & Workspace)
10. **모든 코드 변경 작업 시 `git worktree`를 적극 활용하십시오.**
    *   에이전트는 메인 브랜치(main/master)에 직접 커밋하거나 기존 워킹 디렉터리를 오염시키지 않도록 주의해야 합니다.
    *   기능 추가, 버그 수정, 구조 변경 등 코드 베이스를 수정해야 할 경우, 가급적 독립적인 `git worktree`를 생성하여 별도의 작업 공간에서 코드를 작성하고 검증(빌드 및 테스트)을 수행하십시오.
    *   작업 및 테스트가 성공적으로 완료된 후 변경 사항을 병합(Merge)하고, 작업이 끝난 워크트리는 안전하게 정리(Remove)하는 사이클을 유지하십시오.
    *   `.claude/worktrees/` 격리 세션(`EnterWorktree`)에서는 `git -C <main>`도 사용자 `!` 입력도 가드가 main 병합을 막습니다. `ExitWorktree(keep)`로 main 루트에 돌아간 뒤 `git merge --ff-only <branch>` → 테스트 → `git worktree remove` → `git branch -d` 순으로 마무리하십시오.
    *   워크트리에는 gitignore 대상인 코퍼스 재료 **셋**이 없습니다 — `output/`, `output.bak-2026-08-22/`, `output.bak-stage4-control-20260828/`. **셋 다** 심링크한 뒤 테스트하십시오(`.git/info/exclude`에 `output`·`output.bak-*`가 등록되어 있습니다).

        ```bash
        ln -s <메인 저장소>/output output
        ln -s <메인 저장소>/output.bak-2026-08-22 output.bak-2026-08-22
        ln -s <메인 저장소>/output.bak-stage4-control-20260828 output.bak-stage4-control-20260828
        ```

        일부만 걸면 안 됩니다. 두 계열이 코퍼스 루트를 다르게 해석해, **총 건너뜀 수가 줄어드는데도 다른 테스트가 꺼집니다.** 셋째(`stage4-control`)는 `ProcedureClosureCorpusTests`의 재료이고, `LegacyErrorCodeInventionCorpusTests`가 `RESET_SWEEP_ROOT`로 같은 트리에 자를 대 볼 수 있습니다.

        | 메인 저장소 **안**에 만든 워크트리 | 추출기·골든 계열 | `CoverageMapGoldenTests` 요구 2·3 |
        | --- | --- | --- |
        | 링크 없음 | 건너뜀 | 통과 (조상 탐색이 메인 저장소까지 올라가 재료 둘을 다 찾음) |
        | `output`만 | 통과 | **건너뜀** (탐색이 워크트리에서 멈추는데 거기엔 스냅샷이 없음) |
        | 둘 다 | 통과 | 통과 |

        `output/`만 거는 것은 한쪽을 살리면서 다른 쪽을 끕니다 — 건너뜀 수가 줄어 진전처럼 보이는 함정입니다. **건너뜀 0만이 전부 돈 것입니다.**

        **`output.bak-2026-08-22/`은 백업이 아니라 재생성할 수 없는 테스트 재료입니다.** `CorpusPaths.PriorEdition`이 상수로 들고 `CoverageMapGoldenTests`가 기준 세대로 씁니다 — 덮어쓰면 실패가 아니라 **건너뜀**이 되어 조용히 무력해집니다. 진짜 백업은 `output.bak-<작업>-<날짜>` 꼴로 뜨십시오.

        **링크를 건 워크트리에서 CLI 재생성을 돌리지 마십시오.** `appsettings.json`의 출력 경로가 cwd 상대(`./output`)라 공용 코퍼스를 직접 고칩니다. 테스트는 읽기만 하므로 안전하고 **재생성만** 문제입니다. 되돌릴 수도 없습니다 — `InstructionBundleWriter`가 이번 회차 산출이 모자라면 `verification/`·`steps/`를 디렉터리째 지웁니다(의도된 동작). 재생성 전에 스냅샷을 뜨고, 다른 세션에 알리십시오(코퍼스 스윕 전후 대조가 분모를 공유합니다).

        「반쯤」 상태는 `CorpusSetupGuardTests`가 **셋 다**에 대해 빨간불로 막습니다(2026-09-04까지는 앞의 둘만 지켰습니다). 계열이 왜 갈리는지, 그리고 이 규칙이 없어서 두 세션이 각각 어떻게 당했는지는 그 클래스 주석에 있습니다. (저장소 **밖**에 만든 워크트리는 조상 탐색도 실패하므로 건너뜀이 더 늘어납니다 — 위 표는 안쪽 워크트리 기준입니다.)

        **공유 체크아웃의 메인 디렉터리에서는 커밋 창구뿐 아니라 빌드 산출물(`bin/`·`obj/`)도 겹칩니다.** 둘 다 `.gitignore` 대상이라 `git status`가 깨끗해도 안전을 보장하지 않습니다 — 어느 커밋이 지금 `bin/`의 DLL을 만들었는지 알 수 없습니다. 다른 세션이 메인에서 장시간 실험 중이면 그 자리의 빌드·테스트가 산출물을 덮어써 귀속을 끊습니다. **게이트를 포함해 빌드·테스트는 격리 워크트리 안에서만 실행하십시오.**

---

## 🏃 에이전트 로컬 작업 커맨드

### 프로젝트 빌드 및 실행
```bash
# 종속성 복원 및 빌드
dotnet build

# CLI TUI 대화형 모드 실행
dotnet run --project src/ReSet.Cli

# CLI 특정 SP 분석 배치 자동화 실행
dotnet run --project src/ReSet.Cli -- --conn "Server=localhost;Database=Northwind;User ID=sa;Password=your_password;TrustServerCertificate=true" --sp dbo.CustOrderHist

# 코드 일치성 검증 대화형 TUI 모드 실행
dotnet run --project src/ReSet.Validator.Cli

# 소스코드 일치성 자동 검증 (L3 인간 개입 생략)
dotnet run --project src/ReSet.Validator.Cli -- --spec "./output/Jobs" --code "./output/Jobs" --batch

# 데이터 정합성 테스트 파라미터 설계 배치 모드
dotnet run --project src/ReSet.Validator.Cli -- --spec "./output/Jobs" --gen-inputs --batch

# 검증용 모의 테이블 데이터(Mock Data) 자동 생성 배치 모드
dotnet run --project src/ReSet.Validator.Cli -- --spec "./output/Jobs" --gen-mock-data --batch

# 레거시 DB 결과 데이터 수집 배치 모드 실행
dotnet run --project src/ReSet.Validator.Cli -- --exec-legacy --conn "Server=localhost;Database=Northwind;User ID=sa;Password=your_password;TrustServerCertificate=true" --batch

# 신규 마이그레이션 타겟 결과 데이터 수집 배치 모드 실행
dotnet run --project src/ReSet.Validator.Cli -- --exec-target --conn "Server=localhost;Database=Northwind;User ID=sa;Password=your_password;TrustServerCertificate=true" --batch

# 데이터 정합성 1:1 대조 배치 모드 실행
dotnet run --project src/ReSet.Validator.Cli -- --compare-data --batch
```

### 테스트 실행
```bash
dotnet test
```

---

## ✅ 에이전트 작업 완료 체크리스트 (Agent Checklist)

개발 에이전트는 코드 수정을 마치고 작업을 제출하기 전에 다음 항목을 직접 자가 검증해야 합니다.

- [ ] 컴파일 에러 0개, 경고 **0건**인지 확인했는가? 증분 빌드는 경고를 다시 보고하지 않아 이미 있던 경고도 0으로 보이므로 `dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"`로 세야 한다. (기대 개수를 적지 않는다 — 여기 「정확히 8건」으로 적혀 있던 줄이 `8875e9f`가 그 경고를 지운 뒤로도 낡은 채 남아 있었다.)
- [ ] `dotnet test` 명령어를 실행하여 **실패 0, 건너뜀 0**으로 모든 단위 테스트가 통과(Passed)하였는가? (기대 개수를 여기 적지 않는다 — 테스트를 하나 추가할 때마다 이 줄이 거짓이 되고, 낡은 숫자는 올바른 빌드에서 항목을 실패시켜 다음 사람이 이 체크리스트를 무시하도록 길들인다. 실제로 하루 만에 네 번 낡았다.)
- [ ] 워크트리라면 코퍼스 재료 **셋**을 심링크했는가? 일부만 걸면 다른 테스트가 대신 꺼지는데 총 건너뜀 수는 줄어 성공처럼 보인다(`CorpusSetupGuardTests`).
- [ ] 취소 가능한 `await`를 감싸는 `catch`에 `when (ex is not OperationCanceledException)` 필터를 달았는가? (`CancellationPolicyTests`가 자동 검사하며, 기준선 파일 `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`의 숫자는 고칠 때마다 함께 내려야 한다)
- [ ] AGENTS.md에 600바이트를 넘는 줄을 만들지 않았는가? 그런 줄은 규칙이 아니라 문단이다. (`DocumentationBudgetTests`가 자동 검사하며, 상한은 `tests/ReSet.Core.Tests/documentation-budget-baseline.txt`에 있다)
- [ ] 심볼(클래스·메서드·상수)을 지웠다면 `grep -rn "<지운 이름>" docs/`로 남은 서술을 함께 고쳤는가? 기계 검사로 대신할 수 없다 — 문서 전문 대조는 오탐 88%다(레거시 SQL·CTE·BCL 이름이 같은 백틱을 쓴다). 실측: `IsCandidateForAnchoredStatementCheck` 제거가 문서 네 자리를 남겼고 그중 하나는 캐시 인상 판단의 방향을 반대로 말했다.
- [ ] SQL 객체 타입을 `Contains("TABLE"/"VIEW"/"FUNCTION"/"PROCEDURE")`로 직접 판정한 곳이 없는가? (`SqlObjectTypeClassifier`에 위임해야 하며 `TypeClassificationPolicyTests`가 자동 검사한다)
- [ ] API Key 등 비공개 자격증명이 소스코드나 `appsettings.json`에 하드코딩되지 않고 `appsettings.local.json` 또는 로컬 환경 변수로 격리되었는가?
- [ ] DB 메타데이터, AI 결과 원문 등을 Spectre.Console TUI에 출력할 때 모든 출력 부에 `Markup.Escape()` 조치를 적용했는가?
- [ ] Stored Procedure 실행 및 외부 샌드박스 데이터 수집 시, DB 연결 실패 시 예외 격리(Soft Fail 및 DTO FAIL 상태 주입) 처리가 정상 적용되었는가?
- [ ] 신규 추가된 C# 타겟 러너 내 `DbTransaction`이 작업 결과와 관계없이 항상 `Rollback()` 되도록 누락 없이 명세했는가?
- [ ] 작업 완료 후 수정 및 추가된 모든 코드가 솔루션 컴파일 및 아키텍처 규칙을 위반하지 않는지 재검토했는가?

<!-- synced-through: 7ab3d10c -->
