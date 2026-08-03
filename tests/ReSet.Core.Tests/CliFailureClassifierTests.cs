using System;
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class CliFailureClassifierTests
    {
        private static CliProcessResult Failed(string standardError) => new()
        {
            ExitCode = 1,
            StandardError = standardError,
            TimedOut = false
        };

        [Fact]
        public void Classify_TimedOut_ReturnsTimeout()
        {
            var result = new CliProcessResult { ExitCode = -1, TimedOut = true };
            Assert.Equal(CliFailureKind.Timeout, CliFailureClassifier.Classify(result, null));
        }

        [Theory]
        [InlineData("Claude usage limit reached. Your limit will reset at 3pm.")]
        [InlineData("You have exceeded your quota for this month")]
        [InlineData("rate_limit_error: too many requests")]
        [InlineData("HTTP 429 Too Many Requests")]
        public void Classify_QuotaMessages_ReturnsQuotaExhausted(string standardError)
        {
            Assert.Equal(CliFailureKind.QuotaExhausted,
                CliFailureClassifier.Classify(Failed(standardError), null));
        }

        [Theory]
        [InlineData("Not logged in. Please run `claude login`.")]
        [InlineData("401 Unauthorized")]
        [InlineData("Authentication failed")]
        [InlineData("No credentials found")]
        public void Classify_AuthMessages_ReturnsNotAuthenticated(string standardError)
        {
            Assert.Equal(CliFailureKind.NotAuthenticated,
                CliFailureClassifier.Classify(Failed(standardError), null));
        }

        [Fact]
        public void Classify_QuotaWinsOverAuth_WhenBothPresent()
        {
            // 쿼터 소진 안내문에 "login" 같은 단어가 섞이는 경우가 있다.
            // 쿼터가 더 구체적인 진단이므로 먼저 본다.
            var result = Failed("usage limit reached; please login again later");
            Assert.Equal(CliFailureKind.QuotaExhausted, CliFailureClassifier.Classify(result, null));
        }

        [Fact]
        public void Classify_ExtraDetailIsInspected()
        {
            // claude는 종료 코드 0으로 끝내면서 JSON 안에만 오류를 담을 수 있다.
            var result = new CliProcessResult { ExitCode = 0 };
            Assert.Equal(CliFailureKind.QuotaExhausted,
                CliFailureClassifier.Classify(result, "rate_limit_error"));
        }

        [Fact]
        public void Classify_UnrecognizedMessage_ReturnsUnknown()
        {
            Assert.Equal(CliFailureKind.Unknown,
                CliFailureClassifier.Classify(Failed("segmentation fault"), null));
        }

        [Fact]
        public void ToException_QuotaExhausted_MentionsProviderSwitch()
        {
            var exception = CliFailureClassifier.ToException(
                "claude-cli", "claude", Failed("usage limit reached"), null);

            Assert.Contains("claude-cli", exception.Message);
            Assert.Contains("구독", exception.Message);
        }

        // 분류를 못 맞혔을 때도 진단이 가능해야 한다. stderr 원문을 자르지 않는다.
        [Fact]
        public void ToException_AlwaysIncludesRawStandardError()
        {
            var exception = CliFailureClassifier.ToException(
                "codex-cli", "codex", Failed("something nobody predicted"), null);

            Assert.Contains("something nobody predicted", exception.Message);
        }

        [Fact]
        public void CommandNotFound_MentionsCommandAndPath()
        {
            var exception = CliFailureClassifier.CommandNotFound(
                "agy-cli", "agy", new InvalidOperationException("no such file"));

            Assert.Contains("agy", exception.Message);
            Assert.Contains("PATH", exception.Message);
            Assert.NotNull(exception.InnerException);
        }
    }
}
