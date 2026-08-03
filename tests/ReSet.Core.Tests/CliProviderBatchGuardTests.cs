using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class CliProviderBatchGuardTests
    {
        [Fact]
        public void FindBlockedRole_AllApiProviders_ReturnsNull()
        {
            Assert.Null(CliProviderBatchGuard.FindBlockedRole("Claude", "OpenAI", "Claude"));
        }

        [Fact]
        public void FindBlockedRole_ActorIsCli_ReturnsActor()
        {
            Assert.Equal("Actor",
                CliProviderBatchGuard.FindBlockedRole("claude-cli", "OpenAI", "Claude"));
        }

        // Actor가 API여도 Critic이 CLI면 같은 사고가 난다. 세 역할을 모두 봐야 한다.
        [Fact]
        public void FindBlockedRole_CriticIsCli_ReturnsCritic()
        {
            Assert.Equal("Critic",
                CliProviderBatchGuard.FindBlockedRole("Claude", "codex-cli", "Claude"));
        }

        [Fact]
        public void FindBlockedRole_ConsolidatorIsCli_ReturnsConsolidator()
        {
            Assert.Equal("Consolidator",
                CliProviderBatchGuard.FindBlockedRole("Claude", "OpenAI", "agy-cli"));
        }

        // Critic/Consolidator를 지정하지 않으면 Actor 설정을 물려받는다.
        [Fact]
        public void FindBlockedRole_NullRoleProviders_FallBackToActor()
        {
            Assert.Equal("Actor",
                CliProviderBatchGuard.FindBlockedRole("claude-cli", null, null));
        }

        [Fact]
        public void FindBlockedRole_NullRoleProvidersWithApiActor_ReturnsNull()
        {
            Assert.Null(CliProviderBatchGuard.FindBlockedRole("Claude", null, null));
        }
    }
}
