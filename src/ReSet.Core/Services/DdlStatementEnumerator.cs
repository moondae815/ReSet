using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <param name="StartLine">문장이 시작하는 원본 DDL 줄(1-based).</param>
    /// <param name="EndLine">문장의 마지막 토큰이 놓인 줄. 토큰 스트림이 없으면 StartLine과 같다.</param>
    /// <param name="StatementType">ScriptDom 노드 타입 이름(예: "UpdateStatement").</param>
    /// <param name="NestingDepth">이 문장을 품고 있는 다른 문장의 수.</param>
    /// <param name="IsContainer">다른 문장을 품고 있으면 true. 커버리지는 잎만 센다.</param>
    public sealed record DdlStatement(
        int StartLine,
        int EndLine,
        string StatementType,
        int NestingDepth,
        bool IsContainer);

    /// <summary>
    /// 원본 DDL의 문장을 전수 열거해 커버리지 맵의 <b>좌표계</b>를 만든다.
    ///
    /// [왜 추출기를 참조하지 않는가] 좌표계를 추출기로 만들면 추출기의 사각지대가
    /// 좌표계의 사각지대가 된다. 그러면 커버리지 맵이 답하려는 질문 — "우리 기계 확정
    /// 표가 원본의 무엇을 아예 안 보고 있나" — 에 영원히 답할 수 없다. 이 파일은
    /// ScriptDom 외에 아무것도 쓰지 않는다.
    ///
    /// [왜 컨테이너 유형 목록을 두지 않는가] 설계서 초안은 IfStatement·WhileStatement·
    /// BeginEndBlockStatement·TryCatchStatement 넷을 열거했는데 CreateProcedureStatement가
    /// 빠져 있었다 - 그게 잎으로 세어지면 SP 본문 전체가 잎 하나가 되어 맵이 통째로
    /// 무의미해진다. 목록은 언제든 다시 낡는다. 그래서 유형이 아니라 <b>사실</b>로
    /// 판정한다: 다른 문장을 품고 있으면 컨테이너다. 토큰 범위 포함관계로 본다.
    /// </summary>
    public static class DdlStatementEnumerator
    {
        public static IReadOnlyList<DdlStatement> Enumerate(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<DdlStatement>();

            TSqlFragment? fragment;
            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    // CaseBranchExtractor.Extract와 같은 정책 - 오류가 하나라도 있으면
                    // 빈 목록. 부분 파스 결과로 만든 좌표계는 없느니만 못하다.
                    return Array.Empty<DdlStatement>();
                }
            }
            catch (Exception)
            {
                return Array.Empty<DdlStatement>();
            }

            var visitor = new StatementCollector();
            fragment.Accept(visitor);
            var raw = visitor.Statements;
            if (raw.Count == 0) return Array.Empty<DdlStatement>();

            var result = new List<DdlStatement>(raw.Count);
            foreach (var s in raw)
            {
                var depth = 0;
                var isContainer = false;
                foreach (var other in raw)
                {
                    if (ReferenceEquals(other, s)) continue;
                    if (Contains(other, s)) depth++;
                    if (Contains(s, other)) isContainer = true;
                }

                result.Add(new DdlStatement(
                    s.StartLine,
                    EndLineOf(s.Node),
                    s.Node.GetType().Name,
                    depth,
                    isContainer));
            }

            return result
                .OrderBy(s => s.StartLine)
                .ThenByDescending(s => s.EndLine)
                .ToList();
        }

        public static IReadOnlyList<DdlStatement> Leaves(IReadOnlyList<DdlStatement> all) =>
            all.Where(s => !s.IsContainer).ToList();

        /// <summary>outer가 inner를 <b>진부분</b>으로 품는가. 범위가 같으면 false다 -
        /// 같은 범위끼리 서로를 컨테이너로 만들어 잎이 하나도 안 남는 것을 막는다.</summary>
        private static bool Contains(Collected outer, Collected inner) =>
            outer.First <= inner.First
            && outer.Last >= inner.Last
            && (outer.First < inner.First || outer.Last > inner.Last);

        private static int EndLineOf(TSqlFragment node)
        {
            var stream = node.ScriptTokenStream;
            if (stream == null) return node.StartLine;
            var index = node.LastTokenIndex;
            if (index < 0 || index >= stream.Count) return node.StartLine;
            var line = stream[index].Line;
            return line > 0 ? line : node.StartLine;
        }

        private sealed record Collected(TSqlStatement Node, int StartLine, int First, int Last);

        private sealed class StatementCollector : TSqlFragmentVisitor
        {
            public List<Collected> Statements { get; } = new();

            public override void Visit(TSqlStatement node)
            {
                if (node.StartLine <= 0) return;
                Statements.Add(new Collected(node, node.StartLine, node.FirstTokenIndex, node.LastTokenIndex));
            }
        }
    }
}
