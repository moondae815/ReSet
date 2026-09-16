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

            // 형식 관계를 반사로 본다. `ex is OperationCanceledException`은 ex의 정적 형식이
            // sealed AiCallFailedException이라 컴파일러가 "항상 거짓"으로 증명해 CS0184를 내고,
            // 그 경고 자체가 "이 단언은 공허하다"는 뜻이다. IsNotType은 반대로 정확한 형식만
            // 보므로 하위형 상속을 못 잡는다. 아래는 상속 관계가 생기면 실제로 실패한다.
            Assert.False(typeof(OperationCanceledException).IsAssignableFrom(typeof(AiCallFailedException)));

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
            Assert.False(typeof(OperationCanceledException).IsAssignableFrom(ex.GetType()));
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

        /// <summary>
        /// MaxTries가 0 이하이면 for 루프 본문이 한 번도 안 돌아 lastFailure가 null로
        /// 남는다. 그 상태로 Log.Error(..., lastFailure!.Message)에 이르면 null 허용
        /// 연산자가 진짜 NullReferenceException을 숨긴다. VerificationPipelineOrchestrator의
        /// _stepConcurrency = Math.Max(1, stepConcurrency)와 같은 절상 - 0·음수는 1로
        /// 올리고 상한은 두지 않는다.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task ExecuteAsync_MaxTriesZeroOrNegative_ClampsToOneAndCallsFactoryOnce(int maxTries)
        {
            var calls = 0;
            var plan = new RetryPlan(maxTries, TimeSpan.Zero, TimeSpan.Zero);

            var ex = await Assert.ThrowsAsync<AiCallFailedException>(() =>
                AiCallRetry.ExecuteAsync<string>(
                    () => { calls++; throw Transient(); },
                    CancellationToken.None,
                    plan));

            Assert.Equal(1, calls);
            Assert.Equal(1, ex.Attempts);
        }

        /// <summary>
        /// 재시도 사이 대기 중에 취소되는 경로다 - 사용자가 대기 중에 Ctrl-C를 누르는
        /// 실사용 시나리오다. DelayAsync가 던진 취소가 catch 블록 안에서 삼켜지면 그
        /// 사이 두 번째 시도가 돈 것으로 착각하게 된다. 지연을 짧게 두되 취소는 그보다
        /// 먼저 걸어 대기 도중임을 보장한다.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_CancellationDuringDelay_EscapesWithoutRetryingAgain()
        {
            using var cts = new CancellationTokenSource();
            var calls = 0;
            var plan = new RetryPlan(2, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(90));

            cts.CancelAfter(TimeSpan.FromMilliseconds(15));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AiCallRetry.ExecuteAsync<string>(
                    () => { calls++; throw Transient(); },
                    cts.Token,
                    plan));

            // 대기 중에 취소됐으므로 두 번째 시도(factory의 두 번째 호출)로 넘어가면 안 된다.
            Assert.Equal(1, calls);
        }

        // 쿼터 소진은 CliFailureClassifier가 이미 분류해 준 사실이다 - Fatal처럼
        // 재시도 없이 즉시 포기하되, 사유(Verdict)는 위로 실어 보낸다(설계 §4-1·§4-2).
        [Fact]
        public async Task ExecuteAsync_WhenQuotaIsExhausted_StopsImmediatelyAndCarriesTheVerdict()
        {
            var calls = 0;
            var thrown = await Assert.ThrowsAsync<AiCallFailedException>(() =>
                AiCallRetry.ExecuteAsync<string>(() =>
                {
                    calls++;
                    throw new ReSet.Core.Services.Clients.Cli.CliInvocationException(
                        "한도 소진", ReSet.Core.Services.Clients.Cli.CliFailureKind.QuotaExhausted);
                }, CancellationToken.None, RetryPlan.NoDelay));

            Assert.Equal(1, calls);                                   // 재시도하지 않는다
            Assert.Equal(AiRetryVerdict.Exhausted, thrown.Verdict);   // 사유가 위로 간다
        }

        // 429가 재시도를 다 쓰면 그때는 짧은 막힘이 아니다(설계 §4-1) - 반대 방향은
        // Classify_Http429_IsTransientSoTheRetryStillRuns가 잡는다: 첫 429에 곧바로
        // Exhausted로 보면 넘길 수 있는 막힘에 포기하는 것이라 두 시험이 함께 있어야
        // 방향 오류를 잡는다.
        [Fact]
        public async Task ExecuteAsync_WhenHttp429ExhaustsRetries_EndsAsExhausted()
        {
            var calls = 0;

            var thrown = await Assert.ThrowsAsync<AiCallFailedException>(() =>
                AiCallRetry.ExecuteAsync<string>(
                    () =>
                    {
                        calls++;
                        throw new HttpRequestException("429", null, HttpStatusCode.TooManyRequests);
                    },
                    CancellationToken.None, RetryPlan.NoDelay));

            // 승격 전에 재시도가 실제로 다 쓰였다는 것을 재야 한다 - 최종 Verdict 만 보면
            // 첫 429 에 곧바로 Exhausted 로 승격하는 뮤테이션도 같은 Verdict 를 내
            // 이 시험을 통과시킨다(설계 §5-3). 호출 횟수가 MaxTries 와 같아야 한다.
            Assert.Equal(RetryPlan.NoDelay.MaxTries, calls);
            Assert.Equal(AiRetryVerdict.Exhausted, thrown.Verdict);
        }

        // 대칭 확인: 429가 아닌 다른 일시적 실패(예: 503)가 재시도를 다 써도 Exhausted로
        // 승격되지 않는다 - 승격 조건은 「재시도 소진」 전체가 아니라 「429 소진」에
        // 좁게 걸려 있어야 한다(설계 §4-1 표 - CLI/HTTP 429 외에는 근거가 없다).
        [Fact]
        public async Task ExecuteAsync_WhenNon429TransientExhaustsRetries_StaysTransientNotExhausted()
        {
            var thrown = await Assert.ThrowsAsync<AiCallFailedException>(() =>
                AiCallRetry.ExecuteAsync<string>(
                    () => throw Transient(),
                    CancellationToken.None, RetryPlan.NoDelay));

            Assert.Equal(AiRetryVerdict.Transient, thrown.Verdict);
        }

        // ── 벽시계 상한(2026-09-16) ────────────────────────────────────────────
        // 실측: AI 응답 574 건 중 통합 계획서 리뷰 한 건이 52.9 분을 태우고 본문 0 자를 냈다.
        // 본문을 낸 최장 호출은 24.9 분, 리뷰 중 최장 정상은 11.8 분이다.
        // 판독: docs/audit-reports/2026-09-16-AI호출-벽시계-상한-사전선언.md

        // R1: 상한이 끊고, 계획대로 한 번 더 부른다.
        [Fact]
        public async Task ExecuteAsync_WhenTheFirstCallOutlastsTheDeadline_CallsAgain()
        {
            var calls = 0;

            var result = await AiCallRetry.ExecuteAsync(
                async token =>
                {
                    calls++;
                    if (calls == 1)
                    {
                        // [유한 대기인 이유] 무한 대기로 두면 「연결 토큰 제거」 되돌림에서 이 시험이 **멈춘다** -
                        // 판정을 못 내리는 시험은 되돌림의 자가 아니다. 상한(80ms)이 정상 코드에선 먼저 끊고,
                        // 상한이 안 걸리는 되돌림에선 이 대기가 끝나 결과가 달라져 빨개진다.
                        await Task.Delay(TimeSpan.FromSeconds(3), token);
                    }

                    return "성공";
                },
                CancellationToken.None,
                RetryPlan.NoDelay,
                deadline: TimeSpan.FromMilliseconds(80));

            Assert.Equal("성공", result);
            Assert.Equal(2, calls);
        }

        // R2: 두 번 다 상한에 걸리면 비취소 예외로 오른다 - 호출부 55 곳의 catch 가 잡는 형식이다.
        [Fact]
        public async Task ExecuteAsync_WhenEveryCallOutlastsTheDeadline_ThrowsNonCancellation()
        {
            var calls = 0;

            var ex = await Assert.ThrowsAsync<AiCallFailedException>(() =>
                AiCallRetry.ExecuteAsync<string>(
                    async token => { calls++; await Task.Delay(TimeSpan.FromSeconds(3), token); return "상한이 안 끊었다"; },
                    CancellationToken.None,
                    RetryPlan.NoDelay,
                    deadline: TimeSpan.FromMilliseconds(80)));

            Assert.IsNotType<OperationCanceledException>(ex);
            Assert.Equal(AiRetryVerdict.Transient, ex.Verdict);
            Assert.Equal(RetryPlan.NoDelay.MaxTries, calls);
        }

        // R3: 사용자 취소는 상한으로 둔갑하지 않는다 - 취소로 그대로 오른다.
        [Fact]
        public async Task ExecuteAsync_WithADeadline_UserCancellationStillSurfacesAsCancellation()
        {
            using var cts = new CancellationTokenSource();
            var calls = 0;

            var pending = AiCallRetry.ExecuteAsync<string>(
                async token => { calls++; cts.Cancel(); await Task.Delay(TimeSpan.FromSeconds(3), token); return "취소가 안 걸렸다"; },
                cts.Token,
                RetryPlan.NoDelay,
                deadline: TimeSpan.FromMinutes(20));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(1, calls);   // 취소는 재시도하지 않는다
        }

        // R4: 상한 안에 끝나면 무변화 - 상한을 건 토큰이 팩토리에 살아 있고 결과가 그대로 온다.
        [Fact]
        public async Task ExecuteAsync_WhenTheCallFinishesInsideTheDeadline_IsUnaffected()
        {
            var calls = 0;

            var result = await AiCallRetry.ExecuteAsync(
                token =>
                {
                    calls++;
                    Assert.False(token.IsCancellationRequested);
                    return Task.FromResult("성공");
                },
                CancellationToken.None,
                RetryPlan.NoDelay,
                deadline: TimeSpan.FromMinutes(20));

            Assert.Equal("성공", result);
            Assert.Equal(1, calls);
        }

        // R4 짝: 상한을 안 주면 연결 토큰을 만들지 않는다 - 넘어온 토큰 그대로다(종전 경로).
        [Fact]
        public async Task ExecuteAsync_WithoutADeadline_PassesTheCallersOwnToken()
        {
            using var cts = new CancellationTokenSource();
            CancellationToken seen = default;

            await AiCallRetry.ExecuteAsync(
                token => { seen = token; return Task.FromResult("성공"); },
                cts.Token,
                RetryPlan.NoDelay);

            Assert.Equal(cts.Token, seen);
        }
    }
}
