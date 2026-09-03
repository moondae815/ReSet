using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdAttributionReportTests
    {
        private static PrdValidationResult ResultWith(params PrdDefect[] defects) =>
            new(new List<PrdDefect>(defects));

        [Fact]
        public void BuildBanner_ShouldStateTheUncheckedGap_EvenWhenThereAreNoDefects()
        {
            // 결함이 없다고 해서 「요구와 근거의 대응」이 검증된 것이 아니다.
            // 그 사실을 숨기면 독자가 검사를 실제보다 강하게 믿는다.
            var banner = PrdAttributionReport.BuildBanner(ResultWith());

            Assert.Contains("실재", banner);
            Assert.Contains("미검증", banner);
            Assert.Contains("추정", banner);
        }

        [Fact]
        public void BuildBanner_ShouldListDefects_WhenAttributionFailed()
        {
            var banner = PrdAttributionReport.BuildBanner(ResultWith(new PrdDefect(
                PrdDefectType.EvidenceQuoteNotFound,
                "## 데이터 요구사항",
                "REQ-DATA-01",
                "인용 구절을 찾을 수 없습니다.")));

            Assert.Contains("REQ-DATA-01", banner);
            Assert.Contains("CAUTION", banner);
        }

        [Fact]
        public void BuildPromptFix_ShouldNameEveryDefectiveRequirement()
        {
            var fix = PrdAttributionReport.BuildPromptFix(ResultWith(
                new PrdDefect(PrdDefectType.ConfidenceVocabulary, "## 데이터 요구사항", "REQ-DATA-02", "확신도 어휘 위반"),
                new PrdDefect(PrdDefectType.SectionMissing, "## 기능 요구사항", string.Empty, "섹션 없음")));

            Assert.Contains("REQ-DATA-02", fix);
            Assert.Contains("## 기능 요구사항", fix);
        }

        [Fact]
        public void BuildPromptFix_ShouldBeEmpty_WhenValid()
        {
            Assert.Equal(string.Empty, PrdAttributionReport.BuildPromptFix(ResultWith()));
        }
    }
}
