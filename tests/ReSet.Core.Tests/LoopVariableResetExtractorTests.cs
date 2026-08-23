using System.Collections.Generic;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class LoopVariableResetExtractorTests
    {
        private static readonly Dictionary<string, string> NoColumns = new();

        [Fact]
        public void Extract_ConstantSetAtTopOfWhileBody_ShouldSayItRepeatsEachIteration()
        {
            // UP_UTIL_SETTLE_PROC_ETC:69 실측(🔴) - WHILE 본문 첫 문장 SET @v_intID = 0이
            // 커서 행마다 재설정한다. 이 사실이 없으면 이행자가 재설정을 빠뜨리고, 무매칭
            // 행에서 선행 ID가 남아 IF @v_intID > 0이 반대 갈래를 타 UPDATE가 0행 갱신 →
            // 신규 INSERT 누락 → 금액 검증 불일치로 배치 전량 롤백된다.
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

            var fact = Assert.Single(LoopVariableResetExtractor.Extract(ddl));

            Assert.Equal(7, fact.Line);
            Assert.Equal("@v_intID", fact.Variable);
            Assert.Equal("0", fact.Value);
            Assert.Contains("반복마다", fact.Sentence);
        }

        [Fact]
        public void Extract_SetOutsideLoop_ShouldNotBeCollected()
        {
            // 루프 밖 SET은 DECLARE 초기값과 다르지 않다 - 담을 사실이 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SET @v = 0
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_SetNestedInsideIfWithinLoop_ShouldNotBeCollected()
        {
            // 코퍼스 실물 6건이 이 모양이다(PROC_ETC:139 · SUMMARY_ETC:76·77·128·129 ·
            // WORKDAY2:36). 조건 안에 있으면 "반복마다 다시 실행된다"가 거짓이다 -
            // PROC_ETC:139는 뒤에 RETURN이 붙어 있어 평생 한 번 실행될 수도 있다.
            // 이 테스트가 "루프 본문 최상위" 규칙이 실제로 가르는 것이 있음을 못박는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @po_intRetVal INT
    WHILE (@@FETCH_STATUS = 0) BEGIN
        IF @@ERROR <> 0 BEGIN
            SET @po_intRetVal = -3
            RETURN
        END
    END
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_AccumulatorSetInLoop_ShouldNotBeCollected()
        {
            // UF_GET_WORKDAY2:31 · SUMMARY_ETC:135 실측. 자기 자신을 읽어 더하는 대입은
            // 재설정이 아니라 누적이다 - 직전 반복의 값이 남는 것이 버그가 아니라 요점이다.
            // 종류 칸 "루프 내 재설정" 자체가 주장이므로 이런 행은 첫 칸부터 거짓이 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intIdx INT = 0
    WHILE @v_intIdx < 10 BEGIN
        SET @v_intIdx = @v_intIdx + 1
    END
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_NonConstantSetInLoop_ShouldNotBeCollected()
        {
            // SUMMARY_ETC:77·129 실측 - 문자열 이어붙이기 대입. 다른 변수를 읽으므로
            // "이 지점의 값은 언제나 이 상수"가 거짓이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @po_strErrMsg VARCHAR(200), @v_strClientID VARCHAR(20)
    WHILE (@@FETCH_STATUS = 0) BEGIN
        SET @po_strErrMsg = 'DELETE 실패(' + @v_strClientID + ')'
    END
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_SetPrecededByConditionalExit_ShouldNotBeCollected()
        {
            // "반복마다 다시 실행된다"는 앞선 문장이 루프를 벗어나지 않을 때만 참이다.
            // WHILE 1=1 + IF ... BREAK는 흔한 커서 관용구다. 코퍼스에는 이 모양이 0건이나
            // (실측), 조건을 확인할 수 있는데 확인하지 않으면 거짓 문장이 표에 실린다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    WHILE 1 = 1 BEGIN
        IF @@FETCH_STATUS <> 0 BREAK
        SET @v = 0
    END
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_NegativeLiteral_ShouldBeCollected()
        {
            // 부호 붙은 리터럴도 상수다 - ScriptDom은 -1을 UnaryExpression으로 싼다.
            // 여기서 끊으면 SET @v = 0은 담고 SET @v = -1은 조용히 빠지는 구멍이 생긴다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intIdx INT
    WHILE @v_intIdx < 10 BEGIN
        SET @v_intIdx = -1
    END
END";

            var fact = Assert.Single(LoopVariableResetExtractor.Extract(ddl));

            Assert.Equal("@v_intIdx", fact.Variable);
            Assert.Equal("-1", fact.Value);
        }

        [Fact]
        public void Extract_WhileBodyWithoutBeginEnd_ShouldBeCollected()
        {
            // BEGIN/END 없는 단일 문장 본문도 그 문장은 최상위다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    WHILE (@@FETCH_STATUS = 0)
        SET @v = 0
END";

            var fact = Assert.Single(LoopVariableResetExtractor.Extract(ddl));

            Assert.Equal("@v", fact.Variable);
        }

        [Fact]
        public void Extract_CompoundAssignmentInLoop_ShouldNotBeCollected()
        {
            // SET @v += 1은 오른쪽이 리터럴이어도 직전 값을 읽는다 - 재설정이 아니다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT = 0
    WHILE @v < 10 BEGIN
        SET @v += 1
    END
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_UnparseableDdl_ShouldReturnEmpty()
        {
            // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
            Assert.Empty(LoopVariableResetExtractor.Extract("CREATE PROCEDURE ((("));
            Assert.Empty(LoopVariableResetExtractor.Extract(null));
            Assert.Empty(LoopVariableResetExtractor.Extract("   "));
        }

        [Fact]
        public void Collect_LoopVariableReset_ShouldLandInTheExecutionSemanticsTable()
        {
            // 추출기가 사실을 내도 Collect에 갈래가 없으면 표에 한 행도 실리지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intID INT = 0
    WHILE (@@FETCH_STATUS = 0) BEGIN
        SET @v_intID = 0
    END
END";

            var facts = ExecutionSemanticsFacts.Collect(ddl, null, null, NoColumns);

            var fact = Assert.Single(
                facts, f => f.Kind == ExecutionSemanticsFacts.LoopVariableResetKind);
            Assert.Equal("루프 내 재설정", fact.Kind);
            Assert.Equal("7", fact.Line);
            Assert.Equal("SET @v_intID = 0", fact.Target);
            Assert.Contains("반복마다", fact.Fact);
        }
    }
}
