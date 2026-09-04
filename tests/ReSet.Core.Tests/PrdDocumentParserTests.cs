using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdDocumentParserTests
    {
        private const string TwoSectionPrd = @"## 배경 및 목적

| ID | 요구사항 | 근거 | 확신도 |
| :--- | :--- | :--- | :--- |
| REQ-BG-01 | 일별 정산 마감을 자동화한다 | ## 개요 > ""일별 정산 마감"" | 도출 |

## 데이터 요구사항

| ID | 요구사항 | 근거 | 확신도 |
| :--- | :--- | :--- | :--- |
| REQ-DATA-01 | 미집계 건을 적재한다 | ## CRUD 분석 > ""TB_SETTLE_DAILY에 INSERT"" | 도출 |
| REQ-DATA-02 | 중복 적재를 막는다 | ## CRUD 분석 > ""중복 검사"" | 추정 |
";

        [Fact]
        public void Parse_ShouldReadEveryRequirementRowWithItsSection()
        {
            var requirements = PrdDocumentParser.Parse(TwoSectionPrd);

            Assert.Equal(3, requirements.Count);
            Assert.Equal("## 배경 및 목적", requirements[0].Section);
            Assert.Equal("REQ-BG-01", requirements[0].Id);
            Assert.Equal("## 개요 > \"일별 정산 마감\"", requirements[0].EvidenceRaw);
            Assert.Equal("도출", requirements[0].Confidence);
            Assert.Equal("## 데이터 요구사항", requirements[2].Section);
            Assert.Equal("추정", requirements[2].Confidence);
        }

        [Fact]
        public void Parse_ShouldSkipHeaderAndSeparatorRows()
        {
            var requirements = PrdDocumentParser.Parse(TwoSectionPrd);

            Assert.DoesNotContain(requirements, r => r.Id == "ID");
            Assert.DoesNotContain(requirements, r => r.Id.StartsWith(":---"));
        }

        [Fact]
        public void Parse_ShouldIgnoreTableRowsInsideCodeFence()
        {
            // 생성 모델이 예시를 코드 펜스로 감싸는 일이 잦다. 그것을 요구로 세면
            // 검사가 존재하지 않는 항목을 고발한다.
            const string withFence = @"## 데이터 요구사항

            ```markdown
            | REQ-DATA-99 | 예시일 뿐이다 | ## CRUD 분석 > ""예시"" | 도출 |
            ```

| ID | 요구사항 | 근거 | 확신도 |
| :--- | :--- | :--- | :--- |
| REQ-DATA-01 | 진짜 요구 | ## CRUD 분석 > ""INSERT"" | 도출 |
";

            var requirements = PrdDocumentParser.Parse(withFence);

            Assert.Single(requirements);
            Assert.Equal("REQ-DATA-01", requirements[0].Id);
        }

        [Fact]
        public void Parse_ShouldKeepEscapedPipeInsideTheEvidenceCell()
        {
            // 렌더 관행(MarkdownTableCellCodec.Escape)대로 셀 안 파이프를 `\|`로 적은 행.
            // 날것 Split('|')로 나누면 이 행이 여섯 칸으로 터진다.
            const string prd = @"## 수행 조건 및 입력 계약

| ID | 요구사항 | 근거 | 확신도 |
| :--- | :--- | :--- | :--- |
| REQ-IN-01 | 정산 기준일을 받는다 | ## 파라미터 목록 > ""@pi_strYMD \| CHAR(8) \| 입력"" | 도출 |
";

            var requirements = PrdDocumentParser.Parse(prd);

            var row = Assert.Single(requirements);
            Assert.Equal("REQ-IN-01", row.Id);
            Assert.Equal("## 파라미터 목록 > \"@pi_strYMD | CHAR(8) | 입력\"", row.EvidenceRaw);
            Assert.Equal("도출", row.Confidence);
        }

        [Fact]
        public void Parse_ShouldRecoverTheRow_WhenTheExcerptCarriesUnescapedPipes()
        {
            // 도입 스윕(2026-09-04) 실측. 프롬프트가 "verbatim 인용"을 요구하고 Spec의
            // 알찬 사실이 표 안에 있으므로, 모델은 지시를 지키다가 표 파이프를 그대로
            // 옮긴다. 날것 분해는 이 행을 여덟 칸으로 터뜨려 근거 칸에 조각만, 확신도
            // 칸에 `CHAR(8)`을 넣는다 - 그러면 검사가 "확신도가 도출이 아니다"라는
            // **거짓 진단**을 내고, 그 거짓이 교정 프롬프트와 사람용 배너 양쪽에 실린다.
            const string prd = @"## 수행 조건 및 입력 계약

| ID | 요구사항 | 근거 | 확신도 |
| :--- | :--- | :--- | :--- |
| REQ-IN-01 | 정산 기준일을 받는다 | ## 파라미터 목록 > ""@pi_strYMD | CHAR(8) | 입력 | 명시 없음"" | 도출 |
";

            var requirements = PrdDocumentParser.Parse(prd);

            var row = Assert.Single(requirements);
            Assert.Equal("REQ-IN-01", row.Id);
            Assert.Equal("정산 기준일을 받는다", row.Text);
            Assert.Equal("## 파라미터 목록 > \"@pi_strYMD | CHAR(8) | 입력 | 명시 없음\"", row.EvidenceRaw);
            Assert.Equal("도출", row.Confidence);
        }

        [Fact]
        public void Parse_ShouldNotReassemble_WhenNoCellOpensTheEvidenceGrammar()
        {
            // 되살리기는 근거 칸 문법(`## 헤딩 > "구절"`)을 읽어서만 한다. 그 문법이
            // 아예 없으면 손대지 않고 원래대로 고발되게 둔다 - 어긋난 행을 조용히
            // 그럴듯하게 만들면 검사가 문서 결함을 숨기게 된다.
            const string prd = @"## 데이터 요구사항

| ID | 요구사항 | 근거 | 확신도 |
| :--- | :--- | :--- | :--- |
| REQ-DATA-01 | 값에 파이프가 있다 | 근거 없음 | 도출 |
";

            var requirements = PrdDocumentParser.Parse(prd);

            var row = Assert.Single(requirements);
            Assert.Equal("근거 없음", row.EvidenceRaw);
            Assert.Equal("도출", row.Confidence);
        }

    }
}
