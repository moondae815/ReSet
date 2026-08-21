# AI 호출 재시도 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** L2 리뷰 호출이 일시적 API 오류 한 번에 검증을 포기하지 않게 한다.

**Architecture:** 예외에 실패 유형을 실어(`HttpRequestException.StatusCode`, `CliFailureKind`)
순수 함수 `AiRetryPolicy`가 `Transient / Fatal / Cancelled`로 판정하고, `AiCallRetry`가
`Func<Task<T>>`를 최대 2회 시도한다. 재시도 전체가 하나의 `Task<T>`로 반환되므로 기존
`WrapWithProgress`와 소프트 페일 분기를 손대지 않는다. 소진 시에는
`OperationCanceledException`이 **아닌** `AiCallFailedException`을 던져, HttpClient 타임아웃이
"사용자 취소"로 오보되던 경로를 막는다.

**Tech Stack:** C# / .NET 10 · xUnit · NSubstitute

**Spec:** `docs/superpowers/specs/2026-08-21-ai-call-retry-design.md`

## Global Constraints

- 새 설정 키를 만들지 **않는다.** 재시도 계획은 코드 상수다 (`RetryPlan.Default`).
- 시도 수는 `2` (최초 1회 + 재시도 1회). 지연은 `500~1500ms` 균등 난수.
- `MaxL2Attempts` 예산을 소모하지 않는다.
- 취소(`CancellationToken`이 취소된 경우)는 **절대 삼키지 않고** 원 예외를 그대로 재던진다.
- 이번에 감는 호출은 **리뷰 5곳뿐이다.** 생성 27곳은 건드리지 않는다.
- 문서 앵커는 `타입.멤버` + 그 자리의 식별자로 적는다. 줄 번호를 쓰지 않는다.
- 빌드 경고 기준선은 **8개**다 (`tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`의 CS8600/CS8602).
  이 수를 늘리면 안 된다.
- 전체 테스트는 작업 시작 시점에 **2058건 통과**다. 줄어들면 회귀다.

## 파일 구조

| 파일 | 책임 |
|---|---|
| `src/ReSet.Core/Services/AiRetryPolicy.cs` (신규) | 예외 → `AiRetryVerdict` 판정. 순수 함수. I/O 없음 |
| `src/ReSet.Core/Services/AiCallRetry.cs` (신규) | `RetryPlan` 값 + `ExecuteAsync` 재시도 루프 + `AiCallFailedException` |
| `src/ReSet.Core/Services/Clients/Cli/CliInvocationException.cs` (신규) | `CliFailureKind`를 보존하는 `InvalidOperationException` 하위형 |
| `src/ReSet.Core/Services/Clients/Cli/CliFailureClassifier.cs` (수정) | `BuildException` 반환형을 하위형으로 |
| `src/ReSet.Core/Services/Clients/*.cs` (수정 6곳) | `HttpRequestException`에 `StatusCode` 전달 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (수정) | 리뷰 5곳 감싸기 + `"refinal"` 빈 catch에 로그 |

`AiRetryPolicy`와 `AiCallRetry`를 굳이 두 파일로 나눈 이유: 판정은 순수 함수라 예외 객체만으로
테스트되고, 재시도 루프는 시간과 반복을 다룬다. 섞으면 판정 테스트가 지연에 얽힌다.

## 작업 순서의 근거

Task 1(판정)은 Task 2(루프)가 의존하고, Task 3·4(예외에 유형 싣기)가 없으면 Task 1의 판정이
실제 예외에서 재료를 못 얻는다. Task 5(호출부)는 앞의 넷이 다 있어야 의미가 있다.
**Task 3·4를 Task 1보다 먼저 하지 않는 이유**는 Task 1이 판정 *계약*을 정하고, 3·4가 그 계약이
요구하는 재료를 공급하기 때문이다. 계약 없이 재료부터 만들면 무엇을 실을지 알 수 없다.

---

### Task 1: 판정 함수 `AiRetryPolicy`

**Files:**
- Create: `src/ReSet.Core/Services/AiRetryPolicy.cs`
- Test: `tests/ReSet.Core.Tests/AiRetryPolicyTests.cs`

**Interfaces:**
- Consumes: 없음 (첫 작업)
- Produces:
  - `public enum AiRetryVerdict { Transient, Fatal, Cancelled }`
  - `public static AiRetryVerdict AiRetryPolicy.Classify(Exception ex, CancellationToken cancellationToken)`

