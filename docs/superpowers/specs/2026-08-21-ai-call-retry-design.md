# AI 호출 재시도 — 일시적 실패 한 번에 검증을 포기하지 않는다

작성일: 2026-08-21 · 상태: 설계 승인 대기

## 1. 문제

`docs/known-defects.md`의 P0 ③이다. **5개 설계가 반복 기록한 최다 이월 항목**이며,
이 문서가 그 결정을 처음으로 내린다.

두 검증 루프의 L2 리뷰 호출이 일시적 API 오류 **한 번**에 포기한다.

```csharp
try
{
    l2Result = await WrapWithProgress(_criticService.ReviewSpecificationAsync(...), scope, "review");
    reviewSuccess = true;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    reviewFailureReason = ex.Message;
}
// ...
if (!reviewSuccess) { /* RetryRescue로 구제하거나 ReviewNotRun 배너를 붙이고 break */ }
```

`_maxAttempts`가 남아 있어도 재시도하지 않는다. `RetryRescue`가 이전 회차 최고점을
구제해 완화할 뿐, **그 회차의 검증 자체는 포기된다.**

원본: `2026-08-01-verification-outcome-honesty`, `2026-08-03-cancellation-policy`,
`2026-08-03-stage1-analysis-flow-hardening`, `2026-08-03-verification-annotation-cleanup`,
`2026-08-03-verification-honesty-followups`

## 2. 진단 — 인프라가 없는 게 아니라 한 자리에만 있다

조사에서 네 가지가 드러났고, 그중 둘은 P0 ③의 서술을 바꾼다.

### 2.1 선례가 이미 있다

`VerificationPipelineOrchestrator.GenerateStepSectionWithFloorRetryAsync`가
**작동하는 재시도를 이미 갖고 있다.**

| 요소 | 값 | 그 코드의 근거(주석 원문 요지) |
|---|---|---|
| 시도 수 | `maxTries = 2` | "단계당 1회로 하드 캡해 폭주를 막는다" |
| 예산 | `MaxL2Attempts` 미소모 | "그 예산은 Actor-Critic 문서 레벨의 것이고, 이 보수는 리뷰 호출이 0인 국소 작업이라 성격이 다르다" |
| 지연 | `Random.Shared.Next(500, 1500)` ms, **예외로 끝났을 때만** | "동시 실행 중에는 429가 여러 단계를 같은 창에서 때린다. 무지연으로 재시도하면 네 단계가 두 번의 시도를 모두 그 창 안에 쏟아붓고 함께 강등된다" |
| 취소 | `catch (Exception ex) when (ex is not OperationCanceledException)` | "취소를 삼키면 실패로 위장한 정상 반환이 되어 취소 사실이 사라진다" |
| 실패 시 | 죽지 않고 `StepDefect`로 강등 기록 | "여기서 문서 L1을 실패시키면 같은 결함으로 골격+단계 전체 재생성을 유발해 비용만 태운다" |

**따라서 이 설계는 정책을 발명하지 않는다.** 위 값을 그대로 일반화한다.

### 2.2 실패의 종류가 예외에 남지 않는다

API 클라이언트 6곳(`OpenAiClient` ×2 · `ClaudeClient` · `GoogleClient` · `OllamaClient` ·
`ZaiClient`)이 전부 이렇게 던진다.

```csharp
throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ...");
```

상태 코드가 **문자열 안에만** 있다. `HttpRequestException.StatusCode`(.NET 5+)는 `null`이다.

CLI 경로도 같다. `CliFailureClassifier.BuildException`이 `CliFailureKind`를 계산해
문구로 녹인 뒤 **평범한 `InvalidOperationException`을 돌려주고 kind를 버린다**(`.Data`에도 싣지 않는다).

즉 오늘은 "429였는가"도 "쿼터가 소진됐는가"도 예외에서 알 수 없다.

### 2.3 타임아웃이 "사용자 취소"로 보고된다 — 실측

.NET 10.0.10에서 직접 측정했다(일회용 프로브, 보관하지 않음).

| | 예외 형식 | `is OperationCanceledException` | `InnerException` | 토큰 취소됨 |
|---|---|---|---|---|
| **HttpClient 타임아웃** | `TaskCanceledException` | **True** | `TimeoutException` | **False** |
| **사용자 Ctrl-C** | `TaskCanceledException` | True | `TaskCanceledException` | True |

이 코드베이스에는 `when (ex is not OperationCanceledException)` 필터가 **55곳** 있고,
`ReSet.Cli/Program.cs`의 최상위 `catch (OperationCanceledException)` 처리는 *"사용자에 의해 배치 분석 작업이 중단되었습니다"* 를 찍는다.

