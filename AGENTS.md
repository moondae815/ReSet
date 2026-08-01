# 🤖 ReSet (**RE**verse engineering **SET**tlement) Agent Guidelines (AGENTS.md)

이 문서는 **SQL Server Stored Procedure Reverse Engineering Tool (ReSet (REverse engineering SETtlement))** 프로젝트를 분석하고, 수정하며, 확장하고자 하는 AI 에이전트를 위한 시스템 지침서입니다. 본 프로젝트의 아키텍처 정합성과 코드의 무결성을 유지하기 위해 다음 가이드라인을 반드시 준수하여 개발을 진행해 주십시오.

---

## 📌 프로젝트 개요 (Overview)

본 프로젝트는 SQL Server에 구현된 Stored Procedure(SP)를 재귀적으로 분석하여 비즈니스 기능 명세서(`Spec.md`)와 여러 SP 기반의 통합 배치 전환 계획서(`BatchMigrationPlan.md`)를 작성하는 .NET Core 기반 CLI/TUI 도구입니다.

- **핵심 목표**: 레거시 DB 비즈니스 로직(SP)을 효율적으로 역공학하여 현대적인 애플리케이션 아키텍처(C#, Java Spring Batch 등)로 마이그레이션하기 위한 설계 산출물을 자동 생성 및 검증하는 것입니다.
- **신뢰성 보장**: AI가 단순 생성만 하고 끝나는 것이 아니라 **3단계 신뢰성 검증 파이프라인**을 통해 마크다운 문법, AI 자가 교정, 인간 피드백을 수렴하여 고품질의 설계를 유도합니다.

---

## 📂 프로젝트 구조 및 주요 파일 바로가기 (Key Code References)

에이전트는 코드 수정 시 다음 구성 요소를 참조하고 알맞은 디렉토리에 변경사항을 작성해야 합니다. 모든 클래스 참조 시 아래의 직접 링크를 활용하십시오.

### 1. Core 라이브러리: [ReSet.Core](./src/ReSet.Core)
*   **도메인 모델 ([Models](./src/ReSet.Core/Models))**
    *   [SpDefinition.cs](./src/ReSet.Core/Models/SpDefinition.cs): 분석된 SP 메타데이터(소스코드 DDL, 컬럼, 의존성 등)를 관리하는 루트 데이터 클래스.
        *   [SpStaticAnalysisResult](./src/ReSet.Core/Models/SpDefinition.cs): 테이블 CRUD, 임시 테이블, UDF 및 Linked Server 등 정적 분석 결과 구조를 홀딩하는 도메인 모델.
    *   [CodeObjectKey.cs](./src/ReSet.Core/Models/CodeObjectKey.cs): 데이터베이스·스키마·이름·유형(SP/UDF)을 대소문자 비구분으로 식별하여 재귀 그래프의 중복 분석과 순환을 차단하는 코드 객체 키.
    *   [CodeObjectAnalysisModels.cs](./src/ReSet.Core/Models/CodeObjectAnalysisModels.cs): 코드 객체 그래프의 노드 상태, 의존 간선 및 객체별 분석 결과를 표현하는 모델.
    *   [DependencyInfo.cs](./src/ReSet.Core/Models/DependencyInfo.cs): 재귀적으로 수집된 DB 개체(테이블, 뷰, 다른 SP 등) 의존성을 표현하는 모델.
    *   [ColumnInfo.cs](./src/ReSet.Core/Models/ColumnInfo.cs): 컬럼명, 데이터타입, PK/FK 정보, 한글 설명, 설명 누락 유무(IsDescriptionMissing) 및 Identity/DefaultValue 정보를 수집하는 모델.
    *   [TableIndexInfo.cs](./src/ReSet.Core/Models/TableIndexInfo.cs): 테이블 인덱스 메타데이터(인덱스명, 타입, Unique, PK 여부, 구성 컬럼)를 관리하는 모델.
    *   [AiResult.cs](./src/ReSet.Core/Models/AiResult.cs): AI 응답 내용(Content) 및 추론 텍스트(ThinkingText), 요청된 시스템/사용자 프롬프트 콘텍스트를 모아 관리하는 데이터 모델.
    *   [DbSnapshot.cs](./src/ReSet.Core/Models/DbSnapshot.cs): 오프라인 모드를 위한 데이터베이스 메타데이터 스냅샷 모델.
*   **비즈니스 서비스 ([Services](./src/ReSet.Core/Services))**
    *   [DbMetadataService.cs](./src/ReSet.Core/Services/DbMetadataService.cs): SQL Server 메타데이터(Extended Properties, DDL, 의존성 관계)를 DFS 재귀 탐색을 활용해 수집하는 인터페이스([IDbMetadataService.cs](./src/ReSet.Core/Services/IDbMetadataService.cs)) 구현체.
    *   [SqlStaticParser.cs](./src/ReSet.Core/Services/SqlStaticParser.cs): ScriptDom 라이브러리를 가동해 테이블 CRUD, 임시 테이블, 분기 들여쓰기 린팅, 동적 SQL, UDF 및 Linked Server 원격 참조를 정적으로 파싱하는 정적 분석기 서비스.
    *   [AiService.cs](./src/ReSet.Core/Services/AiService.cs): 수집한 정보를 프롬프트로 다듬어 AI 공급자에 분석 요청을 보내는 인터페이스([IAiService.cs](./src/ReSet.Core/Services/IAiService.cs)) 구현체. AST 참조 컬럼 분석 기반 스키마 필터링 기능 및 Ollama 최적화 구역별 순차 분할 생성 메소드(`GenerateSpecSectionAsync`), 통합 배치를 위한 다단계 브레인스토밍/구조화 설계(`BrainstormBatchPlanAsync`, `DraftBatchPlanStructureAsync`) 로직을 포함합니다.
    *   [IAiClient.cs](./src/ReSet.Core/Services/IAiClient.cs): AI 모델 간의 공통 텍스트 통신 계약 정의 인터페이스 및 프로바이더별 클라이언트 팩토리([AiClientFactory.cs](./src/ReSet.Core/Services/Clients/AiClientFactory.cs)).
    *   [MechanicalValidator.cs](./src/ReSet.Core/Services/MechanicalValidator.cs): Markdig 파서 및 Mermaid 린터를 활용해 산출물 뼈대 및 다이어그램 문법을 정적 검증하고, Mermaid 다이어그램 코드 자동 교정 및 표준화 정화기(`CleanseMermaidCode`)를 기동하는 클래스.
    *   [VerificationPipelineOrchestrator.cs](./src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs): 3단계 검증 파이프라인의 오케스트레이션을 담당. Ollama 구역별 순차 생성 및 피드백 기반 선택적 재생성, L1 자동 정화 마크다운 반영 오케스트레이션을 담당하며, 통합 배치 전환 계획 수립 시 3단계(Brainstorm ➔ Structure ➔ Finalize) Multi-Step Agentic Workflow 흐름을 제어합니다.
    *   [DependencyAnalysisOrchestrator.cs](./src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs): 루트 SP에서 발견한 하위 SP/UDF 코드 객체 그래프를 중복 없이 순회하고, 객체별 검증 파이프라인 실행·실패 격리·아티팩트 저장을 조율합니다.
    *   [SpecificationDocumentFormatter.cs](./src/ReSet.Core/Services/SpecificationDocumentFormatter.cs): 루트 및 재귀 SP/UDF 명세서에 동일한 YAML 신뢰도 헤더와 NOTE 메타데이터(작성일시·분석 AI 정보·최종 Critic 점수)를 렌더링합니다.
    *   [OutputPathResolver.cs](./src/ReSet.Core/Services/OutputPathResolver.cs): 현재 DB와 외부 DB를 구분해 객체별 명세서, 표준 DDL, 의존성 매니페스트의 안전한 출력 경로를 계산합니다.
    *   [SpecificationLinker.cs](./src/ReSet.Core/Services/SpecificationLinker.cs): 성공한 직접 참조 객체에만 상대 `Spec.md` 링크를 추가하고, 실패·외부 DB·깊이 제한 상태는 사유로 표기합니다.
    *   [MetadataExporter.cs](./src/ReSet.Core/Services/MetadataExporter.cs): 원본 DB 메타데이터를 JSON, Raw 프롬프트 마크다운(`raw/prompt-context.md`), 개별 DDL/MD 파일 및 테이블 스키마 파일(`raw/ddl/*.md`) 등으로 보존합니다. 재귀 코드 객체 분석에서는 객체별 표준 DDL과 의존성 매니페스트를 내보내며, `Reference` 또는 `PortableBundle` 모드에 따라 참조 SP/UDF DDL 사본을 제어합니다. 외부 코딩 에이전트용 마이그레이션 지시서 번들 및 체크리스트(`agent/MigrationInstructions.md`, `agent/todo.md`)도 생성합니다.
    *   [OfflineDbMetadataService.cs](./src/ReSet.Core/Services/OfflineDbMetadataService.cs): `DbSnapshot`을 메모리에 로드하여 DB 연결 없이 오프라인으로 메타데이터를 제공하는 인터페이스(`IDbMetadataService`) 구현체.
    *   [SnapshotManager.cs](./src/ReSet.Core/Services/SnapshotManager.cs): 온라인 DB로부터 메타데이터를 추출하여 JSON 스냅샷으로 내보내거나(`ExportSnapshotAsync`) 오프라인 파일에서 다시 불러오는(`ImportSnapshotAsync`) 스냅샷 관리 서비스.
    *   [LocalAiConsolidator.cs](./src/ReSet.Core/Services/LocalAiConsolidator.cs): 로컬 모델 환경에서 분할 생성 시 여러 청크(Chunk)로 추출된 논리 JSON 결과들을 단일 `DeconstructedSpLogic` 객체로 병합하는 병합기.
    *   [CacheManager.cs](./src/ReSet.Core/Services/CacheManager.cs): SHA-256 해시 기반 로컬 증분 분석 글로벌 캐싱 및 레거시 자동 병합 마이그레이션 서비스 구현체.
    *   AI 응답 수집 및 로그 격리: AI 클라이언트 호출 결과에서 추출된 추론(Thinking) 텍스트는 수집 후 TUI 화면을 오염시키지 않도록 `Log.Verbose` 또는 파일 전용 로그에만 기록되게 하고, 기본 실행 수준에서는 실시간 노출을 차단하여 TUI 화면 깨짐을 원천적으로 차단하십시오.
    *   [ExternalCliCodingEngine.cs](./src/ReSet.Core/Services/ExternalCliCodingEngine.cs): CLI 기반 외부 에이전트 프로세스(Claude, agy, codex 등) 기동 및 콘솔 상속 연동 구현체.
    *   [IMultiProgressScope.cs](./src/ReSet.Core/Services/IMultiProgressScope.cs): 멀티태스크 진행률 상황 보고를 위한 추상 인터페이스.
    *   [NullProgressScope.cs](./src/ReSet.Core/Services/NullProgressScope.cs): 유닛 테스트 및 무인 모드 등에서 UI 미출력을 보장하고 NullReferenceException을 막는 방어적 널 객체 구현체.
    *   [SettlementPolicyService.cs](./src/ReSet.Core/Services/SettlementPolicyService.cs): DDL 상수 분석 및 DB 마스터 데이터 프로파일링을 활용한 통합 정산 정책서 생성 서비스 인터페이스([ISettlementPolicyService.cs](./src/ReSet.Core/Services/ISettlementPolicyService.cs) 포함).

### 2. CLI 실행 엔트리: [ReSet.Cli](./src/ReSet.Cli)
*   [Program.cs](./src/ReSet.Cli/Program.cs): CLI 진입점이자 TUI 메뉴 제어 및 흐름 오케스트레이션을 담당합니다.
*   [ConsoleUserInteraction.cs](./src/ReSet.Cli/ConsoleUserInteraction.cs): TUI와 사용자 간의 인터랙션 콘솔 처리 및 DB 동기화 여부 확인(ConfirmMetadataSyncAsync)을 정의한 구현체.
*   [ValidationUiProxy.cs](./src/ReSet.Cli/ValidationUiProxy.cs): 검증기(Validator)의 L1/L2/L3 요약 보고서 등을 Spectre.Console을 활용하여 TUI에 렌더링하는 브릿지 인터페이스 구현체.

### 3. 코드 검증 Core 라이브러리: [ReSet.Validator.Core](./src/ReSet.Validator.Core)
*   **추상화 및 도메인 모델 ([Abstractions](./src/ReSet.Validator.Core/Abstractions), [Models](./src/ReSet.Validator.Core/Models))**
    *   [IValidatorPlugin.cs](./src/ReSet.Validator.Core/Abstractions/IValidatorPlugin.cs): C#([CsValidatorPlugin.cs](./src/ReSet.Validator.Core/Plugins/CsValidatorPlugin.cs)), Java([JavaValidatorPlugin.cs](./src/ReSet.Validator.Core/Plugins/JavaValidatorPlugin.cs)) 등 언어별 L1 정적 구조 및 명칭 검증을 구현하는 플러그인 인터페이스.
    *   [IRuntimeRunner.cs](./src/ReSet.Validator.Core/Abstractions/IRuntimeRunner.cs): 타겟 런타임 코드 실행을 위한 인터페이스 규격 정의.
    *   [IValidationUserInterface.cs](./src/ReSet.Validator.Core/Abstractions/IValidationUserInterface.cs): 검증기 TUI 사용자 인터랙션을 추상화한 인터페이스.
    *   [L1ValidationResult.cs](./src/ReSet.Validator.Core/Abstractions/L1ValidationResult.cs): L1 정적 구문 검증 결과를 담는 모델.
    *   [ValidationResult.cs](./src/ReSet.Validator.Core/Models/ValidationResult.cs): 검증 대상의 L1/L2/L3 전체 상태를 관리하는 데이터 모델.
    *   [MockDataDto.cs](./src/ReSet.Validator.Core/Models/MockDataDto.cs): 기획된 관계형 모의 데이터를 로컬 및 메모리에 들고 있기 위한 데이터 모델.
    *   [GapReport.cs](./src/ReSet.Validator.Core/Models/GapReport.cs): L2 AI 의미론적 Gap 분석 결과 구조 데이터 모델.
    *   [RunnerDtos.cs](./src/ReSet.Validator.Core/Models/RunnerDtos.cs): 타겟 런타임 실행기의 입출력 및 실행 결과를 담는 DTO 모음.
    *   [ValidatorConfig.cs](./src/ReSet.Validator.Core/Models/ValidatorConfig.cs): 검증기 실행 설정을 바인딩하는 구성 모델.
*   **검증 비즈니스 서비스 ([Services](./src/ReSet.Validator.Core/Services))**
    *   [CodegenWorkflowOrchestrator.cs](./src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs): 외부 코딩 에이전트(Actor)와 코드 검증기(Critic) 간의 자가 수정 워크플로우 루프를 전담하는 독립 오케스트레이터.
    *   [CodeVerificationOrchestrator.cs](./src/ReSet.Validator.Core/Services/CodeVerificationOrchestrator.cs): L1(정적) -> L2(AI Gap판정) -> L3(사용자 승인)을 단방향으로 조율하는 코드 검증 오케스트레이터 (루프 기능 제외).
    *   [FileMappingService.cs](./src/ReSet.Validator.Core/Services/FileMappingService.cs): 마이그레이션된 소스 파일과 통합 작업 계획서(`BatchMigrationPlan.md`)를 스캔하여 1:1로 매핑하고 경로를 보정하는 서비스.
    *   [ValidatorAiService.cs](./src/ReSet.Validator.Core/Services/ValidatorAiService.cs): AI에게 설계서와 소스코드를 전달하여 의미론적 일치성을 검사하고 GapReport 구조로 파싱하는 서비스.
    *   [SpExecutionService.cs](./src/ReSet.Validator.Core/Services/SpExecutionService.cs): SQL Server DB에서 Stored Procedure를 동적으로 실행하고 결과를 JSON으로 덤프하는 서비스.
    *   [SandboxSeedingService.cs](./src/ReSet.Validator.Core/Services/SandboxSeedingService.cs): 모의 데이터를 샌드박스 DB에 적재(Insert)하고 실행 후 정리(Delete)하는 수명주기 서비스.
    *   [CSharpReflectionRunner.cs](./src/ReSet.Validator.Core/Services/CSharpReflectionRunner.cs): 마이그레이션된 C# DLL 리플렉션 로드(Task/ValueTask 비동기 대기) 및 DbTransaction 롤백 자동 격리 실행기.
    *   [JavaProcessRunner.cs](./src/ReSet.Validator.Core/Services/JavaProcessRunner.cs): Java JAR/클래스를 외부 프로세스로 기동하여 stdin/stdout JSON 통신을 수행하는 격리 실행기.
    *   [DataComparisonService.cs](./src/ReSet.Validator.Core/Services/DataComparisonService.cs): 레거시 vs 타겟 JSON 데이터의 행 수, 컬럼 타입, 개별 값을 1:1 대조하여 마크다운 리포트 생성하는 서비스.

### 4. 코드 검증 CLI 실행 엔트리: [ReSet.Validator.Cli](./src/ReSet.Validator.Cli)
*   [Program.cs](./src/ReSet.Validator.Cli/Program.cs): 검증기 CLI 진입점.
*   [ConsoleUserInteraction.cs](./src/ReSet.Validator.Cli/ConsoleUserInteraction.cs): TUI 경로 입력 대화창 및 결과 패널 렌더링.

### 5. 단위 테스트 프로젝트: [ReSet.Core.Tests](./tests/ReSet.Core.Tests)
*   **핵심 기능 및 연동 검증 테스트 ([Tests](./tests/ReSet.Core.Tests))**
    *   [SqlStaticParserTests.cs](./tests/ReSet.Core.Tests/SqlStaticParserTests.cs): ScriptDom 파서 동작 및 CRUD 분류, 다단계 들여쓰기 린팅, 동적 SQL, UDF/Linked Server 감지 검증.
    *   [ClaudeClientTests.cs](./tests/ReSet.Core.Tests/ClaudeClientTests.cs), [OpenAiClientTests.cs](./tests/ReSet.Core.Tests/OpenAiClientTests.cs), [OllamaClientTests.cs](./tests/ReSet.Core.Tests/OllamaClientTests.cs): AI 클라이언트별 API 전송 구조 및 페이로드 널 가드/TryGetProperty 구문 안전성 검증.
    *   [JavaProcessRunnerTests.cs](./tests/ReSet.Core.Tests/JavaProcessRunnerTests.cs): Java 프로세스 타임아웃(30초) 및 stdin/stdout 스트림 격리 실행 검증.
    *   [SandboxSeedingServiceTests.cs](./tests/ReSet.Core.Tests/SandboxSeedingServiceTests.cs): 모의 데이터 샌드박스 DB 적재 및 라이프사이클 소거 검증.
    *   [CodeVerificationOrchestratorTests.cs](./tests/ReSet.Core.Tests/CodeVerificationOrchestratorTests.cs): L1/L2/L3 오케스트레이션 및 자가 보완 루프 검증.
    *   [ValidatorAiServiceTests.cs](./tests/ReSet.Core.Tests/ValidatorAiServiceTests.cs): AI 응답 파싱 및 L2 Gap 분석기, 마크다운 코드 블록 정제 무결성 검증.
    *   [DataComparisonServiceTests.cs](./tests/ReSet.Core.Tests/DataComparisonServiceTests.cs): 레거시/타겟 JSON 결과값 1:1 대조 및 예외(JsonException) 핸들링 검증.
    *   [DependencyAnalysisOrchestratorTests.cs](./tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs), [SpecificationLinkerTests.cs](./tests/ReSet.Core.Tests/SpecificationLinkerTests.cs), [OutputPathResolverTests.cs](./tests/ReSet.Core.Tests/OutputPathResolverTests.cs): 재귀 SP/UDF 그래프의 중복 제거·실패 격리, 성공 대상 링크 및 객체별 출력 경로를 검증.

---

## 🚨 에이전트 핵심 준수 규칙 (Development Rules)

모든 작업은 아래 기술된 안전성과 무결성 범주에 맞춰 엄격히 격리되어 진행되어야 합니다.

### 🛡️ 범주 1. 보안 및 크레덴셜 제약 (Security)
1.  **절대 비공개 API Key를 소스 코드나 [appsettings.json](./src/ReSet.Cli/appsettings.json)에 포함하여 커밋하지 마십시오.**
    *   로컬 개발용 API Key는 Git 추적 제외 대상인 `src/ReSet.Cli/appsettings.local.json`을 새로 생성하여 관리해야 합니다.

### ⚡ 범주 2. 예외 처리 및 안정성 (Stability & Soft Fail)
2.  **전방위적 소프트 페일(Soft Fail) 및 예외 격리 정책을 준수하십시오.**
    *   **DB 메타데이터 수집**: [DbMetadataService.cs](./src/ReSet.Core/Services/DbMetadataService.cs)의 스키마 권한 누락 또는 동적 SQL 의존성 탐색 과정의 쿼리 오류 시 프로세스를 중단(`throw`)하지 마십시오. 경고 목록(`Warnings`)에 기록하고 소프트 스킵 처리해야 합니다.
    *   **원천 데이터 파일 덤프**: [MetadataExporter.cs](./src/ReSet.Core/Services/MetadataExporter.cs)의 디스크 쓰기 오류 등이 발생하더라도 핵심 산출물은 안전하게 보존되도록 에러 핸들러로 감싸야 합니다.
    *   **정합성 검증 DB 실행**: [SpExecutionService.cs](./src/ReSet.Validator.Core/Services/SpExecutionService.cs)의 Legacy SQL 실행 수집 시 연결 실패나 쿼리 수행 오류가 나면 크래시하지 말고, 결과 DTO의 테스트 케이스를 `FAIL`로 처리하고 예외 메시지를 `ErrorCode` 필드에 기재하여 직렬화 내보내야 합니다.
    *   **캐싱 및 서브 시스템**: [CacheManager.cs](./src/ReSet.Core/Services/CacheManager.cs)의 글로벌 해시 캐시 조작 및 레거시 마이그레이션(MigrateLegacyCaches) 파일 복사 시 발생하는 모든 IO 예외는 try-catch로 격리하여 메인 파이프라인 중단을 예방하십시오.
    *   **재귀 코드 객체 분석**: [DependencyAnalysisOrchestrator.cs](./src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs)에서 하위 SP/UDF의 메타데이터·분석·`Spec.md` 저장 실패는 해당 노드만 `Failed` 상태와 사유로 남기고 다른 객체 분석을 계속해야 합니다. 깊이 제한 객체는 `SkippedDepth`로, 크로스 DB 분석이 꺼져 있어 진입하지 않은 다른 DB 객체는 `SkippedExternal`로 표기하십시오. 크로스 DB 분석이 켜진 상태에서 발생한 접근 실패는 `SkippedExternal`로 덮지 말고 `Failed`로 노출해야 합니다. 동일 객체의 여러 경로 중 최소 깊이를 우선하며, 성공하지 않은 객체에는 명세서 링크를 만들지 마십시오. 객체 키와 출력 경로는 구분자·파일명 문자를 충돌 없이 인코딩하고, 성공한 모든 하위 객체의 최종 Critic 점수와 `Thinking.md`를 보존해야 합니다. 객체명 표기는 호출부(`sys.sql_expression_dependencies`·AST)가 아니라 카탈로그(`sys.objects`)나 오프라인 스냅샷에 등록된 실제 이름을 따라야 하며, 같은 객체가 호출한 SP마다 다른 표기로 저장되어 케이스 민감 파일시스템에서 링크가 깨지지 않게 하십시오.
    *   **오프라인 스냅샷 파일 검증 (Fail-Fast)**: `appsettings.json`에 `OfflineSnapshotPath`가 설정되어 있으나 실제 파일이 존재하지 않는 경우, 사용자 DB 연결 프롬프트로 우회(Fallback)하지 말고 즉각 예외를 발생시켜 프로그램을 종료함으로써 사용자가 설정 오기입을 바로 인지할 수 있도록 하십시오.
3.  **AI API 응답 널 가드(TryGetProperty) 및 모델 파라미터 매핑을 준수하십시오.**
    *   [ClaudeClient.cs](./src/ReSet.Core/Services/Clients/ClaudeClient.cs), [OpenAiClient.cs](./src/ReSet.Core/Services/Clients/OpenAiClient.cs), [GoogleClient.cs](./src/ReSet.Core/Services/Clients/GoogleClient.cs), [OllamaClient.cs](./src/ReSet.Core/Services/Clients/OllamaClient.cs), [ZaiClient.cs](./src/ReSet.Core/Services/Clients/ZaiClient.cs) 호출 파싱 시 안전 필터 차단이나 응답 누락으로 인해 `KeyNotFoundException` 크래시가 발생하는 것을 원천 차단하십시오.
    *   반드시 `TryGetProperty`를 활용해 JSON 필드 유무를 안전하게 확인하고, 비정상 수신 시 `InvalidOperationException`을 던져 투명하게 거절 사유를 노출하십시오.
    *   **모델별 전송 규격 매핑**: OpenAI 추론 모델(o1/o3) 호출 시 `temperature`를 제외하고 `reasoning_effort`를 표준 매핑하십시오. 또한 gpt-5 Responses API 사용 시 명시적인 `prompt_cache_key`를 주입하여 프롬프트 캐시를 활성화합니다. Claude 통신 시 4/5세대 모델의 빈 생각 블록 누수를 방지하기 위해 옵션을 조율하고, `system` 프롬프트 블록 내에 `cache_control: { type: "ephemeral" }`을 부여하여 캐싱을 활성화합니다.
    *   **OpenAI Responses 추론 보존**: gpt-5 Responses API의 `output`에는 여러 `reasoning` 항목과 빈 `summary`가 섞여 올 수 있습니다. 모든 비어 있지 않은 `summary_text`를 누적하고, 뒤따르는 빈 summary가 앞서 수집한 추론을 지우지 않도록 하여 `Thinking.md`에 보존하십시오.
    *   **Ollama 온도 매핑 및 반복 패널티(Degeneration) 방어**: 로컬 Ollama 구동 시 effort(low/medium/high/max)가 전달될 경우, temperature 파라미터를 각각 0.1/0.4/0.7/0.9로 차등 적용하여 추론 다양성을 제어하십시오. 단, 모델명에 `gemma4` 또는 `qwen3.6`이 포함된 경우 이 매핑을 무시하고 내부적으로 각각 최적 샘플링 설정으로 하드코딩되도록 강제해야 합니다. 또한, 로컬 모델(Ollama, mlx, vllm) 특유의 텍스트 무한 반복 루프를 방지하기 위해 `repeat_penalty`, `repetition_penalty`, `frequency_penalty` 옵션을 반드시 주입하십시오.
    *   **Ollama 모델별 추론(Thinking) 제어 및 파싱 규칙**: Gemma 4에만 공식 추론 트리거인 `<|think|>`를 시스템 프롬프트 선두에 주입하고, 그 외의 모델(Qwen 등)은 프롬프트의 텍스트 지시(Instruction)로만 `<think>` 사용을 유도하여 텍스트 누수(Leakage)를 방지하십시오. 파싱 시 `</think>`뿐만 아니라 `<|end of thought|>` 토큰도 폴백(Fallback)으로 처리하여 추론 텍스트가 명세서에 노출되는 것을 원천 차단해야 합니다.
    *   **프롬프트 응답 정화**: AI 응답 본문에 인사말, 요약 등 불필요한 대화형 문구(Conversational filler)가 포함되거나, 전체 응답을 마크다운 코드 블록(```)으로 감싸는 것을 금지하는 명시적 지시를 프롬프트에 유지하십시오. (단, Mermaid 다이어그램은 예외적으로 래핑 허용)

### 🎨 범주 3. 인터페이스 및 Spectre.Console 예외 회피 (UI/UX)
4.  **TUI 인터페이스의 시각적 안정성 및 사용자 입력을 지원하십시오.**
    *   **마크업 이스케이프**: 출력할 DB 메타데이터, AI 원문, 파일 경로 등에 대괄호(`[...]`)가 포함되어 있으면 Spectre.Console의 스타일 마크업 오인 오류를 방지하기 위해 반드시 **`Markup.Escape()`** 처리를 하십시오.
    *   **유효 디렉토리 및 통합 Job 대화형 선택 유도**: 필수 폴더 경로가 없을 경우 종료하기보다 TUI 상에서 사용자 재입력을 유도하되, `TextPrompt.ShowChoices(false)`를 결합해 슬래시('/') 기호가 구분선으로 오작동하여 화면이 깨지는 현상을 차단하십시오. 또한 검증기(Validator) 가동 시에는 `output/Jobs/` 하위의 Job 목록을 스캔하여 사용자에게 대화형으로 선택(Prompt)할 수 있는 전용 메뉴를 제공함으로써 올바른 경로 진입을 보장하십시오.
    *   **연결 정보 즉석 수정**: 로그인 성공 후에도 [ConsoleUserInteraction.cs](./src/ReSet.Cli/ConsoleUserInteraction.cs) 상에서 appsettings.json을 수정하지 않고 즉석에서 서버 주소 및 DB명을 갱신하여 대상 DB에 교체 접속할 수 있도록 입력 기회를 제공하십시오.
    *   **배치 단계 순서 보장**: 다중 선택 UI의 순서 유실 문제를 차단하기 위해 순차 선택 루프 방식으로 배치 계획 스텝 순서를 확보하십시오.
    *   **TUI 상태 정보 강화 및 간소화**: Actor를 단일 모드로 실행할 때, 메인 태스크 이름에는 모델명과 추론 강도(Effort)를 함께 노출하되, 하위 진행 단계 표시 시 화면 간소화를 위해 불필요한 모델명을 반복 노출하지 않고 괄호 없는 순번(`n/3.`) 형식으로 간결하게 출력하도록 규칙을 준수하십시오.
    *   **TUI 시각적 안정성 (여백 확보)**: L1 기계 검증 및 L2 AI 리뷰에서 결함이 발견되어 화면에 출력할 때, 메시지 끝에 빈 줄바꿈(`AnsiConsole.WriteLine()`)을 강제하여 이어지는 다음 재시도 상태 메시지와 시각적으로 깔끔하게 분리되도록 가독성을 유지하십시오.
    *   **진행 태스크 정보 보존**: TUI 진행도 표시기(Progress Scope) 완료/실패 업데이트 시 원래의 설명(Description) 필드가 누락 또는 다른 값으로 덮어쓰여 화면 렌더링 레이아웃이 깨지는 현상을 방지하십시오.
    *   **재귀 분석 진행 표시**: 참조 SP/UDF 분석도 일반 TUI 진행 스코프에 상태를 위임하여 생성·검증 단계를 스피너와 경과시간으로 표시하십시오. 별도의 일반 콘솔 상태 출력과 진행 UI를 혼용해 화면을 깨뜨리지 마십시오.
5.  **TUI 비파괴식 Serilog 파일 로깅 및 마크업 자동 정화를 준수하십시오.**
    *   진행 상황 로그 파일 기록 시 대화형 TUI 화면과 진행 바가 깨지지 않도록 Serilog를 **오직 파일 저장 전용(File Sink)**으로 가동하십시오.
    *   로그 기록 직전에는 Spectre.Console 스타일 마크업 태그들을 정규식을 활용해 자동 정화(StripMarkup)해야 하며, 프로세스 종료 시 `Serilog.Log.CloseAndFlush()` 호출로 리소스를 정리하십시오.

### ⚙️ 범주 4. 검증 오케스트레이션 및 파이프라인 흐름 (Verification Workflow)
6.  **3단계 검증 파이프라인의 역할 분리 및 L2 Actor-Critic을 운용하십시오.**
    *   **L1 (정적)**: [MechanicalValidator.cs](./src/ReSet.Core/Services/MechanicalValidator.cs)에서 Markdig 파서 필수 섹션 검증, **Anti-Shortcut (생략어) 감지 및 즉시 반려(Fast-Fail)**, 그리고 Mermaid 다이어그램 린팅을 수행하십시오.
        - **Mermaid 다이어그램 자동 정화**: Mermaid 린팅 전 `CleanseMermaidCode`를 통해 화살표 라벨 따옴표 제거, 잘못된 화살표 기호 보정, 노드 ID 특수문자 제거, 특수문자 포함 라벨 큰따옴표 자동 래핑 등 자동 정화기가 구동되어 정화된 마크다운을 산출하도록 설계되어 있습니다. (단, `subgraph` 키워드와 ID 사이의 공백은 훼손되지 않도록 유지하며, 연속 체이닝 화살표(`A --> B --> C`)가 라벨로 오인되지 않도록 엄격한 정규식을 적용합니다.) 이 정화된 내용을 훼손하거나 무력화하지 마십시오.
        - **정화 결과 영속 반영**: L1 검증 단계에서 획득한 정화된 마크다운(CleansedMarkdown)은 검증 성공 여부에 관계없이 파이프라인 오케스트레이터에서 메모리 상의 명세서 및 계획서 원본 텍스트에 다시 덮어써 최종 파일로 영속 보존되도록 구현을 유지하십시오.
    *   **L2 (AI 교차 검토)**: [AiService.cs](./src/ReSet.Core/Services/AiService.cs)의 자가 보완 루프(`MaxL2Attempts` 한도 준수)를 제어하고, **컨텍스트 윈도우 오염 방지를 위해 누적된 이전 피드백을 지우고 최신 피드백만을 Stateful Checklist 포맷으로 단일 압축 주입**하여 회귀 결함(Regression)을 예방하십시오.
        - **로컬 모델 구역별 순차 분할 생성**: `AiClientFactory.IsLocalProvider(ProviderName)` (Ollama, mlx, local-openai 등) 사용 시 1회차 생성 및 자가 수정/피드백 재생성 루프는 "OverviewAndParameters", "CrudAnalysis", "LogicAndVisualization" 구역으로 나누어 순차적으로 구동하되, 피드백 내용의 키워드 분석을 통해 연관된 파트만 선택적으로 재생성 및 조립되도록 설계되어 있습니다. 하드코딩된 "Ollama" 비교 대신 항상 `IsLocalProvider()` 헬퍼를 사용하여 mlx-lm 등 로컬 호환 프레임워크도 이 파이프라인을 타도록 정합성을 준수하십시오. TUI 상의 진행도는 논리 구조 분석(Stage 1)과 3개의 분할 생성(Stage 2)을 합쳐 전체 4단계(1/4 ~ 4/4)로 통합 넘버링하여 사용자에게 직관적인 진행 상황을 제공하도록 구성되어 있습니다.
        - **Stage 1 (Deconstruct) 추론 로깅 보존**: 로컬 모델의 분할 생성(Stage 2)뿐만 아니라 초기 JSON 논리 추출 단계(Stage 1 Deconstruct)의 추론 내용도 `Thinking.md`에 함께 누적(`accumulatedThinking`)되도록 보장하여, 추론 모델의 논리 추출 과정을 투명하게 디버깅할 수 있도록 유지하십시오.
    *   **L2 Actor-Critic**: `ActorEffort: "dynamic"` 시 3종 차등 Effort 병렬 생성 ➔ Critic 채점 ➔ Fast-Pass 판정 ➔ Consolidator 앙상블 합성 ➔ **합성 완료 후 L2 최종 Critic 검증 및 1회 최종 보완 루프**를 순차 구동하십시오. 최종 합성본(또는 보완본)에 대한 최종 L2 Critic 리뷰 결과 점수는 명세서 파일 상단에 누락 없이 출력되어야 합니다.
    *   **품질 기준 엄격 강제 및 경고 표기**: 품질 향상을 위해 단일 모델 자가 수정 루프에서도 감쇄 임계치(Decaying Threshold)를 배제하고 설정된 기준 점수(Threshold)를 일관되게 적용하십시오. 만약 최종 시도 횟수를 소모한 후에도 점수 미달로 검증을 통과하지 못한 경우, 문서를 버리지 않고 채택하여 저장하되 문서 최상단에 `[!CAUTION]` 경고 배너와 상세한 Critic 점수 및 피드백 코멘트를 보존하여 후속 수정을 유도하도록 구현하십시오.
    *   **Mermaid 시스템 변수 예외 허용**: 다이어그램 린팅 시 `@@ERROR` 시스템 변수가 포함되어 있더라도 린팅 컴파일 검사에서 예외적으로 정상 패스하도록 정합성 규칙을 보완하십시오.
    *   **L3 (인간 승인)**: [VerificationPipelineOrchestrator.cs](./src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs)에서 미리보기 및 DB 역동기화를 제어하되, 무인 배치 모드(`isBatchMode: true`) 환경에서는 L3 프롬프트 단계를 생략하고 자동으로 우회 승인하십시오.
    *   **진행도 시각화**: 진행률 시각화([IMultiProgressScope.cs](./src/ReSet.Core/Services/IMultiProgressScope.cs)) 통합 시 Core가 UI에 직접 의존하지 않는 비결합 설계를 유지하고, TUI 구현부(`ConsoleProgressScope`)에서는 렌더링 루프와의 충돌 방지를 위해 `ConcurrentDictionary`와 `TaskCompletionSource`를 적용하여 백그라운드 태스크 방식으로 격리 갱신하십시오.
    *   **신규 공급자 확장**: 새로운 LLM 공급자 연동 시, [IAiClient.cs](./src/ReSet.Core/Services/IAiClient.cs)를 상속받아 클라이언트를 구현하고 [AiClientFactory.cs](./src/ReSet.Core/Services/Clients/AiClientFactory.cs)에 등록하십시오.
    *   **리뷰(검증) 시 풍부한 컨텍스트 유지**: AI 리뷰어(Critic)가 기능 명세서의 정확성과 CRUD/인터페이스 완전성을 정상 검증할 수 있도록, 리뷰 요청([ReviewSpecificationAsync](./src/ReSet.Core/Services/AiService.cs)) 시에도 분석 요청 시와 동일하게 테이블 스키마, 참조 UDF DDL, AST 정적 분석 등의 원본 메타데이터 컨텍스트 정보(`BuildSpMetadataTexts` 헬퍼 이용) 및 대상 stored procedure의 실제 SQL DDL 소스코드(`spDef.DdlText`)를 누락 없이 빌드하여 리뷰 프롬프트(`userPrompt`)에 포함해 전달해야 합니다.
    *   **재귀 객체별 검증과 산출물 모드**: `AnalysisSettings:AnalyzeReferencedCodeObjects`가 활성화되면 SP/UDF마다 기존 L1/L2/L3 파이프라인과 캐시 경로를 그대로 적용하십시오. 직접 의존 메타데이터에는 테이블 스키마·설명·인덱스와 참조 코드 DDL을 유지하되, 외부 DB 테이블·뷰의 컬럼·인덱스 상세는 조회하지 마십시오. `DatabaseSettings:AllowExternalDatabaseConnections`가 켜진 경우에만 다른 DB의 코드 객체 유형·DDL을 3부 이름으로 해석해 분석 대상에 포함하고, 분석 루트 DB는 `DependencyAnalysisRequest`를 통해 파이프라인에 전파하여 산출물 경로와 캐시 키가 어긋나지 않게 하십시오. 링크드 서버(4부 식별자)는 지원 대상이 아닙니다. `OutputSettings:DependencyArtifactMode`의 `Reference` 모드에서는 표준 DDL을 객체당 한 번만 저장하고, `PortableBundle`에서만 참조 SP/UDF DDL 사본을 `raw/ddl/`에 추가하십시오.
    *   **하이브리드 영문 프롬프트 구조 준수**: `AiService.cs` 내부의 시스템 프롬프트(`systemPrompt`)는 반드시 영문(English) 작성을 원칙으로 하고, 최종 출력 및 체크리스트 동작 지시만 한국어 출력 조건 및 영어 매칭 트리거를 사용해야 합니다. 이를 임의로 한국어 프롬프트로 전면 번역하거나 되돌려 규칙 준수 강도를 떨어뜨리지 마십시오.
    *   **스키마 및 환각/숏컷(Shortcut) 차단 룰 유지**: 프롬프트 규칙 내의 "의존 메타데이터 외 컬럼 창작 금지" 및 "DDL 미정의 임의 에러 반환 상숫값 가작 금지" 규정은 로컬 LLM의 안전장치입니다. 또한 통합 배치 전환 계획 수립 시, UNION/JOIN이나 에러 코드 분기 처리(Chunking Key) 로직을 모델이 자의적으로 축약(Shortcut)하지 못하도록 하는 "Anti-Shortcut" 프롬프트 제약 규칙을 절대 간소화하거나 누락하지 마십시오.

### 🔒 범주 5. 타겟 런타임 격리 및 리소스 정리 (Lifecycle & Sandbox)
7.  **타겟 러너 격리 및 모의 데이터(Mock Data) 적재 수명주기를 준수하십시오.**
    *   **트랜잭션/타임아웃 격리**: C# 리플렉션 러너([CSharpReflectionRunner.cs](./src/ReSet.Validator.Core/Services/CSharpReflectionRunner.cs)) 호출 시 생성되는 `DbTransaction`은 항상 **`Rollback()`** 처리하여 Sandbox 상태 변경을 격리하고, 비동기 호출 시 `ValueTask` 및 `ValueTask<T>` 반환 형식도 리플렉션을 통해 동적으로 대기(await)하여 롤백 및 종료 전 작업이 완료되도록 보장하고, Java 프로세스 구동 시에는 30초의 타임아웃 제한을 명확히 설정하십시오.
    *   **모의 데이터 수명주기**: 물리적 FK가 없는 환경을 극복하기 위해 관계 시드가 매핑된 모의 데이터 캐시를 활용하고, [SandboxSeedingService.cs](./src/ReSet.Validator.Core/Services/SandboxSeedingService.cs)를 통해 데이터 적재(Seed) 및 테스트 완료 후 자동 소거(Clean-up/Truncate) 처리를 확실히 수행하십시오.

### 🔌 범주 6. 외부 코딩 에이전트 및 프로세스 제어 (External Agent & Codegen)
8.  **지시서 번들 생성 및 코딩 에이전트 CLI 프로세스 제어를 적용하십시오.**
    *   **번들 및 프롬프트 제공**: [MetadataExporter.cs](./src/ReSet.Core/Services/MetadataExporter.cs)의 지시서 내보내기 시 DDL, 스펙, 계획서 및 의존 관계를 마크다운 하나로 묶어 제공하고, 하단에 외부 에이전트 복사/붙여넣기용 프롬프트를 명시하십시오. 대상 출력 폴더가 없을 시 선행 자동 생성을 처리하십시오. 개별 SP 분석 시에는 에이전트 지시서 번들을 생성하지 않으며, 통합 배치 시에만 문서 리소스(`docs/`)와 에이전트가 생성한 소스코드(`src/`) 모두를 `output/Jobs/{JobName}/` 하위 디렉토리에 엄격하게 분류 격리하여 프로젝트 파일 무결성을 보장하십시오.
    *   **동적 코드 생성 시점 제약**: 개별 SP 분석 완료 직후에는 에이전트 자동 기동을 금지하며, 가급적 복수 SP가 엮인 통합 배치 전환 계획서 수립 완료 시점에만 외부 에이전트를 기동하십시오. 단, 사용자가 메인 메뉴에서 스탠드얼론 메뉴(기작성된 지시서 기반 구동)를 선택한 경우에는 기존 출력 디렉터리의 `agent/MigrationInstructions.md`를 스캔하여 에이전트를 독립적으로 재기동(Resume)할 수 있도록 허용합니다.
    *   **프로세스 양방향 제어**: [ExternalCliCodingEngine.cs](./src/ReSet.Core/Services/ExternalCliCodingEngine.cs) 기동 시 대화형 흐름을 공유할 수 있도록 부모 콘솔 입출력 스트림을 직접 상속 공유하고, 취소(`CancellationToken`) 수신 시 좀비 프로세스를 예방하기 위해 하위 프로세스 트리를 강제 종료(`process.Kill(true)`)하십시오. 띄어쓰기가 포함된 프롬프트 파싱을 막기 위해 Arguments 전체를 쌍따옴표(`\"...\"`)로 래핑하여 공급하십시오.
    *   **무인 자동 기동**: CLI 배치 모드 실행 시 `--job-name` 인자가 공급되면 L3 대화형 단계를 건너뛰고 자동으로 통합 계획 및 지시서 번들을 생성해 외부 에이전트 프로세스 기동까지 연속 수행하는 CI/CD 무인 파이프라인을 지원하십시오.
    *   **자가 수정 및 TDD 테스트 피드백 루프**: 외부 에이전트 기동 시 테스트 뼈대 및 구조 구축을 에이전트에게 자율 위임하고, 지시서(`todo.md`) 내에 명시된 5단계 자율 루프(코드 작성 ➔ 테스트 ➔ 자가 수정 ➔ 자율 리뷰 ➔ 점진적 커밋)를 통해 에이전트 스스로 L0(로컬 테스트)를 통과하도록 유도합니다. 이후 L1 정적 검사(컴파일 오류 시 숏컷) 및 L2 의미론적 대조를 순차 수행하며, 검증 불일치 시 지시서에 피드백을 축적해 외부 에이전트를 재수정 기동시킵니다.

### 🧹 범주 7. 메타데이터 정화 및 주석 보완 (Cleansing & Annotation)
9.  **메타데이터 정화 및 정책 문서 수립 가이드를 준수하십시오.**
    *   **클렌징 스크립트 및 동기화**: AI 분석 완료 시 보완 스크립트 파일(`*_MetadataCleansing.sql`) 생성 기능은 현재 로컬 LLM의 스키마 환각(Hallucination) 방지를 위해 기본 제거되어 있습니다. 향후 수동 태그 삽입 등으로 해당 쿼리 파일이 물리적으로 존재할 때만 조건부로 TUI 최종 승인 및 동의를 묻고 DB 동기화를 실행하십시오. 크로스 DB 분석으로 수집된 다른 DB 소유 객체의 정화 스크립트는 파일명에 DB를 접두해 구분하고, 연결된 DB가 아닌 대상에는 절대 실행하지 마십시오.
    *   **C# 보간 중괄호 이스케이프**: 프롬프트 텍스트 내부의 중괄호(`{}`)는 C# 보간 기호($) 해석 오류를 막기 위해 반드시 이중 중괄호(`{{}}`)로 이스케이프해야 합니다.
    *   **정산 정책서**: SP DDL의 상수 분기 조건 분석과 테이블 데이터 프로파일링 정보를 결합해 정산 정책서(Settlement Rulebook)를 도출하고, 지정된 5대 헤더 구조를 엄격히 준수하도록 설계하십시오.
    *   **컬럼 매핑 표 축약 금지**: CRUD 분석 및 데이터 컬럼 매핑 표 작성 시, '외 다수' 또는 '등'과 같이 컬럼 목록이나 매핑 관계를 임의로 축약하거나 생략하지 말고, 실제 대상 물리 컬럼과 이에 매핑되는 원천값을 누락 없이 1:1 대조 표에 완전하게 기술하십시오.
    *   **DDL 기반 제약 조건 작성**: 프로시저 파라미터나 컬럼 제약 조건에 대해 임의로 'NOT NULL'과 같은 주관적 단정을 짓지 말고, 오직 DDL 소스코드에 명시되어 있는 타입 제약 및 기본값 정의를 기반으로만 사실적으로 기술하십시오.
    *   **의존 스키마 덤프 필터링**: 테이블 상세 스키마 정보를 마크다운 테이블로 덤프할 때, AST 정적 분석이 감지한 실제 참조 컬럼(`ReferencedColumnsPerTable`), PK/FK 컬럼, 인덱스 구성 컬럼만 선별적으로 필터링 출력(KeepCols 필터링)하여 AI 프롬프트 토큰을 절약하도록 구현되어 있습니다. 이 최적화 로직의 정합성을 유지해 주십시오.
    *   **통합 배치 도메인 5대 핵심 제약 (NOLOCK / INSERT-only / Chunk Key / 멱등성 / 예외 처리)**: 통합 배치 전환 계획 수립 시 다음 사항을 엄격히 강제하십시오. 1) **NOLOCK 힌트 전면 금지**: 타겟 프레임워크가 Session-level SNAPSHOT 격리를 사용할 경우 `WITH (NOLOCK)` 힌트는 READ UNCOMMITTED를 유발하여 정책을 위반하므로 마이그레이션 의사코드에서 즉시 전면 제거해야 합니다. 2) **INSERT-only 롤백**: INSERT-only 단계는 섀도우(Shadow) 백업 테이블을 생성해 롤백하지 말고, 단순 `ROLLBACK TRAN`이나 `DELETE WHERE [ChunkKey]`를 통한 보상 트랜잭션으로 설계하십시오. 3) **청킹 키 검증, 원본 필터 및 에러 트래킹**: 청킹용 임시 분기 변수(예: `CLIENTID`)를 지정할 때는 반드시 타겟 테이블 내에 존재하는 실제 컬럼(또는 PK 기반 해시)만을 사용하고, 청킹 필터 적용 시에도 원본 비즈니스 필터(예: `WHERE Status = 'P'`)를 절대 누락 없이 보존하십시오. 에러 코드는 임의로 변조(Remapping)하지 말고 원본 에러 코드를 정확히 트래킹하도록 제한하십시오. 추가로 Chunking `WHILE` 루프 내부에는 부분 커밋 및 격리를 위해 `BEGIN TRAN/COMMIT TRAN` 경계를 명시해야 합니다. 4) **멱등성 보장 및 섀도우 복원**: 체크포인트 기반 스킵 로직을 추가하여 이미 성공한 단계가 재시작 시 실패 처리되지 않도록 설계하고, Shadow 테이블을 활용해 롤백(Restore)할 경우 데이터 중복을 방지하기 위해 반드시 선행 `DELETE` 구문을 실행한 뒤 데이터를 복원하도록 강제하십시오. 5) **XACT_ABORT 및 TRY...CATCH 결합**: `XACT_ABORT ON`을 선언할 경우 반드시 `BEGIN TRY...CATCH` 블록과 결합해야 하며, 구형 `GOTO` 예외 처리 패턴 사용을 원천 차단하십시오.
    *   **복합 필터의 정확한 해석**: `NOT IN`, `ISNULL` 등이 결합된 복합 필터/분기 조건을 해석할 때 논리적 환각을 철저히 배제하고 정확하게 기술하십시오. (예: '특정 값만 포함'이 아니라 '제외된 값 외의 모든 값 및 NULL 치환값 포함'으로 정확히 서술)
    *   **Mermaid flowchart 생성 규칙화 및 정화**: 기능 명세서 내의 Mermaid 다이어그램 작성 시, 화살표/연결선 조건 라벨에는 절대 큰따옴표를 쓰지 말아야 하며(예: `N1 -->|예| N2` 또는 `N1 -- 에러 --> N2`), 노드 내부 텍스트에는 SQL 변수 기호(`@`)를 포함하지 않고 자연어 또는 순화된 명칭으로 나타내어야 합니다. (단, `@@ERROR`는 예외 허용 및 전체 이중 따옴표 래핑) 또한, 다이어그램 린팅 및 정화 시 노드 ID의 공백과 언더스코어를 일괄 제거하는 규칙을 적용하여 노드 선언과 참조의 불일치를 방지하십시오.
    *   **3부/4부 식별자 참조 명확성**: 분석 대상 SP DDL에 3부 식별자(크로스 데이터베이스 참조)가 사용된 경우, 이를 Linked Server(4부 식별자)로 오기하지 않고 동일 인스턴스 내 크로스 데이터베이스 참조임을 명시해야 합니다.

