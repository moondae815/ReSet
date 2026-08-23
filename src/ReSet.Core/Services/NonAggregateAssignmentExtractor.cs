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
    /// 그러면 대입 자체가 일어나지 않아 변수는 이 문장에 도달한 시점의 값을 그대로
    /// 유지한다. 이 사실은 이 SP의 사정이 아니라 T-SQL 명세다.
    ///
    /// [집계 대입과 정반대다] <see cref="AggregateAssignmentExtractor"/>가 담는
    /// `SELECT @v = MAX(...)`는 GROUP BY가 없으면 무결과여도 한 행을 돌려주므로 대입이
    /// 항상 일어나고 NULL이 들어간다. 두 사실이 표에 나란히 놓여야 대비가 보인다.
    /// UP_UTIL_SETTLE_PROC_ETC가 72행(비집계) · 79행(집계)으로 둘 다 가진 실물이다.
    ///
    /// [남는 값이 무엇인지는 판정될 때만 말한다] 수정 라운드 1 - "직전 대입이 남긴 값이라
    /// DECLARE 초기값과 다르다"고 뭉뚱그리면 코퍼스 8행 중 7행에서 거짓이 된다. 그 7행은
    /// 앞선 대입이 아예 없어서 남는 값이 정확히 NULL이기 때문이다. 그래서 갈래를 둘로
    /// 나눈다(<see cref="AggregateAssignmentExtractor"/>가 초기값 유무로 문장을 가르는
    /// 것과 같은 방식이다).
    /// - 앞선 대입이 없다고 **판정되면**: 무결과 시 NULL이 남는다고 확정해서 말한다.
    /// - 그렇지 않으면: "이 문장에 도달한 시점의 값"까지만 말한다. 어떤 값인지는 기계가
    ///   모르므로 말하지 않는다.
    /// 판정 조건은 <see cref="SurvivingValueIsNull"/>에 있다.
    ///
    /// [왜 컬럼 참조만 담는가] 식이 집계를 품고 있으면 결론이 정반대로 뒤집힌다.
    /// 같은 SP의 101행 `SELECT @v = MAX(ID)+1`과 116행 `SELECT @v = ISNULL(SUM(...),0)`이
    /// 그 실물이다 - 최상위가 이항식/스칼라 함수라 집계 추출기는 담지 않지만, 질의 자체는
    /// 집계라 무결과여도 한 행이 돌아온다. 컬럼 참조는 잎 노드라 집계를 품을 수 없다.
    /// CASE 식이나 산술식은 대개 비집계지만 판정에 식 전체를 훑어야 하고 대상 칸 원문도
    /// 길어져, 담지 않고 침묵한다(AGENTS.md 범주 2와 같은 원칙 - 거짓 행보다 없는 행이
    /// 낫다).
    ///
    /// [집계는 FROM 절에도 산다] 수정 라운드 1 - 식이 컬럼 참조여도
    /// `FROM (SELECT MAX(ID) AS MaxID FROM t) X`처럼 파생 테이블이 집계를 품으면 원본이
    /// 비어도 한 행이 돌아온다. 그러면 이 SELECT는 0행이 되지 않아 "무결과"를 전제한
    /// 문장을 읽는 사람이 정반대로 이해한다. 그래서 FROM 절이 집계를 품으면 담지 않는다.
    /// **코퍼스에 이 모양은 없다** - 24개 객체의 object_definition.sql을 이 추출기로 훑어
    /// 이 가드 도입 전후 행이 8행으로 같음을 확인했다.
    ///
    /// [왜 FROM 절을 요구하는가] `SELECT @v = ID`처럼 FROM이 없으면 무결과라는 개념이
    /// 없다 - 한 행이 반드시 돌아와 대입이 일어난다. FROM이 없는 문장에 이 사실 문장을
    /// 붙이면 거짓이 된다.
    /// </summary>
    public static class NonAggregateAssignmentExtractor
    {
        /// <summary>
        /// 집계 함수 이름. AggregateAssignmentExtractor의 목록보다 넓다 - 그쪽은 담을
        /// 사실을 고르는 목록이지만 이쪽은 **거짓을 막는** 목록이라, 하나라도 새면
        /// 정반대 문장이 표에 실린다.
        /// </summary>
        private static readonly HashSet<string> AggregateNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "MIN", "MAX", "SUM", "AVG", "COUNT", "COUNT_BIG", "STDEV", "STDEVP",
            "VAR", "VARP", "CHECKSUM_AGG", "STRING_AGG", "GROUPING", "GROUPING_ID"
        };

        /// <summary>공통 앞머리. 두 갈래 모두 여기서 시작한다.</summary>
        private const string NoAssignmentClause =
            "비집계 SELECT는 결과가 없으면 대입 자체가 일어나지 않습니다. ";

        /// <summary>
        /// 앞선 대입이 없다고 판정된 갈래. T-SQL은 초기값 없는 DECLARE 변수를 NULL로
        /// 시작하므로, 이 문장 전에 대입이 하나도 실행되지 않았다면 남는 값은 NULL이다.
        /// </summary>
        private const string NullSurvivesSentence =
            NoAssignmentClause
            + "이 변수는 DECLARE에 초기값이 없고 이 문장 앞에서 대입되지 않으므로, "
            + "무결과 시 NULL이 그대로 남습니다.";

        /// <summary>
        /// 판정되지 않은 갈래. 어떤 값이 남는지는 말하지 않는다 - 앞선 대입이 실제로
        /// 실행됐는지는 실행 경로에 달렸고, 기계가 확정할 수 없다.
        /// </summary>
        private const string PreviousValueSurvivesSentence =
            NoAssignmentClause
            + "무결과 시 변수에는 이 문장에 도달한 시점의 값이 그대로 남습니다.";

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

                var scope = new VariableScopeVisitor();
                fragment.Accept(scope);

                var visitor = new NonAggregateAssignmentVisitor(scope);
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

        /// <summary>
        /// 대입문 앞에서 이 변수에 값이 들어간 적이 없다고 확정할 수 있는가.
        /// 넷을 모두 만족해야 한다 - 하나라도 어긋나면 값을 말하지 않는 갈래로 간다.
        ///
        /// 1. **매개변수가 아니다.** 매개변수 값은 호출자가 준다 - NULL로 시작한다는
        ///    보장이 없다.
        /// 2. **초기값 없는 DECLARE가 있고, 같은 이름의 초기값 있는 DECLARE는 없다.**
        ///    DECLARE를 못 찾으면 판정하지 않는다. 이름은 배치마다 다시 선언되는데 재료는
        ///    조각 전체에서 모으므로, 같은 이름이 두 모양으로 선언돼 있으면 어느 쪽이
        ///    이 문장의 변수인지 알 수 없다 - 그때도 판정하지 않는다.
        /// 3. **객체에 되돌아가는 흐름이 없다.** WHILE이나 GOTO가 있으면 원문 순서가
        ///    실행 순서를 보장하지 못한다 - 두 번째 반복에서는 *뒤에 있는* 대입이 이미
        ///    실행된 뒤 이 문장에 도달한다.
        /// 4. **이 변수가 이 문장 앞에 한 번도 나오지 않는다.** 읽기든 쓰기든 가리지 않고
        ///    참조 자체가 없어야 한다. 대입 문법을 하나하나 열거하는 것(SET · SELECT ·
        ///    FETCH INTO · OUTPUT 매개변수 · UPDATE의 변수 대입 …)보다 좁게 잡히지만,
        ///    열거에서 하나가 새면 거짓 행이 나온다. 읽기만 앞서는 경우를 함께 놓치는
        ///    대가로 열거 누락의 위험을 없앤다.
        /// </summary>
        private static bool SurvivingValueIsNull(VariableScopeVisitor scope, string variable, int offset)
        {
            if (scope.HasBackwardFlow) return false;
            if (scope.Parameters.Contains(variable)) return false;
            if (!scope.DeclaredWithoutInitializer.Contains(variable)) return false;
            if (scope.DeclaredWithInitializer.Contains(variable)) return false;
            return !scope.HasReferenceBefore(variable, offset);
        }

        /// <summary>
        /// 변수의 출신(매개변수 · DECLARE 초기값 유무)과 참조 위치, 그리고 되돌아가는
        /// 흐름의 유무를 한 번에 모은다.
        /// </summary>
        private sealed class VariableScopeVisitor : TSqlFragmentVisitor
        {
            private readonly Dictionary<string, int> _firstReferenceOffset =
                new(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> DeclaredWithoutInitializer { get; } = new(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> DeclaredWithInitializer { get; } = new(StringComparer.OrdinalIgnoreCase);

            public bool HasBackwardFlow { get; private set; }

            public bool HasReferenceBefore(string variable, int offset)
                => _firstReferenceOffset.TryGetValue(variable, out var first) && first < offset;

            public override void Visit(ProcedureParameter node)
            {
                var name = node.VariableName?.Value;
                if (!string.IsNullOrWhiteSpace(name)) Parameters.Add(name!);
            }

            public override void Visit(DeclareVariableElement node)
            {
                var name = node.VariableName?.Value;
                if (string.IsNullOrWhiteSpace(name)) return;
                if (node.Value != null) DeclaredWithInitializer.Add(name!);
                else DeclaredWithoutInitializer.Add(name!);
            }

            public override void Visit(VariableReference node)
            {
                var name = node.Name;
                if (string.IsNullOrWhiteSpace(name)) return;
                if (_firstReferenceOffset.TryGetValue(name, out var first) && first <= node.StartOffset)
                {
                    return;
                }

                _firstReferenceOffset[name] = node.StartOffset;
            }

            public override void Visit(WhileStatement node) => HasBackwardFlow = true;

            public override void Visit(GoToStatement node) => HasBackwardFlow = true;
        }

        /// <summary>FROM 절이 집계를 품었는지 본다(클래스 주석의 "집계는 FROM 절에도 산다").</summary>
        private sealed class AggregateInFromDetector : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void Visit(FunctionCall node)
            {
                var name = node.FunctionName?.Value;
                if (!string.IsNullOrWhiteSpace(name) && AggregateNames.Contains(name!)) Found = true;
            }
        }

        private sealed class NonAggregateAssignmentVisitor : TSqlFragmentVisitor
        {
            private readonly VariableScopeVisitor _scope;

            public NonAggregateAssignmentVisitor(VariableScopeVisitor scope) => _scope = scope;

            public List<NonAggregateAssignmentFact> Facts { get; } = new();

            // AggregateAssignmentExtractor와 같은 이유로 QuerySpecification 단위로 훑는다:
            // SelectSetVariable 단독으로는 자신을 감싼 문장의 FromClause를 알 수 없다
            // (부모 포인터가 없다). ScriptDom은 Visit을 오버라이드해도 자식 순회를
            // 계속하므로 중첩된 QuerySpecification도 그대로 방문된다.
            public override void Visit(QuerySpecification node)
            {
                // FROM이 없으면 무결과가 성립하지 않는다 - 확정 사실 문장이 거짓이 된다.
                if (node.FromClause == null) return;

                var aggregateInFrom = new AggregateInFromDetector();
                node.FromClause.Accept(aggregateInFrom);
                if (aggregateInFrom.Found) return;

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
                    if (setVariable.Variable is not { } variableReference
                        || string.IsNullOrWhiteSpace(variableReference.Name))
                    {
                        continue;
                    }
                    var variable = variableReference.Name;

                    var sentence = SurvivingValueIsNull(_scope, variable, variableReference.StartOffset)
                        ? NullSurvivesSentence
                        : PreviousValueSurvivesSentence;

                    Facts.Add(new NonAggregateAssignmentFact(
                        setVariable.StartLine, variable, columnText, sentence));
                }
            }
        }
    }
}
