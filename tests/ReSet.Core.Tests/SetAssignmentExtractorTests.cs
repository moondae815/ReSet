using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SetAssignmentExtractorTests
    {
        [Fact]
        public void Extract_SimpleAssignment_ShouldKeepVariableAndExpressionVerbatim()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT
    SET @v = @@ERROR
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal(4, fact.Line);
            Assert.Equal("@v", fact.Variable);
            Assert.Equal("@@ERROR", fact.Expression);
        }

        [Fact]
        public void Extract_SelfReferencingIncrement_ShouldKeepWholeExpression()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @c INT
    SET @c = @c + 1
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal("@c", fact.Variable);
            Assert.Equal("@c + 1", fact.Expression);
        }

        [Fact]
        public void Extract_FunctionCallExpression_ShouldNotSummarise()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @d VARCHAR(8)
    SET @d = CONVERT(VARCHAR(8), GETDATE(), 112)
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Contains("CONVERT", fact.Expression);
            Assert.Contains("112", fact.Expression);
        }

        [Fact]
        public void Extract_SelectAssignment_ShouldNotBeCollected()
        {
            // 관할 경계다. `SELECT @v = ...`는 ScriptDom에서 SelectSetVariable이고
            // AggregateAssignmentExtractor(:104)·NonAggregateAssignmentExtractor(:75)가
            // 그 타입만 본다. 이 표가 그것까지 담으면 정본이 둘로 갈린다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT
    SELECT @v = COUNT(*) FROM dbo.T
END";

            Assert.Empty(SetAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_DeclareWithInitializer_ShouldNotBeCollected()
        {
            // DECLARE @v INT = 15는 DeclareVariableStatement다. 백로그 ④의 몫이라
            // 이 표는 담지 않는다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT = 15
END";

            Assert.Empty(SetAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_MultipleAssignments_ShouldBeOrderedByLine()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @a INT, @b INT
    SET @a = 1
    SET @b = 2
END";

            var facts = SetAssignmentExtractor.Extract(ddl);

            Assert.Equal(new[] { "@a", "@b" }, facts.Select(f => f.Variable).ToArray());
            Assert.True(facts[0].Line < facts[1].Line);
        }

        [Fact]
        public void Extract_StringLiteralWithInternalDoubleSpace_ShouldPreserveLiteralVerbatim()
        {
            // Fix Round 1 - Important 1. 리터럴 내부 공백은 "표 셀이 깨지는 것을 막는"
            // 개행 접기와 무관한 값의 일부다. 토큰 단위가 아니라 문자열 전체를 이어 붙인
            // 뒤 공백을 접으면 리터럴 안의 두 칸까지 한 칸으로 뭉개져 원문이 아닌 값을
            // 대입식으로 보고하게 된다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v VARCHAR(10)
    SET @v = 'a  b'
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal("'a  b'", fact.Expression);
        }

        [Fact]
        public void Extract_AlignmentWhitespaceBetweenTokens_ShouldBePreservedVerbatim()
        {
            // Fix Round 2 정정. 라운드 1은 "정렬 공백도 한 칸으로 접는다"고 주장했지만
            // 그 근거(CollapseWhitespace가 표 셀 붕괴를 막는다)가 틀렸다 -
            // MarkdownTableCellCodec.Escape를 직접 읽어보면 렌더는 개행만 공백으로
            // 바꾸고 스페이스·탭은 손대지 않는다. 추출기가 렌더보다 더 접으면 그만큼
            // 값 충실도를 공짜로 버리는 것이다. 그래서 개행이 없는 정렬 공백은 이제
            // 그대로 보존한다 - "추출기의 정규화는 렌더의 정규화와 정확히 같아야 한다."
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @a INT
    SET @a = @a     +     1
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal("@a     +     1", fact.Expression);
        }

        [Fact]
        public void Extract_MultilineExpression_ShouldCollapseNewlineToSingleSpace()
        {
            // 개행이 든 식이 표 셀을 깨뜨리지 않아야 한다 - MarkdownTableCellCodec.Escape가
            // 개행을 공백 하나로 바꾸는 것과 문자 단위로 같은 치환이어야 모델이 베낀
            // 값(렌더 결과)과 검증기가 대조하는 원본 fact가 어긋나지 않는다. 계속
            // 줄이어지지 않도록 연속 라인 첫 칸부터 시작해 정렬 공백이 섞이지 않게 한다.
            const string ddl = "CREATE PROCEDURE dbo.P AS\nBEGIN\n    DECLARE @a INT\n    SET @a = @a +\n1\nEND";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal("@a + 1", fact.Expression);
        }

        [Fact]
        public void Extract_LiteralWithEmbeddedNewline_ShouldCollapseToSingleSpace_LikeEscapeDoes()
        {
            // Fix Round 2 - 새 Important. 재리뷰어가 실물 프로브로 확인한 결함: 리터럴
            // 안의 진짜 개행이 그대로 보존되고 있었다. MarkdownTableCellCodec.Escape는
            // 개행을 공백 하나로 바꾸므로(왕복 가능), 추출기도 같은 치환을 해야
            // "모델이 볼 수 있는 값(렌더된 값)"과 fact가 일치한다. 리터럴 안이라고
            // 예외를 두지 않는다 - Escape는 셀 전체 문자열에 적용되고 리터럴 여부를
            // 구분하지 않는다.
            const string ddl = "CREATE PROCEDURE dbo.P AS\nBEGIN\n    DECLARE @v VARCHAR(10)\n    SET @v = 'a\nb'\nEND";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal("'a b'", fact.Expression);
        }

        [Fact]
        public void Extract_LiteralWithConsecutiveNewlines_ShouldMatchEscapeBehaviorExactly()
        {
            // Escape는 "\n"을 하나씩 개별 치환하므로 연속 개행 둘은 공백 둘이 된다
            // (하나로 더 접지 않는다). 추출기가 \s+ 정규식으로 접으면 공백 하나로
            // 뭉개져 Escape의 실제 산출과 어긋난다 - 그래서 정규식이 아니라 Escape와
            // 문자 단위로 동일한 치환(MarkdownTableCellCodec.CollapseNewlines)을 쓴다.
            const string ddl = "CREATE PROCEDURE dbo.P AS\nBEGIN\n    DECLARE @v VARCHAR(10)\n    SET @v = 'a\n\nb'\nEND";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal("'a  b'", fact.Expression);
            Assert.Equal(MarkdownTableCellCodec.Escape("'a\n\nb'"), fact.Expression);
        }

        [Fact]
        public void Extract_LiteralWithInternalTab_ShouldPreserveTabVerbatim()
        {
            // Escape는 탭을 손대지 않는다. 그러니 추출기도 접지 않는다 - 두 칸 공백
            // 보존(라운드 1)과 같은 원칙을 탭에도 적용한다.
            const string ddl = "CREATE PROCEDURE dbo.P AS\nBEGIN\n    DECLARE @v VARCHAR(10)\n    SET @v = 'a\tb'\nEND";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal("'a\tb'", fact.Expression);
        }

        [Fact]
        public void Extract_ExpressionWithEmbeddedNewline_ShouldContainNoRawNewlineAfterExtraction()
        {
            // "모델이 베낄 수 있는 값만 요구한다"의 기계적 증거. 개행이 fact에 그대로
            // 남으면 렌더된 셀(개행이 공백으로 바뀐 모습)과 fact가 영원히 어긋나
            // MechanicalValidator 대조를 만족할 수 없는 요구가 된다. 접은 값에
            // 개행이 없으면 Escape를 다시 먹여도 자기 자신과 같다(멱등) - 즉 렌더된
            // 값 그대로가 fact와 일치할 수 있다는 뜻이다.
            const string ddl = "CREATE PROCEDURE dbo.P AS\nBEGIN\n    DECLARE @v VARCHAR(10)\n    SET @v = 'a\nb'\nEND";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.DoesNotContain('\n', fact.Expression);
            Assert.DoesNotContain('\r', fact.Expression);
            Assert.Equal(MarkdownTableCellCodec.Escape(fact.Expression), fact.Expression);
        }

        [Fact]
        public void Extract_SetInsideWhileBody_ShouldStillBeCollected_BecauseThisTableIsExhaustive()
        {
            // Fix Round 1 - Important 2. 클래스 docstring이 [LoopVariableResetExtractor와의
            // 관계] 문단에서 길게 정당화하는 바로 그 중복이다 - WHILE 본문 최상위 SET도
            // 이 표는 담는다(실행 의미 표와 층이 다르다: "어떤 대입이 있나" vs
            // "매 반복 다시 설정된다"). LoopVariableResetExtractorTests의
            // UP_UTIL_SETTLE_PROC_ETC:69 실측 모양을 그대로 쓴다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intID INT = 0
    WHILE (@@FETCH_STATUS = 0) BEGIN
        SET @v_intID = 0
        SELECT @v_intID = ID FROM dbo.TA WITH(NOLOCK)
    END
END";

            var fact = Assert.Single(SetAssignmentExtractor.Extract(ddl));

            Assert.Equal(7, fact.Line);
            Assert.Equal("@v_intID", fact.Variable);
            Assert.Equal("0", fact.Expression);
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(SetAssignmentExtractor.Extract("CREATE PROCEDURE ((("));
        }

        [Fact]
        public void Extract_NullOrBlank_ShouldReturnEmpty()
        {
            Assert.Empty(SetAssignmentExtractor.Extract(null));
            Assert.Empty(SetAssignmentExtractor.Extract("   "));
        }

        [Fact]
        public void TableHeading_ShouldCarryTheMachineConfirmedSuffix()
        {
            Assert.EndsWith(
                MachineConfirmedTables.HeadingSuffix,
                SetAssignmentExtractor.TableHeading);
        }
    }
}
