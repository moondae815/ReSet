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

            var fact = Assert.Single(DmlScopeExtractor.ExtractSetPredicates(ddl));

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
        public void ExtractSetPredicates_SubqueryIn_ShouldBeSkipped()
        {
            // 집합이 리터럴이 아니므로 옮겨 적을 목록 자체가 없다(설계 §3.2).
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

            Assert.Empty(DmlScopeExtractor.ExtractSetPredicates(ddl));
        }

        [Fact]
        public void ExtractSetPredicates_MixedValues_ShouldBeSkipped()
        {
            // 원소에 리터럴 아닌 것이 하나라도 섞이면 담지 않는다 - 리터럴 집합으로
            // 렌더하면 명세서에 거짓 집합이 실린다(설계 §3.2).
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = 1 FROM dbo.T A JOIN dbo.S B ON A.Id = B.Id WHERE A.PGName IN ('PLCard', B.PGName)
END";

            Assert.Empty(DmlScopeExtractor.ExtractSetPredicates(ddl));
        }

        [Fact]
        public void ExtractSetPredicates_InsideScalarSubquery_ShouldBeSkipped()
        {
            // "최상위"의 정의는 TopLevelPredicateCollector가 갖는다 - 스칼라 서브쿼리
            // 안의 IN은 대상 범위를 정하지 않는다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.T SET C = 1
    WHERE  Amt = (SELECT MAX(Amt) FROM dbo.S WHERE PGName IN ('A','B'))
END";

            Assert.Empty(DmlScopeExtractor.ExtractSetPredicates(ddl));
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

            // 리터럴을 우변에 둔 셋만 담는다. `YMD = @pi_strYMD`는 옮겨 적을 리터럴이 없다.
            Assert.Equal(3, facts.Count);
            Assert.Contains(facts, f => f.Column == "InState" && f.Operator == "=" && f.Literals[0] == "0");
            Assert.Contains(facts, f => f.Column == "PGName" && f.Operator == "=" && f.Literals[0] == "'PLCard'");
            Assert.Contains(facts, f => f.Column == "UseState" && f.Operator == "<>" && f.Literals[0] == "1");
            Assert.DoesNotContain(facts, f => f.Column == "YMD");
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

        // 파라미터·컬럼 비교는 옮겨 적을 리터럴이 없다. 담으면 표가 조인 키와
        // 기준일 비교로 뒤덮여 진짜 리터럴 집합이 묻힌다.
        [Theory]
        [InlineData("A.YMD = @pi_strYMD")]
        [InlineData("A.PLTID = B.PLTID")]
        public void ExtractSetPredicates_ComparisonWithoutALiteral_ShouldBeIgnored(string predicate)
        {
            var ddl = $@"
CREATE PROCEDURE dbo.P @pi_strYMD VARCHAR(8) AS
BEGIN
    UPDATE A SET A.PGCOMM = 0
    FROM dbo.TSettleMst A, dbo.TSettleMst B
    WHERE {predicate}
END";

            Assert.Empty(DmlScopeExtractor.ExtractSetPredicates(ddl));
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
        public void ExtractFunctionCalls_StandaloneSelect_ShouldBeSkipped()
        {
            // DML 범위 표·집합 술어 표와 같은 경계다 - 세 표가 같은 문장 집합을
            // 같은 번호로 가리켜야 나란히 읽을 수 있다. 이 경계를 넓히려면 세 표를
            // 함께 넓혀야 하므로, 여기서 조용히 달라지지 않도록 못 박아 둔다.
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
    }
}
