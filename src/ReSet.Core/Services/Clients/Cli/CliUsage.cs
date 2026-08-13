using System.Text.Json;
using Serilog;

namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// CLI 한 번 호출의 토큰 집계.
    ///
    /// 세 CLI는 모두 캐시 수치를 봉투에 담아 주지만 필드 이름이 제각각이고, 보고하는
    /// 항목도 다르다(agy는 캐시 쓰기를 내지 않는다). 그래서 이름 매핑은 각 클라이언트가
    /// 맡고, 이 타입은 "읽은 값을 담아 한 줄로 남기는" 일만 한다.
    ///
    /// 모든 항목이 null 허용인 것은 의도다. 0은 <b>재보니 그만큼이었다</b>는 측정값이고
    /// null은 <b>이 provider가 보고하지 않는다</b>는 뜻이다. 둘을 0으로 뭉개면 필드가
    /// 없는 provider를 두고 "캐시를 쓰지 않는다"고 결론짓게 된다 - 실제로 그런 오판이
    /// 한 번 있었다.
    /// </summary>
    public sealed record CliUsage(
        int? Input,
        int? Output,
        int? CacheWrite,
        int? CacheRead,
        int? Thinking)
    {
        /// <summary>보고되지 않은 항목을 로그에 표기하는 말.</summary>
        public const string NotReported = "미보고";

        /// <summary>
        /// usage 객체에서 정수 하나를 읽는다. 없거나 숫자가 아니면 null이다.
        ///
        /// ValueKind 확인이 반드시 앞에 와야 한다. JsonElement.TryGetInt32는 숫자가
        /// 아닌 종류에 대해 false를 돌려주는 게 아니라 InvalidOperationException을
        /// 던진다. 봉투에 null이나 문자열이 들어오는 날, 집계 한 줄 때문에 분석 전체가
        /// 죽는다.
        /// </summary>
        public static int? ReadCounter(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var count)
                ? count
                : null;

        /// <summary>
        /// API 경로의 "Claude 토큰 사용량" 줄과 같은 모양으로 남긴다. 형식을 맞춰야
        /// CLI provider와 API provider의 캐시 거동을 같은 방식으로 비교할 수 있다.
        /// </summary>
        public void WriteToLog(string providerName)
        {
            Log.Information(
                "{Provider} 토큰 사용량 - 입력: {Input}, 캐시 쓰기: {CacheWrite}, " +
                "캐시 읽기: {CacheRead}, 출력: {Output}, 추론: {Thinking}",
                providerName,
                Format(Input), Format(CacheWrite), Format(CacheRead),
                Format(Output), Format(Thinking));
        }

        private static object Format(int? value) =>
            value.HasValue ? value.Value : NotReported;
    }
}
