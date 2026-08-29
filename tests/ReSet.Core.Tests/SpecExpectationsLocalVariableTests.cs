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
        public void From_WhenDdlHasNoDeclare_ShouldProduceEmptyLocalVariableList()
        {
            // [이 테스트가 실제로 재는 것] `SpecExpectations.From`은 언제나
            // `LocalVariableDeclarations`를 명시적으로 대입한다 - 이 테스트는 그 값을
            // 재는 것이지 record의 기본값 메커니즘을 재는 것이 아니다(2026-08-29 리뷰,
            // 이름이 넓었다). DECLARE가 없는 DDL을 넣으면 추출기가 빈 목록을 낸다는
            // 사실만 확인한다.
            //
            // [진짜 기본값 의존 호출부] record의 기본 매개변수 값에 실제로 기대는
            // 곳은 `MechanicalValidatorTests
            // .Validate_WhenSameCanonicalIsInBothColumnSetAndColumnlessSet_ShouldNotSelfConflict`
            // (MechanicalValidatorTests.cs:1733)다 - `From()`을 거치지 않고
            // `SpecExpectations`를 4개 인자로 직접 생성하며 `LocalVariableDeclarations`를
            // 생략한다. 그 호출이 컴파일되고 빈 목록으로 동작하는 것이 record 기본값의
            // 실물 증거다.
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
