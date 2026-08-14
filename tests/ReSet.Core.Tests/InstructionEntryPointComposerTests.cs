using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class InstructionEntryPointComposerTests
    {
        private static EntryPointInputs Split() => new(
            JobName: "POQSettleProcDaily",
            TargetLanguage: "C#",
            PlanOutcome: VerificationOutcome.Passed,
            Preamble: string.Empty,
            StepsSplit: true,
            Steps: new List<IndexEntry> { new("S01 스냅샷 생성", "steps/S01.md") },
            Dependencies: new List<IndexEntry> { new("dbo.TClient", "raw/ddl/dbo.TClient.md") },
            Specs: new List<IndexEntry> { new("dbo.UP_A", "../../Procedures/dbo.UP_A/docs/Spec.md") },
            HasStepContract: true,
            HasVerification: true,
            SinglePlanRelativePath: null,
            // StepsSplit: true에 단계 1개가 실제로 실려 있으므로, 이 픽스처가 이름대로
            // "완전히 검증된 분할"을 뜻하려면 null(커버리지 개념 없음)이 아니라 그 단계가
            // 모두 검증됐다는 값이어야 한다.
            Coverage: new VerificationCoverage(1, 1, false, false));

        private static EntryPointInputs Fallback() => Split() with
        {
            StepsSplit = false,
            Steps = new List<IndexEntry>(),
            HasStepContract = false,
            HasVerification = false,
            SinglePlanRelativePath = "../docs/BatchMigrationPlan.md",
            // 실제 폴백 회차는 BatchStepPlanParser.TryParse가 null을 돌려주는
            // 경로다 - 빈 목록이 아니라 총량 자체가 없다. Split()의 Coverage(1, 1, false)를
            // 그대로 물려받으면 "분할이 실행돼 1단계를 모두 검증했다"는, 이 픽스처가
            // 나타내려는 것과 정반대의 값을 주장하게 된다.
            Coverage = new VerificationCoverage(null, 0, false, false),
        };

        [Fact]
        public void Compose_ShouldPlaceGuidelinesBeforeAnyPlanLink()
        {
            var markdown = InstructionEntryPointComposer.Compose(Split());

            var guidelines = markdown.IndexOf("에이전트 핵심 수행 지침", StringComparison.Ordinal);
            var stepsLink = markdown.IndexOf("steps/S01.md", StringComparison.Ordinal);

            Assert.True(guidelines >= 0, "지침 섹션이 없다");
            Assert.True(stepsLink >= 0, "단계 링크가 없다");
            Assert.True(guidelines < stepsLink, "지침이 계획 링크보다 뒤에 있다");
        }

        [Fact]
        public void Compose_ShouldPlaceGuidelinesBeforePlanLink_EvenInFallback()
        {
            // 분할이 실패해도 순서 교정만은 잃지 않는다.
            var markdown = InstructionEntryPointComposer.Compose(Fallback());

            var guidelines = markdown.IndexOf("에이전트 핵심 수행 지침", StringComparison.Ordinal);
            var planLink = markdown.IndexOf("BatchMigrationPlan.md", StringComparison.Ordinal);

            Assert.True(guidelines < planLink);
        }

        [Fact]
        public void Compose_ShouldPlaceBoundaryRulesBeforeAnyPlanLink()
        {
            var markdown = InstructionEntryPointComposer.Compose(Split());

            var rules = markdown.IndexOf("데이터 액세스 경계 규칙", StringComparison.Ordinal);
            var stepsLink = markdown.IndexOf("steps/S01.md", StringComparison.Ordinal);

            Assert.True(rules >= 0);
            Assert.True(rules < stepsLink);
        }

        [Fact]
        public void Compose_ShouldPutVerificationBannerFirst()
        {
            // L1Exhausted 경로는 경고 배너를 낸다. Passed 경로는 아래 별도 테스트에서 본다.
            var markdown = InstructionEntryPointComposer.Compose(
                Split() with { PlanOutcome = VerificationOutcome.L1Exhausted });

            var banner = markdown.IndexOf("이 계획서의 검증 상태", StringComparison.Ordinal);
            var guidelines = markdown.IndexOf("에이전트 핵심 수행 지침", StringComparison.Ordinal);

            Assert.True(banner >= 0);
            Assert.True(banner < guidelines);
        }

        [Fact]
        public void Compose_ShouldIncludeReadingContract()
        {
            var markdown = InstructionEntryPointComposer.Compose(Split());

            Assert.Contains("읽기 계약", markdown);
            Assert.Contains("다른 Step 파일을 읽지 마십시오", markdown);
        }

        [Fact]
        public void Compose_ShouldCarryPreamble_WhenPresent()
        {
            var markdown = InstructionEntryPointComposer.Compose(
                Split() with { Preamble = "> 경고: 검증을 소진했습니다." });

            Assert.Contains("검증을 소진했습니다", markdown);
        }

        [Fact]
        public void Compose_ShouldLinkCommonFiles_WhenSkeletonWasSplit()
        {
            var markdown = InstructionEntryPointComposer.Compose(Split());

            Assert.Contains("common/00-architecture.md", markdown);
            Assert.Contains("common/01-step-contract.md", markdown);
            Assert.Contains("common/02-data-access-boundary.md", markdown);
            Assert.Contains("verification/integrity-sql.md", markdown);
        }

        [Fact]
        public void Compose_ShouldNotLinkMissingCommonFiles()
        {
            var markdown = InstructionEntryPointComposer.Compose(
                Split() with { HasStepContract = false, HasVerification = false });

            Assert.DoesNotContain("common/01-step-contract.md", markdown);
            Assert.DoesNotContain("verification/integrity-sql.md", markdown);
            // 경계 규칙 파일은 계획서가 아니라 DataAccessPolicy에서 오므로 항상 있다.
            Assert.Contains("common/02-data-access-boundary.md", markdown);
        }

        [Fact]
        public void Compose_ShouldListDependenciesAndSpecs()
        {
            var markdown = InstructionEntryPointComposer.Compose(Split());

            Assert.Contains("raw/ddl/dbo.TClient.md", markdown);
            Assert.Contains("Procedures/dbo.UP_A/docs/Spec.md", markdown);
        }

        [Fact]
        public void Compose_ShouldNameTheJavaStubFilesAndPackagePath_InGuideline9()
        {
            // 지침 9번이 언어와 무관하게 "src/AbstractSettleTasklet.cs"만 가리키면 Java
            // 에이전트가 존재하지 않는 C# 파일을 배치하라는 지시를 받는다.
            var markdown = InstructionEntryPointComposer.Compose(Split() with { TargetLanguage = "Java" });

            Assert.Contains("src/AbstractSettleTasklet.java", markdown);
            Assert.Contains("src/ISettleStep.java", markdown);
            Assert.Contains("src/ISettleRepository.java", markdown);
            Assert.Contains("src/main/java/com/reset/batch/core/", markdown);
            Assert.DoesNotContain("AbstractSettleTasklet.cs", markdown);
        }

        [Fact]
        public void Compose_ShouldNameTheCSharpStubFile_InGuideline9()
        {
            var markdown = InstructionEntryPointComposer.Compose(Split() with { TargetLanguage = "C#" });

            Assert.Contains("src/AbstractSettleTasklet.cs", markdown);
            Assert.DoesNotContain("AbstractSettleTasklet.java", markdown);
        }

        /// <summary>
        /// 규칙 10 문장만 잘라낸다. 전체 markdown에 대고 Contains를 하면 규칙 9가
        /// 이미 같은 파일명을 언급하므로, 규칙 10이 하드코딩되어 항상 한쪽 언어만
        /// 가리키게 망가져도 전체 문자열 검사로는 잡히지 않는다.
        /// </summary>
        private static string ExtractGuideline10(string markdown)
        {
            var start = markdown.IndexOf("10. **[중요]**", StringComparison.Ordinal);
            Assert.True(start >= 0, "규칙 10을 찾을 수 없습니다.");

            // markdown.IndexOf("\n\n", ...)는 Windows(\r\n)에서 문단 경계를 절대 못 찾는다 -
            // 이 문서는 StringBuilder.AppendLine(Environment.NewLine)으로 만들어지는데,
            // Windows의 Environment.NewLine은 \r\n이라 빈 줄이 "\r\n\r\n"이 되고 그 안에는
            // "\n\n" 부분 문자열이 없기 때문이다. 하우스 스타일(specs/2026-06-02-
            // cross-platform-compatibility-analysis.md:27)이 명시한 \r?\n 개행 무관 대조로
            // 고정한다.
            var boundary = Regex.Match(markdown[start..], @"\r?\n\r?\n");
            Assert.True(boundary.Success, "규칙 10 이후 단락 경계를 찾을 수 없습니다.");
            return markdown[start..(start + boundary.Index)];
        }

        [Fact]
        public void Compose_ShouldNameTheJavaStubFile_InGuideline10()
        {
            // 규칙 10의 스텁 경로가 언어와 무관하게 ".cs"로 굳으면 Java 에이전트가
            // 존재하지 않는 C# 파일을 권위 있는 계약이라고 지시받는다.
            var markdown = InstructionEntryPointComposer.Compose(Split() with { TargetLanguage = "Java" });
            var guideline10 = ExtractGuideline10(markdown);

            Assert.Contains("src/AbstractSettleTasklet.java", guideline10);
            Assert.DoesNotContain("AbstractSettleTasklet.cs", guideline10);
        }

        [Fact]
        public void Compose_ShouldNameTheCSharpStubFile_InGuideline10()
        {
            var markdown = InstructionEntryPointComposer.Compose(Split() with { TargetLanguage = "C#" });
            var guideline10 = ExtractGuideline10(markdown);

            Assert.Contains("src/AbstractSettleTasklet.cs", guideline10);
            Assert.DoesNotContain("AbstractSettleTasklet.java", guideline10);
        }

        [Fact]
        public void ExtractGuideline10_ShouldFindTheParagraphBoundary_OnWindowsLineEndings()
        {
            // 하우스 스타일(docs/superpowers/specs/2026-06-02-cross-platform-compatibility-
            // analysis.md:27)은 개행을 \r?\n으로 CRLF에 무관하게 파싱하라고 못박는다. 이
            // 환경은 macOS라 Environment.NewLine이 \n이므로, 실제 Windows 산출물(\r\n)을
            // 흉내 내려면 여기서 강제로 CRLF화해야 이 테스트가 그 결함을 재현한다.
            var markdown = InstructionEntryPointComposer.Compose(Split()).Replace("\n", "\r\n");

            var guideline10 = ExtractGuideline10(markdown);

            Assert.Contains("src/AbstractSettleTasklet.cs", guideline10);
        }

        [Fact]
        public void Compose_ShouldPlaceGuideline10ImmediatelyAfterGuideline9_ForJava()
        {
            // C# 경로의 순서는 InstructionBundleWriterTests에서 이미 고정한다. Java도
            // 같은 순서를 지켜야 상속 강제(9)와 권위 순서(10)가 같이 읽힌다.
            var markdown = InstructionEntryPointComposer.Compose(Split() with { TargetLanguage = "Java" });

            Assert.True(
                markdown.IndexOf("9. **[중요]**", StringComparison.Ordinal)
                    < markdown.IndexOf("10. **[중요]**", StringComparison.Ordinal),
                "규칙 10이 규칙 9보다 앞에 있습니다.");
        }

        [Fact]
        public void PlanVerificationSection_ShouldSpeakEvenWhenPassed()
        {
            // 표기 부재를 "검증됨"으로 추론하는 것이 이 계열 결함의 뿌리다.
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed, coverage: null);

            Assert.Contains("이 계획서의 검증 상태", section);
            Assert.NotEmpty(section.Trim());
        }

        [Fact]
        public void PlanVerificationSection_ShouldStateBothPassedAndUnverifiableSteps_WhenBothTrue()
        {
            // 스펙 §6: "모두 통과"와 미검증 단계의 존재가 §0에 함께 나오지 않는 것이
            // 결함이다. 보강이 실패해 "검증 불가" 단계가 남는 회차에는 "모두 통과"
            // 한 줄만 찍혀 그 아래 미검증 단계 목록과 정면으로 모순됐다.
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed, coverage: new VerificationCoverage(19, 17, false, false));

            Assert.Contains("L1 기계 검증과 L2 AI 교차 리뷰를 모두 통과", section);
            Assert.Contains("검증되지 못한 단계가 있습니다", section);
        }

        [Fact]
        public void Compose_ShouldStateBothPassedAndUnverifiableSteps_InSection0()
        {
            // Split()은 단계 1개(S01)만 색인한다. Coverage가 19개 중 17개를 말하면
            // 목차와 커버리지가 서로 다른 회차를 묘사하게 된다 - 이 픽스처가 실제로
            // 색인하는 단계 수(1개)와 맞춰, 그중 1개는 미검증이라고만 말한다.
            var markdown = InstructionEntryPointComposer.Compose(
                Split() with { Coverage = new VerificationCoverage(1, 0, false, false) });

            Assert.Contains("L1 기계 검증과 L2 AI 교차 리뷰를 모두 통과", markdown);
            Assert.Contains("검증되지 못한 단계가 있습니다", markdown);
        }

        /// <summary>
        /// CLI의 재구동 경로(메뉴 3)는 이 표식으로 회차 번들과 옛 단일 문서를 가른다.
        /// 표식이 어느 진입점에서든 빠지면 회차용 번들이 "다른 Step을 읽지 마십시오"를
        /// 이해하지 못하는 전체 Job 경로로 흘러간다(b336ee5가 막은 오라우팅).
        /// ReSet.Cli에는 테스트 프로젝트가 없으므로 그 계약을 여기서 고정한다.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Compose_ShouldAlwaysEmitTheStagedBundleMarker(bool stepsSplit)
        {
            // false 분기는 Fallback()을 그대로 쓴다 - 임시 오버라이드로 StepsSplit만
            // 끄고 Steps·Coverage는 Split()에서 그대로 물려받으면, 분할이 꺼졌는데도
            // 단계 링크와 "1/1 검증됨" 커버리지가 남아 폴백을 표방하면서 실제로는
            // 반대를 나타내는 픽스처가 된다.
            var inputs = stepsSplit ? Split() : Fallback();

            var markdown = InstructionEntryPointComposer.Compose(inputs);

            Assert.Contains(InstructionEntryPointComposer.StagedBundleMarker, markdown);
        }

        // 종전에는 FloorViolations에 Unverifiable이 있는지만 봤다. 단계가 아예
        // 없으면 위반도 없으므로 플래그가 꺼졌고, 가장 적게 검증된 문서가 가장
        // 깨끗한 배지를 달았다 - 실측(POQSettleProc7)에서 단계별 섹션이 하나도
        // 없고 원본 오류코드 20개가 빠진 문서가 ✅ "모두 통과"로 나갔다.
        [Fact]
        public void PlanVerificationSection_WhenSplitDidNotRun_WarnsInsteadOfClaimingCleanPass()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(null, 0, false, false));

            Assert.Contains("⚠️", section);
            Assert.DoesNotContain("✅", section);
            Assert.Contains("단계 단위 기계 검증이 실행되지 않았", section);
        }

        [Fact]
        public void PlanVerificationSection_WhenStepsAreUnverified_Warns()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(19, 17, false, false));

            Assert.Contains("⚠️", section);
            Assert.Contains("검증되지 못한 단계", section);
        }

        [Fact]
        public void PlanVerificationSection_WhenDocumentCodesAreMissing_Warns()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(19, 19, true, false));

            Assert.Contains("⚠️", section);
            Assert.Contains("원본 오류코드", section);
        }

        // §0의 배너 사각지대 중 마지막 하나. 이 전에는 단계별 하한 검사가 전부
        // 통과해도(HasUnverifiedSteps == false) 목차가 원본 프로시저 몇 개를
        // 어느 단계에도 담지 못하면(또는 커버리지 대조 자체를 못 돌리면) §0은
        // 그 사실을 몰랐다 - 문서 위쪽의 [커버리지 누락]/[커버리지 검증 불가]
        // 배너와 정면으로 모순되는 ✅가 나갔다.
        [Fact]
        public void PlanVerificationSection_WhenProceduresAreUncovered_Warns()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(19, 19, false, true));

            Assert.Contains("⚠️", section);
            Assert.Contains("원본 프로시저", section);
        }

        // HasUncoveredProcedures는 서로 다른 두 배너 상태를 하나로 합친 것이다 -
        // (1) 대조 자체가 안 돈 상태(CoverageUnverifiable, "확인 불가")와 (2) 대조는
        // 돌았는데 일부가 빠진 상태(UncoveredProcedures, "확인해 보니 없음"). §0의
        // 문구가 "나타나지 않았다"처럼 부재를 단정하면 (1)에서 거짓이 된다 -
        // CoverageUnverifiable의 배너 자체가 "문서가 그 프로시저들을 다루지 않았다는
        // 뜻은 아닙니다"라고 명시적으로 경고하는 바로 그 오류다
        // (VerificationBanner.cs 참고). 두 상태 모두에서 참인 말은 "확인되지
        // 않았다"뿐이다.
        [Fact]
        public void PlanVerificationSection_WhenProceduresAreUncovered_DoesNotAssertAbsence()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(19, 19, false, true));

            Assert.DoesNotContain("나타나지 않았", section);
            Assert.Contains("확인되지 않았", section);
        }

        // 참인 사유만 나열해야 한다. 해당 없는 사유를 적으면 읽는 사람이 실제
        // 결함을 흘려보낸다.
        [Fact]
        public void PlanVerificationSection_ListsOnlyTheReasonsThatApply()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(19, 17, false, false));

            Assert.DoesNotContain("원본 오류코드", section);
            Assert.DoesNotContain("실행되지 않았", section);
            Assert.DoesNotContain("원본 프로시저", section);
        }

        // 설계 §2.2 샘플은 두 사유를 쉼표로 잇는다("…실행되지 않았고, 원본…"). 사유가
        // 둘 이상이면 공백만으로는 문장이 죽죽 이어져 어디서 한 사유가 끝나고 다음이
        // 시작하는지 눈으로 가르기 어렵다.
        [Fact]
        public void PlanVerificationSection_WithTwoReasons_JoinsThemWithAComma()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(null, 0, true, false));

            Assert.Contains("실행되지 않았고, 원본", section);
        }

        // 부재 확인. 조건이 뒤집히면 정상 산출물마다 거짓 경고가 붙는데, 그것을
        // 잡는 테스트는 이것뿐이다. 네 사유 모두 깨끗해야 ✅가 유지된다.
        [Fact]
        public void PlanVerificationSection_WhenEverythingIsVerified_KeepsTheCleanPass()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.Passed,
                new VerificationCoverage(19, 19, false, false));

            Assert.Contains("✅", section);
            Assert.DoesNotContain("⚠️", section);
            Assert.DoesNotContain("다만", section);
        }

        // Passed가 아닌 경로는 이미 ⚠️와 "사람의 검토가 필요합니다"를 쓴다.
        [Fact]
        public void PlanVerificationSection_NonPassedOutcome_IsUnchanged()
        {
            var section = InstructionEntryPointComposer.PlanVerificationSection(
                VerificationOutcome.QualityRejected,
                new VerificationCoverage(19, 19, false, false));

            Assert.Contains("⚠️", section);
            Assert.Contains("사람의 검토가 필요합니다", section);
        }
    }
}
