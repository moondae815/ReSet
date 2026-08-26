using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StepSweepServiceTests
    {
        private const string DdlWithTwoCodes = @"
CREATE PROCEDURE dbo.UP_TEST @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT
AS
BEGIN
    DECLARE @v_err INT = 0;

    UPDATE dbo.TSettleMst SET UseState = 1 WHERE YMD = @pi_strYMD;
    IF @@ERROR <> 0 SET @v_err = -13;

    UPDATE dbo.TSettleMiss SET UseState = 2 WHERE YMD = @pi_strYMD;
    IF @@ERROR <> 0 SET @v_err = -14;
END";

        [Fact]
        public void SimulatedMapPairsEachCodeWithItsStatement()
        {
            var map = StepSweepService.BuildSimulatedErrorCodeMap(DdlWithTwoCodes, "@pi_strYMD");

            Assert.Equal(("UPDATE", 1), map["-13"]);
            Assert.Equal(("UPDATE", 2), map["-14"]);
        }

        // 제품 규칙(SpecStatementFactsExtractor.ReadErrorCodeToOrdinal:299)과 같아야 한다 -
        // 같은 코드가 두 문장에 붙으면 귀속할 수 없으므로 덮어쓰지 않고 아예 뺀다.
        [Fact]
        public void DuplicateCodeIsDroppedNotOverwritten()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.UP_DUP @pi_strYMD CHAR(8)
AS
BEGIN
    DECLARE @v_err INT = 0;
    UPDATE dbo.TA SET C = 1 WHERE YMD = @pi_strYMD;
    IF @@ERROR <> 0 SET @v_err = -13;
    UPDATE dbo.TB SET C = 1 WHERE YMD = @pi_strYMD;
    IF @@ERROR <> 0 SET @v_err = -13;
END";

            Assert.False(
                StepSweepService.BuildSimulatedErrorCodeMap(ddl, "@pi_strYMD").ContainsKey("-13"));
        }

        [Fact]
        public void EmptyOrUnparsableDdlYieldsEmptyMap()
        {
            Assert.Empty(StepSweepService.BuildSimulatedErrorCodeMap(null, "@pi_strYMD"));
            Assert.Empty(StepSweepService.BuildSimulatedErrorCodeMap("NOT SQL (((", "@pi_strYMD"));
        }

        // 왕복이 진짜 리더를 지나는지 확인한다. 헤딩이 어긋나면 리더가 표를 못 찾아
        // 빈 사전을 돌려준다 - 조용히 틀린 사전을 쓰는 대신 눈에 띄는 0이 된다.
        [Fact]
        public void RenderedTableUsesTheHeadingTheRealReaderLooksFor()
        {
            var rendered = StepSweepService.RenderErrorCodeTable(
                DmlScopeExtractor.ExtractErrorCodes(DdlWithTwoCodes, "@pi_strYMD"));

            Assert.Contains(DmlScopeExtractor.ErrorCodeTableHeading, rendered);

            var facts = SpecStatementFactsExtractor.Extract(
                new List<(string, string)> { ("dbo.UP_TEST", rendered) });
            Assert.Equal(2, facts["UP_TEST"].ErrorCodeToOrdinal.Count);

            var broken = rendered.Replace(DmlScopeExtractor.ErrorCodeTableHeading, "### 오류 코드");
            var brokenFacts = SpecStatementFactsExtractor.Extract(
                new List<(string, string)> { ("dbo.UP_TEST", broken) });
            Assert.Empty(brokenFacts["UP_TEST"].ErrorCodeToOrdinal);
        }
    }
}
