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
