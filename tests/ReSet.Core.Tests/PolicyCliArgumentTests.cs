using System;
using ReSet.Cli;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PolicyCliArgumentTests
    {
        // 조용히 무시하면 사용자는 자기가 지정한 SP만 들어간 줄 안다.
        [Fact]
        public void 폐기된_policy_sps를_주면_오류로_중단한다()
        {
            var ex = Assert.Throws<ArgumentException>(
                () => Program.ParseCommandLineArgs(new[] { "--policy", "--policy-sps", "dbo.A" }));

            Assert.Contains("settlement-process.md", ex.Message);
        }

        [Fact]
        public void policy만_주면_배치_모드가_켜진다()
        {
            var args = Program.ParseCommandLineArgs(new[] { "--policy" });

            Assert.True(args.GeneratePolicy);
            Assert.True(args.IsBatchMode);
        }
    }
}
