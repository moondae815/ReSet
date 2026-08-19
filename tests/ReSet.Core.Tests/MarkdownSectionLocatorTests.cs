using System;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class MarkdownSectionLocatorTests
    {
        [Fact]
        public void SplitLines_ShouldNormalizeCrLf()
        {
            var lines = MarkdownSectionLocator.SplitLines("a\r\nb\nc");

            Assert.Equal(new[] { "a", "b", "c" }, lines);
        }

        [Fact]
        public void SplitLines_ShouldReturnSingleEmptyLine_ForNull()
        {
            var lines = MarkdownSectionLocator.SplitLines(null);

            Assert.Single(lines);
            Assert.Equal(string.Empty, lines[0]);
        }

        [Fact]
        public void FindIndexOutsideFence_ShouldIgnoreHeadingInsideCodeFence()
        {
            // 계획서의 공통 규약에는 SQL 블록이 실린다. 그 안의 "## "를 헤딩으로 읽으면
            // 섹션 경계가 코드 한복판에서 끊긴다.
            var lines = MarkdownSectionLocator.SplitLines(
                "본문\n```sql\n-- ## 가짜 헤딩\n```\n## 진짜 헤딩");

            var index = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, l => l.TrimStart().StartsWith("## ", StringComparison.Ordinal));

            Assert.Equal(4, index);
        }

        [Fact]
        public void FindIndexOutsideFence_ShouldRescan_WhenFenceNeverCloses()
        {
            // 모델이 닫는 펜스를 빠뜨리면 이후 전체가 "펜스 안"이 되어 미탐이 난다.
            // 미탐(문서 전체 삼킴)이 오탐(코드 안의 헤딩)보다 훨씬 나쁘므로 재스캔한다.
            var lines = MarkdownSectionLocator.SplitLines("```sql\nSELECT 1\n## 헤딩");

            var index = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, l => l.TrimStart().StartsWith("## ", StringComparison.Ordinal));

            Assert.Equal(2, index);
        }

        [Fact]
        public void FindIndexOutsideFence_ShouldReturnMinusOne_WhenNoMatch()
        {
            var lines = MarkdownSectionLocator.SplitLines("본문만 있다");

            var index = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, l => l.TrimStart().StartsWith("## ", StringComparison.Ordinal));

            Assert.Equal(-1, index);
        }

        [Fact]
        public void LocateSection_ShouldReturnHeaderAndNextBoundary()
        {
            var lines = MarkdownSectionLocator.SplitLines(
                "## 첫째\n내용1\n## 둘째\n내용2");

            var (header, end) = MarkdownSectionLocator.LocateSection(lines, "## 첫째", "## ");

            Assert.Equal(0, header);
            Assert.Equal(2, end);
        }

        [Fact]
        public void LocateSection_ShouldEndAtDocumentEnd_WhenNoNextBoundary()
        {
            var lines = MarkdownSectionLocator.SplitLines("## 유일\n내용");

            var (header, end) = MarkdownSectionLocator.LocateSection(lines, "## 유일", "## ");

            Assert.Equal(0, header);
            Assert.Equal(2, end);
        }

        [Fact]
        public void LocateSection_ShouldReturnMinusOnePair_WhenHeadingMissing()
        {
            var lines = MarkdownSectionLocator.SplitLines("## 다른 것\n내용");

            var (header, end) = MarkdownSectionLocator.LocateSection(lines, "## 없는 헤딩", "## ");

            Assert.Equal(-1, header);
            Assert.Equal(-1, end);
        }

        [Fact]
        public void LocateSection_ShouldNotTreatH3AsBoundary()
        {
            // "### "는 인덱스 2가 '#'이라 StartsWith("## ")에 걸리지 않는다.
            // 이 성질이 깨지면 단계 헤딩이 H2 블록의 끝으로 오인된다.
            var lines = MarkdownSectionLocator.SplitLines("## 상위\n### 하위\n내용");

            var (_, end) = MarkdownSectionLocator.LocateSection(lines, "## 상위", "## ");

            Assert.Equal(3, end);
        }

        // === 느슨 매칭 =======================================================
        //
        // 실측(POQSettleProc17·18 연속): 골격이 H2에 콜론을 붙여
        // `## 단계별 이행 상세 및 의사코드:`로 썼는데, 정확 일치 탐색이 못 찾아
        // BatchPlanAssembler가 문서 끝에 같은 H2를 새로 합성했다. 계획서에 같은 H2가
        // 둘이 되고 공통 규약 절이 단계 본문과 갈라졌다. MechanicalValidator는
        // Contains로 보므로 이 문서를 통과시켰다 - 두 판정 기준이 갈린 자리다.

        [Fact]
        public void LocateSection_WhenNotExact_ShouldFindAHeadingWithATrailingColon()
        {
            var lines = MarkdownSectionLocator.SplitLines(
                "# 계획서\n## 단계별 이행 상세 및 의사코드:\n본문\n## 다음");

            var (header, end) = MarkdownSectionLocator.LocateSection(
                lines, "## 단계별 이행 상세 및 의사코드", "## ", exact: false);

            Assert.Equal(1, header);
            Assert.Equal(3, end);
        }

        // 접두가 붙은 형태도 같은 헤딩이다 - MechanicalValidator가 CRUD 절에서
        // 손으로 쓰던 폴백이 보던 것과 같은 모양이다.
        [Fact]
        public void LocateSection_WhenNotExact_ShouldFindAHeadingWithANumberedPrefix()
        {
            var lines = MarkdownSectionLocator.SplitLines("## 3. CRUD 분석\n본문");

            var (header, _) = MarkdownSectionLocator.LocateSection(
                lines, "## CRUD 분석", "## ", exact: false);

            Assert.Equal(0, header);
        }

        // 기본값은 종전 그대로여야 한다. 넓히면서 기존 소비자의 판정을 함께 바꾸면
        // 이 변경이 닿을 의도가 없던 자리까지 움직인다.
        [Fact]
        public void LocateSection_ByDefault_ShouldStillRequireAnExactMatch()
        {
            var lines = MarkdownSectionLocator.SplitLines("## 단계별 이행 상세 및 의사코드:\n본문");

            var (header, _) = MarkdownSectionLocator.LocateSection(
                lines, "## 단계별 이행 상세 및 의사코드", "## ");

            Assert.Equal(-1, header);
        }

        // 느슨해져도 헤딩이 아닌 줄을 잡으면 안 된다. 본문에 그 문구가 산문으로
        // 등장하는 것은 흔하다.
        [Fact]
        public void LocateSection_WhenNotExact_ShouldIgnoreALineThatIsNotAHeading()
        {
            var lines = MarkdownSectionLocator.SplitLines(
                "이 문서는 단계별 이행 상세 및 의사코드를 담는다\n## 단계별 이행 상세 및 의사코드:");

            var (header, _) = MarkdownSectionLocator.LocateSection(
                lines, "## 단계별 이행 상세 및 의사코드", "## ", exact: false);

            Assert.Equal(1, header);
        }

        // 펜스 안의 헤딩은 느슨 매칭에서도 헤딩이 아니다.
        [Fact]
        public void LocateSection_WhenNotExact_ShouldStillIgnoreAHeadingInsideAFence()
        {
            var lines = MarkdownSectionLocator.SplitLines(
                "```sql\n## 단계별 이행 상세 및 의사코드:\n```\n## 단계별 이행 상세 및 의사코드:");

            var (header, _) = MarkdownSectionLocator.LocateSection(
                lines, "## 단계별 이행 상세 및 의사코드", "## ", exact: false);

            Assert.Equal(3, header);
        }
    }
}
