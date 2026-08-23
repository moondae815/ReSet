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
            Assert.Equal("ID", fact.Column);
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

        [Fact]
        public void Extract_OverTheCorpus_ShouldCollectExactlyEightRowsSevenOfThemNullCertain()
        {
            // 클래스 주석이 코퍼스 수치 위에 서 있는데 단위 테스트는 규칙이 흘러도 그대로
            // 통과한다. 형제 LoopVariableResetExtractorTests가 3/11을 못박은 것과 같은
            // 방식으로 **넷**을 코퍼스에 직접 못박는다 - 비집계 대입 8행, 그중 NULL 확정
            // 7행, 그리고 이 회차가 더한 가드 둘의 분모(CTE 문장 0건 · 복합 대입 0건)다.
            // 코퍼스가 없으면 조용히 통과한다(계획서 STEP ZERO).
            //
            // 뒤의 두 단언이 그 분모다. CTE 문장이 0건이고 복합 대입 SelectSetVariable이
            // 0건이라는 것이 곧 **이 회차의** 두 가드가 위 8행을 한 행도 줄이지 않았다는
            // 증거다 - 분모가 0이 아닌 날이 오면 위 목록이 줄어드는지 함께 드러난다.
            //
            // 클래스 주석의 "FROM 가드 도입 전후 8행 동일"은 여기서 못박히지 않는다.
            // 그 가드의 분모(FROM 절이 집계를 품은 문장 수)는 세지 않으므로 이 테스트가
            // 붙드는 것은 도입 **후**의 8행뿐이고, "전후 동일"은 단언 밖의 일회 실측으로
            // 남는다. 넓게 말하지 않으려고 적어 둔다.
            var root = CorpusRoot();
            if (root == null) return;

            var collected = new List<string>();
            var cteNodes = 0;
            var setVariables = 0;
            var compoundSetVariables = 0;

            foreach (var dir in Directory.GetDirectories(root))
            {
                var path = Path.Combine(dir, "raw", "object_definition.sql");
                if (!File.Exists(path)) continue;

                var ddl = File.ReadAllText(path);
                var name = Path.GetFileName(dir);

                foreach (var fact in NonAggregateAssignmentExtractor.Extract(ddl))
                {
                    var branch = fact.Sentence.Contains("NULL이 그대로 남습니다") ? "NULL확정" : "중립";
                    collected.Add($"{name}:{fact.Line} {fact.Variable} = {fact.Column} [{branch}]");
                }

                var (cte, setVariable, compound) = CountGuardInputs(ddl);
                cteNodes += cte;
                setVariables += setVariable;
                compoundSetVariables += compound;
            }

            Assert.Equal(
                new[]
                {
                    "dbo.UF_GET_CLIENTSECTIONRATE.Function:14 @po_intAmt = SECTIONAMT [NULL확정]",
                    "dbo.UF_GET_COLLECTYMD.Function:29 @v_intCollectStandard = CollectStandard [NULL확정]",
                    "dbo.UF_GET_COLLECTYMD.Function:30 @v_intCollectType = CollectType [NULL확정]",
                    "dbo.UF_GET_COLLECTYMD.Function:47 @v_intHolidayPayFlag = HolidayPayFlag [NULL확정]",
                    "dbo.UIF_SettleYMD.Function:37 @v_intSettleStandard = SettleStandard [NULL확정]",
                    "dbo.UIF_SettleYMD.Function:38 @v_intSettleType = SettleType [NULL확정]",
                    "dbo.UIF_SettleYMD.Function:55 @v_intSettleDayFlag = SettleDayFlag [NULL확정]",
                    "dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure:72 @v_intID = ID [중립]"
                },
                collected.OrderBy(x => x, StringComparer.Ordinal).ToArray());

            Assert.Equal(7, collected.Count(x => x.EndsWith("[NULL확정]", StringComparison.Ordinal)));
            Assert.Equal(1, collected.Count(x => x.EndsWith("[중립]", StringComparison.Ordinal)));

            Assert.Equal(0, cteNodes);
            Assert.Equal(26, setVariables);
            Assert.Equal(0, compoundSetVariables);
        }

        /// <summary>
        /// 코퍼스 뿌리. 없으면 null - 그때 코퍼스 테스트는 조용히 통과한다(계획서 STEP ZERO).
        ///
        /// "output/Objects를 가진 첫 조상"으로 찾으면 안 된다 - 다른 테스트가 실행 중에
        /// bin/Debug/net10.0/output/Objects에 가짜 객체를 만들어 두어, 그쪽이 먼저 걸리면
        /// 이 테스트가 남의 테스트 찌꺼기를 코퍼스로 착각한다. 그래서 src/ReSet.Core를 가진
        /// 조상(저장소 뿌리)을 먼저 찾고 거기서만 본다(형제 LoopVariableResetExtractorTests와
        /// 같은 앵커).
        /// </summary>
        private static string? CorpusRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "ReSet.Core")))
                {
                    var candidate = Path.Combine(dir.FullName, "output", "Objects");
                    return Directory.Exists(candidate) ? candidate : null;
                }

                dir = dir.Parent;
            }

            return null;
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
