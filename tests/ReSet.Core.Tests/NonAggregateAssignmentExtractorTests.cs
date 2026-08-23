using System.Collections.Generic;
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class NonAggregateAssignmentExtractorTests
    {
        private static readonly Dictionary<string, string> NoColumns = new();

        [Fact]
        public void Extract_NonAggregateAssignment_ShouldSayThePreviousValueSurvives()
        {
            // UP_UTIL_SETTLE_PROC_ETC:72 실측 - SELECT @v_intID = ID는 비집계 대입이라
            // 무결과 시 직전 값이 남는다. 79행의 집계 대입(MAX)은 무결과 시 NULL이
            // 대입되므로 정반대다. 둘이 표에 나란히 놓여야 대비가 보인다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intID INT
    SELECT @v_intID = ID
    FROM   dbo.TSettleMiss WITH(NOLOCK)
    WHERE  ClientID = '1'
END";

            var facts = NonAggregateAssignmentExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("@v_intID", fact.Variable);
            Assert.Equal("ID", fact.Column);
            Assert.Equal(6, fact.Line);
            Assert.Contains("직전 값", fact.Sentence);
            Assert.Contains("일어나지 않습니다", fact.Sentence);
        }

        [Fact]
        public void Extract_QualifiedColumn_ShouldKeepTheMultiPartNameAsWritten()
        {
            // 표의 대상 칸은 원문 대조 대상이다. 별칭을 떼면 L1의 행 대조가 어긋난다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = A.ID FROM dbo.TA AS A WITH(NOLOCK)
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.Equal("A.ID", fact.Column);
        }

        [Fact]
        public void Extract_TwoAssignmentsInOneSelect_ShouldReportBoth()
        {
            // 한 SELECT가 변수 둘을 대입하면 둘 다 같은 무결과 동작을 겪는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @a INT, @b INT
    SELECT @a = C1, @b = C2 FROM dbo.TA WITH(NOLOCK)
END";

            var facts = NonAggregateAssignmentExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Equal(new[] { "@a", "@b" }, facts.Select(f => f.Variable).ToArray());
        }

        [Fact]
        public void Extract_AggregateAssignment_ShouldNotBeCollected()
        {
            // 집계는 AggregateAssignmentExtractor의 몫이다. 두 추출기가 같은 문장을
            // 각각 내면 표에 모순되는 두 행이 실린다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = MAX(ID) FROM dbo.TA WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
            // 격리 - 같은 문장을 집계 쪽은 실제로 담는다. 이 단언이 없으면 위의 빈 목록이
            // "아무도 담지 않는다"인지 "집계 쪽이 담는다"인지 구분되지 않는다.
            Assert.Single(AggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_AggregateInsideArithmetic_ShouldNotBeCollected()
        {
            // UP_UTIL_SETTLE_PROC_ETC:101 실측 - MAX(ID)+1은 최상위가 이항식이라
            // AggregateAssignmentExtractor가 담지 않는다. 그렇다고 비집계도 아니다:
            // 집계 질의라 무결과여도 한 행을 돌려주므로 대입이 일어난다. 담으면 거짓이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = MAX(ID)+1 FROM dbo.TA WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_AggregateWrappedInIsNull_ShouldNotBeCollected()
        {
            // UP_UTIL_SETTLE_PROC_ETC:116 실측 - ISNULL(SUM(...),0)도 집계 질의다.
            // 무결과 시 한 행이 돌아오고 0이 대입된다 - 직전 값이 남는다는 문장과 반대다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v MONEY
    SELECT @v = ISNULL(SUM(CAST(CLTotal AS MONEY)),0) FROM dbo.TA WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_AssignmentOfLiteral_ShouldNotBeCollected()
        {
            // SELECT @v = 1은 조회가 아니라 대입이다. 무결과라는 개념이 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = 1
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_ColumnAssignmentWithoutFrom_ShouldNotBeCollected()
        {
            // FROM 절 판정만을 겨냥한다 - 대상은 리터럴이 아니라 컬럼 참조이므로
            // 식 모양 판정은 이 문장을 걸러내지 못한다. FROM이 없으면 한 행이 반드시
            // 돌아와 대입이 일어나므로 "무결과 시 직전 값이 남는다"가 거짓이 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = ID
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_UnparseableDdl_ShouldReturnEmpty()
        {
            // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
            Assert.Empty(NonAggregateAssignmentExtractor.Extract("CREATE PROCEDURE ((("));
            Assert.Empty(NonAggregateAssignmentExtractor.Extract(null));
            Assert.Empty(NonAggregateAssignmentExtractor.Extract("   "));
        }

        [Fact]
        public void Collect_NonAggregateAssignment_ShouldLandInTheExecutionSemanticsTable()
        {
            // 추출기가 사실을 내도 Collect에 갈래가 없으면 표에 한 행도 실리지 않는다.
            // 이 배선은 ExecutionSemanticsFacts의 몫이지만, 이 종류를 더한 Task가
            // 함께 책임지므로 여기에 둔다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intID INT
    SELECT @v_intID = ID FROM dbo.TSettleMiss WITH(NOLOCK)
END";

            var facts = ExecutionSemanticsFacts.Collect(ddl, null, null, NoColumns);

            var fact = Assert.Single(
                facts, f => f.Kind == ExecutionSemanticsFacts.NonAggregateAssignmentKind);
            Assert.Equal("비집계 대입", fact.Kind);
            Assert.Equal("6", fact.Line);
            Assert.Equal("SELECT @v_intID = ID", fact.Target);
            Assert.Contains("직전 값", fact.Fact);
        }
    }
}
