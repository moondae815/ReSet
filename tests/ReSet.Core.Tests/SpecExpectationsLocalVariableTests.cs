using System.Linq;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SpecExpectationsLocalVariableTests
    {
        private static SpDefinition Def(string ddl) => new()
        {
            ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            DdlText = ddl
        };

        [Fact]
        public void From_ShouldCarryLocalVariableDeclarations()
        {
            var expectations = SpecExpectations.From(Def(@"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intCLTotal MONEY = 0
    UPDATE T SET C = 1 WHERE K = 2
END"));

            Assert.NotNull(expectations);
            Assert.Contains(
                expectations!.LocalVariableDeclarations,
                f => f.Name == "@v_intCLTotal" && f.DataType == "MONEY");
        }

        [Fact]
        public void From_WhenLocalVariablesAreTheOnlyMaterial_ShouldNotReturnNull()
        {
            // 널 체인 항을 안 이으면 여기서 null이 나오고 새 L1 검사가 한 번도 안 돈다.
            // 같은 파일의 objectDeclaration 항 주석이 그 실패 양식을 실측으로 적었다.
            //
            // [이 DDL이 정말 다른 재료를 안 만드는지] 본문에 DML 문장이 없고 WITH 절도
            // 없다. 만약 다른 항이 함께 채워지면 이 테스트는 공허한 참이 되므로,
            // Step 4에서 널 체인의 지역 변수 항을 지워 실제로 빨개지는지 확인한다.
            var expectations = SpecExpectations.From(Def(@"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_only INT
END"));

            Assert.NotNull(expectations);
            Assert.Single(expectations!.LocalVariableDeclarations);
        }

        [Fact]
        public void LocalVariableDeclarations_ShouldDefaultToEmptyNotNull()
        {
            var expectations = SpecExpectations.From(Def(@"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE T SET C = 1 WHERE K = 2
END"));

            Assert.NotNull(expectations);
            Assert.Empty(expectations!.LocalVariableDeclarations);
        }
    }
}
