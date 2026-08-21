using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class CursorLifecycleExtractorTests
    {
        [Fact]
        public void Extract_ReturnBetweenOpenAndClose_ShouldReportUnreachedCloseOnThatPath()
        {
            // UP_UTIL_SETTLE_SUMMARY_ETC 실측: 두 오류 경로가 ROLLBACK → SET → RETURN
            // 으로 끝나고 CLOSE/DEALLOCATE는 정상 종료 경로에만 있다. 커서는
            // BEGIN TRAN보다 먼저 OPEN돼 롤백으로도 닫히지 않는다.
            //
            // 문장은 "RETURN이 OPEN과 CLOSE 사이에 있다"는 렉시컬 관측과 "그 경로로
            // 나가면 CLOSE/DEALLOCATE에 도달하지 않는다"는 직접 귀결만 담는다. 그 RETURN이
            // 실제로 오류 경로인지, 도달 가능한지는 정적으로 확정할 수 없으므로 단정하지
            // 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE GetDataCrsr CURSOR READ_ONLY FOR SELECT c FROM dbo.T
    OPEN GetDataCrsr
    IF @@ERROR <> 0 BEGIN RETURN END
    CLOSE GetDataCrsr
    DEALLOCATE GetDataCrsr
END";

            var facts = CursorLifecycleExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("GetDataCrsr", fact.CursorName);
            Assert.Contains("RETURN", fact.Sentence);
            Assert.Contains("CLOSE/DEALLOCATE에 도달하지 않습니다", fact.Sentence);
            Assert.DoesNotContain("오류 경로", fact.Sentence);
            Assert.Contains("LOCAL", fact.Sentence);
        }

        [Fact]
        public void Extract_CursorWithLocalAndNoEarlyReturn_ShouldNotBeReported()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE c1 CURSOR LOCAL FOR SELECT c FROM dbo.T
    OPEN c1
    CLOSE c1
    DEALLOCATE c1
END";

            Assert.Empty(CursorLifecycleExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(CursorLifecycleExtractor.Extract("CREATE PROCEDURE ((("));
        }

        [Fact]
        public void Extract_ReopenedCursorReturnAfterFirstClose_ShouldNotReportFalsePositive()
        {
            // 같은 이름이 두 번 OPEN/CLOSE된다. RETURN은 두 번째 OPEN 뒤에 있지만
            // "첫 OPEN ~ 첫 CLOSE" 구간 밖이다. 그 구간을 판정 기준으로 삼으므로
            // 이 RETURN을 두 번째 구간에 속한다고 단정하지 않고 침묵한다 - 과소 포착은
            // Minor이고 거짓 행은 Critical이라는 원칙에 따른다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE c1 CURSOR LOCAL FOR SELECT c FROM dbo.T
    OPEN c1
    CLOSE c1
    OPEN c1
    IF @@ERROR <> 0 BEGIN RETURN END
    CLOSE c1
    DEALLOCATE c1
END";

            Assert.Empty(CursorLifecycleExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_CloseBeforeOpen_ShouldNotReportFalsePositive()
        {
            // CLOSE가 텍스트상 OPEN보다 앞선 어긋난 순서다. "OPEN과 CLOSE 사이"라는
            // 관측 자체가 성립하지 않으므로 침묵한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE c1 CURSOR LOCAL FOR SELECT c FROM dbo.T
    CLOSE c1
    OPEN c1
    RETURN
    DEALLOCATE c1
END";

            Assert.Empty(CursorLifecycleExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_DeallocateWithoutClose_ShouldNotReportFalsePositive()
        {
            // CLOSE가 아예 없고 DEALLOCATE만 있다. "OPEN과 CLOSE 사이"를 관측할 수
            // 없으므로 침묵한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE c1 CURSOR LOCAL FOR SELECT c FROM dbo.T
    OPEN c1
    RETURN
    DEALLOCATE c1
END";

            Assert.Empty(CursorLifecycleExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_OpenInsideIfBlock_IsDetected()
        {
            // 방문 지점 커버리지 - OPEN이 IF...BEGIN...END 블록 안에 있어도 방문된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE c1 CURSOR LOCAL FOR SELECT c FROM dbo.T
    IF 1 = 1
    BEGIN
        OPEN c1
    END
    IF @@ERROR <> 0
    BEGIN
        RETURN
    END
    CLOSE c1
    DEALLOCATE c1
END";

            var fact = Assert.Single(CursorLifecycleExtractor.Extract(ddl));
            Assert.Equal("c1", fact.CursorName);
        }

        [Fact]
        public void Extract_ReturnInsideNestedWhileInsideIf_IsDetected()
        {
            // 방문 지점 커버리지 - RETURN이 WHILE 블록 안, 그 WHILE이 다시 IF 블록
            // 안에 중첩돼도 방문된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE c1 CURSOR LOCAL FOR SELECT c FROM dbo.T
    OPEN c1
    WHILE 1 = 1
    BEGIN
        IF @@ERROR <> 0
        BEGIN
            RETURN
        END
        BREAK
    END
    CLOSE c1
    DEALLOCATE c1
END";

            var fact = Assert.Single(CursorLifecycleExtractor.Extract(ddl));
            Assert.Equal("c1", fact.CursorName);
            Assert.Contains("RETURN", fact.Sentence);
        }
    }
}
