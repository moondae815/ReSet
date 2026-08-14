using System;
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

        /// <summary>
        /// 계획서는 Shadow 이름(batch_shadow.&lt;Table&gt;_&lt;RunId&gt;_&lt;StepCode&gt;),
        /// 체크포인트 키, 오류 로그, 게시 Manifest를 전부 RunId 기반으로 설계한다.
        /// 스텁이 그 값을 주지 않으면 18개 회차가 각자 다르게 우회한다.
        /// </summary>
        [Fact]
        public void AbstractTaskletStub_ShouldExposeExecutionIdentifiers_ForCSharp()
        {
            var stub = DataAccessPolicy.AbstractTaskletStub("C#");

            Assert.Contains("public Guid RunId { get; set; }", stub);
            Assert.Contains("public string InputHash { get; set; }", stub);
            Assert.Contains("public string SourceSnapshotId { get; set; }", stub);
        }

        [Fact]
        public void SettleContextStub_ShouldExposeExecutionIdentifiers_ForJava()
        {
            var stub = DataAccessPolicy.SettleContextStub("Java");

            Assert.Contains("getRunId", stub);
            Assert.Contains("setRunId", stub);
            Assert.Contains("getInputHash", stub);
            Assert.Contains("getSourceSnapshotId", stub);
        }

        /// <summary>
        /// 설계 1.1의 "최소 확장" 결정을 고정한다. 계획서 본문에는 ExecuteAsync와
        /// SettlementStepResult가 가득하지만, 실행 계약은 동기 Execute 하나다.
        /// 나중에 계획서를 보고 비동기를 끼워 넣으려는 사람에게 이 테스트가
        /// 결정을 상기시킨다.
        /// </summary>
        [Fact]
        public void AbstractTaskletStub_ShouldNotDeclareAsyncExecution_ForCSharp()
        {
            var stub = DataAccessPolicy.AbstractTaskletStub("C#");

            Assert.DoesNotContain("ExecuteAsync", stub);
            Assert.DoesNotContain("SettlementStepResult", stub);
            Assert.Contains("public StepResult Execute(SettleContext context)", stub);
        }

        /// <summary>
        /// C#의 SettleContext는 AbstractTaskletStub 안에 들어 있다. 언어를 착각한
        /// 호출을 조용히 통과시키면 SettleContext.cs라는 중복 파일이 나간다.
        /// </summary>
        [Fact]
        public void SettleContextStub_ShouldRejectCSharp()
        {
            Assert.Throws<NotSupportedException>(() => DataAccessPolicy.SettleContextStub("C#"));
        }

        /// <summary>
        /// 치환 책임을 DataAccessPolicy가 가진다. 두 언어 모두 자리표시자를 쓰고
        /// (C#은 [[ORM_BOUNDARY]], Java는 [[ORM_BOUNDARY_JAVA]]), 그대로 나가면
        /// 에이전트 프로젝트가 컴파일되지 않는다.
        /// </summary>
        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        public void AbstractTaskletStub_ShouldAlreadySubstituteTheOrmBoundaryComment(string targetLanguage)
        {
            var stub = DataAccessPolicy.AbstractTaskletStub(targetLanguage);

            Assert.DoesNotContain("[[ORM_BOUNDARY", stub);
            Assert.Contains("[데이터 액세스 경계]", stub);
        }

        /// <summary>
        /// ArchitectureTestStub_ShouldNotBeEntirelyCommentedOut의 짝이다.
        /// 그 결함(빈 테스트를 방어로 착각)은 한 번 고쳐졌는데 StepLogicTests에만
        /// 적용되지 않아 본문이 주석 세 줄인 채로 남아 있었다 - 그런데 지시서 규칙 6은
        /// "제공된 자가 검증용 단위 테스트를 통과시키라"고 말한다.
        /// </summary>
        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        public void StepLogicTestStub_ShouldFailUntilTheRoundWritesARealTest(string targetLanguage)
        {
            var stub = DataAccessPolicy.StepLogicTestStub(targetLanguage);

            var failMarker = targetLanguage == "Java" ? "fail(" : "Assert.Fail(";
            Assert.Contains(failMarker, stub);
            Assert.DoesNotContain("// Arrange\n\n            // Act", stub);
        }

        /// <summary>
        /// FileMappingService가 name.StartsWith(단계코드)로 회차 산출물을 찾는다.
        /// 테스트 파일을 S08LogicTests.cs로 만들면 Tasklet 없이도 이름 게이트가
        /// 통과해, 구현을 빼먹은 회차가 초록으로 보인다.
        /// </summary>
        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        public void StepLogicTestStub_ShouldDemandASuffixedFileName(string targetLanguage)
        {
            var stub = DataAccessPolicy.StepLogicTestStub(targetLanguage);

            Assert.Contains("LogicTests_", stub);
        }

        /// <summary>
        /// TaskFileComposer의 회차 라운드 지시문과 같은 문구를 스텁 자신의 문서 주석에도
        /// 남긴다. 지시서만 읽고 이 파일을 직접 열어 보지 않는 에이전트도, 파일을 복사해
        /// 채우다 이 주석을 보면 이 파일을 어떻게 다뤄야 하는지 알 수 있어야 한다 - 두
        /// 곳이 다른 문구를 쓰면 한쪽만 고쳐졌을 때 그 사실이 드러나지 않는다.
        ///
        /// 라운드 2 재검토: "원본 스캐폴드 파일을 삭제하십시오"는 이 파일 자체(모든
        /// 회차가 공유하는 템플릿)를 가리키는지, 프로젝트에 놓인 사본을 가리키는지
        /// 모호했다. Job당 한 번만 만들어지고 회차마다 다시 만들어지지 않으므로, 어느
        /// 회차든 이 파일 자체를 지우면 다음 회차부터는 복사할 원본이 없다 - 그래서
        /// "삭제하지 마십시오"로 뜻을 하나로 고정한다.
        /// </summary>
        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        public void StepLogicTestStub_ShouldWarnAgainstDeletingItselfAsTheSharedTemplate(string targetLanguage)
        {
            var stub = DataAccessPolicy.StepLogicTestStub(targetLanguage);

            Assert.Contains(
                "이 파일 자체를 프로젝트 산출물로 남기거나 삭제하지 마십시오. 다음 회차도 이 파일에서 복사합니다.",
                stub);
        }

        /// <summary>
        /// 회차 0이 지시받은 헥사고날 구조를 다중 프로젝트로 만들면 Tasklet과 Domain
        /// 타입이 코어와 다른 어셈블리에 놓인다. 단일 어셈블리만 스캔하면 규칙 1·2·3·4가
        /// 대상 0건으로 조용히 통과한다 - 아키텍처 지시와 검사 방식이 서로를 무력화한다.
        /// </summary>
        [Fact]
        public void ArchitectureTestStub_ShouldScanEveryBatchAssembly_ForCSharp()
        {
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.DoesNotContain("private static Assembly Target =>", stub);
            Assert.Contains("GetReferencedAssemblies", stub);
            Assert.Contains("ReSet.Batch", stub);
        }

        /// <summary>
        /// 0건 판정은 조립 회차에서만 켠다. 회차 0에는 Tasklet이 0개인 것이 정상이다.
        /// 스텁은 자신이 몇 회차에 놓이는지 알 수 없으므로 파일을 나누고, 배치 지시를
        /// 조립 회차에만 둔다 - 배치 지시가 곧 활성화 스위치다.
        /// </summary>
        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        public void AssemblyCompletenessTestStub_ShouldFailWhenNoTaskletExists(string targetLanguage)
        {
            var stub = DataAccessPolicy.AssemblyCompletenessTestStub(targetLanguage);

            Assert.Contains("AbstractSettleTasklet", stub);
            // 실패 메시지가 "왜 0건이 위험한가"를 말해야 한다 - 개수만 세고 끝나면
            // 읽는 사람이 대상 0건 통과라는 함정을 모른 채 넘어간다.
            Assert.Contains("Tasklet이 0개입니다", stub);
            Assert.Contains("대상 0건으로 통과", stub);
            // 회차 0의 아키텍처 테스트와 다른 파일이어야 활성화 스위치가 성립한다.
            Assert.Contains("AssemblyCompletenessTests", stub);
        }

        /// <summary>
        /// xUnit은 기본적으로 서로 다른 테스트 클래스를 별도 컬렉션으로 병렬 실행한다.
        /// ArchitectureTests.Targets처럼 참조된 ReSet.Batch.* 어셈블리를 먼저
        /// 강제 로드하지 않으면, AppDomain에 아직 아무것도 로드되지 않은 채로
        /// 이 검사가 먼저 실행되어 "Tasklet이 0개"라는 거짓 실패를 낼 수 있다 -
        /// 이 검사를 신뢰 가능하게 만들려던 장치 자체가 스케줄링에 좌우되면 안 된다.
        /// </summary>
        [Fact]
        public void AssemblyCompletenessTestStub_ShouldWarmUpReferencedAssemblies_ForCSharp()
        {
            var stub = DataAccessPolicy.AssemblyCompletenessTestStub("C#");

            Assert.Contains("GetReferencedAssemblies", stub);
            Assert.Contains("Assembly.Load", stub);
        }
    }
}
