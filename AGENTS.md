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
| 정적 분석·SQL 객체 타입 판정 | `architecture.md §4.3` + `TypeClassificationPolicyTests` |
| 재귀 의존성 수집·Soft Fail | `architecture.md §4.1` + 범주 2 |
| AI 공급자 추가·CLI 제공자 | `architecture.md §4.5` + 범주 4 |
| 정합성 검증기(Validator) | `architecture.md §4.6` + 범주 5 |
| 취소 처리 | 범주 2 + `CancellationPolicyTests` |
| 프롬프트 문구·환각 차단 규칙 | `architecture.md §4.9` + 범주 7 |

---

## 🚨 에이전트 핵심 준수 규칙 (Development Rules)

모든 작업은 아래 기술된 안전성과 무결성 범주에 맞춰 엄격히 격리되어 진행되어야 합니다.

### 🛡️ 범주 1. 보안 및 크레덴셜 제약 (Security)
1.  **절대 비공개 API Key를 소스 코드나 [appsettings.json](./src/ReSet.Cli/appsettings.json)에 포함하여 커밋하지 마십시오.**
    *   로컬 개발용 API Key는 Git 추적 제외 대상인 `src/ReSet.Cli/appsettings.local.json`을 새로 생성하여 관리해야 합니다.
    *   이 파일은 API Key 전용이 아니라 `appsettings.json`의 **모든 키를 덮어쓸 수 있습니다**. 저장소 기본값은 보수적으로 두고(예: API provider, 재귀 분석 off) 개인 환경 설정은 이쪽에 두십시오.
    *   검증기(`ReSet.Validator.Cli`)는 이 파일에서 **`ApiKey`만** 가져갑니다. 파일을 통째로 병합하는 코드를 되살리지 마십시오 — 나중에 추가된 구성 소스가 이기므로 분석기 쪽 provider가 검증기를 덮어써, CLI provider를 쓰는 순간 검증기의 무인 배치가 `CliProviderBatchGuard`에 걸려 종료 코드 1로 죽습니다.

### ⚡ 범주 2. 예외 처리 및 안정성 (Stability & Soft Fail)
2.  **전방위적 소프트 페일(Soft Fail) 및 예외 격리 정책을 준수하십시오.**
    *   **DB 메타데이터 수집**: [DbMetadataService.cs](./src/ReSet.Core/Services/DbMetadataService.cs)의 스키마 권한 누락 또는 동적 SQL 의존성 탐색 과정의 쿼리 오류 시 프로세스를 중단(`throw`)하지 마십시오. 경고 목록(`Warnings`)에 기록하고 소프트 스킵 처리해야 합니다.
    *   **원천 데이터 파일 덤프**: [MetadataExporter.cs](./src/ReSet.Core/Services/MetadataExporter.cs)의 디스크 쓰기 오류 등이 발생하더라도 핵심 산출물은 안전하게 보존되도록 에러 핸들러로 감싸야 합니다.
    *   **정합성 검증 DB 실행**: [SpExecutionService.cs](./src/ReSet.Validator.Core/Services/SpExecutionService.cs)의 Legacy SQL 실행 수집 시 연결 실패나 쿼리 수행 오류가 나면 크래시하지 말고, 결과 DTO의 테스트 케이스를 `FAIL`로 처리하고 예외 메시지를 `ErrorCode` 필드에 기재하여 직렬화 내보내야 합니다.
    *   **캐싱 및 서브 시스템**: [CacheManager.cs](./src/ReSet.Core/Services/CacheManager.cs)의 글로벌 해시 캐시 조작 및 레거시 마이그레이션(MigrateLegacyCaches) 파일 복사 시 발생하는 모든 IO 예외는 try-catch로 격리하여 메인 파이프라인 중단을 예방하십시오.
    *   **재귀 코드 객체 분석**: [DependencyAnalysisOrchestrator.cs](./src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs)에서 하위 SP/UDF의 메타데이터·분석·`Spec.md` 저장 실패는 해당 노드만 `Failed` 상태와 사유로 남기고 다른 객체 분석을 계속해야 합니다. 깊이 제한 객체는 `SkippedDepth`로, 크로스 DB 분석이 꺼져 있어 진입하지 않은 다른 DB 객체는 `SkippedExternal`로 표기하십시오. 크로스 DB 분석이 켜진 상태에서 발생한 접근 실패는 `SkippedExternal`로 덮지 말고 `Failed`로 노출해야 합니다. 동일 객체의 여러 경로 중 최소 깊이를 우선하며, 성공하지 않은 객체에는 명세서 링크를 만들지 마십시오. 객체 키와 출력 경로는 구분자·파일명 문자를 충돌 없이 인코딩하고, 성공한 모든 하위 객체의 최종 Critic 점수와 `Thinking.md`를 보존해야 합니다. 객체명 표기는 호출부(`sys.sql_expression_dependencies`·AST)가 아니라 카탈로그(`sys.objects`)나 오프라인 스냅샷에 등록된 실제 이름을 따라야 하며, 같은 객체가 호출한 SP마다 다른 표기로 저장되어 케이스 민감 파일시스템에서 링크가 깨지지 않게 하십시오.
    *   **오프라인 스냅샷 파일 검증 (Fail-Fast)**: `appsettings.json`에 `OfflineSnapshotPath`가 설정되어 있으나 실제 파일이 존재하지 않는 경우, 사용자 DB 연결 프롬프트로 우회(Fallback)하지 말고 즉각 예외를 발생시켜 프로그램을 종료함으로써 사용자가 설정 오기입을 바로 인지할 수 있도록 하십시오.
    *   **취소는 소프트 페일 대상이 아님**: `OperationCanceledException`은 실패가 아니라 사용자의 지시입니다. 취소 토큰을 넘기는 `await`를 감싸는 광범위 `catch`(`Exception`, `SystemException`, 타입 미지정)에는 반드시 `when (ex is not OperationCanceledException)` 필터를 달아 취소가 경고 목록으로 흡수되거나 다른 예외 타입으로 세탁되지 않게 하십시오. 취소를 실제로 흡수하는 지점은 [Program.cs](./src/ReSet.Cli/Program.cs)의 최상위 핸들러 하나뿐이며, 거기서 사용자에게 취소 사실을 알리고 Serilog를 정리한 뒤 종료합니다. 취소 이후에는 의존성 그래프 순회나 후속 생성을 계속하지 말고 즉시 되돌아가되, 이미 완료된 객체의 산출물은 보존하고 미분석 참조는 문서에 표기하십시오. 이 규칙은 `CancellationPolicyTests`가 Roslyn 구문 트리로 자동 검사합니다.
    *   **SQL 객체 타입 판정은 반드시 분류기를 거칠 것**: `sys` 카탈로그의 타입 문자열을 `Contains("TABLE")` 같은 부분 문자열로 직접 판정하지 마십시오. `SQL_TABLE_VALUED_FUNCTION`이 `TABLE`을 포함하므로 TVF가 테이블로 오분류되고, 그 함수의 DDL이 수집되지 않은 채 이를 호출하는 SP의 명세서가 로직을 블랙박스로 남긴 채 작성됩니다. 실제로 정산일을 계산하는 `UIF_SettleYMD`가 그렇게 누락됐습니다. [SqlObjectTypeClassifier](./src/ReSet.Core/Services/SqlObjectTypeClassifier.cs)의 `IsTableOrView` / `IsCodeObject` / `ResolveCodeObjectType`을 쓰고, 사본을 만들지 마십시오. **`TypeClassificationPolicyTests`가 자동 검사하는 범위는 정확히 "타입 문자열에 대한 원시 부분 문자열/접두·접미 판정"(`Contains`/`IndexOf`/`StartsWith`/`EndsWith`, 일반 멤버 접근과 널 조건부 체인 모두에서 `Trim`/`ToUpper`류로 감싼 수신자 포함, `?.`가 여러 번 이어져도 소유 관계를 정확히 계산해 끝까지 풉니다)이며, 이것이 분류 권위의 전부라는 뜻은 아닙니다. 언래핑 목록 밖의 메서드(`ToString`, `Substring` 등), 지역 변수 재대입, 수신자를 괄호로 감싼 형태(평문 `(dep.Type).Contains("TABLE")`도, 널 조건부 `(dependencyType?.Trim())?.Contains("TABLE")`도), **널 조건부** 매칭 호출 뒤에 후속 접근이 붙는 형태(`dep.Type?.Contains("TABLE").ToString()` — 평문 `dep.Type.Contains("TABLE").ToString()`은 여전히 잡습니다)는 여전히 놓칩니다 — 세부는 `TypeClassificationPolicyScanner.cs` 상단 주석의 "알려진 한계" 참고.** `DependencyAnalysisOrchestrator.TryParseCodeObjectType`과 `MetadataExporter.NormalizeCodeObjectDdlFolder`는 스캐너가 못 보는 정확 일치 `switch` 테이블로 분류기 밖에 남아 있고, 분류기와 가장자리에서 어긋납니다(`"P"`/`"FN"`/`"TF"`를 분류기는 `Unresolved`로 보지만 두 테이블은 Procedure/Function으로 봅니다). 오늘 오작동하지는 않습니다 — 실제 `Type` 값은 전부 `type_desc`에서 오기 때문입니다 — 하지만 이 두 테이블을 새로 만들거나 베끼지 마십시오. 통합은 별도 후속 과제입니다.
