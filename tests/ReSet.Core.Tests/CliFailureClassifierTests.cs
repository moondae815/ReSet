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

        // stdout은 haystack에서 빠져야 한다. codex exec는 프롬프트와 추론을 stdout으로
        // 흘리고 agy/claude는 답변 본문을 stdout JSON으로 돌려준다. 이 저장소의 도메인은
        // 정산 프로시저라 "한도"(거래 한도, 결제 한도)와 "사용량"이 일상 어휘다.
        [Fact]
        public void Classify_KoreanPromptWithLimitVocabularyOnStdout_IsNotQuota()
        {
            var result = new CliProcessResult
            {
                ExitCode = 1,
                StandardOutput =
                    "프롬프트: 거래 한도와 결제 한도를 검증하고 일별 사용량을 집계하는 프로시저입니다.",
                StandardError = "unexpected end of stream",
                TimedOut = false
            };

            Assert.NotEqual(CliFailureKind.QuotaExhausted,
                CliFailureClassifier.Classify(result, null));
            Assert.Equal(CliFailureKind.Unknown, CliFailureClassifier.Classify(result, null));
        }

        // 오진의 실제 피해 시나리오: 인증 실패인데 stdout의 "한도" 때문에 쿼터로 분류되면
        // 사용자는 로그인 대신 provider를 갈아엎는다.
        [Fact]
        public void Classify_AuthFailureWithLimitVocabularyOnStdout_StaysNotAuthenticated()
        {
            var result = new CliProcessResult
            {
                ExitCode = 1,
                StandardOutput = "결제 한도 계산 로직을 설명하십시오.",
                StandardError = "Not logged in. Please run `codex login`.",
                TimedOut = false
            };

            Assert.Equal(CliFailureKind.NotAuthenticated,
                CliFailureClassifier.Classify(result, null));
        }

        // stdout 자체는 보지 않지만, 클라이언트가 stdout 안의 오류를 extraDetail로
        // 명시해 넘기면 분류는 그대로 살아 있어야 한다 (agy의 종료 코드 0 실패).
        [Fact]
        public void Classify_StdoutErrorRoutedThroughExtraDetail_IsStillClassified()
        {
            var result = new CliProcessResult
            {
                ExitCode = 0,
                StandardOutput = "{\"status\":\"ERROR\",\"error\":\"usage limit reached\"}",
                TimedOut = false
            };

            Assert.Equal(CliFailureKind.QuotaExhausted,
                CliFailureClassifier.Classify(result, result.StandardOutput));
        }

        [Fact]
        public void Classify_UnrecognizedMessage_ReturnsUnknown()
        {
            Assert.Equal(CliFailureKind.Unknown,
                CliFailureClassifier.Classify(Failed("segmentation fault"), null));
        }

        [Theory]
        [InlineData("child process exited with code 14293")]
        [InlineData("retry backoff 40100ms")]
        public void Classify_NumericMarkerWithoutWordBoundary_ReturnsUnknown(string standardError)
        {
            // "429"와 "401"은 숫자로만 된 마커다. "14293"이나 "40100"처럼 더 긴 숫자의
            // 일부로 나타나면 오진이므로 앞뒤가 숫자가 아닐 때만 매칭되어야 한다.
            Assert.Equal(CliFailureKind.Unknown,
                CliFailureClassifier.Classify(Failed(standardError), null));
        }

        [Fact]
        public void Classify_NumericMarker_ScansPastEarlierBoundaryFailure()
        {
            // 앞부분의 "14293"은 경계 검사에 실패하지만, 뒤에 독립된 "429"가 있으므로
            // 검색이 첫 실패에서 멈추지 않고 계속되어야 한다.
            var result = Failed("code 14293; http 429");
            Assert.Equal(CliFailureKind.QuotaExhausted, CliFailureClassifier.Classify(result, null));
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

        // codex는 진행 로그를 stderr로 흘리므로 stderr가 비는 일이 드물다. 둘 중
        // 하나만 실으면 "codex가 결과 파일을 남기지 않았습니다" 같은 가장 구체적인
        // 진단이 진행 로그에 밀려 사라지고, 이유 없는 "종료 코드: 0 실패"만 남는다.
        [Fact]
        public void ToException_BothStandardErrorAndExtraDetail_AreIncluded()
        {
            var result = new CliProcessResult
            {
                ExitCode = 0,
                StandardError = "[2026-08-03] thinking... tokens used 1200",
                TimedOut = false
            };

            var exception = CliFailureClassifier.ToException(
                "codex-cli", "codex", result, "codex가 결과 파일을 남기지 않았습니다.");

            Assert.Contains("thinking... tokens used 1200", exception.Message);
            Assert.Contains("codex가 결과 파일을 남기지 않았습니다.", exception.Message);
        }

        [Fact]
        public void ToException_ExtraDetailOnly_IsIncluded()
        {
            var result = new CliProcessResult { ExitCode = 0 };

            var exception = CliFailureClassifier.ToException(
                "codex-cli", "codex", result, "codex가 빈 응답을 반환했습니다.");

            Assert.Contains("codex가 빈 응답을 반환했습니다.", exception.Message);
        }

        [Fact]
        public void ToException_NeitherDetailPresent_IsSummaryOnly()
        {
            var result = new CliProcessResult { ExitCode = 4 };

            var exception = CliFailureClassifier.ToException("agy-cli", "agy", result, null);

            Assert.Contains("종료 코드: 4", exception.Message);
            Assert.DoesNotContain("[CLI 출력]", exception.Message);
            Assert.DoesNotContain("[추가 진단]", exception.Message);
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
