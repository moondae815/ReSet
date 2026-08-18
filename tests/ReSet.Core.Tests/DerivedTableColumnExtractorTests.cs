using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class DerivedTableColumnExtractorTests
    {
        [Fact]
        public void Extract_UnionAllDerivedTable_ShouldCaptureBothBranches()
        {
            // 2026-08-18 축 A 감사의 🔴 실측 형태. EXCEPTION_PROC 문장 17
            // (PointPay/Payco, object_definition.sql:469-508)의 파생 X는 UNION ALL
            // 두 갈래다. 예전 추출기는 QueryExpression을 QuerySpecification으로만
            // 캐스트해 BinaryQueryExpression에서 그냥 반환했고, 그래서 이 X가 표에서
            // 통째로 빠졌다 - BB.PGVT가 X.PGETC4SUM을 참조하는데 정의가 없어 이행 시
            // PG 부가세 금액이 달라진다. 두 갈래의 PGCOMM4SUM 산식이 서로 다르므로
            // 갈래를 접지 않고 둘 다 실려야 한다.
            var ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE BB SET PGVT = X.PGETC4SUM
    FROM TSettleMst BB
    JOIN (
        SELECT A.PLTID
              ,PGETC4SUM = B.ETCAmt+(B.ETCAmt/10.0)
              ,PGCOMM4SUM = B.CommissionAmt
        FROM TSettleMst A JOIN TPGCMRate B ON A.PGName = B.PGName
        WHERE A.PGName = 'pointpay'
        UNION ALL
        SELECT A.PLTID
              ,PGETC4SUM = B.ETCAmt+(B.ETCAmt/10.0)
              ,PGCOMM4SUM = B.CommissionAmt+(B.CommissionAmt/10.0)
        FROM TSettleMst A JOIN TPGCMRate B ON A.PGName = B.PGName
        WHERE A.PGName = 'payco'
    ) X ON BB.PLTID = X.PLTID
END";

            var definitions = DerivedTableColumnExtractor.Extract(ddl);

            // 두 갈래에서 식이 같은 PGETC4SUM은 한 행으로 접힌다.
            var etc = Assert.Single(definitions, d => d.Alias == "X" && d.Column == "PGETC4SUM");
            Assert.Contains("ETCAmt", etc.Expression);

            // 식이 다른 PGCOMM4SUM은 갈래마다 남아야 한다 - 접으면 차이가 사라진다.
            var comm = definitions.Where(d => d.Alias == "X" && d.Column == "PGCOMM4SUM").ToList();
            Assert.Equal(2, comm.Count);
            Assert.Contains(comm, d => !d.Expression.Contains("/ 10.0") && !d.Expression.Contains("/10.0"));
            Assert.Contains(comm, d => d.Expression.Contains("10.0"));
        }

        [Fact]
        public void Extract_UpdateFromDerivedTable_ShouldCaptureColumnExpressions()
        {
            // EXCEPTION_PROC 실행순서 13 실측 형태. Spec은 SET 우변을
            // ISNULL(X.PGCOMM,0)까지만 적고 X의 정의를 어디에도 적지 않았다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A
    SET    A.PGComm = ISNULL(X.PGCOMM, 0)
    FROM   dbo.TSettleMst A
    JOIN   (SELECT PLTID,
                   IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt) AS PGCOMM
            FROM   dbo.TSettleMst A) X ON X.PLTID = A.PLTID
END";

            var definitions = DerivedTableColumnExtractor.Extract(ddl);

            var pgComm = Assert.Single(definitions, d => d.Column == "PGCOMM");
            Assert.Equal("X", pgComm.Alias);
            Assert.Contains("DiscountFlag", pgComm.Anchors);
            Assert.Contains("DiscountAmt", pgComm.Anchors);
        }

        [Fact]
        public void Extract_NoDerivedTable_ShouldReturnEmpty()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A SET A.C = B.C FROM dbo.T A JOIN dbo.U B ON A.ID = B.ID
