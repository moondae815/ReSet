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
        /// <summary>POQSettleBatch7/S15 그대로 - 커서 선언 안이라 문장이 되지 않는다.</summary>
        private const string CursorSourceStep = @"### S15 단계

```sql
-- SQL_CURSOR_SOURCE
/* SELECT 1: Cur_SettlePost 커서 소스 - 고객사/거래일/지급일 단위 집계 */
DECLARE Cur_SettlePost CURSOR LOCAL FAST_FORWARD FOR
SELECT CLIENTID, YMD FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @p_ymd;
```

```sql
/* SELECT 2: TSettleMiss 기등록여부 확인 */
SELECT @v_intCnt = COUNT(*) FROM SETTLE_POQ_DB.dbo.TSettleMiss WHERE ID = @v_intID;
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
        /// <c>-- SELECT n:</c> 표기. 위 <see cref="CursorSourceStep"/>·<see cref="DmlOnlyStep"/>
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
        public void ReadsSelectAnchorsFromCommentsEvenInsideCursorDeclarations()
        {
            Assert.Equal(new[] { 1, 2 }, StepSqlStatementReader.ReadSelectAnchors(CursorSourceStep));
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
