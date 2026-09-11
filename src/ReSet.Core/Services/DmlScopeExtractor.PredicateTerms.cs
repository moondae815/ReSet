using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <summary>
    /// WHERE 최상위 AND 항 하나를 비교 가능한 모양으로 적은 것.
    ///
    /// [왜 컬럼 이름이 아니라 항인가] 검사 B·C 는 컬럼 <b>이름</b>만 대조한다. 그래서 이행이
    /// 원본 DELETE 의 <c>OUTYMD &gt;= @v_strReqYMD</c> 를 INSERT 에 베껴도 조용하다 -
    /// <c>OUTYMD</c> 는 다른 항(<c>ISNULL(OUTYMD,'') &lt;&gt; ''</c>)으로 원본에 이미 있다
    /// (Batch6·7 <c>S12</c> INSERT 4 실물). 설계:
    /// docs/superpowers/specs/2026-09-11-앵커-DML-최상위-술어-대조-design.md
    /// </summary>
    /// <param name="Normalized">대조 키. R1~R6 을 거친 모양이다 - 사람에게 보이지 않는다.</param>
    /// <param name="Raw">메시지에 싣는 원문. 주석을 빼고 공백을 한 칸으로 접었다.</param>
    /// <param name="Variables">R2 가 지우기 전의 변수 이름. 오케스트레이션 면제(E1)의 재료다.</param>
    /// <param name="IsJoinEquality">한정자가 다른 컬럼 = 컬럼 등식. N5 의 몫이라 이 대조에서 뺀다.</param>
    public sealed record PredicateTerm(
        string Normalized,
        string Raw,
        IReadOnlyList<string> Variables,
        bool IsJoinEquality);

    public static partial class DmlScopeExtractor
    {
        /// <summary>
        /// WHERE 절들의 최상위 AND 항을 정규화해 합집합으로 낸다(R6 - UNION 갈래마다 WHERE 가 다르다).
        ///
        /// [원본과 이행이 이 함수 하나를 부른다] 규칙이 두 곳에 있으면 조용히 갈린다 -
        /// <c>BareObjectName</c>·<c>ResolveOrdinal</c> 이 같은 이유로 공유된다.
        /// 「최상위」의 정의는 <see cref="TopLevelPredicateCollector.TopLevelAndTerms"/> 가 소유한다.
        ///
        /// [정규화 규칙]
        /// R1 한정자·3부 이름 접두를 벗긴다 · R2 변수를 <c>@V</c> 로 · R3 리터럴은 남기고 IN 목록은
        /// 정렬한 집합으로 · R4 컬럼을 좌변으로, 부등호는 뒤집는다 · R5 하위질의 안에도 R1·R2 를
        /// 적용하고 잠금 힌트·테이블 별칭을 버린다 · R6 절들의 합집합.
        /// R3·R4 는 항의 불리언 골격(AND·OR·NOT·괄호)까지 적용한다. 하위질의 본문은 토큰 단위
        /// 정규화(R1·R2·R5)만 받는다.
        ///
        /// [대가] 변수 동일성은 보지 않는다(R2). 동치 재작성(BETWEEN ↔ &gt;= AND &lt;=)은 미리
        /// 만들지 않는다 - 코퍼스에서 실물로 나올 때 더한다(설계 §2-2).
        /// </summary>
        public static IReadOnlyList<PredicateTerm> PredicateTermsOf(IEnumerable<WhereClause?> wheres)
        {
            var terms = new List<PredicateTerm>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var where in wheres ?? Enumerable.Empty<WhereClause?>())
            {
                foreach (var term in TopLevelPredicateCollector.TopLevelAndTerms(where?.SearchCondition))
                {
                    var normalized = NormalizePredicate(term);
                    if (normalized.Length == 0 || !seen.Add(normalized)) continue;

                    terms.Add(new PredicateTerm(
                        normalized, RawPredicateText(term), PredicateVariables(term), IsJoinEqualityTerm(term)));
                }
            }

            return terms;
        }

        private static string NormalizePredicate(BooleanExpression expression)
        {
            var node = StripBooleanParentheses(expression);

            switch (node)
            {
                case BooleanBinaryExpression binary:
                    var op = binary.BinaryExpressionType == BooleanBinaryExpressionType.And ? "AND" : "OR";
                    return $"{NormalizePredicateOperand(binary.FirstExpression, binary.BinaryExpressionType)} {op} " +
                           $"{NormalizePredicateOperand(binary.SecondExpression, binary.BinaryExpressionType)}";

                case BooleanNotExpression not:
                    return $"NOT ( {NormalizePredicate(not.Expression)} )";

                case BooleanComparisonExpression comparison:
                    return NormalizeComparisonTerm(comparison);

                case InPredicate inPredicate when inPredicate.Subquery == null && inPredicate.Values is { Count: > 0 }:
                    var values = inPredicate.Values
                        .Select(v => RenderNormalizedTokens(v))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(v => v, StringComparer.Ordinal);
                    return $"{RenderNormalizedTokens(inPredicate.Expression)} " +
                           $"{(inPredicate.NotDefined ? "NOT IN" : "IN")} ( {string.Join(" , ", values)} )";

                default:
                    return RenderNormalizedTokens(node);
            }
        }

        /// <summary>
        /// 괄호는 표기라 벗기되, 부모와 연산이 다른 이항식은 괄호로 감싸 우선순위를 지킨다.
        /// 같은 연산의 중첩(<c>A OR (B OR C)</c>)은 평탄화된다.
        /// </summary>
        private static string NormalizePredicateOperand(BooleanExpression child, BooleanBinaryExpressionType parentType)
        {
            var inner = StripBooleanParentheses(child);
            var text = NormalizePredicate(inner);
            return inner is BooleanBinaryExpression b && b.BinaryExpressionType != parentType ? $"( {text} )" : text;
        }

        private static BooleanExpression StripBooleanParentheses(BooleanExpression expression)
        {
            var node = expression;
            while (node is BooleanParenthesisExpression paren && paren.Expression != null) node = paren.Expression;
            return node;
        }

        private static string NormalizeComparisonTerm(BooleanComparisonExpression comparison)
        {
            var op = comparison.ComparisonType switch
            {
                BooleanComparisonType.Equals => "=",
                BooleanComparisonType.NotEqualToBrackets => "<>",
                BooleanComparisonType.NotEqualToExclamation => "<>",
                BooleanComparisonType.GreaterThan => ">",
                BooleanComparisonType.GreaterThanOrEqualTo => ">=",
                BooleanComparisonType.LessThan => "<",
                BooleanComparisonType.LessThanOrEqualTo => "<=",
                BooleanComparisonType.NotLessThan => ">=",
                BooleanComparisonType.NotGreaterThan => "<=",
                _ => null
            };
            if (op == null) return RenderNormalizedTokens(comparison);

            var left = RenderNormalizedTokens(comparison.FirstExpression);
            var right = RenderNormalizedTokens(comparison.SecondExpression);
            var leftHasColumn = TopLevelPredicateCollector.ContainsColumn(comparison.FirstExpression);
            var rightHasColumn = TopLevelPredicateCollector.ContainsColumn(comparison.SecondExpression);

            // R4 - 컬럼을 좌변으로. 부등호는 방향을 뒤집는다.
            if (!leftHasColumn && rightHasColumn)
            {
                (left, right) = (right, left);
                op = op switch { ">" => "<", "<" => ">", ">=" => "<=", "<=" => ">=", _ => op };
            }
            // 대칭 연산의 컬럼 대 컬럼은 두 변을 정렬해 방향을 없앤다(A.YMD = A.AYMD ↔ A.AYMD = A.YMD).
            else if (leftHasColumn && rightHasColumn && (op == "=" || op == "<>")
                     && StringComparer.Ordinal.Compare(left, right) > 0)
            {
                (left, right) = (right, left);
            }

            return $"{left} {op} {right}";
        }

        /// <summary>
        /// 조각의 토큰을 한 칸 공백으로 잇는다. 주석·공백은 버리고, 문자열 리터럴이 아닌 토큰은
        /// 대문자로 올리고 <c>[ ]</c> 를 벗긴다. R1·R2·R5 의 편집은 <see cref="PredicateTokenEdits"/> 가 정한다.
        /// </summary>
        private static string RenderNormalizedTokens(TSqlFragment? fragment)
        {
            if (fragment?.ScriptTokenStream == null || fragment.FirstTokenIndex < 0) return string.Empty;

            var edits = new PredicateTokenEdits();
            fragment.Accept(edits);

            var parts = new List<string>();
            for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
            {
                if (edits.Dropped.Contains(i)) continue;

                var token = fragment.ScriptTokenStream[i];
                if (IsPredicateTrivia(token)) continue;

                if (edits.Replaced.TryGetValue(i, out var replacement))
                {
                    parts.Add(replacement);
                    continue;
                }

                parts.Add(token.TokenType is TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral
                    ? token.Text
                    : token.Text.Trim('[', ']').ToUpperInvariant());
            }

            return string.Join(" ", parts);
        }

        private static bool IsPredicateTrivia(TSqlParserToken token) =>
            token.TokenType is TSqlTokenType.WhiteSpace
                or TSqlTokenType.SingleLineComment
                or TSqlTokenType.MultilineComment;

        /// <summary>
        /// 원문을 한 줄로 - 주석을 빼고 공백 연속을 한 칸으로 접는다. 주석을 남기면
        /// <c>--</c> 한 줄 주석이 접힌 뒤 뒤따르는 원문을 삼킨 것처럼 읽힌다.
        /// </summary>
        private static string RawPredicateText(TSqlFragment fragment)
        {
            var b = new StringBuilder();
            var pendingSpace = false;

            for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
            {
                var token = fragment.ScriptTokenStream[i];
                if (IsPredicateTrivia(token))
                {
                    pendingSpace = b.Length > 0;
                    continue;
                }

                if (pendingSpace) b.Append(' ');
                pendingSpace = false;
                b.Append(token.Text);
            }

            return b.ToString();
        }

        private static IReadOnlyList<string> PredicateVariables(TSqlFragment fragment)
        {
            var collector = new PredicateVariableCollector();
            fragment.Accept(collector);
            return collector.Names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsJoinEqualityTerm(BooleanExpression term) =>
            StripBooleanParentheses(term) is BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } comparison
            && comparison.FirstExpression is ColumnReferenceExpression left
            && comparison.SecondExpression is ColumnReferenceExpression right
            && TopLevelPredicateCollector.HaveDifferentQualifiers(left, right);

        /// <summary>
        /// R1·R2·R5 의 토큰 편집. 하위질의 안까지 내려간다(기본 방문자가 자식으로 내려간다).
        /// </summary>
        private sealed class PredicateTokenEdits : TSqlFragmentVisitor
        {
            public HashSet<int> Dropped { get; } = new();
            public Dictionary<int, string> Replaced { get; } = new();

            /// <summary>R1 - 마지막 식별자만 남긴다(<c>A.YMD</c> → <c>YMD</c>).</summary>
            public override void Visit(ColumnReferenceExpression node)
            {
                var parts = node.MultiPartIdentifier?.Identifiers;
                if (parts == null || parts.Count < 2) return;

                for (var i = node.FirstTokenIndex; i < parts[parts.Count - 1].FirstTokenIndex; i++) Dropped.Add(i);
            }

            /// <summary>
            /// R1·R5 - 테이블 참조는 기본 이름만 남긴다. 3부 이름 접두, 별칭, <c>AS</c>,
            /// 잠금 힌트(<c>WITH(NOLOCK)</c>)가 모두 이 노드의 토큰 범위 안에 있다.
            /// </summary>
            public override void Visit(NamedTableReference node)
            {
                var baseIdentifier = node.SchemaObject?.BaseIdentifier;
                if (baseIdentifier == null) return;

                for (var i = node.FirstTokenIndex; i <= node.LastTokenIndex; i++)
                {
                    if (i < baseIdentifier.FirstTokenIndex || i > baseIdentifier.LastTokenIndex) Dropped.Add(i);
                }
            }

            /// <summary>R2 - 변수 이름은 이행 자유도다(<c>@pi_strYMD</c> ↔ <c>@p_batchYmd</c>).</summary>
            public override void Visit(VariableReference node) => Replaced[node.FirstTokenIndex] = "@V";
        }

        private sealed class PredicateVariableCollector : TSqlFragmentVisitor
        {
            public List<string> Names { get; } = new();
            public override void Visit(VariableReference node) => Names.Add(node.Name);
        }
    }
}
