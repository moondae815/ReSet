using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class ClaudeCliClientTests
    {
        // 2026-08-03에 `claude -p --output-format json`을 실제로 호출해 받은 응답을 줄인 것.
        private const string SuccessJson =
            "{\"is_error\":false,\"num_turns\":1,\"session_id\":\"abc\",\"total_cost_usd\":0.042," +
            "\"subtype\":\"success\",\"api_error_status\":null,\"result\":\"PONG\",\"type\":\"result\"}";

        [Fact]
        public void BuildArguments_AlwaysDisablesToolsAndUsesJsonOutput()
        {
            var arguments = ClaudeCliClient.BuildArguments("sonnet", "high", "/tmp/sys.txt");

            Assert.Contains("-p", arguments);
            Assert.Contains("--output-format", arguments);
            Assert.Contains("json", arguments);
            Assert.Contains("--disable-slash-commands", arguments);
            Assert.Contains("--no-session-persistence", arguments);

            // 순수 LLM처럼 쓰기 위해 모든 툴을 끈다. --tools 다음 인자는 빈 문자열이다.
            var toolsIndex = arguments.ToList().IndexOf("--tools");
            Assert.True(toolsIndex >= 0);
            Assert.Equal(string.Empty, arguments[toolsIndex + 1]);
        }

        // 기본 시스템 프롬프트를 '추가'가 아니라 '교체'해야 한다.
        // 실측: append는 호출당 10,186 토큰, 교체는 1,451 토큰.
        [Fact]
        public void BuildArguments_ReplacesSystemPromptViaFile()
        {
            var arguments = ClaudeCliClient.BuildArguments("sonnet", null, "/tmp/sys.txt");

            var index = arguments.ToList().IndexOf("--system-prompt-file");
            Assert.True(index >= 0);
            Assert.Equal("/tmp/sys.txt", arguments[index + 1]);
            Assert.DoesNotContain("--append-system-prompt", arguments);
        }

        [Fact]
        public void BuildArguments_WithEffort_AppendsEffortFlag()
        {
            var arguments = ClaudeCliClient.BuildArguments("sonnet", "xhigh", "/tmp/sys.txt");

            var index = arguments.ToList().IndexOf("--effort");
            Assert.True(index >= 0);
            Assert.Equal("xhigh", arguments[index + 1]);
        }

        [Fact]
        public void BuildArguments_WithoutEffort_OmitsEffortFlag()
        {
            var arguments = ClaudeCliClient.BuildArguments("sonnet", null, "/tmp/sys.txt");
            Assert.DoesNotContain("--effort", arguments);
        }

        [Fact]
        public void BuildArguments_WithBlankModel_OmitsModelFlag()
        {
            var arguments = ClaudeCliClient.BuildArguments("", null, "/tmp/sys.txt");
            Assert.DoesNotContain("--model", arguments);
        }

        [Fact]
        public void ParseResponse_Success_ExtractsResultText()
        {
            var response = ClaudeCliClient.ParseResponse(SuccessJson);

            Assert.False(response.IsError);
            Assert.Equal("PONG", response.Result);
        }

        [Fact]
        public void ParseResponse_ErrorPayload_ExposesSubtypeAndStatus()
        {
            const string errorJson =
                "{\"is_error\":true,\"subtype\":\"error_max_turns\"," +
                "\"api_error_status\":\"rate_limit_error\",\"result\":null,\"type\":\"result\"}";

            var response = ClaudeCliClient.ParseResponse(errorJson);

            Assert.True(response.IsError);
            Assert.Equal("error_max_turns", response.Subtype);
            Assert.Equal("rate_limit_error", response.ApiErrorStatus);
        }

        [Fact]
        public void ParseResponse_NotJson_ThrowsInvalidOperationException()
        {
            Assert.Throws<InvalidOperationException>(() =>
                ClaudeCliClient.ParseResponse("이건 JSON이 아니다"));
        }

        [Fact]
        public void ProviderNameModelNameAndTimeout_AreExposed()
        {
            var client = new ClaudeCliClient("claude", "sonnet", TimeSpan.FromSeconds(30));

            Assert.Equal("claude-cli", client.ProviderName);
            Assert.Equal("sonnet", client.ModelName);
            Assert.Equal(30, client.Timeout.TotalSeconds);
        }

        // ProviderName이 로컬 프로바이더로 오인되면 AiService가 로컬 분할 파이프라인을
        // 켠다. CLI provider는 그 대상이 아니다.
        [Fact]
        public void ProviderName_IsNotTreatedAsLocalProvider()
        {
            var client = new ClaudeCliClient("claude", "sonnet", TimeSpan.FromSeconds(30));

            Assert.False(ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(client.ProviderName));
        }

        [Fact]
        public async Task ChatAsync_MissingCommand_ThrowsWithInstallGuidance()
        {
            var client = new ClaudeCliClient(
                "reset_claude_does_not_exist_42", "sonnet", TimeSpan.FromSeconds(10));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            Assert.Contains("PATH", exception.Message);
        }
    }
}
