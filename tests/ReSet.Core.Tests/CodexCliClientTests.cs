using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class CodexCliClientTests
    {
        [Fact]
        public void BuildArguments_UsesNonInteractiveExecWithStdinAndReadOnlySandbox()
        {
            var arguments = CodexCliClient.BuildArguments("gpt-5.6-terra", "high", "/tmp/out.txt");

            Assert.Equal("exec", arguments[0]);
            // "-" 는 프롬프트를 stdin에서 읽으라는 뜻이다.
            Assert.Equal("-", arguments[1]);
            Assert.Contains("--skip-git-repo-check", arguments);
            Assert.Contains("--ephemeral", arguments);

            var sandboxIndex = arguments.ToList().IndexOf("--sandbox");
            Assert.True(sandboxIndex >= 0);
            Assert.Equal("read-only", arguments[sandboxIndex + 1]);
        }

        // stdout에는 진행 로그가 섞이므로 결과는 파일로 받는다.
        [Fact]
        public void BuildArguments_WritesLastMessageToFile()
        {
            var arguments = CodexCliClient.BuildArguments("gpt-5.6-terra", null, "/tmp/out.txt");

            var index = arguments.ToList().IndexOf("-o");
            Assert.True(index >= 0);
            Assert.Equal("/tmp/out.txt", arguments[index + 1]);
        }

        [Fact]
        public void BuildArguments_EffortIsPassedAsTomlConfigOverride()
        {
            var arguments = CodexCliClient.BuildArguments("gpt-5.6-terra", "medium", "/tmp/out.txt");

            var index = arguments.ToList().IndexOf("-c");
            Assert.True(index >= 0);
            // 값은 TOML로 파싱되므로 문자열은 따옴표로 감싼다.
            Assert.Equal("model_reasoning_effort=\"medium\"", arguments[index + 1]);
        }

        // codex는 low|medium|high만 받는다. ReSet의 xhigh는 낮춰야 한다.
        [Fact]
        public void BuildArguments_XhighEffort_IsClampedToHigh()
        {
            var arguments = CodexCliClient.BuildArguments("gpt-5.6-terra", "xhigh", "/tmp/out.txt");

            var index = arguments.ToList().IndexOf("-c");
            Assert.Equal("model_reasoning_effort=\"high\"", arguments[index + 1]);
        }

        [Fact]
        public void BuildArguments_WithoutEffort_OmitsConfigOverride()
        {
            var arguments = CodexCliClient.BuildArguments("gpt-5.6-terra", null, "/tmp/out.txt");
            Assert.DoesNotContain("-c", arguments);
        }

        [Fact]
        public void BuildArguments_WithBlankModel_OmitsModelFlag()
        {
            var arguments = CodexCliClient.BuildArguments("", null, "/tmp/out.txt");
            Assert.DoesNotContain("-m", arguments);
        }

        [Fact]
        public void ProviderNameModelNameAndTimeout_AreExposed()
        {
            var client = new CodexCliClient("codex", "gpt-5.6-terra", TimeSpan.FromSeconds(30));

            Assert.Equal("codex-cli", client.ProviderName);
            Assert.Equal("gpt-5.6-terra", client.ModelName);
            Assert.Equal(30, client.Timeout.TotalSeconds);
        }

        [Fact]
        public async Task ChatAsync_MissingCommand_ThrowsWithInstallGuidance()
        {
            var client = new CodexCliClient(
                "reset_codex_does_not_exist_42", "gpt-5.6-terra", TimeSpan.FromSeconds(10));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            Assert.Contains("PATH", exception.Message);
        }
    }
}
