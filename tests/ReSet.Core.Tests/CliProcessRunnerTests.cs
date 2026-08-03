using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class CliProcessRunnerTests
    {
        // 실제 CLI 대신 셸 스텁을 쓴다. ExternalCliCodingEngineTests와 같은 방식이다.
        private static (string Command, IReadOnlyList<string> Arguments) Shell(string posixScript, string windowsScript)
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ("cmd", new[] { "/c", windowsScript })
                : ("sh", new[] { "-c", posixScript });
        }

        private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task RunAsync_StdinIsDeliveredAndStdoutCaptured()
        {
            // 표준 입력을 읽어 표준 출력으로 흘리는 필터. Windows에서는 sort가
            // 안정적으로 stdin을 받는다(한 줄이면 그대로 나온다).
            var (command, arguments) = Shell("cat", "sort");

            var result = await CliProcessRunner.RunAsync(
                command, arguments, "안녕하세요", Path.GetTempPath(), Generous, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            // 한글이 깨지지 않고 왕복하는지까지 확인한다. ReSet의 프롬프트는 전부 한글이다.
            Assert.Contains("안녕하세요", result.StandardOutput);
        }

        [Fact]
        public async Task RunAsync_NonZeroExit_IsReportedNotThrown()
        {
            var (command, arguments) = Shell("exit 3", "exit 3");

            var result = await CliProcessRunner.RunAsync(
                command, arguments, null, Path.GetTempPath(), Generous, CancellationToken.None);

            Assert.Equal(3, result.ExitCode);
            Assert.False(result.TimedOut);
        }

        [Fact]
        public async Task RunAsync_StandardErrorIsCaptured()
        {
            var (command, arguments) = Shell("echo boom 1>&2; exit 1", "echo boom 1>&2 & exit 1");

            var result = await CliProcessRunner.RunAsync(
                command, arguments, null, Path.GetTempPath(), Generous, CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("boom", result.StandardError);
        }

        // stdout 파이프 버퍼(보통 64KB)보다 큰 출력을 흘려도 데드락에 빠지지 않아야 한다.
        // 명세서 응답은 실제로 수십 KB다.
        [Fact]
        public async Task RunAsync_LargeStdout_DoesNotDeadlock()
        {
            var payload = new string('x', 300_000);
            var tempFile = Path.Combine(Path.GetTempPath(), $"reset-cli-test-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(tempFile, payload);

            try
            {
                var (command, arguments) = Shell($"cat \"{tempFile}\"", $"type \"{tempFile}\"");

                var result = await CliProcessRunner.RunAsync(
                    command, arguments, null, Path.GetTempPath(), Generous, CancellationToken.None);

                Assert.Equal(0, result.ExitCode);
                Assert.True(result.StandardOutput.Length >= 300_000);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        // 자식이 stdin을 다 읽기 전에 끝나면 파이프가 끊겨 IOException(EPIPE)이 난다.
        // 이것이 그대로 올라가면 호출자는 Win32Exception만 잡으므로 "Broken pipe"라는
        // 원시 메시지가 사용자에게 가고, 진짜 원인인 종료 코드와 stderr는 영영 회수되지
        // 않는다. ReSet의 실제 프롬프트는 191KB로 파이프 버퍼(보통 64KB)를 넘기므로
        // 이 경로는 실제 워크로드에서만 밟힌다 - 한 줄짜리 입력으로는 재현되지 않는다.
        [Fact]
        public async Task RunAsync_ChildExitsBeforeDrainingLargeStdin_StillReportsExitCodeAndStderr()
        {
            var (command, arguments) = Shell(
                "echo 'not logged in' 1>&2; exit 7",
                "echo not logged in 1>&2 & exit 7");

            // 파이프 버퍼를 확실히 넘긴다. 한글은 UTF-8 3바이트이므로 약 900KB다.
            var oversizedPrompt = new string('가', 300_000);

            var result = await CliProcessRunner.RunAsync(
                command, arguments, oversizedPrompt, Path.GetTempPath(), Generous, CancellationToken.None);

            Assert.Equal(7, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.Contains("not logged in", result.StandardError);
        }

        [Fact]
        public async Task RunAsync_Timeout_ReturnsTimedOutResult()
        {
            var (command, arguments) = Shell("sleep 30", "ping -n 30 127.0.0.1 > nul");

            var result = await CliProcessRunner.RunAsync(
                command, arguments, null, Path.GetTempPath(),
                TimeSpan.FromSeconds(1), CancellationToken.None);

            Assert.True(result.TimedOut);
        }

        // 사용자 취소는 타임아웃과 구별되어야 하며, 다른 예외 타입으로 감싸이면 안 된다.
        [Fact]
        public async Task RunAsync_UserCancellation_ThrowsOperationCanceledException()
        {
            var (command, arguments) = Shell("sleep 30", "ping -n 30 127.0.0.1 > nul");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CliProcessRunner.RunAsync(
                    command, arguments, null, Path.GetTempPath(),
                    TimeSpan.FromSeconds(30), cts.Token));
        }

        [Fact]
        public async Task RunAsync_MissingCommand_ThrowsWin32Exception()
        {
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
                CliProcessRunner.RunAsync(
                    "reset_cli_command_does_not_exist_42", Array.Empty<string>(), null,
                    Path.GetTempPath(), Generous, CancellationToken.None));
        }

        [Fact]
        public void CliWorkspace_CreatesEmptyDirectoryAndCleansUp()
        {
            string path;
            using (var workspace = new CliWorkspace())
            {
                path = workspace.Path;
                Assert.True(Directory.Exists(path));
                Assert.Empty(Directory.GetFileSystemEntries(path));

                var written = workspace.WriteFile("system.txt", "규칙");
                Assert.Equal("규칙", File.ReadAllText(written));
            }

            Assert.False(Directory.Exists(path));
        }
    }
}
