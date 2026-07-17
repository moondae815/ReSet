# ReSet (**RE**verse engineering **SET**tlement)

[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-blueviolet.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![SQL Server](https://img.shields.io/badge/SQL%20Server-Database-red.svg)](https://www.microsoft.com/sql-server)
[![AI Providers](https://img.shields.io/badge/AI--Providers-OpenAI%20%7C%20Claude%20%7C%20Google%20%7C%20Ollama%20%7C%20Z.ai-orange.svg)](#)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](#)

본 프로젝트는 **SQL Server**에 저장된 Stored Procedure(SP)를 심층 분석하여, AI(OpenAI, Ollama, Claude, Google Gemini, Z.ai 등)를 통해 사용자 정의 지침에 맞춘 마크다운 형식의 기능 명세서를 자동 생성하는 개발자용 터미널 기반 CLI(TUI) 도구입니다.

---

## 🚀 주요 특징 (Key Features)

본 도구는 크게 **Stored Procedure 역공학(Analyzer)**과 **구현 코드/데이터 검증(Validator)**의 유기적인 결합을 통해, 레거시 DB 비즈니스 로직을 현대적인 아키텍처로 안전하게 마이그레이션하도록 돕는 강력한 개발자용 TUI 도구입니다.

### 1. 지능형 역공학 및 의존성 분석 (Analyzer)
* **재귀적 하이브리드 의존성 추적**: 깊이 우선 탐색(DFS) 방식으로 SP가 참조하는 테이블, 뷰, UDF 및 타 SP를 재귀 추적하여 전체 의존성 구조를 파악합니다. (권한 오류 발생 시 경고 기록 후 안전한 Soft Fail 제공)
* **스키마 및 주석 자동 수집 및 최적화 필터링**: 데이터 타입, Null 여부, PK/FK 관계뿐만 아니라 컬럼의 Identity, 기본값 정의, 테이블 인덱스 메타데이터 및 시스템 설명(`MS_Description`)까지 수집하여 도메인 맥락으로 자동 주입합니다. 특히 AST 분석 정보와 연동하여 실제 참조 컬럼, PK/FK, 인덱스 구성 컬럼만 상세 스키마에 선별적으로 노출함으로써 프롬프트 토큰을 획기적으로 절약합니다.
* **T-SQL AST 정적 분석 (ScriptDom)**: Microsoft 공식 ScriptDom 분석기를 탑재하여 DDL의 CRUD 성격별(SELECT/INSERT/UPDATE/DELETE) 테이블 분류, 테이블별 물리 참조 컬럼(Referenced Columns) 및 Alias 정보 추출(Pre-pass 선행 별칭 스캔), 중첩 분기(IF/WHILE) 들여쓰기 요약, 동적 SQL 및 UDF/Linked Server 원격 참조 자동 감지, 그리고 데이터베이스 호환성 수준별 파서 동적 매핑을 지원합니다.
* **다중 포맷 메타데이터 수출**: 분석에 사용된 원천 데이터를 구조화된 JSON, 프롬프트 텍스트, 그리고 개별 객체 단위 DDL/MD 파일 구조로 자동 분산 저장(Dump)합니다.

### 2. 3단계 신뢰성 검증 파이프라인 (Verification)
* **Level 1 (기계적 정적 검증 및 자동 정화)**: `Markdig` 파서로 구조적 필수 섹션을 검증하고, `mermaid-cli` 컴파일 테스트 또는 구문 린팅을 통해 Mermaid 다이어그램 오류를 방지합니다. 또한, 자체 **Mermaid 다이어그램 자동 정화기(Cleanse)**를 탑재하여 잘못된 화살표 기호 보정, 라벨 따옴표 정형화, 노드 ID 특수문자 제거, 특수문자 포함 라벨 큰따옴표 자동 래핑 등 자동 보정을 거친 정화된 마크다운을 최종 생성물로 채택합니다.
* **Level 2 (Actor-Critic 및 자가 교정)**: `ActorEffort`가 `dynamic`으로 지정된 경우, Low/Medium/High Effort를 적용한 3종의 명세서 후보를 병렬 생성합니다. 이후, 설정에 따라 지정된 **Critic(리뷰어) 에이전트**가 각 후보에 대해 5대 기준(정합성, CRUD 완전성, 인터페이스 구체성, 예외/트랜잭션, 시각화 가독성 각 10점, 총 50점 만점)으로 정량 채점을 가동합니다. 결함이 없고 100점 환산 기준 90점 이상인 우수 후보는 즉시 채택(**스마트 Fast-Pass**)하며, 그렇지 않은 경우 **Consolidator(합성기) 에이전트**가 각 항목별 고득점 후보의 파트를 Source of Truth 삼아 병합 조립하는 Actor-Critic 앙상블 모델을 가동합니다. 특히 로컬 Ollama 모델 실행의 경우, 1회차 생성 단계뿐만 아니라 피드백 보완 및 재생성 루프(L1/L2 자가 수정 및 L3 사용자 피드백)에서도 피드백 키워드를 분석하여 연관된 파트만 선택적으로 분할 재생성 및 조립하는 최적화 파이프라인을 지원합니다. (단일 모델 가동 시에는 설정 한도 내에서 자가 수정 루프를 수행하며, 최종 합성/보완본에 대한 Critic 점수를 최종 산출물 상단에 보존)
* **Level 3 (인간 승인 피드백 루프)**: TUI 모드에서 실시간 문서 미리보기를 제공하며, 개발자의 자연어 보완 피드백을 수렴하여 완벽한 설계서가 나올 때까지 재생성 및 검증을 반복합니다. (무인 배치 모드에서는 생략)
* **하이브리드 영문화 프롬프트 설계**: AI 행동 지침 및 제약 규칙은 100% 영어로 작성하여 명령 이행력(Instruction Following)과 환각 차단율을 극대화하되, 최종 산출물은 완벽한 한글로 작성되도록 설계하여 번역 편향과 오인 분석을 동시에 방어합니다.

### 3. 배치 현대화 설계 및 비용 최적화 (Modernization & Cache)
* **순차적 배치 현대화 계획 수립**: 분석된 여러 SP들의 명세서를 사용자가 선택한 순서대로 조합하여, 워크플로우 제어, 대용량 페이징, 오류 처리 정책이 설계된 통합 배치 계획서(`*_BatchMigrationPlan.md`)를 작성합니다. (배치 작업 단위는 `--job-name` 옵션을 통해 식별 관리됩니다.)
* **코딩 에이전트 자동 기동 브릿지 및 자가 수정 피드백 루프**: 통합 배치 전환 계획 수립 및 최종 승인이 완료되면 Claude Code, Antigravity CLI 등 지정된 전문 코딩 에이전트를 자식 프로세스로 자동 기동하여 전체 배치 코드를 통합적으로 연쇄 생성합니다. 특히 기동 시 타겟 단위 테스트 및 아키텍처 제약 테스트를 미리 생성해 배포하고, 빌드/테스트(L0) 성공 통과 시 정적 린터(L1) 및 AI 의미론적 대조(L2)를 거치며 스스로 코드를 고치는 자가 수정 루프(Self-Correction Loop) 브릿지를 탑재하여 최종 코드 품질을 극대화합니다.
* **하이브리드 동적 SQL 및 Linked Server 대응**: 정적 분석이 까다로운 동적 쿼리(EXEC, sp_executesql)에 대해 DDL 텍스트를 Regex로 2차 분석하여 동적 참조 테이블의 실시간 스키마까지 자동 병합 수집하며, Linked Server 식별자 패턴 감지를 결합해 안전하고 완벽한 현대화 전환 가이드를 제공합니다.
* **해시 기반 로컬 증분 캐싱**: SP 및 관련 의존성 DDL의 복합 SHA-256 해시를 체크하여, 변경이 없을 시 AI 분석을 건너뛰고 기존 마크다운을 복원하여 비용과 시간을 혁신적으로 절감합니다.

### 4. 코드 일치성 및 데이터 정합성 검증 (Validator)
* **설계서 vs 구현 소스코드 일치성**: C#/Java 코드를 정적으로 분석하고 AI Gap 분석을 실행하여, 명세서 대비 입출력 파라미터, 연산 분기, 트랜잭션 구현 불일치점(Gap Report)을 도출합니다.
* **관계지향 모의 데이터(Mock Data) 자동 생성 및 격리 적재**: 보안 규정으로 인해 운영 데이터를 활용할 수 없는 상황에 대처하여, AI가 테이블 DDL과 JOIN문을 파싱해 조인 컬럼 시드 값이 연결된 고품질 모의 데이터(`--gen-mock-data`)를 자동 생성하고, 테스트 실행 시 데이터베이스에 임시 Seeding 한 후 완료 시 자동 복구(Clean-up)합니다.
* **하이브리드 런타임 수집 & 1:1 대조**: 테스트 케이스 입력을 자동 설계하여 Legacy DB의 SP를 호출하고, 마이그레이션된 소스코드(C# DLL 리플렉션 로드 / Java 외부 프로세스 실행)를 안전하게 트랜잭션 격리(Rollback) 및 타임아웃 하에 구동한 뒤 결과셋 데이터를 1:1로 정밀 비교 대조(`*_CompareReport.md`)합니다.
* **풍부한 AI 공급자 및 TUI 인터랙션**: OpenAI, Claude, Google, 로컬 Ollama, Z.ai를 지원하며, 로컬 세션 보존, 실시간 자동완성 검색/경로 완성, 비동기 작업 취소(`CancellationToken`) 및 견고한 텍스트 이스케이프(`Markup.Escape`)가 적용되어 있습니다.

### 5. 메타데이터 정화 및 주석 보완 (Cleansing & Annotation)
* **테이블 스키마 설명 누락 역추론**: 테이블 및 컬럼 설명(`MS_Description`)이 누락된 항목을 `[설명 누락]`으로 식별한 뒤, 해당 컬럼이 활용되는 SP/UDF/뷰 쿼리의 연산 및 대입 문맥을 추론하여 AI가 `[AI 추론 보완: {Schema}.{Table}.{Column} - {설명}]` 형태로 의미를 자동 역추론합니다.
* **코드-주석 불일치 모순 감지**: SP 내부의 개발용 자연어 주석과 실제 SQL 연산/대입 코드 간의 불일치를 자동 비교 분석하여 탐지합니다. 불일치가 발견되면 실제 코드를 진실의 원천으로 두고 정책 문서를 수립하며, 개요 섹션 하단에 `[🚨 주석 불일치 경고]` 문구를 표시해 보완을 알립니다.
* **SQL 스크립트 무인 내보내기 및 DB 동기화**: 분석이 완료되면 사용자의 반영 선택 여부와 관계없이 Extended Property 설명을 채우기 위한 이중 분기형 SQL 스크립트 파일(`*_MetadataCleansing.sql`)을 항상 `output/cleansing/` 디렉토리에 자동으로 생성해 둡니다. TUI 환경에서 최종 승인 동의를 얻으면 샌드박스 또는 실제 데이터베이스에 쿼리를 전송하여 메타데이터를 영구 역동기화(Sync)합니다.

### 6. 통합 정산 정책 문서 도출 (Settlement Policy Rulebook)
* **정적/동적 하이브리드 정책 도출**: 레거시 DB 내 Stored Procedure 코드(DDL)에 숨겨진 비즈니스 분기 조건(예: `WHERE Status = 'S02'`)과 실제 공통 코드 및 마스터 설정 테이블에 적재되어 있는 데이터(예: `S02 = 정산보류`)를 1:1 결합 및 분석(Data Profiling)하여, 실무진과 개발진 모두 즉시 참고할 수 있는 통합 '정산 정책서(Settlement Rulebook)'를 자동 작성합니다.

### 7. 실시간 병렬 태스크 진행률 시각화 (CLI Progress)
* **비결합 멀티태스크 진행률 추적**: 관심사 분리(Clean Architecture) 원칙에 입각하여 Core 비즈니스 로직은 화면 렌더링에 관여하지 않고, 추상화된 `IMultiProgressScope` 인터페이스와 `NullProgressScope`를 주입받아 비동기 진행률 정보를 통보합니다. TUI 프로젝트 단에서는 `Spectre.Console`의 `Progress` 컴포넌트와 연동되어 백그라운드 렌더링 스레드를 제어하며, 회전하는 도트 스피너(Spinner)와 누적 경과 시간(Elapsed Time) 정보 등을 직관적으로 출력해 대기 상태에 대한 피로감을 최소화합니다. 특히 태스크 완료 및 실패 시에도 원래 설명(Description)을 보존해 화면 렌더링 유실을 방지합니다.

### 8. 영속적인 실행 로깅 시스템 (Serilog File Sink & Clean Logging)
* **TUI 비파괴식 Serilog 파일 로깅**: TUI 대화형 화면 및 진행 바가 로깅 출력으로 인해 깨지지 않도록 Serilog는 **오직 파일 전용(File Sink)**으로만 분리 가동됩니다. `appsettings.json` 설정을 통해 로깅 대상 디렉토리, 기록 등급, 보존 주기를 자유롭게 지정할 수 있습니다.
* **마크업 자동 정화 유틸리티**: 로그 파일에 기록하기 직전에 Spectre.Console 마크업 스타일 태그(예: `[yellow]`, `[/]`)를 자동 탐색하여 완전 제거(StripMarkup)함으로써, 로그 텍스트 파일의 가독성과 영속성을 보장합니다.

---

## 📊 핵심 아키텍처 및 워크플로우 (Core Workflow)

본 프로젝트는 깊이 우선 탐색(DFS) 및 동적 SQL Regex 추출을 결합한 하이브리드 의존성 탐색, 3단계 신뢰성 검증(L1: 정적, L2: Actor-Critic AI 검토, L3: 인간 승인), 그리고 1:1 런타임 결과 정합성 검증 엔진을 갖추고 있습니다.

상세한 데이터 흐름 및 모듈 아키텍처는 [architecture.md](file:///home/moondae/git-root/ReSet/docs/architecture.md) 문서를, 프로젝트 일정 및 마일스톤 흐름은 [roadmap.md](file:///home/moondae/git-root/ReSet/docs/roadmap.md) 문서를 참고해 주십시오.

---

## 🛠 요구 사항 및 환경 구성 (Prerequisites)

도구를 빌드하고 실행하기 위해서는 아래의 최소 환경 구성이 필요합니다.

*   **.NET SDK 10.0** 이상 설치
*   **SQL Server** (메타데이터 쿼리 및 SP 실행용)
*   **Node.js & npm** (선택사항, Mermaid 다이어그램 이미지 컴파일 및 L1 정적 검사 수행 시 `mermaid-cli` 연동 필요)
    ```bash
    npm install -g @mermaid-js/mermaid-cli
    ```

---

## 📂 프로젝트 구조 (Project Structure)

```
ReSet/
│
├── ReSet.slnx      # .NET 솔루션 파일
│
├── src/
│   ├── ReSet.Core/            # [클래스 라이브러리] 핵심 비즈니스 로직 및 AI 커뮤니케이션
│   │   ├── Models/                 # SpDefinition, DependencyInfo 데이터 모델
│   │   └── Services/               # DB 조회, AI API 통신, 캐싱 및 코딩 엔진 연동
│   │
│   ├── ReSet.Cli/             # [콘솔 애플리케이션] Spectre.Console 기반 TUI (설계서 생성)
│   │   ├── Program.cs              # CLI 진입점 및 대화형 워크플로우 제어
│   │   ├── CodingEngineFactory.cs  # 설정 기반 외부 코딩 에이전트 생성 팩토리
│   │   ├── appsettings.json        # 기본 설정 파일
│   │   └── instructions.md         # AI 분석 세부 마크다운 지침 템플릿
│   │
│   ├── ReSet.Validator.Core/  # [클래스 라이브러리] 구현 정적/논리 검사 및 데이터 대조 서비스
│   │   ├── Models/                 # ValidationResult, MockDataDto 모델
│   │   └── Services/               # FileMapping, SandboxSeeding, DataComparison 서비스
│   │
│   └── ReSet.Validator.Cli/   # [콘솔 애플리케이션] TUI 및 배치 모드 (소스코드 및 데이터 정합성 대조 검증기)
│       ├── Program.cs              # 검증기 CLI 진입점 및 흐름 제어
│       └── appsettings.json        # 검증기용 설정 파일
│
├── tests/
│   └── ReSet.Core.Tests/      # [단위 테스트 프로젝트] xUnit 기반 단위 테스트
│
└── output/                          # [산출물 폴더] 생성된 스펙, 계획서, 모의 데이터 및 정합성 리포트 저장소
    ├── [Schema].[SP이름]/           # SP 개별 분석 산출물이 격리 보존되는 전용 하위 폴더
    │   ├── [Schema].[SP이름]_Spec.md  # SP 개별 비즈니스 설계 명세서
    │   ├── [Schema].[SP이름]_MigrationInstructions.md # 개별 SP 마이그레이션 지시서 번들
    │   ├── [Schema].[SP이름]_Thinking.md  # AI 모델의 추론 과정 로그
    │   ├── [Schema].[SP이름]_RawContext.md # AI 모델에 실제 전송된 조립 완료 프롬프트 원문 마크다운
    │   └── [Schema].[SP이름]_Raw/     # 의존 테이블 스키마 및 참조 UDF/SP DDL 개별 분산 덤프 폴더
    │
    ├── Jobs/[JobName]/              # 통합 배치 작업용 산출물이 격리 보존되는 전용 하위 폴더
    │   ├── [JobName]_BatchMigrationPlan.md   # 통합 배치 전환 계획서
    │   └── [JobName]_MigrationInstructions.md # 통합 마이그레이션 지시서 번들
    │
    └── validation/                 # 소스코드 정적 검증 및 데이터 정합성 리포트 저장 폴더
        ├── [SP이름]_CompareReport.md  # 1:1 데이터 정합성 비교 분석 보고서
        └── mock/
            └── [SP이름]_mock_data.json # 검증에 활용된 관계형 모의 데이터 캐시
```

---

## ⚙ 설정 방법 (Configuration)

### 1. `appsettings.json` 설정

프로그램 실행 전, 분석기(`src/ReSet.Cli/appsettings.json`) 및 검증기(`src/ReSet.Validator.Cli/appsettings.json`)용 설정 파일을 열어 각각의 목적에 맞게 필요한 데이터베이스 환경 및 AI 설정을 지정합니다. 자격 증명 누출 방지를 위해 `ApiKey`는 비워두는 것을 권장합니다.

#### 1) 분석기 설정 (`src/ReSet.Cli/appsettings.json`)
역공학 및 마이그레이션 설계를 위한 주요 설정 파일입니다.
```json
{
  "DatabaseSettings": {
    "Server": "localhost",          // SQL Server 주소
    "Database": "Northwind",        // 대상 데이터베이스 이름
    "MaxDependencyDepth": 3         // 재귀적 의존성 탐색의 최대 깊이 (기본값: 3)
  },
  "AiSettings": {
    "Provider": "Claude",          // 활성화할 AI 제공자 ("OpenAI" | "Google" | "Claude" | "Ollama" | "Z.ai")
    "ModelName": "claude-sonnet-4-6", // 사용할 LLM 모델명
    "Temperature": 0.2,            // 분석의 일관성을 위해 낮게(0.0 ~ 0.3) 설정을 권장합니다.
    "MaxL2Attempts": 2,            // L2 AI 교차 리뷰 실패 시 추가로 재시도할 자가 보완 횟수 (1 이상의 정수 또는 "unlimited" 지정 시 검증 완료까지 무제한)
    "TimeoutSeconds": 300,         // AI API 호출 시 HttpClient 타임아웃 시간 (초 단위, 기본값: 300)
    "ActorEffort": "dynamic",      // [Actor-Critic] dynamic 설정 시 Low/Medium/High 차등 Effort로 3종 후보군 생성 및 점진적 합성 가동
    "Critic": {
      "Provider": "OpenAI",        // [Actor-Critic] 평가를 담당할 Critic의 AI 제공자
      "ModelName": "gpt-5.4",
      "Effort": "high"             // [Actor-Critic] Critic의 추론 강도 (low | medium | high)
    },
    "Consolidator": {
      "Provider": "Claude",        // [Actor-Critic] 최종 합성을 담당할 Consolidator의 AI 제공자
      "ModelName": "claude-sonnet-4-6",
      "Effort": "medium"           // [Actor-Critic] Consolidator의 추론 강도
    },
    "Providers": {
      "OpenAI": {
        "ApiKey": "",              // OpenAI API 키
        "Endpoint": "https://api.openai.com/v1"
      },
      "Google": {
        "ApiKey": "",              // Google API 키 (Google AI Studio)
        "Endpoint": "https://generativelanguage.googleapis.com"
      },
      "Claude": {
        "ApiKey": "",              // Claude API 키 (Claude Console)
        "Endpoint": "https://api.anthropic.com"
      },
      "Ollama": {
        "Endpoint": "http://localhost:11434" // 로컬 Ollama 엔드포인트
      },
      "Z.ai": {
        "ApiKey": "",              // Z.ai API 키
        "Endpoint": "https://api.z.ai/api"
      }
    }
  },
  "LoggingSettings": {
    "LogDirectory": "./output/logs",       // 실행 로그가 저장될 출력 디렉터리
    "MinimumLevel": "Information",         // 최소 기록 로그 레벨 (Verbose | Debug | Information | Warning | Error | Fatal)
    "RetainedFileCountLimit": 31           // 로그 파일 최대 보존 개수 (일별 롤링 파일 갯수)
  },
  "OutputSettings": {
    "Directory": "./output",       // 명세서 파일이 저장될 출력 디렉터리
    "InstructionsFile": "./instructions.md", // 분석 규칙 지침 파일 명칭
    "SaveRawJson": true,           // [설정] SpDefinition JSON 파일 저장 여부
    "SaveRawContext": true,        // [설정] 조립된 프롬프트 마크다운 원문 저장 여부
    "SaveRawFiles": true,          // [설정] 의존성 개별 객체 파일/폴더 분산 덤프 여부
    "EnableCache": true            // [설정] DDL 해시 기반 로컬 증분 분석 캐싱 활성화 여부
  },
  "MigrationSettings": {
    "Enabled": true,               // [설정] 신규 시스템 현대화 설계서 추가 생성 활성화 여부
    "TargetLanguage": "C#"         // [설정] 제안할 신규 시스템의 배치 프레임워크 언어 (C# | Java 등)
  },
  "ValidationSettings": {
    "UseMermaidCli": false         // [설정] mmdc(mermaid-cli)를 이용한 Mermaid 실시간 렌더링 검사 수행 여부 (기본값: false)
  },
  "CodegenSettings": {
    "Enabled": false,                     // [설정] 분석 완료 후 코딩 에이전트 브릿지 자동 실행 활성화 여부
    "Engine": "claude",                   // [설정] 기본 코딩 엔진 ("claude" | "agy" | "codex")
    "TargetProjectDirectory": "./src",    // [설정] 마이그레이션 코드가 저장될 대상 프로젝트 절대/상대 경로
    "Engines": {
      "claude": {
        "Command": "claude",              // 실행할 Claude CLI 명령어
        "Arguments": "\"write code using {instructions}\"" // 인자 양식 ({instructions}에 지시서 절대 경로가 자동 바인딩)
      },
      "agy": {
        "Command": "agy",                 // Antigravity CLI 명령어 (https://antigravity.google/docs/cli-overview)
        "Arguments": "run {instructions}"
      },
      "codex": {
        "Command": "codex",               // Codex CLI 명령어 (https://developers.openai.com/codex/cli/features)
        "Arguments": "\"{instructions}\""
      }
    }
  }
}
```

> [!TIP]
> **💡 Actor-Critic 및 점진적 합성 가동 가이드**
> * **활성화 조건**: `AiSettings:ActorEffort` 값을 `"dynamic"`으로 지정하면 Actor-Critic 및 점진적 조각 합성(Consolidation) 루프가 활성화됩니다.
> * **다중 후보군 병렬 생성**: 활성화 시, 서로 다른 추론 깊이(`Low`, `Medium`, `High` Effort)를 할당받은 3종의 명세서 후보를 동시에 병렬 생성합니다.
> * **이종 모델 앙상블 권장**: 자가 편향(Self-Confirmation Bias) 방지를 위해 기본 Actor/Consolidator와 Critic의 AI 제공자(`Provider`) 및 모델을 서로 다르게(예: Actor/Consolidator는 Claude, Critic은 OpenAI) 교차 지정하여 검증의 객관성을 극대화하기를 권장합니다.
> * **단일 모델 모드 우회**: `ActorEffort`가 `"dynamic"`이 아닌 단일 값(예: `"low"`, `"medium"`, `"high"`)인 경우에는 Actor-Critic 합성을 건너뛰고, 설정 한도(`MaxL2Attempts`) 내에서 자가 수정(Self-Correction)만을 수행하는 단일 모델 모드로 자동 우회 구동됩니다. (Ollama 구동 시에는 effort에 따라 온도가 0.1~0.9 범위로 자동 대응됩니다.)

#### 2) 검증기 설정 (`src/ReSet.Validator.Cli/appsettings.json`)
마이그레이션된 소스 코드와 설계서의 일치성을 검증하기 위한 설정 파일입니다.
```json
{
  "AiSettings": {
    "Provider": "Claude",              // 활성화할 AI 제공자 ("OpenAI" | "Google" | "Claude" | "Ollama" | "Z.ai")
    "ModelName": "claude-sonnet-4-6",
    "Temperature": 0.1,
    "MaxL2Attempts": 2,
    "TimeoutSeconds": 300,         // AI API 호출 시 HttpClient 타임아웃 시간 (초 단위, 기본값: 300)
    "Providers": {
      "OpenAI": {
        "ApiKey": "",
        "Endpoint": "https://api.openai.com/v1"
      },
      "Google": {
        "ApiKey": "",
        "Endpoint": "https://generativelanguage.googleapis.com"
      },
      "Claude": {
        "ApiKey": "",
        "Endpoint": "https://api.anthropic.com"
      },
      "Ollama": {
        "Endpoint": "http://localhost:11434"
      },
      "Z.ai": {
        "ApiKey": "",
        "Endpoint": "https://api.z.ai/api"
      }
    }
  },
  "ValidationSettings": {
    "SpecDirectory": "./output",          // [설정] 검증에 쓰일 명세서 폴더
    "SourceCodeDirectory": "./src",       // [설정] 검증에 쓰일 구현 소스코드 폴더
    "TargetLanguage": "Auto",             // [설정] 검증 대상 언어 ("Auto" | "C#" | "Java")
    "OutputDirectory": "./output/validation" // [설정] 일치성 Gap 보고서 저장 경로
  }
}
```

### 2. 보안 가이드: `appsettings.local.json` 설정 (권장)
보안상 안전하게 AI API Key 정보를 관리하기 위해, Git에 추적되지 않는 로컬 전용 설정 파일을 사용하는 것을 권장합니다.

1. `src/ReSet.Cli/` 디렉터리에 `appsettings.local.json` 파일을 만듭니다. (이 파일은 `.gitignore`에 무시 대상 파일로 이미 등록되어 안전합니다.)
2. 생성된 `appsettings.local.json` 파일 내에 다음과 같이 발급받은 API 키 설정을 넣으면 로컬 실행 시 보안 키가 우선적으로 적용됩니다.
   ```json
   {
     "AiSettings": {
       "Providers": {
         "OpenAI": {
           "ApiKey": "여기에_새로_발급받은_API키_입력"
         },
         "Google": {
           "ApiKey": "여기에_새로_발급받은_API키_입력"
         },
         "Claude": {
           "ApiKey": "여기에_새로_발급받은_API키_입력"
         },
         "Z.ai": {
           "ApiKey": "여기에_새로_발급받은_API키_입력"
         }
       }
     }
   }
   ```

---

## 📦 단일 파일 독립 실행형 배포 (Single File Deployment)

대상 컴퓨터에 .NET SDK나 런타임이 설치되어 있지 않더라도, 실행 파일 하나만 복사해서 바로 실행할 수 있도록 **자가 포함(Self-contained) 단일 파일(Single File)** 형태로 배포할 수 있습니다.

### 1. 운영체제별 배포 명령어
각 플랫폼(OS)에 맞춰 아래의 배포 명령어를 터미널에서 실행합니다.

*   **Linux (x64)**:
    ```bash
    dotnet publish src/ReSet.Cli/ReSet.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ~/ReSet/Dist/Linux/Cli
    dotnet publish src/ReSet.Validator.Cli/ReSet.Validator.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ~/ReSet/Dist/Linux/Validator
    ```

*   **macOS (Intel x64)**:
    ```bash
    dotnet publish src/ReSet.Cli/ReSet.Cli.csproj -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -o ~/ReSet/Dist/Mac/Cli
    dotnet publish src/ReSet.Validator.Cli/ReSet.Validator.Cli.csproj -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -o ~/ReSet/Dist/Mac/Validator
    ```

*   **macOS (Apple Silicon - M1/M2/M3 등 ARM64)**:
    ```bash
    dotnet publish src/ReSet.Cli/ReSet.Cli.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o ~/ReSet/Dist/Mac_ARM/Cli
    dotnet publish src/ReSet.Validator.Cli/ReSet.Validator.Cli.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o ~/ReSet/Dist/Mac_ARM/Validator
    ```

*   **Windows (x64)**:
    ```bash
    dotnet publish src/ReSet.Cli/ReSet.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ~/ReSet/Dist/Win/Cli
    dotnet publish src/ReSet.Validator.Cli/ReSet.Validator.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ~/ReSet/Dist/Win/Validator
    ```

### 2. 배포된 단일 파일 실행 방법
배포가 완료된 출력 폴더(예: `~/ReSet/Dist/Linux/Cli`) 내에는 단일 바이너리(실행 파일)가 생성됩니다.

*   **Linux / macOS 환경**:
    터미널에서 생성된 실행 파일에 실행 권한을 부여한 뒤 실행합니다.
    ```bash
    # 1. 실행 권한 부여 (필요한 경우 최초 1회)
    chmod +x ~/ReSet/Dist/Linux/Cli/ReSet.Cli
    
    # 2. 프로그램 실행
    ~/ReSet/Dist/Linux/Cli/ReSet.Cli
    ```
*   **Windows 환경**:
    명령 프롬프트(cmd) 또는 PowerShell에서 생성된 `ReSet.Cli.exe` 파일을 직접 더블 클릭하거나 CLI 환경에서 구동합니다.
    ```powershell
    # PowerShell 실행 예시
    .\ReSet.Cli.exe
    ```

---

## 🏃 실행 및 사용 방법 (Running the Tool)
 
### 1. 대화형 TUI 모드 실행
기본적으로 아무 아규먼트 없이 실행하면 로그인 정보 입력 및 메인 메뉴 선택이 가능한 TUI 모드로 시작합니다.
```bash
dotnet run --project src/ReSet.Cli
```
1. DB 계정(ID)과 패스워드를 입력하여 SQL Server에 로그인합니다.
2. 로그인 성공 시 아래 **메인 메뉴**가 화면에 표시됩니다:
   * **`1. Stored Procedure 개별 분석 명세서 작성`**:
     SP를 1개 선택하여, 해당 프로시저의 비즈니스 로직과 데이터 입출력 명세서(`*_Spec.md`)를 작성합니다. (이때 개별 SP 마이그레이션용 지시서 번들 `*_MigrationInstructions.md`도 자동 생성됩니다.)
   * **`2. 기분석 명세서 통합 배치 전환 계획 수립 (Multi-SP)`**:
     출력 디렉터리에 축적된 `*_Spec.md` 목록 중에서 통합할 대상들을 **원하는 순서대로 하나씩 선택**하여 배치 단계를 구성하고, Job 이름(예: `Daily_Order_Job`)을 입력하여 통합 배치 전환 계획서(`*_BatchMigrationPlan.md`)를 작성합니다.
     * **이전 메뉴로 돌아가기**: 파일 선택 화면의 최상단에 제공되는 `[-- 메인 메뉴로 돌아가기 --]` 옵션을 선택하여 이전 메인 메뉴로 안전하게 되돌아올 수 있습니다.
     * **대칭형 검증 적용**: 전환 계획서가 생성된 후에는 1단계와 대칭되는 **3단계 검증 파이프라인(L1 린터 -> L2 AI 리뷰 -> L3 사용자 피드백 반영 및 컨펌)**을 수행하며, 최종 승인 시에만 파일로 저장됩니다.
     * **통합 소스 코드 자동 생성 및 에이전트 기동**: 최종 컨펌 및 저장이 완료되면, 복원된 SP 메타데이터들을 바탕으로 통합 마이그레이션 지시서 (`{JobName}_MigrationInstructions.md`)를 저장하고 외부 코딩 에이전트(Claude Code 등)를 자동/선택 기동하여 전체 코드를 생성합니다.
   * **`3. 정산 정책 문서 도출 (Settlement Policy Rulebook)`**:
     정산 로직 및 데이터를 활용하여 비즈니스 관점의 통합 정책 정의서(`*_Settlement_Policy_Rulebook.md`)를 도출합니다. 분석할 SP들을 순차 선택하고 Job 이름을 입력하여 정책서를 생성합니다.
   * **`4. 종료 (Exit)`**: 도구를 완전히 종료합니다.

### 2. 배치 모드 및 CLI 자동화 실행 (Batch Mode)
명령줄 아규먼트(`--conn`, `--all`, `--sp`, `--policy`) 또는 환경 변수(`SP_ANALYZER_CONN_STR`)를 통해 로그인 및 TUI 메뉴 단계를 완전히 건너뛰고 무인 대량 일괄 처리가 가능합니다.

- **명령줄 옵션**:
  - `--conn <연결문자열>`: 분석용 데이터베이스 연결 문자열을 직접 지정합니다. (생략 시 `SP_ANALYZER_CONN_STR` 환경 변수 값을 조회합니다.)
  - `--all`: 데이터베이스 내의 모든 Stored Procedure를 일괄 분석합니다.
  - `--sp <SP이름1,SP이름2,...>`: 특정 Stored Procedure들만 지정하여 분석합니다. 쉼표(`,`)로 구분하며 스키마명을 포함(`dbo.USP_1`)하거나 생략(`USP_1`)할 수 있습니다.
  - `--policy`: 정산 정책 문서 도출을 활성화합니다.
  - `--policy-sps <SP이름1,SP이름2,...>`: 정산 정책 도출에 쓰일 분석 대상 SP들을 지정합니다. (생략 시 전체 SP가 분석 대상이 됩니다.)
  - `--job-name <작업이름>`: 배치 모드 실행 시 지정된 이름으로 개별 명세서들을 엮어 **통합 배치 전환 계획 및 통합 마이그레이션 지시서 번들을 자동으로 일괄 생성**하도록 지시합니다. (정산 정책 문서 작성 시에도 파일명 접두사로 기능합니다.)
  - `--codegen`: (TUI 전용) 통합 배치 전환 계획 수립 최종 승인 완료 후, 자동으로 코딩 에이전트 브릿지 프로세스를 기동하여 소스 코드를 생성하도록 설정합니다. (배치 모드에서 `--job-name` 지정 시에도 함께 적용 가능합니다.)
  - `--engine <엔진명>`: 코딩 에이전트 종류를 명시적으로 지정합니다. (`claude` | `agy` | `codex`)
  
- **배치 실행 예시**:
  - **특정 SP 지정 분석**:
    ```bash
    dotnet run --project src/ReSet.Cli -- --conn "Server=localhost;Database=my_db;User ID=sa;Password=my_password;TrustServerCertificate=true" --sp dbo.USP_GetUsers,dbo.USP_UpdateOrder
    ```
  - **전체 SP 일괄 분석**:
    ```bash
    dotnet run --project src/ReSet.Cli -- --conn "Server=localhost;Database=my_db;User ID=sa;Password=my_password;TrustServerCertificate=true" --all
    ```

> [!NOTE]
> 배치 모드로 대량 실행 중 특정 SP에 대한 메타데이터 조회 실패 또는 AI 통신 에러가 발생하더라도, 해당 SP만 에러 로그가 출력되고 스킵(try-catch 격리)되며 전체 배치 작업은 중단 없이 다음 SP 분석을 계속 수행합니다.

### 3. 코드 일치성 검증 및 데이터 정합성 검증 (ReSet.Validator)
역공학 마이그레이션이 끝난 뒤, 생성된 명세서와 실제 마이그레이션 소스코드가 동일하게 구현되었는지 검증하고, 레거시 DB와 실제 실행 결과 정합성을 대조할 때 실행합니다.

*   **대화형 TUI 모드 실행**:
    ```bash
    dotnet run --project src/ReSet.Validator.Cli
    ```
    *   **1. 설계서 vs 마이그레이션 소스코드 일치성 검증 (L1/L2/L3)**: C#/Java 소스코드 정적 분석 및 AI 의미론적 Gap 분석, 인간 피드백 루프를 가동하여 검증합니다.
    *   **2. 데이터 정합성 검증용 테스트 파라미터 설계 (AI)**: 설계서(`*_Spec.md`)를 분석해 AI가 정상/경계값/오류 시나리오 테스트 파라미터 JSON(`*_test_inputs.json`)을 생성합니다.
    *   **3. 검증용 모의 테이블 데이터(Mock Data) 자동 생성 및 캐싱 (AI)**: 원본 메타데이터 및 설계서를 분석해 테이블 간의 조인 키 난수 시드가 일치하는 모의 데이터(`*_mock_data.json`)를 생성하여 캐싱합니다.
    *   **4. 원본 Stored Procedure 실행 데이터 수집 (Legacy DB)**: 생성된 테스트 입력값 JSON을 기반으로 실제 Legacy DB에 접근해 SP를 호출하고, 다중 ResultSet 데이터를 JSON(`*_legacy_results.json`)으로 덤프 수집합니다. (모의 데이터가 있을 경우 자동 Seeding 및 Clean-up 실행)
    *   **5. 신규 마이그레이션 타겟 소스코드 실행 데이터 수집 (Target System)**: 마이그레이션된 C#(DLL 리플렉션 로드) 또는 Java(외부 JAR/클래스 프로세스 실행) 코드를 실제로 구동하여 실행 결과 JSON(`*_target_results.json`) 데이터를 수집합니다. (모의 데이터 자동 Seeding/Clean-up 및 트랜잭션 자동 롤백 적용)
    *   **6. 실행 결과 데이터 정합성 1:1 대조 및 보고서 생성 (Compare)**: 수집된 레거시 결과와 신규 타겟 결과(`*_target_results.json`)를 상세 1:1 비교 대조하여 데이터 정합성 분석 보고서(`*_CompareReport.md`)를 작성합니다.

*   **배치 검증 자동화 모드 실행 (CI/CD 무인 모드)**:
    ```bash
    # 소스코드 일치성 자동 검증 (L3 인간 개입 생략)
    dotnet run --project src/ReSet.Validator.Cli -- --spec "./output" --code "./src/Migration" --batch

    # 데이터 정합성 테스트 파라미터 설계 배치 모드
    dotnet run --project src/ReSet.Validator.Cli -- --spec "./output" --gen-inputs --batch

    # 검증용 모의 테이블 데이터(Mock Data) 자동 생성 배치 모드
    dotnet run --project src/ReSet.Validator.Cli -- --spec "./output" --gen-mock-data --batch

    # 레거시 DB 실행 결과 덤프 배치 모드
    dotnet run --project src/ReSet.Validator.Cli -- --exec-legacy --conn "Server=localhost;Database=Northwind;User ID=sa;Password=your_password;TrustServerCertificate=true" --batch

    # 신규 마이그레이션 타겟 실행 결과 덤프 배치 모드
    dotnet run --project src/ReSet.Validator.Cli -- --exec-target --conn "Server=localhost;Database=Northwind;User ID=sa;Password=your_password;TrustServerCertificate=true" --batch

    # 레거시 vs 타겟 1:1 데이터 정합성 대조 배치 모드
    dotnet run --project src/ReSet.Validator.Cli -- --compare-data --batch
    ```

---

## 🛠 트러블슈팅 및 자주 묻는 질문 (Troubleshooting)

> [!TIP]
> **Q. Mermaid 다이어그램 이미지 컴파일(Level 1 검증) 중에 오류가 납니다.**
> * **원인**: 시스템에 Node.js 전역 패키지인 `mermaid-cli (mmdc)`가 설치되어 있지 않거나 경로에 등록되지 않았기 때문입니다.
> * **해결**: `npm install -g @mermaid-js/mermaid-cli` 명령을 통해 설치를 완료하거나, `appsettings.json` 내 `"UseMermaidCli": false`로 설정을 변경하여 텍스트 정적 린팅만 수행하도록 설정을 완화할 수 있습니다.

> [!WARNING]
> **Q. AI 분석(리버스 엔지니어링) 중 HttpClient.Timeout 관련 취소(Cancellation) 오류가 발생합니다.**
> * **원인**: 분석하려는 Stored Procedure나 참조하는 DDL의 크기가 너무 커서 AI의 응답을 받기까지 기본 제한 시간(100초 등)을 초과했기 때문입니다.
> * **해결**: `appsettings.json` (또는 `appsettings.local.json`) 파일 내 `AiSettings` 하위에 `TimeoutSeconds` 값을 300초(5분) 혹은 600초(10분) 등으로 늘려서 재시도하십시오.

> [!WARNING]
> **Q. Stored Procedure 실행 결과 수집 단계에서 데이터베이스 연결 예외가 발생하며 수집이 실패합니다.**
> * **원인**: 일시적인 DB 네트워크 차단, 잘못된 연결 문자열 또는 계정 권한 부족 등이 원인입니다.
> * **해결**: 프로그램은 **Soft Fail**을 채택하여 DB 실행 오류가 나더라도 크래시되지 않고 결과 JSON에 `FAIL` 상태를 기록하여 리턴합니다. 연결 대상 데이터베이스가 샌드박스 또는 적절한 테스트 DB에 접근할 수 있도록 `--conn` 파라미터나 `appsettings.local.json` 내 정보를 점검해 주십시오.

---

## 🧪 단위 테스트 실행 (Running Tests)

단위 테스트를 실행하여 모든 코드가 무결하게 작동하는지 검증합니다.
```bash
dotnet test
```
