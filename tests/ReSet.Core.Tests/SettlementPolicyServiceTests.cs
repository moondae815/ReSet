using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementPolicyServiceTests
    {
        [Fact]
        public async Task GenerateSettlementPolicyRulebookAsync_ShouldGatherMetadataAndCallAiService()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var service = new SettlementPolicyService(dbService, aiService);

            var connectionString = "Server=localhost;Database=Northwind;Integrated Security=true;TrustServerCertificate=true;";
            var spList = new List<string> { "dbo.sp_Test" };
            var maxDepth = 3;

            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "sp_Test",
                DdlText = "SELECT * FROM dbo.TestCodeTable",
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo
                    {
                        Schema = "dbo",
                        Name = "TestCodeTable",
                        Type = "USER_TABLE",
                        Columns = new List<ColumnInfo>
                        {
                            new ColumnInfo { ColumnName = "Code", DataType = "varchar(10)", IsPrimaryKey = true },
                            new ColumnInfo { ColumnName = "CodeName", DataType = "nvarchar(50)" }
                        }
                    }
                }
            };

            dbService.GetSpDetailsAsync(connectionString, "dbo", "sp_Test", maxDepth, Arg.Any<CancellationToken>())
                .Returns(spDef);

            var previewData = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "Code", "01" }, { "CodeName", "정산대기" } }
            };

            dbService.GetTableDataPreviewAsync(connectionString, null, "dbo", "TestCodeTable", 100, Arg.Any<CancellationToken>())
                .Returns(previewData);

            aiService.GenerateSettlementPolicyRulebookAsync(Arg.Any<List<SpDefinition>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "Generated Policy Document" }));

            // Act
            var result = await service.GenerateSettlementPolicyRulebookAsync(connectionString, spList, maxDepth, CancellationToken.None);

            // Assert
            Assert.Equal("Generated Policy Document", result);
            await dbService.Received(1).GetSpDetailsAsync(connectionString, "dbo", "sp_Test", maxDepth, Arg.Any<CancellationToken>());
            await dbService.Received(1).GetTableDataPreviewAsync(connectionString, null, "dbo", "TestCodeTable", 100, Arg.Any<CancellationToken>());
            await aiService.Received(1).GenerateSettlementPolicyRulebookAsync(
                Arg.Is<List<SpDefinition>>(list => list.Count == 1 && list[0].Name == "sp_Test"),
                Arg.Is<string>(json => json.Contains("TestCodeTable") && json.Contains("정산대기")),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task GenerateSettlementPolicyRulebookAsync_WhenTableMissing_ShouldAppendWarningAndPrependToDocument()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var service = new SettlementPolicyService(dbService, aiService);

            var connectionString = "Server=localhost;Database=Northwind;Integrated Security=true;TrustServerCertificate=true;";
            var spList = new List<string> { "dbo.sp_Test" };
            var maxDepth = 3;

            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "sp_Test",
                DdlText = "SELECT * FROM dbo.TestCodeTable",
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo
                    {
                        Schema = "dbo",
                        Name = "TestCodeTable",
                        Type = "USER_TABLE",
                        Columns = new List<ColumnInfo>()
                    }
                }
            };

            dbService.GetSpDetailsAsync(connectionString, "dbo", "sp_Test", maxDepth, Arg.Any<CancellationToken>())
                .Returns(spDef);

            dbService.GetTableDataPreviewAsync(connectionString, null, "dbo", "TestCodeTable", 100, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<List<Dictionary<string, object>>>(new System.Exception("Table not found")));

            aiService.GenerateSettlementPolicyRulebookAsync(Arg.Any<List<SpDefinition>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "Generated Policy Document" }));

            // Act
            var result = await service.GenerateSettlementPolicyRulebookAsync(connectionString, spList, maxDepth, CancellationToken.None);

            // Assert
            Assert.Contains("> [!WARNING]", result);
            Assert.Contains("TestCodeTable 테이블이 데이터베이스에 존재하지 않거나 액세스할 수 없습니다.", result);
            Assert.Contains("Generated Policy Document", result);
        }

        [Fact]
        public async Task GenerateSettlementPolicyRulebookAsync_WhenDataEmpty_ShouldAppendWarningAndPrependToDocument()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var service = new SettlementPolicyService(dbService, aiService);

            var connectionString = "Server=localhost;Database=Northwind;Integrated Security=true;TrustServerCertificate=true;";
            var spList = new List<string> { "dbo.sp_Test" };
            var maxDepth = 3;

            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "sp_Test",
                DdlText = "SELECT * FROM dbo.TestCodeTable",
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo
                    {
                        Schema = "dbo",
                        Name = "TestCodeTable",
                        Type = "USER_TABLE",
                        Columns = new List<ColumnInfo>()
                    }
                }
            };

            dbService.GetSpDetailsAsync(connectionString, "dbo", "sp_Test", maxDepth, Arg.Any<CancellationToken>())
                .Returns(spDef);

            dbService.GetTableDataPreviewAsync(connectionString, null, "dbo", "TestCodeTable", 100, Arg.Any<CancellationToken>())
                .Returns(new List<Dictionary<string, object>>());

            aiService.GenerateSettlementPolicyRulebookAsync(Arg.Any<List<SpDefinition>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "Generated Policy Document" }));

            // Act
            var result = await service.GenerateSettlementPolicyRulebookAsync(connectionString, spList, maxDepth, CancellationToken.None);

            // Assert
            Assert.Contains("> [!WARNING]", result);
            Assert.Contains("TestCodeTable 테이블의 실제 설정/공통코드 데이터가 비어있습니다.", result);
            Assert.Contains("Generated Policy Document", result);
        }

        [Fact]
        public async Task GenerateSettlementPolicyRulebookAsync_ShouldNotProfileATableValuedFunction()
        {
            // "SQL_TABLE_VALUED_FUNCTION"이 "TABLE"을 포함하므로 원시 판정은 TVF를
            // 프로파일링 대상으로 넣는다. 그러면 인자가 필요한 함수를 인자 없이
            // SELECT ... FROM 으로 읽으려 해 실패한다. 이름이 코드성 키워드에
            // 걸릴 때만 대상이 되므로 여기서는 "Rate"와 "Map"을 모두 가진
            // 이름을 쓴다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var service = new SettlementPolicyService(dbService, aiService);

            var connectionString = "Server=localhost;Database=Northwind;Integrated Security=true;TrustServerCertificate=true;";
            var spList = new List<string> { "dbo.sp_UsesFunction" };

            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "sp_UsesFunction",
                DdlText = "SELECT * FROM dbo.UIF_RateMap(@d)",
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo
                    {
                        Schema = "dbo",
                        Name = "UIF_RateMap",
                        Type = "SQL_TABLE_VALUED_FUNCTION"
                    }
                }
            };

            dbService.GetSpDetailsAsync(connectionString, "dbo", "sp_UsesFunction", 3, Arg.Any<CancellationToken>())
                .Returns(spDef);
            aiService.GenerateSettlementPolicyRulebookAsync(Arg.Any<List<SpDefinition>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "Generated Policy Document" }));

            await service.GenerateSettlementPolicyRulebookAsync(connectionString, spList, 3, CancellationToken.None);

            await dbService.DidNotReceive().GetTableDataPreviewAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task GenerateSettlementPolicyRulebookAsync_ShouldNotWarnAnSpThatOnlyReferencesASameNamedFunction()
        {
            // 테이블 프로파일링 경고는 그 테이블을 참조하는 SP에만 붙어야 한다.
            // 원시 판정은 같은 스키마·이름의 TVF를 참조하는 SP에도 경고를 붙인다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var service = new SettlementPolicyService(dbService, aiService);

            var connectionString = "Server=localhost;Database=Northwind;Integrated Security=true;TrustServerCertificate=true;";
            var spList = new List<string> { "dbo.sp_UsesTable", "dbo.sp_UsesFunction" };

            var tableSp = new SpDefinition
            {
                Schema = "dbo",
                Name = "sp_UsesTable",
                DdlText = "SELECT * FROM dbo.RateMap",
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo { Schema = "dbo", Name = "RateMap", Type = "USER_TABLE" }
                }
            };

            var functionSp = new SpDefinition
            {
                Schema = "dbo",
                Name = "sp_UsesFunction",
                DdlText = "SELECT * FROM dbo.RateMap(@d)",
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo { Schema = "dbo", Name = "RateMap", Type = "SQL_TABLE_VALUED_FUNCTION" }
                }
            };

            dbService.GetSpDetailsAsync(connectionString, "dbo", "sp_UsesTable", 3, Arg.Any<CancellationToken>())
                .Returns(tableSp);
            dbService.GetSpDetailsAsync(connectionString, "dbo", "sp_UsesFunction", 3, Arg.Any<CancellationToken>())
                .Returns(functionSp);

            // 빈 결과 -> "데이터가 비어있습니다" 경고 경로를 탄다.
            dbService.GetTableDataPreviewAsync(connectionString, null, "dbo", "RateMap", 100, Arg.Any<CancellationToken>())
                .Returns(new List<Dictionary<string, object>>());

            aiService.GenerateSettlementPolicyRulebookAsync(Arg.Any<List<SpDefinition>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "Generated Policy Document" }));

            await service.GenerateSettlementPolicyRulebookAsync(connectionString, spList, 3, CancellationToken.None);

            Assert.Single(tableSp.Warnings);
            Assert.Empty(functionSp.Warnings);
        }
    }
}
