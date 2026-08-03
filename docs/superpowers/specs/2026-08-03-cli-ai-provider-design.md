# CLI 코딩 에이전트를 AI Provider로 사용하는 설계

- 작성일: 2026-08-03
- 상태: 설계 승인됨 (구현 계획 수립 전)

## 배경

ReSet의 AI 호출은 전부 HTTP API를 경유한다. `AiClientFactory`가 아는 provider는 OpenAI, Google, Claude, Ollama, mlx, vLLM, local-openai, Z.ai 여덟 종이고, 모두 API 키와 엔드포인트를 요구하며 토큰 단위로 과금된다.

한편 개발자는 이미 Claude Max, ChatGPT Pro 같은 **정액 구독**을 보유하고 있고, 그 구독으로 동작하는 CLI 코딩 에이전트(`claude`, `codex`, `agy`)가 로컬에 설치되어 있다. 같은 모델을 같은 계정으로 쓰면서 API 경로에만 추가 과금이 발생하는 상태다.

**목적은 비용 절감이다.** 분석·리뷰 호출을 정액 구독 CLI로 돌려 API 종량 과금을 대체한다.

## 실행 가능성 — 실측 결과

세 CLI 모두 헤드리스 모드를 제공하며, 실제로 호출해 확인했다.

| CLI | 호출 | 결과 추출 | 왕복 | 시스템 프롬프트 | effort | 구조화 출력 |
|---|---|---|---|---|---|---|
| `claude` | `-p --output-format json` | JSON `.result` | 2.5초 | `--system-prompt[-file]` | `--effort low\|medium\|high\|xhigh\|max` | `--json-schema` |
| `codex` | `exec - --sandbox read-only` | `-o <파일>` | 6.5초 | 없음 | `-c model_reasoning_effort=` | `--output-schema` |
| `agy` | `-p --output-format json` | JSON `.response` | 10초 | 없음 | `--effort low\|medium\|high` | `--json-schema` |

`--tools ""`(claude) / `--sandbox read-only`(codex)로 툴을 죽이면 순수 LLM처럼 동작한다. 실측 응답에 군더더기는 없었다.

### 삽입 지점은 이미 있다

`src/ReSet.Core/Services/IAiClient.cs:5`:

```csharp
Task<AiResult> ChatAsync(string systemPrompt, string userPrompt, float temperature, string? effort, CancellationToken ct);
```

상태 없는 1회성 호출 계약이다. 프로세스를 매번 새로 띄우는 CLI 헤드리스 모드와 의미론이 정확히 일치한다. `AiClientFactory`에 케이스를 추가하면 `AiService`와 `VerificationPipelineOrchestrator`는 손대지 않아도 된다.

`src/ReSet.Core/Services/ExternalCliCodingEngine.cs`에 프로세스 기동과 취소 토큰 강제 종료 패턴이 이미 검증된 채로 존재한다(`:67-82`).

`src/ReSet.Validator.Cli/Program.cs:222`도 같은 팩토리를 쓴다. 팩토리에 케이스를 추가하면 Validator의 Gap 분석과 모의 데이터 생성까지 추가 작업 없이 CLI provider를 지원한다.

## 전제

사용자가 확정한 운용 조건이다. 설계 범위를 크게 줄인다.

- CLI provider는 **대화형 TUI 전용**이다. 무인 배치 모드에서는 쓰지 않는다
- `ActorEffort: dynamic`은 쓰지 않는다. 후보 3종 병렬 생성이 없으므로 **호출당 프로세스 1개**다
- 쿼터가 소진되면 **즉시 실패**한다. 자동 폴백은 만들지 않는다. 사람이 `appsettings.json`에서 다른 provider로 바꿔 재실행한다
- 기존 API provider 여덟 종은 그대로 둔다. CLI 3종을 나란히 추가한다

자동 폴백을 만들지 않기로 한 결과, **실패 원인 분류가 이 설계의 핵심 산출물**이 된다. 사람이 로그만 보고 "다른 CLI로 갈지, API로 갈지"를 판단할 수 있어야 한다.

