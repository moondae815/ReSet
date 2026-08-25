using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SpecExpectationsTransactionAndSetTests
    {
        private static SpDefinition Def(string ddl) => new()
        {
            ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            DdlText = ddl
        };

        [Fact]
        public void From_TransactionOnlyProcedure_ShouldNotReturnNull()
        {
            // 작성 계약 1: null 체인에 자기 항을 잇지 않으면 이 명세서에서 L1이
            // 한 번도 안 돈다. 스위트는 초록으로 남는다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    COMMIT TRANSACTION
END";

            var expectations = SpecExpectations.From(Def(ddl));

            Assert.NotNull(expectations);
            Assert.Equal(2, expectations!.TransactionBoundaries.Count);
        }

        [Fact]
        public void From_SetAssignmentOnlyProcedure_ShouldNotReturnNull()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT
    SET @v = 1
END";

            var expectations = SpecExpectations.From(Def(ddl));

            Assert.NotNull(expectations);
            Assert.Single(expectations!.SetAssignments);
        }

        [Fact]
        public void From_EmptyProcedure_ShouldStillReturnNull()
        {
            // 체인을 넓히되 "아무 재료도 없으면 null"이라는 계약은 지켜야 한다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    PRINT 'x'
END";

            Assert.Null(SpecExpectations.From(Def(ddl)));
        }
    }
}
