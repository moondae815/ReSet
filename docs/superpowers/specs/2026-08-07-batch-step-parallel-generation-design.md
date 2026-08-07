# 통합 배치 단계 본문 병렬 생성 설계

- 작성일: 2026-08-07
- 상태: 설계 승인됨 (구현 계획 수립 전)
- 선행: [2026-08-06 단계별 분할 생성](2026-08-06-batch-plan-step-split-design.md)

## 배경

통합 배치 계획서의 `## 단계별 이행 상세 및 의사코드`를 단계마다 한 번씩 호출해 만드는 분할 생성이 `feat/batch-plan-step-split`에서 완료됐다. 목표였던 출력 예산 붕괴는 사라졌다 — 실측 비교는 이렇다.

| | 단일 호출 (`POQSettleProcDaily`) | 분할 (`POQSettleProcDaily2`) |
|---|---|---|
| 단계 본문 합계 | 1,062줄 | 6,514줄 |
| 단계별 최소 / 최대 | 12줄 / 386줄 (32배) | 295줄 / 620줄 (2.1배) |
| 코드 블록이 없는 단계 | 4개 | 0개 |

대신 벽시계가 드러났다. 같은 실행의 산출물 타임스탬프로 **48분 00초**가 측정됐다(`raw/Brainstorming.md` 22:13:54 → `docs/BatchMigrationPlan.md` 23:01:54). 시도 3회 기준 분해는 다음과 같다.

| 구간 | 1회당 | 3회 합계 |
|---|---:|---:|
| 브레인스토밍 + 목차 (1회만) | 약 3분 | 3분 |
| 골격 | 약 2분 | 6분 |
| **단계 13개 (순차)** | **약 13분** | **약 39분** |

**단계 생성이 회차 시간의 85%, 전체의 81%다.** 선행 설계의 §7 비용표는 토큰만 따졌고 시간은 검토한 적이 없다. 이 설계는 그 구간을 병렬화한다.

## 목표와 범위

단계 본문 생성을 순차에서 제한된 병렬로 바꿔 벽시계를 줄인다. 프롬프트 접두사 캐시의 이점과 산출물의 결정성은 그대로 유지한다.

**범위 안**
- 단계 본문 생성의 병렬 실행과 동시 실행 수 제한
- 첫 단계 단독 실행(캐시 워밍)
- 공유 가변 상태 제거와 결정적 병합
- 동시 실행 수 설정 키

**범위 밖**
- 429 백오프·재시도 정책. `OpenAiClient`는 현재 비성공 응답을 즉시 예외로 던지며 재시도가 없다(`OpenAiClient.cs:95`). 이를 바꾸는 것은 이 클래스 전반의 오류 정책을 건드리는 별개 과제다. 이 설계는 동시 실행 수를 사용자가 낮출 수 있게 하는 것으로 대응한다
- 골격·브레인스토밍·목차 호출의 병렬화. 각각 1회씩이라 병렬화할 대상이 없다
- L3 피드백 재생성 경로. 그 경로는 아직 분할 생성을 쓰지 않으며(단일 호출), 별건이다
- 캐시 접두사 규약, 하한 검사, 배너, `DefectiveSteps` 지목 재생성 — 전부 무변경

## 설계

### 1. 실행 형태

```
골격 1회
  ↓
[워밍]   Steps[0] 단독 실행
  ↓
[팬아웃] Steps[1..N] 을 SemaphoreSlim(동시 실행 수)로 제한해 Task.WhenAll
  ↓
[병합]   단일 스레드에서 목차 순서대로 sections·floorViolations 채움
  ↓
조립 (현행 그대로)
```

지목 재생성 경로도 같은 형태를 쓴다. 지목된 단계가 하나면 워밍과 팬아웃이 같은 호출이 되어 자연히 순차가 된다.

### 2. 캐시 워밍을 코드로 강제하는 이유

프롬프트 접두사 캐시는 **요청이 완료돼야 채워진다.** N개를 동시에 쏘면 N개 전부 미스다. 선행 설계가 시스템 프롬프트에서 단계 파생 값을 걷어낸 것도(그 설계의 사용자 판정 사항) 이 캐시를 살리기 위해서였으므로, 병렬화가 그것을 무효로 만들면 안 된다.

워밍은 벽시계를 쓰지 않는다.

