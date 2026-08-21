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
    }
}
