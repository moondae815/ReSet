using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class ExternalCliCodingEngineTests
    {
        [Fact]
        public async Task GenerateCodeAsync_ValidCommand_ExitsZero()
        {
            // Arrange
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            var command = isWindows ? "cmd" : "echo";
            var args = isWindows ? "/c echo ok" : "ok";
            var engine = new ExternalCliCodingEngine("TestEngine", command, args, isHeadless: false);

            // Act
            var result = await engine.GenerateCodeAsync(null, "dummy.txt", "", CancellationToken.None);

            // Assert
            Assert.Equal(0, result.ExitCode);
        }

        [Fact]
        public async Task GenerateCodeAsync_CommandFails_ExitsNonZero()
        {
            // Arrange
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            var command = isWindows ? "cmd" : "sh";
            var args = isWindows ? "/c exit 1" : "-c \"exit 1\"";
            var engine = new ExternalCliCodingEngine("TestEngine", command, args, isHeadless: false);

            // Act
            var result = await engine.GenerateCodeAsync(null, "dummy.txt", "", CancellationToken.None);

            // Assert
            Assert.NotEqual(0, result.ExitCode);
        }

        [Fact]
        public async Task GenerateCodeAsync_InvalidCommand_ThrowsInvalidOperationException()
        {
            // Arrange
            var engine = new ExternalCliCodingEngine("TestEngine", "fake_command_does_not_exist_123", "", isHeadless: false);

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.GenerateCodeAsync(null, "dummy.txt", "", CancellationToken.None));
        }

        [Fact]
        public async Task GenerateCodeAsync_Cancelled_KillsProcess()
        {
            // Arrange
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            var command = isWindows ? "ping" : "sleep";
            var args = isWindows ? "127.0.0.1 -n 10" : "10";
            var engine = new ExternalCliCodingEngine("TestEngine", command, args, isHeadless: false);
            var cts = new CancellationTokenSource();
            
            // Cancel immediately after short delay
            cts.CancelAfter(500);

            // Act
            // Act & Assert
            // 취소는 더 이상 InvalidOperationException으로 세탁되지 않는다 - 하류의
            // catch (OperationCanceledException) 핸들러가 매칭할 수 있도록 그대로 올라온다.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.GenerateCodeAsync(null, "dummy.txt", "", cts.Token));
        }
    }
}
