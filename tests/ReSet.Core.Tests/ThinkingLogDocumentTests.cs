using System;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ThinkingLogDocumentTests
    {
        private static readonly DateTime WrittenAt = new(2026, 8, 7, 17, 28, 39);

        [Fact]
        public void Compose_CarriesTheThinkingTextUnderTheHeader()
        {
            var document = ThinkingLogDocument.Compose(
                "**Considering deletion logic**", "OpenAI", "gpt-5.6-terra", "high", WrittenAt);

            Assert.Contains("# AI 추론 과정 로그", document);
            Assert.Contains("**Considering deletion logic**", document);
        }

        // 이 결함이 POQSettleProcDaily3에서 docs/Thinking.md를 통째로 사라지게 했다.
        // 골격 응답의 summary_text가 비어 오면 호출부가 파일 쓰기를 건너뛰었고,
        // README·architecture·AGENTS가 보장한 산출물이 없는 채로 나갔다.
        // 본문이 없어도 문서는 나와야 하며, 그 자리는 사유를 적어야 한다.
        [Fact]
        public void Compose_WhenThinkingTextIsEmpty_FallsBackToThePlaceholderInsteadOfProducingNothing()
        {
            var document = ThinkingLogDocument.Compose(null, "OpenAI", "gpt-5.6-terra", "high", WrittenAt);

            Assert.Contains("# AI 추론 과정 로그", document);
            Assert.Contains(ThinkingLogPlaceholder.For("OpenAI"), document);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Compose_TreatsBlankThinkingTextAsAbsent(string? thinkingText)
        {
            var document = ThinkingLogDocument.Compose(
                thinkingText, "OpenAI", "gpt-5.6-terra", "high", WrittenAt);

            Assert.Contains(ThinkingLogPlaceholder.For("OpenAI"), document);
        }

        // 사유를 뭉뚱그리면 진단이 어긋난다. CLI 제공자의 빈 추론은 설정 문제가 아니다.
        [Fact]
        public void Compose_WhenThinkingTextIsEmptyOnACliProvider_UsesTheCliWording()
        {
            var document = ThinkingLogDocument.Compose(null, "claude-cli", "sonnet", "high", WrittenAt);

            Assert.Contains("CLI", document);
            Assert.DoesNotContain("추론 비활성화", document);
        }

        [Fact]
        public void Compose_NamesTheProviderModelAndEffortInTheHeader()
        {
            var document = ThinkingLogDocument.Compose(
                "thinking", "OpenAI", "gpt-5.6-terra", "high", WrittenAt);

            Assert.Contains("OpenAI", document);
            Assert.Contains("gpt-5.6-terra", document);
            Assert.Contains("Effort: high", document);
        }

        [Fact]
        public void Compose_WhenEffortIsAbsent_OmitsTheEffortSuffix()
        {
            var document = ThinkingLogDocument.Compose(
                "thinking", "OpenAI", "gpt-5.6-terra", null, WrittenAt);

            Assert.DoesNotContain("Effort", document);
        }

        [Fact]
        public void Compose_StampsTheSuppliedTimestamp()
        {
            var document = ThinkingLogDocument.Compose(
                "thinking", "OpenAI", "gpt-5.6-terra", "high", WrittenAt);

            Assert.Contains("2026-08-07 17:28:39", document);
        }
    }
}
