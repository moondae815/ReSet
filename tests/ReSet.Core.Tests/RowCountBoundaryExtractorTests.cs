using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class RowCountBoundaryExtractorTests
    {
        [Fact]
        public void Extract_RowCountReadAfterAnIfStatement_ShouldReportTheBoundary()
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
            Assert.Equal(RowCountBoundaryExtractor.SemanticsSentence, fact.Sentence);
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

        [Fact]
        public void SemanticsSentence_ShouldCoverBothBranchesInsteadOfClaimingAlwaysTrue()
        {
            // Fix Round 1 - 조정자가 로컬 Docker SQL Server 2022 16.0.4255.1에서
            // 2026-08-22에 나란히 재현한 결과:
            //   CASE X(직전 IF의 분기가 건너뛰어짐)  -> @@ROWCOUNT가 0으로 리셋 -> 조건 참(RESET_TO_0)
            //   CASE Y(직전 IF의 분기가 실행되고 행에 영향을 주는 문장으로 끝남)
            //     -> @@ROWCOUNT가 그 마지막 문장의 행 수로 남음 -> 조건이 거짓일 수 있음(NOT_RESET)
            // "이 조건은 항상 참이다"는 CASE Y에서 거짓이므로 상수 문장은 그 단정을
            // 담아서는 안 되고, 두 경우 모두를 참으로 서술해야 한다.
            var sentence = RowCountBoundaryExtractor.SemanticsSentence;

            Assert.DoesNotContain("항상 참", sentence);
            Assert.Contains("건너뛰", sentence);
            Assert.Contains("실행", sentence);
        }

        [Fact]
        public void Extract_TopLevelStatementList_WithoutOuterBeginEnd_IsCovered()
        {
            // 방문 지점 회귀 테스트(Fix Round 1) - 프로시저 본문이 BEGIN...END로
            // 감싸이지 않은 경우에도 최상위 StatementList가 방문돼야 한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
    DECLARE @x INT
    SELECT @x = c FROM dbo.T
    IF @@ROWCOUNT < 1 SET @x = 1
    IF @@ROWCOUNT < 1 SET @x = 2
";

            Assert.Single(RowCountBoundaryExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_NestedInsideIfBeginEndBlock_IsCovered()
        {
            // 방문 지점 회귀 테스트(Fix Round 1) - IF 블록 안에 중첩된 StatementList도
            // 방문돼야 한다(바깥 BEGIN...END와 무관하게).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @x INT
    IF 1 = 1
    BEGIN
        SELECT @x = c FROM dbo.T
        IF @@ROWCOUNT < 1 BEGIN SET @x = 1 END
        IF @@ROWCOUNT < 1 BEGIN SET @x = 2 END
    END
END";

            Assert.Single(RowCountBoundaryExtractor.Extract(ddl));
        }
    }
}