END";

            Assert.Empty(DerivedTableColumnExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_EmptyDdl_ShouldReturnEmpty()
        {
            Assert.Empty(DerivedTableColumnExtractor.Extract(null));
        }

        [Fact]
        public void Extract_QuotedStringLiteralInExpression_ShouldNotBecomeAnAnchor()
        {
            // 실측(UP_UTIL_SETTLE_INS_EXTRA4PLCARD 실행순서): PGComm 정의가
            // A.PGName='dacomcard' 같은 문자열 리터럴 비교를 포함한다. 이 리터럴은
            // 식별자가 아니라 값이므로 앵커가 되어서는 안 된다 - 명세서가 다른
            // 표현으로 이 값을 서술해도(예: 다음카드 코드) 앵커 불일치로 결함
            // 처리되면 안 되고, 애초에 앵커 목록에 이런 값이 섞이면 안 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    UPDATE A
    SET    A.PGComm = X.PGComm
    FROM   dbo.TSettleMst A
    JOIN   (SELECT PLTID,
                   CASE WHEN A.PGName = 'dacomcard' THEN 1 ELSE 0 END AS PGComm
            FROM   dbo.TSettleMst A) X ON X.PLTID = A.PLTID
END";

            var definitions = DerivedTableColumnExtractor.Extract(ddl);

            var pgComm = Assert.Single(definitions, d => d.Column == "PGComm");
            Assert.Contains("PGName", pgComm.Anchors);
            Assert.DoesNotContain("dacomcard", pgComm.Anchors);
        }

        [Fact]
        public void Extract_NestedDerivedTable_ShouldCaptureBothLevels()
        {
            // 실측(UF_GET_COLLECTYMD): 파생 테이블 Z가 파생 테이블 A를 감싼다.
            // 두 단 모두 컬럼 정의로 잡혀야 한다 - 바깥 단만 잡고 안쪽 단을
            // 놓치면 A.YMD를 만드는 DATEADD 표현식이 소실된다.
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS VARCHAR(8) AS
BEGIN
    DECLARE @r VARCHAR(8)
    SELECT @r = MIN(YMD)
    FROM (
        SELECT A.YMD
        FROM ( SELECT CONVERT(CHAR(8), DATEADD(D, NUMBER, @v_strYMD), 112) AS YMD
               FROM   MASTER..SPT_VALUES
               WHERE  NUMBER < 15
             ) A
    ) Z
    RETURN @r
END";

            var definitions = DerivedTableColumnExtractor.Extract(ddl);

            Assert.Contains(definitions, d => d.Alias == "Z" && d.Column == "YMD");
            var innerYmd = Assert.Single(definitions, d => d.Alias == "A" && d.Column == "YMD");
            Assert.Contains("v_strYMD", innerYmd.Anchors);
        }

        [Fact]
        public void Extract_InsertSelectFromDerivedTable_ShouldCaptureColumnExpressions()
        {
            // 실측(UP_UTIL_SETTLE_INS_EXTRA): 파생 테이블이 UPDATE...FROM이 아니라
            // INSERT INTO ... SELECT의 소스 절에 있다. UPDATE 전용으로 오인해
            // InsertStatement를 건너뛰면 이 SP의 파생 테이블 정의가 통째로
            // 빠진다.
            const string ddl = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    INSERT INTO dbo.T (YMD, PGComm)
    SELECT X.YMD, X.PGComm
    FROM ( SELECT A.ReqYMD AS YMD,
                  IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt) AS PGComm
           FROM   dbo.TSettleMst A
         ) X
END";

            var definitions = DerivedTableColumnExtractor.Extract(ddl);

            var pgComm = Assert.Single(definitions, d => d.Column == "PGComm");
            Assert.Equal("X", pgComm.Alias);
            Assert.Contains("DiscountFlag", pgComm.Anchors);
            Assert.Contains("DiscountAmt", pgComm.Anchors);
        }
    }
}
