# 🤖 ReSet (**RE**verse engineering **SET**tlement) Agent Guidelines (AGENTS.md)

이 문서는 **SQL Server Stored Procedure Reverse Engineering Tool (ReSet (REverse engineering SETtlement))** 프로젝트를 분석하고, 수정하며, 확장하고자 하는 AI 에이전트를 위한 시스템 지침서입니다. 본 프로젝트의 아키텍처 정합성과 코드의 무결성을 유지하기 위해 다음 가이드라인을 반드시 준수하여 개발을 진행해 주십시오.

---

## 📌 프로젝트 개요 (Overview)

본 프로젝트는 SQL Server에 구현된 Stored Procedure(SP)를 재귀적으로 분석하여 비즈니스 기능 명세서(`*_Spec.md`)와 여러 SP 기반의 통합 배치 전환 계획서(`*_BatchMigrationPlan.md`)를 작성하는 .NET Core 기반 CLI/TUI 도구입니다.

- **핵심 목표**: 레거시 DB 비즈니스 로직(SP)을 효율적으로 역공학하여 현대적인 애플리케이션 아키텍처(C#, Java Spring Batch 등)로 마이그레이션하기 위한 설계 산출물을 자동 생성 및 검증하는 것입니다.
- **신뢰성 보장**: AI가 단순 생성만 하고 끝나는 것이 아니라 **3단계 신뢰성 검증 파이프라인**을 통해 마크다운 문법, AI 자가 교정, 인간 피드백을 수렴하여 고품질의 설계를 유도합니다.

---

## 📂 프로젝트 구조 및 주요 파일 바로가기 (Key Code References)

에이전트는 코드 수정 시 다음 구성 요소를 참조하고 알맞은 디렉토리에 변경사항을 작성해야 합니다. 모든 클래스 참조 시 아래의 직접 링크를 활용하십시오.

### 1. Core 라이브러리: [ReSet.Core](file:///home/moondae/git-root/ReSet/src/ReSet.Core)
*   **도메인 모델 ([Models](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Models))**
    *   [SpDefinition.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Models/SpDefinition.cs): 분석된 SP 메타데이터(소스코드 DDL, 컬럼, 의존성 등)를 관리하는 루트 데이터 클래스.
        *   [SpStaticAnalysisResult](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Models/SpDefinition.cs#L16): 테이블 CRUD, 임시 테이블, UDF 및 Linked Server 등 정적 분석 결과 구조를 홀딩하는 도메인 모델.
    *   [DependencyInfo.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Models/DependencyInfo.cs): 재귀적으로 수집된 DB 개체(테이블, 뷰, 다른 SP 등) 의존성을 표현하는 모델.
    *   [ColumnInfo.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Models/ColumnInfo.cs): 컬럼명, 데이터타입, PK/FK 정보, 한글 설명, 설명 누락 유무(IsDescriptionMissing) 및 Identity/DefaultValue 정보를 수집하는 모델.
    *   [TableIndexInfo.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Models/TableIndexInfo.cs): 테이블 인덱스 메타데이터(인덱스명, 타입, Unique, PK 여부, 구성 컬럼)를 관리하는 모델.
    *   [AiResult.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Models/AiResult.cs): AI 응답 내용(Content) 및 추론 텍스트(ThinkingText), 요청된 시스템/사용자 프롬프트 콘텍스트를 모아 관리하는 데이터 모델.
*   **비즈니스 서비스 ([Services](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services))**
    *   [DbMetadataService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/DbMetadataService.cs): SQL Server 메타데이터(Extended Properties, DDL, 의존성 관계)를 DFS 재귀 탐색을 활용해 수집하는 인터페이스([IDbMetadataService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/IDbMetadataService.cs)) 구현체.
    *   [SqlStaticParser.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/SqlStaticParser.cs): ScriptDom 라이브러리를 가동해 테이블 CRUD, 임시 테이블, 분기 들여쓰기 린팅, 동적 SQL, UDF 및 Linked Server 원격 참조를 정적으로 파싱하는 정적 분석기 서비스.
    *   [AiService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/AiService.cs): 수집한 정보를 프롬프트로 다듬어 AI 공급자에 분석 요청을 보내는 인터페이스([IAiService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/IAiService.cs)) 구현체.
    *   [IAiClient.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/IAiClient.cs): AI 모델 간의 공통 텍스트 통신 계약 정의 인터페이스 및 프로바이더별 클라이언트 팩토리([AiClientFactory.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/Clients/AiClientFactory.cs)).
    *   [MechanicalValidator.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/MechanicalValidator.cs): Markdig 파서 및 Mermaid 린터를 활용해 산출물 뼈대 및 다이어그램 문법을 정적 검증하는 클래스.
    *   [VerificationPipelineOrchestrator.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs): 3단계 검증 파이프라인의 오케스트레이션을 담당.
    *   [MetadataExporter.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/MetadataExporter.cs): 원본 DB 메타데이터를 JSON, Raw 프롬프트 마크다운(`*_RawContext.md`), 개별 DDL/MD 파일 등으로 보존하고, 외부 코딩 에이전트용 가이드라인 번들(`*_MigrationInstructions.md`) 및 통합 마이그레이션 지시서 번들(`{JobName}_MigrationInstructions.md`)을 생성하는 기능 구현체 (SP 및 통합 Job별 전용 하위 디렉토리에 격리 분류 저장).
    *   [CacheManager.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/CacheManager.cs): SHA-256 해시 기반 로컬 증분 분석 캐싱 서비스 구현체 ([ICacheManager.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/ICacheManager.cs) 포함).
    *   AI 응답 수집 및 로그 격리: AI 클라이언트 호출 결과에서 추출된 추론(Thinking) 텍스트는 수집 후 TUI 화면을 오염시키지 않도록 `Log.Verbose` 또는 파일 전용 로그에만 기록되게 하고, 기본 실행 수준에서는 실시간 노출을 차단하여 TUI 화면 깨짐을 원천적으로 차단하십시오.
    *   [ExternalCliCodingEngine.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/ExternalCliCodingEngine.cs): CLI 기반 외부 에이전트 프로세스(Claude, agy, codex 등) 기동 및 콘솔 상속 연동 구현체.
    *   [IMultiProgressScope.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/IMultiProgressScope.cs): 멀티태스크 진행률 상황 보고를 위한 추상 인터페이스.
    *   [NullProgressScope.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/NullProgressScope.cs): 유닛 테스트 및 무인 모드 등에서 UI 미출력을 보장하고 NullReferenceException을 막는 방어적 널 객체 구현체.
    *   [SettlementPolicyService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/SettlementPolicyService.cs): DDL 상수 분석 및 DB 마스터 데이터 프로파일링을 활용한 통합 정산 정책서 생성 서비스 인터페이스([ISettlementPolicyService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/ISettlementPolicyService.cs) 포함).

### 2. CLI 실행 엔트리: [ReSet.Cli](file:///home/moondae/git-root/ReSet/src/ReSet.Cli)
*   [Program.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Cli/Program.cs): CLI 진입점이자 TUI 메뉴 제어 및 흐름 오케스트레이션을 담당합니다.
*   [ConsoleUserInteraction.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Cli/ConsoleUserInteraction.cs): TUI와 사용자 간의 인터랙션 콘솔 처리 및 DB 동기화 여부 확인(ConfirmMetadataSyncAsync)을 정의한 구현체.

### 3. 코드 검증 Core 라이브러리: [ReSet.Validator.Core](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core)
*   **추상화 및 도메인 모델 ([Abstractions](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Abstractions), [Models](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Models))**
    *   [IValidatorPlugin.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Abstractions/IValidatorPlugin.cs): C#([CsValidatorPlugin.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Plugins/CsValidatorPlugin.cs)), Java([JavaValidatorPlugin.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Plugins/JavaValidatorPlugin.cs)) 등 언어별 L1 정적 구조 및 명칭 검증을 구현하는 플러그인 인터페이스.
    *   [IRuntimeRunner.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Abstractions/IRuntimeRunner.cs): 타겟 런타임 코드 실행을 위한 인터페이스 규격 정의.
    *   [IValidationUserInterface.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Abstractions/IValidationUserInterface.cs): 검증기 TUI 사용자 인터랙션을 추상화한 인터페이스.
    *   [L1ValidationResult.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Abstractions/L1ValidationResult.cs): L1 정적 구문 검증 결과를 담는 모델.
    *   [ValidationResult.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Models/ValidationResult.cs): 검증 대상의 L1/L2/L3 전체 상태를 관리하는 데이터 모델.
    *   [MockDataDto.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Models/MockDataDto.cs): 기획된 관계형 모의 데이터를 로컬 및 메모리에 들고 있기 위한 데이터 모델.
    *   [GapReport.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Models/GapReport.cs): L2 AI 의미론적 Gap 분석 결과 구조 데이터 모델.
    *   [RunnerDtos.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Models/RunnerDtos.cs): 타겟 런타임 실행기의 입출력 및 실행 결과를 담는 DTO 모음.
    *   [ValidatorConfig.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Models/ValidatorConfig.cs): 검증기 실행 설정을 바인딩하는 구성 모델.
*   **검증 비즈니스 서비스 ([Services](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Services))**
    *   [FileMappingService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Services/FileMappingService.cs): 설계서 파일(`*_Spec.md`)과 마이그레이션된 소스 파일을 스캔하여 1:1로 매핑하는 서비스.
    *   [ValidatorAiService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Services/ValidatorAiService.cs): AI에게 설계서와 소스코드를 전달하여 의미론적 일치성을 검사하고 GapReport 구조로 파싱하는 서비스 (TDD용 단위 테스트 및 ArchUnit 아키텍처 규칙 검증 테스트 코드 자동 설계 기능 추가 포함).
    *   [SpExecutionService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Services/SpExecutionService.cs): SQL Server DB에서 Stored Procedure를 동적으로 실행하고 결과를 JSON으로 덤프하는 서비스.
    *   [SandboxSeedingService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Services/SandboxSeedingService.cs): 모의 데이터를 샌드박스 DB에 적재(Insert)하고 실행 후 정리(Delete)하는 수명주기 서비스.
    *   [CSharpReflectionRunner.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Services/CSharpReflectionRunner.cs): 마이그레이션된 C# DLL 리플렉션 로드 및 DbTransaction 롤백 자동 격리 실행기.
    *   [JavaProcessRunner.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Services/JavaProcessRunner.cs): Java JAR/클래스를 외부 프로세스로 기동하여 stdin/stdout JSON 통신을 수행하는 격리 실행기.
    *   [DataComparisonService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Services/DataComparisonService.cs): 레거시 vs 타겟 JSON 데이터의 행 수, 컬럼 타입, 개별 값을 1:1 대조하여 마크다운 리포트 생성하는 서비스.
    *   [CodeVerificationOrchestrator.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Services/CodeVerificationOrchestrator.cs): L1(정적) -> L2(AI Gap분석) -> L3(사용자 승인) 흐름 제어 오케스트레이터.

### 4. 코드 검증 CLI 실행 엔트리: [ReSet.Validator.Cli](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Cli)
*   [Program.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Cli/Program.cs): 검증기 CLI 진입점.
*   [ConsoleUserInteraction.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Cli/ConsoleUserInteraction.cs): TUI 경로 입력 대화창 및 결과 패널 렌더링.

### 5. 단위 테스트 프로젝트: [ReSet.Core.Tests](file:///home/moondae/git-root/ReSet/tests/ReSet.Core.Tests)
*   **핵심 기능 및 연동 검증 테스트 ([Tests](file:///home/moondae/git-root/ReSet/tests/ReSet.Core.Tests))**
    *   [SqlStaticParserTests.cs](file:///home/moondae/git-root/ReSet/tests/ReSet.Core.Tests/SqlStaticParserTests.cs): ScriptDom 파서 동작 및 CRUD 분류, 다단계 들여쓰기 린팅, 동적 SQL, UDF/Linked Server 감지 검증.
    *   [ClaudeClientTests.cs](file:///home/moondae/git-root/ReSet/tests/ReSet.Core.Tests/ClaudeClientTests.cs), [OpenAiClientTests.cs](file:///home/moondae/git-root/ReSet/tests/ReSet.Core.Tests/OpenAiClientTests.cs), [OllamaClientTests.cs](file:///home/moondae/git-root/ReSet/tests/ReSet.Core.Tests/OllamaClientTests.cs): AI 클라이언트별 API 전송 구조 및 페이로드 널 가드/TryGetProperty 구문 안전성 검증.
    *   [JavaProcessRunnerTests.cs](file:///home/moondae/git-root/ReSet/tests/ReSet.Core.Tests/JavaProcessRunnerTests.cs): Java 프로세스 타임아웃(30초) 및 stdin/stdout 스트림 격리 실행 검증.
    *   [SandboxSeedingServiceTests.cs](file:///home/moondae/git-root/ReSet/tests/ReSet.Core.Tests/SandboxSeedingServiceTests.cs): 모의 데이터 샌드박스 DB 적재 및 라이프사이클 소거 검증.
    *   [CodeVerificationOrchestratorTests.cs](file:///home/moondae/git-root/ReSet/tests/ReSet.Core.Tests/CodeVerificationOrchestratorTests.cs): L1/L2/L3 오케스트레이션 및 자가 보완 루프 검증.

---

## 🚨 에이전트 핵심 준수 규칙 (Development Rules)

모든 작업은 아래 기술된 안전성과 무결성 범주에 맞춰 엄격히 격리되어 진행되어야 합니다.

### 🛡️ 범주 1. 보안 및 크레덴셜 제약 (Security)
1.  **절대 비공개 API Key를 소스 코드나 [appsettings.json](file:///home/moondae/git-root/ReSet/src/ReSet.Cli/appsettings.json)에 포함하여 커밋하지 마십시오.**
    *   로컬 개발용 API Key는 Git 추적 제외 대상인 `src/ReSet.Cli/appsettings.local.json`을 새로 생성하여 관리해야 합니다.

### ⚡ 범주 2. 예외 처리 및 안정성 (Stability & Soft Fail)
2.  **전방위적 소프트 페일(Soft Fail) 및 예외 격리 정책을 준수하십시오.**
    *   **DB 메타데이터 수집**: [DbMetadataService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/DbMetadataService.cs)의 스키마 권한 누락 또는 동적 SQL 의존성 탐색 과정의 쿼리 오류 시 프로세스를 중단(`throw`)하지 마십시오. 경고 목록(`Warnings`)에 기록하고 소프트 스킵 처리해야 합니다.
    *   **원천 데이터 파일 덤프**: [MetadataExporter.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/MetadataExporter.cs)의 디스크 쓰기 오류 등이 발생하더라도 핵심 산출물은 안전하게 보존되도록 에러 핸들러로 감싸야 합니다.
    *   **정합성 검증 DB 실행**: [SpExecutionService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Services/SpExecutionService.cs)의 Legacy SQL 실행 수집 시 연결 실패나 쿼리 수행 오류가 나면 크래시하지 말고, 결과 DTO의 테스트 케이스를 `FAIL`로 처리하고 예외 메시지를 `ErrorCode` 필드에 기재하여 직렬화 내보내야 합니다.
    *   **캐싱 및 서브 시스템**: [CacheManager.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/CacheManager.cs)의 DDL 해시 캐시 조작 및 기타 보조 연동 시 발생하는 모든 예외는 try-catch로 격리하여 마이그레이션 메인 파이프라인의 중단을 예방하십시오.
3.  **AI API 응답 널 가드(TryGetProperty) 및 모델 파라미터 매핑을 준수하십시오.**
    *   [ClaudeClient.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/Clients/ClaudeClient.cs), [OpenAiClient.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/Clients/OpenAiClient.cs), [GoogleClient.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/Clients/GoogleClient.cs), [OllamaClient.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/Clients/OllamaClient.cs), [ZaiClient.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/Clients/ZaiClient.cs) 호출 파싱 시 안전 필터 차단이나 응답 누락으로 인해 `KeyNotFoundException` 크래시가 발생하는 것을 원천 차단하십시오.
    *   반드시 `TryGetProperty`를 활용해 JSON 필드 유무를 안전하게 확인하고, 비정상 수신 시 `InvalidOperationException`을 던져 투명하게 거절 사유를 노출하십시오.
    *   **모델별 전송 규격 매핑**: OpenAI 추론 모델(o1/o3) 호출 시 `temperature`를 제외하고 `reasoning_effort`를 표준 매핑하고, Claude 4세대 모델 호출 시 `budget_tokens` 대신 `output_config.effort`에 강도를 위임해 400 에러를 방지하십시오.

### 🎨 범주 3. 인터페이스 및 Spectre.Console 예외 회피 (UI/UX)
4.  **TUI 인터페이스의 시각적 안정성 및 사용자 입력을 지원하십시오.**
    *   **마크업 이스케이프**: 출력할 DB 메타데이터, AI 원문, 파일 경로 등에 대괄호(`[...]`)가 포함되어 있으면 Spectre.Console의 스타일 마크업 오인 오류를 방지하기 위해 반드시 **`Markup.Escape()`** 처리를 하십시오.
    *   **유효 디렉토리 유도**: 필수 폴더 경로가 없을 경우 종료하기보다 TUI 상에서 사용자 재입력을 유도하되, `TextPrompt.ShowChoices(false)`를 결합해 슬래시('/') 기호가 구분선으로 오작동하여 화면이 깨지는 현상을 차단하십시오. (경로 기준점은 항상 `Directory.GetCurrentDirectory()` 활용)
    *   **연결 정보 즉석 수정**: 로그인 성공 후에도 [ConsoleUserInteraction.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Cli/ConsoleUserInteraction.cs) 상에서 appsettings.json을 수정하지 않고 즉석에서 서버 주소 및 DB명을 갱신하여 대상 DB에 교체 접속할 수 있도록 입력 기회를 제공하십시오.
    *   **배치 단계 순서 보장**: 다중 선택 UI의 순서 유실 문제를 차단하기 위해 순차 선택 루프 방식으로 배치 계획 스텝 순서를 확보하십시오.
    *   **TUI 상태 정보 강화**: Actor를 dynamic이 아닌 단일 모드로 실행할 때, CLI 출력 화면에 모델명 뿐만 아니라 활성 추론 강도(Effort) 값도 유기적으로 함께 노출되도록 구현하십시오.
5.  **TUI 비파괴식 Serilog 파일 로깅 및 마크업 자동 정화를 준수하십시오.**
    *   진행 상황 로그 파일 기록 시 대화형 TUI 화면과 진행 바가 깨지지 않도록 Serilog를 **오직 파일 저장 전용(File Sink)**으로 가동하십시오.
    *   로그 기록 직전에는 Spectre.Console 스타일 마크업 태그들을 정규식을 활용해 자동 정화(StripMarkup)해야 하며, 프로세스 종료 시 `Serilog.Log.CloseAndFlush()` 호출로 리소스를 정리하십시오.

### ⚙️ 범주 4. 검증 오케스트레이션 및 파이프라인 흐름 (Verification Workflow)
6.  **3단계 검증 파이프라인의 역할 분리 및 L2 Actor-Critic을 운용하십시오.**
    *   **L1 (정적)**: [MechanicalValidator.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/MechanicalValidator.cs)에서 Markdig 파서 필수 섹션 검증 및 Mermaid 다이어그램 린팅을 수행하십시오.
    *   **L2 (AI 교차 검토)**: [AiService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/AiService.cs)의 자가 보완 루프(`MaxL2Attempts` 한도 준수)를 제어하고, 이전 실패 원인 및 `GapReport`를 컨텍스트 프롬프트에 동적 주입하십시오.
    *   **L2 Actor-Critic**: `ActorEffort: "dynamic"` 시 3종 차등 Effort 병렬 생성 ➔ Critic 채점 ➔ Fast-Pass 판정 ➔ Consolidator 앙상블 합성 ➔ **합성 완료 후 L2 최종 Critic 검증 및 1회 최종 보완 루프**를 순차 구동하십시오. 최종 합성본(또는 보완본)에 대한 최종 L2 Critic 리뷰 결과 점수는 명세서 파일 상단에 누락 없이 출력되어야 합니다.
    *   **단일 모드 꼬리 잡기 방지**: 단일 모델 자가 수정 루프에서는 attempt 횟수에 따라 Critic 통과 허들 점수를 선형 차감하는 **Decaying Threshold** 및 결함 심각도(Accuracy/Crud는 Critical, Interface/Exception/Readability는 Minor)별 점수 완화 가중치를 활용하여 꼬리 잡기 교착을 원천 회방하십시오.
    *   **Mermaid 시스템 변수 예외 허용**: 다이어그램 린팅 시 `@@ERROR` 시스템 변수가 포함되어 있더라도 린팅 컴파일 검사에서 예외적으로 정상 패스하도록 정합성 규칙을 보완하십시오.
    *   **L3 (인간 승인)**: [VerificationPipelineOrchestrator.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs)에서 미리보기 및 DB 역동기화를 제어하되, 무인 배치 모드(`isBatchMode: true`) 환경에서는 L3 프롬프트 단계를 생략하고 자동으로 우회 승인하십시오.
    *   **진행도 시각화**: 진행률 시각화([IMultiProgressScope.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/IMultiProgressScope.cs)) 통합 시 Core가 UI에 직접 의존하지 않는 비결합 설계를 유지하고, TUI 구현부(`ConsoleProgressScope`)에서는 렌더링 루프와의 충돌 방지를 위해 `ConcurrentDictionary`와 `TaskCompletionSource`를 적용하여 백그라운드 태스크 방식으로 격리 갱신하십시오.
    *   **신규 공급자 확장**: 새로운 LLM 공급자 연동 시, [IAiClient.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/IAiClient.cs)를 상속받아 클라이언트를 구현하고 [AiClientFactory.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/Clients/AiClientFactory.cs)에 등록하십시오.
    *   **리뷰(검증) 시 풍부한 컨텍스트 유지**: AI 리뷰어(Critic)가 기능 명세서의 정확성과 CRUD/인터페이스 완전성을 정상 검증할 수 있도록, 리뷰 요청([ReviewSpecificationAsync](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/AiService.cs#L277)) 시에도 분석 요청 시와 동일하게 테이블 스키마, 참조 UDF DDL, AST 정적 분석 등의 원본 메타데이터 컨텍스트 정보(`BuildSpMetadataTexts` 헬퍼 이용)를 누락 없이 빌드하여 리뷰 프롬프트(`userPrompt`)에 포함해 전달해야 합니다.

### 🔒 범주 5. 타겟 런타임 격리 및 리소스 정리 (Lifecycle & Sandbox)
7.  **타겟 러너 격리 및 모의 데이터(Mock Data) 적재 수명주기를 준수하십시오.**
    *   **트랜잭션/타임아웃 격리**: C# 리플렉션 러너([CSharpReflectionRunner.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Services/CSharpReflectionRunner.cs)) 호출 시 생성되는 `DbTransaction`은 항상 **`Rollback()`** 처리하여 Sandbox 상태 변경을 격리하고, Java 프로세스 구동 시에는 30초의 타임아웃 제한을 명확히 설정하십시오.
    *   **모의 데이터 수명주기**: 물리적 FK가 없는 환경을 극복하기 위해 관계 시드가 매핑된 모의 데이터 캐시를 활용하고, [SandboxSeedingService.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Validator.Core/Services/SandboxSeedingService.cs)를 통해 데이터 적재(Seed) 및 테스트 완료 후 자동 소거(Clean-up/Truncate) 처리를 확실히 수행하십시오.

### 🔌 범주 6. 외부 코딩 에이전트 및 프로세스 제어 (External Agent & Codegen)
8.  **지시서 번들 생성 및 코딩 에이전트 CLI 프로세스 제어를 적용하십시오.**
    *   **번들 및 프롬프트 제공**: [MetadataExporter.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/MetadataExporter.cs)의 지시서 내보내기 시 DDL, 스펙, 계획서 및 의존 관계를 마크다운 하나로 묶어 제공하고, 하단에 외부 에이전트 복사/붙여넣기용 프롬프트를 명시하십시오. 대상 출력 폴더가 없을 시 선행 자동 생성을 처리하십시오. 개별 SP 분석 시에는 `output/{Schema}.{Name}/` 하위 디렉토리에, 통합 배치 시에는 `output/Jobs/{JobName}/` 하위 디렉토리에 분류 보존하여 파일 격리 무결성을 보장하십시오.
    *   **동적 코드 생성 시점 제약**: 개별 SP 분석 완료 직후에는 기동을 금지하며, 반드시 복수 SP가 엮인 통합 배치 전환 계획서 수립 및 최종 승인 완료 시점에만 외부 에이전트를 기동하십시오.
    *   **프로세스 양방향 제어**: [ExternalCliCodingEngine.cs](file:///home/moondae/git-root/ReSet/src/ReSet.Core/Services/ExternalCliCodingEngine.cs) 기동 시 대화형 흐름을 공유할 수 있도록 부모 콘솔 입출력 스트림을 직접 상속 공유하고, 취소(`CancellationToken`) 수신 시 좀비 프로세스를 예방하기 위해 하위 프로세스 트리를 강제 종료(`process.Kill(true)`)하십시오. 띄어쓰기가 포함된 프롬프트 파싱을 막기 위해 Arguments 전체를 쌍따옴표(`\"...\"`)로 래핑하여 공급하십시오.
    *   **무인 자동 기동**: CLI 배치 모드 실행 시 `--job-name` 인자가 공급되면 L3 대화형 단계를 건너뛰고 자동으로 통합 계획 및 지시서 번들을 생성해 외부 에이전트 프로세스 기동까지 연속 수행하는 CI/CD 무인 파이프라인을 지원하십시오.
    *   **자가 수정 및 TDD 테스트 피드백 루프**: 외부 에이전트 기동 전 `ValidatorAiService`를 활용해 타겟 언어 단위 테스트 코드를 생성해 적재하고, 에이전트가 로컬 테스트를 통과(L0 성공)한 코드를 작성하면 L1 정적 검사(구문/컴파일 오류 발생 시 L2 AI 검증을 건너뛰는 숏컷 적용) 및 L2 AI 의미론적 일치성 분석을 순차 수행하십시오. 검증 불일치 시 마커 주석(`<!-- FEEDBACK_START -->`, `<!-- FEEDBACK_END -->`)을 활용해 지시서 파일에 피드백을 축적하고 재수정을 위해 외부 에이전트를 재기동하도록 파이프라인을 조율하십시오.

### 🧹 범주 7. 메타데이터 정화 및 주석 보완 (Cleansing & Annotation)
9.  **메타데이터 정화 및 정책 문서 수립 가이드를 준수하십시오.**
    *   **설명 누락 추론**: 스키마 정보에 `[설명 누락]`으로 식별된 컬럼이 있는 경우, AI가 SP 내 연산 문맥을 분석하여 반드시 `[AI 추론 보완: {Schema}.{Table}.{Column} - {유추된설명}]` 형태로 마크다운에 출력하도록 유도해야 합니다.
    *   **주석-코드 모순 탐지**: 자연어 주석과 실제 쿼리 실행 연산 코드 간에 모순이 감지되는 경우, 실제 코드를 진실의 원천으로 두고 개요 섹션 하단에 `[🚨 주석 불일치 경고] {모순내용}` 형식으로 명세서를 작성하게 하십시오.
    *   **클렌징 스크립트 및 동기화**: AI 분석 성공 완료 시 보완 스크립트 파일(`*_MetadataCleansing.sql`)을 항상 무인으로 자동 생성 및 갱신하되, 실제 DB 정화는 TUI 최종 승인 및 동의 시에만 실행하십시오.
    *   **C# 보간 중괄호 이스케이프**: 프롬프트 텍스트 내부의 중괄호(`{}`)는 C# 보간 기호($) 해석 오류를 막기 위해 반드시 이중 중괄호(`{{}}`)로 이스케이프해야 합니다.
    *   **정산 정책서**: SP DDL의 상수 분기 조건 분석과 테이블 데이터 프로파일링 정보를 결합해 정산 정책서(Settlement Rulebook)를 도출하고, 지정된 5대 헤더 구조를 엄격히 준수하도록 설계하십시오.
    *   **컬럼 매핑 표 축약 금지**: CRUD 분석 및 데이터 컬럼 매핑 표 작성 시, '외 다수' 또는 '등'과 같이 컬럼 목록이나 매핑 관계를 임의로 축약하거나 생략하지 말고, 실제 대상 물리 컬럼과 이에 매핑되는 원천값을 누락 없이 1:1 대조 표에 완전하게 기술하십시오.
    *   **DDL 기반 제약 조건 작성**: 프로시저 파라미터나 컬럼 제약 조건에 대해 임의로 'NOT NULL'과 같은 주관적 단정을 짓지 말고, 오직 DDL 소스코드에 명시되어 있는 타입 제약 및 기본값 정의를 기반으로만 사실적으로 기술하십시오.
    *   **NOLOCK 힌트 격리 반영**: 레거시 쿼리 내에서 `WITH(NOLOCK)` 또는 `NOLOCK` 등의 테이블 읽기 힌트가 사용된 경우, 그에 따른 더티 리드(Dirty Read) 가능성과 같은 데이터 격리 및 정합성 특성을 명세서 내 예외 처리/제약 사항 또는 트랜잭션 설명부에 반영하고, 배치 현대화 계획서 작성 시에도 타겟 프레임워크 ORM 상에서 이에 대칭되는 트랜잭션 격리 수준으로 포팅하기 위한 코딩 가이드라인을 수립하도록 하십시오.
    *   **복합 필터의 정확한 해석**: `NOT IN`, `ISNULL` 등이 결합된 복합 필터/분기 조건을 해석할 때 논리적 환각을 철저히 배제하고 정확하게 기술하십시오. (예: '특정 값만 포함'이 아니라 '제외된 값 외의 모든 값 및 NULL 치환값 포함'으로 정확히 서술)

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

# 소스코드 일치성 검증 자동화 배치 모드 실행
dotnet run --project src/ReSet.Validator.Cli -- --spec "./output" --code "./src" --batch

# 데이터 정합성 검증용 테스트 파라미터 설계 배치 모드 실행
dotnet run --project src/ReSet.Validator.Cli -- --spec "./output" --gen-inputs --batch

# 검증용 모의 테이블 데이터(Mock Data) 자동 생성 배치 모드 실행
dotnet run --project src/ReSet.Validator.Cli -- --spec "./output" --gen-mock-data --batch

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
- [ ] `dotnet test` 명령어를 실행하여 100개의 단위 테스트가 모두 예외 없이 100% 통과(Passed)하였는가?
- [ ] API Key 등 비공개 자격증명이 소스코드나 `appsettings.json`에 하드코딩되지 않고 `appsettings.local.json` 또는 로컬 환경 변수로 격리되었는가?
- [ ] DB 메타데이터, AI 결과 원문 등을 Spectre.Console TUI에 출력할 때 모든 출력 부에 `Markup.Escape()` 조치를 적용했는가?
- [ ] Stored Procedure 실행 및 외부 샌드박스 데이터 수집 시, DB 연결 실패 시 예외 격리(Soft Fail 및 DTO FAIL 상태 주입) 처리가 정상 적용되었는가?
- [ ] 신규 추가된 C# 타겟 러너 내 `DbTransaction`이 작업 결과와 관계없이 항상 `Rollback()` 되도록 누락 없이 명세했는가?
- [ ] 작업 완료 후 수정 및 추가된 모든 코드가 솔루션 컴파일 및 아키텍처 규칙을 위반하지 않는지 재검토했는가?
