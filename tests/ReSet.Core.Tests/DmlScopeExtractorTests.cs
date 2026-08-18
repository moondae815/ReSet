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
    }
}
