using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Spectre.Console;

namespace ReSet.Cli
{
    /// <summary>
    /// `--probe-anchors`. 얼어붙은 Job 하나를 재료로, 명세서가 SELECT 행을 선언한
    /// 단계만 <b>첫 생성 한 번씩</b> 실물로 돌려 앵커 계약이 듣는지 잰다.
    ///
    /// [왜 이 자가 있나 - 2026-09-13]
    /// 프롬프트 계약을 한 줄 고칠 때마다 판정에 4 시간(`--plan-only` 한 판)이 들었다.
    /// 그 판이 주는 것은 배송본이지만, 계약이 묻는 것은 「프롬프트가 모델을 그 모양으로
    /// 이끄는가」다 - 그 질문에는 단계당 호출 한 번이면 답이 나온다.
    ///
    /// [스텁이 아니다] 실제 파이프라인이 부르는 <see cref="IAiService.GenerateBatchStepSectionAsync"/>
    /// 를 그 재료로 부른다. 재료 다섯을 얼린 Job 에서 복원하되 `floorFeedback` 과
    /// `previousBody` 만 null 로 둔다 - 그 둘이 「첫 생성」 축을 만든다.
    ///
    /// [산출물을 output/Jobs 밖에 쓰는 이유] 거기 쓰면 코퍼스 시험이 프로브 결과를
    /// 실물 산출물로 읽는다. 재는 자가 재는 대상을 만들어내면 그 수는 아무것도
    /// 말하지 않는다.
    /// </summary>
    public static class AnchorProbeCommand
    {
        public static async Task<int> RunAsync(
            string outputRoot,
            string sourceJob,
            IReadOnlyList<string> entryPoints,
            IAiService aiService,
            string targetLanguage,
            string? effort,
            CancellationToken cancellationToken)
        {
            var jobDir = Path.Combine(outputRoot, "Jobs", sourceJob);
            var planPath = Path.Combine(jobDir, "raw", "PlanStructure.md");
            if (!File.Exists(planPath))
            {
                AnsiConsole.MarkupLine($"[red]에러: 목차가 없습니다 - {Markup.Escape(planPath)}[/]");
                return 1;
            }

            var skeletonPath = NewestSkeletonPath(jobDir);
            if (skeletonPath == null)
            {
                // 공통 규약이 빠지면 단계 프롬프트가 실물과 달라진다. 그 차이를 안고
                // 잰 수는 「계약이 듣는가」가 아니라 「규약 없이도 듣는가」의 답이다.
                AnsiConsole.MarkupLine(
                    $"[red]에러: {Markup.Escape(sourceJob)} 의 골격(raw/attempts/run-*/skeleton.md)을 찾지 못했습니다.[/]");
                AnsiConsole.MarkupLine("[grey]공통 규약 없이 재면 실물과 다른 프롬프트를 재게 됩니다.[/]");
                return 1;
            }

            var planMarkdown = await File.ReadAllTextAsync(planPath, cancellationToken);
            var steps = BatchStepPlanParser.TryParse(planMarkdown);
            if (steps == null || steps.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]에러: 목차에서 단계 목록을 읽지 못했습니다.[/]");
                return 1;
            }

            var conventions = BatchPlanAssembler.ExtractSharedConventions(
                await File.ReadAllTextAsync(skeletonPath, cancellationToken));

            var materials = await PlanOnlyMaterialLoader.LoadAsync(outputRoot, entryPoints, cancellationToken);
            if (materials.Specs.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]에러: 명세서 재료가 없습니다.[/]");
                return 1;
            }

            var facts = SpecStatementFactsExtractor.Extract(materials.Specs);
            var selected = AnchorProbe.SelectSteps(steps, facts);
            if (selected.Count == 0)
            {
                // 표본 0 을 성공으로 내면 「재지 않았다」가 「합격」으로 읽힌다.
                AnsiConsole.MarkupLine(
                    "[red]에러: 명세서가 SELECT 행을 선언한 단계가 없습니다 - 잴 대상이 없습니다.[/]");
                return 1;
            }

            var parameters = StepInterfaceFacts.CollectParameters(materials.Definitions);
            var stepInterfaces = StepInterfaceFacts.Build(steps, parameters);
            var callGraph = StepInterfaceFacts.BuildCallGraph(materials.Definitions);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var probeDir = Path.Combine(outputRoot, "probes", $"{sourceJob}-{stamp}");
            Directory.CreateDirectory(probeDir);