### 🌳 범주 8. 버전 관리 및 작업 공간 제어 (Version Control & Workspace)
10. **모든 코드 변경 작업 시 `git worktree`를 적극 활용하십시오.**
    *   에이전트는 메인 브랜치(main/master)에 직접 커밋하거나 기존 워킹 디렉터리를 오염시키지 않도록 주의해야 합니다.
    *   기능 추가, 버그 수정, 구조 변경 등 코드 베이스를 수정해야 할 경우, 가급적 독립적인 `git worktree`를 생성하여 별도의 작업 공간에서 코드를 작성하고 검증(빌드 및 테스트)을 수행하십시오.
    *   작업 및 테스트가 성공적으로 완료된 후 변경 사항을 병합(Merge)하고, 작업이 끝난 워크트리는 안전하게 정리(Remove)하는 사이클을 유지하십시오.

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

- [ ] `dotnet build` 명령어를 통한 컴파일 경고/에러가 0개인지 확인했는가?
- [ ] `dotnet test` 명령어를 실행하여 328개의 단위 테스트가 모두 예외 없이 100% 통과(Passed)하였는가?
- [ ] API Key 등 비공개 자격증명이 소스코드나 `appsettings.json`에 하드코딩되지 않고 `appsettings.local.json` 또는 로컬 환경 변수로 격리되었는가?
- [ ] DB 메타데이터, AI 결과 원문 등을 Spectre.Console TUI에 출력할 때 모든 출력 부에 `Markup.Escape()` 조치를 적용했는가?
- [ ] Stored Procedure 실행 및 외부 샌드박스 데이터 수집 시, DB 연결 실패 시 예외 격리(Soft Fail 및 DTO FAIL 상태 주입) 처리가 정상 적용되었는가?
- [ ] 신규 추가된 C# 타겟 러너 내 `DbTransaction`이 작업 결과와 관계없이 항상 `Rollback()` 되도록 누락 없이 명세했는가?
- [ ] 작업 완료 후 수정 및 추가된 모든 코드가 솔루션 컴파일 및 아키텍처 규칙을 위반하지 않는지 재검토했는가?
