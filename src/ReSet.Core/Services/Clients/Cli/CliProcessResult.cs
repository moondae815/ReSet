namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// CLI 프로세스 1회 실행의 결과. 실패를 예외로 던지지 않고 값으로 돌려주므로
    /// 호출자가 원인을 분류할 수 있다.
    /// </summary>
    public sealed class CliProcessResult
    {
        public int ExitCode { get; init; }
        public string StandardOutput { get; init; } = string.Empty;
        public string StandardError { get; init; } = string.Empty;

        /// <summary>타임아웃으로 프로세스를 강제 종료했는가. 사용자 취소와는 다르다.</summary>
        public bool TimedOut { get; init; }

        public bool Succeeded => !TimedOut && ExitCode == 0;
    }
}
