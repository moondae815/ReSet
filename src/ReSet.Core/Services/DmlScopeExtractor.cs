using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Operation">"UPDATE" 또는 "DELETE".</param>
    /// <param name="Line">원본 DDL에서의 줄 번호(1부터) - 해당 문장 자체의 시작 줄이다.</param>
    /// <param name="Target">갱신·삭제 대상의 원문 표기 (파서가 정규화하지 않은 소스 그대로).</param>
    /// <param name="PredicateColumns">WHERE 최상위가 거르는 컬럼 이름.</param>
    /// <param name="DateParameterApplied">
    /// 기준일 파라미터가 <b>대상 범위에</b> 적용되는가. 서브쿼리 안에만 있으면 false다.
    /// 이 칸 하나가 A1 결함 넷 중 셋을 드러낸다.
    /// </param>
    /// <param name="JoinKeys">FROM 절 조인의 ON 조건이 쓰는 컬럼 이름.</param>
    public sealed record DmlScopeFact(
        string Operation,
        int Line,
        string Target,
        IReadOnlyList<string> PredicateColumns,
        bool DateParameterApplied,
        IReadOnlyList<string> JoinKeys);

    /// <summary>
    /// DML 문장별로 "무엇이 대상 범위를 정하는가"를 뽑는다.
    ///
    /// 명세서가 부재를 서술했는지는 자연어 판정이라 앵커가 없다. 그래서 이 재료는
    /// 서술을 요구하지 않고 <b>표</b>를 강제하는 데 쓴다 - 프롬프트가 표를 채워
    /// 주고 L1은 행의 존재와 확정 값의 보존만 본다. CheckUpdateMappings와 같은 형태다.
    ///
    /// 값과 연산자는 담지 않는다. 축 B가 이미 결론 낸 지점이다 - 값까지 대조하면
    /// 노이즈다(SpecConditionColumnExtractor 주석). 조인 키의 유일성도 판정하지
    /// 않는다 - 프롬프트 규칙이 이미 "추측하지 마라"고 못박았다.
    ///
    /// [MERGE·CTE 기반 UPDATE] 실측 SP 24건 어디에도 MERGE와 CTE 기반 UPDATE가
    /// 없었다(전수 grep 확인). 이 방문자는 UpdateSpecification/DeleteSpecification만
    /// 방문하므로 MergeStatement는 애초에 매칭되지 않아 조용히 빠진다 - 예외를
    /// 던지지 않고 그 문장 하나가 표에 실리지 않을 뿐이다. WITH 절이 있는 CTE는
    /// ScriptDom에서 WithCtesAndXmlNamespaces로 감싸이지만 그 안의 UpdateSpecification/
    /// DeleteSpecification 자체는 그대로 방문되므로(방문자가 자식으로 계속 내려간다)
    /// CTE 기반 UPDATE가 나타나도 처리는 된다 - 다만 실측 코퍼스에는 이 형태가 없어
    /// 실물로 검증하지는 못했다.
    /// </summary>
    public static class DmlScopeExtractor
    {
        public const string DmlScopeTableHeading = "### DML 범위 (기계 확정 — 수정 금지)";

        public static IReadOnlyList<DmlScopeFact> Extract(string? ddlText, string dateParameterName)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<DmlScopeFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null) return Array.Empty<DmlScopeFact>();

                var visitor = new DmlScopeVisitor(dateParameterName ?? string.Empty);
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[DmlScopeExtractor] DML 범위 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<DmlScopeFact>();
            }
        }

        private sealed class DmlScopeVisitor : TSqlFragmentVisitor
        {
            private readonly string _dateParameter;

            public DmlScopeVisitor(string dateParameter) => _dateParameter = dateParameter;

            public List<DmlScopeFact> Facts { get; } = new();

            public override void Visit(UpdateSpecification node) =>
                Record("UPDATE", node, node.Target, node.WhereClause, node.FromClause);

            public override void Visit(DeleteSpecification node) =>
                Record("DELETE", node, node.Target, node.WhereClause, node.FromClause);

            private void Record(
                string operation,
                TSqlFragment statement,
                TableReference? target,
                WhereClause? where,
                FromClause? from)
            {
                var predicateColumns = new List<string>();
                var dateApplied = false;

                if (where?.SearchCondition != null)
                {
                    // 최상위 술어만 본다. 서브쿼리 안의 조건은 대상 범위를
                    // 정하지 않는다 - 그 구분이 이 추출기의 존재 이유다.
                    var top = new TopLevelPredicateCollector();
                    where.SearchCondition.Accept(top);
                    predicateColumns.AddRange(top.Columns);
                    dateApplied = _dateParameter.Length > 0
                        && top.Parameters.Contains(_dateParameter, StringComparer.OrdinalIgnoreCase);
                }

                var joinKeys = new List<string>();
                if (from != null)
                {
                    var joins = new JoinConditionCollector();
                    from.Accept(joins);
                    joinKeys.AddRange(joins.Columns);
                }

                Facts.Add(new DmlScopeFact(
                    operation,
                    // 문장 자체의 시작 줄이다. 감싸는 BEGIN...END 블록이 아니라
                    // 이 UPDATE/DELETE 키워드가 나오는 줄이어야 사람이 raw DDL을
                    // 열었을 때 바로 그 행을 찾는다.
                    statement.StartLine,
                    TextOf(target),
                    predicateColumns,
                    dateApplied,
                    joinKeys));
            }

            private static string TextOf(TSqlFragment? fragment)
            {
                if (fragment == null || fragment.ScriptTokenStream == null) return string.Empty;

                return string.Concat(
                    fragment.ScriptTokenStream
                        .Skip(fragment.FirstTokenIndex)
                        .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                        .Select(t => t.Text)).Trim();
            }
        }

        /// <summary>
        /// WHERE 최상위 술어의 컬럼과 파라미터. 서브쿼리 안으로 내려가지 않는다 -
        /// EXISTS(... B.YMD = @pi_strYMD ...)는 대상 범위를 좁히지 않기 때문이다.
        /// </summary>
        private sealed class TopLevelPredicateCollector : TSqlFragmentVisitor
        {
            public List<string> Columns { get; } = new();
            public List<string> Parameters { get; } = new();

            public override void ExplicitVisit(ScalarSubquery node) { }
            public override void ExplicitVisit(ExistsPredicate node) { }

            /// <summary>
            /// PLTID IN (SELECT ...) 형태(EXCEPTION_PROC 실행순서 18 실측)에서 왼쪽
            /// 피연산자(PLTID)는 대상 범위를 실제로 좁히므로 잃으면 안 된다. 오른쪽이
            /// 서브쿼리면 그 안으로는 내려가지 않는다 - 대상 범위를 정하지 않는 남의
            /// 스코프다. 값 목록(IN (1,2,3))이면 서브쿼리가 없으므로 그대로 내려간다.
            /// </summary>
            public override void ExplicitVisit(InPredicate node)
            {
                node.Expression?.Accept(this);

                if (node.Subquery == null && node.Values != null)
                {
                    foreach (var value in node.Values)
                    {
                        value.Accept(this);
                    }
                }
            }

            public override void Visit(ColumnReferenceExpression node)
            {
                var name = node.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value;
                if (!string.IsNullOrWhiteSpace(name)
                    && !Columns.Contains(name!, StringComparer.OrdinalIgnoreCase))
                {
                    Columns.Add(name!);
                }
            }

            public override void Visit(VariableReference node)
            {
                if (!Parameters.Contains(node.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Parameters.Add(node.Name);
                }
            }
        }

        /// <summary>조인 ON 조건이 쓰는 컬럼. ANSI JOIN(ON)만 본다 - 콤마로 나열한 옛
        /// 스타일 조인(FROM A, B WHERE A.X = B.Y)의 결합 조건은 WHERE 최상위 술어로
        /// 이미 PredicateColumns에 잡힌다(COMM_UPD 문장 7 실측). 여기서 또 잡으면
        /// 중복 앵커일 뿐이라 그대로 둔다.</summary>
        private sealed class JoinConditionCollector : TSqlFragmentVisitor
        {
            public List<string> Columns { get; } = new();

            public override void Visit(QualifiedJoin node)
            {
                if (node.SearchCondition == null) return;

                var collector = new TopLevelPredicateCollector();
                node.SearchCondition.Accept(collector);

                foreach (var column in collector.Columns)
                {
                    if (!Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                    {
                        Columns.Add(column);
                    }
                }
            }
        }
    }
}
