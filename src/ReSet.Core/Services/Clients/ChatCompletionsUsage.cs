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
    /// 캐시 수치는 둘 다 <c>prompt_tokens_details</c> 안에 있다. 여기서 한 번
    /// 틀렸다 - "이 규격에는 캐시 쓰기 칸이 없다"고 단언하고 null로 못박았는데,
    /// OpenRouter 실물 봉투에는 <c>cache_write_tokens</c>가 실려 온다. 그래서
    /// <b>없다고 단정하지 않고 읽어 본다</b>: 있으면 그 값이고 없으면 미보고다.
    /// 규격 문서가 아니라 봉투가 무엇을 말하는지가 기준이다.
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
                CacheWrite: TokenUsage.ReadNestedCounter(usage, "prompt_tokens_details", "cache_write_tokens"),
                CacheRead: TokenUsage.ReadNestedCounter(usage, "prompt_tokens_details", "cached_tokens"),
                Thinking: TokenUsage.ReadNestedCounter(usage, "completion_tokens_details", "reasoning_tokens"));
        }
    }
}
