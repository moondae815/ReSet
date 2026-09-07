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

        // OpenRouter 실물 봉투(2026-09-07 hy4 실행 로그에서 그대로 옮김). 이 규격에
        // 캐시 쓰기 칸이 없다고 단언했다가 틀렸다 - prompt_tokens_details 안에
        // cache_write_tokens 가 실려 온다. 명세 지식으로 세운 전제를 실물로 대조하지
        // 않은 자리였고, 이 픽스처가 그 대조를 대신한다.
        [Fact]
        public void Read_ExtractsCacheWriteTokens_FromARealOpenRouterEnvelope()
        {
            var root = Parse(@"{""usage"":{""prompt_tokens"":8837,""completion_tokens"":28665,
                ""total_tokens"":37502,""cost"":0.078655719,""is_byok"":false,
                ""prompt_tokens_details"":{""cached_tokens"":512,""cache_write_tokens"":0,
                                           ""audio_tokens"":0,""video_tokens"":0}}}");

            var usage = ChatCompletionsUsage.Read(root);

            Assert.Equal(8837, usage.Input);
            Assert.Equal(28665, usage.Output);
            Assert.Equal(512, usage.CacheRead);
            // 0 은 측정값이다. 미보고로 뭉개면 "이 봉투는 캐시 쓰기를 말하지 않는다"가
            // 되어, hy4 가 캐시를 쓰지 않는다는 실측 자체를 지운다.
            Assert.Equal(0, usage.CacheWrite);
        }

        // 칸이 실제로 없는 봉투에서만 미보고다.
        [Fact]
        public void Read_WithoutCacheWriteField_MarksItUnreported()
        {
            var root = Parse(@"{""usage"":{""prompt_tokens"":4250,""completion_tokens"":310,
                ""prompt_tokens_details"":{""cached_tokens"":0}}}");

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
