using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>한 단계를 재고 난 결과. 실패를 <b>종류별로</b> 나눠 담는다.</summary>
    /// <param name="StepCode">단계 코드.</param>
    /// <param name="Declared">명세서 DML 범위 표가 이 단계에 선언한 SELECT 서수.</param>
    /// <param name="Anchors">본문에서 계약대로 읽힌 앵커 서수.</param>
    /// <param name="FoldedCount">이름표에 접어 넣어 안 읽힌 앵커 수.</param>
    /// <param name="MissedDeclarations">선언됐는데 앵커가 안 붙은 서수.</param>
    /// <param name="FabricatedOrdinals">명세서에 행이 없는데 앵커가 붙은 서수.</param>
    public sealed record AnchorProbeStepOutcome(
        string StepCode,
        IReadOnlyList<int> Declared,
        IReadOnlyList<int> Anchors,
        int FoldedCount,
        IReadOnlyList<int> MissedDeclarations,
        IReadOnlyList<int> FabricatedOrdinals);

    /// <summary>한 판의 집계.</summary>
    public sealed record AnchorProbeReport(
        int StepsProbed,
        int Reached,
        int MissedDeclarationRows,
        int FabricatedOrdinalCount,
        int FoldedCount);

    /// <summary>
    /// 앵커 계약 프로브의 <b>순수 판정 로직</b>. AI도 파일도 부르지 않는다 - 재료를
    /// 받아 수를 낸다.
    ///
    /// [왜 실패를 셋으로 나누나] 도달률 하나로 뭉치면 처방이 안 갈린다.
    /// <list type="bullet">
    /// <item>선언했는데 앵커 0 → 모델이 <b>안 달았다</b>. 규칙이 약하다.</item>
    /// <item>접어 넣음 → <b>달았는데 자리가 틀렸다</b>. 자리 조항·예시가 약하다.</item>
    /// <item>명세서에 없는 서수 → <b>지어냈다</b>. 「행이 있는 문장에만」이 약하다.</item>
    /// </list>
    /// 2026-09-13 에 이 셋이 한 회차에 차례로 났고, 매번 다른 층을 고쳐야 했다.
    ///
    /// [이 자는 배송본을 판정하지 않는다] 프로브는 재시도·자가수정 없이 <b>첫
    /// 생성</b>만 본다. 그래서 여기 초록이어도 배송본이 계약을 지킨다는 뜻이 아니다 -
    /// 재는 것은 「프롬프트가 모델을 그 모양으로 이끄는가」다.
    /// </summary>
    public static class AnchorProbe
    {
        /// <summary>명세서가 SELECT 행을 선언한 단계만 고른다.</summary>
        public static IReadOnlyList<(BatchStepPlan Step, IReadOnlyList<int> Declared)> SelectSteps(
            IReadOnlyList<BatchStepPlan> steps,
            IReadOnlyDictionary<string, SpecStatementFacts> facts)
        {
            var selected = new List<(BatchStepPlan, IReadOnlyList<int>)>();
            if (steps == null || facts == null) return selected;

            foreach (var step in steps)
            {
                var declared = DeclaredSelectOrdinals(facts, step);
                if (declared.Count == 0) continue;

                selected.Add((step, declared.OrderBy(x => x).ToList()));
            }

            return selected;
        }

        /// <summary>한 단계의 본문을 선언과 대조한다.</summary>
        public static AnchorProbeStepOutcome Judge(
            string stepCode, IReadOnlyCollection<int> declared, string? stepMarkdown)
        {
            var declaredSet = new HashSet<int>(declared ?? Array.Empty<int>());
            var anchors = StepSqlStatementReader.ReadSelectAnchors(stepMarkdown);
            var anchorSet = new HashSet<int>(anchors);

            return new AnchorProbeStepOutcome(
                stepCode,
                declaredSet.OrderBy(x => x).ToList(),
                anchors,
                StepSqlStatementReader.CountFoldedSelectAnchors(stepMarkdown),
                declaredSet.Where(o => !anchorSet.Contains(o)).OrderBy(x => x).ToList(),
                anchorSet.Where(o => !declaredSet.Contains(o)).OrderBy(x => x).ToList());
        }

        /// <summary>
        /// 판 전체를 집계한다. <b>표본 0 은 0 으로 낸다</b> - 재지 않은 판을
        /// 「도달률 100%」로 내면 아무것도 안 한 실행이 합격으로 읽힌다.
        /// </summary>
        public static AnchorProbeReport Summarize(IReadOnlyCollection<AnchorProbeStepOutcome> outcomes)
        {
            if (outcomes == null || outcomes.Count == 0)
            {
                return new AnchorProbeReport(0, 0, 0, 0, 0);
            }

            return new AnchorProbeReport(
                outcomes.Count,
                outcomes.Count(o => o.Anchors.Count > 0),
                outcomes.Sum(o => o.MissedDeclarations.Count),
                outcomes.Sum(o => o.FabricatedOrdinals.Count),
                outcomes.Sum(o => o.FoldedCount));
        }

        private static HashSet<int> DeclaredSelectOrdinals(
            IReadOnlyDictionary<string, SpecStatementFacts> facts, BatchStepPlan step)
        {
            var declared = new HashSet<int>();
            foreach (var procedure in step.LegacyProcedures ?? Array.Empty<string>())
            {
                // 목차는 `dbo.UP_A` 로 적고 명세서 사실은 `UP_A` 로 색인된다.
                // 접두사를 안 벗기면 선별이 통째로 비고, 그 빈 결과는 「선언한
                // 단계가 없다」로 읽혀 조용히 통과한다.
                if (!facts.TryGetValue(BareName(procedure), out var procedureFacts)) continue;

                foreach (var row in procedureFacts.DmlRows)
                {
                    if (row.Kind.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                    {
                        declared.Add(row.Ordinal);
                    }
                }
            }

            return declared;
        }

        private static string BareName(string? qualified)
        {
            if (string.IsNullOrWhiteSpace(qualified)) return string.Empty;
            var dot = qualified.LastIndexOf('.');
            return dot < 0 ? qualified : qualified[(dot + 1)..];
        }
    }
}
