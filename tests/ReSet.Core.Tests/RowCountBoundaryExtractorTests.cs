using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class RowCountBoundaryExtractorTests
    {
        [Fact]
        public void Extract_RowCountReadAfterAnIfStatement_ShouldReportTheReset()
        {
            // UF_GET_COMM4CLIENT 실측(실행 대조 2026-08-22, SQL Server 2022 16.0.4255.1):
            // 1차 조회가 행을 찾아 2차 블록이 건너뛰어져도, 그 IF 문이 @@ROWCOUNT를
            // 0으로 리셋해 3차 조회가 돈다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @x INT
    SELECT @x = c FROM dbo.T
    IF @@ROWCOUNT < 1 BEGIN SELECT TOP 1 @x = c FROM dbo.THist ORDER BY v DESC END
    IF @@ROWCOUNT < 1 BEGIN SELECT TOP 1 @x = c FROM dbo.T ORDER BY c DESC END
END";

            var facts = RowCountBoundaryExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Contains("직전 IF", fact.Sentence);
            Assert.Contains("항상 참", fact.Sentence);
        }

        [Fact]
        public void Extract_RowCountReadRightAfterAQuery_ShouldNotBeReported()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @x INT
    SELECT @x = c FROM dbo.T
    IF @@ROWCOUNT < 1 BEGIN SET @x = 0 END
END";

            Assert.Empty(RowCountBoundaryExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(RowCountBoundaryExtractor.Extract("CREATE PROCEDURE ((("));
        }
    }
}
