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
    }
}
