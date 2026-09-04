using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdPromptTests
    {
        private static (AiService Service, IAiClient Client) Build()
        {
            var client = Substitute.For<IAiClient>();
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "## 배경 및 목적" }));

            // 생성자 인자는 src/ReSet.Cli/Program.cs:606의 실제 생성 구문과 같은 순서다.
            // temperature는 float이고 contextScope는 string?(설정 값)이다.
            var service = new AiService(client, 0.2f, false, 8, true, null);
            return (service, client);
        }

        [Fact]
        public async Task GeneratePrdFromSpecAsync_ShouldCarryEverySectionAndItsAllowedSources()
        {
            var (service, _) = Build();

            var result = await service.GeneratePrdFromSpecAsync("dbo.UP_TEST", "## 개요\n\n본문", null, null, CancellationToken.None);

            // Nullable enable 하에서 AiResult.SystemPrompt는 string?다.
            // Assert.Contains(string, string)에 곧장 넘기면 CS8604가 나므로 먼저 NotNull로 좁힌다.
            Assert.NotNull(result.SystemPrompt);
            var systemPrompt = result.SystemPrompt!;

            foreach (var rule in PrdSectionContract.Sections)
            {
                Assert.Contains(rule.Heading, systemPrompt);
                Assert.Contains(rule.IdPrefix, systemPrompt);
                foreach (var source in rule.AllowedSources)
                {
                    Assert.Contains(source, systemPrompt);
                }
            }
        }

        [Fact]
        public async Task GeneratePrdFromSpecAsync_ShouldForbidEvidenceOutsideTheSpec()
        {
            var (service, _) = Build();

            var result = await service.GeneratePrdFromSpecAsync("dbo.UP_TEST", "## 개요\n\n본문", null, null, CancellationToken.None);

            Assert.NotNull(result.SystemPrompt);
            var systemPrompt = result.SystemPrompt!;

            Assert.Contains("도출", systemPrompt);
            Assert.Contains("추정", systemPrompt);
            Assert.Contains("verbatim", systemPrompt);
        }

        [Fact]
        public async Task GeneratePrdFromSpecAsync_ShouldCarryTheAttributionFeedback_WhenRetrying()
        {
            var (service, _) = Build();

            var result = await service.GeneratePrdFromSpecAsync(
                "dbo.UP_TEST", "## 개요\n\n본문", "REQ-DATA-01: 인용을 찾을 수 없습니다.", null, CancellationToken.None);

            Assert.NotNull(result.UserPrompt);
            var userPrompt = result.UserPrompt!;

            Assert.Contains("REQ-DATA-01", userPrompt);
        }
    }
}
