# 코딩 에이전트 브릿지 헤드리스 배치 설계

- 작성일: 2026-08-07
- 상태: 설계 승인됨 (구현 계획 수립 전)
- 대상: `CodegenSettings`, `ExternalCliCodingEngine`, `CodingEngineFactory`, `CodegenWorkflowOrchestrator`

## 배경

AGENTS.md 범주 6은 **무인 자동 기동**을 요구한다. `--job-name` 배치 실행 시 지시서 생성부터 외부 에이전트 기동까지 연속 수행하는 CI/CD 파이프라인이다. 이 경로가 **세 엔진 모두에서 동작하지 않는다.**

`appsettings.json`의 `Engines:<name>:Arguments`에 적힌 인자는 전부 **대화형 TUI 형식**이다. 무인 실행 조건(비-TTY stdin, 작업 디렉터리 `<job>/src`)에서 실측한 결과다.

| 엔진 | 결과 | 종료 코드 |
|---|---|---|
| claude | `현재 권한 설정으로 인해 해당 파일을 읽을 수 없습니다` — 지시서를 읽지도 못함 | **0** |
| codex | `Error: stdin is not a terminal` | 1 |
| agy | `CLI error: bubbletea: could not open TTY: /dev/tty: device not configured` | **0** |

헤드리스 플래그(`-p` / `exec`)를 붙여 CLI를 정상 기동시킨 뒤 파일 쓰기를 시험하면 **세 엔진 모두 쓰기에 실패하고, 셋 다 종료 코드 0을 반환한다.**

| 엔진 | 응답 | 파일 생성 | 종료 코드 |
|---|---|---|---|
| claude `-p` | `파일 쓰기 권한이 승인되지 않아 …` | ✗ | **0** |
| codex `exec` | `현재 환경이 읽기 전용이라 파일을 만들 수 없습니다` | ✗ | **0** |
| agy `-p` | `no output produced — a tool required the "command" permission that headless mode cannot prompt for, so it was auto-denied` | ✗ | **0** |

## 두 개의 결함

### 결함 1 — 인자 형태가 모드를 구분하지 않는다

AGENTS.md 범주 6은 상충하는 두 가지를 동시에 요구한다.

- **프로세스 양방향 제어**: 대화형 흐름 공유를 위해 부모 콘솔 입출력 스트림을 상속 (`RedirectStandard* = false`의 근거)
- **무인 자동 기동**: CI/CD 무인 파이프라인 지원

두 모드는 CLI 인자 형태 자체가 다르다. `Engines:<name>:Arguments`가 문자열 하나뿐이라 양쪽을 만족시킬 수 없다.

claude에는 추가 제약이 하나 더 있다. 브릿지는 작업 디렉터리를 `<job>/src`로 주는데(`Program.cs:869`, `:2049`) 지시서는 `<job>/agent/MigrationInstructions.md`에 있어 **cwd 바깥**이다. claude는 cwd 밖 파일 읽기에 권한을 요구하고, 헤드리스에서는 물을 수 없어 자동 거부한다. `--add-dir`로 해결되지만 **거기 넣을 경로를 표현할 자리표시자가 없다** — 현재 치환되는 건 `{instructions}` 하나뿐이다.

### 결함 2 — 종료 코드 0이 성공으로 위장된다

`ExternalCliCodingEngine.cs:87`은 `success = exitCode == 0`으로만 판정한다. 위 표대로 claude와 agy는 아무것도 못 하고도 0을 반환한다. 그 결과:

- `CodegenWorkflowOrchestrator.cs:49-54` → `engineSuccess = true`
- 빈 `src/`를 상대로 Critic 검증 → L1 실패 → 피드백을 지시서에 append → 재기동 → 또 아무것도 안 함
- `MaxL2Attempts`를 전부 소진하며 **L2 AI 검증 토큰만 태운다**

codex는 종료 코드 1이라 경고 로그는 남지만(`:53`), 그마저도 루프를 멈추지 않고 검증으로 진행한다.

## 설계

### 1. 설정 스키마

`CodegenSettings:Engines:<name>`에 `BatchArguments`를 추가한다. 대화형은 기존 `Arguments`, 무인 배치는 `BatchArguments`를 쓴다.

