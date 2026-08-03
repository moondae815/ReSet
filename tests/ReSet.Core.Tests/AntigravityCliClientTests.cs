using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class AntigravityCliClientTests
    {
        // 2026-08-03에 `agy -p --output-format json`을 실제로 호출해 받은 응답.
        private const string SuccessJson =
            "{\"conversation_id\":\"7d1a7000\",\"status\":\"SUCCESS\",\"response\":\"PONG\\n\"," +
            "\"duration_seconds\":3.32,\"num_turns\":1}";

        [Fact]
        public void BuildArguments_PassesPromptAsArgumentNotStdin()
        {
            // agy는 stdin으로 프롬프트를 받지 못한다 (실측: 툴 권한 오류로 빈 응답).
            var arguments = AntigravityCliClient.BuildArguments(
                "프롬프트 본문", "gemini", "high", TimeSpan.FromSeconds(600));

            var index = arguments.ToList().IndexOf("-p");
            Assert.True(index >= 0);
            Assert.Equal("프롬프트 본문", arguments[index + 1]);
        }

        [Fact]
        public void BuildArguments_RequestsJsonOutputAndPassesTimeout()
        {
            var arguments = AntigravityCliClient.BuildArguments(
                "본문", "gemini", null, TimeSpan.FromSeconds(600));

            var formatIndex = arguments.ToList().IndexOf("--output-format");
            Assert.True(formatIndex >= 0);
            Assert.Equal("json", arguments[formatIndex + 1]);

            var timeoutIndex = arguments.ToList().IndexOf("--print-timeout");
            Assert.True(timeoutIndex >= 0);
            Assert.Equal("600s", arguments[timeoutIndex + 1]);
        }

        [Fact]
        public void BuildArguments_XhighEffort_IsClampedToHigh()
        {
            var arguments = AntigravityCliClient.BuildArguments(
                "본문", "gemini", "xhigh", TimeSpan.FromSeconds(600));

            var index = arguments.ToList().IndexOf("--effort");
            Assert.Equal("high", arguments[index + 1]);
        }

        [Fact]
        public void BuildArguments_WithBlankModel_OmitsModelFlag()
        {
            var arguments = AntigravityCliClient.BuildArguments(
                "본문", "", null, TimeSpan.FromSeconds(600));

            Assert.DoesNotContain("--model", arguments);
        }

        [Fact]
        public void MaxCommandLineLength_MatchesPlatformLimit()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Equal(32_767, AntigravityCliClient.MaxCommandLineLength);
            }
            else
            {
                Assert.True(AntigravityCliClient.MaxCommandLineLength > 100_000);
            }
        }

        [Fact]
        public void EnsureCommandLineFits_ShortPrompt_DoesNotThrow()
        {
            var arguments = AntigravityCliClient.BuildArguments(
                "짧은 본문", "gemini", null, TimeSpan.FromSeconds(600));

            AntigravityCliClient.EnsureCommandLineFits("agy", arguments);
        }

        // ReSet의 실제 최대 프롬프트는 191KB다. Windows 32KB 한계를 넘는다.
        [Fact]
        public void EnsureCommandLineFits_OverLimit_ThrowsWithActionableMessage()
        {
            var huge = new string('가', AntigravityCliClient.MaxCommandLineLength + 1000);
            var arguments = new List<string> { "-p", huge };

            var exception = Assert.Throws<InvalidOperationException>(() =>
                AntigravityCliClient.EnsureCommandLineFits("agy", arguments));

            Assert.Contains("agy-cli", exception.Message);
            Assert.Contains("claude-cli", exception.Message);
        }

        [Fact]
        public void ParseResult_Success_ExtractsResponseText()
        {
            Assert.Equal("PONG", AntigravityCliClient.ParseResult(SuccessJson).Trim());
        }

        [Fact]
        public void ParseResult_NonSuccessStatus_Throws()
        {
            const string failureJson =
                "{\"conversation_id\":\"x\",\"status\":\"ERROR\",\"response\":\"\"}";

            Assert.Throws<InvalidOperationException>(() =>
                AntigravityCliClient.ParseResult(failureJson));
        }

        [Fact]
        public void ParseResult_NotJson_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                AntigravityCliClient.ParseResult("이건 JSON이 아니다"));
        }

        [Fact]
        public void ProviderNameModelNameAndTimeout_AreExposed()
        {
            var client = new AntigravityCliClient("agy", "gemini", TimeSpan.FromSeconds(30));

            Assert.Equal("agy-cli", client.ProviderName);
            Assert.Equal("gemini", client.ModelName);
            Assert.Equal(30, client.Timeout.TotalSeconds);
        }

        [Fact]
        public async Task ChatAsync_MissingCommand_ThrowsWithInstallGuidance()
        {
            var client = new AntigravityCliClient(
                "reset_agy_does_not_exist_42", "gemini", TimeSpan.FromSeconds(10));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            Assert.Contains("PATH", exception.Message);
        }
    }
}
