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

        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        public void ArchitectureTestStub_ShouldCheckStepIdentifiers(string targetLanguage)
        {
            var stub = DataAccessPolicy.ArchitectureTestStub(targetLanguage);

            Assert.Contains("StepName", stub);
            Assert.Contains("SourceProcName", stub);
        }

        /// <summary>
        /// 규칙 4는 이름만 SourceProcName을 약속하고 본문은 StepName만 봤다. 이름과 본문이
        /// 다르면 검사되지 않은 항목이 검사된 것으로 보고된다 — 결함 5(빈 테스트를 방어로
        /// 착각)의 재발이다. SourceProcName은 protected지만 리플렉션으로 읽을 수 있다.
        /// </summary>
        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        public void ArchitectureTestStub_ShouldActuallyReadSourceProcName(string targetLanguage)
        {
            var stub = DataAccessPolicy.ArchitectureTestStub(targetLanguage);

            var marker = targetLanguage == "Java" ? "getSourceProcName" : "\"SourceProcName\"";
            Assert.Contains(marker, stub);
        }

        /// <summary>
        /// 생성자 주입은 지침 4번(DIP)과 common/03-hosting-and-config.md가 <b>권장</b>하는
        /// 형태다. 매개변수 없는 인스턴스화 실패를 위반으로 기록하면 도구가 권장한 설계를
        /// 도구가 벌하고, 에이전트는 이 파일을 통과시키라는 지시를 받으므로 매개변수 없는
        /// 생성자로 물러서거나 테스트 자체를 고치는 쪽으로 떠밀린다.
        /// </summary>
        [Fact]
        public void ArchitectureTestStub_ShouldNotPunishConstructorInjection_ForCSharp()
        {
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.DoesNotContain("매개변수 없는 생성자로 인스턴스화 실패", stub);
            // 생성자를 부를 수 없으면 생성자를 거치지 않고 값만 읽는다.
            Assert.Contains("RuntimeHelpers.GetUninitializedObject", stub);
        }

        [Fact]
        public void ArchitectureTestStub_ShouldNotPunishConstructorInjection_ForJava()
        {
            var stub = DataAccessPolicy.ArchitectureTestStub("Java");

            // 생성자를 못 부르면 값 확인을 건너뛰되 위반으로 기록하지 않는다.
            Assert.Contains("getDeclaredConstructor().newInstance()", stub);
            Assert.Contains("위반으로 기록하지 않는다", stub);
        }

        /// <summary>
        /// 설계 §8 규칙 5. 회차 0에는 Tasklet이 없어 규칙 1·2·4가 대상 0건으로 통과하므로,
        /// 이 규칙이 없으면 부트스트랩의 아키텍처 게이트가 실질적으로 한 가지만 검사한다.
        /// </summary>
        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        public void ArchitectureTestStub_ShouldCheckConnectionFactoryInjection(string targetLanguage)
        {
            var stub = DataAccessPolicy.ArchitectureTestStub(targetLanguage);

            Assert.Contains("SettleContext", stub);
            Assert.Contains("IDbConnectionFactory", stub);
            // 채워 넣을 구현체가 실제로 존재하는지까지 본다 - 회차 0에서 진짜로 검사되는 항목이다.
            Assert.Contains("구현체가 없습니다", stub);
        }

        /// <summary>
        /// 두 언어가 같은 규칙 집합을 표현해야 한다. Java에는 규칙 4가 통째로 없었다.
        /// </summary>
        [Fact]
        public void ArchitectureTestStub_ShouldExposeTheSameRuleCount_ForBothLanguages()
        {
            var csharp = DataAccessPolicy.ArchitectureTestStub("C#");
            var java = DataAccessPolicy.ArchitectureTestStub("Java");

            Assert.Equal(5, CountOccurrences(csharp, "[Fact]"));
            Assert.Equal(5, CountOccurrences(java, "@Test"));
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
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
