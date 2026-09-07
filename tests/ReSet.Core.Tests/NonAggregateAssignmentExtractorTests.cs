using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
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
            Assert.Equal("SECTIONAMT", fact.Expression);
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
        public void Extract_AggregateInsideCommonTableExpression_ShouldNotBeCollected()
        {
            // 파생 테이블 가드와 같은 함정인데 붙는 자리가 다르다. WITH 절은 FromClause
            // 아래가 아니라 문장(StatementWithCtesAndXmlNamespaces)에 달려 있어, FROM만
            // 훑는 가드는 이 집계를 보지 못한다. GROUP BY 없는 집계 CTE는 원본이 비어도
            // 한 행을 돌려주므로 이 SELECT는 0행이 되지 않는다 - 담으면 표의 문장이
            // 정확히 반대를 말한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    ;WITH c AS (SELECT MAX(ID) AS m FROM dbo.TA)
    SELECT @v = c.m FROM c
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_NonAggregateCommonTableExpression_ShouldAlsoBeSkipped()
        {
            // 감수한 대가다. CTE 본문이 비집계면 이 문장은 실제로 0행이 될 수 있어 사실
            // 문장이 참이지만, 그걸 가려내려면 CTE 본문마다 집계를 판정하고 어느 CTE가
            // 이 FROM에 실제로 닿는지까지 따라가야 한다. 이 추출기의 원칙은 "거짓 행보다
            // 없는 행"이므로 WITH를 단 문장은 통째로 침묵한다. 이 단언이 없으면 가드의
            // 폭이 문서에만 있고 코드에는 없게 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    ;WITH c AS (SELECT ID FROM dbo.TA)
    SELECT @v = c.ID FROM c
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_SiblingStatementOfACteStatement_ShouldStillBeCollected()
        {
            // 가드는 WITH를 단 **그 문장**까지다. 같은 객체에 CTE가 하나 있다고 객체
            // 전체가 침묵하면 코퍼스 행이 조용히 사라진다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @a INT, @b INT
    ;WITH c AS (SELECT MAX(ID) AS m FROM dbo.TA)
    SELECT @a = c.m FROM c
    SELECT @b = ID FROM dbo.TB WITH(NOLOCK)
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.Equal("@b", fact.Variable);
            Assert.Equal("ID", fact.Expression);
        }

        [Fact]
        public void Extract_CompoundAssignment_ShouldNotBeCollected()
        {
            // `SELECT @v += col`도 SelectSetVariable로 담기는데 대상 칸은 `SELECT @v = col`로
            // 렌더된다 - 원문에 없는 문장이 표에 실린다. 형제 LoopVariableResetExtractor가
            // 같은 자리에서 AssignmentKind != Equals를 거르는 것과 같은 규칙이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT = 0
    SELECT @v += ID FROM dbo.TA WITH(NOLOCK)
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
            Assert.Equal("A.ID", fact.Expression);
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

        [Fact]
        public void Extract_IifOverTwoColumns_IsCarriedWithTheBranchExpressionVerbatim()
        {
            // UF_GET_COMM4CLIENT4PARTIALCANCEL:43 실측 - 축 A 🔴 #1 이다. 이 대입이 표에
            // 없어서 명세서가 수수료율 분기를 한 줄도 서술하지 않았고, 그 함수를 부르는
            // 모든 SP 의 금액이 무이자 여부에 따라 갈리는 사실이 통째로 사라졌다.
            //
            // 대상 칸은 분기식을 **축자로** 실어야 한다. 컬럼 이름만 실으면 어느 쪽
            // 컬럼인지가 조건에 달렸다는 사실이 그대로 지워진다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_intFreeInterestFlag INT) RETURNS INT
AS
BEGIN
    DECLARE @v_intCommissionRate INT
    SELECT TOP 1
           @v_intCommissionRate = IIF(@pi_intFreeInterestFlag IN (0,2), A.CommissionRate, A.FreeInterestInstCommRate)
    FROM   dbo.TClientCardContractDtlHist A WITH(NOLOCK)
    WHERE  A.ClientID = '1'
    RETURN @v_intCommissionRate
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));

            Assert.Equal("@v_intCommissionRate", fact.Variable);
            Assert.Equal(
                "IIF(@pi_intFreeInterestFlag IN (0,2), A.CommissionRate, A.FreeInterestInstCommRate)",
                fact.Expression);
            Assert.Contains("대입 자체가 일어나지 않습니다", fact.Sentence);
        }

        [Fact]
        public void Extract_CaseOverColumns_IsCarried()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = CASE WHEN A.Flag = 1 THEN A.RateA ELSE A.RateB END
    FROM   dbo.T A WITH(NOLOCK)
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));

            Assert.Equal("CASE WHEN A.Flag = 1 THEN A.RateA ELSE A.RateB END", fact.Expression);
        }

        [Fact]
        public void Extract_IsNullOverAColumn_IsCarriedWithTheWrapperInTheTargetCell()
        {
            // 감쌈이 벗겨져 갈래가 정해지지만, 대상 칸에는 **감싼 원문**이 실린다.
            // 확정 문장은 그대로 참이다 - 행이 0이면 대입 자체가 없으므로 ISNULL 이
            // 돌 자리가 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = ISNULL(A.Rate, 0)
    FROM   dbo.T A WITH(NOLOCK)
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));

            Assert.Equal("ISNULL(A.Rate, 0)", fact.Expression);
            Assert.Contains("대입 자체가 일어나지 않습니다", fact.Sentence);
        }

        [Fact]
        public void Extract_IifWithALiteralBranch_IsCarried()
        {
            // 2 회차 넓힘 - 분기 결과가 리터럴이어도 이제 담는다(구 시험명
            // Extract_IifWithANonColumnBranch_StaysSilent 가 이 방향으로 뒤집혔다).
            // 이 시험이 원래 지키던 것("모르는 모양은 침묵")은 아래 중첩 분기식·
            // 함수 호출·하위 질의 세 시험이 대신 지킨다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = IIF(A.Flag = 1, A.RateA, 0)
    FROM   dbo.T A WITH(NOLOCK)
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.Equal("IIF(A.Flag = 1, A.RateA, 0)", fact.Expression);
        }

        [Fact]
        public void Extract_IifWithArithmeticOfColumnsBranch_IsCarried()
        {
            // 2 회차 넓힘 - 분기 결과가 컬럼 간 산술식이어도 담는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = IIF(A.Flag = 1, A.RateA - B.RateB, A.RateC)
    FROM   dbo.T A WITH(NOLOCK) JOIN dbo.T2 B WITH(NOLOCK) ON B.ID = A.ID
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.Equal("IIF(A.Flag = 1, A.RateA - B.RateB, A.RateC)", fact.Expression);
        }

        [Fact]
        public void Extract_NestedCaseInABranch_StaysSilent()
        {
            // 좁게 유지 - 분기 결과 안에 또 분기식(중첩 CASE)이 오면 담지 않는다.
            // UF_GET_COLLECTYMD:31·UIF_SettleYMD:39 실측 모양이다. 통째 완화를 실측해
            // 보니 이 모양이 원본 줄 주석을 셀 안으로 끌고 들어와(설계서 §10-1) 배제됐다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = CASE WHEN A.Flag = 1 THEN CASE WHEN A.Flag2 = 1 THEN A.RateA ELSE A.RateB END ELSE A.RateC END
    FROM   dbo.T A WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_ArithmeticWrappingANestedCaseInABranch_StaysSilent()
        {
            // 좁게 유지 - 분기 결과가 "산술식이 분기식을 감싼" 모양이어도 담지 않는다.
            // 산술식 재귀가 분기 재허용을 실어 보내면(뮤턴트) 여기서 뚫린다 - 그 뮤턴트를
            // 돌려 이 시험이 실패로 잡는 것을 확인했다(리뷰 라운드 1, FINDING I2).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = IIF(A.Flag = 1, A.RateA * CASE WHEN A.Flag2 = 1 THEN A.RateB ELSE A.RateC END, A.RateD)
    FROM   dbo.T A WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_ParenthesizedCaseInABranch_StaysSilent()
        {
            // 좁게 유지 - 분기 결과가 괄호로 감싼 분기식이어도 담지 않는다. 괄호 벗기기가
            // 분기 재허용을 실어 보내면(뮤턴트) 여기서 뚫린다(리뷰 라운드 1, FINDING I2).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = IIF(A.Flag = 1, (CASE WHEN A.Flag2 = 1 THEN A.RateB ELSE A.RateC END), A.RateD)
    FROM   dbo.T A WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_UnaryWrappingANestedCaseInABranch_StaysSilent()
        {
            // 좁게 유지 - 분기 결과가 단항 부호로 감싼 분기식이어도 담지 않는다. `-CASE …`는
            // `UnaryExpression{ Expression = SearchedCaseExpression }`로 파싱된다(직접 재서
            // 확인). Binary·Paren·Unary 셋 다 "매개변수를 실어 나르는" 재귀 구조를 공유하므로
            // 셋 다 뮤턴트로 잠가야 한다(리뷰 라운드 2 - 이 시험이 Unary를 마저 잠근다).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = IIF(A.Flag = 1, -CASE WHEN A.Flag2 = 1 THEN A.RateB ELSE A.RateC END, A.RateD)
    FROM   dbo.T A WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_FunctionCallInABranch_StaysSilent()
        {
            // 좁게 유지 - 분기 결과가 함수 호출이면 담지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = IIF(A.Flag = 1, dbo.UF_GET_RATE(A.ClientID), A.RateB)
    FROM   dbo.T A WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_SubqueryInABranch_StaysSilent()
        {
            // 좁게 유지 - 분기 결과가 하위 질의면 담지 않는다.
            // UF_Get_CLComm4MobileCo:25 실측 모양이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = CASE WHEN A.Flag = 1 THEN (SELECT TOP 1 Rate FROM dbo.T2) ELSE A.RateB END
    FROM   dbo.T A WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_IsNullWrappingAnAggregate_StaysSilentHereAndBelongsToTheAggregateSide()
        {
            // 두 갈래는 배타적이다. 벗긴 안쪽이 집계면 집계 추출기가 가져간다 -
            // 집계는 잎이 아니므로 IsCapturableExpression 에 걸리지 않는 것이 그 기전이다.
            // 이 문장이 어느 표에도 안 실리던 것이 UP_UTIL_SETTLE_PROC_ETC 🟠 이었다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = ISNULL(SUM(A.CLTotal), 0)
    FROM   dbo.T A WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_HavingWithoutGroupByOverLiteral_ShouldNotBeCollected()
        {
            // 2 회차가 연 거짓 행 경로 - GROUP BY 없는 HAVING은 T-SQL이 전체를 한
            // 그룹으로 묶어 행이 0건이어도 1행을 돌려준다. `SELECT @v = 1`은 최상위가
            // 리터럴이라 IsCapturableExpression을 통과하는데, HAVING이 있으면 "무결과
            // 시 대입이 일어나지 않는다"는 확정 문장 자체가 거짓이 된다
            // (`HAVING COUNT(*) = 0`은 T가 비면 참이 되어 1행이 돌아오고 대입이 일어난다).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = 1 FROM dbo.T WITH(NOLOCK) HAVING COUNT(*) = 0
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_HavingWithoutGroupByOverColumn_ShouldNotBeCollected()
        {
            // GROUP BY 없는 HAVING은 우변 모양(컬럼 참조여도)과 무관하게 거짓이 된다 -
            // HAVING이 참이면 행이 반드시 1건 돌아온다는 사실은 SELECT 목록이 아니라
            // 절 자체에서 나온다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = A.x FROM dbo.T A WITH(NOLOCK) HAVING COUNT(*) > 0
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_SiblingAggregateInSameSelect_ShouldSilenceOnlyTheNonAggregateSide()
        {
            // 상반된 확정 문장 둘이 한 표에 나란히 실리던 자리 - `@a = 1`은 비집계
            // 추출기가, `@b = COUNT(*)`는 집계 추출기가 각각 담아 정반대 문장을 냈다.
            // 같은 SelectElements 안에 집계를 품은 형제가 있으면 이 QuerySpecification
            // 전체를 비집계 쪽에서 침묵한다 - 집계 쪽은 그대로 담아야 한다(격리).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @a INT, @b INT
    SELECT @a = 1, @b = COUNT(*) FROM dbo.T WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
            // 격리 - 집계 쪽은 이 가드와 무관하게 그대로 담겨야 한다. 이 단언이 없으면
            // 위의 빈 목록이 "이 문장 전체가 사라졌다"인지 "형제만 정확히 걸렀다"인지
            // 구분되지 않는다.
            Assert.Single(AggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_SiblingApproxCountDistinctInSameSelect_ShouldSilenceTheNonAggregateSide()
        {
            // ★ 둘째 재검토 - AggregateNames 목록에 APPROX_COUNT_DISTINCT가 없어서
            // HasAggregateSibling(같은 목록을 쓴다)이 이 형제를 못 보고 `@a = 1`을
            // 그대로 담던 자리. 원본이 비어도 APPROX_COUNT_DISTINCT는 여느 집계처럼
            // 1행을 돌려주므로 HAVING·GROUP BY와 같은 함정이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @a INT, @b INT
    SELECT @a = 1, @b = APPROX_COUNT_DISTINCT(A.x) FROM dbo.T A WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_AggregateInsideDerivedTable_ApproxCountDistinct_ShouldNotBeCollected()
        {
            // ★ 둘째 재검토 - 같은 목록 누락이 선재 FROM 가드(AggregateInFromDetector)도
            // 새게 만든다. 파생 테이블 안의 APPROX_COUNT_DISTINCT를 못 보면 원본이
            // 비어도 1행이 돌아온다는 사실을 놓치고 담아 버린다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = A.m FROM (SELECT APPROX_COUNT_DISTINCT(x) AS m FROM dbo.U) A
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_SiblingCaseWithAggregateInSameSelect_ShouldSilenceTheNonAggregateSide()
        {
            // 리뷰가 지목한 파생형 - 형제가 맨 집계가 아니라 분기식으로 감싼 집계여도
            // 같은 함정이다(집계는 잎이 아니라 IsCapturableExpression에 안 걸리므로
            // 이 형제 자체는 어차피 비집계 추출기에 안 담기지만, 그 형제가 있다는
            // 사실만으로 같은 SELECT의 @a도 침묵해야 한다).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @a INT, @b INT
    SELECT @a = CASE WHEN 1 = 1 THEN 1 ELSE 2 END, @b = MAX(A.X)
    FROM   dbo.T A WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_NonSetVariableSiblingInSameSelect_ShouldSilenceTheAssignment()
        {
            // `SELECT @v = ID, Name FROM T`처럼 대입과 일반 컬럼 조회가 한 SELECT에
            // 섞이면 같은 SelectElements 안에 SelectSetVariable이 아닌 요소가 있다.
            // 이 모양은 코퍼스에 0건이지만(클래스 주석), 침묵이 안전한 선택이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = ID, Name FROM dbo.T WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_GroupByWithHaving_StaysSilentByTheSameBlanketGuard()
        {
            // 판단이 필요한 자리 - GROUP BY가 있으면 원본이 비어도 그룹이 0개이므로
            // 이 SELECT는 실제로 0행이 될 수 있고, "무결과 시 대입이 일어나지 않는다"는
            // 확정 문장은 참이다. 그런데도 침묵을 택한다: 가드를
            // `HavingClause != null`(GROUP BY 유무를 안 가림) 하나로 단순하게 유지하는
            // 것이 ROLLUP/CUBE/GROUPING SETS 등 GROUP BY의 여러 변형까지 옳게 가리는
            // 조건을 새로 만드는 것보다 안전하다 - 조건이 한 군데라도 새면 정반대
            // 문장이 표에 실리고, 이 표는 「수정 금지」라 뒤에서 거를 장치가 없다
            // (클래스 주석 "집계는 CTE에도 산다"와 같은 논리). 코퍼스에 이 모양이
            // 0건이라 이 선택의 비용은 지금 0이다(대장 52행 불변, 아래 코퍼스 시험).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = A.x FROM dbo.T A WITH(NOLOCK) GROUP BY A.x HAVING COUNT(*) > 0
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        // 아래 일곱은 ★ 둘째 재검토가 연 자리 - HavingClause 가드와 같은 진리 조건이
        // GroupByClause 쪽에는 없었다. 총계 그룹화 집합(grand total grouping set)은
        // 원본이 비어도 그룹을 정확히 1개 만들어 1행을 돌려주므로, "무결과 시 대입이
        // 일어나지 않는다"는 확정 문장이 거짓이 된다 - HAVING과 완전히 같은 함정이다.
        // 파싱 모양은 짐작하지 않고 직접 재서 확인했다(ScriptDom TSql160Parser로 각
        // 모양을 파싱해 QuerySpecification.GroupByClause를 리플렉션으로 관찰) - 일곱
        // 모두 GroupByClause가 non-null이고 GroupingSpecifications가 각각
        // GrandTotalGroupingSpecification · GroupingSetsGroupingSpecification(2회,
        // 중첩 유무 무관) · RollupGroupingSpecification · CubeGroupingSpecification ·
        // ExpressionGroupingSpecification(WITH ROLLUP은 GroupByOption=Rollup, 평범한
        // GROUP BY는 GroupByOption=None)이다. 그래서 가드는 변형을 하나하나 가리지
        // 않고 `GroupByClause != null` 하나로 통째 침묵한다(HavingClause와 같은 층,
        // 같은 자리) - 변형을 열거해서 좁히면 하나라도 새는 순간 같은 거짓 행이
        // 다시 열리고, 코퍼스 대가는 지금 0이다(CountGuardInputs가 groupByClauses로
        // 못박는다).
        //
        // 일곱째(평범한 GROUP BY, 총계 그룹이 안 생기는 것)까지 침묵시키는 것은
        // 보수적 선택이다 - `... GROUP BY A.x`는 원본이 비면 그룹이 0개이므로 실제로는
        // "무결과 시 대입이 일어나지 않는다"가 참이다. 그런데도 함께 침묵하는 이유는
        // ExpressionGroupingSpecification 하나만으로는 이 SELECT가 총계 그룹화
        // 집합인지(GrandTotal·빈 GroupingSets 원소) 아닌지를 가리는 조건이 따로
        // 필요하고, 그 조건이 새면 다시 거짓 행이 열리기 때문이다 - 이 시험이 그
        // 선택을 코드가 아니라 시험에서 읽히게 못박는다.
        [Fact]
        public void Extract_GroupByEmptyGroupingSet_ShouldNotBeCollected()
        {
            // `GROUP BY ()`는 GrandTotalGroupingSpecification으로 파싱된다 - 원본이
            // 비어도 총계 그룹 1개가 생겨 1행이 돌아온다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = 1 FROM dbo.T WITH(NOLOCK) GROUP BY ()
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_GroupByGroupingSetsWithEmptySet_ShouldNotBeCollected()
        {
            // `GROUPING SETS (())`는 GroupingSetsGroupingSpecification 하나로
            // 파싱되고 그 안의 빈 집합이 총계 그룹과 같은 효과를 낸다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = 1 FROM dbo.T WITH(NOLOCK) GROUP BY GROUPING SETS (())
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_GroupByGroupingSetsMixedWithEmptySet_ShouldNotBeCollected()
        {
            // 빈 집합이 다른 원소와 섞여도 같은 GroupingSetsGroupingSpecification
            // 노드이므로 같은 가드에 걸린다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = 1 FROM dbo.T WITH(NOLOCK) GROUP BY GROUPING SETS ((x), ())
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_GroupByRollup_ShouldNotBeCollected()
        {
            // `ROLLUP(A.x)`는 RollupGroupingSpecification - 최상위 총계 행을
            // 포함하므로 원본이 비어도 1행이 돌아온다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = A.x FROM dbo.T A WITH(NOLOCK) GROUP BY ROLLUP(A.x)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_GroupByCube_ShouldNotBeCollected()
        {
            // `CUBE(A.x)`도 같은 이유로 CubeGroupingSpecification이 총계 행을 낸다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = A.x FROM dbo.T A WITH(NOLOCK) GROUP BY CUBE(A.x)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_GroupByWithRollupOption_ShouldNotBeCollected()
        {
            // `GROUP BY A.x WITH ROLLUP`은 ExpressionGroupingSpecification이지만
            // GroupByOption이 Rollup이라 우변이 맨 컬럼이어도 합법이고(§9-9와 달리
            // 이 형은 1 회차부터 열려 있던 자리다 - base에 GroupBy 문자열이 없었다),
            // 총계 행이 돌아온다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = A.x FROM dbo.T A WITH(NOLOCK) GROUP BY A.x WITH ROLLUP
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_PlainGroupBy_ShouldNotBeCollected_ConservativeChoice()
        {
            // 총계 그룹이 안 생기는 평범한 GROUP BY다 - 원본이 비면 실제로 0행이
            // 돌아와 확정 문장이 참이다. 그런데도 가드가 GroupByClause 유무만 보고
            // 변형을 가리지 않기로 했으므로(위 블록 주석) 이 모양도 함께 침묵한다.
            // 이 시험은 그 보수적 선택 자체를 못박는다 - 다음 사람이 "왜 참인
            // 문장까지 침묵시키지"를 코드가 아니라 여기서 읽도록.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = A.x FROM dbo.T A WITH(NOLOCK) GROUP BY A.x
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        // ============================================================================
        // 3 회차(설계서 §12) - 진리 조건을 단일 재귀 술어로 접은 뒤의 시험군.
        //
        // 위의 아홉(HAVING 둘 · GROUP BY 일곱)은 전부 **최상위** 변형만 잠근다 - 3 회차
        // 검토가 정확히 그 지점을 지적했다("단위 시험 일곱 개는 최상위 변형만 잠근다").
        // 아래 두 Theory(층을 바꾼 같은 변형)가 그 구멍을 메운다 - 같은 모양(총계
        // 그룹화 집합)을 최상위 → 파생 → APPLY → 2 겹 중첩 넷으로 반복해, 재귀가
        // "어느 층인가"와 무관하게 같은 판정을 내리는지 잠근다. 이 술어를 재귀가
        // 아닌 자기 층만 보는 뮤턴트로 되돌리면 파생·APPLY·2 겹 중첩 세 층이 깨진다
        // (아래 MUTANTS 절 - 3 회차 보고서에 수치가 있다).
        // ============================================================================

        public static IEnumerable<object[]> PlainColumnPassthroughAcrossLayers()
        {
            yield return new object[]
            {
                "top-level",
                @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = A.x FROM dbo.T A WITH(NOLOCK)
END"
            };
            yield return new object[]
            {
                "derived",
                @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = D.c FROM (SELECT A.x AS c FROM dbo.T A WITH(NOLOCK)) D
END"
            };
            yield return new object[]
            {
                "apply",
                @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = D.c FROM dbo.T0 WITH(NOLOCK) CROSS APPLY (SELECT A.x AS c FROM dbo.T A WITH(NOLOCK)) D
END"
            };
            yield return new object[]
            {
                "nested-derived",
                @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = D2.c FROM (SELECT D1.c FROM (SELECT A.x AS c FROM dbo.T A WITH(NOLOCK)) D1) D2
END"
            };
        }

        [Theory]
        [MemberData(nameof(PlainColumnPassthroughAcrossLayers))]
        public void Extract_PlainColumnPassthrough_IsCapturedRegardlessOfLayer(string layer, string ddl)
        {
            // §12-6 "담긴다" 표의 파생 테이블 행 - 그런데 파생 테이블 하나만이 아니라
            // APPLY 왼쪽·2 겹 중첩까지 같은 판정이 나와야 재귀가 실제로 층을
            // 없앴다고 말할 수 있다. layer 인자는 실패 메시지에 어느 층이 깨졌는지
            // 드러내려는 것뿐이고 단언 자체에는 쓰지 않는다.
            _ = layer;
            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.Equal("@v", fact.Variable);
        }

        public static IEnumerable<object[]> GrandTotalGroupPoisonAcrossLayers()
        {
            yield return new object[]
            {
                "top-level",
                @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = 1 FROM dbo.T WITH(NOLOCK) GROUP BY ()
END"
            };
            yield return new object[]
            {
                "derived",
                @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = D.c FROM (SELECT 1 AS c FROM dbo.T GROUP BY ()) D
END"
            };
            yield return new object[]
            {
                // ★ §12-6 어긋난 칸(R5) - 설계서는 이 자리를
                // `FROM T0 OUTER APPLY (SELECT 1 c FROM T GROUP BY ()) D`(오염을
                // APPLY의 **오른쪽**에 둔 모양)로 침묵을 예측했지만, §12-3 자신의
                // 표가 "CROSS/OUTER APPLY | 왼쪽이 보장할 때만"이라고 못박고 있다 -
                // 왼쪽 `T0`가 이름 있는 테이블이면 왼쪽만으로 이미 보장이 성립하므로
                // 오른쪽에 무엇이 오든(총계 그룹이든 아니든) 전체 판정이 바뀌지
                // 않는다(OUTER/CROSS APPLY는 왼쪽이 0행이면 오른쪽을 평가조차
                // 하지 않는다 - 왼쪽에 행이 없으면 적용할 행 자체가 없다). 그래서
                // 그 정확한 문구로는 **담긴다**가 실측값이고, 이 시험은 대신 오염을
                // APPLY의 **왼쪽**에 두어(§12-3 규칙이 실제로 검사하는 자리) 같은
                // "APPLY 층"에서 재귀가 작동하는지를 잠근다 - 3 회차 보고서
                // PREDICTION SCORECARD에 이 어긋남을 그대로 적는다.
                "apply-left-poisoned",
                @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = D.c FROM (SELECT 1 AS x FROM dbo.T GROUP BY ()) L CROSS APPLY (SELECT 1 AS c) D
END"
            };
            yield return new object[]
            {
                "nested-derived",
                @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = D2.c FROM (SELECT D1.c FROM (SELECT 1 AS c FROM dbo.T GROUP BY ()) D1) D2
END"
            };
        }

        [Theory]
        [MemberData(nameof(GrandTotalGroupPoisonAcrossLayers))]
        public void Extract_GrandTotalGroupPoison_StaysSilentRegardlessOfLayer(string layer, string ddl)
        {
            // 3 회차의 표제 결함 그대로 - 총계 그룹화 집합은 원본이 비어도 1행을
            // 돌려준다. 예전 코드는 이 진리조건을 최상위 QuerySpecification에서만
            // 봤으므로(GroupByClause 검사가 자기 층만 확인) 같은 모양이 파생·APPLY·
            // 2 겹 중첩 안으로 들어가면 판정에서 벗어나 다시 열렸다. 이 Theory
            // 네 층 전부가 침묵해야 재귀가 실제로 막았다는 증거다.
            _ = layer;
            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_OuterApplyWithNamedTableOnTheLeft_IsCapturedEvenIfRightSideIsPoisoned()
        {
            // ★ §12-6 어긋난 칸(R5)의 실측값을 그대로 잠근다. 설계서 §12-6이
            // "FROM T0 OUTER APPLY (SELECT 1 c FROM T GROUP BY ()) D"에 침묵을
            // 예측한 바로 그 모양이다 - §12-3의 APPLY 규칙("왼쪽이 보장할 때만")을
            // 그대로 따르면 왼쪽 `T0`가 이름 있는 테이블이라 오른쪽의 총계 그룹과
            // 무관하게 **담긴다**. 코드를 비틀어 오른쪽까지 보게 만들지 않는다 -
            // 그러면 §12-3 자신의 규칙(그리고 OUTER/CROSS APPLY의 실제 SQL 의미론 -
            // 왼쪽이 비면 오른쪽은 평가조차 안 된다)을 어기게 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = D.c FROM dbo.T0 WITH(NOLOCK) OUTER APPLY (SELECT 1 AS c FROM dbo.T GROUP BY ()) D
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.Equal("D.c", fact.Expression);
        }

        [Fact]
        public void Extract_HavingInsideDerivedTable_ShouldNotBeCollected()
        {
            // §12-6 침묵 표 - `FROM (SELECT 1 c FROM T HAVING 1=1) D`. GROUP BY 없는
            // HAVING과 같은 함정이 파생 테이블 층에서도 열린다 - 예전 코드는
            // HavingClause 검사를 최상위에서만 했다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = D.c FROM (SELECT 1 c FROM dbo.T HAVING 1 = 1) D
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_PlainGroupByInsideDerivedTable_ShouldNotBeCollected()
        {
            // §12-6 "담긴다" 표 마지막 행의 정정 - 파생 테이블 안쪽의 평범한(총계
            // 그룹이 생기지 않는) GROUP BY는 사전 예측이 "담긴다"에서 "침묵"으로
            // 바뀐 자리다. 안쪽 질의 층 규칙(GroupByClause 유무만 보는 보수적 선택,
            // §11-10 그대로)이 재귀에도 그대로 적용되므로 이 모양도 침묵한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = D.c FROM (SELECT A.x AS c FROM dbo.T A WITH(NOLOCK) GROUP BY A.x) D
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_InnerJoinOfTwoNamedTables_IsCaptured()
        {
            // §12-6 담긴다 표 - INNER JOIN, 양쪽 다 이름 있는 테이블이면 한쪽만
            // 보장해도 전체가 보장된다(공집합 × 무엇 = 공집합).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = B.x FROM dbo.A A WITH(NOLOCK) INNER JOIN dbo.B B WITH(NOLOCK) ON A.id = B.id
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.Equal("B.x", fact.Expression);
        }

        [Fact]
        public void Extract_LeftJoinTargetOnTheRightSide_IsCaptured()
        {
            // §12-6 담긴다 표 - 설계서 §12-4가 검토와 갈리기로 못박은 자리다.
            // `SELECT @v = B.x FROM A LEFT JOIN B ON ...`은 A·B가 둘 다 비면 0행이므로
            // 확정 문장("무결과**면**")이 참이다 - A에 행이 있는데 B에 짝이 없어
            // NULL이 대입되는 경우는 애초에 "무결과"가 아니라 조건문의 전건이
            // 거짓이다. 그래서 LEFT는 왼쪽 기준으로 판정한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = B.x FROM dbo.A A WITH(NOLOCK) LEFT JOIN dbo.B B WITH(NOLOCK) ON A.id = B.id
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.Equal("B.x", fact.Expression);
        }

        [Fact]
        public void Extract_RightJoinWithConstantLeftSide_ShouldNotBeCollected()
        {
            // RIGHT는 오른쪽 기준 - 왼쪽이 상수 원천(VALUES)이면 왼쪽은 보장하지
            // 않지만 판정은 오른쪽만 보므로, 오른쪽이 이름 있는 테이블이면 그래도
            // 담긴다(대비를 위해 왼쪽도 상수로 만들어 반대 방향 - 오른쪽이 상수면
            // RIGHT는 담기지 않는다 - 을 함께 잠근다).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = B.x FROM (VALUES(1)) v(c) RIGHT JOIN dbo.B B WITH(NOLOCK) ON v.c = B.id
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.Equal("B.x", fact.Expression);
        }

        [Fact]
        public void Extract_FullJoinWithOneSideConstant_ShouldNotBeCollected()
        {
            // FULL은 양쪽 다 보장해야 한다 - 한쪽이 상수 원천(VALUES)이면 전체가
            // 보장되지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = B.x FROM (VALUES(1)) v(c) FULL JOIN dbo.B B WITH(NOLOCK) ON v.c = B.id
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_PivotedSource_ShouldNotBeCollected()
        {
            // §12-6 침묵 표 - PIVOT은 보수적으로 막는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = p.x FROM (SELECT a, b FROM dbo.T) s PIVOT (SUM(b) FOR a IN ([1],[2])) p
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_UnpivotedSource_ShouldNotBeCollected()
        {
            // PIVOT과 같은 노드 계열(UnpivotedTableReference) - 같은 이유로 보수적.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = p.val FROM (SELECT a, b, c FROM dbo.T) s UNPIVOT (val FOR col IN (b, c)) p
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_ValuesConstructorSource_ShouldNotBeCollected()
        {
            // §12-6 침묵 표 - `FROM (VALUES(1)) v(c)`. VALUES는 기저 테이블과 무관하게
            // 행을 만든다 - InlineDerivedTable은 항상 "아니오".
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = v.c FROM (VALUES(1)) v(c)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_ConstantDerivedTableWithoutFrom_ShouldNotBeCollected()
        {
            // §12-6 침묵 표 - `FROM (SELECT 1) v`. 안쪽 질의에 FROM이 없으므로
            // 재귀가 "무결과 개념 자체가 없다"로 거짓을 낸다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = v.c FROM (SELECT 1 AS c) v
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_LeftJoinWithConstantLeftSide_ShouldNotBeCollected()
        {
            // §12-6 침묵 표 - `FROM (VALUES(1)) v LEFT JOIN T ON ...`. LEFT는 왼쪽
            // 기준인데 왼쪽이 상수 원천이라 보장하지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = B.x FROM (VALUES(1)) v(c) LEFT JOIN dbo.B B WITH(NOLOCK) ON v.c = B.id
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_TableValuedFunctionSource_ShouldNotBeCollected()
        {
            // §12-6 침묵 표 - `FROM dbo.MyTvf(1)`. TVF는 비었는지 알 수 없다
            // (SchemaObjectFunctionTableReference - "그 밖의 알지 못하는 노드"와
            // 같은 취급이 아니라 명시적으로 "아니오"인 알려진 노드다).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = t.x FROM dbo.MyTvf(1) t
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_ParenthesizedJoinOfTwoNamedTables_IsCaptured()
        {
            // 재귀가 흐르는 자리 중 하나 - 괄호로 감싼 조인(JoinParenthesisTableReference).
            // ScriptDom은 `(A JOIN B ON ...)`를 이 노드로 감싸 안쪽 QualifiedJoin을
            // 별도로 보관한다(3 회차 보고서 PARSE SHAPES가 프로브로 확인). 감쌈을
            // 벗기고 안쪽 조인에 그대로 재귀해야 한다 - 양쪽 다 이름 있는 테이블이면
            // 담긴다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = B.x FROM (dbo.A A WITH(NOLOCK) INNER JOIN dbo.B B WITH(NOLOCK) ON A.id = B.id)
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.Equal("B.x", fact.Expression);
        }

        [Fact]
        public void Extract_ParenthesizedJoinWithConstantLeftSide_ShouldNotBeCollected()
        {
            // 위 시험의 대비 - 괄호 감쌈을 벗긴 뒤에도 안쪽 LEFT JOIN의 왼쪽 기준
            // 판정이 그대로 적용돼야 한다. 왼쪽이 VALUES(상수 원천)이면 괄호가
            // 있든 없든 보장하지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = B.x FROM ((VALUES(1)) v(c) LEFT JOIN dbo.B B WITH(NOLOCK) ON v.c = B.id)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_CatalogViewSource_IsCapturedDespitePredictingSilence()
        {
            // ★ §12-6 어긋난 칸(R5 - 되돌림 아님, 코드를 비틀지 않는다). 설계서
            // §12-6은 `FROM sys.objects`를 "TVF·카탈로그 뷰"로 묶어 침묵을
            // 예측했지만, ScriptDom은 카탈로그 뷰를 일반 명명 테이블과 똑같이
            // NamedTableReference로 파싱한다(3 회차 보고서 PARSE SHAPES가 실측한
            // 값 - 프로브로 확인, 짐작하지 않았다). §12-3의 "이름 있는 테이블 → 예"는
            // 노드 종류만으로 판정하므로 스키마가 `sys`인지 가리지 않는다 - 그래서
            // 실제 동작은 예측과 달리 **담긴다**. 코드를 비틀어 스키마 이름으로
            // 특례를 만들지 않고, 이 시험이 실측값을 그대로 잠근다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = o.name FROM sys.objects o
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.Equal("o.name", fact.Expression);
        }

        [Fact]
        public void Extract_UnionAllWithOneConstantBranchInDerivedTable_ShouldNotBeCollected()
        {
            // §12-6 침묵 표 - `FROM (SELECT x FROM T UNION ALL SELECT 1) D`. 파생
            // 테이블 안의 UNION/UNION ALL은 모든 갈래가 보장할 때만 참인데, 둘째
            // 갈래(`SELECT 1`)는 FROM이 없어 보장하지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = D.x FROM (SELECT x FROM dbo.T UNION ALL SELECT 1) D
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_UnionAllWithBothNamedTableBranchesInDerivedTable_IsCaptured()
        {
            // 위 시험의 대비 - 두 갈래 다 이름 있는 테이블에서 오면 둘 다 보장하므로
            // AND가 참이 되어 담긴다. UNION 재귀가 "모든 갈래"를 실제로 요구하는지
            // (한쪽만 보장해도 통과시키는 뮤턴트라면 이 대비 없이는 못 잡는다)
            // 이 둘의 쌍으로 확인한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = D.x FROM (SELECT x FROM dbo.T1 UNION ALL SELECT x FROM dbo.T2) D
END";

            var fact = Assert.Single(NonAggregateAssignmentExtractor.Extract(ddl));
            Assert.Equal("D.x", fact.Expression);
        }

        [Fact]
        public void Extract_SiblingJsonArrayAggInSameSelect_ShouldSilenceTheNonAggregateSide()
        {
            // §12-5 - AggregateNames에 JSON_ARRAYAGG를 더한 근거. 목록에 없으면
            // 형제 판정(ProjectionHasAggregate)이 이 형제를 못 보고 `@a = 1`을
            // 그대로 담아 버린다 - HAVING·GROUP BY와 같은 함정(원본이 비어도
            // JSON_ARRAYAGG는 GROUP BY 없이 1행, 빈 배열을 돌려준다).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @a INT, @b NVARCHAR(MAX)
    SELECT @a = 1, @b = JSON_ARRAYAGG(x) FROM dbo.T WITH(NOLOCK)
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_AggregateInsideDerivedTable_JsonObjectAgg_ShouldNotBeCollected()
        {
            // §12-5 - 같은 목록 누락이 FROM(파생 테이블) 경로에서도 샌다. 목록에
            // 없으면 파생 테이블 안쪽 질의의 투영 집계 판정이 이 호출을 못 보고
            // 원본이 비어도 1행(빈 객체)이 돌아온다는 사실을 놓친다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v NVARCHAR(MAX)
    SELECT @v = A.m FROM (SELECT JSON_OBJECTAGG(k, val) AS m FROM dbo.U) A
END";

            Assert.Empty(NonAggregateAssignmentExtractor.Extract(ddl));
        }

        [SkippableFact]
        public void Extract_OverTheCorpus_ShouldCollectExactlyTheseRows()
        {
            // 클래스 주석이 코퍼스 수치 위에 서 있는데 단위 테스트는 규칙이 흘러도 그대로
            // 통과한다. 형제 LoopVariableResetExtractorTests가 3/11을 못박은 것과 같은
            // 방식으로 코퍼스에 직접 못박는다.
            //
            // [2026-09-06] 코퍼스 뿌리가 output/Objects 로컬 24개에서 output/External의
            // 외부 DB까지 넓어졌다 - 행 수가 8에서 36(로컬 8 · SETTLE_CARD_DB 28)으로,
            // NULL 확정이 7에서 23으로, 대입 분모(setVariables)가 26에서 68로 늘었다.
            // 늘어난 28행은 전부 원본 DDL을 열어 (1) SELECT @변수 = 컬럼 FROM 문인지
            // (2) NULL확정이면 그 앞에 같은 변수 대입이 정말 없는지 확인한 뒤 적었다.
            // 코퍼스가 없으면 건너뜀으로 표시된다(CorpusSkip.Reason).
            //
            // [2026-09-06 감쌈 벗기기] 우변 가드가 "맨 컬럼"에서 "감쌈을 한 겹 벗긴 뒤
            // 전부 컬럼인 분기식"으로 넓어져 36행에서 43행으로 늘었다. 늘어난 7행은
            // 전부 원본을 열어 (1) 대상 칸이 원문 그대로인지 (2) 그 SELECT에 FROM이
            // 있고 집계가 없는지 (3) NULL확정이면 그 앞에 같은 변수 대입이 정말 없는지
            // 확인했다 - UF_GET_COMM4CLIENT:40·54·70(IIF, 신용카드 수수료율 분기),
            // UF_GET_COMM4CLIENT4PARTIALCANCEL:43·58(IIF, 축 A 🔴 #1),
            // UF_Get_ExtraCardCommissionAmt:40·41(ISNULL(컬럼, 리터럴)).
            //
            // 힌트로 지목됐던 감쌈 자리 다섯은 **당시** 실측으로 기각됐다 - 분기 결과가
            // 리터럴·산술식이면 침묵하던 1 회차 규칙 때문이다.
            // - UF_GET_COMM4PG4INTEREST:42·UF_GET_COMM4CLIENT4INTEREST:35 - CASE의
            //   ELSE가 리터럴 0이고, 그마저 최상위가 `CASE ... END / 100.0`(이항식)이라
            //   감쌈 벗기기 대상 자체가 아니다.
            // - UF_GET_EXTRACOMM4CLIENT:41·53·66·UF_Get_ExtraCardCommissionAmt:42·47 -
            //   ISNULL(CASE ...)의 안쪽 CASE가 두 컬럼의 차(`A - B`)를 THEN에 두거나
            //   ELSE가 리터럴 0이라 "전부 컬럼"이 아니다.
            //
            // 뒤의 두 단언이 그 회차가 더한 가드 둘의 분모다. CTE 문장이 0건이고 복합
            // 대입 SelectSetVariable이 0건이라는 것이 곧 그 회차의 두 가드가 위 43행을
            // 한 행도 줄이지 않았다는 증거다 - 분모가 0이 아닌 날이 오면 위 목록이
            // 줄어드는지 함께 드러난다.
            //
            // 클래스 주석의 "FROM 가드 도입 전후 행이 같음"은 여기서 못박히지 않는다.
            // 그 가드의 분모(FROM 절이 집계를 품은 문장 수)는 세지 않으므로 이 테스트가
            // 붙드는 것은 도입 **후**의 행뿐이고, "전후 동일"은 단언 밖의 일회 실측으로
            // 남는다. 넓게 말하지 않으려고 적어 둔다.
            //
            // [2026-09-07 2 회차 - 분기 결과를 컬럼·리터럴·산술식까지 넓힌다] 위 두 불릿
            // (그 위 "기각" 문단)이 세는 일곱 자리 — 첫 불릿의 둘(UF_GET_COMM4PG4INTEREST:42·
            // UF_GET_COMM4CLIENT4INTEREST:35, "감쌈 벗기기 대상 자체가 아니다"로 기각됐던
            // 자리)과 둘째 불릿의 다섯(UF_GET_EXTRACOMM4CLIENT:41·53·66·
            // UF_Get_ExtraCardCommissionAmt:42·47, "전부 컬럼이 아니다"로 기각됐던 자리) —
            // 은 **전량** 이번에 담긴다(리뷰 라운드 1이 재확인 - "다섯 중 넷"은 두 읽기
            // 어느 쪽으로도 틀렸다). 여기에 설계서 §9-6①의 "이번 검토가 처음 찾은 여섯"
            // (UF_Get_CLComm4MobileCo:25·UF_GET_PGCommOption:21·UF_GET_COLLECTYMD:31·48·
            // UIF_SettleYMD:39·56) 중 **하나**(UF_GET_PGCommOption:21, `CASE … THEN 컬럼 …
            // ELSE 0 END`)가 더 담기고 나머지 다섯은 여전히 침묵한다. 마지막 한 자리
            // UF_GET_SETTLE_EXCHANGERATE:26(컬럼 산술 + 곱셈으로 묶인 **형제** `IIF` 둘
            // - `IIF(A.ModifyType=1, 1, -1) * IIF(A.ModifyCommType=0, …, A.ModifyCommAmt)`,
            // 중첩이 아니라 나란히 곱해진 것이다. 원본 DDL 확인, 리뷰 라운드 1 FINDING M1)
            // 은 설계서 어느 목록에도 이름이 없던 자리다 - 이번 최종 검토가 처음 찾았다.
            // 합 7 + 1 + 1 = 9, 43행에서 52행(+9)으로 늘었다(설계서
            // §10-3 사전 예측 그대로 어긋남 0). 늘어난 행 전량을 원본 DDL로 대조해 대상
            // 칸이 원문 그대로이고 거짓 행이 없음을 확인했다(설계서 §10-4 조건 ⑤).
            //
            // 안 담기는 다섯은 여전히 침묵한다 - 분기 결과 안에 또 분기식(중첩 CASE)이
            // 오거나(UF_GET_COLLECTYMD:31·48, UIF_SettleYMD:39·56) ELSE가 하위 질의라서
            // (UF_Get_CLComm4MobileCo:25)다. 이 다섯은 통째 완화를 실측했을 때 원본 줄
            // 주석이 대상 칸 안으로 섞여 들어오던 바로 그 자리였다(설계서 §10-1) - 분기식은
            // 한 겹만 허용하는 이번 술어가 그 자리를 애초에 배제한다.
            var objects = CorpusObjects().ToList();
            Skip.If(objects.Count == 0, CorpusSkip.Reason);

            var collected = new List<string>();
            var cteNodes = 0;
            var setVariables = 0;
            var compoundSetVariables = 0;
            var groupByOnSetVariableQueries = 0;

            foreach (var (name, ddl) in objects)
            {
                foreach (var fact in NonAggregateAssignmentExtractor.Extract(ddl))
                {
                    var branch = fact.Sentence.Contains("NULL이 그대로 남습니다") ? "NULL확정" : "중립";
                    collected.Add($"{name}:{fact.Line} {fact.Variable} = {fact.Expression} [{branch}]");
                }

                var (cte, setVariable, compound, groupBy) = CountGuardInputs(ddl);
                cteNodes += cte;
                setVariables += setVariable;
                compoundSetVariables += compound;
                groupByOnSetVariableQueries += groupBy;
            }

            Assert.Equal(
                new[]
                {
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:40 @v_intCommissionRate = IIF(@pi_intFreeInterestFlag IN(0,2), A.CommissionRate, A.FreeInterestInstCommRate) [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:41 @v_intCommissionRate4Foreign = B.CommissionRate4Foreign [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:42 @v_intCommissionRate4UPOP = B.CommissionRate4UPOP [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:43 @v_intFreeInterestInstUseFlag = A.FreeInterestInstUseFlag [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:44 @v_intFreeInterestInstCommCode = A.FreeInterestInstCommCode [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:54 @v_intCommissionRate = IIF(@pi_intFreeInterestFlag IN (0,2), A.CommissionRate, A.FreeInterestInstCommRate) [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:55 @v_intCommissionRate4Foreign = B.CommissionRate4Foreign [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:56 @v_intCommissionRate4UPOP = B.CommissionRate4UPOP [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:57 @v_intFreeInterestInstUseFlag = A.FreeInterestInstUseFlag [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:58 @v_intFreeInterestInstCommCode = A.FreeInterestInstCommCode [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:70 @v_intCommissionRate = IIF(@pi_intFreeInterestFlag IN (0,2), A.CommissionRate, A.FreeInterestInstCommRate) [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:71 @v_intCommissionRate4Foreign = B.CommissionRate4Foreign [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:72 @v_intCommissionRate4UPOP = B.CommissionRate4UPOP [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:73 @v_intFreeInterestInstUseFlag = A.FreeInterestInstUseFlag [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT.Function:74 @v_intFreeInterestInstCommCode = A.FreeInterestInstCommCode [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT4INTEREST.Function:35 @v_intFreeInterestRate = CASE @pi_strAllotPeriod WHEN '02' THEN CommissionRate2 WHEN '03' THEN CommissionRate3 WHEN '04' THEN CommissionRate4 WHEN '05' THEN CommissionRate5 WHEN '06' THEN CommissionRate6 WHEN '07' THEN CommissionRate7 WHEN '08' THEN CommissionRate8 WHEN '09' THEN CommissionRate9 WHEN '10' THEN CommissionRate10 WHEN '11' THEN CommissionRate11 WHEN '12' THEN CommissionRate12 WHEN '13' THEN CommissionRate13 WHEN '14' THEN CommissionRate14 WHEN '15' THEN CommissionRate15 WHEN '16' THEN CommissionRate16 WHEN '17' THEN CommissionRate17 WHEN '18' THEN CommissionRate18 WHEN '19' THEN CommissionRate19 WHEN '20' THEN CommissionRate20 WHEN '21' THEN CommissionRate21 WHEN '22' THEN CommissionRate22 WHEN '23' THEN CommissionRate23 WHEN '24' THEN CommissionRate24 WHEN '36' THEN CommissionRate36 ELSE 0 END / 100.0 [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function:43 @v_intCommissionRate = IIF(@pi_intFreeInterestFlag IN (0,2), A.CommissionRate, A.FreeInterestInstCommRate) [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function:44 @v_intCommissionRate4Foreign = B.CommissionRate4Foreign [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function:45 @v_intCommissionRate4UPOP = B.CommissionRate4UPOP [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function:46 @v_intFreeInterestInstUseFlag = A.FreeInterestInstUseFlag [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function:47 @v_intFreeInterestInstCommCode = A.FreeInterestInstCommCode [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function:58 @v_intCommissionRate = IIF(@pi_intFreeInterestFlag IN(0,2), A.CommissionRate, A.FreeInterestInstCommRate) [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function:59 @v_intCommissionRate4Foreign = B.CommissionRate4Foreign [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function:60 @v_intCommissionRate4UPOP = B.CommissionRate4UPOP [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function:61 @v_intFreeInterestInstUseFlag = A.FreeInterestInstUseFlag [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4CLIENT4PARTIALCANCEL.Function:62 @v_intFreeInterestInstCommCode = A.FreeInterestInstCommCode [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4PG.Function:40 @v_intCommissionRate = CommissionRate [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4PG.Function:41 @v_intCommissionRate4Check = CommissionRate4Check [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4PG.Function:42 @v_intCommissionRate4Foreign = CommissionRate4Foreign [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4PG.Function:43 @v_intCommissionRate4UPOP = CommissionRate4UPOP [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4PG.Function:44 @v_intFreeInterestInstUseFlag = FreeInterestInstUseFlag [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4PG.Function:45 @v_intFreeInterestInstCommCode = FreeInterestInstCommCode [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4PG4INTEREST.Function:37 @v_intFreeInterestInstCommCode = FreeInterestInstCommCode [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_COMM4PG4INTEREST.Function:42 @v_intFreeInterestRate = CASE @pi_strAllotPeriod WHEN '02' THEN CommissionRate2 WHEN '03' THEN CommissionRate3 WHEN '04' THEN CommissionRate4 WHEN '05' THEN CommissionRate5 WHEN '06' THEN CommissionRate6 WHEN '07' THEN CommissionRate7 WHEN '08' THEN CommissionRate8 WHEN '09' THEN CommissionRate9 WHEN '10' THEN CommissionRate10 WHEN '11' THEN CommissionRate11 WHEN '12' THEN CommissionRate12 WHEN '13' THEN CommissionRate13 WHEN '14' THEN CommissionRate14 WHEN '15' THEN CommissionRate15 WHEN '16' THEN CommissionRate16 WHEN '17' THEN CommissionRate17 WHEN '18' THEN CommissionRate18 WHEN '19' THEN CommissionRate19 WHEN '20' THEN CommissionRate20 WHEN '21' THEN CommissionRate21 WHEN '22' THEN CommissionRate22 WHEN '23' THEN CommissionRate23 WHEN '24' THEN CommissionRate24 WHEN '36' THEN CommissionRate36 ELSE 0 END / 100.0 [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_EXTRACOMM4CLIENT.Function:31 @v_intExtraType = ExtraType [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_EXTRACOMM4CLIENT.Function:41 @v_intCommissionRate = ISNULL(CASE WHEN @pi_intCompanySalesType = 0 THEN CommissionRate4 - CommissionRate0 WHEN @pi_intCompanySalesType = 1 THEN CommissionRate4 - CommissionRate1 WHEN @pi_intCompanySalesType = 2 THEN CommissionRate4 - CommissionRate2 WHEN @pi_intCompanySalesType = 3 THEN CommissionRate4 - CommissionRate3 ELSE 0 END, 0) [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_GET_EXTRACOMM4CLIENT.Function:53 @v_intCommissionRate = ISNULL(CASE WHEN @pi_intCompanySalesType = 0 THEN CommissionRate4 - CommissionRate0 WHEN @pi_intCompanySalesType = 1 THEN CommissionRate4 - CommissionRate1 WHEN @pi_intCompanySalesType = 2 THEN CommissionRate4 - CommissionRate2 WHEN @pi_intCompanySalesType = 3 THEN CommissionRate4 - CommissionRate3 ELSE 0 END, 0) [중립]",
                    "SETTLE_CARD_DB/dbo.UF_GET_EXTRACOMM4CLIENT.Function:66 @v_intCommissionRate = ISNULL(CASE WHEN @pi_intCompanySalesType = 0 THEN A.CommissionRate - B.CommRate0 WHEN @pi_intCompanySalesType = 1 THEN A.CommissionRate - B.CommRate1 WHEN @pi_intCompanySalesType = 2 THEN A.CommissionRate - B.CommRate2 WHEN @pi_intCompanySalesType = 3 THEN A.CommissionRate - B.CommRate3 ELSE 0 END, 0) [중립]",
                    "SETTLE_CARD_DB/dbo.UF_Get_ExtraCardCommissionAmt.Function:40 @v_intCommissionRate = ISNULL(CommissionRate4, 0) [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_Get_ExtraCardCommissionAmt.Function:41 @v_intCommissionRate4Check = ISNULL(CheckCommissionRate4, 0) [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_Get_ExtraCardCommissionAmt.Function:42 @v_intExtraCommissionRate = ISNULL(CASE WHEN @pi_intCompanySalesType = 0 THEN CommissionRate0 WHEN @pi_intCompanySalesType = 1 THEN CommissionRate1 WHEN @pi_intCompanySalesType = 2 THEN CommissionRate2 WHEN @pi_intCompanySalesType = 3 THEN CommissionRate3 ELSE 0 END, 0) [NULL확정]",
                    "SETTLE_CARD_DB/dbo.UF_Get_ExtraCardCommissionAmt.Function:47 @v_intExtraCommissionRate4Check = ISNULL(CASE WHEN @pi_intCompanySalesType = 0 THEN CheckCommissionRate0 WHEN @pi_intCompanySalesType = 1 THEN CheckCommissionRate1 WHEN @pi_intCompanySalesType = 2 THEN CheckCommissionRate2 WHEN @pi_intCompanySalesType = 3 THEN CheckCommissionRate3 ELSE 0 END, 0) [NULL확정]",
                    "dbo.UF_GET_CLIENTSECTIONRATE.Function:14 @po_intAmt = SECTIONAMT [NULL확정]",
                    "dbo.UF_GET_COLLECTYMD.Function:29 @v_intCollectStandard = CollectStandard [NULL확정]",
                    "dbo.UF_GET_COLLECTYMD.Function:30 @v_intCollectType = CollectType [NULL확정]",
                    "dbo.UF_GET_COLLECTYMD.Function:47 @v_intHolidayPayFlag = HolidayPayFlag [NULL확정]",
                    "dbo.UF_GET_PGCommOption.Function:21 @po_intOptionValue = CASE WHEN @pi_intOptionFlag = 1 THEN CommMethod WHEN @pi_intOptionFlag = 2 THEN CommStandard WHEN @pi_intOptionFlag = 3 THEN CommRoundFlag WHEN @pi_intOptionFlag = 4 THEN CommSumRoundFlag WHEN @pi_intOptionFlag = 5 THEN VatRoundFlag ELSE 0 END [중립]",
                    "dbo.UF_GET_SETTLE_EXCHANGERATE.Function:26 @po_intExchangeRate = (B.TTSellRate/C.BasicSettleRate) + IIF(A.ModifyType=1, 1, -1) * IIF(A.ModifyCommType=0,(B.TTSellRate/C.BasicSettleRate) * (A.ModifyCommRate/100.0), A.ModifyCommAmt) [NULL확정]",
                    "dbo.UIF_SettleYMD.Function:37 @v_intSettleStandard = SettleStandard [NULL확정]",
                    "dbo.UIF_SettleYMD.Function:38 @v_intSettleType = SettleType [NULL확정]",
                    "dbo.UIF_SettleYMD.Function:55 @v_intSettleDayFlag = SettleDayFlag [NULL확정]",
                    "dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure:72 @v_intID = ID [중립]"
                },
                collected.OrderBy(x => x, StringComparer.Ordinal).ToArray());

            Assert.Equal(31, collected.Count(x => x.EndsWith("[NULL확정]", StringComparison.Ordinal)));
            Assert.Equal(21, collected.Count(x => x.EndsWith("[중립]", StringComparison.Ordinal)));

            Assert.Equal(0, cteNodes);
            Assert.Equal(68, setVariables);
            Assert.Equal(0, compoundSetVariables);
            // ★ 둘째 재검토의 분모 - SelectSetVariable을 가진 QuerySpecification 중
            // GroupByClause가 non-null인 것이 코퍼스에 0건이다. 이번 GROUP BY 가드가
            // 대장 52행을 한 행도 안 줄였다는 근거가 이 값이다.
            Assert.Equal(0, groupByOnSetVariableQueries);
        }

        /// <summary>
        /// 저장소 뿌리. "output/Objects를 가진 첫 조상"으로 찾으면 안 된다 - 다른 테스트가
        /// 실행 중에 bin/Debug/net10.0/output/Objects에 가짜 객체를 만들어 두어, 그쪽이
        /// 먼저 걸리면 남의 테스트 찌꺼기를 코퍼스로 착각한다. 그래서 src/ReSet.Core를
        /// 가진 조상을 찾는다.
        /// </summary>
        private static string? RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "ReSet.Core")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            return null;
        }

        /// <summary>
        /// 코퍼스 객체 전량 - 로컬 <c>output/Objects</c> **와** 외부 DB
        /// <c>output/External/[DB]/Objects</c> 둘 다.
        ///
        /// [왜 External을 함께 훑는가 - 2026-09-06] 이 대장은 오래도록 로컬 24개만 훑었다.
        /// 그런데 명세서가 만들어지는 객체는 그 24개가 아니라 **참조 폐포**이고, 폐포에는
        /// 외부 DB 함수 7개가 들어 있다(reset-consistency-audit SKILL.md 1-1절). 실제로
        /// 축 A 🔴 하나의 대상 <c>UF_GET_COMM4CLIENT4PARTIALCANCEL</c>이 그 7개 안에
        /// 있어서, 로컬만 훑는 자로는 그 결함이 이 대장에 **한 번도 나타나지 않았다.**
        /// 자가 관할을 좁게 잡으면 결함이 아니라 자가 침묵한다.
        ///
        /// 이름은 외부 DB만 <c>[DB]/</c>로 접두한다 - 로컬 이름 24개가 그대로 남아야
        /// 이 회차의 증분이 diff에서 바로 읽힌다.
        /// </summary>
        private static IEnumerable<(string Name, string Ddl)> CorpusObjects()
        {
            var root = RepoRoot();
            if (root == null) yield break;

            var roots = new List<(string Prefix, string Dir)>();

            var local = Path.Combine(root, "output", "Objects");
            if (Directory.Exists(local)) roots.Add((string.Empty, local));

            var external = Path.Combine(root, "output", "External");
            if (Directory.Exists(external))
            {
                foreach (var db in Directory.GetDirectories(external).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var objects = Path.Combine(db, "Objects");
                    if (Directory.Exists(objects)) roots.Add((Path.GetFileName(db) + "/", objects));
                }
            }

            foreach (var (prefix, dir) in roots)
            {
                foreach (var objectDir in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var path = Path.Combine(objectDir, "raw", "object_definition.sql");
                    if (!File.Exists(path)) continue;
                    yield return (prefix + Path.GetFileName(objectDir), File.ReadAllText(path));
                }
            }
        }

        /// <summary>
        /// 두 가드의 분모를 세는 테스트 전용 계수기. 추출기의 거르기를 거치지 않은 날것이라야
        /// "코퍼스에 이 모양이 0건"을 못박을 수 있다.
        /// grep이 아니라 AST 노드를 센다 - 주석의 문자열 모양(`WITH ... AS (`)은 힌트
        /// `WITH(NOLOCK)`과 구분되지 않는다.
        /// </summary>
        private static (int Cte, int SetVariable, int CompoundSetVariable, int GroupByOnSetVariableQuery) CountGuardInputs(string ddl)
        {
            var parser = new TSql160Parser(true);
            using var reader = new StringReader(ddl);
            var fragment = parser.Parse(reader, out var errors);
            if (fragment == null || errors.Count > 0) return (0, 0, 0, 0);

            var counter = new GuardInputCounter();
            fragment.Accept(counter);
            return (counter.Cte, counter.SetVariable, counter.CompoundSetVariable, counter.GroupByOnSetVariableQuery);
        }

        private sealed class GuardInputCounter : TSqlFragmentVisitor
        {
            public int Cte { get; private set; }

            public int SetVariable { get; private set; }

            public int CompoundSetVariable { get; private set; }

            /// <summary>
            /// ★ 둘째 재검토의 분모 - <see cref="SelectSetVariable"/>을 가진
            /// <see cref="QuerySpecification"/> 중 <see cref="QuerySpecification.GroupByClause"/>가
            /// non-null인 것의 수. 이번 GROUP BY 가드가 대장 52행을 실제로 안 줄였다는
            /// 근거가 이 값이다(추출기의 거르기를 거치지 않은 날것을 센다).
            /// </summary>
            public int GroupByOnSetVariableQuery { get; private set; }

            public override void Visit(CommonTableExpression node) => Cte++;

            public override void Visit(SelectSetVariable node)
            {
                SetVariable++;
                if (node.AssignmentKind != AssignmentKind.Equals) CompoundSetVariable++;
            }

            public override void Visit(QuerySpecification node)
            {
                if (node.GroupByClause != null && node.SelectElements.Any(e => e is SelectSetVariable))
                {
                    GroupByOnSetVariableQuery++;
                }
            }
        }
    }
}
