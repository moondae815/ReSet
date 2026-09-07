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

        /// <summary>
        /// 규칙 5-1 과 Few-Shot 블록 안 주석이 **거짓 절대 명제**로 되돌아가지 않게
        /// 잠근다.
        ///
        /// [무엇이 거짓이었나] 「the binding list carries the original procedure's
        /// parameters only」. 같은 프롬프트 문자열 안의 Few-Shot 이 그것을 반증한다 -
        /// `SQL_CREATE_AND_CAPTURE_SHADOW` 는 `p_runId` 를, `SQL_INSERT_CHUNK` 와
        /// `SQL_COPY_CHUNK` 는 `p_from`·`p_to` 를 바인딩하는데 셋 다 원본 프로시저의
        /// 파라미터가 아니다. `runId` 는 **규칙 5 가 「입력에 더하지 마라」고 해서**
        /// 제어 테이블에서 읽는 값이고, `from`/`to` 는 규칙 8-1 의 청크 경계다.
        /// 모델이 그 명제를 문자 그대로 따르면 규칙 4(c)·8-1 과 정면으로 부딪힌다.
        ///
        /// [왜 여기에 또 잠그는가 - 재감염 경로] `CriticCriteriaCoverageTests` 의
        /// `DoesNotContain` 은 **Critic 프롬프트만** 본다. 생성 프롬프트 쪽 두 자리는
        /// 무주공산이었다 - 옛 명제가 돌아와도 조용하다. 그리고 그 둘은 **언어가
        /// 다르다**(규칙은 영문, Few-Shot 블록 안 주석은 한글). 하나만 잠그면 다른
        /// 쪽이 조용히 돌아오므로 넷을 따로 건다.
        ///
        /// [왜 Contains 도 함께 거는가] `DoesNotContain` 만 있으면 문장을 **통째로
        /// 지워도** 초록이다. 없앴을 때 깨지는 쪽이 있어야 검사가 효력을 가진다.
        /// </summary>
        [Fact]
        public void BatchStepPrompt_ScopesTheBindingListClaimInsteadOfClaimingParametersOnly()
        {
            var text = PromptText();

            // (a) 옛 절대 명제가 규칙 5-1 로 돌아오지 않는다 - 영문 자리.
            Assert.DoesNotContain("the original procedure's parameters only", text);
            // (b) 옛 절대 명제가 Few-Shot 블록 안 주석으로 돌아오지 않는다 - 한글 자리.
            //     생성물은 주석이 아니라 예시를 베끼므로 이 자리가 가장 멀리 퍼진다.
            Assert.DoesNotContain("바인딩 목록에는 원본 프로시저의 파라미터만 들어간다", text);

            // (c) 규칙 5-1 이 관할을 밝힌 문장을 싣는다 - 지우면 여기서 깨진다.
            Assert.Contains(
                "the binding list carries the step's own parameters and the values the orchestration itself supplies",
                text);
            // (d) Few-Shot 블록 안 주석도 관할이 붙은 문장이다 - 지우면 여기서 깨진다.
            Assert.Contains("원본이 초기값과 함께 선언한 상수는 바인딩 목록에 넣지 않는다", text);
        }
    }
}