`AiSettings:TimeoutSeconds`는 `3600`이다. **한 시간 멈춰 있던 호출이 "사용자가 중단했다"로
보고되고 배치 전체가 끝난다.** P0 ③이 적지 않은 사실이며, 재시도 설계의 제약이 된다 —
재시도 헬퍼가 이 예외를 잡으려 들면 진짜 취소도 함께 삼키게 된다.

구분 기준은 위 표의 마지막 두 칸이다. 둘 중 **토큰 취소 여부**를 쓴다. `InnerException`
검사는 런타임 구현 세부에 기대지만 토큰은 우리가 넘긴 것이라 계약이 명확하다.

### 2.4 호출 32곳 중 리뷰는 5곳

| 종류 | 호출 수 |
|---|---|
| 생성 (`Generate*` · `Draft*` · `Brainstorm*` · `Deconstruct*`) | 27 |
| L2 리뷰 (`Review*`) | 5 |

생성 쪽 재시도 부재는 `known-defects.md`에 **별건**으로 등재돼 있다. 두 항목이 따로 적혀
있지만 **필요한 인프라는 하나**다.

## 3. 결정

| 항목 | 결정 | 이유 |
|---|---|---|
| **범위** | 인프라는 공용, 이번 적용은 **리뷰 5곳만** | P0의 범위를 지키면서 생성 27곳을 같은 헬퍼로 나중에 감을 수 있다. 검증 표면이 작아 한 번에 끝난다 |
| **판정 근거** | 예외에 유형을 **싣는다** | 문자열 매칭은 `RegenerationScopeSelector`가 이미 폐기한 방식이다(그 클래스 주석: "산문에 키워드를 거는 방식이라 문구가 바뀌면 아무 신호 없이 오작동한다") |
| **횟수·지연** | `maxTries = 2`, 지터 500~1500ms | §2.1 선례를 그대로 따른다. 새 설정 키를 만들지 않는다 |
| **예산** | `MaxL2Attempts` 미소모 | 같은 선례의 근거가 그대로 적용된다 |

`AiSettings`에 새 키를 추가하지 **않는다.** 선례가 의도적으로 상수를 택했고
("단계당 1회로 하드 캡해 폭주를 막는다"), 설정으로 열면 그 보장이 사라진다.

## 4. 설계 — 구성 요소

신규 4개 · 기존 수정 7곳(API 클라이언트 6 + CLI 분류기 1). 여기에 §5의 호출부 5곳이 더해진다.

| 단위 | 하는 일 | 의존 |
|---|---|---|
| `AiRetryPolicy` (신규, 정적) | 예외 하나를 `Transient` / `Fatal` / `Cancelled`로 판정. **순수 함수** | 없음 |
| `AiCallRetry` (신규, 정적) | `Func<Task<T>>`를 받아 계획대로 재시도. 전체를 하나의 `Task<T>`로 반환. 계획 값 `RetryPlan`을 같은 파일에 둔다 | `AiRetryPolicy` |
| `CliInvocationException` (신규) | `InvalidOperationException` 하위형. `CliFailureKind Kind`를 속성으로 보존 | 없음 |
| `AiCallFailedException` (신규) | 재시도 소진을 알리는 예외. **`OperationCanceledException`을 상속하지 않는다.** 마지막 시도의 예외를 `InnerException`으로, 시도 횟수를 `Attempts`로 싣는다 | 없음 |
| API 클라이언트 6곳 (수정) | `new HttpRequestException(msg, null, response.StatusCode)` — **한 줄씩** | — |
| `CliFailureClassifier.BuildException` (수정) | 반환을 `CliInvocationException`으로. 하위형이라 **기존 호출부 무변경** | — |

### 4.1 판정 규칙

```
OperationCanceledException + 토큰 취소됨                  → Cancelled
OperationCanceledException + 토큰 미취소                  → Transient   (HttpClient 타임아웃)
HttpRequestException, StatusCode ∈ {429,500,502,503,504}  → Transient
HttpRequestException, StatusCode == null                  → Transient   (연결 거부 등, 로컬 Ollama)
HttpRequestException, 그 밖의 4xx                         → Fatal
CliInvocationException, Kind == Timeout                   → Transient
CliInvocationException, Kind ∈ {QuotaExhausted,
                                NotAuthenticated,
                                ToolPermissionDenied}     → Fatal
그 밖 (파싱 실패·에러 응답 등 InvalidOperationException)  → Fatal
```

시그니처는 `Classify(Exception ex, CancellationToken ct)`다. 토큰 취소 여부가 판정에
들어가므로 예외만으로는 정할 수 없다.

### 4.2 재시도 계획을 값으로 만든다

