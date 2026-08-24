using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>설계서 §2의 4상태.</summary>
    public enum CoverageState
    {
        /// <summary>🟩 추출기 재료가 있고 명세서도 지목했다.</summary>
        Consistent,

        /// <summary>🟥 추출기 재료가 있는데 명세서가 지목하지 않았다. 재생성으로 닫힌다.</summary>
        SpecMissing,

        /// <summary>🟦 명세서는 지목했는데 추출기 재료가 없다. 검증 안 된 산문이다.</summary>
        ProseOnly,

        /// <summary>🟧 둘 다 없다. 도구를 고쳐야 닫히는 사각지대.</summary>
        OutOfScope
    }

    public sealed record StatementCoverage(
        DdlStatement Statement,
        CoverageState State,
        IReadOnlyList<int> ExtractorLines,
        IReadOnlyList<SpecAnchor> Anchors,
        IReadOnlyList<SpecAnchor> CommentAnchors,
        bool IsKnownUncovered);

    public sealed record ObjectCoverage(
        string ObjectName,
        string DdlText,
        IReadOnlyList<StatementCoverage> Statements,
        int TableKindsRead)
    {
        public int LeafCount => Statements.Count;

        public int Count(CoverageState state) => Statements.Count(s => s.State == state);
    }

    /// <summary>
    /// 좌표계(잎 문장)에 추출기 재료와 문서 앵커 두 축을 겹쳐 4상태를 확정한다.
    ///
    /// [주석 앵커는 상태를 바꾸지 않는다] SpecAnchorIndex가 갈라 준 IsCommentAnchor는
    /// 판정에서 빠지고 근거 패널용으로만 실린다. 이유는 SpecAnchorIndex 문서 참고.
    /// </summary>
    public static class CoverageMapComposer
    {
        public static ObjectCoverage Compose(string objectName, SpDefinition spDef, string? specMarkdown)
        {
            ArgumentNullException.ThrowIfNull(spDef);

            var ddl = spDef.DdlText ?? string.Empty;
            var leaves = DdlStatementEnumerator.Leaves(DdlStatementEnumerator.Enumerate(ddl));
            var allAnchors = SpecAnchorIndex.Build(specMarkdown);
            var extractorLines = ExtractorFactLines(spDef);
            var knownUncoveredLines = DmlScopeExtractor.ExtractUncoveredStatements(ddl)
                .Select(u => u.Line)
                .ToHashSet();

            var statements = new List<StatementCoverage>(leaves.Count);
            foreach (var leaf in leaves)
            {
                bool InRange(int line) => line >= leaf.StartLine && line <= leaf.EndLine;

                var mine = allAnchors.Where(a => InRange(a.Line)).ToList();
                var factAnchors = mine.Where(a => !a.IsCommentAnchor).ToList();
                var commentAnchors = mine.Where(a => a.IsCommentAnchor).ToList();
                var facts = extractorLines.Where(InRange).Distinct().OrderBy(l => l).ToList();

                var state = (facts.Count > 0, factAnchors.Count > 0) switch
                {
                    (true, true) => CoverageState.Consistent,
                    (true, false) => CoverageState.SpecMissing,
                    (false, true) => CoverageState.ProseOnly,
                    (false, false) => CoverageState.OutOfScope
                };

                statements.Add(new StatementCoverage(
                    leaf,
                    state,
                    facts,
                    factAnchors,
                    commentAnchors,
                    knownUncoveredLines.Any(InRange)));
            }

            return new ObjectCoverage(
                objectName,
                ddl,
                statements,
                SpecAnchorIndex.CountLineBearingTables(specMarkdown));
        }

        /// <summary>
        /// SpecExpectations가 낸 재료 중 <b>줄 번호를 가진 것</b>을 전부 모은다.
        /// 파생 테이블 정의(DerivedColumnDefinition)는 줄 번호가 없어 여기 들어오지
        /// 못한다 - 설계서 미확정 사항 5번의 실측 대상이다.
        /// </summary>
        private static IReadOnlyList<int> ExtractorFactLines(SpDefinition spDef)
        {
            var expectations = SpecExpectations.From(spDef);
            if (expectations == null) return Array.Empty<int>();

            var lines = new List<int>();
            lines.AddRange(expectations.DmlScopeFacts.Select(f => f.Line));
            lines.AddRange(expectations.SetPredicates.Select(f => f.Line));
            lines.AddRange(expectations.LockHints.Select(f => f.Line));
            lines.AddRange(expectations.CaseBranches.Select(f => f.Line));
            lines.AddRange(expectations.ReferencedFunctionCalls.Select(f => f.Line));
            lines.AddRange(expectations.RoundingCalls.Select(f => f.Line));

            // ExecutionSemanticFact.Line은 string이다(ExecutionSemanticsFacts.cs:12).
            // 숫자가 아닌 값("-" 등)은 조용히 버린다.
            foreach (var fact in expectations.ExecutionSemantics)
            {
                if (int.TryParse(fact.Line, out var line)) lines.Add(line);
            }

            return lines;
        }
    }
}
