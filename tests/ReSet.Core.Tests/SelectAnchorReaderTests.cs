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
    /// 여기서는 <b>주석이 `SELECT n:` 으로 시작할 때만</b> 앵커로 본다 - 계약이 정한
    /// 형식 그대로다.
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