이 작업 시점에는 `CliInvocationException`이 아직 없다. `Classify`는 그 형식을 **직접 참조하지
않고** Task 4에서 연결한다. 이번 작업의 CLI 관련 판정은 다루지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/AiRetryPolicyTests.cs`:

```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 재시도 가능 여부를 예외에 실린 유형으로 판정한다. 메시지 문자열을 보지 않는다 -
    /// 산문 매칭은 RegenerationScopeSelector가 이미 폐기한 방식이다.
    /// </summary>
    public class AiRetryPolicyTests
    {
        /// <summary>
        /// HttpClient 타임아웃의 실제 모양. .NET 10에서 측정한 결과 TaskCanceledException이고
        /// InnerException이 TimeoutException이며 우리가 넘긴 토큰은 취소되지 않은 상태다.
        /// </summary>
        private static Exception HttpClientTimeout() =>
            new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
                new TimeoutException());

        [Fact]
        public void Classify_HttpClientTimeoutWithLiveToken_IsTransient()
        {
            // 토큰이 취소되지 않았다 = 사용자가 멈춘 게 아니다 = 다시 해볼 만하다.
            Assert.Equal(
                AiRetryVerdict.Transient,
                AiRetryPolicy.Classify(HttpClientTimeout(), CancellationToken.None));
        }

        [Fact]
        public void Classify_CancellationRequested_IsCancelled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // 같은 예외 형식이라도 토큰이 취소됐으면 사용자의 의사다. 재시도하면 안 된다.
            Assert.Equal(
                AiRetryVerdict.Cancelled,
                AiRetryPolicy.Classify(new TaskCanceledException(), cts.Token));
        }

        [Theory]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.BadGateway)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        [InlineData(HttpStatusCode.GatewayTimeout)]
        public void Classify_TransientStatusCodes_AreTransient(HttpStatusCode code)
        {
            var ex = new HttpRequestException("boom", null, code);

            Assert.Equal(AiRetryVerdict.Transient, AiRetryPolicy.Classify(ex, CancellationToken.None));
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.NotFound)]
        public void Classify_ClientErrors_AreFatal(HttpStatusCode code)
        {
            // 입력이 틀린 것을 돈 내고 반복하지 않는다.
            var ex = new HttpRequestException("boom", null, code);

            Assert.Equal(AiRetryVerdict.Fatal, AiRetryPolicy.Classify(ex, CancellationToken.None));
        }

        [Fact]
        public void Classify_HttpRequestExceptionWithoutStatusCode_IsTransient()
        {
            // 연결 거부·DNS 실패 등. 로컬 Ollama가 아직 안 떴을 때 실제로 나온다.
            var ex = new HttpRequestException("connection refused");

            Assert.Null(ex.StatusCode);
            Assert.Equal(AiRetryVerdict.Transient, AiRetryPolicy.Classify(ex, CancellationToken.None));
        }

        [Fact]
        public void Classify_ParsingFailure_IsFatal()
        {
            // 응답 본문이 규약을 어긴 경우. 다시 불러도 같은 응답이 올 이유가 크다.
            var ex = new InvalidOperationException("응답 데이터 내에 choices 속성이 존재하지 않습니다.");

            Assert.Equal(AiRetryVerdict.Fatal, AiRetryPolicy.Classify(ex, CancellationToken.None));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet build tests/ReSet.Core.Tests -v q --nologo 2>&1 | grep "error CS"`

Expected: `error CS0246` 또는 `CS0103` — `AiRetryPolicy` / `AiRetryVerdict` 형식이 없다.

- [ ] **Step 3: 최소 구현**

`src/ReSet.Core/Services/AiRetryPolicy.cs`:

```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace ReSet.Core.Services
{
    /// <summary>재시도해도 되는 실패인가.</summary>
    public enum AiRetryVerdict
    {
        /// <summary>일시적이다. 다시 해볼 만하다.</summary>
        Transient,

        /// <summary>다시 해도 같은 결과다. 돈만 태운다.</summary>
        Fatal,

        /// <summary>사용자가 멈췄다. 삼키지 말고 그대로 올려보낸다.</summary>
        Cancelled
    }

    /// <summary>
    /// 예외에 실린 유형으로 재시도 가능 여부를 판정한다. 메시지 문자열을 보지 않는다 -
    /// 산문에 키워드를 거는 방식은 RegenerationScopeSelector가 이미 폐기했다.
    ///
    /// 순수 함수다. I/O도 시간도 다루지 않으므로 예외 객체만으로 전수 테스트된다.
    /// </summary>
    public static class AiRetryPolicy
    {
        public static AiRetryVerdict Classify(Exception ex, CancellationToken cancellationToken)
        {
            // 취소와 타임아웃은 둘 다 TaskCanceledException으로 온다(.NET 10에서 실측).
            // 구분은 우리가 넘긴 토큰이다 - InnerException 검사는 런타임 구현 세부에
            // 기대지만 토큰은 계약이 명확하다.
            //
            // 경합(취소와 타임아웃이 거의 동시)에서는 취소로 판정된다. 안전한 방향이다.
            if (ex is OperationCanceledException)
            {
                return cancellationToken.IsCancellationRequested
                    ? AiRetryVerdict.Cancelled
                    : AiRetryVerdict.Transient;
            }

            if (ex is HttpRequestException httpEx)
            {
                // 상태 코드가 없는 것은 응답 자체가 오지 않았다는 뜻이다
                // (연결 거부·DNS 실패). 그쪽은 다시 해볼 만하다.
                if (httpEx.StatusCode == null)
                {
                    return AiRetryVerdict.Transient;
                }

                return httpEx.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => AiRetryVerdict.Transient,
                    HttpStatusCode.InternalServerError => AiRetryVerdict.Transient,
                    HttpStatusCode.BadGateway => AiRetryVerdict.Transient,
                    HttpStatusCode.ServiceUnavailable => AiRetryVerdict.Transient,
                    HttpStatusCode.GatewayTimeout => AiRetryVerdict.Transient,
                    _ => AiRetryVerdict.Fatal
                };
            }

            // 파싱 실패·에러 응답 등. 같은 입력에 같은 응답이 올 이유가 크다.
            return AiRetryVerdict.Fatal;
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --nologo -v q --filter "FullyQualifiedName~AiRetryPolicyTests"`

Expected: `통과!  - 실패: 0, 통과: 13` (Fact 4개 + Theory InlineData 9개)

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/AiRetryPolicy.cs tests/ReSet.Core.Tests/AiRetryPolicyTests.cs
git commit -m "feat: 예외에 실린 유형으로 재시도 가능 여부를 판정하는 AiRetryPolicy"
```

---

### Task 2: 재시도 루프 `AiCallRetry`

**Files:**
- Create: `src/ReSet.Core/Services/AiCallRetry.cs`
- Test: `tests/ReSet.Core.Tests/AiCallRetryTests.cs`

**Interfaces:**
- Consumes: `AiRetryPolicy.Classify(Exception, CancellationToken)`, `AiRetryVerdict` (Task 1)
- Produces:
  - `public readonly record struct RetryPlan(int MaxTries, TimeSpan MinDelay, TimeSpan MaxDelay)`
    with `RetryPlan.Default` / `RetryPlan.NoDelay`
  - `public sealed class AiCallFailedException : Exception` with `int Attempts`
  - `public static Task<T> AiCallRetry.ExecuteAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken, RetryPlan? plan = null)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/AiCallRetryTests.cs`:

```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 재시도 루프. 모든 테스트가 RetryPlan.NoDelay를 넘겨 실제 대기 없이 돈다 -
    /// 여기서 보는 것은 호출 횟수와 분기이지 지연의 길이가 아니다.
    /// </summary>
    public class AiCallRetryTests
    {
        private static HttpRequestException Transient() =>
            new("서비스가 일시적으로 응답하지 않습니다", null, HttpStatusCode.ServiceUnavailable);

        private static HttpRequestException Fatal() =>
            new("인증 실패", null, HttpStatusCode.Unauthorized);

        [Fact]
        public async Task ExecuteAsync_TransientThenSuccess_CallsFactoryTwiceAndReturnsResult()
        {
            var calls = 0;

            var result = await AiCallRetry.ExecuteAsync(
                () =>
                {
                    calls++;
                    if (calls == 1) throw Transient();
                    return Task.FromResult("성공");
                },
                CancellationToken.None,
                RetryPlan.NoDelay);

            Assert.Equal("성공", result);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task ExecuteAsync_AlwaysTransient_StopsAtMaxTriesAndThrowsNonCancellation()
        {
            var calls = 0;

            var ex = await Assert.ThrowsAsync<AiCallFailedException>(() =>
                AiCallRetry.ExecuteAsync<string>(
                    () => { calls++; throw Transient(); },
                    CancellationToken.None,
                    RetryPlan.NoDelay));

            Assert.Equal(2, calls);
            Assert.Equal(2, ex.Attempts);

            // 이것이 이 설계의 핵심이다. OperationCanceledException으로 던지면 호출부의
            // when (ex is not OperationCanceledException) 필터가 또 놓쳐서 타임아웃이
            // "사용자 취소"로 둔갑한다.
            // is 검사를 쓴다. IsNotType은 정확한 형식만 보므로 하위형 상속을 못 잡는다.
            Assert.False(ex is OperationCanceledException);

            // 진단 정보를 잃지 않는다.
            Assert.IsType<HttpRequestException>(ex.InnerException);
        }

        [Fact]
        public async Task ExecuteAsync_HttpClientTimeoutExhausted_DoesNotSurfaceAsCancellation()
        {
            // 회귀 방지. TimeoutSeconds가 3600이므로 이 경로가 실제로 한 시간을 태운 뒤
            // "사용자에 의해 중단되었습니다"로 보고되던 자리다.
            var calls = 0;

            var ex = await Assert.ThrowsAsync<AiCallFailedException>(() =>
                AiCallRetry.ExecuteAsync<string>(
                    () =>
                    {
                        calls++;
                        throw new TaskCanceledException("HttpClient.Timeout", new TimeoutException());
                    },
                    CancellationToken.None,
                    RetryPlan.NoDelay));

            Assert.Equal(2, calls);
            Assert.False(ex is OperationCanceledException);
        }

        [Fact]
        public async Task ExecuteAsync_FatalOnFirstTry_DoesNotRetryAndRethrowsOriginal()
        {
            var calls = 0;

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
                AiCallRetry.ExecuteAsync<string>(
                    () => { calls++; throw Fatal(); },
                    CancellationToken.None,
                    RetryPlan.NoDelay));

            Assert.Equal(1, calls);
            Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        }

        [Fact]
        public async Task ExecuteAsync_CancellationRequested_RethrowsAndDoesNotRetry()
        {
            using var cts = new CancellationTokenSource();
            var calls = 0;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AiCallRetry.ExecuteAsync<string>(
                    () =>
                    {
                        calls++;
                        cts.Cancel();
                        throw new TaskCanceledException();
                    },
                    cts.Token,
                    RetryPlan.NoDelay));

            // 취소를 삼키면 실패로 위장한 정상 반환이 되어 취소 사실이 사라진다.
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task ExecuteAsync_Success_CallsFactoryOnce()
        {
            var calls = 0;

            var result = await AiCallRetry.ExecuteAsync(
                () => { calls++; return Task.FromResult(42); },
                CancellationToken.None,
                RetryPlan.NoDelay);

            Assert.Equal(42, result);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void DefaultPlan_MatchesTheEstablishedPrecedent()
        {
            // GenerateStepSectionWithFloorRetryAsync가 이미 쓰는 값이다.
            // 값을 바꾸려면 그 자리도 함께 봐야 한다.
            Assert.Equal(2, RetryPlan.Default.MaxTries);
            Assert.Equal(TimeSpan.FromMilliseconds(500), RetryPlan.Default.MinDelay);
            Assert.Equal(TimeSpan.FromMilliseconds(1500), RetryPlan.Default.MaxDelay);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet build tests/ReSet.Core.Tests -v q --nologo 2>&1 | grep "error CS"`

Expected: `AiCallRetry` · `RetryPlan` · `AiCallFailedException` 형식이 없다는 `CS0246`/`CS0103`.

- [ ] **Step 3: 최소 구현**

`src/ReSet.Core/Services/AiCallRetry.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 재시도 계획. 상수가 아니라 값인 이유는 생성 호출이 나중에 다른 계획으로 같은
    /// 헬퍼를 쓰기 때문이다(설계 §3의 "인프라는 공용" 결정).
    /// </summary>
    public readonly record struct RetryPlan(int MaxTries, TimeSpan MinDelay, TimeSpan MaxDelay)
    {
        /// <summary>
        /// GenerateStepSectionWithFloorRetryAsync가 이미 쓰는 값을 그대로 쓴다.
        /// 그 자리의 주석이 근거다 - "단계당 1회로 하드 캡해 폭주를 막는다",
        /// "무작위 지연이 상관된 폭풍을 흩트러진 재시도로 바꾼다".
        /// </summary>
        public static readonly RetryPlan Default =
            new(2, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500));

        /// <summary>테스트용. 횟수와 분기만 보고 실제로 기다리지 않는다.</summary>
        public static readonly RetryPlan NoDelay =
            new(2, TimeSpan.Zero, TimeSpan.Zero);
    }

    /// <summary>
    /// 재시도를 다 쓰고도 실패했다.
    ///
    /// OperationCanceledException을 상속하지 <b>않는다.</b> 이것이 이 설계의 핵심
    /// 이음매다 - 호출부 55곳이 when (ex is not OperationCanceledException)으로 거르므로,
    /// 타임아웃을 그 형식 그대로 올려보내면 또 놓치고 "사용자 취소"로 둔갑한다.
    /// </summary>
    public sealed class AiCallFailedException : Exception
    {
        public int Attempts { get; }

        public AiCallFailedException(string message, Exception inner, int attempts)
            : base(message, inner)
        {
            Attempts = attempts;
        }
    }

    /// <summary>
    /// AI 호출 하나를 계획대로 재시도한다. 재시도 전체를 하나의 Task로 돌려주므로
    /// 호출부는 WrapWithProgress를 그대로 쓸 수 있다.
    ///
    /// 이 재시도는 MaxL2Attempts를 소모하지 않는다. 그 예산은 Actor-Critic 문서 레벨의
    /// 것이고, 여기는 호출 하나가 일시적으로 실패한 것을 메우는 국소 보수다.
    /// </summary>
    public static class AiCallRetry
    {
        public static async Task<T> ExecuteAsync<T>(
            Func<Task<T>> factory,
            CancellationToken cancellationToken,
            RetryPlan? plan = null)
        {
            var effectivePlan = plan ?? RetryPlan.Default;
            Exception? lastFailure = null;

            for (var attempt = 1; attempt <= effectivePlan.MaxTries; attempt++)
            {
                try
                {
                    return await factory();
                }
                catch (Exception ex)
                {
                    var verdict = AiRetryPolicy.Classify(ex, cancellationToken);

                    // 취소를 삼키면 실패로 위장한 정상 반환이 되어 취소 사실이 사라진다.
                    if (verdict == AiRetryVerdict.Cancelled)
                    {
                        throw;
                    }

                    // 같은 입력에 같은 응답이 온다. 돈만 태우므로 즉시 올려보낸다.
                    if (verdict == AiRetryVerdict.Fatal)
                    {
                        throw;
                    }

                    lastFailure = ex;

                    if (attempt < effectivePlan.MaxTries)
                    {
                        Log.Warning(
                            "[재시도] AI 호출이 일시적으로 실패했습니다 - 시도 {Attempt}/{MaxTries}, 사유: {Reason}",
                            attempt, effectivePlan.MaxTries, ex.Message);

                        await DelayAsync(effectivePlan, cancellationToken);
                    }
                }
            }

            Log.Error(
                "[재시도] AI 호출이 {MaxTries}회 모두 실패했습니다 - 마지막 사유: {Reason}",
                effectivePlan.MaxTries, lastFailure!.Message);

            throw new AiCallFailedException(
                $"AI 호출이 {effectivePlan.MaxTries}회 모두 실패했습니다: {lastFailure.Message}",
                lastFailure,
                effectivePlan.MaxTries);
        }

        /// <summary>
        /// 무작위 지연. 동시 실행 중에는 429가 여러 호출을 같은 창에서 때리므로,
        /// 무지연으로 재시도하면 그 시도들을 모두 같은 창 안에 쏟아붓게 된다.
        /// 무작위 지연이 상관된 폭풍을 흩트러진 재시도로 바꾼다.
        /// </summary>
        private static Task DelayAsync(RetryPlan plan, CancellationToken cancellationToken)
        {
            if (plan.MaxDelay <= TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            var milliseconds = Random.Shared.Next(
                (int)plan.MinDelay.TotalMilliseconds,
                (int)plan.MaxDelay.TotalMilliseconds);

            return Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --nologo -v q --filter "FullyQualifiedName~AiCallRetryTests"`

Expected: `통과!  - 실패: 0, 통과: 7`

- [ ] **Step 5: `Fatal` 가드의 실효를 뮤테이션으로 확인한다**

`ExecuteAsync_FatalOnFirstTry_DoesNotRetryAndRethrowsOriginal`은 오늘도 통과하는 가드다.
실효가 있는지 확인한다.

`AiRetryPolicy.Classify`의 마지막 `return AiRetryVerdict.Fatal;`을 잠시
`return AiRetryVerdict.Transient;`로 바꾸고, `HttpRequestException` 분기의
`_ => AiRetryVerdict.Fatal`도 `_ => AiRetryVerdict.Transient`로 바꾼다.

Run: `dotnet test tests/ReSet.Core.Tests --nologo --filter "FullyQualifiedName~AiCallRetryTests"`

Expected: `ExecuteAsync_FatalOnFirstTry_...`가 **실패**한다 (`calls`가 1이 아니라 2).
확인했으면 **두 줄을 원래대로 되돌리고** 다시 돌려 전부 통과하는지 본다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/AiCallRetry.cs tests/ReSet.Core.Tests/AiCallRetryTests.cs
git commit -m "feat: AI 호출 재시도 루프 - 소진 시 취소가 아닌 예외로 던진다"
```

---

### Task 3: API 클라이언트 6곳이 상태 코드를 보존한다

**Files:**
- Modify: `src/ReSet.Core/Services/Clients/OpenAiClient.cs` (2곳)
- Modify: `src/ReSet.Core/Services/Clients/ClaudeClient.cs`
- Modify: `src/ReSet.Core/Services/Clients/GoogleClient.cs`
- Modify: `src/ReSet.Core/Services/Clients/OllamaClient.cs`
- Modify: `src/ReSet.Core/Services/Clients/ZaiClient.cs`
- Test: `tests/ReSet.Core.Tests/OpenAiClientTests.cs` (기존 파일에 추가)

**Interfaces:**
- Consumes: 없음
- Produces: 던져진 `HttpRequestException.StatusCode`가 `null`이 아니다. Task 1의
  `AiRetryPolicy.Classify`가 이 값을 읽는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/OpenAiClientTests.cs`의 `ChatAsync_WithErrorStatusCode_ShouldThrowHttpRequestException`
**바로 아래에** 추가한다 (`public class OpenAiRequestSpyHandler` 선언 앞):

```csharp
        /// <summary>
        /// 상태 코드가 메시지 문자열 안에만 있으면 재시도 판정이 산문 매칭이 된다.
        /// AiRetryPolicy가 429와 401을 가르는 근거가 이 속성이다.
        /// </summary>
        [Theory]
        [InlineData(System.Net.HttpStatusCode.TooManyRequests)]
        [InlineData(System.Net.HttpStatusCode.ServiceUnavailable)]
        [InlineData(System.Net.HttpStatusCode.Unauthorized)]
        public async Task ChatAsync_ErrorResponse_PreservesStatusCodeOnException(
            System.Net.HttpStatusCode statusCode)
        {
            var spyHandler = new OpenAiRequestSpyHandler("error body", statusCode);
            var httpClient = new HttpClient(spyHandler);
            var client = new OpenAiClient(httpClient, "test_api_key", "https://api.openai.com/v1", "gpt-4o");

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync("System", "User", 0.7f));

            Assert.Equal(statusCode, ex.StatusCode);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --nologo --filter "FullyQualifiedName~PreservesStatusCodeOnException"`

Expected: FAIL — `Assert.Equal() Failure`, 실제 값은 `null`.

- [ ] **Step 3: 최소 구현 — 6곳을 같은 모양으로 고친다**

여섯 자리 모두 아래 문자열을 그대로 갖고 있다. `);` 앞을 바꾼다.

```csharp
// 전
throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).\n상세 에러 내용: {errorContent}");

// 후
throw new HttpRequestException(
    $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).\n상세 에러 내용: {errorContent}",
    null,
    response.StatusCode);
```

고칠 자리:

| 파일 | 메서드 안의 표지 |
|---|---|
| `OpenAiClient.cs` | Responses API 분기 (`if (!response.IsSuccessStatusCode)` 첫 번째) |
| `OpenAiClient.cs` | Chat Completions 분기 (`if (!response.IsSuccessStatusCode)` 두 번째) |
| `ClaudeClient.cs` | 유일한 `if (!response.IsSuccessStatusCode)` |
| `GoogleClient.cs` | 유일한 `if (!response.IsSuccessStatusCode)` |
| `OllamaClient.cs` | 유일한 `if (!response.IsSuccessStatusCode)` |
| `ZaiClient.cs` | 유일한 `if (!response.IsSuccessStatusCode)` |

전부 고쳤는지 확인:

```bash
grep -rn "new HttpRequestException" src/ReSet.Core/Services/Clients/ | grep -v "response.StatusCode)"
```

Expected: 출력 없음.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --nologo -v q --filter "FullyQualifiedName~OpenAiClientTests"`

Expected: 전부 통과. 기존 `ChatAsync_WithErrorStatusCode_ShouldThrowHttpRequestException`도
계속 통과해야 한다 — 메시지 내용은 안 바뀌었다.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/Clients/ tests/ReSet.Core.Tests/OpenAiClientTests.cs
git commit -m "fix: API 클라이언트 6곳이 HTTP 상태 코드를 예외에 보존한다"
```

---

### Task 4: CLI 실패의 유형이 예외에 남는다

**Files:**
- Create: `src/ReSet.Core/Services/Clients/Cli/CliInvocationException.cs`
- Modify: `src/ReSet.Core/Services/Clients/Cli/CliFailureClassifier.cs`
- Modify: `src/ReSet.Core/Services/AiRetryPolicy.cs`
- Test: `tests/ReSet.Core.Tests/CliFailureClassifierTests.cs` (기존 파일에 추가)
- Test: `tests/ReSet.Core.Tests/AiRetryPolicyTests.cs` (기존 파일에 추가)

**Interfaces:**
- Consumes: `AiRetryPolicy.Classify` (Task 1), `CliFailureKind` (기존)
- Produces: `public sealed class CliInvocationException : InvalidOperationException`
  with `public CliFailureKind Kind { get; }`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/CliFailureClassifierTests.cs`의 클래스 안 끝에 추가:

```csharp
        /// <summary>
        /// 분류를 문구로 녹인 뒤 버리면 재시도 판정이 그 문구를 다시 파싱해야 한다.
        /// 쿼터 소진을 재시도하면 이미 빈 지갑을 계속 두드리게 된다.
        /// </summary>
        [Fact]
        public void ToException_PreservesFailureKindOnTheException()
        {
            var result = new CliProcessResult { ExitCode = -1, TimedOut = true };

            var exception = CliFailureClassifier.ToException("codex-cli", "codex", result, null);

            var typed = Assert.IsType<CliInvocationException>(exception);
            Assert.Equal(CliFailureKind.Timeout, typed.Kind);
        }

        [Fact]
        public void ToException_QuotaExhausted_CarriesQuotaKind()
        {
            var exception = CliFailureClassifier.ToException(
                "claude-cli", "claude", Failed("429 too many requests"), null);

            var typed = Assert.IsType<CliInvocationException>(exception);
            Assert.Equal(CliFailureKind.QuotaExhausted, typed.Kind);
        }

        /// <summary>
        /// 하위형이므로 InvalidOperationException을 잡던 기존 호출부가 그대로 잡는다.
        /// 이게 깨지면 도입 자체가 회귀다.
        /// </summary>
        [Fact]
        public void ToException_IsStillAnInvalidOperationException()
        {
            var exception = CliFailureClassifier.ToException(
                "codex-cli", "codex", Failed("something"), null);

            Assert.IsAssignableFrom<InvalidOperationException>(exception);
        }
```

`tests/ReSet.Core.Tests/AiRetryPolicyTests.cs`의 클래스 안 끝에 추가
(파일 상단에 `using ReSet.Core.Services.Clients.Cli;`를 더한다):

```csharp
        [Fact]
        public void Classify_CliTimeout_IsTransient()
        {
            var ex = new CliInvocationException("타임아웃", CliFailureKind.Timeout);

            Assert.Equal(AiRetryVerdict.Transient, AiRetryPolicy.Classify(ex, CancellationToken.None));
        }

        [Theory]
        [InlineData(CliFailureKind.QuotaExhausted)]
        [InlineData(CliFailureKind.NotAuthenticated)]
        [InlineData(CliFailureKind.ToolPermissionDenied)]
        public void Classify_CliFatalKinds_AreFatal(CliFailureKind kind)
        {
            // 쿼터 소진을 재시도하면 이미 빈 지갑을 계속 두드리게 된다.
            var ex = new CliInvocationException("실패", kind);

            Assert.Equal(AiRetryVerdict.Fatal, AiRetryPolicy.Classify(ex, CancellationToken.None));
        }

        [Fact]
        public void Classify_CliUnknown_IsFatal()
        {
            // 무엇인지 모르는 것을 돈 내고 반복하지 않는다.
            var ex = new CliInvocationException("알 수 없음", CliFailureKind.Unknown);

            Assert.Equal(AiRetryVerdict.Fatal, AiRetryPolicy.Classify(ex, CancellationToken.None));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet build tests/ReSet.Core.Tests -v q --nologo 2>&1 | grep "error CS"`

Expected: `CliInvocationException` 형식이 없다는 `CS0246`.

- [ ] **Step 3: 최소 구현**

`src/ReSet.Core/Services/Clients/Cli/CliInvocationException.cs`:

```csharp
using System;

namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// CLI 호출 실패. 분류 결과를 속성으로 보존한다.
    ///
    /// 이전에는 CliFailureClassifier가 CliFailureKind를 계산해 안내 문구로 녹인 뒤
    /// 평범한 InvalidOperationException을 돌려주고 kind를 버렸다. 그래서 재시도 판정이
    /// 그 문구를 다시 파싱해야 했다 - 산문 매칭은 문구가 바뀌면 아무 신호 없이 오작동한다.
    ///
    /// InvalidOperationException 하위형이므로 기존 catch가 그대로 잡는다.
    /// </summary>
    public sealed class CliInvocationException : InvalidOperationException
    {
        public CliFailureKind Kind { get; }

        public CliInvocationException(string message, CliFailureKind kind)
            : base(message)
        {
            Kind = kind;
        }
    }
}
```

`CliFailureClassifier.BuildException`의 마지막 줄을 바꾼다:

```csharp
// 전
            return new InvalidOperationException(builder.ToString());

// 후
            return new CliInvocationException(builder.ToString(), kind);
```

`BuildException`의 반환 형식 선언도 좁힌다:

```csharp
// 전
        private static InvalidOperationException BuildException(

// 후
        private static CliInvocationException BuildException(
```

`AiRetryPolicy.Classify`에 CLI 분기를 더한다. `HttpRequestException` 분기 **뒤**,
마지막 `return AiRetryVerdict.Fatal;` **앞**에 넣는다:

```csharp
            // CLI 프로바이더. 분류가 예외에 실려 있으므로 문구를 보지 않는다.
            if (ex is Clients.Cli.CliInvocationException cliEx)
            {
                return cliEx.Kind == Clients.Cli.CliFailureKind.Timeout
                    ? AiRetryVerdict.Transient
                    : AiRetryVerdict.Fatal;
            }
```

> **순서가 중요하다.** `CliInvocationException`은 `InvalidOperationException`이므로,
> 이 분기가 마지막 `Fatal` 폴백보다 **앞**에 있어야 `Timeout`이 `Transient`로 판정된다.

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --nologo -v q --filter "FullyQualifiedName~CliFailureClassifierTests|FullyQualifiedName~AiRetryPolicyTests"`

Expected: 전부 통과. 기존 `CliFailureClassifierTests`의 다른 테스트들도 계속 통과해야 한다 —
메시지 문구는 안 바뀌었다.

- [ ] **Step 5: 전체 테스트로 회귀를 본다**

`InvalidOperationException`을 잡던 자리가 있으므로 전체를 돌린다.

Run: `dotnet test --nologo -v q 2>&1 | grep -v "warning CS" | tail -5`

Expected: `실패: 0`, 통과 수가 이전보다 늘어나 있다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/Clients/Cli/ src/ReSet.Core/Services/AiRetryPolicy.cs \
        tests/ReSet.Core.Tests/CliFailureClassifierTests.cs tests/ReSet.Core.Tests/AiRetryPolicyTests.cs
git commit -m "fix: CLI 실패 유형을 예외에 보존하고 재시도 판정에 연결한다"
```

---

### Task 5: 리뷰 호출 5곳을 감싼다

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (5곳 + 로그 1줄)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` (기존 파일에 추가)

**Interfaces:**
- Consumes: `AiCallRetry.ExecuteAsync<T>(Func<Task<T>>, CancellationToken, RetryPlan?)` (Task 2)
- Produces: 없음 (최종 작업)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`의
`RunConsolidatedPipelineAsync_ReviewClaimsNoDefectsButAxisBelowThreshold_ShouldNotPass`
**바로 앞에** 추가한다:

```csharp
        /// <summary>
        /// 일시적 API 오류 한 번에 그 회차의 검증을 포기하던 자리다.
        /// RetryRescue가 이전 회차 최고점을 구제해 완화할 뿐 검증 자체는 버려졌다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipelineAsync_TransientReviewFailure_RetriesAndSucceeds()
        {
            var specs = new List<(string, string)> { ("dbo.USP_Test1", "내용") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            var goodReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
            };

            // 1회차는 503, 2회차는 성공. 재시도가 없으면 이 Job은 교차 검증 없이 끝난다.
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), plan, "Job_Test")
                .Returns(
                    _ => throw new HttpRequestException("서비스 일시 오류", null, System.Net.HttpStatusCode.ServiceUnavailable),
                    _ => Task.FromResult(goodReview));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var result = await _orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            Assert.NotNull(result.Plan);
            // 같은 회차 안에서 두 번 불렸어야 한다.
            await _aiService.Received(2).ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), plan, "Job_Test");
            // 교차 검증을 건너뛴 배너가 없어야 한다. 문구는 VerificationBanner.ReviewNotRun의 것이다
            // (콘솔 NotifyError 문구와 다르므로 그쪽을 쓰면 거짓 통과한다).
            Assert.DoesNotContain("L2 AI 교차 리뷰가 수행되지 않았습니다", result.Plan);
            _userInteraction.Received(1).NotifyValidationSuccess("Job_Test");
        }

        /// <summary>
        /// HttpClient 타임아웃은 TaskCanceledException이고 이것은 OperationCanceledException이다.
        /// 호출부 필터가 그것을 거르므로 예외가 파이프라인 밖으로 새어 나가
        /// "사용자에 의해 중단되었습니다"로 보고됐다. TimeoutSeconds가 3600이므로
        /// 한 시간을 태운 뒤에 사용자 탓이 되던 자리다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipelineAsync_HttpTimeout_DoesNotEscapeAsCancellation()
        {
            var specs = new List<(string, string)> { ("dbo.USP_Test1", "내용") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            // 토큰은 취소되지 않았다. 사용자가 멈춘 게 아니라 HttpClient가 시간을 다 쓴 것이다.
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), plan, "Job_Test")
                .Returns(_ => throw new TaskCanceledException("HttpClient.Timeout", new TimeoutException()));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            // 던지지 않고 끝나야 한다. 이 줄이 회귀 방지의 핵심이다.
            var result = await _orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            Assert.NotNull(result.Plan);
            // 소진될 때까지 재시도했고, 그 뒤 소프트 페일 경로로 내려왔다.
            await _aiService.Received(2).ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), plan, "Job_Test");
            Assert.Contains("L2 AI 교차 리뷰가 수행되지 않았습니다", result.Plan);
        }

        /// <summary>
        /// 인증 실패를 재시도하면 이미 빈 지갑을 계속 두드리게 된다.
        /// 과잉 재시도 방지 가드 - 오늘도 통과하므로 뮤테이션으로 실효를 확인한다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipelineAsync_FatalReviewFailure_DoesNotRetry()
        {
            var specs = new List<(string, string)> { ("dbo.USP_Test1", "내용") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), plan, "Job_Test")
                .Returns(_ => throw new HttpRequestException("인증 실패", null, System.Net.HttpStatusCode.Unauthorized));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var result = await _orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            Assert.NotNull(result.Plan);
            await _aiService.Received(1).ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), plan, "Job_Test");
        }