```csharp
public readonly record struct RetryPlan(int MaxTries, TimeSpan MinDelay, TimeSpan MaxDelay)
{
    public static readonly RetryPlan Default = new(2, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500));
    public static readonly RetryPlan NoDelay = new(2, TimeSpan.Zero, TimeSpan.Zero);
}

AiCallRetry.ExecuteAsync(factory, cancellationToken, RetryPlan? plan = null)   // null = Default
```

이 매개변수는 테스트 훅이 아니다. §3의 "인프라는 공용" 결정에 따라 생성 호출이 나중에
다른 계획으로 같은 헬퍼를 쓴다. 테스트가 `NoDelay`를 쓰는 것은 그 부수 효과다.

### 4.3 소진 시 무엇을 던지는가 — 이 설계의 핵심 이음매

타임아웃으로 재시도를 다 쓴 뒤 `TaskCanceledException`을 **그대로 재던지면**, 호출부의
`when (ex is not OperationCanceledException)` 필터가 또 놓치고 §2.3의 오보가 그대로 재현된다.

그래서 `AiCallRetry`는 소진 시 **`AiCallFailedException`** 으로 감싸 던진다. 이 형식은
`Exception`을 직접 상속하며 `OperationCanceledException`이 **아니다.** 마지막 시도의 예외가
`InnerException`에 남으므로 진단 정보를 잃지 않는다.

그러면 호출부의 `catch (Exception ex) when (ex is not OperationCanceledException)`가 정상적으로
잡아 `reviewFailureReason = ex.Message`를 기록하고, 기존 소프트 페일 경로가 그대로 작동한다.
**55곳의 필터를 손대지 않아도 된다.**

`Cancelled` 판정일 때만 원 예외를 그대로 재던진다. 진짜 Ctrl-C는 재시도하지 않는다.

## 5. 설계 — 호출부 적용

리뷰 5곳 전부 **한 줄 감싸기**다.

```csharp
// 전
l2Result = await WrapWithProgress(
    _criticService.ReviewSpecificationAsync(spDef, markdown, _criticEffort, cancellationToken),
    progressScope, "review");

// 후
l2Result = await WrapWithProgress(
    AiCallRetry.ExecuteAsync(
        () => _criticService.ReviewSpecificationAsync(spDef, markdown, _criticEffort, cancellationToken),
        cancellationToken),
    progressScope, "review");
```

`ExecuteAsync`가 재시도 전체를 하나의 `Task<T>`로 돌려주므로 **`WrapWithProgress`의
시그니처를 바꾸지 않는다.** 그 헬퍼는 이미 시작된 `Task<T>`를 받아 스스로는 재시도할 수
없지만, 재시도를 그 아래에 합성하면 문제가 사라진다. 진행 표시는 모든 시도가 소진된
뒤에만 실패로 찍힌다.

앵커는 `타입.멤버` + 그 자리의 식별자로 적는다(`known-defects.md`의 앵커 규약). 다섯 중
넷이 `RunCodeObjectPipelineCoreAsync` 한 멤버 안에 있으므로 진행 스코프의 taskKey 리터럴로
자리를 좁힌다 — 그 문자열은 각 호출 바로 옆에 실제로 있다.

| 멤버 | 자리 (taskKey) | 맥락 | 재시도가 바꾸는 것 |
|---|---|---|---|
| `RunCodeObjectPipelineCoreAsync` | `"review"` | 단일 객체 루프 | **P0 ③ 본체.** `reviewSuccess`가 2회 소진 뒤에만 false |
| `RunConsolidatedPipelineAsync` | `"batchreview"` | 통합 계획서 루프 | **P0 ③ 본체.** 위와 같음 |
| `RunCodeObjectPipelineCoreAsync` | `"final_review"` | 합성본 최종 검토 | 실패 도달 빈도만 낮아짐(사유는 이미 기록됨) |
| `RunCodeObjectPipelineCoreAsync` | `"refinal"` | 보완본 재검토 | 아래 참조 |
| `RunCodeObjectPipelineCoreAsync` | `reviewTasks` 조립부 | 3후보 **병렬** 검토 | 후보별 각자 재시도. `Task.WhenAll` 구조는 그대로 |

`"refinal"` 자리는 **빈 catch**다 — `catch (Exception ex) when (ex is not OperationCanceledException) { }`.
재검토가 실패해도 아무도 모른다. 재시도를 넣어도 이 침묵은 남으므로 **로그 한 줄을 추가한다**
(동작은 바꾸지 않는다).

### 5.1 비용

최악의 경우 리뷰 호출 비용이 **2배**다(일시적 오류가 계속될 때). `Fatal` 판정이 쿼터 소진·
인증 실패·4xx를 즉시 걸러내므로, 돈이 새는 대표 경로에서는 재시도가 일어나지 않는다.

## 6. 검증

