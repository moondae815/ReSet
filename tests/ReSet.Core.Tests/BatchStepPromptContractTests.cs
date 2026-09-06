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
    /// 붙인다. const 는 IsLiteral 이라 아래 훑기가 실제 배송 텍스트에 닿는다.
    /// </summary>
    public class BatchStepPromptContractTests
    {
        private static string PromptText()
        {
            // 프롬프트 상수는 private이다. 리플렉션으로 전부 이어 붙여 본다 -
            // 이 저장소가 제품 필드를 리플렉션으로 읽는 관례를 따른다.
            var sb = new System.Text.StringBuilder();
            foreach (var f in typeof(AiService).GetFields(
                         BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public))
            {
                if (f.FieldType == typeof(string) && f.IsLiteral)
                    sb.AppendLine((string?)f.GetRawConstantValue() ?? string.Empty);
            }
            return sb.ToString();
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
