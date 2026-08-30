using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ErrorCodeAttributionTests
    {
        private static BatchStepPlan Step(string code, params string[] errorCodes) =>
            new(code, $"{code} 단계",
                LegacyProcedures: new[] { $"dbo.UP_{code}" },
                TargetTables: new[] { $"dbo.T{code}" },
                ErrorCodes: errorCodes,
                Chunkable: false,
                SchemaTables: new[] { $"dbo.T{code}" });

        [Fact]
        public void MissingCode_IsAttributedToTheStepThatDeclaredIt()
        {
            var steps = new[] { Step("S01", "-9010"), Step("S02", "-9140") };
            var missing = new Dictionary<string, IReadOnlyList<string>>
            {
                ["dbo.UP_S02"] = new[] { "-9140" }
            };

            var result = ErrorCodeAttribution.Attribute(missing, steps);

            Assert.Equal(new[] { "S02" }, result.StepCodes);
            Assert.False(result.HasUnattributed);
        }

        // 어느 쪽이 빠뜨렸는지 모른다. 좁히지 않고 둘 다 연다 -
        // 잘못 좁히면 결함이 남은 단계가 동결된다.
        [Fact]
        public void CodeDeclaredByTwoSteps_OpensBoth()
        {
            var steps = new[] { Step("S05", "-9010"), Step("S06", "-9010") };
            var missing = new Dictionary<string, IReadOnlyList<string>>
            {
                ["dbo.UP_S05"] = new[] { "-9010" }
            };

            var result = ErrorCodeAttribution.Attribute(missing, steps);

            Assert.Equal(new[] { "S05", "S06" }, result.StepCodes);
        }

        // 어느 단계도 선언하지 않은 코드가 누락됐다면 목차 결함이다.
        // 아무 단계나 골라 붙이면 멀쩡한 단계를 다시 쓰게 된다.
        [Fact]
        public void CodeDeclaredByNoStep_IsReportedAsUnattributed()
        {
            var steps = new[] { Step("S01", "-9010") };
            var missing = new Dictionary<string, IReadOnlyList<string>>
            {
                ["dbo.UP_Other"] = new[] { "-4000" }
            };

            var result = ErrorCodeAttribution.Attribute(missing, steps);

            Assert.Empty(result.StepCodes);
            Assert.True(result.HasUnattributed);
        }

        // 목차가 없으면 귀속할 좌표 자체가 없다. 조용히 빈 목록을 내면
        // "고칠 단계가 없다"로 읽혀 누락이 사라진다.
        [Fact]
        public void NullSteps_ReportsUnattributed()
        {
            var missing = new Dictionary<string, IReadOnlyList<string>>
            {
                ["dbo.UP_S01"] = new[] { "-9010" }
            };

            var result = ErrorCodeAttribution.Attribute(missing, steps: null);

            Assert.Empty(result.StepCodes);
            Assert.True(result.HasUnattributed);
        }

        [Fact]
        public void NoMissingCodes_AttributesNothing()
        {
            var steps = new[] { Step("S01", "-9010") };

            var result = ErrorCodeAttribution.Attribute(
                new Dictionary<string, IReadOnlyList<string>>(), steps);

            Assert.Empty(result.StepCodes);
            Assert.False(result.HasUnattributed);
        }

        // 코드 표기는 공백과 대소문자로 갈린다. 정규화하지 않으면
        // 선언된 코드를 못 찾아 전부 미귀속이 된다.
        [Fact]
        public void CodeMatching_IgnoresSurroundingWhitespace()
        {
            var steps = new[] { Step("S01", " -9010 ") };
            var missing = new Dictionary<string, IReadOnlyList<string>>
            {
                ["dbo.UP_S01"] = new[] { "-9010" }
            };

            var result = ErrorCodeAttribution.Attribute(missing, steps);

            Assert.Equal(new[] { "S01" }, result.StepCodes);
        }
    }
}
