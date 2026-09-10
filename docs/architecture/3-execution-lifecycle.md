<!-- 아키텍처 정의서 §3 — 허브: [docs/architecture.md](../architecture.md) -->

## 3. 전체 실행 라이프사이클 및 데이터 흐름 (Visual Execution Flow)

### 3.1. 프로그램 거시 실행 흐름
ReSet 프로그램이 기동되어 설정을 파싱하고 DB에 연결한 뒤, 사용자가 고른 실행 경로에 따라 분기하는 거시적인(Macro) 흐름은 다음과 같습니다. 다섯 갈래 중 1번(개별 SP 역공학 분석)과 2번(통합 배치 마이그레이션 설계)의 상세 파이프라인을 아래에 접어 두었고, 3번(코딩 에이전트 구동)의 상세 시퀀스는 3.3절에, 4번(통합 정산 정책 문서 도출)의 메커니즘은 4.10절에, 5번(명세서 기반 요구사항 도출)의 메커니즘은 4.14절에 있습니다.

```mermaid
graph TD
    %% 1단계: 초기화 및 연결
    subgraph Setup ["1. 초기 설정 및 DB 연결 (Setup)"]
        Start["시작 (CLI 실행)"] --> Parse["설정 로드 및 CLI 인자 파싱 (CliArgs)"]

        Parse --> OfflineCheck{"오프라인<br/>모드인가?"}

        %% 오프라인 모드
        OfflineCheck -- "예 (Snapshot)" --> Snapshot["SnapshotManager 파일 로드<br/>(DB 접속 생략)"]

        %% 온라인 모드
        OfflineCheck -- "아니오" --> ModeCheck{"배치 모드 여부?"}

        ModeCheck -- "아니오 (TUI)" --> TUI["대화형 로그인 입력<br/>(세션 복구 및 실시간 연결 정보 수정)"]
        ModeCheck -- "예 (Batch)" --> BatchConn["연결 문자열 추출 (인자/환경변수)"]

        TUI & BatchConn --> ConnTest["데이터베이스 연결성 검증"]
    end

    %% 2단계: 실행 경로 분기
    Snapshot & ConnTest --> Entry{"실행 경로 선택<br/>(TUI 메인 메뉴 / 배치 인자)"}

    Entry -- "1. 개별 SP 역공학 분석" --> PathAnalysis["SP 목록 로드 및 대상 선택 ➔ 메타데이터 수집 ➔<br/>3단계 검증 ➔ 객체별 Spec.md 및 계획서 저장"]
    Entry -- "2. 통합 배치 마이그레이션 설계" --> PathBatch["저장된 Spec.md 조합 ➔ 통합 계획 파이프라인 ➔<br/>Jobs 하위 산출물 및 지시서 번들 생성"]
    Entry -- "3. 코딩 에이전트 구동" --> PathCodegen["기작성 지시서 선택 ➔ 외부 에이전트 자가 수정 루프<br/>(상세 흐름은 3.3절 참고)"]
    Entry -- "4. 통합 정산 정책 문서 도출" --> PathPolicy["Spec.md 코퍼스 + 사람 소유 명부 ➔<br/>코드값 사전(DB 선택) ➔ 정산 정책서 저장"]
    Entry -- "5. 명세서 기반 요구사항 도출" --> PathPrd["기존 Spec.md 선택(DB 미접속) ➔<br/>귀속 검사(1회 재시도) ➔ Prd.md 저장"]

    %% 통합 설계가 만든 지시서를 코딩 에이전트 경로가 소비한다
    PathBatch --> PathCodegen
    PathAnalysis & PathCodegen & PathPolicy & PathPrd --> End["종료<br/>(TUI는 메인 메뉴로 복귀)"]
```

<details>
<summary><b>1번 경로 상세 — 개별 SP 역공학 분석 파이프라인 (클릭하여 펼치기)</b></summary>

