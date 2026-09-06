using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PolicyDocumentChecksTests
    {
        private static PolicyRule Rule(string id, string evidence, string codeValue) =>
            new("## 1. 단계", 1, id, "규칙", evidence, codeValue, 10);

        private static SettlementCodebook Codebook(params string[] values) =>
            new(values.Select(v => new CodebookEntry(
                    v, null, new[] { "dbo.UP_A" }, true,
                    new[] { new CodebookMatch("dbo.TCode", new Dictionary<string, string> { ["Name"] = "뜻" }) }))
                .ToList(),
                Array.Empty<string>());

        private static SettlementProcessRoster Roster(params string[] procedures) =>
            new(new[] { new PolicyStage("1. 단계", procedures) }, Array.Empty<string>());

        [Fact]
        public void 사전에_있는_코드값은_통과한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "impaymobile") },
                Codebook("impaymobile"));

            Assert.Empty(defects);
        }

        // 이 검사가 「AI가 지어낸 번역」을 막는 자리다.
        [Fact]
        public void 사전에_없는_코드값을_고발한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "지어낸값") },
                Codebook("impaymobile"));

            var defect = Assert.Single(defects);
            Assert.Equal(PolicyDefectType.CodeValueNotInCodebook, defect.Type);
            Assert.Equal("S1-01", defect.RuleId);
        }

        [Fact]
        public void 하이픈은_해당없음_표기이므로_고발하지_않는다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", PolicySectionContract.NoCodeValue) },
                Codebook("impaymobile"));

            Assert.Empty(defects);
        }

        [Fact]
        public void 한_칸에_쉼표로_여러_코드값을_적어도_각각_대조한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "impaymobile, 지어낸값") },
                Codebook("impaymobile"));

            var defect = Assert.Single(defects);
            Assert.Contains("지어낸값", defect.Message);
        }

        [Fact]
        public void 명부의_SP가_모두_인용되면_통과한다()
        {
            var defects = PolicyDocumentChecks.CheckProcedureCitationCoverage(
                new[]
                {
                    Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "-"),
                    Rule("S1-02", "dbo.UP_B · ## 개요 > \"y\"", "-"),
                },
                Roster("dbo.UP_A", "dbo.UP_B"));

            Assert.Empty(defects);
        }

        // 명부 대조(Task 3)는 「명부에 있는가」만 본다. 명부에 있어도 AI가 그 명세서를
        // 안 읽고 지나가면 규칙이 통째로 빠지고 문서는 멀쩡해 보인다. 여기가 그 두 번째 문이다.
        [Fact]
        public void 한_번도_인용되지_않은_SP를_고발한다()
        {
            var defects = PolicyDocumentChecks.CheckProcedureCitationCoverage(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "-") },
                Roster("dbo.UP_A", "dbo.UP_B"));

            var defect = Assert.Single(defects);
            Assert.Equal(PolicyDefectType.ProcedureNeverCited, defect.Type);
            Assert.Equal("dbo.UP_B", defect.Subject);
        }

        [Fact]
        public void 제외에_적힌_SP는_인용_의무가_없다()
        {
            var roster = new SettlementProcessRoster(
                new[] { new PolicyStage("1. 단계", new[] { "dbo.UP_A" }) },
                new[] { "dbo.UP_B" });

            var defects = PolicyDocumentChecks.CheckProcedureCitationCoverage(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "-") }, roster);

            Assert.Empty(defects);
        }

        [Fact]
        public void 형식이_깨진_근거는_인용으로_세지_않는다()
        {
            var defects = PolicyDocumentChecks.CheckProcedureCitationCoverage(
                new[] { Rule("S1-01", "그냥 문장", "-") },
                Roster("dbo.UP_A"));

            Assert.Contains(defects, d => d.Type == PolicyDefectType.ProcedureNeverCited);
        }
    }
}
