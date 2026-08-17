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
        public void Extract_HeaderComment_ShouldNotAnchorOnCopyrightOrDateTokens()
        {
            // 실측(output/**/docs/Spec.md 26건): "PayLetter"가 0건 등장하는데도, Header
            // 블록이 NonExecutable과 같은 식별자·날짜 앵커 규칙을 쓰면 저작권 고지
            // 자체가 대조 앵커가 되어 모든 문서가 이 보일러플레이트 전사를 강제로
            // 요구받는다. 헤더 재료의 역할은 A5(헤더/구현 모순) 검사이지 저작권·
            // 작성자 표기 전사가 아니다(설계 §2.1·§2.4 - "선언 키워드"는
            // HeaderContractTerms가 이미 담당한다).
            const string ddl = @"-- ProcedureName   : UP_Util_Settle_Ins
-- Copyright ⓒ 2001 by PayLetter Inc. All rights reserved.
-- Author          : kks, 2019-04-30
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT 1
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            Assert.Contains(blocks, b => b.Kind == "Header");
            Assert.All(blocks.Where(b => b.Kind == "Header"), b => Assert.Empty(b.Anchors));
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

        // 아래는 실제 코퍼스(output/Objects/*/raw/object_definition.sql)를 대상으로
        // 뽑기를 돌려 본 독립 리뷰가 찾아낸 결함의 회귀 테스트다. 문자열은 리뷰가
        // 인용한 실측 그대로다 - 지어낸 예시가 아니다.

        [Fact]
        public void Extract_TimestampInsideParentheses_ShouldNotBeMisreadAsACodeLegend()
        {
            // UP_UTIL_SETTLE_COMM_UPD.Procedure:95 실측. "17:37"은 시각이지 코드:라벨이
            // 아니다 - 콜론 뒤가 라벨(글자)이 아니라 숫자다. 이 앵커는 명세서 저자가
            // 그대로 옮겨 적을 이유가 없는 우연한 문자열이라, CodeLegend로 오분류되면
            // "17:37)"이 유일한 앵커가 되어 L1이 재생성으로 고칠 수 없는 요구를 낸다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    --비정산거래금액 발생기준(2019.06-10 17:37) : PLTID(tmoney-201906101198596)
    SELECT 1
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            Assert.DoesNotContain(blocks, b => b.Kind == "CodeLegend");
            Assert.All(blocks, b => Assert.DoesNotContain(b.Anchors, a => a.Contains("17:37")));
        }

        [Fact]
        public void Extract_ClientIdExclusionComment_ShouldSurviveWithUsableAnchors()
        {
            // UP_UTIL_SETTLE_INS_EXTRA.Procedure:312 실측 - 이 태스크 전체가 잡으려는
            // 정범 결함 형태다. 비실행 조건과 그 도입 사유(주석 처리된 날짜)가 함께
            // 한 줄에 있다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T SET C = 1
    --AND    ClientID NOT IN (SELECT ClientID FROM dbo.UF_GET_CLIENTID4TMONET())    --예외처리 제거(2021.11.29)
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            var block = Assert.Single(blocks, b => b.Kind == "NonExecutable");
            Assert.Contains("ClientID", block.Anchors);
            Assert.Contains("UF_GET_CLIENTID4TMONET", block.Anchors);
            Assert.Contains("2021.11.29", block.Anchors);
        }

        [Fact]
        public void Extract_LegendFollowedByTrailingParenthesis_ShouldNotBakeThePunctuationIntoTheAnchor()
        {
            // UP_UTIL_SETTLE_INS_EXTRA.Procedure:99 실측. 범례의 마지막 항목 뒤에 닫는
            // 괄호가 바로 붙는다 - 앵커에 ")"가 섞이면 명세서가 정확히 옮겨도 대조가
            // 실패한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    ,X.ABROADCHK --해외카드구분(1:해외카드 0:그외카드) : 영중소 우대수수료는 국내카드만 대상
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            var block = Assert.Single(blocks, b => b.Kind == "CodeLegend");
            Assert.Contains("1:해외카드", block.Anchors);
            Assert.Contains("0:그외카드", block.Anchors);
            Assert.DoesNotContain(block.Anchors, a => a.Contains(")"));
        }

        [Fact]
        public void Extract_SingleItemLegendWithoutAComma_ShouldStillBeACodeLegend()
        {
            // UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure:63 실측 (Task 6가 반올림 의미론을
            // 뽑을 때도 근거로 쓰는 바로 그 주석). 항목이 하나뿐이라고 범례가 아닌 것은
            // 아니다 - "나열은 최소 2개"라는 문턱을 세우면 이 실측을 포함해 코퍼스의
            // 진짜 범례 다수가 격하된다. 진짜 판별자는 "콜론 뒤가 라벨인가"이지
            // "항목이 몇 개인가"가 아니다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    --0:반올림, 0<>절사
    SELECT 1
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            var block = Assert.Single(blocks, b => b.Kind == "CodeLegend");
            Assert.Contains("0:반올림", block.Anchors);
        }

        [Fact]
        public void Extract_AsciiDividerLine_ShouldProduceNoBlockAtAll()
        {
            // UP_UTIL_SETTLE_COMM_UPD.Procedure:26 실측 형태의 배너 구분선. 글자·숫자가
            // 하나도 없는 순수 기호 줄은 재료에 들어가서는 안 된다 - 이런 배너가
            // 캡(MaxBlocks) 예산을 먼저 먹으면, 뒤쪽에 있는 진짜 범례·비실행 주석이
            // 밀려난다(Important 리뷰 결함).
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    --------------------------------------------------
    SELECT 1
END";

            var blocks = SourceCommentExtractor.Extract(ddl);

            Assert.Empty(blocks);
        }

        [Fact]
        public void Extract_WhenNoiseAndLowValueCommentsCrowdTheCap_ShouldStillSurfaceInformativeBlocksBeyondPosition40()
        {
            // Important 리뷰 결함의 재현. 실측(COMM_UPD)에서 76줄의 주석이 캡에 밀려
            // 사라졌고, 그중에는 진짜 범례 쌍도 있었다. 위치가 아니라 정보성(앵커를
            // 낳는 종류)으로 선정해야 한다 - 산문 주석 45줄이 앞서더라도, 41번째 줄에
            // 있는 실측 형태의 범례는 살아남아야 한다.
            var lines = new System.Collections.Generic.List<string> { "CREATE PROCEDURE dbo.P AS", "BEGIN" };
            for (var i = 0; i < 45; i++)
            {
                // 매입요청일(D)+1 : 집계 고려 - 기존 테스트가 쓰는 실측 산문 주석. 앵커가
                // 없어 정보성이 낮다.
                lines.Add("    --매입요청일(D)+1 : 집계 고려");
            }
            lines.Add("    --AND    ClientID NOT IN (SELECT ClientID FROM dbo.UF_GET_CLIENTID4TMONET())    --예외처리 제거(2021.11.29)");
            lines.Add("END");
            var ddl = string.Join("\n", lines);

            var blocks = SourceCommentExtractor.Extract(ddl);

            var block = Assert.Single(blocks, b => b.Kind == "NonExecutable");
            Assert.Contains("UF_GET_CLIENTID4TMONET", block.Anchors);
        }
    }
}
