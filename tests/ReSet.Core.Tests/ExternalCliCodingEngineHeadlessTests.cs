using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 헤드리스 실행 경로(stdin 닫기, stderr 비동기 캡처, 산출물 스냅샷, 실패 분류)를
    /// 실제 프로세스로 검증한다. 지금까지의 ExternalCliCodingEngineTests는 전부
    /// isHeadless: false로만 기동해 이 경로를 한 번도 거치지 않았다.
    /// </summary>
    public class ExternalCliCodingEngineHeadlessTests : IDisposable
    {
        private readonly string _workingDir;
        private readonly string _instructionsFilePath;

        public ExternalCliCodingEngineHeadlessTests()
        {
            _workingDir = Path.Combine(Path.GetTempPath(), "reset-headless-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workingDir);
            // 인자 템플릿에 {instructions}/{jobDir}를 쓰지 않으므로 실제로 존재할 필요는 없다.
            _instructionsFilePath = Path.Combine(_workingDir, "..", "dummy-instructions.txt");
        }

        public void Dispose()
        {
            if (Directory.Exists(_workingDir))
            {
                Directory.Delete(_workingDir, recursive: true);
            }
        }

        private static (string Command, string Args) ShellCommand(string script)
        {
            // ProcessStartInfo.Arguments는 두 OS 모두 동일한(Windows 스타일) 인자
            // 파싱 규칙을 쓰므로, 셸 스크립트 전체를 하나의 인자로 감싸 넘긴다.
            return OperatingSystem.IsWindows()
                ? ("cmd", $"/c \"{script}\"")
                : ("sh", $"-c \"{script}\"");
        }

        [Fact]
        public async Task Headless_CommandCreatesFile_ProducedArtifactsTrue()
        {
            // Arrange: 작업 디렉터리 안에 파일 하나를 만들고 정상 종료하는 명령
            var script = OperatingSystem.IsWindows()
                ? "echo hello> created.txt"
                : "echo hello > created.txt";
            var (command, args) = ShellCommand(script);
            var engine = new ExternalCliCodingEngine("headless-test", command, args, isHeadless: true);

            // Act
            var result = await engine.GenerateCodeAsync(null, _instructionsFilePath, _workingDir, CancellationToken.None);

            // Assert
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.ProducedArtifacts);
            Assert.True(File.Exists(Path.Combine(_workingDir, "created.txt")));
        }

        [Fact]
        public async Task Headless_CommandDoesNothingAndExitsZero_ProducedArtifactsFalse()
        {
            // Arrange: 이 기능이 존재하는 이유 그 자체 - 아무것도 안 하고 0으로 끝나는 명령.
            // 예전 판정(exitCode == 0)이라면 이 실행을 성공으로 오판했을 시나리오다.
            var (command, args) = ShellCommand("exit 0");
            var engine = new ExternalCliCodingEngine("headless-test", command, args, isHeadless: true);

            // Act
            var result = await engine.GenerateCodeAsync(null, _instructionsFilePath, _workingDir, CancellationToken.None);

            // Assert
            Assert.Equal(0, result.ExitCode);
            Assert.False(result.ProducedArtifacts);
        }

        [Fact]
        public async Task Headless_QuotaMarkerOnStderrWithNonZeroExit_ClassifiesAsQuotaExhausted()
        {
            // Arrange: stderr에 쿼터 마커("rate limit")를 남기고 비정상 종료하는 명령
            var script = OperatingSystem.IsWindows()
                ? "echo rate limit exceeded 1>&2 & exit 1"
                : "echo rate limit exceeded 1>&2; exit 1";
            var (command, args) = ShellCommand(script);
            var engine = new ExternalCliCodingEngine("headless-test", command, args, isHeadless: true);

            // Act
            var result = await engine.GenerateCodeAsync(null, _instructionsFilePath, _workingDir, CancellationToken.None);

            // Assert
            Assert.Equal(1, result.ExitCode);
            Assert.Equal(CliFailureKind.QuotaExhausted, result.FailureKind);
            Assert.NotNull(result.Diagnostic);
            Assert.Contains("rate limit exceeded", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Interactive_StderrIsNotCaptured_DiagnosticNullAndFailureKindUnknown()
        {
            // Arrange: 캡처된다면 QuotaExhausted로 분류될 동일한 명령을 대화형으로 기동한다.
            // 그래도 Unknown/null이 나와야 "대화형에서는 stderr를 안 본다"는 계약이 증명된다.
            // 대화형은 stderr를 상속하므로 이 한 줄은 테스트 러너 콘솔에 그대로 찍힌다 -
            // 출력을 짧게 한 줄로 유지해 로그 오염을 최소화한다.
            var script = OperatingSystem.IsWindows()
                ? "echo rate limit 1>&2 & exit 1"
                : "echo rate limit 1>&2; exit 1";
            var (command, args) = ShellCommand(script);
            var engine = new ExternalCliCodingEngine("interactive-test", command, args, isHeadless: false);

            // Act
            var result = await engine.GenerateCodeAsync(null, _instructionsFilePath, _workingDir, CancellationToken.None);

            // Assert
            Assert.Equal(1, result.ExitCode);
            Assert.Null(result.Diagnostic);
            Assert.Equal(CliFailureKind.Unknown, result.FailureKind);
        }
    }
}