3.  **AI API 응답 널 가드(TryGetProperty) 및 모델 파라미터 매핑을 준수하십시오.**
    *   [ClaudeClient.cs](./src/ReSet.Core/Services/Clients/ClaudeClient.cs), [OpenAiClient.cs](./src/ReSet.Core/Services/Clients/OpenAiClient.cs), [GoogleClient.cs](./src/ReSet.Core/Services/Clients/GoogleClient.cs), [OllamaClient.cs](./src/ReSet.Core/Services/Clients/OllamaClient.cs), [ZaiClient.cs](./src/ReSet.Core/Services/Clients/ZaiClient.cs) 호출 파싱 시 안전 필터 차단이나 응답 누락으로 인해 `KeyNotFoundException` 크래시가 발생하는 것을 원천 차단하십시오.
    *   반드시 `TryGetProperty`를 활용해 JSON 필드 유무를 안전하게 확인하고, 비정상 수신 시 `InvalidOperationException`을 던져 투명하게 거절 사유를 노출하십시오.
    *   **모델별 전송 규격 매핑**: OpenAI 추론 모델(o1/o3) 호출 시 `temperature`를 제외하고 `reasoning_effort`를 표준 매핑하십시오. 또한 gpt-5 Responses API 사용 시 명시적인 `prompt_cache_key`를 주입하여 프롬프트 캐시를 활성화합니다. gpt-5.6 이후 모델은 캐시 접두사를 **cache breakpoint** 단위로 비교하며, breakpoint는 기본값(implicit)이 **마지막 메시지 하나**에만 놓입니다 — 그러므로 요청마다 달라지는 지시를 별개 메시지로 분리하는 것만으로는 캐시가 살지 않고, 공통 메시지 경계를 `prompt_cache_breakpoint: { "mode": "explicit" }`로 직접 찍어야 합니다. 이 breakpoint는 메시지가 아니라 **content 블록**에 붙으므로, 찍는 요청은 `content`를 `input_text` 타입 블록 배열로 보내야 합니다. 한 요청 안에서 문자열 `content`와 블록 배열을 섞지 마십시오 — 문서가 보증하지 않는 형태입니다. 접미사가 비면 메시지를 만들지도, 형식을 바꾸지도 마십시오(빈 메시지 하나가 느는 것 자체가 접두사를 바꾸고, 이득 없는 곳에서 표현만 바꾸면 400 위험만 떠안습니다). Claude 통신 시 4/5세대 모델의 빈 생각 블록 누수를 방지하기 위해 옵션을 조율하고, `system` 프롬프트 블록 내에 `cache_control: { type: "ephemeral" }`을 부여하여 캐싱을 활성화합니다. 재생성 회차(같은 접두사를 이미 보낸 뒤 `volatileUserSuffix`가 있는 경우)에는 `PromptCacheBreakpointPolicy`가 user 블록에도 `cache_control`을 찍어 명세서 블록을 캐시합니다 — 이 두 번째 중단점은 첫 전송에는 찍히지 않습니다.
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
        - 하한 검사의 대조 기준(`ErrorCodes`)은 AI가 아니라 도구가 명세서에서 채웁니다. 빈 배열은 통과가 아니라 "검증 불가"입니다. 단, `LegacyProcedures`가 비어 있는 단계는 보존할 원본 코드가 없으므로 정상입니다.
        - 목차의 대상 테이블(`TargetTables`)은 정적 분석이 진실의 원천입니다. 명세서 산문에서 다시 뽑지 마십시오 — 대상 테이블은 오류코드와 달리 파서가 AST에서 이미 확정한 구조화된 데이터이므로, 산문을 재해석하면 정확도가 오히려 떨어집니다. `SpecTargetTableExtractor`가 뽑은 쓰기 집합으로 목차의 선언을 교체하고, 버려진 선언은 배너가 아니라 경고로 남기십시오.
        - `TargetTables`(검증 재료)와 `SchemaTables`(회차 지시서 DDL 스코프 재료)를 한 필드로 합치지 마십시오. 합치면 읽기 원본을 넣을 때 검증이 과해지고(존재하지 않는 요건이 생김), 빼면 에이전트가 SELECT를 쓸 스키마를 받지 못합니다.
        - 명세서의 스키마 주장은 L1이 기계적으로 대조한다. 대조 기준은 프롬프트에 실린 컬럼이며,
          DB 전체 컬럼이 아니다(`SchemaPromptColumnSelector`, `MechanicalValidator.CheckSchemaClaims`).
    *   **L2 (AI 교차 검토)**: [AiService.cs](./src/ReSet.Core/Services/AiService.cs)의 자가 보완 루프(`MaxL2Attempts` 한도 준수)를 제어하고, **최근 3개 라운드의 Critic 피드백을 항목별 점수와 함께 누적 주입**하여 회귀 결함(Regression)을 예방하십시오. Actor는 이전 명세서를 받지 않고 매번 백지에서 다시 쓰므로, 누적을 끊고 최신 피드백만 넣으면 앞 라운드에서 정리된 오류가 되살아납니다. 조립은 [CriticFeedbackLog](./src/ReSet.Core/Services/CriticFeedbackLog.cs)가 소유하며, L1 실패 회차에는 `ComposeAfterL1Failure`가 L1 수정 지시와 누적 피드백을 함께 보냅니다.
        - **로컬 모델 구역별 순차 분할 생성**: `AiClientFactory.IsLocalProvider(ProviderName)` (Ollama, mlx, local-openai 등) 사용 시 1회차 생성 및 자가 수정/피드백 재생성 루프는 "OverviewAndParameters", "CrudAnalysis", "LogicAndVisualization" 구역으로 나누어 순차적으로 구동하되, 피드백 내용의 키워드 분석을 통해 연관된 파트만 선택적으로 재생성 및 조립되도록 설계되어 있습니다. 하드코딩된 "Ollama" 비교 대신 항상 `IsLocalProvider()` 헬퍼를 사용하여 mlx-lm 등 로컬 호환 프레임워크도 이 파이프라인을 타도록 정합성을 준수하십시오. TUI 상의 진행도는 논리 구조 분석(Stage 1)과 3개의 분할 생성(Stage 2)을 합쳐 전체 4단계(1/4 ~ 4/4)로 통합 넘버링하여 사용자에게 직관적인 진행 상황을 제공하도록 구성되어 있습니다.
        - **Stage 1 (Deconstruct) 추론 로깅 보존**: 로컬 모델의 분할 생성(Stage 2)뿐만 아니라 초기 JSON 논리 추출 단계(Stage 1 Deconstruct)의 추론 내용도 `Thinking.md`에 함께 누적(`accumulatedThinking`)되도록 보장하여, 추론 모델의 논리 추출 과정을 투명하게 디버깅할 수 있도록 유지하십시오.
    *   **L2 Actor-Critic**: `ActorEffort: "dynamic"` 시 3종 차등 Effort 병렬 생성 ➔ Critic 채점 ➔ Fast-Pass 판정 ➔ Consolidator 앙상블 합성 ➔ **합성 완료 후 L2 최종 Critic 검증 및 1회 최종 보완 루프**를 순차 구동하십시오. 최종 합성본(또는 보완본)에 대한 최종 L2 Critic 리뷰 결과 점수는 명세서 파일 상단에 누락 없이 출력되어야 합니다.
    *   **품질 기준 엄격 강제 및 경고 표기**: 품질 향상을 위해 단일 모델 자가 수정 루프에서도 감쇄 임계치(Decaying Threshold)를 배제하고 설정된 기준 점수(Threshold)를 일관되게 적용하십시오. 만약 최종 시도 횟수를 소모한 후에도 점수 미달로 검증을 통과하지 못한 경우, 문서를 버리지 않고 채택하여 저장하되 문서 최상단에 `[!CAUTION]` 경고 배너와 상세한 Critic 점수 및 피드백 코멘트를 보존하여 후속 수정을 유도하도록 구현하십시오.
    *   **검증 종료 상태 정직성**: 파이프라인이 어디서 끝났는지는 [VerificationOutcome.cs](./src/ReSet.Core/Models/VerificationOutcome.cs)의 네 값으로만 표현하고, `bool` 플래그나 `ReviewResult`의 널 여부로 대체 판정하지 마십시오. 리뷰 호출이 실패해 점수가 없는 상태를 통과로 보고하는 것을 금지하며, Critic 점수는 종료 상태가 `Passed` 또는 `QualityRejected`일 때만 문서에 실어야 합니다. 상태의 한국어 표기는 `VerificationDocumentFormatter.StatusLabel`에서만 만들고, 명세서 헤더·L3 승인 화면·캐시 복원 보고·지시서 번들이 모두 같은 표기를 쓰도록 유지하십시오. 파이프라인에 진입한 적 없는 문서(단일 SP 계획서, 정산 정책서)는 점수를 받을 수 없는 별도 진입점(`FormatUnverifiedDocument`)으로 렌더링하고, 근거 명세서가 있다면 그 상태를 인용하십시오. 캐시 항목에도 종료 상태를 함께 보존해 복원된 산출물이 신규 분석과 다르게 보고되지 않게 하십시오.
    *   **Mermaid 시스템 변수 예외 허용**: 다이어그램 린팅 시 `@@ERROR` 시스템 변수가 포함되어 있더라도 린팅 컴파일 검사에서 예외적으로 정상 패스하도록 정합성 규칙을 보완하십시오.
    *   **L3 (인간 승인)**: [VerificationPipelineOrchestrator.cs](./src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs)에서 미리보기 및 DB 역동기화를 제어하되, 무인 배치 모드(`isBatchMode: true`) 환경에서는 L3 프롬프트 단계를 생략하고 자동으로 우회 승인하십시오.
    *   **진행도 시각화**: 진행률 시각화([IMultiProgressScope.cs](./src/ReSet.Core/Services/IMultiProgressScope.cs)) 통합 시 Core가 UI에 직접 의존하지 않는 비결합 설계를 유지하고, TUI 구현부(`ConsoleProgressScope`)에서는 렌더링 루프와의 충돌 방지를 위해 `ConcurrentDictionary`와 `TaskCompletionSource`를 적용하여 백그라운드 태스크 방식으로 격리 갱신하십시오.
    *   **신규 공급자 확장**: 새로운 LLM 공급자 연동 시, [IAiClient.cs](./src/ReSet.Core/Services/IAiClient.cs)를 상속받아 클라이언트를 구현하고 [AiClientFactory.cs](./src/ReSet.Core/Services/Clients/AiClientFactory.cs)에 등록하십시오.
    *   **CLI 기반 제공자(`claude-cli`/`codex-cli`/`agy-cli`)의 원칙**: 로컬에 로그인된 코딩 에이전트 CLI를 헤드리스로 기동해 종량제 API 키 없이 구독 비용만으로 운용하는 별도 제공자군입니다. `ApiKey`를 두지 말고 `Command` 설정만 사용하십시오. 모델은 CLI 전용 키를 새로 만들지 말고 API 제공자와 동일한 `ModelName`을 그대로 전달하되, 값이 비어 있으면 모델 인자를 생략해 CLI 기본값에 맡기십시오. temperature는 지원하지 않으므로 클라이언트 생성 시 무시된다는 경고를 로깅하십시오. 호출 실패 시 다른 제공자로 자동 대체(Fallback)하지 말고 [CliFailureClassifier.cs](./src/ReSet.Core/Services/Clients/Cli/CliFailureClassifier.cs)로 원인을 분류해 원본 CLI 출력과 함께 예외로 보고해, 전환 여부를 사람이 설정을 고쳐 판단하게 하십시오. **응답 봉투의 토큰 집계는 반드시 읽어 로그로 남기십시오** - 캐시 미스는 오류를 내지 않으므로, 읽지 않으면 캐시가 도는지 아닌지가 영원히 드러나지 않습니다(세 CLI 모두 캐싱을 하지만 ReSet은 중단점을 제어할 수 없어 관측만 가능합니다). 필드 이름은 제공자마다 다르므로 매핑은 각 클라이언트가 맡고, **봉투에 없는 항목을 0으로 채우지 마십시오** - 0은 "재보니 그만큼이었다"는 측정값이라, 없는 필드를 0으로 적으면 나중에 그것이 캐시 판정의 근거로 쓰입니다([CliUsage.cs](./src/ReSet.Core/Services/Clients/Cli/CliUsage.cs)의 미보고 표기를 쓰십시오). CLI 에이전트 자체는 헤드리스로 정상 동작하지만, 무인 배치 도중 구독 쿼터가 소진되거나 권한 프롬프트에서 멈추면 장시간 실행이 통째로 날아갈 수 있으므로, Actor/Critic/Consolidator 중 하나라도 CLI 제공자면 [CliProviderBatchGuard.cs](./src/ReSet.Core/Services/Clients/Cli/CliProviderBatchGuard.cs)가 ReSet.Cli와 ReSet.Validator.Cli 양쪽 모두에서 DB 연결 전에 실행을 즉시 중단시켜야 합니다.
    *   **코드가 강제하는 제약은 프롬프트에도 실으십시오**: 파서나 검증기가 상한·형식을 강제하는데 프롬프트가 그 사실을 알리지 않으면, 모델은 자신이 무엇을 어겼는지 알 방법이 없고 파이프라인은 오류 없이 폴백합니다 — 아무도 눈치채지 못하는 종류의 실패입니다. 실측 사례로 목차가 73단계를 내 상한 40(`BatchStepPlanParser.MaxSteps`)에 걸리자 단계 목록이 통째로 버려졌고, 단계별 섹션이 하나도 없는 문서가 `Passed`로 끝났습니다. 상한을 프롬프트에 실은 뒤 같은 잡이 32단계로 들어왔습니다. 같은 결함이 세 번 나왔습니다 — `ErrorCodes` 빈 배열, `MaxSteps`, 그리고 규칙 없이 JSON 예시에만 등장하던 `LegacyProcedures`(파이프라인의 커버리지 검사와 하한 검사가 전적으로 기대는 필드인데, 33단계가 전부 비운 채 나와 두 검사가 통째로 무력화됐습니다). 제약을 코드에 새로 넣을 때는 그것을 받는 프롬프트도 함께 고치고, **파이프라인이 의존하는 필드에는 예외 없이 규칙 문장을 주십시오** — 스키마 예시에 등장한다는 것은 모델에게 선택 항목이라는 뜻입니다.
    *   **리뷰(검증) 시 풍부한 컨텍스트 유지**: AI 리뷰어(Critic)가 기능 명세서의 정확성과 CRUD/인터페이스 완전성을 정상 검증할 수 있도록, 리뷰 요청([ReviewSpecificationAsync](./src/ReSet.Core/Services/AiService.cs)) 시에도 분석 요청 시와 동일하게 테이블 스키마, 참조 UDF DDL, AST 정적 분석 등의 원본 메타데이터 컨텍스트 정보(`BuildSpMetadataTexts` 헬퍼 이용) 및 대상 stored procedure의 실제 SQL DDL 소스코드(`spDef.DdlText`)를 누락 없이 빌드하여 리뷰 프롬프트(`userPrompt`)에 포함해 전달해야 합니다.
    *   **재귀 객체별 검증과 산출물 모드**: `AnalysisSettings:AnalyzeReferencedCodeObjects`가 활성화되면 SP/UDF마다 기존 L1/L2/L3 파이프라인과 캐시 경로를 그대로 적용하십시오. 직접 의존 메타데이터에는 테이블 스키마·설명·인덱스와 참조 코드 DDL을 유지하되, 외부 DB 테이블·뷰의 컬럼·인덱스 상세는 조회하지 마십시오. `DatabaseSettings:AllowExternalDatabaseConnections`가 켜진 경우에만 다른 DB의 코드 객체 유형·DDL을 3부 이름으로 해석해 분석 대상에 포함하고, 분석 루트 DB는 `DependencyAnalysisRequest`를 통해 파이프라인에 전파하여 산출물 경로와 캐시 키가 어긋나지 않게 하십시오. 링크드 서버(4부 식별자)는 지원 대상이 아닙니다. `OutputSettings:DependencyArtifactMode`의 `Reference` 모드에서는 표준 DDL을 객체당 한 번만 저장하고, `PortableBundle`에서만 참조 SP/UDF DDL 사본을 `raw/ddl/`에 추가하십시오.
    *   **하이브리드 영문 프롬프트 구조 준수**: `AiService.cs` 내부의 시스템 프롬프트(`systemPrompt`)는 반드시 영문(English) 작성을 원칙으로 하고, 최종 출력 및 체크리스트 동작 지시만 한국어 출력 조건 및 영어 매칭 트리거를 사용해야 합니다. 이를 임의로 한국어 프롬프트로 전면 번역하거나 되돌려 규칙 준수 강도를 떨어뜨리지 마십시오.
    *   **스키마 및 환각/숏컷(Shortcut) 차단 룰 유지**: 프롬프트 규칙 내의 "의존 메타데이터 외 컬럼 창작 금지" 및 "DDL 미정의 임의 에러 반환 상숫값 가작 금지" 규정은 로컬 LLM의 안전장치입니다. 또한 통합 배치 전환 계획 수립 시, UNION/JOIN이나 에러 코드 분기 처리(Chunking Key) 로직을 모델이 자의적으로 축약(Shortcut)하지 못하도록 하는 "Anti-Shortcut" 프롬프트 제약 규칙을 절대 간소화하거나 누락하지 마십시오.
    *   **`GenerateBySplitAsync`의 캐시 워밍 순서를 보존하십시오**: 단계 본문 생성은 `StepConcurrency`만큼 동시에 실행되지만, 첫 단계는 설정값과 무관하게 항상 단독으로 먼저 실행해 프롬프트 접두사 캐시를 채운 뒤에야 나머지가 동시에 시작됩니다. 이 서술은 gpt-5 경로 기준입니다 — Claude는 [PromptCacheBreakpointPolicy](./src/ReSet.Core/Services/PromptCacheBreakpointPolicy.cs)가 두 번째 전송부터 공용 블록에 중단점을 찍으므로, 첫 단계가 채우는 것은 시스템 블록뿐입니다. 이 첫 단계 단독 실행을 "불필요한 직렬화"로 보고 제거하지 마십시오 — 지웠을 때의 증상은 산출물은 그대로인데 입력 토큰 비용만 조용히 오르는 것이라 코드만 봐서는 원인을 알기 어렵습니다. `RunConsolidatedPipeline_WarmsCacheBeforeFanningOut` 테스트가 이 순서를 지킵니다.
    *   **`GenerateStepSectionWithFloorRetryAsync`의 예외 재시도 지연을 보존하십시오**: 단계 생성이 예외로 실패한 경우에만 재시도 전에 무작위 지연을 둡니다. 하한 미달 재시도는 지연하지 않습니다 — 동시 실행 중 rate limit 폭풍을 흩트러뜨리기 위함이지, 모델 품질 문제까지 차연하는 것이 아닙니다. 이를 어기면 429 하나가 여러 단계를 같은 창에서 때릴 때 모두 함께 생성 실패로 강등됩니다. `RunConsolidatedPipeline_WhenStepGenerationThrows_DelaysRetryWithJitter`와 `RunConsolidatedPipeline_WhenStepMissesFloor_RetriesWithoutDelay` 테스트가 이를 보증합니다.