```jsonc
"claude": {
  "Command": "claude",
  "Arguments":      "--model claude-sonnet-5 \"write code using {instructions}\"",
  "BatchArguments": "--add-dir {jobDir} --model claude-sonnet-5 --permission-mode acceptEdits -p \"write code using {instructions}\""
},
"codex": {
  "Command": "codex",
  "Arguments":      "-m gpt-5.6-terra \"{instructions}\"",
  "BatchArguments": "exec -m gpt-5.6-terra --skip-git-repo-check --full-auto \"{instructions}\""
},
"agy": {
  "Command": "agy",
  "Arguments":      "--model gemini-3.1-pro --effort high --prompt-interactive \"{instructions} 파일을 읽고 …\"",
  "BatchArguments": ""
}
```

`BatchArguments`가 비면 그 엔진은 **무인 배치 미지원**이다. `Arguments`로 폴백하지 않는다 — 대화형 인자로 무인 실행하면 위 표의 TTY 오류로 조용히 실패하므로, 폴백은 결함 1을 그대로 되살린다.

**agy를 미지원으로 둔 근거.** claude에는 `--permission-mode acceptEdits`, codex에는 샌드박스 기반 `--full-auto`라는 중간 단계가 있어 "파일 쓰기는 허용, 그 외는 통제"를 만들 수 있다. agy에는 그 중간이 없다. 선택지는 `--dangerously-skip-permissions`(툴 22종 무조건 승인, `run_command` 포함) 아니면 agy `settings.json` 허용 규칙뿐이다. 앞의 것은 무인 배치에서 임의 명령 실행을 허용하며, `CliFailureClassifier.cs:116`에 이미 같은 취지의 프로젝트 입장이 명시돼 있다. 또한 이 플래그는 설계 시점에 실측 검증을 하지 못했다. 검증되지 않은 위험한 플래그를 기본 설정으로 배포하지 않는다. 대화형 agy는 영향받지 않는다.

### 2. `{jobDir}` 자리표시자

지시서 경로에서 유도한다. `<job>/agent/MigrationInstructions.md` → `<job>`. 새 파라미터가 필요 없다.

치환 규칙:

| 자리표시자 | 치환값 | 따옴표 |
|---|---|---|
| `{instructions}` | 지시서 절대 경로 | 포함 (현행 유지) |
| `{jobDir}` | Job 루트 절대 경로 | 포함 (경로에 공백 가능) |

`--add-dir`는 가변 인자라 프롬프트보다 앞에 와야 한다. 이 제약은 설정 파일 주석으로 경고하고, **코드가 인자 순서를 강제하지는 않는다** — 강제하는 순간 "설정으로 자유롭게 조정"이라는 이 구조의 이점이 사라진다.

### 3. 팩토리

`CreateEngine(string engineName, bool isBatchMode)`로 시그니처를 바꾼다. 모드 선택을 생성 시점에 끝내면 `ICodingEngine`은 자기가 배치인지 알 필요가 없다.

배치인데 `BatchArguments`가 비면 여기서 예외를 던진다.

> `{engineName} 엔진은 무인 배치 모드를 지원하지 않습니다(BatchArguments 미지정). CodegenSettings:Engine을 배치를 지원하는 엔진으로 변경하거나, CodegenSettings:Engines:{engineName}:BatchArguments를 채우십시오.`

### 4. 모드별 스트림 처리

대화형은 지금 그대로 부모 콘솔 스트림을 상속한다 (AGENTS.md 범주 6 "양방향 제어"). 배치에서만 바뀐다.

| 스트림 | 대화형 | 배치 |
|---|---|---|
| stdin | 상속 | 리다이렉트 후 즉시 닫음 |
| stdout | 상속 | 상속 (CI 로그에 진행 상황이 보여야 함) |
| stderr | 상속 | 캡처 (분류용) |

stdin을 닫는 근거는 실측이다. 정상 동작한 조건이 정확히 `< /dev/null`이었다. 상속된 TTY를 그대로 두면 CLI가 대화형으로 오인할 여지가 남는다.

stderr는 **비동기로 읽으면서** `WaitForExit`을 건다. 순서를 바꾸면 파이프 버퍼가 차는 순간 교착한다.

### 5. 산출물 변화 감지

기동 전후로 `targetProjectDir`를 재귀 스냅샷한다. 항목은 `(상대경로, 길이, 최종수정시각)`이고, 집합이 달라지면 산출물이 생긴 것으로 본다 (추가·삭제·수정 모두 포함).

