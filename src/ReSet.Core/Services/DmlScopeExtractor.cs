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
    /// <param name="OrderByColumns">
    /// INSERT ... SELECT 의 최상위 ORDER BY 컬럼. UPDATE·DELETE는 최상위 ORDER BY가
    /// 문법상 불가하므로 항상 빈 목록이고 표에서 "—"로 렌더된다.
    ///
    /// [존재 여부가 아니라 목록인 이유 - 2026-08-21 축 A 감사]
    /// STAT_PGCOLLECT_INS:113의 `ORDER BY INYMD, CLIENTID, PGNAME, MALLID`가 문서
    /// 어디에도 없었다. 불리언으로 담으면 "있다"만 알고 무엇으로 정렬하는지는 여전히
    /// 모른다. 컬럼 목록을 담는 비용이 같으므로 더 충실한 쪽을 택한다.
    ///
    /// [원천이 UNION일 때도 놓치지 않는 이유 - 2026-08-21 구현 중 프로브 실측]
    /// ScriptDom에서 OrderByClause는 QuerySpecification이 아니라 그 공통 기반 클래스인
    /// QueryExpression에 선언돼 있다(리플렉션으로 확인). 그래서 UNION으로 묶인 원천의
    /// 최상위 ORDER BY는 어느 갈래(QuerySpecification)에도 붙지 않고 UNION 노드 자신인
    /// BinaryQueryExpression.OrderByClause에 붙는다 - 갈래마다 OrderByClause는 항상
    /// null이었다(직접 파싱해 확인). InsertSource.Select를 QuerySpecification으로
    /// 좁혀 캐스팅하면 이 ORDER BY를 통째로 놓친다. Select를 QueryExpression 그대로
    /// 두고 OrderByClause에 바로 접근하면 QuerySpecification·BinaryQueryExpression·
    /// QueryParenthesisExpression 세 경우 모두 같은 코드로 잡힌다 - 실측: 실제 코퍼스의
    /// UP_Util_PG_Client_CMRate_Ins INSERT 2(76행)·INSERT 4(159행)는 UNION ALL 원천이지만
    /// 원문에 ORDER BY 자체가 없어(grep 확인) 이 필드는 빈 목록이 맞다.
    /// </param>
    public sealed record DmlScopeFact(
        string Operation,
        int Line,
        string Target,
        IReadOnlyList<string> PredicateColumns,
        bool DateParameterApplied,
        IReadOnlyList<string> JoinKeys,
        IReadOnlyList<string> OrderByColumns);

    /// <param name="Operation">"INSERT", "UPDATE", "DELETE" 중 하나.</param>
    /// <param name="Line">원본 DDL에서 그 문장이 시작하는 줄 번호(1부터).</param>
    /// <param name="Column">
    /// IN 좌변의 원문 표기 그대로 - 한정자가 있으면 한정자를 포함한다(`A.USESTATE`),
    /// 없으면 컬럼 이름만이다(`UseState`). 마지막 식별자 조각만 담지 않는 이유는
    /// 실측 코퍼스에서 키 충돌이 실제로 나기 때문이다:
    /// `output/Objects/dbo.UP_Util_PG_Client_CMRate_Ins.Procedure/raw/object_definition.sql:97-98`가
    /// 같은 INSERT 원천 SELECT의 같은 WHERE 최상위에서
    /// `A.USESTATE IN (0,4,5,6)`과 `B.USESTATE IN (0,4)`를 나란히 쓴다 - 마지막
    /// 조각만 담으면 둘 다 `USESTATE`가 되어 (Operation, Line, Column) 키가
    /// 충돌하고, Task 3의 L1이 그 라인+컬럼으로 행을 찾을 때 하나가 엉뚱한 행에
    /// 매칭된다. 한정자를 포함하면 코퍼스에서 키가 유일해질 뿐 아니라, "어느
    /// 테이블의 USESTATE가 (0,4)로 제한되는가"라는 정보 자체가 이관 결과를
    /// 바꾸므로 원문 그대로 담는 편이 옳다 - 이 재료가 이미 Literals에 적용한
    /// "원문 그대로" 원칙을 좌변에도 일관되게 적용한 것이다.
    /// </param>
    /// <param name="IsNegated">NOT IN이면 true.</param>
    /// <param name="Literals">
    /// 집합의 원소를 원문 그대로 담는다 - 문자열은 따옴표를 포함한다('PLCard').
    /// 파생 테이블 정의 표가 표현식 원문을 그대로 싣는 것과 같은 이유이고, 표에서
    /// 문자열과 숫자를 구분할 수 있게 한다.
    /// </param>
    /// <param name="StatementOrdinal">
    /// 이 사실을 낸 문장의 "연산 종류별 · 1부터" 번호(예: 세 번째 UPDATE면 3).
    /// SetPredicateVisitor가 자신의 Visit(UpdateSpecification/DeleteSpecification/
    /// InsertSpecification) 오버라이드 안에서(Collect 안이 아니라) 연산별 카운터를
    /// 증가시켜 채운다.
    ///
    /// [왜 DML 범위 표의 채번을 조회하지 않고 여기 직접 담는가 - FIX ROUND 3]
    /// 예전엔(FIX ROUND 2) AiService가 이 사실을 (Operation, Line) 키로 DML 범위
    /// 사실 목록에서 찾아 그 문장 번호를 "빌려 썼다". 그런데 같은 물리 줄에 같은
    /// 연산 문장이 둘이고 <b>둘 다</b> 집합 술어를 가지면, 그 키가 여전히 충돌해
    /// 두 번째 문장의 집합 술어 행이 첫 문장의 번호를 빌려 쓰는 회귀가 났다
    /// (2026-08-18 재리뷰 실측 - 표 하나가 "UPDATE 1 dbo.T1 / UPDATE 2 dbo.T2"인데
    /// 옆 표의 dbo.T2 리터럴 행이 "UPDATE 2"가 아니라 "UPDATE 1"로 찍혔다).
    ///
    /// 리뷰가 반박한 예전 주석의 주장("(연산, 라인)만으로는 원천적으로 구분할 수
    /// 없다")은 SetPredicateFact의 <b>모양</b>에 대한 이야기였을 뿐, 두 방문자가
    /// 실제로 훑는 <b>원본 조각과 그 순서</b>는 애초에 그 정보에 기대지 않는다:
    /// SetPredicateVisitor와 DmlScopeVisitor는 같은 파싱 트리를 같은 세 Visit
    /// 오버라이드로, 같은 순서로 방문한다. 두 방문자가 각자 독립적으로(서로를
    /// 참조하지 않고) 연산별 카운터를 문장당 정확히 한 번 증가시키면, 두 카운터는
    /// 항상 같은 값을 낸다 - 사전 조회 없이도 "몇 번째 UPDATE인가"를 소스 구조
    /// 자체가 답한다. 그래서 여기 문장 번호를 직접 담아 사전 조회 자체를 없앤다.
    ///
    /// [Collect가 아니라 Visit에서 세는 이유] INSERT의 Collect는 UNION 갈래마다
    /// (QuerySpecification마다) 여러 번 불릴 수 있는데, DmlScopeVisitor의 INSERT는
    /// 갈래를 합쳐 사실을 하나만 낸다(Visit(InsertSpecification) 안에서 Facts.Add를
    /// 정확히 한 번 호출). Collect 안에서 세면 UNION 갈래 수만큼 카운터가 더 늘어
    /// 이 문장 뒤의 모든 INSERT 번호가 DML 범위 표보다 밀린다 - FIX ROUND 1이
    /// 집합 술어가 없는 문장을 건너뛰어 밀리던 것과 같은 모양의 결함이 INSERT
    /// 카운터에도 생긴다. 그래서 카운터는 반드시 Visit(InsertSpecification) 진입
    /// 시점에, InsertSource의 종류(VALUES/SELECT)와 무관하게 정확히 한 번만 늘린다 -
    /// DmlScopeVisitor가 VALUES 원천의 INSERT에도 사실을 하나 내는 것과 대칭이다.
    /// </param>
    public sealed record SetPredicateFact(
        string Operation,
        int Line,
        string Column,
        bool IsNegated,
        IReadOnlyList<string> Literals,
        int StatementOrdinal = 0,
        string Operator = "IN",
        string Scope = "최상위");

    /// <summary>
    /// "이 문장이 어느 사용자 함수를 부르는가"를 담는다.
    ///
    /// [왜 동작이 아니라 호출 사실만 담는가 - 2026-08-20 축 A 교차 대조]
    /// SP 명세서가 참조 함수의 동작을 산문으로 요약하던 자리에서 10행 중 8행이
    /// 결함이었고 그중 🔴이 5건이었다(필수 술어 USESTATE=0 누락, IIF 분기 누락,
    /// 기본값 0 반환 누락). 함수 DDL 전문은 이미 프롬프트에 들어가고 "분석하라"는
    /// 지시까지 있었는데도 그랬다 - 같은 함수를 SP마다 다르게 썼다.
    /// 그래서 요약을 정확하게 만드는 대신 요약 자체를 없앤다. 함수 동작의 단일
    /// 진실의 원천은 그 함수의 Spec.md이고, SP 명세서는 거기로 링크만 건다.
    /// </summary>
    /// <param name="QualifiedName">호출문에 적힌 그대로의 한정명(예: `dbo.UF_GET_ROUND4VAT`).</param>
    /// <param name="Operation">
    /// 이 호출을 담은 문장의 연산(UPDATE/INSERT/DELETE).
    ///
    /// [독립 SELECT 문의 호출은 담지 않는다] DML 범위 표·집합 술어 표가 세우는 경계와
    /// 같다 - 세 표가 같은 문장 집합을 같은 번호로 가리켜야 나란히 읽을 수 있다.
    /// 변수 대입용 SELECT(`SELECT @v = dbo.UF_X(...)`)의 호출은 이 표에 나오지 않는다.
    /// </param>
    /// <param name="StatementOrdinal">
    /// 연산 종류별 · 1부터인 문장 번호. DML 범위 표·집합 술어 표와 같은 채번이라
    /// 세 표를 나란히 읽을 수 있다(SetPredicateFact.StatementOrdinal 문서 참고).
    /// </param>
    /// <param name="Line">호출식이 있는 원본 줄 번호.</param>
    /// <param name="CallExpression">호출식 원문. 인자를 그대로 보여 준다.</param>
    public sealed record ReferencedFunctionCallFact(
        string QualifiedName,
        string Operation,
        int StatementOrdinal,
        int Line,
        string CallExpression);

    /// <summary>
    /// "이 문장이 어느 자리를 어떤 잠금 힌트로 읽는가"를 담는다.
    ///
    /// [행 단위가 (문장 × 스캔 자리)인 이유 - 2026-08-21 축 A 감사]
    /// 감사가 지적한 것은 "문장별로 힌트가 붙은 곳과 안 붙은 곳이 갈린다"였다.
    /// INS_EXTRA4PLCARD에서 TPGProperty가 P·Y 별칭에는 붙고 PG에는 안 붙는데,
    /// 명세서는 "5개 테이블의 조회 또는 조인에 사용됩니다"로 뭉갰다. 문장당 한 칸으로는
    /// 이 결함을 담을 수 없다.
    /// </summary>
    /// <param name="Alias">별칭이 없으면 "-".</param>
    /// <param name="Scope">
    /// "최상위" 또는 "파생". SetPredicateFact.Scope와 같은 선례다 - 파생 테이블 안의
    /// 참조를 빼지 않고 표시해서 싣는다(수정 라운드 2, 아래 ExtractLockHints 문서의
    /// 실측 근거 참고).
    /// </param>
    /// <param name="Hints">힌트가 없으면 빈 목록. 한 참조에 여럿 붙을 수 있다.</param>
    public sealed record LockHintFact(
        string Operation,
        int StatementOrdinal,
        int Line,
        string Table,
        string Alias,
        string Scope,
        IReadOnlyList<string> Hints);

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
        public const string ReferencedFunctionTableHeading = "### 참조 함수 (기계 확정 — 수정 금지)";
        public const string LockHintTableHeading = "### 잠금 힌트 (기계 확정 — 수정 금지)";

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
        ///
        /// [주의 - (Operation, Line, Column) 키가 유일하다고 가정하지 마라] 같은
        /// 한정 컬럼이 같은 문장에 두 번 IN으로 걸리면(예: `A.X IN (1) AND A.X IN
        /// (2)`) 키가 여전히 충돌한다. 이 추출기는 그 경우를 합치거나 걸러내지
        /// 않는다 - 합치면 AND/OR 의미를 날조하게 된다. 소비자(L1 검사 등)는 같은
        /// 키의 사실이 둘 이상일 수 있다고 보고 다뤄야 한다.
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

        /// <summary>
        /// DDL에서 사용자 정의 함수 호출을 문장 번호와 함께 뽑는다.
        /// </summary>
        /// <param name="knownFunctionNames">
        /// 한정자 없는 함수 이름 집합. SpDefinition.Dependencies의 FUNCTION 타입에서
        /// 온다 - StaticAnalysis.ReferencedFunctions를 쓰지 않는 이유는 그쪽이 인라인
        /// TVF를 싣지 못하기 때문이다(2026-08-20 실측: EXPECT_PROC·INS_EXTRA 모두
        /// UIF_SettleYMD가 Dependencies에만 있었다). 이 집합에 없는 이름은 내장
        /// 함수(ISNULL·ROUND·CAST)로 보고 건너뛴다.
        /// </param>
        public static IReadOnlyList<ReferencedFunctionCallFact> ExtractFunctionCalls(
            string? ddlText,
            IReadOnlyCollection<string> knownFunctionNames)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<ReferencedFunctionCallFact>();
            if (knownFunctionNames == null || knownFunctionNames.Count == 0)
                return Array.Empty<ReferencedFunctionCallFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null) return Array.Empty<ReferencedFunctionCallFact>();

                var visitor = new ReferencedFunctionVisitor(knownFunctionNames);
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[DmlScopeExtractor] 참조 함수 호출 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<ReferencedFunctionCallFact>();
            }
        }

        /// <summary>
        /// DML 문장이 읽는 자리와 그 잠금 힌트를 뽑는다.
        ///
        /// [행이 되는 자리가 셋인 이유 - 2026-08-21 프로브 실측]
        /// 처음에는 "대상 노드를 싣지 않는다"로 정했다가 규칙이 사실을 잃는 것을 봤다.
        ///   DELETE T FROM dbo.T A WITH(NOLOCK)  대상 (없음) · FROM NoLock  ← 대상은 껍데기
        ///   DELETE FROM dbo.T WITH(NOLOCK)      대상 NoLock · FROM 없음    ← 대상이 곧 스캔
        /// FROM이 있으면 대상 노드는 갱신 대상 지시자일 뿐 스캔이 아니고 힌트를 지지 않는다.
        /// 그대로 실으면 같은 테이블이 "힌트 있음/없음" 두 행으로 나와 독자를 오도한다.
        ///
        /// [대상 노드를 싣는 조건이 "힌트가 있을 때"인 이유 - 2026-08-21 테스트 실측]
        /// "FROM이 없으면 무조건 대상이 스캔"으로 조건을 잡으면 FROM도 없고 힌트도
        /// 없는 문장(UPDATE dbo.T SET C=1 WHERE X=1)까지 빈 힌트 행을 낸다.
        /// ExtractLockHints_StatementWithNoScan_ProducesNoRow가 이를 실측으로 잡았다 -
        /// "대상 자체가 스캔이다"와 "그 스캔에 보고할 힌트가 있다"는 다른 질문이고,
        /// 이 표는 후자만 싣는다. FROM 유무와 무관하게 대상이 힌트를 질 때만 싣는다.
        ///
        /// [파생 테이블 안으로도 내려가는 이유 - 수정 라운드 2, 조정자 판정]
        /// 초안은 "파생 테이블 안으로 내려가지 않는다"였다. 그 규칙을
        /// SqlStaticParser.FindAliasForTarget에서 베껴 왔는데, 거기서는 옳았다 - 별칭
        /// 해석은 이름의 스코프 문제라 안쪽 별칭이 바깥 대상과 무관하다. 잠금 힌트에는
        /// 그 논리가 서지 않는다 - 파생 테이블의 FROM은 같은 문장이 실제로 하는 스캔이고
        /// 그 힌트가 곧 그 문장의 잠금 동작이다. 리뷰어가 실물로 보였다: UP_UTIL_SETTLE_INS의
        /// INSERT(55행)는 최상위 FROM 항목이 파생 테이블 하나뿐이라 초안 규칙 아래에서
        /// 행이 0개가 되고, PaymentDB.dbo.TTxMst WITH(NOLOCK, INDEX=CIDX_TTxMst_YMD)를
        /// 포함한 네 테이블의 힌트가 통째로 사라졌다 - 스캔이 정말 없는 문장과 구별되지
        /// 않는, 이 표가 막으려는 바로 그 실패 모양이다. 「집합 술어」 표의
        /// SetPredicateFact.Scope 선례를 따라 빼지 않고 LockHintFact.Scope로 "최상위"/
        /// "파생"을 표시해서 싣는다.
        ///
        /// [INSERT 원천이 UNION이면 갈래마다 훑는 이유 - 수정 라운드 2, 리뷰 실측]
        /// 원천이 BinaryQueryExpression일 수 있는데 QuerySpecification으로 좁히면 통째로
        /// 빠진다. UP_Util_PG_Client_CMRate_Ins의 INSERT 2(76행)·INSERT 4(159행)가 모든
        /// 테이블에 NOLOCK을 지고 있는데도 행이 0개였다. QuerySpecificationsOf(이 파일의
        /// DmlScopeVisitor·SetPredicateVisitor가 이미 쓰는 헬퍼)를 재사용한다 - 새로
        /// 만들지 않는다.
        ///
        /// [중복 제거 키에 Line이 필요한 이유 - 수정 라운드 2, 리뷰 실측]
        /// Line이 참조별로 갈리므로(수정 라운드 1) 대상 노드와 FROM 참조가 같은
        /// (Operation, StatementOrdinal, Table, Alias)로 정규화되면(둘 다 별칭 없음
        /// -> "-") Line을 빼고 판정하던 예전 키가 뒤에 추가되는 쪽을 같은 행으로 오인해
        /// 조용히 버렸다 - `UPDATE dbo.T WITH(NOLOCK) ... FROM dbo.T`에서 대상의 NOLOCK이
        /// 사라졌다. 두 참조는 원문에서 서로 다른 줄에 있는 별개의 스캔 자리이므로 Line을
        /// 키에 포함해 둘 다 지킨다.
        /// </summary>
        public static IReadOnlyList<LockHintFact> ExtractLockHints(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<LockHintFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<LockHintFact>();
                }

                var visitor = new LockHintVisitor();
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[DmlScopeExtractor] 잠금 힌트 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<LockHintFact>();
            }
        }

        private sealed class LockHintVisitor : TSqlFragmentVisitor
        {
            /// <summary>최상위 FROM에 직접 실린 참조. SetPredicateFact.Scope와 같은 문자열.</summary>
            private const string TopLevelScope = "최상위";

            /// <summary>파생 테이블 안의 참조.</summary>
            private const string DerivedScope = "파생";

            public List<LockHintFact> Facts { get; } = new();

            private readonly Dictionary<string, int> _ordinals = new(StringComparer.Ordinal);

            public override void Visit(InsertSpecification node)
            {
                var ordinal = NextOrdinal("INSERT");

                // 원천이 UNION(BinaryQueryExpression)이면 갈래마다 FROM이 다르므로
                // QuerySpecificationsOf(DmlScopeVisitor·SetPredicateVisitor가 이미 쓰는
                // 헬퍼)로 전부 훑는다. VALUES 원천이면 QuerySpecificationsOf가 빈 시퀀스를
                // 내므로 아무 것도 더해지지 않는다.
                if (node.InsertSource is SelectInsertSource select)
                {
                    foreach (var spec in QuerySpecificationsOf(select.Select))
                    {
                        CollectFrom("INSERT", ordinal, spec.FromClause);
                    }
                }

                RecordTargetHint("INSERT", ordinal, node.Target);
            }

            public override void Visit(UpdateSpecification node)
            {
                var ordinal = NextOrdinal("UPDATE");
                CollectFrom("UPDATE", ordinal, node.FromClause);
                RecordTargetHint("UPDATE", ordinal, node.Target);
            }

            public override void Visit(DeleteSpecification node)
            {
                var ordinal = NextOrdinal("DELETE");
                CollectFrom("DELETE", ordinal, node.FromClause);
                RecordTargetHint("DELETE", ordinal, node.Target);
            }

            private int NextOrdinal(string operation)
            {
                _ordinals.TryGetValue(operation, out var n);
                _ordinals[operation] = ++n;
                return n;
            }

            private void CollectFrom(string operation, int ordinal, FromClause? from)
            {
                if (from == null) return;

                var collector = new FromTableCollector();
                foreach (var reference in from.TableReferences) reference.Accept(collector);
                foreach (var (table, scope) in collector.Tables) Add(operation, ordinal, table, scope);
            }

            // 대상 노드는 힌트를 질 때만 싣는다(INSERT INTO T WITH(TABLOCK),
            // DELETE FROM dbo.T WITH(NOLOCK)). FROM이 없다고 무조건 실으면
            // "FROM도 없고 힌트도 없는" 문장(UPDATE dbo.T SET C=1 WHERE X=1)까지
            // 빈 힌트 행을 내 "스캔할 자리가 없다"는 사실을 잃는다 - 대상 자체가
            // 곧 스캔이라는 것과, 그 스캔에 대해 보고할 힌트가 있다는 것은 다른
            // 질문이다(2026-08-21 테스트 실측 - 브리프 초안의 from==null 단독
            // 조건은 ExtractLockHints_StatementWithNoScan_ProducesNoRow에서 실패했다).
            // 대상 노드는 파생 테이블 안에 있을 수 없으므로 항상 최상위다.
            private void RecordTargetHint(string operation, int ordinal, TableReference target)
            {
                if (target is NamedTableReference named && named.TableHints.Count > 0)
                {
                    Add(operation, ordinal, named, TopLevelScope);
                }
            }

            /// <summary>
            /// Line은 문장 시작 줄이 아니라 참조 노드 자신의 줄이다.
            ///
            /// [왜 참조별 줄인가 - 수정 라운드 1 실물 검증 실측]
            /// 잠금 힌트는 문장 하나에서 행이 여럿 난다(FromTableCollector가 FROM의
            /// 테이블마다 하나씩 낸다). 문장 줄을 쓰면 그 여럿이 전부 같은 줄로 찍혀
            /// "문장" 칸이 이미 주는 정보를 되풀이할 뿐이고, 독자가 어느 스캔을 가리키는지
            /// 원문에서 찾을 수 없다. INS_EXTRA4PLCARD의 INSERT 1은 52~174행에 걸쳐 있는데
            /// 문장 줄을 쓰면 그 안의 참조가 전부 "52"로 찍혔다.
            ///
            /// ReferencedFunctionCallFact(CallCollector.Record, 이 파일 아래쪽)가 이미
            /// 같은 이유로 호출 노드 자신의 줄을 쓴다 - 그 표는 인접 호출을 줄 번호로
            /// 구분해 감사에서 실적이 있다. DmlScopeFact·SetPredicateFact가 문장 줄을
            /// 쓰는 것은 문장당 행이 하나뿐이라 되풀이 문제가 없기 때문이고, 잠금 힌트는
            /// 그 전제가 깨지므로 같은 규칙을 따를 수 없다.
            ///
            /// [중복 제거 키에 Line이 필요한 이유 - 수정 라운드 2 리뷰 실측]
            /// Line이 참조별로 갈리는데(위 문단) 대상 노드와 FROM 참조가 같은
            /// (Operation, StatementOrdinal, Table, Alias)로 정규화되면 Line을 뺀 키가
            /// 뒤에 추가되는 쪽을 같은 행으로 오인해 조용히 버렸다 -
            /// `UPDATE dbo.T WITH(NOLOCK) ... FROM dbo.T`에서 대상의 NOLOCK이 사라졌다.
            /// 두 참조는 원문에서 서로 다른 줄에 있는 별개의 스캔 자리이므로 Line을
            /// 포함해야 판정이 옳다.
            /// </summary>
            private void Add(string operation, int ordinal, NamedTableReference node, string scope)
            {
                var table = string.Join(
                    ".", node.SchemaObject.Identifiers.Select(i => i.Value));
                var alias = string.IsNullOrEmpty(node.Alias?.Value) ? "-" : node.Alias!.Value;
                var hints = node.TableHints.Select(RenderHint).ToList();
                var line = node.StartLine;

                if (Facts.Any(f =>
                        f.Operation == operation && f.StatementOrdinal == ordinal &&
                        f.Table == table && f.Alias == alias && f.Line == line))
                {
                    return;
                }

                Facts.Add(new LockHintFact(operation, ordinal, line, table, alias, scope, hints));
            }

            /// <summary>
            /// 힌트 하나를 표에 실을 문자열로 낸다.
            ///
            /// [값을 지는 힌트는 원문 토큰 그대로 낸다 - 수정 라운드 3, 조정자 판정]
            /// HintKind 이름만 내면(예: "INDEX") 어느 인덱스인지가 사라진다. 실물:
            /// UP_UTIL_SETTLE_INS 146행 `PaymentDB.dbo.TTxMst A WITH(NOLOCK,
            /// INDEX=CIDX_TTxMst_YMD)` - 이관 시 질의 계획이 달라지는 사실인데 "INDEX"라고만
            /// 적으면 원본에서 찾을 수 없다. 작업 3(객체 선언 추출기)이 EXECUTEAS·INLINE에서
            /// 같은 부류의 결함을 겪었다 - "주체와 상태가 통째로 사라져 원문에서 찾을 수
            /// 없는, 없는 것보다 나쁜 그럴듯한 오답"이 됐다.
            ///
            /// 값을 직접 IndexValues·ColumnValues 같은 프로퍼티에서 다시 조립하지 않고
            /// 힌트 노드 자신의 원문 토큰(TextOf)을 쓰는 이유: `INDEX=CIDX_x`와
            /// `INDEX(CIDX_x)`는 둘 다 유효 문법이고 ScriptDom은 어느 표기였는지를 별도
            /// 타입으로 구분하지 않는다(둘 다 IndexTableHint, IndexValues만 다르다) - 프로브로
            /// 확인했다(2026-08-21, 수정 라운드 3). 값에서 다시 조립하면 어느 표기를 쓸지
            /// 우리가 지어내야 하고 원문과 다를 위험이 있다. 원문 토큰을 그대로 쓰면 원문의
            /// 구두점(`=` vs `()`)까지 보존되어 항상 원본에서 축자로 찾을 수 있다 - 같은
            /// 프로브로 실측: `WITH(FORCESEEK(IX_a(col)))`도 `FORCESEEK(IX_a(col))`로,
            /// `WITH(SPATIAL_WINDOW_MAX_CELLS=8)`도 `SPATIAL_WINDOW_MAX_CELLS=8`로 그대로
            /// 나온다.
            ///
            /// [값이 없는 힌트는 손대지 않는 이유] NOLOCK 등은 HintKind 이름 자체가 이미
            /// 원문과 축자로 같다(대소문자만 다를 뿐 - 이 코퍼스는 전부 대문자로 쓴다).
            /// 수정 라운드 3의 지시 범위가 값 있는 힌트로 한정돼 있고, 렌더를 통째로 원문
            /// 토큰 기반으로 바꾸면 검증하지 않은 대소문자 차이가 새로 생길 위험이 있어
            /// 건드리지 않는다.
            ///
            /// [알려진 값 있는 힌트 셋 - ScriptDom 180.37.3 전수 확인]
            /// TableHint의 서브타입은 IndexTableHint·ForceSeekTableHint·LiteralTableHint
            /// 셋뿐이다(리플렉션으로 어셈블리 전체를 훑어 확인, 2026-08-21 수정 라운드 3).
            /// 값 없는 힌트는 서브타입이 없는 TableHint 그 자체다.
            ///
            /// [미래 서브타입에 대한 안전망] 이 분기(`GetType() != typeof(TableHint)`)는
            /// 오늘 존재하는 셋을 원문 토큰으로 정확히 처리하지만, ScriptDom이 나중에
            /// 새 값 있는 힌트 타입을 추가하면 자동으로 이 분기를 타 원문 토큰으로 렌더된다 -
            /// TextOf가 노드 종류를 가리지 않고 원문을 그대로 잇기 때문에 대체로 안전하다.
            /// 그래도 프로브 없이 넘겨짚지 말 것: 힌트를 새로 다루게 되면(테스트가 이
            /// 분기에 처음 걸리면) 위 리플렉션 프로브를 다시 돌려 그 타입의 프로퍼티
            /// 구조를 확인하고, 원문 토큰이 여전히 원하는 표기와 일치하는지 실물 DDL로
            /// 검증한 뒤 이 문서에 사례를 추가하라.
            /// </summary>
            private static string RenderHint(TableHint hint) =>
                hint.GetType() == typeof(TableHint)
                    ? hint.HintKind.ToString().ToUpperInvariant()
                    : CollapseWhitespace(TextOf(hint));

            /// <summary>
            /// FROM 절의 명명 테이블 참조를 모은다. 파생 테이블 안으로도 내려가되, 그
            /// 안에서 모은 참조는 Scope="파생"으로 구분한다(수정 라운드 2 - 조정자 판정,
            /// ExtractLockHints 문서의 실측 근거 참고). ScriptDom은 Visit을 비워도 자식으로
            /// 계속 내려가므로, 파생 테이블 진입/이탈을 표시하려면 ExplicitVisit을 오버라이드해
            /// base.ExplicitVisit으로 자식 순회를 이어가야 한다.
            /// </summary>
            private sealed class FromTableCollector : TSqlFragmentVisitor
            {
                public List<(NamedTableReference Node, string Scope)> Tables { get; } = new();

                private bool _inDerivedTable;

                public override void Visit(NamedTableReference node) =>
                    Tables.Add((node, _inDerivedTable ? DerivedScope : TopLevelScope));

                public override void ExplicitVisit(QueryDerivedTable node)
                {
                    var wasInDerivedTable = _inDerivedTable;
                    _inDerivedTable = true;
                    base.ExplicitVisit(node);
                    _inDerivedTable = wasInDerivedTable;
                }
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
                    predicateColumns, dateApplied, joinKeys, OrderByColumnsOf(node.InsertSource)));
            }

            /// <summary>
            /// INSERT 원천의 최상위 ORDER BY 컬럼을 뽑는다.
            ///
            /// [Select를 QuerySpecification으로 좁히지 않는 이유] OrderByClause는
            /// QuerySpecification이 아니라 그 공통 기반 QueryExpression에 선언돼 있다
            /// (2026-08-21 프로브 실측, 이 파일 DmlScopeFact.OrderByColumns 문서 참고).
            /// UNION 원천의 최상위 ORDER BY는 BinaryQueryExpression 자신에 붙고 갈래
            /// QuerySpecification에는 붙지 않으므로, Select를 QueryExpression 그대로 두고
            /// OrderByClause에 바로 접근해야 QuerySpecification·BinaryQueryExpression·
            /// QueryParenthesisExpression 세 경우가 한 코드로 잡힌다.
            /// </summary>
            private static IReadOnlyList<string> OrderByColumnsOf(InsertSource? source)
            {
                var orderBy = (source as SelectInsertSource)?.Select?.OrderByClause;
                if (orderBy == null) return Array.Empty<string>();

                return orderBy.OrderByElements
                    .Select(e => TextOf(e.Expression))
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();
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
                    joinKeys,
                    // UPDATE·DELETE는 최상위 ORDER BY가 문법상 불가하다 - 항상 빈 목록.
                    Array.Empty<string>()));
            }
        }

        /// <summary>
        /// 토큰 원문을 그대로 잇는다 - 파서가 정규화하지 않은 소스 그대로다(문자열
        /// 리터럴의 따옴표, 컬럼 참조의 한정자가 그대로 보존된다). DmlScopeVisitor의
        /// Target 표기와 TopLevelPredicateCollector의 집합 술어 좌변·리터럴이 모두
        /// 이 메서드 하나를 쓴다 - "원문 그대로 담는다"는 원칙이 재료마다 따로
        /// 구현되며 갈라지지 않도록.
        /// </summary>
        private static string TextOf(TSqlFragment? fragment)
        {
            if (fragment == null || fragment.ScriptTokenStream == null) return string.Empty;

            return string.Concat(
                fragment.ScriptTokenStream
                    .Skip(fragment.FirstTokenIndex)
                    .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                    .Select(t => t.Text)).Trim();
        }

        /// <summary>
        /// 여러 줄로 쓰인 원문 조각을 한 줄로 접는다. TopLevelPredicateCollector의 집합
        /// 술어 좌변(2026-08-20 리뷰 Important)과 LockHintVisitor의 값 있는 힌트
        /// 렌더(수정 라운드 3)가 함께 쓴다 - 프롬프트 쪽은 EscapeTableCell이 개행을
        /// 공백으로 접어 싣는데 검증기는 접지 않은 원문과 대조하므로, 재료를 만들 때
        /// 한 번 접어 두지 않으면 개행이 있는 값은 어떤 산출물도 만족시킬 수 없는
        /// 요구가 된다. 접히는 것은 공백뿐이라 의미는 그대로다.
        /// </summary>
        private static string CollapseWhitespace(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

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
        /// 문장 문맥(연산 종류·시작 줄·문장 번호)을 붙인다.
        ///
        /// [문장 번호를 이 방문자가 직접 매기는 이유] SetPredicateFact.StatementOrdinal
        /// 문서 참고 - DmlScopeVisitor와 같은 파싱 트리를 같은 세 Visit 오버라이드로
        /// 같은 순서로 방문하므로, 이 방문자가 독자적으로 세어도 DML 범위 표의
        /// 번호와 항상 일치한다. 카운터는 반드시 각 Visit 오버라이드 안에서(Collect
        /// 안이 아니라) 문장당 정확히 한 번 늘려야 한다 - NextOrdinal 문서 참고.
        /// </summary>
        private sealed class SetPredicateVisitor : TSqlFragmentVisitor
        {
            private readonly Dictionary<string, int> _perOperation =
                new(StringComparer.OrdinalIgnoreCase);

            public List<SetPredicateFact> Facts { get; } = new();

            public override void Visit(UpdateSpecification node) =>
                Collect("UPDATE", node, node.WhereClause, NextOrdinal("UPDATE"));

            public override void Visit(DeleteSpecification node) =>
                Collect("DELETE", node, node.WhereClause, NextOrdinal("DELETE"));

            public override void Visit(InsertSpecification node)
            {
                // DmlScopeVisitor는 원천이 VALUES든 SELECT든 InsertSpecification마다
                // 사실을 정확히 하나 낸다(Visit 안에서 Facts.Add를 한 번만 호출) -
                // 그래서 이 카운터도 원천 종류와 무관하게 여기서 먼저, 한 번만
                // 늘려야 두 방문자의 번호가 계속 맞는다. Collect(→UNION 갈래마다,
                // 즉 QuerySpecification마다 호출됨) 안에서 늘리면 갈래 수만큼
                // 카운터가 밀린다(SetPredicateFact.StatementOrdinal 문서 참고).
                var ordinal = NextOrdinal("INSERT");

                // INSERT ... SELECT의 대상 범위는 원천 SELECT의 최상위 WHERE가 정한다
                // (DmlScopeExtractor.Visit(InsertSpecification)와 같은 판단). UNION으로
                // 묶인 원천은 갈래마다 WHERE가 다르므로 전부 훑되, 문장 번호는 위에서
                // 미리 정한 하나를 공유한다 - DmlScopeVisitor가 갈래를 합쳐 사실
                // 하나만 내는 것과 대칭이다.
                if (node.InsertSource is not SelectInsertSource select) return;

                foreach (var spec in QuerySpecificationsOf(select.Select))
                {
                    Collect("INSERT", node, spec.WhereClause, ordinal);
                }
            }

            /// <summary>
            /// 연산 종류별 문장 번호를 1부터 매긴다. SetPredicateFact.StatementOrdinal
            /// 문서의 실측 근거 참고 - DmlScopeVisitor가 문장 하나당 사실을 정확히
            /// 하나만 내는 지점(각 Visit 오버라이드)과 카운터 증가 지점을 맞춰야,
            /// 두 방문자가 독립적으로 세어도 항상 같은 번호가 나온다.
            /// </summary>
            private int NextOrdinal(string operation)
            {
                _perOperation.TryGetValue(operation, out var n);
                _perOperation[operation] = ++n;
                return n;
            }

            private void Collect(string operation, TSqlFragment statement, WhereClause? where, int ordinal)
            {
                CollectFrom(operation, statement, where?.SearchCondition, ordinal, TopLevelScope);

                // 파생 테이블 안의 필터도 대상 행 집합을 좁힌다 - 2026-08-19 축 A 감사의
                // 🟠 4건 중 둘(COMM_UPD:243, EXCEPTION_PROC:375)이 이 자리였다. 최상위
                // WHERE만 훑으면 그 술어는 사실이 하나도 나오지 않아 L1이 침묵한다.
                var derived = new DerivedTableCollector();
                statement.Accept(derived);

                foreach (var (alias, searchCondition) in derived.Tables)
                {
                    CollectFrom(operation, statement, searchCondition, ordinal, $"파생 테이블 {alias}");
                }
            }

            /// <summary>최상위 WHERE에서 나온 사실의 범위 표기.</summary>
            private const string TopLevelScope = "최상위";

            /// <summary>
            /// 문장 안의 파생 테이블(`FROM (SELECT ...) X`)을 찾아 별칭과 그 WHERE를 낸다.
            ///
            /// 별칭이 없는 파생 테이블은 명세서 표에서 가리킬 이름이 없으므로 건너뛴다.
            /// 스칼라 서브쿼리는 대상 범위를 정하지 않으므로 여기서도 다루지 않는다 -
            /// <see cref="TopLevelPredicateCollector"/>가 세운 것과 같은 경계다.
            /// </summary>
            private sealed class DerivedTableCollector : TSqlFragmentVisitor
            {
                public List<(string Alias, BooleanExpression? Where)> Tables { get; } = new();

                public override void ExplicitVisit(ScalarSubquery node) { }

                public override void ExplicitVisit(QueryDerivedTable node)
                {
                    var alias = node.Alias?.Value;
                    if (!string.IsNullOrWhiteSpace(alias))
                    {
                        foreach (var spec in QuerySpecificationsOf(node.QueryExpression))
                        {
                            Tables.Add((alias!, spec.WhereClause?.SearchCondition));
                        }
                    }

                    base.ExplicitVisit(node);
                }
            }

            private void CollectFrom(
                string operation, TSqlFragment statement, BooleanExpression? searchCondition,
                int ordinal, string scope)
            {
                if (searchCondition == null) return;

                var top = new TopLevelPredicateCollector();
                searchCondition.Accept(top);

                foreach (var (column, op, literals) in top.SetPredicates)
                {
                    Facts.Add(new SetPredicateFact(
                        operation, statement.StartLine, column,
                        op == "NOT IN", literals, ordinal, op, scope));
                }
            }
        }

        /// <summary>
        /// 문장마다 연산별 번호를 매기고 그 안의 사용자 함수 호출을 모은다.
        /// 번호를 매기는 규칙은 SetPredicateVisitor와 같다 - 두 방문자가 같은 파싱
        /// 트리를 같은 순서로 훑고 문장당 정확히 한 번 카운터를 늘리므로, 서로를
        /// 참조하지 않고도 항상 같은 번호가 나온다.
        /// </summary>
        private sealed class ReferencedFunctionVisitor : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _known;
            private readonly Dictionary<string, int> _perOperation =
                new(StringComparer.OrdinalIgnoreCase);

            public ReferencedFunctionVisitor(IReadOnlyCollection<string> knownFunctionNames) =>
                _known = new HashSet<string>(knownFunctionNames, StringComparer.OrdinalIgnoreCase);

            public List<ReferencedFunctionCallFact> Facts { get; } = new();

            public override void Visit(UpdateSpecification node) =>
                Collect("UPDATE", node, NextOrdinal("UPDATE"));

            public override void Visit(DeleteSpecification node) =>
                Collect("DELETE", node, NextOrdinal("DELETE"));

            public override void Visit(InsertSpecification node) =>
                Collect("INSERT", node, NextOrdinal("INSERT"));

            private int NextOrdinal(string operation)
            {
                _perOperation.TryGetValue(operation, out var n);
                _perOperation[operation] = ++n;
                return n;
            }

            private void Collect(string operation, TSqlFragment statement, int ordinal)
            {
                var calls = new CallCollector(_known);
                statement.Accept(calls);

                foreach (var (qualified, line, text) in calls.Calls)
                {
                    Facts.Add(new ReferencedFunctionCallFact(qualified, operation, ordinal, line, text));
                }
            }

            /// <summary>
            /// 문장 안의 모든 함수 호출을 훑는다. 중첩 호출은 바깥과 안쪽이 모두
            /// 나와야 "이 문장이 무엇을 부르는가"가 빠짐없이 전달되므로, 자식으로
            /// 계속 내려간다(base.ExplicitVisit 호출).
            /// </summary>
            private sealed class CallCollector : TSqlFragmentVisitor
            {
                private readonly HashSet<string> _known;

                public CallCollector(HashSet<string> known) => _known = known;

                public List<(string Qualified, int Line, string Text)> Calls { get; } = new();

                public override void ExplicitVisit(FunctionCall node)
                {
                    Record(node.FunctionName?.Value, node);
                    base.ExplicitVisit(node);
                }

                // 인라인 TVF는 FROM 절의 SchemaObjectFunctionTableReference로 나온다.
                public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
                {
                    Record(node.SchemaObject?.BaseIdentifier?.Value, node, node.SchemaObject);
                    base.ExplicitVisit(node);
                }

                private void Record(string? bareName, TSqlFragment node, SchemaObjectName? schemaObject = null)
                {
                    if (string.IsNullOrWhiteSpace(bareName) || !_known.Contains(bareName)) return;

                    Calls.Add((Qualify(bareName, schemaObject), node.StartLine, TextOf(node)));
                }

                /// <summary>스칼라 함수는 호출식 원문에서, TVF는 SchemaObjectName에서 한정자를 얻는다.</summary>
                private static string Qualify(string bareName, SchemaObjectName? schemaObject)
                {
                    var schema = schemaObject?.SchemaIdentifier?.Value;
                    var database = schemaObject?.DatabaseIdentifier?.Value;

                    if (!string.IsNullOrWhiteSpace(database) && !string.IsNullOrWhiteSpace(schema))
                        return $"{database}.{schema}.{bareName}";
                    if (!string.IsNullOrWhiteSpace(schema))
                        return $"{schema}.{bareName}";
                    return bareName;
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
            public List<(string Column, string Operator, List<string> Literals)> SetPredicates { get; } = new();

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
            ///
            /// [Subquery != null 검사가 남아 있는 이유] T-SQL 문법상 `IN (서브쿼리)`와
            /// `IN (값 목록)`은 서로 다른 생산 규칙이라 Subquery와 Values가 동시에
            /// 채워지는 파싱 결과는 나오지 않는다 - 그래서 `node.Values == null`
            /// 검사 하나만으로도 서브쿼리 IN은 이미 걸러진다. 그런데도 이 검사를
            /// 명시적으로 남기는 이유는, 그 사실이 ScriptDom의 내부 불변식이지 이
            /// 메서드의 계약이 아니기 때문이다 - 파서 버전이 바뀌거나 이 메서드가
            /// 텍스트 파싱이 아닌 경로(직접 조립한 AST 등)로 호출되는 날, "서브쿼리
            /// IN은 담지 않는다"는 §3.2의 결정이 Values 상태에 우연히 얹혀 있지
            /// 않고 코드에 그대로 선언돼 있어야 한다. 이 불변식이 SQL 텍스트로는
            /// 깨지지 않으므로, DmlScopeExtractorTests의 SubqueryIn 테스트는 이
            /// 한 줄만 떼어 실패시키지 못한다 - 그 테스트 옆 주석에 같은 설명을
            /// 남겨 뒀다.
            /// </summary>
            private void RecordSetPredicate(InPredicate node)
            {
                if (node.Subquery != null || node.Values == null || node.Values.Count == 0) return;

                // Column은 마지막 식별자 조각이 아니라 원문 표기 그대로 담는다(레코드
                // 문서의 실측 근거 참고) - 한정자가 있으면 A.USESTATE처럼 한정자까지
                // 포함해야 같은 문장 안의 A.USESTATE와 B.USESTATE가 서로 다른 키가 된다.
                var column = LeftSideText(node.Expression);
                if (column == null) return;

                var literals = new List<string>();
                foreach (var value in node.Values)
                {
                    if (value is not Literal literal) return;   // 하나라도 아니면 통째로 버린다
                    literals.Add(TextOf(literal));
                }

                SetPredicates.Add((column, node.NotDefined ? "NOT IN" : "IN", literals));
            }

            /// <summary>
            /// 술어 좌변의 표기를 낸다. 순수 컬럼 참조면 그 원문, `ISNULL(A.X,'')`처럼
            /// 컬럼을 감싼 호출이면 그 호출 원문을 그대로 낸다. 어느 쪽도 아니면 null.
            ///
            /// [왜 래핑을 통째로 버리지 않는가 - 2026-08-19 축 A 감사]
            /// 예전에는 좌변이 <c>ColumnReferenceExpression</c>이 아니면 사실을 버렸다.
            /// 그래서 `ISNULL(A.MobileCo,'') IN ('1'..'6')`(EXCEPTION_PROC:423)이 수집되지
            /// 않았고, MobileCo가 NULL·기타값인 건까지 갱신 대상이 되는데도 명세서에
            /// 리터럴 집합이 실리지 않았다. 원본 코퍼스에 이 형태가 13건 있다.
            ///
            /// 표기를 컬럼 이름으로 축약하지 않고 호출 원문 그대로 담는 이유는
            /// <see cref="SetPredicateFact.Column"/>이 이미 세운 "원문 그대로" 원칙과 같다 -
            /// 명세서가 래핑까지 옮겨야 NULL 처리 의미가 보존된다.
            /// </summary>
            private static string? LeftSideText(ScalarExpression? expression)
            {
                switch (expression)
                {
                    case ColumnReferenceExpression columnRef:
                        var column = TextOf(columnRef);
                        return string.IsNullOrWhiteSpace(column) ? null : column;

                    // [노드 타입을 열거하지 않는 이유 - 2026-08-20 축 A 감사]
                    // 예전에는 FunctionCall만 받았다. 그런데 ScriptDom은 LEFT·RIGHT를
                    // FunctionCall이 아니라 전용 노드(LeftFunctionCall·RightFunctionCall)로
                    // 판다. 그래서 같은 SP에서 ISNULL 래핑은 잡히는데
                    // `LEFT(D.PayToolType,1) IN ('C')`(EXPECT_PROC:146·168)만 통째로 빠졌고,
                    // 통신군과 금융·상품권군을 가르는 필터가 "수정 금지" 표에서 사라졌다.
                    //
                    // 타입을 하나 더 열거하면 오늘 LEFT는 닫히지만 CAST·CONVERT·COALESCE·IIF
                    // 처럼 전용 노드를 갖는 다음 것이 같은 구멍에 빠진다. 좌변이 무엇이든
                    // "컬럼 참조를 품고 있는가"만 보면 부류가 닫힌다. 상수만으로 이뤄진
                    // 식은 술어의 좌변이 아니므로 그대로 버린다.
                    case ScalarExpression other when ContainsColumn(other):
                        var text = CollapseWhitespace(TextOf(other));
                        return string.IsNullOrWhiteSpace(text) ? null : text;

                    default:
                        return null;
                }
            }

            /// <summary>
            /// 식 어딘가에 컬럼 참조가 있는가. 하위 질의 안으로는 내려가지 않는다 -
            /// 그 스코프의 컬럼은 이 술어의 좌변이 아니다.
            /// </summary>
            private static bool ContainsColumn(ScalarExpression expression)
            {
                var probe = new ColumnPresenceProbe();
                expression.Accept(probe);
                return probe.Found;
            }

            private sealed class ColumnPresenceProbe : TSqlFragmentVisitor
            {
                public bool Found { get; private set; }

                public override void Visit(ColumnReferenceExpression node) => Found = true;

                /// <summary>하위 질의는 남의 스코프다. ExplicitVisit이라야 자식으로 안 내려간다.</summary>
                public override void ExplicitVisit(ScalarSubquery node) { }
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

                RecordComparisonPredicate(node);
                base.ExplicitVisit(node);
            }

            /// <summary>
            /// 리터럴을 우변에 둔 `=`·`&lt;&gt;` 비교를 원소 하나짜리 집합 사실로 담는다.
            ///
            /// [왜 등호까지 담는가 - 2026-08-19 축 A 감사]
            /// 감사에서 나온 대상 행 집합 결함 4건이 전부 "원본 필터가 명세서 어디에도
            /// 없다"는 한 부류였고, 그중 둘이 `CommissionCancelFlag = 1`이었다. 등호를
            /// 담지 않으면 L1이 대조할 재료 자체가 없어, 취소수수료 미부과 계약을
            /// 걸러내는 조건이 통째로 사라져도 아무 검사도 울리지 않는다. 원본 코퍼스
            /// 기준 이 형태가 129건이다.
            ///
            /// 우변이 리터럴일 때만 담는다 - `A.YMD = @pi_strYMD`나 `A.PLTID = B.PLTID`는
            /// 옮겨 적을 리터럴이 없고, 담으면 표가 기준일 비교와 조인 키로 뒤덮여 진짜
            /// 리터럴 집합이 묻힌다. 조인 키는 바로 위에서 <see cref="JoinKeys"/>가 담는다.
            /// </summary>
            private void RecordComparisonPredicate(BooleanComparisonExpression node)
            {
                var op = node.ComparisonType switch
                {
                    BooleanComparisonType.Equals => "=",
                    BooleanComparisonType.NotEqualToBrackets => "<>",
                    BooleanComparisonType.NotEqualToExclamation => "<>",
                    _ => null
                };

                if (op == null || node.SecondExpression is not Literal literal) return;

                var column = LeftSideText(node.FirstExpression);
                if (column == null) return;

                SetPredicates.Add((column, op, new List<string> { TextOf(literal) }));
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