```mermaid
graph TD
    %% 2단계: 대상 필터링
    subgraph Selection ["2. 분석 대상 필터링 (Selection)"]
        Enter["개별 SP 역공학 분석 경로 진입"] --> LoadSps["SP 목록 로드<br/>(온라인 DB 조회 또는 오프라인 DbSnapshot)"]
        LoadSps --> TargetCheck{"배치 모드 여부?"}

        TargetCheck -- "아니오" --> SelectTUI["TUI에서 분석할 SP 선택"]
        TargetCheck -- "예" --> SelectBatch["--all 또는 --sp 기준으로 분석 대상 목록 필터링"]
    end

    %% 3단계: 메인 분석 및 검증 파이프라인
    subgraph Pipeline ["3. 분석 및 검증 파이프라인 (Pipeline)"]
        SelectTUI & SelectBatch --> LoopStart["분석 루프 시작 (SP 개별 단위 예외 격리)"]

        LoopStart --> Discover["DependencyAnalysisOrchestrator (유일 진입점)<br/>참조 재귀 사용 시 SP/UDF 그래프 발견·중복 제거·깊이/외부 경계 적용,<br/>미사용 시 루트 한 노드 그래프"]
        Discover --> QueryMeta["대상 객체 메타데이터 수집 및 정적 파싱<br/>- DbMetadataService 스키마 & 한글 주석 수집<br/>- 수집 범위: 재귀 사용 시 직접 의존성, 미사용 시 전이적<br/>- SqlStaticParser CRUD 분류, 중첩 제어 구조 요약,<br/>동적 SQL, UDF/Linked Server 감지"]
        QueryMeta --> GeneratePrompt["AI 프롬프트 컨텍스트 조립 (System 규칙 + 사용자 지침)"]

        GeneratePrompt --> VerificationPipeline["3단계 검증 파이프라인 실행<br/>(L1 기계검증 / L2 AI리뷰 / L3 개발자검토)"]
    end

    %% 4단계: 산출물 내보내기 및 현대화 전환 설계
    subgraph Save ["4. 결과 저장 및 현대화 설계 (Export)"]
        VerificationPipeline -- "승인 및 완료" --> ExportRaw["객체별 원천 데이터·표준 DDL·의존성 매니페스트 저장"]
        ExportRaw --> SaveSpec["SpecificationLinker로 성공 참조만 연결한<br/>SP/UDF별 Spec.md 저장"]
        SaveSpec --> GenMigrationCheck{"현대화 전환 계획 생성 활성화?<br/>(MigrationSettings:Enabled)"}
        GenMigrationCheck -- "예" --> GenMigration["SP별 배치 전환 계획서 저장<br/>(Procedures 하위 docs 폴더, 검증 없음으로 표기)"]
        GenMigrationCheck -- "아니오" --> CheckNext
        GenMigration --> CheckNext
    end

    CheckNext{"다음 분석 대상이<br/>남았는가?"} -- "예" --> LoopStart
    CheckNext -- "아니오" --> PathEnd["경로 종료 (메인 메뉴 복귀)"]
```

</details>

<details>
<summary><b>2번 경로 상세 — 통합 배치 마이그레이션 설계 파이프라인 (클릭하여 펼치기)</b></summary>

