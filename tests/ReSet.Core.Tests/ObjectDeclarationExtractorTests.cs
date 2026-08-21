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
            // Fix Round 1 리뷰가 뮤테이션으로 실측: Contains+Count만으로는 Render에서
            // ReturnsNullOnNullInput 케이스를 통째로 지워도(결과가 "RETURNSNULLONNULLINPUT"로
            // 뭉개져도) 이 테스트가 계속 통과했다. 순서·문자열을 그대로 단언해 막는다.
            const string ddl =
                "CREATE FUNCTION dbo.F(@a INT) RETURNS INT " +
                "WITH SCHEMABINDING, RETURNS NULL ON NULL INPUT AS BEGIN RETURN 1 END";

            Assert.Equal(
                new[] { "SCHEMABINDING", "RETURNS NULL ON NULL INPUT" },
                ObjectDeclarationExtractor.Extract(ddl)!.WithOptions);
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

        [Fact]
        public void Extract_AlterFunction_ReportsOptions()
        {
            // Fix Round 1 리뷰 실측(180.37.3 프로브): ALTER FUNCTION은 AlterFunctionStatement로,
            // CREATE OR ALTER FUNCTION은 CreateOrAlterFunctionStatement로 각각 CreateFunctionStatement와
            // 구조적으로 다른 타입에 파싱된다. DbMetadataService.cs가 읽는 sys.sql_modules.definition은
            // 마지막 배포에 실제로 쓰인 CREATE/ALTER 키워드를 그대로 보존하므로, 이 셋을 다 못 잡으면
            // 표가 조용히 빠진다 - 정확히 이 작업이 닫으려는 "🟡 확인할 수 없음" 결함 모양이다.
            // 지금 코퍼스(31개 객체)에는 ALTER FUNCTION 형태가 0건이라 오늘 물지는 않지만,
            // SessionOptionsExtractor.ProcedureBodyFinder가 같은 이유로 CreateProcedureStatement와
            // CreateOrAlterProcedureStatement 둘 다 훑는 선례가 이미 이 저장소에 있어 따르지
            // 않을 근거가 없다.
            const string ddl =
                "ALTER FUNCTION dbo.F(@a INT) RETURNS INT WITH SCHEMABINDING " +
                "AS BEGIN RETURN 1 END";

            var fact = ObjectDeclarationExtractor.Extract(ddl);

            Assert.NotNull(fact);
            Assert.Equal("dbo.F", fact!.QualifiedName);
            Assert.Equal(new[] { "SCHEMABINDING" }, fact.WithOptions);
        }

        [Fact]
        public void Extract_CreateOrAlterFunction_ReportsOptions()
        {
            const string ddl =
                "CREATE OR ALTER FUNCTION dbo.F(@a INT) RETURNS INT WITH SCHEMABINDING " +
                "AS BEGIN RETURN 1 END";

            var fact = ObjectDeclarationExtractor.Extract(ddl);

            Assert.NotNull(fact);
            Assert.Equal("dbo.F", fact!.QualifiedName);
            Assert.Equal(new[] { "SCHEMABINDING" }, fact.WithOptions);
        }

        [Fact]
        public void Extract_ExecuteAsCaller_RendersPrincipal()
        {
            // Fix Round 1 리뷰 실측: ExecuteAs는 ExecuteAsFunctionOption 노드로 파싱되고
            // 실제 원문(CALLER/SELF/OWNER/'user')은 OptionKind가 아니라 ExecuteAs.ExecuteAsOption에
            // 있다. 옛 fallback(kind.ToString())은 "EXECUTEAS"만 냈다 - 원본에서 찾을 수 없는
            // 텍스트이고 principal 정보가 통째로 사라진다.
            const string ddl =
                "CREATE FUNCTION dbo.F(@a INT) RETURNS INT WITH EXECUTE AS CALLER " +
                "AS BEGIN RETURN 1 END";

            Assert.Equal(
                new[] { "EXECUTE AS CALLER" },
                ObjectDeclarationExtractor.Extract(ddl)!.WithOptions);
        }

        [Fact]
        public void Extract_ExecuteAsUserLiteral_RendersQuotedPrincipal()
        {
            // 'user_name' 형은 ExecuteAsOption.String이고 실제 이름은 Literal.Value에
            // 따옴표 없이 담긴다 - 원문 표기(따옴표 포함)로 되돌려 실어야 독자가 DDL에서
            // 찾을 수 있다.
            const string ddl =
                "CREATE FUNCTION dbo.F(@a INT) RETURNS INT WITH EXECUTE AS 'someuser' " +
                "AS BEGIN RETURN 1 END";

            Assert.Equal(
                new[] { "EXECUTE AS 'someuser'" },
                ObjectDeclarationExtractor.Extract(ddl)!.WithOptions);
        }

        [Fact]
        public void Extract_NativeCompilation_RendersWithUnderscore()
        {
            // Fix Round 1 리뷰 실측: 실제 T-SQL 키워드는 NATIVE_COMPILATION(밑줄 포함)인데
            // 옛 fallback(kind.ToString().ToUpperInvariant())은 "NATIVECOMPILATION"을 내
            // 원문에서 찾을 수 없었다.
            const string ddl =
                "CREATE FUNCTION dbo.F(@a INT) RETURNS INT WITH NATIVE_COMPILATION, SCHEMABINDING " +
                "AS BEGIN RETURN 1 END";

            Assert.Equal(
                new[] { "NATIVE_COMPILATION", "SCHEMABINDING" },
                ObjectDeclarationExtractor.Extract(ddl)!.WithOptions);
        }

        [Fact]
        public void Extract_InlineOn_RendersOptionState()
        {
            // Fix Round 1 리뷰 실측: Inline은 InlineFunctionOption 노드고 ON/OFF 상태가
            // OptionState에 있다. 옛 fallback은 "INLINE"만 내고 상태를 버렸는데, 상태가
            // 이 옵션의 존재 이유(INLINE = ON이 성능 계약이다) 그 자체다.
            const string ddl =
                "CREATE FUNCTION dbo.UIF_T(@a INT) RETURNS TABLE WITH INLINE = ON " +
                "AS RETURN (SELECT 1 AS X)";

            Assert.Equal(
                new[] { "INLINE = ON" },
                ObjectDeclarationExtractor.Extract(ddl)!.WithOptions);
        }

        [Fact]
        public void Extract_ValidFunctionFollowedByMalformedBatch_ReturnsNull()
        {
            // Fix Round 1 리뷰 실측: 유효한 CREATE FUNCTION 뒤에 깨진 배치가 와도 파서는
            // fragment를 non-null로, errors.Count==1로 돌려준다 - 첫 배치는 정상 파싱됐기
            // 때문이다. Extract의 errors 가드를 지우면 방문자가 첫 함수를 그대로 찾아
            // 그럴듯하지만 틀린(파싱이 실은 실패한) Fact를 낸다. Extract_UnparsableDdl_ReturnsNull은
            // 배치 자체가 하나도 안 만들어지는 입력이라 이 가드 없이도 통과해 왔다 - 그
            // 갭을 이 테스트가 메운다.
            const string ddl =
                "CREATE FUNCTION dbo.F(@a INT) RETURNS INT AS BEGIN RETURN 1 END\n" +
                "GO\n" +
                "CREATE PROCEDURE (((";

            Assert.Null(ObjectDeclarationExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_MultipleFunctionStatements_ReturnsFirstOnly()
        {
            // DbMetadataService가 넘기는 DdlText는 항상 sys.sql_modules의 단일 객체
            // 정의 하나뿐이라 실제 입력에서는 도달하지 않지만, "첫 번째가 이긴다"는
            // CreateFunctionVisitor의 계약을 테스트로 고정해 둔다.
            const string ddl =
                "CREATE FUNCTION dbo.F1(@a INT) RETURNS INT AS BEGIN RETURN 1 END\n" +
                "GO\n" +
                "CREATE FUNCTION dbo.F2(@a INT) RETURNS INT AS BEGIN RETURN 2 END";

            var fact = ObjectDeclarationExtractor.Extract(ddl);

            Assert.NotNull(fact);
            Assert.Equal("dbo.F1", fact!.QualifiedName);
        }
    }
}