| 방식 | 라운드 수 (13단계, 동시 4) | 캐시 미스 |
|---|---:|---:|
| 워밍 없이 4씩 | `ceil(13/4)` = 4라운드 | 4회 |
| 1개 워밍 후 4씩 | `1 + ceil(12/4)` = 4라운드 | 1회 |

같은 4라운드로 미스가 4회에서 1회가 된다. 회차당 약 33만, 3회차면 약 100만 입력 토큰 차이다.

**설정값이 얼마든 첫 단계는 항상 단독으로 돈다.** 이 값을 만지는 사람이 접두사 캐시의 존재를 알아야 할 이유가 없어야 하기 때문이다.

### 3. 설정 키

`appsettings.json`의 `AiSettings` 아래, `MaxL2Attempts` 옆에 둔다.

```jsonc
"StepConcurrency": 4,   // [통합 배치] 단계 본문 동시 생성 수. 1이면 순차(종전 동작).
                        // 첫 단계는 항상 단독 실행해 프롬프트 접두사 캐시를 채운 뒤
                        // 나머지를 이 수만큼 동시 실행합니다. 값을 올려도 전체 벽시계는
                        // 골격 호출(약 2분) 아래로 내려가지 않습니다.
                        // 로컬 모델(Ollama·mlx·local-openai)에서는 1을 권장합니다 —
                        // 단일 GPU에서 동시 실행은 순차보다 느리거나 메모리가 터집니다.
```

기본값을 4로 두는 근거는 골격 호출이 이미 약 2분이라는 점이다. 단계 구간을 그 아래로 줄여도 전체에서 체감되지 않는다. 4는 13분을 약 4분으로 줄이며, 12로 올려도 약 2분을 얻을 뿐인데 429 위험은 3배가 된다.

### 4. 오케스트레이터 생성자

```csharp
public VerificationPipelineOrchestrator(
    ...,
    int criticScoreThreshold = 8,
    int stepConcurrency = 1)     // 기본값 1 = 종전 순차
```

**기본값이 두 곳에서 다르다. 의도된 것이다.**

| 위치 | 기본값 | 이유 |
|---|---:|---|
| `appsettings.json`의 `StepConcurrency` | 4 | 실사용 값. 설정 파일을 건드리지 않은 사용자가 병렬의 이득을 본다 |
| 생성자 매개변수 `stepConcurrency` | 1 | 테스트 기본값. 인자를 넘기지 않는 호출은 종전 순차를 유지한다 |

`Program.cs`는 항상 설정값을 명시적으로 넘기므로 실제 실행에서 생성자 기본값이 쓰이는 일은 없다. **생성자 기본값 1이 회귀 방어의 본체다** — `VerificationPipelineOrchestratorTests`에는 오케스트레이터를 만드는 곳이 94군데 있고, 기본값이 1이면 그 94개가 한 줄도 바뀌지 않고 종전과 동일한 순차 동작을 유지한다. 병렬은 명시적으로 켠 테스트에서만 돈다.

값 해석은 생성자에서 한 번만 한다.

```csharp
// 0·음수는 1로 절상한다. 상한은 두지 않는다 — 사용자가 12를 원하면 12를 쓴다.
_stepConcurrency = Math.Max(1, stepConcurrency);
```

로컬 공급자에서 1을 넘기면 실행 시점에 한 번 경고한다(`AiClientFactory.IsLocalProvider`). **자동으로 1로 강제하지 않는다** — 사용자가 명시한 값을 조용히 뒤집는 것보다 이유를 말하고 그대로 두는 편이 정직하고, 증상이 "그냥 느림"이라 경고가 없으면 원인을 찾기 어렵다.

### 5. 공유 가변 상태를 제거한다 (잠그지 않는다)

현행 루프는 두 사전에 동시 쓰기를 하게 된다 — 호출부가 `sections`에, 헬퍼 내부가 `floorViolations`에 쓴다. `ConcurrentDictionary`나 `lock`으로 막을 수도 있지만, 공유 자체를 없애는 편이 낫다. 헬퍼가 위반 기록을 바깥 사전에 쓰는 대신 **돌려주게** 한다.

