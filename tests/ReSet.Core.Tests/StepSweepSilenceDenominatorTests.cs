using System.Collections.Generic;
using System.Reflection;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 침묵 분모가 제품 규칙을 그대로 부르는지 본다.
    ///
    /// [왜 스윕이 아니라 여기서 보는가] 스윕은 코퍼스가 있어야 돌아 CI에서 건너뛴다.
    /// 이 가드는 합성 재료로 돌아 어디서나 빨개진다.
    ///
    /// [왜 리플렉션인가] BuildSpecTargets는 internal이고 이 프로젝트에는
    /// InternalsVisibleTo가 어디에도 없다(MechanicalValidatorTests의
    /// InvokeFirstStepRowCreationMessage·InvokeBatchRunRowCreationMessage와 같은
    /// 전례) - 그래서 테스트 조립체에서 직접 부를 수 없어 리플렉션으로 부른다.
    /// </summary>
    public class StepSweepSilenceDenominatorTests
    {
        [Fact]
        public void BuildSpecTargets_CollectsTargetTablesFromDmlRows()
        {
            var facts = new List<SpecStatementFacts>
            {
                SpecStatementFactsForTest("dbo.TSettleMst", "dbo.TSettleByTX"),
            };

            var targets = InvokeBuildSpecTargets(facts);

            Assert.Contains("dbo.TSettleMst", targets);
            Assert.Contains("dbo.TSettleByTX", targets);
        }

        private static HashSet<string> InvokeBuildSpecTargets(IEnumerable<SpecStatementFacts> facts)
        {
            var method = typeof(MechanicalValidator).GetMethod(
                "BuildSpecTargets", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return (HashSet<string>)method!.Invoke(null, new object[] { facts })!;
        }

        /// <summary>
        /// DmlRows의 TargetTable만 채운 최소 재료. BuildSpecTargets가 읽지 않는
        /// 다른 칸(PredicateColumns·JoinKeys·GroupBy·OrderBy·SetTargets·
        /// LocalVariables)은 빈 값으로 둔다.
        /// </summary>
        private static SpecStatementFacts SpecStatementFactsForTest(params string[] targetTables)
        {
            var rows = new List<SpecDmlRow>();
            for (var i = 0; i < targetTables.Length; i++)
            {
                rows.Add(new SpecDmlRow(
                    "UPDATE",
                    i + 1,
                    (i + 1) * 10,
                    targetTables[i],
                    System.Array.Empty<string>(),
                    System.Array.Empty<string>(),
                    System.Array.Empty<string>(),
                    System.Array.Empty<string>()));
            }

            return new SpecStatementFacts(
                rows,
                System.Array.Empty<SpecSetTarget>(),
                System.Array.Empty<SpecLocalVariable>());
        }
    }
}
