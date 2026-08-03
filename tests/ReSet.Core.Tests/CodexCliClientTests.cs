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

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

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

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            Assert.Contains("codex progress: thinking", exception.Message);
            Assert.Contains("결과 파일을 남기지 않았습니다", exception.Message);
        }
    }
}
