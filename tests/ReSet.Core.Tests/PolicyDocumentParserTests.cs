using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PolicyDocumentParserTests
    {
        private static readonly string[] Stages = { "## 1. 수수료율 스냅샷 적재", "## 2. 정산 원장 적재" };

        private const string Policy = @"## 정산 업무 개요

전체 조망.

## 1. 수수료율 스냅샷 적재

요율을 하루 한 번 적재한다.

| ID | 업무 규칙 | 근거 | 코드값 |
| :--- | :--- | :--- | :--- |
| S1-01 | 요율을 당일자로 적재한다 | dbo.UP_RATE · ## 개요 > ""요율을 적재"" | - |

## 2. 정산 원장 적재

원장을 적재한다.

| ID | 업무 규칙 | 근거 | 코드값 |
| :--- | :--- | :--- | :--- |
| S2-01 | 간편결제 건을 대상으로 한다 | dbo.UP_INS · ## 개요 > ""결제수단이 impaymobile"" | impaymobile |

## 부록 A. 코드값 사전

| 코드값 | 의미 |
| :--- | :--- |
| impaymobile | 간편결제 |
";

        [Fact]
        public void 단계별_규칙을_읽는다()
        {
            var rules = PolicyDocumentParser.Parse(Policy, Stages);

            Assert.Equal(2, rules.Count);
            Assert.Equal("S1-01", rules[0].Id);
            Assert.Equal("S2-01", rules[1].Id);
        }

        [Fact]
        public void 단계_번호를_함께_담는다()
        {
            var rules = PolicyDocumentParser.Parse(Policy, Stages);

            Assert.Equal(1, rules[0].StageNumber);
            Assert.Equal(2, rules[1].StageNumber);
        }

        [Fact]
        public void 코드값_칸을_읽는다()
        {
            var rules = PolicyDocumentParser.Parse(Policy, Stages);

            Assert.Equal("-", rules[0].CodeValue);
            Assert.Equal("impaymobile", rules[1].CodeValue);
        }

        [Fact]
        public void 부록의_표는_규칙으로_읽지_않는다()
        {
            var rules = PolicyDocumentParser.Parse(Policy, Stages);

            Assert.DoesNotContain(rules, r => r.StageHeading.Contains("부록"));
        }

        [Fact]
        public void 표_머리와_구분선은_규칙이_아니다()
        {
            var rules = PolicyDocumentParser.Parse(Policy, Stages);

            Assert.DoesNotContain(rules, r => r.Id == "ID");
            Assert.DoesNotContain(rules, r => r.Id.All(c => c == ':' || c == '-'));
        }

        [Fact]
        public void 근거_인용_안의_이스케이프_안_된_파이프도_행을_되살린다()
        {
            const string policyWithPipeInQuote = @"## 1. 수수료율 스냅샷 적재

| ID | 업무 규칙 | 근거 | 코드값 |
| :--- | :--- | :--- | :--- |
| S1-01 | 요율에 FLAGS를 적용한다 | dbo.UP_RATE · ## 개요 > ""요율에 FLAGS | 4를 적용한다"" | - |
";

            var rules = PolicyDocumentParser.Parse(
                policyWithPipeInQuote, new[] { "## 1. 수수료율 스냅샷 적재" });

            var rule = Assert.Single(rules);
            Assert.Equal("S1-01", rule.Id);
            Assert.Contains("FLAGS | 4", rule.EvidenceRaw);
            Assert.Equal("-", rule.CodeValue);
        }

        [Fact]
        public void 근거_문법이_없는_다섯칸_행은_되살리지_않는다()
        {
            const string policyWithProseRow = @"## 1. 수수료율 스냅샷 적재

| ID | 업무 규칙 | 근거 | 코드값 |
| :--- | :--- | :--- | :--- |
| S1-09 | 그냥 문장이다 | 아무 근거 표기가 없다 | 값1 | 값2 |
";

            var rules = PolicyDocumentParser.Parse(
                policyWithProseRow, new[] { "## 1. 수수료율 스냅샷 적재" });

            Assert.DoesNotContain(rules, r => r.Id == "S1-09");
        }
    }
}