### 🔒 범주 5. 타겟 런타임 격리 및 리소스 정리 (Lifecycle & Sandbox)
7.  **타겟 러너 격리 및 모의 데이터(Mock Data) 적재 수명주기를 준수하십시오.**
    *   **트랜잭션/타임아웃 격리**: C# 리플렉션 러너([CSharpReflectionRunner.cs](./src/ReSet.Validator.Core/Services/CSharpReflectionRunner.cs)) 호출 시 생성되는 `DbTransaction`은 항상 **`Rollback()`** 처리하여 Sandbox 상태 변경을 격리하고, 비동기 호출 시 `ValueTask` 및 `ValueTask<T>` 반환 형식도 리플렉션을 통해 동적으로 대기(await)하여 롤백 및 종료 전 작업이 완료되도록 보장하고, Java 프로세스 구동 시에는 30초의 타임아웃 제한을 명확히 설정하십시오.
    *   **모의 데이터 수명주기**: 물리적 FK가 없는 환경을 극복하기 위해 관계 시드가 매핑된 모의 데이터 캐시를 활용하고, [SandboxSeedingService.cs](./src/ReSet.Validator.Core/Services/SandboxSeedingService.cs)를 통해 데이터 적재(Seed) 및 테스트 완료 후 자동 소거(Clean-up/Truncate) 처리를 확실히 수행하십시오.

