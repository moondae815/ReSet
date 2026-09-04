using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 도입 스윕(2026-09-04)이 실제로 잡아낸 거짓 고발을 원문 그대로 고정한다.
    ///
    /// [왜 합성 픽스처로 갈음하지 않는가] 이 결함의 실물 증거는 코퍼스에 있던
    /// `dbo.UP_UTIL_SETTLE_COMM_UPD` 의 `Prd.md` 한 편이었는데, 배너가 거짓이라
    /// 그 파일은 재생성으로 덮였다. 형태만 본뜬 픽스처는 "이 입력에서 여섯 번
    /// 거짓 고발했다"를 증언하지 못한다 - 다음 사람이 원장 텍스트를 믿는 수밖에
    /// 없어진다. 그래서 발화를 냈던 세 행과 그 인용이 실재하던 명세서 세 줄을
    /// **글자 그대로** 옮겨 실행 가능한 앵커로 만든다.
    ///
    /// 명세서 줄의 출처: `Spec.md` 111·112행(`## 파라미터 목록`), 689행(`## 로직 흐름 요약`).
    /// </summary>
    public class PrdPipeInQuoteCorpusRegressionTests
    {
        // 발화를 냈던 세 행. 인용에 표 파이프가 그대로 들어 있다.
        private const string RealRowIn01 =
            @"| REQ-IN-01 | 본 기능은 실행 시 정산 기준일(YYYYMMDD, 8자리)을 입력 조건으로 반드시 전달받아야 한다. | ## 파라미터 목록 > ""@pi_strYMD | CHAR(8) | 입력 | 명시 없음 | 정산 기준일 (YYYYMMDD)"" | 도출 |";

        private const string RealRowIn02 =
            @"| REQ-IN-02 | 본 기능은 처리 결과를 0=성공, 음수=실패(오류 코드별 상이)로 구분하는 출력 값을 제공해야 한다. | ## 파라미터 목록 > ""@po_intRetVal | INT | 출력 | 명시 없음 | 반환값: 0=성공, 음수=실패 (오류 코드별 상이). 호출자는 사전 0 초기화 권장"" | 도출 |";

        private const string RealRowNfr02 =
            @"| REQ-NFR-02 | 처리 단계별로 서로 다른 오류 코드를 반환하여 실패 지점을 식별할 수 있어야 한다. | ## 로직 흐름 요약 > ""UPDATE 1 | -1 | @po_intRetVal"" | 도출 |";

        // 세 인용이 실재하던 명세서 줄. 이것이 이 검사의 기준값이다.
        private const string RealSpec = @"## 개요

정산 수수료를 갱신한다.

## 파라미터 목록

| 이름 | 타입 | 구분 | 기본값 | 설명 |
| :--- | :--- | :--- | :--- | :--- |
| @pi_strYMD | CHAR(8) | 입력 | 명시 없음 | 정산 기준일 (YYYYMMDD) |
| @po_intRetVal | INT | 출력 | 명시 없음 | 반환값: 0=성공, 음수=실패 (오류 코드별 상이). 호출자는 사전 0 초기화 권장 |

## CRUD 분석

TSettleMst 를 갱신한다.

## 로직 흐름 요약

| 단계 | 오류코드 | 반환 |
| :--- | :--- | :--- |
| UPDATE 1 | -1 | @po_intRetVal |
";

        private static string PrdWithRealRows() =>
            "## 배경 및 목적\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-BG-01 | 정산 수수료를 갱신한다 | ## 개요 > \"정산 수수료를 갱신한다\" | 도출 |\n\n"
            + "## 수행 조건 및 입력 계약\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + RealRowIn01 + "\n" + RealRowIn02 + "\n\n"
            + "## 데이터 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-DATA-01 | 정산 마스터를 갱신한다 | ## CRUD 분석 > \"TSettleMst 를 갱신한다\" | 도출 |\n\n"
            + "## 기능 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-FUNC-01 | 단계별로 갱신한다 | ## 로직 흐름 요약 > \"UPDATE 1\" | 도출 |\n\n"
            + "## 예외 및 비기능 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + RealRowNfr02 + "\n";

        [Fact]
        public void Validate_TheThreeRowsThatWereFalselyAccused_ShouldStayClean()
        {
            // 되돌리면 이 한 건이 결함 6건을 낸다 - 세 행 각각이 확신도 어휘와
            // 근거 형식을 동시에 고발당한다. 실제 배너에 박혔던 수 그대로다.
            var result = PrdAttributionValidator.Validate(PrdWithRealRows(), RealSpec);

            Assert.Empty(result.Defects);
        }

        [Fact]
        public void Parse_TheThreeRowsThatWereFalselyAccused_ShouldKeepTheirConfidenceAndEvidence()
        {
            // 거짓 고발의 정체를 못박는다: 확신도 칸에 들어갔던 'CHAR(8)'·'INT'·'-1' 은
            // 확신도가 아니라 **인용 안의 두 번째 칸**이었다.
            var rows = PrdDocumentParser.Parse(PrdWithRealRows());

            var in01 = Assert.Single(rows, r => r.Id == "REQ-IN-01");
            Assert.Equal("도출", in01.Confidence);
            Assert.Contains("@pi_strYMD | CHAR(8)", in01.EvidenceRaw);

            var in02 = Assert.Single(rows, r => r.Id == "REQ-IN-02");
            Assert.Equal("도출", in02.Confidence);
            Assert.Contains("@po_intRetVal | INT", in02.EvidenceRaw);

            var nfr02 = Assert.Single(rows, r => r.Id == "REQ-NFR-02");
            Assert.Equal("도출", nfr02.Confidence);
            Assert.Contains("UPDATE 1 | -1", nfr02.EvidenceRaw);
        }
    }
}
