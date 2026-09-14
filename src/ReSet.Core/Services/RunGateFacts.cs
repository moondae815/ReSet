using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <summary>
    /// SQL 펜스에서 <b>실행 완료 게이트</b> 문장과 그 문장이 요구하는 단계 코드를 뽑는다.
    /// <see cref="MechanicalValidator"/> 의 「RunId 발급 전 단계를 요구하는 게이트」 검사 재료다.
    ///
    /// [게이트] 최상위 세미콜론 문장 중 <c>batch.BatchCheckpoint</c>·<c>batch.BatchStepJournal</c> 을 FROM·JOIN 으로 읽는 질의가 WHERE 에
    /// <c>RunId = @변수</c> 를 갖고, 같은 문장 어딘가에서 상태 칸(<c>CheckpointStatus</c>·<c>StepStatus</c>)을 <c>N'Succeeded'</c> 와 견주는 것.
    /// 실물 B12 S18 은 <c>Succeeded</c> 를 WHERE 가 아니라 <c>SUM(CASE WHEN …)</c> 안에서 본다 - 그래서 문장 단위로 본다.
    ///
    /// [요구 단계는 긍정 자리만] <c>StepCode = N'…'</c> · <c>StepCode IN (…)</c> · <c>StepCode BETWEEN N'…' AND N'…'</c> · 열 이름이 <c>StepCode</c> 인
    /// 인라인 <c>VALUES</c>. <c>&lt;&gt;</c>·<c>!=</c>·<c>NOT IN</c>·<c>NOT BETWEEN</c>·<c>NOT (…)</c> 아래는 세지 않는다 - 그 아래 <c>NOT EXISTS</c> 부질의의
    /// 체크포인트 읽기는 게이트 판정에는 쓴다(실물 B13 S19 의 모양).
    /// 판독: <c>docs/audit-reports/2026-09-14-RunId이전단계-게이트-사전선언.md</c>.
    /// </summary>
    internal static class RunGateFacts
    {
        internal sealed record Gate(string Statement, IReadOnlyCollection<string> RequiredStepCodes);

        private static readonly string[] GateTables = { "BatchCheckpoint", "BatchStepJournal" };

        private static readonly string[] StatusColumns = { "CheckpointStatus", "StepStatus" };

        private static readonly Regex StepCodeLiteral = new(@"^S\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static IReadOnlyList<Gate> Gates(string sql, IReadOnlyList<string> documentStepCodes)
        {
            var gates = new List<Gate>();
            if (string.IsNullOrWhiteSpace(sql)) return gates;

            var tokens = new TSql160Parser(initialQuotedIdentifiers: true).GetTokenStream(new StringReader(sql), out var tokenErrors);
            if (tokens == null || tokenErrors is { Count: > 0 }) return gates;

            foreach (var (start, end, _) in StepSqlStatementReader.SplitAtTopLevelSemicolons(sql, tokens))
            {
                var chunk = sql.Substring(start, end - start);
                if (string.IsNullOrWhiteSpace(chunk)) continue;

                var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(chunk), out var errors);
                if (fragment == null || errors is { Count: > 0 }) continue;

                var flags = new GateFlagVisitor();
                fragment.Accept(flags);
                if (!flags.ReadsGateTableByRunId || !flags.ComparesSucceeded) continue;

                var requirements = new RequirementVisitor(documentStepCodes);
                fragment.Accept(requirements);
                if (requirements.Codes.Count > 0) gates.Add(new Gate(chunk, requirements.Codes));
            }

            return gates;
        }

        /// <summary>
        /// [면제 재료 - 최종 리뷰 Important 1] 체크포인트·저널 표에 행을 쓰면서 <b>StepCode 칸에 실제로 넣는</b> 단계 코드 리터럴.
        /// <c>INSERT … VALUES</c> · <c>INSERT … SELECT</c> 의 그 칸 위치와 <c>MERGE … WHEN NOT MATCHED THEN INSERT (…) VALUES (…)</c> 만 본다.
        /// 처음엔 쓰기 문장 안의 <c>N'S01'</c> 을 전부 주워, <c>WHERE NOT EXISTS (… StepCode = N'S01')</c> 로 참조만 한 것까지 면제했다.
        /// </summary>
        internal static IReadOnlySet<string> WrittenStepCodes(string sql) =>
            InsertedColumnLiterals.Collect(sql, "StepCode", table => GateTables.Contains(table, StringComparer.OrdinalIgnoreCase))
                .Where(value => StepCodeLiteral.IsMatch(value))
                .Select(value => value.ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static string? LastIdentifier(ColumnReferenceExpression column) =>
            column.MultiPartIdentifier?.Identifiers is { Count: > 0 } ids ? ids[^1].Value : null;

        private static bool IsColumn(ScalarExpression expression, string name) =>
            expression is ColumnReferenceExpression column && LastIdentifier(column)?.Equals(name, StringComparison.OrdinalIgnoreCase) == true;

        private sealed class GateFlagVisitor : TSqlFragmentVisitor
        {
            internal bool ReadsGateTableByRunId { get; private set; }

            internal bool ComparesSucceeded { get; private set; }

            public override void ExplicitVisit(QuerySpecification node)
            {
                var readsGateTable = (node.FromClause?.TableReferences ?? (IList<TableReference>)Array.Empty<TableReference>())
                    .Any(ReferencesGateTable);
                if (readsGateTable && node.WhereClause?.SearchCondition != null && HasRunIdVariableFilter(node.WhereClause.SearchCondition))
                    ReadsGateTableByRunId = true;

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                if (node.ComparisonType == BooleanComparisonType.Equals &&
                    (IsStatusSucceeded(node.FirstExpression, node.SecondExpression) || IsStatusSucceeded(node.SecondExpression, node.FirstExpression)))
                    ComparesSucceeded = true;

                base.ExplicitVisit(node);
            }

            private static bool IsStatusSucceeded(ScalarExpression column, ScalarExpression literal) =>
                StatusColumns.Any(c => IsColumn(column, c)) &&
                literal is StringLiteral { Value: var value } && value.Equals("Succeeded", StringComparison.OrdinalIgnoreCase);

            private static bool ReferencesGateTable(TableReference reference) => reference switch
            {
                NamedTableReference named => named.SchemaObject?.BaseIdentifier?.Value is { } bare &&
                                             GateTables.Contains(bare, StringComparer.OrdinalIgnoreCase),
                JoinTableReference join => ReferencesGateTable(join.FirstTableReference) || ReferencesGateTable(join.SecondTableReference),
                JoinParenthesisTableReference parenthesis => ReferencesGateTable(parenthesis.Join),
                _ => false
            };

            private static bool HasRunIdVariableFilter(BooleanExpression expression) => expression switch
            {
                BooleanBinaryExpression binary => HasRunIdVariableFilter(binary.FirstExpression) || HasRunIdVariableFilter(binary.SecondExpression),
                BooleanParenthesisExpression parenthesis => HasRunIdVariableFilter(parenthesis.Expression),
                BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } comparison =>
                    (IsColumn(comparison.FirstExpression, "RunId") && comparison.SecondExpression is VariableReference) ||
                    (IsColumn(comparison.SecondExpression, "RunId") && comparison.FirstExpression is VariableReference),
                _ => false
            };
        }

        private sealed class RequirementVisitor : TSqlFragmentVisitor
        {
            private readonly IReadOnlyList<string> _documentStepCodes;

            internal RequirementVisitor(IReadOnlyList<string> documentStepCodes) => _documentStepCodes = documentStepCodes;

            internal HashSet<string> Codes { get; } = new(StringComparer.OrdinalIgnoreCase);

            // 단계 조건에 **바로** 걸린 부정(`NOT (StepCode = N'S01')`)은 요구가 아니다 - 내려가지 않는다.
            // `NOT EXISTS (…)` 는 내려간다: 실물 B13 S19 는 요구 목록 VALUES 가 `WHEN NOT EXISTS (… WHERE NOT EXISTS (체크포인트))` 안에 있다
            // (「빠진 것이 없다」 = 전부 요구). 처음엔 부정 아래를 통째로 건너뛰어 그 게이트를 놓쳤다.
            public override void ExplicitVisit(BooleanNotExpression node)
            {
                if (Unwrap(node.Expression) is ExistsPredicate) base.ExplicitVisit(node);
            }

            private static BooleanExpression Unwrap(BooleanExpression expression) =>
                expression is BooleanParenthesisExpression parenthesis ? Unwrap(parenthesis.Expression) : expression;

            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                if (node.ComparisonType == BooleanComparisonType.Equals)
                {
                    if (IsColumn(node.FirstExpression, "StepCode")) AddLiteral(node.SecondExpression);
                    else if (IsColumn(node.SecondExpression, "StepCode")) AddLiteral(node.FirstExpression);
                }

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(InPredicate node)
            {
                if (node.NotDefined) return;
                if (IsColumn(node.Expression, "StepCode"))
                    foreach (var value in node.Values) AddLiteral(value);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(BooleanTernaryExpression node)
            {
                if (node.TernaryExpressionType != BooleanTernaryExpressionType.Between) return;
                if (IsColumn(node.FirstExpression, "StepCode") &&
                    node.SecondExpression is StringLiteral { Value: var low } && StepCodeLiteral.IsMatch(low) &&
                    node.ThirdExpression is StringLiteral { Value: var high } && StepCodeLiteral.IsMatch(high))
                {
                    var (from, to) = (Number(low), Number(high));
                    foreach (var code in _documentStepCodes.Where(c => Number(c) >= from && Number(c) <= to)) Codes.Add(code);
                }

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(InlineDerivedTable node)
            {
                var position = node.Columns.ToList().FindIndex(c => c.Value.Equals("StepCode", StringComparison.OrdinalIgnoreCase));
                if (position >= 0)
                {
                    foreach (var row in node.RowValues)
                        if (row.ColumnValues.Count > position) AddLiteral(row.ColumnValues[position]);
                }

                base.ExplicitVisit(node);
            }

            private void AddLiteral(ScalarExpression expression)
            {
                if (expression is StringLiteral { Value: var value } && StepCodeLiteral.IsMatch(value)) Codes.Add(value.ToUpperInvariant());
            }

            private static int Number(string code) => int.TryParse(code.AsSpan(1), out var n) ? n : -1;
        }
    }
}
