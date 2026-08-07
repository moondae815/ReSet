using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public sealed record BundleInputs(
        string JobName,
        string TargetLanguage,
        VerificationOutcome PlanOutcome,
        string FinalPlanMarkdown,
        PlanLayout? Layout,
        IReadOnlyList<SpDefinition> SpDefs,
        OutputPathResolver Paths,
        string JobOutputDir);

    /// <param name="StepCodes">실제로 파일이 쓰인 단계 코드. 회차 정의의 근거가 된다.</param>
    public sealed record BundleResult(
        string EntryPointPath,
        IReadOnlyList<string> StepCodes,
        IReadOnlyList<string> Warnings,
        bool StepsSplit);

    /// <summary>
    /// 코딩 에이전트에 넘길 `agent/` 번들을 디스크에 쓴다.
    ///
    /// 이전에는 계획서 전문(7,661줄)을 진입점 한 파일에 인라인했다. 그러면 에이전트가
    /// 읽어야 할 입력이 253k 토큰이 되어 코드를 쓰기 전에 컨텍스트가 찬다. 여기서
    /// 계획을 파일로 나누고, 진입점은 인덱스만 남긴다.
    /// </summary>
    public sealed class InstructionBundleWriter
    {
        public async Task<BundleResult> WriteAsync(BundleInputs inputs, CancellationToken cancellationToken = default)
        {
            var agentDir = Path.Combine(inputs.JobOutputDir, "agent");
            Directory.CreateDirectory(agentDir);

            var slices = PlanBoundaryResolver.Resolve(inputs.FinalPlanMarkdown, inputs.Layout);
            var warnings = new List<string>(slices.Warnings);

            var commonDir = Path.Combine(agentDir, "common");
            Directory.CreateDirectory(commonDir);

            await WriteAsync(Path.Combine(commonDir, "00-architecture.md"), slices.Architecture, cancellationToken);

            if (slices.StepContract != null)
            {
                await WriteAsync(Path.Combine(commonDir, "01-step-contract.md"), slices.StepContract, cancellationToken);
            }

            // 경계 규칙은 계획서가 아니라 DataAccessPolicy에서 온다. 계획 분할이 실패해도
            // 이 파일은 언제나 존재한다 - 규칙 없이 코드를 쓰게 두지 않는다.
            await WriteAsync(
                Path.Combine(commonDir, "02-data-access-boundary.md"),
                "# 데이터 액세스 경계 규칙\n\n" + DataAccessPolicy.InstructionRules(inputs.TargetLanguage),
                cancellationToken);

            if (slices.Verification != null)
            {
                var verificationDir = Path.Combine(agentDir, "verification");
                Directory.CreateDirectory(verificationDir);
                await WriteAsync(
                    Path.Combine(verificationDir, "integrity-sql.md"), slices.Verification, cancellationToken);
            }

            var stepCodes = new List<string>();
            var stepIndex = new List<IndexEntry>();

            if (slices.StepsSplit)
            {
                var stepsDir = Path.Combine(agentDir, "steps");
                Directory.CreateDirectory(stepsDir);

                foreach (var code in OrderedStepCodes(inputs.Layout, slices.Steps))
                {
                    var body = slices.Steps[code];
                    var banner = BuildFloorBanner(inputs.Layout, code);
                    await WriteAsync(
                        Path.Combine(stepsDir, $"{code}.md"), banner + body, cancellationToken);

                    stepCodes.Add(code);
                    stepIndex.Add(new IndexEntry(DescribeStep(inputs.Layout, code), $"steps/{code}.md"));
                }
            }

            var dependencies = await WriteDependencySchemasAsync(inputs, agentDir, cancellationToken);
            var specs = BuildSpecIndex(inputs, agentDir);

            var entryPoint = InstructionEntryPointComposer.Compose(new EntryPointInputs(
                inputs.JobName,
                inputs.TargetLanguage,
                inputs.PlanOutcome,
                slices.Preamble,
                slices.StepsSplit,
                stepIndex,
                dependencies,
                specs,
                HasStepContract: slices.StepContract != null,
                HasVerification: slices.Verification != null,
                SinglePlanRelativePath: slices.StepsSplit ? null : RelativeToAgent(agentDir,
                    Path.Combine(inputs.JobOutputDir, "docs", "BatchMigrationPlan.md"))));

            var entryPointPath = Path.Combine(agentDir, "MigrationInstructions.md");
            await WriteAsync(entryPointPath, entryPoint, cancellationToken);

            Log.Information(
                "지시서 번들을 작성했습니다 - Job: {JobName}, 단계 분할: {StepsSplit}, 단계 수: {StepCount}개, 경고: {WarningCount}건",
                inputs.JobName, slices.StepsSplit, stepCodes.Count, warnings.Count);

            return new BundleResult(entryPointPath, stepCodes, warnings, slices.StepsSplit);
        }

        /// <summary>
        /// 목차가 선언한 순서를 따른다. 사전 순으로 정렬하면 S10이 S2 앞에 오는 식으로
        /// 회차 순서가 실행 의존성과 어긋난다.
        /// </summary>
        private static IReadOnlyList<string> OrderedStepCodes(
            PlanLayout? layout, IReadOnlyDictionary<string, string> steps)
        {
            if (layout?.Steps is { Count: > 0 })
            {
                var ordered = layout.Steps
                    .Select(step => step.Code)
                    .Where(steps.ContainsKey)
                    .ToList();

                if (ordered.Count == steps.Count)
                {
                    return ordered;
                }
            }

            return steps.Keys.ToList();
        }

        private static string DescribeStep(PlanLayout? layout, string code)
        {
            var name = layout?.Steps?.FirstOrDefault(step =>
                string.Equals(step.Code, code, StringComparison.OrdinalIgnoreCase))?.Name;

            return string.IsNullOrWhiteSpace(name) ? code : $"{code} {name}";
        }

        /// <summary>
        /// 하한 미달 기록이 있는 단계에만 배너를 붙인다. 이전에는 문서 전체 상단에
        /// 배너 하나만 있어 어느 단계가 부실한지 에이전트가 알 수 없었다.
        /// </summary>
        private static string BuildFloorBanner(PlanLayout? layout, string code)
        {
            if (layout?.FloorViolations == null ||
                !layout.FloorViolations.TryGetValue(code, out var reason) ||
                string.IsNullOrWhiteSpace(reason))
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine("> ⚠️ **이 단계는 품질 미달로 기록되었습니다.**");
            sb.AppendLine("> ");
            sb.AppendLine($"> {reason.Trim()}");
            sb.AppendLine("> ");
            sb.AppendLine("> 이 절만으로 구현이 불가능하면 추측하지 말고 원본 명세서(Spec.md)를 확인하십시오.");
            sb.AppendLine();
            return sb.ToString();
        }

        private static async Task<List<IndexEntry>> WriteDependencySchemasAsync(
            BundleInputs inputs, string agentDir, CancellationToken cancellationToken)
        {
            var rawDdlDir = Path.Combine(inputs.JobOutputDir, "raw", "ddl");
            Directory.CreateDirectory(rawDdlDir);

            var distinct = inputs.SpDefs
                .SelectMany(sp => sp.Dependencies)
                .GroupBy(d => $"{d.Database}.{d.Schema}.{d.Name}")
                .Select(g => g.First())
                .ToList();

            var entries = new List<IndexEntry>();

            foreach (var dep in distinct)
            {
                var cleanName = string.IsNullOrEmpty(dep.Database)
                    ? $"{dep.Schema}.{dep.Name}"
                    : $"{dep.Database}.{dep.Schema}.{dep.Name}";

                var filePath = Path.Combine(rawDdlDir, $"{cleanName}.md");
                var sb = new StringBuilder();
                sb.AppendLine($"# {dep.Type}: {cleanName}");
                sb.AppendLine();

                if (dep.Columns.Count > 0)
                {
                    sb.AppendLine(MetadataExporter.FormatTableSchemaToMarkdown(dep));
                }

                if (!string.IsNullOrEmpty(dep.ReferencedDdlText))
                {
                    sb.AppendLine("## Referenced SQL DDL:");
                    sb.AppendLine("```sql");
                    sb.AppendLine(dep.ReferencedDdlText);
                    sb.AppendLine("```");
                }

                await WriteAsync(filePath, sb.ToString(), cancellationToken);
                entries.Add(new IndexEntry(cleanName, RelativeToAgent(agentDir, filePath)));
            }

            return entries;
        }

        private static List<IndexEntry> BuildSpecIndex(BundleInputs inputs, string agentDir)
        {
            var entries = new List<IndexEntry>();

            foreach (var spDef in inputs.SpDefs)
            {
                var objectKey = spDef.ObjectKey ?? CodeObjectKey.Create(
                    inputs.Paths.CurrentDatabase, spDef.Schema, spDef.Name, CodeObjectType.Procedure);
                var specPath = inputs.Paths.ResolveSpecPath(objectKey);
                var label = $"{spDef.Schema}.{spDef.Name}";

                entries.Add(File.Exists(specPath)
                    ? new IndexEntry(label, RelativeToAgent(agentDir, specPath))
                    : new IndexEntry($"{label} (명세서 파일 없음 — 이 단계의 비즈니스 로직은 참조할 수 없습니다)", "#"));
            }

            return entries;
        }

        private static string RelativeToAgent(string agentDir, string absolutePath) =>
            Path.GetRelativePath(agentDir, absolutePath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');

        private static Task WriteAsync(string path, string content, CancellationToken cancellationToken) =>
            File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
    }
}
