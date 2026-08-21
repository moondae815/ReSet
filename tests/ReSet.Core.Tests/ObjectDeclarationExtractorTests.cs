using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ObjectDeclarationExtractorTests
    {
        [Fact]
        public void Extract_FunctionWithoutOptions_ReportsEmptyList()
        {
            // UF_GET_OUTYMD4REFUND:16-18 실측. WITH 절이 없다는 것이 원문에서 확정되는데
            // 명세서가 "확인할 수 없음"으로 적어 🟡이었다. 빈 목록이 곧 "스키마 바인딩 아님"이다.
            const string ddl =
                "CREATE FUNCTION dbo.UF_GET_OUTYMD4REFUND(@a VARCHAR(8)) " +
                "RETURNS VARCHAR(8) AS BEGIN RETURN '' END";

            var fact = ObjectDeclarationExtractor.Extract(ddl);

            Assert.NotNull(fact);
            Assert.Equal("dbo.UF_GET_OUTYMD4REFUND", fact!.QualifiedName);
            Assert.Empty(fact.WithOptions);
        }

        [Fact]
        public void Extract_FunctionWithSchemaBinding_ReportsIt()
        {
            const string ddl =
                "CREATE FUNCTION dbo.F(@a INT) RETURNS INT WITH SCHEMABINDING " +
                "AS BEGIN RETURN 1 END";

            Assert.Equal(
                new[] { "SCHEMABINDING" },
                ObjectDeclarationExtractor.Extract(ddl)!.WithOptions);
        }

        [Fact]
        public void Extract_FunctionWithSeveralOptions_ListsAll()
        {
            const string ddl =
                "CREATE FUNCTION dbo.F(@a INT) RETURNS INT " +
                "WITH SCHEMABINDING, RETURNS NULL ON NULL INPUT AS BEGIN RETURN 1 END";

            var options = ObjectDeclarationExtractor.Extract(ddl)!.WithOptions;

            Assert.Contains("SCHEMABINDING", options);
            Assert.Equal(2, options.Count);
        }

        [Fact]
        public void Extract_InlineTableValuedFunction_IsCovered()
        {
            // 인라인 TVF도 WITH 옵션을 질 수 있다. 스칼라와 같게 다룬다.
            const string ddl =
                "CREATE FUNCTION dbo.UIF_T(@a INT) RETURNS TABLE " +
                "WITH SCHEMABINDING AS RETURN (SELECT 1 AS X)";

            Assert.Equal(
                new[] { "SCHEMABINDING" },
                ObjectDeclarationExtractor.Extract(ddl)!.WithOptions);
        }

        [Fact]
        public void Extract_Procedure_ReturnsNull()
        {
            // 프로시저에는 SCHEMABINDING 옵션 자체가 없다. 표를 싣지 않는다.
            const string ddl = "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 END";

            Assert.Null(ObjectDeclarationExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_UnparsableDdl_ReturnsNull()
        {
            Assert.Null(ObjectDeclarationExtractor.Extract("CREATE FUNCTION ((("));
        }

        [Fact]
        public void Extract_EmptyDdl_ReturnsNull()
        {
            Assert.Null(ObjectDeclarationExtractor.Extract(null));
            Assert.Null(ObjectDeclarationExtractor.Extract("   "));
        }
    }
}