### 🔌 범주 6. 외부 코딩 에이전트 및 프로세스 제어 (External Agent & Codegen)
8.  **지시서 번들 생성 및 코딩 에이전트 CLI 프로세스 제어를 적용하십시오.**
    *   **번들 분할 제공**: 지시서를 마크다운 하나로 묶지 마십시오. 진입점(`agent/MigrationInstructions.md`)·공통 문서(`agent/common/`)·단계 본문(`agent/steps/`)·회차별 작업 지시서(`agent/task-NN-<코드>.md`)로 나눠 쓰고, 한 회차의 지시서는 그 회차가 읽어야 할 것만 가리키게 하십시오. 대상 출력 폴더가 없을 시 선행 자동 생성을 처리하십시오. 개별 SP 분석 시에는 에이전트 지시서 번들을 생성하지 않으며, 통합 배치 시에만 문서 리소스(`docs/`)와 에이전트가 생성한 소스코드(`src/`) 모두를 `output/Jobs/{JobName}/` 하위 디렉토리에 엄격하게 분류 격리하여 프로젝트 파일 무결성을 보장하십시오.
    *   **분할 실패 시에도 지침은 앞으로**: 계획서를 조각내지 못해 단일 파일로 폴백하더라도 **지침을 문서 앞으로 옮기는 순서 교정은 반드시 적용**하십시오. 지켜야 할 규칙이 문서 뒤쪽에 있으면 파일 읽기 절단선 너머로 밀려 에이전트에게 보이지 않습니다. 진입점 조립 순서를 분할 성공 여부로 분기시키지 마십시오. 그리고 **부분 분할을 만들지 마십시오** — 단계 하나라도 경계를 못 찾으면 전체를 단일 파일로 되돌리십시오. 비어 있는 단계 문서가 조용히 생기는 것이 가장 나쁩니다.
    *   **회차 진행 상태는 도구가 소유**: `agent/progress.json`은 도구가 쓰고 `agent/todo.md`는 거기서 렌더링하십시오. 에이전트에게 자기 체크리스트를 채점하게 하면 신뢰성이 의심되는 주체가 유일한 완료 기록을 쓰게 됩니다. 상태 파일 쓰기는 임시 파일 교체로 원자적으로 하고, 읽지 못한 파일은 덮어쓰지 말고 보존하십시오.
    *   **AI가 만든 이름을 파일명으로 쓰지 마십시오**: 단계 코드는 AI 생성 목차에서 오므로 경로 구분자나 `..`가 들어올 수 있습니다. 파일명으로 쓰기 전에 반드시 정화하고, 정화 결과가 충돌하면 부분 분할을 만들지 말고 분할 전체를 포기하십시오.
    *   **데이터 액세스 경계 규칙 포함**: 지시서에는 반드시 [DataAccessPolicy.cs](./src/ReSet.Core/Services/DataAccessPolicy.cs)의 SQL/ORM 경계 규칙이 포함되어야 합니다. 이 규칙 문구를 지시서 조립 코드에서 직접 다시 쓰지 말고 항상 `DataAccessPolicy`를 참조하십시오.
    *   **동적 코드 생성 시점 제약**: 개별 SP 분석 완료 직후에는 에이전트 자동 기동을 금지하며, 가급적 복수 SP가 엮인 통합 배치 전환 계획서 수립 완료 시점에만 외부 에이전트를 기동하십시오. 단, 사용자가 메인 메뉴에서 스탠드얼론 메뉴(기작성된 지시서 기반 구동)를 선택한 경우에는 기존 출력 디렉터리의 `agent/MigrationInstructions.md`를 스캔하여 에이전트를 독립적으로 재기동(Resume)할 수 있도록 허용합니다. 이때 고른 지시서가 레거시 단일 문서인지 회차 번들인지 **먼저 판정**하십시오. 번들이면 디스크에서 회차 목록을 복원해 회차 경로로 보내고, 복원이 성립하지 않으면 전체 Job 경로로 떨어뜨리지 말고 사유를 설명하며 거부하십시오 — 회차용 문서를 전체 Job 경로에 먹이는 조합만은 만들지 마십시오.
    *   **프로세스 양방향 제어**: [ExternalCliCodingEngine.cs](./src/ReSet.Core/Services/ExternalCliCodingEngine.cs) 기동 시 대화형 흐름을 공유할 수 있도록 부모 콘솔 입출력 스트림을 직접 상속 공유하고, 취소(`CancellationToken`) 수신 시 좀비 프로세스를 예방하기 위해 하위 프로세스 트리를 강제 종료(`process.Kill(true)`)하십시오. 띄어쓰기가 포함된 프롬프트 파싱을 막기 위해 Arguments 전체를 쌍따옴표(`\"...\"`)로 래핑하여 공급하십시오.
    *   **대화형/배치 인자 분리**: 코딩 엔진 인자는 `Arguments`(대화형)와 `BatchArguments`(무인)로 나뉩니다. 대화형 TUI 형식은 무인 실행에서 TTY를 열지 못해 종료 코드 0인 채 조용히 실패하므로 **폴백하지 마십시오**. `BatchArguments`가 비면 그 엔진은 무인 배치 미지원이며 `CodingEngineFactory`가 명시적으로 거부합니다. 지시서가 작업 디렉터리(`<job>/src`) 바깥에 있으므로 `{jobDir}` 자리표시자로 접근 범위를 열어 주어야 하며, **원본 명세서는 Job 루트의 하위가 아니라 형제**(`<출력루트>/Procedures/...` vs `<출력루트>/Jobs/<job>`)이므로 `{specRoot}`로 따로 열어 주어야 합니다. 출력 루트 전체를 열지 마십시오 — 다른 Job의 번들과 진행 상태까지 쓰기 범위에 들어옵니다.
    *   **무인 자동 기동**: CLI 배치 모드 실행 시 `--job-name` 인자가 공급되면 L3 대화형 단계를 건너뛰고 자동으로 통합 계획 및 지시서 번들을 생성해 외부 에이전트 프로세스 기동까지 연속 수행하는 CI/CD 무인 파이프라인을 지원하십시오.
    *   **회차 단위 순차 실행**: 코드 생성은 0회차(골격·DI·설정) ➔ 단계 1..N ➔ 조립의 **순차** 회차로 돌리고, 각 회차에 그 회차의 `task-*.md` 하나만 넘기십시오. 병렬로 돌리지 마십시오. 한 회차가 실패하면 사유를 기록하고 다음 회차로 넘어가되, 쿼터 소진·미인증·툴 권한 거부처럼 회차와 무관한 실패는 남은 회차를 중단하십시오 — 다음 회차도 같은 벽에 부딪힙니다.
    *   **회차 게이트는 fail-closed**: 검증 대상을 찾지 못한 회차를 통과로 기록하지 마십시오. "검증할 것이 없어서 통과"와 "실제로 통과"가 구별되지 않으면 코드가 생성되지 않은 회차가 게이트를 지납니다. 0회차는 대조할 설계서가 없어 검증을 걸지 않고 산출물 생성 여부만 보며, 조립 회차는 모든 단계가 통과했을 때만 Job 전체 L2를 걸고 아니면 사유를 남기고 건너뜁니다. 재시도 분기를 새로 만들 때는 **진전 없는 재시도에 반드시 상한을 두십시오** — 피드백이 바뀌지 않으면 다음 기동은 같은 결과를 내며, 무인 배치에서 유료 프로세스를 무한히 기동하게 됩니다. 그리고 **상한에 걸려 접기 전에 사유를 먼저 지시서에 붙이십시오** — 마지막 시도에서 끊더라도 무엇을 못 찾았는지가 지시서에 남아야 사람이 열어 볼 수 있습니다. 대조 쌍을 하나도 못 찾은 재시도를 빈손으로 돌리지 마십시오. 붙일 L1/L2 결과가 없더라도 검증기가 짝을 찾는 파일·폴더 이름 규약을 설명하는 피드백은 있으며, 그것 없이 재시도하면 에이전트는 같은 자리에서 다시 끝납니다.
    *   **자가 수정 및 TDD 테스트 피드백 루프**: 외부 에이전트 기동 시 테스트 뼈대 및 구조 구축을 에이전트에게 자율 위임하고, 회차 지시서에 명시된 자율 루프(코드 작성 ➔ 테스트 ➔ 자가 수정 ➔ 자율 리뷰 ➔ 점진적 커밋)를 통해 에이전트 스스로 L0(로컬 테스트)를 통과하도록 유도합니다. 이후 L1 정적 검사(컴파일 오류 시 숏컷) 및 L2 의미론적 대조를 순차 수행하며, 검증 불일치 시 **그 회차의 작업 지시서**에 피드백을 축적해 재수정 기동시킵니다.

