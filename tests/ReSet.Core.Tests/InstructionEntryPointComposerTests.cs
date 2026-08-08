using System;
using System.Collections.Generic;
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
            SinglePlanRelativePath: null);

        private static EntryPointInputs Fallback() => Split() with
        {
            StepsSplit = false,
            Steps = new List<IndexEntry>(),
            HasStepContract = false,
            HasVerification = false,
            SinglePlanRelativePath = "../docs/BatchMigrationPlan.md",
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

        [Fact]
        public void PlanVerificationSection_ShouldSpeakEvenWhenPassed()
        {
            // 표기 부재를 "검증됨"으로 추론하는 것이 이 계열 결함의 뿌리다.
            var section = InstructionEntryPointComposer.PlanVerificationSection(VerificationOutcome.Passed);

            Assert.Contains("이 계획서의 검증 상태", section);
            Assert.NotEmpty(section.Trim());
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
            var inputs = stepsSplit
                ? Split()
                : Split() with { StepsSplit = false, SinglePlanRelativePath = "../docs/BatchMigrationPlan.md" };

            var markdown = InstructionEntryPointComposer.Compose(inputs);

            Assert.Contains(InstructionEntryPointComposer.StagedBundleMarker, markdown);
        }
    }
}
