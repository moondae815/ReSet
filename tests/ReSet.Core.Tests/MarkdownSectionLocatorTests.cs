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
    }
}