```

파일 상단 `using`에 `System.Net.Http`가 없으면 더한다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --nologo --filter "FullyQualifiedName~TransientReviewFailure|FullyQualifiedName~HttpTimeout_DoesNotEscape"`

Expected: 둘 다 FAIL.
- `TransientReviewFailure` → `Received(2)`인데 실제 1회.
- `HttpTimeout_DoesNotEscape` → `TaskCanceledException`이 테스트 밖으로 던져진다.

- [ ] **Step 3: 리뷰 5곳을 감싼다**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`에서 아래 다섯 자리를 고친다.
전부 같은 변형이다 — `_criticService.Review*Async(...)`를
`AiCallRetry.ExecuteAsync(() => _criticService.Review*Async(...), cancellationToken)`으로 감싼다.

**① `RunCodeObjectPipelineCoreAsync` — taskKey `"review"`**

```csharp
// 전
l2Result = await WrapWithProgress(_criticService.ReviewSpecificationAsync(spDef, specificationMarkdown, _criticEffort, cancellationToken), progressScope, "review");

// 후
l2Result = await WrapWithProgress(
    AiCallRetry.ExecuteAsync(
        () => _criticService.ReviewSpecificationAsync(spDef, specificationMarkdown, _criticEffort, cancellationToken),
        cancellationToken),
    progressScope, "review");
