using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class DmlScopeExtractorErrorCodeTests
    {
        [Fact]
        public void ExtractErrorCodes_GuardAfterEachUpdate_ShouldPairOrdinalWithCode()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -1
        RETURN
    END
    UPDATE A SET A.Y = 2 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN
        ROLLBACK TRAN
        SET @po_intRetVal = -2
        RETURN
    END
END";

            var facts = DmlScopeExtractor.ExtractErrorCodes(ddl, "@pi_strYMD");

            Assert.Equal(2, facts.Count);
            Assert.Equal("UPDATE", facts[0].Operation);
            Assert.Equal(1, facts[0].StatementOrdinal);
            Assert.Equal("-1", facts[0].Code);
            Assert.Equal("@po_intRetVal", facts[0].Variable);
            Assert.Equal(2, facts[1].StatementOrdinal);
            Assert.Equal("-2", facts[1].Code);
        }

        [Fact]
        public void ExtractErrorCodes_NoGuard_ShouldProduceNoRowForThatStatement()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    UPDATE A SET A.Y = 2 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN
        SET @po_intRetVal = -2
    END
END";

            var facts = DmlScopeExtractor.ExtractErrorCodes(ddl, "@pi_strYMD");

            // 가드가 없는 UPDATE 1은 행이 없다. 침묵이지 실패가 아니다.
            var single = Assert.Single(facts);
            Assert.Equal(2, single.StatementOrdinal);
            Assert.Equal("-2", single.Code);
        }

        [Fact]
        public void ExtractErrorCodes_NextSiblingIsAnotherDml_ShouldNotReachPastIt()
        {
            // UPDATE 1 다음 형제가 IF가 아니라 UPDATE 2다. UPDATE 1은 행이 없어야
            // 하며, 뒤쪽 IF의 코드를 훔쳐 오면 안 된다.
            const string ddl = @"CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    UPDATE A SET A.Y = 2 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN
        SET @po_intRetVal = -9
    END
END";

            var facts = DmlScopeExtractor.ExtractErrorCodes(ddl, "@pi_strYMD");

            Assert.DoesNotContain(facts, f => f.StatementOrdinal == 1);
        }

        [Fact]
        public void ExtractErrorCodes_InsertAndDelete_ShouldNumberPerKind()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P @po_intRetVal INT OUTPUT AS
BEGIN
    INSERT INTO dbo.T (X) VALUES (1)
    IF @@ERROR <> 0 BEGIN SET @po_intRetVal = -5 END
    DELETE FROM dbo.T
    IF @@ERROR <> 0 BEGIN SET @po_intRetVal = -6 END
END";

            var facts = DmlScopeExtractor.ExtractErrorCodes(ddl, "@pi_strYMD");

            Assert.Contains(facts, f => f.Operation == "INSERT" && f.StatementOrdinal == 1 && f.Code == "-5");
            Assert.Contains(facts, f => f.Operation == "DELETE" && f.StatementOrdinal == 1 && f.Code == "-6");
        }

        [Fact]
        public void ExtractErrorCodes_NonNumericAssignment_ShouldBeIgnored()
        {
            // 가드 안이라도 정수 리터럴이 아니면 담지 않는다 - 표는 코드를 담지
            // 식을 담지 않는다.
            const string ddl = @"CREATE PROCEDURE dbo.P @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T AS A
    IF @@ERROR <> 0 BEGIN
        SET @po_intRetVal = @@ERROR
    END
END";

            Assert.Empty(DmlScopeExtractor.ExtractErrorCodes(ddl, "@pi_strYMD"));
        }

        [Fact]
        public void ExtractErrorCodes_UnparsableDdl_ShouldReturnEmpty()
        {
            Assert.Empty(DmlScopeExtractor.ExtractErrorCodes("NOT SQL AT ALL (((", "@pi_strYMD"));
            Assert.Empty(DmlScopeExtractor.ExtractErrorCodes(null, "@pi_strYMD"));
        }
    }
}
