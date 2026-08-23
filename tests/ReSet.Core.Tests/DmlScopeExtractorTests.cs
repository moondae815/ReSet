using System;
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class DmlScopeExtractorTests
    {
        [Fact]
        public void Extract_DateParameterOnlyInSubquery_ShouldReportNotApplied()
        {
            // EXCEPTION_PROC 실행순서 18 실측: 바깥 UPDATE에 YMD 필터가 없고
            // 서브쿼리만 정산일로 제한되는데 Spec은 "YMD = @pi_strYMD를 기본
            // 범위로"라 일괄 기술했다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A
    SET    A.OutState = 9
    FROM   dbo.TSettleMst A
    WHERE  A.UseState = 0
    AND    EXISTS (SELECT 1 FROM dbo.TSettleMst B WHERE B.YMD = @pi_strYMD AND B.PLTID = A.PLTID)
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var fact = Assert.Single(facts);
            Assert.Equal("UPDATE", fact.Operation);
            Assert.False(fact.DateParameterApplied);
            Assert.Contains("UseState", fact.PredicateColumns);
        }

        [Fact]
        public void Extract_DateParameterOnTheTarget_ShouldReportApplied()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A
    SET    A.OutState = 2
    FROM   dbo.TSettleMst A
    WHERE  A.YMD = @pi_strYMD
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            Assert.True(Assert.Single(facts).DateParameterApplied);
        }

        [Fact]
        public void Extract_JoinKeys_ShouldBeCaptured()
        {
            // EXCEPTION_PROC 실행순서 4 실측: 조인 키에 MallID가 없는데
            // Spec은 조인 키를 아예 기술하지 않았다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A
    SET    A.CLComm = B.CLComm
    FROM   dbo.TSettleMst  A
    JOIN   dbo.TClientRate B ON A.YMD = B.YMD AND A.ClientID = B.ClientID AND A.PGName = B.PGName
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var joinKeys = Assert.Single(facts).JoinKeys;
            Assert.Contains("ClientID", joinKeys);
            Assert.DoesNotContain("MallID", joinKeys);
        }

        [Fact]
        public void Extract_Delete_ShouldBeIncluded()
        {
            // INS_EXTRA 실측: DELETE에 OutState/OutYMD 조건이 전혀 없는데
            // Spec은 "지급 완료·확정 행은 삭제 대상에 포함되지 않습니다"라 단언했다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    DELETE FROM dbo.TSettleMst WHERE YMD = @pi_strYMD AND ClientID = 'X'
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var fact = Assert.Single(facts);
            Assert.Equal("DELETE", fact.Operation);
            Assert.Contains("YMD", fact.PredicateColumns);
            Assert.DoesNotContain("OutState", fact.PredicateColumns);
        }

        [Fact]
        public void Extract_EmptyDdl_ShouldReturnEmpty()
        {
            Assert.Empty(DmlScopeExtractor.Extract(null, "@pi_strYMD"));
        }

        [Fact]
        public void Extract_InPredicateWithSubquery_ShouldKeepTheTestedColumnButNotApplyTheDateParameter()
        {
            // EXCEPTION_PROC 실행순서 18의 실제 형태(PLTID IN (서브쿼리)). 왼쪽
            // 피연산자(PLTID)는 대상 범위를 실제로 좁히므로 잃으면 안 되지만,
            // 서브쿼리 안의 @pi_strYMD는 대상에 걸리지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE TSettleMst
    SET    OutState = 9
    WHERE  PLTID IN (SELECT PLTID FROM TSettleMst WHERE YMD = @pi_strYMD AND UseState = 1)
    AND    UseState = 0
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var fact = Assert.Single(facts);
            Assert.False(fact.DateParameterApplied);
            Assert.Contains("PLTID", fact.PredicateColumns);
            Assert.Contains("UseState", fact.PredicateColumns);
        }

        [Fact]
        public void Extract_CommaStyleJoin_ShouldCaptureEquiJoinColumnsAsJoinKeys()
        {
            // EXCEPTION_PROC 실행순서 3(108행)/4(130행) 실측 형태. 콤마로 나열한 옛
            // 스타일 조인은 ON절이 없고 결합 조건이 WHERE 최상위에 있다. 두 문장이
            // 나란히 있고, 3번은 MallID까지 결합하지만 4번은 MallID 없이 YMD·
            // ClientID·PGName만 결합한다 - A1-3(MallID 누락)이 정확히 이 대비다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE TSettleMst
    SET    CLComm = 0
    FROM   TSettleMst        A WITH(NOLOCK)
          ,TClientSettleRate B WITH(NOLOCK)
    WHERE  A.YMD      = B.YMD
    AND    A.CLIENTID = B.CLIENTID
    AND    A.PGNAME   = B.PGNAME
    AND    A.MALLID   = B.MALLID
    AND    A.YMD      = @pi_strYMD
    AND    A.PGNAME   = 'KFTC'

    UPDATE TSettleMst
    SET    CLCOMM = 0
    FROM   TSettleMst        A WITH(NOLOCK)
          ,TClientSettleRate B WITH(NOLOCK)
    WHERE  A.YMD      = B.YMD
    AND    A.CLIENTID = B.CLIENTID
    AND    A.PGNAME   = B.PGNAME
    AND    A.YMD      = @pi_strYMD
    AND    B.MINCOMMISSIONAMT <> 0
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            Assert.Equal(2, facts.Count);
            var withMallId = facts[0];
            var withoutMallId = facts[1];

            Assert.Contains("MALLID", withMallId.JoinKeys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("CLIENTID", withMallId.JoinKeys, StringComparer.OrdinalIgnoreCase);

            Assert.DoesNotContain("MALLID", withoutMallId.JoinKeys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("CLIENTID", withoutMallId.JoinKeys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("PGNAME", withoutMallId.JoinKeys, StringComparer.OrdinalIgnoreCase);

            // 파라미터 비교(A.YMD = @pi_strYMD)는 컬럼-컬럼 동등비교가 아니므로
            // 조인 키로 새지 않는다.
            Assert.DoesNotContain(withoutMallId.JoinKeys, k => string.Equals(k, "@pi_strYMD", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Extract_ExistsCorrelatedColumn_ShouldKeepOuterColumnButNotApplyDateParameter()
        {
            // EXISTS 서브쿼리 내부에서 바깥 별칭(A)을 참조하는 조건은 실제로 대상
            // 범위를 좁힌다. 서브쿼리 자신의 별칭(B)이 아닌 한정자를 쓰는 컬럼만
            // "바깥 참조"로 본다 - 어느 쪽이 진짜 대상인지 추측하지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A
    SET    A.OutState = 9
    FROM   dbo.TSettleMst A
    WHERE  EXISTS (SELECT 1 FROM dbo.TSettleMst B WHERE B.PLTID = A.PLTID AND B.YMD = @pi_strYMD)
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var fact = Assert.Single(facts);
            Assert.False(fact.DateParameterApplied);
            Assert.Contains("PLTID", fact.PredicateColumns);
        }

        [Fact]
        public void Extract_SameAliasEquality_ShouldNotBeAJoinKey()
        {
            // EXCEPTION_PROC 실행순서 4(228행) 실측 형태: A.YMD = A.AYMD는 같은 별칭
            // 안의 날짜 제외 필터일 뿐, 두 테이블을 잇지 않는다. 값 자체는
            // PredicateColumns에는 그대로 남아야 한다 - 정보가 빠지는 게 아니라
            // "조인이다"라는 잘못된 주장만 빠져야 한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A
    SET    A.OutState = 9
    FROM   dbo.TSettleMst A
    WHERE  A.YMD = @pi_strYMD
    AND    A.YMD = A.AYMD
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var fact = Assert.Single(facts);
            Assert.DoesNotContain("AYMD", fact.JoinKeys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("AYMD", fact.PredicateColumns, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Extract_BothUnqualifiedEquality_ShouldNotBeAJoinKey()
        {
            // 두 피연산자 모두 한정자가 없으면 어느 테이블 소속인지 알 수 없다 -
            // 조인이라고 주장할 근거가 없으므로 놓치는 쪽(안전한 기본값)으로 기운다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE TSettleMst
    SET    OutState = 9
    WHERE  YMD = @pi_strYMD
    AND    TID = CID
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var fact = Assert.Single(facts);
            Assert.DoesNotContain("TID", fact.JoinKeys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("CID", fact.JoinKeys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("TID", fact.PredicateColumns, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("CID", fact.PredicateColumns, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Extract_OneSideQualifiedEquality_ShouldNotBeAJoinKey()
        {
            // 한쪽만 한정자가 있으면 반대쪽 소속을 알 수 없다 - 같은 이유로
            // 조인 키로 세지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A
    SET    A.OutState = 9
    FROM   dbo.TSettleMst A
    WHERE  A.YMD = @pi_strYMD
    AND    A.TID = CID
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var fact = Assert.Single(facts);
            Assert.DoesNotContain("TID", fact.JoinKeys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("CID", fact.JoinKeys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("TID", fact.PredicateColumns, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("CID", fact.PredicateColumns, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Extract_InsertSelect_ShouldRecordSourceWherePredicates()
        {
            // 2026-08-18 축 A 감사 실측. 표 이름이 "DML 범위(기계 확정 — 수정 금지)"인데
            // UPDATE/DELETE만 담던 동안 INSERT를 가진 SP 8개가 전부 걸렸다.
            // UP_UTIL_STAT_PGCOLLECT_INS는 삭제 전용처럼 보였고,
            // UP_Util_PG_Client_CMRate_Ins는 INSERT 5문이 라인 앵커가 붙은 유일한
            // 표에서 통째로 빠져 추적 근거를 잃었다.
            var ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    DELETE FROM dbo.T WHERE YMD = @pi_strYMD

    INSERT INTO dbo.T (YMD, Amt)
    SELECT A.YMD, A.Amt
    FROM   dbo.S A JOIN dbo.U B ON A.PLTID = B.PLTID
    WHERE  A.YMD = @pi_strYMD AND A.UseState = 1
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var insert = Assert.Single(facts, f => f.Operation == "INSERT");
            Assert.Contains("YMD", insert.PredicateColumns);
            Assert.Contains("UseState", insert.PredicateColumns);
            Assert.True(insert.DateParameterApplied);
            Assert.Contains("PLTID", insert.JoinKeys);
            Assert.Contains(facts, f => f.Operation == "DELETE");
        }

        [Fact]
        public void Extract_InsertValues_ShouldRecordStatementWithoutPredicates()
        {
            // VALUES 원천은 조건이 없다 - 술어가 비고 기준일이 false인 것이 사실이다.
            // 그래도 문장 자체는 표에 실려야 라인 앵커가 남는다.
            var ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    INSERT INTO dbo.T (YMD, Amt) VALUES (@pi_strYMD, 0)
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.Equal("INSERT", fact.Operation);
            Assert.Empty(fact.PredicateColumns);
            Assert.False(fact.DateParameterApplied);
        }

        [Fact]
        public void Extract_InsertFromUnionAllSource_ShouldMergeBranchPredicates()
        {
            // UP_UTIL_SETTLE_SUMMARY_EXTRA 형태. 갈래마다 WHERE가 다르므로 전부 합친다.
            var ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    INSERT INTO dbo.T (YMD, Amt)
    SELECT YMD, Amt FROM dbo.S WHERE YMD = @pi_strYMD
    UNION ALL
    SELECT YMD, Amt FROM dbo.U WHERE ReqYMD = '20260101' AND UseState = 0
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.Contains("YMD", fact.PredicateColumns);
            Assert.Contains("ReqYMD", fact.PredicateColumns);
            Assert.Contains("UseState", fact.PredicateColumns);
            Assert.True(fact.DateParameterApplied);
        }

        [Fact]
        public void Extract_InsertSelectWithOrderBy_CarriesTheColumns()
        {
            // STAT_PGCOLLECT_INS:113 실측. ORDER BY INYMD, CLIENTID, PGNAME, MALLID가
            // 문서 어디에도 없어 🟡이었다(2026-08-21 축 A 감사). 존재 여부가 아니라
            // 컬럼 목록을 싣는다 - 더 충실하고 비용이 같다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TStat (A, B)
    SELECT INYMD, CLIENTID FROM dbo.TSource
    GROUP BY INYMD, CLIENTID
    ORDER BY INYMD, CLIENTID
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.Equal(new[] { "INYMD", "CLIENTID" }, fact.OrderByExpressions);
        }

        [Fact]
        public void Extract_InsertWithoutOrderBy_HasEmptyOrderByList()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TStat (A) SELECT X FROM dbo.TSource
END";

            Assert.Empty(Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD")).OrderByExpressions);
        }

        [Fact]
        public void Extract_UpdateAndDelete_HaveEmptyOrderBy()
        {
            // UPDATE·DELETE는 최상위 ORDER BY가 문법상 불가하다. 표에서는 "—"로 렌더된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T SET C = 1 WHERE X = 1
    DELETE FROM dbo.T WHERE X = 2
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            Assert.All(facts, f => Assert.Empty(f.OrderByExpressions));
        }

        [Fact]
        public void Extract_InsertFromUnionAllWithTopLevelOrderBy_CarriesTheColumns()
        {
            // UP_Util_PG_Client_CMRate_Ins의 INSERT 2(76행)·INSERT 4(159행)가 UNION ALL
            // 원천이다(웨이브 1 실측). 프로브(2026-08-21)로 확인: 최상위 ORDER BY는
            // ScriptDom에서 BinaryQueryExpression 자신의 OrderByClause에 붙는다 -
            // 어느 갈래(QuerySpecification)에도 붙지 않는다. Select as QuerySpecification
            // 단일 캐스팅만 하면 이 ORDER BY를 통째로 놓친다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.T (A, B)
    SELECT A, B FROM dbo.S1 WHERE X = 1
    UNION ALL
    SELECT A, B FROM dbo.S2 WHERE Y = 2
    ORDER BY A, B
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.Equal(new[] { "A", "B" }, fact.OrderByExpressions);
        }

        [Fact]
        public void Extract_InsertOrderByMultilineExpression_ShouldNotContainNewlines()
        {
            // 수정 라운드 1 리뷰 실측 - CASE WHEN을 여러 줄로 쓰면 TextOf(e.Expression)이
            // 개행을 그대로 담는다. L1은 접지 않은 원문과 대조하므로(CollapseWhitespace
            // 문서, :752-759) 개행이 든 값은 어떤 산출물도 만족시킬 수 없는 요구가 된다.
            // TopLevelPredicateCollector(:1165)·LockHintVisitor.RenderHint(:538)가 이미
            // 같은 이유로 접고 있다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TStat (A, B)
    SELECT A, B FROM dbo.TSource
    ORDER BY CASE WHEN A = 1
                  THEN A
                  ELSE B
             END
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.DoesNotContain(fact.OrderByExpressions, c => c.Contains('\n') || c.Contains('\r'));
        }

        [Fact]
        public void Extract_InsertOrderByWithDescDirection_ShouldKeepTheDirection()
        {
            // 수정 라운드 1 리뷰 Minor 실측 - DESC는 OrderByElement에 달려 있고
            // e.Expression에는 없다. TextOf(e.Expression)만 보면 방향이 조용히 사라져
            // 표가 원본 DDL과 grep으로 대조되지 않는다(원본이 `ORDER BY A DESC`인데
            // 표가 `A`라고 적으면 못 찾는다).
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TStat (A, B)
    SELECT A, B FROM dbo.TSource
    ORDER BY A DESC, B
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.Equal(new[] { "A DESC", "B" }, fact.OrderByExpressions);
        }

        [Fact]
        public void Extract_InsertWithGroupBy_ShouldRecordTheGroupingKeys()
        {
            // UP_Util_Settle_Summary 실측: GROUP BY 첫 키 YMD가 매핑 표의 설명 칸에서
            // "그룹화 키"로 표기되지 않아, 표로 GROUP BY를 재구성하면 키가 빠졌다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD CHAR(8)
AS
BEGIN
    INSERT INTO dbo.TSettleByTX (YMD, CLIENTID, CNT)
    SELECT YMD, CLIENTID, COUNT(*)
    FROM   dbo.TSettleMst
    WHERE  YMD = @pi_strYMD
    GROUP BY YMD, CLIENTID
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.Equal(new[] { "YMD", "CLIENTID" }, fact.GroupByColumns);
        }

        [Fact]
        public void Extract_InsertWithoutGroupBy_HasEmptyGroupByList()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TStat (A) SELECT X FROM dbo.TSource
END";

            Assert.Empty(Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD")).GroupByColumns);
        }

        [Fact]
        public void Extract_UpdateAndDelete_HaveEmptyGroupBy()
        {
            // UPDATE·DELETE는 최상위 GROUP BY가 문법상 불가하다 - ORDER BY와 같은
            // 규약을 쓴다(표에서는 "—"로 렌더된다, AiService.BuildDmlScopeTableLines 참고).
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T SET C = 1 WHERE X = 1
    DELETE FROM dbo.T WHERE X = 2
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            Assert.All(facts, f => Assert.Empty(f.GroupByColumns));
        }

        [Fact]
        public void Extract_InsertSelectFromDerivedTableWithGroupBy_ShouldNotLeakTheInnerGroupByToTheOuterStatement()
        {
            // 제약 6 (Task 8) - INSERT ... SELECT ... FROM (SELECT ... GROUP BY ...) X는
            // 파생 테이블 안에서 그룹화하지만, 바깥 INSERT 문장 자신은 GROUP BY 절이
            // 없다. QuerySpecificationsOf는 UNION/괄호만 펼치고 FROM 안의 파생 테이블로는
            // 내려가지 않으므로, 바깥 QuerySpecification의 GroupByClause만 봐야 이
            // 구분이 지켜진다 - 이 배치의 Task 4가 정확히 이 부류(GROUP BY 귀속)에서
            // 결함이 났다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TOut (YMD, CNT)
    SELECT X.YMD, X.CNT
    FROM (SELECT YMD, COUNT(*) AS CNT FROM dbo.TSource GROUP BY YMD) X
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.Empty(fact.GroupByColumns);
        }

        [Fact]
        public void Extract_InsertSelectDirectGroupBy_IsNotConfusedWithDerivedTableGroupBy()
        {
            // 위 테스트의 대조군 - INSERT ... SELECT ... GROUP BY(파생 테이블 없이
            // 바깥 문장 자신의 GROUP BY)는 정상적으로 잡혀야 한다. 두 모양을 가르는
            // 것이 이 추출기의 존재 이유다(제약 6).
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TOut (YMD, CNT)
    SELECT YMD, COUNT(*)
    FROM   dbo.TSource
    GROUP BY YMD
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.Equal(new[] { "YMD" }, fact.GroupByColumns);
        }

        [Fact]
        public void Extract_InsertFromUnionWithSameGroupByOnEveryBranch_CarriesTheSharedKeys()
        {
            // 제약 7 - UNION 갈래마다 같은 GROUP BY 키를 쓰면 하나의 사실로 실을 수
            // 있다(애매하지 않다).
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TOut (YMD, CNT)
    SELECT YMD, COUNT(*) FROM dbo.S1 GROUP BY YMD
    UNION ALL
    SELECT YMD, COUNT(*) FROM dbo.S2 GROUP BY YMD
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.Equal(new[] { "YMD" }, fact.GroupByColumns);
        }

        [Fact]
        public void Extract_InsertFromUnionWithDifferentGroupByPerBranch_ShouldLeaveGroupByEmpty()
        {
            // 제약 7 - 갈래마다 GROUP BY가 다르면(또는 한쪽만 있으면) 한 문장에 대해
            // 무엇을 실어야 할지 애매하다. 애매하면 비운다 - 과소 포착은 Minor,
            // 거짓 행은 Critical이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TOut (YMD, CLIENTID, CNT)
    SELECT YMD, CLIENTID, COUNT(*) FROM dbo.S1 GROUP BY YMD, CLIENTID
    UNION ALL
    SELECT YMD, NULL, COUNT(*) FROM dbo.S2 GROUP BY YMD
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.Empty(fact.GroupByColumns);
        }

        [Fact]
        public void Extract_CursorSourceSelect_ShouldCarryOrderByAndGroupBy()
        {
            // PROC_ETC:62 실측 - 커서 원천의 ORDER BY가 문서 전체에 없었다.
            // 처리 순서가 MAX(ID)+1 채번 결과와 -3 중단 지점을 가른다(2026-08-22 축 A 재감사 🟡).
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    DECLARE Cur_SettlePost CURSOR FOR
    SELECT A.ClientID, A.YMD, A.OutYMD
    FROM   dbo.TSettleMst A WITH(NOLOCK)
    WHERE  A.YMD = @pi_strYMD
    GROUP BY A.ClientID, A.YMD, A.OutYMD
    ORDER BY A.OutYMD, A.ClientID
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var fact = Assert.Single(facts);
            Assert.Equal("SELECT", fact.Operation);
            Assert.Equal(new[] { "A.OutYMD", "A.ClientID" }, fact.OrderByExpressions);
            // GroupByColumns는 마지막 식별자 조각만 담는다(CollectGroupByColumns) -
            // INSERT 원천의 GROUP BY와 같은 표기여야 한 칸을 두 규칙으로 읽지 않는다.
            Assert.Equal(new[] { "ClientID", "YMD", "OutYMD" }, fact.GroupByColumns);
            Assert.Contains("YMD", fact.PredicateColumns);

            // 갱신 대상이 없으므로 대상은 비우고 기준일 판정도 하지 않는다 -
            // 표시(—)는 렌더러가 정한다(OrderByExpressions가 이미 쓰는 분업).
            Assert.Equal(string.Empty, fact.Target);
            Assert.False(fact.DateParameterApplied);
        }

        [Fact]
        public void Extract_StandaloneSelect_ShouldNotDisturbDmlOrdinals()
        {
            // DML 범위 표의 문장 번호는 사실이 들고 있는 값이 아니라 목록 안 자리로
            // 매겨진다(DmlScopeExtractor.BuildStatementOrdinals(렌더러 AiService는 이를 위임 호출)). 그래서 "UPDATE 번호가 밀리지
            // 않는다"는 "UPDATE 사실의 개수와 상대 순서가 그대로다"와 같은 말이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T1 A WHERE A.YMD = @pi_strYMD

    SELECT @v = MIN(ReqYMD) FROM dbo.TA WITH(NOLOCK)

    UPDATE B SET B.X = 2 FROM dbo.T2 B WHERE B.YMD = @pi_strYMD
END";

            var facts = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");

            var updates = facts.Where(f => f.Operation == "UPDATE").ToList();
            Assert.Equal(new[] { "A", "B" }, updates.Select(f => f.Target).ToArray());
            Assert.All(updates, f => Assert.True(f.DateParameterApplied));

            var select = Assert.Single(facts, f => f.Operation == "SELECT");
            Assert.Equal(string.Empty, select.Target);
        }

        [Fact]
        public void Extract_StandaloneSelectWithJoin_ShouldCarryJoinKeys()
        {
            // 조인 키 칸을 비우면 렌더러가 "(없음)"으로 낸다 - ON 절이 실재하는데
            // 없다고 적는 것은 거짓 행이다. UPDATE·DELETE·INSERT 세 경로가 이미
            // JoinConditionCollector로 ON 절을 훑으므로 독립 SELECT만 예외로 둘
            // 근거가 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @v = MIN(A.ReqYMD)
    FROM   dbo.TA A WITH(NOLOCK)
    INNER JOIN dbo.TB B WITH(NOLOCK) ON A.ClientID = B.ClientID
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.Equal("SELECT", fact.Operation);
            // JoinConditionCollector는 한정자를 뗀 이름을 담고 양쪽이 같은 이름이면
            // 하나로 접는다 - UPDATE 경로(Extract_JoinKeys_ShouldBeCaptured)와 같은 표기다.
            Assert.Equal(new[] { "ClientID" }, fact.JoinKeys);
        }

        [Fact]
        public void ExtractAndLockHintsAndFunctionCallsAndSetPredicates_ShouldAgreeOnSelectStatementNumbers()
        {
            // 네 방문자가 "무엇이 SELECT n인가"를 각자 복제해 판정하면 같은 DDL에서
            // 네 표의 같은 번호가 다른 문장을 가리킬 수 있고, 표를 가로질러 읽는
            // 독자에게 그 어긋남은 조용하다. 판정은 DmlScopeExtractor의 정적 헬퍼
            // 하나(HasFromClause)이고 네 방문자가 그것을 부른다(2026-08-22 Task 1
            // 리뷰 C5 · 2026-08-23 축 A ③(b) Task 1 수정 라운드 1 F2 · 같은 배치 Task 2).
            //
            // [판정을 공유하는 것만으로는 부족하다] 채번 지점(NextOrdinal("SELECT"))은
            // 방문자마다 따로 있다. 판정이 같아도 한쪽 채번만 갈라지면 표가 조용히
            // 어긋나므로, 채번 지점이 늘 때마다 이 가드도 함께 넓혀야 한다.
            //
            // [양쪽을 다 잡아야 하는 이유 - 수정 라운드 1] 두 표의 라인 목록만
            // 맞대면 한 방향밖에 못 잡는다. 잠금 힌트 쪽 판정만 갈라져 FROM 없는
            // `SELECT @a = 1`까지 세는 날에도, 그 문장은 훑을 자리가 없어 행을 하나도
            // 내지 않으므로 라인 목록은 그대로다 - 그동안 번호만 조용히 밀려
            // 잠금 힌트 표는 SELECT 2·3, DML 범위 표는 SELECT 1·2가 된다. 그래서
            // 라인이 아니라 <b>번호와 라인의 짝</b>을 맞댄다.
            //
            // [픽스처가 TVF SELECT를 끼우는 이유] 정합을 실제로 지탱하는 불변식은
            // "잠금 힌트 쪽은 행이 0개인 문장도 번호를 소비한다"이다. TVF만 훑는
            // SELECT가 그 모양이다 - FROM은 있으므로(HasFromClause 참) 두 방문자
            // 모두 번호를 주지만, 잠금 힌트를 만드는 FromTableCollector.Visit는
            // NamedTableReference만 받으므로 행은 나지 않는다. 이 문장이 없으면
            // 그 불변식이 픽스처에 아예 등장하지 않아 검증되지 않는다.
            //
            // 각 SELECT의 FROM을 SELECT와 같은 줄에 두어, 잠금 힌트 사실의 라인
            // (테이블 참조 위치)과 DML 범위 사실의 라인(문장 시작)이 같은 값이 되게
            // 했다 - 두 표를 라인으로 맞대 볼 수 있다. 커서 원천의 WHERE도 같은 줄에
            // 둔 이유가 같다: 집합 술어 사실의 라인은 그 **항 자신**의 줄이라
            // (SetPredicateFact.Line) 항을 다음 줄에 두면 문장 시작줄과 맞댈 수 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @a = 1

    SELECT @v = MIN(ReqYMD) FROM dbo.TA WITH(NOLOCK)

    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A WITH(NOLOCK)

    SELECT @c = COUNT(*) FROM dbo.UIF_SettleYMD('20260101') F

    DECLARE Cur_SettlePost CURSOR FOR
    SELECT B.ClientID FROM dbo.TB B WITH(NOLOCK) WHERE B.UseState = 0 ORDER BY B.ClientID
END";

            // DML 범위 표의 번호는 사실 목록 안의 자리다(DmlScopeExtractor.BuildStatementOrdinals(렌더러 AiService는 이를 위임 호출)) -
            // 연산별로 세므로 SELECT만 걸러낸 목록의 i번째가 곧 `SELECT i+1`이다.
            var scopeSelectLines = DmlScopeExtractor.Extract(ddl, "@pi_strYMD")
                .Where(f => f.Operation == "SELECT")
                .Select(f => f.Line)
                .ToArray();

            // FROM이 없는 `SELECT @a = 1`은 세지 않고, 나머지 셋은 센다.
            Assert.Equal(3, scopeSelectLines.Length);

            var hintSelects = DmlScopeExtractor.ExtractLockHints(ddl)
                .Where(f => f.Operation == "SELECT")
                .ToArray();

            // 잠금 힌트 표가 실제로 쓰는 번호는 1과 3이다 - 2번(TVF)은 행이 없어
            // 비어 있지만 번호는 소비됐다. 한쪽 판정이 갈라지면 이 배열이 {2,4}나
            // {1,2}로 바뀌므로, 라인이 그대로여도 여기서 잡힌다.
            Assert.Equal(new[] { 1, 3 }, hintSelects.Select(f => f.StatementOrdinal).ToArray());

            // 그리고 그 번호들은 DML 범위 표의 같은 번호와 같은 문장을 가리킨다.
            Assert.All(hintSelects, f => Assert.Equal(scopeSelectLines[f.StatementOrdinal - 1], f.Line));

            // [세 번째 채번 지점 - 2026-08-23 축 A ③(b) Task 1] 참조 함수 표도 독립
            // SELECT를 방문하면서 NextOrdinal("SELECT")를 따로 돌린다. 픽스처의 TVF
            // SELECT가 마침 이 표에서만 행을 내므로(잠금 힌트 표는 NamedTableReference가
            // 없어 비운다) 세 표가 SELECT 1·2·3을 나눠 덮는다 - 잠금 힌트가 1과 3,
            // 참조 함수가 2다.
            var functionSelects = DmlScopeExtractor.ExtractFunctionCalls(ddl, new[] { "UIF_SettleYMD" })
                .Where(f => f.Operation == "SELECT")
                .ToArray();

            var tvfCall = Assert.Single(functionSelects);

            // 짝을 먼저 맞댄다 - 이 단언이 이 가드가 지키는 주장(참조 함수 표의
            // SELECT n이 DML 범위 표의 SELECT n과 같은 문장이다) 그 자체다. 절대
            // 번호를 먼저 단언하면 채번이 갈라진 날 그쪽이 먼저 터져, 정작 표 사이
            // 어긋남을 보여 주는 라인 비교는 실행되지도 않는다.
            Assert.Equal(scopeSelectLines[tvfCall.StatementOrdinal - 1], tvfCall.Line);
            Assert.Equal(2, tvfCall.StatementOrdinal);

            // [네 번째 채번 지점 - 2026-08-23 축 A ③(b) Task 2] 집합 술어 표도 독립
            // SELECT를 방문하면서 NextOrdinal("SELECT")를 따로 돌린다. 커서 원천
            // 문장은 **이미 잠금 힌트 표에 `SELECT 3`으로 실려 있다** - 위 `{1,3}`
            // 단언이 그것을 못 박는다. 그러니 여기서 맞대는 것은 "이 표만 덮는
            // 문장이 생겼다"가 아니라 **넷째 채번 지점이 같은 문장을 같은 번호로
            // 가리키는가**이다. 커서 원천에 WHERE를 둔 목적도 커버리지가 아니라
            // 그 대조에 쓸 재료를 만드는 것이다.
            //
            // 위 잠금 힌트 단언과 이 블록은 **각자** `scopeSelectLines`(DML 범위 표)를
            // 기준으로 맞댄다 - 서로의 전제가 아니다. 실측으로 확인했다: 위 `{1,3}`
            // 단언과 그 아래 `Assert.All(hintSelects, ...)` 둘을 주석 처리해도 이
            // 블록은 그대로 통과한다. 그런데도 그 둘이 중복이 아닌 이유는,
            // 네 표의 합의가 "각 표가 같은 기준(DML 범위 표)과 짝이 맞는다"의 이행으로
            // 성립하기 때문이다 - 잠금 힌트 쪽 짝을 지우면 그 표만 합의에서 조용히
            // 빠지고, 이 블록은 그 사실을 알려 주지 않는다.
            //
            // (참조 함수 표는 이 문장에서 담을 것이 없다 - 알려진 함수 호출이 없어
            //  위 Assert.Single이 TVF 행 하나만 남긴다. 그 표가 덮는 것은 SELECT 2다.)
            //
            // 이 표의 채번만 갈라져 FROM 없는 `SELECT @a = 1`까지 세는 날, 행 수는
            // 그대로다(그 문장은 WHERE가 없어 낼 행이 없다) - 그래서 Assert.Single은
            // 그 어긋남을 잡지 못하고, 아래 짝 단언만이 잡는다.
            var predicateSelects = DmlScopeExtractor.ExtractSetPredicates(ddl)
                .Where(f => f.Operation == "SELECT")
                .ToArray();

            var cursorPredicate = Assert.Single(predicateSelects);

            // 짝을 먼저 맞댄다 - 위 참조 함수 블록과 같은 이유다(번호를 먼저 단언하면
            // 채번이 갈라진 날 그쪽이 먼저 터져 라인 비교가 실행되지 않는다).
            Assert.Equal(scopeSelectLines[cursorPredicate.StatementOrdinal - 1], cursorPredicate.Line);
            Assert.Equal(3, cursorPredicate.StatementOrdinal);
        }

        [Fact]
        public void ExtractSetPredicates_TopLevelNotIn_ShouldCaptureEveryLiteral()
        {
            // EXPECT_PROC 갱신 1(object_definition.sql:39) 실측 형태. 명세서는 이 9개
            // 자리에 5개짜리 다른 목록을 그럴듯한 대체물로 채워 넣었다 - 집합의 크기와
            // 원소는 컬럼 이름으로 추측할 수 없다는 것이 이 재료의 존재 이유다.
            var ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    UPDATE A SET A.InState = 1
    FROM   dbo.TSettleMst A
    WHERE  A.YMD = @pi_strYMD
    AND    A.PGName NOT IN ('PLCard','SamSungPay','SSGPayCard','KakaoPay','KakaoCard','impaymobile','NaverCard','ApplePay','TossCardAuth')
END";

            // [1행 → 2행 - 2026-08-22 축 A 재감사 ③ Task 6] 행이 하나 늘었지 분해가
            // 사라진 것이 아니다. 행 단위가 최상위 AND 항이 되면서 `A.YMD = @pi_strYMD`
            // 항이 원문 전용 행으로 자리를 얻었다(설계 §3 결정 3). NOT IN 항의 분해는
            // 아래 단언이 그대로 못 박는다 - 원문 전용 행이 분해되는 행을 삼키면
            // 여기서 잡힌다.
            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);
            Assert.Equal(2, facts.Count);

            var fact = Assert.Single(facts, f => f.Operator == "NOT IN");

            Assert.Equal("UPDATE", fact.Operation);
            // Column은 원문 표기 그대로다(한정자 포함) - 아래
            // SameLastSegmentDifferentQualifiers 테스트가 그 이유(키 충돌 방지)를
            // 실측 코퍼스로 증명한다.
            Assert.Equal("A.PGName", fact.Column);
            Assert.True(fact.IsNegated);
            Assert.Equal(9, fact.Literals.Count);
            Assert.Equal("'PLCard'", fact.Literals[0]);
            Assert.Contains("'SSGPayCard'", fact.Literals);
            Assert.Contains("'KakaoCard'", fact.Literals);

            var textOnly = Assert.Single(facts, f => f.Column == "—");
            Assert.Equal("A.YMD = @pi_strYMD", textOnly.PredicateText);
            Assert.Empty(textOnly.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_PositiveInWithNumbers_ShouldKeepRawLiterals()
        {
            // 숫자 리터럴도 담는다. 표에서 대조하므로 앵커 문제가 생기지 않는다
            // (설계 §5.1 - 산문에서 "0"을 찾는 것이 아니다).
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DELETE FROM dbo.T WHERE UseState IN (0, 1)
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("DELETE", fact.Operation);
            Assert.Equal("UseState", fact.Column);
            Assert.False(fact.IsNegated);
            Assert.Equal(new[] { "0", "1" }, fact.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_SubqueryIn_ShouldNotBeDecomposed()
        {
            // 집합이 리터럴이 아니므로 옮겨 적을 목록 자체가 없다(설계 §3.2).
            //
            // [0행 → 1행 - 2026-08-22 축 A 재감사 ③ Task 6] 행이 하나 늘었지 분해가
            // 사라진 것이 아니다(애초에 분해되는 행이 없었다). 항은 이제 원문 전용
            // 행으로 자리를 얻는다 - 리터럴 칸은 여전히 비어 있으므로 "거짓 집합을
            // 싣지 않는다"는 §3.2의 결정은 그대로다.
            //
            // [정직한 범위 고지 - 리뷰 라운드 1 Important 1] 이 테스트는 파이프라인
            // 전체("서브쿼리 IN은 결과에 나타나지 않는다")를 증명하는 창발적
            // 단언이지 RecordSetPredicate의 `node.Subquery != null` 가드 한 줄을
            // 격리해서 증명하지 않는다 - T-SQL 문법상 `IN (서브쿼리)`와 `IN (값
            // 목록)`은 서로 다른 생산 규칙이라 파싱 결과에서 Subquery와 Values가
            // 동시에 채워지는 경우가 없고(ScriptDom이 이 불변식을 지킨다), 그래서
            // 이 DDL로는 `node.Values == null` 검사 하나만으로도 이미 걸러진다.
            // 그 가드가 왜 그래도 코드에 명시적으로 남아 있는지는
            // RecordSetPredicate의 XML 주석에 적었다 - 요약하면 "ScriptDom의
            // 내부 불변식에 이 메서드의 계약을 얹지 않기 위해서"다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.T SET C = 1 WHERE PLTID IN (SELECT PLTID FROM dbo.S)
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("—", fact.Column);
            Assert.Equal("—", fact.Operator);
            Assert.Empty(fact.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_MixedValues_ShouldNotBeDecomposed()
        {
            // 원소에 리터럴 아닌 것이 하나라도 섞이면 리터럴 칸에 담지 않는다 -
            // 리터럴 집합으로 렌더하면 명세서에 거짓 집합이 실린다(설계 §3.2).
            //
            // [0행 → 1행 - Task 6] 행이 하나 늘었지 분해가 사라진 것이 아니다.
            // 항은 원문 전용 행으로 실리고 리터럴 칸은 비어 있다 - 'PLCard' 하나만
            // 담아 집합을 반쪽으로 만드는 일이 없다는 것을 아래에서 직접 못 박는다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = 1 FROM dbo.T A JOIN dbo.S B ON A.Id = B.Id WHERE A.PGName IN ('PLCard', B.PGName)
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("—", fact.Column);
            Assert.Empty(fact.Literals);
            Assert.Equal("A.PGName IN ('PLCard', B.PGName)", fact.PredicateText);
        }

        [Fact]
        public void ExtractSetPredicates_InsideScalarSubquery_ShouldNotLeakTheInnerIn()
        {
            // "최상위"의 정의는 TopLevelPredicateCollector가 갖는다 - 스칼라 서브쿼리
            // 안의 IN은 대상 범위를 정하지 않는다.
            //
            // [0행 → 1행 - Task 6] 바깥 항(`Amt = (SELECT ...)`)이 원문 전용 행으로
            // 자리를 얻는다. 그 안의 `PGName IN ('A','B')`는 여전히 자기 행을 갖지
            // 않는다 - 이 테스트가 지키는 경계는 그쪽이므로 아래에서 직접 단언한다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.T SET C = 1
    WHERE  Amt = (SELECT MAX(Amt) FROM dbo.S WHERE PGName IN ('A','B'))
END";

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("—", fact.Column);
            Assert.Empty(fact.Literals);
            Assert.DoesNotContain(facts, f => f.Column.Equals("PGName", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ExtractSetPredicates_TwoInPredicatesInOneStatement_ShouldKeepBoth()
        {
            // 한 문장에 IN이 둘일 수 있다 - 그래서 L1의 행 키가 라인 하나로는
            // 부족하고 라인+컬럼이어야 한다(설계 §5).
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.T SET C = 1 WHERE PGName IN ('A','B') AND UseState IN (0,1)
END";

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Contains(facts, f => f.Column == "PGName");
            Assert.Contains(facts, f => f.Column == "UseState");
            Assert.All(facts, f => Assert.Equal(5, f.Line));
        }

        [Fact]
        public void ExtractSetPredicates_SameLastSegmentDifferentQualifiers_ShouldKeepDistinctColumns()
        {
            // 리뷰 라운드 1 Important 3 - 코디네이터 결정. 실측 코퍼스
            // (output/Objects/dbo.UP_Util_PG_Client_CMRate_Ins.Procedure/raw/object_definition.sql:97-98)
            // 는 같은 INSERT 원천 SELECT의 같은 WHERE 최상위에서
            // A.USESTATE IN (0,4,5,6)과 B.USESTATE IN (0,4)를 나란히 쓴다. Column이
            // 마지막 식별자 조각만 담으면 둘 다 "UseState"가 되어 (Operation, Line,
            // Column) 키가 충돌하고, Task 3의 L1이 라인+컬럼으로 행을 찾을 때 하나가
            // 엉뚱한 행에 매칭된다. 한정자를 포함해야 키가 코퍼스에서 유일해진다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = 1
    FROM   dbo.T A JOIN dbo.S B ON A.Id = B.Id
    WHERE  A.UseState IN (0,4,5,6) AND B.UseState IN (0,4)
END";

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Contains(facts, f => f.Column == "A.UseState");
            Assert.Contains(facts, f => f.Column == "B.UseState");
        }

        // [범위 결정 재정당화 - 2026-08-19 축 A 감사]
        // 이 테스트는 원래 "스칼라 리터럴 비교는 담지 않는다"(설계 §2)를 잠갔고, 그 주석은
        // 누군가 이 범위를 넓히면 테스트를 깨뜨려 결정을 다시 정당화하도록 요구했다.
        // 축 A 감사가 그 요구를 충족하는 세 가지 사실을 냈다.
        //
        // (1) 실제 피해: 감사에서 나온 대상 행 집합 결함 4건 중 둘이 `CommissionCancelFlag = 1`
        //     (COMM_UPD:169·243)이었다. 등호가 수집 대상 밖이라 L1이 대조할 재료조차 없었고,
        //     취소수수료 미부과 계약을 걸러내는 필터가 명세서에서 통째로 사라졌는데 아무
        //     검사도 울리지 않았다.
        // (2) 옛 논거의 반증: "INSTATE = 0은 컬럼 이름만 봐도 존재를 안다"고 했으나, 명세서
        //     DML 범위표에 컬럼명이 실려 있었는데도 `= 1`이라는 값이 문서 전체에 없었다.
        //     이름만으로는 어느 값이 대상을 가르는지 알 수 없다.
        // (3) 부피 재측정: 옛 주석의 474건은 파라미터·컬럼 비교까지 센 값이다. 우변이
        //     리터럴인 것만 파서로 세면 119건이고, 표 전체는 79 → 198행으로 2.5배다
        //     (SP당 평균 14행, 최대 40행). 5배가 아니다.
        //
        // 그래서 범위를 넓히되 경계는 유지한다 - 우변이 리터럴일 때만 담는다.
        // 파라미터·컬럼 비교는 여전히 제외이며 그 회귀는 별도 Theory가 잠근다.
        [Fact]
        public void ExtractSetPredicates_ComparisonsAgainstLiterals_ShouldNowBeCaptured()
        {
            // 설계 §2. 코퍼스 실측에서 스칼라 리터럴 비교는 474건(집합 리터럴은 약
            // 104건)이라, 담으면 부피가 5배가 되고 "값까지 대조하면 노이즈"라는 축 B의
            // 기존 판단이 그대로 옳은 지점이 된다. 둘을 가르는 것은 구조다 -
            // INSTATE = 0은 컬럼 이름만 봐도 존재를 알지만, 집합의 크기와 원소는
            // 컬럼 이름으로 추측할 수 없다.
            //
            // [정직한 범위 고지 - 리뷰 라운드 1 Important 2] 이 DDL에는 InPredicate가
            // 하나도 없다. RecordSetPredicate는 오직 ExplicitVisit(InPredicate)에서만
            // 호출되므로, 이 픽스처는 RecordSetPredicate의 어떤 가드도 실행하지
            // 않는다 - RecordSetPredicate 본문을 통째로 지워도 이 테스트는 똑같이
            // 통과한다. 이 테스트가 실제로 증명하는 것은 "스칼라 비교는 IN 파이프라인
            // 바깥이라 애초에 방문되지 않는다"는 구조적 사실이다(설계 §2 범위 결정의
            // 회귀 가드) - 나중에 누군가 스칼라 비교까지 담는 코드를 별도로 추가하면
            // (예: BooleanComparisonExpression을 집합 사실로 취급) 그때 이 테스트가
            // 깨져서 §2의 범위 결정을 다시 정당화하도록 요구한다.
            var ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    UPDATE dbo.T SET C = 1
    WHERE  YMD = @pi_strYMD AND InState = 0 AND PGName = 'PLCard' AND UseState <> 1
END";

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            // 리터럴을 우변에 둔 셋만 분해한다. `YMD = @pi_strYMD`는 옮겨 적을 리터럴이 없다.
            //
            // [3행 → 4행 - 2026-08-22 축 A 재감사 ③ Task 6] 행이 하나 늘었지 분해가
            // 사라진 것이 아니다. 분해되던 셋은 아래에서 그대로 단언한다. 늘어난 하나는
            // `YMD = @pi_strYMD` 항의 원문 전용 행이고, 그래도 컬럼 칸에 "YMD"가 실리지
            // 않는다는 이 테스트의 원래 경계는 마지막 단언이 그대로 지킨다.
            Assert.Equal(4, facts.Count);
            Assert.Contains(facts, f => f.Column == "InState" && f.Operator == "=" && f.Literals[0] == "0");
            Assert.Contains(facts, f => f.Column == "PGName" && f.Operator == "=" && f.Literals[0] == "'PLCard'");
            Assert.Contains(facts, f => f.Column == "UseState" && f.Operator == "<>" && f.Literals[0] == "1");
            Assert.DoesNotContain(facts, f => f.Column == "YMD");

            var textOnly = Assert.Single(facts, f => f.Column == "—");
            Assert.Equal("YMD = @pi_strYMD", textOnly.PredicateText);
            Assert.Empty(textOnly.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_NullDdl_ShouldReturnEmpty()
        {
            Assert.Empty(DmlScopeExtractor.ExtractSetPredicates(null));
        }

        // === 수집 범위 확장 (2026-08-19 축 A 감사 실측) =======================
        //
        // 감사에서 나온 🟠 4건이 전부 "원본 필터가 명세서 어디에도 없다"는 한 부류였고,
        // 넷 다 이 추출기가 사실을 내지 않아 L1이 대조할 재료조차 갖지 못한 자리였다.
        // 원본 코퍼스 기준으로 등호·부등호 리터럴 비교 129건, ISNULL 래핑 IN 13건이
        // 수집 대상 밖이었고, 파생 테이블 내부 술어는 형태를 막론하고 빠졌다.

        [Fact]
        public void ExtractSetPredicates_InWithWrappedLeftSide_ShouldStillCapture()
        {
            // EXCEPTION_PROC:423 실측. 좌변이 ISNULL 호출이라 ColumnReference가
            // 아니어서 통째로 버려졌다 - MobileCo가 NULL·기타값인 건까지 갱신 대상이
            // 되는데 명세서에는 리터럴 집합이 실리지 않았다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.CLComm = 0
    FROM dbo.TSettleMst A
    WHERE ISNULL(A.MobileCo,'') IN ('1','2')
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("IN", fact.Operator);
            Assert.Contains("MobileCo", fact.Column);
            Assert.Equal(2, fact.Literals.Count);
        }

        [Fact]
        public void ExtractSetPredicates_InWithLeftFunctionCall_ShouldStillCapture()
        {
            // EXPECT_PROC:146·168 실측. 좌변이 LEFT(...) 호출인데 ScriptDom은 이것을
            // FunctionCall이 아니라 전용 노드(LeftFunctionCall)로 판다. 그래서 위
            // ISNULL 사례는 잡히는데 이 형태만 통째로 빠졌다 - 같은 SP에서 ISNULL
            // 래핑은 정상 수집됐으므로 "래핑을 원천적으로 못 담는" 것이 아니라
            // LEFT/RIGHT 한정 누락이다.
            //
            // 2026-08-20 축 A 감사의 🟡. 통신군(C)과 금융·상품권군(A,B)을 가르는
            // 필터가 「집합 술어 (기계 확정 — 수정 금지)」 표에서 빠졌다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.CollectFlag = 1
    FROM dbo.TSettleMst A
    JOIN dbo.TPayTool D ON A.ToolID = D.ToolID
    WHERE LEFT(D.PayToolType,1) IN ('A','B')
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("IN", fact.Operator);
            Assert.Contains("PayToolType", fact.Column);
            Assert.Equal(2, fact.Literals.Count);
        }

        [Fact]
        public void ExtractSetPredicates_MultiLineLeftSide_ShouldBeCollapsedToOneLine()
        {
            // 2026-08-20 리뷰 Important. 좌변 원문에 개행이 있으면 통과 불가능한
            // L1 실패가 난다. 프롬프트는 EscapeTableCell이 개행을 공백으로 접어
            // 한 줄로 싣는데(AiService), 검증기는 접지 않은 원문과 셀을 대조한다
            // (MechanicalValidator.CheckSetPredicates). 모델이 지시대로 표를 축자로
            // 옮겨도 두 문자열이 영영 같아지지 않는다 - 어떤 산출물도 만족시킬 수 없는
            // 요구가 된다.
            //
            // 좌변을 노드 타입이 아니라 컬럼 유무로 받게 넓힌 뒤로 CASE·산술식처럼
            // 여러 줄로 쓰이는 형태가 흔해져, 이론상 위험이 실제 위험이 됐다.
            // 재료를 만들 때 한 줄로 접어 두면 양쪽이 같은 것을 본다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.Flag = 1
    FROM dbo.TSettleMst A
    WHERE CASE WHEN A.PayType = 1
               THEN 'a'
               ELSE 'b'
          END IN ('a','b')
END";

            // [2행 → 1행 - 2026-08-22 축 A 재감사 ③ Task 6] 예전에는 바깥 IN 팩트와
            // 안쪽 분기 조건(A.PayType = 1) 팩트를 함께 냈다. 행 단위가 최상위 AND
            // 항으로 올라가면서 안쪽 분기 조건은 자기 행을 갖지 않는다 - 그것은 항이
            // 아니라 이 항의 좌변을 이루는 조각이고, 그 원문은 이 행의 컬럼 칸과
            // 원문 칸에 이미 통째로 실린다. 여기서 사라진 것은 **분해되던 최상위 항이
            // 아니라 항의 부분식**이므로 "원문 전용 행이 분해되는 행을 삼킨다"는
            // 회귀가 아니다. 바깥 항의 분해(컬럼·IN·원소 둘)는 아래에서 그대로 잠근다.
            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            var fact = Assert.Single(facts);
            Assert.StartsWith("CASE", fact.Column, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("IN", fact.Operator);
            Assert.Equal(new[] { "'a'", "'b'" }, fact.Literals);

            Assert.DoesNotContain("\n", fact.Column);
            Assert.DoesNotContain("\r", fact.Column);
            Assert.DoesNotContain("  ", fact.Column);
            Assert.Contains("PayType", fact.Column);
        }

        [Fact]
        public void ExtractSetPredicates_EqualityAgainstLiteral_ShouldBeCaptured()
        {
            // COMM_UPD:169 실측. 취소수수료 미부과 계약을 걸러내는 필터인데
            // 등호는 수집 대상이 아니어서 `= 1`이라는 값이 명세서 전체에 없었다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.PGCOMM = 0
    FROM dbo.TSettleMst A
    WHERE A.CommissionCancelFlag = 1
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("=", fact.Operator);
            Assert.Equal("A.CommissionCancelFlag", fact.Column);
            Assert.Equal(new[] { "1" }, fact.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_InequalityAgainstLiteral_ShouldBeCaptured()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.PGCOMM = 0
    FROM dbo.TSettleMst A
    WHERE A.UseState <> 1
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("<>", fact.Operator);
            Assert.Equal(new[] { "1" }, fact.Literals);
        }

        // 파라미터·컬럼 비교는 옮겨 적을 리터럴이 없으므로 컬럼·연산·원소 칸에
        // 담지 않는다. 담으면 표가 조인 키와 기준일 비교로 뒤덮여 진짜 리터럴 집합이
        // 묻힌다.
        //
        // [0행 → 1행 - 2026-08-22 축 A 재감사 ③ Task 6] 행이 하나 늘었지 분해가
        // 사라진 것이 아니다. 항은 원문 전용 행으로 자리를 얻고 리터럴 칸은 그대로
        // 비어 있다 - 두 결정이 충돌하지 않는다(설계 §2.2 · §3 결정 3). 오히려
        // `A.YMD = A.AYMD`류가 조인 키 그물과 집합 술어 그물 사이로 새던 🟠이
        // 이 행으로 닫힌다.
        [Theory]
        [InlineData("A.YMD = @pi_strYMD")]
        [InlineData("A.PLTID = B.PLTID")]
        public void ExtractSetPredicates_ComparisonWithoutALiteral_ShouldNotBeDecomposed(string predicate)
        {
            var ddl = $@"
CREATE PROCEDURE dbo.P @pi_strYMD VARCHAR(8) AS
BEGIN
    UPDATE A SET A.PGCOMM = 0
    FROM dbo.TSettleMst A, dbo.TSettleMst B
    WHERE {predicate}
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("—", fact.Column);
            Assert.Equal("—", fact.Operator);
            Assert.Empty(fact.Literals);
            Assert.Equal(predicate, fact.PredicateText);
        }

        [Fact]
        public void ExtractSetPredicates_InsideADerivedTable_ShouldBeCapturedWithItsAlias()
        {
            // EXCEPTION_PROC:375 · COMM_UPD:243 실측. 파생 테이블 안의 필터도
            // 대상 행 집합을 좁히는데 최상위 WHERE만 훑어 사실이 하나도 나오지 않았다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.PGCOMM = X.Amt
    FROM dbo.TSettleMst A
    INNER JOIN (SELECT B.PLTID, SUM(B.Amt) AS Amt FROM dbo.TSettleMst B
                WHERE B.CommissionCancelFlag = 1 GROUP BY B.PLTID) X
        ON A.PLTID = X.PLTID
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("파생 테이블 X", fact.Scope);
            Assert.Equal("B.CommissionCancelFlag", fact.Column);
            Assert.Equal("=", fact.Operator);
        }

        // 같은 컬럼이 최상위와 파생 테이블에 각각 걸리면 사실이 둘이어야 한다.
        // 하나로 합치면 L1이 한쪽만 대조하고 다른 쪽 누락을 통과시킨다.
        [Fact]
        public void ExtractSetPredicates_SameColumnInBothScopes_ShouldYieldTwoFacts()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.PGCOMM = X.Amt
    FROM dbo.TSettleMst A
    INNER JOIN (SELECT B.PLTID, SUM(B.Amt) AS Amt FROM dbo.TSettleMst B
                WHERE B.UseState = 1 GROUP BY B.PLTID) X
        ON A.PLTID = X.PLTID
    WHERE A.UseState = 0
END";

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Contains(facts, f => f.Scope == "최상위" && f.Literals[0] == "0");
            Assert.Contains(facts, f => f.Scope == "파생 테이블 X" && f.Literals[0] == "1");
        }

        // 기존 IN 사실도 새 필드를 채워야 한다 - 표의 연산 칸과 범위 칸이 비면
        // L1이 행을 찾지 못한다.
        [Fact]
        public void ExtractSetPredicates_PlainTopLevelIn_ShouldFillOperatorAndScope()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.PGCOMM = 0 FROM dbo.TSettleMst A WHERE A.UseState NOT IN (0,4)
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("NOT IN", fact.Operator);
            Assert.Equal("최상위", fact.Scope);
        }

        // 분해되는 항은 지금처럼 컬럼·연산·원소를 채우면서 원문 칸도 함께 진다
        // (계획 Task 5 · 설계 §4 C). 분해가 원문을 대체하는 것이 아니라 둘이 나란히
        // 실려야, 독자가 표의 분해를 원문과 대조할 수 있다.
        [Fact]
        public void ExtractSetPredicates_DecomposableTerm_ShouldCarryBothDecompositionAndText()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TSettleMst A
    WHERE  A.PGNAME IN ('KFTC', 'YELOPAY')
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("A.PGNAME", fact.Column);
            Assert.Equal("IN", fact.Operator);
            Assert.Equal(new[] { "'KFTC'", "'YELOPAY'" }, fact.Literals);
            Assert.Equal("A.PGNAME IN ('KFTC', 'YELOPAY')", fact.PredicateText);
        }

        // 리터럴 우변 비교(`=`·`<>`)도 같은 원문 칸을 진다 - 이 갈래는 InPredicate가
        // 아니라 BooleanComparisonExpression에서 사실이 나므로, IN 테스트가 통과해도
        // 이 자리는 따로 비어 있을 수 있다.
        [Fact]
        public void ExtractSetPredicates_ComparisonTerm_ShouldCarryItsText()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TSettleMst A
    WHERE  A.CommissionCancelFlag = 1
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("=", fact.Operator);
            Assert.Equal("A.CommissionCancelFlag = 1", fact.PredicateText);
        }

        // 원문 칸은 CollapseWhitespace를 거친다(좌변이 이미 같은 규칙으로 접힌다).
        //
        // [이 픽스처는 코퍼스에 없는 형태를 방어한다 - 2026-08-22 전수 프로브]
        // 여러 줄에 걸친 항은 현재 코퍼스에 하나도 없다: 수집 판정을 통과하는 항
        // 373건(실제 수집 범위보다 넓게 잡은 상위집합) 중 개행을 포함한 항이 0건이고,
        // 여러 줄 IN 두 자리(COMM_UPD:141, EXCEPTION_PROC:527)는 둘 다 서브쿼리 IN이라
        // 이 표가 담지 않는다. 그래도 픽스처를 남기는 이유는
        // 개행이 든 값이 실리면 렌더된 칸과 접지 않은 원문이 어긋나기 때문이다
        // (SetPredicateFact.PredicateText 문서 참고) - 실측으로 요구되는 접기는
        // 줄 안의 공백 쪽이고, 그쪽은 AxisAGoldenCaseTests의 COMM_UPD:77 단언이
        // 실물로 못 박는다.
        [Fact]
        public void ExtractSetPredicates_MultiLineTerm_ShouldCollapseWhitespaceInText()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TSettleMst A
    WHERE  A.PGNAME IN ('KFTC',
                        'YELOPAY')
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.DoesNotContain("\n", fact.PredicateText);
            Assert.Equal("A.PGNAME IN ('KFTC', 'YELOPAY')", fact.PredicateText);
        }

        // 라인은 문장 시작줄이 아니라 그 항 자신의 줄이다(계획 Task 5 · 설계 §4 C).
        // 문장 줄을 쓰면 EXCEPTION_PROC의 UPDATE 7처럼 한 문장의 술어가 전부 210으로
        // 찍혀 독자가 원문에서 어느 항인지 못 찾는다. 잠금 힌트 표가 같은 이유로 이미
        // 참조 노드 자신의 줄을 쓴다
        // (ExtractLockHints_ReferencesOnDifferentLines_EachReportsItsOwnLine).
        [Fact]
        public void ExtractSetPredicates_TermsOnDifferentLines_EachReportsItsOwnLine()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TSettleMst A
    WHERE  A.PGNAME IN ('KFTC')
    AND    A.UseState = 0
END";

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            var inFact = Assert.Single(facts, f => f.Operator == "IN");
            var equalsFact = Assert.Single(facts, f => f.Operator == "=");

            // UPDATE 키워드는 5행, IN 항은 8행, 등호 항은 9행이다.
            Assert.Equal(8, inFact.Line);
            Assert.Equal(9, equalsFact.Line);
            // 문장 줄로 되돌아가면 두 행이 모두 5가 된다 - 그 회귀를 직접 막는다.
            Assert.DoesNotContain(facts, f => f.Line == 5);
        }

        // === 분해되지 않는 항도 행을 낸다 (2026-08-22 축 A 재감사 ③ Task 6) ========
        //
        // 행 단위가 "분해된 원소"에서 "최상위 AND 항"으로 올라간다. 분해되는 항은
        // 지금처럼 컬럼·연산·리터럴을 채우고, 분해되지 않는 항은 그 칸을 `—`로 두고
        // 원문 칸만 채운다(설계 §3 결정 3 · §4 C). 어느 경우에도 항 자체는 빠지지 않는다.

        [Fact]
        public void ExtractSetPredicates_OrCombinedTerm_ShouldProduceOneRowWithTextOnly()
        {
            // EXCEPTION_PROC:210 실측 - 지금은 UseState <> 1과 UseState = 1 두 행이 나란히
            // 실려 AND로 읽히고, 그렇게 읽으면 모순(공집합)이다. A.YMD = A.AYMD 항은
            // 조인 키에서도 집합 술어에서도 빠져 두 그물 사이로 샜다(2026-08-22 축 A 재감사 🟠).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TSettleMst A
    WHERE  (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("—", fact.Column);
            Assert.Equal("—", fact.Operator);
            Assert.Empty(fact.Literals);
            Assert.Equal("(A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))", fact.PredicateText);
        }

        // OR 갈래 안의 AND는 항이 아니다. 안으로 내려가 갈래별 조건을 따로 실으면
        // 그것이 바로 위 테스트가 고치는 오독이므로, 갈래 안의 분해 가능한 비교
        // (A.UseState = 1)가 독립 행으로 새어 나오지 않는 것까지 못 박는다.
        [Fact]
        public void ExtractSetPredicates_OrCombinedTerm_ShouldNotLeakItsBranchesAsRows()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TSettleMst A
    WHERE  (A.UseState <> 1 OR (A.UseState = 1 AND A.YMD = A.AYMD))
END";

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            Assert.DoesNotContain(facts, f => f.Column.Equals("A.UseState", StringComparison.OrdinalIgnoreCase));
            Assert.All(facts, f => Assert.Empty(f.Literals));
        }

        // 괄호 없는 최상위 OR도 한 항이다.
        //
        // [이 픽스처가 따로 필요한 이유 - 변이 실측] 위 두 OrCombinedTerm 픽스처는
        // 원문(EXCEPTION_PROC:210)을 따라 바깥 괄호가 있다. 그래서 평탄화가 OR로
        // 내려가지 못하게 막는 것이 "괄호 안이 AND일 때만 벗긴다"는 괄호 갈래이지
        // AND 갈래가 아니다. AND 평탄화를 OR까지 하도록 바꿔 놓아도 이 파일의 다른
        // 테스트가 전부 통과하는 것을 확인했다 - 그 회귀를 잡는 것은 이 픽스처뿐이다.
        [Fact]
        public void ExtractSetPredicates_UnparenthesizedTopLevelOr_ShouldStayOneRow()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A
    WHERE  A.UseState = 0 OR A.PGNAME IN ('KFTC')
END";

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("—", fact.Column);
            Assert.Equal("—", fact.Operator);
            Assert.Empty(fact.Literals);
            Assert.Equal("A.UseState = 0 OR A.PGNAME IN ('KFTC')", fact.PredicateText);
        }

        [Fact]
        public void ExtractSetPredicates_InequalityTerm_ShouldProduceRow()
        {
            // EXCEPTION_PROC:320 실측 - 수집 연산자가 =·<>·IN 셋뿐이라 >= 가 0행이었다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A
    WHERE  A.AYMD >= '20230101'
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("A.AYMD >= '20230101'", fact.PredicateText);
            Assert.Equal("—", fact.Column);
            Assert.Equal("—", fact.Operator);
            Assert.Empty(fact.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_ArithmeticRightHandSide_ShouldProduceRow()
        {
            // EXCEPTION_PROC:442 실측 - 우변이 리터럴이 아니라 어떤 표에도 없었다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A
    WHERE  TxAmt != CardAmt+CouponAmt+MoneyAmt+PointAmt
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("TxAmt != CardAmt+CouponAmt+MoneyAmt+PointAmt", fact.PredicateText);
            Assert.Equal("—", fact.Column);
            Assert.Empty(fact.Literals);
        }

        // 설계 §2.2의 🟠 - 같은 별칭 안의 비교라 조인 키에서 의도적으로 빠지고,
        // 우변이 리터럴이 아니라 집합 술어에서 구조적으로 빠져 두 그물 사이로 샜다.
        // 원문 전용 행이 그 자리를 메운다.
        [Fact]
        public void ExtractSetPredicates_ColumnToColumnTerm_ShouldProduceTextOnlyRow()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A
    WHERE  A.YMD = A.AYMD
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("—", fact.Column);
            Assert.Equal("—", fact.Operator);
            Assert.Empty(fact.Literals);
            Assert.Equal("A.YMD = A.AYMD", fact.PredicateText);
        }

        [Fact]
        public void ExtractSetPredicates_MixedTerms_ShouldKeepDecomposableOnesDecomposed()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A
    WHERE  A.PGNAME IN ('KFTC', 'YELOPAY')
    AND    (A.UseState <> 1 OR A.YMD = A.AYMD)
END";

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            Assert.Equal(2, facts.Count);

            var decomposed = Assert.Single(facts, f => f.Column == "A.PGNAME");
            Assert.Equal(new[] { "'KFTC'", "'YELOPAY'" }, decomposed.Literals);

            var textOnly = Assert.Single(facts, f => f.Column == "—");
            Assert.Equal("(A.UseState <> 1 OR A.YMD = A.AYMD)", textOnly.PredicateText);
        }

        // 원문 전용 행이 분해되는 행을 삼키면 안 된다(계획 Task 6의 제약). 괄호는
        // 의미가 아니라 표기이므로, 괄호 하나로 감싼 항이 원문 전용으로 떨어지면
        // 그 항의 리터럴 집합이 표에서 통째로 사라진다 - 이 표가 존재하는 이유가
        // 바로 그 집합이다.
        [Fact]
        public void ExtractSetPredicates_ParenthesizedDecomposableTerm_ShouldStillDecompose()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A
    WHERE  (A.PGNAME IN ('KFTC', 'YELOPAY'))
    AND    (A.UseState = 0)
END";

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            Assert.Equal(2, facts.Count);

            var inFact = Assert.Single(facts, f => f.Operator == "IN");
            Assert.Equal("A.PGNAME", inFact.Column);
            Assert.Equal(new[] { "'KFTC'", "'YELOPAY'" }, inFact.Literals);

            var equalsFact = Assert.Single(facts, f => f.Operator == "=");
            Assert.Equal("A.UseState", equalsFact.Column);
            Assert.Equal(new[] { "0" }, equalsFact.Literals);

            // 괄호를 벗기는 것은 **분해 판정뿐**이고 원문 칸은 괄호까지 원본 그대로
            // 진다. 두 갈래가 한 메서드에 있지 않으므로(평탄화는 TopLevelAndTerms,
            // 벗기기는 TryDecompose) 한쪽만 무너질 수 있고, 위 세 단언은 그 경우를
            // 잡지 못한다 - 컬럼·연산·원소가 다 맞아도 원문에서 괄호만 사라진다.
            //
            // Task 7의 L1이 원문 칸을 행 키로 쓰므로(설계 §4 E) 그렇게 되면 모델이
            // 표를 축자로 옳게 옮겨도 검증기가 틀렸다고 한다 - 어떤 산출물도 만족시킬
            // 수 없는 요구가 되는, 이 파일의 주석들이 거듭 경고하는 그 실패 모양이다.
            Assert.Equal("(A.PGNAME IN ('KFTC', 'YELOPAY'))", inFact.PredicateText);
            Assert.Equal("(A.UseState = 0)", equalsFact.PredicateText);
        }

        // 파생 테이블 안의 항도 같은 규칙을 받는다 - 범위 칸이 최상위든 파생이든
        // 행 단위는 그 스코프의 최상위 AND 항이다.
        [Fact]
        public void ExtractSetPredicates_UndecomposableTermInsideDerivedTable_ShouldCarryItsScope()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.PGCOMM = X.Amt
    FROM dbo.TSettleMst A
    INNER JOIN (SELECT B.PLTID, SUM(B.Amt) AS Amt FROM dbo.TSettleMst B
                WHERE B.YMD = B.AYMD GROUP BY B.PLTID) X
        ON A.PLTID = X.PLTID
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("파생 테이블 X", fact.Scope);
            Assert.Equal("—", fact.Column);
            Assert.Equal("B.YMD = B.AYMD", fact.PredicateText);
        }

        [Fact]
        public void ExtractSetPredicates_StandaloneSelectWhere_ShouldBeCollected()
        {
            // UF_GET_COLLECTYMD:100 실측 - `CollectFlag = 1`은 리터럴 우변 등치라 이 표가
            // 담을 수 있는 형태인데, 독립 SELECT라 방문조차 되지 않아 표에 오지 못했다.
            // "회수구분이 1(자동회수)인 행만 조회한다"는 사실이 어떤 기계 확정 표에도
            // 없고 산문에만 있었다(설계 §2.2).
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT
AS
BEGIN
    DECLARE @v INT
    SELECT @v = CollectDay
    FROM   dbo.TPGCollectPeriodMst WITH(NOLOCK)
    WHERE  CollectFlag = 1
    RETURN @v
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("SELECT", fact.Operation);
            Assert.Equal(1, fact.StatementOrdinal);
            Assert.Equal("CollectFlag", fact.Column);
            Assert.Equal("=", fact.Operator);
            Assert.Equal("최상위", fact.Scope);
            Assert.Equal(new[] { "1" }, fact.Literals);
            Assert.Equal("CollectFlag = 1", fact.PredicateText);
        }

        [Fact]
        public void ExtractSetPredicates_StandaloneSelectWithoutFrom_ShouldNotConsumeAnOrdinal()
        {
            // 훑을 자리가 없는 `SELECT @a = 1`이 번호를 삼키면 뒤 문장의 `SELECT n`이
            // 조용히 밀려, 같은 번호가 표마다 다른 문장을 가리킨다. 판정은 네 방문자가
            // 공유하는 HasFromClause 하나다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @a = 1

    SELECT @b = C FROM dbo.TA WITH(NOLOCK) WHERE X = 7
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("SELECT", fact.Operation);
            Assert.Equal(1, fact.StatementOrdinal);
            Assert.Equal(new[] { "7" }, fact.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_StandaloneSelectDerivedTable_ShouldCarryItsScope()
        {
            // 독립 SELECT 안의 파생 테이블 WHERE도 그 질의가 훑는 행을 좁힌다 -
            // DML 문장에서 이미 담던 것과 같은 범위 표기를 쓴다(설계 §4 B).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT X.Amt
    FROM   (SELECT B.PLTID, SUM(B.Amt) AS Amt FROM dbo.TSettleMst B
            WHERE B.UseState = 0 GROUP BY B.PLTID) X
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("SELECT", fact.Operation);
            Assert.Equal(1, fact.StatementOrdinal);
            Assert.Equal("파생 테이블 X", fact.Scope);
            Assert.Equal("B.UseState", fact.Column);
            Assert.Equal(new[] { "0" }, fact.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_StandaloneSelect_ShouldNotShiftDmlOrdinals()
        {
            // 채번은 연산 이름별로 독립이다 - SELECT 행이 앞에 끼어도 UPDATE 1은
            // 계속 첫 UPDATE다. 이것이 깨지면 집합 술어 표와 DML 범위 표의 같은
            // `UPDATE n`이 다른 문장을 가리킨다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @v = C FROM dbo.TA WITH(NOLOCK) WHERE X = 1

    UPDATE A SET A.Y = 1 FROM dbo.TB A WHERE A.Z = 2
END";

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            var update = Assert.Single(facts, f => f.Operation == "UPDATE");
            Assert.Equal(1, update.StatementOrdinal);
            Assert.Equal(new[] { "2" }, update.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_InsertSourceSelect_ShouldStayInsertOnly()
        {
            // `INSERT ... SELECT`의 원천은 문장 노드(SelectStatement)가 아니라
            // QueryExpression이라 독립 SELECT 방문에 다시 잡히지 않는다 - 잡히면
            // 같은 술어가 `INSERT 1`과 `SELECT 1` 두 행으로 실려 표가 문장 수를
            // 부풀린다. DmlScopeFact.Operation 문서가 적은 AST 모양이 그 근거다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    INSERT INTO dbo.TA (C) SELECT B.C FROM dbo.TB B WITH(NOLOCK) WHERE B.UseState = 0
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

            Assert.Equal("INSERT", fact.Operation);
            Assert.Equal(1, fact.StatementOrdinal);
        }

        [Fact]
        public void ExtractFunctionCalls_ShouldNumberStatementsLikeDmlScopeTable()
        {
            // EXCEPTION_PROC 실측 형태. DML 범위 표가 "UPDATE 1 / UPDATE 2"로 세는 것과
            // 같은 번호가 나와야 두 표를 나란히 읽을 수 있다.
            var ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    UPDATE A SET A.CLVT = dbo.UF_GET_ROUND4VAT(A.CLCOMM)
    FROM   dbo.TSettleMst A
    WHERE  A.YMD = @pi_strYMD

    UPDATE B SET B.PGVT = dbo.UF_GET_ROUND4VAT(B.PGCOMM)
    FROM   dbo.TSettleMst B
    WHERE  B.YMD = @pi_strYMD
END";

            var facts = DmlScopeExtractor.ExtractFunctionCalls(
                ddl, new[] { "UF_GET_ROUND4VAT" });

            Assert.Equal(2, facts.Count);
            Assert.Equal("UPDATE", facts[0].Operation);
            Assert.Equal(1, facts[0].StatementOrdinal);
            Assert.Equal(2, facts[1].StatementOrdinal);
            // 라인도 DML 범위 표와 같은 기준(호출식이 있는 원본 줄)이어야 한다.
            Assert.Equal(5, facts[0].Line);
            Assert.Equal(9, facts[1].Line);
            Assert.All(facts, f => Assert.Equal("UF_GET_ROUND4VAT", f.QualifiedName));
        }

        [Fact]
        public void ExtractFunctionCalls_BuiltInFunctions_ShouldBeSkipped()
        {
            // ISNULL/ROUND/CAST는 Dependencies에 없으므로 knownFunctionNames에도 없다.
            // 이 표는 "어느 사용자 함수를 어디서 부르는가"만 답한다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = ROUND(ISNULL(A.X, 0), 0)
    FROM   dbo.T A
END";

            Assert.Empty(DmlScopeExtractor.ExtractFunctionCalls(
                ddl, new[] { "UF_GET_ROUND4VAT" }));
        }

        [Fact]
        public void ExtractFunctionCalls_InlineTvf_ShouldBeCaptured()
        {
            // 파서의 ReferencedFunctions는 인라인 TVF를 싣지 못한다(2026-08-20 실측:
            // EXPECT_PROC·INS_EXTRA 모두 UIF_SettleYMD가 Dependencies에만 있었다).
            // 이 추출기는 Dependencies에서 온 이름 집합을 쓰므로 그 구멍이 닫힌다.
            var ddl = @"
CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8)
AS
BEGIN
    UPDATE A SET A.OutYMD = (SELECT OutYMD FROM dbo.UIF_SettleYMD(@pi_strYMD, A.PeriodID))
    FROM   dbo.TSettleMst A
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractFunctionCalls(
                ddl, new[] { "UIF_SettleYMD" }));

            Assert.Equal("dbo.UIF_SettleYMD", fact.QualifiedName);
            Assert.Equal("UPDATE", fact.Operation);
            Assert.Equal(1, fact.StatementOrdinal);
        }

        [Fact]
        public void ExtractFunctionCalls_NestedCalls_ShouldCaptureBoth()
        {
            // EXCEPTION_PROC UPDATE 3 실측 형태 - 바깥 ROUND4VAT과 안쪽 두 함수가
            // 모두 나와야 "이 문장이 무엇을 부르는가"가 빠짐없이 전달된다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.CLVT = dbo.UF_GET_ROUND4VAT(dbo.UF_GET_CLIENTSECTIONRATE(A.CLIENTID) * dbo.UF_GET_INCVTAXRATE(A.CLVTType))
    FROM   dbo.TSettleMst A
END";

            var facts = DmlScopeExtractor.ExtractFunctionCalls(
                ddl,
                new[] { "UF_GET_ROUND4VAT", "UF_GET_CLIENTSECTIONRATE", "UF_GET_INCVTAXRATE" });

            var names = facts.Select(f => f.QualifiedName).ToList();
            Assert.Equal(3, facts.Count);
            Assert.Contains("UF_GET_ROUND4VAT", names);
            Assert.Contains("UF_GET_CLIENTSECTIONRATE", names);
            Assert.Contains("UF_GET_INCVTAXRATE", names);
            Assert.All(facts, f => Assert.Equal(1, f.StatementOrdinal));
        }

        [Fact]
        public void ExtractFunctionCalls_StandaloneSelectWithoutFrom_ShouldBeSkipped()
        {
            // [2026-08-23 축 A ③(b)] 예전에는 "독립 SELECT는 통째로 빠진다"가 경계였고
            // 이 테스트가 그것을 못 박았다. 지금 경계는 FROM 유무다 - 독립 SELECT도
            // 방문하되, 훑을 자리가 없는 FROM 없는 대입만 빠진다. 판정은 DML 범위·
            // 잠금 힌트와 공유하는 HasFromClause 하나다.
            //
            // 남는 사실: FROM 없는 대입은 호출을 담고 있어도 행을 내지 않는다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = dbo.UF_GET_ROUND4VAT(100)
END";

            Assert.Empty(DmlScopeExtractor.ExtractFunctionCalls(
                ddl, new[] { "UF_GET_ROUND4VAT" }));
        }

        [Fact]
        public void ExtractFunctionCalls_CallInsideStandaloneSelectList_ShouldBeCollected()
        {
            // COLLECTYMD:53 실측 - 변수 대입 SELECT의 SELECT 목록 안 CASE 식에서 함수를 부른다.
            // 이 호출이 수집되지 않아 참조 함수 표가 아예 생기지 않았고, 표가 없으니 링크도
            // 없어 모델이 산문으로 요약했다 - 그 요약에서 간격 0 특례가 빠진 것이 🔴이다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_strYMD VARCHAR(8)) RETURNS VARCHAR(8)
AS
BEGIN
    DECLARE @v VARCHAR(8)
    SELECT @v = CASE WHEN CollectType = 2
                     THEN dbo.UF_GET_WORKDAY2(@pi_strYMD, CollectDay)
                     ELSE CONVERT(VARCHAR(8), @pi_strYMD, 112)
                END
    FROM   dbo.TPGCollectPeriodMst WITH(NOLOCK)
    WHERE  CollectFlag = 1
    RETURN @v
END";

            var facts = DmlScopeExtractor.ExtractFunctionCalls(ddl, new[] { "UF_GET_WORKDAY2" });

            var fact = Assert.Single(facts);
            Assert.Equal("SELECT", fact.Operation);
            Assert.Equal(1, fact.StatementOrdinal);
            // 스칼라 호출은 맨이름으로 실린다 - ScriptDom의 FunctionCall.FunctionName이
            // 한정자를 담지 않고, 스키마는 렌더러가 Dependencies에서 붙인다
            // (ExtractFunctionCalls_ScalarCall_ShouldReportBareName이 같은 계약을 못 박는다).
            Assert.Equal("UF_GET_WORKDAY2", fact.QualifiedName);
            Assert.Contains("UF_GET_WORKDAY2", fact.CallExpression);
        }

        [Fact]
        public void ExtractFunctionCalls_CallInsideIfPredicate_ShouldBeNumberedAsIf()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF dbo.UF_GET_ROUND4VAT(100) > 0
    BEGIN
        RETURN
    END
END";

            var facts = DmlScopeExtractor.ExtractFunctionCalls(ddl, new[] { "UF_GET_ROUND4VAT" });

            var fact = Assert.Single(facts);
            Assert.Equal("IF", fact.Operation);
            Assert.Equal(1, fact.StatementOrdinal);
        }

        [Fact]
        public void ExtractFunctionCalls_CallInsideIfBody_ShouldBelongToItsOwnStatement()
        {
            // 술어만 IF로 귀속시킨다 - 본문의 DML은 자기 문장이고 자기 번호를 받아야
            // 한다. 술어 갈래가 Collect(문장 전체 훑기)를 쓰면 이 단언이 무너진다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF dbo.UF_GET_ROUND4VAT(100) > 0
    BEGIN
        UPDATE A SET A.C = dbo.UF_GET_INCVTAXRATE(A.T) FROM dbo.TA A
    END
END";

            var facts = DmlScopeExtractor.ExtractFunctionCalls(
                ddl, new[] { "UF_GET_ROUND4VAT", "UF_GET_INCVTAXRATE" });

            Assert.Equal(2, facts.Count);
            var predicate = Assert.Single(facts, f => f.Operation == "IF");
            Assert.Equal("UF_GET_ROUND4VAT", predicate.QualifiedName);
            var body = Assert.Single(facts, f => f.Operation == "UPDATE");
            Assert.Equal("UF_GET_INCVTAXRATE", body.QualifiedName);
            Assert.Equal(1, body.StatementOrdinal);
        }

        [Fact]
        public void ExtractFunctionCalls_IfPredicateWithoutCall_ShouldNotConsumeAnOrdinal()
        {
            // 호출이 있을 때만 번호를 집는다 - 앞 IF가 빈손으로 번호를 삼키면
            // 뒤 IF의 번호가 조용히 밀린다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF @x > 0
    BEGIN
        RETURN
    END

    IF dbo.UF_GET_ROUND4VAT(100) > 0
    BEGIN
        RETURN
    END
END";

            var facts = DmlScopeExtractor.ExtractFunctionCalls(ddl, new[] { "UF_GET_ROUND4VAT" });

            var fact = Assert.Single(facts);
            Assert.Equal("IF", fact.Operation);
            Assert.Equal(1, fact.StatementOrdinal);
        }

        [Fact]
        public void ExtractFunctionCalls_StandaloneSelectWithoutFrom_ShouldNotConsumeAnOrdinal()
        {
            // FROM이 없는 대입은 스캔할 자리가 없다 - 잠금 힌트·DML 범위와 같은 판정
            // (HasFromClause)을 써야 네 표의 SELECT 번호가 같은 문장을 가리킨다.
            //
            // [앞 문장에 호출을 두지 않는 이유 - 변이 증명의 격리]
            // 앞 SELECT에도 호출을 두면 HasFromClause 가드를 지웠을 때 Assert.Single이
            // 먼저 터져(2건) 번호 단언은 실행되지도 않는다. 호출 없는 문장을 앞에 두면
            // 가드가 사라질 때 남는 사실은 여전히 하나이고 번호만 1에서 2로 밀리므로,
            // 이 테스트가 죽는 자리가 정확히 StatementOrdinal 단언이 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @a = 1

    SELECT @b = dbo.UF_GET_ROUND4VAT(2) FROM dbo.TA WITH(NOLOCK)
END";

            var facts = DmlScopeExtractor.ExtractFunctionCalls(ddl, new[] { "UF_GET_ROUND4VAT" });

            var fact = Assert.Single(facts);
            Assert.Equal("SELECT", fact.Operation);
            Assert.Equal(1, fact.StatementOrdinal);
            Assert.Contains("2", fact.CallExpression);
        }

        [Fact]
        public void ExtractFunctionCalls_DmlOrdinals_ShouldNotShift()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @v = dbo.UF_GET_ROUND4VAT(1) FROM dbo.TA WITH(NOLOCK)

    UPDATE A SET A.X = dbo.UF_GET_ROUND4VAT(2) FROM dbo.TB A
END";

            var facts = DmlScopeExtractor.ExtractFunctionCalls(ddl, new[] { "UF_GET_ROUND4VAT" });

            var update = Assert.Single(facts, f => f.Operation == "UPDATE");
            Assert.Equal(1, update.StatementOrdinal);
        }

        [Fact]
        public void ExtractFunctionCalls_UnparsableDdl_ShouldReturnEmpty()
        {
            // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
            Assert.Empty(DmlScopeExtractor.ExtractFunctionCalls(
                "CREATE PROCEDURE ((( broken", new[] { "UF_X" }));
            Assert.Empty(DmlScopeExtractor.ExtractFunctionCalls(null, new[] { "UF_X" }));
            Assert.Empty(DmlScopeExtractor.ExtractFunctionCalls("SELECT 1", Array.Empty<string>()));
        }

        [Fact]
        public void ExtractFunctionCalls_ScalarCall_ShouldReportBareName()
        {
            // ScriptDom의 FunctionCall.FunctionName은 한정자를 담지 않는다.
            // 스키마·DB는 렌더러가 Dependencies에서 붙인다 - 여기서 추측하지 않는다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = SETTLE_CARD_DB.dbo.UF_GET_COMM4PG(A.CPID)
    FROM   dbo.T A
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractFunctionCalls(
                ddl, new[] { "UF_GET_COMM4PG" }));

            Assert.Equal("UF_GET_COMM4PG", fact.QualifiedName);
        }

        [Fact]
        public void ExtractLockHints_FromClauseReferences_AreListedWithTheirHints()
        {
            // INS_EXTRA4PLCARD 실측 형태. 같은 TPGProperty가 별칭마다 힌트가 갈린다 -
            // 산문은 "5개 테이블의 조회 또는 조인에 사용됩니다"로 뭉갰고 그것이 🟡이었다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = 1
    FROM dbo.TSettleMst A WITH(NOLOCK)
    JOIN dbo.TPGProperty PG ON A.PGName = PG.PGName
    JOIN dbo.TPGProperty Y  WITH(NOLOCK) ON A.PGName = Y.PGName
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.Equal(3, facts.Count);
            Assert.Equal(new[] { "NOLOCK" }, Assert.Single(facts, f => f.Alias == "A").Hints);
            Assert.Empty(Assert.Single(facts, f => f.Alias == "PG").Hints);
            Assert.Equal(new[] { "NOLOCK" }, Assert.Single(facts, f => f.Alias == "Y").Hints);
        }

        [Fact]
        public void ExtractLockHints_TargetNodeWithoutFromClause_IsTheScan()
        {
            // 설계 초안은 "대상 노드를 싣지 않는다"였는데 프로브가 그 규칙이 사실을
            // 잃는 것을 보여 줬다. FROM 절이 없으면 대상 노드가 곧 스캔이고 힌트를 진다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DELETE FROM dbo.TSettleByOUT WITH(NOLOCK) WHERE OutYMD = '20260101'
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractLockHints(ddl));

            Assert.Equal("DELETE", fact.Operation);
            Assert.Equal("dbo.TSettleByOUT", fact.Table);
            Assert.Equal("-", fact.Alias);
            Assert.Equal(new[] { "NOLOCK" }, fact.Hints);
        }

        [Fact]
        public void ExtractLockHints_TargetNodeWithFromClause_IsNotDoubleCounted()
        {
            // UPDATE T ... FROM T A 에서 대상 T와 FROM의 A는 다른 노드이고 대상 쪽엔
            // 힌트가 없다. 둘 다 실으면 같은 테이블이 "힌트 있음/없음" 두 행으로 나와
            // 독자를 오도한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE TSettleMst SET C = 1 FROM dbo.TSettleMst A WITH(NOLOCK) WHERE A.X = 1
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractLockHints(ddl));

            Assert.Equal("A", fact.Alias);
            Assert.Equal(new[] { "NOLOCK" }, fact.Hints);
        }

        [Fact]
        public void ExtractLockHints_DerivedTableInterior_IsCollectedWithDerivedScope()
        {
            // 수정 라운드 2 - 조정자 판정: 초안("파생 테이블 안으로 내려가지 않는다")은
            // SqlStaticParser.FindAliasForTarget의 별칭-해석 규칙을 잘못 베낀 것이었다.
            // 별칭 해석은 이름의 스코프 문제라 안쪽 별칭이 바깥 대상과 무관하지만, 잠금
            // 힌트에는 그 논리가 서지 않는다 - 파생 테이블의 FROM은 같은 문장이 실제로
            // 하는 스캔이고 그 힌트가 곧 그 문장의 잠금 동작이다. 리뷰어가 실물로 보였다:
            // UP_UTIL_SETTLE_INS의 INSERT(55행)는 최상위 FROM 항목이 파생 테이블
            // 하나뿐이라 초안 규칙 아래에서 행이 0개가 되고, PaymentDB.dbo.TTxMst의
            // NOLOCK·INDEX를 포함한 네 테이블의 힌트가 통째로 사라졌다 - 스캔이 정말
            // 없는 문장과 구별되지 않는, 이 표가 막으려는 바로 그 실패 모양이다.
            // 「집합 술어」 표의 SetPredicateFact.Scope 선례를 따라 빼지 않고 "파생"으로
            // 표시해서 싣는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = 1
    FROM (SELECT PLTID FROM dbo.THidden B WITH(NOLOCK)) X
        ,dbo.TSettleMst A WITH(NOLOCK)
    WHERE X.PLTID = A.PLTID
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.Equal(2, facts.Count);

            var b = Assert.Single(facts, f => f.Alias == "B");
            Assert.Equal("dbo.THidden", b.Table);
            Assert.Equal("파생", b.Scope);
            Assert.Equal(new[] { "NOLOCK" }, b.Hints);

            var a = Assert.Single(facts, f => f.Alias == "A");
            Assert.Equal("최상위", a.Scope);
        }

        [Fact]
        public void ExtractLockHints_StatementWithNoScan_ProducesNoRow()
        {
            // FROM도 없고 대상에 힌트도 없으면 스캔할 자리가 없다. 빈 행으로 채우지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.TSettleMst SET C = 1 WHERE X = 1
END";

            Assert.Empty(DmlScopeExtractor.ExtractLockHints(ddl));
        }

        [Fact]
        public void ExtractLockHints_MultipleHints_AreAllListed()
        {
            // 한 참조에 힌트가 여럿 붙을 수 있다. 칸은 불리언이 아니라 목록이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = 1 FROM dbo.TSettleMst A WITH(NOLOCK, READUNCOMMITTED) WHERE A.X = 1
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractLockHints(ddl));

            Assert.Equal(new[] { "NOLOCK", "READUNCOMMITTED" }, fact.Hints);
        }

        [Fact]
        public void ExtractLockHints_InsertSourceFromClause_IsCollected()
        {
            // INSERT는 원천 SELECT의 FROM이 스캔 자리다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TStat (A) SELECT X FROM dbo.TSource S WITH(NOLOCK)
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractLockHints(ddl));

            Assert.Equal("INSERT", fact.Operation);
            Assert.Equal("S", fact.Alias);
            Assert.Equal(new[] { "NOLOCK" }, fact.Hints);
        }

        [Fact]
        public void ExtractLockHints_ReferencesOnDifferentLines_EachReportsItsOwnLine()
        {
            // 문장 줄을 쓰면 한 문장 안의 여러 참조가 전부 같은 줄로 찍혀 "문장" 칸이
            // 이미 주는 정보를 되풀이할 뿐이고, 독자가 어느 스캔인지 원문에서 못 찾는다.
            // INS_EXTRA4PLCARD의 INSERT 1은 52~174행에 걸쳐 있는데 그 안의 참조들이
            // 전부 "52"로 찍혔다(수정 라운드 1 실물 검증 실측). Line은 참조 노드 자신의
            // 줄이어야 한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = 1
    FROM dbo.TSettleMst A WITH(NOLOCK)
    JOIN dbo.TPGProperty PG
        ON A.PGName = PG.PGName
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            var a = Assert.Single(facts, f => f.Alias == "A");
            var pg = Assert.Single(facts, f => f.Alias == "PG");
            Assert.NotEqual(a.Line, pg.Line);
            Assert.Equal(5, a.Line);
            Assert.Equal(6, pg.Line);
        }

        [Fact]
        public void ExtractLockHints_InsertSourceIsUnion_EachBranchIsCollected()
        {
            // 수정 라운드 2 - 리뷰 실측: 원천이 BinaryQueryExpression(UNION ALL)이면
            // QuerySpecification으로 좁히는 캐스트가 통째로 실패해 FROM 수집이 비었다.
            // UP_Util_PG_Client_CMRate_Ins의 INSERT 2(76행)·INSERT 4(159행)가 전 테이블
            // NOLOCK인데 행이 0개였다. 같은 파일의 QuerySpecificationsOf(DmlScopeVisitor·
            // SetPredicateVisitor가 이미 쓰는 헬퍼)를 재사용해 갈래마다 훑는다 - 새로
            // 만들지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.TStat (A)
    SELECT X FROM dbo.TSource1 S1 WITH(NOLOCK)
    UNION ALL
    SELECT X FROM dbo.TSource2 S2 WITH(NOLOCK)
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Equal(new[] { "NOLOCK" }, Assert.Single(facts, f => f.Alias == "S1").Hints);
            Assert.Equal(new[] { "NOLOCK" }, Assert.Single(facts, f => f.Alias == "S2").Hints);
            Assert.All(facts, f => Assert.Equal("INSERT", f.Operation));
            Assert.All(facts, f => Assert.Equal(1, f.StatementOrdinal));
        }

        [Fact]
        public void ExtractLockHints_TargetAndFromReferToSameTableAndAlias_BothAreKept()
        {
            // 수정 라운드 2 - 리뷰 실측(Important, 88b0aa2류): 대상 노드와 FROM 절 참조가
            // 같은 (테이블, 별칭)으로 정규화되면(둘 다 별칭 없음 -> "-") Line을 뺀 중복
            // 제거 키가 둘을 같은 행으로 오인해 뒤에 추가되는 쪽을 조용히 버렸다 -
            // UPDATE dbo.T WITH(NOLOCK) ... FROM dbo.T에서 대상의 NOLOCK이 사라졌다.
            // 두 참조는 원문에서 서로 다른 줄에 있는 별개의 스캔 자리이므로 Line을
            // 키에 포함해 둘 다 지켜야 한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE dbo.T WITH(NOLOCK)
    SET    C = 1
    FROM   dbo.T
    WHERE  X = 1
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Contains(facts, f => f.Hints.Contains("NOLOCK"));
            Assert.Contains(facts, f => f.Hints.Count == 0);
        }

        [Fact]
        public void ExtractLockHints_IndexHintWithEqualsForm_RendersOriginalIndexName()
        {
            // 수정 라운드 3 - 리뷰 실측: 값을 지는 힌트를 HintKind 이름만으로 뽑으면
            // (예: "INDEX") 어느 인덱스를 강제하는지가 사라진다. 실물: UP_UTIL_SETTLE_INS
            // 146행 PaymentDB.dbo.TTxMst A WITH(NOLOCK, INDEX=CIDX_TTxMst_YMD) - 이관 시
            // 질의 계획이 달라지는 사실인데 표에 "INDEX"라고만 적으면 원본에서 찾을 수
            // 없다. IndexTableHint 노드 자신의 원문 토큰을 그대로 낸다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = 1 FROM dbo.T A WITH(INDEX=CIDX_x) WHERE A.X = 1
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractLockHints(ddl));

            Assert.Contains("INDEX=CIDX_x", fact.Hints);
        }

        [Fact]
        public void ExtractLockHints_IndexHintWithMultipleNames_RendersAllNames()
        {
            // INDEX(a, b) 형태 - 힌트 이름만으로는 두 인덱스 이름이 모두 사라진다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = 1 FROM dbo.T A WITH(INDEX(IX_a, IX_b)) WHERE A.X = 1
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractLockHints(ddl));

            Assert.Contains("INDEX(IX_a, IX_b)", fact.Hints);
        }

        [Fact]
        public void ExtractLockHints_ForceSeekHintWithColumns_RendersOriginalText()
        {
            // FORCESEEK(IX_a(col)) - 값이 중첩된 형태(인덱스 이름 + 컬럼 목록). ScriptDom은
            // 이를 ForceSeekTableHint로 판다(IndexValue·ColumnValues 별도 프로퍼티) -
            // HintKind 이름만으로는 인덱스도 컬럼도 사라진다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = 1 FROM dbo.T A WITH(FORCESEEK(IX_a(col))) WHERE A.X = 1
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractLockHints(ddl));

            Assert.Contains("FORCESEEK(IX_a(col))", fact.Hints);
        }

        [Fact]
        public void ExtractLockHints_ValuelessHint_StillRendersHintKindName()
        {
            // 값이 없는 힌트(NOLOCK 등)의 렌더는 이번 수정에서 손대지 않는다(조정자 판정 -
            // 범위를 값 있는 힌트로 한정). 회귀 확인용 고정 테스트.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = 1 FROM dbo.T A WITH(NOLOCK) WHERE A.X = 1
END";

            var fact = Assert.Single(DmlScopeExtractor.ExtractLockHints(ddl));

            Assert.Equal(new[] { "NOLOCK" }, fact.Hints);
        }

        [Fact]
        public void ExtractLockHints_StandaloneSelectWithFrom_ShouldBeNumberedAsSelect()
        {
            // INS_EXTRA:22 실측 - 변수 대입 SELECT의 NOLOCK이 표 밖이라
            // 문서 전체에서 한 번도 언급되지 않았다(2026-08-22 축 A 재감사 🟡).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @v_strReqYMD = MIN(ReqYMD)
    FROM   PaymentDB.dbo.TExtraSettleIn WITH(NOLOCK)

    UPDATE A SET A.X = 1 FROM dbo.TSettleMst A WITH(NOLOCK)
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            var select = Assert.Single(facts, f => f.Operation == "SELECT");
            Assert.Equal(1, select.StatementOrdinal);
            Assert.Equal("PaymentDB.dbo.TExtraSettleIn", select.Table);
            Assert.Equal("최상위", select.Scope);
            Assert.Equal(new[] { "NOLOCK" }, select.Hints);

            // 기존 DML 채번이 밀리지 않는다.
            var update = Assert.Single(facts, f => f.Operation == "UPDATE");
            Assert.Equal(1, update.StatementOrdinal);
        }

        [Fact]
        public void ExtractLockHints_SelectWithoutFrom_ShouldNotBeNumbered()
        {
            // 스캔할 자리가 없는 대입은 문장 번호를 소비하지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT @a = 1

    SELECT @v = MIN(ReqYMD) FROM dbo.TA WITH(NOLOCK)
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            var select = Assert.Single(facts, f => f.Operation == "SELECT");
            Assert.Equal(1, select.StatementOrdinal);
            Assert.Equal("dbo.TA", select.Table);
        }

        [Fact]
        public void ExtractLockHints_CursorSourceSelect_ShouldBeNumberedAsSelect()
        {
            // PROC_ETC:62 실측 - 커서 원천 질의는 SelectStatement로 방문된다(프로브 확인).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE Cur_SettlePost CURSOR FOR
    SELECT A.ClientID
    FROM   dbo.TSettleMst A WITH(NOLOCK)
    ORDER BY A.OutYMD, A.ClientID
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            var select = Assert.Single(facts);
            Assert.Equal("SELECT", select.Operation);
            Assert.Equal("dbo.TSettleMst", select.Table);
            Assert.Equal("A", select.Alias);
        }

        [Fact]
        public void ExtractLockHints_InsertSelectSource_ShouldNotProduceSelectRow()
        {
            // INSERT ... SELECT의 원천은 SelectStatement로 방문되지 않는다(프로브 확인).
            // 이 테스트는 그 사실이 깨지면 INSERT 원천이 두 번 실린다는 것을 잡는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    INSERT INTO dbo.TF (C)
    SELECT C FROM dbo.TG WITH(NOLOCK)
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            // Assert.Empty(facts.Where(...)) 대신 DoesNotContain을 쓴다 - 같은 판정이지만
            // 전자는 xUnit2029 경고를 낸다(빌드 실측).
            Assert.DoesNotContain(facts, f => f.Operation == "SELECT");
            Assert.Single(facts, f => f.Operation == "INSERT");
        }

        [Fact]
        public void ExtractLockHints_ControlFlowPredicate_ShouldBeNumberedAsIf()
        {
            // INS_EXTRA:31 실측 - -9 차단 게이트의 판단 근거 스캔이다.
            // 축 A 계약이 이 자리를 제어 흐름 술어 하위 질의의 실물 사례로 지목한다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF EXISTS(SELECT PLTID
              FROM   TSettleMst WITH(NOLOCK)
              WHERE  ProcYMD = @pi_strYMD)
    BEGIN
        RETURN -9
    END
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("IF", fact.Operation);
            Assert.Equal(1, fact.StatementOrdinal);
            Assert.Equal("TSettleMst", fact.Table);
            Assert.Equal("최상위", fact.Scope);
            Assert.Equal(new[] { "NOLOCK" }, fact.Hints);
        }

        [Fact]
        public void ExtractLockHints_TwoControlFlowPredicates_ShouldNumberIndependently()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF EXISTS(SELECT 1 FROM dbo.TA WITH(NOLOCK)) RETURN -1
    IF EXISTS(SELECT 1 FROM dbo.TB WITH(NOLOCK)) RETURN -2
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Equal(1, facts[0].StatementOrdinal);
            Assert.Equal("dbo.TA", facts[0].Table);
            Assert.Equal(2, facts[1].StatementOrdinal);
            Assert.Equal("dbo.TB", facts[1].Table);
        }

        [Fact]
        public void ExtractLockHints_IfWithoutScanNestingAnotherIf_ShouldNotShiftInnerOrdinal()
        {
            // 술어에 하위 질의가 없는 IF는 번호를 쓰지 않는다. 그 IF가 다른 IF를 품고
            // 있어도 안쪽이 1번을, 뒤따르는 IF가 2번을 받아야 한다.
            //
            // 이 테스트가 고정하는 것은 그 결과뿐이고 구현 방식이 아니다. 계획서의
            // "먼저 집었다가 스캔이 없으면 되돌린다" 방식으로도 이 테스트는 통과한다 -
            // 되돌리기가 base.ExplicitVisit보다 앞서므로 집는 시점과 되돌리는 시점
            // 사이에 안쪽 IF가 끼어들 자리가 없다. 지금 구현이 되감기를 아예 쓰지 않는
            // 이유는 LockHintVisitor.ExplicitVisit(IfStatement) 문서에 있다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF @p_intMode = 1
    BEGIN
        IF EXISTS(SELECT 1 FROM dbo.TA WITH(NOLOCK)) RETURN -1
    END

    IF EXISTS(SELECT 1 FROM dbo.TB WITH(NOLOCK)) RETURN -2
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Equal(1, facts[0].StatementOrdinal);
            Assert.Equal("dbo.TA", facts[0].Table);
            Assert.Equal(2, facts[1].StatementOrdinal);
            Assert.Equal("dbo.TB", facts[1].Table);
        }

        [Fact]
        public void ExtractLockHints_SubqueryInsideDml_ShouldKeepStatementOrdinalAndMarkScope()
        {
            // COMM_UPD:145 · EXCEPTION_PROC:529 실측 - 최상위 WHERE 하위 질의의 NOLOCK이
            // 표 밖이라 산문도 함께 침묵했다(2026-08-22 축 A 재감사 🟡).
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TSettleMst A WITH(NOLOCK)
    WHERE  A.PLTID IN (SELECT PLTID FROM PaymentDB.dbo.TCCanceledMst WITH(NOLOCK))
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.All(facts, f => Assert.Equal("UPDATE", f.Operation));
            Assert.All(facts, f => Assert.Equal(1, f.StatementOrdinal));

            var sub = Assert.Single(facts, f => f.Table == "PaymentDB.dbo.TCCanceledMst");
            Assert.Equal("하위 질의", sub.Scope);
            Assert.Equal(new[] { "NOLOCK" }, sub.Hints);

            var top = Assert.Single(facts, f => f.Table == "dbo.TSettleMst");
            Assert.Equal("최상위", top.Scope);
        }

        [Fact]
        public void ExtractLockHints_SubqueryInsideDml_ShouldNotConsumeSelectOrdinal()
        {
            // DML 안 하위 질의는 그 DML 문장의 일부다. 독립 SELECT로 다시 채번하면
            // 뒤 문장의 SELECT 번호가 밀려 다른 표와 나란히 읽을 수 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TA A WITH(NOLOCK)
    WHERE  A.ID IN (SELECT ID FROM dbo.TB WITH(NOLOCK))

    SELECT @v = MIN(ReqYMD) FROM dbo.TC WITH(NOLOCK)
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            var sub = Assert.Single(facts, f => f.Table == "dbo.TB");
            Assert.Equal("UPDATE", sub.Operation);
            Assert.Equal(1, sub.StatementOrdinal);
            Assert.Equal("하위 질의", sub.Scope);

            // 뒤 SELECT의 번호가 밀리지 않는다.
            var select = Assert.Single(facts, f => f.Operation == "SELECT");
            Assert.Equal(1, select.StatementOrdinal);
            Assert.Equal("dbo.TC", select.Table);
        }

        [Fact]
        public void ExtractLockHints_SubqueryInsideDelete_ShouldBeMarkedAsSubqueryScope()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DELETE FROM dbo.TA
    WHERE  ID IN (SELECT ID FROM dbo.TB WITH(NOLOCK))
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            var sub = Assert.Single(facts, f => f.Table == "dbo.TB");
            Assert.Equal("DELETE", sub.Operation);
            Assert.Equal(1, sub.StatementOrdinal);
            Assert.Equal("하위 질의", sub.Scope);
            Assert.Equal(new[] { "NOLOCK" }, sub.Hints);
        }

        [Fact]
        public void ExtractLockHints_SubqueryInsideInsertSource_ShouldBeMarkedAsSubqueryScope()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    INSERT INTO dbo.TDst (ID)
    SELECT A.ID
    FROM   dbo.TA A WITH(NOLOCK)
    WHERE  A.ID IN (SELECT ID FROM dbo.TB WITH(NOLOCK))
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.All(facts, f => Assert.Equal("INSERT", f.Operation));
            Assert.All(facts, f => Assert.Equal(1, f.StatementOrdinal));

            var sub = Assert.Single(facts, f => f.Table == "dbo.TB");
            Assert.Equal("하위 질의", sub.Scope);

            var top = Assert.Single(facts, f => f.Table == "dbo.TA");
            Assert.Equal("최상위", top.Scope);
        }

        [Fact]
        public void ExtractLockHints_NestedSubqueryInIfPredicate_ShouldUseSubqueryScope()
        {
            // 같은 중첩 모양이 문장 종류에 따라 다른 범위로 찍히면 "최상위"를 믿을 수 없다.
            // IF 술어의 첫 겹만 그 IF가 직접 훑는 자리이고, 그보다 깊은 겹은 DML의 WHERE
            // 하위 질의와 같은 자리다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF EXISTS(SELECT 1
              FROM   dbo.TA WITH(NOLOCK)
              WHERE  ID IN (SELECT ID FROM dbo.TB WITH(NOLOCK)))
        RETURN -1
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.All(facts, f => Assert.Equal("IF", f.Operation));
            Assert.All(facts, f => Assert.Equal(1, f.StatementOrdinal));

            var top = Assert.Single(facts, f => f.Table == "dbo.TA");
            Assert.Equal("최상위", top.Scope);

            var sub = Assert.Single(facts, f => f.Table == "dbo.TB");
            Assert.Equal("하위 질의", sub.Scope);
            Assert.Equal(new[] { "NOLOCK" }, sub.Hints);
        }

        [Fact]
        public void ExtractLockHints_DmlInsideIfBody_ShouldGetItsOwnOrdinal()
        {
            // IF 본문 안의 DML은 술어가 아니라 자기 문장이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF EXISTS(SELECT 1 FROM dbo.TA WITH(NOLOCK))
    BEGIN
        UPDATE B SET B.X = 1 FROM dbo.TB B WITH(NOLOCK)
    END
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            var ifFact = Assert.Single(facts, f => f.Operation == "IF");
            Assert.Equal(1, ifFact.StatementOrdinal);

            var update = Assert.Single(facts, f => f.Operation == "UPDATE");
            Assert.Equal(1, update.StatementOrdinal);
            Assert.Equal("최상위", update.Scope);
        }

        [Fact]
        public void ExtractLockHints_SubqueryInsideJoinPredicate_ShouldUseSubqueryScope()
        {
            // 수정 라운드 1 리뷰 실측 - JOIN ... ON 안의 하위 질의는 WHERE 안의 것과
            // 같은 자리인데 "최상위"로 실렸다. 빠진 것이 아니라 틀리게 실린 것이라
            // 더 나쁘다(스펙 §2.4) - 프롬프트는 "최상위"를 "그 문장 자신의 FROM"으로
            // 정의해 축자로 읽는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TA A
    JOIN   dbo.TB B ON B.ID IN (SELECT ID FROM dbo.TC WITH(NOLOCK))
    WHERE  A.ID = 1
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.All(facts, f => Assert.Equal("UPDATE", f.Operation));
            Assert.All(facts, f => Assert.Equal(1, f.StatementOrdinal));

            var sub = Assert.Single(facts, f => f.Table == "dbo.TC");
            Assert.Equal("하위 질의", sub.Scope);
            Assert.Equal(new[] { "NOLOCK" }, sub.Hints);

            Assert.Equal("최상위", Assert.Single(facts, f => f.Table == "dbo.TA").Scope);
            Assert.Equal("최상위", Assert.Single(facts, f => f.Table == "dbo.TB").Scope);
        }

        [Fact]
        public void ExtractLockHints_SubqueryInsideDerivedTable_ShouldWinOverDerivedScope()
        {
            // 수정 라운드 1 리뷰 실측 - 파생과 하위 질의가 겹치는 자리. 규칙은
            // "하위 질의가 파생을 이긴다"이며, 등록 순서가 아니라 FromTableCollector의
            // 표시 우선순위가 그것을 정한다. 이 테스트가 없으면 수집 순서를 한 줄
            // 옮기는 것만으로 라벨이 조용히 뒤집힌다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF EXISTS(SELECT 1
              FROM (SELECT ID
                    FROM   dbo.TD WITH(NOLOCK)
                    WHERE  ID IN (SELECT ID FROM dbo.TE WITH(NOLOCK))) D)
        RETURN -1
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.All(facts, f => Assert.Equal("IF", f.Operation));
            Assert.All(facts, f => Assert.Equal(1, f.StatementOrdinal));

            Assert.Equal("파생", Assert.Single(facts, f => f.Table == "dbo.TD").Scope);
            Assert.Equal("하위 질의", Assert.Single(facts, f => f.Table == "dbo.TE").Scope);
        }

        [Fact]
        public void ExtractLockHints_SubqueryInSelectList_ShouldUseSubqueryScope()
        {
            // UF_Get_CLComm4MobileCo:31-32 실측 - SELECT 목록 안 스칼라 하위 질의의 NOLOCK이
            // 표에 오지 않았다. 같은 문장의 FROM은 최상위로 실리므로, 표가 그 문장에 대해
            // 채워진 것처럼 보이는데 두 스캔 중 하나가 빠진다 - 없는 것보다 나쁜 모양이다.
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT
AS
BEGIN
    DECLARE @v INT
    SELECT @v = CASE WHEN MobileCo1 > 0 THEN MobileCo1
                     ELSE (SELECT CommissionRate
                           FROM   dbo.TClientCMRate WITH(NOLOCK)
                           WHERE  ClientID = '1')
                END
    FROM   dbo.TClientSettleRate4MobileCo WITH(NOLOCK)
    RETURN @v
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            var outer = Assert.Single(facts, f => f.Table == "dbo.TClientSettleRate4MobileCo");
            Assert.Equal("최상위", outer.Scope);

            var inner = Assert.Single(facts, f => f.Table == "dbo.TClientCMRate");
            Assert.Equal("하위 질의", inner.Scope);
            Assert.Equal(new[] { "NOLOCK" }, inner.Hints);
            Assert.Equal(outer.StatementOrdinal, inner.StatementOrdinal);
        }

        [Fact]
        public void ExtractLockHints_SubqueryInSetClause_ShouldUseSubqueryScope()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.OutYMD = (SELECT MAX(YMD) FROM dbo.TB WITH(NOLOCK))
    FROM   dbo.TA A WITH(NOLOCK)
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            var inner = Assert.Single(facts, f => f.Table == "dbo.TB");
            Assert.Equal("하위 질의", inner.Scope);
            Assert.Equal("UPDATE", inner.Operation);
            Assert.Equal(1, inner.StatementOrdinal);
        }

        [Fact]
        public void ExtractLockHints_SubqueryInStandaloneSelectWhere_ShouldUseSubqueryScope()
        {
            // 세 모양 중 셋째. `ExplicitVisit(SelectStatement)`이 원래부터 이 노드를
            // 방문하지만 훑는 것은 CollectFromQuery, 즉 FROM 절뿐이라 독립 SELECT의
            // WHERE 하위 질의는 담기지 않았다 - 하위 질의를 훑는 경로(당시 이름은
            // CollectWhereSubqueries, 지금은 CollectStatementSubqueries)를 DML 셋만
            // 불렀기 때문이다. "SelectStatement를 방문하니 이미 되고 있다"는 추정이
            // 거짓임을 못 박는다 - 이 테스트는 넓히기 전에 실제로 빨갰다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = ID
    FROM   dbo.TA WITH(NOLOCK)
    WHERE  ID IN (SELECT ID FROM dbo.TB WITH(NOLOCK))
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.All(facts, f => Assert.Equal("SELECT", f.Operation));
            Assert.All(facts, f => Assert.Equal(1, f.StatementOrdinal));

            Assert.Equal("최상위", Assert.Single(facts, f => f.Table == "dbo.TA").Scope);

            var inner = Assert.Single(facts, f => f.Table == "dbo.TB");
            Assert.Equal("하위 질의", inner.Scope);
            Assert.Equal(new[] { "NOLOCK" }, inner.Hints);
        }

        [Fact]
        public void ExtractLockHints_FromClauseSubquery_ShouldNotDependOnCollectionOrder()
        {
            // Task 3이 수집을 문장 노드 전체로 넓히면서 FROM 절 **안의** 하위 질의가
            // CollectFrom과 CollectStatementSubqueries 양쪽으로 들어오게 됐다. Add의
            // 중복 제거 키에 Scope가 없으므로 두 경로가 다른 라벨을 내면 먼저 등록된
            // 쪽이 남아 수집 순서 한 줄이 답을 가른다.
            //
            // dbo.TC는 하위 질의 안의 파생 테이블이라 FromTableCollector에서 두 표시가
            // 동시에 켜지는 자리다. CollectFrom이 먼저 도는데도 `하위 질의`가 나오는 것은
            // ScopeOf의 우선순위(_inSubquery 먼저) 덕이다 - CollectSubqueryScans는 언제나
            // SubqueryScope를 쓰므로 두 경로의 답이 여기서 일치한다.
            //
            // 이 테스트가 기제를 실제로 잡는지는 뮤테이션으로 확인했다: ScopeOf에서
            // _inDerivedTable을 먼저 보게 뒤집으면 CollectFrom이 `파생`을 먼저 등록해
            // 이 단언이 빨개진다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A
    SET    A.X = 1
    FROM   dbo.TA A WITH(NOLOCK)
    JOIN   dbo.TB B ON B.ID IN (SELECT ID
                                FROM   (SELECT ID FROM dbo.TC WITH(NOLOCK)) D)
    WHERE  A.ID = 1
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.All(facts, f => Assert.Equal("UPDATE", f.Operation));
            Assert.All(facts, f => Assert.Equal(1, f.StatementOrdinal));

            var inner = Assert.Single(facts, f => f.Table == "dbo.TC");
            Assert.Equal("하위 질의", inner.Scope);
            Assert.Equal(new[] { "NOLOCK" }, inner.Hints);

            Assert.Equal("최상위", Assert.Single(facts, f => f.Table == "dbo.TA").Scope);
        }

        [Fact]
        public void ExtractLockHints_IfPredicateAndDmlBody_ShouldNotOverlap()
        {
            // Task 3이 수집을 문장 노드 전체로 넓혔으므로 "IF 술어 안 하위 질의가 DML
            // 경로에서도 잡히는가"를 물어야 한다. 잡히지 않는다 - 겹치려면 IF 술어가
            // UPDATE 노드의 자손이어야 하는데 T-SQL에는 그 형태가 없고, 실제 중첩은
            // 반대 방향(IF가 DML을 담는다)이라 UPDATE에서 시작하는 훑기가 술어에
            // 닿지 못한다. 반대 방향도 안전하다 - IfStatement 오버라이드는 술어만 훑고
            // 본문은 자식 순회에 맡긴다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    IF EXISTS(SELECT 1 FROM dbo.TG WITH(NOLOCK))
    BEGIN
        UPDATE A
        SET    A.X = (SELECT MAX(ID) FROM dbo.TH WITH(NOLOCK))
        FROM   dbo.TA A WITH(NOLOCK)
    END
END";

            var facts = DmlScopeExtractor.ExtractLockHints(ddl);

            Assert.Equal(3, facts.Count);

            // IF 술어의 스캔은 IF 번호로만 실린다 - UPDATE 경로가 이 자리를 다시 담지 않는다.
            var gate = Assert.Single(facts, f => f.Table == "dbo.TG");
            Assert.Equal("IF", gate.Operation);
            Assert.Equal(1, gate.StatementOrdinal);
            Assert.Equal("최상위", gate.Scope);

            // 본문 UPDATE의 두 스캔은 UPDATE 번호로만 실린다 - IF 경로가 본문을 담지 않는다.
            var target = Assert.Single(facts, f => f.Table == "dbo.TA");
            Assert.Equal("UPDATE", target.Operation);
            Assert.Equal("최상위", target.Scope);

            var setSubquery = Assert.Single(facts, f => f.Table == "dbo.TH");
            Assert.Equal("UPDATE", setSubquery.Operation);
            Assert.Equal(1, setSubquery.StatementOrdinal);
            Assert.Equal("하위 질의", setSubquery.Scope);
        }
    }
}
