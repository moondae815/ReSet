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
            // 문자 수로도 바이트 수로도 확실히 넘는 크기.
            var huge = new string('가', AntigravityCliClient.MaxCommandLineLength + 1000);
            var arguments = new List<string> { "-p", huge };

            var exception = Assert.Throws<InvalidOperationException>(() =>
                AntigravityCliClient.EnsureCommandLineFits("agy", arguments));

            Assert.Contains("agy-cli", exception.Message);
            Assert.Contains("claude-cli", exception.Message);
        }

        // OS 한계는 바이트 단위다. UTF-16 문자 수로 재면 한글(UTF-8 3바이트) 프롬프트가
        // 검사를 통과한 뒤 execve가 E2BIG로 거절하고, 그것이 "명령을 찾지 못했습니다 -
        // PATH 확인" 이라는 엉뚱한 안내로 둔갑한다.
        [Fact]
        public void EnsureCommandLineFits_KoreanUnderCharLimitButOverByteLimit_Throws()
        {
            // 문자 수는 한계의 절반, 바이트 수는 한계의 1.5배.
            var korean = new string('가', AntigravityCliClient.MaxCommandLineLength / 2);
            Assert.True(korean.Length < AntigravityCliClient.MaxCommandLineLength);

            var arguments = new List<string> { "-p", korean };

            var exception = Assert.Throws<InvalidOperationException>(() =>
                AntigravityCliClient.EnsureCommandLineFits("agy", arguments));

            Assert.Contains("바이트", exception.Message);
        }

        // 경계의 반대편: 바이트로 재도 한계 아래인 한글 프롬프트는 통과해야 한다.
        [Fact]
        public void EnsureCommandLineFits_KoreanUnderByteLimit_DoesNotThrow()
        {
            var korean = new string('가', (AntigravityCliClient.MaxCommandLineLength / 3) - 100);
            var arguments = new List<string> { "-p", korean };

            AntigravityCliClient.EnsureCommandLineFits("agy", arguments);
        }

        [Fact]
        public void ParseResult_Success_ExtractsResponseText()
        {
            var response = AntigravityCliClient.ParseResult(SuccessJson);

            Assert.True(response.IsSuccess);
            Assert.Equal("PONG", response.Response?.Trim());
        }

        // ParseResult는 더 이상 실패를 스스로 예외로 만들지 않는다. 손으로 만든 예외는
        // CliFailureClassifier를 우회해 종류 판정과 provider 전환 안내를 잃기 때문이다.
        // 상태를 값으로 돌려주고, 예외 생성은 ChatAsync가 분류기를 통해 한다.
        [Fact]
        public void ParseResult_NonSuccessStatus_IsReportedAsValueNotException()
        {
            const string failureJson =
                "{\"conversation_id\":\"x\",\"status\":\"ERROR\",\"response\":\"\"}";

            var response = AntigravityCliClient.ParseResult(failureJson);

            Assert.False(response.IsSuccess);
            Assert.Equal("ERROR", response.Status);
        }

        // status/response가 문자열이 아닌 JSON 종류로 오면 GetString()이
        // InvalidOperationException을 던지는데, 그것은 catch (JsonException)에 걸리지
        // 않아 출력 덤프 없는 프레임워크 메시지로 새어 나갔다.
        [Fact]
        public void ParseResult_NonStringJsonKinds_AreGuardedNotThrown()
        {
            const string oddJson = "{\"status\":123,\"response\":{\"text\":\"hi\"}}";

            var response = AntigravityCliClient.ParseResult(oddJson);

            Assert.Null(response.Status);
            Assert.Null(response.Response);
            Assert.False(response.IsSuccess);
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

        // ---- 스텁으로 조립된 호출을 실제로 실행한다 ----
        // 진짜 agy 바이너리는 부르지 않는다.

        [Fact]
        public async Task ChatAsync_StubReturnsSuccessJson_ContentIsTheResponseText()
        {
            using var stub = CliStubScript.Create(
                posixBody: "echo '{\"status\":\"SUCCESS\",\"response\":\"PONG\"}'\n",
                windowsBody: "echo {\"status\":\"SUCCESS\",\"response\":\"PONG\"}\r\n");

            var client = new AntigravityCliClient(stub.Path, "gemini", TimeSpan.FromSeconds(60));

            var result = await client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f);

            Assert.Equal("PONG", result.Content);
        }

        // agy는 쿼터 소진을 종료 코드 0 + stdout JSON으로 보고한다. 분류기가 stdout을
        // 보지 않게 된 뒤에도 클라이언트가 원문 JSON을 extraDetail로 넘기므로
        // claude-cli와 같은 계약(종류 판정 + provider 전환 안내)이 유지되어야 한다.
        [Fact]
        public async Task ChatAsync_StubReportsQuotaInBandWithExitCodeZero_IsClassifiedAsQuota()
        {
            using var stub = CliStubScript.Create(
                posixBody: "echo '{\"status\":\"ERROR\",\"response\":\"\",\"error\":\"usage limit reached\"}'\n",
                windowsBody: "echo {\"status\":\"ERROR\",\"response\":\"\",\"error\":\"usage limit reached\"}\r\n");

            var client = new AntigravityCliClient(stub.Path, "gemini", TimeSpan.FromSeconds(60));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            Assert.Contains("agy-cli", exception.Message);
            Assert.Contains("구독", exception.Message);
            // 원문 JSON도 함께 실려야 진단이 된다.
            Assert.Contains("usage limit reached", exception.Message);
        }

        [Fact]
        public async Task ChatAsync_StubExitsNonZeroWithAuthMessage_ThrowsClassifiedException()
        {
            using var stub = CliStubScript.Create(
                posixBody: "echo 'Not logged in' 1>&2\nexit 1\n",
                windowsBody: "echo Not logged in 1>&2\r\nexit 1\r\n");

            var client = new AntigravityCliClient(stub.Path, "gemini", TimeSpan.FromSeconds(60));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            Assert.Contains("로그인", exception.Message);
            Assert.Contains("Not logged in", exception.Message);
        }

        // ---- 토큰 집계 ----
        // 2026-08-12에 `agy -p --output-format json`을 실제로 호출해 받은 usage 객체다.
        // 세 CLI 중 필드 이름이 가장 다르다: 캐시 읽기는 cache_read_tokens이고,
        // 캐시 쓰기 항목은 아예 없으며, 대신 thinking_tokens를 유일하게 내놓는다.

        [Fact]
        public void ParseResult_ReadsUsageCountersFromEnvelope()
        {
            const string json =
                "{\"status\":\"SUCCESS\",\"response\":\"2\",\"usage\":{\"input_tokens\":19406," +
                "\"output_tokens\":299,\"thinking_tokens\":288,\"cache_read_tokens\":0," +
                "\"total_tokens\":19705}}";

            var response = AntigravityCliClient.ParseResult(json);

            Assert.NotNull(response.Usage);
            Assert.Equal(19406, response.Usage!.Input);
            Assert.Equal(299, response.Usage.Output);
            Assert.Equal(288, response.Usage.Thinking);
            Assert.Equal(0, response.Usage.CacheRead);
        }

        // agy는 캐시 쓰기를 보고하지 않는다. 0으로 채우면 "쓰기가 0회였다"는 측정값이
        // 되어, 캐시가 도는지 판정할 때 근거로 쓰이게 된다. 실제로는 알 수 없다.
        [Fact]
        public void ParseResult_LeavesCacheWriteUnreported()
        {
            const string json =
                "{\"status\":\"SUCCESS\",\"response\":\"2\",\"usage\":{\"input_tokens\":19406," +
                "\"output_tokens\":299,\"cache_read_tokens\":0}}";

            var response = AntigravityCliClient.ParseResult(json);

            Assert.Null(response.Usage!.CacheWrite);
        }

        [Fact]
        public void ParseResult_WithoutUsageObject_LeavesUsageNull()
        {
            var response = AntigravityCliClient.ParseResult(
                "{\"status\":\"SUCCESS\",\"response\":\"PONG\"}");

            Assert.Null(response.Usage);
        }
    }
}
