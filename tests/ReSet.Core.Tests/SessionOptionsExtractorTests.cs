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

        [Fact]
        public void Extract_StrayAsInParameterDefaultAndCommentedOption_ShouldNotFalsePositive()
        {
            // 리뷰 실측(Fix Round 1) - 정규식 CreateBodyStartRegex의 지연 매치(lazy match)는
            // 파라미터 기본값 문자열 리터럴 안의 "AS"에서 멈춰 진짜 본문 AS 앞에서
            // 스캔을 시작한다. 그 결과 CREATE와 진짜 AS 사이의 블록 주석 속
            // "SET ARITHABORT ON"이 본문 옵션으로 오탐된다. AST는 문자열 리터럴을
            // AS 키워드로, 주석을 문장으로 착각하지 않으므로 이 오탐이 구조적으로
            // 발생할 수 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @Desc VARCHAR(100) = 'value used AS default'
    /*
    SET ARITHABORT ON  -- old note, not real code
    */
AS
BEGIN
    SET NOCOUNT ON
END";

            var options = SessionOptionsExtractor.Extract(ddl);

            Assert.DoesNotContain("ARITHABORT", options);
            Assert.Contains("NOCOUNT", options);
        }

        [Fact]
        public void Extract_CommentedOutSetInBody_ShouldNotBeCaptured()
        {
            // 본문 안의 주석 처리된 SET은 실제 문장이 아니다. 정규식은 라인 시작(^\s*SET)만
            // 보므로 "-- SET NOCOUNT ON" 같은 줄 주석은 이미 걸러지지만, AST 전환 후에도
            // 이 보장이 유지되는지 명시적으로 고정한다 - 주석은 애초에 파스 트리의 문장
            // 노드가 되지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    -- SET NOCOUNT ON (예전 메모, 실제 코드 아님)
    SELECT 1
END";

            Assert.Empty(SessionOptionsExtractor.Extract(ddl));
        }
    }
}
