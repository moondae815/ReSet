# 채택본 응집과 재생성 신호 구조화 설계

작성일: 2026-08-05

[2026-08-05 구제 설계](./2026-08-05-retry-rescue-design.md)의 전체 브랜치 리뷰가 후속 과제 셋을 남겼다. 이 문서는 그 셋을 다룬다.

## 문제

### 1. `HasCandidate`가 죽은 채 남았다

`BestAttempt.HasCandidate`는 "후보가 있는가"를 표현하기 위해 정의됐는데 `src/` 어디에서도 참조되지 않는다. 유일한 소비자인 `RetryRescue.cs:30`이 그 정의를 인라인으로 재진술한다.

```csharp
if (best?.Review == null || best.Markdown == null)
```

프로퍼티를 그냥 호출하면 될 것 같지만 안 된다. `HasCandidate`는 프로퍼티라서, 확인 후에도 C# 흐름 분석이 `best.Review`의 non-null을 알지 못한다. 구현자가 인라인으로 쓴 이유가 그것이다.

같은 규칙이 두 곳에 사는 상태다. 이 저장소는 그 패턴으로 이미 세 번 사고를 냈다.

### 2. 배치의 `finalAiResult`가 채택본과 어긋난다

`VerificationPipelineOrchestrator.cs:1752`는 **생성이 성공할 때만** `finalAiResult`를 갱신하고, 구제 경로는 그것을 건드리지 않는다. 1차가 88점, 2차가 생성은 성공했으나 64점, 3차가 예외로 죽으면 채택본은 1차인데 `finalAiResult`는 2차의 것이다.

`Program.cs:833`·`:1317`이 그 값으로 `Thinking.md`와 `raw/prompt-context.md`를 쓴다. 점수·상태·배너는 정확하므로 **틀린 메타데이터가 아니라 어긋난 디버그 산출물**이다. 그래도 "이 문서는 어떻게 만들어졌나"를 보려고 여는 파일이 다른 시도를 가리킨다.

순차 SP 경로에는 이 문제가 없다. `accumulatedThinking`이 **모든 시도**의 추론을 누적해 기록하므로 채택본 하나를 가리킬 개념 자체가 없다.

### 3. 점수 줄이 재생성 휴리스틱을 무력화했다

지역 모델 경로(`:892-922`)는 `feedbackLog`에 키워드 매칭을 걸어 어느 섹션을 다시 생성할지 정한다. `regenPart2`의 조건에 `logUpper.Contains("CRUD")`가 있다.

그런데 `CriticFeedbackLog.Record`가 넣는 점수 줄은 항상 `정합성 … CRUD … 인터페이스 … 가독성 … 예외`를 포함한다. 따라서 **누적 이력이 있는 모든 재시도 회차에서 `regenPart2`가 무조건 참**이다. 2026-08-04 누적 도입 시점부터 그랬다. 섹션 선택이 사실상 동작하지 않는다.

전체 리뷰는 이것을 "L1 실패 회차의 문제"로 보고했지만, 실제로는 **모든 L2 재시도 회차**에 해당한다.

근본 원인은 더 앞에 있다. **LLM이 쓴 산문에 키워드를 매칭하는 방식 자체가 조용히 깨진다.** 프롬프트 문구가 바뀌면 아무 신호 없이 오작동한다.

### 셋의 관계

1과 2는 같은 뿌리다. `BestAttempt`가 후보를 세 개의 독립 프로퍼티로 들고 있어서, "후보가 있는가"를 물으려면 무엇을 봐야 하는지 매번 정해야 하고(1), "채택된 시도"가 여러 변수에 흩어져 하나만 갱신될 수 있다(2).

3은 별개지만, 셋 다 **구조화된 값이 있는데 그것을 쓰지 않아 생긴 문제**라는 성격을 공유한다.

## 결정

| 사안 | 결정 | 근거 |
|---|---|---|
| 1과 2를 묶을까 | **묶는다.** `BestAttempt`가 후보를 단일 레코드로 보관 | 중복 규칙과 변수 어긋남이 같은 원인에서 나온다. 따로 고치면 근본 구조가 그대로 남는다 |
| 3의 깊이 | **구조화된 신호로 교체** | 최소 수정(점수 줄만 제거)은 산문 키워드 매칭 구조를 남겨 다음 문구 변경 때 다시 조용히 깨진다 |
| `Generation`의 nullable 여부 | **nullable.** 순차는 `null` | 두 루프가 실제로 다른 산출물을 만든다. 대칭성 위반이 아니라 실재하는 차이다 |
| L1 오류의 섹션 특정 | **하지 않는다.** 보수적으로 전체 재생성 | 메시지를 파싱하면 산문 추측을 없애자는 이번 변경의 취지와 자기모순이다 |

