using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Operation">"INSERT", "UPDATE", "DELETE" 중 하나.</param>
    /// <param name="Line">원본 DDL에서의 줄 번호(1부터) - 해당 문장 자체의 시작 줄이다.</param>
    /// <param name="Target">갱신·삭제 대상의 원문 표기 (파서가 정규화하지 않은 소스 그대로).</param>
    /// <param name="PredicateColumns">
    /// WHERE 최상위가 거르는 컬럼 이름. INSERT는 원천 SELECT의 최상위 WHERE를 본다.
    /// </param>
    /// <param name="DateParameterApplied">
    /// 기준일 파라미터가 <b>대상 범위에</b> 적용되는가. 서브쿼리 안에만 있으면 false다.
    /// 이 칸 하나가 A1 결함 넷 중 셋을 드러낸다.
    /// </param>
    /// <param name="JoinKeys">
    /// 테이블을 잇는 컬럼 이름. ANSI JOIN의 ON 조건과, 콤마로 나열한 옛 스타일
    /// 조인(FROM A, B WHERE A.X = B.Y)의 컬럼=컬럼 동등비교를 모두 담는다 -
    /// 후자는 PredicateColumns와 겹칠 수 있다(같은 WHERE 텍스트가 필터와 조인
    /// 역할을 동시에 하기 때문).
    /// </param>
    public sealed record DmlScopeFact(
        string Operation,
        int Line,
        string Target,
        IReadOnlyList<string> PredicateColumns,
        bool DateParameterApplied,
        IReadOnlyList<string> JoinKeys);

    /// <param name="Operation">"INSERT", "UPDATE", "DELETE" 중 하나.</param>
    /// <param name="Line">원본 DDL에서 그 문장이 시작하는 줄 번호(1부터).</param>
    /// <param name="Column">IN 좌변의 컬럼 이름.</param>
    /// <param name="IsNegated">NOT IN이면 true.</param>
    /// <param name="Literals">
    /// 집합의 원소를 원문 그대로 담는다 - 문자열은 따옴표를 포함한다('PLCard').
    /// 파생 테이블 정의 표가 표현식 원문을 그대로 싣는 것과 같은 이유이고, 표에서
    /// 문자열과 숫자를 구분할 수 있게 한다.
    /// </param>
    public sealed record SetPredicateFact(
        string Operation,
        int Line,
        string Column,
        bool IsNegated,
        IReadOnlyList<string> Literals);

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
        public const string SetPredicateTableHeading = "### 집합 술어 (기계 확정 — 수정 금지)";

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

        /// <summary>
        /// DML 최상위 WHERE의 IN/NOT IN 리터럴 목록을 뽑는다.
        ///
        /// [왜 별도 진입점인가] "어디까지가 대상 범위를 정하는 술어인가"라는 지식은
        /// TopLevelPredicateCollector 한 곳에 인코딩돼 있다. 새 추출기가 그 순회를
        /// 다시 구현하면 두 정의가 갈라지고, 그 순간 이 재료는 프롬프트가 말하는
        /// "최상위"와 다른 것을 뜻하게 된다. 그래서 수집기를 넓히고 진입점만 나눈다 -
        /// 순회는 두 번 돌지만 비용은 무시할 수준이고 주인은 계속 한 곳이다.
        /// </summary>
        public static IReadOnlyList<SetPredicateFact> ExtractSetPredicates(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<SetPredicateFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null) return Array.Empty<SetPredicateFact>();

                var visitor = new SetPredicateVisitor();
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[DmlScopeExtractor] 집합 술어 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<SetPredicateFact>();
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

            /// <summary>
            /// INSERT도 담는다.
            ///
            /// [왜 담아야 하는가] 표의 이름은 "DML 범위"이고 "기계 확정 — 수정 금지"라
            /// 못 박혀 있다. 그런데 UPDATE/DELETE만 담던 동안 INSERT를 가진 SP 8개가
            /// 2026-08-18 축 A 감사에서 전부 걸렸다. UP_UTIL_STAT_PGCOLLECT_INS는
            /// 삭제 전용 SP처럼 보였고, UP_Util_PG_Client_CMRate_Ins는 INSERT 5문이
            /// <b>라인 앵커가 붙은 유일한 표</b>에서 통째로 빠져 추적 근거를 잃었으며,
            /// UP_UTIL_SETTLE_SUMMARY_EXTRA는 같은 문서의 상태코드 표가 "8개 DML 단계"
            /// 라고 적어 문서 내부 모순까지 났다.
            ///
            /// [열 의미는 그대로다] 이 표의 열은 "무엇이 대상 범위를 정하는가"를 묻는다.
            /// INSERT ... SELECT에서 그 답은 원천 SELECT의 최상위 WHERE다 - 어느 행이
            /// 실리는지를 그것이 정한다. INSERT ... VALUES는 조건이 없으므로 술어가
            /// 비고 기준일도 false다(그것이 사실이다 - 무조건 한 행이 실린다).
            /// UNION으로 묶인 원천은 갈래마다 WHERE가 다르므로 전부 합쳐 담는다.
            /// </summary>
            public override void Visit(InsertSpecification node)
            {
                var predicateColumns = new List<string>();
                var joinKeys = new List<string>();
                var dateApplied = false;

                foreach (var spec in SourceQuerySpecifications(node.InsertSource))
                {
                    if (spec.WhereClause?.SearchCondition != null)
                    {
                        var top = new TopLevelPredicateCollector();
                        spec.WhereClause.SearchCondition.Accept(top);
                        foreach (var c in top.Columns)
                        {
                            if (!predicateColumns.Contains(c, StringComparer.OrdinalIgnoreCase)) predicateColumns.Add(c);
                        }
                        foreach (var k in top.JoinKeys)
                        {
                            if (!joinKeys.Contains(k, StringComparer.OrdinalIgnoreCase)) joinKeys.Add(k);
                        }
                        dateApplied |= _dateParameter.Length > 0
                            && top.Parameters.Contains(_dateParameter, StringComparer.OrdinalIgnoreCase);
                    }

                    if (spec.FromClause != null)
                    {
                        var joins = new JoinConditionCollector();
                        spec.FromClause.Accept(joins);
                        foreach (var k in joins.Columns)
                        {
                            if (!joinKeys.Contains(k, StringComparer.OrdinalIgnoreCase)) joinKeys.Add(k);
                        }
                    }
                }

                Facts.Add(new DmlScopeFact(
                    "INSERT", node.StartLine, TextOf(node.Target),
                    predicateColumns, dateApplied, joinKeys));
            }

            /// <summary>
            /// INSERT의 원천에서 QuerySpecification을 전부 끌어낸다. VALUES 원천이면
            /// 아무것도 내지 않는다 - 조건 없이 실리는 행이라 대조할 술어가 없다.
            /// </summary>
            private static IEnumerable<QuerySpecification> SourceQuerySpecifications(InsertSource? source) =>
                source is SelectInsertSource select
                    ? QuerySpecificationsOf(select.Select)
                    : Enumerable.Empty<QuerySpecification>();

            private void Record(
                string operation,
                TSqlFragment statement,
                TableReference? target,
                WhereClause? where,
                FromClause? from)
            {
                var predicateColumns = new List<string>();
                var dateApplied = false;
                var joinKeys = new List<string>();

                if (where?.SearchCondition != null)
                {
                    // 최상위 술어만 본다. 서브쿼리 안의 조건은 대상 범위를
                    // 정하지 않는다 - 그 구분이 이 추출기의 존재 이유다.
                    var top = new TopLevelPredicateCollector();
                    where.SearchCondition.Accept(top);
                    predicateColumns.AddRange(top.Columns);
                    dateApplied = _dateParameter.Length > 0
                        && top.Parameters.Contains(_dateParameter, StringComparer.OrdinalIgnoreCase);

                    // 콤마로 나열한 옛 스타일 조인(FROM A, B WHERE A.X = B.Y)의 결합
                    // 조건은 ON절이 없어 WHERE 최상위에 있다 - EXCEPTION_PROC 실행순서
                    // 3(108행)/4(130행) 실측. 이 값은 의도적으로 predicateColumns와
                    // 중복될 수 있다 - "WHERE에 나온 컬럼"과 "테이블을 잇는 컬럼"은
                    // 다른 질문이고, 같은 텍스트(WHERE)가 두 역할을 동시에 하는 것이
                    // 콤마 조인의 실제 구조이기 때문이다. ON절 조인은 애초에 WHERE가
                    // 아니므로 predicateColumns에 실리지 않는다 - 편집으로 뺀 게 아니라
                    // 소스 텍스트 구조가 다른 것이다.
                    foreach (var key in top.JoinKeys)
                    {
                        if (!joinKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                        {
                            joinKeys.Add(key);
                        }
                    }
                }

                if (from != null)
                {
                    var joins = new JoinConditionCollector();
                    from.Accept(joins);
                    foreach (var key in joins.Columns)
                    {
                        if (!joinKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                        {
                            joinKeys.Add(key);
                        }
                    }
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
        /// INSERT의 원천에서 QuerySpecification을 전부 끌어낸다. VALUES 원천이면
        /// 아무것도 내지 않는다 - 조건 없이 실리는 행이라 대조할 술어가 없다.
        /// </summary>
        private static IEnumerable<QuerySpecification> QuerySpecificationsOf(QueryExpression? query)
        {
            switch (query)
            {
                case QuerySpecification spec:
                    yield return spec;
                    break;
                case BinaryQueryExpression binary:
                    foreach (var s in QuerySpecificationsOf(binary.FirstQueryExpression)) yield return s;
                    foreach (var s in QuerySpecificationsOf(binary.SecondQueryExpression)) yield return s;
                    break;
                case QueryParenthesisExpression paren:
                    foreach (var s in QuerySpecificationsOf(paren.QueryExpression)) yield return s;
                    break;
            }
        }

        /// <summary>
        /// DML 문장을 찾아 그 최상위 WHERE에서 집합 술어를 모으고, 수집기가 모르는
        /// 문장 문맥(연산 종류·시작 줄)을 붙인다.
        /// </summary>
        private sealed class SetPredicateVisitor : TSqlFragmentVisitor
        {
            public List<SetPredicateFact> Facts { get; } = new();

            public override void Visit(UpdateSpecification node) =>
                Collect("UPDATE", node, node.WhereClause);

            public override void Visit(DeleteSpecification node) =>
                Collect("DELETE", node, node.WhereClause);

            public override void Visit(InsertSpecification node)
            {
                // INSERT ... SELECT의 대상 범위는 원천 SELECT의 최상위 WHERE가 정한다
                // (DmlScopeExtractor.Visit(InsertSpecification)와 같은 판단). UNION으로
                // 묶인 원천은 갈래마다 WHERE가 다르므로 전부 훑는다.
                if (node.InsertSource is not SelectInsertSource select) return;

                foreach (var spec in QuerySpecificationsOf(select.Select))
                {
                    Collect("INSERT", node, spec.WhereClause);
                }
            }

            private void Collect(string operation, TSqlFragment statement, WhereClause? where)
            {
                if (where?.SearchCondition == null) return;

                var top = new TopLevelPredicateCollector();
                where.SearchCondition.Accept(top);

                foreach (var (column, isNegated, literals) in top.SetPredicates)
                {
                    Facts.Add(new SetPredicateFact(
                        operation, statement.StartLine, column, isNegated, literals));
                }
            }
        }

        /// <summary>
        /// WHERE 최상위 술어의 컬럼과 파라미터. 서브쿼리 안으로 내려가지 않는다 -
        /// EXISTS(... B.YMD = @pi_strYMD ...)는 대상 범위를 좁히지 않기 때문이다.
        ///
        /// 부수적으로 <see cref="JoinKeys"/>도 모은다 - 컬럼 = 컬럼 형태의 최상위
        /// 동등비교는 콤마로 나열한 옛 스타일 조인(ON절이 없는 FROM A, B)의 결합
        /// 조건이 WHERE에 그대로 놓인 것일 수 있다. 다만 두 한정자가 서로 다를
        /// 때만 조인 키로 본다(<see cref="HaveDifferentQualifiers"/>) - 같은 별칭
        /// 안의 비교(A.YMD = A.AYMD)나 한정자를 알 수 없는 비교(TID = CID, A.TID =
        /// CID)는 조인이라고 주장할 근거가 없다(리뷰 라운드 2 실측: EXCEPTION_PROC
        /// 210/228/271/290행, COMM_UPD 58행, EXPECT_PROC 48행이 모두 이 오탐이었다).
        /// </summary>
        private sealed class TopLevelPredicateCollector : TSqlFragmentVisitor
        {
            public List<string> Columns { get; } = new();
            public List<string> Parameters { get; } = new();
            public List<string> JoinKeys { get; } = new();

            /// <summary>
            /// 최상위 IN/NOT IN의 리터럴 집합. Column은 좌변 컬럼 이름, IsNegated는
            /// NOT 여부, Literals는 원문 그대로다. Operation과 Line은 이 수집기가
            /// 모르므로(문장 문맥은 호출부가 안다) 호출부가 채운다.
            /// </summary>
            public List<(string Column, bool IsNegated, List<string> Literals)> SetPredicates { get; } = new();

            public override void ExplicitVisit(ScalarSubquery node) { }

            /// <summary>
            /// EXISTS 서브쿼리 자체는 대상 범위를 좁히지 않지만, 그 안에서 바깥
            /// 별칭을 참조하는 상관(correlated) 조건은 실제로 좁힌다 - 예:
            /// EXISTS (SELECT 1 FROM B WHERE B.PLTID = A.PLTID)의 A.PLTID. 서브쿼리
            /// 자신의 FROM이 선언한 별칭이 아닌 한정자를 쓰는 컬럼만 "바깥 참조"로
            /// 본다 - 어느 쪽이 진짜 대상인지 추측하지 않고, 서브쿼리 스스로 선언하지
            /// 않은 이름이라는 사실 하나로 판단한다. 파라미터는 이 서브쿼리 안에
            /// 있으면 이유를 막론하고 대상에 적용된 것으로 세지 않는다 - 그 판정은
            /// 여전히 바깥 최상위 WHERE에서만 이뤄진다(CorrelatedOuterColumnCollector가
            /// VariableReference를 아예 수집하지 않는 이유).
            ///
            /// [알려진 한계 - 고치지 않기로 함, 리뷰 라운드 2] EXISTS 안에 또 다른
            /// EXISTS가 중첩되면(2단 상관 서브쿼리) CorrelatedOuterColumnCollector가
            /// ExistsPredicate를 스스로 억제하므로 안쪽 EXISTS의 상관 컬럼은 담기지
            /// 않는다 - 진짜 바깥(최상위) 테이블을 참조하더라도 마찬가지다. 놓치는
            /// 방향(과소 수집)이라 안전하지만 정보 손실은 있다. 실측 코퍼스에 이
            /// 형태가 없어 픽스처도 만들지 않았다 - 나타나면 그때 3단 이상 재귀
            /// 판정을 넣는다.
            /// </summary>
            public override void ExplicitVisit(ExistsPredicate node)
            {
                if (node.Subquery?.QueryExpression is not QuerySpecification spec
                    || spec.WhereClause?.SearchCondition == null)
                {
                    return;
                }

                var localAliases = CollectLocalAliases(spec.FromClause);
                var correlated = new CorrelatedOuterColumnCollector(localAliases);
                spec.WhereClause.SearchCondition.Accept(correlated);

                foreach (var column in correlated.Columns)
                {
                    if (!Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                    {
                        Columns.Add(column);
                    }
                }
            }

            /// <summary>
            /// PLTID IN (SELECT ...) 형태(EXCEPTION_PROC 실행순서 18 실측)에서 왼쪽
            /// 피연산자(PLTID)는 대상 범위를 실제로 좁히므로 잃으면 안 된다. 오른쪽이
            /// 서브쿼리면 그 안으로는 내려가지 않는다 - 대상 범위를 정하지 않는 남의
            /// 스코프다. 값 목록(IN (1,2,3))이면 서브쿼리가 없으므로 그대로 내려간다.
            /// </summary>
            public override void ExplicitVisit(InPredicate node)
            {
                RecordSetPredicate(node);

                node.Expression?.Accept(this);

                if (node.Subquery == null && node.Values != null)
                {
                    foreach (var value in node.Values)
                    {
                        value.Accept(this);
                    }
                }
            }

            /// <summary>
            /// 리터럴만으로 이뤄진 최상위 IN을 집합 사실로 담는다.
            ///
            /// [담지 않는 셋] 서브쿼리 IN은 옮겨 적을 리터럴 목록이 없다. 원소에
            /// 리터럴 아닌 것이 섞이면 리터럴 집합으로 렌더할 때 명세서에 거짓
            /// 집합이 실린다. 좌변이 단순 컬럼 참조가 아니면(예: 식) 표의 "컬럼"
            /// 칸에 쓸 이름이 없다.
            /// </summary>
            private void RecordSetPredicate(InPredicate node)
            {
                if (node.Subquery != null || node.Values == null || node.Values.Count == 0) return;

                if (node.Expression is not ColumnReferenceExpression columnRef) return;
                var column = columnRef.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value;
                if (string.IsNullOrWhiteSpace(column)) return;

                var literals = new List<string>();
                foreach (var value in node.Values)
                {
                    if (value is not Literal literal) return;   // 하나라도 아니면 통째로 버린다
                    literals.Add(TextOfFragment(literal));
                }

                SetPredicates.Add((column!, node.NotDefined, literals));
            }

            /// <summary>토큰 원문을 그대로 잇는다 - 문자열 리터럴의 따옴표를 보존한다.</summary>
            private static string TextOfFragment(TSqlFragment fragment)
            {
                if (fragment.ScriptTokenStream == null) return string.Empty;

                return string.Concat(
                    fragment.ScriptTokenStream
                        .Skip(fragment.FirstTokenIndex)
                        .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                        .Select(t => t.Text)).Trim();
            }

            /// <summary>
            /// 컬럼 = 컬럼 형태의 최상위 동등비교를, 두 한정자가 서로 다를 때만 조인
            /// 키 후보로 겸해 담는다. base.ExplicitVisit을 그대로 호출해 기존 Columns
            /// 수집(양쪽 컬럼 모두, 한정자 무관)은 손대지 않는다 - 이 오버라이드는
            /// 순수 추가다.
            ///
            /// [리뷰 라운드 2] 한정자를 보지 않고 양쪽 컬럼을 무조건 담았더니
            /// 같은 별칭 안의 비교(A.YMD = A.AYMD - 날짜 제외 필터, EXCEPTION_PROC
            /// 228행 등 실측)와 컬럼명이 우연히 다른 같은 테이블의 두 컬럼 비교
            /// (A.TID = A.CID - "카카오머니만 강제회수" 규칙)까지 조인 키로
            /// 잘못 단언했다. 표는 "기계 확정, 있는 그대로 베낄 것"이라 사람이
            /// 다시 검증하지 않는다 - 빈 칸(놓침)보다 거짓 단언이 더 나쁘다.
            /// </summary>
            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                if (node.ComparisonType == BooleanComparisonType.Equals
                    && node.FirstExpression is ColumnReferenceExpression left
                    && node.SecondExpression is ColumnReferenceExpression right
                    && HaveDifferentQualifiers(left, right))
                {
                    AddJoinKey(left);
                    AddJoinKey(right);
                }

                base.ExplicitVisit(node);
            }

            /// <summary>
            /// 두 컬럼 참조의 한정자가 서로 다른지 본다. 한쪽이라도 한정자가 없으면
            /// (한정자 없는 컬럼, 또는 부(部)가 하나뿐인 참조) 어느 테이블 소속인지
            /// 알 근거가 없으므로 false를 돌려준다 - "놓치는 쪽"이 안전한 기본값이다.
            /// 값·연산자를 보지 않는 것과 같은 원칙으로, 이름 그 자체 말고는
            /// 아무것도 추측하지 않는다.
            /// </summary>
            private static bool HaveDifferentQualifiers(
                ColumnReferenceExpression left, ColumnReferenceExpression right)
            {
                var leftQualifier = QualifierOf(left);
                var rightQualifier = QualifierOf(right);
                if (leftQualifier == null || rightQualifier == null) return false;

                return !string.Equals(leftQualifier, rightQualifier, StringComparison.OrdinalIgnoreCase);
            }

            private static string? QualifierOf(ColumnReferenceExpression reference)
            {
                var parts = reference.MultiPartIdentifier?.Identifiers;
                if (parts == null || parts.Count < 2) return null;
                return parts[parts.Count - 2].Value;
            }

            private void AddJoinKey(ColumnReferenceExpression reference)
            {
                var name = reference.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value;
                if (!string.IsNullOrWhiteSpace(name)
                    && !JoinKeys.Contains(name!, StringComparer.OrdinalIgnoreCase))
                {
                    JoinKeys.Add(name!);
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

        /// <summary>
        /// EXISTS 서브쿼리 자신의 FROM이 선언한 별칭(과 한정자 없이도 쓸 수 있는
        /// 테이블 이름)을 모은다 - 상관 컬럼 판정의 "로컬 이름" 기준이다.
        /// </summary>
        private static HashSet<string> CollectLocalAliases(FromClause? from)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (from?.TableReferences == null) return result;

            foreach (var reference in from.TableReferences)
            {
                CollectLocalAliasesFrom(reference, result);
            }

            return result;
        }

        private static void CollectLocalAliasesFrom(TableReference? reference, HashSet<string> result)
        {
            switch (reference)
            {
                case NamedTableReference named:
                    if (named.Alias != null) result.Add(named.Alias.Value);
                    var baseName = named.SchemaObject?.BaseIdentifier?.Value;
                    if (!string.IsNullOrEmpty(baseName)) result.Add(baseName);
                    break;
                case QualifiedJoin qualifiedJoin:
                    CollectLocalAliasesFrom(qualifiedJoin.FirstTableReference, result);
                    CollectLocalAliasesFrom(qualifiedJoin.SecondTableReference, result);
                    break;
                case UnqualifiedJoin unqualifiedJoin:
                    CollectLocalAliasesFrom(unqualifiedJoin.FirstTableReference, result);
                    CollectLocalAliasesFrom(unqualifiedJoin.SecondTableReference, result);
                    break;
                case QueryDerivedTable derived:
                    if (derived.Alias != null) result.Add(derived.Alias.Value);
                    break;
            }
        }

        /// <summary>
        /// EXISTS 서브쿼리 최상위 WHERE에서, 서브쿼리 자신의 로컬 별칭이 아닌
        /// 한정자를 쓰는 컬럼만 담는다. 한정자가 없는 컬럼은 서브쿼리 자신의
        /// 것으로 본다(안전한 기본값 - 놓치는 방향이지 잘못 담는 방향이 아니다).
        /// 파라미터는 아예 수집하지 않는다 - 이 서브쿼리 안의 파라미터가 대상에
        /// 적용된 것으로 잘못 세지는 것을 원천 차단한다.
        /// </summary>
        private sealed class CorrelatedOuterColumnCollector : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _localAliases;

            public CorrelatedOuterColumnCollector(HashSet<string> localAliases) => _localAliases = localAliases;

            public List<string> Columns { get; } = new();

            public override void ExplicitVisit(ScalarSubquery node) { }
            public override void ExplicitVisit(ExistsPredicate node) { }

            public override void ExplicitVisit(InPredicate node) => node.Expression?.Accept(this);

            public override void Visit(ColumnReferenceExpression node)
            {
                var parts = node.MultiPartIdentifier?.Identifiers;
                if (parts == null || parts.Count < 2) return;

                var qualifier = parts[parts.Count - 2].Value;
                if (_localAliases.Contains(qualifier)) return;

                var name = parts[parts.Count - 1].Value;
                if (!string.IsNullOrWhiteSpace(name)
                    && !Columns.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    Columns.Add(name);
                }
            }
        }

        /// <summary>조인 ON 조건이 쓰는 컬럼(ANSI JOIN). 콤마로 나열한 옛 스타일
        /// 조인(FROM A, B WHERE A.X = B.Y)의 결합 조건은 TopLevelPredicateCollector가
        /// WHERE를 훑을 때 함께 담는다 - ON절이 없어 원본 텍스트 자체가 WHERE에만
        /// 있기 때문이다.</summary>
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
