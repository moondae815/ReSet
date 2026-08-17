using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SessionOptionsExtractorTests
    {
        [Fact]
        public void Extract_BodyOption_ShouldBeCaptured()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SET NOCOUNT ON
    BEGIN TRAN
    COMMIT TRAN
END";

            Assert.Contains("NOCOUNT", SessionOptionsExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_BatchPreambleOption_ShouldBeIgnored()
        {
            // CREATE 앞의 SET ANSI_NULLS ON은 배치 관례이지 이 SP의 로직이 아니다.
            const string ddl = @"SET ANSI_NULLS ON
GO
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT 1
END";

            Assert.Empty(SessionOptionsExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_EmptyDdl_ShouldReturnEmpty()
        {
            Assert.Empty(SessionOptionsExtractor.Extract(null));
        }
    }
}
