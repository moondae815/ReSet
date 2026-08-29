using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 새 기계 확정 표가 검사 D의 리더에 실제로 닿는지 잠근다.
    ///
    /// [왜 이 테스트가 있는가 - known-defects (5-3-7)]
    /// 검사 D(CheckSpecLocalVariablesDeclared)는 SpecStatementFactsExtractor가 읽은
    /// LocalVariables가 비면 조용히 반환한다. 이 계획이 만든 표가 그 리더에 안 걸리면
    /// 강제 세 층을 다 세우고도 검사는 여전히 꺼져 있다 - 그리고 그 사실은
    /// 아무 테스트도 빨갛게 만들지 않는다.
    /// </summary>
    public class LocalVariableTableSeamTests
    {
        private static string SpecMarkdown() =>
            "## 파라미터 목록\n\n"
            + LocalVariableDeclarationExtractor.TableHeading + "\n"
            + "| 변수 명칭 | 데이터 타입 | 초기값 |\n"
            + "| :--- | :--- | :--- |\n"
            + "| @v_intCLTotal | MONEY | 0 |\n"
            + "| @v_strClientID | VARCHAR(20) |  |\n"
            + "\n## 다음 절\n";

        [Fact]
        public void TheMachineHeading_ShouldBeReadableByTheCheckDReader()
        {
            // FileName에 .md를 붙이면 안 된다 - BareObjectName이 "md"로 뭉갠다.
            // Extract는 IReadOnlyDictionary<string, SpecStatementFacts>를 낸다
            // (SpecStatementFactsExtractor.cs:142). 키는 BareObjectName(fileName)이다.
            var facts = SpecStatementFactsExtractor.Extract(
                new List<(string FileName, string Content)> { ("dbo.P", SpecMarkdown()) });

            var variables = facts.Values.SelectMany(f => f.LocalVariables).ToList();

            Assert.Equal(2, variables.Count);
            Assert.Contains(variables, v => v.Name == "@v_intCLTotal" && v.TypeOrKind == "MONEY");
            Assert.Contains(variables, v => v.Name == "@v_strClientID");
        }

        [Fact]
        public void TheMachineHeading_ShouldStartWithAKnownReaderPrefix()
        {
            // 리더의 실제 접두사 배열(SpecStatementFactsExtractor.LocalVariableHeadingPrefixes,
            // private static)을 리플렉션으로 읽는다 - 손으로 적은 기대값과 비교하면
            // 리더가 접두사 목록을 바꿔도 이 테스트가 계속 초록일 수 있다(픽스 라운드 1,
            // Minor). 선례: MachineConfirmedTablesTests.cs:95.
            var field = typeof(SpecStatementFactsExtractor).GetField(
                "LocalVariableHeadingPrefixes", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);

            var prefixes = (string[])field!.GetValue(null)!;
            Assert.NotEmpty(prefixes);

            Assert.Contains(
                prefixes,
                prefix => LocalVariableDeclarationExtractor.TableHeading.StartsWith(
                    prefix, StringComparison.Ordinal));
        }

        private const string TwoVariableDdl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intCLTotal MONEY = 0
    DECLARE @v_strClientID VARCHAR(20)
    UPDATE T SET C = 1 WHERE K = 2
END";

        private static SpDefinition TwoVariableDef() => new()
        {
            ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            DdlText = TwoVariableDdl
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

        /// <summary>
        /// 프롬프트 본문에서 지역 변수 표 블록만 뽑아 명세서 조각으로 되돌린다.
        ///
        /// [벗기는 것과 벗기지 않는 것 - 근거]
        /// AiService.BuildLocalVariableTableLines는 표의 각 줄 앞에 프롬프트 전용
        /// 들여쓰기 "   "(공백 3칸)를 붙이고, 헤딩 바로 앞줄에 명세서에는 없는
        /// "[CRITICAL LOCAL VARIABLE TABLE] ..." 지시문을 한 줄 얹는다(AiService.cs:1351-1370).
        /// 그 들여쓰기와 지시문은 명세서 원문에는 존재하지 않는 프롬프트 조립 산물이므로
        /// 벗겨야 실제 명세서 문서와 같은 모양이 된다. 그 외에는 손대지 않는다 -
        /// 헤딩 리터럴·헤더 칸 문자열·행 내용은 전부 렌더러가 실제로 낸 그대로 쓴다.
        /// 벗기는 규칙을 "헤딩부터 파이프로 시작하지 않는 줄이 나올 때까지"로 최소화한
        /// 이유는, 그보다 더 다듬으면(예: 특정 칸 개수 검사) 그 규칙 자체가 또 하나의
        /// 손으로 쓴 픽스처가 되기 때문이다.
        /// </summary>
        private static string ExtractRenderedTableAsSpecMarkdown(string prompt)
        {
            var lines = prompt.Replace("\r\n", "\n").Split('\n');
            var headingIndex = Array.FindIndex(
                lines, line => line.TrimStart() == LocalVariableDeclarationExtractor.TableHeading);
            Assert.True(headingIndex >= 0, "렌더러 출력에서 지역 변수 표 헤딩을 찾지 못했습니다.");

            var tableLines = new List<string> { lines[headingIndex].TrimStart() };
            for (var i = headingIndex + 1; i < lines.Length; i++)
            {
                var stripped = lines[i].TrimStart();
                if (!stripped.StartsWith("|", StringComparison.Ordinal))
                {
                    break;
                }

                tableLines.Add(stripped);
            }

            return "## 파라미터 목록\n\n" + string.Join("\n", tableLines) + "\n\n## 다음 절\n";
        }

        [Fact]
        public async Task TheRenderedTable_ShouldBeReadableByTheCheckDReader()
        {
            // 손으로 쓴 픽스처가 아니라 렌더러(AiService.BuildLocalVariableTableLines)가
            // 실제로 낸 표를 리더(SpecStatementFactsExtractor)에 그대로 먹인다 - 이
            // 계획이 실제로 의존하는 이음매는 "내가 쓴 표를 리더가 읽는다"가 아니라
            // "렌더러가 내는 표를 리더가 읽는다"다(픽스 라운드 1, Important).
            var result = await Service().GenerateSpecificationAsync(TwoVariableDef(), "");
            var prompt = (result.SystemPrompt ?? "") + "\n" + (result.UserPrompt ?? "");

            var specMarkdown = ExtractRenderedTableAsSpecMarkdown(prompt);

            var facts = SpecStatementFactsExtractor.Extract(
                new List<(string FileName, string Content)> { ("dbo.P", specMarkdown) });

            var variables = facts.Values.SelectMany(f => f.LocalVariables).ToList();

            Assert.Equal(2, variables.Count);
            Assert.Contains(variables, v => v.Name == "@v_intCLTotal" && v.TypeOrKind == "MONEY");
            Assert.Contains(variables, v => v.Name == "@v_strClientID" && v.TypeOrKind == "VARCHAR(20)");
        }
    }
}