### 🧹 범주 7. 메타데이터 정화 및 주석 보완 (Cleansing & Annotation)
9.  **메타데이터 정화 및 정책 문서 수립 가이드를 준수하십시오.**
    *   **클렌징 스크립트 및 동기화**: AI 분석 완료 시 보완 스크립트 파일(`*_MetadataCleansing.sql`) 생성 기능은 현재 로컬 LLM의 스키마 환각(Hallucination) 방지를 위해 기본 제거되어 있습니다. 향후 수동 태그 삽입 등으로 해당 쿼리 파일이 물리적으로 존재할 때만 조건부로 TUI 최종 승인 및 동의를 묻고 DB 동기화를 실행하십시오. 크로스 DB 분석으로 수집된 다른 DB 소유 객체의 정화 스크립트는 파일명에 DB를 접두해 구분하고, 연결된 DB가 아닌 대상에는 절대 실행하지 마십시오.
    *   **C# 보간 중괄호 이스케이프**: 프롬프트 텍스트 내부의 중괄호(`{}`)는 C# 보간 기호($) 해석 오류를 막기 위해 반드시 이중 중괄호(`{{}}`)로 이스케이프해야 합니다.
    *   **정산 정책서**: SP DDL의 상수 분기 조건 분석과 테이블 데이터 프로파일링 정보를 결합해 정산 정책서(Settlement Rulebook)를 도출하고, 지정된 5대 헤더 구조를 엄격히 준수하도록 설계하십시오.
    *   **컬럼 매핑 표 축약 금지**: CRUD 분석 및 데이터 컬럼 매핑 표 작성 시, '외 다수' 또는 '등'과 같이 컬럼 목록이나 매핑 관계를 임의로 축약하거나 생략하지 말고, 실제 대상 물리 컬럼과 이에 매핑되는 원천값을 누락 없이 1:1 대조 표에 완전하게 기술하십시오.
    *   **UPDATE 매핑표는 정적 파서가 확정합니다**: UPDATE의 SET 절 타겟 컬럼과 원천 표현식은 `SqlStaticParser`가 `AstUpdateMappings`로 추출해 프롬프트 표에 이미 채워 넣습니다. `AiService`의 fill-in-the-blank 표에서 컬럼이나 원천 표현식을 AI가 채우도록 되돌리지 마십시오. 되돌렸을 때의 증상은 "명세서가 산문으로 뭉개지는데 검증은 통과함"이라 코드만 봐서는 원인을 알 수 없습니다. `MechanicalValidator`가 같은 컬럼 목록을 대조하므로, 프롬프트 쪽만 지우면 L1이 영원히 실패합니다.
    *   **DDL 기반 제약 조건 작성**: 프로시저 파라미터나 컬럼 제약 조건에 대해 임의로 'NOT NULL'과 같은 주관적 단정을 짓지 말고, 오직 DDL 소스코드에 명시되어 있는 타입 제약 및 기본값 정의를 기반으로만 사실적으로 기술하십시오.
    *   **의존 스키마 덤프 필터링**: 테이블 상세 스키마 정보를 마크다운 테이블로 덤프할 때, AST 정적 분석이 감지한 실제 참조 컬럼(`ReferencedColumnsPerTable`), PK/FK 컬럼, 인덱스 구성 컬럼만 선별적으로 필터링 출력(KeepCols 필터링)하여 AI 프롬프트 토큰을 절약하도록 구현되어 있습니다. 이 최적화 로직의 정합성을 유지해 주십시오. **테이블 식별자 비교는 반드시 canonical 3-part(`{Database}.{Schema}.{Name}`) 정확 일치로만 수행하십시오.** 부분 문자열 매칭은 `TSettleMst`를 `TSettleMstBackup`에 걸리게 하고, 첫 매치에서 중단하면 INSERT 대상 전용 컬럼이 담긴 키를 놓쳐 실존 컬럼이 프롬프트에서 사라집니다. 실제로 그 결함이 14개 명세서에 "스키마 불일치" 허위 경고를 만들어 냈습니다. 단, 3-part 한정에 필요한 DB 컨텍스트(`spDef.ObjectKey?.Database`)가 없으면 이 정확 비교 자체가 불가능해지므로, 그때만 베이스 이름(마지막 세그먼트) 비교로 폴백해 과다 포함 쪽으로 기웁니다. 이 필터는 토큰 절약용 최적화일 뿐 정확성 장치가 아니므로, 과다 포함(불필요한 행 몇 개)이 과소 포함(허위 "컬럼 없음")보다 낫다는 판단입니다.
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

