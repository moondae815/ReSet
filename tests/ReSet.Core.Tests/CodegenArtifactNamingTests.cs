using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using ReSet.Validator.Core.Models;
using ReSet.Validator.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 조립 회차 지시서가 말하는 이름 규약과, Job 전체 검증이 실제로 판정하는 이름 규약이
    /// 같은지 교차 검증한다.
    ///
    /// 이 두 쪽이 갈라져 있던 동안 <b>모든 회차가 통과한 성공 실행</b>이 매핑 0건 →
    /// Unrecoverable → 조립 실패로 끝났다. 규약이 어느 지시서에도 적혀 있지 않았기
    /// 때문이다. 문구만 고치고 판정부를 잊거나 그 반대가 되면 같은 실패가 되살아나므로,
    /// 여기서는 <b>지시서 문구에 등장하는 이름으로 실제 파일/디렉터리를 만들어</b>
    /// FileMappingService가 그것을 짝지어 주는지까지 확인한다.
    /// </summary>
    public class CodegenArtifactNamingTests : IDisposable
    {
        private const string JobName = "SETTLE_ProcDaily";

        private readonly string _root;
        private readonly string _specDir;
        private readonly string _codeDir;

        public CodegenArtifactNamingTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "reset-naming-" + Guid.NewGuid().ToString("N"));
            // 자동 탐색은 계획서의 "조부모 디렉터리 이름"을 매핑명으로 삼는다.
            // 실제 배치와 같은 <job>/docs/BatchMigrationPlan.md 형태를 그대로 만든다.
            _specDir = Path.Combine(_root, "Jobs", JobName, "docs");
            _codeDir = Path.Combine(_root, "Jobs", JobName, "src");
            Directory.CreateDirectory(_specDir);
            Directory.CreateDirectory(_codeDir);
            File.WriteAllText(Path.Combine(_specDir, "BatchMigrationPlan.md"), "# 계획서");
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private ValidatorConfig Config() => new()
        {
            SpecDirectory = _specDir,
            SourceCodeDirectory = _codeDir,
            OutputDirectory = Path.Combine(_root, "validation"),
        };

        private static string AssemblyTaskFile(string targetLanguage) => TaskFileComposer.Compose(new TaskFileInputs(
            Kind: StageKind.Assembly,
            JobName: JobName,
            TargetLanguage: targetLanguage,
            StepCode: null,
            StepName: null,
            StepRelativePath: null,
            SpecRelativePath: null,
            Dependencies: Array.Empty<IndexEntry>(),
            HasStepContract: true,
            HasVerification: false,
            FailedStepCodes: Array.Empty<string>(),
            SinglePlanRelativePath: null));

        [Fact]
        public void AssemblyTaskFile_ShouldStateTheNamingConventionTheJobWideGateApplies()
        {
            var taskFile = AssemblyTaskFile("C#");

            Assert.Contains("산출물 이름 규약", taskFile);
            Assert.Contains($"`{JobName}.cs`", taskFile);

            foreach (var directory in CodegenArtifactNaming.JobProjectDirectoryNames(JobName))
            {
                Assert.Contains($"`{directory}/`", taskFile);
            }
        }

        [Fact]
        public void AssemblyTaskFile_ShouldUseJavaExtensionForJavaTargets()
        {
            Assert.Contains($"`{JobName}.java`", AssemblyTaskFile("Java"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void EveryDirectoryNameTheTaskFileNames_ShouldBeAcceptedByTheJobWideGate(int index)
        {
            // 지시서가 나열한 디렉터리 이름 하나하나가 실제로 매핑을 성사시켜야 한다.
            // 하나라도 판정부가 모르면 에이전트가 지시대로 만들고도 조립에서 떨어진다.
            var names = CodegenArtifactNaming.JobProjectDirectoryNames(JobName);
            Assert.Contains($"`{names[index]}/`", AssemblyTaskFile("C#"));

            Directory.CreateDirectory(Path.Combine(_codeDir, names[index]));

            var mapping = Assert.Single(new FileMappingService().ResolveMappings(Config()));

            Assert.Equal(Path.Combine(_codeDir, names[index]), mapping.SourceCodePath);
        }

        [Fact]
        public void TheEntryPointFileNameTheTaskFileNames_ShouldBeAcceptedByTheJobWideGate()
        {
            var expected = Path.Combine(_codeDir, CodegenArtifactNaming.JobEntryPointFileBaseName(JobName) + ".cs");
            File.WriteAllText(expected, "public class Job { }");

            var mapping = Assert.Single(new FileMappingService().ResolveMappings(Config()));

            Assert.Equal(expected, mapping.SourceCodePath);
        }

        [Fact]
        public void AFullyImplementedJobFollowingTheConvention_ShouldNotProduceZeroMappings()
        {
            // C1의 본체: 모든 회차가 통과한 실행에서 조립 게이트가 0건을 받아
            // Unrecoverable로 떨어지던 자리다. 규약대로 만들면 0건이 아니어야 한다.
            Directory.CreateDirectory(Path.Combine(_codeDir, $"{JobName.Replace("_", string.Empty)}.Batch"));
            File.WriteAllText(
                Path.Combine(_codeDir, $"{JobName.Replace("_", string.Empty)}.Batch", "Program.cs"),
                "public class Program { }");

            Assert.NotEmpty(new FileMappingService().ResolveMappings(Config()));
        }

        [Fact]
        public void StepTaskFile_ShouldStateThePrefixRuleTheStepGateApplies()
        {
            // 단계 게이트는 접두사, 조립 게이트는 완전 일치/디렉터리다. 규칙 모양이
            // 다르므로 두 문구 모두 자기 게이트를 명시해야 한다 - 한쪽 문구를 일반화해
            // 다른 회차에 적용하면 통과하지 못한다.
            var stepText = CodegenArtifactNaming.DescribeStepArtifactNaming("S01", "C#");
            var assemblyText = CodegenArtifactNaming.DescribeJobArtifactNaming(JobName, "C#");

            Assert.Contains("`S01`로 시작", stepText);
            Assert.Contains("`S01Tasklet.cs`", stepText);
            Assert.Contains("Job 전체 검증", assemblyText);
            Assert.DoesNotContain("로 시작", assemblyText);
        }

        [Fact]
        public void StepPrefixTheTaskFileStates_ShouldBeTheNameTheStepGateMatchesOn()
        {
            // 지시서가 알려 주는 접두사는 정화된 코드여야 한다 - 검증기가 대조하는
            // MappedName(CodegenStage.StepCode)이 그 값이기 때문이다.
            const string rawCode = "S01: 회원";
            var safeCode = TaskFileComposer.SanitizeStepCode(rawCode);

            var taskFile = TaskFileComposer.Compose(new TaskFileInputs(
                Kind: StageKind.Step,
                JobName: JobName,
                TargetLanguage: "C#",
                StepCode: rawCode,
                StepName: "회원 이관",
                StepRelativePath: $"steps/{safeCode}.md",
                SpecRelativePath: null,
                Dependencies: Array.Empty<IndexEntry>(),
                HasStepContract: true,
                HasVerification: false,
                FailedStepCodes: Array.Empty<string>(),
                SinglePlanRelativePath: null));

            Assert.Contains($"`{safeCode}`로 시작", taskFile);

            // 실제 매칭기가 그 접두사를 인정하는지까지 확인한다.
            var spec = Path.Combine(_specDir, "step.md");
            File.WriteAllText(spec, "### 단계");
            File.WriteAllText(Path.Combine(_codeDir, safeCode + "Tasklet.cs"), "class T {}");

            var results = new FileMappingService().ResolveMappings(
                Config(), new[] { new ExplicitPair(spec, safeCode, null) });

            Assert.Single(results);
        }

        [Fact]
        public void JobProjectDirectoryNames_ShouldNotRepeatWhenJobNameHasNoUnderscore()
        {
            var names = CodegenArtifactNaming.JobProjectDirectoryNames("SettleProcDaily");

            Assert.Equal(names.Distinct(StringComparer.Ordinal).Count(), names.Count);
            Assert.Contains("SettleProcDaily.Batch", names);
            Assert.Contains("SettleProcDaily", names);
        }
    }
}
