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

        /// <summary>접어 넣은 표기. <c>POQSettleBatch8/S12</c> 89~96 행을 그대로 오려
        /// 왔다(끝 두 줄만 문장을 닫으려고 줄였다). 모델이 앵커를 이름 있는 SQL 블록의
        /// 이름표 줄에 괄호로 접어 넣은 실물이다 - 여섯 줄이 같은 모양이었다.
        ///
        /// [이것이 0 으로 읽히는 것은 의도다] 계약이 「앵커는 그 주석의 맨 앞에 온다」를
        /// 못박는다(<see cref="StatementAnchorClauseTests"/>). 리더를 이 모양까지 넓히면
        /// 산문(`SELECT1/DELETE1/INSERT1` 같은 열거)도 앵커로 읽혀 오탐이 된다. 그래서
        /// 여기서 0 이 나오는 것은 리더의 결함이 아니라 계약 위반의 탐지다.
        /// 실측 근거: docs/audit-reports/2026-09-12-A2-신규Job-계약판정-착수기록.md</summary>
        private const string FoldedSelectAnchorStep = @"### S12 단계

```sql
-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';

-- SQL_FETCH_SETTLE_POST_SOURCE (SELECT 1: 후취정산 대상 커서 원천 조회)
SELECT A.ClientID AS ClientID,
       A.YMD       AS YMD,
  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A;
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
        public void DoesNotReadAnAnchorFoldedIntoTheBlockNameLine()
        {
            // 이 0 은 「못 읽는다」가 아니라 「계약 위반을 잡는다」다. 위 주석의 근거를
            // 읽어라 - 넓히면 산문 열거까지 앵커가 된다.
            var anchors = StepSqlStatementReader.ReadSelectAnchors(FoldedSelectAnchorStep);

            Assert.Empty(anchors);
        }

        [Fact]
        public void CountsTheFoldedAnchorSoAProbeCanReportTheContractViolation()
        {
            // 도달률 0 만으로는 「모델이 안 달았다」와 「달았는데 자리가 틀렸다」가
            // 구분되지 않는다. 프로브가 그 둘을 갈라 보고하려면 접어 넣은 모양을
            // 세는 자가 따로 있어야 한다 - 그래야 처방이 갈린다(Few-Shot 인가 자리인가).
            Assert.Equal(1, StepSqlStatementReader.CountFoldedSelectAnchors(FoldedSelectAnchorStep));
        }

        [Fact]
        public void DoesNotCountProseEnumerationsAsFoldedAnchors()
        {
            // 실물 오탐 후보. POQSettleBatch8/S09:91 이 이 모양이다 - 이름표도
            // 괄호도 없고 콜론도 없다. 이것까지 세면 프로브가 없는 위반을 보고한다.
            const string prose = @"### S09 단계

```sql
-- 명세서의 기계 확정 표에는 SELECT1/DELETE1/INSERT1/UPDATE1~5만 등재되어 있다
SELECT 1;
```
";

            Assert.Equal(0, StepSqlStatementReader.CountFoldedSelectAnchors(prose));
        }

        [Fact]
        public void ReadsTheSameAnchorOnceItIsMovedToItsOwnLine()
        {
            // 양성 대조군. 위 시험의 0 이 「이 리더가 이 본문에서 아무것도 못 읽는다」
            // 때문이 아님을 보인다 - 같은 본문에서 앵커를 자기 줄로 내리면 읽힌다.
            var fixedUp = FoldedSelectAnchorStep.Replace(
                "-- SQL_FETCH_SETTLE_POST_SOURCE (SELECT 1: 후취정산 대상 커서 원천 조회)",
                "-- SQL_FETCH_SETTLE_POST_SOURCE\n/* SELECT 1: 후취정산 대상 커서 원천 조회 */");

            Assert.Equal(new[] { 1 }, StepSqlStatementReader.ReadSelectAnchors(fixedUp));
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
