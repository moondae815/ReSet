using System.Text.Json;

namespace ReSet.Core.Services.Clients
{
    /// <summary>
    /// OpenAI 규격 <c>/chat/completions</c> 응답에서 토큰 집계를 읽는다.
    ///
    /// 이름 매핑은 각 클라이언트가 맡는다는 것이 이 폴더의 원칙인데, 이 규격만은
    /// 예외로 한 자리에 둔다 - 이것을 쓰는 클라이언트가 셋(OpenAI 채팅 경로 ·
    /// OpenRouter · Zai)이고 봉투가 바이트까지 같기 때문이다. 셋이 각자 베끼면
    /// 한 곳만 고쳐지고 나머지 둘은 조용히 낡는다.
    ///
    /// 이 규격에는 <b>캐시 쓰기를 담을 칸이 없다.</b> Anthropic 모델을 OpenRouter로
    /// 부를 때도 마찬가지다. 그래서 CacheWrite는 언제나 미보고(null)이고, 0이
    /// 아니다 - 0으로 적으면 "재보니 캐시를 안 썼다"는 측정값으로 읽힌다.
    /// </summary>
    public static class ChatCompletionsUsage
    {
        public static TokenUsage Read(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return new TokenUsage(null, null, null, null, null);
            }

            return new TokenUsage(
                Input: TokenUsage.ReadCounter(usage, "prompt_tokens"),
                Output: TokenUsage.ReadCounter(usage, "completion_tokens"),
                CacheWrite: null,
                CacheRead: TokenUsage.ReadNestedCounter(usage, "prompt_tokens_details", "cached_tokens"),
                Thinking: TokenUsage.ReadNestedCounter(usage, "completion_tokens_details", "reasoning_tokens"));
        }
    }
}