```mermaid
graph TD
    %% 1단계: 계획 수립의 재료는 이미 저장된 명세서다. 갈래는 셋이고, 앞의 SP 분석을
    %% 함께 도는가만 다르다 - TUI와 --plan-only는 저장된 것만 읽고, --all/--sp는 방금
    %% 분석한 것을 이어받는다. 사람/인자가 고른 것은 어느 쪽이든 진입점일 뿐이며, 셋 다
    %% 그 진입점이 부르는 프로시저 타입 참조의 명세를 CloseOverProcedureReferences로
    %% 재료에 더한다(함수 참조는 안 더한다).
    subgraph Collect ["1. 대상 명세서 수집 (Collection)"]
        EnterB["통합 배치 마이그레이션 설계 경로 진입"] --> ModeB{"진입 경로?"}
        ModeB -- "TUI (2번 메뉴)" --> PickSpecs["순차 단일 선택 루프로 저장된 Spec.md를 큐에 적재<br/>(물리 선택 순서 보장, 5.2절)"]
        ModeB -- "무인: SP 분석에 이어서<br/>(--all/--sp + --job-name)" --> AutoSpecs["방금 분석한 SP들의 명세서를<br/>분석 순서대로 재료에 적재"]
        ModeB -- "무인: 저장된 명세서만으로<br/>(--plan-only)" --> PlanOnly["PlanOnlyMaterialLoader가 --sp 나열 순서로<br/>진입점 명세서를 해석<br/>(못 찾으면 종료 코드 1)"]
        PickSpecs --> CloseTui["CloseOverProcedureReferences로 참조 프로시저 명세를<br/>재료에 추가(closure.SpecPaths 순서 그대로 사용)"]
        AutoSpecs --> CloseBatch["CloseOverProcedureReferences로 재료 추가 후<br/>ReorderByClosure로 폐포 순서에 맞게 재정렬"]
        PlanOnly --> CloseTui
        CloseTui & CloseBatch --> JobName["Job 이름 확정 및 출력 루트 결정<br/>(output/Jobs 하위)"]
    end

    %% 2단계: 명세서 경로의 dynamic/단일 분기와 달리 항상 3단계 순차 생성이다
    subgraph Agentic ["2. 3단계 Agentic Workflow (생성)"]
        JobName --> StructCheck{"목차(PlanStructure)가<br/>이미 있는가?"}
        StructCheck -- "아니오 (1회차)" --> P1["1/3. 브레인스토밍<br/>(raw/Brainstorming.md 보존)"]
        P1 --> P2["2/3. 목차 설계<br/>(raw/PlanStructure.md 보존)"]
        P2 --> P3
        StructCheck -- "예 (재시도)" --> P3["3/3. 최종 생성 진입<br/>(목차 재사용, 누적 피드백 최근 3회차 주입)"]
        P3 --> StepParse{"단계 목록(Steps[]) 파싱 및<br/>골격 생성 성공?"}
        StepParse -- "예" --> StepGen["단계별 본문 생성<br/>(1단계 단독 워밍 → 나머지 StepConcurrency 동시 생성)"]
        StepGen --> StepFloor{"단계 하한 검사<br/>(SQL/의사코드·대상테이블·오류코드·조건컬럼·축약어·반올림)"}
        StepFloor -- "미달 (재시도 여력 있음)" --> StepRetry["해당 단계만 1회 재시도"]
        StepRetry --> StepGen
        StepFloor -- "미달 (재시도 소진)" --> StepAdopt["미달 상태로 채택<br/>(하한 미달 배너 기록)"]
        StepFloor -- "통과" --> Asm["결정적 조립<br/>(BatchPlanAssembler)"]
        StepAdopt --> Asm
        StepParse -- "아니오 (파싱/골격 실패)" --> Single["단일 호출로 전체 본문 생성"]
    end

    subgraph VerifyB ["3. 검증 및 종료 상태 판정"]
        Asm & Single --> L1B{"L1 기계 검증 통과?"}
        L1B -- "실패 — 위반이 단계·골격으로 귀속됨" --> L1Fix["3/3 재실행 — 귀속된 자리만 다시 생성<br/>(골격은 직전 골격 위의 패치 + 앵커 가드,<br/>단계 섹션은 동결)<br/>(L1 자기 예산 MaxL1RepairAttempts,<br/>채점 회차를 먹지 않음)"]
        L1Fix --> P3
        L1B -- "실패 — 귀속 불가 (재시도 여력 있음)" --> Retry["피드백 세팅 후 골격·전 단계 재생성<br/>(채점 예산 소모)"]
        Retry --> P3
        L1B -- "실패 (재시도 소진)" --> OutL1["종료 상태: L1 미통과<br/>경고 배너 삽입"]
        L1B -- "성공" --> L2B{"L2 Critic 교차 리뷰<br/>(ReviewConsolidatedPlanAsync)"}
        L2B -- "리뷰 호출 실패" --> OutNR["종료 상태: 리뷰 미수행"]
        L2B -- "결함 (재시도 여력 있음)" --> Stall{"최고점을 갱신했는가?<br/>(BestAttempt.TryRecord)"}
        Stall -- "예 (개선 중)" --> Retry
        Stall -- "아니오 (2회 연속 정체) + 구조 결함 지목<br/>+ 재수립 미소진" --> Redraft["2/3 재실행 — 이전 목차와<br/>누적 피드백을 넣어 구조 재설계<br/>(Job당 1회, 직전 목차는<br/>PlanStructure.superseded-n.md로 보존)"]
        Redraft --> P3
        L2B -- "결함 (재시도 소진)" --> OutQR["종료 상태: 품질 미달<br/>점수·피드백 배너 삽입<br/>(구제 채택 시 그 시도를 만든<br/>목차를 현행으로 복원)"]
        L2B -- "통과" --> OutPass["종료 상태: 통과"]
    end

    subgraph ExportB ["4. 산출물 저장 및 지시서 번들 (Export)"]
        OutL1 & OutNR & OutQR & OutPass --> L3B{"무인 모드인가?"}
        L3B -- "예 (--all/--sp 또는 --plan-only)" --> SavePlan["BatchMigrationPlan.md 저장<br/>(종료 상태와 점수를 헤더에 기록)"]
        L3B -- "아니오 (TUI)" --> Human{"L3 사용자 결정?"}
        Human -- "1. 승인" --> SavePlan
        Human -- "2. 피드백" --> Regen["구조 변경이면 목차부터 재수립,<br/>아니면 사용자가 지목한 단계만 분할 재생성<br/>(지목이 없거나 골격을 고르면 전 단계)<br/>L2를 다시 거치지 않으므로<br/>종료 상태를 리뷰 미수행으로 되돌림"]
        Regen --> Human
        Human -- "3. 취소" --> AbortB["저장 없이 이탈"]
        SavePlan --> Bundle["지시서 번들 생성<br/>진입점 + common/ + steps/ + 회차별 task-*.md<br/>(계획서 검증 상태를 0번 섹션에 명시)"]
    end

    Bundle --> ToCodegen["코딩 에이전트 구동 경로로 연결 (3.3절)"]
```

