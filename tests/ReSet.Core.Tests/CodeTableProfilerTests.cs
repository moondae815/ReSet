using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class CodeTableProfilerTests
    {
        private const string ConnectionString =
            "Server=localhost;Database=Northwind;Integrated Security=true;TrustServerCertificate=true;";

        private static DependencyInfo Dependency(
            string name, string schema = "dbo", string type = "USER_TABLE", string? database = null) =>
            new()
            {
                Database = database,
                Schema = schema,
                Name = name,
                Type = type,
            };

        [Fact]
        public async Task 행_수가_임계_아래인_테이블은_읽는다()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            dbService.GetTableRowCountAsync(ConnectionString, null, "dbo", "TCode", Arg.Any<CancellationToken>())
                .Returns(499);
            dbService.GetTableDataPreviewAsync(ConnectionString, null, "dbo", "TCode", 500, Arg.Any<CancellationToken>())
                .Returns(new List<Dictionary<string, object>>
                {
                    new() { { "Code", "01" } },
                });

            var profiler = new CodeTableProfiler(dbService, ConnectionString);

            var result = await profiler.ProfileAsync(new[] { Dependency("TCode") });

            var table = Assert.Single(result);
            Assert.Equal("dbo.TCode", table.Table);
            await dbService.Received(1).GetTableDataPreviewAsync(
                ConnectionString, null, "dbo", "TCode", 500, Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task 행_수가_정확히_임계면_읽는다()
        {
            // 경계는 포함이다 - _rowThreshold를 500에서 499나 501로 바꾸면 이 테스트가
            // 잡는다(기본 임계값에 기대는 테스트이므로 상수 변경 자체가 위반을 만든다).
            var dbService = Substitute.For<IDbMetadataService>();
            dbService.GetTableRowCountAsync(ConnectionString, null, "dbo", "TCode", Arg.Any<CancellationToken>())
                .Returns(500);
            dbService.GetTableDataPreviewAsync(ConnectionString, null, "dbo", "TCode", 500, Arg.Any<CancellationToken>())
                .Returns(new List<Dictionary<string, object>> { new() { { "Code", "01" } } });

            var profiler = new CodeTableProfiler(dbService, ConnectionString);

            var result = await profiler.ProfileAsync(new[] { Dependency("TCode") });

            Assert.Single(result);
            await dbService.Received(1).GetTableDataPreviewAsync(
                ConnectionString, null, "dbo", "TCode", 500, Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task 행_수가_임계보다_많으면_읽지_않는다()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            dbService.GetTableRowCountAsync(ConnectionString, null, "dbo", "TBig", Arg.Any<CancellationToken>())
                .Returns(501);

            var profiler = new CodeTableProfiler(dbService, ConnectionString);

            var result = await profiler.ProfileAsync(new[] { Dependency("TBig") });

            Assert.Empty(result);
            await dbService.DidNotReceive().GetTableDataPreviewAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task 한_테이블에서_읽기_실패해도_나머지_테이블은_계속_프로파일링한다()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            dbService.GetTableRowCountAsync(ConnectionString, null, "dbo", "TBroken", Arg.Any<CancellationToken>())
                .Returns<int>(_ => throw new InvalidOperationException("연결 실패"));
            dbService.GetTableRowCountAsync(ConnectionString, null, "dbo", "TGood", Arg.Any<CancellationToken>())
                .Returns(10);
            dbService.GetTableDataPreviewAsync(ConnectionString, null, "dbo", "TGood", 500, Arg.Any<CancellationToken>())
                .Returns(new List<Dictionary<string, object>> { new() { { "Code", "01" } } });

            var profiler = new CodeTableProfiler(dbService, ConnectionString);

            var result = await profiler.ProfileAsync(new[] { Dependency("TBroken"), Dependency("TGood") });

            var table = Assert.Single(result);
            Assert.Equal("dbo.TGood", table.Table);
        }

        [Fact]
        public async Task 테이블도_뷰도_아닌_의존은_건너뛴다()
        {
            var dbService = Substitute.For<IDbMetadataService>();

            var profiler = new CodeTableProfiler(dbService, ConnectionString);

            var result = await profiler.ProfileAsync(new[] { Dependency("USP_Test", type: "SQL_STORED_PROCEDURE") });

            Assert.Empty(result);
            await dbService.DidNotReceive().GetTableRowCountAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task 같은_테이블이_두_번_의존_목록에_있으면_한_번만_읽는다()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            dbService.GetTableRowCountAsync(ConnectionString, null, "dbo", "TCode", Arg.Any<CancellationToken>())
                .Returns(10);
            dbService.GetTableDataPreviewAsync(ConnectionString, null, "dbo", "TCode", 500, Arg.Any<CancellationToken>())
                .Returns(new List<Dictionary<string, object>> { new() { { "Code", "01" } } });

            var profiler = new CodeTableProfiler(dbService, ConnectionString);

            var result = await profiler.ProfileAsync(new[] { Dependency("TCode"), Dependency("TCode") });

            Assert.Single(result);
            await dbService.Received(1).GetTableRowCountAsync(
                ConnectionString, null, "dbo", "TCode", Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task null과_DBNull은_빈_문자열로_정규화한다()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            dbService.GetTableRowCountAsync(ConnectionString, null, "dbo", "TCode", Arg.Any<CancellationToken>())
                .Returns(10);
            dbService.GetTableDataPreviewAsync(ConnectionString, null, "dbo", "TCode", 500, Arg.Any<CancellationToken>())
                .Returns(new List<Dictionary<string, object>>
                {
                    new() { { "Code", "01" }, { "Note", null! }, { "Memo", DBNull.Value } },
                });

            var profiler = new CodeTableProfiler(dbService, ConnectionString);

            var result = await profiler.ProfileAsync(new[] { Dependency("TCode") });

            var row = Assert.Single(Assert.Single(result).Rows);
            Assert.Equal(string.Empty, row["Note"]);
            Assert.Equal(string.Empty, row["Memo"]);
        }

        [Fact]
        public async Task 행_딕셔너리는_컬럼_이름의_대소문자를_무시한다()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            dbService.GetTableRowCountAsync(ConnectionString, null, "dbo", "TCode", Arg.Any<CancellationToken>())
                .Returns(10);
            dbService.GetTableDataPreviewAsync(ConnectionString, null, "dbo", "TCode", 500, Arg.Any<CancellationToken>())
                .Returns(new List<Dictionary<string, object>>
                {
                    new() { { "PayMethod", "impaymobile" } },
                });

            var profiler = new CodeTableProfiler(dbService, ConnectionString);

            var result = await profiler.ProfileAsync(new[] { Dependency("TCode") });

            var row = Assert.Single(Assert.Single(result).Rows);
            Assert.True(row.TryGetValue("paymethod", out var value));
            Assert.Equal("impaymobile", value);
        }
    }
}
