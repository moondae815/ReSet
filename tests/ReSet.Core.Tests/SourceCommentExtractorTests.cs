using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SourceCommentExtractorTests
    {
        [Fact]
        public void Extract_NonExecutableCodeComment_ShouldCarryIdentifierAnchors()
        {
            // COMM_UPD 실측 형태. 이 주석이 명세서에 통째로 빠졌다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T SET C = 1
    WHERE  ID > 0
    --AND ClientID NOT IN (SELECT ClientID FROM dbo.UF_GET_CLIENTID4TMONET()) --예외처리 제거(2021.11.29)
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            var block = Assert.Single(blocks, b => b.Kind == "NonExecutable");
            Assert.Contains("UF_GET_CLIENTID4TMONET", block.Anchors);
            Assert.Contains("2021.11.29", block.Anchors);
        }

        [Fact]
        public void Extract_CodeLegendComment_ShouldCarryNumberLabelAnchors()
        {
            // PROC_ETC 실측: 0:일반,1:내부테스트용,... 범례가 명세서에 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    -- ClientIDType 0:일반,1:내부테스트용,2:Cafe24
    SELECT 1
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            var block = Assert.Single(blocks, b => b.Kind == "CodeLegend");
            Assert.Contains("0:일반", block.Anchors);
            Assert.Contains("2:Cafe24", block.Anchors);
        }

        [Fact]
        public void Extract_HeaderComment_ShouldBeClassifiedAsHeader()
        {
            const string ddl = @"-- Return Value : =0->성공, <>0->실패
--- 내부 SP 호출 : NONE
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT 1
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            Assert.Contains(blocks, b => b.Kind == "Header" && b.Text.Contains("NONE"));
        }

        [Fact]
        public void Extract_PlainProseComment_ShouldHaveNoAnchors()
        {
            // 앵커가 없으면 프롬프트에만 싣고 L1은 대조하지 않는다.
            // 억지로 대조하면 오탐만 낳는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    --매입요청일(D)+1 : 집계 고려
    SELECT 1
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            Assert.All(blocks, b => Assert.Empty(b.Anchors));
        }

        [Fact]
        public void Extract_EmptyDdl_ShouldReturnEmpty()
        {
            Assert.Empty(SourceCommentExtractor.Extract(null));
            Assert.Empty(SourceCommentExtractor.Extract("   "));
        }
    }
}
