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

            foreach (var (name, ddl) in objects)
            {
                foreach (var fact in NonAggregateAssignmentExtractor.Extract(ddl))
                {
                    var branch = fact.Sentence.Contains("NULL이 그대로 남습니다") ? "NULL확정" : "중립";
                    collected.Add($"{name}:{fact.Line} {fact.Variable} = {fact.Expression} [{branch}]");
                }

                var (cte, setVariable, compound) = CountGuardInputs(ddl);
                cteNodes += cte;
                setVariables += setVariable;
                compoundSetVariables += compound;
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
        private static (int Cte, int SetVariable, int CompoundSetVariable) CountGuardInputs(string ddl)
        {
            var parser = new TSql160Parser(true);
            using var reader = new StringReader(ddl);
            var fragment = parser.Parse(reader, out var errors);
            if (fragment == null || errors.Count > 0) return (0, 0, 0);

            var counter = new GuardInputCounter();
            fragment.Accept(counter);
            return (counter.Cte, counter.SetVariable, counter.CompoundSetVariable);
        }

        private sealed class GuardInputCounter : TSqlFragmentVisitor
        {
            public int Cte { get; private set; }

            public int SetVariable { get; private set; }

            public int CompoundSetVariable { get; private set; }

            public override void Visit(CommonTableExpression node) => Cte++;

            public override void Visit(SelectSetVariable node)
            {
                SetVariable++;
                if (node.AssignmentKind != AssignmentKind.Equals) CompoundSetVariable++;
            }
        }
    }
}