## 구조

provider별 전용 클래스 3개와 공통 프로세스 러너를 만든다.

```
src/ReSet.Core/Services/Clients/
  ├─ Cli/
  │   ├─ CliProcessRunner.cs       # 프로세스·stdin·stdout/stderr·취소·타임아웃
  │   ├─ ClaudeCliClient.cs
  │   ├─ CodexCliClient.cs
  │   └─ AntigravityCliClient.cs
  ├─ ClaudeClient.cs               # 기존 API
  └─ OpenAiClient.cs               # 기존 API
```

**설정 주도 범용 클래스 하나를 쓰지 않는 이유**는 세 CLI의 차이가 설정으로 뭉개기엔 너무 크기 때문이다. agy는 stdin을 못 받고, codex는 결과를 파일로 내보내며, effort 플래그 이름과 결과 JSON 경로가 제각각이다. 이것들을 설정 스키마로 표현하면 `PromptVia`, `ResultJsonPath`, `EffortFlag` 같은 필드가 줄줄이 붙고, 오타는 런타임에 정체불명의 파싱 실패로 나타난다. 대상이 3개로 고정되어 있으므로 확장성의 이득도 실현되지 않는다.

기존 `Clients/` 폴더가 이미 provider당 클래스 1개 패턴이다. 그 패턴을 따른다.

## 설정 스키마

`AiSettings:Providers` 아래에 API provider와 나란히 추가한다.

```json
"claude-cli": { "Command": "claude" },
"codex-cli":  { "Command": "codex"  },
"agy-cli":    { "Command": "agy"    }
```

API provider가 `ApiKey`/`Endpoint`를 갖듯 CLI provider는 `Command`를 갖는다. PATH에 없거나 버전을 나눠 쓰는 경우 절대 경로를 지정할 수 있어야 하므로 노출한다. 미지정 시 provider별 기본 명령어를 쓴다.

`AiSettings:ModelName`, `Critic:*`, `Consolidator:*`는 **바꾸지 않는다.** 이미 provider와 model을 역할별로 독립 지정하는 구조라 `Provider: "claude-cli"` + `Critic:Provider: "codex-cli"` 같은 조합이 추가 작업 없이 성립한다. 모델명은 각 CLI가 아는 별칭(`sonnet`, `gpt-5.6-terra` 등)을 그대로 쓰고, 비우면 CLI 기본값을 따른다.

`AiSettings:TimeoutSeconds`를 그대로 재사용한다. CLI 클라이언트는 이 값을 넘기면 프로세스를 강제 종료한다.

### 팩토리 배선

`AiClientFactory.CreateClient`에 선택적 매개변수 `string? command = null`을 마지막에 추가한다.

```csharp
"claude-cli" => new ClaudeCliClient(command ?? "claude", modelName, timeout),
"codex-cli"  => new CodexCliClient(command ?? "codex", modelName, timeout),
"agy-cli"    => new AntigravityCliClient(command ?? "agy", modelName, timeout),
```

타임아웃은 **새 매개변수로 받지 않는다.** 팩토리는 이미 `HttpClient`를 인수로 받고, 호출부가 그 인스턴스를 `AiSettings:TimeoutSeconds`로 만들어 넘긴다(`Program.cs:358-363`). 팩토리 안에서 `client.Timeout`을 읽어 CLI 클라이언트에 전달하면 설정이 한 곳에서만 관리되고 API 경로와 값이 어긋날 여지가 없다.

호출부 4곳(`ReSet.Cli/Program.cs`의 Actor `:383`·Critic `:406`·Consolidator `:427`, `ReSet.Validator.Cli/Program.cs:222`)이 한 줄씩 바뀐다.

**`endpoint` 슬롯에 명령어 경로를 밀어넣지 않는다.** 의미가 뒤틀리고, 전용 클래스를 고른 이유와 어긋난다.

