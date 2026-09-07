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
    /// <param name="Expression">대입식 우변의 원문(감쌈·분기식을 벗기지 않은 그대로).</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record NonAggregateAssignmentFact(
        int Line, string Variable, string Expression, string Sentence);

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
    /// DECLARE 초기값과 다르다"고 뭉뚱그리면 코퍼스 52행 중 31행에서 거짓이 된다. 그 31행은
    /// 앞선 대입이 아예 없어서 남는 값이 정확히 NULL이기 때문이다. 그래서 갈래를 둘로
    /// 나눈다(<see cref="AggregateAssignmentExtractor"/>가 초기값 유무로 문장을 가르는
    /// 것과 같은 방식이다).
    /// - 앞선 대입이 없다고 **판정되면**: 무결과 시 NULL이 남는다고 확정해서 말한다.
    /// - 그렇지 않으면: "이 문장에 도달한 시점의 값"까지만 말한다. 어떤 값인지는 기계가
    ///   모르므로 말하지 않는다.
    /// 판정 조건은 <see cref="SurvivingValueIsNull"/>에 있다.
    ///
    /// [무엇을 담는가 - 2026-09-06 넓힘, 2026-09-07 2 회차 재넓힘] 우변이 컬럼 참조 /
    /// 리터럴 / 그 둘의 산술식이거나, 그 셋을 결과(`THEN`·`ELSE`)로 갖는 `IIF`/`CASE`
    /// (한 겹만), 그리고 그 전체를 `ISNULL(X, 리터럴)`/`COALESCE(X, 리터럴)`로 한 겹 감싼
    /// 것이면 담는다(<see cref="AssignmentExpressionUnwrapper.IsCapturableExpression"/>).
    /// 1 회차는 "분기가 전부 컬럼"만 담았으나, 대상 칸이 이미 우변 원문을 축자로 싣게 된
    /// 뒤로는 그 좁힘의 근거(대상 칸이 흐려진다)가 사라져 있었다 - 2 회차가 분기 결과에
    /// 리터럴·산술식도 더했다.
    ///
    /// 넓혀도 확정 문장이 거짓이 되지 않는 이유가 중요하다 - 이 문장은 **행이 없다**를
    /// 말하지 우변의 모양을 말하지 않는다. 0행이면 대입 자체가 일어나지 않으므로 우변이
    /// 무엇으로 감싸여 있든 결론이 같다. 위험은 정반대쪽, 즉 집계 갈래에만 있다
    /// (거기서는 `ISNULL`이 대입되는 값을 실제로 바꾼다 -
    /// <see cref="AggregateAssignmentExtractor"/>가 그 갈래를 따로 말한다).
    ///
    /// [그래도 좁게 잡는 자리들] 분기 결과 안에 또 분기식(중첩 `CASE`/`IIF`)이 오거나,
    /// 함수 호출·하위 질의가 오면 담지 않는다 - 통째 완화를 실측해 보니 그 모양이
    /// 원본 줄 주석을 대상 칸 안으로 끌고 들어왔다(2026-09-07 2 회차 사전선언 §10-1).
    /// `ELSE` 없는 `CASE`도 담지 않으며, 감쌈의 기본값이 리터럴이 아니면 벗기지 않는다.
    /// 대입식이 집계를 품으면 결론이 정반대로 뒤집힌다(무결과여도 한 행이 돌아온다) -
    /// 집계는 잎이 아니라 <see cref="AssignmentExpressionUnwrapper.IsCapturableExpression"/>의
    /// 판정에 걸리지 않으므로 이 추출기는 그 자리를 담지 않는다.
    ///
    /// [그런데 그 반대쪽이 항상 담기는 것은 아니다 - 원본과 두 대장에서 직접 확인했다]
    /// `UP_UTIL_SETTLE_PROC_ETC.Procedure:116`의 `ISNULL(SUM(...),0)`은 최상위가
    /// `ISNULL`이고 한 겹 벗기면 안쪽이 `FunctionCall`(`SUM`)이라
    /// <see cref="AggregateAssignmentExtractor"/>가 담는다(그 클래스의
    /// `unwrapped.Inner is not FunctionCall call` 가드를 통과한다) - **이제는** 집계
    /// 추출기의 몫이 맞다.
    ///
    /// 그러나 같은 객체 `:101`의 `SELECT @v_intID = MAX(ID)+1 FROM TSettleMiss`는
    /// 최상위가 이항식(`MAX(ID) + 1`)이라 한 겹 벗겨도 안쪽이 `FunctionCall`이 아니고
    /// (`BinaryExpression`), 집계 추출기의 같은 가드에 걸려 **담기지 않는다**
    /// (`AggregateAssignmentExtractorTests`에도 이 행을 담는 시험이 없다). 이 클래스도
    /// `MAX(ID)+1`은 `IsCapturableExpression`이 `FunctionCall`을 잎으로 인정하지 않아
    /// 담지 않는다(`Extract_AggregateInsideArithmetic_ShouldNotBeCollected`가 이 침묵을
    /// 잠근다). 결과적으로 `:101`은 **어느 대장에도 없다** - 두 그물 사이로 새는 자리다.
    ///
    /// 이 사실은 base(`0b6df499`)의 클래스 주석이 정확히 적고 있었다: "최상위가
    /// 이항식/스칼라 함수라 집계 추출기는 담지 않지만, 질의 자체는 집계라 무결과여도
    /// 한 행이 돌아온다." 이 문단이 한때 그 문장을 "그쪽은 집계 추출기의 몫이다"로
    /// 갈아치워 열린 구멍을 닫힌 것처럼 적었다 - `:116`은 그 회차가 참으로 만들었지만
    /// `:101`은 그 뒤로도 계속 비어 있었다. 이 문단은 그 서술을 사실로 되돌린 것이고,
    /// `:101`을 담게 만드는 것은 이 브랜치의 범위 밖이다(별건).
    ///
    /// [넓히기 전에 무엇이 빠졌었나] `UF_GET_COMM4CLIENT4PARTIALCANCEL:43`의
    /// `IIF(@pi_intFreeInterestFlag IN (0,2), A.CommissionRate, A.FreeInterestInstCommRate)`가
    /// 어떤 기계 확정 표에도 없었고, 명세서가 수수료율 분기를 한 줄도 서술하지 않았다
    /// (2026-09-06 축 A 감사 🔴).
    ///
    /// [집계는 FROM 절에도 산다] 수정 라운드 1 - 식이 컬럼 참조여도
    /// `FROM (SELECT MAX(ID) AS MaxID FROM t) X`처럼 파생 테이블이 집계를 품으면 원본이
    /// 비어도 한 행이 돌아온다. 그러면 이 SELECT는 0행이 되지 않아 "무결과"를 전제한
    /// 문장을 읽는 사람이 정반대로 이해한다. 그래서 FROM 절이 집계를 품으면 담지 않는다.
    /// **코퍼스에 이 모양은 없다** - 31개 객체(로컬 24 + 외부 7)의 object_definition.sql을
    /// 이 추출기로 훑어 이 가드 도입 전후 행이 43행으로 같음을 확인했다(2026-09-06 재실측 -
    /// 코퍼스가 External까지 넓어지고 우변 가드가 감쌈까지 담게 되면서 8행이던 예전 수치가
    /// 낡았다). 그 뒤 2026-09-07 2 회차가 분기 결과 판정을 다시 넓혀 43행에서 52행이
    /// 됐다 - 이 가드의 분모(FROM 절이 집계를 품은 문장 수)는 그 회차도 여전히 0건이다.
    ///
    /// [집계는 CTE에도 산다] 수정 라운드 2 - 같은 함정인데 붙는 자리가 다르다.
    /// `WITH c AS (SELECT MAX(ID) AS m FROM t) SELECT @v = c.m FROM c`에서 WITH 절은
    /// FromClause 아래가 아니라 문장(<c>StatementWithCtesAndXmlNamespaces</c>)에 달려 있어,
    /// FROM만 훑는 위 가드가 이 집계를 보지 못한다. 그래서 **WITH를 단 문장에 속한
    /// QuerySpecification은 통째로 침묵한다**(<see cref="CteStatementRangeCollector"/>).
    ///
    /// 대가를 적어 둔다: 이 가드는 비집계 CTE
    /// (`WITH c AS (SELECT ID FROM t) SELECT @v = c.ID FROM c`)까지 함께 침묵시킨다. 그쪽은
    /// 사실 문장이 참이므로 담을 수 있는 행을 버리는 셈이다. 그래도 이렇게 하는 이유는,
    /// 가려내려면 CTE 본문마다 집계를 판정하고 어느 CTE가 이 FROM에 실제로 닿는지까지
    /// 따라가야 하는데(재귀 CTE·중첩 CTE·참조되지 않는 CTE가 모두 갈래를 늘린다), 그 판정이
    /// 한 군데라도 새면 표에는 정반대 문장이 실리기 때문이다. 이 표는 「수정 금지」이고 L1이
    /// 축자 전사를 강제하므로 거짓 행을 뒤에서 거를 장치가 없다 - AGENTS.md 범주 2와 같은
    /// 원칙으로 거짓 행보다 없는 행을 고른다.
    ///
    /// **코퍼스에 이 모양도 없다** - 31개 객체를 파싱해 <c>CommonTableExpression</c> 노드를
    /// 센 결과가 0건이다. 문자열 검색(`WITH ... AS (`)이 아니라 AST 노드 수로 확인했다 -
    /// 코퍼스는 `WITH(NOLOCK)` 힌트를 곳곳에 쓰고 있어 문자열로는 둘이 구분되지 않는다.
    /// 그 0건과 아래 52행(2026-09-07 2 회차 재넓힘 반영)을 함께 못박은 것이
    /// <c>NonAggregateAssignmentExtractorTests.Extract_OverTheCorpus_...</c>다.
    ///
    /// [복합 대입은 담지 않는다] 수정 라운드 2 - `SELECT @v += col`도 SelectSetVariable로
    /// 담기지만 대상 칸은 `SELECT @v = col`로 렌더돼 원문에 없는 문장이 표에 실린다.
    /// <see cref="LoopVariableResetExtractor"/>가 같은 자리에서 거르는 것과 같은 규칙으로
    /// <c>AssignmentKind != Equals</c>면 침묵한다. 코퍼스의 복합 대입은 전부
    /// `UPDATE ... SET` 컬럼 대입이라 SelectSetVariable로는 0건이다(위 코퍼스 테스트가
    /// 68건 중 0건으로 못박는다).
    ///
    /// [왜 FROM 절을 요구하는가] `SELECT @v = ID`처럼 FROM이 없으면 무결과라는 개념이
    /// 없다 - 한 행이 반드시 돌아와 대입이 일어난다. FROM이 없는 문장에 이 사실 문장을
    /// 붙이면 거짓이 된다.
    ///
    /// [암묵적 그룹화도 진리조건을 뚫는다 - 2 회차가 연 거짓 행 경로를 닫는다] 위의
    /// FROM 집계 · CTE 집계 가드는 모두 "집계가 어디 사는가"를 식 · FROM · WITH에서
    /// 찾는다. 그런데 `GROUP BY` 없는 `HAVING`은 SELECT 목록 어디에도 집계가 없어도
    /// 같은 함정을 연다 - T-SQL은 `GROUP BY` 없는 질의 전체를 암묵적으로 한 그룹으로
    /// 묶으므로, `HAVING` 조건이 참이면(예: `HAVING COUNT(*) = 0`은 원본이 비었을 때
    /// 참이다) 행이 0건이어도 **1행을 돌려준다.** 그러면 무결과를 전제로 "대입 자체가
    /// 일어나지 않는다"고 말하는 이 확정 문장이 거짓이 된다.
    ///
    /// 1 회차는 이 자리가 안전했다 - 최상위가 <see cref="ColumnReferenceExpression"/>뿐이었고,
    /// 암묵 그룹 질의의 SELECT 목록에 맨 컬럼이 오는 것은 애초에 SQL로 불법이라(그
    /// 컬럼이 `GROUP BY`에도 집계 함수 안에도 있지 않은 채로 `HAVING`과 함께 쓰이면
    /// 구문 오류다) 이 모양이 못 들어왔다. **2 회차가 `case Literal: return true;`
    /// (<see cref="AssignmentExpressionUnwrapper.IsCapturableExpression"/>)로 최상위
    /// 리터럴을 열면서 길이 났다** - 리터럴은 암묵 그룹 목록에 합법이라
    /// `SELECT @v = 1 FROM T HAVING COUNT(*) = 0`이 파싱을 통과하고 우변 가드도
    /// 통과해 표에 실렸다.
    ///
    /// 그래서 <see cref="QuerySpecification.HavingClause"/>가 있으면 우변 모양과
    /// 무관하게 문장 전체를 통째로 침묵한다 - `GROUP BY` 유무를 가리지 않는다.
    /// `GROUP BY`가 있으면 원본이 비었을 때 그룹이 0개이므로 이 SELECT는 실제로
    /// 0행이 될 수 있어 확정 문장이 참인 경우도 있지만(예:
    /// `... GROUP BY A.x HAVING COUNT(*) > 0`), 그 경우만 가려내려면
    /// `ROLLUP`/`CUBE`/`GROUPING SETS` 등 `GROUP BY`의 변형까지 옳게 판정하는 조건이
    /// 새로 필요하고, 그 판정이 한 군데라도 새면 정반대 문장이 「수정 금지」 표에
    /// 실린다(클래스 주석 "집계는 CTE에도 산다"와 같은 논리 - 거짓 행보다 없는 행을
    /// 고른다). **정정(★ 둘째 재검토)**: 코퍼스에 `HAVING`이 0건이 아니다 -
    /// `UP_UTIL_SETTLE_COMM_UPD.Procedure`의 원본
    /// `raw/object_definition.sql:248`에 `HAVING SUM(TxAmt) = 0`이 실재한다(`UPDATE ...
    /// FROM ...`의 파생 테이블 안, `SelectSetVariable`과 무관한 자리). 참인 문장은
    /// "**대입 SELECT** 중 `HAVING`을 단 것이 0건"이다 - 이 가드가 보는 것은
    /// `SelectSetVariable`을 가진 `QuerySpecification`뿐이라, 그 좁은 관할 안에서는
    /// 이 선택의 비용이 지금도 0이다.
    ///
    /// [형제도 같은 함정을 연다] `SELECT @a = 1, @b = COUNT(*) FROM T`처럼 한
    /// <see cref="QuerySpecification.SelectElements"/> 안에 비집계 대입과 집계 대입이
    /// 섞이면, 집계 쪽이 있다는 사실만으로 이 SELECT는 무결과여도 1행을 돌려준다 -
    /// `HAVING`과 같은 결과다. 그러면 `@a = 1`에 "무결과 시 대입이 일어나지 않는다"를
    /// 붙이는 것과 `@b = COUNT(*)`에 <see cref="AggregateAssignmentExtractor"/>가 붙이는
    /// "무결과여도 대입이 항상 일어난다"가 같은 표에 나란히 실려 정반대를 말한다.
    /// 그래서 형제 <see cref="SelectSetVariable"/>의 식에 집계가 있으면 이
    /// QuerySpecification 전체를 비집계 쪽에서 침묵한다(집계 쪽은 이 가드와 무관하게
    /// 그대로 담긴다 - 두 갈래가 배타적으로 유지된다). `SelectSetVariable`이 아닌
    /// 형제(예: `SELECT @v = ID, Name FROM T`)도 같은 이유로 침묵한다 - 이쪽은 집계
    /// 여부와 무관하게 대상 칸이 이 SELECT의 부분(`@v = ID`)만을 가리키는 것이 맞는지부터
    /// 이 판정 범위 밖이라, 좁게 잡는다. 코퍼스에 두 모양 다 0건이다(전수 스캔 - 아래
    /// 코퍼스 시험의 <c>setVariables</c> · <c>compoundSetVariables</c>와 나란히 잰다).
    /// </summary>
    public static class NonAggregateAssignmentExtractor
    {
        /// <summary>
        /// 집계 함수 이름. AggregateAssignmentExtractor의 목록보다 넓다 - 그쪽은 담을
        /// 사실을 고르는 목록이지만 이쪽은 **거짓을 막는** 목록이라, 하나라도 새면
        /// 정반대 문장이 표에 실린다.
        ///
        /// [★ 둘째 재검토 - APPROX_COUNT_DISTINCT · APPROX_PERCENTILE_CONT ·
        /// APPROX_PERCENTILE_DISC를 더한다] 근거는 Microsoft T-SQL 문서의 "집계 함수
        /// (Transact-SQL)" 분류다 - 그 분류에 속한 함수는 전부 원본이 비어도(GROUP BY가
        /// 없으면) 정확히 1행을 돌려주는 동일한 진리조건을 공유한다. 이 셋은 그 분류의
        /// 구성원이면서 기존 목록에 없었다 - 실행 확인으로도 새는 것을 재현했다
        /// (`Extract_SiblingApproxCountDistinctInSameSelect_...` ·
        /// `Extract_AggregateInsideDerivedTable_ApproxCountDistinct_...`).
        ///
        /// [뺀 것] `PERCENTILE_CONT`/`PERCENTILE_DISC`(근사가 아닌 쪽)와
        /// `RANK`/`DENSE_RANK`/`ROW_NUMBER`/`NTILE`/`LAG`/`LEAD`/`FIRST_VALUE`/
        /// `LAST_VALUE`는 문서에서 "집계 함수"가 아니라 "분석 함수"로 따로 분류되고,
        /// 반드시 `OVER(...)`를 동반해 행마다 계산되는 윈도 함수라 결과 카디널리티를
        /// 줄이지 않는다 - 이 목록이 막으려는 "0행이 1행이 된다"는 함정 자체가
        /// 성립하지 않으므로 넣지 않는다.
        /// </summary>
        private static readonly HashSet<string> AggregateNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "MIN", "MAX", "SUM", "AVG", "COUNT", "COUNT_BIG", "STDEV", "STDEVP",
            "VAR", "VARP", "CHECKSUM_AGG", "STRING_AGG", "GROUPING", "GROUPING_ID",
            "APPROX_COUNT_DISTINCT", "APPROX_PERCENTILE_CONT", "APPROX_PERCENTILE_DISC"
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

                var cteStatements = new CteStatementRangeCollector();
                fragment.Accept(cteStatements);

                var visitor = new NonAggregateAssignmentVisitor(scope, cteStatements);
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

        /// <summary>
        /// WITH 절을 단 문장이 원문에서 차지하는 범위를 모은다(클래스 주석의 "집계는 CTE에도
        /// 산다").
        ///
        /// 범위로 재는 이유: 방문자는 QuerySpecification 단위로 훑는데 ScriptDom 노드에는
        /// 부모 포인터가 없어 "내가 속한 문장이 WITH를 달았는가"를 노드에서 되물을 수 없다.
        /// 그래서 문장 범위를 미리 모아 두고 오프셋 포함 여부로 판정한다. 범위는 그 문장
        /// 하나까지다 - 객체에 CTE가 하나 있다고 나머지 문장까지 침묵하면 안 된다.
        /// </summary>
        private sealed class CteStatementRangeCollector : TSqlFragmentVisitor
        {
            private readonly List<(int Start, int End)> _ranges = new();

            public override void Visit(StatementWithCtesAndXmlNamespaces node)
            {
                var ctes = node.WithCtesAndXmlNamespaces?.CommonTableExpressions;
                if (ctes == null || ctes.Count == 0) return;
                if (node.StartOffset < 0 || node.FragmentLength <= 0) return;

                _ranges.Add((node.StartOffset, node.StartOffset + node.FragmentLength));
            }

            public bool Contains(int offset)
                => _ranges.Any(range => offset >= range.Start && offset < range.End);
        }

        private sealed class NonAggregateAssignmentVisitor : TSqlFragmentVisitor
        {
            private readonly VariableScopeVisitor _scope;
            private readonly CteStatementRangeCollector _cteStatements;

            public NonAggregateAssignmentVisitor(
                VariableScopeVisitor scope, CteStatementRangeCollector cteStatements)
            {
                _scope = scope;
                _cteStatements = cteStatements;
            }

            public List<NonAggregateAssignmentFact> Facts { get; } = new();

            // AggregateAssignmentExtractor와 같은 이유로 QuerySpecification 단위로 훑는다:
            // SelectSetVariable 단독으로는 자신을 감싼 문장의 FromClause를 알 수 없다
            // (부모 포인터가 없다). ScriptDom은 Visit을 오버라이드해도 자식 순회를
            // 계속하므로 중첩된 QuerySpecification도 그대로 방문된다.
            public override void Visit(QuerySpecification node)
            {
                // FROM이 없으면 무결과가 성립하지 않는다 - 확정 사실 문장이 거짓이 된다.
                if (node.FromClause == null) return;

                // WITH를 단 문장에 속하면 침묵한다(클래스 주석의 "집계는 CTE에도 산다").
                if (_cteStatements.Contains(node.StartOffset)) return;

                var aggregateInFrom = new AggregateInFromDetector();
                node.FromClause.Accept(aggregateInFrom);
                if (aggregateInFrom.Found) return;

                // GROUP BY 없는 HAVING은 암묵적으로 전체를 한 그룹으로 묶어 무결과여도
                // 1행을 돌려준다(클래스 주석 "암묵적 그룹화도 진리조건을 뚫는다"). 우변
                // 모양과 무관하게 이 문장 전체를 침묵한다 - GROUP BY 유무는 가리지 않는다.
                if (node.HavingClause != null) return;

                // ★ 둘째 재검토 - 같은 진리조건이 GROUP BY 쪽에도 열려 있었다. 총계
                // 그룹화 집합(grand total grouping set)은 원본이 비어도 그룹을 정확히
                // 1개 만들어 1행을 돌려준다 - `GROUP BY ()` · `GROUPING SETS(())`(빈
                // 원소를 하나라도 포함) · `ROLLUP(...)` · `CUBE(...)` · `... WITH ROLLUP`가
                // 전부 이 모양이고, ScriptDom으로 직접 파싱해 각각의
                // QuerySpecification.GroupByClause가 non-null임을 확인했다(단위 시험
                // 일곱 개가 근거). `WITH ROLLUP`형은 우변이 맨 컬럼이어도 합법이라
                // 2 회차의 리터럴 개방과 무관하게 1 회차부터 열려 있던 자리다.
                //
                // 변형을 하나하나 가려서 총계 그룹인 것만 침묵시키는 조건은 새로
                // 만들지 않는다 - HavingClause 가드와 같은 논리로, 조건이 한 군데라도
                // 새면 정반대 문장이 「수정 금지」 표에 그대로 실리고 뒤에서 거를 장치가
                // 없다. 그래서 `GroupByClause`가 있으면 총계 그룹이 실제로 생기든
                // 안 생기든(평범한 `GROUP BY A.x`처럼 총계 행이 없는 경우까지) 통째로
                // 침묵한다 - 보수적 선택이다. 코퍼스에 SelectSetVariable을 가진
                // QuerySpecification 중 GROUP BY를 단 것이 0건이라 이 선택의 비용은
                // 지금 0이다(아래 코퍼스 시험의 groupByOnSetVariableQueries).
                if (node.GroupByClause != null) return;

                // 형제 SelectSetVariable이 집계를 품었거나, SelectSetVariable이 아닌
                // 형제가 있으면 이 SELECT는 HAVING과 같은 함정을 연다(위 주석 "형제도
                // 같은 함정을 연다"). 문장 전체를 비집계 쪽에서 침묵한다.
                if (HasNonSetVariableSibling(node) || HasAggregateSibling(node)) return;

                foreach (var element in node.SelectElements)
                {
                    if (element is not SelectSetVariable setVariable) continue;

                    // `SELECT @v += col`은 대상 칸이 `SELECT @v = col`로 렌더돼 원문에 없는
                    // 문장이 된다. 형제 LoopVariableResetExtractor와 같은 규칙으로 거른다.
                    if (setVariable.AssignmentKind != AssignmentKind.Equals) continue;

                    // 감쌈을 한 겹 벗겨 안쪽이 "전부 컬럼 참조인 분기식"인지 본다
                    // (클래스 주석의 "무엇을 담는가"). 대상 칸에는 벗기기 **전** 원문을
                    // 싣는다 - 벗긴 것을 실으면 원문에 없는 문장이 표에 들어간다.
                    if (setVariable.Expression == null) continue;

                    var unwrapped = AssignmentExpressionUnwrapper.Unwrap(setVariable.Expression);
                    if (!AssignmentExpressionUnwrapper.IsCapturableExpression(unwrapped.Inner)) continue;

                    var expressionText = AssignmentExpressionUnwrapper.TextOf(setVariable.Expression);
                    if (string.IsNullOrWhiteSpace(expressionText)) continue;

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
                        setVariable.StartLine, variable, expressionText, sentence));
                }
            }

            /// <summary>
            /// 같은 <see cref="QuerySpecification.SelectElements"/> 안에
            /// <see cref="SelectSetVariable"/>이 아닌 요소가 있는가(클래스 주석 "형제도
            /// 같은 함정을 연다"). 있으면 대상 칸이 이 SELECT의 부분만을 가리키는 것이
            /// 맞는지가 이 판정 범위 밖이라 좁게 침묵한다. 코퍼스에 0건이다.
            /// </summary>
            private static bool HasNonSetVariableSibling(QuerySpecification node)
                => node.SelectElements.Any(element => element is not SelectSetVariable);

            /// <summary>
            /// 형제 <see cref="SelectSetVariable"/>의 식 어딘가에 집계 함수가 있는가
            /// (클래스 주석 "형제도 같은 함정을 연다"). <see cref="AggregateInFromDetector"/>를
            /// 재사용한다 - FROM 절 전용이 아니라 어떤 조각을 훑어도 그 안의
            /// <see cref="FunctionCall"/> 이름만 본다.
            /// </summary>
            private static bool HasAggregateSibling(QuerySpecification node)
            {
                foreach (var element in node.SelectElements)
                {
                    if (element is not SelectSetVariable sibling || sibling.Expression == null) continue;

                    var detector = new AggregateInFromDetector();
                    sibling.Expression.Accept(detector);
                    if (detector.Found) return true;
                }

                return false;
            }
        }
    }
}
