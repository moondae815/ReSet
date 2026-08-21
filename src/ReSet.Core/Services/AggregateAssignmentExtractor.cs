using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">대입문의 원본 줄 번호.</param>
    /// <param name="Variable">대입 대상 변수명.</param>
    /// <param name="Aggregate">집계 함수 이름(대문자).</param>
    /// <param name="HasInitializer">DECLARE에 초기값이 있었는가.</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record AggregateAssignmentFact(
        int Line, string Variable, string Aggregate, bool HasInitializer, string Sentence);

    /// <summary>
    /// `SELECT @v = AGG(...)` 형태의 변수 대입을 뽑는다.
    ///
    /// [왜 이것이 확정 사실인가] T-SQL의 집계 SELECT는 GROUP BY가 없으면 일치 행이
    /// 0건이어도 한 행을 돌려준다. 그래서 대입은 항상 일어나고, MIN/MAX/SUM/AVG는
    /// NULL을, COUNT는 0을 넣는다. 이 사실은 이 SP의 사정이 아니라 T-SQL 명세다.
    ///
    /// [왜 비집계 대입은 담지 않는가] `SELECT @v = c FROM t`는 무결과면 대입 자체가
    /// 일어나지 않아 변수가 직전 값을 유지한다 - 정확히 반대 의미다. 담으면 거짓이 된다.
    ///
    /// [실측] UP_UTIL_SETTLE_INS_EXTRA:16,21-25와 UP_UTIL_SETTLE_SUMMARY_EXTRA:20,25-29.
    /// 둘 다 초기값 ''가 NULL로 덮이는 사실이 명세서 전체에 한 번도 없었고, 그 결과
    /// 후속 DML의 대상 행 집합이 "없음"과 "전부"로 뒤집히는 결함이 났다.
    /// </summary>
    public static class AggregateAssignmentExtractor
    {
        private static readonly string[] NullOnEmptyAggregates = { "MIN", "MAX", "SUM", "AVG" };

        public static IReadOnlyList<AggregateAssignmentFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<AggregateAssignmentFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<AggregateAssignmentFact>();
                }

                var declareVisitor = new DeclareVisitor();
                fragment.Accept(declareVisitor);

                var visitor = new AggregateAssignmentVisitor(declareVisitor.Initialized);
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[AggregateAssignmentExtractor] 집계 대입 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<AggregateAssignmentFact>();
            }
        }

        private sealed class DeclareVisitor : TSqlFragmentVisitor
        {
            public HashSet<string> Initialized { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void Visit(DeclareVariableElement node)
            {
                if (node.Value == null) return;
                var name = node.VariableName?.Value;
                if (!string.IsNullOrWhiteSpace(name)) Initialized.Add(name!);
            }
        }

        private sealed class AggregateAssignmentVisitor : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _initialized;

            public AggregateAssignmentVisitor(HashSet<string> initialized) => _initialized = initialized;

            public List<AggregateAssignmentFact> Facts { get; } = new();

            public override void Visit(SelectSetVariable node)
            {
                if (node.Expression is not FunctionCall call) return;

                var name = call.FunctionName?.Value;
                if (string.IsNullOrWhiteSpace(name)) return;

                var upper = name!.ToUpperInvariant();
                var isCount = upper == "COUNT" || upper == "COUNT_BIG";
                if (!isCount && !NullOnEmptyAggregates.Contains(upper)) return;

                var variable = node.Variable?.Name ?? "(미상)";
                var hasInitializer = _initialized.Contains(variable);

                var sentence = isCount
                    ? "집계 SELECT는 무결과여도 한 행을 돌려주므로 대입이 항상 일어납니다. COUNT는 0을 넣습니다."
                    : "집계 SELECT는 무결과여도 한 행을 돌려주므로 대입이 항상 일어납니다. 무결과 시 NULL이 대입됩니다"
                      + (hasInitializer ? " — DECLARE의 초기값은 유지되지 않습니다." : ".");

                Facts.Add(new AggregateAssignmentFact(
                    node.StartLine, variable, upper, hasInitializer, sentence));
            }
        }
    }
}
