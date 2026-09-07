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
    /// 문장을 읽는 사람이 정반대로 이해한다. 그래서 FROM의 각 원천이 재귀 판정을
    /// 통과하지 못하면 담지 않는다(이 라운드는 FROM 절 전체를 하나로 봤으나, 3
    /// 회차가 이 조건을 원천별 재귀 판정으로 일반화했다 - 아래 "3 회차" 문단과
    /// <see cref="NonAggregateAssignmentVisitor.GuaranteesZeroRows(TableReference)"/>
    /// 참고. "FROM 절이 집계를 품으면 통째로 침묵한다"는 지금은 거짓이다 - 예를 들어
    /// `FROM T0 OUTER APPLY (SELECT COUNT(*) n FROM S) X`는 왼쪽 `T0`가 보장하므로
    /// 오른쪽의 집계와 무관하게 담긴다).
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
    ///
    /// [3 회차 - 진리 조건을 한 문장으로 못박고 층을 판정에서 없앴다] 위 세 함정
    /// (FROM 집계·`HAVING`·`GROUP BY`·형제)은 전부 같은 진리 조건 - "이 질의의 기저
    /// 테이블이 모두 비면, 이 질의는 0행을 돌려준다" - 이 뚫리는 자리였는데, 예전
    /// 코드는 이 조건을 여섯 자리에 흩어 각자 다른 층만 봤다. FROM 집계 검사만
    /// `node.FromClause.Accept(...)`로 하위까지 훑었고, `HAVING`·`GROUP BY`·형제
    /// 검사는 **이 QuerySpecification 자신의 층**만 봤다. 그래서 같은 위험한 모양이
    /// 파생 테이블·APPLY 안으로 한 층만 들어가면(예: `FROM (SELECT 1 c FROM T
    /// GROUP BY ()) D`) 판정에서 벗어나 다시 열렸다 - 이 브랜치에서 전 범위 검토가
    /// 세 번 났고 세 번 다 이 부류의 Critical이었다. 이 회차는 그 여섯 자리를
    /// <see cref="NonAggregateAssignmentVisitor.GuaranteesZeroRowsWhenSourcesAreEmpty"/>
    /// 라는 단일 재귀 술어로 접는다 - 그 술어가 FROM의 각 원천(이름 있는 테이블·파생
    /// 테이블·조인·APPLY·VALUES·TVF·PIVOT/UNPIVOT·테이블 변수)에 "너는 기저 테이블이
    /// 비면 0행을 보장하는가"를 재귀로 되묻는다. 다음 사람이 이 진리 조건을 넓힐
    /// 때는 "어느 층에 검사를 추가할까"가 아니라 "이 새 노드 종류가 그 질문에 뭐라고
    /// 답하는가"만 물으면 된다 - 층 자체가 판정에서 사라졌기 때문이다. 조건의
    /// 집합은 이 회차에서 넓히거나 좁히지 않았다(코퍼스 대장 52행 불변) - 설계서
    /// `docs/superpowers/specs/2026-09-06-대입-감쌈-벗기기-design.md` §12.
    ///
    /// [4 회차 - 암묵적 그룹화를 켜는 절은 셋이다] 3 회차까지는 이 목록을 SELECT
    /// 목록(투영)·`HAVING` 둘로 셌다. **다음 사람이 넷째를 찾을 때를 위해 지금
    /// 아는 셋을 여기 못박는다** - `GROUP BY` 없는 질의에서 T-SQL의 암묵적 전체
    /// 그룹화를 켜는 절은:
    /// 1. SELECT 목록(투영) - <see cref="NonAggregateAssignmentVisitor.ProjectionHasAggregate"/>.
    /// 2. `HAVING` - 절의 **존재 자체**가 켠다(집계 유무와 무관, 위 "암묵적
    ///    그룹화도 진리조건을 뚫는다" 문단).
    /// 3. `ORDER BY` - 단, 이쪽은 절의 존재가 아니라 **집계를 품을 때만** 켠다
    ///    (`ORDER BY`는 결과 카디널리티를 바꾸지 않고 정렬만 하므로, 집계가 없으면
    ///    무결과 시 실제로 0행이다) -
    ///    <see cref="NonAggregateAssignmentVisitor.OrderByHasAggregate"/>.
    /// `GROUP BY` 자체도 총계 그룹화 집합이면 같은 함정을 열지만 그것은 이 목록과
    /// 별도로 다룬다(둘째 재검토 문단, 절 유무 통짜 침묵). 코퍼스 노출은 0이다 -
    /// `SelectSetVariable`을 가진 질의 32건 중 `ORDER BY`를 단 것 4건이 있고, 그
    /// 넷 전부 원본에서 집계를 품지 않는다(설계서 §14). 실행 재현은 SQL Server로
    /// 직접 확인하지 못했다(로컬은 빈 스키마) - 파싱 통과와 이 침묵은 실측이고,
    /// 무결과 시 1행 반환은 T-SQL 명세에 근거한 판단이다.
    ///
    /// [5 회차(마지막 회차) - 다섯째 축, 감싼 집합 연산] 위 진리 조건은 전부 "이
    /// `QuerySpecification` 자신과 그 FROM"만 본다 - 그런데 이 질의가 **자기를
    /// 감싼 `QueryExpression`**의 일부일 수 있다는 것은 아무도 묻지 않았다. 파생
    /// 테이블 안에서는 무해하다 - <see cref="NonAggregateAssignmentVisitor.
    /// GuaranteesZeroRows(QueryExpression)"/>가 그 감쌈(`BinaryQueryExpression`의
    /// UNION/UNION ALL)을 이미 갈래마다 재귀로 합성해서 판정하기 때문이다(호출자가
    /// FROM 원천으로 들어갈 때 이 메서드를 거친다). **최상위에서는 다르다** -
    /// `SelectSetVariable`을 가진 질의가 최상위 `BinaryQueryExpression`의 한
    /// 갈래이면, `Visit(QuerySpecification)`은 그 사실을 모른 채(부모 포인터가
    /// 없다) 그 갈래 자신의 FROM만으로 판정해 버린다. 그런데 이 SELECT 문 전체의
    /// 무결과 여부는 그 갈래 하나가 아니라 **모든 갈래**에 달려 있다 - 다른
    /// 갈래가 행을 하나라도 내면(상수 원천·총계 그룹·`HAVING`·집계·다른 갈래의
    /// `ORDER BY` 등, 이 술어가 이미 아는 함정들과 똑같다) 전체 UNION은 무결과가
    /// 아니다. 게다가 `ORDER BY`·`OFFSET`·`FOR` 절은 `BinaryQueryExpression`
    /// 자신에도 달릴 수 있는데(`SELECT ... UNION ALL SELECT ... ORDER BY ...`),
    /// 그 절을 보는 경로가 이 추출기 어디에도 없다(<see cref="NonAggregateAssignmentVisitor.
    /// GuaranteesZeroRowsWhenSourcesAreEmpty"/>는 `QuerySpecification`만 받고,
    /// 재귀 <see cref="NonAggregateAssignmentVisitor.GuaranteesZeroRows(QueryExpression)"/>도
    /// `BinaryQueryExpression`의 `OrderByClause`·`OffsetClause`·`ForClause`를
    /// 읽지 않는다). 그래서 개별 조건을 하나씩 더 넓히는 대신, "이 질의가 어떤
    /// 집합 연산의 갈래인가"만 물어 그렇다면 **통째로 침묵**한다 - CTE 판정과
    /// 같은 이유다: 갈래마다 옳게 판정하려면 모든 갈래를 순회하며 이 술어를
    /// 되묻고 `BinaryQueryExpression` 자신의 절까지 따로 판정해야 하는데, 그
    /// 판정이 한 군데라도 새면 거짓 행이 표에 실리기 때문이다(거짓 행보다 없는
    /// 행을 고른다). 범위는 <see cref="SetOperationBranchRangeCollector"/>가
    /// <see cref="CteStatementRangeCollector"/>와 같은 기법(부모 포인터가 없어
    /// 원문 범위 + 오프셋 포함 여부로 판정)으로 모은다 - 이름만 내용에 맞춘다.
    /// 코퍼스 노출은 0이다(`SelectSetVariable`을 가진 질의 중 집합 연산의 갈래인
    /// 것이 0건). 도달성은 미검증이다 - SQL Server가 변수 대입과 집합 연산의
    /// 결합 자체를 거부할 가능성이 있다(설계서 §15 CANNOT VERIFY). 그래도 같은
    /// 자(4 회차의 `ORDER BY COUNT(*)`도 도달성 미검증인 채로 코퍼스 대가 0을
    /// 근거로 넣었다)로 재면 여기도 넣는 것이 맞다 - 침묵은 거짓 행을 만들지
    /// 않고, 대가가 0이면 안전장치를 마다할 이유가 없다.
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
        ///
        /// [3 회차 - JSON_ARRAYAGG · JSON_OBJECTAGG를 더한다] 설계서 §12-5. 같은
        /// Microsoft 분류("집계 함수 (Transact-SQL)")의 구성원이고 GROUP BY가 없으면
        /// 원본이 비어도 정확히 1행(빈 배열/빈 객체)을 돌려주는 같은 진리조건을
        /// 공유한다 - 목록에 없어서 형제·FROM(파생 테이블) 양쪽에서 새는 것을
        /// 실행으로 재현했다(`Extract_SiblingJsonArrayAggInSameSelect_...` ·
        /// `Extract_AggregateInsideDerivedTable_JsonObjectAgg_...`).
        /// </summary>
        private static readonly HashSet<string> AggregateNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "MIN", "MAX", "SUM", "AVG", "COUNT", "COUNT_BIG", "STDEV", "STDEVP",
            "VAR", "VARP", "CHECKSUM_AGG", "STRING_AGG", "GROUPING", "GROUPING_ID",
            "APPROX_COUNT_DISTINCT", "APPROX_PERCENTILE_CONT", "APPROX_PERCENTILE_DISC",
            "JSON_ARRAYAGG", "JSON_OBJECTAGG"
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

                var setOperationBranches = new SetOperationBranchRangeCollector();
                fragment.Accept(setOperationBranches);

                var visitor = new NonAggregateAssignmentVisitor(scope, cteStatements, setOperationBranches);
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

        /// <summary>
        /// 어떤 조각이든 그 안에 집계 함수 호출이 있는지 본다(클래스 주석의 "집계는
        /// FROM 절에도 산다"). 3 회차 - 이름이 "FROM" 전용이던 시절의 흔적이었다.
        /// 지금은 <see cref="GuaranteesZeroRowsWhenSourcesAreEmpty"/>가 질의 층마다
        /// 재귀로 재사용하므로(형제 SelectSetVariable의 식이든, 파생 테이블 안쪽
        /// QuerySpecification의 투영이든) 훑는 대상이 FROM 절 하나로 고정돼 있지
        /// 않다 - 이름을 그 실태에 맞춘다.
        /// </summary>
        private sealed class AggregateFunctionDetector : TSqlFragmentVisitor
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

        /// <summary>
        /// 5 회차(설계서 §15) - 어떤 `BinaryQueryExpression`(UNION/UNION ALL/EXCEPT/
        /// INTERSECT, 모든 종류를 가리지 않는다)이 원문에서 차지하는 범위를 모은다
        /// (클래스 주석의 "감싼 집합 연산").
        ///
        /// <see cref="CteStatementRangeCollector"/>와 똑같은 기법이다 - 방문자는
        /// `QuerySpecification` 단위로 훑는데 ScriptDom 노드에는 부모 포인터가 없어
        /// "내가 어떤 집합 연산의 갈래인가"를 노드에서 되물을 수 없다. 그래서
        /// `BinaryQueryExpression` 자신의 원문 범위(양쪽 갈래를 포함한 전체, 괄호로
        /// 감쌌으면 그 괄호까지)를 미리 모아 두고, 판정 대상 `QuerySpecification`의
        /// 시작 오프셋이 그 범위 안에 있는지로 "이 질의가 어떤 집합 연산의 갈래다"를
        /// 판정한다.
        ///
        /// [파생 테이블 안의 UNION과 섞이지 않는 이유] `SelectSetVariable`(변수 대입)은
        /// 문법상 파생 테이블·하위 질의 안에 올 수 없다 - `(SELECT @v = 1 FROM T) x`
        /// 같은 모양은 SQL Server가 애초에 거부한다. 그래서 이 범위 안에 실제로 드는
        /// `SelectSetVariable`을 가진 `QuerySpecification`은 오직 **최상위 문장**이
        /// 집합 연산인 경우뿐이다 - 파생 테이블 안쪽 UNION의 갈래(예:
        /// `FROM (SELECT x FROM T1 UNION ALL SELECT x FROM T2) D`의 두 갈래)는
        /// `SelectSetVariable`을 가질 수 없으므로 아래 가드는 그 갈래들에서는 아무
        /// 일도 하지 않는다 - `Extract_UnionAllWithBothNamedTableBranchesInDerivedTable_
        /// IsCaptured`가 그 무해함을 그대로 잠근다(그 시험의 바깥쪽 `QuerySpecification`
        /// 은 이 범위 밖에 있고, 안쪽 두 갈래는 이 범위 안에 있지만 `SelectSetVariable`이
        /// 없다).
        /// </summary>
        private sealed class SetOperationBranchRangeCollector : TSqlFragmentVisitor
        {
            private readonly List<(int Start, int End)> _ranges = new();

            public override void Visit(BinaryQueryExpression node)
            {
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
            private readonly SetOperationBranchRangeCollector _setOperationBranches;

            public NonAggregateAssignmentVisitor(
                VariableScopeVisitor scope,
                CteStatementRangeCollector cteStatements,
                SetOperationBranchRangeCollector setOperationBranches)
            {
                _scope = scope;
                _cteStatements = cteStatements;
                _setOperationBranches = setOperationBranches;
            }

            public List<NonAggregateAssignmentFact> Facts { get; } = new();

            // AggregateAssignmentExtractor와 같은 이유로 QuerySpecification 단위로 훑는다:
            // SelectSetVariable 단독으로는 자신을 감싼 문장의 FromClause를 알 수 없다
            // (부모 포인터가 없다). ScriptDom은 Visit을 오버라이드해도 자식 순회를
            // 계속하므로 중첩된 QuerySpecification도 그대로 방문된다.
            public override void Visit(QuerySpecification node)
            {
                // WITH를 단 문장에 속하면 침묵한다(클래스 주석의 "집계는 CTE에도 산다").
                // 문장 범위로 재는 판정이라 층에 이미 무관하다 - 재귀 술어 밖에
                // 그대로 둔다(설계서 §12 "CTE 판정은 지금 방식 그대로 둬도 된다").
                if (_cteStatements.Contains(node.StartOffset)) return;

                // ★ 5 회차(마지막 회차, 클래스 주석의 "감싼 집합 연산") - 이 질의가
                // 어떤 집합 연산(`BinaryQueryExpression`)의 갈래이면 침묵한다. 이
                // 질의 자신의 FROM만으로는 다른 갈래가 행을 내는지 알 수 없고,
                // `BinaryQueryExpression` 자신에 달린 `ORDER BY`·`OFFSET`·`FOR`도
                // 이 추출기 어디에서도 읽지 않기 때문이다. CTE 판정과 같은 범위
                // 기법을 쓴다 - `SetOperationBranchRangeCollector` 참고. 파생
                // 테이블 안의 UNION은 `SelectSetVariable`을 애초에 가질 수 없어
                // 이 가드가 손대지 않는다(그 주석 참고).
                if (_setOperationBranches.Contains(node.StartOffset)) return;

                // SelectSetVariable이 아닌 형제가 있으면 대상 칸이 이 SELECT의
                // 부분만을 가리키는 것이 맞는지가 판정 범위 밖이라 좁게 침묵한다(클래스
                // 주석 "형제도 같은 함정을 연다"). 이것도 "이 문장이 판정 대상 대입문인가"
                // 를 가리는 관문이지 "무결과 시 0행"의 진리치 자체는 아니라서 재귀
                // 술어 밖에 둔다 - 파생 테이블 등 재귀 층에는 SelectSetVariable이
                // 애초에 없으므로 이 조건을 술어 안에 넣으면 모든 재귀가 공허하게
                // 거짓이 되어 버린다.
                if (HasNonSetVariableSibling(node)) return;

                // ★ 3 회차(설계서 §12) - 진리 조건을 단일 재귀 술어로 접는다. 예전
                // 코드는 여기서 FROM 유무 · FROM 집계 · HAVING · GROUP BY · 형제 집계를
                // 조기 반환 다섯으로 각각 따로 봤고, 그중 FROM 집계 하나만
                // `node.FromClause.Accept(...)`로 하위까지 훑었다 - HAVING·GROUP BY·
                // 형제는 **이 질의 자신의 층만** 봤다. 그래서 같은 위험한 모양이 파생
                // 테이블·APPLY 안으로 한 층만 들어가면 판정에서 벗어나 다시 열렸다
                // (클래스 주석 "3 회차 검토가 세 번째로 낸 Critical"). 지금은 다섯 조건
                // 전부(FROM 유무 포함)를 <see cref="GuaranteesZeroRowsWhenSourcesAreEmpty"/>
                // 하나로 묻는다 - 그 술어가 FROM의 각 원천에 **같은 질문을 재귀로**
                // 던지므로(<see cref="GuaranteesZeroRows(TableReference)"/>), 판정이
                // "어느 층에 가드를 걸었는가"에서 자유로워진다.
                if (!GuaranteesZeroRowsWhenSourcesAreEmpty(node)) return;

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
            /// §12-2 진리 조건 - "이 질의의 기저 테이블이 모두 비면, 이 질의는 0행을
            /// 돌려준다" - 을 한 문장으로 못박은 단일 재귀 술어(설계서 §12-3). 이
            /// QuerySpecification 자신의 층에서 HAVING·GROUP BY·투영 집계를 보고,
            /// FROM의 각 원천에는 <see cref="GuaranteesZeroRows(TableReference)"/>로
            /// 같은 질문을 재귀로 던진다 - 이 재귀가 3 회차의 핵심이다. 최상위
            /// 대입문에서 호출될 때도, 파생 테이블·APPLY 안쪽 QuerySpecification에서
            /// 재귀로 호출될 때도 **같은 메서드, 같은 조건**을 쓴다 - "어느 층인가"가
            /// 판정에서 사라지는 지점이 여기다.
            ///
            /// [이 술어를 넓힐 때] 새 FROM 원천 종류를 추가로 담고 싶으면
            /// <see cref="GuaranteesZeroRows(TableReference)"/>의 switch에 그 노드
            /// 종류가 "기저 테이블이 비면 0행을 보장하는가"의 답을 더하면 된다 - 층을
            /// 가리키는 코드를 이 메서드에 새로 얹지 마라, 그러면 3 회차 이전의 결함이
            /// 그대로 되돌아온다.
            /// </summary>
            private static bool GuaranteesZeroRowsWhenSourcesAreEmpty(QuerySpecification query)
            {
                // FROM이 없으면 "무결과"라는 개념 자체가 없다 - 한 행이 반드시 돌아와
                // 확정 사실 문장이 거짓이 된다.
                if (query.FromClause == null) return false;

                // HAVING·GROUP BY는 변형을 가리지 않고 존재만 본다 - 보수적 선택이다
                // (클래스 주석 "암묵적 그룹화도 진리조건을 뚫는다" · "★ 둘째 재검토"와
                // 같은 논리를 재귀 층에도 그대로 적용한다. 파생 테이블 안쪽의 평범한
                // `GROUP BY`까지 함께 침묵시키는 것도 포함해서다 - 설계서 §12-6의
                // 마지막 "담긴다" 행이 이 자리다).
                if (query.HavingClause != null) return false;
                if (query.GroupByClause != null) return false;

                // 투영(SELECT 목록)에 집계가 있으면 GROUP BY가 없어도 T-SQL이 암묵적
                // 전체 그룹을 만들어 무결과여도 1행을 돌려준다 - HAVING·GROUP BY와
                // 같은 함정이다. 최상위 대입문에서는 형제 SelectSetVariable의 식이
                // 이 검사에 걸리고(구 코드의 HasAggregateSibling이 하던 일), 파생
                // 테이블 등 재귀 층에서는 그 층의 SelectScalarExpression이 걸린다 -
                // 두 자리가 결국 "이 QuerySpecification의 SELECT 목록"이라는 같은
                // 개념이라 이 회차가 하나로 합쳤다.
                if (ProjectionHasAggregate(query)) return false;

                // ★ 4 회차(설계서 §14) - 암묵적 그룹화를 켜는 절은 SELECT 목록·
                // HAVING뿐 아니라 ORDER BY까지 셋이다(다음 사람이 넷째를 찾을 때
                // 여기를 봐라). GROUP BY 없는 질의는 ORDER BY 안에 집계 함수가
                // 있어도 T-SQL이 전체를 암묵적 한 그룹으로 묶어 무결과여도 1행을
                // 돌려준다(`SELECT @v = 1 FROM T ORDER BY COUNT(*)`가 T가 비어도
                // 1행을 돌려주고 @v에 1이 실제로 대입된다). 그러면 "무결과 시 대입이
                // 일어나지 않는다"는 이 확정 문장이 거짓이 된다.
                //
                // HAVING·GROUP BY와 달리 여기서는 **절의 존재 자체가 아니라 집계를
                // 품을 때만** 그룹화가 켜진다 - ORDER BY는 그 자체로는 결과 집합의
                // 카디널리티를 바꾸지 않기 때문이다(정렬만 한다). 그래서
                // `OrderByClause != null`로 통짜 침묵하면 집계 없는 ORDER BY(코퍼스
                // 대장 52행 중 4건이 이 모양이다)까지 거짓으로 줄인다 - 대신 같은
                // `AggregateFunctionDetector`를 재사용해 `ProjectionHasAggregate`와
                // 대칭을 맞춘다(둘 다 "집계를 품을 때만" 침묵).
                if (OrderByHasAggregate(query)) return false;

                // FROM 층 - 쉼표로 나열한 원천은 암묵적 CROSS JOIN이므로(ScriptDom도
                // 별도 조인 노드 없이 TableReferences에 여러 항목으로 그대로 담는다.
                // 프로브로 확인 - 3 회차 보고서 PARSE SHAPES) INNER/CROSS와 같은 논리로
                // 아무 하나만 보장해도 전체가 보장된다.
                return query.FromClause.TableReferences.Any(GuaranteesZeroRows);
            }

            /// <summary>
            /// 이 질의 자신의 투영에 집계 함수가 있는가(위 술어의 한 조각).
            /// <see cref="AggregateFunctionDetector"/>를 재사용한다 - 형제
            /// <see cref="SelectSetVariable"/>이든 파생 테이블의
            /// <see cref="SelectScalarExpression"/>이든, "이 SELECT 목록의 식 하나에
            /// 집계 함수 호출이 있는가"라는 같은 질문이다.
            /// </summary>
            private static bool ProjectionHasAggregate(QuerySpecification query)
            {
                foreach (var element in query.SelectElements)
                {
                    ScalarExpression? expression = element switch
                    {
                        SelectScalarExpression scalar => scalar.Expression,
                        SelectSetVariable setVariable => setVariable.Expression,
                        _ => null
                    };
                    if (expression == null) continue;

                    var detector = new AggregateFunctionDetector();
                    expression.Accept(detector);
                    if (detector.Found) return true;
                }

                return false;
            }

            /// <summary>
            /// 이 질의 자신의 <see cref="QuerySpecification.OrderByClause"/>에 집계
            /// 함수가 있는가(위 술어의 한 조각, 설계서 §14). `ProjectionHasAggregate`와
            /// 같은 <see cref="AggregateFunctionDetector"/>를 재사용해 판정 자를
            /// 갈라지지 않게 한다 - 두 곳이 다른 목록을 쓰면 한쪽만 새는 자리가
            /// 생긴다.
            /// </summary>
            private static bool OrderByHasAggregate(QuerySpecification query)
            {
                var orderBy = query.OrderByClause;
                if (orderBy == null) return false;

                foreach (var element in orderBy.OrderByElements)
                {
                    if (element.Expression == null) continue;

                    var detector = new AggregateFunctionDetector();
                    element.Expression.Accept(detector);
                    if (detector.Found) return true;
                }

                return false;
            }

            /// <summary>
            /// FROM 층 - 원천 하나가 "기저 테이블이 비면 0행을 보장"하는가(설계서
            /// §12-3의 표를 그대로 코드로 옮긴다). 파싱 모양은 짐작하지 않고 프로브로
            /// 직접 재서 확인했다(3 회차 보고서 PARSE SHAPES) - 쉼표로 나열한 원천은
            /// 별도 조인 노드가 아니라 <see cref="FromClause.TableReferences"/>에 여러
            /// 항목으로 그대로 들어오고, <c>UnqualifiedJoinType</c>은 CrossJoin·
            /// CrossApply·OuterApply 셋뿐이며(LEFT/RIGHT APPLY라는 것은 없다),
            /// <c>QualifiedJoinType</c>은 Inner·LeftOuter·RightOuter·FullOuter 넷뿐이다.
            /// 모르는 노드 종류는 <c>default</c>로 떨어져 거짓이다 - "모르는 것은
            /// 담지 않는다"(§12-3 마지막 행). 이 스위치에 없는 종류를 새로 담고 싶은
            /// 다음 사람은 여기에 그 노드의 답만 추가하면 된다 - 호출부를 고칠 필요가
            /// 없다(재귀가 알아서 새 갈래를 태운다).
            /// </summary>
            private static bool GuaranteesZeroRows(TableReference reference)
            {
                switch (reference)
                {
                    // 이름 있는 테이블 - 기저 테이블이 비면 0행이다. 카탈로그 뷰
                    // (`sys.objects` 등)도 ScriptDom에서 이 노드와 구분되지 않는다 -
                    // 3 회차 보고서 PREDICTION SCORECARD가 이 자리의 어긋남을 그대로
                    // 적는다(§12-6은 카탈로그 뷰를 "침묵"으로 예측했지만 이 술어는
                    // "이름 있는 테이블"을 노드 종류로만 판정하므로 구분하지 못한다).
                    case NamedTableReference:
                        return true;

                    // 파생 테이블 - 안쪽 질의가 같은 질문에 재귀로 답할 때만.
                    case QueryDerivedTable derived:
                        return GuaranteesZeroRows(derived.QueryExpression);

                    // 괄호로 감싼 조인 - 감쌈을 벗기고 안쪽 조인에 그대로 묻는다.
                    case JoinParenthesisTableReference paren:
                        return GuaranteesZeroRows(paren.Join);

                    case QualifiedJoin qualified:
                        return qualified.QualifiedJoinType switch
                        {
                            // INNER - 공집합 × 무엇 = 공집합이므로 한쪽만 보장해도 된다.
                            QualifiedJoinType.Inner =>
                                GuaranteesZeroRows(qualified.FirstTableReference)
                                || GuaranteesZeroRows(qualified.SecondTableReference),
                            // LEFT - 결과 행 수가 왼쪽 행 수 이상이므로 왼쪽이 보장할
                            // 때만(설계서 §12-4 - 검토와 갈리는 판단. 오른쪽이 비어도
                            // 왼쪽에 행이 있으면 NULL이 채워진 1행이 돌아오지만, 확정
                            // 문장은 "무결과**면**"이라는 조건문이라 왼쪽에 행이 있는
                            // 경우는 애초에 전건이 거짓이다).
                            QualifiedJoinType.LeftOuter => GuaranteesZeroRows(qualified.FirstTableReference),
                            QualifiedJoinType.RightOuter => GuaranteesZeroRows(qualified.SecondTableReference),
                            // FULL - 양쪽 다 보장해야 결과도 보장된다.
                            QualifiedJoinType.FullOuter =>
                                GuaranteesZeroRows(qualified.FirstTableReference)
                                && GuaranteesZeroRows(qualified.SecondTableReference),
                            _ => false
                        };

                    case UnqualifiedJoin unqualified:
                        return unqualified.UnqualifiedJoinType switch
                        {
                            // CROSS JOIN은 INNER와 같은 논리(한쪽만 보장해도 됨).
                            UnqualifiedJoinType.CrossJoin =>
                                GuaranteesZeroRows(unqualified.FirstTableReference)
                                || GuaranteesZeroRows(unqualified.SecondTableReference),
                            // CROSS/OUTER APPLY - 오른쪽은 왼쪽의 매 행마다 평가되므로
                            // 왼쪽이 비면 오른쪽 내용과 무관하게 결과가 비고, 왼쪽이
                            // 안 비면 오른쪽만 보고는 결과를 알 수 없다. 그래서 왼쪽만
                            // 본다 - 오른쪽 파생 테이블은 재귀하지 않는다(§12-3
                            // "CROSS/OUTER APPLY | 왼쪽이 보장할 때만").
                            UnqualifiedJoinType.CrossApply => GuaranteesZeroRows(unqualified.FirstTableReference),
                            UnqualifiedJoinType.OuterApply => GuaranteesZeroRows(unqualified.FirstTableReference),
                            _ => false
                        };

                    // VALUES(InlineDerivedTable) · TVF(SchemaObjectFunctionTableReference) ·
                    // PIVOT/UNPIVOT · 테이블 변수(VariableTableReference) · 그 밖의
                    // 알지 못하는 노드 - 전부 "아니오"(§12-3). VALUES는 기저 테이블과
                    // 무관하게 행을 만들고, TVF·테이블 변수는 비었는지 알 수 없으며,
                    // PIVOT/UNPIVOT은 보수적으로 막는다.
                    default:
                        return false;
                }
            }

            /// <summary>
            /// 파생 테이블 안쪽 질의에 같은 질문을 던진다.
            /// <see cref="QuerySpecification"/>이면 <see cref="GuaranteesZeroRowsWhenSourcesAreEmpty"/>
            /// 로 재귀하고, <c>UNION</c>/<c>UNION ALL</c>(<see cref="BinaryQueryExpression"/>
            /// 이고 <see cref="BinaryQueryExpressionType.Union"/>)이면 모든 갈래가
            /// 보장할 때만 참이다(설계서 §12-3 "파생 테이블 안의 UNION/UNION ALL은
            /// 모든 갈래가 보장할 때만 참"), 괄호는 벗긴다. `EXCEPT`/`INTERSECT`는
            /// 설계서의 사전 예측표에 없는 모양이라 다루지 않는다 - "모르는 것은
            /// 담지 않는다"로 떨어진다(보수적 - 실제로는 두 갈래가 다 보장해도
            /// 참이지만, 이 회차의 범위가 아니라 넓히지 않는다).
            /// </summary>
            private static bool GuaranteesZeroRows(QueryExpression? expression)
            {
                switch (expression)
                {
                    case QuerySpecification spec:
                        return GuaranteesZeroRowsWhenSourcesAreEmpty(spec);

                    case BinaryQueryExpression binary
                        when binary.BinaryQueryExpressionType == BinaryQueryExpressionType.Union:
                        return GuaranteesZeroRows(binary.FirstQueryExpression)
                               && GuaranteesZeroRows(binary.SecondQueryExpression);

                    case QueryParenthesisExpression paren:
                        return GuaranteesZeroRows(paren.QueryExpression);

                    default:
                        return false;
                }
            }
        }
    }
}