</details>

### 3.2. 실행 모드 분기
* **대화형 TUI 모드**: 개발자가 직접 화면을 보며 분석할 SP를 원하는 순서대로 골라 담은 후 배치 전환 계획을 수립하고, AI 검증 결과와 피드백을 실시간 조율하며 승인 및 DB 동기화를 제어합니다.
* **무인 배치 모드 (CI/CD)**: `--all`·`--sp`·`--policy`·`--plan-only` 중 하나가 공급되면 사용자의 대화형 개입 단계를 생략하고 L1/L2 검증을 통과한 산출물을 자동 생성 및 병합하며, 외부 코딩 에이전트 기동까지 파이프라인을 무정지로 실행합니다. 단, Actor/Critic/Consolidator 중 하나라도 CLI 기반 AI 제공자(`claude-cli` | `codex-cli` | `agy-cli`)로 지정되어 있으면 `CliProviderBatchGuard`가 DB 연결 전에 실행을 즉시 중단시킵니다. `AiSettings:AllowCliProviderInBatch`(기본 `false`)를 켜면 `claude-cli`·`codex-cli`에 한해 차단을 열 수 있으며, 이때는 위험을 감수한 실행임을 알리는 경고를 남기고 진행합니다. `agy-cli`는 옵트인 대상이 아닙니다. `--job-name`은 그 자체로 이 모드를 켜지 않습니다 — 계획서를 놓을 이름일 뿐이라 단독으로 주면 TUI로 진입합니다.
* **계획 전용 모드 (`--plan-only`)**: 무인 모드의 한 갈래로, SP를 다시 분석하지 않고 **DB에 연결하지도 않은 채** 이미 저장된 명세서만으로 계획서와 지시서 번들을 만듭니다. 재료는 `--sp`에 적은 순서가 실행 순서가 되며, 진입점의 명세서를 하나라도 찾지 못하면 조용히 빼지 않고 종료 코드 1로 끝냅니다. 재료를 모으는 방식만 다르고 계획 수립부터 코딩 에이전트 기동까지는 위 갈래와 같은 코드를 탑니다. DB 연결을 건너뛸 수 있는 근거는 통합 배치 파이프라인과 지시서 번들 생성이 DB를 부르지 않는다는 것이며, 그 전제는 테스트가 되돌림으로 못박습니다(`ConsolidatedPipelineDbIndependenceTests`). AI는 무인으로 부르므로 `CliProviderBatchGuard`는 이 갈래에도 그대로 적용됩니다.

