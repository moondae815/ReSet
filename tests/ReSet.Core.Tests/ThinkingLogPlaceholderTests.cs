using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class ThinkingLogPlaceholderTests
    {
        // CLI 제공자는 추론을 수행한다. claude-cli를 stream-json으로 띄우면 thinking 블록이
        // signature까지 달고 오지만 본문 길이는 0이고, 표시 방식을 바꿀 인자가 없다.
        // codex-cli도 --json / show_raw_agent_reasoning 어느 쪽으로도 추론 이벤트를 내보내지 않는다.
        // 이 상황을 "추론 비활성화"로 적으면 Effort 설정이 먹지 않은 것으로 오독된다.
        [Theory]
        [InlineData("claude-cli")]
        [InlineData("codex-cli")]
        [InlineData("agy-cli")]
        public void CliProvider_StatesThatThinkingTextIsNotExposed(string provider)
        {
            var placeholder = ThinkingLogPlaceholder.For(provider);

            Assert.Contains("CLI", placeholder);
            Assert.DoesNotContain("추론 비활성화", placeholder);
        }

        // API 제공자에서 추론이 비어 있으면 실제로 꺼져 있거나 미지원인 경우다. 문구를 유지한다.
        [Theory]
        [InlineData("Claude")]
        [InlineData("OpenAI")]
        [InlineData("Ollama")]
        public void ApiProvider_KeepsDisabledOrUnsupportedWording(string provider)
        {
            var placeholder = ThinkingLogPlaceholder.For(provider);

            Assert.Contains("추론 비활성화", placeholder);
        }

        [Fact]
        public void UnknownProvider_FallsBackToDisabledOrUnsupportedWording()
        {
            Assert.Contains("추론 비활성화", ThinkingLogPlaceholder.For(null));
            Assert.Contains("추론 비활성화", ThinkingLogPlaceholder.For(string.Empty));
        }
    }
}
