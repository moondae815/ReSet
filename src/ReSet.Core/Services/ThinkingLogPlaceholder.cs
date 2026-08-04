using ReSet.Core.Services.Clients;

namespace ReSet.Core.Services
{
    /// <summary>
    /// Thinking.md에 추론 본문이 없을 때 대신 실을 문구를 단독 소유한다.
    /// 문구를 다른 곳에서 새로 쓰지 마십시오.
    ///
    /// 사유가 두 가지인데 하나로 뭉뚱그리면 진단이 어긋난다.
    /// - API 제공자: 추론이 실제로 꺼져 있거나 모델이 지원하지 않는다.
    /// - CLI 제공자: 추론은 수행되지만 CLI가 본문을 돌려주지 않는다.
    ///   claude-cli를 stream-json으로 띄우면 thinking 블록이 signature까지 달고 오지만
    ///   본문 길이는 0이고(표시 방식을 바꿀 인자가 없다), codex-cli는 --json과
    ///   show_raw_agent_reasoning 어느 쪽으로도 추론 이벤트를 내보내지 않는다.
    ///   이 경우를 "추론 비활성화"로 적으면 Effort 설정이 먹지 않은 것으로 오독된다.
    /// </summary>
    public static class ThinkingLogPlaceholder
    {
        private const string DisabledOrUnsupported =
            "*(추론 비활성화 또는 추론 기능을 지원하지 않는 모델입니다.)*";

        private const string CliNotExposed =
            "*(CLI 제공자는 추론 본문을 반환하지 않습니다. 설정된 Effort로 추론은 수행되지만, " +
            "헤드리스 CLI가 추론 텍스트를 노출하지 않아 기록할 수 없습니다.)*";

        public static string For(string? providerName) =>
            AiClientFactory.IsCliProvider(providerName ?? string.Empty)
                ? CliNotExposed
                : DisabledOrUnsupported;
    }
}