```

**② `RunConsolidatedPipelineAsync` — taskKey `"batchreview"`**

```csharp
// 전
l2Result = await WrapWithProgress(_criticService.ReviewConsolidatedPlanAsync(specs, consolidatedPlan, jobName, _criticEffort, cancellationToken), progressScope, "batchreview");

// 후
l2Result = await WrapWithProgress(
    AiCallRetry.ExecuteAsync(
        () => _criticService.ReviewConsolidatedPlanAsync(specs, consolidatedPlan, jobName, _criticEffort, cancellationToken),
        cancellationToken),
    progressScope, "batchreview");
```

**③ `RunCodeObjectPipelineCoreAsync` — taskKey `"final_review"`**

```csharp
// 전
finalL2Result = await WrapWithProgress(_criticService.ReviewSpecificationAsync(spDef, specificationMarkdown, _criticEffort, cancellationToken), progressScope, "final_review");

// 후
finalL2Result = await WrapWithProgress(
    AiCallRetry.ExecuteAsync(
        () => _criticService.ReviewSpecificationAsync(spDef, specificationMarkdown, _criticEffort, cancellationToken),
        cancellationToken),
    progressScope, "final_review");
```

**④ `RunCodeObjectPipelineCoreAsync` — taskKey `"refinal"`**

```csharp
// 전
var reFinalReview = await WrapWithProgress(_criticService.ReviewSpecificationAsync(spDef, specificationMarkdown, _criticEffort, cancellationToken), progressScope, "refinal");

