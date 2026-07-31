using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class OfflineDbMetadataServiceTests
    {
        [Fact]
        public async Task GetStoredProcedureNamesAsync_ReturnsNamesFromSnapshot()
        {
            var snapshot = new DbSnapshot();
            snapshot.StoredProcedures.Add("dbo.TestSp", new SpDefinition { Name = "TestSp", Schema = "dbo" });
            
            var service = new OfflineDbMetadataService(snapshot);
            var names = await service.GetStoredProcedureNamesAsync("dummy_conn", CancellationToken.None);
            
            Assert.Single(names);
            Assert.Contains("dbo.TestSp", names);
        }

        [Fact]
        public async Task GetSpDetailsAsync_ReturnsSpDefinition()
        {
            var snapshot = new DbSnapshot();
            var expectedDef = new SpDefinition { Name = "TestSp", Schema = "dbo" };
            snapshot.StoredProcedures.Add("dbo.TestSp", expectedDef);
            
            var service = new OfflineDbMetadataService(snapshot);
            var sp = await service.GetSpDetailsAsync("dummy", "dbo", "TestSp", 1, CancellationToken.None);
            
            Assert.Equal(expectedDef, sp);
        }

        [Fact]
        public async Task GetCodeObjectDetailsAsync_ReturnsFunctionFromCodeObjects()
        {
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "FN_Calc", CodeObjectType.Function);
            var snapshot = new DbSnapshot { Database = "PaymentDB" };
            snapshot.CodeObjects[key.CanonicalName] = new SpDefinition
            {
                Name = "FN_Calc",
                ObjectType = CodeObjectType.Function
            };

            var result = await new OfflineDbMetadataService(snapshot)
                .GetCodeObjectDetailsAsync("ignored", key, 2);

            Assert.Equal(CodeObjectType.Function, result.ObjectType);
        }

        [Fact]
        public async Task GetCodeObjectDetailsAsync_FallsBackToLegacyStoredProcedureKey()
        {
            var snapshot = new DbSnapshot { Database = "PaymentDB" };
            snapshot.StoredProcedures["dbo.usp_Legacy"] = new SpDefinition { Name = "usp_Legacy" };
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "usp_Legacy", CodeObjectType.Procedure);

            var result = await new OfflineDbMetadataService(snapshot)
                .GetCodeObjectDetailsAsync("ignored", key, 2);

            Assert.Equal("usp_Legacy", result.Name);
        }

        [Fact]
        public async Task GetCodeObjectDetailsAsync_WhenMissing_IncludesCanonicalNameInException()
        {
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "FN_Missing", CodeObjectType.Function);

            var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                new OfflineDbMetadataService(new DbSnapshot())
                    .GetCodeObjectDetailsAsync("ignored", key, 2));

            Assert.Contains("PaymentDB.dbo.FN_Missing.Function", exception.Message);
        }

        [Fact]
        public async Task GetTableDataPreviewAsync_ThrowsNotSupportedException()
        {
            var service = new OfflineDbMetadataService(new DbSnapshot());
            await Assert.ThrowsAsync<NotSupportedException>(() => 
                service.GetTableDataPreviewAsync("dummy", null, "dbo", "Table1", 100, CancellationToken.None));
        }
    }
}