`IsLocalProvider`에는 CLI provider를 넣지 않는다. 이 술어는 로컬 LLM의 분할 생성 파이프라인(`EnableLocalChunking`)을 켜는 용도이며, CLI provider는 컨텍스트 한계가 다르다.

## 각 클라이언트의 호출 형태

### 공통 — CliProcessRunner

- 프로세스 기동, stdin 주입, **stdout/stderr 동시 비동기 수집**, 타임아웃, 취소 토큰 강제 종료(`Kill(true)`)
- stdout/stderr를 동시에 읽는 것이 중요하다. 명세서 응답은 수십 KB이고, 한쪽만 읽으면 파이프 버퍼가 차서 데드락에 빠진다. `ExternalCliCodingEngine`은 리디렉션을 아예 하지 않아 이 문제가 없었지만 여기서는 출력을 받아야 한다
- 취소 처리는 `ExternalCliCodingEngine.cs:67-82`의 패턴을 재사용한다. **`OperationCanceledException`을 다른 타입으로 감싸지 않는다** — `2026-08-03-cancellation-policy-design.md`가 정한 규칙이다
- 호출마다 빈 임시 작업 디렉토리를 만들어 거기서 실행하고, 끝나면 정리한다. 이유는 아래 "컨텍스트 격리" 참조

### ClaudeCliClient

```
claude -p --output-format json --tools "" --disable-slash-commands
       --no-session-persistence --model <ModelName>
       --system-prompt-file <임시파일>
       [--effort low|medium|high|xhigh|max]
  stdin ← userPrompt
```

`--system-prompt`(전체 교체)를 `--append-system-prompt`(추가) 대신 쓴다. 실측에서 호출당 오버헤드가 **10,186 → 1,451 토큰**으로 떨어졌다. 구독 쿼터가 목적이므로 7배 차이가 그대로 이득이다.

시스템 프롬프트를 argv가 아니라 `--system-prompt-file`로 넘긴다. ReSet의 시스템 프롬프트도 클 수 있고, 파일 경유는 크기 제약이 없다. 이 플래그는 `--help` 목록에는 없지만 실측으로 동작을 확인했다(`--bare` 설명문에 언급되어 있다).

결과는 JSON `.result`에서 뽑는다. `.is_error`, `.subtype`, `.api_error_status`로 실패를 판별한다.

### CodexCliClient

```
codex exec - --sandbox read-only --skip-git-repo-check --ephemeral
      -m <ModelName> -c model_reasoning_effort=<...> -o <임시파일>
  stdin ← systemPrompt + "\n\n" + userPrompt
```

codex는 시스템 프롬프트 분리 개념이 없어 병합한다. 결과는 `-o` 파일에서 읽는다. stdout에는 진행 로그가 섞이므로 쓰지 않는다.

### AntigravityCliClient

```
agy -p "<systemPrompt + userPrompt>" --output-format json
    --model <ModelName> --effort <low|medium|high> --print-timeout <T>
```

**agy만 stdin으로 프롬프트를 받지 못한다.** 실측에서 파이프로 주면 툴 권한 오류로 빈 응답이 났다. argv로 넘겨야 한다.

ReSet의 실제 프롬프트 최대치는 **191KB**다(`output/Jobs/Settle_Proc_Daily/raw/prompt-context.md`). macOS/Linux는 ARG_MAX가 1MB라 통과하지만, **Windows는 명령행 32KB 제한이라 대형 SP에서 실패한다.**

우회로가 없으므로, 조립한 명령행 길이를 **호출 전에 검사**해 한계를 넘으면 명확한 예외를 던진다.

```
이 프롬프트는 agy-cli로 처리할 수 없습니다 (크기 187KB, 플랫폼 한계 32KB).
claude-cli 또는 API provider를 사용하십시오.
```

조용히 잘리거나 알 수 없는 오류로 죽는 것보다 낫다. 한계값은 플랫폼별로 다르게 잡는다(Windows 32,767자, 그 외 ARG_MAX 조회값 또는 보수적 기본값).

### effort 매핑

ReSet은 `low|medium|high|xhigh`를 쓴다(`ClaudeClient.cs:88-95` 참조).

