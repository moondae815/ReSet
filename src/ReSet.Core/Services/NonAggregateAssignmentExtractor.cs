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
    /// <param name="Column">대입되는 컬럼 원문(별칭이 있으면 별칭까지).</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record NonAggregateAssignmentFact(
        int Line, string Variable, string Column, string Sentence);

    /// <summary>
    /// `SELECT @v = 컬럼 FROM ...` 형태의 **비집계** 변수 대입을 뽑는다.
    ///
    /// [왜 이것이 확정 사실인가] 집계가 없는 SELECT는 일치 행이 0건이면 0행을 돌려준다.
    /// 그러면 대입 자체가 일어나지 않아 변수는 이 문장 직전 값을 그대로 유지한다.
    /// 이 사실은 이 SP의 사정이 아니라 T-SQL 명세다.
    ///
    /// [집계 대입과 정반대다] <see cref="AggregateAssignmentExtractor"/>가 담는
    /// `SELECT @v = MAX(...)`는 GROUP BY가 없으면 무결과여도 한 행을 돌려주므로 대입이
    /// 항상 일어나고 NULL이 들어간다. 두 사실이 표에 나란히 놓여야 대비가 보인다.
    /// UP_UTIL_SETTLE_PROC_ETC가 72행(비집계) · 79행(집계)으로 둘 다 가진 실물이다.
    ///
    /// [왜 컬럼 참조만 담는가] 식이 집계를 품고 있으면 결론이 정반대로 뒤집힌다.
    /// 같은 SP의 101행 `SELECT @v = MAX(ID)+1`과 116행 `SELECT @v = ISNULL(SUM(...),0)`이
    /// 그 실물이다 - 최상위가 이항식/스칼라 함수라 집계 추출기는 담지 않지만, 질의 자체는
    /// 집계라 무결과여도 한 행이 돌아온다. 컬럼 참조는 잎 노드라 집계를 품을 수 없으므로
    /// 이 모양만 담으면 "무결과 시 대입이 없다"가 예외 없이 참이다. CASE 식이나 산술식은
    /// 대개 비집계지만 판정에 식 전체를 훑어야 하고 대상 칸 원문도 길어져, 담지 않고
    /// 침묵한다(AGENTS.md 범주 2와 같은 원칙 - 거짓 행보다 없는 행이 낫다).
    ///
    /// [왜 FROM 절을 요구하는가] `SELECT @v = ID`처럼 FROM이 없으면 무결과라는 개념이
    /// 없다 - 한 행이 반드시 돌아와 대입이 일어난다. FROM이 없는 문장에 이 사실 문장을
    /// 붙이면 거짓이 된다.
    /// </summary>
    public static class NonAggregateAssignmentExtractor
    {
        /// <summary>
        /// 확정 사실 문장. "DECLARE의 초기값이 아니다"라고 단정하지 않는 이유는, 이 문장
        /// 앞에 다른 대입이 없었다면 직전 값이 실제로 DECLARE의 초기값이기 때문이다 -
        /// 단정하면 그 경우에 표가 거짓을 말한다. "같다는 보장이 없습니다"는 두 경우 모두
        /// 참이면서 이행자가 놓치던 위험(앞선 대입이 남긴 값)을 그대로 경고한다.
        /// </summary>
        private const string FactSentence =
            "비집계 SELECT는 결과가 없으면 대입 자체가 일어나지 않습니다. "
            + "무결과 시 변수에는 이 문장 직전 값이 그대로 남습니다 — 이 문장 앞의 마지막 대입이 "
            + "남긴 값이며, DECLARE 시점의 초기값과 같다는 보장이 없습니다.";

        public static IReadOnlyList<NonAggregateAssignmentFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<NonAggregateAssignmentFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<NonAggregateAssignmentFact>();
                }

                var visitor = new NonAggregateAssignmentVisitor();
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[NonAggregateAssignmentExtractor] 비집계 대입 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<NonAggregateAssignmentFact>();
            }
        }

        private sealed class NonAggregateAssignmentVisitor : TSqlFragmentVisitor
        {
            public List<NonAggregateAssignmentFact> Facts { get; } = new();

            // AggregateAssignmentExtractor와 같은 이유로 QuerySpecification 단위로 훑는다:
            // SelectSetVariable 단독으로는 자신을 감싼 문장의 FromClause를 알 수 없다
            // (부모 포인터가 없다). ScriptDom은 Visit을 오버라이드해도 자식 순회를
            // 계속하므로 중첩된 QuerySpecification도 그대로 방문된다.
            public override void Visit(QuerySpecification node)
            {
                // FROM이 없으면 무결과가 성립하지 않는다 - 확정 사실 문장이 거짓이 된다.
                if (node.FromClause == null) return;

                foreach (var element in node.SelectElements)
                {
                    if (element is not SelectSetVariable setVariable) continue;

                    // 컬럼 참조만 담는다(클래스 주석의 "왜 컬럼 참조만 담는가").
                    if (setVariable.Expression is not ColumnReferenceExpression column) continue;
                    if (column.ColumnType != ColumnType.Regular) continue;

                    var parts = column.MultiPartIdentifier?.Identifiers;
                    if (parts == null || parts.Count == 0) continue;
                    var columnText = string.Join(".", parts.Select(id => id.Value));
                    if (string.IsNullOrWhiteSpace(columnText)) continue;

                    // AggregateAssignmentExtractor와 같은 방어 - 변수명을 모르면 대상 칸이
                    // 진술 불가능해지므로 행을 내지 않는다.
                    if (setVariable.Variable?.Name is not { } variable
                        || string.IsNullOrWhiteSpace(variable))
                    {
                        continue;
                    }

                    Facts.Add(new NonAggregateAssignmentFact(
                        setVariable.StartLine, variable, columnText, FactSentence));
                }
            }
        }
    }
}
