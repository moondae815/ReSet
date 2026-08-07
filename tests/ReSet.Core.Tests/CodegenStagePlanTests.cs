using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using ReSet.Validator.Core.Models;
using Xunit;

namespace ReSet.Core.Tests
{
    public class CodegenStagePlanTests
    {
        private static string Agent => Path.Combine(Path.GetTempPath(), "JobX", "agent");

        private static BundleResult Bundle() => new(
            EntryPointPath: Path.Combine(Agent, "MigrationInstructions.md"),
            StepCodes: new[] { "S01", "S02" },
            Warnings: Array.Empty<string>(),
            StepsSplit: true,
            TaskFilePaths: new[]
            {
                Path.Combine(Agent, "task-00-bootstrap.md"),
                Path.Combine(Agent, "task-01-S01.md"),
                Path.Combine(Agent, "task-02-S02.md"),
                Path.Combine(Agent, "task-99-assembly.md"),
            });

        [Fact]
        public void FromBundle_ShouldPreserveStageOrder()
        {
            var plan = CodegenStagePlan.FromBundle(Bundle(), Agent);

            Assert.Equal(
                new[] { StageKind.Bootstrap, StageKind.Step, StageKind.Step, StageKind.Assembly },
                plan.Stages.Select(s => s.Kind));
        }

        [Fact]
        public void FromBundle_ShouldAttachStepCodesToStepStagesOnly()
        {
            var plan = CodegenStagePlan.FromBundle(Bundle(), Agent);

            Assert.Null(plan.Stages[0].StepCode);
            Assert.Equal("S01", plan.Stages[1].StepCode);
            Assert.Equal("S02", plan.Stages[2].StepCode);
            Assert.Null(plan.Stages[3].StepCode);
        }

        [Fact]
        public void FromBundle_ShouldPointStepSpecAtTheStepFile()
        {
            var plan = CodegenStagePlan.FromBundle(Bundle(), Agent);

            Assert.Equal(Path.Combine(Agent, "steps", "S01.md"), plan.Stages[1].StepSpecPath);
            Assert.Null(plan.Stages[0].StepSpecPath);
        }

        [Fact]
        public void FromBundle_ShouldFallBackToPlanFile_WhenNotSplit()
        {
            // 분할이 실패하면 단계별 파일이 없다. 그때는 검증 대상 설계서가 계획서 전문이다.
            var bundle = Bundle() with
            {
                StepsSplit = false,
                StepCodes = Array.Empty<string>(),
                TaskFilePaths = new[]
                {
                    Path.Combine(Agent, "task-00-bootstrap.md"),
                    Path.Combine(Agent, "task-99-assembly.md"),
                },
            };

            var plan = CodegenStagePlan.FromBundle(bundle, Agent);

            Assert.Equal(2, plan.Stages.Count);
            Assert.All(plan.Stages, s => Assert.Null(s.StepSpecPath));
        }

        [Fact]
        public void FromBundle_ShouldDeriveIdFromTaskFileName()
        {
            var plan = CodegenStagePlan.FromBundle(Bundle(), Agent);

            Assert.Equal(new[] { "00-bootstrap", "01-S01", "02-S02", "99-assembly" },
                plan.Stages.Select(s => s.Id));
        }

        [Fact]
        public void FromBundle_ShouldBuildStepSpecPathFromRawStepCode_WhenSanitizedDiffersFromRaw()
        {
            // 단계 코드는 AI가 생성한 계획서 텍스트에서 온다. 공백·콜론·슬래시처럼
            // 파일명에 안전하지 않은 문자가 실제로 나타날 수 있다(예: "S01: 회원 이관/추가").
            // task-*.md 파일명은 정화된 코드를 쓰지만, steps/{코드}.md 파일 자체는
            // InstructionBundleWriter가 원본 코드로 쓴다. StepSpecPath가 정화된
            // 파일명을 되짚어 조합하면 존재하지 않는 파일을 가리킨다.
            var rawCode = "S01: 회원 이관/추가";
            var taskFileName = TaskFileComposer.FileName(StageKind.Step, 1, rawCode);

            var bundle = new BundleResult(
                EntryPointPath: Path.Combine(Agent, "MigrationInstructions.md"),
                StepCodes: new[] { rawCode },
                Warnings: Array.Empty<string>(),
                StepsSplit: true,
                TaskFilePaths: new[]
                {
                    Path.Combine(Agent, "task-00-bootstrap.md"),
                    Path.Combine(Agent, taskFileName),
                    Path.Combine(Agent, "task-99-assembly.md"),
                });

            var plan = CodegenStagePlan.FromBundle(bundle, Agent);

            Assert.Equal(Path.Combine(Agent, "steps", $"{rawCode}.md"), plan.Stages[1].StepSpecPath);
        }
    }
}
