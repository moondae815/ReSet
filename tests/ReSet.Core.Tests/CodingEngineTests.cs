using Microsoft.Extensions.Configuration;
using ReSet.Core.Models;
using ReSet.Core.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReSet.Core.Tests
{
    public class CodingEngineTests
    {
        [Fact]
        public void CodingEngineFactory_ShouldCreateEngineFromConfiguration()
        {
            // Arrange
            var inMemorySettings = new Dictionary<string, string?> {
                {"CodegenSettings:Engines:test-claude:Command", "claude-cli"},
                {"CodegenSettings:Engines:test-claude:Arguments", "run {instructions}"}
            };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            var factory = new ReSet.Cli.CodingEngineFactory(configuration);

            // Act
            var engine = factory.CreateEngine("test-claude");

            // Assert
            Assert.NotNull(engine);
            Assert.Equal("test-claude", engine.Name);
        }

        [Fact]
        public void CodingEngineFactory_ShouldThrowException_WhenEngineConfigDoesNotExist()
        {
            // Arrange
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            var factory = new ReSet.Cli.CodingEngineFactory(configuration);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => factory.CreateEngine("non-existent"));
        }

        [Fact]
        public async Task ExternalCliCodingEngine_ShouldThrow_WhenCommandDoesNotExist()
        {
            // Arrange
            var engine = new ExternalCliCodingEngine("test-engine", "non-existent-command-12345", "--help", isHeadless: false);
            var spDef = new SpDefinition { Schema = "dbo", Name = "TestSp" };
            
            var tempFile = Path.GetTempFileName();

            try
            {
                // Act & Assert
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await engine.GenerateCodeAsync(spDef, tempFile, Directory.GetCurrentDirectory(), CancellationToken.None);
                });
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }
    }
}
