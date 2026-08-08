using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace ReSet.Core.Tests
{
    // 전역 Serilog.Log.Logger를 교체하는 테스트가 있어 병렬 실행에서 분리한다.
    [Collection(GlobalSerilogLoggerCollection.Name)]
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

        private static PlanLayout Layout(IReadOnlyDictionary<string, StepDefect>? violations = null) => new(
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

        private static SpDefinition SpDefWithDependency(string procedureName, string dependencyTable) => new()
        {
            Schema = "dbo",
            Name = procedureName,
            Dependencies = new List<DependencyInfo>
            {
                new() { Database = string.Empty, Schema = "dbo", Name = dependencyTable, Type = "Table" }
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
        public async Task WriteAsync_ShouldWriteHostingAndConfigFile_ForCSharp()
        {
            // 배치 호스팅(Worker Service)과 멀티 DB 연결 문자열(ConnectionStrings) 안내는
            // 구 MetadataExporter §5에 있다가 Phase B 분할 때 유실됐던 것을 여기로 복원했다.
            await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            var hostingConfig = await File.ReadAllTextAsync(
                Path.Combine(_agentDir, "common", "03-hosting-and-config.md"));

            Assert.Contains("Worker Service", hostingConfig);
            Assert.Contains("ConnectionStrings", hostingConfig);
            Assert.DoesNotContain("Spring Batch", hostingConfig);
        }

        [Fact]
        public async Task WriteAsync_ShouldWriteHostingAndConfigFile_ForJava()
        {
            var inputs = Inputs(Layout()) with { TargetLanguage = "Java" };
            await new InstructionBundleWriter().WriteAsync(inputs, CancellationToken.None);

            var hostingConfig = await File.ReadAllTextAsync(
                Path.Combine(_agentDir, "common", "03-hosting-and-config.md"));

            Assert.Contains("Spring Batch", hostingConfig);
            Assert.Contains("application.yml", hostingConfig);
            Assert.DoesNotContain("Worker Service", hostingConfig);
        }

        [Fact]
        public async Task WriteAsync_ShouldNotLinkHostingAndConfigFile_FromEntryPoint()
        {
            // 호스팅/설정 안내는 스캐폴딩을 세우는 Bootstrap 회차 전용이다. 모든 회차가
            // 읽는 진입점 인덱스(00~02)와 달리 여기에는 실리지 않는다 - 링크는
            // task-00-bootstrap.md만 갖는다(TaskFileComposerTests에서 검증).
            var result = await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            var entry = await File.ReadAllTextAsync(result.EntryPointPath);
            Assert.DoesNotContain("03-hosting-and-config.md", entry);
        }

        [Fact]
        public async Task WriteAsync_ShouldBannerOnlyTheViolatingStep()
        {
            var violations = new Dictionary<string, StepDefect>
            {
                ["S02"] = new StepDefect(StepDefectKind.QualityFloor, "의사코드가 없습니다."),
            };

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

        [Fact]
        public async Task WriteAsync_ShouldScopeStepDependenciesToItsOwnTargetTables()
        {
            // 각 단계는 서로 다른 테이블을 건드린다 - S01 지시서에는 S01의 테이블만,
            // S02 지시서에는 S02의 테이블만 있어야 한다. Job 전체 의존성을 그대로
            // 물려주면 "한 회차의 지시서는 그 회차가 읽어야 할 것만 가리켜야 한다"는
            // 원칙이 깨진다.
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string>
                {
                    ["S01"] = "### S01 스냅샷 생성\n조각 본문",
                    ["S02"] = "### S02 원장 생성\n조각 본문",
                },
                new[]
                {
                    new BatchStepPlan("S01", "스냅샷 생성", new[] { "UP_S01" }, new[] { "dbo.TClient" }, new[] { "-1" }, false),
                    new BatchStepPlan("S02", "원장 생성", new[] { "UP_S02" }, new[] { "dbo.TLedger" }, new[] { "-1" }, false),
                },
                null);

            var inputs = Inputs(layout) with
            {
                SpDefs = new List<SpDefinition>
                {
                    SpDefWithDependency("UP_S01", "TClient"),
                    SpDefWithDependency("UP_S02", "TLedger"),
                },
            };

            await new InstructionBundleWriter().WriteAsync(inputs, CancellationToken.None);

            var s01Task = await File.ReadAllTextAsync(Path.Combine(_agentDir, "task-01-S01.md"));
            var s02Task = await File.ReadAllTextAsync(Path.Combine(_agentDir, "task-02-S02.md"));

            Assert.Contains("dbo.TClient", s01Task);
            Assert.DoesNotContain("dbo.TLedger", s01Task);

            Assert.Contains("dbo.TLedger", s02Task);
            Assert.DoesNotContain("dbo.TClient", s02Task);
        }

        [Fact]
        public async Task WriteAsync_ShouldWarnWhenStepDeclaresNoTargetTables()
        {
            // TargetTables가 비면 필터가 통째로 풀려 그 회차만 Job 전체 스키마를
            // 받는다. 실측: POQSettleProcDaily5의 S12가 55개를 받는 동안 나머지는
            // 1개였는데 로그에는 "경고: 0건"으로 끝났다. 바로 아래 matched.Count == 0
            // 폴백에는 경고가 있으므로, 같은 결과를 내는 두 폴백의 관측성이
            // 어긋나 있던 셈이다.
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string>
                {
                    ["S01"] = "### S01 스냅샷 생성\n조각 본문",
                    ["S02"] = "### S02 원장 생성\n조각 본문",
                },
                new[]
                {
                    new BatchStepPlan("S01", "스냅샷 생성", new[] { "UP_S01" }, new[] { "dbo.TClient" }, new[] { "-1" }, false),
                    new BatchStepPlan("S02", "원장 생성", new[] { "UP_S02" }, Array.Empty<string>(), new[] { "-1" }, false),
                },
                null);

            var inputs = Inputs(layout) with
            {
                SpDefs = new List<SpDefinition>
                {
                    SpDefWithDependency("UP_S01", "TClient"),
                    SpDefWithDependency("UP_S02", "TLedger"),
                },
            };

            var sink = new CapturingSink();
            var previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(sink).CreateLogger();
            try
            {
                await new InstructionBundleWriter().WriteAsync(inputs, CancellationToken.None);
            }
            finally
            {
                Log.CloseAndFlush();
                Log.Logger = previousLogger;
            }

            Assert.Contains(sink.Messages, m => m.Contains("S02") && m.Contains("TargetTables"));

            // 폴백 자체는 유지한다 - 좁히지 못했다고 스키마를 통째로 빼앗으면
            // 그 회차는 컬럼을 확인할 방법이 아예 없어진다.
            var s02Task = await File.ReadAllTextAsync(Path.Combine(_agentDir, "task-02-S02.md"));
            Assert.Contains("dbo.TClient", s02Task);
            Assert.Contains("dbo.TLedger", s02Task);
        }

        [Fact]
        public async Task WriteAsync_ShouldNotIncludeDependenciesSection_InBootstrapTaskFile()
        {
            // 부트스트랩은 "단계 상세 문서를 읽지 마십시오"라고 지시하면서 작업
            // 전체 스키마를 붙이면 모순이다. 부트스트랩 지시서에는 스키마 섹션 자체가
            // 없어야 한다.
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string> { ["S01"] = "### S01 스냅샷 생성\n조각 본문" },
                new[] { new BatchStepPlan("S01", "스냅샷 생성", new[] { "UP_S01" }, new[] { "dbo.TClient" }, new[] { "-1" }, false) },
                null);

            var inputs = Inputs(layout) with
            {
                SpDefs = new List<SpDefinition> { SpDefWithDependency("UP_S01", "TClient") },
            };

            await new InstructionBundleWriter().WriteAsync(inputs, CancellationToken.None);

            var bootstrap = await File.ReadAllTextAsync(Path.Combine(_agentDir, "task-00-bootstrap.md"));

            Assert.DoesNotContain("## 참조할 스키마", bootstrap);
        }

        /// <summary>
        /// 단계 코드는 AI가 만든 목차에서 온 자유 문자열이다. steps/{코드}.md 쓰기 경로만
        /// 정화를 건너뛰고 있어 "../"나 경로 구분자가 든 코드로 agent/steps/ 바깥에 파일을
        /// 쓸 수 있었다. task-*.md 쪽에는 이미 회귀 테스트가 있었고 steps/ 쪽만 없었다.
        /// </summary>
        [Theory]
        [InlineData("../evil")]
        [InlineData("a/b")]
        [InlineData("S01:회원")]
        public async Task WriteAsync_ShouldSanitizeStepFileNames(string unsafeCode)
        {
            var plan = $"""
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

### {unsafeCode} 위험한 코드

본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문
""";

            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string> { [unsafeCode] = $"### {unsafeCode} 위험한 코드\n조각" },
                new[] { Step(unsafeCode, "위험한 코드") },
                null);

            var result = await new InstructionBundleWriter().WriteAsync(
                Inputs(layout) with { FinalPlanMarkdown = plan }, CancellationToken.None);

            Assert.True(result.StepsSplit);

            var safeCode = TaskFileComposer.SanitizeStepCode(unsafeCode);
            var stepsDir = Path.Combine(_agentDir, "steps");

            Assert.Equal(new[] { safeCode }, result.StepCodes);
            Assert.True(File.Exists(Path.Combine(stepsDir, safeCode + ".md")));

            // 정화하지 않았다면 쓰였을 경로가 실제로 비어 있어야 한다.
            Assert.False(File.Exists(Path.GetFullPath(Path.Combine(stepsDir, unsafeCode + ".md"))));
            Assert.Single(Directory.GetFiles(stepsDir, "*.md"));

            // 이 번들이 만든 모든 마크다운이 Job 디렉터리 안에 남아야 한다.
            foreach (var written in Directory.GetFiles(_outputRoot, "*.md", SearchOption.AllDirectories))
            {
                Assert.StartsWith(_jobDir + Path.DirectorySeparatorChar, Path.GetFullPath(written));
            }
        }

        /// <summary>
        /// steps/{코드}.md와 task-NN-{코드}.md가 같은 이름을 써야 재구동 경로(메뉴 3)가
        /// 둘을 짝지을 수 있다. 정화가 코드를 바꾸는 정상 번들이 Broken으로 거부되던
        /// 막다른 길이 여기서 닫힌다.
        /// </summary>
        [Fact]
        public async Task WriteAsync_ShouldNameStepFilesAndTaskFilesWithTheSameCode()
        {
            var plan = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

### S01: 회원 이관

본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문
""";

            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string> { ["S01: 회원"] = "### S01: 회원 이관\n조각" },
                new[] { Step("S01: 회원", "회원 이관") },
                null);

            var result = await new InstructionBundleWriter().WriteAsync(
                Inputs(layout) with { FinalPlanMarkdown = plan }, CancellationToken.None);

            var safeCode = TaskFileComposer.SanitizeStepCode("S01: 회원");
            var stepTaskFile = result.TaskFilePaths.Single(path =>
                TaskFileComposer.ParseStageIdentity(Path.GetFileNameWithoutExtension(path)).Kind == StageKind.Step);

            Assert.Equal(
                safeCode,
                TaskFileComposer.ParseStageIdentity(Path.GetFileNameWithoutExtension(stepTaskFile)).StepCode);
            Assert.True(File.Exists(Path.Combine(_agentDir, "steps", safeCode + ".md")));

            // 회차 지시서가 가리키는 단계 상세 링크도 같은 이름이어야 한다.
            Assert.Contains($"steps/{safeCode}.md", await File.ReadAllTextAsync(stepTaskFile));
        }

        /// <summary>
        /// 정화가 서로 다른 두 코드를 같은 이름으로 뭉개면 한 단계의 상세가 다른 단계의
        /// 파일을 덮어쓰고, 두 회차가 같은 소스 파일로 게이트를 통과한다. 부분 분할을
        /// 하지 않는다는 규칙과 같은 이유로 그때는 분할 자체를 포기한다.
        /// </summary>
        [Fact]
        public async Task WriteAsync_ShouldFallBackToSinglePlan_WhenSanitizedStepCodesCollide()
        {
            var plan = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

### S01. 스냅샷

본문 1

### S01: 원장

본문 2

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문
""";

            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string>
                {
                    ["S01."] = "### S01. 스냅샷\n조각",
                    ["S01:"] = "### S01: 원장\n조각",
                },
                new[] { Step("S01.", "스냅샷"), Step("S01:", "원장") },
                null);

            var result = await new InstructionBundleWriter().WriteAsync(
                Inputs(layout) with { FinalPlanMarkdown = plan }, CancellationToken.None);

            Assert.False(result.StepsSplit);
            Assert.Empty(result.StepCodes);
            Assert.False(Directory.Exists(Path.Combine(_agentDir, "steps")));
            Assert.Contains(result.Warnings, warning => warning.Contains("같은 이름"));

            // 폴백이므로 회차는 부트스트랩과 조립뿐이고, 진입점은 계획서 전문을 가리킨다.
            Assert.Equal(2, result.TaskFilePaths.Count);
            Assert.Contains(
                "BatchMigrationPlan.md",
                await File.ReadAllTextAsync(result.EntryPointPath));
        }

        private sealed class CapturingSink : ILogEventSink
        {
            public List<string> Messages { get; } = new();
            public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
        }
    }
}