### 3.3. 외부 코딩 에이전트 자가 수정 및 TDD 검증 흐름 (Codegen Self-Correction Flow)
ReSet이 외부 코딩 에이전트를 가동하고 TDD 선제 검증(L0) 및 L1/L2 피드백 루프를 통해 코드를 고품질로 자가 교정하는 시퀀스 흐름은 다음과 같습니다.

<details>
<summary><b>시퀀스 다이어그램 (클릭하여 펼치기)</b></summary>

```mermaid
sequenceDiagram
    autonumber
    participant CLI as ReSet.Cli + Core (Program / MetadataExporter)
    participant RC as Validator Core (CodegenWorkflowOrchestrator)
    participant ECE as External Coding CLI (Claude)
    participant VAL as Validator Core (CodeVerificationOrchestrator)

    CLI->>CLI: 통합 배치 설계서 확정 후 지시서 번들 생성 (진입점·공통 문서·단계 본문·회차별 task-*.md·progress.json)
    Note over CLI: 설정된 대상 언어(MigrationSettings:TargetLanguage)를 지시서에 각인하고 테스트/프로젝트 구조 생성 책임은 에이전트에 자율 위임
    CLI->>RC: 회차 목록과 코드 생성 디렉터리를 넘겨 순차 회차 실행 위임
    loop 회차 (0회차 골격 ➔ 단계 1..N ➔ 조립), 각 회차마다 최대 MaxL2Attempts 회 (총 시도는 MaxTotalAttempts로 상한)
        RC->>ECE: 코딩 에이전트 기동 (그 회차의 task-*.md 하나만 전달)
        ECE->>ECE: 소스코드 파일 생성/수정, 자체 테스트 구조 구축 및 단위테스트 수행
        alt 자체 빌드 또는 단위테스트 실패 (L0 실패)
            ECE->>ECE: 오류 분석 후 자체 자가 디버깅 시도
        else 자체 빌드 및 테스트 통과 (L0 성공)
            ECE-->>RC: 프로세스 종료 (종료 코드 및 배치 모드에서 캡처한 stderr 반환)
            RC->>RC: 작업 디렉터리 전후 스냅샷을 대조해 산출물 변화 판정
            Note over RC: 종료 코드 0은 성공의 근거가 아니다. 에이전트가 아무것도 쓰지 못한 채 0으로 끝나는 경우가 실재한다.
            alt 산출물 없음 + 쿼터 소진 / 미인증 / 툴 권한 거부
                Note over RC,ECE: 재시도해도 결과가 같으므로 중단 사유를 실어 루프 즉시 종료
            else 산출물 없음 + 그 외 원인
                Note over RC,ECE: 검증을 건너뛰고 지시서를 그대로 둔 채 재기동 (연속 2회 초과 시 중단)
            else 산출물 있음
                RC->>VAL: 그 회차의 산출물만 범위로 잡아 검증 요청 (설계서-소스 대조 쌍 해석 후 L1 수행)
                Note over RC,VAL: 0회차는 대조할 설계서가 없어 검증을 걸지 않고, 조립 회차는 모든 단계가 통과했을 때만 Job 전체 L2를 건다
                alt 대조 쌍 0건 (설계서와 소스의 짝을 찾지 못함)
                    RC->>RC: 소스만 못 찾은 경우에 한해 이름 규약 피드백을 task-*.md에 추가
                    Note over RC,ECE: 통과로 읽지 않는다. 피드백을 먼저 붙이고 연속 2회에서 그 회차를 접는다
                else L1 정적 검증 실패 (구문·중괄호 쌍 오류, 트랜잭션 참여 위반 등)
                    RC->>RC: 그 회차의 task-*.md 하단에 [L1 에러 피드백] 추가
                    Note over RC,ECE: L2 AI 검증 건너뛰고 즉시 재수정 요청 (L1 Shortcut)
                else L1 정적 검증 성공
                    RC->>VAL: L2 AI 의미론적 일치성 분석 수행
                    alt L2 검증 결과 불일치 (MISMATCH / PARTIAL)
                        RC->>RC: 그 회차의 task-*.md 하단에 [L2 Gap Report & Suggestions] 추가
                    else L2 검증 결과 일치 (MATCH)
                        Note over RC,ECE: 이 회차 통과, progress.json에 기록 후 다음 회차로
                    end
                end
            end
        end
        RC->>RC: 회차 결과를 progress.json에 기록하고 todo.md를 다시 렌더링
        Note over RC: 한 회차가 실패해도 사유를 남기고 다음 회차로 넘어간다. 다만 쿼터 소진·미인증처럼 회차와 무관한 실패는 남은 회차를 중단한다.
    end
```

