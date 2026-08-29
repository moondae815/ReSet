using System.Linq;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class LocalVariableTableL1Tests
    {
        private const string Ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intCLTotal MONEY = 0
    DECLARE @v_strClientID VARCHAR(20)
    UPDATE T SET C = 1 WHERE K = 2
END";

        // Validate는 인스턴스 메서드다 - `public ValidationResult Validate(string, SpecExpectations?)`
        // (MechanicalValidator.cs:154). 생성자는 `MechanicalValidator(bool useMermaidCli = false)`라
        // 인자가 필요 없다. 기존 테스트도 `new MechanicalValidator()`를 쓴다
        // (VerificationPipelineOrchestratorTests.cs:38).
        private static ValidationResult Validate(string markdown, SpecExpectations expectations) =>
            new MechanicalValidator().Validate(markdown, expectations);

        private static SpecExpectations Expectations() =>
            SpecExpectations.From(new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                DdlText = Ddl
            })!;

        private static string DocWithTable(string rows) =>
            "## 파라미터 목록\n\n"
            + LocalVariableDeclarationExtractor.TableHeading + "\n"
            + "| 변수 명칭 | 데이터 타입 | 초기값 |\n"
            + "| :--- | :--- | :--- |\n"
            + rows
            + "\n### 다음 절\n";

        private const string CompleteRows =
            "| @v_intCLTotal | MONEY | 0 |\n| @v_strClientID | VARCHAR(20) |  |\n";

        [Fact]
        public void WhenTableIsCompletelyTranscribed_ShouldNotReport()
        {
            var result = Validate(DocWithTable(CompleteRows), Expectations());

            Assert.DoesNotContain(result.Errors, e => e.Contains("지역 변수"));
        }

        [Fact]
        public void WhenTheHeadingIsMissing_ShouldReportOnce()
        {
            var result = Validate("## 파라미터 목록\n\n본문뿐입니다.\n", Expectations());

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.LocalVariableTableMismatch);
            Assert.Contains(result.Errors, e => e.Contains(LocalVariableDeclarationExtractor.TableHeading));
        }

        [Fact]
        public void WhenARowIsMissing_ShouldReportThatVariable()
        {
            var result = Validate(
                DocWithTable("| @v_intCLTotal | MONEY | 0 |\n"), Expectations());

            Assert.Contains(result.Errors, e => e.Contains("@v_strClientID"));
        }

        [Fact]
        public void WhenADeclaredTypeIsChanged_ShouldReportThatVariable()
        {
            // 이 검사가 존재하는 이유다 - 이름이 int를 시사하는 MONEY 변수를 모델이
            // INT로 적으면 이행자가 그대로 선언해 금액이 절삭된다.
            var result = Validate(
                DocWithTable("| @v_intCLTotal | INT | 0 |\n| @v_strClientID | VARCHAR(20) |  |\n"),
                Expectations());

            Assert.Contains(result.Errors, e => e.Contains("@v_intCLTotal") && e.Contains("MONEY"));
        }

        [Fact]
        public void WhenTheTableHasAnInventedRow_ShouldReportIt()
        {
            // 역방향. 전사 표이므로 사실 없는 행은 그 자체로 위반이다.
            var result = Validate(
                DocWithTable(CompleteRows + "| @v_invented | INT | 0 |\n"), Expectations());

            Assert.Contains(result.Errors, e => e.Contains("@v_invented"));
        }

        [Fact]
        public void WhenThereAreNoDeclarations_ShouldStaySilent()
        {
            var expectations = SpecExpectations.From(new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "Q", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "Q",
                DdlText = "CREATE PROCEDURE dbo.Q AS BEGIN UPDATE T SET C = 1 WHERE K = 2 END"
            })!;

            var result = Validate("## 파라미터 목록\n\n본문뿐입니다.\n", expectations);

            Assert.DoesNotContain(result.Errors, e => e.Contains("지역 변수"));
        }
    }
}
