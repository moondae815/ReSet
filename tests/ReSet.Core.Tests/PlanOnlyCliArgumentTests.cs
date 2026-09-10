using ReSet.Cli;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// `--plan-only` — 이미 저장된 명세서만으로 통합 배치 전환 계획서를 만드는 무인 경로의
    /// 인자 계약. 이 모드는 DB에 붙지 않지만 AI는 무인으로 부르므로, 배치 모드로 켜져
    /// <see cref="ReSet.Core.Services.Clients.Cli.CliProviderBatchGuard"/>를 그대로 받아야 한다.
    ///
    /// 거부 판정을 파서가 소유하는 이유는 <see cref="PolicyCliArgumentTests"/>와 같다 —
    /// 안내 문구가 행동할 수 있는 말인지를 테스트가 재려면 반환값으로 봐야 한다.
    /// </summary>
    public class PlanOnlyCliArgumentTests
    {
        [Fact]
        public void plan_only는_배치_모드를_켠다()
        {
            var args = Program.ParseCommandLineArgs(
                new[] { "--plan-only", "--job-name", "Settle_Daily", "--sp", "dbo.UP_A" });

            Assert.True(args.PlanOnly);
            Assert.True(args.IsBatchMode);
        }

        [Fact]
        public void plan_only는_sp_나열_순서를_그대로_보존한다()
        {
            // 이 순서가 곧 배치 스텝의 실행 순서다. 파서가 정렬하거나 중복을 뭉개면
            // 사람이 지정한 정산 순서가 조용히 바뀐다.
            var args = Program.ParseCommandLineArgs(
                new[] { "--plan-only", "--job-name", "J", "--sp", "dbo.UP_C,dbo.UP_A,dbo.UP_B" });

            Assert.Equal(new[] { "dbo.UP_C", "dbo.UP_A", "dbo.UP_B" }, args.TargetProcedures);
        }

        [Fact]
        public void job_name_없는_plan_only는_안내와_종료코드_1로_돌아온다()
        {
            // Job 이름이 없으면 계획서를 놓을 자리가 없다. 파이프라인을 수십 분 돌린
            // 뒤에 알면 늦는다.
            var result = Program.TryParseCommandLineArgs(new[] { "--plan-only", "--sp", "dbo.UP_A" });

            Assert.Null(result.Arguments);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("--job-name", result.UserMessage);
        }

        [Fact]
        public void sp_없는_plan_only는_안내와_종료코드_1로_돌아온다()
        {
            var result = Program.TryParseCommandLineArgs(new[] { "--plan-only", "--job-name", "J" });

            Assert.Null(result.Arguments);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("--sp", result.UserMessage);
        }

        [Fact]
        public void plan_only와_all은_함께_쓸_수_없다()
        {
            // --all은 대상을 주지만 순서를 주지 않는다. 배치 스텝은 순서가 의미의
            // 일부이므로 임의 순서로 진행하면 안 된다.
            var result = Program.TryParseCommandLineArgs(
                new[] { "--plan-only", "--job-name", "J", "--all" });

            Assert.Null(result.Arguments);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("--all", result.UserMessage);
        }

        [Fact]
        public void plan_only와_policy는_함께_쓸_수_없다()
        {
            var result = Program.TryParseCommandLineArgs(
                new[] { "--plan-only", "--job-name", "J", "--sp", "dbo.UP_A", "--policy" });

            Assert.Null(result.Arguments);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("--policy", result.UserMessage);
        }

        [Fact]
        public void plan_only가_없으면_job_name만으로는_배치_모드가_아니다()
        {
            // 종래 계약을 못박는다 — --job-name은 그 자체로 무인 실행을 켜지 않는다.
            var args = Program.ParseCommandLineArgs(new[] { "--job-name", "Settle_Daily" });

            Assert.False(args.IsBatchMode);
        }
    }
}
