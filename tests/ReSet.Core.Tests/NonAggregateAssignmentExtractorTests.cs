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
        public void Extract_NoInitializerAndNoEarlierUse_ShouldSayNullSurvives()
        {
            // UF_GET_CLIENTSECTIONRATE:14 실측 - DECLARE에 초기값이 없고(12행) 이 문장
            // 앞에서 이 변수가 한 번도 쓰이지 않으며 객체에 되돌아가는 흐름이 없다.
            // 그러면 무결과 시 남는 값은 정확히 NULL이다 - 23~25행의 IF @@ROWCOUNT <> 1이
            // 그 NULL을 0으로 덮는 것이 이 함수의 요점이다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_strClientID VARCHAR(20)) RETURNS INT
AS
BEGIN
    DECLARE @po_intAmt INT
    SELECT TOP 1 @po_intAmt = SECTIONAMT
    FROM   dbo.TClientSectionRate WITH(NOLOCK)
    WHERE  CLIENTID = @pi_strClientID
    RETURN @po_intAmt
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));

            Assert.Equal("@po_intAmt", fact.Variable);
            Assert.Equal("SECTIONAMT", fact.Column);
            Assert.Contains("대입 자체가 일어나지 않습니다", fact.Sentence);
            Assert.Contains("NULL이 그대로 남습니다", fact.Sentence);
        }

        [Fact]
        public void Extract_PrecedingSetOnSameVariable_ShouldNotClaimNull()
        {
            // UP_UTIL_SETTLE_PROC_ETC:69·72 실측 - 앞선 SET이 값을 남겼으므로 무결과 시
            // 남는 값은 NULL이 아니다. 어떤 값인지는 기계가 판정할 수 없으므로
            // "이 문장에 도달한 시점의 값"까지만 말한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intID INT
    SET @v_intID = 0
    SELECT @v_intID = ID
    FROM   dbo.TSettleMiss WITH(NOLOCK)
    WHERE  ClientID = '1'
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));

            Assert.Equal("@v_intID", fact.Variable);
            Assert.Equal(7, fact.Line);
            Assert.Contains("대입 자체가 일어나지 않습니다", fact.Sentence);
            Assert.Contains("이 문장에 도달한 시점의 값", fact.Sentence);
            Assert.DoesNotContain("NULL", fact.Sentence);
        }

        [Fact]
        public void Extract_DeclareWithInitializer_ShouldNotClaimNull()
        {
            // 초기값이 있으면 무결과 시 남는 값은 NULL이 아니라 그 초기값이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT = 0
    SELECT @v = ID FROM dbo.TA WITH(NOLOCK)
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.DoesNotContain("NULL", fact.Sentence);
        }

        [Fact]
        public void Extract_SameNameDeclaredWithInitializerInAnotherBatch_ShouldNotClaimNull()
        {
            // 이름은 배치마다 다시 선언된다. 판정 재료는 조각 전체에서 모으므로, 앞
            // 배치의 초기값 없는 DECLARE를 보고 뒤 배치의 초기값 있는 변수에 NULL을
            // 단정할 수 있다 - 같은 이름이 두 모양으로 선언돼 있으면 판정하지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.A
AS
BEGIN
    DECLARE @v INT
    SELECT 1
END
GO
CREATE PROCEDURE dbo.B
AS
BEGIN
    DECLARE @v INT = 0
    SELECT @v = ID FROM dbo.TA WITH(NOLOCK)
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.DoesNotContain("NULL", fact.Sentence);
        }

        [Fact]
        public void Extract_InsideWhileLoop_ShouldNotClaimNull()
        {
            // 루프는 원문 순서를 뒤집는다 - 두 번째 반복에서는 *뒤에 있는* SET이 이미
            // 실행된 뒤 이 문장에 도달한다. 원문에서 앞선 대입이 없다는 것만으로
            // NULL을 단정하면 거짓이 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    WHILE (@@FETCH_STATUS = 0) BEGIN
        SELECT @v = ID FROM dbo.TA WITH(NOLOCK)
        SET @v = 1
    END
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.DoesNotContain("NULL", fact.Sentence);
        }

        [Fact]
        public void Extract_WithBackwardGoto_ShouldNotClaimNull()
        {
            // GOTO도 같은 이유로 원문 순서를 무너뜨린다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    Again:
    SELECT @v = ID FROM dbo.TA WITH(NOLOCK)
    SET @v = 1
    GOTO Again
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.DoesNotContain("NULL", fact.Sentence);
        }

        [Fact]
        public void Extract_OutputParameter_ShouldNotClaimNull()
        {
            // 매개변수는 호출자가 값을 준다 - DECLARE 변수와 달리 NULL로 시작한다는
            // 보장이 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @po_intID INT OUTPUT
AS
BEGIN
    SELECT @po_intID = ID FROM dbo.TA WITH(NOLOCK)
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.DoesNotContain("NULL", fact.Sentence);
        }

        [Fact]
        public void Extract_AggregateInsideDerivedTable_ShouldNotBeCollected()
        {
            // 집계는 식이 아니라 FROM 절에도 산다. GROUP BY 없는 파생 테이블은 원본이
            // 비어도 한 행을 돌려주므로 이 SELECT는 0행이 되지 않는다 - "무결과면
            // 대입이 없다"를 읽는 사람은 정반대로 이해하게 된다. 담지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = X.MaxID FROM (SELECT MAX(ID) AS MaxID FROM dbo.TA) X
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
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
            // UF_GET_COLLECTYMD:29~30이 그 실물이다.
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
            // 앞 변수의 참조가 뒤 변수의 판정을 오염시키면 안 된다 - 둘 다 NULL 갈래다.
            Assert.All(facts, f => Assert.Contains("NULL이 그대로 남습니다", f.Sentence));
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
            // 무결과 시 한 행이 돌아오고 0이 대입된다 - 대입이 없다는 문장과 반대다.
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
            // 돌아와 대입이 일어나므로 무결과를 전제한 문장이 거짓이 된다.
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
            Assert.Contains("NULL이 그대로 남습니다", fact.Fact);
        }
    }
}
