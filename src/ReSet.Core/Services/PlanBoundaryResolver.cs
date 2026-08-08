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
    /// <param name="LastStepEndLineIndex">마지막 단계 본문이 끝나는 줄 인덱스(그 줄은 포함되지
    /// 않는다). 단계 구간 <b>뒤</b>에 남은 내용이 어디서 시작하는지를 알려 준다 - 그 구간이
    /// 어느 조각에도 담기지 않으면 문서에서 조용히 사라진다. 분할에 실패하면 -1이다.</param>
    public sealed record StepBoundaryResult(
        IReadOnlyDictionary<string, string> Steps,
        bool Split,
        IReadOnlyList<string> Warnings,
        int FirstStepLineIndex,
        int LastStepEndLineIndex);

    /// <summary>
    /// 최종 계획서를 산출물 파일 단위로 자른 결과.
    /// </summary>
    /// <param name="Preamble">첫 H2 앞의 내용. L1Exhausted 배너가 여기 실린다.</param>
    /// <param name="Architecture">개요 + Mermaid 흐름도. 골격 분할이 실패하면 계획서 전문.
    /// 골격 분할은 성공했는데 단계 분할이 실패한 경우("단계별 이행 상세" 섹션을 어디서
    /// 자를지 모르는 경우)에는 그 섹션 전체까지 흡수한다 - 잘라낼 기준점이 없는 내용을
    /// 버리는 것보다는 여기 통짜로 남기는 편이 낫다.</param>
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
                    return new StepBoundaryResult(
                        anchored.Value.Steps, true, warnings, anchored.Value.FirstIndex, anchored.Value.LastEnd);
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
                    return new StepBoundaryResult(
                        byCode.Value.Steps, true, warnings, byCode.Value.FirstIndex, byCode.Value.LastEnd);
                }
            }

            warnings.Add("단계 경계를 찾지 못했습니다. 계획서를 분할하지 않고 단일 파일로 유지합니다.");
            Log.Warning("단계 경계 결정 실패 - 단일 파일 폴백");
            return new StepBoundaryResult(NoSteps, false, warnings, -1, -1);
        }

        private static (Dictionary<string, string> Steps, int FirstIndex, int LastEnd)? TryLocateByAnchor(
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

        private static (Dictionary<string, string> Steps, int FirstIndex, int LastEnd)? TryLocateByCode(
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
        private static (Dictionary<string, string> Steps, int FirstIndex, int LastEnd)? Materialize(
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

            // 마지막 단계가 끝나는 자리. 호출부는 이 뒤에 남은 내용을 어느 조각엔가
            // 반드시 실어야 한다 - 담지 않으면 문서에서 조용히 사라진다.
            var lastEnd = lines.Count;

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
                    lastEnd = end;
                }

                var body = string.Join("\n", lines.Skip(start).Take(end - start)).Trim();
                if (body.Length == 0)
                {
                    warnings.Add($"단계 {ordered[i].Code}의 본문이 비어 있습니다.");
                    return null;
                }

                result[ordered[i].Code] = body;
            }

            return (result, ordered[0].Index, lastEnd);
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
            var positions = new int[headings.Count];
            var allFound = true;

            for (var i = 0; i < headings.Count; i++)
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
                // 골격을 통짜로 남기더라도 단계 구간까지 삼키면 안 된다.
                // common/00-architecture.md는 <b>모든</b> 회차가 무조건 읽는 파일이라
                // (TaskFileComposer.Compose의 "먼저 읽을 것" 2번), 여기서 문서 끝까지
                // 담으면 이 작업이 없애려던 85k 토큰짜리 문서가 회차마다 한 번씩
                // 되살아난다 - 게다가 단계 슬라이스가 그 안에 통째로 중복된다.
                // 단계 분할이 성공했으면 그 뒤는 steps/*.md가 이미 덮으므로 첫 단계
                // 헤딩에서 끊는다. (01-step-contract.md는 골격 분할 성공 경로에서만
                // 만들어지고 끝점이 이미 첫 단계 헤딩이며, 02-data-access-boundary.md는
                // 계획서가 아니라 DataAccessPolicy에서 오므로 같은 문제가 없다.)
                var skeletonStart = positions[0] > 0 ? positions[0] : 0;
                var wholeSkeleton = BuildWholeSkeletonAroundSteps(lines, steps, skeletonStart);

                Log.Warning(
                    "골격 H2 탐색 실패 - 골격을 통짜로 유지합니다. 단계 분할 여부: {StepsSplit}, 단계 구간: [{FirstStep}, {LastStepEnd})",
                    steps.Split, steps.FirstStepLineIndex, steps.LastStepEndLineIndex);

                return new PlanSlices(
                    preamble,
                    wholeSkeleton,
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

        /// <summary>
        /// 골격 분할이 실패했을 때의 개요 조각을 만든다. 단계 구간 <b>양옆</b>을 모두 담는다.
        ///
        /// 이 분기의 조각은 개요 하나뿐이다(StepContract·Verification이 모두 null이고,
        /// StepsSplit이 true라 진입점에 계획서 전문 링크도 실리지 않는다). 그래서 여기 담기지
        /// 않은 구간은 에이전트가 읽을 방법이 아예 없다.
        ///
        /// 단계 구간만 빼는 이유는 <b>중복</b> 때문이다 - 그 구간은 steps/*.md가 이미 덮으므로,
        /// 여기 다시 담으면 매 회차가 읽는 파일에 계획서 전문이 통째로 되살아난다(이 작업이
        /// 없애려던 바로 그것). 반대로 <b>마지막 단계 뒤</b>는 아무도 덮지 않는다. 실제 문서에서는
        /// 대개 "통합 데이터 정합성 검증 SQL 세트"가 거기 있고, 골격 탐색이 실패하는 가장 흔한
        /// 원인이 헤딩 문구 변경이므로 - 즉 그 절이 <b>다른 이름으로 실재</b>하기 때문에 실패하므로 -
        /// 이 분기야말로 그것을 잃기 가장 쉬운 자리다.
        ///
        /// 검증 SQL 조각(verification/)으로 내보내지 않는 이유: 이 분기는 어느 H2가 무엇인지
        /// 판별하지 못한 상태다. 꼬리가 검증 SQL이라고 단정해 Verification에 실으면
        /// HasVerification이 참이 되어 조립 회차 지시서가 "검증 SQL을 배치하십시오"라고
        /// 말하는데, 그 내용이 실제로 무엇인지는 아무도 확인하지 않았다. 게다가
        /// verification/은 조립 회차만 가리키므로 단계 회차가 그 구간을 영영 못 본다.
        /// 판별에 실패했을 때의 정답은 "통짜로 남긴다"이며, 이 분기의 통짜 바구니가 개요다.
        /// (Task 4에서 고친 결함과 같은 부류다 - 그때도 어느 조각에도 속하지 못한 구간이
        /// 사라졌고, 처방은 통짜 바구니에 흡수시키는 것이었다.)
        /// </summary>
        private static string BuildWholeSkeletonAroundSteps(
            IReadOnlyList<string> lines, StepBoundaryResult steps, int skeletonStart)
        {
            if (!steps.Split || steps.FirstStepLineIndex <= skeletonStart)
            {
                // 단계 구간을 특정할 수 없다. 끊을 기준점이 없으므로 문서 끝까지 남긴다.
                return Join(lines, skeletonStart, lines.Count);
            }

            var head = Join(lines, skeletonStart, steps.FirstStepLineIndex);

            var tailStart = steps.LastStepEndLineIndex;
            var tail = tailStart >= 0 && tailStart < lines.Count
                ? Join(lines, tailStart, lines.Count)
                : string.Empty;

            if (tail.Length == 0)
            {
                return head;
            }

            Log.Information(
                "골격 통짜 유지 - 마지막 단계 뒤의 구간을 개요 조각에 흡수했습니다 - 시작 줄: {TailStart}", tailStart);

            return head.Length == 0 ? tail : head + "\n\n" + tail;
        }

        /// <summary>
        /// [0, lineCount) 중 covered가 덮지 않은 구간을 오름차순으로 돌려준다.
        ///
        /// 조각을 새로 만들면 그 범위를 <see cref="Resolve"/>의 covered 목록에 반드시
        /// 등록해야 한다. 등록을 잊으면 그 구간이 개요에 <b>중복</b>으로 실리고(회차마다
        /// 읽는 파일이 부푼다), 범위만 등록하고 조각을 만들지 않으면 구간이 <b>사라진다</b>.
        /// 둘 다 눈으로는 드러나지 않으므로 이 계산을 조각 나누는 코드 옆에 둔다.
        ///
        /// 겹치는 범위는 병합한다. 문서가 기형이라 단계 구간이 검증 SQL 구간과 겹치는
        /// 경우가 있는데, 그것을 빈틈으로 읽으면 이미 실린 내용을 한 번 더 싣게 된다.
        /// </summary>
        public static IReadOnlyList<(int Start, int End)> FindUncoveredRanges(
            int lineCount, IEnumerable<(int Start, int End)> covered)
        {
            if (covered == null) throw new ArgumentNullException(nameof(covered));

            var gaps = new List<(int Start, int End)>();
            if (lineCount <= 0)
            {
                return gaps;
            }

            var normalized = covered
                .Select(range => (Start: Math.Max(0, range.Start), End: Math.Min(lineCount, range.End)))
                .Where(range => range.End > range.Start)
                .OrderBy(range => range.Start)
                .ToList();

            var cursor = 0;
            foreach (var range in normalized)
            {
                if (range.Start > cursor)
                {
                    gaps.Add((cursor, range.Start));
                }

                if (range.End > cursor)
                {
                    cursor = range.End;
                }
            }

            if (cursor < lineCount)
            {
                gaps.Add((cursor, lineCount));
            }

            return gaps;
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