| ReSet | claude-cli | codex-cli | agy-cli |
|---|---|---|---|
| low | `low` | `low` | `low` |
| medium | `medium` | `medium` | `medium` |
| high | `high` | `high` | `high` |
| xhigh | `xhigh` | `high` (클램프) | `high` (클램프) |

클램프가 일어나면 로그에 남긴다. 요청한 추론 강도가 조용히 낮아지는 것은 품질에 영향을 준다.

`effort`가 null이면 해당 플래그를 붙이지 않고 CLI 기본값을 따른다.

## 실패 처리

자동 폴백이 없으므로 원인 분류가 사람의 유일한 판단 근거다. 네 가지로 나눈다.

| 원인 | 판별 | 메시지 |
|---|---|---|
| CLI 미설치 | `Win32Exception` (실행 파일 없음) | 명령어를 찾을 수 없음 → 설치·PATH·`Command` 설정 확인 |
| 미인증 | stderr 인증 패턴 + 비정상 종료 | 해당 CLI 로그인 필요 |
| 쿼터 소진 | claude `.subtype`/`.api_error_status`, codex/agy stderr | 구독 한도 소진 → 다른 provider 전환 안내 |
| 타임아웃 | `TimeoutSeconds` 초과 | 프로세스 강제 종료됨 |

전부 `InvalidOperationException`으로 올린다(기존 클라이언트 관례). **원문 stderr를 잘라내지 않고 메시지에 포함한다** — 분류를 못 맞힌 경우에도 진단이 가능해야 한다.

취소(`OperationCanceledException`)는 위 분류에 넣지 않고 그대로 전파한다.

## 배치 모드 가드

무인 배치 도중 쿼터가 소진되거나 CLI가 권한 프롬프트에서 멈추면, 수십 분에서 수 시간짜리 실행이 통째로 날아간다. 설정 실수 한 번의 대가가 지나치게 크다.

`src/ReSet.Cli/Program.cs`에 이미 `cliArgs.IsBatchMode`가 있다(`:214`, `:500`). 배치 모드에서 Actor·Critic·Consolidator 중 **하나라도** CLI provider면 시작 직후 중단하고 이유를 출력한다.

세 역할을 모두 검사하는 것이 중요하다. Actor만 API여도 Critic이 CLI면 같은 사고가 난다.

시작 5초 만에 실패하는 편이 3시간 뒤에 실패하는 것보다 낫다.

## 컨텍스트 격리

CLI를 ReSet 프로젝트 디렉토리에서 그냥 띄우면 `CLAUDE.md`와 `AGENTS.md`(53KB)를 자동으로 읽어 컨텍스트에 얹는다. 분석 품질을 오염시키고 쿼터를 낭비한다.

호출마다 빈 임시 디렉토리를 만들어 작업 디렉토리로 지정하고, 종료 시 정리한다. claude는 `--system-prompt`(교체)와 `--disable-slash-commands`로 나머지 자동 주입도 차단된다.

## temperature

**세 CLI 모두 temperature를 노출하지 않는다.**

ReSet은 이 값을 의미 있게 쓴다 — Critic 채점에 0.1(`AiService.cs:1642`), 일반 생성에 설정값(기본 0.2). CLI 경로에서는 전부 무시된다.

조용히 무시하지 않고 **클라이언트 생성 시 1회 경고 로그**를 남긴다. 호출마다 남기면 로그가 도배된다.

## 프롬프트 캐싱

README가 내세우는 프롬프트 캐싱은 CLI 경로에서도 유효하다. 프로세스는 매번 새로 뜨지만 캐시는 서버 측에서 프롬프트 접두사 기준으로 잡힌다. 실측에서 `cache_read_input_tokens: 3298`을 확인했다.

반복되는 시스템 규칙과 스키마 메타데이터는 계속 캐시 히트된다.

## 테스트 전략

`tests/ReSet.Core.Tests/`에 `AiClientFactoryTests.cs`와 `ExternalCliCodingEngineTests.cs`가 이미 있어 패턴이 잡혀 있다.

