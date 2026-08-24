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
    ///
    /// [잎 문장이 겹치지 않는다는 불변식에 기댄다] Compose는 각 잎의 [StartLine,
    /// EndLine]으로 fact·앵커를 귀속시킨다 - 두 잎의 라인 범위가 겹치면 그 사실이
    /// 양쪽 문장에 동시에 실려 4상태가 이중으로 잡힌다. 이 불변식은
    /// DdlStatementEnumerator.Contains의 <b>진부분</b> 판정이 지킨다(범위가 완전히
    /// 같은 두 문장은 서로를 컨테이너로 못 만들어 둘 다 잎으로 남는 이론상의 틈이
    /// 있다 - 이번 라운드 리뷰가 지적한 Minor, 실측 픽스처로 재현되지 않아 다음
    /// 라운드로 이월했다). DdlStatementEnumerator를 고치는 사람은 이 소비처가
    /// 비겹침에 기댄다는 것을 알아야 한다.
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
        /// SpecExpectations가 낸 재료 중 <b>줄 번호를 가진 것</b>을 모은다 - 단 전부는
        /// 아니다. 파생 테이블 정의(DerivedColumnDefinition)는 줄 번호가 없어 여기
        /// 들어오지 못한다(설계서 미확정 사항 5번의 실측 대상). <b>SourceComments도
        /// 의도적으로 뺀다</b>(2026-08-24 Fix Round 1 - 실측으로 가른 결정).
        ///
        /// [왜 SourceComments를 빼는가 - SpecAnchorIndex의 주석 앵커 배제와 짝이다]
        /// SpecAnchorIndex는 원본 주석 표에서 나온 앵커를 IsCommentAnchor=true로 갈라
        /// 판정에서 뺀다 - "38번 줄에 이런 주석이 있었다"가 "38번 줄의 문장이
        /// 문서화됐다"를 뜻하지 않기 때문이다. SourceComments를 재료 쪽에는 넣고
        /// 앵커 쪽에서는 계속 빼면 그 대칭이 깨진다: 주석이 달린 문장은 재료 있음
        /// + (주석 앵커라 제외된) 앵커 없음 = SpecMissing(🟥)이 되어, Spec.md가
        /// 그 주석을 원본 주석 표에 성실히 옮겨 적었는데도 "명세서 결함"으로
        /// 오보된다.
        ///
        /// 실측(14개 SP 전체, 잎 487개)으로 가렸다: SourceComments를 포함시키면
        /// 새로 SpecMissing이 되는 잎이 2건 나왔다(UP_UTIL_SETTLE_EXPECT_PROC:16,
        /// UP_UTIL_SETTLE_PROC_ETC:23). 둘 다 눈으로 Spec.md를 열어 확인한 결과
        /// 실제로는 "원본 주석 기록" 표에 그 줄이 이미 옮겨져 있었다 - 즉 둘 다
        /// 허위 SpecMissing이었다. 반대로 포함해서 얻는 것(정말 안 옮겨진 주석을
        /// 잡아내는 사례)은 이 코퍼스에서 0건이었다. 그래서 배제 쪽을 고정한다 -
        /// CoverageMapComposerTests.Compose_SourceCommentOnlyStatement_ShouldNotBecomeSpecMissing가
        /// 이 결정을 회귀로 잠근다.
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
