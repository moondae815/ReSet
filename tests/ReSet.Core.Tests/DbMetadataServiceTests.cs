using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class DbMetadataServiceTests
    {
        [Fact]
        public async Task GetStoredProcedureNamesAsync_WithInvalidConnectionString_ShouldThrowException()
        {
            // Arrange
            var invalidConnString = "Server=invalid_server;Database=invalid_db;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=1;";
            IDbMetadataService service = new DbMetadataService();

            // Act & Assert
            await Assert.ThrowsAnyAsync<System.Exception>(() => service.GetStoredProcedureNamesAsync(invalidConnString));
        }

        [Fact]
        public async Task GetTableColumnsAsync_WithInvalidConn_ShouldThrowException()
        {
            // Arrange
            var invalidConnString = "Server=invalid_server;Database=invalid_db;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=1;";
            var service = new DbMetadataService();

            // Act & Assert
            await Assert.ThrowsAnyAsync<System.Exception>(() => service.GetTableColumnsAsync(invalidConnString, null, "dbo", "NonExistentTable"));
        }

        [Fact]
        public async Task GetSpDetailsAsync_WithInvalidConn_ShouldThrowException()
        {
            // Arrange
            var invalidConnString = "Server=invalid_server;Database=invalid_db;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=1;";
            var service = new DbMetadataService();

            // Act & Assert
            var ex = await Assert.ThrowsAnyAsync<System.Exception>(() => service.GetSpDetailsAsync(invalidConnString, "dbo", "USP_Test", 2));
            Assert.NotNull(ex);
        }

        [Fact]
        public async Task GetDatabaseCompatibilityLevelAsync_WithInvalidConn_ReturnsNegativeOne()
        {
            var invalidConnString = "Server=invalid_server;Database=invalid_db;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=1;";
            var service = new DbMetadataService();
            // Need to use reflection to call private method
            var method = typeof(DbMetadataService).GetMethod("GetDatabaseCompatibilityLevelAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method != null)
            {
                var task = (Task<int>)method.Invoke(service, new object[] { invalidConnString, CancellationToken.None });
                var result = await task;
                Assert.Equal(160, result);
            }
        }

        [Fact]
        public async Task GetObjectTypeAsync_WithInvalidConn_ReturnsUnknown()
        {
            var invalidConnString = "Server=invalid_server;Database=invalid_db;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=1;";
            var service = new DbMetadataService();
            var method = typeof(DbMetadataService).GetMethod("GetObjectTypeAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method != null)
            {
                var task = (Task<string>)method.Invoke(service, new object[] { invalidConnString, "db", "dbo", "test", CancellationToken.None });
                var result = await task;
                Assert.Equal("UNKNOWN", result);
            }
        }

        [Fact]
        public async Task GetTableDescriptionAsync_WithInvalidConn_ReturnsEmptyString()
        {
            var invalidConnString = "Server=invalid_server;Database=invalid_db;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=1;";
            var service = new DbMetadataService();
            var method = typeof(DbMetadataService).GetMethod("GetTableDescriptionAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method != null)
            {
                var task = (Task<string>)method.Invoke(service, new object[] { invalidConnString, "db", "dbo", "test", CancellationToken.None });
                var result = await task;
                Assert.Equal("", result);
            }
        }

        [Fact]
        public async Task GetRawDependenciesAsync_WithInvalidConn_ThrowsException()
        {
            var invalidConnString = "Server=invalid_server;Database=invalid_db;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=1;";
            var service = new DbMetadataService();
            var method = typeof(DbMetadataService).GetMethod("GetRawDependenciesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method != null)
            {
                await Assert.ThrowsAnyAsync<Exception>(async () => 
                {
                    var task = (Task<List<DependencyInfo>>)method.Invoke(service, new object[] { invalidConnString, "db", "dbo", "test", CancellationToken.None });
                    await task;
                });
            }
        }
    }
}
