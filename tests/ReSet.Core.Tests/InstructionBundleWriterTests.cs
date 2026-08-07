using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class InstructionBundleWriterTests : IDisposable
    {
        private readonly string _outputRoot;
        private readonly string _jobDir;
        private readonly string _agentDir;

        public InstructionBundleWriterTests()
        {
            _outputRoot = Path.Combine(Path.GetTempPath(), "reset-bundle-" + Guid.NewGuid().ToString("N"));
            _jobDir = Path.Combine(_outputRoot, "Jobs", "TestJob");
            _agentDir = Path.Combine(_jobDir, "agent");
            Directory.CreateDirectory(_jobDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_outputRoot)) Directory.Delete(_outputRoot, recursive: true);
        }

        private const string FinalPlan = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

### 공통 Tasklet 실행 계약

공통 규약 본문

### S01 스냅샷 생성

S01 본문

### S02 원장 생성

S02 본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문
""";

        private static BatchStepPlan Step(string code, string name) =>
            new(code, name, new[] { "UP_" + code }, new[] { "dbo.T" }, new[] { "-1" }, false);

        private static PlanLayout Layout(IReadOnlyDictionary<string, string>? violations = null) => new(
            "골격",
            new Dictionary<string, string>
            {
                ["S01"] = "### S01 스냅샷 생성\n조각 본문",
                ["S02"] = "### S02 원장 생성\n조각 본문",
            },
            new[] { Step("S01", "스냅샷 생성"), Step("S02", "원장 생성") },
            violations);

        private static SpDefinition SpDef(string name) => new()
        {
            Schema = "dbo",
            Name = name,
            Dependencies = new List<DependencyInfo>
            {
                new() { Database = string.Empty, Schema = "dbo", Name = "TClient", Type = "Table" }
            }
        };

        private BundleInputs Inputs(PlanLayout? layout) => new(
            JobName: "TestJob",
            TargetLanguage: "C#",
            PlanOutcome: VerificationOutcome.Passed,
            FinalPlanMarkdown: FinalPlan,
            Layout: layout,
            SpDefs: new List<SpDefinition> { SpDef("UP_A") },
            Paths: new OutputPathResolver("SettleDB", _outputRoot),
            JobOutputDir: _jobDir);

        [Fact]
        public async Task WriteAsync_ShouldPlaceEntryPointAtAgentRoot()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            Assert.Equal(Path.Combine(_agentDir, "MigrationInstructions.md"), result.EntryPointPath);
            Assert.True(File.Exists(result.EntryPointPath));
        }

        [Fact]
        public async Task WriteAsync_ShouldWriteOneFilePerStep()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            Assert.True(result.StepsSplit);
            Assert.Equal(new[] { "S01", "S02" }, result.StepCodes.OrderBy(c => c));
            Assert.True(File.Exists(Path.Combine(_agentDir, "steps", "S01.md")));
            Assert.True(File.Exists(Path.Combine(_agentDir, "steps", "S02.md")));
        }

        [Fact]
        public async Task WriteAsync_ShouldTakeStepBodiesFromFinalDocument()
        {
            await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            var s01 = await File.ReadAllTextAsync(Path.Combine(_agentDir, "steps", "S01.md"));

            Assert.Contains("S01 본문", s01);
            Assert.DoesNotContain("조각 본문", s01);
        }

        [Fact]
        public async Task WriteAsync_ShouldWriteCommonAndVerificationFiles()
        {
            await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            Assert.Contains("개요 본문",
                await File.ReadAllTextAsync(Path.Combine(_agentDir, "common", "00-architecture.md")));
            Assert.Contains("공통 규약 본문",
                await File.ReadAllTextAsync(Path.Combine(_agentDir, "common", "01-step-contract.md")));
            Assert.Contains("데이터 액세스 경계 규칙",
                await File.ReadAllTextAsync(Path.Combine(_agentDir, "common", "02-data-access-boundary.md")));
            Assert.Contains("검증 SQL 본문",
                await File.ReadAllTextAsync(Path.Combine(_agentDir, "verification", "integrity-sql.md")));
        }

        [Fact]
        public async Task WriteAsync_ShouldBannerOnlyTheViolatingStep()
        {
            var violations = new Dictionary<string, string> { ["S02"] = "의사코드가 없습니다." };

            await new InstructionBundleWriter().WriteAsync(Inputs(Layout(violations)), CancellationToken.None);

            var s01 = await File.ReadAllTextAsync(Path.Combine(_agentDir, "steps", "S01.md"));
            var s02 = await File.ReadAllTextAsync(Path.Combine(_agentDir, "steps", "S02.md"));

            Assert.DoesNotContain("품질 미달", s01);
            Assert.Contains("품질 미달", s02);
            Assert.Contains("의사코드가 없습니다", s02);
        }

        [Fact]
        public async Task WriteAsync_ShouldFallBackToSinglePlanFile_WhenLayoutMissing()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(null), CancellationToken.None);

            Assert.False(result.StepsSplit);
            Assert.False(Directory.Exists(Path.Combine(_agentDir, "steps")));
            Assert.NotEmpty(result.Warnings);

            var entry = await File.ReadAllTextAsync(result.EntryPointPath);
            Assert.Contains("BatchMigrationPlan.md", entry);
        }

        [Fact]
        public async Task WriteAsync_ShouldKeepGuidelinesFirst_EvenInFallback()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(null), CancellationToken.None);

            var entry = await File.ReadAllTextAsync(result.EntryPointPath);
            var guidelines = entry.IndexOf("에이전트 핵심 수행 지침", StringComparison.Ordinal);
            var planLink = entry.IndexOf("BatchMigrationPlan.md", StringComparison.Ordinal);

            Assert.True(guidelines >= 0 && guidelines < planLink);
        }

        [Fact]
        public async Task WriteAsync_ShouldWriteDependencySchemaFiles()
        {
            await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(_jobDir, "raw", "ddl", "dbo.TClient.md")));
        }

        [Fact]
        public async Task WriteAsync_ShouldProduceRelativeLinksThatResolve()
        {
            // Spec.md가 실제로 있는 경우의 분기(File.Exists 참)도 검증한다 - 없을 때의
            // 폴백 분기만 타는 9건짜리 원래 스위트는 이 경로를 한 번도 실행하지 않았다.
            var specDir = Path.Combine(_outputRoot, "Procedures", "dbo.UP_A", "docs");
            Directory.CreateDirectory(specDir);
            await File.WriteAllTextAsync(Path.Combine(specDir, "Spec.md"), "명세서 본문");

            var result = await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);
            var entry = await File.ReadAllTextAsync(result.EntryPointPath);

            // 진입점이 가리키는 링크는 진입점 위치 기준으로 실제 파일에 닿아야 한다.
            // raw/ddl과 Spec.md는 agent/가 아니라 각각 Job 루트와 출력 루트 아래에
            // 있으므로 "../"로 올라간다 - 이 경로가 어긋나면 에이전트는 스키마나
            // 명세서를 영영 읽지 못한다.
            foreach (var relative in new[]
            {
                "common/00-architecture.md", "common/01-step-contract.md",
                "common/02-data-access-boundary.md", "verification/integrity-sql.md",
                "steps/S01.md", "steps/S02.md", "../raw/ddl/dbo.TClient.md",
                "../../../Procedures/dbo.UP_A/docs/Spec.md",
            })
            {
                Assert.Contains(relative, entry);

                var resolved = Path.GetFullPath(
                    Path.Combine(_agentDir, relative.Replace('/', Path.DirectorySeparatorChar)));
                Assert.True(File.Exists(resolved), $"링크 대상이 없다: {relative}");
            }
        }

        [Fact]
        public async Task WriteAsync_ShouldRemoveStaleStepFiles_WhenRerunFallsBackToSingleFile()
        {
            var writer = new InstructionBundleWriter();

            // 1차: 분할 성공 - steps/S01.md, steps/S02.md가 생긴다.
            await writer.WriteAsync(Inputs(Layout()), CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(_agentDir, "steps", "S01.md")));
            Assert.True(File.Exists(Path.Combine(_agentDir, "steps", "S02.md")));

            // 2차: 같은 Job 디렉터리에 목차 없이 재실행 - 단일 파일 폴백.
            var result = await writer.WriteAsync(Inputs(null), CancellationToken.None);

            Assert.False(result.StepsSplit);
            Assert.False(
                Directory.Exists(Path.Combine(_agentDir, "steps")),
                "폴백 전환 후에도 이전 실행의 steps/ 디렉터리가 남아 있다.");
        }

        [Fact]
        public async Task WriteAsync_ShouldRemoveStaleStepFiles_WhenStepSetShrinks()
        {
            var writer = new InstructionBundleWriter();

            // 1차: S01, S02 모두 분할.
            await writer.WriteAsync(Inputs(Layout()), CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(_agentDir, "steps", "S01.md")));
            Assert.True(File.Exists(Path.Combine(_agentDir, "steps", "S02.md")));

            // 2차: 재수립으로 S02가 사라지고 S01만 남은 목차로 재실행.
            var shrunkLayout = new PlanLayout(
                "골격",
                new Dictionary<string, string> { ["S01"] = "### S01 스냅샷 생성\n조각 본문" },
                new[] { Step("S01", "스냅샷 생성") },
                null);

            var result = await writer.WriteAsync(Inputs(shrunkLayout), CancellationToken.None);

            Assert.True(result.StepsSplit);
            Assert.True(File.Exists(Path.Combine(_agentDir, "steps", "S01.md")));
            Assert.False(
                File.Exists(Path.Combine(_agentDir, "steps", "S02.md")),
                "목차에서 빠진 S02의 파일이 정리되지 않고 남아 있다.");
        }

        [Fact]
        public async Task WriteAsync_ShouldWriteTaskFilesFlatUnderAgent()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            Assert.Equal(4, result.TaskFilePaths.Count); // bootstrap + S01 + S02 + assembly
            foreach (var path in result.TaskFilePaths)
            {
                Assert.True(File.Exists(path));
                // agent/ 직하여야 ResolveJobDirectory가 Job 루트를 반환한다.
                Assert.Equal(_agentDir, Path.GetDirectoryName(path));
            }
        }

        [Fact]
        public async Task WriteAsync_ShouldOrderTaskFilesByStructureOrder()
        {
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            var names = result.TaskFilePaths.Select(Path.GetFileName).ToList();

            Assert.Equal(
                new[] { "task-00-bootstrap.md", "task-01-S01.md", "task-02-S02.md", "task-99-assembly.md" },
                names);
        }

        [Fact]
        public async Task WriteAsync_ShouldStillWriteBootstrapAndAssembly_WhenNotSplit()
        {
            // 분할이 실패해도 회차 구조 자체는 유지한다 - 한 세션에 전부 몰아넣는
            // 것이 이 작업이 없애려는 바로 그 문제다.
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(null), CancellationToken.None);

            var names = result.TaskFilePaths.Select(Path.GetFileName).ToList();

            Assert.Contains("task-00-bootstrap.md", names);
            Assert.Contains("task-99-assembly.md", names);
        }
    }
}
