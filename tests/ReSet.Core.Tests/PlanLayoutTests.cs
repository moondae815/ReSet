using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PlanLayoutTests
    {
        private static BatchStepPlan Step(string code) =>
            new(code, $"{code} 단계", new[] { "UP_X" }, new[] { "dbo.T" }, new[] { "-1" }, false, Array.Empty<string>());

        [Fact]
        public void IsSplitAvailable_ShouldBeFalse_WhenSectionsMissing()
        {
            // 조각이 없으면 앵커를 만들 수 없다. Skeleton만으로는 Step 경계를 모른다.
            var layout = new PlanLayout("골격", null, new[] { Step("S01") }, null);

            Assert.False(layout.IsSplitAvailable);
        }

        [Fact]
        public void IsSplitAvailable_ShouldBeFalse_WhenSectionsEmpty()
        {
            var layout = new PlanLayout("골격", new Dictionary<string, string>(), new[] { Step("S01") }, null);

            Assert.False(layout.IsSplitAvailable);
        }

        [Fact]
        public void IsSplitAvailable_ShouldBeTrue_WhenSectionsPresent()
        {
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string> { ["S01"] = "### S01 본문" },
                new[] { Step("S01") },
                null);

            Assert.True(layout.IsSplitAvailable);
        }

        [Fact]
        public void ConsolidatedPipelineResult_ShouldDefaultLayoutToNull()
        {
            // 기본값이 있어야 기존 호출부 4곳이 변경 없이 컴파일된다.
            var result = new ConsolidatedPipelineResult("계획", null, null, VerificationOutcome.Passed);

            Assert.Null(result.Layout);
        }

        [Fact]
        public void ConsolidatedPipelineResult_ShouldCarryLayout_WhenProvided()
        {
            var layout = new PlanLayout("골격", new Dictionary<string, string> { ["S01"] = "본문" }, null, null);

            var result = new ConsolidatedPipelineResult("계획", null, null, VerificationOutcome.Passed, layout);

            Assert.Same(layout, result.Layout);
        }
    }
}
