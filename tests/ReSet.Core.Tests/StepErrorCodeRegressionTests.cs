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
        // 조건 컬럼 대조를 쓰지 않는 테스트용 빈 재료. 비어 있으면 검사가
        // 소프트 스킵하므로 이 테스트들이 보는 동작은 달라지지 않는다.
        private static readonly System.Collections.Generic.IReadOnlyDictionary<string, SpecConditions> NoConditions =
            new System.Collections.Generic.Dictionary<string, SpecConditions>();

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

        /// <summary>
        /// 옛 명제("레거시 출신이 없으면 ErrorCodes가 비고, 대조 항목이 없어서
        /// 하한 검사를 통과한다")는 더 이상 참이 아니다 - PlanStructureEnricher가
        /// 이제 그런 단계에 예약 대역 코드를 발급한다(S00 → -9000, Task 2). 그러나
        /// 이 테스트가 원래 지키던 성질 - "레거시 출신이 없는 단계가 코드가 없다는
        /// 이유로 결함 판정을 받지 않는다" - 은 여전히 지켜져야 한다. 다만 방식이
        /// 바뀐다: 대조 항목이 0개라서 통과하는 것이 아니라, 발급된 코드가 본문에
        /// 실제로 등장해서 통과한다.
        /// </summary>
        [Fact]
        public void StepsWithoutLegacyOriginReceiveTheReservedCodeAndPassTheFloorCheckWhenTheBodyCarriesIt()
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

            // S00의 블록 시작은 -9000 - 0*10 = -9000이다.
            Assert.Equal(new[] { "-9000" }, s00.ErrorCodes);

            var body = $"### {s00.Code} {s00.Name}\n\n```sql\nSELECT 1 FROM {s00.TargetTables[0]};\n```\n\n-9000";
            var result = new MechanicalValidator().ValidateBatchStep(body, s00, Array.Empty<string>(), NoConditions);

            Assert.True(result.IsValid);
        }

        /// <summary>
        /// 새 설계의 실제 회귀 방어선이다. 예약 코드가 발급됐는데 본문이 그 코드를
        /// 담지 않으면 하한 검사가 걸어야 한다 - 그렇지 않으면 예약 대역 발급은
        /// 검증되지 않는 장식일 뿐이고, 옛 결함(모델이 지어낸 코드가 아무 대조 없이
        /// 통과하던 것)과 본질이 같은 구멍이 다시 열린다.
        /// </summary>
        [Fact]
        public void StepsWithoutLegacyOriginFailTheFloorCheckWhenTheBodyOmitsTheReservedCode()
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

            // 발급된 코드(-9000)를 본문에 싣지 않는다 - 옛 테스트의 본문과 같은 모양이다.
            var body = $"### {s00.Code} {s00.Name}\n\n```sql\nSELECT 1 FROM {s00.TargetTables[0]};\n```";
            var result = new MechanicalValidator().ValidateBatchStep(body, s00, Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("-9000"));
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

            var result = new MechanicalValidator().ValidateBatchStep(body, s06, Array.Empty<string>(), NoConditions);

            Assert.True(result.IsValid);
        }
    }
}
