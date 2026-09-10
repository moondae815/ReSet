using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 기계 확정 「오류 코드」 표에 <b>같은 코드를 쓰는 문장</b>이 있으면, 표를 건네는
    /// 자리가 산문에서 그 코드를 뭐라 부를지까지 말하는지 본다.
    ///
    /// [왜 L1 만으로는 안 닫히는가 - 2026-09-09 실측]
    /// <c>UP_UTIL_SETTLE_EXCEPTION_PROC</c> 의 표는 18 행 중 14 개 코드가 실제로 서로
    /// 다르고 <c>-1</c>·<c>-2</c> 둘만 겹친다. 모델이 보기에 「문장별 고유 음수 코드」는
    /// <b>거의</b> 참이라, <c>CheckErrorCodeUniquenessClaim</c> 이 그 낱말을 지목하고
    /// 무엇을 쓸지까지 명령형으로 줘도 다음 시도에서 같은 문장을 거의 그대로 다시 썼다:
    ///   시도 1 - 2 자리 (「고유한 음수 코드를 설정하고」·「고유 음수 코드 설정 →」)
    ///   시도 2 - 3 자리 (같은 둘 + 「문장별 고유 음수 코드가 명시적으로 대입되며」)
    /// <b>줄었어야 할 수가 늘었다.</b> 매 시도가 문서를 통째로 새로 쓰는데, 표를 건네는
    /// 자리가 「축자 복사하라」만 말하고 산문 표현은 아무 말도 안 했기 때문이다.
    ///
    /// 그래서 규칙 층에서 먼저 막고 L1 은 뒤에 남긴다 - F1′ 를 닫을 때와 같은 짝이다.
    /// 두 층이 같은 기계 확정 재료(이 표의 중복)에서 문장을 만들므로 서로 어긋날 수 없다.
    ///
    /// [이 시험이 증명하지 못하는 것] 프롬프트에 그 줄이 실린다는 것뿐이다. <b>모델이
    /// 실제로 따를지는 증명하지 못한다</b> - 그건 다음 재생성의 시도 1 발화가 0 인지로만
    /// 드러난다.
    /// </summary>
    public class DuplicateErrorCodePromptTests
    {
        private static async Task<string> BuildRulesAsync(SpDefinition spDef)
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 명세서\"}}]}";
            var client = new OpenAiClient(
                new HttpClient(new MockHttpMessageHandler(mockResponse)),
                "k", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateSpecificationAsync(spDef, "rules");
            return result.SystemPrompt!;
        }

        /// <summary>
        /// 실물과 같은 모양이다 - 코드 넷 중 <b>둘만</b> 겹치고 나머지는 서로 다르다.
        /// 전부 겹치는 픽스처를 쓰면 「거의 참이라 모델이 못 버린다」는 실물의 성질이
        /// 사라져, 이 검사가 무엇을 막는지가 시험에서 지워진다.
        /// </summary>
        private static SpDefinition DuplicateErrorCodeSpDefinition(bool duplicated)
        {
            var third = duplicated ? "-1" : "-3";
            var spDef = new SpDefinition
            {
                ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UP_UTIL_DUP_CODE", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "UP_UTIL_DUP_CODE",
                ObjectType = CodeObjectType.Procedure,
                DdlText = @"
CREATE PROCEDURE dbo.UP_UTIL_DUP_CODE
    @pi_strYMD CHAR(8),
    @po_intRetVal INT OUTPUT
AS
BEGIN
    BEGIN TRAN

    UPDATE dbo.TSettleMst SET ProcState = 1 WHERE YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN ROLLBACK TRAN SET @po_intRetVal = -1 RETURN END

    UPDATE dbo.TSettleMst SET ProcState = 2 WHERE YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN ROLLBACK TRAN SET @po_intRetVal = -2 RETURN END

    UPDATE dbo.TSettleMst SET ProcState = 3 WHERE YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN ROLLBACK TRAN SET @po_intRetVal = " + third + @" RETURN END

    UPDATE dbo.TSettleMst SET ProcState = 4 WHERE YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN ROLLBACK TRAN SET @po_intRetVal = -4 RETURN END

    COMMIT TRAN
END"
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ProcedureParameters = new List<string> { "@pi_strYMD", "@po_intRetVal" }
            };
            return spDef;
        }

        [Fact]
        public async Task WhenTheTableHasDuplicates_ThePromptShouldSayWhatToCallThem()
        {
            var rules = await BuildRulesAsync(DuplicateErrorCodeSpDefinition(duplicated: true));

            var notice = Assert.Single(
                rules.Split('\n'), l => l.Contains("[DUPLICATE CODES IN THIS TABLE]"));
            var proseRule = Assert.Single(
                rules.Split('\n'),
                l => l.Contains(MechanicalValidator.PromptInstructionMarker)
                     && l.Contains("「고유」"));

            // [사실은 공지 줄에] 어느 문장이 무엇을 공유하는지 - 모델이 그대로 옮겨 적을
            // 재료다. 이 줄은 옮겨 적혀도 문서 내용으로 성립한다.
            Assert.Contains("UPDATE 1", notice);
            Assert.Contains("UPDATE 3", notice);
            Assert.Contains("실패 지점을 특정할 수 없습니다", notice);

            // [지시는 표지 줄에] 금지할 낱말과, 「거의 다 다른데 왜?」를 미리 막는 문장.
            Assert.Contains("「고유」", proseRule);
            Assert.Contains("「서로 다른」", proseRule);
            Assert.Contains("most of the codes really are distinct", proseRule);

            // ★ [2026-09-10 - 이 시험의 본체] 공지 줄에 지시가 섞이면 안 된다.
            // 섞여 있던 종전 판이 배송본 둘을 만들었다 - 1판 :579 · 2판 :720. 2판은 L1을
            // 통과해 배송됐고, 남은 결함은 「납품 문서가 자기 작성자에게 지시한다」였다.
            // 표지 없는 지시가 축자 복사 블록 안에 있으면 모델이 옮겨 적는다.
            Assert.DoesNotContain("마십시오", notice);
            Assert.DoesNotContain("서술하십시오", notice);
            Assert.DoesNotContain("「고유」", notice);
        }

        [Fact]
        public async Task WhenEveryCodeIsDistinct_ThePromptShouldStaySilent()
        {
            var rules = await BuildRulesAsync(DuplicateErrorCodeSpDefinition(duplicated: false));

            // 중복이 없는 객체의 프롬프트는 바이트가 불변이어야 한다 - 캐시 파급이
            // 코퍼스 31 편 중 2 편에 머무는 근거가 이것이다.
            Assert.DoesNotContain("[DUPLICATE CODES IN THIS TABLE]", rules);
        }

        // 규칙 층과 L1 이 <b>같은 재료</b>에서 문장을 만드는지 본다. 오라클이 갈리면
        // 모델은 둘 중 하나를 버린다 - 그때 어느 쪽을 버릴지는 우리가 못 정한다.
        [Fact]
        public async Task ThePromptAndL1ShouldNameTheSameSharedCodes()
        {
            var rules = await BuildRulesAsync(DuplicateErrorCodeSpDefinition(duplicated: true));
            var notice = rules.Split('\n').Single(l => l.Contains("[DUPLICATE CODES IN THIS TABLE]"));

            Assert.Contains("-1→UPDATE 1·UPDATE 3", notice);
        }
    }
}
