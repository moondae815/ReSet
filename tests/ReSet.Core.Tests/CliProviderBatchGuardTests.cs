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

        // 옵트인을 명시적으로 끈 호출은 인자를 생략한 호출과 같아야 한다.
        // 기본값이 조용히 뒤집히면 CI가 보호막 없이 돌게 된다.
        [Fact]
        public void FindBlockedRole_OptInDisabled_StillBlocksCliProvider()
        {
            Assert.Equal("Critic",
                CliProviderBatchGuard.FindBlockedRole("Claude", "claude-cli", "Claude", false));
        }

        [Theory]
        [InlineData("claude-cli")]
        [InlineData("codex-cli")]
        public void FindBlockedRole_OptInEnabled_AllowsActorCli(string provider)
        {
            Assert.Null(
                CliProviderBatchGuard.FindBlockedRole(provider, "OpenAI", "Claude", true));
        }

        [Theory]
        [InlineData("claude-cli")]
        [InlineData("codex-cli")]
        public void FindBlockedRole_OptInEnabled_AllowsCriticCli(string provider)
        {
            Assert.Null(
                CliProviderBatchGuard.FindBlockedRole("Claude", provider, "Claude", true));
        }

        [Theory]
        [InlineData("claude-cli")]
        [InlineData("codex-cli")]
        public void FindBlockedRole_OptInEnabled_AllowsConsolidatorCli(string provider)
        {
            Assert.Null(
                CliProviderBatchGuard.FindBlockedRole("Claude", "OpenAI", provider, true));
        }

        // agy-cli는 툴 22종을 끌 수단이 없어 헤드리스에서 자동 거부 후 빈 응답만 남긴다.
        // 배치 여부와 무관하게 분석 역할에서 깨지므로 옵트인으로도 열지 않는다.
        [Fact]
        public void FindBlockedRole_OptInEnabled_StillBlocksAgyActor()
        {
            Assert.Equal("Actor",
                CliProviderBatchGuard.FindBlockedRole("agy-cli", "OpenAI", "Claude", true));
        }

        [Fact]
        public void FindBlockedRole_OptInEnabled_StillBlocksAgyCritic()
        {
            Assert.Equal("Critic",
                CliProviderBatchGuard.FindBlockedRole("Claude", "agy-cli", "Claude", true));
        }

        [Fact]
        public void FindBlockedRole_OptInEnabled_StillBlocksAgyConsolidator()
        {
            Assert.Equal("Consolidator",
                CliProviderBatchGuard.FindBlockedRole("Claude", "OpenAI", "agy-cli", true));
        }

        // 역할 provider를 비워 두면 Actor를 물려받는 규칙은 옵트인 경로에서도 같다.
        [Fact]
        public void FindBlockedRole_OptInEnabled_AgyInheritedByRoles_ReturnsActor()
        {
            Assert.Equal("Actor",
                CliProviderBatchGuard.FindBlockedRole("agy-cli", null, null, true));
        }

        [Fact]
        public void FindBlockedRole_OptInEnabled_CliInheritedByRoles_ReturnsNull()
        {
            Assert.Null(CliProviderBatchGuard.FindBlockedRole("claude-cli", null, null, true));
        }

        [Fact]
        public void FindBlockedRole_OptInEnabled_AllApiProviders_ReturnsNull()
        {
            Assert.Null(CliProviderBatchGuard.FindBlockedRole("Claude", "OpenAI", "Claude", true));
        }

        // 옵트인으로 통과시킨 실행에도 경고를 남겨야 한다. 경고를 낼지 판정하는 것은
        // "차단할 역할"이 아니라 "CLI를 실제로 쓰는 역할"이므로 질문을 갈라 둔다.
        [Fact]
        public void FindCliRole_AllApiProviders_ReturnsNull()
        {
            Assert.Null(CliProviderBatchGuard.FindCliRole("Claude", "OpenAI", "Claude"));
        }

        [Theory]
        [InlineData("claude-cli")]
        [InlineData("codex-cli")]
        [InlineData("agy-cli")]
        public void FindCliRole_ActorIsCli_ReturnsActor(string provider)
        {
            Assert.Equal("Actor",
                CliProviderBatchGuard.FindCliRole(provider, "OpenAI", "Claude"));
        }

        [Fact]
        public void FindCliRole_CriticIsCli_ReturnsCritic()
        {
            Assert.Equal("Critic",
                CliProviderBatchGuard.FindCliRole("Claude", "claude-cli", "Claude"));
        }

        [Fact]
        public void FindCliRole_ConsolidatorIsCli_ReturnsConsolidator()
        {
            Assert.Equal("Consolidator",
                CliProviderBatchGuard.FindCliRole("Claude", "OpenAI", "codex-cli"));
        }

        [Fact]
        public void FindCliRole_NullRoleProviders_FallBackToActor()
        {
            Assert.Equal("Actor", CliProviderBatchGuard.FindCliRole("codex-cli", null, null));
        }
    }
}
