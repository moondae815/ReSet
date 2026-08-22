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

        [Fact]
        public void Extract_LocalMissing_ShouldNameDatabaseNotServerAsTheScopeSetting()
        {
            // Fix Round 1 - default_to_local_cursor(CURSOR_DEFAULT)는 서버 전역이 아니라
            // 데이터베이스 단위 옵션이다. 근거: docs/audit-reports/2026-08-20a-POQSettlePrco20-axisA.md:123
            // ("스코프가 DB 옵션 의존")와 이 클래스의 XML 문서 자체("범위는 DB의
            // default_to_local_cursor 설정에 달려 있다"). "서버의"라고 쓰면 이 문서와
            // 저장소의 실측 근거 모두와 어긋나는 거짓 행이 기계 확정 표에 실린다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE c1 CURSOR FOR SELECT c FROM dbo.T
    OPEN c1
    CLOSE c1
    DEALLOCATE c1
END";

            var fact = Assert.Single(CursorLifecycleExtractor.Extract(ddl));

            Assert.Contains("데이터베이스", fact.Sentence);
            Assert.DoesNotContain("서버", fact.Sentence);
        }

        [Fact]
        public void Extract_ExplicitGlobalCursor_ShouldNotReportFalseScopeSentence()
        {
            // I1 - GLOBAL이 명시되면 범위는 데이터베이스의 default_to_local_cursor 설정과
            // 무관하게 전역으로 확정된다. IsLocal만 보고 판단하면 GLOBAL이 명시된 경우에도
            // "범위가 설정에 달려 있다"는 거짓 문장이 나간다. 확정할 수 없는 새 문장을
            // 지어내는 대신 침묵한다 - 이 클래스의 다른 네 곳과 같은 침묵 계약.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE c1 CURSOR GLOBAL FOR SELECT c FROM dbo.T
    OPEN c1
    CLOSE c1
    DEALLOCATE c1
END";

            Assert.Empty(CursorLifecycleExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_ExplicitGlobalCursorWithUnclosedReturn_StillReportsReturnFact()
        {
            // GLOBAL이 명시돼 범위 문장은 침묵하지만, OPEN과 CLOSE 사이 RETURN 관측은
            // 독립적인 사실이므로 그 문장은 그대로 나가야 한다. 다만 LOCAL 관련 문구는
            // 섞이면 안 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE c1 CURSOR GLOBAL FOR SELECT c FROM dbo.T
    OPEN c1
    IF @@ERROR <> 0 BEGIN RETURN END
    CLOSE c1
    DEALLOCATE c1
END";

            var fact = Assert.Single(CursorLifecycleExtractor.Extract(ddl));
            Assert.Contains("CLOSE/DEALLOCATE에 도달하지 않습니다", fact.Sentence);
            Assert.DoesNotContain("default_to_local_cursor", fact.Sentence);
        }

        [Fact]
        public void Extract_LocalMissing_SentenceNamesBothLocalAndGlobalAsTheGateCondition()
        {
            // Fix Round 2 - 게이트는 !IsLocal && !IsGlobal인데(I1), 이 문장은 여전히
            // "LOCAL이 지정되지 않아"로만 적혀 있었다. 실제로 나는 경우(LOCAL·GLOBAL
            // 둘 다 미지정)에는 참이라 거짓 행은 아니지만, 이 문장은 "기계 확정 — 수정
            // 금지" 표에 그대로 실린다 - 문장만 읽으면 "LOCAL만 안 쓰면 이 사실이
            // 성립한다"고 오독하기 쉽고, GLOBAL이 명시된 경우 이 문장이 아예 나지
            // 않는다는 것을 문장 자신에서는 알 수 없다. 게이트·클래스 요약·
            // docs/architecture.md는 이미 "LOCAL도 GLOBAL도 없으면"으로 통일됐으므로
            // 산출물에 실제로 박히는 이 문장도 같은 어휘를 쓰게 한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE c1 CURSOR FOR SELECT c FROM dbo.T
    OPEN c1
    CLOSE c1
    DEALLOCATE c1
END";

            var fact = Assert.Single(CursorLifecycleExtractor.Extract(ddl));

            Assert.Contains("LOCAL", fact.Sentence);
            Assert.Contains("GLOBAL", fact.Sentence);
        }
    }
}
