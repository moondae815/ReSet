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

        /// <summary>
        /// 최종 전체 리뷰 I1 - 「던지는가」와 「사용자가 행동할 수 있는 말로 받는가」는
        /// 다른 질문이다. 위 테스트는 파서가 던지는 것만 재고, 던짐을 소유한 자리는
        /// 있었지만 <b>잡음을 소유한 자리가 없었다</b>. Main의 유일한 catch는
        /// OperationCanceledException(Program.cs:1875)이고 AppDomain.UnhandledException
        /// 핸들러는 src/ 전체에 없다 - 그래서 이 오류는 날것 스택 트레이스와 런타임이
        /// 정한 종료 코드로 사용자에게 갔다. 설계서 §6.3은 「오류로 중단하고 명부
        /// 파일을 안내한다」를 요구한다.
        ///
        /// 화면 출력 자체를 단정하지 않는 이유: AnsiConsole이 출력 대상을 첫 사용
        /// 시점에 캐시해 Console.SetOut을 나중에 바꿔도 못 잡는다는 실측이 이 저장소에
        /// 이미 있다(CoverageMapCommandTests:86-89). 그래서 화면에 낼 말과 종료 코드를
        /// 반환값으로 내주는 자리를 두고 그것을 잰다 - 출력 한 줄은 Main이 그 값을
        /// 관례대로(빨간 안내 + Environment.ExitCode) 옮기는 일만 한다.
        /// </summary>
        [Fact]
        public void 폐기된_policy_sps는_예외가_아니라_안내와_종료코드_1로_돌아온다()
        {
            var result = Program.TryParseCommandLineArgs(new[] { "--policy", "--policy-sps", "dbo.A" });

            Assert.Null(result.Arguments);
            Assert.Equal(1, result.ExitCode);
            Assert.NotNull(result.UserMessage);
            Assert.Contains("settlement-process.md", result.UserMessage);
        }

        [Fact]
        public void 정상_인자는_안내_없이_그대로_돌아온다()
        {
            var result = Program.TryParseCommandLineArgs(new[] { "--policy" });

            Assert.Null(result.UserMessage);
            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(result.Arguments);
            Assert.True(result.Arguments!.GeneratePolicy);
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
