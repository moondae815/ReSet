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
        public void 사전에_있는_값_여럿을_쉼표로_나열해도_전부_통과한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "impaymobile, payco") },
                Codebook("impaymobile", "payco"));

            Assert.Empty(defects);
        }

        // T9 프롬프트가 코드값을 백틱으로 감싸 verbatim 을 요구한다. 그 표기 자체를
        // 오탐으로 고발하면 교정 재호출 피드백이 모델에게 실행 불가능한 지시가 된다.
        [Fact]
        public void 백틱으로_감싼_코드값도_사전에_있으면_통과한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "`impaymobile`") },
                Codebook("impaymobile"));

            Assert.Empty(defects);
        }

        [Fact]
        public void 큰따옴표로_감싼_코드값도_사전에_있으면_통과한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "\"impaymobile\"") },
                Codebook("impaymobile"));

            Assert.Empty(defects);
        }

        // 벗기기가 검사를 무력화하지 않았는지 - 감싸도 사전에 없으면 여전히 고발돼야 한다.
        [Fact]
        public void 백틱으로_감싸도_사전에_없으면_여전히_고발한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "`지어낸값`") },
                Codebook("impaymobile"));

            var defect = Assert.Single(defects);
            Assert.Equal(PolicyDefectType.CodeValueNotInCodebook, defect.Type);
        }

        // 한쪽에만 감싼 문자가 있으면 모델이 형식을 잘못 쓴 것이다. 조용히 벗겨 주면
        // 잘못된 형태가 통과해 버리므로, 짝이 맞을 때만 벗겨야 한다.
        [Fact]
        public void 한쪽만_감싸진_코드값은_벗기지_않고_고발한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "`impaymobile") },
                Codebook("impaymobile"));

            var defect = Assert.Single(defects);
            Assert.Equal(PolicyDefectType.CodeValueNotInCodebook, defect.Type);
        }

        // 벗기기는 표기를 걷어내는 것이지 값을 없애는 것이 아니다. 빈 감쌈 쌍(``)은
        // 벗기면 빈 문자열이 되어 「빈 조각」 필터에 조용히 먹혔었다(2026-09-06 회귀).
        // 감싼 표기만 있고 값이 없는 것이므로 원본 그대로 대조에 넘겨 고발돼야 한다.
        [Fact]
        public void 빈_감쌈만_있는_코드값은_고발한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "``") },
                Codebook("impaymobile"));

            var defect = Assert.Single(defects);
            Assert.Equal(PolicyDefectType.CodeValueNotInCodebook, defect.Type);
        }

        [Fact]
        public void 빈_감쌈과_정상_값이_섞이면_빈_쪽만_고발되고_정상_값은_통과한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "``, impaymobile") },
                Codebook("impaymobile"));

            var defect = Assert.Single(defects);
            Assert.DoesNotContain("impaymobile", defect.Message);
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
