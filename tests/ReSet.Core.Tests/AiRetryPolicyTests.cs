using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients.Cli;
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
    }
}
