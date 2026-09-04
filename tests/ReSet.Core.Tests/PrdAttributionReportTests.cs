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
        public void BuildBanner_InvalidBranch_AlwaysIncludesUnverifiedDisclosure()
        {
            // 결함이 있는 배너에서 미검증 공개 문단을 빼면 나중에 누군가 그 문단을 검증 성공 분기로
            // 옮길 수 있고 모든 테스트가 초록색을 유지한다. 그것이 이 기능이 막으려는 정확한 실패다.
            var banner = PrdAttributionReport.BuildBanner(ResultWith(new PrdDefect(
                PrdDefectType.EvidenceQuoteNotFound,
                "## 데이터 요구사항",
                "REQ-DATA-01",
                "인용 구절을 찾을 수 없습니다.")));

            Assert.Contains("미검증", banner);
            Assert.Contains("추정", banner);
        }

        [Fact]
        public void BuildBanner_RequirementIdWithMarkdownCharacters_IsNeutralized()
        {
            // RequirementId는 모델이 작성하므로 마크다운 문법을 포함할 수 있다.
            // 백틱과 별표로 스타일을 주입하려는 시도가 무효화되어야 한다.
            var crafted = "REQ-1` **정상**";
            var banner = PrdAttributionReport.BuildBanner(ResultWith(new PrdDefect(
                PrdDefectType.EvidenceQuoteNotFound,
                "## 데이터 요구사항",
                crafted,
                "인용 구절을 찾을 수 없습니다.")));

            // CAUTION 헤더와 미검증 공개 문단은 유지되어야 한다.
            Assert.Contains("CAUTION", banner);
            Assert.Contains("미검증", banner);

            // 하지만 원본 백틱과 별표는 마크다운으로 해석되지 않아야 한다.
            // 마크다운에서 렌더링되지 않으려면 대체 문자를 사용했어야 한다.
            Assert.DoesNotContain(crafted, banner);  // 원본 문자열은 없어야 한다
            Assert.Contains("´", banner);  // 백틱이 아포스트로피로 바뀌어야 한다
            Assert.Contains("·", banner);  // 별표가 중점으로 바뀌어야 한다
        }

        [Fact]
        public void BuildBanner_MessageWithMarkdownLink_DoesNotRenderAsLink()
        {
            // Message는 검증기가 구성하며, 요구자가 작성한 인용 구절을 포함할 수 있다.
            // 그 인용이 마크다운 링크 구문을 포함하면 배너에서 링크로 렌더링되면 안 된다.
            // 제거: SafeForMarkdownBullet에서 [ 와 ] 대체를 제거하면 이 테스트는 실패한다.
            var banner = PrdAttributionReport.BuildBanner(ResultWith(new PrdDefect(
                PrdDefectType.EvidenceQuoteNotFound,
                "## 데이터 요구사항",
                "REQ-DATA-01",
                "근거 칸의 인용: '[정상 확인됨](https://example.invalid)'")));

            // CAUTION 헤더와 미검증 공개 문단은 유지되어야 한다.
            Assert.Contains("CAUTION", banner);
            Assert.Contains("미검증", banner);

            // 링크 구문이 무효화되어야 한다. 원본 대괄호는 없어야 한다.
            Assert.DoesNotContain("[정상", banner);
            Assert.DoesNotContain("](https", banner);
            // 대괄호가 수학 기호로 대체되어야 한다.
            Assert.Contains("⟦정상 확인됨⟧", banner);
        }

        [Fact]
        public void BuildBanner_MessageWithAutolinkOrInlineHtml_DoesNotRender()
        {
            // Message에 자동 링크 <url> 이나 인라인 HTML <tag> 가 포함되면 렌더링되면 안 된다.
            // 제거: SafeForMarkdownBullet에서 < 대체를 제거하면 이 테스트는 실패한다.
            var banner = PrdAttributionReport.BuildBanner(ResultWith(new PrdDefect(
                PrdDefectType.EvidenceQuoteNotFound,
                "## 데이터 요구사항",
                "REQ-DATA-01",
                "근거 칸의 HTML: '<b>정상 확인됨</b>' 또는 <https://example.invalid>")));

            // CAUTION 헤더와 미검증 공개 문단은 유지되어야 한다.
            Assert.Contains("CAUTION", banner);
            Assert.Contains("미검증", banner);

            // HTML 및 자동 링크 구문이 무효화되어야 한다.
            Assert.DoesNotContain("<b>", banner);
            Assert.DoesNotContain("</b>", banner);
            Assert.DoesNotContain("<https://example.invalid>", banner);
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
