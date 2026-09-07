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
    /// 「컬럼 참조 / 리터럴 / 그 둘의 산술식」이거나 그 셋을 결과로 갖는 분기식(한 겹만)인지
    /// 판정한다.
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
        /// 식이 「컬럼 참조 / 리터럴 / 그 둘의 산술식」이거나, 그 셋을 결과(<c>THEN</c>·
        /// <c>ELSE</c>)로 갖는 <c>IIF</c>/<c>CASE</c>(**한 겹만**)인지 본다.
        ///
        /// [2026-09-07 2 회차 - 왜 넓혔는가] 1 회차는 "분기가 전부 컬럼"만 담았다.
        /// 그런데 대상 칸이 이미 우변 원문을 축자로 싣게 됐으므로(1 회차 ②), 분기
        /// 결과가 리터럴이거나 산술식이어도 대상 칸이 흐려지지 않는다 - 가드가 자기
        /// 근거보다 오래 살아 있었다(설계서 §9-9 첫 항목). 그래서 담는 모양을
        /// 「컬럼/리터럴/산술식」으로 넓힌다.
        ///
        /// [왜 "한 겹"만 허용하는가] 통째로 완화(모든 산술식 재귀 허용)해 코퍼스를
        /// 돌려 보니, 분기 결과 안에 또 분기식(중첩 CASE)이 오는 자리 다섯에서 원본
        /// 줄 주석이 셀 안으로 섞여 「아는 것을 틀리게 싣는」 결과가 나왔다(설계서
        /// §10-1). 중첩 분기식·함수 호출·하위 질의를 배제하면 그 자리가 애초에
        /// 안 들어온다 - 길이가 아니라 모양으로 자르는 것이다.
        ///
        /// [ELSE 없는 CASE는 거른다] 어느 WHEN도 참이 아니면 NULL이 대입되므로
        /// "분기 결과가 셋 중 하나"라는 전제가 깨진다.
        /// </summary>
        public static bool IsCapturableExpression(ScalarExpression? expression)
            => IsCapturableExpression(expression, allowBranchExpression: true);

        private static bool IsCapturableExpression(ScalarExpression? expression, bool allowBranchExpression)
        {
            switch (expression)
            {
                case ColumnReferenceExpression column:
                    return column.ColumnType == ColumnType.Regular;

                case Literal:
                    return true;

                case ParenthesisExpression paren:
                    return IsCapturableExpression(paren.Expression, allowBranchExpression);

                case UnaryExpression unary:
                    return IsCapturableExpression(unary.Expression, allowBranchExpression);

                case BinaryExpression binary:
                    return IsCapturableExpression(binary.FirstExpression, allowBranchExpression)
                           && IsCapturableExpression(binary.SecondExpression, allowBranchExpression);

                case IIfCall iif when allowBranchExpression:
                    return IsBranchResult(iif.ThenExpression) && IsBranchResult(iif.ElseExpression);

                case SearchedCaseExpression searched when allowBranchExpression:
                    return searched.ElseExpression != null
                           && searched.WhenClauses.All(w => IsBranchResult(w.ThenExpression))
                           && IsBranchResult(searched.ElseExpression);

                case SimpleCaseExpression simple when allowBranchExpression:
                    return simple.ElseExpression != null
                           && simple.WhenClauses.All(w => IsBranchResult(w.ThenExpression))
                           && IsBranchResult(simple.ElseExpression);

                default:
                    return false;
            }

            // 분기 결과(THEN·ELSE) 하나. 재귀 허용이지만 분기식은 한 겹만 - 결과 안에
            // 또 분기식이 오면 배제한다(중첩 CASE·IIF).
            bool IsBranchResult(ScalarExpression? candidate)
                => IsCapturableExpression(candidate, allowBranchExpression: false);
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
