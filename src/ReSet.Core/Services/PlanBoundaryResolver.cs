using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 단계 경계 결정의 결과.
    /// </summary>
    /// <param name="Steps">단계 코드 → 최종 문서에서 잘라낸 본문. 실패하면 비어 있다.</param>
    /// <param name="Split">분할에 성공했는가. false면 호출부는 단일 파일 폴백을 취한다.</param>
    /// <param name="Warnings">사용자에게 보여줄 경고. 성공해도 2순위로 내려왔다면 비어 있지 않다.</param>
    /// <param name="FirstStepLineIndex">첫 단계 헤딩의 줄 인덱스. 공통 규약을 잘라내는
    /// 끝점이 된다. 분할에 실패하면 -1이다.</param>
    public sealed record StepBoundaryResult(
        IReadOnlyDictionary<string, string> Steps,
        bool Split,
        IReadOnlyList<string> Warnings,
        int FirstStepLineIndex);

    /// <summary>
    /// 최종 계획서를 산출물 파일 단위로 자른 결과.
    /// </summary>
    /// <param name="Preamble">첫 H2 앞의 내용. L1Exhausted 배너가 여기 실린다.</param>
    /// <param name="Architecture">개요 + Mermaid 흐름도. 골격 분할이 실패하면 계획서 전문.</param>
    /// <param name="StepContract">모든 단계가 공유하는 실행 계약. 잘라내지 못했으면 null.</param>
    /// <param name="Verification">정합성 검증 SQL 세트. 잘라내지 못했으면 null.</param>
    /// <param name="Steps">단계 코드 → 본문. 분할에 실패하면 비어 있다.</param>
    public sealed record PlanSlices(
        string Preamble,
        string Architecture,
        string? StepContract,
        string? Verification,
        IReadOnlyDictionary<string, string> Steps,
        bool SkeletonSplit,
        bool StepsSplit,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// 최종 계획서에서 단계별 경계를 찾아 본문을 잘라낸다.
    ///
    /// 핵심 규칙: <b>조각(PlanLayout.Sections)은 앵커로만 쓰고 본문은 언제나 최종
    /// 문서에서 잘라낸다.</b> split.Markdown이 나온 뒤에도 최종 문서는 L1 정제·자가
    /// 교정·구제 채택으로 계속 바뀌므로, 조각 본문을 실으면 BatchMigrationPlan.md와
    /// steps/*.md가 조용히 달라진다. 그 불일치는 코딩 에이전트가 옛 로직을 구현하게
    /// 만들고, 아무도 그것을 알아채지 못한다.
    ///
    /// 정규식으로 `### S\d\d` 같은 패턴을 잡지 않는다. BatchStepPlan의 주석이 이미
    /// 실측으로 반증했다 - 단계가 H3에 오기도 H4에 오기도 하고, 한 헤딩이 여러 단계를
    /// 묶기도 한다.
    /// </summary>
    public static class PlanBoundaryResolver
    {
        private static readonly IReadOnlyDictionary<string, string> NoSteps =
            new Dictionary<string, string>();

        public static StepBoundaryResult ResolveSteps(string finalPlanMarkdown, PlanLayout? layout)
        {
            var warnings = new List<string>();
            var lines = MarkdownSectionLocator.SplitLines(finalPlanMarkdown);

            // 1순위: 조각의 첫 헤딩을 앵커로 쓴다. 조각이 어느 단계에서 왔는지 알기
            // 때문에 중복 헤딩이나 순서 뒤바뀜을 스스로 검출할 수 있다.
            if (layout?.IsSplitAvailable == true)
            {
                var anchored = TryLocateByAnchor(lines, layout.Sections!, warnings);
                if (anchored != null)
                {
                    Log.Information("단계 경계를 조각 앵커로 결정했습니다 - 단계 수: {Count}개", anchored.Value.Steps.Count);
                    return new StepBoundaryResult(anchored.Value.Steps, true, warnings, anchored.Value.FirstIndex);
                }
            }

            // 2순위: 목차가 선언한 단계 코드로 헤딩을 찾는다. 정제가 헤딩 문구를
            // 바꿔 앵커가 어긋난 경우의 복구 경로다.
            if (layout?.Steps is { Count: > 0 })
            {
                var byCode = TryLocateByCode(lines, layout.Steps, warnings);
                if (byCode != null)
                {
                    Log.Information("단계 경계를 목차 단계 코드로 결정했습니다 - 단계 수: {Count}개", byCode.Value.Steps.Count);
                    return new StepBoundaryResult(byCode.Value.Steps, true, warnings, byCode.Value.FirstIndex);
                }
            }

            warnings.Add("단계 경계를 찾지 못했습니다. 계획서를 분할하지 않고 단일 파일로 유지합니다.");
            Log.Warning("단계 경계 결정 실패 - 단일 파일 폴백");
            return new StepBoundaryResult(NoSteps, false, warnings, -1);
        }

        private static (Dictionary<string, string> Steps, int FirstIndex)? TryLocateByAnchor(
            List<string> lines, IReadOnlyDictionary<string, string> sections, List<string> warnings)
        {
            var located = new List<(string Code, int Index)>();

            foreach (var pair in sections)
            {
                var heading = FirstHeadingLine(pair.Value);
                if (heading == null)
                {
                    warnings.Add($"단계 {pair.Key}의 조각에 헤딩이 없어 앵커를 만들 수 없습니다.");
                    return null;
                }

                var index = MarkdownSectionLocator.FindIndexOutsideFence(
                    lines, 0, line => line.Trim() == heading);
                if (index < 0)
                {
                    warnings.Add($"단계 {pair.Key}의 헤딩을 최종 문서에서 찾지 못했습니다: {heading}");
                    return null;
                }

                located.Add((pair.Key, index));
            }

            return Materialize(lines, located, warnings);
        }

        private static (Dictionary<string, string> Steps, int FirstIndex)? TryLocateByCode(
            List<string> lines, IReadOnlyList<BatchStepPlan> steps, List<string> warnings)
        {
            var located = new List<(string Code, int Index)>();

            foreach (var step in steps)
            {
                var index = MarkdownSectionLocator.FindIndexOutsideFence(lines, 0, line =>
                {
                    var trimmed = line.TrimStart();
                    return trimmed.StartsWith("#", StringComparison.Ordinal)
                        && trimmed.Contains(step.Code, StringComparison.OrdinalIgnoreCase);
                });

                if (index < 0)
                {
                    warnings.Add($"목차의 단계 {step.Code}에 해당하는 헤딩을 최종 문서에서 찾지 못했습니다.");
                    return null;
                }

                located.Add((step.Code, index));
            }

            // 목차 순서와 문서 순서가 어긋나면 코드 포함 판정이 엉뚱한 헤딩을 잡은 것이다
            // (예: "### S02 (S01 이후)"가 S01로 먼저 걸리는 경우). 신뢰할 수 없다.
            for (var i = 1; i < located.Count; i++)
            {
                if (located[i].Index <= located[i - 1].Index)
                {
                    warnings.Add("목차 순서와 문서의 헤딩 순서가 어긋나 단계 코드 탐색을 신뢰할 수 없습니다.");
                    return null;
                }
            }

            return Materialize(lines, located, warnings);
        }

        /// <summary>
        /// 찾아낸 시작 인덱스들로 실제 본문을 잘라낸다. 각 단계는 다음 단계의 시작 직전까지이며,
        /// 마지막 단계는 다음 H2(= 검증 SQL 세트)에서 끝난다.
        /// </summary>
        private static (Dictionary<string, string> Steps, int FirstIndex)? Materialize(
            List<string> lines, List<(string Code, int Index)> located, List<string> warnings)
        {
            if (located.Count == 0)
            {
                warnings.Add("잘라낼 단계가 하나도 없습니다.");
                return null;
            }

            var ordered = located.OrderBy(item => item.Index).ToList();

            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Index == ordered[i - 1].Index)
                {
                    warnings.Add(
                        $"단계 {ordered[i - 1].Code}와 {ordered[i].Code}가 같은 헤딩을 가리켜 경계를 정할 수 없습니다.");
                    return null;
                }
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < ordered.Count; i++)
            {
                var start = ordered[i].Index;
                int end;

                if (i + 1 < ordered.Count)
                {
                    end = ordered[i + 1].Index;
                }
                else
                {
                    // 마지막 단계는 다음 H2에서 끝난다. H2가 없으면 문서 끝까지.
                    var nextH2 = MarkdownSectionLocator.FindIndexOutsideFence(
                        lines, start + 1,
                        line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal));
                    end = nextH2 < 0 ? lines.Count : nextH2;
                }

                var body = string.Join("\n", lines.Skip(start).Take(end - start)).Trim();
                if (body.Length == 0)
                {
                    warnings.Add($"단계 {ordered[i].Code}의 본문이 비어 있습니다.");
                    return null;
                }

                result[ordered[i].Code] = body;
            }

            return (result, ordered[0].Index);
        }

        /// <summary>조각에서 첫 헤딩 줄을 뽑는다. 이 줄이 최종 문서를 찾을 앵커가 된다.</summary>
        private static string? FirstHeadingLine(string? sectionMarkdown)
        {
            var lines = MarkdownSectionLocator.SplitLines(sectionMarkdown);
            var index = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, line => line.TrimStart().StartsWith("#", StringComparison.Ordinal));

            return index < 0 ? null : lines[index].Trim();
        }

        /// <summary>
        /// 최종 계획서를 산출물 파일 단위로 자른다.
        ///
        /// 골격 분할과 단계 분할은 독립적으로 판정한다. 골격의 H2 하나를 못 찾았다고
        /// 단계 분할까지 포기하면, 회차당 입력에서 가장 큰 몫을 차지하는 단계 상세가
        /// 통짜로 남아 이 작업의 목적 자체가 사라진다.
        /// </summary>
        public static PlanSlices Resolve(string finalPlanMarkdown, PlanLayout? layout)
        {
            var lines = MarkdownSectionLocator.SplitLines(finalPlanMarkdown);
            var steps = ResolveSteps(finalPlanMarkdown, layout);
            var warnings = new List<string>(steps.Warnings);

            var headings = MechanicalValidator.RequiredConsolidatedHeaders;
            var positions = new int[headings.Length];
            var allFound = true;

            for (var i = 0; i < headings.Length; i++)
            {
                positions[i] = LocateH2(lines, headings[i]);
                if (positions[i] < 0)
                {
                    warnings.Add($"골격 H2를 찾지 못했습니다: {headings[i]}");
                    allFound = false;
                }
            }

            // 순서가 어긋나면 헤딩 판정이 엉뚱한 줄을 잡은 것이다.
            if (allFound)
            {
                for (var i = 1; i < positions.Length; i++)
                {
                    if (positions[i] <= positions[i - 1])
                    {
                        warnings.Add("골격 H2의 문서 내 순서가 기대와 달라 골격을 분할하지 않습니다.");
                        allFound = false;
                        break;
                    }
                }
            }

            var preamble = positions[0] > 0
                ? Join(lines, 0, positions[0])
                : string.Empty;

            if (!allFound)
            {
                Log.Warning("골격 H2 탐색 실패 - 골격을 통짜로 유지합니다. 단계 분할 여부: {StepsSplit}", steps.Split);
                return new PlanSlices(
                    preamble,
                    Join(lines, positions[0] > 0 ? positions[0] : 0, lines.Count),
                    null,
                    null,
                    steps.Steps,
                    SkeletonSplit: false,
                    StepsSplit: steps.Split,
                    warnings);
            }

            // 개요 + Mermaid = [H2①, H2③). 단, 단계 분할이 실패해 공통 규약을 잘라낼
            // 기준점(첫 단계 헤딩)이 없으면 "단계별 이행 상세" 섹션 전체를 골격에 통짜로
            // 남긴다. 그러지 않으면 그 구간이 어느 조각에도 속하지 못하고 사라진다.
            var architectureEnd = steps.Split ? positions[2] : positions[3];
            var architecture = Join(lines, positions[0], architectureEnd);

            // 공통 규약 = (H2③, 첫 단계 헤딩). 단계 경계를 모르면 끝점을 정할 수 없다.
            string? stepContract = null;
            if (steps.Split && steps.FirstStepLineIndex > positions[2])
            {
                stepContract = Join(lines, positions[2] + 1, steps.FirstStepLineIndex);
                if (stepContract.Length == 0)
                {
                    stepContract = null;
                }
            }

            // 검증 SQL = [H2④, 다음 H2 또는 문서 끝)
            var verificationEnd = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, positions[3] + 1,
                line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal));
            var verification = Join(lines, positions[3], verificationEnd < 0 ? lines.Count : verificationEnd);

            Log.Information(
                "골격을 분할했습니다 - 공통 규약: {HasContract}, 검증 SQL: {HasVerification}",
                stepContract != null, verification.Length > 0);

            return new PlanSlices(
                preamble,
                architecture,
                stepContract,
                verification.Length > 0 ? verification : null,
                steps.Steps,
                SkeletonSplit: true,
                StepsSplit: steps.Split,
                warnings);
        }

        /// <summary>
        /// H2 헤딩을 찾는다. 정확 일치를 먼저 보고, 실패하면 이름 포함으로 완화한다.
        /// 정제가 헤딩에 번호나 이모지를 덧붙이는 경우가 있어 정확 일치만으로는 놓친다.
        /// </summary>
        private static int LocateH2(IReadOnlyList<string> lines, string headingName)
        {
            var exact = "## " + headingName;
            var index = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, line => line.Trim() == exact);
            if (index >= 0)
            {
                return index;
            }

            return MarkdownSectionLocator.FindIndexOutsideFence(lines, 0, line =>
            {
                var trimmed = line.TrimStart();
                return trimmed.StartsWith("## ", StringComparison.Ordinal)
                    && trimmed.Contains(headingName, StringComparison.Ordinal);
            });
        }

        private static string Join(IReadOnlyList<string> lines, int start, int end)
        {
            if (start < 0 || end <= start)
            {
                return string.Empty;
            }

            return string.Join("\n", lines.Skip(start).Take(end - start)).Trim();
        }
    }
}
