using System.Text.Json;
using Xunit;
using ReSet.Core.Services.Clients;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// OpenAI 규격 `/chat/completions` 봉투에서 토큰 집계를 읽는 판독기.
    ///
    /// 이 규격을 쓰는 클라이언트가 셋(OpenAI 채팅 경로·OpenRouter·Zai)인데 봉투가
    /// 바이트까지 같으므로 매핑을 한 자리에 둔다. 셋이 각자 베끼면 한 곳만 고쳐지고
    /// 나머지 둘은 조용히 낡는다.
    /// </summary>
    public class ChatCompletionsUsageTests
    {
        private static JsonElement Parse(string json) =>
            JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void Read_ExtractsPromptCompletionAndCachedTokens()
        {
            var root = Parse(@"{""usage"":{""prompt_tokens"":4250,
                                           ""completion_tokens"":310,
                                           ""total_tokens"":4560,
                                           ""prompt_tokens_details"":{""cached_tokens"":4096}}}");

            var usage = ChatCompletionsUsage.Read(root);

            Assert.Equal(4250, usage.Input);
            Assert.Equal(310, usage.Output);
            Assert.Equal(4096, usage.CacheRead);
        }

        [Fact]
        public void Read_ExtractsReasoningTokens()
        {
            var root = Parse(@"{""usage"":{""completion_tokens"":900,
                                           ""completion_tokens_details"":{""reasoning_tokens"":612}}}");

            var usage = ChatCompletionsUsage.Read(root);

            Assert.Equal(612, usage.Thinking);
        }

        // 이 규격에는 캐시 쓰기를 담을 칸이 아예 없다. 0으로 적으면 "재보니 캐시를
        // 쓰지 않았다"는 측정값으로 읽히는데, 사실은 이 봉투가 그 값을 말하지 않는
        // 것이다. hy4 의 캐시를 오독한 자리가 정확히 이 구분이었다.
        [Fact]
        public void Read_MarksCacheWriteUnreported_BecauseTheEnvelopeHasNoFieldForIt()
        {
            var root = Parse(@"{""usage"":{""prompt_tokens"":4250,""completion_tokens"":310}}");

            var usage = ChatCompletionsUsage.Read(root);

            Assert.Null(usage.CacheWrite);
        }

        [Fact]
        public void Read_WithoutAUsageObject_ReportsNothing()
        {
            var root = Parse(@"{""choices"":[]}");

            var usage = ChatCompletionsUsage.Read(root);

            Assert.Null(usage.Input);
            Assert.Null(usage.Output);
            Assert.Null(usage.CacheRead);
            Assert.Null(usage.CacheWrite);
            Assert.Null(usage.Thinking);
        }

        // 캐시를 안 태운 요청에서 cached_tokens 는 0 으로 온다. 이것은 측정값이므로
        // "미보고"와 달라야 한다 - 둘이 같아 보이면 캐시가 죽은 날과 봉투가 바뀐 날을
        // 구별할 수 없다.
        [Fact]
        public void Read_WithZeroCachedTokens_KeepsItAsAMeasurement()
        {
            var root = Parse(@"{""usage"":{""prompt_tokens"":4250,
                                           ""prompt_tokens_details"":{""cached_tokens"":0}}}");

            var usage = ChatCompletionsUsage.Read(root);

            Assert.Equal(0, usage.CacheRead);
        }

        [Theory]
        [InlineData(@"{""usage"":null}")]
        [InlineData(@"{""usage"":7}")]
        [InlineData(@"{""usage"":{""prompt_tokens"":""4250""}}")]
        [InlineData(@"{""usage"":{""prompt_tokens_details"":null}}")]
        public void Read_WithMalformedEnvelope_ReportsNothingWithoutThrowing(string json)
        {
            var root = Parse(json);

            var usage = ChatCompletionsUsage.Read(root);

            Assert.Null(usage.Input);
            Assert.Null(usage.CacheRead);
        }
    }
}
