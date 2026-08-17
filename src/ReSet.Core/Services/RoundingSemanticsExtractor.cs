using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">원본 DDL에서의 줄 번호(1부터).</param>
    /// <param name="ThirdArgument">ROUND의 세 번째 인자 원문.</param>
    public sealed record RoundingCall(int Line, string ThirdArgument);

    /// <summary>
    /// 3인자 ROUND 호출을 뽑는다.
    ///
    /// 세 번째 인자의 의미는 이 SP의 사정이 아니라 T-SQL 명세다 - 0이면 반올림,
    /// 0이 아니면 절사. 그래서 재료는 그 문장을 상수로 들고 있으면 되고 추측이
    /// 아니다. 원본 주석 --0:반올림, 0&lt;&gt;절사는 그 명세를 재확인해 줄 뿐이다.
    ///
    /// 2인자 호출은 담지 않는다. 항상 반올림이라 기술할 값 매핑이 없다.
    ///
    /// [AST 파서를 쓰는 이유] 정규식으로 "ROUND("를 찾으면 주석이나 문자열 리터럴
    /// 속 텍스트까지 함수 호출로 오인한다. ScriptDom은 실제 파스 트리를 보므로
    /// 그런 텍스트가 애초에 FunctionCall 노드가 되지 않는다.
    ///
    /// [중첩을 그대로 두는 이유] 실측(UP_UTIL_SETTLE_COMM_UPD.Procedure:63,
    /// UP_UTIL_SETTLE_EXCEPTION_PROC.Procedure:69)에서 바깥 ROUND의 인자 안에
    /// 또 다른 3인자 ROUND가 들어 있다. 방문자가 자식 노드까지 계속 내려가므로
    /// (Visit을 오버라이드해도 기본 ExplicitVisit이 AcceptChildren을 호출한다)
    /// 별도 처리 없이 안팎 모두 잡힌다 - 안쪽만 잡고 바깥을 놓치면(또는 그 반대)
    /// 정확히 SpecRoundingShapeExtractor가 겨냥하는 반올림 순서 결함과 같은 종류의
    /// 정보가 L1에서도 새어 나간다.
    /// </summary>
    public static class RoundingSemanticsExtractor
    {
        /// <summary>프롬프트와 L1이 함께 쓰는 의미 문장. 두 곳이 다르게 말하면 안 된다.</summary>
        public const string SemanticsSentence =
            "ROUND의 세 번째 인자는 0이면 반올림, 0이 아니면 절사입니다.";

        public static IReadOnlyList<RoundingCall> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<RoundingCall>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);

                // 구문 오류가 있어도 파싱된 조각으로 계속한다 - 파서 본체가
                // 소프트 페일하는 것과 같은 판단이다.
                if (fragment == null) return Array.Empty<RoundingCall>();

                var visitor = new RoundCallVisitor();
                fragment.Accept(visitor);
                return visitor.Calls;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[RoundingSemanticsExtractor] ROUND 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<RoundingCall>();
            }
        }

        private sealed class RoundCallVisitor : TSqlFragmentVisitor
        {
            public List<RoundingCall> Calls { get; } = new();

            public override void Visit(FunctionCall node)
            {
                if (!string.Equals(node.FunctionName?.Value, "ROUND", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (node.Parameters == null || node.Parameters.Count < 3) return;

                var third = node.Parameters[2];
                var text = string.Concat(
                    third.ScriptTokenStream
                        .Skip(third.FirstTokenIndex)
                        .Take(third.LastTokenIndex - third.FirstTokenIndex + 1)
                        .Select(t => t.Text));

                Calls.Add(new RoundingCall(node.StartLine, text.Trim()));
            }
        }
    }
}