// 후
var reFinalReview = await WrapWithProgress(
    AiCallRetry.ExecuteAsync(
        () => _criticService.ReviewSpecificationAsync(spDef, specificationMarkdown, _criticEffort, cancellationToken),
        cancellationToken),
    progressScope, "refinal");
```

**⑤ `RunCodeObjectPipelineCoreAsync` — `reviewTasks` 조립부 (3후보 병렬)**

```csharp
// 전
reviewTasks.Add(WrapWithProgress(_criticService.ReviewSpecificationAsync(spDef, candidates[i], _criticEffort, cancellationToken), progressScope, taskKey));

// 후
var candidate = candidates[i];   // 클로저가 루프 변수를 붙잡지 않게 지역으로 고정한다
reviewTasks.Add(WrapWithProgress(
    AiCallRetry.ExecuteAsync(
        () => _criticService.ReviewSpecificationAsync(spDef, candidate, _criticEffort, cancellationToken),
        cancellationToken),
    progressScope, taskKey));
```

> ⑤만 지역 변수를 추가한다. 팩토리가 지연 실행되므로 `candidates[i]`를 클로저 안에 두면
> 재시도 시점의 `i`를 읽는다. C#의 `for` 루프 변수는 반복마다 새로 만들어지지 않는다.

- [ ] **Step 4: `"refinal"`의 빈 catch에 로그를 더한다**

같은 파일 ④ 바로 아래에 있다.

```csharp
// 전
                                catch (Exception ex) when (ex is not OperationCanceledException) { }

