using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// mermaid 파스 오류의 <b>원인을 이름으로</b> 말하는 층.
    ///
    /// [왜 필요한가 - 2026-09-10 실측, 캐시 v22 재생성 첫 판]
    /// `UF_GET_INCVTAXRATE` 가 5 시도를 **글자까지 같은 오류**로 태우고 사람이 멈췄다.
    /// 모델이 노드 <b>ID</b> 를 따옴표로 감쌌다:
    /// <code>
    ///     "시작"["함수 호출: UF_GET_INCVTAXRATE(@pi_intCLVTType)"]
    ///     "조건판단"{"@pi_intCLVTType 값 판단"}
    /// </code>
    /// 그런데 L1 이 모델에게 돌려준 것은 mmdc 원문뿐이었다 -
    /// <c>Expecting 'SEMI', 'NEWLINE', … got 'STR'</c>. 모델은 그것으로 「ID 의 따옴표를
    /// 빼라」를 못 읽는다. 다섯 시도가 같았던 것이 그 서명이다.
    ///
    /// 같은 부류를 이미 고친 전례가 있다 - <c>17da79de</c>(「표가 없습니다」→「헤딩 레벨
    /// `####` 로 쓰여 있습니다」). 문구가 진짜 원인을 한 마디도 안 하면 모델은 이미 쓴 것을
    /// 다시 쓰고 헤딩은 동전 던지기로 남는다.
    ///
    /// 이 판의 범위는 코퍼스가 정했다 - 그 판의 mermaid 블록은 <b>정상 ID 0 · 따옴표 ID 40</b>
    /// 이었다(모델 claude-cli · claude-sonnet-5). 지어낸 트리거가 아니다.
    /// </summary>
    public class MermaidNodeIdDiagnosisTests
    {
        [Fact]
        public void WhenTheIdIsQuoted_ShouldNameTheCauseAndTheOffendingIds()
        {
            // Arrange - 2026-09-10 로그 :295 의 실물 모양.
            var mermaid = string.Join("\n",
                "flowchart TD",
                "    \"시작\"[\"함수 호출: UF_GET_INCVTAXRATE(@pi_intCLVTType)\"]",
                "    \"조건판단\"{\"@pi_intCLVTType 값 판단\"}",
                "    \"시작\" --> \"조건판단\"");

            // Act
            var diagnosis = MermaidSyntaxDiagnosis.Explain(mermaid);

            // Assert - 원인을 이름으로 말하고, 어느 자리인지 보여 준다.
            Assert.NotNull(diagnosis);
            Assert.Contains("따옴표", diagnosis!);
            Assert.Contains("시작", diagnosis);
            Assert.Contains("조건판단", diagnosis);
        }

        [Fact]
        public void WhenTheIdIsBare_ShouldStaySilent()
        {
            // Arrange - 계약대로 쓴 모양. 라벨만 따옴표로 감싼다.
            var mermaid = string.Join("\n",
                "flowchart TD",
                "    START[\"함수 호출: UF_GET_INCVTAXRATE(@pi_intCLVTType)\"]",
                "    START --> CHK{\"@pi_intCLVTType 값 판단\"}",
                "    CHK -->|0|R0[\"10.0/100.0 대입\"]");

            Assert.Null(MermaidSyntaxDiagnosis.Explain(mermaid));
        }

        [Fact]
        public void WhenALabelMerelyContainsABracket_ShouldStaySilent()
        {
            // Arrange - 라벨 **안**의 따옴표와 대괄호는 정상이다. 판정은 ID 자리에서만 한다.
            // 이 시험이 없으면 「따옴표 뒤에 대괄호」라는 넓은 그물이 정상 라벨을 문다.
            var mermaid = string.Join("\n",
                "flowchart TD",
                "    A[\"배열 표기 T[0] 을 설명하는 라벨\"] --> B[\"끝\"]");

            Assert.Null(MermaidSyntaxDiagnosis.Explain(mermaid));
        }

        [Fact]
        public void TheDiagnosisMustNotTripTheAuthorInstructionCheck()
        {
            // [작성 계약 - 2026-09-10] 이 문구는 재시도 프롬프트로 돌아가고, 모델이 문서에
            // 옮겨 적을 수 있다. CheckDocumentInstructsItsAuthor 의 트리거를 담으면
            // 「고칠 것이 없는데 재시도가 소진되는」 루프를 새로 연다.
            var mermaid = "flowchart TD\n    \"시작\"[\"x\"]";
            var diagnosis = MermaidSyntaxDiagnosis.Explain(mermaid)!;

            var result = new MechanicalValidator().Validate(
                string.Join("\n", new[]
                {
                    "## 개요", diagnosis, "## 파라미터 목록", "내용",
                    "## CRUD 분석", "내용", "## 로직 흐름 요약", "내용",
                    "## 비즈니스 흐름 시각화", "```mermaid", "flowchart TD", "A[\"시\"] --> B[\"끝\"]", "```"
                }));

            Assert.DoesNotContain(
                result.DetailedErrors, e => e.Type == ErrorType.DocumentInstructsItsAuthor);
        }
    }
}
