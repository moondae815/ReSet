using System.Reflection;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 배치 스텝 프롬프트의 규칙 5-1 과 그 Few-Shot 예시가 조용히 사라지지 않게
    /// 잠근다.
    ///
    /// [왜 예시까지 잠그는가] 실측: 프롬프트 주석만 고쳤을 때 배송본이 안 닫혔다 -
    /// 생성물은 **예시를 베끼지 주석을 베끼지 않는다.** 규칙만 잠그면 예시가
    /// 사라져도 초록이다.
    ///
    /// [리플렉션이 무엇에 닿는가] 규칙 목록과 Few-Shot 절은 둘 다
    /// AiService 의 private const 인 ConsolidatedPlanRules 안에 있고,
    /// GenerateBatchStepSectionAsync 가 그것을 시스템 프롬프트 꼬리에 그대로
    /// 붙인다. 그래서 아래 PromptText()는 그 필드 **하나만** 읽는다.
    /// </summary>
    public class BatchStepPromptContractTests
    {
        /// <summary>
        /// 배치 스텝 프롬프트가 실제로 싣는 텍스트 하나만 읽는다.
        ///
        /// [왜 좁혔나] 처음에는 AiService의 const 문자열을 전부 이어 붙였다. 그러면
        /// 규칙 5-1이 배치 스텝 프롬프트가 **아닌** 다른 const로 옮겨져도 초록이다 -
        /// 통과하지만 아무것도 안 잠그는 검사가 된다. 실측으로 확인했다: 규칙을 다른
        /// const로 옮긴 뮤턴트에서 넓은 판은 초록, 이 좁은 판은 빨강이었다.
        ///
        /// 필드를 못 찾으면 **실패한다.** 조용히 빈 문자열을 돌려주면 이름이 바뀐
        /// 순간 세 테스트가 전부 초록인 채로 아무것도 안 잠근다.
        /// </summary>
        private static string PromptText()
        {
            var field = typeof(AiService).GetField(
                "ConsolidatedPlanRules",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.True(
                field is not null,
                "AiService.ConsolidatedPlanRules 를 찾지 못했습니다. 이름이 바뀌었다면 " +
                "이 테스트가 잠그는 대상도 같이 옮겨졌는지 확인하십시오.");

            var text = field!.GetValue(null) as string;

            Assert.False(
                string.IsNullOrWhiteSpace(text),
                "AiService.ConsolidatedPlanRules 가 비어 있습니다.");

            return text!;
        }

        [Fact]
        public void BatchStepPrompt_CarriesLocalVariableTypeContractRule()
        {
            var text = PromptText();

            Assert.Contains("Local Variable Type Contract", text);
            Assert.Contains("rule 5-1", text);
        }

        [Fact]
        public void BatchStepPrompt_ShowsLegacyLocalPreservedAsDeclare()
        {
            // 예시의 DECLARE 는 신설 인프라 변수(@v_shadow·@v_sql)가 아니라
            // **레거시 지역 변수 보존**의 본이어야 한다.
            var text = PromptText();

            Assert.Contains("DECLARE @v_someRate DECIMAL(5,2) = 1.25;", text);
        }

        [Fact]
        public void BatchStepPrompt_DoesNotShipCorpusLiteralsInTheExample()
        {
            // 실물 이름·값을 예시에 쓰면 생성물이 그 리터럴을 아무 데나 베낀다.
            var text = PromptText();

            Assert.DoesNotContain("DECLARE @v_valIncVat", text);
        }
    }
}