</details>

### 3.4. 정합성 검증기 거시 실행 흐름 (Validator Macro Flow)
마이그레이션된 소스코드와 레거시 DB Stored Procedure 간의 로직 일치성 및 결과 데이터 정합성을 검증하는 `ReSet.Validator` 프로그램의 거시 실행 흐름은 다음과 같습니다. 검증 과정은 단순 TUI 메뉴 분기 외에 선후행 파일의 의존성 관계에 의해 **구조 일치성 검증(A 트랙)** 및 **실행 데이터 정합성 검증(B 트랙)**으로 유기적으로 연결됩니다.

<details>
<summary><b>검증기 거시 흐름도 (클릭하여 펼치기)</b></summary>

```mermaid
graph TD
    %% 초기화 및 모드 판단
    StartVal["검증기 시작 (Validator CLI)"] --> ParseVal["설정 로드 & 디렉토리 유효성 검사"]
    ParseVal --> ModeCheckVal{"배치 모드인가?"}

    %% 배치 모드
    ModeCheckVal -- "예 (--batch)" --> ExecBatchVal["자동 배치 정합성 검증 실행<br/>(인자 조합에 따른 파이프라인 실행)"]
    ExecBatchVal --> EndVal["종료"]

    %% TUI 모드 진입
    ModeCheckVal -- "아니오 (TUI)" --> TuiMenu["검증기 TUI 메인 메뉴 노출"]
    
    %% 구조적 일치성 흐름 (독립적 트랙)
    TuiMenu --> StructTrack["[A 트랙] 소스코드 구조/논리 일치성 검증"]
    StructTrack --> Menu1["1. 설계서 대비 소스코드 논리 일치성 검증 (Code Validation)<br/>FileMapping 매핑 ➔ L1/L2 Gap 검사 ➔ L3 승인"]
    Menu1 --> TuiMenu

    %% 데이터 정합성 검증 흐름 (상호 의존적 파이프라인 트랙)
    TuiMenu --> DataTrack["[B 트랙] 실행 결과 데이터 정합성 검증 파이프라인"]
    
    %% B-1단계: 테스트 자료 설계
    DataTrack --> Step1["B-1. 테스트 설계 및 모의 데이터 생성 (AI)"]
    Step1 --> Menu2["2. 데이터 정합성 대조용 테스트 파라미터 설계 (Test Design)"]
    Step1 --> Menu3["3. 테스트용 모의 데이터 생성 및 적재 (Data Seeding)"]
    
    %% B-2단계: 실행 및 수집 (Seeding 포함)
    Menu2 & Menu3 --> Step2["B-2. Sandbox DB 적재 및 실행 결과 수집"]
    Step2 --> Menu4["4. 레거시 시스템 실행 결과 수집 (Legacy Run)"]
    Step2 --> Menu5["5. 타겟 시스템 실행 결과 수집 (Target Run)"]
    
    %% B-3단계: 최종 비교 대조
    Menu4 & Menu5 --> Step3["B-3. 데이터 정합성 1:1 비교 대조"]
    Step3 --> Menu6["6. 양단 간 데이터 정합성 1:1 대조 보고서 생성 (Data Compare)"]
    
    %% 루프백 및 종료
    Menu6 --> TuiMenu
    TuiMenu --> MenuExit["7. 종료"]
    MenuExit --> EndVal
```

</details>

---

