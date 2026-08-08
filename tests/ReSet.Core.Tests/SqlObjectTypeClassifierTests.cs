using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SqlObjectTypeClassifierTests
    {
        [Theory]
        [InlineData("SQL_TABLE_VALUED_FUNCTION")]
        [InlineData("SQL_INLINE_TABLE_VALUED_FUNCTION")]
        [InlineData("SQL_SCALAR_FUNCTION")]
        [InlineData("SQL_STORED_PROCEDURE")]
        public void IsCodeObject_ShouldRecogniseFunctionsAndProcedures(string sqlObjectType)
        {
            Assert.True(SqlObjectTypeClassifier.IsCodeObject(sqlObjectType));
        }

        [Theory]
        [InlineData("USER_TABLE")]
        [InlineData("VIEW")]
        [InlineData("SYSTEM_TABLE")]
        public void IsCodeObject_ShouldRejectTablesAndViews(string sqlObjectType)
        {
            Assert.False(SqlObjectTypeClassifier.IsCodeObject(sqlObjectType));
        }

        [Fact]
        public void IsTableOrView_ShouldRejectTableValuedFunctions()
        {
            // 이것이 UIF_SettleYMD의 DDL이 주입되지 않은 이유다.
            // "SQL_TABLE_VALUED_FUNCTION"은 "TABLE"을 포함한다.
            Assert.False(SqlObjectTypeClassifier.IsTableOrView("SQL_TABLE_VALUED_FUNCTION"));
        }

        [Theory]
        [InlineData("USER_TABLE")]
        [InlineData("VIEW")]
        public void IsTableOrView_ShouldAcceptTablesAndViews(string sqlObjectType)
        {
            Assert.True(SqlObjectTypeClassifier.IsTableOrView(sqlObjectType));
        }

        [Fact]
        public void Predicates_ShouldTreatNullAsNeither()
        {
            Assert.False(SqlObjectTypeClassifier.IsCodeObject(null));
            Assert.False(SqlObjectTypeClassifier.IsTableOrView(null));
        }

        [Theory]
        [InlineData("SQL_TABLE_VALUED_FUNCTION", CodeObjectType.Function)]
        [InlineData("SQL_SCALAR_FUNCTION", CodeObjectType.Function)]
        [InlineData("SQL_STORED_PROCEDURE", CodeObjectType.Procedure)]
        [InlineData("USER_TABLE", CodeObjectType.Unresolved)]
        [InlineData(null, CodeObjectType.Unresolved)]
        public void ResolveCodeObjectType_ShouldMapSqlTypeStrings(string? sqlObjectType, CodeObjectType expected)
        {
            Assert.Equal(expected, SqlObjectTypeClassifier.ResolveCodeObjectType(sqlObjectType));
        }
    }
}