            AnsiConsole.MarkupLine(
                $"[bold blue]=== 앵커 프로브 ({Markup.Escape(sourceJob)}) - 단계 {selected.Count}개 · 첫 생성만 ===[/]");
            AnsiConsole.MarkupLine($"[grey]결과: {Markup.Escape(probeDir)}[/]");

            var outcomes = new List<AnchorProbeStepOutcome>();
            foreach (var (step, declared) in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AnsiConsole.MarkupLine(
                    $"[grey]{Markup.Escape(step.Code)} · 선언 서수 {string.Join(", ", declared)} 생성 중…[/]");

                var result = await aiService.GenerateBatchStepSectionAsync(
                    step, steps, conventions, materials.Specs, stepInterfaces, targetLanguage,
                    sourceJob + "-probe", effort,
                    floorFeedback: null, previousBody: null,
                    callGraph: callGraph, cancellationToken: cancellationToken);

                var body = result?.Content ?? string.Empty;
                await File.WriteAllTextAsync(
                    Path.Combine(probeDir, step.Code + ".md"), body, cancellationToken);

                var outcome = AnchorProbe.Judge(step.Code, declared, body);
                outcomes.Add(outcome);

                AnsiConsole.MarkupLine(
                    $"  앵커 {outcome.Anchors.Count} · 놓친 선언 {outcome.MissedDeclarations.Count} · " +
                    $"지어낸 서수 {outcome.FabricatedOrdinals.Count} · 접어 넣음 {outcome.FoldedCount}");
            }

            var report = AnchorProbe.Summarize(outcomes);
            var text = Render(sourceJob, planPath, skeletonPath, report, outcomes);
            await File.WriteAllTextAsync(Path.Combine(probeDir, "REPORT.md"), text, cancellationToken);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[bold]도달률 {report.Reached}/{report.StepsProbed} · 놓친 선언 행 {report.MissedDeclarationRows} · " +
                $"지어낸 서수 {report.FabricatedOrdinalCount} · 접어 넣음 {report.FoldedCount}[/]");

            // 종료 코드로 판정을 내보낸다 - 사람이 표를 안 읽어도 CI 가 읽는다.
            return report.Reached == report.StepsProbed
                && report.FabricatedOrdinalCount == 0
                && report.FoldedCount == 0 ? 0 : 2;
        }

        /// <summary>가장 최근 판의 골격. 판이 여럿이면 마지막이 그 Job 을 만든 것이다.</summary>
        private static string? NewestSkeletonPath(string jobDir)
        {
            var attempts = Path.Combine(jobDir, "raw", "attempts");
            if (!Directory.Exists(attempts)) return null;

            return Directory.EnumerateDirectories(attempts, "run-*")
                .OrderBy(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "skeleton.md"))
                .LastOrDefault(File.Exists);
        }

        private static string Render(
            string sourceJob, string planPath, string skeletonPath,
            AnchorProbeReport report, IReadOnlyList<AnchorProbeStepOutcome> outcomes)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 앵커 프로브 - {sourceJob}");
            sb.AppendLine();
            sb.AppendLine($"- 시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("- 축: **첫 생성만**(재시도·자가수정 없음). 배송본 판정이 아니다.");
            // 입력 지문을 적는다 - 판 사이에 재료가 바뀌었으면 두 판의 수를 나란히
            // 놓을 수 없다. 「무엇 대비」를 산출물 자신이 들고 있게 한다.
            sb.AppendLine($"- 목차: `{planPath}` (md5 {Md5(planPath)})");
            sb.AppendLine($"- 골격: `{skeletonPath}` (md5 {Md5(skeletonPath)})");
            sb.AppendLine();
            sb.AppendLine($"**도달률 {report.Reached}/{report.StepsProbed} · 놓친 선언 행 {report.MissedDeclarationRows} " +
                          $"· 지어낸 서수 {report.FabricatedOrdinalCount} · 접어 넣음 {report.FoldedCount}**");
            sb.AppendLine();
            sb.AppendLine("| 단계 | 선언 | 앵커 | 놓침 | 지어냄 | 접힘 |");
            sb.AppendLine("| :--- | :--- | :--- | ---: | ---: | ---: |");
            foreach (var o in outcomes)
            {
                sb.AppendLine(
                    $"| {o.StepCode} | {Join(o.Declared)} | {Join(o.Anchors)} | " +
                    $"{o.MissedDeclarations.Count} | {o.FabricatedOrdinals.Count} | {o.FoldedCount} |");
            }

            return sb.ToString();
        }

        private static string Join(IReadOnlyList<int> values) =>
            values.Count == 0 ? "—" : string.Join(", ", values);

        private static string Md5(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant()[..12];
        }
    }
}