### 6.1 시간 의존 제거

테스트는 `RetryPlan.NoDelay`를 넘긴다. 실제 대기 없이 횟수와 분기만 본다.

### 6.2 층별 검사

**`AiRetryPolicy` — 표 기반 순수 함수**

§4.1의 여덟 줄이 그대로 테스트가 된다. §2.3에서 실측한 두 줄
(`TaskCanceledException` + 토큰 미취소 → `Transient`, 토큰 취소 → `Cancelled`)이 포함된다.

**`AiCallRetry` — 호출 횟수와 취소 전파**

- 1회차 `Transient` → 2회차 성공 ⇒ 팩토리 **2회**, 결과 반환
- 매번 `Transient` ⇒ 팩토리 **2회**에서 멈춤, **`OperationCanceledException`이 아닌** 예외를 던짐
- 1회차 `Fatal` ⇒ 팩토리 **1회**, 원 예외 재던짐
- 토큰 취소 ⇒ 팩토리 1회, `OperationCanceledException` 그대로 재던짐

**예외에 유형이 실리는가**

- `CliFailureClassifier.ToException(...)` 반환이 `CliInvocationException`이고 `Kind`가 분류 결과와 같다
- `OpenAiClient` — 기존 `OpenAiRequestSpyHandler(..., HttpStatusCode.TooManyRequests)`를 재사용해
  던져진 `HttpRequestException.StatusCode == 429`

**오케스트레이터 행동**

- 일시적 실패 후 성공 ⇒ 파이프라인 통과, 리뷰 메서드 `Received(2)`
- **타임아웃 오보 회귀** ⇒ `TaskCanceledException`(토큰 미취소)이 계속 와도 파이프라인이
  예외를 밖으로 던지지 않고 `ReviewNotRun` 배너로 끝난다
- `Fatal` ⇒ `Received(1)`, 소프트 페일

### 6.3 RED 예상

| 테스트 | 오늘 |
|---|---|
| `AiRetryPolicy` · `AiCallRetry` 전부 | 컴파일 실패 (형식 없음) |
| `CliInvocationException.Kind` | RED (평범한 `InvalidOperationException`) |
| `HttpRequestException.StatusCode == 429` | RED (`null`) |
| 일시적 실패 후 성공 → `Received(2)` | RED (1회 후 포기) |
| 타임아웃 오보 회귀 | RED (`OperationCanceledException`이 밖으로 샘) |
| `Fatal` → `Received(1)` | **오늘도 통과** — 과잉 재시도 방지 가드. 모든 예외를 `Transient`로 바꾸는 뮤테이션으로 실효를 확인한다 |

## 7. 범위 밖

이 설계가 **닫지 않는** 것들이다. 닫은 척하면 다음 사람이 속는다.

- **생성 호출 27곳.** 인프라는 쓸 수 있게 두되 이번엔 감지 않는다.
  `known-defects.md`의 "생성 호출 실패 재시도 0회"는 열린 채로 둔다.
- **`Task.WhenAll`이 첫 예외만 표면화한다** (`RunCodeObjectPipelineCoreAsync`의 `Task.WhenAll(reviewTasks)`). `known-defects.md`에 별건으로 있다.
  재시도는 일시적 오류가 `WhenAll`까지 도달할 확률을 낮출 뿐 그 결함을 고치지 않는다.
- **55곳의 `is not OperationCanceledException` 필터.** §4.3의 이음매가 리뷰 경로에서만
  오보를 막는다. 다른 경로의 타임아웃은 여전히 "사용자 취소"로 보고된다.
- **`AiSettings:TimeoutSeconds: 3600` 값 자체.** 한 시간이 옳은지는 실측이 필요한 문제다.
- **CLI 프로바이더의 동시 실행 제어.** `known-defects.md`의 `ActorEffort: dynamic` 항목이다.

## 8. 위험

| 위험 | 완화 |
|---|---|
| `Transient` 판정이 너무 넓어 4xx 입력 오류를 돈 내고 반복한다 | 4xx는 429만 `Transient`. 나머지는 `Fatal`. 뮤테이션 테스트로 고정 |
| `StatusCode == null`을 `Transient`로 두어 예상 밖 예외가 재시도된다 | `HttpRequestException`에 한정되고 `maxTries = 2`라 상한이 1회 추가다 |
| 토큰 취소 판정이 경합한다 — 취소와 타임아웃이 거의 동시에 오면 | 취소로 판정된다(안전한 방향). 취소를 삼키지 않는 것이 이 코드베이스의 일관된 규약이다 |
| `CliInvocationException` 도입이 기존 `catch (InvalidOperationException)`을 깬다 | 하위형이므로 기존 catch가 그대로 잡는다. 반환 형식만 좁아진다 |
