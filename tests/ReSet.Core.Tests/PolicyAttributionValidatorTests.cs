using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PolicyAttributionValidatorTests
    {
        private static readonly string[] Stages = { "## 1. 수수료율 스냅샷 적재", "## 2. 정산 원장 적재" };

        private static readonly Dictionary<string, string> Specs = new()
        {
            ["dbo.UP_RATE"] = "## 개요\n\n요율을 적재한다.\n",
            ["dbo.UP_INS"] = "## 개요\n\n결제수단이 impaymobile인 건을 대상으로 한다.\n",
        };

        private static string Sound() =>
            "## 1. 수수료율 스냅샷 적재\n\n" + PolicySectionContract.TableHeader + "\n"
            + PolicySectionContract.TableSeparator + "\n"
            + "| S1-01 | 요율을 적재한다 | dbo.UP_RATE · ## 개요 > \"요율을 적재\" | - |\n\n"
            + "## 2. 정산 원장 적재\n\n" + PolicySectionContract.TableHeader + "\n"
            + PolicySectionContract.TableSeparator + "\n"
            + "| S2-01 | 간편결제 건을 대상으로 한다 | dbo.UP_INS · ## 개요 > \"impaymobile인 건\" | impaymobile |\n";

        [Fact]
        public void 근거가_실재하면_결함이_없다()
        {
            var result = PolicyAttributionValidator.Validate(Sound(), Stages, Specs);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void 명부에_있는_단계가_문서에_없으면_고발한다()
        {
            var body = Sound().Replace("## 2. 정산 원장 적재", "## 2. 다른 이름");

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.StageMissing);
        }

        [Fact]
        public void 단계_순서가_뒤바뀌면_고발한다()
        {
            var body =
                "## 2. 정산 원장 적재\n\n" + PolicySectionContract.TableHeader + "\n"
                + PolicySectionContract.TableSeparator + "\n"
                + "| S2-01 | 간편결제 건 | dbo.UP_INS · ## 개요 > \"impaymobile인 건\" | impaymobile |\n\n"
                + "## 1. 수수료율 스냅샷 적재\n\n" + PolicySectionContract.TableHeader + "\n"
                + PolicySectionContract.TableSeparator + "\n"
                + "| S1-01 | 요율을 적재한다 | dbo.UP_RATE · ## 개요 > \"요율을 적재\" | - |\n";

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.StageOutOfOrder);
        }

        [Fact]
        public void 없는_명세서를_인용하면_고발한다()
        {
            var body = Sound().Replace("dbo.UP_RATE ·", "dbo.UP_없는것 ·");

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.EvidenceLabelUnknown);
        }

        [Fact]
        public void 없는_헤딩을_인용하면_고발한다()
        {
            var body = Sound().Replace("## 개요 > \"요율을 적재\"", "## 없는절 > \"요율을 적재\"");

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.EvidenceHeadingNotFound);
        }

        [Fact]
        public void 원문에_없는_구절을_인용하면_고발한다()
        {
            var body = Sound().Replace("\"요율을 적재\"", "\"요율을 삭제\"");

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.EvidenceQuoteNotFound);
        }

        [Fact]
        public void ID_접두사가_단계_번호와_다르면_고발한다()
        {
            var body = Sound().Replace("| S2-01 |", "| S9-01 |");

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.IdPrefixMismatch);
        }

        [Fact]
        public void 근거_칸이_형식을_어기면_고발한다()
        {
            var body = Sound().Replace("dbo.UP_RATE · ## 개요 > \"요율을 적재\"", "그냥 문장");

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.EvidenceMissing);
        }

        [Fact]
        public void 번호_없는_단계_제목_아래에서는_명부_순서로_ID_접두사를_기대한다()
        {
            var unnumberedStages = new[] { "## 수수료율 스냅샷 적재", "## 정산 원장 적재" };
            var body =
                "## 수수료율 스냅샷 적재\n\n" + PolicySectionContract.TableHeader + "\n"
                + PolicySectionContract.TableSeparator + "\n"
                + "| S1-01 | 요율을 적재한다 | dbo.UP_RATE · ## 개요 > \"요율을 적재\" | - |\n\n"
                + "## 정산 원장 적재\n\n" + PolicySectionContract.TableHeader + "\n"
                + PolicySectionContract.TableSeparator + "\n"
                + "| S2-01 | 간편결제 건을 대상으로 한다 | dbo.UP_INS · ## 개요 > \"impaymobile인 건\" | impaymobile |\n";

            var result = PolicyAttributionValidator.Validate(body, unnumberedStages, Specs);

            Assert.DoesNotContain(result.Defects, d => d.Type == PolicyDefectType.IdPrefixMismatch);
        }

        [Fact]
        public void 번호_있는_단계_제목은_선언된_번호를_그대로_기대한다()
        {
            var skippedNumberStages = new[] { "## 1. 수수료율 스냅샷 적재", "## 3. 정산 집계" };
            var body =
                "## 1. 수수료율 스냅샷 적재\n\n" + PolicySectionContract.TableHeader + "\n"
                + PolicySectionContract.TableSeparator + "\n"
                + "| S1-01 | 요율을 적재한다 | dbo.UP_RATE · ## 개요 > \"요율을 적재\" | - |\n\n"
                + "## 3. 정산 집계\n\n" + PolicySectionContract.TableHeader + "\n"
                + PolicySectionContract.TableSeparator + "\n"
                + "| S2-01 | 간편결제 건을 대상으로 한다 | dbo.UP_INS · ## 개요 > \"impaymobile인 건\" | impaymobile |\n";

            var result = PolicyAttributionValidator.Validate(body, skippedNumberStages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.IdPrefixMismatch && d.RuleId == "S2-01");
        }

        [Fact]
        public void 제목이_같은_단계_둘에서_첫_단계의_규칙은_잘못된_접두사_기대로_고발되지_않는다()
        {
            // 리뷰어가 재현한 입력: 번호 없는 같은 제목의 단계가 둘이면
            // (파서가 LocateSection의 선재 한계로 같은 절을 두 번 읽어 S1-01
            // 행이 StageNumber 1과 2로 각각 한 번씩 파싱된다), 검증기가 헤딩
            // 문자열을 키로 한 맵을 쓰면 맵이 마지막 색인(2)으로 덮어써져
            // 첫 통과분(StageNumber=1, 진짜로 옳은 S1-01)까지 'S2-' 기대로
            // 고발됐다. rule.StageNumber를 그대로 쓰면 그 오탐 한 건이 사라져
            // IdPrefixMismatch 총수가 2에서 1로 준다.
            var duplicateNamedStages = new[] { "## 수수료율 스냅샷 적재", "## 수수료율 스냅샷 적재" };
            var body =
                "## 수수료율 스냅샷 적재\n\n" + PolicySectionContract.TableHeader + "\n"
                + PolicySectionContract.TableSeparator + "\n"
                + "| S1-01 | 요율을 적재한다 | dbo.UP_RATE · ## 개요 > \"요율을 적재\" | - |\n\n"
                + "## 수수료율 스냅샷 적재\n\n" + PolicySectionContract.TableHeader + "\n"
                + PolicySectionContract.TableSeparator + "\n"
                + "| S2-01 | 간편결제 건을 대상으로 한다 | dbo.UP_INS · ## 개요 > \"impaymobile인 건\" | impaymobile |\n";

            var result = PolicyAttributionValidator.Validate(body, duplicateNamedStages, Specs);

            Assert.Equal(1, result.Defects.Count(d => d.Type == PolicyDefectType.IdPrefixMismatch));
        }
    }
}