```csharp
private async Task<(string Markdown, string? FloorViolation)> GenerateStepSectionWithFloorRetryAsync(
    BatchStepPlan step,
    IReadOnlyList<BatchStepPlan> steps,
    string conventions,
    List<(string FileName, string Content)> specs,
    string targetLanguage,
    string jobName,
    CancellationToken cancellationToken)     // floorViolations 매개변수 제거
```

각 단계 태스크가 공유 컬렉션을 전혀 만지지 않게 되고, `Task.WhenAll` 이후 단일 스레드에서 병합한다.

```csharp
var results = await Task.WhenAll(tasks);
foreach (var r in results)                 // 단일 스레드, 목차 순서
{
    sections[r.Code] = r.Markdown;
    if (r.FloorViolation != null) floorViolations[r.Code] = r.FloorViolation;
}
```

**잠금을 아끼려는 것이 아니다.** 잠금은 "동시에 써도 깨지지 않는다"만 보장하고 순서는 보장하지 않는다. 병합을 단일 스레드로 빼면 `floorViolations`의 내용이 완료 순서와 무관하게 결정적이 된다 — 같은 입력에 같은 배너가 나온다. 선행 브랜치에서 세 번 물린 결함이 전부 "어느 문서에 어떤 기록이 붙는가"였으므로, 비결정성을 새로 들이지 않는다.

부수 효과로 헬퍼의 매개변수가 하나 줄고 반환값이 자기 결과를 온전히 서술하게 된다.

### 6. 동시성 제어

```csharp
private sealed record StepResult(string Code, string Markdown, string? FloorViolation);

using var gate = new SemaphoreSlim(_stepConcurrency);

async Task<StepResult> RunAsync(BatchStepPlan step, int index)
{
    await gate.WaitAsync(cancellationToken);
    try
    {
        var taskKey = $"step_{step.Code}";
        progressScope.AddTask(taskKey, $"3/3. 단계 본문 생성 중 ({step.Code} · {index + 1}/{pending.Count})...");
        var (markdown, violation) = await GenerateStepSectionWithFloorRetryAsync(...);
        progressScope.CompleteTask(taskKey);
        return new StepResult(step.Code, markdown, violation);
    }
    finally { gate.Release(); }
}

// 워밍: 첫 단계를 await로 끝까지 기다린 뒤에야 나머지를 띄운다.
// 세마포어가 아니라 이 await가 워밍을 보장한다 — 슬롯이 여러 개여도
// 두 번째 호출은 여기서 시작조차 하지 않는다.
var results = new List<StepResult>(pending.Count);
if (pending.Count > 0)
{
    results.Add(await RunAsync(pending[0], 0));
}
if (pending.Count > 1)
{
    var rest = pending.Skip(1).Select((step, i) => RunAsync(step, i + 1));
    results.AddRange(await Task.WhenAll(rest));
}
```

경계 조건은 셋 다 자연히 처리된다. `pending`이 비면(지목 코드가 하나도 목록에 없는 경우) 아무 호출도 하지 않고, 1개면 워밍이 곧 전부이며, `_stepConcurrency == 1`이면 세마포어 슬롯이 하나라 팬아웃이 사실상 순차가 된다.

두 가지가 중요하다.

- **슬롯은 단계당 재시도 2회를 모두 감싼 채 유지한다.** 재시도 사이에 슬롯을 놓으면 다른 단계가 끼어들어 동시 요청 수가 설정값을 넘는다.
- **진행률 행은 슬롯을 잡은 뒤에 추가한다.** 먼저 추가하면 대기 중인 13개가 전부 "생성 중"으로 떠서, 실제로는 4개만 돌고 있다는 사실이 화면에서 사라진다.

`ConsoleProgressScope`는 이미 `ConcurrentDictionary` 3개와 `lock`으로 보호돼 있어 동시 `AddTask`/`CompleteTask`가 안전하다. 새로 손댈 것이 없다.

### 7. 취소

`SemaphoreSlim.WaitAsync(cancellationToken)`가 대기 중인 단계를 즉시 깨운다. 진행 중인 요청은 `HttpClient`가 토큰을 들고 있으므로 각자 끊긴다. `Task.WhenAll`이 첫 `OperationCanceledException`을 올리고, 오케스트레이터의 기존 `when (ex is not OperationCanceledException)` 필터가 그대로 통과시킨다.

