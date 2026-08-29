using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 지역 변수 표가 어느 프롬프트 갈래에 실리는지 잠근다.
    ///
    /// [왜 갈래를 잠그는가 - AiService의 Task 14/17 실측]
    /// 자기가 쓸 수 없는 H2에 표를 넣으라는 지시를 받은 모델은 둘 중 하나를 한다 -
    /// H2 제약을 어기고 헤딩을 합성하거나(같은 ### 가 두 번 생기고 LocateHeadingSection이
    /// 첫 일치만 보므로 뒤 사본이 조용히 사라진다), 표를 통째로 버린다.
    /// 이 표의 거처는 `## 파라미터 목록`이고 그것을 쓰는 갈래는 OverviewAndParameters다.
    ///
    /// [왜 System과 User를 이어 붙여 보는가] 이 표가 두 프롬프트 중 어느 쪽에 실리는지는
    /// 이 테스트의 관심사가 아니다 - 관심사는 "그 갈래의 모델이 이 표를 보는가"다.
    /// 한쪽만 단언하면 조립 자리가 바뀔 때 내용이 그대로인데도 빨개진다.
    /// </summary>
    public class LocalVariableTablePromptTests
    {
        private const string Ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intCLTotal MONEY = 0
    UPDATE T SET C = 1 WHERE K = 2
END";

        private static SpDefinition Def() => new()
        {
            ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            DdlText = Ddl
        };

        // 함수 갈래(BuildFunctionSpecificationPrompts, `GenerateSpecificationAsync`가
        // `ObjectType == Function`일 때 위임하는 곳)도 SP 전체 갈래와 같이
        // localVariablePresentation: Table을 받는다 - 픽스 라운드 1, 리뷰 커버리지
        // 공백 지적. DDL은 함수 몸체로도 유효한 형태를 그대로 재사용한다.
        private static SpDefinition FunctionDef() => new()
        {
            ObjectKey = CodeObjectKey.Create("DB", "dbo", "F", CodeObjectType.Function),
            ObjectType = CodeObjectType.Function,
            Schema = "dbo",
            Name = "F",
            DdlText = Ddl
        };

        private static IAiService Service()
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-test");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "ok" });

            return new AiService(client, 0.2f);
        }

        private static string Both(AiResult result) =>
            (result.SystemPrompt ?? "") + "\n" + (result.UserPrompt ?? "");

        [Fact]
        public async Task WholeSpBranch_ShouldCarryTheTableWithItsHeadingAndRows()
        {
            var result = await Service().GenerateSpecificationAsync(Def(), "");
            var prompt = Both(result);

            Assert.Contains(LocalVariableDeclarationExtractor.TableHeading, prompt);
            Assert.Contains("| 변수 명칭 | 데이터 타입 | 초기값 |", prompt);
            Assert.Contains("@v_intCLTotal", prompt);
            Assert.Contains("MONEY", prompt);
            // [변이 검증 - 2026-08-29] 위 세 단언은 이름·타입 칸만 본다. 렌더러가
            // InitialValue 칸을 언제나 빈 칸으로 내도(Ddl의 `= 0`을 버려도) 위
            // 단언은 전부 그대로 초록이었다 - 행 전체 모양을 잡는 이 단언이 그
            // 표시 계층 결함을 잡는다.
            Assert.Contains("| @v_intCLTotal | MONEY | 0 |", prompt);
        }

        [Fact]
        public async Task FunctionBranch_ShouldCarryTheTableWithItsHeadingAndRows()
        {
            var result = await Service().GenerateSpecificationAsync(FunctionDef(), "");
            var prompt = Both(result);

            Assert.Contains(LocalVariableDeclarationExtractor.TableHeading, prompt);
            Assert.Contains("| 변수 명칭 | 데이터 타입 | 초기값 |", prompt);
            Assert.Contains("@v_intCLTotal", prompt);
            Assert.Contains("MONEY", prompt);
            Assert.Contains("| @v_intCLTotal | MONEY | 0 |", prompt);
        }

        [Fact]
        public async Task OverviewAndParametersBranch_ShouldCarryTheTable()
        {
            // 이 갈래가 `## 파라미터 목록`을 쓴다 - 표의 거처다.
            var result = await Service().GenerateSpecSectionAsync(Def(), "OverviewAndParameters", "");

            Assert.Contains(LocalVariableDeclarationExtractor.TableHeading, Both(result));
        }

        [Theory]
        [InlineData("CrudAnalysis")]
        [InlineData("LogicAndVisualization")]
        public async Task BranchesThatCannotWriteParameterList_ShouldNotCarryTheTable(string sectionType)
        {
            var result = await Service().GenerateSpecSectionAsync(Def(), sectionType, "");
            var prompt = Both(result);

            // 참고 재료 형태로도 새지 않아야 한다 - Omit은 표도 참고 재료도 만들지
            // 않는다는 뜻이다(BuildMachineFactBlockLines의 Omit 분기 - Table이 아니면
            // 아무것도 싣지 않는다).
            //
            // [왜 "@v_intCLTotal" 원문 그대로는 여기서 단언하지 않는가] `BuildSpecSectionPrompts`는
            // 갈래와 무관하게 `<sp-source-ddl>`에 원문 DDL 전체를 항상 싣는다
            // (AiService.cs:3648-3652, 2026-07-16/17자 기존 코드로 Task 4 이전부터
            // 있었다). 그 블록이 모든 식별자(변수·파라미터·테이블명 포함)를 이미
            // 그대로 노출하므로 "@v_intCLTotal 문자열이 프롬프트 어디에도 없다"는
            // 이 렌더러의 Table/Omit 분기와 무관하게 항상 거짓이다 - 이 표 렌더러가
            // 원문 DDL 노출 여부를 결정하지 않는다.
            //
            // [대신 행 모양으로 다시 잡는다 - 픽스 라운드 1, Important]
            // 원문 DDL은 `DECLARE @v_intCLTotal MONEY = 0`으로 렌더되어 파이프가
            // 없다 - 그래서 이 마크다운 행 문자열(BuildLocalVariableTableLines가
            // 실제로 내는 모양, EscapeTableCell을 거쳐도 이 값들은 그대로다)은
            // <sp-source-ddl>의 원문 누출과 절대 충돌하지 않으면서, 렌더러가
            // 회귀해 이 갈래에 진짜 표 행을 흘리면 그때는 걸린다. 위 헤딩
            // 단언(TableHeading)만으로는 "헤딩 없이 행만 새는" 회귀를 놓친다 -
            // 이 표가 헤딩과 행을 같은 호출에서 함께 내므로 실전에서는 일어나기
            // 어렵지만, 단언 두 개가 서로 다른 실패 모드를 잡는 편이 한 개보다
            // 강하다.
            Assert.DoesNotContain(LocalVariableDeclarationExtractor.TableHeading, prompt);
            Assert.DoesNotContain("| @v_intCLTotal | MONEY | 0 |", prompt);
        }
    }
}
