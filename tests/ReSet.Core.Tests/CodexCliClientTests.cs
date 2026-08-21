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

        // ---- 스텁으로 조립된 호출을 실제로 실행한다 ----
        // 결과를 -o 파일에서 읽는 경로는 단위 테스트로 전혀 덮이지 않았다.
        // 스텁이 인자에서 -o 값을 직접 찾아 파일을 쓰므로, 인자 순서가 틀리면 실패한다.
        // 진짜 codex 바이너리는 부르지 않는다.

        private const string PosixWriteResultFile =
            "cat > /dev/null\n" +
            "prev=\"\"\n" +
            "for a in \"$@\"; do\n" +
            "  if [ \"$prev\" = \"-o\" ]; then printf 'PONG-FROM-CODEX\\n' > \"$a\"; fi\n" +
            "  prev=\"$a\"\n" +
            "done\n";

        private const string WindowsWriteResultFile =
            "more > nul\r\n" +
            ":loop\r\n" +
            "if \"%~1\"==\"\" goto :eof\r\n" +
            "if \"%~1\"==\"-o\" goto found\r\n" +
            "shift\r\n" +
            "goto loop\r\n" +
            ":found\r\n" +
            "shift\r\n" +
            "echo PONG-FROM-CODEX>\"%~1\"\r\n";

        [Fact]
        public async Task ChatAsync_StubWritesResultFile_ContentComesFromThatFile()
        {
            using var stub = CliStubScript.Create(PosixWriteResultFile, WindowsWriteResultFile);

            var client = new CodexCliClient(stub.Path, "gpt-5.6-terra", TimeSpan.FromSeconds(60));

            var result = await client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f);

            Assert.Equal("PONG-FROM-CODEX", result.Content.Trim());
        }

        [Fact]
        public async Task ChatAsync_StubExitsNonZeroWithAuthMessage_ThrowsClassifiedException()
        {
            using var stub = CliStubScript.Create(
                posixBody: "cat > /dev/null\necho 'Not logged in. Please run codex login.' 1>&2\nexit 1\n",
                windowsBody: "more > nul\r\necho Not logged in. Please run codex login. 1>&2\r\nexit 1\r\n");

            var client = new CodexCliClient(stub.Path, "gpt-5.6-terra", TimeSpan.FromSeconds(60));

            var exception = await Assert.ThrowsAsync<CliInvocationException>(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            Assert.Equal(CliFailureKind.NotAuthenticated, exception.Kind);
            Assert.Contains("codex-cli", exception.Message);
            Assert.Contains("로그인", exception.Message);
            Assert.Contains("Not logged in", exception.Message);
        }

        // codex는 진행 로그를 stderr로 흘리면서 종료 코드 0으로 끝날 수 있다.
        // stderr가 비어 있지 않다는 이유로 "결과 파일을 남기지 않았습니다"라는
        // 가장 구체적인 진단이 사라지면, 사용자에게는 이유 없는 실패만 남는다.
        [Fact]
        public async Task ChatAsync_StubExitsZeroWithoutResultFile_KeepsBothStderrAndDiagnosis()
        {
            using var stub = CliStubScript.Create(
                posixBody: "cat > /dev/null\necho 'codex progress: thinking' 1>&2\nexit 0\n",
                windowsBody: "more > nul\r\necho codex progress: thinking 1>&2\r\nexit 0\r\n");

            var client = new CodexCliClient(stub.Path, "gpt-5.6-terra", TimeSpan.FromSeconds(60));

            var exception = await Assert.ThrowsAsync<CliInvocationException>(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            Assert.Contains("codex progress: thinking", exception.Message);
            Assert.Contains("결과 파일을 남기지 않았습니다", exception.Message);
        }

        // ---- 토큰 집계 ----
        // 세 CLI 중 codex만 인자를 하나 더 붙여야 집계를 볼 수 있다. --json 없이는
        // stdout에 사람이 읽을 진행 로그만 흐르고("tokens used 16,665"), 본문은 -o
        // 파일로 따로 나온다. 본문 경로는 그대로 두고 stdout만 파싱 가능해진다.

        [Fact]
        public void BuildArguments_RequestsJsonEventsSoUsageIsObservable()
        {
            var arguments = CodexCliClient.BuildArguments("gpt-5.6-terra", null, "/tmp/out.txt");

            Assert.Contains("--json", arguments);
            // 본문은 여전히 -o 파일에서 읽는다. --json은 stdout만 바꾼다.
            Assert.Contains("-o", arguments);
        }

        // 2026-08-12에 `codex exec --json`으로 실제로 받은 이벤트다. 필드 이름이
        // claude와 다르다: cached_input_tokens / cache_write_input_tokens.
        [Fact]
        public void ParseUsage_ReadsTurnCompletedEvent()
        {
            var stdout = string.Join("\n", new[]
            {
                "{\"type\":\"thread.started\",\"thread_id\":\"abc\"}",
                "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":14165," +
                "\"cached_input_tokens\":9984,\"cache_write_input_tokens\":0," +
                "\"output_tokens\":5,\"reasoning_output_tokens\":0}}"
            });

            var usage = CodexCliClient.ParseUsage(stdout);

            Assert.NotNull(usage);
            Assert.Equal(14165, usage!.Input);
            Assert.Equal(9984, usage.CacheRead);
            Assert.Equal(0, usage.CacheWrite);
            Assert.Equal(5, usage.Output);
            Assert.Equal(0, usage.Thinking);
        }

        // stdout이 순수 JSONL이라는 보장은 없다. 한 줄이라도 깨지면 집계를 못 읽는 것이
        // 아니라 분석 전체가 죽는 구조는 곤란하다 - 집계는 진단 정보일 뿐이다.
        [Fact]
        public void ParseUsage_IgnoresNonJsonLines()
        {
            var stdout = string.Join("\n", new[]
            {
                "codex progress: thinking",
                string.Empty,
                "{ 깨진 JSON",
                "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":7}}"
            });

            var usage = CodexCliClient.ParseUsage(stdout);

            Assert.Equal(7, usage!.Input);
        }

        [Fact]
        public void ParseUsage_WithoutTurnCompletedEvent_ReturnsNull()
        {
            Assert.Null(CodexCliClient.ParseUsage("{\"type\":\"thread.started\"}"));
        }

        // 한 실행에 turn.completed가 여럿이면 마지막이 그 실행의 최종 상태다.
        [Fact]
        public void ParseUsage_WithSeveralTurnCompletedEvents_TakesTheLast()
        {
            var stdout = string.Join("\n", new[]
            {
                "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1}}",
                "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":99}}"
            });

            Assert.Equal(99, CodexCliClient.ParseUsage(stdout)!.Input);
        }
    }
}
