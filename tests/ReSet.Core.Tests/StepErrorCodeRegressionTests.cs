using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 실측 회차를 축약한 픽스처로, 결함이 되살아나면 잡는다.
    ///
    /// 실측 산출물(output/Jobs/...)은 git 추적 대상이 아니라 픽스처로 쓸 수 없다.
    /// 결함을 재현하는 최소 형태만 저장소에 남긴다.
    /// </summary>
    public class StepErrorCodeRegressionTests
    {
        // CancellationPolicyTests가 baseline 파일을 읽는 방식과 같다 - 저장소
        // 루트에서 소스 트리를 직접 읽는다. 이 저장소에는 출력 디렉터리 복사
        // 설정이 없으므로 픽스처 하나 때문에 도입하지 않는다.
        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(
                RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", name));

        [Fact]
        public void MeasuredPlanStructure_GainsErrorCodesFromTheSpec()
        {
            var codes = SpecReturnCodeExtractor.Extract(new[]
            {
                ("dbo.UP_UTIL_SETTLE_COMM_UPD", Fixture("SettleCommUpdSpecExcerpt.md")),
            });

            var before = BatchStepPlanParser.TryParse(Fixture("PlanStructureWithEmptyErrorCodes.md"))!;
            var after = BatchStepPlanParser.TryParse(
                PlanStructureEnricher.Enrich(
                    Fixture("PlanStructureWithEmptyErrorCodes.md"),
                    codes,
                    new Dictionary<string, SpecTargetTableExtractor.StepTableSets>()).Markdown)!;

            Assert.Empty(before.Single(s => s.Code == "S06").ErrorCodes);
            Assert.Equal(16, after.Single(s => s.Code == "S06").ErrorCodes.Count);
        }

        [Fact]
        public void StepsWithoutLegacyOriginStayEmptyAndPassTheFloorCheck()
        {
            var codes = SpecReturnCodeExtractor.Extract(new[]
            {
                ("dbo.UP_UTIL_SETTLE_COMM_UPD", Fixture("SettleCommUpdSpecExcerpt.md")),
            });

            var steps = BatchStepPlanParser.TryParse(
                PlanStructureEnricher.Enrich(
                    Fixture("PlanStructureWithEmptyErrorCodes.md"),
                    codes,
                    new Dictionary<string, SpecTargetTableExtractor.StepTableSets>()).Markdown)!;

            var s00 = steps.Single(s => s.Code == "S00");
            Assert.Empty(s00.ErrorCodes);

            var body = $"### {s00.Code} {s00.Name}\n\n```sql\nSELECT 1 FROM {s00.TargetTables[0]};\n```";
            var result = new MechanicalValidator().ValidateBatchStep(body, s00, Array.Empty<string>());

            Assert.True(result.IsValid);
        }

        [Fact]
        public void EnrichedStepPassesTheFloorCheckWhenTheBodyCarriesEveryCode()
        {
            // 실측에서 24개 단계 전부가 이미 코드를 본문에 담고 있었다. 보강이
            // 검사를 진짜로 만들되 재시도 폭주를 부르지는 않는다는 뜻이다.
            var codes = SpecReturnCodeExtractor.Extract(new[]
            {
                ("dbo.UP_UTIL_SETTLE_COMM_UPD", Fixture("SettleCommUpdSpecExcerpt.md")),
            });

            var s06 = BatchStepPlanParser.TryParse(
                PlanStructureEnricher.Enrich(
                    Fixture("PlanStructureWithEmptyErrorCodes.md"),
                    codes,
                    new Dictionary<string, SpecTargetTableExtractor.StepTableSets>()).Markdown)!
                .Single(s => s.Code == "S06");

            var body = $"### {s06.Code} {s06.Name}\n\n```sql\nSELECT 1 FROM {s06.TargetTables[0]};\n```\n\n"
                + string.Join(" ", s06.ErrorCodes.Select(c => $"`{c}`"));

            var result = new MechanicalValidator().ValidateBatchStep(body, s06, Array.Empty<string>());

            Assert.True(result.IsValid);
        }
    }
}