- [ ] 컴파일 에러가 0개이고, 경고가 **정확히 8건**(모두 `tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`의 기존 CS8600/CS8602)인지 확인했는가? 증분 빌드는 경고를 다시 보고하지 않아 0건으로 보이므로 반드시 `dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l`로 세야 한다. 8건보다 많으면 이번 변경이 새 경고를 넣은 것이다.
- [ ] `dotnet test` 명령어를 실행하여 **실패 0, 건너뜀 0**으로 모든 단위 테스트가 통과(Passed)하였는가? (기대 개수를 여기 적지 않는다 — 테스트를 하나 추가할 때마다 이 줄이 거짓이 되고, 낡은 숫자는 올바른 빌드에서 항목을 실패시켜 다음 사람이 이 체크리스트를 무시하도록 길들인다. 실제로 하루 만에 네 번 낡았다.)
- [ ] 취소 가능한 `await`를 감싸는 `catch`에 `when (ex is not OperationCanceledException)` 필터를 달았는가? (`CancellationPolicyTests`가 자동 검사하며, 기준선 파일 `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`의 숫자는 고칠 때마다 함께 내려야 한다)
- [ ] SQL 객체 타입을 `Contains("TABLE"/"VIEW"/"FUNCTION"/"PROCEDURE")`로 직접 판정한 곳이 없는가? (`SqlObjectTypeClassifier`에 위임해야 하며 `TypeClassificationPolicyTests`가 자동 검사한다)
- [ ] API Key 등 비공개 자격증명이 소스코드나 `appsettings.json`에 하드코딩되지 않고 `appsettings.local.json` 또는 로컬 환경 변수로 격리되었는가?
- [ ] DB 메타데이터, AI 결과 원문 등을 Spectre.Console TUI에 출력할 때 모든 출력 부에 `Markup.Escape()` 조치를 적용했는가?
- [ ] Stored Procedure 실행 및 외부 샌드박스 데이터 수집 시, DB 연결 실패 시 예외 격리(Soft Fail 및 DTO FAIL 상태 주입) 처리가 정상 적용되었는가?
- [ ] 신규 추가된 C# 타겟 러너 내 `DbTransaction`이 작업 결과와 관계없이 항상 `Rollback()` 되도록 누락 없이 명세했는가?
- [ ] 작업 완료 후 수정 및 추가된 모든 코드가 솔루션 컴파일 및 아키텍처 규칙을 위반하지 않는지 재검토했는가?

<!-- synced-through: c8d6074 -->
