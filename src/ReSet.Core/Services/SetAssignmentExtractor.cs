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
        ///
        /// [Fix Round 1 - Important 1: 토큰 단위로 공백을 접는 이유] 이전 구현은 모든
        /// 토큰의 Text를 먼저 통째로 이어 붙인 뒤 결과 문자열에서 공백을 접었다. 그러면
        /// 문자열 리터럴 토큰의 Text 안에 든 공백(리터럴 값의 일부)까지 정렬 공백과
        /// 구분 없이 뭉개진다 - `'a  b'`가 `'a b'`가 되어 "요약·정규화 금지" 계약을
        /// 어긴다. ScriptDom 토큰 스트림에서 공백/개행은 그 자체로 별도 토큰
        /// (`TSqlTokenType.WhiteSpace`)이므로, 그 토큰만 공백 하나로 치환하고 그 외
        /// 토큰의 Text는 손대지 않고 그대로 이으면 정렬 공백(원래 목적)만 접히고
        /// 리터럴 내부 공백은 보존된다. `DerivedTableColumnExtractor.TextOf`도 같은
        /// 결함을 갖고 있지만 그쪽은 서술 텍스트용이라 "요약 금지" 계약이 없다 -
        /// 이 표는 계약이 더 엄해 선례보다 엄격하게 처리한다.
        /// </summary>
        private static string TextOf(TSqlFragment? fragment)
        {
            if (fragment == null || fragment.ScriptTokenStream == null) return string.Empty;

            var stream = fragment.ScriptTokenStream;
            var first = fragment.FirstTokenIndex;
            var last = fragment.LastTokenIndex;
            if (first < 0 || last < first || last >= stream.Count) return string.Empty;

            var sb = new StringBuilder();
            for (var i = first; i <= last; i++)
            {
                var token = stream[i];
                if (token.TokenType == TSqlTokenType.WhiteSpace)
                {
                    // 정렬 공백·개행은 표 셀 붕괴를 막기 위해 공백 하나로 접는다.
                    // 리터럴 내부 공백은 이 분기를 타지 않고(별도 토큰이 아니라
                    // 리터럴 토큰 자신의 Text 안에 있으므로) 아래 else 경로로
                    // 그대로 보존된다.
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                    continue;
                }
                sb.Append(token.Text);
            }

            return sb.ToString().TrimEnd(' ');
        }
    }
}