// 후
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    // 재검토 실패는 이전 finalReview를 유지하므로 치명적이지 않다.
                                    // 다만 조용히 삼키면 재시도까지 다 쓰고 실패한 사실이 어디에도 남지 않는다.
                                    Log.Warning("[파이프라인] 보완본 L2 재검토 실패 - 이전 최종 리뷰를 유지합니다: {Reason}", ex.Message);
                                }
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --nologo -v q --filter "FullyQualifiedName~TransientReviewFailure|FullyQualifiedName~HttpTimeout_DoesNotEscape|FullyQualifiedName~FatalReviewFailure_DoesNotRetry"`

Expected: `통과!  - 실패: 0, 통과: 3`

- [ ] **Step 6: `Fatal` 가드의 실효를 뮤테이션으로 확인한다**

`RunConsolidatedPipelineAsync_FatalReviewFailure_DoesNotRetry`는 Task 5 이전에도 통과했다.

`AiRetryPolicy.Classify`의 `HttpRequestException` 분기에서 `_ => AiRetryVerdict.Fatal`을
`_ => AiRetryVerdict.Transient`로 잠시 바꾼다.

Run: `dotnet test tests/ReSet.Core.Tests --nologo --filter "FullyQualifiedName~FatalReviewFailure_DoesNotRetry"`

Expected: **실패** — `Received(1)`인데 실제 2회.
확인했으면 **되돌리고** 다시 통과하는지 본다.

- [ ] **Step 7: 감싸지 않은 리뷰 호출이 남았는지 확인한다**

```bash
grep -n "_criticService.Review" src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs | grep -v "AiCallRetry"
```

Expected: 출력 없음. 다섯 자리 전부 감겼다.

- [ ] **Step 8: 전체 검증**

```bash
dotnet build --no-incremental --nologo 2>&1 | tail -3
dotnet test --nologo -v q 2>&1 | grep -v "warning CS" | tail -3
```

Expected: 경고 **8개**, 오류 0개. 테스트 `실패: 0`.

- [ ] **Step 9: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs \
        tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "fix: L2 리뷰 호출 5곳에 재시도를 걸어 일시적 오류로 검증을 버리지 않는다"
```

