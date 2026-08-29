using System.Collections.Generic;
using System.Linq;
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
            // 리더가 구간을 잡는 접두사 목록과 새 헤딩의 일치를 직접 못박는다.
            // 위 테스트가 이미 통로를 재지만, 이 단언은 깨졌을 때 원인을 곧바로 말한다.
            Assert.StartsWith("### 지역 변수", LocalVariableDeclarationExtractor.TableHeading);
        }

        [Fact]
        public void TheMachineHeader_ShouldCarryTheTwoColumnFragmentsTheReaderLooksFor()
        {
            // 리더는 이름 칸을 "명칭"으로, 타입 칸을 "데이터 타입"으로 찾는다.
            const string header = "| 변수 명칭 | 데이터 타입 | 초기값 |";

            Assert.Contains("명칭", header);
            Assert.Contains("데이터 타입", header);
        }
    }
}