## 구성요소 A — 단일 후보 (문제 1·2)

```csharp
public sealed record AttemptCandidate(
    string Markdown, ReviewResult Review, int AttemptNumber, AiResult? Generation);

public sealed class BestAttempt
{
    public AttemptCandidate? Current { get; private set; }

    public bool TryRecord(int attemptNumber, string markdown, ReviewResult review, AiResult? generation);
}
```

기존 공개 프로퍼티 넷(`Markdown`, `Review`, `AttemptNumber`, `HasCandidate`)이 `Current` 하나로 대체된다. 갱신 규칙 — 엄격 부등호, 동점이면 먼저 나온 시도 유지 — 은 그대로다.

`RetryRescue`의 가드가 한 줄이 된다.

```csharp
var candidate = best?.Current;
if (candidate == null) return null;
```

흐름 분석이 통과하므로 재진술도 null-forgiving도 필요 없다. **`HasCandidate`가 죽은 채 남는 문제가 개념째 사라진다** — 삭제하는 것이 아니라, 물어볼 것이 하나뿐이라 별도 프로퍼티가 성립하지 않게 된다.

`RescuedAttempt`도 `Generation`을 싣는다.

```csharp
public sealed record RescuedAttempt(string Markdown, ReviewResult Review, int AttemptNumber, AiResult? Generation);
```

배치 루프의 구제 자리 넷은 채택 시 `finalAiResult = rescued.Generation ?? finalAiResult;`로 채택본의 것을 반영한다. 이것이 문제 2의 수정이다.

**`Generation`이 nullable인 이유.** 순차 루프는 `accumulatedThinking`에 모든 시도의 추론을 누적해 `Thinking.md`로 내보내므로 채택본 하나를 가리킬 필요가 없고, `aiResult`가 `TryRecord` 호출 지점(`:1128`)에서 스코프에 없기도 하다. 배치만 단일 `AiResult`를 스냅샷하므로 거기서만 어긋난다. 순차는 `null`을 넘긴다.

**두 `TryRecord` 호출부에 무엇을 넘기는가.** 순차(`:1128`)는 `null`. 배치(`:1843`)는 `finalAiResult` — 그 지점의 `aiResult`는 try 블록(`:1730`) 안에서 선언돼 스코프 밖이지만, `finalAiResult`(`:1704` 선언)가 `:1752`에서 같은 회차의 값으로 이미 갱신돼 있다.

## 구성요소 B — 재생성 신호 (문제 3)

```csharp
public sealed record RegenerationScope(bool RunStage1, bool Overview, bool Crud, bool Logic)
{
    public static readonly RegenerationScope Everything = new(true, true, true, true);
}

public static class RegenerationScopeSelector
{
    public static RegenerationScope FromReview(ReviewResult review, int scoreThreshold);
    public static RegenerationScope FromL1Errors(IReadOnlyList<DetailedError> errors);
}
```

**점수 → 섹션.** Critic의 다섯 항목이 세 섹션에 대응한다.

| 기준 미달 항목 | Stage 1 재실행 | part1 개요·파라미터 | part2 CRUD | part3 로직·시각화 |
|---|:-:|:-:|:-:|:-:|
| 정합성 | ✓ | | | ✓ |
| CRUD | ✓ | | ✓ | |
| 인터페이스 | | ✓ | | |
| 가독성 | | | | ✓ |
| 예외 | | | | ✓ |

Stage 1(구조화 데이터 재도출)은 정합성·CRUD가 미달일 때만 돈다. 나머지 셋은 표현의 문제라 이미 뽑아 둔 구조를 다시 쓰면 된다. 이것이 원래 코드가 주석으로 밝힌 의도(`단순 포맷/Mermaid 교정이므로 1단계(추론) 스킵`)이며, 이제 산문 추측이 아니라 점수로 결정된다.

**L1 오류 → 섹션.** `MechanicalValidator`가 이미 `DetailedError.Type`으로 분류해 둔 값을 쓴다.

- 전부 `MermaidQuoteMissing`/`MermaidCliError` → part3만
- `HeaderMissing`이나 `General`이 하나라도 있으면 → 세 섹션 모두
- **L1 회차는 `RunStage1`이 항상 false** — 기계 검증은 형식 문제라 구조화 데이터에 영향이 없다

**폴백 셋.**

- 결함은 있는데 미달 항목이 없으면 `Everything` (지역화할 근거가 없다)
- 계산 결과가 공집합이면 `Everything`
- 이전 섹션 산출물이 없으면 `Everything` — 이 조건은 호출부의 지역 상태(`ollamaPart1/2/3`)라 호출부에 남긴다

**배선.** `feedbackLog`가 설정되는 바로 그 두 자리에서 `RegenerationScope`도 함께 정한다.