제외 디렉터리: `bin`, `obj`, `.git`, `node_modules`, `.vs`, `target`.

이 제외가 없으면 에이전트가 코드는 안 쓰고 `dotnet build`만 돌려도 "산출물 생성"으로 잡혀 감지 자체가 무력해진다.

### 6. 실패 분류

기존 `CliFailureClassifier`를 재사용한다. 캡처한 stderr로 `CliProcessResult`를 만들어 넘긴다.

```csharp
var probe = new CliProcessResult { ExitCode = exitCode, StandardError = capturedStderr };
var kind  = CliFailureClassifier.Classify(probe, extraDetail: null);
```

분류기가 stdout을 의도적으로 보지 않는다는 기존 설계(`CliFailureClassifier.cs:61-68`)와 맞아떨어진다. 여기서도 stdout은 캡처하지 않고 콘솔로 흘려보낸다.

대화형 모드에서는 stderr를 캡처하지 않으므로 `FailureKind`가 항상 `Unknown`이다. 사용자가 화면에서 오류를 직접 보고 있으므로 문제되지 않는다.

### 7. 결과 모델

```csharp
public sealed record CodegenRunResult(
    bool ProducedArtifacts,
    int ExitCode,
    CliFailureKind FailureKind,
    string? Diagnostic);
```

`Diagnostic`은 배치 모드에서 캡처한 stderr 원문이다. 대화형 모드에서는 캡처하지 않으므로 `null`이다. 자르지 않는다 — 분류가 `Unknown`으로 떨어졌을 때 사람이 원인을 볼 수 있어야 한다.

`ICodingEngine.GenerateCodeAsync`의 반환형을 `bool`에서 이 타입으로 바꾼다. 엔진은 "무슨 일이 있었는지"를 사실대로 보고하고, "루프를 계속할지"는 오케스트레이터가 판단한다.

성공 여부를 나타내는 편의 속성은 두지 않는다. 루프 판단은 `ProducedArtifacts`와 `FailureKind`의 조합으로 이뤄지고(§8), 종료 코드 단독으로는 아무것도 결정하지 않는다. `Succeeded` 같은 속성을 두면 결함 2를 만든 것과 똑같은 착각 — "0이면 성공" — 을 다시 불러들인다.

### 8. 루프 제어

```csharp
var run = await _codingEngine.GenerateCodeAsync(null, instructionsFilePath, codeDir, ct);

if (run.ProducedArtifacts)
{
    // 종료 코드와 무관하게 검증 진행. 부분 산출물도 L1/L2가 볼 가치가 있다.
}
else if (run.FailureKind is QuotaExhausted or NotAuthenticated or ToolPermissionDenied)
{
    // 재시도해도 결과가 같다. 루프를 즉시 끝낸다.
}
else
{
    // 일시적일 수 있다. 검증만 건너뛰고 다음 시도로.
}
```

검증을 건너뛴 시도에서는 **피드백을 지시서에 추가하지 않는다.** 붙일 검증 결과가 없고, 지시서를 손대지 않은 채 그대로 재시도하는 것이 맞다.

### 9. 중단 이유 노출

`RunSelfHealingWorkflowAsync`가 `bool`만 돌려주면 호출부는 "검증 실패"와 "에이전트가 아예 못 돌았음"을 구분하지 못한다. 인증이 만료돼 중단됐는데 화면에는 빨간 "실패"만 뜨고 이유는 로그 파일에만 남는다 — 무인 배치에서 가장 알아야 할 정보가 가장 안 보이는 곳으로 간다.

```csharp
public sealed record CodegenWorkflowResult(bool Succeeded, string? AbortReason);
```

`AbortReason`은 §8에서 루프를 즉시 끝낸 경우에만 채운다. 값은 `CliFailureClassifier.ToException(...).Message`다 — 분류별 안내문(쿼터 소진 시 provider 변경, 미인증 시 로그인 등)이 이미 거기 있으므로, 같은 말을 두 곳에서 다르게 쓰지 않는다. 예외를 만들어 메시지만 꺼내 쓰는 형태가 어색하면 구현 단계에서 요약 문구 생성부를 별도 메서드로 분리하되, **문구 자체는 한 곳에서만 정의한다.**

