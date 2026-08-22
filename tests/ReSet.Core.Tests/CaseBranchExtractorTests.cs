using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class CaseBranchExtractorTests
    {
        [Fact]
        public void Extract_SearchedCase_ShouldKeepOperatorsVerbatim()
        {
            // UIF_SettleYMD 실측: 명세서가 엄격 초과(>)를 "비교해"로 뭉개 경계에서
            // 오프셋이 일주일 어긋났다. 조건은 원문 그대로여야 한다.
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    DECLARE @v INT
    SET @v = CASE WHEN DATEPART(DW, GETDATE()) > 3 THEN 7 ELSE 0 END
    RETURN @v
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Equal("WHEN 1", facts[0].Ordinal);
            Assert.Contains(">", facts[0].Condition);
            Assert.Equal("ELSE", facts[1].Ordinal);
            Assert.Equal("(그 외 전부)", facts[1].Condition);
        }

        [Fact]
        public void Extract_SimpleCase_ShouldRecordTheInputExpressionInEachCondition()
        {
            const string ddl = @"
CREATE FUNCTION dbo.F(@p VARCHAR(2)) RETURNS INT AS
BEGIN
    RETURN CASE @p WHEN '02' THEN 2 WHEN '03' THEN 3 END
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Contains("@p", facts[0].Condition);
            Assert.Contains("'02'", facts[0].Condition);
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(CaseBranchExtractor.Extract("CREATE FUNCTION ((("));
        }

        [Fact]
        public void Extract_CaseWithoutElse_ShouldNotSynthesizeAnElseRow()
        {
            // ELSE가 없는 CASE를 있는 것처럼 다루면 거짓 행이 된다 - ELSE가 없으면
            // 그 행을 내면 안 된다.
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    RETURN CASE WHEN 1 = 1 THEN 10 END
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("WHEN 1", fact.Ordinal);
            Assert.DoesNotContain(facts, f => f.Ordinal == "ELSE");
        }

        [Fact]
        public void Extract_NestedCase_ShouldAttributeEachBranchToItsOwnCaseOnly()
        {
            // 바깥 CASE의 THEN 안에 또 CASE가 있을 때, 안쪽 분기가 바깥 것으로
            // 잘못 귀속되거나 같은 분기가 두 번 실리면 안 된다.
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    DECLARE @v INT
    SET @v = CASE WHEN 1 = 1 THEN (CASE WHEN 2 = 2 THEN 20 ELSE 21 END) ELSE 2 END
    RETURN @v
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            // 바깥 CASE: WHEN 1, ELSE. 안쪽 CASE: WHEN 1, ELSE. 합쳐서 4행 - 각
            // 분기가 정확히 한 번씩만 실린다.
            Assert.Equal(4, facts.Count);
            Assert.Equal(2, facts.Count(f => f.Ordinal == "WHEN 1"));
            Assert.Equal(2, facts.Count(f => f.Ordinal == "ELSE"));

            var outerWhen = facts.Single(f => f.Condition == "1 = 1");
            Assert.Equal("(CASE WHEN 2 = 2 THEN 20 ELSE 21 END)", outerWhen.Result);

            var innerWhen = facts.Single(f => f.Condition == "2 = 2");
            Assert.Equal("20", innerWhen.Result);

            var innerElse = facts.Single(f => f.Condition == "(그 외 전부)" && f.Result == "21");
            Assert.NotNull(innerElse);
        }

        [Fact]
        public void Extract_CaseInsideWhereClause_ShouldStillBeVisited()
        {
            // CASE는 SELECT 목록·SET뿐 아니라 WHERE 자리에도 나온다 - 방문 지점
            // 커버리지 확인.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT * FROM T1 WHERE Col1 = CASE WHEN 1 = 1 THEN 1 ELSE 2 END
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
        }

        [Fact]
        public void Extract_CaseInsideSelectList_ShouldStillBeVisited()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT CASE WHEN Col1 > 1 THEN 'a' ELSE 'b' END AS X FROM T1
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
        }

        [Fact]
        public void Extract_MultilineWhenCondition_ConditionMatchesRenderedTableCell()
        {
            // Task 13 (최종 브랜치 리뷰 Critical): UIF_SettleYMD:74-76 모양 - WHEN
            // 조건이 여러 줄에 걸친다. MechanicalValidator.CheckCaseBranches는 렌더되지
            // 않은 fact.Condition을, 렌더 파이프라인(AiService.EscapeTableCell →
            // MarkdownTableCellCodec.Escape)을 거친 뒤 SplitRow로 되돌린 셀 문자열과
            // ==로 원문 그대로 비교한다(MechanicalValidator.cs:3574-3580). Condition에
            // 개행이 남아 있으면 표 셀(한 줄에서 잘라낸 것이라 개행을 담을 수 없다)과
            // 영원히 일치하지 않는다 - 명세서를 한 글자도 안 틀리고 옮겨도 L1이 매 회차
            // CaseBranchTableMissing을 낸다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_strYMD CHAR(8)) RETURNS INT AS
BEGIN
    DECLARE @v INT
    SET @v = CASE WHEN DATEPART(DW, CONVERT(VARCHAR(6), @pi_strYMD, 112)
                        + RIGHT('0'+CONVERT(VARCHAR(2), @pi_strYMD),2) ) > 3
                   THEN 7 ELSE 0 END
    RETURN @v
END";

            var fact = CaseBranchExtractor.Extract(ddl).First(f => f.Ordinal == "WHEN 1");

            Assert.DoesNotContain("\n", fact.Condition);
            Assert.DoesNotContain("\r", fact.Condition);

            // 렌더러가 실제로 거치는 변환(개행 접기 + | 이스케이프)을 그대로 적용한 뒤,
            // L1이 쓰는 셀 분리기로 되돌려 실제 렌더된 셀과 fact.Condition이 같은지
            // 확인한다 - 둘 중 하나만 보면 이 결함이 또 숨는다.
            var renderedRow = $"| {MarkdownTableCellCodec.Escape(fact.Condition)} |";
            var renderedCell = MarkdownTableCellCodec.SplitRow(renderedRow)[1];
            Assert.Equal(fact.Condition, renderedCell);
        }

        [Fact]
        public void Extract_MultilineWhenCondition_ConditionHasNoConsecutiveWhitespace()
        {
            // Fix Round 1 (Important, 조정자 브리프 오류 인정): 개행만 공백 하나로
            // 접으면 원본의 들여쓰기가 그 자리에 남아 긴 연속 공백 런이 생긴다 - 이
            // ddl은 접으면 "112)" 뒤에 약 21칸 공백이 남는다. MarkdownTableCellCodec.
            // Escape는 그 런을 건드리지 않고 SplitRow는 셀 양끝만 Trim하므로
            // (MarkdownTableCellCodec.cs:26-34,44-70), L1은 모델에게 그 공백 개수를
            // 바이트 단위로 재현하라고 요구하는 셈이 된다 - 표는 코드 펜스 밖의
            // 평문 마크다운으로 주어져 그 런이 렌더링에서 보이지 않는다
            // (AiService.cs:1060-1063,1084-1087). DmlScopeExtractor.CollapseWhitespace
            // (2026-08-20 리뷰 Important)와 DerivedTableColumnExtractor.TextOf가 이미
            // 연속 공백류 전부를 하나로 접는 이유가 이것이다 - 같은 계약을 세 번째
            // 추출기에도 물려준다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_strYMD CHAR(8)) RETURNS INT AS
BEGIN
    DECLARE @v INT
    SET @v = CASE WHEN DATEPART(DW, CONVERT(VARCHAR(6), @pi_strYMD, 112)
                        + RIGHT('0'+CONVERT(VARCHAR(2), @pi_strYMD),2) ) > 3
                   THEN 7 ELSE 0 END
    RETURN @v
END";

            var fact = CaseBranchExtractor.Extract(ddl).First(f => f.Ordinal == "WHEN 1");

            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(fact.Condition, @"\s{2,}"),
                $"연속 공백류가 남아 있습니다: [{fact.Condition}]");
        }
    }
}
