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
        public async Task GenerateCodeAsync_ValidCommand_ReturnsTrue()
        {
            // Arrange
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            var command = isWindows ? "cmd" : "echo";
            var args = isWindows ? "/c echo ok" : "ok";
            var engine = new ExternalCliCodingEngine("TestEngine", command, args);

            // Act
            var result = await engine.GenerateCodeAsync(null, "dummy.txt", "", CancellationToken.None);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task GenerateCodeAsync_CommandFails_ReturnsFalse()
        {
            // Arrange
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            var command = isWindows ? "cmd" : "sh";
            var args = isWindows ? "/c exit 1" : "-c \"exit 1\"";
            var engine = new ExternalCliCodingEngine("TestEngine", command, args);

            // Act
            var result = await engine.GenerateCodeAsync(null, "dummy.txt", "", CancellationToken.None);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task GenerateCodeAsync_InvalidCommand_ThrowsInvalidOperationException()
        {
            // Arrange
            var engine = new ExternalCliCodingEngine("TestEngine", "fake_command_does_not_exist_123", "");

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
            var engine = new ExternalCliCodingEngine("TestEngine", command, args);
            var cts = new CancellationTokenSource();
            
            // Cancel immediately after short delay
            cts.CancelAfter(500);

            // Act
            // Act & Assert
            await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => await engine.GenerateCodeAsync(null, "dummy.txt", "", cts.Token));
        }
    }
}