**실제 CLI를 호출하는 테스트는 만들지 않는다.** 비용·쿼터·네트워크·로그인 상태에 의존하므로 CI에서 재현되지 않는다.

대신 순수 함수로 분리해 검증한다.

| 대상 | 방법 |
|---|---|
| 팩토리 해석 | `claude-cli`/`codex-cli`/`agy-cli` 문자열이 올바른 타입으로 해석되는지. `Command` 미지정 시 기본 명령어가 쓰이는지 |
| 인자 조립 | 각 클라이언트가 기대한 인자 목록을 만드는지. effort 클램프가 적용되는지 |
| 응답 파싱 | 실측 JSON을 픽스처로 고정하고 결과 텍스트 추출과 오류 판별을 확인 |
| agy 길이 검사 | 명령행이 플랫폼 한계를 넘을 때 호출 **전에** 예외가 나는지 |
| 배치 모드 가드 | Actor·Critic·Consolidator 각각이 CLI provider일 때 모두 차단되는지 |
| 취소 전파 | `CliProcessRunner`가 `OperationCanceledException`을 감싸지 않는지 |

인자 조립과 응답 파싱을 테스트하려면 이 둘이 프로세스 기동과 분리되어야 한다. 각 클라이언트에서 "인자 목록을 만드는 함수"와 "출력에서 결과를 뽑는 함수"를 **`public static` 메서드**로 둔다. 이 저장소는 `InternalsVisibleTo`를 쓰지 않으며 서비스와 클라이언트가 전부 `public`이다. 그 관례를 따른다.

프로세스 기동 자체는 **스텁 명령어**로 검증한다. `ExternalCliCodingEngineTests`가 이미 쓰는 방법으로, 실제 CLI 대신 `echo`/`sh -c`/존재하지 않는 명령어를 주고 성공·비정상 종료·미설치 경로를 확인한다(Windows에서는 `cmd /c`). 픽스처 JSON을 뱉는 스텁을 클라이언트에 물리면 인자 조립부터 파싱까지 한 번에 확인할 수 있고, 모킹 인프라가 필요 없다.

## 에러 처리 규약

프로젝트 규약을 따른다.

- `OperationCanceledException`을 삼키거나 다른 타입으로 감싸지 않는다
- Spectre.Console 출력에 들어가는 런타임 값(stderr 원문, 파일 경로)은 `Markup.Escape`로 이스케이프한다
- API 키를 소스나 `appsettings.json`에 하드코딩하지 않는다. CLI provider는 애초에 키를 갖지 않는다
- 임시 파일과 임시 디렉토리는 실패 경로에서도 정리한다

## 범위 밖

- **자동 폴백 체인** — 사용자가 명시적으로 제외했다. 실패 시 사람이 설정을 바꿔 재실행한다
- **구조화 출력(`--json-schema` / `--output-schema`)** — 세 CLI 모두 지원하며 Critic 채점 JSON 파싱을 지금보다 견고하게 만들 수 있다. 다만 현재 `ExtractJson` 기반 파싱이 동작 중이고, 스키마 정의는 API 경로와 CLI 경로의 동작을 갈라놓는다. 별도 사이클로 미룬다
- **`ActorEffort: dynamic`에서의 동시 실행 제어** — 사용자가 dynamic을 쓰지 않으므로 동시 프로세스는 1개다. dynamic을 쓰게 되면 프로세스 3개가 동시에 뜨고 쿼터 소진이 빨라지므로 그때 별도 검토가 필요하다
- **agy의 Windows 명령행 한계 우회** — stdin을 못 받는 것은 CLI 쪽 제약이라 ReSet에서 해결할 수 없다. 명확한 예외로 알리는 데서 멈춘다
- **비용 계측** — claude JSON은 `total_cost_usd`를 돌려주지만(실측 0.042), 이는 API 환산가이지 구독 사용자의 실제 지출이 아니다. 오해를 부르므로 산출물에 싣지 않는다
