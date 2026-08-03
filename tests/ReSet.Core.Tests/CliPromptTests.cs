using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class CliPromptTests
    {
        [Fact]
        public void Combine_JoinsSystemAndUserPrompt()
        {
            var combined = CliPrompt.Combine("규칙입니다", "본문입니다");

            Assert.StartsWith("규칙입니다", combined);
            Assert.EndsWith("본문입니다", combined);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Combine_BlankSystemPrompt_ReturnsUserPromptOnly(string systemPrompt)
        {
            Assert.Equal("본문입니다", CliPrompt.Combine(systemPrompt, "본문입니다"));
        }
    }
}
