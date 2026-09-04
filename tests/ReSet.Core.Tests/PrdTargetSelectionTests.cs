using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdTargetSelectionTests
    {
        [Fact]
        public void Resolve_ShouldNotPullInATargetWhoseLabelIsAPrefixOfAnotherSelectedLabel()
        {
            // 실제 코퍼스 사례: "dbo.UP_UTIL_SETTLE_INS"는
            // "dbo.UP_UTIL_SETTLE_INS_EXTRA"의 접두어다. 긴 쪽만 골랐을 때
            // 짧은 쪽까지 딸려 들어오면 안 된다(StartsWith 되짚기의 결함).
            var shorter = new PrdTarget("dbo.UP_UTIL_SETTLE_INS", "/docs/short", HasExistingPrd: false);
            var longer = new PrdTarget("dbo.UP_UTIL_SETTLE_INS_EXTRA", "/docs/long", HasExistingPrd: false);
            var targets = new List<PrdTarget> { shorter, longer };

            var picked = new List<string> { PrdTargetSelection.ToDisplayLabel(longer) };

            var resolved = PrdTargetSelection.Resolve(targets, picked);

            Assert.Single(resolved);
            Assert.Equal("dbo.UP_UTIL_SETTLE_INS_EXTRA", resolved[0].Label);
        }

        [Fact]
        public void Resolve_ShouldMatchDisplayLabelIncludingExistingPrdSuffix()
        {
            var target = new PrdTarget("dbo.UP_A", "/docs/a", HasExistingPrd: true);
            var picked = new List<string> { PrdTargetSelection.ToDisplayLabel(target) };

            var resolved = PrdTargetSelection.Resolve(new List<PrdTarget> { target }, picked);

            Assert.Single(resolved);
            Assert.Equal("dbo.UP_A", resolved[0].Label);
        }

        [Fact]
        public void Resolve_ShouldReturnEmpty_WhenNothingPicked()
        {
            var target = new PrdTarget("dbo.UP_A", "/docs/a", HasExistingPrd: false);

            var resolved = PrdTargetSelection.Resolve(new List<PrdTarget> { target }, new List<string>());

            Assert.Empty(resolved);
        }
    }
}
