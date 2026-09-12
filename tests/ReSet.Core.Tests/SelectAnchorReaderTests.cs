using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// <see cref="StepSqlStatementReader.ReadSelectAnchors"/> 가 리더가 보는 자리
    /// (SQL 펜스 안 · 줄 첫머리 주석)에서만 <c>SELECT n</c> 을 집는지 본다.
    ///
    /// [왜 DML 앵커보다 엄격한가] <see cref="StepSqlStatementReader"/> 의 앵커 정규식은
    /// 주석 안 어디서든 가장 왼쪽 매치를 잡는다. 그 규칙을 그대로 쓰면
    /// <c>/* U1: … (SELECT 1) */</c> 처럼 설명에 종류를 적은 주석이 SELECT 앵커로 읽힌다.
    /// 여기서는 <b>주석이 `SELECT n:` 으로 시작할 때만</b> 앵커로 본다 - 다만 <b>표기
    /// 갈래는 좁히지 않는다.</b> 계약(프롬프트)이 규정하는 형식은 블록형
    /// <c>/* SELECT n: … */</c> 하나뿐이지만, 리더는 규정이 아니라 실물을 서술한다 -
    /// 대시형 <c>-- SELECT n: …</c> 도 코퍼스 절반을 차지해 똑같이 받는다
    /// (<see cref="DashSelectAnchorStep"/> 참고). 엄격함이 거르는 것은 표기 갈래가
    /// 아니라 "줄 맨 앞에서 시작하는가"뿐이다.
    /// </summary>
    public class SelectAnchorReaderTests
    {
        /// <summary>POQSettleBatch7/S15 그대로(줄 그대로 오려냄, 트리밍 없음) - 블록형
        /// <c>/* SELECT n: … */</c> 앵커 둘. 실물에는 <c>DECLARE … CURSOR</c> 문이
        /// 없다(§2 정정이 그 시나리오를 코퍼스 발화 0 이라 못박았다) - 앵커 1 앞의
        /// 문장은 평범한 최상위 <c>SELECT … GROUP BY … ORDER BY</c>, 앵커 2 앞의
        /// 문장은 <c>SELECT ID FROM …</c>(대입문이 아니다). 대신 각 앵커 앞에는
        /// 그것대로의 잡음이 실제로 있다 - 라벨 주석(<c>-- SQL_CURSOR_SOURCE</c>,
        /// <c>SELECT n:</c> 으로 시작하지 않아 앵커가 아니다)과 스칼라 <c>DECLARE
        /// @v_intIssueType TINYINT = 15;</c> 문(커서가 아니라 변수 선언). 이 시험이
        /// 지키는 값: 이 코퍼스에서 블록형 표기의 <b>유일한 실물 양성 커버리지</b>다
        /// - <see cref="DashSelectAnchorStep"/> 은 대시형이고,
        /// <see cref="KeepsDuplicatesBecauseADuplicateIsItselfADefectSignal"/> 의
        /// 블록형은 합성 데이터다.</summary>
        private const string BlockSelectAnchorStep = @"### S15 단계

```sql
-- SQL_CURSOR_SOURCE
/* SELECT 1: Cur_SettlePost 커서 소스 - 고객사/거래일/지급일 단위 집계 */
SELECT A.ClientID AS ClientID,
       A.YMD      AS YMD,
       A.OutYMD   AS OutYMD,
       SUM(A.CLTotal)                AS CLTotal,
       SUM(A.CLTotal - A.CLVT) * -1  AS CLComm,
       SUM(A.CLVT) * -1              AS CLVT
  FROM SETTLE_POQ_DB.dbo.TSettleMst          AS A
  JOIN SETTLE_POQ_DB.dbo.TClientSettleRate   AS B
    ON A.YMD = B.YMD AND A.ClientID = B.ClientID AND A.PGName = B.PGName AND A.MallID = B.MallID
  JOIN SETTLE_POQ_DB.dbo.TClient             AS C
    ON A.ClientID = C.ClientID
 WHERE ISNULL(B.TaxFGBill, 2) = 1     -- 세금계산서 청구유형코드(1:청구,2:영수)
   AND A.YMD = @p_batchYmd
   AND A.OutState = 2
 GROUP BY A.ClientID, A.YMD, A.OutYMD
 ORDER BY A.OutYMD, A.ClientID;
```

```sql
-- SQL_CHECK_EXISTING_MISS
DECLARE @v_intIssueType TINYINT = 15;
/* SELECT 2: TSettleMiss 기등록여부 확인 */
SELECT ID
  FROM SETTLE_POQ_DB.dbo.TSettleMiss
 WHERE ClientID = @p_clientId
   AND OutYMD = @p_outYmd
   AND OutState = 2
   AND IssueType = @v_intIssueType;
```
";

        /// <summary>POQSettleBatch1/S06 모양 - SELECT 앵커가 없고 DML 앵커만 있다.</summary>
        private const string DmlOnlyStep = @"### S06 단계

```sql
-- SQL_DELETE_CHUNK : /* DELETE 1: 정산 데이터 삭제 */ 원본 필터 + PLTID 청크 범위
DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @p_ymd;

SELECT MIN(A.PLTID), MAX(A.PLTID) FROM SETTLE_POQ_DB.dbo.TSettleMst A WHERE A.YMD = @p_ymd;
```
";

        /// <summary>
        /// POQSettleBatch1/S14 그대로(줄 그대로 오려냄) - 코퍼스 16 건 중 절반이 쓰는
        /// <c>-- SELECT n:</c> 표기. 위 <see cref="BlockSelectAnchorStep"/>·<see cref="DmlOnlyStep"/>
        /// 은 전부 <c>/* … */</c> 형이라, 실물 절반을 지고 있는 이 갈래에 단위 커버리지가
        /// 없었다(그 부재가 착수 전 분모를 8로 잘못 재게 한 원인).
        /// </summary>
        private const string DashSelectAnchorStep = @"### S14 단계

```sql
-- SQL_S14_FIND_TARGET_GROUPS
-- SELECT 1: 원본 커서 GetDataCrsr의 소스 SELECT - 회수 후 취소된 거래의 그룹 키를 식별
SELECT DISTINCT
    A.YMD, A.AYMD, A.INYMD, A.OUTYMD, A.CLIENTID, A.PGNAME, A.MALLID,
    A.SERVICENAME, A.PRODUCTNAME, A.USESTATE, A.OUTSTATE,
    A.CompanySalesType, A.ProcYMD, A.ExtraSettleFlag
  FROM SETTLE_POQ_DB.dbo.TSettleMst A
  INNER JOIN SETTLE_POQ_DB.dbo.TSettleMst B ON A.PLTID = B.PLTID
 WHERE B.YMD = @p_batchYmd
   AND B.OUTSTATE = 9
   AND B.USESTATE = 1
   AND A.OUTSTATE = 9
   AND A.USESTATE = 0
   AND A.OUTYMD IS NOT NULL;
```
";

        [Fact]
        public void ReadsSelectAnchorsWrittenWithTheBlockCommentFormFromRealCorpus()
        {
            Assert.Equal(new[] { 1, 2 }, StepSqlStatementReader.ReadSelectAnchors(BlockSelectAnchorStep));
        }

        [Fact]
        public void DoesNotInventSelectAnchorsWhereOnlyDmlAnchorsExist()
        {
            Assert.Empty(StepSqlStatementReader.ReadSelectAnchors(DmlOnlyStep));
        }

        [Fact]
        public void ReadsSelectAnchorsWrittenWithTheDashCommentForm()
        {
            Assert.Equal(new[] { 1 }, StepSqlStatementReader.ReadSelectAnchors(DashSelectAnchorStep));
        }

        [Fact]
        public void IgnoresSelectMentionedInsideAnotherAnchorsProse()
        {
            // 실물 모양(POQSettleBatch6/S13 이 `/* U1: … (DELETE 1) */` 로 적었다).
            // 종류 이름이 설명 안에 있으면 앵커가 아니다.
            const string step = "### S 단계\n\n```sql\n" +
                "/* U1: 후회수 취소 기존 집계 삭제 (SELECT 1) */\n" +
                "DELETE FROM SETTLE_POQ_DB.dbo.TSettleByOUT WHERE YMD = @p_ymd;\n```\n";

            Assert.Empty(StepSqlStatementReader.ReadSelectAnchors(step));
        }

        [Fact]
        public void IgnoresTrailingComments()
        {
            // 꼬리 주석은 앞 문장의 것이지 뒤 문장의 앵커가 아니다 - 리더의 규칙과 같다.
            const string step = "### S 단계\n\n```sql\n" +
                "DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @p_ymd; /* SELECT 1: 꼬리 */\n```\n";

            Assert.Empty(StepSqlStatementReader.ReadSelectAnchors(step));
        }

        [Fact]
        public void IgnoresNonSqlFences()
        {
            const string step = "### S 단계\n\n```csharp\n/* SELECT 1: 앵커가 아니다 */\n```\n";

            Assert.Empty(StepSqlStatementReader.ReadSelectAnchors(step));
        }

        [Fact]
        public void KeepsDuplicatesBecauseADuplicateIsItselfADefectSignal()
        {
            const string step = "### S 단계\n\n```sql\n" +
                "/* SELECT 1: 첫째 */\nSELECT 1 AS X;\n" +
                "/* SELECT 1: 둘째 - 같은 서수 */\nSELECT 2 AS Y;\n```\n";

            Assert.Equal(new[] { 1, 1 }, StepSqlStatementReader.ReadSelectAnchors(step));
        }
    }
}
