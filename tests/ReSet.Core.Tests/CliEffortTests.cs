using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class CliEffortTests
    {
        [Theory]
        [InlineData("low", "low")]
        [InlineData("medium", "medium")]
        [InlineData("high", "high")]
        [InlineData("xhigh", "xhigh")]
        [InlineData("HIGH", "high")]
        public void ForClaude_KnownLevels_PassThrough(string input, string expected)
        {
            Assert.Equal(expected, CliEffort.ForClaude(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("dynamic")]
        public void ForClaude_UnknownOrBlank_ReturnsNull(string? input)
        {
            // null이면 호출자가 --effort 플래그를 붙이지 않고 CLI 기본값을 따른다.
            Assert.Null(CliEffort.ForClaude(input));
        }

        [Theory]
        [InlineData("low", "low")]
        [InlineData("medium", "medium")]
        [InlineData("high", "high")]
        public void ForThreeLevel_WithinRange_NotClamped(string input, string expected)
        {
            var result = CliEffort.ForThreeLevel(input, out var clamped);
            Assert.Equal(expected, result);
            Assert.False(clamped);
        }

        [Theory]
        [InlineData("xhigh")]
        [InlineData("max")]
        public void ForThreeLevel_AboveRange_ClampsToHigh(string input)
        {
            var result = CliEffort.ForThreeLevel(input, out var clamped);
            Assert.Equal("high", result);
            Assert.True(clamped);
        }

        [Fact]
        public void ForThreeLevel_Unknown_ReturnsNullAndNotClamped()
        {
            var result = CliEffort.ForThreeLevel("dynamic", out var clamped);
            Assert.Null(result);
            Assert.False(clamped);
        }
    }
}