`Program.cs`가 `AbortReason`이 있으면 콘솔에 출력한다.

### 10. 작업 디렉터리 보장

`targetProjectDir`(`<job>/src`)를 **아무도 생성하지 않는다.** `MetadataExporter.cs:616`이 만드는 것은 `<job>/agent/src`이고, 브릿지가 작업 디렉터리로 넘기는 것은 `<job>/src`다. 존재하지 않는 디렉터리를 `ProcessStartInfo.WorkingDirectory`에 주면 `Process.Start`가 던지고, 현재 코드는 이를 "명령어가 설치되어 있는지 확인해 주십시오"라는 무관한 메시지로 감싼다.

산출물 스냅샷도 이 디렉터리를 전제하므로, 기동 직전 `<job>/src` 보장 생성을 포함한다.

`agent/src` 스텁과 `src` 작업 디렉터리가 갈라져 있는 문제는 **이번 범위 밖**이다. 별도 사안으로 남긴다.

## 범위에서 제외

**타임아웃을 넣지 않는다.** 코딩 에이전트가 수십 분 도는 것은 정상이고, `AiSettings:TimeoutSeconds`(HTTP 호출용)를 끌어다 쓰면 정상 작업을 끊는다. 취소는 지금처럼 `CancellationToken` + `Kill(true)`로 처리한다. 따라서 `CliFailureKind.Timeout`은 이 경로에서 발생하지 않는다.

**`CliFailureClassifier.CommandNotFound`를 재사용하지 않는다.** 그 메시지가 안내하는 설정 키는 `AiSettings:Providers:*:Command`라서 여기서는 틀린 경로를 알려주게 된다. 대신 기존 예외 문구가 `CodegenSettings:Engines:<name>:Command`를 가리키도록 고친다.

## 테스트

핵심은 프로세스를 띄우지 않고 검증할 수 있도록 순수 함수를 뽑아내는 것이다. 현재 코드는 인자 치환도 루프 판단도 부수효과 한가운데 묻혀 있어 테스트가 불가능하다.

| 대상 | 뽑아낼 단위 | 테스트 |
|---|---|---|
| 인자 치환 | `ArgumentTemplateResolver.Resolve(template, instructionsPath)` | `{instructions}`·`{jobDir}` 각각 치환, 둘 다 있는 경우, 공백 포함 경로가 따옴표로 감싸지는지 |
| 산출물 감지 | `ArtifactChangeDetector.Snapshot(dir)` / `HasChanged(before, after)` | 추가·수정·삭제 감지, `bin`/`obj` 변화는 무시, 빈 디렉터리는 변화 없음 |
| 루프 판단 | `Decide(CodegenRunResult)` → `Validate` \| `RetryWithoutValidation` \| `Abort` | 산출물 있으면 항상 `Validate`, 산출물 없고 Quota/Auth/ToolPerm이면 `Abort`, 산출물 없고 Unknown이면 `RetryWithoutValidation` |
| 팩토리 | 기존 `CodingEngineTests` 확장 | 배치는 `BatchArguments` 선택, 대화형은 `Arguments` 선택, 배치인데 `BatchArguments`가 비면 예외 |

`CodeVerificationOrchestrator`는 인터페이스가 없는 구상 클래스라 루프 전체를 목으로 감싸려면 그것부터 손대야 한다. `Decide`를 순수 함수로 분리하면 그 리팩터링 없이 판단 로직을 전부 검증할 수 있다.

기존 테스트 3건(`CodingEngineTests`)은 그대로 통과해야 한다.

**CLI 실측 재확인**은 구현 계획의 수동 단계로 남긴다. claude와 codex의 `BatchArguments`는 설계 시점에 파일 생성까지 확인했으나, 코드가 조립한 실제 인자 문자열로 다시 확인해야 한다.

## 문서 갱신

- `README.md:238` 설정 예시에 `BatchArguments` 반영
- `AGENTS.md` 범주 6에 대화형/배치 인자 분리 규칙 추가
- **`AGENTS.md:63` 정정** — "`CodegenSettings:Engines:agy`는 툴이 켜져 있는 것이 정상이므로 이 제약과 무관합니다"는 사실과 다르다. agy는 브릿지 경로에서도 헤드리스 툴 자동 거부에 걸린다 (실측: `no output produced — … auto-denied`, 종료 코드 0)