---

### Task 6: 문서 갱신

**Files:**
- Modify: `docs/known-defects.md`
- Modify: `docs/superpowers/specs/2026-08-21-ai-call-retry-design.md`

**Interfaces:**
- Consumes: Task 1–5의 결과
- Produces: 없음

- [ ] **Step 1: `known-defects.md`의 P0 ③을 「닫힌 것」으로 옮긴다**

`### P0 — 실사용 피해가 즉시 발생` 절에서 **L2 리뷰 호출 재시도 인프라 부재** 항목을
통째로 지운다. P0 절이 비면 그 헤딩도 지운다.

「닫힌 것」 표에 두 줄을 더한다:

```markdown
| L2 리뷰 호출 재시도 인프라 부재 (5개 설계 이월) | `AiRetryPolicy`(순수 판정) + `AiCallRetry`(2회·지터 500~1500ms, `MaxL2Attempts` 미소모)를 신설하고 리뷰 5곳에 걸었다. 판정 재료를 위해 API 클라이언트 6곳이 `HttpRequestException.StatusCode`를, CLI가 `CliInvocationException.Kind`를 보존한다. **생성 호출 27곳은 열려 있다.** 설계: `2026-08-21-ai-call-retry-design.md` |
| HttpClient 타임아웃이 "사용자 취소"로 보고됐다 | 실측(.NET 10.0.10)으로 드러났다 — 타임아웃도 `TaskCanceledException`이라 `when (ex is not OperationCanceledException)` 필터 55곳이 전부 놓치고 최상위가 "사용자에 의해 중단되었습니다"를 찍었다. `AiCallRetry`가 소진 시 `AiCallFailedException`(취소 아님)으로 감싸 **리뷰 경로에서만** 닫혔다 |
```