단계 실패는 여전히 예외를 던지지 않는다. 헬퍼가 취소 외 예외를 삼켜 경고 마커를 돌려주므로 `Task.WhenAll`이 던지는 경우는 취소뿐이다. 즉 **한 단계의 실패가 나머지를 죽이지 않는다**는 현행 성질이 병렬에서도 유지된다.

### 8. 실패 처리 — 종전과 달라지지 않는다

| 상황 | 동작 |
|---|---|
| 한 단계 생성 실패 | 그 단계만 경고 마커. 나머지는 계속 |
| 한 단계 하한 미달 | 그 단계만 1회 재시도, 여전히 미달이면 채택 + 기록 |
| 여러 단계가 동시에 429 | 각자 재시도 1회를 소모하고 실패 마커. 배너에 전부 표기됨 |
| 취소 | `WhenAll`이 첫 `OperationCanceledException`을 올림 |
| `StepConcurrency = 1` | 세마포어 슬롯 1개 → 워밍과 팬아웃이 합쳐져 완전한 순차 |

429가 겹치는 것이 새로 생기는 위험이다. 새 처리를 넣지 않는 이유는 범위 밖 절에 적었다. 배너가 사실을 드러내므로 침묵하지는 않으며, 잦으면 `StepConcurrency`를 낮추는 것이 올바른 대응이다 — 그래서 이 값을 설정으로 뺐다.

## 테스트

**① 동시 실행 수 상한** — fake `IAiService`가 호출 진입에서 `Interlocked.Increment`, 이탈에서 `Decrement`하며 관측 최댓값을 기록한다. 각 호출에 짧은 지연을 주어 겹치게 하고, `StepConcurrency = 4`, 13단계로 돌려 관측 최댓값이 4 이하임을 단언한다. 세마포어를 제거하면 13을 관측하고 실패해야 한다.

**② 워밍이 실제로 단독인가** — fake가 각 호출의 시작·종료 시각을 기록한다. 첫 단계의 종료가 두 번째 단계의 시작보다 앞섬을 단언한다. 이것이 캐시 이점의 유일한 기계적 보증이다. 없으면 누군가 워밍을 "불필요한 직렬화"로 보고 지운다.

**③ 완료 순서가 결과를 바꾸지 않는가** — fake가 역순으로 완료하도록 지연을 준다(S13이 가장 먼저, S01이 가장 늦게). 조립된 문서의 섹션 순서가 여전히 목차 순서이고 배너의 위반 목록도 동일함을 단언한다.

**④ `StepConcurrency = 1`이 종전과 동일한가** — 94개 기존 테스트가 생성자 기본값 1로 한 줄도 바뀌지 않고 통과하는 것이 이 회귀 방어의 본체다. 그 위에, 동시성을 4로 켠 실행과 1로 켠 실행이 **같은 문서를 만드는지** 비교하는 테스트를 하나 더 둔다. 병렬화가 산출물을 바꾸지 않는다는 것이 이 설계의 전제이므로, 전제 자체를 단언한다.

**⑤ 취소가 삼켜지지 않는가** — 팬아웃 도중 토큰을 취소해 `OperationCanceledException`이 호출부까지 올라오는지 확인한다. `CancellationPolicyTests`가 새 `catch`의 필터를 자동 검사한다.

## 문서 동기화

- `docs/architecture.md` §3.1 Mermaid의 단계 생성 노드에 워밍 → 팬아웃 반영
- `docs/architecture.md` §4.4.5에 동시성과 캐시 워밍의 관계를 한 문단 추가
- `AGENTS.md`에 **워밍을 지우지 말 것** 규칙. 지웠을 때의 증상이 "비용만 조용히 오름"이라 코드만 봐서는 이유를 알 수 없다
- `README.md`의 `appsettings.json` 설정 레퍼런스에 `StepConcurrency` 추가

## 완료 기준

- `dotnet clean && dotnet build`에서 경고가 정확히 8건 (기존 `DbMetadataServiceTests`의 CS8600/CS8602)
- `dotnet test`가 기존 746건 + 신규분 전부 통과
- 위 문서 4종 동기화 완료
- 실측 회귀: 동일한 12개 SP로 `StepConcurrency = 4`로 재실행해 단계 구간 벽시계와 캐시 히트율을 기록하고, 산출물 품질(단계별 분량, 하한 미달 배너 유무)이 순차 실행과 동등함을 확인
