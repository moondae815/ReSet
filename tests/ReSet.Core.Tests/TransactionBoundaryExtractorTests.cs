using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class TransactionBoundaryExtractorTests
    {
        [Fact]
        public void Extract_BeginCommitRollback_ShouldRecordLineAndKindInDocumentOrder()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    UPDATE dbo.T SET A = 1
    IF @@ERROR <> 0
        ROLLBACK TRANSACTION
    COMMIT TRANSACTION
END";

            var facts = TransactionBoundaryExtractor.Extract(ddl);

            Assert.Equal(3, facts.Count);
            Assert.Equal(3, facts[0].Line);
            Assert.Equal("BEGIN TRANSACTION", facts[0].Kind);
            Assert.Equal("ROLLBACK TRANSACTION", facts[1].Kind);
            Assert.Equal("COMMIT TRANSACTION", facts[2].Kind);
        }

        [Fact]
        public void Extract_UnnamedTransaction_ShouldRecordPlaceholderName()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    COMMIT TRANSACTION
END";

            var facts = TransactionBoundaryExtractor.Extract(ddl);

            Assert.All(facts, f => Assert.Equal("(없음)", f.Name));
        }

        [Fact]
        public void Extract_NamedTransaction_ShouldKeepNameVerbatim()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION SettleTran
    COMMIT TRANSACTION SettleTran
END";

            var facts = TransactionBoundaryExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Equal("SettleTran", facts[0].Name);
            Assert.Equal("SettleTran", facts[1].Name);
        }

        [Fact]
        public void Extract_SaveTransaction_ShouldBeRecordedAsItsOwnKind()
        {
            // 실측 코퍼스에는 0건이다. 그래도 담는 이유는 세이브포인트가 하나라도 있으면
            // 롤백 의미가 전체 취소가 아니라 지점 복귀로 바뀌기 때문이다 - 빠뜨리면 이 표가
            // "트랜잭션 경계는 이게 전부"라고 거짓말을 한다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    SAVE TRANSACTION Point1
    ROLLBACK TRANSACTION Point1
    COMMIT TRANSACTION
END";

            var facts = TransactionBoundaryExtractor.Extract(ddl);

            Assert.Equal(4, facts.Count);
            Assert.Contains(facts, f => f.Kind == "SAVE TRANSACTION" && f.Name == "Point1");
            Assert.Contains(facts, f => f.Kind == "ROLLBACK TRANSACTION" && f.Name == "Point1");
        }

        [Fact]
        public void Extract_NestedTransactions_ShouldRecordEveryStatement()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    BEGIN TRANSACTION
    COMMIT TRANSACTION
    COMMIT TRANSACTION
END";

            var facts = TransactionBoundaryExtractor.Extract(ddl);

            Assert.Equal(4, facts.Count);
            Assert.Equal(2, System.Linq.Enumerable.Count(facts, f => f.Kind == "BEGIN TRANSACTION"));
        }

        [Fact]
        public void Extract_NoTransaction_ShouldReturnEmpty()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T SET A = 1
END";

            Assert.Empty(TransactionBoundaryExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(TransactionBoundaryExtractor.Extract("CREATE PROCEDURE ((("));
        }

        [Fact]
        public void Extract_NullOrBlank_ShouldReturnEmpty()
        {
            Assert.Empty(TransactionBoundaryExtractor.Extract(null));
            Assert.Empty(TransactionBoundaryExtractor.Extract("   "));
        }

        [Fact]
        public void TableHeading_ShouldCarryTheMachineConfirmedSuffix()
        {
            Assert.EndsWith(
                MachineConfirmedTables.HeadingSuffix,
                TransactionBoundaryExtractor.TableHeading);
        }
    }
}
