using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <param name="Inner">감쌈을 한 겹 벗긴 안쪽 식. 벗길 것이 없으면 원래 식 그대로.</param>
    /// <param name="WrapperName">벗겨낸 감쌈의 이름(<c>ISNULL</c>·<c>COALESCE</c>). 안 벗겼으면 null.</param>
    /// <param name="Fallback">그 감쌈의 기본값 리터럴. 안 벗겼으면 null.</param>
    public readonly record struct UnwrapResult(
        ScalarExpression Inner, string? WrapperName, Literal? Fallback);

    /// <summary>
    /// 변수 대입의 우변에서 **행 수를 바꾸지 않는 스칼라 감쌈**을 한 겹 벗기고, 안쪽이
    /// 「전부 컬럼 참조인 분기식」인지 판정한다.
    ///
    /// [왜 필요한가 - 2026-09-06 축 A 감사] 두 대입 추출기의 우변 가드가 각각
    /// <c>Expression is not ColumnReferenceExpression</c>(비집계)와
    /// <c>Expression is not FunctionCall</c> + 집계 이름 화이트리스트(집계)라,
    /// <c>ISNULL(SUM(x),0)</c> 같은 자리가 **두 그물 사이로 샜다** - 집계 쪽에는
    /// "집계 이름이 아님"으로, 비집계 쪽에는 "맨 컬럼이 아님"으로 각각 떨어진다.
    /// <c>IIF(...)</c>로 감싼 대입도 같은 이유로 통째로 빠졌다.
    ///
    /// [이름을 조심할 것 - 보존되는 것은 NULL이 아니라 행 수다] <c>ISNULL</c>은 NULL을
    /// 보존하지 않고 **대체한다.** 이 클래스가 벗기는 근거는 그 감쌈이 질의가 돌려주는
    /// **행 수**를 바꾸지 않는다는 것이다. 값이 바뀌는 쪽은 소비자가 문장으로 말해야 한다
    /// (<see cref="AggregateAssignmentExtractor"/>의 <c>ISNULL(&lt;집계&gt;, 리터럴)</c> 갈래).
    ///
    /// [한 겹만 벗긴다] 끝까지 벗기면 판정 범위가 소리 없이 넓어진다. 한 겹만 벗기고,
    /// 안쪽이 판정 가능한 모양이 아니면 소비자가 침묵한다 - 이 클래스는 「모르는 것을
    /// 확정 표에 싣는다」 방향으로 한 발도 가지 않는다(그 표는 「수정 금지」다).
    /// </summary>
    public static class AssignmentExpressionUnwrapper
    {
        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// <c>ISNULL(X, 리터럴)</c>·<c>COALESCE(X, 리터럴)</c>를 한 겹 벗긴다.
        /// 기본값이 리터럴이 아니면 벗기지 않는다 - 무결과 시 무엇이 들어가는지
        /// 기계가 말할 수 없어서, 벗기면 소비자가 없는 문장을 지어내게 된다.
        /// </summary>
        public static UnwrapResult Unwrap(ScalarExpression expression)
        {
            switch (expression)
            {
                case FunctionCall call
                    when string.Equals(call.FunctionName?.Value, "ISNULL", System.StringComparison.OrdinalIgnoreCase)
                         && call.Parameters.Count == 2
                         && call.Parameters[1] is Literal isNullDefault:
                    return new UnwrapResult(call.Parameters[0], "ISNULL", isNullDefault);

                case CoalesceExpression coalesce
                    when coalesce.Expressions.Count == 2
                         && coalesce.Expressions[1] is Literal coalesceDefault:
                    return new UnwrapResult(coalesce.Expressions[0], "COALESCE", coalesceDefault);

                default:
                    return new UnwrapResult(expression, null, null);
            }
        }

        /// <summary>
        /// 식이 컬럼 참조이거나, 모든 분기가 컬럼 참조인 <c>IIF</c>/<c>CASE</c>인지 본다.
        ///
        /// [왜 "전부"를 요구하는가] 한 분기라도 리터럴·산술식이면 그 분기가 골라졌을 때
        /// 대입값이 컬럼에서 오지 않는다. 그러면 비집계 확정 문장("무결과면 대입 자체가
        /// 일어나지 않는다")은 여전히 참이지만 대상 칸이 실을 것이 흐려진다. 좁게 잡는다.
        ///
        /// [ELSE 없는 CASE는 거른다] 어느 WHEN도 참이 아니면 NULL이 대입되므로
        /// "분기가 전부 컬럼"이라는 전제가 깨진다.
        /// </summary>
        public static bool TryColumnBranches(
            ScalarExpression expression, out IReadOnlyList<ColumnReferenceExpression> branches)
        {
            var found = new List<ColumnReferenceExpression>();
            branches = found;

            switch (expression)
            {
                case ColumnReferenceExpression column:
                    if (column.ColumnType != ColumnType.Regular) return false;
                    found.Add(column);
                    return true;

                case IIfCall iif:
                    return Collect(found, iif.ThenExpression, iif.ElseExpression);

                case SearchedCaseExpression searched:
                    return Collect(
                        found,
                        searched.WhenClauses.Select(w => w.ThenExpression)
                            .Concat(new[] { searched.ElseExpression }).ToArray());

                case SimpleCaseExpression simple:
                    return Collect(
                        found,
                        simple.WhenClauses.Select(w => w.ThenExpression)
                            .Concat(new[] { simple.ElseExpression }).ToArray());

                default:
                    return false;
            }
        }

        private static bool Collect(
            List<ColumnReferenceExpression> found, params ScalarExpression?[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (candidate is not ColumnReferenceExpression column) return false;
                if (column.ColumnType != ColumnType.Regular) return false;
                found.Add(column);
            }

            return found.Count > 0;
        }

        /// <summary>
        /// 조각의 원문 텍스트. 토큰 스트림에서 잘라 온다 - 재구성하면 원문에 없는 표기가
        /// 생긴다(형제 <see cref="DerivedTableColumnExtractor"/>·<see cref="CaseBranchExtractor"/>와
        /// 같은 방식이다).
        /// </summary>
        public static string TextOf(TSqlFragment? fragment)
        {
            if (fragment?.ScriptTokenStream == null) return string.Empty;

            var text = string.Concat(
                fragment.ScriptTokenStream
                    .Skip(fragment.FirstTokenIndex)
                    .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                    .Select(t => t.Text));

            return Whitespace.Replace(text, " ").Trim();
        }
    }
}
