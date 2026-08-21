using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">@@ROWCOUNT를 읽는 문장의 줄 번호.</param>
    /// <param name="Predicate">그 문장의 조건 원문.</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record RowCountBoundaryFact(int Line, string Predicate, string Sentence);

    /// <summary>
    /// 직전 형제 문장이 IF인 자리에서 @@ROWCOUNT를 읽는 문장을 뽑는다.
    ///
    /// [실행으로 확정한 사실 - 2026-08-22, SQL Server 2022 16.0.4255.1]
    /// 원본 구조(SELECT → IF @@ROWCOUNT&lt;1 BEGIN…END → IF @@ROWCOUNT&lt;1 BEGIN…END)를
    /// 그대로 재현한 결과, 앞의 IF가 조건 거짓으로 블록을 건너뛰어도 그 IF 문 자체가
    /// @@ROWCOUNT를 0으로 만든다. 따라서 두 번째 IF의 조건은 항상 참이다.
    /// 실측 대상: UF_GET_COMM4CLIENT.Function:52,68 - 명세서 mermaid는 1차 성공 시
    /// 3차를 건너뛰는 것으로 그려 금액 결정 규칙 자체가 달랐다(🔴).
    ///
    /// [왜 이 모양에만 한정하는가] T-SQL에서 어떤 문장이 @@ROWCOUNT를 보존하고
    /// 어떤 문장이 0으로 만드는지의 일반 규칙을 전부 구현하려 들면 틀릴 여지가 크다.
    /// 기계 확정 표에 추측이 섞이면 표 전체의 신뢰가 무너진다. 실측으로 닫은 모양만
    /// 싣고 나머지는 침묵한다 - 실패 방향이 안전한 쪽이다.
    /// </summary>
    public static class RowCountBoundaryExtractor
    {
        public const string SemanticsSentence =
            "직전 IF 문이 @@ROWCOUNT를 0으로 리셋하므로 이 조건은 항상 참입니다.";

        public static IReadOnlyList<RowCountBoundaryFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<RowCountBoundaryFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<RowCountBoundaryFact>();
                }

                var visitor = new BlockVisitor();
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[RowCountBoundaryExtractor] @@ROWCOUNT 경계 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<RowCountBoundaryFact>();
            }
        }

        private sealed class BlockVisitor : TSqlFragmentVisitor
        {
            public List<RowCountBoundaryFact> Facts { get; } = new();

            // Visit(StatementList)를 오버라이드해도 기본 ExplicitVisit이 AcceptChildren을
            // 호출하므로(RoundingSemanticsExtractor와 같은 근거) 프로시저 본문 최상위
            // 문장 목록뿐 아니라 BEGIN…END 블록 안, IF의 THEN 절이 BEGIN…END인 경우의
            // 내부 문장 목록까지 모두 이 메서드로 들어온다 - 별도 처리가 필요 없다.
            public override void Visit(StatementList node)
            {
                var statements = node.Statements;
                if (statements == null) return;

                for (var i = 1; i < statements.Count; i++)
                {
                    if (statements[i - 1] is not IfStatement) continue;
                    if (statements[i] is not IfStatement current) continue;
                    if (current.Predicate == null) continue;

                    var predicate = TextOf(current.Predicate);
                    if (predicate.IndexOf("@@ROWCOUNT", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    Facts.Add(new RowCountBoundaryFact(
                        current.StartLine, predicate, SemanticsSentence));
                }
            }

            private static string TextOf(TSqlFragment fragment)
            {
                return string.Concat(
                    fragment.ScriptTokenStream
                        .Skip(fragment.FirstTokenIndex)
                        .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                        .Select(t => t.Text)).Trim();
            }
        }
    }
}
