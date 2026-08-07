using System;
using System.Collections.Generic;
using System.IO;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class TaskFileComposerTests
    {
        private static TaskFileInputs StepInputs() => new(
            Kind: StageKind.Step,
            JobName: "TestJob",
            TargetLanguage: "C#",
            StepCode: "S01",
            StepName: "스냅샷 생성",
            StepRelativePath: "steps/S01.md",
            SpecRelativePath: "../../Procedures/dbo.UP_A/docs/Spec.md",
            Dependencies: new List<IndexEntry> { new("dbo.TClient", "../raw/ddl/dbo.TClient.md") },
            HasStepContract: true,
            HasVerification: true,
            FailedStepCodes: Array.Empty<string>(),
            SinglePlanRelativePath: null);

        [Fact]
        public void FileName_ShouldPlaceTaskFilesFlatUnderAgent()
        {
            // agent/ 직하가 아니면 ResolveJobDirectory(두 단계 위)가 {jobDir}을
            // agent/로 해석해 --add-dir이 raw/ddl과 Spec.md를 덮지 못한다.
            Assert.Equal("task-00-bootstrap.md", TaskFileComposer.FileName(StageKind.Bootstrap, 0, null));
            Assert.Equal("task-01-S01.md", TaskFileComposer.FileName(StageKind.Step, 1, "S01"));
            Assert.Equal("task-99-assembly.md", TaskFileComposer.FileName(StageKind.Assembly, 99, null));
            Assert.DoesNotContain("/", TaskFileComposer.FileName(StageKind.Step, 1, "S01"));
        }

        [Fact]
        public void FileName_ShouldPadOrdinalToTwoDigits()
        {
            // 파일 목록이 사전 순으로 보일 때 회차 순서와 어긋나지 않게 한다.
            Assert.Equal("task-02-S02.md", TaskFileComposer.FileName(StageKind.Step, 2, "S02"));
            Assert.Equal("task-12-S12.md", TaskFileComposer.FileName(StageKind.Step, 12, "S12"));
        }

        [Theory]
        [InlineData("..")]
        [InlineData("../x")]
        [InlineData("/abs/path")]
        [InlineData("a/b")]
        [InlineData("")]
        [InlineData("!!!")]
        public void FileName_ShouldSanitizeUnsafeStepCodes(string unsafeCode)
        {
            // stepCode는 계획서 텍스트에서 뽑아낸 값이라 신뢰할 수 없다. 그대로
            // 파일명에 꽂으면 "../"나 경로 구분자, 절대 경로 조각으로 agent/
            // 바깥에 파일을 쓸 수 있다 - Task 7의 steps/{code}.md 쓰기 경로에
            // 있던 것과 같은 결함을 여기서 반복하지 않는다는 것을 고정한다.
            var fileName = TaskFileComposer.FileName(StageKind.Step, 1, unsafeCode);

            Assert.DoesNotContain("..", fileName);
            Assert.DoesNotContain("/", fileName);
            Assert.DoesNotContain("\\", fileName);
            Assert.StartsWith("task-01-", fileName);
            Assert.EndsWith(".md", fileName);
        }

        [Fact]
        public void ParseStageIdentity_ShouldRoundTripFileNameForEachStageKind()
        {
            // MetadataExporter(progress.json)와 CodegenStagePlan은 둘 다 이 메서드로
            // FileName의 인코딩을 되짚는다. 인코딩·디코딩이 어긋나면 두 산출물이
            // 서로 다른 회차를 가리키게 된다.
            var bootstrap = TaskFileComposer.ParseStageIdentity(
                Path.GetFileNameWithoutExtension(TaskFileComposer.FileName(StageKind.Bootstrap, 0, null)));
            Assert.Equal(new TaskStageIdentity("00-bootstrap", StageKind.Bootstrap, null), bootstrap);

            var step = TaskFileComposer.ParseStageIdentity(
                Path.GetFileNameWithoutExtension(TaskFileComposer.FileName(StageKind.Step, 1, "S01")));
            Assert.Equal(new TaskStageIdentity("01-S01", StageKind.Step, "S01"), step);

            var assembly = TaskFileComposer.ParseStageIdentity(
                Path.GetFileNameWithoutExtension(TaskFileComposer.FileName(StageKind.Assembly, 99, null)));
            Assert.Equal(new TaskStageIdentity("99-assembly", StageKind.Assembly, null), assembly);
        }

        [Fact]
        public void ParseStageIdentity_ShouldKeepMultiHyphenStepCodeIntact()
        {
            // 정화 후에도 "-"는 살아남는 문자다(SanitizeStepCode 참고). tail을
            // 두 번째 부분까지만 잘라내면 "S01-A"가 "S01"로 잘린다.
            var identity = TaskFileComposer.ParseStageIdentity("task-01-S01-A");

            Assert.Equal(StageKind.Step, identity.Kind);
            Assert.Equal("S01-A", identity.StepCode);
        }

        [Fact]
        public void ParseStageIdentity_ShouldFallBackToStepForMalformedFileName()
        {
            // "task-<서수>-<코드>" 형태를 벗어난 파일명은 회차 종류를 판별할 근거가
            // 없다. 조용히 넘어가지 않고 Step으로 물러서되(부트스트랩/조립으로
            // 잘못 단정하지 않음) StepCode는 비워 둔다.
            var identity = TaskFileComposer.ParseStageIdentity("weird-name");

            Assert.Equal(StageKind.Step, identity.Kind);
            Assert.Null(identity.StepCode);
        }

        [Fact]
        public void Compose_ShouldLinkEntryPointFirst()
        {
            var markdown = TaskFileComposer.Compose(StepInputs());

            var entry = markdown.IndexOf("MigrationInstructions.md", StringComparison.Ordinal);
            var step = markdown.IndexOf("steps/S01.md", StringComparison.Ordinal);

            Assert.True(entry >= 0 && entry < step);
        }

        [Fact]
        public void Compose_ShouldScopeToOneStepOnly()
        {
            var markdown = TaskFileComposer.Compose(StepInputs());

            Assert.Contains("S01", markdown);
            Assert.Contains("이번 회차에서 구현할 것", markdown);
            Assert.Contains("다른 Step의 코드를 작성하지 마십시오", markdown);
        }

        [Fact]
        public void Compose_ShouldLinkTheStepSpecAndSchemas()
        {
            var markdown = TaskFileComposer.Compose(StepInputs());

            Assert.Contains("steps/S01.md", markdown);
            Assert.Contains("Procedures/dbo.UP_A/docs/Spec.md", markdown);
            Assert.Contains("../raw/ddl/dbo.TClient.md", markdown);
            Assert.Contains("common/01-step-contract.md", markdown);
        }

        [Fact]
        public void Compose_ShouldTellBootstrapToBuildTheSkeletonOnly()
        {
            var markdown = TaskFileComposer.Compose(StepInputs() with
            {
                Kind = StageKind.Bootstrap, StepCode = null, StepName = null, StepRelativePath = null,
                SpecRelativePath = null,
            });

            Assert.Contains("공통 인프라", markdown);
            Assert.Contains("Tasklet을 구현하지 마십시오", markdown);
            Assert.DoesNotContain("steps/", markdown);
        }

        [Fact]
        public void Compose_ShouldLinkHostingAndConfig_OnlyForBootstrap()
        {
            // 배치 호스팅/DI와 멀티 DB 연결 문자열 구성은 스캐폴딩을 세우는 Bootstrap
            // 회차의 일이다. Step/Assembly 회차 지시서는 이미 구성된 것을 다시
            // 참조할 이유가 없으므로 이 링크를 받지 않는다.
            var bootstrap = TaskFileComposer.Compose(StepInputs() with
            {
                Kind = StageKind.Bootstrap, StepCode = null, StepName = null, StepRelativePath = null,
                SpecRelativePath = null,
            });
            Assert.Contains("common/03-hosting-and-config.md", bootstrap);

            var step = TaskFileComposer.Compose(StepInputs());
            Assert.DoesNotContain("common/03-hosting-and-config.md", step);

            var assembly = TaskFileComposer.Compose(StepInputs() with
            {
                Kind = StageKind.Assembly, StepCode = null, StepName = null, StepRelativePath = null,
                SpecRelativePath = null,
            });
            Assert.DoesNotContain("common/03-hosting-and-config.md", assembly);
        }

        [Fact]
        public void Compose_ShouldTellAssemblyToSkipFailedSteps()
        {
            var markdown = TaskFileComposer.Compose(StepInputs() with
            {
                Kind = StageKind.Assembly, StepCode = null, StepName = null, StepRelativePath = null,
                SpecRelativePath = null,
                FailedStepCodes = new[] { "S05", "S09" },
            });

            Assert.Contains("S05", markdown);
            Assert.Contains("S09", markdown);
            Assert.Contains("손대지 마십시오", markdown);
        }

        [Fact]
        public void Compose_ShouldNotClaimAllStepsSucceeded_WhenNoneFailed()
        {
            var markdown = TaskFileComposer.Compose(StepInputs() with
            {
                Kind = StageKind.Assembly, StepCode = null, StepName = null, StepRelativePath = null,
                SpecRelativePath = null,
            });

            Assert.Contains("파이프라인", markdown);
            Assert.DoesNotContain("손대지 마십시오", markdown);
        }

        [Fact]
        public void Compose_ShouldPointAtSinglePlanFile_WhenNotSplit()
        {
            var markdown = TaskFileComposer.Compose(StepInputs() with
            {
                StepRelativePath = null,
                SinglePlanRelativePath = "../docs/BatchMigrationPlan.md",
            });

            Assert.Contains("BatchMigrationPlan.md", markdown);
            Assert.Contains("S01", markdown);
        }
    }
}
