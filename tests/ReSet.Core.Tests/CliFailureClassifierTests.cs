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

        // 2026-08-04에 agy 1.1.10을 빈 임시 디렉터리에서 실제로 호출해 받은 stderr.
        // agy는 툴을 끄는 인자가 없어 모델이 툴을 잡으면 이 경로를 밟는다.
        // 종료 코드는 0, stdout의 status도 SUCCESS인데 response만 비어 있어,
        // 분류가 없으면 "호출이 실패했습니다 (종료 코드: 0)"라는 자기모순 문구가 남는다.
        private const string AgyPermissionDeniedStderr =
            "jetski: no output produced — a tool required the \"command\" permission that " +
            "headless mode cannot prompt for, so it was auto-denied. Add an allow-rule under " +
            "permissions.allow in settings.json (e.g. command(<target>)). Alternatively, " +
            "re-run with --dangerously-skip-permissions to auto-approve all tools.";

        [Fact]
        public void Classify_HeadlessToolPermissionDenied_ReturnsToolPermissionDenied()
        {
            var result = new CliProcessResult { ExitCode = 0, StandardError = AgyPermissionDeniedStderr };

            Assert.Equal(CliFailureKind.ToolPermissionDenied,
                CliFailureClassifier.Classify(result, null));
        }

        [Fact]
        public void ToException_ToolPermissionDenied_ExplainsWhyAndNamesAWorkingProvider()
        {
            var result = new CliProcessResult { ExitCode = 0, StandardError = AgyPermissionDeniedStderr };

            var exception = CliFailureClassifier.ToException("agy-cli", "agy", result, null);

            Assert.Contains("툴 권한", exception.Message);
            Assert.Contains("claude-cli", exception.Message);
            // 종료 코드 0을 실패로 부르는 자기모순 문구가 헤드라인에 남으면 안 된다.
            Assert.DoesNotContain("종료 코드: 0", exception.Message);
        }

        // 이 분류의 haystack에는 extraDetail을 통해 agy의 stdout 전문이 들어온다.
        // ReSet의 도메인은 정산 프로시저이고 GRANT/DENY 같은 권한 구문과 "권한"이라는
        // 단어는 명세서 본문에 일상적으로 등장한다. 일반 단어로 매칭하면 멀쩡한 분석이
        // "툴 권한 거부"로 오진된다. 마커는 agy 안내문 고유 문구로만 잡는다.
        [Theory]
        [InlineData("이 프로시저는 실행 권한이 필요합니다. permission denied on TSettleMst.")]
        [InlineData("GRANT EXECUTE 권한을 부여해야 합니다.")]
        public void Classify_AnalysisTextMentioningPermissions_IsNotMisreadAsToolDenial(string text)
        {
            var result = new CliProcessResult { ExitCode = 0 };

            Assert.NotEqual(CliFailureKind.ToolPermissionDenied,
                CliFailureClassifier.Classify(result, text));
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

        // Finding 2: BuildAbortReason(CodegenWorkflowOrchestrator)이 재사용하던 ToException은
        // 분석 경로(AiSettings:Providers:*)용 구제책 문구를 담고 있었다. 코딩 에이전트
        // 브릿지에서는 CodegenSettings:Engine을 가리켜야 하고, "claude-cli 또는 API provider로
        // 변경하십시오" 같은 문구는 애초에 코딩 에이전트 브릿지에는 툴이 켜져 있는 것이
        // 정상이라는 전제와 모순된다.
        [Fact]
        public void ToCodegenAbortException_ToolPermissionDenied_PointsAtCodegenSettingsEngine()
        {
            var exception = CliFailureClassifier.ToCodegenAbortException(
                "agy", "agy", CliFailureKind.ToolPermissionDenied, exitCode: 0, diagnostic: AgyPermissionDeniedStderr);

            Assert.Contains("CodegenSettings:Engines:agy:BatchArguments", exception.Message);
            Assert.Contains("CodegenSettings:Engine", exception.Message);
            // 분석 경로 전용 구제책("claude-cli 또는 API provider로 변경")이 새어 들어오면
            // codegen에서는 claude-cli가 유효한 CodegenSettings:Engine 값이 아니므로 오도한다.
            Assert.DoesNotContain("API provider로 변경하십시오", exception.Message);
            Assert.DoesNotContain("종료 코드: 0", exception.Message);
        }

        [Fact]
        public void ToCodegenAbortException_QuotaExhausted_PointsAtCodegenSettingsEngine()
        {
            var exception = CliFailureClassifier.ToCodegenAbortException(
                "codex", "codex", CliFailureKind.QuotaExhausted, exitCode: 1, diagnostic: "usage limit reached");

            Assert.Contains("CodegenSettings:Engine", exception.Message);
            Assert.DoesNotContain("다른 CLI provider 또는 API provider로 변경", exception.Message);
        }

        [Fact]
        public void ToCodegenAbortException_DoesNotReclassify_UsesGivenKindAsIs()
        {
            // diagnostic이 null(대화형 실행)이어도 이미 알려진 FailureKind를 그대로 써야 한다.
            // 여기서 다시 Classify를 돌리면 빈 문자열에서 Unknown만 나온다.
            var exception = CliFailureClassifier.ToCodegenAbortException(
                "claude", "claude", CliFailureKind.NotAuthenticated, exitCode: 1, diagnostic: null);

            Assert.Contains("로그인되어 있지 않습니다", exception.Message);
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
