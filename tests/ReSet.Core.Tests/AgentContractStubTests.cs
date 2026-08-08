using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class AgentContractStubTests
    {
        [Fact]
        public void ArchitectureTestStub_ShouldNotBeEntirelyCommentedOut()
        {
            // 이전 스텁은 본문이 전부 주석이라 통과해도 아무것도 보장하지 않았다.
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.Contains("Assert.True", stub);
            Assert.DoesNotContain("// var result = Types.InCurrentDomain()", stub);
        }

        [Fact]
        public void ArchitectureTestStub_ShouldEnforceTaskletInheritance()
        {
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.Contains("ISettleStep", stub);
            Assert.Contains("AbstractSettleTasklet", stub);
        }

        [Fact]
        public void ArchitectureTestStub_ShouldForbidDirectConnectionCreation()
        {
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.Contains("SqlConnection", stub);
        }

        [Fact]
        public void ArchitectureTestStub_ShouldCheckStepIdentifiers()
        {
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.Contains("StepName", stub);
            Assert.Contains("SourceProcName", stub);
        }

        [Fact]
        public void ArchitectureTestStub_ShouldStateWhatItCannotCheck()
        {
            // UseTransaction 강제는 호출 그래프 분석이 필요해 여기서 못 잡는다.
            // 잡아준다고 착각하면 경계 위반이 조용히 통과한다.
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.Contains("UseTransaction", stub);
            Assert.Contains("L1", stub);
        }

        [Fact]
        public void ArchitectureTestStub_ShouldUseArchUnitForJava()
        {
            var stub = DataAccessPolicy.ArchitectureTestStub("Java");

            Assert.Contains("ArchUnit", stub);
            Assert.DoesNotContain("NetArchTest", stub);
        }

        [Fact]
        public void RepositoryContractStub_ShouldDeclareStepRegistration()
        {
            var stub = DataAccessPolicy.RepositoryContractStub("C#");

            Assert.Contains("ISettleStep", stub);
            Assert.Contains("Order", stub);
        }
    }
}
