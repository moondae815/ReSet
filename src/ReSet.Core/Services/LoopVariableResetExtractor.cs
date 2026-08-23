using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">대입문의 원본 줄 번호.</param>
    /// <param name="Variable">재설정되는 변수명.</param>
    /// <param name="Value">대입되는 상수 원문.</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record LoopVariableResetFact(
        int Line, string Variable, string Value, string Sentence);

    /// <summary>
    /// `WHILE` 본문 최상위에서 변수를 상수로 되돌리는 `SET`을 뽑는다.
    ///
    /// [왜 이것이 확정 사실인가] 지역 변수 표는 `DECLARE` 시점의 초기값을 싣는다. 루프
    /// 안에서 같은 변수를 다시 상수로 되돌리면 실제 반복의 시작값은 그 재설정이 정하는데,
    /// 표만 보면 `DECLARE`의 값이 매 반복의 시작값처럼 읽힌다. `UP_UTIL_SETTLE_PROC_ETC`가
    /// 그 실물이다 - 22행이 `DECLARE @v_intID INT = 0`, 69행이 커서 행마다 도는
    /// `SET @v_intID = 0`이다. 감사가 지적한 것도 이것이다("로직 흐름 4단계에 루프 내 0
    /// 재설정 없음. 지역 변수 표의 '초기값 0'은 DECLARE 시점 값").
    ///
    /// [비집계 대입과 짝이다] 이 사실 하나만으로는 위험이 보이지 않는다. 69행의 재설정과
    /// 71행의 비집계 대입(<see cref="NonAggregateAssignmentExtractor"/>)이 표에 함께
    /// 실려야, 무매칭 행에서 무슨 일이 벌어지는지가 읽는 사람 머릿속에서 조립된다.
    /// 그래서 이 추출기는 **이 지점의 사실만** 말하고 뒤에 무엇이 오는지는 말하지 않는다.
    ///
    /// [왜 이렇게 좁게 거르는가 - 실측] 코퍼스 24개 객체 중 `WHILE`을 가진 것은 4개이고,
    /// 그 `WHILE` 본문 안의 `SET`은 모두 11건이다. 그중 이 추출기가 담는 것은 3건뿐이다
    /// (`PROC_ETC` 69 · 113 · 114). 나머지 8건에 "반복마다 다시 실행되는 재설정"이라고
    /// 붙이면 거짓이 된다 - 실행 의미 표는 Critic 면제이고 L1이 축자 전사를 강제하므로
    /// 거짓 문장을 걸러 낼 장치가 없다. 그래서 세 조건이 **모두** 기계로 확인될 때만 담고,
    /// 아니면 침묵한다(AGENTS.md 범주 2 - 거짓 행보다 없는 행이 낫다).
    ///
    /// 1. **`WHILE` 본문의 최상위 문장이다.** 조건문 안에 있으면 반복마다 실행된다는 말이
    ///    거짓이다. 실측 6건이 이 모양이다(`PROC_ETC:139` · `SUMMARY_ETC:76·77·128·129` ·
    ///    `WORKDAY2:36`). `PROC_ETC:139`는 뒤에 `RETURN`이 붙어 평생 한 번만 실행될 수도
    ///    있다.
    /// 2. **대입값이 상수다**(리터럴, 부호가 붙어도 된다). 오른쪽이 자기 자신이나 다른
    ///    변수를 읽으면 재설정이 아니라 누적이거나 계산이다. 실측 3건이 이 모양이다
    ///    (`WORKDAY2:31` · `SUMMARY_ETC:135`가 누적, `SUMMARY_ETC:77·129`가 문자열 조립 -
    ///    뒤 둘은 조건 1에도 걸린다). 누적을 담으면 **종류 칸 "루프 내 재설정"이 첫 칸부터
    ///    거짓**이 된다. 직전 반복의 값이 남는 것이 누적에서는 버그가 아니라 요점이다.
    ///    `+=` 같은 복합 대입도 오른쪽이 리터럴이지만 직전 값을 읽으므로 제외한다.
    /// 3. **앞선 최상위 문장이 반복을 벗어나지 않는다.** `WHILE 1=1` + `IF ... BREAK`는
    ///    흔한 커서 관용구고, 그 뒤 문장에는 "반복마다"가 성립하지 않는다. **코퍼스에는
    ///    이 모양이 0건이다**(11건을 하나씩 확인). 그래도 거르는 이유는, 확인할 수 있는
    ///    조건을 확인하지 않은 채 강한 문장을 실으면 그것이 바로 거짓 행이 새는 길이기
    ///    때문이다. 앞 라운드에서 같은 부류의 문장이 8행 중 7행에서 거짓이었다.
    /// </summary>
    public static class LoopVariableResetExtractor
    {
        /// <summary>
        /// 확정 사실 문장. 세 절 모두 위 세 조건이 참일 때만 참이다.
        ///
        /// 마지막 절이 "이 지점의 값"에 머무는 것은 의도적이다. "이 재설정을 빠뜨리면
        /// 직전 값이 남는다"고 잘라 말하면 `PROC_ETC` 113·114행에서 거짓이 된다 - 그
        /// 둘은 바로 뒤에 무결과여도 반드시 대입되는 집계 SELECT가 와서, 재설정을 빼도
        /// 값이 남지 않는다. 뒤에 무엇이 오는지는 이 추출기가 보지 않으므로 말하지 않는다.
        /// </summary>
        private const string ResetSentence =
            "이 대입은 WHILE 본문의 최상위에 있고 앞에 루프를 벗어나는 문장이 없어, "
            + "반복마다 다시 실행됩니다. 대입값이 상수이므로 이 지점에서 변수는 언제나 "
            + "이 값이며, DECLARE 시점의 값도 직전 반복이 남긴 값도 아닙니다. "
            + "이행 시 이 재설정을 빠뜨리면 이 지점의 값이 앞선 실행이 남긴 값으로 바뀝니다.";

        public static IReadOnlyList<LoopVariableResetFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<LoopVariableResetFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<LoopVariableResetFact>();
                }

                var visitor = new LoopVariableResetVisitor();
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[LoopVariableResetExtractor] 루프 내 재설정 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<LoopVariableResetFact>();
            }
        }

        private sealed class LoopVariableResetVisitor : TSqlFragmentVisitor
        {
            public List<LoopVariableResetFact> Facts { get; } = new();

            // ScriptDom은 Visit을 오버라이드해도 자식 순회를 계속하므로 중첩된 WHILE도
            // 그대로 방문된다. 각 SET은 자신을 직접 담은 본문 하나에서만 보이므로 중복은
            // 생기지 않고, 중첩 루프의 SET은 자신의 안쪽 루프 기준으로 판정된다.
            public override void Visit(WhileStatement node)
            {
                foreach (var statement in TopLevelStatementsOf(node.Statement))
                {
                    if (statement is SetVariableStatement set) CollectIfReset(set);

                    // 이 문장이 반복을 벗어날 수 있으면 뒤 문장에는 "반복마다"가
                    // 성립하지 않는다. 자기 자신까지는 앞이 막지 않았으므로 위에서
                    // 먼저 담고, 그 다음에 훑기를 멈춘다.
                    if (CanLeaveTheIteration(statement)) return;
                }
            }

            private void CollectIfReset(SetVariableStatement set)
            {
                // `SET @v += 1`은 오른쪽이 리터럴이어도 직전 값을 읽는다.
                if (set.AssignmentKind != AssignmentKind.Equals) return;
                if (!IsConstant(set.Expression)) return;

                var variable = set.Variable?.Name;
                if (string.IsNullOrWhiteSpace(variable)) return;

                var value = TextOf(set.Expression);
                if (string.IsNullOrWhiteSpace(value)) return;

                Facts.Add(new LoopVariableResetFact(
                    set.StartLine, variable!, value, ResetSentence));
            }

            /// <summary>
            /// 본문의 최상위 문장들. `BEGIN`/`END`가 없는 단일 문장 본문도 그 문장이
            /// 최상위다.
            /// </summary>
            private static IEnumerable<TSqlStatement> TopLevelStatementsOf(TSqlStatement? body)
            {
                if (body == null) return Enumerable.Empty<TSqlStatement>();

                if (body is BeginEndBlockStatement block)
                {
                    return block.StatementList?.Statements
                           ?? (IEnumerable<TSqlStatement>)Array.Empty<TSqlStatement>();
                }

                return new[] { body };
            }

            /// <summary>
            /// 상수인가. 리터럴이거나 리터럴에 부호만 붙은 식이면 참이다. 컬럼도 변수도
            /// 함수 호출도 읽지 않으므로 값이 실행 이력에 달리지 않는다.
            /// </summary>
            private static bool IsConstant(ScalarExpression? expression) => expression switch
            {
                Literal => true,
                UnaryExpression unary
                    when unary.UnaryExpressionType is UnaryExpressionType.Negative
                                                   or UnaryExpressionType.Positive
                    => IsConstant(unary.Expression),
                _ => false
            };

            /// <summary>
            /// 이 문장 안에 반복을 벗어나거나 건너뛰는 흐름이 있는가. 안쪽 중첩 루프의
            /// `BREAK`는 바깥 루프를 벗어나지 않지만, 여기서는 가리지 않고 멈춘다 -
            /// 틀리는 방향이 "행을 덜 담는" 쪽이라 거짓 행을 만들지 않는다.
            /// </summary>
            private static bool CanLeaveTheIteration(TSqlStatement statement)
            {
                var detector = new LoopExitDetector();
                statement.Accept(detector);
                return detector.Found;
            }

            private static string TextOf(TSqlFragment? fragment)
            {
                if (fragment == null || fragment.ScriptTokenStream == null) return string.Empty;

                var text = string.Concat(
                    fragment.ScriptTokenStream
                        .Skip(fragment.FirstTokenIndex)
                        .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                        .Select(t => t.Text));

                return Regex.Replace(text, @"\s+", " ").Trim();
            }
        }

        private sealed class LoopExitDetector : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void Visit(ReturnStatement node) => Found = true;

            public override void Visit(BreakStatement node) => Found = true;

            public override void Visit(ContinueStatement node) => Found = true;

            public override void Visit(GoToStatement node) => Found = true;

            public override void Visit(ThrowStatement node) => Found = true;
        }
    }
}