| 자리 | 신호 |
|---|---|
| L2 결함 재시도 (`:1140`, `feedbackLog = CriticFeedbackLog.Compose(`) | `FromReview(l2Result, _criticScoreThreshold)` |
| L1 실패 재시도 (`:1029`, `feedbackLog = CriticFeedbackLog.ComposeAfterL1Failure(`) | `FromL1Errors(l1Result.DetailedErrors)` |
| 1차 시도 | `null` → `Everything` |

배치 쌍둥이(`:1854`, `:1791`)는 지역 모델 블록이 없어 배선하지 않는다.

지역 모델 블록은 키워드 매칭 대신 이 값을 읽는다. 키워드 매칭 약 35줄이 사라지고, 구제 수정 웨이브에서 `:810`에 붙인 오염 경고 주석도 함께 지운다 — 그 주석이 경고하던 상황 자체가 없어지기 때문이다.

**영향 범위는 순차 SP 경로의 지역 모델(`ollama`/`local-openai`/`mlx`/`vllm`)뿐이다.** 배치는 청킹 경로가 없고, API·CLI 제공자는 이 블록에 진입하지 않는다.

## 테스트 전략

`BestAttempt`의 공개 표면이 바뀌므로 기존 테스트 8건(`BestAttemptTests` 5, `RetryRescueTests` 3)을 새 형태로 다시 쓴다. **검증하는 동작은 그대로 유지한다** — 특히 동점 규칙은 이번 변경으로 흔들리면 안 된다.

| 대상 | 항목 |
|---|---|
| `BestAttemptTests` | 갱신 규칙(엄격 부등호·동점 유지) 보존 / `Current`가 네 값을 한 덩어리로 보관 / `Generation`이 null이어도 기록 |
| `RetryRescueTests` | 가드가 후보 없음을 정확히 걸러냄 / `Generation`이 `RescuedAttempt`까지 전달 |
| `RegenerationScopeSelectorTests` (신규) | 항목별 미달 5종 각각의 플래그 / 복수 미달의 합집합 / 미달 없는데 결함 있음 → `Everything` / L1 Mermaid만 → part3 + Stage1 off / L1에 `HeaderMissing` 섞임 → 전체 + Stage1 off / 빈 목록 → `Everything` |
| 기존 구제 테스트 전부 | `BestAttempt` 형태 변경의 안전망. 그대로 통과해야 한다 |
| `VerificationPipelineOrchestratorTests` (신규) | **문제 2의 결정적 증거** — 1차 88점, 2차 생성 성공하나 64점, 3차 예외. 채택본이 1차이고 `ConsolidatedPipelineResult.AiResult`도 1차의 것이어야 한다. 각 `AiResult`에 고유 `ThinkingText`를 심어 구별한다 |

마지막 항목이 핵심이다. 현재 코드라면 2차의 `AiResult`가 나가며, 그것이 정확히 `Thinking.md`가 채택본과 어긋나는 시나리오다.

## 경계 조건

| 상황 | 동작 |
|---|---|
| 순차 루프의 `Generation` | 항상 `null`. 소비자가 없다 — `accumulatedThinking`이 모든 시도를 이미 기록한다 |
| 하이브리드 (`ActorEffort: dynamic`) | 범위 밖. `BestAttempt`를 쓰지 않는다 |
| L3 인간 승인 루프 | 범위 밖. 재시도 루프가 아니다 |
| 배치 루프의 재생성 범위 | 해당 없음. 청킹 경로가 순차 SP 전용이다 |
| 캐시 · 사용자 취소 | 변화 없음 |
| `BestAttempt` 공개 표면 변경 | 응용 프로그램 내부 전용. 소비자는 오케스트레이터·`RetryRescue`·테스트뿐이다 |

## 검증의 한계

`RegenerationScopeSelector` 자체는 결정적이라 단위 테스트로 잠긴다. 다만 **그것이 실제 지역 모델 실행의 재생성 동작을 개선하는지는 검증되지 않는다** — ollama 등을 돌리지 않는 한 확인할 수 없다. 이번에도 실증 재실행을 계획에 넣지 않되, 이 한계를 남긴다.

구성요소 A는 전부 결정적이며 기존 구제 테스트가 회귀 안전망 역할을 한다.

## 범위 밖

- **`HeaderMissing` 메시지 파싱으로 섹션 특정** — 산문 추측을 없애자는 취지와 자기모순이다
- **시도 간 진동 억제** — Actor가 매번 백지에서 재작성해 점수가 출렁이는 문제. `IAiService` 인터페이스와 프롬프트를 함께 바꿔야 하는 별건이다
- **합격 기준 정책** — 다섯 항목 전부가 기준을 넘어야 하는 현행 게이트를 유지한다
- **순차 경로의 `Generation` 활용** — 소비자가 없으므로 만들지 않는다
