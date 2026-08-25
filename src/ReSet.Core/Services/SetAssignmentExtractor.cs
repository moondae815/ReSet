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
        /// 원문 토큰을 그대로 이어 붙인 뒤 개행만 접는다.
        ///
        /// [자기 사본을 쓰는 이유] `DmlScopeExtractor.TextOf`는 그 클래스 내부 private이라
        /// 부를 수 없다. `DerivedTableColumnExtractor.cs:165`가 이미 같은 로직의 자기
        /// 사본을 갖고 있는 것이 이 코드베이스의 관례다.
        ///
        /// [Fix Round 2 - 왜 개행만 접고 스페이스·탭은 그대로 두는가] 라운드 1은 "이 표는
        /// '요약 금지' 계약이 더 엄해서 형제 추출기(`DerivedTableColumnExtractor` 등)보다
        /// 엄격하게 리터럴 내부까지 보존한다"고 적었다 - 그 축이 틀렸다. 진짜 근거는
        /// 렌더/검증 왕복이다: `AiService`가 표를 렌더할 때 이 값은
        /// `MarkdownTableCellCodec.Escape`를 거치고, `Escape`는 개행만 공백으로 바꾼다
        /// (스페이스·탭은 손대지 않는다). `MechanicalValidator`는 모델이 그 렌더된
        /// 값을 베껴 온 텍스트를 **접히지 않은 원본 fact**와 대조하므로, fact에 개행이
        /// 남아 있으면 모델이 볼 수 있는 값(렌더된 값)으로는 그 대조를 절대 통과할 수
        /// 없다 - 개행이 있는 값은 어떤 산출물도 만족시킬 수 없는 요구가 된다. 그래서
        /// 추출기는 `Escape`가 접는 만큼만, 정확히 그만큼만 접어야 한다: 더 접으면(예:
        /// 스페이스·탭까지 하나로 뭉개면) 형제 추출기들처럼 값 충실도를 공짜로 버리고,
        /// 덜 접으면(개행을 보존하면) 왕복이 성립하지 않는다. `MarkdownTableCellCodec
        /// .CollapseNewlines`를 `Escape`와 공유해서 이 규칙이 두 곳에서 따로 어긋나는
        /// (드리프트) 것을 구조적으로 막는다.
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
                sb.Append(stream[i].Text);
            }

            return MarkdownTableCellCodec.CollapseNewlines(sb.ToString());
        }
    }
}
