using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <param name="Line">원본 DDL에서의 줄 번호(1부터).</param>
    /// <param name="Variable">대입 대상 변수 이름(`@`를 포함한 원문).</param>
    /// <param name="Expression">대입식 원문 그대로. 요약·정규화하지 않는다.</param>
    public sealed record SetAssignmentFact(int Line, string Variable, string Expression);

    /// <summary>
    /// `SET @v = <식>` 대입을 전수 뽑는다.
    ///
    /// [관할 경계] `SELECT @v = ...`는 여기 안 들어온다 - ScriptDom에서 그것은
    /// `SelectSetVariable`이고 `AggregateAssignmentExtractor`·
    /// `NonAggregateAssignmentExtractor`가 그 타입만 본다. `DECLARE @v INT = 15`도
    /// 안 들어온다(`DeclareVariableStatement`). 관할이 겹치면 정본이 갈라진다.
    ///
    /// [`LoopVariableResetExtractor`와의 관계] `WHILE` 최상위 상수 재설정은 실행 의미
    /// 표에도 있지만 여기서도 담는다. 중복이 아니라 층이 다르다 - 이 표는 "어떤 대입이
    /// 있나"(원본 전사)에, 실행 의미 표는 "매 반복 다시 설정된다"(DDL 원문이 말하지 않는
    /// 실행 시점의 사실)에 답한다. 여기서 빼면 표가 전수가 아니게 되고, 다음 사람이 왜
    /// 이 줄만 빠졌는지를 찾아야 한다.
    /// </summary>
    public static class SetAssignmentExtractor
    {
        public const string TableHeading =
            "### 변수 대입 " + MachineConfirmedTables.HeadingSuffix;

        public static IReadOnlyList<SetAssignmentFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<SetAssignmentFact>();

            TSqlFragment? fragment;
            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    // CaseBranchExtractor.Extract와 같은 정책 - 부분 파스 결과가 기계 확정
                    // 표에 섞이면 표 전체의 신뢰가 무너진다.
                    return Array.Empty<SetAssignmentFact>();
                }
            }
            catch (Exception)
            {
                return Array.Empty<SetAssignmentFact>();
            }

            var visitor = new AssignmentVisitor();
            fragment.Accept(visitor);
            return visitor.Facts.OrderBy(f => f.Line).ToList();
        }

        private sealed class AssignmentVisitor : TSqlFragmentVisitor
        {
            public List<SetAssignmentFact> Facts { get; } = new();

            public override void Visit(SetVariableStatement node)
            {
                if (node.StartLine <= 0) return;

                var variable = node.Variable?.Name;
                if (string.IsNullOrWhiteSpace(variable)) return;

                var expression = TextOf(node.Expression);
                if (string.IsNullOrWhiteSpace(expression)) return;

                Facts.Add(new SetAssignmentFact(node.StartLine, variable!, expression));
            }
        }

        /// <summary>
        /// 원문 토큰을 그대로 이어 붙인다.
        ///
        /// [자기 사본을 쓰는 이유] `DmlScopeExtractor.TextOf`는 그 클래스 내부 private이라
        /// 부를 수 없다. `DerivedTableColumnExtractor.cs:165`가 이미 같은 로직의 자기
        /// 사본을 갖고 있는 것이 이 코드베이스의 관례다.
        /// </summary>
        private static string TextOf(TSqlFragment? fragment)
        {
            if (fragment == null || fragment.ScriptTokenStream == null) return string.Empty;

            var sb = new StringBuilder();
            var stream = fragment.ScriptTokenStream;
            var first = fragment.FirstTokenIndex;
            var last = fragment.LastTokenIndex;
            if (first < 0 || last < first || last >= stream.Count) return string.Empty;

            for (var i = first; i <= last; i++)
            {
                sb.Append(stream[i].Text);
            }

            return CollapseWhitespace(sb.ToString());
        }

        /// <summary>표 셀에 개행이 들어가면 마크다운 표가 깨진다. 공백 하나로 접는다.</summary>
        private static string CollapseWhitespace(string text)
        {
            var sb = new StringBuilder(text.Length);
            var pendingSpace = false;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch)) { pendingSpace = sb.Length > 0; continue; }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
