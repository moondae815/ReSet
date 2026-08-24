using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class StepSqlStatementReaderTests
{
    private static string Fence(string sql) => $"### S07 단계\n\n```sql\n{sql}\n```\n";

    [Fact]
    public void CountsStatementsByKindAndTable()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
            "UPDATE A SET A.CLVT = 2 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
            "INSERT INTO batch.BatchStepJournal (RunId) VALUES (1);"));

        Assert.Equal(2, statements.Count(s => s.Kind == "UPDATE" && s.TargetTable == "TSettleMst"));
        Assert.Single(statements, s => s.Kind == "INSERT" && s.TargetTable == "BatchStepJournal");
    }

    [Fact]
    public void ReadsAnchorFromLeadingComment()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "/* U13: 카드사 원가 반영 */\n" +
            "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y WHERE Y.YMD = @p;"));

        Assert.Equal(13, Assert.Single(statements).Anchor);
    }

    [Theory]
    [InlineData("-- 갱신 4")]
    [InlineData("-- UPDATE 4")]
    [InlineData("/* U4 */")]
    public void AcceptsThreeAnchorSpellings(string comment)
    {
        var statements = StepSqlStatementReader.Read(Fence(
            comment + "\nUPDATE A SET A.CLVT = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;"));

        Assert.Equal(4, Assert.Single(statements).Anchor);
    }

    [Fact]
    public void CollectsTopLevelPredicateAndJoinColumns_ButNotSubqueryColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = (SELECT TOP 1 X.Amt FROM dbo.TCost AS X WHERE X.Hidden = 1)\n" +
            "FROM dbo.TSettleMst AS Y INNER JOIN dbo.TCost AS C ON C.PLTID = Y.PLTID\n" +
            "WHERE Y.YMD = @p AND Y.UseState = 1;"));

        var statement = Assert.Single(statements);
        Assert.Contains("YMD", statement.PredicateColumns);
        Assert.Contains("UseState", statement.PredicateColumns);
        Assert.Contains("PLTID", statement.JoinColumns);
        Assert.DoesNotContain("Hidden", statement.PredicateColumns);   // 스칼라 하위질의 안쪽
    }

    [Fact]
    public void FlagsGroupingWhenGroupByOrHavingPresent()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = 0 FROM dbo.TSettleMst AS Y\n" +
            "WHERE Y.PLTID IN (SELECT PLTID FROM dbo.TTx GROUP BY PLTID HAVING SUM(TxAmt) = 0);"));

        Assert.True(Assert.Single(statements).HasGrouping);
    }

    [Fact]
    public void UnparsableFence_IsSilentlySkipped()
    {
        // 단계 문서의 펜스에는 T-SQL이 아닌 것도 온다. 재료가 없다는 사실은
        // 다른 검사가 들고, 이 읽기는 조용히 건너뛴다.
        var statements = StepSqlStatementReader.Read(Fence("이것은 SQL이 아니다 <<<>>>"));

        Assert.Empty(statements);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Task 16 - C2. 파싱 실패가 실재하는 문장을 "없다"로 만드는 것을 막으려면
    // 호출부가 "펜스를 못 읽었다"는 사실 자체를 알아야 한다. 기존 단일 인자
    // Read(markdown)은 이 신호를 전혀 내지 않는다 - 검사 A가 파싱 실패와
    // "정말 문장이 없음"을 구별할 수 없다.
    //
    // [Task 20] 이 신호는 이제 "펜스 전체 소실"이 아니라 "파싱에 실패해
    // 잃어버린 DML 문장 개수"를 센다 - 아래 GenuinelyUnparsableDmlStatement_
    // CountsAsOneLostStatement와 NonSqlProseFence_DoesNotCountAsLostStatement가
    // 그 차이를 보인다.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void GenuinelyUnparsableDmlStatement_CountsAsOneLostStatement()
    {
        // 펜스 전체가 파싱 불가능한 DML 문장 하나뿐이면(예전 실측 그대로),
        // 손실 신호는 여전히 1이어야 한다 - 실재하는 INSERT 시도가 있었는데
        // 못 읽었다는 사실은 여전히 필요하다.
        var statements = StepSqlStatementReader.Read(
            Fence("INSERT INTO dbo.T SELECT /* 주석뿐 */ FROM dbo.S;"), out var lostStatementCount);

        Assert.Empty(statements);
        Assert.Equal(1, lostStatementCount);
    }

    [Fact]
    public void ParsableFenceWithNoStatements_DoesNotCountAsUnparsed()
    {
        // 파싱은 성공했지만 DML 문장이 없는 것(예: SET만 있는 펜스)은 "못
        // 읽음"이 아니다 - 이 둘을 같은 신호로 합치면 정상 펜스도 검사 A를
        // 접게 만든다.
        var statements = StepSqlStatementReader.Read(
            Fence("SET NOCOUNT ON;"), out var lostStatementCount);

        Assert.Empty(statements);
        Assert.Equal(0, lostStatementCount);
    }

    [Fact]
    public void MixOfParsableAndUnparsableFences_KeepsRealStatementsAndCountsOnlyFailures()
    {
        // 실물 모양(S12.md:75-80): `INSERT ... SELECT /* 주석만 */ FROM ...`처럼
        // SELECT 목록이 통째로 주석으로 비면 파싱이 실패한다 - 그 펜스만
        // 버려지고 나머지 펜스의 실재하는 문장은 살아야 한다.
        var markdown =
            "### S12 단계\n\n" +
            "```sql\n" +
            "DELETE FROM dbo.TSettleByTX WHERE YMD = @pi_strYMD;\n" +
            "```\n\n" +
            "```sql\n" +
            "INSERT INTO dbo.TPartialCancelByTX\n" +
            "SELECT\n" +
            "    /* 동일한 집계 열 */\n" +
            "    /* PLTID를 포함 */\n" +
            "FROM dbo.TSettleMst\n" +
            "WHERE YMD = @pi_strYMD\n" +
            "GROUP BY YMD;\n" +
            "```\n";

        var statements = StepSqlStatementReader.Read(markdown, out var lostStatementCount);

        Assert.Single(statements, s => s.Kind == "DELETE" && s.TargetTable == "TSettleByTX");
        Assert.Equal(1, lostStatementCount);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Task 20 - 파싱 실패가 펜스 전체를 삼키지 않게 한다.
    //
    // [실측] `TSqlParser.Parse`는 구문 오류를 만나면 그 지점에서 완전히
    // 멈춘다 - 오류 뒤의 실재하는 문장은 fragment에 전혀 나타나지 않는다
    // (실물 확인: POQSettleProc3/S08의 sp_getapplock 오류 뒤 UPDATE 2개가
    // 배치 전체 실패로 통째로 사라짐). 그래서 "부분 결과의 문장 중 오류와
    // 겹치는 것만 뺀다"는 접근은 통하지 않는다 - 오류 뒤 문장은 애초에
    // 부분 결과에 없다. 대신 펜스를 최상위(괄호 깊이 0) 세미콜론으로 잘라
    // 조각마다 독립적으로 파싱한다 - 한 조각의 오류가 다른 조각에 번지지
    // 않는다.
    // ─────────────────────────────────────────────────────────────────────

    private static string FenceRaw(string sql) => $"### 단계\n\n```sql\n{sql}\n```\n";

    [Fact]
    public void FailingStatementInTheMiddleOfAFence_DoesNotDropStatementsAfterIt()
    {
        // 실물 모양(POQSettleProc3/S08:81-85): 함수 호출식을 named-parameter
        // 값으로 쓰는 EXEC sp_getapplock 관용구는 ScriptDom 전 버전에서
        // 구문 오류를 낸다. 그 뒤에 오는 진짜 DELETE·UPDATE는 살아야 한다.
        var statements = StepSqlStatementReader.Read(FenceRaw(
            "SET NOCOUNT ON;\n" +
            "EXEC @v_lockResult = sys.sp_getapplock @Resource = CONCAT('Job:S08:', @p), @LockMode = 'Exclusive';\n" +
            "DELETE FROM dbo.TSettleMst WHERE YMD = @p;\n" +
            "UPDATE dbo.TSettleMst SET CLCOMM = 1 WHERE YMD = @p;"),
            out var lostStatementCount);

        Assert.Single(statements, s => s.Kind == "DELETE" && s.TargetTable == "TSettleMst");
        Assert.Single(statements, s => s.Kind == "UPDATE" && s.TargetTable == "TSettleMst");
        // EXEC은 INSERT·UPDATE·DELETE가 아니므로 이 실패로 잃어버린 DML은 없다.
        Assert.Equal(0, lostStatementCount);
    }

    [Fact]
    public void GenuinelyBrokenDmlStatement_IsExcludedButSurroundingStatementsSurvive()
    {
        // 실물 모양(S12.md:74-84): SELECT 목록이 통째로 주석으로 비어 그
        // 문장 자체가 파싱 불가능하다(산출물 결함 자체) - 같은 펜스의 앞뒤
        // DELETE·INSERT는 살아야 하고, 못 읽은 INSERT 1개는 손실로 집계된다.
        var statements = StepSqlStatementReader.Read(FenceRaw(
            "DELETE FROM dbo.TSettleByTX WHERE YMD = @p;\n" +
            "INSERT INTO dbo.TPartialCancelByTX\n" +
            "SELECT\n" +
            "    /* 동일한 집계 열 */\n" +
            "FROM dbo.TSettleMst\n" +
            "WHERE YMD = @p\n" +
            "GROUP BY YMD;\n" +
            "INSERT INTO dbo.TSettleByOUT (YMD) VALUES (@p);"),
            out var lostStatementCount);

        Assert.Single(statements, s => s.Kind == "DELETE" && s.TargetTable == "TSettleByTX");
        Assert.Single(statements, s => s.Kind == "INSERT" && s.TargetTable == "TSettleByOUT");
        Assert.DoesNotContain(statements, s => s.TargetTable == "TPartialCancelByTX");
        Assert.Equal(1, lostStatementCount);
    }

    [Fact]
    public void FailedChunkWithNoDmlKeyword_DoesNotCountAsLost()
    {
        // BEGIN TRY·IF EXISTS 같은 제어문 조각은 세미콜론 분할 과정에서
        // 자연스럽게 단독으로는 파싱되지 않는 조각이 될 수 있다 - 그 조각에
        // INSERT·UPDATE·DELETE가 없으면 잃어버린 DML이 아니므로 세면 안 된다.
        var statements = StepSqlStatementReader.Read(FenceRaw(
            "BEGIN TRY\n" +
            "    BEGIN TRAN;\n" +
            "    UPDATE dbo.TSettleMst SET CLCOMM = 1 WHERE YMD = @p;\n" +
            "    COMMIT TRAN;\n" +
            "END TRY\n" +
            "BEGIN CATCH\n" +
            "    ROLLBACK TRAN;\n" +
            "END CATCH;"),
            out var lostStatementCount);

        Assert.Single(statements, s => s.Kind == "UPDATE" && s.TargetTable == "TSettleMst");
        Assert.Equal(0, lostStatementCount);
    }

    [Fact]
    public void DmlStatementImmediatelyAfterBareBegin_IsRecoveredNotLost()
    {
        // 실물 모양(POQSettleBatch1/S01.md): `IF @ErrorCode IS NOT NULL BEGIN
        // UPDATE ... WHERE ...; END`처럼 DML이 세미콜론 없는 `BEGIN` 바로
        // 다음에 오면, `BEGIN`을 분할 지점으로 안 두면 "IF ... BEGIN UPDATE
        // ...;" 조각 전체가 BEGIN 짝이 안 맞아 파싱에 실패해 실재하는 UPDATE가
        // 억울하게 손실로 잡힌다(코퍼스 실측 25개 표본 중 6개가 이 모양).
        var statements = StepSqlStatementReader.Read(FenceRaw(
            "IF @ErrorCode IS NOT NULL\n" +
            "BEGIN\n" +
            "    UPDATE dbo.TSettleMst SET CLCOMM = 1 WHERE YMD = @p;\n" +
            "END;"),
            out var lostStatementCount);

        Assert.Single(statements, s => s.Kind == "UPDATE" && s.TargetTable == "TSettleMst");
        Assert.Equal(0, lostStatementCount);
    }

    [Fact]
    public void BareBeginTransaction_IsNotSplitAwayFromItsOwnStatement()
    {
        // `BEGIN TRAN`·`BEGIN TRANSACTION`은 블록 여는 `BEGIN`이 아니라 그
        // 자체로 완결된 문장이다 - 이걸 분할 지점으로 다뤄도 결과가 달라지지는
        // 않지만(그 문장에는 DML 키워드가 없다), 있는 그대로 한 조각으로 남아야
        // 불필요한 조각 증식을 피한다.
        var statements = StepSqlStatementReader.Read(FenceRaw(
            "BEGIN TRANSACTION;\n" +
            "UPDATE dbo.TSettleMst SET CLCOMM = 1 WHERE YMD = @p;\n" +
            "COMMIT TRANSACTION;"),
            out var lostStatementCount);

        Assert.Single(statements, s => s.Kind == "UPDATE" && s.TargetTable == "TSettleMst");
        Assert.Equal(0, lostStatementCount);
    }

    [Fact]
    public void NonSqlProseFence_DoesNotCountAsLostStatement()
    {
        // 순수 산문·의사코드에는 애초에 DML이 없으니 잃어버릴 것도 없다 -
        // 이전 구현은 이런 펜스도 "1개 손실"로 셌지만, 이는 근거 없는 신호였다.
        var statements = StepSqlStatementReader.Read(
            FenceRaw("이것은 SQL이 아니다 <<<>>>"), out var lostStatementCount);

        Assert.Empty(statements);
        Assert.Equal(0, lostStatementCount);
    }

    [Fact]
    public void TrailingCommentOnPreviousStatement_IsNotMisattributedToNextStatement()
    {
        // "-- U4 ..."는 A 문장 끝에 붙은 꼬리 주석이지 B 문장의 선행 주석이 아니다.
        // 리뷰 1라운드 실측: 고치기 전에는 이 주석이 B의 앵커로 잘못 붙었다.
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p; -- U4 참고: A에 대한 설명\n" +
            "UPDATE B SET B.CLVT = 2 FROM dbo.TSettleMst AS B WHERE B.YMD = @p;"));

        Assert.Equal(2, statements.Count);
        Assert.Null(statements[0].Anchor);
        Assert.Null(statements[1].Anchor);
    }

    [Fact]
    public void ResolvesTargetAlias_WhenFromClauseHasMultipleAliasedTables()
    {
        // FROM 절에 별칭이 둘 있을 때 갱신 대상 별칭(A)과 같은 것만 골라야 한다 -
        // "먼저 찾은 별칭"으로 어림하면 B(TCost)를 잘못 돌려준다.
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.CLCOMM = 1 FROM dbo.TCost AS B INNER JOIN dbo.TSettleMst AS A " +
            "ON A.PLTID = B.PLTID WHERE A.YMD = @p;"));

        Assert.Equal("TSettleMst", Assert.Single(statements).TargetTable);
    }

    [Fact]
    public void ResolvesTargetTable_WhenUpdateHasNoFromClauseOrAlias()
    {
        // FROM 절 자체가 없으면 별칭 사전이 비어 있고, 대상 이름은 그 자체가
        // 물리 테이블명이다 - 한 부(部) 이름을 별칭으로 오인해 빈 문자열로
        // 떨어뜨리면 안 된다.
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE TSettleMst SET CLCOMM = 1 WHERE YMD = @p;"));

        Assert.Equal("TSettleMst", Assert.Single(statements).TargetTable);
    }

    [Fact]
    public void DoesNotMisreadAnchorFromCompoundKoreanWord()
    {
        // "재갱신4"는 "갱신 4"의 오검출 대상이다 - "갱신" 앞에 다른 단어 성분이
        // 붙어 있으면 그 자체로 앵커 표기가 아니다.
        var statements = StepSqlStatementReader.Read(Fence(
            "-- 재갱신4\nUPDATE A SET A.CLVT = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;"));

        Assert.Null(Assert.Single(statements).Anchor);
    }
}
