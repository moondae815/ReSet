using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Operation">
    /// "INSERT", "UPDATE", "DELETE", "SELECT" 중 하나.
    ///
    /// ["SELECT"는 무엇인가 - 2026-08-22 축 A 재감사 ③ Task 4] DML 밖의 독립
    /// SELECT 문장 중 <b>FROM이 있는 것</b>이다(판정은 DmlScopeExtractor.HasFromClause
    /// 하나 - 그 문서 참고). 변수 대입 SELECT·커서 원천 질의·함수 본문 SELECT가
    /// 여기 해당하고, `SELECT @a = 1`처럼 훑을 자리가 없는 문장은 번호조차 소비하지
    /// 않는다. `INSERT ... SELECT`의 원천은 문장 노드가 아니라 QueryExpression이라
    /// 이 종류로 다시 실리지 않는다(중복 없음).
    ///
    /// 이 종류를 담게 된 이유는 커서 원천 질의의 ORDER BY·GROUP BY다 - 처리 순서가
    /// 결과를 가르는데(PROC_ETC:62) 그것을 담을 자리가 이 표의 기존 칸이었다.
    ///
    /// [이 표에 `IF`는 없다 - 잠금 힌트 표와 다른 점] 잠금 힌트 표는 `IF` 술어 안의
    /// 스캔을 `IF n`으로 담지만(LockHintVisitor.ExplicitVisit(IfStatement)), 이 표는
    /// 담지 않는다. 스펙 §4 D는 두 표를 함께 적었으나 계획서가 이 태스크를 SELECT로
    /// 좁혔다 - 실측된 결함 11건 중 `IF` 술어 질의의 <b>대상 범위</b>(술어 컬럼·조인
    /// 키·정렬)가 산문에만 있던 사례가 없었기 때문이다. 두 표의 문장 집합이 다른 것
    /// 자체는 이 재료가 원래 허용하는 모양이고(WHERE 없는 UPDATE가 집합 술어 표에
    /// 없는 것과 같다), 번호는 종류별로 세므로 한쪽에 `IF n`이 있어도 다른 종류의
    /// 번호는 밀리지 않는다. 나중에 그 부류가 실측되면 DmlScopeVisitor에 IfStatement
    /// 오버라이드를 더하되 판정은 LockHintVisitor와 같은 것을 쓰면 된다.
    /// </param>
    /// <param name="Line">원본 DDL에서의 줄 번호(1부터) - 해당 문장 자체의 시작 줄이다.</param>
    /// <param name="Target">
    /// 갱신·삭제 대상의 원문 표기 (파서가 정규화하지 않은 소스 그대로).
    ///
    /// 독립 SELECT 행에서는 <b>빈 문자열</b>이다 - 갱신 대상이라는 것이 없기 때문이다.
    /// 이 레코드는 표시 문자열을 담지 않으므로(OrderByExpressions가 "—"와 "(없음)"을
    /// 가르지 않는 것과 같은 분업) 빈 칸을 "—"로 낼지는 렌더러가 정한다.
    /// </param>
    /// <param name="PredicateColumns">
    /// WHERE 최상위가 거르는 컬럼 이름. INSERT는 원천 SELECT의 최상위 WHERE를 본다.
    /// </param>
    /// <param name="DateParameterApplied">
    /// 기준일 파라미터가 <b>대상 범위에</b> 적용되는가. 서브쿼리 안에만 있으면 false다.
    /// 이 칸 하나가 A1 결함 넷 중 셋을 드러낸다.
    ///
    /// 독립 SELECT 행에서는 <b>항상 false</b>다 - 이 칸이 묻는 것은 "갱신 대상 범위가
    /// 기준일로 좁혀지는가"인데 독립 SELECT에는 갱신 대상이 없다. 그 문장의 WHERE가
    /// 기준일을 쓰더라도 false이므로, 이 칸을 독립 SELECT 행에서 "기준일을 쓰지
    /// 않는다"로 읽어서는 안 된다 - 판정 자체가 없었다는 뜻이다. 그 구분을 "—"로
    /// 낼지는 렌더러가 정한다(Target과 같은 분업). 그 갈래는 2026-08-22 축 A 재감사 ③
    /// Task 7이 넣었다 - AiService는 독립 SELECT 행에 "—"를 낸다
    /// (BuildDmlScopeTableLines의 <c>isStandaloneSelect ? "—"</c>). 이 문단은 그때까지
    /// "아직 그 갈래가 없어 SELECT 행에도 '아니오'를 낼 것이다"라고 적혀 있었다.
    /// </param>
    /// <param name="JoinKeys">
    /// 테이블을 잇는 컬럼 이름. ANSI JOIN의 ON 조건과, 콤마로 나열한 옛 스타일
    /// 조인(FROM A, B WHERE A.X = B.Y)의 컬럼=컬럼 동등비교를 모두 담는다 -
    /// 후자는 PredicateColumns와 겹칠 수 있다(같은 WHERE 텍스트가 필터와 조인
    /// 역할을 동시에 하기 때문).
    /// </param>
    /// <param name="OrderByExpressions">
    /// 문장의 최상위 ORDER BY 요소 원문(정렬 방향 포함). 두 종류에서 채워진다 -
    /// INSERT ... SELECT 의 원천, 그리고 독립 SELECT(커서 원천 질의가 그 실물이다).
    /// UPDATE·DELETE는 최상위 ORDER BY가 문법상 불가하므로 항상 빈 목록이고 표에서
    /// "—"로 렌더된다.
    ///
    /// [독립 SELECT가 이 칸을 쓰는 이유 - 2026-08-22 축 A 재감사 ③ Task 4]
    /// PROC_ETC:62의 `DECLARE Cur_SettlePost CURSOR FOR SELECT ... ORDER BY A.OutYMD,
    /// A.ClientID`가 실물이다. 커서가 도는 순서가 MAX(ID)+1 채번 결과와 -3 중단
    /// 지점을 가르는데 그 ORDER BY가 문서 전체에 없었다. 새 표를 만들지 않고 이 칸을
    /// 그대로 쓴다 - 그래서 "이 필드는 INSERT 전용"이라고 읽으면 안 된다.
    ///
    /// [이름이 Columns가 아니라 Expressions인 이유 - 수정 라운드 1, 조정자 판정]
    /// ORDER BY는 컬럼 이름뿐 아니라 임의 식(`CASE WHEN ... END`, `LEN(A)`)과 정렬
    /// 방향(`DESC`/`ASC`)을 받는다. Column이라는 이름은 "단순 식별자만 담는다"는
    /// 기대를 주는데 그 기대가 실제로는 깨진다 - PredicateColumns·JoinKeys는 그
    /// 기대가 성립하는 자리(항상 단순 식별자)라 이름이 정확하지만, 여기는 처음부터
    /// 그렇지 않았다. 이 사실을 아직 아무도 소비하지 않는 지금(AiService·L1 배선은
    /// 별도 과제) 이름을 고치는 비용이 가장 싸므로 지금 고친다.
    ///
    /// [존재 여부가 아니라 목록인 이유 - 2026-08-21 축 A 감사]
    /// STAT_PGCOLLECT_INS:113의 `ORDER BY INYMD, CLIENTID, PGNAME, MALLID`가 문서
    /// 어디에도 없었다. 불리언으로 담으면 "있다"만 알고 무엇으로 정렬하는지는 여전히
    /// 모른다. 목록을 담는 비용이 같으므로 더 충실한 쪽을 택한다.
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
    /// <param name="GroupByColumns">
    /// 문장의 GROUP BY 키. 없으면 빈 목록.
    ///
    /// 매핑 표의 설명 칸이 유일한 GROUP BY 기록처였고, 한 SP에서 세 문장의 첫 키가
    /// 통째로 빠진 실측이 있다(UP_Util_Settle_Summary·UP_Util_Settle_Summary_AcqManual).
    /// 기계 확정 열로 올려 그 자리를 산문에 맡기지 않는다.
    ///
    /// [ORDER BY와 같은 "—"/"(없음)" 규약을 쓰는 이유 - Task 8, 제약 3]
    /// GROUP BY도 ORDER BY와 마찬가지로 UPDATE·DELETE의 최상위 절로는 문법상 불가능하고
    /// (T-SQL은 UPDATE·DELETE에 GROUP BY를 허용하지 않는다), 질의를 여는 문장 -
    /// INSERT ... SELECT의 원천과 독립 SELECT - 에서만 나타날 수 있다. 그래서 렌더
    /// 규약은 UPDATE·DELETE 행에 "—"(문법상 불가), INSERT·SELECT 행의 빈 목록에
    /// "(없음)"(절 부재)이다 - ORDER BY 칸이 이미 세운 구분을 그대로 재사용한다.
    /// 이 레코드 자신은 둘을 구분하지 않는다(OrderByExpressions가 그렇듯 항상 빈
    /// 목록으로 담고, "—"인지 "(없음)"인지는 렌더 시점에 Operation으로 가른다).
    ///
    /// [렌더러는 아직 SELECT 행을 가르지 않는다 - Task 4 시점의 사실]
    /// AiService.BuildDmlScopeTableLines의 GROUP BY·ORDER BY 칸은 오늘
    /// `Operation == "INSERT"`만 보고 나머지를 전부 "—"로 낸다. 그래서 이 재료가
    /// 담기 시작한 독립 SELECT의 ORDER BY·GROUP BY는 표에서 아직 보이지 않는다 -
    /// 그 갈래를 넓히는 것은 이 배치의 Task 7(렌더러·L1)이다. 추출기와 렌더러를
    /// 한 커밋에 묶지 않는 것이 계획서의 배분이고, 이 문단은 그 사이의 어긋남을
    /// 다음 사람이 결함으로 오인하지 않게 적어 둔다.
    ///
    /// [파생 테이블 안의 GROUP BY가 새지 않는 이유 - Task 8, 제약 6]
    /// INSERT ... SELECT ... FROM (SELECT ... GROUP BY ...) X처럼 GROUP BY가 파생
    /// 테이블 안에 있으면 바깥 문장 자신의 GROUP BY가 아니다. DmlScopeVisitor는
    /// QuerySpecificationsOf(select.Select)로 얻은 각 QuerySpecification의
    /// GroupByClause만 직접 읽는다 - 이 헬퍼는 UNION·괄호만 펼치고 FROM 절 안의
    /// 파생 테이블로는 내려가지 않으므로(SourceQuerySpecifications 문서 참고),
    /// 파생 테이블의 GROUP BY는 애초에 이 순회에 잡히지 않는다. 이 배치의 Task 4가
    /// 정확히 같은 부류(GROUP BY 귀속)에서 결함이 났었다.
    ///
    /// [UNION 갈래마다 GROUP BY가 다르면 비우는 이유 - Task 8, 제약 7]
    /// INSERT 원천이 UNION일 때 갈래마다 WHERE·JOIN 키는 합쳐 담지만(교집합이 아니라
    /// 합집합 - 각 갈래가 실제로 지는 조건이므로 합쳐도 거짓이 되지 않는다), GROUP BY는
    /// 그렇게 할 수 없다. "이 INSERT 문의 GROUP BY"는 갈래마다 다른 값일 수 없는
    /// 단일 사실이어야 하는데, 갈래마다 실제로 다르면 그 단일 답 자체가 없다 -
    /// 억지로 합치면(합집합이든 첫 갈래든) 어느 갈래도 쓰지 않는 조합이나 다른
    /// 갈래의 그룹화 의미를 사실인 것처럼 단언하게 된다. 그래서 모든 갈래가 완전히
    /// 같은 GROUP BY 키 목록을 가질 때만 그 값을 싣고, 하나라도 다르면(갈래 중
    /// 하나만 GROUP BY가 있는 경우 포함) 빈 목록으로 둔다 - 과소 포착(빈 칸)은
    /// Minor, 거짓 행은 Critical이라는 판단 기준을 그대로 따른다.
    /// </param>
    public sealed record DmlScopeFact(
        string Operation,
        int Line,
        string Target,
        IReadOnlyList<string> PredicateColumns,
        bool DateParameterApplied,
        IReadOnlyList<string> JoinKeys,
        IReadOnlyList<string> OrderByExpressions,
        IReadOnlyList<string>? GroupByColumns = null)
    {
        /// <summary>기본값을 null이 아니라 빈 목록으로 정규화한다 - 기존 생성 자리가
        /// 이 파라미터를 생략해도 소비자는 항상 비-null 목록을 본다.</summary>
        public IReadOnlyList<string> GroupByColumns { get; init; } = GroupByColumns ?? Array.Empty<string>();
    }

    /// <param name="Operation">
    /// "INSERT", "UPDATE", "DELETE", "SELECT" 중 하나.
    ///
    /// ["SELECT"는 무엇인가 - 2026-08-23 축 A ③(b) Task 2] DML 밖의 독립 SELECT 문장 중
    /// <b>FROM이 있는 것</b>이다 - 판정도 번호도 DmlScopeFact.Operation의 "SELECT"와 같다
    /// (DmlScopeExtractor.HasFromClause 하나가 유일한 출처). 커서 원천 질의가 그 실물이고
    /// (UP_Util_Settle_Summary_AcqManual:29-36의 `B.AcqType = 1`·`A.OutState IN (2,9)`),
    /// `INSERT ... SELECT`의 원천은 문장 노드가 아니라 QueryExpression이라 `INSERT n`
    /// 행과 겹쳐 실리지 않는다.
    ///
    /// [이 표에도 `IF`는 없다] DML 범위 표와 같다 - 잠금 힌트 표·참조 함수 표만 `IF n`을
    /// 담는다(DmlScopeFact.Operation 문서의 같은 문단 참고).
    /// </param>
    /// <param name="Line">
    /// 원본 DDL에서 <b>이 술어 항 자신</b>이 시작하는 줄 번호(1부터) - 문장의 시작줄이
    /// 아니다.
    ///
    /// [문장 줄에서 항의 줄로 내린 이유 - 2026-08-22 축 A 재감사 ③ Task 5, 설계 §4 C]
    /// 예전엔 문장 조각의 StartLine을 그대로 실었다. 그래서 한 문장의 술어가 전부 같은
    /// 줄로 찍혔다 - UP_UTIL_SETTLE_EXCEPTION_PROC의 UPDATE 7은 WHERE가 여러 줄에 걸쳐
    /// 있는데 그 술어 행이 모두 210이었고, "문장" 칸이 이미 주는 정보를 되풀이할 뿐
    /// 독자가 어느 항인지 원문에서 찾을 수 없었다. LockHintFact.Line이 같은 이유로 이미
    /// 참조 노드 자신의 줄을 쓴다(수정 라운드 1 실물 검증) - 그 선례를 따른다.
    ///
    /// [L1의 행 키에 미치는 영향] MechanicalValidator.CheckSetPredicates가
    /// (Operation, Line, Column, Scope)로 행을 묶는다. 라인이 항마다 갈리므로 키는
    /// 더 유일해진다 - 같은 문장의 서로 다른 컬럼이 이제 라인부터 다르다.
    /// </param>
    /// <param name="Column">
    /// IN 좌변의 원문 표기 그대로 - 한정자가 있으면 한정자를 포함한다(`A.USESTATE`),
    /// 없으면 컬럼 이름만이다(`UseState`).
    ///
    /// [분해되지 않는 항은 `—`다 - 2026-08-22 축 A 재감사 ③ Task 6, 설계 §3 결정 3]
    /// 행 단위가 최상위 AND 항이고, 항이 분해되는 것은 우변이 전부 리터럴일 때뿐이다.
    /// OR 결합 · 컬럼 대 컬럼(`A.YMD = A.AYMD`) · 산술식 우변 · 화이트리스트 밖
    /// 연산자(`&gt;=`)는 분해되지 않으므로 이 칸과 <paramref name="Operator"/>가
    /// 전각 대시 `—`이고 <paramref name="Literals"/>가 빈 목록이다. 그 항은
    /// <paramref name="PredicateText"/> 하나로 표에 자리를 얻는다 - 예전에는 이런 항이
    /// 사실 자체를 내지 않아 어떤 표에도 나타나지 않았다.
    ///
    /// 이 세 칸은 항상 함께 움직인다: 분해되면 셋 다 차고, 안 되면 셋 다 비운다.
    /// 반쪽 분해(예: 컬럼만 채우고 리터럴은 빈 목록)는 표가 거짓 집합을 단언하게
    /// 만들므로 내지 않는다.
    ///
    /// 마지막 식별자 조각만 담지 않는 이유는
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
    /// InsertSpecification)과 ExplicitVisit(SelectStatement) 오버라이드 안에서
    /// (Collect 안이 아니라) 연산별 카운터를 증가시켜 채운다.
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
    /// SetPredicateVisitor와 DmlScopeVisitor는 같은 파싱 트리를 같은 네 오버라이드로,
    /// 같은 순서로 방문한다(DML 셋은 `Visit`, 독립 SELECT는
    /// `ExplicitVisit(SelectStatement)`이고 그 판정은 HasFromClause 하나를 공유한다 -
    /// 넷째는 2026-08-23 축 A ③(b) Task 2가 더했다). 두 방문자가 각자 독립적으로(서로를
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
    /// <param name="PredicateText">
    /// 이 사실을 낸 술어 항의 원문을 한 줄로 접어 담는다
    /// (`A.PGNAME IN ('KFTC', 'YELOPAY')`).
    ///
    /// [왜 분해와 원문을 함께 싣는가 - 2026-08-22 축 A 재감사 ③ Task 5, 설계 §4 C]
    /// 컬럼·연산·리터럴 칸은 분해된 결과라서, 분해가 담지 못한 것은 표에서 흔적도
    /// 없이 사라진다. UP_UTIL_SETTLE_COMM_UPD:78의
    /// `(A.UseState &lt;&gt; 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))`이 실측 사례다 -
    /// 분해된 두 행만 나란히 실려 AND로 읽히고, 그렇게 읽으면 모순(공집합)이다.
    /// 원문 칸이 있으면 독자가 분해를 원문과 대조할 수 있고, 분해되지 않는 항도
    /// 이 칸 하나로 표에 자리를 얻는다.
    ///
    /// 공백을 접는 이유는 좌변(Column)이 이미 접히는 이유와 같다 - 같은 표의 두 칸이
    /// 같은 원문 규칙을 따라야 표기가 갈리지 않는다.
    ///
    /// [무엇이 접기를 실제로 요구하는가 - 2026-08-22 코퍼스 전수 프로브]
    /// 개행이 아니라 <b>줄 안의 공백</b>이다. ScriptDom으로 코퍼스 31개 파일을 전수
    /// 조사했다. 수집 판정(좌변에 컬럼·우변이 리터럴)을 통과하는 항이 373건인데 -
    /// 방문 범위를 실제보다 넓게(트리 전체) 잡은 상위집합이라 진짜 수집분은 이보다
    /// 적다 - 그중 개행을 포함한 항은 <b>0건</b>이다.
    /// 여러 줄에 걸친 IN은 코퍼스에 두 자리뿐인데(COMM_UPD:141, EXCEPTION_PROC:527)
    /// 둘 다 서브쿼리 IN이라 RecordSetPredicate가 애초에 담지 않는 형태다. 반면 줄
    /// 안의 공백은 실재한다 - COMM_UPD:77의 `A.PGNAME     IN (...)`은 한정자와 IN
    /// 사이가 다섯 칸이고, 코퍼스 최대는 AcqManual:34의 열네 칸
    /// (`B.AcqType              = 1`)이다. 접지 않으면 원문 칸의 표기가 원본의 정렬
    /// 공백에 좌우된다.
    ///
    /// 개행 쪽 접기는 지금은 방어적이다 - 코퍼스에 아직 그 형태가 없다. 다만 규칙
    /// 자체는 CollapseWhitespace 문서가 적은 기전 그대로다: EscapeTableCell은 개행만
    /// 공백으로 접고 공백 연속은 건드리지 않으므로(MarkdownTableCellCodec.Escape
    /// 확인함), 개행이 든 값을 그대로 실으면 렌더된 칸과 접지 않은 원문이 어긋난다.
    ///
    /// [기본값이 빈 문자열인 이유] 이 재료를 손으로 조립하는 기존 테스트가 인자를
    /// 생략해도 깨지지 않게 한다. 추출기 경로는 언제나 값을 채운다.
    /// </param>
    public sealed record SetPredicateFact(
        string Operation,
        int Line,
        string Column,
        bool IsNegated,
        IReadOnlyList<string> Literals,
        int StatementOrdinal = 0,
        string Operator = "IN",
        string Scope = "최상위",
        string PredicateText = "");

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
    /// 이 호출을 담은 문장의 연산(UPDATE/INSERT/DELETE/SELECT/IF).
    ///
    /// [독립 SELECT와 `IF` 술어도 담는다 - 2026-08-23 축 A ③(b) Task 1]
    /// 이 자리는 "독립 SELECT 문의 호출은 담지 않는다"고 적혀 있었다. 그 경계가 🔴을
    /// 냈다 - `UF_GET_COLLECTYMD`가 `UF_GET_WORKDAY2`를 부르는 두 자리(53·78행)가
    /// 모두 변수 대입 SELECT의 SELECT 목록 안 `CASE` 식이라 수집이 0건이었고,
    /// 수집이 0건이면 렌더러가 참조 함수 표를 통째로 빼므로 링크도 없어졌다.
    /// 링크가 없으니 모델이 함수 동작을 산문으로 요약했고 그 요약에서 간격 0 특례가
    /// 빠졌다. 지금은 세 DML에 더해 FROM이 있는 독립 SELECT와 `IF` 술어를 방문한다.
    ///
    /// [표들의 문장 집합은 서로 다르다 - 2026-08-23 축 A ③(b) Task 1 기준]
    /// 이 자리는 한때 "세 표가 같은 문장 집합을 같은 번호로 가리켜야 나란히 읽을 수
    /// 있다"고 적혀 있었다. 2026-08-22 축 A 재감사 ③ Task 8이 그 대칭을 의도적으로
    /// 깼고, <b>이 표가 독립 SELECT와 `IF` 술어를 담게 된 것은 오늘(축 A ③(b) Task 1)
    /// 이다.</b> 지금 갈리는 자리는 집합 술어 표 하나다 - 그 표만 세 DML 문장을
    /// 방문하고, 이 표와 DML 범위 표와 잠금 힌트 표는 독립 SELECT까지 방문한다
    /// (`IF n` 행은 이 표와 잠금 힌트 표에 있다). 표를 나란히 읽을 때 `SELECT 1` 행이
    /// 한쪽에만 있다고 해서 다른 쪽이 빠뜨린 것이 아니다.
    ///
    /// 다만 `IF n`은 두 표를 가로질러 대조할 수 없다 - 채번 조건이 서로 다르다
    /// (LockHintVisitor.ExplicitVisit(IfStatement) 문서의 마지막 문단).
    /// </param>
    /// <param name="StatementOrdinal">
    /// 연산 종류별 · 1부터인 문장 번호. 채번이 연산 이름별로 독립이라, UPDATE·DELETE·
    /// INSERT 번호는 네 표(이 표 · 집합 술어 · DML 범위 · 잠금 힌트)에서 여전히 같은
    /// 문장을 가리킨다 - `SELECT n`·`IF n` 행이 늘어도 DML 카운터는 밀리지 않는다
    /// (SetPredicateFact.StatementOrdinal 문서 참고).
    /// 위 Operation 문서가
    /// 적은 대로 문장 집합 자체는 표마다 다르다.
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
    /// "최상위" · "파생" · "하위 질의". SetPredicateFact.Scope와 같은 선례다 - 최상위
    /// FROM에 직접 실리지 않은 참조를 빼지 않고 표시해서 싣는다(수정 라운드 2, 아래
    /// ExtractLockHints 문서의 실측 근거 참고).
    ///
    /// "최상위"는 그 문장(또는 그 IF 술어)이 직접 훑는 자리를 뜻한다. 술어 안에서
    /// 다시 열린 질의가 훑는 자리는 문장 종류를 가리지 않고 "하위 질의"다
    /// (2026-08-22 축 A 재감사 - LockHintVisitor.SubqueryScope 문서에 경계를 적었다).
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
    /// [어떤 문장을 방문하는가] Insert/Update/DeleteSpecification과, FROM이 있는
    /// 독립 SelectStatement 넷이다(DmlScopeFact.Operation 문서 참고 - `IF`는 담지
    /// 않는다).
    ///
    /// [MERGE·CTE 기반 UPDATE] 실측 SP 24건 어디에도 MERGE와 CTE 기반 UPDATE가
    /// 없었다(전수 grep 확인). 이 방문자는 위 네 종류만 방문하므로 MergeStatement는
    /// 애초에 매칭되지 않아 조용히 빠진다 - 예외를 던지지 않고 그 문장 하나가 표에
    /// 실리지 않을 뿐이다. WITH 절이 있는 CTE는
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
        /// DML 문장에서 대상 행을 가르는 술어를 <b>항 단위로</b> 뽑는다.
        ///
        /// 사실 하나가 최상위 WHERE의 `AND` 항 하나이고, 파생 테이블 내부 WHERE의
        /// 항도 함께 담아 <c>SetPredicateFact.Scope</c>로 가른다. 항 중에서 우변이
        /// 전부 리터럴인 `IN`/`NOT IN`(DecomposeIn)과 `=`/`&lt;&gt;`(DecomposeComparison)만
        /// 컬럼·연산자·리터럴 목록으로 분해되고, 나머지 항 - 우변이 파라미터인 비교,
        /// 컬럼 대 컬럼, 서브쿼리 `IN`, `OR` 결합, 부등식 - 은 분해 없이
        /// <c>SetPredicateFact.PredicateText</c> 하나로 자리를 얻는다(그 문서의
        /// "분해되지 않는 항은 `—`다" 참고). 실측 코퍼스에서는 분해 불가 쪽이 다수다 -
        /// 재생성된 EXCEPTION_PROC 표에서 102행 중 72행이고, 그중 34행이 컬럼 대 컬럼,
        /// 21행이 `= @기준일 파라미터`, 6행이 서브쿼리 `IN`이다.
        ///
        /// [옛 요약이 왜 틀렸나 - 전체 브랜치 리뷰 M1] 이 자리는 "DML 최상위 WHERE의
        /// IN/NOT IN 리터럴 목록을 뽑는다"였다. 감사가 수집 범위를 세 번 넓히는 동안
        /// 고쳐지지 않아, 소비자가 가장 먼저 읽는 줄이 실제보다 훨씬 좁은 계약을
        /// 약속하고 있었다(`docs/architecture.md`의 「명세서 충실도의 기계 확정 재료」
        /// 항목이 세 회차를 모두 기록한다).
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
        /// 문장이 읽는 자리와 그 잠금 힌트를 뽑는다.
        ///
        /// [문장 집합이 DML을 넘어섰다 - 2026-08-22 축 A 재감사 ③ Task 8에서 고쳐 적는다]
        /// 이 요약은 오래도록 "DML 문장이 읽는 자리"라고 적혀 있었지만, 같은 재감사가
        /// 그 범위를 넓혔다. 이 방문자가 지금 받는 문장은 다섯이다 - INSERT · UPDATE ·
        /// DELETE에 더해 DML 밖의 독립 SELECT(Task 1)와 `IF` 술어(Task 2)다. 잠금은
        /// DML만의 성질이 아니라 스캔의 성질이므로, 판단 근거로만 읽는 자리(`IF
        /// EXISTS(...)`)와 커서 원천·변수 대입 SELECT도 같은 표에 실려야 대상 행을
        /// 가르는 잠금 동작이 문서에서 새지 않는다. 문장 칸은 그래서 `SELECT n`·`IF n`도
        /// 진다. 짝이 되는 「DML 범위」 표는 이 다섯 중 IfStatement를 담지 않는다 -
        /// 두 표의 문장 집합이 갈리는 유일한 지점이고, 근거는 DmlScopeFact.Operation과
        /// DmlScopeVisitor.ExplicitVisit(SelectStatement) 문서에 있다.
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
        /// "파생"/"하위 질의"를 표시해서 싣는다 - 술어 안에서 다시 열린 질의가 훑는
        /// 자리가 "하위 질의"다(2026-08-22 축 A 재감사. 경계는 LockHintVisitor의
        /// SubqueryScope 문서에 적었다).
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

            /// <summary>
            /// 그 문장이 직접 훑는 자리가 아니라, 그 안에서 다시 열린 질의가 훑는 자리.
            /// 술어 안이 흔하지만 거기에 한정되지 않는다 - 파생 테이블의 SELECT 목록에
            /// 걸린 스칼라 하위 질의도 `ScopeOf`가 이 범위로 표시한다.
            ///
            /// [무엇을 "최상위"라 부르는지의 경계 - 2026-08-22 축 A 재감사]
            /// 한 스캔 자리가 `최상위`라는 말은 "그 문장(또는 그 IF 술어)이 직접 훑는
            /// 자리"라는 뜻이다. 그 자리 안에서 다시 열리는 질의는 문장 종류와
            /// 무관하게 전부 이 범위로 간다 - `UPDATE ... WHERE x IN (SELECT ...)`의
            /// 하위 질의와 `IF EXISTS(SELECT ... WHERE x IN (SELECT ...))`의 안쪽
            /// 하위 질의는 원문에서 같은 모양이므로 표에서도 같은 범위여야 한다.
            /// 갈라 놓으면 독자가 `최상위`를 문장 종류마다 다르게 읽어야 한다.
            ///
            /// 술어는 WHERE만이 아니다. `JOIN ... ON` 안의 하위 질의도 그 문장이 직접
            /// 훑는 자리가 아니므로 이 범위다(수정 라운드 1 리뷰 실측 - 그 자리가
            /// `최상위`로 실리고 있었다).
            ///
            /// [아직 표에 오지 않는 자리 - 유예된 것이지 설계로 뺀 것이 아니다]
            /// 수집 경로가 훑는 것은 DML·IF의 술어와 FROM 절뿐이다. 그 둘 어디에도
            /// 걸리지 않는 자리에서 열리는 하위 질의는 아래 세 모양 모두 표에 오지
            /// 않는다(코퍼스 24개 객체의 하위 질의 개시점 33곳을 전수 분류해 확인했다.
            /// 2026-08-22 수정 라운드 3).
            ///
            /// 1. 문장의 SELECT 목록 안 하위 질의. 실물 2건. 그중
            ///    UF_Get_CLComm4MobileCo:31-32가 **표가 실제로 힌트를 잃는 유일한 자리**
            ///    다 - `ELSE (SELECT CommissionRate FROM TClientCMRate WITH(NOLOCK) ...)`.
            ///    같은 문장의 37행 FROM(TClientSettleRate4MobileCo)은 `SELECT 1 · 최상위`
            ///    로 실리는데 32행의 NOLOCK은 표 어디에도 나타나지 않는다 - 이 축이
            ///    없애려는 "보이지 않는 NOLOCK"의 실물이다. 나머지 1건
            ///    (INS_EXTRA4PLCARD:162)은 TVF 호출이라 잃는 힌트가 없다(아래).
            /// 2. DML의 `SET` 절 하위 질의. 계획서가 이번 범위에서 뺐다. **실물 6건이
            ///    있다** - UP_UTIL_SETTLE_EXPECT_PROC:139·160·184·204·246과
            ///    UP_UTIL_SETTLE_INS_EXTRA:213, 전부
            ///    `OutYMD = (SELECT OutYMD FROM dbo.UIF_SettleYMD(A.YMD, C.SettlePeriodID))`
            ///    모양이다. 그런데 원천이 전부 TVF 호출이라 `FromTableCollector`가 보는
            ///    `NamedTableReference`가 아니고(`SchemaObjectFunctionTableReference`다)
            ///    힌트도 지고 있지 않다 - 경로가 훑더라도 새로 실릴 행은 0건이다.
            /// 3. 독립 `SELECT n`의 WHERE 하위 질의. 그 경로는 FROM만 훑기 때문이다
            ///    (`IF n`은 술어 전체를 훑으므로 해당 없다). 실물 0건 - 위 33곳 중
            ///    술어 안 하위 질의는 전부 DML의 WHERE이거나 IF 술어였다.
            ///
            /// 즉 이 세 모양이 "코퍼스에 없다"는 것은 거짓이고, 참인 것은 "이 세 모양
            /// 때문에 표가 잃는 힌트는 UF_Get_CLComm4MobileCo:32 하나뿐"이다. 유예
            /// 판단은 후자에 기대고 있다.
            ///
            /// 셋은 한 조각으로 닫아야 한다. 하나만 고치면 비대칭이 없어지는 게 아니라
            /// 자리만 옮긴다. CTE 본문(`WITH cte AS (SELECT ... WITH(NOLOCK))`)도 어느
            /// 경로도 훑지 않는다 - 이쪽은 실물 0건이다(같은 라운드에 grep으로 확인).
            ///
            /// 이 자리들은 "틀리게 실리는" 것이 아니라 "실리지 않는" 것이라 이 라벨의
            /// 뜻은 그대로 참이다. 그래도 이 표를 "그 문장이 하는 모든 스캔"으로 읽으면
            /// 안 된다.
            ///
            /// [파생과 겹치면 이쪽이 이긴다] 두 표시 모두 "최상위가 아니다"를 말하는데,
            /// 겹치는 자리가 양쪽 중첩 순서로 다 생긴다(파생 안의 하위 질의, 하위 질의
            /// 안의 파생). 어느 쪽이 이길지를 `Add`의 등록 순서에 맡기면 그 중복 제거
            /// 키에 Scope가 없어 먼저 등록된 라벨이 남고, 수집 순서를 한 줄 옮기는 것만
            /// 으로 조용히 뒤집힌다. 그래서 `FromTableCollector.ScopeOf`에서 우선순위로
            /// 고정하고 테스트로 못박았다
            /// (ExtractLockHints_SubqueryInsideDerivedTable_ShouldWinOverDerivedScope).
            /// </summary>
            private const string SubqueryScope = "하위 질의";

            public List<LockHintFact> Facts { get; } = new();

            private readonly Dictionary<string, int> _ordinals = new(StringComparer.Ordinal);

            public override void ExplicitVisit(InsertSpecification node)
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
                        CollectWhereSubqueries("INSERT", ordinal, spec.WhereClause);
                    }
                }

                RecordTargetHint("INSERT", ordinal, node.Target);

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(UpdateSpecification node)
            {
                var ordinal = NextOrdinal("UPDATE");
                CollectFrom("UPDATE", ordinal, node.FromClause);
                CollectWhereSubqueries("UPDATE", ordinal, node.WhereClause);
                RecordTargetHint("UPDATE", ordinal, node.Target);

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(DeleteSpecification node)
            {
                var ordinal = NextOrdinal("DELETE");
                CollectFrom("DELETE", ordinal, node.FromClause);
                CollectWhereSubqueries("DELETE", ordinal, node.WhereClause);
                RecordTargetHint("DELETE", ordinal, node.Target);

                base.ExplicitVisit(node);
            }

            /// <summary>
            /// 독립 SELECT. 변수 대입 SELECT · 커서 원천 질의 · 함수 본문 SELECT가 전부
            /// 이 노드로 온다(프로브 실측 - `DECLARE CURSOR FOR SELECT`의 원천도
            /// `SelectStatement`다).
            ///
            /// [DML 안의 질의가 여기로 오지 않는다는 보장 - 수정 라운드 1 리뷰 판정]
            /// 보장하는 것은 방문 상태가 아니라 **AST 모양**이다. `Insert`/`Update`/
            /// `DeleteSpecification` 아래에 문장 노드인 `SelectStatement`가 놓이는 T-SQL
            /// 형태가 없다 - INSERT 원천은 `QueryExpression`이고, `SET x = (SELECT ...)`·
            /// 파생 테이블·`EXISTS(...)`는 전부 `ScalarSubquery`/`QueryDerivedTable` 아래의
            /// `QueryExpression`이다. 그래서 `INSERT ... SELECT`의 원천이 여기서 다시
            /// 잡혀 중복되는 일이 없다(ExtractLockHints_InsertSelectSource_ShouldNotProduceSelectRow).
            ///
            /// 예전에는 `_dmlDepth == 0` 가드가 이 자리를 지키는 것처럼 적혀 있었으나,
            /// 그 필드는 읽히는 지점에서 0이 아닐 수 없어 실제로는 아무것도 가르지 않았다.
            /// 근거와 역할이 어긋난 문서라 필드째 지웠다.
            ///
            /// [무엇을 세는지는 이 방문자가 정하지 않는다] 판정은 파일 수준의
            /// `DmlScopeExtractor.HasFromClause` 하나이고 `DmlScopeVisitor`도 같은
            /// 메서드를 부른다 - 그 문서에 이유가 있다. FROM이 없는
            /// `SELECT @a = 1`을 세지 않는 것은 `RecordTargetHint`가 "FROM도 없고
            /// 힌트도 없는 문장"을 싣지 않는 것과 같은 판단이다.
            /// </summary>
            public override void ExplicitVisit(SelectStatement node)
            {
                if (HasFromClause(node))
                {
                    CollectFromQuery("SELECT", NextOrdinal("SELECT"), node.QueryExpression);
                }

                base.ExplicitVisit(node);
            }

            /// <summary>UNION 갈래를 포함해 질의식의 모든 FROM을 훑는다.</summary>
            private void CollectFromQuery(string operation, int ordinal, QueryExpression? query)
            {
                if (query == null) return;

                foreach (var spec in QuerySpecificationsOf(query))
                {
                    CollectFrom(operation, ordinal, spec.FromClause);
                }
            }

            /// <summary>
            /// 제어 흐름 술어 안의 스캔. `IF EXISTS(SELECT ... WITH(NOLOCK))`이 실물이다
            /// (INS_EXTRA:31 - -9 차단 게이트의 판단 근거 스캔이 표 밖이었다).
            ///
            /// [본문이 아니라 술어만 감싸는 이유] `IF ... BEGIN UPDATE ... END`의 UPDATE는
            /// 자기 문장이고 자기 번호를 받아야 한다. 술어만 훑고 본문은 평소대로
            /// 자식 순회(base.ExplicitVisit)에 맡긴다.
            ///
            /// [번호를 미리 집지 않는 이유 - 계획서와 다른 자리]
            /// 계획서는 먼저 NextOrdinal("IF")로 집고 스캔이 없으면 `_ordinals["IF"]`를
            /// 되돌려 놓는 방식을 적었다. 결과는 같지만(집는 시점과 되돌리는 시점 사이에
            /// 오늘은 아무도 끼어들지 않는다) 카운터를 되감는 코드는, 나중에 그 사이에
            /// 채번이 한 줄이라도 들어오면 조용히 남의 번호를 지운다. 스캔이 있을 때만
            /// 집으면 되감을 일 자체가 없다 - `ExplicitVisit(SelectStatement)`이
            /// `HasFromClause`로 미리 가르는 것과 같은 모양이다.
            ///
            /// DML 안 하위 질의(CollectWhereSubqueries)와 겹치지 않는다 - 그쪽은 세 DML
            /// 오버라이드에서만 불리고, IF는 DML 문장 안에 나타날 수 없다.
            ///
            /// [첫 겹만 그 IF의 스캔인 이유] 술어의 첫 겹 하위 질의는 IF가 직접 훑는
            /// 자리이므로 `최상위`/`파생`으로 싣지만, 그 안에서 다시 열리는 질의는
            /// `하위 질의`다 - SubqueryScope 문서의 경계와 같다.
            ///
            /// [스캔이 없는 술어도 번호를 쓰는 자리 - 알고 남기는 비대칭]
            /// 여기서 세는 것은 "스캔"이 아니라 "하위 질의"다. 그래서 `IF @x = (SELECT 1)`
            /// 처럼 FROM 없는 하위 질의를 진 IF는 행을 하나도 내지 않으면서 IF 번호를
            /// 소비한다. `ExplicitVisit(SelectStatement)`이 `HasFromClause`로 FROM 없는
            /// SELECT를 아예 세지 않는 것과 어긋난다.
            ///
            /// 알고 남긴다. 실측 코퍼스의 IF 술어 하위 질의 6건은 전부
            /// `IF EXISTS(SELECT ... FROM ...)`이라 이 자리에 걸리는 원문이 없다.
            /// **여기에 `HasFromClause` 같은 조건을 나중에 더하면 기존 IF 번호가 조용히
            /// 밀린다** - 재생성된 명세서의 「IF n」이 전부 어긋나므로, 고치려거든
            /// 산출물 재생성과 함께 해야 한다.
            ///
            /// [`IF n`을 쓰는 표가 이제 둘이고, 두 표의 채번 조건이 다르다 -
            /// 2026-08-23 축 A ③(b) Task 1 수정 라운드 1 F1]
            /// 이 자리는 근거 (1)로 "IF 번호를 쓰는 표는 잠금 힌트 표 하나뿐이라 다른
            /// 표와 어긋날 여지가 없다"고 적고 있었다. Task 1이 그 문장을 거짓으로
            /// 만들었다 - `ReferencedFunctionVisitor`도 `IF n`을 매기기 시작했고, 두
            /// 방문자가 번호를 소비하는 조건이 서로 다르다. 이쪽은 술어에 <b>하위 질의</b>가
            /// 있을 때, 저쪽은 술어에 <b>알려진 함수 호출</b>이 있을 때다. 그래서
            /// `IF EXISTS(SELECT ...)` 다음에 `IF dbo.UF_X(1) &gt; 0`이 오는 프로시저에서
            /// 앞엣것은 잠금 힌트 표의 `IF 1`이 되고 뒤엣것은 참조 함수 표의 `IF 1`이
            /// 된다 - 같은 이름표, 다른 문장이다.
            ///
            /// 이것도 알고 남긴다. 양쪽을 맞추려면 한쪽 채번 조건을 넓혀야 하는데,
            /// 그 순간 이미 발행된 명세서의 「IF n」이 조용히 밀린다(바로 위 문단이
            /// 경고하는 것과 같은 사고다). `SELECT n`은 사정이 다르다 - 네 표가
            /// `HasFromClause` 하나를 공유하고
            /// `ExtractAndLockHintsAndFunctionCallsAndSetPredicates_ShouldAgreeOnSelectStatementNumbers`가
            /// 번호와 라인의 짝으로 그 합의를 못 박는다. `IF n`에는 그런 합의가 없다.
            /// <b>두 표의 `IF n`을 가로질러 대조하지 마라.</b> 언젠가 통일한다면
            /// 산출물 재생성과 함께여야 한다.
            /// </summary>
            public override void ExplicitVisit(IfStatement node)
            {
                var queries = SubqueriesOf(node.Predicate);

                if (queries.Count > 0)
                {
                    var ordinal = NextOrdinal("IF");
                    foreach (var (query, depth) in queries)
                    {
                        if (depth == 0) CollectFromQuery("IF", ordinal, query);
                        else CollectSubqueryScans("IF", ordinal, query);
                    }
                }

                base.ExplicitVisit(node);
            }

            /// <summary>
            /// DML 문장의 WHERE 안 하위 질의를 그 문장 번호로 훑는다.
            ///
            /// 범위를 `하위 질의`로 다는 이유는 `파생`과 같다 - 빼지 않고 표시해서 싣는다.
            /// 별도 문장 번호를 주지 않는 이유는 이 스캔이 이미 그 DML 문장의 일부라서,
            /// 새로 세면 같은 UPDATE가 두 번호로 나타나 다른 표와 대조할 수 없기 때문이다.
            ///
            /// 겹의 깊이를 가리지 않는다 - WHERE 하위 질의 안에서 또 열린 질의도 그
            /// 문장이 직접 훑는 자리가 아니므로 같은 범위다.
            ///
            /// WHERE만 훑는다고 해서 `JOIN ... ON` 안의 하위 질의가 빠지는 것은 아니다 -
            /// 그쪽은 FROM 절 안이라 `CollectFrom`이 이미 지나가고, `FromTableCollector`가
            /// ScalarSubquery 안을 같은 범위로 표시한다.
            /// </summary>
            private void CollectWhereSubqueries(string operation, int ordinal, WhereClause? where)
            {
                if (where?.SearchCondition == null) return;

                foreach (var (query, _) in SubqueriesOf(where.SearchCondition))
                {
                    CollectSubqueryScans(operation, ordinal, query);
                }
            }

            /// <summary>
            /// 하위 질의 하나가 훑는 자리를 전부 `하위 질의` 범위로 싣는다.
            ///
            /// "이 겹만 본다"고 읽지 말 것 - `QuerySpecificationsOf`는 UNION 갈래로만
            /// 내려가지만, 그 뒤에 쓰는 `FromTableCollector`는 파생 테이블과 그 안의
            /// 하위 질의까지 계속 내려간다. 더 깊은 겹은 `SubqueriesOf`를 통해서도
            /// 한 번 더 들어오는데, 두 경로가 같은 라벨을 내므로(FromTableCollector가
            /// ScalarSubquery 안을 `하위 질의`로 표시한다) 결과는 어느 쪽이 먼저 등록
            /// 되든 같다.
            /// </summary>
            private void CollectSubqueryScans(string operation, int ordinal, QueryExpression? query)
            {
                if (query == null) return;

                foreach (var spec in QuerySpecificationsOf(query))
                {
                    if (spec.FromClause == null) continue;

                    var tables = new FromTableCollector();
                    foreach (var reference in spec.FromClause.TableReferences) reference.Accept(tables);
                    foreach (var (table, _) in tables.Tables) Add(operation, ordinal, table, SubqueryScope);
                }
            }

            /// <summary>
            /// 불리언 식 안의 하위 질의를 겹 깊이와 함께 모은다. 깊이 0은 그 식이 직접
            /// 여는 질의이고, 1 이상은 그 질의의 술어 안에서 다시 열린 질의다.
            /// </summary>
            private static List<(QueryExpression Query, int Depth)> SubqueriesOf(BooleanExpression? predicate)
            {
                if (predicate == null) return new List<(QueryExpression, int)>();

                var collector = new SubqueryCollector();
                predicate.Accept(collector);
                return collector.Queries;
            }

            /// <summary>
            /// 술어 안의 `ScalarSubquery`를 모은다. EXISTS·IN·비교 어느 자리에 있든
            /// 하위 질의는 이 노드로 온다(프로브 실측). 술어 안에는 `SelectStatement`가
            /// 없으므로 바깥 방문자의 `SELECT n` 채번과 겹치지 않는다.
            ///
            /// 겹의 깊이를 함께 낸다. 호출부가 "이 식이 직접 여는 질의"와 "그 안에서 다시
            /// 열린 질의"를 갈라야 하기 때문이다(SubqueryScope 문서 참고). Visit이 아니라
            /// ExplicitVisit을 오버라이드해 base로 자식 순회를 이어가야 깊이를 표시할 수
            /// 있다 - FromTableCollector가 파생 테이블 진입/이탈을 다루는 방식과 같다.
            /// </summary>
            private sealed class SubqueryCollector : TSqlFragmentVisitor
            {
                public List<(QueryExpression Query, int Depth)> Queries { get; } = new();

                private int _depth;

                public override void ExplicitVisit(ScalarSubquery node)
                {
                    Queries.Add((node.QueryExpression, _depth));

                    _depth++;
                    base.ExplicitVisit(node);
                    _depth--;
                }
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
            /// 구분해 감사에서 실적이 있다. 가르는 기준은 "문장당 행이 하나뿐인가"다:
            /// DmlScopeFact는 문장당 행이 하나라 문장 줄을 그대로 쓰고, 잠금 힌트는 그
            /// 전제가 깨지므로 참조별 줄을 쓴다.
            ///
            /// [SetPredicateFact는 이제 이쪽이다 - 전체 브랜치 리뷰 M1] 이 문단은
            /// SetPredicateFact도 "문장당 행이 하나뿐이라" 문장 줄을 쓴다고 적고 있었다.
            /// Task 5가 행 단위를 최상위 `AND` 항으로 올리면서 그 전제가 깨졌고, 그 줄은
            /// 항 자신의 줄로 내려갔다(<c>SetPredicateFact.Line</c>) - 그리고 그 결정의
            /// 선례로 인용된 것이 바로 이 문단이다. 두 문서가 서로를 근거로 대면서
            /// 반대되는 사실을 적고 있었으므로 이쪽을 사실에 맞춘다.
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
            ///
            /// [ScalarSubquery도 표시해야 하는 이유 - 수정 라운드 1 리뷰 실측]
            /// 기본 순회는 `QualifiedJoin.SearchCondition` 안으로도 내려간다. 그래서
            /// `JOIN dbo.TB B ON B.ID IN (SELECT ID FROM dbo.TC WITH(NOLOCK))`의 TC가
            /// 이 수집기에 걸리는데, 표시가 없으면 `최상위`(파생 안이면 `파생`)로 실렸다 -
            /// 빠진 것이 아니라 틀리게 실린 것이라 더 나쁘다(스펙 §2.4). WHERE 안 하위
            /// 질의와 원문에서 같은 모양이므로 같은 범위여야 한다.
            ///
            /// [파생과 겹칠 때 하위 질의가 이긴다 - 규칙으로 고정]
            /// 두 표시가 동시에 켜지는 자리가 있다(파생 테이블 안의 WHERE 하위 질의,
            /// 하위 질의 안의 파생 테이블). 어느 쪽이 이길지를 `Add`의 등록 순서에
            /// 맡기면 - 그 중복 제거 키에는 Scope가 없어 먼저 등록된 라벨이 남는다 -
            /// 수집 순서를 한 줄 옮기는 것만으로 라벨이 조용히 뒤집힌다. 여기서
            /// 우선순위로 고정해 두 경로가 언제나 같은 답을 내게 한다
            /// (ExtractLockHints_SubqueryInsideDerivedTable_ShouldWinOverDerivedScope).
            /// </summary>
            private sealed class FromTableCollector : TSqlFragmentVisitor
            {
                public List<(NamedTableReference Node, string Scope)> Tables { get; } = new();

                private bool _inDerivedTable;

                private bool _inSubquery;

                public override void Visit(NamedTableReference node) =>
                    Tables.Add((node, ScopeOf()));

                private string ScopeOf()
                {
                    if (_inSubquery) return SubqueryScope;
                    return _inDerivedTable ? DerivedScope : TopLevelScope;
                }

                public override void ExplicitVisit(QueryDerivedTable node)
                {
                    var wasInDerivedTable = _inDerivedTable;
                    _inDerivedTable = true;
                    base.ExplicitVisit(node);
                    _inDerivedTable = wasInDerivedTable;
                }

                public override void ExplicitVisit(ScalarSubquery node)
                {
                    var wasInSubquery = _inSubquery;
                    _inSubquery = true;
                    base.ExplicitVisit(node);
                    _inSubquery = wasInSubquery;
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
            /// DML 밖의 독립 SELECT. 변수 대입 SELECT · 커서 원천 질의 · 함수 본문
            /// SELECT가 전부 이 노드로 온다(프로브 실측 - `DECLARE CURSOR FOR SELECT`의
            /// 원천도 `SelectStatement`다). `INSERT ... SELECT`의 원천은 문장 노드가
            /// 아니라 `QueryExpression`이라 여기로 오지 않는다 - 중복으로 실리지 않는다
            /// (LockHintVisitor.ExplicitVisit(SelectStatement) 문서의 AST 모양 근거 참고).
            ///
            /// [왜 이 표에 싣는가 - 2026-08-22 축 A 재감사] 커서 원천 질의의 ORDER BY와
            /// GROUP BY를 담을 자리가 이 표의 기존 칸이다(PROC_ETC:62 - 처리 순서가
            /// MAX(ID)+1 채번 결과와 -3 중단 지점을 가르는데 문서 전체에 없었다).
            /// 새 표를 만들지 않고 문장 집합만 넓히면 그 칸이 저절로 채워진다.
            ///
            /// [판정은 LockHintVisitor와 같은 메서드다] `HasFromClause`는 파일 수준
            /// 헬퍼 하나다 - 두 표의 `SELECT n`이 같은 문장을 가리키는 근거가 그것이고,
            /// 복제하면 갈라질 수 있다(그 메서드 문서 참고).
            ///
            /// [SELECT만 더하고 IfStatement는 더하지 않는 이유] LockHintVisitor는
            /// `IF` 술어 안의 스캔도 `IF n`으로 담지만 이 방문자는 담지 않는다 -
            /// 근거는 DmlScopeFact.Operation 문서에 적어 두었다. 두 방문자의 문장
            /// 집합이 이 지점에서 다르다는 것을 모르고 읽으면 결함처럼 보인다.
            ///
            /// [ExplicitVisit인 이유 - 이 방문자의 DML 오버라이드는 `Visit`인데도]
            /// LockHintVisitor가 같은 노드를 `ExplicitVisit`으로 받아 자식 순회
            /// (`base.ExplicitVisit`) 전에 번호를 매긴다. 여기도 같은 오버라이드에서
            /// 같은 자리(자식 순회 전)에 사실을 더해야 두 표의 SELECT가 원문 순서라는
            /// 같은 근거로 늘어선다 - 이 표의 문장 번호는 사실 목록 안의 자리로
            /// 매겨지므로(AiService.BuildStatementOrdinals) 더하는 시점이 곧 번호다.
            /// </summary>
            public override void ExplicitVisit(SelectStatement node)
            {
                if (HasFromClause(node))
                {
                    RecordStandaloneSelect(node);
                }

                base.ExplicitVisit(node);
            }

            /// <summary>
            /// 독립 SELECT의 사실 하나를 만든다.
            ///
            /// [대상과 기준일이 비는 이유] 이 표의 열은 "무엇이 <b>갱신</b> 대상 범위를
            /// 정하는가"를 묻는데 독립 SELECT에는 갱신 대상이 없다. 그래서 `Target`은
            /// 빈 문자열, `DateParameterApplied`는 항상 false로 두고, 그 두 칸을 "—"로
            /// 낼지 "(없음)"으로 낼지는 렌더러가 정한다 - `OrderByExpressions`가
            /// UPDATE·DELETE에서 이미 쓰는 것과 같은 분업이다. 추출기에 표시 문자열을
            /// 넣지 않는다.
            /// </summary>
            private void RecordStandaloneSelect(SelectStatement node)
            {
                var predicateColumns = new List<string>();
                var joinKeys = new List<string>();
                var groupByPerBranch = new List<IReadOnlyList<string>>();

                foreach (var spec in QuerySpecificationsOf(node.QueryExpression))
                {
                    if (spec.WhereClause?.SearchCondition != null)
                    {
                        // 최상위 술어만 본다 - Record·Visit(InsertSpecification)과 같은
                        // 경계다(TopLevelPredicateCollector 문서 참고).
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
                    }

                    // ON 절의 결합 조건도 훑는다 - UPDATE·DELETE(Record)와
                    // INSERT(Visit)가 이미 하는 일이다. 여기만 빼면 조인이 실재하는
                    // 문장의 조인 키 칸이 "(없음)"으로 렌더돼 거짓 행이 된다.
                    if (spec.FromClause != null)
                    {
                        var joins = new JoinConditionCollector();
                        spec.FromClause.Accept(joins);
                        foreach (var k in joins.Columns)
                        {
                            if (!joinKeys.Contains(k, StringComparer.OrdinalIgnoreCase)) joinKeys.Add(k);
                        }
                    }

                    // UNION 갈래마다 모아 뒀다가 ResolveGroupByColumns로 합친다 -
                    // 갈래마다 다르면 비운다(DmlScopeFact.GroupByColumns 제약 7).
                    groupByPerBranch.Add(CollectGroupByColumns(spec));
                }

                Facts.Add(new DmlScopeFact(
                    "SELECT",
                    node.StartLine,
                    string.Empty,
                    predicateColumns,
                    false,
                    joinKeys,
                    OrderByExpressionsOf(node.QueryExpression),
                    ResolveGroupByColumns(groupByPerBranch)));
            }

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
                var groupByPerBranch = new List<IReadOnlyList<string>>();

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

                    // 이 QuerySpecification 자신의 GroupByClause만 본다 - 파생 테이블
                    // 안의 GROUP BY는 애초에 이 순회에 잡히지 않는다(SourceQuerySpecifications가
                    // UNION·괄호만 펼치고 FROM 안으로는 내려가지 않으므로). 갈래별로
                    // 모아 뒀다가 마지막에 ResolveGroupByColumns로 합친다 - DmlScopeFact.
                    // GroupByColumns 문서의 제약 7 실측 근거 참고.
                    groupByPerBranch.Add(CollectGroupByColumns(spec));
                }

                Facts.Add(new DmlScopeFact(
                    "INSERT", node.StartLine, TextOf(node.Target),
                    predicateColumns, dateApplied, joinKeys, OrderByExpressionsOf(node.InsertSource),
                    ResolveGroupByColumns(groupByPerBranch)));
            }

            /// <summary>
            /// QuerySpecification 하나의 GROUP BY 키 목록을 뽑는다. 단순 컬럼 참조가
            /// 아닌 그루핑 식(ROLLUP·CUBE·식 그루핑 등)은 담지 않는다 - 표의 "컬럼"
            /// 칸에 쓸 이름이 없는 것을 억지로 만들지 않는다(TopLevelPredicateCollector.
            /// LeftSideText가 컬럼 아닌 좌변을 버리는 것과 같은 원칙).
            /// </summary>
            private static List<string> CollectGroupByColumns(QuerySpecification? query)
            {
                var columns = new List<string>();
                var clause = query?.GroupByClause;
                if (clause == null) return columns;

                foreach (var spec in clause.GroupingSpecifications)
                {
                    if (spec is not ExpressionGroupingSpecification expr) continue;
                    if (expr.Expression is not ColumnReferenceExpression column) continue;

                    var name = column.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value;
                    if (!string.IsNullOrWhiteSpace(name)) columns.Add(name!);
                }

                return columns;
            }

            /// <summary>
            /// UNION 갈래별 GROUP BY 키를 문장 하나의 사실로 합친다.
            ///
            /// [갈래가 다르면 비우는 이유 - DmlScopeFact.GroupByColumns 문서의 제약 7
            /// 실측 근거 참고] 모든 갈래가 완전히 같은 GROUP BY 키 목록(순서 포함)을
            /// 가질 때만 그 값을 싣는다. 갈래가 하나뿐이면(UNION이 아니면) 그 갈래의
            /// 값을 그대로 쓴다. 갈래가 없으면(VALUES 원천) 빈 목록이다.
            /// </summary>
            private static IReadOnlyList<string> ResolveGroupByColumns(
                IReadOnlyList<IReadOnlyList<string>> perBranch)
            {
                if (perBranch.Count == 0) return Array.Empty<string>();

                var first = perBranch[0];
                for (var i = 1; i < perBranch.Count; i++)
                {
                    if (!perBranch[i].SequenceEqual(first, StringComparer.OrdinalIgnoreCase))
                    {
                        return Array.Empty<string>();
                    }
                }

                return first;
            }

            /// <summary>
            /// INSERT 원천의 최상위 ORDER BY 컬럼을 뽑는다.
            ///
            /// [Select를 QuerySpecification으로 좁히지 않는 이유] OrderByClause는
            /// QuerySpecification이 아니라 그 공통 기반 QueryExpression에 선언돼 있다
            /// (2026-08-21 프로브 실측, 이 파일 DmlScopeFact.OrderByExpressions 문서 참고).
            /// UNION 원천의 최상위 ORDER BY는 BinaryQueryExpression 자신에 붙고 갈래
            /// QuerySpecification에는 붙지 않으므로, Select를 QueryExpression 그대로 두고
            /// OrderByClause에 바로 접근해야 QuerySpecification·BinaryQueryExpression·
            /// QueryParenthesisExpression 세 경우가 한 코드로 잡힌다.
            ///
            /// [e.Expression이 아니라 e(OrderByElement) 자신의 원문을 접어서 쓰는 이유 -
            /// 수정 라운드 1 리뷰 실측]
            /// 두 가지가 한 줄로 닫힌다.
            ///
            /// 1. TextOf(e.Expression)만 쓰면 여러 줄로 쓰인 식(`CASE WHEN ... END`처럼
            ///    ORDER BY도 임의 식을 받는다)이 개행을 그대로 담는다. 이 재료의 값은
            ///    "컬럼 이름"이 아니라 "임의 식의 원문"이라 PredicateColumns·JoinKeys(항상
            ///    단순 식별자)와 달리 개행 위험이 실재한다. L1은 접지 않은 원문과 대조하므로
            ///    (CollapseWhitespace 문서, 이 파일 아래쪽) 개행이 든 값은 어떤 산출물도
            ///    만족시킬 수 없는 요구가 된다 - TopLevelPredicateCollector의 집합 술어
            ///    좌변과 LockHintVisitor.RenderHint의 값 있는 힌트가 이미 같은 이유로
            ///    CollapseWhitespace(TextOf(...))를 쓰고 있다. 세 번째 자리만 예외로 둘
            ///    근거가 없다.
            /// 2. DESC·ASC는 OrderByElement.SortOrder로 표현되고 그 키워드 토큰은
            ///    e.Expression이 아니라 e(OrderByElement) 자신의 토큰 스트림에 있다
            ///    (프로브 실측: `ORDER BY A DESC`에서 TextOf(e.Expression)은 "A"만, TextOf(e)는
            ///    "A DESC"를 낸다). e.Expression만 보면 방향이 조용히 사라져, 원본이
            ///    `ORDER BY A DESC`인데 표가 `A`라고 적어 grep으로 원본을 찾을 수 없게 된다 -
            ///    이 표의 원칙("독자가 원본에서 찾을 수 있어야 한다")을 어긴다.
            /// </summary>
            private static IReadOnlyList<string> OrderByExpressionsOf(InsertSource? source) =>
                OrderByExpressionsOf((source as SelectInsertSource)?.Select);

            /// <summary>
            /// 질의식의 최상위 ORDER BY. 독립 SELECT는 `InsertSource`가 아니라
            /// `QueryExpression`을 들고 있으므로 이 오버로드를 부르고, INSERT 쪽은
            /// 원천을 벗겨 여기에 위임한다 - 본문이 둘로 갈리지 않는다. 왜 갈래
            /// `QuerySpecification`이 아니라 `QueryExpression`에서 절을 읽는지, 왜
            /// `e.Expression`이 아니라 `OrderByElement` 자신의 원문을 접어 쓰는지는
            /// 위 오버로드의 문서에 있다.
            /// </summary>
            private static IReadOnlyList<string> OrderByExpressionsOf(QueryExpression? query)
            {
                var orderBy = query?.OrderByClause;
                if (orderBy == null) return Array.Empty<string>();

                return orderBy.OrderByElements
                    .Select(e => CollapseWhitespace(TextOf(e)))
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
        /// 그 SELECT 문장이 훑는 자리가 있는가 - `SELECT n`으로 번호를 줄지 가르는
        /// 판정이다. UNION이면 갈래 중 하나만 FROM을 가져도 참이다.
        ///
        /// [왜 방문자 안이 아니라 여기 있는가 - 설계 §4 A, Task 1 리뷰 C5]
        /// 이 판정을 쓰는 방문자가 넷이다(LockHintVisitor는 잠금 힌트 표의 `SELECT n`,
        /// DmlScopeVisitor는 DML 범위 표의 `SELECT n`, ReferencedFunctionVisitor는
        /// 참조 함수 표의 `SELECT n`, SetPredicateVisitor는 집합 술어 표의 `SELECT n` -
        /// 셋째는 2026-08-23 축 A ③(b) Task 1이, 넷째는 같은 배치 Task 2가 더했다).
        /// 네 방문자는 서로를 참조하지
        /// 않고 각자 세는 것이 계약인데, 그 계약이 성립하려면 "무엇이 SELECT 문장
        /// 하나인가"만은 반드시 같아야 한다. 판정을 각 방문자 안에 복제하면 한쪽만
        /// 고쳐지는 날 네 표의 같은 번호가 다른 문장을 가리키게 되고, 표를 가로질러
        /// 읽는 독자에게 그 어긋남은 조용하다 - 그래서 판정은 이 메서드 하나가
        /// 유일한 출처다. ExtractAndLockHintsAndFunctionCallsAndSetPredicates_ShouldAgreeOnSelectStatementNumbers가
        /// 네 표의 <b>번호와 라인의 짝</b>을 맞대 그 합의를 못박는다 - 라인만 맞대면
        /// 한쪽이 행 없는 문장까지 세기 시작해도 라인 목록은 그대로라 어긋남이 그냥
        /// 통과한다(수정 라운드 1에 뮤테이션으로 실측했다: 잠금 힌트 쪽만 FROM 없는
        /// SELECT를 세게 하면 번호가 {1,3}에서 {2,4}로 밀리는데 라인 목록은 불변이다).
        ///
        /// [판정을 공유해도 채번은 각자다] 이 메서드가 같아도 `NextOrdinal("SELECT")`
        /// 호출부는 방문자마다 따로 있다. 그래서 채번 지점을 늘리는 사람은 위 테스트도
        /// 함께 넓혀야 한다 - Task 1이 셋째 지점을 더하면서 그 확장을 빠뜨렸고,
        /// 수정 라운드 1 F2가 잡았다. Task 2가 넷째 지점(SetPredicateVisitor)을 더하면서
        /// 그 지시대로 가드를 함께 넓혔다 - 픽스처의 커서 원천 SELECT에 WHERE를 두어
        /// 그 표가 `SELECT 3`을 덮게 했다.
        ///
        /// [FROM이 없으면 세지 않는 이유] `SELECT @a = 1`에는 훑는 자리가 없다.
        /// 번호를 소비하면 표에 낼 행도 없이 뒤 문장의 번호만 민다.
        /// </summary>
        private static bool HasFromClause(SelectStatement node) =>
            QuerySpecificationsOf(node.QueryExpression).Any(q => q.FromClause != null);

        /// <summary>
        /// DML 문장과 <b>DML 밖의 독립 SELECT</b>를 찾아 그 최상위 WHERE(와 파생 테이블
        /// WHERE)에서 집합 술어를 모으고, 수집기가 모르는 문장 문맥(연산 종류·문장 번호)을
        /// 붙인다. 독립 SELECT는 2026-08-23 축 A ③(b) Task 2가 더했다 -
        /// <see cref="ExplicitVisit(SelectStatement)"/>에 그 근거가 있다.
        ///
        /// [줄 번호는 여기서 붙이지 않는다 - Task 5] 예전에는 문장의 시작 줄도 이
        /// 방문자가 붙였다. 지금은 <c>SetPredicateFact.Line</c>이 <b>항 자신</b>의 줄이라
        /// 술어를 만드는 자리에서 정해진다 - 문장당 술어가 여럿이므로 문장 줄을 쓰면
        /// 그 여럿이 전부 같은 줄로 찍힌다(그 문서의 "문장 줄에서 항의 줄로 내린 이유"
        /// 참고).
        ///
        /// [문장 번호를 이 방문자가 직접 매기는 이유] SetPredicateFact.StatementOrdinal
        /// 문서 참고 - DmlScopeVisitor와 같은 파싱 트리를 같은 네 오버라이드
        /// (DML 셋은 `Visit`, 독립 SELECT는 `ExplicitVisit`)로 같은 순서로 방문하고
        /// SELECT의 판정은 <see cref="HasFromClause"/> 하나를 공유하므로, 이 방문자가
        /// 독자적으로 세어도 DML 범위 표의 번호와 항상 일치한다. 카운터는 반드시 각
        /// 오버라이드 안에서(Collect 안이 아니라) 문장당 정확히 한 번 늘려야 한다 -
        /// NextOrdinal 문서 참고.
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
            /// DML 밖의 독립 SELECT의 WHERE. 변수 대입 SELECT · 커서 원천 질의 · 함수 본문
            /// SELECT가 전부 이 노드로 온다. `INSERT ... SELECT`의 원천은 문장 노드가 아니라
            /// QueryExpression이라 여기로 오지 않는다 - 그래서 `INSERT n` 행과 겹쳐 실리지
            /// 않는다(DmlScopeFact.Operation 문서의 AST 모양 근거,
            /// `ExtractSetPredicates_InsertSourceSelect_ShouldStayInsertOnly`가 못 박는다).
            ///
            /// [왜 넓히는가 - 2026-08-23 축 A ③(b) Task 2, 설계 §2.2] UF_GET_COLLECTYMD:100의
            /// `CollectFlag = 1`은 리터럴 우변 등치라 이 표가 <b>담을 수 있는</b> 형태인데
            /// 독립 SELECT라는 이유만으로 담기지 않았다. "회수구분이 1(자동회수)인 행만
            /// 조회한다"가 어떤 기계 확정 표에도 없고 산문에만 있었다.
            ///
            /// [판정은 네 표가 공유한다] FROM 유무 판정은 <see cref="HasFromClause"/> 하나다 -
            /// DmlScopeVisitor·LockHintVisitor·ReferencedFunctionVisitor가 이미 그것을 부른다.
            /// 이 오버라이드가 <b>넷째 채번 지점</b>이라
            /// `ExtractAndLockHintsAndFunctionCallsAndSetPredicates_ShouldAgreeOnSelectStatementNumbers`도
            /// 함께 넓혔다(그 메서드 문서의 상시 지시).
            ///
            /// [IfStatement는 더하지 않는다] 잠금 힌트 표·참조 함수 표는 `IF n`을 담지만 이
            /// 표와 DML 범위 표는 담지 않는다 - `IF` 술어의 <b>집합 술어</b>가 산문에만 있던
            /// 사례가 실측되지 않았다(DmlScopeFact.Operation 문서가 같은 판단을 적었다).
            /// </summary>
            public override void ExplicitVisit(SelectStatement node)
            {
                if (HasFromClause(node))
                {
                    // 번호는 UNION 갈래 수와 무관하게 문장당 하나다 - INSERT가 갈래마다
                    // Collect를 부르면서 번호는 미리 집어 공유하는 것과 같은 구조다.
                    //
                    // [갈래마다 Collect를 부르는 대가 - 알고 남긴다] Collect 안의
                    // DerivedTableCollector는 문장 **전체**를 훑는다. 그래서 독립 SELECT가
                    // UNION과 별칭 있는 파생 테이블을 동시에 가지면 그 파생 테이블 술어가
                    // 갈래 수만큼 중복 수집된다. 바로 위 Visit(InsertSpecification)이 이미
                    // 같은 구조라 이 작업이 새로 만든 위험은 아니고, 코퍼스에 그 모양이
                    // 없다 - 2026-08-23 실측: output/Objects 전체에서 UNION 연산자는 다섯
                    // 객체 여덟 자리뿐이고, 전부 **문장 노드가 아닌 자리**에 있다.
                    // 여덟 자리를 원문에서 하나씩 확인한 분류다:
                    //   CMRate_Ins:100·179   INSERT(76·159) 원천의 최상위 UNION
                    //   SETTLE_INS:165·226   INSERT(55) 원천 **안의 파생 테이블**(`) X`는 300행)
                    //   STAT_PGCOLLECT_INS:78·95  INSERT(31) 원천 안의 파생 테이블(`FROM (`는 57행)
                    //   EXCEPTION_PROC:485   UPDATE(452) 안의 파생 테이블 X(`) X` 508 → `) BB` 510)
                    //   COMM_UPD:143         UPDATE 안의 `NOT IN (...)` 서브쿼리
                    // 즉 독립 SELECT 문장의 UNION은 0건이라 이 오버라이드를 타는 것이 없다.
                    // 코퍼스에 그 모양이 들어오는 날 고칠 자리는 여기가 아니라 Collect다
                    // (파생 테이블 훑기를 갈래 루프 밖으로 한 번만 빼면 INSERT도 같이 낫는다).
                    var ordinal = NextOrdinal("SELECT");
                    foreach (var spec in QuerySpecificationsOf(node.QueryExpression))
                    {
                        Collect("SELECT", node, spec.WhereClause, ordinal);
                    }
                }

                base.ExplicitVisit(node);
            }

            /// <summary>
            /// 연산 종류별 문장 번호를 1부터 매긴다. SetPredicateFact.StatementOrdinal
            /// 문서의 실측 근거 참고 - DmlScopeVisitor가 문장 하나당 사실을 정확히
            /// 하나만 내는 지점(DML 셋은 각 `Visit` 오버라이드, 독립 SELECT는
            /// `ExplicitVisit(SelectStatement)`의 자식 순회 <b>전</b>)과 카운터 증가
            /// 지점을 맞춰야, 두 방문자가 독립적으로 세어도 항상 같은 번호가 나온다.
            /// </summary>
            private int NextOrdinal(string operation)
            {
                _perOperation.TryGetValue(operation, out var n);
                _perOperation[operation] = ++n;
                return n;
            }

            private void Collect(string operation, TSqlFragment statement, WhereClause? where, int ordinal)
            {
                CollectFrom(operation, where?.SearchCondition, ordinal, TopLevelScope);

                // 파생 테이블 안의 필터도 대상 행 집합을 좁힌다 - 2026-08-19 축 A 감사의
                // 🟠 4건 중 둘(COMM_UPD:243, EXCEPTION_PROC:375)이 이 자리였다. 최상위
                // WHERE만 훑으면 그 술어는 사실이 하나도 나오지 않아 L1이 침묵한다.
                var derived = new DerivedTableCollector();
                statement.Accept(derived);

                foreach (var (alias, searchCondition) in derived.Tables)
                {
                    CollectFrom(operation, searchCondition, ordinal, $"파생 테이블 {alias}");
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

            /// <summary>
            /// 한 검색 조건의 최상위 AND 항을 사실로 옮긴다. 문장 조각을 받지 않는 이유는
            /// 라인이 더 이상 문장 시작줄이 아니기 때문이다 - 각 사실의 라인은 그 항
            /// 자신의 줄이다(SetPredicateFact.Line 문서 참고).
            ///
            /// 방문(Accept)이 아니라 <see cref="TopLevelPredicateCollector.CollectTerms"/>를
            /// 부르는 이유: 이 표의 행 단위는 항이지 트리 어딘가의 술어 노드가 아니다.
            /// 방문으로 모으면 OR 갈래 안의 비교까지 독립 행이 되어 AND로 읽히고,
            /// 그렇게 읽으면 모순(공집합)이 된다(설계 §2.4). Columns·Parameters·JoinKeys를
            /// 쓰는 DmlScopeVisitor 쪽 호출부는 지금처럼 Accept를 쓴다 - 그쪽은 식 전체를
            /// 훑어야 하고, 이 작업으로 좁아져서는 안 된다.
            /// </summary>
            private void CollectFrom(
                string operation, BooleanExpression? searchCondition, int ordinal, string scope)
            {
                if (searchCondition == null) return;

                var top = new TopLevelPredicateCollector();
                top.CollectTerms(searchCondition);

                foreach (var (column, op, literals, term) in top.SetPredicates)
                {
                    Facts.Add(new SetPredicateFact(
                        operation, term.StartLine, column,
                        op == "NOT IN", literals, ordinal, op, scope,
                        CollapseWhitespace(TextOf(term))));
                }
            }
        }

        /// <summary>
        /// 문장마다 연산별 번호를 매기고 그 안의 사용자 함수 호출을 모은다.
        ///
        /// [어느 표와 번호가 맞는가 - 2026-08-23 축 A ③(b) Task 1 수정 라운드 1]
        /// 이 자리는 "번호를 매기는 규칙은 SetPredicateVisitor와 같다 … 항상 같은
        /// 번호가 나온다"고 적고 있었다. Task 1이 방문 범위를 넓히면서 그 문장이
        /// 거짓이 됐다. 지금 성립하는 것은 셋이다.
        ///
        /// (1) `UPDATE`·`DELETE`·`INSERT` 번호는 네 표 전부에서 같은 문장을 가리킨다 -
        ///     채번이 연산 이름별로 독립이라 SELECT·IF 행이 늘어도 밀리지 않는다.
        /// (2) `SELECT` 번호는 이 표 · DML 범위 표 · 잠금 힌트 표 · 집합 술어 표
        ///     넷이 합의한다 - 판정을 <see cref="HasFromClause"/> 하나로 공유하고
        ///     `ExtractAndLockHintsAndFunctionCallsAndSetPredicates_ShouldAgreeOnSelectStatementNumbers`가
        ///     번호와 라인의 짝으로 못 박는다. 이 자리는 "집합 술어 표에는 `SELECT n`
        ///     행이 없다"고 적고 있었다 - 같은 배치 Task 2가 그 표도 독립 SELECT를
        ///     방문하게 하면서 거짓이 됐다.
        /// (3) `IF` 번호는 <b>합의가 없다.</b> 이 표는 술어에 알려진 함수 호출이 있을 때,
        ///     잠금 힌트 표는 술어에 하위 질의가 있을 때 번호를 소비한다 - 조건이 다르니
        ///     같은 `IF 1`이 다른 문장일 수 있다. 근거는
        ///     `LockHintVisitor.ExplicitVisit(IfStatement)` 문서에 있다.
        /// </summary>
        private sealed class ReferencedFunctionVisitor : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _known;
            private readonly Dictionary<string, int> _perOperation =
                new(StringComparer.OrdinalIgnoreCase);

            public ReferencedFunctionVisitor(IReadOnlyCollection<string> knownFunctionNames) =>
                _known = new HashSet<string>(knownFunctionNames, StringComparer.OrdinalIgnoreCase);

            public List<ReferencedFunctionCallFact> Facts { get; } = new();

            public override void ExplicitVisit(UpdateSpecification node)
            {
                Collect("UPDATE", node, NextOrdinal("UPDATE"));
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(DeleteSpecification node)
            {
                Collect("DELETE", node, NextOrdinal("DELETE"));
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(InsertSpecification node)
            {
                Collect("INSERT", node, NextOrdinal("INSERT"));
                base.ExplicitVisit(node);
            }

            /// <summary>
            /// DML 밖의 독립 SELECT. 변수 대입 SELECT · 커서 원천 · 함수 본문이 전부 이 노드로 온다.
            ///
            /// [왜 넓히는가 - 2026-08-23 축 A ③(b)] COLLECTYMD:53·78이 변수 대입 SELECT의
            /// SELECT 목록 안 CASE 식에서 UF_GET_WORKDAY2를 부른다. 이 호출이 수집되지 않아
            /// 참조 함수 표가 아예 생기지 않았고(AiService가 수집 0건이면 표를 통째로 뺀다),
            /// 표가 없으니 링크도 없어 모델이 산문으로 요약했다 - 그 요약에서 간격 0 특례가
            /// 빠진 것이 🔴이다. 링크만 걸렸으면 결함이 없었다(UF_GET_WORKDAY2 자신의
            /// 명세서에는 그 사실이 정확히 있다).
            ///
            /// [FROM이 없으면 세지 않는 이유] 네 표가 같은 문장을 같은 번호로 가리켜야
            /// 하므로 판정을 공유한다 - <see cref="HasFromClause"/>는 DmlScopeExtractor의
            /// 파일 수준 헬퍼이고 LockHintVisitor·DmlScopeVisitor가 이미 그것을 부른다.
            /// </summary>
            public override void ExplicitVisit(SelectStatement node)
            {
                if (HasFromClause(node))
                {
                    Collect("SELECT", node, NextOrdinal("SELECT"));
                }

                base.ExplicitVisit(node);
            }

            /// <summary>
            /// 제어 흐름 술어 안의 함수 호출. 술어만 훑고 본문은 자식 순회에 맡긴다 -
            /// IF 본문의 DML은 자기 문장이고 자기 번호를 받아야 한다. 그래서 문장 전체를
            /// 훑는 <see cref="Collect"/>를 쓰지 않고 술어만 CallCollector에 넘긴다.
            /// 스캔이 아니라 호출을 세므로 FROM 유무를 묻지 않는다.
            ///
            /// [번호를 호출이 있을 때만 집는 이유] 빈손인 IF가 번호를 삼키면 뒤 IF의
            /// 번호가 조용히 밀린다. LockHintVisitor.ExplicitVisit(IfStatement)이 같은
            /// 판단을 했고 그 근거가 그 자리 주석에 있다.
            /// </summary>
            public override void ExplicitVisit(IfStatement node)
            {
                var calls = new CallCollector(_known);
                node.Predicate?.Accept(calls);

                if (calls.Calls.Count > 0)
                {
                    var ordinal = NextOrdinal("IF");
                    foreach (var (qualified, line, text) in calls.Calls)
                    {
                        Facts.Add(new ReferencedFunctionCallFact(qualified, "IF", ordinal, line, text));
                    }
                }

                base.ExplicitVisit(node);
            }

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
            /// 최상위 AND 항마다 사실 하나. Term은 그 항의 노드 자신이다 - 호출부가
            /// 여기서 라인(<see cref="SetPredicateFact.Line"/>)과 원문
            /// (<see cref="SetPredicateFact.PredicateText"/>)을 얻는다. 노드를 그대로
            /// 넘기고 호출부가 접는 이유는, 이 수집기가 라인·원문 표기 규칙(어느 줄을
            /// 쓸지, 공백을 접을지)을 알 필요가 없기 때문이다 - 문장 문맥과 표기 규칙은
            /// 둘 다 호출부의 몫이다.
            ///
            /// 분해되는 항(IN/NOT IN·리터럴 우변 비교)은 Column에 좌변 표기, Operator에
            /// 연산, Literals에 원문 그대로의 원소를 담는다. 분해되지 않는 항은 세 칸을
            /// <see cref="NotDecomposed"/>·빈 목록으로 두고 Term만 진다
            /// (2026-08-22 축 A 재감사 ③ Task 6 · 설계 §3 결정 3).
            ///
            /// <see cref="CollectTerms"/>만 이 목록을 채운다. 방문(Accept) 경로는
            /// Columns·Parameters·JoinKeys만 모은다 - 방문으로 채우면 OR 갈래 안의
            /// 비교까지 독립 행이 되어 AND로 읽히고, 그렇게 읽으면 모순이 된다(설계 §2.4).
            /// </summary>
            public List<(string Column, string Operator, List<string> Literals, TSqlFragment Term)> SetPredicates { get; } = new();

            /// <summary>분해되지 않는 항의 컬럼·연산 칸에 쓰는 표기.</summary>
            internal const string NotDecomposed = "—";

            /// <summary>
            /// 최상위 AND 항마다 집합 사실을 하나씩 낸다. 분해되면 분해해서도 싣고,
            /// 못 하면 원문 전용으로 싣는다 - 어느 경우에도 항 자체는 빠지지 않는다
            /// (설계 §3 결정 3). 이 메서드는 <see cref="SetPredicates"/>만 채운다.
            /// Columns·Parameters·JoinKeys는 지금처럼 식 전체를 훑는 Accept 경로가
            /// 계속 모은다 - DML 범위 표의 술어 컬럼 칸과 조인 키 칸이 이 작업으로
            /// 좁아져서는 안 된다.
            /// </summary>
            public void CollectTerms(BooleanExpression? searchCondition)
            {
                foreach (var term in TopLevelAndTerms(searchCondition))
                {
                    var decomposed = TryDecompose(term);

                    SetPredicates.Add(decomposed is { } d
                        ? (d.Column, d.Operator, d.Literals, (TSqlFragment)term)
                        : (NotDecomposed, NotDecomposed, new List<string>(), term));
                }
            }

            /// <summary>
            /// 최상위 AND 항을 평탄화한다. OR로 묶인 것은 통째로 한 항이다 - 안으로
            /// 내려가면 갈래마다의 조건이 AND처럼 나란히 실려 모순으로 읽힌다
            /// (2026-08-22 축 A 재감사 실측: EXCEPTION_PROC:210이 정확히 그 모양이었다.
            /// `(A.UseState &lt;&gt; 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))`이
            /// UseState &lt;&gt; 1과 UseState = 1 두 행으로 실려 공집합을 뜻했다).
            ///
            /// 괄호는 안이 AND일 때만 벗긴다. `(A.X = 1)`처럼 항 하나를 감싼 괄호는
            /// 여기서 벗기지 않고 <see cref="TryDecompose"/>가 분해할 때만 벗긴다 -
            /// 그래야 원문 칸이 괄호까지 원본 그대로 진다.
            /// </summary>
            private static IEnumerable<BooleanExpression> TopLevelAndTerms(BooleanExpression? node)
            {
                if (node == null) yield break;

                if (node is BooleanBinaryExpression binary
                    && binary.BinaryExpressionType == BooleanBinaryExpressionType.And)
                {
                    foreach (var term in TopLevelAndTerms(binary.FirstExpression)) yield return term;
                    foreach (var term in TopLevelAndTerms(binary.SecondExpression)) yield return term;
                    yield break;
                }

                if (node is BooleanParenthesisExpression paren
                    && paren.Expression is BooleanBinaryExpression inner
                    && inner.BinaryExpressionType == BooleanBinaryExpressionType.And)
                {
                    foreach (var term in TopLevelAndTerms(paren.Expression)) yield return term;
                    yield break;
                }

                yield return node;
            }

            /// <summary>
            /// 항 하나를 컬럼·연산·리터럴로 분해한다. 분해되지 않으면 null.
            ///
            /// 분해가 성립하는 것은 <b>우변이 전부 리터럴일 때</b>뿐이다. `A.YMD = A.AYMD`
            /// (설계 §2.2)나 `TxAmt != CardAmt+CouponAmt`(§2.3)는 옮겨 적을 리터럴이 없어
            /// 여기서 실패하고, 호출부가 원문 전용 행으로 담는다. 예전에는 이 실패가
            /// "사실 없음"이었고 그래서 그 항들이 어떤 표에도 나타나지 않았다.
            ///
            /// 괄호를 벗기고 판정하는 이유는 괄호가 의미가 아니라 표기이기 때문이다 -
            /// 벗기지 않으면 `(A.PGNAME IN (...))`처럼 괄호 하나로 감싼 항이 원문
            /// 전용으로 떨어져 리터럴 집합이 표에서 사라진다. 원문 전용 행이 분해되는
            /// 행을 삼키는 것은 이 작업이 막아야 할 회귀다.
            /// </summary>
            private static (string Column, string Operator, List<string> Literals)? TryDecompose(
                BooleanExpression term)
            {
                var node = term;
                while (node is BooleanParenthesisExpression paren && paren.Expression != null)
                {
                    node = paren.Expression;
                }

                return node switch
                {
                    InPredicate inPredicate => DecomposeIn(inPredicate),
                    BooleanComparisonExpression comparison => DecomposeComparison(comparison),
                    _ => null
                };
            }

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
            /// 리터럴만으로 이뤄진 IN 항을 컬럼·연산·원소로 분해한다. 분해되지 않으면 null.
            ///
            /// [분해하지 않는 셋] 서브쿼리 IN은 옮겨 적을 리터럴 목록이 없다. 원소에
            /// 리터럴 아닌 것이 섞이면 리터럴 집합으로 렌더할 때 명세서에 거짓
            /// 집합이 실린다. 좌변이 단순 컬럼 참조가 아니면(예: 식) 표의 "컬럼"
            /// 칸에 쓸 이름이 없다. 셋 다 <b>사실이 사라지는 것은 아니다</b> - 호출부가
            /// 원문 전용 행으로 담는다(2026-08-22 축 A 재감사 ③ Task 6).
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
            private static (string Column, string Operator, List<string> Literals)? DecomposeIn(InPredicate node)
            {
                if (node.Subquery != null || node.Values == null || node.Values.Count == 0) return null;

                // Column은 마지막 식별자 조각이 아니라 원문 표기 그대로 담는다(레코드
                // 문서의 실측 근거 참고) - 한정자가 있으면 A.USESTATE처럼 한정자까지
                // 포함해야 같은 문장 안의 A.USESTATE와 B.USESTATE가 서로 다른 키가 된다.
                var column = LeftSideText(node.Expression);
                if (column == null) return null;

                var literals = new List<string>();
                foreach (var value in node.Values)
                {
                    if (value is not Literal literal) return null;   // 하나라도 아니면 통째로 버린다
                    literals.Add(TextOf(literal));
                }

                return (column, node.NotDefined ? "NOT IN" : "IN", literals);
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

                base.ExplicitVisit(node);
            }

            /// <summary>
            /// 리터럴을 우변에 둔 `=`·`&lt;&gt;` 비교를 원소 하나짜리 집합으로 분해한다.
            /// 분해되지 않으면 null.
            ///
            /// [왜 등호까지 담는가 - 2026-08-19 축 A 감사]
            /// 감사에서 나온 대상 행 집합 결함 4건이 전부 "원본 필터가 명세서 어디에도
            /// 없다"는 한 부류였고, 그중 둘이 `CommissionCancelFlag = 1`이었다. 등호를
            /// 담지 않으면 L1이 대조할 재료 자체가 없어, 취소수수료 미부과 계약을
            /// 걸러내는 조건이 통째로 사라져도 아무 검사도 울리지 않는다. 원본 코퍼스
            /// 기준 이 형태가 129건이다.
            ///
            /// 우변이 리터럴일 때만 분해한다 - `A.YMD = @pi_strYMD`나 `A.PLTID = B.PLTID`는
            /// 옮겨 적을 리터럴이 없고, 리터럴 칸에 담으면 표가 기준일 비교와 조인 키로
            /// 뒤덮여 진짜 리터럴 집합이 묻힌다. 조인 키는 바로 위에서
            /// <see cref="JoinKeys"/>가 담는다. 그 항들은 이제 표에서 사라지지 않고
            /// 원문 전용 행으로 실린다 - 분해 칸이 아니라 원문 칸을 쓰므로 리터럴
            /// 집합은 여전히 묻히지 않는다(2026-08-22 축 A 재감사 ③ Task 6 · 설계 §2.2).
            ///
            /// `&gt;=`·`&gt;`·`&lt;=`·`&lt;` 등 화이트리스트 밖 연산자도 여기서 분해되지
            /// 않고 원문 전용 행이 된다. 화이트리스트를 넓히는 대신 원문으로 담는 이유는
            /// 설계 §3 결정 3 - 앞으로 나올 미지의 술어 형태에도 샘이 없다.
            /// </summary>
            private static (string Column, string Operator, List<string> Literals)? DecomposeComparison(
                BooleanComparisonExpression node)
            {
                var op = node.ComparisonType switch
                {
                    BooleanComparisonType.Equals => "=",
                    BooleanComparisonType.NotEqualToBrackets => "<>",
                    BooleanComparisonType.NotEqualToExclamation => "<>",
                    _ => null
                };

                if (op == null || node.SecondExpression is not Literal literal) return null;

                var column = LeftSideText(node.FirstExpression);
                if (column == null) return null;

                return (column, op, new List<string> { TextOf(literal) });
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