- [ ] **Step 2: 「알려진 한계」에 남은 구멍을 적는다**

`### 검증 파이프라인` 절에 더한다:

```markdown
- **타임아웃 오보가 리뷰 경로 밖에는 그대로다** — `when (ex is not OperationCanceledException)`
  필터 55곳. `AiCallRetry`가 감싼 리뷰 5곳만 `AiCallFailedException`으로 바뀌어 정상
  보고된다. 생성 호출 27곳을 포함한 나머지 경로의 HttpClient 타임아웃은 여전히
  "사용자에 의해 중단되었습니다"로 찍힌다. `AiSettings:TimeoutSeconds`가 3600이므로
  한 시간을 태운 뒤 그렇게 된다.
  출처: `2026-08-21-ai-call-retry-design.md` §7
```

- [ ] **Step 3: 설계 문서의 상태를 갱신한다**

`2026-08-21-ai-call-retry-design.md`의 3번째 줄:

```markdown
// 전
작성일: 2026-08-21 · 상태: 설계 승인 대기

// 후
작성일: 2026-08-21 · 상태: 구현 완료
```

- [ ] **Step 4: 줄 번호 앵커가 새로 들어가지 않았는지 확인한다**

```bash
grep -n "\.cs:[0-9]" docs/known-defects.md docs/superpowers/specs/2026-08-21-ai-call-retry-design.md
```

Expected: 출력 없음. `known-defects.md`의 앵커 규약은 `타입.멤버`를 요구한다.

- [ ] **Step 5: 커밋**

```bash
git add docs/known-defects.md docs/superpowers/specs/2026-08-21-ai-call-retry-design.md
git commit -m "docs: P0 ③을 닫고 리뷰 경로 밖에 남은 타임아웃 오보를 기록한다"
```

---

## 완료 기준

- [ ] `dotnet build --no-incremental` — 경고 8개, 오류 0개
- [ ] `dotnet test` — 실패 0
- [ ] `grep -n "_criticService.Review" src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs | grep -v AiCallRetry` — 출력 없음
- [ ] `grep -rn "new HttpRequestException" src/ReSet.Core/Services/Clients/ | grep -v "response.StatusCode)"` — 출력 없음
- [ ] 뮤테이션 확인 2건(Task 2 Step 5, Task 5 Step 6)을 실제로 돌리고 **되돌렸다**
- [ ] `known-defects.md`에서 P0 ③이 「닫힌 것」으로 옮겨졌고, 남은 구멍이 「알려진 한계」에 적혔다

## 이 계획이 하지 않는 것

설계 §7 그대로다. 닫은 척하면 다음 사람이 속는다.

- 생성 호출 27곳 (`Generate*` · `Draft*` · `Brainstorm*` · `Deconstruct*`)
- `RunCodeObjectPipelineCoreAsync`의 `Task.WhenAll(reviewTasks)` 예외 표면화 결함
- 리뷰 경로 밖의 `is not OperationCanceledException` 필터
- `AiSettings:TimeoutSeconds: 3600` 값 자체
- CLI 프로바이더 동시 실행 제어
